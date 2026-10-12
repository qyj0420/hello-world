"""DAC evidence primitives and a deliberately non-equivalent research scheduler.

Recovered scalar rules are annotated with virtual addresses from the loaded
6.0.0.545 image. The default matching score is an explicit reference substitute,
NOT the recovered DAC FFT score, a Pearson coefficient, or a probability.
No output from this module confirms that two pulses share a physical cause.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
import hashlib
import math
from typing import Any

import numpy as np
from scipy.signal import correlate, find_peaks

from .filters import automatic_threshold


LIMITATIONS = [
    "Research orchestration; not a sample-for-sample reconstruction of DAC Explorer.",
    "reference_template_correlation replaces the unresolved original FFT matching path.",
    "Reference scores are template dot products; ratios are not Pearson coefficients or probabilities.",
    "No original band-pass, wavelet, dual-signal, connector or manual edit path is reproduced here.",
    "Two independent similar discharges may satisfy every implemented candidate gate.",
    "A single passing candidate remains physically unverified; multiple passing candidates require review.",
    "Threshold modes are explicit: manual input, observed positive-peak mean branch, or opt-in research MAD.",
    "Masks after proposals are hypothetical search state; original samples and competing candidates are retained.",
    "Refinement models the single-signal polarity/window search only; the original preceding-200-sample condition is unresolved.",
    "Delphi rounding is modeled as ties-to-even; the original runtime FPU control state was not replayed.",
]

EVIDENCE = {
    "original_peak": "0x005f520c: largest absolute working-waveform amplitude",
    "window": "0x005f5b18 / 0x005f65b8: threshold crossings with 51/151 sample limits",
    "roundtrip": "0x005ee430..0x005ee456: round(2L/(v*1e6*dt))",
    "matching_ratio": "0x005f5a25..0x005f5a36: candidate/main > LowerCorrelationLimit",
    "envelope": "0x005edc28 / 0x005fd238: exponential envelope gates",
    "location": "0x005f5014: L-(n1-n0)*dt*1e6*v/2; -10<x<0 maps to zero",
    "endpoint": "0x005f23d4: near-end original half-height width > configured sample limit",
    "blanking": "0x005f400c: length-based or linear-decay extension of working mask",
    "spectral_product": "0x005ef0b0..0x005ef11a: observed nonstandard complex product",
}


@dataclass(frozen=True)
class ReflectionConfig:
    matching_mode: str = "reference_template_correlation"
    detection_mode: str = "manual"
    detection_threshold: float | None = None
    amplitude_limit: float | None = None
    detection_factor: int | None = None
    research_noise_multiplier: float = 6.0
    lower_correlation_limit: float = 0.1
    envelope_tolerance: int = 16
    joint_approx_factor_percent: float = 1.0
    start_end_detection_limit_samples: int = 28
    endpoint_correction: bool = False
    blanking_mode: str = "linear_decay"
    max_events: int = 10
    stop_on_ambiguity: bool = True


def _positive(value: float, name: str) -> float:
    value = float(value)
    if not math.isfinite(value) or value <= 0:
        raise ValueError(f"{name} must be finite and positive")
    return value


def _waveform(waveform: Any) -> np.ndarray:
    a = np.array(waveform, dtype=np.float64, copy=True)
    if a.ndim != 1 or a.size < 5 or not np.isfinite(a).all():
        raise ValueError("waveform must contain at least five finite 1-D samples")
    return a


def _index(value: int, size: int, name: str) -> int:
    if isinstance(value, (bool, np.bool_)) or int(value) != value:
        raise ValueError(f"{name} must be an integer sample index")
    value = int(value)
    if not 0 <= value < size:
        raise ValueError(f"{name} is outside the waveform")
    return value


def roundtrip_samples(dt_seconds: float, L_m: float, v_m_per_us: float) -> int:
    """Recovered round-trip expression; rounding uses ties to even."""
    dt = _positive(dt_seconds, "dt_seconds")
    length = _positive(L_m, "L_m")
    speed = _positive(v_m_per_us, "v_m_per_us")
    if not 50 <= speed <= 300:
        raise ValueError("v_m_per_us must be within the user-confirmed 50..300 range")
    samples = int(round(2 * length / (speed * 1e6 * dt)))
    if samples < 1:
        raise ValueError("round-trip time is shorter than one rounded sample")
    return samples


def observed_spectral_product(Ar: Any, Ai: Any, Br: Any, Bi: Any) -> tuple[np.ndarray, np.ndarray]:
    """Literal observed arithmetic, not a repaired complex multiply.

    The second imaginary term really loads Ar again in the examined assembly.
    Exposed for auditing only; deliberately not used by the default scheduler.
    """
    ar, ai, br, bi = np.broadcast_arrays(*[np.asarray(x, dtype=float) for x in (Ar, Ai, Br, Bi)])
    if not all(np.isfinite(a).all() for a in (ar, ai, br, bi)):
        raise ValueError("spectral operands must be finite")
    return ar * br - ai * bi, ar * bi + ar * br


def location_from_indices(n0: int, n1: int, dt_seconds: float, L_m: float,
                          v_m_per_us: float, *, opposite_direction: bool = False) -> dict:
    """Recovered arithmetic, exposing the signed physical result before UI correction."""
    roundtrip_samples(dt_seconds, L_m, v_m_per_us)
    if int(n0) != n0 or int(n1) != n1 or n0 < 0 or n1 <= n0:
        raise ValueError("indices must be integers with 0 <= n0 < n1")
    distance = (n1 - n0) * dt_seconds * 1e6 * v_m_per_us / 2
    x_raw = float(min(distance, L_m) if opposite_direction else L_m - distance)
    x_legacy = 0.0 if -10.0 < x_raw < 0.0 else x_raw
    return {
        "n0": int(n0), "n1": int(n1), "delta_t_seconds": float((n1 - n0) * dt_seconds),
        "x_raw": x_raw, "x_legacy_nearzero": x_legacy, "x_corrected": x_legacy,
        "nearzero_applied": x_raw != x_legacy,
        "within_legacy_position_range": bool(-max(0.005 * L_m, 10) <= x_legacy <= L_m),
        "evidence": EVIDENCE["location"],
    }


def half_height_width(waveform: Any, peak_index: int) -> dict:
    """Absolute half-height width with safe bounds (no fractional interpolation)."""
    y = _waveform(waveform)
    n = _index(peak_index, len(y), "peak_index")
    height = 0.5 * abs(float(y[n]))
    if height == 0:
        return {"left": n, "right": n, "width_samples": 0, "edge_truncated": False}
    left = n
    while left > 0:
        left -= 1
        if abs(y[left]) < height:
            break
    right = n
    while right < len(y) - 1:
        right += 1
        if abs(y[right]) < height:
            break
    return {
        "left": left, "right": right, "width_samples": right - left,
        "edge_truncated": bool((left == 0 and abs(y[left]) >= height) or
                               (right == len(y) - 1 and abs(y[right]) >= height)),
    }


def extract_peak_window(waveform: Any, peak_index: int, positive_threshold: float,
                        negative_threshold: float | None = None) -> dict:
    """Threshold crossing window with recovered 51/151 sample scan caps.

    Safe clipping at array edges is a research safeguard. This helper does not
    claim equivalence to all Delphi bounds or the dual-signal branch.
    """
    y = _waveform(waveform)
    n = _index(peak_index, len(y), "peak_index")
    threshold = float(positive_threshold)
    if not math.isfinite(threshold) or threshold < 0:
        raise ValueError("positive_threshold must be finite and nonnegative")
    negative = -threshold if negative_threshold is None else float(negative_threshold)
    if not math.isfinite(negative) or negative > 0:
        raise ValueError("negative_threshold must be finite and nonpositive")
    positive = y[n] > 0
    crossed = (lambda value: value <= threshold) if positive else (lambda value: value >= negative)
    left, right = n, n
    while left > max(0, n - 51):
        left -= 1
        if crossed(y[left]):
            break
    while right < min(len(y) - 1, n + 151):
        right += 1
        if crossed(y[right]):
            break
    return {"left": left, "right": right, "peak": n, "polarity": 1 if positive else -1,
            "evidence": EVIDENCE["window"], "implementation": "safe_single_signal_research_window"}


def decay_parameters(waveform: Any, n0: int, D: int) -> dict:
    """Recover A0, A1 and tau for the observed single raw-signal envelope."""
    y = _waveform(waveform)
    n0 = _index(n0, len(y), "n0")
    if int(D) != D or D < 1:
        raise ValueError("D must be a positive integer")
    lo, hi = max(0, n0 + D - 50), min(len(y) - 1, n0 + D + 50)
    a0 = float(y[n0])
    if lo > hi or a0 == 0:
        return {"evaluable": False, "reason": "roundtrip_reference_unavailable", "a0": a0,
                "tau_samples": None, "reference_window": [int(lo), int(hi)]}
    n_ref = int(lo + np.argmax(np.abs(y[lo:hi + 1])))
    a1 = float(y[n_ref])
    ratio = a1 / a0
    tau, source = 1500, "observed_default"
    if 0 < ratio < 0.5:
        denominator = -math.log(ratio) / D - 1 / (300 + D)
        if denominator == 0:
            return {"evaluable": False, "reason": "singular_decay_fit", "a0": a0,
                    "tau_samples": None, "reference_window": [int(lo), int(hi)]}
        tau = int(round(1 / denominator))
        source = "observed_fitted"
        if tau < 0:
            tau, source = 1500, "observed_negative_fit_fallback"
        if tau == 0:
            return {"evaluable": False, "reason": "zero_decay_time", "a0": a0,
                    "tau_samples": 0, "reference_window": [int(lo), int(hi)]}
    return {"evaluable": True, "a0": a0, "a1": a1, "a1_index": n_ref,
            "a1_over_a0": ratio, "tau_samples": tau, "tau_source": source,
            "reference_window": [int(lo), int(hi)], "evidence": EVIDENCE["envelope"]}


def attenuation_envelope(distance_samples: Any, a0: float, tau_samples: float,
                         q: float, D: int, connector_extension_samples: int = 0) -> np.ndarray:
    """Recovered E_q(d) arithmetic, including its near-roundtrip special case."""
    d = np.asarray(distance_samples, dtype=float)
    tau = _positive(tau_samples, "tau_samples")
    if not np.isfinite(d).all() or np.any(d < 0):
        raise ValueError("distance_samples must be finite and nonnegative")
    if not all(math.isfinite(float(v)) for v in (a0, q)) or D < 1 or connector_extension_samples < 0:
        raise ValueError("invalid envelope parameters")
    value = a0 * np.exp(-d * (1 / (d + 300) + 1 / tau)) - (100 - q) * a0 * 0.01
    special = (d > 0.95 * D) & (d < D + connector_extension_samples) & (q > 99)
    return np.where(special, a0 - (100 - q) * a0 * 0.01, value)


def endpoint_correction(waveform: Any, n0: int, x_legacy: float, L_m: float, *,
                        enabled: bool = False, factor_percent: float = 1,
                        limit_samples: int = 28) -> dict:
    """Recovered near-end original-width test, not a two-pulse width comparison."""
    _positive(L_m, "L_m")
    if factor_percent < 0 or not math.isfinite(float(factor_percent)) or limit_samples < 0:
        raise ValueError("invalid endpoint parameters")
    width = half_height_width(waveform, n0)
    near = bool(x_legacy < factor_percent * L_m / 100)
    flag = bool(near and width["width_samples"] > limit_samples)
    applied = bool(enabled and flag and not width["edge_truncated"])
    return {"x_corrected": float(L_m - x_legacy if applied else x_legacy),
            "near_end_window": near, "endpoint_flag": flag, "applied": applied,
            "half_height": width, "threshold_samples": int(limit_samples),
            "edge_safety_override": bool(enabled and flag and width["edge_truncated"]),
            "evidence": EVIDENCE["endpoint"]}


def blanking_interval(waveform: Any, n0: int, n1: int, original_left: int,
                      reflection_right: int, D: int, *, mode: str = "linear_decay") -> dict:
    """Recovered interval; returns a mask description and never changes samples."""
    y = _waveform(waveform)
    n0, n1 = _index(n0, len(y), "n0"), _index(n1, len(y), "n1")
    if n1 <= n0 or D < 1 or mode not in ("length", "linear_decay"):
        raise ValueError("invalid blanking parameters")
    start = max(0, int(original_left))
    end = max(int(reflection_right), n0 + int(D))
    extrapolated = None
    a0, a1 = abs(float(y[n0])), abs(float(y[n1]))
    if mode == "linear_decay" and a0 != a1:
        slope = (a0 - a1) / (n1 - n0 + 1)
        extrapolated = n0 + int(round(a0 / slope))
        end = max(end, extrapolated)
    raw_end = end
    end = min(len(y) - 1, end)
    if start > end:
        raise ValueError("blanking start is beyond the clipped interval")
    return {"start": start, "end_inclusive": end, "formula_end_unclipped": raw_end,
            "linear_zero_index": extrapolated, "mode": mode,
            "evidence": EVIDENCE["blanking"], "hypothetical_after_proposal": True}


def _local_extremum(y: np.ndarray, n: int) -> bool:
    if n < 6 or n > len(y) - 6:
        return False
    v = y[n]
    return bool((v > y[n - 1] and v > y[n - 2] and v >= y[n + 1] and v >= y[n + 2]) or
                (v < y[n - 1] and v < y[n - 2] and v <= y[n + 1] and v <= y[n + 2]))


def refine_reflection_peak(waveform: Any, n0: int, candidate_index: int, D: int) -> dict:
    """Auditable single-signal refinement approximation, not exact DAC output.

    Observed bounds are candidate-160 .. candidate+100, n0+10 .. n0+D.
    The unresolved preceding-200-sample predicate is NOT invented here.
    """
    y = _waveform(waveform)
    n0 = _index(n0, len(y), "n0")
    n = _index(candidate_index, len(y), "candidate_index")
    if n <= n0 or D < 1:
        raise ValueError("refinement requires n0 < candidate_index and positive D")
    lo, hi = max(0, n0 + 10, n - 160), min(len(y) - 1, n0 + D, n + 100)
    polarity = 1 if y[n0] > 0 else -1
    peaks = []
    for i in range(max(12, lo), min(len(y) - 13, hi) + 1):
        if y[i] * polarity > 0 and abs(y[i]) >= np.max(np.abs(y[i-10:i+11])):
            peaks.append(i)
    refined = max(peaks, key=lambda i: abs(y[i])) if peaks else n
    return {"candidate_index": n, "refined_index": int(refined), "window": [lo, hi],
            "eligible_indices": peaks, "changed": bool(refined != n),
            "full_dac_equivalence": False,
            "unresolved": ["preceding_200_sample_predicate", "filtered_and_dual_signal_paths"],
            "evidence": "0x005f8b64 / 0x005edf58: single-signal refinement bounds and extrema"}


def _validate_config(c: ReflectionConfig) -> None:
    if c.matching_mode != "reference_template_correlation":
        raise ValueError("Only explicit reference_template_correlation is implemented; DAC exact FFT is unresolved")
    if c.blanking_mode not in ("length", "linear_decay"):
        raise ValueError("blanking_mode must be length or linear_decay")
    if not isinstance(c.max_events, int) or c.max_events < 1:
        raise ValueError("max_events must be a positive integer")
    _positive(c.research_noise_multiplier, "research_noise_multiplier")
    if c.detection_mode not in ("manual", "observed_positive_peak_mean", "research_mad"):
        raise ValueError("unknown detection_mode")
    if c.detection_mode == "manual":
        if c.detection_threshold is None:
            raise ValueError("manual detection_mode requires an explicit detection_threshold")
        _positive(c.detection_threshold, "detection_threshold")
    elif c.detection_threshold is not None:
        raise ValueError("detection_threshold is only valid in manual detection_mode")
    if c.detection_mode == "observed_positive_peak_mean":
        if c.amplitude_limit is None or c.detection_factor is None:
            raise ValueError("observed_positive_peak_mean requires amplitude_limit and detection_factor")
        _positive(c.amplitude_limit, "amplitude_limit")
        if isinstance(c.detection_factor, bool) or not isinstance(c.detection_factor, int) or c.detection_factor < 0:
            raise ValueError("detection_factor must be a nonnegative integer")
    if not math.isfinite(c.lower_correlation_limit) or c.lower_correlation_limit < 0:
        raise ValueError("lower_correlation_limit must be finite and nonnegative")
    if not isinstance(c.envelope_tolerance, int) or not 0 <= c.envelope_tolerance <= 100:
        raise ValueError("envelope_tolerance must be an integer in 0..100")
    if c.joint_approx_factor_percent < 0 or not math.isfinite(c.joint_approx_factor_percent):
        raise ValueError("joint_approx_factor_percent must be finite and nonnegative")
    if not isinstance(c.start_end_detection_limit_samples, int) or c.start_end_detection_limit_samples < 0:
        raise ValueError("start_end_detection_limit_samples must be a nonnegative integer")


def analyze_waveform(waveform: Any, dt_seconds: float, L_m: float, v_m_per_us: float,
                     config: ReflectionConfig | None = None) -> dict:
    """Propose and audit pulse pairs using recovered gates and reference scores.

    Output is JSON serializable with ``allow_nan=False``. No candidate is marked
    confirmed. Ambiguity means multiple candidates pass these implemented gates,
    not an assertion about the true number of discharges. All original candidates
    and all gate failures are retained in ``searches``.
    """
    c = config or ReflectionConfig()
    _validate_config(c)
    y = _waveform(waveform)
    D = roundtrip_samples(dt_seconds, L_m, v_m_per_us)
    if c.detection_mode == "manual":
        threshold = float(c.detection_threshold)
        threshold_info = {"value": threshold, "source": "user", "mode": "manual"}
    elif c.detection_mode == "observed_positive_peak_mean":
        recovered = automatic_threshold(y, c.amplitude_limit, factor=c.detection_factor)
        threshold = float(recovered.effective_threshold)
        threshold_info = {
            "value": threshold, "source": "observed_positive_peak_mean",
            "mode": c.detection_mode, "raw_automatic_threshold": float(recovered.threshold),
            "positive_baseline": float(recovered.positive_baseline),
            "nonpositive_baseline": float(recovered.nonpositive_baseline),
            "positive_count": int(recovered.positive_count),
            "nonpositive_count": int(recovered.nonpositive_count),
            "factor": recovered.factor, "amplitude_limit": float(c.amplitude_limit),
            "evidence": list(recovered.evidence.addresses),
            "limitations": list(recovered.evidence.limitations),
        }
    else:
        center = float(np.median(y))
        mad_sigma = float(np.median(np.abs(y - center)) / 0.6745)
        floor = np.finfo(float).eps * max(1.0, float(np.max(np.abs(y)))) * 16
        threshold = float(max(floor, c.research_noise_multiplier * mad_sigma))
        threshold_info = {"value": threshold, "source": "research_MAD_not_recovered_DAC",
                          "mode": "research_mad", "mad_sigma": mad_sigma, "explicit_opt_in": True}
    result: dict = {
        "schema_version": 1, "algorithm": "dac_evidence_reference_research_v1",
        "matching_mode": c.matching_mode, "score_is_original_dac_equivalent": False,
        "physical_identity_confirmed": False, "config": asdict(c),
        "parameters": {"dt_seconds": float(dt_seconds), "L_m": float(L_m),
                       "v_m_per_us": float(v_m_per_us), "roundtrip_samples": D},
        "input": {"samples": len(y), "normalized_float64_sha256":
                  hashlib.sha256(y.astype("<f8", copy=False).tobytes()).hexdigest()},
        "threshold": threshold_info,
        "events": [], "searches": [], "limitations": list(LIMITATIONS), "evidence": dict(EVIDENCE),
    }
    available = np.ones(len(y), dtype=bool)
    for iteration in range(c.max_events):
        eligible_amplitude = np.where(available, np.abs(y), -1.0)
        n0 = int(np.argmax(eligible_amplitude))
        if eligible_amplitude[n0] <= threshold:
            result["stop_reason"] = "no_available_peak_above_threshold"
            break
        window = extract_peak_window(y, n0, threshold)
        template = y[window["left"]:window["right"] + 1]
        energy = float(np.dot(template, template))
        if not math.isfinite(energy):
            raise ValueError("waveform magnitude overflows reference template energy")
        # scipy's correlation lag is converted to the template's original peak.
        full = correlate(y, template, mode="full", method="auto")
        scores = full[window["right"] - n0:window["right"] - n0 + len(y)]
        if not np.isfinite(scores).all():
            raise ValueError("nonfinite reference scores; rescale waveform amplitude")
        sequence_peaks, _ = find_peaks(scores)
        lo, hi = n0 + 1, min(len(y) - 6, n0 + D)
        possible = sequence_peaks[(sequence_peaks >= lo) & (sequence_peaks <= hi)]
        decay = decay_parameters(y, n0, D)
        candidates = []
        for n in possible:
            n = int(n)
            if not _local_extremum(scores, n):
                continue
            ratio = float(scores[n] / energy) if energy > 0 else 0.0
            envelope = {"evaluable": bool(decay["evaluable"]), "lower_abs": None, "upper_abs": None}
            if decay["evaluable"]:
                p = c.envelope_tolerance
                lower = abs(float(attenuation_envelope(n - n0, decay["a0"], decay["tau_samples"], 100-p, D)))
                upper = abs(float(attenuation_envelope(n - n0, decay["a0"], decay["tau_samples"], 100+4*p, D)))
                envelope.update(lower_abs=lower, upper_abs=upper)
                envelope_pass = lower < abs(y[n]) < upper
            else:
                envelope_pass = False
            location = location_from_indices(n0, n, dt_seconds, L_m, v_m_per_us)
            gates = {
                "after_template": bool(n - n0 > max(window["right"] - n0, 10)),
                "within_roundtrip": bool(0 < n - n0 <= D),
                "raw_local_extremum": _local_extremum(y, n),
                "matching_peak_ratio": bool(ratio > c.lower_correlation_limit),
                "attenuation_envelope": bool(envelope_pass),
                "amplitude_decay": bool(abs(y[n]) < abs(y[n0])),
                "same_polarity_reference_guard": bool(y[n] * y[n0] > 0),
                "position_range": location["within_legacy_position_range"],
            }
            rejected = [name for name, passed in gates.items() if not passed]
            candidates.append({"n0": n0, "n1": n, "x_raw": location["x_raw"],
                               "x_corrected": location["x_corrected"], "location": location,
                               "reference_score": float(scores[n]), "reference_main_score": energy,
                               "matching_peak_ratio": ratio, "score_is_original_dac_equivalent": False,
                               "gates": gates, "envelope": envelope,
                               "status": "rejected" if rejected else "passing_unverified",
                               "reason": rejected or ["passes_implemented_gates_not_causal_proof"],
                               "evidence": [EVIDENCE["matching_ratio"], EVIDENCE["envelope"]]})
        passing = [item for item in candidates if item["status"] == "passing_unverified"]
        passing.sort(key=lambda item: item["reference_score"], reverse=True)
        search = {"iteration": iteration, "n0": n0, "window": window, "decay": decay,
                  "candidate_count": len(candidates), "passing_count": len(passing), "candidates": candidates}
        result["searches"].append(search)
        if not passing:
            # Research scheduler: only remove this original pulse window. DAC's
            # failure paths may blank a larger interval; that is not replicated.
            available[window["left"]:window["right"] + 1] = False
            search["research_mask"] = [window["left"], window["right"]]
            search["reason"] = "no_passing_candidate_original_window_only_masked"
            continue
        selected = dict(passing[0])
        endpoint = endpoint_correction(y, n0, selected["location"]["x_legacy_nearzero"], L_m,
                                       enabled=c.endpoint_correction,
                                       factor_percent=c.joint_approx_factor_percent,
                                       limit_samples=c.start_end_detection_limit_samples)
        selected.update(status="ambiguous_review" if len(passing) > 1 else "single_candidate_review",
                        reason=["multiple_candidates_pass" if len(passing) > 1 else "physical_identity_unverified"],
                        x_corrected=endpoint["x_corrected"], endpoint=endpoint,
                        alternatives_n1=[item["n1"] for item in passing[1:]],
                        matching_mode=c.matching_mode, physical_identity_confirmed=False)
        selected["evidence"] = list(selected["evidence"]) + [EVIDENCE["location"], EVIDENCE["endpoint"]]
        refinement = refine_reflection_peak(y, n0, selected["n1"], D)
        selected["refinement_preview"] = refinement
        if refinement["changed"]:
            # Never silently substitute an index whose candidate gates were not
            # evaluated. Preserve both interpretations for a human reviewer.
            selected["status"] = "refinement_disagreement_review"
            selected["reason"].append("refinement_changed_candidate_not_silently_applied")
        result["events"].append(selected)
        if refinement["changed"]:
            result["stop_reason"] = "refinement_disagreement_preserved"
            break
        if len(passing) > 1 and c.stop_on_ambiguity:
            result["stop_reason"] = "ambiguity_preserved_no_automatic_acceptance"
            break
        reflected_window = extract_peak_window(y, selected["n1"], threshold)
        blanking = blanking_interval(y, n0, selected["n1"], window["left"], reflected_window["right"],
                                    D, mode=c.blanking_mode)
        selected["hypothetical_blanking"] = blanking
        available[blanking["start"]:blanking["end_inclusive"] + 1] = False
    else:
        result["stop_reason"] = "research_iteration_limit"
    result["status"] = "review_required" if result["events"] else "no_pair_proposed"
    return result
