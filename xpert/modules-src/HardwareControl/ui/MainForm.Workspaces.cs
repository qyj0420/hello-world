using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using HVX.OWTS.Task38;

namespace HVX.OWTS.Task38.Desktop
{
    // Independent UI draft, deliberately not a Task38Configuration: these fields cannot
    // silently turn into high-voltage outputs or change the live acquisition session.
    public sealed class CalibrationUiDraft
    {
        public int AcquisitionMode = 0;
        public int ChargeIndex = 11;
        public int RangeIndex = 4;
        public int ImpedanceIndex = 0;
        public bool BandwidthEnabled;
        public decimal BandwidthMHz = 60;
        public decimal FrequencyHz = 100;
        public decimal AverageCount = 16;
        public decimal DurationMs = 60;
        public decimal ThresholdMv = 5;
        public decimal TimeoutSeconds = 3;
        public string InputPort = "A";
        public int ResolutionBits = 14;
        public decimal CableLengthM = 100;
        public decimal VelocityMPerUs = 160;
        public decimal PrePeakSamples = 400;
        public decimal TailSamples = 256;
    }

    internal sealed partial class MainForm
    {
        private readonly Panel pageHost = new Panel { Dock = DockStyle.Fill };
        private readonly Panel pressurePage = new Panel { Dock = DockStyle.Fill };
        private readonly Panel calibrationPage = new Panel { Dock = DockStyle.Fill };
        private readonly Panel hardwarePage = new Panel { Dock = DockStyle.Fill };
        private readonly Panel pressureSettings = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(10) };
        private readonly Button[] navigation = new Button[3];
        private int workspaceIndex = 1;
        private readonly ComboBox calMode = Choice("连续脉冲平均", "定长段周期平均");
        private readonly ComboBox calResolution = Choice("14 bit", "15 bit");
        private readonly NumericUpDown calLength = CalNumber(.1m, 100000, 100, 1);
        private readonly NumericUpDown calVelocity = CalNumber(1, 300, 160, 1);
        private readonly NumericUpDown calPrePeak = CalNumber(256, 100000, 400, 0);
        private readonly NumericUpDown calTail = CalNumber(64, 100000, 256, 0);
        private readonly Label calResolutionHint = new Label { AutoSize = true, MaximumSize = new Size(310, 0), ForeColor = Color.DimGray };
        private readonly ComboBox calInput = Choice("A · 局放输入");
        private readonly ComboBox calCharge = Choice("200 nC", "150 nC", "100 nC", "50 nC", "20 nC", "10 nC", "5 nC", "2 nC", "1 nC", "500 pC", "200 pC", "100 pC", "50 pC", "20 pC", "10 pC");
        private readonly double[] calChargesPc = { 200000, 150000, 100000, 50000, 20000, 10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10 };
        private readonly ComboBox calRange = Choice("±10 mV", "±20 mV", "±50 mV", "±100 mV", "±200 mV", "±500 mV", "±1 V", "±2 V", "±5 V", "±10 V", "±20 V");
        private readonly double[] calRanges = { .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 20 };
        private readonly ComboBox calImpedance = Choice("保持现状 · 不输出", "P1.2 低 · 10 kHz 低截止", "P1.2 高 · 3 MHz 高通 / 短电缆");
        private readonly CheckBox calBandwidth = new CheckBox { Text = "启用带宽参数", AutoSize = true };
        private readonly NumericUpDown calCutoff = CalNumber(1, 60, 60, 1);
        private readonly NumericUpDown calFrequency = CalNumber(1, 1000, 100, 1);
        private readonly NumericUpDown calAverageCount = CalNumber(2, 128, 16, 0);
        private readonly NumericUpDown calDuration = CalNumber(0, 500, 60, 1);
        private readonly NumericUpDown calThreshold = CalNumber(-19999, 19999, 5, 3);
        private readonly NumericUpDown calTimeout = CalNumber(.1m, 60, 3, 1);
        private readonly Button calStart = Button("演示采集");
        private readonly Button calStop = Button("取消校准");
        private readonly Button calSave = Button("保存校准参数");
        private readonly Button calApply = Button("应用仿真系数");
        private readonly Button calClearReference = Button("清除跨档参考");
        private readonly List<CalibrationLevelReference> calibrationLevels = new List<CalibrationLevelReference>();
        private CalibrationIncidentAssessment calibrationQuality;
        private double calibrationDemoAmplitudeFactor = 1;
        private readonly Label calNotice = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(147, 77, 20), Padding = new Padding(12), TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label calResult = new Label { Dock = DockStyle.Fill, Padding = new Padding(12), ForeColor = Navy };
        private readonly Panel calParameters = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(10) };
        private readonly WaveChart calRaw = new WaveChart("现场长段 · 校准脉冲与噪声 · 仿真", "时间 / ms", "输入 / mV", Color.FromArgb(17, 121, 194));
        private readonly WaveChart calPulse = new WaveChart("对齐平均脉冲 · 仿真", "时间 / μs", "输入 / mV", Color.FromArgb(138, 76, 180));
        private bool calibrationBusy, calibrationCancel, loadingCalibration;
        private double calibrationCandidate = Double.NaN;
        private int calibrationLongSampleCount;
        private string CalibrationDraftPath { get { return Path.Combine(dataRoot, activeMode == 0 ? "demo-calibration-ui.xml" : "hardware-calibration-ui.xml"); } }

        private static ComboBox Choice(params object[] values)
        {
            ComboBox c = new ReadableComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(values); c.SelectedIndex = 0; return c;
        }
        private static NumericUpDown CalNumber(decimal min, decimal max, decimal value, int precision)
        { return new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = value, DecimalPlaces = precision, Increment = precision == 0 ? 1 : .1m }; }

        private TableLayoutPanel ParameterTable(Panel parent, string title)
        {
            TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
            Label heading = new Label { Text = title, Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 13, FontStyle.Bold), ForeColor = Navy, TextAlign = ContentAlignment.MiddleLeft };
            table.RowCount = 1; table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); table.Controls.Add(heading, 0, 0); table.SetColumnSpan(heading, 2);
            parent.Controls.Add(table); return table;
        }

        private void BuildNavigation(TableLayoutPanel shell)
        {
            FlowLayoutPanel rail = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.White, Padding = new Padding(6, 15, 0, 0) };
            string[] names = { "校准采集", "加压试验", "硬件设置" };
            for (int i = 0; i < names.Length; i++)
            {
                int selected = i;
                Button b = new NavigationButton { Text = names[i], Number = (i + 1).ToString("00"), Width = 124, Height = 88, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font(Font.FontFamily, 12, FontStyle.Bold), Margin = new Padding(0, 0, 0, 16), Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0; b.Click += (s, e) => SelectWorkspace(selected);
                rail.Controls.Add(b); navigation[i] = b;
            }
            rail.Controls.Add(new Label { Text = "单通道 A\n独立校准\n同步段采集", Width = 124, Height = 90, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleCenter });
            shell.Controls.Add(rail, 0, 0); shell.Controls.Add(pageHost, 1, 0);
            pageHost.Controls.Add(pressurePage); pageHost.Controls.Add(calibrationPage); pageHost.Controls.Add(hardwarePage);
        }

        private void BuildHardwarePage()
        {
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            layout.Controls.Add(settings, 0, 0);
            Panel wiringScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            Label wiring = new Label { Dock = DockStyle.Top, Height = 650, BackColor = Color.White, Padding = new Padding(24), ForeColor = Navy, Font = new Font(Font.FontFamily, 11),
                Text = "硬件与信号分配\n\n控制与电压模块：USB3135\n局放采集模块：5242D，单通道 A\n\n同步触发输入\nP1.0 / PFI0 · DB37 第 21 脚\n局放模块 EXT · 共用外部触发\n\n阻抗板控制\nP1.2 / PFI2 · DB37 第 20 脚\n高：3 MHz 高通，短电缆 TDR\n低：10 kHz 低截止\n\n电源与反馈\nAO0 → VSET\nAI0 → IMON\nAI2 → VMON\nAI4 → 分压器输入\n\n修改设置后需主动点击“连接 / 应用设置”。\n切换页面不会连接设备，也不会产生输出。\n\n校准与加压共用采集设备，必须互斥。\n未确认放电，禁止靠近设备。" };
            wiringScroll.Controls.Add(wiring); layout.Controls.Add(wiringScroll, 1, 0); hardwarePage.Controls.Add(layout);
        }

        private void BuildCalibrationPage()
        {
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4, 0, 0, 0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 345)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(calParameters, 0, 0);
            TableLayoutPanel t = ParameterTable(calParameters, "校准采集参数");
            Row(t, "采集输入端口", calInput); Row(t, "校准放电量", calCharge); Row(t, "采集模式", calMode); Row(t, "局放输入量程", calRange);
            Row(t, "分辨率模式", calResolution);
            Row(t, "电缆长度 / m", calLength); Row(t, "波速 / m每μs", calVelocity);
            Row(t, "入射峰前 / 点", calPrePeak); Row(t, "4L/v 后余量 / 点", calTail);
            Row(t, "阻抗控制选择", calImpedance); Row(t, "带宽设置", calBandwidth); Row(t, "截止频率 / MHz", calCutoff);
            Row(t, "校准源频率 / Hz", calFrequency); Row(t, "触发采集次数 N", calAverageCount); Row(t, "段长跟随加压 / ms", calDuration);
            Row(t, "脉冲阈值 / mV", calThreshold); Row(t, "等待超时 / s", calTimeout);
            Label hint = new Label { AutoSize = true, MaximumSize = new Size(310, 0), ForeColor = Color.DimGray,
                Text = "长度100 m、波速160 m/μs仅为仿真示例，请按实物填写。\n峰后窗口 = 4L/v + 尾部余量。长度与波速只用于开窗，不作定位。\n段模式使用加压页单段时长，平均段内所有有效周期（至少3个）。\n回路参考100 pC ≈ 150 mV；偏差≥3倍拒绝应用。阻抗及带宽暂仅保存参数。" };
            int row = t.RowCount++; t.Controls.Add(hint, 0, row); t.SetColumnSpan(hint, 2);
            row = t.RowCount++; t.Controls.Add(calResolutionHint, 0, row); t.SetColumnSpan(calResolutionHint, 2);
            TableLayoutPanel display = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, Padding = new Padding(10, 0, 0, 0) };
            display.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); display.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); display.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            display.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); display.RowStyles.Add(new RowStyle(SizeType.Absolute, 85));
            display.Controls.Add(calNotice, 0, 0); display.Controls.Add(calRaw, 0, 1); display.Controls.Add(calPulse, 0, 2); display.Controls.Add(calResult, 0, 3);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            foreach (Button b in new[] { calStart, calStop, calSave, calApply, calClearReference }) { b.Margin = new Padding(4, 6, 4, 2); actions.Controls.Add(b); }
            display.Controls.Add(actions, 0, 4); layout.Controls.Add(display, 1, 0); calibrationPage.Controls.Add(layout);
            calStart.BackColor = Color.FromArgb(0, 125, 113); calStart.ForeColor = Color.White;
            calStart.Click += async (s, e) => await RunCalibrationDemo();
            calStop.Click += (s, e) => calibrationCancel = true;
            calSave.Click += (s, e) => Guard(() => { SaveCalibrationDraft(); calResult.Text = "校准参数已保存；没有执行硬件输出，也没有改写加压校准系数。"; });
            calApply.Click += (s, e) => Guard(ApplyDemoCalibration);
            calClearReference.Click += (s, e) => { calibrationLevels.Clear(); InvalidateCalibration(); calResult.Text = "跨档参考已清除，请重新采集。变更校准回路或接线后应执行此操作。"; };
            foreach (ComboBox box in new[] { calMode, calInput, calCharge, calRange, calImpedance, calResolution }) box.SelectedIndexChanged += (s, e) => InvalidateCalibration();
            foreach (NumericUpDown n in new[] { calCutoff, calFrequency, calAverageCount, calThreshold, calTimeout, calLength, calVelocity, calPrePeak, calTail }) n.ValueChanged += (s, e) => InvalidateCalibration();
            inputs["capture"].ValueChanged += (s, e) => { SetCalValue(calDuration, inputs["capture"].Value); if (calMode.SelectedIndex == 1) InvalidateCalibration(); };
            calBandwidth.CheckedChanged += (s, e) => InvalidateCalibration();
        }

        private void SelectWorkspace(int index)
        {
            workspaceIndex = index;
            pressurePage.Visible = index == 1; calibrationPage.Visible = index == 0; hardwarePage.Visible = index == 2;
            for (int i = 0; i < navigation.Length; i++)
            { navigation[i].BackColor = i == index ? Color.FromArgb(92, 131, 164) : Color.FromArgb(37, 51, 62); ((NavigationButton)navigation[i]).Selected = i == index; navigation[i].AccessibleDescription = i == index ? "当前页面" : "切换页面"; navigation[i].Invalidate(); }
            RefreshWorkspaceControls();
        }
        private void RefreshWorkspaceControls()
        {
            connect.Visible = workspaceIndex != 0; start.Visible = workspaceIndex == 1;
            // Keep the stop/exit actions reachable from every page while an HV session exists.
            stop.Visible = reset.Visible = exit.Visible = workspaceIndex == 1 || session != null;
            disconnect.Visible = persistentDisconnect.Visible = workspaceIndex == 1 && activeMode == 0;
            bool free = !connectionInspectionBusy && !connectionInspectionBlocked && !calibrationBusy && session == null && !analyzing && !changingMode && !resetting;
            calStart.Enabled = free && activeMode == 0;
            calStart.Text = activeMode == 0 ? "演示采集" : "真实采集未接入";
            calSave.Enabled = free; calParameters.Enabled = free; calStop.Enabled = calibrationBusy;
            calClearReference.Enabled = free;
            calApply.Enabled = free && activeMode == 0 && !Double.IsNaN(calibrationCandidate);
            calCutoff.Enabled = calBandwidth.Checked;
            calAverageCount.Enabled = calMode.SelectedIndex == 0; calDuration.Enabled = false;
            SetCalValue(calDuration, inputs["capture"].Value);
            calResolutionHint.Text = "125 MS/s（仿真设定，非硬件回读）\n" + PicoResolutionInfo.Describe(calResolution.SelectedIndex == 1 ? 15 : 14, Math.Max(0, calRange.SelectedIndex));
            calNotice.Text = activeMode != 0
                ? "真实校准尚未接入：可保存参数，采集与阻抗输出已禁用。需完成端口实测、校准采集与质量评估联调。"
                : session != null ? "请先“退出测试”，等待加压会话释放后再校准。未确认放电，禁止靠近设备。"
                : "校准仿真：两种模式均显示现场长段（含校准脉冲与噪声）。无硬件输出；纯背景噪声须关闭校准源后测量。";
        }
        private void InvalidateCalibration()
        {
            if (loadingCalibration) return;
            calibrationCandidate = Double.NaN; calibrationQuality = null; calResult.ForeColor = Navy; calResult.Text = "等待有效入射波：系数 = 输入放电量 / 扣基线入射峰值。\n100 pC / 0.150 V ≈ 666.667 pC/V；参数修改后须重新采集。";
            calRaw.Clear(); calPulse.Clear(); RefreshWorkspaceControls();
        }
        private CalibrationUiDraft ReadCalibrationDraft()
        {
            return new CalibrationUiDraft { AcquisitionMode = calMode.SelectedIndex, ChargeIndex = calCharge.SelectedIndex, RangeIndex = calRange.SelectedIndex, ImpedanceIndex = calImpedance.SelectedIndex,
                BandwidthEnabled = calBandwidth.Checked, BandwidthMHz = calCutoff.Value, FrequencyHz = calFrequency.Value, AverageCount = calAverageCount.Value, DurationMs = inputs["capture"].Value, ThresholdMv = calThreshold.Value, TimeoutSeconds = calTimeout.Value,
                ResolutionBits = calResolution.SelectedIndex == 1 ? 15 : 14, CableLengthM = calLength.Value, VelocityMPerUs = calVelocity.Value, PrePeakSamples = calPrePeak.Value, TailSamples = calTail.Value };
        }
        private void SaveCalibrationDraft()
        {
            Directory.CreateDirectory(dataRoot);
            using (Stream stream = File.Create(CalibrationDraftPath)) new XmlSerializer(typeof(CalibrationUiDraft)).Serialize(stream, ReadCalibrationDraft());
        }
        private void LoadCalibrationDraft()
        {
            calibrationLevels.Clear();
            loadingCalibration = true;
            try
            {
                CalibrationUiDraft draft = new CalibrationUiDraft();
                if (File.Exists(CalibrationDraftPath)) using (Stream stream = File.OpenRead(CalibrationDraftPath)) draft = (CalibrationUiDraft)new XmlSerializer(typeof(CalibrationUiDraft)).Deserialize(stream);
                calInput.SelectedIndex = 0;
                calMode.SelectedIndex = Math.Max(0, Math.Min(1, draft.AcquisitionMode)); calCharge.SelectedIndex = Math.Max(0, Math.Min(14, draft.ChargeIndex));
                calRange.SelectedIndex = Math.Max(0, Math.Min(10, draft.RangeIndex)); calImpedance.SelectedIndex = Math.Max(0, Math.Min(2, draft.ImpedanceIndex));
                calBandwidth.Checked = draft.BandwidthEnabled;
                calResolution.SelectedIndex = draft.ResolutionBits == 15 ? 1 : 0;
                SetCalValue(calLength, draft.CableLengthM); SetCalValue(calVelocity, draft.VelocityMPerUs);
                SetCalValue(calPrePeak, draft.PrePeakSamples); SetCalValue(calTail, draft.TailSamples);
                SetCalValue(calCutoff, draft.BandwidthMHz); SetCalValue(calFrequency, draft.FrequencyHz); SetCalValue(calAverageCount, draft.AverageCount);
                SetCalValue(calDuration, draft.DurationMs); SetCalValue(calThreshold, draft.ThresholdMv); SetCalValue(calTimeout, draft.TimeoutSeconds);
            }
            finally { loadingCalibration = false; InvalidateCalibration(); }
        }
        private static void SetCalValue(NumericUpDown control, decimal value) { control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, value)); }

        private async Task RunCalibrationDemo()
        {
            if (connectionInspectionBusy || connectionInspectionBlocked || calibrationBusy || session != null || activeMode != 0 || analyzing || changingMode || resetting) return;
            calibrationCandidate = Double.NaN; calibrationBusy = true; calibrationCancel = false; RefreshView();
            calibrationQuality = null; calPulse.Clear();
            CalibrationUiDraft draft = ReadCalibrationDraft();
            try
            {
                double fullScale = calRanges[draft.RangeIndex];
                if (Math.Abs((double)draft.ThresholdMv / 1000) >= fullScale) throw new ArgumentException("校准脉冲阈值必须在当前输入量程内。");
                if (draft.DurationMs >= draft.TimeoutSeconds * 1000) throw new ArgumentException("等待超时必须大于现场长段时长。");
                calResult.Text = "正在生成校准仿真段；支持取消，禁止同时创建加压会话。";
                await Task.Delay(200);
                var windowSettings = new CalibrationWindowSettings { CableLengthM = (double)draft.CableLengthM, VelocityMPerUs = (double)draft.VelocityMPerUs,
                    PrePeakSamples = (int)draft.PrePeakSamples, TailSamples = (int)draft.TailSamples, FrequencyHz = (double)draft.FrequencyHz,
                    ThresholdV = (double)draft.ThresholdMv / 1000, FullScaleV = fullScale };
                windowSettings.Validate();
                CalibrationDemoResult result = await Task.Run(() => CalibrationDemo.Generate(draft.AcquisitionMode, (int)draft.AverageCount,
                    (double)draft.DurationMs, (double)draft.FrequencyHz, fullScale,
                    calChargesPc[draft.ChargeIndex] / 100 * .150 * calibrationDemoAmplitudeFactor, () => calibrationCancel, windowSettings));
                if (calibrationCancel) throw new OperationCanceledException();
                calRaw.SetAdcWave(result.Raw, 125000000, fullScale / 32767 * 1000, 0, 0);
                calibrationLongSampleCount = result.Raw.Length;
                calRaw.ZeroMarkerMilliseconds = Double.NaN;
                if (!String.IsNullOrEmpty(result.Error)) throw new InvalidOperationException(result.Error);
                calPulse.SetWave(result.Average, 125, 1000);
                string key = String.Join("|", "SIM-A", draft.ResolutionBits, draft.RangeIndex, draft.ImpedanceIndex, draft.BandwidthEnabled, draft.BandwidthMHz, draft.AcquisitionMode, draft.CableLengthM, draft.VelocityMPerUs, draft.PrePeakSamples, draft.TailSamples);
                int pre = windowSettings.PrePeakSamples;
                calibrationQuality = CalibrationIncidentQuality.Analyze(result.Windows, pre / 2, pre - 64, pre + 64, calChargesPc[draft.ChargeIndex], fullScale, false, key, calibrationLevels);
                var q = calibrationQuality;
                if (q.SaveRecommended)
                {
                    calibrationCandidate = q.CoefficientPcPerV;
                    calibrationLevels.RemoveAll(x => x.ConfigurationKey == key && x.ChargePc == calChargesPc[draft.ChargeIndex]);
                    calibrationLevels.Add(new CalibrationLevelReference { ConfigurationKey = key, ChargePc = calChargesPc[draft.ChargeIndex], IncidentPeakV = q.IncidentPeakV });
                }
                calResult.ForeColor = q.SaveRecommended ? Color.FromArgb(0, 110, 100) : Color.Firebrick;
                calResult.Text = String.Format("入射质量：{0}   |   峰值 {1:F3} mV   |   系数 {2}\n信噪比 {3:F1} dB；幅值 CV {4:P1}；有效脉冲 {5} 个\n参考峰值 {6:F1} mV；参考偏差 {7}；跨档偏差 {8}\n{9}\n{10}",
                    q.Quality, q.IncidentPeakV * 1000, q.SaveRecommended ? q.CoefficientPcPerV.ToString("F3") + " pC/V" : "未通过，不可应用",
                    q.SnrDb, q.PeakVariationFraction, q.PulseCount, q.ExpectedPeakV * 1000, DeviationText(q.ReferenceDeviationFactor),
                    DeviationText(q.CrossLevelDeviationFactor), q.Message, result.Message);
            }
            catch (OperationCanceledException) { calResult.Text = "校准仿真已取消，未改写系数。"; }
            catch (Exception ex) { calResult.Text = "校准未完成：" + OperatorDiagnostics.Report(ex); }
            finally { calibrationBusy = false; RefreshView(); }
        }
        private static string DeviationText(double value) { return Double.IsNaN(value) ? "未评估" : value.ToString("F2") + " 倍"; }
        private void ApplyDemoCalibration()
        {
            if (calibrationBusy || activeMode != 0 || session != null || Double.IsNaN(calibrationCandidate)) throw new InvalidOperationException("当前结果不能应用；仅允许无设备会话时应用仿真参考系数。");
            Task38Profile saved = ReadProfile(); saved.PdCalibrationPcPerVolt = calibrationCandidate; saved.Save(ProfilePath);
            V("pdcal", calibrationCandidate); calResult.Text = "参考系数已保存到仿真加压配置。真实硬件配置未改动；加压前仍需应用设置。";
        }
    }

    internal sealed class CalibrationDemoResult
    {
        internal double[][] Windows;
        internal short[] Raw;
        internal double[] Average;
        internal int PulseCount;
        internal double PeakV;
        internal bool Clipped;
        internal string Message;
        internal string Error;
    }
    internal static class CalibrationDemo
    {
        // Synthetic positions create test data, but extraction uses the shared detector.
        internal static CalibrationDemoResult Generate(int mode, int repeats, double durationMs, double frequency, double range, double nominalPeakV, Func<bool> cancelled, CalibrationWindowSettings settings, bool rawOnly = false)
        {
            // Both modes acquire an independent long overview first. Mode 0 then obtains
            // N triggered blocks; mode 1 extracts from this same long overview.
            short[] overview = mode == 0 ? Generate(1, repeats, durationMs, frequency, range, nominalPeakV, cancelled, settings, true).Raw : null;
            int window = settings.WindowSamples + 256, peak = settings.PrePeakSamples + 128;
            int size = mode == 0 ? window * repeats : (int)Math.Round(durationMs * 125000);
            if (size < 1 || size > 64000000) throw new ArgumentException("校准数据超过本程序6400万点预算或时长无效，请减小长度、次数或段长。");
            short[] raw = new short[size]; double[] average = new double[window];
            Random random = new Random(3847); bool clipped = false; int count = 0;
            List<double[]> windows = new List<double[]>();
            for (int i = 0; i < size; i++)
            {
                if (i % 32768 == 0 && cancelled()) throw new OperationCanceledException();
                raw[i] = (short)Math.Round((random.NextDouble() - .5) * .00035 / range * 32767);
            }
            int spacing = mode == 0 ? window : (int)Math.Round(125000000 / frequency);
            int first = mode == 0 ? 0 : spacing / 2;
            for (int start = first; start + window <= size; start += spacing)
            {
                if (cancelled()) throw new OperationCanceledException();
                double amplitude = nominalPeakV * (1 + (random.NextDouble() - .5) * .04);
                double[] incident = new double[window];
                for (int j = 0; j < window; j++)
                {
                    double x = (j - peak) / 18.0;
                    double reflection = 2 * settings.CableLengthM / settings.VelocityMPerUs * settings.SampleRateHz / 1000000;
                    double x1 = (j - peak - reflection) / 18, x2 = (j - peak - 2 * reflection) / 18;
                    double volts = amplitude * (Math.Exp(-x * x / 2) - .4 * Math.Exp(-x1 * x1 / 2) + .2 * Math.Exp(-x2 * x2 / 2)) + raw[start + j] * range / 32767;
                    double adc = volts / range * 32767;
                    if (Math.Abs(adc) >= 32767) clipped = true;
                    raw[start + j] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(adc)));
                    average[j] += raw[start + j] * range / 32767;
                    incident[j] = raw[start + j] * range / 32767;
                }
                windows.Add(incident);
                count++;
            }
            double max = 0;
            if (count > 0) for (int i = 0; i < window; i++) { average[i] /= count; max = Math.Max(max, Math.Abs(average[i])); }
            if (rawOnly) return new CalibrationDemoResult { Raw = raw };
            CalibrationWindowResult extracted;
            try
            {
            if (mode == 0)
            {
                short[][] blocks = new short[repeats][];
                for (int n = 0; n < repeats; n++) { blocks[n] = new short[window]; Array.Copy(raw, n * window, blocks[n], 0, window); }
                extracted = CalibrationWindowing.FromTriggeredBlocks(blocks, settings, cancelled);
            }
            else extracted = CalibrationWindowing.FromSegment(raw, settings, cancelled);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex) { return new CalibrationDemoResult { Raw = overview ?? raw, Error = ex.Message }; }
            return new CalibrationDemoResult { Raw = overview ?? raw, Average = extracted.Average, Windows = extracted.Windows, PulseCount = extracted.Windows.Length, PeakV = max, Clipped = clipped, Message = extracted.Message };
        }
    }
}
