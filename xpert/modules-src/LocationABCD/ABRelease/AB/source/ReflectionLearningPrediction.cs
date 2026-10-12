using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

public sealed partial class ReflectionLearningEngine
{
    public sealed class Candidate
    {
        public int index;
        public double position_m, score, positive_similarity, negative_similarity;
        public int manual_neighbors, bootstrap_neighbors;
        public int manual_positive_neighbors, manual_negative_neighbors, negative_independent_neighbors;
    }
    internal sealed class Neighbor
    { internal Sample Sample; internal double Similarity; }
    internal static double Similarity(double[] query, double[] sample)
    {
        double error = 0, weight = 0;
        for (int i = 0; i < FeatureCount; i++)
        { double w = i < 12 ? 1 : .25; double delta = query[i] - sample[i]; error += w * delta * delta; weight += w; }
        return Math.Exp(-4 * error / weight);
    }
    internal static double Evidence(List<Neighbor> neighbors, string label, out int manual, out int weak, out double strength)
    {
        var selected = neighbors.Where(n => n.Sample.pair_label == label)
            .GroupBy(n => n.Sample.source_sha256).Select(g => g.OrderByDescending(n => n.Similarity).First())
            .OrderByDescending(n => n.Similarity).Take(5).ToArray();
        manual = selected.Count(n => Human(n.Sample)); weak = selected.Length - manual;
        double sum = 0, total = 0;
        foreach (var n in selected) { double w = n.Sample.label_weight; sum += w * n.Similarity; total += w; }
        // A weak event carries 0.15 of one manual event even when it is the
        // only member of its class. Dividing by the class's own weight alone
        // would cancel the weak-label attenuation completely.
        strength = Math.Min(1, total);
        return total > 0 ? sum / total : 0;
    }
    internal static double WeightedMedian(List<KeyValuePair<double, double>> entries)
    {
        var sorted = entries.OrderBy(e => e.Key).ToArray(); double half = sorted.Sum(e => e.Value) / 2, mass = 0;
        foreach (var e in sorted) { mass += e.Value; if (mass >= half) return e.Key; }
        return sorted.Last().Key;
    }
    /// <summary>Ranks actual raw peaks in the exact cable window. Suggestions are never physical acceptance.</summary>
    public string Suggest(double[] rawSignal, string queryJson, string modelJson)
    {
        try
        {
            var c = Context(rawSignal, queryJson); var m = ReadModel(modelJson); var x = c.Input;
            var records = m.records.Where(s => s.scope_key == x.scope_key && s.project_key == x.project_key && s.calibration_sha256 == x.calibration_sha256 && s.source_sha256 != x.source_sha256 && s.timing_mode == x.timing_mode).ToList();
            int manualCount = records.Where(Human).Select(EventKey).Distinct().Count(), bootstrapCount = records.Where(s => !Human(s)).Select(EventKey).Distinct().Count();
            int positiveRecords = records.Where(s => s.pair_label == "positive").Select(s => s.source_sha256).Distinct().Count();
            bool enough = positiveRecords >= 3;
            var candidates = new List<Candidate>(); var skip = new Dictionary<string, int>();
            bool peakMode = x.timing_mode == "peak";
            bool complete = (long)c.Incident + (long)Math.Floor(c.ExactSpan) + c.ShapeRadius + 1 < rawSignal.Length;
            int right = Math.Min(rawSignal.Length - c.ShapeRadius - 2, (int)Math.Min(Int32.MaxValue, (long)c.Incident + (long)Math.Floor(c.ExactSpan)));
            int left = Math.Max(c.Incident + c.ShapeRadius + 1, c.ShapeRadius);
            int examined = 0; bool budgetComplete = true;
            // The query's currently selected reflection and historical absolute
            // delays are deliberately not used to generate / score candidates.
            for (int i = left; peakMode && i <= right; i++)
            {
                double a = Math.Abs(rawSignal[i] - c.Baseline);
                if (a <= 0 || a < c.Noise * 3 || a < Math.Abs(rawSignal[i - 1] - c.Baseline) || a <= Math.Abs(rawSignal[i + 1] - c.Baseline)) continue;
                if (++examined > 128) { budgetComplete = false; break; }
                try
                {
                    int ip, rp; var f = Features(c, i, out ip, out rp);
                    if (rp != i) { if (!skip.ContainsKey("not_packet_peak")) skip["not_packet_peak"] = 0; skip["not_packet_peak"]++; continue; }
                    var neighbors = records.Select(s => new Neighbor { Sample = s, Similarity = Similarity(f, s.features) }).ToList();
                    int pm, pw, nm, nw; double ps, ns; double positive = Evidence(neighbors, "positive", out pm, out pw, out ps), negative = Evidence(neighbors, "negative", out nm, out nw, out ns);
                    candidates.Add(new Candidate { index = i, position_m = Position(x, i - c.Incident), positive_similarity = positive, negative_similarity = negative,
                        score = ps * positive - .8 * ns * negative, manual_neighbors = pm + nm, bootstrap_neighbors = pw + nw,
                        manual_positive_neighbors = pm, manual_negative_neighbors = nm, negative_independent_neighbors = nm + nw });
                }
                catch (ArgumentException ex) { if (!skip.ContainsKey(ex.Message)) skip[ex.Message] = 0; skip[ex.Message]++; }
            }
            candidates = candidates.OrderByDescending(v => v.score).ThenBy(v => v.index).ToList();
            double margin = candidates.Count > 1 ? candidates[0].score - candidates[1].score : candidates.Count == 1 ? candidates[0].score : 0;
            // A high rank alone is not evidence that two labelled classes can
            // be separated. In initial weak training, three independent LOCs
            // with strongly similar negatives veto a proposal, even if the
            // positive class has a slightly higher score. This is morphology-
            // based abstention, not a hard-coded 80m inference filter.
            bool negativeConflict = candidates.Count > 0 && candidates[0].negative_independent_neighbors >= 3 &&
                ((candidates[0].manual_positive_neighbors < 3 && candidates[0].negative_similarity >= .80) ||
                 (candidates[0].manual_negative_neighbors >= 3 && candidates[0].negative_similarity >= .90 && candidates[0].positive_similarity - candidates[0].negative_similarity < .08));
            bool reflectionAvailable = peakMode && !negativeConflict && enough && complete && budgetComplete && candidates.Count > 0 && candidates[0].positive_similarity >= .65 && candidates[0].score >= (manualCount > 0 ? .18 : .08) && margin >= .025;
            // Threshold labels are independent of pair labels. A negative pair
            // does NOT imply that its acquisition threshold is good training.
            var thresholdRecords = records.Where(s => s.pair_label == "positive" && s.threshold_log_noise_ratio.HasValue).ToList();
            var humanThresholds = thresholdRecords.Where(s => s.threshold_human_confirmed && Human(s)).ToList();
            bool humanThresholdSupport = humanThresholds.Select(s => s.source_sha256).Distinct().Count() >= 3;
            if (humanThresholdSupport) thresholdRecords = humanThresholds;
            else thresholdRecords = thresholdRecords.Where(s => !Human(s) && s.label_source == "weak_bootstrap" && s.threshold_mode == "automatic").ToList();
            // One observation per LOC; pulses from the same acquisition do not
            // turn one threshold choice into hundreds of independent labels.
            var groupedThresholds = thresholdRecords.GroupBy(s => s.source_sha256)
                .Select(g => new KeyValuePair<double, double>(WeightedMedian(g.Select(s => new KeyValuePair<double, double>(s.threshold_log_noise_ratio.Value, s.label_weight)).ToList()), g.Max(s => s.label_weight))).ToList();
            bool thresholdAvailable = c.Noise > 0 && groupedThresholds.Count >= 3;
            double? thresholdPc = null, multiplier = null;
            if (thresholdAvailable)
            {
                double logRatio = WeightedMedian(groupedThresholds);
                double ratio = Math.Exp(logRatio), threshold = ratio * c.Noise;
                thresholdAvailable = IsFinite(ratio) && ratio >= 1 && ratio <= 512 && IsFinite(threshold) && threshold > 0;
                if (thresholdAvailable) { thresholdPc = threshold; multiplier = ratio; }
            }
            string status = !peakMode ? "first_arrival_requires_manual_timing" : !complete ? "incomplete_physical_window" : !budgetComplete ? "candidate_budget_exceeded" : !enough ? "insufficient_independent_positive_records" : negativeConflict ? "negative_pattern_conflict_requires_manual_review" : reflectionAvailable ? (manualCount > 0 ? "manual_learning_review_suggestion" : "bootstrap_review_suggestion") : "ambiguous_or_out_of_distribution";
            return Json(new { ok = true, status = status, reflection_available = reflectionAvailable, threshold_available = thresholdAvailable,
                suggested_reflection_index = reflectionAvailable ? (int?)candidates[0].index : null,
                suggested_threshold_pc = thresholdPc, suggested_threshold_multiplier = multiplier,
                threshold_status = thresholdAvailable ? (humanThresholdSupport ? "human_confirmed_noise_normalized" : "bootstrap_observation_not_optimal_threshold") : "no_independent_threshold_support",
                manual_count = manualCount, bootstrap_count = bootstrapCount, independent_positive_records = positiveRecords,
                candidate_window_complete = complete, candidate_budget_complete = budgetComplete, examined_candidates = examined,
                candidate_margin = margin, candidates = candidates, excluded_candidates = skip,
                strong_negative_conflict = negativeConflict,
                automatic_acceptance = false, position_is_feature = false, current_source_excluded = true,
                endpoint_learning = "start_end_interior_labels_and_stratified_evaluation" });
        }
        catch (Exception ex) { return Failure("suggestion_rejected", ex.Message); }
    }
}
