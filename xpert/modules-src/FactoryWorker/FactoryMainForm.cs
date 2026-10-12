using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm : Form
    {
        private readonly Color teal = Color.FromArgb(0, 125, 116);
        private readonly Label rampStatistics = new Label { Dock = DockStyle.Bottom, Height = 26, AutoEllipsis = true, Text = "HV UP：未执行", ForeColor = Color.Teal };
        private readonly ComboBox mode = Choice("仿真演示", "实机出厂测试");
        private readonly NumericUpDown targetKv = Number(5, 0, 30, 3);
        private readonly CheckBox continuous = new CheckBox { Text="连续加压", AutoSize=true, Margin=new Padding(5,17,3,0) };
        private readonly NumericUpDown repeatCount = new NumericUpDown { Minimum=1, Maximum=100, Value=1, Width=54, Enabled=false, Margin=new Padding(0,15,6,0) };
        private readonly Label remainingCount = new Label { Text="剩余 — 次",AutoSize=false,Size=new Size(83,21),ForeColor=Color.Teal,TextAlign=ContentAlignment.MiddleLeft };
        private readonly Label completedCount = new Label { Text="完成 0 次",AutoSize=false,Size=new Size(78,23),ForeColor=Color.Teal,TextAlign=ContentAlignment.MiddleLeft };
        private readonly Button start = Button("开始加压测试", 130), stop = Button("停止", 76),
            calibrate = Button("校准页面", 110), export = Button("导出数据", 94),
            openFolder = Button("打开记录", 94), reset = Button("视图复位", 94), discover = Button("自动获取两卡", 122),
            applyCalibration = Button("应用候选校准系数", 210), applyRange = Button("采用推荐量程 · 下一轮", 188),
            calibrationStart = Button("采集校准源", 210);
        private readonly ComboBox artChoice = Choice("等待获取 ART"), picoChoice = Choice("等待获取 Pico");
        private readonly TextBox artIdentity = ReadOnlyText(), picoIdentity = ReadOnlyText();
        private readonly TextBox artLibrary = new TextBox { Text = @"C:\Program Files (x86)\ART Technology\ART-DAQ\Lib\x64" },
            picoLibrary = new TextBox { Text = "auto" };
        private readonly ComboBox terminal = Choice("RSE · AGND（固定）"),
            resolution = Choice("8 bit", "12 bit", "14 bit", "15 bit", "16 bit"), coupling = Choice("DC", "AC"),
            range = Choice("±10 mV", "±20 mV", "±50 mV", "±100 mV", "±200 mV", "±500 mV", "±1 V", "±2 V", "±5 V", "±10 V", "±20 V"),
            calibrationFrequency = Choice("100 Hz", "400 Hz");
        private readonly CheckBox bandwidth = new CheckBox { Text = "Pico A：20 MHz 硬件限带", AutoSize = true },
            wiring = new CheckBox { Text = "本轮已按现场程序独立确认放电、核对反馈接线；人员撤离", AutoSize = true },
            oscillationCircuit = new CheckBox { Text = "已接标准电容与振荡回路（启用指标）", AutoSize = true },
            referenceCapacitor = new CheckBox { Text = "用标准电容 / 介损反算电感", Checked = true, AutoSize = true },
            automaticRange = new CheckBox { Text = "量程提醒", Checked = true, AutoSize = true, Margin=new Padding(3,17,6,0) },
            calibrationConnected = new CheckBox { Text = "本轮已手动注入已知电量校准源，高压隔离", AutoSize = true };
        private readonly Dictionary<string, NumericUpDown> values = new Dictionary<string, NumericUpDown>();
        private readonly Dictionary<string, Button> stages = new Dictionary<string, Button>();
        private readonly Dictionary<string, string> stageDetails = new Dictionary<string, string>();
        private readonly Label notice = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, ForeColor = Color.DarkSlateGray, Padding = new Padding(4,0,4,0), TextAlign=ContentAlignment.MiddleLeft },
            status = new Label { AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DarkSlateGray },
            resultSummary = new SingleLineSummary { AutoSize = false, AutoEllipsis=true, Dock = DockStyle.Fill, Padding = new Padding(8,0,8,0), BackColor = Color.White },
            calibrationSummary = new Label { AutoSize = false, Dock = DockStyle.Fill, Padding = new Padding(8), Text = "尚无校准候选。只采集人工注入的校准信号，不远程操作校准源。" },
            emergency = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(226, 232, 236), Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Text = "急停 P0.4\n尚未读取" },
            referenceSummary = new SingleLineSummary { AutoSize = false, AutoEllipsis=true, Dock = DockStyle.Fill, Padding = new Padding(8,0,8,0), Text = "标准电容参考 200 nF / 介损 0.24%；等待有效振荡数据。" };
        private readonly TextBox diagnostics = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, WordWrap = true, BackColor = Color.White };
        private readonly FactoryWaveformControl combined = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "检波局放 / 电压" },
            rawPd = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "定位局放 · 整段" },
            calibrationRaw = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "校准时段 · 原始脉冲与背景", RawUnit = "V" };
        private readonly WavePanel calibrationAverage = new WavePanel { Title = "对齐平均校准脉冲 · 扣基线", Unit = "V", TraceColor = Color.FromArgb(0, 125, 116) };
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly FactoryShutdownDiagnosticControl shutdownDiagnostic = new FactoryShutdownDiagnosticControl();
        private readonly FactoryFeedbackTrendControl feedbackTrend = new FactoryFeedbackTrendControl();
        private readonly FactoryFeedbackTrendControl pressureTrend = new FactoryFeedbackTrendControl { OverlayChannels = true };
        private TabPage monitorPage;
        private TabPage parametersPage, calibrationPage, acceptancePage;
        private CancellationTokenSource cancellation;
        private FactoryResult latest;
        private DiscoveryResult devices;
        private bool busy, scanning, exporting, closeAfterStop, smokeMode, applyingDiscovery;
        private decimal? simulationCoefficientBeforeApply;
        private int? simulationRangeBeforeApply;
        private FactorySettings appliedCalibrationProfile;
        private string pinnedArtSerial = "", pinnedPicoSerial = "";
        private string roundFailureDetail;
        private bool emergencyRead, lastEmergencyReleased;
        private readonly FactoryEmergencyNotice emergencyNotice = new FactoryEmergencyNotice();
        private FactorySequenceProgress sequenceProgress;
        private DateTime emergencyReadTime;
        private static readonly string[] StageKeys = { "INIT", "AO_ZERO", "SAFE", "HV_ON", "HV_DELAY", "HV_UP", "ARM", "ARM_DELAY", "HV_OFF", "OPTICAL", "WAIT_TRIGGER", "READ", "DISCHARGE", "COMPLETE" };
        private static readonly string[] StageNames = { "初始化两卡", "AO = 0 V", "反馈稳定 / 联锁", "HV ON", "HV ON 零输出监测", "升压条件", "布置两卡/计时", "布置起≥500ms", "HV OFF/AO归零", "光触发 1.5 秒", "同步检测", "读取本轮数据", "残压核验 / 释放", "本轮结束" };

        private sealed class SingleLineSummary : Label
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                // Keep the full original Text for tooltips and saved evidence;
                // paint one line instead of a clipped fragment of a second line.
                var area=new Rectangle(Padding.Left,Padding.Top,Math.Max(0,ClientSize.Width-Padding.Horizontal),Math.Max(0,ClientSize.Height-Padding.Vertical));
                TextRenderer.DrawText(e.Graphics,(Text??"").Replace("\r","").Replace("\n","  |  "),Font,area,ForeColor,
                    TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            }
        }

        internal FactoryMainForm(string testLabelRoot = null,string testParameterDirectory = null)
        {
            Text = "雄新电力科技 · 出厂测试程序 V1.0.30 · 批次连续测试版 1.0.30.9";
            Font = new Font("Microsoft YaHei UI", 9); BackColor = Color.FromArgb(239, 244, 247);
            Size = new Size(1600, 1040); MinimumSize = new Size(1280, 926); StartPosition = FormStartPosition.CenterScreen;
            using (var stream = GetType().Assembly.GetManifestResourceStream("Factory.Brand.ico")) if (stream != null) Icon = new Icon(stream);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(root);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 255));
            heading.Controls.Add(new Label { Text = "出厂测试程序 · V1.0.30", Font = new Font(Font.FontFamily, 21, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            using (var stream = GetType().Assembly.GetManifestResourceStream("Factory.Brand.Logo")) if (stream != null)
                heading.Controls.Add(new PictureBox { Image = new Bitmap(stream), Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom }, 1, 0);
            root.Controls.Add(heading, 0, 0);
            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
            mode.Width = 150; mode.Height = 32; mode.Margin = new Padding(0, 20, 6, 0);
            targetKv.Font = new Font(Font.FontFamily, 26, FontStyle.Bold); targetKv.ForeColor = teal; targetKv.Dock = DockStyle.Fill;
            var voltageEntry = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            voltageEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); voltageEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            voltageEntry.Controls.Add(targetKv, 0, 0); voltageEntry.Controls.Add(new Label { Text = "kV", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 13, FontStyle.Bold) }, 1, 0);
            toolbar.Controls.Add(mode, 0, 0); toolbar.Controls.Add(voltageEntry, 1, 0);
            var actions = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2, Padding=Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute,48)); actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var primaryActions=new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, AutoScroll=false, Margin=Padding.Empty };
            var secondaryActions=new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, AutoScroll=false, Margin=Padding.Empty };
            start.Height = stop.Height = 42; start.Font = stop.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
            start.Margin=stop.Margin=new Padding(2);
            continuous.Margin=new Padding(5,12,3,0); automaticRange.Margin=new Padding(3,12,6,0);
            foreach(var button in new[]{calibrate,saveData,export,openFolder,reset})
            {button.Size=new Size(button==saveData?100:84,26);button.Margin=new Padding(2,1,2,1);}
            start.BackColor = teal; start.ForeColor = Color.White; stop.BackColor = Color.FromArgb(177, 48, 48); stop.ForeColor = Color.White;
            var countPanel=new Panel {Size=new Size(141,48),Margin=Padding.Empty};
            repeatCount.Location=new Point(2,0);repeatCount.Width=58;repeatCount.Margin=Padding.Empty;remainingCount.Location=new Point(2,25);
            completedCount.Location=new Point(64,0);
            countPanel.Controls.Add(repeatCount);countPanel.Controls.Add(remainingCount);countPanel.Controls.Add(completedCount);
            primaryActions.Controls.AddRange(new Control[]{start,continuous,countPanel,automaticRange,stop});
            secondaryActions.Controls.AddRange(new Control[]{calibrate,saveData,export,openFolder,reset,repeatPrompts});
            pulseTip.SetToolTip(repeatPrompts,"恢复/隐藏普通提示，按两卡序列号保存。连续批次首次确认后，正常轮次自动继续，不再逐轮弹窗或内嵌确认。隐藏普通提示不等于同意调档；量程提醒开启时保留当前档，关闭时按有效建议调整。停止、急停及故障保护不受影响。");
            actions.Controls.Add(primaryActions,0,0);actions.Controls.Add(secondaryActions,0,1);
            toolbar.Controls.Add(actions, 2, 0); emergency.Margin = new Padding(6, 7, 0, 7); toolbar.Controls.Add(emergency, 3, 0);
            root.Controls.Add(toolbar, 0, 1); root.Controls.Add(BuildStages(), 0, 2); root.Controls.Add(notice, 0, 3);
            root.Controls.Add(tabs, 0, 4); root.Controls.Add(status, 0, 5);
            resultSummary.TextChanged+=(s,e)=>pulseTip.SetToolTip(resultSummary,resultSummary.Text);
            status.TextChanged+=(s,e)=>pulseTip.SetToolTip(status,status.Text);
            referenceSummary.TextChanged+=(s,e)=>pulseTip.SetToolTip(referenceSummary,referenceSummary.Text);
            BuildTabs(); resolution.SelectedIndex = 2; range.SelectedIndex = 10; terminal.Enabled = false; knownCharge.SelectedIndex = FactoryCalibrationLevels.DefaultIndex;
            values["pd"].ValueChanged += (s,e) =>
            {
                if (!applyingCalibrationValue) { appliedCalibrationProfile = null; calibrationOrigin.Text = "手动设置（未声明校准通过）；已保存数据仍用采集时系数。"; }
                RefreshRangeLabels(); RefreshStoredCalibration();
            }; RefreshRangeLabels();
            values["startupvmon"].ValueChanged += (s,e) => RefreshMode();
            values["residual"].ValueChanged += (s,e) => RefreshMode();
            stop.Enabled = export.Enabled = applyCalibration.Enabled = applyRange.Enabled = false;
            mode.SelectedIndexChanged += async (s, e) =>
            {
                if(restoringStartupMode)return;
                SaveParameterPreferences();
                loadingParameters = parameterStore != null;
                wiring.Checked = calibrationConnected.Checked = oscillationCircuit.Checked = false;
                // Values derived from synthetic results are for simulation only.
                // Restore the pre-application settings before entering real mode.
                if (simulationCoefficientBeforeApply.HasValue) values["pd"].Value = simulationCoefficientBeforeApply.Value;
                if (simulationRangeBeforeApply.HasValue) range.SelectedIndex = simulationRangeBeforeApply.Value;
                simulationCoefficientBeforeApply = null; simulationRangeBeforeApply = null;
                appliedCalibrationProfile = null; calibrationOrigin.Text = "默认 / 手动系数；尚无当前模式校准来源。";
                // Never carry a simulation candidate or acceptance result into a
                // different operating mode. Stored records remain on disk.
                latest = null; applyCalibration.Enabled = applyRange.Enabled = export.Enabled = false;
                combined.ClearWave(); rawPd.ClearWave(); calibrationRaw.ClearWave(); calibrationAverage.ClearWave(); ResetTf(true); UpdatePostProcessingButtons(); ResetStages(); ResetEmergency();
                resultSummary.Text = "模式已切换；等待本轮数据，先前结果不能作为当前状态。";
                calibrationSummary.Text = "模式已切换，原校准候选不再可应用；已保存记录不变。";
                LoadParameterPreferences(mode.SelectedIndex);
                loadingParameters = false;
                UpdateAcceptanceDisplay(); RefreshMode(); if (mode.SelectedIndex == 1 && !smokeMode && !busy && !scanning) await Scan();
            };
            discover.Click += async (s, e) => await Scan(); start.Click += async (s, e) => await Run(FactoryRunKind.Pressure);
            diagnoseP04.Click += async (s, e) => await DiagnoseP04();
            diagnoseAi.Click += async (s,e) => await DiagnoseAi();
            targetKv.ValueChanged += (s,e) => RefreshMode();
            continuous.CheckedChanged += (s,e) => {repeatCount.Enabled = continuous.Checked && !busy && !scanning;RefreshRemainingCount();};
            repeatCount.ValueChanged+=(s,e)=>RefreshRemainingCount();
            calibrate.Click += (s, e) => tabs.SelectedTab = calibrationPage;
            calibrationStart.Click += async (s, e) => await Run(FactoryRunKind.Calibration);
            stop.Click += (s, e) => RequestUserStop();
            export.Click += async (s, e) => await Export(); openFolder.Click += (s, e) => OpenRecords();
            reset.Click += (s, e) => { combined.ResetView(); rawPd.ResetView(); selectedPulse.ResetView(); calibrationRaw.ResetView(); calibrationAverage.ResetView(); };
            artChoice.SelectedIndexChanged += (s, e) => SelectArt(); picoChoice.SelectedIndexChanged += (s, e) => SelectPico();
            applyCalibration.Click += (s, e) => ApplyCalibration(); applyRange.Click += (s, e) => ApplyRange();
            openCapture.Click += async (s, e) => await OpenSavedCapture();
            FormClosing += (s, e) => { if (busy || scanning || exporting || labelProcessing) { e.Cancel = true; closeAfterStop = true; RequestUserStop(); status.Text = "等待当前操作结束后关闭；软件退出不代表设备已放电。"; } };
            ResetStages(); RefreshMode(); status.Text = "默认仿真；实机需核对准确序列号、反馈接线及残压。P0.2 固定低电平过流保护。";
            InitializePulseLabels(testLabelRoot);
            InitializeParameterPreferences(testLabelRoot,testParameterDirectory);
            InitializeConnectionDisplay(testLabelRoot!=null);
        }

        private Control BuildStages()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 2 };
            for (int i = 0; i < 7; i++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int i = 0; i < StageKeys.Length; i++)
            {
                string key = StageKeys[i]; var button = new StageButton { Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(2), Font = new Font(Font.FontFamily, 8.5f), Tag = StageNames[i] };
                button.Click += (s, e) => ShowStage(key); stages.Add(key, button); panel.Controls.Add(button, i % 7, i / 7);
            }
            return panel;
        }

        private void BuildTabs()
        {
            var wavePage = new TabPage("加压测试"); var waveGrid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(6) };
            waveGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            waveGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); waveGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); waveGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            var statusPanel = new Panel { Dock = DockStyle.Fill }; statusPanel.Controls.Add(resultSummary); statusPanel.Controls.Add(rampStatistics);
            statusPanel.Controls.Add(BuildCapturePosition());
            waveGrid.Controls.Add(statusPanel, 0, 0); waveGrid.Controls.Add(BuildFourWaveViews(), 0, 1); waveGrid.Controls.Add(referenceSummary, 0, 2); wavePage.Controls.Add(waveGrid);
            parametersPage = new TabPage("参数与接线");
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var identity = Group("两卡身份 · 精确锁定，不自动换卡");
            Add(identity, "ART 设备", artChoice); Add(identity, "锁定 ART", artIdentity); Add(identity, "Pico 设备", picoChoice); Add(identity, "锁定 Pico", picoIdentity); Add(identity, "读取设备", discover);
            Add(identity, "联锁排查 · 不加压", diagnoseP04);
            Add(identity, "原始反馈排查 · 不加压", diagnoseAi);
            // Per-round acknowledgement is consolidated into the existing start
            // dialog. There is no second checkbox and no persisted approval.
            settings.Controls.Add(WrapGroup(identity), 0, 0); settings.SetColumnSpan(identity.Parent, 2);
            var acquisition = Group("采集 · 原始 Pico / 80k 检波");
            Num(acquisition, "rate", "Pico 请求 / MS/s", 125, .1M, 1000, 3); Add(acquisition, "分辨率模式", resolution);
            Add(acquisition, "A 耦合", coupling); Add(acquisition, "A 起始量程", range); Add(acquisition, "A 带宽", bandwidth);
            // Keep legacy values visible instead of silently clamping saved
            // profiles; new-run validation rejects values above 60 ms.
            Num(acquisition, "duration", "采集长度 / ms（新采集≤60）", 60, 1, 1000, 3); Num(acquisition, "ext", "Pico EXT 阈值 / V", 1.5M, .01M, 4.9M, 6);
            values["duration"].ValueChanged+=(s,e)=>RefreshCapturePosition();
            Add(acquisition, "ART AI4 输入", terminal); Add(acquisition, "同步检测", new Label { Text = "PFI0 + Pico EXT，上升沿；不自动触发替代", AutoSize = true });
            settings.Controls.Add(WrapGroup(acquisition), 0, 1);
            var conversion = Group("换算与回路");
            Num(conversion, "divider", "分压比 / V每V", 11000, .001M, 1000000, 3); Num(conversion, "pd", "局放系数 / pC每V", 2100, .001M, 10000000, 3);
            Num(conversion, "locationsavecycles", "定位周期数量 N", 1, 1, 100, 0);
            Add(conversion, "系数来源", calibrationOrigin);
            Num(conversion, "pressure", "加压比 / V每V", 6000, .001M, 1000000, 3); Num(conversion, "inductance", "电感 / H", .7M, .000001M, 1000, 6);
            Num(conversion, "resistance", "电阻 / Ω", 22, 0, 100000, 3); Num(conversion, "exclude", "起始过冲排除 / ms", .5M, 0, 19, 3);
            Add(conversion, "振荡指标许可", oscillationCircuit);
            Num(conversion, "referencec", "标准电容 / nF", 200, .001M, 1000000, 3); Num(conversion, "referenceloss", "标准介损 / %", .24M, 0, 20, 4);
            Add(conversion, "参考电容分析", referenceCapacitor);
            Add(conversion, "量程模式", new Label { Text="首过零点后定位保存 N 周期，不足取实际末尾；超量程升档，幅值占用低于25%降档；提醒关闭自动调档并记录。", AutoSize=true });
            // Preserve the legacy profile value without offering an inactive range gate.
            values.Add("rangecycles", new NumericUpDown { Minimum=1, Maximum=100, Value=8 });
            Add(conversion, "量程窗口", new Label { Text = "非零：第一过零点向后N周期，不足取实际末尾；0 kV：整段原始局放。定位派生段与量程使用同一窗口，整段原始数据另存。建议只修改下一轮量程。", AutoSize = true });
            settings.Controls.Add(WrapGroup(conversion), 1, 1);
            var limits = Group("反馈与安全限值");
            Num(limits, "feedbackv", "反馈满量程 / V", 5, .001M, 10, 3);
            Num(limits, "vmon", "VMON 满量程 / kV", 31, .001M, 1000, 3); Num(limits, "imon", "IMON 满量程 / mA", 13, .001M, 1000, 3);
            Num(limits, "current", "限流 / mA", 10, .001M, 1000, 3); Num(limits, "maxkv", "最大目标 / kV", 30, .001M, 30, 3);
            Num(limits, "maxao", "最大 AO / V", 5, .001M, 5, 3);
            Num(limits, "startupvmon", "两路启动/背景基准 / V", 500, 1, 1000, 1);
            Num(limits, "residual", "故障/校准残压阈值 / V", 50, 1, 1000, 1);
            Num(limits, "agreement", "双反馈容差 / %", 15, .1M, 30, 1); Num(limits, "targettol", "目标容差 / %", 2, .1M, 10, 1);
            Add(limits, "共同上限", new Label { Text = "仅|AI4|窗口反馈±2%达压，不取整。HV ON前不采AI；AO=0、HV ON后两路启动/背景默认500 V，VMON负向容差单独采用此基准，-500～0 V工作显示按0但原始值保留。正常非零单次/中间轮不追加AI；仅连续末轮新采AI4 10ms均值观察，不作放电确认。独立过压≤30 kV；AO≤min(目标/加压比×1.05,最大AO,5 V)。阈值不是放电或接近许可。", AutoSize = true });
            Add(limits, "反馈采样", new Label { Text = "R10：P0.4独立线程每10 ms请求读取；AI/AO/DO另行串行执行。硬件1000 S/s/通道；缓存有界追读、保留各组峰值。归零等待200 ms后连续5组确认，2秒超时，无自动扣零。启动不写HV OFF，只有尝试HV ON后关断。振荡段±10 V / 80 kS/s，Windows非硬实时。", AutoSize = true });
            settings.Controls.Add(WrapGroup(limits), 0, 2);
            var timing = Group("流程延时 · Windows 非硬实时");
            Num(timing, "step", "AO 步长 / V", .01M, .0001M, .1M, 4); Num(timing, "loop", "升压循环请求 / ms", 1, 1, 1000, 0);
            Num(timing, "hvdelay", "HV ON 延时 / ms", 50, 50, 5000, 0); Num(timing, "armdelay", "布置开始至关断 / ms", 500, 500, 5000, 0);
            Num(timing, "triggerwait", "校准/0kV等待 ms（加压固定3秒）", 3000, 1101, 60000, 0); Num(timing, "rampwait", "升压超时 / s", 10, 1, 600, 1);
            Num(timing, "holdchecks", "达压复核轮数（0取消）", 0, 0, 50, 0);
            Num(timing, "holdinterval", "达压复核间隔 / ms", 10, 1, 100, 0);
            Num(timing, "holdtimeout", "达压复核限时 / ms", 500, 50, 2000, 0);
            Num(timing, "aiwindow", "AI反馈窗口中位数 / ms", 10, 3, 40, 0);
            Num(timing, "dischargewait", "放电等待超时 / s", 30, 1, 120, 1);
            Add(timing, "光触发", new Label { Text = "P0.7 正常保持 " + BenchSettings.OpticalHoldMilliseconds + " ms 后回低；停止/错误可提前回低", AutoSize = true });
            settings.Controls.Add(WrapGroup(timing), 1, 2);
            var references = Group("独立参考 · 可选，0 表示未提供");
            Num(references, "referencedc", "独立参考直流 / kV", 0, 0, 1000, 3); Num(references, "referenceosc", "独立参考振荡峰 / kV", 0, 0, 1000, 3);
            Num(references, "ratiotol", "参考比值容差 / %", 5, .1M, 20, 1);
            Add(references, "有效性", new Label { Text = "未提供独立参考时，加压比和分压比仅是设定值，不能标为已核验。频率、电容和介损需有效振荡波形。", AutoSize = true });
            settings.Controls.Add(WrapGroup(references), 0, 3); settings.SetColumnSpan(references.Parent, 2);
            scroll.Controls.Add(settings); parametersPage.Controls.Add(scroll); parametersPage.Controls.Add(parameterPersistence);
            calibrationPage = BuildCalibrationPage();
            var resultPage = new TabPage("诊断"); var resultGrid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(12) };
            resultGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); resultGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            actions.Controls.Add(applyRange); actions.Controls.Add(openCapture); resultGrid.Controls.Add(actions, 0, 0); resultGrid.Controls.Add(diagnostics, 0, 1); resultPage.Controls.Add(resultGrid);
            acceptancePage = BuildAcceptancePage();
            tabs.TabPages.Add(wavePage); tabs.TabPages.Add(calibrationPage); tabs.TabPages.Add(acceptancePage); tabs.TabPages.Add(parametersPage); tabs.TabPages.Add(resultPage);
            var shutdownPage=new TabPage("HV OFF前后诊断"); shutdownPage.Controls.Add(shutdownDiagnostic); tabs.TabPages.Add(shutdownPage);
            monitorPage=new TabPage("加压监测 · 三路AI"); monitorPage.Controls.Add(feedbackTrend); tabs.TabPages.Add(monitorPage);
            UpdateAcceptanceDisplay();
        }

        private TabPage BuildCalibrationPage()
        {
            var page = new TabPage("校准"); var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 375)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var calibration = Group("标准电容负载校准 · 无电缆定位验证");
            Add(calibration, "手动注入电量档位", knownCharge); Add(calibration, "校准频率", calibrationFrequency);
            // Retain the old value for profile/record compatibility; it no
            // longer controls time-segment calibration and is not an input.
            var legacyPulseCount=Number(8,3,100,0);values["pulsecount"]=legacyPulseCount;
            page.Disposed += delegate {legacyPulseCount.Dispose();};
            Add(calibration, "平均方式", new Label {AutoSize=true,Text="本段内全部有效脉冲，不按目标次数补采。"});
            Add(calibration, "本轮确认", calibrationConnected);
            Add(calibration, "采集", calibrationStart); calibrationStart.Height = 45; calibrationStart.BackColor = teal; calibrationStart.ForeColor = Color.White;
            Add(calibration, "系数", applyCalibration); applyCalibration.Height = 45;
            Add(calibration, "当前使用系数", storedCalibration);
            Add(calibration, "校准逻辑", new Label { AutoSize = true, Text = "复用100的电容负载路径：整段背景、脉冲开窗、高通、异常剔除、对齐平均及质量核验。无需电缆长度或反射波。" });
            Add(calibration, "记录长度", new Label { AutoSize = true, Text = "使用参数页设置的本段时长，最多60 ms，不自动扩段或补采。全段有效脉冲经周期、形态及信噪比核验后平均；没有有效脉冲、系数非有限或原始数据质量未通过时不应用。" });
            Add(calibration, "应用规则", new Label { AutoSize = true, Text = "系数=已知pC/扣基线平均峰值V。质量通过并结束量程确认后自动带入下一轮，也可手动修改；本轮原始记录保持原参数。仿真系数不能带入实机。" });
            var left = BuildScrollableGroup(calibration); grid.Controls.Add(left, 0, 0);
            var graphs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            graphs.RowStyles.Add(new RowStyle(SizeType.Absolute, 105)); graphs.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); graphs.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            graphs.Controls.Add(calibrationSummary, 0, 0); graphs.Controls.Add(calibrationRaw, 0, 1); graphs.Controls.Add(calibrationAverage, 0, 2);
            grid.Controls.Add(graphs, 1, 0); page.Controls.Add(grid); return page;
        }

        private static ComboBox Choice(params string[] items)
        { var control = new PrintedChoice { Dock = DockStyle.Fill }; control.Items.AddRange(items); control.SelectedIndex = 0; return control; }
        // Draw the title and state separately: native button printing can omit
        // the second line, leaving color as the only stage-status evidence.
        internal sealed class StageButton : Button
        {
            internal string StateText = "未执行";
            protected override void OnPaint(PaintEventArgs e)
            {
                using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
                ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Color.FromArgb(121, 142, 144), ButtonBorderStyle.Solid);
                int half = ClientSize.Height / 2;
                TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(e.Graphics, Convert.ToString(Tag), Font, new Rectangle(3, 1, Math.Max(0, Width - 6), half), ForeColor, flags);
                TextRenderer.DrawText(e.Graphics, "● " + StateText, Font, new Rectangle(3, half, Math.Max(0, Width - 6), Height - half - 1), ForeColor, flags);
                if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
            }
        }
        // Native ComboBox edits can disappear in DrawToBitmap. Draw the selected
        // value explicitly so offline screenshots verify actual settings too.
        internal sealed class PrintedChoice : ComboBox
        {
            internal PrintedChoice() { DropDownStyle = ComboBoxStyle.DropDownList; DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 24; }
            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                bool selected = Enabled && (e.State & DrawItemState.Selected) != 0;
                Color back = !Enabled ? SystemColors.Control : selected ? SystemColors.Highlight : BackColor;
                Color fore = !Enabled ? SystemColors.GrayText : selected ? SystemColors.HighlightText : ForeColor;
                using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
                string value = e.Index >= 0 && e.Index < Items.Count ? GetItemText(Items[e.Index]) : Text;
                Rectangle bounds = e.Bounds; bounds.Inflate(-3, 0);
                TextRenderer.DrawText(e.Graphics, value, Font, bounds, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (Enabled && (e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if ((m.Msg == 0x0317 || m.Msg == 0x0318) && m.WParam != IntPtr.Zero && Width > 28 && Height > 4)
                    using (Graphics graphics = Graphics.FromHdc(m.WParam))
                        TextRenderer.DrawText(graphics, Text, Font, new Rectangle(3, 2, Width - 25, Height - 4), Enabled ? ForeColor : SystemColors.GrayText,
                            Enabled ? BackColor : SystemColors.Control, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
        private static NumericUpDown Number(decimal value, decimal minimum, decimal maximum, int decimals)
        { return new NumericUpDown { Minimum = minimum, Maximum = maximum, DecimalPlaces = decimals, ThousandsSeparator = true, Value = value, Dock = DockStyle.Fill }; }
        private static Button Button(string text, int width)
        { return new Button { Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 2, 0, 0) }; }
        private static TextBox ReadOnlyText() { return new TextBox { ReadOnly = true, Dock = DockStyle.Fill }; }
        private static TableLayoutPanel Group(string title)
        { return new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(10), Tag = title }; }
        private static GroupBox WrapGroup(TableLayoutPanel table)
        {
            var group = new GroupBox { Text = (string)table.Tag, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8, 22, 8, 10), Margin = new Padding(6) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); group.Controls.Add(table); return group;
        }
        private static void Add(TableLayoutPanel table, string caption, Control control)
        {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 7, 5, 7), MaximumSize = new Size(165, 0) }, 0, row);
            control.Margin = new Padding(3, 4, 0, 4); control.Anchor = AnchorStyles.Left | AnchorStyles.Right; control.MinimumSize = new Size(140, 25); table.Controls.Add(control, 1, row);
        }
        private void Num(TableLayoutPanel group, string key, string caption, decimal value, decimal min, decimal max, int decimals)
        { var n = Number(value, min, max, decimals); values.Add(key, n); Add(group, caption, n); }
        private double N(string key) { return (double)values[key].Value; }

        private FactorySettings ReadSettings()
        {
            var art = artChoice.SelectedItem as ArtDeviceIdentity;
            bool simulation = mode.SelectedIndex == 0;
            string pico = picoChoice.SelectedItem as string;
            if (pico != null && (devices == null || !devices.PicoSerials.Contains(pico))) pico = "";
            return new FactorySettings
            {
                Simulation = simulation, TargetKv = (double)targetKv.Value, MaximumKv = N("maxkv"), MaximumFeedbackKv = 30, MaximumAoV = N("maxao"), PressureVPerV = N("pressure"), DividerOnlyFeedback = true,
                VmonFullScaleKv = N("vmon"), ImonFullScaleMa = N("imon"), FeedbackFullScaleV = N("feedbackv"), CurrentLimitMa = N("current"), SafeResidualV = N("residual"),
                RampTimeoutSeconds = N("rampwait"), DischargeTimeoutSeconds = N("dischargewait"), TargetTolerancePercent = N("targettol"), AgreementTolerancePercent = N("agreement"),
                TargetHoldChecks=(int)N("holdchecks"), TargetHoldIntervalMs=(int)N("holdinterval"), TargetHoldTimeoutMs=(int)N("holdtimeout"),
                FeedbackWindowMs=(int)N("aiwindow"), VmonAfterHvStartup=true, StartupVmonLimitV=N("startupvmon"), ZeroBackgroundLimitV=N("startupvmon"),
                IndependentDischargeConfirmed=wiring.Checked, EndAfterPressureCapture=true, OpticalTimeoutRetry=true,
                FeedbackWiringConfirmed = wiring.Checked, CalibrationSourceConfirmed = calibrationConnected.Checked, CalibrationPulseCount = (int)N("pulsecount"),
                CalibrationFrequencyHz = calibrationFrequency.SelectedIndex == 1 ? 400 : 100, ReferenceDcKv = N("referencedc"), ReferenceOscillationPeakKv = N("referenceosc"), RatioTolerancePercent = N("ratiotol"),
                ReferenceCapacitorEnabled = referenceCapacitor.Checked, ReferenceCapacitanceNf = N("referencec"), ReferenceLossTangent = N("referenceloss") / 100,
                RangeEvaluationCycleCount = (int)N("rangecycles"),
                LocationSaveCycleCount = (int)N("locationsavecycles"),
                VerificationContextRecorded = true, VerificationLoadCapacitanceNf = (double)acceptanceLoad.Value,
                VerificationRatedMaximumKv = (double)acceptanceMaximum.Value, VerificationAt80PercentConfirmed = voltage80Percent.Checked,
                Input = new BenchSettings { RealHardware = !simulation, ArtDevice = art == null ? "Dev1" : art.Name, ArtSerial = art == null ? "" : art.Serial, PicoSerial = pico ?? "",
                    ArtLibraryDirectory = artLibrary.Text.Trim(), PicoLibraryDirectory = picoLibrary.Text.Trim(), ArtTerminal = 10083,
                    ArtTrigger = "/" + (art == null ? "Dev1" : art.Name) + "/PFI0", FallingTrigger = false, AutomaticSharedTrigger = false, TriggerPulseMs = BenchSettings.OpticalHoldMilliseconds,
                    RequestedPicoRateHz = N("rate") * 1e6, ResolutionBits = new[] { 8, 12, 14, 15, 16 }[resolution.SelectedIndex], CaptureDurationMs = N("duration"),
                    PreTriggerPercent=targetKv.Value>0?capturePosition.Value+50:0,
                    PicoCoupling = coupling.SelectedIndex == 0 ? 1 : 0, PicoBandwidth20MHz = bandwidth.Checked, RangeIndex = range.SelectedIndex, ExternalTriggerVolts = N("ext"),
                    PdCalibrationPcPerV = N("pd"), DividerVPerV = N("divider"), InductanceH = N("inductance"), ResistanceOhms = N("resistance"), TransientExclusionMs = N("exclude"),
                    AutoRange = automaticRange.Checked,
                    OscillationCircuitConnected = simulation || oscillationCircuit.Checked,
                    CalibrationPulsePc = FactoryCalibrationLevels.Pc[knownCharge.SelectedIndex], AoStepV = N("step"), LoopDelayMs = (int)N("loop"), HvDelayMs = (int)N("hvdelay"), ArmDelayMs = (int)N("armdelay"), TriggerTimeoutMs = (int)N("triggerwait") }
            };
        }

        private void RefreshMode()
        {
            bool simulation = mode.SelectedIndex == 0;
            continuous.Enabled = !busy && !scanning && !exporting && targetKv.Value>0;
            repeatCount.Enabled = continuous.Enabled && continuous.Checked;
            automaticRange.Enabled = !busy && !scanning && !exporting;
            discover.Enabled = !simulation && !busy && !scanning; wiring.Enabled = calibrationConnected.Enabled = oscillationCircuit.Enabled = !simulation && !busy && !scanning;
            diagnoseP04.Enabled = !simulation && !busy && !scanning && !exporting && !labelProcessing;
            diagnoseAi.Enabled = diagnoseP04.Enabled;
            notice.Text = simulation ? "仿真：全部波形与状态为合成演示，不枚举或操作硬件，不用于设备验收。\n绿=通过，浅红=错误，黄=跳过，蓝=运行；点击步骤查看证据。"
                : targetKv.Value == 0 ? "0 kV：只读自由采集 AI4 / Pico A，P0.4 高低均可采集；不创建 AO/DO，不启动直流源、不发光。\n两卡独立起点，未证明同步；0 kV 设定不代表实测无电或已放电。"
                : "实机：精确锁定 ART / Pico；急停、启动背景、过流/过压保护保留；结束/故障不追加AI残压读取。P0.2固定低。\nP0.7正常保持1.5秒；联动超时3秒仅在允许条件下补发一次，诊断段不计完成；现场放电程序仍由操作人执行。";
            if (historicalCapturePreview && displayedPreview != null)
                notice.Text = "历史记录只读：当前波形来自" + (displayedPreview.Result.Settings.Simulation ? "仿真记录" : "实机记录") +
                    "，不连接或操作设备。残压未确认，不代表试验合格或已放电。\n顶部模式与电压为下一轮设置，不代表这份历史记录的来源或设定。";
            pulseTip.SetToolTip(notice,notice.Text);
            notice.Text=notice.Text.Replace("\r","").Replace("\n","  |  ");
            RefreshResidualStageLabels();
            RefreshStartAvailability();
        }

        private async Task Scan()
        {
            if (busy || scanning || exporting || smokeMode || mode.SelectedIndex == 0) return;
            scanning = true; cancellation = new CancellationTokenSource(); SetBusy(true);
            try
            {
                await SuspendConnectionMonitor();
                string directory = NewDirectory("SCAN");
                var found = await Program.Discover(ReadSettings(), directory, Progress, cancellation.Token);
                ApplyDevices(found); status.Text = "只读枚举完成；列表和序列号不是采集成功或加压许可。";
            }
            catch (Exception ex) { Progress("SCAN_ERROR", ex.Message); status.Text = "设备获取失败：" + ex.Message; }
            finally { scanning = false; cancellation.Dispose(); cancellation = null; SetBusy(false); if (closeAfterStop) Close(); }
        }

        private void ApplyDevices(DiscoveryResult found)
        {
            var selection = DiscoverySelection.Resolve(found, pinnedArtSerial, pinnedPicoSerial); devices = found; applyingDiscovery = true;
            try
            {
                artChoice.Items.Clear(); picoChoice.Items.Clear();
                foreach (var item in found.ArtDevices) if (item != null && item.Model == "USB3135") artChoice.Items.Add(item);
                foreach (string serial in found.PicoSerials) picoChoice.Items.Add(serial);
                artChoice.SelectedIndex = selection.ArtDevice == null ? -1 : artChoice.Items.IndexOf(selection.ArtDevice);
                picoChoice.SelectedIndex = String.IsNullOrWhiteSpace(selection.PicoSerial) ? -1 : picoChoice.Items.IndexOf(selection.PicoSerial);
            }
            finally { applyingDiscovery = false; }
            SelectArt(); SelectPico();
            ScheduleParameterSave();
            Progress("DEVICES", selection.ArtMessage + "；" + selection.PicoMessage + "\nART: " + found.ArtError + "；Pico: " + found.PicoError);
        }
        private void SelectArt()
        {
            if (applyingDiscovery) return; var device = artChoice.SelectedItem as ArtDeviceIdentity;
            if (!SwitchSelectedParameterPair()) return;
            artIdentity.Text = device == null ? "未选择；原卡缺失不自动换卡" : device.Name + " / " + device.Serial;
            if (parameterStore==null)
            {
                if (device == null || device.Serial != pinnedArtSerial) oscillationCircuit.Checked = false;
                if (device != null) pinnedArtSerial = device.Serial;
            }
            wiring.Checked = calibrationConnected.Checked = false;
            if (latest != null && !latest.Settings.Simulation && (device == null || device.Serial != latest.Settings.Input.ArtSerial)) applyCalibration.Enabled = false;
        }
        private void SelectPico()
        {
            if (applyingDiscovery) return; string serial = picoChoice.SelectedItem as string;
            if (!SwitchSelectedParameterPair()) return;
            picoIdentity.Text = serial ?? "未选择；原卡缺失不自动换卡";
            if (parameterStore==null)
            {
                if (String.IsNullOrWhiteSpace(serial) || serial != pinnedPicoSerial) oscillationCircuit.Checked = false;
                if (!String.IsNullOrWhiteSpace(serial)) pinnedPicoSerial = serial;
            }
            wiring.Checked = calibrationConnected.Checked = false;
            if (latest != null && !latest.Settings.Simulation && serial != latest.Settings.Input.PicoSerial) applyCalibration.Enabled = false;
        }

        private FactorySettings ReadStartSettings(FactoryRunKind kind)
        {
            var settings = ReadSettings();
            settings.AutomaticBatch = kind == FactoryRunKind.Pressure && settings.TargetKv > 0 && continuous.Checked;
            return settings;
        }

        private async Task Run(FactoryRunKind kind)
        {
            if (busy || scanning || exporting || labelProcessing || smokeMode) return;
            string unavailable;
            if(kind==FactoryRunKind.Pressure && !CanStartPressure(out unavailable))
            {status.Text=unavailable;return;}
            FactorySettings settings;
            try
            {
                settings = ReadStartSettings(kind);
                if (kind == FactoryRunKind.Pressure) RequireAppliedCalibrationProfile(settings);
                if (kind == FactoryRunKind.Calibration) { settings.TargetKv = 0;settings.Input.PreTriggerPercent=0; }
                settings.ValidateBeforeOperatorConfirmation(kind);
                if(kind==FactoryRunKind.Pressure && continuous.Checked)FactoryPressureStartPrompt.ValidateRangePreflight(settings,(int)repeatCount.Value);
            }
            catch (Exception ex) { status.Text = "参数未通过：" + ex.Message; diagnostics.AppendText("START_PARAMETER_REJECTED："+ex.Message+Environment.NewLine); tabs.SelectedTab = parametersPage; return; }
            bool repeating = kind == FactoryRunKind.Pressure && settings.TargetKv > 0 && continuous.Checked;
            FactorySequenceProgress chosenSequence = null;
            bool startConfirmed = false;
            if (repeating && sequenceProgress != null && sequenceProgress.CanResume)
            {
                using (var dialog = new FactorySequenceResumeDialog(sequenceProgress,settings,(int)repeatCount.Value,Icon))
                {
                    dialog.ShowDialog(this);
                    if (dialog.Choice == FactorySequenceChoice.Cancel) return;
                    if (dialog.Choice == FactorySequenceChoice.Continue)
                    { settings = sequenceProgress.ResumeRequest(settings,(int)repeatCount.Value); chosenSequence = sequenceProgress; }
                    startConfirmed = true; // Same dialog includes the new round's acknowledgement.
                }
            }
            if (!settings.Simulation && !startConfirmed && kind == FactoryRunKind.Pressure &&
                MessageBox.Show(this,FactoryPressureStartPrompt.Text(settings,repeating ? (int)repeatCount.Value : 1,automaticRange.Checked),
                    "开始加压 / 采集",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            if (!settings.Simulation && kind == FactoryRunKind.Calibration)
            {
                string detail = "ART " + settings.Input.ArtDevice + " / SN " + settings.Input.ArtSerial + "\nPico SN " + settings.Input.PicoSerial +
                    "\n分压比 " + settings.Input.DividerVPerV.ToString("0.###") + " V/V；加压比 " + settings.PressureVPerV.ToString("0.###") + " V/V。\n" +
                    "VMON " + settings.VmonFullScaleKv.ToString("0.###") + " kV / " + settings.FeedbackFullScaleV.ToString("0.###") + " V；IMON " + settings.ImonFullScaleMa.ToString("0.###") + " mA / " + settings.FeedbackFullScaleV.ToString("0.###") + " V。\n" +
                    "AO标称 " + settings.NominalAoV.ToString("0.######") + " V；本轮上限 " + settings.AoCommandCeilingV.ToString("0.######") + " V（标称×1.05，且不超过最大AO/5 V）；目标上限 " + settings.MaximumKv.ToString("0.###") + " kV；限流 " + settings.CurrentLimitMa.ToString("0.###") + " mA。\n" +
                    "Pico A " + range.GetItemText(range.Items[settings.Input.RangeIndex]) + " / " + settings.Input.ResolutionBits + " bit / " + (settings.Input.RequestedPicoRateHz / 1e6).ToString("0.###") + " MS/s。\n" +
                    (kind == FactoryRunKind.Pressure ? "HV ON前不采AI、不判残压，须已现场独立确认放电；AO=0、HV ON后VMON/AI4启动背景基准 " + settings.StartupVmonLimitV + " V；单次/中间轮不追加残压；连续末轮另采AI4 10ms均值，仅测量，不标放电通过。" : "校准前残压阈值 " + settings.SafeResidualV + " V。") +
                    "中途故障观察阈值 " + settings.SafeResidualV + " V。仅AI4升压闭环，过压/过流保护保留。\n" +
                    "确认 AI0/AI2/AI4 全部有效、无悬空，P0.4 高为急停解除，已核实反馈及安全条件。换算设定不代表比例已物理核验。\n";
                if (kind == FactoryRunKind.Pressure && settings.TargetKv == 0)
                    detail = "ART " + settings.Input.ArtDevice + " / SN " + settings.Input.ArtSerial + "\nPico SN " + settings.Input.PicoSerial +
                        "\n0 kV 只读自由采集：不创建 AO/DO、不启停直流源、不发光。P0.4 高低只记录，不阻止采集；AI0/AI2 不用于放行。\n两卡起点未同步；不确认残压或设备已放电。\n";
                else detail += kind == FactoryRunKind.Pressure ? "本轮目标 " + settings.TargetKv.ToString("0.000") + " kV；程序会操作 AO、HV 开关及 1.5 秒光触发。\n高压风险：未确认放电禁止靠近设备。"
                    : "高压源已隔离、低压校准源已人工注入：" + settings.Input.CalibrationPulsePc.ToString("0.###") + " pC / " + settings.CalibrationFrequencyHz + " Hz。\n本次只采当前设定的 " + settings.Input.CaptureDurationMs.ToString("0.###") + " ms（单段最多60 ms），使用本段全部有效脉冲平均，不按目标次数扩段或补采。原始数据与质量标记保留。\n程序不会远程启动校准源，也不得将加压目标用于校准。";
                if(kind==FactoryRunKind.Pressure && settings.TargetKv>0 && continuous.Checked)
                    detail += "\n连续共 "+repeatCount.Value+" 次（含首轮），同一目标；量程提醒"+(automaticRange.Checked?"开启，确认后调整下一轮":"关闭，有效建议自动调整下一轮")+"；首次确认整批，正常轮次自动继续；取消或故障停止队列。";
                if (MessageBox.Show(this, detail + "\n点击确定表示：本轮已独立确认放电、核对反馈接线和换算、人员已撤离。确认开始？", "确认本轮设备与操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            }
            try { settings = FactoryStartConfirmation.Accept(settings, kind, true); }
            catch (Exception ex) { status.Text = "参数未通过：" + ex.Message; return; }
            if(!settings.Simulation){BindConnectionJournal(settings);if(connectionLogFailed)return;}
            sequenceProgress = repeating ? chosenSequence ?? new FactorySequenceProgress(settings,(int)repeatCount.Value) : null;
            RefreshRemainingCount();
            recognitionSuspended=false;
            busy = true; calibrationAutoAccepted = false; cancellation = new CancellationTokenSource(); SetBusy(true); ResetStages(); ResetEmergency(); diagnostics.Clear(); latest = null; ResetTf(false);
            combined.ClearWave(); rawPd.ClearWave(); calibrationRaw.ClearWave(); calibrationAverage.ClearWave(); resultSummary.Text = "本轮正在执行；先前结果不代表当前设备状态。"; calibrationSummary.Text = "等待本轮完整数据与质量核验。";
            applyCalibration.Enabled = applyRange.Enabled = export.Enabled = false;
            try
            {
                if (repeating)
                {
                    int done = await FactoryPressureSequence.Run(settings,(int)repeatCount.Value,
                        next=>RunWithRangeReview(next,kind,cancellation.Token),
                        (index,total,next)=>ConfirmNextRound(index,total,next,cancellation.Token),
                        (index,total,next)=>{
                            if (next.Simulation && range.SelectedIndex != next.Input.RangeIndex && !simulationRangeBeforeApply.HasValue) simulationRangeBeforeApply=range.SelectedIndex;
                            range.SelectedIndex=next.Input.RangeIndex;
                            RefreshRemainingCount();
                            Progress("SEQUENCE","第 "+index+" / "+total+" 轮；序列执行批次 "+next.SequenceAttempt+"；已完成 "+sequenceProgress.Completed+" 轮。");
                        },cancellation.Token,
                        (round,quality)=>Task.FromResult(ReviewCapturedRange(round,quality,true)),sequenceProgress,RefreshRemainingCount,
                        (code,detail)=>{Progress(code,detail);resultSummary.Text=detail;});
                }
                else await RunWithRangeReview(settings, kind, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (kind == FactoryRunKind.Calibration && calibrationAutoAccepted) ApplyCalibration(true);
            }
            catch (FactorySequenceStoppedException ex) { SequenceStopped(ex); }
            catch (FactoryTriggerFailureException ex)
            {
                MarkActiveErrors(ex.Message);Progress("STEP_COMPLETE_ERROR",ex.Message);
                string message=ex.Message;
                try
                {
                    var decision=FactoryTriggerThresholdPolicy.Shared.Failure(ex.Result);
                    if(decision.Changed){values["ext"].DecimalPlaces=6;values["ext"].Value=(decimal)decision.NewVolts;
                        if(!SaveParameterPreferences())message+="\n新阈值未成功保存，请查看参数页。";}
                    Progress("TRIGGER_THRESHOLD_DECISION",decision.Detail);message+="\n"+decision.Detail;
                }
                catch(Exception logError){message+="\n阈值决定未应用："+logError.Message;}
                status.Text=message;
                MessageBox.Show(this,message+"\n队列已停止；检查联动接线后重新点击加压。本提示不会启动输出。","联动触发错误",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
            catch (OperationCanceledException)
            {
                if(sequenceProgress!=null && !sequenceProgress.InFlight)
                {status.Text="连续队列已停止；已完成 "+sequenceProgress.Completed+" 次，剩余 "+sequenceProgress.Remaining+" 次；未启动下一轮。";Progress("SEQUENCE_CANCELLED",status.Text);}
                else {MarkActiveErrors("本轮已取消；未完成步骤不能标记通过。"); Progress("STEP_COMPLETE_ERROR", "本轮已取消；停止状态以工作进程实际处理结果为准。"); status.Text = "已取消，不证明设备已放电。";}
            }
            catch (Exception ex) { MarkActiveErrors(ex.Message); Progress("STEP_COMPLETE_ERROR", ex.ToString()); status.Text = "本轮失败：" + (roundFailureDetail ?? ex.Message) + "；未确认放电，禁止靠近设备。"; }
            finally
            {
                // UI-side range review, loading or audit errors can occur after
                // the device finished normally. They also end this batch, so do
                // not leave its unused next-round capability in the server.
                if(repeating && !settings.Simulation && sequenceProgress!=null && sequenceProgress.Remaining>0)
                {
                    try { FactoryResidentDevices.RevokeBatch(); }
                    catch(Exception ex) { batchRevocationFailed=true;status.Text+="；批次撤销记录失败："+ex.Message+"。请关闭程序并检查日志。"; }
                }
                if (sequenceProgress != null && sequenceProgress.CanResume)
                { var stoppedSettings=ReadSettings();stoppedSettings.AutomaticBatch=repeating;sequenceProgress.RememberStoppedUi(stoppedSettings); }
                LogConnectionResult("本次任务已退出；"+(sequenceProgress==null?"单次中断需重新点击开始，不补记完成":"序列已完成 "+sequenceProgress.Completed+" / "+sequenceProgress.Count+"；未完成轮需确认后重做")+"。输出处理以本轮关断记录为准，不证明物理断源/放电。");
                wiring.Checked = calibrationConnected.Checked = false; busy = false; cancellation.Dispose(); cancellation = null; SetBusy(false); FreezeEmergency(); feedbackTrend.SetLive(false); pressureTrend.SetLive(false);
                RefreshRemainingCount();
                if(connectionJournal!=null){connectionJournal.EnterIdle();if(connectionInterrupted)tabs.SelectedTab=connectionPage;}
                if (closeAfterStop) Close();
            }
        }

        private void RefreshRemainingCount()
        {
            completedCount.Text="完成 "+(sequenceProgress==null?0:sequenceProgress.Completed)+" 次";
            remainingCount.Text=sequenceProgress!=null ? "剩余 "+sequenceProgress.Remaining+" 次" : continuous.Checked ? "剩余 "+repeatCount.Value+" 次" : "剩余 — 次";
            remainingCount.ForeColor=sequenceProgress!=null && !sequenceProgress.Running && sequenceProgress.Attempt>0 && sequenceProgress.Remaining>0 ? Color.DarkOrange : Color.Teal;
            pulseTip.SetToolTip(remainingCount,sequenceProgress==null ? "计划次数含首轮；本轮完整采集并正常结束后才减一。" : "当前序列：已完成 "+sequenceProgress.Completed+" / "+sequenceProgress.Count+" 次；剩余包括正在执行或中断未完成的本轮。重连或急停复位不自动开始。");
        }

        private void DisplayResult(FactoryResult result)
        {
            if (result == null || result.Capture == null || result.Settings == null) throw new InvalidDataException("没有本轮完整采集结果。");
            ResetCapturePreview();
            rampStatistics.Text = result.RampStatistics.Describe();
            result.Capture.ValidateCapture();
            var metrics = result.Metrics ?? FactoryAnalysis.Analyze(result); result.Metrics = metrics;
            var detected = metrics.Detected ?? FactoryDetectedWaveforms.Build(result.Capture);
            string prefix = result.Settings.Simulation ? "仿真 · " : "实测 · ";
            combined.Title = prefix + (result.ReadOnlyAcquisition ? "自由采集 · 起点未同步" : "检波"); combined.VoltageReferenceKv = Math.Max(5, result.Settings.TargetKv);
            combined.SetCombined(detected.VoltageKv, detected.PdEnvelopePc, detected.OutputRateHz, detected.DurationSeconds * 1000);
            rawPd.Title = prefix + "定位局放 · 整段";
            rawPd.SetFilteredCapture(result.Capture,result.Capture.Settings.PdCalibrationPcPerV);
            resultSummary.Text = (result.Settings.Simulation ? "仿真演示，不是实机验收" : "实机本轮完整数据") + "  |  目标 " + result.Settings.TargetKv.ToString("0.###") + " kV  |  Pico 实际 " + (result.Capture.PicoRateHz / 1e6).ToString("0.###") + " MS/s / " + result.Capture.Settings.ResolutionBits + " bit 模式 / 量程规格 " + result.Capture.HardwareBits + " bit\n" +
                "最大放电量 " + (metrics.PdClipped ? "削顶/溢出，不可定量" : Format(metrics.MaxPdPc) + " pC") + "  |  振荡峰 " + Format(metrics.OscillationPeakKv) + " kV  |  " +
                (metrics.FrequencyValid ? "频率 " + Format(metrics.FrequencyHz) + " Hz / 电容 " + Format(metrics.CapacitanceNf) + " nF / 介损估计 " + Format(metrics.EstimatedLossTangent, "0.#####") : !result.Capture.Settings.OscillationCircuitConnected ? "频率 / 电容 / 介损：未确认 L/R 回路，未计算" : "频率 / 电容 / 介损：波形质量未通过，不能计算");
            var reference = metrics.ReferenceAnalysis;
            referenceSummary.Text = "标准电容参考 " + Format(result.Settings.ReferenceCapacitanceNf) + " nF / 介损 " + Format(result.Settings.ReferenceLossTangent * 100, "0.####") + "%  |  " +
                (reference != null && reference.Valid ? "反算 L " + Format(reference.InductanceH, "0.######") + " H；等效回路 R " + Format(reference.MeasuredTotalSeriesResistanceOhms) + " Ω；其他串联损耗 " + Format(reference.OtherSeriesResistanceOhms) + " Ω" : "电感反算：无有效结果") + "\n" +
                (reference != null && reference.DecayValid ? "前8完整周期衰减 " + Format(reference.MeasuredDecayPercent) + "%（9个同极性峰）；首零 " + Format(reference.FirstZeroMs) + " ms；模型预期衰减 " + Format(reference.ExpectedDecayPercent) + "%" : "前8周期衰减：数据不足或质量未通过") + "；参考模型诊断，不是介损计量检定。";
            Progress("RESULT", metrics.Detail + "\n" + metrics.RangeDetail + "\n加压比：" + (metrics.PressureRatioValid ? "独立参考核验通过 " + metrics.MeasuredPressureVPerV.ToString("0.###") : "未核验或未通过，设定值不等于实测") +
                "；分压比：" + (metrics.DividerRatioValid ? "独立参考核验通过 " + metrics.MeasuredDividerVPerV.ToString("0.###") : "未核验或未通过") +
                "\n残压：" + (result.ResidualSafe ? "本轮反馈残压核验通过；仍需按现场放电规程确认" : "未通过 / 未确认放电，禁止靠近") + "\n" + result.SafetyDetail);
            if (result.Kind == FactoryRunKind.Calibration && result.Calibration != null)
            {
                var candidate = result.Calibration;
                calibrationRaw.Title = prefix + "校准长段 · 10 kHz高通";
                calibrationRaw.SetFilteredCapture(result.Capture,1);
                if (candidate.AveragePulseV != null && candidate.AveragePulseV.Length > 0 && FactorySettings.Positive(candidate.AveragePulseRateHz))
                    calibrationAverage.Set(candidate.AveragePulseV, candidate.AveragePulseRateHz, 1, candidate.AveragePulseV.Length / candidate.AveragePulseRateHz * 1000);
                else calibrationAverage.ClearWave();
                calibrationSummary.Text = (candidate.Valid ? "有效候选系数：" + candidate.CoefficientPcPerV.ToString("0.###") + " pC/V；结束量程确认后自动带入下一轮。" : "校准未通过；不允许应用系数。") + "\n" + candidate.Detail;
                Progress("CALIBRATION_RESULT", (candidate.Valid ? "校准质量通过" : "校准质量未通过") + "；完整脉冲 " + candidate.CompleteWindowCount +
                    "，频率 " + Format(candidate.PeriodHz) + " Hz，平均峰值 " + Format(candidate.MeanPeakV, "0.######") + " V，噪声 RMS " + Format(candidate.NoiseRmsV, "0.######") +
                    " V，SNR " + Format(candidate.PeakSnr) + "，幅度 CV " + Format(candidate.AmplitudeCv * 100) + "%。\n" + candidate.Detail);
                applyCalibration.Enabled = candidate.Valid && FactorySettings.Positive(candidate.CoefficientPcPerV) && candidate.CoefficientPcPerV >= (double)values["pd"].Minimum && candidate.CoefficientPcPerV <= (double)values["pd"].Maximum;
            }
            else { calibrationSummary.Text = "本轮为加压采集，不生成新的局放校准系数。\n" + metrics.RangeDetail; applyCalibration.Enabled = false; }
            applyRange.Enabled = metrics.RecommendedRangeIndex >= 0 && metrics.RecommendedRangeIndex < range.Items.Count && metrics.RecommendedRangeIndex != range.SelectedIndex;
            status.Text = (result.Settings.Simulation ? "仿真完成" : "本轮完成") + "；记录：" + result.DirectoryPath + (result.ResidualSafe ? "；反馈残压核验通过" : "；未确认放电，禁止靠近设备");
            tabs.SelectedTab = result.Kind == FactoryRunKind.Calibration ? calibrationPage : tabs.TabPages[0];
            UpdateAcceptanceDisplay(); PrepareTfRound(result);
        }

        private void ApplyCalibration(bool automatic = false)
        {
            var candidate = latest == null ? null : latest.Calibration;
            if ((busy && !automatic) || scanning || exporting || (automatic && !calibrationAutoAccepted) || candidate == null || latest.Kind != FactoryRunKind.Calibration || !latest.Completed || !latest.ResidualSafe || latest.Settings == null || latest.Settings.Simulation != (mode.SelectedIndex == 0) || !candidate.Valid || !FactorySettings.Positive(candidate.CoefficientPcPerV)) return;
            if (!latest.Settings.Simulation)
            {
                var device = artChoice.SelectedItem as ArtDeviceIdentity;
                if (device == null || device.Serial != latest.Settings.Input.ArtSerial || (picoChoice.SelectedItem as string) != latest.Settings.Input.PicoSerial) return;
            }
            var present = ReadSettings().Input;
            if (present.PicoCoupling != latest.Capture.Settings.PicoCoupling || present.PicoBandwidth20MHz != latest.Capture.Settings.PicoBandwidth20MHz ||
                present.ResolutionBits != latest.Capture.Settings.ResolutionBits || present.RequestedPicoRateHz != latest.Capture.Settings.RequestedPicoRateHz)
            { status.Text = "Pico通道/带宽/位数/采样率已改变，候选系数不能用于不同采集条件；请重新校准。"; return; }
            if (candidate.CoefficientPcPerV < (double)values["pd"].Minimum || candidate.CoefficientPcPerV > (double)values["pd"].Maximum) return;
            decimal next = (decimal)candidate.CoefficientPcPerV;
            if (latest.Settings.Simulation && !simulationCoefficientBeforeApply.HasValue) simulationCoefficientBeforeApply = values["pd"].Value;
            applyingCalibrationValue = true;
            try { values["pd"].Value = next; } finally { applyingCalibrationValue = false; }
            calibrationOrigin.Text = (latest.Settings.Simulation ? "仿真 · " : "实测 · ") + (automatic ? "自动应用有效校准 " : "手动应用有效校准 ") + latest.Capture.CapturedUtc.AddHours(8).ToString("MM-dd HH:mm:ss") + "；可手动修改。";
            status.Text = "已" + (automatic ? "自动" : "手动") + "应用有效校准系数到下一轮；本轮原始数据及系数未修改。";
            calibrationSummary.Text = "已" + (automatic ? "自动" : "手动") + "应用：" + next.ToString("0.###") + " pC/V；下一轮加压使用，本轮记录不改。\n" + candidate.Detail;
            appliedCalibrationProfile = latest.Settings.Clone(); appliedCalibrationProfile.Input.PdCalibrationPcPerV = (double)next;
            RefreshStoredCalibration(); SaveParameterPreferences();
            applyCalibration.Enabled = false;
        }
        private void ApplyRange()
        {
            if (busy || latest == null || latest.Metrics == null) return;
            try { ReviewRangeForNextRound(latest); }
            catch (Exception ex) { status.Text = "量程建议未应用：" + ex.Message; }
        }
        private async Task Export()
        {
            if (latest == null || busy || scanning || exporting || smokeMode) return;
            using (var dialog = new SaveFileDialog { Title = "导出本轮80k检波数据", Filter = "CSV 数据 (*.csv)|*.csv", FileName = "出厂检波数据.csv" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return; exporting = true; SetBusy(true);
                try { await Task.Run(() => Program.Export(latest, dialog.FileName)); status.Text = "已导出本轮数据，质量与校准有效性以记录为准：" + dialog.FileName; }
                catch (Exception ex) { status.Text = "导出失败：" + ex.Message; }
                finally { exporting = false; SetBusy(false); if (closeAfterStop) Close(); }
            }
        }
        private void OpenRecords()
        {
            string directory = latest != null ? latest.DirectoryPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records");
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory); Process.Start("explorer.exe", "\"" + directory + "\"");
        }
        private static string NewDirectory(string prefix)
        { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records", prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6)); }
        private void SetBusy(bool enabled)
        {
            capturePosition.Enabled=!enabled;
            openCapture.Enabled = !enabled;
            start.Enabled = calibrationStart.Enabled = mode.Enabled = targetKv.Enabled = parametersPage.Enabled = calibrationPage.Enabled = openFolder.Enabled = !enabled;
            if (acceptancePage != null) acceptancePage.Enabled = !enabled;
            stop.Enabled = (enabled || labelProcessing) && !exporting; export.Enabled = !enabled && latest != null; discover.Enabled = !enabled && mode.SelectedIndex == 1;
            if (enabled) applyCalibration.Enabled = applyRange.Enabled = false; RefreshMode();
            UpdatePostProcessingButtons();
            RefreshStartAvailability();
            if (!enabled) UpdateAcceptanceDisplay();
        }
        private void ResetStages()
        { ++timelineGeneration;ResetCapturePreview(); stageSettings=null; roundFailureDetail=null; residualStageStarted=false; rampStatistics.Text = "HV UP：未执行"; shutdownDiagnostic.ClearRecord(); feedbackTrend.ClearTrend(); pressureTrend.ClearTrend(); foreach (string key in StageKeys) { stageDetails[key] = "尚未执行；没有本轮硬件证据。"; SetStage(key, "未执行", Color.White, Color.Gray); } }
        private static string Format(double value, string format = "0.###")
        { return FactorySettings.Finite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "无有效值"; }
        private void MarkActiveErrors(string detail)
        { foreach (string key in StageKeys) if (stages[key].Text.IndexOf("● 运行", StringComparison.Ordinal) >= 0) Progress("STEP_" + key + "_ERROR", detail); }
        private void SetStage(string key, string state, Color background, Color foreground)
        { Button button; if (stages.TryGetValue(key, out button)) { button.Text = button.Tag + "\r\n● " + state; button.BackColor = background; button.ForeColor = foreground; var stage = button as StageButton; if (stage != null) stage.StateText = state; button.Invalidate(); } }
        internal void Progress(string stage, string detail)
        {
            if (InvokeRequired)
            {
                try { if (!IsDisposed && !Disposing) BeginInvoke(new Action(() => Progress(stage, detail))); }
                catch (InvalidOperationException) { if (!IsDisposed && !Disposing) throw; }
                return;
            }
            string key = stage ?? "", state = "运行";
            if(ObserveConnection(key,detail,true))return;
            if(connectionJournal!=null && workerConnectionOwned)
            {
                if(key.StartsWith("STEP_") && key.EndsWith("_RUNNING"))
                {string phase=key.Substring(5,key.Length-13);connectionJournal.Progress=stages.ContainsKey(phase)?stages[phase].Tag+" ("+key+")":key;}
                if(key=="STEP_HV_OFF_PASSED" || key=="STEP_HV_OFF_ERROR" || key=="SAFETY_ERROR" || key=="STEP_COMPLETE_ERROR")LogConnectionResult(key+"："+detail);
            }
            if (sequenceProgress != null) sequenceProgress.Observe(key);
            if (key == "CAPTURE_READY_FILE" || key == "FAILED_CAPTURE_FILE") RequestCapturePreview(detail);
            if (key == "FEEDBACK_TREND") { if(!activeArtLinkFault && !activePicoLinkFault){feedbackTrend.Add(detail); pressureTrend.Add(detail);} return; }
            if (key == "FAULT_RESIDUAL" || key == "FAULT_RESIDUAL_END")
            { resultSummary.Text = (displayedPreview == null ? "" : PreviewSafety + "\r\n") + detail; if (displayedPreview == null) tabs.SelectedTab = monitorPage; }
            if (key == "STEP_DISCHARGE_RUNNING")
            { residualStageStarted=true; stages["DISCHARGE"].Visible = true; }
            if (key == "RAMP_PROGRESS" || key == "RAMP_SUMMARY" || key == "TARGET_HOLD") rampStatistics.Text = detail;
            if(key=="SHUTDOWN_DIAG_FILE") {shutdownDiagnostic.LoadRecord(detail);var pendingTimeline=LoadProcessTimeline(Path.GetDirectoryName(detail),pulseAnalysisRound);}
            if (key == "HV_CONDITION" || key == "RESIDUAL" || key == "INITIAL_FEEDBACK" || key == "HV_ZERO_FEEDBACK" || key == "FEEDBACK_CONFIG")
                resultSummary.Text = displayedPreview != null ? PreviewSafety + "\r\n" + detail :
                    (key == "HV_CONDITION" ? "当前升压条件与反馈" : key == "HV_ZERO_FEEDBACK" ? "HV ON后零输出检查（AO=0）" : key == "FEEDBACK_CONFIG" ? "AI配置说明（不代表已采集）" : "当前残压检查") + "\r\n" + detail + "\r\n完整采集数据就绪后显示；不以残压通过作为显示条件。";
            if (roundFailureDetail == null && key.StartsWith("STEP_", StringComparison.Ordinal) && key.EndsWith("_ERROR", StringComparison.Ordinal))
            {roundFailureDetail = detail;resultSummary.Text="本轮停止原因："+detail;}
            emergencyNotice.Observe(key, busy && stageSettings != null && stageSettings.TargetKv > 0);
            if (key == "EMERGENCY_RELEASED" || key == "EMERGENCY_ACTIVE") UpdateEmergency(key == "EMERGENCY_RELEASED", detail);
            else if (key == "SAFETY_ERROR" || key == "STEP_SAFE_ERROR" || key == "STEP_COMPLETE_ERROR") MarkEmergencyUnknown();
            if (key.StartsWith("STEP_", StringComparison.Ordinal)) key = key.Substring(5);
            foreach (string suffix in new[] { "_RUNNING", "_PASSED", "_ERROR", "_FAILED", "_SKIPPED", "_CANCELED", "_UNVERIFIED" })
                if (key.EndsWith(suffix, StringComparison.Ordinal)) { key = key.Substring(0, key.Length - suffix.Length); state = suffix == "_PASSED" ? "通过" : suffix == "_SKIPPED" ? "跳过" : suffix == "_RUNNING" ? "运行" : suffix == "_UNVERIFIED" ? "待验证" : "错误"; break; }
            if (key == "TRG_OUTPUT") key = "OPTICAL"; if (key == "RELEASE") key = "DISCHARGE";
            if (key == "HV_CONDITION") key = "HV_UP";
            if (key == "RESIDUAL") key = "DISCHARGE";
            if (key == "INITIAL_FEEDBACK" || key == "FEEDBACK_CONFIG") key = "SAFE";
            if (key == "HV_ZERO_FEEDBACK") key = "HV_DELAY";
            if (key == "WAIT") key = "WAIT_TRIGGER";
            if (key == "SAFETY" && state == "错误") key = "DISCHARGE";
            if (stages.ContainsKey(key))
            {
                stageDetails[key] = (stageDetails[key] == "尚未执行；没有本轮硬件证据。" ? "" : stageDetails[key] + "\r\n") + DateTime.Now.ToString("HH:mm:ss.fff") + " " + state + " · " + detail;
                SetStage(key, state, state == "通过" ? Color.FromArgb(196, 232, 208) : state == "错误" ? Color.FromArgb(255, 210, 210) : state == "跳过" ? Color.FromArgb(255, 236, 158) : Color.FromArgb(203, 228, 247), Color.DarkSlateGray);
            }
            if (diagnostics.TextLength > 120000) diagnostics.Text = diagnostics.Text.Substring(diagnostics.TextLength - 80000);
            diagnostics.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + stage + "  " + detail + Environment.NewLine);
            status.Text = roundFailureDetail == null ? detail : "本轮异常：" + roundFailureDetail + "（收尾不改变原始失败原因）";
            if (emergencyNotice.Active) ShowLatchedEmergency();
            if (stage == "STEP_COMPLETE_UNVERIFIED" && stageSettings != null && stageSettings.UseCaptureOnlyEnd && roundFailureDetail == null)
            {
                // Neutral end-of-acquisition, never a safety PASS. The full UNVERIFIED
                // event remains in diagnostics and the persisted result stays false.
                SetStage("COMPLETE", "已结束", Color.Gainsboro, Color.DarkSlateGray);
                status.Text = "";
            }
            RenderConnection();
        }
        private void ShowStage(string key)
        {
            // Non-modal details keep the main Stop button reachable during a run.
            var dialog = new Form { Text = "步骤证据 · " + stages[key].Tag, Size = new Size(850, 480), MinimumSize = new Size(650, 360), StartPosition = FormStartPosition.CenterParent, Icon = Icon, Font = Font };
            dialog.Controls.Add(new TextBox { Text = stageDetails[key], ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill });
            dialog.FormClosed += (s, e) => dialog.Dispose(); dialog.Show(this);
        }

        // Synthetic UI smoke only. Never calls discovery/worker/export or any native driver.
        internal async Task Smoke(string imagePath)
        {
            smokeMode = true; string full = Path.GetFullPath(imagePath); Directory.CreateDirectory(Path.GetDirectoryName(full));
            try
            {
                if (mode.SelectedIndex != 0 || values["pd"].Value != 2100 || values["duration"].Value != 60 || resolution.SelectedIndex != 2 || range.SelectedIndex != 10 || wiring.Checked || calibrationConnected.Checked || terminal.Enabled || terminal.Items.Count != 1 || values["feedbackv"].Value != 5)
                    throw new InvalidOperationException("默认仿真、采集、安全确认或校准参数错误。");
                var settings = ReadSettings(); if (settings.Input.ArtTerminal != 10083 || settings.FeedbackFullScaleV != 5 || !settings.Input.OscillationCircuitConnected || oscillationCircuit.Checked) throw new InvalidOperationException("反馈、满量程或仿真振荡指标参数未正确传递。");
                if(settings.TargetKv!=5 || !settings.DividerOnlyFeedback || !settings.UsePostHvVmonStartup || settings.StartupVmonLimitV!=500 || settings.ZeroBackgroundLimitV!=500 || settings.SafeResidualV!=50 ||
                    settings.MaximumKv!=30 || settings.MaximumFeedbackKv!=30 || settings.MaximumAoV!=5 || settings.TargetHoldChecks!=0 || settings.TargetTolerancePercent!=2)
                    throw new InvalidOperationException("R23完整界面未采用两路启动/背景500 V / 采后50 V分阶段设置。");
                await SmokeIntegratedKernel(Path.Combine(Path.GetDirectoryName(full),"integrated"));
                if (knownCharge.Items.Count != 15 || settings.Input.CalibrationPulsePc != 100) throw new InvalidOperationException("校准档位或默认电量错误。");
                knownCharge.SelectedIndex = 13; if (ReadSettings().Input.CalibrationPulsePc != 20) throw new InvalidOperationException("20pC档位未正确传递。");
                knownCharge.SelectedIndex = 0; if (ReadSettings().Input.CalibrationPulsePc != 200000) throw new InvalidOperationException("200nC档位单位错误。");
                knownCharge.SelectedIndex = FactoryCalibrationLevels.DefaultIndex;
                if (settings.ReferenceCapacitanceNf != 200 || Math.Abs(settings.ReferenceLossTangent - .0024) > 1e-12 ||
                    settings.RangeEvaluationCycleCount != 8 || !settings.VerificationContextRecorded || settings.VerificationLoadCapacitanceNf != 200)
                    throw new InvalidOperationException("标准电容、介损、量程周期或采集时核验工况快照错误。");
                settings.Simulation = true; settings.Input.ArtSerial = "SIM-ART"; settings.Input.PicoSerial = "SIM-PICO";
                settings.TargetKv = 10; targetKv.Value = 10;
                var capture = new BenchCapture { Settings = settings.Input, Simulation = true, CapturedUtc = DateTime.UtcNow, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO", Runtime = "UI SMOKE · synthetic only", ArtRateHz = 80000,
                    PicoRateHz = settings.Input.RequestedPicoRateHz, PicoFullScaleV = BenchSettings.Ranges[settings.Input.RangeIndex], MaximumAdc = 32767, HardwareBits = 14,
                    VoltageRaw = new double[settings.Input.ExpectedArtCount], PdAdc = new short[settings.Input.RequestedPicoCount] };
                for (int i = 0; i < capture.VoltageRaw.Length; i++) { double t = i / capture.ArtRateHz; capture.VoltageRaw[i] = 10000 / settings.Input.DividerVPerV * Math.Exp(-70 * t) * Math.Sin(2 * Math.PI * 600 * t); }
                for (int center = 500000; center < capture.PdAdc.Length - 100; center += 500000)
                    for (int i = center - 120; i <= center + 120; i++) capture.PdAdc[i] = (short)(1000 * Math.Exp(-.5 * Math.Pow((i - center) / (12.0 + center / 500000 * 1.5), 2)));
                latest = new FactoryResult { Settings = settings, Kind = FactoryRunKind.Pressure, Capture = capture, Completed = true, ResidualSafe = true, SafetyDetail = "仅仿真：没有物理输出或残压测量", DirectoryPath = "UI-SMOKE-ONLY" };
                foreach (string key in StageKeys) Progress("STEP_" + key + "_PASSED", "仅仿真状态；没有驱动访问。");
                Progress("STEP_AO_ZERO_SKIPPED", "用于显示黄色状态，未操作 AO。"); Progress("STEP_SAFE_ERROR", "用于显示浅红错误状态，不是实机故障。"); Progress("STEP_ARM_RUNNING", "用于显示蓝色运行状态。");
                DisplayResult(latest); if (!latest.Metrics.FrequencyValid || Math.Abs(latest.Metrics.FrequencyHz - 600) > 2) throw new InvalidOperationException("仿真 600 Hz 振荡指标未启用或结果错误。");
                await Task.Delay(80);
                if (!combined.HasWaveform || combined.Width < 300 || combined.Height < 180 || rawPd.SourceSampleCount != capture.PdAdc.Length ||
                    Math.Abs(rawPd.VisibleStartMs) > 1e-8 || Math.Abs(rawPd.VisibleEndMs - 60) > 1e-8 || targetKv.Font.Size < 24)
                    throw new InvalidOperationException("组合波形、原始整段视窗或大字体电压布局未正确显示。");
                SaveImage(full);
                string stem = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full));
                double roiStart=rawPd.SelectionStartMs,roiEnd=rawPd.SelectionEndMs;var zoomPlot=rawPd.PlotBounds;
                rawPd.ApplyBoxZoomForTest(new Point(zoomPlot.Left+zoomPlot.Width/3,zoomPlot.Top+zoomPlot.Height/5),new Point(zoomPlot.Left+zoomPlot.Width*2/3,zoomPlot.Top+zoomPlot.Height*4/5));
                if(rawPd.VisibleStartMs<19 || rawPd.VisibleEndMs>41 || rawPd.SelectionStartMs!=roiStart || rawPd.SelectionEndMs!=roiEnd)
                    throw new InvalidOperationException("定位框选放大未生效或误改脉冲选区。");
                rawPd.ChargeRulerMaximumPc=1000;SaveImage(stem+".location-box-zoom.png");rawPd.ChargeRulerMaximumPc=0;rawPd.ResetView();
                continuous.Checked=true;repeatCount.Value=15;RefreshRemainingCount();
                if(remainingCount.Text!="剩余 15 次")throw new InvalidOperationException("连续计划剩余次数未显示。");
                sequenceProgress=new FactorySequenceProgress(settings,15);sequenceProgress.Begin(settings,15);sequenceProgress.Starting(settings);sequenceProgress.Captured();RefreshRemainingCount();
                if(remainingCount.Text!="剩余 14 次")throw new InvalidOperationException("完成一轮未减剩余次数。");
                sequenceProgress.Starting(settings);sequenceProgress.Observe("EMERGENCY_ACTIVE");sequenceProgress.End();RefreshRemainingCount();
                if(remainingCount.Text!="剩余 14 次" || remainingCount.ForeColor!=Color.DarkOrange)throw new InvalidOperationException("中断轮不得扣次数。");
                SaveImage(stem+".remaining-count.png");sequenceProgress=null;continuous.Checked=false;repeatCount.Value=1;RefreshRemainingCount();
                if (!tfConfirm.Enabled || !saveData.Enabled) throw new InvalidOperationException("完整结束后TF确认/保存未开放。");
                currentTf = FactoryTfAnalysis.Extract(latest, (double)tfThresholdPc.Value); tfSession.Add(currentTf); tfSession.Add(currentTf); RefreshTfView();
                await labelTask;
                if (currentTf.Pulses.Count != 14 || !currentTf.Pulses.TrueForAll(p => p.Valid) || tfSession.RoundCount != 1) throw new InvalidOperationException("UI真实采样率TF或累计去重失败。");
                await Task.Delay(80); SaveImage(stem + ".two-plots.png");
                if (!combined.Visible || !rawPd.Visible || !selectedPulse.Visible || !tfPlot.Visible || pulseSplit.Panel1Collapsed || pulseSplit.Panel2Collapsed ||
                    combined.PointToScreen(Point.Empty).X >= rawPd.PointToScreen(Point.Empty).X || combined.PointToScreen(Point.Empty).Y != rawPd.PointToScreen(Point.Empty).Y ||
                    selectedPulse.PointToScreen(Point.Empty).Y <= rawPd.PointToScreen(Point.Empty).Y || selectedPulse.PointToScreen(Point.Empty).X >= tfPlot.PointToScreen(Point.Empty).X ||
                    upperWaveSplit.IsSplitterFixed || pulseSplit.IsSplitterFixed || pulseSplit.SplitterWidth < 6 || !selectedPulse.HasWaveform || !tfPlot.SelectedMarkerDrawn)
                    throw new InvalidOperationException("上方检波/定位、下方脉冲/TF或选中标记没有同时显示。");
                pulseNext.PerformClick(); await Task.Delay(80);
                if (pulseSelection.Index != 1 || !FactoryPulseSelection.Same(tfPlot.SelectedPoint, pulseSelection.Current)) throw new InvalidOperationException("下一条没有联动TF选中点。");
                pulsePrevious.PerformClick(); if (pulseSelection.Index != 0 || pulsePrevious.Enabled) throw new InvalidOperationException("上一条或首条边界错误。");
                var chosen = TfPoints()[6]; tfPlot.ActivateAt(Point.Round(tfPlot.PointPixel(chosen))); await Task.Delay(80);
                if (pulseSelection.Index != 6 || selectedPulse.SourceSampleCount != chosen.Pulse.EndSample - chosen.Pulse.StartSample + 1 || !tfPlot.SelectedMarkerDrawn)
                    throw new InvalidOperationException("TF双击命中路径未选择对应原始脉冲。");
                double pulseEnd = selectedPulse.VisibleEndMs; selectedPulse.SetVisibleTime(pulseEnd * .2, pulseEnd * .7); await Task.Delay(80); SaveImage(stem + ".pulse-zoom.png");
                if (!FactoryPulseSelection.Same(tfPlot.SelectedPoint, chosen) || !tfPlot.SelectedMarkerDrawn || selectedPulse.VisibleStartMs <= 0)
                    throw new InvalidOperationException("调整脉冲图视窗后TF选中标记丢失。");
                selectedPulse.SetVisibleTime(pulseEnd * .3, pulseEnd * .8); selectedPulse.ResetView();
                if (pulseSelection.Index != 6 || !FactoryPulseSelection.Same(tfPlot.SelectedPoint, chosen)) throw new InvalidOperationException("平移/复位改变了TF选中项。");
                ShowTfPulse(TfPoints()[13]); if (pulseNext.Enabled || !pulsePrevious.Enabled) throw new InvalidOperationException("末条导航边界错误。"); ShowTfPulse(chosen);
                await Task.Delay(80); SaveImage(stem + ".selected-pulse.png");
                await SmokeAnnotations(stem, chosen);
                int monitorDistance=pressureWaveSplit.SplitterDistance, monitorWidth=pressureTrend.Width, remainingWidth=upperWaveSplit.Width;
                pressureWaveSplit.SplitterDistance+=40;await Task.Delay(80);
                if(pressureTrend.Width<=monitorWidth || upperWaveSplit.Width>=remainingWidth || !pressureTrend.Visible)
                    throw new Exception("R28 three-pane splitter did not resize monitor and capture panes");
                pressureWaveSplit.SplitterDistance=monitorDistance;
                int upperDistance = upperWaveSplit.SplitterDistance, upperCombinedWidth = combined.Width, upperRawWidth = rawPd.Width;
                upperWaveSplit.SplitterDistance += 70; await Task.Delay(80);
                if (combined.Width <= upperCombinedWidth || rawPd.Width >= upperRawWidth) throw new InvalidOperationException("上方分隔栏未联动两图宽度。"); upperWaveSplit.SplitterDistance = upperDistance;
                int rowDistance = waveRowsSplit.SplitterDistance, upperHeight = rawPd.Height, lowerHeight = selectedPulse.Height;
                waveRowsSplit.SplitterDistance += 30; await Task.Delay(80);
                if (waveRowsSplit.IsSplitterFixed || rawPd.Height <= upperHeight || selectedPulse.Height >= lowerHeight || !FactoryPulseSelection.Same(tfPlot.SelectedPoint, chosen))
                    throw new InvalidOperationException("上下分隔栏未调整两组图高度，或破坏了脉冲选择。"); waveRowsSplit.SplitterDistance = rowDistance;
                int priorDistance = pulseSplit.SplitterDistance, priorPulseWidth = selectedPulse.Width, priorTfWidth = tfPlot.Width;
                pulseSplit.SplitterDistance = priorDistance + 80; await Task.Delay(80); SaveImage(stem + ".divider-moved.png");
                if (selectedPulse.Width <= priorPulseWidth || tfPlot.Width >= priorTfWidth || rawPd.SourceSampleCount != capture.PdAdc.Length || currentTf.Pulses.Count != 14 || pulseSelection.Index != 6)
                    throw new InvalidOperationException("分隔栏未调整两图宽度，或改变了原始数据/TF结果。"); pulseSplit.SplitterDistance = priorDistance;
                tfScope.SelectedIndex = 1; await labelTask; await Task.Delay(80); SaveImage(stem + ".cumulative.png");
                if (!rawPd.Visible || !tfPlot.Visible || !combined.Visible || !selectedPulse.HasWaveform || pulseSelection.Index != 6 || !tfPlot.SelectedMarkerDrawn)
                    throw new InvalidOperationException("累计TF未匹配原始脉冲、丢失选择或隐藏上方图。"); tfScope.SelectedIndex = 0; await labelTask;
                OpenTfWindow(); await Task.Delay(80);
                foreach (Form owned in OwnedForms) if (owned.Text.Contains("TF脉冲统计"))
                { using (var bitmap = new Bitmap(owned.Width, owned.Height)) { owned.DrawToBitmap(bitmap, new Rectangle(Point.Empty, owned.Size)); bitmap.Save(stem + ".tf-expanded.png"); } owned.Close(); }
                using (var saveDialog = new FactoryDataSaveDialog(Icon, "设备001", @"D:\出厂数据", "采集身份：ART SIM-ART；Pico SIM-PICO（仅界面测试）"))
                { saveDialog.Show(this); await Task.Delay(80); using (var bitmap = new Bitmap(saveDialog.Width, saveDialog.Height)) { saveDialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, saveDialog.Size)); bitmap.Save(stem + ".save-dialog.png"); } saveDialog.Close(); }
                busy = true; SetBusy(true); MovePulse(1); tfPlot.ActivateAt(Point.Round(tfPlot.PointPixel(TfPoints()[0])));
                if (saveData.Enabled || tfConfirm.Enabled || tfHistory.Enabled || pulsePrevious.Enabled || pulseNext.Enabled || selectedPulse.Enabled || tfPlot.Enabled || pulseSelection.Index != 6)
                    throw new InvalidOperationException("运行/保存时处理入口或脉冲导航未关闭。"); busy = false; SetBusy(false);
                tabs.SelectedTab = parametersPage; await Task.Delay(80); SaveImage(stem + ".parameters.png"); tabs.SelectedIndex = 4; await Task.Delay(80); SaveImage(stem + ".diagnostics.png");
                tabs.SelectedTab = acceptancePage; await Task.Delay(80); SaveImage(stem + ".acceptance.png");
                await SaveScrolledLeft(acceptancePage, stem + ".acceptance-bottom.png");
                int combinedDraws = combined.StaticRenderCount, rawDraws = rawPd.StaticRenderCount, pulseDraws = selectedPulse.StaticRenderCount, tfDraws = tfPlot.RenderCount;
                Size = MinimumSize; tabs.SelectedIndex = 0; await Task.Delay(80); SaveImage(stem + ".minimum.png");
                VerifyMainToolbarVisible();
                File.WriteAllText(stem+".layout.txt",DescribeLayout(tabs.SelectedTab,0));
                if (combined.StaticRenderCount <= combinedDraws || rawPd.StaticRenderCount <= rawDraws || selectedPulse.StaticRenderCount <= pulseDraws || tfPlot.RenderCount <= tfDraws ||
                    !combined.Visible || !rawPd.Visible || !selectedPulse.Visible || !tfPlot.Visible || !tfPlot.SelectedMarkerDrawn || tfPlot.Height < 100 || combined.PlotBounds.Height < 45 || rawPd.PlotBounds.Height < 45 || selectedPulse.PlotBounds.Height < 45 || pulseSplit.Panel1.Width < 320 || pulseSplit.Panel2.Width < 320)
                    throw new InvalidOperationException("最小窗口中四图未同时实际绘制，或绘图区过小：检波=" + combined.PlotBounds.Height + "，TF=" + tfPlot.Height + "，定位=" + rawPd.PlotBounds.Height + "，脉冲=" + selectedPulse.PlotBounds.Height);
                int measuredRange = capture.Settings.RangeIndex; double measuredCoefficient = capture.Settings.PdCalibrationPcPerV;
                latest.Kind = FactoryRunKind.Calibration;
                latest.Calibration = new CalibrationResult { Valid = false, CoefficientPcPerV = 2300, Detail = "UI 假测试：无有效脉冲，禁止应用" };
                DisplayResult(latest); if (applyCalibration.Enabled) throw new InvalidOperationException("无效校准被允许应用。");
                calibrationAutoAccepted = true; ApplyCalibration(true); if (values["pd"].Value != 2100) throw new InvalidOperationException("无效系数改变了下一轮参数。");
                latest.Calibration = new CalibrationResult { Valid = true, CoefficientPcPerV = 2300, Detail = "UI 假测试：质量通过，测试结束量程确认后自动应用" };
                DisplayResult(latest); if (!applyCalibration.Enabled || values["pd"].Value != 2100) throw new InvalidOperationException("候选系数被自动应用，或有效按钮未开启。");
                calibrationAutoAccepted = false; ApplyCalibration(true); if (values["pd"].Value != 2100) throw new InvalidOperationException("量程确认未结束即自动应用系数。"); calibrationAutoAccepted = true;
                coupling.SelectedIndex = 1; ApplyCalibration(true); if (values["pd"].Value != 2100) throw new InvalidOperationException("不同通道条件的候选系数被应用。"); coupling.SelectedIndex = 0;
                latest.Calibration.AveragePulseRateHz = capture.PicoRateHz; latest.Calibration.AveragePulseV = new double[1000];
                for (int i = 0; i < 1000; i++) latest.Calibration.AveragePulseV[i] = .047 * Math.Exp(-Math.Pow((i - 400) / 18.0, 2));
                DisplayResult(latest); ApplyCalibration(true); await Task.Delay(80); SaveImage(stem + ".calibration.png");
                await SaveScrolledLeft(calibrationPage, stem + ".calibration-bottom.png");
                if (values["pd"].Value != 2300 || capture.Settings.PdCalibrationPcPerV != measuredCoefficient)
                    throw new InvalidOperationException("自动应用校准没有保留本轮原始系数。");
                var sameProfile = latest.Settings.Clone(); RequireAppliedCalibrationProfile(sameProfile);
                sameProfile.Input.PicoCoupling = sameProfile.Input.PicoCoupling == 1 ? 0 : 1;
                bool mismatchedProfileBlocked = false;
                try { RequireAppliedCalibrationProfile(sameProfile); } catch (InvalidOperationException) { mismatchedProfileBlocked = true; }
                if (!mismatchedProfileBlocked) throw new InvalidOperationException("已应用系数的采集条件改变后未要求重新校准。");
                values["pd"].Value = 2350; if (appliedCalibrationProfile != null || !calibrationOrigin.Text.Contains("手动设置")) throw new InvalidOperationException("手动修改系数未取消自动校准来源声明。"); ApplyCalibration(true);
                var rangeQuality = FactoryRangeReview.Evaluate(latest);
                if(!rangeQuality.WindowValid || !rangeQuality.Change)throw new Exception("Smoke fixture must produce a real range suggestion");
                latest.Metrics.RecommendedRangeIndex = rangeQuality.RecommendedIndex;
                using(var dialog=new FactoryNextRangeDialog(latest,rangeQuality,range.SelectedIndex,Icon))
                {
                    dialog.Show(this);await Task.Delay(80);
                    using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,dialog.Size));bitmap.Save(stem+".range-popup.png");}
                    if(!dialog.ApplyButton.Enabled)throw new Exception("Valid range recommendation disabled");
                    dialog.ApplyButton.PerformClick();if(!dialog.ApplyNext)throw new Exception("Range popup did not select next-round adjustment");
                }
                ApplyRangeDecision(latest,FactoryNextRangeReview.Decide(latest,rangeQuality,range.SelectedIndex,true));
                if (range.SelectedIndex != latest.Metrics.RecommendedRangeIndex || capture.Settings.RangeIndex != measuredRange)
                    throw new InvalidOperationException("下一轮推荐量程改写了本轮量程。");
                oscillationCircuit.Checked = true;
                mode.SelectedIndex = 1; // smokeMode prevents read-only discovery as well.
                if (values["pd"].Value != (decimal)measuredCoefficient || range.SelectedIndex != measuredRange || latest != null || applyCalibration.Enabled || applyRange.Enabled || oscillationCircuit.Checked || ReadSettings().Input.OscillationCircuitConnected)
                    throw new InvalidOperationException("仿真候选/推荐量程或回路确认被带入实机模式。");
                mode.SelectedIndex = 0;
                File.WriteAllText(full + ".txt", "FACTORY_UI_SMOKE=PASS\nHARDWARE=none\nDRIVERS=none\nDEFAULT_MODE=simulation\nPD_AND_VOLTAGE=shared80k-plot\nRAW_LOCATION_INPUT=whole-segment\nLOCATION_DISTANCE_VALIDATION=none\nPICO_RATE=125MS/s\nBITS=14\nDURATION_MS=60\nPD_COEFFICIENT=2100\nFEEDBACK_FULL_SCALE_V=5\nAI_REFERENCE=RSE-AGND-fixed\nOPTICAL_HOLD_MS=1000\nP02=low-protection-only\nCONFIRMATIONS_PERSISTED=false\nINVALID_CALIBRATION_APPLY=blocked\nDIFFERENT_CHANNEL_PROFILE_APPLY=blocked\nPOST_APPLY_PROFILE_CHANGE=blocked\nVERIFICATION_CONTEXT=frozen-at-acquisition\nVALID_CALIBRATION_APPLY=automatic-after-final-round-acceptance\nMANUAL_COEFFICIENT=editable-with-origin-label\nCALIBRATION_LEVELS=100-compatible-15-levels-default100pC\nTF=14-valid-synthetic-pulses-native125MS/s\nTF_CONFIRM=required\nTF_ACCUMULATION=no-duplicate-source\nTF_MINIMUM_LAYOUT=PASS\nSAVE_DIALOG=rendered-no-action\nBUSY_POSTPROCESSING=blocked\nNEXT_RANGE_APPLY=no-pressure-command\nRAW_METADATA=unchanged\nSIMULATION_DERIVED_VALUES_TO_HARDWARE=blocked\nCIRCUIT_CONFIRMATION_ON_MODE_CHANGE=cleared\nSIMULATION_FREQUENCY_600HZ=PASS\n", Encoding.UTF8);
                File.AppendAllText(full + ".txt", "UPPER_PLOTS=detected-and-whole-raw-left-right\nLOWER_PLOTS=selected-pulse-and-TF-left-right\nNESTED_DIVIDERS=upper-lower-and-two-left-right-draggable\nDIVIDER_DATA_MUTATION=none\nMINIMUM_FOUR_PLOTS_RENDER=PASS\nPULSE_PREVIOUS_NEXT=linked-bounded\nTF_DOUBLE_CLICK_HIT_PATH=linked-native-window\nPULSE_ZOOM_PAN_RESET=TF-selection-retained\nCUMULATIVE_TF_PULSE_SOURCE_MATCH=PASS\nSTALE_TF_AND_BUSY_NAVIGATION=blocked\n", Encoding.UTF8);
            }
            finally { smokeMode = false; }
        }
        private async Task SaveScrolledLeft(TabPage page, string path)
        {
            var grid = page.Controls[0] as TableLayoutPanel;
            var scroll = grid == null ? null : grid.GetControlFromPosition(0, 0) as Panel;
            if (scroll == null || !scroll.AutoScroll) throw new InvalidOperationException("校准/核验左侧缺少滚动宿主。");
            scroll.AutoScrollPosition = new Point(0, scroll.DisplayRectangle.Height);
            await Task.Delay(80); SaveImage(path); scroll.AutoScrollPosition = Point.Empty;
        }
        private void SaveImage(string path)
        { PerformLayout(); Update(); using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path); } }
        private static string DescribeLayout(Control c,int depth)
        {
            string text=new string(' ',depth*2)+c.GetType().Name+" Bounds="+c.Bounds+" Min="+c.MinimumSize+" Preferred="+c.GetPreferredSize(Size.Empty)+" Dock="+c.Dock+Environment.NewLine;
            if(depth<6)foreach(Control child in c.Controls)text+=DescribeLayout(child,depth+1);
            return text;
        }
    }
}
