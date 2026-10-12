using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Bounded, nonblocking handoff to a disk writer. A full queue or write error
    // is an explicit fault, never a reason to silently drop samples or keep HV on.
    internal sealed class FactoryAiTrace : IDisposable
    {
        private sealed class Item
        {
            internal double[] Raw;
            internal long ReadMs, FirstIndex;
            internal int Segment;
            internal double Ao;
            internal bool Hv;
            internal DateTime Utc;
            internal string Stage, Event;
        }
        private readonly BlockingCollection<Item> queue = new BlockingCollection<Item>(2048);
        private readonly FactorySettings settings;
        private readonly IFactoryClock clock;
        private readonly Task worker;
        private readonly StreamWriter rawWriter, eventWriter;
        private volatile Exception failure;
        private long index;
        private int segment;
        private double ao;
        private bool hv, closed;
        private string stage = "INITIALIZING";
        internal FactoryAiTrace(string directory, FactorySettings settings, IFactoryClock clock)
        {
            this.settings = settings.Clone(); this.clock = clock;
            Directory.CreateDirectory(directory);
            rawWriter = Open(Path.Combine(directory,"ai-monitor-raw.csv"));
            try { eventWriter = Open(Path.Combine(directory,"ai-monitor-events.csv")); }
            catch { rawWriter.Dispose(); throw; }
            rawWriter.WriteLine("segment,sample_index,segment_sample_ms,host_read_elapsed_ms,host_read_utc,stage_at_read,ao_command_at_read_V,hv_command_at_read,AI0_IMON_raw_V,AI2_VMON_raw_V,AI4_divider_raw_V,IMON_mA,VMON_kV,divider_signed_kV,simulation");
            eventWriter.WriteLine("host_elapsed_ms,host_utc,event,ao_command_V,hv_command,simulation");
            eventWriter.WriteLine("0," + DateTime.UtcNow.ToString("O") + ",\"START: raw SDK batches before validation; segment index at nominal 1000 S/s; host read time and command state are NOT per-sample hardware timestamps; no filtering/offset correction. Capture gaps and unread stop-tail are not fabricated. Simulation stores observations only, not 1000 S/s.\",0,False," + settings.Simulation);
            rawWriter.Flush(); eventWriter.Flush();
            worker = Task.Factory.StartNew(WriteLoop,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        private static StreamWriter Open(string path)
        { return new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(true),65536); }
        private static string Number(double v) { return v.ToString("R",CultureInfo.InvariantCulture); }
        private static string Csv(string v) { return "\"" + (v ?? "").Replace("\"","\"\"").Replace("\r"," ").Replace("\n"," ") + "\""; }
        private Item NewItem() { return new Item { ReadMs=clock.ElapsedMilliseconds,Utc=DateTime.UtcNow,Segment=segment,FirstIndex=index,Ao=ao,Hv=hv,Stage=stage }; }
        private void Submit(Item item)
        {
            if (closed) throw new ObjectDisposedException("FactoryAiTrace");
            if (failure != null) throw new IOException("AI原始记录写入失败；停止本轮，不继续加压。",failure);
            if (!queue.TryAdd(item)) throw new IOException("AI原始记录队列已满；本轮记录不完整，停止加压。未等待磁盘阻塞控制线程。");
        }
        internal void Event(string name)
        { var item=NewItem(); item.Event=name; Submit(item); }
        internal void EventAt(string name, long elapsed, DateTime utc)
        { var item=NewItem(); item.ReadMs=elapsed; item.Utc=utc; item.Event=name; Submit(item); }
        internal void Stage(string key)
        {
            if (key.EndsWith("_RUNNING",StringComparison.Ordinal) || key.EndsWith("_ERROR",StringComparison.Ordinal))
            { stage=key; Event(key); }
        }
        internal void Command(string name, double? aoValue = null, bool? hvValue = null)
        { if (aoValue.HasValue) ao=aoValue.Value; if (hvValue.HasValue) hv=hvValue.Value; Event(name); }
        internal void Monitor(bool started)
        {
            if (started) { segment++; index=0; }
            Event(started ? "MONITOR_STARTED: new sample segment" : "MONITOR_STOPPED: no fabricated samples across gap; unread tail may remain");
        }
        internal void Raw(double[] values, int count)
        {
            if (values == null || count < 0 || count > values.Length/3) throw new InvalidDataException("AI日志收到无效的SDK点数。");
            if (count == 0) { Event("SDK_READ_ZERO_SAMPLES"); return; }
            var item=NewItem(); item.Raw=new double[count*3]; Array.Copy(values,item.Raw,item.Raw.Length);
            Submit(item); index+=count;
        }
        private void WriteLoop()
        {
            try
            {
                long lastFlush=Environment.TickCount;
                while (!queue.IsCompleted)
                {
                    Item item;
                    if (queue.TryTake(out item,100))
                    {
                        if (item.Raw == null)
                            eventWriter.WriteLine(item.ReadMs + "," + item.Utc.ToString("O") + "," + Csv(item.Event) + "," + Number(item.Ao) + "," + item.Hv + "," + settings.Simulation);
                        else for (int i=0; i<item.Raw.Length/3; i++)
                        {
                            double im=item.Raw[i*3], vm=item.Raw[i*3+1], div=item.Raw[i*3+2];
                            rawWriter.WriteLine(item.Segment + "," + (item.FirstIndex+i) + "," +
                                (settings.Simulation ? "" : Number((item.FirstIndex+i)*1000.0/FactoryArtFeedbackPlan.RateHz)) + "," + item.ReadMs + "," + item.Utc.ToString("O") + "," + Csv(item.Stage) + "," +
                                Number(item.Ao) + "," + item.Hv + "," + Number(im) + "," + Number(vm) + "," + Number(div) + "," +
                                Number(im*settings.ImonFullScaleMa/settings.FeedbackFullScaleV) + "," + Number(vm*settings.VmonFullScaleKv/settings.FeedbackFullScaleV) + "," +
                                Number(div*settings.Input.DividerVPerV/1000) + "," + settings.Simulation);
                        }
                    }
                    if (Environment.TickCount-lastFlush >= 100 || queue.Count == 0)
                    { rawWriter.Flush(); eventWriter.Flush(); lastFlush=Environment.TickCount; }
                }
            }
            catch (Exception ex) { failure=ex; }
            finally
            {
                try { rawWriter.Dispose(); } catch (Exception ex) { failure=ex; }
                try { eventWriter.Dispose(); } catch (Exception ex) { failure=ex; }
            }
        }
        public void Dispose()
        {
            if (closed) return; closed=true; queue.CompleteAdding();
            if (!worker.Wait(5000)) throw new IOException("AI记录写盘未在5秒内结束；记录完整性未确认。硬件清理已先行尝试。");
            if (failure != null) throw new IOException("AI原始记录不完整：写盘失败。",failure);
        }
    }
    // Logging must NEVER prevent a requested zero/OFF/cleanup operation.
    // Enabling commands require a healthy logger; disabling commands execute first.
    internal sealed class FactoryAiRecordingBackend : IFactoryBackend, IFactoryFaultMonitor, IFactoryOpticalRecovery, IFactoryFinalAi4Observation
    {
        private readonly IFactoryBackend inner;
        private readonly NativeFactoryBackend native;
        private readonly FactoryAiTrace trace;
        private readonly FactoryShutdownDiagnostic diagnostic;
        private readonly IFactoryClock clock;
        private readonly string directory;
        private readonly Action<string,string> report;
        private readonly bool opticalOnly;
        private readonly bool leaveDeviceOpen;
        private bool restoreMonitorAfterCapture = true;
        private bool skipPressureResidual;
        private bool closed, hvAttempted, monitorAvailable;
        internal FactoryAiRecordingBackend(IFactoryBackend inner, string directory, FactorySettings settings, IFactoryClock clock, Action<string,string> report = null, bool leaveDeviceOpen = false)
        {
            if(leaveDeviceOpen && !(inner is NativeFactoryBackend))throw new ArgumentException("常驻会话只适用于真实设备后端。");
            this.leaveDeviceOpen=leaveDeviceOpen;
            this.clock=clock; this.directory=directory; this.report=report; opticalOnly=settings.ZeroSynchronizedOptical;
            skipPressureResidual=settings.SkipPressureResidualMeasurement;
            this.inner=inner; trace=new FactoryAiTrace(directory,settings,clock); native=inner as NativeFactoryBackend;
            diagnostic=new FactoryShutdownDiagnostic(settings,clock);
            if (native != null) { native.RawFeedbackRead=Raw; native.MonitorChanged=Monitor; }
        }
        private void Raw(double[] values,int count) { diagnostic.Raw(values,count); trace.Raw(values,count); }
        private void Monitor(bool start) { monitorAvailable=start; diagnostic.Monitor(start); trace.Monitor(start); }
        internal void ObserveStage(string key)
        {
            if (closed) return;
            if (key.Contains("HV_UP") || key.Contains("HV_OFF") || key.Contains("ARM") || key.Contains("ERROR")) diagnostic.Event(key);
            trace.Stage(key);
        }
        public void Initialize(FactorySettings s, bool calibration)
        { skipPressureResidual=!calibration&&s.SkipPressureResidualMeasurement;restoreMonitorAfterCapture = NativeFactoryBackend.RestoreMonitorAfterCapture(s,calibration,false); trace.Event("INITIALIZE_REQUEST"); inner.Initialize(s,calibration); if (native == null) Monitor(NativeFactoryBackend.StartMonitorBeforeRamp(s,calibration)); trace.Event("INITIALIZE_DONE"); }
        public FactoryFeedback ReadFeedback()
        {
            if(native==null&&skipPressureResidual&&!monitorAvailable)Monitor(true);
            var f=inner.ReadFeedback();
            if (native == null) Raw(new[] { f.ImonRawV,f.VmonRawV,f.DividerRawV },1);
            return f;
        }
        public bool ReadEmergencyReleased() { return inner.ReadEmergencyReleased(); }
        public void PrepareFaultMonitoring()
        {
            if(skipPressureResidual)throw new InvalidOperationException("故障后自动残压测量已禁用；不启动AI。");
            var monitor=inner as IFactoryFaultMonitor;
            if(monitor==null)throw new InvalidOperationException("当前后端不支持故障只读监测；残压未知。");
            monitor.PrepareFaultMonitoring();
            if(native==null)Monitor(true);
            trace.Stage("STEP_DISCHARGE_RUNNING");
        }
        public FactoryFeedback ReadFaultFeedback()
        {
            if(skipPressureResidual)throw new InvalidOperationException("故障后自动残压读取已禁用。");
            var monitor=inner as IFactoryFaultMonitor;
            if(monitor==null)throw new InvalidOperationException("当前后端不支持故障只读监测；残压未知。");
            var f=monitor.ReadFaultFeedback();
            if(native==null && f!=null)Raw(new[]{f.ImonRawV,f.VmonRawV,f.DividerRawV},1);
            return f;
        }
        public void WriteAo(double v)
        {
            if(v>0) trace.Event("AO_REQUEST="+v.ToString("R",CultureInfo.InvariantCulture));
            long start=clock.ElapsedMilliseconds;
            inner.WriteAo(v);
            if(v==0) { diagnostic.EventAt("AO_ZERO_REQUEST (software)",start); diagnostic.Event("AO_ZERO_RETURNED (not physical readback)"); }
            trace.Command("AO_RETURNED",v);
        }
        public void SetHv(bool high)
        {
            if (!high) { Off(() => inner.SetHv(false),"HV_OFF",false); return; }
            trace.Event("HV_ON_REQUEST"); hvAttempted=true; diagnostic.Event("HV_ON_ATTEMPT");
            inner.SetHv(true); if (native == null && !monitorAvailable && !skipPressureResidual) Monitor(true); trace.Command("HV_ON_RETURNED",null,true);
        }
        public void SetOptical(bool high)
        { if(high) trace.Event("OPTICAL_HIGH_REQUEST"); inner.SetOptical(high); trace.Event(high ? "OPTICAL_HIGH_RETURNED" : "OPTICAL_LOW_RETURNED"); }
        public void Arm(bool free)
        { trace.Event("ARM_REQUEST: AI0/2/4 monitoring paused for finite AI4 capture"); inner.Arm(free); if(native==null) Monitor(false); }
        public void ArmBeforeShutdown()
        {
            trace.Event("ARM_BEFORE_OFF_REQUEST: stop/clear monitor then arm finite AI4 and Pico; no AO increments");
            inner.ArmBeforeShutdown(); if(native==null) Monitor(false);
            diagnostic.Event("FINITE_AND_PICO_ARMED_BEFORE_OFF"); trace.Event("ARM_BEFORE_OFF_RETURNED");
        }
        public bool ArtComplete { get { return inner.ArtComplete; } }
        public bool PicoComplete { get { return inner.PicoComplete; } }
        public BenchCapture ReadCapture() { return inner.ReadCapture(); }
        public FactoryFinalAi4Observation ReadFinalAi4Observation(System.Threading.CancellationToken token)
        {
            if(skipPressureResidual)throw new InvalidOperationException("加压后自动残压测量已禁用；不新采末轮AI4。");
            var reader=inner as IFactoryFinalAi4Observation;
            if(reader==null)throw new InvalidOperationException("当前后端不支持末轮AI4独立均值观察。");
            trace.Event("FINAL_AI4_MEAN_REQUEST: new AI4-only 10ms segment, no output permission");
            var result=reader.ReadFinalAi4Observation(token);
            trace.Event("FINAL_AI4_MEAN_RETURNED: "+result.Describe());
            return result;
        }
        public void StopCapture() { inner.StopCapture(); if(native==null) Monitor(restoreMonitorAfterCapture); }
        public void PrepareOpticalRetry()
        {
            var recovery=inner as IFactoryOpticalRecovery;
            if(recovery==null)throw new InvalidOperationException("当前后端不支持双卡隔离后的光补发。");
            trace.Event("OPTICAL_RETRY_REARM_REQUEST: discard both old finite blocks; no HV restart");
            recovery.PrepareOpticalRetry(); if(native==null)Monitor(false);
            trace.Event("OPTICAL_RETRY_REARM_RETURNED");
        }
        public void Shutdown()
        { Off(inner.Shutdown,opticalOnly ? "SHUTDOWN: optical LOW only; no HV/AO commands" : "SHUTDOWN: optical LOW -> HV OFF -> AO0=0",!opticalOnly); }
        private void Off(Action disable,string name,bool zero)
        {
            long requested=clock.ElapsedMilliseconds;
            DateTime requestedUtc=DateTime.UtcNow;
            Exception outputError=null;
            // NO logger, allocation of a record, AI read or delay before disable.
            try { disable(); } catch(Exception ex) { outputError=ex; }
            long returned=clock.ElapsedMilliseconds;
            bool started=hvAttempted && diagnostic.Trigger(requested,returned,name,outputError==null);
            var errors=new List<Exception>(); if(outputError!=null) errors.Add(outputError);
            try
            {
                trace.EventAt(name+"_REQUEST (host command boundary, not physical)",requested,requestedUtc);
                if(outputError==null) trace.Command(name+"_RETURNED: not proof of discharge",zero?(double?)0:null,false);
                else trace.Event(name+"_FAILED: output state uncertain");
            }
            catch(Exception ex) { errors.Add(ex); }
            if(started)
            {
                if(outputError!=null) diagnostic.Finish("关断调用报错；不等待诊断，继续其他清理",false);
                else if(skipPressureResidual||!restoreMonitorAfterCapture)
                {
                    diagnostic.Finish("SHUTDOWN_AI_SKIPPED：不追加关断后AI/残压读取；保留实际命令时间与结果，电压未知，须现场独立安全放电",false);
                    trace.Event("SHUTDOWN_AI_SKIPPED: no post-shutdown acquisition; voltage/discharge unknown");
                }
                else if(!(native==null ? monitorAvailable : native.DiagnosticMonitorAvailable))
                    diagnostic.Finish("三路AI已停止/清除，关断后三路数据不可用；两卡已改为有限段等待检测触发，不能伪造连续三路记录",false);
                else if(errors.Count>0) diagnostic.Finish("记录器故障；输出关断已先行尝试",false);
                else
                {
                    try
                    {
                        FactoryShutdownReadWindow.Run(clock,() => { if(native!=null) native.ReadShutdownDiagnostic(); else ReadFeedback(); });
                        diagnostic.Finish("关断后500 ms只读窗口已执行；请核对实际点数/空档/非有限值",true);
                    }
                    catch(Exception ex) { diagnostic.Finish(ex.Message,false); errors.Add(ex); }
                }
            }
            if(errors.Count>0) throw new AggregateException("关断或只读诊断异常；不继续加压/发光。",errors);
        }
        public void Dispose()
        {
            if(closed) return; closed=true; var errors=new List<Exception>();
            try { Shutdown(); } catch(Exception ex) { errors.Add(ex); }
            try { if(leaveDeviceOpen)native.EndRound();else inner.Dispose(); } catch(Exception ex) { errors.Add(ex); }
            if(native!=null) { native.RawFeedbackRead=null; native.MonitorChanged=null; }
            try { trace.Dispose(); } catch(Exception ex) { errors.Add(ex); }
            try { string path=diagnostic.Save(directory); if(path!=null && report!=null) report("SHUTDOWN_DIAG_FILE",path); }
            catch(Exception ex) { errors.Add(ex); }
            if(errors.Count>0) throw new AggregateException("硬件释放或AI原始记录完整性未确认。",errors);
        }
    }
}
