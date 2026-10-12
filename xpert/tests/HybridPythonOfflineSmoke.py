"""Ten independently seeded OFFLINE native-phase/security verification rounds.

These rounds verify this bridge, not ten whole-GUI or physical-device acceptances.
Uses only the Python standard library and a separately hash-approved native DLL.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import random
import shutil
import subprocess
import sys
import uuid

sys.dont_write_bytecode = True
PROJECT = Path(__file__).resolve().parents[1]
TOOL = PROJECT / "hybrid-src" / "python" / "hvx_offline_verify.py"
spec = importlib.util.spec_from_file_location("hvx_offline_verify_fixed", TOOL)
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


def cs_phase(reference, sample_index):
    """Independent transcription of the existing C# PhaseAt for QA only.

    Root also compiles the actual C# source for cross-language golden verification;
    this helper does not claim independently proven field algorithm accuracy.
    """
    rate, count = reference["sampleRateHz"], reference["sampleCount"]
    if not math.isfinite(sample_index) or sample_index < 0 or sample_index >= count:
        return math.nan
    times = reference["crossingSeconds"]
    t = sample_index / rate
    if t < times[0] - 1e-12 or t > times[-1] + 1e-12:
        return math.nan
    lo, hi = 0, len(times) - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if times[mid] <= t:
            lo = mid
        else:
            hi = mid
    phase = (0 if reference["firstCrossingPositive"] else 180) + 180 * (
        lo + (t - times[lo]) / (times[hi] - times[lo]))
    phase = math.fmod(phase, 360)
    return phase + 360 if phase < 0 else phase


def base_request():
    return {"schema": bridge.REQUEST_SCHEMA, "mode": "cycle", "cycleReference": {
        "schema": "HVX.LocationCycles/1", "sampleRateHz": 1000.0, "sampleCount": 100,
        "captureStartSeconds": 0.1, "firstCrossingPositive": True,
        "crossingSeconds": [-0.00025, 0.05, 0.0995]}, "sampleIndices": [0.0, 1.0, 50.0, 99.0]}


def assert_raises(action, label="invalid input"):
    try:
        action()
    except (bridge.VerificationError, OSError, ValueError):
        return
    raise AssertionError(label + " unexpectedly accepted.")


def assert_phase(actual, expected):
    error = abs(actual - expected)
    # Circular endpoint: two valid results on either side of 0 degrees are equivalent.
    error = min(error, abs(error - 360))
    if error > 2e-9:
        raise AssertionError("Cross-language phase mismatch: %.17g versus %.17g." % (actual, expected))
    return error


def run_round(core, number, oracle_exe, output_root):
    seed = 0x48565800 + number * 0x10001
    rng = random.Random(seed)
    checks = 0
    maximum_error = 0.0
    for trial in range(30):
        rate = 10 ** rng.uniform(2, 9)
        count = rng.randint(250, 10000)
        cycles = rng.randint(1, 100)
        first = -rng.random() / rate
        last = (count - 1 + rng.uniform(0.05, 0.95)) / rate
        points = sorted(rng.uniform(first, last) for _ in range(2 * cycles - 1))
        crossings = [first, *points, last]
        indices = [0.0, float(count - 1), last * rate]
        indices.extend(rng.uniform(0, min(count - 1, last * rate)) for _ in range(100))
        request = base_request()
        ref = request["cycleReference"]
        ref.update(sampleRateHz=rate, sampleCount=count, captureStartSeconds=rng.uniform(0, 100),
                   firstCrossingPositive=bool(trial % 2), crossingSeconds=crossings)
        request["sampleIndices"] = indices
        output = core.evaluate(request)
        if output["hardwareAccessed"] or output["trainingPerformed"]:
            raise AssertionError("Offline scope violated.")
        for index, phase in zip(indices, output["phasesDegrees"]):
            maximum_error = max(maximum_error, assert_phase(phase, cs_phase(ref, index)))
            checks += 1
        if trial == 0:
            oracle_request = output_root / ("oracle-round-%02d-request.json" % number)
            bridge.write_new_json(oracle_request, request)
            actual_cs = subprocess.run([str(oracle_exe), str(oracle_request)], capture_output=True,
                                       text=True, timeout=30)
            if actual_cs.returncode != 0:
                raise AssertionError("Actual unchanged C# oracle failed: " + actual_cs.stderr[:500])
            oracle_result = bridge.decode_json(actual_cs.stdout.encode("utf-8"))
            if (oracle_result.get("schema") != "HVX.ManagedPhaseOracle/1" or
                    len(oracle_result.get("phasesDegrees", [])) != len(indices)):
                raise AssertionError("Actual C# oracle result contract mismatch.")
            for phase, expected in zip(output["phasesDegrees"], oracle_result["phasesDegrees"]):
                maximum_error = max(maximum_error, assert_phase(phase, expected))
                checks += 1
            bridge.write_new_json(output_root / ("oracle-round-%02d-response.json" % number), oracle_result)
        uniform = {"schema": bridge.REQUEST_SCHEMA, "mode": "uniform",
                   "periodSeconds": 10 ** rng.uniform(-6, 2),
                   "initialPhaseDegrees": rng.uniform(-3600, 3600), "timesSeconds": []}
        uniform["timesSeconds"] = [uniform["periodSeconds"] * rng.uniform(0, 2000) for _ in range(30)]
        uniform["timesSeconds"].extend([0.0, uniform["periodSeconds"], 2 * uniform["periodSeconds"]])
        output = core.evaluate(uniform)
        for time, phase in zip(uniform["timesSeconds"], output["phasesDegrees"]):
            expected = math.fmod(uniform["initialPhaseDegrees"] + 360 * time / uniform["periodSeconds"], 360)
            if expected < 0:
                expected += 360
            maximum_error = max(maximum_error, assert_phase(phase, expected))
            checks += 1
    mutations = []
    for field, bad in (("sampleRateHz", 0), ("sampleRateHz", True), ("sampleCount", 15),
                       ("sampleCount", 16_000_001), ("sampleCount", 100.0),
                       ("captureStartSeconds", -1), ("firstCrossingPositive", 1),
                       ("crossingSeconds", [0, 0.05]), ("crossingSeconds", [0, 0, 0.1]),
                       ("crossingSeconds", [-0.002, 0.05, 0.0995]),
                       ("crossingSeconds", [0, 0.05, 0.098]),
                       ("crossingSeconds", [0, 0.05, 0.1001])):
        item = base_request()
        item["cycleReference"][field] = bad
        mutations.append(item)
    for field, bad in (("sampleIndices", []), ("sampleIndices", [True]),
                       ("sampleIndices", [math.nan]), ("sampleIndices", [math.inf]),
                       ("sampleIndices", [-1]), ("sampleIndices", [100]),
                       ("schema", "HVX.Unknown/1"), ("mode", "plugin")):
        item = base_request()
        item[field] = bad
        mutations.append(item)
    # A whole batch must fail without publishing a valid prefix.
    item = base_request()
    item["sampleIndices"] = [0, 50, 100, 99]
    mutations.append(item)
    item = base_request()
    item["cycleReference"]["crossingSeconds"] = [0, 0.05, 0.099]
    item["sampleIndices"] = [99.5]  # Within samples but outside recorded cycle window.
    mutations.append(item)
    item = base_request()
    item["eval"] = "__import__('os').system('echo SHOULD_NOT_RUN')"
    mutations.append(item)
    rng.shuffle(mutations)
    for index, item in enumerate(mutations):
        assert_raises(lambda item=item: core.evaluate(item), "malformed mutation %d" % index)
        checks += 1
    for raw in (b'{"schema":"x","schema":"y"}', b'{"x":NaN}', b'{"x":Infinity}',
                b'{"x":1e999}', b'\xff', b'[' * 1024 + b']' * 1024,
                b' ' * (bridge.MAX_JSON_BYTES + 1)):
        # 1e999 is valid JSON syntax but cannot pass finite-number request validation.
        if raw == b'{"x":1e999}':
            assert_raises(lambda: bridge._number(bridge.decode_json(raw)["x"]))
        else:
            assert_raises(lambda raw=raw: bridge.decode_json(raw))
        checks += 1
    return {"round": number, "seed": seed, "status": "PASS", "checks": checks,
            "maximumCircularPhaseErrorDegrees": maximum_error,
            "actualUnchangedCSharpOracleCompared": True,
            "hardwareAccessed": False, "trainingPerformed": False}


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--manifest-sha256", required=True)
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--oracle-exe", required=True)
    args = parser.parse_args(argv)
    output_root = bridge._absolute_safe_path(args.output_root)
    output_root.mkdir(exist_ok=False)
    checks = []
    try:
        manifest = bridge._absolute_safe_path(args.manifest)
        oracle_exe = bridge._absolute_safe_path(args.oracle_exe)
        if not oracle_exe.is_file():
            raise AssertionError("Actual unchanged C# phase oracle executable is required.")
        # Verify tamper rejection on private QA copies; never alter the approved bundle.
        tamper = output_root / "tamper"
        tamper.mkdir()
        copied_manifest = tamper / manifest.name
        shutil.copyfile(manifest, copied_manifest)
        shutil.copyfile(manifest.parent / "HvxHybridCore.dll", tamper / "HvxHybridCore.dll")
        assert_raises(lambda: bridge.ApprovedNativeCore(copied_manifest, "0" * 64))
        checks.append("independently pinned manifest hash rejects substitution")
        with (tamper / "HvxHybridCore.dll").open("ab") as stream:
            stream.write(b"TAMPER-QA")
        assert_raises(lambda: bridge.ApprovedNativeCore(copied_manifest, args.manifest_sha256))
        checks.append("native DLL hash rejects tamper before load")
        core = bridge.ApprovedNativeCore(manifest, args.manifest_sha256)
        edge = base_request()
        edge["cycleReference"]["crossingSeconds"] = [1e-18, 0.05, 0.1]
        edge["sampleIndices"] = [0.0, 50.0, 99.0]
        edge_request = output_root / "oracle-tolerance-request.json"
        bridge.write_new_json(edge_request, edge)
        edge_native = core.evaluate(edge)
        edge_cs = subprocess.run([str(oracle_exe), str(edge_request)], capture_output=True, text=True, timeout=30)
        if edge_cs.returncode != 0:
            raise AssertionError("Actual C# tolerance oracle rejected its valid edge.")
        edge_actual = bridge.decode_json(edge_cs.stdout.encode("utf-8"))
        if edge_native["phasesDegrees"] != edge_actual["phasesDegrees"]:
            raise AssertionError("C# tolerance/360-rounding endpoint parity failed.")
        bridge.write_new_json(output_root / "oracle-tolerance-response.json", edge_actual)
        checks.append("actual C# last-crossing tolerance and 360-degree rounding endpoint preserved")
        # Windows pins exclude write/delete while DLL remains callable.
        if os.name == "nt":
            assert_raises(lambda: (manifest.parent / "HvxHybridCore.dll").open("ab"))
            checks.append("loaded native DLL cannot be changed through ordinary file write")
        for path in ("relative.json", str(output_root / "x.json:stream"),
                     str(output_root / "NUL"), "\\\\server\\share\\x.json"):
            assert_raises(lambda path=path: bridge._absolute_safe_path(path))
            checks.append("unsafe path rejected")
        sentinel = output_root / "create-new.json"
        bridge.write_new_json(sentinel, {"sentinel": "preserved"})
        before = sentinel.read_bytes()
        assert_raises(lambda: bridge.write_new_json(sentinel, {"sentinel": "overwritten"}))
        if sentinel.read_bytes() != before:
            raise AssertionError("Create-new output overwritten.")
        checks.append("create-new output preserves pre-existing file")
        results = []
        for number in range(1, 11):
            result = run_round(core, number, oracle_exe, output_root)
            results.append(result)
            bridge.write_new_json(output_root / ("round-%02d.json" % number), result)
            print(json.dumps(result), flush=True)
        # Real executable CLI, identical request/results/hashes; zero device access.
        request_path, response_path = output_root / "cli-request.json", output_root / "cli-response.json"
        bridge.write_new_json(request_path, base_request())
        command = [sys.executable, "-I", "-B", str(TOOL), "--manifest", str(manifest),
                   "--manifest-sha256", args.manifest_sha256, "--input", str(request_path),
                   "--output", str(response_path)]
        completed = subprocess.run(command, capture_output=True, text=True, timeout=30)
        if completed.returncode != 0 or not response_path.is_file():
            raise AssertionError("Actual isolated CLI failed: " + completed.stderr[:500])
        response = bridge.decode_json(response_path.read_bytes())
        if response["status"] != "PASS" or response["hardwareAccessed"] or response["trainingPerformed"]:
            raise AssertionError("CLI response contract failed.")
        if response["requestSha256"] != hashlib.sha256(request_path.read_bytes()).hexdigest():
            raise AssertionError("CLI request identity hash mismatch.")
        checks.append("actual isolated Python CLI success and exact request SHA")
        before = response_path.read_bytes()
        completed = subprocess.run(command, capture_output=True, text=True, timeout=30)
        if completed.returncode == 0 or response_path.read_bytes() != before:
            raise AssertionError("Actual CLI overwrite was not rejected.")
        checks.append("actual isolated CLI rejects existing output without mutation")
        report = {"schema": "HVX.HybridPythonOfflineVerification/1", "status": "PASS",
            "roundCount": len(results), "rounds": results, "boundaryChecks": checks,
            "checkCount": sum(item["checks"] for item in results) + len(checks),
            "pythonExecutable": sys.executable, "pythonVersion": sys.version,
            "toolSha256": hashlib.sha256(TOOL.read_bytes()).hexdigest(),
            "testSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "manifestSha256": core.manifest_sha256, "nativeLibrarySha256": core.library_sha256,
            "actualCSharpOracleSha256": hashlib.sha256(oracle_exe.read_bytes()).hexdigest(),
            "hardwareAccessed": False, "trainingPerformed": False,
            "wholeApplicationAcceptance": False,
            "limitations": ["These are phase ABI/offline bridge checks, not full application or field acceptance.",
                             "Hashes require a separately trusted release inventory; Python source is not encrypted.",
                             "No proprietary neural model, automatic learning or task17 integration is included."]}
        bridge.write_new_json(output_root / "verification.json", report)
        print(json.dumps({"status": "PASS", "roundCount": 10, "checkCount": report["checkCount"],
                          "hardwareAccessed": False, "outputRoot": str(output_root)}), flush=True)
        return 0
    except Exception as exc:
        report = {"schema": "HVX.HybridPythonOfflineVerification/1", "status": "FAIL",
                  "reason": str(exc), "completedBoundaryChecks": checks,
                  "hardwareAccessed": False, "trainingPerformed": False,
                  "wholeApplicationAcceptance": False}
        bridge.write_new_json(output_root / "verification.json", report)
        print(json.dumps(report), file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
