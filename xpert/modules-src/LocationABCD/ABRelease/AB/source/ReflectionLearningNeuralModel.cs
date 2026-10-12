using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HVX.Task17.Location;

public sealed partial class ReflectionLearningEngine
{
    internal const string NeuralSchema = "reflection-neural-model-v1";
    internal const string NeuralVersion = "1.0.0";
    internal const int NeuralHidden = 12, NeuralEpochs = 200;
    public sealed class NeuralValidation
    {
        public int train_count, heldout_count, train_positive, train_negative, heldout_positive, heldout_negative;
        public int train_positive_sources, train_negative_sources, heldout_positive_sources, heldout_negative_sources;
        public double? balanced_accuracy, auc, brier;
        public bool passed;
        public string status;
        public Dictionary<string, int> train_endpoint_counts, heldout_endpoint_counts;
    }
    public sealed class NeuralPartition
    {
        public string key, scope_key, project_key, calibration_sha256, timing_mode, status;
        public string[] training_sources, heldout_sources;
        public double[] mean, scale, hidden_bias, output_weights;
        public double[][] hidden_weights;
        public double output_bias, ood_distance_limit;
        public int epochs;
        public NeuralValidation validation;
    }
    public sealed class NeuralModel
    {
        public bool ok;
        public string schema, engine_version, checksum, status;
        public string[] feature_names, global_training_sources, global_heldout_sources;
        public List<Sample> records;
        public List<NeuralPartition> partitions;
        public int input_count, manual_count, bootstrap_count, positive_count, negative_count;
        public int excluded_nonhuman_count, excluded_other_method_count, duplicate_count, overridden_count, conflict_count;
    }
    internal static string NeuralPartitionKey(Sample s)
    { return Digest(Json(new[] { s.scope_key, s.project_key, s.calibration_sha256, s.timing_mode })); }
    internal static string NeuralPartitionKey(Input s)
    { return Digest(Json(new[] { s.scope_key, s.project_key, s.calibration_sha256, s.timing_mode })); }
    internal static string NeuralDigest(NeuralModel m)
    { var old = m.checksum; try { m.checksum = null; return SemanticDigest(m); } finally { m.checksum = old; } }
    internal static void NeuralFinite(double[] v, int n, double bound, string name)
    { Require(v != null && v.Length == n && v.All(x => IsFinite(x) && Math.Abs(x) <= bound), "neural_" + name + "_invalid"); }
    internal static NeuralModel ReadNeuralModel(string json)
    {
        Require(json != null && json.Length <= 24 * 1024 * 1024, "neural_model_budget_exceeded");
        var m = Serializer().Deserialize<NeuralModel>(json);
        Require(m != null && m.ok && m.schema == NeuralSchema && m.engine_version == NeuralVersion, "neural_schema_mismatch");
        Require(HashText(m.checksum) && NeuralDigest(m) == m.checksum, "neural_integrity_failed");
        Require(m.feature_names != null && m.feature_names.SequenceEqual(FeatureNames), "neural_features_mismatch");
        Require(m.records != null && m.records.Count <= MaxSamples && m.partitions != null && m.partitions.Count <= 128, "neural_model_size_invalid");
        Require(m.input_count >= m.records.Count && m.input_count <= MaxSamples && m.excluded_nonhuman_count >= 0 && m.excluded_nonhuman_count <= m.input_count &&
            m.excluded_other_method_count >= 0 && m.excluded_other_method_count <= m.input_count && m.duplicate_count >= 0 && m.overridden_count >= 0 && m.conflict_count >= 0,
            "neural_input_statistics_invalid");
        foreach (var s in m.records) { ValidateSample(s); Require(Human(s) && s.algorithm_index == 6, "neural_nonhuman_or_non_method7_record"); }
        Require(m.records.Select(s => NeuralPartitionKey(s) + PairKey(s)).Distinct().Count() == m.records.Count, "neural_duplicate_pair");
        Require(m.manual_count == m.records.Select(s => NeuralPartitionKey(s) + EventKey(s)).Distinct().Count() && m.bootstrap_count == 0 &&
            m.positive_count == m.records.Count(s => s.pair_label == "positive") && m.negative_count == m.records.Count(s => s.pair_label == "negative"), "neural_counts_mismatch");
        Require(m.global_training_sources != null && m.global_heldout_sources != null &&
            m.global_training_sources.All(HashText) && m.global_heldout_sources.All(HashText) &&
            m.global_training_sources.Distinct().Count() == m.global_training_sources.Length && m.global_heldout_sources.Distinct().Count() == m.global_heldout_sources.Length &&
            !m.global_training_sources.Intersect(m.global_heldout_sources).Any(), "neural_split_overlap_or_invalid");
        Require(m.global_training_sources.Concat(m.global_heldout_sources).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(m.records.Select(s => s.source_sha256).Distinct().OrderBy(x => x, StringComparer.Ordinal)), "neural_split_sources_mismatch");
        Require(m.partitions.Select(p => p.key).Distinct().Count() == m.partitions.Count &&
            m.partitions.Select(p => p.key).OrderBy(x => x).SequenceEqual(m.records.Select(NeuralPartitionKey).Distinct().OrderBy(x => x)), "neural_partitions_mismatch");
        foreach (var p in m.partitions)
        {
            var r = m.records.Where(s => NeuralPartitionKey(s) == p.key).ToList(); var a = r[0];
            Require(p.scope_key == a.scope_key && p.project_key == a.project_key && p.calibration_sha256 == a.calibration_sha256 && p.timing_mode == a.timing_mode, "neural_partition_identity_mismatch");
            Require(p.training_sources != null && p.heldout_sources != null && p.training_sources.SequenceEqual(r.Select(s => s.source_sha256).Distinct().Where(m.global_training_sources.Contains).OrderBy(x => x, StringComparer.Ordinal)) &&
                p.heldout_sources.SequenceEqual(r.Select(s => s.source_sha256).Distinct().Where(m.global_heldout_sources.Contains).OrderBy(x => x, StringComparer.Ordinal)), "neural_partition_split_mismatch");
            var train = r.Where(s => p.training_sources.Contains(s.source_sha256)).ToList(); var held = r.Where(s => p.heldout_sources.Contains(s.source_sha256)).ToList();
            Require(p.validation != null, "neural_validation_missing"); var expected = NeuralSupport(train, held);
            Require(p.validation.train_count == expected.train_count && p.validation.heldout_count == expected.heldout_count &&
                p.validation.train_positive == expected.train_positive && p.validation.train_negative == expected.train_negative && p.validation.heldout_positive == expected.heldout_positive && p.validation.heldout_negative == expected.heldout_negative &&
                p.validation.train_positive_sources == expected.train_positive_sources && p.validation.train_negative_sources == expected.train_negative_sources && p.validation.heldout_positive_sources == expected.heldout_positive_sources && p.validation.heldout_negative_sources == expected.heldout_negative_sources &&
                Json(p.validation.train_endpoint_counts) == Json(expected.train_endpoint_counts) && Json(p.validation.heldout_endpoint_counts) == Json(expected.heldout_endpoint_counts), "neural_validation_support_mismatch");
            if (p.epochs == 0)
            {
                Require(p.training_sources.Length < 6 || p.heldout_sources.Length < 2 || !NeuralSupported(expected), "neural_supported_partition_cannot_be_untrained");
                Require(!p.validation.passed && p.hidden_weights == null && p.mean == null && p.scale == null && p.output_weights == null && p.hidden_bias == null &&
                    p.status == "insufficient_independent_human_support" && p.validation.status == "insufficient_independent_human_support" &&
                    !p.validation.balanced_accuracy.HasValue && !p.validation.auc.HasValue && !p.validation.brier.HasValue && p.output_bias == 0 && p.ood_distance_limit == 0, "neural_untrained_state_invalid");
                continue;
            }
            Require(p.epochs == NeuralEpochs && NeuralSupported(expected) && p.training_sources.Length >= 6 && p.heldout_sources.Length >= 2, "neural_training_support_or_epoch_invalid");
            NeuralFinite(p.mean, FeatureCount, 8, "mean"); NeuralFinite(p.scale, FeatureCount, 16, "scale"); Require(p.scale.All(v => v >= .05), "neural_scale_zero");
            NeuralFinite(p.hidden_bias, NeuralHidden, 100, "hidden_bias"); NeuralFinite(p.output_weights, NeuralHidden, 100, "output_weights");
            Require(p.hidden_weights != null && p.hidden_weights.Length == NeuralHidden, "neural_hidden_dimension_invalid"); foreach (var w in p.hidden_weights) NeuralFinite(w, FeatureCount, 100, "hidden_weights");
            Require(IsFinite(p.output_bias) && Math.Abs(p.output_bias) <= 100 && IsFinite(p.ood_distance_limit) && p.ood_distance_limit >= .25 && p.ood_distance_limit <= 3, "neural_scalar_invalid");
            Require(p.validation.balanced_accuracy.HasValue && p.validation.auc.HasValue && p.validation.brier.HasValue &&
                new[] { p.validation.balanced_accuracy.Value, p.validation.auc.Value, p.validation.brier.Value }.All(v => IsFinite(v) && v >= 0 && v <= 1), "neural_validation_metric_invalid");
            bool pass = NeuralMetricsPassed(p.validation);
            Require(p.validation.passed == pass && p.status == (pass ? "validated_review_only" : "heldout_validation_failed") && p.validation.status == p.status, "neural_validation_gate_invalid");
            // The checksum is corruption detection, not label authentication.
            // Recompute heldout performance as well, so stale/fabricated metrics
            // cannot legitimize changed network weights even after re-signing.
            var measured = NeuralEvaluate(p, held);
            Require(Math.Abs(measured[0] - p.validation.balanced_accuracy.Value) <= 1e-10 && Math.Abs(measured[1] - p.validation.auc.Value) <= 1e-10 && Math.Abs(measured[2] - p.validation.brier.Value) <= 1e-10,
                "neural_validation_metrics_do_not_match_weights");
        }
        Require(m.status == (m.records.Count == 0 ? "awaiting_manual_labels" : m.partitions.Any(p => p.validation.passed) ? "review_only_neural_model" : "awaiting_independent_validation"), "neural_status_mismatch");
        return m;
    }
    internal static NeuralValidation NeuralSupport(List<Sample> train, List<Sample> held)
    {
        return new NeuralValidation { train_count = train.Count, heldout_count = held.Count, train_positive = train.Count(s => s.pair_label == "positive"), train_negative = train.Count(s => s.pair_label == "negative"),
            heldout_positive = held.Count(s => s.pair_label == "positive"), heldout_negative = held.Count(s => s.pair_label == "negative"),
            train_positive_sources = train.Where(s => s.pair_label == "positive").Select(s => s.source_sha256).Distinct().Count(), train_negative_sources = train.Where(s => s.pair_label == "negative").Select(s => s.source_sha256).Distinct().Count(),
            heldout_positive_sources = held.Where(s => s.pair_label == "positive").Select(s => s.source_sha256).Distinct().Count(), heldout_negative_sources = held.Where(s => s.pair_label == "negative").Select(s => s.source_sha256).Distinct().Count(),
            train_endpoint_counts = train.GroupBy(s => s.endpoint_class).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()), heldout_endpoint_counts = held.GroupBy(s => s.endpoint_class).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()) };
    }
    internal static bool NeuralSupported(NeuralValidation v)
    { return v.train_positive_sources >= 4 && v.train_negative_sources >= 4 && v.heldout_positive_sources >= 2 && v.heldout_negative_sources >= 2 && v.train_count >= 12 && v.heldout_count >= 4; }
    internal static bool NeuralMetricsPassed(NeuralValidation v)
    { return v.balanced_accuracy >= .75 && v.auc >= .80 && v.brier <= .25; }
    public string InspectNeuralModel(string modelJson)
    {
        try
        {
            var m = ReadNeuralModel(modelJson);
            return Json(new { ok = true, m.schema, m.engine_version, m.status, m.manual_count, m.bootstrap_count, m.positive_count, m.negative_count,
                independent_records = m.records.Select(s => s.source_sha256).Distinct().Count(), m.input_count, m.excluded_nonhuman_count, m.excluded_other_method_count,
                architecture = "76 morphology inputs -> 12 tanh -> 1 sigmoid", inference_policy = "heldout_gated_review_only", neural_pair_score = true,
                threshold_head = "non_neural_human_noise_normalized_median", partitions = m.partitions.Select(p => new { p.key, p.scope_key, p.project_key, p.calibration_sha256, p.timing_mode, p.status, p.epochs, p.training_sources, p.heldout_sources, p.validation }),
                automatic_acceptance = false, position_is_feature = false, delay_is_feature = false, labels_are_not_physical_truth = true,
                endpoint_learning = "manual_endpoint_labels_stratified_support_not_position_input" });
        }
        catch (Exception ex) { return Failure("neural_model_rejected", ex.Message); }
    }
}
