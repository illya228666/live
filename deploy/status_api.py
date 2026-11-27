"""Read-only HTTP origin and authenticated release wake-up for Cloudflare Tunnel."""
import hashlib
import hmac
import json
import os
import re
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from release_lib import REPOSITORY, TAG

OBSERVATION = Path(os.getenv("LIVE_OBSERVATION", "/var/lib/live-observation/world.json"))
WAKE = Path(os.getenv("LIVE_WAKE", "/run/live-inbox/reconcile"))
ALLOWED = {"127.0.0.1", os.getenv("LIVE_TUNNEL_IP", "192.168.178.71")}
MAX_BODY = 128 * 1024


def world_status():
    try:
        snapshot = json.loads(OBSERVATION.read_text())
        observed = datetime.fromisoformat(snapshot["observedAt"])
        if (datetime.now(timezone.utc) - observed).total_seconds() > 15:
            return 503, {"schemaVersion": 1, "status": "stale", "observedAt": snapshot["observedAt"]}
        return (200 if snapshot["status"] == "running" else 503), snapshot
    except (OSError, ValueError, KeyError, TypeError):
        return 503, {"schemaVersion": 1, "status": "unavailable", "observedAt": None}


def github_event(kind, signature, data, secret, repository_id):
    expected = "sha256=" + hmac.new(secret, data, hashlib.sha256).hexdigest()
    if not isinstance(signature, str) or not re.fullmatch(r"sha256=[0-9a-f]{64}", signature):
        return 403
    if not hmac.compare_digest(signature, expected):
        return 403
    payload = json.loads(data)
    repository = payload["repository"]
    if repository["id"] != repository_id or repository["full_name"] != REPOSITORY:
        return 403
    if kind != "release" or payload.get("action") != "published":
        return 204
    release = payload["release"]
    if release["draft"] or release["prerelease"] or not TAG.fullmatch(release["tag_name"]):
        return 204
    return 202


class Origin(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 32
    slots = threading.BoundedSemaphore(12)

    def verify_request(self, request, address):
        return address[0] in ALLOWED

    def process_request(self, request, address):
        if not self.slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        request.settimeout(5)
        try:
            super().process_request(request, address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, address):
        try:
            super().process_request_thread(request, address)
        finally:
            self.slots.release()


class RequestHandler(BaseHTTPRequestHandler):
    def reply(self, status, payload=None, *, head=False):
        body = json.dumps(payload, allow_nan=False).encode() if payload is not None else b""
        self.send_response(status)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if not head and body:
            self.wfile.write(body)

    def do_GET(self):
        if self.path not in ("/", "/api/world"):
            self.reply(404)
            return
        self.reply(*world_status(), head=self.command == "HEAD")

    do_HEAD = do_GET

    def do_POST(self):
        if self.path != "/hooks/github":
            self.reply(404)
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
            if not 0 < size <= MAX_BODY:
                self.reply(413)
                return
            data = self.rfile.read(size)
            status = github_event(self.headers.get("X-GitHub-Event"),
                                  self.headers.get("X-Hub-Signature-256"), data,
                                  self.server.secret, self.server.repository_id)
            if status == 202:
                WAKE.touch(mode=0o660)
            self.reply(status)
        except (ValueError, TypeError, KeyError):
            self.reply(400)
        except OSError:
            self.reply(503)


if __name__ == "__main__":
    secret = Path(os.environ["CREDENTIALS_DIRECTORY"], "webhook").read_bytes().strip()
    if len(secret) < 32:
        raise ValueError("GitHub webhook secret is missing")
    with Origin((os.getenv("LIVE_BIND", "0.0.0.0"), int(os.getenv("LIVE_PORT", "8080"))),
                RequestHandler) as server:
        server.secret = secret
        server.repository_id = int(os.environ["LIVE_REPOSITORY_ID"])
        server.serve_forever()
