using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryManualForm : Form
    {
        private readonly FactorySettings settings = new FactorySettings { TargetKv = 5, MaximumKv = 5, MaximumFeedbackKv = 5.1, DividerOnlyFeedback = true };
        private readonly Label rampStatistics = new Label { Dock = DockStyle.Bottom, Height = 44, AutoEllipsis = true, Text = "HV UP：未执行", ForeColor = Color.Teal };
        private readonly FactoryFeedbackTrendControl feedbackTrend = new FactoryFeedbackTrendControl();
        private readonly FactoryShutdownDiagnosticControl shutdownDiagnostic = new FactoryShutdownDiagnosticControl();
        private readonly TabControl plots = new TabControl { Dock = DockStyle.Fill };
        private readonly ComboBox mode = Choice("仿真逐项手动","实机逐项手动"), art = Choice(), pico = Choice();
        private readonly NumericUpDown target = new NumericUpDown { Minimum = 0, Maximum = 5, Value = 5, DecimalPlaces = 3, Increment = .1M, Width = 180, Font = new Font("Microsoft YaHei UI",28,FontStyle.Bold) };
        private readonly CheckBox wiring = new CheckBox { Text = "已核对反馈、比例及现场保护；人员已撤离", AutoSize = true };
        private readonly Button connect = Make("初始化两卡",150), scan = Make("读取两卡",120), parameters = Make("参数",95), stop = Make("停止 / 断开",145), folder = Make("打开记录",115);
        private readonly Button on = Make("1 · HV ON",175), lampOn = Make("指示灯开",115), lampOff = Make("指示灯关",115), up = Make("2 · HV UP",175), off = Make("3 · 布置采集 / HV OFF",230), trigger = Make("4 · 触发振荡并采集",225);
        private readonly Label emergency = new Label { Text = "P0.4 未读取", TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Gainsboro, Dock = DockStyle.Fill },
            telemetry = new Label { Text = "VMON / IMON / 分压器等待初始化", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI",14,FontStyle.Bold) },
            state = new Label { Text = "默认仿真。每一步须单独点击；没有自动关源/发光串联。", Dock = DockStyle.Fill, AutoEllipsis = true };
        private readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
        private readonly FactoryWaveformControl combined = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "振荡电压 / 检波局放 · 80kS/s", VoltageReferenceKv = 5 },
            raw = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "局放原始数据 · 整段" };
        private CancellationTokenSource cancel;
        private BlockingCollection<Action<FactoryManualEngine>> commands;
        private Task owner;
        private bool connected, busy, closePending, smoke, hasDisplayedCapture;
        private volatile bool cleanupFailed;
        private string activeStage = "INIT";
        private string records = "";
        private readonly Dictionary<string,Button> stages = new Dictionary<string,Button>();

        internal FactoryManualForm(bool test = false)
        {
            smoke = test; settings.Input.AutoRange = false;
            Text = "雄新电力科技 · R21 逐项手动验证（≤5 kV）";
            Font = new Font("Microsoft YaHei UI",10); Size = new Size(1450,1000); MinimumSize = new Size(1250,860);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(239,244,247);
            using (var s = GetType().Assembly.GetManifestResourceStream("Factory.Brand.ico")) Icon = new Icon(s);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12) };
            foreach (int h in new[] { 50,70,50,65,65,38 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,120)); Controls.Add(root);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,230));
            heading.Controls.Add(new Label { Text = "逐项手动加压 / 振荡与局放采集", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily,22,FontStyle.Bold) });
            using (var s = GetType().Assembly.GetManifestResourceStream("Factory.Brand.Logo")) heading.Controls.Add(new PictureBox { Image = new Bitmap(s), Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom },1,0);
            root.Controls.Add(heading,0,0);
            var top = Flow(); mode.Width = 190; top.Controls.Add(mode); top.Controls.Add(target); top.Controls.Add(new Label { Text = "kV", AutoSize = true, Padding = new Padding(0,20,10,0) });
            foreach (var b in new[] { connect,scan,parameters,stop,folder }) top.Controls.Add(b);
            stop.BackColor = Color.Firebrick; stop.ForeColor = Color.White; root.Controls.Add(top,0,1);
            var devices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
            devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30)); devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,23)); devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30)); devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,17));
            art.Dock = pico.Dock = DockStyle.Fill; devices.Controls.Add(art,0,0); devices.Controls.Add(pico,1,0); devices.Controls.Add(wiring,2,0); devices.Controls.Add(emergency,3,0); root.Controls.Add(devices,0,2);
            var steps = Flow(); foreach (var b in new[] { on,lampOn,lampOff,up,off,trigger }) steps.Controls.Add(b); root.Controls.Add(steps,0,3);
            stages.Add("HV_ON",on); stages.Add("LAMP_ON",lampOn); stages.Add("LAMP_OFF",lampOff); stages.Add("HV_UP",up); stages.Add("HV_OFF",off); stages.Add("TRIGGER",trigger);
            root.Controls.Add(telemetry,0,4);
            var statusPanel = new Panel { Dock = DockStyle.Fill }; statusPanel.Controls.Add(state); statusPanel.Controls.Add(rampStatistics);
            root.Controls.Add(statusPanel,0,5); root.RowStyles[5].Height = 82;
            var waves = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1200,450), SplitterDistance = 225, Panel1MinSize = 140, Panel2MinSize = 140 };
            waves.Panel1.Controls.Add(combined); waves.Panel2.Controls.Add(raw);
            var monitorPage = new TabPage("加压监测 · VMON / IMON / 分压器"); monitorPage.Controls.Add(feedbackTrend); plots.TabPages.Add(monitorPage);
            var capturePage = new TabPage("振荡与局放采集"); capturePage.Controls.Add(waves); plots.TabPages.Add(capturePage);
            var offPage = new TabPage("HV OFF前后诊断"); offPage.Controls.Add(shutdownDiagnostic); plots.TabPages.Add(offPage);
            root.Controls.Add(plots,0,6); root.Controls.Add(log,0,7);
            mode.SelectedIndex = 0; SetBusy(false);
            mode.SelectedIndexChanged += async (s,e) => { wiring.Checked = false; hasDisplayedCapture = false; combined.ClearWave(); raw.ClearWave(); feedbackTrend.ClearTrend(); telemetry.Text = "等待本模式初始化后的实时反馈"; emergency.Text = "P0.4未读取"; emergency.BackColor = Color.Gainsboro; if (mode.SelectedIndex == 1 && !smoke) await Discover(); };
            scan.Click += async (s,e) => await Discover(); parameters.Click += (s,e) => Configure();
            connect.Click += (s,e) => StartSession();
            on.Click += (s,e) => { if (mode.SelectedIndex == 1 && MessageBox.Show(this,"即将启动直流源。确认人员撤离、反馈接线及硬件保护有效？","手动 HV ON",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning) != DialogResult.OK) return;
                double volts = (double)target.Value; Queue("HV_ON",engine => engine.HvOn(volts)); };
            up.Click += (s,e) => { double volts = (double)target.Value; Queue("HV_UP",engine => engine.HvUp(volts)); };
            off.Click += (s,e) => Queue("HV_OFF",engine => engine.HvOff());
            lampOn.Click += (s,e) => Queue("LAMP_ON",engine => engine.Indicator(true)); lampOff.Click += (s,e) => Queue("LAMP_OFF",engine => engine.Indicator(false));
            trigger.Click += (s,e) => { if (mode.SelectedIndex == 1 && MessageBox.Show(this,"将发出1.5秒晶闸管光触发。确认现场隔离、HV已手动关闭，人员仍在安全区域？","手动振荡触发",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning) != DialogResult.OK) return;
                Queue("TRIGGER",engine => { engine.TriggerCapture(); SaveAndDisplay(engine.Result); }); };
            stop.Click += (s,e) => { if (cancel != null) { state.Text = "停止请求已发送，等待关断清理；不确认设备已放电。"; cancel.Cancel(); } };
            folder.Click += (s,e) => { if (Directory.Exists(records)) Process.Start(new ProcessStartInfo(records) { UseShellExecute = true }); };
            FormClosing += (s,e) => { if (owner != null && !owner.IsCompleted) { e.Cancel = true; closePending = true; cancel.Cancel(); } };
        }
        private static ComboBox Choice(params string[] items)
        { var c = new FactoryMainForm.PrintedChoice { Width = 260 }; c.Items.AddRange(items); return c; }
        private static Button Make(string text,int width) { return new Button { Text = text, Width = width, Height = 48, FlatStyle = FlatStyle.Flat }; }
        private static FlowLayoutPanel Flow() { return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true }; }
        private void Post(Action work)
        { if (IsDisposed) return; try { if (InvokeRequired) BeginInvoke(work); else work(); } catch (InvalidOperationException) { } }
        private void SetBusy(bool value)
        {
            busy = value; foreach (var b in new[] { on,lampOn,lampOff,up,off,trigger }) b.Enabled = connected && !busy;
            connect.Enabled = scan.Enabled = parameters.Enabled = mode.Enabled = art.Enabled = pico.Enabled = wiring.Enabled = !connected && !busy;
            if (cleanupFailed) connect.Enabled = false;
            stop.Enabled = connected || busy; target.Enabled = !busy;
        }
        private void Report(string key,string text)
        {
            Post(() =>
            {
                if (key == "TELEMETRY") { telemetry.Text = text; return; }
                if (key == "FEEDBACK_TREND") { feedbackTrend.Add(text); return; }
                if (key == "INIT_PASSED") rampStatistics.Text = "HV UP：未执行";
                if (key == "RAMP_PROGRESS" || key == "RAMP_SUMMARY" || key == "TARGET_HOLD") rampStatistics.Text = text;
                if (key == "SHUTDOWN_DIAG_FILE") { shutdownDiagnostic.LoadRecord(text); plots.SelectedIndex=2; }
                if (key == "FAILED_CAPTURE_FILE")
                {
                    try
                    {
                        var c=BenchCapture.Load(text); var d=FactoryDetectedWaveforms.Build(c);
                        combined.Title="异常/提前触发诊断 · 不代表正常试验或同步通过";
                        combined.SetCombined(d.VoltageKv,d.PdEnvelopePc,d.OutputRateHz,d.OutputCount*1000.0/d.OutputRateHz);
                        raw.Title="异常原始局放 · 仅诊断";
                        raw.SetFilteredCapture(c,c.Settings.PdCalibrationPcPerV);
                        plots.SelectedIndex=1;
                    }
                    catch(Exception ex) { log.AppendText("故障波形读取失败："+ex.Message+Environment.NewLine); }
                }
                log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + " " + key + " " + text + Environment.NewLine);
                if (key.StartsWith("EMERGENCY_")) { emergency.Text = key == "EMERGENCY_RELEASED" ? "P0.4 高 · 急停解除" : "P0.4 低 · 停止锁存"; emergency.BackColor = key == "EMERGENCY_RELEASED" ? Color.FromArgb(196,232,208) : Color.FromArgb(255,210,210); }
                if (key == "HV_CONDITION" || (key.EndsWith("_PASSED") && !(key == "RELEASE_PASSED" && hasDisplayedCapture)) || key.EndsWith("_ERROR")) state.Text = text;
                if (key == "SESSION_ERROR") { emergency.Text = "会话停止 · 状态需复查"; emergency.BackColor = Color.FromArgb(255,210,210); }
                foreach (var pair in stages)
                    if (key == pair.Key + "_RUNNING" || key == pair.Key + "_PASSED" || key == pair.Key + "_ERROR" || key == pair.Key + "_SKIPPED")
                        pair.Value.BackColor = key.EndsWith("_PASSED") ? Color.FromArgb(196,232,208) : key.EndsWith("_ERROR") ? Color.FromArgb(255,210,210) : key.EndsWith("_SKIPPED") ? Color.FromArgb(255,237,153) : Color.LightBlue;
            });
        }
        private FactorySettings Snapshot()
        {
            var s = settings.Clone(); s.Simulation = mode.SelectedIndex == 0; s.MaximumKv = s.TargetKv = 5; s.FeedbackWiringConfirmed = wiring.Checked;
            s.Input.RealHardware = !s.Simulation; var chosen = art.SelectedItem as ArtDeviceIdentity;
            s.Input.ArtDevice = chosen == null ? "Dev1" : chosen.Name; s.Input.ArtSerial = chosen == null ? "" : chosen.Serial; s.Input.PicoSerial = pico.SelectedItem as string ?? "";
            s.Input.ArtTerminal = 10083; s.Input.ArtTrigger = "/" + s.Input.ArtDevice + "/PFI0"; s.Input.FallingTrigger = false; return s;
        }
        private async Task Discover()
        {
            if (connected || busy || mode.SelectedIndex == 0 || smoke) return; SetBusy(true);
            try
            {
                var found = await Program.Discover(Snapshot(),NewDirectory("SCAN"),Report,CancellationToken.None);
                art.Items.Clear(); pico.Items.Clear(); foreach (var a in found.ArtDevices) if (a.Model == "USB3135") art.Items.Add(a); foreach (var p in found.PicoSerials) pico.Items.Add(p);
                art.SelectedIndex = art.Items.Count == 1 ? 0 : -1; pico.SelectedIndex = pico.Items.Count == 1 ? 0 : -1; wiring.Checked = false;
            }
            catch (Exception ex) { Report("SCAN_ERROR",ex.ToString()); }
            finally { SetBusy(false); }
        }
        private static string NewDirectory(string prefix)
        { string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"records",prefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0,8)); Directory.CreateDirectory(path); return path; }
        private void StartSession()
        {
            if (connected || busy || (smoke && mode.SelectedIndex != 0)) return;
            FactorySettings s; try { s = Snapshot(); s.Validate(FactoryRunKind.Pressure); } catch (Exception ex) { MessageBox.Show(this,ex.Message,"参数未通过",MessageBoxButtons.OK,MessageBoxIcon.Error); return; }
            records = NewDirectory(s.Simulation ? "MANUAL_SIM" : "MANUAL_HW"); cancel = new CancellationTokenSource(); commands = new BlockingCollection<Action<FactoryManualEngine>>(1); SetBusy(true);
            hasDisplayedCapture = false; telemetry.Text = "等待本次实时反馈"; emergency.Text = "P0.4等待本次读取"; emergency.BackColor = Color.Gainsboro; combined.ClearWave(); raw.ClearWave();
            shutdownDiagnostic.ClearRecord(); feedbackTrend.ClearTrend(); feedbackTrend.SetLive(true); plots.SelectedIndex = 0;
            foreach (var b in stages.Values) b.BackColor = SystemColors.Control; lampOff.BackColor = SystemColors.Control;
            owner = Task.Factory.StartNew(() => OwnerLoop(s),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            owner.ContinueWith(done => { var observed = done.Exception; Post(() => { feedbackTrend.SetLive(false); connected = false; SetBusy(false); telemetry.Text = "末次反馈（已停止，非实时） | " + telemetry.Text; emergency.Text += "\n监测已停止"; if (closePending) Close(); }); },TaskScheduler.Default);
        }
        private void OwnerLoop(FactorySettings s)
        {
            FactoryManualEngine engine = null; StreamWriter writer = null; activeStage = "INIT";
            IDisposable deviceLease=null;
            try
            {
                if(!s.Simulation)deviceLease=FactoryDeviceLease.Acquire();
                writer = new StreamWriter(Path.Combine(records,"progress.log"),false,new UTF8Encoding(false));
                {
                    FactoryAiRecordingBackend recorded = null;
                    writer.AutoFlush = true; Action<string,string> report = (key,text) => { if(recorded!=null) recorded.ObserveStage(key); writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + key + " " + text); Report(key,text); };
                    var clock = new FactoryClock(); var native = s.Simulation ? null : new NativeFactoryBackend();
                    IFactoryBackend backend = s.Simulation ? (IFactoryBackend)new SimulationFactoryBackend(clock) : native;
                    recorded = new FactoryAiRecordingBackend(backend,records,s,clock,report);
                    engine = new FactoryManualEngine(recorded,high => { if (native != null) native.SetPressureIndicator(high); else report("SIM_LAMP", "仿真指示灯=" + high); },clock,report,cancel.Token);
                    engine.Initialize(s); Post(() => { connected = true; SetBusy(false); });
                    while (!cancel.IsCancellationRequested)
                    {
                        Action<FactoryManualEngine> command;
                        if (commands.TryTake(out command,10))
                        {
                            command(engine);
                            // This is a one-round manual bench. After a complete
                            // read, clean up without feeding post-analysis backlog
                            // into a new pressure command or automatic retry.
                            if (engine.Result.Capture != null) break;
                            Post(() => SetBusy(false));
                        }
                        engine.Tick();
                    }
                }
            }
            catch (OperationCanceledException) { Report("STOP_REQUESTED","停止当前手动会话，无自动重启。"); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(records,"error.txt"),ex.ToString()); Report(activeStage + "_ERROR",ex.ToString()); Report("SESSION_ERROR","手动会话停止，等待关断清理；未确认放电。" ); }
            finally
            {
                try { if (engine != null) engine.Dispose(); Report("RELEASE_PASSED","本工具关断及释放调用完成；不证明设备已放电。" ); }
                catch (Exception ex) { cleanupFailed = true; File.AppendAllText(Path.Combine(records,"error.txt"),"\nCLEANUP " + ex); Report("RELEASE_ERROR",ex + "\n资源/输出清理未确认，禁止在本进程重新初始化；请先独立确认设备安全。" ); }
                // Preserve startup/ramp diagnostics even when the manual run failed
                // before capture. Do this only AFTER all output cleanup attempts.
                try { if (engine != null && engine.Result != null && !File.Exists(Path.Combine(records,"partial-result.xml")))
                    { Program.SaveResult(engine.Result,records,true); if(engine.Result.Capture!=null && !hasDisplayedCapture) Report("FAILED_CAPTURE_FILE",records); } }
                catch (Exception ex) { Report("RECORD_ERROR","手动失败记录保存失败：" + ex.Message); }
                try { if(writer!=null) writer.Dispose(); } catch(Exception ex) { Report("RECORD_ERROR","阶段日志关闭失败："+ex.Message); }
                FactoryDeviceLease.ReleaseAfterCleanup(deviceLease,cleanupFailed,detail=>
                {
                    File.AppendAllText(Path.Combine(records,"error.txt"),"\nDEVICE_LEASE_RETAINED "+detail);
                    Report("DEVICE_LEASE_RETAINED",detail);
                });
                GC.KeepAlive(deviceLease);
            }
        }
        private void Queue(string stage,Action<FactoryManualEngine> command)
        {
            if (!connected || busy || cancel == null || cancel.IsCancellationRequested) return; SetBusy(true);
            commands.Add(engine => { activeStage = stage; try { command(engine); } catch (Exception ex) { Report(stage + "_ERROR",ex.Message); throw; } });
        }
        private void SaveAndDisplay(FactoryResult r)
        {
            r.DirectoryPath = records; Program.SaveResult(r,records,true);
            Report("RECORD_PASSED","原始数据已存为手动验证记录，不标为完整安全试验：" + records);
            Post(() => Display(r));
        }
        private void Display(FactoryResult r)
        {
            hasDisplayedCapture = true;
            var d = r.Metrics.Detected; combined.SetCombined(d.VoltageKv,d.PdEnvelopePc,d.OutputRateHz,d.OutputCount * 1000.0 / d.OutputRateHz);
            raw.SetFilteredCapture(r.Capture,r.Capture.Settings.PdCalibrationPcPerV);
            state.Text = (r.Settings.Simulation ? "仿真 · " : "实测 · ") + "振荡峰值 " + r.Metrics.OscillationPeakKv.ToString("F3") + " kV；最大输入等效局放 " + r.Metrics.MaxPdPc.ToString("F1") + " pC。整段已显示/保存，未确认放电。";
        }
        private void Configure()
        {
            using (var dialog = new Form { Text = "手动验证参数 · 修改后重新初始化", Icon = Icon, Font = Font, Size = new Size(650,680), StartPosition = FormStartPosition.CenterParent })
            {
                var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(12) }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,48)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,52));
                var fields = new List<NumericUpDown>(); Action<string,double,decimal,decimal,int> add = (name,value,min,max,places) => { int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute,38)); var number = new NumericUpDown { Minimum = min, Maximum = max, Value = (decimal)value, DecimalPlaces = places, Dock = DockStyle.Fill }; fields.Add(number); table.Controls.Add(new Label { Text = name, AutoSize = true },0,row); table.Controls.Add(number,1,row); };
                add("分压比 V/V",settings.Input.DividerVPerV,1,1000000,3); add("加压比 V/V",settings.PressureVPerV,1,1000000,3); add("局放校准系数 pC/V",settings.Input.PdCalibrationPcPerV,.001M,10000000,3);
                add("VMON满量程 kV / 5 V",settings.VmonFullScaleKv,5,1000,3); add("IMON满量程 mA / 5 V",settings.ImonFullScaleMa,.001M,1000,3); add("限流 mA",settings.CurrentLimitMa,.001M,1000,3); add("残压阈值 V",settings.SafeResidualV,1,1000,1);
                add("AO步长 V",settings.Input.AoStepV,.0001M,.1M,4); add("循环请求间隔 ms",settings.Input.LoopDelayMs,1,1000,0); add("Pico EXT 阈值 V",settings.Input.ExternalTriggerVolts,.01M,4.9M,2); add("采集长度 ms",settings.Input.CaptureDurationMs,1,1000,3);
                add("连续加压时限 s",settings.RampTimeoutSeconds,.05M,600,2);
                add("布置开始至关断延时 ms",Math.Max(500,settings.Input.ArmDelayMs),500,5000,0);
                add("达压复核连续轮数",settings.TargetHoldChecks,0,50,0);
                add("达压复核间隔 ms",settings.TargetHoldIntervalMs,1,100,0);
                add("达压复核限时 ms",settings.TargetHoldTimeoutMs,50,2000,0);
                add("AI反馈窗口 ms（中位数）",settings.FeedbackWindowMs,3,40,0);
                int rangeRow = table.RowCount++; var range = Choice(); for (int i = 0; i < BenchSettings.Ranges.Length; i++) range.Items.Add("±" + BenchSettings.Ranges[i] + " V"); range.SelectedIndex = settings.Input.RangeIndex; range.Dock = DockStyle.Fill; table.RowStyles.Add(new RowStyle(SizeType.Absolute,38)); table.Controls.Add(new Label { Text = "Pico A 量程", AutoSize = true },0,rangeRow); table.Controls.Add(range,1,rangeRow);
                int noteRow = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute,92)); var note = new Label { Text = "固定Pico A DC / 125MS/s / 14bit；ART AI4 RSE / 80kS/s。\nP0.0高ON、P0.3指示灯、P0.4高许可、P0.7光1.5秒；P0.2始终低过流保护。\n独立急停10 ms请求，Windows非硬实时。0 kV只读请使用自动版0 kV入口。", Dock = DockStyle.Fill }; table.Controls.Add(note,0,noteRow); table.SetColumnSpan(note,2);
                var save = Make("保存参数",120); int saveRow = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.Absolute,50)); table.Controls.Add(save,1,saveRow);
                save.Click += (sender,args) => settings.FeedbackWindowMs = (int)fields[16].Value;
                save.Click += (sender,args) => { settings.Input.DividerVPerV = (double)fields[0].Value; settings.PressureVPerV = (double)fields[1].Value; settings.Input.PdCalibrationPcPerV = (double)fields[2].Value; settings.VmonFullScaleKv = (double)fields[3].Value; settings.ImonFullScaleMa = (double)fields[4].Value; settings.CurrentLimitMa = (double)fields[5].Value; settings.SafeResidualV = (double)fields[6].Value; settings.Input.AoStepV = (double)fields[7].Value; settings.Input.LoopDelayMs = (int)fields[8].Value; settings.Input.ExternalTriggerVolts = (double)fields[9].Value; settings.Input.CaptureDurationMs = (double)fields[10].Value; settings.RampTimeoutSeconds = (double)fields[11].Value; settings.Input.ArmDelayMs = (int)fields[12].Value; settings.TargetHoldChecks=(int)fields[13].Value; settings.TargetHoldIntervalMs=(int)fields[14].Value; settings.TargetHoldTimeoutMs=(int)fields[15].Value; settings.Input.RangeIndex = range.SelectedIndex; wiring.Checked = false; dialog.Close(); };
                dialog.Controls.Add(table);
                if (smoke) dialog.Shown += (sender,args) => dialog.BeginInvoke(new Action(() =>
                {
                    if (fields.Count != 17 || fields[0].Value != (decimal)settings.Input.DividerVPerV || fields[12].Minimum != 500 || fields[13].Value != 0 || fields[13].Minimum != 0 || fields[16].Value != settings.FeedbackWindowMs || range.Items.Count != BenchSettings.Ranges.Length)
                        throw new Exception("Manual parameter controls lost values or ranges.");
                    fields[7].Value=.005M; fields[8].Value=7; fields[12].Value=750; fields[16].Value=20;
                    save.PerformClick();
                }));
                dialog.ShowDialog(this);
            }
        }
        internal async Task Smoke(string directory)
        {
            Directory.CreateDirectory(directory); settings.Input.RequestedPicoRateHz = 1000000;
            parameters.PerformClick();
            if(settings.Input.AoStepV!=.005 || settings.Input.LoopDelayMs!=7 || settings.Input.ArmDelayMs!=750 || settings.FeedbackWindowMs!=20) throw new Exception("Manual step/interval/settle/window controls did not apply.");
            connect.PerformClick(); await WaitUi(() => connected && !busy,"initialization");
            on.PerformClick(); await WaitUi(() => !busy,"HV ON");
            if (log.Text.Contains("HV_UP_PASSED") || log.Text.Contains("TRIGGER_PASSED")) throw new Exception("ON automatically advanced a manual stage.");
            lampOn.PerformClick(); await WaitUi(() => !busy,"indicator ON");
            up.PerformClick(); await WaitUi(() => !busy,"HV UP");
            if (settings.RampTimeoutSeconds != 10 || settings.MaximumFeedbackKv != 5.1 || settings.TargetHoldChecks!=0 || !log.Text.Contains("TARGET_HOLD") || !log.Text.Contains("取消多轮复核"))
                throw new Exception("Manual R18 first-reach contract missing.");
            if (log.Text.Contains("HV_OFF_PASSED") || log.Text.Contains("TRIGGER_PASSED")) throw new Exception("UP automatically turned off or triggered.");
            off.PerformClick(); await WaitUi(() => !busy,"HV OFF");
            lampOff.PerformClick(); await WaitUi(() => !busy,"indicator OFF");
            trigger.PerformClick(); await WaitUi(() => owner.IsCompleted && !connected,"capture and cleanup");
            if (!log.Text.Contains("TRIGGER_PASSED") || !File.Exists(Path.Combine(records,"capture.bin")) ||
                !File.Exists(Path.Combine(records,"partial-result.xml")) || File.Exists(Path.Combine(records,"result.xml")))
                throw new Exception("Manual UI did not preserve a complete raw but explicitly partial safety record.");
            using (var bitmap = new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height)); bitmap.Save(Path.Combine(directory,"manual-5kv.png")); }
            if (!settings.DividerOnlyFeedback || feedbackTrend.SampleCount < 2) throw new Exception("Manual three-channel trend missing.");
            plots.SelectedIndex = 1; await Task.Delay(30); using (var bitmap = new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height)); bitmap.Save(Path.Combine(directory,"manual-capture.png")); }
            string captureDirectory = records;
            connect.PerformClick(); await WaitUi(() => connected && !busy,"second initialization");
            on.PerformClick(); await WaitUi(() => !busy,"second HV ON");
            stop.PerformClick(); await WaitUi(() => owner.IsCompleted && !connected,"Stop while HV ON");
            if (File.Exists(Path.Combine(records,"capture.bin")) || !File.Exists(Path.Combine(records,"partial-result.xml")))
                throw new Exception("Stopped manual session must preserve partial diagnostics, without manufacturing capture.");
            File.WriteAllText(Path.Combine(directory,"manual-ui-test.txt"),"MANUAL_UI=PASS; MODE=SIM; hardware=false; HV_ON/HV_UP/AO_ZERO/HV_OFF/OPTICAL=explicit; automatic-next-step=false; STOP_WHILE_HV_ON=PASS; raw=" + captureDirectory);
        }
        private async Task WaitUi(Func<bool> ready,string description)
        { var elapsed = Stopwatch.StartNew(); while (!ready()) { if (elapsed.ElapsedMilliseconds > 15000) throw new TimeoutException("Manual UI wait failed: " + description + "\n" + log.Text); await Task.Delay(20); } }
    }
}
