"""Threshold-seed windows for raw packet discovery; raw time is never compressed.

Only original continuous windows are passed to the packet helper. Overlapping
or adjacent windows are merged before analysis. A missing quiet boundary at an
artificial window edge is explicitly unresolved, distinct from raw-record EOF.
"""
from __future__ import annotations

import math
from numbers import Integral

import numpy as np
from a_packet_guard import discover_packets


VERSION = 'Eplus-threshold-seed-continuous-windows-r02'


def discover_seed_packets(y, sigma, D, seeds):
    """Return ``(packets, diagnostics)`` in original absolute sample indices.

    The caller computes threshold seeds on the uncompressed waveform. Each
    unique seed contributes [seed-D-margin, seed+3D+margin], inclusive, where
    margin=max(128,D//2). Subthreshold evidence and quiet context inside these
    windows remain available. This helper never applies a discharge-location
    prior and never upgrades packet discovery to physical pulse confirmation.
    """
    raw = np.asarray(y, dtype=float)
    if raw.ndim != 1 or len(raw) < 1 or not np.isfinite(raw).all():
        raise ValueError('Finite raw centered 1-D samples required')
    if isinstance(sigma, bool) or not math.isfinite(sigma) or sigma <= 0:
        raise ValueError('Positive finite noise sigma required')
    if isinstance(D, bool) or not isinstance(D, Integral) or D < 1:
        raise ValueError('Positive integer roundtrip required')
    D = int(D)
    received = list(seeds)
    if any(isinstance(s, (bool, np.bool_)) or not isinstance(s, Integral)
           or not 0 <= int(s) < len(raw) for s in received):
        raise ValueError('Seeds must be original integer sample indices inside the raw record')
    unique = sorted({int(s) for s in received})
    margin = max(128, D//2)
    windows = []
    for seed in unique:
        left, right = max(0,seed-D-margin), min(len(raw)-1,seed+3*D+margin)
        if windows and left <= windows[-1][1]+1:
            windows[-1][1] = max(windows[-1][1],right)
            windows[-1][2].append(seed)
        else:
            windows.append([left,right,[seed]])
    packets, receipts = [], []
    for window_id, (left,right,local_seeds) in enumerate(windows):
        window_raw = raw[left:right+1]
        local = discover_packets(window_raw,float(sigma),D)
        first_triggers = np.flatnonzero(np.abs(window_raw) >= 4.5*sigma)
        first_trigger = int(first_triggers[0]) if len(first_triggers) else None
        receipt = {'window_id':window_id, 'sample_range_inclusive':[left,right],
                   'sample_count':right-left+1, 'seeds':local_seeds,
                   'discovered_packets':len(local)}
        receipts.append(receipt)
        for source in local:
            packet = dict(source)
            for name in ('start','end','peak'):
                packet[name] = int(source[name])+left
            packet['quiet_guards'] = [None if guard is None else
                                      [int(guard[0])+left,int(guard[1])+left]
                                      for guard in source['quiet_guards']]
            # Only the FIRST local packet can inherit a truncated earlier
            # ringing packet. Interior packets may legitimately lack a left
            # guard because discover_packets bounds their backward search by
            # the previous packet end; that is not a window-edge truncation.
            # Likewise, an absent guard after a complete bounded backward
            # search is an original helper outcome, not caused by this slice.
            backward_span = max(source['quiet_hold_samples']*3,D//5)
            left_cut = (left > 0 and source['packet_id'] == 0
                        and (source['start'] == 0
                             or (source['quiet_guards'][0] is None
                                 and first_trigger is not None
                                 and first_trigger <= backward_span)))
            right_cut = right < len(raw)-1 and source['end'] >= right-left
            raw_clipped = packet['start'] == 0 or packet['end'] >= len(raw)-1
            packet.update(window_id=window_id,
                          window_sample_range=[left,right],
                          window_clipped=bool(left_cut or right_cut),
                          window_left_boundary_unproven=bool(left_cut),
                          window_right_boundary_unproven=bool(right_cut),
                          raw_record_clipped=bool(raw_clipped),
                          boundary_unresolved=bool(left_cut or right_cut),
                          clipped=bool(raw_clipped or left_cut or right_cut))
            packets.append(packet)
    packets.sort(key=lambda p:(p['start'],p['end'],p['peak']))
    for packet_id, packet in enumerate(packets):
        packet['packet_id'] = packet_id
    # Every input seed remains explicitly accountable, including seeds whose
    # amplitude is lower than the packet helper's own raw-trigger criterion.
    starts = np.array([p['start'] for p in packets],dtype=np.int64)
    seed_packets, missing = [], []
    for seed in unique:
        i = int(np.searchsorted(starts,seed,side='right'))-1
        packet = packets[i] if i >= 0 and seed <= packets[i]['end'] else None
        seed_packets.append({'seed':seed, 'packet_id':packet['packet_id'] if packet else None,
                             'boundary_unresolved':packet['boundary_unresolved'] if packet else None})
        if packet is None:
            missing.append(seed)
    scanned = sum(r['sample_count'] for r in receipts)
    diagnostics = {'version':VERSION, 'raw_samples':len(raw),
                   'input_seed_count':len(received), 'unique_seed_count':len(unique),
                   'duplicate_seeds_removed':len(received)-len(unique),
                   'window_margin_samples':margin, 'windows':receipts,
                   'scanned_samples':scanned, 'scanned_fraction':scanned/len(raw),
                   'sample_decimation_applied':False, 'continuous_windows_concatenated':False,
                   'absolute_sample_indices_preserved':True,
                   'packet_count':len(packets), 'seed_packet_identity':seed_packets,
                   'seeds_without_packet':missing,
                   'artificial_window_clipped_packet_ids':[p['packet_id'] for p in packets if p['window_clipped']],
                   'raw_record_clipped_packet_ids':[p['packet_id'] for p in packets if p['raw_record_clipped']],
                   'whole_record_packet_equivalence_assumed':False,
                   'known_location_used':False, 'physical_packet_identity_confirmed':False}
    return packets, diagnostics
