"""Offline provenance fixtures. No CEF, compiler, native process, download, install or Git process runs.

Run: python -B -m unittest discover -s tests -p test_cef_runtime.py -v
"""
from __future__ import annotations

import argparse
import contextlib
import copy
import io
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

REPOSITORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY / "scripts"))
import cef_runtime as runtime


class RuntimeProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="cef-provenance-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        (self.source / "native").mkdir(parents=True)
        (self.source / "scripts").mkdir()
        self.protocol(3)
        self.pin = json.loads((REPOSITORY / "scripts/cef-distributions.json").read_text(encoding="utf-8"))
        self.write_pin()
        self.git = {"commit": "1" * 40, "modified": False}
        self.commands = []
        self.patch(runtime, "ROOT", self.source)
        self.patch(runtime, "git_identity", side_effect=lambda: dict(self.git))
        self.patch(runtime.subprocess, "run", side_effect=AssertionError("unexpected external process"))
        self.patch(runtime.cef_restore, "restore", side_effect=AssertionError("unexpected restore/download"))
        self.patch(runtime, "cmake_executable", return_value="cmake-fixture")
        self.patch(runtime, "visual_studio_generator", return_value="Visual Studio 17 2022")
        self.patch(runtime, "check_build_platform")  # Cross-RID fixture bytes, never a real cross-build.
        self.patch(runtime.shutil, "which", return_value=None)
        self.addCleanup(mock.patch.stopall)
        output = contextlib.redirect_stdout(io.StringIO())
        output.__enter__()
        self.addCleanup(output.__exit__, None, None, None)

    def patch(self, target, name, *args, **kwargs):
        return mock.patch.object(target, name, *args, **kwargs).start()

    def protocol(self, value):
        (self.source / "native/protocol.h").write_text(f"constexpr uint16_t kProtocolVersion = {value};\n", encoding="utf-8")

    def write_pin(self):
        self.save(self.source / "scripts/cef-distributions.json", self.pin)

    @staticmethod
    def save(path, value):
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def distribution(self, rid):
        entry = self.pin["distributions"][rid]
        name = runtime.cef_restore.archive_name(self.pin["cefVersion"], entry)
        return {"cefVersion": self.pin["cefVersion"], "rid": rid, "archive": name,
                "origin": self.pin["origin"] + name, "upstreamSha1": entry["sha1"], "sha256": entry["sha256"]}

    def prepare(self, rid="win-x64", **options):
        cef = self.root / ("cef-" + rid)
        cef.mkdir(exist_ok=True)
        self.save(cef / "cef-distribution.json", self.distribution(rid))
        for configuration in ("Debug", "Release"):
            (cef / configuration).mkdir(exist_ok=True)
            (cef / configuration / "bootstrapc.exe").write_bytes(b"fixture official bootstrap")
        args = argparse.Namespace(rid=rid, revision=None, configuration="Release", generator=None, keep_debug=True,
                                  parallel=None, binlog=None, cef_root=cef, build_dir=self.root / ("build-" + rid),
                                  payload=self.root / ("payload-" + rid), offline=True, force=False,
                                  work=self.root / ("work-" + rid))
        for key, value in options.items():
            setattr(args, key, value)
        return args

    def fake_cmake(self, args, *, fail_step=None, after_install=None):
        def execute(command, **kwargs):
            self.commands.append(command)
            self.assertTrue(kwargs.get("check"))
            self.assertEqual("cmake-fixture", command[0])
            step = "install" if "--install" in command else "build" if "--build" in command else "configure"
            if step == fail_step:
                raise subprocess.CalledProcessError(1, command)
            if step == "install":
                payload = Path(command[command.index("--prefix") + 1])
                self.assertFalse(payload.exists())
                for name in runtime.required_files(args.rid):
                    path = payload / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(("fixture " + name).encode("utf-8"))
                if args.rid.startswith("win-"):
                    (payload / "InfiniFrame.Cef.Host.exe").write_bytes(b"fixture official bootstrap")
                if after_install:
                    after_install(payload)
            return subprocess.CompletedProcess(command, 0)
        return mock.patch.object(runtime.subprocess, "run", side_effect=execute)

    def fresh(self, rid="win-x64", **options):
        args = self.prepare(rid, **options)
        with self.fake_cmake(args):
            runtime.build_native(args)
        return args.payload

    @staticmethod
    def manifest(payload):
        return runtime.read_record(payload / runtime.MANIFEST)

    def refresh(self, payload, **options):
        args = argparse.Namespace(payload=payload, identity=None, rid=None, revision=None, configuration=None, signature=None)
        for key, value in options.items():
            setattr(args, key, value)
        runtime.manifest_command(args)

    def refused(self, payload, message=None, **options):
        before = {path.relative_to(payload): path.read_bytes() for path in payload.rglob("*") if path.is_file()}
        with self.assertRaisesRegex(SystemExit, message or "cef_runtime:"):
            self.refresh(payload, **options)
        after = {path.relative_to(payload): path.read_bytes() for path in payload.rglob("*") if path.is_file()}
        self.assertEqual(before, after, "a rejected refresh must not overwrite any payload evidence")

    def legacy(self, payload):
        manifest = self.manifest(payload)
        (payload / runtime.BUILD_RECEIPT).unlink()
        manifest["files"] = [entry for entry in manifest["files"] if entry["path"] != runtime.BUILD_RECEIPT]
        manifest["companion"].pop("inputsSha256")
        self.save(payload / runtime.MANIFEST, manifest)

    def test_p2_to_p3_same_pin_refresh_refused_with_or_without_identity(self):
        self.protocol(2)
        payload = self.fresh()
        self.protocol(3)
        self.refused(payload, "incompatible native build")
        self.refused(payload, "incompatible native build", identity=self.root / "cef-win-x64/cef-distribution.json")
        self.assertEqual(2, runtime.verify(payload, compatibility=False)["protocol"])
        with self.assertRaisesRegex(SystemExit, "incompatible runtime"):
            runtime.verify(payload)
        with self.assertRaisesRegex(SystemExit, "incompatible runtime"):
            runtime.pack(argparse.Namespace(payload=payload, out=self.root / "rejected-package"))
        self.assertFalse((self.root / "rejected-package").exists())

    def test_p2_legacy_refresh_refused_without_overwrite(self):
        self.protocol(2)
        payload = self.fresh()
        self.legacy(payload)
        self.protocol(3)
        self.refused(payload, "rebuild with a supported build command")

    def test_later_checkout_preserves_original_commit_revision_and_input_hash(self):
        payload = self.fresh()
        original = self.manifest(payload)
        self.git = {"commit": "2" * 40, "modified": False}
        self.pin["runtime"]["revision"] += 1
        self.write_pin()
        (self.source / "native/host.cpp").write_text("// newer source, same protocol", encoding="utf-8")
        self.refresh(payload)
        self.assertEqual(original, self.manifest(payload))

    def test_unknown_and_modified_build_provenance_is_never_upgraded(self):
        for number, identity in enumerate(({"commit": "unknown", "modified": True},
                                           {"commit": "a" * 40, "modified": True})):
            with self.subTest(identity=identity):
                self.git = identity
                payload = self.fresh(payload=self.root / f"payload-{number}")
                original = self.manifest(payload)["companion"]
                self.git = {"commit": "b" * 40, "modified": False}
                with mock.patch.object(runtime, "git_identity", side_effect=AssertionError("refresh read current Git identity")):
                    self.refresh(payload)
                self.assertEqual(original, self.manifest(payload)["companion"])

    def test_signing_updates_inventory_and_signature_only(self):
        payload = self.fresh()
        before = self.manifest(payload)
        receipt_bytes = (payload / runtime.BUILD_RECEIPT).read_bytes()
        host = payload / "InfiniFrame.Cef.Host.dll"
        host.write_bytes(host.read_bytes() + b" fixture signing bytes")
        with self.assertRaisesRegex(SystemExit, "differs from its manifest"):
            runtime.verify(payload)
        self.refresh(payload, signature="authenticode")
        after = runtime.verify(payload)
        self.assertEqual(receipt_bytes, (payload / runtime.BUILD_RECEIPT).read_bytes())
        self.assertEqual("authenticode", after["signature"])
        self.assertNotEqual(before["files"], after["files"])
        self.assertEqual({k: v for k, v in before.items() if k not in ("files", "signature")},
                         {k: v for k, v in after.items() if k not in ("files", "signature")})
        out = self.root / "fixture-package"
        runtime.pack(argparse.Namespace(payload=payload, out=out))
        with zipfile.ZipFile(next(out.glob("*.zip"))) as archive:
            self.assertEqual(receipt_bytes, archive.read(after["runtimeVersion"] + "/" + runtime.BUILD_RECEIPT))
            self.assertEqual(after, json.loads(archive.read(after["runtimeVersion"] + "/" + runtime.MANIFEST)))

    def test_distribution_identity_cannot_bootstrap_manifest(self):
        args = self.prepare()
        args.payload.mkdir()
        self.refused(args.payload, "missing", identity=args.cef_root / "cef-distribution.json", rid=args.rid)
        self.assertEqual([], list(args.payload.iterdir()))

    def test_legacy_unknown_remains_inspectable_but_refresh_cannot_adopt_it(self):
        payload = self.fresh()
        self.legacy(payload)
        manifest = self.manifest(payload)
        manifest["companion"] = {"commit": "unknown", "modified": True}
        self.save(payload / runtime.MANIFEST, manifest)
        self.assertEqual(manifest, runtime.verify(payload))
        self.refused(payload, "missing", identity=self.root / "cef-win-x64/cef-distribution.json")
        self.assertFalse((payload / runtime.BUILD_RECEIPT).exists())

    def test_altered_manifest_identity_is_refused(self):
        payload = self.fresh()
        original = self.manifest(payload)
        changes = {"protocol": 2, "apiVersion": 15500, "rid": "win-arm64", "sandbox": False,
                   "configuration": "Debug", "runtimeVersion": "156.0.8-p3.99", "archiveSha256": "0" * 64,
                   "companion": {**original["companion"], "commit": "b" * 40, "modified": False},
                   "source": {**original["source"], "origin": "https://example.invalid/fake"},
                   "strippedFromUpstream": {"libcef.so": "0" * 64}}
        for key, value in changes.items():
            with self.subTest(field=key):
                self.save(payload / runtime.MANIFEST, {**original, key: value})
                self.refused(payload, "differs from the original")

    def test_altered_receipt_and_matching_manifest_cannot_upgrade_dirty_build(self):
        self.git["modified"] = True
        payload = self.fresh()
        receipt_path = payload / runtime.BUILD_RECEIPT
        receipt = runtime.read_record(receipt_path)
        manifest = self.manifest(payload)
        receipt["identity"]["companion"]["modified"] = False
        manifest["companion"]["modified"] = False
        self.save(receipt_path, receipt)
        self.save(payload / runtime.MANIFEST, manifest)
        self.refused(payload, "receipt is missing from the inventory or changed")

    def test_missing_or_uninventoried_receipt_is_refused(self):
        payload = self.fresh()
        manifest = self.manifest(payload)
        manifest["files"] = [entry for entry in manifest["files"] if entry["path"] != runtime.BUILD_RECEIPT]
        self.save(payload / runtime.MANIFEST, manifest)
        self.refused(payload, "receipt is missing from the inventory")
        (payload / runtime.BUILD_RECEIPT).unlink()
        self.refused(payload, "missing")

    def test_ambiguous_records_are_refused(self):
        payload = self.fresh()
        receipt_path = payload / runtime.BUILD_RECEIPT
        raw = receipt_path.read_text(encoding="utf-8")
        receipt_path.write_text(raw.replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1'), encoding="utf-8")
        self.refused(payload, "duplicate field")
        receipt_path.write_text(raw, encoding="utf-8")
        path = payload / runtime.MANIFEST
        raw = path.read_text(encoding="utf-8")
        path.write_text(raw.replace('"protocol": 3', '"protocol": 3, "protocol": 3'), encoding="utf-8")
        self.refused(payload, "duplicate field")

    def test_incomplete_receipt_and_duplicate_inventory_refused(self):
        payload = self.fresh()
        path = payload / runtime.BUILD_RECEIPT
        receipt = runtime.read_record(path)
        original = copy.deepcopy(receipt)
        del receipt["identity"]["apiVersion"]
        self.save(path, receipt)
        self.refused(payload, "incomplete or ambiguous")
        self.save(path, original)
        manifest = self.manifest(payload)
        manifest["files"].append(next(entry for entry in manifest["files"] if entry["path"] == runtime.BUILD_RECEIPT))
        self.save(payload / runtime.MANIFEST, manifest)
        self.refused(payload, "receipt is missing from the inventory or changed")

    def test_protocol_api_pin_or_rid_mismatch_refused(self):
        payload = self.fresh()
        with mock.patch.object(runtime, "API_VERSION", runtime.API_VERSION + 1):
            self.refused(payload, "incompatible native build")
        self.refused(payload, "incompatible runtime", rid="win-arm64")
        self.pin["distributions"]["win-x64"]["sha256"] = "0" * 64
        self.write_pin()
        self.refused(payload, "does not match the pinned")

    def test_identity_override_is_only_an_equality_assertion(self):
        payload = self.fresh()
        original = self.distribution("win-x64")
        path = self.root / "assert-identity.json"
        for key in ("cefVersion", "rid", "archive", "origin", "upstreamSha1", "sha256"):
            with self.subTest(field=key):
                self.save(path, {**original, key: "fake"})
                self.refused(payload, "does not match the pinned", identity=path)
        self.save(path, original)
        self.refresh(payload, identity=path, revision=self.pin["runtime"]["revision"], configuration="Release", rid="win-x64")
        self.refused(payload, "cannot change", revision=self.pin["runtime"]["revision"] + 1)
        self.refused(payload, "cannot change", configuration="Debug")

    def test_refresh_refuses_changed_file_set(self):
        payload = self.fresh()
        extra = payload / "unexpected.bin"
        extra.write_bytes(b"extra")
        self.refused(payload, "unlisted file")
        extra.unlink()
        (payload / "locales/en-US.pak").unlink()
        self.refused(payload, "differs from its manifest")

    def test_failed_candidate_validation_preserves_original_manifest(self):
        payload = self.fresh()
        original = self.manifest(payload)
        next(entry for entry in original["files"] if entry["path"] == "resources.pak")["sha256"] = "invalid"
        self.save(payload / runtime.MANIFEST, original)
        self.refused(payload, "invalid inventory metadata")

    def test_fresh_build_records_all_rids_and_requires_clean_build(self):
        for rid in runtime.RIDS:
            with self.subTest(rid=rid):
                self.commands.clear()
                payload = self.fresh(rid, generator="Visual Studio 18 2026" if rid.startswith("win-") else "Ninja",
                                     parallel=4, binlog=self.root / "native-{}.binlog")
                manifest = runtime.verify(payload, rid)
                self.assertEqual(self.git["commit"], manifest["companion"]["commit"])
                self.assertEqual(runtime.runtime_version(), manifest["runtimeVersion"])
                if os.name != "nt":
                    for name in (runtime.MANIFEST, runtime.BUILD_RECEIPT):
                        self.assertEqual(0o644, stat.S_IMODE((payload / name).stat().st_mode))
                configure, compile_command, install = self.commands
                self.assertIn(f"-Dapi_version={runtime.API_VERSION}", configure)
                self.assertIn("-DINFINIFRAME_CEF_ENABLE_SANITIZERS=OFF", configure)
                self.assertIn("--clean-first", compile_command)
                self.assertEqual("4", compile_command[compile_command.index("--parallel") + 1])
                self.assertNotIn("InfiniFrame.Cef.TransportTests", compile_command)
                self.assertEqual(str(payload), install[install.index("--prefix") + 1])
                if rid.startswith("win-"):
                    self.assertEqual("ARM64" if rid.endswith("arm64") else "x64", configure[configure.index("-A") + 1])
                    self.assertIn(f"/bl:{self.root / 'native-{}.binlog'}", compile_command)
                else:
                    self.assertNotIn("-A", configure)
                    self.assertNotIn("/v:minimal", compile_command)

    def test_direct_linux_keeps_default_generator_while_runtime_selects_ninja(self):
        args = self.prepare("linux-x64")
        with self.fake_cmake(args), mock.patch.object(runtime.shutil, "which", return_value="/fixture/ninja"):
            runtime.build_native(args)
            self.assertNotIn("-G", self.commands[0])
            self.commands.clear()
            runtime.build(args)
            configure = self.commands[0]
            self.assertEqual("Ninja", configure[configure.index("-G") + 1])

    def test_runtime_build_uses_same_receipt_helper_and_transport_target(self):
        args = self.prepare()
        with self.fake_cmake(args):
            runtime.build(args)
        payload = args.work / "runtime" / runtime.runtime_version() / args.rid
        self.assertEqual(self.git["commit"], runtime.verify(payload)["companion"]["commit"])
        self.assertIn("InfiniFrame.Cef.TransportTests", self.commands[1])
        self.assertIn("--clean-first", self.commands[1])
        runtime.cef_restore.restore.assert_not_called()

    def test_build_native_cli_and_powershell_delegate_actual_build_without_restore(self):
        args = self.prepare(configuration="Debug", parallel=4)
        with self.fake_cmake(args), mock.patch.object(sys, "argv", ["cef_runtime.py", "build-native", "--rid", args.rid,
                "--cef-root", str(args.cef_root), "--build-dir", str(args.build_dir), "--payload", str(args.payload),
                "--configuration", "Debug", "--parallel", "4"]):
            runtime.main()
        self.assertEqual("Debug", runtime.verify(args.payload)["configuration"])
        runtime.cef_restore.restore.assert_not_called()
        script = (REPOSITORY / "scripts/cef-build.ps1").read_text(encoding="utf-8")
        self.assertIn("'build-native'", script)
        self.assertIn("'--cef-root', $CefRoot", script)
        self.assertIn("'--configuration', $Configuration", script)
        self.assertIn("'--generator', $Generator", script)
        self.assertIn("'--binlog'", script)
        self.assertNotIn(" cef_runtime.py manifest", script)
        self.assertNotIn("& cmake", script)

    def test_build_failure_emits_no_receipt(self):
        for step in ("configure", "build", "install"):
            with self.subTest(step=step):
                args = self.prepare(payload=self.root / f"failed-{step}")
                with self.fake_cmake(args, fail_step=step), self.assertRaises(subprocess.CalledProcessError):
                    runtime.build_native(args)
                self.assertFalse((args.payload / runtime.BUILD_RECEIPT).exists())
                self.assertFalse((args.payload / runtime.MANIFEST).exists())

    def test_build_checks_source_protocol_api_and_distribution_stability(self):
        def edit_source(payload):
            (self.source / "native/host.cpp").write_text("// edited during build", encoding="utf-8")

        def edit_distribution(payload):
            self.save(self.root / "cef-win-x64/cef-distribution.json", {**self.distribution("win-x64"), "sha256": "0" * 64})

        mutations = {"source": edit_source, "protocol": lambda payload: self.protocol(2),
                     "commit": lambda payload: self.git.update(commit="2" * 40),
                     "api": lambda payload: setattr(runtime, "API_VERSION", runtime.API_VERSION + 1),
                     "distribution": edit_distribution}
        for name, change in mutations.items():
            with self.subTest(change=name):
                self.protocol(3)
                args = self.prepare(payload=self.root / f"changed-{name}")
                with mock.patch.object(runtime, "API_VERSION", runtime.API_VERSION), self.fake_cmake(args, after_install=change):
                    with self.assertRaises(SystemExit):
                        runtime.build_native(args)
                self.assertFalse((args.payload / runtime.BUILD_RECEIPT).exists())
                self.assertFalse((args.payload / runtime.MANIFEST).exists())

    def test_existing_payload_is_never_adopted_by_build_native(self):
        args = self.prepare()
        args.payload.mkdir()
        sentinel = args.payload / "historical-evidence.txt"
        sentinel.write_bytes(b"preserve me")
        with self.assertRaisesRegex(SystemExit, "already exists"):
            runtime.build_native(args)
        self.assertEqual(b"preserve me", sentinel.read_bytes())
        runtime.subprocess.run.assert_not_called()

    def test_incomplete_install_or_wrong_bootstrap_emits_no_receipt(self):
        mutations = {"missing": lambda payload: (payload / "resources.pak").unlink(),
                     "bootstrap": lambda payload: (payload / "InfiniFrame.Cef.Host.exe").write_bytes(b"wrong"),
                     "unexpected-record": lambda payload: self.save(payload / runtime.BUILD_RECEIPT, {})}
        for name, change in mutations.items():
            with self.subTest(change=name):
                args = self.prepare(payload=self.root / name)
                with self.fake_cmake(args, after_install=change), self.assertRaises(SystemExit):
                    runtime.build_native(args)
                self.assertFalse((args.payload / runtime.MANIFEST).exists())
                if name == "unexpected-record":
                    self.assertEqual({}, runtime.read_record(args.payload / runtime.BUILD_RECEIPT))
                else:
                    self.assertFalse((args.payload / runtime.BUILD_RECEIPT).exists())

    def test_linux_stripping_provenance_survives_refresh(self):
        args = self.prepare("linux-x64", keep_debug=False)
        upstream = {"libcef.so": "a" * 64}
        with self.fake_cmake(args), mock.patch.object(runtime, "strip_debug", return_value=upstream) as strip:
            runtime.build_native(args)
        strip.assert_called_once_with(args.payload)
        self.refresh(args.payload)
        self.assertEqual(upstream, runtime.verify(args.payload)["strippedFromUpstream"])


if __name__ == "__main__":
    unittest.main()
