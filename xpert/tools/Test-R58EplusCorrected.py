"""Pure synthetic E+ source/native/protocol regression; never starts the product."""
from pathlib import Path
import argparse
import hashlib
import importlib.util
import json
import math
import os
import struct
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
PACKAGE = ROOT / 'provenance/eplus-corrected-20261012-r01/package'
IDENTITY = 'F69A945B5FBD67C114F64B736C747CDA51124883B5AAFC22F6F962D652884D2F'
VERSION = 'Eplus-complete-final-pair-composition-20261011-r01'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def waveform(name):
    import numpy as np
    n = 322865 if name == 'long' else 1600
    y = np.zeros(n, dtype=np.float64)
    if name != 'empty':
        values = np.r_[np.linspace(0, -100, 8), np.linspace(-100, 80, 9)[1:], np.linspace(80, 0, 14)[1:]]
        for start, scale in ((300, 1.), (420, .9), (500, .4)):
            y[start:start+len(values)] += scale*values
        if name == 'long':
            for start in range(1300, n-600, 4000):
                y[start:start+len(values)] += values
                y[start+120:start+120+len(values)] += values*.9
    return y


def profile():
    return dict(e_max_component_nodes=20, e_max_component_edges=128, e_solver_state_budget=200000,
        e_local_relative_margin=.05, e_global_relative_gap=.02, e_max_echo_order=3,
        e_echo_correlation_floor=.8, detection_mode='observed_positive_peak_mean', detection_factor=5,
        amplitude_limit_mode='user_record_absmax', noise_multiplier=6., lower_matching_ratio=.1,
        envelope_tolerance=16., max_incident_attempts_per_record=2048, suppression_mode='linear_decay',
        endpoint_near_factor_percent=1., endpoint_width_limit_samples=28, waveform_filter='off')


def child(args):
    start = time.perf_counter()
    import numpy as np
    if args.child == 'reference':
        sys.path.insert(0, str(PACKAGE))
        import eplus_api as module
        module._load_dependencies()
    else:
        import q73
        if args.child != 'legacy':
            import q86
            module = q86
    imported = time.perf_counter()
    y = waveform(args.fixture)
    cfg = profile()
    if args.fixture != 'empty':
        cfg.update(detection_mode='manual', manual_threshold_pc=10.)
    if args.fixture == 'budget':
        cfg['max_incident_attempts_per_record'] = 1
    begin = time.perf_counter()
    if args.child in ('bridge', 'legacy'):
        result = q73.analyze(dict(algorithm=args.algorithm, config=dict(sample_rate_hz=100e6,
            cable_length_m=170., velocity_m_per_us=170., settings=dict(manual_threshold_enabled=args.fixture!='empty',
            manual_threshold_pc=10., detection_factor=5, envelope_tolerance=16.)),
            records=[dict(record_id='synthetic-'+args.fixture, samples=y)]))
    else:
        result = module.analyze_wave(y, 100e6, 170., 170., cfg, args.mode)
    analyzed = time.perf_counter()
    encoded = json.dumps(result, ensure_ascii=False, allow_nan=False)
    end = time.perf_counter()
    print(json.dumps(dict(result=json.loads(encoded), metrics=dict(import_seconds=imported-start,
        analysis_seconds=analyzed-begin, serialization_seconds=end-analyzed, samples=len(y),
        python=sys.version.split()[0], numpy=np.__version__, raw_sha256=hashlib.sha256(y.tobytes()).hexdigest(),
        result_bytes=len(encoded.encode('utf-8'))))))


def compare(a, b, path=''):
    if isinstance(a, dict):
        assert isinstance(b, dict) and set(a)==set(b), (path, 'keys', set(a)^set(b))
        for k in a:
            compare(a[k], b[k], path+'.'+k)
    elif isinstance(a, list):
        assert isinstance(b, list) and len(a)==len(b), (path, 'length')
        for i, (x,y) in enumerate(zip(a,b)):
            compare(x,y,path+'['+str(i)+']')
    elif isinstance(a, float):
        assert isinstance(b,(int,float)) and math.isclose(a,b,abs_tol=1e-10,rel_tol=1e-10), (path,a,b)
    else:
        assert a==b, (path,a,b)


def run(python, kind, fixture, mode='observed_spectral', algorithm='E+'):
    started=time.perf_counter()
    completed=subprocess.run([str(python),'-I','-B',str(Path(__file__).resolve()),'--child',kind,
        '--fixture',fixture,'--mode',mode,'--algorithm',algorithm],capture_output=True,encoding='utf-8',errors='strict',timeout=180)
    assert completed.returncode==0,(kind,fixture,completed.stderr[-3000:])
    value=json.loads(completed.stdout)
    value['metrics']['end_to_end_seconds']=time.perf_counter()-started
    return value


def main(args):
    out=Path(args.output).absolute()
    out.relative_to(ROOT/'qa')
    assert not out.exists(), 'Preserve earlier evidence'
    for node in (out,*out.parents):
        assert not node.is_symlink() and not (hasattr(node,'is_junction') and node.is_junction())
    out.mkdir(parents=True)
    runtime=Path(args.runtime).absolute()
    if runtime!=ROOT/'dependencies/ResearchRuntime/r58-corrected-runtime/r71':
        runtime.relative_to(ROOT/'build')
    python=runtime/'python.exe'
    pins=json.loads((runtime.parent/'research-pins.json').read_text('utf-8-sig'))
    native_receipt=json.loads((runtime.parent/'research-runtime-input-receipt.json').read_text('utf-8-sig'))
    paths=[Path(__file__),ROOT/'modules-src/LocationE/runtime_sources.py',ROOT/'modules-src/ResearchAdapter/research_entry.py',
        ROOT/'modules-src/DataAnalysis/src/ReviewEAnalysis.cs',ROOT/'modules-src/DataAnalysis/src/ReviewERuntime.cs',
        PACKAGE/'eplus_api.py',PACKAGE/'source/current/Eplus/profile.json',runtime.parent/'research-pins.json',
        runtime.parent/'research-runtime-input-receipt.json']+[runtime/name for name in pins]
    generated=(runtime.parent/'research-native/compiler-input' if (runtime.parent/'research-native/compiler-input').is_dir()
               else runtime.parent.parent/'research-native/compiler-input')
    paths+=list(generated.glob('q*.py'))+[generated.parent/'source-receipt.json',generated.parent/'e-source-receipt.json']
    if args.old_runtime:
        paths+=list(Path(args.old_runtime).glob('q*.pyd'))+[Path(args.old_runtime)/'python.exe']
    before={str(p):sha(p) for p in paths}
    pin_start=time.perf_counter()
    assert all(sha(runtime/name)==value for name,value in pins.items())
    pin_seconds=time.perf_counter()-pin_start
    assert native_receipt['eSourceReceipt']['correctedEPlusZipSha256']==IDENTITY
    assert native_receipt['eSourceReceipt']['correctedCompositionSha256'].upper()==sha(PACKAGE/'eplus_api.py')
    sources_spec=importlib.util.spec_from_file_location('r58_sources',ROOT/'modules-src/LocationE/runtime_sources.py')
    sources=importlib.util.module_from_spec(sources_spec);sources_spec.loader.exec_module(sources)
    for name,code in sources.source_inputs().items():
        assert (generated/(name+'.py')).read_text('utf-8')==code,('actual-compiled-source',name)
    checks=0; summaries=[]
    for fixture in ('empty','paired','budget','long'):
        for mode in ('observed_spectral','reference_correlation'):
            reference=run(args.reference_python,'reference',fixture,mode)
            native=run(python,'native',fixture,mode)
            compare(reference['result'],native['result']);checks+=1
            result=native['result']
            assert result['composition']['raw_unchanged'] and result['composition']['all_discovered_events_retained'];checks+=2
            assert all(not e.get('engineering_eligible') or e['final_pair_score']['qualified'] for e in result['events']);checks+=1
            assert all(e['score_source_ordinal']==i for i,e in enumerate(result['events']));checks+=1
            if fixture=='budget':
                assert result['budget_exhausted'] and not any(e.get('engineering_eligible') for e in result['events']);checks+=1
            summaries.append(dict(fixture=fixture,mode=mode,events=len(result['events']),
                reference=reference['metrics'],native=native['metrics']))
    for fixture in ('empty','paired','long'):
        bridge=run(python,'bridge',fixture)
        native=run(python,'native',fixture)
        for key,value in native['result'].items():
            # Existing process envelope uses status=ok for successful execution;
            # no-evaluable-threshold remains in threshold/graph/ledger evidence.
            if key=='status':
                continue
            compare(value,bridge['result'][key],key)
        assert bridge['result']['version']==VERSION and bridge['result']['candidate_source_identity']==IDENTITY;checks+=2
        # Exercise the actual binary protocol, not JSON samples or a product entry.
        import numpy as np
        y=waveform(fixture)
        header=json.dumps(dict(algorithm='E+',transport='float64-le-v1',config=dict(sample_rate_hz=100e6,cable_length_m=170.,
            velocity_m_per_us=170.,settings=dict(manual_threshold_enabled=fixture!='empty',manual_threshold_pc=10.,
            detection_factor=5,envelope_tolerance=16.)),records=[dict(record_id='synthetic-'+fixture,sample_count=len(y))])).encode('utf-8')
        begin=time.perf_counter()
        actual=subprocess.run([str(python),'-I','-B','-c','import q73;q73.main()'],input=struct.pack('<I',len(header))+header+y.astype('<f8').tobytes(),
            capture_output=True,timeout=180)
        elapsed=time.perf_counter()-begin
        assert actual.returncode==0,actual.stderr[-2000:]
        compare(bridge['result'],json.loads(actual.stdout));checks+=1
        old=None
        if args.old_runtime:
            # A pre-existing compiled q73, never the old installed application.
            old=run(Path(args.old_runtime)/'python.exe','legacy',fixture)
            assert old['metrics']['raw_sha256']==bridge['metrics']['raw_sha256'];checks+=1
        summaries.append(dict(fixture=fixture,mode='actual-binary-protocol',bridge=bridge['metrics'],
            protocol_end_to_end_seconds=elapsed,old_compiled_bridge=None if old is None else old['metrics']))
    if args.old_runtime:
        for fixture in ('empty','paired','long'):
            new_e=run(python,'bridge',fixture,algorithm='E')
            old_e=run(Path(args.old_runtime)/'python.exe','legacy',fixture,algorithm='E')
            compare(new_e['result'],old_e['result']);checks+=1
            summaries.append(dict(fixture=fixture,mode='unchanged-E-mode',new=new_e['metrics'],old=old_e['metrics']))
    assert all(sha(p)==digest for p,digest in before.items());checks+=1
    receipt=dict(schema='HVX.R58.CorrectedEPlus.Offline/1',passed=True,checks=checks,inputHashes=before,
        inputSetsUnchanged=True,runtime=str(runtime),zipSha256=IDENTITY,compositionVersion=VERSION,summaries=summaries,
        referenceDependencies='Python 3.12.8, NumPy 2.5.1, SciPy 1.18.0',
        runtimePinRehashSeconds=pin_seconds,runtimePinnedFiles=len(pins),runtimePinnedBytes=sum((runtime/name).stat().st_size for name in pins),
        nativeDependencies='Frozen Python 3.12.14, NumPy 2.3.5, bounded q74 used SciPy signatures',
        earlierAttempt='01 harness expected discovery status at process-envelope status. Existing successful transport status=ok is intentional; test now compares all non-envelope fields and the actual protocol envelope separately. Earlier output retained.',
        physicalAccuracyVerified=False,hardwareAccessed=False,SDKCalled=False,productStarted=False,
        limitations=['Synthetic fixtures only; no labelled field accuracy acceptance.',
            'Legacy/new runtime timings are measured, not an actual GUI latency promise.',
            'Third-party runtime reuse is not a dependency security certification.'])
    (out/'verification.json').write_text(json.dumps(receipt,indent=2,ensure_ascii=False)+'\n','utf-8')
    print('R58_EPLUS_CORRECTED_PASS checks='+str(checks)+' proof='+str(out/'verification.json'))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime');parser.add_argument('--old-runtime');parser.add_argument('--reference-python')
    parser.add_argument('--output');parser.add_argument('--child',choices=['reference','native','bridge','legacy'])
    parser.add_argument('--fixture');parser.add_argument('--mode',default='observed_spectral')
    parser.add_argument('--algorithm',default='E+')
    args=parser.parse_args()
    if args.child:
        child(args)
    else:
        main(args)
