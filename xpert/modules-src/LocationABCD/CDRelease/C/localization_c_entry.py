"""Independent C entry. Existing A/B selection indices are not changed."""
import json
from pathlib import Path
import sys

_root = str(Path(__file__).resolve().parent)
if _root not in sys.path:
    sys.path.insert(0, _root)
try:
    from owts_pd.localization_c import analyze_project, analyze_localization_c_json, VERSION
except ModuleNotFoundError as exc:
    if exc.name not in ("owts_pd", "owts_pd.localization_c"):
        raise
    from localization_c import analyze_project, analyze_localization_c_json, VERSION


def describe_localization_c_json():
    return json.dumps({"algorithm": "C", "version": VERSION, "input": "JSON object: config + records; optional confirmed calibration", "units": {"sample_rate_hz": "Hz", "velocity_m_per_us": "full speed m/us", "length_m": "m", "indices": "zero-based original samples"}, "score_is_probability": False, "A_B_unchanged": True}, ensure_ascii=False)


def main():
    import argparse
    parser = argparse.ArgumentParser(description="Task17 independent algorithm C; JSON on stdout, progress on stderr")
    parser.add_argument("request", help="UTF-8 JSON request file, or - for stdin")
    args = parser.parse_args()
    try:
        text = sys.stdin.read() if args.request == "-" else Path(args.request).read_text(encoding="utf-8-sig")
        request = json.loads(text)
        result = analyze_project(request, progress=lambda done,total: print(json.dumps({"progress": done, "total": total}), file=sys.stderr, flush=True))
        # Serialization belongs to the checked process boundary too.
        serialized = json.dumps(result, ensure_ascii=False, allow_nan=False)
    except KeyboardInterrupt:
        result = {"status": "cancelled", "algorithm": "C", "version": VERSION}
    except (ValueError, TypeError, KeyError, OSError, OverflowError) as exc:
        result = {"status": "error", "algorithm": "C", "version": VERSION, "error_type": type(exc).__name__, "message": str(exc)}
    print(serialized if result["status"] == "ok" else json.dumps(result, ensure_ascii=False, allow_nan=False))
    return 0 if result["status"] == "ok" else 2


if __name__ == "__main__":
    raise SystemExit(main())
