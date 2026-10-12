using System;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>连续监测 AI 可读缓冲（对照 Factory IFactoryFeedbackBuffer）。</summary>
    public interface IArtFeedbackBuffer
    {
        uint Available();
        /// <summary>按通道请求点数；返回 interleaved [i*channels+c]（GroupByScan）。</summary>
        double[] Read(int samplesPerChannel);
    }

    /// <summary>
    /// AO 后新鲜反馈窗（精简自 FactoryFeedbackStream）：
    /// 写 AO 后丢弃边界前积压样本，凑满窗口；积压&gt;1 s 或追读 8 组仍积压 → 禁止继续升压。
    /// </summary>
    public static class ArtFreshFeedbackWindow
    {
        public const double RateHz = 1000;
        public const int MaximumQueue = 1000;
        public const int MaximumBatches = 8;
        public const int MaximumReadMs = 100;
        public const int MaximumNewSamplesPerRead = 10;
        public const int DefaultWindowSamples = 10;
        public const int Channels = 3;
        public const int FinalPendingLimit = 20;

        public static LiveFeedbackSample Read(
            IArtFeedbackBuffer source,
            int windowSamples,
            Func<long> elapsedMilliseconds,
            bool requireFreshWindow)
        {
            if (source == null || elapsedMilliseconds == null)
                throw new ArgumentNullException();
            if (windowSamples < 1 || windowSamples > 40)
                throw new ArgumentException("AI反馈窗口须为1～40 样本（约 ms@1 kS/s）。");

            long start = elapsedMilliseconds();
            LiveFeedbackSample merged = null;
            uint firstQueue = 0;
            var freshTail = requireFreshWindow ? new double[windowSamples * Channels] : null;
            int freshTailCount = 0;
            int freshSamples = 0;

            for (int batch = 0; batch < MaximumBatches; batch++)
            {
                if (elapsedMilliseconds() - start >= MaximumReadMs)
                    throw new InvalidOperationException("ART 追读缓存超过100 ms；未得到及时反馈，禁止继续升压。");

                uint queued = source.Available();
                if (batch == 0) firstQueue = queued;
                if (queued > MaximumQueue)
                    throw new InvalidOperationException("ART 缓存积压超过1秒：未读=" + queued
                        + "点/通道，追读组=" + batch + "；禁止继续升压。");

                int requested = requireFreshWindow
                    ? checked((int)queued + Math.Min(MaximumNewSamplesPerRead, windowSamples - freshTailCount))
                    : Math.Max(windowSamples, (int)queued);
                if (requested < 1) requested = 1;

                double[] raw = source.Read(requested);
                if (raw == null || raw.Length < requested * Channels)
                    throw new InvalidOperationException("ART 反馈读点数不足：请求=" + requested + "。");

                LiveFeedbackSample part = ParsePeak(raw, requested);
                merged = merged == null ? part : MergePeaks(merged, part);

                if (requireFreshWindow)
                {
                    int skip = batch == 0 ? checked((int)firstQueue) : 0;
                    if (skip > requested) skip = requested;
                    int added = requested - skip;
                    freshSamples = checked(freshSamples + added);
                    AppendTail(freshTail, ref freshTailCount, raw, skip, added, windowSamples);
                    merged.WindowSampleCount = 0;
                    if (freshTailCount == windowSamples)
                    {
                        ApplyWindow(merged, freshTail, windowSamples);
                        merged.FreshWindowAfterAoCommand = true;
                        merged.FreshWindowBoundaryQueued = checked((int)firstQueue);
                        merged.FreshWindowSamplesAfterBoundary = freshSamples;
                    }
                }
                else
                {
                    ApplyWindow(merged, raw, Math.Min(requested, windowSamples));
                }

                uint pending = source.Available();
                long elapsed = elapsedMilliseconds() - start;
                merged.BacklogBefore = (int)firstQueue;
                merged.BacklogAfter = checked((int)pending);
                merged.ReadBatchCount = batch + 1;
                merged.ReadElapsedMs = elapsed;

                if (elapsed >= MaximumReadMs)
                    throw new InvalidOperationException("ART 本次反馈读取耗时=" + elapsed + " ms，组=" + (batch + 1)
                        + "，未读=" + pending + "；超过100 ms，不使用该反馈升压。");

                if (pending <= FinalPendingLimit
                    && (!requireFreshWindow || freshTailCount == windowSamples))
                {
                    if (requireFreshWindow && merged.WindowSampleCount != windowSamples)
                        throw new InvalidOperationException("反馈窗口未满，不能退回单点升压。");
                    return merged;
                }
            }

            throw new InvalidOperationException("ART 追读8组后仍积压：未读="
                + (merged != null ? merged.BacklogAfter : -1)
                + "，耗时=" + (merged != null ? merged.ReadElapsedMs : -1)
                + " ms；未得到及时反馈，禁止继续升压。");
        }

        static LiveFeedbackSample ParsePeak(double[] interleaved, int count)
        {
            var f = new LiveFeedbackSample
            {
                CurrentA = double.NaN,
                SourceVoltageV = double.NaN,
                SourceVoltageRawV = double.NaN,
                DividerVoltageV = double.NaN,
                DividerRawV = double.NaN,
                SampleCount = count,
                PeakCurrentAbs = 0,
                PeakSourceAbs = 0,
                PeakDividerAbs = 0
            };
            for (int i = 0; i < count; i++)
            {
                double current = interleaved[i * Channels];
                double source = interleaved[i * Channels + 1];
                double divider = interleaved[i * Channels + 2];
                if (!IsFinite(current) || !IsFinite(source) || !IsFinite(divider))
                    throw new InvalidOperationException("ART 原始反馈非有限；不平均、不截断后放行。");
                f.CurrentA = current;
                f.SourceVoltageV = source;
                f.SourceVoltageRawV = source;
                f.DividerRawV = divider;
                f.DividerVoltageV = divider;
                f.PeakCurrentAbs = Math.Max(f.PeakCurrentAbs, Math.Abs(current));
                f.PeakSourceAbs = Math.Max(f.PeakSourceAbs, Math.Abs(source));
                f.PeakDividerAbs = Math.Max(f.PeakDividerAbs, Math.Abs(divider));
            }
            return f;
        }

        static void ApplyWindow(LiveFeedbackSample f, double[] interleaved, int count)
        {
            if (f == null || interleaved == null || count < 1) return;
            // 窗口末点作为控制值；峰值已在 Parse/Merge 中保留
            int last = count - 1;
            f.CurrentA = interleaved[last * Channels];
            f.SourceVoltageV = interleaved[last * Channels + 1];
            f.SourceVoltageRawV = f.SourceVoltageV;
            f.DividerRawV = interleaved[last * Channels + 2];
            f.DividerVoltageV = f.DividerRawV;
            f.WindowSampleCount = count;
            for (int i = 0; i < count; i++)
            {
                f.PeakCurrentAbs = Math.Max(f.PeakCurrentAbs, Math.Abs(interleaved[i * Channels]));
                f.PeakSourceAbs = Math.Max(f.PeakSourceAbs, Math.Abs(interleaved[i * Channels + 1]));
                f.PeakDividerAbs = Math.Max(f.PeakDividerAbs, Math.Abs(interleaved[i * Channels + 2]));
            }
        }

        static void AppendTail(double[] tail, ref int tailCount, double[] raw, int offset, int count, int window)
        {
            if (count >= window)
            {
                Array.Copy(raw, (offset + count - window) * Channels, tail, 0, window * Channels);
                tailCount = window;
                return;
            }
            int keep = Math.Min(tailCount, window - count);
            if (keep > 0) Array.Copy(tail, (tailCount - keep) * Channels, tail, 0, keep * Channels);
            if (count > 0) Array.Copy(raw, offset * Channels, tail, keep * Channels, count * Channels);
            tailCount = keep + count;
        }

        static LiveFeedbackSample MergePeaks(LiveFeedbackSample old, LiveFeedbackSample next)
        {
            next.SampleCount = checked(old.SampleCount + next.SampleCount);
            next.PeakCurrentAbs = Math.Max(old.PeakCurrentAbs, next.PeakCurrentAbs);
            next.PeakSourceAbs = Math.Max(old.PeakSourceAbs, next.PeakSourceAbs);
            next.PeakDividerAbs = Math.Max(old.PeakDividerAbs, next.PeakDividerAbs);
            return next;
        }

        static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
