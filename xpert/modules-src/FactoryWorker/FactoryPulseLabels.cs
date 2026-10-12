using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HVX.PD.PulseToolkit;
using HVX.PD.TimeFrequencyClustering;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryPulseTemplate
    {
        public int PeakSample, PeakOffset;
        public string SourceId = "", Method = "native-baseline-hilbert-v1";
        public double TimeUs, FrequencyMHz, RateHz;
        // Frozen, normalized native samples. Not display-decimated samples.
        public double[] Wave = new double[0], Envelope = new double[0];
        public void Validate()
        {
            if (Method != "native-baseline-hilbert-v1" || String.IsNullOrWhiteSpace(SourceId) || SourceId.Length > 4096 || PeakSample < 0 ||
                !FactorySettings.Positive(TimeUs) || !FactorySettings.Positive(FrequencyMHz) || !FactorySettings.Positive(RateHz) || RateHz > 1e9 ||
                Wave == null || Envelope == null || Wave.Length < 16 || Wave.Length > 65536 || Wave.Length != Envelope.Length || PeakOffset < 0 || PeakOffset >= Wave.Length ||
                Wave.Any(v => !FactorySettings.Finite(v) || Math.Abs(v) > 1.00000001) || Envelope.Any(v => !FactorySettings.Finite(v) || v < 0 || v > 1.00000001) ||
                Math.Abs(Wave.Max(v => Math.Abs(v)) - 1) > 1e-8 || Math.Abs(Envelope.Max() - 1) > 1e-8)
                throw new InvalidDataException("脉冲模板的原始采样、包络或归一化参数无效。");
        }
    }
    internal sealed class FactoryPulseMatch
    {
        internal string Text = "未识别", Identity = "";
        internal bool Matched, Conflict, Filter;
        internal double TimeError, FrequencyError, WaveError, EnvelopeError;
        internal FactoryTfAnnotationRegion Label;
        internal List<FactoryTfAnnotationRegion> Candidates = new List<FactoryTfAnnotationRegion>();
    }
    internal static class FactoryPulseLabels
    {
        internal const double Tolerance = .01;
        internal static FactoryPulseTemplate Create(FactoryTfDisplayPoint point, double[] native)
        {
            if (point == null || !point.Pulse.Valid || point.Pulse.Clipped || point.Round.DriverOverflow || native == null ||
                native.Length != point.Pulse.EndSample - point.Pulse.StartSample + 1 || native.Length < 16 || native.Length > 65536 || native.Any(v => !FactorySettings.Finite(v)))
                throw new InvalidDataException("只能标记有效、未削顶且有完整原始波形的脉冲。");
            var centered = native.Select(v => v - point.Round.BaselineV).ToArray();
            double peak = centered.Max(v => Math.Abs(v));
            if (!FactorySettings.Positive(peak)) throw new InvalidDataException("零幅度脉冲不能建立识别模板。");
            var envelope = InternalPulseDsp.AnalyticEnvelope(centered); double ePeak = envelope.Max();
            var value = new FactoryPulseTemplate { SourceId = point.Round.SourceId, PeakSample = point.Pulse.PeakSample,
                PeakOffset = point.Pulse.PeakSample - point.Pulse.StartSample, TimeUs = point.Pulse.EquivalentTimeUs,
                FrequencyMHz = point.Pulse.EquivalentBandwidthMHz, RateHz = point.Round.PicoRateHz,
                Wave = centered.Select(v => v / peak).ToArray(), Envelope = envelope.Select(v => v / ePeak).ToArray() };
            value.Validate(); return value;
        }
        internal static double[] Envelope(double[] native, double baseline)
        { return InternalPulseDsp.AnalyticEnvelope(native.Select(v => v - baseline).ToArray()); }
        internal static bool Within(double a, double b) { return FactorySettings.Positive(a) && FactorySettings.Positive(b) && Math.Abs(a - b) / b <= Tolerance + 1e-12; }
        private static double Relative(double a, double b) { return Math.Abs(a - b) / b; }
        internal static FactoryPulseMatch Match(FactoryTfAnnotationDocument document, FactoryTfDisplayPoint point, FactoryPulseTemplate candidate)
        {
            var result = new FactoryPulseMatch();
            if (document == null || document.ChainKey != point.Round.ChainKey || !point.Pulse.Valid || point.Pulse.Clipped || point.Round.DriverOverflow) return result;
            candidate.Validate(); var hits = new List<FactoryPulseMatch>();
            foreach (var region in document.Regions)
            foreach (var template in region.Templates)
            {
                if (!Within(candidate.TimeUs, template.TimeUs) || !Within(candidate.FrequencyMHz, template.FrequencyMHz) || candidate.RateHz != template.RateHz) continue;
                // Same physical sample spacing, aligned by native peak. Never
                // time-stretch a pulse to force a match (100 noise-template rule).
                int offset = candidate.PeakOffset - template.PeakOffset;
                if (offset < 0 || offset + template.Wave.Length > candidate.Wave.Length) continue;
                double waveError = 0, envelopeError = 0;
                for (int i = 0; i < template.Wave.Length; i++)
                { waveError = Math.Max(waveError, Math.Abs(template.Wave[i] - candidate.Wave[i + offset])); envelopeError = Math.Max(envelopeError, Math.Abs(template.Envelope[i] - candidate.Envelope[i + offset])); }
                if (waveError > Tolerance + 1e-12 || envelopeError > Tolerance + 1e-12) continue;
                // A short template cannot ignore an extra lobe outside its packet.
                if (candidate.Wave.Take(offset).Concat(candidate.Wave.Skip(offset + template.Wave.Length)).Any(v => Math.Abs(v) > Tolerance)) continue;
                string identity = ((int)region.Kind) + "|" + region.Name + "|" + region.Defect + "|" + region.PossibleInterferenceSource;
                hits.Add(new FactoryPulseMatch { Matched = true, Label = region, Identity = identity,
                    Filter = region.Kind == FactoryTfLabelKind.SuspectedInterference && region.FilterInterference,
                    TimeError = Relative(candidate.TimeUs, template.TimeUs), FrequencyError = Relative(candidate.FrequencyMHz, template.FrequencyMHz), WaveError = waveError, EnvelopeError = envelopeError });
            }
            if (hits.Count == 0) return result;
            var labels = hits.Select(h => h.Label).GroupBy(r => r.Id).Select(g => g.First()).ToList();
            if (hits.Select(h => h.Identity).Distinct().Count() > 1)
            { result.Candidates = labels; result.Conflict = true; result.Text = "标签冲突（不自动过滤）：" + String.Join(" / ", hits.Select(h => h.Label.Name).Distinct()); return result; }
            result = hits.OrderBy(h => Math.Max(Math.Max(h.TimeError, h.FrequencyError), Math.Max(h.WaveError, h.EnvelopeError))).First();
            result.Candidates = labels;
            // Contradictory filter permission stays conservative.
            result.Filter = hits.All(h => h.Filter);
            result.Text = "匹配人工标签：" + result.Label.Name + "（" + FactoryTfAnnotationRegion.KindText(result.Label.Kind) + "）" +
                (String.IsNullOrWhiteSpace(result.Label.Defect) ? "" : "；现象：" + result.Label.Defect) +
                (String.IsNullOrWhiteSpace(result.Label.PossibleInterferenceSource) ? "" : "；可能来源：" + result.Label.PossibleInterferenceSource) +
                "；T/F/原波/包络偏差=" + String.Join("/", new[] { result.TimeError, result.FrequencyError, result.WaveError, result.EnvelopeError }.Select(v => (v * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%")) +
                (result.Filter ? "；干扰模板已授权派生过滤" : "；不排除原始脉冲");
            return result;
        }
        internal static FactoryPulseMatch Selection(IEnumerable<FactoryPulseMatch> source)
        {
            var values = source.ToArray();
            if (values.Length == 0 || values.Any(v => !v.Matched)) return new FactoryPulseMatch { Text = "未识别（选区内含未匹配或冲突脉冲；不以多数投票代替逐脉冲1%判据）" };
            if (values.Select(v => v.Identity).Distinct().Count() != 1) return new FactoryPulseMatch { Conflict = true, Text = "混合标签：" + String.Join(" / ", values.Select(v => v.Label.Name).Distinct()) };
            return values[0];
        }
        internal static List<FactoryTfAnnotationRegion> EditableLabels(FactoryTfAnnotationDocument document,
            IEnumerable<FactoryTfDisplayPoint> points, Func<FactoryTfDisplayPoint, FactoryPulseMatch> matchFor)
        {
            if (document == null) return new List<FactoryTfAnnotationRegion>();
            var sameChain = points.Where(p => p != null && p.Round.ChainKey == document.ChainKey).ToList();
            var ids = new HashSet<string>(document.MatchMap(sameChain).Values.SelectMany(v => v).Select(r => r.Id));
            if (matchFor != null) foreach (var point in sameChain)
            {
                var match = matchFor(point); if (match == null) continue;
                foreach (var label in match.Candidates) ids.Add(label.Id);
                if (match.Label != null && match.Matched) ids.Add(match.Label.Id);
            }
            // Resolve matching snapshots back to the actual library objects.
            // Conflict candidates may be edited individually, never auto-merged.
            return document.Regions.Where(r => ids.Contains(r.Id)).ToList();
        }
    }
    internal sealed class FactoryClusterResult
    {
        internal readonly Dictionary<string, int> Groups = new Dictionary<string, int>();
        internal readonly Dictionary<int, List<FactoryTfDisplayPoint>> Members = new Dictionary<int, List<FactoryTfDisplayPoint>>();
        internal string Detail;
    }
    internal static class FactoryTfClustering
    {
        internal static FactoryClusterResult Analyze(List<FactoryTfDisplayPoint> points)
        {
            var valid = points.Where(p => p.Pulse.Valid).ToArray(); var result = new FactoryClusterResult();
            if (valid.Length == 0) { result.Detail = "暂无有效点"; return result; }
            // Exact task 27 engine, X Hz / Y ns; preserve every native identity.
            var xy = new double[valid.Length, 2];
            for (int i = 0; i < valid.Length; i++) { xy[i, 0] = valid[i].Pulse.EquivalentBandwidthMHz * 1e6; xy[i, 1] = valid[i].Pulse.EquivalentTimeUs * 1000; }
            ClusterPointSet[] clusters; ClusterPointSet noise, invalid; int[] labels;
            new TimeFrequencyClusterEngine().GetXYClusters(xy, out clusters, out noise, out invalid, out labels);
            for (int i = 0; i < valid.Length; i++)
            {
                result.Groups[FactoryTfAnnotationDocument.PointKey(valid[i])] = labels[i];
                List<FactoryTfDisplayPoint> group;
                if (!result.Members.TryGetValue(labels[i], out group)) result.Members[labels[i]] = group = new List<FactoryTfDisplayPoint>();
                group.Add(valid[i]);
            }
            result.Detail = "任务27自动聚类：" + clusters.Length + " 类；离群 " + noise.PointCount + "；类别编号仅本视图有效，不代表物理源身份";
            return result;
        }
    }
    internal static class FactoryLabelStore
    {
        internal static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Xiongxin", "FactoryAcceptance", "PulseLabels"); } }
        internal static string PathFor(string root, string chain)
        { using (var hash = SHA256.Create()) return Path.Combine(root, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(chain))).Replace("-", "") + ".tfann.xml"); }
        internal static void Save(string root, FactoryTfAnnotationDocument document)
        {
            Directory.CreateDirectory(root); string path = PathFor(root, document.ChainKey);
            if (File.Exists(path))
                try { FactoryTfAnnotationDocument.Load(path); }
                catch (Exception ex) { throw new IOException("旧标签库无效，未自动覆盖；请恢复备份或另存标签。", ex); }
            document.Save(path);
        }
        internal static Dictionary<string, FactoryTfAnnotationDocument> Load(string root, Action<string> error)
        {
            var documents = new Dictionary<string, FactoryTfAnnotationDocument>(); var conflicts = new HashSet<string>(); if (!Directory.Exists(root)) return documents;
            foreach (var path in Directory.GetFiles(root, "*.tfann.xml").OrderBy(p => p).Take(101))
            {
                if (documents.Count >= 100) { error("标签库超过100个采集链，剩余文件未加载。"); break; }
                try
                { var doc = FactoryTfAnnotationDocument.Load(path); if (documents.ContainsKey(doc.ChainKey) || conflicts.Contains(doc.ChainKey)) { documents.Remove(doc.ChainKey); conflicts.Add(doc.ChainKey); throw new InvalidDataException("重复采集链，整链未载入，不选择任意模板。"); } documents.Add(doc.ChainKey, doc); }
                catch (Exception ex) { error("标签库未加载 " + Path.GetFileName(path) + "：" + ex.Message); }
            }
            return documents;
        }
    }
}
