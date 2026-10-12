using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.Owts.Acquisition.Live;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 雄新电力 · 雄新 OWTS 出厂加压测试 GUI。
    /// 编排 FactoryPressApi：Connect → Calibrate → Press → Stop → Release。
    /// 清理失败只写日志/状态，不 MessageBox 阻塞。
    /// </summary>
    public sealed class FactoryPressForm : Form
    {
        FactoryPressApi _api;
        CancellationTokenSource _workCts;
        bool _busy;
        /// <summary>急停显示去抖：0空闲 1解除 2按下 3锁存 4未知；-1=本帧无新样保持</summary>
        int _estopStableKind;
        int _estopPendingKind = -1;
        int _estopPendingHits;
        const int EStopDebounceHits = 2; // 缩短：连续 2 次一致即改色（原 4×750ms 过慢）
        AcquisitionStatusSnapshot _lastGoodSnap;

        readonly Label _lblConnected = MakeState();
        readonly Label _lblCalibrated = MakeState();
        readonly Label _lblReady = MakeState();
        readonly Label _lblEStop = MakeState();
        readonly Label _lblHvOn = MakeState();
        readonly Label _lblHvUp = MakeState();
        readonly Label _lblHvOff = MakeState();
        readonly Label _lblCapture = MakeState();
        readonly Label _lblPhase = new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Padding = new Padding(8, 8, 8, 8),
            Text = "阶段：未连接"
        };

        /// <summary>局放校准系数 pC/V；优先读 {AppBase}/config/pd-calibration.json；无效默认 2000。</summary>
        readonly NumericUpDown _numPdCal = new NumericUpDown
        {
            DecimalPlaces = 3,
            Minimum = 0,
            Maximum = 1000000,
            Increment = 1,
            Value = 2000, // 默认 2000；启动/加压优先套用已保存系数
            Width = 110,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>校准器注入放电量（pC）档位选择。</summary>
        readonly ComboBox _cboInjectedPc = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        readonly Button _btnSavePdCal = new Button
        {
            Text = "保存校准系数",
            Width = 120,
            Height = 28,
            Font = new Font("Microsoft YaHei UI", 9f)
        };
        readonly Label _lblPdCalHint = new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.DimGray,
            Text = "由校准 exe 写入；加压仅加载/保存系数"
        };
        readonly NumericUpDown _numGain = new NumericUpDown
        {
            DecimalPlaces = 0,
            Minimum = 1,
            Maximum = 100000,
            Value = 6000,
            Width = 100
        };
        readonly TextBox _txtTargets = new TextBox
        {
            Text = "1,2",
            Width = 160,
            Font = new Font(FontFamily.GenericMonospace, 9.5f)
        };
        readonly TextBox _txtOut = new TextBox
        {
            Width = 360,
            Font = new Font(FontFamily.GenericMonospace, 9f)
        };
        readonly CheckBox _chkSuppressSplash = new CheckBox
        {
            Text = "抑制 Pico 启动窗（不启 PicoScope.exe）",
            Checked = true,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f)
        };

        readonly Button _btnConnect = new Button { Text = "连接", Width = 100, Height = 32 };
        readonly Button _btnCalibrate = new Button { Text = "校准采集", Width = 110, Height = 32 };
        /// <summary>空闲=开始加压；加压中=停止（同一切换按钮）。</summary>
        readonly Button _btnPressToggle = new Button { Text = "开始加压", Width = 120, Height = 32 };
        readonly Button _btnRelease = new Button { Text = "释放会话", Width = 110, Height = 32 };
        readonly NumericUpDown _numPressRounds = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 999,
            Value = 1,
            Width = 70,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>AO 步进间隔 ms（RampStepDelayMilliseconds；下限 1）。</summary>
        readonly NumericUpDown _numRampStepDelay = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 200,
            Value = 1,
            Width = 60,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>AO 步长 V（RampStepAoVolts；Gain=6000 时 0.01≈工程 60V/步）。</summary>
        readonly NumericUpDown _numRampStepAo = new NumericUpDown
        {
            DecimalPlaces = 3,
            Minimum = 0.002m,
            Maximum = 0.1m,
            Increment = 0.001m,
            Value = 0.01m,
            Width = 70,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        bool _lastPicoOverrange;
        /// <summary>最近一次 meta 振荡频率 Hz（BasicMetrics）；定位窗优先用其截完整 N 周期。</summary>
        double _lastOscFreqHz = double.NaN;
        readonly ComboBox _cboPicoRate = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 110,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        readonly ComboBox _cboPicoBits = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 80,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>勾选=自动量程（原逻辑升/降档重加）；取消=手动量程（不自动切，仅提示超量程或峰&lt;20%FS；增大量程可跳一档）。</summary>
        readonly CheckBox _chkAutoRange = new CheckBox
        {
            Text = "自动量程",
            Checked = true,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f)
        };
        readonly ComboBox _cboPicoRange = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        readonly CheckBox _chkImpedanceWide = new CheckBox
        {
            Text = "阻抗宽频",
            Checked = false,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f)
        };
        /// <summary>高通线性滤波：勾选则左窗检波 + 右窗定位原始局放均用滤波后数据（定位不降采样不检波）；仅量程调节参考/超量程判定用未滤波 Pico。</summary>
        readonly CheckBox _chkHighPass = new CheckBox
        {
            Text = "高通线性滤波",
            Checked = true,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f)
        };
        /// <summary>高通低截止频率 Hz；默认 10 kHz。紧挨滤波选项之后。</summary>
        readonly NumericUpDown _numHpCutoffHz = new NumericUpDown
        {
            Minimum = 100,
            Maximum = 5000000,
            Increment = 1000,
            Value = 10000,
            DecimalPlaces = 0,
            Width = 90,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>定位窗：从相位起点起 N 个振荡周期。</summary>
        readonly NumericUpDown _numLocateCycles = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 20,
            Value = 2,
            Width = 60,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>局放绘图标尺（pC）：10pC～10nC，默认 100pC；定位纵轴 ±对称。</summary>
        readonly ComboBox _cboPdScale = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>触发位置 0–100（%）；0=窗起点，50=触发在 50%。最大 100。</summary>
        readonly TrackBar _trkTriggerPos = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Width = 160,
            Height = 45,
            AutoSize = false
        };
        readonly Label _lblTriggerPos = new Label
        {
            AutoSize = true,
            Text = "触发位置 0%",
            Font = new Font("Microsoft YaHei UI", 9f),
            Margin = new Padding(4, 12, 4, 4)
        };
        /// <summary>触发采集振荡/局放长度（ms）；默认 60；写入 Pico/ART 实际采集长度。</summary>
        readonly NumericUpDown _numCaptureMs = new NumericUpDown
        {
            Minimum = LiveHardwareOptions.MinOscillationCaptureDurationMilliseconds,
            Maximum = LiveHardwareOptions.MaxOscillationCaptureDurationMilliseconds,
            Increment = 10,
            Value = LiveHardwareOptions.OscillationCaptureDurationMilliseconds,
            DecimalPlaces = 0,
            Width = 70,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>校准采集脉冲平均次数 N（固定间隔筛选后按点平均）。</summary>
        readonly NumericUpDown _numPulseAvg = new NumericUpDown
        {
            Minimum = 2,
            Maximum = 64,
            Value = 8,
            Width = 60,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>校准采集电缆长度 L（m）。</summary>
        readonly NumericUpDown _numCableLengthM = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            DecimalPlaces = 1,
            Increment = 10,
            Value = 100,
            Width = 80,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        /// <summary>波速 v（m/µs），默认 150。</summary>
        readonly NumericUpDown _numVelocityMPerUs = new NumericUpDown
        {
            Minimum = 50,
            Maximum = 300,
            DecimalPlaces = 1,
            Increment = 1,
            Value = 150,
            Width = 70,
            Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        // 上图：直流源/分压/电流绝对值 live（不画振荡）
        readonly List<PointF> _liveSrcKv = new List<PointF>();
        readonly List<PointF> _liveDivKv = new List<PointF>();
        readonly List<PointF> _liveCurMa = new List<PointF>();
        DateTime _livePlotT0 = DateTime.MinValue;
        double _lastPicoRateHz = 125e6;
        int _lastPicoBits = 14;
        double _lastPicoFullScaleV = 2.0;
        bool _safetyAbortLatchedUi;
        /// <summary>操作员停止：本批连续加压不再进下一轮；本轮须走完 HVOFF+采集；量程可按数据调。</summary>
        bool _operatorStopEndBatch;
        /// <summary>UI 轮询清锁存节流（ms ticks）。</summary>
        long _lastLatchClearAttemptMs;

        readonly TextBox _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            WordWrap = false
        };

        readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer { Interval = 100 }; // 急停/加压钮联动：原 750ms 过慢
        readonly System.Windows.Forms.Timer _demoTimer = new System.Windows.Forms.Timer { Interval = 50 };

        readonly WaveformPlotPanel _plotRamp = new WaveformPlotPanel
        {
            Dock = DockStyle.Fill,
            Title = "直流加压（电压/时间）",
            XLabel = "t / s",
            YLabel = "kV",
            TraceColor = Color.FromArgb(0, 120, 180),
            EmptyHint = "等待升压曲线…"
        };
        /// <summary>左下：完整采集振荡平滑折线 + 检波局放超细直方（同stem全段；可滚轮放大）。</summary>
        readonly WaveformPlotPanel _plotPdDemod = new WaveformPlotPanel
        {
            Dock = DockStyle.Fill,
            Title = "检波后振荡局放",
            XLabel = "t / ms",
            YLeftLabel = "振荡 / kV",
            YRightLabel = "检波 / V",
            LeftTraceColor = Color.FromArgb(0, 120, 180),
            RightTraceColor = Color.FromArgb(200, 80, 40),
            RightAsHistogramFill = true,
            EmptyHint = "等待检波局放…"
        };
        /// <summary>右下：与左同stem原始；首零起 N 周期切电压+局放（HP后原始，不降采样不检波）。</summary>
        readonly WaveformPlotPanel _plotLocate = new WaveformPlotPanel
        {
            Dock = DockStyle.Fill,
            Title = "定位（相位起 N 周期）",
            XLabel = "t / ms",
            YLeftLabel = "振荡 / kV",
            YRightLabel = "局放 / V",
            LeftTraceColor = Color.FromArgb(0, 120, 180),
            RightTraceColor = Color.FromArgb(120, 60, 180),
            EmptyHint = "等待定位窗…"
        };
        /// <summary>兼容旧引用：指向左下检波图。</summary>
        WaveformPlotPanel _plotOsc { get { return _plotPdDemod; } }
        SplitContainer _splitMain;   // 图区 | 日志
        SplitContainer _splitPd;     // 检波 | 定位

        // FakeLive / 演示：升压与导通后振荡的合成刷新
        bool _demoActive;
        int _demoTick;
        double _demoTargetKv = 2.0;
        readonly List<PointF> _rampLive = new List<PointF>();
        bool _oscillationShown;

        public FactoryPressForm()
        {
            Text = FactoryPressApi.BrandName + " · " + FactoryPressApi.ProductName;
            Width = 1080;
            Height = 860;
            MinimumSize = new Size(920, 700);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            _cboPicoRate.Items.AddRange(new object[] { "125 MS/s", "62.5 MS/s", "31.25 MS/s" });
            _cboPicoRate.SelectedIndex = 0; // 125M 默认
            _cboPicoBits.Items.AddRange(new object[] { "14 bit", "15 bit" });
            _cboPicoBits.SelectedIndex = 0; // 14bit 默认
            // 与 Pico5242DFullScaleVolts 对齐；默认 ±1 V（索引 6）
            _cboPicoRange.Items.AddRange(new object[] {
                "±10 mV", "±20 mV", "±50 mV", "±100 mV", "±200 mV", "±500 mV",
                "±1 V", "±2 V", "±5 V", "±10 V", "±20 V" });
            _cboPicoRange.SelectedIndex = 6; // ±1 V 初始
            InitPdScaleCombo();
            _plotRamp.Title = "直流监控（源V / 分压 / 电流·绝对值）";
            _plotRamp.YLeftLabel = "|V| / kV";
            _plotRamp.YRightLabel = "|I| / mA";
            _plotRamp.EmptyHint = "等待直流源/分压/电流…";
            _plotPdDemod.Subtitle = "设定 " + PressTestConfig.FormatPicoRate(125e6) + " / 14bit；滚轮放大";
            _plotPdDemod.ZoomEnabled = true;
            _plotLocate.ZoomEnabled = true;
            _txtOut.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "hv-press-out");

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            root.Controls.Add(BuildStatusBar(), 0, 0);
            root.Controls.Add(BuildConfigPanel(), 0, 1);
            root.Controls.Add(BuildActionPanel(), 0, 2);
            root.Controls.Add(BuildChartsAndLogSplit(), 0, 3);

            _btnConnect.Click += async (s, e) => await SafeUi(ConnectAsync);
            _btnSavePdCal.Click += (s, e) => SafeUiSync(SavePdCalibrationNow);
            _chkImpedanceWide.CheckedChanged += (s, e) => OnImpedanceWideChanged();
            _chkAutoRange.CheckedChanged += (s, e) => OnAutoRangeModeChanged();
            _cboPicoRange.SelectedIndexChanged += (s, e) => OnPicoRangeUiChanged();
            _btnPressToggle.Click += async (s, e) => await OnPressToggleAsync();
            _btnRelease.Click += (s, e) => SafeUiSync(ReleaseNow);
            _uiTimer.Tick += (s, e) => RefreshStatusUi();
            _demoTimer.Tick += (s, e) => OnDemoTick();
            FormClosing += OnFormClosing;
            Shown += (s, e) =>
            {
                AppendLog(FactoryPressApi.BrandName + " · " + FactoryPressApi.ProductName);
                AppendLog("构建：" + FactoryPressBuildInfo.Stamp);
                AppendLog("定稿五态：「" + StatusCopy.Connected + "」≠「" + StatusCopy.ReadyToEnergize + "」");
                AppendLog(StatusCopy.PicoSdkOnlyNoSplash);
                AppendLog("台架默认 Gain=" + PressTestConfig.TestBenchVoltageGain.ToString(CultureInfo.InvariantCulture)
                    + "（AO5V→30kV）Divider=" + PressTestConfig.TestBenchDividerRatio.ToString(CultureInfo.InvariantCulture)
                    + ":1；禁止危险占位 1000。");
                AppendLog("模式固定 Live。校准采集请用独立「雄新校准采集」exe；本界面仅保留/加载校准系数 pC/V。");
                TryLoadSavedPdCalibrationIntoUi(announce: true);
                AppendLog("布局：上直流；下左「检波」=完整采集振荡+局放（同stem）；下右「定位」=同一段原始数据首零起N周期切电压+局放（真Pico.raw.i16×cal）；触发采集长度ms可调（默认60）；AO步长/步间延时可调。");
                SeedIdleDemoCharts();
                RefreshStatusUi();
                _uiTimer.Start();
            };
        }

        /// <summary>
        /// 主分隔：上图区 / 下日志，可拖；图区内再分直流与左右局放/定位，尺寸互协调。
        /// </summary>
        Control BuildChartsAndLogSplit()
        {
            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                Panel1MinSize = 120,
                Panel2MinSize = 80
            };
            // 图区：上直流 | 下 检波+定位
            var charts = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 5,
                Panel1MinSize = 80,
                Panel2MinSize = 100
            };
            charts.Panel1.Controls.Add(_plotRamp);
            _splitPd = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 5,
                Panel1MinSize = 120,
                Panel2MinSize = 120
            };
            _splitPd.Panel1.Controls.Add(_plotPdDemod);
            _splitPd.Panel2.Controls.Add(_plotLocate);
            charts.Panel2.Controls.Add(_splitPd);
            _splitMain.Panel1.Controls.Add(charts);
            _splitMain.Panel2.Controls.Add(_log);
            // 初次布局按比例：图约 68%、日志 32%；其后用户拖动互协调
            _splitMain.Resize += (s, e) => EnsureMainSplitRatio();
            charts.Resize += (s, e) =>
            {
                try
                {
                    if (charts.Height > 200)
                    {
                        int want = Math.Max(charts.Panel1MinSize, charts.Height * 40 / 100);
                        if (want < charts.Height - charts.Panel2MinSize - charts.SplitterWidth)
                            charts.SplitterDistance = want;
                    }
                }
                catch { }
            };
            Shown += (s, e) =>
            {
                try
                {
                    EnsureMainSplitRatio(force: true);
                    if (_splitPd != null && _splitPd.Width > 200)
                        _splitPd.SplitterDistance = Math.Max(_splitPd.Panel1MinSize, _splitPd.Width / 2);
                }
                catch { }
            };
            return _splitMain;
        }

        void EnsureMainSplitRatio(bool force = false)
        {
            if (_splitMain == null || _splitMain.Height < 180) return;
            try
            {
                int total = _splitMain.Height - _splitMain.SplitterWidth;
                int want = total * 68 / 100;
                int min1 = _splitMain.Panel1MinSize;
                int max1 = total - _splitMain.Panel2MinSize;
                if (want < min1) want = min1;
                if (want > max1) want = max1;
                // 仅初始或偏差极大时纠正，避免打架用户拖动
                if (force || Math.Abs(_splitMain.SplitterDistance - want) > total / 3)
                    _splitMain.SplitterDistance = want;
            }
            catch { }
        }

        Control BuildWaveformPanel()
        {
            return BuildChartsAndLogSplit();
        }

        Control BuildStatusBar()
        {
            var p = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(2)
            };
            p.Controls.Add(Caption("状态："));
            p.Controls.Add(_lblConnected);
            p.Controls.Add(_lblCalibrated);
            p.Controls.Add(_lblReady);
            p.Controls.Add(_lblEStop);
            p.Controls.Add(_lblHvOn);
            p.Controls.Add(_lblHvUp);
            p.Controls.Add(_lblHvOff);
            p.Controls.Add(_lblCapture);
            p.Controls.Add(_lblPhase);
            return p;
        }

        Control BuildConfigPanel()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 6,
                Padding = new Padding(0, 4, 0, 4)
            };
            for (int i = 0; i < 6; i++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // 加压界面：只保留校准系数（校准采集走独立 FactoryCalibrationUi.exe）
            grid.Controls.Add(Caption("增益 V/V"), 0, 0);
            grid.Controls.Add(_numGain, 1, 0);
            grid.Controls.Add(Caption("加压列表 kV"), 2, 0);
            grid.Controls.Add(_txtTargets, 3, 0);
            grid.Controls.Add(Caption("校准系数 pC/V"), 4, 0);
            grid.Controls.Add(_numPdCal, 5, 0);

            grid.RowCount = 2;
            grid.Controls.Add(_lblPdCalHint, 0, 1);
            grid.Controls.Add(_btnSavePdCal, 1, 1);

            var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            row2.Controls.Add(Caption("输出目录"));
            row2.Controls.Add(_txtOut);
            var browse = new Button { Text = "浏览…", Width = 70, Height = 28 };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "选择加压输出目录";
                    if (Directory.Exists(_txtOut.Text)) dlg.SelectedPath = _txtOut.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        _txtOut.Text = dlg.SelectedPath;
                }
            };
            row2.Controls.Add(browse);
            row2.Controls.Add(Caption("连续加压次数(设压→加压→振荡=1轮)"));
            row2.Controls.Add(_numPressRounds);
            row2.Controls.Add(Caption("Pico采样率"));
            row2.Controls.Add(_cboPicoRate);
            row2.Controls.Add(Caption("位数"));
            row2.Controls.Add(_cboPicoBits);
            row2.Controls.Add(_chkAutoRange);
            row2.Controls.Add(Caption("Pico量程"));
            row2.Controls.Add(_cboPicoRange);
            row2.Controls.Add(Caption("AO步间延时ms"));
            row2.Controls.Add(_numRampStepDelay);
            row2.Controls.Add(Caption("AO步长V"));
            row2.Controls.Add(_numRampStepAo);
            row2.Controls.Add(_chkImpedanceWide);
            row2.Controls.Add(_chkHighPass);
            row2.Controls.Add(Caption("低截止Hz"));
            row2.Controls.Add(_numHpCutoffHz);
            row2.Controls.Add(Caption("定位周期N"));
            row2.Controls.Add(_numLocateCycles);
            row2.Controls.Add(Caption("触发采集振荡/局放长度ms"));
            row2.Controls.Add(_numCaptureMs);
            row2.Controls.Add(Caption("触发位置%"));
            row2.Controls.Add(_trkTriggerPos);
            row2.Controls.Add(_lblTriggerPos);
            row2.Controls.Add(Caption("局放标尺"));
            row2.Controls.Add(_cboPdScale);
            row2.Controls.Add(_chkSuppressSplash);
            _chkHighPass.CheckedChanged += (s, e) =>
            {
                _numHpCutoffHz.Enabled = _chkHighPass.Checked;
                try { TryLoadPreviewFromRun(); } catch { }
            };
            _numHpCutoffHz.ValueChanged += (s, e) => { try { TryLoadPreviewFromRun(); } catch { } };
            _numLocateCycles.ValueChanged += (s, e) => { try { TryLoadPreviewFromRun(); } catch { } };
            _cboPdScale.SelectedIndexChanged += (s, e) => { try { TryLoadPreviewFromRun(); } catch { } };
            _trkTriggerPos.ValueChanged += (s, e) =>
            {
                int v = Math.Max(0, Math.Min(100, _trkTriggerPos.Value));
                if (_trkTriggerPos.Value != v) _trkTriggerPos.Value = v;
                _lblTriggerPos.Text = "触发位置 " + v + "%";
            };

            var wrap = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
            wrap.Controls.Add(grid, 0, 0);
            wrap.Controls.Add(row2, 0, 1);
            return wrap;
        }

        Control BuildActionPanel()
        {
            var p = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(0, 6, 0, 6)
            };
            foreach (var b in new[] { _btnConnect, _btnPressToggle, _btnRelease })
            {
                b.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
                b.Margin = new Padding(4);
                p.Controls.Add(b);
            }
            return p;
        }

        static Label MakeState()
        {
            return new Label
            {
                AutoSize = true,
                Padding = new Padding(10, 6, 10, 6),
                Margin = new Padding(4),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.Gainsboro,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                MinimumSize = new Size(0, 32),
                Text = "—"
            };
        }

        static Label Caption(string t)
        {
            return new Label
            {
                Text = t,
                AutoSize = true,
                Margin = new Padding(8, 8, 4, 4),
                Font = new Font("Microsoft YaHei UI", 9.5f)
            };
        }

        PressTestConfig BuildConfigFromUi()
        {
            var cfg = PressTestConfig.CreateSmokeDefaults();
            cfg.Mode = "Live"; // 主界面仅 Live，无仿真/模式选择
            cfg.VoltageGainVPerAoVolt = (double)_numGain.Value;
            // 校准系数无效(≤0)时强制默认 2000，避免落盘/日志出现系数为 0
            if (_numPdCal.Value > 0)
                cfg.PdCalibrationPcPerVolt = (double)_numPdCal.Value;
            else
            {
                cfg.PdCalibrationPcPerVolt = PdCalibrationStore.DefaultPdCalibrationPcPerVolt;
                try { _numPdCal.Value = (decimal)PdCalibrationStore.DefaultPdCalibrationPcPerVolt; } catch { }
            }
            // 注入量/缆长/平均N 仅属校准 exe；加压侧用已存系数或默认
            cfg.InjectedChargePc = PdCalibrationStore.DefaultInjectedChargePc;
            cfg.DividerRatioVPerV = PressTestConfig.TestBenchDividerRatio;
            if (Math.Abs(cfg.VoltageGainVPerAoVolt - 1000.0) < 1e-9)
                throw new InvalidOperationException("增益 1000 为危险占位，禁止；请用 6000（AO5V→30kV）或台架实测值。");
            if (cfg.VoltageGainVPerAoVolt < 100)
                throw new InvalidOperationException("增益过小，请确认台架 VoltageGain（默认 6000）。");
            cfg.SuppressPicoStartupWindow = _chkSuppressSplash.Checked;
            cfg.AutoRangeAdjust = _chkAutoRange.Checked;
            cfg.InitialPicoRangeIndex = RangeIndexFromUi();
            cfg.RampStepDelayMilliseconds = Math.Max(1, (int)_numRampStepDelay.Value);
            cfg.RampStepAoVolts = Math.Max(0.002, Math.Min(0.1, (double)_numRampStepAo.Value));
            cfg.PicoResolutionBits = _cboPicoBits.SelectedIndex == 1 ? 15 : 14;
            cfg.PicoTargetSampleRateHz = PicoRateHzFromUi();
            cfg.PicoTimebase = PressTestConfig.ResolvePicoTimebase(cfg.PicoTargetSampleRateHz);
            cfg.CurrentScaleAPerVolt = PressTestConfig.TestBenchCurrentScaleAPerVolt;
            cfg.SourceVoltageScaleVPerV = PressTestConfig.TestBenchSourceVoltageScale;
            cfg.DividerRatioVPerV = PressTestConfig.TestBenchDividerRatio;
            cfg.ImpedanceBandwidthWide = _chkImpedanceWide.Checked;
            cfg.HighPassLinearFilterEnabled = _chkHighPass.Checked;
            cfg.HighPassCutoffHz = (double)_numHpCutoffHz.Value;
            cfg.LocateCycleCount = Math.Max(1, (int)_numLocateCycles.Value);
            cfg.PulseAverageCount = 8;
            cfg.CableLengthM = 50.0;
            cfg.VelocityMPerUs = 150.0;
            cfg.PicoTriggerPositionPercent = LiveHardwareOptions.ClampTriggerPositionPercent(_trkTriggerPos.Value);
            cfg.OscillationCaptureDurationMs = LiveHardwareOptions.ClampOscillationCaptureDurationMs((double)_numCaptureMs.Value);
            cfg.PeriodToleranceFraction = CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction; // ±35%；50/100/400Hz
            cfg.RunCalibrationFirst = false; // GUI 分步：连接/校准采集/加压分开
            cfg.OutputDirectory = string.IsNullOrWhiteSpace(_txtOut.Text)
                ? Path.Combine(Path.GetTempPath(), "hv-press-out")
                : _txtOut.Text.Trim();
            cfg.TargetsKv = ParseTargets(_txtTargets.Text);
            if (cfg.TargetsKv.Count == 0)
                cfg.TargetsKv = new List<double> { 1, 2 };
            return cfg;
        }


        void TryClearLatchAfterAbortUi()
        {
            try
            {
                var snap = _api != null ? _api.TryGetStatus() : null;
                if (snap == null) return;
                // P0.4 已解除却仍显示锁存：提示可再次开始加压（PrepareInterlocks 会清）
                if (snap.EmergencyLatched && snap.EStopRawDiValid && !snap.EStopPressed)
                {
                    string detail;
                    if (_api.TryClearEmergencyLatchIfHardwareSafe(out detail))
                    {
                        _safetyAbortLatchedUi = false;
                        ResetEStopDebounce();
                        AppendLog("P0.4 已解除，已清除软件急停锁存：" + (detail ?? "OK") + "；可再次开始加压。");
                    }
                    else
                        AppendLog("提示：P0.4 已解除但锁存未清：" + (detail ?? "") + "；源关断后将自动再试。");
                }
            }
            catch { }
        }

        /// <summary>
        /// UI 轮询：硬件急停已解除且仍软件锁存时清锁存（加压中急停→释放后可再压）。
        /// 节流避免每 tick ForceSourceOff。
        /// </summary>
        void TryAutoClearEmergencyLatchUi(AcquisitionStatusSnapshot snap, bool connected)
        {
            if (!connected || snap == null || _api == null) return;
            if (!snap.EmergencyLatched) return;
            if (!snap.EStopRawDiValid || snap.EStopPressed) return;
            long now = Environment.TickCount64;
            if (_lastLatchClearAttemptMs != 0 && (now - _lastLatchClearAttemptMs) < 200)
                return;
            _lastLatchClearAttemptMs = now;
            try
            {
                string detail;
                if (_api.TryClearEmergencyLatchIfHardwareSafe(out detail))
                {
                    _safetyAbortLatchedUi = false;
                    ResetEStopDebounce();
                    if (!string.IsNullOrEmpty(detail) && detail.IndexOf("已清", StringComparison.Ordinal) >= 0)
                        AppendLog("自动清急停锁存：" + detail);
                }
            }
            catch { }
        }

        int RangeIndexFromUi()
        {
            int idx = _cboPicoRange.SelectedIndex;
            if (idx < 0 || idx > 10) return 6; // ±1 V
            return idx;
        }

        void SyncPicoRangeUiFromIndex(int rangeIndex)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SyncPicoRangeUiFromIndex(rangeIndex))); } catch { }
                return;
            }
            try
            {
                if (rangeIndex < 0 || rangeIndex >= _cboPicoRange.Items.Count)
                    return;
                if (_cboPicoRange.SelectedIndex != rangeIndex)
                    _cboPicoRange.SelectedIndex = rangeIndex;
                double[] fs = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0 };
                if (rangeIndex < fs.Length)
                    _lastPicoFullScaleV = fs[rangeIndex];
            }
            catch (Exception ex)
            {
                AppendLog("量程下拉同步失败（已忽略）：" + ex.Message);
            }
        }

        double PicoRateHzFromUi()
        {
            switch (_cboPicoRate.SelectedIndex)
            {
                case 1: return 62.5e6;
                case 2: return 31.25e6;
                default: return 125e6;
            }
        }

        void UpdatePicoPlotSubtitle()
        {
            string set = "设定 " + PressTestConfig.FormatPicoRate(PicoRateHzFromUi())
                + " / " + (_cboPicoBits.SelectedIndex == 1 ? "15" : "14") + "bit / "
                + (_cboPicoRange.SelectedItem != null ? _cboPicoRange.SelectedItem.ToString() : "±1 V");
            string act = "实际 " + PressTestConfig.FormatPicoRate(_lastPicoRateHz)
                + " / " + _lastPicoBits + "bit / ±" + _lastPicoFullScaleV.ToString("0.##", CultureInfo.InvariantCulture) + "V";
            _plotPdDemod.Subtitle = set + " → " + act + (_chkHighPass.Checked ? ("；HP@" + ((int)_numHpCutoffHz.Value) + "Hz") : "；未滤波");
        }

        void TryReadLatestPicoMeta(string waveDir)
        {
            try
            {
                string[] metas = Directory.GetFiles(waveDir, "*.meta.txt");
                if (metas.Length == 0) return;
                Array.Sort(metas, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
                string text = File.ReadAllText(metas[metas.Length - 1]);
                foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("picoSampleRateHz=", StringComparison.OrdinalIgnoreCase))
                    {
                        double hz;
                        if (double.TryParse(line.Substring("picoSampleRateHz=".Length).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz > 0)
                            _lastPicoRateHz = hz;
                    }
                    else if (line.StartsWith("picoResolutionBits=", StringComparison.OrdinalIgnoreCase))
                    {
                        int bits;
                        if (int.TryParse(line.Substring("picoResolutionBits=".Length).Trim(), out bits) && bits > 0)
                            _lastPicoBits = bits;
                    }
                    else if (line.StartsWith("picoFullScaleV=", StringComparison.OrdinalIgnoreCase))
                    {
                        double fs;
                        if (double.TryParse(line.Substring("picoFullScaleV=".Length).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out fs) && fs > 0)
                            _lastPicoFullScaleV = fs;
                    }
                    else if (line.StartsWith("picoRangeIndex=", StringComparison.OrdinalIgnoreCase))
                    {
                        int ri;
                        if (int.TryParse(line.Substring("picoRangeIndex=".Length).Trim(), out ri) && ri >= 0)
                            SyncPicoRangeUiFromIndex(ri);
                    }
                    else if (line.StartsWith("picoOverrangeOrClipped=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring("picoOverrangeOrClipped=".Length).Trim();
                        _lastPicoOverrange = v.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || v == "1" || v.Equals("True", StringComparison.Ordinal);
                    }
                    else if (line.StartsWith("oscillationFrequencyHz=", StringComparison.OrdinalIgnoreCase))
                    {
                        double f;
                        if (double.TryParse(line.Substring("oscillationFrequencyHz=".Length).Trim(),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out f)
                            && f >= BasicMetricsCalculator.MinFrequencyHz
                            && f <= BasicMetricsCalculator.MaxFrequencyHz)
                            _lastOscFreqHz = f;
                    }
                }
            }
            catch { }
        }

        static List<double> ParseTargets(string text)
        {
            var list = new List<double>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (string part in text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0)
                    list.Add(v);
            }
            return list;
        }

        async Task ConnectAsync()
        {
            if (_api != null && _api.IsConnected)
            {
                AppendLog("已处于「" + StatusCopy.Connected + "」，无需重复连接。");
                return;
            }
            ReleaseQuiet();

            // 清理门：允许操作员确认关断后复位，无需杀进程
            if (FactoryPressApi.IsCleanupGateSet)
            {
                var ask = MessageBox.Show(this,
                    "检测到「清理失败门」仍置位（可能为上次关闭时的误报或真清理失败）。\r\n\r\n"
                    + "若您已确认高压继电器已断开、AO=0、现场安全，可选「是」复位清理门并继续连接。\r\n"
                    + "选「否」则取消本次连接。",
                    FactoryPressApi.ProductName + " — 复位清理门",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (ask != DialogResult.Yes)
                {
                    AppendLog("已取消连接（清理门未复位）。");
                    _lblPhase.Text = "阶段：未连接";
                    RefreshStatusUi();
                    return;
                }
                string resetDetail;
                if (!FactoryPressApi.TryResetCleanupGateAfterOperatorAck(true, out resetDetail))
                {
                    AppendLog("清理门复位失败：" + (resetDetail ?? ""));
                    _lblPhase.Text = "阶段：未连接";
                    MessageBox.Show(this, resetDetail ?? "复位失败", FactoryPressApi.ProductName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RefreshStatusUi();
                    return;
                }
                AppendLog("清理门已复位：" + resetDetail);
            }

            var cfg = BuildConfigFromUi();
            // 连接：优先 {AppBase}/config/pd-calibration.json（校准采集落盘）；无效则保留 UI 默认 2000
            {
                PdCalibrationRecord rec;
                string pathUsed;
                if (PdCalibrationStore.TryLoad(out rec, out pathUsed))
                {
                    cfg.PdCalibrationPcPerVolt = rec.PdCalibrationPcPerVolt;
                    if (rec.InjectedChargePc > 0) cfg.InjectedChargePc = rec.InjectedChargePc;
                    SetPdCalUi(rec.PdCalibrationPcPerVolt, rec.InjectedChargePc);
                    AppendLog("Connect：套用已保存校准系数 " + rec.PdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture)
                        + " pC/V ← " + pathUsed);
                }
                else if (!(cfg.HasPdCalibration))
                {
                    cfg.PdCalibrationPcPerVolt = PdCalibrationStore.DefaultPdCalibrationPcPerVolt;
                    SetPdCalUi(cfg.PdCalibrationPcPerVolt, cfg.InjectedChargePc);
                    AppendLog("Connect：无有效 " + PdCalibrationStore.FileName
                        + "，默认 pdCalibrationPcPerVolt="
                        + cfg.PdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture));
                }
            }
            if (cfg.IsFakeLive == false)
            {
                var a = MessageBox.Show(this,
                    "即将以 Live 连接真实 ART+Pico：\r\n"
                    + "• " + StatusCopy.PicoSdkOnlyNoSplash + "\r\n"
                    + "• 急停以 P0.4 硬件为准\r\n\r\n确认现场安全？",
                    "Live 连接 — 第一次确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (a != DialogResult.Yes)
                {
                    AppendLog("已取消 Live 连接。");
                    _lblPhase.Text = "阶段：未连接";
                    RefreshStatusUi();
                    return;
                }
                var b = MessageBox.Show(this, "再次确认 Live 连接？", "Live 连接 — 第二次确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (b != DialogResult.Yes)
                {
                    AppendLog("已取消 Live 连接。");
                    _lblPhase.Text = "阶段：未连接";
                    RefreshStatusUi();
                    return;
                }
            }

            var logWriter = new UiLogWriter(AppendLog);
            _api = new FactoryPressApi(logWriter);
            _lblPhase.Text = "阶段：连接中…";
            RefreshStatusUi();
            try
            {
                // Connect 在 UI 线程：与 Release 同线程释放 DeviceSdkLease
                _api.Connect(cfg);
                await Task.CompletedTask;
                AppendLog("Connect OK → 「" + StatusCopy.Connected + "」（≠「" + StatusCopy.ReadyToEnergize + "」） mode=Live"
                    + " gain=" + (double.IsNaN(cfg.VoltageGainVPerAoVolt) ? PressTestConfig.TestBenchVoltageGain : cfg.VoltageGainVPerAoVolt)
                        .ToString(CultureInfo.InvariantCulture)
                    + " pdCal=" + (cfg.HasPdCalibration
                        ? cfg.PdCalibrationPcPerVolt.ToString("G6", CultureInfo.InvariantCulture) + "pC/V"
                        : "未填"));
                _lblPhase.Text = "阶段：" + StatusCopy.Connected;
            }
            catch
            {
                _lblPhase.Text = "阶段：连接失败";
                try { if (_api != null) { _api.Dispose(); } } catch { }
                _api = null;
                throw;
            }
            finally
            {
                RefreshStatusUi();
            }
        }



        void OnAutoRangeModeChanged()
        {
            bool autoOn = _chkAutoRange.Checked;
            if (_api != null && _api.Config != null)
                _api.Config.AutoRangeAdjust = autoOn;
            AppendLog(autoOn
                ? "量程模式：自动（可升/降档重加本轮）"
                : "量程模式：手动（仅提示不重加）");
        }

        void OnPicoRangeUiChanged()
        {
            if (_busy) return;
            int idx = RangeIndexFromUi();
            if (_api != null && _api.Config != null)
                _api.Config.InitialPicoRangeIndex = idx;
            if (_api != null && _api.IsConnected)
            {
                try
                {
                    int bits = _cboPicoBits.SelectedIndex == 1 ? 15 : 14;
                    double rateHz = PicoRateHzFromUi();
                    bool zWide = _chkImpedanceWide.Checked;
                    _api.ApplyUiCaptureSettings(bits, rateHz, idx, zWide);
                    AppendLog("Pico量程已切换 → " + (_cboPicoRange.SelectedItem != null ? _cboPicoRange.SelectedItem.ToString() : ("idx=" + idx)));
                }
                catch (Exception ex)
                {
                    AppendLog("Pico量程切换失败: " + ex.Message);
                }
            }
            UpdatePicoPlotSubtitle();
        }

        void OnImpedanceWideChanged()
        {
            bool wide = _chkImpedanceWide.Checked;
            if (_api == null || !_api.IsConnected)
            {
                AppendLog("阻抗带宽勾选=" + (wide ? "宽" : "窄") + "（未连接，连接后生效）");
                return;
            }
            try
            {
                _api.SetImpedanceBandwidthWide(wide);
                AppendLog("阻抗带宽已切换并生效: " + (wide ? "宽" : "窄") + "（与加压解耦）");
            }
            catch (Exception ex)
            {
                AppendLog("阻抗带宽切换失败: " + ex.Message);
            }
        }

        async Task CalibrateAsync()
        {
            EnsureConnected();
            // 须急停按下才可校准；解除（高电平）则禁并提示
            if (!TryEnsureEStopPressedBeforeCalibration())
                return;
            // 校准前推送界面：注入档（pC/nC）+ 脉冲平均/缆长 + 阻抗（勿沿用 Connect 旧 100pC）
            try
            {
                double inj = InjectedChargePcFromUi();
                _api.ApplyCalibrationUiSettings(
                    inj,
                    Math.Max(2, (int)_numPulseAvg.Value),
                    Math.Max(1.0, (double)_numCableLengthM.Value),
                    Math.Max(1.0, (double)_numVelocityMPerUs.Value),
                    CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction);
                AppendLog("校准前注入档 → " + FormatChargePcAndNc(inj)
                    + "（须与校准器拨档一致；5 nC=5000 pC）");
                bool z = _chkImpedanceWide.Checked;
                _api.SetImpedanceBandwidthWide(z);
                AppendLog("校准前阻抗带宽 → " + (z ? "宽" : "窄"));
            }
            catch (Exception ex) { AppendLog("校准前 UI 设定失败: " + ex.Message); return; }
            _workCts = new CancellationTokenSource();
            _lblPhase.Text = "阶段：" + StatusCopy.Calibrating;
            int code = await Task.Run(() => _api.Calibrate(_workCts.Token), _workCts.Token).ConfigureAwait(true);
            if (code == 0)
            {
                AppendLog("校准完成 → 「" + StatusCopy.Calibrated + "」（≠「" + StatusCopy.ReadyToEnergize + "」）");
                _lblPhase.Text = "阶段：" + StatusCopy.Calibrated;
                ApplyComputedPdCalToUi();
                try { SavePdCalibrationNow(); }
                catch (Exception ex) { AppendLog("校准系数自动落盘失败: " + ex.Message); }
                try { TryShowCalibrationPulseFromRun(); } catch (Exception ex) { AppendLog("校准波形显示失败: " + ex.Message); }
            }
            else
            {
                string reason = _api.AbortReason ?? "";
                AppendLog("校准失败 exit=" + code + " aborted=" + _api.AbortedBySafety + " " + reason);
                bool noWave = !string.IsNullOrEmpty(reason)
                    && reason.IndexOf(StatusCopy.NoCalibrationWaveform, StringComparison.Ordinal) >= 0;
                bool estopBlock = !string.IsNullOrEmpty(reason)
                    && (reason.IndexOf(StatusCopy.CalibNeedsEStopPressed, StringComparison.Ordinal) >= 0
                        || reason.IndexOf("无法校准采集", StringComparison.Ordinal) >= 0);
                if (noWave)
                {
                    _lblPhase.Text = "阶段：" + StatusCopy.NoCalibrationWaveform;
                    AppendLog(StatusCopy.NoCalibrationWaveform);
                    _plotPdDemod.ClearSeries();
                    _plotLocate.ClearSeries();
                    _plotPdDemod.EmptyHint = StatusCopy.NoCalibrationWaveform;
                    _plotLocate.EmptyHint = StatusCopy.NoCalibrationWaveform;
                }
                else if (estopBlock)
                {
                    _lblPhase.Text = "阶段：" + StatusCopy.CalibNeedsEStopPressed;
                }
                else
                {
                    _lblPhase.Text = "阶段：校准失败";
                    // 其它失败若已落盘可尝试画，便于对照
                    try { TryShowCalibrationPulseFromRun(); } catch { }
                }
            }
            RefreshStatusUi();
        }

        /// <summary>
        /// 校准入口急停门禁：须「急停按下」。解除（高电平）→ 禁止校准并提示。
        /// 0kV 校准不走「开始加压列表」门禁；按下时仍可写 AO=0。
        /// </summary>
        bool TryEnsureEStopPressedBeforeCalibration()
        {
            try
            {
                var snap = _api != null ? _api.TryGetStatus() : null;
                if (snap == null) return true; // 无快照时交给 API 再判
                if (!snap.EStopPressed)
                {
                    string msg = StatusCopy.CalibNeedsEStopPressed
                        + "（当前「" + StatusCopy.EStopReleased + "」）";
                    AppendLog(msg);
                    _lblPhase.Text = "阶段：" + StatusCopy.CalibNeedsEStopPressed;
                    RefreshStatusUi();
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppendLog("校准前急停检查失败: " + ex.Message);
            }
            return true;
        }

        /// <summary>
        /// 校准成功后：把落盘的平均校准脉冲（*.pico.preview.csv）画到下左/下右图（双极性原始，非加压检波）。
        /// 无有效点返回 false。
        /// </summary>
        bool TryShowCalibrationPulseFromRun()
        {
            string run = _api != null ? _api.RunDirectory : null;
            if (string.IsNullOrWhiteSpace(run) || !Directory.Exists(run))
            {
                AppendLog("校准波形：无 RunDirectory。");
                return false;
            }
            string waveDir = Path.Combine(run, "waveforms");
            if (!Directory.Exists(waveDir))
            {
                AppendLog("校准波形：无 waveforms。");
                return false;
            }
            TryReadLatestPicoMeta(waveDir);
            UpdatePicoPlotSubtitle();

            string[] pico = Directory.GetFiles(waveDir, "*.pico.preview.csv");
            if (pico.Length == 0)
            {
                AppendLog("校准波形：无 pico.preview.csv。");
                _plotPdDemod.ClearSeries();
                _plotLocate.ClearSeries();
                _plotPdDemod.EmptyHint = StatusCopy.NoCalibrationWaveform;
                _plotLocate.EmptyHint = StatusCopy.NoCalibrationWaveform;
                return false;
            }
            Array.Sort(pico, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            string pathPd = pico[pico.Length - 1];
            double hzNom, hzEff;
            bool seq;
            List<PointF> pulse = LoadPreviewCsv(pathPd, preferApproxVolts: true, out hzNom, out hzEff, out seq);
            if (pulse == null || pulse.Count < 2)
            {
                AppendLog("校准波形：预览点数不足 ← " + Path.GetFileName(pathPd));
                _plotPdDemod.ClearSeries();
                _plotLocate.ClearSeries();
                _plotPdDemod.EmptyHint = StatusCopy.NoCalibrationWaveform;
                _plotLocate.EmptyHint = StatusCopy.NoCalibrationWaveform;
                return false;
            }
            double remapHz = (seq && hzEff > 0) ? hzEff : (hzNom > 0 ? hzNom : (_lastPicoRateHz > 0 ? _lastPicoRateHz : 125e6));
            RemapXToMs(pulse, remapHz);

            float peakAbs = 0;
            foreach (var pt in pulse)
            {
                float a = Math.Abs(pt.Y);
                if (a > peakAbs) peakAbs = a;
            }
            if (!(peakAbs > 1e-9f))
            {
                AppendLog(StatusCopy.NoCalibrationWaveform + "（预览峰≈0）");
                _plotPdDemod.ClearSeries();
                _plotLocate.ClearSeries();
                _plotPdDemod.EmptyHint = StatusCopy.NoCalibrationWaveform;
                _plotLocate.EmptyHint = StatusCopy.NoCalibrationWaveform;
                return false;
            }

            string title = "校准平均脉冲"
                + (peakAbs > 0 ? (" · peak=" + peakAbs.ToString("G4", CultureInfo.InvariantCulture) + "V") : "");
            _plotPdDemod.Title = title;
            _plotPdDemod.XLabel = "t / µs";
            _plotPdDemod.YLeftLabel = "校准 / V";
            _plotPdDemod.YRightLabel = "";
            _plotPdDemod.RightAsHistogramFill = false;
            _plotPdDemod.LeftTraceColor = Color.FromArgb(200, 80, 40);
            // 校准脉冲时间尺度 µs：RemapXToMs 得到 ms，×1000 → µs 便于局放看
            var pulseUs = new List<PointF>(pulse.Count);
            for (int i = 0; i < pulse.Count; i++)
                pulseUs.Add(new PointF(pulse[i].X * 1000f, pulse[i].Y));
            _plotPdDemod.SetSeries(pulseUs, autoScale: true);
            bool over = _lastPicoOverrange || (_lastPicoFullScaleV > 0 && peakAbs >= _lastPicoFullScaleV * 0.80f);
            _plotPdDemod.SetOverrangeMarker(over, (float)_lastPicoFullScaleV,
                over ? ("超量程/饱和 ±" + _lastPicoFullScaleV.ToString("0.##", CultureInfo.InvariantCulture) + "V") : null);

            _plotLocate.Title = "校准平均脉冲（局放窗）";
            _plotLocate.XLabel = "t / µs";
            _plotLocate.YLeftLabel = "校准 / V";
            _plotLocate.YRightLabel = "";
            _plotLocate.RightAsHistogramFill = false;
            _plotLocate.RightAsBipolarStem = false;
            _plotLocate.LeftTraceColor = Color.FromArgb(120, 60, 180);
            _plotLocate.SetSeries(pulseUs, autoScale: true);

            _oscillationShown = true;
            AppendLog("校准波形已显示 ← " + Path.GetFileName(pathPd)
                + " n=" + pulseUs.Count
                + " peak=" + peakAbs.ToString("G4", CultureInfo.InvariantCulture) + "V"
                + " Fs=" + remapHz.ToString("0.#", CultureInfo.InvariantCulture));
            return true;
        }

        async Task OnPressToggleAsync()
        {
            if (_busy)
            {
                // HVOFF/预臂/采集中禁止停止
                AcquisitionStatusSnapshot snap = null;
                try { if (_api != null) snap = _api.TryGetStatus(); } catch { }
                if (IsPostHvupCapturePhase(snap))
                {
                    AppendLog("已进入 HVOFF/采集环节，停止钮已禁用，请等待关源后同步采集完成。");
                    return;
                }
                StopNow();
                return;
            }
            await SafeUi(PressAsync);
        }

        async Task PressAsync()
        {
            EnsureConnected();
            var targets = ParseTargets(_txtTargets.Text);
            if (targets.Count == 0)
                throw new InvalidOperationException("请填写加压列表（例如 1,2）。");
            int rounds = (int)_numPressRounds.Value;
            if (rounds < 1) rounds = 1;
            try { if (_workCts != null) _workCts.Dispose(); } catch { }
            _workCts = new CancellationTokenSource();
            _lblPhase.Text = "阶段：加压中…";
            // 加压启动：自动加载刚保存的校准系数（勿再手填）
            EnsurePdCalibrationAppliedForPress();
            // 加压前必须同步「自动量程」勾选到 Config（Connect 快照会过期：取消勾选后仍 AutoRangeAdjust=true → MaxRangeRetries 重加约三次）
            if (_api.Config != null)
                _api.Config.AutoRangeAdjust = _chkAutoRange.Checked;
            AppendLog("开始加压 targets=" + string.Join(",", targets.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)))
                + " rounds=" + rounds + "（1轮=设压→加压→振荡结束）"
                + "；量程=" + (_chkAutoRange.Checked ? "自动（可重加本轮）" : "手动（仅提示、不重加）")
                + "；AO步间延时 RampStepDelay=" + Math.Max(1, (int)_numRampStepDelay.Value) + "ms"
                + "；AO步长 RampStepAo=" + ((double)_numRampStepAo.Value).ToString("G4", CultureInfo.InvariantCulture) + "V"
                + "；pdCal=" + ((double)_numPdCal.Value).ToString("G6", CultureInfo.InvariantCulture) + " pC/V"
                + "；capMs=" + LiveHardwareOptions.ClampOscillationCaptureDurationMs((double)_numCaptureMs.Value).ToString("0.###", CultureInfo.InvariantCulture)
                + "；trigPos=" + LiveHardwareOptions.ClampTriggerPositionPercent(_trkTriggerPos.Value) + "%");
            _safetyAbortLatchedUi = false;
            _operatorStopEndBatch = false;
            BeginLiveDcMonitor();
            UpdatePicoPlotSubtitle();
            int code = 0;
            int totalCompleted = 0;
            try
            {
                for (int r = 0; r < rounds; r++)
                {
                    _workCts.Token.ThrowIfCancellationRequested();
                    if (_safetyAbortLatchedUi)
                    {
                        AppendLog("安全中止后禁止自动再压；已停止后续连续轮次。");
                        break;
                    }
                    if (_operatorStopEndBatch)
                    {
                        AppendLog("操作员已停止：本轮 HVOFF/采集完成后不再进入下一轮加压；可按技术数据调量程后手动再压。");
                        break;
                    }
                    if (rounds > 1)
                        AppendLog("—— 连续加压第 " + (r + 1) + "/" + rounds + " 轮 ——");
                    // 每轮沿用界面 Pico 位数/采样率与阻抗；自动量程优先上次成功量程，勿回原点
                    int bits = _cboPicoBits.SelectedIndex == 1 ? 15 : 14;
                    double rateHz = PicoRateHzFromUi();
                    bool zWide = _chkImpedanceWide.Checked;
                    int rangeIdx = RangeIndexFromUi();
                    if (_chkAutoRange.Checked)
                    {
                        int remembered = _api.CurrentPicoRangeIndex;
                        if (remembered >= 0 && remembered != rangeIdx)
                        {
                            AppendLog("自动量程：沿用上次成功 index=" + remembered
                                + "（界面曾为 " + rangeIdx + "，跳过回原点重调）");
                            rangeIdx = remembered;
                            SyncPicoRangeUiFromIndex(rangeIdx);
                        }
                    }
                    int rangeAtStart = rangeIdx;
                    int trigPct = LiveHardwareOptions.ClampTriggerPositionPercent(_trkTriggerPos.Value);
                    double capMs = LiveHardwareOptions.ClampOscillationCaptureDurationMs((double)_numCaptureMs.Value);
                    if (_api.Config != null)
                    {
                        _api.Config.AutoRangeAdjust = _chkAutoRange.Checked; // 每轮再同步，防 Connect 后改勾选未传入
                        _api.Config.RampStepDelayMilliseconds = Math.Max(1, (int)_numRampStepDelay.Value);
                        _api.Config.RampStepAoVolts = Math.Max(0.002, Math.Min(0.1, (double)_numRampStepAo.Value));
                        _api.Config.PicoTriggerPositionPercent = trigPct;
                        _api.Config.OscillationCaptureDurationMs = capMs;
                    }
                    _api.ApplyUiCaptureSettings(bits, rateHz, rangeIdx, zWide);
                    // 设定量程即当前会话量程（直至采集回读刷新实际）
                    double[] fs = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0 };
                    if (rangeIdx >= 0 && rangeIdx < fs.Length)
                        _lastPicoFullScaleV = fs[rangeIdx];
                    UpdatePicoPlotSubtitle();
                    var token = _workCts.Token;
                    code = await Task.Run(() => _api.Press(targets, token), CancellationToken.None).ConfigureAwait(true);
                    totalCompleted += _api.CompletedSteps;
                    try { TryLoadPreviewFromRun(); } catch { }
                    RefreshStatusUi();
                    if (code != 0)
                    {
                        // exit=6：操作员停止；关源不锁存，允许再次开始加压
                        bool operatorStop = code == 6
                            || (_api.AbortReason != null
                                && _api.AbortReason.IndexOf("操作员停止", StringComparison.Ordinal) >= 0);
                        if (operatorStop)
                        {
                            _safetyAbortLatchedUi = false;
                            _operatorStopEndBatch = true; // 禁止再进下一轮；本轮已走完 HVOFF+采集
                            // 同步到本轮实际量程（含自动量程结果），供按技术数据再调；勿回滚到开压初值
                            SyncPicoRangeUiFromIndex(_api.CurrentPicoRangeIndex >= 0
                                ? _api.CurrentPicoRangeIndex : rangeAtStart);
                            AppendLog("本轮操作员停止 exit=" + code
                                + "（关源未锁存；已走完 HVOFF+采集；禁止自动下一轮；可按数据调量程后手动再压）。");
                            TryClearLatchAfterAbortUi();
                            break;
                        }
                        _safetyAbortLatchedUi = true;
                        AppendLog("本轮非零退出 exit=" + code + "，停批，禁止自动再压。");
                        TryClearLatchAfterAbortUi();
                        break;
                    }
                    else
                    {
                        // 自动：同步到实际量程；手动：保持开压所选，绝不因 Suggested 改下拉/开下一轮
                        if (_chkAutoRange.Checked)
                            SyncPicoRangeUiFromIndex(_api.CurrentPicoRangeIndex);
                        else
                            SyncPicoRangeUiFromIndex(rangeAtStart);
                        UpdatePicoPlotSubtitle();
                    }
                    // 轮间时刻：会话内「导通指令起算满 2s」门控，勿另加无关 Delay
                }
            }
            finally
            {
                StopLiveDcMonitorKeepLast();
            }
            if (code == 0)
            {
                AppendLog("加压完成 completed=" + totalCompleted + " csv=" + (_api.CsvPath ?? ""));
                _lblPhase.Text = "阶段：加压完成";
            }
            else
            {
                bool operatorStop = code == 6
                    || (_api.AbortReason != null
                        && _api.AbortReason.IndexOf("操作员停止", StringComparison.Ordinal) >= 0);
                AppendLog("加压结束 exit=" + code
                    + " aborted=" + _api.AbortedBySafety
                    + " reason=" + (_api.AbortReason ?? "")
                    + " completed=" + totalCompleted);
                if (operatorStop)
                    _lblPhase.Text = "阶段：已停止";
                else
                    _lblPhase.Text = _api.AbortedBySafety ? "阶段：安全中止" : "阶段：加压失败";
            }
            TryLoadPreviewFromRun();
            RefreshStatusUi();
        }

        void StopNow()
        {
            AcquisitionStatusSnapshot snap = null;
            try { if (_api != null) snap = _api.TryGetStatus(); } catch { }
            if (IsPostHvupCapturePhase(snap))
            {
                AppendLog("HVOFF/采集进行中，忽略停止请求。");
                RefreshStatusUi();
                return;
            }
            try { if (_workCts != null) _workCts.Cancel(); } catch { }
            try { if (_api != null) _api.Stop(); } catch (Exception ex) { AppendLog("Stop: " + ex.Message); }
            _safetyAbortLatchedUi = false;
            _operatorStopEndBatch = true; // 走完本轮 HVOFF+采集后禁止再进下一轮
            AppendLog("已请求停止：关源进 HVOFF 并完成同步采集（不永久锁存）；本批不再下一轮；可按技术数据调量程后手动再压。");
            _lblPhase.Text = "阶段：已停止";
            TryClearLatchAfterAbortUi();
            RefreshStatusUi();
        }

        void ReleaseNow()
        {
            try { if (_workCts != null) _workCts.Cancel(); } catch { }
            if (_api == null)
            {
                AppendLog("无会话可释放。");
                RefreshStatusUi();
                return;
            }
            int hint = 0;
            try { hint = _api.Release(); }
            catch (Exception ex)
            {
                // 清理失败：只记日志，禁止 MessageBox 挡路径
                AppendLog("Release 异常（已记录，不弹窗）: " + ex.Message);
            }
            finally
            {
                try { _api.Dispose(); } catch { }
                _api = null;
            }
            if (hint != 0)
                AppendLog("Release 完成：cleanupFailed hint=" + hint
                    + "（若刚 USB/ART/Pico 掉线，多为 ClearTask/CloseUnit 误报；确认关断后点连接可复位清理门再重连。"
                    + " OpenUnit 0x3/GetValues 0x7/-200477 常见于句柄残留或设备未枚举）");
            else
                AppendLog("Release 完成。");
            _lblPhase.Text = "阶段：已释放";
            ResetEStopDebounce();
            RefreshStatusUi();
        }

        void ReleaseQuiet()
        {
            if (_api == null) return;
            try { _api.Release(); } catch { /* 不弹窗 */ }
            try { _api.Dispose(); } catch { }
            _api = null;
            ResetEStopDebounce();
        }

        void ResetEStopDebounce()
        {
            _estopStableKind = 0;
            _estopPendingKind = -1;
            _estopPendingHits = 0;
            _lastGoodSnap = null;
        }

        void EnsureConnected()
        {
            if (_api == null || !_api.IsConnected)
                throw new InvalidOperationException(StatusCopy.DeviceNotConnected + " 请先点「连接」。");
        }

        void RefreshStatusUi()
        {
            try { RefreshStatusUiCore(); }
            catch (Exception ex) { try { AppendLog("状态刷新异常（已忽略）：" + ex.Message); } catch { } }
        }

        void RefreshStatusUiCore()
        {
            bool connected = _api != null && _api.IsConnected;
            AcquisitionStatusSnapshot snap = null;
            bool snapFresh = false;
            if (connected)
            {
                snap = _api.TryGetStatus();
                if (snap != null)
                {
                    _lastGoodSnap = snap;
                    snapFresh = true;
                }
                else
                    snap = _lastGoodSnap; // 读失败勿当「未知」与锁存互刷
            }
            else
                _lastGoodSnap = null;

            bool deviceConnected = connected && (snap == null || snap.IsDeviceConnected || snap.Initialized);
            PaintStateIfChanged(_lblConnected,
                deviceConnected ? StatusCopy.Connected : (connected ? "连接中?" : "未连接"),
                deviceConnected, Color.SeaGreen);

            bool calibrated = snap != null
                ? snap.IsCalibrated
                : (connected && _lblPhase.Text != null
                    && (_lblPhase.Text.IndexOf(StatusCopy.Calibrated, StringComparison.Ordinal) >= 0
                        || _lblPhase.Text.IndexOf("加压完成", StringComparison.Ordinal) >= 0));
            PaintStateIfChanged(_lblCalibrated,
                calibrated ? StatusCopy.Calibrated : StatusCopy.NotCalibrated,
                calibrated, Color.SteelBlue);

            // —— 软件锁存自动复位：P0.4 已解除且源关 → 清锁存，恢复「开始加压」——
            TryAutoClearEmergencyLatchUi(snap, connected);
            if (connected && snap != null && snap.EmergencyLatched)
            {
                // 清锁存后重取快照，避免本帧仍按锁存禁用「开始加压」
                var snap2 = _api.TryGetStatus();
                if (snap2 != null) { snap = snap2; _lastGoodSnap = snap2; snapFresh = true; }
            }

            // —— 急停灯：同源 + 迟滞。锁存与未知禁止每 tick 互换 ——
            int rawKind = ClassifyEStopKind(connected, snap, snapFresh);
            int stableKind = DebounceEStopKind(rawKind, connected);

            string estopText;
            Color estopColor;
            bool estopActivePaint;
            bool estopBlocksReady;
            MapEStopKind(stableKind, out estopText, out estopColor, out estopActivePaint, out estopBlocksReady);
            PaintStateIfChanged(_lblEStop, estopText, estopActivePaint, estopColor);

            string phase = _lblPhase.Text ?? "";
            // 「已停止」= 操作员 Stop（关源不锁存），不挡可加压/再压
            bool phaseBlocksReady =
                phase.IndexOf("安全中止", StringComparison.Ordinal) >= 0
                || phase.IndexOf("加压失败", StringComparison.Ordinal) >= 0
                || phase.IndexOf("连接失败", StringComparison.Ordinal) >= 0
                || phase.IndexOf(StatusCopy.Calibrating, StringComparison.Ordinal) >= 0;

            bool ready = connected && calibrated && !_busy && !estopBlocksReady && !phaseBlocksReady;
            PaintStateIfChanged(_lblReady, ready ? StatusCopy.ReadyToEnergize : "不可加压", ready, Color.Orange);
            PaintStageLamps(connected, snap, phase);

            _btnConnect.Enabled = !_busy && !connected;
            // 校准门禁与顶栏红灯同源：stableKind==2「急停按下」才可点校准（勿仅信瞬时 snap，防灯红钮灰）
            // 急停关/按下 → 可校准 + 可 0V；高电平解除 → 可加压、不可校准
            // 校准采集已迁独立 exe；加压界面不启用校准钮
            // 加压：仅急停解除可点「开始加压」。加压中 HVON/HVUP 可点「停止」；
            // 一旦进入 HVOFF/预臂/采集触发环节则禁用停止（须跑完关源后同步采集）。
            bool inHvoffPhase = IsPostHvupCapturePhase(snap);
            if (_busy)
                _btnPressToggle.Enabled = connected && !inHvoffPhase;
            else
                _btnPressToggle.Enabled = connected && !estopBlocksReady;
            string toggleText = _busy ? "停止" : "开始加压";
            if (_btnPressToggle.Text != toggleText)
                _btnPressToggle.Text = toggleText;
            Color toggleBg = _busy ? Color.IndianRed : SystemColors.Control;
            if (_btnPressToggle.BackColor != toggleBg)
                _btnPressToggle.BackColor = toggleBg;
            _btnRelease.Enabled = !_busy && _api != null;
            _numGain.Enabled = !_busy;
            _numPdCal.Enabled = !_busy;
            _txtTargets.Enabled = !_busy;
            _numPressRounds.Enabled = !_busy;
            _txtOut.Enabled = !_busy && !connected;
            _chkSuppressSplash.Enabled = !_busy && !connected;
            _cboPicoRate.Enabled = !_busy; // 连续加压每轮沿用当前选择
            _cboPicoBits.Enabled = !_busy;
            // 空闲连接态必须可改自动/手动量程与 Pico 量程；仅加压进行中 (_busy) 按安全策略锁定
            bool rangeIdleEditable = !_busy;
            _chkAutoRange.Enabled = rangeIdleEditable;
            _cboPicoRange.Enabled = rangeIdleEditable || _operatorStopEndBatch; // 停止后亦可按技术数据调量程
            _trkTriggerPos.Enabled = !_busy;
            _numCaptureMs.Enabled = !_busy;
            _cboPdScale.Enabled = !_busy;
            _numRampStepDelay.Enabled = !_busy;
            _numRampStepAo.Enabled = !_busy;
            _chkImpedanceWide.Enabled = connected; // 与加压忙闲解耦，随时可调
            _chkHighPass.Enabled = !_busy;
            _numHpCutoffHz.Enabled = !_busy && _chkHighPass.Checked;
            _numLocateCycles.Enabled = !_busy;
        }



        /// <summary>HVOFF 及关源后预臂/采集触发：禁止点停止（量程/采集须跑完）。</summary>
        static bool IsPostHvupCapturePhase(AcquisitionStatusSnapshot snap)
        {
            if (snap == null) return false;
            string msg = snap.StateMessage ?? "";
            return msg.IndexOf("HVOFF", StringComparison.Ordinal) >= 0
                || msg.IndexOf("设置窗", StringComparison.Ordinal) >= 0
                || msg.IndexOf("AO清零", StringComparison.Ordinal) >= 0
                || msg.IndexOf("布防", StringComparison.Ordinal) >= 0
                || msg.IndexOf("预臂", StringComparison.Ordinal) >= 0
                || msg.IndexOf("采集触发", StringComparison.Ordinal) >= 0
                || msg.IndexOf("开关导通", StringComparison.Ordinal) >= 0
                || msg.IndexOf(StatusCopy.PressAbortHvOffPicoCapture, StringComparison.Ordinal) >= 0;
        }

        /// <summary>各环节指示灯：绿正常 / 黄警示 / 红错误（断连、未触发等）。</summary>
        void PaintStageLamps(bool connected, AcquisitionStatusSnapshot snap, string phase)
        {
            bool hardEstop = snap != null && snap.EStopRawDiValid && snap.EStopPressed;
            bool latchedOnly = snap != null && snap.EmergencyLatched && !hardEstop;
            bool aborted = phase.IndexOf("安全中止", StringComparison.Ordinal) >= 0
                || phase.IndexOf("加压失败", StringComparison.Ordinal) >= 0
                || phase.IndexOf("已停止", StringComparison.Ordinal) >= 0;

            // HVON：断连/真急停按下=红；加压中=绿；0kV/校准噪声跳过 HVON=灰；已中止/仅软件锁存=黄
            bool zeroKvOrCal = phase.IndexOf(StatusCopy.Calibrating, StringComparison.Ordinal) >= 0
                || phase.IndexOf("噪声", StringComparison.Ordinal) >= 0
                || phase.IndexOf("0 kV", StringComparison.Ordinal) >= 0
                || phase.IndexOf("0kV", StringComparison.Ordinal) >= 0;
            TrafficKind hvon = TrafficKind.Idle;
            if (!connected) hvon = TrafficKind.Error;
            else if (hardEstop) hvon = TrafficKind.Error;
            else if (zeroKvOrCal) hvon = TrafficKind.Idle; // 0V 跳过 HVON
            else if (snap != null && snap.CycleRunning) hvon = TrafficKind.Ok;
            else if (aborted || latchedOnly) hvon = TrafficKind.Warn;
            else if (connected) hvon = TrafficKind.Warn;
            int hvonMs = snap != null ? snap.StepHvonElapsedMs : 0;
            int hvupMs = snap != null ? snap.StepHvupElapsedMs : 0;
            int hvoffMs = snap != null ? snap.StepHvoffElapsedMs : 0;
            int capMs = snap != null ? snap.StepCaptureElapsedMs : 0;
            PaintTraffic(_lblHvOn, "HVON", hvon, hvonMs);

            TrafficKind hvup = TrafficKind.Idle;
            if (!connected) hvup = TrafficKind.Error;
            else if (hardEstop) hvup = TrafficKind.Error;
            else if (zeroKvOrCal) hvup = TrafficKind.Idle; // 0V 跳过 HVUP
            else if (snap != null && snap.CycleRunning) hvup = TrafficKind.Ok;
            else if (phase.IndexOf("加压完成", StringComparison.Ordinal) >= 0) hvup = TrafficKind.Ok;
            else if (aborted || latchedOnly) hvup = TrafficKind.Warn;
            else if (connected) hvup = TrafficKind.Warn;
            PaintTraffic(_lblHvUp, "HVUP", hvup, hvupMs);

            // HVOFF：仅真正关源+AO清零耗时（预臂/500ms/50ms 不计入灯）
            TrafficKind hvoff = TrafficKind.Idle;
            string msg = snap != null ? (snap.StateMessage ?? "") : "";
            bool inHvoff = msg.IndexOf("HVOFF", StringComparison.Ordinal) >= 0
                || msg.IndexOf("设置窗", StringComparison.Ordinal) >= 0
                || msg.IndexOf("AO清零", StringComparison.Ordinal) >= 0
                || msg.IndexOf("AO=0", StringComparison.Ordinal) >= 0
                || msg.IndexOf("布防", StringComparison.Ordinal) >= 0;
            bool inCapTrig = msg.IndexOf("采集触发", StringComparison.Ordinal) >= 0
                || msg.IndexOf("开关导通", StringComparison.Ordinal) >= 0;
            if (!connected) hvoff = TrafficKind.Error;
            else if (hardEstop) hvoff = TrafficKind.Error;
            else if (inHvoff) hvoff = TrafficKind.Ok;
            else if (hvoffMs > 0 || phase.IndexOf("加压完成", StringComparison.Ordinal) >= 0)
                hvoff = TrafficKind.Ok;
            else if (aborted || latchedOnly || (snap != null && snap.CycleRunning))
                hvoff = TrafficKind.Warn;
            else if (connected) hvoff = TrafficKind.Warn;
            PaintTraffic(_lblHvOff, "HVOFF", hvoff, hvoffMs);

            // 采集触发：仅触发起至读回（不含触发前延时/HVOFF）
            TrafficKind cap = TrafficKind.Idle;
            if (!connected) cap = TrafficKind.Error;
            else if (hardEstop) cap = TrafficKind.Error;
            else if (inCapTrig || phase.IndexOf("加压完成", StringComparison.Ordinal) >= 0)
                cap = TrafficKind.Ok;
            else if (msg.IndexOf("超时", StringComparison.Ordinal) >= 0
                     || msg.IndexOf("未触发", StringComparison.Ordinal) >= 0
                     || (msg.IndexOf("CaptureFault", StringComparison.Ordinal) >= 0 && !aborted))
                cap = TrafficKind.Error;
            else if (aborted || latchedOnly
                     || (snap != null && snap.CycleRunning && !inHvoff))
                cap = TrafficKind.Warn;
            else if (connected) cap = TrafficKind.Warn;
            PaintTraffic(_lblCapture, "采集触发", cap, capMs);

            // 延时漏电提示（仅状态栏日志节流由外层；此处更新按钮副信息已含 ms）
            if (snap != null && !double.IsNaN(snap.StepDelayLeakageA) && snap.StepDelayLeakageA > 0)
            {
                // peak 漏电已体现在 CurrentA；阶段灯以 ms 为主
            }
        }

        enum TrafficKind { Idle, Ok, Warn, Error }

        void PaintTraffic(Label lbl, string name, TrafficKind kind, int elapsedMs = 0)
        {
            Color bg;
            string text;
            string ms = elapsedMs > 0 ? (" " + elapsedMs + "ms") : "";
            switch (kind)
            {
                case TrafficKind.Ok:
                    bg = Color.FromArgb(46, 160, 90); text = name + "·正常" + ms; break;
                case TrafficKind.Warn:
                    bg = Color.FromArgb(220, 170, 40); text = name + "·警示" + ms; break;
                case TrafficKind.Error:
                    bg = Color.FromArgb(200, 60, 60); text = name + "·错误" + ms; break;
                default:
                    bg = Color.FromArgb(160, 160, 160); text = name + "·—" + ms; break;
            }
            if (lbl.Text != text) lbl.Text = text;
            if (lbl.BackColor != bg) lbl.BackColor = bg;
            if (lbl.ForeColor != Color.White) lbl.ForeColor = Color.White;
        }

        /// <summary>
        /// 瞬时分类。snapFresh=false（沿用旧快照/读失败）→ 返回 -1 保持，绝不标未知去打锁存。
        /// 锁存优先于 DI；未知仅在未锁存且 DI 无效时。
        /// </summary>
        static int ClassifyEStopKind(bool connected, AcquisitionStatusSnapshot snap, bool snapFresh)
        {
            if (!connected) return 0;
            if (snap == null) return -1; // 无样：保持
            if (!snapFresh) return -1;
            if (snap.EmergencyLatched) return 3; // 锁存时绝不以 DI 无效改「未知」
            if (!snap.EStopRawDiValid) return 4; // 稳态灰
            if (snap.EStopPressed) return 2;
            return 1;
        }

        int DebounceEStopKind(int rawKind, bool connected)
        {
            if (!connected)
            {
                _estopStableKind = 0;
                _estopPendingKind = -1;
                _estopPendingHits = 0;
                return 0;
            }
            // 本帧无新样：完全保持，清 pending 以免「未知」积满误切
            if (rawKind < 0)
            {
                _estopPendingKind = -1;
                _estopPendingHits = 0;
                return _estopStableKind;
            }
            if (_estopStableKind == 0 && rawKind != 0)
            {
                _estopStableKind = rawKind == 4 ? 4 : rawKind; // 首样可进未知灰
                _estopPendingKind = rawKind;
                _estopPendingHits = 0;
                return _estopStableKind;
            }
            if (rawKind == _estopStableKind)
            {
                _estopPendingKind = rawKind;
                _estopPendingHits = 0;
                return _estopStableKind;
            }

            // —— 迟滞表：防止锁存(3) ↔ 未知(4) 互刷 ——
            // 已锁存：忽略未知/短暂无效；仅「解除」或「按下」连续 N 次才离开锁存
            if (_estopStableKind == 3)
            {
                if (rawKind == 4 || rawKind < 0)
                    return _estopStableKind;
                // 离开锁存需连续确认
            }
            // 已未知：忽略单次锁存闪烁；需连续 N 次 EmergencyLatched 才进锁存
            if (_estopStableKind == 4 && rawKind == 3)
            {
                // fall through to normal N-hit
            }
            // 解除/空闲：单帧未知保持绿灯，不跳灰
            if (rawKind == 4 && (_estopStableKind == 1 || _estopStableKind == 0))
                return _estopStableKind;
            // 未知稳态：单帧解除也不立刻绿（避免未知↔解除抖）；需连续 N 次解除
            // （由下面 pending 处理）

            if (rawKind != _estopPendingKind)
            {
                _estopPendingKind = rawKind;
                _estopPendingHits = 1;
                return _estopStableKind;
            }
            _estopPendingHits++;
            int need = EStopDebounceHits;
            // 锁存↔未知 / 未知↔锁存 更严
            if ((_estopStableKind == 3 && rawKind == 4) || (_estopStableKind == 4 && rawKind == 3))
                need = EStopDebounceHits + 2;
            if (_estopPendingHits >= need)
            {
                // 禁止：从锁存直接切到未知（只允许切到解除/按下）
                if (_estopStableKind == 3 && rawKind == 4)
                {
                    _estopPendingHits = 0;
                    return _estopStableKind;
                }
                _estopStableKind = rawKind;
                _estopPendingHits = 0;
            }
            return _estopStableKind;
        }

        static void MapEStopKind(int kind, out string text, out Color color, out bool activePaint, out bool blocksReady)
        {
            switch (kind)
            {
                case 1:
                    text = StatusCopy.EStopReleased;
                    color = Color.MediumSeaGreen;
                    activePaint = true;
                    blocksReady = false;
                    return;
                case 2:
                    text = StatusCopy.EStopPressed;
                    color = Color.IndianRed;
                    activePaint = true;
                    blocksReady = true;
                    return;
                case 3:
                    text = "急停锁存";
                    color = Color.OrangeRed;
                    activePaint = true;
                    blocksReady = true;
                    return;
                case 4:
                    text = "急停未知";
                    color = Color.DarkGray;
                    activePaint = true;
                    blocksReady = true; // 未知不可加压，但显示灰而非与绿互闪
                    return;
                default:
                    text = "急停—";
                    color = Color.Gainsboro;
                    activePaint = false;
                    blocksReady = true;
                    return;
            }
        }

        static void PaintStateIfChanged(Label lbl, string text, bool active, Color on)
        {
            Color back = active ? on : Color.Gainsboro;
            Color fore = active ? Color.White : Color.Black;
            if (lbl.Text == text && lbl.BackColor == back && lbl.ForeColor == fore)
                return;
            lbl.Text = text;
            lbl.BackColor = back;
            lbl.ForeColor = fore;
        }

        static void PaintState(Label lbl, string text, bool active, Color on)
        {
            PaintStateIfChanged(lbl, text, active, on);
        }

        void AppendLog(string line)
        {
            if (IsDisposed) return;
            string text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendLog(line)));
                return;
            }
            _log.AppendText(text);
        }

        async Task SafeUi(Func<Task> action)
        {
            if (_busy) { AppendLog("忙，请稍候或点「停止」。"); return; }
            _busy = true;
            RefreshStatusUi();
            UseWaitCursor = true;
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                AppendLog("已取消。");
                if (_lblPhase.Text != null && _lblPhase.Text.IndexOf("加压", StringComparison.Ordinal) >= 0)
                    _lblPhase.Text = "阶段：已停止";
            }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
                // 业务错误可用提示框；清理路径禁止
                MessageBox.Show(this, ex.Message, FactoryPressApi.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _busy = false;
                UseWaitCursor = false;
                RefreshStatusUi();
            }
        }

        void SafeUiSync(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                AppendLog("错误: " + ex.Message);
            }
            finally { RefreshStatusUi(); }
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _uiTimer.Stop();
            _demoTimer.Stop();
            try { if (_workCts != null) _workCts.Cancel(); } catch { }
            try { if (_api != null) _api.Stop(); } catch { }
            // 关闭时 Release：失败只吞掉记不得 MessageBox
            try
            {
                if (_api != null)
                {
                    int hint = _api.Release();
                    if (hint != 0)
                    {
                        // 不能挡关闭；写诊断文件可选
                        try
                        {
                            File.AppendAllText(
                                Path.Combine(Path.GetTempPath(), "xiongxin-hvpress-cleanup.log"),
                                DateTime.Now.ToString("o") + " cleanupFailed hint=" + hint + Environment.NewLine);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (_api != null) _api.Dispose(); } catch { }
                _api = null;
            }
        }

        void SeedIdleDemoCharts()
        {
            _demoTargetKv = 2.0;
            _plotRamp.Title = "直流监控（源V / 分压 / 电流·绝对值）";
            _plotRamp.ClearSeries();
            _plotRamp.EmptyHint = "连接并加压后显示 |源V| / |分压| / |电流|…";
            _plotPdDemod.ClearSeries();
            _plotLocate.ClearSeries();
            _plotPdDemod.EmptyHint = "等待实测振荡/局放（不回落合成）…";
            _plotLocate.EmptyHint = "等待定位窗…";
            _oscillationShown = false;
            UpdatePicoPlotSubtitle();
        }

        void BeginLiveDcMonitor()
        {
            _liveSrcKv.Clear();
            _liveDivKv.Clear();
            _liveCurMa.Clear();
            _livePlotT0 = DateTime.UtcNow;
            _demoActive = true;
            _oscillationShown = false;
            _plotRamp.ClearSeries();
            _plotPdDemod.ClearSeries();
            _plotLocate.ClearSeries();
            _plotRamp.EmptyHint = "升压中（绝对值）…";
            _plotPdDemod.EmptyHint = "导通后显示检波局放…";
            _plotLocate.EmptyHint = "导通后显示定位窗…";
            _plotRamp.YLeftLabel = "|V| / kV";
            _plotRamp.YRightLabel = "|I| / mA";
            _demoTimer.Start();
            AppendLog("上图：直流源电压/分压/电流绝对值；不画振荡。");
        }

        void StopLiveDcMonitorKeepLast()
        {
            _demoActive = false;
            _demoTimer.Stop();
            // 关源后必须刷新：再采一拍（会话已清源压/分压缓存）并强制钉零，禁止绿线假平台
            try { SampleLiveDcPoint(); } catch { }
            ForceAppendZeroLivePoint();
            PaintLiveDcChart();
            // 下图勿回落合成：无实测 preview 时保持空提示，等 TryLoadPreviewFromRun
            if (!_oscillationShown)
            {
                _plotPdDemod.EmptyHint = "等待实测振荡/局放（不回落合成）…";
                _plotLocate.EmptyHint = "等待定位窗…";
            }
        }

        /// <summary>关源后在直流图尾部钉 0，避免源已掉仍显示高压平台。</summary>
        void ForceAppendZeroLivePoint()
        {
            if (_livePlotT0 == DateTime.MinValue) return;
            float t = (float)(DateTime.UtcNow - _livePlotT0).TotalSeconds;
            if (_liveSrcKv.Count > 0)
            {
                float lastT = _liveSrcKv[_liveSrcKv.Count - 1].X;
                if (t <= lastT) t = lastT + 0.05f;
            }
            _liveSrcKv.Add(new PointF(t, 0f));
            _liveDivKv.Add(new PointF(t, 0f));
            _liveCurMa.Add(new PointF(t, 0f));
        }

        void OnDemoTick()
        {
            if (!_demoActive) return;
            try { SampleLiveDcPoint(); } catch { }
            PaintLiveDcChart();
        }

        void SampleLiveDcPoint()
        {
            if (_api == null || !_api.IsConnected) return;
            AcquisitionStatusSnapshot snap = _api.TryGetStatus();
            if (snap == null) return;
            if (_livePlotT0 == DateTime.MinValue) _livePlotT0 = DateTime.UtcNow;
            float t = (float)(DateTime.UtcNow - _livePlotT0).TotalSeconds;
            double srcV = Math.Abs(snap.LastSourceVoltageEngineeringV);
            double divV = Math.Abs(snap.LastDividerEngineeringV);
            if (divV <= 0 && snap.ActualVoltageV != 0) divV = Math.Abs(snap.ActualVoltageV);
            double curA = Math.Abs(snap.CurrentA);
            _liveSrcKv.Add(new PointF(t, (float)(srcV / 1000.0)));
            _liveDivKv.Add(new PointF(t, (float)(divV / 1000.0)));
            _liveCurMa.Add(new PointF(t, (float)(curA * 1000.0))); // A → mA
            const int maxPts = 2400;
            if (_liveSrcKv.Count > maxPts)
            {
                _liveSrcKv.RemoveRange(0, _liveSrcKv.Count - maxPts);
                _liveDivKv.RemoveRange(0, _liveDivKv.Count - maxPts);
                _liveCurMa.RemoveRange(0, _liveCurMa.Count - maxPts);
            }
        }

        void PaintLiveDcChart()
        {
            // 三通道同步：左轴分压(蓝)+直流源电压(绿) kV；右轴电流 mA。同一时间轴。
            if (_liveDivKv.Count < 2 && _liveSrcKv.Count < 2 && _liveCurMa.Count < 2)
                return;
            _plotRamp.YLeftLabel = "|V| kV";
            _plotRamp.YRightLabel = "|I| mA";
            _plotRamp.LeftTraceColor = Color.FromArgb(0, 120, 180);   // 分压
            _plotRamp.MidTraceColor = Color.FromArgb(40, 170, 70);    // 源V 绿
            _plotRamp.RightTraceColor = Color.FromArgb(200, 80, 40);  // 电流
            var div = _liveDivKv.Count >= 2 ? _liveDivKv : null;
            var src = _liveSrcKv.Count >= 2 ? _liveSrcKv : null;
            var cur = _liveCurMa.Count >= 2 ? _liveCurMa : null;
            if (div != null || src != null)
                _plotRamp.SetTriSeries(div ?? new List<PointF>(), src ?? new List<PointF>(), cur);
            else if (cur != null)
                _plotRamp.SetDualSeries(new List<PointF>(), cur);
        }

        static List<PointF> BuildRampSeries(double targetKv, double durationSec, double completeness)
        {
            int n = 120;
            var pts = new List<PointF>(n);
            double end = Math.Max(0.05, durationSec * Math.Max(0.05, Math.Min(1.0, completeness)));
            for (int i = 0; i < n; i++)
            {
                double t = end * i / (n - 1);
                double p = t / Math.Max(1e-9, durationSec);
                double kv = targetKv * (1.0 - Math.Exp(-3.2 * p));
                pts.Add(new PointF((float)t, (float)kv));
            }
            return pts;
        }

        void ApplyOscDualDemo(double targetKv)
        {
            // 仅作空闲预览气质；正式路径不回落合成。左：振(平滑)+检波直方；右：振+定位滤波原始(不检波)
            var osc = BuildOscillationSeries(targetKv, 1.0);
            var pd = BuildPicoPdSeries(targetKv, 1.0);
            if (pd != null && pd.Count >= 2)
            {
                var dem = AbsPeakDetectAligned(osc, pd);
                _plotPdDemod.YLeftLabel = "振荡 / kV";
                _plotPdDemod.YRightLabel = "检波 / V";
                _plotPdDemod.RightAsHistogramFill = true;
                if (osc != null && osc.Count >= 2 && dem != null)
                    _plotPdDemod.SetDualSeries(osc, dem);
                else if (dem != null)
                {
                    _plotPdDemod.RightAsHistogramFill = true;
                    _plotPdDemod.SetDualSeries(null, dem);
                }
                int n = Math.Max(2, (osc != null ? osc.Count : pd.Count) / 4);
                var locOsc = new List<PointF>();
                var locPd = new List<PointF>();
                if (osc != null)
                {
                    for (int i = 0; i < Math.Min(n, osc.Count); i++)
                        locOsc.Add(new PointF(osc[i].X - osc[0].X, osc[i].Y));
                }
                for (int i = 0; i < Math.Min(n, pd.Count); i++)
                    locPd.Add(new PointF(pd[i].X - pd[0].X, pd[i].Y));
                _plotLocate.YLeftLabel = "振荡 / kV";
                _plotLocate.YRightLabel = "局放 / V";
                if (locOsc.Count >= 2 && locPd.Count >= 2)
                    _plotLocate.SetDualSeries(locOsc, locPd);
                else if (locPd.Count >= 2)
                    _plotLocate.SetSeries(locPd);
            }
            else if (osc != null)
                _plotPdDemod.SetSeries(osc);
        }

        static List<PointF> BuildOscillationSeries(double targetKv, double amplitudeScale)
        {
            // 阻尼正弦：约 600 Hz 气质，时间轴 ms（左轴：振荡电压）
            int n = 400;
            double fHz = 600.0;
            double tauMs = 4.5;
            double peakV = Math.Max(0.2, targetKv * 0.35) * amplitudeScale;
            var pts = new List<PointF>(n);
            double tMaxMs = 12.0;
            for (int i = 0; i < n; i++)
            {
                double tMs = tMaxMs * i / (n - 1);
                double env = Math.Exp(-tMs / tauMs);
                double v = peakV * env * Math.Sin(2.0 * Math.PI * fHz * tMs / 1000.0);
                pts.Add(new PointF((float)tMs, (float)v));
            }
            return pts;
        }

        static List<PointF> BuildPicoPdSeries(double targetKv, double amplitudeScale)
        {
            // 右轴：Pico 局放气质（稀疏脉冲 + 微噪声），与振荡同时间窗，便于同框对照
            int n = 400;
            double tMaxMs = 12.0;
            double peak = Math.Max(0.05, targetKv * 0.08) * amplitudeScale;
            var pts = new List<PointF>(n);
            var rnd = new Random(42);
            for (int i = 0; i < n; i++)
            {
                double tMs = tMaxMs * i / (n - 1);
                double v = (rnd.NextDouble() - 0.5) * peak * 0.08;
                // 若干衰减脉冲簇
                for (int k = 0; k < 5; k++)
                {
                    double center = 0.8 + k * 1.9;
                    double dt = tMs - center;
                    if (Math.Abs(dt) < 0.35)
                        v += peak * (0.7 + 0.3 * k / 4.0) * Math.Exp(-dt * dt / 0.012)
                            * Math.Sin(2.0 * Math.PI * 2500.0 * dt / 1000.0);
                }
                pts.Add(new PointF((float)tMs, (float)v));
            }
            return pts;
        }

        void TryLoadPreviewFromRun()
        {
            try
            {
                string run = _api != null ? _api.RunDirectory : null;
                if (string.IsNullOrWhiteSpace(run) || !Directory.Exists(run))
                {
                    AppendLog("无 RunDirectory，下图不回落合成。");
                    _plotPdDemod.EmptyHint = "无实测目录…";
                    _plotLocate.EmptyHint = "无实测目录…";
                    return;
                }
                string waveDir = Path.Combine(run, "waveforms");
                if (!Directory.Exists(waveDir))
                {
                    AppendLog("无 waveforms 目录，下图不回落合成。");
                    _plotPdDemod.EmptyHint = "无 waveforms…";
                    _plotLocate.EmptyHint = "无 waveforms…";
                    return;
                }

                TryReadLatestPicoMeta(waveDir);
                UpdatePicoPlotSubtitle();

                List<PointF> leftOsc = null;
                List<PointF> rightPd = null;
                string leftSrc = null, rightSrc = null;
                string pairedStem = null;
                double artHz = 80000;
                double picoHz = _lastPicoRateHz > 0 ? _lastPicoRateHz : 125e6;
                double unifiedHz = 0;
                bool picoSequential = false, artSequential = false;

                // 左右必须同一 stem：AI4 定 t0/相位，Pico raw/preview 同源同触发窗；禁止各自取「最新」错档
                pairedStem = FindLatestPairedWaveformStem(waveDir);
                if (!string.IsNullOrEmpty(pairedStem))
                {
                    string pathPd = Path.Combine(waveDir, pairedStem + ".pico.preview.csv");
                    if (File.Exists(pathPd))
                    {
                        double hzNominal, hzEffective;
                        bool seq;
                        rightPd = LoadPreviewCsv(pathPd, preferApproxVolts: true, out hzNominal, out hzEffective, out seq);
                        picoSequential = seq;
                        if (hzNominal > 0) picoHz = hzNominal;
                        if (hzEffective > 0) unifiedHz = hzEffective;
                        if (rightPd != null && rightPd.Count >= 2)
                        {
                            double remapHz = (picoSequential && hzEffective > 0) ? hzEffective
                                : (hzNominal > 0 ? hzNominal : picoHz);
                            RemapXToMs(rightPd, remapHz);
                            rightSrc = Path.GetFileName(pathPd);
                        }
                        else rightPd = null;
                    }

                    string pathAi4 = Path.Combine(waveDir, pairedStem + ".ai4.preview.csv");
                    if (File.Exists(pathAi4))
                    {
                        double hzArtNom, hzArtEff;
                        bool seqArt;
                        leftOsc = LoadPreviewCsv(pathAi4, preferApproxVolts: false, out hzArtNom, out hzArtEff, out seqArt);
                        artSequential = seqArt;
                        if (hzArtNom > 0) artHz = hzArtNom;
                        if (hzArtEff > 0)
                        {
                            if (unifiedHz <= 0) unifiedHz = hzArtEff;
                            artHz = hzArtEff;
                        }
                        if (leftOsc != null && leftOsc.Count >= 2)
                        {
                            double remapArt = (artSequential && hzArtEff > 0) ? hzArtEff
                                : (hzArtNom > 0 ? hzArtNom : artHz);
                            RemapXToMs(leftOsc, remapArt);
                            if (hzArtEff > 0) artHz = hzArtEff;
                            else if (hzArtNom > 0) artHz = hzArtNom;
                            double ratio = ResolveDividerRatioForPlot();
                            ScaleYInPlace(leftOsc, (float)(ratio / 1000.0));
                            leftSrc = Path.GetFileName(pathAi4);
                        }
                        else leftOsc = null;
                    }
                }

                // 定位首零必须在未平滑原始振荡电压上重算；平滑仅用于显示，禁止平滑后找零（会错起点）
                List<PointF> leftOscRaw = leftOsc;
                List<PointF> leftOscDisplay = leftOsc;
                if (leftOsc != null && leftOsc.Count >= 7)
                    leftOscDisplay = SmoothOscillationForDisplay(leftOsc);

                // Pico 定位预览目标 ≥20k（默认 80k）；缺省不拿 AI4/8k 去覆盖 Pico Fs
                double picoEffForFilter = unifiedHz; // 上方若已从 pico.preview 读到 effective 则用之
                if (picoEffForFilter <= 0) picoEffForFilter = WaveformSummaryWriter.PicoLocatePreviewRateHz;
                if (unifiedHz <= 0) unifiedHz = WaveformSummaryWriter.PicoLocatePreviewRateHz;

                bool hasLeft = leftOsc != null && leftOsc.Count >= 2;
                bool hasRight = rightPd != null && rightPd.Count >= 2;
                if (!hasLeft && !hasRight)
                {
                    AppendLog("无实测 AI4/Pico preview，下图不回落合成。");
                    _plotPdDemod.EmptyHint = "无实测振荡/局放…";
                    _plotLocate.EmptyHint = "无实测…";
                    return;
                }

                // —— 显示/量程分流（用户权威）：检波 + 定位原始局放 = 滤波后；仅量程参考/超量程横幅用未滤波 Pico ——
                // 预览 CSV 有 stride：必须用 Pico 有效采样率，不能拿名义 125MS/s，也不能被 AI4 Fs 覆盖。
                List<PointF> pdForCalc = rightPd;
                bool hpOn = _chkHighPass.Checked;
                double cutoff = (double)_numHpCutoffHz.Value;
                string calcSrc = rightSrc;
                double picoFilterHz = EstimateEffectiveSampleRateHz(rightPd, picoEffForFilter > 0 ? picoEffForFilter : picoHz);
                if (hpOn && hasRight && picoFilterHz >= 20000)
                {
                    try
                    {
                        double[] raw = PointsToY(rightPd);
                        double[] filt = FactoryPdHighPass.Apply(raw, picoFilterHz, cutoff);
                        pdForCalc = YToPoints(rightPd, filt);
                        calcSrc = (rightSrc ?? "Pico") + "+HP@" + cutoff.ToString("0", CultureInfo.InvariantCulture)
                            + "Hz@FsEff=" + picoFilterHz.ToString("0.#", CultureInfo.InvariantCulture);
                    }
                    catch (Exception fx)
                    {
                        AppendLog("高通滤波跳过: " + fx.Message);
                        pdForCalc = rightPd;
                    }
                }
                else if (hpOn && hasRight && picoFilterHz < 20000)
                    AppendLog("高通跳过：Pico 有效采样率 <20 kS/s（FsEff="
                        + picoFilterHz.ToString("0.#", CultureInfo.InvariantCulture) + "）。");

                // —— 用户权威：左=完整采集振荡+局放；右=同一段原始数据首零起 N 周期切电压+局放 ——
                // 左右强制同 stem（AI4+Pico）同 t0；周期用真完整周期（拒 meta 倍频半周）；
                // 右窗满采样局放峰保留降采样，与左窗同窗可见尖峰对应。
                double pdFactor = PdCalibrationFactorFromUi(); // pC/V；检波与定位原始局放均 ×系数 → 放电量(pC)
                float pdScalePc = (float)PdScalePcFromUi();   // 标尺满量程 pC
                int nCycles = Math.Max(1, (int)_numLocateCycles.Value);
                double preferFreq = (!double.IsNaN(_lastOscFreqHz) && _lastOscFreqHz > 0) ? _lastOscFreqHz : double.NaN;
                float t0Ms, t1Ms;
                double usedPeriodMs;
                // 真首零：必须用振荡电压通道（AI4 原始）重算；禁止用平滑曲线/数据起点冒充
                List<PointF> locateOsc = BuildLocateOscWindow(
                    leftOscRaw, leftOscDisplay, artHz, nCycles, preferFreq,
                    out t0Ms, out t1Ms, out usedPeriodMs);
                if (!(t1Ms > t0Ms) || !(usedPeriodMs > 1e-9) || locateOsc == null || locateOsc.Count < 2)
                {
                    AppendLog("定位：振荡电压通道未找到真首零过0点，拒绝错起点切窗。");
                    t0Ms = 0; t1Ms = 0; usedPeriodMs = 0;
                    locateOsc = null;
                }
                else if (usedPeriodMs > 1e-9 && (t1Ms - t0Ms) + 1e-6 < usedPeriodMs * nCycles)
                    t1Ms = (float)(t0Ms + usedPeriodMs * nCycles);

                // 右窗优先满采样 .pico.raw.i16（与左同 stem）；失败禁止用 80k 预览冒充定位
                string locateSrc = null;
                double locateFsHz = 0;
                List<PointF> locatePts = null;
                if (locateOsc != null && t1Ms > t0Ms)
                {
                    locatePts = TryBuildLocateFromFullRateRaw(
                        waveDir, pairedStem, nCycles, preferFreq, hasLeft ? leftOscRaw : null, artHz,
                        hpOn, cutoff, t0Ms, t1Ms, usedPeriodMs,
                        out t0Ms, out t1Ms, out usedPeriodMs, out locateSrc, out locateFsHz);
                }
                if (locatePts == null || locatePts.Count < 2)
                {
                    AppendLog("定位：无满采样 pico.raw.i16，拒绝 80k 预览切片（须按 125MS/s×周期满采样）。");
                    locatePts = null;
                }

                // —— 左窗：完整采集（全段时间轴，不相对首零归零）——
                List<PointF> fullOscPlot = hasLeft ? leftOscDisplay : null;
                List<PointF> pdAbsFull = null;
                if (pdForCalc != null && pdForCalc.Count >= 2)
                {
                    pdAbsFull = new List<PointF>(pdForCalc.Count);
                    for (int i = 0; i < pdForCalc.Count; i++)
                        pdAbsFull.Add(new PointF(pdForCalc[i].X, Math.Abs(pdForCalc[i].Y)));
                }
                List<PointF> demod = AbsPeakDetectAligned(fullOscPlot, pdAbsFull);
                List<PointF> demodPc = ScaleYByFactor(demod, (float)pdFactor);

                string freqNote = usedPeriodMs > 1e-9
                    ? (" · f≈" + (1000.0 / usedPeriodMs).ToString("0.#", CultureInfo.InvariantCulture) + "Hz")
                    : "";
                string fsNote = locateFsHz > 0
                    ? (" · " + PressTestConfig.FormatPicoRate(locateFsHz) + "×" + (locatePts != null ? locatePts.Count : 0) + "点")
                    : "";
                string stemNote = !string.IsNullOrEmpty(pairedStem) ? (" · stem=" + pairedStem) : "";
                string t0Note = (locateOsc != null && t1Ms > t0Ms)
                    ? (" · 右切t0=" + t0Ms.ToString("G4", CultureInfo.InvariantCulture) + "ms")
                    : "";

                _plotPdDemod.XLabel = "t / ms（完整采集）";
                _plotPdDemod.YLeftLabel = "振荡 / kV";
                _plotPdDemod.YRightLabel = "检波放电量 / pC";
                _plotPdDemod.LeftTraceColor = Color.FromArgb(0, 120, 180);
                _plotPdDemod.RightTraceColor = Color.FromArgb(200, 80, 40);
                _plotPdDemod.RightAsHistogramFill = true;
                _plotPdDemod.Title = "检波（完整采集振荡+局放）"
                    + (hpOn ? (" · HP " + cutoff.ToString("0.#") + "Hz") : " · 未滤波")
                    + stemNote + t0Note
                    + " · 标尺 " + FormatPdScaleLabel(pdScalePc);
                bool leftOk = false;
                bool hasFullOsc = fullOscPlot != null && fullOscPlot.Count >= 2;
                if (hasFullOsc && demodPc != null && demodPc.Count >= 2)
                {
                    _plotPdDemod.SetDualSeries(fullOscPlot, demodPc, autoScale: true);
                    _plotPdDemod.SetFixedYR(0f, pdScalePc); // 检波绝对值：0～标尺
                    leftOk = true;
                }
                else if (demodPc != null && demodPc.Count >= 2)
                {
                    _plotPdDemod.YLeftLabel = "振荡 / kV";
                    _plotPdDemod.YRightLabel = "检波放电量 / pC";
                    _plotPdDemod.RightAsHistogramFill = true;
                    _plotPdDemod.SetDualSeries(null, demodPc, autoScale: true);
                    _plotPdDemod.SetFixedYR(0f, pdScalePc);
                    leftOk = true;
                }
                else if (hasFullOsc)
                {
                    _plotPdDemod.RightAsHistogramFill = false;
                    _plotPdDemod.SetSeries(fullOscPlot, autoScale: true);
                    leftOk = true;
                }
                // 超量程横幅：量程参考 → 未滤波 rightPd 峰（禁止用滤波后检波峰）
                if (leftOk && hasRight)
                {
                    float peakRaw = 0;
                    foreach (var pt in rightPd) { float a = Math.Abs(pt.Y); if (a > peakRaw) peakRaw = a; }
                    bool over = _lastPicoOverrange || (_lastPicoFullScaleV > 0 && peakRaw >= _lastPicoFullScaleV * 0.80f);
                    _plotPdDemod.SetOverrangeMarker(over, (float)_lastPicoFullScaleV,
                        over ? ("超量程/饱和 ±" + _lastPicoFullScaleV.ToString("0.##", CultureInfo.InvariantCulture) + "V") : null);
                }
                else if (!leftOk)
                {
                    _plotPdDemod.ClearSeries();
                    _plotPdDemod.EmptyHint = hasLeft
                        ? "无同源局放检波…"
                        : "需 AI4/Pico 完整采集…";
                }

                // 右窗：同一段原始数据（同stem）首零起 N 周期切电压+局放；相对 t0=0
                bool hasLocOsc = locateOsc != null && locateOsc.Count >= 2;
                _plotLocate.Title = "定位（同原始·首零起 " + nCycles + " 周期切电压+局放）"
                    + (hpOn ? (" · HP " + cutoff.ToString("0.#") + "Hz") : " · 未滤波")
                    + freqNote + fsNote + t0Note
                    + " · ±" + FormatPdScaleLabel(pdScalePc);
                // 满采样→|峰|保留降采样 + 双极 stem：与左同 stem/t0，尖峰可见，禁止紫带
                List<PointF> locatePtsForPlot = PeakPreserveDownsample(locatePts, 8000);
                List<PointF> locatePtsPc = ScaleYByFactor(locatePtsForPlot, (float)pdFactor);
                _plotLocate.XLabel = "t / ms（首零=0）";
                _plotLocate.YLeftLabel = "振荡 / kV";
                _plotLocate.YRightLabel = "定位放电量 / pC";
                _plotLocate.LeftTraceColor = Color.FromArgb(0, 120, 180);
                _plotLocate.RightTraceColor = Color.FromArgb(120, 60, 180);
                _plotLocate.RightAsHistogramFill = false;
                _plotLocate.RightAsBipolarStem = true; // 定位原始：stem，禁止 DrawLines 紫带
                bool hasLocPd = locatePtsPc != null && locatePtsPc.Count >= 2;
                // 右窗绑数：优先双轴；局放纵轴按标尺 ±对称
                if (hasLocOsc && hasLocPd)
                {
                    _plotLocate.SetDualSeries(locateOsc, locatePtsPc, autoScale: true);
                    _plotLocate.SetFixedYR(-pdScalePc, pdScalePc);
                }
                else if (hasLocPd)
                {
                    // 缺振荡时仍显示局放，勿标成振荡；单轴亦 ±对称
                    _plotLocate.YLeftLabel = "定位放电量 / pC";
                    _plotLocate.YRightLabel = "";
                    _plotLocate.Title = "定位（局放·" + (hasLeft ? "相位窗" : "缺分压器") + "）"
                        + (hpOn ? (" · HP " + cutoff.ToString("0.#") + "Hz") : "")
                        + " · ±" + FormatPdScaleLabel(pdScalePc);
                    _plotLocate.SetSeries(locatePtsPc, autoScale: true);
                    _plotLocate.SetFixedYL(-pdScalePc, pdScalePc);
                }
                else if (hasLocOsc)
                    _plotLocate.SetSeries(locateOsc, autoScale: true);
                else
                {
                    _plotLocate.ClearSeries();
                    _plotLocate.EmptyHint = hasRight ? "定位窗截取失败…" : (hasLeft ? "未找到相位起点…" : "需 AI4 分压器定相位…");
                }

                _oscillationShown = true;
                float peakR = 0;
                if (locatePts != null) foreach (var pt in locatePts) { float a = Math.Abs(pt.Y); if (a > peakR) peakR = a; }
                else if (pdForCalc != null) foreach (var pt in pdForCalc) { float a = Math.Abs(pt.Y); if (a > peakR) peakR = a; }
                AppendLog("下图：左=完整采集 / 右=同stem同t0首零起N周期切 stem=" + (pairedStem ?? "-")
                    + "；左检波(全段直方=" + (demod != null ? demod.Count : 0) + " src=" + (calcSrc ?? "-") + ")"
                    + "；右定位=真Pico.raw.i16×pdCal N=" + nCycles
                    + " T=" + usedPeriodMs.ToString("G4", CultureInfo.InvariantCulture) + "ms"
                    + " t0=" + t0Ms.ToString("G4", CultureInfo.InvariantCulture)
                    + "ms t1=" + t1Ms.ToString("G4", CultureInfo.InvariantCulture)
                    + "ms osc(AI4切)=" + (locateOsc != null ? locateOsc.Count : 0)
                    + " osc(全段)=" + (fullOscPlot != null ? fullOscPlot.Count : 0)
                    + " pd(fullRateHP)=" + (locatePts != null ? locatePts.Count : 0)
                    + " pd(plot)=" + (locatePtsForPlot != null ? locatePtsForPlot.Count : 0)
                    + " src=" + (locateSrc ?? "-")
                    + " Fs=" + locateFsHz.ToString("0.#", CultureInfo.InvariantCulture)
                    + " previewFsEff=" + picoFilterHz.ToString("0.#", CultureInfo.InvariantCulture)
                    + " unifiedFs=" + unifiedHz.ToString("0.#", CultureInfo.InvariantCulture)
                    + " metaF=" + (double.IsNaN(_lastOscFreqHz) ? "NaN" : _lastOscFreqHz.ToString("0.#", CultureInfo.InvariantCulture))
                    + " usedF=" + (usedPeriodMs > 1e-9 ? (1000.0 / usedPeriodMs).ToString("0.#", CultureInfo.InvariantCulture) : "NaN")
                    + " pdCal=" + pdFactor.ToString("G6", CultureInfo.InvariantCulture)
                    + "；引出=" + (calcSrc ?? "无") + " peak=" + peakR.ToString("G4", CultureInfo.InvariantCulture) + "V"
                    + "；AI4=" + (leftSrc ?? "无")
                    + "；左=完整采集平滑振荡+|HP预览|直方；右=同stem同t0完整周期电压+真Pico原始峰保留×cal。");
            }
            catch (Exception ex)
            {
                AppendLog("加载 preview CSV 失败（不回落合成）: " + ex.Message);
                _plotPdDemod.EmptyHint = "preview 加载失败…";
                _plotLocate.EmptyHint = "preview 加载失败…";
            }
        }

        static double[] PointsToY(List<PointF> pts)
        {
            var y = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++) y[i] = pts[i].Y;
            return y;
        }

        static List<PointF> YToPoints(List<PointF> xRef, double[] y)
        {
            int n = Math.Min(xRef.Count, y.Length);
            var o = new List<PointF>(n);
            for (int i = 0; i < n; i++)
                o.Add(new PointF(xRef[i].X, (float)y[i]));
            return o;
        }

        /// <summary>
        /// 局放绝对值峰值检波（仅左窗直方）：按振荡周期的一小段做包络峰值，
        /// 与分压器振荡同 X。勿对每个样点取 |PD|（同 Fs 时会变成高频噪声墙）。
        /// 振荡曲线本身不检波、不经 Pico。
        /// </summary>
        /// <summary>
        /// 显示用：分压器振荡轻平滑（对称加权移动平均）。不改采集/量程/落盘；仅 UI。
        /// </summary>
        static List<PointF> SmoothOscillationForDisplay(List<PointF> oscMs)
        {
            if (oscMs == null || oscMs.Count < 7) return oscMs;
            double span = oscMs[oscMs.Count - 1].X - oscMs[0].X;
            if (!(span > 1e-9)) return oscMs;
            double dt = span / (oscMs.Count - 1);
            // ~0.08 ms 半窗：抑锯齿，保留阻尼正弦形态
            int half = Math.Max(2, (int)Math.Round(0.08 / Math.Max(1e-6, dt)));
            if (half > oscMs.Count / 5) half = Math.Max(2, oscMs.Count / 5);
            int win = half * 2 + 1;
            var outPts = new List<PointF>(oscMs.Count);
            for (int i = 0; i < oscMs.Count; i++)
            {
                double sum = 0, wsum = 0;
                for (int k = -half; k <= half; k++)
                {
                    int j = i + k;
                    if (j < 0) j = -j;
                    if (j >= oscMs.Count) j = 2 * oscMs.Count - 2 - j;
                    if (j < 0 || j >= oscMs.Count) continue;
                    double w = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * (k + half) / Math.Max(1, win - 1));
                    sum += oscMs[j].Y * w;
                    wsum += w;
                }
                float y = wsum > 1e-12 ? (float)(sum / wsum) : oscMs[i].Y;
                outPts.Add(new PointF(oscMs[i].X, y));
            }
            return outPts;
        }

        static List<PointF> AbsPeakDetectAligned(List<PointF> oscMs, List<PointF> pdMs)
        {
            if (pdMs == null || pdMs.Count < 2) return null;
            if (oscMs == null || oscMs.Count < 2)
            {
                // 无振荡格：粗包络（约 0.05ms 仓），避免整段 |PD| 噪声
                const float fallbackBinMs = 0.05f;
                var dem = new List<PointF>();
                int i = 0;
                while (i < pdMs.Count)
                {
                    float t0 = pdMs[i].X;
                    float t1 = t0 + fallbackBinMs;
                    float peak = 0;
                    int k = i;
                    while (k < pdMs.Count && pdMs[k].X < t1 + 1e-9f)
                    {
                        float a = Math.Abs(pdMs[k].Y);
                        if (a > peak) peak = a;
                        k++;
                    }
                    dem.Add(new PointF(t0, peak));
                    if (k <= i) k = i + 1;
                    i = k;
                }
                return dem.Count >= 2 ? dem : null;
            }

            // 估计振荡周期 → 包络仓≈周期/40（至少 0.05ms，至多 0.5ms）
            double periodMs = EstimateOscPeriodMs(oscMs);
            float binMs = (float)Math.Max(0.05, Math.Min(0.5, periodMs / 40.0));
            var o = new List<PointF>();
            int j = 0;
            float tStart = oscMs[0].X;
            float tEnd = oscMs[oscMs.Count - 1].X;
            for (float t0 = tStart; t0 <= tEnd + 1e-9f; t0 += binMs)
            {
                float t1 = t0 + binMs;
                float peak = 0;
                while (j < pdMs.Count && pdMs[j].X < t0 - 1e-9f) j++;
                int k = j;
                while (k < pdMs.Count && pdMs[k].X < t1 + 1e-9f)
                {
                    float a = Math.Abs(pdMs[k].Y);
                    if (a > peak) peak = a;
                    k++;
                }
                o.Add(new PointF(t0, peak));
            }
            return o.Count >= 2 ? o : null;
        }

        /// <summary>由分压器上升过零估振荡周期（ms）；失败回退 ~600Hz。</summary>
        static double EstimateOscPeriodMs(List<PointF> oscMs)
        {
            if (oscMs == null || oscMs.Count < 8) return 1000.0 / 600.0;
            double mean = 0;
            for (int i = 0; i < oscMs.Count; i++) mean += oscMs[i].Y;
            mean /= oscMs.Count;
            var rise = new List<float>();
            for (int i = 1; i < oscMs.Count; i++)
            {
                double a = oscMs[i - 1].Y - mean;
                double b = oscMs[i].Y - mean;
                if (a < 0 && b >= 0) rise.Add(oscMs[i].X);
            }
            var periods = new List<double>();
            for (int k = 1; k < rise.Count; k++)
            {
                double p = rise[k] - rise[k - 1];
                if (!(p > 1e-9)) continue;
                double f = 1000.0 / p;
                if (f >= BasicMetricsCalculator.MinFrequencyHz && f <= BasicMetricsCalculator.MaxFrequencyHz)
                    periods.Add(p);
            }
            if (periods.Count == 0) return 1000.0 / 600.0;
            periods.Sort();
            return periods[periods.Count / 2];
        }

        /// <summary>
        /// 振荡电压通道重算真首零（上升过 0），再截随后 N 个完整周期；局放须用同一 t0。
        /// rawForZero：未平滑 AI4（强制）；displayOsc：可选平滑仅用于绘图。禁止用数据起点/平滑错起点。
        /// </summary>
        static List<PointF> BuildLocateOscWindow(List<PointF> rawForZero, List<PointF> displayOsc, double artHz, int nCycles,
            double preferredFreqHz, out float t0Ms, out float t1Ms, out double periodMs)
        {
            t0Ms = 0; t1Ms = 0; periodMs = 0;
            if (rawForZero == null || rawForZero.Count < 8 || nCycles < 1) return null;

            float firstZeroMs;
            double periodFromZerosMs;
            if (!TryFindTrueFirstRisingZero(rawForZero, preferredFreqHz, out firstZeroMs, out periodFromZerosMs))
                return null;

            t0Ms = firstZeroMs;
            // 周期必须用 TryFindTrueFirstRisingZero 已校验的结果（preferredFreq 仅当与
            // 完整周期中位相差<35% 才被采纳）。禁止此处再盲用 meta 倍频（常见 2×）把 N 周期切成半周。
            if (periodFromZerosMs > 1e-9)
                periodMs = periodFromZerosMs;
            else
                return null; // 禁止 600Hz 错周期冒充

            float fullT1 = (float)(t0Ms + periodMs * nCycles);
            t1Ms = fullT1;
            List<PointF> src = (displayOsc != null && displayOsc.Count >= 2) ? displayOsc : rawForZero;
            float dataEnd = src[src.Count - 1].X;
            float oscEnd = fullT1 > dataEnd ? dataEnd : fullT1;
            if (!(oscEnd > t0Ms)) return null;

            // 窗起点=真首零：强制 (0,0)，再跟随后续样点（相对 t0）；局放同 t0 切窗
            var win = new List<PointF>();
            win.Add(new PointF(0f, 0f));
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i].X <= t0Ms + 1e-9f) continue;
                if (src[i].X > oscEnd + 1e-9f) break;
                win.Add(new PointF(src[i].X - t0Ms, src[i].Y));
            }
            t1Ms = fullT1;
            return win.Count >= 2 ? win : null;
        }

        /// <summary>兼容旧签名：raw=display 同源。</summary>
        static List<PointF> BuildLocateOscWindow(List<PointF> oscKv, double artHz, int nCycles,
            double preferredFreqHz, out float t0Ms, out float t1Ms, out double periodMs)
        {
            return BuildLocateOscWindow(oscKv, oscKv, artHz, nCycles, preferredFreqHz, out t0Ms, out t1Ms, out periodMs);
        }

        /// <summary>
        /// 振荡电压真首零：AI4 上升过 0（插值）。
        /// 取「其后紧跟带内完整周期」的第一个上升过零：两上升过零之间须有正负峰（拒半周倍频），
        /// 并合并过近噪声双过零。周期优先用校验后的带内中位（preferredFreq 仅当其与中位相差<35%时采用）。
        /// 优先绝对 Y=0；整段不跨 0 则去直流。禁止无完整带内周期仍返回 true。
        /// </summary>
        static bool TryFindTrueFirstRisingZero(List<PointF> oscKv, double preferredFreqHz,
            out float firstZeroMs, out double periodMs)
        {
            firstZeroMs = 0; periodMs = 0;
            if (oscKv == null || oscKv.Count < 8) return false;

            double mean = 0, yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
            for (int i = 0; i < oscKv.Count; i++)
            {
                double y = oscKv[i].Y;
                mean += y;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
            mean /= oscKv.Count;
            bool straddlesZero = yMin < -1e-6 && yMax > 1e-6;
            double baseline = straddlesZero ? 0.0 : mean;
            double peakAbs = Math.Max(Math.Abs(yMin - baseline), Math.Abs(yMax - baseline));
            double ampGate = peakAbs > 1e-12 ? peakAbs * 0.12 : 0;

            var riseT = new List<float>();
            var riseI = new List<int>();
            double minGapMs = 1000.0 / BasicMetricsCalculator.MaxFrequencyHz; // 合并噪声双过零
            for (int i = 1; i < oscKv.Count; i++)
            {
                double y0 = oscKv[i - 1].Y - baseline;
                double y1 = oscKv[i].Y - baseline;
                if (!(y0 < 0 && y1 >= 0)) continue;
                double denom = y1 - y0;
                double frac = Math.Abs(denom) > 1e-18 ? (-y0 / denom) : 0.0;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                float t = (float)(oscKv[i - 1].X + frac * (oscKv[i].X - oscKv[i - 1].X));
                if (riseT.Count > 0 && (t - riseT[riseT.Count - 1]) < minGapMs)
                {
                    riseT[riseT.Count - 1] = t;
                    riseI[riseI.Count - 1] = i;
                    continue;
                }
                riseT.Add(t);
                riseI.Add(i);
            }
            if (riseT.Count < 2) return false;

            // 带内完整周期（正负峰）；首零 = 第一个开启完整带内周期的上升过零
            var inband = new List<double>();
            int firstInbandIdx = -1;
            for (int k = 1; k < riseT.Count; k++)
            {
                double pMs = riseT[k] - riseT[k - 1];
                if (!(pMs > 1e-9)) continue;
                double fHz = 1000.0 / pMs;
                if (fHz < BasicMetricsCalculator.MinFrequencyHz
                    || fHz > BasicMetricsCalculator.MaxFrequencyHz)
                    continue;
                int i0 = riseI[k - 1];
                int i1 = riseI[k];
                double segMax = double.NegativeInfinity, segMin = double.PositiveInfinity;
                for (int j = i0; j <= i1 && j < oscKv.Count; j++)
                {
                    double y = oscKv[j].Y - baseline;
                    if (y > segMax) segMax = y;
                    if (y < segMin) segMin = y;
                }
                if (!(segMax >= ampGate && segMin <= -ampGate)) continue;
                inband.Add(pMs);
                if (firstInbandIdx < 0) firstInbandIdx = k - 1;
            }
            if (firstInbandIdx < 0 || inband.Count == 0) return false;

            firstZeroMs = riseT[firstInbandIdx];
            inband.Sort();
            double medianPeriodMs = inband[inband.Count / 2];
            double preferPeriodMs = 0;
            if (preferredFreqHz >= BasicMetricsCalculator.MinFrequencyHz
                && preferredFreqHz <= BasicMetricsCalculator.MaxFrequencyHz)
                preferPeriodMs = 1000.0 / preferredFreqHz;
            // preferredFreq 若与完整周期中位相差≥35%（常见倍频错误）则弃用，改用中位
            if (preferPeriodMs > 1e-9 && medianPeriodMs > 1e-9
                && Math.Abs(preferPeriodMs - medianPeriodMs) / medianPeriodMs < 0.35)
                periodMs = preferPeriodMs;
            else
                periodMs = medianPeriodMs;
            return periodMs > 1e-9;
        }


        /// <summary>
        /// 选最新一档「AI4+Pico」同 stem。优先含 pico.raw.i16（真定位源）；否则 preview 配对。
        /// 禁止左右各取最新导致不同 step 错相位。
        /// </summary>
        static string FindLatestPairedWaveformStem(string waveDir)
        {
            if (string.IsNullOrWhiteSpace(waveDir) || !Directory.Exists(waveDir)) return null;
            string bestStem = null;
            DateTime bestUtc = DateTime.MinValue;
            bool bestHasRaw = false;
            string[] ai4s;
            try { ai4s = Directory.GetFiles(waveDir, "*.ai4.preview.csv"); }
            catch { return null; }
            if (ai4s == null || ai4s.Length == 0) return null;
            for (int i = 0; i < ai4s.Length; i++)
            {
                string name = Path.GetFileName(ai4s[i]);
                if (name == null || !name.EndsWith(".ai4.preview.csv", StringComparison.OrdinalIgnoreCase))
                    continue;
                string stem = name.Substring(0, name.Length - ".ai4.preview.csv".Length);
                string picoPrev = Path.Combine(waveDir, stem + ".pico.preview.csv");
                string picoRaw = Path.Combine(waveDir, stem + ".pico.raw.i16");
                bool hasPrev = File.Exists(picoPrev);
                bool hasRaw = File.Exists(picoRaw);
                if (!hasPrev && !hasRaw) continue;
                DateTime m = File.GetLastWriteTimeUtc(ai4s[i]);
                try
                {
                    if (hasRaw)
                    {
                        DateTime mr = File.GetLastWriteTimeUtc(picoRaw);
                        if (mr > m) m = mr;
                    }
                    else if (hasPrev)
                    {
                        DateTime mp = File.GetLastWriteTimeUtc(picoPrev);
                        if (mp > m) m = mp;
                    }
                }
                catch { }
                // 有 raw 的档优先于仅 preview；同档级再比 mtime
                if (bestStem == null
                    || (hasRaw && !bestHasRaw)
                    || (hasRaw == bestHasRaw && m >= bestUtc))
                {
                    bestStem = stem;
                    bestUtc = m;
                    bestHasRaw = hasRaw;
                }
            }
            return bestStem;
        }

        /// <summary>
        /// 定位：从 stem.pico.raw.i16 按名义 Fs（≈125MS/s）满采样切片 N 周期，再 HP（不检波）→ 点列（V）。
        /// 禁止用 80k preview。约 32 万点/周期@~390Hz；滤波在切片+延拓上做。
        /// </summary>
        List<PointF> TryBuildLocateFromFullRateRaw(
            string waveDir, string preferredStem, int nCycles, double preferFreqHz, List<PointF> leftOsc, double artHz,
            bool hpOn, double cutoffHz, float t0MsIn, float t1MsIn, double periodMsIn,
            out float t0Ms, out float t1Ms, out double usedPeriodMs, out string srcNote, out double fsHz)
        {
            t0Ms = t0MsIn; t1Ms = t1MsIn; usedPeriodMs = periodMsIn;
            srcNote = null; fsHz = 0;
            if (string.IsNullOrWhiteSpace(waveDir) || !Directory.Exists(waveDir)) return null;
            string rawPath = null;
            string stem = preferredStem;
            if (!string.IsNullOrEmpty(stem))
            {
                string cand = Path.Combine(waveDir, stem + ".pico.raw.i16");
                if (File.Exists(cand)) rawPath = cand;
            }
            if (rawPath == null)
            {
                // 无配对 stem 的 raw 时拒用其它档最新 raw（会与 AI4 t0 错档）
                AppendLog("定位满采样：stem=" + (preferredStem ?? "-") + " 无 pico.raw.i16，拒绝错档最新 raw。");
                return null;
            }
            if (string.IsNullOrEmpty(stem))
            {
                stem = Path.GetFileName(rawPath);
                if (stem.EndsWith(".pico.raw.i16", StringComparison.OrdinalIgnoreCase))
                    stem = stem.Substring(0, stem.Length - ".pico.raw.i16".Length);
            }
            string metaPath = Path.Combine(waveDir, stem + ".meta.txt");
            double picoHz = _lastPicoRateHz > 0 ? _lastPicoRateHz : 125e6;
            double fullScale = _lastPicoFullScaleV > 0 ? _lastPicoFullScaleV : 1.0;
            short maxAdc = 32512;
            if (File.Exists(metaPath))
            {
                foreach (string line in File.ReadAllLines(metaPath))
                {
                    if (line.StartsWith("picoSampleRateHz=", StringComparison.OrdinalIgnoreCase))
                    {
                        double v; if (double.TryParse(line.Substring(17).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) picoHz = v;
                    }
                    else if (line.StartsWith("picoFullScaleV=", StringComparison.OrdinalIgnoreCase))
                    {
                        double v; if (double.TryParse(line.Substring(15).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) fullScale = v;
                    }
                    else if (line.StartsWith("picoMaximumAdc=", StringComparison.OrdinalIgnoreCase))
                    {
                        short v; if (short.TryParse(line.Substring(15).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0) maxAdc = v;
                    }
                }
            }
            fsHz = picoHz;
            byte[] bytes = File.ReadAllBytes(rawPath);
            if (bytes.Length < 4 || (bytes.Length % 2) != 0) return null;
            int n = bytes.Length / 2;
            var adc = new short[n];
            Buffer.BlockCopy(bytes, 0, adc, 0, bytes.Length);

            if (!(usedPeriodMs > 1e-9))
            {
                if (preferFreqHz > 0) usedPeriodMs = 1000.0 / preferFreqHz;
                else if (leftOsc != null) usedPeriodMs = EstimateOscPeriodMs(leftOsc);
                else usedPeriodMs = 1000.0 / 600.0;
            }
            if (!(t1Ms > t0Ms))
            {
                // 禁止 t0=0 冒充首零：无有效真首零窗则拒切
                return null;
            }
            // ms → 样点（满采样）；与 AI4 同触发零点时间轴对齐
            int i0 = (int)Math.Floor(t0Ms * picoHz / 1000.0);
            int i1 = (int)Math.Ceiling(t1Ms * picoHz / 1000.0);
            if (i0 < 0) i0 = 0;
            if (i1 > n) i1 = n;
            if (i1 - i0 < 2)
            {
                // 禁止回退到样点 0（会与振荡首零错位）；宁缺勿错起点
                AppendLog("定位满采样：真首零窗映射样点不足（t0="
                    + t0Ms.ToString("G4", CultureInfo.InvariantCulture) + "ms），拒绝错起点。");
                return null;
            }

            // 切片 + 短延拓供零相位滤波；再裁回窗
            int pad = 0;
            if (hpOn && picoHz >= 20000)
            {
                try { pad = Math.Min(i0, Math.Max(64, (int)Math.Ceiling(3.0 * picoHz / Math.Max(100.0, cutoffHz)))); }
                catch { pad = 0; }
            }
            int s0 = Math.Max(0, i0 - pad);
            int s1 = Math.Min(n, i1 + pad);
            int len = s1 - s0;
            var volts = new double[len];
            double scale = maxAdc > 0 ? fullScale / maxAdc : 0;
            for (int i = 0; i < len; i++)
                volts[i] = adc[s0 + i] * scale;

            double[] use = volts;
            if (hpOn && picoHz >= 20000)
            {
                try { use = FactoryPdHighPass.Apply(volts, picoHz, cutoffHz); }
                catch (Exception ex)
                {
                    AppendLog("定位满采样 HP 跳过: " + ex.Message);
                    use = volts;
                }
            }
            int trim0 = i0 - s0;
            int outN = i1 - i0;
            if (trim0 < 0) trim0 = 0;
            if (trim0 + outN > use.Length) outN = use.Length - trim0;
            if (outN < 2) return null;
            var pts = new List<PointF>(outN);
            double msPer = 1000.0 / picoHz;
            for (int i = 0; i < outN; i++)
                pts.Add(new PointF((float)(i * msPer), (float)use[trim0 + i]));
            srcNote = Path.GetFileName(rawPath) + (hpOn ? "+HP" : "");
            return pts;
        }


        /// <summary>截取 [t0,t1] 的局放（相对 t0；不做检波、不降采样；定位通道用滤波后原始）。</summary>
        static List<PointF> SlicePdWindow(List<PointF> pd, float t0Ms, float t1Ms)
        {
            if (pd == null || pd.Count < 2 || !(t1Ms > t0Ms)) return null;
            var win = new List<PointF>();
            for (int i = 0; i < pd.Count; i++)
            {
                if (pd[i].X + 1e-9f < t0Ms) continue;
                if (pd[i].X > t1Ms + 1e-9f) break;
                win.Add(new PointF(pd[i].X - t0Ms, pd[i].Y));
            }
            return win.Count >= 2 ? win : null;
        }

        /// <summary>兼容：仅局放定位窗（未滤波路径请直接 SlicePdWindow）。</summary>
        static List<PointF> BuildLocateWindow(List<PointF> oscKv, double artHz, List<PointF> pd, double picoHz,
            int nCycles, double preferredFreqHz = double.NaN)
        {
            float t0, t1;
            double periodMs;
            BuildLocateOscWindow(oscKv, artHz, nCycles, preferredFreqHz, out t0, out t1, out periodMs);
            if (!(t1 > t0) && pd != null && pd.Count >= 4)
            {
                t0 = pd[0].X;
                if (!(periodMs > 1e-9))
                    periodMs = preferredFreqHz > 0 ? (1000.0 / preferredFreqHz)
                        : Math.Max(0.05, (pd[pd.Count - 1].X - pd[0].X) / Math.Max(4, nCycles * 2));
                t1 = (float)(t0 + periodMs * nCycles);
            }
            return SlicePdWindow(pd, t0, t1);
        }

        double ResolveDividerRatioForPlot()
        {
            try
            {
                if (_api != null && _api.Config != null && _api.Config.DividerRatioVPerV > 1)
                    return _api.Config.DividerRatioVPerV;
            }
            catch { }
            return PressTestConfig.TestBenchDividerRatio > 1
                ? PressTestConfig.TestBenchDividerRatio
                : 12000.0;
        }

        static void ScaleYInPlace(List<PointF> pts, float scale)
        {
            if (pts == null || Math.Abs(scale - 1f) < 1e-12f) return;
            for (int i = 0; i < pts.Count; i++)
                pts[i] = new PointF(pts[i].X, pts[i].Y * scale);
        }

        static void RemapXToMs(List<PointF> pts, double sampleRateHz)
        {
            if (pts == null || pts.Count == 0 || sampleRateHz <= 0) return;
            for (int i = 0; i < pts.Count; i++)
            {
                double index = pts[i].X;
                float ms = (float)(index * 1000.0 / sampleRateHz);
                pts[i] = new PointF(ms, pts[i].Y);
            }
        }

        /// <summary>
        /// 预览点已按 stride 抽稀：高通须用有效 Fs（由时间间距推定），而非名义 Pico 采样率。
        /// </summary>


        static readonly double[] PdScaleStepsPc = {
            10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000
        };

        void InitPdScaleCombo()
        {
            _cboPdScale.Items.Clear();
            int select = 3; // 默认 100pC
            for (int i = 0; i < PdScaleStepsPc.Length; i++)
            {
                _cboPdScale.Items.Add(FormatPdScaleLabel(PdScaleStepsPc[i]));
                if (Math.Abs(PdScaleStepsPc[i] - 100.0) < 1e-9) select = i;
            }
            if (_cboPdScale.Items.Count > 0)
                _cboPdScale.SelectedIndex = select;
        }

        static string FormatPdScaleLabel(double pc)
        {
            if (pc >= 1000.0 - 1e-9)
            {
                double nc = pc / 1000.0;
                string s = nc.ToString("0.###", CultureInfo.InvariantCulture);
                return s + "nC";
            }
            return pc.ToString("0.###", CultureInfo.InvariantCulture) + "pC";
        }

        /// <summary>局放绘图标尺满量程（pC）；10～10000，默认 100。</summary>
        double PdScalePcFromUi()
        {
            int idx = _cboPdScale.SelectedIndex;
            if (idx >= 0 && idx < PdScaleStepsPc.Length)
                return PdScaleStepsPc[idx];
            return 100.0;
        }

        double PdCalibrationFactorFromUi()
        {
            double f = (double)_numPdCal.Value;
            if (!(f > 0)) f = PdCalibrationStore.DefaultPdCalibrationPcPerVolt;
            return f;
        }

        static List<PointF> ScaleYByFactor(IList<PointF> src, float factor)
        {
            if (src == null) return null;
            var dst = new List<PointF>(src.Count);
            for (int i = 0; i < src.Count; i++)
                dst.Add(new PointF(src[i].X, src[i].Y * factor));
            return dst;
        }

        /// <summary>
        /// 满采样局放 |峰|保留降采样：每仓只留 |Y| 最大一点（保留符号），供双极 stem 绘制。
        /// 禁止 min+max 交替造成 DrawLines「紫带」淹没与左窗同源尖峰。不改时间窗/t0/幅值标定。
        /// </summary>
        static List<PointF> PeakPreserveDownsample(List<PointF> src, int maxPoints)
        {
            if (src == null || src.Count < 2) return src;
            if (maxPoints < 64) maxPoints = 64;
            if (src.Count <= maxPoints) return src;
            int bins = Math.Max(2, maxPoints);
            var dst = new List<PointF>(bins);
            int n = src.Count;
            for (int b = 0; b < bins; b++)
            {
                int i0 = (int)((long)b * n / bins);
                int i1 = (int)((long)(b + 1) * n / bins);
                if (i1 <= i0) i1 = i0 + 1;
                if (i1 > n) i1 = n;
                int iPeak = i0;
                float aPeak = Math.Abs(src[i0].Y);
                for (int i = i0 + 1; i < i1; i++)
                {
                    float a = Math.Abs(src[i].Y);
                    if (a > aPeak) { aPeak = a; iPeak = i; }
                }
                dst.Add(src[iPeak]);
            }
            return dst.Count >= 2 ? dst : src;
        }

        void InitInjectedChargeCombo()
        {
            _cboInjectedPc.Items.Clear();
            int select = 0;
            // 现场校准器常用 5 nC=5000 pC；下拉双标 pC/(nC) 防把 5pC 当成 5nC
            for (int i = 0; i < PdCalibrationStore.CommonInjectedChargesPc.Length; i++)
            {
                double pc = PdCalibrationStore.CommonInjectedChargesPc[i];
                _cboInjectedPc.Items.Add(FormatChargePcAndNc(pc));
                if (Math.Abs(pc - PdCalibrationStore.DefaultInjectedChargePc) < 1e-9)
                    select = i;
            }
            // 默认选 5000 pC（5 nC）；列表无则回落 DefaultInjectedChargePc
            for (int i = 0; i < PdCalibrationStore.CommonInjectedChargesPc.Length; i++)
            {
                if (Math.Abs(PdCalibrationStore.CommonInjectedChargesPc[i] - 5000.0) < 1e-9)
                { select = i; break; }
            }
            if (_cboInjectedPc.Items.Count > 0)
                _cboInjectedPc.SelectedIndex = select;
        }

        static string FormatChargePcAndNc(double pc)
        {
            if (!(pc > 0) || double.IsNaN(pc) || double.IsInfinity(pc))
                return "—";
            double nC = pc / 1000.0;
            string pcS = pc.ToString("0.###", CultureInfo.InvariantCulture);
            string nCS = nC.ToString("0.###", CultureInfo.InvariantCulture);
            return pcS + " pC (" + nCS + " nC)";
        }

        double InjectedChargePcFromUi()
        {
            int idx = _cboInjectedPc.SelectedIndex;
            if (idx >= 0 && idx < PdCalibrationStore.CommonInjectedChargesPc.Length)
                return PdCalibrationStore.CommonInjectedChargesPc[idx];
            return PdCalibrationStore.DefaultInjectedChargePc;
        }

        void SetInjectedChargeUi(double pc)
        {
            int best = 0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < PdCalibrationStore.CommonInjectedChargesPc.Length; i++)
            {
                double d = Math.Abs(PdCalibrationStore.CommonInjectedChargesPc[i] - pc);
                if (d < bestDiff) { bestDiff = d; best = i; }
            }
            if (_cboInjectedPc.Items.Count > best)
                _cboInjectedPc.SelectedIndex = best;
        }

        void SetPdCalUi(double pcPerVolt, double injectedPc)
        {
            double use = pcPerVolt;
            if (!(use > 0) || double.IsNaN(use) || double.IsInfinity(use))
                use = PdCalibrationStore.DefaultPdCalibrationPcPerVolt; // 无效/0 → 默认 2000
            decimal v = (decimal)Math.Min((double)_numPdCal.Maximum, Math.Max((double)_numPdCal.Minimum, use));
            _numPdCal.Value = v;
            if (injectedPc > 0 && !double.IsNaN(injectedPc))
                SetInjectedChargeUi(injectedPc);
        }

        void TryLoadSavedPdCalibrationIntoUi(bool announce)
        {
            PdCalibrationRecord rec;
            string pathUsed;
            if (!PdCalibrationStore.TryLoad(out rec, out pathUsed))
            {
                SetPdCalUi(PdCalibrationStore.DefaultPdCalibrationPcPerVolt, InjectedChargePcFromUi());
                if (announce)
                    AppendLog("本地无有效校准系数（含为0），已用默认 "
                        + PdCalibrationStore.DefaultPdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture)
                        + " pC/V；路径=" + (pathUsed ?? PdCalibrationStore.DefaultPath()));
                return;
            }
            SetPdCalUi(rec.PdCalibrationPcPerVolt, rec.InjectedChargePc);
            if (announce)
                AppendLog("已加载校准系数 " + rec.PdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture)
                    + " pC/V（注入 " + rec.InjectedChargePc.ToString("G6", CultureInfo.InvariantCulture)
                    + " pC）← " + pathUsed);
        }

        void ApplyComputedPdCalToUi()
        {
            if (_api == null) return;
            double factor = _api.LastComputedPdCalibrationPcPerVolt;
            double peak = _api.LastCalibrationPeakVolts;
            double inj = _api.LastInjectedChargePc;
            if (factor > 0 && !double.IsNaN(factor))
            {
                SetPdCalUi(factor, inj);
                double qCheck = (peak > 0 && factor > 0) ? peak * factor : double.NaN;
                AppendLog("已填入自动校准系数 " + factor.ToString("G9", CultureInfo.InvariantCulture)
                    + " pC/V（注入 " + FormatChargePcAndNc(inj)
                    + " / 峰压 " + peak.ToString("G6", CultureInfo.InvariantCulture)
                    + " V；自检 Q≈" + (double.IsNaN(qCheck) ? "NaN" : qCheck.ToString("G6", CultureInfo.InvariantCulture))
                    + " pC≈" + (double.IsNaN(qCheck) ? "NaN" : (qCheck / 1000.0).ToString("G6", CultureInfo.InvariantCulture))
                    + " nC，应≈注入）。将自动落盘 pd-calibration.json。");
            }
            else
                AppendLog("本次未算出校准系数（检查注入档与脉冲幅度）。");
        }

        void SavePdCalibrationNow()
        {
            double factor = (double)_numPdCal.Value;
            double inj = InjectedChargePcFromUi();
            if (!(factor > 0))
                throw new InvalidOperationException("校准系数为空：请先「校准采集」自动计算，或手工填入 pC/V。");
            double peak = _api != null ? _api.LastCalibrationPeakVolts : double.NaN;
            // 若无峰压记录，用公式反推展示用峰压
            if (!(peak > 0) && factor > 0 && inj > 0)
                peak = inj / factor;
            var rec = new PdCalibrationRecord
            {
                InjectedChargePc = inj,
                PeakVolts = peak,
                PdCalibrationPcPerVolt = factor,
                PicoResolutionBits = _cboPicoBits.SelectedIndex == 1 ? 15 : 14,
                PicoSampleIntervalNanoseconds = LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(
                    PressTestConfig.ResolvePicoTimebase(PicoRateHzFromUi())),
                PicoRangeIndex = RangeIndexFromUi(),
                Notes = "factory-press-ui; " + PdCalibrationStore.FormulaId
            };
            string path = PdCalibrationStore.Save(rec);
            if (_api != null && _api.IsConnected)
            {
                try { _api.ApplyPdCalibrationPcPerVolt(factor, inj); }
                catch (Exception ex) { AppendLog("已保存但写回会话失败: " + ex.Message); }
            }
            AppendLog("校准系数已保存 → " + path
                + "；pdCalibrationPcPerVolt=" + factor.ToString("G9", CultureInfo.InvariantCulture)
                + "；加压启动将自动加载。");
        }

        /// <summary>
        /// 加压前：读 {AppBase}/config/pd-calibration.json（TryLoadAndApplySavedPdCalibration）；
        /// 无效则默认 pdCalibrationPcPerVolt=2000。勿另起文件名。
        /// </summary>
        void EnsurePdCalibrationAppliedForPress()
        {
            double inj = InjectedChargePcFromUi();
            string pathUsed = null;
            string detail = null;
            if (_api != null && _api.IsConnected
                && _api.TryLoadAndApplySavedPdCalibration(out pathUsed, out detail))
            {
                PdCalibrationRecord rec;
                string p;
                if (PdCalibrationStore.TryLoad(out rec, out p))
                    SetPdCalUi(rec.PdCalibrationPcPerVolt, rec.InjectedChargePc);
                AppendLog("加压自动加载已保存校准系数 ← " + (detail ?? pathUsed));
                return;
            }

            // 落盘无效/缺失：默认 2000（与校准采集约定同路径；无可调时兜底）
            double factor = PdCalibrationStore.DefaultPdCalibrationPcPerVolt;
            SetPdCalUi(factor, inj);
            if (_api != null && _api.IsConnected)
            {
                try { _api.ApplyPdCalibrationPcPerVolt(factor, inj); }
                catch (Exception ex) { AppendLog("默认校准系数写回失败: " + ex.Message); }
            }
            AppendLog("无有效 " + PdCalibrationStore.FileName
                + "（" + (detail ?? PdCalibrationStore.DefaultPath()) + "）；加压默认 pdCalibrationPcPerVolt="
                + factor.ToString("G9", CultureInfo.InvariantCulture));
        }


        static double EstimateEffectiveSampleRateHz(List<PointF> ptsMs, double nominalHz)
        {
            if (ptsMs != null && ptsMs.Count >= 2)
            {
                double dtMs = ptsMs[ptsMs.Count - 1].X - ptsMs[0].X;
                if (dtMs > 1e-9)
                {
                    double eff = (ptsMs.Count - 1) * 1000.0 / dtMs;
                    if (eff >= 1000 && eff <= 2e9) return eff;
                }
            }
            return nominalHz > 0 ? nominalHz : 125e6;
        }

        /// <summary>
        /// 加载 preview CSV。跳过 # 注释行找表头；Pico 优先 approxVolts（勿把 ADC 当伏特→噪声墙）。
        /// nominalHz：硬件名义 Fs；effectiveHz：预览有效 Fs（与振荡对齐的 unified）；sequential：索引为 0..N-1。
        /// </summary>
        static List<PointF> LoadPreviewCsv(string path, bool preferApproxVolts,
            out double nominalHz, out double effectiveHz, out bool sequentialIndex)
        {
            nominalHz = 0;
            effectiveHz = 0;
            sequentialIndex = false;
            var pts = new List<PointF>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return pts;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2) return pts;

            int headerIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].Trim();
                if (raw.Length == 0) continue;
                if (raw.StartsWith("#", StringComparison.Ordinal))
                {
                    ParsePreviewCommentRates(raw, ref nominalHz, ref effectiveHz, ref sequentialIndex);
                    continue;
                }
                if (raw.StartsWith("index", StringComparison.OrdinalIgnoreCase))
                {
                    headerIdx = i;
                    break;
                }
            }
            if (headerIdx < 0) return pts;

            string[] cols = lines[headerIdx].Split(',');
            int yCol = 1;
            if (preferApproxVolts)
            {
                for (int c = 0; c < cols.Length; c++)
                    if (cols[c].Trim().Equals("approxVolts", StringComparison.OrdinalIgnoreCase))
                        yCol = c;
            }
            else
            {
                for (int c = 0; c < cols.Length; c++)
                {
                    string h = cols[c].Trim();
                    if (h.Equals("dividerRawV", StringComparison.OrdinalIgnoreCase)
                        || h.Equals("approxVolts", StringComparison.OrdinalIgnoreCase))
                        yCol = c;
                }
            }
            for (int i = headerIdx + 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(',');
                if (parts.Length <= yCol) continue;
                float x, y;
                if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x))
                    continue;
                if (!float.TryParse(parts[yCol].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                    continue;
                pts.Add(new PointF(x, y));
            }
            // 旧文件无 sequential 标记：若索引近似连续且带 effective，按 sequential 处理
            if (!sequentialIndex && effectiveHz > 0 && pts.Count >= 3)
            {
                double d1 = pts[1].X - pts[0].X;
                double d2 = pts[2].X - pts[1].X;
                if (Math.Abs(d1 - 1) < 0.01 && Math.Abs(d2 - 1) < 0.01)
                    sequentialIndex = true;
            }
            return pts;
        }

        static void ParsePreviewCommentRates(string commentLine,
            ref double nominalHz, ref double effectiveHz, ref bool sequentialIndex)
        {
            if (string.IsNullOrEmpty(commentLine)) return;
            if (commentLine.IndexOf("previewIndexMode=sequential", StringComparison.OrdinalIgnoreCase) >= 0)
                sequentialIndex = true;

            // 名义 Fs
            TryParseHzKey(commentLine, "picoSampleRateHz=", ref nominalHz);
            TryParseHzKey(commentLine, "artSampleRateHz=", ref nominalHz);
            // 通道有效 Fs：只用 picoEffective / artEffective；勿用目标 picoLocatePreviewRateHz 覆盖实值
            // （长波形被点数上限压到 ~8k 时，目标仍写 80k，覆盖会导致高通/时间轴错）
            TryParseHzKey(commentLine, "picoEffectiveRateHz=", ref effectiveHz);
            TryParseHzKey(commentLine, "artEffectiveRateHz=", ref effectiveHz);
            // 仅当尚无通道有效 Fs 时才回退目标/旧 unified
            if (!(effectiveHz > 0))
                TryParseHzKey(commentLine, "picoLocatePreviewRateHz=", ref effectiveHz);
            if (!(effectiveHz > 0))
                TryParseHzKey(commentLine, "unifiedPreviewRateHz=", ref effectiveHz);
        }

        static void TryParseHzKey(string commentLine, string key, ref double hzOut)
        {
            int at = commentLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            int start = at + key.Length;
            int end = start;
            while (end < commentLine.Length)
            {
                char ch = commentLine[end];
                if (char.IsWhiteSpace(ch) || ch == ',') break;
                end++;
            }
            double hz;
            if (double.TryParse(commentLine.Substring(start, end - start).Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out hz) && hz > 0)
                hzOut = hz;
        }

        sealed class UiLogWriter : TextWriter
        {
            readonly Action<string> _append;
            public UiLogWriter(Action<string> append) { _append = append; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void WriteLine(string value) { if (value != null) _append(value); }
            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value) || value == "\r" || value == "\n" || value == "\r\n") return;
                _append(value.TrimEnd('\r', '\n'));
            }
        }
    }
}
