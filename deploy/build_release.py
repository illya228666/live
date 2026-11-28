"""Build changed package inputs, publish a cumulative manifest, and smoke-test it."""
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tarfile
import tempfile
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from release_lib import MANIFEST, asset, fetch_manifest, latest_tag, run, sha256, write_json

ROOT = Path(__file__).resolve().parents[1]
os.chdir(ROOT)
CATALOG = Path("deploy/modules.json")
SDK_INPUTS = ("global.json", "Directory.Build.props", "Directory.Packages.props", "NuGet.config")
FOUNDATION_INPUTS = ("tools/package.ps1", "configs/default.json", "assets/foundation", "assets/wildlife",
                     "src/ArtificialLife.Modules.Vision", "src/ArtificialLife.Modules.Temperature",
                     "src/ArtificialLife.Modules.Hunger", "src/ArtificialLife.Modules.Food",
                     "src/ArtificialLife.Modules.Homeostasis", "src/ArtificialLife.Modules.Wildlife")


def fingerprint(*paths):
    contents = subprocess.check_output(["git", "ls-tree", "-r", "-z", "HEAD", "--",
                                         *SDK_INPUTS, *paths])
    return hashlib.sha256(contents).hexdigest()


def zip_package(package, output):
    with ZipFile(output, "w", ZIP_DEFLATED) as archive:
        for file in sorted(package.rglob("*")):
            if file.is_file():
                archive.write(file, file.relative_to(package).as_posix())


def metadata(tag, path, **fields):
    return dict(fields, tag=tag, asset=path.name, sha256=sha256(path))


def smoke(cli, manifest, output, scratch, previous=None):
    """Use the same package importer as the server, then restart the real supervisor."""
    catalog = scratch / "smoke-catalog"
    restore = scratch / "world"
    archived = scratch / "download"
    for package in manifest["modules"]:
        path = output / package["asset"]
        if package["tag"] != manifest["tag"]:
            path = asset(package["tag"], package["asset"], archived, package["sha256"])
        run(cli, "package", "publish", "--sources", path, "--root", catalog, stdout=subprocess.DEVNULL)
    config = json.loads(Path("configs/world-only.json").read_text())
    config["packageRoot"] = str(catalog)
    config["packages"] = [dict(id=p["id"], version=p["version"]) for p in manifest["modules"]]
    write_json(scratch / "world.json", config)
    args = (cli, "world", "run", "--directory", restore, "--config", scratch / "world.json",
            "--mode", "world-only", "--scene-directory", scratch / "scene")
    run(*args, "--speed", "100x", "--steps", "260", "--observation-copy", scratch / "public.json")
    before = json.loads((restore / "observation.json").read_text())
    public = json.loads((scratch / "public.json").read_text())
    if (before["Frame"]["Agent"] is not None or public["mode"] != "WorldOnly"
            or before["Frame"]["Age"] != 260):
        raise RuntimeError("World Only did not start with the requested packages")
    first = json.loads((scratch / "scene/scene.json").read_text())
    for module, visual in (("wildlife", "boar"), ("flora", "plant"), ("pollination", "moth")):
        if not any(package["id"] == module for package in manifest["modules"]):
            continue
        matches = [(entity, appearance) for entity in first["entities"] for appearance in entity["appearance"]
                   if appearance["key"].split("/")[::2] == [module, visual]]
        if not matches:
            raise RuntimeError(f"The scene is missing the declared {module} visual")
        for entity, appearance in matches:
            sprite_file = scratch / "scene/sprites" / appearance["sprite"].rsplit("/", 1)[1]
            if sprite_file.stem != sha256(sprite_file):
                raise RuntimeError(f"The scene must publish the verified {module} sprite")
            if module == "flora" and any(style["key"].startswith("food/") for style in entity["appearance"]):
                raise RuntimeError("The plant body must replace the apple visual on the same entity")
    run(*args, "--speed", "Max", "--steps", "24")
    after = json.loads((restore / "observation.json").read_text())
    if (before["SessionId"] != after["SessionId"] or after["Frame"]["Age"] != 284
            or before["Frame"]["Revision"] != after["Frame"]["Revision"]):
        raise RuntimeError("World Only did not resume the same checkpoint")
    restored = json.loads((scratch / "scene/scene.json").read_text())
    if (restored["sessionId"] != first["sessionId"] or restored["epoch"] == first["epoch"]
            or restored["age"] < 260):
        raise RuntimeError("The public frame did not identify the restored world generation")
    if not previous:
        return
    changes = [package for package in manifest["modules"] if package["tag"] == manifest["tag"]]
    if not changes:
        return
    old = scratch / "old-world"
    config["packages"] = [dict(id=p["id"], version=p["version"]) for p in previous["modules"]]
    for package in previous["modules"]:
        path = archived / package["asset"]
        if not path.exists():
            path = asset(package["tag"], package["asset"], archived, package["sha256"])
        run(cli, "package", "publish", "--sources", path, "--root", catalog, stdout=subprocess.DEVNULL)
    write_json(scratch / "old.json", config)
    run(cli, "world", "run", "--directory", old, "--config", scratch / "old.json", "--steps", "24")
    recovery = json.loads((old / "recovery.json").read_text())
    validation = scratch / "update"
    validation.mkdir()
    write_json(validation / "sources.json", [str(output / package["asset"]) for package in changes])
    try:
        run(cli, "world", "verify", "--directory", validation, "--checkpoint", recovery["Current"],
            "--sources-file", validation / "sources.json", "--output", validation)
    except subprocess.CalledProcessError:
        print((validation / "error.txt").read_text())
        raise


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sequence", type=int, required=True)
    parser.add_argument("--smoke-only", action="store_true")
    options = parser.parse_args()
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    sequence = options.sequence
    tag = f"live-{sequence}"
    version = f"r{sequence}"
    with tempfile.TemporaryDirectory(prefix="live-release-") as tmp:
        scratch = Path(tmp)
        previous = None
        if not options.smoke_only and (old_tag := latest_tag()):
            previous = fetch_manifest(old_tag, scratch, attest=False)
            if previous["sequence"] >= sequence:
                print(f"{old_tag} already supersedes this run")
                return
            run("git", "merge-base", "--is-ancestor", previous["commit"], commit)
        old = {p["id"]: p for p in previous["modules"]} if previous else {}
        config = json.loads(CATALOG.read_text())
        foundation = config["foundation"]
        digest = fingerprint(*FOUNDATION_INPUTS)
        new_base = any(old.get(name, {}).get("input") != digest for name in foundation)
        modules = []
        catalog = scratch / "packages"
        host = scratch / "host"
        host_inputs = fingerprint("src/ArtificialLife.Cli", "src/ArtificialLife.Hosting",
                                  "src/ArtificialLife.Application", "src/ArtificialLife.Core",
                                  "src/ArtificialLife.Observation.Contracts", "src/ArtificialLife.Packages",
                                  "src/ArtificialLife.Brain", "deploy/web")
        # Publishing the apphost with a pinned runtime also routes child processes back to this build.
        run("dotnet", "publish", "src/ArtificialLife.Cli", "-c", "Release", "-r", "linux-x64",
            "--self-contained", "true", "-o", host)
        cli = host / "ArtificialLife.Cli"
        shutil.copytree(ROOT / "deploy/web", host / "web")
        if new_base:
            run("pwsh", "-NoProfile", "-File", "tools/package.ps1", "-LogicOnly",
                "-Version", version, "-OutputRoot", catalog)
        for name in foundation:
            if not new_base:
                modules.append(old[name])
                continue
            path = output / f"{name}-{version}.zip"
            zip_package(catalog / name / version, path)
            modules.append(metadata(tag, path, id=name, version=version, input=digest))
        for entry in config["modules"]:
            name, description = entry["id"], Path(entry["description"])
            description_data = json.loads(description.read_text())
            definition = next(p for p in description_data["packages"] if p["manifest"]["id"] == name)
            files = definition.get("files", {})
            sprites = [(description.parent / files[visual["sprite"]]).resolve().relative_to(ROOT).as_posix()
                       for visual in definition["manifest"].get("visuals", [])
                       if visual.get("sprite") in files and Path(visual["sprite"]).suffix.lower() in (".png", ".webp")]
            digest = fingerprint(description.parent, "tools/package-module.ps1", *sprites)
            if old.get(name, {}).get("input") == digest:
                modules.append(old[name])
                continue
            run("pwsh", "-NoProfile", "-File", "tools/package-module.ps1", "-Description",
                description, "-Id", name, "-Version", version, "-PortableVisuals", "-OutputRoot",
                catalog, "-Cli", cli)
            path = output / f"{name}-{version}.zip"
            zip_package(catalog / name / version, path)
            modules.append(metadata(tag, path, id=name, version=version, input=digest))
        ids = [p["id"] for p in modules]
        if len(ids) != len(set(ids)) or set(old).difference(ids):
            raise ValueError("Duplicate module or removal of a deployed module")
        host_release = previous["host"] if previous else None
        new_host = not host_release or host_release["input"] != host_inputs
        if not new_host and modules == previous["modules"]:
            print("No deployment inputs changed")
            return
        if new_host and not options.smoke_only:
            path = output / "live-host-linux-x64.tar.gz"
            with tarfile.open(path, "w:gz", compresslevel=3) as archive:
                archive.add(host, arcname="host")
            host_release = metadata(tag, path, input=host_inputs)
        manifest = dict(schemaVersion=1, sequence=sequence, tag=tag, commit=commit, host=host_release,
                        modules=modules)
        smoke(cli, manifest, output, scratch, previous)
        if not options.smoke_only:
            write_json(output / MANIFEST, manifest)


if __name__ == "__main__":
    main()
