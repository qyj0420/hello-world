"""Bounded offline C/D/E/E+ bridge. No file/device/network access in the protocol."""
import json
import sys
import struct
import numpy as np
import q71 as c
import q72 as d


def analyze(request):
    method = request.get('algorithm')
    if method in ('E','E+'):
        return analyze_e(request)
    if method not in ('C', 'D'):
        raise ValueError('Expected C, D, E or E+')
    records = request.get('records')
    if not isinstance(records, list) or len(records) != 1:
        raise ValueError('Exactly one same-input record is required')
    record = records[0]
    result_c = c.analyze_project(dict(request, algorithm='C'))
    if method == 'C':
        return result_c
    phase = {'A': 'L1', 'B': 'L2', 'C': 'L3'}.get(record.get('phase'))
    if phase is None:
        raise ValueError('D requires the actual record phase A, B or C; ALL is not guessed')
    evidence = request.get('ab_evidence')
    if not isinstance(evidence, list) or {x.get('algorithm') for x in evidence} != {'A', 'B'} or len(evidence) != 2:
        raise ValueError('D requires genuine same-record A and B evidence')
    cr = result_c['records'][0]
    for bundle in evidence:
        # A/B were run by the C# adapter while the LOC was write-locked.
        # Their geometry/phase/rate are taken from their actual matrices, not
        # filled from the comparison controls to hide a disagreement.
        if bundle['record_id'] != record['record_id'] or bundle['phase'] != phase:
            raise ValueError('A/B record identity mismatch')
        if bundle['waveform_sha256'].lower() != cr['waveform_sha256'].lower():
            raise ValueError('A/B waveform identity mismatch')
        if bundle['complete'] is not False:
            raise ValueError('Current A/B facade does not expose complete competition graphs')
    hard = {'truncated_or_unresolved_pulse', 'outside_propagation_model',
            'weak_direct', 'low_shape_match', 'width_ratio_mismatch',
            'amplitude_ratio_mismatch', 'polarity_mismatch',
            'calibration_attenuation_mismatch', 'low_engineering_score'}
    proposals = [p for p in cr['pairs'] if not hard.intersection(p['reasons'])]
    evidence = list(evidence) + [dict(algorithm='C', version=c.VERSION,
        record_id=record['record_id'], phase=phase,
        waveform_sha256=cr['waveform_sha256'], sample_rate_hz=request['config']['sample_rate_hz'],
        segments=result_c['config']['segments'], reference_plane=request['config'].get('reference_plane', 'unconfirmed'),
        timing_basis='peak', complete=False,
        pairs=[dict(direct_index=p['direct_index'], reflection_index=p['reflection_index'],
                    engineering_score=p['engineering_score'], reasons=p['reasons'],
                    source_identity_supported=False) for p in proposals])]
    result = d.analyze(dict(algorithm='D', record_id=record['record_id'], phase=phase,
        voltage_kv=record.get('voltage_kv'), samples=record['samples'],
        config=request['config'], evidence=evidence))
    # Keep the complete C alternatives, even those not submitted for D ranking.
    result['source_c_result'] = result_c
    result['source_ab_evidence'] = evidence[:2]
    result['adapter_scope'] = 'AB selected/review pairs and C feature-qualified pairs; all incomplete'
    return result


def analyze_e(request):
    import q79
    import q80
    method=request['algorithm']; records=request.get('records')
    if not isinstance(records,list) or len(records)!=1:
        raise ValueError('E requires exactly one immutable raw record')
    record=records[0]; config=request['config']; settings=config.get('settings',{})
    profile={'e_max_component_nodes':20,'e_max_component_edges':128,'e_solver_state_budget':200000,
        'e_local_relative_margin':.05,'e_global_relative_gap':.02,'e_max_echo_order':3,
        'e_echo_correlation_floor':.8,'detection_mode':'observed_positive_peak_mean','detection_factor':5,
        'amplitude_limit_mode':'user_record_absmax','noise_multiplier':6.,'lower_matching_ratio':.1,
        'envelope_tolerance':16.,'max_incident_attempts_per_record':2048,'suppression_mode':'linear_decay',
        'endpoint_near_factor_percent':1.,'endpoint_width_limit_samples':28,'waveform_filter':'off'}
    if settings.get('manual_threshold_enabled',False):
        profile.update(detection_mode='manual',manual_threshold_pc=settings['manual_threshold_pc'])
    else:
        factor=settings.get('detection_factor',5)
        if type(factor) is not int or not 0<=factor<=1000:
            raise ValueError('E automatic threshold factor must be an integer 0..1000')
        profile['detection_factor']=factor or 5
    profile['envelope_tolerance']=settings.get('envelope_tolerance',16.)
    wave=np.asarray(record['samples'],dtype=np.float64)
    core=q79
    if method=='E+':
        import q86
        core=q86
    result=core.analyze_wave(wave,config['sample_rate_hz'],config['cable_length_m'],
                           config['velocity_m_per_us'],profile,'observed_spectral')
    result.update(status='ok',algorithm=method,version=core.VERSION if method=='E+' else 'E-Eplus-20261010-r01',
                  profile=profile,record_id=record['record_id'],
                  waveform_sha256=q79.hashlib.sha256(wave.tobytes()).hexdigest(),
                  adapter_scope='immutable raw peak timing; equivalent single-speed CABLE geometry; no CAL shape/charge correction',
                  filter_b_applied=False,physical_identity_confirmed=False)
    if method=='E+':
        result['candidate_source_identity']=q86.SOURCE_IDENTITY
        result['known_unresolved']=['Original discovery envelope/signed-tau/near-EOF/reference-plane limitations remain; physical identity is unverified',
            'Historical research replay retains outside-zone mispairing and approximately 28-30% loss of 80m-region candidates; not a physical accuracy acceptance']
    return result


def read_request(stream, size_bytes=None, require_eof=True):
    def exact(count):
        data = bytearray(count)
        view = memoryview(data)
        offset = 0
        while offset < count:
            n = stream.readinto(view[offset:])
            if not n:
                raise ValueError('Incomplete comparison input; no partial record used')
            offset += n
        return data
    size = struct.unpack('<I', exact(4) if size_bytes is None else size_bytes)[0]
    if not 2 <= size <= 16 * 1024 * 1024:
        raise ValueError('Invalid comparison header size')
    request = json.loads(exact(size).decode('utf-8'))
    if request.get('transport') != 'float64-le-v1' or len(request.get('records', [])) != 1:
        raise ValueError('Invalid comparison transport')
    record = request['records'][0]
    count = record.pop('sample_count')
    if type(count) is not int or not 32 <= count <= 20000000:
        raise ValueError('Sample count out of range; no truncation performed')
    record['samples'] = np.frombuffer(exact(count * 8), dtype='<f8')
    if require_eof and stream.read(1):
        raise ValueError('Unexpected trailing comparison data')
    return request


def serve():
    """Serial framed algorithm-only worker; no retained record/result cache.

    Every request contains one unchanged record and a fresh caller identity.
    A malformed request or algorithm failure terminates this worker; callers
    must never accept an old response or silently retry a failed analysis.
    """
    stream = sys.stdin.buffer
    output = sys.stdout.buffer
    while True:
        prefix = stream.read(4)
        if not prefix:
            return
        if len(prefix) != 4:
            raise ValueError('Incomplete worker request frame')
        request_id = None
        try:
            request = read_request(stream, prefix, require_eof=False)
            request_id = request.pop('request_id', None)
            if (not isinstance(request_id, str) or len(request_id) != 32 or
                    any(char not in '0123456789abcdef' for char in request_id)):
                raise ValueError('Invalid worker request identity')
            if request.get('algorithm') not in ('E', 'E+'):
                raise ValueError('Retained worker only accepts E/E+')
            result = analyze(request)
            # Preserve the pre-existing one-shot result character budget. The
            # worker envelope/UTF-8 encoding must not silently shrink it.
            result_json = json.dumps(result, ensure_ascii=False, allow_nan=False)
            if len(result_json) > 32 * 1024 * 1024:
                raise ValueError('Output too large; no partial result returned')
            envelope = json.dumps(dict(schema='HVX.LocationE.Worker/1',
                                       request_id=request_id, status='ok'))
            encoded = (envelope[:-1] + ', "result": ' + result_json + '}').encode('utf-8')
            if len(encoded) > 4 * 32 * 1024 * 1024 + 4096:
                raise ValueError('Worker output frame exceeds its bound')
        except Exception as error:
            encoded = json.dumps(dict(schema='HVX.LocationE.Worker/1', request_id=request_id,
                                      status='error', message=str(error),
                                      error_type=type(error).__name__),
                                 ensure_ascii=False).encode('utf-8')
            if len(encoded) > 128 * 1024:
                encoded = json.dumps(dict(schema='HVX.LocationE.Worker/1', request_id=request_id,
                                          status='error', error_type=type(error).__name__,
                                          message='Worker failed; oversized diagnostic omitted')).encode('utf-8')
            output.write(struct.pack('<I', len(encoded)))
            output.write(encoded)
            output.flush()
            raise SystemExit(2)
        output.write(struct.pack('<I', len(encoded)))
        output.write(encoded)
        output.flush()
        # Explicitly drop the complete prior waveform/ledger between requests.
        del request, result, result_json, envelope, encoded


def main():
    try:
        result = analyze(read_request(sys.stdin.buffer))
        output = json.dumps(result, ensure_ascii=False, allow_nan=False)
        if len(output) > 32 * 1024 * 1024:
            raise ValueError('Output too large; no partial result returned')
        print(output)
    except Exception as error:
        print(json.dumps(dict(status='error', message=str(error), error_type=type(error).__name__)))
        raise SystemExit(2)


if __name__ == '__main__':
    main()
