"""Stage only explicitly reviewed CPython/NumPy inputs; never approve implicitly."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
import zipfile

SCHEMA = "HVX.ResearchRuntime.Inputs/1"
CORE = ("python.exe", "python3.dll", "python312.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE.txt")
MAX_FILES = 50000
MAX_MANIFEST_BYTES = 8 * 1024 * 1024


def _no_links(path):
    path = Path(path).absolute()
    for node in (path, *path.parents):
        if node.is_symlink() or (hasattr(node, "is_junction") and node.is_junction()):
            raise RuntimeError("Runtime input/output cannot use a symbolic link or junction: " + str(node))
    return path.resolve()


def _sha(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _walk(root, excluded_dirs=(), excluded_suffixes=()):
    _no_links(root)
    if not root.is_dir():
        raise RuntimeError("Missing runtime input directory: " + str(root))
    for current, directories, names in os.walk(root, followlinks=False):
        directories[:] = sorted(d for d in directories if d not in excluded_dirs)
        for name in directories:
            _no_links(Path(current) / name)
        for name in sorted(names):
            if name.endswith(excluded_suffixes):
                continue
            path = _no_links(Path(current) / name)
            if not path.is_file():
                raise RuntimeError("Runtime input is not a regular file: " + str(path))
            yield path


def inventory(base, python_version):
    """Return an unapproved inventory. Scanning cannot grant approval."""
    base = _no_links(base)
    entries = []
    targets = set()

    def add(path, target, archive=""):
        path = _no_links(path)
        if not path.is_file():
            raise RuntimeError("Missing runtime input: " + str(path))
        key = (archive + ":" + target).casefold()
        if key in targets:
            raise RuntimeError("Ambiguous runtime destination: " + target)
        targets.add(key)
        entries.append({"source": path.relative_to(base).as_posix(), "target": target,
                        "archive": archive, "sha256": _sha(path), "bytes": path.stat().st_size})
        if len(entries) > MAX_FILES:
            raise RuntimeError("Runtime input file budget exceeded")

    for name in CORE:
        add(base / name, name)
    for path in _walk(base / "DLLs", excluded_suffixes=(".pdb", ".lib", ".exp")):
        add(path, path.relative_to(base).as_posix())
    for path in _walk(base / "Lib", ("site-packages", "test", "tests", "idlelib", "ensurepip", "venv", "tkinter", "__pycache__")):
        if path.suffix == ".py":
            add(path, path.relative_to(base / "Lib").as_posix(), "python312.zip")
    site = base / "Lib" / "site-packages"
    for name in ("numpy", "numpy.libs"):
        for path in _walk(site / name, ("tests", "__pycache__"), (".pyc", ".pdb", ".h", ".pxd")):
            add(path, path.relative_to(site).as_posix())
    metadata_dirs = sorted(site.glob("numpy-*.dist-info"))
    if len(metadata_dirs) != 1:
        raise RuntimeError("Exactly one NumPy metadata directory is required")
    metadata = _no_links(metadata_dirs[0] / "METADATA")
    if not metadata.is_file() or metadata.stat().st_size > 1024 * 1024:
        raise RuntimeError("Missing or oversized NumPy metadata")
    versions = [line[9:].strip() for line in metadata.read_text(encoding="utf-8").splitlines() if line.startswith("Version: ")]
    if len(versions) != 1 or not versions[0]:
        raise RuntimeError("NumPy metadata has no unique version")
    add(metadata, "licenses/numpy-METADATA.txt")
    licenses = metadata_dirs[0] / "licenses"
    for path in _walk(licenses):
        add(path, "licenses/numpy/" + path.relative_to(licenses).as_posix())
    return {"schema": SCHEMA, "approvalStatus": "pending-review", "pythonVersion": python_version,
            "numpyVersion": versions[0], "files": sorted(entries, key=lambda entry: entry["source"])}


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate manifest field: " + key)
        result[key] = value
    return result


def read_approved_manifest(path):
    path = _no_links(path)
    if not path.is_file():
        raise RuntimeError("Approved research runtime input manifest is missing. Review and explicitly approve a fixed input inventory before building: " + str(path))
    if path.stat().st_size > MAX_MANIFEST_BYTES:
        raise RuntimeError("Runtime input manifest exceeds its size budget")
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=_unique_object)
    except (ValueError, UnicodeError) as error:
        raise RuntimeError("Invalid runtime input manifest: " + str(error)) from error
    if not isinstance(value, dict) or value.get("schema") != SCHEMA or value.get("approvalStatus") != "approved":
        raise RuntimeError("Runtime inventory has not been explicitly approved; draft inventories cannot be staged")
    if not isinstance(value.get("files"), list) or not 1 <= len(value["files"]) <= MAX_FILES:
        raise RuntimeError("Runtime input manifest has an invalid file list")
    if not isinstance(value.get("reviewedBy"), str) or not value["reviewedBy"].strip() or not isinstance(value.get("reviewedUtc"), str) or not value["reviewedUtc"].strip():
        raise RuntimeError("Approved runtime manifest requires a review identity and review time")
    return value


def validate_inputs(base, manifest_path, python_version):
    approved = read_approved_manifest(manifest_path)
    observed = inventory(base, python_version)
    for field in ("pythonVersion", "numpyVersion", "files"):
        if approved.get(field) != observed[field]:
            raise RuntimeError("Research runtime inputs differ from the approved manifest (" + field + "). No automatic dependency replacement is permitted")
    return observed


def _safe_output(output, project):
    output = _no_links(output)
    project = _no_links(project)
    try:
        relative = output.relative_to(project / "build")
    except ValueError as error:
        raise RuntimeError("Runtime staging must remain under this project's build directory") from error
    if not relative.parts or output.exists():
        raise RuntimeError("Use a fresh, non-root runtime output directory")
    return output


def stage_runtime(base, native, output, manifest_path, project, python_version):
    output = _safe_output(output, project)
    manifest_path = _no_links(manifest_path)
    manifest_hash = _sha(manifest_path) if manifest_path.is_file() else ""
    observed = validate_inputs(base, manifest_path, python_version)
    base = _no_links(base)
    native = _no_links(native)
    modules = sorted((native / "runtime").glob("*.pyd"))
    expected_native = {"q%d.cp312-win_amd64.pyd" % i for i in range(71,87)}
    if {path.name for path in modules} != expected_native:
        raise RuntimeError("The current native build must contain exactly q71-q86 research modules")
    native_hashes = {path.name: _sha(_no_links(path)) for path in modules}
    pins = output.parent / "research-pins.json"
    if pins.exists():
        raise RuntimeError("Refusing to replace an existing runtime pin file")
    output.parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix=".research-pending-", dir=str(output.parent)))
    try:
        with zipfile.ZipFile(work / "python312.zip", "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for entry in observed["files"]:
                source = base / entry["source"]
                if entry["archive"]:
                    archive.write(source, entry["target"])
                else:
                    target = work / entry["target"]
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target)
        for source in modules:
            shutil.copy2(source, work / source.name)
        (work / "python312._pth").write_text("python312.zip\nDLLs\n.\n", encoding="ascii")
        # Check staged bytes too: transient source changes restored before the
        # second inventory must not introduce different bytes into the payload.
        with zipfile.ZipFile(work / "python312.zip") as archive:
            for entry in observed["files"]:
                actual = hashlib.sha256(archive.read(entry["target"])).hexdigest().upper() if entry["archive"] else _sha(work / entry["target"])
                if actual != entry["sha256"]:
                    raise RuntimeError("Copied runtime bytes differ from the reviewed input: " + entry["source"])
        for name, expected in native_hashes.items():
            if _sha(work / name) != expected or _sha(native / "runtime" / name) != expected:
                raise RuntimeError("Native research module changed while staging: " + name)
        validate_inputs(base, manifest_path, python_version)
        if _sha(manifest_path) != manifest_hash:
            raise RuntimeError("Approved runtime manifest changed while staging")
        runtime_pins = {path.relative_to(work).as_posix(): _sha(path) for path in work.rglob("*") if path.is_file()}
        pin_temp = work / "research-pins.json.pending"
        pin_temp.write_text(json.dumps(runtime_pins, sort_keys=True), encoding="utf-8")
        work.rename(output)
        (output / pin_temp.name).rename(pins)
        return {"files": len(runtime_pins), "python": python_version, "numpy": observed["numpyVersion"],
                "inputManifestSha256": manifest_hash, "algorithm_sources_distributed": False}
    finally:
        if work.exists():
            _no_links(work)
            if work.parent != output.parent or not work.name.startswith(".research-pending-"):
                raise RuntimeError("Invalid owned staging cleanup directory")
            shutil.rmtree(work)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("native", nargs="?")
    parser.add_argument("output", nargs="?")
    parser.add_argument("--manifest")
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument("--inventory-output", help="Write an unapproved review draft only; never changes dependency files")
    args = parser.parse_args(argv)
    project = Path(__file__).resolve().parent.parent
    base = Path(sys.base_prefix)
    version = ".".join(str(part) for part in sys.version_info[:3])
    manifest = Path(args.manifest) if args.manifest else project / "dependencies" / "ResearchRuntime" / "approved-runtime-inputs.json"
    if args.inventory_output:
        if args.check_only or args.native or args.output:
            parser.error("Inventory capture cannot be combined with staging or verification")
        target = _safe_output(Path(args.inventory_output).absolute(), project)
        draft = inventory(base, version)
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("x", encoding="utf-8") as stream:
            json.dump(draft, stream, indent=2, ensure_ascii=False)
        print("UNAPPROVED_RUNTIME_INVENTORY=" + str(target))
    elif args.check_only:
        validate_inputs(base, manifest, version)
        print("RESEARCH_RUNTIME_INPUTS_VERIFIED")
    else:
        if not args.native or not args.output:
            parser.error("Staging requires native and output paths")
        print(json.dumps(stage_runtime(base, Path(args.native).absolute(), Path(args.output).absolute(), manifest, project, version)))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile) as error:
        print("Research runtime staging failed: " + str(error), file=sys.stderr)
        sys.exit(1)
