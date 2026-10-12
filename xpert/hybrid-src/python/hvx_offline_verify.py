"""Bounded, offline orchestration for the approved HVX C++ phase ABI.

This script contains no device control, training, plug-in loading, or model weights.
The manifest SHA must come from an independently trusted release inventory. A hash
stored next to a replaceable DLL is not an authenticity guarantee by itself.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import sys
import uuid

MAX_JSON_BYTES = 4 * 1024 * 1024
MAX_BATCH = 100_000
MAX_DEPTH = 8
REQUEST_SCHEMA = "HVX.HybridOfflinePhase/1"
RESULT_SCHEMA = "HVX.HybridOfflinePhaseResult/1"
MANIFEST_SCHEMA = "HVX.HybridNative/1"
SHA256 = re.compile(r"[0-9a-fA-F]{64}\Z")
# ctypes keeps a loaded DLL callable independently of Python object lifetimes.
# Preserve deny-write/delete pins until this short-lived offline process exits.
_LIVE_APPROVED_CORES = []


class VerificationError(ValueError):
    """A bounded request, trusted-path, or ABI check did not pass."""


def _no_constant(value):
    raise VerificationError("Non-finite JSON constant is prohibited.")


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise VerificationError("Duplicate JSON field is prohibited.")
        result[key] = value
    return result


def _depth(value, depth=0):
    if depth > MAX_DEPTH:
        raise VerificationError("JSON nesting limit exceeded.")
    if isinstance(value, dict):
        for item in value.values():
            _depth(item, depth + 1)
    elif isinstance(value, list):
        for item in value:
            _depth(item, depth + 1)


def decode_json(raw):
    if not isinstance(raw, bytes) or not raw or len(raw) > MAX_JSON_BYTES:
        raise VerificationError("JSON size must be 1..4194304 bytes.")
    try:
        result = json.loads(raw.decode("utf-8-sig"), parse_constant=_no_constant,
                            object_pairs_hook=_unique_object)
    except (UnicodeError, json.JSONDecodeError, RecursionError, ValueError) as exc:
        if isinstance(exc, VerificationError):
            raise
        raise VerificationError("Invalid bounded UTF-8 JSON.") from exc
    _depth(result)
    return result


def _fields(value, expected):
    if not isinstance(value, dict) or set(value) != set(expected):
        raise VerificationError("Missing or unsupported JSON fields.")


def _number(value):
    if type(value) not in (int, float):
        raise VerificationError("A finite JSON number is required, not bool/string.")
    try:
        result = float(value)
    except (OverflowError, ValueError) as exc:
        raise VerificationError("Number cannot be represented as finite binary64.") from exc
    if not math.isfinite(result):
        raise VerificationError("A finite JSON number is required.")
    return result


def _array(value):
    if not isinstance(value, list) or not 1 <= len(value) <= MAX_BATCH:
        raise VerificationError("Batch length must be 1..100000.")
    return [_number(item) for item in value]


def validate_request(value):
    if not isinstance(value, dict) or value.get("schema") != REQUEST_SCHEMA:
        raise VerificationError("Unsupported offline request schema.")
    mode = value.get("mode")
    if mode == "uniform":
        _fields(value, ("schema", "mode", "timesSeconds", "periodSeconds", "initialPhaseDegrees"))
        times = _array(value["timesSeconds"])
        period, initial = _number(value["periodSeconds"]), _number(value["initialPhaseDegrees"])
        if period <= 0 or any(item < 0 for item in times):
            raise VerificationError("Uniform period must be positive and times non-negative.")
        return {"mode": mode, "times": times, "period": period, "initial": initial}
    if mode != "cycle":
        raise VerificationError("Supported modes are uniform and cycle.")
    _fields(value, ("schema", "mode", "cycleReference", "sampleIndices"))
    ref = value["cycleReference"]
    _fields(ref, ("schema", "sampleRateHz", "sampleCount", "captureStartSeconds",
                  "firstCrossingPositive", "crossingSeconds"))
    if ref["schema"] != "HVX.LocationCycles/1":
        raise VerificationError("Unsupported LOC cycle-reference schema.")
    rate = _number(ref["sampleRateHz"])
    count = ref["sampleCount"]
    capture = _number(ref["captureStartSeconds"])
    if rate <= 0 or type(count) is not int or not 16 <= count <= 16_000_000 or capture < 0:
        raise VerificationError("Invalid LOC cycle-reference dimensions.")
    if type(ref["firstCrossingPositive"]) is not bool:
        raise VerificationError("LOC crossing direction must be a JSON bool.")
    source = ref["crossingSeconds"]
    if not isinstance(source, list) or not 3 <= len(source) <= 201 or len(source) % 2 != 1:
        raise VerificationError("LOC crossings require 3..201 odd elements.")
    crossings = [_number(item) for item in source]
    if any(right <= left for left, right in zip(crossings, crossings[1:])):
        raise VerificationError("LOC crossings must be strictly increasing.")
    if (crossings[0] > 1e-12 or crossings[0] < -1 / rate - 1e-12 or
            crossings[-1] < (count - 1) / rate - 1e-12 or
            crossings[-1] >= count / rate + 1e-12):
        raise VerificationError("LOC samples do not cover the complete cycle window.")
    indices = _array(value["sampleIndices"])
    # Do not clamp/pad/replace out-of-reference samples. Native ABI rejects them.
    return {"mode": mode, "indices": indices, "rate": rate, "sampleCount": count,
            "crossings": crossings, "positive": ref["firstCrossingPositive"]}


def _absolute_safe_path(value):
    source = os.fspath(value)
    path = Path(source)
    if not path.is_absolute() or len(source) > 240 or "\x00" in source:
        raise VerificationError("An absolute local path of at most 240 characters is required.")
    if os.name == "nt":
        if source.startswith(("\\\\", "//")) or ":" in source[2:]:
            raise VerificationError("UNC, device paths and alternate data streams are prohibited.")
        if any(part.endswith((" ", ".")) for part in path.parts[1:]):
            raise VerificationError("Ambiguous Windows path component is prohibited.")
        if any(part.upper().split(".")[0] in {"CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$"}
               or re.fullmatch(r"(?:COM|LPT)[1-9]", part.upper().split(".")[0])
               for part in path.parts[1:]):
            raise VerificationError("Reserved Windows device path is prohibited.")
    if ".." in path.parts or "." in path.parts:
        raise VerificationError("Relative traversal component is prohibited.")
    for candidate in (path, *path.parents):
        try:
            info = candidate.lstat()
        except FileNotFoundError:
            if candidate == path:
                continue
            raise VerificationError("Parent directory does not exist.")
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise VerificationError("Reparse/symbolic-link path is prohibited.")
    return path


class PinnedRead:
    """Hold Windows file+ancestor handles: no write/delete or path substitution."""
    def __init__(self, path):
        self.path = _absolute_safe_path(path)
        self.handles = []
        self.stream = None
        try:
            if os.name != "nt":
                # Offline JSON utilities work cross-platform; native ABI is Windows x64.
                self.stream = self.path.open("rb")
                return
            import msvcrt
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            create = kernel.CreateFileW
            create.argtypes = (ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                               ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p)
            create.restype = ctypes.c_void_p
            invalid = ctypes.c_void_p(-1).value
            for parent in reversed(self.path.parents):
                handle = create(str(parent), 0x80, 3, None, 3, 0x02200000, None)
                if handle == invalid:
                    raise VerificationError("Cannot pin a trusted path directory.")
                self.handles.append(handle)
            handle = create(str(self.path), 0x80000000, 1, None, 3, 0x00200080, None)
            if handle == invalid:
                raise VerificationError("Cannot pin the requested file against replacement.")
            try:
                descriptor = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
            except Exception:
                kernel.CloseHandle(ctypes.c_void_p(handle))
                raise
            self.stream = os.fdopen(descriptor, "rb")
            # Re-check while every existing ancestor is pinned against rename.
            _absolute_safe_path(self.path)
        except Exception:
            self.close()
            raise

    def read_bounded(self, maximum):
        if not self.stream or os.fstat(self.stream.fileno()).st_size > maximum:
            raise VerificationError("Pinned input exceeds its size budget.")
        self.stream.seek(0)
        result = self.stream.read(maximum + 1)
        if len(result) > maximum:
            raise VerificationError("Pinned input exceeds its size budget.")
        return result

    def close(self):
        if self.stream is not None:
            self.stream.close()
            self.stream = None
        if os.name == "nt" and self.handles:
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
            for handle in reversed(self.handles):
                kernel.CloseHandle(handle)
        self.handles = []

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


class PinnedOutputParents:
    """Prevent output directory replacement; final leaf remains create-new only."""
    def __init__(self, output):
        self.handles = []
        self.output = _absolute_safe_path(output)
        if os.name == "nt":
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            create = kernel.CreateFileW
            create.argtypes = (ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
                               ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p)
            create.restype = ctypes.c_void_p
            try:
                for parent in reversed(self.output.parents):
                    handle = create(str(parent), 0x80, 3, None, 3, 0x02200000, None)
                    if handle == ctypes.c_void_p(-1).value:
                        raise VerificationError("Cannot pin an output parent directory.")
                    self.handles.append(handle)
                _absolute_safe_path(self.output)
            except Exception:
                self.close()
                raise

    def close(self):
        if os.name == "nt":
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
            for handle in reversed(self.handles):
                kernel.CloseHandle(handle)
        self.handles = []

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


class ApprovedNativeCore:
    def __init__(self, manifest_path, expected_manifest_sha256):
        self.pins = []
        self.library = None
        if os.name != "nt" or ctypes.sizeof(ctypes.c_void_p) != 8:
            raise VerificationError("Approved native core requires Windows x64 Python.")
        if not isinstance(expected_manifest_sha256, str) or not SHA256.fullmatch(expected_manifest_sha256):
            raise VerificationError("An independently pinned manifest SHA-256 is required.")
        try:
            manifest_pin = PinnedRead(manifest_path)
            self.pins.append(manifest_pin)
            raw = manifest_pin.read_bounded(65536)
            self.manifest_sha256 = hashlib.sha256(raw).hexdigest()
            if self.manifest_sha256.lower() != expected_manifest_sha256.lower():
                raise VerificationError("Native manifest SHA-256 mismatch.")
            manifest = decode_json(raw)
            _fields(manifest, ("schema", "abiVersion", "architecture", "libraryFile", "librarySha256"))
            if (manifest["schema"] != MANIFEST_SCHEMA or type(manifest["abiVersion"]) is not int or
                    manifest["abiVersion"] != 1 or manifest["architecture"] != "x64"):
                raise VerificationError("Native manifest schema, ABI or architecture mismatch.")
            filename = manifest["libraryFile"]
            if filename != "HvxHybridCore.dll":
                raise VerificationError("Only the approved HvxHybridCore.dll leaf is supported.")
            digest = manifest["librarySha256"]
            if not isinstance(digest, str) or not SHA256.fullmatch(digest):
                raise VerificationError("Invalid native library SHA-256.")
            dll_pin = PinnedRead(manifest_pin.path.parent / filename)
            self.pins.append(dll_pin)
            contents = dll_pin.read_bounded(64 * 1024 * 1024)
            self.library_sha256 = hashlib.sha256(contents).hexdigest()
            if self.library_sha256.lower() != digest.lower():
                raise VerificationError("Native library SHA-256 mismatch; DLL was not loaded.")
            # Full pinned path; dependency search excludes CWD/PATH and includes System32.
            self.library = ctypes.CDLL(str(dll_pin.path), winmode=0x00000100 | 0x00000800)
            self.library.hx_core_abi_version.argtypes = ()
            self.library.hx_core_abi_version.restype = ctypes.c_uint32
            if self.library.hx_core_abi_version() != 1:
                raise VerificationError("Loaded native ABI does not match the approved contract.")
            pointer = ctypes.POINTER(ctypes.c_double)
            self.library.hx_phase_uniform_batch.argtypes = (pointer, ctypes.c_uint32, ctypes.c_double,
                                                           ctypes.c_double, pointer)
            self.library.hx_phase_uniform_batch.restype = ctypes.c_int
            self.library.hx_phase_cycle_batch.argtypes = (pointer, ctypes.c_uint32, ctypes.c_double,
                ctypes.c_uint32, pointer, ctypes.c_uint32, ctypes.c_int32, pointer)
            self.library.hx_phase_cycle_batch.restype = ctypes.c_int
            _LIVE_APPROVED_CORES.append(self)
        except Exception:
            self.close()
            raise

    def evaluate(self, request):
        checked = validate_request(request)
        values = checked["times"] if checked["mode"] == "uniform" else checked["indices"]
        source = (ctypes.c_double * len(values))(*values)
        # Distinct caller-owned buffers prevent native overlap and uninitialized reads.
        result = (ctypes.c_double * len(values))(*([math.nan] * len(values)))
        if checked["mode"] == "uniform":
            error = self.library.hx_phase_uniform_batch(source, len(values), checked["period"],
                                                        checked["initial"], result)
        else:
            crossings = (ctypes.c_double * len(checked["crossings"]))(*checked["crossings"])
            error = self.library.hx_phase_cycle_batch(source, len(values), checked["rate"],
                checked["sampleCount"], crossings, len(crossings), int(checked["positive"]), result)
        if error:
            if any(not math.isnan(item) for item in result):
                raise VerificationError("Native failure unexpectedly changed output; contract violation.")
            raise VerificationError("Native phase request rejected (error %d); no output fabricated." % error)
        phases = list(result)
        # Match existing C# exactly: adding 360 to an extremely small negative
        # remainder can round to exactly 360 in binary64. Do not silently map it
        # to 0 or reject an otherwise valid legacy result.
        if any(not math.isfinite(item) or not 0 <= item <= 360 for item in phases):
            raise VerificationError("Native output is non-finite or outside [0,360].")
        return {"schema": RESULT_SCHEMA, "status": "PASS", "mode": checked["mode"],
                "phasesDegrees": phases, "nativeAbiVersion": 1,
                "nativeLibrarySha256": self.library_sha256,
                "nativeManifestSha256": self.manifest_sha256,
                "hardwareAccessed": False, "trainingPerformed": False}

    def close(self):
        # Keep immutable DLL/path pins until process exit: ctypes may retain a module
        # handle. Dropping references is not a reliable explicit FreeLibrary contract.
        if self.library is None:
            for pin in reversed(self.pins):
                pin.close()
            self.pins = []


def write_new_json(path, value):
    """Atomically publish a complete new file; never overwrite an existing target."""
    target = _absolute_safe_path(path)
    if target.exists():
        raise VerificationError("Output already exists; overwrite is prohibited.")
    raw = (json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":")) + "\n").encode("utf-8")
    if len(raw) > MAX_JSON_BYTES:
        raise VerificationError("Output exceeds JSON size budget.")
    temporary = target.parent / (".hvx-" + uuid.uuid4().hex + ".tmp")
    with PinnedOutputParents(target):
        descriptor = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_BINARY", 0), 0o600)
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            # A hard-link creates the final name atomically and fails if it already exists.
            # Unlike replace/rename on some systems, it cannot clobber a raced target.
            os.link(temporary, target)
        finally:
            if temporary.exists():
                temporary.unlink()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Offline C++ phase verification. No hardware or training.")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--manifest-sha256", required=True)
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    try:
        source, destination = _absolute_safe_path(args.input), _absolute_safe_path(args.output)
        if source == destination or destination.exists():
            raise VerificationError("Output must be a new file separate from input.")
        with PinnedRead(source) as pin:
            raw = pin.read_bounded(MAX_JSON_BYTES)
            payload = decode_json(raw)
            validate_request(payload)  # Validate before loading any optional native code.
            core = ApprovedNativeCore(args.manifest, args.manifest_sha256)
            result = core.evaluate(payload)
            result["requestSha256"] = hashlib.sha256(raw).hexdigest()
            write_new_json(destination, result)
        print(json.dumps({"status": "PASS", "output": str(destination), "hardwareAccessed": False}))
        return 0
    except (VerificationError, OSError, AttributeError, OverflowError) as exc:
        print(json.dumps({"status": "REJECTED", "reason": str(exc), "hardwareAccessed": False}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
