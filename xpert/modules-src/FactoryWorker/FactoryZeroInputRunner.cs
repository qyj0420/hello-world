using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using HVX.OWTS.Task38;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // This capability has no AO/DO, shutdown, HV or optical methods. Zero mode
    // must not accidentally "make safe" a port which this process does not own.
    internal interface IFactoryZeroInput : IDisposable
    {
        void Initialize(FactorySettings settings);
        bool EmergencyHigh { get; }
        void Arm();
        bool ArtComplete { get; }
        bool PicoComplete { get; }
        BenchCapture ReadCapture();
    }

    internal interface IFactoryZeroStatusSource
    {
        Action<string,string> ConnectionReport { set; }
    }

    // The adapter owns a round lease, not the persistent Pico/DI connection.
    // No output or connection-close capability is exposed to the zero runner.
    internal interface IFactoryResidentZeroInputSession
    {
        long BeginZeroInput(FactorySettings settings);
        bool ReadZeroEmergency(long generation);
        void ArmZeroInput(long generation);
        bool ZeroArtComplete(long generation);
        bool ZeroPicoComplete(long generation);
        BenchCapture ReadZeroCapture(long generation);
        void EndZeroInput(long generation);
    }

    internal sealed class ResidentFactoryZeroInput : IFactoryZeroInput
    {
        private readonly IFactoryResidentZeroInputSession session;
        private long generation;
        private bool active;
        internal ResidentFactoryZeroInput(IFactoryResidentZeroInputSession session)
        {this.session=session??throw new ArgumentNullException("session");}
        public void Initialize(FactorySettings settings)
        {
            if(active)throw new InvalidOperationException("只读轮次不能重复初始化。");
            generation=session.BeginZeroInput(FactoryNativeSessionPolicy.PrepareZeroInputRound(settings));active=true;
        }
        private long Current {get{if(!active)throw new InvalidOperationException("只读轮次已结束或尚未开始。");return generation;}}
        public bool EmergencyHigh {get{return session.ReadZeroEmergency(Current);}}
        public void Arm(){session.ArmZeroInput(Current);}
        public bool ArtComplete {get{return session.ZeroArtComplete(Current);}}
        public bool PicoComplete {get{return session.ZeroPicoComplete(Current);}}
        public BenchCapture ReadCapture(){return session.ReadZeroCapture(Current);}
        public void Dispose()
        {
            if(!active)return;
            session.EndZeroInput(generation); // failed Stop retains this lease and ownership
            active=false;
        }
    }

    internal static class FactoryZeroInputCleanup
    {
        internal static void Stop(Action stopAi,Action stopPico,Action releaseRound,Action latchFault)
        {
            var failures=new List<Exception>();
            try{stopAi();}catch(Exception e){failures.Add(e);}
            try{stopPico();}catch(Exception e){failures.Add(e);}
            if(failures.Count!=0)
            {latchFault();throw new AggregateException("0 kV输入停止未确认完成；保留轮次和句柄，禁止重开。",failures);}
            releaseRound();
        }
    }

    internal static class FactoryZeroInputRunner
    {
        internal static FactoryResult Run(FactorySettings requested, IFactoryZeroInput input,
            IFactoryClock clock, Action<string,string> report, CancellationToken token)
        {
            if (requested == null || input == null || clock == null) throw new ArgumentNullException();
            var s = requested.Clone();
            s.PressureFeedbackContext=!s.ManualZeroInputWithoutEmergency;
            if (s.TargetKv != 0 || s.ZeroSynchronizedOptical) throw new ArgumentException("只读自由采集只允许0 kV且未选择光触发的模式。");
            s.Validate(FactoryRunKind.Pressure);
            report = report ?? delegate { };
            var statusSource=input as IFactoryZeroStatusSource;
            if(statusSource!=null)statusSource.ConnectionReport=report;
            var r = new FactoryResult { Settings = s, Kind = FactoryRunKind.Pressure, ReadOnlyAcquisition = true,
                SafetyDetail = "0 kV 只读自由采集：未创建 AO/DO，未启动或关闭直流源，未发光。未检查 VMON 残压，不证明设备无电或已放电。两卡独立启动，不声明共同触发起点。" };
            var failures = new List<Exception>(); string stage = "INIT";
            Action<string,string> begin = (key, text) => { stage = key; report("STEP_" + key + "_RUNNING", text); };
            try
            {
                token.ThrowIfCancellationRequested();
                begin("INIT", "0 kV：只初始化 ART AI4 / P0.4 输入和 Pico A，不创建任何输出任务。");
                input.Initialize(s); report("STEP_INIT_PASSED", "只读两卡初始化通过。");
                // This input-only path has no HV/AO/optical output to delay.
                FactoryRunner.WaitForCaptureCooldown(s.Simulation,report,token);
                // Native input publishes real timestamped DI samples throughout
                // initialization/cooling/capture. Untimed fake/simulation input
                // is never used as a native pressure permission source.
                if(statusSource==null && s.Simulation)
                    report("SIM_"+(input.EmergencyHigh ? "EMERGENCY_RELEASED" : "EMERGENCY_ACTIVE"),
                        DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+"|模拟 P0.4；不作为实机输出许可。");
                foreach (string key in new[] { "SAFE", "AO_ZERO", "HV_ON", "HV_DELAY", "HV_UP", "HV_OFF", "ARM_DELAY", "OPTICAL", "DISCHARGE" })
                    report("STEP_" + key + "_SKIPPED", r.SafetyDetail);
                token.ThrowIfCancellationRequested();
                begin("ARM", "ART 80 kS/s 与 Pico A 分别自由有限采集；不等待 PFI0 / EXT，不发触发信号。");
                input.Arm(); report("STEP_ARM_PASSED", "两卡自由采集已启动；起点未同步。");
                begin("WAIT_TRIGGER", "等待两卡自由段完成（不是等待数字触发）。");
                long start = clock.ElapsedMilliseconds;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (input.ArtComplete && input.PicoComplete) break;
                    if (clock.ElapsedMilliseconds - start >= s.Input.TriggerTimeoutMs)
                        throw new TimeoutException("0 kV 自由采集超时；没有输出，也没有外部触发等待。");
                    clock.Pause(10, token);
                }
                report("STEP_WAIT_TRIGGER_PASSED", "两卡自由采集完整结束；不声明同步触发。");
                begin("READ", "读取完整 AI4 电压和 Pico 原始 ADC；检波为 80 kS/s，不删除真实信号。");
                r.Capture = input.ReadCapture(); r.Capture.ValidateCapture();
                FactoryZeroVoltageObservation.Evaluate(r,report);
                report("STEP_READ_PASSED", "完整点数 / 时基已校验，P0.4 高低不影响只读采集。");
                token.ThrowIfCancellationRequested();
                if (s.DesktopPostProcessing)
                {
                    r.FullMetricsDeferred = true;
                    r.Metrics = new FactoryMetrics { RecommendedRangeIndex = s.Input.RangeIndex,
                        Detail = "Full metrics deferred to the desktop saved-record pipeline." };
                    report("POST_PROCESSING_DEFERRED", "Raw input capture retained; desktop processing remains pending.");
                }
                else r.Metrics = FactoryAnalysis.Analyze(r);
            }
            catch (Exception ex) { failures.Add(ex); }
            finally
            {
                try { input.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0)
            {
                // Only a single cooperative cancellation plus successful input
                // disposal is eligible. The resident server still checks current
                // request, supervision and session/DI health before any receipt.
                if(failures.Count==1&&failures[0] is OperationCanceledException&&token.IsCancellationRequested)
                    throw new FactoryInputStopCompletedException(r);
                report("STEP_" + stage + "_ERROR", failures[0].Message);
                report("STEP_COMPLETE_ERROR", "只读采集/清理失败，没有执行输出；不确认放电。");
                throw new FactoryRunException(r, failures);
            }
            if(token.IsCancellationRequested)throw new FactoryInputStopCompletedException(r);
            report("STEP_COMPLETE_PASSED", "0 kV 输入采集及输入资源释放完成；未确认放电，未验证 HV / AO / 光触发。");
            r.Completed = true; return r;
        }
    }

    internal sealed class NativeFactoryZeroInput : IFactoryZeroInput,IFactoryZeroStatusSource
    {
        private readonly InputOnlyArt art = new InputOnlyArt();
        private readonly PicoInputCapture pico = new PicoInputCapture();
        private FactorySettings settings;
        private FactoryZeroEmergencyStatus status;
        public Action<string,string> ConnectionReport { private get; set; }
        public void Initialize(FactorySettings requested)
        {
            if (requested.Simulation || requested.TargetKv != 0) throw new ArgumentException("仅实机 0 kV 只读输入。");
            settings = requested.Clone(); settings.Validate(FactoryRunKind.Pressure);
            NativeFactoryBackend.ValidatePhysicalContract(settings.Input);
            art.Prepare(settings.Input, true, !settings.ManualZeroInputWithoutEmergency, !settings.ManualZeroInputWithoutEmergency);
            if(!settings.ManualZeroInputWithoutEmergency)
            {
                status=new FactoryZeroEmergencyStatus(art.ReadSafetyInput,ConnectionReport);
                status.Start();
            }
            pico.Initialize(settings.Input, true);
        }
        public bool EmergencyHigh { get { if(status==null)throw new InvalidOperationException("人工0 kV未采集急停状态，不能作为输出许可。");return status.ReadCurrent(); } }
        public void Arm() { if(status!=null)status.ReadCurrent();pico.Arm(); art.Arm(); }
        public bool ArtComplete { get { if(status!=null)status.CheckFault();return art.AvailableSamples() == settings.Input.ExpectedArtCount; } }
        public bool PicoComplete { get { if(status!=null)status.CheckFault();pico.CheckCompletionStatus(); return pico.Ready; } }
        public BenchCapture ReadCapture()
        {
            if (!ArtComplete || !PicoComplete) throw new InvalidOperationException("只读两卡尚未完整结束。");
            double[] voltage = art.Read(100); PicoBlockCapture pd = pico.ReadCompleted();
            return new BenchCapture { Settings = settings.Input, VoltageRaw = voltage, PdAdc = pd.RawAdc,
                ArtRateHz = art.ActualRate, PicoRateHz = 1e9 / pd.SampleIntervalNanoseconds,
                MaximumAdc = pd.MaximumAdc, PicoFullScaleV = pd.FullScaleVolts, HardwareBits = pd.RangeHardwareBits,
                Overflow = pd.HardwareOverflow, Simulation = false, ArtSerial = art.Serial, PicoSerial = pd.Serial,
                CapturedUtc = DateTime.UtcNow, Runtime = "ZERO_INPUT_ONLY; FREE_RUN; NO_COMMON_TRIGGER | ART: " + art.RuntimePath + " | Pico: " + Pico5000aNative.LoadedPath };
        }
        public void Dispose()
        {
            var errors = new List<Exception>();
            bool readerStopped=true;
            try { if(status!=null)status.Dispose(); } catch (Exception ex) { readerStopped=false;errors.Add(ex); }
            // Never clear a DI task while its native read may still be running.
            if(readerStopped)try { art.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            try { pico.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("只读输入资源释放失败。", errors);
        }
    }

    // Same retained DI task, the existing 10/300/500 ms policy and one native
    // reader. This publisher only copies cached samples; it performs no SDK IO.
    internal sealed class FactoryZeroEmergencyStatus : IDisposable
    {
        private readonly FactoryEmergencyMonitor monitor;
        private readonly Action<string,string> report;
        private readonly ManualResetEvent stop=new ManualResetEvent(false);
        private readonly string sessionId=Guid.NewGuid().ToString("N");
        private Thread publisher;
        private Exception publicationError;
        private bool disposed;
        internal FactoryZeroEmergencyStatus(Func<int,bool> read,Action<string,string> report)
        {
            this.report=report??delegate{};
            monitor=new FactoryEmergencyMonitor(read,()=>{},null);
        }
        internal void Start()
        {
            // LOW is a valid read-only observation, not a high-voltage request.
            monitor.EnableIdleTimeoutRetry();monitor.Start();
            Publish();
            publisher=new Thread(()=>{while(!stop.WaitOne(100))try{Publish();}catch(Exception e){publicationError=e;return;}})
                {IsBackground=true,Name="0kV DI status publisher (no SDK)"};
            publisher.Start();
        }
        private void Publish()
        {
            string detail;DateTime sampleUtc;string state=monitor.ConnectionState(out detail,out sampleUtc);
            bool valid=state=="HIGH"||state=="LOW";
            report(valid?(state=="HIGH"?"EMERGENCY_RELEASED":"EMERGENCY_ACTIVE"):"EMERGENCY_UNKNOWN",
                (valid?sampleUtc:DateTime.UtcNow).ToString("O",CultureInfo.InvariantCulture)+"|"+detail+
                "; ZeroSessionId="+sessionId+"; DiTaskGeneration=1; Source=ZERO_INPUT_ONLY; "+
                (valid?"采样时间为本次DI读取完成的主机UTC，非发布时刻。":"状态无效；历史输入不作许可。"));
        }
        internal bool ReadCurrent(){CheckFault();return monitor.ReadReleased();}
        internal void CheckFault()
        {
            if(publicationError!=null)throw new InvalidOperationException("0 kV急停状态发布失败。",publicationError);
            string detail;if(monitor.ConnectionState(out detail)=="ERROR")throw new InvalidOperationException(detail);
        }
        public void Dispose()
        {
            if(disposed)return;
            stop.Set();
            if(publisher!=null&&!publisher.Join(1000))throw new InvalidOperationException("0 kV状态发布线程尚未退出；保留DI任务。");
            monitor.Dispose(); // waits for the sole read before task cleanup
            disposed=true;stop.Dispose();
        }
    }

    internal sealed class SimulationFactoryZeroInput : IFactoryZeroInput
    {
        private readonly SimulationFactoryBackend inner;
        internal SimulationFactoryZeroInput(IFactoryClock clock) { inner = new SimulationFactoryBackend(clock); }
        public void Initialize(FactorySettings s) { inner.Initialize(s, false); }
        public bool EmergencyHigh { get { return true; } }
        public void Arm() { inner.Arm(true); }
        public bool ArtComplete { get { return inner.ArtComplete; } }
        public bool PicoComplete { get { return inner.PicoComplete; } }
        public BenchCapture ReadCapture()
        { var c = inner.ReadCapture(); c.Runtime += " | ZERO_INPUT_ONLY; FREE_RUN; NO_COMMON_TRIGGER"; return c; }
        public void Dispose() { inner.StopCapture(); }
    }
}
