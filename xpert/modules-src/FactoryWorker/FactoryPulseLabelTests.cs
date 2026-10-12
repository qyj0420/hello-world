using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPulseLabelTests
    {
        private static int count;
        private static void Check(bool ok, string name) { count++; if (!ok) throw new Exception("标签/聚类回归：" + name); }
        private static void Throws(Action action, string name) { bool thrown = false; try { action(); } catch { thrown = true; } Check(thrown, name); }
        private static FactoryPulseTemplate Copy(FactoryPulseTemplate source)
        { return new FactoryPulseTemplate { SourceId = source.SourceId, PeakSample = source.PeakSample, PeakOffset = source.PeakOffset, RateHz = source.RateHz, TimeUs = source.TimeUs, FrequencyMHz = source.FrequencyMHz, Wave = (double[])source.Wave.Clone(), Envelope = (double[])source.Envelope.Clone() }; }
        internal static FactoryTfDisplayPoint Point(string source, int offset = 1000)
        {
            var raw = new double[128]; for (int i = 0; i < raw.Length; i++) raw[i] = .1 * Math.Exp(-Math.Pow((i - 64) / 10.0, 2)) * Math.Cos((i - 64) * .25);
            var pulse = new FactoryTfPulse { StartSample = offset, EndSample = offset + raw.Length - 1, PeakSample = offset + 64, TimeMs = (offset + 64) / 1000.0,
                SamplesV = raw, EquivalentTimeUs = 1, EquivalentBandwidthMHz = .1, SnrDb = 35, Valid = true, PeakV = .1, ChargePc = 210 };
            var round = new FactoryTfResult { SourceId = Path.GetFullPath(source), SourceDirectory = Path.GetFullPath(source), CapturedUtc = DateTime.UtcNow, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO", Simulation = true,
                PicoRateHz = 1000000, CaptureDurationMs = 10, FullScaleV = 1, CoefficientPcPerV = 2100, ThresholdPc = 10, HardwareBits = 14, ResolutionBits = 14, CandidateCount = 1,
                ChainKey = FactoryTfAnalysis.CreateChainKey(true, "SIM-ART", "SIM-PICO", 1000000, 14, 1, false) };
            round.Pulses.Add(pulse); round.Validate(); return new FactoryTfDisplayPoint { Round = round, Pulse = pulse };
        }
        private static FactoryTfAnnotationDocument Document(FactoryTfDisplayPoint point, string label = "试验干扰", bool filter = false)
        {
            var document = new FactoryTfAnnotationDocument();
            document.Add(FactoryMainForm.BoundsFor(new List<FactoryTfDisplayPoint> { point }), new[] { point }, new FactoryTfAnnotationRegion { Name = label, Kind = FactoryTfLabelKind.SuspectedInterference,
                PossibleInterferenceSource = "手动记录", FilterInterference = filter, Templates = new List<FactoryPulseTemplate> { FactoryPulseLabels.Create(point, point.Pulse.SamplesV) } });
            document.Validate(); return document;
        }
        internal static string Run()
        {
            count = 0; string root = Path.Combine(Path.GetTempPath(), "Xiongxin-LabelTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var p = Point(Path.Combine(root, "raw")); var rawCopy = (double[])p.Pulse.SamplesV.Clone(); var doc = Document(p); var template = FactoryPulseLabels.Create(p, p.Pulse.SamplesV);
            Check(p.Pulse.SamplesV.SequenceEqual(rawCopy), "Hilbert模板不改写原始波形");
            Check(FactoryPulseLabels.Match(doc, p, template).Matched, "同样本四判据匹配");
            doc.Regions[0].Kind = FactoryTfLabelKind.Undetermined;
            Check(FactoryPulseLabels.Match(doc, p, template).Matched && !FactoryPulseLabels.Match(doc, p, template).Filter, "未分类的人工标签仍识别、不可过滤"); doc.Regions[0].Kind = FactoryTfLabelKind.SuspectedInterference;
            Check(!FactoryPulseLabels.Match(doc, p, template).Filter, "仅标注不默认授权过滤");
            var candidate = Copy(template); candidate.TimeUs *= 1.01;
            Check(FactoryPulseLabels.Match(doc, p, candidate).Matched, "时长1%含边界"); candidate.TimeUs = template.TimeUs * 1.010001;
            Check(!FactoryPulseLabels.Match(doc, p, candidate).Matched, "时长超1%拒绝");
            candidate = Copy(template); candidate.FrequencyMHz *= .99; Check(FactoryPulseLabels.Match(doc, p, candidate).Matched, "频宽负1%含边界");
            candidate.FrequencyMHz = template.FrequencyMHz * .989999; Check(!FactoryPulseLabels.Match(doc, p, candidate).Matched, "频宽超1%拒绝");
            candidate = Copy(template); candidate.Wave[20] += .01; Check(FactoryPulseLabels.Match(doc, p, candidate).Matched, "原波1%含边界");
            candidate.Wave[20] += .00001; Check(!FactoryPulseLabels.Match(doc, p, candidate).Matched, "原波超1%拒绝");
            candidate = Copy(template); candidate.Envelope[20] += .01; Check(FactoryPulseLabels.Match(doc, p, candidate).Matched, "包络1%含边界");
            candidate.Envelope[20] += .00001; Check(!FactoryPulseLabels.Match(doc, p, candidate).Matched, "包络超1%拒绝");
            candidate = Copy(template); candidate.Wave = candidate.Wave.Select(v => -v).ToArray(); Check(!FactoryPulseLabels.Match(doc, p, candidate).Matched, "反极性不因包络相似误识别");
            candidate = Copy(template); candidate.PeakOffset += 1; Check(!FactoryPulseLabels.Match(doc, p, candidate).Matched, "不扭曲时间窗强行匹配");
            var foreign = Point(Path.Combine(root, "other")); foreign.Round.PicoSerial = "OTHER"; foreign.Round.ChainKey = FactoryTfAnalysis.CreateChainKey(true, "SIM-ART", "OTHER", 1000000, 14, 1, false);
            Check(!FactoryPulseLabels.Match(doc, foreign, template).Matched, "其他设备标签不混用");
            p.Pulse.Clipped = true; Check(!FactoryPulseLabels.Match(doc, p, template).Matched, "削顶不匹配"); p.Pulse.Clipped = false;
            p.Round.DriverOverflow = true; Check(!FactoryPulseLabels.Match(doc, p, template).Matched, "驱动溢出不匹配"); p.Round.DriverOverflow = false;
            doc.Regions[0].FilterInterference = true; Check(FactoryPulseLabels.Match(doc, p, template).Filter, "显式干扰授权才过滤");
            var conflict = Document(p, "其他缺陷"); conflict.Regions[0].Kind = FactoryTfLabelKind.SuspectedDefect;
            doc.Add(FactoryMainForm.BoundsFor(new List<FactoryTfDisplayPoint> { p }), new[] { p }, conflict.Regions[0]);
            var match = FactoryPulseLabels.Match(doc, p, template); Check(match.Conflict && !match.Filter && !match.Matched, "冲突标签不投票、不自动过滤");
            Check(!FactoryPulseLabels.Selection(new[] { match, new FactoryPulseMatch() }).Matched, "混合未识别选区不按多数识别");
            doc.Remove(doc.Regions[1]); doc.Validate();
            Check(FactoryPulseLabels.EditableLabels(doc, new[] { p }, point => match).Count == 1, "冲突历史引用只解析仍存在的库内标签");
            var unknown = Point(Path.Combine(root, "unmatched")); unknown.Pulse.EquivalentBandwidthMHz *= 2;
            Check(FactoryPulseLabels.EditableLabels(doc, new[] { unknown }, point => new FactoryPulseMatch()).Count == 0, "未标记未匹配脉冲不提供伪编辑项");
            Check(FactoryPulseLabels.EditableLabels(doc, new[] { foreign }, point => match).Count == 0, "其他设备禁止修改本库标签");
            FactoryLabelStore.Save(root, doc); var file = doc.FilePath;
            var loaded = FactoryLabelStore.Load(root, message => { throw new Exception(message); });
            Check(loaded.Count == 1 && loaded[doc.ChainKey].Regions[0].Templates.Count == 1, "启动读取模板及人工标签");
            Check(FactoryPulseLabels.Match(loaded[doc.ChainKey], p, template).Matched, "重启后不依赖原始目录即可匹配新脉冲");
            doc.Regions[0].Notes = "更新"; doc.Touch(); FactoryLabelStore.Save(root, doc);
            Check(Directory.GetFiles(root, "*.bak-*").Length == 1 && File.Exists(file), "自动保存原子替换保留旧库备份");
            Check(FactoryTfAnnotationDocument.Load(file).Regions[0].Notes == "更新", "自动保存内容可复读");
            var editDoc = FactoryTfAnnotationDocument.Clone(doc); var beforeEdit = editDoc.Regions[0];
            var editText = FactoryTfAnnotationDocument.Clone(editDoc).Regions[0];
            var preservedGeometry = beforeEdit.Geometry; var preservedMembers = beforeEdit.Members;
            var preservedTemplates = beforeEdit.Templates; var originalWave = (double[])preservedTemplates[0].Wave.Clone();
            var originalEnvelope = (double[])preservedTemplates[0].Envelope.Clone(); var originalId = beforeEdit.Id; var originalCreated = beforeEdit.CreatedUtc;
            editText.Name = "改后标签"; editText.Kind = FactoryTfLabelKind.SuspectedDefect; editText.FilterInterference = false;
            editText.Defect = "人工修改现象"; editText.PossibleInterferenceSource = "待核实"; editText.Author = "测试"; editText.Notes = "原标签编辑"; editText.ModifiedUtc = DateTime.UtcNow;
            editDoc.UpdateLabel(originalId, editText); editDoc.Validate();
            Check(editDoc.Regions.Count == 1 && beforeEdit.Id == originalId && beforeEdit.CreatedUtc == originalCreated, "修改原标签不新增区域或变更身份");
            Check(beforeEdit.Name == "改后标签" && beforeEdit.Defect == "人工修改现象" && beforeEdit.Author == "测试" && !beforeEdit.FilterInterference, "标签字段及过滤授权准确更新");
            Check(Object.ReferenceEquals(preservedGeometry, beforeEdit.Geometry) && Object.ReferenceEquals(preservedMembers, beforeEdit.Members) && Object.ReferenceEquals(preservedTemplates, beforeEdit.Templates), "编辑保留坐标原始成员及模板对象");
            Check(originalWave.SequenceEqual(beforeEdit.Templates[0].Wave) && originalEnvelope.SequenceEqual(beforeEdit.Templates[0].Envelope), "编辑不改变原波及包络模板");
            var future = Point(Path.Combine(root, "future")); var futureMatch = FactoryPulseLabels.Match(editDoc, future, FactoryPulseLabels.Create(future, future.Pulse.SamplesV));
            Check(futureMatch.Matched && futureMatch.Label.Name == "改后标签" && !futureMatch.Filter, "新脉冲立即识别修改后的标签");
            Check(FactoryPulseLabels.EditableLabels(editDoc, new[] { future }, point => futureMatch).Single().Id == originalId, "自动识别的新脉冲可修改对应旧模板标签");
            var editFolder = Path.Combine(root, "edit-library"); FactoryLabelStore.Save(editFolder, editDoc);
            Check(FactoryLabelStore.Load(editFolder, message => { throw new Exception(message); })[editDoc.ChainKey].Regions.Single().Name == "改后标签", "修改后自动保存并重启读取");
            var badEdit = FactoryTfAnnotationDocument.Clone(editDoc).Regions[0]; badEdit.Name = "";
            Throws(() => editDoc.UpdateLabel(originalId, badEdit), "非法编辑在修改前拒绝");
            Check(editDoc.Regions[0].Name == "改后标签", "非法编辑保留原标签");
            badEdit.Name = "其他标签"; badEdit.Id = Guid.NewGuid().ToString("N");
            Throws(() => editDoc.UpdateLabel(originalId, badEdit), "不允许编辑替换标签身份");
            Check(doc.Regions[0].Name != "改后标签", "编辑冻结副本不修改旧库");
            var geometry = new Rectangle(10, 20, 200, 100);
            var rect = FactoryTfRegionGeometry.FromDrag(geometry, new Point(30, 30), new Point(130, 90), false, 2, 4);
            Check(rect.Shape == FactoryTfRegionShape.Rectangle && rect.Contains(.7, 2), "矩形坐标映射");
            var circle = FactoryTfRegionGeometry.FromDrag(geometry, new Point(30, 30), new Point(81, 80), true, 2, 4);
            Check(circle.Shape == FactoryTfRegionShape.Circle, "近方形拖拽为正圆");
            var ellipse = FactoryTfRegionGeometry.FromDrag(geometry, new Point(30, 30), new Point(130, 80), true, 2, 4);
            Check(ellipse.Shape == FactoryTfRegionShape.Ellipse && !ellipse.Contains(ellipse.MinimumFrequencyMHz, ellipse.MinimumTimeUs), "长宽不同为椭圆且角点不误选");
            Check(FactoryTfRegionGeometry.FromDrag(geometry, new Point(30, 30), new Point(32, 32), true, 2, 4) == null, "误点不生成选区");
            Throws(() => Document(p).Add(rect, new[] { new FactoryTfDisplayPoint { Round = p.Round, Pulse = Point(root).Pulse } }, new FactoryTfAnnotationRegion { Name = "伪造" }), "伪造脉冲关联被阻止");
            var clone = FactoryTfAnnotationDocument.Clone(doc); clone.Regions[0].Templates[0].Wave[20] += .1;
            Check(doc.Regions[0].Templates[0].Wave[20] != clone.Regions[0].Templates[0].Wave[20], "冻结模板无数组别名");
            doc.Regions[0].Templates[0].Wave[0] = Double.NaN; Throws(() => doc.Save(file), "坏模板不覆盖旧库");
            Check(FactoryTfAnnotationDocument.Load(file).Regions.Count == 1, "验证失败旧库仍有效");
            var points = new List<FactoryTfDisplayPoint>();
            for (int i = 0; i < 60; i++)
            { var value = Point(Path.Combine(root, "cluster-" + i), 1000 + i); value.Pulse.EquivalentTimeUs = (i < 30 ? 1 : 8) * (1 + .025 * Math.Sin(i)); value.Pulse.EquivalentBandwidthMHz = (i < 30 ? .1 : 3) * (1 + .02 * Math.Cos(i)); points.Add(value); }
            var clusters = FactoryTfClustering.Analyze(points);
            Check(clusters.Groups.Count == points.Count && clusters.Members.Where(pair => pair.Key >= 0).Count() == 2, "原任务27自动识别双簇");
            Check(clusters.Members.Values.Sum(v => v.Count) == points.Count, "自动聚类保留每个原始身份");
            var second = FactoryTfClustering.Analyze(points); Check(second.Groups.OrderBy(v => v.Key).SequenceEqual(clusters.Groups.OrderBy(v => v.Key)), "聚类确定性复算");
            Check(FactoryTfClustering.Analyze(new List<FactoryTfDisplayPoint>()).Groups.Count == 0, "空簇不造点");
            p.Pulse.Valid = false; Check(FactoryTfClustering.Analyze(new List<FactoryTfDisplayPoint> { p }).Groups.Count == 0, "质量无效不混入聚类"); p.Pulse.Valid = true;
            var calibration = FactoryCompatibility100Tests.CalibrationCapture(3);
            var settings = new FactorySettings { Input = calibration.Settings };
            var result = new FactoryResult { Settings = settings, Capture = calibration, Kind = FactoryRunKind.Calibration, Completed = true, ResidualSafe = true, DirectoryPath = Path.Combine(root, "calibration") };
            var originalAdc = (short[])calibration.PdAdc.Clone(); var originalVoltage = (double[])calibration.VoltageRaw.Clone();
            var rawTf = FactoryTfAnalysis.ExtractForCalibrationLabels(result, FactoryTfAnalysis.Estimate(calibration).ThresholdPc);
            Check(rawTf.Pulses.Count >= 8 && rawTf.Pulses.All(value => value.Valid), "校准原生候选可建立模板：" + rawTf.CandidateCount + "/" + rawTf.Pulses.Count + "; " + String.Join(";", rawTf.Pulses.Select(value => value.Detail + ":" + value.SnrDb)));
            var calibrationPoints = rawTf.Pulses.Select(value => new FactoryTfDisplayPoint { Round = rawTf, Pulse = value }).ToList();
            var noiseDoc = new FactoryTfAnnotationDocument();
            noiseDoc.Add(FactoryMainForm.BoundsFor(calibrationPoints), calibrationPoints, new FactoryTfAnnotationRegion { Name = "已确认干扰", Kind = FactoryTfLabelKind.SuspectedInterference, FilterInterference = true,
                Templates = calibrationPoints.Select(value => FactoryPulseLabels.Create(value, value.Pulse.SamplesV)).ToList() }); noiseDoc.Validate();
            Check(FactoryCalibrationAdapter.Calibrate(calibration, 100, 100, 8).Valid, "无标签时保留原100质量流程");
            Check(!FactoryCalibrationAdapter.Calibrate(calibration, 100, 100, 8, noiseDoc, rawTf).Valid, "全部命中干扰后不得伪造校准系数");
            noiseDoc.Regions[0].FilterInterference = false;
            Check(FactoryCalibrationAdapter.Calibrate(calibration, 100, 100, 8, noiseDoc, rawTf).Valid, "未授权过滤的标签不改变校准");
            noiseDoc.Regions[0].FilterInterference = true;
            var matches = new Dictionary<string, FactoryPulseMatch>();
            foreach (var value in calibrationPoints) matches[FactoryTfAnnotationDocument.PointKey(value)] = FactoryPulseLabels.Match(noiseDoc, value, FactoryPulseLabels.Create(value, value.Pulse.SamplesV));
            var detected = FactoryDetectedWaveforms.Build(calibration);
            var filtered = FactoryPulseFilter.Build(calibration, rawTf, matches, detected);
            Check(filtered.FilteredCount == rawTf.Pulses.Count && filtered.PdPc.Max() < detected.PdEnvelopePc.Max(), "过滤只生成派生检波产品");
            Check(originalAdc.SequenceEqual(calibration.PdAdc) && originalVoltage.SequenceEqual(calibration.VoltageRaw), "干扰过滤不改变原始ADC或电压反馈");
            Check(calibration.Settings.PdCalibrationPcPerV == 2100 && calibration.Settings.RangeIndex == 4, "过滤不改变原系数及量程保护");
            foreach (var value in matches.Values) value.Filter = false;
            var unfiltered = FactoryPulseFilter.Build(calibration, rawTf, matches, detected);
            Check(unfiltered.FilteredCount == 0 && unfiltered.PdPc.SequenceEqual(detected.PdEnvelopePc), "撤销过滤恢复完整检波而非残留旧视图");
            Check(!Object.ReferenceEquals(unfiltered.PdPc, detected.PdEnvelopePc), "派生数组独立");
            Throws(() => FactoryTfAnalysis.Extract(result, 10), "校准背景不混入加压试验TF接口");
            return "PulseLabels: " + count + " checks PASS; hardware accessed=False; TF + native + Hilbert tolerance <=1%; task27 exact engine";
        }
    }
}
