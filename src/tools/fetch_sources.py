"""Fetch pinned upstream trees and verify every Git blob before publishing them."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tarfile
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
SOURCES = {
    "PKHeX-Avalonia": ("realgarit/PKHeX-Avalonia", "v1.101.2"),
    "mgba": ("mgba-emu/mgba", "0.10.5"),
}


def request_json(url):
    with urllib.request.urlopen(url, timeout=60) as response:
        return json.load(response)


def fetch(name, archive_override=None):
    repository, tag = SOURCES[name]
    api = f"https://api.github.com/repos/{repository}"
    commit = request_json(f"{api}/commits/{tag}")["sha"]
    tree = request_json(f"{api}/git/trees/{commit}?recursive=1")
    if tree.get("truncated"):
        raise RuntimeError("Upstream tree response was truncated")
    downloads = ROOT / ".tools/downloads"
    downloads.mkdir(parents=True, exist_ok=True)
    archive = archive_override or downloads / f"{name}-{commit}.tar.gz"
    if not archive.exists():
        temporary = archive.with_suffix(".download")
        try:
            with urllib.request.urlopen(f"{api}/tarball/{commit}", timeout=60) as response:
                with temporary.open("wb") as output:
                    shutil.copyfileobj(response, output)
            temporary.replace(archive)
        finally:
            temporary.unlink(missing_ok=True)

    upstream = ROOT / "upstream"
    upstream.mkdir(exist_ok=True)
    stage = upstream / f".fetch-{name}-{uuid.uuid4().hex[:8]}"
    stage.mkdir()
    try:
        with tarfile.open(archive) as source:
            for member in source:
                parts = Path(member.name).parts[1:]
                if not parts:
                    continue
                member.name = str(Path(*parts))
                source.extract(member, stage, filter="data")
        blobs = [entry for entry in tree["tree"] if entry["type"] == "blob"]
        for entry in blobs:
            path = stage / entry["path"]
            data = path.readlink().as_posix().encode() if path.is_symlink() else path.read_bytes()
            header = b"blob " + str(len(data)).encode() + b"\0"
            if hashlib.sha1(header + data).hexdigest() != entry["sha"]:
                raise RuntimeError(f"Source hash mismatch at {entry['path']}")
        target = upstream / name
        if target.exists():
            previous = upstream / f".previous-{name}-{uuid.uuid4().hex[:8]}"
            target.rename(previous)
            try:
                stage.rename(target)
            except BaseException:
                previous.rename(target)
                raise
            shutil.rmtree(previous)
        else:
            stage.rename(target)
        manifest = {
            "repository": repository, "tag": tag, "commit": commit,
            "tree": tree["sha"], "files": len(blobs),
            "archive_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        }
        (upstream / f"{name}-source.json").write_text(json.dumps(manifest, indent=2) + "\n")
        print(f"Verified {name} {commit}, {len(blobs)} files", flush=True)
    finally:
        if stage.exists():
            shutil.rmtree(stage)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", choices=SOURCES)
    parser.add_argument("--archive", type=Path)
    args = parser.parse_args()
    fetch(args.source, args.archive)
