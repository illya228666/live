"""Release metadata shared by the publisher and the server."""
import hashlib
import json
import os
import re
import shutil
import subprocess
from pathlib import Path
from urllib.request import Request, urlopen

REPOSITORY = "illya228666/live"
WORKFLOW = f"{REPOSITORY}/.github/workflows/live-release.yml"
TAG = re.compile(r"live-([1-9][0-9]*)")
PACKAGE_ID = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9_.-]{0,95}")
MANIFEST = "live-manifest.json"
BUNDLE = "live-manifest.sigstore.json"


def run(*args, **kwargs):
    return subprocess.run([str(arg) for arg in args], check=True, timeout=240, **kwargs)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def request(url, *, authenticated=False):
    headers = {"User-Agent": "MyPocketWorld-Release/1"}
    if authenticated and (token := os.getenv("GH_TOKEN")):
        headers["Authorization"] = f"Bearer {token}"
    return urlopen(Request(url, headers=headers), timeout=30)


def latest_tag():
    with request(f"https://api.github.com/repos/{REPOSITORY}/releases?per_page=100",
                 authenticated=True) as response:
        releases = json.load(response)
    tags = [item["tag_name"] for item in releases if not item["draft"] and not item["prerelease"]
            and TAG.fullmatch(item["tag_name"])]
    return max(tags, key=lambda tag: int(TAG.fullmatch(tag)[1]), default=None)


def download(tag, name, destination):
    if not TAG.fullmatch(tag) or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}", name):
        raise ValueError("Invalid release asset address")
    url = f"https://github.com/{REPOSITORY}/releases/download/{tag}/{name}"
    with request(url) as response, destination.open("wb") as target:
        shutil.copyfileobj(response, target)


def asset(tag, name, directory, digest):
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / name
    download(tag, name, path)
    if sha256(path) != digest:
        raise ValueError(f"Checksum mismatch for {name}")
    return path


def fetch_manifest(tag, directory, *, attest=True):
    ref = TAG.fullmatch(tag)
    path = directory / MANIFEST
    download(tag, MANIFEST, path)
    if attest:
        bundle = directory / BUNDLE
        download(tag, BUNDLE, bundle)
        run("gh", "attestation", "verify", path, "--bundle", bundle, "--repo", REPOSITORY,
            "--signer-workflow", WORKFLOW, "--source-ref", "refs/heads/master",
            "--deny-self-hosted-runners")
    manifest = json.loads(path.read_text())
    if (manifest["schemaVersion"] != 1 or manifest["sequence"] != int(ref[1])
            or not re.fullmatch(r"[a-f0-9]{40}", manifest["commit"])):
        raise ValueError("Invalid release manifest")
    ids = set()
    for package in manifest["modules"]:
        if (not PACKAGE_ID.fullmatch(package["id"]) or package["id"] in ids
                or not re.fullmatch(r"r[1-9][0-9]*", package["version"])):
            raise ValueError("Invalid module identity in release manifest")
        ids.add(package["id"])
    return manifest


def write_json(path, value):
    temporary = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)
