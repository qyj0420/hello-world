using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.Calibration
{
    // One owner calls the input delegate. The watchdog never calls the SDK or
    // clears a task: a managed deadline is not proof that native I/O returned.
    internal sealed class CalibrationEmergencyMonitor : IDisposable
    {
        internal const int MinimumDelayMs = 10, FirstReadTimeoutMs = 300, NoDataDeadlineMs = 500;
        readonly Func<int, bool> read;
        readonly Func<bool> retryContextAvailable;
        readonly Action<Exception> cancelRound;
        readonly Action rebuildInput;
        readonly Action<CalibrationEmergencySnapshot> changed;
        readonly object gate = new object();
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<TaskCompletionSource<bool>> waiting = new List<TaskCompletionSource<bool>>();
        readonly List<TaskCompletionSource<bool>> active = new List<TaskCompletionSource<bool>>();
        Thread reader;
        Timer watchdog;
        bool started, disposed, readerEnded, inFlight, retrying, cancellationDelivered, recoveryAttempted;
        bool? high;
        Exception failure, recoveryFailure;
        // The deadline never pauses between reads or while a notification runs.
        // Only a timely valid result moves the session's no-data baseline.
        long lastValidOrStarted, sampleSequence;

        internal CalibrationEmergencyMonitor(Func<int, bool> read, Func<bool> retryContextAvailable,
            Action<Exception> cancelRound, Action rebuildInput, Action<CalibrationEmergencySnapshot> changed)
        {
            this.read = read ?? throw new ArgumentNullException("read");
            this.retryContextAvailable = retryContextAvailable ?? (() => false);
            this.cancelRound = cancelRound ?? throw new ArgumentNullException("cancelRound");
            this.rebuildInput = rebuildInput; this.changed = changed;
        }
        internal CalibrationEmergencySnapshot Snapshot
        {
            get { lock (gate) return new CalibrationEmergencySnapshot { High =
                failure == null && !retrying && started && !disposed && !readerEnded && !stopping.WaitOne(0) &&
                clock.ElapsedMilliseconds-lastValidOrStarted<NoDataDeadlineMs ? high : null,
                Error = failure, RecoveryError = recoveryFailure, ReadInFlight = inFlight,
                RecoveryAttempted = recoveryAttempted, Sequence = sampleSequence, RetryPending = retrying }; }
        }
        internal void Start()
        {
            lock (gate)
            {
                if (started || disposed) throw new InvalidOperationException("急停读取会话不能重复启动。");
                started = true; lastValidOrStarted = clock.ElapsedMilliseconds;
                watchdog = new Timer(CheckDeadline, null, MinimumDelayMs, MinimumDelayMs);
                reader = new Thread(ReadLoop) { IsBackground = true, Name = "Calibration emergency input" };
                reader.Start();
            }
        }
        // A request is attached only to a read which STARTS after the request.
        // Neither an old level nor an in-flight read started earlier is fresh evidence.
        internal Task<bool> ReadFreshAsync()
        {
            CheckDeadline(null);
            lock (gate)
            {
                if (!started || disposed || stopping.WaitOne(0))
                    return Task.FromException<bool>(new InvalidOperationException("急停读取会话已停止。"));
                if (failure != null) return Task.FromException<bool>(failure);
                if (readerEnded)
                    return Task.FromException<bool>(new InvalidOperationException("急停读取会话已停止。"));
                if (waiting.Count + active.Count >= 32)
                    return Task.FromException<bool>(new InvalidOperationException("设备状态检查请求过多，请等待当前操作结束。"));
                var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiting.Add(request); return request.Task;
            }
        }
        internal void ThrowIfFaulted()
        {
            CheckDeadline(null);
            lock (gate)
            {
                if (failure != null) throw new InvalidOperationException("急停读取故障已锁存，请停止并重新检查连接。", failure);
                if (!started || disposed || readerEnded || stopping.WaitOne(0)) throw new InvalidOperationException("急停读取会话不可用。");
            }
        }
        void CheckDeadline(object ignored)
        {
            long baseline;
            lock (gate) baseline = lastValidOrStarted;
            Fail(new TimeoutException("急停状态在500 ms内未返回有效数据，当前校准已停止；读取未返回时仍保留设备任务。"),baseline);
        }
        void ReadLoop()
        {
            try
            {
                while (!stopping.WaitOne(0))
                {
                    lock (gate) if (failure != null) break;
                    bool succeeded = false;
                    for (int attempt = 0; attempt < 2 && !stopping.WaitOne(0); attempt++)
                    {
                        int budget;
                        lock (gate)
                        {
                            if (failure != null) break;
                            budget = (int)Math.Max(0, NoDataDeadlineMs - (clock.ElapsedMilliseconds - lastValidOrStarted));
                            budget = Math.Min(attempt == 0 ? FirstReadTimeoutMs : budget, budget);
                            active.AddRange(waiting); waiting.Clear();
                            inFlight = budget > 0;
                        }
                        if (budget <= 0) { Fail(new TimeoutException("急停状态检查已超过500 ms，禁止继续校准。")); break; }
                        bool value = false; Exception readError = null;
                        try { value = read(budget); }
                        catch (Exception error) { readError = error; }
                        bool late;
                        lock (gate) { inFlight = false; late = clock.ElapsedMilliseconds - lastValidOrStarted >= NoDataDeadlineMs; }
                        if (stopping.WaitOne(0)) break;
                        bool officialTimeout = readError is CalibrationArtException && ((CalibrationArtException)readError).IsReadTimeout;
                        if (late) Fail(new TimeoutException("急停状态检查总时长已达500 ms，迟到数据不作为校准许可。", readError));
                        if (attempt == 0 && officialTimeout && !late && Snapshot.Error == null && retryContextAvailable())
                        {
                            lock (gate) { retrying = true; high = null; }
                            Notify();
                            // The first call returned. There is no concurrent read,
                            // and this wait does not reset the original 500 ms budget.
                            if (WaitMinimumDelay()) break;
                            continue;
                        }
                        if (readError != null)
                        {
                            Fail(readError);
                            if (attempt == 1 && officialTimeout) RecoverAfterEndedRead();
                            break;
                        }
                        if (late || Snapshot.Error != null) break;
                        List<TaskCompletionSource<bool>> completed = null;
                        lock (gate)
                        {
                            if (failure != null || stopping.WaitOne(0)) break;
                            // Check again under the commit lock; the reader may
                            // have been descheduled since the return check above.
                            if(clock.ElapsedMilliseconds-lastValidOrStarted<NoDataDeadlineMs) {
                                lastValidOrStarted=clock.ElapsedMilliseconds;
                                high = value; retrying = false; sampleSequence++;
                                completed = new List<TaskCompletionSource<bool>>(active); active.Clear(); succeeded = true;
                            }
                        }
                        if(completed==null){Fail(new TimeoutException("急停状态许可已过期，迟到数据不作为校准许可。"));break;}
                        foreach (var request in completed) request.TrySetResult(value);
                        Notify(); break;
                    }
                    if (!succeeded || WaitMinimumDelay()) break;
                }
            }
            catch (Exception error) { Fail(error); }
            finally
            {
                bool unexpected;
                lock (gate) { inFlight = false; readerEnded = true; unexpected=failure==null&&!stopping.WaitOne(0); }
                if(unexpected)Fail(new IOException("急停读取循环已退出，当前校准已停止。"));
                CompletePending(new OperationCanceledException("急停读取已停止，当前请求未获得新许可。"));
            }
        }
        void Fail(Exception error,long? expiredBaseline=null)
        {
            bool notify;
            lock (gate)
            {
                if(expiredBaseline.HasValue&&(!started||disposed||stopping.WaitOne(0)||lastValidOrStarted!=expiredBaseline.Value||
                    clock.ElapsedMilliseconds-lastValidOrStarted<NoDataDeadlineMs))return;
                notify = failure == null && !disposed;
                if (!notify) return;
                failure = error; high = null; retrying = false;
            }
            CompletePending(error);
            // No UI dispatch before cancellation. The callback must only latch
            // and request cancellation; it must not clear or wait on the DI task.
            try { cancelRound(error); lock (gate) cancellationDelivered = true; }
            catch (Exception cancelError) { lock (gate) recoveryFailure = cancelError; }
            Notify();
        }
        void RecoverAfterEndedRead()
        {
            lock (gate)
            {
                if (rebuildInput == null || recoveryAttempted || !cancellationDelivered || inFlight || stopping.WaitOne(0)) return;
                recoveryAttempted = true;
            }
            try { rebuildInput(); }
            catch (Exception error) { lock (gate) recoveryFailure = error; }
            // Always retain the fault. Rebuilding DI never resumes acquisition,
            // clears a stop, or supplies a made-up LOW value.
            Notify();
        }
        bool WaitMinimumDelay()
        {
            // WaitOne's millisecond timeout may be rounded by the scheduler.
            // Recheck a monotonic high-resolution deadline instead of treating
            // a single timed wait as proof that ten real milliseconds elapsed.
            long until=clock.ElapsedTicks+(long)Math.Ceiling(Stopwatch.Frequency*MinimumDelayMs/1000.0);
            while(true){
                long remaining=until-clock.ElapsedTicks;
                if(remaining<=0)return stopping.WaitOne(0);
                int milliseconds=Math.Max(1,(int)Math.Ceiling(remaining*1000.0/Stopwatch.Frequency));
                if(stopping.WaitOne(milliseconds))return true;
            }
        }
        void CompletePending(Exception error)
        {
            List<TaskCompletionSource<bool>> pending;
            lock (gate) { pending = new List<TaskCompletionSource<bool>>(active); pending.AddRange(waiting); active.Clear(); waiting.Clear(); }
            foreach (var request in pending) request.TrySetException(error);
        }
        void Notify()
        {
            if (changed == null) return;
            try { changed(Snapshot); } catch (Exception error) { Debug.WriteLine("Calibration emergency state notification: " + error); }
        }
        internal bool Stop(int timeoutMs)
        {
            Thread owned;
            lock (gate) { if (disposed) return true; stopping.Set(); owned = reader; }
            CompletePending(new OperationCanceledException("急停读取正在结束。"));
            return owned == null || (owned != Thread.CurrentThread && owned.Join(timeoutMs));
        }
        public void Dispose()
        {
            if (!Stop(NoDataDeadlineMs)) throw new IOException("急停读取尚未退出，任务句柄仍被保留，禁止重新连接。");
            lock (gate) { if (disposed) return; disposed = true; if (watchdog != null) watchdog.Dispose(); stopping.Dispose(); }
        }
    }
    internal sealed class CalibrationEmergencySnapshot
    {
        internal bool? High;
        internal Exception Error, RecoveryError;
        internal bool ReadInFlight, RecoveryAttempted, RetryPending;
        internal long Sequence;
    }
    internal sealed class CalibrationArtException : IOException
    {
        internal readonly int Status;
        internal bool IsReadTimeout { get { return Status == -26802; } }
        internal string OfficialName { get; private set; }
        internal CalibrationArtException(int status) : base("ART-DAQ 错误：" + status + " (" + Name(status) + ")")
        { Status = status; OfficialName = Name(status); }
        static string Name(int status)
        {
            // Official Art_DAQ.h. Names are diagnostics, not interchangeable retry codes.
            switch (status)
            {
                case -26802: return "ArtDAQError_Timeout";
                case -200284: return "ArtDAQError_SamplesNotYetAvailable";
                case -200474: return "ArtDAQError_ErrorOperationTimedOut";
                case -50401: return "ArtDAQError_PALCommunicationsFault";
                case -229779: return "ArtDAQError_SerivesCanNotConnect";
                case -229778: return "ArtDAQError_SendSerivesData";
                case -229776: return "ArtDAQError_RecieveSerivesData";
                case -229775: return "ArtDAQError_SerivesResponseError";
                case -200088: return "ArtDAQError_InvalidTask";
                case -201003: return "ArtDAQError_DevCannotBeAccessed";
                case -88717: return "ArtDAQError_DeviceNameNotFound_Routing";
                default: return "ART status preserved";
            }
        }
    }
}
