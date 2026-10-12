"""Complete offline DAC-evidence research flow, NOT original binary equivalence.

The spectral path preserves the observed nonstandard complex arithmetic and
1-based slots. Its scheduling, failure continuation and float64 transform are
explicit research implementations. The reference path is a separate comparator.
No statistical result, correlation or envelope proves physical pulse identity.
"""
from __future__ import annotations
import hashlib
import math
import re
from pathlib import Path
import numpy as np
from scipy.ndimage import maximum_filter1d
from scipy.signal import correlate, find_peaks
from dac_recovered.reflection import (roundtrip_samples, observed_spectral_product,
    extract_peak_window, decay_parameters, attenuation_envelope, half_height_width)
from dac_recovered.cluster import Event, analyze_events
from task17_dac import endpoint_analysis
from e_adjudication import adjudicate


def fast_automatic_detection(y, amplitude_limit, factor):
    """Vectorized equivalent of the recovered local peak/200 sample guard.

    Kept separate from recovered source. Equivalence is regression tested against
    task17_dac.automatic_detection, not asserted for original DAC whole program.
    """
    if not math.isfinite(amplitude_limit) or amplitude_limit <= 0:
        raise ValueError('Finite positive amplitude limit required')
    if isinstance(factor,bool) or not isinstance(factor,int) or factor < 0:
        raise ValueError('Nonnegative integer threshold factor required')
    magnitude=np.abs(y);ii=np.arange(len(y))
    extreme=magnitude>=maximum_filter1d(magnitude,size=21,mode='constant',cval=np.inf)
    extreme[:12]=False;extreme[-12:]=False
    prefix=np.r_[0,np.cumsum(magnitude>round(.99*amplitude_limit),dtype=np.int64)]
    rejected=(prefix[ii+1]-prefix[np.maximum(1,ii-199)])>0
    edges=(ii>=12)&(ii<len(y)-12)
    indices=np.flatnonzero(extreme & ~rejected)
    amplitudes=y[indices];positive=amplitudes[amplitudes>0];nonpositive=amplitudes[amplitudes<=0]
    base=1.2*float(positive.mean()) if len(positive) else 0.
    return {'raw_threshold':factor*base,'effective_threshold':max(base,factor*base),
            'positive_baseline':base,'nonpositive_baseline':1.2*float(nonpositive.mean()) if len(nonpositive) else 0.,
            'positive_count':len(positive),'nonpositive_count':len(nonpositive),'factor':factor,
            'peak_indices':indices.tolist(),'over_limit_rejected_indices':np.flatnonzero(rejected & edges).tolist(),
            'state':'zero_positive_baseline_review' if len(positive)==0 else 'threshold_calculated',
            'physical_identity_confirmed':False}


def sha(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def read_loc(path, metadata):
    """Strict registered 11-header-line text format; preserve sample ordinals.

    Scaling matches frozen A8ReadLocation (/100000). No nan_to_num, fallback
    sample rate, binary guessing, or number extraction from malformed lines.
    Registered metadata is usable only after the caller checks input hashes.
    """
    raw = Path(path).read_bytes()
    parts = raw.split(b'\n', 11)
    if len(parts) != 12 or not re.fullmatch(rb'Data:\s*', parts[10]):
        raise ValueError('Unsupported LOC header/layout; expected 11-line text Data marker')
    body = parts[11]
    if re.search(rb'[^0-9eE+\-.\s]', body):
        raise ValueError('Non-numeric or nonfinite token in waveform; no samples discarded')
    tokens = len(body.split())
    a = np.fromstring(body.decode('ascii'), dtype=np.float64, sep=' ')
    if a.size != tokens or not 16 <= a.size <= 8388608 or not np.isfinite(a).all():
        raise ValueError('Invalid/truncated/nonfinite or oversized numeric waveform')
    fs = float(metadata['sample_rate_hz']); v = float(metadata['velocity_m_per_us'])
    length = float(metadata['cable_length_m'])
    if not math.isfinite(fs) or not 1e3 <= fs <= 5e9:
        raise ValueError('Invalid explicit sampling rate')
    if not math.isfinite(length) or not 1 <= length <= 100000 or not 50 <= v <= 300:
        raise ValueError('Invalid explicit cable geometry or speed')
    a = a / 100000.0
    return a, {'samples': len(a), 'engineering_samples_sha256': hashlib.sha256(a.tobytes()).hexdigest(),
               'unit': 'pC per existing text LOC engineering conversion /100000',
               'fs_hz': fs, 'v_m_us': v, 'length_m': length}


def spectral_slots(a, b):
    """0x5eead0, 0x5e1ecc, 0x5ef0b0: slots 1..N transformed, slot 0 untouched.
    Orthonormal float64 FFT substitutes the observed normalized x87 FFT.
    """
    a = np.asarray(a, dtype=float); b = np.asarray(b, dtype=float)
    n = len(a) - 1
    if a.shape != b.shape or n < 2 or n & (n-1) or not np.isfinite(a).all() or not np.isfinite(b).all():
        raise ValueError('Two finite equal N+1 arrays required; N power of two')
    aa = np.zeros(n+1, complex); bb = np.zeros(n+1, complex)
    aa[0], bb[0] = a[0], b[0]
    aa[1:] = np.fft.fft(a[1:], norm='ortho')
    bb[1:] = np.fft.fft(b[1:], norm='ortho')
    if not np.isfinite(aa).all() or not np.isfinite(bb).all():
        raise ValueError('Nonfinite spectral transform; numeric failure, not no discharge')
    re_part, im_part = observed_spectral_product(aa.real, aa.imag, bb.real, bb.imag)
    c = re_part + 1j*im_part
    if not np.isfinite(c).all():
        raise ValueError('Nonfinite spectral product; numeric failure, not no discharge')
    result = np.zeros(n+1); result[0] = -c[0].real
    result[1:] = -np.fft.ifft(c[1:], norm='ortho').real
    if not np.isfinite(result).all():
        raise ValueError('Nonfinite inverse transform; numeric failure, not no discharge')
    return result


def matching_curve(y, n0, left, right, D, mode):
    if mode == 'observed_spectral':
        n = 1 << max(12, (D+500-1).bit_length())
        a = y[(n0-100+np.arange(n+1)) % len(y)]
        b = np.zeros(n+1); b[:right-left+1] = y[left:right+1]
        scores = spectral_slots(a, b)
        hi = min(len(scores)-6, right-n0+D+540)
        peaks, _ = find_peaks(scores[100:hi])
        if not len(peaks):
            return [], None
        main = int(max(peaks+100, key=lambda i: scores[i]))
        if scores[main] <= 0:
            return [], None
        start = main + max(right-n0, 10) + 1
        stop = min(len(scores)-6, main+right-n0+D+40)
        peaks, _ = find_peaks(scores[start:stop])
        return [(int(n0+i-main), float(scores[i]/scores[main]), int(i)) for i in peaks+start], main
    if mode != 'reference_correlation':
        raise ValueError('Unknown matching mode')
    # Only local physical search region is correlated; equivalent to whole-record
    # dot product in its interior, avoiding full-record FFT at every iteration.
    template = y[left:right+1]
    lo, hi = max(0, n0-200), min(len(y), n0+D+202)
    full = correlate(y[lo:hi], template, mode='full', method='auto')
    energy = float(np.dot(template, template))
    if not math.isfinite(energy) or not np.isfinite(full).all():
        raise ValueError('Nonfinite template correlation; numeric failure, not no discharge')
    if energy <= 0:
        return [], None
    first = n0+max(right-n0,10)+1
    last = min(n0+D, len(y)-7)
    offsets = np.arange(first, last+1) - lo + right-n0
    if len(offsets) < 3:
        return [], None
    curve = full[offsets] / energy
    peaks, _ = find_peaks(curve)
    return [(int(first+i), float(curve[i]), int(offsets[i])) for i in peaks], n0


def descriptor(y, i, fs):
    lo, hi = max(0,i-51), min(len(y),i+152)
    a = y[lo:hi]
    hw = half_height_width(a, i-lo)
    z = (a-np.mean(a))*np.hanning(len(a))
    power = np.abs(np.fft.rfft(z))**2
    total = float(power.sum())
    if not np.isfinite(power).all() or not math.isfinite(total):
        raise ValueError('Nonfinite packet spectrum descriptor')
    centroid = float(np.dot(np.fft.rfftfreq(len(z),1/fs),power)/total) if total>0 else None
    return {'halfheight_samples': hw['width_samples'], 'edge_truncated': hw['edge_truncated'],
            'centroid_hz': centroid, 'spectrum_window': [lo,hi]}


def analyze_wave(y, fs, length, velocity, config, mode):
    if mode not in ('observed_spectral','reference_correlation'):
        raise ValueError('Unknown E matching mode')
    raw = np.asarray(y, dtype=float)
    if raw.ndim != 1 or len(raw)<16 or not np.isfinite(raw).all():
        raise ValueError('Finite 1-D waveform required')
    if not math.isfinite(fs) or not 1e3<=fs<=5e9 or not math.isfinite(length) or not 1<=length<=100000 or not math.isfinite(velocity) or not 50<=velocity<=300:
        raise ValueError('Invalid sampling/length/velocity input')
    for key in ('detection_factor','max_incident_attempts_per_record','endpoint_width_limit_samples'):
        value=config[key]
        if isinstance(value,bool) or not isinstance(value,int) or value<0 or (key=='max_incident_attempts_per_record' and value==0):
            raise ValueError('Invalid nonnegative integer configuration: '+key)
    for key in ('endpoint_near_factor_percent','envelope_tolerance','lower_matching_ratio'):
        value=config[key]
        if isinstance(value,bool) or not math.isfinite(value) or value<0:
            raise ValueError('Invalid finite nonnegative configuration: '+key)
    if config['envelope_tolerance']>=100:
        raise ValueError('Envelope tolerance must remain below100percent')
    multiplier=config['noise_multiplier']
    if isinstance(multiplier,bool) or not math.isfinite(multiplier) or multiplier<=0:
        raise ValueError('Finite positive noise multiplier required')
    if config['suppression_mode'] not in ('length','linear_decay'):
        raise ValueError('Unknown suppression mode')
    if config['waveform_filter'] != 'off':
        raise ValueError('Full-record filter scheduling is unverified; this profile only supports off')
    D = roundtrip_samples(1/fs,length,velocity)
    if D+500 > 1048576:
        raise ValueError('FFT budget exceeded; unsupported sample rate/length combination')
    baseline = float(np.median(raw)); y = raw-baseline
    if not np.isfinite(y).all():
        raise ValueError('Nonfinite centered waveform')
    noise = float(np.median(np.abs(y))/.6744897501960817)
    threshold_mode=config['detection_mode']
    if threshold_mode=='research_mad':
        threshold = max(float(config['noise_multiplier'])*noise, np.finfo(float).eps*max(1,float(np.max(np.abs(y))))*32)
        threshold_source='research 6*MAD; not original DAC automatic threshold'
    elif threshold_mode=='manual':
        threshold=float(config['manual_threshold_pc']);threshold_source='explicit caller manual threshold'
    elif threshold_mode=='observed_positive_peak_mean':
        limit=float(np.max(np.abs(raw))) if config.get('amplitude_limit_mode')=='user_record_absmax' else config.get('amplitude_limit_pc')
        if limit==0:
            limit=None
        if limit is not None:
            detected=fast_automatic_detection(y,float(limit),config['detection_factor'])
            detected['amplitude_limit_pc']=float(limit)
            detected['amplitude_limit_source']=config.get('amplitude_limit_mode','explicit_caller')
            detected['raw_absmax_sample']=int(np.argmax(np.abs(raw)))
            detected['guard_rejection_level_pc']=round(.99*limit)
            detected['guard_rejected_sample_count']=len(detected['over_limit_rejected_indices'])
            threshold=detected['effective_threshold']
            allowed=np.zeros(len(y),bool);allowed[detected['peak_indices']]=True
            threshold_source='latest recovered auto formula+M guard; user M=max(abs raw data), not verified hardware range'
        else:
            # Same positive-peak formula, but NOT exact recovered detector: M is
            # absent. Never pretend max(record) is a hardware/engineering limit.
            magnitude=np.abs(y)
            allowed=magnitude>=maximum_filter1d(magnitude,size=21,mode='constant',cval=np.inf)
            allowed[:12]=False;allowed[-12:]=False
            positive=y[allowed & (y>0)]
            base=1.2*float(positive.mean()) if len(positive) else 0.
            threshold=max(base,base*config['detection_factor'])
            detected={'positive_baseline':base,'positive_count':len(positive),
                      'effective_threshold':threshold,'factor':config['detection_factor'],
                      'state':'provisional_missing_M_guard','amplitude_limit_pc':None}
            threshold_source='provisional recovered positive-peak formula; 200-sample M guard unavailable'
        if threshold==0:return {'mode':mode,'events':[],'attempts':[],'roundtrip_samples':D,
            'threshold_pc':0,'noise_mad_pc':noise,'baseline_pc':baseline,'threshold_source':threshold_source,
            'incident_attempts':0,'above_threshold_peaks':0,'unprocessed_peaks':0,'budget_exhausted':False,
            'automatic_threshold_details':detected,'working_array_matching':False,'suppression_mode':'E_packet_graph_and_two_endpoint_echo_review',
            'dac_interval_blanking_applied':False,'immutable_raw_discovery':True,
            'e_adjudication':{'status':'no_evaluable_threshold','input_candidates':0},
            'status':'no_evaluable_positive_threshold','full_dac_equivalence':False,'physical_accuracy_verified':False}
    else:raise ValueError('Unknown detection mode')
    if not math.isfinite(threshold) or threshold<=0:raise ValueError('Finite positive detection threshold required')
    peaks, _ = find_peaks(np.abs(y), height=threshold)
    # Add edge extrema so they are explicitly audited as truncated, not omitted.
    peaks = np.unique(np.r_[peaks, [i for i in (0,len(y)-1) if abs(y[i])>threshold]]).astype(int)
    order = peaks[np.argsort(-np.abs(y[peaks]),kind='stable')]
    masked = np.zeros(len(y),bool); working=y.copy(); events=[]; attempts=[]; count=0
    extrema = np.abs(y) >= maximum_filter1d(np.abs(y), size=21, mode='constant',cval=np.inf)
    if threshold_mode=='observed_positive_peak_mean':
        order=order[allowed[order]]
        extrema &= allowed
    for original in order:
        n0 = int(original)
        if masked[n0]:
            continue
        if count >= config['max_incident_attempts_per_record']:
            break
        count += 1
        row = {'incident':n0,'raw_amplitude_pc':float(raw[n0]),'centered_amplitude_pc':float(y[n0]),'candidates':[]}
        # Recovered boundary requirement; emit diagnostic rather than wrap a
        # physically truncated packet and pretend it has a valid full window.
        if n0<102 or n0+D+51>=len(y):
            row['status']='truncated_physical_window'; masked[n0]=True; attempts.append(row); continue
        windowlo=max(0,n0-160); windowhi=min(len(y),n0+D+202)
        packet=working[windowlo:windowhi]; local=n0-windowlo
        w=extract_peak_window(packet,local,threshold)
        left,right=w['left']+windowlo,w['right']+windowlo
        decay=decay_parameters(packet,local,D)
        row['window']=[left,right]; row['decay']=decay
        if not decay['evaluable']:
            row['status']='decay_unavailable'; masked[left:right+1]=True; attempts.append(row); continue
        candidates,main=matching_curve(working,n0,left,right,D,mode)
        row['matching_main_index']=main
        # Spectral peaks are refined on the raw signal, then every final gate is
        # rechecked. Missing preceding-200 amplitude-limit metadata is explicit.
        dedupe={}
        for proposed,ratio,scoreindex in candidates:
            if ratio <= config['lower_matching_ratio'] or not n0+10 < proposed <= n0+D:
                continue
            lo=max(n0+11,proposed-160); hi=min(n0+D,proposed+100,len(y)-13)
            # E discovers reflections on immutable raw evidence. The processed
            # incident inventory must not hide potential partners in this pass.
            eligible=np.flatnonzero(extrema[lo:hi+1] & (working[lo:hi+1]*working[n0]>0))+lo
            if not len(eligible):
                continue
            n1=int(eligible[np.argmax(np.abs(y[eligible]))]); d=n1-n0
            e0=float(abs(attenuation_envelope(d,y[n0],decay['tau_samples'],100-config['envelope_tolerance'],D)))
            e1=float(abs(attenuation_envelope(d,y[n0],decay['tau_samples'],100+4*config['envelope_tolerance'],D)))
            reasons=[]
            if not e0<abs(y[n1])<e1: reasons.append('outside_decay_envelope')
            if abs(y[n1])>=abs(y[n0]): reasons.append('reflection_not_smaller')
            if d<=max(right-n0,10): reasons.append('template_or_tail_overlap')
            x=length-d/fs*1e6*velocity/2
            if not 0<=x<=length: reasons.append('outside_raw_cable_window')
            r={'reflection':n1,'proposed_reflection':proposed,'refinement_changed':n1!=proposed,
               'matching_ratio':ratio,'matching_score_index':scoreindex,'delay_ns':d/fs*1e9,
               'x_raw_m':x,'raw_reflection_amplitude_pc':float(raw[n1]),
               'envelope_pc':[e0,e1],'rejections':reasons,
               'preceding_200_guard':'applied_user_M' if threshold_mode=='observed_positive_peak_mean' and limit is not None else 'unavailable_caller_amplitude_limit',
               'physical_identity_confirmed':False}
            if n1 not in dedupe or ratio>dedupe[n1]['matching_ratio']:dedupe[n1]=r
        row['candidates']=list(dedupe.values())
        passing=[r for r in row['candidates'] if not r['rejections']]
        if not passing:
            row['status']='unpaired'; masked[left:right+1]=True
        else:
            for best in sorted(passing,key=lambda r:r['reflection']):
                n1=best['reflection'];rw=extract_peak_window(packet,n1-windowlo,threshold)
                desc0=descriptor(y,n0,fs);desc1=descriptor(y,n1,fs)
                endpoint=endpoint_analysis(y,n0,best['x_raw_m'],length,enable_mirror=False,
                             near_factor_percent=config['endpoint_near_factor_percent'],
                             width_limit_samples=config['endpoint_width_limit_samples'])
                event={**best,'incident':n0,'q_pc':float(raw[n0]),'candidate_count':len(passing),
                   'status':'multiple_candidates_review' if len(passing)>1 else 'single_candidate_review',
                   'incident_window':[left,right],
                   'reflection_window':[rw['left']+windowlo,rw['right']+windowlo],
                   'incident_descriptor':desc0,'reflection_descriptor':desc1,
                   'endpoint_width_flag':endpoint['endpoint_flag'],'endpoint_analysis':endpoint,
                   'endpoint_mirror_proposal_m':length-best['x_raw_m'] if endpoint['endpoint_flag'] else None,
                   'noise_sigma_pc':noise,
                   'endpoint_mirror_applied':False,'position_clamped':False}
                events.append(event)
            row['status']='E_complete_passing_candidate_inventory';row['passing_candidates']=len(passing)
        # This marks ONLY a processed incident packet. It never clears samples,
        # masks a complete cable window, or removes a candidate reflection.
        masked[left:right+1]=True
        attempts.append(row)
    unresolved=int(np.count_nonzero(~masked[order]))
    events,e_diagnostics=adjudicate(events,y,fs,D,config)
    e_diagnostics['candidate_discovery_complete']=unresolved==0
    e_diagnostics['candidate_discovery_unprocessed_peaks']=unresolved
    if unresolved:
        # A unique solution for an incomplete discovered subgraph is not a
        # complete-record ownership result. Keep the provisional solution in
        # diagnostics, but do not feed its prefix into the statistics stage.
        e_diagnostics['provisional_independent_eligible_events']=e_diagnostics['independent_eligible_events']
        for event in events:
            event['e_provisional_decision']=event['e_decision']
            event['e_provisional_engineering_eligible']=event['engineering_eligible']
            event['e_decision']='review';event['engineering_eligible']=False
            event['e_reasons'].append('candidate_discovery_budget_incomplete_no_record_ownership')
        e_diagnostics['independent_eligible_events']=0
        e_diagnostics['counts']={'review':len(events)}
    return {'mode':mode,'events':events,'attempts':attempts,'roundtrip_samples':D,
            'threshold_pc':threshold,'noise_mad_pc':noise,'baseline_pc':baseline,
            'threshold_source':threshold_source,
            'automatic_threshold_details':detected if threshold_mode=='observed_positive_peak_mean' else None,
            'working_array_matching':False,'supppression_mode':'E_packet_graph_and_two_endpoint_echo_review',
            'dac_interval_blanking_applied':False,'immutable_raw_discovery':True,'e_adjudication':e_diagnostics,
            'incident_attempts':count,'above_threshold_peaks':len(order),
            'unprocessed_peaks':unresolved,'budget_exhausted':unresolved>0,
            'full_dac_equivalence':False,'physical_accuracy_verified':False}


def apply_statistics(events, length, config):
    """Spatial suggestions only; no deletion, relocation or confirmation."""
    if not events:return {'events':[], 'parameters':{}}
    indices=[i for i,r in enumerate(events) if r.get('engineering_eligible') is True]
    e=[Event(events[i]['phase'],events[i]['voltage_kv'],events[i]['x_raw_m'],events[i]['q_pc']) for i in indices]
    evaluated=analyze_events(e,length,config['plot_amplitude_threshold_pc'],config['cluster_strength'],
                       max(x.voltage_kv for x in e),config['minimum_cluster_size']) if e else {'events':[], 'parameters':{}}
    decisions=dict(zip(indices,evaluated['events']))
    out={'events':[], 'parameters':evaluated['parameters']}
    for i,src in enumerate(events):
        decision=decisions.get(i,{'visible_candidate':False,'spatial_mask_keep':False,
                         'E_upstream_not_eligible':True,'statistical_review_not_submitted':True})
        # Upstream receipts may contain an older display decision. Preserve the
        # source evidence, but never let stale keep flags override this pass.
        decision={**src,**decision}
        if not 0<=src['x_raw_m']<=length:
            decision['visible_candidate']=False
            decision['spatial_mask_keep']=False
            decision['safety_override']='raw_position_outside_window_no_clamp'
        out['events'].append(decision)
    return out
