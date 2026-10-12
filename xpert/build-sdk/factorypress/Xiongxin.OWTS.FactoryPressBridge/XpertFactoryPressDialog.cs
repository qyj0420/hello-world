#if NETFRAMEWORK
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.FactoryPressBridge
{
    /// <summary>
    /// 雄新电力 · 雄新 OWTS「加压测试」— FactoryPress 硬件栈对话框。
    /// Opened by the Xpert host (reflection, <see cref="ShowForXpert"/>) from the 加压测试 page when the
    /// FactoryPress backend is enabled. Owns exactly one <see cref="XpertFactoryPressBackend"/>; the host
    /// does not configure the legacy PressureRuntime for that page while this dialog is open.
    /// </summary>
    public sealed class XpertFactoryPressDialog : Form
    {
        static readonly Color Accent = Color.FromArgb(204, 76, 76);
        static readonly Color Header = Color.FromArgb(233, 240, 246);
        static readonly Color Ok = Color.FromArgb(41, 151, 111);
        static readonly Color Danger = Color.FromArgb(207, 58, 66);

        readonly string _xpertArtSerial, _picoSerial, _roundsRoot;
        readonly double _pressureRatio, _dividerRatio, _maximumKv;
        readonly int _rangeIndex;
        readonly bool _fakeLive;
        readonly Action<string, string> _diagnostics;

        readonly Label _status = new Label();
        readonly Label _estop = new Label();
        readonly NumericUpDown _targetKv = new NumericUpDown();
        readonly NumericUpDown _rounds = new NumericUpDown();
        readonly Button _connect = new Button();
        readonly Button _start = new Button();
        readonly Button _stop = new Button();
        readonly Button _disconnect = new Button();
        readonly CheckBox _fakeEStop = new CheckBox();
        readonly TextBox _log = new TextBox();
        readonly System.Windows.Forms.Timer _refresh = new System.Windows.Forms.Timer();

        XpertFactoryPressBackend _backend;
        CancellationTokenSource _press;
        bool _busy;
        int _completedRounds;
        string _lastOutcome = string.Empty;
        string _closeResult = string.Empty;

        XpertFactoryPressDialog(string xpertArtSerial, string picoSerial, double pressureRatio, double dividerRatio,
            double maximumKv, int rangeIndex, string roundsRoot, bool fakeLive, Action<string, string> diagnostics)
        {
            _xpertArtSerial = xpertArtSerial ?? string.Empty;
            _picoSerial = picoSerial ?? string.Empty;
            _pressureRatio = pressureRatio;
            _dividerRatio = dividerRatio;
            _maximumKv = maximumKv;
            _rangeIndex = rangeIndex;
            _roundsRoot = roundsRoot;
            _fakeLive = fakeLive;
            _diagnostics = diagnostics;
            BuildUi();
        }

        /// <summary>
        /// Host entry point. Blocks (modal) until the operator closes the dialog and the session is closed.
        /// Returns a one-line summary for the host diagnostics log.
        /// </summary>
        public static string ShowForXpert(IWin32Window owner, string xpertArtSerial, string picoSerial,
            double pressureRatio, double dividerRatio, double maximumKv, int rangeIndex, string roundsRoot,
            bool fakeLive, Action<string, string> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(roundsRoot)) throw new ArgumentException("缺少项目采集目录。", "roundsRoot");
            using (var dialog = new XpertFactoryPressDialog(xpertArtSerial, picoSerial, pressureRatio, dividerRatio,
                maximumKv, rangeIndex, roundsRoot, fakeLive, diagnostics))
            {
                dialog.ShowDialog(owner);
                return dialog.Summary();
            }
        }

        string Summary()
        {
            return "rounds=" + _completedRounds.ToString(CultureInfo.InvariantCulture)
                + "; lastOutcome=" + (_lastOutcome.Length == 0 ? "-" : _lastOutcome)
                + "; close=" + (_closeResult.Length == 0 ? "released" : _closeResult)
                + "; records=" + _roundsRoot;
        }

        void BuildUi()
        {
            Text = XpertFactoryPressBackend.Brand + " — 加压测试（出厂加压硬件栈）" + (_fakeLive ? " · FakeLive 无驱动演示" : string.Empty);
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(960, 640);
            MinimumSize = new Size(820, 520);
            BackColor = Color.White;

            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Header, Padding = new Padding(18, 10, 18, 8) };
            var title = new Label
            {
                Dock = DockStyle.Top, Height = 30, ForeColor = Accent,
                Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
                Text = XpertFactoryPressBackend.Brand + " · 加压测试"
            };
            var info = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Color.FromArgb(60, 60, 60),
                Text = "Pico 5242D " + (_picoSerial.Length == 0 ? "(first unit)" : _picoSerial)
                    + " · ART DB37（P0.0 HVON+加压灯 · P0.3 加压灯 · P0.4 急停独立回路 · P0.5 连接灯）"
                    + Environment.NewLine
                    + "加压比例 " + _pressureRatio.ToString("G6", CultureInfo.InvariantCulture) + " V/V · 分压比 "
                    + _dividerRatio.ToString("G6", CultureInfo.InvariantCulture) + " · 上限 "
                    + _maximumKv.ToString("G6", CultureInfo.InvariantCulture) + " kV · 项目校准已匹配 · 原始记录 → "
                    + _roundsRoot
            };
            header.Controls.Add(info);
            header.Controls.Add(title);

            var statusRow = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(18, 8, 18, 4) };
            _estop.Dock = DockStyle.Right;
            _estop.Width = 180;
            _estop.TextAlign = ContentAlignment.MiddleCenter;
            _estop.ForeColor = Color.White;
            _estop.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            _status.Dock = DockStyle.Fill;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            statusRow.Controls.Add(_status);
            statusRow.Controls.Add(_estop);

            var controls = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, Height = 54, Padding = new Padding(14, 8, 14, 4), WrapContents = false
            };
            controls.Controls.Add(new Label { Text = "目标电压 (kV)", AutoSize = true, Margin = new Padding(4, 9, 4, 0) });
            _targetKv.DecimalPlaces = 1;
            _targetKv.Increment = 0.5M;
            _targetKv.Minimum = 0;
            _targetKv.Maximum = (decimal)Math.Max(0.1, Math.Floor(_maximumKv * 10) / 10);
            _targetKv.Value = Math.Min(_targetKv.Maximum, 1M);
            _targetKv.Width = 90;
            controls.Controls.Add(_targetKv);
            controls.Controls.Add(new Label { Text = "轮数", AutoSize = true, Margin = new Padding(14, 9, 4, 0) });
            _rounds.Minimum = 1;
            _rounds.Maximum = 20;
            _rounds.Value = 1;
            _rounds.Width = 60;
            controls.Controls.Add(_rounds);
            Style(_connect, "连接设备", Color.FromArgb(0, 143, 153));
            Style(_start, "开始加压", Accent);
            Style(_stop, "停止", Danger);
            Style(_disconnect, "断开设备", Color.FromArgb(69, 112, 190));
            _connect.Margin = new Padding(24, 2, 4, 2);
            controls.Controls.Add(_connect);
            controls.Controls.Add(_start);
            controls.Controls.Add(_stop);
            controls.Controls.Add(_disconnect);
            _fakeEStop.Text = "FakeLive 急停按下";
            _fakeEStop.AutoSize = true;
            _fakeEStop.Margin = new Padding(16, 9, 4, 0);
            _fakeEStop.Visible = _fakeLive;
            _fakeEStop.Checked = true;
            controls.Controls.Add(_fakeEStop);

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.FromArgb(250, 251, 252);
            _log.Font = new Font("Consolas", 9.5F);
            var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 6, 18, 18) };
            logHost.Controls.Add(_log);

            Controls.Add(logHost);
            Controls.Add(controls);
            Controls.Add(statusRow);
            Controls.Add(header);

            _connect.Click += (s, e) => Connect();
            _start.Click += (s, e) => StartPress();
            _stop.Click += (s, e) => StopPress();
            _disconnect.Click += (s, e) => Disconnect();
            _fakeEStop.CheckedChanged += (s, e) =>
            {
                var b = _backend;
                if (b != null && b.IsFakeLive) b.SimulateFakeLiveEStop(_fakeEStop.Checked);
                RefreshStatus();
            };
            _refresh.Interval = 500;
            _refresh.Tick += (s, e) => RefreshStatus();
            FormClosing += OnClosing;
            Shown += (s, e) =>
            {
                Append("非零加压前须：项目校准已匹配、急停解除、现场清场。急停/故障 → HV OFF + Pico 同步，不自动续压。");
                Append("本对话框原始记录（owts-factory-round.json / pico-raw-adc.i16le / divider-raw-volts.f64le）不进入 Xpert 分析/E+ 定位。");
                string gate = XpertFactoryPressBackend.Preflight();
                if (gate.Length != 0) Append(gate);
                _refresh.Start();
                UpdateButtons();
                RefreshStatus();
            };
        }

        static void Style(Button b, string text, Color color)
        {
            b.Text = text;
            b.AutoSize = false;
            b.Size = new Size(108, 36);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = color;
            b.ForeColor = Color.White;
            b.Margin = new Padding(4, 2, 4, 2);
        }

        void UpdateButtons()
        {
            bool open = _backend != null;
            bool pressing = _press != null;
            _connect.Enabled = !open && !_busy;
            _start.Enabled = open && !_busy && !pressing && !_backend.RequiresReopen;
            _stop.Enabled = pressing;
            _disconnect.Enabled = open && !_busy && !pressing;
            _targetKv.Enabled = !pressing;
            _rounds.Enabled = !pressing;
            _fakeEStop.Enabled = open && _backend.IsFakeLive;
        }

        void RefreshStatus()
        {
            var b = _backend;
            if (b == null)
            {
                _status.Text = "设备未连接（出厂加压会话未打开）";
                _estop.Text = "急停：未知";
                _estop.BackColor = Color.Gray;
                return;
            }
            try
            {
                _status.Text = b.StatusFiveStates + (b.RequiresReopen ? "  ·  安全退出后须断开并重新连接" : string.Empty);
                bool pressed = b.EStopPressed;
                _estop.Text = pressed ? "急停按下" : "急停解除";
                _estop.BackColor = pressed ? Danger : Ok;
            }
            catch (Exception ex)
            {
                _status.Text = "状态读取失败：" + ex.Message;
                _estop.Text = "急停：未知";
                _estop.BackColor = Color.Gray;
            }
        }

        void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Append), line); } catch (InvalidOperationException) { }
                return;
            }
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
        }

        void Diag(string code, string text)
        {
            var d = _diagnostics;
            if (d == null) return;
            try { d(code, text); } catch { }
        }

        async void Connect()
        {
            if (_backend != null || _busy) return;
            _busy = true;
            UpdateButtons();
            Append("连接：DeviceSdkLease 独占 → ART/Pico Open（Pico 单次 Open）→ 台架增益 Apply → 双身份校验…");
            try
            {
                var opened = await Task.Run(() => XpertFactoryPressBackend.Open(_xpertArtSerial, _picoSerial,
                    _pressureRatio, _dividerRatio, _maximumKv, _rangeIndex, _fakeLive, Append));
                if (IsDisposed) { opened.Close(); opened.Dispose(); return; }
                _backend = opened;
                _backend.MarkProjectCalibrated(true);
                if (_backend.IsFakeLive) _backend.SimulateFakeLiveEStop(_fakeEStop.Checked);
                Append("已连接；Xpert 项目校准作为「已校准」依据（≠ 可加压）。");
                Diag("PressureOwtsConnected", "FactoryPress session opened; fakeLive=" + _backend.IsFakeLive);
            }
            catch (Exception ex)
            {
                Append("连接失败：" + ex.Message);
                Diag("PressureOwtsConnectFailed", ex.ToString());
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                UpdateButtons();
                RefreshStatus();
            }
        }

        async void StartPress()
        {
            var b = _backend;
            if (b == null || _press != null || _busy) return;
            double kv = (double)_targetKv.Value;
            int count = (int)_rounds.Value;
            if (kv > 0)
            {
                string reason;
                if (!b.CanEnterNonZeroPressure(kv, out reason))
                {
                    Append("拒绝加压：" + reason);
                    MessageBox.Show(this, reason, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var answer = MessageBox.Show(this,
                    "确认现场已清场、接地已拆除、急停可随时按下。\r\n开始加压 " + kv.ToString("0.0", CultureInfo.InvariantCulture)
                    + " kV × " + count.ToString(CultureInfo.InvariantCulture) + " 轮？",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) { Append("操作员取消，未加压。"); return; }
            }
            var cts = new CancellationTokenSource();
            _press = cts;
            UpdateButtons();
            Diag("PressureOwtsStart", "targetKv=" + kv.ToString("R", CultureInfo.InvariantCulture) + "; rounds=" + count);
            try
            {
                for (int i = 1; i <= count; i++)
                {
                    string dir = Path.Combine(_roundsRoot,
                        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                    Append("—— 第 " + i + "/" + count + " 轮 目标 " + kv.ToString("0.0", CultureInfo.InvariantCulture) + " kV ——");
                    string message = string.Empty;
                    string outcome = await Task.Run(() =>
                    {
                        string m;
                        string o = b.PressRound(kv, _rangeIndex, dir, cts.Token, Append, out m);
                        message = m;
                        return o;
                    });
                    _lastOutcome = outcome;
                    Append(outcome + "：" + message + "（记录 " + dir + "）");
                    Diag("PressureOwtsRound", "outcome=" + outcome + "; dir=" + dir + "; message=" + message);
                    if (outcome == XpertFactoryPressBackend.OutcomeCompleted) _completedRounds++;
                    if (outcome != XpertFactoryPressBackend.OutcomeCompleted) break;
                    if (cts.IsCancellationRequested) break;
                }
                if (b.RequiresReopen)
                {
                    Append("安全退出：须「断开设备」后重新连接；不自动续压。" + b.SessionFaultSummary);
                    MessageBox.Show(this, "加压已安全退出（HV OFF + Pico 同步）。请检查现场，断开设备后再重新连接。",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                Append("加压异常：" + ex.Message);
                Diag("PressureOwtsFailed", ex.ToString());
            }
            finally
            {
                _press = null;
                cts.Dispose();
                UpdateButtons();
                RefreshStatus();
            }
        }

        void StopPress()
        {
            var cts = _press;
            var b = _backend;
            if (cts == null) return;
            Append("停止：OperatorAbort（关源，不永久锁存；P0.4 急停仍独立监视）。");
            try { if (b != null) b.RequestStop(); } catch (Exception ex) { Append("停止请求异常：" + ex.Message); }
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        void Disconnect()
        {
            var b = _backend;
            if (b == null || _press != null) return;
            _busy = true;
            UpdateButtons();
            try
            {
                string result = b.Close();
                _closeResult = result;
                b.Dispose();
                _backend = null;
                if (result.Length == 0)
                {
                    Append("已断开：设备租约已释放。");
                    Diag("PressureOwtsClosed", "released");
                }
                else
                {
                    Append(result);
                    Diag("PressureOwtsCloseFailed", result);
                    MessageBox.Show(this, result + "\r\n本进程禁止再次打开硬件；请确认高压已关断后退出程序。",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                _busy = false;
                UpdateButtons();
                RefreshStatus();
            }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_press != null || _busy)
            {
                e.Cancel = true;
                if (_press != null) StopPress();
                Append("本轮仍在执行：已请求停止，待关源完成后再关闭窗口。");
                return;
            }
            _refresh.Stop();
            if (_backend != null) Disconnect();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refresh.Dispose();
                var b = _backend;
                _backend = null;
                if (b != null)
                {
                    string result = b.Close();
                    if (_closeResult.Length == 0) _closeResult = result;
                    b.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
#endif
