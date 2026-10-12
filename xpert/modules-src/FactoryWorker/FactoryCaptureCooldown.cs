using System;
using System.Diagnostics;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    // One conservative gate for every Pico session in this process. A newly
    // opened handle also starts a 2 s guard, so a new worker cannot skip it.
    // Callers must still establish that the previous worker/SDK call has ended.
    internal static class FactoryCaptureCooldown
    {
        private static readonly FactoryCaptureCooldownState shared=
            new FactoryCaptureCooldownState(Stopwatch.GetTimestamp,Stopwatch.Frequency);
        internal static int RemainingMilliseconds { get { return shared.RemainingMilliseconds; } }
        internal static bool ReleasePending { get { return shared.ReleasePending; } }
        internal static void Wait(CancellationToken token,Action<int> remainingChanged=null,Action checkSafety=null) { shared.Wait(token,remainingChanged,checkSafety); }
        internal static void EnsureReady() { shared.EnsureReady(); }
        internal static void MarkOpened() { shared.MarkOpened(); }
        internal static void MarkCaptureStarted(double durationMs) { shared.MarkCaptureStarted(durationMs); }
        internal static void MarkReleased(double durationMs) { shared.MarkReleased(durationMs); }
        internal static int CooldownForDurationMilliseconds(double durationMs)
        {
            if(Double.IsNaN(durationMs) || Double.IsInfinity(durationMs) || durationMs<=0)
                throw new ArgumentOutOfRangeException("durationMs","采集时长必须为有效正数，不能用未知时长解除冷却。");
            double steps=durationMs<=60?0:Math.Ceiling((durationMs-60)/10.0);
            return (int)Math.Min(Int32.MaxValue,2000+steps*500);
        }
        internal static void RequireSupportedDuration(double durationMs)
        {
            if(Double.IsNaN(durationMs) || Double.IsInfinity(durationMs) || durationMs<=0 || durationMs>60)
                throw new ArgumentOutOfRangeException("durationMs","Pico新采集单段必须大于0且不超过60 ms；冷却递增规则不允许采集更长新段。");
        }
    }

    // Injectable clock permits exact offline boundary tests; no device or file I/O.
    internal sealed class FactoryCaptureCooldownState
    {
        private readonly object gate=new object();
        private readonly Func<long> timestamp;
        private readonly long frequency;
        private long readyAt;
        private bool releasePending;
        private double armedDurationMs;
        internal FactoryCaptureCooldownState(Func<long> timestamp,long frequency)
        {
            if(timestamp==null || frequency<=0)throw new ArgumentException("冷却需要有效单调时钟。");
            this.timestamp=timestamp;this.frequency=frequency;
        }
        // MaxValue denotes an unfinished acquisition, not a known duration.
        internal int RemainingMilliseconds { get { lock(gate) return RemainingLocked(); } }
        internal bool ReleasePending { get { lock(gate) return releasePending; } }
        private int RemainingLocked()
        {
            if(releasePending)return Int32.MaxValue;
            double ticks=(double)readyAt-timestamp();
            return ticks<=0?0:(int)Math.Min(Int32.MaxValue,Math.Ceiling(ticks*1000.0/frequency));
        }
        private void ExtendLocked(int milliseconds)
        {
            long duration=(long)Math.Ceiling(milliseconds*(double)frequency/1000.0);
            long began=timestamp();
            long deadline=began>Int64.MaxValue-duration?Int64.MaxValue:began+duration;
            if(deadline>readyAt)readyAt=deadline;
        }
        private void EnsureReadyLocked()
        {
            if(releasePending)throw new InvalidOperationException("Pico上一段尚未确认停止并释放缓冲，禁止布置新段；不能以Ready或固定延时替代释放。");
            int remaining=RemainingLocked();
            if(remaining>0)throw new InvalidOperationException("Pico采后冷却尚余至少"+remaining+" ms；此入口不等待，禁止在带压布置阶段拖延或绕过冷却。");
        }
        internal void EnsureReady() { lock(gate) EnsureReadyLocked(); }
        internal void MarkOpened() { lock(gate) ExtendLocked(2000); }
        internal void MarkCaptureStarted(double durationMs)
        {
            FactoryCaptureCooldown.RequireSupportedDuration(durationMs);
            lock(gate) { EnsureReadyLocked();releasePending=true;armedDurationMs=durationMs; }
        }
        internal void MarkReleased(double durationMs)
        {
            int wait=FactoryCaptureCooldown.CooldownForDurationMilliseconds(durationMs);
            lock(gate)
            {
                if(releasePending)wait=Math.Max(wait,FactoryCaptureCooldown.CooldownForDurationMilliseconds(armedDurationMs));
                ExtendLocked(wait);releasePending=false;armedDurationMs=0;
            }
        }
        // The workflow invokes this only before HV ON or after shutdown/Stop.
        // The gate is never held while waiting, and cancellation wakes promptly.
        internal void Wait(CancellationToken token,Action<int> remainingChanged=null,Action checkSafety=null)
        {
            int previous=-1;
            while(true)
            {
                token.ThrowIfCancellationRequested();
                // Unlike optional progress, a protection failure must propagate.
                if(checkSafety!=null)checkSafety();
                int remaining=RemainingMilliseconds;
                if(remaining==0)return;
                if(remainingChanged!=null && remaining!=previous)
                {
                    previous=remaining;
                    try { remainingChanged(remaining); } catch { /* Progress cannot grant capture permission. */ }
                }
                if(token.WaitHandle.WaitOne(Math.Min(checkSafety==null?100:10,remaining)))token.ThrowIfCancellationRequested();
            }
        }
    }
}
