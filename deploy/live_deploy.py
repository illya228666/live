"""Install the first pinned Host and reconcile published module releases."""
import argparse
import fcntl
import json
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import time
from pathlib import Path, PurePosixPath

from release_lib import TAG, asset, fetch_manifest, latest_tag, run, write_json

WORLD = Path(os.getenv("LIVE_WORLD_DIR", "/var/lib/live/world"))
STATE = WORLD.parent
HOST = Path(os.getenv("LIVE_HOST_DIR", "/opt/live/host"))
WAKE = Path(os.getenv("LIVE_WAKE", "/run/live-inbox/reconcile"))


def read_json(path):
    return json.loads(path.read_text())


def extract_host(archive, directory):
    with tarfile.open(archive) as bundle:
        for entry in bundle:
            name = PurePosixPath(entry.name)
            if not name.parts or name.parts[0] != "host" or ".." in name.parts:
                raise ValueError("Unexpected path in Host archive")
            path = directory.joinpath(*name.parts)
            if entry.isdir():
                path.mkdir(mode=0o755, parents=True, exist_ok=True)
            elif entry.isfile():
                path.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
                with bundle.extractfile(entry) as source, path.open("xb") as target:
                    shutil.copyfileobj(source, target)
                path.chmod(0o755 if entry.mode & 0o111 else 0o644)
            else:
                raise ValueError("Host archive must contain regular files and directories")


def stage_host(manifest, scratch):
    release = manifest["host"]
    name = release["tag"]
    if not TAG.fullmatch(name):
        raise ValueError("Invalid Host release")
    binary = HOST / name / "ArtificialLife.Cli"
    if not binary.is_file():
        archive = asset(name, release["asset"], scratch, release["sha256"])
        with tempfile.TemporaryDirectory(dir=HOST, prefix=".host-") as directory:
            extract_host(archive, Path(directory))
            (Path(directory) / "host").rename(HOST / name)
    return binary


def bootstrap(manifest, scratch):
    if (WORLD / "recovery.json").exists():
        raise RuntimeError("World exists. Host replacement requires a checkpoint and planned switch")
    binary = stage_host(manifest, scratch)
    name = binary.parent.name
    catalog = STATE / "bootstrap-packages"
    run("chown", "live:live", catalog.parent)
    archives = []
    for package in manifest["modules"]:
        archives.append(asset(package["tag"], package["asset"], scratch, package["sha256"]))
    run(binary, "package", "publish", "--sources", ";".join(map(str, archives)), "--root", catalog)
    config = read_json(Path(__file__).with_name("world-only.json"))
    config["packageRoot"] = str(catalog)
    config["packages"] = [dict(id=p["id"], version=p["version"]) for p in manifest["modules"]]
    write_json(Path("/etc/live/world.json"), config)
    temporary_link = HOST / ".next"
    temporary_link.unlink(missing_ok=True)
    temporary_link.symlink_to(name)
    temporary_link.replace(HOST / "current")
    print(f"Installed Host {name}; initial module release live-{manifest['sequence']}", flush=True)


def fresh():
    path = WORLD / "observation.json"
    if time.time() - path.stat().st_mtime > 15:
        raise RuntimeError("World observation is stale")
    result = read_json(path)
    if result["Fault"] is not None:
        raise RuntimeError("World reports a fault")
    return result


def verify_application(manifest, result_file, start, command):
    deadline = time.monotonic() + 125
    while time.monotonic() < deadline:
        time.sleep(1)
        if not result_file.exists():
            continue
        reply = read_json(result_file)
        if not reply["Accepted"]:
            return {"status": "rejected", "message": reply["Message"], "command": command}
        if read_json(WORLD / "recovery.json")["Pending"] is not None:
            continue
        current = fresh()
        installed = {p["Id"]: p["Version"] for p in current["Packages"]}
        if all(installed.get(p["id"]) == p["version"] for p in manifest["modules"]):
            if (current["SessionId"] == start["SessionId"]
                    and current["Frame"]["Age"] > max(start["Frame"]["Age"], reply["Age"])):
                return {"status": "applied", "age": current["Frame"]["Age"],
                        "revision": current["Frame"]["Revision"], "command": command}
        elif current["Frame"]["Age"] > reply["Age"] and current["Frame"]["Revision"] < reply["Revision"]:
            return {"status": "rejected", "message": "Worker restored the previous deployment",
                    "command": command}
    raise RuntimeError(f"World update still unconfirmed ({command}); retry will check the same ID")


def reconcile(manifest, scratch):
    if read_json(WORLD / "recovery.json")["Pending"] is not None:
        raise RuntimeError("A world update is in probation")
    start = fresh()
    installed = {p["Id"]: p["Version"] for p in start["Packages"]}
    changes = [p for p in manifest["modules"] if installed.get(p["id"]) != p["version"]]
    result_path = STATE / "reconcile.json"
    sequence = manifest["sequence"]
    if not changes:
        result = {"status": "current", "release": sequence, "revision": start["Frame"]["Revision"]}
        write_json(result_path, result)
        return
    for package in changes:
        old = installed.get(package["id"])
        if old and (not re.fullmatch(r"r[1-9][0-9]*", old)
                    or int(old[1:]) >= int(package["version"][1:])):
            raise RuntimeError(f"Refusing to downgrade {package['id']} from {old}")
    command = f"github-{sequence}"
    ready = WORLD / "commands" / f"{command}.ready.json"
    result_file = WORLD / "commands" / f"{command}.result.json"
    if not result_file.exists() and not ready.exists():
        if read_json(WORLD / "recovery.json")["Pending"] is not None:
            raise RuntimeError("Another world update is in probation")
        # A queued command may outlive this process. Keep its archives until it completes.
        directory = STATE / "downloads" / command
        files = [asset(p["tag"], p["asset"], directory, p["sha256"]) for p in changes]
        run(HOST / "current" / "ArtificialLife.Cli", "world", "update", "--directory", WORLD,
            "--id", command, "--sources", ";".join(map(str, files)))
    result = verify_application(manifest, result_file, start, command)
    write_json(result_path, dict(result, release=sequence))
    print(json.dumps(result), flush=True)
    if result["status"] != "applied":
        raise RuntimeError("World rejected the published deployment")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("bootstrap", "stage-host", "reconcile"))
    parser.add_argument("--tag")
    args = parser.parse_args()
    if args.tag and args.operation != "bootstrap":
        if args.operation != "stage-host":
            parser.error("--tag is only for installing a Host")
    with (STATE / ".reconcile.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if args.operation == "reconcile":
            WAKE.unlink(missing_ok=True)
        tag = args.tag or latest_tag()
        if tag is None:
            print("No Live releases published")
            return
        previous = STATE / "reconcile.json"
        if args.operation == "reconcile" and previous.exists():
            recorded = read_json(previous)
            if recorded["release"] > int(TAG.fullmatch(tag)[1]):
                print(f"Release {tag} predates the server ledger")
                return
        with tempfile.TemporaryDirectory(prefix="live-download-") as directory:
            scratch = Path(directory)
            manifest = fetch_manifest(tag, scratch)
            if args.operation == "bootstrap":
                bootstrap(manifest, scratch)
            elif args.operation == "stage-host":
                print(stage_host(manifest, scratch), flush=True)
            else:
                reconcile(manifest, scratch)


if __name__ == "__main__":
    main()
