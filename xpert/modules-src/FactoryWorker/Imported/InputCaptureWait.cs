using System;
using System.Diagnostics;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    internal interface IInputCaptureClock
    {
        double Milliseconds { get; }
        void Pause(int milliseconds, CancellationToken cancellation);
    }

    internal sealed class MonotonicInputCaptureClock : IInputCaptureClock
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public double Milliseconds { get { return clock.Elapsed.TotalMilliseconds; } }
        public void Pause(int milliseconds, CancellationToken cancellation)
        {
            if (cancellation.WaitHandle.WaitOne(milliseconds)) cancellation.ThrowIfCancellationRequested();
        }
    }

    // State polling is independent of native drivers so cancellation and trigger
    // gates can be verified offline with a monotonic, deterministic test clock.
    internal sealed class InputCaptureWait
    {
        private readonly Func<uint> artAvailable;
        private readonly Func<bool> picoReady;
        private readonly Action checkPicoStatus;
        private readonly IInputCaptureClock clock;
        private readonly uint requiredSamples;
        private readonly double captureMilliseconds;
        private const int PollMilliseconds = 10;

        internal InputCaptureWait(Func<uint> artAvailable, Func<bool> picoReady,
            Action checkPicoStatus, uint requiredSamples, double captureMilliseconds,
            IInputCaptureClock clock)
        {
            if (artAvailable == null || picoReady == null || checkPicoStatus == null || clock == null)
                throw new ArgumentNullException("采集状态查询与单调时钟不能为空。");
            if (requiredSamples == 0 || Double.IsNaN(captureMilliseconds) || Double.IsInfinity(captureMilliseconds) || captureMilliseconds <= 0)
                throw new ArgumentException("采集点数和时长必须有效。");
            this.artAvailable = artAvailable; this.picoReady = picoReady;
            this.checkPicoStatus = checkPicoStatus; this.clock = clock;
            this.requiredSamples = requiredSamples; this.captureMilliseconds = captureMilliseconds;
        }

        internal void WaitQuiet(int quietMilliseconds, CancellationToken cancellation)
        {
            if (quietMilliseconds < 0) throw new ArgumentOutOfRangeException("quietMilliseconds");
            double start = clock.Milliseconds;
            cancellation.ThrowIfCancellationRequested();
            while (clock.Milliseconds - start < quietMilliseconds)
            {
                cancellation.ThrowIfCancellationRequested();
                checkPicoStatus();
                bool picoComplete = picoReady();
                uint samples = artAvailable();
                cancellation.ThrowIfCancellationRequested();
                if (picoComplete || samples != 0)
                    throw new InvalidOperationException("布置完成前已检测到触发/数据，本轮可能不是同一边沿，已拒绝。请先停止外部连续触发，进入 WAIT_TRIGGER 后仅送一个共享边沿。");
                clock.Pause(PollMilliseconds, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
        }

        internal void WaitComplete(int timeoutMilliseconds, Action reportWaiting,
            CancellationToken cancellation)
        {
            WaitComplete(timeoutMilliseconds, reportWaiting, null, cancellation, null);
        }

        internal void WaitComplete(int timeoutMilliseconds, Action reportWaiting,
            Action issueSharedTrigger, CancellationToken cancellation,
            Action<uint, bool> reportState = null)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            cancellation.ThrowIfCancellationRequested();
            if (issueSharedTrigger != null)
            {
                // The quiet gate is a separate, mandatory caller step. Check again
                // here because an unsolicited edge may arrive just after that gate.
                checkPicoStatus();
                bool alreadyReady = picoReady();
                uint alreadyAvailable = artAvailable();
                cancellation.ThrowIfCancellationRequested();
                if (alreadyReady || alreadyAvailable != 0)
                    throw new InvalidOperationException("主动 TRG 前已有触发/数据，已拒绝输出触发并拒绝配对。" +
                        StateDescription(alreadyAvailable, alreadyReady));
            }
            double start = clock.Milliseconds;
            // Start before WAIT_TRIGGER and before the output edge. A synchronous
            // optical helper holds its control for the configured fixed duration while both receivers
            // collect the configured finite segment (60 ms by default). This
            // hold is inside the overall timeout, never added to ADC data length.
            if (reportWaiting != null) reportWaiting();
            cancellation.ThrowIfCancellationRequested();
            if (issueSharedTrigger != null) issueSharedTrigger();
            bool reported = false, lastPicoComplete = false, lastArtComplete = false;
            uint lastSamples = 0;
            double lastReportAt = Double.NegativeInfinity;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                checkPicoStatus();
                bool picoComplete = picoReady();
                uint samples = artAvailable();
                bool artComplete = samples >= requiredSamples;
                cancellation.ThrowIfCancellationRequested();
                double elapsed = clock.Milliseconds - start;
                // Publish completion transitions immediately, but throttle partial
                // sample progress so the 10 ms poll does not flood the UI/log.
                if (reportState != null && (!reported || picoComplete != lastPicoComplete ||
                    artComplete != lastArtComplete || (samples != lastSamples && elapsed - lastReportAt >= 250)))
                {
                    reportState(samples, picoComplete);
                    reported = true; lastPicoComplete = picoComplete; lastArtComplete = artComplete;
                    lastSamples = samples; lastReportAt = elapsed;
                }
                cancellation.ThrowIfCancellationRequested();
                if ((picoComplete || artComplete) && elapsed < captureMilliseconds - 1)
                    throw new InvalidOperationException("进入触发等待后不足" + DurationDescription() +
                        "ms 已完成采集，表明存在提前触发；本轮拒绝配对。" + StateDescription(samples, picoComplete));
                if (elapsed >= timeoutMilliseconds)
                    throw new TimeoutException("两卡未在触发超时内完成完整" + DurationDescription() +
                        "ms 采集；无自动触发替代。" + StateDescription(samples, picoComplete) +
                        (artComplete && picoComplete ? "完整数据仅在超过总超时后观察到，不接受本轮；请检查光触发调用或驱动延迟。" : MissingSideDescription(artComplete, picoComplete)));
                if (artComplete && picoComplete) return;
                clock.Pause(PollMilliseconds, cancellation);
            }
        }

        private string DurationDescription()
        { return captureMilliseconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }

        private string StateDescription(uint samples, bool picoComplete)
        {
            return " ART " + samples + "/" + requiredSamples + " 点；Pico " +
                (picoComplete ? "完成" : "等待完成") + "。";
        }

        private static string MissingSideDescription(bool artComplete, bool picoComplete)
        {
            if (artComplete && !picoComplete)
                return "ART 已完成，Pico 未完成：请检查 Pico EXT 同线接线、触发阈值和边沿，以及 Pico 状态；不能认定两卡同步成功。";
            if (!artComplete && picoComplete)
                return "Pico 已完成，ART 未完成：请检查 ART PFI0 同线接线、触发边沿和采样任务状态；不能认定两卡同步成功。";
            return "两卡均未完整完成：请检查 P0.7 光触发、晶闸管导通及共用开关同步检测到 PFI0/Pico EXT 的线路、边沿、阈值和两卡采样状态；超时不等于已证明采集卡故障。";
        }
    }
}
