using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Compact single-round bench. Uses exactly the supervised factory worker;
    // no UI-side SDK poll, hidden voltage list, or automatic repressure exists.
    internal sealed class FactorySimpleForm : Form
    {
        private FactorySettings settings = new FactorySettings { TargetKv = 5, MaximumKv = 30, MaximumFeedbackKv = 30, MaximumAoV = 5, DividerOnlyFeedback = true, VmonAfterHvStartup = true, StartupVmonLimitV = 500, ZeroBackgroundLimitV = 500, SafeResidualV = 50, EndAfterPressureCapture = true, OpticalTimeoutRetry=true };
        private readonly Label rampStatistics = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "HV UP：未执行", ForeColor = Color.Teal };
        private readonly FactoryFeedbackTrendControl feedbackTrend = new FactoryFeedbackTrendControl();
        private FactorySequenceProgress sequenceProgress;
        private readonly FactoryShutdownDiagnosticControl shutdownDiagnostic = new FactoryShutdownDiagnosticControl();
        private readonly TabControl plots = new TabControl { Dock = DockStyle.Fill };
        private readonly ComboBox mode = Choice("仿真（不接硬件）", "实机 · 单次加压", "仿真 · 0 kV 光触发（须联锁）", "实机 · 0 kV 光触发（须联锁）", "仿真 · 0 kV 只读（不发光）", "实机 · 0 kV 只读（不发光）");
        private bool ZeroOpticalMode { get { return mode.SelectedIndex == 2 || mode.SelectedIndex == 3; } }
        private readonly ComboBox art = Choice(), pico = Choice();
        private readonly NumericUpDown voltage = new NumericUpDown { Minimum = 0, Maximum = 30, Value = 5,
            DecimalPlaces = 3, Increment = .5M, Width = 180, Font = new Font("Microsoft YaHei UI", 27, FontStyle.Bold) };
        private readonly Button start = Button("开始本轮", 130), stop = Button("停止", 95), scan = Button("读取两卡", 115),
            configure = Button("参数", 80), diagnostic = Button("AI / P0.4 诊断", 130), nextRange = Button("推荐量程 → 下一轮", 180), folder = Button("打开记录", 105);
        private readonly CheckBox wiring = new CheckBox { Text = "已核对反馈接线、换算与现场安全条件（非零加压必选）", AutoSize = true };
        private readonly CheckBox continuous = new CheckBox { Text="连续加压（逐轮确认）", AutoSize=true, Margin=new Padding(5,16,3,0) };
        private readonly NumericUpDown repeatCount = new NumericUpDown { Minimum=1, Maximum=100, Value=1, Width=54, Enabled=false, Margin=new Padding(0,14,6,0) };
        private readonly CheckBox rangeReminder = new CheckBox { Text="量程提醒", Checked=true, AutoSize=true, Margin=new Padding(3,16,6,0) };
        private int? simulationRangeBeforeApply;
        private readonly Label identity = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft },
            emergency = new Label { Text = "P0.4 未读取\n不代表加压许可", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Gainsboro },
            summary = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "目标默认 5 kV；只在点击并确认后执行。0 kV 为自由只读采集。" };
        private readonly TextBox log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        private readonly FactoryWaveformControl combined = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "振荡电压 / 检波局放 · 80 kS/s" },
            raw = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "局放原始数据 · 整段" };
        private readonly Dictionary<string,Button> lamps = new Dictionary<string,Button>();
        private readonly Dictionary<string,string> details = new Dictionary<string,string>();
        private readonly string[] keys = { "INIT", "AO_ZERO", "SAFE", "HV_ON", "HV_DELAY", "HV_UP", "ARM", "ARM_DELAY", "HV_OFF", "OPTICAL", "WAIT_TRIGGER", "READ", "DISCHARGE", "COMPLETE" };
        private readonly string[] names = { "初始化", "AO=0", "反馈稳定 / 联锁", "HV ON", "零输出监测", "升压", "布置两卡/计时", "布置起≥500ms", "HV OFF/AO零", "光 1.5 秒", "采集完成", "读数据", "残压 / 释放", "本轮结束" };
        private CancellationTokenSource cancellation;
        private bool busy, closePending, smoke, completeCaptureDisplayed;
        private readonly FactoryEmergencyNotice emergencyNotice = new FactoryEmergencyNotice();
        private FactoryResult latest;
        private string faultResidualSummary;
        private string activeRecordDirectory;
        private int previewGeneration;
        private Task previewLoad = Task.FromResult(0);

        private async Task LoadPreview(string directory, int generation)
        {
            try
            {
                var preview = await Task.Run(() => FactoryCapturePreview.Load(directory));
                if (IsDisposed || Disposing || generation != previewGeneration || latest != null) return;
                var c = preview.Result.Capture; var d = preview.Detected;
                combined.VoltageReferenceKv = Math.Max(5, preview.Result.Settings.TargetKv);
                combined.Title = "采集波形 · 残压未确认 · 不代表试验合格";
                combined.SetCombined(d.VoltageKv,d.PdEnvelopePc,d.OutputRateHz,d.DurationSeconds*1000);
                raw.Title = "原始局放整段 · 仅查看，非合格结论";
                raw.SetFilteredCapture(c,c.Settings.PdCalibrationPcPerV);
                completeCaptureDisplayed = combined.HasWaveform && raw.HasWaveform;
                nextRange.Enabled = false; plots.SelectedIndex = 1;
                summary.Text = "数据已保存；残压未确认，不代表试验合格或已放电。" + (faultResidualSummary ?? "残压检查继续。");
                if (preview.Result.PostCaptureMonitoringSkipped && faultResidualSummary == null)
                { combined.Title = "振荡电压 / 检波局放"; raw.Title = "局放原始数据 · 整段"; summary.Text = ""; lamps["DISCHARGE"].Visible = false; }
                if(preview.Result.TriggerRecovery!=null && preview.Result.TriggerRecovery.DiagnosticCaptureSaved)
                {combined.Title="补发诊断 · 振荡电压/局放";raw.Title="补发诊断 · 局放整段";summary.Text="联动触发错误；补发段仅作诊断，未计入本轮完成。";}
                log.AppendText("CAPTURE_DISPLAYED " + directory + Environment.NewLine);
            }
            catch (Exception ex)
            { if (!IsDisposed && generation == previewGeneration) log.AppendText("预览失败（不改变安全判定）：" + ex.Message + Environment.NewLine); }
        }
        private string pinnedArt = "", pinnedPico = "";

        internal FactorySimpleForm(bool test = false)
        {
            smoke = test; settings.Input.AutoRange = false;
            Text = "雄新电力科技 · 出厂测试程序 V1.0.30 简洁调试";
            Font = new Font("Microsoft YaHei UI", 9); Size = new Size(1400, 950); MinimumSize = new Size(1120, 800);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(239,244,247);
            using (var stream = GetType().Assembly.GetManifestResourceStream("Factory.Brand.ico")) Icon = new Icon(stream);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
            foreach (int h in new[] { 45, 84, 72, 46, 100 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,112)); Controls.Add(root);
            var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); title.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,235));
            title.Controls.Add(new Label { Text = "单次振荡与局放采集（≤30 kV）", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily,20,FontStyle.Bold) });
            using (var stream = GetType().Assembly.GetManifestResourceStream("Factory.Brand.Logo"))
                title.Controls.Add(new PictureBox { Image = new Bitmap(stream), Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom },1,0);
            root.Controls.Add(title,0,0);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            mode.Width = 210; bar.Controls.Add(mode); bar.Controls.Add(voltage); bar.Controls.Add(new Label { Text = "kV", AutoSize = true, Padding = new Padding(0,18,6,0) });
            foreach (Button b in new[] { start, stop, scan, configure, diagnostic, folder })
            { b.Height = 48; bar.Controls.Add(b); if(b==start){bar.Controls.Add(continuous);bar.Controls.Add(repeatCount);bar.Controls.Add(rangeReminder);} }
            start.BackColor = Color.Teal; start.ForeColor = Color.White; stop.BackColor = Color.Firebrick; stop.ForeColor = Color.White; root.Controls.Add(bar,0,1);
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
            foreach (int w in new[] { 325, 270, 435, 225 }) rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,w / 12.55f));
            rows.Controls.Add(art,0,0); rows.Controls.Add(pico,1,0); rows.Controls.Add(emergency,3,0); rows.SetRowSpan(emergency,2);
            rows.Controls.Add(identity,0,1); rows.SetColumnSpan(identity,3); root.Controls.Add(rows,0,2);
            var stages = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 14 };
            for (int i = 0; i < keys.Length; i++)
            {
                stages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f / 14)); string key = keys[i];
                var b = new FactoryMainForm.StageButton { Text = names[i] + "\n未执行", Dock = DockStyle.Fill, Margin = new Padding(1), Font = new Font(Font.FontFamily,8), Tag = names[i] };
                b.Click += (s,e) =>
                {
                    var dialog = new Form { Text = "步骤证据 · " + b.Tag, Icon = Icon, Font = Font, Size = new Size(850,480), StartPosition = FormStartPosition.CenterParent };
                    dialog.Controls.Add(new TextBox { Text = details.ContainsKey(key) ? details[key] : "本轮未执行。", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill });
                    dialog.FormClosed += (sender,args) => dialog.Dispose(); dialog.Show(this);
                };
                lamps.Add(key,b); stages.Controls.Add(b,i,0);
            }
            root.Controls.Add(stages,0,3);
            var resultBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            resultBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); resultBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,200));
            resultBar.Controls.Add(summary,0,0); resultBar.Controls.Add(nextRange,1,0); root.Controls.Add(resultBar,0,4);
            resultBar.RowStyles.Add(new RowStyle(SizeType.Percent,100)); resultBar.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
            resultBar.Controls.Add(rampStatistics,0,1); resultBar.SetColumnSpan(rampStatistics,2);
            var waves = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Panel1MinSize = 150, Panel2MinSize = 150, Size = new Size(1200,500), SplitterDistance = 250 };
            waves.Panel1.Controls.Add(combined); waves.Panel2.Controls.Add(raw);
            var monitorPage = new TabPage("加压监测 · VMON / IMON / 分压器"); monitorPage.Controls.Add(feedbackTrend); plots.TabPages.Add(monitorPage);
            var capturePage = new TabPage("振荡与局放采集"); capturePage.Controls.Add(waves); plots.TabPages.Add(capturePage);
            var offPage = new TabPage("HV OFF前后诊断"); offPage.Controls.Add(shutdownDiagnostic); plots.TabPages.Add(offPage);
            root.Controls.Add(plots,0,5); root.Controls.Add(log,0,6);
            stop.Enabled = nextRange.Enabled = false; mode.SelectedIndex = 0;
            mode.SelectedIndexChanged += async (s,e) => { if(simulationRangeBeforeApply.HasValue){settings.Input.RangeIndex=simulationRangeBeforeApply.Value;simulationRangeBeforeApply=null;} if(mode.SelectedIndex>=2)voltage.Value=0; wiring.Checked = false; latest = null; nextRange.Enabled = false; combined.ClearWave(); raw.ClearWave(); Reset(); UpdateIdentity(); if (mode.SelectedIndex % 2 == 1 && !smoke) await Discover(); };
            art.SelectedIndexChanged += (s,e) => { var a = art.SelectedItem as ArtDeviceIdentity; if (a != null) pinnedArt = a.Serial; wiring.Checked = false; UpdateIdentity(); };
            pico.SelectedIndexChanged += (s,e) => { if (pico.SelectedItem != null) pinnedPico = (string)pico.SelectedItem; wiring.Checked = false; UpdateIdentity(); };
            scan.Click += async (s,e) => await Discover(); diagnostic.Click += async (s,e) => await Diagnose(); configure.Click += (s,e) => Configure();
            start.Click += async (s,e) => await Run(); stop.Click += (s,e) => { if (cancellation != null) cancellation.Cancel(); };
            voltage.ValueChanged += (s,e) => { wiring.Checked = false; UpdateIdentity(); };
            continuous.CheckedChanged += (s,e)=>repeatCount.Enabled=continuous.Checked && continuous.Enabled;
            nextRange.Click += (s,e) => { if (latest != null && latest.CompleteCaptureAvailable() && latest.Metrics.RecommendedRangeIndex >= 0) { settings.Input.RangeIndex = latest.Metrics.RecommendedRangeIndex; nextRange.Enabled = false; summary.Text = "已采用推荐量程，仅供下一轮；没有自动启动或重加压。"; } };
            folder.Click += (s,e) => { string path = latest == null ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"records") : latest.DirectoryPath; if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); };
            FormClosing += (s,e) => { if (busy) { e.Cancel = true; closePending = true; cancellation.Cancel(); summary.Text = "停止请求已发送，等待本工具关断与退出；不确认设备已放电。"; } };
            UpdateIdentity();
        }
        private static ComboBox Choice(params string[] items)
        { var c = new FactoryMainForm.PrintedChoice { Width = 255, Dock = DockStyle.Fill }; c.Items.AddRange(items); return c; }
        private static Button Button(string text, int width) { return new Button { Text = text, Width = width, Height = 36, FlatStyle = FlatStyle.Flat }; }
        private string DirectoryFor(string prefix) { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"records",prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0,8)); }
        private FactorySettings Snapshot()
        {
            var s = settings.Clone(); s.Simulation = mode.SelectedIndex % 2 == 0; s.TargetKv = (double)voltage.Value; s.MaximumKv = 30; s.MaximumFeedbackKv = 30;
            s.ZeroSynchronizedOptical = ZeroOpticalMode;
            s.FeedbackWiringConfirmed = wiring.Checked; s.IndependentDischargeConfirmed = wiring.Checked; s.Input.RealHardware = !s.Simulation; s.Input.AutoRange = rangeReminder.Checked;
            s.Input.OscillationCircuitConnected = s.Simulation || wiring.Checked;
            var a = art.SelectedItem as ArtDeviceIdentity;
            s.Input.ArtDevice = a == null ? "Dev1" : a.Name; s.Input.ArtSerial = a == null ? "" : a.Serial;
            s.Input.PicoSerial = pico.SelectedItem as string ?? ""; s.Input.ArtTrigger = "/" + s.Input.ArtDevice + "/PFI0"; return s;
        }
        private void UpdateIdentity()
        {
            identity.Text = "R28 · 仅 |AI4| 升压闭环；HV ON前不采AI，启动/背景基准 " + settings.ZeroBackgroundLimitV + " V；AO步长 " + settings.Input.AoStepV +  " V / 窗口 " + settings.FeedbackWindowMs + " ms / 间隔请求 " + settings.Input.LoopDelayMs + " ms；125 MS/s · 14 bit · " + settings.Input.CaptureDurationMs + " ms；分压比 " + settings.Input.DividerVPerV;
            lamps["DISCHARGE"].Visible = voltage.Value == 0 || ZeroOpticalMode || faultResidualSummary != null;
            start.Text = ZeroOpticalMode ? "0 kV 光触发采集" : voltage.Value == 0 ? "0 kV 只读采集" : "开始加压";
            voltage.Enabled = !busy && mode.SelectedIndex<2;
            continuous.Enabled=!busy && voltage.Value>0 && !ZeroOpticalMode;
            repeatCount.Enabled=continuous.Enabled && continuous.Checked;
            wiring.Text=ZeroOpticalMode ? "已独立断源、放电，核对反馈与接线；人员撤离" : voltage.Value == 0 ? "只读采集不要求P0.4高；不发光，起点未同步" : "本轮已按现场程序独立确认放电、核对反馈接线；人员撤离";
            if (!busy)
            {
                lamps["SAFE"].Tag = voltage.Value > 0 ? "急停许可 · 不采AI" : "反馈稳定 / 联锁";
                lamps["HV_DELAY"].Tag = voltage.Value > 0 ? "两路启动≤" + settings.StartupVmonLimitV + " V" : "零输出监测";
                lamps["SAFE"].Invalidate(); lamps["HV_DELAY"].Invalidate();
            }
            scan.Enabled = diagnostic.Enabled = mode.SelectedIndex % 2 == 1 && !busy;
        }
        private void Busy(bool value)
        {
            busy = value; foreach (Control c in new Control[] { start, scan, configure, diagnostic, mode, voltage, wiring, art, pico, nextRange, folder,continuous,repeatCount,rangeReminder }) c.Enabled = !value;
            stop.Enabled = value; if (!value) { UpdateIdentity(); nextRange.Enabled = latest != null && latest.CompleteCaptureAvailable() && latest.Metrics.RecommendedRangeIndex >= 0 && latest.Metrics.RecommendedRangeIndex != settings.Input.RangeIndex; }
        }
        private void Reset()
        { ++previewGeneration; activeRecordDirectory = null; completeCaptureDisplayed = false; rampStatistics.Text = "HV UP：未执行"; shutdownDiagnostic.ClearRecord(); feedbackTrend.ClearTrend(); details.Clear(); foreach (var pair in lamps) { pair.Value.Text = pair.Value.Tag + "\n未执行"; pair.Value.BackColor = Color.White; ((FactoryMainForm.StageButton)pair.Value).StateText = "未执行"; pair.Value.Invalidate(); } emergency.Text = "P0.4 未读取\n不代表加压许可"; emergency.BackColor = Color.Gainsboro; }
        private void Report(string key,string detail)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Report(key,detail))); return; }
            emergencyNotice.Observe(key, busy && voltage.Value > 0);
            if (sequenceProgress != null) sequenceProgress.Observe(key);
            if (key == "FEEDBACK_TREND") { feedbackTrend.Add(detail); return; }
            if (key == "RAMP_PROGRESS" || key == "RAMP_SUMMARY") rampStatistics.Text = detail;
            if (key == "TARGET_HOLD") summary.Text = detail;
            if (key == "FAULT_RESIDUAL" || key == "FAULT_RESIDUAL_END")
            { summary.Text=detail; faultResidualSummary=detail; if (!completeCaptureDisplayed) plots.SelectedIndex=0; }
            if (key == "STEP_DISCHARGE_RUNNING") lamps["DISCHARGE"].Visible = true;
            if (key == "STEP_COMPLETE_UNVERIFIED")
            {
                var end = lamps["COMPLETE"]; end.Text = end.Tag + "\n已结束";
                ((FactoryMainForm.StageButton)end).StateText = "已结束";
                end.BackColor = Color.Gainsboro; end.Invalidate();
            }
            if (key == "SHUTDOWN_DIAG_FILE") { shutdownDiagnostic.LoadRecord(detail); if(faultResidualSummary==null && !completeCaptureDisplayed)plots.SelectedIndex=2; }
            if (key == "FAILED_CAPTURE_FILE" || key == "CAPTURE_READY_FILE")
            {
                if (latest == null && !completeCaptureDisplayed && FactoryCapturePreview.BelongsTo(activeRecordDirectory, detail))
                    previewLoad = LoadPreview(detail, ++previewGeneration);
            }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + " " + key + " " + detail + Environment.NewLine);
            if (key == "EMERGENCY_RELEASED" || key == "EMERGENCY_ACTIVE")
            { emergency.Text = (mode.SelectedIndex % 2 == 0 ? "仿真 · " : "实测 · ") + "P0.4 " + (key == "EMERGENCY_RELEASED" ? "高" : "低") + "\n不是完整加压许可"; emergency.BackColor = key == "EMERGENCY_RELEASED" ? Color.FromArgb(196,232,208) : Color.FromArgb(255,210,210); emergency.ForeColor=key=="EMERGENCY_RELEASED" ? Color.DarkGreen : Color.DarkRed; }
            if (key.StartsWith("STEP_"))
                foreach (string stage in keys) foreach (string state in new[] { "RUNNING", "PASSED", "ERROR", "SKIPPED" })
                    if (key == "STEP_" + stage + "_" + state)
                    { var b = lamps[stage]; string display = state == "PASSED" ? "通过" : state == "ERROR" ? "错误" : state == "SKIPPED" ? "跳过" : "运行";
                        b.Text = b.Tag + "\n" + display; ((FactoryMainForm.StageButton)b).StateText = display;
                        b.BackColor = state == "PASSED" ? Color.FromArgb(196,232,208) : state == "ERROR" ? Color.FromArgb(255,210,210) : state == "SKIPPED" ? Color.FromArgb(255,237,153) : Color.LightBlue;
                        details[stage] = (details.ContainsKey(stage) ? details[stage] + Environment.NewLine : "") + detail; b.Invalidate(); }
            if (key == "HV_CONDITION") summary.Text = detail;
            if (key == "INITIAL_FEEDBACK" || key == "HV_ZERO_FEEDBACK" || key == "FEEDBACK_CONFIG")
            {
                summary.Text = detail;
                string stage = key == "HV_ZERO_FEEDBACK" ? "HV_DELAY" : "SAFE";
                details[stage] = (details.ContainsKey(stage) ? details[stage] + Environment.NewLine : "") + detail;
            }
            if (emergencyNotice.Active) ShowEmergencyNotice();
        }
        private void ShowEmergencyNotice()
        { emergency.Text=FactoryEmergencyNotice.Title; emergency.BackColor=Color.FromArgb(255,210,210); emergency.ForeColor=Color.DarkRed; summary.Text=FactoryEmergencyNotice.Message; }
        private async Task Discover()
        {
            if (busy || mode.SelectedIndex % 2 == 0 || smoke) return; cancellation = new CancellationTokenSource(); Busy(true);
            try
            {
                var found = await Program.Discover(Snapshot(),DirectoryFor("SCAN"),Report,cancellation.Token);
                var chosen = DiscoverySelection.Resolve(found,pinnedArt,pinnedPico);
                art.Items.Clear(); pico.Items.Clear(); foreach (var a in found.ArtDevices) if (a.Model == "USB3135") art.Items.Add(a); foreach (var p in found.PicoSerials) pico.Items.Add(p);
                art.SelectedIndex = chosen.ArtDevice == null ? -1 : art.Items.IndexOf(chosen.ArtDevice); pico.SelectedIndex = chosen.PicoSerial == null ? -1 : pico.Items.IndexOf(chosen.PicoSerial);
                summary.Text = chosen.ArtMessage + "；" + chosen.PicoMessage + "。仅枚举，不操作输出。";
            }
            catch (Exception ex) { summary.Text = "枚举失败：" + ex.Message; Report("SCAN_ERROR",ex.ToString()); }
            finally { cancellation.Dispose(); cancellation = null; Busy(false); if (closePending) Close(); }
        }
        private async Task Diagnose()
        {
            if (busy || mode.SelectedIndex % 2 == 0 || smoke) return; cancellation = new CancellationTokenSource(); Busy(true);
            try { string text = await Program.DiagnoseFeedback(Snapshot(),DirectoryFor("AI_DIAG"),Report,cancellation.Token); ShowText("AI / P0.4 只读诊断 · 未操作输出",text); }
            catch (Exception ex) { Report("AI_DIAG_ERROR",ex.ToString()); ShowText("AI / P0.4 诊断失败",ex.Message); }
            finally { cancellation.Dispose(); cancellation = null; Busy(false); if (closePending) Close(); }
        }
        private async Task Run()
        {
            if (busy || smoke) return;
            FactorySettings s;
            try { s = Snapshot(); s.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure); }
            catch (Exception ex) { ShowText("参数未通过",ex.Message); return; }
            bool repeating = continuous.Checked && s.TargetKv > 0 && !s.ZeroSynchronizedOptical;
            FactorySequenceProgress chosenSequence = null;
            bool startConfirmed = false;
            if (repeating && sequenceProgress != null && sequenceProgress.CanResume)
            {
                using (var dialog = new FactorySequenceResumeDialog(sequenceProgress,s,(int)repeatCount.Value,Icon))
                {
                    dialog.ShowDialog(this);
                    if (dialog.Choice == FactorySequenceChoice.Cancel) return;
                    if (dialog.Choice == FactorySequenceChoice.Continue)
                    { s=sequenceProgress.ResumeRequest(s,(int)repeatCount.Value); chosenSequence=sequenceProgress; }
                    startConfirmed=true;
                }
            }
            if (!s.Simulation && !startConfirmed && MessageBox.Show(this,
                FactoryPressureStartPrompt.Text(s,repeating ? (int)repeatCount.Value : 1,rangeReminder.Checked) +
                    (repeating ? "\n简洁调试入口保留实机逐轮确认。无逐轮弹窗的自动批次请使用‘出厂测试程序.exe’主界面。" : ""),
                "开始加压 / 采集",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            try { s = FactoryStartConfirmation.Accept(s, FactoryRunKind.Pressure, true); }
            catch (Exception ex) { ShowText("参数未通过", ex.Message); return; }
            if (!s.Simulation && s.TargetKv > 0) s.Input.OscillationCircuitConnected = true;
            sequenceProgress = repeating ? chosenSequence ?? new FactorySequenceProgress(s,(int)repeatCount.Value) : null;
            emergencyNotice.Reset(); cancellation = new CancellationTokenSource(); latest = null; faultResidualSummary=null; Reset(); combined.ClearWave(); raw.ClearWave(); Busy(true); summary.Text = "本轮运行中；没有自动重加压。";
            plots.SelectedIndex = 0; feedbackTrend.SetLive(s.TargetKv > 0);
            try
            {
                if(repeating)
                    await FactoryPressureSequence.Run(s,(int)repeatCount.Value,RunSingle,
                        (index,total,next)=>Task.FromResult(MessageBox.Show(this,
                            "下一轮 "+index+" / "+total+"；目标 "+next.TargetKv.ToString("0.000")+" kV；量程 ±"+BenchSettings.Ranges[next.Input.RangeIndex]+" V。\n本轮已独立确认放电、接线有效且人员撤离后，确定继续；取消结束队列。\n本入口为逐轮确认调试；自动批次请使用出厂测试程序主界面。",
                            "简洁调试 · 逐轮确认",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning)==DialogResult.OK),
                        (index,total,next)=>{
                            if(next.Simulation && settings.Input.RangeIndex != next.Input.RangeIndex && !simulationRangeBeforeApply.HasValue) simulationRangeBeforeApply=settings.Input.RangeIndex;
                            settings.Input.RangeIndex=next.Input.RangeIndex;
                            Report("SEQUENCE","第 "+index+" / "+total+" 轮；序列执行批次 "+next.SequenceAttempt+"；已完成 "+sequenceProgress.Completed+" 轮。");
                        },cancellation.Token,
                        (round,quality)=>Task.FromResult(ReviewCapturedRange(round,quality,true)),sequenceProgress,null,
                        (code,detail)=>{Report(code,detail);summary.Text=detail;});
                else await RunSingle(s);
            }
            catch (FactorySequenceStoppedException ex) { SequenceStopped(ex); }
            catch(OperationCanceledException)
            {
                if(sequenceProgress!=null && !sequenceProgress.InFlight)
                {summary.Text="连续队列已停止；已完成 "+sequenceProgress.Completed+" 次，剩余 "+sequenceProgress.Remaining+" 次；未启动下一轮。";Report("SEQUENCE_CANCELLED",summary.Text);}
                else {Report("STEP_COMPLETE_ERROR","本轮已取消，未计完成。");summary.Text="本轮已取消；未确认设备已放电。";}
            }
            catch (FactoryTriggerFailureException ex)
            {
                string message=ex.Message;
                try { var d=FactoryTriggerThresholdPolicy.Shared.Failure(ex.Result); if(d.Changed)settings.Input.ExternalTriggerVolts=d.NewVolts;
                    Report("TRIGGER_THRESHOLD_DECISION",d.Detail);message+="\n"+d.Detail; }
                catch(Exception logError){message+="\n阈值未调整："+logError.Message;}
                Report("STEP_COMPLETE_ERROR",message);summary.Text=message;
                ShowText("联动触发错误",message+"\n队列已停止；检查接线后重新点击加压，本提示不启动输出。");
            }
            catch (Exception ex) { Report("STEP_COMPLETE_ERROR",ex.ToString()); summary.Text = emergencyNotice.Active ? FactoryEmergencyNotice.Message : faultResidualSummary ?? "本轮未完成；查看下方具体错误。未确认放电，禁止靠近设备。"; ShowText(emergencyNotice.Active ? "急停已动作" : "本轮失败 · 残压需确认",summary.Text+Environment.NewLine+ex.Message); }
            finally { if(sequenceProgress != null && sequenceProgress.CanResume)sequenceProgress.RememberStoppedUi(Snapshot()); feedbackTrend.SetLive(false); wiring.Checked = false; if(emergencyNotice.Active)ShowEmergencyNotice();else{emergency.Text += "\n已离线，非实时许可"; emergency.BackColor = Color.Gainsboro;} cancellation.Dispose(); cancellation = null; Busy(false); if (closePending) Close(); }
        }
        private async Task<FactoryResult> RunSingle(FactorySettings s)
        {
                latest=null; faultResidualSummary=null; Reset(); combined.ClearWave();raw.ClearWave();UpdateIdentity();
                activeRecordDirectory = DirectoryFor(s.Simulation ? "SIMPLE_SIM" : "SIMPLE_HW");
                var r = await Program.StartWorker(s,FactoryRunKind.Pressure,activeRecordDirectory,Report,cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (r.CaptureOnlyAvailable())
                {
                    await previewLoad;
                    if (!completeCaptureDisplayed) await LoadPreview(r.DirectoryPath, ++previewGeneration);
                    summary.Text = ""; rampStatistics.Text = r.DescribeRampAndHold();
                    if(s.SequenceCount==1 || s.SequenceRound==s.SequenceCount)
                        ReviewCapturedRange(r,FactoryRangeReview.EvaluateCapturedData(r),false);
                }
                else { latest = r; Display(r); }
                if(r.ZeroVoltageObservation!=null && r.ZeroVoltageObservation.Warning)
                    MessageBox.Show(this,r.ZeroVoltageObservation.Detail,"0 kV残压观察警告",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return r;
        }
        private void SequenceStopped(FactorySequenceStoppedException ex)
        { Report("SEQUENCE_STOPPED",ex.Message); summary.Text=ex.Message; summary.ForeColor=Color.DarkOrange; }
        private bool ReviewCapturedRange(FactoryResult round,LocationRangeResult quality,bool automaticNext)
        {
            if(!FactoryNextRangeReview.NeedsPrompt(round,quality,round.Settings.Input.AutoRange))
            {
                var silent=FactoryNextRangeReview.Silent(round,quality,automaticNext);
                FactoryNextRangeReview.Save(round,silent);
                Report("NEXT_RANGE",silent.Meaning);
                return silent.ApplyNextRound;
            }
            using(var dialog=new FactoryNextRangeDialog(round,quality,round.Capture.Settings.RangeIndex,Icon))
            {
                dialog.ShowDialog(this);
                var choice=FactoryNextRangeReview.Decide(round,quality,round.Capture.Settings.RangeIndex,dialog.ApplyNext);
                FactoryNextRangeReview.Save(round,choice);
                if(choice.ApplyNextRound)
                {
                    if(round.Settings.Simulation && !simulationRangeBeforeApply.HasValue) simulationRangeBeforeApply=settings.Input.RangeIndex;
                    settings.Input.RangeIndex=choice.RecommendedRangeIndex;
                }
                Report("NEXT_RANGE",choice.Meaning);
                return choice.ApplyNextRound;
            }
        }
        private void Display(FactoryResult r)
        {
            ++previewGeneration;
            rampStatistics.Text = r.DescribeRampAndHold();
            var d = r.Metrics.Detected; combined.VoltageReferenceKv = Math.Max(5,r.Settings.TargetKv);
            combined.Title = r.ZeroTriggerDiagnostic ? "0 kV · 数字触发与光输出诊断（非放电确认）" : r.ReadOnlyAcquisition ? "0 kV · 自由采集两路（起点未同步）" : "振荡电压 / 检波局放 · 80 kS/s";
            raw.Title="定位局放整段 · 10 kHz线性相位高通（原始ADC保留）";
            combined.SetCombined(d.VoltageKv,d.PdEnvelopePc,d.OutputRateHz,d.OutputCount * 1000.0 / d.OutputRateHz);
            raw.SetFilteredCapture(r.Capture,r.Capture.Settings.PdCalibrationPcPerV);
            summary.Text = (r.Settings.Simulation ? "仿真 · " : "实测 · ") + "峰值 " + r.Metrics.OscillationPeakKv.ToString("F3") + " kV；最大输入等效局放 " + r.Metrics.MaxPdPc.ToString("F1") + " pC；" + (r.Metrics.FrequencyValid ? "f=" + r.Metrics.FrequencyHz.ToString("F1") + " Hz" : "无有效振荡频率") + "\n" + (r.ReadOnlyAcquisition ? "0 kV 只读；起点未同步，未确认放电。" : r.SafetyDetail);
            if(r.ZeroVoltageObservation!=null)
            {
                var v=r.ZeroVoltageObservation;
                summary.Text="0 kV残压观察"+(v.Warning?"警告":"")+" · 窗口 "+v.DurationMs.ToString("F1")+" ms；均值 "+v.MeanV.ToString("F1")+" V；RMS "+v.RmsV.ToString("F1")+" V；峰值 "+v.AbsolutePeakV.ToString("F1")+" V；阈值50 V。\n"+
                    (v.Warning?"超阈值电压可能含残压或噪声，请独立核实。":"本窗口未检出超阈值点。")+" 非实时记录，不确认已放电。";
                summary.ForeColor=v.Warning?Color.DarkRed:Color.Black;
            }
            else summary.ForeColor=Color.Black;
            Report("RANGE",r.Metrics.RangeDetail); Report("RECORD",r.DirectoryPath);
            Report("PD_BASELINE","原始局放估计基线 " + (d.BaselineV*1000).ToString("F3") + " mV；输入等效 " + (d.BaselineV*r.Settings.Input.PdCalibrationPcPerV).ToString("F1") + " pC，不是局放脉冲电量；DC耦合保留偏置。原始数据不扣零，检波链为10kHz高通。");
            // Navigate only after both complete waveforms were rendered into the controls.
            // A later diagnostic-file notification must not steal this selected tab.
            completeCaptureDisplayed = r.CompleteCaptureAvailable() && combined.HasWaveform && raw.HasWaveform;
            if (completeCaptureDisplayed) plots.SelectedIndex = 1;
        }
        private void ShowText(string title,string text)
        {
            using (var f = new Form { Text = title, Icon = Icon, Font = Font, Size = new Size(880,520), StartPosition = FormStartPosition.CenterParent })
            { f.Controls.Add(new TextBox { Text = text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill }); f.ShowDialog(this); }
        }
        private void Configure()
        {
            using (var f = new Form { Text = "采集与反馈参数 · 非零加压前核对", Icon = Icon, Font = Font, Size = new Size(650,730), StartPosition = FormStartPosition.CenterParent })
            {
                var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(15) };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));
                var fields = new Dictionary<string,NumericUpDown>(); var copy = settings.Clone();
                Action<string,decimal,decimal,decimal,int> add = (name,value,min,max,dp) => { int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute,35)); var n = new NumericUpDown { Minimum = min, Maximum = max, Value = value, DecimalPlaces = dp, Dock = DockStyle.Fill }; fields.Add(name,n); table.Controls.Add(new Label { Text = name, AutoSize = true },0,row); table.Controls.Add(n,1,row); };
                add("分压比 V/V",(decimal)copy.Input.DividerVPerV,.001M,1000000,3); add("加压比 V/V",(decimal)copy.PressureVPerV,.001M,1000000,3);
                add("局放系数 pC/V",(decimal)copy.Input.PdCalibrationPcPerV,.001M,10000000,3); add("反馈满量程 V",(decimal)copy.FeedbackFullScaleV,.001M,10,3);
                add("VMON 满量程 kV",(decimal)copy.VmonFullScaleKv,5,1000,3); add("IMON 满量程 mA",(decimal)copy.ImonFullScaleMa,.001M,1000,3);
                add("限流 mA",(decimal)copy.CurrentLimitMa,.001M,1000,3); add("故障/光触发诊断残压 V",(decimal)copy.SafeResidualV,1,1000,1);
                add("两路启动/背景基准 V",(decimal)copy.ZeroBackgroundLimitV,1,1000,1);
                add("最大 AO V",(decimal)copy.MaximumAoV,.001M,5,3); add("Pico EXT 阈值 V",(decimal)copy.Input.ExternalTriggerVolts,.01M,4.9M,6);
                add("采集长度 ms",(decimal)copy.Input.CaptureDurationMs,1,1000,3);
                add("AO 加压步长 V",(decimal)copy.Input.AoStepV,.0001M,.1M,4);
                add("升压循环请求间隔 ms",copy.Input.LoopDelayMs,1,1000,0);
                add("AI反馈窗口 ms（中位数）",copy.FeedbackWindowMs,3,40,0);
                add("连续加压时限 s",(decimal)copy.RampTimeoutSeconds,.05M,600,2);
                add("达压复核轮数（0取消）",copy.TargetHoldChecks,0,50,0);
                add("达压复核间隔 ms",copy.TargetHoldIntervalMs,1,100,0);
                add("达压复核限时 ms",copy.TargetHoldTimeoutMs,50,2000,0);
                add("布置开始至关断延时 ms",Math.Max(500,copy.Input.ArmDelayMs),500,5000,0);
                var ranges = Choice(); for (int i = 0; i < BenchSettings.Ranges.Length; i++) ranges.Items.Add("±" + BenchSettings.Ranges[i] + " V"); ranges.SelectedIndex = copy.Input.RangeIndex;
                var artDll = new TextBox { Text = copy.Input.ArtLibraryDirectory, Dock = DockStyle.Fill }; var picoDll = new TextBox { Text = copy.Input.PicoLibraryDirectory, Dock = DockStyle.Fill };
                foreach (var pair in new[] { Tuple.Create("Pico A 量程",(Control)ranges), Tuple.Create("ART x64 运行库",(Control)artDll), Tuple.Create("Pico 运行库",(Control)picoDll) }) { int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); table.Controls.Add(new Label { Text = pair.Item1, AutoSize = true },0,row); table.Controls.Add(pair.Item2,1,row); }
                int info = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute,280)); var note = new Label { Text = "单次设定及独立过压上限30 kV；AO≤min(目标/加压比×1.05,最大AO,5 V)。\nVMON信号0～5 V对应0～31 kV；采集卡量程不改变反馈比例。\nPico A DC / 125 MS/s / 14 bit；振荡 ART AI4 ±10 V / 80 kS/s。\n非零自动：HV ON前不采AI，须现场独立确认放电；AO=0、HV ON后两路启动/背景默认500 V。VMON负向容差单独用此基准，范围内负值工作显示按0，原始值不改。\nP0.4独立10 ms请求；监测1000 S/s，窗口中位数反馈；保护仍看原始峰值。\n首次进入±2%停止递增AO；默认复核0轮。正常非零采完不追加AI；故障/光触发诊断残压默认50 V。\n两卡布置开始计时，调用通过且累计满设定延时后HV OFF/AO归零，再光触发1.5秒。\n任务切换有监测空档。故障先关断、显示原因，再只读观察残压；不自动重连或发光。Windows/USB非硬实时。", Dock = DockStyle.Fill }; table.Controls.Add(note,0,info); table.SetColumnSpan(note,2);
                var ok = Button("保存参数",120); ok.DialogResult = DialogResult.OK; var cancel = Button("取消",100); cancel.DialogResult = DialogResult.Cancel;
                var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45 }; actions.Controls.Add(ok); actions.Controls.Add(cancel); f.Controls.Add(table); f.Controls.Add(actions); f.AcceptButton = ok; f.CancelButton = cancel;
                if(smoke) f.Shown += (sender,args) => f.BeginInvoke(new Action(() =>
                { fields["AO 加压步长 V"].Value=.005M; fields["升压循环请求间隔 ms"].Value=7; fields["布置开始至关断延时 ms"].Value=750; fields["AI反馈窗口 ms（中位数）"].Value=20; ok.PerformClick(); }));
                if (f.ShowDialog(this) != DialogResult.OK) return;
                copy.Input.DividerVPerV = (double)fields["分压比 V/V"].Value; copy.PressureVPerV = (double)fields["加压比 V/V"].Value;
                copy.Input.PdCalibrationPcPerV = (double)fields["局放系数 pC/V"].Value; copy.FeedbackFullScaleV = (double)fields["反馈满量程 V"].Value;
                copy.VmonFullScaleKv = (double)fields["VMON 满量程 kV"].Value; copy.ImonFullScaleMa = (double)fields["IMON 满量程 mA"].Value;
                copy.CurrentLimitMa = (double)fields["限流 mA"].Value; copy.SafeResidualV = (double)fields["故障/光触发诊断残压 V"].Value; copy.MaximumAoV = (double)fields["最大 AO V"].Value;
                copy.StartupVmonLimitV = copy.ZeroBackgroundLimitV = (double)fields["两路启动/背景基准 V"].Value;
                copy.Input.ExternalTriggerVolts = (double)fields["Pico EXT 阈值 V"].Value; copy.Input.CaptureDurationMs = (double)fields["采集长度 ms"].Value; copy.Input.RangeIndex = ranges.SelectedIndex;
                copy.Input.AoStepV = (double)fields["AO 加压步长 V"].Value; copy.Input.LoopDelayMs = (int)fields["升压循环请求间隔 ms"].Value;
                copy.RampTimeoutSeconds = (double)fields["连续加压时限 s"].Value;
                copy.FeedbackWindowMs = (int)fields["AI反馈窗口 ms（中位数）"].Value;
                copy.TargetHoldChecks = (int)fields["达压复核轮数（0取消）"].Value;
                copy.TargetHoldIntervalMs = (int)fields["达压复核间隔 ms"].Value;
                copy.TargetHoldTimeoutMs = (int)fields["达压复核限时 ms"].Value;
                copy.Input.ArmDelayMs = (int)fields["布置开始至关断延时 ms"].Value;
                copy.Input.ArtLibraryDirectory = artDll.Text.Trim(); copy.Input.PicoLibraryDirectory = picoDll.Text.Trim(); settings = copy; wiring.Checked = false; latest = null; nextRange.Enabled = false; UpdateIdentity();
            }
        }
        internal async Task Smoke(string directory)
        {
            if(!rangeReminder.Checked || rangeReminder.Left<=repeatCount.Left || stop.Left<=rangeReminder.Left || !Snapshot().Input.AutoRange)
                throw new Exception("R26 simple reminder placement/default/snapshot failed");
            rangeReminder.Checked=false;
            if(Snapshot().Input.AutoRange)throw new Exception("R26 simple reminder unchecked snapshot failed");
            if(settings.StartupVmonLimitV!=500 || settings.ZeroBackgroundLimitV!=500 || settings.SafeResidualV!=50)
                throw new Exception("R23 simple zero/background500 vs residual50 defaults failed");
            Directory.CreateDirectory(directory); configure.PerformClick();
            if(settings.StartupVmonLimitV!=settings.ZeroBackgroundLimitV || settings.ZeroBackgroundLimitV!=500 || settings.SafeResidualV!=50)
                throw new Exception("R23 simple settings save coupled background to residual");
            if(settings.Input.AoStepV!=.005 || settings.Input.LoopDelayMs!=7 || settings.Input.ArmDelayMs!=750 || settings.FeedbackWindowMs!=20) throw new Exception("Simple step/interval/settle/window controls did not apply.");
            var s = Snapshot(); s.Input.RequestedPicoRateHz = 1000000; s.Input.RangeIndex = 5; s.Input.OscillationCircuitConnected = true;
            if (!s.EndAfterPressureCapture || wiring.Parent != null || s.IndependentDischargeConfirmed || s.FeedbackWiringConfirmed)
                throw new Exception("R25 simple fresh-round/one-dialog defaults failed");
            s.EndAfterPressureCapture = false; // Historical fully-verified workflow coverage.
            var c = new FactoryClock(); feedbackTrend.SetLive(true); FactoryResult r;
            using(var recorded=new FactoryAiRecordingBackend(new SimulationFactoryBackend(c),Path.Combine(directory,"normal-diag"),s,c,Report))
                r=FactoryRunner.Run(s,FactoryRunKind.Pressure,recorded,c,(k,t)=>{recorded.ObserveStage(k);Report(k,t);},CancellationToken.None);
            feedbackTrend.SetLive(false); latest = r; plots.SelectedIndex = 2; Display(r);
            if (plots.SelectedIndex != 1 || !completeCaptureDisplayed) throw new Exception("Completed pressure capture did not navigate to waveforms.");
            Report("SHUTDOWN_DIAG_FILE",Path.Combine(directory,"normal-diag","shutdown-diagnostic.xml"));
            if (plots.SelectedIndex != 1) throw new Exception("Late shutdown diagnostics stole the completed waveform tab.");
            if (settings.RampTimeoutSeconds != 10 || s.MaximumFeedbackKv != 30 || s.MaximumKv != 30 || voltage.Maximum != 30 || s.MaximumAoV != 5 || r.RampStatistics.ExitCode != "Reached" || r.TargetHold.Started || r.TargetHold.Reads!=0)
                throw new Exception("R18 first-reach and bounded 30kV UI contract failed.");
            await Task.Delay(100); using (var b = new Bitmap(Width,Height)) { DrawToBitmap(b,new Rectangle(Point.Empty,b.Size)); b.Save(Path.Combine(directory,"simple-5kv.png")); }
            if (!s.DividerOnlyFeedback || feedbackTrend.SampleCount < 2) throw new Exception("Missing divider-only mode or live three-channel trend.");
            plots.SelectedIndex=2; await Task.Delay(30); using(var b=new Bitmap(Width,Height)) { DrawToBitmap(b,new Rectangle(Point.Empty,b.Size)); b.Save(Path.Combine(directory,"simple-shutdown.png")); }
            if(shutdownDiagnostic.Record==null || shutdownDiagnostic.Record.PostReadFinished) throw new Exception("Normal task handoff must expose monitoring gap.");
            plots.SelectedIndex = 1; await Task.Delay(30); using (var b = new Bitmap(Width,Height)) { DrawToBitmap(b,new Rectangle(Point.Empty,b.Size)); b.Save(Path.Combine(directory,"simple-capture.png")); }
            if (!combined.HasWaveform || !raw.HasWaveform || !r.Completed || !r.ResidualSafe || voltage.Value != 5) throw new Exception("Simple 5 kV UI regression.");
            Reset(); latest = null; combined.ClearWave(); raw.ClearWave();
            var captureOnly = s.Clone(); captureOnly.EndAfterPressureCapture = true; c = new FactoryClock();
            var captured = FactoryRunner.Run(captureOnly,FactoryRunKind.Pressure,new SimulationFactoryBackend(c),c,Report,CancellationToken.None);
            string capturedPath = Path.Combine(directory,"capture-only"); Directory.CreateDirectory(capturedPath);
            Program.SaveResult(captured,capturedPath,false); activeRecordDirectory = capturedPath;
            await LoadPreview(capturedPath,++previewGeneration);
            if (!captured.CaptureOnlyAvailable() || !completeCaptureDisplayed || plots.SelectedIndex != 1 || latest != null ||
                !combined.HasWaveform || !raw.HasWaveform || lamps["DISCHARGE"].Visible || summary.Text != "" ||
                ((FactoryMainForm.StageButton)lamps["COMPLETE"]).StateText != "已结束" || lamps["COMPLETE"].BackColor != Color.Gainsboro)
                throw new Exception("R25 simple quiet capture-only end failed: data="+captured.CaptureOnlyAvailable()+",display="+completeCaptureDisplayed+",tab="+plots.SelectedIndex+",residual="+lamps["DISCHARGE"].Visible+",summary="+summary.Text+",end="+((FactoryMainForm.StageButton)lamps["COMPLETE"]).StateText);
            await Task.Delay(30); using(var b=new Bitmap(Width,Height)) {DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(Path.Combine(directory,"simple-r25-end.png"));}
            SequenceStopped(new FactorySequenceStoppedException(1,6,captured,new InvalidOperationException("量程窗口无效测试")));
            Busy(false);
            if(lamps["DISCHARGE"].Visible || ((FactoryMainForm.StageButton)lamps["COMPLETE"]).StateText!="已结束" ||
                !summary.Text.Contains("连续队列停止") || faultResidualSummary!=null || !combined.HasWaveform || !raw.HasWaveform)
                throw new Exception("R26 simple queue stop changed completed capture into residual fault");
            using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(Path.Combine(directory,"simple-r26-queue-stop.png"));}
            s.TargetKv = 0; c = new FactoryClock(); var zero = FactoryZeroInputRunner.Run(s,new SimulationFactoryZeroInput(c),c,Report,CancellationToken.None); plots.SelectedIndex=0; Display(zero);
            if (plots.SelectedIndex != 1) throw new Exception("Completed zero-input capture did not navigate to waveforms.");
            if (!rampStatistics.Text.Contains("未执行")) throw new Exception("Zero result retained previous ramp statistics.");
            if (zero.ResidualSafe || !zero.CompleteCaptureAvailable() || !combined.Title.Contains("未同步")) throw new Exception("Zero mode must not claim synchronized/discharged.");
            mode.SelectedIndex=2;var opt=Snapshot();opt.Input.RequestedPicoRateHz=1000000;opt.Input.RangeIndex=5;
            if(!opt.ZeroSynchronizedOptical || !opt.Simulation || opt.TargetKv!=0 || voltage.Enabled || !start.Text.Contains("光触发"))throw new Exception("Zero optical selector contract failed.");
            c=new FactoryClock();var optical=FactoryZeroTriggerRunner.Run(opt,new SimulationFactoryBackend(c),c,Report,CancellationToken.None);plots.SelectedIndex=0;Display(optical);
            if (plots.SelectedIndex != 1) throw new Exception("Completed zero optical capture did not navigate to waveforms.");
            if(!optical.ZeroTriggerDiagnostic || optical.ResidualSafe || !combined.Title.Contains("数字触发"))throw new Exception("Zero optical UI result mislabeled.");
            plots.SelectedIndex=1;await Task.Delay(30);using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(Path.Combine(directory,"simple-zero-optical.png"));}
            mode.SelectedIndex=4;var free=Snapshot();
            if(free.ZeroSynchronizedOptical || !free.Simulation || free.TargetKv!=0 || voltage.Enabled || !start.Text.Contains("只读"))throw new Exception("Explicit zero read-only selector failed.");
            mode.SelectedIndex=5;free=Snapshot();
            if(free.ZeroSynchronizedOptical || free.Simulation || free.TargetKv!=0)throw new Exception("Real zero read-only selector failed.");
            mode.SelectedIndex=0;
            Reset(); latest=null; combined.ClearWave(); raw.ClearWave();
            if (completeCaptureDisplayed) throw new Exception("Previous completion leaked into a new round.");
            var fault=new FactoryTests.Fixture(); fault.Backend.FeedbackRule=(b,v)=>{if(b.Hv && b.Ao>0)v.ImonRawV=5;return v;};
            using(var recorded=new FactoryAiRecordingBackend(fault.Backend,Path.Combine(directory,"fault-diag"),fault.Settings,fault.Clock,Report))
                try { FactoryRunner.Run(fault.Settings,FactoryRunKind.Pressure,recorded,fault.Clock,(k,t)=>{recorded.ObserveStage(k);Report(k,t);},CancellationToken.None); throw new Exception("Missing simulated protection fault"); }
                catch(FactoryRunException) { }
            if(shutdownDiagnostic.Record==null || !shutdownDiagnostic.Record.PostReadFinished) throw new Exception("Fault post-off diagnostics missing.");
            if (plots.SelectedIndex == 1 || completeCaptureDisplayed) throw new Exception("Fault without capture must retain the diagnostic view.");
            await Task.Delay(30); using(var b=new Bitmap(Width,Height)) {DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(Path.Combine(directory,"simple-shutdown-fault.png"));}
            Reset(); var early=new FactoryTests.Fixture(); early.Backend.PrematureComplete=true; latest=null;
            try { early.Run(Report); } catch(FactoryRunException ex)
            { string failed=Path.Combine(directory,"early"); Directory.CreateDirectory(failed); Program.SaveResult(ex.PartialResult,failed,true); activeRecordDirectory=failed; Report("FAILED_CAPTURE_FILE",failed); }
            await previewLoad;
            if(latest!=null || !combined.Title.Contains("残压未确认") || !raw.HasWaveform) throw new Exception("Early capture not displayed as diagnostic-only.");
            await Task.Delay(30); using(var b=new Bitmap(Width,Height)) {DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(Path.Combine(directory,"simple-early.png"));}
            busy=true;voltage.Value=5;emergencyNotice.Reset();
            Report("EMERGENCY_ACTIVE","仿真事件：P0.4低");
            Report("EMERGENCY_RELEASED","仿真事件：随后复位");
            Report("STEP_COMPLETE_ERROR","仿真事件：本轮失败");
            busy=false;ShowEmergencyNotice();
            if(!emergencyNotice.Active || emergency.ForeColor!=Color.DarkRed || !emergency.Text.Contains("本轮锁存") || !summary.Text.Contains("复位不自动恢复"))
                throw new Exception("R27 simple emergency notice overwritten by release/final error");
            using(var b=new Bitmap(Width,Height)){DrawToBitmap(b,new Rectangle(Point.Empty,b.Size));b.Save(Path.Combine(directory,"simple-r27-emergency.png"));}
            File.WriteAllText(Path.Combine(directory,"simple-ui-test.txt"),"5kV UI + 0kV free/optical input + completed capture navigation + fault diagnostic retention + R27 emergency latch: PASS; HardwareAccessed=False; PhysicalAcceptance=False");
        }
    }
}
