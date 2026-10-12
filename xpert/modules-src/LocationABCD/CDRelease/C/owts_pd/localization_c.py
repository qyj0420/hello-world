"""Independent Task 17 localizer C. Engineering prototype, NOT Megger code.

No dependency on A/B or vendor event indices. NumPy only. All defaults are our
engineering parameters, not manufacturer thresholds or calibrated probabilities.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass, fields
import hashlib
import json
import math
from pathlib import Path
import re
from typing import Callable

import numpy as np

VERSION = "C-0.3.0"


@dataclass(frozen=True)
class Settings:
    detection_snr: float = 4.5
    direct_snr: float = 7.0
    minimum_shape: float = 0.70
    minimum_score: float = 0.74
    ambiguity_margin: float = 0.08
    maximum_width_ratio: float = 4.5
    minimum_width_ratio: float = 0.45
    maximum_amplitude_ratio: float = 2.0
    calibration_ratio_factor: float = 4.0
    timing_tolerance_samples: float = 6.0
    feature_half_window: int = 48
    boundary_radius: int = 64
    maximum_pulses: int = 128
    maximum_pairs: int = 4096
    cluster_tolerance_m: float = 3.0
    minimum_independent_records: int = 3
    polarity: str = "either"

    @classmethod
    def parse(cls, data):
        data = {} if data is None else data
        if not isinstance(data, dict):
            raise ValueError("settings must be an object")
        allowed = {f.name for f in fields(cls)}
        if set(data) - allowed:
            raise ValueError("Unknown C settings: " + str(sorted(set(data) - allowed)))
        s = cls(**data)
        for f in fields(cls):
            if f.name == "polarity":
                continue
            v = getattr(s, f.name)
            if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) or v <= 0:
                raise ValueError(f"{f.name} must be positive and finite")
        for key in ("feature_half_window", "boundary_radius", "maximum_pulses", "maximum_pairs", "minimum_independent_records"):
            if type(getattr(s, key)) is not int:
                raise ValueError(f"{key} must be an integer")
        if not (4 <= s.feature_half_window <= 256 and 4 <= s.boundary_radius <= 1024):
            raise ValueError("feature window / boundary radius outside supported range")
        if s.maximum_pulses > 2048 or s.maximum_pairs > 100000:
            raise ValueError("Candidate limits exceed safe bounds")
        if s.minimum_shape > 1 or s.minimum_score > 1 or s.ambiguity_margin > 1:
            raise ValueError("Scores must be in (0, 1]")
        if s.minimum_width_ratio > s.maximum_width_ratio:
            raise ValueError("Inverted width-ratio bounds")
        if s.polarity not in ("either", "same", "opposite"):
            raise ValueError("polarity must be either / same / opposite")
        return s


def _positive(value, name):
    if isinstance(value, bool):
        raise ValueError(name + " must be positive and finite")
    v = float(value)
    if not math.isfinite(v) or v <= 0:
        raise ValueError(name + " must be positive and finite")
    return v


def _object(value, name):
    if not isinstance(value, dict):
        raise ValueError(name + " must be an object")
    return value


def _identifier(value, name):
    if not isinstance(value, str) or not value.strip() or value != value.strip():
        raise ValueError(name + " must be a nonempty string without outer whitespace")
    return value


def _validate_config(config):
    _object(config, "config")
    allowed = {"sample_rate_hz", "cable_length_m", "velocity_m_per_us", "segments", "settings", "reference_plane"}
    if set(config) - allowed:
        raise ValueError("Unknown C config keys: " + str(sorted(set(config)-allowed)))
    fs = _positive(config["sample_rate_hz"], "sample_rate_hz")
    if fs > 5e9:
        raise ValueError("sample_rate_hz exceeds 5 GHz")
    if config.get("reference_plane", "unconfirmed") not in ("unconfirmed", "cable_input"):
        raise ValueError("reference_plane must be cable_input or unconfirmed; no automatic lead compensation")
    # Check an explicitly supplied global speed even if segments are present.
    if "velocity_m_per_us" in config and not 50 <= _positive(config["velocity_m_per_us"], "velocity_m_per_us") <= 300:
        raise ValueError("Full propagation speed must be 50..300 m/us")
    return fs


def _cable(config):
    segments = config.get("segments")
    if segments is None:
        segments = [{"length_m": config["cable_length_m"], "velocity_m_per_us": config["velocity_m_per_us"]}]
    if not isinstance(segments, list) or not segments:
        raise ValueError("segments must be a nonempty list")
    result = []
    for seg in segments:
        _object(seg, "segment")
        length = _positive(seg["length_m"], "length_m")
        speed = _positive(seg["velocity_m_per_us"], "velocity_m_per_us")
        if not 50 <= speed <= 300:
            raise ValueError("Full propagation speed must be 50..300 m/us, NOT v/2")
        result.append({"length_m": length, "velocity_m_per_us": speed})
    if "cable_length_m" in config and not math.isclose(sum(s["length_m"] for s in result), _positive(config["cable_length_m"], "cable_length_m"), rel_tol=1e-8, abs_tol=1e-6):
        raise ValueError("Segment sum conflicts with cable_length_m")
    return result


def round_trip_s(segments):
    return 2e-6 * sum(s["length_m"] / s["velocity_m_per_us"] for s in segments)


def position_from_delay(delay_s, segments):
    """Strict piecewise inversion. Never clip impossible delays to 0 / L."""
    total = round_trip_s(segments)
    if not math.isfinite(delay_s) or delay_s < 0 or delay_s > total:
        return None
    remaining = delay_s / 2
    end = sum(s["length_m"] for s in segments)
    for seg in reversed(segments):
        duration = seg["length_m"] / (seg["velocity_m_per_us"] * 1e6)
        if remaining <= duration:
            return float(end - remaining * seg["velocity_m_per_us"] * 1e6)
        remaining -= duration
        end -= seg["length_m"]
    return 0.0  # exact round trip, after floating-point subtraction only


def _wave(values):
    a = np.asarray(values, dtype=np.float64)
    if a.ndim != 1 or a.size < 32 or a.size > 20000000:
        raise ValueError("Waveform must have 32..20000000 one-dimensional samples")
    if not np.isfinite(a).all() or np.max(np.abs(a)) > 1e100:
        raise ValueError("Nonfinite waveform; data are not silently repaired")
    return a


def _read_text(path):
    """Strict LOC/CAL payload reader. No guessed header units or NaN repair."""
    p = Path(path)
    if p.suffix.lower() not in (".loc", ".cal"):
        raise ValueError("C file adapter supports text LOC/CAL only")
    raw = p.read_bytes()
    if re.search(rb"(?im)^BINARY:", raw[:8192]):
        raise ValueError("Binary LOC/CAL requires an explicit external decoder")
    marker = re.search(rb"(?im)^Data:[ \t]*\r?\n", raw[:8192])
    if marker:
        payload = raw[marker.end():]
    elif p.suffix.lower() == ".cal":
        lines = raw.splitlines()
        first = next((i for i, line in enumerate(lines) if re.fullmatch(rb"\s*[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\s*", line)), None)
        if first is None:
            raise ValueError("No CAL numeric payload")
        payload = b"\n".join(lines[first:])
    else:
        raise ValueError("No LOC Data marker")
    text = payload.decode("ascii").replace(",", " ")
    # Validate every token; np.fromstring alone can silently truncate bad text.
    number = r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?"
    # Separators are mandatory: a regex that merely removes numbers also
    # accepts '2-3'. Do not delegate correctness to a NumPy-version detail.
    if re.sub(r"(?<!\S)(?:" + number + r")(?!\S)|\s+", "", text):
        raise ValueError("Malformed or nonfinite text waveform payload")
    return _wave(np.fromstring(text, sep=" "))


def _check_cancel(cancel):
    if cancel and cancel():
        raise InterruptedError("C analysis cancelled")


def _noise(a):
    base = float(np.median(a))
    y = a - base
    sigma = float(1.4826 * np.median(np.abs(y)))
    floor = max(float(np.max(np.abs(y))) * 1e-12, np.finfo(float).tiny ** 0.25)
    return y, base, max(sigma, floor)


def _timing(y, left, peak, half_window):
    """EC and AIC are independent diagnostics, not proof of pulse identity."""
    start = max(0, left - half_window)
    stop = min(len(y), peak + 2)
    z = y[start:stop]
    energy = np.cumsum(z * z)
    ec = energy - np.arange(1, len(z) + 1) * energy[-1] / len(z)
    ec_index = start + int(np.argmin(ec))
    floor = max(float(np.max(np.abs(z))) ** 2 * 1e-16, np.finfo(float).tiny)
    aic = [(k * math.log(max(float(np.var(z[:k])), floor)) + (len(z) - k) * math.log(max(float(np.var(z[k:])), floor)), k) for k in range(3, len(z) - 3)]
    aic_index = start + min(aic)[1] if aic else left
    return ec_index, aic_index


def _front(y, peak, sigma, width, radius):
    """Bounded threshold front, with precursor-valley re-arming.

    This is a measurable engineering fiducial, NOT an inferred invisible
    physical first arrival. No coordinate is changed by this diagnostic.
    """
    start = max(0, peak-radius)
    amplitude = abs(float(y[peak]))
    low, high = max(3*sigma, .02*amplitude), max(6*sigma, .05*amplitude)
    hold = max(3, min(12, (width+3)//4))
    result = dict(index=None, status="unresolved", precursor_valley=False,
                  search_start=start, search_end=peak, low_threshold=low,
                  high_threshold=high, quiet_samples=hold)
    if amplitude < high or not np.isfinite(y[start:peak+1]).all():
        return result
    quiet = 0
    for i in range(peak-1, start-1, -1):
        quiet = quiet+1 if abs(y[i]) < high else 0
        if quiet < hold:
            continue
        entry = i+hold
        sign = 1 if y[entry] >= 0 else -1
        onset, valley_index, valley = entry, entry, abs(float(y[entry]))
        while onset > start:
            v = float(y[onset-1])*sign
            if v <= 0 or v < low:
                result.update(index=onset, status="measured_threshold_front")
                return result
            if v > valley+3*sigma:
                result["precursor_valley"] = True
                for j in range(valley_index+1, peak+1):
                    if y[j]*sign >= valley+low:
                        result.update(index=j, status="local_rise_after_valley")
                        return result
                return result
            if v < valley:
                valley, valley_index = v, onset-1
            onset -= 1
        return result
    return result


def _feature(y, peak, sigma, fs, s):
    sign = 1 if y[peak] >= 0 else -1
    left = right = peak
    while left > max(0, peak - s.boundary_radius) and y[left - 1] * sign > 0:
        left -= 1
    while right + 1 < min(len(y), peak + s.boundary_radius + 1) and y[right + 1] * sign > 0:
        right += 1
    half = abs(y[peak]) / 2
    lo = hi = peak
    while lo > left and abs(y[lo - 1]) >= half:
        lo -= 1
    while hi < right and abs(y[hi + 1]) >= half:
        hi += 1
    h = s.feature_half_window
    covered = peak >= h and peak + h < len(y)
    segment = np.zeros(2 * h + 1)
    a, b = max(0, peak - h), min(len(y), peak + h + 1)
    segment[a - peak + h:b - peak + h] = y[a:b]
    # rfft(n < len(segment)) silently discards the tail. half_window=256
    # produces 513 samples; preserve full support and disclose true bin spacing.
    fft_size = max(512, 1 << (len(segment)-1).bit_length())
    power = abs(np.fft.rfft(segment * np.hanning(len(segment)), n=fft_size)) ** 2
    power[0] = 0
    total = float(power.sum())
    power = power / total if total else power
    frequencies = np.fft.rfftfreq(fft_size, 1 / fs)
    cumulative = np.cumsum(power)
    low = float(frequencies[min(np.searchsorted(cumulative, .05), len(frequencies)-1)])
    high = float(frequencies[min(np.searchsorted(cumulative, .95), len(frequencies)-1)])
    bands = [float(v.sum()) for v in np.array_split(power[1:], 4)]
    ec, aic = _timing(y, left, peak, h)
    return {"peak_index": int(peak), "zero_start_index": int(left), "zero_end_index": int(right), "signed_amplitude": float(y[peak]), "snr": float(abs(y[peak]) / sigma), "fwhm_samples": int(hi-lo+1), "bounded_front": _front(y, peak, sigma, hi-lo+1, s.boundary_radius), "ec_index": ec, "aic_index": aic, "frequency_5pct_hz": low, "frequency_95pct_hz": high, "spectral_bands": bands, "window_complete": bool(covered), "boundary_complete": bool(left > 0 and right < len(y)-1 and peak-left < s.boundary_radius and right-peak < s.boundary_radius), "_segment": segment, "_power": power}


def _detect(y, sigma, fs, s, cancel):
    v = np.abs(y)
    indices = np.flatnonzero((v[1:-1] >= v[:-2]) & (v[1:-1] > v[2:]) & (v[1:-1] >= s.detection_snr * sigma)) + 1
    raw_count = len(indices)
    # Candidate caps are explicit; a capped record cannot have accepted results.
    limited = len(indices) > s.maximum_pulses * 4
    if limited:
        indices = indices[np.argsort(v[indices], kind="stable")[-s.maximum_pulses*4:]]
    unique = {}
    for index in indices:
        _check_cancel(cancel)
        f = _feature(y, int(index), sigma, fs, s)
        key = (f["zero_start_index"], f["zero_end_index"])
        if key not in unique or abs(f["signed_amplitude"]) > abs(unique[key]["signed_amplitude"]):
            unique[key] = f
    values = list(unique.values())
    limited |= len(values) > s.maximum_pulses
    values = sorted(values, key=lambda f: -abs(f["signed_amplitude"]))[:s.maximum_pulses]
    counts = dict(raw_peaks_before_cap=int(raw_count), raw_peaks_after_prefilter_cap=int(len(indices)), unique_lobes_before_cap=len(unique), retained_lobes=len(values))
    return sorted(values, key=lambda f: f["peak_index"]), bool(limited), counts


def _shape(a, b):
    grid = np.arange(len(a), dtype=float) - (len(a)-1)/2
    best = (0.0, 1.0)
    for stretch in (0.75, 1.0, 1.5, 2.0, 3.0, 4.0):
        stretched = np.interp(grid / stretch, grid, a, left=0, right=0)
        x, z = stretched-stretched.mean(), b-b.mean()
        den = float(np.linalg.norm(x) * np.linalg.norm(z))
        corr = abs(float(np.dot(x, z) / den)) if den else 0.0
        if corr > best[0]:
            best = (min(corr, 1.0), stretch)
    return best


def _calibration(data, fs, segments, s):
    if data is None:
        return None
    _object(data, "calibration")
    if data.get("confirmed_pair") is not True:
        raise ValueError("CAL requires explicit confirmed_pair=true; C does not guess the far reflection")
    y, _, sigma = _noise(_wave(data["samples"]) if "samples" in data else _read_text(data["path"]))
    direct, reflected = data["direct_index"], data["reflection_index"]
    if type(direct) is not int or type(reflected) is not int or not 0 <= direct < reflected < len(y):
        raise ValueError("Invalid CAL indices")
    t = (reflected-direct)/fs
    expected = round_trip_s(segments)
    if abs(t-expected) > max(2/fs, .05*expected):
        raise ValueError("CAL round trip conflicts with supplied cable model/reference plane")
    a, b = _feature(y, direct, sigma, fs, s), _feature(y, reflected, sigma, fs, s)
    if a["snr"] < s.direct_snr or b["snr"] < s.detection_snr or not a["window_complete"] or not b["window_complete"]:
        raise ValueError("CAL pulse is weak or truncated")
    ratio = abs(b["signed_amplitude"] / a["signed_amplitude"])
    if not 0 < ratio < 1:
        raise ValueError("CAL ratio cannot support the simple attenuation model (requires 0<ratio<1)")
    return {"round_trip_s": t, "amplitude_ratio": ratio, "source": str(data.get("path", data.get("id", "provided_array"))), "waveform_sha256": hashlib.sha256(y.astype("<f8").tobytes()).hexdigest(), "model": "uniform-effective exponential attenuation; not a general multi-section transfer function"}


def _pair(a, b, fs, segments, cal, s, reference_confirmed):
    dt = (b["peak_index"]-a["peak_index"])/fs
    position = position_from_delay(dt, segments)
    shape, stretch = _shape(a["_segment"], b["_segment"])
    den = float(np.linalg.norm(a["_power"]) * np.linalg.norm(b["_power"]))
    spectrum = float(np.dot(a["_power"], b["_power"]) / den) if den else 0.0
    width = b["fwhm_samples"]/a["fwhm_samples"]
    amplitude = abs(b["signed_amplitude"]/a["signed_amplitude"])
    same = a["signed_amplitude"]*b["signed_amplitude"] > 0
    ec_dt = (b["ec_index"]-a["ec_index"])/fs
    aic_dt = (b["aic_index"]-a["aic_index"])/fs
    timing = max(abs(ec_dt-dt), abs(aic_dt-dt))*fs
    fa, fb = a["bounded_front"]["index"], b["bounded_front"]["index"]
    front_dt = (fb-fa)/fs if fa is not None and fb is not None else None
    reasons = []
    for f in (a, b):
        if not f["window_complete"] or not f["boundary_complete"]:
            reasons.append("truncated_or_unresolved_pulse")
    if position is None:
        reasons.append("outside_propagation_model")
    if a["snr"] < s.direct_snr:
        reasons.append("weak_direct")
    if shape < s.minimum_shape:
        reasons.append("low_shape_match")
    if not s.minimum_width_ratio <= width <= s.maximum_width_ratio:
        reasons.append("width_ratio_mismatch")
    if amplitude > s.maximum_amplitude_ratio:
        reasons.append("amplitude_ratio_mismatch")
    if (s.polarity == "same" and not same) or (s.polarity == "opposite" and same):
        reasons.append("polarity_mismatch")
    separation = b["peak_index"]-a["peak_index"]
    blind_samples = 2 * max(a["fwhm_samples"], b["fwhm_samples"])
    if separation <= blind_samples:
        reasons.append("overlap_review")
    if round_trip_s(segments)*fs - separation <= blind_samples:
        reasons.append("endpoint_review")
    if timing > s.timing_tolerance_samples + abs(b["fwhm_samples"]-a["fwhm_samples"]):
        reasons.append("timing_disagreement")
    attenuation = 0.5
    predicted = None
    if cal:
        predicted = cal["amplitude_ratio"] ** (dt/cal["round_trip_s"])
        residual = abs(math.log(amplitude/predicted))
        attenuation = math.exp(-residual)
        if residual > math.log(s.calibration_ratio_factor):
            reasons.append("calibration_attenuation_mismatch")
    width_score = math.exp(-abs(math.log(max(width, 1e-12)/stretch)))
    score = .45*shape + .15*spectrum + .15*width_score + .15*min(1.0, b["snr"]/10) + .10*attenuation
    if score < s.minimum_score:
        reasons.append("low_engineering_score")
    if not reference_confirmed:
        reasons.append("reference_plane_unconfirmed")
    return {"direct_index": a["peak_index"], "reflection_index": b["peak_index"], "delay_s": dt, "timing_basis": "peak", "bounded_front_delay_s": front_dt, "bounded_front_is_diagnostic_only": True, "front_peak_delta_samples": (front_dt-dt)*fs if front_dt is not None else None, "ec_delay_s": ec_dt, "aic_delay_s": aic_dt, "candidate_position_m": position, "position_m": None, "shape_score": shape, "template_stretch": stretch, "spectral_score": min(spectrum, 1.0), "amplitude_ratio": amplitude, "expected_amplitude_ratio": predicted, "width_ratio": width, "same_polarity": bool(same), "engineering_score": float(score), "reasons": sorted(set(reasons)), "status": "review", "score_is_probability": False}


def _mark_role_conflicts(plausible, margin):
    # The original all-pairs scan was O(P^2). Only shared endpoints can
    # conflict. Two strongest references per node suffice (exclude self).
    top = {}
    for p in plausible:
        for node in (p["direct_index"], p["reflection_index"]):
            bucket = top.setdefault(node, [])
            bucket.append(p)
            bucket.sort(key=lambda q: -q["engineering_score"])
            del bucket[2:]
    for p in plausible:
        if any(q is not p and q["engineering_score"] >= p["engineering_score"]-margin
               for node in (p["direct_index"], p["reflection_index"])
               for q in top[node]):
            p["reasons"].append("pulse_role_conflict")


def analyze_record(samples, config, *, calibration=None, cancel=None):
    fs = _validate_config(config)
    segments, s = _cable(config), Settings.parse(config.get("settings"))
    confirmed = config.get("reference_plane") == "cable_input"
    a = _wave(samples)
    y, baseline, sigma = _noise(a)
    pulses, limited, count = _detect(y, sigma, fs, s, cancel)
    pairs = []
    horizon = round_trip_s(segments)*fs
    for i, first in enumerate(pulses):
        _check_cancel(cancel)
        for second in pulses[i+1:]:
            if second["peak_index"]-first["peak_index"] > horizon:
                break
            if len(pairs) >= s.maximum_pairs:
                limited = True
                break
            pairs.append(_pair(first, second, fs, segments, calibration, s, confirmed))
        if limited and len(pairs) >= s.maximum_pairs:
            break
    # Reference/endpoint flags do NOT hide physically plausible rivals.
    soft = {"reference_plane_unconfirmed", "endpoint_review", "overlap_review", "timing_disagreement"}
    plausible = [p for p in pairs if not set(p["reasons"])-soft]
    by_direct = {}
    for p in plausible:
        by_direct.setdefault(p["direct_index"], []).append(p)
    for group in by_direct.values():
        group.sort(key=lambda p: (-p["engineering_score"], p["reflection_index"]))
        best = group[0]
        if len(group) > 1 and best["engineering_score"]-group[1]["engineering_score"] < s.ambiguity_margin:
            best["reasons"].append("ambiguous_reflections")
        for p in group[1:]:
            p["reasons"].append("competing_reflection")
    # Check incoming AND outgoing roles across the plausible pair graph, not
    # merely whether a reflection has already been used by a greedy winner.
    _mark_role_conflicts(plausible, s.ambiguity_margin)
    for p in pairs:
        if limited:
            p["reasons"].append("candidate_limit_review")
        p["reasons"] = sorted(set(p["reasons"]))
        if not p["reasons"]:
            p["status"] = "accepted_candidate"
            p["position_m"] = p["candidate_position_m"]
    warnings = ["Experimental C result: not factory output or physically confirmed PD", "Scores are engineering scores, not probabilities"]
    if calibration is None:
        warnings.append("No confirmed calibration attenuation model; attenuation contributes neutral evidence")
    if s.polarity == "either":
        warnings.append("Termination polarity unspecified; both signs allowed and recorded")
    if limited:
        warnings.append("Candidate budget reached: no pair in this record is accepted")
    return {"algorithm": "C", "version": VERSION, "waveform_sha256": hashlib.sha256(a.astype("<f8").tobytes()).hexdigest(), "sample_count": len(a), "baseline": baseline, "noise_sigma": sigma, "raw_peak_count_after_prefilter_cap": count["raw_peaks_after_prefilter_cap"], "detection_counts": count, "candidate_limited": limited, "pulses": [{k:v for k,v in p.items() if not k.startswith("_")} for p in pulses], "pairs": pairs, "accepted_candidate_count": sum(p["status"] == "accepted_candidate" for p in pairs), "warnings": warnings}


def analyze_project(request, *, progress: Callable | None = None, cancel: Callable | None = None):
    """JSON-compatible batch; progress(done,total), cancel()->bool. No writes."""
    _object(request, "request")
    if request.get("algorithm", "C") != "C":
        raise ValueError("Independent entry accepts algorithm C only; never routes A/B")
    config = request["config"]
    fs = _validate_config(config)
    segments, settings = _cable(config), Settings.parse(config.get("settings"))
    cal = _calibration(request.get("calibration"), fs, segments, settings)
    if cal and len(segments) > 1:
        raise ValueError("Scalar attenuation calibration is not supported for mixed segments; omit it or supply a validated future transfer model")
    records = request["records"]
    if not isinstance(records, list) or not records or len(records) > 10000:
        raise ValueError("records must contain 1..10000 entries")
    result, ids = [], set()
    for number, record in enumerate(records, 1):
        _check_cancel(cancel)
        _object(record, "record")
        identity = _identifier(record["record_id"], "record_id")
        if identity in ids:
            raise ValueError("record_id must be nonempty and unique")
        ids.add(identity)
        independence = _identifier(record.get("independence_key", identity), "independence_key")
        phase = record.get("phase", "unknown")
        if phase not in ("A", "B", "C", "L1", "L2", "L3", "unknown"):
            raise ValueError("phase must be A/B/C, L1/L2/L3, or unknown")
        phase = {"A":"L1", "B":"L2", "C":"L3"}.get(phase, phase)
        wave = _wave(record["samples"]) if "samples" in record else _read_text(record["path"])
        r = analyze_record(wave, config, calibration=cal, cancel=cancel)
        r.update(record_id=identity, phase=phase, independence_key=independence)
        voltage = record.get("voltage_kv")
        if voltage is not None:
            if isinstance(voltage, bool):
                raise ValueError("voltage_kv cannot be boolean")
            voltage = float(voltage)
            if not math.isfinite(voltage) or voltage < 0:
                raise ValueError("voltage_kv must be finite and nonnegative")
            r["voltage_kv"] = voltage
            if voltage == 0:
                for pair in r["pairs"]:
                    pair["reasons"] = sorted(set(pair["reasons"] + ["zero_voltage_background"]))
                    pair["status"], pair["position_m"] = "review", None
                r["accepted_candidate_count"] = 0
                r["warnings"].append("0 kV background excluded from PD support")
        result.append(r)
        if progress:
            progress(number, len(records))
    groups = []
    for phase in sorted({r["phase"] for r in result}):
        points = sorted(((p["position_m"], r["record_id"], r["independence_key"], r["waveform_sha256"], p) for r in result if r["phase"] == phase for p in r["pairs"] if p["status"] == "accepted_candidate"), key=lambda item: (item[0], item[1], item[4]["direct_index"]))
        buckets = []
        for point in points:
            if not buckets or point[0]-buckets[-1][0][0] > 2*settings.cluster_tolerance_m:
                buckets.append([])
            buckets[-1].append(point)
        for bucket in buckets:
            # Transitive union, not greedy skipping: A/hashX, B/hashX,
            # B/hashY are one connected acquisition family, not two votes.
            parent = list(range(len(bucket)))
            def find(i):
                while parent[i] != i:
                    parent[i] = parent[parent[i]]; i = parent[i]
                return i
            seen = {}
            for i, point in enumerate(bucket):
                for key in (("acquisition",point[2]), ("wave",point[3])):
                    if key in seen:
                        parent[find(i)] = find(seen[key])
                    else:
                        seen[key] = i
            components = {}
            for i, point in enumerate(bucket):
                components.setdefault(find(i), []).append(point[0])
            positions = [float(np.median(values)) for values in components.values()]
            groups.append({"phase": phase, "position_m": float(np.median(positions)), "span_m": bucket[-1][0]-bucket[0][0], "pair_count": len(bucket), "independent_record_count": len(positions), "record_ids": sorted({p[1] for p in bucket}), "status": "repeat_supported_candidate" if len(positions) >= settings.minimum_independent_records else "insufficient_repeat_support", "physical_truth": False})
    return {"schema_version": 1, "algorithm": "C", "version": VERSION, "status": "ok", "config": {**config, "settings": asdict(settings), "segments": segments}, "calibration": cal, "records": result, "clusters": groups, "summary": {"record_count": len(result), "pair_count": sum(len(r["pairs"]) for r in result), "accepted_candidate_count": sum(r["accepted_candidate_count"] for r in result), "candidate_limited_record_count": sum(r["candidate_limited"] for r in result)}, "limitations": ["No guarantee of unique reflection under overlapping sources", "No A/B fallback or hidden vendor-index seeding", "Statistics never promote an ambiguous pair", "No pC estimation, ML training, dispersion transfer-function inversion or factory grading thresholds"]}


def analyze_localization_c_json(request_json):
    """LabVIEW/C# process boundary: explicit error JSON, never a fake 0 m."""
    try:
        result = analyze_project(json.loads(request_json))
        return json.dumps(result, ensure_ascii=False, allow_nan=False)
    except (ValueError, TypeError, KeyError, OSError, OverflowError) as exc:
        result = {"schema_version": 1, "algorithm": "C", "version": VERSION, "status": "error", "error_type": type(exc).__name__, "message": str(exc), "records": [], "clusters": []}
    return json.dumps(result, ensure_ascii=False, allow_nan=False)
