using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.ZeroBench
{
    internal sealed partial class MainForm : Form
    {
        private readonly Color teal = Color.FromArgb(0, 125, 116);
        private readonly ComboBox mode = Choice("仿真顺序验证", "实机 0 V · P0.7 共享触发", "实机外部触发 · 只读诊断");
        private readonly TextBox artDevice = new TextBox { Text = "Dev1" }, artSerial = new TextBox(), picoSerial = new TextBox();
        private readonly ComboBox artChoice = Choice("等待扫描 ART"), picoChoice = Choice("等待扫描 Pico");
        private readonly Button discover = new Button { Text = "自动获取两卡", Width = 125, Height = 36 };
        private bool discovering, applyingDiscovery;
        private CancellationTokenSource discoveryCancellation;
        private DiscoveryResult detectedDevices;
        private readonly TextBox artLibrary = new TextBox { Text = @"C:\Program Files (x86)\ART Technology\ART-DAQ\Lib\x64" }, picoLibrary = new TextBox { Text = "auto" };
        private readonly ComboBox terminal = Choice("参考单端 RSE · AGND", "非参考单端 NRSE · AISE", "差分 · AI4 / AI12"), trigger = Choice("PFI0", "PFI1", "PFI2", "PFI3"), edge = Choice("上升沿", "下降沿");
        private readonly ComboBox range = Choice("±10 mV", "±20 mV", "±50 mV", "±100 mV", "±200 mV", "±500 mV", "±1 V", "±2 V", "±5 V", "±10 V", "±20 V");
        private readonly Dictionary<string, NumericUpDown> numbers = new Dictionary<string, NumericUpDown>();
        private readonly CheckBox autoRange = new CheckBox { Text = "测后弹窗确认量程", Checked = true, AutoSize = true };
        private readonly ComboBox resolution = Choice("8 bit", "12 bit", "14 bit", "15 bit", "16 bit"), coupling = Choice("DC", "AC");
        private readonly CheckBox channelA = new CheckBox { Text = "启用 A 通道", Checked = true, AutoSize = true };
        private readonly CheckBox bandwidth = new CheckBox { Text = "20 MHz 硬件带宽限制", AutoSize = true };
        private readonly CheckBox circuit = new CheckBox { Text = "已接入所设 L/R 的振荡回路", AutoSize = true };
        private readonly CheckBox simOscillation = new CheckBox { Text = "仿真加入衰减振荡，测试指标计算", AutoSize = true };
        private readonly Label notice = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(151, 78, 18), AutoSize = false };
        private readonly Label result = new Label { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(10), BackColor = Color.White };
        private readonly TextBox rangeDiagnosticText = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Text = "等待本轮定位原始数据。" };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DarkSlateGray };
        private readonly Button start = new Button { Text = "开始本轮", Width = 120, Height = 36 }, stop = new Button { Text = "停止采集", Width = 115, Height = 36, Enabled = false };
        private readonly Button export = new Button { Text = "导出检波数据", Width = 125, Height = 36, Enabled = false };
        private readonly Button folder = new Button { Text = "打开记录目录", Width = 125, Height = 36 };
        private readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
        private readonly Dictionary<string, Label> lamps = new Dictionary<string, Label>();
        private readonly Dictionary<string, Label> conditions = new Dictionary<string, Label>();
        private readonly WavePanel voltage = new WavePanel { Title = "分压器电压 · 80 kS/s", Unit = "kV", MinimumSymmetricHalfRange = 1 };
        private readonly WavePanel discharge = new WavePanel { Title = "局放输入换算 · 完整 60 ms", Unit = "pC", TraceColor = Color.FromArgb(140, 63, 171) };
        private readonly WavePanel location = new WavePanel { Title = "0 V 定位数据窗口 · 触发后 0～20 ms", Unit = "pC", TraceColor = Color.FromArgb(140, 63, 171) };
        private Control settingsPanel;
        private TabControl viewTabs;
        private CancellationTokenSource cancellation;
        private BenchCapture latest;
        private bool closeAfterStop;
        private bool exportInProgress, closeAfterExport, applyingAutomaticRange, smokeMode;
        private int runNumber;
        private string rangeContext;
        private BenchSettings activeSettings;
        private string LastSettingsPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last-settings.xml"); } }

        internal MainForm()
        {
            Text = "雄新电力科技 · 量程弹窗确认 / 0 V 同步采集";
            Font = new Font("Microsoft YaHei UI", 9); BackColor = Color.FromArgb(239, 244, 247);
            Size = new Size(1460, 1020); MinimumSize = new Size(1220, 820); StartPosition = FormStartPosition.CenterScreen;
            using (var stream = GetType().Assembly.GetManifestResourceStream("ZeroBench.Brand.ico")) if (stream != null) Icon = new Icon(stream);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(14) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); Controls.Add(root);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 275));
            heading.Controls.Add(new Label { Text = "0 V 顺序验证与 ART / Pico 同步采集", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            using (var stream = GetType().Assembly.GetManifestResourceStream("ZeroBench.Brand.Logo"))
                if (stream != null) heading.Controls.Add(new PictureBox { Image = new Bitmap(stream), Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom }, 1, 0);
            root.Controls.Add(heading, 0, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            mode.Width = 240; mode.Height = 36; toolbar.Controls.Add(mode);
            start.BackColor = teal; start.ForeColor = Color.White; stop.BackColor = Color.FromArgb(177, 48, 48); stop.ForeColor = Color.White;
            toolbar.Controls.Add(discover); toolbar.Controls.Add(start); toolbar.Controls.Add(stop); toolbar.Controls.Add(export); toolbar.Controls.Add(folder);
            var reset = new Button { Text = "视图复位", Width = 110, Height = 36 }; toolbar.Controls.Add(reset);
            reset.Click += (s, e) => { voltage.ResetView(); discharge.ResetView(); location.ResetView(); };
            root.Controls.Add(toolbar, 0, 1);
            root.Controls.Add(BuildStageLamps(), 0, 2);
            var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 385)); middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settingsPanel = BuildSettings(); middle.Controls.Add(settingsPanel, 0, 0);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 158));
            right.Controls.Add(notice, 0, 0);
            viewTabs = new TabControl { Dock = DockStyle.Fill };
            var waves = new TabPage("同步波形 · 60 ms") { AutoScroll = true, AutoScrollMinSize = new Size(0, 340) }; var window = new TabPage("定位数据 · 20 ms") { AutoScroll = true, AutoScrollMinSize = new Size(0, 220) }; var diagnostics = new TabPage("HV UP 结束条件与状态");
            var pair = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, MinimumSize = new Size(0, 340) };
            location.MinimumSize = new Size(0, 220);
            pair.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); pair.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); pair.Controls.Add(voltage, 0, 0); pair.Controls.Add(discharge, 0, 1);
            waves.Controls.Add(pair); window.Controls.Add(location); diagnostics.Controls.Add(BuildConditions());
            viewTabs.TabPages.Add(waves); viewTabs.TabPages.Add(window); viewTabs.TabPages.Add(diagnostics); right.Controls.Add(viewTabs, 0, 1); right.Controls.Add(result, 0, 2);
            var rangePage = new TabPage("定位量程诊断"); rangePage.Controls.Add(rangeDiagnosticText); viewTabs.TabPages.Add(rangePage);
            middle.Controls.Add(right, 1, 0); root.Controls.Add(middle, 0, 3); root.Controls.Add(log, 0, 4); root.Controls.Add(status, 0, 5);
            start.Click += async (s, e) => await StartRun(); stop.Click += (s, e) => { if (cancellation != null) { cancellation.Cancel(); status.Text = "正在停止采集并释放本工具的设备任务……"; } };
            discover.Click += async (s, e) => await DiscoverCards();
            artChoice.SelectedIndexChanged += (s, e) => ApplyArtChoice();
            picoChoice.SelectedIndexChanged += (s, e) => ApplyPicoChoice();
            export.Click += async (s, e) => await ExportData(); folder.Click += (s, e) => { string path = latest != null ? latest.DirectoryPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records"); Directory.CreateDirectory(path); Process.Start("explorer.exe", "\"" + path + "\""); };
            mode.SelectedIndexChanged += (s, e) => RefreshMode(); simOscillation.CheckedChanged += (s, e) => RefreshMode();
            range.SelectedIndexChanged += (s, e) => { if (!applyingAutomaticRange) rangeState.Reset(); };
            FormClosing += (s, e) =>
            {
                if (discovering) { e.Cancel = true; closeAfterStop = true; if (discoveryCancellation != null) discoveryCancellation.Cancel(); return; }
                if (exportInProgress) { e.Cancel = true; closeAfterExport = true; status.Text = "正在完成本轮 CSV 导出，完成后自动关闭……"; return; }
                if (cancellation != null) { e.Cancel = true; closeAfterStop = true; cancellation.Cancel(); }
            };
            LoadLastSettings(); RefreshMode(); status.Text = "就绪。将自动枚举两卡；多台设备请按序列号选择，扫描不会初始化采集或输出。";
            ResetConditions();
            Shown += async (s, e) =>
            {
                var arguments = Environment.GetCommandLineArgs();
                if (Array.IndexOf(arguments, "--ui-smoke") < 0 && Array.IndexOf(arguments, "--ui-discovery-check") < 0 && Array.IndexOf(arguments, "--ui-record-check") < 0 && Array.IndexOf(arguments, "--ui-range-review-test") < 0)
                    await DiscoverCards();
            };
        }

        private Control BuildSettings()
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
            var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(10) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            artDevice.ReadOnly = artSerial.ReadOnly = picoSerial.ReadOnly = true;
            artChoice.DropDownWidth = 360; picoChoice.DropDownWidth = 250;
            Add(table, "ART 设备选择", artChoice); Add(table, "ART 模块名", artDevice); Add(table, "ART 锁定序列号", artSerial);
            Add(table, "Pico 设备选择", picoChoice); Add(table, "Pico 锁定序列号", picoSerial);
            Add(table, "设备获取", new Label { Text = "启动自动读取；唯一设备自动填入。\n多台时按序列号选择。\n原卡缺失不会自动换卡。", AutoSize = true });
            Add(table, "AI4 输入配置", terminal); Add(table, "ART 数字触发", trigger); Add(table, "两卡触发边沿", edge);
            Num(table, "ext", "Pico EXT 阈值 / V", 1.5M, -4.9M, 4.9M, 2);
            Num(table, "trgpulse", "TRG 脉冲请求 / ms", 20, 1, 1000, 0);
            Num(table, "picorate", "Pico 请求 / MS/s", 125, .08M, 1000, 3);
            Add(table, "Pico 分辨率模式", resolution); resolution.SelectedIndex = 2;
            Num(table, "duration", "两卡采集长度 / ms", 60, 1, 1000, 3);
            Add(table, "Pico A 通道", channelA); Add(table, "A 耦合", coupling); Add(table, "A 带宽", bandwidth);
            Add(table, "A 下一轮量程", range); range.SelectedIndex = 10;
            Num(table, "pulse", "校准脉冲 / pC", 100, .001M, 1000000, 3);
            var guess = new Button { Text = "按脉冲估算起始量程", AutoSize = true }; Add(table, "", guess);
            guess.Click += (s, e) => { range.SelectedIndex = SignalAnalysis.InitialRangeFromPulse(N("pulse"), N("pdcal")); rangeState.Reset(); status.Text = "起始量程依据输入电量/校准系数估算并预留50%余量，实际波形将再次校验削顶与过冲。"; };
            Add(table, "", autoRange);
            var restoreRangePrompts = new Button { Text = "恢复量程弹窗", AutoSize = true }; Add(table, "", restoreRangePrompts);
            restoreRangePrompts.Click += (s, e) => { silentRangeSession = false; status.Text = "已恢复每次量程问题的弹窗确认；没有启动采集或输出。"; };
            var rangeInfo = new Button { Text = "查看定位量程诊断", AutoSize = true }; Add(table, "", rangeInfo);
            rangeInfo.Click += (s, e) => viewTabs.SelectedIndex = 3;
            Num(table, "pdcal", "局放系数 / pC每V", 2100, .001M, 10000000, 3);
            Num(table, "divider", "分压比 / V每V", 11000, .001M, 10000000, 3);
            Num(table, "vscale", "电压图最小 ±kV", 1, .1M, 1000, 1);
            numbers["vscale"].ValueChanged += (s, e) => voltage.MinimumSymmetricHalfRange = N("vscale");
            Num(table, "inductance", "回路电感 / H", .7M, .000001M, 1000, 6);
            Num(table, "resistance", "回路电阻 / Ω", 22, 0, 100000, 3);
            Add(table, "", circuit); Add(table, "", simOscillation);
            Num(table, "skip", "过冲排除 / ms", .5M, 0, 19, 3);
            Num(table, "hvdelay", "HV ON 延时 / ms", 50, 50, 5000, 0);
            Num(table, "armdelay", "布置后延时 / ms", 200, 200, 5000, 0);
            Num(table, "timeout", "触发超时 / ms", 5000, 100, 60000, 0);
            Num(table, "loop", "循环请求间隔 / ms", 1, 1, 1000, 0);
            Num(table, "step", "AO 请求步长 / V", .01M, .0001M, .1M, 4);
            Add(table, "数据定义", new Label { Text = "ART / 检波输出：80 kS/s\n定位：前 min(20ms,采集长度) 原始数据\n量程按定位窗口评估，窗外过冲单列\nA 偏置固定 0V；目标固定 0V，不执行 AO", AutoSize = true });
            Add(table, "ART x64 运行库", artLibrary); Add(table, "Pico 运行库目录", picoLibrary);
            Add(table, "接线说明", new Label { Text = "AI0、AI2 未接：不参与工程量判断。\nAI4→AGND 本底输入。\nP0.7（4脚）TRG → PFI0（21脚）\n及 Pico EXT；仅低压无储能台架。", AutoSize = true });
            scroll.Controls.Add(table); return scroll;
        }
        private Control BuildConditions()
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(15), AutoScroll = true };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            string[] keys = { "zero", "ao", "hv", "feedback", "safety", "art", "pico", "trigger", "complete", "timing" };
            string[] captions = { "HV UP 结束条件：设定电压=0", "AO 输出", "HV ON / HV OFF", "IMON / VMON", "P0.4 数字输入", "ART 采集布置", "Pico 采集布置", "共享物理触发", "完整数据长度", "循环延时性质" };
            string[] values = { "● 成立：立即结束 HV UP，随后继续采集步骤", "仿真0 V；实机只读模式未写输出", "仿真演示；实机只读模式不执行", "AI0、AI2 悬空，不读作有效电压/电流", "等待读取，仅显示物理电平", "未布置", "未布置", "等待；软件命令不等于已收到触发", "ART 4,800 点；Pico 7,500,000 点", "1 ms 为请求间隔，Windows 软件调度不保证实时精度" };
            for (int i = 0; i < keys.Length; i++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); table.Controls.Add(new Label { Text = captions[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
                var label = new Label { Text = values[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = i == 0 ? teal : Color.DarkSlateGray };
                table.Controls.Add(label, 1, i); conditions[keys[i]] = label;
            }
            return table;
        }
        private static ComboBox Choice(params string[] items) { var c = new PrintedComboBox(); c.Items.AddRange(items); c.SelectedIndex = 0; return c; }

        // Owner drawing also gives DrawToBitmap a real selected value instead of an empty native edit rectangle.
        private sealed class PrintedComboBox : ComboBox
        {
            internal PrintedComboBox() { DropDownStyle = ComboBoxStyle.DropDownList; DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 24; }
            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                bool selected = Enabled && (e.State & DrawItemState.Selected) != 0;
                Color back = !Enabled ? SystemColors.Control : selected ? SystemColors.Highlight : BackColor;
                Color fore = !Enabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : ForeColor;
                using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
                string value = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
                Rectangle textBounds = e.Bounds; textBounds.Inflate(-3, 0);
                TextRenderer.DrawText(e.Graphics, value, Font, textBounds, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (Enabled && (e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                // Native ComboBox WM_PRINT/WM_PRINTCLIENT can omit selected text even for owner draw.
                if ((m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero && Width > 28 && Height > 4)
                {
                    using (Graphics graphics = Graphics.FromHdc(m.WParam))
                    {
                        Rectangle bounds = new Rectangle(3, 2, Width - 25, Height - 4);
                        using (var brush = new SolidBrush(Enabled ? BackColor : SystemColors.Control)) graphics.FillRectangle(brush, bounds);
                        TextRenderer.DrawText(graphics, SelectedItem == null ? Text : GetItemText(SelectedItem), Font, bounds,
                            Enabled ? ForeColor : SystemColors.GrayText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    }
                }
            }
        }
        private static void Add(TableLayoutPanel t, string title, Control c)
        {
            int row = t.RowCount++; t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 9, 3, 7) }, 0, row);
            c.Margin = new Padding(2, 4, 3, 5); c.Anchor = AnchorStyles.Left | AnchorStyles.Right; c.MinimumSize = new Size(170, 25); t.Controls.Add(c, 1, row);
        }
        private void Num(TableLayoutPanel t, string key, string caption, decimal value, decimal min, decimal max, int decimals)
        { var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, ThousandsSeparator = true, Value = value }; numbers.Add(key, n); Add(t, caption, n); }
        private double N(string key) { return (double)numbers[key].Value; }
        private BenchSettings Settings()
        {
            return new BenchSettings { RealHardware = mode.SelectedIndex != 0, AutomaticSharedTrigger = mode.SelectedIndex == 1, TriggerPulseMs = (int)N("trgpulse"), ArtDevice = artDevice.Text.Trim(), ArtSerial = artSerial.Text.Trim(), PicoSerial = picoSerial.Text.Trim(),
                ArtLibraryDirectory = artLibrary.Text.Trim(), PicoLibraryDirectory = picoLibrary.Text.Trim(), ArtTerminal = terminal.SelectedIndex == 0 ? 10083 : terminal.SelectedIndex == 1 ? 10078 : 10106,
                ArtTrigger = "/" + artDevice.Text.Trim() + "/PFI" + trigger.SelectedIndex, FallingTrigger = edge.SelectedIndex == 1, ExternalTriggerVolts = N("ext"), RangeIndex = range.SelectedIndex,
                PdCalibrationPcPerV = N("pdcal"), DividerVPerV = N("divider"), InductanceH = N("inductance"), ResistanceOhms = N("resistance"), TargetVoltageV = 0,
                AoStepV = N("step"), LoopDelayMs = (int)N("loop"), HvDelayMs = (int)N("hvdelay"), ArmDelayMs = (int)N("armdelay"), TriggerTimeoutMs = (int)N("timeout"),
                TransientExclusionMs = N("skip"), CalibrationPulsePc = N("pulse"), AutoRange = autoRange.Checked, SimulationOscillation = simOscillation.Checked,
                RequestedPicoRateHz = N("picorate") * 1e6, ResolutionBits = new[] { 8, 12, 14, 15, 16 }[resolution.SelectedIndex],
                CaptureDurationMs = N("duration"), PicoChannelAEnabled = channelA.Checked, PicoCoupling = coupling.SelectedIndex == 0 ? 1 : 0,
                PicoBandwidth20MHz = bandwidth.Checked, OscillationCircuitConnected = circuit.Checked, RunNumber = ++runNumber };
        }
        private void RefreshMode()
        {
            bool real = mode.SelectedIndex != 0, shared = mode.SelectedIndex == 1; simOscillation.Enabled = !real;
            if (shared) { trigger.SelectedIndex = 0; edge.SelectedIndex = 0; }
            trigger.Enabled = edge.Enabled = !shared; numbers["trgpulse"].Enabled = shared;
            notice.Text = shared ? "低压台架：仅写 P0.7 TRG，先低→两卡布置→延时→高→低；同线进入 PFI0 / Pico EXT。\n不操作 AO、HV 或其他输出。驱动写入成功≠物理电平已回读。点击步骤灯查看详情。"
                : real ? "实机只读：采集 AI4 + Pico A，读取 P0.4；不写 AO/DO。AI0、AI2 未接入。\nHV ON/HV UP 不执行。同步边沿需外部送到 ART PFI 和 Pico EXT。点击步骤灯查看详情。"
                : "仿真：演示 AO=0 → HV ON → HV UP(0 V 结束) → HV OFF → 两卡布置 → 触发 → 采集。\n所有波形与状态均为合成数据；勾选仿真振荡可检验 f/C/介损估算。";
        }
        private void LoadLastSettings()
        {
            // Restoring parameters must never opt into real hardware automatically.
            mode.SelectedIndex = 0;
            if (!File.Exists(LastSettingsPath)) return;
            try
            {
                var s = BenchSettings.Load(LastSettingsPath);
                if (s == null) throw new InvalidDataException("保存的设置为空。");
                s.RealHardware = false;
                s.Validate();
                var savedNumbers = new Dictionary<string, double>
                {
                    { "ext", s.ExternalTriggerVolts }, { "pulse", s.CalibrationPulsePc }, { "trgpulse", s.TriggerPulseMs },
                    { "pdcal", s.PdCalibrationPcPerV }, { "divider", s.DividerVPerV },
                    { "inductance", s.InductanceH }, { "resistance", s.ResistanceOhms },
                    { "skip", s.TransientExclusionMs }, { "hvdelay", s.HvDelayMs },
                    { "armdelay", s.ArmDelayMs }, { "timeout", s.TriggerTimeoutMs },
                    { "loop", s.LoopDelayMs }, { "step", s.AoStepV }
                    , { "picorate", s.RequestedPicoRateHz / 1e6 }, { "duration", s.CaptureDurationMs }
                };
                // Validate all UI ranges before assigning any fields: do not silently clamp
                // a trigger threshold/calibration value or leave a partially restored setup.
                var restoredNumbers = new Dictionary<string, decimal>();
                foreach (var item in savedNumbers)
                {
                    decimal value = (decimal)item.Value;
                    if (value < numbers[item.Key].Minimum || value > numbers[item.Key].Maximum)
                        throw new InvalidDataException("保存的参数超出当前界面允许范围：" + item.Key);
                    restoredNumbers.Add(item.Key, value);
                }
                int triggerIndex = Int32.Parse(s.ArtTrigger.Substring(("/" + s.ArtDevice + "/PFI").Length), CultureInfo.InvariantCulture);
                artDevice.Text = s.ArtDevice; artSerial.Text = s.ArtSerial; picoSerial.Text = s.PicoSerial;
                artLibrary.Text = s.ArtLibraryDirectory; picoLibrary.Text = s.PicoLibraryDirectory;
                range.SelectedIndex = s.RangeIndex; terminal.SelectedIndex = s.ArtTerminal == 10078 ? 1 : s.ArtTerminal == 10106 ? 2 : 0;
                trigger.SelectedIndex = triggerIndex; edge.SelectedIndex = s.FallingTrigger ? 1 : 0;
                foreach (var item in restoredNumbers) numbers[item.Key].Value = item.Value;
                autoRange.Checked = s.AutoRange; simOscillation.Checked = s.SimulationOscillation;
                circuit.Checked = s.OscillationCircuitConnected;
                resolution.SelectedIndex = Array.IndexOf(new[] { 8, 12, 14, 15, 16 }, s.ResolutionBits);
                channelA.Checked = s.PicoChannelAEnabled; coupling.SelectedIndex = s.PicoCoupling == 1 ? 0 : 1; bandwidth.Checked = s.PicoBandwidth20MHz;
            }
            catch (Exception ex) { log.AppendText("未恢复上次设置，保留默认参数及仿真模式：" + ex.Message + Environment.NewLine); }
        }
        private async Task StartRun()
        {
            if (cancellation != null || exportInProgress || discovering) return;
            if (mode.SelectedIndex == 1 && MessageBox.Show(this,
                "仅限高压源未上电、设备无储能的低压台架。\n将仅在指定 ART P0.7（DB37 第4脚）输出一次 TRG 脉冲，接 PFI0（21脚）与 Pico EXT。\n不操作 AO、HV 或其他数字线；本轮不验证加压安全。\n确认当前满足上述条件并开始？",
                "确认单次 TRG 输出", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (mode.SelectedIndex != 0)
            {
                if (!await DiscoverCards()) return;
                if (exportInProgress || cancellation != null || discovering || closeAfterStop || closeAfterExport || IsDisposed || Disposing) return;
                if (!SelectedCardsAvailable()) { status.Text = "请选择本次 ART 和 Pico；未找到原序列号时不会自动换用另一张卡。"; return; }
            }
            try { activeSettings = Settings(); activeSettings.Validate(); if (!smokeMode) activeSettings.Save(LastSettingsPath); }
            catch (Exception ex) { InvalidateRunConditions("参数校验未通过，本轮未启动"); status.Text = ex.Message; return; }
            string context = RangeHistoryContext(activeSettings);
            if (rangeContext != context) { rangeState.Reset(); rangeContext = context; }
            latest = null; analysis = null; locationAdc = null; export.Enabled = false; result.Text = "本轮尚未完成。"; rangeDiagnosticText.Text = "等待本轮定位原始数据。"; voltage.ClearWave(); discharge.ClearWave(); location.ClearWave();
            ResetConditions();
            ResetStageDiagnostics();
            settingsPanel.Enabled = mode.Enabled = start.Enabled = discover.Enabled = false; stop.Enabled = true;
            cancellation = new CancellationTokenSource();
            try
            {
                await RunWithRangeReviewAsync(activeSettings);
            }
            catch (OperationCanceledException) { InvalidateRunConditions("本轮已停止，未取得完整结果"); status.Text = "已停止；本轮不作为完整测量。"; }
            catch (Exception ex) { InvalidateRunConditions("本轮失败，状态未通过验证"); Progress("ERROR", ex.Message); result.Text = "本轮失败，不能据此判定设备或采集通过。\n" + ex.Message; }
            finally
            {
                cancellation.Dispose(); cancellation = null; settingsPanel.Enabled = mode.Enabled = start.Enabled = discover.Enabled = true; stop.Enabled = false; RefreshMode();
                if (closeAfterStop) Close();
            }
        }
        private void Progress(string key, string message)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Progress(key, message))); } catch (InvalidOperationException) { if (!IsDisposed && !Disposing) throw; } return; }
            ApplyStageDiagnostic(key, message);
            if (key == "SAFETY") conditions["safety"].Text = message;
            if (key == "AO_ZERO") conditions["ao"].Text = message;
            if (key == "HV_ON" || key == "HV_OFF") conditions["hv"].Text = message;
            if (key == "HV_UP") { conditions["zero"].Text = "● 设定为 0 V：本轮立即结束升压阶段"; conditions["zero"].ForeColor = teal; }
            if (key == "WAIT_TRIGGER") { conditions["art"].Text = "已布置 / 等待边沿"; conditions["pico"].Text = "已布置 / 等待边沿"; conditions["trigger"].Text = "两卡等待实际触发及采集完成"; }
            if (key == "ACQ_STATE")
            {
                // Preserve actual per-card evidence, not just a generic shared wait lamp.
                conditions["trigger"].Text = message;
                string[] parts = message.Split(';', '；');
                foreach (string part in parts)
                {
                    if (part.IndexOf("ART", StringComparison.OrdinalIgnoreCase) >= 0) conditions["art"].Text = part.Trim();
                    if (part.IndexOf("Pico", StringComparison.OrdinalIgnoreCase) >= 0) conditions["pico"].Text = part.Trim();
                }
            }
            if (log.TextLength > 50000) log.Text = log.Text.Substring(log.TextLength - 30000);
            log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + key + "  " + message + Environment.NewLine); status.Text = message;
        }

        private static string RangeHistoryContext(BenchSettings settings)
        {
            // Include every acquisition-affecting setting, including runtime directories, input reference,
            // trigger threshold and timeout. Range changes use their own manual/automatic reset semantics.
            var context = new StringBuilder();
            var fields = typeof(BenchSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            foreach (FieldInfo field in fields)
            {
                if (field.Name == "RunNumber" || field.Name == "RangeIndex") continue;
                object value = field.GetValue(settings);
                string text = value == null ? "" : value is IFormattable ? ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture) : value.ToString();
                context.Append(field.Name).Append('=').Append(text.Length).Append(':').Append(text).Append(';');
            }
            return context.ToString();
        }

        private void ResetConditions()
        {
            foreach (Label label in conditions.Values) label.ForeColor = Color.DarkSlateGray;
            conditions["zero"].Text = "目标设定 0 V；本轮尚未进入 HV UP 阶段";
            conditions["ao"].Text = mode.SelectedIndex != 0 ? "本轮不创建 AO 输出" : "等待本轮仿真 AO=0 状态";
            conditions["hv"].Text = mode.SelectedIndex != 0 ? "HV ON/OFF 均不执行；共享模式只操作 TRG" : "等待本轮仿真 HV 状态";
            conditions["feedback"].Text = "AI0、AI2 悬空，不读作有效电压/电流";
            conditions["safety"].Text = mode.SelectedIndex != 0 ? "本轮尚未读取 P0.4 原始电平" : "仿真未读取物理 P0.4 输入";
            conditions["art"].Text = conditions["pico"].Text = "本轮尚未布置";
            conditions["trigger"].Text = "本轮尚未确认触发与完整采集";
            conditions["complete"].Text = "本轮无完整数据";
            conditions["timing"].Text = N("loop").ToString("G", CultureInfo.InvariantCulture) + " ms 为请求间隔，Windows 不保证实时精度";
        }

        private void InvalidateRunConditions(string reason)
        {
            rangeState.Reset(); latest = null; analysis = null; locationAdc = null; export.Enabled = false;
            rangeDiagnosticText.Text = reason + "；没有本轮有效定位量程诊断。";
            conditions["art"].Text = conditions["pico"].Text = reason;
            conditions["trigger"].Text = "未通过本轮完整同步采集验证";
            conditions["complete"].Text = "无可接受的本轮完整结果";
            foreach (string key in new[] { "art", "pico", "trigger", "complete" }) conditions[key].ForeColor = Color.Firebrick;
            stageDiagnostics.Abort(reason, reason.IndexOf("停止", StringComparison.Ordinal) >= 0);
            RefreshStageLamps();
        }

        internal async Task SmokeAsync(string path)
        {
            smokeMode = true;
            VerifyDiscoveryUi();
            Size originalSize = Size;
            try
            {
                mode.SelectedIndex = 0; simOscillation.Checked = true; terminal.SelectedIndex = trigger.SelectedIndex = edge.SelectedIndex = 0;
                artDevice.Text = "Dev1"; artSerial.Text = "SIM-ART"; picoSerial.Text = "SIM-PICO"; range.SelectedIndex = 4;
                // Synthetic identity for UI-only rendering; no SDK is called by --ui-smoke.
                ApplyDiscovery(new DiscoveryResult { ArtDevices = new List<ArtDeviceIdentity> { new ArtDeviceIdentity { Name = "SIM-Dev1", Model = "USB3135", Serial = "900000001" } }, PicoSerials = new List<string> { "SIM-PICO" } }, "", "");
                numbers["pdcal"].Value = 2100; numbers["pulse"].Value = 100; numbers["divider"].Value = 11000;
                numbers["inductance"].Value = .7M; numbers["resistance"].Value = 22; numbers["skip"].Value = .5M;
                await StartRun();
                if (latest == null || latest.PdAdc.Length != BenchSettings.PicoCount) throw new InvalidOperationException("界面仿真未完成。");
                string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path));
                viewTabs.SelectedIndex = 0; await Task.Delay(60); SaveSmokeImage(path);
                viewTabs.SelectedIndex = 1; await Task.Delay(60); SaveSmokeImage(stem + ".location20ms.png");
                viewTabs.SelectedIndex = 3; await Task.Delay(60); SaveSmokeImage(stem + ".range-diagnostics.png");
                viewTabs.SelectedIndex = 2; await Task.Delay(60); SaveSmokeImage(stem + ".conditions.png");
                viewTabs.SelectedIndex = 0; Size = MinimumSize; await Task.Delay(60); SaveSmokeImage(stem + ".minimum.png");
                Size = originalSize; SetDiagnosticFailureSmokeState(); await Task.Delay(60); SaveSmokeImage(stem + ".stage-error.png");
                using (var dialog = CreateStageDetailsDialog("WAIT_TRIGGER"))
                {
                    dialog.Show(this); dialog.PerformLayout(); await Task.Delay(60);
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(stem + ".stage-details.png"); }
                    dialog.Close();
                }
            }
            finally { Size = originalSize; viewTabs.SelectedIndex = 0; smokeMode = false; }
        }

        private void SaveSmokeImage(string path)
        { PerformLayout(); Update(); using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(path); } }

        internal async Task RecordCheckAsync(string sourceDirectory, string imagePath)
        {
            // Offline replay only. The constructor suppresses enumeration for this command.
            smokeMode = true;
            var capture = BenchCapture.Load(sourceDirectory);
            int originalRange = capture.Settings.RangeIndex;
            string output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imagePath)), "record-replay");
            Directory.CreateDirectory(output); capture.DirectoryPath = output;
            range.SelectedIndex = originalRange;
            numbers["picorate"].Value = (decimal)(capture.PicoRateHz / 1e6);
            numbers["duration"].Value = (decimal)capture.Settings.CaptureDurationMs;
            numbers["pulse"].Value = (decimal)capture.Settings.CalibrationPulsePc;
            numbers["pdcal"].Value = (decimal)capture.Settings.PdCalibrationPcPerV;
            numbers["ext"].Value = (decimal)capture.Settings.ExternalTriggerVolts;
            numbers["trgpulse"].Value = capture.Settings.TriggerPulseMs;
            resolution.SelectedIndex = Array.IndexOf(new[] { 8, 12, 14, 15, 16 }, capture.Settings.ResolutionBits);
            latest = capture; DisplayCapture(capture);
            if (capture.Settings.RangeIndex != originalRange) throw new InvalidOperationException("自动量程更改了本轮元数据。");
            if (range.SelectedIndex != originalRange) throw new InvalidOperationException("未确认弹窗就修改了界面量程。");
            string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imagePath)), Path.GetFileNameWithoutExtension(imagePath));
            notice.Text = "历史记录离线回放；本次未枚举或打开硬件、未发送 TRG。图表与量程建议来自原始记录。";
            viewTabs.SelectedIndex = 0; await Task.Delay(80); SaveSmokeImage(imagePath);
            viewTabs.SelectedIndex = 1; await Task.Delay(80); SaveSmokeImage(stem + ".location.png");
            viewTabs.SelectedIndex = 3; await Task.Delay(80); SaveSmokeImage(stem + ".range-diagnostics.png");
            var d = DetectedWaveforms.Build(capture);
            File.WriteAllText(imagePath + ".txt", "OFFLINE_REPLAY=PASS\nSOURCE=" + sourceDirectory + "\nRAW_RANGE=" + originalRange + "\nNEXT_RANGE=" + analysis.LocationRange.RecommendedIndex +
                "\nPICO_RAW_COUNT=" + capture.PdAdc.Length + "\nDETECTED_RATE=" + d.OutputRateHz + "\nDETECTED_COUNT=" + d.OutputCount + "\n" + result.Text, Encoding.UTF8);
        }
    }
}
