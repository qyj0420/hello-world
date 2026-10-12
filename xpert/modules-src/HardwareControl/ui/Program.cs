using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.OWTS.Task38;

namespace HVX.OWTS.Task38.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 4 && args[0] == "--connection-check-worker")
            { ConnectionInspectionWorker.Execute(args[1], args[2], args[3]); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--dmc-preview")
            {
                using (ArtDeviceCleanupForm dialog = new ArtDeviceCleanupForm(new ArtDeviceCleanup(new ArtDmcDeviceCatalog(), Path.Combine(Path.GetTempPath(), "Task38-unused-preview")), true))
                {
                    dialog.Shown += async (s, e) =>
                    {
                        await dialog.RefreshDevices();
                        using (Bitmap b = new Bitmap(dialog.Width, dialog.Height))
                        { dialog.DrawToBitmap(b, new Rectangle(Point.Empty, dialog.Size)); b.Save(args[1]); }
                        dialog.Close();
                    };
                    Application.Run(dialog);
                }
                return;
            }
            bool smoke = args.Length == 2 && args[0] == "--ui-smoke";
            string initialPage = args.Length == 2 && args[0] == "--page" ? args[1] : String.Empty;
            using (MainForm form = new MainForm(smoke, initialPage))
            {
                if (args.Length == 2 && args[0] == "--render-preview")
                {
                    form.PreparePreview();
                    form.Show(); Application.DoEvents();
                    using (Bitmap b = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(b, new Rectangle(Point.Empty, form.Size)); b.Save(args[1]); }
                    form.Hide();
                }
                else
                {
                    if(!smoke)Xiongxin.OWTS.Ui.DialogPlacement.Install();
                    if (smoke) form.Shown += async (s, e) => await form.RunUiSmoke(args[1]);
                    Application.Run(form);
                }
            }
        }
    }

    internal sealed partial class MainForm : BrandedForm
    {
        private static readonly Color Navy = Color.FromArgb(26, 47, 70);
        private readonly Dictionary<string, NumericUpDown> inputs = new Dictionary<string, NumericUpDown>();
        private readonly ComboBox mode = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
        private readonly ComboBox range = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox pdResolution = Choice("14 bit", "15 bit");
        private readonly TextBox picoRuntimeDirectory = new TextBox { Dock = DockStyle.Fill };
        private readonly Label pdResolutionHint = new Label { AutoSize = true, MaximumSize = new Size(290, 0), ForeColor = Color.DimGray };
        private void UpdateResolutionHint()
        {
            pdResolutionHint.Text = "局放请求 125 MS/s\n" + PicoResolutionInfo.Describe(pdResolution.SelectedIndex == 1 ? 15 : 14, Math.Max(0, range.SelectedIndex));
        }
        private readonly CheckBox autoRange = new CheckBox { Text = "首零相位后自动评估下一轮量程", Checked = true, AutoSize = true };
        private readonly TextBox artName = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox picoSerial = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox artTriggerMode = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox artTriggerChannel = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox artAnalogChannel = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox artEdge = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox picoSource = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox picoEdge = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TabControl settings = new TabControl { Dock = DockStyle.Fill };
        private readonly Button connect = Button("连接 / 应用设置");
        private readonly ComboBox verificationState = new ReadableComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Button start = Button("开始加压");
        private readonly Button stop = Button("停止加压");
        private readonly Button reset = Button("复位故障");
        private readonly Button disconnect = Button("模拟断连 2 秒");
        private readonly Button persistentDisconnect = Button("模拟持续断连");
        private readonly Button exit = Button("退出测试");
        private readonly Button cleanupArt = Button("清理离线控制模块");
        private readonly Button diagnostics = Button("诊断信息");
        private readonly Label state = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label metrics = new Label { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label analysis = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10), ForeColor = Navy };
        private readonly WaveChart dc = new WaveChart("直流加压过程", "时间 / s", "电压 / kV", Color.FromArgb(17, 121, 194));
        private readonly SynchronizedWaveChart synchronized = new SynchronizedWaveChart();
        private readonly FlowLayoutPanel lamps = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(8) };
        private readonly List<Label> lampLabels = new List<Label>();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
        private Task38Session session;
        private Task38DemoFactory demo;
        private Task38Profile profile;
        private Task38Task6Analyzer analyzer;
        private Task38CycleResult processed;
        private ReconnectForm reconnectForm;
        private bool analyzing, dirty, loading, preview;
        private bool changingMode, resetting, closeAfterRelease;
        private int activeMode, pendingMode = -1;
        private readonly Task38Profile[] modeProfiles = new Task38Profile[2];
        private readonly bool verificationMode;
        private string uiError = String.Empty;
        private readonly string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OHV", "Task38");
        private string ProfilePath { get { return Path.Combine(dataRoot, activeMode == 0 ? "demo-profile.xml" : "hardware-profile.xml"); } }

        public MainForm(bool verify = false, string initialPage = "")
        {
            verificationMode = verify;
            if (verify) dataRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-test-data");
            OperatorDiagnostics.DirectoryPath = Path.Combine(dataRoot, "diagnostics");
            Text = "任务 38 · 振荡波加压与同步采集";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(239, 243, 247);
            Size = new Size(1500, 1020); MinimumSize = new Size(1280, 900); StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(18) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(root);
            mode.Items.AddRange(new object[] { "仿真演示", "真实硬件" }); mode.SelectedIndex = 0;
            root.Controls.Add(BuildBrandedHeader(), 0, 0);
            root.Controls.Add(BuildTraceabilityStrip(), 0, 2);
            string[] names = { "初始化", "HV ON", "延时 50 ms", "HV UP", "HV OFF", "ACQ 布置", "延时 200 ms", "ACQ 采集", "WAIT" };
            foreach (string n in names)
            {
                Label l = new Label { Text = "●  " + n, AutoSize = false, Width = 126, Height = 40,
                    TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.White, ForeColor = Color.Gray, Margin = new Padding(3), BorderStyle = BorderStyle.FixedSingle };
                lamps.Controls.Add(l); lampLabels.Add(l);
            }
            root.Controls.Add(lamps, 0, 1);
            lamps.SizeChanged += (s, e) => { foreach (Label lamp in lampLabels) lamp.Width = Math.Max(68, (lamps.ClientSize.Width - lamps.Padding.Horizontal) / 9 - 6); };
            TableLayoutPanel shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(shell, 0, 3);
            BuildNavigation(shell);
            TableLayoutPanel main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330)); main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pressurePage.Controls.Add(main);
            main.Controls.Add(pressureSettings, 0, 0);
            BuildSettings();
            BuildHardwarePage();
            BuildCalibrationPage();
            Panel chartsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            TableLayoutPanel charts = new TableLayoutPanel { Dock = DockStyle.Top, Height = 640, ColumnCount = 1, RowCount = 4, Padding = new Padding(10, 0, 0, 0) };
            chartsScroll.Controls.Add(charts);
            chartsScroll.SizeChanged += (s, e) => charts.Height = Math.Max(640, chartsScroll.ClientSize.Height);
            charts.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); charts.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            charts.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            charts.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
            charts.Controls.Add(metrics, 0, 0); charts.Controls.Add(dc, 0, 1); charts.Controls.Add(synchronized, 0, 2);
            charts.Controls.Add(analysis, 0, 3);
            main.Controls.Add(chartsScroll, 1, 0);
            FlowLayoutPanel controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(4, 8, 0, 0) };
            foreach (Button b in new[] { connect, start, stop, reset, disconnect, persistentDisconnect, exit, diagnostics }) controls.Controls.Add(b);
            start.BackColor = Color.FromArgb(0, 125, 113); start.ForeColor = Color.White;
            stop.BackColor = Color.FromArgb(173, 50, 50); stop.ForeColor = Color.White;
            root.Controls.Add(controls, 0, 4); root.Controls.Add(state, 0, 5);
            connect.Click += (s, e) => Guard(Connect);
            start.Click += (s, e) => Guard(StartRecordedCycle);
            stop.Click += (s, e) => { if (session != null) session.RequestStop(); };
            reset.Click += async (s, e) => { resetting = true; try { RefreshView(); Task38Controller c = session == null ? null : session.Controller; if (c != null) await Task.Run(() => c.ResetFaultLatch()); } catch (Exception ex) { Error(ex); } finally { resetting = false; } };
            disconnect.Click += async (s, e) => { Task38DemoFactory captured = demo; if (captured == null) return; captured.Connected = false; await Task.Delay(2000); captured.Connected = true; };
            persistentDisconnect.Click += (s, e) => { if (demo == null) return; demo.Connected = !demo.Connected; persistentDisconnect.Text = demo.Connected ? "模拟持续断连" : "恢复模拟通讯"; };
            exit.Click += (s, e) => ExitSession();
            diagnostics.Click += (s, e) =>
            {
                using (BrandedForm dialog = new BrandedForm { Text = "设备诊断信息", Size = new Size(820, 540), StartPosition = FormStartPosition.CenterParent })
                {
                    string raw = session == null ? "当前没有设备会话。" : session.GetConnectionStatus().LastError;
                    if (session != null && session.Controller != null) raw += "\r\n" + session.Controller.GetStatus().LastError;
                    dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                        Text = "以下为维护人员使用的原始驱动诊断；操作提示已转换为设备功能描述。\r\n日志目录：" + OperatorDiagnostics.DirectoryPath + "\r\n" + OperatorDiagnostics.LastWriteFailure + "\r\n\r\n" + raw });
                    dialog.ShowDialog(this);
                }
            };
            cleanupArt.Click += (s, e) => Guard(() =>
            {
                if (connectionInspectionBusy || connectionInspectionBlocked) throw new InvalidOperationException("连接校验未结束，不能清理设备记录。");
                if (session != null) throw new InvalidOperationException("请先退出测试，等待连接释放完成，再清理离线记录。");
                using (ArtDeviceCleanupForm dialog = new ArtDeviceCleanupForm(new ArtDeviceCleanup(new ArtDmcDeviceCatalog(), Path.Combine(dataRoot, "device-records"))))
                { dialog.Shown += async (sender, args) => await dialog.RefreshDevices(); dialog.ShowDialog(this); }
            });
            mode.SelectedIndexChanged += (s, e) => Guard(ChangeMode);
            FormClosing += (s, e) =>
            {
                if(rawSaveBusy){e.Cancel=true;uiError="原始数据正在保存，请稍后关闭。";return;}
                if (connectionInspectionBusy) { e.Cancel = true; uiError = "连接校验正在结束，请稍候关闭（最多约 12 秒）。"; return; }
                if (calibrationBusy) { calibrationCancel = true; e.Cancel = true; uiError = "正在取消校准，请等待结束后关闭。"; return; }
                if (session != null && !preview)
                {
                    Task38StatusSnapshot current = session.Controller == null ? null : session.Controller.GetStatus();
                    if (current != null && current.CycleRunning)
                    {
                        session.RequestStop(); e.Cancel = true;
                        uiError = "已请求停止并接地；等待本轮结束后再关闭。"; return;
                    }
                    e.Cancel = true;
                    closeAfterRelease = true;
                    ExitSession();
                    return;
                }
                timer.Stop();
            };
            int initialWorkspace = String.Equals(initialPage, "calibration", StringComparison.OrdinalIgnoreCase) ? 0
                : String.Equals(initialPage, "hardware", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            LoadMode(); SelectWorkspace(initialWorkspace); timer.Tick += (s, e) => RefreshView(); timer.Start();
        }

        private static Button Button(string text)
        {
            Button b = new Button { Text = text, AutoSize = true, Height = 36, MinimumSize = new Size(100, 36), FlatStyle = FlatStyle.Flat,
                BackColor = Color.White, ForeColor = Navy, Padding = new Padding(8, 0, 8, 0), Margin = new Padding(4, 0, 4, 0), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(210, 221, 232); b.FlatAppearance.MouseOverBackColor = Color.FromArgb(228, 237, 245); return b;
        }
        private TableLayoutPanel Page(string title)
        {
            TabPage page = new TabPage(title) { AutoScroll = true, BackColor = Color.White, Padding = new Padding(8) };
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            page.Controls.Add(t); settings.TabPages.Add(page); return t;
        }
        private void Row(TableLayoutPanel t, string label, Control control)
        {
            int i = t.RowCount++; t.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            t.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
            control.Margin = new Padding(2, 8, 2, 4); t.Controls.Add(control, 1, i);
        }
        private void Number(TableLayoutPanel t, string key, string title, decimal max, int precision)
        {
            NumericUpDown n = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 0, Maximum = max,
                DecimalPlaces = precision, Increment = precision == 0 ? 1 : (decimal)Math.Pow(10, -Math.Min(precision, 3)), ThousandsSeparator = true };
            n.ValueChanged += (s, e) => { if (!loading) dirty = true; }; inputs.Add(key, n); Row(t, title, n);
        }
        private void BuildSettings()
        {
            TableLayoutPanel t = ParameterTable(pressureSettings, "加压参数");
            Number(t, "target", "目标电压 / kV", 1000, 3);
            Number(t, "step", "AO 升压步长 / V", 5, 4);
            Number(t, "stepDelay", "步进延时 / ms", 60000, 0);
            Number(t, "rampTimeout", "升压超时 / s", 3600, 1);
            Number(t, "stall", "限流无增长 / s", 60, 1);
            Number(t, "capture", "单段时长 / ms", 500, 1);
            Number(t, "captureTimeout", "触发等待超时 / s", 60, 1);
            for (int i = 0; i < 11; i++) range.Items.Add(new[] { "±10 mV", "±20 mV", "±50 mV", "±100 mV", "±200 mV", "±500 mV", "±1 V", "±2 V", "±5 V", "±10 V", "±20 V" }[i]);
            range.SelectedIndexChanged += (s, e) => { if (!loading) dirty = true; UpdateResolutionHint(); }; Row(t, "局放输入量程", range);
            Row(t, "局放分辨率模式", pdResolution);
            pdResolution.SelectedIndexChanged += (s, e) => { if (!loading) dirty = true; UpdateResolutionHint(); };
            int row = t.RowCount++; t.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); t.Controls.Add(autoRange, 0, row); t.SetColumnSpan(autoRange, 2);
            autoRange.CheckedChanged += (s, e) => { if (!loading) dirty = true; };
            row = t.RowCount++; t.Controls.Add(pdResolutionHint, 0, row); t.SetColumnSpan(pdResolutionHint, 2);
            Label info = new Label { Text = "电压 80 kS/s\n自动量程仅作用于下一轮\n控制限幅：目标 ÷ 加压比 × 1.10，最高 5 V", AutoSize = true, MaximumSize = new Size(290, 0), ForeColor = Color.DimGray, Padding = new Padding(0, 12, 0, 6) };
            row = t.RowCount++; t.Controls.Add(info, 0, row); t.SetColumnSpan(info, 2);
            TableLayoutPanel c = Page("设备与反馈");
            verificationState.Items.AddRange(new object[] { "设备验证（整机已连接）", "控制箱校验（禁止加压）" });
            verificationState.SelectedIndexChanged += (s, e) => { if (!loading) dirty = true; };
            Row(c, "校验状态（退出后切换）", verificationState);
            Number(c, "divider", "直流分压器比 / V每V", 1000000, 3);
            Number(c, "acDivider", "振荡分压器比 / V每V", 1000000, 3);
            Number(c, "vcal", "电压校准系数", 1000, 5);
            Number(c, "pdcal", "局放校准 / pC每V", 1000000000, 3);
            Number(c, "gain", "加压比 / V每V", 1000000, 2);
            Number(c, "sourceV", "VMON 满量程 / kV", 1000, 3);
            Number(c, "sourceI", "IMON 满量程 / mA", 100000, 3);
            Number(c, "limitI", "限流设定 / mA", 100000, 3);
            Number(c, "residual", "安全残压阈值 / V", 1000000, 1);
            Number(c, "zeroAlarm", "零压报警 / V（0沿用残压）", 1000000, 1);
            Number(c, "inductance", "回路电感 / H", 100000, 4);
            Number(c, "dcr", "电感直流电阻 / Ω", 1000000, 4);
            Number(c, "skip", "导通拟合跳过 / μs", 100000, 0);
            Row(c, "控制模块名称", artName); Row(c, "局放模块序列号", picoSerial);
            Row(c, "局放运行库目录", picoRuntimeDirectory);
            FlowLayoutPanel runtimeActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            Button chooseRuntime = Button("选择DLL"), autoRuntime = Button("自动查找");
            runtimeActions.Controls.Add(chooseRuntime); runtimeActions.Controls.Add(autoRuntime);
            Row(c, "驱动包路径适配", runtimeActions);
            chooseRuntime.Click += (s, e) => { using (var dialog = new OpenFileDialog { Filter = "64位局放运行库|ps5000a.dll", Title = "选择完整配套目录内的ps5000a.dll" })
                if (dialog.ShowDialog(this) == DialogResult.OK) picoRuntimeDirectory.Text = Path.GetDirectoryName(dialog.FileName); };
            autoRuntime.Click += (s, e) => picoRuntimeDirectory.Text = "auto";
            picoRuntimeDirectory.TextChanged += (s, e) => { if (!loading) dirty = true; };
            int deviceRow = c.RowCount++; c.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            c.Controls.Add(cleanupArt, 0, deviceRow); c.SetColumnSpan(cleanupArt, 2);
            Label cleanupHint = new Label { Text = "连接前可用；管理真实控制模块记录，\n仅可删除已确认离线的兼容设备。", AutoSize = true, ForeColor = Color.DimGray };
            deviceRow = c.RowCount++; c.Controls.Add(cleanupHint, 0, deviceRow); c.SetColumnSpan(cleanupHint, 2);
            artName.TextChanged += (s, e) => { if (!loading) dirty = true; }; picoSerial.TextChanged += (s, e) => { if (!loading) dirty = true; };
            TableLayoutPanel tr = Page("同步触发");
            artTriggerMode.Items.AddRange(new object[] { "PFI 数字边沿", "AI 模拟阈值" }); Row(tr, "电压触发方式", artTriggerMode);
            for (int i = 0; i < 4; i++) artTriggerChannel.Items.Add("PFI" + i);
            Row(tr, "电压数字触发通道", artTriggerChannel);
            for (int i = 0; i < 16; i++) artAnalogChannel.Items.Add("ai" + i);
            Row(tr, "电压模拟触发通道", artAnalogChannel);
            Number(tr, "artThreshold", "电压模拟触发阈值 / V", 10, 3); inputs["artThreshold"].Minimum = -10;
            artEdge.Items.AddRange(new object[] { "上升沿", "下降沿" }); Row(tr, "电压触发边沿", artEdge);
            picoSource.Items.AddRange(new object[] { "EXT 同步输入", "A 通道自触发" }); Row(tr, "局放触发通道", picoSource);
            Number(tr, "picoThreshold", "局放 EXT 触发阈值 / V", 5, 3); inputs["picoThreshold"].Minimum = -5;
            Number(tr, "picoAThreshold", "局放 A 触发阈值 / V", 20, 4); inputs["picoAThreshold"].Minimum = -20;
            picoEdge.Items.AddRange(new object[] { "上升沿", "下降沿" }); Row(tr, "局放触发边沿", picoEdge);
            Label triggerHint = new Label { AutoSize = true, MaximumSize = new Size(295, 0), ForeColor = Color.DimGray,
                Text = "默认 PFI0 + EXT，共享触发。\nPFI 为固定 TTL：高≥3 V，低≤0.8 V，输入0～5 V，不提供可调数字阈值。\nAI 阈值模式须将触发线改接所选 AI 与模拟地；不能仅改设置。不要占用已有反馈通道。\nPFI2 已用于带宽控制，PFI3 已用于 PPS，使用前核对冲突。\n下降沿需要外部线路产生该边沿；接地命令仍保持原有逻辑。\nA 自触发并非共享同步，需关闭自动应用量程。\n3 秒默认期限从导通命令后计时，包含固定段采集完成；超时不自动加压。" };
            int triggerRow = tr.RowCount++; tr.Controls.Add(triggerHint, 0, triggerRow); tr.SetColumnSpan(triggerHint, 2);
            foreach (ComboBox box in new[] { artTriggerMode, artTriggerChannel, artAnalogChannel, artEdge, picoSource, picoEdge })
                box.SelectedIndexChanged += (s, e) => { UpdateTriggerControls(); if (!loading) dirty = true; };
        }

        private void UpdateTriggerControls()
        {
            bool analog = artTriggerMode.SelectedIndex == 1;
            artTriggerChannel.Enabled = !analog; artAnalogChannel.Enabled = analog; inputs["artThreshold"].Enabled = analog;
            inputs["picoThreshold"].Enabled = picoSource.SelectedIndex != 1;
            inputs["picoAThreshold"].Enabled = picoSource.SelectedIndex == 1;
        }

        private double N(string key) { return (double)inputs[key].Value; }
        private void V(string key, double value) { inputs[key].Value = (decimal)Math.Max((double)inputs[key].Minimum, Math.Min((double)inputs[key].Maximum, value)); }
        private void LoadMode()
        {
            loading = true;
            try
            {
                profile = modeProfiles[activeMode] ?? (!verificationMode && File.Exists(ProfilePath) ? Task38Profile.Load(ProfilePath) : activeMode == 0 ? Task38DemoFactory.CreateProfile() : new Task38Profile());
                profile.Task6Directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Task6");
                Task38Configuration c = profile.Hardware;
                verificationState.SelectedIndex = c.ControlBoxVerification ? 1 : 0;
                V("target", c.TargetVoltageV / 1000); V("step", c.RampStepAoVolts); V("stepDelay", c.RampStepDelayMilliseconds);
                V("rampTimeout", c.MaximumRampSeconds); V("stall", c.CurrentLimitNoGrowthSeconds); V("capture", c.CaptureDurationSeconds * 1000);
                V("captureTimeout", c.TriggerWaitTimeoutMilliseconds / 1000.0); V("gain", c.VoltageGainVPerAoVolt); V("divider", c.DividerRatioVPerV);
                V("acDivider", profile.AcDividerRatioVPerV); V("vcal", profile.VoltageCalibration); V("pdcal", profile.PdCalibrationPcPerVolt);
                V("sourceV", c.SourceVoltageFeedbackFullScaleV / 1000); V("sourceI", c.SourceCurrentFeedbackFullScaleA * 1000);
                V("limitI", c.CurrentLimitA * 1000); V("residual", c.ResidualVoltageSafeThresholdV); V("inductance", profile.InductanceH);
                V("zeroAlarm", c.ZeroVoltageAlarmThresholdV);
                V("dcr", profile.InductorDcrOhm);
                artTriggerMode.SelectedIndex = c.ArtUseAnalogTrigger ? 1 : 0;
                string terminal = c.ArtFiniteTriggerTerminal ?? String.Empty;
                artTriggerChannel.SelectedItem = terminal.Substring(terminal.LastIndexOf('/') + 1).ToUpperInvariant();
                if (artTriggerChannel.SelectedIndex < 0) artTriggerChannel.SelectedIndex = 0;
                artAnalogChannel.SelectedItem = c.ArtAnalogTriggerChannel;
                if (artAnalogChannel.SelectedIndex < 0) artAnalogChannel.SelectedIndex = 1;
                artEdge.SelectedIndex = c.ArtTriggerFalling ? 1 : 0; picoEdge.SelectedIndex = c.PicoTriggerFalling ? 1 : 0;
                picoSource.SelectedIndex = c.PicoTriggerOnChannelA ? 1 : 0;
                V("artThreshold", c.ArtAnalogTriggerThresholdV); V("picoThreshold", c.PicoExternalTriggerThresholdV); V("picoAThreshold", c.PicoChannelATriggerThresholdV);
                UpdateTriggerControls();
                V("skip", c.SwitchingTransientSkipMicroseconds); range.SelectedIndex = c.PicoRangeIndex; autoRange.Checked = c.AutoApplyTask6RangeForNextCycle;
                pdResolution.SelectedIndex = c.PicoResolutionBits == 15 ? 1 : 0; UpdateResolutionHint();
                artName.Text = c.ArtDeviceName; picoSerial.Text = c.PicoSerial;
                picoRuntimeDirectory.Text = c.PicoSdkLibraryDirectory;
                analysis.Text = "量程评估：等待同步采集。以任务 6 检出的第一振荡零相位为起点；导通瞬态保存在原始数据中。";
                uiError = mode.SelectedIndex == 0 ? "仿真演示：使用合成波形，无真实硬件输出。" : "真实硬件：请填写现场标定参数；打开界面不会自动加压。";
                dirty = false;
                LoadCalibrationDraft();
                string checkpoint = Path.Combine(dataRoot, mode.SelectedIndex == 0 ? "demo-interrupted.xml" : "hardware-interrupted.xml");
                if (!verificationMode && File.Exists(checkpoint))
                {
                    InterruptedOperation saved = Task38Session.LoadCheckpoint(checkpoint);
                    analysis.Text = String.Format("保留的中断记录 {0:g}：目标 {1:F3} kV，末次实测 {2:F3} kV，AO {3:F3} V。\n历史记录不是当前残压；请连接后重新确认状态。", saved.CapturedUtc.ToLocalTime(), saved.Configuration.TargetVoltageV / 1000, saved.LastMeasuredVoltageV / 1000, saved.LastAoCommandVolts);
                }
            }
            catch (Exception ex) { uiError = "读取设置失败：" + OperatorDiagnostics.Report(ex); }
            finally { loading = false; }
        }
        private Task38Profile ReadProfile()
        {
            Task38Profile p = new Task38Profile { Hardware = profile.Hardware.Clone(), VoltageCalibration = N("vcal"),
                AcDividerRatioVPerV = N("acDivider"), PdCalibrationPcPerVolt = N("pdcal"), InductanceH = N("inductance"), InductorDcrOhm = N("dcr"),
                Task6Directory = profile.Task6Directory };
            Task38Configuration c = p.Hardware;
            c.ControlBoxVerification = verificationState.SelectedIndex == 1;
            c.TargetVoltageV = N("target") * 1000; c.RampStepAoVolts = N("step"); c.RampStepDelayMilliseconds = (int)N("stepDelay");
            c.MaximumRampSeconds = N("rampTimeout"); c.CurrentLimitNoGrowthSeconds = N("stall"); c.CaptureDurationSeconds = N("capture") / 1000;
            c.TriggerWaitTimeoutMilliseconds = (int)(N("captureTimeout") * 1000);
            c.PicoCaptureTimeoutMilliseconds = c.ArtCaptureTimeoutMilliseconds = c.TriggerWaitTimeoutMilliseconds;
            c.VoltageGainVPerAoVolt = N("gain"); c.DividerRatioVPerV = N("divider"); c.SourceVoltageFeedbackFullScaleV = N("sourceV") * 1000;
            c.SourceCurrentFeedbackFullScaleA = N("sourceI") / 1000; c.CurrentLimitA = N("limitI") / 1000; c.HardOverCurrentA = 0;
            c.ResidualVoltageSafeThresholdV = N("residual"); c.SwitchingTransientSkipMicroseconds = N("skip");
            c.ZeroVoltageAlarmThresholdV = N("zeroAlarm");
            c.PicoRangeIndex = range.SelectedIndex; c.AutoApplyTask6RangeForNextCycle = autoRange.Checked;
            c.PicoResolutionBits = pdResolution.SelectedIndex == 1 ? 15 : 14;
            c.PicoSdkLibraryDirectory = picoRuntimeDirectory.Text.Trim();
            c.ArtDeviceName = artName.Text.Trim(); c.ArtFiniteTriggerTerminal = "/" + c.ArtDeviceName + "/" + artTriggerChannel.SelectedItem;
            c.ArtUseAnalogTrigger = artTriggerMode.SelectedIndex == 1; c.ArtAnalogTriggerChannel = (string)artAnalogChannel.SelectedItem;
            c.ArtAnalogTriggerThresholdV = N("artThreshold"); c.ArtTriggerFalling = artEdge.SelectedIndex == 1;
            c.PicoTriggerOnChannelA = picoSource.SelectedIndex == 1; c.PicoTriggerFalling = picoEdge.SelectedIndex == 1;
            c.PicoExternalTriggerThresholdV = N("picoThreshold"); c.PicoChannelATriggerThresholdV = N("picoAThreshold");
            c.PicoSerial = picoSerial.Text.Trim(); c.GroundInSafeIdle = c.HoldGroundAfterCapture = true;
            return p;
        }
        private void Connect()
        {
            if (connectionInspectionBusy || connectionInspectionBlocked) throw new InvalidOperationException("连接校验未结束，不能创建加压会话。");
            if (calibrationBusy) throw new InvalidOperationException("校准采集期间不能连接加压会话。");
            if (changingMode || resetting) throw new InvalidOperationException("请等待旧连接释放或故障复位完成。");
            if (analyzing) throw new InvalidOperationException("等待本轮任务6分析完成后应用参数。");
            Task38Profile requested = ReadProfile();
            Task38Configuration effective = requested.EffectiveHardware();
            string validation = effective.Validate();
            if (validation.Length != 0) throw new ArgumentException(validation);
            analyzer = new Task38Task6Analyzer(requested.Task6Directory);
            requested.Save(ProfilePath);
            if (session == null)
            {
                demo = mode.SelectedIndex == 0 ? new Task38DemoFactory() : null;
                ITask38DeviceFactory factory = demo == null ? (ITask38DeviceFactory)new Task38HardwareFactory() : demo;
                session = new Task38Session(factory, Path.Combine(dataRoot, demo == null ? "hardware-interrupted.xml" : "demo-interrupted.xml"));
                session.Connect(effective);
            }
            else session.ApplyConfiguration(effective);
            profile = requested; processed = null; dirty = false; uiError = String.Empty;
            if (effective.ControlBoxVerification) { dc.Clear(); synchronized.Clear(); }
        }
        private void ExitSession()
        {
            if (session == null) return;
            session.ExitTest();
            uiError = "正在停止测试并释放连接，完成后可重新连接或切换模式。未确认放电，禁止靠近设备！";
        }

        private void ChangeMode()
        {
            if (changingMode || mode.SelectedIndex == activeMode) return;
            int requestedMode = mode.SelectedIndex;
            changingMode = true;
            try { mode.SelectedIndex = activeMode; }
            finally { changingMode = false; }
            if (connectionInspectionBusy || connectionInspectionBlocked || calibrationBusy || analyzing || resetting || session != null &&
                (session.GetConnectionStatus().Phase != ConnectionPhase.Ready || session.GetConnectionStatus().WorkerBusy ||
                 session.Controller == null || session.Controller.GetStatus().CycleRunning))
                throw new InvalidOperationException("请等待本轮加压、采集、分析或重连完成后切换模式。");
            Task38Profile saved = ReadProfile();
            saved.Save(ProfilePath);
            modeProfiles[activeMode] = saved;
            pendingMode = requestedMode;
            if (session == null) CompleteModeChange();
            else { changingMode = true; ExitSession(); }
            RefreshView();
        }

        private void CompleteModeChange()
        {
            if (pendingMode < 0) return;
            activeMode = pendingMode; pendingMode = -1;
            changingMode = true;
            try { mode.SelectedIndex = activeMode; LoadMode(); }
            finally { changingMode = false; }
            dc.Clear(); synchronized.Clear(); processed = null;
        }

        private void ObserveReleasedSession()
        {
            if (session == null || session.GetConnectionStatus().Phase != ConnectionPhase.Exited) return;
            if (!session.ReleaseFinished || analyzing || resetting) return;
            if (session.ReleaseError.Length != 0)
            {
                uiError = "连接释放失败，禁止创建新会话。请先确认放电，再点击退出测试重试释放：" + session.ReleaseError;
                return;
            }
            session = null; demo = null; analyzer = null; processed = null; changingMode = false;
            persistentDisconnect.Text = "模拟持续断连";
            if (reconnectForm != null && !reconnectForm.IsDisposed) reconnectForm.CloseFromOwner();
            uiError = "连接已释放，可重新连接或切换模式。未确认放电，禁止靠近设备！";
            CompleteModeChange();
            if (closeAfterRelease) { Close(); return; }
        }

        private void RefreshView()
        {
            if (preview) return;
            ObserveReleasedSession();
            if (IsDisposed) return;
            ConnectionSnapshot connection = session == null ? null : session.GetConnectionStatus();
            RefreshTraceability(connection);
            Task38Controller c = session == null ? null : session.Controller;
            Task38StatusSnapshot s = c == null ? null : c.GetStatus();
            if (connection != null) OperatorDiagnostics.Record(connection.LastError);
            if (s != null) OperatorDiagnostics.Record(s.LastError);
            bool ready = connection != null && connection.Phase == ConnectionPhase.Ready;
            bool running = s != null && s.CycleRunning;
            mode.Enabled = !connectionInspectionBusy && !connectionInspectionBlocked && !calibrationBusy && !changingMode && !analyzing && !resetting && !running && (session == null || ready && !connection.WorkerBusy);
            cleanupArt.Enabled = session == null && !connectionInspectionBusy && !connectionInspectionBlocked;
            settings.Enabled = !connectionInspectionBusy && !connectionInspectionBlocked && !calibrationBusy && !running && !analyzing && !resetting && !changingMode && (session == null || ready);
            pressureSettings.Enabled = settings.Enabled;
            connect.Enabled = settings.Enabled;
            verificationState.Enabled = session == null && settings.Enabled;
            start.Enabled = !rawSaveBusy && !calibrationBusy && ready && s != null && s.StartAllowed && !analyzing && !dirty && !resetting && !changingMode;
            RefreshRawCaptureActions(running);
            start.Text = verificationState.SelectedIndex == 1 ? "控制箱校验禁止加压" : N("target") == 0 ? "零压检查采集" : "开始加压";
            RefreshWorkspaceControls();
            stop.Enabled = running; reset.Enabled = ready && !running && !resetting && !analyzing && s != null && s.State == Task38ControllerState.Faulted;
            disconnect.Enabled = demo != null && ready; persistentDisconnect.Enabled = demo != null && connection.Phase != ConnectionPhase.Exited && (!connection.WorkerBusy || !demo.Connected);
            exit.Enabled = session != null && (connection.Phase != ConnectionPhase.Exited || session.ReleaseFinished && session.ReleaseError.Length != 0);
            if (ready && s != null)
            {
                metrics.Text = String.Format("实测 {0:F3} kV     目标 {1:F3} kV     电流 {2:F3} mA     AO {3:F3} V", s.ActualVoltageV / 1000, profile.Hardware.TargetVoltageV / 1000, s.CurrentA * 1000, s.AoCommandVolts);
                if (profile.Hardware.TargetVoltageV == 0)
                    metrics.Text = String.Format("零压监测  分压器 {0:F2} V   电源反馈 {1:F2} V   阈值 {2:F2} V", s.ActualVoltageV, s.SourceVoltageFeedbackV, profile.Hardware.EffectiveZeroVoltageThresholdV);
                if (s.ControlBoxVerification)
                {
                    metrics.Text = String.Format("控制箱原始输入  AI0 {0:F4} V   AI2 {1:F4} V   AI4 {2:F4} V", s.CurrentFeedbackRawV, s.SourceVoltageFeedbackRawV, s.DividerFeedbackRawV);
                    analysis.Text = "控制箱校验：未连接完整反馈回路，不换算为有效高压或残压。\nHV禁止使能，AO保持0，不释放接地，不执行零压触发；不证明设备已放电。";
                }
                if (running)
                {
                    if (dc.Count == 0) dc.StartedUtc = DateTime.UtcNow;
                    dc.Append((DateTime.UtcNow - dc.StartedUtc).TotalSeconds, s.ActualVoltageV / 1000);
                }
                if (!dirty && range.SelectedIndex != s.PicoRangeIndex)
                {
                    loading = true; range.SelectedIndex = s.PicoRangeIndex; loading = false;
                    profile.Hardware.PicoRangeIndex = s.PicoRangeIndex;
                }
                if (!running && !analyzing)
                {
                    Task38CycleResult r = c.GetLastResult();
                    if (r != null && !Object.ReferenceEquals(r, processed)) { processed = r; CompleteRunRecord(r); Analyze(c, r); }
                }
                if (analyzing) { mode.Enabled = false; settings.Enabled = false; pressureSettings.Enabled = false; connect.Enabled = false; start.Enabled = false; reset.Enabled = false; }
            }
            else metrics.Text = connectionInspectionBusy ? "正在校验设备连接（不执行端口输出）…" : "实测电压 --      电流 --      加压会话未就绪 / 请连接并应用设置";
            int active = 8;
            if (connection != null && (connection.Phase == ConnectionPhase.Connecting || connection.Phase == ConnectionPhase.Reconnecting)) active = 0;
            else if (s != null)
            {
                switch (s.State)
                {
                    case Task38ControllerState.Precheck: case Task38ControllerState.ReleasingGround: active = 0; break;
                    case Task38ControllerState.HvEnableDelay: active = 2; break;
                    case Task38ControllerState.Ramping: active = 3; break;
                    case Task38ControllerState.SourceOff: active = 4; break;
                    case Task38ControllerState.ArmingAcquisition: active = 5; break;
                    case Task38ControllerState.GroundDelay: active = 6; break;
                    case Task38ControllerState.CaptureWaiting: active = 7; break;
                }
            }
            for (int i = 0; i < lampLabels.Count; i++)
            {
                bool on = i == active || (i == 1 && (active == 2 || active == 3));
                lampLabels[i].ForeColor = on ? Color.White : Color.Gray;
                lampLabels[i].BackColor = on ? Color.FromArgb(0, 128, 119) : Color.White;
            }
            state.Text = DeviceMessages.Describe(!String.IsNullOrEmpty(uiError) ? uiError : connection == null ? "等待连接" : ready && s != null ? s.StateMessage : connection.Message);
            if (OperatorDiagnostics.LastWriteFailure.Length > 0) state.Text += "；诊断日志未能保存";
            state.ForeColor = connection != null && connection.Phase == ConnectionPhase.TimedOut || s != null && s.State == Task38ControllerState.Faulted ? Color.Firebrick : Navy;
            if (connection != null && (connection.Phase == ConnectionPhase.Connecting || connection.Phase == ConnectionPhase.Reconnecting || connection.Phase == ConnectionPhase.TimedOut || connection.Phase == ConnectionPhase.ReconnectedAwaitingConfirmation))
            {
                if (reconnectForm == null || reconnectForm.IsDisposed) { reconnectForm = new ReconnectForm(session); reconnectForm.Show(this); }
                reconnectForm.UpdateSnapshot(connection);
            }
            else if (reconnectForm != null && !reconnectForm.IsDisposed) reconnectForm.CloseFromOwner();
        }

        private async void Analyze(Task38Controller capturedController, Task38CycleResult r)
        {
            if (r.UsbDividerRawVolts.Length > 0)
                synchronized.SetVoltage(r.UsbDividerRawVolts, r.UsbSampleRateHz, profile.AcDividerRatioVPerV * profile.VoltageCalibration / 1000, profile.Hardware.CaptureDurationSeconds);
            if (r.IsBackgroundCapture && r.PicoMaximumAdc > 0)
                synchronized.SetDischarge(r.PicoRawAdc, r.PicoSampleRateHz, r.PicoFullScaleVolts / r.PicoMaximumAdc * profile.PdCalibrationPcPerVolt,
                    0, profile.Hardware.PicoStartRelativeToArtNanoseconds * 1e-9, true);
            if (!r.Success) { analysis.Text = "本轮未完成：" + DeviceMessages.ExitReason(r.ExitReason) + "\n" + OperatorDiagnostics.Report(r.ErrorMessage); return; }
            if (r.IsBackgroundCapture)
            {
                synchronized.SetDischarge(r.PicoRawAdc, r.PicoSampleRateHz,
                    r.PicoFullScaleVolts / r.PicoMaximumAdc * profile.PdCalibrationPcPerVolt,
                    0, profile.Hardware.PicoStartRelativeToArtNanoseconds * 1e-9, true);
                analysis.Text = "零压检查：显示全段，HV未使能、AO为0 V；电压段已按零压阈值检查。\n背景等效量不代表已识别局放；不计算介损、频率或首零相位量程。待机继续监测分压器与电源反馈。\n触发等待阶段AI切换为段采集，实时反馈暂不可用；不能替代独立硬件高压保护。";
                return;
            }
            analyzing = true; analysis.Text = "任务 6 正在分析：检测首个振荡零相位，评估其后局放量程……";
            try
            {
                Task38AnalysisResult result = await Task.Run(() => analyzer.Analyze(r, profile));
                if (session == null || session.GetConnectionStatus().Phase != ConnectionPhase.Ready || !Object.ReferenceEquals(session.Controller, capturedController)) return;
                capturedController.ApplyTask6RangeFeedback(result.RangeFeedback);
                synchronized.SetDischarge(r.PicoRawAdc, r.PicoSampleRateHz,
                    r.PicoFullScaleVolts / r.PicoMaximumAdc * profile.PdCalibrationPcPerVolt,
                    result.RangeFeedback.Task6Success ? result.RangeFeedback.FirstZeroTimeSeconds : Double.NaN, profile.Hardware.PicoStartRelativeToArtNanoseconds * 1e-9);
                string loss = result.VoltageAnalysisSuccess && !Double.IsNaN(result.CorrectedTanDelta) && !Double.IsInfinity(result.CorrectedTanDelta)
                    ? (result.CorrectedTanDelta * 100).ToString("F4") + "%（任务6；质量码 " + result.VoltageQualityFlags + "）" : "无效，需检查波形";
                analysis.Text = String.Format("首零相位 {0:F3} ms   频率 {1:F1} Hz   衰减 {2:F2} /s   局放峰值 {3:F2} pC\n补偿介损 {5}　{4}",
                    result.RangeFeedback.FirstZeroTimeSeconds * 1000, result.FrequencyHz, result.DampingPerSecond,
                    result.RangeFeedback.PdMaximumAbsolute, DeviceMessages.Describe(r.RepeatPressurizationMessage), loss);
                analysis.Text += String.Format("\n{0}：{1:F3} MS/s · {2:F3} ns/点 · {3:N0}点 · 模式{4} bit / 量程规格{5} bit",
                    activeMode == 0 ? "仿真元数据（非实测）" : "采集回读（规格位数非ENOB）", r.PicoSampleRateHz / 1000000,
                    r.PicoSampleIntervalNanoseconds, r.PicoRawAdc.Length, r.PicoResolutionModeBits, r.PicoRangeHardwareBits);
                profile.Hardware.PicoRangeIndex = capturedController.GetConfigurationSnapshot().PicoRangeIndex;
                loading = true;
                try { range.SelectedIndex = profile.Hardware.PicoRangeIndex; }
                finally { loading = false; }
                profile.Save(ProfilePath);
            }
            catch (Exception ex) { analysis.Text = "量程评估失败，保留当前档位：" + OperatorDiagnostics.Report(ex); }
            finally { analyzing = false; }
        }
        private void Guard(Action f) { try { f(); } catch (Exception ex) { Error(ex); } }
        private void Error(Exception ex) { uiError = OperatorDiagnostics.Report(ex); MessageBox.Show(this, uiError, "设备操作提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        internal void PreparePreview()
        {
            preview = true; timer.Stop();
            for (int i = 0; i < 150; i++) dc.Append(i * 0.05, Math.Min(10, i * 0.075));
            double[] w = new double[4800];
            for (int i = 0; i < w.Length; i++) { double t = i / 80000.0 - .002; w[i] = t < 0 ? 10 : 10 * Math.Exp(-60 * t) * Math.Cos(2 * Math.PI * 300 * t); }
            synchronized.SetVoltage(w, 80000, 1, .06);
            metrics.Text = "实测 10.000 kV     目标 10.000 kV     电流 3.000 mA     AO 1.000 V";
            analysis.Text = "演示界面 · 首零相位 2.833 ms   频率 300.0 Hz\n量程将由正式任务 6 评估首零相位之后的数据，并用于下一轮。";
            state.Text = "仿真预览：本图使用合成波形，未连接硬件。";
            lampLabels[8].BackColor = Color.FromArgb(0, 128, 119); lampLabels[8].ForeColor = Color.White;
        }

        internal async Task RunUiSmoke(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            try
            {
                if (session != null) throw new Exception("UI startup unexpectedly connected hardware");
                double savedGain = N("gain"), savedTarget = N("target");
                V("gain", 0); V("target", 0);
                Task inspection = InspectConnection(false);
                if (!connectionInspectionBusy || mode.Enabled || connect.Enabled || calStart.Enabled || cleanupArt.Enabled)
                    throw new Exception("Inspection did not interlock hardware ownership");
                bool inspectionConnectBlocked = false;
                try { Connect(); } catch (InvalidOperationException) { inspectionConnectBlocked = true; }
                if (!inspectionConnectBlocked) throw new Exception("Connect bypassed inspection interlock");
                await inspection;
                if (session != null || start.Enabled || !inspectionIdentity.Contains("SIM-USB3135") || !inspectionIdentity.Contains("SIM-5242D") || !inspectConnection.Enabled)
                    throw new Exception("Discovery was blocked by HV parameters or created a live session");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-connection-check.png")); }
                string oldName = artName.Text; artName.Text = "DevChanged"; RefreshView();
                if (identityLine.Text.Contains("SIM-USB3135")) throw new Exception("Changed device selection retained old inspection snapshot");
                artName.Text = oldName;
                V("gain", savedGain); V("target", savedTarget);
                string timeoutInput = Path.Combine(outputDirectory, "inspection-timeout-request.xml");
                using (var stream = File.Create(timeoutInput)) new System.Xml.Serialization.XmlSerializer(typeof(ConnectionInspectionRequest)).Serialize(stream,
                    new ConnectionInspectionRequest { Simulation = true, SimulationDelayMilliseconds = 5000 });
                var timedOut = await ConnectionInspectionWorker.Run(timeoutInput, "pico", Path.Combine(outputDirectory, "inspection-timeout-result.xml"), 1000);
                if (timedOut.Passed || !timedOut.WorkerStopped || File.Exists(Path.Combine(outputDirectory, "inspection-timeout-result.xml")))
                    throw new Exception("Timed-out owned inspection helper was not stopped");
                synchronized.SetVoltage(new double[] { 1, -2, 1 }, 1000, 3, .010);
                synchronized.SetDischarge(new short[] { 30000, 30000, 10, -20, 5 }, 1000, 2, .004, .002);
                if (Math.Abs(synchronized.FirstDischargeMilliseconds - 4) > .00001 ||
                    Math.Abs(synchronized.VoltageAxisExtent - 6 * 1.12) > .001 || Math.Abs(synchronized.DischargeAxisExtent - 40 * 1.12) > .001)
                    throw new Exception("Dual-axis units, offset or first-zero crop is incorrect");
                synchronized.SetDischarge(new short[] { 1, 2 }, 1000, 1, Double.NaN, 0);
                if (synchronized.DischargeCount != 0 || synchronized.VoltageCount != 3) throw new Exception("Invalid first zero did not suppress PD only");
                synchronized.Clear();
                V("dcr", 5.25); V("picoThreshold", -0.25);
                artTriggerChannel.SelectedIndex = 1; artEdge.SelectedIndex = 1;
                Task38Profile triggerDraft = ReadProfile();
                if (triggerDraft.InductorDcrOhm != 5.25 || triggerDraft.Hardware.PicoExternalTriggerThresholdV != -.25 ||
                    triggerDraft.Hardware.ArtFiniteTriggerTerminal != "/Dev1/PFI1" || !triggerDraft.Hardware.ArtTriggerFalling || N("captureTimeout") != 3)
                    throw new Exception("Trigger controls did not transfer settings");
                V("picoThreshold", 1.5); artTriggerChannel.SelectedIndex = 0; artEdge.SelectedIndex = 0;
                SelectWorkspace(0); RefreshView();
                if (!calStart.Enabled || start.Visible || connect.Visible) throw new Exception("Calibration workspace exposes HV actions");
                calMode.SelectedIndex = 0;
                calCharge.SelectedIndex = 11; calRange.SelectedIndex = 4; calFrequency.Value = 100;
                calResolution.SelectedIndex = 1;
                pdResolution.SelectedIndex = 1;
                if (ReadProfile().Hardware.PicoResolutionBits != 15) throw new Exception("15bit did not persist in profile");
                int calibrationEnableTransitions = 0;
                calParameters.EnabledChanged += (s, e) => calibrationEnableTransitions++;
                for (int i = 0; i < 20; i++) RefreshView();
                if (calibrationEnableTransitions != 0) throw new Exception("Idle refresh toggled calibration controls");
                await RunCalibrationDemo();
                if (calibrationLongSampleCount != 7500000) throw new Exception("Pulse mode omitted60ms overview");
                if (Double.IsNaN(calibrationCandidate) || !calApply.Enabled || session != null) throw new Exception("Calibration demo did not complete independently");
                if (Math.Abs(calibrationQuality.IncidentPeakV - .150) > .005 || Math.Abs(calibrationCandidate - 666.667) > 25)
                    throw new Exception("Incident peak or automatic calibration coefficient is incorrect");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-calibration-pulse.png")); }
                double oldPd = N("pdcal"); ApplyDemoCalibration();
                if (N("pdcal") == oldPd) throw new Exception("Demo calibration result was not applied");
                V("pdcal", oldPd);
                calMode.SelectedIndex = 1;
                if (calApply.Enabled) throw new Exception("Changed calibration parameters retained a valid result");
                await RunCalibrationDemo();
                if (Double.IsNaN(calibrationCandidate)) throw new Exception("Fixed-block calibration simulation failed");
                if (calibrationLongSampleCount != 7500000) throw new Exception("Segment mode omitted60ms overview");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-calibration-block.png")); }
                calFrequency.Value = 25; await RunCalibrationDemo();
                if (!Double.IsNaN(calibrationCandidate)) throw new Exception("Insufficient calibration pulse count was accepted");
                if (calRaw.Count == 0 || calibrationLongSampleCount != 7500000) throw new Exception("Failed calibration lost noise overview");
                calFrequency.Value = 100;
                calRange.SelectedIndex = 0; await RunCalibrationDemo();
                if (!Double.IsNaN(calibrationCandidate) || calApply.Enabled) throw new Exception("Clipped calibration was allowed to apply");
                calRange.SelectedIndex = 6;
                calibrationDemoAmplitudeFactor = 3.2; await RunCalibrationDemo();
                if (!calibrationQuality.CircuitSuspected || calApply.Enabled || !Double.IsNaN(calibrationCandidate) || N("pdcal") != oldPd)
                    throw new Exception("Threefold circuit deviation was not blocked or changed previous coefficient");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-calibration-abnormal.png")); }
                calibrationDemoAmplitudeFactor = 1;
                await RunCalibrationDemo();
                calCharge.SelectedIndex = 10; await RunCalibrationDemo();
                if (!calibrationQuality.SaveRecommended || Math.Abs(calibrationQuality.CrossLevelDeviationFactor - 1) > .1)
                    throw new Exception("Charge-normalized cross-level comparison failed");
                calClearReference.PerformClick();
                if (calibrationLevels.Count != 0 || calApply.Enabled) throw new Exception("Clearing references retained a candidate");
                calCharge.SelectedIndex = 11; calRange.SelectedIndex = 4;
                Task pendingCalibration = RunCalibrationDemo();
                bool connectBlocked = false;
                try { Connect(); } catch (InvalidOperationException) { connectBlocked = true; }
                if (!connectBlocked || mode.Enabled) throw new Exception("Calibration/HV mutual exclusion failed");
                calibrationCancel = true; await pendingCalibration;
                if (!Double.IsNaN(calibrationCandidate)) throw new Exception("Cancelled calibration retained a result");
                calCharge.SelectedIndex = 11; SaveCalibrationDraft(); calCharge.SelectedIndex = 0; LoadCalibrationDraft();
                if (calCharge.SelectedIndex != 11) throw new Exception("Calibration draft persistence failed");
                if (calResolution.SelectedIndex != 1 || calLength.Value != 100 || calPrePeak.Value != 400) throw new Exception("Window and resolution settings did not persist");
                SelectWorkspace(2); settings.SelectedIndex = 1;
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-trigger-settings.png")); }
                RefreshView(); settings.SelectedIndex = 0;
                if (!cleanupArt.Enabled) throw new Exception("Device cleanup unavailable before connection");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-device-tools.png")); }
                SelectWorkspace(1);
                Connect();
                await WaitUi(() => session.GetConnectionStatus().Phase == ConnectionPhase.Ready, 4000);
                RefreshView(); if (inspectConnection.Enabled) throw new Exception("Inspection allowed during a device session");
                if (session.GetConnectionStatus().ArtSerial != "SIM-USB3135" || session.GetConnectionStatus().PicoSerial != "SIM-5242D")
                    throw new Exception("Connected serial identity was not exposed");
                RefreshView(); start.PerformClick();
                RefreshView();
                SelectWorkspace(0); RefreshView();
                if (calStart.Enabled || calParameters.Enabled || !stop.Visible) throw new Exception("HV session did not interlock calibration or hid stop");
                SelectWorkspace(1);
                if (mode.Enabled) throw new Exception("Mode switch allowed during ramp/capture");
                if (cleanupArt.Enabled) throw new Exception("Device cleanup allowed during a device session");
                await WaitUi(() => processed != null && !analyzing, 15000);
                if (!processed.Success || !processed.Task6AssessmentApplied || profile.Hardware.PicoRangeIndex >= 10 || synchronized.DischargeCount == 0 || synchronized.VoltageCount != processed.UsbDividerRawVolts.Length ||
                    synchronized.FirstDischargeMilliseconds + .00001 < synchronized.FirstZeroMilliseconds || Math.Abs(synchronized.TimeMaximumMilliseconds - 60) > .01)
                    throw new Exception("UI did not complete capture and formal automatic range assessment: " + analysis.Text);
                if (activeRecord == null || activeRecord.Status != "Completed" || processed.RecordId != activeRecord.RecordId ||
                    !File.Exists(Path.Combine(activeRecordDirectory, activeRecord.RecordId + ".xml"))) throw new Exception("Run identity was not bound and persisted");
                RefreshView();
                await Task.Delay(150);
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-capture.png")); }
                SelectWorkspace(2); settings.SelectedIndex = 0;
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-calibration.png")); }
                SelectWorkspace(1);
                RefreshView(); start.PerformClick();
                await WaitUi(() => session.Controller.GetStatus().AoCommandVolts > .05, 3000);
                demo.Connected = false;
                await WaitUi(() => reconnectForm != null && !reconnectForm.IsDisposed, 1500);
                using (Bitmap b = new Bitmap(reconnectForm.Width, reconnectForm.Height)) { reconnectForm.DrawToBitmap(b, new Rectangle(Point.Empty, reconnectForm.Size)); b.Save(Path.Combine(outputDirectory, "ui-reconnecting.png")); }
                demo.Connected = true;
                await WaitUi(() => session.GetConnectionStatus().CanContinue, 6000);
                reconnectForm.UpdateSnapshot(session.GetConnectionStatus());
                using (Bitmap b = new Bitmap(reconnectForm.Width, reconnectForm.Height)) { reconnectForm.DrawToBitmap(b, new Rectangle(Point.Empty, reconnectForm.Size)); b.Save(Path.Combine(outputDirectory, "ui-reconnected.png")); }
                reconnectForm.ConfirmForTest();
                if (demo.EnergizeCount != 2 || session.GetConnectionStatus().Phase != ConnectionPhase.Ready)
                    throw new Exception("Reconnect confirmation altered HV output or did not restore idle UI");
                await WaitUi(() => !analyzing, 5000);
                RefreshView();
                if (!mode.Enabled) throw new Exception("Idle mode switch is still locked");
                V("target", 11); V("pdcal", 1250);
                Task38Session previous = session;
                mode.SelectedIndex = 1;
                if (mode.SelectedIndex != 0 || !changingMode) throw new Exception("Mode changed before releasing old session");
                await WaitUi(() => session == null && activeMode == 1 && !changingMode, 3000);
                if (!previous.ReleaseFinished || previous.ReleaseError.Length != 0 || demo != null || !mode.Enabled || !cleanupArt.Enabled)
                    throw new Exception("Switch did not release old connection or unexpectedly connected hardware");
                SelectWorkspace(0); RefreshView();
                if (calStart.Enabled || calApply.Enabled || session != null) throw new Exception("Real calibration was incorrectly enabled");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-calibration-hardware-gate.png")); }
                SelectWorkspace(1);
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-mode-switch.png")); }
                mode.SelectedIndex = 0;
                if (N("target") != 11 || N("pdcal") != 1250 || session != null) throw new Exception("Simulation parameters lost or mode auto-connected");
                Connect();
                await WaitUi(() => session.GetConnectionStatus().Phase == ConnectionPhase.Ready, 4000);
                RefreshView(); exit.PerformClick();
                await WaitUi(() => session == null, 3000);
                if (!mode.Enabled || !connect.Enabled || !cleanupArt.Enabled) throw new Exception("Exit did not restore reusable disconnected UI");
                V("target", 0); Connect();
                await WaitUi(() => session.GetConnectionStatus().Phase == ConnectionPhase.Ready, 4000);
                RefreshView(); start.PerformClick();
                await WaitUi(() => processed != null && processed.IsBackgroundCapture && !analyzing, 10000);
                if (!processed.Success || processed.Task6AssessmentApplied || demo.EnergizeCount != 0 ||
                    demo.MaximumAoCommandVolts != 0 || synchronized.DischargeCount == 0 || !Double.IsNaN(synchronized.FirstZeroMilliseconds))
                    throw new Exception("Zero-voltage UI incorrectly energized, cropped or assessed background");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-background.png")); }
                RefreshView(); exit.PerformClick();
                await WaitUi(() => session == null, 3000);
                Size normalSize = Size; Size = MinimumSize;
                foreach (int page in new[] { 0, 1, 2 })
                {
                    SelectWorkspace(page); PerformLayout(); await Task.Delay(80);
                    using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-minimum-" + page + ".png")); }
                }
                Size = normalSize;
                verificationState.SelectedIndex = 1; SelectWorkspace(1); Connect();
                await WaitUi(() => session.GetConnectionStatus().Phase == ConnectionPhase.Ready, 4000);
                demo.ResidualVoltageV = -2747; demo.SafetyReady = false;
                await Task.Delay(150); RefreshView();
                if (start.Enabled || verificationState.Enabled || session.GetConnectionStatus().DischargeVerified ||
                    !metrics.Text.Contains("原始输入") || session.StartCycle()) throw new Exception("Control-box UI exposed equipment operation");
                using (Bitmap b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(Point.Empty, Size)); b.Save(Path.Combine(outputDirectory, "ui-control-box.png")); }
                exit.PerformClick(); await WaitUi(() => session == null, 3000);
                verificationState.SelectedIndex = 0;
                File.WriteAllText(Path.Combine(outputDirectory, "ui-smoke-result.txt"), "TASK38_UI_SMOKE=PASS\r\nThree independent navigation pages, calibration demo in both modes, clipping rejection, cancellation, draft persistence, simulation-only coefficient application, real calibration gate, HV exclusion, charts, Task6 range, reconnect and mode switching verified.");
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outputDirectory, "ui-smoke-result.txt"), "TASK38_UI_SMOKE=FAIL\r\n" + ex);
                Environment.ExitCode = 1;
            }
            finally { if (session != null) session.ExitTest(); timer.Stop(); preview = true; Close(); }
        }
        private static async Task WaitUi(Func<bool> f, int milliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!f()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("UI verification timeout"); await Task.Delay(40); }
        }
    }

    internal sealed class WaveChart : Control
    {
        private readonly List<PointF> samples = new List<PointF>();
        private readonly string title, xCaption, yCaption;
        private readonly Color trace;
        public DateTime StartedUtc;
        public double ZeroMarkerMilliseconds = Double.NaN;
        public int Count { get { return samples.Count; } }
        internal WaveChart(string heading, string x, string y, Color color)
        { title = heading; xCaption = x; yCaption = y; trace = color; Dock = DockStyle.Fill; BackColor = Color.White; Margin = new Padding(0, 0, 0, 10); DoubleBuffered = true; }
        public void Clear() { samples.Clear(); ZeroMarkerMilliseconds = Double.NaN; Invalidate(); }
        public void Append(double x, double y) { if (samples.Count > 100000) samples.RemoveRange(0, 10000); samples.Add(new PointF((float)x, (float)y)); Invalidate(); }
        public void SetWave(double[] values, double samplesPerXUnit, double scale)
        {
            samples.Clear(); ZeroMarkerMilliseconds = Double.NaN;
            for (int i = 0; i < values.Length; i++) samples.Add(new PointF((float)(i / samplesPerXUnit), (float)(values[i] * scale)));
            Invalidate();
        }
        public void SetAdcWave(short[] values, double sampleRate, double scale, double firstZeroSeconds, double offsetSeconds)
        {
            Clear();
            if (Double.IsNaN(firstZeroSeconds) || Double.IsInfinity(firstZeroSeconds) || firstZeroSeconds < 0 || sampleRate <= 0) return;
            int first = (int)Math.Min(values.Length, Math.Max(0, Math.Ceiling((firstZeroSeconds - offsetSeconds) * sampleRate)));
            int bucket = Math.Max(1, (values.Length - first + 4095) / 4096);
            // Preserve narrow positive and negative peaks without storing millions of UI points.
            for (int i = first; i < values.Length; i += bucket)
            {
                int low = i, high = i, end = Math.Min(values.Length, i + bucket);
                for (int j = i + 1; j < end; j++)
                { if (values[j] < values[low]) low = j; if (values[j] > values[high]) high = j; }
                int a = Math.Min(low, high), b = Math.Max(low, high);
                samples.Add(new PointF((float)((a / sampleRate + offsetSeconds) * 1000), (float)(values[a] * scale)));
                if (a != b) samples.Add(new PointF((float)((b / sampleRate + offsetSeconds) * 1000), (float)(values[b] * scale)));
            }
            ZeroMarkerMilliseconds = firstZeroSeconds * 1000;
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font f = new Font("Microsoft YaHei UI", 12, FontStyle.Bold)) g.DrawString(title, f, Brushes.Black, 18, 12);
            RectangleF plot = new RectangleF(65, 48, Math.Max(10, Width - 95), Math.Max(10, Height - 93));
            float maxX = 1, minY = 0, maxY = 1;
            foreach (PointF p in samples) { maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
            float pad = (maxY - minY) * .12f; minY -= pad; maxY += pad;
            using (Pen grid = new Pen(Color.FromArgb(229, 234, 240)))
            using (Font tick = new Font("Microsoft YaHei UI", 8))
            {
                int tickCount = Math.Max(1, Math.Min(5, (int)(plot.Height / 24)));
                for (int i = 0; i <= tickCount; i++)
                {
                    float x = plot.Left + plot.Width * i / tickCount, y = plot.Top + plot.Height * i / tickCount;
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom); g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    g.DrawString((maxX * i / tickCount).ToString("0.##"), tick, Brushes.Gray, x - 10, plot.Bottom + 6);
                    g.DrawString((maxY - (maxY - minY) * i / tickCount).ToString("0.##"), tick, Brushes.Gray, 5, y - 7);
                }
                g.DrawString(xCaption, tick, Brushes.Gray, plot.Right - 120, Height - 18);
                g.DrawString(yCaption, tick, Brushes.Gray, 15, 32);
            }
            if (samples.Count > 1)
            {
                // Draw per-pixel min/max envelopes so short waveform extrema remain visible.
                using (Pen pen = new Pen(trace, 1.4f))
                {
                    int bucket = Math.Max(1, samples.Count / Math.Max(1, (int)plot.Width));
                    PointF? previous = null;
                    for (int i = 0; i < samples.Count; i += bucket)
                    {
                        float lo = samples[i].Y, hi = lo;
                        for (int j = i + 1; j < Math.Min(samples.Count, i + bucket); j++) { lo = Math.Min(lo, samples[j].Y); hi = Math.Max(hi, samples[j].Y); }
                        float x = plot.Left + samples[i].X / maxX * plot.Width;
                        PointF a = new PointF(x, plot.Bottom - (hi - minY) / (maxY - minY) * plot.Height);
                        PointF b = new PointF(x, plot.Bottom - (lo - minY) / (maxY - minY) * plot.Height);
                        if (previous.HasValue) g.DrawLine(pen, previous.Value, a); g.DrawLine(pen, a, b); previous = b;
                    }
                }
                if (!Double.IsNaN(ZeroMarkerMilliseconds) && ZeroMarkerMilliseconds >= 0 && ZeroMarkerMilliseconds <= maxX)
                {
                    float x = plot.Left + (float)ZeroMarkerMilliseconds / maxX * plot.Width;
                    using (Pen marker = new Pen(Color.DarkOrange, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLine(marker, x, plot.Top, x, plot.Bottom);
                    g.DrawString("首零相位", Font, Brushes.DarkOrange, x + 5, plot.Top + 4);
                }
            }
            else g.DrawString("等待本轮数据", Font, Brushes.SlateGray, plot.Left + 20, plot.Top + 25);
        }
    }

    internal sealed class ReadableComboBox : ComboBox
    {
        public ReadableComboBox() { DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 25; }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Native combo boxes omit their selected text in WM_PRINT screenshots.
            if ((m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero)
                using (Graphics g = Graphics.FromHdc(m.WParam))
                {
                    Rectangle bounds = new Rectangle(3, 2, Math.Max(1, Width - 25), Height - 4);
                    using (Brush b = new SolidBrush(BackColor)) g.FillRectangle(b, bounds);
                    TextRenderer.DrawText(g, GetItemText(SelectedItem), Font, bounds, Enabled ? ForeColor : Color.DimGray,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            e.DrawBackground();
            string value = e.Index >= 0 ? GetItemText(Items[e.Index]) : Text;
            TextRenderer.DrawText(e.Graphics, value, Font, e.Bounds, Enabled ? ForeColor : Color.DimGray,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }
    }

    internal sealed class ConnectionProgress : Control
    {
        private int value;
        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        public ConnectionProgress() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(230, 237, 240));
            using (Brush fill = new SolidBrush(Color.FromArgb(101, 195, 181)))
                e.Graphics.FillRectangle(fill, 0, 0, Width * value / 100, Height);
            TextRenderer.DrawText(e.Graphics, value + "%", Font, ClientRectangle, Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    internal sealed class ReconnectForm : BrandedForm
    {
        private readonly Task38Session session;
        private readonly Label warning = new Label { Dock = DockStyle.Top, Height = 55, Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold), ForeColor = Color.Firebrick, TextAlign = ContentAlignment.MiddleCenter, Text = "未确认放电，禁止靠近设备！" };
        private readonly Label detail = new Label { Dock = DockStyle.Top, Height = 240, Padding = new Padding(12), TextAlign = ContentAlignment.MiddleLeft };
        private readonly ConnectionProgress progress = new ConnectionProgress { Dock = DockStyle.Top, Height = 25 };
        private readonly Button retry = new Button { Text = "重试连接", AutoSize = true, Height = 36 };
        private readonly Button resume = new Button { Text = "确认继续当前操作", AutoSize = true, Height = 36 };
        private readonly Button leave = new Button { Text = "退出加压测试", AutoSize = true, Height = 36 };
        private bool canClose;
        public ReconnectForm(Task38Session owner)
        {
            session = owner; Text = "任务 38 · 设备重新连接"; Size = new Size(760, 460);
            Font = new Font("Microsoft YaHei UI", 10); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ControlBox = false; StartPosition = FormStartPosition.CenterParent;
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(10) };
            buttons.Controls.AddRange(new Control[] { retry, resume, leave });
            Controls.Add(progress); Controls.Add(detail); Controls.Add(warning); Controls.Add(buttons);
            retry.Click += (s, e) => session.RetryConnection();
            resume.Click += (s, e) => { if (session.ConfirmContinue()) CloseFromOwner(); };
            leave.Click += (s, e) => { session.ExitTest(); CloseFromOwner(); };
            FormClosing += (s, e) => { if (!canClose) e.Cancel = true; };
        }
        public void UpdateSnapshot(ConnectionSnapshot s)
        {
            Text = s.Interrupted == null ? "任务 38 · 设备连接与初始化" : "任务 38 · 设备重新连接";
            progress.Value = s.ProgressPercent;
            retry.Enabled = s.CanRetry; resume.Enabled = s.CanContinue;
            warning.Text = s.ControlBoxVerification ? "控制箱校验 · 禁止加压 · 不证明已放电" : s.DischargeVerified ? "连接成功，残压与联锁检查通过" : "未确认放电，禁止靠近设备！";
            warning.ForeColor = s.DischargeVerified ? Color.Teal : Color.Firebrick;
            string saved = s.Interrupted == null ? String.Empty : String.Format("\n已记忆：目标 {0:F3} kV，最后实测 {1:F3} kV，控制电压 {2:F3} V\n中断阶段：{3}；记录不是当前残压。", s.Interrupted.Configuration.TargetVoltageV / 1000, s.Interrupted.LastMeasuredVoltageV / 1000, s.Interrupted.LastAoCommandVolts, DeviceMessages.State(s.Interrupted.InterruptedState));
            detail.Text = DeviceMessages.Describe(s.Message) + String.Format("\n连接计时 {0:F1} / 10.0 秒", s.ElapsedSeconds) + saved +
                (String.IsNullOrEmpty(s.LastError) ? String.Empty : "\n" + OperatorDiagnostics.Report(s.LastError)) +
                (s.Phase == ConnectionPhase.TimedOut && s.WorkerBusy ? "\n驱动调用尚未返回，重试暂不可用；可以退出测试。" : String.Empty);
        }
        public void CloseFromOwner() { canClose = true; Close(); }
        internal void ConfirmForTest() { resume.PerformClick(); }
    }
}
