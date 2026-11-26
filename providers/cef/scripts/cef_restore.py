"""Explicit pinned CEF archive restoration; never runs in a default build."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import tarfile
import tempfile
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
MAX_EXTRACTED_BYTES = 8 * 1024**3
MAX_MEMBERS = 100_000


def hashes(path):
    sha1 = hashlib.sha1()
    sha256 = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha1.update(block)
            sha256.update(block)
    return sha1.hexdigest(), sha256.hexdigest()


def validate_members(members, expected_root):
    total = 0
    seen = set()
    for index, member in enumerate(members):
        if index >= MAX_MEMBERS:
            raise ValueError("CEF archive contains too many members")
        path = PurePosixPath(member.name)
        if (path.is_absolute() or not path.parts or path.parts[0] != expected_root
                or ".." in path.parts or "\\" in member.name or ":" in member.name
                or not (member.isdir() or member.isfile())
                or member.issym() or member.islnk()):
            raise ValueError(f"Unsafe CEF archive member: {member.name}")
        key = str(path).casefold()
        if key in seen:
            raise ValueError(f"Duplicate CEF archive member: {member.name}")
        seen.add(key)
        total += member.size
        if member.size < 0 or total > MAX_EXTRACTED_BYTES:
            raise ValueError("CEF archive exceeds the extracted size limit")


def extract(archive, destination, expected_root):
    destination = destination.resolve()
    if destination.exists():
        raise FileExistsError(f"Extraction destination already exists: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix="cef-extract-", dir=destination.parent))
    try:
        with tarfile.open(archive, "r:bz2") as tar:
            members = tar.getmembers()
            validate_members(members, expected_root)
            for member in members:
                relative = PurePosixPath(member.name).relative_to(expected_root)
                if str(relative) == ".":
                    continue
                target = staging.joinpath(*relative.parts)
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    source = tar.extractfile(member)
                    if source is None:
                        raise ValueError(f"Unreadable CEF member: {member.name}")
                    with source, target.open("xb") as output:
                        shutil.copyfileobj(source, output, 1024 * 1024)
                    os.chmod(target, member.mode & 0o777)
        staging.rename(destination)
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def archive_name(version, entry):
    """Official archive name; the "standard" type has no suffix, others (for example "minimal") do."""
    suffix = "" if entry.get("type", "standard") == "standard" else "_" + entry["type"]
    return f"cef_binary_{version}_{entry['platform']}{suffix}.tar.bz2"


def verify_pin(path, entry):
    """Size, upstream SHA-1 and, when pinned, SHA-256 (from the official .sha256 sidecar)."""
    sha1, sha256 = hashes(path)
    if path.stat().st_size != entry["bytes"] or sha1 != entry["sha1"]:
        raise ValueError(f"CEF archive size/upstream SHA-1 mismatch: {path}")
    if "sha256" in entry and sha256 != entry["sha256"]:
        raise ValueError(f"CEF archive SHA-256 mismatch: {path}")
    return sha1, sha256


def restore(rid, cache, output, offline=False):
    manifest = json.loads((ROOT / "scripts" / "cef-distributions.json").read_text(encoding="utf-8"))
    entry = manifest["distributions"][rid]
    version = manifest["cefVersion"]
    name = archive_name(version, entry)
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / name
    if not archive.exists():
        if offline:
            raise FileNotFoundError(f"Offline CEF archive missing: {archive}")
        handle, temporary = tempfile.mkstemp(prefix="cef-download-", suffix=".partial", dir=cache)
        try:
            with os.fdopen(handle, "wb") as target:
                with urllib.request.urlopen(manifest["origin"] + urllib.parse.quote(name), timeout=60) as response:
                    count = 0
                    while block := response.read(1024 * 1024):
                        count += len(block)
                        if count > entry["bytes"]:
                            raise ValueError("CEF download exceeds the pinned archive size")
                        target.write(block)
            temporary_path = Path(temporary)
            verify_pin(temporary_path, entry)
            temporary_path.replace(archive)
        finally:
            Path(temporary).unlink(missing_ok=True)
    sha1, sha256 = verify_pin(archive, entry)
    stamp = {"cefVersion": version, "rid": rid, "archive": name,
             "upstreamSha1": sha1, "sha256": sha256, "origin": manifest["origin"] + name}
    stamp_path = output / "cef-distribution.json"
    if output.exists():
        if not stamp_path.exists() or json.loads(stamp_path.read_text(encoding="utf-8")) != stamp:
            raise ValueError(f"Existing CEF output has a different or missing identity: {output}")
        # Revalidate the official archive on every call; extracted local edits are not a binary trust boundary.
    else:
        extract(archive, output, name.removesuffix(".tar.bz2"))
        stamp_path.write_text(json.dumps(stamp, indent=2) + "\n", encoding="utf-8")
    return {"directory": str(output.resolve()), **stamp}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=("win-x64", "win-arm64", "linux-x64", "linux-arm64"))
    parser.add_argument("--cache", type=Path, default=ROOT / "artifacts" / "cef" / "cache")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--offline", action="store_true")
    args = parser.parse_args()
    version = json.loads((ROOT / "scripts" / "cef-distributions.json").read_text(encoding="utf-8"))["cefVersion"]
    output = args.output or ROOT / "artifacts" / "cef" / version / args.rid / "distribution"
    print(json.dumps(restore(args.rid, args.cache.resolve(), output.resolve(), args.offline), indent=2))


if __name__ == "__main__":
    main()
