"""Named E+ same-engineering-input QA; no product, device or SDK entry point."""
from pathlib import Path
import argparse, hashlib, importlib.util, json, math, struct, subprocess, sys, time, types
import numpy as np

ROOT=Path(__file__).resolve().parent.parent
BUNDLE=Path('C:/Users/qyj04/Documents/TDR算法/outputs/eplus_packet_guard_20261011_r01/algorithm-bundle')
VERSION='Eplus-final-raw-packet-score-r01'
FINAL=ROOT/'modules-src/LocationEPlusFinalScore/Upstream'

def reference_score(result,wave,fs,profile):
    # Independent composition of the frozen handoff, not production bridge code.
    sys.path.insert(0,str(FINAL))
    from pair_score import score_pairs
    from score_adjudication import adjudicate
    from eplus_research import apply_a_interval_suppression
    all_events,ready,scoring=score_pairs(wave,fs,result['events'],result['a_packet_guard']['packets'],result['noise_mad_pc'])
    decisions,graph=adjudicate(ready,wave-result['baseline_pc'],fs,result['roundtrip_samples'],profile)
    after,mask=apply_a_interval_suppression(decisions,wave,result['roundtrip_samples'])
    by_id={e['rescore_event_id']:e for e in after}
    merged=[]
    for event in all_events:
        if event['rescore_event_id'] in by_id:event=by_id[event['rescore_event_id']]
        else:
            event.update(engineering_eligible=False,e_decision='final_pair_qualification_review',eplus_state='final_pair_qualification_review')
            event['e_reasons']=list(event.get('e_reasons',[]))+['final_pair_not_submitted_to_ownership_graph']
        if result.get('budget_exhausted'):
            event['engineering_eligible']=False
            event.setdefault('e_reasons',[]).append('original_discovery_incomplete_no_record_ownership')
        merged.append(event)
    result['upstream_discovery_adjudication']=result['e_adjudication']
    result.update(events=merged,final_pair_scoring=scoring,e_adjudication=graph,eplus_suppression=mask,
        score_version=VERSION,ownership_version='Eplus-final-pair-linear-score-ownership-r01',candidate_discovery_unchanged=True,
        all_prior_event_pairs_retained=len(all_events)==len(merged))
    if result.get('budget_exhausted'):graph.update(candidate_discovery_complete=False,independent_eligible_events=0)
    return result

def bridge_module(native=False):
    if native:return __import__('q73')
    sys.modules['q71']=types.ModuleType('q71');sys.modules['q72']=types.ModuleType('q72')
    spec=importlib.util.spec_from_file_location('private_bridge',ROOT/'modules-src/ResearchAdapter/research_entry.py')
    bridge=importlib.util.module_from_spec(spec);spec.loader.exec_module(bridge);return bridge

def load_adapted(native=None):
    spec=importlib.util.spec_from_file_location('private_sources',ROOT/'modules-src/LocationE/runtime_sources.py')
    source=importlib.util.module_from_spec(spec);spec.loader.exec_module(source)
    for name,code in source.source_inputs().items():
        if native:
            sys.path.insert(0,str(native));__import__(name)
        else:
            m=types.ModuleType(name);sys.modules[name]=m;exec(compile(code,name+'.py','exec'),m.__dict__)
    return sys.modules['q79'],sys.modules['q80'],sys.modules['q81']

def compare(a,b,path=''):
    if isinstance(a,dict):
        assert set(a)==set(b),(path,'keys',set(a)^set(b))
        for k in a:compare(a[k],b[k],path+'.'+str(k))
    elif isinstance(a,list):
        assert len(a)==len(b),(path,'length',len(a),len(b))
        for i,(v,w) in enumerate(zip(a,b)):compare(v,w,path+'['+str(i)+']')
    elif isinstance(a,float):assert isinstance(b,(int,float)) and math.isclose(a,b,rel_tol=1e-10,abs_tol=1e-10),(path,a,b)
    else:assert a==b,(path,a,b)

def child(args):
    f=json.loads(Path(args.fixture).read_text('utf-8'));wave=np.fromfile(f['binary'],dtype='<f8')
    for key in ('fs','length','velocity'):f[key]=struct.unpack('<d',struct.pack('<Q',int(f[key+'Bits'],16)))[0]
    assert len(wave)==f['samples'] and hashlib.sha256(wave.tobytes()).hexdigest().upper()==f['waveformSha256']
    profile=json.loads((ROOT/'modules-src/LocationE/Upstream/profile.json').read_text('utf-8'))
    profile.update(detection_factor=5,envelope_tolerance=16,incident_later_lobe_increase_limit=.20)
    before=hashlib.sha256(wave.tobytes()).hexdigest();start=time.perf_counter()
    if args.child=='reference':
        sys.path.insert(0,str(BUNDLE));from algorithm_api import load_algorithm
        entry=load_algorithm('Eplus','current')
        result=entry.core.analyze_wave(wave,f['fs'],f['length'],f['velocity'],profile,args.mode)
        result=reference_score(result,wave,f['fs'],profile)
    else:
        old,temporal,new=load_adapted(Path(args.native) if args.native else None)
        core=old if args.child=='old' else new
        result=core.analyze_wave(wave,f['fs'],f['length'],f['velocity'],profile,args.mode)
        if args.child=='new':bridge_module(bool(args.native)).apply_eplus_final_score(result,wave,f['fs'],profile)
        else:result['events'],result['eplus_suppression']=temporal.apply_a_interval_suppression(result['events'],wave,result['roundtrip_samples'])
    elapsed=time.perf_counter()-start
    assert hashlib.sha256(wave.tobytes()).hexdigest()==before
    result['_qa_seconds']=elapsed
    with Path(args.output).open('x',encoding='utf-8') as stream:json.dump(result,stream,allow_nan=False)

def main(args):
    directory=Path(args.inputs).resolve();directory.relative_to(ROOT/'qa')
    fixtures=json.loads((directory/'inputs.json').read_text('utf-8'));checks=0;summaries=[]
    for f in fixtures:
        for key in ('fs','length','velocity'):f[key]=struct.unpack('<d',struct.pack('<Q',int(f[key+'Bits'],16)))[0]
    files=[ROOT/'modules-src/LocationE/runtime_sources.py',ROOT/'modules-src/LocationEPlusPacket/source-manifest.json',ROOT/'modules-src/ResearchAdapter/research_entry.py',
        ROOT/'modules-src/DataAnalysis/src/ReviewEAnalysis.cs',ROOT/'modules-src/DataAnalysis/src/ReviewERuntime.cs',ROOT/'modules-src/DataAnalysis/src/PressureAnalysisCache.cs',
        ROOT/'tests/EPlusPacketManagedSmoke.cs',ROOT/'tools/Test-EPlusPacketSameInput.ps1',Path(__file__)]
    files+=sorted((ROOT/'modules-src/LocationEPlusPacket/Upstream').glob('*.py'))
    files+=[ROOT/'modules-src/LocationEPlusFinalScore/source-manifest.json']+sorted(FINAL.glob('*.py'))
    if args.native:
        native=Path(args.native).resolve();native.relative_to(ROOT/'build')
        pins_path=Path(args.native_pins).resolve() if args.native_pins else native.parent/'research-pins.json'
        receipt_path=Path(args.native_receipt).resolve() if args.native_receipt else native.parent/'research-runtime-input-receipt.json'
        files+=sorted(native.glob('q*.pyd'))+[pins_path,receipt_path]
        pins=json.loads(pins_path.read_text('utf-8-sig'))
        receipt=json.loads(receipt_path.read_text('utf-8-sig'))
        for file in sorted(native.glob('q*.pyd')):
            actual=hashlib.sha256(file.read_bytes()).hexdigest().upper()
            assert actual==pins[file.name] and actual==receipt['newNativeModules'][file.name],('final native pin mismatch',file.name)
    hashes={str(p.relative_to(ROOT)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest().upper() for p in files}
    for f in fixtures:
        for mode in ('observed_spectral','reference_correlation'):
            results={}
            for revision in ('old','new','reference'):
                out=directory/(f['id']+'.'+revision+'-'+mode+'.json')
                executable=args.reference_python if revision=='reference' and args.reference_python else sys.executable
                cmd=[executable,'-B',str(Path(__file__).resolve()),'--child',revision,'--fixture',str(directory/(f['id']+'.input.json')),'--mode',mode,'--output',str(out)]
                if args.native and revision!='reference':cmd+=['--native',args.native]
                subprocess.run(cmd,check=True,timeout=180,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
                results[revision]=json.loads(out.read_text('utf-8'))
            a=dict(results['new']);b=dict(results['reference'])
            # Named entry calls the unchanged temporal receipt A_suppression;
            # the integrated adapter's existing schema calls it eplus_suppression.
            if 'A_suppression' in b:b['eplus_suppression']=b.pop('A_suppression')
            for key in ('_qa_seconds','algorithm_name','candidate_version','auxiliary_review'):a.pop(key,None);b.pop(key,None)
            compare(a,b);checks+=1
            assert a['all_prior_event_pairs_retained'] and a['final_pair_scoring']['qualified_graph'];checks+=1
            for e in a['events']:
                if e['engineering_eligible']:assert e['final_pair_score']['qualified'] and e['final_pair_score']['state']=='measured'
                assert e['incident']==e['final_pair_score'].get('final_incident',e['incident'])
                assert e['reflection']==e['final_pair_score'].get('final_reflection',e['reflection'])
            checks+=1
            summaries.append({'id':f['id'],'mode':mode,'samples':f['samples'],'waveformSha256':f['waveformSha256'],
                'oldCandidates':len(results['old']['events']),'newCandidates':len(a['events']),
                'oldSeconds':results['old']['_qa_seconds'],'newSeconds':results['new']['_qa_seconds'],
                'namedCandidateEquivalent':True,'nativeExecuted':bool(args.native)})
    old,temporal,new=load_adapted(Path(args.native) if args.native else None);packet=sys.modules['q82'];sparse=sys.modules['q83']
    for sign in (-1,1):
        for later,expected in ((119.9,True),(120.,False),(120.1,False)):
            y=np.zeros(1600);y[300:305]=sign*100;y[310:315]=-sign*later
            p={'start':280,'end':350,'local_baseline':0.}
            value=packet.incident_representative(y,p,1.)
            assert value['first_lobe_preferred']==expected and value['incident_peak']==(300 if expected else 310);checks+=1
    profile=json.loads((ROOT/'modules-src/LocationE/Upstream/profile.json').read_text('utf-8'));profile.update(detection_mode='manual',manual_threshold_pc=10.)
    for value in (np.nan,np.inf,-np.inf):
        y=np.zeros(1600);y[400]=value
        try:new.analyze_wave(y,125e6,160.,160.,profile,'observed_spectral')
        except ValueError:checks+=1
        else:raise AssertionError('nonfinite sample accepted')
    y=np.zeros(1600);y[-30:]=100;boundary=packet.discover_packets(y,1.,200)[-1]
    assert boundary['clipped'] and not boundary['right_quiet_proven'];checks+=1
    p,d=sparse.discover_seed_packets(y,1.,200,[1599]);assert p and p[-1]['clipped'];checks+=1
    # Native QA executes the actual compiled q73 dispatch, still algorithm-only.
    # Interpreted QA uses inert C/D import names; neither C nor D is invoked.
    bridge=bridge_module(bool(args.native))
    f=fixtures[0];wave=np.fromfile(f['binary'],dtype='<f8');request={'algorithm':'E+','config':{'sample_rate_hz':f['fs'],'cable_length_m':f['length'],'velocity_m_per_us':f['velocity'],'settings':{'detection_factor':5,'envelope_tolerance':16}},'records':[{'record_id':f['id'],'samples':wave}]}
    current=bridge.analyze(request);assert current['version']==VERSION and current['auxiliary_review']['status']=='not_run' and current['filter_b_applied'] is False;checks+=1
    request['algorithm']='E';previous=bridge.analyze(request);assert previous['version']=='E-Eplus-20261010-r01' and 'a_packet_guard' not in previous;checks+=1
    # Frozen scoring/linear graph contracts, run against the same native slots.
    import unittest
    sys.modules['pair_score']=sys.modules['q84'];sys.modules['score_adjudication']=sys.modules['q85']
    suites=[];fixture_module=None
    for name in ('test_pair_score','test_score_adjudication'):
        spec=importlib.util.spec_from_file_location(name,FINAL/(name+'.py'));test=importlib.util.module_from_spec(spec);spec.loader.exec_module(test)
        if name=='test_pair_score':fixture_module=test
        if name=='test_score_adjudication':test.config=lambda:profile
        suites.append(unittest.defaultTestLoader.loadTestsFromModule(test))
    unit=unittest.TextTestRunner(verbosity=1).run(unittest.TestSuite(suites));assert unit.wasSuccessful();checks+=unit.testsRun
    # Bridge-only safety contracts which are not proved by isolated score tests.
    import copy
    raw,packets,events=fixture_module.fixture()
    bad=copy.deepcopy(events[0]);bad['reflection_window']=[270,280]
    base=dict(events=events+[bad],a_packet_guard={'packets':packets},noise_mad_pc=1.,
        baseline_pc=0.,roundtrip_samples=400,e_adjudication={'old_graph_only':True},budget_exhausted=False)
    original=copy.deepcopy(base)
    full=bridge.apply_eplus_final_score(copy.deepcopy(base),raw,125e6,profile)
    assert len(full['events'])==2 and full['events'][1]['final_pair_score']['score'] is None
    assert not full['events'][1]['engineering_eligible'] and base==original;checks+=1
    base['budget_exhausted']=True
    partial=bridge.apply_eplus_final_score(copy.deepcopy(base),raw,125e6,profile)
    assert not any(e['engineering_eligible'] for e in partial['events']) and partial['e_adjudication']['independent_eligible_events']==0;checks+=1
    base.update(events=events,noise_mad_pc=20.,budget_exhausted=False)
    failure=bridge.apply_eplus_final_score(base,raw,125e6,profile)['events'][0]
    assert failure['final_pair_score']['score']>.8 and not failure['final_pair_score']['qualified'] and not failure['engineering_eligible'];checks+=1
    for p in files:assert hashlib.sha256(p.read_bytes()).hexdigest().upper()==hashes[str(p.relative_to(ROOT)).replace('\\','/')]
    report={'schema':'HVX.EPlusPacket.SameInputReplay/1','passed':True,'checks':checks,'inputHashes':hashes,'fixtures':summaries,
        'reader':'actual managed parser export; identical float64 little-endian samples to both revisions','tolerance':{'floatAbs':1e-10,'floatRel':1e-10,'indicesAndDecisions':'exact'},
        'auxiliaryLowpassExecuted':False,'Task28ResearchStatisticsExecuted':False,'nativeExecuted':bool(args.native),
        'runtime':{'python':sys.version.split()[0],'numpy':np.__version__,'referencePython':args.reference_python or sys.executable},
        'hardwareAccessed':False,'productStarted':False,'physicalValidation':False,
        'knownUnresolved':['Named-candidate observed_spectral near-EOF modulo context retained; historical delivery identified 7 affected candidates. No causal/physical validation claimed.']}
    out=directory/('verification-native.json' if args.native else 'verification-python.json')
    with out.open('x',encoding='utf-8') as stream:json.dump(report,stream,ensure_ascii=False,indent=2)
    print(json.dumps({'passed':True,'checks':checks,'fixtures':len(fixtures),'proof':str(out)}))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--inputs');p.add_argument('--native');p.add_argument('--reference-python');p.add_argument('--native-pins');p.add_argument('--native-receipt');p.add_argument('--child',choices=['old','new','reference']);p.add_argument('--fixture');p.add_argument('--mode');p.add_argument('--output');a=p.parse_args()
    if a.child:child(a)
    else:main(a)
