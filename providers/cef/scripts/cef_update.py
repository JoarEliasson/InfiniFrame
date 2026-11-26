"""Detects a newer official CEF build on the tracked channel and prepares the pin update.

Reads the official CEF build index (https://cef-builds.spotifycdn.com/index.json), finds the newest version on the
channel in scripts/cef-distributions.json that is published for every pinned platform and archive type, and compares
it with the pin. For a newer build it downloads the official .sha256 sidecars (the archives themselves are verified
later by cef_restore.py), then either

    --apply   rewrites scripts/cef-distributions.json (resetting the runtime revision to 1) and replaces the full CEF
              version string in the provider constant, the test fakes and the docs, for a build in the same
              major version (same stable API), or
    (always)  writes a JSON summary to --summary for the workflow: kind "none", "update" (same major) or
              "major" (a new major needs an API version migration and is reported as an issue, not edited).

Standard library only.
"""
from __future__ import annotations

import argparse
import json
import sys
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PIN = ROOT / "scripts" / "cef-distributions.json"
# Files that carry the full CEF version string (the provider's compatibility constant and the test doubles).
VERSION_FILES = ["src/Contoso.InfiniFrame.CefHost/CefHostOptions.cs", "tests/Contoso.InfiniFrame.CefHost.Tests/fake_companion.py",
                 "native/tests/smoke.py", "native/README.md", "README.md"]


def fetch(url, limit):
    with urllib.request.urlopen(url, timeout=60) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ValueError(f"{url} exceeds {limit} bytes")
    return data


def key(version):
    """Sort key of a CEF version such as 156.0.8+g426920c+chromium-156.0.8078.48."""
    return tuple(int(part) for part in version.split("+", 1)[0].split("."))


def archive_type(name, platform):
    stem = name.removesuffix(".tar.bz2")
    suffix = stem.split(f"_{platform}", 1)[1] if f"_{platform}" in stem else None
    return "standard" if suffix == "" else (suffix[1:] if suffix else None)


def candidates(index, pin):
    """Versions on the tracked channel available for every pinned platform/type, with their file entries."""
    channel = pin.get("channel", "stable")
    available = None
    files = {}
    for rid, entry in pin["distributions"].items():
        platform, kind = entry["platform"], entry.get("type", "standard")
        versions = {}
        for version in index.get(platform, {}).get("versions", []):
            if version.get("channel") != channel:
                continue
            for item in version.get("files", []):
                if item["name"].endswith(".tar.bz2") and archive_type(item["name"], platform) == kind:
                    versions[version["cef_version"]] = (version, item)
        files[rid] = versions
        available = set(versions) if available is None else available & set(versions)
    return sorted(available or [], key=key), files


def update_pin(pin, version, files):
    updated = json.loads(json.dumps(pin))
    updated["cefVersion"] = version
    updated["runtime"]["revision"] = 1
    for rid, entry in updated["distributions"].items():
        release, item = files[rid][version]
        updated["chromiumVersion"] = release.get("chromium_version", updated.get("chromiumVersion"))
        sidecar = fetch(pin["origin"] + urllib.parse.quote(item["name"]) + ".sha256", 256).decode("ascii").split()[0].lower()
        if len(sidecar) != 64 or any(c not in "0123456789abcdef" for c in sidecar):
            raise ValueError(f"invalid SHA-256 sidecar for {item['name']}")
        entry.update(sha1=item["sha1"], bytes=item["size"], sha256=sidecar)
    return updated


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--index", help="a local index.json instead of the official URL")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()
    pin = json.loads(PIN.read_text(encoding="utf-8"))
    index = json.loads(Path(args.index).read_bytes() if args.index else fetch(pin["origin"] + "index.json", 256 * 1024 * 1024))
    versions, files = candidates(index, pin)
    current = pin["cefVersion"]
    newest = versions[-1] if versions else None
    summary = {"current": current, "latest": newest, "channel": pin.get("channel", "stable"), "kind": "none"}
    if newest and key(newest) > key(current):
        summary["kind"] = "update" if key(newest)[0] == key(current)[0] else "major"
        summary["chromium"] = files[next(iter(files))][newest][0].get("chromium_version")
        if args.apply and summary["kind"] == "update":
            updated = update_pin(pin, newest, files)
            PIN.write_text(json.dumps(updated, indent=2) + "\n", encoding="utf-8", newline="\n")
            for relative in VERSION_FILES:
                path = ROOT / relative
                text = path.read_text(encoding="utf-8")
                if current in text:
                    path.write_text(text.replace(current, newest), encoding="utf-8", newline="")
            summary["applied"] = True
    if args.summary:
        args.summary.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    json.dump(summary, sys.stdout, indent=2)
    print()


if __name__ == "__main__":
    main()
