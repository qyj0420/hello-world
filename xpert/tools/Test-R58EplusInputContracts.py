"""Compiled E+ rejection and binary transport contracts; synthetic, no SDK."""
from pathlib import Path
import hashlib
import json
import struct
import subprocess
import sys
import numpy as np
import q86


def main():
    output=Path(sys.argv[1]).absolute()
    root=Path(__file__).resolve().parent.parent
    output.relative_to(root/'qa')
    assert not output.exists()
    for p in (output,*output.parents):
        assert not p.is_symlink() and not (hasattr(p,'is_junction') and p.is_junction())
    checks=[]
    wave=np.zeros(1600)
    for label,raw,config,score in (
        ('nan',np.r_[np.nan,np.zeros(1599)],None,None),
        ('infinity',np.r_[np.inf,np.zeros(1599)],None,None),
        ('known-position',wave,{'known_location_input':True},None),
        ('ranking-only',wave,None,{'qualification_mode':'ranking_only'}),
        ('short',np.zeros(15),None,None),
        ('multidimensional',np.zeros((20,20)),None,None)):
        try:
            q86.analyze_wave(raw,100e6,170.,170.,config,score_config=score)
        except (TypeError,ValueError):
            checks.append(label)
        else:
            raise AssertionError('Invalid input was accepted: '+label)
    base=dict(algorithm='E+',transport='float64-le-v1',config=dict(sample_rate_hz=100e6,cable_length_m=170.,
        velocity_m_per_us=170.,settings={}),records=[dict(record_id='synthetic-invalid',sample_count=1600)])
    encoded=json.dumps(base).encode('utf-8')
    valid=struct.pack('<I',len(encoded))+encoded+wave.astype('<f8').tobytes()
    cases={'partial-samples':valid[:-8], 'trailing-byte':valid+b'x',
        'invalid-header-size':struct.pack('<I',1)+b'x',
        'partial-header':struct.pack('<I',len(encoded))+encoded[:-2]}
    for label,payload in cases.items():
        completed=subprocess.run([sys.executable,'-I','-B','-c','import q73;q73.main()'],
            input=payload,capture_output=True,timeout=30)
        assert completed.returncode==2,(label,completed.returncode)
        result=json.loads(completed.stdout)
        assert result['status']=='error' and 'events' not in result,label
        checks.append(label)
    path=Path(q86.__file__)
    receipt=dict(schema='HVX.R58.EPlus.NativeInputContracts/1',passed=True,checks=len(checks),cases=checks,
        nativeModuleSha256=hashlib.sha256(path.read_bytes()).hexdigest().upper(),nativeVersion=q86.VERSION,
        sourceIdentity=q86.SOURCE_IDENTITY,python=sys.version,numpy=np.__version__,
        testSourceSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest().upper(),
        hardwareAccessed=False,SDKCalled=False,productStarted=False,physicalAccuracyVerified=False)
    output.parent.mkdir(parents=True,exist_ok=True)
    with output.open('x',encoding='utf-8') as stream:
        json.dump(receipt,stream,indent=2)
    print('R58_NATIVE_INPUT_CONTRACTS_PASS='+str(len(checks)))


if __name__=='__main__':
    main()
