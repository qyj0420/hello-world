"""A-inspired raw packet boundaries, before E+ pairing (not A binary equivalence).

Ported boundary/period/quiet logic: frozen AB/source/AstraSignal.cs A4Extract,
A4Period, A4QuietBlock; canonical-seed rule: AstraRawSeedWindows.cs.
No geometry/known defect position enters the boundary/peak choice. Round trip
only bounds unresolved duration and search span, exactly as in the A source.
"""
from __future__ import annotations
import math
import numpy as np

VERSION = 'Eplus-A-raw-packet-guard-r01'


def incident_representative(y, packet, sigma, increase_limit=.20):
    """User rule: later dominant < 1.20*first significant peak -> use first.

    Denominator is FIRST amplitude; exactly 20% is not 'below 20%'.
    Packet support/absolute maximum remain unchanged. This is a representative
    PEAK, not a claim to have measured the physical first-arrival onset.
    """
    if isinstance(increase_limit,bool) or not math.isfinite(increase_limit) or not 0<=increase_limit<1:
        raise ValueError('Finite lobe increase limit in [0,1) required')
    left,right=packet['start'],packet['end']; baseline=packet.get('local_baseline',0.)
    m=np.abs(y[left:right+1]-baseline);dominant=left+int(np.argmax(m))
    level=max(4.5*sigma,.10*float(m.max()))
    before=np.r_[-np.inf,m[:-1]];after=np.r_[m[1:],-np.inf]
    peaks=np.flatnonzero((m>before)&(m>=after)&(m>=level))+left
    first=int(peaks[0]) if len(peaks) else dominant
    a0,a1=abs(float(y[first]-baseline)),abs(float(y[dominant]-baseline))
    limit=a0*(1+increase_limit)
    prefer=(first<dominant and a0>0 and a1<limit
            and not math.isclose(a1,limit,rel_tol=1e-12,abs_tol=0.))
    return {'incident_peak':first if prefer else dominant,'absolute_peak':dominant,
            'first_significant_peak':first,'later_peak_increase_fraction':a1/a0-1 if a0 else None,
            'first_lobe_preferred':prefer,'increase_limit_fraction':increase_limit,
            'lobe_comparison_baseline':float(baseline),
            'timing_kind':'selected_lobe_peak_not_first_arrival_onset'}


def period(y, begin, end, sigma, amplitude):
    block = y[begin:end+1]
    main = begin + int(np.argmax(np.abs(block)))
    level = max(sigma*2, amplitude*.05)
    ii = np.flatnonzero(y[main+1:end+1]*y[main] < 0) + main+1
    if len(ii):
        opposite = int(ii[np.argmax(np.abs(y[ii]))])
        if abs(y[opposite]) >= max(level, abs(y[main])*.10):
            return max(4, min(80, 2*(opposite-main)))
    signs = np.sign(block[np.abs(block) >= level])
    crossings = np.flatnonzero(signs[1:] != signs[:-1])+1
    # Gaps use original sample indices, not compressed active-sample ordinals.
    active = np.flatnonzero(np.abs(block) >= level)
    if len(crossings) < 2:
        return 8
    return max(4, min(80, round(2*float(np.median(np.diff(active[crossings]))))))


def quiet_blocks(y, hold, sigma):
    """Exactly A quiet test for each inclusive start:start+hold-1 block."""
    if len(y) < hold:
        return np.empty(0, bool)
    windows = np.lib.stride_tricks.sliding_window_view(y, hold)
    # Chunk to avoid record_length*hold temporary memory for large LOC files.
    out = np.zeros(len(windows), bool)
    for start in range(0, len(windows), 16384):
        a = windows[start:start+16384]
        mean = a.mean(axis=1)
        residual = a-mean[:, None]
        out[start:start+len(a)] = ((np.abs(mean) <= 4*sigma)
            & ((np.abs(residual) >= 2.5*sigma).sum(axis=1) <= hold//10)
            & ((residual*residual).sum(axis=1) <= hold*(2.5*sigma)**2))
    return out


def discover_packets(y, sigma, roundtrip):
    y = np.asarray(y, dtype=float)
    if y.ndim != 1 or len(y) < 1 or not np.isfinite(y).all():
        raise ValueError('Finite raw centered 1-D samples required')
    if not math.isfinite(sigma) or sigma <= 0 or isinstance(roundtrip, bool) or not isinstance(roundtrip, int) or roundtrip < 1:
        raise ValueError('Positive noise and integer roundtrip required')
    magnitude = np.abs(y)
    triggers = np.flatnonzero(magnitude >= 4.5*sigma)
    packets, scan = [], 0
    while True:
        j = int(np.searchsorted(triggers, scan))
        if j == len(triggers):
            break
        trigger = int(triggers[j])
        initial_end = min(len(y)-1, trigger+max(12, min(96, roundtrip//6)))
        initial_peak = trigger+int(np.argmax(magnitude[trigger:initial_end+1]))
        quiet = 0
        for k in range(trigger, initial_peak+1):
            quiet = quiet+1 if magnitude[k] < 2.5*sigma else 0
            if quiet >= 6:
                initial_peak = trigger+int(np.argmax(magnitude[trigger:k-quiet+1]))
                break
        p = period(y, trigger, min(len(y)-1, initial_peak+max(12, roundtrip//8)), sigma, magnitude[initial_peak])
        hold = max(6, min(64, math.ceil(p*.8)))
        previous_end = packets[-1]['end'] if packets else -1
        lower = max(previous_end+1, 0, trigger-max(hold*3, roundtrip//5))
        # The old full-record cache was read only over these bounded ranges.
        # Local arrays contain exactly the same inclusive windows, in the same
        # order; all reductions still use quiet_blocks without approximation.
        q = np.flatnonzero(quiet_blocks(y[lower:trigger], hold, sigma))
        left_guard=(lower+int(q[-1]),lower+int(q[-1])+hold-1) if len(q) else None
        start = lower+int(q[-1])+hold if len(q) else lower
        limit = min(len(y)-1, trigger+max(24, roundtrip*2))
        q = np.flatnonzero(quiet_blocks(y[trigger:limit+1], hold, sigma))
        terminated = len(q) > 0
        stop = trigger+int(q[0])+hold-1 if terminated else limit
        right_guard=(stop-hold+1,stop) if terminated else None
        active = np.flatnonzero(magnitude[trigger:stop+1] >= 2.5*sigma)
        end = trigger+int(active[-1]) if terminated and len(active) else stop
        peak = start+int(np.argmax(magnitude[start:end+1]))
        onset = max(3*sigma, magnitude[peak]*.025)
        lead = np.flatnonzero(magnitude[start:peak+1] >= onset)
        start = start+int(lead[0]) if len(lead) else peak
        packet = {'packet_id':len(packets), 'start':start, 'end':end,
                  'peak':peak, 'period_samples':p, 'quiet_hold_samples':hold,
                  'right_quiet_proven':bool(terminated),
                  'clipped':start == 0 or end >= len(y)-1,
                  'unresolved_overlap':end-start > roundtrip,
                  'physical_packet_identity_confirmed':False}
        guards=[y[a:b+1] for a,b in (g for g in (left_guard,right_guard) if g is not None)]
        packet['local_baseline']=float(np.median(np.concatenate(guards))) if guards else 0.
        packet['quiet_guards']=[list(g) if g else None for g in (left_guard,right_guard)]
        packets.append(packet)
        scan = max(trigger+1, end+hold)
    return packets


class PacketIndex:
    def __init__(self, packets):
        self.packets = packets
        self.starts = np.array([p['start'] for p in packets], dtype=int)

    def at(self, index):
        j = int(np.searchsorted(self.starts, index, side='right'))-1
        if j >= 0 and index <= self.packets[j]['end']:
            return self.packets[j]
        return None
