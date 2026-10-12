using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private readonly Dictionary<string, FactoryTfAnnotationDocument> tfAnnotationFiles = new Dictionary<string, FactoryTfAnnotationDocument>();
        private readonly List<Control> annotationActions = new List<Control>();
        private FactoryTfAnnotationDocument lastAnnotationDocument;
        private string labelRoot;
        private Dictionary<string, FactoryPulseMatch> pulseMatches = new Dictionary<string, FactoryPulseMatch>();
        private FactoryClusterResult pulseClusters = new FactoryClusterResult();
        private Task labelTask = Task.FromResult(0);
        private bool labelProcessing;
        private string lastRecognitionSignature = "";
        private void InitializePulseLabels(string testRoot)
        {
            labelRoot = testRoot ?? FactoryLabelStore.DefaultRoot;
            var errors = new List<string>();
            foreach (var pair in FactoryLabelStore.Load(labelRoot, errors.Add)) tfAnnotationFiles.Add(pair.Key, pair.Value);
            Progress("PULSE_LABEL_LIBRARY", "自动读取 " + tfAnnotationFiles.Values.Sum(d => d.Regions.Count) + " 个标签；默认目录：" + labelRoot +
                (errors.Count == 0 ? "；T/F/原波/包络均≤1%。" : "；" + String.Join("；", errors)));
        }
        private FactoryPulseMatch MatchFor(FactoryTfDisplayPoint point)
        { FactoryPulseMatch match; return point != null && pulseMatches.TryGetValue(FactoryTfAnnotationDocument.PointKey(point), out match) ? match : new FactoryPulseMatch(); }
        internal static FactoryTfRegionGeometry BoundsFor(List<FactoryTfDisplayPoint> points)
        {
            if (points.Count == 0) throw new ArgumentException("没有脉冲。");
            double f = points.Min(p => p.Pulse.EquivalentBandwidthMHz), F = points.Max(p => p.Pulse.EquivalentBandwidthMHz),
                t = points.Min(p => p.Pulse.EquivalentTimeUs), T = points.Max(p => p.Pulse.EquivalentTimeUs);
            double df = Math.Max(F * .015, .000001), dt = Math.Max(T * .015, .000001);
            return new FactoryTfRegionGeometry { Shape = FactoryTfRegionShape.Rectangle, MinimumFrequencyMHz = Math.Max(0, f - df), MaximumFrequencyMHz = F + df, MinimumTimeUs = Math.Max(0, t - dt), MaximumTimeUs = T + dt };
        }
        private void RecordSelectedPulse(string surface)
        { var point = pulseSelection.Current; if (point != null) RecordTfRegion(this, BoundsFor(new List<FactoryTfDisplayPoint> { point }), new List<FactoryTfDisplayPoint> { point }, null, surface); }
        private List<FactoryTfAnnotationRegion> SelectedPulseLabels()
        { return FactoryPulseLabels.EditableLabels(ActiveAnnotationDocument(false), new[] { pulseSelection.Current }, MatchFor); }
        private void EditTfLabel(IWin32Window owner, FactoryTfAnnotationRegion requested)
        {
            if (busy || scanning || exporting || labelProcessing || requested == null) return;
            var doc = ActiveAnnotationDocument(false);
            var original = doc == null ? null : doc.Regions.FirstOrDefault(r => r.Id == requested.Id);
            if (original == null) { status.Text = "该标签已不在当前标签库，请刷新视图后重试。"; return; }
            var staged = FactoryTfAnnotationDocument.Clone(doc);
            var initial = staged.Regions.First(r => r.Id == original.Id); FactoryTfAnnotationRegion text;
            if (smokeMode) { text = initial; text.Notes += "\r\n右键编辑验证"; text.ModifiedUtc = DateTime.UtcNow; }
            else using (var dialog = new FactoryTfAnnotationEditDialog(Icon, original.Geometry, original.Members.Count,
                initial, "库内已有人工标签：“" + original.Name + "”", true))
            { if (dialog.ShowDialog(owner) != DialogResult.OK) return; text = dialog.Value; }
            try
            {
                staged.UpdateLabel(original.Id, text); staged.Validate(); FactoryLabelStore.Save(labelRoot, staged);
                tfAnnotationFiles[staged.ChainKey] = staged; lastAnnotationDocument = staged;
                pulseMatches.Clear(); lastRecognitionSignature = ""; RefreshTfAnnotations(); SchedulePulseRecognition();
                status.Text = "已有标签已更新并自动保存：“" + text.Name + "”；未新增标签，原波/包络模板及原始数据不变。";
            }
            catch (Exception ex)
            { status.Text = "标签修改未保存，旧库保留：" + ex.Message; if (!smokeMode) MessageBox.Show(owner, status.Text, "修改标签", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void SchedulePulseRecognition()
        {
            if (recognitionSuspended || labelProcessing || String.IsNullOrEmpty(labelRoot)) return;
            var points = TfPoints(); string signature = String.Join("\n", points.Select(FactoryTfAnnotationDocument.PointKey));
            if (signature == lastRecognitionSignature) return;
            lastRecognitionSignature = signature; labelTask = RecognizeView(points, signature);
        }
        private async Task RecognizeView(List<FactoryTfDisplayPoint> points, string signature)
        {
            labelProcessing = true; UpdatePostProcessingButtons(); start.Enabled = calibrationStart.Enabled = false;stop.Enabled=true;
            var cancel=new CancellationTokenSource();recognitionCancellation=cancel;var token=cancel.Token;
            var result = latest; var tf = currentTf; var docs = tfAnnotationFiles.ToDictionary(p => p.Key, p => FactoryTfAnnotationDocument.Clone(p.Value));
            try
            {
                var computed = await Task.Run(() =>
                {
                    var matches = new Dictionary<string, FactoryPulseMatch>(); FactoryPulseRawCache cache = null;
                    foreach (var point in points)
                    {
                        token.ThrowIfCancellationRequested();
                        var match = new FactoryPulseMatch(); FactoryTfAnnotationDocument doc;
                        if (point.Pulse.Valid && docs.TryGetValue(point.Round.ChainKey, out doc) && doc.Regions.Any(r => r.Templates.Count > 0))
                        {
                            try { match = FactoryPulseLabels.Match(doc, point, FactoryPulseLabels.Create(point, FactoryTfPulseReader.Read(point, result, ref cache))); }
                            catch (Exception ex) { match.Text = "未识别（原始波形/模板不可用：" + ex.Message + "）"; }
                        }
                        matches[FactoryTfAnnotationDocument.PointKey(point)] = match;
                    }
                    token.ThrowIfCancellationRequested();
                    var report = result != null && tf != null && result.Kind == FactoryRunKind.Pressure ? FactoryPulseFilter.Build(result.Capture, tf, matches, result.Metrics.Detected) : null;
                    token.ThrowIfCancellationRequested();
                    var clusters=FactoryTfClustering.Analyze(points);token.ThrowIfCancellationRequested();
                    return Tuple.Create(matches, clusters, report);
                },token);
                token.ThrowIfCancellationRequested();
                if (IsDisposed || signature != String.Join("\n", TfPoints().Select(FactoryTfAnnotationDocument.PointKey))) return;
                pulseMatches = computed.Item1; pulseClusters = computed.Item2;
                tfPlot.SetRecognition(pulseMatches, pulseClusters); foreach (var plot in expandedTfPlots) plot.SetRecognition(pulseMatches, pulseClusters);
                DisplaySelectedPulse(); tfSummary.Text = tfSummary.Text.Split(new[] { "；任务27自动聚类：" }, StringSplitOptions.None)[0] + "；" + pulseClusters.Detail + "；已有标签匹配 " + pulseMatches.Values.Count(m => m.Matched) + "。";
                if (result != null && result == latest && currentTf != null && result.Kind == FactoryRunKind.Pressure)
                {
                    var report = computed.Item3;
                    combined.SetCombined(result.Metrics.Detected.VoltageKv, report.PdPc, result.Metrics.Detected.OutputRateHz, result.Metrics.Detected.DurationSeconds * 1000);
                    combined.Title = (result.Settings.Simulation ? "仿真 · " : "实测 · ") + (report.FilteredCount > 0 ? "检波（模板过滤派生视图）" : "检波");
                    if (!smokeMode && Directory.Exists(result.DirectoryPath)) FactoryDataArchive.SaveXml(report, Path.Combine(result.DirectoryPath, "pulse-recognition-" + Guid.NewGuid().ToString("N") + ".xml"));
                }
            }
            catch(OperationCanceledException) {lastRecognitionSignature="";status.Text="自动分析已停止；原始数据保留。";}
            catch (Exception ex) { status.Text = "自动识别/聚类未完成，原始数据保留：" + ex.Message; }
            finally
            {
                if(ReferenceEquals(recognitionCancellation,cancel))recognitionCancellation=null;cancel.Dispose();
                labelProcessing = false;
                if (!IsDisposed) { SetBusy(busy || scanning || exporting); SchedulePulseRecognition();if(closeAfterStop && !busy && !scanning && !exporting)Close(); }
            }
        }
        private FactoryTfAnnotationDocument ActiveAnnotationDocument(bool create)
        {
            var first = TfPoints().FirstOrDefault();
            if (first == null) return lastAnnotationDocument;
            FactoryTfAnnotationDocument doc;
            if (!tfAnnotationFiles.TryGetValue(first.Round.ChainKey, out doc) && create)
                tfAnnotationFiles[first.Round.ChainKey] = doc = new FactoryTfAnnotationDocument { ChainKey = first.Round.ChainKey };
            if (doc != null) lastAnnotationDocument = doc;
            return doc;
        }
        private Control BuildAnnotationToolbar(FactoryTfPlot plot)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = Padding.Empty, Margin = Padding.Empty };
            var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 88, Font = new Font(Font.FontFamily, 8.5f), Margin = new Padding(2, 1, 3, 0) };
            choice.Items.AddRange(new object[] { "浏览", "矩形圈选", "圆 / 椭圆" }); choice.SelectedIndex = 0;
            choice.SelectedIndexChanged += (s,e) => plot.DrawingMode = (FactoryTfDrawMode)choice.SelectedIndex;
            Button manage = Button("标注", 46), save = Button("另存", 46), load = Button("读取", 46), folder = Button("标签目录", 68);
            foreach (var button in new[] { manage, save, load, folder }) { button.Height = 24; button.Margin = new Padding(2, 0, 2, 0); button.Font = new Font(Font.FontFamily, 8.5f); }
            row.Controls.Add(new Label { Text = "TF", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 4, 1, 0) });
            row.Controls.Add(choice); row.Controls.Add(manage); row.Controls.Add(save); row.Controls.Add(load); row.Controls.Add(folder);
            pulseTip.SetToolTip(choice, "选择矩形或圆/椭圆后拖拽圈选；近方形自动成圆。先显示已有标签匹配，右键添加标签，Esc取消。浏览模式双击TF点查看脉冲。");
            pulseTip.SetToolTip(manage, "编辑、查看人工标签，管理可能的干扰源。");
            pulseTip.SetToolTip(save, "保存当前人工标注文件，含关联TF特征；不会修改原始数据。");
            pulseTip.SetToolTip(load, "读取历史TF标注，独立浏览历史数据；可作为本轮参考，但需重新确认。");
            annotationActions.Add(choice); annotationActions.Add(manage); annotationActions.Add(save); annotationActions.Add(load); annotationActions.Add(folder);
            row.Disposed += (s,e) => { foreach (var c in new Control[] { choice, manage, save, load, folder }) annotationActions.Remove(c); };
            plot.RegionSelected += (s,e) => { RecordTfRegion(this, e.Geometry, e.Points, null); choice.SelectedIndex = 0; };
            plot.LabelEditRequested += (s,e) => EditTfLabel(this, e.Region);
            manage.Click += (s,e) => OpenAnnotationWindow(ActiveAnnotationDocument(false));
            save.Click += (s,e) =>
            {
                if (busy || scanning || exporting) return; var doc = ActiveAnnotationDocument(false);
                if (doc == null || doc.Regions.Count == 0) { status.Text = "先圈选有效TF点并记录人工标签，再保存标注文件。"; return; }
                FactoryTfAnnotationWindow.SaveFile(this, doc, ShowOptionalNotice);
            };
            load.Click += (s,e) => ReadTfAnnotations();
            folder.Click += (s,e) => { if (busy || scanning || exporting || labelProcessing || smokeMode) return; try { Directory.CreateDirectory(labelRoot); System.Diagnostics.Process.Start("explorer.exe", labelRoot); } catch (Exception ex) { status.Text = ex.Message; } };
            return row;
        }
        private void RefreshTfAnnotations()
        {
            var doc = ActiveAnnotationDocument(false); tfPlot.SetAnnotations(doc);
            foreach (var plot in expandedTfPlots) plot.SetAnnotations(doc);
        }
        private void RecordTfRegion(IWin32Window owner, FactoryTfRegionGeometry geometry, List<FactoryTfDisplayPoint> points, FactoryTfAnnotationRegion reference, string surface = "TF选区/簇")
        {
            if (busy || scanning || exporting || labelProcessing) return;
            var active = TfPoints();
            // An expanded snapshot cannot silently tag another or a new analysis.
            var keys = new HashSet<string>(active.Select(FactoryTfAnnotationDocument.PointKey));
            if (points.Any(p => !keys.Contains(FactoryTfAnnotationDocument.PointKey(p)))) { status.Text = "圈选来自旧TF快照；请在当前或累计视图重新选择。"; return; }
            if (points.Count == 0) { status.Text = "区域内没有有效TF点，未创建空标注。"; return; }
            var doc = ActiveAnnotationDocument(true);
            var recognition = FactoryPulseLabels.Selection(points.Select(MatchFor));
            var initial = reference == null ? new FactoryTfAnnotationRegion { Name = "区域 " + (doc.Regions.Count + 1) } :
                new FactoryTfAnnotationRegion { Name = reference.Name, Kind = reference.Kind, Defect = reference.Defect, PossibleInterferenceSource = reference.PossibleInterferenceSource,
                    Notes = "参考历史区域，需根据本轮实际情况核实。\r\n" + reference.Notes.Substring(0, Math.Min(reference.Notes.Length, 3900)), Author = reference.Author };
            initial.MarkedSurface = surface;
            if (recognition.Matched) { initial.Name = recognition.Label.Name; initial.Kind = recognition.Label.Kind; initial.Defect = recognition.Label.Defect; initial.PossibleInterferenceSource = recognition.Label.PossibleInterferenceSource; }
            FactoryTfAnnotationRegion text;
            if (smokeMode) { text = initial; text.Name = "仿真标签"; text.Kind = FactoryTfLabelKind.SuspectedInterference; text.PossibleInterferenceSource = "合成脉冲（UI测试）"; }
            else using (var dialog = new FactoryTfAnnotationEditDialog(Icon, geometry, points.Count, initial, recognition.Text))
            { if (dialog.ShowDialog(owner) != DialogResult.OK) return; text = dialog.Value; }
            try
            {
                FactoryPulseRawCache cache = null;
                text.Templates = points.Select(point => FactoryPulseLabels.Create(point, FactoryTfPulseReader.Read(point, latest, ref cache))).ToList();
                var staged = FactoryTfAnnotationDocument.Clone(doc); staged.Add(geometry, points, text); staged.Validate();
                FactoryLabelStore.Save(labelRoot, staged); tfAnnotationFiles[staged.ChainKey] = staged; lastAnnotationDocument = staged;
                pulseMatches.Clear(); lastRecognitionSignature = ""; RefreshTfAnnotations(); SchedulePulseRecognition();
                status.Text = "标签已自动保存：" + staged.FilePath + "；启动自动读取，按四项≤1%匹配；原始ADC与保护不变。";
            }
            catch (Exception ex) { status.Text = "标签未保存，旧库保留：" + ex.Message; if (!smokeMode) MessageBox.Show(owner, status.Text, "保存标签", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void OpenAnnotationWindow(FactoryTfAnnotationDocument doc)
        {
            if (busy || scanning || exporting) return;
            if (doc == null || doc.Regions.Count == 0) doc = tfAnnotationFiles.Values.FirstOrDefault(d => d.Regions.Count > 0 && d.ChainKey.StartsWith(mode.SelectedIndex == 0 ? "SIM|" : "HW|"));
            if (doc == null || doc.Regions.Count == 0) { status.Text = "先在TF图圈选并标注，或点击“读取”查看历史标注。"; return; }
            using (var form = new FactoryTfAnnotationWindow(Icon, doc, () =>
            {
                try { doc.Validate(); FactoryLabelStore.Save(labelRoot, doc); tfAnnotationFiles[doc.ChainKey] = doc; lastAnnotationDocument = doc; lastRecognitionSignature = ""; RefreshTfAnnotations(); SchedulePulseRecognition(); }
                catch (Exception ex) { status.Text = "标注变更未自动保存：" + ex.Message; MessageBox.Show(formOwner(), status.Text, "标注保存", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }, UseTfRegionReference, ShowOptionalNotice)) form.ShowDialog(this);
            RefreshTfAnnotations();
        }
        private IWin32Window formOwner() { return this; }
        private void UseTfRegionReference(IWin32Window owner, FactoryTfAnnotationDocument library, FactoryTfAnnotationRegion region)
        {
            if (busy || scanning || exporting) return;
            var points = TfPoints();
            if (points.Count == 0 || points.Any(p => p.Round.ChainKey != library.ChainKey))
            { ShowOptionalNotice(owner,"参考区域", "当前没有相同设备/采集条件的TF数据。历史标签不会自动套用到其他设备、模式或采集条件。"); return; }
            RecordTfRegion(owner, region.Geometry, points.Where(p => p.Pulse.Valid && region.Geometry.Contains(p.Pulse.EquivalentBandwidthMHz, p.Pulse.EquivalentTimeUs)).ToList(), region);
        }
        private void ReadTfAnnotations()
        {
            if (busy || scanning || exporting) return;
            using (var dialog = new OpenFileDialog { Title = "读取TF人工标注文件", Filter = "TF标注文件|*.tfann.xml|XML文件|*.xml" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { var doc = FactoryTfAnnotationDocument.Load(dialog.FileName); OpenAnnotationWindow(doc); status.Text = "历史TF与标签已独立读取；未覆盖当前波形或控制参数。"; }
                catch (Exception ex) { MessageBox.Show(this, "读取标注未成功：" + ex.Message, "TF标注读取", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
