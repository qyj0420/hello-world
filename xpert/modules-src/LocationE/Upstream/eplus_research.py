"""E+ offline research replay: E candidate ownership + A-derived time mask.

This is a sidecar research implementation. It never writes to source projects,
the frozen A/B/E algorithms, or the 100/101 application. A mask is an
engineering review exclusion, not proof that a pulse is a physical echo.
"""
from __future__ import annotations

import argparse
import concurrent.futures as cf
import copy
import datetime as dt
import hashlib
import html
import json
import math
import os
from pathlib import Path
import sys
import time

HERE = Path(__file__).resolve().parent
WORK = HERE.parents[1]
DAC_RUN = WORK / 'outputs/dac_complete_research_20261010_r01/run-all13-r01'
DAC_ROOT = WORK / 'outputs/dac_complete_research_20261010_r01'
E_ROOT = WORK / 'outputs/e_dac_a_fusion_20261010_r01'
REG_PATH = DAC_RUN / 'registration.json'
NAME = {'floating':'悬浮放电','tip':'尖端放电','void':'气隙放电','p050537':'050537—050538',
        'yibin':'宜宾测试','jinmao2023':'金茂府','landfill':'垃圾填埋场','north':'二期临电北线',
        'south':'二期南线临时电','baigu2_3':'白古屯2#—3#','baigu5_3':'白古屯5#—3#',
        'baigu5_sub':'白古屯5#—升压站','baigu7_sub':'白古屯7#—升压站'}
MODES = ('observed_spectral', 'reference_correlation')

sys.path.insert(0, str(E_ROOT))
import dac_core as core


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def save(path, value):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def sha(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def source_manifest():
    roots = [E_ROOT, DAC_ROOT]
    files = [E_ROOT / n for n in ('dac_core.py','e_adjudication.py','task17_dac.py','profile.json')]
    files += sorted((E_ROOT/'dac_recovered').glob('*.py'))
    files += [HERE/'eplus_research.py']
    files += [DAC_ROOT/'profile.json', REG_PATH,
              WORK/'outputs/a_dac_suppression_fusion_20261010_r01/source/AstraSixthDacSuppression.cs',
              WORK/'outputs/a_dac_suppression_fusion_20261010_r01/source/DacSuppressionIntervalMath.cs']
    return {str(p): sha(p) for p in files}


def apply_a_interval_suppression(events, raw, round_trip):
    """Apply A6 candidate's post-graph, accepted-anchor-only interval rule."""
    values = [copy.deepcopy(e) for e in events]
    masks, anchors = [], []
    for event_id, event in sorted(enumerate(values), key=lambda pair: (pair[1].get('incident', 0), pair[1].get('reflection', 0))):
        if event.get('engineering_eligible') is not True:
            event['eplus_state'] = event.get('e_decision', 'review')
            continue
        incident, reflection = int(event['incident']), int(event['reflection'])
        iw, rw = event.get('incident_window'), event.get('reflection_window')
        if (not 0 <= incident < reflection < len(raw) or reflection-incident > round_trip
                or not isinstance(iw, (list, tuple)) or len(iw) != 2
                or not isinstance(rw, (list, tuple)) or len(rw) != 2
                or not 0 <= int(iw[0]) <= incident <= int(iw[1]) < len(raw)
                or not 0 <= int(rw[0]) <= reflection <= int(rw[1]) < len(raw)
                or int(iw[1]) >= int(rw[0])):
            raise ValueError('E+ accepted pair has invalid raw indices or overlapping supports')
        blocked = next((m for m in masks if m['start'] <= incident <= m['end'] or m['start'] <= reflection <= m['end']), None)
        event['eplus_original_eligible'] = True
        if blocked is not None:
            event['engineering_eligible'] = False
            event['eplus_state'] = 'a_temporal_mask_review'
            event['eplus_suppressed_by_event_id'] = blocked['anchor_event_id']
            event.setdefault('e_reasons', []).append('A_interval_masked_packet_preserved_for_review_not_proven_echo')
            continue
        event['eplus_state'] = 'eligible_after_A_interval_review'
        left = int(event['incident_window'][0])
        right = int(event['reflection_window'][1])
        formula_end = float(max(right, incident + round_trip))
        a0, a1 = abs(float(raw[incident])), abs(float(raw[reflection]))
        zero = None
        if a0 != a1:
            slope = (a0-a1)/(reflection-incident+1)
            if slope != 0:
                zero = incident + round(a0/slope)  # Python round matches .NET ToEven
                if math.isfinite(zero):
                    formula_end = max(formula_end, float(zero))
                else:
                    zero = None
        end = min(len(raw)-1, int(formula_end))
        mask = {'anchor_event_id':event_id, 'incident':incident, 'reflection':reflection,
                'start':left, 'end':end, 'formula_end_unclipped':formula_end,
                'linear_zero_index':zero, 'round_trip_samples':int(round_trip)}
        masks.append(mask); anchors.append(event_id)
    return values, {'mode':'A6-derived linear_decay post-graph temporal mask',
                    'anchors':len(anchors),'masks':masks,
                    'excluded_pairs':sum(e.get('eplus_state')=='a_temporal_mask_review' for e in values),
                    'rematching':False,'raw_waveform_mutated':False,
                    'physical_echo_identity_confirmed':False}


def process(job):
    project, file_entry, config, out = job
    started = time.monotonic()
    result = {'project':project['id'],'file_id':file_entry['id'],'path':file_entry['path'],
              'source_sha256':None,'ok':False,'modes':{}}
    try:
        path = Path(file_entry['path']); result['source_sha256'] = sha(path)
        if result['source_sha256'].lower() != file_entry['sha256'].lower():
            raise ValueError('LOC differs from registered hash; stale metadata refused')
        if file_entry.get('metadata_error'):
            raise ValueError('Registered metadata error: '+str(file_entry['metadata_error']))
        y, info = core.read_loc(path, file_entry['metadata'])
        result['input'] = info
        result['phase'] = file_entry['metadata']['phase']
        result['voltage_kv'] = file_entry['metadata']['voltage_kv']
        result['zero_voltage_control'] = result['voltage_kv'] <= 0
        for mode in MODES:
            base = core.analyze_wave(y, info['fs_hz'], info['length_m'], info['v_m_us'], config, mode)
            e_events = [copy.deepcopy(e) for e in base.get('events', [])]
            for event in e_events:
                event.update(phase={'A':1,'B':2,'C':3}[result['phase']], voltage_kv=result['voltage_kv'],
                             file_id=file_entry['id'], length_m=info['length_m'],
                             zero_voltage_control=result['zero_voltage_control'])
            eplus_events, suppression = apply_a_interval_suppression(e_events, y, base['roundtrip_samples'])
            result['modes'][mode] = {
                'roundtrip_samples':base['roundtrip_samples'],
                'incident_attempts':base.get('incident_attempts',0),
                'unprocessed_peaks':base.get('unprocessed_peaks',0),
                'budget_exhausted':base.get('budget_exhausted',False),
                'e_diagnostics':base.get('e_adjudication',{}),
                'E_events':e_events, 'Eplus_events':eplus_events,
                'A_suppression':suppression}
        result['ok'] = True
    except Exception as exc:
        result['error'] = type(exc).__name__+': '+str(exc)
    result['elapsed_s'] = time.monotonic()-started
    save(Path(out)/project['id']/'records'/f"{file_entry['id']:04d}.json", result)
    return {k:v for k,v in result.items() if k!='modes'} | ({'mode_counts':{
        m:{'E_events':len(v['E_events']),'Eplus_eligible':sum(e.get('engineering_eligible') is True for e in v['Eplus_events']),
           'Eplus_mask_review':v['A_suppression']['excluded_pairs'],'budget_exhausted':v['budget_exhausted']}
        for m,v in result['modes'].items()}} if result['ok'] else {})


def summarize_and_plot(out, projects, config):
    from draw_plots import draw_chart
    summaries=[]; cards=[]
    for project in projects:
        pid=project['id']; records=[load(p) for p in sorted((out/pid/'records').glob('*.json'))]
        good=[r for r in records if r['ok']]
        lengths=sorted({r['input']['length_m'] for r in good})
        modes_summary={}
        for mode in MODES:
            e_all=[];eplus_all=[]
            for r in good:
                m=r['modes'][mode]
                e_all.extend(m['E_events']); eplus_all.extend(m['Eplus_events'])
            def stats(events):
                rows=[]; params=[]
                for length in sorted({e['length_m'] for e in events}):
                    subset=[e for e in events if e['length_m']==length and not e.get('zero_voltage_control')]
                    evaluated=core.apply_statistics(subset,length,config)
                    rows.extend(evaluated['events']);params.append({'length_m':length,'parameters':evaluated['parameters']})
                return rows,params
            eb,epb=stats(e_all); ex,epx=stats(eplus_all)
            for label,rows in (('E',eb),('Eplus',ex)):
                save(out/pid/f'{mode}-{label}-statistics.json',{'events':rows,'groups':epb if label=='E' else epx,
                    'known_locations_used':False,'physical_accuracy_verified':False})
            def metrics(rows):
                return {'candidates':len(rows),'engineering_eligible':sum(e.get('engineering_eligible') is True for e in rows),
                        'statistical_keep':sum(e.get('visible_candidate') is True for e in rows),
                        'statistical_hide':sum(e.get('visible_candidate') is False for e in rows),
                        'unknown_edge':sum(e.get('visible_candidate') is None for e in rows),
                        'causal_echo_hypotheses':sum(e.get('e_decision')=='causal_echo' for e in rows),
                        'A_interval_review':sum(e.get('eplus_state')=='a_temporal_mask_review' for e in rows)}
            modes_summary[mode]={'E':metrics(eb),'Eplus':metrics(ex)}
            draw_chart(out/pid/f'{mode}-E.png', NAME[pid]+' | E', {mode:eb}, {mode:metrics(eb)}, max(lengths,default=1),len(good),len(records)-len(good))
            draw_chart(out/pid/f'{mode}-Eplus.png', NAME[pid]+' | E+', {mode:ex}, {mode:metrics(ex)}, max(lengths,default=1),len(good),len(records)-len(good))
        summary={'project':pid,'name':NAME[pid],'LOC_total':len(records),'LOC_ok':len(good),'LOC_failed':len(records)-len(good),
                 'matching_modes':modes_summary,'budget_exhausted_records':sum(
                     r['ok'] and any(r['modes'][m]['budget_exhausted'] for m in MODES) for r in records)}
        save(out/pid/'summary.json',summary);summaries.append(summary)
        cards.append('<section><h2>'+html.escape(NAME[pid])+'</h2><p>LOC 成功 '+str(len(good))+' / '+str(len(records)-len(good))+' 失败；统计仅作建议。</p>'+''.join(
            f'<h3>{mode}</h3><div class="grid"><figure><figcaption>E基线（统计前/后）</figcaption><a href="{pid}/{mode}-E.png"><img src="{pid}/{mode}-E.png"></a></figure><figure><figcaption>E+（A时域抑制后，未重配）</figcaption><a href="{pid}/{mode}-Eplus.png"><img src="{pid}/{mode}-Eplus.png"></a></figure></div>' for mode in MODES)+f'<p><a href="{pid}/summary.json">项目汇总</a> · 原始候选及抑制账本见records目录</p></section>')
    page='''<!doctype html><meta charset="utf-8"><title>E+ historical validation</title><style>body{font:16px "Microsoft YaHei",sans-serif;background:#edf2f5;color:#203646;margin:24px}main{max-width:1500px;margin:auto}section{background:white;padding:20px;margin:20px 0;border-radius:12px}.grid{display:grid;grid-template-columns:1fr 1fr;gap:15px}img{width:100%;border:1px solid #ccd6dc}aside{background:#fff0ca;padding:18px;line-height:1.7}</style><main><h1>E+：DAC/E配对 + A来源时域折反射抑制</h1><aside>13个既有历史项目，均已曝光，不是盲验。E+仅在E独占/竞争与因果复核后，采用A6候选的accepted-anchor-only、linear-decay时域掩码；被掩候选转待核，不宣称物理回波已确认。E和E+使用同一原始输入/波对搜索；不按已知位置、簇数或预期结果调参。统计采用研究版ClusterStrength 40显示建议，不等同正式Task28逐样结果或物理真值。原始LOC/辅助数据、A/B/E冻结版本、正式100/101均未改。<br><a href="summary.json">总汇总</a> · <a href="registration.json">输入/源码登记</a> · <a href="failures.json">失败列表</a></aside>'''+''.join(cards)+'</main>'
    (out/'index.html').write_text(page,encoding='utf-8')
    return summaries


def main():
    ap=argparse.ArgumentParser();ap.add_argument('--run',required=True);ap.add_argument('--workers',type=int,default=3);a=ap.parse_args()
    if not a.run.startswith('run-') or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-' for c in a.run):
        raise ValueError('run name must start run- and contain ASCII alphanumeric/hyphen only')
    out=HERE/a.run;out.mkdir(parents=False,exist_ok=False)
    old=load(REG_PATH); eprof=load(E_ROOT/'profile.json'); dacprof=load(DAC_ROOT/'profile.json')
    # E settings are pinned to the existing single-project E profile; M follows the
    # user-approved max(abs(record)) convention, not a hardware range.
    config={**dacprof,**eprof,'detection_mode':'observed_positive_peak_mean','detection_factor':5,
            'amplitude_limit_mode':'user_record_absmax','suppression_mode':'linear_decay'}
    projects=old['projects']; protected=old.get('protected',old.get('protected_auxiliary',{}))
    input_hashes={}
    for project in projects:
        for f in project['files']:
            current=sha(f['path']);
            if current.lower()!=f['sha256'].lower():raise ValueError('Registered LOC changed: '+f['path'])
            input_hashes[f['path']]=current
    aux_hashes={}; aux_changes=[]
    for path,expected in protected.items():
        try:current=sha(path)
        except OSError:current=None
        aux_hashes[path]=current
        if current is not None and current.lower()!=expected.lower():aux_changes.append(path)
    src=source_manifest()
    registration={'created_utc':dt.datetime.now(dt.timezone.utc).isoformat(),'version':'Eplus-r01',
        'historical_validation':str(REG_PATH),'historical_registration_sha256':sha(REG_PATH),
        'projects':projects,'input_loc_hashes':input_hashes,'protected_auxiliary_hashes':aux_hashes,
        'auxiliary_changes':aux_changes,'source_hashes':src,'profile':config,
        'A_method_basis':'AstraSixthDacSuppression.cs + DacSuppressionIntervalMath.cs from stopped A/DAC candidate r03; not default-enabled in frozen A6',
        'E_basis':'E dac_core.py + e_adjudication.py; reused E automatic threshold and packet ownership',
        'historical_projects_not_blind':True,'known_location_or_cluster_input':False,
        'task28_native_replay':False,'physical_accuracy_verified':False}
    save(out/'registration.json',registration)
    jobs=[(p,f,config,str(out)) for p in projects for f in p['files']]
    results=[];start=time.monotonic()
    with cf.ProcessPoolExecutor(max_workers=max(1,min(a.workers,4))) as pool:
        futures=[pool.submit(process,j) for j in jobs]
        for fut in cf.as_completed(futures):
            res=fut.result();results.append(res)
            save(out/'progress.json',{'done':len(results),'total':len(jobs),'elapsed_s':time.monotonic()-start,'results':results})
            if len(results)%10==0 or len(results)==len(jobs):print('PROGRESS',len(results),'/',len(jobs),'seconds',round(time.monotonic()-start),flush=True)
    summaries=summarize_and_plot(out,projects,config)
    # Check immutable inputs and source modules after computation.
    changed_inputs=[p for p,h in input_hashes.items() if sha(p).lower()!=h.lower()]
    changed_sources=[p for p,h in src.items() if sha(p).lower()!=h.lower()]
    failures=[r for r in results if not r['ok']]
    save(out/'failures.json',failures)
    final={'completed_utc':dt.datetime.now(dt.timezone.utc).isoformat(),'elapsed_s':time.monotonic()-start,
        'LOC_attempts':len(results),'LOC_ok':sum(r['ok'] for r in results),'LOC_failed':len(failures),
        'waveform_analyses':sum(2 for r in results if r['ok']),'project_count':len(projects),
        'candidate_discovery_budget_exhausted_records':sum(r['ok'] and any(r['modes'][m]['budget_exhausted'] for m in MODES) for r in results),
        'changed_inputs_after_run':changed_inputs,'changed_sources_after_run':changed_sources,
        'auxiliary_changes_before_run':aux_changes,'projects':summaries,
        'physical_accuracy_verified':False,'task28_native_replay':False}
    save(out/'summary.json',final)
    if changed_inputs or changed_sources:raise RuntimeError('Input/source changed during run; do not treat output as valid')
    print('COMPLETE',json.dumps({k:v for k,v in final.items() if k!='projects'},ensure_ascii=False),flush=True)


if __name__=='__main__':main()
