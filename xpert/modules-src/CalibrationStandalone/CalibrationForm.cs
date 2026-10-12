using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.OWTS.Task38;
using Xiongxin.Scope;
using HVX.SignalProcessing;

namespace Xiongxin.Calibration
{
    internal sealed class CalibrationForm : Form
    {
        readonly TextBox artPath = new TextBox { Width = 360, Text = @"C:\Program Files (x86)\ART Technology\ART-DAQ\Lib\x64" };
        readonly TextBox device = new TextBox { Width = 70, Text = "Dev1" };
        readonly TextBox picoSerial = new TextBox { Width = 150 };
        readonly TextBox projectPath = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        readonly ComboBox impedance = Choice("10 kHz 低截止 · 长电缆", "3 MHz 高通 · 短电缆");
        readonly ComboBox captureMode = Choice("单段记录，提取全部有效脉冲", "触发段模式，逐段提取");
        readonly ComboBox inputRange = Choice("±0.1 V", "±0.2 V", "±0.5 V", "±1 V", "±2 V", "±5 V");
        readonly NumericUpDown length = Num(1, 50000, 180, 1, 10), velocity = Num(50, 250, 170, 2, .1m);
        readonly NumericUpDown charge = Num(1, 500000, 100, 0, 10), frequency = Num(1, 1000, 100, 0, 10);
        readonly NumericUpDown threshold = Num(.001m, 10, .015m, 3, .005m);
        readonly NumericUpDown duration = Num(40, 60, 40, 0, 10);
        readonly Label boxStatus = new Label { AutoSize = true }, picoStatus = new Label { AutoSize = true }, windowInfo = new Label { AutoSize = true };
        readonly Label geometryStatus = new Label {AutoSize=true,Text="长度与波速待确认",ForeColor=Color.Firebrick};
        readonly TextBox result = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly ScopePlot plot = new ScopePlot(), qualityPlot = new ScopePlot();
        readonly Label qualityMetrics = new Label {Dock=DockStyle.Top, Height=56,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(12)};
        readonly Button loadProject = Button("加载项目"), confirmGeometry=Button("确认长度与波速"), connectBox = Button("连接控制箱"), disconnectBox = Button("断开控制箱"), connectPico = Button("连接 Pico"), disconnectPico = Button("断开 Pico"), acquire = Button("开始校准采集"), qualityCapture = Button("采集 40ms 信号质量"), cancel = Button("停止采集");
        // UI snapshot refresh only. ART reads run on the dedicated session monitor.
        readonly System.Windows.Forms.Timer safetyTimer = new System.Windows.Forms.Timer { Interval = 200 };
        readonly object emergencySessionGate = new object();
        long emergencySessionEpoch;
        bool emergencyNotificationsDisposed;
        int emergencyNoticeQueued;
        readonly string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XIONGXIN校准验证数据");
        ControlBox box;
        volatile ICalibrationWorker client;
        bool picoConnected, capturing, operationBusy, readFailureNotified, closing;
        bool picoReleaseUnverified;
        bool applyingHostImpedance;
        bool geometryConfirmed, lengthAvailable, velocityAvailable;
        string finalPreflightPath;
        Capture noiseObservationSource;
        double[] noiseObservationSamples;
        volatile bool stopRequested;
        volatile ICalibrationWorker cancelledWorker;
        int hostEntryActive;
        volatile bool hostEntrySafetyCheckActive;
        readonly CalibrationAcquisitionFlow acquisitionFlow=new CalibrationAcquisitionFlow();
        readonly CalibrationPreflightMemory preflightMemory=new CalibrationPreflightMemory();
        internal bool LastCaptureSucceeded { get; private set; }
        internal bool HostKeepSessionOpen { get; set; }
        volatile bool hostReusableSession;
        string hostSessionConfiguration;
        string hostBoundIdentity;
        string hostArtSerial;
        internal Func<string[]> HostHardwareIdentityProvider;
        internal bool HostSessionReusable { get { return hostReusableSession && !capturing && !operationBusy &&
            !stopRequested && !picoReleaseUnverified && client != null && !client.Broken && picoConnected &&
            box != null && box.Connected && !box.CleanupPending; } }

        readonly bool integratedMode;
        internal bool HasOwnedHardwareResources { get { return client != null || picoConnected || (box != null && (box.Connected || box.CleanupPending)); } }
        internal bool HasActiveHardwareSession { get { return capturing || operationBusy || HasOwnedHardwareResources; } }
        internal bool HostRequiresConnectionRecheck { get { return picoReleaseUnverified; } }
        internal async Task HostSetShortCableWhenIdleAsync(bool value)
        {
            if(!integratedMode||capturing||operationBusy||picoReleaseUnverified||box==null||!box.Connected||box.CleanupPending)
                throw new IOException("阻抗切换未完成，请检查设备状态后重试。");
            RequireNotStopped();RequireCurrentHostHardwareIdentity();
            var owner=box;
            operationBusy=true;RefreshState();
            try {
                await owner.SetShortCableAsync(value);
                RequireNotStopped();RequireCurrentHostHardwareIdentity();
                if(!Object.ReferenceEquals(owner,box)||!owner.ShortCableVerified||owner.ShortCable!=value)
                    throw new IOException("阻抗切换状态未确认，请检查设备状态。");
                preflightMemory.Forget();
                Log("已确认阻抗手动设置；当前已有波形保留原采集配置，后续采集使用实际新配置。");
            } finally {operationBusy=false;RefreshState();}
        }
        internal event Action<double[], double, double> HostWaveformCaptured;
        internal event Action<double[], double, double> HostNoiseCaptured;
        internal event Action<double, double, double> HostSignalMetricsCaptured;
        internal IWin32Window HostDialogOwner { get; set; }
        internal Func<string,string> HostTranslateUi;
        internal int HostAverageCount { get; set; } = 16;
        internal double? HostScreeningThresholdV { get; set; }
        internal Action<double> HostAutomaticThresholdResolved;
        internal string HostAcquisitionMode { get; set; } = "Pulse";
        internal bool HostDistanceMode { get; set; }
        internal string HostPhaseCode { get; set; }
        internal string HostLevelCode { get; set; }
        internal bool HostCapacitiveLoad { get; set; }
        internal string HostGeometryDescription { get; set; }
        internal event Action<CalibrationCaptureResult> HostCaptureCompleted;
        internal event Action<Exception> HostErrorReported;
        internal Func<string,bool,DialogResult> SupplementChoiceForTests;
        internal Func<DialogResult> HostParameterConfirmationForTests;
        internal Func<Task<bool>> HostCaptureForTests;
        internal Func<IControlBoxIo> HostControlBoxForTests;
        internal Func<ICalibrationWorker> HostWorkerForTests;
        internal Func<double[],int,double,bool> HostPulseExcluded;
        internal void HostCancel() {
            ICalibrationWorker ownedWorker;
            lock(emergencySessionGate){stopRequested=true;hostReusableSession=false;ownedWorker=client;cancelledWorker=ownedWorker;}
            if(ownedWorker!=null)ownedWorker.Cancel();
        }
        bool WorkerWasCancelled { get { var owned=client;return owned!=null&&Object.ReferenceEquals(owned,cancelledWorker); } }
        void RequireUncancelledWorker() {
            if(WorkerWasCancelled)throw new InvalidOperationException("本次采集已停止。请先断开并重新连接采集设备，再开始新的校准。");
        }
        internal async Task HostReleaseAsync()
        {
            if (capturing || operationBusy) throw new InvalidOperationException("采集仍在结束，请等待后重试释放。");
            hostReusableSession=false;
            preflightMemory.Forget();
            if (client != null) await DisconnectPico();
            if (box != null && (box.Connected || box.CleanupPending)) DisconnectBox();
            if (HasOwnedHardwareResources) throw new IOException("设备释放未完成，已禁止新的校准。请点击重试释放；不要启动其他设备程序。");
            hostSessionConfiguration=null;
            hostBoundIdentity=null;
        }
        // Navigation reads only the existing emergency input. A new DI-only
        // owner is closed after this single check; Pico and outputs are untouched.
        // This result is a reminder decision, never permission for a later capture.
        internal async Task<bool> HostCheckEmergencyOnEntryAsync()
        {
            if(Interlocked.CompareExchange(ref hostEntryActive,1,0)!=0)
                throw new InvalidOperationException("请等待当前校准操作结束。");
            bool borrowed=false, connectedHere=false, wasReusable=false, entryOperationOwned=false;
            try
            {
                if(!integratedMode || capturing || operationBusy || picoReleaseUnverified)
                    throw new InvalidOperationException("当前校准会话不能读取新的急停状态。");
                RequireUncancelledWorker();
                await BindHostHardwareIdentityAsync();
                if(box!=null && box.CleanupPending)
                    throw new IOException("控制箱释放未完成，禁止新增急停读取。");
                borrowed=box!=null && box.Connected;
                wasReusable=HostSessionReusable;
                if(borrowed && !wasReusable)
                    throw new IOException("校准会话状态未确认，请先释放原会话。");
                hostEntrySafetyCheckActive=true;
                operationBusy=true;
                entryOperationOwned=true;
                bool high;
                // Both actual levels are valid external-input observations.
                {
                    RequireCurrentHostHardwareIdentity();
                    if(borrowed)
                        await box.RequireInputReadyAsync(); // Same sole monitor, post-request sample.
                    else
                    {
                        ControlBox entryBox;long epoch;
                        lock(emergencySessionGate)
                        {
                            epoch=++emergencySessionEpoch;
                            box=entryBox=new ControlBox(HostControlBoxForTests!=null ? HostControlBoxForTests() :
                                new ArtControlBoxIo(device.Text.Trim(),artPath.Text.Trim(),hostArtSerial));
                        }
                        await Task.Run(()=>entryBox.ConnectMonitored(error=>LatchEmergencyStop(entryBox,epoch),null));
                        connectedHere=true;
                    }
                    RequireCurrentHostHardwareIdentity();
                    RequireNotStopped();
                    if(!box.EmergencySignalHigh.HasValue)throw new IOException("设备输入状态尚未返回有效数据。");
                    high=box.EmergencySignalHigh.Value;
                }
                return high;
            }
            finally
            {
                try
                {
                    if(entryOperationOwned)operationBusy=false;
                    if(connectedHere)
                    {
                        var owned=box;
                        lock(emergencySessionGate){emergencySessionEpoch++;}
                        await Task.Run(()=>owned.Disconnect());
                    }
                    else if(borrowed && wasReusable && !HostSessionReusable)
                        await HostReleaseAsync();
                }
                finally
                {
                    if(entryOperationOwned)hostEntrySafetyCheckActive=false;
                    Interlocked.Exchange(ref hostEntryActive,0);
                    RefreshState();
                }
            }
        }
        string[] ReadHostHardwareIdentity()
        {
            var value=HostHardwareIdentityProvider==null?null:HostHardwareIdentityProvider();
            uint artSerialNumber;
            if(value==null||value.Length!=4||String.IsNullOrWhiteSpace(value[0])||
                !System.Text.RegularExpressions.Regex.IsMatch(value[1]??"","^[A-Za-z0-9_-]{1,63}$")||
                !UInt32.TryParse(value[2],NumberStyles.None,CultureInfo.InvariantCulture,out artSerialNumber)||artSerialNumber==0||
                artSerialNumber.ToString(CultureInfo.InvariantCulture)!=value[2]||
                !System.Text.RegularExpressions.Regex.IsMatch(value[3]??"","^[A-Z0-9]{5}/[0-9]{4}$"))
                throw new InvalidOperationException("未取得所选设备的完整身份，请返回主页连接并核验设备后再校准。");
            return (string[])value.Clone();
        }
        static string BindingKey(string[] value) { return String.Join("|",value); }
        async Task BindHostHardwareIdentityAsync()
        {
            string[] value;
            try {value=ReadHostHardwareIdentity();}
            catch {if(HasOwnedHardwareResources)await HostReleaseAsync();throw;}
            string key=BindingKey(value);
            if(hostBoundIdentity!=null && hostBoundIdentity!=key)
            {
                await HostReleaseAsync();
                throw new InvalidOperationException("所选设备已改变，原校准会话已释放。请核对设备后重新开始。");
            }
            hostBoundIdentity=key;hostArtSerial=value[2];device.Text=value[1];picoSerial.Text=value[3];
        }
        void RequireCurrentHostHardwareIdentity()
        {
            if(!integratedMode)return;
            var value=ReadHostHardwareIdentity();
            if(hostBoundIdentity==null||hostBoundIdentity!=BindingKey(value)||device.Text!=value[1]||
                hostArtSerial!=value[2]||picoSerial.Text!=value[3])
                throw new InvalidOperationException("设备身份已改变，当前校准已取消，请返回主页核对设备。");
        }
        string HostConfigurationKey()
        {
            var config=Settings();
            return String.Join("|",new[]{projectPath.Text,device.Text.Trim(),artPath.Text.Trim(),picoSerial.Text.Trim(),
                config.Resolution.ToString(CultureInfo.InvariantCulture),config.RateMS.ToString("R",CultureInfo.InvariantCulture),
                config.A.Coupling.ToString(CultureInfo.InvariantCulture),HostCapacitiveLoad.ToString()});
        }
        void RequireNotStopped() { if(stopRequested || IsDisposed)throw new OperationCanceledException("采集已停止；已写入的原始数据保留，不生成新系数。"); }
        protected override void Dispose(bool disposing)
        {
            if(disposing) {
                lock(emergencySessionGate){emergencyNotificationsDisposed=true;emergencySessionEpoch++;stopRequested=true;hostReusableSession=false;}
                safetyTimer.Stop(); safetyTimer.Dispose();
                if(box!=null)box.RequestMonitorStop();
            }
            base.Dispose(disposing);
        }

        // The main application's established calibration page owns the visible UI.
        // This form is retained only as the Pico/ART acquisition coordinator.
        internal static void ValidateHostGeometry(double cableLengthM,double velocityMPerUs,double chargePc,bool capacitor,bool distance)
        {
            if(!capacitor&&(!Xiongxin.Scope.Settings.Finite(cableLengthM)||cableLengthM<1||cableLengthM>50000))throw new ArgumentOutOfRangeException("cableLengthM","电缆长度须为 1–50000 m；当前输入不会被截断。");
            if(!capacitor&&(!Xiongxin.Scope.Settings.Finite(velocityMPerUs)||velocityMPerUs<50||velocityMPerUs>250))throw new ArgumentOutOfRangeException("velocityMPerUs","波速须为 50–250 m/μs，请按电缆材料确认。");
            if(!distance&&(!Xiongxin.Scope.Settings.Finite(chargePc)||chargePc<1||chargePc>500000))throw new ArgumentOutOfRangeException("chargePc","校准量须为 1–500000 pC；测距不校验视在放电量。");
        }
        internal async Task HostAcquireAsync(double cableLengthM, double velocityMPerUs,
            double chargePc, bool shortCable)
        {
            if(Interlocked.CompareExchange(ref hostEntryActive,1,0)!=0)throw new InvalidOperationException("请等待当前校准操作结束。");
            try{await HostAcquireCoreAsync(cableLengthM,velocityMPerUs,chargePc,shortCable);}
            finally{Interlocked.Exchange(ref hostEntryActive,0);}
        }
        async Task HostAcquireCoreAsync(double cableLengthM,double velocityMPerUs,double chargePc,bool shortCable)
        {
            if (picoReleaseUnverified) throw new IOException("设备释放状态未确认，请返回主页检查设备连接后重新进入校准。");
            if(capturing || operationBusy)throw new InvalidOperationException("请等待当前校准操作结束。");
            RequireUncancelledWorker();
            if (!integratedMode) throw new InvalidOperationException("仅主软件可调用内嵌采集。");
            if(hostSessionConfiguration!=null && hostSessionConfiguration!=HostConfigurationKey())
            {
                await HostReleaseAsync();
                throw new InvalidOperationException("设备或采集配置已改变，原会话已释放。请核对设置后重新开始校准。");
            }
            await BindHostHardwareIdentityAsync();
            hostReusableSession=false;
            stopRequested=false; LastCaptureSucceeded=false;
            noiseObservationSource=null;noiseObservationSamples=null;finalPreflightPath=null;
            if (!Directory.Exists(projectPath.Text)) throw new DirectoryNotFoundException("请先加载有效项目。");
            ValidateHostGeometry(cableLengthM,velocityMPerUs,chargePc,HostCapacitiveLoad,HostDistanceMode);
            if(!HostCapacitiveLoad) { length.Value = (decimal)cableLengthM; velocity.Value = (decimal)velocityMPerUs; }
            charge.Value = HostDistanceMode ? 100 : (decimal)chargePc;
            applyingHostImpedance = true;
            try { impedance.SelectedIndex = shortCable ? 1 : 0; }
            finally { applyingHostImpedance = false; }
            lengthAvailable = true; velocityAvailable = true;
            // The explicit Start action supplies the ordinary parameter intent.
            // Keep geometry validation above and live emergency checks below;
            // do not repeat a modal parameter confirmation on every capture.
            var answer = HostParameterConfirmationForTests != null ? HostParameterConfirmationForTests() : DialogResult.OK;
            if (answer != DialogResult.OK) { if(HostKeepSessionOpen)await HostReleaseAsync();return; }
            geometryConfirmed = true;
            try
            {
                RequireNotStopped();
                RequireCurrentHostHardwareIdentity();
                if (box != null && box.CleanupPending) throw new IOException("控制箱释放未完成，请先重试释放。");
                if (box == null || !box.Connected) ConnectBox();
                if (box == null || !box.Connected) return;
                if (!box.EmergencySignalHigh.HasValue)
                {
                    Func<string,string> translate=HostTranslateUi??(text=>text);
                    MessageBox.Show(HostDialogOwner ?? this, translate("急停状态未确认，请检查设备连接后重试。当前未取得有效急停状态。"),
                        translate("急停状态提醒"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // Only the separately authorized explicit idle impedance change
                // may write P1.2; merely selecting a Boolean never writes it.
                await box.RequireInputReadyAsync();
                if(box.ShortCableVerified) {
                    applyingHostImpedance=true;
                    try {impedance.SelectedIndex=box.ShortCable?1:0;}
                    finally {applyingHostImpedance=false;}
                }
                Log(box.ShortCableVerified?"外部信号采集；使用已确认阻抗设置："+impedance.Text:
                    "外部信号采集；阻抗按既有配置标注，本次未写入控制箱输出："+impedance.Text);
                RequireNotStopped();
                RequireCurrentHostHardwareIdentity();
                if (!picoConnected) await ConnectPico();
                RequireNotStopped();
                if (!picoConnected) return;
                RequireCurrentHostHardwareIdentity();
                if(HostCaptureForTests!=null)LastCaptureSucceeded=await HostCaptureForTests();
                else await Acquire();
                RequireCurrentHostHardwareIdentity();
                if(LastCaptureSucceeded && !stopRequested && HostKeepSessionOpen && !client.Broken && !box.CleanupPending)
                {
                    await box.RequireInputReadyAsync();
                    RequireNotStopped();
                    hostSessionConfiguration=HostConfigurationKey();
                    hostReusableSession=true;
                }
            }
            finally
            {
                if(stopRequested)LastCaptureSucceeded=false;
                if(!HostSessionReusable)await HostReleaseAsync();
            }
        }

        internal CalibrationForm(string initialProject=null, bool integrated=false)
        {
            integratedMode=integrated;
            if(integratedMode)device.Text=String.Empty;
            Text = "校准采集";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            MinimumSize = new Size(1100, 780); Size = new Size(1450, 930);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(240, 246, 249);
            HVX.Presentation.OemUiBranding.Apply(this);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 370)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(root);
            var title = new Label { Text = "校准采集", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font.FontFamily, 19, FontStyle.Bold), ForeColor = Color.FromArgb(17, 51, 77) };
            root.Controls.Add(title, 0, 0);
            var brand = new PictureBox { Dock = DockStyle.Right, Width = 300, SizeMode = PictureBoxSizeMode.Zoom };
            brand.Image = HVX.Presentation.OemUiBranding.LoadLogo();
            brand.Disposed += (s,e) => { if (brand.Image != null) brand.Image.Dispose(); };
            root.Controls.Add(brand, 1, 0);
            var side = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(10) };
            root.Controls.Add(side, 0, 1);
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));
            side.Controls.Add(fields);
            Field(fields, "项目", projectPath); Field(fields, "加载项目", loadProject);
            Field(fields, "ART 驱动目录", artPath); Field(fields, "设备名", device);
            Field(fields, "控制箱", boxStatus); Field(fields, "连接", connectBox); Field(fields, "断开", disconnectBox);
            Field(fields, "Pico 序列号", picoSerial); Field(fields, "Pico", picoStatus);
            Field(fields, "连接 Pico", connectPico); Field(fields, "断开 Pico", disconnectPico);
            Field(fields, "阻抗", impedance); Field(fields, "采集方式", captureMode);
            Field(fields, "输入量程", inputRange); Field(fields, "电缆长度 / m", length);
            Field(fields, "波速 / m/μs", velocity); Field(fields, "校准量 / pC", charge);
            Field(fields,"几何参数",geometryStatus);Field(fields,"确认参数",confirmGeometry);
            Field(fields, "脉冲频率 / Hz", frequency); Field(fields, "阈值 / V", threshold);
            Field(fields, "记录时长 / ms", duration); Field(fields, "提取窗口", windowInfo);
            Field(fields, "开始", acquire); Field(fields, "质量", qualityCapture); Field(fields, "取消", cancel);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 76)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
            var tabs=new TabControl {Dock=DockStyle.Fill};
            var wavePage=new TabPage("TDR / 校准波形") {Padding=new Padding(2)};wavePage.Controls.Add(plot);tabs.TabPages.Add(wavePage);
            var qualityPage=new TabPage("信号质量 · 50 Hz 干扰") {Padding=new Padding(2)};
            qualityPage.Controls.Add(qualityPlot);qualityPage.Controls.Add(qualityMetrics);tabs.TabPages.Add(qualityPage);
            right.Controls.Add(tabs, 0, 0); right.Controls.Add(result, 0, 1); root.Controls.Add(right, 1, 1);
            inputRange.SelectedIndex = 1;
            foreach (var n in new[] { length, velocity, frequency }) n.ValueChanged += (s,e) => UpdateWindow();
            length.ValueChanged+=(s,e)=>{lengthAvailable=true;geometryConfirmed=false;geometryStatus.Text="长度已变化，请重新确认";RefreshState();};
            velocity.ValueChanged+=(s,e)=>{velocityAvailable=true;geometryConfirmed=false;geometryStatus.Text="波速已变化，请重新确认";RefreshState();};
            confirmGeometry.Click+=(s,e)=>{geometryConfirmed=true;geometryStatus.Text="已确认（"+length.Value+" m，"+velocity.Value+" m/μs）";geometryStatus.ForeColor=Color.SeaGreen;RefreshState();};
            captureMode.SelectedIndexChanged += (s,e) => UpdateWindow(); UpdateWindow();
            connectBox.Click += (s,e) => ConnectBox(); disconnectBox.Click += (s,e) => DisconnectBox();
            loadProject.Click += (s,e) => {using(var pick=new FolderBrowserDialog {Description="选择包含 ITEM.in、CABLE.in 的项目目录"}) if(pick.ShowDialog(this)==DialogResult.OK) LoadProject(pick.SelectedPath);};
            connectPico.Click += async (s,e) => await ConnectPico(); disconnectPico.Click += async (s,e) => await DisconnectPico();
            impedance.SelectedIndexChanged += (s,e) => SwitchImpedance();
            acquire.Click += async (s,e) => await Acquire(); qualityCapture.Click += async (s,e) => await CaptureQualityOnly();
            cancel.Click += (s,e) => HostCancel();
            safetyTimer.Tick += (s,e) => PollEmergency(); safetyTimer.Start();
            FormClosing += async (s,e) =>
            {
                if (closing) return;
                e.Cancel = true;
                if (capturing || operationBusy) { HostCancel(); MessageBox.Show(this, "正在结束采集，请完成后再次关闭。", "独立校准"); return; }
                try { await HostReleaseAsync(); } catch (Exception ex) { Error(ex); }
                if (!HasOwnedHardwareResources) { closing = true; safetyTimer.Stop(); Close(); }
            };
            result.Text = "1. 按下设备急停，并按现场规程确认高压已隔离、试品已放电。\r\n2. 连接控制箱和采集设备，选择阻抗后采集。\r\n3. 原始波形自动保存到" +
                (integrated ? "当前项目的 Calibration\\Acquisition 文件夹。" : "文档中的 XIONGXIN校准验证数据文件夹。") +
                "\r\n窗口按设定电缆长度和实际采样率计算。";
            qualityMetrics.Text="至少连续记录 40 ms，覆盖两个 50 Hz 周期；显示噪声底和校准脉冲。";
            if(!String.IsNullOrWhiteSpace(initialProject))LoadProject(initialProject);
            RefreshState();
        }
        static ComboBox Choice(params string[] items)
        { var c = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Height = 29 }; c.Items.AddRange(items); c.SelectedIndex = 0; return c; }
        static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal increment)
        { return new NumericUpDown { Dock = DockStyle.Fill, Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = increment }; }
        static Button Button(string text) { return new HVX.Presentation.R2RoundedButton { Text = text, Dock = DockStyle.Fill, Height = 32 }; }
        static void Field(TableLayoutPanel fields, string name, Control c)
        { int row = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); fields.Controls.Add(new Label { Text=name, Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleLeft },0,row); fields.Controls.Add(c,1,row); }
        void RefreshState()
        {
            bool cleanup = box != null && box.CleanupPending;
            bool connected = box != null && box.Connected && !cleanup;
            boxStatus.Text = cleanup ? "释放未完成，请重试断开" : !connected ? "未连接" : box.EmergencySignalHigh == true ? "急停已解除 · 外部信号采集" : box.EmergencySignalHigh == false ? "急停已按下 · 外部信号采集" : "设备输入待确认";
            boxStatus.ForeColor = connected && box.EmergencySignalHigh.HasValue ? Color.SeaGreen : Color.Firebrick;
            picoStatus.Text = picoReleaseUnverified ? "设备状态待重新确认" : WorkerWasCancelled ? "已停止，请断开后重新连接" : picoConnected ? "已连接" : "未连接";
            connectBox.Enabled = !picoReleaseUnverified && !connected && !cleanup && !operationBusy && !capturing; disconnectBox.Enabled = (connected || cleanup) && !operationBusy && !capturing;
            connectPico.Enabled = !picoReleaseUnverified && client == null && !picoConnected && !cleanup && !operationBusy && !capturing;
            disconnectPico.Enabled = client != null && !operationBusy && !capturing;
            impedance.Enabled = !integratedMode && connected && !capturing && !operationBusy;
            qualityCapture.Enabled = connected && picoConnected && !WorkerWasCancelled && box.EmergencySignalHigh.HasValue && !operationBusy && !capturing &&
                (!integratedMode || Directory.Exists(projectPath.Text));
            confirmGeometry.Enabled=!HostCapacitiveLoad && lengthAvailable && velocityAvailable && !capturing;
            acquire.Enabled = qualityCapture.Enabled && geometryConfirmed;
            cancel.Enabled = capturing;
        }
        void ConnectBox()
        {
            try
            {
                if (picoReleaseUnverified) throw new IOException("设备状态未确认，请关闭校准并重新检查设备连接。");
                if (box != null && box.CleanupPending) throw new IOException("控制箱释放未完成，请先重试断开。");
                if (box != null && box.Connected) return;
                RequireCurrentHostHardwareIdentity();
                ControlBox connectedBox;long epoch;
                lock(emergencySessionGate){
                    epoch=++emergencySessionEpoch;
                    box=connectedBox=new ControlBox(HostControlBoxForTests!=null ? HostControlBoxForTests() :
                        new ArtControlBoxIo(device.Text.Trim(), artPath.Text.Trim(), integratedMode ? hostArtSerial : null));
                }
                connectedBox.ConnectMonitored(error=>LatchEmergencyStop(connectedBox,epoch),
                    snapshot=>QueueEmergencyNotice(connectedBox,epoch,snapshot));
                Log("控制箱已连接，已读取急停状态。");
            }
            catch (Exception ex) { Error(ex); }
            RefreshState();
        }
        internal void LoadProject(string path, bool interactive = true)
        {
            try
            {
                var defaults=ProjectDefaults.Load(path);
                projectPath.Text=Path.GetFullPath(path);
                HostCapacitiveLoad=defaults.CapacitiveLoad;
                length.Visible=velocity.Visible=confirmGeometry.Visible=!defaults.CapacitiveLoad;
                if(defaults.CapacitiveLoad) {
                    HostDistanceMode=false;geometryConfirmed=true;lengthAvailable=velocityAvailable=false;
                    geometryStatus.Text=defaults.Notice;geometryStatus.ForeColor=Color.SeaGreen;
                    Log("项目："+projectPath.Text+"；"+defaults.Notice);UpdateWindow();RefreshState();return;
                }
                if(defaults.LengthM<(double)length.Minimum||defaults.LengthM>(double)length.Maximum)throw new ArgumentOutOfRangeException("length","电缆长度须为 1–50000 m；不会自动截断项目长度。");
                length.Value=(decimal)defaults.LengthM;
                if(defaults.VelocityMPerUs>0) velocity.Value=(decimal)defaults.VelocityMPerUs;
                lengthAvailable=true;velocityAvailable=defaults.VelocityMPerUs>0;
                geometryConfirmed=false;
                geometryStatus.Text=defaults.VelocityMPerUs>0?"请核对并确认材料波速":"缺少波速，请填写并确认";
                geometryStatus.ForeColor=Color.Firebrick;
                Log("项目："+projectPath.Text+"；长度 "+length.Value+" m；材料 "+defaults.Material+
                    (defaults.VelocityMPerUs>0?"；波速 "+velocity.Value+" m/μs（"+defaults.VelocitySource+"）":"；未获得可靠波速，须人工填写并核对。")+
                    (defaults.Notice.Length>0?"\r\n"+defaults.Notice:""));
                RefreshState();
            }
            catch(Exception ex){if(interactive)Error(ex);else Log("项目参数读取失败："+ex.Message);}
        }
        void DisconnectBox()
        {
            preflightMemory.Forget();
            lock(emergencySessionGate){emergencySessionEpoch++;}
            try { if (box != null) box.Disconnect(); Log("控制箱已断开。"); }
            catch (Exception ex) { Error(ex); }
            RefreshState();
        }
        void LatchEmergencyStop(ControlBox owner,long epoch)
        {
            ICalibrationWorker ownedWorker;
            lock(emergencySessionGate){
                if(emergencyNotificationsDisposed||epoch!=emergencySessionEpoch||!Object.ReferenceEquals(box,owner))return;
                stopRequested=true;hostReusableSession=false;ownedWorker=client;cancelledWorker=ownedWorker;
            }
            // Cancellation does not wait for the UI message queue and never
            // closes the ART/Pico handles on the input-reader thread.
            if(ownedWorker!=null)ownedWorker.Cancel();
        }
        void QueueEmergencyNotice(ControlBox owner,long epoch,CalibrationEmergencySnapshot snapshot)
        {
            if(hostEntrySafetyCheckActive)return; // Entry caller owns the single reminder.
            if(snapshot.Error==null)return; // Normal display refresh reads a managed snapshot.
            lock(emergencySessionGate)if(emergencyNotificationsDisposed||epoch!=emergencySessionEpoch||!Object.ReferenceEquals(box,owner))return;
            Control dispatcher=HostDialogOwner as Control??this;
            if(dispatcher.IsDisposed||!dispatcher.IsHandleCreated||Interlocked.Exchange(ref emergencyNoticeQueued,1)!=0)return;
            try{dispatcher.BeginInvoke((Action)(()=>{
                Interlocked.Exchange(ref emergencyNoticeQueued,0);
                lock(emergencySessionGate)if(emergencyNotificationsDisposed||epoch!=emergencySessionEpoch||!Object.ReferenceEquals(box,owner))return;
                PollEmergency();
            }));}catch(InvalidOperationException){Interlocked.Exchange(ref emergencyNoticeQueued,0);}
        }
        void PollEmergency()
        {
            if(hostEntrySafetyCheckActive)return; // Do not duplicate entry notices or reads.
            if (box == null || !box.Connected || box.CleanupPending) return;
            try
            {
                var snapshot=box.EmergencySnapshot;
                if(snapshot!=null&&snapshot.Error!=null) {
                    throw new IOException("无法确认设备急停状态，当前校准已停止。请检查连接后重新开始。",snapshot.Error);
                }
                if(snapshot!=null&&!snapshot.High.HasValue){RefreshState();return;}
                if(snapshot==null)box.Poll();
                readFailureNotified = false;
                // HIGH/LOW only update the input display in this calibration-only
                // session. Neither level starts, resumes, or enables HV output.
            }
            catch (Exception ex)
            {
                if (capturing) HostCancel();
                if (!readFailureNotified)
                {
                    readFailureNotified = true;
                    System.Diagnostics.Debug.WriteLine("Emergency status read: " + ex);
                    Func<string,string> translate=HostTranslateUi??(text=>text);
                    MessageBox.Show(HostDialogOwner ?? this, translate("急停状态未确认，请检查设备连接后重试。当前未取得有效急停状态。"),
                        translate("急停状态提醒"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            RefreshState();
        }
        async void SwitchImpedance()
        {
            // The integrated external-input entry is not an output-control tool.
            if (integratedMode || applyingHostImpedance || box == null || !box.Connected || box.CleanupPending || capturing || operationBusy) return;
            operationBusy=true;RefreshState();
            try { await box.SetShortCableAsync(impedance.SelectedIndex == 1); Log("阻抗已选择：" + impedance.Text); }
            catch (Exception ex) { Error(ex); }
            finally{operationBusy=false;if(!IsDisposed)RefreshState();}
        }
        async Task ConnectPico()
        {
            if (operationBusy || client != null) return;
            if (picoReleaseUnverified || (box != null && box.CleanupPending)) { Error(new IOException("设备释放未完成，请先检查设备状态。")); return; }
            operationBusy = true; RefreshState();
            try
            {
                string worker = null;
                RequireCurrentHostHardwareIdentity();
                if (integratedMode && !String.Equals(Path.GetExtension(Assembly.GetExecutingAssembly().Location), ".exe", StringComparison.OrdinalIgnoreCase))
                    worker = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        "..", "CalibrationAcquisition", "XIONGXIN.Calibration.Verification.exe"));
                client = HostWorkerForTests!=null ? HostWorkerForTests() : new WorkerClient(worker);
                var config = Settings();
                var reply = await client.Send(new Command { Op="OPEN", Config=config }, 12000);
                if (!reply.Ok || !reply.Connected) throw new IOException(reply.Message);
                if(integratedMode && !String.Equals(config.Serial,reply.Serial,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("采集卡返回的身份与所选设备不符，已禁止校准。请返回主页检查设备连接。");
                picoConnected = true; picoSerial.Text=reply.Serial; Log("Pico 已连接：" + reply.Serial + " / " + reply.Model);
            }
            catch (Exception ex) { Error(ex); if (client != null) { try { await RetirePicoWithoutReleaseProof(); } catch (Exception cleanup) { Error(cleanup); } } }
            finally { operationBusy = false; RefreshState(); }
        }
        async Task DisconnectPico()
        {
            if (client == null) return;
            operationBusy = true; RefreshState();
            try {
                if (client.Broken) await RetirePicoWithoutReleaseProof();
                else { await client.Close(); client = null; picoConnected = false; Log("Pico 已释放。"); }
            }
            catch (Exception ex) { Error(ex); }
            finally { operationBusy = false; RefreshState(); }
        }
        async Task RetirePicoWithoutReleaseProof()
        {
            // Process exit is not a successful native Close acknowledgement.
            // Keep that distinction after disposing the retired process object.
            await client.RetireUnresponsive();
            picoReleaseUnverified = true;
            client = null;
            picoConnected = false;
            Log("采集进程已退出，设备释放状态未确认。请返回主页检查设备连接。");
        }
        Settings Settings()
        {
            int[] ranges = {3,4,5,6,7,8};
            return new Settings { Simulation=false, Serial=picoSerial.Text.Trim(), Resolution=2,
                A=new ChannelSettings {Enabled=true,Range=ranges[inputRange.SelectedIndex],Coupling=CaptureVoltage.AcCoupling},
                B=new ChannelSettings {Enabled=false,Range=6,Coupling=1},
                RateMS=125,DurationMs=(double)duration.Value,
                TriggerMode=captureMode.SelectedIndex == 0 ? 0 : 1,
                PrePercent=captureMode.SelectedIndex == 0 ? 0 : 20,
                ThresholdV=(double)threshold.Value, TimeoutMs=10000 };
        }
        CalibrationWindowSettings Window(double sampleRate, double fullScale)
        { return new CalibrationWindowSettings { SampleRateHz=sampleRate,CableLengthM=HostCapacitiveLoad?0:(double)length.Value,VelocityMPerUs=HostCapacitiveLoad?0:(double)velocity.Value,CapacitiveLoad=HostCapacitiveLoad,
            FrequencyHz=(double)frequency.Value,ThresholdV=(double)threshold.Value,FullScaleV=fullScale,
            PrePeakSamples=400,TailSamples=256,PeakSearchSamples=128 }; }
        void UpdateWindow()
        {
            try
            {
                var w=Window(125000000, .2); w.Validate();
                windowInfo.Text = w.WindowSamples.ToString("N0") + " 点 / " + (w.WindowSamples / 125.0).ToString("F2") + (HostCapacitiveLoad?" μs（电容入射脉冲包络）":" μs（125 MS/s，覆盖 5 倍线长）");
            }
            catch (Exception ex) { windowInfo.Text=ex.Message; }
        }
        async Task Acquire()
        {
            if (capturing || operationBusy || !picoConnected || box == null || !box.Connected) return;
            if(WorkerWasCancelled){Error(new InvalidOperationException("本次采集已停止。请先断开并重新连接采集设备，再开始新的校准。"));return;}
            if(!integratedMode)stopRequested=false;
            LastCaptureSucceeded=false;
            var captureBox=box;
            long captureImpedanceRevision=captureBox.ImpedanceRevision;
            bool capturedShortCable=captureBox.ShortCableVerified?captureBox.ShortCable:impedance.SelectedIndex==1;
            bool captureOwned=false;
            string folder = Path.Combine(CaptureRoot(), DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0,6));
            CalibrationSelfCheckWindow selfCheck=null;
            try
            {
                acquisitionFlow.Begin();capturing = true; RefreshState();await captureBox.BeginCaptureAsync();captureOwned=true;RequireNotStopped();
                if(!Object.ReferenceEquals(box,captureBox))throw new OperationCanceledException("设备会话已改变，当前校准已取消。");
                Directory.CreateDirectory(folder);
                var config=Settings();
                string rangeContext=String.Join("|",new[]{HostConfigurationKey(),charge.Value.ToString(CultureInfo.InvariantCulture),
                    frequency.Value.ToString(CultureInfo.InvariantCulture),impedance.SelectedIndex.ToString(CultureInfo.InvariantCulture)});
                config.A.Range=preflightMemory.StartingRange(client,captureBox,rangeContext);
                config.TriggerMode=0; config.PrePercent=0; config.Validate();
                selfCheck=new CalibrationSelfCheckWindow(Icon,HostCancel,HostTranslateUi);selfCheck.UpdateStep("内部参数适配中…");selfCheck.Show(HostDialogOwner??this);
                Log("采集使用 AC 耦合；每段读取后先去除直流偏置，再进行滤波与脉冲处理。原始 ADC 记录保留。\r\n从"+
                    (config.A.Range==Xiongxin.Scope.Settings.Ranges.Length-1?"最大量程 ±20 V":"本输入会话已实测量程 ±"+Xiongxin.Scope.Settings.Ranges[config.A.Range]+" V")+
                    "开始新的 40 ms 预采，正在确认本次信号幅值范围。");
                PreflightDecision decision=null; Capture preflight=null;
                var rawPaths=new List<string>();
                bool settled=false;
                // Each range decision is followed by a new continuous acquisition.
                // Never label a record from the previous range as the final noise view.
                for(int attempt=0;attempt<12;attempt++) {
                    RequireNotStopped();
                    RequireStableCaptureImpedance(captureBox,captureImpedanceRevision,folder);
                    acquisitionFlow.RequireCapture(true);
                    // Publish each complete measured segment, even when it later
                    // fails the trigger test. A preview is never a calibration.
                    preflight=await CaptureQuality(folder,config,"preflight_"+(attempt+1).ToString("D2")+".xxs");
                    rawPaths.Add(finalPreflightPath);
                    RequireStableCaptureImpedance(captureBox,captureImpedanceRevision,folder);
                    decision=await Task.Run(()=> {
                        // Physical headroom must still use raw ADC values; DC
                        // removal cannot make a clipped input safe to average.
                        var rangeDecision=AcquisitionPlanning.Plan(AcquisitionPlanning.RawVolts(preflight),config.A.Range,preflight.Info.Overflow!=0);
                        var observation=Object.ReferenceEquals(noiseObservationSource,preflight)?noiseObservationSamples:null;
                        var pulseDecision=AcquisitionPlanning.Plan(observation??AcquisitionPlanning.NoiseObservation(preflight),config.A.Range,false);
                        rangeDecision.NoiseV=pulseDecision.NoiseV;rangeDecision.ThresholdV=pulseDecision.ThresholdV;rangeDecision.Falling=pulseDecision.Falling;rangeDecision.FilteredPeakV=pulseDecision.PeakV;
                        return rangeDecision;
                    });
                    RequireNotStopped();
                    bool currentRangeFits=decision.RangeIndex==config.A.Range && preflight.Info.Overflow==0 && decision.PeakV<Xiongxin.Scope.Settings.Ranges[config.A.Range]*.97;
                    Log("当前量程 ±"+Xiongxin.Scope.Settings.Ranges[config.A.Range]+" V；建议量程 ±"+Xiongxin.Scope.Settings.Ranges[decision.RangeIndex]+" V；触发幅值 "+decision.ThresholdV.ToString("G4")+" V。"+
                        (currentRangeFits?"当前实采量程已合适，复用本段预采。":"正在以新量程复采连续 40 ms 数据。"));
                    // CaptureQuality verifies the record's actual range/coupling.
                    // A fitting first segment already supplies a valid noise view;
                    // only a changed range requires another physical acquisition.
                    if(currentRangeFits){settled=true;break;}
                    if(decision.RangeIndex==config.A.Range)break;
                    config.A.Range=decision.RangeIndex;
                    config.ThresholdV=decision.Falling?-decision.ThresholdV:decision.ThresholdV;
                }
                if(!settled)throw new InvalidOperationException("自动量程未稳定或信号仍溢出，已保留预采数据，不生成校准系数。");
                if(decision.FilteredPeakV<=Math.Max(decision.NoiseV*8,Xiongxin.Scope.Settings.Ranges[config.A.Range]/32767*16)) {
                    Log("预采段没有足够高于噪声的脉冲，不能自动设置可靠触发。已显示并保留当前预采数据，不生成校准系数。");
                    throw new InvalidOperationException("没有检测到校准信号，请核对是否正确接入了视在放电量校准器！");
                }
                preflightMemory.Remember(client,captureBox,rangeContext,config.A.Range);
                selfCheck.Complete();selfCheck.Dispose();selfCheck=null;
                acquisitionFlow.RangeAndTriggerReady();
                double actualRate=1e9/preflight.Info.IntervalNs;
                double screeningThreshold=AcquisitionPlanning.ResolveScreeningThreshold(HostScreeningThresholdV,decision.ThresholdV,Xiongxin.Scope.Settings.Ranges[config.A.Range]);
                if(HostAutomaticThresholdResolved!=null)HostAutomaticThresholdResolved(decision.ThresholdV);
                var estimate=Window(actualRate,Xiongxin.Scope.Settings.Ranges[config.A.Range]); estimate.ThresholdV=screeningThreshold;estimate.Validate();
                bool segmentMode=integratedMode?HostAcquisitionMode=="Segment":captureMode.SelectedIndex==0;
                config.CalibrationPulse=!segmentMode;
                config.TriggerMode=1;config.ThresholdV=decision.Falling?-decision.ThresholdV:decision.ThresholdV;
                config.TriggerEdge=decision.Falling?3:2;
                config.DurationMs=Math.Max(1,(estimate.WindowSamples/actualRate*1000+.6)*1.3);
                config.PrePercent=.3/config.DurationMs*100; config.Validate();
                int blocks=HostAverageCount;
                if(!segmentMode&&(blocks<3||blocks>256))throw new InvalidOperationException("触发段模式的平均次数必须为 3～256。");
                if(segmentMode){config.TriggerMode=0;config.PrePercent=0;config.DurationMs=(double)duration.Value;}
                var filtered=new List<double[]>();var segmentWindows=new List<double[]>(); Capture last=null;
                var accumulated=new List<double[]>();int captureOrdinal=0,templateExcluded=0,outlierExcluded=0;
                int segmentCandidates=0,segmentRejected=0;
                CalibrationWindowResult extracted=null;bool qualityOverride=false;string qualityReviewReason="";
                double sampleRate=actualRate;
                var window=Window(sampleRate,Xiongxin.Scope.Settings.Ranges[config.A.Range]);window.ThresholdV=screeningThreshold;window.Validate();
                while(true) {
                filtered.Clear();segmentWindows.Clear();
                int remaining=Math.Max(1,blocks-accumulated.Count);
                // A segment is a single finite record. Its pulse population is not
                // a request for N separate acquisitions or a reason to refill it.
                int captureLimit=segmentMode?1:remaining;
                for (int i=0;i<captureLimit;i++)
                {
                    RequireNotStopped();
                    RequireStableCaptureImpedance(captureBox,captureImpedanceRevision,folder);
                    acquisitionFlow.RequireCapture(false);
                    await captureBox.RequireInputReadyAsync();
                    RequireNotStopped();
                    string path=Path.Combine(folder,"capture_"+(++captureOrdinal).ToString("D4")+".xxs");
                    Reply reply=await client.Send(new Command {Op="CAPTURE",Config=config,DataPath=path}, config.TimeoutMs+12000);
                    RequireNotStopped();
                    if (!reply.Ok) throw new IOException(reply.Message);
                    rawPaths.Add(path);
                    RequireStableCaptureImpedance(captureBox,captureImpedanceRevision,folder);
                    await captureBox.RequireInputReadyAsync();RequireNotStopped();
                    last=Wire.Load(path);plot.SetData(last);
                    if(last.Info.Config.A.Coupling!=CaptureVoltage.AcCoupling)throw new IOException("返回记录未使用本次要求的 AC 耦合，请重新采集。");
                    if(Math.Abs(1e9/last.Info.IntervalNs-actualRate)>actualRate*1e-6 || last.Info.Config.A.Range!=config.A.Range || last.Info.Serial!=preflight.Info.Serial)
                        throw new IOException("连续采集中的实际采样率或量程发生变化，不能混合平均。");
                    if(last.Info.Overflow!=0) {Log("该段溢出，保留原始数据但不参与平均。");continue;}
                    var captured=last;
                    var values=await Task.Run(()=>PdLinearHighPass.Apply(AcquisitionPlanning.Volts(captured),actualRate));
                    RequireNotStopped();
                    if(segmentMode) {
                        var selected=await Task.Run(()=>CalibrationWindowing.FromFilteredSegment(values,estimate,PdLinearHighPass.GuardSamples(actualRate)));
                        segmentWindows.AddRange(selected.Windows);
                        segmentCandidates=selected.CandidateCount;segmentRejected=selected.RejectedCount;
                    }else filtered.Add(values);
                    Log((segmentMode?"本段采集":"采集 " + (i+1) + "/" + blocks) + "：" + reply.Samples + " 点，实际间隔 " + reply.IntervalNs.ToString("F3") + " ns。\r\n原始文件：" + path);
                }
                if (last==null) throw new IOException("没有返回波形。");
                // All finite block/segment acquisitions have completed. Release the
                // acquisition state before analysis, callbacks or automatic saving.
                // No capture command is allowed below this point.
                acquisitionFlow.StopForReview();captureBox.EndCapture();captureOwned=false;capturing=false;operationBusy=true;RefreshState();
                Log("采集已自动停止，正在处理本次波形；不会因保存、重绘或调整游标而重新采集。");
                windowInfo.Text=window.WindowSamples.ToString("N0")+" 点 / "+(window.WindowSamples / sampleRate * 1e6).ToString("F2")+" μs（实际采样率）";
                var batch=await Task.Run(()=> {
                    var windows=segmentMode ? new CalibrationWindowResult{Windows=segmentWindows.ToArray(),CandidateCount=segmentCandidates,RejectedCount=segmentRejected,Message="本段候选 "+segmentCandidates+" 次；周期/边界排除 "+segmentRejected+" 次"} : CalibrationWindowing.FromFilteredTriggeredBlocks(filtered.ToArray(),window,PdLinearHighPass.GuardSamples(sampleRate));
                    if(HostPulseExcluded!=null) {
                        var allowed=new List<double[]>();foreach(var pulse in windows.Windows)if(!HostPulseExcluded(pulse,window.PrePeakSamples,sampleRate))allowed.Add(pulse);
                        int removed=windows.Windows.Length-allowed.Count;windows.RejectedCount+=removed;windows.Windows=allowed.ToArray();
                        templateExcluded+=removed;
                        windows.Message+="；用户干扰模板排除 "+removed+" 次";
                    }
                    return windows;
                });
                accumulated.AddRange(batch.Windows);
                if(accumulated.Count>4096||(long)accumulated.Count*window.WindowSamples>64000000)
                    throw new InvalidOperationException("本次待处理数据过多。原始记录已保留，请检查校准信号与过滤条件后重新采集。");
                var pool=new CalibrationWindowResult{Windows=accumulated.ToArray(),CandidateCount=segmentMode?batch.CandidateCount:accumulated.Count,RejectedCount=segmentMode?batch.RejectedCount:0,Message=batch.Message};
                extracted=await Task.Run(()=>AcquisitionPlanning.SelectConsistentPulses(pool));
                outlierExcluded+=accumulated.Count-extracted.Windows.Length;
                accumulated.Clear();accumulated.AddRange(extracted.Windows);
                extracted.Message+="；"+(segmentMode?"本段全部有效 "+accumulated.Count:"累计有效 "+accumulated.Count+" / "+blocks)+" 次；干扰过滤 "+templateExcluded+" 次；形态排除 "+outlierExcluded+" 次。";
                Log(extracted.Message);RequireNotStopped();await captureBox.RequireInputReadyAsync();RequireNotStopped();
                if(segmentMode) {
                    if(extracted.Windows.Length==0)throw new InvalidOperationException("本段没有可计算的有效校准脉冲。原始记录已保留，请检查校准源或筛选条件后重新采集。");
                    // SelectConsistentPulses already averages every surviving window.
                    // Do not truncate to N, request supplement, or label this choice
                    // as an operator quality override that never happened.
                    break;
                }
                if(accumulated.Count>=blocks) {
                    extracted=AcquisitionPlanning.ContinueForReview(new CalibrationWindowResult{Windows=System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Take(accumulated,blocks)),Message=extracted.Message});
                    extracted.Message="已累计 "+blocks+" 次有效脉冲；干扰过滤 "+templateExcluded+" 次；形态排除 "+outlierExcluded+" 次。";
                    break;
                }
                qualityReviewReason="当前有效脉冲 "+accumulated.Count+" / "+blocks+" 次。\r\n干扰过滤 "+templateExcluded+" 次，形态排除 "+outlierExcluded+" 次。";
                string auditError;
                if(!CalibrationProjectAudit.TryWrite(projectPath.Text,HostPhaseCode,HostLevelCode,"QualityCapturePrompt","Shown",qualityReviewReason,out auditError))
                    Log("项目操作日志未写入："+auditError);
                DialogResult choice=ChooseSupplementCapture(qualityReviewReason,pool.Windows.Length>0);
                if(!CalibrationProjectAudit.TryWrite(projectPath.Text,HostPhaseCode,HostLevelCode,"QualityCaptureChoice",choice.ToString(),qualityReviewReason,out auditError))
                    Log("项目操作日志未写入："+auditError);
                RequireNotStopped();await captureBox.RequireInputReadyAsync();RequireNotStopped();
                if(choice==DialogResult.Retry) {
                    acquisitionFlow.ResumeForSupplement();await captureBox.BeginCaptureAsync();captureOwned=true;RequireNotStopped();capturing=true;operationBusy=false;RefreshState();
                    Log("已确认再次采集，保留 "+accumulated.Count+" 次有效脉冲继续累积。");continue;
                }
                if(choice!=DialogResult.Ignore)throw new OperationCanceledException("已停止补采，原始数据保留。");
                extracted=await Task.Run(()=>AcquisitionPlanning.ContinueForReview(pool));qualityOverride=true;
                File.WriteAllText(Path.Combine(folder,"quality-review-choice.txt"),DateTime.UtcNow.ToString("o")+"\r\n操作员选择继续计算，仅供复核。\r\n"+qualityReviewReason,Encoding.UTF8);
                break;
                }
                RequireNotStopped();await captureBox.RequireInputReadyAsync();RequireNotStopped();
                RequireStableCaptureImpedance(captureBox,captureImpedanceRevision,folder);
                double measuredLength=(double)length.Value,measuredVelocity=(double)velocity.Value;
                var search=await Task.Run(()=>HostDistanceMode?
                    CalibrationReflectionBandwidth.Search(extracted.Average,window.PrePeakSamples,sampleRate,measuredLength,measuredVelocity,.5,HostCapacitiveLoad,()=>stopRequested):
                    CalibrationReflectionBandwidth.SearchTerminal(extracted.Average,window.PrePeakSamples,sampleRate,measuredLength,measuredVelocity,.15,HostCapacitiveLoad,()=>stopRequested));
                var tdr=search.Reflection;var bandwidth=search.Bandwidth;
                var qualityWindows=extracted.Windows;
                // Charge metrology always uses the complete 10 kHz high-passed
                // pulses. The separate low-pass search is only for localization.
                RequireNotStopped();await captureBox.RequireInputReadyAsync();RequireNotStopped();
                var assessment=CalibrationIncidentQuality.Analyze(qualityWindows,200,336,Math.Min(window.WindowSamples,464),
                    (double)charge.Value,window.FullScaleV,false,config.A.Range+"/"+impedance.SelectedIndex+"/"+PdLinearHighPass.FilterId,null);
                if(segmentMode&&qualityWindows.Length<3&&assessment.CoefficientPcPerV>0&&!Double.IsInfinity(assessment.CoefficientPcPerV)) {
                    assessment.SaveRecommended=false;assessment.Quality="脉冲重复性证据不足";
                    assessment.Message+=" 本段仅有 "+qualityWindows.Length+" 个有效脉冲，已全部用于计算；周期与重复性证据不足，请核对结果。";
                }
                var metricsHandler = HostSignalMetricsCaptured;
                RequireStableCaptureImpedance(captureBox,captureImpedanceRevision,folder);
                if (metricsHandler != null && !Double.IsNaN(assessment.SnrDb) && !Double.IsInfinity(assessment.SnrDb))
                    metricsHandler(assessment.SnrDb, assessment.IncidentPeakV, assessment.NoiseRmsV);
                string summary=extracted.Message+"\r\nAC 耦合；读取后按整段均值去直流，再滤波、提取和平均。\r\n脉冲筛选阈值：±"+screeningThreshold.ToString("G5",CultureInfo.InvariantCulture)+" V（"+(HostScreeningThresholdV.HasValue?"手动":"自动")+"）；触发设置仍按预采信号确定。\r\n质量："+assessment.Quality+"；入射峰值："+assessment.IncidentPeakV.ToString("G5")+" V；系数："+assessment.CoefficientPcPerV.ToString("G5")+" pC/V\r\n"+assessment.Message+
                    (HostCapacitiveLoad?"\r\n电容校准：仅入射，不进行测距或反射分析。":"\r\nTDR："+(tdr.Valid?"相关度 "+Math.Abs(tdr.Correlation).ToString("F3")+"；估计长度 "+tdr.DistanceM.ToString("F1")+" m":"未自动确定长度")+"。"+tdr.Message);
                File.WriteAllText(Path.Combine(folder,"calibration-review.txt"),summary+(HostCapacitiveLoad?"\r\n长度/波速：不适用；开窗：":"\r\n长度："+length.Value+" m；波速："+velocity.Value+" m/μs；开窗：")+window.WindowSamples+" 点；实际采样率："+sampleRate.ToString("R",CultureInfo.InvariantCulture)+" Hz；量程：±"+Xiongxin.Scope.Settings.Ranges[config.A.Range]+" V；触发："+config.ThresholdV.ToString("R",CultureInfo.InvariantCulture)+" V；模式："+(segmentMode?"Segment；平均范围：本段全部有效脉冲":"Pulse；请求平均次数："+blocks)+"；实际平均次数："+extracted.Windows.Length+"。\r\n高通："+PdLinearHighPass.FilterId+"；低通："+(bandwidth!=null&&bandwidth.CutoffMHz>0?PdLinearLowPass.FilterId+" / "+bandwidth.CutoffMHz+" MHz":"未启用")+"；反射/入射比："+(HostCapacitiveLoad?"不适用":bandwidth==null?"未确定":bandwidth.Ratio.ToString("R",CultureInfo.InvariantCulture))+"。\r\n已自动停止采集。原始数据请见本目录 .xxs 文件。",Encoding.UTF8);
                var waveformHandler = HostWaveformCaptured;
                if(bandwidth!=null)File.AppendAllText(Path.Combine(folder,"calibration-review.txt"),
                    "\r\nDisplayPolicy="+CalibrationDisplayBandwidth.EngineeringPolicyId+"; TargetRatio="+bandwidth.TargetRatio.ToString("R",CultureInfo.InvariantCulture)+
                    "; Stable="+(bandwidth.Stable.HasValue?bandwidth.Stable.Value.ToString():"NotEvaluated")+"; Sufficient="+bandwidth.Sufficient+
                    "; Evidence="+(bandwidth.StabilityReason??"NotEvaluated")+"; EngineeringDisplayCheckOnly=true; PhysicalAcceptance=false\r\n",Encoding.UTF8);
                if (waveformHandler != null) waveformHandler(extracted.Average, 0, 1.0 / sampleRate);
                var completed=HostCaptureCompleted;
                if(completed!=null)completed(new CalibrationCaptureResult {Average=extracted.Average,Windows=extracted.Windows,
                    SampleRateHz=sampleRate,Assessment=assessment,Reflection=tdr,RawPaths=rawPaths.ToArray(),Folder=folder,
                    DeviceSerial=last.Info.Serial,DeviceModel=last.Info.Model,RangeIndex=config.A.Range,ShortCable=capturedShortCable,
                    PicoCoupling=last.Info.Config.A.Coupling,DcRemoval=CaptureVoltage.DcRemovalId,
                    ThresholdV=config.ThresholdV,ScreeningThresholdV=screeningThreshold,ManualScreeningThreshold=HostScreeningThresholdV.HasValue,AcquisitionMode=segmentMode?"Segment":"Pulse",RequestedCount=segmentMode?0:blocks,Bandwidth=bandwidth,CapacitiveLoad=HostCapacitiveLoad,
                    QualityOverrideForReview=qualityOverride,QualityReviewReason=qualityReviewReason});
                LastCaptureSucceeded=!qualityOverride;
                Log(summary);
                if (qualityOverride || !assessment.SaveRecommended) Log("本次结果保留质量提示。请在校准页面核对后选择是否保存；原始波形已保留。");
                else Log("信号质量检查完成，可在校准页面核对并保存。记录目录："+folder);
            }
            catch(OperationCanceledException ex) { preflightMemory.Forget();Log(ex.Message); }
            catch (Exception ex) { preflightMemory.Forget();if(selfCheck!=null){selfCheck.Complete();selfCheck.Dispose();selfCheck=null;} Error(ex); Log("本次验证目录："+folder+"；仅存在已完整写入的原始段时才可用于复核。"); }
            finally { if(selfCheck!=null){selfCheck.Complete();selfCheck.Dispose();}acquisitionFlow.Finish();if(captureOwned)captureBox.EndCapture(); capturing=false;operationBusy=false; RefreshState(); }
        }
        async Task CaptureQualityOnly()
        {
            if(capturing || operationBusy || !picoConnected || box==null || !box.Connected)return;
            if(WorkerWasCancelled){Error(new InvalidOperationException("本次采集已停止。请先断开并重新连接采集设备，再开始新的校准。"));return;}
            stopRequested=false;
            var captureBox=box;
            bool captureOwned=false;
            string folder=Path.Combine(CaptureRoot(),DateTime.Now.ToString("yyyyMMdd_HHmmss",CultureInfo.InvariantCulture)+"_quality_"+Guid.NewGuid().ToString("N").Substring(0,6));
            try
            {
                capturing=true;RefreshState();await captureBox.BeginCaptureAsync();captureOwned=true;RequireNotStopped();
                if(!Object.ReferenceEquals(box,captureBox))throw new OperationCanceledException("设备会话已改变，当前校准已取消。");
                Directory.CreateDirectory(folder);
                await CaptureQuality(folder,Settings());
            }
            catch(Exception ex){Error(ex);}
            finally{if(captureOwned)captureBox.EndCapture();capturing=false;RefreshState();}
        }
        DialogResult ChooseSupplementCapture(string details,bool canReview)
        {
            if(SupplementChoiceForTests!=null)return SupplementChoiceForTests(details,canReview);
            using(var dialogFont=new Font("Microsoft YaHei UI",10f))
            using(var dialog=new Form {Text="继续采集",Icon=Icon,StartPosition=FormStartPosition.CenterParent,
                FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,
                ClientSize=new Size(570,220),Font=dialogFont,AutoScaleMode=AutoScaleMode.Dpi}) {
                var message=new Label{AutoSize=false,Dock=DockStyle.Fill,Padding=new Padding(18),Text=details+
                    "\r\n\r\n再次采集会保留有效脉冲并继续累积。\r\n继续计算会保留原始记录及质量提示，结果仅供核对。"};
                var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=55,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8)};
                var stop=new HVX.Presentation.R2RoundedButton{Text="停止",DialogResult=DialogResult.Cancel,Size=new Size(95,34)};
                var review=new HVX.Presentation.R2RoundedButton{Text="继续计算",DialogResult=DialogResult.Ignore,Enabled=canReview,Size=new Size(110,34)};
                var retry=new HVX.Presentation.R2RoundedButton{Text="再次采集",DialogResult=DialogResult.Retry,Size=new Size(110,34)};
                buttons.Controls.Add(stop);buttons.Controls.Add(review);buttons.Controls.Add(retry);
                dialog.Controls.Add(message);dialog.Controls.Add(buttons);dialog.AcceptButton=retry;dialog.CancelButton=stop;
                return dialog.ShowDialog(HostDialogOwner??this);
            }
        }
        async Task<Capture> CaptureQuality(string folder,Settings requested,string fileName="signal-quality-50Hz.xxs",bool display=true)
        {
            var captureBox=box;long impedanceRevision=captureBox.ImpedanceRevision;
            var config=AcquisitionPlanning.QualitySettings(requested);
            RequireNotStopped();
            config.Validate();await box.RequireInputReadyAsync();
            RequireNotStopped();
            string path=Path.Combine(folder,fileName);
            var reply=await client.Send(new Command{Op="CAPTURE",Config=config,DataPath=path},config.TimeoutMs+12000);
            RequireNotStopped();
            if(!reply.Ok)throw new IOException(reply.Message);
            RequireStableCaptureImpedance(captureBox,impedanceRevision,folder);
            await box.RequireInputReadyAsync();RequireNotStopped();var wave=Wire.Load(path);
            AcquisitionPlanning.RequireQualityRecord(wave,config);
            finalPreflightPath=path;
            if(display)await ShowQualityAsync(wave);
            Log("信号质量原始波形："+path);
            return wave;
        }
        async Task ShowQualityAsync(Capture wave)
        {
            qualityPlot.SetData(wave);
            var noiseHandler = HostNoiseCaptured;
            if (noiseHandler != null && wave.A != null && wave.A.Length > 0)
            {
                // Selection must index original samples at their actual timestamps.
                // Filter the full observation off the UI thread. The diagnostic
                // ScopePlot and original ADC retain their separate raw contract.
                var observation=await Task.Run(()=>AcquisitionPlanning.NoiseObservation(wave));
                RequireNotStopped();
                noiseObservationSource=wave;noiseObservationSamples=observation;
                noiseHandler(observation,0,wave.Info.IntervalNs*1e-9);
            }
            // A metric failure must not hide an otherwise valid raw capture.
            try {
                var q=SignalQuality.FromCapture(wave,(double)threshold.Value);
                qualityMetrics.Text="连续 "+q.DurationMs.ToString("F2")+" ms · 噪声 RMS "+q.NoiseRmsV.ToString("G4")+" V · 50 Hz 分量约 "+q.Fundamental50V.ToString("G4")+" V · 峰值 "+q.MaxAbsV.ToString("G4")+" V";
            } catch(InvalidOperationException ex) { qualityMetrics.Text="当前原始采集已显示；噪声指标暂不可用。";Log(ex.Message); }
        }
        void RequireStableCaptureImpedance(ControlBox owner,long revision,string folder)
        {
            if(owner.ImpedanceRevision!=revision&&Directory.Exists(folder)) {
                string detail="UTC="+DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+
                    "\r\nCaptureImpedanceRevision="+revision.ToString(CultureInfo.InvariantCulture)+
                    "\r\nCurrentImpedanceRevision="+owner.ImpedanceRevision.ToString(CultureInfo.InvariantCulture)+
                    "\r\nShortCableWriteConfirmed="+(owner.ShortCableVerified&&owner.ShortCable)+
                    "\r\nStableCalibration=false\r\nRaw captures retained; impedance changed during this acquisition.\r\n";
                try {File.WriteAllText(Path.Combine(folder,"impedance-transition.txt"),detail,Encoding.UTF8);}
                catch(Exception journalError) {Log("阻抗切换记录未写入："+journalError.Message);}
            }
            owner.RequireStableImpedance(revision);
        }
        void Log(string text) { result.AppendText("\r\n["+DateTime.Now.ToString("HH:mm:ss")+"] "+text+"\r\n"); }
        string CaptureRoot()
        {
            if(integratedMode && Directory.Exists(projectPath.Text))
                return Path.Combine(projectPath.Text,"Calibration","Acquisition");
            return dataRoot;
        }
        void Error(Exception ex) {
            Log("错误："+ex);var handler=HostErrorReported;if(handler!=null){handler(ex);return;}
            string text="校准未完成，请检查设备连接和采集设置后重试。";
            if(HostTranslateUi!=null)text=HostTranslateUi(text);
            MessageBox.Show(HostDialogOwner ?? this,text,HostTranslateUi==null?"校准验证":HostTranslateUi("校准验证"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }
}
