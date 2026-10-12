import copy
import unittest
from unittest.mock import patch
import numpy as np
from owts_pd import localization_c as c

class Revision03(unittest.TestCase):
    def test_candidate_cap_overflow_not_silent(self):
        # Exactly one pair produced for first direct, then a second pair exists.
        pulses=[dict(peak_index=i) for i in (100,400,700)]
        def pair(a,b,*args):
            return dict(direct_index=a['peak_index'],reflection_index=b['peak_index'],reasons=['low_shape_match'],status='review',engineering_score=.2,candidate_position_m=1.,position_m=None)
        config=dict(sample_rate_hz=100e6,cable_length_m=400.,velocity_m_per_us=200.,settings={'maximum_pairs':1})
        with patch.object(c,'_detect',return_value=(pulses,False,{'raw_peaks_after_prefilter_cap':3})),patch.object(c,'_pair',side_effect=pair):
            r=c.analyze_record(np.arange(1000)*.001,config)
        self.assertTrue(r['candidate_limited']);self.assertIn('candidate_limit_review',r['pairs'][0]['reasons'])

    def test_exact_cap_without_overflow_is_complete(self):
        pulses=[dict(peak_index=i) for i in (100,400)]
        p=dict(direct_index=100,reflection_index=400,reasons=['weak_direct'],status='review',engineering_score=.2,candidate_position_m=1.,position_m=None)
        config=dict(sample_rate_hz=100e6,cable_length_m=400.,velocity_m_per_us=200.,settings={'maximum_pairs':1})
        with patch.object(c,'_detect',return_value=(pulses,False,{'raw_peaks_after_prefilter_cap':2})),patch.object(c,'_pair',return_value=p):
            r=c.analyze_record(np.arange(1000)*.001,config)
        self.assertFalse(r['candidate_limited'])

    def test_fft_full_window_oracle(self):
        y=np.random.default_rng(104).normal(0,.04,1100);y[520]=1
        s=c.Settings(feature_half_window=256)
        f=c._feature(y,520,.04,125e6,s)
        segment=y[264:777]*np.hanning(513)
        power=abs(np.fft.rfft(segment,n=1024))**2;power[0]=0;power/=power.sum()
        np.testing.assert_allclose(f['_power'],power,rtol=1e-13,atol=1e-15)

    def test_role_graph_equivalent_to_quadratic_oracle(self):
        for seed in range(20):
            rng=np.random.default_rng(seed);rows=[dict(direct_index=int(rng.integers(30)),reflection_index=int(rng.integers(30,60)),engineering_score=float(rng.random()),reasons=[]) for _ in range(200)]
            expected=[any(q is not p and {p['direct_index'],p['reflection_index']}.intersection((q['direct_index'],q['reflection_index'])) and q['engineering_score']>=p['engineering_score']-.08 for q in rows) for p in rows]
            c._mark_role_conflicts(rows,.08)
            self.assertEqual(expected,['pulse_role_conflict' in p['reasons'] for p in rows])

if __name__=='__main__':unittest.main()
