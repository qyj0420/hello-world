import copy,hashlib,json,unittest
from unittest.mock import patch
import numpy as np
import localization_d as d

def request():
    x=np.arange(1200);w=np.random.default_rng(14).normal(0,.001,1200)
    w+=np.exp(-.5*((x-200)/3)**2)+.5*np.exp(-.5*((x-450)/5)**2)
    digest=hashlib.sha256(w.astype('<f8').tobytes()).hexdigest()
    seg=[dict(length_m=400.,velocity_m_per_us=160.)]
    pair=dict(direct_index=200,reflection_index=450,engineering_score=.9,reasons=[],source_identity_supported=True)
    evidence=[dict(algorithm=m,version='test',record_id='r1',phase='L1',waveform_sha256=digest,sample_rate_hz=125e6,
        timing_basis='peak',segments=seg,reference_plane='cable_input',complete=True,pairs=[copy.deepcopy(pair)]) for m in 'ABC']
    return dict(algorithm='D',record_id='r1',phase='L1',voltage_kv=10.,samples=w.tolist(),
        config=dict(sample_rate_hz=125e6,cable_length_m=400.,velocity_m_per_us=160.,reference_plane='cable_input'),evidence=evidence)

class TestD(unittest.TestCase):
    def test_same_pair_one_result_not_three(self):
        r=d.analyze(request());self.assertEqual(len(r['candidates']),1)
        self.assertEqual(r['candidates'][0]['evidence_families'],['AB','C']);self.assertFalse(r['physical_confirmed'])
    def test_score_not_average_of_old_scores(self):
        q=request();r=d.analyze(q)
        for e in q['evidence']:e['pairs'][0]['engineering_score']=0.
        self.assertEqual(r['candidates'][0]['engineering_score'],d.analyze(q)['candidates'][0]['engineering_score'])
    def test_indices_recompute_coordinate(self):
        p=d.analyze(request())['candidates'][0];self.assertEqual(p['candidate_position_m'],240.)
    def test_conflicting_candidates_not_averaged(self):
        q=request();q['evidence'][1]['pairs'][0]['reflection_index']=550
        r=d.analyze(q);self.assertEqual(len(r['candidates']),2)
        self.assertEqual({p['candidate_position_m'] for p in r['candidates']},{240.,176.})
    def test_foreign_hash(self):
        q=request();q['evidence'][0]['waveform_sha256']='bad'
        with self.assertRaises(ValueError):d.analyze(q)
    def test_foreign_phase(self):
        q=request();q['evidence'][0]['phase']='L2'
        with self.assertRaises(ValueError):d.analyze(q)
    def test_first_peak_not_mixed(self):
        q=request();q['evidence'][0]['timing_basis']='first_arrival'
        with self.assertRaises(ValueError):d.analyze(q)
    def test_wrong_rate(self):
        q=request();q['evidence'][0]['sample_rate_hz']=100e6
        with self.assertRaises(ValueError):d.analyze(q)
    def test_duplicate_bundle(self):
        q=request();q['evidence'][1]['algorithm']='A'
        with self.assertRaises(ValueError):d.analyze(q)
    def test_duplicate_pair(self):
        q=request();q['evidence'][0]['pairs']*=2
        with self.assertRaises(ValueError):d.analyze(q)
    def test_incomplete_keeps_points_not_acceptance(self):
        q=request();q['evidence'][2]['complete']=False;r=d.analyze(q)
        self.assertEqual(len(r['candidates']),1);self.assertEqual(r['summary']['engineering_candidates'],0)
    def test_reference_unknown(self):
        q=request();q['config']['reference_plane']='unconfirmed'
        for e in q['evidence']:e['reference_plane']='unconfirmed'
        r=d.analyze(q);self.assertEqual(r['summary']['engineering_candidates'],0)
        self.assertEqual(r['candidates'][0]['candidate_position_m'],240.)
    def test_geometry_disagreement_retained(self):
        q=request();q['evidence'][0]['segments']=[dict(length_m=410.,velocity_m_per_us=160.)]
        r=d.analyze(q);self.assertIn('A:geometry_disagreement',r['input_issues'])
        self.assertEqual(r['summary']['engineering_candidates'],0)
    def test_zero_voltage(self):
        q=request();q['voltage_kv']=0;self.assertEqual(d.analyze(q)['summary']['engineering_candidates'],0)
    def test_unresolved_identity_no_majority_promotion(self):
        q=request()
        for e in q['evidence']:e['pairs'][0]['source_identity_supported']=False
        r=d.analyze(q);self.assertEqual(r['summary']['engineering_candidates'],0)
    def test_source_reason_survives(self):
        q=request();q['evidence'][0]['pairs'][0]['reasons']=['unresolved_guard']
        self.assertIn('A:unresolved_guard',d.analyze(q)['candidates'][0]['reasons'])
    def test_nan_error_not_zero(self):
        q=request();q['samples'][0]=float('nan');r=json.loads(d.analyze_json(json.dumps(q)))
        self.assertEqual(r['status'],'error');self.assertEqual(r['candidates'],[])
    def test_outside_not_clipped(self):
        q=request()
        for e in q['evidence']:e['pairs'][0]['reflection_index']=1100
        r=d.analyze(q);self.assertIsNone(r['candidates'][0]['candidate_position_m'])
    def test_cancel(self):
        with self.assertRaises(InterruptedError):d.analyze(request(),cancel=lambda:True)
    def test_gain_and_polarity(self):
        q=request();before=d.analyze(q)
        q['samples']=(np.array(q['samples'])*-5).tolist();h=hashlib.sha256(np.array(q['samples'],dtype='<f8').tobytes()).hexdigest()
        for e in q['evidence']:e['waveform_sha256']=h
        after=d.analyze(q);self.assertAlmostEqual(before['candidates'][0]['engineering_score'],after['candidates'][0]['engineering_score'])
    def test_stable_json(self):
        q=request();self.assertEqual(d.analyze_json(json.dumps(q)),d.analyze_json(json.dumps(q)))
    def test_zero_waveform_error(self):
        q=request();q['samples']=[0.]*1200;h=hashlib.sha256(np.zeros(1200,dtype='<f8').tobytes()).hexdigest()
        for e in q['evidence']:e['waveform_sha256']=h
        self.assertEqual(json.loads(d.analyze_json(json.dumps(q)))['status'],'error')
    def test_unknown_score_preserved_not_zero(self):
        q=request();q['evidence'][0]['pairs'][0]['engineering_score']=None
        r=d.analyze(q);self.assertEqual(r['status'],'ok');self.assertIsNone(r['candidates'][0]['sources'][0]['engineering_score'])
        self.assertIn('A:score_unavailable',r['candidates'][0]['reasons'])
    def test_pair_limit_no_partial_success(self):
        q=request();q['evidence'][1]['pairs'][0]['reflection_index']=550
        with patch.object(d,'MAX_PAIRS',1),self.assertRaises(ValueError):d.analyze(q)

if __name__=='__main__':unittest.main()
