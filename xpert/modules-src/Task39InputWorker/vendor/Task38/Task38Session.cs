using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml.Serialization;

namespace HVX.OWTS.Task38
{
    public enum ConnectionPhase { Offline, Connecting, Ready, Reconnecting, ReconnectedAwaitingConfirmation, TimedOut, Exited }

    public interface ITask38DeviceFactory
    {
        IArtUsb3135Device CreateArt();
        IPicoScope5242DBlockDevice CreatePico();
    }

    public sealed class Task38HardwareFactory : ITask38DeviceFactory
    {
        public IArtUsb3135Device CreateArt() { return new ArtUsb3135Device(); }
        public IPicoScope5242DBlockDevice CreatePico() { return new PicoScope5242DBlockDevice(); }
    }

    [Serializable]
    public sealed class InterruptedOperation
    {
        public DateTime CapturedUtc { get; set; }
        public string DeviceAndError { get; set; }
        public Task38ControllerState InterruptedState { get; set; }
        public bool WasCycleRunning { get; set; }
        public double LastMeasuredVoltageV { get; set; }
        public double LastAoCommandVolts { get; set; }
        public double LastCurrentA { get; set; }
        public Task38Configuration Configuration { get; set; }
        public InterruptedOperation Clone()
        {
            InterruptedOperation c = (InterruptedOperation)MemberwiseClone();
            c.Configuration = Configuration == null ? null : Configuration.Clone();
            return c;
        }
    }

    public sealed class ConnectionSnapshot
    {
        public bool ControlBoxVerification { get; internal set; }
        public string ArtSerial { get; internal set; }
        public string PicoSerial { get; internal set; }
        public string IdentityDiagnostic { get; internal set; }
        public ConnectionPhase Phase { get; internal set; }
        public int ProgressPercent { get; internal set; }
        public double ElapsedSeconds { get; internal set; }
        public string Message { get; internal set; }
        public string LastError { get; internal set; }
        public bool WorkerBusy { get; internal set; }
        public bool DischargeVerified { get; internal set; }
        public bool CanRetry { get; internal set; }
        public bool CanContinue { get; internal set; }
        public InterruptedOperation Interrupted { get; internal set; }
    }

    // Owns complete device generations. A late native Open/Close can NEVER publish a
    // Ready state after its ten-second window or overlap a newer device generation.
    public sealed class Task38Session : IDisposable
    {
        public const int ReconnectTimeoutMilliseconds = 10000;
        private readonly object gate = new object();
        private readonly ITask38DeviceFactory factory;
        private readonly string checkpointPath;
        private readonly Thread monitor;
        private volatile bool exited;
        private ConnectionPhase phase = ConnectionPhase.Offline;
        private string message = "尚未连接设备";
        private string lastError = String.Empty;
        private string pinnedPicoSerial = String.Empty;
        private string pinnedArtSerial = String.Empty;
        private string identityDiagnostic = String.Empty;
        private Stopwatch connectionClock;
        private bool busy;
        private bool cleanupRunning, cleanupFinished;
        private string cleanupError = String.Empty;
        private bool dischargeVerified;
        private int generation;
        private Task38Configuration config;
        private Task38Controller controller;
        private ObservedArt observedArt;
        private ObservedPico observedPico;
        private InterruptedOperation interrupted;

        public Task38Session(ITask38DeviceFactory deviceFactory, string recoveryCheckpointPath)
        {
            if (deviceFactory == null) throw new ArgumentNullException("deviceFactory");
            factory = deviceFactory;
            checkpointPath = recoveryCheckpointPath;
            monitor = new Thread(MonitorConnection) { IsBackground = true, Name = "Task38-ConnectionWatch" };
            monitor.Start();
        }

        public Task38Controller Controller { get { lock (gate) return controller; } }
        public bool ReleaseFinished { get { lock (gate) return cleanupFinished; } }
        public string ReleaseError { get { lock (gate) return cleanupError; } }

        public void Connect(Task38Configuration requested)
        {
            if (requested == null) throw new ArgumentNullException("requested");
            string error = requested.Validate();
            if (error.Length != 0) throw new ArgumentException(error);
            lock (gate)
            {
                if (exited || busy || (phase != ConnectionPhase.Offline && phase != ConnectionPhase.TimedOut))
                    throw new InvalidOperationException("当前连接状态不允许重新初始化。");
                config = requested.Clone();
                config.GroundInSafeIdle = true;
                StartConnectionLocked(false);
            }
        }

        public ConnectionSnapshot GetConnectionStatus()
        {
            lock (gate)
            {
                EnforceDeadlineLocked();
                double elapsed = connectionClock == null ? 0.0 : Math.Min(10.0, connectionClock.Elapsed.TotalSeconds);
                return new ConnectionSnapshot
                {
                    Phase = phase, ElapsedSeconds = elapsed, ControlBoxVerification = config != null && config.ControlBoxVerification,
                    ArtSerial = pinnedArtSerial, PicoSerial = pinnedPicoSerial, IdentityDiagnostic = identityDiagnostic,
                    ProgressPercent = phase == ConnectionPhase.Ready || phase == ConnectionPhase.ReconnectedAwaitingConfirmation
                        ? 100 : (int)Math.Min(100, elapsed * 10),
                    Message = message, LastError = lastError, WorkerBusy = busy,
                    DischargeVerified = dischargeVerified,
                    CanRetry = phase == ConnectionPhase.TimedOut && !busy && !exited,
                    CanContinue = phase == ConnectionPhase.ReconnectedAwaitingConfirmation && (dischargeVerified || config.ControlBoxVerification) && !busy,
                    Interrupted = interrupted == null ? null : interrupted.Clone()
                };
            }
        }

        public bool RetryConnection()
        {
            lock (gate)
            {
                EnforceDeadlineLocked();
                if (exited || busy || phase != ConnectionPhase.TimedOut) return false;
                StartConnectionLocked(true);
                return true;
            }
        }

        public bool ConfirmContinue()
        {
            lock (gate)
            {
                if (phase != ConnectionPhase.ReconnectedAwaitingConfirmation || busy || (!dischargeVerified && !config.ControlBoxVerification) || exited)
                    return false;
                Task38StatusSnapshot s = controller.GetStatus();
                if (s.State == Task38ControllerState.Faulted || (!config.ControlBoxVerification && (!s.SafetyReady || !s.StartAllowed))) return false;
                phase = ConnectionPhase.Ready;
                message = config.ControlBoxVerification ? "控制箱校验已恢复，禁止加压；反馈未验证，不证明已放电。" : "连接成功，参数已恢复；可继续当前测试。下一轮从 AO=0 开始步进加压。";
                return true;
            }
        }

        public bool StartCycle()
        { return StartCycle(String.Empty); }

        public bool StartCycle(string recordId)
        {
            lock (gate)
            {
                if (phase != ConnectionPhase.Ready || busy || controller == null || exited) return false;
                return controller.StartCycle(recordId);
            }
        }

        public void ApplyConfiguration(Task38Configuration requested)
        {
            string error = requested.Validate();
            if (error.Length != 0) throw new ArgumentException(error);
            lock (gate)
            {
                if (exited || busy || phase != ConnectionPhase.Ready || controller.GetStatus().CycleRunning)
                    throw new InvalidOperationException("仅允许空闲连接状态下应用设置。");
                if (requested.ControlBoxVerification != config.ControlBoxVerification)
                    throw new InvalidOperationException("改变校验状态前必须退出并释放设备，再重新连接。");
                config = requested.Clone();
                config.GroundInSafeIdle = true;
                observedArt.Inhibit = true;
                StartConnectionLocked(false);
            }
        }

        public void RequestStop() { Task38Controller c = Controller; if (c != null) c.RequestStop(); }

        private void Fault(int ownerGeneration, string device, Exception error)
        {
            lock (gate)
            {
                if (exited || ownerGeneration != generation || (phase != ConnectionPhase.Ready && phase != ConnectionPhase.ReconnectedAwaitingConfirmation)) return;
                // This executes inside the failing I/O call, before shutdown can zero the live status.
                Task38StatusSnapshot s = controller.GetStatus();
                config = controller.GetConfigurationSnapshot();
                interrupted = new InterruptedOperation
                {
                    CapturedUtc = DateTime.UtcNow, DeviceAndError = device + "：" + error.Message,
                    InterruptedState = s.State, WasCycleRunning = s.CycleRunning,
                    LastMeasuredVoltageV = s.ActualVoltageV, LastAoCommandVolts = s.AoCommandVolts,
                    LastCurrentA = s.CurrentA, Configuration = config.Clone()
                };
                observedArt.Inhibit = true;
                controller.RequestStop();
                lastError = interrupted.DeviceAndError;
                StartConnectionLocked(true);
            }
        }

        private void StartConnectionLocked(bool recovery)
        {
            busy = true;
            dischargeVerified = false;
            phase = recovery ? ConnectionPhase.Reconnecting : ConnectionPhase.Connecting;
            message = recovery ? "硬件通讯中断，正在重新连接。未确认放电，禁止靠近设备！" : "正在连接 USB3135 与 PicoScope";
            connectionClock = Stopwatch.StartNew();
            int token = ++generation;
            Thread worker = new Thread(() => ConnectionWorker(token, recovery))
            { IsBackground = true, Name = "Task38-Reconnect" };
            worker.Start();
        }

        private bool IsCurrent(int token)
        {
            lock (gate)
            {
                EnforceDeadlineLocked();
                return !exited && generation == token &&
                    (phase == ConnectionPhase.Connecting || phase == ConnectionPhase.Reconnecting);
            }
        }

        private void EnforceDeadlineLocked()
        {
            if ((phase == ConnectionPhase.Connecting || phase == ConnectionPhase.Reconnecting) &&
                connectionClock != null && connectionClock.ElapsedMilliseconds >= ReconnectTimeoutMilliseconds)
            {
                phase = ConnectionPhase.TimedOut;
                lastError = "截止步骤：" + message + (String.IsNullOrEmpty(lastError) ? "" : "\n" + lastError);
                message = "10 秒内未完成初始化及安全就绪检查。请查看下方步骤与诊断；可能是驱动调用失败，也可能是联锁或残压条件未满足。未确认放电，禁止靠近设备！";
                dischargeVerified = false;
                if (observedArt != null) observedArt.Inhibit = true;
            }
        }

        private void ConnectionWorker(int token, bool recovery)
        {
            try
            {
                if (recovery) SaveCheckpoint();
                // Fully quiesce old workers before replacing native task handles. If native code
                // hangs, the UI deadline still expires, and Retry remains disabled until it returns.
                if (controller != null) controller.Dispose();
                if (observedArt != null) observedArt.Dispose();
                if (observedPico != null) observedPico.Dispose();
                lock (gate) { controller = null; observedArt = null; observedPico = null; }
                while (IsCurrent(token))
                {
                    Task38Controller candidate = null;
                    ObservedArt a = null;
                    ObservedPico p = null;
                    bool accepted = false;
                    try
                    {
                        a = new ObservedArt(factory.CreateArt(), ex => Fault(token, "USB3135", ex));
                        p = new ObservedPico(factory.CreatePico(), ex => Fault(token, "PicoScope", ex));
                        candidate = new Task38Controller(a, p);
                        Task38Configuration attempt = config.Clone();
                        if (!String.IsNullOrEmpty(pinnedPicoSerial)) attempt.PicoSerial = pinnedPicoSerial;
                        attempt.GroundInSafeIdle = true;
                        candidate.Initialize(attempt, step => { lock (gate) if (IsCurrent(token)) message = step; });
                        string artSerial = a.DeviceSerial;
                        if (!String.IsNullOrEmpty(pinnedArtSerial) && artSerial != pinnedArtSerial)
                            throw new InvalidOperationException("控制模块序列号不匹配或无法复核，禁止接管其他设备。");
                        string serial = p.CheckConnection();
                        if (!String.IsNullOrEmpty(serial))
                        {
                            if (!String.IsNullOrEmpty(pinnedPicoSerial) && serial != pinnedPicoSerial)
                                throw new InvalidOperationException("PicoScope 序列号不匹配，禁止接管其他设备。");
                            pinnedPicoSerial = serial;
                            config.PicoSerial = serial;
                        }
                        a.ForceSourceOff();
                        a.SetDigitalOutputs(false, false, true, true);
                        int stableSafeReads = 0;
                        while (IsCurrent(token) && stableSafeReads < 3)
                        {
                            var controllerStatus = candidate.GetStatus();
                            if (controllerStatus.State == Task38ControllerState.Faulted)
                                throw new InvalidOperationException(controllerStatus.LastError);
                            FeedbackSample f = a.ReadFeedback();
                            bool interlock = a.ReadSafetyReady();
                            double threshold = config.TargetVoltageV == 0 ? config.EffectiveZeroVoltageThresholdV : config.ResidualVoltageSafeThresholdV;
                            bool safe = (config.ControlBoxVerification || interlock) && Finite(f.DividerVoltageV) &&
                                Finite(f.SourceVoltageV) && Finite(f.CurrentA) &&
                                Finite(f.DividerFeedbackRawV) && Finite(f.SourceVoltageFeedbackRawV) && Finite(f.CurrentFeedbackRawV) &&
                                (config.ControlBoxVerification || (Math.Abs(f.DividerVoltageV) <= threshold &&
                                Math.Abs(f.SourceVoltageV) <= threshold));
                            stableSafeReads = safe ? stableSafeReads + 1 : 0;
                            lock (gate) if (IsCurrent(token)) message = config.ControlBoxVerification
                                ? "控制箱校验：正在确认两卡与输入读取；未验证外部反馈回路，禁止加压。" : safe
                                ? "双设备已连接，正在确认安全联锁和残压……"
                                : String.Format("两卡已初始化，等待安全条件：联锁{0}，分压器{1:F2} V，电源反馈{2:F2} V，阈值{3:F2} V。未确认放电，禁止靠近设备！", interlock ? "有效" : "未就绪", f.DividerVoltageV, f.SourceVoltageV, threshold);
                            Thread.Sleep(50);
                        }
                        if (stableSafeReads == 3 && IsCurrent(token))
                        {
                            lock (gate)
                            {
                                if (IsCurrent(token) && candidate.GetStatus().State != Task38ControllerState.Faulted &&
                                    (config.ControlBoxVerification || candidate.GetStatus().StartAllowed))
                                {
                                    controller = candidate; observedArt = a; observedPico = p;
                                    pinnedArtSerial = artSerial; identityDiagnostic = a.IdentityDiagnostic;
                                    phase = recovery ? ConnectionPhase.ReconnectedAwaitingConfirmation : ConnectionPhase.Ready;
                                    dischargeVerified = !config.ControlBoxVerification;
                                    message = config.ControlBoxVerification ? "控制箱校验：两卡初始化完成；反馈仅为原始输入，禁止加压，不证明已放电。" : recovery ? "重新连接成功，联锁与残压检查通过。参数已恢复，请确认继续当前操作。"
                                        : "双设备连接成功，可以开始测试";
                                    lastError = String.Empty;
                                    connectionClock.Stop();
                                    accepted = true;
                                }
                            }
                        }
                        if (accepted) return;
                    }
                    catch (Exception ex) { lock (gate) lastError = ex.ToString(); }
                    finally
                    {
                        if (!accepted)
                        {
                            if (candidate != null) candidate.Dispose();
                            // Retain a failed cleanup owner rather than opening another generation.
                            try { if (a != null) a.Dispose(); if (p != null) p.Dispose(); }
                            catch
                            {
                                lock (gate) { controller = candidate; observedArt = a; observedPico = p; }
                                throw;
                            }
                        }
                    }
                    Thread.Sleep(250);
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    lastError = ex.Message;
                    if (!exited)
                    {
                        phase = ConnectionPhase.TimedOut;
                        message = "连接或资源清理失败。请先放电并检查通讯线，再重试或退出加压测试。未确认放电，禁止靠近设备！";
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    if (generation == token) busy = false;
                    Monitor.PulseAll(gate);
                }
                if (exited) Cleanup();
            }
        }

        private void MonitorConnection()
        {
            while (!exited)
            {
                ObservedPico p;
                lock (gate)
                {
                    EnforceDeadlineLocked();
                    p = phase == ConnectionPhase.Ready || phase == ConnectionPhase.ReconnectedAwaitingConfirmation
                        ? observedPico : null;
                }
                // Native probing occurs off the UI thread and off the 50 ms ART safety loop.
                if (p != null) try { p.CheckConnection(); } catch { }
                Thread.Sleep(500);
            }
        }

        private void SaveCheckpoint()
        {
            InterruptedOperation saved;
            lock (gate) saved = interrupted == null ? null : interrupted.Clone();
            if (saved == null || String.IsNullOrEmpty(checkpointPath)) return;
            try
            {
                string full = Path.GetFullPath(checkpointPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                string temp = full + ".pending";
                using (FileStream stream = File.Create(temp))
                    new XmlSerializer(typeof(InterruptedOperation)).Serialize(stream, saved);
                if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            }
            catch (Exception ex) { lock (gate) lastError += "；断点落盘失败（内存仍保留）：" + ex.Message; }
        }

        public static InterruptedOperation LoadCheckpoint(string path)
        {
            using (FileStream stream = File.OpenRead(path))
                return (InterruptedOperation)new XmlSerializer(typeof(InterruptedOperation)).Deserialize(stream);
        }

        public void ExitTest()
        {
            lock (gate)
            {
                if (exited && (!cleanupFinished || cleanupError.Length == 0)) return;
                cleanupFinished = false;
                cleanupError = String.Empty;
                exited = true;
                phase = ConnectionPhase.Exited;
                message = "已退出加压测试；退出软件不代表完成放电。未确认放电，禁止靠近设备！";
                if (observedArt != null) observedArt.Inhibit = true;
                if (controller != null) controller.RequestStop();
                if (busy) return;
            }
            new Thread(Cleanup) { IsBackground = true, Name = "Task38-ExitCleanup" }.Start();
        }

        private void Cleanup()
        {
            lock (gate)
            {
                if (cleanupRunning || cleanupFinished) return;
                cleanupRunning = true;
            }
            string error = String.Empty;
            try { if (controller != null) controller.Dispose(); } catch (Exception ex) { error += "控制器：" + ex.Message + "；"; }
            try { if (observedArt != null) observedArt.Dispose(); } catch (Exception ex) { error += "ART：" + ex.Message + "；"; }
            try { if (observedPico != null) observedPico.Dispose(); } catch (Exception ex) { error += "Pico：" + ex.Message + "；"; }
            if (monitor != Thread.CurrentThread) monitor.Join();
            lock (gate)
            {
                cleanupError = error;
                cleanupRunning = false;
                cleanupFinished = true;
                Monitor.PulseAll(gate);
            }
        }

        public void Dispose() { ExitTest(); }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
    }

    internal sealed class ObservedArt : IArtUsb3135Device, IDeviceIdentity
    {
        private readonly IArtUsb3135Device inner;
        private readonly Action<Exception> fault;
        internal volatile bool Inhibit;
        internal ObservedArt(IArtUsb3135Device device, Action<Exception> onFault) { inner = device; fault = onFault; }
        public string DeviceSerial { get { IDeviceIdentity identity = inner as IDeviceIdentity; return identity == null ? String.Empty : identity.DeviceSerial; } }
        public string IdentityDiagnostic { get { IDeviceIdentity identity = inner as IDeviceIdentity; return identity == null ? "控制模块没有提供序列号查询接口。" : identity.IdentityDiagnostic; } }
        private T Call<T>(Func<T> f) { try { return f(); } catch (Exception e) { fault(e); throw; } }
        private void Call(Action f) { Call(() => { f(); return true; }); }
        public void Initialize(Task38Configuration c) { inner.Initialize(c); }
        public bool ReadSafetyReady() { return Call(() => inner.ReadSafetyReady()); }
        public FeedbackSample ReadFeedback() { return Call(() => inner.ReadFeedback()); }
        public void WriteAoVolts(double v)
        {
            if (Inhibit && v > 0) throw new InvalidOperationException("通讯中断，已禁止升压输出。");
            Call(() => inner.WriteAoVolts(v));
        }
        public void SetDigitalOutputs(bool h, bool l, bool c, bool g)
        {
            if (Inhibit && (h || !g)) throw new InvalidOperationException("通讯中断，禁止恢复高压或撤除接地。");
            Call(() => inner.SetDigitalOutputs(h, l, c, g));
        }
        public void ForceSourceOff() { Call(() => inner.ForceSourceOff()); }
        public void StopMonitoringInputs() { Call(() => inner.StopMonitoringInputs()); }
        public void RestartMonitoringInputs() { Call(() => inner.RestartMonitoringInputs()); }
        public void ArmFiniteDividerCapture() { Call(() => inner.ArmFiniteDividerCapture()); }
        public double[] ReadFiniteDividerCapture(int t)
        {
            try { return inner.ReadFiniteDividerCapture(t); }
            catch (TimeoutException) { throw; }
            catch (Exception e) { fault(e); throw; }
        }
        public void AbortFiniteDividerCapture() { inner.AbortFiniteDividerCapture(); }
        public bool FiniteCaptureArmed { get { return inner.FiniteCaptureArmed; } }
        public void Dispose() { inner.Dispose(); }
    }

    internal sealed class ObservedPico : IPicoScope5242DBlockDevice
    {
        private readonly IPicoScope5242DBlockDevice inner;
        private readonly Action<Exception> fault;
        internal ObservedPico(IPicoScope5242DBlockDevice device, Action<Exception> onFault) { inner = device; fault = onFault; }
        private T Call<T>(Func<T> f) { try { return f(); } catch (Exception e) { fault(e); throw; } }
        public string CheckConnection()
        {
            return Call(() => { IDeviceConnectionProbe p = inner as IDeviceConnectionProbe; return p == null ? String.Empty : p.CheckConnection(); });
        }
        public void Initialize(Task38Configuration c) { inner.Initialize(c); }
        public void ArmBlockCapture(int r) { Call(() => { inner.ArmBlockCapture(r); return true; }); }
        public PicoBlockCapture ReadBlockCapture(int t)
        {
            try { return inner.ReadBlockCapture(t); }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) { CheckConnection(); throw; }
            catch (Exception e) { fault(e); throw; }
        }
        public void AbortBlockCapture() { inner.AbortBlockCapture(); }
        public bool BlockCaptureArmed { get { return inner.BlockCaptureArmed; } }
        public void Dispose() { inner.Dispose(); }
    }
}
