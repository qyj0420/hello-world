"""Reuse exact reviewed frozen third-party bytes; add source-pinned E/E+ modules."""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import importlib.util
import json
import shutil
import sys
import tempfile

PROJECT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("runtime_input_guard", PROJECT / "tools" / "Stage-ResearchRuntime.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
FREEZE_SHA = "79EC1832D17F15D2FB4698AF950D94ED39B94CE7DB939D2FEB8E5AB8703B68CF"
PINS_SHA = "E107F81077FEB010AD9E1D66AA4F6BB5511FA28D9BC3B7144F62CC24448ECDE7"
OLD_NATIVE_NAMES = {"q71.cp312-win_amd64.pyd", "q72.cp312-win_amd64.pyd", "q73.cp312-win_amd64.pyd"}
NATIVE_NAMES = {"q%d.cp312-win_amd64.pyd" % i for i in range(71,87)}


def read_json(path, budget):
    path = guard._no_links(path)
    if not path.is_file() or path.stat().st_size > budget:
        raise RuntimeError("Missing or oversized frozen runtime metadata: " + str(path))
    return json.loads(path.read_text("utf-8-sig"), object_pairs_hook=guard._unique_object)


def safe_relative(value):
    if not isinstance(value, str) or not value or "\\" in value or ":" in value:
        raise RuntimeError("Invalid runtime relative path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(p in ("", ".", "..") for p in value.split("/")):
        raise RuntimeError("Unsafe runtime relative path")
    return path


def verify_frozen(manifest_path):
    manifest_path = guard._no_links(manifest_path)
    manifest = read_json(manifest_path, 32768)
    if manifest.get("schema") != "HVX.ResearchRuntime.FrozenInputs/1" or manifest.get("approvalStatus") != "reviewed-frozen-reuse":
        raise RuntimeError("Frozen runtime input has not been reviewed")
    if manifest.get("freezeSha256") != FREEZE_SHA or manifest.get("pinsSha256") != PINS_SHA or manifest.get("frozenFileCount") != 439:
        raise RuntimeError("Frozen runtime identity differs from the reviewed source")
    configured_root=Path(manifest["sourceBuildRoot"])
    root = guard._no_links(configured_root if configured_root.is_absolute() else PROJECT/configured_root)
    freeze_path, pins_path = root / "qa" / "r27-freeze.json", root / "research-pins.json"
    if guard._sha(guard._no_links(freeze_path)) != FREEZE_SHA or guard._sha(guard._no_links(pins_path)) != PINS_SHA:
        raise RuntimeError("Frozen runtime metadata changed")
    freeze = read_json(freeze_path, 8 * 1024 * 1024)
    matching = [entry for entry in freeze["BuildFiles"] if entry.get("Path") == "research-pins.json"]
    if len(matching) != 1 or matching[0].get("SHA256") != PINS_SHA:
        raise RuntimeError("The freeze does not authenticate the runtime pin file")
    pins = read_json(pins_path, 4 * 1024 * 1024)
    if not isinstance(pins, dict) or len(pins) != manifest["frozenFileCount"]:
        raise RuntimeError("Invalid frozen runtime file count")
    seen = set()
    for name, digest in pins.items():
        safe_relative(name)
        if name.casefold() in seen or not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdefABCDEF" for c in digest):
            raise RuntimeError("Invalid or ambiguous frozen runtime pin")
        seen.add(name.casefold())
    runtime = guard._no_links(root / "modules" / "r71")
    actual = {path.relative_to(runtime).as_posix() for path in guard._walk(runtime)}
    if actual != set(pins):
        raise RuntimeError("Frozen runtime has missing or additional files")
    for name, expected in pins.items():
        if guard._sha(runtime / name) != expected.upper():
            raise RuntimeError("Frozen runtime bytes changed: " + name)
    if not OLD_NATIVE_NAMES.issubset(pins):
        raise RuntimeError("Frozen runtime lacks the expected module slots")
    return manifest, runtime, pins, guard._sha(manifest_path)


def stage_frozen(native, output, manifest_path):
    output = guard._safe_output(output, PROJECT)
    manifest, runtime, pins, manifest_hash = verify_frozen(manifest_path)
    native = guard._no_links(native)
    files = list(guard._walk(native / "runtime"))
    if {p.name for p in files} != NATIVE_NAMES or any(p.parent != native / "runtime" for p in files):
        raise RuntimeError("Current native output must contain exactly q71-q86 rebuilt modules")
    native_pins = {path.name: guard._sha(path) for path in files}
    output_pins = output.parent / "research-pins.json"
    output_receipt = output.parent / "research-runtime-input-receipt.json"
    if output_pins.exists() or output_receipt.exists():
        raise RuntimeError("Refusing to replace existing runtime evidence")
    output.parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix=".research-frozen-pending-", dir=str(output.parent)))
    try:
        for name in sorted(set(pins)|NATIVE_NAMES):
            expected = native_pins[name] if name in NATIVE_NAMES else pins[name]
            source = native / "runtime" / name if name in NATIVE_NAMES else runtime / name
            target = work / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            if guard._sha(target) != (native_pins[name] if name in NATIVE_NAMES else expected.upper()):
                raise RuntimeError("Copied runtime bytes changed: " + name)
        verify_frozen(manifest_path)
        if guard._sha(manifest_path) != manifest_hash or any(guard._sha(native / "runtime" / name) != digest for name, digest in native_pins.items()):
            raise RuntimeError("Inputs changed while staging the runtime")
        result_pins = {path.relative_to(work).as_posix(): guard._sha(path) for path in guard._walk(work)}
        receipt = {"schema": "HVX.ResearchRuntime.FrozenReuse/1", "sourceBuildRoot": str(runtime.parent.parent),
                   "sourceFreezeSha256": FREEZE_SHA, "sourcePinsSha256": PINS_SHA,
                   "inputManifestSha256": manifest_hash, "thirdPartyFilesReused": len(pins) - len(OLD_NATIVE_NAMES),
                   "newNativeModules": native_pins, "sourceFilesVerified": len(pins),
                   "eSourceReceipt":read_json(native/'e-source-receipt.json',65536),
                   "pythonVersion": manifest["pythonVersion"], "numpyVersion": manifest["numpyVersion"],
                   "opensslVersion": manifest["opensslVersion"], "maintenanceStatus": manifest["maintenanceStatus"],
                   "currentMachineRuntimeCopied": False, "hardwareAccessed": False,
                   "confidentialityApproved": False, "dependencySecurityCertification": False}
        (work / "research-pins.json.pending").write_text(json.dumps(result_pins, sort_keys=True), "utf-8")
        (work / "research-runtime-input-receipt.json.pending").write_text(json.dumps(receipt, indent=2), "utf-8")
        work.rename(output)
        (output / "research-pins.json.pending").rename(output_pins)
        (output / "research-runtime-input-receipt.json.pending").rename(output_receipt)
        return receipt
    finally:
        if work.exists():
            guard._no_links(work)
            if work.parent != output.parent or not work.name.startswith(".research-frozen-pending-"):
                raise RuntimeError("Invalid owned staging cleanup directory")
            shutil.rmtree(work)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("native", nargs="?")
    parser.add_argument("output", nargs="?")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()
    if args.check_only:
        _, _, pins, digest = verify_frozen(Path(args.manifest))
        print(json.dumps({"frozenFilesVerified": len(pins), "inputManifestSha256": digest, "runtimeExecuted": False}))
    else:
        if not args.native or not args.output:
            parser.error("Native output and destination required")
        print(json.dumps(stage_frozen(Path(args.native).absolute(), Path(args.output).absolute(), Path(args.manifest))))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print("Frozen runtime staging failed: " + str(error), file=sys.stderr)
        sys.exit(1)
