"""Synthetic frozen reuse tests; fixture 'DLLs' are text and never executed."""
from pathlib import Path
import hashlib
import importlib.util
import json
import shutil
import sys
import unittest
from unittest import mock

PROJECT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("frozen_stage", PROJECT / "tools" / "Stage-ResearchFrozenRuntime.py")
stage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage)
QA = Path(sys.argv[1]).absolute()
if PROJECT / "build" not in QA.parents or QA.exists():
    raise RuntimeError("Fresh QA root under build required")
QA.mkdir(parents=True)
sys.argv = [sys.argv[0]]


class FrozenReuse(unittest.TestCase):
    def setUp(self):
        self.root = QA / self._testMethodName
        self.frozen = self.root / "old-build"
        self.runtime = self.frozen / "modules" / "r71"
        self.runtime.mkdir(parents=True)
        for index in range(436):
            (self.runtime / ("f%03d.dat" % index)).write_text("synthetic old runtime %d" % index)
        for name in stage.NATIVE_NAMES:
            (self.runtime / name).write_text("old algorithm fixture")
        self.pins = {p.name: stage.guard._sha(p) for p in self.runtime.iterdir()}
        (self.frozen / "research-pins.json").write_text(json.dumps(self.pins))
        (self.frozen / "qa").mkdir()
        pins_hash = stage.guard._sha(self.frozen / "research-pins.json")
        self.freeze = {"BuildFiles": [{"Path": "research-pins.json", "SHA256": pins_hash}]}
        (self.frozen / "qa" / "r27-freeze.json").write_text(json.dumps(self.freeze))
        freeze_hash = stage.guard._sha(self.frozen / "qa" / "r27-freeze.json")
        self.manifest = self.root / "reviewed.json"
        self.value = {"schema": "HVX.ResearchRuntime.FrozenInputs/1", "approvalStatus": "reviewed-frozen-reuse",
                      "sourceBuildRoot": str(self.frozen), "freezeSha256": freeze_hash, "pinsSha256": pins_hash,
                      "frozenFileCount": 439, "pythonVersion": "SYNTHETIC", "numpyVersion": "SYNTHETIC",
                      "opensslVersion": "SYNTHETIC", "maintenanceStatus": "fixture only"}
        self.save()
        self.native = self.root / "new-native"
        (self.native / "runtime").mkdir(parents=True)
        for name in stage.NATIVE_NAMES:
            (self.native / "runtime" / name).write_text("new algorithm fixture")
        self.out = self.root / "build" / "candidate" / "r71"
        self.patches = [mock.patch.object(stage, "PROJECT", self.root), mock.patch.object(stage, "FREEZE_SHA", freeze_hash), mock.patch.object(stage, "PINS_SHA", pins_hash)]
        for patch in self.patches:
            patch.start()

    def tearDown(self):
        for patch in reversed(self.patches):
            patch.stop()

    def save(self):
        self.manifest.write_text(json.dumps(self.value))

    def run_stage(self):
        return stage.stage_frozen(self.native, self.out, self.manifest)

    def test_exact_reuse(self):
        receipt = self.run_stage()
        self.assertEqual(436, receipt["thirdPartyFilesReused"])
        self.assertFalse(receipt["currentMachineRuntimeCopied"])
        self.assertFalse(receipt["confidentialityApproved"])
        for name in self.pins:
            source = self.native / "runtime" / name if name in stage.NATIVE_NAMES else self.runtime / name
            self.assertEqual(source.read_bytes(), (self.out / name).read_bytes())

    def test_changed_bytes(self):
        (self.runtime / "f001.dat").write_text("changed")
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_additional_source(self):
        (self.runtime / "extra.dll").write_text("extra")
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_missing_source(self):
        (self.runtime / "f001.dat").unlink()
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_changed_metadata(self):
        (self.frozen / "research-pins.json").write_text("{}")
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_changed_freeze(self):
        (self.frozen / "qa" / "r27-freeze.json").write_text("{}")
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_draft_denied(self):
        self.value["approvalStatus"] = "pending"; self.save()
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_forged_new_manifest_identity(self):
        self.value["pinsSha256"] = "0" * 64; self.save()
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_extra_native(self):
        (self.native / "runtime" / "q74.pyd").write_text("unexpected")
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_missing_native(self):
        (self.native / "runtime" / next(iter(stage.NATIVE_NAMES))).unlink()
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_nested_native(self):
        name = next(iter(stage.NATIVE_NAMES)); (self.native / "runtime" / "sub").mkdir()
        (self.native / "runtime" / name).rename(self.native / "runtime" / "sub" / name)
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_path_traversal(self):
        for path in ("../x", "/x", "C:/x", "a/../x", "a//b", "a\\b", "./a", ""):
            with self.subTest(path=path), self.assertRaises(RuntimeError): stage.safe_relative(path)

    def test_existing_output(self):
        self.out.mkdir(parents=True)
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_output_outside_build(self):
        self.out = self.root / "other" / "r71"
        with self.assertRaises(RuntimeError): self.run_stage()

    def test_existing_pins(self):
        self.out.parent.mkdir(parents=True); (self.out.parent / "research-pins.json").write_text("preserve")
        with self.assertRaises(RuntimeError): self.run_stage()
        self.assertEqual("preserve", (self.out.parent / "research-pins.json").read_text())

    def test_copy_tamper(self):
        original = shutil.copy2
        def tamper(source, target):
            original(source, target)
            if Path(target).name == "f001.dat": Path(target).write_text("tampered transient copy")
        with mock.patch.object(stage.shutil, "copy2", tamper), self.assertRaises(RuntimeError): self.run_stage()
        self.assertFalse(self.out.exists())

    def test_manifest_changes_during_copy(self):
        original = shutil.copy2
        def tamper(source, target):
            original(source, target)
            if Path(target).name == "f001.dat": self.value["maintenanceStatus"] = "changed"; self.save()
        with mock.patch.object(stage.shutil, "copy2", tamper), self.assertRaises(RuntimeError): self.run_stage()

    def test_duplicate_json(self):
        self.manifest.write_text('{"schema":"a","schema":"b"}')
        with self.assertRaises(ValueError): self.run_stage()

    def test_oversize_manifest(self):
        self.manifest.write_text(" " * 32769)
        with self.assertRaises(RuntimeError): self.run_stage()


suite = unittest.defaultTestLoader.loadTestsFromTestCase(FrozenReuse)
result = unittest.TextTestRunner(verbosity=2).run(suite)
evidence = {"schema": "HVX.ResearchRuntime.FrozenReuseTests/1", "tests": result.testsRun,
            "errors": len(result.errors), "failures": len(result.failures), "syntheticOnly": True,
            "runtimeExecuted": False, "modelDecrypted": False, "hardwareAccessed": False,
            "inputs": {str(p): hashlib.sha256(p.read_bytes()).hexdigest().upper() for p in
                       (Path(__file__), PROJECT / "tools" / "Stage-ResearchFrozenRuntime.py", PROJECT / "tools" / "Stage-ResearchRuntime.py")}}
(QA / "verification.json").write_text(json.dumps(evidence, indent=2))
sys.exit(0 if result.wasSuccessful() else 1)
