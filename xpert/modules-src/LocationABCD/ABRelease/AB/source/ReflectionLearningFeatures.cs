using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

public sealed partial class ReflectionLearningEngine
{
    internal static double[] Subsample(double[] values, int max)
    {
        if (values.Length <= max) return (double[])values.Clone();
        var result = new double[max];
        for (int i = 0; i < max; i++) result[i] = values[(int)((long)i * (values.Length - 1) / (max - 1))];
        return result;
    }
    internal static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values); int mid = values.Length / 2;
        return values.Length % 2 == 0 ? values[mid - 1] / 2 + values[mid] / 2 : values[mid];
    }
    internal static double Noise(double[] raw)
    {
        int count = Math.Min(raw.Length - 1, 4096); var differences = new double[count];
        for (int i = 0; i < count; i++) { int k = (int)((long)i * (raw.Length - 2) / Math.Max(1, count - 1)); differences[i] = raw[k + 1] - raw[k]; }
        Require(differences.All(IsFinite), "difference_overflow");
        double center = Median((double[])differences.Clone());
        for (int i = 0; i < count; i++) differences[i] = Math.Abs(differences[i] - center);
        return Median(differences) / 0.6744897501960817 / Math.Sqrt(2);
    }
    internal static double Limit(double x, double a, double b) { return Math.Max(a, Math.Min(b, x)); }
    internal static int PeakNear(SignalContext c, int index, int min, int max)
    {
        int radius = Math.Max(1, Math.Min(32, c.ShapeRadius / 2));
        int left = Math.Max(min, index - radius), right = Math.Min(max, index + radius);
        int best = index; double amplitude = -1;
        for (int i = left; i <= right; i++)
        { double a = Math.Abs(c.Raw[i] - c.Baseline); if (a > amplitude) { amplitude = a; best = i; } }
        Require(amplitude > 0, "anchor_has_no_signal");
        Require(Math.Abs(c.Raw[index] - c.Baseline) >= amplitude * 0.05 || c.Input.timing_mode == "first_arrival", "anchor_not_on_packet");
        return best;
    }
    internal sealed class Shape
    {
        internal double[] Values;
        internal double Amplitude, WidthNs, Energy, Asymmetry, Roughness;
        internal int Peak;
    }
    internal static Shape ShapeAt(SignalContext c, int peak)
    {
        Require(peak - c.ShapeRadius >= 0 && peak + c.ShapeRadius < c.Raw.Length, "shape_window_truncated");
        double amplitude = Math.Abs(c.Raw[peak] - c.Baseline);
        Require(amplitude > 0 && IsFinite(amplitude), "shape_amplitude_invalid");
        var values = new double[32]; double energy = 0, roughness = 0;
        for (int j = 0; j < values.Length; j++)
        {
            // Fixed physical time window, independent of sampling rate and cable length.
            double offset = (-64 + 128d * j / 31) * 1e-9 * c.Input.sample_rate_hz;
            double idx = peak + offset; int left = (int)Math.Floor(idx); double fraction = idx - left;
            Require(left >= 0 && left + 1 < c.Raw.Length, "resample_window_truncated");
            values[j] = Limit(((c.Raw[left] - c.Baseline) * (1 - fraction) + (c.Raw[left + 1] - c.Baseline) * fraction) / amplitude, -1, 1);
            energy += values[j] * values[j];
            if (j > 0) roughness += Math.Abs(values[j] - values[j - 1]);
        }
        int a = peak, b = peak;
        while (a > peak - c.ShapeRadius && Math.Abs(c.Raw[a - 1] - c.Baseline) >= amplitude * .5) a--;
        while (b < peak + c.ShapeRadius && Math.Abs(c.Raw[b + 1] - c.Baseline) >= amplitude * .5) b++;
        return new Shape { Values = values, Amplitude = amplitude, WidthNs = (b - a + 1) * 1e9 / c.Input.sample_rate_hz,
            Energy = Math.Max(energy, 1e-12), Asymmetry = (double)(b + a - 2 * peak) / (b - a + 1), Roughness = roughness / 31, Peak = peak };
    }
    internal static double Correlation(double[] a, double[] b, bool envelope)
    {
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { double x = envelope ? Math.Abs(a[i]) : a[i], y = envelope ? Math.Abs(b[i]) : b[i]; dot += x * y; aa += x * x; bb += y * y; }
        return aa > 1e-20 && bb > 1e-20 ? Limit(dot / Math.Sqrt(aa * bb), -1, 1) : 0;
    }
    internal static double[] Features(SignalContext c, int reflection, out int incidentPeak, out int reflectionPeak)
    {
        incidentPeak = PeakNear(c, c.Incident, 0, Math.Min(c.Raw.Length - 1, reflection - 1));
        reflectionPeak = PeakNear(c, reflection, incidentPeak + 1, c.Raw.Length - 1);
        Require(reflectionPeak > incidentPeak && reflectionPeak - incidentPeak <= c.ExactSpan, "morphology_anchor_outside_cable_window");
        Require(reflectionPeak - incidentPeak > c.ShapeRadius, "incident_reflection_packets_overlap");
        var a = ShapeAt(c, incidentPeak); var b = ShapeAt(c, reflectionPeak);
        double sigma = c.Noise > 0 ? c.Noise : Math.Max(a.Amplitude, b.Amplitude) * 1e-12;
        Require(a.Amplitude / sigma >= 3 && b.Amplitude / sigma >= 3, "packet_below_noise_floor");
        var f = new double[FeatureCount];
        f[0] = Correlation(a.Values, b.Values, false); f[1] = Correlation(a.Values, b.Values, true);
        f[2] = Limit(Math.Log(b.Amplitude / a.Amplitude) / 3, -2, 2);
        f[3] = Limit(Math.Log(b.WidthNs / a.WidthNs) / 3, -2, 2);
        f[4] = Limit(Math.Log(b.Energy / a.Energy) / 3, -2, 2);
        f[5] = Math.Sign(c.Raw[a.Peak] - c.Baseline) * Math.Sign(c.Raw[b.Peak] - c.Baseline);
        f[6] = Limit(Math.Log(1 + a.Amplitude / sigma) / 8, 0, 2);
        f[7] = Limit(Math.Log(1 + b.Amplitude / sigma) / 8, 0, 2);
        f[8] = a.Asymmetry; f[9] = b.Asymmetry; f[10] = a.Roughness; f[11] = b.Roughness;
        Array.Copy(a.Values, 0, f, 12, 32); Array.Copy(b.Values, 0, f, 44, 32);
        Require(f.All(IsFinite), "feature_nonfinite"); return f;
    }

    /// <summary>Captures a positive or an explicitly labelled negative pair; stores morphology, never the full waveform.</summary>
    public string BuildSample(double[] rawSignal, string metadataJson)
    {
        try
        {
            var c = Context(rawSignal, metadataJson); var x = c.Input;
            Require(x.chosen_reflection_index.HasValue && x.chosen_reflection_index > c.Incident && x.chosen_reflection_index < rawSignal.Length, "reflection_index_invalid");
            int reflection = x.chosen_reflection_index.Value; double delay = reflection - c.Incident;
            Require(delay <= c.ExactSpan, "pair_outside_exact_cable_window");
            Require((x.label_source == "manual_confirmed" && x.pair_label == "positive") || (x.label_source == "manual_rejected" && x.pair_label == "negative") || (x.label_source == "weak_bootstrap" && x.pair_label == "positive") || (x.label_source == "project_rule_negative" && x.pair_label == "negative"), "label_authority_invalid");
            Require(GoodText(x.operator_id, 160) && GoodText(x.discharge_type, 160), "label_provenance_invalid");
            int ip, rp; var features = Features(c, reflection, out ip, out rp);
            if (x.canonical_incident_index.HasValue) Require(Math.Abs((long)x.canonical_incident_index.Value - ip) <= c.ShapeRadius, "canonical_anchor_inconsistent");
            int globalReflection = checked(c.SourceIncident + reflection - c.Incident);
            double position = Position(x, delay);
            Require(IsFinite(position) && position >= 0 && position <= x.cable_length_m, "position_outside_cable");
            bool human = x.label_source == "manual_confirmed" || x.label_source == "manual_rejected";
            if (x.selected_threshold_pc.HasValue) Require(IsFinite(x.selected_threshold_pc.Value) && x.selected_threshold_pc.Value > 0 && x.selected_threshold_pc.Value < 1e15, "selected_threshold_invalid");
            if (x.automatic_threshold_multiplier.HasValue) Require(IsFinite(x.automatic_threshold_multiplier.Value) && x.automatic_threshold_multiplier.Value > 0 && x.automatic_threshold_multiplier.Value <= 512, "automatic_multiplier_invalid");
            Require(x.threshold_mode == "manual" || x.threshold_mode == "automatic" || x.threshold_mode == "unknown", "threshold_mode_invalid");
            Require(!x.threshold_human_confirmed || (human && x.threshold_mode == "manual" && x.selected_threshold_pc.HasValue), "independent_threshold_confirmation_required");
            var s = new Sample { ok = true, schema = SampleSchema, sample_id = Guid.NewGuid().ToString("N"), created_utc = DateTime.UtcNow.ToString("o"),
                scope_key = x.scope_key, project_key = x.project_key, record_key = x.record_key, source_sha256 = x.source_sha256, calibration_sha256 = x.calibration_sha256,
                discharge_type = x.discharge_type, timing_mode = x.timing_mode, algorithm_index = x.algorithm_index.Value,
                incident_index = c.Incident, source_incident_index = c.SourceIncident, reflection_index = reflection, source_reflection_index = globalReflection,
                exact_round_trip_samples = c.ExactSpan, cable_length_m = x.cable_length_m, sample_rate_hz = x.sample_rate_hz, position_m = position,
                endpoint_band_m = x.endpoint_band_m, segment_edges_m = x.segment_edges_m, segment_velocities_m_per_us = x.segment_velocities_m_per_us,
                endpoint_class = position <= x.endpoint_band_m ? "start" : position >= x.cable_length_m - x.endpoint_band_m ? "end" : "interior",
                features = features, pair_label = x.pair_label, label_source = x.label_source, label_weight = human ? 1 : .15, operator_id = x.operator_id,
                noise_sigma_pc = c.Noise, noise_schema = NoiseSchema, threshold_mode = x.threshold_mode,
                selected_threshold_pc = x.selected_threshold_pc, automatic_threshold_multiplier = x.automatic_threshold_multiplier,
                threshold_human_confirmed = x.threshold_human_confirmed,
                threshold_log_noise_ratio = c.Noise > 0 && x.selected_threshold_pc.HasValue ? (double?)Math.Log(x.selected_threshold_pc.Value / c.Noise) : null };
            s.checksum = SampleDigest(s); ValidateSample(s); return Json(s);
        }
        catch (Exception ex) { return Failure("sample_rejected", ex.Message); }
    }
}
