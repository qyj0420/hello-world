"""Regression contracts for C-0.2.0; no use of expected field locations."""
import json
import subprocess
import sys
import tempfile
from pathlib import Path
import unittest
import numpy as np
from owts_pd import localization_c as c
from test_localization_c import config, request, waveform


class RevisionTests(unittest.TestCase):
    def test_json_nonobjects_always_structured_error(self):
        for text in ('null', '[]', 'false', '1', '"C"'):
            with self.subTest(text=text):
                r=json.loads(c.analyze_localization_c_json(text))
                self.assertEqual(r['status'],'error');self.assertEqual(r['records'],[])

    def test_nested_nonobjects_rejected(self):
        for key,value in [('config',None),('config',[]),('calibration',[]),('records',[None])]:
            q=request();q[key]=value
            with self.subTest(key=key,value=value),self.assertRaises(ValueError):c.analyze_project(q)
        with self.assertRaises(ValueError):c.analyze_record(waveform(),config(segments=[None]))

    def test_false_empty_settings_not_silently_defaults(self):
        for value in (False,0,'',[],True):
            with self.subTest(value=value),self.assertRaises(ValueError):c.Settings.parse(value)

    def test_extra_nonfinite_config_rejected(self):
        q=request();q['config']['unrecognized']=float('nan')
        result=json.loads(c.analyze_localization_c_json(json.dumps(q)))
        self.assertEqual(result['status'],'error')

    def test_records_require_real_identifiers(self):
        for key in ('record_id','independence_key'):
            for value in (None,{},True,'',' x'):
                q=request();q['records'][0][key]=value
                with self.subTest(key=key,value=value),self.assertRaises(ValueError):c.analyze_project(q)

    def test_phase_aliases_share_one_physical_phase(self):
        q=request(3);q['records'][0]['phase']='A'
        r=c.analyze_project(q)
        self.assertEqual(len(r['clusters']),1)
        self.assertEqual(r['clusters'][0]['independent_record_count'],3)

    def test_invalid_phase_rejected(self):
        q=request();q['records'][0]['phase']='L4'
        with self.assertRaises(ValueError):c.analyze_project(q)

    def test_boolean_voltage_rejected(self):
        q=request();q['records'][0]['voltage_kv']=True
        with self.assertRaises(ValueError):c.analyze_project(q)

    def test_transitive_independence(self):
        q=request(3)
        q['records'][1]['samples']=q['records'][0]['samples']
        q['records'][1]['independence_key']='shared'
        q['records'][2]['independence_key']='shared'
        r=c.analyze_project(q)
        self.assertEqual(r['clusters'][0]['independent_record_count'],1)

    def test_role_graph_does_not_depend_on_reference_confirmation(self):
        y=waveform(((200,1.,3.),(450,.7,3.),(700,.49,3.)))
        known=c.analyze_record(y,config())['pairs']
        unknown=c.analyze_record(y,config(reference_plane='unconfirmed'))['pairs']
        for a,b in zip(known,unknown):
            self.assertEqual(set(a['reasons']),set(b['reasons'])-{'reference_plane_unconfirmed'})
        p=next(p for p in unknown if (p['direct_index'],p['reflection_index'])==(450,700))
        self.assertIn('pulse_role_conflict',p['reasons'])

    def test_text_missing_separator_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder)/'test.loc'
            for bad in ('2-3','1.2.3','1e2+3','NaN','inf','1e999'):
                p.write_text('Data:\n'+'1\n'*32+bad+'\n',encoding='ascii')
                with self.subTest(token=bad),self.assertRaises(ValueError):c._read_text(p)

    def test_text_valid_scientific_and_comma(self):
        with tempfile.TemporaryDirectory() as folder:
            p=Path(folder)/'test.loc'
            p.write_text('Data:\n'+('1e-2, -.5\n'*16),encoding='ascii')
            np.testing.assert_allclose(c._read_text(p),[.01,-.5]*16)

    def test_candidate_counts_keep_precap_denominator(self):
        y=waveform(tuple((100+i*60,1.,2.) for i in range(18)),n=1400)
        r=c.analyze_record(y,config(settings={'maximum_pulses':2}))
        d=r['detection_counts']
        self.assertGreater(d['raw_peaks_before_cap'],d['raw_peaks_after_prefilter_cap'])
        self.assertEqual(d['retained_lobes'],2)
        self.assertTrue(r['candidate_limited']);self.assertEqual(r['accepted_candidate_count'],0)

    def test_bounded_front_smooth_and_valley_scale_invariance(self):
        for stretch in (1,2,4):
            for gain in (.1,1.,10.):
                for sign in (-1,1):
                    y=np.zeros(500*stretch)
                    y[100*stretch:200*stretch+1]=np.arange(100*stretch+1)/stretch
                    r=c._front(y*sign*gain,200*stretch,gain,40*stretch,210*stretch)
                    self.assertEqual(r['index'],103*stretch)
                    # Valley is below C's adaptive high threshold (6 sigma).
                    # An above-high pedestal cannot provide this quiet guard.
                    y[50*stretch:100*stretch]=4;y[50*stretch:75*stretch]=20
                    y[100*stretch:200*stretch+1]+=4
                    r=c._front(y*sign*gain,200*stretch,gain,40*stretch,210*stretch)
                    self.assertEqual(r['index'],103*stretch)
                    self.assertTrue(r['precursor_valley'])

    def test_front_crop_and_nonfinite_unresolved(self):
        y=np.arange(400,dtype=float)
        self.assertIsNone(c._front(y,200,1,20,50)['index'])
        y[150]=np.nan
        self.assertIsNone(c._front(y,200,1,20,64)['index'])

    def test_front_noisy_monotonic_foot_not_cut_at_peak(self):
        for seed in range(20):
            x=np.arange(600);y=np.random.default_rng(seed).normal(0,.2,600)
            y+=100*np.exp(-.5*((x-300)/12)**2)
            r=c._front(y,300,.2,28,100)
            self.assertIsNotNone(r['index'])
            self.assertLessEqual(abs(r['index']-267),2)

    def test_front_is_not_silently_used_as_peak_coordinate(self):
        r=c.analyze_record(waveform(),config())
        p=next(p for p in r['pairs'] if p['status']=='accepted_candidate')
        self.assertEqual(p['timing_basis'],'peak');self.assertEqual(p['position_m'],240.)
        self.assertIsNotNone(p['bounded_front_delay_s'])

    def test_multiple_sources_at_random_interior_positions(self):
        for separation in (80,130,250,410):
            y=waveform(((200,1.,3.),(200+separation,.4,3.)))
            r=c.analyze_record(y,config())
            p=[p for p in r['pairs'] if p['status']=='accepted_candidate']
            self.assertEqual(len(p),1)
            self.assertAlmostEqual(p[0]['position_m'],400-separation/125e6*160e6/2)

    def test_nan_and_outside_windows_not_returned_as_valid_position(self):
        for value in (float('nan'),float('inf'),-1.,1.):
            self.assertIsNone(c.position_from_delay(value,c._cable(config())))

    def test_cli_returns_error_json_and_nonzero_for_nonobject(self):
        entry=Path(__file__).resolve().parents[1]/'localization_c_entry.py'
        p=subprocess.run([sys.executable,'-B',str(entry),'-'],input='[]',text=True,capture_output=True,timeout=15)
        self.assertEqual(p.returncode,2);self.assertEqual(json.loads(p.stdout)['status'],'error')
        self.assertNotIn('Traceback',p.stderr)

    def test_bipolar_packet_limitations_are_visible(self):
        y=waveform(((200,1.,3.),(210,-.5,4.),(450,.4,4.),(463,-.2,5.)))
        r=c.analyze_record(y,config())
        # Does not claim this proves packet grouping or a unique physical echo.
        self.assertTrue(any(p['signed_amplitude']<0 for p in r['pulses']))
        self.assertTrue(all(np.isfinite(p['delay_s']) for p in r['pairs']))


if __name__=='__main__':unittest.main(verbosity=2)
