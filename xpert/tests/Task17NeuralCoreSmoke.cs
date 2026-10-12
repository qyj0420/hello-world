using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using HVX.Task17.Location;

internal static class Task17NeuralCoreCases
{
    static readonly ReflectionLearningEngine E = new ReflectionLearningEngine();
    static readonly JavaScriptSerializer J = new JavaScriptSerializer { MaxJsonLength = 32000000 };
    static readonly List<object> Results = new List<object>(); static readonly List<string> Failures = new List<string>(); static int Checks;
    // Exercise the integrated DLL, not a second compilation of its source.
    static string Digest(string name, object value)
    { return (string)typeof(ReflectionLearningEngine).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new[] { value }); }
    static Dictionary<string, object> Read(string json) { return J.Deserialize<Dictionary<string, object>>(json); }
    static bool Yes(Dictionary<string, object> d, string k) { return d.ContainsKey(k) && Convert.ToBoolean(d[k]); }
    static void Check(bool ok, string detail) { Checks++; if (!ok) Failures.Add(detail); }
    static void Case(string name, Action action) { int before = Failures.Count, checks = Checks; try { action(); } catch (Exception ex) { Failures.Add(name + ": " + ex); } Results.Add(new { name, passed = before == Failures.Count, checks = Checks - checks }); }
    static string Json(object o) { return J.Serialize(o); }
    static Dictionary<string, object> Meta(int n, int reflection = 120, bool positive = true)
    {
        return new Dictionary<string, object> { { "schema", "reflection-learning-input-v1" }, { "scope_key", "synthetic-neural-test" }, { "project_key", "synthetic-not-field-evidence" },
            { "record_key", "LOC-" + n }, { "source_sha256", n.ToString("x64") }, { "calibration_sha256", new string('a', 64) }, { "algorithm_index", 6 },
            { "discharge_type", "synthetic-not-real" }, { "timing_mode", "peak" }, { "incident_index", 80 }, { "source_incident_index", 800 }, { "chosen_reflection_index", reflection },
            { "sample_rate_hz", 125000000d }, { "exact_round_trip_samples", 200d }, { "cable_length_m", 100d }, { "endpoint_band_m", 10d },
            { "pair_label", positive ? "positive" : "negative" }, { "label_source", positive ? "manual_confirmed" : "manual_rejected" }, { "operator_id", "synthetic-test-harness" },
            { "threshold_mode", "manual" }, { "threshold_human_confirmed", false }, { "selected_threshold_pc", 1d } };
    }
    static double[] Wave(int seed, int reflection = 120, double scale = 1, bool tie = false, bool wrongShape = false)
    {
        var s = new double[400]; var random = new Random(seed);
        for (int i = 0; i < s.Length; i++) { double a = (i - 80d) / 2, b = (i - reflection) / (wrongShape ? .7 : 2d), n = (i - 220d) / .65, t = (i - 170d) / 2;
            s[i] = scale * ((random.NextDouble() - .5) * .08 + 20 * Math.Exp(-a * a / 2) + (wrongShape ? -15 : 25) * Math.Exp(-b * b / 2) - 12 * Math.Exp(-n * n / 2) + (tie ? 25 * Math.Exp(-t * t / 2) : 0)); }
        return s;
    }
    static ReflectionLearningEngine.Sample Sample(int n, int reflection, bool positive, string timing = "peak")
    {
        var metadata = Meta(n, reflection, positive); metadata["timing_mode"] = timing; var raw = Wave(n);
        var s = J.Deserialize<ReflectionLearningEngine.Sample>(E.BuildSample(raw, Json(metadata))); Check(s != null && s.ok, "sample build " + n + ":" + reflection);
        metadata["selected_threshold_pc"] = s.noise_sigma_pc * 4; metadata["threshold_human_confirmed"] = positive;
        s = J.Deserialize<ReflectionLearningEngine.Sample>(E.BuildSample(raw, Json(metadata)));
        s.created_utc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(n * 2 + (positive ? 0 : 1)).ToString("o");
        s.sample_id = "synthetic-" + n + "-" + reflection + "-" + timing; s.checksum = Digest("SampleDigest", s); return s;
    }
    static List<ReflectionLearningEngine.Sample> Rows(int count = 8) { var r = new List<ReflectionLearningEngine.Sample>(); for (int n = 1; n <= count; n++) { r.Add(Sample(n, 120, true)); r.Add(Sample(n, 220, false)); } return r; }
    static ReflectionLearningEngine.NeuralModel Model(List<ReflectionLearningEngine.Sample> rows)
    { string json = E.TrainNeural(Json(rows)); Check(Yes(Read(json), "ok"), "training ok " + json.Substring(0, Math.Min(160, json.Length))); return J.Deserialize<ReflectionLearningEngine.NeuralModel>(json); }
    static void Rehash(ReflectionLearningEngine.Sample s) { s.checksum = Digest("SampleDigest", s); }
    internal static int Run(string[] args)
    {
        string destination = args[0]; var rows = Rows(); var m = Model(rows); string model = Json(m);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(destination), "synthetic-test-only-trained-model.json"), model);
        if (args.Length > 1) { Directory.CreateDirectory(args[1]); string empty = E.TrainNeural("[]"); File.WriteAllText(Path.Combine(args[1], "method7-neural-awaiting-manual-labels.json"), empty); File.WriteAllText(Path.Combine(args[1], "method7-neural-awaiting-manual-labels.inspect.json"), E.InspectNeuralModel(empty)); }
        Case("real_mlp_learning_and_heldout_gate", () => { Check(m.partitions.Count == 1, "one partition"); var p = m.partitions[0]; Check(p.epochs == 200 && p.hidden_weights.Length == 12 && p.hidden_weights[0].Length == 76, "MLP dimensions/training"); Check(p.validation.passed, "heldout pass"); Check(p.validation.auc >= .8 && p.validation.brier < .25, "heldout discrimination"); Check(p.training_sources.Length == 6 && p.heldout_sources.Length == 2 && !p.training_sources.Intersect(p.heldout_sources).Any(), "LOC group separated"); Check(!p.hidden_weights[0].SequenceEqual(new double[76]), "actual trained weights"); });
        Case("deterministic_training", () => { var b = Model(rows); Check(m.checksum == b.checksum, "deterministic model digest"); });
        for (int round = 0; round < 20; round++)
        {
            int seed = 101 + round;
            Case("round_" + round + "_unseen_loc_actual_peak_and_manual_threshold", () => { var r = Read(E.SuggestNeural(Wave(seed), Json(Meta(seed)), model)); Check(Yes(r, "ok") && Yes(r, "reflection_available"), "unseen LOC suggestion " + Json(r)); Check(Convert.ToInt32(r["suggested_reflection_index"]) == 120, "true synthetic peak chosen"); Check(!Yes(r, "automatic_acceptance") && !Yes(r, "position_is_feature") && !Yes(r, "score_is_calibrated_probability"), "review-only morphology score"); Check(Yes(r, "threshold_available") && Math.Abs(Convert.ToDouble(r["suggested_threshold_multiplier"]) - 4) < 1e-9, "manual threshold learned"); });
            Case("round_" + round + "_amplitude_and_delay_transfer", () => { var q = Meta(seed, 150); var r = Read(E.SuggestNeural(Wave(seed, 150, 7), Json(q), model)); Check(Yes(r, "reflection_available") && Convert.ToInt32(r["suggested_reflection_index"]) == 150, "shifted peak selected without target position"); Check(Yes(r, "threshold_available"), "threshold scales"); var a = Read(E.SuggestNeural(Wave(seed, 150), Json(q), model)); Check(Math.Abs(Convert.ToDouble(r["suggested_threshold_pc"]) / Convert.ToDouble(a["suggested_threshold_pc"]) - 7) < 1e-8, "threshold scale invariant"); });
        }
        Case("no_self_evaluation_train_and_validation_sources", () => { foreach (var source in m.global_training_sources.Concat(m.global_heldout_sources)) { var q = Meta(999); q["source_sha256"] = source; var r = Read(E.SuggestNeural(Wave(999), Json(q), model)); Check(!Yes(r, "reflection_available") && !Yes(r, "threshold_available") && Yes(r, "current_source_seen"), "all seen sources abstain"); } });
        Case("empty_and_insufficient_labels_fail_closed", () => { var empty = Model(new List<ReflectionLearningEngine.Sample>()); Check(empty.status == "awaiting_manual_labels" && empty.bootstrap_count == 0, "empty cold start"); var r = Read(E.SuggestNeural(Wave(999), Json(Meta(999)), Json(empty))); Check(Yes(r, "ok") && !Yes(r, "reflection_available"), "empty abstains"); var few = Model(Rows(3)); Check(few.partitions.All(p => p.epochs == 0), "insufficient no fit"); });
        Case("weak_and_project_rule_samples_never_train", () => { var weak = Rows(); foreach (var s in weak) { s.label_source = s.pair_label == "positive" ? "weak_bootstrap" : "project_rule_negative"; s.label_weight = .15; s.threshold_human_confirmed = false; Rehash(s); } var w = Model(weak); Check(w.records.Count == 0 && w.bootstrap_count == 0 && w.partitions.Count == 0 && w.excluded_nonhuman_count == 16, "weak excluded from all weights"); });
        Case("weak_cannot_override_new_or_old_manual", () => { var r = Rows(); var weak = J.Deserialize<ReflectionLearningEngine.Sample>(Json(r[0])); weak.sample_id = "weak-later"; weak.created_utc = DateTime.UtcNow.ToString("o"); weak.pair_label = "negative"; weak.label_source = "project_rule_negative"; weak.label_weight = .15; weak.threshold_human_confirmed = false; Rehash(weak); r.Add(weak); var b = Model(r); Check(b.positive_count == 8 && b.negative_count == 8 && b.overridden_count == 1, "manual authoritative before weak filter"); });
        Case("duplicate_timestamp_and_manual_correction", () => { var r = Rows(); r.Add(r[0]); var b = Model(r); Check(b.records.Count == 16 && b.duplicate_count == 1, "idempotent duplicate"); var corrected = J.Deserialize<ReflectionLearningEngine.Sample>(Json(r[0])); corrected.created_utc = DateTime.UtcNow.ToString("o"); corrected.sample_id = "manual-correction"; corrected.pair_label = "negative"; corrected.label_source = "manual_rejected"; corrected.threshold_human_confirmed = false; Rehash(corrected); r.Add(corrected); b = Model(r); Check(b.positive_count == 7 && b.negative_count == 9, "latest manual label wins"); });
        Case("method6_and_other_methods_excluded", () => { var r = Rows(); foreach (var s in r) { s.algorithm_index = 0; Rehash(s); } var b = Model(r); Check(b.records.Count == 0 && b.excluded_other_method_count == 16, "other physical methods not train"); r[0].algorithm_index = 5; Rehash(r[0]); Check(!Yes(Read(E.TrainNeural(Json(r))), "ok"), "method6 sample rejected"); var q = Meta(999); q["algorithm_index"] = 5; Check(!Yes(Read(E.SuggestNeural(Wave(999), Json(q), model)), "ok"), "method6 query rejected"); });
        Case("partition_isolation_and_global_LOC_split", () => { var r = Rows(); var copy = Rows(); foreach (var s in copy) { s.project_key = "other-project"; Rehash(s); } r.AddRange(copy); var b = Model(r); Check(b.partitions.Count == 2, "two independent networks"); Check(b.partitions[0].training_sources.SequenceEqual(b.partitions[1].training_sources) && b.partitions[0].heldout_sources.SequenceEqual(b.partitions[1].heldout_sources), "same LOC always same global side"); var q = Meta(999); q["calibration_sha256"] = new string('b', 64); var d = Read(E.SuggestNeural(Wave(999), Json(q), model)); Check(!Yes(d, "reflection_available") && !Yes(d, "threshold_available"), "calibration mismatch abstain"); });
        Case("manual_threshold_only", () => { var r = Rows(); foreach (var s in r) { s.threshold_human_confirmed = false; s.threshold_mode = "automatic"; Rehash(s); } var b = Model(r); var d = Read(E.SuggestNeural(Wave(999), Json(Meta(999)), Json(b))); Check(!Yes(d, "threshold_available"), "automatic threshold not trained"); });
        Case("first_arrival_manual_timing_only", () => { var q = Meta(999); q["timing_mode"] = "first_arrival"; var d = Read(E.SuggestNeural(Wave(999), Json(q), model)); Check(!Yes(d, "reflection_available") && Convert.ToString(d["status"]) == "first_arrival_requires_manual_timing", "no automatic first anchor"); });
        Case("near_tie_and_negative_pattern_abstain", () => { var d = Read(E.SuggestNeural(Wave(999, 120, 1, true), Json(Meta(999)), model)); Check(!Yes(d, "reflection_available"), "near tie abstain " + Json(d)); d = Read(E.SuggestNeural(Wave(999, 120, 1, false, true), Json(Meta(999)), model)); Check(!Yes(d, "reflection_available"), "negative waveform abstain"); });
        Case("nonfinite_waveform_and_artifact_tamper_rejected", () => { var w = Wave(999); w[3] = Double.NaN; Check(!Yes(Read(E.SuggestNeural(w, Json(Meta(999)), model)), "ok"), "NaN rejected"); var b = J.Deserialize<ReflectionLearningEngine.NeuralModel>(model); b.partitions[0].output_weights[0] += .1; Check(!Yes(Read(E.InspectNeuralModel(Json(b))), "ok"), "weight tamper checksum rejected"); b.checksum = Digest("NeuralDigest", b); b.partitions[0].hidden_weights[0] = new double[75]; b.checksum = Digest("NeuralDigest", b); Check(!Yes(Read(E.InspectNeuralModel(Json(b))), "ok"), "dimension tamper rejected after rehash"); });
        Case("resigned_weights_cannot_reuse_stale_heldout_metrics", () => { var b = J.Deserialize<ReflectionLearningEngine.NeuralModel>(model); b.partitions[0].output_bias += .5; b.checksum = Digest("NeuralDigest", b); var result = Read(E.InspectNeuralModel(Json(b))); Check(!Yes(result, "ok") && Convert.ToString(result["detail"]).Contains("metrics_do_not_match"), "recompute validation rejects resigned changed weights"); });
        Case("anonymous_query_geometry_not_score_input", () => { var a = Meta(998); var b = Meta(999); b["cable_length_m"] = 150d; b["record_key"] = "anonymous-renamed"; b["source_incident_index"] = 70000; var first = Read(E.SuggestNeural(Wave(999), Json(a), model)); var second = Read(E.SuggestNeural(Wave(999), Json(b), model)); var ac = J.Deserialize<List<ReflectionLearningEngine.NeuralCandidate>>(Json(first["candidates"])); var bc = J.Deserialize<List<ReflectionLearningEngine.NeuralCandidate>>(Json(second["candidates"])); Check(ac.Count == bc.Count && ac.Zip(bc, (x,y) => x.index == y.index && x.score == y.score).All(x => x), "anonymous identity and valid cable geometry do not affect neural score"); });
        Case("heldout_gate_fails_for_inverted_unseen_source_labels", () => { var r = Rows(); foreach (var s in r.Where(s => m.global_heldout_sources.Contains(s.source_sha256))) { s.pair_label = s.pair_label == "positive" ? "negative" : "positive"; s.label_source = s.pair_label == "positive" ? "manual_confirmed" : "manual_rejected"; s.threshold_human_confirmed = false; Rehash(s); } var b = Model(r); Check(!b.partitions[0].validation.passed && b.partitions[0].validation.auc < .5, "bad heldout blocks"); var d = Read(E.SuggestNeural(Wave(999), Json(Meta(999)), Json(b))); Check(!Yes(d, "reflection_available"), "failed validation no suggestion"); });
        Case("position_and_identity_not_network_features", () => { var r = Rows(); foreach (var s in r) { s.cable_length_m = 150; s.position_m = s.cable_length_m * (1 - (s.reflection_index - s.incident_index) / s.exact_round_trip_samples); s.endpoint_class = s.position_m <= s.endpoint_band_m ? "start" : s.position_m >= s.cable_length_m - s.endpoint_band_m ? "end" : "interior"; s.record_key = "renamed"; Rehash(s); } var b = Model(r); Check(Json(m.partitions[0].hidden_weights) == Json(b.partitions[0].hidden_weights) && Json(m.partitions[0].output_weights) == Json(b.partitions[0].output_weights), "geometry/id change does not change weights"); });
        Case("physical_window_incomplete_never_suggests", () => { var d = Read(E.SuggestNeural(Wave(999).Take(260).ToArray(), Json(Meta(999)), model)); Check(!Yes(d, "reflection_available") && !Yes(d, "candidate_window_complete"), "truncated cable window abstains"); });
        var output = new { ok = Failures.Count == 0, synthetic_only = true, real_field_labels_trained = false, cases = Results.Count, checks = Checks, results = Results, failures = Failures, model_checksum = m.checksum, validation = m.partitions[0].validation };
        File.WriteAllText(destination, Json(output)); Console.WriteLine(Json(new { output.ok, output.cases, output.checks, output.failures, destination })); return Failures.Count == 0 ? 0 : 1;
    }
}

internal static class Task17NeuralCoreSmoke
{
    static string ResolveDll(string input)
    {
        if (File.Exists(input)) return Path.GetFullPath(input);
        string flat = Path.Combine(input, "fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
        return Path.GetFullPath(File.Exists(flat) ? flat : Path.Combine(input, "modules", "DataAnalysis", "fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"));
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Build/DLL and QA directory required.");
            string dll = ResolveDll(args[0]), qa = Path.GetFullPath(args[1]);
            if (qa.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("QA root must be beneath build.");
            Directory.CreateDirectory(qa);
            var assembly = System.Reflection.Assembly.LoadFile(dll);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) { return e.Name == assembly.FullName ? assembly : null; };
            Console.WriteLine("INTEGRATED_LOCATION_DLL=" + dll);
            return InvokeCases(Path.Combine(qa, "neural-core-regression.json"));
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int InvokeCases(string destination)
    { return Task17NeuralCoreCases.Run(new[] { destination }); }
}
