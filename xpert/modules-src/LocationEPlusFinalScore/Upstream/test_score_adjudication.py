"""Linear-score ownership contracts; synthetic graph tests, not physical truth."""
from pathlib import Path
import copy
import json
import unittest
import numpy as np
from score_adjudication import adjudicate


def config():
    root = Path(__file__).resolve().parents[1]
    return json.loads((root / "e_dac_a_fusion_20261010_r01/profile.json").read_text(encoding="utf-8"))


def event(n1=80, score=.80, ratio=4.):
    return {"incident":20, "reflection":n1, "incident_window":[16,24], "reflection_window":[n1-4,n1+4], "matching_ratio":ratio, "x_raw_m":10., "q_pc":100., "noise_sigma_pc":1., "final_pair_score":{"score":score,"state":"measured"}}


class LinearScoreTests(unittest.TestCase):
    def test_objective_is_linear_final_score(self):
        events, receipt = adjudicate([event()], np.zeros(256), 125e6, 200, config())
        self.assertAlmostEqual(events[0]["e_objective_weight"], .8)
        self.assertTrue(events[0]["engineering_eligible"])
        self.assertIn("linear", receipt["objective"])

    def test_close_linear_scores_not_distorted_by_odds(self):
        events, receipt = adjudicate([event(score=.80,ratio=4.), event(140,.78,.78/.22)], np.zeros(256), 125e6, 200, config())
        self.assertTrue(all(not e["engineering_eligible"] for e in events))
        self.assertAlmostEqual(events[0]["e_local_relative_gap"], .025)

    def test_legacy_proposed_ratio_not_used_in_ranking(self):
        a = [event(score=.80,ratio=.1001), event(140,.60,999.)]
        before = copy.deepcopy(a)
        events, receipt = adjudicate(a, np.zeros(256), 125e6, 200, config())
        self.assertTrue(events[0]["engineering_eligible"])
        self.assertFalse(events[1]["engineering_eligible"])
        self.assertEqual(a, before)

    def test_unknown_score_not_zero_confidence(self):
        events, receipt = adjudicate([event(score=None)], np.zeros(256), 125e6, 200, config())
        self.assertFalse(events[0]["engineering_eligible"])
        self.assertTrue(events[0]["e_reasons"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
