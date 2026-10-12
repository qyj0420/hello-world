using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.Task17.Location;

/// <summary>
/// Local, review-only reflection morphology learning. No position target is a
/// predictive feature. The eight physical analyzers are not modified or called.
/// Method 6 is excluded. Only explicit human labels / bounded project-rule
/// bootstrap labels are admitted; no automatic feedback loop is implemented.
/// </summary>
public sealed partial class ReflectionLearningEngine
{
    internal const int MaxRaw = 2000000, MaxSamples = 4096, FeatureCount = 76;
    internal const string SampleSchema = "reflection-learning-sample-v1";
    internal const string ModelSchema = "reflection-learning-model-v1";
    internal const string NoiseSchema = "raw-diff-mad-v1";
    internal static readonly string[] FeatureNames = MakeFeatureNames();

    public sealed class Input
    {
        public string schema;
        public string scope_key, project_key, record_key, source_sha256, calibration_sha256;
        public string discharge_type, timing_mode, pair_label, label_source, operator_id;
        public int? algorithm_index, incident_index, source_incident_index, canonical_incident_index, chosen_reflection_index;
        public int[] rejected_reflection_indices;
        public double sample_rate_hz, exact_round_trip_samples, cable_length_m;
        public double[] segment_edges_m, segment_velocities_m_per_us;
        public double? selected_threshold_pc, automatic_threshold_multiplier;
        public string threshold_mode;
        public bool threshold_human_confirmed;
        public double endpoint_band_m = 10;
    }
    public sealed class Sample
    {
        public bool ok;
        public string schema, sample_id, created_utc, checksum;
        public string scope_key, project_key, record_key, source_sha256, calibration_sha256;
        public string discharge_type, timing_mode, pair_label, label_source, operator_id;
        public string endpoint_class, threshold_mode, noise_schema;
        public int algorithm_index, incident_index, source_incident_index, reflection_index, source_reflection_index;
        public double sample_rate_hz, exact_round_trip_samples, cable_length_m, position_m;
        public double endpoint_band_m;
        public double[] segment_edges_m, segment_velocities_m_per_us;
        public double[] features;
        public double noise_sigma_pc, label_weight;
        public double? selected_threshold_pc, automatic_threshold_multiplier, threshold_log_noise_ratio;
        public bool threshold_human_confirmed;
    }
    public sealed class Model
    {
        public bool ok;
        public string schema, engine_version, checksum, status;
        public string[] feature_names;
        public List<Sample> records;
        public int manual_count, bootstrap_count, positive_count, negative_count;
        public int input_count, duplicate_count, overridden_count, conflict_count;
    }
    internal sealed class SignalContext
    {
        internal Input Input;
        internal double[] Raw;
        internal double Baseline, Noise, ExactSpan;
        internal int Incident, SourceIncident, ShapeRadius;
    }
    internal static bool IsFinite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
    internal static JavaScriptSerializer Serializer()
    { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 48 }; }
    internal static string Json(object value) { return Serializer().Serialize(value); }
    internal static string Failure(string status, string detail)
    { return Json(new { ok = false, status = status, detail = detail, reflection_available = false, threshold_available = false }); }
    internal static void Require(bool condition, string message)
    { if (!condition) throw new ArgumentException(message); }
    internal static bool GoodText(string s, int limit)
    { return !String.IsNullOrWhiteSpace(s) && s.Length <= limit && !s.Any(Char.IsControl); }
    internal static bool HashText(string s)
    { return s != null && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')); }
    internal static string Digest(string text)
    { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
    internal static void CanonicalValue(object value, StringBuilder output)
    {
        if (value == null) { output.Append("null"); return; }
        var dictionary = value as IDictionary<string, object>;
        if (dictionary != null)
        {
            output.Append('{'); bool first = true;
            foreach (var item in dictionary.OrderBy(p => p.Key, StringComparer.Ordinal))
            { if (!first) output.Append(','); first = false; output.Append(Json(item.Key)).Append(':'); CanonicalValue(item.Value, output); }
            output.Append('}'); return;
        }
        if (value is string || value is bool) { output.Append(Json(value)); return; }
        var sequence = value as IEnumerable;
        if (sequence != null)
        {
            output.Append('['); bool first = true;
            foreach (var item in sequence) { if (!first) output.Append(','); first = false; CanonicalValue(item, output); }
            output.Append(']'); return;
        }
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        Require(IsFinite(number), "checksum_nonfinite");
        // Semantic JSON integrity at 12 significant digits: adapters that
        // deserialize to decimal and re-serialize must not invalidate a model
        // for a sub-ULP floating conversion. Raw file hashes remain byte-exact.
        output.Append(number == 0 ? "0" : number.ToString("G12", CultureInfo.InvariantCulture));
    }
    internal static string SemanticDigest(object value)
    { var canonical = new StringBuilder(); CanonicalValue(Serializer().DeserializeObject(Json(value)), canonical); return Digest(canonical.ToString()); }
    internal static string SampleDigest(Sample s)
    { string previous = s.checksum; try { s.checksum = null; return SemanticDigest(s); } finally { s.checksum = previous; } }
    internal static string ModelDigest(Model m)
    { string previous = m.checksum; try { m.checksum = null; return SemanticDigest(m); } finally { m.checksum = previous; } }
    internal static string EventKey(Sample s)
    { return Digest(Json(new object[] { s.scope_key, s.project_key, s.calibration_sha256, s.source_sha256, s.source_incident_index })); }
    internal static string PairKey(Sample s)
    { return EventKey(s) + "|" + s.source_reflection_index.ToString(CultureInfo.InvariantCulture); }
    internal static bool Human(Sample s)
    { return s.label_source == "manual_confirmed" || s.label_source == "manual_rejected"; }
    internal static string[] MakeFeatureNames()
    {
        var names = new List<string> { "signed_correlation", "envelope_correlation", "log_amplitude_ratio", "log_width_ratio", "log_energy_ratio", "polarity", "incident_log_snr", "reflection_log_snr", "incident_asymmetry", "reflection_asymmetry", "incident_roughness", "reflection_roughness" };
        for (int i = 0; i < 32; i++) names.Add("incident_shape_" + i);
        for (int i = 0; i < 32; i++) names.Add("reflection_shape_" + i);
        return names.ToArray();
    }

    internal static SignalContext Context(double[] raw, string metadataJson)
    {
        Require(raw != null && raw.Length >= 12 && raw.Length <= MaxRaw, "raw_length_out_of_budget");
        Require(raw.All(IsFinite), "raw_contains_nonfinite");
        Require(!String.IsNullOrWhiteSpace(metadataJson) && metadataJson.Length <= 128 * 1024, "metadata_size_invalid");
        var untyped = Serializer().Deserialize<Dictionary<string, object>>(metadataJson);
        Require(untyped != null, "metadata_object_required");
        foreach (string key in new[] { "algorithm_index", "incident_index", "source_incident_index", "canonical_incident_index", "chosen_reflection_index" })
        {
            object value;
            if (!untyped.TryGetValue(key, out value) || value == null) continue;
            Require(!(value is string) && !(value is bool), "integer_index_type_invalid:" + key);
            double n = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            Require(IsFinite(n) && n >= 0 && n <= Int32.MaxValue && n == Math.Truncate(n), "integer_index_invalid:" + key);
        }
        var x = Serializer().Deserialize<Input>(metadataJson);
        Require(x != null && x.schema == "reflection-learning-input-v1", "input_schema_mismatch");
        Require(GoodText(x.scope_key, 160) && GoodText(x.project_key, 1024) && GoodText(x.record_key, 1024), "identity_invalid");
        Require(HashText(x.source_sha256) && HashText(x.calibration_sha256), "provenance_hash_missing");
        x.source_sha256 = x.source_sha256.ToLowerInvariant(); x.calibration_sha256 = x.calibration_sha256.ToLowerInvariant();
        Require(x.algorithm_index.HasValue && x.algorithm_index >= 0 && x.algorithm_index <= 7 && x.algorithm_index != 5, "method6_or_invalid_algorithm_excluded");
        Require(x.timing_mode == "peak" || x.timing_mode == "first_arrival", "timing_mode_invalid");
        Require(IsFinite(x.sample_rate_hz) && x.sample_rate_hz >= 100000 && x.sample_rate_hz <= 5000000000d, "sample_rate_hz_invalid");
        Require(IsFinite(x.exact_round_trip_samples) && x.exact_round_trip_samples > 2 && x.exact_round_trip_samples <= MaxRaw, "physical_span_invalid");
        Require(IsFinite(x.cable_length_m) && x.cable_length_m > 0 && x.cable_length_m <= 1000000, "cable_length_invalid");
        Require(x.incident_index.HasValue && x.incident_index >= 0 && x.incident_index < raw.Length, "incident_index_invalid");
        Require(x.source_incident_index.HasValue && x.source_incident_index >= 0, "global_incident_index_missing");
        Require(IsFinite(x.endpoint_band_m) && x.endpoint_band_m >= 0 && x.endpoint_band_m <= x.cable_length_m / 2, "endpoint_band_invalid");
        Require(x.rejected_reflection_indices == null || x.rejected_reflection_indices.Length == 0, "use_separate_explicit_negative_samples");
        ValidateGeometry(x);
        double baseline = Median(Subsample(raw, 4096));
        double noise = Noise(raw);
        double excursion = raw.Max(v => Math.Abs(v - baseline));
        Require(IsFinite(excursion) && excursion > 0 && IsFinite(noise) && noise >= 0, "flat_or_overflow_signal");
        int radius = Math.Max(2, Math.Min(256, (int)Math.Ceiling(x.sample_rate_hz * 64e-9)));
        return new SignalContext { Input = x, Raw = raw, Baseline = baseline, Noise = noise, Incident = x.incident_index.Value, SourceIncident = x.source_incident_index.Value, ShapeRadius = radius, ExactSpan = x.exact_round_trip_samples };
    }
    internal static void ValidateGeometry(Input x)
    {
        bool edges = x.segment_edges_m != null, speeds = x.segment_velocities_m_per_us != null;
        Require(edges == speeds, "partial_segment_geometry");
        if (!edges)
        {
            double speed = 2 * x.cable_length_m * x.sample_rate_hz / (1000000d * x.exact_round_trip_samples);
            Require(IsFinite(speed) && speed > 0 && speed <= 300, "implicit_wave_speed_invalid");
            return;
        }
        Require(x.segment_edges_m.Length >= 2 && x.segment_edges_m.Length <= 1025 && x.segment_velocities_m_per_us.Length == x.segment_edges_m.Length - 1, "segment_shape_invalid");
        Require(x.segment_edges_m.All(IsFinite) && x.segment_velocities_m_per_us.All(v => IsFinite(v) && v > 0 && v <= 300), "segment_nonfinite_or_speed_invalid");
        Require(Math.Abs(x.segment_edges_m[0]) <= 1e-9 && Math.Abs(x.segment_edges_m.Last() - x.cable_length_m) <= Math.Max(1e-6, x.cable_length_m * 1e-8), "segment_length_mismatch");
        double travel = 0;
        for (int i = 0; i < x.segment_velocities_m_per_us.Length; i++)
        { Require(x.segment_edges_m[i + 1] > x.segment_edges_m[i], "segment_edges_not_increasing"); travel += (x.segment_edges_m[i + 1] - x.segment_edges_m[i]) / x.segment_velocities_m_per_us[i]; }
        double span = 2 * travel * x.sample_rate_hz / 1000000d;
        Require(IsFinite(span) && Math.Abs(span - x.exact_round_trip_samples) <= Math.Max(0.05, span * 1e-6), "segment_round_trip_mismatch");
    }
    internal static double Position(Input x, double delay)
    {
        double ratio = delay / x.exact_round_trip_samples;
        Require(IsFinite(ratio) && ratio > 0 && ratio <= 1, "pair_outside_exact_cable_window");
        if (x.segment_edges_m == null) return x.cable_length_m * (1 - ratio);
        // One-way remaining travel from cable end; invert piecewise travel.
        double target = (x.exact_round_trip_samples - delay) * 500000d / x.sample_rate_hz;
        for (int i = 0; i < x.segment_velocities_m_per_us.Length; i++)
        {
            double duration = (x.segment_edges_m[i + 1] - x.segment_edges_m[i]) / x.segment_velocities_m_per_us[i];
            if (target <= duration || i == x.segment_velocities_m_per_us.Length - 1)
                return x.segment_edges_m[i] + target * x.segment_velocities_m_per_us[i];
            target -= duration;
        }
        throw new ArgumentException("position_inversion_failed");
    }
    internal static void ValidateSample(Sample s)
    {
        Require(s != null && s.ok && s.schema == SampleSchema && HashText(s.checksum) && SampleDigest(s) == s.checksum, "sample_integrity_failed");
        Require(GoodText(s.sample_id, 80) && GoodText(s.scope_key, 160) && GoodText(s.project_key, 1024) && GoodText(s.record_key, 1024), "sample_identity_invalid");
        DateTime timestamp;
        Require(DateTime.TryParseExact(s.created_utc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp) && timestamp.Kind == DateTimeKind.Utc && timestamp.ToString("o", CultureInfo.InvariantCulture) == s.created_utc, "sample_timestamp_not_canonical_utc");
        Require(HashText(s.source_sha256) && HashText(s.calibration_sha256), "sample_provenance_invalid");
        Require(s.source_sha256 == s.source_sha256.ToLowerInvariant() && s.calibration_sha256 == s.calibration_sha256.ToLowerInvariant(), "sample_hash_case_not_canonical");
        Require(s.algorithm_index >= 0 && s.algorithm_index <= 7 && s.algorithm_index != 5, "method6_sample_excluded");
        Require(s.pair_label == "positive" || s.pair_label == "negative", "sample_label_invalid");
        Require((s.label_source == "manual_confirmed" && s.pair_label == "positive") || (s.label_source == "manual_rejected" && s.pair_label == "negative") || (s.label_source == "weak_bootstrap" && s.pair_label == "positive") || (s.label_source == "project_rule_negative" && s.pair_label == "negative"), "sample_label_authority_invalid");
        Require(s.label_weight == (Human(s) ? 1 : 0.15), "sample_weight_invalid");
        Require(s.features != null && s.features.Length == FeatureCount && s.features.All(v => IsFinite(v) && Math.Abs(v) <= 8), "feature_vector_invalid");
        Require(s.incident_index >= 0 && s.reflection_index > s.incident_index && s.source_incident_index >= 0 && (long)s.source_reflection_index - s.source_incident_index == (long)s.reflection_index - s.incident_index, "sample_index_invalid");
        Require(IsFinite(s.exact_round_trip_samples) && s.exact_round_trip_samples > 2 && s.reflection_index - s.incident_index <= s.exact_round_trip_samples, "sample_outside_cable_window");
        Require(IsFinite(s.cable_length_m) && s.cable_length_m > 0 && IsFinite(s.position_m) && s.position_m >= 0 && s.position_m <= s.cable_length_m, "sample_position_invalid");
        Require(IsFinite(s.sample_rate_hz) && s.sample_rate_hz >= 100000 && s.sample_rate_hz <= 5000000000d && s.noise_schema == NoiseSchema && IsFinite(s.noise_sigma_pc) && s.noise_sigma_pc >= 0, "sample_noise_or_rate_invalid");
        Require(s.endpoint_class == "start" || s.endpoint_class == "end" || s.endpoint_class == "interior", "endpoint_class_invalid");
        Require((s.timing_mode == "peak" || s.timing_mode == "first_arrival") && (s.threshold_mode == "manual" || s.threshold_mode == "automatic" || s.threshold_mode == "unknown") && GoodText(s.operator_id, 160) && GoodText(s.discharge_type, 160), "sample_mode_or_authority_invalid");
        Require(IsFinite(s.endpoint_band_m) && s.endpoint_band_m >= 0 && s.endpoint_band_m <= s.cable_length_m / 2, "sample_endpoint_band_invalid");
        var geometry = new Input { sample_rate_hz = s.sample_rate_hz, exact_round_trip_samples = s.exact_round_trip_samples, cable_length_m = s.cable_length_m, segment_edges_m = s.segment_edges_m, segment_velocities_m_per_us = s.segment_velocities_m_per_us };
        ValidateGeometry(geometry);
        Require(Math.Abs(Position(geometry, s.reflection_index - s.incident_index) - s.position_m) <= Math.Max(1e-8, s.cable_length_m * 1e-9), "sample_position_geometry_mismatch");
        string expectedClass = s.position_m <= s.endpoint_band_m ? "start" : s.position_m >= s.cable_length_m - s.endpoint_band_m ? "end" : "interior";
        Require(s.endpoint_class == expectedClass, "sample_endpoint_class_mismatch");
        Require(!s.selected_threshold_pc.HasValue || (IsFinite(s.selected_threshold_pc.Value) && s.selected_threshold_pc.Value > 0 && s.selected_threshold_pc.Value < 1e15), "sample_threshold_invalid");
        Require(!s.automatic_threshold_multiplier.HasValue || (IsFinite(s.automatic_threshold_multiplier.Value) && s.automatic_threshold_multiplier.Value > 0 && s.automatic_threshold_multiplier.Value <= 512), "sample_multiplier_invalid");
        Require(!s.threshold_log_noise_ratio.HasValue || (s.noise_sigma_pc > 0 && s.selected_threshold_pc.HasValue && IsFinite(s.threshold_log_noise_ratio.Value) && Math.Abs(s.threshold_log_noise_ratio.Value - Math.Log(s.selected_threshold_pc.Value / s.noise_sigma_pc)) < 1e-9), "threshold_noise_ratio_invalid");
        Require(!s.threshold_human_confirmed || (Human(s) && s.threshold_mode == "manual" && s.selected_threshold_pc.HasValue), "threshold_confirmation_invalid");
    }

    /// <summary>Rebuilds entirely from the active, hash-verified sample set. Removing a label and calling Train implements undo.</summary>
    public string Train(string samplesJson)
    {
        try
        {
            Require(samplesJson != null && samplesJson.Length <= 24 * 1024 * 1024, "sample_json_budget_exceeded");
            var input = Serializer().Deserialize<List<Sample>>(samplesJson);
            Require(input != null && input.Count <= MaxSamples, "sample_count_budget_exceeded");
            foreach (var s in input) ValidateSample(s);
            var active = new List<Sample>(); int duplicates = 0, overridden = 0, conflicts = 0;
            foreach (var group in input.GroupBy(EventKey).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(s => s.created_utc, StringComparer.Ordinal).ThenBy(s => s.sample_id, StringComparer.Ordinal).ToList();
                bool hasHuman = ordered.Any(Human);
                if (hasHuman) { overridden += ordered.Count(s => !Human(s)); ordered = ordered.Where(Human).ToList(); }
                var humanPositive = ordered.Where(s => Human(s) && s.pair_label == "positive").LastOrDefault();
                foreach (var pair in ordered.GroupBy(PairKey).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var list = pair.ToList(); duplicates += list.Count - 1;
                    if (!hasHuman && list.Select(s => s.pair_label).Distinct().Count() > 1) { conflicts++; continue; }
                    Sample selected = list.Last();
                    if (Human(selected) && selected.pair_label == "positive" && humanPositive != null && selected != humanPositive) { overridden++; continue; }
                    active.Add(selected);
                }
            }
            var model = new Model { ok = true, schema = ModelSchema, engine_version = "2.13.0", feature_names = FeatureNames,
                records = active, input_count = input.Count, duplicate_count = duplicates, overridden_count = overridden, conflict_count = conflicts,
                manual_count = active.Where(Human).Select(EventKey).Distinct().Count(),
                bootstrap_count = active.Where(s => !Human(s)).Select(EventKey).Distinct().Count(),
                positive_count = active.Count(s => s.pair_label == "positive"), negative_count = active.Count(s => s.pair_label == "negative"),
                status = active.Count == 0 ? "empty_model" : "review_only_model" };
            model.checksum = ModelDigest(model);
            return Json(model);
        }
        catch (Exception ex) { return Failure("training_rejected", ex.Message); }
    }
    internal static Model ReadModel(string modelJson)
    {
        Require(modelJson != null && modelJson.Length <= 24 * 1024 * 1024, "model_json_budget_exceeded");
        var m = Serializer().Deserialize<Model>(modelJson);
        Require(m != null && m.ok && m.schema == ModelSchema && m.engine_version == "2.13.0" && HashText(m.checksum) && ModelDigest(m) == m.checksum, "model_integrity_or_schema_failed");
        Require(m.feature_names != null && m.feature_names.SequenceEqual(FeatureNames) && m.records != null && m.records.Count <= MaxSamples, "model_feature_or_size_mismatch");
        foreach (var s in m.records) ValidateSample(s);
        Require(m.records.Select(PairKey).Distinct().Count() == m.records.Count, "model_duplicate_pairs");
        Require(m.manual_count == m.records.Where(Human).Select(EventKey).Distinct().Count() && m.bootstrap_count == m.records.Where(s => !Human(s)).Select(EventKey).Distinct().Count() && m.positive_count == m.records.Count(s => s.pair_label == "positive") && m.negative_count == m.records.Count(s => s.pair_label == "negative"), "model_counts_mismatch");
        return m;
    }
    public string InspectModel(string modelJson)
    {
        try
        {
            var m = ReadModel(modelJson);
            return Json(new { ok = true, schema = m.schema, status = m.status, m.manual_count, m.bootstrap_count, m.positive_count, m.negative_count,
                independent_records = m.records.Select(s => s.source_sha256).Distinct().Count(),
                endpoint_counts = m.records.GroupBy(s => s.endpoint_class).ToDictionary(g => g.Key, g => g.Count()),
                labels_are_not_physical_truth = true, automatic_acceptance = false, position_is_feature = false });
        }
        catch (Exception ex) { return Failure("model_rejected", ex.Message); }
    }
}
