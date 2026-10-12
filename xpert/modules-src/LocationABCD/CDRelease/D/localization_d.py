"""D-0.1: auditable A/B/C candidate fusion, not an average or majority vote.

Consumes same-record native peak pairs and independently remeasures the waveform.
A/B are a correlated family. No historical position prior, automatic physical
confirmation, endpoint clipping, file writes or hidden algorithm reruns.
Requires the sibling C-0.3.0 package (NumPy). Peak mode only in this first version.
"""
from __future__ import annotations
import hashlib
import json
import math
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent.parent/'C'))
import numpy as np
from owts_pd import localization_c as c

VERSION='D-0.1.1'
MAX_PAIRS=100000

def _finite(x):
    return not isinstance(x,bool) and isinstance(x,(float,int)) and math.isfinite(x)

def analyze(request, *, cancel=None, progress=None):
    if not isinstance(request,dict) or request.get('algorithm')!='D':
        raise ValueError('Expected algorithm D request object')
    config=request['config'];fs=c._validate_config(config);segments=c._cable(config)
    s=c.Settings.parse(config.get('settings'));wave=c._wave(request['samples'])
    phase=request.get('phase')
    if phase not in ('L1','L2','L3'):raise ValueError('Explicit L1/L2/L3 phase required')
    record_id=c._identifier(request['record_id'],'record_id')
    voltage=request.get('voltage_kv')
    if voltage is not None and (not _finite(voltage) or voltage<0):raise ValueError('Invalid voltage_kv')
    digest=hashlib.sha256(wave.astype('<f8').tobytes()).hexdigest()
    bundles=request['evidence']
    if not isinstance(bundles,list) or not 1<=len(bundles)<=3:raise ValueError('One A/B/C evidence bundle each, at most three')
    by_pair={};seen=set();input_issues=[];all_nodes=set()
    for bundle in bundles:
        c._check_cancel(cancel)
        if not isinstance(bundle,dict):raise ValueError('Evidence must be an object')
        method=bundle['algorithm']
        if method not in ('A','B','C') or method in seen:raise ValueError('Unknown or duplicate algorithm bundle')
        seen.add(method)
        c._identifier(bundle['version'],'version')
        if bundle['waveform_sha256']!=digest or bundle['record_id']!=record_id or bundle['phase']!=phase:
            raise ValueError('Evidence sample/record/phase identity mismatch')
        if bundle['timing_basis']!='peak':raise ValueError('D-0.1 requires canonical peak indices; no first/peak mixing')
        if not _finite(bundle['sample_rate_hz']) or bundle['sample_rate_hz']!=fs:raise ValueError('Evidence sampling rate mismatch')
        if type(bundle['complete']) is not bool:raise ValueError('Evidence completeness must be explicit bool')
        if not bundle['complete']:input_issues.append(method+':candidate_set_incomplete')
        other=bundle['segments'];c._cable({'segments':other})
        if len(other)!=len(segments) or any(not math.isclose(a[k],b[k],rel_tol=1e-9,abs_tol=1e-9) for a,b in zip(other,segments) for k in ('length_m','velocity_m_per_us')):
            input_issues.append(method+':geometry_disagreement')
        if bundle.get('reference_plane')!=config.get('reference_plane','unconfirmed'):
            input_issues.append(method+':reference_plane_disagreement')
        rows=bundle['pairs']
        if not isinstance(rows,list) or len(rows)>100000:raise ValueError('Invalid input pair capacity')
        unique=set()
        for row in rows:
            if not isinstance(row,dict):raise ValueError('Invalid pair')
            i,j=row['direct_index'],row['reflection_index']
            if type(i) is not int or type(j) is not int or not 0<=i<j<len(wave):raise ValueError('Invalid native peak indices')
            if (i,j) in unique:raise ValueError('Duplicate pair within algorithm; not extra evidence')
            unique.add((i,j))
            all_nodes.update((i,j))
            if len(all_nodes)>4096:raise ValueError('D distinct pulse capacity exceeded; no partial success')
            reasons=row['reasons']
            if not isinstance(reasons,list) or any(not isinstance(v,str) for v in reasons):raise ValueError('Reasons must be strings')
            if type(row['source_identity_supported']) is not bool:raise ValueError('Explicit source identity flag required')
            # A intentionally leaves scores unavailable on unresolved pairs.
            # Preserve missingness and require review, never reinterpret as 0.
            if row['engineering_score'] is not None and (not _finite(row['engineering_score']) or not 0<=row['engineering_score']<=1):raise ValueError('Score outside 0..1')
            sources=by_pair.setdefault((i,j),[])
            if len(by_pair)>MAX_PAIRS:raise ValueError('D pair capacity exceeded; no partial success')
            sources.append(dict(algorithm=method,version=bundle['version'],engineering_score=row['engineering_score'],reasons=reasons,source_identity_supported=row['source_identity_supported']))
    y,base,sigma=c._noise(wave);features={};results=[]
    if progress:progress(0,len(by_pair))
    for ordinal,((i,j),sources) in enumerate(sorted(by_pair.items()),1):
        c._check_cancel(cancel)
        for index in (i,j):
            if index not in features:features[index]=c._feature(y,index,sigma,fs,s)
        if features[i]['signed_amplitude']==0 or features[j]['signed_amplitude']==0:
            raise ValueError('Supplied pair points to zero-amplitude samples; no fabricated pulse')
        p=c._pair(features[i],features[j],fs,segments,None,s,config.get('reference_plane')=='cable_input')
        reasons=set(p['reasons']);reasons.update(input_issues)
        # Never borrow A/B/C scores as probability; propagate source objections.
        for source in sources:
            reasons.update(source['algorithm']+':'+r for r in source['reasons'])
            if source['engineering_score'] is None:reasons.add(source['algorithm']+':score_unavailable')
        families=sorted({'AB' if q['algorithm'] in ('A','B') else 'C' for q in sources})
        identity=any(q['algorithm']=='B' and q['source_identity_supported'] for q in sources)
        if not identity:reasons.add('reflection_identity_unresolved')
        if voltage is None:reasons.add('voltage_not_provided')
        elif voltage==0:reasons.add('zero_voltage_background')
        p.update(reasons=sorted(reasons),sources=sources,evidence_families=families,
                 physical_confirmed=False,position_m=None,status='review',selected_for_review=False)
        results.append(p)
        if progress and (ordinal%100==0 or ordinal==len(by_pair)):progress(ordinal,len(by_pair))
    # Physical reranking among every supplied alternative for the SAME direct.
    # Source count/old score/known location are not rank inputs.
    groups={}
    for p in results:groups.setdefault(p['direct_index'],[]).append(p)
    for group in groups.values():
        group.sort(key=lambda p:(-p['engineering_score'],p['reflection_index']))
        best=group[0];best['selected_for_review']=True
        if len(group)>1 and best['engineering_score']-group[1]['engineering_score']<s.ambiguity_margin:
            best['reasons'].append('fusion_ambiguous_reflections')
        for p in group[1:]:p['reasons'].append('fusion_competing_reflection')
    # Reuse verified indexed competition, including different direct anchors.
    plausible=[p for p in results if p['shape_score']>=s.minimum_shape and p['candidate_position_m'] is not None]
    c._mark_role_conflicts(plausible,s.ambiguity_margin)
    for p in results:
        p['reasons']=sorted(set(p['reasons']))
        if not p['reasons']:
            p['status']='engineering_candidate';p['position_m']=p['candidate_position_m']
    return dict(algorithm='D',version=VERSION,status='ok',record_id=record_id,phase=phase,
        waveform_sha256=digest,config=config,sample_count=len(wave),noise_sigma=sigma,
        candidates=results,input_issues=input_issues,physical_confirmed=False,
        summary=dict(candidate_count=len(results),engineering_candidates=sum(p['status']=='engineering_candidate' for p in results),
                     selected_for_review=sum(p['selected_for_review'] for p in results)),
        limitations=['D0.1 is evidence fusion, not independently proven superior localization',
                     'A and B share ancestry, agreement is not two independent votes',
                     'Source objections and all supplied alternative pairs retained; no coordinate averaging',
                     'No C full candidate generation here; caller must disclose incomplete submissions',
                     'No physical truth, probability, ML training or automatic reference compensation',
                     'Peak-only; no joint transfer function or calibrated timing uncertainty yet'])

def analyze_json(text, *, progress=None):
    try:return json.dumps(analyze(json.loads(text),progress=progress),ensure_ascii=False,allow_nan=False)
    except (ValueError,TypeError,KeyError,OSError,OverflowError) as e:
        return json.dumps(dict(algorithm='D',version=VERSION,status='error',error_type=type(e).__name__,message=str(e),candidates=[]),ensure_ascii=False,allow_nan=False)

if __name__=='__main__':
    try:
        text=sys.stdin.read() if len(sys.argv)==1 or sys.argv[1]=='-' else Path(sys.argv[1]).read_text(encoding='utf-8-sig')
        result=analyze_json(text,progress=lambda done,total:print(json.dumps(dict(stage='D_waveform_recheck',done=done,total=total)),file=sys.stderr,flush=True));print(result);raise SystemExit(0 if json.loads(result)['status']=='ok' else 2)
    except KeyboardInterrupt:
        print(json.dumps(dict(algorithm='D',version=VERSION,status='cancelled',candidates=[])));raise SystemExit(2)
