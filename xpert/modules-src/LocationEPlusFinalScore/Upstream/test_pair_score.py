"""Synthetic unit/contract checks; not field accuracy or independent truth."""
import copy
import math
import unittest
import numpy as np
from pair_score import score_pairs,settings


def fixture(scale=.5,polarity=1,shift=0,baseline=0.):
    raw=np.zeros(600)+baseline
    t=np.arange(-30,31,dtype=float)
    pulse=-np.exp(-(t/5)**2)+.75*np.exp(-((t-12)/7)**2)
    raw[70:131]+=100*pulse
    raw[270+shift:331+shift]+=scale*polarity*100*pulse
    packets=[{'start':70,'end':130,'right_quiet_proven':True,'clipped':False,
              'unresolved_overlap':False,'quiet_guards':[[50,65],[135,150]]},
             {'start':270,'end':330+max(0,shift),'right_quiet_proven':True,'clipped':False,
              'unresolved_overlap':False,'quiet_guards':[[250,265],[340,355]]}]
    event={'incident':100,'reflection':300,'incident_window':[70,130],
           'reflection_window':[270,330+max(0,shift)],'matching_ratio':.2,
           'proposed_reflection':350,'x_raw_m':81.7,'q_pc':float(raw[100]),
           'noise_sigma_pc':1.,'engineering_eligible':True}
    return raw,packets,[event]


class FinalPairScoreTests(unittest.TestCase):
    def run_fixture(self,**kwargs):
        raw,p,e=fixture(**kwargs)
        return score_pairs(raw,125e6,e,p,1.)

    def test_identical_scaled_complete_shape(self):
        a,g,d=self.run_fixture()
        m=a[0]['final_pair_score']
        self.assertGreater(m['final_absolute_ncc'],.99999)
        self.assertTrue(m['qualified'])
        self.assertEqual(len(g),1)
        self.assertFalse(d['physical_accuracy_verified'])

    def test_inverted_polarity_allowed_and_recorded(self):
        a,_,_=self.run_fixture(polarity=-1)
        m=a[0]['final_pair_score']
        self.assertLess(m['final_signed_ncc'],-.99999)
        self.assertEqual(m['preferred_polarity'],-1)
        self.assertTrue(m['qualified'])

    def test_raw_anchors_and_input_objects_unchanged(self):
        raw,p,e=fixture(); original=raw.copy();events=copy.deepcopy(e);packets=copy.deepcopy(p)
        a,g,d=score_pairs(raw,125e6,e,p,1.)
        np.testing.assert_array_equal(raw,original)
        self.assertEqual(e,events);self.assertEqual(p,packets)
        self.assertEqual((g[0]['incident'],g[0]['reflection']),(100,300))
        self.assertEqual(g[0]['x_raw_m'],81.7)
        self.assertEqual(g[0]['q_pc'],events[0]['q_pc'])
        self.assertTrue(d['raw_unchanged'])

    def test_dc_offset_local_guard_invariance(self):
        a,_,_=self.run_fixture(baseline=0.)
        b,_,_=self.run_fixture(baseline=2345.)
        self.assertAlmostEqual(a[0]['final_pair_score']['score'],b[0]['final_pair_score']['score'],places=10)
        self.assertEqual(b[0]['final_pair_score']['incident']['baseline_pc'],2345.)

    def test_old_ratio_does_not_score_final_pair(self):
        raw,p,e=fixture();a,_,_=score_pairs(raw,125e6,e,p,1.)
        e[0]['matching_ratio']=100000.;e[0]['proposed_reflection']=200
        b,_,_=score_pairs(raw,125e6,e,p,1.)
        self.assertEqual(a[0]['final_pair_score']['score'],b[0]['final_pair_score']['score'])
        self.assertEqual(b[0]['final_pair_score']['old_proposed_matching_ratio'],100000.)

    def test_known_position_does_not_score(self):
        raw,p,e=fixture();a,_,_=score_pairs(raw,125e6,e,p,1.)
        e[0]['x_raw_m']=999999.;e[0]['known_defect']=80.
        b,_,_=score_pairs(raw,125e6,e,p,1.)
        self.assertEqual(a[0]['final_pair_score']['score'],b[0]['final_pair_score']['score'])

    def test_final_lobe_snr_uses_larger_global_sigma(self):
        raw,p,e=fixture(scale=.02)
        a,g,_=score_pairs(raw,125e6,e,p,2.)
        m=a[0]['final_pair_score']
        self.assertAlmostEqual(m['reflection']['effective_noise_sigma_pc'],2.)
        self.assertFalse(m['qualified']);self.assertEqual(g,[])
        self.assertIn('final_selected_lobe_local_snr_below_research_floor',m['rejection_reasons'])

    def test_ranking_only_is_explicit_control(self):
        raw,p,e=fixture(scale=.02)
        a,g,d=score_pairs(raw,125e6,e,p,2.,{'qualification_mode':'ranking_only'})
        self.assertEqual(len(g),1)
        self.assertFalse(a[0]['final_pair_score']['qualified'])
        self.assertTrue(g[0]['score_graph_control_only'])
        self.assertFalse(d['qualified_graph'])

    def test_quiet_unknown_not_zero_score_or_control_accepted(self):
        raw,p,e=fixture();p[1]['quiet_guards']=[None,None]
        a,g,_=score_pairs(raw,125e6,e,p,1.,{'qualification_mode':'ranking_only'})
        self.assertIsNone(a[0]['final_pair_score']['score'])
        self.assertEqual(a[0]['final_pair_score']['state'],'unavailable')
        self.assertEqual(g,[])

    def test_incomplete_support_cannot_cherry_pick(self):
        raw,p,e=fixture();e[0]['reflection_window']=[298,304]
        a,g,_=score_pairs(raw,125e6,e,p,1.)
        self.assertIsNone(a[0]['final_pair_score']['score']);self.assertEqual(g,[])

    def test_finite_lag_is_diagnostic_not_new_anchor(self):
        a,g,_=self.run_fixture(shift=3)
        m=a[0]['final_pair_score']
        self.assertGreater(m['final_absolute_ncc'],m['direct_absolute_ncc'])
        self.assertLessEqual(abs(m['diagnostic_alignment_samples']),4)
        self.assertEqual(g[0]['reflection'],300)
        self.assertFalse(m['alignment_changes_location'])

    def test_graph_weight_equals_clipped_score(self):
        raw,p,e=fixture(scale=1.)
        a,g,_=score_pairs(raw,125e6,e,p,0.)
        m=a[0]['final_pair_score'];ratio=g[0]['matching_ratio']
        self.assertTrue(math.isfinite(ratio))
        self.assertAlmostEqual(ratio/(1+ratio),min(1-1e-9,max(1e-9,m['score'])),places=12)

    def test_frequency_units_and_diagnostic_stretch_not_gate(self):
        a,_,_=self.run_fixture()
        m=a[0]['final_pair_score']
        self.assertTrue(0<m['incident_frequency']['centroid_hz']<62.5e6)
        self.assertFalse(m['stretch_used_for_qualification'])
        self.assertEqual(len(m['stretch_diagnostics']),4)

    def test_boundary_unknown_retains_row(self):
        raw,p,e=fixture();p[0]['clipped']=True
        a,g,d=score_pairs(raw,125e6,e,p,1.)
        self.assertEqual(len(a),1);self.assertEqual(g,[])
        self.assertTrue(d['all_candidates_retained'])

    def test_nonfinite_raw_rejects(self):
        raw,p,e=fixture();raw[0]=np.nan
        with self.assertRaises(ValueError):score_pairs(raw,125e6,e,p,1.)

    def test_config_unknown_and_invalid_reject(self):
        with self.assertRaises(ValueError):settings({'known_position':80})
        with self.assertRaises(ValueError):settings({'minimum_shape_correlation':True})
        with self.assertRaises(ValueError):settings({'snr_weight':.5})


if __name__=='__main__':unittest.main()
