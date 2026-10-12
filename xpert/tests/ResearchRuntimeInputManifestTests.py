"""Synthetic-only runtime pinning tests. Does not execute any fixture binary."""
from pathlib import Path
import copy
import hashlib
import importlib.util
import json
import shutil
import sys
import unittest
from unittest import mock
import uuid
import zipfile

PROJECT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("runtime_stage", PROJECT / "tools" / "Stage-ResearchRuntime.py")
stage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage)
QA = Path(sys.argv[1]).absolute()
if PROJECT / "build" not in QA.parents or QA.exists():
    raise RuntimeError("Use a fresh QA root under the project build directory")
QA.mkdir(parents=True)
sys.argv = [sys.argv[0]]


class RuntimeInputs(unittest.TestCase):
    def setUp(self):
        self.root = QA / self._testMethodName
        self.base = self.root / "fake-python"
        self.native = self.root / "native"
        self.out = self.root / "build" / "candidate" / "r71"
        self.manifest = self.root / "approved.json"
        self.version = "3.12.14"
        for name in stage.CORE:
            self.put(name, "not executable: " + name)
        for name in ("DLLs/_test.pyd", "Lib/os.py", "Lib/xml/__init__.py", "Lib/test/ignored.py", "Lib/site-packages/numpy/__init__.py", "Lib/site-packages/numpy.libs/lapack.dll", "Lib/site-packages/numpy/tests/ignored.py", "Lib/site-packages/numpy-2.3.5.dist-info/licenses/LICENSE.txt"):
            self.put(name, "synthetic " + name)
        self.put("Lib/site-packages/numpy-2.3.5.dist-info/METADATA", "Name: numpy\nVersion: 2.3.5\n")
        self.native.joinpath("runtime").mkdir(parents=True)
        for name in ("q71", "q72", "q73"):
            (self.native / "runtime" / (name + ".cp312-win_amd64.pyd")).write_bytes(b"synthetic; do not execute")
        self.approved = stage.inventory(self.base, self.version)
        self.approved.update(approvalStatus="approved", reviewedBy="SYNTHETIC FIXTURE ONLY", reviewedUtc="2026-10-08T00:00:00Z")
        self.save()

    def put(self, relative, value):
        path = self.base / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(value, encoding="utf-8")
        return path

    def save(self):
        self.manifest.write_text(json.dumps(self.approved), encoding="utf-8")

    def run_stage(self):
        return stage.stage_runtime(self.base, self.native, self.out, self.manifest, self.root, self.version)

    def rejected(self, pattern):
        with self.assertRaisesRegex((RuntimeError, OSError), pattern):
            self.run_stage()
        self.assertFalse(self.out.exists())
        self.assertFalse((self.out.parent / "research-pins.json").exists())
        self.assertEqual([], list(self.out.parent.glob(".research-pending-*")))

    def test_inventory_is_never_approval(self):
        draft = stage.inventory(self.base, self.version)
        self.assertEqual("pending-review", draft["approvalStatus"])
        self.assertNotIn("reviewedBy", draft)
        self.assertFalse(any("/tests/" in e["source"] or "/test/" in e["source"] for e in draft["files"]))

    def test_missing_manifest(self):
        self.manifest.unlink()
        self.rejected("manifest is missing")

    def test_unapproved_manifest(self):
        self.approved["approvalStatus"] = "pending-review"; self.save()
        self.rejected("not been explicitly approved")

    def test_review_identity_required(self):
        self.approved.pop("reviewedBy"); self.save()
        self.rejected("review identity")

    def test_duplicate_json_field(self):
        self.manifest.write_text('{"schema":"x","schema":"y"}')
        self.rejected("Duplicate manifest field")

    def test_malformed_and_empty_manifest(self):
        for text in ("not json", "null", "[]", "{}"):
            with self.subTest(text=text):
                self.manifest.write_text(text)
                self.rejected("manifest|approved")

    def test_oversized_manifest(self):
        with mock.patch.object(stage, "MAX_MANIFEST_BYTES", 16):
            self.rejected("size budget")

    def test_same_version_changed_bytes(self):
        self.put("python312.dll", "different bytes, same declared version")
        self.rejected("differ.*files")

    def test_python_version_drift(self):
        self.version = "3.12.15"
        self.rejected("differ.*pythonVersion")

    def test_numpy_version_drift(self):
        self.put("Lib/site-packages/numpy-2.3.5.dist-info/METADATA", "Name: numpy\nVersion: 2.3.6\n")
        self.rejected("differ.*numpyVersion")

    def test_new_input_is_not_silently_allowed(self):
        self.put("DLLs/unreviewed.dll", "unexpected dependency")
        self.rejected("differ.*files")

    def test_missing_input(self):
        (self.base / "python312.dll").unlink()
        self.rejected("Missing runtime input")

    def test_ambiguous_numpy_metadata(self):
        self.put("Lib/site-packages/numpy-9.9.dist-info/METADATA", "Version: 9.9\n")
        self.rejected("Exactly one NumPy")

    def test_reordered_or_forged_mapping_rejected(self):
        self.approved["files"][0]["target"] = "../outside"
        self.save(); self.rejected("differ.*files")

    def test_extra_native_module(self):
        (self.native / "runtime" / "unrequested.pyd").write_bytes(b"not executable")
        self.rejected("exactly the three")

    def test_missing_native_module(self):
        (self.native / "runtime" / "q72.cp312-win_amd64.pyd").unlink()
        self.rejected("exactly the three")

    def test_output_escape_is_rejected(self):
        self.out = self.root / "build" / ".." / "outside"
        self.rejected("under this project's build")

    def test_existing_output_not_replaced(self):
        self.out.mkdir(parents=True)
        sentinel = self.out / "retain.txt"; sentinel.write_text("retained")
        with self.assertRaisesRegex(RuntimeError, "fresh"):
            self.run_stage()
        self.assertEqual("retained", sentinel.read_text())

    def test_prior_pin_file_not_replaced(self):
        self.out.parent.mkdir(parents=True)
        pins = self.out.parent / "research-pins.json"; pins.write_text("old")
        with self.assertRaisesRegex(RuntimeError, "existing runtime pin"):
            self.run_stage()
        self.assertEqual("old", pins.read_text()); self.assertFalse(self.out.exists())

    def test_source_changed_during_copy(self):
        original = stage.shutil.copy2
        def copying(source, target, *args, **kwargs):
            result = original(source, target, *args, **kwargs)
            if Path(source).name == "python312.dll":
                Path(source).write_bytes(b"changed after copy")
            return result
        with mock.patch.object(stage.shutil, "copy2", side_effect=copying):
            self.rejected("differ.*files")

    def test_bad_copy_not_hidden_by_restored_source(self):
        original = stage.shutil.copy2
        def copying(source, target, *args, **kwargs):
            result = original(source, target, *args, **kwargs)
            if Path(source).name == "python312.dll":
                Path(target).write_bytes(b"wrong copied payload")
            return result
        with mock.patch.object(stage.shutil, "copy2", side_effect=copying):
            self.rejected("Copied runtime bytes")

    def test_stdlib_archive_checked(self):
        original = stage.zipfile.ZipFile.write
        def writing(archive, source, target, *args, **kwargs):
            if Path(source).name == "os.py":
                archive.writestr(target, b"wrong archive content")
                return None
            return original(archive, source, target, *args, **kwargs)
        with mock.patch.object(stage.zipfile.ZipFile, "write", new=writing):
            self.rejected("Copied runtime bytes")

    def test_manifest_review_changed_during_copy(self):
        original = stage.shutil.copy2
        def copying(source, target, *args, **kwargs):
            result = original(source, target, *args, **kwargs)
            if Path(source).name == "python312.dll":
                self.approved["reviewedBy"] = "different review"; self.save()
            return result
        with mock.patch.object(stage.shutil, "copy2", side_effect=copying):
            self.rejected("manifest changed")

    def test_native_changed_during_copy(self):
        original = stage.shutil.copy2
        def copying(source, target, *args, **kwargs):
            result = original(source, target, *args, **kwargs)
            if Path(source).name == "q71.cp312-win_amd64.pyd":
                Path(source).write_bytes(b"changed native")
            return result
        with mock.patch.object(stage.shutil, "copy2", side_effect=copying):
            self.rejected("Native research module changed")

    def test_partial_io_failure_not_published(self):
        with mock.patch.object(stage.shutil, "copy2", side_effect=OSError("synthetic disk failure")):
            self.rejected("synthetic disk failure")

    def test_success_same_bytes_and_pins_contract(self):
        result = self.run_stage()
        pins = json.loads((self.out.parent / "research-pins.json").read_text())
        self.assertEqual("3.12.14", result["python"])
        self.assertEqual("2.3.5", result["numpy"])
        self.assertEqual(stage._sha(self.manifest), result["inputManifestSha256"])
        self.assertEqual("approved", json.loads(self.manifest.read_text())["approvalStatus"])
        self.assertEqual(set(pins), {p.relative_to(self.out).as_posix() for p in self.out.rglob("*") if p.is_file()})
        for path, expected in pins.items():
            self.assertEqual(expected, stage._sha(self.out / path))
        with zipfile.ZipFile(self.out / "python312.zip") as archive:
            for entry in self.approved["files"]:
                actual = archive.read(entry["target"]) if entry["archive"] else (self.out / entry["target"]).read_bytes()
                self.assertEqual((self.base / entry["source"]).read_bytes(), actual)
        self.assertEqual([], list(self.out.parent.glob(".research-pending-*")))


if __name__ == "__main__":
    inputs = [PROJECT / "tools" / "Stage-ResearchRuntime.py", PROJECT / "tools" / "Build-ResearchRuntime.ps1", Path(__file__).resolve()]
    hashes = {str(path): stage._sha(path) for path in inputs}
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(RuntimeInputs))
    unchanged = all(stage._sha(Path(path)) == expected for path, expected in hashes.items())
    proof = {"schema": "HVX.ResearchRuntime.InputTests/1", "tests": result.testsRun,
             "errors": len(result.errors), "failures": len(result.failures), "sourceUnchanged": unchanged,
             "inputs": hashes, "syntheticOnly": True, "nativeBinariesExecuted": False, "hardwareAccessed": False,
             "productionManifestApproved": False}
    (QA / "verification.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
    print("RUNTIME_INPUT_TESTS=" + str(result.testsRun) + ";PASS=" + str(result.wasSuccessful() and unchanged))
    sys.exit(0 if result.wasSuccessful() and unchanged else 1)
