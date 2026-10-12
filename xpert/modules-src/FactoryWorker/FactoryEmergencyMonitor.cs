using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // One thread owns DI reads. A separate managed observer latches the total
    // deadline even while native ReadDigital is blocked; it never calls an SDK,
    // logs, clears a task, or writes an output. The serialized owner shuts down.
    internal sealed class FactoryEmergencyMonitor : IDisposable
    {
        internal const int RequestedPeriodMs = FactoryEmergencyReadPolicy.AfterReturnDelayMs;
        internal const int MaximumAgeMs = FactoryEmergencyReadPolicy.OutputFreshnessMs;
        private readonly Func<int,bool> readInput;
        private readonly Action signalFault;
        private readonly Func<FactoryArtCommunicationSnapshot> communicationSnapshot;
        private readonly object gate = new object();
        private readonly ManualResetEvent stopping = new ManualResetEvent(false);
        private readonly ManualResetEvent firstRead = new ManualResetEvent(false);
        private readonly DateTime clockOriginUtc = DateTime.UtcNow;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Thread reader, deadlineObserver;
        private bool started, disposed, hasSample, sampleHigh, lowLatched, notified, idleLowObserved;
        private long sampleAt, startedAt;
        private Exception error;
        private string errorDiagnostic;
        private bool idleRetryEnabled, retryPending, retryExhausted, deferIdleRebuild;
        private int readModeGeneration, sampleGeneration;
        private string retryFirstDetail;
        private readonly Queue<KeyValuePair<string,string>> retryEvents = new Queue<KeyValuePair<string,string>>();
        internal bool IdleTimeoutRebuildRequired { get { lock(gate) return retryExhausted; } }
        internal bool IdleLowObserved { get { lock(gate) return idleLowObserved; } }
        internal bool OutputStopLatched { get { lock(gate) return lowLatched; } }
        internal bool HasReadFailure { get { lock(gate) return error!=null; } }
        internal void AllowOneIdleRebuildDeferral() { lock(gate) deferIdleRebuild=true; }

        internal FactoryEmergencyMonitor(Func<bool> readInput, Action signalFault)
            : this(readInput == null ? (Func<int,bool>)null : budget => readInput(),signalFault,null) { }
        internal FactoryEmergencyMonitor(Func<int,bool> readInput, Action signalFault,
            Func<FactoryArtCommunicationSnapshot> communicationSnapshot)
        {
            if (readInput == null || signalFault == null) throw new ArgumentNullException();
            this.readInput=readInput; this.signalFault=signalFault;
            this.communicationSnapshot=communicationSnapshot;
        }
        internal void EnableIdleTimeoutRetry()
        {
            lock(gate)
            {
                if(disposed || stopping.WaitOne(0) || error!=null || lowLatched)
                    throw new InvalidOperationException("P0.4故障/停止状态不能启用空闲监控。");
                idleRetryEnabled=true; ++readModeGeneration;
            }
        }
        internal void EnterActiveReadMode() { EnterActiveReadMode(false); }
        internal void EnterActiveReadMode(bool batchContinuation)
        {
            lock(gate)
            {
                if(retryPending || retryExhausted)throw new FactoryEmergencyRetryPendingException();
                if(batchContinuation && idleLowObserved)throw new InvalidOperationException("轮间急停已动作，不能自动续轮。");
                idleRetryEnabled=false; idleLowObserved=false; ++readModeGeneration;
            }
            // Wait outside gate: only a read begun in the new mode can permit
            // output. An old idle callback cannot authorize a new round.
            if(!ReadReleased())throw new FactoryEmergencyStopException();
        }
        internal void DrainRetryEvents(Action<string,string> report)
        {
            if(report==null)return;
            while(true)
            {
                KeyValuePair<string,string> item;
                lock(gate){if(retryEvents.Count==0)return;item=retryEvents.Dequeue();}
                report(item.Key,item.Value);
            }
        }
        internal bool? ReadIdleReleased()
        {
            lock(gate)
            {
                if(retryExhausted)return null; // fault remains latched; owner may only rebuild DI read-only
                if(error==null && !lowLatched && (retryPending || !hasSample ||
                    !FactoryEmergencyReadPolicy.IsFresh(clock.ElapsedMilliseconds,sampleAt)))return null;
            }
            return ReadReleased();
        }
        internal void Start()
        {
            lock(gate)
            {
                if(started || disposed)throw new InvalidOperationException("P0.4监控不能重复启动或复用。");
                started=true; startedAt=clock.ElapsedMilliseconds;
                deadlineObserver=new Thread(ObserveDeadline){IsBackground=true,Name="P0.4 independent deadline"};
                reader=new Thread(ReadLoop){IsBackground=true,Name="P0.4 single DI owner"};
                deadlineObserver.Start(); reader.Start();
            }
            // Startup gets no longer permission budget than an active round.
            if(!firstRead.WaitOne(FactoryEmergencyReadPolicy.NoValidDataDeadlineMs))
                LatchExpiredDeadline();
            lock(gate){if(retryExhausted)return;}
            ReadReleased();
        }
        private long DeadlineOrigin { get { return hasSample ? sampleAt : startedAt; } }
        private void ObserveDeadline()
        {
            while(!stopping.WaitOne(RequestedPeriodMs))
            {
                bool expired;
                lock(gate)
                {
                    if(error!=null)return;
                    expired=FactoryEmergencyReadPolicy.DeadlineReached(clock.ElapsedMilliseconds,DeadlineOrigin);
                }
                if(expired)
                {
                    if(LatchExpiredDeadline())return;
                }
            }
        }
        private void ReadLoop()
        {
            try
            {
                while(!stopping.WaitOne(0))
                {
                    long begin=clock.ElapsedMilliseconds;
                    int mode,budget;
                    lock(gate)
                    {
                        if(error!=null || retryExhausted)break;
                        mode=readModeGeneration;
                        budget=FactoryEmergencyReadPolicy.ReadBudgetMs(begin,DeadlineOrigin);
                    }
                    if(budget<=0){LatchError(new TimeoutException("P0.4有效数据总期限已到，禁止开始另一读取。"));break;}
                    bool high;
                    try { high=readInput(budget); }
                    catch(Exception ex)
                    {
                        long ended=clock.ElapsedMilliseconds;
                        if(stopping.WaitOne(0))break;
                        if(TryRetry(ex,begin,ended,mode))
                        { if(WaitAfterReturn())break;continue; }
                        lock(gate)
                        {
                            var timeout=ex as FactoryEmergencyReadTimeoutException;
                            if(timeout!=null && FactoryEmergencyReadPolicy.IsRetryableStatus(timeout.DriverStatus) &&
                                retryPending && deferIdleRebuild && idleRetryEnabled && mode==readModeGeneration && !lowLatched)
                                retryExhausted=true;
                        }
                        LatchError(ex,DescribeRead(begin,ended,null));break;
                    }
                    long returnedAt=clock.ElapsedMilliseconds;
                    if(stopping.WaitOne(0))break;
                    bool notify=false,expired=false;
                    lock(gate)
                    {
                        // A late HIGH or a mode change never erases a fault.
                        if(error!=null)break;
                        expired=FactoryEmergencyReadPolicy.DeadlineReached(returnedAt,DeadlineOrigin);
                        // A physical LOW in read-only idle is a valid observation,
                        // not a communications failure. Active LOW (including an
                        // in-flight idle read crossing the active boundary) still
                        // latches and cannot be cleared by a later HIGH.
                        if(!high && (!idleRetryEnabled || mode!=readModeGeneration))
                        {lowLatched=true;notify=MarkNotified();}
                        if(!expired && (mode==readModeGeneration || !high))
                        {
                            if(!high && !lowLatched)
                            {
                                idleLowObserved=true;
                                if(!hasSample || sampleHigh)QueueRetryEvent("P04_IDLE_ACTIVE",
                                    "急停按下；保持只读监测，撤销续轮授权。"+DescribeRead(begin,returnedAt,false));
                            }
                            sampleHigh=high;
                            hasSample=true;sampleAt=returnedAt;sampleGeneration=mode;
                            if(retryPending)QueueRetryEvent("P04_RETRY_RECOVERED",retryFirstDetail+
                                "; retryReturnUtc="+HostUtc(returnedAt)+"；当前P0.4已回读；未恢复已撤销的批次授权。");
                            retryPending=false;
                        }
                    }
                    if(notify)signalFault();
                    // LOW revokes output permanently for this monitor, but it
                    // does not terminate the sole read-only display reader.
                    if(notify)firstRead.Set();
                    if(expired){LatchError(new TimeoutException("P0.4返回超过500 ms总期限，不接受迟到高电平。"),DescribeRead(begin,returnedAt,high));break;}
                    lock(gate){if(hasSample && sampleGeneration==readModeGeneration)firstRead.Set();}
                    // The interval begins AFTER return, including timeout paths.
                    if(WaitAfterReturn())break;
                }
            }
            catch(Exception ex){LatchError(ex);}
            finally{firstRead.Set();}
        }
        private bool TryRetry(Exception failure,long began,long ended,int mode)
        {
            var timeout=failure as FactoryEmergencyReadTimeoutException;
            if(timeout==null || !FactoryEmergencyReadPolicy.IsRetryableStatus(timeout.DriverStatus))return false;
            FactoryArtCommunicationSnapshot other=communicationSnapshot==null ? null : communicationSnapshot();
            lock(gate)
            {
                if(retryPending || retryExhausted || retryEvents.Count>250 || error!=null || lowLatched ||
                    mode!=readModeGeneration || stopping.WaitOne(0) ||
                    FactoryEmergencyReadPolicy.ReadBudgetMs(ended+RequestedPeriodMs,DeadlineOrigin)<=0 ||
                    (other!=null && other.HasFailure))return false;
                // Other-port UNKNOWN is not HIGH. The exact DI status permits
                // one input-only retry, not output or a fault/batch reset.
                retryPending=true;
                retryFirstDetail="ART="+timeout.DriverStatus+"/"+FactoryEmergencyReadPolicy.OfficialStatusName(timeout.DriverStatus)+
                    "; firstTimeoutUtc="+HostUtc(ended)+"; "+(other==null?"OtherART=UNKNOWN":other.Describe())+
                    DescribeRead(began,ended,null);
                QueueRetryEvent("P04_RETRY_PENDING",retryFirstDetail+"；一次串行重读，总500 ms期限不重置；禁止新增输出。");
                return true;
            }
        }
        private bool WaitAfterReturn()
        {
            // Windows wait timeout quantization can return slightly before its
            // nominal millisecond duration. Require actual monotonic elapsed
            // ticks; each remaining wait is rounded UP and cancellation wakes
            // the event immediately. This is not a busy-spin delay.
            long deadline=Stopwatch.GetTimestamp()+(long)Math.Ceiling(Stopwatch.Frequency*(RequestedPeriodMs/1000.0));
            while(true)
            {
                long remaining=deadline-Stopwatch.GetTimestamp();
                if(remaining<=0)return stopping.WaitOne(0);
                int wait=Math.Max(1,(int)Math.Ceiling(remaining*1000.0/Stopwatch.Frequency));
                if(stopping.WaitOne(wait))return true;
            }
        }
        internal bool ReadReleased()
        {
            while(true)
            {
                Exception failure;string diagnostic;bool low,expired;
                lock(gate)
                {
                    if(!started || disposed || stopping.WaitOne(0))throw new InvalidOperationException("P0.4监控未启动、已停止或已释放。");
                    failure=error;diagnostic=errorDiagnostic;low=lowLatched;
                    expired=FactoryEmergencyReadPolicy.DeadlineReached(clock.ElapsedMilliseconds,DeadlineOrigin);
                    if(failure==null && !low && !expired && !retryPending && !retryExhausted && hasSample &&
                        sampleGeneration==readModeGeneration && FactoryEmergencyReadPolicy.IsFresh(clock.ElapsedMilliseconds,sampleAt))return sampleHigh;
                }
                if(failure!=null)throw new InvalidOperationException("P0.4读取故障已锁存，禁止继续输出。"+diagnostic,failure);
                if(low)return false;
                if(expired){LatchExpiredDeadline();continue;}
                // No gate is held. Only this output command waits. DI and the
                // independent deadline observer continue and remain cancellable.
                if(stopping.WaitOne(RequestedPeriodMs))throw new OperationCanceledException("P0.4监控已停止，输出请求取消。");
            }
        }
        internal string ConnectionState(out string detail)
        {
            DateTime sampleUtc;
            return ConnectionState(out detail,out sampleUtc);
        }
        // Snapshot state and the accepted read time atomically. Publishing a
        // cached sample must never replace its timestamp with publication time.
        internal string ConnectionState(out string detail,out DateTime sampleUtc)
        {
            lock(gate)
            {
                sampleUtc=hasSample ? clockOriginUtc.AddMilliseconds(sampleAt) : DateTime.MinValue;
                detail=error==null?"P0.4独立输入读取":error.ToString()+errorDiagnostic;
                if(error!=null)return "ERROR";
                if(disposed || stopping.WaitOne(0)){detail="P0.4监控已停止；禁止输出。";return "UNKNOWN";}
                if(!hasSample){detail="尚无有效P0.4回读；禁止输出。";return "WAIT";}
                // Read-mode policy is not a new DI task/source. A still-fresh
                // physical sample on this same sole owner remains displayable
                // when a completed round returns to idle, without inventing an
                // UNKNOWN gap or a newer timestamp. ReadReleased separately
                // requires sampleGeneration==readModeGeneration before output;
                // a late old-mode HIGH is still not accepted by ReadLoop.
                long now=clock.ElapsedMilliseconds;
                if(retryPending || retryExhausted || !FactoryEmergencyReadPolicy.IsFresh(now,sampleAt))
                {
                    // A healthy, still-owned read inside the existing total
                    // deadline is not a terminal error. This state denies NEW
                    // output; ReadReleased still waits for a genuinely fresh
                    // new-mode sample. Neither freshness nor deadline changes.
                    bool pending=started&&sampleHigh&&!lowLatched&&!retryExhausted&&
                        !FactoryEmergencyReadPolicy.DeadlineReached(now,DeadlineOrigin);
                    detail="P0.4等待有效回读；历史高电平不作输出许可。"+
                        "; PendingUntilUtc="+HostUtc(DeadlineOrigin+FactoryEmergencyReadPolicy.NoValidDataDeadlineMs)+
                        "; LastAcceptedSampleUtc="+sampleUtc.ToString("o",CultureInfo.InvariantCulture);
                    return pending ? "PENDING" : "UNKNOWN";
                }
                if(lowLatched)
                {
                    detail="P0.4当前真实电平；本会话急停故障保持锁存，显示不恢复输出许可。";
                    return sampleHigh ? "LATCHED_HIGH" : "LATCHED_LOW";
                }
                return sampleHigh ? "HIGH" : "LOW";
            }
        }
        private bool MarkNotified(){if(notified)return false;notified=true;return true;}
        private string HostUtc(long elapsed){return clockOriginUtc.AddMilliseconds(elapsed).ToString("o",CultureInfo.InvariantCulture);}
        private string DescribeRead(long began,long ended,bool? value)
        {return " monitorBeginUtc="+HostUtc(began)+"; monitorEndUtc="+HostUtc(ended)+"; hostElapsedMs="+(ended-began)+
            "; returnedValue="+(value.HasValue?(value.Value?"HIGH":"LOW"):"UNKNOWN")+
            "；主机单调钟测量，非硬件时间戳，不等同物理响应时长。";}
        private void LatchError(Exception failure,string diagnostic=null)
        {
            bool notify;
            lock(gate)
            {
                if(error==null){error=failure;errorDiagnostic=diagnostic;}
                notify=MarkNotified();
            }
            // Callback is only the backend volatile latch, with no SDK/report.
            if(notify)signalFault();
            firstRead.Set();
        }
        private bool LatchExpiredDeadline()
        {
            bool notify;
            lock(gate)
            {
                if(error!=null)return true;
                // A timely sample may arrive between the observer's snapshot
                // and this lock; only the still-current deadline may latch.
                if(!FactoryEmergencyReadPolicy.DeadlineReached(clock.ElapsedMilliseconds,DeadlineOrigin))return false;
                error=new TimeoutException("P0.4连续500 ms没有有效输入；旧读取未确认退出前禁止清理或重读。");
                notify=MarkNotified();
            }
            if(notify)signalFault();
            firstRead.Set();
            return true;
        }
        private void QueueRetryEvent(string key,string detail)
        {if(retryEvents.Count<254)retryEvents.Enqueue(new KeyValuePair<string,string>(key,detail));}
        internal bool Stop(int timeoutMs)
        {
            Thread owned,observer;
            lock(gate)
            {
                if(disposed)return true;
                idleRetryEnabled=false;++readModeGeneration;stopping.Set();owned=reader;observer=deadlineObserver;
            }
            var wait=Stopwatch.StartNew();
            bool readExited=owned==null || owned.Join(Math.Max(0,timeoutMs));
            bool observerExited=observer==null || observer.Join((int)Math.Max(0,timeoutMs-wait.ElapsedMilliseconds));
            return readExited && observerExited;
        }
        public void Dispose()
        {
            if(!Stop(600))throw new InvalidOperationException("P0.4读取线程尚未退出；保留DI句柄，禁止并发清除。");
            lock(gate){if(disposed)return;disposed=true;stopping.Dispose();firstRead.Dispose();}
        }
    }
    internal sealed class FactoryEmergencyReadTimeoutException : TimeoutException
    {
        internal readonly int DriverStatus;
        internal FactoryEmergencyReadTimeoutException(int status,Exception inner)
            : base("P0.4数字读取SDK超时，ART状态="+status+"；无有效输入电平。",inner){DriverStatus=status;}
    }
    internal sealed class FactoryEmergencyRetryPendingException : InvalidOperationException
    {
        internal FactoryEmergencyRetryPendingException():base("P0.4重读中，电平未知；未开始新轮或恢复输出。"){}
    }
    internal sealed class FactoryHvCommandState
    {
        private bool mayBeOn;
        internal bool MayBeOn { get { return mayBeOn; } }
        internal bool Write(bool high, Action<bool> write)
        {
            if (!high && !mayBeOn) return false; // User-confirmed default HV OFF: no startup OFF command.
            if (high) mayBeOn = true; // Include a failed/partial ON write in mandatory cleanup.
            write(high);
            if (!high) mayBeOn = false; // Only successful OFF clears command ownership.
            return true;
        }
    }
}
