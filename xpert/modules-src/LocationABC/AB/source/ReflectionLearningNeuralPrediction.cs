using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

public sealed partial class ReflectionLearningEngine
{
    public sealed class NeuralCandidate
    {
        public int index, canonical_incident_index, canonical_reflection_index;
        public double position_m, score, ood_distance;
        public bool in_distribution;
        public string status;
    }
    /// <summary>Scores existing physical-window peaks only. The score is not calibrated physical confidence and never auto-accepts.</summary>
    public string SuggestNeural(double[] rawSignal, string queryJson, string modelJson)
    {
        try
        {
            var c = Context(rawSignal, queryJson); Require(c.Input.algorithm_index == 6, "neural_requires_method7"); var x = c.Input; var m = ReadNeuralModel(modelJson);
            bool seenSource = m.records.Any(s => s.source_sha256 == x.source_sha256); var p = m.partitions.FirstOrDefault(t => t.key == NeuralPartitionKey(x));
            var records = m.records.Where(s => NeuralPartitionKey(s) == NeuralPartitionKey(x)).ToList();
            var train = p == null ? new List<Sample>() : records.Where(s => p.training_sources.Contains(s.source_sha256)).ToList();
            bool peak = x.timing_mode == "peak", validated = p != null && p.validation.passed;
            bool complete = (long)c.Incident + (long)Math.Floor(c.ExactSpan) + c.ShapeRadius + 1 < rawSignal.Length;
            int right = Math.Min(rawSignal.Length - c.ShapeRadius - 2, (int)Math.Min(Int32.MaxValue, (long)c.Incident + (long)Math.Floor(c.ExactSpan)));
            int left = Math.Max(c.Incident + c.ShapeRadius + 1, c.ShapeRadius); var candidates = new List<NeuralCandidate>(); var skips = new Dictionary<string, int>(); int examined = 0; bool budgetComplete = true;
            if (!seenSource && validated && peak)
            for (int i = left; i <= right; i++)
            {
                double a = Math.Abs(rawSignal[i] - c.Baseline);
                if (a <= 0 || a < c.Noise * 3 || a < Math.Abs(rawSignal[i - 1] - c.Baseline) || a <= Math.Abs(rawSignal[i + 1] - c.Baseline)) continue;
                if (++examined > 128) { budgetComplete = false; break; }
                try
                {
                    int ip, rp; var f = Features(c, i, out ip, out rp); if (rp != i) { if (!skips.ContainsKey("not_packet_peak")) skips["not_packet_peak"] = 0; skips["not_packet_peak"]++; continue; }
                    double distance = train.Min(s => NeuralDistance(f, s.features, p.scale)); double score = NeuralForward(p, NeuralNormalize(p, f), new double[NeuralHidden]);
                    Require(IsFinite(score) && IsFinite(distance), "neural_candidate_nonfinite");
                    bool within = distance <= p.ood_distance_limit;
                    candidates.Add(new NeuralCandidate { index = i, canonical_incident_index = ip, canonical_reflection_index = rp, position_m = Position(x, i - c.Incident), score = score, ood_distance = distance, in_distribution = within, status = within ? "review_only_neural_score" : "out_of_training_distribution" });
                }
                catch (ArgumentException ex) { if (!skips.ContainsKey(ex.Message)) skips[ex.Message] = 0; skips[ex.Message]++; }
            }
            candidates = candidates.OrderByDescending(t => t.score).ThenBy(t => t.index).ToList();
            double margin = candidates.Count > 1 ? candidates[0].score - candidates[1].score : candidates.Count == 1 ? candidates[0].score : 0;
            bool available = !seenSource && validated && peak && complete && budgetComplete && candidates.Count > 0 && candidates[0].in_distribution && candidates[0].score >= .70 && margin >= .10;
            // Threshold prediction is deliberately a separate, transparent head:
            // only explicit HUMAN confirmations, only training LOCs, one median
            // per acquisition. Rejected pairs and automatic/weak thresholds never fit it.
            var thresholds = train.Where(s => s.pair_label == "positive" && Human(s) && s.threshold_human_confirmed && s.threshold_mode == "manual" && s.threshold_log_noise_ratio.HasValue)
                .GroupBy(s => s.source_sha256).Select(g => Median(g.Select(s => s.threshold_log_noise_ratio.Value).ToArray())).ToArray();
            bool thresholdAvailable = !seenSource && c.Noise > 0 && thresholds.Length >= 3; double? thresholdPc = null, thresholdMultiplier = null;
            if (thresholdAvailable)
            {
                double ratio = Math.Exp(Median(thresholds)), value = ratio * c.Noise;
                thresholdAvailable = IsFinite(ratio) && ratio >= 1 && ratio <= 512 && IsFinite(value) && value > 0 && value < 1e15;
                if (thresholdAvailable) { thresholdPc = value; thresholdMultiplier = ratio; }
            }
            string status = seenSource ? "source_seen_in_training_or_validation_abstain" : !peak ? "first_arrival_requires_manual_timing" : p == null ? "awaiting_manual_labels_for_partition" : !validated ? p.status : !complete ? "incomplete_physical_window" : !budgetComplete ? "candidate_budget_exceeded" : available ? "neural_review_suggestion" : "ambiguous_or_out_of_distribution";
            return Json(new { ok = true, status, schema = NeuralSchema, reflection_available = available, threshold_available = thresholdAvailable,
                suggested_reflection_index = available ? (int?)candidates[0].index : null, suggested_threshold_pc = thresholdPc, suggested_threshold_multiplier = thresholdMultiplier,
                threshold_status = thresholdAvailable ? "human_confirmed_noise_normalized_non_neural" : "no_independent_human_threshold_support",
                manual_count = records.Select(EventKey).Distinct().Count(), bootstrap_count = 0, independent_positive_records = train.Where(s => s.pair_label == "positive").Select(s => s.source_sha256).Distinct().Count(),
                threshold_independent_sources = thresholds.Length, candidate_window_complete = complete, candidate_budget_complete = budgetComplete, examined_candidates = examined, candidate_margin = margin, candidates, excluded_candidates = skips,
                current_source_excluded = true, current_source_seen = seenSource, automatic_acceptance = false, position_is_feature = false, delay_is_feature = false,
                neural_pair_score = true, score_is_calibrated_probability = false, physical_analyzer_unchanged = true, first_arrival_automatic_timing_supported = false,
                endpoint_learning = "manual_endpoint_labels_stratified_support_not_position_input", partition_key = p == null ? null : p.key, model_checksum = m.checksum });
        }
        catch (Exception ex) { return Failure("neural_suggestion_rejected", ex.Message); }
    }
}
