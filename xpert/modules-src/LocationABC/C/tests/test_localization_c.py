import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import numpy as np

try:
    from owts_pd import localization_c as c
except ModuleNotFoundError:
    import localization_c as c


def config(**changes):
    result = dict(sample_rate_hz=125e6, cable_length_m=400., velocity_m_per_us=160., reference_plane="cable_input")
    result.update(changes)
    return result


def waveform(pulses=((200, 1., 3.), (450, .4, 5.)), seed=1, n=1300):
    x = np.arange(n)
    a = np.random.default_rng(seed).normal(0, .0005, n)
    for t, magnitude, width in pulses:
        a += magnitude*np.exp(-.5*((x-t)/width)**2)
    return a


def request(records=1):
    return dict(algorithm="C", config=config(), records=[dict(record_id=str(i), phase="L1", samples=waveform(seed=i).tolist()) for i in range(records)])


class LocalizerCTests(unittest.TestCase):
    def test_known_uniform_position_independent_of_saved_indices(self):
        r = c.analyze_record(waveform(), config())
        pairs = [p for p in r["pairs"] if p["status"] == "accepted_candidate"]
        self.assertEqual(len(pairs), 1, r)
        self.assertAlmostEqual(pairs[0]["position_m"], 240.)
        self.assertEqual((pairs[0]["direct_index"], pairs[0]["reflection_index"]), (200,450))

    def test_piecewise_inversion_and_strict_no_clipping(self):
        seg = [dict(length_m=100., velocity_m_per_us=100.), dict(length_m=300., velocity_m_per_us=150.)]
        self.assertAlmostEqual(c.position_from_delay(5e-6, seg), 50.)
        self.assertAlmostEqual(c.position_from_delay(2e-6, seg), 250.)
        self.assertIsNone(c.position_from_delay(6.1e-6, seg))
        self.assertIsNone(c.position_from_delay(-1e-9, seg))

    def test_noise_only(self):
        r = c.analyze_record(np.random.default_rng(0).normal(0,1,16000), config())
        self.assertEqual(r["accepted_candidate_count"], 0)

    def test_constant(self):
        r = c.analyze_record(np.ones(1000), config())
        self.assertEqual(r["pulses"], [])

    def test_nonfinite_and_extreme_rejected(self):
        for v in (np.nan, np.inf, 1e308):
            a = waveform(); a[9] = v
            with self.assertRaises(ValueError): c.analyze_record(a, config())

    def test_second_reflection_is_ambiguous(self):
        a = waveform(((200,1.,3.),(450,.4,5.),(650,.4,5.)))
        r = c.analyze_record(a, config())
        rooted = [p for p in r["pairs"] if p["direct_index"] == 200]
        self.assertFalse(any(p["status"] == "accepted_candidate" for p in rooted))
        self.assertTrue(any("ambiguous_reflections" in p["reasons"] for p in rooted))

    def test_role_conflict_preserved(self):
        a = waveform(((200,1.,3.),(450,.7,3.),(700,.49,3.)))
        r = c.analyze_record(a, config())
        self.assertEqual(r["accepted_candidate_count"], 0)
        self.assertTrue(any("pulse_role_conflict" in p["reasons"] for p in r["pairs"]))

    def test_unknown_reference_is_review(self):
        r = c.analyze_record(waveform(), config(reference_plane="unconfirmed"))
        self.assertEqual(r["accepted_candidate_count"], 0)
        self.assertTrue(any("reference_plane_unconfirmed" in p["reasons"] for p in r["pairs"]))
        self.assertTrue(all(p["position_m"] is None for p in r["pairs"]))

    def test_near_end_is_not_forced_to_zero(self):
        r = c.analyze_record(waveform(((200,1.,3.),(825,.4,5.))), config())
        self.assertEqual(r["accepted_candidate_count"], 0)
        self.assertTrue(any("endpoint_review" in p["reasons"] for p in r["pairs"]))

    def test_overlap_is_review(self):
        r = c.analyze_record(waveform(((200,1.,2.),(209,.8,2.))), config())
        self.assertEqual(r["accepted_candidate_count"], 0)

    def test_opposite_polarity_modes(self):
        wave = waveform(((200,1.,3.),(450,-.4,5.)))
        r = c.analyze_record(wave, config())
        self.assertEqual(r["accepted_candidate_count"], 1)
        r = c.analyze_record(wave, config(settings={"polarity":"same"}))
        self.assertEqual(r["accepted_candidate_count"], 0)

    def test_cap_cannot_accept(self):
        r = c.analyze_record(waveform(((200,1.,3.),(450,.4,5.),(750,.4,5.))), config(settings={"maximum_pulses":2}))
        self.assertTrue(r["candidate_limited"])
        self.assertEqual(r["accepted_candidate_count"], 0)

    def test_roundtrip_and_calibration_validation(self):
        q = request()
        q["calibration"] = dict(samples=waveform(((100,1.,3.),(725,.2,5.))).tolist(), direct_index=100, reflection_index=725, confirmed_pair=True)
        r = c.analyze_project(q)
        self.assertAlmostEqual(r["calibration"]["round_trip_s"], 5e-6)
        q["calibration"]["reflection_index"] = 450
        with self.assertRaises(ValueError): c.analyze_project(q)

    def test_no_assumed_calibration_confirmation(self):
        q = request(); q["calibration"] = {"samples":waveform().tolist()}
        with self.assertRaises(ValueError): c.analyze_project(q)

    def test_three_independent_records(self):
        result = c.analyze_project(request(3))
        self.assertTrue(any(p["independent_record_count"] == 3 for p in result["clusters"]))

    def test_duplicates_not_independent_evidence(self):
        q = request(3)
        for r in q["records"]: r["samples"] = q["records"][0]["samples"]
        r = c.analyze_project(q)
        self.assertTrue(all(p["independent_record_count"] == 1 for p in r["clusters"]))

    def test_repeated_events_in_record_do_not_inflate_support(self):
        q = request(3)
        for r in q["records"]: r["independence_key"] = "same-acquisition"
        result = c.analyze_project(q)
        self.assertTrue(all(p["independent_record_count"] == 1 for p in result["clusters"]))

    def test_phase_isolation(self):
        q = request(3); q["records"][0]["phase"] = "L2"
        r = c.analyze_project(q)
        self.assertEqual({p["phase"] for p in r["clusters"]}, {"L1","L2"})

    def test_zero_voltage_background_not_pd(self):
        q = request(3)
        for r in q["records"]: r["voltage_kv"] = 0
        result = c.analyze_project(q)
        self.assertEqual(result["summary"]["accepted_candidate_count"], 0)
        self.assertEqual(result["clusters"], [])

    def test_json_error_not_zero_position(self):
        r = json.loads(c.analyze_localization_c_json('{"algorithm":"B"}'))
        self.assertEqual(r["status"], "error")
        self.assertEqual(r["records"], [])

    def test_input_not_mutated(self):
        q = request(); before = copy.deepcopy(q)
        c.analyze_project(q)
        self.assertEqual(q, before)

    def test_progress_and_cancel(self):
        progress = []
        c.analyze_project(request(2), progress=lambda d,t:progress.append((d,t)))
        self.assertEqual(progress, [(1,2),(2,2)])
        with self.assertRaises(InterruptedError): c.analyze_project(request(), cancel=lambda:True)

    def test_configuration_errors(self):
        for change in ({"sample_rate_hz":0}, {"velocity_m_per_us":170000}, {"settings":{"bogus":1}}, {"settings":{"maximum_pulses":1.5}}, {"segments":[{"length_m":1,"velocity_m_per_us":160}]}):
            with self.assertRaises(ValueError): c.analyze_record(waveform(), config(**change))

    def test_source_grouping_retains_two_locations(self):
        q = request(6)
        for i in range(3,6): q["records"][i]["samples"] = waveform(((200,1.,3.),(575,.4,5.)), seed=i).tolist()
        result = c.analyze_project(q)
        self.assertEqual(len(result["clusters"]), 2)
        self.assertEqual([p["independent_record_count"] for p in result["clusters"]], [3,3])


if __name__ == "__main__":
    unittest.main(verbosity=2)
