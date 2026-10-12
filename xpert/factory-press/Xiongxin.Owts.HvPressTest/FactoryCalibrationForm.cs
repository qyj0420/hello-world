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
    /// 独立「校准采集」：外观对照 approvedUI（电缆振荡波校准系统 / CalibrationProgramControl Theme）；
    /// 底层 FactoryPress A（急停按下可校准；ChannelA Rapid；pd-calibration.json）。
    /// 线性滤波权威：HVX_PD_BandpassFilter 移交包（显示路径经 FactoryPdHighPass）。
    /// </summary>
    public sealed class FactoryCalibrationForm : Form
    {
        // approvedUI / CalibrationProgramControl.Theme
        static readonly Color TopBar = ColorTranslator.FromHtml("#0B2D4D");
        static readonly Color SectionBar = ColorTranslator.FromHtml("#EAF2F8");
        static readonly Color Accent = ColorTranslator.FromHtml("#00A3C7");
        static readonly Color AccentHover = ColorTranslator.FromHtml("#0089AA");
        static readonly Color Canvas = ColorTranslator.FromHtml("#F3F7FB");
        static readonly Color Surface = Color.White;
        static readonly Color Border = ColorTranslator.FromHtml("#CAD8E5");
        static readonly Color Ink = ColorTranslator.FromHtml("#18334D");
        static readonly Color Muted = ColorTranslator.FromHtml("#60758A");
        static readonly Color Danger = ColorTranslator.FromHtml("#C44747");
        static readonly Color Success = ColorTranslator.FromHtml("#238A67");

        FactoryPressApi _api;
        CancellationTokenSource _workCts;
        bool _busy;
        readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 200 };

        readonly Label _hdrStatus = new Label();
        readonly Label _hdrMeta = new Label();
        readonly Label _boxStatus = new Label();
        readonly Label _estopStatus = new Label();
        readonly Label _pdCalStatus = new Label();
        readonly Label _qualityMetrics = new Label();
        readonly Label _badge = new Label();

        readonly NumericUpDown _numLength = MakeNum(1, 50000, 180, 1, 10);
        readonly NumericUpDown _numVelocity = MakeNum(1, 300, 170.5m, 2, 1);
        readonly NumericUpDown _numPulseAvg = MakeNum(2, 64, 8, 0, 1);
        readonly NumericUpDown _numPdCal = MakeNum(0, 1000000, (decimal)PdCalibrationStore.DefaultPdCalibrationPcPerVolt, 3, 1);
        readonly ComboBox _cboImpedance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = YaHei(9.5f) };
        readonly ListBox _lstLevels = new ListBox
        {
            Font = YaHei(10f),
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            BackColor = Surface
        };

        readonly Button _btnConnect = MakeAccentBtn("连接设备");
        readonly Button _btnDisconnect = MakeGhostBtn("断开");
        readonly Button _btnStart = MakePrimaryBtn("开始校准");
        readonly Button _btnCancel = MakeGhostBtn("停止采集");
        readonly Button _btnSave = MakeGhostBtn("保存系数");

        readonly WaveformPlotPanel _plot = new WaveformPlotPanel
        {
            Dock = DockStyle.Fill,
            Title = "TDR 波形",
            Subtitle = "实时反射信号与定位标记",
            XLabel = "t / µs",
            YLabel = "幅度 / V",
            EmptyHint = "按下急停并连接后开始校准",
            ZoomEnabled = true,
            BackColor = Surface
        };
        readonly TextBox _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = YaHei(9f),
            BackColor = Surface,
            BorderStyle = BorderStyle.None,
            ForeColor = Ink
        };

        public FactoryCalibrationForm()
        {
            Text = "电缆振荡波校准系统";
            Font = YaHei(9.5f);
            MinimumSize = new Size(1280, 800);
            Size = new Size(1480, 920);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Canvas;
            DoubleBuffered = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(0),
                BackColor = Canvas
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);
            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildBody(), 0, 1);

            _cboImpedance.Items.AddRange(new object[] { "窄", "宽" });
            _cboImpedance.SelectedIndex = 0;
            InitLevelList();

            _btnConnect.Click += async (s, e) => await SafeUi(ConnectAsync);
            _btnDisconnect.Click += async (s, e) => await SafeUi(DisconnectAsync);
            _btnStart.Click += async (s, e) => await SafeUi(AcquireAsync);
            _btnCancel.Click += (s, e) =>
            {
                try { if (_workCts != null) _workCts.Cancel(); } catch { }
                AppendLog("已请求停止采集。");
            };
            _btnSave.Click += (s, e) =>
            {
                try { SavePdCalibrationNow(); }
                catch (Exception ex) { AppendLog("保存失败: " + ex.Message); }
            };
            _cboImpedance.SelectedIndexChanged += (s, e) =>
            {
                if (_api == null || !_api.IsConnected) return;
                try
                {
                    bool wide = _cboImpedance.SelectedIndex == 1;
                    _api.SetImpedanceBandwidthWide(wide);
                    AppendLog("阻抗带宽 → " + (wide ? "宽" : "窄"));
                }
                catch (Exception ex) { AppendLog("阻抗切换失败: " + ex.Message); }
            };
            _lstLevels.SelectedIndexChanged += (s, e) => UpdateStartCaption();

            _poll.Tick += (s, e) => RefreshState();
            _poll.Start();
            FormClosing += async (s, e) =>
            {
                if (_busy)
                {
                    e.Cancel = true;
                    try { if (_workCts != null) _workCts.Cancel(); } catch { }
                    AppendLog("正在结束采集，请完成后再次关闭。");
                    return;
                }
                _poll.Stop();
                try
                {
                    if (_api != null)
                    {
                        await Task.Run(() => _api.Release()).ConfigureAwait(true);
                        _api.Dispose();
                        _api = null;
                    }
                }
                catch (Exception ex) { AppendLog("释放失败: " + ex.Message); }
            };

            _log.Text =
                "对照 approvedUI · 底层 FactoryPress A\r\n"
                + "1. 按下急停（有效低），确认高压隔离与放电。\r\n"
                + "2. 连接设备，选校准档位（默认 5 nC=5000 pC）后「开始校准」。\r\n"
                + "3. ChannelA：自适量程→最佳档→1%FS+autoMs=100→50/100/400Hz±35%→N 平均。\r\n"
                + "4. 成功落盘同目录 config/pd-calibration.json（injected/peak；默认系数 2000）。\r\n"
                + "5. 线性滤波权威：HVX_PD_BandpassFilter 移交包（禁止自造核）。\r\n";
            TryLoadSavedPdCal(announce: false);
            UpdateStartCaption();
            RefreshState();
        }

        Panel BuildHeader()
        {
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = TopBar, Padding = new Padding(14, 0, 14, 0) };
            var title = new Label
            {
                Text = "电缆振荡波校准系统",
                AutoSize = true,
                Font = YaHei(12f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(14, 14)
            };
            _hdrMeta.AutoSize = true;
            _hdrMeta.Font = YaHei(9.5f);
            _hdrMeta.ForeColor = Color.White;
            _hdrMeta.Location = new Point(420, 16);
            _hdrMeta.Text = "出厂校准采集 / 电缆长度 —";

            _hdrStatus.AutoSize = true;
            _hdrStatus.Font = YaHei(9.5f, FontStyle.Bold);
            _hdrStatus.ForeColor = Success;
            _hdrStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _hdrStatus.Text = "● 未连接";
            bar.Controls.Add(title);
            bar.Controls.Add(_hdrMeta);
            bar.Controls.Add(_hdrStatus);
            bar.Resize += (s, e) =>
            {
                _hdrStatus.Location = new Point(Math.Max(600, bar.Width - _hdrStatus.Width - 20), 16);
            };
            return bar;
        }

        Control BuildBody()
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(12, 10, 12, 12),
                BackColor = Canvas
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            body.Controls.Add(BuildLeftCard(), 0, 0);
            body.Controls.Add(BuildCenterCard(), 1, 0);
            body.Controls.Add(BuildRightCard(), 2, 0);
            return body;
        }

        Control BuildLeftCard()
        {
            var card = MakeCard("校准参数");
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoSize = true,
                Padding = new Padding(12, 8, 12, 8)
            };
            stack.Controls.Add(FieldLabel("电缆长度 / m"));
            stack.Controls.Add(Pad(_numLength));
            stack.Controls.Add(FieldLabel("波速 / m/μs"));
            stack.Controls.Add(Pad(_numVelocity));
            stack.Controls.Add(FieldLabel("阻抗带宽"));
            stack.Controls.Add(Pad(_cboImpedance));
            stack.Controls.Add(FieldLabel("脉冲平均 N"));
            stack.Controls.Add(Pad(_numPulseAvg));
            stack.Controls.Add(FieldLabel("系数 pC/V"));
            stack.Controls.Add(Pad(_numPdCal));
            stack.Controls.Add(FieldLabel("急停"));
            StyleStatus(_estopStatus, "急停待确认", Muted);
            stack.Controls.Add(Pad(_estopStatus));
            stack.Controls.Add(FieldLabel("设备"));
            StyleStatus(_boxStatus, "未连接", Danger);
            stack.Controls.Add(Pad(_boxStatus));

            var connRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            _btnConnect.Width = 110; _btnDisconnect.Width = 90;
            connRow.Controls.Add(_btnConnect);
            connRow.Controls.Add(_btnDisconnect);
            stack.Controls.Add(connRow);

            var foot = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 64,
                BackColor = SectionBar,
                Padding = new Padding(12, 8, 12, 8)
            };
            var footTitle = new Label
            {
                Text = "出厂校准 · Live/Pico",
                Font = YaHei(10f, FontStyle.Bold),
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(12, 8)
            };
            StyleStatus(_pdCalStatus, "系数默认 " + PdCalibrationStore.DefaultPdCalibrationPcPerVolt.ToString("G6", CultureInfo.InvariantCulture), Muted);
            _pdCalStatus.Location = new Point(12, 34);
            _pdCalStatus.AutoSize = true;
            foot.Controls.Add(footTitle);
            foot.Controls.Add(_pdCalStatus);

            var host = new Panel { Dock = DockStyle.Fill };
            host.Controls.Add(stack);
            host.Controls.Add(foot);
            card.Controls.Add(host);
            return card;
        }

        Control BuildCenterCard()
        {
            var wrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8, 0, 8, 0),
                BackColor = Canvas
            };
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 72));
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 28));

            var waveCard = MakeCard("TDR 波形");
            var waveHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            _badge.Text = "待校准";
            _badge.Font = YaHei(9f, FontStyle.Bold);
            _badge.ForeColor = Accent;
            _badge.AutoSize = true;
            _badge.Location = new Point(8, 4);
            waveHost.Controls.Add(_plot);
            waveHost.Controls.Add(_badge);
            _badge.BringToFront();
            waveCard.Controls.Add(waveHost);
            wrap.Controls.Add(waveCard, 0, 0);

            var qCard = MakeCard("信号质量 · 日志");
            var qHost = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(8) };
            qHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            qHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            StyleStatus(_qualityMetrics, "信噪比 —   峰值 —   噪声 —", Muted);
            _qualityMetrics.Dock = DockStyle.Fill;
            qHost.Controls.Add(_qualityMetrics, 0, 0);
            qHost.Controls.Add(_log, 0, 1);
            qCard.Controls.Add(qHost);
            wrap.Controls.Add(qCard, 0, 1);
            return wrap;
        }

        Control BuildRightCard()
        {
            var card = MakeCard("校准档位");
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10)
            };
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));

            var hint = new Label
            {
                Text = "注入放电量（与校准器拨档一致）",
                Font = YaHei(9f),
                ForeColor = Muted,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            stack.Controls.Add(hint, 0, 0);

            _lstLevels.Dock = DockStyle.Fill;
            stack.Controls.Add(_lstLevels, 0, 1);

            _btnSave.Dock = DockStyle.Fill;
            stack.Controls.Add(_btnSave, 0, 2);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            _btnStart.Dock = DockStyle.Fill;
            _btnCancel.Dock = DockStyle.Fill;
            actions.Controls.Add(_btnStart, 0, 0);
            actions.Controls.Add(_btnCancel, 0, 1);
            stack.Controls.Add(actions, 0, 3);

            card.Controls.Add(stack);
            return card;
        }

        static Panel MakeCard(string title)
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Padding = new Padding(1),
                Margin = new Padding(4)
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            var bar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = SectionBar
            };
            bar.Controls.Add(new Label
            {
                Text = title,
                Font = YaHei(10.5f, FontStyle.Bold),
                ForeColor = Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            });
            card.Controls.Add(bar);
            return card;
        }

        static Label FieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Font = YaHei(9.5f),
                ForeColor = Ink,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 2)
            };
        }

        static Control Pad(Control c)
        {
            c.Width = 210;
            c.Margin = new Padding(0, 0, 0, 2);
            if (c is NumericUpDown || c is ComboBox) c.Height = 28;
            return c;
        }

        static void StyleStatus(Label lbl, string text, Color color)
        {
            lbl.Text = text;
            lbl.ForeColor = color;
            lbl.Font = YaHei(9.5f);
            lbl.AutoSize = true;
        }

        static Font YaHei(float pt, FontStyle style = FontStyle.Regular)
        {
            return new Font("Microsoft YaHei UI", pt, style);
        }

        static NumericUpDown MakeNum(decimal min, decimal max, decimal value, int decimals, decimal increment)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                DecimalPlaces = decimals,
                Increment = increment,
                Font = YaHei(9.5f),
                Width = 210
            };
        }

        static Button MakePrimaryBtn(string text)
        {
            var b = new Button
            {
                Text = text,
                Font = YaHei(11f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Accent,
                FlatStyle = FlatStyle.Flat,
                Height = 48,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            return b;
        }

        static Button MakeAccentBtn(string text)
        {
            var b = MakePrimaryBtn(text);
            b.Font = YaHei(9.5f, FontStyle.Bold);
            b.Height = 34;
            return b;
        }

        static Button MakeGhostBtn(string text)
        {
            var b = new Button
            {
                Text = text,
                Font = YaHei(9.5f),
                ForeColor = Ink,
                BackColor = SectionBar,
                FlatStyle = FlatStyle.Flat,
                Height = 34,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Border;
            return b;
        }

        void InitLevelList()
        {
            _lstLevels.Items.Clear();
            int select = 0;
            for (int i = 0; i < PdCalibrationStore.CommonInjectedChargesPc.Length; i++)
            {
                double pc = PdCalibrationStore.CommonInjectedChargesPc[i];
                _lstLevels.Items.Add(FormatCharge(pc));
                if (Math.Abs(pc - 5000.0) < 1e-9) select = i;
            }
            if (_lstLevels.Items.Count > 0)
                _lstLevels.SelectedIndex = select;
        }

        void UpdateStartCaption()
        {
            double inj = InjectedChargePcFromUi();
            _btnStart.Text = "开始校准\r\n当前量程: " + FormatCharge(inj);
        }

        static string FormatCharge(double pc)
        {
            if (pc >= 1000)
                return (pc / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " nC  ("
                    + pc.ToString("0.###", CultureInfo.InvariantCulture) + " pC)";
            return pc.ToString("0.###", CultureInfo.InvariantCulture) + " pC";
        }

        double InjectedChargePcFromUi()
        {
            int idx = _lstLevels.SelectedIndex;
            if (idx >= 0 && idx < PdCalibrationStore.CommonInjectedChargesPc.Length)
                return PdCalibrationStore.CommonInjectedChargesPc[idx];
            return 5000.0;
        }

        async Task SafeUi(Func<Task> work)
        {
            if (_busy) return;
            _busy = true;
            RefreshState();
            try { await work().ConfigureAwait(true); }
            catch (OperationCanceledException) { AppendLog("已取消。"); }
            catch (Exception ex) { AppendLog("错误: " + ex.Message); }
            finally
            {
                _busy = false;
                RefreshState();
            }
        }

        async Task ConnectAsync()
        {
            if (_api != null && _api.IsConnected) { AppendLog("已连接。"); return; }
            if (_api != null) { try { _api.Dispose(); } catch { } _api = null; }
            _api = new FactoryPressApi(new LogWriter(AppendLog));
            var cfg = new PressTestConfig();
            cfg.Mode = "live";
            cfg.RunCalibrationFirst = false;
            cfg.PdCalibrationPcPerVolt = (double)_numPdCal.Value > 0
                ? (double)_numPdCal.Value
                : PdCalibrationStore.DefaultPdCalibrationPcPerVolt;
            cfg.InjectedChargePc = InjectedChargePcFromUi();
            cfg.PulseAverageCount = Math.Max(2, (int)_numPulseAvg.Value);
            cfg.CableLengthM = Math.Max(1.0, (double)_numLength.Value);
            cfg.VelocityMPerUs = Math.Max(1.0, (double)_numVelocity.Value);
            cfg.PeriodToleranceFraction = CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction;
            cfg.ImpedanceBandwidthWide = _cboImpedance.SelectedIndex == 1;
            cfg.OutputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "hv-press-out", "calibration");
            cfg.SuppressPicoStartupWindow = true;
            try
            {
                await Task.Run(() => _api.Connect(cfg)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog("连接失败: " + ex.Message);
                try { _api.Dispose(); } catch { }
                _api = null;
                return;
            }
            TryLoadSavedPdCal(announce: true);
            AppendLog("已连接（Live）。急停按下可校准；Rapid=ChannelA；禁止 EXT 加压路径。");
        }

        async Task DisconnectAsync()
        {
            if (_api == null) return;
            try
            {
                await Task.Run(() => _api.Release()).ConfigureAwait(true);
                AppendLog("已断开并释放会话。");
            }
            catch (Exception ex) { AppendLog("断开失败: " + ex.Message); }
            try { _api.Dispose(); } catch { }
            _api = null;
        }

        async Task AcquireAsync()
        {
            if (_api == null || !_api.IsConnected)
            {
                AppendLog(StatusCopy.CalibNeedsConnected);
                return;
            }
            var snap = _api.TryGetStatus();
            if (snap != null && !snap.EStopPressed)
            {
                AppendLog(StatusCopy.CalibNeedsEStopPressed + "（当前「" + StatusCopy.EStopReleased + "」）");
                return;
            }
            double inj = InjectedChargePcFromUi();
            _api.ApplyCalibrationUiSettings(
                inj,
                Math.Max(2, (int)_numPulseAvg.Value),
                Math.Max(1.0, (double)_numLength.Value),
                Math.Max(1.0, (double)_numVelocity.Value),
                CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction);
            try { _api.SetImpedanceBandwidthWide(_cboImpedance.SelectedIndex == 1); }
            catch (Exception ex) { AppendLog("阻抗设定失败: " + ex.Message); return; }

            _badge.Text = "校准中";
            _badge.ForeColor = Accent;
            AppendLog("开始校准：注入 " + FormatCharge(inj)
                + "；ChannelA Rapid 自适量程→最佳档→1%FS+autoMs=100→50/100/400Hz±35%→N="
                + Math.Max(2, (int)_numPulseAvg.Value));
            _workCts = new CancellationTokenSource();
            int code = await Task.Run(() => _api.Calibrate(_workCts.Token), _workCts.Token).ConfigureAwait(true);
            if (code == 0)
            {
                _badge.Text = "已校准";
                _badge.ForeColor = Success;
                AppendLog("校准完成 → 「" + StatusCopy.Calibrated + "」（≠ 可加压）");
                ApplyComputedToUi();
                try { SavePdCalibrationNow(); }
                catch (Exception ex) { AppendLog("自动落盘失败: " + ex.Message); }
                try { TryShowPulse(); } catch (Exception ex) { AppendLog("波形显示失败: " + ex.Message); }
            }
            else
            {
                _badge.Text = "待校准";
                _badge.ForeColor = Accent;
                string reason = _api.AbortReason ?? "";
                AppendLog("校准失败 exit=" + code + " " + reason);
                if (!string.IsNullOrEmpty(reason)
                    && reason.IndexOf(StatusCopy.NoCalibrationWaveform, StringComparison.Ordinal) >= 0)
                {
                    _plot.ClearSeries();
                    _plot.EmptyHint = StatusCopy.NoCalibrationWaveform;
                }
            }
        }

        void ApplyComputedToUi()
        {
            if (_api == null) return;
            double factor = _api.LastComputedPdCalibrationPcPerVolt;
            double peak = _api.LastCalibrationPeakVolts;
            double inj = _api.LastInjectedChargePc;
            if (!(factor > 0) || double.IsNaN(factor) || double.IsInfinity(factor)) return;
            try { _numPdCal.Value = (decimal)Math.Min((double)_numPdCal.Maximum, Math.Max((double)_numPdCal.Minimum, factor)); } catch { }
            _pdCalStatus.Text = "峰=" + peak.ToString("G6", CultureInfo.InvariantCulture)
                + " V → " + factor.ToString("G9", CultureInfo.InvariantCulture) + " pC/V";
            _pdCalStatus.ForeColor = Success;
            _qualityMetrics.Text = "信噪比 —   峰值 "
                + peak.ToString("0.###", CultureInfo.InvariantCulture) + " V   注入 "
                + FormatCharge(inj);
            _qualityMetrics.ForeColor = Ink;
            AppendLog("系数=注入/峰 → " + factor.ToString("G9", CultureInfo.InvariantCulture) + " pC/V");
        }

        void SavePdCalibrationNow()
        {
            double factor = (double)_numPdCal.Value;
            if (!(factor > 0) || double.IsNaN(factor))
                throw new InvalidOperationException("校准系数为空：请先「开始校准」自动计算，或手工填入 pC/V。");
            double inj = InjectedChargePcFromUi();
            double peak = _api != null ? _api.LastCalibrationPeakVolts : double.NaN;
            if (!(peak > 0) && factor > 0 && inj > 0) peak = inj / factor;
            var rec = new PdCalibrationRecord
            {
                InjectedChargePc = inj,
                PeakVolts = peak,
                PdCalibrationPcPerVolt = factor,
                Notes = "FactoryCalibrationForm/approvedUI; " + PdCalibrationStore.FormulaId
                    + "; ChannelA Rapid; filter=HVX_PD_BandpassFilter handoff"
            };
            string path = PdCalibrationStore.Save(rec);
            if (_api != null)
            {
                try { _api.ApplyPdCalibrationPcPerVolt(factor, inj); }
                catch (Exception ex) { AppendLog("已保存但写回会话失败: " + ex.Message); }
            }
            AppendLog("校准系数已保存 → " + path);
            _pdCalStatus.Text = "已落盘 " + Path.GetFileName(path);
            _pdCalStatus.ForeColor = Success;
        }

        void TryLoadSavedPdCal(bool announce)
        {
            PdCalibrationRecord rec;
            string pathUsed;
            if (!PdCalibrationStore.TryLoad(out rec, out pathUsed) || !(rec.PdCalibrationPcPerVolt > 0))
            {
                if (announce)
                    AppendLog("无已保存校准系数，默认 "
                        + PdCalibrationStore.DefaultPdCalibrationPcPerVolt.ToString("G6", CultureInfo.InvariantCulture) + " pC/V");
                return;
            }
            try
            {
                _numPdCal.Value = (decimal)Math.Min((double)_numPdCal.Maximum,
                    Math.Max((double)_numPdCal.Minimum, rec.PdCalibrationPcPerVolt));
            }
            catch { }
            if (rec.InjectedChargePc > 0)
            {
                for (int i = 0; i < PdCalibrationStore.CommonInjectedChargesPc.Length; i++)
                {
                    if (Math.Abs(PdCalibrationStore.CommonInjectedChargesPc[i] - rec.InjectedChargePc) < 1e-6)
                    {
                        _lstLevels.SelectedIndex = i;
                        break;
                    }
                }
            }
            _pdCalStatus.Text = "已加载 " + rec.PdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture) + " pC/V";
            _pdCalStatus.ForeColor = Success;
            if (_api != null)
            {
                try { _api.ApplyPdCalibrationPcPerVolt(rec.PdCalibrationPcPerVolt, rec.InjectedChargePc); } catch { }
            }
            if (announce) AppendLog("已加载校准系数 ← " + pathUsed);
            UpdateStartCaption();
        }

        void TryShowPulse()
        {
            string run = _api != null ? _api.RunDirectory : null;
            if (string.IsNullOrWhiteSpace(run) || !Directory.Exists(run)) return;
            string waveDir = Path.Combine(run, "waveforms");
            if (!Directory.Exists(waveDir)) return;
            string[] pico = Directory.GetFiles(waveDir, "*.pico.preview.csv");
            if (pico.Length == 0)
            {
                _plot.ClearSeries();
                _plot.EmptyHint = StatusCopy.NoCalibrationWaveform;
                return;
            }
            Array.Sort(pico, (a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
            string path = pico[pico.Length - 1];
            List<PointF> pts = LoadPreviewCsv(path);
            if (pts == null || pts.Count < 2)
            {
                _plot.ClearSeries();
                _plot.EmptyHint = StatusCopy.NoCalibrationWaveform;
                return;
            }
            // 显示路径可选：用移交包高通后再画（不改采集/量程）
            try
            {
                if (pts.Count >= 8)
                {
                    double dtUs = Math.Max(1e-9, (pts[pts.Count - 1].X - pts[0].X) / Math.Max(1, pts.Count - 1));
                    double fs = 1e6 / dtUs;
                    if (fs >= 20000)
                    {
                        double[] y = new double[pts.Count];
                        for (int i = 0; i < pts.Count; i++) y[i] = pts[i].Y;
                        double[] filt = FactoryPdHighPass.Apply(y, fs, FactoryPdHighPass.DefaultCutoffHz);
                        for (int i = 0; i < pts.Count; i++)
                            pts[i] = new PointF(pts[i].X, (float)filt[i]);
                        _plot.Subtitle = "HVX 移交包 HP@" + FactoryPdHighPass.DefaultCutoffHz.ToString("0", CultureInfo.InvariantCulture)
                            + "Hz · " + Path.GetFileName(path);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("显示高通跳过: " + ex.Message);
                _plot.Subtitle = Path.GetFileName(path);
            }
            _plot.SetSeries(pts, autoScale: true);
            AppendLog("已显示校准脉冲 ← " + Path.GetFileName(path) + "（" + pts.Count + " 点）");
        }

        static List<PointF> LoadPreviewCsv(string path)
        {
            var list = new List<PointF>();
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)
                    || line.StartsWith("index", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("sample", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("t,", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("i,", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = line.Split(',');
                if (parts.Length < 2) continue;
                double x, y;
                if (parts.Length >= 3
                    && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                    list.Add(new PointF((float)x, (float)y));
                else if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                    list.Add(new PointF((float)x, (float)y));
            }
            if (list.Count >= 2)
            {
                float x0 = list[0].X, x1 = list[list.Count - 1].X;
                if (x1 - x0 > 10 && Math.Abs(list[1].X - list[0].X - 1f) < 0.01f)
                {
                    const float dtUs = 0.008f;
                    for (int i = 0; i < list.Count; i++)
                        list[i] = new PointF(list[i].X * dtUs, list[i].Y);
                }
            }
            return list;
        }

        void RefreshState()
        {
            bool connected = _api != null && _api.IsConnected;
            bool estopPressed = false;
            bool estopKnown = false;
            if (connected)
            {
                try
                {
                    var snap = _api.TryGetStatus();
                    if (snap != null)
                    {
                        estopKnown = snap.EStopRawDiValid;
                        estopPressed = snap.EStopPressed;
                    }
                }
                catch { }
            }

            _hdrMeta.Text = "出厂校准采集 / 电缆长度 "
                + ((double)_numLength.Value).ToString("0.0", CultureInfo.InvariantCulture) + " m"
                + "  ·  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            if (!connected)
            {
                _hdrStatus.Text = "● 未连接";
                _hdrStatus.ForeColor = Color.FromArgb(255, 200, 200);
                _boxStatus.Text = "未连接";
                _boxStatus.ForeColor = Danger;
                _estopStatus.Text = "急停待确认";
                _estopStatus.ForeColor = Muted;
            }
            else
            {
                _hdrStatus.Text = "● 设备已连接";
                _hdrStatus.ForeColor = Success;
                _boxStatus.Text = "已连接 · Live";
                _boxStatus.ForeColor = Success;
                if (!estopKnown)
                {
                    _estopStatus.Text = "设备输入待确认";
                    _estopStatus.ForeColor = Color.DarkOrange;
                }
                else if (estopPressed)
                {
                    _estopStatus.Text = StatusCopy.EStopPressed + " · 可校准";
                    _estopStatus.ForeColor = Danger;
                }
                else
                {
                    _estopStatus.Text = StatusCopy.EStopReleased + " · 禁校准";
                    _estopStatus.ForeColor = Success;
                }
            }

            _btnConnect.Enabled = !_busy && !connected;
            _btnDisconnect.Enabled = !_busy && connected;
            _btnStart.Enabled = !_busy && connected && estopPressed;
            _btnCancel.Enabled = _busy;
            _btnSave.Enabled = !_busy;
            _cboImpedance.Enabled = !_busy && connected;
            _lstLevels.Enabled = !_busy;
            _numPulseAvg.Enabled = !_busy;
            _numLength.Enabled = !_busy;
            _numVelocity.Enabled = !_busy;
            _numPdCal.Enabled = !_busy;
        }

        void AppendLog(string line)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), line);
                return;
            }
            string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            _log.AppendText("[" + stamp + "] " + line + "\r\n");
        }

        sealed class LogWriter : TextWriter
        {
            readonly Action<string> _sink;
            public LogWriter(Action<string> sink) { _sink = sink; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void WriteLine(string value)
            {
                if (_sink != null) _sink(value ?? "");
            }
            public override void Write(char value) { }
        }
    }
}
