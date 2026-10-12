"""Offline same-sample timing/profile; no product, worker, SDK or hardware."""
import argparse
import cProfile
import hashlib
import io
import json
import pstats
import os
import struct
import subprocess
from pathlib import Path
import sys
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--qa', required=True)
    parser.add_argument('--sources', required=True)
    parser.add_argument('--mode', choices=('compiled', 'interpreted', 'protocol'), required=True)
    args = parser.parse_args()
    qa = Path(args.qa).resolve(strict=True)
    output = qa / ('algorithm-' + args.mode + '.json')
    assert not output.exists(), 'Keep earlier evidence'
    before = time.perf_counter()
    if args.mode == 'protocol':
        request = json.loads((qa / 'request.json').read_text('utf-8'))
        request['transport'] = 'float64-le-v1'
        header = json.dumps(request).encode('utf-8')
        data = (qa / 'samples.f64le').read_bytes()
        binary = struct.pack('<I', len(header)) + header + data
        expected = json.loads((qa / 'compiled-result.json').read_text('utf-8'))
        env = {key: value for key in ('SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'LOCALAPPDATA', 'USERPROFILE')
               if (value := os.environ.get(key)) is not None}
        env['PATH'] = str(Path(sys.executable).parent)+';'+str(Path(os.environ['SystemRoot'])/'System32')
        trials = []
        for index in range(3):
            begin = time.perf_counter()
            child = subprocess.run([sys.executable, '-I', '-X', 'utf8', '-B', '-c', 'import q73;q73.main()'],
                                   input=binary, capture_output=True, env=env, timeout=180)
            elapsed = 1000*(time.perf_counter()-begin)
            assert child.returncode == 0, child.stderr[-2000:]
            assert json.loads(child.stdout) == expected
            trials.append(dict(index=index, process_full_protocol_ms=elapsed, output_bytes=len(child.stdout)))
            print(json.dumps(trials[-1]), flush=True)
        output.write_text(json.dumps(dict(schema='HVX.R58.SingleLOC.ProtocolTiming/1', trials=trials,
            samples_sha256=hashlib.sha256(data).hexdigest().upper(), complete_ledger_equal=True,
            sdk_called=False, hardware_used=False, product_started=False), indent=2)+'\n', 'utf-8')
        return
    import numpy as np
    if args.mode == 'interpreted':
        sys.path.insert(0, str(Path(args.sources).resolve(strict=True)))
    import q73
    imported = time.perf_counter()
    request = json.loads((qa / 'request.json').read_text('utf-8'))
    data = (qa / 'samples.f64le').read_bytes()
    wave = np.frombuffer(data, dtype='<f8')
    count = request['records'][0].pop('sample_count')
    assert count == len(wave)
    request['records'][0]['samples'] = wave
    reference = json.loads((qa / 'compiled-result.json').read_text('utf-8'))
    input_ready = time.perf_counter()
    trials = []
    for index in range(3 if args.mode == 'compiled' else 1):
        profiler = cProfile.Profile() if args.mode == 'interpreted' else None
        started = time.perf_counter()
        if profiler:
            profiler.enable()
        result = q73.analyze(request)
        if profiler:
            profiler.disable()
        analyzed = time.perf_counter()
        encoded = json.dumps(result, ensure_ascii=False, allow_nan=False)
        serialized = time.perf_counter()
        assert json.loads(encoded) == reference, 'Exact complete ledger mismatch'
        compared = time.perf_counter()
        trial = dict(index=index, analysis_ms=1000*(analyzed-started),
                     json_serialization_ms=1000*(serialized-analyzed),
                     complete_ledger_comparison_ms=1000*(compared-serialized),
                     output_bytes=len(encoded.encode('utf-8')),
                     events=len(result['events']), attempts=len(result['attempts']),
                     eligible=sum(e.get('engineering_eligible', False) for e in result['events']),
                     threshold_pc=result['threshold_pc'])
        if profiler:
            stats = pstats.Stats(profiler)
            profile = []
            for (file, line, function), (primitive, calls, exclusive, cumulative, callers) in stats.stats.items():
                profile.append(dict(file=file, line=line, function=function, primitive_calls=primitive,
                                    calls=calls, exclusive_ms=1000*exclusive, cumulative_ms=1000*cumulative))
            trial['profile_top_cumulative'] = sorted(profile, key=lambda x: -x['cumulative_ms'])[:70]
            trial['profile_top_exclusive'] = sorted(profile, key=lambda x: -x['exclusive_ms'])[:40]
            profiler.dump_stats(str(qa / 'algorithm-interpreted.pstats'))
        trials.append(trial)
        print(json.dumps(dict(mode=args.mode, trial=trial if not profiler else {k: v for k, v in trial.items() if not k.startswith('profile_')})), flush=True)
    receipt = dict(schema='HVX.R58.SingleLOC.AlgorithmProfile/1', mode=args.mode,
                   import_ms=1000*(imported-before), input_preparation_ms=1000*(input_ready-imported),
                   samples=len(wave), samples_sha256=hashlib.sha256(data).hexdigest().upper(),
                   imported_modules={key: str(getattr(value, '__file__', '')) for key, value in sys.modules.items()
                                     if key in {'q'+str(i) for i in range(71, 87)}},
                   input_configuration=request['config'], trials=trials,
                   result_exact_complete_ledger_equal=True, product_started=False, sdk_called=False,
                   hardware_used=False, user_data_written=False,
                   caveat='Interpreted cProfile attributes source hotspots and includes interpreter/profiler overhead; do not report its duration as deployed latency.')
    output.write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+'\n', 'utf-8')


if __name__ == '__main__':
    main()
