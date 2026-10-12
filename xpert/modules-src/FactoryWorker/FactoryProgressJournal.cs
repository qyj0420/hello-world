using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.FactoryTest
{
    // One bounded managed writer, not a device owner. Producers never perform
    // disk work or silently drop a record. OFF/error publication cannot delay
    // shutdown; enabling/ARM/normal terminal barriers acknowledge prior records.
    internal sealed class FactoryProgressJournal : IDisposable
    {
        internal const int DefaultCapacity = 256;
        internal const int DrainTimeoutMs = 5000;
        private sealed class Entry
        {
            internal string Line;
            internal bool Flush;
            internal readonly TaskCompletionSource<bool> Completed = new TaskCompletionSource<bool>();
        }
        private readonly object submitGate = new object();
        private readonly BlockingCollection<Entry> queue;
        private readonly Func<TextWriter> open;
        private readonly Action<Exception> onFailure, fallback;
        private readonly CancellationToken cancellation;
        private readonly int timeoutMs;
        private readonly Task writerTask;
        private Exception failure;
        private bool closed;
        internal Exception Failure { get { return Interlocked.CompareExchange(ref failure, null, null); } }
        internal int PendingCount { get { return queue.Count; } }

        internal FactoryProgressJournal(string directory, Action<Exception> failed, CancellationToken cancellation)
            : this(DefaultCapacity,
                () => new StreamWriter(new FileStream(Path.Combine(directory, "progress.log"), FileMode.Append,
                    FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 16384), failed,
                error => File.WriteAllText(Path.Combine(directory, "progress-error.txt"), error.ToString(), new UTF8Encoding(false)),
                cancellation, DrainTimeoutMs) { }

        // Pure managed tests inject a writer. No SDK, clock, settings or output
        // permission is changed by this journal.
        internal FactoryProgressJournal(int capacity, Func<TextWriter> open, Action<Exception> failed,
            Action<Exception> fallback, CancellationToken cancellation, int timeoutMs = DrainTimeoutMs)
        {
            if (capacity < 1 || capacity > 65536) throw new ArgumentOutOfRangeException("capacity");
            if (open == null) throw new ArgumentNullException("open");
            if (timeoutMs < 1) throw new ArgumentOutOfRangeException("timeoutMs");
            queue = new BlockingCollection<Entry>(capacity); this.open = open;
            onFailure = failed; this.fallback = fallback; this.cancellation = cancellation; this.timeoutMs = timeoutMs;
            writerTask = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal static bool IsOrdinary(string stage)
        {
            return stage == "FEEDBACK_TREND" || stage == "RAMP_PROGRESS" || stage == "HV_CONDITION" ||
                stage == "WAIT" || stage == "INITIAL_FEEDBACK" || stage == "HV_ZERO_FEEDBACK" || stage == "RESIDUAL";
        }
        internal static bool RequiresBarrier(string stage)
        {
            // In particular STEP_HV_OFF_RUNNING, emergency and ERROR must not
            // wait for disk before the runner can attempt actual OFF/cleanup.
            return stage == "DEVICE_SESSION" || stage == "STEP_INIT_RUNNING" || stage == "STEP_INIT_PASSED" ||
                stage == "STEP_HV_ON_RUNNING" || stage == "STEP_HV_UP_RUNNING" || stage == "STEP_HV_UP_PASSED" ||
                stage == "STEP_ARM_RUNNING" || stage == "STEP_COMPLETE_PASSED" || stage == "STEP_COMPLETE_UNVERIFIED" ||
                stage == "RECORD";
        }
        internal static bool IsPostCleanupBarrier(string stage)
        {
            // A user Stop must interrupt an enabling/ARM wait, but must not
            // cancel the acknowledgement of cleanup/save that already returned.
            // The independent writer still has the same bounded deadline and
            // propagates every real write/flush failure.
            return stage == "STEP_COMPLETE_PASSED" || stage == "STEP_COMPLETE_UNVERIFIED" || stage == "RECORD";
        }
        internal void Report(string stage, string detail)
        {
            var entry = new Entry {
                Line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) + "\t" + stage + "\t" +
                    (detail ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ") + "\r\n",
                Flush = !IsOrdinary(stage)
            };
            Submit(entry);
            if (RequiresBarrier(stage)) Wait(entry, IsPostCleanupBarrier(stage) ? CancellationToken.None : cancellation);
        }
        internal void Flush()
        {
            var entry = new Entry { Flush = true }; Submit(entry); Wait(entry, cancellation);
        }
        private void Submit(Entry entry)
        {
            Exception rejected = null;
            lock (submitGate)
            {
                if (closed) throw new ObjectDisposedException("FactoryProgressJournal");
                ThrowFailure();
                if (!queue.TryAdd(entry)) rejected = new IOException("进度日志队列已满；停止本轮，不等待磁盘或静默丢弃。未提交记录：" + entry.Line);
            }
            if (rejected != null) { Fail(rejected); ThrowFailure(); }
        }
        private void Wait(Entry entry, CancellationToken token)
        {
            if (!entry.Completed.Task.Wait(timeoutMs, token))
                Fail(new TimeoutException("关键进度日志未在5秒内完成写入；本轮停止，不将未持久化记录声明为完成。"));
            ThrowFailure();
        }
        private void ThrowFailure()
        {
            var error = Failure;
            if (error != null) throw new IOException("进度记录失败；停止本轮，不继续加压。", error);
        }
        private void Fail(Exception error)
        {
            Exception prior;
            do {
                prior = Failure;
            } while (Interlocked.CompareExchange(ref failure, prior == null ? error :
                new AggregateException("进度记录的原始错误均保留。", prior, error), prior) != prior);
            if (prior == null && onFailure != null)
            {
                try { onFailure(error); }
                catch (Exception callbackError) { AddFailure(callbackError); }
            }
        }
        private void AddFailure(Exception error)
        {
            Exception prior;
            do { prior = Failure; }
            while (Interlocked.CompareExchange(ref failure, prior == null ? error :
                new AggregateException("进度记录的原始错误均保留。", prior, error), prior) != prior);
        }
        private void WriteLoop()
        {
            TextWriter writer = null;
            Entry active = null;
            try
            {
                writer = open(); var elapsed = Stopwatch.StartNew(); long lastFlush = 0; bool dirty = false;
                while (!queue.IsCompleted)
                {
                    if (queue.TryTake(out active, 25))
                    {
                        if (active.Line != null) { writer.Write(active.Line); dirty = true; }
                        if (active.Flush) { writer.Flush(); dirty = false; lastFlush = elapsed.ElapsedMilliseconds; }
                        active.Completed.TrySetResult(true); active = null;
                    }
                    if (dirty && elapsed.ElapsedMilliseconds - lastFlush >= 100)
                    { writer.Flush(); dirty = false; lastFlush = elapsed.ElapsedMilliseconds; }
                }
                writer.Flush();
            }
            catch (Exception ex) { Fail(new IOException("进度日志写入或刷新失败；未确认的当前记录：" +
                (active == null ? "普通记录缓冲刷新；前序写入的持久化未确认。" : active.Line), ex)); }
            finally
            {
                if (writer != null) try { writer.Dispose(); } catch (Exception ex) { Fail(ex); }
                if (active != null) active.Completed.TrySetResult(false);
                Entry pending;
                while (queue.TryTake(out pending))
                {
                    // Preserve critical details even if the primary disk writer
                    // failed before reaching them. No successful ACK is issued.
                    if (Failure != null && pending.Flush && pending.Line != null)
                        AddFailure(new IOException("未能持久化的排队关键记录原文：" + pending.Line));
                    pending.Completed.TrySetResult(false);
                }
                // Disk fallback runs only on this independent writer, never on
                // the sole DI reader or the AO control producer.
                var error = Failure;
                if (error != null && fallback != null) try { fallback(error); } catch (Exception ex) { AddFailure(ex); }
            }
        }
        public void Dispose()
        {
            lock (submitGate) { if (closed) return; closed = true; queue.CompleteAdding(); }
            if (!writerTask.Wait(timeoutMs))
            { Fail(new TimeoutException("进度记录写盘未在5秒内结束；硬件收尾已先行，不确认本轮记录完整。")); ThrowFailure(); }
            queue.Dispose(); ThrowFailure();
        }
    }
}
