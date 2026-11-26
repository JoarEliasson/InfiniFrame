"""InfiniFrame CEF runtime: build, manifest, verify, test, pack and install the CEF companion runtime.

The runtime is the companion (InfiniFrame.Cef.Host) plus the complete official CEF binary payload for one RID,
described by cef-backend.json. It is versioned and installed separately from applications:

    <root>/<runtimeVersion>/              one complete, verified runtime per version (side by side)
    runtimeVersion = <CEF major.minor.patch>-p<protocol>.<revision>, for example 156.0.8-p3.1

Applications select a version by its explicit path (CefHostOptions.PayloadDirectory). The provider refuses a
runtime whose protocol, RID, CEF version or API differs, or whose files differ from the manifest's inventory.

Subcommands (standard library only; Python 3.10+):

    build     restore the pinned CEF archive (SHA-1 and SHA-256 verified), build and install the companion,
              strip debug sections on Linux, write cef-backend.json
    build-native  configure, clean-build and install from an explicit local CEF distribution (no restore)
    manifest  refresh an existing build's inventory/signature without changing its recorded build identity
    verify    check a payload exactly as the provider does (identity, required files, hashes, no extra files)
    test      run the native protocol/sandbox/smoke suites against a payload
    pack      write the distributable archive, its .sha256, an SBOM and a copy of the manifest
    install   install an archive into <root>/<runtimeVersion> atomically; never touches other versions
    list      list the versions installed under a root and whether each still verifies
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import cef_restore  # noqa: E402

API_VERSION = 15600
MANIFEST = "cef-backend.json"
BUILD_RECEIPT = "cef-build-receipt.json"
BUILD_FIELDS = ("protocol", "rid", "cefVersion", "chromiumVersion", "apiVersion", "runtimeName",
                "runtimeVersion", "configuration", "sandbox", "archiveSha256", "source", "companion",
                "strippedFromUpstream")
LPAC_GRANT = "*S-1-15-2-2:(OI)(CI)(RX)"
MAX_FILES = 4096
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64")


def fail(message):
    raise SystemExit(f"cef_runtime: {message}")


def pin():
    return json.loads((ROOT / "scripts" / "cef-distributions.json").read_text(encoding="utf-8"))


def protocol_version():
    match = re.search(r"kProtocolVersion\s*=\s*(\d+)\s*;", (ROOT / "native" / "protocol.h").read_text(encoding="utf-8"))
    if not match:
        fail("kProtocolVersion not found in native/protocol.h")
    return int(match.group(1))


def runtime_version(revision=None):
    distributions = pin()
    revision = distributions["runtime"]["revision"] if revision is None else revision
    if not isinstance(revision, int) or revision < 1:
        fail("the runtime revision must be a positive integer")
    short = distributions["cefVersion"].split("+", 1)[0]
    return f"{short}-p{protocol_version()}.{revision}"


def required_files(rid):
    native = (["InfiniFrame.Cef.Host.exe", "InfiniFrame.Cef.Host.dll", "libcef.dll", "chrome_elf.dll"]
              if rid.startswith("win-") else ["InfiniFrame.Cef.Host", "libcef.so"])
    return native + ["icudtl.dat", "resources.pak", "chrome_100_percent.pak", "chrome_200_percent.pak",
                     "v8_context_snapshot.bin", "locales/en-US.pak", "LICENSE.txt", "CREDITS.html"]


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def inventory(payload):
    files = []
    for path in sorted(payload.rglob("*")):
        if path.is_symlink():
            fail(f"payloads must not contain symbolic links: {path}")
        relative = path.relative_to(payload).as_posix()
        if path.is_file() and relative != MANIFEST:
            files.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256(path)})
    if not 1 <= len(files) <= MAX_FILES:
        fail(f"a payload holds 1..{MAX_FILES} files, found {len(files)}")
    return files


def git_identity():
    """Record the enclosing checkout commit and changes to the provider-relative native build inputs."""
    try:
        commit = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "HEAD"], capture_output=True, text=True,
                                check=True).stdout.strip()
        dirty = bool(subprocess.run(["git", "-C", str(ROOT), "status", "--porcelain", "--untracked-files=all", "--", "native", "scripts"],
                                    capture_output=True, text=True, check=True).stdout.strip())
        return {"commit": commit, "modified": dirty}
    except (OSError, subprocess.CalledProcessError):
        return {"commit": "unknown", "modified": True}


def read_record(path):
    """Read our small local records; duplicate fields make provenance ambiguous."""
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                fail(f"duplicate field {key!r} in {path}")
            result[key] = value
        return result

    if path.is_symlink() or not path.is_file() or path.stat().st_size > 1024 * 1024:
        fail(f"missing, linked or oversized record: {path}; rebuild with a supported build command")
    try:
        record = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        fail(f"unreadable record {path}: {error}")
    if not isinstance(record, dict):
        fail(f"record must be an object: {path}")
    return record


def write_record(path, record):
    """Replace only after validation; a failed write leaves the previous record intact."""
    data = json.dumps(record, indent=2) + "\n"
    if len(data.encode("utf-8")) > 1024 * 1024:
        fail(f"record exceeds 1 MiB: {path}")
    handle, temporary = tempfile.mkstemp(prefix=".cef-record-", dir=path.parent)
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(data)
        # mkstemp defaults to 0600 on POSIX; payload metadata must remain readable by the application user.
        Path(temporary).chmod(0o644)
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def distribution_identity(identity, rid):
    """A distribution identity identifies CEF, never the locally compiled companion."""
    distributions = pin()
    if rid not in RIDS:
        fail(f"unsupported RID: {rid!r}")
    entry = distributions["distributions"][rid]
    archive = cef_restore.archive_name(distributions["cefVersion"], entry)
    expected = {"cefVersion": distributions["cefVersion"], "rid": rid, "archive": archive,
                "origin": distributions["origin"] + archive, "upstreamSha1": entry["sha1"]}
    if "sha256" in entry:
        expected["sha256"] = entry["sha256"]
    if (any(identity.get(key) != value for key, value in expected.items())
            or not isinstance(identity.get("sha256"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", identity["sha256"])):
        fail("the CEF distribution identity does not match the pinned archive, checksums, version and RID")
    return {key: identity[key] for key in ("cefVersion", "rid", "archive", "origin", "upstreamSha1", "sha256")}


def source_identity():
    """Snapshot local build inputs too: two dirty trees need not contain the same source."""
    digest = hashlib.sha256()
    for folder in (ROOT / "native", ROOT / "scripts"):
        for path in sorted(folder.rglob("*")):
            if "__pycache__" in path.parts:
                continue
            if path.is_symlink():
                fail(f"linked build input: {path}")
            if path.is_file():
                digest.update(path.relative_to(ROOT).as_posix().encode("utf-8") + b"\0")
                digest.update(sha256(path).encode("ascii") + b"\n")
    return {**git_identity(), "inputsSha256": digest.hexdigest()}


def build_identity(rid, revision, identity, configuration):
    distributions = pin()
    identity = distribution_identity(identity, rid)
    return {
        "protocol": protocol_version(),
        "rid": rid,
        "cefVersion": distributions["cefVersion"],
        "chromiumVersion": distributions.get("chromiumVersion", distributions["cefVersion"].rsplit("chromium-", 1)[-1]),
        "apiVersion": API_VERSION,
        "runtimeName": distributions["runtime"]["name"],
        "runtimeVersion": runtime_version(revision),
        "configuration": configuration,
        "sandbox": True,
        "archiveSha256": identity["sha256"],
        "source": {"archive": identity["archive"], "origin": identity["origin"], "upstreamSha1": identity["upstreamSha1"],
                   "sha256": identity["sha256"]},
        "companion": source_identity(),
        "strippedFromUpstream": {},
    }


def validate_build_identity(identity, compatibility=True):
    if not isinstance(identity, dict) or set(identity) != set(BUILD_FIELDS):
        fail("incomplete or ambiguous native build identity; rebuild with a supported build command")
    if (identity["rid"] not in RIDS or identity["sandbox"] is not True
            or type(identity["protocol"]) is not int or identity["protocol"] < 1
            or type(identity["apiVersion"]) is not int or identity["apiVersion"] < 1
            or identity["configuration"] not in ("Release", "Debug")
            or not all(isinstance(identity[key], str) and identity[key]
                       for key in ("cefVersion", "chromiumVersion", "runtimeName", "runtimeVersion"))):
        fail("invalid native build identity")
    version = re.fullmatch(re.escape(identity["cefVersion"].split("+", 1)[0])
                           + rf"-p{identity['protocol']}\.([1-9][0-9]*)", identity["runtimeVersion"])
    companion, source = identity["companion"], identity["source"]
    if (not version or not isinstance(companion, dict)
            or set(companion) != {"commit", "modified", "inputsSha256"}
            or not isinstance(companion["commit"], str)
            or not (companion["commit"] == "unknown" or re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", companion["commit"]))
            or type(companion["modified"]) is not bool
            or (companion["commit"] == "unknown" and not companion["modified"])
            or not isinstance(companion["inputsSha256"], str)
            or not re.fullmatch(r"[0-9a-f]{64}", companion["inputsSha256"])
            or not isinstance(source, dict) or set(source) != {"archive", "origin", "upstreamSha1", "sha256"}
            or not all(isinstance(value, str) and value for value in source.values())
            or not re.fullmatch(r"[0-9a-f]{40}", source["upstreamSha1"])
            or not re.fullmatch(r"[0-9a-f]{64}", source["sha256"])
            or identity["archiveSha256"] != source["sha256"]
            or not isinstance(identity["strippedFromUpstream"], dict)):
        fail("invalid or ambiguous native build provenance")
    if compatibility:
        expected = {"protocol": protocol_version(), "apiVersion": API_VERSION,
                    "cefVersion": pin()["cefVersion"]}
        if any(identity[key] != value for key, value in expected.items()):
            fail("incompatible native build protocol/CEF/API; use matching tools or rebuild the companion")
        distribution_identity({"cefVersion": identity["cefVersion"], "rid": identity["rid"], **source}, identity["rid"])


def receipt_identity(payload, manifest, compatibility=True):
    """Require the original, inventoried receipt before trusting refreshable metadata."""
    path = payload / BUILD_RECEIPT
    receipt = read_record(path)
    if set(receipt) != {"schemaVersion", "identity"} or type(receipt["schemaVersion"]) is not int or receipt["schemaVersion"] != 1:
        fail("unsupported or ambiguous native build receipt")
    identity = receipt["identity"]
    validate_build_identity(identity, compatibility)
    if json.dumps({key: manifest.get(key) for key in BUILD_FIELDS}, sort_keys=True) != json.dumps(identity, sort_keys=True):
        fail("manifest identity differs from the original native build receipt; refusing provenance changes")
    files = manifest.get("files")
    if not isinstance(files, list):
        fail("missing inventory for native build receipt")
    records = [entry for entry in files if isinstance(entry, dict) and entry.get("path") == BUILD_RECEIPT]
    if (len(records) != 1 or records[0].get("bytes") != path.stat().st_size
            or records[0].get("sha256") != sha256(path)):
        fail("native build receipt is missing from the inventory or changed; refusing provenance changes")
    return identity


def machine_rid():
    """The RID of this machine, as CefHostOptions.CurrentRid computes it for the running process."""
    import platform
    system = {"win32": "win", "linux": "linux"}.get(sys.platform)
    arch = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    return f"{system}-{arch}" if system and arch else None


def verify(payload, rid=None, compatibility=True, *, manifest=None, check_hashes=True):
    """The provider's checks (CefPayload.Validate) for a payload directory. Returns the manifest.

    compatibility=False checks integrity only (manifest shape, sandbox, RID when given, required files, hashes, no
    extra files), not the protocol/CEF/API pairing with this checkout, so `install` and `list` keep handling the
    older versions kept for rollback after the pin or protocol moved on.
    """
    payload = payload.resolve()
    manifest_path = payload / MANIFEST
    if manifest is None:
        manifest = read_record(manifest_path)
    if not isinstance(manifest, dict) or manifest.get("rid") not in RIDS:
        fail(f"the manifest at {manifest_path} is not an object with a supported RID")
    rid = rid or manifest["rid"]
    expected = {"rid": rid, "sandbox": True}
    if compatibility:
        expected.update(protocol=protocol_version(), cefVersion=pin()["cefVersion"], apiVersion=API_VERSION)
    mismatches = [f"{key} {manifest.get(key)!r} (expected {value!r})" for key, value in expected.items()
                  if manifest.get(key) != value]
    if mismatches:
        fail(f"incompatible runtime at {payload}: " + "; ".join(mismatches))
    files = manifest.get("files")
    if (not isinstance(files, list) or not 1 <= len(files) <= MAX_FILES
            or not all(isinstance(entry, dict) and isinstance(entry.get("path"), str) for entry in files)):
        fail("the manifest needs a bounded file inventory")
    listed = set()
    for entry in files:
        relative = entry.get("path", "")
        parts = relative.split("/")
        if (not relative or "\\" in relative or ":" in relative or any(part in ("", ".", "..") for part in parts)
                or relative in listed or relative == MANIFEST):
            fail(f"unsafe or duplicate inventory path: {relative!r}")
        listed.add(relative)
        path = payload.joinpath(*parts)
        if (type(entry.get("bytes")) is not int or entry["bytes"] < 0
                or not isinstance(entry.get("sha256"), str) or not re.fullmatch(r"[0-9a-f]{64}", entry["sha256"])):
            fail(f"invalid inventory metadata: {relative}")
        if (path.is_symlink() or not path.is_file()
                or (check_hashes and (path.stat().st_size != entry["bytes"] or sha256(path) != entry["sha256"]))):
            fail(f"payload file differs from its manifest: {relative}")
    for name in required_files(rid):
        if name not in listed:
            fail(f"required file absent from the inventory: {name}")
    for path in payload.rglob("*"):
        if path.is_symlink():
            fail(f"symbolic link in payload: {path}")
        relative = path.relative_to(payload).as_posix()
        if path.is_file() and relative != MANIFEST and relative not in listed:
            fail(f"unlisted file in payload: {relative}")
    # Historical manifests can still be inspected, but cannot bootstrap a receipt by refresh.
    if BUILD_RECEIPT in listed or (payload / BUILD_RECEIPT).exists():
        receipt_identity(payload, manifest, compatibility)
    return manifest


def cmake_executable():
    return shutil.which("cmake") or fail("cmake was not found on PATH")


def visual_studio_generator():
    vswhere = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if vswhere.is_file():
        found = subprocess.run([str(vswhere), "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild",
                                "-version", "[18.0,19.0)", "-property", "installationPath"], capture_output=True, text=True)
        if found.stdout.strip():
            return "Visual Studio 18 2026"
    return "Visual Studio 17 2022"


def strip_debug(payload):
    """Removes DWARF debug sections from ELF files (the official libcef.so carries about 2 GB). Records originals."""
    strip = shutil.which("strip") or fail("strip (binutils) was not found")
    upstream = {}
    for path in sorted(payload.rglob("*")):
        if path.is_file() and not path.is_symlink():
            with open(path, "rb") as stream:
                if stream.read(4) != b"\x7fELF":
                    continue
            original = sha256(path)
            subprocess.run([strip, "--strip-debug", str(path)], check=True)
            if sha256(path) != original:
                upstream[path.relative_to(payload).as_posix()] = original
    return upstream


def check_build_platform(rid):
    if (os.name == "nt") != rid.startswith("win-"):
        fail("build each RID on its own operating system")
    if rid.startswith("linux-"):
        machine = os.uname().machine
        if {"x86_64": "linux-x64", "aarch64": "linux-arm64"}.get(machine) != rid:
            fail(f"the Linux build needs a native {rid} toolchain (this machine is {machine}); use the ARM64 container")


def build_payload(args, cef_root, build_dir, payload, *, transport=False, auto_ninja=True):
    """The only receipt producer: own configure/clean-build/install, never adopt existing binaries.

    This is a trusted local tool contract, not an authenticated attestation. It assumes the compiler,
    CMake and restored distribution are trusted, and no input is changed and reverted during the build.
    """
    rid = args.rid
    check_build_platform(rid)
    if payload.exists():
        fail(f"native build output already exists: {payload}; choose a fresh output directory")
    identity_path = cef_root / "cef-distribution.json"
    identity = distribution_identity(read_record(identity_path), rid)
    captured = build_identity(rid, args.revision, identity, args.configuration)
    validate_build_identity(captured)
    cmake = cmake_executable()
    configure = [cmake, "-S", str(ROOT / "native"), "-B", str(build_dir), f"-DCEF_ROOT={cef_root.as_posix()}",
                 f"-Dapi_version={API_VERSION}", f"-DCMAKE_BUILD_TYPE={args.configuration}",
                 "-DINFINIFRAME_CEF_ENABLE_SANITIZERS=OFF"]
    if rid.startswith("win-"):
        configure += ["-G", args.generator or visual_studio_generator(), "-A", "ARM64" if rid.endswith("arm64") else "x64"]
    elif args.generator or (auto_ninja and shutil.which("ninja")):
        configure += ["-G", args.generator or "Ninja"]
    subprocess.run(configure, check=True)
    # A cached object/POST_BUILD layout must never become evidence for a different source snapshot.
    compile_command = [cmake, "--build", str(build_dir), "--config", args.configuration, "--clean-first",
                       "--target", "InfiniFrame.Cef.Host"]
    if transport:
        compile_command.append("InfiniFrame.Cef.TransportTests")
    compile_command.append("--parallel")
    if args.parallel:
        compile_command.append(str(args.parallel))
    if args.binlog and rid.startswith("win-") and configure[configure.index("-G") + 1].startswith("Visual Studio"):
        compile_command += ["--", f"/bl:{args.binlog}", "/v:minimal"]
    subprocess.run(compile_command, check=True)
    subprocess.run([cmake, "--install", str(build_dir), "--config", args.configuration, "--prefix", str(payload)], check=True)
    if rid.startswith("win-") and sha256(cef_root / args.configuration / "bootstrapc.exe") != sha256(payload / "InfiniFrame.Cef.Host.exe"):
        fail("the installed bootstrap differs from the pinned official distribution")
    upstream = strip_debug(payload) if rid.startswith("linux-") and not args.keep_debug else {}
    if (distribution_identity(read_record(identity_path), rid) != identity
            or build_identity(rid, args.revision, identity, args.configuration) != captured):
        fail("native build inputs changed during the build; no build receipt was emitted")
    for name in required_files(rid):
        if not (payload / name).is_file():
            fail(f"the installed payload is incomplete: {name}")
    inventory(payload)  # Reject links and invalid output before writing provenance.
    if (payload / MANIFEST).exists() or (payload / BUILD_RECEIPT).exists():
        fail("native install unexpectedly supplied provenance records; refusing to replace them")
    captured["strippedFromUpstream"] = upstream
    write_record(payload / BUILD_RECEIPT, {"schemaVersion": 1, "identity": captured})
    manifest = {**captured, "signature": "none", "files": inventory(payload)}
    verify(payload, rid, manifest=manifest)
    write_record(payload / MANIFEST, manifest)
    print(f"CEF_RUNTIME_VERSION={manifest['runtimeVersion']}")
    print(f"CEF_PAYLOAD_DIRECTORY={payload}")
    if transport:
        executable = next((build_dir / "transport-tests").rglob("InfiniFrame.Cef.TransportTests*"), None)
        if executable:
            print(f"CEF_TRANSPORT_TESTS={executable}")


def build(args):
    check_build_platform(args.rid)
    work = args.work.resolve()
    version = runtime_version(args.revision)
    cef_root = (args.cef_root.resolve() if args.cef_root
                else Path(cef_restore.restore(args.rid, (work / "cache").resolve(),
                                              (work / "cef" / args.rid / "distribution").resolve(), args.offline)["directory"]))
    distribution_identity(read_record(cef_root / "cef-distribution.json"), args.rid)
    payload = work / "runtime" / version / args.rid
    if payload.exists() and args.force:
        shutil.rmtree(payload)
    build_payload(args, cef_root, work / "build" / args.rid, payload, transport=True)


def build_native(args):
    # No restore/download path: the direct developer build requires an explicit local distribution.
    build_payload(args, args.cef_root.resolve(), args.build_dir.resolve(), args.payload.resolve(), auto_ninja=False)


def manifest_command(args):
    payload = args.payload.resolve()
    current = read_record(payload / MANIFEST)
    original = receipt_identity(payload, current)
    rid = args.rid or original["rid"]
    if ((args.revision is not None and runtime_version(args.revision) != original["runtimeVersion"])
            or (args.configuration is not None and args.configuration != original["configuration"])):
        fail("manifest refresh cannot change the native build revision or configuration; rebuild instead")
    if args.identity:
        supplied = distribution_identity(read_record(args.identity), rid)
        if supplied != {"cefVersion": original["cefVersion"], "rid": original["rid"], **original["source"]}:
            fail("--identity differs from the original native build distribution")
    # The file set and receipt stay fixed; trusted post-build signing may change file bytes.
    verify(payload, rid, manifest=current, check_hashes=False)
    refreshed = {**current, "files": inventory(payload)}
    if args.signature is not None:
        refreshed["signature"] = args.signature
    verify(payload, rid, manifest=refreshed)
    write_record(payload / MANIFEST, refreshed)
    print(f"CEF_RUNTIME_VERSION={original['runtimeVersion']}")
    print(f"CEF_PAYLOAD_DIRECTORY={payload}")


def test(args):
    payload = args.payload.resolve()
    manifest = verify(payload)
    rid = manifest["rid"]
    host = payload / ("InfiniFrame.Cef.Host.exe" if rid.startswith("win-") else "InfiniFrame.Cef.Host")
    logs = (args.logs or payload.parent / f"{rid}-test-logs").resolve()
    logs.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, INFINIFRAME_CEF_SMOKE_HOST=str(host), INFINIFRAME_CEF_SMOKE_RID=rid,
                       INFINIFRAME_CEF_SMOKE_LOGS=str(logs), INFINIFRAME_CEF_SMOKE_CWD=str(Path(tempfile.gettempdir())))
    suites = ["protocol_version_regression.py"]
    if rid.startswith("linux-"):
        suites += ["check_posix_install.py", "check_library_paths.py"]
    if args.suite in ("smoke", "full"):
        suites += ["smoke.py", "window_style_regression.py", "navigation_resource_regression.py"]
    if args.suite == "full":
        suites += ["geometry_state_regression.py"]
    results = []
    if args.transport_tests:
        code = subprocess.run([str(args.transport_tests)]).returncode
        results.append(("transport_deadlines_test", code))
    for suite in suites:
        print(f"=== {suite}", flush=True)
        code = subprocess.run([sys.executable, str(ROOT / "native" / "tests" / suite)], env=environment,
                              cwd=str(ROOT / "native" / "tests")).returncode
        results.append((suite, code))
    (logs / "results.json").write_text(json.dumps(dict(results), indent=2) + "\n", encoding="utf-8")
    for name, code in results:
        print(f"{'PASS' if code == 0 else 'FAIL'} {name} (exit {code})")
    if any(code for _, code in results):
        raise SystemExit(1)


def sbom(manifest):
    cef = manifest["cefVersion"]
    return {
        "bomFormat": "CycloneDX", "specVersion": "1.5", "version": 1,
        "metadata": {"component": {"type": "application", "name": manifest["runtimeName"],
                                   "version": manifest["runtimeVersion"], "bom-ref": "runtime"}},
        "components": [
            {"type": "framework", "name": "CEF", "version": cef, "bom-ref": "cef",
             "purl": f"pkg:generic/cef@{cef.replace('+', '%2B')}?platform={manifest['source']['archive']}",
             "externalReferences": [{"type": "distribution", "url": manifest["source"]["origin"]}],
             "hashes": [{"alg": "SHA-256", "content": manifest["source"]["sha256"]},
                        {"alg": "SHA-1", "content": manifest["source"]["upstreamSha1"]}]},
            {"type": "framework", "name": "Chromium", "version": manifest["chromiumVersion"], "bom-ref": "chromium",
             "purl": f"pkg:generic/chromium@{manifest['chromiumVersion']}",
             "externalReferences": [{"type": "vcs", "url": "https://chromium.googlesource.com/chromium/src"}]},
            {"type": "application", "name": "InfiniFrame.Cef.Host", "version": manifest["runtimeVersion"],
             "bom-ref": "companion", "properties": [{"name": "commit", "value": manifest["companion"]["commit"]},
                                                     {"name": "protocol", "value": str(manifest["protocol"])}]},
        ],
        "dependencies": [{"ref": "runtime", "dependsOn": ["cef", "companion"]}, {"ref": "cef", "dependsOn": ["chromium"]}],
    }


def pack(args):
    payload = args.payload.resolve()
    manifest = verify(payload)
    version, rid = manifest["runtimeVersion"], manifest["rid"]
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    base = f"infiniframe-cef-runtime-{version}-{rid}"
    entries = sorted(path for path in payload.rglob("*") if path.is_file())
    if rid.startswith("win-"):
        archive = out / f"{base}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
            for path in entries:
                info = zipfile.ZipInfo(f"{version}/{path.relative_to(payload).as_posix()}", (2020, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(path, "rb") as source, bundle.open(info, "w") as target:
                    shutil.copyfileobj(source, target, 1024 * 1024)
    else:
        archive = out / f"{base}.tar.gz"
        with tarfile.open(archive, "w:gz", compresslevel=6) as bundle:
            for path in entries:
                info = bundle.gettarinfo(str(path), f"{version}/{path.relative_to(payload).as_posix()}")
                info.uid = info.gid = 0
                info.uname = info.gname = "root"
                info.mtime = 1577836800
                info.mode = 0o755 if path.name == "InfiniFrame.Cef.Host" else 0o644
                with open(path, "rb") as source:
                    bundle.addfile(info, source)
    digest = sha256(archive)
    (out / f"{archive.name}.sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8", newline="\n")
    (out / f"{base}.cef-backend.json").write_text((payload / MANIFEST).read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
    (out / f"{base}.cdx.json").write_text(json.dumps(sbom(manifest), indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"CEF_RUNTIME_ARCHIVE={archive}")
    print(f"CEF_RUNTIME_ARCHIVE_SHA256={digest}")


def safe_members(names):
    roots = set()
    for name in names:
        path = PurePosixPath(name)
        if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name or len(path.parts) < 2:
            fail(f"unsafe archive member: {name}")
        roots.add(path.parts[0])
    if len(roots) != 1:
        fail("the archive must contain exactly one runtime version folder")
    return roots.pop()


def install(args):
    archive = args.archive.resolve()
    sidecar = archive.with_name(archive.name + ".sha256")
    expected = args.sha256 or (sidecar.read_text(encoding="utf-8").split()[0] if sidecar.is_file() else None)
    if not expected:
        fail("pass --sha256 or keep the archive's .sha256 file beside it")
    if sha256(archive) != expected.lower():
        fail("the archive does not match its SHA-256")
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".staging-", dir=root))
    try:
        if archive.name.endswith(".zip"):
            with zipfile.ZipFile(archive) as bundle:
                version = safe_members([info.filename for info in bundle.infolist() if not info.is_dir()])
                bundle.extractall(staging)
        else:
            with tarfile.open(archive, "r:gz") as bundle:
                members = bundle.getmembers()
                if any(not (member.isfile() or member.isdir()) for member in members):
                    fail("the archive may contain only files and folders")
                version = safe_members([member.name for member in members if member.isfile()])
                bundle.extractall(staging, filter="data")
                host = staging / version / "InfiniFrame.Cef.Host"
                if host.is_file():
                    host.chmod(0o755)
        if not re.fullmatch(r"\d+\.\d+\.\d+-p\d+\.\d+", version):
            fail(f"unexpected runtime version folder: {version}")
        rid = args.rid or machine_rid() or fail("cannot determine this machine's RID; pass --rid")
        # Integrity and RID only: an installer may (re)install an older version kept for rollback.
        manifest = verify(staging / version, rid, compatibility=False)
        if manifest["runtimeVersion"] != version:
            fail("the archive folder does not match the manifest's runtime version")
        target = root / version
        if target.exists():
            installed = verify(target, rid, compatibility=False)
            if installed["files"] != manifest["files"]:
                fail(f"{target} already holds different files; runtime versions are immutable")
            print(f"CEF_RUNTIME_INSTALLED={target} (already present)")
            return
        if os.name == "nt":
            # The installer owns this grant: read/execute for the LPAC sandbox on this version folder only.
            subprocess.run(["icacls", str(staging / version), "/grant", LPAC_GRANT, "/Q"], check=True)
        os.replace(staging / version, target)
        print(f"CEF_RUNTIME_INSTALLED={target}")
    finally:
        shutil.rmtree(staging, ignore_errors=True)


def list_command(args):
    root = args.root.resolve()
    rid = args.rid or machine_rid()
    for folder in sorted(path for path in root.iterdir() if path.is_dir() and not path.name.startswith(".")) if root.is_dir() else []:
        try:
            manifest = verify(folder, rid, compatibility=False)
            print(f"{folder.name}\tintact\t{manifest['rid']}\tprotocol {manifest.get('protocol')}\t"
                  f"CEF {manifest.get('cefVersion')}\tAPI {manifest.get('apiVersion')}\t{folder}")
        except (SystemExit, Exception) as error:  # report every damaged folder and continue
            print(f"{folder.name}\tDAMAGED OR OTHER RID\t{error}\t{folder}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    command = commands.add_parser("build")
    command.add_argument("--rid", required=True, choices=RIDS)
    command.add_argument("--work", type=Path, default=ROOT / "artifacts" / "runtime")
    command.add_argument("--cef-root", type=Path, help="an already restored distribution (with cef-distribution.json)")
    command.add_argument("--revision", type=int)
    command.add_argument("--configuration", default="Release", choices=("Release", "Debug"))
    command.add_argument("--generator")
    command.add_argument("--offline", action="store_true")
    command.add_argument("--force", action="store_true")
    command.add_argument("--keep-debug", action="store_true", help="Linux: keep upstream debug sections")
    command.set_defaults(run=build, parallel=None, binlog=None)
    command = commands.add_parser("build-native", help="actual local configure/clean-build/install; never imports provenance")
    command.add_argument("--rid", required=True, choices=RIDS)
    command.add_argument("--cef-root", type=Path, required=True, help="existing restored distribution; never downloaded")
    command.add_argument("--build-dir", type=Path, required=True)
    command.add_argument("--payload", type=Path, required=True, help="fresh, nonexistent installation directory")
    command.add_argument("--revision", type=int)
    command.add_argument("--configuration", default="Release", choices=("Release", "Debug"))
    command.add_argument("--generator")
    command.add_argument("--parallel", type=int, default=4)
    command.add_argument("--binlog", type=Path, help="MSBuild binary log, for a Windows Visual Studio generator")
    command.set_defaults(run=build_native, keep_debug=True)
    command = commands.add_parser("manifest")
    command.add_argument("--payload", type=Path, required=True)
    command.add_argument("--rid", choices=RIDS)
    command.add_argument("--identity", type=Path, help="optional equality check against the original build; cannot bootstrap provenance")
    command.add_argument("--revision", type=int, help="optional equality check; refresh cannot change the build revision")
    command.add_argument("--signature", help="for example 'authenticode' after signing")
    command.add_argument("--configuration", choices=("Release", "Debug"))
    command.set_defaults(run=manifest_command)
    command = commands.add_parser("verify")
    command.add_argument("--payload", type=Path, required=True)
    command.add_argument("--rid", choices=RIDS)
    command.set_defaults(run=lambda args: print(json.dumps({key: value for key, value in verify(args.payload, args.rid).items()
                                                            if key != "files"}, indent=2)))
    command = commands.add_parser("test")
    command.add_argument("--payload", type=Path, required=True)
    command.add_argument("--suite", choices=("basic", "smoke", "full"), default="basic",
                         help="basic: no display needed; smoke: needs a desktop session; full: adds window-manager state tests")
    command.add_argument("--transport-tests", type=Path)
    command.add_argument("--logs", type=Path)
    command.set_defaults(run=test)
    command = commands.add_parser("pack")
    command.add_argument("--payload", type=Path, required=True)
    command.add_argument("--out", type=Path, required=True)
    command.set_defaults(run=pack)
    command = commands.add_parser("install")
    command.add_argument("--archive", type=Path, required=True)
    command.add_argument("--root", type=Path, required=True)
    command.add_argument("--sha256")
    command.add_argument("--rid", choices=RIDS, help="the target RID (default: this machine's)")
    command.set_defaults(run=install)
    command = commands.add_parser("list")
    command.add_argument("--root", type=Path, required=True)
    command.add_argument("--rid", choices=RIDS, help="the target RID (default: this machine's)")
    command.set_defaults(run=list_command)
    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
