"""Run unchanged upstream contracts against protected native modules; no devices."""
import importlib.util
import io
import json
import struct
import sys
import types
import unittest
from pathlib import Path
import numpy as np
import q71
import q72
import q73

project = Path(sys.argv[1]).resolve()
source = project / 'modules-src/LocationABCD/CDRelease'
package = types.ModuleType('owts_pd')
package.localization_c = q71
sys.modules['owts_pd'] = package
sys.modules['owts_pd.localization_c'] = q71
sys.modules['localization_d'] = q72
suite = unittest.TestSuite()
for index, path in enumerate(sorted((source/'C/tests').glob('test*.py')) + [source/'D/test_localization_d.py']):
    spec = importlib.util.spec_from_file_location(path.stem, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[path.stem] = module
    spec.loader.exec_module(module)
    suite.addTests(unittest.defaultTestLoader.loadTestsFromModule(module))

class Transport(unittest.TestCase):
    def frame(self, values):
        raw = np.asarray(values, dtype='<f8').tobytes()
        header = json.dumps({'transport':'float64-le-v1', 'records':[{'sample_count':len(values)}]}).encode()
        return struct.pack('<I',len(header)) + header + raw
    def test_exact_float64(self):
        values = np.random.default_rng(4).normal(size=7500000)
        result = q73.read_request(io.BytesIO(self.frame(values)))
        self.assertEqual(result['records'][0]['samples'].tobytes(),values.astype('<f8').tobytes())
    def test_truncated(self):
        with self.assertRaises(ValueError): q73.read_request(io.BytesIO(self.frame(np.zeros(32))[:-1]))
    def test_trailing(self):
        with self.assertRaises(ValueError): q73.read_request(io.BytesIO(self.frame(np.zeros(32))+b'x'))
    def test_oversize_header(self):
        with self.assertRaises(ValueError): q73.read_request(io.BytesIO(struct.pack('<I', 17000000)))

suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(Transport))
class Adapter(unittest.TestCase):
    def request(self):
        value = sys.modules['test_localization_d'].request()
        evidence = value.pop('evidence')[:2]
        for item in evidence: item['complete'] = False
        return dict(algorithm='D', config=value['config'], ab_evidence=evidence,
                    records=[dict(record_id=value['record_id'],phase='A',voltage_kv=10.,samples=value['samples'])])
    def test_real_evidence_retained(self):
        result = q73.analyze(self.request())
        self.assertEqual(result['version'],'D-0.1.1')
        self.assertEqual(len(result['source_ab_evidence']),2)
        self.assertIn('source_c_result',result)
        self.assertFalse(result['physical_confirmed'])
    def test_unknown_phase_not_guessed(self):
        value = self.request(); value['records'][0]['phase']='unknown'
        with self.assertRaises(ValueError): q73.analyze(value)
    def test_foreign_evidence_refused(self):
        value = self.request(); value['ab_evidence'][0]['waveform_sha256']='0'*64
        with self.assertRaises(ValueError): q73.analyze(value)
    def test_invented_complete_refused(self):
        value = self.request(); value['ab_evidence'][0]['complete']=True
        with self.assertRaises(ValueError): q73.analyze(value)
    def test_c_binary_samples_analyzed(self):
        value = self.request(); value['algorithm']='C'
        value['records'][0]['samples']=np.asarray(value['records'][0]['samples'])
        self.assertEqual(q73.analyze(value)['version'],'C-0.3.0')

suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(Adapter))
result = unittest.TextTestRunner(verbosity=2).run(suite)
print('HARDWARE_ACCESSED=False; PHYSICAL_ACCEPTANCE=False')
raise SystemExit(0 if result.wasSuccessful() else 1)
