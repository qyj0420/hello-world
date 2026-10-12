using System;
using System.Collections.Generic;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 局放校准脉冲近似固定间隔；偏离标称周期的捕获视为噪声/干扰剔除。
    /// 现场脉冲一致性很好（50/100/400 Hz）；容差宜宽，勿把真脉冲剔光导致「无信号」。
    /// </summary>
    public static class CalibrationPulseIntervalFilter
    {
        /// <summary>相对标称周期容差（默认 ±35%；脉冲一致性好，主要剔噪声尖峰）。</summary>
        public const double DefaultPeriodToleranceFraction = 0.35;

        /// <summary>筛后至少保留的脉冲数下限（相对目标 N 的比例）。</summary>
        public const double DefaultMinKeepFractionOfTarget = 0.35;

        /// <summary>筛后绝对最少脉冲数。</summary>
        public const int DefaultMinKeepAbsolute = 2;

        /// <summary>400 Hz → 2.5 ms（ns）。</summary>
        public const double Period400HzNanoseconds = 2.5e6;

        /// <summary>100 Hz → 10 ms（ns）。</summary>
        public const double Period100HzNanoseconds = 10e6;

        /// <summary>50 Hz → 20 ms（ns）。</summary>
        public const double Period50HzNanoseconds = 20e6;

        /// <summary>现场常用校准脉冲周期候选（优先匹配实测中位数）。</summary>
        public static readonly double[] DefaultExpectedPeriodCandidatesNs =
        {
            Period400HzNanoseconds,
            Period100HzNanoseconds,
            Period50HzNanoseconds
        };

        /// <summary>实测中位数贴近某候选的最大相对偏差（选锚用，宽于筛除容差）。</summary>
        public const double CandidateMatchFraction = 0.40;

        public static CalibrationPulseIntervalFilterResult Filter(
            PicoRapidBlockCaptureResult rapid,
            int targetAverageCount,
            double periodToleranceFraction,
            double minKeepFractionOfTarget,
            int minKeepAbsolute)
        {
            return Filter(rapid, targetAverageCount, periodToleranceFraction,
                minKeepFractionOfTarget, minKeepAbsolute,
                expectedPeriodNanoseconds: double.NaN,
                expectedPeriodCandidatesNs: DefaultExpectedPeriodCandidatesNs);
        }

        /// <param name="expectedPeriodNanoseconds">
        /// 显式标称周期（ns）；&gt;0 时优先采用。NaN/≤0 则在候选与实测中位数间自适应。
        /// </param>
        /// <param name="expectedPeriodCandidatesNs">
        /// 候选周期（默认 2.5/10/20 ms = 400/100/50 Hz）；可为 null。
        /// </param>
        public static CalibrationPulseIntervalFilterResult Filter(
            PicoRapidBlockCaptureResult rapid,
            int targetAverageCount,
            double periodToleranceFraction,
            double minKeepFractionOfTarget,
            int minKeepAbsolute,
            double expectedPeriodNanoseconds,
            double[] expectedPeriodCandidatesNs)
        {
            var result = new CalibrationPulseIntervalFilterResult();
            if (rapid == null || rapid.Captures == null || rapid.Captures.Length == 0)
            {
                result.Success = false;
                result.Message = "Rapid 结果为空，无法做固定间隔筛选。";
                return result;
            }

            int n = rapid.Captures.Length;
            double tol = periodToleranceFraction > 0 ? periodToleranceFraction : DefaultPeriodToleranceFraction;
            if (tol > 0.60) tol = 0.60; // 上限：仍能剔明显噪声，但不误杀真脉冲
            if (tol < 0.10) tol = 0.10;

            var tNs = new double[n];
            for (int i = 0; i < n; i++)
            {
                long raw = rapid.TriggerTimeOffsets != null && i < rapid.TriggerTimeOffsets.Length
                    ? rapid.TriggerTimeOffsets[i] : 0;
                int unit = rapid.TriggerTimeUnits != null && i < rapid.TriggerTimeUnits.Length
                    ? rapid.TriggerTimeUnits[i] : 2;
                tNs[i] = ToNanoseconds(raw, unit);
            }

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => tNs[a].CompareTo(tNs[b]));

            if (n == 1)
            {
                if (targetAverageCount <= 1)
                {
                    result.Success = true;
                    result.KeptIndices = new[] { order[0] };
                    result.RejectedIndices = new int[0];
                    result.NominalPeriodNanoseconds = double.NaN;
                    result.Message = "仅 1 段捕获，跳过周期筛选。";
                    return result;
                }
                result.Success = false;
                result.Message = "仅捕获 1 段，无法验证固定间隔；需要至少 2 段局放脉冲。";
                return result;
            }

            var intervals = new List<double>(n - 1);
            for (int k = 1; k < n; k++)
            {
                double dt = tNs[order[k]] - tNs[order[k - 1]];
                if (dt > 0) intervals.Add(dt);
            }

            // 时间戳无有效正间隔（常见于 Bulk 偏移同值）：有峰则按序保留，勿误判无信号
            if (intervals.Count == 0)
            {
                return FallbackKeepByOrder(rapid, order, targetAverageCount, minKeepFractionOfTarget, minKeepAbsolute,
                    "触发时间戳无有效正间隔；脉冲峰有效则按捕获序保留（不因间隔筛剔光）。");
            }

            intervals.Sort();
            double measuredMedian = Median(intervals);
            double nominal = ResolveNominalPeriodNs(
                measuredMedian, expectedPeriodNanoseconds, expectedPeriodCandidatesNs, out string nominalSrc);
            if (!(nominal > 0))
            {
                return FallbackKeepByOrder(rapid, order, targetAverageCount, minKeepFractionOfTarget, minKeepAbsolute,
                    "标称周期无法解析；有峰则按序保留。");
            }
            result.NominalPeriodNanoseconds = nominal;
            result.MeasuredMedianPeriodNanoseconds = measuredMedian;
            result.NominalSource = nominalSrc ?? "";

            var kept = new List<int>();
            var rejected = new List<int>();
            kept.Add(order[0]);
            double expect = tNs[order[0]] + nominal;
            for (int k = 1; k < n; k++)
            {
                int idx = order[k];
                double t = tNs[idx];
                double err = Math.Abs(t - expect) / nominal;
                if (err <= tol)
                {
                    kept.Add(idx);
                    expect = t + nominal;
                    continue;
                }

                // 允许多格（漏采 1～数个周期后仍落在 50/100/400Hz 栅格上）
                double slots = (t - (expect - nominal)) / nominal;
                int nearest = (int)Math.Round(slots);
                if (nearest >= 1 && nearest <= 8)
                {
                    double grid = (expect - nominal) + nearest * nominal;
                    if (Math.Abs(t - grid) / nominal <= tol)
                    {
                        kept.Add(idx);
                        expect = t + nominal;
                        continue;
                    }
                }
                rejected.Add(idx);
            }

            result.KeptIndices = kept.ToArray();
            result.RejectedIndices = rejected.ToArray();
            result.RejectedCount = rejected.Count;

            int target = targetAverageCount > 0 ? targetAverageCount : PicoRapidBlockBulkOverlapped.DefaultCaptureCount;
            int minKeep = Math.Max(minKeepAbsolute > 0 ? minKeepAbsolute : DefaultMinKeepAbsolute,
                (int)Math.Ceiling(target * (minKeepFractionOfTarget > 0 ? minKeepFractionOfTarget : DefaultMinKeepFractionOfTarget)));
            if (minKeep < 1) minKeep = 1;
            // 放宽：至少 2；勿因 N 大把门槛抬到剔光
            if (minKeep > Math.Max(2, n / 2)) minKeep = Math.Max(2, n / 2);

            if (kept.Count < minKeep)
            {
                // 间隔筛过严时：若多数段有有效峰，回退按峰保留，避免「无信号」假阴性
                int peakful = CountMeaningfulPeaks(rapid);
                if (peakful >= minKeep)
                {
                    return FallbackKeepByPeak(rapid, order, target, minKeep,
                        "间隔筛仅剩 " + kept.Count + "（剔除 " + rejected.Count
                        + "）但有峰段=" + peakful + "；按峰序保留（容差 ±"
                        + (tol * 100).ToString("0.#") + "%，标称="
                        + (nominal / 1e6).ToString("0.###") + " ms/" + nominalSrc + "）。");
                }

                result.Success = false;
                result.Message = "固定间隔筛选后仅剩 " + kept.Count + " 段（剔除干扰 " + rejected.Count
                    + "），低于最少 " + minKeep + "（目标平均 N=" + target
                    + "，容差 ±" + (tol * 100).ToString("0.#") + "%，标称="
                    + (nominal / 1e6).ToString("0.###") + " ms/" + nominalSrc
                    + "）；请重试校准采集。";
                return result;
            }

            if (kept.Count > target)
            {
                var trimmed = new int[target];
                for (int i = 0; i < target; i++) trimmed[i] = kept[i];
                result.KeptIndices = trimmed;
                result.TrimmedExtraKept = kept.Count - target;
            }

            result.Success = true;
            result.Message = "固定间隔筛选通过：保留 " + result.KeptIndices.Length
                + " 段，剔除干扰 " + rejected.Count
                + "，标称周期=" + (nominal / 1e6).ToString("0.###") + " ms（"
                + nominalSrc + "，实测中位=" + (measuredMedian / 1e6).ToString("0.###") + " ms）"
                + "，容差 ±" + (tol * 100).ToString("0.#") + "%。";
            return result;
        }

        /// <summary>
        /// 在显式期望、50/100/400Hz 候选、实测中位数间解析标称周期。
        /// </summary>
        public static double ResolveNominalPeriodNs(
            double measuredMedianNs,
            double expectedPeriodNanoseconds,
            double[] candidatesNs,
            out string source)
        {
            if (expectedPeriodNanoseconds > 0
                && !double.IsNaN(expectedPeriodNanoseconds)
                && !double.IsInfinity(expectedPeriodNanoseconds))
            {
                source = "显式配置";
                return expectedPeriodNanoseconds;
            }

            double[] cands = candidatesNs != null && candidatesNs.Length > 0
                ? candidatesNs
                : DefaultExpectedPeriodCandidatesNs;

            if (measuredMedianNs > 0 && !double.IsNaN(measuredMedianNs))
            {
                // 1) 中位数贴近某候选（含 2×/3× 漏采倍频）→ 锚到基频候选
                double bestCand = double.NaN;
                double bestErr = double.MaxValue;
                for (int i = 0; i < cands.Length; i++)
                {
                    double c = cands[i];
                    if (!(c > 0)) continue;
                    for (int mult = 1; mult <= 3; mult++)
                    {
                        double err = Math.Abs(measuredMedianNs - c * mult) / (c * mult);
                        if (err < bestErr)
                        {
                            bestErr = err;
                            bestCand = c; // 锚基频，栅格用 50/100/400Hz
                        }
                    }
                }
                if (bestCand > 0 && bestErr <= CandidateMatchFraction)
                {
                    source = "候选匹配≈" + (1e9 / bestCand).ToString("0.#") + "Hz";
                    return bestCand;
                }

                // 2) 无候选贴近 → 用实测中位数（自适应主导间隔）
                source = "实测中位数";
                return measuredMedianNs;
            }

            // 无实测：默认 400Hz
            source = "默认400Hz";
            return Period400HzNanoseconds;
        }

        static CalibrationPulseIntervalFilterResult FallbackKeepByOrder(
            PicoRapidBlockCaptureResult rapid,
            int[] order,
            int targetAverageCount,
            double minKeepFractionOfTarget,
            int minKeepAbsolute,
            string why)
        {
            var result = new CalibrationPulseIntervalFilterResult();
            int peakful = CountMeaningfulPeaks(rapid);
            if (peakful < (minKeepAbsolute > 0 ? minKeepAbsolute : DefaultMinKeepAbsolute))
            {
                result.Success = false;
                result.Message = why + " 且有效峰段不足（" + peakful + "）。";
                result.KeptIndices = new int[0];
                result.RejectedIndices = order ?? new int[0];
                result.RejectedCount = result.RejectedIndices.Length;
                return result;
            }

            int target = targetAverageCount > 0 ? targetAverageCount : PicoRapidBlockBulkOverlapped.DefaultCaptureCount;
            var kept = new List<int>();
            for (int i = 0; i < order.Length && kept.Count < target; i++)
            {
                int idx = order[i];
                if (CapturePeakAbs(rapid, idx) >= MinPeakAdc(rapid))
                    kept.Add(idx);
            }
            if (kept.Count < (minKeepAbsolute > 0 ? minKeepAbsolute : DefaultMinKeepAbsolute))
            {
                result.Success = false;
                result.Message = why + " 按序有峰段仍不足。";
                return result;
            }
            result.Success = true;
            result.KeptIndices = kept.ToArray();
            result.RejectedIndices = new int[0];
            result.RejectedCount = 0;
            result.NominalPeriodNanoseconds = double.NaN;
            result.NominalSource = "fallback-order";
            result.Message = why + " 保留 " + kept.Count + " 段。";
            return result;
        }

        static CalibrationPulseIntervalFilterResult FallbackKeepByPeak(
            PicoRapidBlockCaptureResult rapid,
            int[] order,
            int target,
            int minKeep,
            string why)
        {
            var result = new CalibrationPulseIntervalFilterResult();
            var kept = new List<int>();
            for (int i = 0; i < order.Length && kept.Count < target; i++)
            {
                int idx = order[i];
                if (CapturePeakAbs(rapid, idx) >= MinPeakAdc(rapid))
                    kept.Add(idx);
            }
            if (kept.Count < minKeep)
            {
                result.Success = false;
                result.Message = why;
                return result;
            }
            result.Success = true;
            result.KeptIndices = kept.ToArray();
            result.RejectedIndices = new int[0];
            result.RejectedCount = 0;
            result.NominalSource = "fallback-peak";
            result.Message = why;
            return result;
        }

        static int CountMeaningfulPeaks(PicoRapidBlockCaptureResult rapid)
        {
            if (rapid == null || rapid.Captures == null) return 0;
            int minP = MinPeakAdc(rapid);
            int n = 0;
            for (int i = 0; i < rapid.Captures.Length; i++)
                if (CapturePeakAbs(rapid, i) >= minP) n++;
            return n;
        }

        static int MinPeakAdc(PicoRapidBlockCaptureResult rapid)
        {
            int maxAdc = rapid != null && rapid.MaximumAdc > 0 ? rapid.MaximumAdc : 32512;
            return Math.Max(1, (int)(maxAdc * CalibrationPulseAcquisitionRunner.MinPeakFractionOfFullScale));
        }

        static int CapturePeakAbs(PicoRapidBlockCaptureResult rapid, int idx)
        {
            if (rapid == null || rapid.Captures == null || idx < 0 || idx >= rapid.Captures.Length)
                return 0;
            short[] w = rapid.Captures[idx];
            if (w == null || w.Length == 0) return 0;
            int peak = 0;
            for (int i = 0; i < w.Length; i++)
            {
                int a = Math.Abs((int)w[i]);
                if (a > peak) peak = a;
            }
            return peak;
        }

        public static short[] AverageCaptures(PicoRapidBlockCaptureResult rapid, int[] keptIndices)
        {
            if (rapid == null || rapid.Captures == null || keptIndices == null || keptIndices.Length == 0)
                throw new ArgumentException("无可用捕获做平均。");
            int len = -1;
            for (int i = 0; i < keptIndices.Length; i++)
            {
                int idx = keptIndices[i];
                if (idx < 0 || idx >= rapid.Captures.Length || rapid.Captures[idx] == null)
                    throw new ArgumentException("保留索引无效：" + idx);
                if (len < 0) len = rapid.Captures[idx].Length;
                else if (rapid.Captures[idx].Length != len)
                    throw new InvalidOperationException("捕获长度不一致，无法按点平均。");
            }
            var sum = new double[len];
            for (int i = 0; i < keptIndices.Length; i++)
            {
                short[] w = rapid.Captures[keptIndices[i]];
                for (int s = 0; s < len; s++) sum[s] += w[s];
            }
            double inv = 1.0 / keptIndices.Length;
            var avg = new short[len];
            for (int s = 0; s < len; s++)
                avg[s] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(sum[s] * inv)));
            return avg;
        }

        static double ToNanoseconds(long value, int timeUnits)
        {
            // PS5000A_TIME_UNITS: 0=fs, 1=ps, 2=ns, 3=us, 4=ms, 5=s
            switch (timeUnits)
            {
                case 0: return value * 1e-6;
                case 1: return value * 1e-3;
                case 2: return value;
                case 3: return value * 1e3;
                case 4: return value * 1e6;
                case 5: return value * 1e9;
                default: return value;
            }
        }

        static double Median(List<double> sorted)
        {
            int n = sorted.Count;
            if (n == 0) return double.NaN;
            if ((n & 1) == 1) return sorted[n / 2];
            return 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
        }
    }

    public sealed class CalibrationPulseIntervalFilterResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int[] KeptIndices { get; set; }
        public int[] RejectedIndices { get; set; }
        public int RejectedCount { get; set; }
        public int TrimmedExtraKept { get; set; }
        public double NominalPeriodNanoseconds { get; set; }
        public double MeasuredMedianPeriodNanoseconds { get; set; }
        public string NominalSource { get; set; }

        public CalibrationPulseIntervalFilterResult()
        {
            Message = string.Empty;
            KeptIndices = new int[0];
            RejectedIndices = new int[0];
            NominalPeriodNanoseconds = double.NaN;
            MeasuredMedianPeriodNanoseconds = double.NaN;
            NominalSource = string.Empty;
        }
    }
}
