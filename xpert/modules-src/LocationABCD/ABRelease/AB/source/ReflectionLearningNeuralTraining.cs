using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

public sealed partial class ReflectionLearningEngine
{
    internal static double NeuralSigmoid(double z)
    { if (z >= 0) { double e = Math.Exp(-Math.Min(z, 700)); return 1 / (1 + e); } double a = Math.Exp(Math.Max(z, -700)); return a / (1 + a); }
    internal static double[] NeuralNormalize(NeuralPartition p, double[] f)
    { var x = new double[FeatureCount]; for (int j = 0; j < x.Length; j++) x[j] = Limit((f[j] - p.mean[j]) / p.scale[j], -5, 5); return x; }
    internal static double NeuralForward(NeuralPartition p, double[] x, double[] h)
    {
        double z = p.output_bias;
        for (int k = 0; k < NeuralHidden; k++) { double a = p.hidden_bias[k]; for (int j = 0; j < FeatureCount; j++) a += p.hidden_weights[k][j] * x[j]; h[k] = Math.Tanh(a); z += p.output_weights[k] * h[k]; }
        return NeuralSigmoid(z);
    }
    internal static double NeuralDistance(double[] a, double[] b, double[] scale)
    { double sum = 0; for (int j = 0; j < FeatureCount; j++) { double v = (a[j] - b[j]) / scale[j]; sum += v * v; } return Math.Sqrt(sum / FeatureCount); }
    internal static double[] NeuralWeights(List<Sample> samples)
    {
        // Each class has equal total weight; each LOC contributes equally in its
        // class, regardless of how many pulses the acquisition contains.
        var weights = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i]; int sources = samples.Where(t => t.pair_label == s.pair_label).Select(t => t.source_sha256).Distinct().Count();
            int count = samples.Count(t => t.pair_label == s.pair_label && t.source_sha256 == s.source_sha256);
            weights[i] = .5 / (sources * count);
        }
        return weights;
    }
    internal static void NeuralFit(NeuralPartition p, List<Sample> train, List<Sample> held)
    {
        p.mean = new double[FeatureCount]; p.scale = new double[FeatureCount]; var weights = NeuralWeights(train);
        for (int j = 0; j < FeatureCount; j++)
        {
            for (int i = 0; i < train.Count; i++) p.mean[j] += weights[i] * train[i].features[j];
            double variance = 0; for (int i = 0; i < train.Count; i++) { double d = train[i].features[j] - p.mean[j]; variance += weights[i] * d * d; }
            p.scale[j] = Math.Max(.05, Math.Sqrt(variance));
        }
        p.hidden_weights = new double[NeuralHidden][]; p.hidden_bias = new double[NeuralHidden]; p.output_weights = new double[NeuralHidden];
        // Explicit analytic initialization is stable across framework Random implementations.
        for (int k = 0; k < NeuralHidden; k++) { p.hidden_weights[k] = new double[FeatureCount]; p.output_weights[k] = .1 * Math.Sin((k + 1) * 1.31); for (int j = 0; j < FeatureCount; j++) p.hidden_weights[k][j] = .08 * Math.Sin((k + 1) * (j + 1) * 1.73); }
        var x = train.Select(s => NeuralNormalize(p, s.features)).ToArray();
        for (int epoch = 0; epoch < NeuralEpochs; epoch++)
        {
            var gw = new double[NeuralHidden][]; var gb = new double[NeuralHidden]; var go = new double[NeuralHidden]; for (int k = 0; k < NeuralHidden; k++) gw[k] = new double[FeatureCount]; double gz = 0;
            for (int i = 0; i < train.Count; i++)
            {
                var h = new double[NeuralHidden]; double score = NeuralForward(p, x[i], h), error = (score - (train[i].pair_label == "positive" ? 1 : 0)) * weights[i]; gz += error;
                for (int k = 0; k < NeuralHidden; k++) { go[k] += error * h[k]; double d = error * p.output_weights[k] * (1 - h[k] * h[k]); gb[k] += d; for (int j = 0; j < FeatureCount; j++) gw[k][j] += d * x[i][j]; }
            }
            const double rate = .12, regularization = .0005;
            p.output_bias = Limit(p.output_bias - rate * gz, -20, 20);
            for (int k = 0; k < NeuralHidden; k++) { p.output_weights[k] = Limit(p.output_weights[k] - rate * (go[k] + regularization * p.output_weights[k]), -20, 20); p.hidden_bias[k] = Limit(p.hidden_bias[k] - rate * gb[k], -20, 20); for (int j = 0; j < FeatureCount; j++) p.hidden_weights[k][j] = Limit(p.hidden_weights[k][j] - rate * (gw[k][j] + regularization * p.hidden_weights[k][j]), -20, 20); }
        }
        p.epochs = NeuralEpochs;
        var distances = train.Select(s => train.Where(t => t.source_sha256 != s.source_sha256 && t.pair_label == s.pair_label).Min(t => NeuralDistance(s.features, t.features, p.scale))).OrderBy(v => v).ToArray();
        p.ood_distance_limit = Limit(distances[(int)Math.Floor((distances.Length - 1) * .95)] * 1.5 + .25, .25, 3);
        var measured = NeuralEvaluate(p, held);
        p.validation.balanced_accuracy = measured[0]; p.validation.auc = measured[1]; p.validation.brier = measured[2];
        p.validation.passed = NeuralMetricsPassed(p.validation); p.status = p.validation.passed ? "validated_review_only" : "heldout_validation_failed"; p.validation.status = p.status;
    }
    internal static double[] NeuralEvaluate(NeuralPartition p, List<Sample> held)
    {
        var scores = held.Select(s => NeuralForward(p, NeuralNormalize(p, s.features), new double[NeuralHidden])).ToArray(); var vw = NeuralWeights(held);
        double accuracy = 0, brier = 0, auc = 0, aucWeight = 0;
        for (int i = 0; i < held.Count; i++) { double y = held[i].pair_label == "positive" ? 1 : 0; if ((scores[i] >= .5) == (y == 1)) accuracy += vw[i]; brier += vw[i] * (scores[i] - y) * (scores[i] - y); }
        for (int i = 0; i < held.Count; i++) if (held[i].pair_label == "positive") for (int j = 0; j < held.Count; j++) if (held[j].pair_label == "negative") { double w = vw[i] * vw[j]; aucWeight += w; auc += w * (scores[i] > scores[j] ? 1 : scores[i] == scores[j] ? .5 : 0); }
        return new[] { Limit(accuracy, 0, 1), Limit(auc / aucWeight, 0, 1), Limit(brier, 0, 1) };
    }
    /// <summary>Fixed-budget, deterministic MLP fitting. Human labels only; no auto-feedback or position labels enter features.</summary>
    public string TrainNeural(string samplesJson)
    {
        try
        {
            Require(samplesJson != null && samplesJson.Length <= 24 * 1024 * 1024, "neural_sample_json_budget_exceeded");
            var input = Serializer().Deserialize<List<Sample>>(samplesJson); Require(input != null && input.Count <= MaxSamples, "neural_sample_count_budget_exceeded");
            foreach (var s in input) ValidateSample(s);
            var m = new NeuralModel { ok = true, schema = NeuralSchema, engine_version = NeuralVersion, feature_names = FeatureNames, records = new List<Sample>(), partitions = new List<NeuralPartition>(), input_count = input.Count,
                excluded_nonhuman_count = input.Count(s => !Human(s)), excluded_other_method_count = input.Count(s => s.algorithm_index != 6) };
            // Keep weak/manual chronology together until the existing authoritative
            // conflict resolver runs. Partition timing/method first so another
            // analyzer or timing policy cannot override a method7 human correction.
            foreach (var group in input.Where(s => s.algorithm_index == 6).GroupBy(NeuralPartitionKey).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var resolved = ReadModel(Train(Json(group.ToList())));
                m.duplicate_count += resolved.duplicate_count; m.overridden_count += resolved.overridden_count; m.conflict_count += resolved.conflict_count;
                m.records.AddRange(resolved.records.Where(Human));
            }
            m.records = m.records.OrderBy(NeuralPartitionKey, StringComparer.Ordinal).ThenBy(s => s.source_sha256, StringComparer.Ordinal).ThenBy(s => s.source_incident_index).ThenBy(s => s.source_reflection_index).ToList();
            Require(m.records.Select(NeuralPartitionKey).Distinct().Count() <= 128, "neural_partition_budget_exceeded");
            var sources = m.records.Select(s => s.source_sha256).Distinct().OrderBy(s => Digest("neural-split-v1|" + s), StringComparer.Ordinal).ToArray();
            int heldCount = sources.Length >= 8 ? Math.Max(2, sources.Length / 4) : 0;
            m.global_heldout_sources = sources.Take(heldCount).OrderBy(s => s, StringComparer.Ordinal).ToArray(); m.global_training_sources = sources.Skip(heldCount).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            foreach (var group in m.records.GroupBy(NeuralPartitionKey).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var records = group.ToList(); var s = records[0]; var train = records.Where(t => m.global_training_sources.Contains(t.source_sha256)).ToList(); var held = records.Where(t => m.global_heldout_sources.Contains(t.source_sha256)).ToList();
                var p = new NeuralPartition { key = group.Key, scope_key = s.scope_key, project_key = s.project_key, calibration_sha256 = s.calibration_sha256, timing_mode = s.timing_mode,
                    training_sources = train.Select(t => t.source_sha256).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray(), heldout_sources = held.Select(t => t.source_sha256).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray(), validation = NeuralSupport(train, held) };
                if (p.training_sources.Length >= 6 && p.heldout_sources.Length >= 2 && NeuralSupported(p.validation)) NeuralFit(p, train, held);
                else { p.status = "insufficient_independent_human_support"; p.validation.status = p.status; }
                m.partitions.Add(p);
            }
            m.manual_count = m.records.Select(s => NeuralPartitionKey(s) + EventKey(s)).Distinct().Count(); m.positive_count = m.records.Count(s => s.pair_label == "positive"); m.negative_count = m.records.Count(s => s.pair_label == "negative"); m.bootstrap_count = 0;
            m.status = m.records.Count == 0 ? "awaiting_manual_labels" : m.partitions.Any(p => p.validation.passed) ? "review_only_neural_model" : "awaiting_independent_validation";
            m.checksum = NeuralDigest(m); string json = Json(m); ReadNeuralModel(json); return json;
        }
        catch (Exception ex) { return Failure("neural_training_rejected", ex.Message); }
    }
}
