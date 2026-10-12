using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private readonly ComboBox knownCharge = Choice(FactoryCalibrationLevels.Labels), tfScope = Choice("本轮 TF", "累计 TF");
        private readonly NumericUpDown tfThresholdPc = Number(1, .000001M, 1000000000000M, 6);
        private readonly CheckBox tfAccumulate = new CheckBox { Text = "同时累计同设备试验", Checked = true, AutoSize = true };
        private readonly CheckBox monitorExtrema = new CheckBox {Text="显示原始最小/最大值竖线（非瞬时波形）",Checked=false,AutoSize=true,Dock=DockStyle.Top};
        private readonly Button tfConfirm = Button("确认阈值 · 提取最多100个", 210), tfSuggest = Button("重估阈值", 86), tfClear = Button("清空累计", 86),
            tfHistory = Button("加入历史", 86), tfExpand = Button("放大TF", 80), saveData = Button("保存试验数据", 120);
        private readonly Label tfSummary = new Label { Dock = DockStyle.Fill, Text = "等待本轮完整原始数据；TF只统计脉冲候选，不作定位或局放类型判断。", AutoEllipsis = true },
            calibrationOrigin = new Label { Text = "默认/手动系数；有效校准结束后自动带入，也可修改", AutoSize = true };
        private readonly FactoryTfPlot tfPlot = new FactoryTfPlot { Dock = DockStyle.Fill };
        private readonly FactoryTfSession tfSession = new FactoryTfSession();
        private readonly FactoryWaveformControl selectedPulse = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "选中脉冲 · 等待确认阈值" };
        private readonly Button pulsePrevious = Button("上一条", 72), pulseNext = Button("下一条", 72);
        private readonly Label pulsePosition = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "尚未选中脉冲" };
        private readonly ToolTip pulseTip = new ToolTip();
        private readonly FactoryPulseSelection pulseSelection = new FactoryPulseSelection();
        private readonly List<FactoryTfPlot> expandedTfPlots = new List<FactoryTfPlot>();
        private FactoryPulseRawCache pulseRawCache;
        private FactoryTfResult currentTf;
        private SplitContainer waveRowsSplit, upperWaveSplit, pressureWaveSplit, pulseSplit;
        private bool calibrationAutoAccepted, applyingCalibrationValue;
        private string archiveRoot = "", archiveNumber = "";

        private static SplitContainer WaveSplit()
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1200, 300), Orientation = Orientation.Vertical,
                SplitterDistance = 594, Panel1MinSize = 320, Panel2MinSize = 320, SplitterWidth = 8,
                BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(180, 203, 215), Margin = new Padding(3) };
            split.Panel1.BackColor = split.Panel2.BackColor = Color.White;
            bool positioned = false;
            split.SizeChanged += (s,e) =>
            { if (!positioned && split.Width >= 800) { split.SplitterDistance = (split.Width - split.SplitterWidth) / 2; positioned = true; } };
            return split;
        }
        private Control BuildUpperWaveViews()
        {
            upperWaveSplit = WaveSplit(); combined.WrapToolbar = rawPd.WrapToolbar = true;rawPd.EnableLocationInteraction();
            upperWaveSplit.Panel1.Controls.Add(combined); upperWaveSplit.Panel2.Controls.Add(rawPd);
            pressureWaveSplit = new SplitContainer { Dock=DockStyle.Fill, Size=new Size(1500,300),
                Orientation=Orientation.Vertical, SplitterDistance=490, Panel1MinSize=300, Panel2MinSize=650,
                SplitterWidth=8, BorderStyle=BorderStyle.FixedSingle, BackColor=Color.FromArgb(180,203,215), Margin=new Padding(3) };
            pressureWaveSplit.Panel1.Controls.Add(pressureTrend); pressureWaveSplit.Panel2.Controls.Add(upperWaveSplit);
            monitorExtrema.CheckedChanged+=(s,e)=>{pressureTrend.ShowIntervalExtrema=monitorExtrema.Checked;pressureTrend.Invalidate();};
            pressureTrend.ShowIntervalExtrema=monitorExtrema.Checked;
            pressureWaveSplit.Panel1.Controls.Add(monitorExtrema);
            pulseTip.SetToolTip(monitorExtrema,"细曲线为反馈窗口中位数。竖线覆盖约100 ms内原始最小/最大值，含正常升压跨度，不是瞬时尖峰。显隐仅改变绘图，原始CSV和过流/过压保护不变。");
            PreserveSplitFraction(pressureWaveSplit,1.0/3);
            PreserveSplitFraction(upperWaveSplit,.5);
            return pressureWaveSplit;
        }
        private static void PreserveSplitFraction(SplitContainer split,double fraction)
        {
            bool changing=false;
            split.SizeChanged += (s,e) =>
            {
                int width=split.ClientSize.Width-split.SplitterWidth;
                if(width<split.Panel1MinSize+split.Panel2MinSize)return;
                changing=true;
                try { split.SplitterDistance=Math.Max(split.Panel1MinSize,Math.Min(width-split.Panel2MinSize,(int)(width*fraction))); }
                finally { changing=false; }
            };
            split.SplitterMoved += (s,e) =>
            { if(!changing && split.ClientSize.Width>split.SplitterWidth)fraction=(double)split.SplitterDistance/(split.ClientSize.Width-split.SplitterWidth); };
        }
        private Control BuildFourWaveViews()
        {
            waveRowsSplit = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1200, 600), Orientation = Orientation.Horizontal,
                SplitterDistance = 330, Panel1MinSize = 195, Panel2MinSize = 215, SplitterWidth = 8,
                BackColor = Color.FromArgb(180, 203, 215), Margin = Padding.Empty };
            waveRowsSplit.Panel1.Controls.Add(BuildUpperWaveViews()); waveRowsSplit.Panel2.Controls.Add(BuildPulseViews());
            double fraction = .62; bool adjusting = false;
            waveRowsSplit.SizeChanged += (s,e) =>
            {
                int usable = waveRowsSplit.Height - waveRowsSplit.SplitterWidth;
                if (usable < waveRowsSplit.Panel1MinSize + waveRowsSplit.Panel2MinSize) return;
                adjusting = true;
                try { waveRowsSplit.SplitterDistance = Math.Max(waveRowsSplit.Panel1MinSize, Math.Min(usable - waveRowsSplit.Panel2MinSize, (int)(usable * fraction))); }
                finally { adjusting = false; }
            };
            waveRowsSplit.SplitterMoved += (s,e) =>
            { if (!adjusting && waveRowsSplit.Height > waveRowsSplit.SplitterWidth) fraction = (double)waveRowsSplit.SplitterDistance / (waveRowsSplit.Height - waveRowsSplit.SplitterWidth); };
            return waveRowsSplit;
        }
        private Control BuildPulseViews()
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            actions.Controls.Add(new Label { Text = "阈值 / pC", AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
            tfThresholdPc.Dock = DockStyle.None; tfThresholdPc.Width = 110; tfThresholdPc.Height = 28;
            tfScope.Dock = DockStyle.None; tfScope.Width = 105; tfAccumulate.Margin = new Padding(8, 9, 4, 0);
            actions.Controls.Add(tfThresholdPc); actions.Controls.Add(tfSuggest); actions.Controls.Add(tfConfirm); actions.Controls.Add(tfAccumulate);
            actions.Controls.Add(tfScope); actions.Controls.Add(tfExpand); actions.Controls.Add(tfHistory); actions.Controls.Add(tfClear);
            pulseSplit = WaveSplit(); selectedPulse.UsePulseNavigation(pulsePrevious, pulseNext, pulsePosition);
            pulseSplit.Panel1.Controls.Add(selectedPulse);
            var tfPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            tfPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); tfPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tfPane.Controls.Add(BuildAnnotationToolbar(tfPlot), 0, 0);
            tfPane.Controls.Add(tfPlot, 0, 1); pulseSplit.Panel2.Controls.Add(tfPane);
            grid.Controls.Add(actions, 0, 0); grid.Controls.Add(pulseSplit, 0, 1); grid.Controls.Add(tfSummary, 0, 2);
            tfSuggest.Click += (s,e) => SuggestTfThreshold(); tfConfirm.Click += async (s,e) => await ConfirmTf();
            tfScope.SelectedIndexChanged += (s,e) => RefreshTfView(); tfExpand.Click += (s,e) => OpenTfWindow();
            tfClear.Click += (s,e) => { tfSession.Clear(); RefreshTfView(); status.Text = "仅清空内存累计；已保存记录未删除。"; };
            tfHistory.Click += (s,e) => ImportTfHistory(); tfPlot.PulseClicked += (s,e) => ShowTfPulse(e);
            pulsePrevious.Click += (s,e) => MovePulse(-1); pulseNext.Click += (s,e) => MovePulse(1);
            selectedPulse.ConfigurePulseLabelMenu(() => RecordSelectedPulse("原始脉冲"), () => RecordSelectedPulse("Hilbert包络"),
                SelectedPulseLabels, region => EditTfLabel(this, region));
            tfThresholdPc.ValueChanged += (s,e) =>
            {
                if(pulseAnalysisRound!=null){var estimate=FactoryTfAnalysis.Estimate(pulseAnalysisRound.Capture);rawPd.SetThreshold((double)tfThresholdPc.Value,estimate.BaselineV*pulseAnalysisRound.Capture.Settings.PdCalibrationPcPerV);}
                if (currentTf != null && Math.Abs((double)tfThresholdPc.Value - currentTf.ThresholdPc) > .0000005)
                    tfSummary.Text = "阈值已修改，尚未确认；图中仍为已确认阈值 " + Format(currentTf.ThresholdPc, "0.######") + " pC 的结果。点击确认重新提取。";
            };
            rawPd.SelectionChanged+=(s,e)=>PulseWindowChanged();
            rawPd.PulseWindowRequested+=async(first,last)=>await SelectPulseEnvelope(first,last);
            saveData.Click += async (s,e) => await SaveTrialData(); UpdatePostProcessingButtons();
            return grid;
        }
        private void PrepareTfRound(FactoryResult result)
        {
            currentTf = null;
            PreparePulseWindow(result);
            if (result.Kind == FactoryRunKind.Pressure && result.CompleteCaptureAvailable()) SuggestTfThreshold();
            else { tfPlot.SetData(new List<FactoryTfDisplayPoint>()); tfSummary.Text = "校准记录不混入加压试验TF；累计记录仅在累计页查看。"; }
            RefreshTfView(); UpdatePostProcessingButtons();
        }
        private void SuggestTfThreshold()
        {
            var source=pulseAnalysisRound??latest;
            if (source == null || source.Kind != FactoryRunKind.Pressure || source.Capture == null) return;
            var estimate = FactoryTfAnalysis.Estimate(source.Capture);
            tfThresholdPc.Value = Math.Max(tfThresholdPc.Minimum, Math.Min(tfThresholdPc.Maximum, (decimal)estimate.ThresholdPc));
            tfSummary.Text = "已自动建议阈值 " + Format(estimate.ThresholdPc, "0.######") + " pC；原始基线 " + Format(estimate.BaselineV, "0.######") +
                " V。可修改后点击确认；不自动把噪声认作局放。";
        }
        private async Task ConfirmTf()
        {
            if (busy || scanning || exporting || pulseAnalysisRound == null || !rawPd.HasSelection || pulseAnalysisRound.Kind != FactoryRunKind.Pressure) return;
            recognitionSuspended=false;
            var round = pulseAnalysisRound; double threshold = (double)tfThresholdPc.Value,first=rawPd.SelectionStartMs,last=rawPd.SelectionEndMs; exporting = true; SetBusy(true);
            var cancel=new CancellationTokenSource();analysisCancellation=cancel;stop.Enabled=true;var token=cancel.Token;
            try
            {
                status.Text = "只对本轮原始ADC进行阈值开窗和TF分析，不访问硬件。";
                var computed = await Task.Run(() => FactoryTfAnalysis.ExtractSelected(round, threshold,first,last),token);
                token.ThrowIfCancellationRequested();currentTf=computed;
                if (tfAccumulate.Checked)
                { try { tfSession.Add(currentTf); } catch (Exception ex) { Progress("TF_ACCUMULATION", ex.Message); } }
                if (Directory.Exists(round.DirectoryPath)) FactoryDataArchive.SaveXml(currentTf, Path.Combine(round.DirectoryPath, "tf-analysis-" + Guid.NewGuid().ToString("N") + ".xml"));
                RefreshTfView(); status.Text = "TF提取完成：本轮 " + currentTf.Pulses.Count + " 个，质量有效 " + currentTf.Pulses.Count(p => p.Valid) +
                    " 个；每轮最多100个幅值最大的独立候选，同一轮再确认替换统计，不重复累加。";
            }
            catch(OperationCanceledException) {status.Text="本轮TF分析已停止；不应用迟到结果，原始数据保留。";}
            catch (Exception ex) { status.Text = "TF分析失败：" + ex.Message; }
            finally {if(ReferenceEquals(analysisCancellation,cancel))analysisCancellation=null;cancel.Dispose(); exporting = false; SetBusy(false); if (closeAfterStop) Close(); }
        }
        private List<FactoryTfDisplayPoint> TfPoints()
        {
            var results = tfScope.SelectedIndex == 1 ? tfSession.Rounds : (IList<FactoryTfResult>)(currentTf == null ? new List<FactoryTfResult>() : new List<FactoryTfResult> { currentTf });
            return results.SelectMany(r => r.Pulses.Select(p => new FactoryTfDisplayPoint { Round = r, Pulse = p })).ToList();
        }
        private void RefreshTfView()
        {
            var points = TfPoints(); pulseSelection.SetPoints(points); tfPlot.SetData(points); RefreshTfAnnotations(); DisplaySelectedPulse();
            if (tfScope.SelectedIndex == 1 || currentTf != null)
                tfSummary.Text = (tfScope.SelectedIndex == 1 ? "累计 " + tfSession.RoundCount + " 轮" : "本轮") + "：候选 " + points.Count +
                    " 个；有效 TF " + points.Count(p => p.Pulse.Valid) + " 个。" + (tfScope.SelectedIndex == 0 ? "已确认阈值 " + Format(currentTf.ThresholdPc, "0.######") + " pC；" : "各轮使用自己的确认阈值；") + "X=等效频宽/MHz，Y=等效时长/µs；双击点选中脉冲。";
            UpdatePostProcessingButtons();
            SchedulePulseRecognition();
        }
        private void ResetTf(bool clearSession)
        {
            pulseAnalysisRound=null;
            currentTf = null; if (clearSession) tfSession.Clear(); tfPlot.SetData(new List<FactoryTfDisplayPoint>());
            pulseSelection.SetPoints(new List<FactoryTfDisplayPoint>()); pulseRawCache = null; DisplaySelectedPulse();
            tfSummary.Text = "等待本轮完整原始数据并确认阈值；不沿用上一轮阈值或当前结果。";
        }
        private void UpdatePostProcessingButtons()
        {
            bool idle = !busy && !scanning && !exporting && !labelProcessing;
            bool pressure = idle && pulseAnalysisRound != null && pulseAnalysisRound.Kind == FactoryRunKind.Pressure && (pulseAnalysisRound.CompleteCaptureAvailable() || pulseAnalysisRound.CaptureOnlyAvailable()) && rawPd.HasSelection;
            tfConfirm.Enabled = tfSuggest.Enabled = tfThresholdPc.Enabled = pressure;
            tfClear.Enabled = tfHistory.Enabled = idle; tfAccumulate.Enabled = idle;
            saveData.Enabled = idle && latest != null && latest.CompleteCaptureAvailable();
            pulsePrevious.Enabled = idle && pulseSelection.Index > 0;
            pulseNext.Enabled = idle && pulseSelection.Index >= 0 && pulseSelection.Index + 1 < pulseSelection.Count;
            tfScope.Enabled = tfExpand.Enabled = tfPlot.Enabled = selectedPulse.Enabled = idle;
            foreach (var plot in expandedTfPlots) plot.Enabled = idle;
            foreach (var control in annotationActions) control.Enabled = idle;
        }
        private void ImportTfHistory()
        {
            if (busy || scanning || exporting) return;
            using (var dialog = new OpenFileDialog { Title = "加入此前试验TF（同设备、同模式/采集条件）", Filter = "TF分析记录|tf-current.xml;tf-analysis-*.xml|XML记录|*.xml", Multiselect = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var loaded = dialog.FileNames.Select(FactoryDataArchive.LoadTf).ToArray();
                    var staged = new FactoryTfSession(); foreach (var r in tfSession.Rounds) staged.Add(r);
                    foreach (var r in loaded)
                    {
                        if (r.Simulation != (mode.SelectedIndex == 0)) throw new InvalidDataException("仿真和实机历史不能混用。");
                        if (latest != null && latest.Capture != null && r.ChainKey != FactoryTfAnalysis.ChainKey(latest.Capture)) throw new InvalidDataException("历史设备或采集条件与本轮不同，未加入。");
                        staged.Add(r);
                    }
                    // Apply only after all selected files pass, avoiding partial mixed imports.
                    tfSession.Clear(); foreach (var r in staged.Rounds) tfSession.Add(r);
                    tfScope.SelectedIndex = 1; RefreshTfView(); status.Text = "已加入历史TF；相同原始轮次不会重复计数，原始文件未改。";
                }
                catch (Exception ex) { status.Text = "未加入历史TF：" + ex.Message; }
            }
        }
        private void OpenTfWindow()
        {
            var points = TfPoints(); var form = new Form { Text = "雄新电力科技 · TF脉冲统计", Icon = Icon, Font = Font,
                Size = new Size(1080, 780), MinimumSize = new Size(800, 560), StartPosition = FormStartPosition.CenterParent };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            var plot = new FactoryTfPlot { Dock = DockStyle.Fill }; plot.SetData(points); plot.PulseClicked += (s,e) => ShowTfPulse(e); grid.Controls.Add(plot, 0, 1);
            grid.Controls.Add(BuildAnnotationToolbar(plot), 0, 0); plot.SetAnnotations(ActiveAnnotationDocument(false));
            plot.SetSelected(pulseSelection.Current); expandedTfPlots.Add(plot);
            var table = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            foreach (string name in new[] { "采集时间", "峰时/ms", "电量/pC", "等效时长/µs", "等效频宽/MHz", "质量" }) table.Columns.Add(name, name);
            foreach (var p in points) table.Rows.Add(p.Round.CapturedUtc.AddHours(8).ToString("MM-dd HH:mm:ss"), Format(p.Pulse.TimeMs, "0.######"), Format(p.Pulse.ChargePc),
                Format(p.Pulse.EquivalentTimeUs, "0.######"), Format(p.Pulse.EquivalentBandwidthMHz, "0.######"), p.Pulse.Detail);
            table.CellDoubleClick += (s,e) => { if (e.RowIndex >= 0 && e.RowIndex < points.Count) ShowTfPulse(points[e.RowIndex]); };
            grid.Controls.Add(table, 0, 2); form.Controls.Add(grid); form.FormClosed += (s,e) => { expandedTfPlots.Remove(plot); form.Dispose(); }; form.Show(this);
        }
        private void ShowTfPulse(FactoryTfDisplayPoint point)
        {
            if (busy || scanning || exporting) return;
            if (!pulseSelection.Select(point)) { status.Text = "该点不属于当前TF视图；请回到对应轮次或累计视图再选择。"; return; }
            DisplaySelectedPulse(); UpdatePostProcessingButtons(); tabs.SelectedIndex = 0;
        }
        private void MovePulse(int direction)
        {
            if (busy || scanning || exporting || !pulseSelection.Move(direction)) return;
            DisplaySelectedPulse(); UpdatePostProcessingButtons();
        }
        private void DisplaySelectedPulse()
        {
            var point = pulseSelection.Current;
            tfPlot.SetSelected(point); foreach (var plot in expandedTfPlots) plot.SetSelected(point);
            if (point == null)
            { selectedPulse.ClearWave(); selectedPulse.LabelStatus = selectedPulse.LabelCaption = "未识别"; selectedPulse.Title = "选中脉冲 · 等待确认阈值"; pulsePosition.Text = "尚未选中脉冲"; pulseTip.SetToolTip(pulsePosition, ""); return; }
            var p = point.Pulse; string prefix = point.Round.Simulation ? "仿真" : "实测";
            string identity = "脉冲 " + (pulseSelection.Index + 1) + " / " + pulseSelection.Count;
            pulsePosition.Text = prefix + " #" + (pulseSelection.Index + 1) + "/" + pulseSelection.Count + " · " + Format(p.TimeMs, "0.######") + " ms";
            selectedPulse.Title = prefix + " · " + identity;
            string detail = point.Round.CapturedUtc.AddHours(8).ToString("MM-dd HH:mm:ss.fff") + "；采集峰时 " + Format(p.TimeMs, "0.######") + " ms；" +
                Format(p.ChargePc) + " pC；T=" + Format(p.EquivalentTimeUs, "0.######") + " µs，F=" + Format(p.EquivalentBandwidthMHz, "0.######") + " MHz；" + p.Detail +
                "；波形横轴为独立窗口内时间，使用采集时系数 " + Format(point.Round.CoefficientPcPerV) + " pC/V。";
            try
            {
                var samples = FactoryTfPulseReader.Read(point, latest, ref pulseRawCache);
                selectedPulse.SetPulse(samples, point.Round.PicoRateHz, point.Round.CoefficientPcPerV,point.Round.PdFilterProtocol==FactoryPdSignal.FilterId);
                selectedPulse.SetPulseEnvelope(FactoryPulseLabels.Envelope(samples, point.Round.BaselineV), point.Round.CoefficientPcPerV);
                selectedPulse.LabelStatus = MatchFor(point).Text;
                var identified = MatchFor(point); selectedPulse.LabelCaption = identified.Matched ? identified.Label.Name : identified.Conflict ? "标签冲突" : "未识别";
                detail += "\n" + (point.Round.PdFilterProtocol==""?"历史未滤波脉冲":point.Round.PdFilterProtocol) + "；" + MatchFor(point).Text;
                pulsePosition.ForeColor = p.Valid ? Color.DarkSlateGray : Color.Firebrick;
            }
            catch (Exception ex)
            {
                selectedPulse.ClearWave(); selectedPulse.Title = "选中脉冲 · 原始数据不可用";
                pulsePosition.Text = identity + " · 原始数据不可用"; pulsePosition.ForeColor = Color.Firebrick;
                detail += "\n" + ex.Message; status.Text = ex.Message;
            }
            pulseTip.SetToolTip(pulsePosition, detail);
        }
        private async Task SaveTrialData()
        {
            if (busy || scanning || exporting || smokeMode || latest == null) return;
            string suggested = archiveNumber.Length > 0 ? archiveNumber : latest.Capture.PicoSerial;
            using (var dialog = new FactoryDataSaveDialog(Icon, suggested, archiveRoot, "采集身份：ART " + latest.Capture.ArtSerial + "；Pico " + latest.Capture.PicoSerial))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                archiveNumber = dialog.DeviceNumber; archiveRoot = dialog.RootDirectory; var round = latest; var tf = currentTf;
                exporting = true; SetBusy(true);
                try
                { var annotations = ActiveAnnotationDocument(false); string saved = await Task.Run(() => FactoryDataArchive.Save(round, archiveRoot, archiveNumber, tf, tfSession, annotations)); status.Text = "已按设备编号和采集北京时间保存：" + saved; }
                catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
                finally { exporting = false; SetBusy(false); if (closeAfterStop) Close(); }
            }
        }
    }
    internal sealed class FactoryTfDisplayPoint : EventArgs
    { internal FactoryTfResult Round; internal FactoryTfPulse Pulse; }
    internal sealed partial class FactoryTfPlot : Control
    {
        private List<FactoryTfDisplayPoint> points = new List<FactoryTfDisplayPoint>();
        private readonly List<Tuple<PointF, FactoryTfDisplayPoint>> hits = new List<Tuple<PointF, FactoryTfDisplayPoint>>();
        internal event EventHandler<FactoryTfDisplayPoint> PulseClicked;
        internal int RenderCount { get; private set; }
        internal FactoryTfDisplayPoint SelectedPoint { get; private set; }
        internal bool SelectedMarkerDrawn { get; private set; }
        internal FactoryTfPlot() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Color.White; }
        internal void SetData(List<FactoryTfDisplayPoint> source) { CancelRegionDrag(); points = new List<FactoryTfDisplayPoint>(source); RefreshAnnotationMap(); SetSelected(SelectedPoint); }
        internal void SetSelected(FactoryTfDisplayPoint point)
        { SelectedPoint = points.FirstOrDefault(p => FactoryPulseSelection.Same(p, point)); Invalidate(); }
        internal PointF PointPixel(FactoryTfDisplayPoint point)
        {
            var hit = hits.FirstOrDefault(p => FactoryPulseSelection.Same(p.Item2, point));
            if (hit == null) throw new InvalidOperationException("当前TF点尚未绘制或质量无效。"); return hit.Item1;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); hits.Clear(); SelectedMarkerDrawn = false; var g = e.Graphics;
            if (Height < 78 || Width < 250) { TextRenderer.DrawText(g, "点击“放大TF”查看完整统计", Font, ClientRectangle, Color.DarkSlateGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            RenderCount++;
            var p = new Rectangle(62, 32, Math.Max(10, Width - 92), Math.Max(10, Height - 68));
            var valid = points.Where(x => x.Pulse.Valid).ToList();
            double xmax = valid.Count == 0 ? 1 : Math.Max(.001, valid.Max(x => x.Pulse.EquivalentBandwidthMHz) * 1.15);
            double ymax = valid.Count == 0 ? 1 : Math.Max(.001, valid.Max(x => x.Pulse.EquivalentTimeUs) * 1.15);
            RememberAxes(p, xmax, ymax);
            using (var pen = new Pen(Color.FromArgb(223, 231, 239)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int x = p.Left + p.Width * i / 4;
                    g.DrawLine(pen, x, p.Top, x, p.Bottom);
                    TextRenderer.DrawText(g, (xmax * i / 4).ToString("0.###"), Font, new Rectangle(x - 30, p.Bottom + 1, 60, 20), Color.DarkSlateGray, TextFormatFlags.HorizontalCenter);
                }
                int divisions = Math.Max(1, Math.Min(4, p.Height / (Font.Height + 8)));
                for (int i = 0; i <= divisions; i++)
                {
                    int y = p.Bottom - p.Height * i / divisions;
                    g.DrawLine(pen, p.Left, y, p.Right, y);
                    TextRenderer.DrawText(g, (ymax * i / divisions).ToString("G3", CultureInfo.InvariantCulture), Font, new Rectangle(0, y - 9, 58, 20), Color.DarkSlateGray, TextFormatFlags.Right);
                }
            }
            TextRenderer.DrawText(g, "等效时长/µs", Font, new Rectangle(2, 0, 105, 20), Color.DarkSlateGray);
            TextRenderer.DrawText(g, "等效频宽/MHz", Font, new Rectangle(p.Left, Height - 20, p.Width, 20), Color.DarkSlateGray, TextFormatFlags.HorizontalCenter);
            if (valid.Count == 0) { TextRenderer.DrawText(g, "暂无有效TF点；先确认阈值", Font, p, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            DrawAnnotationRegions(g);
            foreach (var item in valid)
            {
                var pixel = new PointF((float)(p.Left + item.Pulse.EquivalentBandwidthMHz / xmax * p.Width), (float)(p.Bottom - item.Pulse.EquivalentTimeUs / ymax * p.Height));
                using (var brush = new SolidBrush(PointColor(item))) g.FillEllipse(brush, pixel.X - 3, pixel.Y - 3, 6, 6);
                hits.Add(Tuple.Create(pixel, item));
            }
            var selected = hits.FirstOrDefault(h => FactoryPulseSelection.Same(h.Item2, SelectedPoint));
            if (selected != null)
            {
                // Draw last so coincident points cannot obscure the selection.
                var at = selected.Item1;
                using (var ring = new Pen(Color.DarkOrange, 2.5f))
                {
                    g.DrawEllipse(ring, at.X - 7, at.Y - 7, 14, 14);
                    g.DrawLine(ring, at.X - 11, at.Y, at.X - 8, at.Y); g.DrawLine(ring, at.X + 8, at.Y, at.X + 11, at.Y);
                }
                int index = points.FindIndex(item => FactoryPulseSelection.Same(item, SelectedPoint));
                TextRenderer.DrawText(g, "选中 #" + (index + 1), Font, new Rectangle(p.Left, 0, p.Width, 24), Color.FromArgb(163, 87, 0), TextFormatFlags.Right);
                SelectedMarkerDrawn = true;
            }
            else if (SelectedPoint != null)
                TextRenderer.DrawText(g, "选中候选质量无效，未绘制TF点", Font, new Rectangle(p.Left, 0, p.Width, 24), Color.Firebrick, TextFormatFlags.Right);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left && DrawingMode == FactoryTfDrawMode.Browse) ActivateAt(e.Location);
        }
        internal void ActivateAt(Point location)
        {
            if (!Enabled) return;
            var nearest = hits.OrderBy(x => Math.Pow(x.Item1.X - location.X, 2) + Math.Pow(x.Item1.Y - location.Y, 2)).FirstOrDefault();
            if (nearest != null && Math.Pow(nearest.Item1.X - location.X, 2) + Math.Pow(nearest.Item1.Y - location.Y, 2) < 100)
            { var handler = PulseClicked; if (handler != null) handler(this, nearest.Item2); }
        }
    }
}
