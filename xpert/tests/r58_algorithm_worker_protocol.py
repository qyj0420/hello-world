"""Pure offline framed-protocol QA; only the pinned algorithm Python runtime.

Protocol budget fixtures replace analyze() explicitly, never the implementation
or physics rules. Actual complete-ledger equivalence is a separate C# test.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import time


def main():
    args = argparse.ArgumentParser()
    args.add_argument('--runtime', required=True)
    args.add_argument('--qa', required=True)
    options = args.parse_args()
    root = Path(options.runtime).resolve()
    qa = Path(options.qa).resolve()
    qa.mkdir(exist_ok=False)
    python = root / 'python.exe'
    manifest = json.loads((root.parent.parent / 'research-pins.json').read_text(encoding='utf-8-sig'))
    for relative, digest in manifest.items():
        assert hashlib.sha256((root / relative).read_bytes()).hexdigest().lower() == digest.lower()
    environment = {key: os.environ[key] for key in ('SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'LOCALAPPDATA', 'USERPROFILE') if key in os.environ}
    environment['PATH'] = str(root) + ';' + os.path.join(os.environ.get('SystemRoot', r'C:\Windows'), 'System32')
    checks = []

    def run_case(name, payload, code='import q73;q73.serve()', expected=0):
        timer = time.perf_counter()
        process = subprocess.run([str(python), '-I', '-X', 'utf8', '-B', '-c', code],
                                 input=payload, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                 cwd=root, env=environment, timeout=40)
        assert process.returncode == expected, (name, process.returncode, process.stderr[:600])
        assert len(process.stdout) >= 4, (name, process.stdout, process.stderr)
        size, = struct.unpack('<I', process.stdout[:4])
        assert 2 <= size <= 4 * 32 * 1024 * 1024 + 4096
        assert size == len(process.stdout) - 4
        result = json.loads(process.stdout[4:].decode('utf-8'))
        checks.append(dict(name=name, passed=True, elapsed_ms=(time.perf_counter()-timer)*1000,
                           output_bytes=size, returncode=process.returncode))
        return result

    def frame(identity='a'*32, extra=None):
        request = dict(algorithm='E+', transport='float64-le-v1', request_id=identity,
                       config={}, records=[dict(record_id='offline-protocol-fixture', sample_count=32)])
        if extra:
            request.update(extra)
        header = json.dumps(request).encode('utf-8')
        return struct.pack('<I', len(header)) + header + b'\0' * 256

    # Compiled q73 is exercised, not a reimplemented server loop. Verify that
    # Cython resolves this explicit protocol-only analyze fixture dynamically.
    code = "import q73;q73.analyze=lambda r:{'stub':True};q73.serve()"
    output = run_case('compiled_server_fixture_is_explicit', frame(), code)
    assert output['result'] == {'stub': True}
    budget = 32 * 1024 * 1024
    # json.dumps({'text': '...'}) contributes exactly 12 ASCII characters.
    code = "import q73;q73.analyze=lambda r:{'text':'x'*(32*1024*1024-12)};q73.serve()"
    output = run_case('result_character_budget_exact_boundary_accepted', frame(), code)
    assert len(json.dumps(output['result'], ensure_ascii=False)) == budget
    code = "import q73;q73.analyze=lambda r:{'text':'x'*(32*1024*1024-11)};q73.serve()"
    output = run_case('result_character_budget_plus_one_rejected', frame(), code, 2)
    assert output['status'] == 'error' and 'too large' in output['message']
    code = "import q73;q73.analyze=lambda r:{'text':'中'*(16*1024*1024)};q73.serve()"
    output = run_case('UTF8_over_32MiB_preserves_old_character_budget', frame(), code)
    assert len(output['result']['text']) == 16*1024*1024 and checks[-1]['output_bytes'] > budget
    for name, identity in [('invalid_request_identity', 'bad'), ('uppercase_request_identity', 'A'*32)]:
        output = run_case(name, frame(identity), expected=2)
        assert output['status'] == 'error' and 'identity' in output['message']
    output = run_case('bad_transport_error_exits', frame(extra={'transport':'not-a-transport'}), expected=2)
    assert output['status'] == 'error' and 'transport' in output['message']
    output = run_case('header_over_bound_rejected_without_payload', struct.pack('<I', 16*1024*1024+1), expected=2)
    assert output['status'] == 'error' and 'header size' in output['message']
    (qa/'offline-proof.json').write_text(json.dumps(dict(schema='HVX.R58.AlgorithmWorkerProtocolOffline/1',
        passed=True, checks=checks, runtime=str(root), pinned_files_verified=len(manifest),
        algorithm_budget_fixtures_only=True, actual_algorithm_rules_changed=False,
        hardware_sdk_called=False, product_started=False, user_project_written=False), indent=2), encoding='utf-8')
    print(json.dumps(checks))


if __name__ == '__main__':
    main()
