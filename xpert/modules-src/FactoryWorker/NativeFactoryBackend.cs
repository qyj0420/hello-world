using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HVX.OWTS.Task38;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // The runner exclusively owns AI/AO/DO and cleanup. A separate monitor
    // waits >=10 ms after each return and exclusively reads its P0.4 DI task.
    // An independent managed 500 ms observer only latches faults.
    // No port-wide write or P0.2 high output. R30's explicit pressure-timeout
    // supplement is bounded by PrepareOpticalRetry and never clears faulted.
    internal sealed partial class NativeFactoryBackend : IFactoryBackend, IFactoryFaultMonitor, IFactoryOpticalRecovery, IFactoryFinalAi4Observation, IFactoryResidentZeroInputSession
    {
        internal const double DriverIoTimeoutSeconds = 0.020;
        private const int Rse = 10083, Volts = 10348, Rising = 10280, FiniteSamples = 10178;
        private sealed class OwnedTask
        {
            internal IntPtr Handle;
            internal bool Configured, Started;
            internal readonly string Name;
            internal OwnedTask(string name) { Name = name; }
        }
        private readonly OwnedTask hv = new OwnedTask("P0.0 HV ON/OFF 单线");
        private readonly OwnedTask currentMode = new OwnedTask("P0.2 过流停机模式单线");
        private readonly OwnedTask optical = new OwnedTask("P0.7 晶闸管光触发单线");
        private readonly OwnedTask indicator = new OwnedTask("P0.3 加压指示灯单线（仅手动入口）");
        private readonly OwnedTask ao = new OwnedTask("AO0 VSET");
        private readonly OwnedTask emergency = new OwnedTask("P0.4 急停反馈输入");
        private readonly OwnedTask monitor = new OwnedTask("AI0/AI2/AI4 RSE 监测");
        private readonly OwnedTask finite = new OwnedTask("AI4 80 kS/s 有限采集");
        private readonly PicoInputCapture pico = new PicoInputCapture();
        private readonly IFactoryArtTaskIo taskIo;
        private Exception cleanupFailure;
        private volatile FactoryIoTimingDiagnostics ioTiming = new FactoryIoTimingDiagnostics();
        private readonly FactoryArtCommunicationEvidence artCommunication = new FactoryArtCommunicationEvidence();
        internal FactoryIoTimingDiagnostics IoTiming { get { return ioTiming; } }
        internal NativeFactoryBackend() : this(new FactoryArtTaskIo()) { }
        internal NativeFactoryBackend(IFactoryArtTaskIo taskIo)
        {
            this.taskIo = taskIo ?? throw new ArgumentNullException("taskIo");
            pico.TimingScope = operation => IoTiming.BeginOperation(operation);
        }
        private FactorySettings settings;
        private FactorySettings sessionIdentity;
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private bool initialized, initializing, disposed, calibration, identitiesVerified;
        private bool roundActive;
        private FactoryHealthyRoundStopFacts healthyRoundStopFacts;
        internal FactoryHealthyRoundStopFacts HealthyRoundStopFacts {get{return healthyRoundStopFacts;}}
        internal void ResetHealthyRoundStopEvidence(){healthyRoundStopFacts=null;}
        private volatile bool faulted;
        private bool hvOn, opticalOn, opticalAttempted, finiteArmed;
        private bool opticalRetryUsed;
        private bool opticalRetryStopped;
        private bool requireFreshWindowAfterAo;
        private readonly FactoryFeedbackStartupWindow feedbackStartup = new FactoryFeedbackStartupWindow();
        private Stopwatch monitorStartClock;
        private double lastAo, actualArtRate;
        private string artRuntime, artSerial;
        private FactoryArtFeedbackPlan feedbackPlan;
        private FactoryEmergencyMonitor emergencyMonitor;
        private FactoryEmergencySnapshotPublisher emergencyPublisher;
        private Exception emergencyPublisherFailure;
        private readonly object connectionSnapshotGate=new object();
        private Exception emergencyDisplayFailure;
        private readonly FactoryIdleDiRebuildPolicy idleDiRebuild = new FactoryIdleDiRebuildPolicy();
        private int emergencyTaskGeneration;
        private readonly FactoryHvCommandState hvCommands = new FactoryHvCommandState();
        // Diagnostic taps run on the existing AI owner thread. They never read
        // the SDK or write outputs. Raw batches are retained BEFORE validation.
        internal Action<double[],int> RawFeedbackRead;
        internal Action<bool> MonitorChanged;
        internal volatile Action<string,string> ConnectionReport;
        internal string SessionId { get { return sessionId; } }
        internal bool SessionOpen { get { return initialized && !disposed && pico.ConnectionOpen; } }
        internal bool RoundActive { get { return roundActive; } }
        internal bool Faulted { get { return faulted; } }
        internal bool EmergencyOutputLatched { get { return emergencyMonitor!=null && emergencyMonitor.OutputStopLatched; } }
        internal bool CanReuseSession { get { return SessionOpen && !roundActive && !faulted && !EmergencyOutputLatched; } }
        // LOW permanently withdraws this session's output capability, not the
        // healthy DI task. No new round may use these handles after the latch.
        internal bool CanRetainEmergencyDisplaySession
        {
            get
            {
                if(!SessionOpen || roundActive || faulted || !EmergencyOutputLatched || emergencyMonitor.HasReadFailure ||
                    emergencyPublisherFailure!=null || cleanupFailure!=null || healthyRoundStopFacts==null || !healthyRoundStopFacts.Complete ||
                    hvOn || hvCommands.MayBeOn || opticalOn || lastAo!=0 || finiteArmed || pico.Armed ||
                    finite.Handle!=IntPtr.Zero || monitor.Handle!=IntPtr.Zero)return false;
                string detail;DateTime sample;string state=emergencyMonitor.ConnectionState(out detail,out sample);
                return state=="LATCHED_LOW" || state=="LATCHED_HIGH";
            }
        }
        private DateTime picoCheckedAt=DateTime.MinValue;
        private bool EmergencyDisplayOnly
        {get{return sessionIdentity!=null&&sessionIdentity.ManualZeroInputWithoutEmergency;}}
        private void CheckPicoConnection()
        {
            if(DateTime.UtcNow<picoCheckedAt || !pico.ConnectionOpen)return;
            picoCheckedAt=DateTime.UtcNow.AddMilliseconds(500);pico.CheckTransport();
        }
        internal void ReportConnectionSnapshot()
        {
            // Serializes only managed snapshots/queue callbacks, never SDK or
            // file/UI work. The sole DI reader is independent of this gate.
            lock(connectionSnapshotGate)ReportConnectionSnapshotCore();
        }
        private void ReportConnectionSnapshotCore()
        {
            var mon=emergencyMonitor;var report=ConnectionReport;
            if(report==null)return;
            if(mon==null)
            {
                if(EmergencyDisplayOnly && emergencyDisplayFailure!=null)
                    report("LINK_ART_UNKNOWN",FactoryConnectionWatch.Stamp("P0.4只读显示未建立；人工0 kV不以此作采集许可。"+emergencyDisplayFailure+
                        "; SessionId="+sessionId+"; DiTaskGeneration="+emergencyTaskGeneration));
                return;
            }
            mon.DrainRetryEvents((key,value)=>report(key,FactoryConnectionWatch.Stamp(value)));
            string detail;DateTime sampleUtc;string state=mon.ConnectionState(out detail,out sampleUtc);
            // Manual input owns this DI solely for real status display. Its
            // failure is UNKNOWN, not a fault/output permission for this input
            // capability. A separate supervised session still requires fresh DI.
            if(EmergencyDisplayOnly && state=="ERROR")state="UNKNOWN";
            if(state!="WAIT")report("LINK_ART_"+state,
                ((state=="HIGH"||state=="LOW"||state=="LATCHED_HIGH"||state=="LATCHED_LOW")?sampleUtc:DateTime.UtcNow).ToString("O",CultureInfo.InvariantCulture)+"|"+
                detail+"; SessionId="+sessionId+"; DiTaskGeneration="+emergencyTaskGeneration);
        }

        internal bool DiagnosticMonitorAvailable { get { return initialized && !disposed && monitor.Started && monitor.Handle != IntPtr.Zero && finite.Handle == IntPtr.Zero; } }
        internal void ReadShutdownDiagnostic()
        {
            // Diagnostic-only drain AFTER disable calls. A latched low P0.4 must
            // not prevent saving existing input samples; it still forbids outputs.
            EnsureInitialized();
            if(settings.SkipPressureResidualMeasurement)
                throw new InvalidOperationException("加压关断后自动残压诊断已禁用；没有读取AI，不代表零电压或已放电。");
            if (hvOn || opticalOn || lastAo != 0 || !DiagnosticMonitorAvailable)
                throw new InvalidOperationException("只读关断诊断需要关断调用完成且三路监测任务仍在运行。");
            ReadFaultFeedbackCore();
        }
        private bool faultMonitoring;
        public void PrepareFaultMonitoring()
        {
            EnsureInitialized();
            EnsureRoundActive();
            if(settings.SkipPressureResidualMeasurement)
                throw new InvalidOperationException("故障后自动残压监测已禁用；本轮仍失败，须现场独立安全放电。");
            faulted = true; // remains latched for the rest of this session
            faultMonitoring = true;
            if (!DiagnosticMonitorAvailable) StopCapture(); // AI/Pico stop only; no optical edge or output recreation
            if (!DiagnosticMonitorAvailable) throw new InvalidOperationException("故障后ART监测任务不可用；残压未知，不重连输出。");
        }
        public FactoryFeedback ReadFaultFeedback()
        {
            EnsureInitialized();
            if(settings.SkipPressureResidualMeasurement)
                throw new InvalidOperationException("故障后自动残压读取已禁用；没有测得电压。");
            if (!faultMonitoring || !DiagnosticMonitorAvailable) throw new InvalidOperationException("故障只读监测未建立；残压未知。");
            return ReadFaultFeedbackCore();
        }
        private FactoryFeedback ReadFaultFeedbackCore()
        {
            // Available=0 is not evidence of a zero input. Request at least one
            // sample with the existing bounded SDK timeout, then drain/validate.
            var elapsed = Stopwatch.StartNew();
            return FactoryFeedbackStream.Read(feedbackPlan,new MonitorBuffer(this),lastAo,() => elapsed.ElapsedMilliseconds,false,false,FeedbackReadCancellation);
        }

        public void Initialize(FactorySettings requested, bool calibrationRun)
        {
            if(requested.AutomaticBatch && !SessionOpen)
                throw new InvalidOperationException("自动批次只能在已建立并持有本批次许可的常驻会话中启动。");
            // Legacy one-round callers still own Dispose. Resident callers open
            // once, call this/BeginRound for each round and EndRound afterwards.
            if (!SessionOpen)
            {
                var first = FactoryNativeSessionPolicy.PrepareRound(requested,calibrationRun);
                OpenSession(first);
                BeginRound(first,calibrationRun);
            }
            else BeginRound(requested,calibrationRun);
        }

        internal void OpenSession(FactorySettings requested)
        {
            if (initialized || initializing || disposed || AnyTaskOwned() || pico.ConnectionOpen)
                throw new InvalidOperationException("设备会话不能重叠打开或复用未释放的任务。");
            // Opening an idle session does not request an output permission:
            // only exact identities, the Pico connection and P0.4 DI are owned.
            settings = FactoryNativeSessionPolicy.PrepareOpen(requested);
            sessionIdentity = settings.Clone();
            pico.CommunicationError=detail=>{faulted=true;if(ConnectionReport!=null)ConnectionReport("LINK_PICO_ERROR",FactoryConnectionWatch.Stamp("Pico驱动/读写异常："+detail));};
            pico.CommunicationOk=()=>{if(ConnectionReport!=null)ConnectionReport("LINK_PICO_ONLINE",FactoryConnectionWatch.Stamp("Pico原连接通信轮询返回"));};
            pico.CommunicationBusy=detail=>{if(ConnectionReport!=null)ConnectionReport("LINK_PICO_BUSY",FactoryConnectionWatch.Stamp(detail));};
            initializing = true;
            Exception failure = null;
            try
            {
                artRuntime = InputOnlyArtNative.Load(settings.Input.ArtLibraryDirectory);
                VerifyArtIdentity();
                // Establish the sole read-only DI owner before a potentially
                // slow Pico open. This reports input state, NOT a completed
                // two-card session or permission to create any output task.
                StartEmergencyInput(); // also manual zero: display only, never a capture/output permit
                ReportConnectionSnapshot();
                StartEmergencyPublisher();
                // The exact Pico must still succeed BEFORE any ART AO/DO task.
                pico.Initialize(settings.Input, false);
                if (!String.Equals(pico.Serial, settings.Input.PicoSerial.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Pico 身份复核失败；未创建任何 ART 输出任务。");
                VerifyArtIdentity(); // Pico Open may take time; recheck DI identity.
                identitiesVerified = true;
                initialized = true;
                RebuildIdleEmergencyTaskIfRequired();
                // Idle LOW remains a live input status, not a connection fault.
                // BeginRound still requires a fresh HIGH in the active epoch.
                ReportConnectionSnapshot();
            }
            catch (Exception ex) { faulted = true; failure = ex; }
            finally { initializing = false; }
            if (failure != null)
            {
                var failures = new List<Exception> { failure };
                Try(Shutdown, failures);
                Try(Dispose, failures);
                throw new AggregateException("设备只读会话打开失败；所有可用清理路径均已尝试，失败详情保留。", failures);
            }
        }

        private FactoryEmergencyMonitor CreateEmergencyMonitor(Func<int,bool> read)
        {
            FactoryEmergencyMonitor owned=null;
            owned=new FactoryEmergencyMonitor(read,()=>{
                // The monitor itself irrevocably latches active LOW. Only a
                // read/deadline failure invalidates the communication owner.
                if(!EmergencyDisplayOnly && (owned==null || owned.HasReadFailure))faulted=true;
            },artCommunication.Snapshot);
            return owned;
        }
        private void StartEmergencyPublisher()
        {
            if(emergencyPublisher!=null)throw new InvalidOperationException("P0.4状态发布已有唯一所有者。");
            emergencyPublisher=new FactoryEmergencySnapshotPublisher(50,"P0.4 managed snapshot publication",
                ReportConnectionSnapshot,error=>{emergencyPublisherFailure=error;faulted=true;});
            emergencyPublisher.Start();
        }
        private void StopEmergencyPublisher()
        {
            var owned=emergencyPublisher;
            if(owned==null)return;
            owned.Dispose(); // A failed join retains the field and ownership.
            emergencyPublisher=null;
            if(owned.Failure!=null)throw new InvalidOperationException("P0.4状态发布失败；未恢复输出许可。",owned.Failure);
        }
        private void StartEmergencyInput()
        {
            bool displayOnly=EmergencyDisplayOnly;
            try
            {
                if(emergency.Handle!=IntPtr.Zero||emergencyMonitor!=null)
                    throw new InvalidOperationException("P0.4已有唯一所有者，不能重叠建立读取。");
                // Diagnostic DI setup must not latch the independent AI/Pico
                // capture when manual zero explicitly has no emergency permit.
                // Every returned handle is still retained for actual cleanup.
                Action<int,string> check=displayOnly?(Action<int,string>)taskIo.Check:Check;
                IntPtr created;int code=InputOnlyArtNative.CreateTask(String.Empty,out created);
                emergency.Handle=created;check(code,"创建 "+emergency.Name);
                if(created==IntPtr.Zero)throw new InvalidOperationException("P0.4未返回有效任务句柄。");
                ++emergencyTaskGeneration;
                check(InputOnlyArtNative.CreateDi(created,Physical("port0/line4"),String.Empty,0),"配置 P0.4 单线急停输入");
                emergency.Configured=true;emergency.Started=true;
                check(InputOnlyArtNative.StartTask(created),"启动 "+emergency.Name);
                emergencyMonitor=CreateEmergencyMonitor(ReadEmergencyCore);
                if(!displayOnly)emergencyMonitor.AllowOneIdleRebuildDeferral();
                emergencyMonitor.EnableIdleTimeoutRetry();
                emergencyMonitor.Start();
            }
            catch(Exception error)
            {
                if(!displayOnly)throw;
                emergencyDisplayFailure=error;
                if(ConnectionReport!=null)ConnectionReport("P04_DI_DISPLAY_ERROR",FactoryConnectionWatch.Stamp(
                    "人工0 kV只读P0.4显示失败；保留未知状态及资源所有权，不授予输出，不改变人工输入许可。"+error));
            }
        }

        internal void BeginRound(FactorySettings requested, bool calibrationRun)
        {
            ResetHealthyRoundStopEvidence();
            var next = FactoryNativeSessionPolicy.PrepareRound(requested,calibrationRun);
            EnsureInitialized();
            if (roundActive || initializing) throw new InvalidOperationException("上一轮尚未结束，不能重叠开始新轮。");
            if (!FactoryNativeSessionPolicy.SameDeviceSession(sessionIdentity,next))
                throw new InvalidOperationException("本轮设备身份或运行库与常驻会话不符；须先结束原会话，不能换用旧句柄。");
            if (EmergencyOutputLatched) throw new FactoryEmergencyStopException();
            if (faulted) throw new InvalidOperationException("设备会话故障已锁存，不能复用开始新轮。");
            ReleaseRetainedZeroAi(); // mode change only; never closes Pico or the sole DI reader
            if (hvOn || hvCommands.MayBeOn || opticalOn || lastAo != 0 || finiteArmed || pico.Armed ||
                finite.Handle != IntPtr.Zero || monitor.Handle != IntPtr.Zero)
                Fail("上一轮输出或采集资源尚未结束，禁止开始下一轮。");
            if (next.ZeroSynchronizedOptical && (ao.Handle != IntPtr.Zero || hv.Handle != IntPtr.Zero || currentMode.Handle != IntPtr.Zero))
                throw new InvalidOperationException("零电压光诊断不能接管已创建HV/AO的会话；须先关闭原设备会话。");
            if(next.BatchContinuation && emergencyMonitor.IdleLowObserved)
                throw new InvalidOperationException("轮间急停已动作，不能自动续轮；请结束本轮并重新开始。");
            AcceptBatchPermit(next);
            // Atomic with the DI monitor's retry eligibility. A pending retry
            // cannot become this round's old-HIGH permission; no output exists
            // yet, and a rejected request does not automatically run later.
            emergencyMonitor.EnterActiveReadMode(next.BatchContinuation);
            // The DI owner keeps a local reference for a complete read, so a
            // late completion from idle/last round cannot enter this new ring.
            ioTiming = new FactoryIoTimingDiagnostics();
            try
            {
                if (!ReadEmergencyReleased()) throw new FactoryEmergencyStopException();
                using(IoTiming.BeginOperation("BeginRound.PicoCheckTransport"))CheckPicoConnection();
                VerifyArtIdentity();
                pico.Configure(next.Input,calibrationRun);
                // Publish a frozen round only after its existing-handle Pico
                // configuration passed. No caller-owned settings are retained.
                settings = next; calibration = calibrationRun;
                opticalAttempted = opticalRetryUsed = opticalRetryStopped = faultMonitoring = requireFreshWindowAfterAo = false;
                roundActive = true;
                EnsurePositiveOutputsAllowed();
                EnsureRoundOutputs();
                ConfigureFinite(calibration);
                ClearTask(finite);
                CreateMonitor(StartMonitorBeforeRamp(settings,calibration));
                if (!ReadEmergencyReleased()) throw new FactoryEmergencyStopException();
                using(IoTiming.BeginOperation("BeginRound.PicoCheckTransport"))CheckPicoConnection();
            }
            catch (Exception failure)
            {
                if (!(failure is FactoryEmergencyStopException)) faulted = true;
                var failures = new List<Exception> { failure };
                Try(EndRound,failures);
                throw new AggregateException("常驻设备本轮准备失败；会话故障已锁存。",failures);
            }
        }

        private void EnsureRoundOutputs()
        {
            if (optical.Handle == IntPtr.Zero)
            { CreateSingleOutput(optical,7); WriteLine(optical,false); }
            if (settings.ZeroSynchronizedOptical) return;
            // Preserve the user's default HV OFF contract: task creation is not
            // a P0.0 write. Only an attempted ON owns a subsequent OFF command.
            if (hv.Handle == IntPtr.Zero) CreateSingleOutput(hv,0);
            if (currentMode.Handle == IntPtr.Zero)
            { CreateSingleOutput(currentMode,2); WriteLine(currentMode,false); }
            if (ao.Handle == IntPtr.Zero)
            {
                CreateTask(ao); ao.Configured = true;
                Check(FactoryArtNative.CreateAo(ao.Handle,Physical("ao0"),String.Empty,0,5,Volts,null),"配置 AO0 0～5 V 硬件量程，软件另限本轮上限");
                WriteAoCore(0);
            }
        }

        internal void EndRound()
        {
            if (!initialized || disposed || (!roundActive && !EmergencyOutputLatched)) return;
            if(zeroInputRound){EndZeroInputRound(zeroInputGeneration);return;}
            var failures = new List<Exception>();
            // Disable first, then release only this round's acquisition data.
            // Deliberately do not call StopCapture's optional AI restoration.
            bool outputReturned=false,picoReturned=false;
            Try(()=>{Shutdown();outputReturned=true;},failures);
            Try(() => ClearTask(finite),failures);
            finiteArmed = finite.Handle != IntPtr.Zero;
            // StopCapture returns only after the native stop (when required),
            // pin.Free and callback/buffer release. An exception retains ownership.
            Try(()=>{pico.StopCapture();picoReturned=true;},failures);
            Try(() => ClearTask(monitor),failures);
            roundActive = false;
            requireFreshWindowAfterAo = false;
            if (failures.Count != 0)
            { faulted = true; throw new AggregateException("本轮关断/采集释放不完整；常驻会话不可复用。",failures); }
            CompleteBatchPermit();
            if (!faulted && !EmergencyOutputLatched) emergencyMonitor.EnableIdleTimeoutRetry();
            healthyRoundStopFacts=new FactoryHealthyRoundStopFacts(outputReturned&&!hvOn&&!hvCommands.MayBeOn&&!opticalOn&&lastAo==0,
                finite.Handle==IntPtr.Zero&&monitor.Handle==IntPtr.Zero&&!finiteArmed,picoReturned,picoReturned&&!pico.Armed,true);
        }

        // Called only by the serialized device owner while no round is active.
        // The separate supervisor may call ReportConnectionSnapshot (managed
        // memory only), but must never call this method alongside SDK work.
        internal void PollIdle()
        {
            EnsureInitialized();
            if (roundActive) throw new InvalidOperationException("本轮仍在运行，不能并行执行空闲SDK轮询。");
            try
            {
                if(settings.ManualZeroInputWithoutEmergency)
                {
                    CheckPicoConnection();VerifyArtIdentity();
                    if(!faulted&&identitiesVerified)ReportIdleIdentity(settings.Input.ArtDevice,ConnectionReport);
                    return;
                }
                RebuildIdleEmergencyTaskIfRequired();
                bool? released=emergencyMonitor.ReadIdleReleased();
                if (!released.HasValue) return; // UNKNOWN, never a cached HIGH permit
                // A valid idle LOW prohibits pressure, but does not close the
                // input session that must observe the operator releasing it.
                CheckPicoConnection();
                // Watch subscribers intentionally start at the journal tail.
                // Repeat the already verified module identity on healthy idle
                // polls so a restored profile can rebuild its ART selector.
                if (!faulted && identitiesVerified) ReportIdleIdentity(settings.Input.ArtDevice,ConnectionReport);
            }
            catch { faulted = true; throw; }
            // Fresh DI publication is owned by its independent managed loop,
            // not delayed until these Pico/ART idle queries return.
        }

        private void RebuildIdleEmergencyTaskIfRequired()
        {
            if(EmergencyDisplayOnly)return; // diagnostic failure never recreates a manual input capability
            if (emergencyMonitor == null || !emergencyMonitor.IdleTimeoutRebuildRequired) return;
            // Fault stays latched after input-only rebuild. Output-owning or
            // active sessions must finish cleanup and explicitly reconnect.
            bool noOutput = !roundActive && !hvOn && !hvCommands.MayBeOn && !opticalOn && lastAo==0 &&
                !finiteArmed && !pico.Armed && finite.Handle==IntPtr.Zero && monitor.Handle==IntPtr.Zero &&
                ao.Handle==IntPtr.Zero && hv.Handle==IntPtr.Zero && optical.Handle==IntPtr.Zero &&
                currentMode.Handle==IntPtr.Zero && indicator.Handle==IntPtr.Zero;
            var previous=emergencyMonitor;
            ReportConnectionSnapshot(); // retain first and second timeout evidence before replacement
            if(ConnectionReport!=null)ConnectionReport("P04_IDLE_REBUILD_BEGIN",FactoryConnectionWatch.Stamp(
                "两次明确SDK超时；仅空闲无输出尝试本会话唯一一次DI重建。其他端口状态不代替P0.4许可。SessionId="+sessionId+"; oldDiTaskGeneration="+emergencyTaskGeneration));
            try
            {
                idleDiRebuild.Rebuild(noOutput,
                    () => { StopEmergencyPublisher(); if(!previous.Stop(600))throw new InvalidOperationException("旧P0.4调用尚未退出；禁止清除或重建DI任务。"); previous.Dispose(); },
                    () => {
                        // Existing vendor SDK identity attributes only; this
                        // does not Open/Reset, read DI concurrently, or prove
                        // whether the driver served cached identity data.
                        VerifyArtIdentity(detail=>{
                            if(ConnectionReport!=null)ConnectionReport("P04_IDLE_ART_IDENTITY_QUERY",FactoryConnectionWatch.Stamp(detail));
                        });
                        if(ConnectionReport!=null)ConnectionReport("P04_IDLE_ART_IDENTITY_VERIFIED",FactoryConnectionWatch.Stamp(
                            "厂商SDK现有设备身份属性查询通过；仅核对原ART序列号/型号，不证明P0.4电平、当前物理在线或排除瞬断；驱动缓存行为未验证。SessionId="+sessionId));
                    },
                    () => ClearTask(emergency),
                    () => {
                        emergencyMonitor=null; // old reader exited AND old task clear succeeded
                        CreateTask(emergency);
                        ++emergencyTaskGeneration;
                        Check(InputOnlyArtNative.CreateDi(emergency.Handle,Physical("port0/line4"),String.Empty,0),"重建P0.4单线输入");
                        emergency.Configured=true;StartTask(emergency);
                        var replacement=CreateEmergencyMonitor(ReadEmergencyCore);
                        emergencyMonitor=replacement;
                        // Replacement must return a fresh, timely HIGH. No
                        // retry/rebuild is enabled during this validation.
                        replacement.Start();
                        if(!replacement.ReadReleased())throw new InvalidOperationException("重建P0.4未获得新的有效高电平；保留锁存，不恢复输出。");
                        replacement.EnableIdleTimeoutRetry(); // further double-timeouts latch; no second rebuild
                        StartEmergencyPublisher(); // prior producer exited before changing DI generation
                    });
                if(ConnectionReport!=null)ConnectionReport("P04_IDLE_REBUILD_RECOVERED",FactoryConnectionWatch.Stamp(
                    "旧读退出、旧DI任务清除成功，新DI任务获得新鲜HIGH；原会话故障保持锁存，须明确重新连接，不自动开始任务或加压。SessionId="+sessionId+"; newDiTaskGeneration="+emergencyTaskGeneration));
            }
            catch(Exception ex)
            {
                faulted=true;
                if(ConnectionReport!=null)ConnectionReport("P04_IDLE_REBUILD_FAILED",FactoryConnectionWatch.Stamp(ex.ToString()));
                throw;
            }
            finally { ReportConnectionSnapshot(); }
        }

        internal static void ReportIdleIdentity(string module,Action<string,string> report)
        { if (report != null) report("LINK_ART_NAME",module); } // Existing UI protocol requires the bare module name.

        internal static void ValidatePhysicalContract(BenchSettings input)
        {
            if (input.ArtTerminal != Rse) throw new ArgumentException("已确认 AI0/AI2/AI4 为参考单端 RSE · AGND，不允许改变参考接线。");
            foreach (char c in input.ArtDevice)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    throw new ArgumentException("ART 模块名不得含通道列表、路径或范围语法。");
            uint serial;
            if (!UInt32.TryParse(input.ArtSerial.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out serial) || serial == 0)
                throw new ArgumentException("ART 身份必须为非零实际序列号。");
            if (input.ArtTrigger != "/" + input.ArtDevice + "/PFI0" || input.FallingTrigger || input.ExternalTriggerVolts <= 0)
                throw new ArgumentException("已确认接线要求 ART PFI0 / Pico EXT 正阈值上升沿，不能自动替换触发端或边沿。");
        }

        private void VerifyArtIdentity(Action<string> recordQuery = null)
        {
            uint serial;
            int serialStatus=InputOnlyArtNative.GetSerial(settings.Input.ArtDevice, 0x0632, out serial, IntPtr.Zero);
            if(recordQuery!=null)recordQuery("ArtDAQ_GetDeviceAttribute(DeviceSerialNum): status="+serialStatus+
                "; requestedModule="+settings.Input.ArtDevice+"; serial="+(serialStatus>=0?serial.ToString(CultureInfo.InvariantCulture):"未知（失败返回值不作有效身份）"));
            Check(serialStatus, "读取 ART 序列号");
            artSerial = serial.ToString(CultureInfo.InvariantCulture);
            if (serial == 0 || !String.Equals(artSerial, settings.Input.ArtSerial.Trim(), StringComparison.Ordinal))
                Fail("ART 序列号不符；身份故障已锁存，禁止新增输出。已有输出仅尝试关断。");
            var model = new StringBuilder(128);
            int modelStatus=InputOnlyArtNative.GetProduct(settings.Input.ArtDevice, 0x0631, model, model.Capacity, IntPtr.Zero);
            if(recordQuery!=null)recordQuery("ArtDAQ_GetDeviceAttributeEx(DeviceProductType): status="+modelStatus+
                "; requestedModule="+settings.Input.ArtDevice+"; model="+(modelStatus>=0?model.ToString().Trim():"未知（失败返回值不作有效身份）"));
            Check(modelStatus, "读取 ART 型号");
            if (!String.Equals(model.ToString().Trim(), "USB3135", StringComparison.OrdinalIgnoreCase))
                Fail("仅允许 USB3135，实际 " + model + "；身份故障已锁存，禁止新增输出。已有输出仅尝试关断。");
        }

        public FactoryFeedback ReadFeedback()
        {
            EnsureInitialized();
            if(settings.SkipPressureResidualMeasurement && (!roundActive || calibration || !hvOn || opticalOn || finiteArmed || faulted))
                throw new InvalidOperationException("加压反馈只在实际升压/达压保持阶段读取；不为启动前、关断后或故障残压追加AI采样。");
            // The dormant owned monitor is started only at the first actual
            // HV_UP feedback read, not at HV ON or during its zero-output wait.
            if(settings.SkipPressureResidualMeasurement && roundActive && !calibration && hvOn &&
                !faulted && !opticalOn && !finiteArmed && finite.Handle==IntPtr.Zero && !monitor.Started)
            {
                EnsurePositiveOutputsAllowed();StartMonitor();
            }
            if (finite.Handle != IntPtr.Zero || monitor.Handle == IntPtr.Zero || !monitor.Started)
                throw new InvalidOperationException("有限 AI4 采集占用 AI 子系统；此阶段只允许读取 P0.4，不能伪造监测反馈。");
            try
            {
                var buffer=new MonitorBuffer(this);
                bool preparing=feedbackStartup.NeedsPreparation(lastAo,requireFreshWindowAfterAo,
                    roundActive && !faulted && !opticalOn && !opticalAttempted && !finiteArmed);
                int readBudget=FactoryFeedbackStream.MaximumReadMs;
                if(preparing)
                {
                    if(monitorStartClock == null)throw new InvalidOperationException("ART 首帧缺少当前监测任务的启动时间。");
                    using(IoTiming.BeginOperation("ART.Monitor.InitialWindowWait.ZeroOutput"))
                        FactoryFeedbackStream.WaitForInitialWindow(feedbackPlan,buffer,
                            () => monitorStartClock.ElapsedMilliseconds,feedbackStartup,FeedbackReadCancellation);
                    readBudget=Math.Min(readBudget,feedbackStartup.RemainingMilliseconds(monitorStartClock.ElapsedMilliseconds));
                }
                var elapsed=Stopwatch.StartNew();
                FactoryFeedback feedback;
                using(IoTiming.BeginOperation(preparing ? "ART.Monitor.FirstWindowRead" : "ART.Monitor.RunningFeedbackRead"))
                    feedback=FactoryFeedbackStream.Read(feedbackPlan,buffer,lastAo,
                        () => elapsed.ElapsedMilliseconds,true,requireFreshWindowAfterAo,FeedbackReadCancellation,null,readBudget);
                if(preparing)feedbackStartup.RemainingMilliseconds(monitorStartClock.ElapsedMilliseconds);
                // Only a successful bounded read consumes the pending request.
                // A failure retains the request and latches the existing fault.
                feedbackStartup.Complete();
                requireFreshWindowAfterAo = false;
                return feedback;
            }
            catch(FactoryEmergencyStopException){throw;}
            catch(OperationCanceledException) when(FeedbackReadCancellation.IsCancellationRequested){throw;}
            catch { faulted = true; throw; }
        }
        private sealed class MonitorBuffer : IFactoryFeedbackBuffer
        {
            private readonly NativeFactoryBackend owner;
            private uint lastAvailable;
            internal MonitorBuffer(NativeFactoryBackend backend) { owner = backend; }
            public uint Available()
            {
                uint available;
                owner.CheckObservedArtStatus(InputOnlyArtNative.GetAvailableSamples(owner.monitor.Handle,out available),"读取 ART 连续反馈未读点数");
                return lastAvailable=available;
            }
            public double[] Read(int requested)
            {
                var raw = new double[requested * 3]; int read=-1,status;
                var elapsed=Stopwatch.StartNew();
                using (owner.IoTiming.BeginOperation("ART.Monitor.ReadAnalog"))
                    status=InputOnlyArtNative.ReadAnalogReceipt(owner.monitor.Handle,requested,DriverIoTimeoutSeconds,1,
                        raw,(uint)raw.Length,ref read,IntPtr.Zero);
                owner.ObserveMonitorRead(new FactoryAnalogReadReceipt(status,requested,read,raw.Length,lastAvailable,elapsed.ElapsedMilliseconds));
                if (owner.RawFeedbackRead != null) owner.RawFeedbackRead(raw,read);
                return raw;
            }
            public bool EmergencyReleased() { return owner.ReadEmergencyReleased(); }
        }

        public bool ReadEmergencyReleased()
        {
            EnsureInitialized();
            if (emergencyMonitor == null) Fail("独立 P0.4 监控未就绪。");
            // Consume only the sole DI owner's current permission. A periodic
            // Pico USB query is not DI evidence and must not occupy an energized
            // handoff boundary. Open/BeginRound/idle and actual Pico operations
            // retain their transport/identity checks and failure handling.
            try { return emergencyMonitor.ReadReleased(); }
            catch { faulted = true; throw; }
        }
        private bool ReadEmergencyCore(int timeoutMilliseconds)
        {
            // ONLY called on emergencyMonitor's thread, never on AI/output/UI threads.
            if (emergency.Handle == IntPtr.Zero || !emergency.Started) throw new InvalidOperationException("P0.4 输入任务未就绪。");
            var data = new byte[] { 0xA5 };
            int count=-1, bytes=-1;
            // The DI owner only captures bounded in-memory diagnostics; never
            // formats/writes a file or waits for the AI/Pico owner here.
            var timing = IoTiming;
            string context = timing.CurrentOperation;
            int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
            long began = FactoryIoTimingDiagnostics.Timestamp();
            if(timeoutMilliseconds<=0 || timeoutMilliseconds>FactoryEmergencyReadPolicy.FirstReadTimeoutMs)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            int status = InputOnlyArtNative.ReadDigitalReceipt(emergency.Handle, 1, timeoutMilliseconds/1000.0, 0,
                data, 1, ref count, ref bytes, IntPtr.Zero);
            long ended = FactoryIoTimingDiagnostics.Timestamp();
            timing.RecordDigital(began, ended, status, count, bytes, data[0], context,
                GC.CollectionCount(0)-gc0, GC.CollectionCount(1)-gc1, GC.CollectionCount(2)-gc2);
            // Only the two official pending codes with SDK-written zero count
            // use the existing one-retry/500 ms monitor policy. Neither an
            // unchanged -1 sentinel nor partial data is a pending permission.
            if (FactoryEmergencyReadPolicy.IsZeroSamplePending(status,count))
            {
                throw new FactoryEmergencyReadTimeoutException(status,null);
            }
            // Do not call error-text SDK/report callbacks on this reader. The
            // managed monitor latches; the serialized owner handles reporting.
            if(status<0)throw new InvalidOperationException("ART P0.4 status="+status+"/"+
                FactoryEmergencyReadPolicy.OfficialStatusName(status)+"; actual="+count+"; bytes="+bytes);
            if (count != 1 || bytes != 1 || data[0] > 1) throw new InvalidOperationException("P0.4 未返回有效单线样本；原始值=" + data[0] + "，样本=" + count + "，字节=" + bytes + "。");
            return data[0] != 0; // Explicit user-confirmed high = emergency released.
        }
        internal static bool IsEmergencySdkTimeout(int status) { return FactoryEmergencyReadPolicy.IsRetryableStatus(status); }

        public void WriteAo(double volts)
        {
            EnsureInitialized();
            EnsureRoundActive();
            if (settings.ZeroSynchronizedOptical) throw new InvalidOperationException("零电压光触发诊断禁止写入AO，包括归零命令。");
            settings.ValidateAoCommand(volts);
            if (volts > 0)
            {
                EnsurePositiveOutputsAllowed();
                if (calibration || !hvOn || opticalOn || finiteArmed || finite.Handle != IntPtr.Zero || !DiagnosticMonitorAvailable)
                    throw new InvalidOperationException("非零 AO 仅允许三路监测运行中的升压阶段；切换有限采集后禁止继续增压。");
                if (!ReadEmergencyReleased()) throw new FactoryEmergencyStopException();
            }
            WriteAoCore(volts);
        }

        public void SetHv(bool on)
        {
            EnsureInitialized();
            EnsureRoundActive();
            if (settings.ZeroSynchronizedOptical) throw new InvalidOperationException("零电压光触发诊断禁止HV ON/OFF。");
            if (on)
            {
                EnsurePositiveOutputsAllowed();
                if (calibration || opticalOn || finiteArmed) throw new InvalidOperationException("校准或采集布置阶段禁止 HV ON。");
                if (!ReadEmergencyReleased()) throw new FactoryEmergencyStopException();
                WriteLine(currentMode, false);
            }
            if (hvCommands.Write(on, high => WriteLine(hv, high))) hvOn = on;
            if (on && settings.UsePostHvVmonStartup && !settings.SkipPressureResidualMeasurement && !monitor.Started)
                StartMonitor(); // No AI StartTask/read until the HV ON write has returned.
        }
        internal void SetPressureIndicator(bool on)
        {
            EnsureInitialized();
            EnsureRoundActive();
            if (settings.ZeroSynchronizedOptical) throw new InvalidOperationException("零电压光触发诊断仅允许P0.7输出。");
            if (on) EnsurePositiveOutputsAllowed();
            if (indicator.Handle == IntPtr.Zero)
            {
                if (!on) return;
                VerifyArtIdentity(); CreateSingleOutput(indicator,3);
            }
            WriteLine(indicator,on);
        }

        public void SetOptical(bool on)
        {
            EnsureInitialized();
            EnsureRoundActive();
            if (on)
            {
                EnsurePositiveOutputsAllowed();
                if (calibration || hvOn || lastAo != 0 || !finiteArmed || !pico.Armed || opticalAttempted)
                    throw new InvalidOperationException("光触发只允许 HV OFF / AO=0 / 两卡已布置的压测阶段，每轮仅一次。");
                if (!ReadEmergencyReleased()) throw new FactoryEmergencyStopException();
                if ((settings.Input.ArtPreTriggerCount>0?ArtComplete:ArtAvailable()!=0) || pico.Ready) Fail("发出光触发前采集已完成；共享物理触发状态不成立。");
                opticalAttempted = true;
            }
            WriteLine(optical, on);
            opticalOn = on;
        }

        public void Arm(bool freeRun)
        {
            EnsureInitialized();
            EnsurePositiveOutputsAllowed();
            if (freeRun != calibration || hvOn || opticalOn || lastAo != 0 || finite.Handle != IntPtr.Zero || pico.Armed)
                throw new InvalidOperationException("采集模式或输出状态不允许布置两卡；必须先 HV OFF、AO=0、光触发低。");
            ArmCore(freeRun, false);
        }

        public void PrepareOpticalRetry()
        {
            EnsureInitialized(); EnsurePositiveOutputsAllowed();
            if (!settings.UseOpticalTimeoutRetry || calibration || opticalRetryUsed || !opticalAttempted ||
                hvOn || opticalOn || lastAo != 0)
                throw new InvalidOperationException("不满足单次光补发条件；不得重用部分完成段或故障状态。");
            if (!opticalRetryStopped)
            {
                if (!finiteArmed || !pico.Armed || ArtComplete || PicoComplete)
                    throw new InvalidOperationException("补发前旧段未布置或已有一张卡完成，不能替换原始段。");
                StopCapture();
            }
            if (!opticalRetryStopped) throw new InvalidOperationException("旧段未经完整停止并确认两卡均未完成，禁止补发。");
            opticalRetryUsed = true; // Consumed before any SDK action; a failure cannot retry this path.
            opticalRetryStopped = false;
            Arm(false);   // Existing guarded HV OFF / AO=0 path, no source restart.
        }

        public void ArmBeforeShutdown()
        {
            EnsureInitialized(); EnsurePositiveOutputsAllowed();
            if (calibration || !hvOn || opticalOn || finite.Handle != IntPtr.Zero || pico.Armed || !DiagnosticMonitorAvailable)
                throw new InvalidOperationException("达到目标后的任务交接状态无效；禁止重复布置或新增输出。");
            ArmCore(false, true);
        }

        private void ArmCore(bool freeRun, bool beforeShutdown)
        {
            var handoff = Stopwatch.StartNew();
            Action checkHandoff = () =>
            {
                using(IoTiming.BeginOperation("Handoff.CheckCurrentEmergency"))
                    if (!ReadEmergencyReleased()) throw new FactoryEmergencyStopException();
                // Guard on each returning SDK boundary. Cannot preempt a hung SDK
                // call; independent hardware protection remains necessary.
                if (beforeShutdown && handoff.ElapsedMilliseconds > 500)
                    Fail("AI任务交接超过500 ms；停止输出，不发光。SDK阻塞不能由此软件计时抢占。");
            };
            using(IoTiming.BeginOperation("Handoff.TotalIncludingFailureCleanup"))
            {
            try
            {
                using(IoTiming.BeginOperation("Handoff.VerifyArtIdentity"))VerifyArtIdentity(); checkHandoff();
                using(IoTiming.BeginOperation("Handoff.ClearMonitor"))ClearTask(monitor);
                checkHandoff(); using(IoTiming.BeginOperation("Handoff.ConfigureFinite"))ConfigureFinite(freeRun); checkHandoff();
                using(IoTiming.BeginOperation("Handoff.PicoSetFreeRun"))pico.SetFreeRun(freeRun); checkHandoff();
                using(IoTiming.BeginOperation("Handoff.PicoArm"))pico.Arm(); checkHandoff();
                finiteArmed = true; // Retain uncertain ownership if native Start reports failure.
                using(IoTiming.BeginOperation("Handoff.StartFinite"))StartTask(finite); checkHandoff();
                // Fill pretrigger history inside the EXISTING 500 ms handoff budget.
                // Never extend an energized hold just to obtain more history.
                var priming=Stopwatch.StartNew();
                while(!freeRun && settings.Input.ArtPreTriggerCount>0 && priming.Elapsed.TotalMilliseconds<settings.Input.PreTriggerDurationMs+1)
                {checkHandoff();System.Threading.Thread.Sleep(1);}
                checkHandoff();
                opticalAttempted = false;
            }
            catch (Exception failure)
            {
                if (!(failure is FactoryEmergencyStopException)) faulted = true;
                var failures = new List<Exception> { failure };
                Try(Shutdown, failures);
                Try(StopCapture, failures);
                throw new AggregateException("两卡布置失败；未发送光触发，关断及恢复监测已尝试。", failures);
            }
            }
        }

        public bool ArtComplete { get { EnsureInitialized();if(!finiteArmed)return false;
            if(!calibration && !zeroInputRound && settings.Input.ArtPreTriggerCount>0){uint done;Check(IsTaskDone(finite.Handle,out done),"读取ART参考触发完成状态");if(done==0)return false;}
            return ArtAvailable() == settings.Input.ExpectedArtCount; } }
        public bool PicoComplete
        {
            get
            {
                EnsureInitialized();
                try { if (pico.Ready) pico.CheckCompletionStatus(); return pico.Armed && pico.Ready; }
                catch { faulted = true; throw; }
            }
        }
        private uint ArtAvailable()
        {
            if (!finiteArmed || finite.Handle == IntPtr.Zero) Fail("ART 有限任务未布置。");
            uint count;
            Check(InputOnlyArtNative.GetAvailableSamples(finite.Handle, out count), "读取 ART 有限任务可用点数");
            if (count > settings.Input.ExpectedArtCount) Fail("ART 可用点数超过本轮完整长度。");
            return count;
        }

        public BenchCapture ReadCapture()
        {
            using (IoTiming.BeginOperation("Capture.ReadCapture")) return ReadCaptureCore();
        }
        private BenchCapture ReadCaptureCore()
        {
            EnsureInitialized();
            if (hvOn || opticalOn || lastAo != 0) Fail("读取数据前 HV、AO、光触发尚未回到关闭状态。");
            if (!ArtComplete || !PicoComplete) throw new InvalidOperationException("两卡未完成，禁止阻塞等待或读取部分段。");
            using (IoTiming.BeginOperation("ART.VerifyIdentity.BeforeRead")) VerifyArtIdentity();
            var voltage = new double[settings.Input.ExpectedArtCount];
            int count;
            using (IoTiming.BeginOperation("ART.Finite.ReadAnalog"))
            CheckObservedArtStatus(InputOnlyArtNative.ReadAnalog(finite.Handle, voltage.Length, DriverIoTimeoutSeconds, 0,
                voltage, (uint)voltage.Length, out count, IntPtr.Zero), "读取已完成 ART AI4 完整有限段");
            if (count != voltage.Length) Fail("ART 实际数据点数不足本轮完整段。");
            using (IoTiming.BeginOperation("Capture.ValidateArtSamples"))
                foreach (double value in voltage) if (!FactorySettings.Finite(value)) Fail("ART 有限段含非有限电压。");
            PicoBlockCapture pd;
            try { pd = pico.ReadCompleted(); }
            catch { faulted = true; throw; }
            var result = new BenchCapture { Settings = CloneInput(settings.Input), VoltageRaw = voltage, PdAdc = pd.RawAdc,
                ArtRateHz = actualArtRate, ArtFullScaleV=FactoryArtFeedbackPlan.DividerFullScale(settings), PicoRateHz = 1e9 / pd.SampleIntervalNanoseconds, MaximumAdc = pd.MaximumAdc,
                PicoFullScaleV = pd.FullScaleVolts, HardwareBits = pd.RangeHardwareBits, Overflow = pd.HardwareOverflow,
                Simulation = false, ArtSerial = artSerial, PicoSerial = pd.Serial, CapturedUtc = DateTime.UtcNow,
                Runtime = "ART: " + artRuntime + " | Pico: " + Pico5000aNative.LoadedPath +
                    (zeroInputRound ? " | ZERO_INPUT_ONLY; FREE_RUN; NO_COMMON_TRIGGER" :
                    calibration ? " | 校准自由段：两卡启动独立，未证明共同物理起点" : " | ART PFI0 / Pico EXT 共同物理检测上升沿") };
            using (IoTiming.BeginOperation("Capture.ValidateComplete")) result.ValidateCapture();
            return result;
        }

        public void StopCapture()
        {
            using (IoTiming.BeginOperation("Capture.StopCapture")) StopCaptureCore();
        }
        private void StopCaptureCore()
        {
            if (disposed) return;
            if(zeroInputRound){EndZeroInputRound(zeroInputGeneration);return;}
            var failures = new List<Exception>();
            bool retryEligible = false;
            if (initialized && roundActive && !faulted && settings.UseOpticalTimeoutRetry && !calibration &&
                !opticalRetryUsed && opticalAttempted && !hvOn && !hvCommands.MayBeOn && !opticalOn && lastAo == 0 &&
                finiteArmed && pico.Armed)
                Try(() => retryEligible = !ArtComplete && !PicoComplete,failures);
            opticalRetryStopped = false;
            Try(() => ClearTask(finite), failures);
            finiteArmed = finite.Handle != IntPtr.Zero;
            Try(pico.StopCapture, failures);
            if (initialized && finite.Handle == IntPtr.Zero && monitor.Handle != IntPtr.Zero && !monitor.Started)
                Try(() => ClearTask(monitor), failures);
            if (initialized && roundActive && finite.Handle == IntPtr.Zero && monitor.Handle == IntPtr.Zero &&
                RestoreMonitorAfterCapture(settings, calibration, faultMonitoring))
                Try(CreateMonitor, failures);
            if (failures.Count != 0) { faulted = true; throw new AggregateException("停止有限采集／恢复监测失败，资源状态未被伪装为成功。", failures); }
            // A successful stop carries only a one-shot retry eligibility token;
            // it never carries old buffers, old completion or output permission.
            opticalRetryStopped = retryEligible;
        }

        internal static bool RestoreMonitorAfterCapture(FactorySettings s, bool calibrationRun, bool faultObservation)
        { return calibrationRun || s == null || (!s.SkipPressureResidualMeasurement && (faultObservation || s.ShouldCheckEndResidual)); }
        internal static bool StartMonitorBeforeRamp(FactorySettings s,bool calibrationRun)
        { return calibrationRun || (!s.SkipPressureResidualMeasurement && !s.UsePostHvVmonStartup); }

        public void Shutdown()
        {
            using (IoTiming.BeginOperation("Outputs.Shutdown")) ShutdownCore();
        }
        private void ShutdownCore()
        {
            if(zeroInputRound)throw new InvalidOperationException("0 kV只读轮次不允许输出操作，包括关断写入；请停止输入轮次。");
            if (!identitiesVerified) return; // Identity failure can never create or write outputs.
            var failures = new List<Exception>();
            if (optical.Configured) Try(() => { WriteLine(optical, false); opticalOn = false; }, failures);
            if (hv.Configured) Try(() => { hvCommands.Write(false, high => WriteLine(hv, high)); hvOn = false; }, failures);
            if (ao.Configured) Try(() => WriteAoCore(0), failures);
            if (indicator.Configured) Try(() => WriteLine(indicator,false), failures);
            if (currentMode.Configured) Try(() => WriteLine(currentMode, false), failures);
            // All disable actions are attempted BEFORE any input cleanup. Do
            // not leave the ramp AI task acquiring a post-OFF residual tail.
            // A failed Stop retains the handle/Started state for later cleanup.
            if(settings!=null&&settings.SkipPressureResidualMeasurement&&monitor.Handle!=IntPtr.Zero&&monitor.Started)
                Try(StopPressureMonitorAfterShutdown,failures);
            if (failures.Count != 0) { faulted = true; throw new AggregateException("关断输出失败；P0.7低、P0.0低、AO0零及P0.2低均分别尝试，不能确认物理输出安全。", failures); }
        }
        private void StopPressureMonitorAfterShutdown()
        {
            using(IoTiming.BeginOperation("ART.StopTask:pressure-monitor-after-off"))
                Check(taskIo.Stop(monitor.Handle),"关断后停止升压监测AI；不读取残压");
            monitor.Started=false;
            if(MonitorChanged!=null)MonitorChanged(false);
        }

        public void Dispose()
        {
            using (IoTiming.BeginOperation("Backend.Dispose")) DisposeCore();
        }
        private void DisposeCore()
        {
            // Resource release and successful shutdown are distinct facts.
            // A later idempotent Dispose must not erase a previous failed LOW
            // write/Stop just because all Clear calls happened to succeed.
            if (disposed) { if (cleanupFailure != null) throw cleanupFailure; return; }
            var failures = new List<Exception>();
            // A zero-input round was admitted only with all outputs already
            // off. Its cleanup must not issue even an AO=0 / DO=false write.
            if(!zeroInputRound)Try(Shutdown, failures);
            bool publisherStopped=true;
            try { StopEmergencyPublisher(); }
            catch(Exception error) { publisherStopped=emergencyPublisher==null;failures.Add(error); }
            if(emergencyPublisherFailure!=null && !failures.Contains(emergencyPublisherFailure))
                failures.Add(emergencyPublisherFailure);
            Try(pico.Dispose, failures);
            bool emergencyStopped = publisherStopped;
            if (publisherStopped && emergencyMonitor != null)
            {
                try { using (IoTiming.BeginOperation("P0.4.StopReader")) emergencyMonitor.Dispose(); emergencyMonitor = null; }
                catch (Exception ex) { emergencyStopped = false; failures.Add(ex); }
            }
            foreach (OwnedTask task in new[] { finite, monitor, ao, currentMode, hv, optical, indicator })
                Try(() => ClearTask(task), failures);
            if (emergencyStopped) Try(() => ClearTask(emergency), failures);
            initialized = false;
            roundActive = false;
            disposed = !AnyTaskOwned() && !pico.ConnectionOpen && emergencyPublisher==null;
            if (failures.Count == 0 && cleanupFailure == null && ConnectionReport != null)
            {
                Try(() => ConnectionReport("LINK_ART_CLOSED",FactoryConnectionWatch.Stamp("设备会话输入及输出任务已释放")),failures);
                Try(() => ConnectionReport("LINK_PICO_CLOSED",FactoryConnectionWatch.Stamp("设备会话采集连接已释放")),failures);
            }
            if (failures.Count != 0)
            {
                faulted = true;
                if (cleanupFailure != null) failures.Insert(0, cleanupFailure);
                cleanupFailure = new AggregateException("出厂测试关断及资源清理报告失败；保留所有错误和未清除句柄，不证明已放电。", failures).Flatten();
            }
            if (cleanupFailure != null) throw cleanupFailure;
        }

        private void ConfigureFinite(bool freeRun)
        {
            double range=FactoryArtFeedbackPlan.DividerFullScale(settings);
            CreateTask(finite);
            Check(InputOnlyArtNative.CreateAi(finite.Handle, Physical("ai4"), String.Empty,
                Rse, -range, range, Volts, null), "配置 AI4 RSE · AGND 分压器量程 ±"+range+" V");
            finite.Configured = true;
            Check(InputOnlyArtNative.ConfigureClock(finite.Handle, String.Empty, BenchSettings.ArtRate,
                Rising, FiniteSamples, settings.Input.ExpectedArtCount), "配置 ART 80 kS/s 完整有限段");
            if (!freeRun)
            {
                if(settings.Input.ArtPreTriggerCount>0)
                    Check(ConfigureReferenceTrigger(finite.Handle,"/"+settings.Input.ArtDevice+"/PFI0",Rising,(uint)settings.Input.ArtPreTriggerCount),"配置ART PFI0参考触发/触发前样点");
                else Check(InputOnlyArtNative.ConfigureTrigger(finite.Handle, "/" + settings.Input.ArtDevice + "/PFI0", Rising), "配置 ART PFI0 数字检测触发，禁止自动替代");
            }
            Check(InputOnlyArtNative.TaskControl(finite.Handle, 2), "验证 ART 有限时钟及触发");
            Check(InputOnlyArtNative.TaskControl(finite.Handle, 3), "提交 ART 有限输入配置，尚未启动");
            FactoryArtFeedbackNative.VerifyDividerRange(finite.Handle,Physical("ai4"),range,Check);
            Check(InputOnlyArtNative.GetTimingRate(finite.Handle, 0x1344, out actualArtRate, sizeof(double)), "回读 ART 有限采样率");
            if (!FactorySettings.Finite(actualArtRate) || Math.Abs(actualArtRate - BenchSettings.ArtRate) > .5)
                Fail("ART 采样率回读与 80 kS/s 请求不一致。");
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll",EntryPoint="ArtDAQ_CfgDigEdgeRefTrig",CallingConvention=CallingConvention.StdCall,CharSet=CharSet.Ansi)]
        private static extern int ConfigureReferenceTrigger(IntPtr task,string source,int edge,uint pretrigger);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll",EntryPoint="ArtDAQ_IsTaskDone",CallingConvention=CallingConvention.StdCall)]
        private static extern int IsTaskDone(IntPtr task,out uint done);
        private void CreateMonitor()
        { CreateMonitor(true); }
        private void CreateMonitor(bool start)
        {
            if (monitor.Handle != IntPtr.Zero || finite.Handle != IntPtr.Zero) throw new InvalidOperationException("ART AI 子系统仍由旧任务占用。");
            CreateTask(monitor);
            monitor.Configured = true;
            try { feedbackPlan = FactoryArtFeedbackNative.Configure(monitor.Handle, settings, Check); }
            catch { faulted = true; throw; }
            if (start) StartMonitor();
        }
        private void StartMonitor()
        {
            if (monitor.Handle == IntPtr.Zero || !monitor.Configured || monitor.Started || finite.Handle != IntPtr.Zero)
                throw new InvalidOperationException("监测AI任务尚未配置或子系统仍被占用，不能启动。");
            // Include the native start and any intervening scheduling in the
            // existing initial total deadline; never restart it on each poll.
            monitorStartClock=Stopwatch.StartNew();
            feedbackStartup.Started(settings.InitialFeedbackTimeoutMs,
                lastAo == 0 && !faulted && !opticalAttempted);
            using(IoTiming.BeginOperation("ART.Monitor.StartTask"))StartTask(monitor);
            if (MonitorChanged != null) MonitorChanged(true);
        }

        private void CreateSingleOutput(OwnedTask task, int line)
        {
            if (!identitiesVerified || (line != 0 && line != 2 && line != 7 && (line != 3 || !ReferenceEquals(task,indicator)))) throw new InvalidOperationException("输出身份或固定单线映射未通过。");
            CreateTask(task);
            // Preserve uncertain configuration ownership on native failure so
            // initialization cleanup attempts a LOW write as well as Clear.
            task.Configured = true;
            Check(FactoryArtNative.CreateDo(task.Handle, Physical("port0/line" + line), String.Empty, 0), "配置 " + task.Name + "，禁止整口写入");
        }
        private void WriteLine(OwnedTask task, bool high)
        {
            if (!identitiesVerified || !task.Configured || task.Handle == IntPtr.Zero) throw new InvalidOperationException("输出任务未通过身份和通道验证。");
            if (ReferenceEquals(task, currentMode) && high) throw new InvalidOperationException("P0.2 固定低电平过流停机模式；禁止满功率输出模式。");
            if (high) EnsurePositiveOutputsAllowed();
            int written;
            task.Started = true;
            CheckObservedArtStatus(taskIo.WriteLines(task.Handle, 1, 1, DriverIoTimeoutSeconds, 0,
                new[] { high ? (byte)1 : (byte)0 }, out written, IntPtr.Zero), "写入 " + task.Name + " = " + (high ? "高" : "低"));
            if (written != 1) Fail(task.Name + " 写入样本数不完整。");
        }
        private void WriteAoCore(double volts)
        {
            if (!identitiesVerified || !ao.Configured || ao.Handle == IntPtr.Zero) throw new InvalidOperationException("AO 输出任务未验证。");
            if (volts > 0) EnsurePositiveOutputsAllowed();
            int written;
            ao.Started = true;
            CheckObservedArtStatus(taskIo.WriteAnalog(ao.Handle, 1, 1, DriverIoTimeoutSeconds, 0,
                new[] { volts }, out written, IntPtr.Zero), "写入 AO0 VSET");
            if (written != 1) Fail("AO0 写入样本数不完整。");
            lastAo = volts;
            feedbackStartup.ObserveOutput(volts);
            requireFreshWindowAfterAo = volts > 0;
        }
        private void CreateTask(OwnedTask task)
        {
            if (task.Handle != IntPtr.Zero) throw new InvalidOperationException(task.Name + " 已占用任务句柄。");
            IntPtr created;
            int code = InputOnlyArtNative.CreateTask(String.Empty, out created);
            task.Handle = created;
            Check(code, "创建 " + task.Name);
            if (task.Handle == IntPtr.Zero) Fail(task.Name + " 未返回有效句柄。");
        }
        private void StartTask(OwnedTask task)
        {
            task.Started = true;
            Check(InputOnlyArtNative.StartTask(task.Handle), "启动 " + task.Name);
        }
        private void ClearTask(OwnedTask task)
        {
            if (task.Handle == IntPtr.Zero) return;
            var failures = new List<Exception>();
            if (task.Started) Try(() => { using (IoTiming.BeginOperation("ART.StopTask:" + task.Name))
                Check(taskIo.Stop(task.Handle), "停止 " + task.Name); task.Started = false; }, failures);
            try
            {
                using (IoTiming.BeginOperation("ART.ClearTask:" + task.Name))
                Check(taskIo.Clear(task.Handle), "清除 " + task.Name);
                task.Handle = IntPtr.Zero; task.Configured = false; task.Started = false;
                if(ReferenceEquals(task,monitor)){feedbackStartup.Complete();monitorStartClock=null;}
                if (ReferenceEquals(task,monitor) && MonitorChanged != null) MonitorChanged(false);
            }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) throw new AggregateException(task.Name + " 清理报告错误；只有成功 Clear 的句柄才释放。", failures);
        }
        private void Check(int code, string operation)
        { try { taskIo.Check(code, operation); } catch(Exception ex) { faulted = true;
            try { if(ConnectionReport!=null)ConnectionReport("LINK_ART_ERROR",FactoryConnectionWatch.Stamp("ART驱动/读写异常（不等同已证实拔线）："+ex.Message)); }
            catch(Exception reportError) { throw new AggregateException("ART操作及诊断报告均失败；保留原始驱动错误。",ex,reportError); }
            throw; } }
        private void Fail(string message) { faulted = true; throw new InvalidOperationException(message); }
        private void CheckObservedArtStatus(int code,string operation)
        { artCommunication.Observe(code,operation); Check(code,operation); }
        private void EnsureInitialized()
        { if (disposed || !initialized) throw new InvalidOperationException("出厂测试后端未完成初始化或已释放。"); }
        private void EnsureRoundActive()
        { if (!roundActive || zeroInputRound) throw new InvalidOperationException("空闲或0 kV只读会话没有输出许可。"); }
        private void EnsurePositiveOutputsAllowed()
        {
            EnsureRoundActive();
            if (emergencyMonitor == null) Fail("急停读取任务不存在，不能开放输出。");
            if (!ReadEmergencyReleased()) throw new FactoryEmergencyStopException();
            if (faulted || !identitiesVerified) throw new InvalidOperationException("后端故障已锁存或身份无效，禁止新增使能、升压及光触发。");
        }
        private bool AnyTaskOwned()
        {
            foreach (OwnedTask task in new[] { hv, currentMode, optical, ao, emergency, monitor, finite, indicator })
                if (task.Handle != IntPtr.Zero) return true;
            return false;
        }
        private string Physical(string channel) { return settings.Input.ArtDevice + "/" + channel; }
        private static BenchSettings CloneInput(BenchSettings input)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(BenchSettings));
                serializer.Serialize(stream, input); stream.Position = 0;
                return (BenchSettings)serializer.Deserialize(stream);
            }
        }
        private static void Try(Action operation, List<Exception> failures)
        { try { operation(); } catch (Exception ex) { failures.Add(ex); } }
    }

    // Only the serialized backend owner uses these synchronous operations.
    // The native implementation preserves the exact SDK arguments; a managed
    // implementation allows lifecycle faults to be exercised without loading
    // any driver, creating tasks or opening hardware.
    internal interface IFactoryArtTaskIo
    {
        int Stop(IntPtr task);
        int Clear(IntPtr task);
        int WriteLines(IntPtr task,int samples,uint autoStart,double timeout,uint layout,byte[] data,out int written,IntPtr reserved);
        int WriteAnalog(IntPtr task,int samples,uint autoStart,double timeout,uint layout,double[] data,out int written,IntPtr reserved);
        void Check(int code,string operation);
    }
    internal sealed class FactoryArtTaskIo : IFactoryArtTaskIo
    {
        public int Stop(IntPtr task) { return InputOnlyArtNative.StopTask(task); }
        public int Clear(IntPtr task) { return InputOnlyArtNative.ClearTask(task); }
        public int WriteLines(IntPtr task,int samples,uint autoStart,double timeout,uint layout,byte[] data,out int written,IntPtr reserved)
        { return FactoryArtNative.WriteLines(task,samples,autoStart,timeout,layout,data,out written,reserved); }
        public int WriteAnalog(IntPtr task,int samples,uint autoStart,double timeout,uint layout,double[] data,out int written,IntPtr reserved)
        { return FactoryArtNative.WriteAnalog(task,samples,autoStart,timeout,layout,data,out written,reserved); }
        public void Check(int code,string operation) { InputOnlyArtNative.Check(code,operation); }
    }
    internal static class FactoryArtNative
    {
        // Installed Art_DAQ.h: bool32 is 32-bit, ChanPerLine=0, GroupByChannel=0.
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateDOChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateDo(IntPtr task, string lines, string name, int grouping);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_WriteDigitalLines", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WriteLines(IntPtr task, int samples, uint autoStart, double timeout, uint layout,
            [In] byte[] data, out int written, IntPtr reserved);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateAOVoltageChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateAo(IntPtr task, string channel, string name, double minimum, double maximum, int units, string scale);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_WriteAnalogF64", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WriteAnalog(IntPtr task, int samples, uint autoStart, double timeout, uint layout,
            [In] double[] data, out int written, IntPtr reserved);
    }
}
