"""Offline E/E+ regression against actual frozen upstream, never hardware."""
from pathlib import Path
import argparse
import ast
import copy
import hashlib
import importlib.util
import json
import math
import sys
import types
import numpy as np
import scipy
from scipy.ndimage import maximum_filter1d
from scipy.signal import find_peaks,correlate

ROOT=Path(__file__).resolve().parent.parent
E=ROOT/'modules-src/LocationE'


def load_sources(native=None):
    spec=importlib.util.spec_from_file_location('e_sources',E/'runtime_sources.py')
    sources=importlib.util.module_from_spec(spec);spec.loader.exec_module(sources)
    adapted=sources.source_inputs()
    if native:
        sys.path.insert(0,str(native))
        for name in adapted:__import__(name)
    else:
        for name,code in adapted.items():
            module=types.ModuleType(name);module.__file__=str(E/(name+'.py'));sys.modules[name]=module
            exec(compile(code,module.__file__,'exec'),module.__dict__)
    source_manifest=json.loads((E/'source-manifest.json').read_text('utf-8'))
    upstream=Path(source_manifest['upstream']['dac_core.py']['path']).parent
    for entry in source_manifest['upstream'].values():
        assert hashlib.sha256(Path(entry['path']).read_bytes()).hexdigest()==entry['sha256']
    sys.path.insert(0,str(upstream))
    spec=importlib.util.spec_from_file_location('original_e',upstream/'dac_core.py')
    original=importlib.util.module_from_spec(spec);spec.loader.exec_module(original)
    plus=Path(source_manifest['upstream']['eplus_research.py']['path']).read_text('utf-8-sig')
    node=next(n for n in ast.parse(plus).body if isinstance(n,ast.FunctionDef) and n.name=='apply_a_interval_suppression')
    original_plus={'copy':copy,'math':math}
    exec(compile(ast.Module(body=[node],type_ignores=[]),'<upstream E+ function>','exec'),original_plus)
    return original,original_plus['apply_a_interval_suppression'],source_manifest


def compare(a,b,path='',tolerance=1e-10):
    if isinstance(a,dict):
        assert set(a)==set(b),(path,'keys')
        for k in a:compare(a[k],b[k],path+'.'+str(k),tolerance)
    elif isinstance(a,(list,tuple)):
        assert len(a)==len(b),(path,'length')
        for i,(v,w) in enumerate(zip(a,b)):compare(v,w,path+'['+str(i)+']',tolerance)
    elif isinstance(a,float):
        assert isinstance(b,(int,float)) and math.isclose(a,b,rel_tol=tolerance,abs_tol=tolerance),(path,a,b)
    else:assert a==b,(path,a,b)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',required=True);parser.add_argument('--historical',type=int,default=4);parser.add_argument('--native')
    args=parser.parse_args();native=Path(args.native).resolve() if args.native else None
    if native:native.relative_to(ROOT/'build')
    original,plus,manifest=load_sources(native)
    import q74,q79,q80
    rng=np.random.default_rng(170810);checks=0;fixtures=[]
    for size in [0,1,2,3,20,21,22,51,256,4096]:
        for mode in range(8):
            wave=rng.integers(-4,5,size).astype(float) if mode<4 else rng.normal(size=size)
            if size:
                np.testing.assert_array_equal(q74.maximum_filter1d(wave,21),maximum_filter1d(wave,21,mode='constant',cval=np.inf));checks+=1
            for height in [None,0.,1.]:
                a,b=q74.find_peaks(wave,height),find_peaks(wave,height=height)
                np.testing.assert_array_equal(a[0],b[0]);assert set(a[1])==set(b[1])
                for k in a[1]:np.testing.assert_array_equal(a[1][k],b[1][k])
                checks+=1
    for n,m in [(1,1),(15,3),(51,24),(1000,101),(100001,151)]:
        a,b=rng.normal(size=n),rng.normal(size=m)
        np.testing.assert_allclose(q74.correlate(a,b),correlate(a,b,mode='full',method='auto'),rtol=1e-10,atol=1e-10);checks+=1
    profile=json.loads((E/'Upstream/profile.json').read_text('utf-8'))
    samples=[]
    for seed in range(6):
        wave=np.random.default_rng(seed).normal(0,.05,12000)
        for center,amp in [(500,100),(750,55),(1700,80),(1950,38),(3600,60),(3850,26)]:
            positions=np.arange(len(wave))-center
            wave+=amp*np.exp(-(positions/6.)**2)
        samples.append(('synthetic-'+str(seed),wave,125e6,160.,160.,None))
    registration=Path(manifest['upstream']['eplus_research.py']['path']).parent/'run-all13-r01/registration.json'
    registered=json.loads(registration.read_text('utf-8'));rejected_inputs=[]
    for project in registered['projects']:
        valid=[f for f in project['files'] if f.get('metadata') and not f.get('metadata_error') and f['metadata'].get('voltage_kv',0)>0 and Path(f['path']).is_file()]
        if not valid:continue
        selected=None
        for candidate in sorted(valid,key=lambda f:f['bytes']):
            path=Path(candidate['path'])
            assert hashlib.sha256(path.read_bytes()).hexdigest()==candidate['sha256']
            try:wave,_=original.read_loc(path,candidate['metadata'])
            except ValueError as error:
                rejected_inputs.append({'path':str(path),'sha256':candidate['sha256'],'upstream_reader_rejection':str(error)});continue
            selected=candidate;break
        if selected is None:continue
        entry=selected;meta=entry['metadata']
        samples.append((project['id'],wave,meta['sample_rate_hz'],meta['cable_length_m'],meta['velocity_m_per_us'],entry))
        if sum(x[-1] is not None for x in samples)>=args.historical:break
    for name,wave,rate,length,speed,entry in samples:
        before=hashlib.sha256(wave.tobytes()).hexdigest()
        results=[]
        for mode in ['observed_spectral','reference_correlation']:
            a=original.analyze_wave(wave,rate,length,speed,profile,mode)
            b=q79.analyze_wave(wave,rate,length,speed,profile,mode)
            compare(a,b);checks+=1
            p,pa=plus(a['events'],wave,a['roundtrip_samples'])
            q,qa=q80.apply_a_interval_suppression(b['events'],wave,b['roundtrip_samples'])
            compare(p,q);compare(pa,qa);checks+=2
            results.append({'mode':mode,'candidates':len(a['events']),'eligible_E':sum(e['engineering_eligible'] for e in a['events']),
                            'eligible_Eplus':sum(e['engineering_eligible'] for e in p),'excluded_Eplus':pa['excluded_pairs'],
                            'candidate_discovery_complete':not a['budget_exhausted']})
        assert hashlib.sha256(wave.tobytes()).hexdigest()==before;checks+=1
        fixtures.append({'id':name,'samples':len(wave),'waveformSha256':before,'sourcePath':entry['path'] if entry else None,
                         'sourceSha256':entry['sha256'] if entry else None,'analyses':results})
    # Exercise an E+-specific exclusion, not merely a renamed E selection.
    raw=np.zeros(1000);events=[]
    for i,j in [(100,200),(250,350)]:
        raw[i],raw[j]=100.,50.;events.append({'incident':i,'reflection':j,'incident_window':[i-2,i+2],
            'reflection_window':[j-2,j+2],'engineering_eligible':True,'e_decision':'global_unique','e_reasons':[]})
    values,ledger=q80.apply_a_interval_suppression(events,raw,400)
    compare((values,ledger),plus(events,raw,400));assert ledger['excluded_pairs']==1 and len(values)==2 and events[1]['engineering_eligible'];checks+=1
    for bad in [np.full(128,np.nan),np.full(128,np.inf)]:
        try:q79.analyze_wave(bad,125e6,160,160,profile,'observed_spectral')
        except ValueError:checks+=1
        else:raise AssertionError('Nonfinite record accepted')
    files=[E/'source-manifest.json',E/'runtime_sources.py',E/'numpy_compat.py',ROOT/'modules-src/ResearchAdapter/research_entry.py',Path(__file__)]
    if native:files+=sorted(native.glob('q*.pyd'))
    report={'schema':'HVX.LocationE.OfflineRegression/1','passed':True,'checks':checks,'tolerance':{'floatRel':1e-10,'floatAbs':1e-10,'indicesAndDecisions':'exact'},
        'runtime':{'python':sys.version.split()[0],'numpy':np.__version__,'scipyReferenceOnly':scipy.__version__},
        'inputHashes':{str(p.relative_to(ROOT)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest().upper() for p in files},
        'fixtures':fixtures,'nativeModulesExecuted':bool(native),'upstreamRejectedInputs':rejected_inputs,'hardwareAccessed':False,'physicalValidation':False,'fullHistoricalReplay':False}
    output=Path(args.output).resolve();output.relative_to(ROOT/'qa');output.parent.mkdir(parents=True,exist_ok=True)
    with output.open('x',encoding='utf-8') as stream:json.dump(report,stream,ensure_ascii=False,indent=2)
    print(json.dumps({'passed':True,'checks':checks,'fixtures':len(fixtures),'proof':str(output)}))


if __name__=='__main__':main()
