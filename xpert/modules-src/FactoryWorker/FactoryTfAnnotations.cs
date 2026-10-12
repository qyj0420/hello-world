using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    public enum FactoryTfRegionShape { Rectangle, Ellipse, Circle }
    public enum FactoryTfLabelKind { Undetermined, SuspectedDefect, SuspectedInterference, Other }
    public sealed class FactoryTfRegionGeometry
    {
        public FactoryTfRegionShape Shape;
        public double MinimumFrequencyMHz, MaximumFrequencyMHz, MinimumTimeUs, MaximumTimeUs;
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(FactoryTfRegionShape), Shape) ||
                !FactorySettings.Finite(MinimumFrequencyMHz) || !FactorySettings.Finite(MaximumFrequencyMHz) ||
                !FactorySettings.Finite(MinimumTimeUs) || !FactorySettings.Finite(MaximumTimeUs) ||
                MinimumFrequencyMHz < 0 || MinimumTimeUs < 0 || MaximumFrequencyMHz > 1000000 || MaximumTimeUs > 1000000000 || MaximumFrequencyMHz <= MinimumFrequencyMHz || MaximumTimeUs <= MinimumTimeUs)
                throw new InvalidDataException("圈选区域的TF坐标或形状无效。");
        }
        internal bool Contains(double frequency, double time)
        {
            if (!FactorySettings.Finite(frequency) || !FactorySettings.Finite(time)) return false;
            double x = (frequency - MinimumFrequencyMHz) / (MaximumFrequencyMHz - MinimumFrequencyMHz),
                y = (time - MinimumTimeUs) / (MaximumTimeUs - MinimumTimeUs);
            if (x < -1e-10 || x > 1 + 1e-10 || y < -1e-10 || y > 1 + 1e-10) return false;
            return Shape == FactoryTfRegionShape.Rectangle || Math.Pow(2 * x - 1, 2) + Math.Pow(2 * y - 1, 2) <= 1 + 1e-10;
        }
        internal FactoryTfRegionGeometry Copy()
        { return new FactoryTfRegionGeometry { Shape = Shape, MinimumFrequencyMHz = MinimumFrequencyMHz, MaximumFrequencyMHz = MaximumFrequencyMHz, MinimumTimeUs = MinimumTimeUs, MaximumTimeUs = MaximumTimeUs }; }
        internal static FactoryTfRegionGeometry FromDrag(Rectangle plot, Point first, Point last, bool oval, double xmax, double ymax)
        {
            if (plot.Width <= 0 || plot.Height <= 0 || !FactorySettings.Positive(xmax) || !FactorySettings.Positive(ymax)) return null;
            first = Clamp(plot, first); last = Clamp(plot, last);
            float left = Math.Min(first.X, last.X), top = Math.Min(first.Y, last.Y), width = Math.Abs(last.X - first.X), height = Math.Abs(last.Y - first.Y);
            if (width < 6 || height < 6) return null;
            var shape = oval ? FactoryTfRegionShape.Ellipse : FactoryTfRegionShape.Rectangle;
            if (oval && Math.Abs(width - height) <= Math.Max(2, Math.Max(width, height) * .05))
            {
                // Near-square gestures snap to a genuine screen-space circle.
                float side = Math.Min(width, height); left += (width - side) / 2; top += (height - side) / 2;
                width = height = side; shape = FactoryTfRegionShape.Circle;
            }
            return new FactoryTfRegionGeometry { Shape = shape, MinimumFrequencyMHz = (left - plot.Left) / plot.Width * xmax,
                MaximumFrequencyMHz = (left + width - plot.Left) / plot.Width * xmax,
                MinimumTimeUs = (plot.Bottom - top - height) / plot.Height * ymax, MaximumTimeUs = (plot.Bottom - top) / plot.Height * ymax };
        }
        private static Point Clamp(Rectangle p, Point at)
        { return new Point(Math.Max(p.Left, Math.Min(p.Right, at.X)), Math.Max(p.Top, Math.Min(p.Bottom, at.Y))); }
    }
    public sealed class FactoryTfAnnotatedRound
    { public string Id = Guid.NewGuid().ToString("N"); public FactoryTfResult Data; }
    public sealed class FactoryTfAnnotationMember
    { public string RoundId = ""; public int PeakSample; }
    public sealed class FactoryTfAnnotationRegion
    {
        public string Id = Guid.NewGuid().ToString("N"), Name = "", Defect = "", PossibleInterferenceSource = "", Notes = "", Author = "";
        public FactoryTfLabelKind Kind;
        public bool FilterInterference;
        public string MarkedSurface = "TF";
        public List<FactoryPulseTemplate> Templates = new List<FactoryPulseTemplate>();
        public FactoryTfRegionGeometry Geometry = new FactoryTfRegionGeometry();
        public DateTime CreatedUtc = DateTime.UtcNow, ModifiedUtc = DateTime.UtcNow;
        public List<FactoryTfAnnotationMember> Members = new List<FactoryTfAnnotationMember>();
        internal void ValidateText()
        {
            Guid id;
            if (!Guid.TryParseExact(Id, "N", out id) || !Enum.IsDefined(typeof(FactoryTfLabelKind), Kind) || String.IsNullOrWhiteSpace(Name) || Name.Length > 80 ||
                Defect == null || Defect.Length > 200 || PossibleInterferenceSource == null || PossibleInterferenceSource.Length > 500 ||
                Notes == null || Notes.Length > 4000 || Author == null || Author.Length > 80 ||
                (FilterInterference && Kind != FactoryTfLabelKind.SuspectedInterference) || MarkedSurface == null || MarkedSurface.Length > 40 ||
                CreatedUtc.Kind != DateTimeKind.Utc || ModifiedUtc.Kind != DateTimeKind.Utc || CreatedUtc == DateTime.MinValue || ModifiedUtc < CreatedUtc || ModifiedUtc > DateTime.MaxValue.AddHours(-8))
                throw new InvalidDataException("人工标注名称、字段长度或时间无效。");
        }
        internal static string KindText(FactoryTfLabelKind kind)
        { return new[] { "待确定", "疑似缺陷", "疑似干扰", "其他人工标签" }[(int)kind]; }
        internal static string ShapeText(FactoryTfRegionShape shape)
        { return shape == FactoryTfRegionShape.Rectangle ? "矩形" : shape == FactoryTfRegionShape.Circle ? "圆形" : "椭圆"; }
    }
    public sealed class FactoryTfAnnotationDocument
    {
        public int SchemaVersion = 1;
        public string ChainKey = "";
        public string Boundary = "人工经验标注，包含疑似缺陷及可能干扰源；不是自动诊断或确认结论，不排除脉冲、不改变原始数据及保护逻辑。";
        public DateTime CreatedUtc = DateTime.UtcNow, ModifiedUtc = DateTime.UtcNow;
        public List<FactoryTfAnnotatedRound> Rounds = new List<FactoryTfAnnotatedRound>();
        public List<FactoryTfAnnotationRegion> Regions = new List<FactoryTfAnnotationRegion>();
        [XmlIgnore] public string FilePath = "";
        [XmlIgnore] public bool Dirty;
        internal static FactoryTfAnnotationDocument Clone(FactoryTfAnnotationDocument source)
        {
            var xml = new XmlSerializer(typeof(FactoryTfAnnotationDocument));
            using (var stream = new MemoryStream())
            { xml.Serialize(stream, source); stream.Position = 0; var copy = (FactoryTfAnnotationDocument)xml.Deserialize(stream); copy.FilePath = source.FilePath; return copy; }
        }
        internal static FactoryTfResult Snapshot(FactoryTfResult source)
        {
            var xml = new XmlSerializer(typeof(FactoryTfResult));
            using (var stream = new MemoryStream())
            { xml.Serialize(stream, source); stream.Position = 0; var copy = (FactoryTfResult)xml.Deserialize(stream); copy.RawDataDirectory = source.RawDataDirectory; return copy; }
        }
        private static bool SameAnalysis(FactoryTfResult a, FactoryTfResult b)
        {
            return a.SourceId.Equals(b.SourceId, StringComparison.OrdinalIgnoreCase) && a.CapturedUtc == b.CapturedUtc && a.ChainKey == b.ChainKey &&
                a.ThresholdPc == b.ThresholdPc && a.CoefficientPcPerV == b.CoefficientPcPerV && a.FullScaleV == b.FullScaleV && a.Pulses.Count == b.Pulses.Count &&
                a.Pulses.Zip(b.Pulses, (x,y) => x.PeakSample == y.PeakSample && x.StartSample == y.StartSample && x.EndSample == y.EndSample &&
                    x.EquivalentTimeUs == y.EquivalentTimeUs && x.EquivalentBandwidthMHz == y.EquivalentBandwidthMHz && x.ChargePc == y.ChargePc && x.Valid == y.Valid).All(x => x);
        }
        internal FactoryTfAnnotationRegion Add(FactoryTfRegionGeometry geometry, IEnumerable<FactoryTfDisplayPoint> source, FactoryTfAnnotationRegion text)
        {
            geometry.Validate(); text.ValidateText();
            var points = source.ToList();
            if (points.Any(p => p == null || p.Round == null || p.Pulse == null || !p.Round.Pulses.Contains(p.Pulse))) throw new InvalidDataException("标注来源并非该轮原始TF脉冲。");
            points = points.Where(p => p.Pulse.Valid && geometry.Contains(p.Pulse.EquivalentBandwidthMHz, p.Pulse.EquivalentTimeUs)).ToList();
            if (points.Count == 0) throw new InvalidOperationException("区域内没有有效TF点，未创建空标注。");
            string chain = points[0].Round.ChainKey;
            if (points.Any(p => p.Round.ChainKey != chain) || (ChainKey.Length > 0 && ChainKey != chain)) throw new InvalidDataException("不同设备/采集条件的标注不能混在同一文件。");
            if (Regions.Count >= 200 || Regions.Sum(r => r.Members.Count) + points.Count > 50000) throw new InvalidOperationException("标注到达本文件容量限制，请先另存文件。");
            var addedRounds = new List<FactoryTfAnnotatedRound>(); var members = new List<FactoryTfAnnotationMember>();
            foreach (var group in points.GroupBy(p => p.Round))
            {
                group.Key.Validate();
                var round = Rounds.FirstOrDefault(r => SameAnalysis(r.Data, group.Key)) ?? addedRounds.FirstOrDefault(r => SameAnalysis(r.Data, group.Key));
                if (round == null) { round = new FactoryTfAnnotatedRound { Data = Snapshot(group.Key) }; addedRounds.Add(round); }
                foreach (var p in group)
                    if (!members.Any(m => m.RoundId == round.Id && m.PeakSample == p.Pulse.PeakSample)) members.Add(new FactoryTfAnnotationMember { RoundId = round.Id, PeakSample = p.Pulse.PeakSample });
            }
            if (Rounds.Count + addedRounds.Count > 100) throw new InvalidOperationException("本文件已到100个分析快照，请另存一个标注文件。");
            var region = new FactoryTfAnnotationRegion { Name = text.Name.Trim(), Kind = text.Kind, Defect = text.Defect.Trim(), PossibleInterferenceSource = text.PossibleInterferenceSource.Trim(),
                Notes = text.Notes.Trim(), Author = text.Author.Trim(), Geometry = geometry.Copy(), Members = members,
                FilterInterference = text.FilterInterference, MarkedSurface = text.MarkedSurface, Templates = text.Templates };
            ChainKey = chain; Rounds.AddRange(addedRounds); Regions.Add(region); Touch(); return region;
        }
        internal void Touch() { ModifiedUtc = DateTime.UtcNow; Dirty = true; }
        internal FactoryTfAnnotationRegion UpdateLabel(string id, FactoryTfAnnotationRegion text)
        {
            var region = Regions.FirstOrDefault(r => r.Id == id);
            if (region == null || text == null || text.Id != id || text.CreatedUtc != region.CreatedUtc)
                throw new InvalidDataException("原标签已不存在或身份发生变化，未修改标签库。");
            text.ValidateText();
            // Only human metadata changes. Frozen packet identities, geometry,
            // native templates and acquisition settings must remain unchanged.
            region.Name = text.Name.Trim(); region.Kind = text.Kind; region.Defect = text.Defect.Trim();
            region.PossibleInterferenceSource = text.PossibleInterferenceSource.Trim(); region.Author = text.Author.Trim();
            region.Notes = text.Notes.Trim(); region.FilterInterference = text.FilterInterference;
            region.ModifiedUtc = text.ModifiedUtc > region.ModifiedUtc ? text.ModifiedUtc : region.ModifiedUtc;
            Touch(); return region;
        }
        internal void Remove(FactoryTfAnnotationRegion region)
        {
            Regions.Remove(region); Rounds.RemoveAll(r => !Regions.Any(a => a.Members.Any(m => m.RoundId == r.Id))); Touch();
        }
        internal List<FactoryTfDisplayPoint> PointsFor(FactoryTfAnnotationRegion region)
        {
            var ids = new HashSet<string>(region.Members.Select(m => m.RoundId));
            return Rounds.Where(r => ids.Contains(r.Id)).SelectMany(r => r.Data.Pulses.Select(p => new FactoryTfDisplayPoint { Round = r.Data, Pulse = p })).ToList();
        }
        internal bool HasMember(FactoryTfAnnotationRegion region, FactoryTfDisplayPoint point)
        {
            if (point == null) return false;
            foreach (var member in region.Members)
            {
                if (member.PeakSample != point.Pulse.PeakSample) continue;
                var round = Rounds.FirstOrDefault(r => r.Id == member.RoundId);
                if (round == null || round.Data.CapturedUtc != point.Round.CapturedUtc || !round.Data.SourceId.Equals(point.Round.SourceId, StringComparison.OrdinalIgnoreCase) || round.Data.ChainKey != point.Round.ChainKey) continue;
                var p = round.Data.Pulses.FirstOrDefault(x => x.PeakSample == member.PeakSample);
                if (p != null && point.Pulse.Valid && p.Valid && p.EquivalentTimeUs == point.Pulse.EquivalentTimeUs && p.EquivalentBandwidthMHz == point.Pulse.EquivalentBandwidthMHz &&
                    p.ChargePc == point.Pulse.ChargePc && round.Data.CoefficientPcPerV == point.Round.CoefficientPcPerV && round.Data.ThresholdPc == point.Round.ThresholdPc) return true;
            }
            return false;
        }
        internal static string PointKey(FactoryTfDisplayPoint point)
        {
            var r = point.Round; var p = point.Pulse;
            return r.SourceId.ToUpperInvariant() + "|" + r.CapturedUtc.Ticks + "|" + r.ChainKey + "|" + p.PeakSample + "|" +
                p.EquivalentTimeUs.ToString("R", CultureInfo.InvariantCulture) + "|" + p.EquivalentBandwidthMHz.ToString("R", CultureInfo.InvariantCulture) + "|" +
                p.ChargePc.ToString("R", CultureInfo.InvariantCulture) + "|" + r.CoefficientPcPerV.ToString("R", CultureInfo.InvariantCulture) + "|" + r.ThresholdPc.ToString("R", CultureInfo.InvariantCulture);
        }
        internal Dictionary<string, List<FactoryTfAnnotationRegion>> MatchMap(IEnumerable<FactoryTfDisplayPoint> points)
        {
            var keys = new HashSet<string>(points.Where(p => p.Pulse.Valid).Select(PointKey));
            var lookup = new Dictionary<string, string>();
            foreach (var r in Rounds) foreach (var p in r.Data.Pulses.Where(p => p.Valid))
                lookup[r.Id + ":" + p.PeakSample] = PointKey(new FactoryTfDisplayPoint { Round = r.Data, Pulse = p });
            var matches = new Dictionary<string, List<FactoryTfAnnotationRegion>>();
            foreach (var region in Regions) foreach (var member in region.Members)
            {
                string key; if (!lookup.TryGetValue(member.RoundId + ":" + member.PeakSample, out key) || !keys.Contains(key)) continue;
                List<FactoryTfAnnotationRegion> list;
                if (!matches.TryGetValue(key, out list)) matches[key] = list = new List<FactoryTfAnnotationRegion>();
                if (!list.Contains(region)) list.Add(region);
            }
            return matches;
        }
        public void Validate()
        {
            if (SchemaVersion != 1 || ChainKey == null || Rounds == null || Regions == null || Rounds.Count > 100 || Regions.Count > 200 ||
                CreatedUtc.Kind != DateTimeKind.Utc || ModifiedUtc.Kind != DateTimeKind.Utc || CreatedUtc == DateTime.MinValue || ModifiedUtc < CreatedUtc || ModifiedUtc > DateTime.MaxValue.AddHours(-8))
                throw new InvalidDataException("TF标注文件版本、时间或容量无效。");
            var ids = new HashSet<string>();
            foreach (var r in Rounds)
            {
                Guid id;
                if (r == null || !Guid.TryParseExact(r.Id, "N", out id) || !ids.Add(r.Id) || r.Data == null) throw new InvalidDataException("TF分析快照重复或缺失。");
                r.Data.Validate(); if (r.Data.ChainKey != ChainKey) throw new InvalidDataException("TF标注文件混入其他设备/采集条件。");
                if (r.Data.Pulses.Select(p => p.PeakSample).Distinct().Count() != r.Data.Pulses.Count) throw new InvalidDataException("TF脉冲采样身份重复。");
            }
            ids.Clear(); int total = 0, nativeSamples = 0;
            foreach (var r in Regions)
            {
                if (r == null || !ids.Add(r.Id) || r.Geometry == null || r.Members == null || r.Members.Count == 0 || r.Members.Count > 10000) throw new InvalidDataException("圈选区域重复、为空或超过限制。");
                r.ValidateText(); r.Geometry.Validate(); total += r.Members.Count;
                if (r.Templates == null || r.Templates.Count > r.Members.Count) throw new InvalidDataException("模板数量与标注脉冲不符。");
                foreach (var template in r.Templates)
                {
                    if (template == null) throw new InvalidDataException("空模板。"); template.Validate(); nativeSamples += template.Wave.Length;
                    if (nativeSamples > 500000) throw new InvalidDataException("本库原始模板超过50万点，请另存标签库。");
                    if (!r.Members.Any(m => m.PeakSample == template.PeakSample && Rounds.Any(a => a.Id == m.RoundId && a.Data.SourceId == template.SourceId && a.Data.PicoRateHz == template.RateHz && a.Data.Pulses.Any(p => p.PeakSample == template.PeakSample && p.EquivalentTimeUs == template.TimeUs && p.EquivalentBandwidthMHz == template.FrequencyMHz))))
                        throw new InvalidDataException("识别模板未关联到标注脉冲。");
                }
                if (total > 50000) throw new InvalidDataException("TF标注关联脉冲数量超过限制。");
                var members = new HashSet<string>();
                foreach (var member in r.Members)
                {
                    if (member == null || !members.Add(member.RoundId + ":" + member.PeakSample)) throw new InvalidDataException("圈选脉冲身份重复或缺失。");
                    var round = Rounds.FirstOrDefault(x => x.Id == member.RoundId);
                    var pulse = round == null ? null : round.Data.Pulses.FirstOrDefault(p => p.PeakSample == member.PeakSample);
                    if (pulse == null || !pulse.Valid || !r.Geometry.Contains(pulse.EquivalentBandwidthMHz, pulse.EquivalentTimeUs)) throw new InvalidDataException("标注脉冲不属于该区域或质量无效。");
                }
            }
        }
        public void Save(string path)
        {
            Validate(); string full = Path.GetFullPath(path), pending = full + ".pending-" + Guid.NewGuid().ToString("N");
            // User-confirmed overwrites retain a uniquely named recoverable copy.
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) new XmlSerializer(typeof(FactoryTfAnnotationDocument)).Serialize(stream, this);
            Load(pending);
            if (File.Exists(full)) File.Replace(pending, full, full + ".bak-" + DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd_HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            else File.Move(pending, full);
            FilePath = full; Dirty = false;
        }
        public static FactoryTfAnnotationDocument Load(string path)
        {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32000000 }))
            {
                var doc = (FactoryTfAnnotationDocument)new XmlSerializer(typeof(FactoryTfAnnotationDocument)).Deserialize(reader); doc.Validate(); doc.FilePath = Path.GetFullPath(path);
                foreach (var r in doc.Rounds) r.Data.RawDataDirectory = Path.GetDirectoryName(doc.FilePath);
                return doc;
            }
        }
    }
    internal sealed class FactoryTfRegionEvent : EventArgs
    { internal FactoryTfRegionGeometry Geometry; internal List<FactoryTfDisplayPoint> Points; }
    internal sealed class FactoryTfLabelEditEvent : EventArgs
    { internal FactoryTfAnnotationRegion Region; }
}
