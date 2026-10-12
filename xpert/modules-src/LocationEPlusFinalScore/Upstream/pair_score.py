"""Final-raw-anchor full-packet score, isolated E+ research candidate.

All thresholds/weights below are preregistered research choices, not calibrated
probabilities or physical identity guarantees. No position, geometry, known
defect, spatial cluster, original proposal ratio, or attenuation curve enters
the score. Raw n0/n1 and inclusive packet supports are never repicked.
"""
from __future__ import annotations
import copy
import hashlib
import math
from collections import Counter
import numpy as np

VERSION = 'Eplus-final-raw-packet-score-r01'
DEFAULT_CONFIG = {
    'qualification_mode': 'qualified',
    'minimum_shape_correlation': .60,
    'minimum_lobe_snr': 4.5,
    'snr_saturation': 10.,
    'maximum_alignment_ns': 32.,
    'maximum_alignment_samples': 16,
    'minimum_guard_samples': 8,
    'shape_weight': .65, 'spectral_weight': .15,
    'width_weight': .10, 'snr_weight': .10,
    'maximum_diagnostic_stretch': 1.5,
    'diagnostic_stretches': [1., 1.15, 1.30, 1.50],
    'graph_score_clip': 1e-9,
}


def settings(config=None):
    out = copy.deepcopy(DEFAULT_CONFIG)
    if config:
        extra = set(config) - set(out)
        if extra:
            raise ValueError('Unknown pair-score settings: ' + ', '.join(sorted(extra)))
        out.update(copy.deepcopy(config))
    if out['qualification_mode'] not in ('qualified', 'ranking_only'):
        raise ValueError('qualification_mode must be qualified or ranking_only')
    for k in ('minimum_shape_correlation', 'shape_weight', 'spectral_weight', 'width_weight', 'snr_weight'):
        if isinstance(out[k], bool) or not math.isfinite(out[k]) or not 0 <= out[k] <= 1:
            raise ValueError(k + ' must be finite in [0,1]')
    if not math.isclose(sum(out[k] for k in ('shape_weight','spectral_weight','width_weight','snr_weight')), 1., abs_tol=1e-12):
        raise ValueError('Pair-score weights must sum to one')
    for k in ('minimum_lobe_snr', 'snr_saturation', 'maximum_alignment_ns', 'maximum_diagnostic_stretch'):
        if isinstance(out[k], bool) or not math.isfinite(out[k]) or out[k] <= 0:
            raise ValueError(k + ' must be finite and positive')
    for k in ('maximum_alignment_samples', 'minimum_guard_samples'):
        if isinstance(out[k], bool) or not isinstance(out[k], int) or not 1 <= out[k] <= 256:
            raise ValueError(k + ' must be an integer in 1..256')
    if not 0 < out['graph_score_clip'] < .01:
        raise ValueError('graph_score_clip must be in (0,.01)')
    stretches = out['diagnostic_stretches']
    if not isinstance(stretches, (list, tuple)) or not stretches or len(stretches) > 16:
        raise ValueError('1..16 finite diagnostic stretches required')
    if any(isinstance(t, bool) or not math.isfinite(t) or not 1 <= t <= out['maximum_diagnostic_stretch'] for t in stretches):
        raise ValueError('Diagnostic stretches outside preregistered finite range')
    out['diagnostic_stretches'] = sorted(set(float(t) for t in stretches) | {1.})
    return out


def _int(v, name):
    if isinstance(v, (bool, np.bool_)) or not isinstance(v, (int, np.integer)):
        raise ValueError(name + ' must be an original integer sample index')
    return int(v)


def _support(v, anchor, count):
    if not isinstance(v, (list, tuple)) or len(v) != 2:
        raise ValueError('Two inclusive full-support sample indices required')
    a, b = (_int(x, 'support index') for x in v)
    if not 0 <= a <= anchor <= b < count or b-a < 4:
        raise ValueError('Final anchor must be in a >=5-sample complete raw support')
    return a, b


def _width(samples, anchor_local):
    """Same-polarity selected-lobe half-height width, no absolute-peak repick."""
    sign = 1 if samples[anchor_local] >= 0 else -1
    a = sign * samples
    height = float(a[anchor_local])
    if height <= 0:
        raise ValueError('Final selected lobe has no baseline-resolved positive amplitude')
    level = height / 2
    left = anchor_local
    while left > 0 and a[left-1] >= level:
        left -= 1
    right = anchor_local
    while right+1 < len(a) and a[right+1] >= level:
        right += 1
    clipped = left == 0 or right == len(a)-1
    # Linear interpolation is for width diagnostics, never for raw time anchors.
    lo = float(left)
    hi = float(right)
    if left > 0 and a[left] != a[left-1]:
        lo = left-1 + (level-a[left-1])/(a[left]-a[left-1])
    if right+1 < len(a) and a[right] != a[right+1]:
        hi = right + (a[right]-level)/(a[right]-a[right+1])
    return max(float(hi-lo), np.finfo(float).eps), clipped


def _packet_data(raw, anchor, support, packet, global_sigma, fs, cfg):
    left, right = support
    if packet is None:
        raise ValueError('Complete packet identity unavailable for final anchor')
    if (packet.get('start'), packet.get('end')) != support:
        raise ValueError('Event support is not the complete inventoried raw packet')
    if packet.get('clipped') or packet.get('window_clipped') or packet.get('unresolved_overlap') or not packet.get('right_quiet_proven'):
        raise ValueError('Raw packet clipping/overlap/quiet boundary unresolved')
    guards = []
    guard_ranges = []
    for guard in packet.get('quiet_guards', []):
        if guard is None:
            continue
        if not isinstance(guard, (list, tuple)) or len(guard) != 2:
            raise ValueError('Malformed proved quiet guard')
        a, b = (_int(x, 'quiet guard') for x in guard)
        if not 0 <= a <= b < len(raw):
            raise ValueError('Quiet guard outside original raw record')
        guards.append(raw[a:b+1]); guard_ranges.append([a,b])
    if not guards or sum(len(x) for x in guards) < cfg['minimum_guard_samples']:
        raise ValueError('Insufficient proved quiet samples for local baseline/noise')
    quiet = np.concatenate(guards)
    baseline = float(np.median(quiet))
    with np.errstate(over='ignore',invalid='ignore'):
        residual=quiet-baseline
    if not np.isfinite(residual).all():
        raise ValueError('Local baseline/noise subtraction overflow')
    local_sigma = float(np.median(np.abs(residual)) / .6744897501960817)
    if not math.isfinite(local_sigma):
        raise ValueError('Local noise sigma overflow')
    numerical_floor = np.finfo(float).eps * max(1., float(np.max(np.abs(raw[left:right+1])))) * 32
    sigma = max(global_sigma, local_sigma, numerical_floor)
    with np.errstate(over='ignore',invalid='ignore'):
        a = raw[left:right+1] - baseline
    if not np.isfinite(a).all():
        raise ValueError('Complete packet baseline subtraction overflow')
    scale = float(np.max(np.abs(a)))
    if scale <= numerical_floor:
        raise ValueError('Complete packet has numerically unresolved energy')
    # Normalize before all energy calculations; accepts large finite amplitudes.
    norm = a / scale
    width, clipped = _width(norm, anchor-left)
    amplitude = abs(float(raw[anchor]-baseline))
    return {'anchor': anchor, 'support': [left,right], 'baseline_pc': baseline,
            'quiet_guards': guard_ranges, 'local_noise_sigma_pc': local_sigma,
            'effective_noise_sigma_pc': sigma, 'global_noise_sigma_pc': global_sigma,
            'amplitude_pc': amplitude, 'selected_lobe_snr': amplitude/sigma,
            'selected_lobe_snr_db': 20*math.log10(amplitude/sigma) if amplitude > 0 else None,
            'halfheight_width_samples': width, 'halfheight_width_ns': width/fs*1e9,
            'halfheight_boundary_clipped': clipped, 'duration_ns': (right-left+1)/fs*1e9,
            '_signal': norm, '_offset': left-anchor, '_scale': scale}


def _ncc(a, b, lag, stretch=1.):
    """Cosine NCC over the UNION of both complete supports; no overlap cherry-pick.

    Stretch b around its fixed raw anchor for diagnostic comparison only.
    Missing support outside each packet is zero after its proved baseline.
    """
    ax = a['_offset'] + np.arange(len(a['_signal']), dtype=float)
    bx = (b['_offset'] + np.arange(len(b['_signal']), dtype=float)) / stretch + lag
    low = int(math.floor(min(ax[0],bx[0])))
    high = int(math.ceil(max(ax[-1],bx[-1])))
    grid = np.arange(low,high+1,dtype=float)
    aa = np.interp(grid,ax,a['_signal'],left=0.,right=0.)
    bb = np.interp(grid,bx,b['_signal'],left=0.,right=0.)
    den = float(np.linalg.norm(aa)*np.linalg.norm(bb))
    if den <= np.finfo(float).tiny:
        return None
    return float(np.clip(np.dot(aa,bb)/den,-1.,1.))


def _frequency(a, b, fs):
    # Equal transform spacing in Hz, complete packet, no peak-dependent crop.
    n = 1 << max(5,(max(len(a['_signal']),len(b['_signal']))-1).bit_length())
    out = []
    probabilities = []
    frequency = np.fft.rfftfreq(n,1/fs)
    for p in (a,b):
        z = p['_signal']*np.hanning(len(p['_signal']))
        power = np.abs(np.fft.rfft(z,n=n))**2
        power[0] = 0.  # Removed baseline/DC is not a discriminating PD band.
        total = float(power.sum())
        if total <= np.finfo(float).tiny:
            raise ValueError('Complete packet frequency evidence unavailable')
        prob = power/total; probabilities.append(prob)
        cumulative = np.cumsum(prob)
        low = int(np.searchsorted(cumulative,.05));high = int(np.searchsorted(cumulative,.95))
        out.append({'centroid_hz': float(np.dot(frequency,prob)),
                    'energy90_band_hz': [float(frequency[min(low,len(frequency)-1)]),float(frequency[min(high,len(frequency)-1)])],
                    'fft_samples': n, 'frequency_bin_hz': fs/n})
    similarity = float(np.clip(np.sqrt(probabilities[0]*probabilities[1]).sum(),0.,1.))
    return out, similarity


def _score(raw, fs, event, packet_by_support, sigma, cfg, cache):
    n0 = _int(event['incident'],'incident');n1 = _int(event['reflection'],'reflection')
    if not 0 <= n0 < n1 < len(raw):
        raise ValueError('Ordered immutable final raw anchors required')
    supports = [_support(event[role+'_window'],n,len(raw)) for role,n in (('incident',n0),('reflection',n1))]
    if supports[0][1] >= supports[1][0]:
        raise ValueError('Final complete packet supports overlap')
    data=[]
    for n,support in zip((n0,n1),supports):
        key=(n,*support)
        if key not in cache:
            cache[key] = _packet_data(raw,n,support,packet_by_support.get(support),sigma,fs,cfg)
        data.append(cache[key])
    a,b=data
    # Bounded by physical ns AND selected-lobe width AND resource cap.
    allowed = min(cfg['maximum_alignment_samples'],int(math.floor(cfg['maximum_alignment_ns']*1e-9*fs)),
                  max(0,int(math.floor(min(a['halfheight_width_samples'],b['halfheight_width_samples'])/2))))
    direct = _ncc(a,b,0)
    candidates = [(lag,_ncc(a,b,lag)) for lag in range(-allowed,allowed+1)]
    candidates = [(lag,c) for lag,c in candidates if c is not None]
    if not candidates:
        raise ValueError('Final complete packet normalized correlation unavailable')
    lag, signed = max(candidates,key=lambda v:(abs(v[1]),-abs(v[0]),-v[0]))
    shape = abs(signed)
    diagnostic=[]
    for stretch in cfg['diagnostic_stretches']:
        tries=[(k,_ncc(a,b,k,stretch)) for k in range(-allowed,allowed+1)]
        k,c=max((v for v in tries if v[1] is not None),key=lambda v:(abs(v[1]),-abs(v[0]),-v[0]))
        diagnostic.append({'reflection_time_compression_factor':stretch,'lag_samples':k,
                           'signed_correlation':c,'absolute_correlation':abs(c)})
    spectral, band_score = _frequency(a,b,fs)
    ratio=b['halfheight_width_samples']/a['halfheight_width_samples']
    width_score=math.exp(-abs(math.log(ratio))/math.log(2.))
    snr=min(a['selected_lobe_snr'],b['selected_lobe_snr'])
    snr_score=min(1.,snr/cfg['snr_saturation'])
    factors={'shape':shape,'spectral':band_score,'width':width_score,'snr':snr_score}
    final_score=sum(cfg[k+'_weight']*factors[k] for k in factors)
    final_score=float(np.clip(final_score,0.,1.))
    reasons=[]
    if shape < cfg['minimum_shape_correlation']:
        reasons.append('final_complete_packet_shape_below_research_floor')
    if snr < cfg['minimum_lobe_snr']:
        reasons.append('final_selected_lobe_local_snr_below_research_floor')
    if a['halfheight_boundary_clipped'] or b['halfheight_boundary_clipped']:
        reasons.append('final_selected_lobe_halfheight_boundary_unresolved')
    qualified=not reasons
    warnings=[]
    if ratio < 1:
        warnings.append('reflection_selected_lobe_narrower_than_incident_not_a_hard_identity_veto')
    if max(x['absolute_correlation'] for x in diagnostic)>shape+.10:
        warnings.append('stretch_model_improves_shape_diagnostic_only_no_automatic_promotion')
    if event.get('proposed_reflection',n1) != n1:
        warnings.append('legacy_proposal_ratio_not_measured_at_final_anchor')
    graph_score=float(np.clip(final_score,cfg['graph_score_clip'],1-cfg['graph_score_clip']))
    return {'version':VERSION,'state':'measured','score':final_score,'score_kind':'research_weight_not_probability',
            'old_proposed_matching_ratio': event.get('matching_ratio'),
            'original_proposed_reflection':event.get('proposed_reflection',n1),
            'final_incident':n0,'final_reflection':n1,'raw_time_anchors_changed':False,
            'direct_signed_ncc':direct,'direct_absolute_ncc':abs(direct) if direct is not None else None,
            'final_signed_ncc':signed,'final_absolute_ncc':shape,'preferred_polarity':1 if signed>=0 else -1,
            'diagnostic_alignment_samples':lag,'diagnostic_alignment_ns':lag/fs*1e9,
            'allowed_alignment_samples':allowed,'alignment_changes_location':False,
            'stretch_diagnostics':diagnostic,'stretch_used_for_qualification':False,
            'incident':{k:v for k,v in a.items() if not k.startswith('_')},
            'reflection':{k:v for k,v in b.items() if not k.startswith('_')},
            'incident_frequency':spectral[0],'reflection_frequency':spectral[1],
            'width_ratio_reflection_over_incident':ratio,'factors':factors,
            'qualified':qualified,'rejection_reasons':reasons,'warnings':warnings,
            'graph_score_clipped':graph_score!=final_score,
            'graph_matching_ratio':graph_score/(1-graph_score),
            'known_position_used':False,'spatial_density_used':False,'physical_identity_confirmed':False}


def score_pairs(raw, fs_hz, events, packets, global_noise_sigma, config=None):
    """Return (all annotated original-ratio events, graph-ready events, receipt).

    Qualified graph copies replace matching_ratio with S/(1-S), so unchanged
    e_adjudication's weight ratio/(1+ratio) equals S. Its local relative margin
    is then a margin in transformed odds, NOT the same fraction of S. The
    caller MUST disclose this changed nonlinear meaning or use a new solver
    that compares S directly. Original proposed scores remain explicit.

    ranking_only admits measured failures to a separate CONTROL graph while
    preserving qualified=False and all rejection reasons. It does NOT admit
    unknown/malformed/quiet-unavailable scores, and must not be labelled a
    physically qualified result. Every input row remains in all_annotated.
    """
    cfg=settings(config)
    wave=np.asarray(raw,dtype=np.float64)
    if wave.ndim!=1 or len(wave)<16 or not np.isfinite(wave).all():
        raise ValueError('Immutable finite one-dimensional raw waveform required')
    if isinstance(fs_hz,bool) or not math.isfinite(fs_hz) or not 1e3<=fs_hz<=5e9:
        raise ValueError('Finite sample rate required')
    if isinstance(global_noise_sigma,bool) or not math.isfinite(global_noise_sigma) or global_noise_sigma<0:
        raise ValueError('Finite nonnegative existing global noise sigma required')
    if not isinstance(events,list) or not isinstance(packets,list):
        raise ValueError('Explicit event and complete-packet inventories required')
    before=hashlib.sha256(wave.tobytes()).hexdigest()
    lookup={}
    for p in packets:
        key=(_int(p['start'],'packet start'),_int(p['end'],'packet end'))
        if key in lookup and p != lookup[key]:
            raise ValueError('Conflicting complete packet identities')
        lookup[key]=p
    annotated=[];ready=[];cache={}
    for i,source in enumerate(events):
        event=copy.deepcopy(source);event['rescore_event_id']=i
        try:
            measured=_score(wave,float(fs_hz),event,lookup,float(global_noise_sigma),cfg,cache)
        except (ValueError,KeyError,TypeError,OverflowError,ZeroDivisionError) as exc:
            measured={'version':VERSION,'state':'unavailable','score':None,'qualified':False,
                      'graph_matching_ratio':None,'rejection_reasons':['final_pair_measurement_unavailable:'+str(exc)],
                      'raw_time_anchors_changed':False,'physical_identity_confirmed':False}
        event['final_pair_score']=measured
        annotated.append(event)
        if measured['state']=='measured' and (measured['qualified'] or cfg['qualification_mode']=='ranking_only'):
            graph=copy.deepcopy(event)
            graph['upstream_matching_ratio']=source.get('matching_ratio')
            graph['matching_ratio']=measured['graph_matching_ratio']
            graph['score_graph_control_only']=cfg['qualification_mode']=='ranking_only' and not measured['qualified']
            ready.append(graph)
    after=hashlib.sha256(wave.tobytes()).hexdigest()
    if before!=after:
        raise RuntimeError('Pair-score layer changed raw samples')
    receipt={'version':VERSION,'parameters':cfg,'input_events':len(events),'all_candidates_retained':True,
             'measured_events':sum(e['final_pair_score']['state']=='measured' for e in annotated),
             'qualified_events':sum(e['final_pair_score']['qualified'] for e in annotated),
             'graph_events':len(ready),'qualified_graph':cfg['qualification_mode']=='qualified',
             'rejection_counts':dict(Counter(reason for e in annotated for reason in e['final_pair_score']['rejection_reasons'])),
             'packet_descriptor_count':len(cache),'raw_sha256_before':before,'raw_sha256_after':after,
             'raw_unchanged':True,'raw_time_anchors_changed':False,'physical_accuracy_verified':False,
             'score_includes_old_matching_ratio':False,'score_includes_decay_envelope':False,
             'score_includes_known_position':False,'score_includes_spatial_density':False,
             'graph_adapter':'matching_ratio=S/(1-S), clipped to finite range; objective=S',
             'local_margin_semantics':'Existing relative margin compares transformed odds, not linear S gaps; disclose or adapt solver.',
             'limitations':['Thresholds/weights are frozen research choices, not field-calibrated error rates.',
                            'Polarity and finite shift allowance cannot distinguish all independent similar discharges.',
                            'Proved quiet guards are selected low-noise regions; max(global,local) only mitigates that selection bias.',
                            'Complete-support zero padding and Hann spectra remain sensitive to packet boundaries and dispersion.',
                            'Original upstream discovery/envelope exclusions are NOT recovered by this layer.']}
    return annotated,ready,receipt
