using System;
using System.Diagnostics;
using System.Threading;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// Pico 采后冷却状态机（精简自 FactoryCaptureCooldown）。
    /// 新 Open 后 ≥2 s；Stop/缓冲释放后共享单调时钟冷却；带压 Arm 冷却不足立即失败（不等待）。
    /// 新段硬上限 60 ms。
    /// </summary>
    public static class PicoCaptureCooldown
    {
        static readonly PicoCaptureCooldownState Shared =
            new PicoCaptureCooldownState(Stopwatch.GetTimestamp, Stopwatch.Frequency);

        public static int RemainingMilliseconds { get { return Shared.RemainingMilliseconds; } }
        public static bool ReleasePending { get { return Shared.ReleasePending; } }

        public static void Wait(CancellationToken token, Action<int> remainingChanged = null, Action checkSafety = null)
        {
            Shared.Wait(token, remainingChanged, checkSafety);
        }

        public static void EnsureReady() { Shared.EnsureReady(); }
        public static void MarkOpened() { Shared.MarkOpened(); }
        public static void MarkCaptureStarted(double durationMs) { Shared.MarkCaptureStarted(durationMs); }
        /// <summary>关源后布防：冷却未尽也允许开段（绝不干等）；冷却只在采后 MarkReleased/档间 Wait。</summary>
        public static void MarkCaptureStartedAllowUnready(double durationMs) { Shared.MarkCaptureStartedAllowUnready(durationMs); }
        public static void MarkReleased(double durationMs) { Shared.MarkReleased(durationMs); }

        public static int CooldownForDurationMilliseconds(double durationMs)
        {
            if (double.IsNaN(durationMs) || double.IsInfinity(durationMs) || durationMs <= 0)
                throw new ArgumentOutOfRangeException("durationMs", "采集时长必须为有效正数，不能用未知时长解除冷却。");
            double steps = durationMs <= 60 ? 0 : Math.Ceiling((durationMs - 60) / 10.0);
            return (int)Math.Min(int.MaxValue, 2000 + steps * 500);
        }

        public static void RequireSupportedDuration(double durationMs)
        {
            if (double.IsNaN(durationMs) || double.IsInfinity(durationMs) || durationMs <= 0 || durationMs > 60)
                throw new ArgumentOutOfRangeException("durationMs",
                    "Pico 新采集单段必须大于 0 且不超过 60 ms；冷却递增规则不允许采集更长新段。");
        }

        /// <summary>由配置推算武装段时长（ms）：sampleCount × intervalNs / 1e6。</summary>
        public static double EstimateDurationMs(int sampleCount, double intervalNanoseconds)
        {
            if (sampleCount <= 0 || !(intervalNanoseconds > 0))
                throw new ArgumentOutOfRangeException("sampleCount", "无法从非法点数/间隔推算 Pico 段时长。");
            double ms = sampleCount * intervalNanoseconds / 1e6;
            if (ms > 60) ms = 60;
            if (ms < 0.001) ms = 0.001;
            return ms;
        }
    }

    /// <summary>可注入时钟，便于离线边界测试。</summary>
    public sealed class PicoCaptureCooldownState
    {
        readonly object _gate = new object();
        readonly Func<long> _timestamp;
        readonly long _frequency;
        long _readyAt;
        bool _releasePending;
        double _armedDurationMs;

        public PicoCaptureCooldownState(Func<long> timestamp, long frequency)
        {
            if (timestamp == null || frequency <= 0)
                throw new ArgumentException("冷却需要有效单调时钟。");
            _timestamp = timestamp;
            _frequency = frequency;
        }

        public int RemainingMilliseconds { get { lock (_gate) return RemainingLocked(); } }
        public bool ReleasePending { get { lock (_gate) return _releasePending; } }

        int RemainingLocked()
        {
            if (_releasePending) return int.MaxValue;
            double ticks = (double)_readyAt - _timestamp();
            return ticks <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(ticks * 1000.0 / _frequency));
        }

        void ExtendLocked(int milliseconds)
        {
            long duration = (long)Math.Ceiling(milliseconds * (double)_frequency / 1000.0);
            long began = _timestamp();
            long deadline = began > long.MaxValue - duration ? long.MaxValue : began + duration;
            if (deadline > _readyAt) _readyAt = deadline;
        }

        void EnsureReadyLocked()
        {
            if (_releasePending)
                throw new InvalidOperationException(
                    "Pico 上一段尚未确认停止并释放缓冲，禁止布置新段；不能以 Ready 或固定延时替代释放。");
            int remaining = RemainingLocked();
            if (remaining > 0)
                throw new InvalidOperationException(
                    "Pico 采后冷却尚余至少 " + remaining + " ms；此入口不等待，禁止在带压布置阶段拖延或绕过冷却。");
        }

        public void EnsureReady() { lock (_gate) EnsureReadyLocked(); }

        public void MarkOpened() { lock (_gate) ExtendLocked(2000); }

        public void MarkCaptureStarted(double durationMs)
        {
            PicoCaptureCooldown.RequireSupportedDuration(durationMs);
            lock (_gate)
            {
                EnsureReadyLocked();
                _releasePending = true;
                _armedDurationMs = durationMs;
            }
        }

        /// <summary>与 MarkCaptureStarted 相同但不 EnsureReady（关源后禁止干等冷却）。</summary>
        public void MarkCaptureStartedAllowUnready(double durationMs)
        {
            PicoCaptureCooldown.RequireSupportedDuration(durationMs);
            lock (_gate)
            {
                // 不清 _readyAt、不 Sleep；调用方须已 Abort 上一段。冷却只在采后 MarkReleased / 档间 Wait。
                _releasePending = true;
                _armedDurationMs = durationMs;
            }
        }

        public void MarkReleased(double durationMs)
        {
            int wait = PicoCaptureCooldown.CooldownForDurationMilliseconds(durationMs);
            lock (_gate)
            {
                if (_releasePending)
                    wait = Math.Max(wait, PicoCaptureCooldown.CooldownForDurationMilliseconds(_armedDurationMs));
                ExtendLocked(wait);
                _releasePending = false;
                _armedDurationMs = 0;
            }
        }

        public void Wait(CancellationToken token, Action<int> remainingChanged = null, Action checkSafety = null)
        {
            int previous = -1;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (checkSafety != null) checkSafety();
                int remaining = RemainingMilliseconds;
                if (remaining == 0) return;
                if (remainingChanged != null && remaining != previous)
                {
                    previous = remaining;
                    try { remainingChanged(remaining); }
                    catch { /* 进度不能授予采集许可 */ }
                }
                int slice = Math.Min(checkSafety == null ? 100 : 10, remaining == int.MaxValue ? 50 : remaining);
                if (token.WaitHandle.WaitOne(Math.Max(1, slice)))
                    token.ThrowIfCancellationRequested();
            }
        }
    }
}
