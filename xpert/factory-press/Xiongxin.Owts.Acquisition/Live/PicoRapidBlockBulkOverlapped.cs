using System;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// Rapid Block Bulk Overlapped 布防参数（通道 A；校准用自动触发+低电平，加压仍用 EXT 单段 Block）。
    /// </summary>
    public sealed class RapidBlockArmRequest
    {
        public int RangeIndex { get; set; }
        /// <summary>重复触发段数（≥1）。</summary>
        public int CaptureCount { get; set; }
        public int PreTriggerSamples { get; set; }
        public int PostTriggerSamples { get; set; }
        /// <summary>通道 A 触发电平（V）。校准：低电平（~1%FS）+ AutoTrigger；加压 EXT 路径不走本字段。</summary>
        public double ChannelATriggerThresholdVolts { get; set; }
        /// <summary>自动触发毫秒；校准须 &gt;0。0=一直等边沿（校准禁用，易空波）。</summary>
        public short AutoTriggerMilliseconds { get; set; }

        public RapidBlockArmRequest()
        {
            RangeIndex = 6;
            CaptureCount = 8;
            PreTriggerSamples = 0;
            PostTriggerSamples = 4096;
            ChannelATriggerThresholdVolts = 0.5;
            AutoTriggerMilliseconds = 3000;
        }
    }

    /// <summary>Rapid Block 多段采集结果。</summary>
    public sealed class PicoRapidBlockCaptureResult
    {
        /// <summary>[captureIndex][sample]；每段长度 = Pre+Post。</summary>
        public short[][] Captures { get; set; }
        public long[] TriggerTimeOffsets { get; set; }
        public int[] TriggerTimeUnits { get; set; }
        public short[] OverflowPerCapture { get; set; }
        public double SampleIntervalNanoseconds { get; set; }
        public double SampleRateHz { get; set; }
        public double FullScaleVolts { get; set; }
        public short MaximumAdc { get; set; }
        public int RangeIndex { get; set; }
        public int PreTriggerSamples { get; set; }
        public int PostTriggerSamples { get; set; }
        public int CaptureCount { get; set; }
        public string Serial { get; set; }

        public PicoRapidBlockCaptureResult()
        {
            Captures = new short[0][];
            TriggerTimeOffsets = new long[0];
            TriggerTimeUnits = new int[0];
            OverflowPerCapture = new short[0];
            MaximumAdc = 32512;
            Serial = string.Empty;
        }

        public bool AnyHardwareOverflow
        {
            get
            {
                if (OverflowPerCapture == null) return false;
                for (int i = 0; i < OverflowPerCapture.Length; i++)
                    if (OverflowPerCapture[i] != 0) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Rapid Block Bulk Overlapped 辅助：触发电平、段数校验等纯逻辑（无 SDK 调用）。
    /// </summary>
    public static class PicoRapidBlockBulkOverlapped
    {
        public const int DefaultCaptureCount = 8;
        public const int MinCaptureCount = 1;
        public const int MaxCaptureCount = 64;

        public static int ClampCaptureCount(int n)
        {
            if (n < MinCaptureCount) return MinCaptureCount;
            if (n > MaxCaptureCount) return MaxCaptureCount;
            return n;
        }

        /// <summary>通道 A 触发电平（V）← 满量程 × 利用率（默认 Task6 50%）。</summary>
        public static double ChannelATriggerVoltsFromRange(int rangeIndex, double utilizationFraction)
        {
            double[] table = LiveHardwareOptions.Pico5242DFullScaleVolts;
            if (rangeIndex < 0 || rangeIndex >= table.Length)
                throw new ArgumentOutOfRangeException("rangeIndex");
            double util = utilizationFraction > 0 && utilizationFraction <= 1.0
                ? utilizationFraction
                : Task6RangeAdvisor.TargetUtilization;
            return table[rangeIndex] * util;
        }

        /// <summary>伏特 → SetSimpleTrigger 用 ADC 阈值（相对当前满量程 / maximumAdc）。</summary>
        public static short VoltsToAdcThreshold(double thresholdVolts, double fullScaleVolts, short maximumAdc)
        {
            if (!(fullScaleVolts > 0) || maximumAdc <= 0) return 0;
            double frac = thresholdVolts / fullScaleVolts;
            if (frac < 0) frac = 0;
            if (frac > 0.95) frac = 0.95;
            int adc = (int)Math.Round(frac * maximumAdc);
            if (adc < 1) adc = 1;
            if (adc > maximumAdc) adc = maximumAdc;
            return (short)adc;
        }
    }
}
