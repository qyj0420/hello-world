"""Research reconstruction of observed DAC filter arithmetic, never target code.

No claim of sample-exact equivalence to DAC Explorer. NumPy uses float64 rather
than Delphi's intermediate x87 extended precision; source coefficient banks and
the application's waveform/window selection have not been reproduced here.
"""
from __future__ import annotations

from dataclasses import dataclass
import math
import numpy as np
from numpy.typing import ArrayLike, NDArray

FloatArray = NDArray[np.float64]
LIMITATIONS = (
    "Research arithmetic reconstruction; not compared sample-by-sample with DAC.",
    "NumPy float64 differs from Delphi x87 intermediate extended precision.",
    "Finite-input checks are this implementation's safeguards, not recovered DAC behavior.",
)


@dataclass(frozen=True)
class FilterEvidence:
    addresses: tuple[str, ...]
    limitations: tuple[str, ...] = LIMITATIONS


@dataclass
class FftFilterResult:
    output_slots: FloatArray
    mask_slots: NDArray[np.bool_]
    low_cut_slot: int
    high_cut_slot: int
    imaginary_residual_max: float
    evidence: FilterEvidence


@dataclass
class WaveletCoefficients:
    approximation: FloatArray
    details: tuple[FloatArray, ...]  # D1, D2, ...: first/highest-frequency first
    input_lengths: tuple[int, ...]
    lowpass: FloatArray
    coefficient_source: str = "explicit caller-supplied coefficients; DAC bank unverified"


@dataclass
class WaveletFilterResult:
    output: FloatArray
    coefficients: WaveletCoefficients
    approximation_threshold: float
    detail_thresholds: tuple[float, ...]
    evidence: FilterEvidence


@dataclass
class PeakCandidates:
    indices: NDArray[np.int64]
    amplitudes: FloatArray
    over_limit_rejected_indices: NDArray[np.int64]
    evidence: FilterEvidence


@dataclass
class DetectionThresholdResult:
    threshold: float
    effective_threshold: float
    positive_baseline: float
    nonpositive_baseline: float
    positive_count: int
    nonpositive_count: int
    factor: int
    candidates: PeakCandidates
    evidence: FilterEvidence


def _array(values: ArrayLike, name: str = "signal") -> FloatArray:
    arr = np.asarray(values, dtype=np.float64)
    if arr.ndim != 1 or arr.size == 0 or not np.all(np.isfinite(arr)):
        raise ValueError(f"{name} must be a nonempty, finite one-dimensional array")
    return arr.copy()


def _integer(value: int, name: str, minimum: int = 0) -> int:
    if isinstance(value, (bool, np.bool_)) or not isinstance(value, (int, np.integer)):
        raise ValueError(f"{name} must be an integer")
    if int(value) < minimum:
        raise ValueError(f"{name} must be >= {minimum}")
    return int(value)


def fft_window_length(required_samples: int) -> int:
    """0x5fa61c: 2**max(12, ceil(log2(required_samples)))."""
    n = _integer(required_samples, "required_samples", 1)
    return 1 << max(12, (n - 1).bit_length())


def dac_frequency_mask(n_fft: int, position: int) -> NDArray[np.bool_]:
    """Exact observed slot intervals, including unused transform slot zero.

    Original FFT uses slots 1..N. Do not silently replace this mask with a
    conventional symmetric mask over NumPy's zero-based bins.
    Round assumes x87's usual nearest/even rounding mode (not runtime verified).
    """
    n = _integer(n_fft, "n_fft", 2)
    p = _integer(position, "position", 2)
    if n & (n - 1) or p > 20:
        raise ValueError("n_fft must be a power of two and position in [2, 20]")
    a, b = round(n / 1000.0), round(n * p / 250.0)
    mask = np.ones(n + 1, dtype=bool)
    mask[:a + 1] = False
    mask[b:n - b + 1] = False
    mask[n - a:] = False
    return mask


def fft_bandpass_slots(samples: ArrayLike, position: int = 7) -> FftFilterResult:
    """Filter one already-selected N+1-slot window; sample zero is not transformed.

    Application window selection and modulo wrapping are caller responsibilities.
    The recovered routine returns the real component; any imaginary residual from
    its slot mask is exposed instead of being silently labelled zero-phase.
    """
    x = _array(samples)
    n = x.size - 1
    mask = dac_frequency_mask(n, position)
    spectrum = np.zeros(n + 1, dtype=np.complex128)
    spectrum[1:] = np.fft.fft(x[1:], norm="ortho")
    spectrum[~mask] = 0
    inverse = np.fft.ifft(spectrum[1:], norm="ortho")
    output = np.zeros(n + 1)
    output[1:] = inverse.real
    return FftFilterResult(
        output, mask, round(n / 1000.0), round(n * position / 250.0),
        float(np.max(np.abs(inverse.imag))),
        FilterEvidence(("0x5fa37c", "0x5e1ecc"), LIMITATIONS + (
            "N+1-slot API preserves observed 1-based FFT indexing; slot 0 output is initialized to zero.",
            "Round uses nearest/even; original runtime FPU rounding mode remains unverified.",
        )),
    )


def wavelet_threshold(coefficients: ArrayLike, input_length: int, position: int) -> float:
    """0x56c96c: ((15-p)/10) * median(abs(C))/0.6745 * sqrt(2*log10(n)).

    Uses common original input length for every level, including approximation.
    Negative thresholds for p>15 are deliberately NOT clamped.
    """
    c = _array(coefficients, "coefficients")
    n = _integer(input_length, "input_length", 1)
    p = _integer(position, "position", 2)
    if p > 20:
        raise ValueError("position must be in [2, 20]")
    return float((15 - p) / 10.0 * np.median(np.abs(c)) / 0.6745
                 * math.sqrt(2.0 * math.log10(n)))


def hard_threshold(coefficients: ArrayLike, threshold: float) -> FloatArray:
    """0x56cb6c: strict abs(c)<threshold -> zero; equality survives."""
    c = _array(coefficients, "coefficients")
    if not math.isfinite(threshold):
        raise ValueError("threshold must be finite")
    c[np.abs(c) < threshold] = 0.0
    return c


def reference_lowpass(order: int = 1) -> FloatArray:
    """Public reference coefficients, explicitly not the captured DAC heap bank.

    db1/Haar is analytic and requires no optional dependency. Other orders need
    PyWavelets, if the caller has separately installed it; this module installs
    nothing. rec_lo is the coefficient ordering used by this analysis convention.
    """
    order = _integer(order, "order", 1)
    if order > 10:
        raise ValueError("observed DAC bank order range is 1..10")
    if order == 1:
        return np.array([1 / math.sqrt(2), 1 / math.sqrt(2)])
    try:
        import pywt
    except ImportError as exc:
        raise ImportError("Orders 2..10 need explicit lowpass coefficients or optional PyWavelets") from exc
    return np.asarray(pywt.Wavelet(f"db{order}").rec_lo, dtype=np.float64)


def coefficients_for_order(order: int = 1, provider: str = "pywavelets") -> FloatArray:
    """Explicit public coefficient provider; never identifies it as DAC's bank."""
    if provider == "haar":
        if order != 1:
            raise ValueError("analytic Haar provider supports order 1 only")
        return reference_lowpass(1)
    if provider != "pywavelets":
        raise ValueError("provider must be 'pywavelets' or 'haar'")
    order = _integer(order, "order", 1)
    if order > 10:
        raise ValueError("observed DAC bank order range is 1..10")
    try:
        import pywt
    except ImportError as exc:
        raise ImportError("PyWavelets provider requested but PyWavelets is not installed") from exc
    return np.asarray(pywt.Wavelet(f"db{order}").rec_lo, dtype=np.float64)


def _lowpass(values: ArrayLike) -> FloatArray:
    h = _array(values, "lowpass")
    if h.size < 2 or h.size > 20 or h.size % 2:
        raise ValueError("lowpass must have an even length from 2 to 20")
    return h


def wavelet_decompose(signal: ArrayLike, lowpass: ArrayLike, levels: int = 1,
                      coefficient_source: str = "explicit coefficients; DAC bank unverified") -> WaveletCoefficients:
    """Observed 0x56c0a4 analysis, including periodic tail and odd mean append."""
    a, h = _array(signal), _lowpass(lowpass)
    levels = _integer(levels, "levels", 1)
    details, lengths = [], []
    high = h[::-1] * np.where(np.arange(h.size) % 2 == 0, 1.0, -1.0)
    for _ in range(levels):
        lengths.append(a.size)
        if a.size % 2:
            a = np.append(a, (a[-1] + a[0]) / 2.0)
        if a.size < h.size:
            raise ValueError("decomposition input at this level is shorter than the filter")
        indices = (2 * np.arange(a.size // 2)[:, None] + np.arange(h.size)) % a.size
        windows = a[indices]
        details.append(windows @ high)
        a = windows @ h
    return WaveletCoefficients(a, tuple(details), tuple(lengths), h, coefficient_source)


def wavelet_reconstruct(coefficients: WaveletCoefficients) -> FloatArray:
    """0x56c384 synthesis with periodic prefix, then undo each odd append.

    Explicit coefficients must correspond to an orthonormal analysis bank for
    inverse reconstruction. No assertion is made about arbitrary supplied taps.
    """
    a, h = _array(coefficients.approximation), _lowpass(coefficients.lowpass)
    if len(coefficients.details) != len(coefficients.input_lengths):
        raise ValueError("details and input_lengths must have the same length")
    r = h.size // 2
    for detail, length in reversed(tuple(zip(coefficients.details, coefficients.input_lengths))):
        d = _array(detail, "detail")
        length = _integer(length, "input length", 1)
        if a.size != d.size or 2 * a.size not in (length, length + 1) or a.size < r:
            raise ValueError("inconsistent decomposition lengths")
        idx = (np.arange(a.size)[:, None] + np.arange(r) - (r - 1)) % a.size
        aw, dw = a[idx], d[idx]
        y = np.empty(2 * a.size)
        y[::2] = aw @ h[-2::-2] + dw @ h[1::2]
        y[1::2] = aw @ h[-1::-2] - dw @ h[::2]
        a = y[:length]
    return a


def wavelet_denoise(signal: ArrayLike, *, position: int = 7, levels: int = 1,
                    ignore: int = 0, order: int = 1, lowpass: ArrayLike | None = None) -> WaveletFilterResult:
    """Runnable reference bank + recovered threshold/Ignore/boundary arithmetic."""
    x = _array(signal)
    ignore = _integer(ignore, "ignore")
    h = reference_lowpass(order) if lowpass is None else _lowpass(lowpass)
    source = f"public db{order} reference bank; DAC bank unverified" if lowpass is None else "caller-supplied bank"
    dec = wavelet_decompose(x, h, levels, source)
    if ignore > len(dec.details):
        raise ValueError("ignore must not exceed decomposition levels")
    at = wavelet_threshold(dec.approximation, x.size, position)
    dt = tuple(wavelet_threshold(d, x.size, position) for d in dec.details)
    cleaned = WaveletCoefficients(
        hard_threshold(dec.approximation, at),
        tuple(np.zeros_like(d) if j < ignore else hard_threshold(d, dt[j])
              for j, d in enumerate(dec.details)),
        dec.input_lengths, dec.lowpass, dec.coefficient_source,
    )
    return WaveletFilterResult(wavelet_reconstruct(cleaned), cleaned, at, dt,
        FilterEvidence(("0x56c0a4", "0x56c384", "0x56c96c", "0x56cb6c"), LIMITATIONS + (
            "Reference/supplied coefficients have not been compared with DAC's module-external coefficient bank.",
            "Original application window selection, stored parameters and output normalization not reproduced.",
        )))


def local_peak_candidates(signal: ArrayLike, amplitude_limit: float) -> PeakCandidates:
    """0x5edf58: +/-10 absolute maxima, 12-point edge exclusion, 200-point guard.

    Guard excludes candidates if any current/preceding sample (indices clamped
    to at least 1) exceeds Round(0.99*M). It is not a proven hardware saturation
    model; M is the caller's amplitude limit in the same units as the signal.
    """
    x = _array(signal)
    if not math.isfinite(amplitude_limit) or amplitude_limit <= 0:
        raise ValueError("amplitude_limit must be finite and positive")
    candidates, rejected = [], []
    magnitude = np.abs(x)
    bad = magnitude > round(0.99 * amplitude_limit)
    prefix = np.concatenate(([0], np.cumsum(bad, dtype=np.int64)))
    for i in range(12, x.size - 12):
        if prefix[i + 1] - prefix[max(1, i - 199)] > 0:
            rejected.append(i)
        elif magnitude[i] >= np.max(magnitude[i - 10:i + 11]):
            candidates.append(i)
    indices = np.asarray(candidates, dtype=np.int64)
    return PeakCandidates(indices, x[indices], np.asarray(rejected, dtype=np.int64),
                          FilterEvidence(("0x5edf58",)))


def automatic_threshold(signal: ArrayLike, amplitude_limit: float, *, factor: int = 5) -> DetectionThresholdResult:
    """Confirmed auto branch only: threshold=K*1.2*mean(positive accepted peaks).

    No conflation with wavelet coefficient or plotted charge thresholds. Zero
    count gives baseline zero. Nonpositive baseline is reported separately and
    is not substituted for the positive baseline in this recovered branch.
    """
    factor = _integer(factor, "factor")
    peaks = local_peak_candidates(signal, amplitude_limit)
    positive = peaks.amplitudes[peaks.amplitudes > 0]
    nonpositive = peaks.amplitudes[peaks.amplitudes <= 0]
    bp = 1.2 * float(positive.mean()) if positive.size else 0.0
    bn = 1.2 * float(nonpositive.mean()) if nonpositive.size else 0.0
    threshold = factor * bp
    return DetectionThresholdResult(threshold, max(bp, threshold), bp, bn,
        int(positive.size), int(nonpositive.size), factor, peaks,
        FilterEvidence(("0x5ee2e0", "0x5eead0", "0x5ef9fc", "0x610415"), LIMITATIONS + (
            "Only the confirmed positive-peak automatic branch is reconstructed; other entry paths differ.",
            "No waveform unit conversion, manual threshold, multi-file state, or hardware control is performed.",
        )))
