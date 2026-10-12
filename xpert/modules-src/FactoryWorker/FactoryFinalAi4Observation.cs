using System;
using System.Globalization;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // A separate end-of-sequence measurement, never a discharge clearance or
    // authorization to start another pressure round. Historical XML omits it.
    [Serializable]
    public sealed class FactoryFinalAi4Observation
    {
        public const int RequiredSampleCount = 800;
        public const double RequiredSampleRateHz = 80000;
        public bool Requested, Available, Simulation;
        public double ThresholdV = 1000, WindowMs = 10;
        public double SampleRateHz;
        public int SampleCount;
        public DateTime StartedUtc, EndedUtc;
        public double[] RawVolts = new double[0];
        public double DividerVPerV;
        public double ArtFullScaleV=10;
        public double MeanV = Double.NaN;
        // abs(signed mean), NOT mean(abs(samples)). Peak/RMS preserve evidence
        // of an alternating voltage whose signed mean cancels to near zero.
        public double AbsoluteMeanV = Double.NaN;
        public double AbsolutePeakV = Double.NaN;
        public double RmsV = Double.NaN;
        public bool AbsMeanBelowThreshold;
        public string Error = "", Detail = "";

        public static FactoryFinalAi4Observation Evaluate(double[] raw, double actualRate,
            double divider, bool simulation, DateTime start, DateTime end,double artFullScaleV=10)
        {
            var result = new FactoryFinalAi4Observation {
                Requested = true, Simulation = simulation, SampleRateHz = actualRate,
                SampleCount = raw == null ? 0 : raw.Length,
                RawVolts = raw == null ? new double[0] : (double[])raw.Clone(),
                DividerVPerV = divider, ArtFullScaleV=artFullScaleV, StartedUtc = start, EndedUtc = end
            };
            if (raw == null || raw.Length != RequiredSampleCount)
                return Invalid(result, "AI4末轮窗口点数不完整：须为新采800点，不补零、不拼接旧段。");
            if (!Finite(actualRate) || Math.Abs(actualRate - RequiredSampleRateHz) > .5)
                return Invalid(result, "AI4实际采样率未通过80 kS/s回读校验。");
            if (!Finite(divider) || divider <= 0)
                return Invalid(result, "分压换算比必须为有限正数。");
            if(!Finite(artFullScaleV)||artFullScaleV<=0||artFullScaleV>10)return Invalid(result,"分压器输入量程无效。");
            if (start == DateTime.MinValue || end == DateTime.MinValue ||
                start.Kind != DateTimeKind.Utc || end.Kind != DateTimeKind.Utc ||
                end < start || (end - start).TotalMilliseconds > 100)
                return Invalid(result, "AI4宿主采样时间无效、倒退或超过100 ms；不是有效10 ms新窗口。");

            // Calculate in bounded raw volts first. Conversion afterwards avoids
            // overflowing a sum of large converted voltage values.
            double sum = 0, correction = 0, squareSum = 0, peak = 0;
            foreach (double value in result.RawVolts)
            {
                if (!Finite(value)) return Invalid(result, "AI4原始窗口含非有限样点。");
                double absolute = Math.Abs(value);
                if (absolute >= artFullScaleV*.995)
                    return Invalid(result, "AI4原始输入达到本轮输入量程的饱和保护区；均值指标无效。");
                double adjusted = value - correction;
                double next = sum + adjusted;
                correction = (next - sum) - adjusted;
                sum = next;
                squareSum += value * value;
                peak = Math.Max(peak, absolute);
            }
            result.MeanV = sum / RequiredSampleCount * divider;
            result.AbsoluteMeanV = Math.Abs(result.MeanV);
            result.AbsolutePeakV = peak * divider;
            result.RmsV = Math.Sqrt(squareSum / RequiredSampleCount) * divider;
            if (!Finite(result.MeanV) || !Finite(result.AbsoluteMeanV) ||
                !Finite(result.AbsolutePeakV) || !Finite(result.RmsV))
                return Invalid(result, "AI4换算结果非有限，均值指标无效。");
            result.Available = true;
            result.AbsMeanBelowThreshold = result.AbsoluteMeanV < result.ThresholdV;
            result.Detail = "新采AI4单段800点，回读约80 kS/s，对应约10 ms；判据为有符号均值的绝对值<1000 V，" +
                "不是全部样点持续低于阈值。正负电压可能相互抵消；同时保留绝对峰值和RMS。" +
                "低读数不证明接线有效、已放电或人员可接近，不作为中间轮或下一轮加压许可。";
            return result;
        }

        private static FactoryFinalAi4Observation Invalid(FactoryFinalAi4Observation result, string reason)
        {
            result.Available = false;
            result.AbsMeanBelowThreshold = false;
            result.MeanV = result.AbsoluteMeanV = result.AbsolutePeakV = result.RmsV = Double.NaN;
            result.Error = reason;
            result.Detail = "仅末轮AI4诊断；无效测量不能判为低于阈值，不代表已放电，不提供加压许可。";
            return result;
        }

        public string Describe()
        {
            string prefix = (Simulation ? "仿真 · " : "") + "末轮AI4均值观察：";
            if (!Requested) return prefix + "未请求，不代表已测或已放电。";
            if (!Available) return prefix + "不可用；" + (String.IsNullOrEmpty(Error) ? "未取得有效新窗口。" : Error) +
                " 不作为放电确认或加压许可。";
            return prefix + "有符号均值 " + Format(MeanV) + " V，均值绝对值 " + Format(AbsoluteMeanV) +
                " V（" + (AbsMeanBelowThreshold ? "低于" : "不低于") + Format(ThresholdV) +
                " V）；绝对峰值 " + Format(AbsolutePeakV) + " V，RMS " + Format(RmsV) +
                " V；" + SampleCount + "点/" + Format(SampleRateHz) + " S/s。" + Detail;
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static string Format(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    internal interface IFactoryFinalAi4Observation
    {
        FactoryFinalAi4Observation ReadFinalAi4Observation(CancellationToken token);
    }

    // Keep evidence already read before a later permission/cleanup failure.
    // Carrying this record never suppresses the failure or grants validity.
    internal sealed class FactoryFinalAi4ObservationException : InvalidOperationException
    {
        internal readonly FactoryFinalAi4Observation PartialObservation;
        internal FactoryFinalAi4ObservationException(FactoryFinalAi4Observation observation, Exception inner)
            : base("末轮AI4诊断或清理失败；保留已取得的观察数据，不代表放电确认。", inner)
        { PartialObservation = observation; }
    }
}
