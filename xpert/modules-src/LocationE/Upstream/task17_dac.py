"""Four isolated DAC-derived helpers for task 17; no production integration.

Native structure and added safeguards are recorded in every result. This module
does not confirm physical reflection identity or overwrite caller data.
"""
from __future__ import annotations
from dataclasses import asdict, dataclass
import math
import numpy as np
from dac_recovered.filters import automatic_threshold
from dac_recovered.reflection import endpoint_correction, blanking_interval


@dataclass(frozen=True)
class Segment:
    start_m: float
    end_m: float
    velocity_m_per_us: float
    material: str = "caller supplied"


def _segments(segments):
    parts = tuple(segments)
    if not parts:
        raise ValueError('At least one confirmed segment is required')
    end = 0.0
    for part in parts:
        if not all(math.isfinite(v) for v in (part.start_m, part.end_m, part.velocity_m_per_us)):
            raise ValueError('Segment values must be finite')
        if part.start_m != end or part.end_m <= part.start_m or not 50 <= part.velocity_m_per_us <= 300:
            raise ValueError('Segments must be contiguous from 0, positive length, speed 50..300 m/us')
        end = part.end_m
    return parts


def automatic_detection(waveform, *, amplitude_limit, factor=5):
    """Recovered positive-local-peak mean branch, not a confidence score."""
    r = automatic_threshold(waveform, amplitude_limit, factor=factor)
    return {'raw_threshold':r.threshold, 'effective_threshold':r.effective_threshold,
            'positive_baseline':r.positive_baseline, 'nonpositive_baseline':r.nonpositive_baseline,
            'positive_count':r.positive_count, 'nonpositive_count':r.nonpositive_count,
            'factor':r.factor, 'peak_indices':r.candidates.indices.tolist(),
            'over_limit_rejected_indices':r.candidates.over_limit_rejected_indices.tolist(),
            'evidence':asdict(r.evidence), 'state':'zero_positive_baseline_review' if r.positive_count == 0 else 'threshold_calculated',
            'physical_identity_confirmed':False}


def mixed_velocity(segments, *, calibration_velocity=None):
    """0x5e85e8: L / sum(length_i / v_i), not arithmetic mean."""
    parts = _segments(segments)
    time = sum((s.end_m-s.start_m)/s.velocity_m_per_us for s in parts)
    velocity = parts[-1].end_m/time
    result = {'length_m':parts[-1].end_m,'oneway_time_us':time,'calculated_velocity_m_per_us':velocity,
              'evidence':['0x5e85e8..0x5e86ab'], 'input_segments':[asdict(s) for s in parts],
              'lengths_confirmed_by_function':False, 'full_dac_equivalence':False}
    if calibration_velocity is not None:
        if not math.isfinite(calibration_velocity) or not 50 <= calibration_velocity <= 300:
            raise ValueError('calibration_velocity must be 50..300 m/us')
        result.update(calibration_velocity_m_per_us=calibration_velocity,
                      velocity_difference_m_per_us=velocity-calibration_velocity,
                      difference_is_research_diagnostic=True)
    return result


def mixed_location(delta_samples, segments, *, dt_seconds=1e-8, mode='dac_native_10ns'):
    """From far end backwards. Exact boundary is intentionally unresolved in native mode.

    Native branch hardcodes 200: each segment Round(200*l/v); x=end-(d-cumulative)*v/200.
    General mode is a separate physical time-integral model, not native arithmetic.
    Caller must first confirm pair identity, measurement orientation and reference plane.
    """
    parts = _segments(segments)
    if isinstance(delta_samples,bool) or int(delta_samples) != delta_samples or delta_samples < 0:
        raise ValueError('delta_samples must be a nonnegative integer')
    if not math.isfinite(dt_seconds) or dt_seconds <= 0:
        raise ValueError('dt_seconds must be finite and positive')
    if mode not in ('dac_native_10ns','physical_time_integral'):
        raise ValueError('Unknown mixed location mode')
    if mode == 'dac_native_10ns':
        if not math.isclose(dt_seconds,1e-8,rel_tol=0,abs_tol=1e-20):
            raise ValueError('DAC observed branch hardcodes 200, equivalent to 10ns sampling only')
        if any(int(x) != x for s in parts for x in (s.start_m,s.end_m)):
            raise ValueError('DAC observed boundaries are int32 metres; use physical mode for fractional coordinates')
        if parts[-1].end_m > 2147483647/200:
            raise ValueError('Avoid native int32 length-times-200 overflow')
    traversed = 0.0
    trace=[]
    for index in range(len(parts)-1,-1,-1):
        s=parts[index]
        extent=(200*(s.end_m-s.start_m)/s.velocity_m_per_us if mode == 'dac_native_10ns'
                else 2*(s.end_m-s.start_m)/(s.velocity_m_per_us*1e6*dt_seconds))
        window=int(round(extent)) if mode == 'dac_native_10ns' else extent
        upper=traversed+window
        trace.append({'segment':index,'lower_samples':traversed,'upper_samples':upper,
                      'unrounded_extent_samples':extent})
        if delta_samples < upper:
            scale=200.0 if mode == 'dac_native_10ns' else 2/(1e6*dt_seconds)
            x=s.end_m-(delta_samples-traversed)*s.velocity_m_per_us/scale
            # Native handler stores float32; physical model deliberately keeps float64.
            stored=float(np.float32(x)) if mode == 'dac_native_10ns' else float(x)
            return {'state':'conditional_segment_location', 'x_before_store_m':x,'position_m':stored,
                    'segment_index':index, 'segment':asdict(s), 'trace':trace,'mode':mode,
                    'dt_seconds':dt_seconds,'evidence':['0x62f118','0x62f484','0x62f410=200.0','0x62f7a4=200.0'],
                    'physical_identity_confirmed':False,'full_dac_equivalence':False,
                    'limits':['UI supports two caller-configured speed types; this interface generalizes to supplied segments.',
                              'Modeled Round is ties-to-even; x87 runtime state not captured.',
                              'No automatic segment-length inference or connection-cable correction.']}
        traversed=upper
    return {'state':'unresolved_boundary_or_outside_window','position_m':None,'trace':trace,'mode':mode,
            'physical_identity_confirmed':False,'reason':'Native strict < did not select any segment; no stale record reused.'}


def endpoint_analysis(waveform, original_peak, x_before_endpoint_m, length_m, *,
                      enable_mirror=False, near_factor_percent=1.0, width_limit_samples=28):
    r=endpoint_correction(waveform,original_peak,x_before_endpoint_m,length_m,
                          enabled=enable_mirror,factor_percent=near_factor_percent,limit_samples=width_limit_samples)
    return {**r,'x_before_endpoint_m':float(x_before_endpoint_m),
            'state':'endpoint_mirror_proposal' if r['applied'] else 'endpoint_review',
            'physical_identity_confirmed':False,
            'scope':'Observed near-window original half-height rule; no universal near/far classifier.'}


def reflection_suppression(waveform,n0,n1,original_left,reflection_right,D, *,
                           mode='length',apply=False,decision='proposal',protected_windows=()):
    """DAC post-decision blanking, not reflection-order classification.

    Returns copied search array and full interval/conflict audit. protected_windows
    only reports overlaps: no silent narrowing of the observed DAC interval.
    apply requires an explicit accepted/skipped processing decision, not physical truth.
    """
    if decision not in ('proposal','accepted','skipped'):
        raise ValueError('decision must be proposal, accepted or skipped')
    if apply and decision == 'proposal':
        raise ValueError('A proposal must be reviewed before applying its blanking to further search')
    interval=blanking_interval(waveform,n0,n1,original_left,reflection_right,D,mode=mode)
    y=np.array(waveform,dtype=float,copy=True)
    conflicts=[]
    for start,end in protected_windows:
        if not 0 <= start <= end < len(y) or int(start) != start or int(end) != end:
            raise ValueError('Protected windows must be valid inclusive sample intervals')
        overlap=(max(start,interval['start']),min(end,interval['end_inclusive']))
        if overlap[0] <= overlap[1]:
            conflicts.append({'protected_window':[start,end],'overlap':list(overlap)})
    mask=np.zeros(len(y),dtype=bool)
    if apply:
        mask[interval['start']:interval['end_inclusive']+1]=True
        y[mask]=0
    return {'working_waveform':y,'suppression_mask':mask,'interval':interval,
            'applied':bool(apply),'decision':decision,'protected_window_conflicts':conflicts,
            'physical_identity_confirmed':False,
            'scope':'Search-state suppression only; independent discharges inside the interval may also be hidden.'}
