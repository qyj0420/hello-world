using System;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 校准采集窗：pre = 0.5×(L/v)；总长 = pre + 3×(L/v)。
    /// v 默认对齐 Algorithms ConnectingCableGeometry.ConnectingCableVelocityMPerUs（150 m/µs）。
    /// </summary>
    public static class CalibrationCaptureTiming
    {
        /// <summary>与 ConnectingCableGeometry.ConnectingCableVelocityMPerUs 一致（m/µs）。</summary>
        public const double DefaultVelocityMPerUs = 150.0;

        /// <summary>默认被试/引线等效长度（m）；UI/配置可覆盖。</summary>
        public const double DefaultCableLengthM = 100.0;

        public static CalibrationCaptureTimingResult Compute(
            double cableLengthM,
            double velocityMPerUs,
            double sampleIntervalNanoseconds)
        {
            if (!(cableLengthM > 0) || double.IsNaN(cableLengthM) || double.IsInfinity(cableLengthM))
                throw new ArgumentOutOfRangeException("cableLengthM", "电缆长度 L 须为正有限值（m）。");
            if (!(velocityMPerUs > 0) || double.IsNaN(velocityMPerUs) || double.IsInfinity(velocityMPerUs))
                throw new ArgumentOutOfRangeException("velocityMPerUs", "波速 v 须为正有限值（m/µs）。");
            double intervalNs = sampleIntervalNanoseconds > 0 ? sampleIntervalNanoseconds : 8.0;

            // L/v → µs；×1e-6 → s
            double transitUs = cableLengthM / velocityMPerUs;
            double preSeconds = 0.5 * transitUs * 1e-6;
            double totalSeconds = preSeconds + 3.0 * transitUs * 1e-6;
            double intervalSec = intervalNs * 1e-9;

            int pre = Math.Max(0, (int)Math.Round(preSeconds / intervalSec));
            int total = Math.Max(pre + 1, (int)Math.Round(totalSeconds / intervalSec));
            int post = Math.Max(1, total - pre);

            // 硬顶：不超过 Field 单段容量，避免 MemorySegments 失败
            int maxTotal = LiveHardwareOptions.FieldPicoSampleCount;
            if (total > maxTotal)
            {
                double scale = (double)maxTotal / total;
                pre = Math.Max(0, (int)Math.Floor(pre * scale));
                total = maxTotal;
                post = Math.Max(1, total - pre);
            }

            return new CalibrationCaptureTimingResult
            {
                CableLengthM = cableLengthM,
                VelocityMPerUs = velocityMPerUs,
                TransitTimeUs = transitUs,
                PreTriggerSeconds = preSeconds,
                TotalSeconds = totalSeconds,
                SampleIntervalNanoseconds = intervalNs,
                PreTriggerSamples = pre,
                PostTriggerSamples = post,
                TotalSamples = pre + post
            };
        }

        public static CalibrationCaptureTimingResult ComputeDefault(double sampleIntervalNanoseconds)
        {
            return Compute(DefaultCableLengthM, DefaultVelocityMPerUs, sampleIntervalNanoseconds);
        }
    }

    public sealed class CalibrationCaptureTimingResult
    {
        public double CableLengthM { get; set; }
        public double VelocityMPerUs { get; set; }
        /// <summary>单程传播时间 L/v（µs）。</summary>
        public double TransitTimeUs { get; set; }
        public double PreTriggerSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public double SampleIntervalNanoseconds { get; set; }
        public int PreTriggerSamples { get; set; }
        public int PostTriggerSamples { get; set; }
        public int TotalSamples { get; set; }
    }
}
