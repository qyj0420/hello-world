using System;
using System.Globalization;

namespace Xiongxin.ZeroBench
{
    public sealed class LocationRangeWindowResult
    {
        public bool Valid;
        public int StartSample;
        public int Count;
        public int EndExclusive;
        public string Definition = "";
        public string FailureReason = "";
        public double TargetVoltage;
        public int RequestedCycles;
        public double ActualSampleRateHz;
        public double StartTimeSeconds;
        public double EndTimeSeconds;
    }

    /// <summary>
    /// Window selection only; never detects or invents a first zero phase/frequency.
    /// Times and indices are relative to the original capture trigger (sample zero).
    /// A nonzero target requires caller-validated voltage phase and frequency measurements.
    /// This analysis API does not authorize HV output or relax the 0 V capture safety restriction.
    /// </summary>
    public static class LocationRangeWindow
    {
        public static LocationRangeWindowResult Select(double targetVoltage, int availableStartSample,
            int availableCount, double actualFs, int cycles, double validatedFirstZeroTimeSeconds,
            double validatedFrequencyHz, bool phaseValidated)
        {
            var result = new LocationRangeWindowResult
            {
                TargetVoltage = targetVoltage,
                RequestedCycles = targetVoltage == 0 ? 0 : cycles,
                ActualSampleRateHz = actualFs
            };
            if (!Finite(targetVoltage)) return Invalid(result, "目标电压无效。");
            if (availableStartSample < 0 || availableCount <= 0 || (long)availableStartSample + availableCount > Int32.MaxValue)
                return Invalid(result, "定位可用原始段范围无效。");
            if (!Finite(actualFs) || actualFs <= 0) return Invalid(result, "实际采样率无效。");
            int availableEnd = availableStartSample + availableCount;
            if (targetVoltage == 0)
            {
                result.Valid = true;
                result.StartSample = availableStartSample;
                result.Count = availableCount;
                result.EndExclusive = availableEnd;
                result.StartTimeSeconds = availableStartSample / actualFs;
                result.EndTimeSeconds = availableEnd / actualFs;
                result.Definition = "0 V：采用全部已提供定位原始段 [" + availableStartSample + ", " + availableEnd + ")，不跳过起始过冲，不依赖相位、频率或周期 N";
                return result;
            }
            if (!phaseValidated) return Invalid(result, "非零目标尚无经验证的电压第一零相位，不能默认从样点 0 开始。");
            if (!Finite(validatedFirstZeroTimeSeconds) || validatedFirstZeroTimeSeconds < 0 ||
                !Finite(validatedFrequencyHz) || validatedFrequencyHz <= 0 || cycles <= 0)
                return Invalid(result, "非零目标必须提供有效的第一零相位时间、已验证频率及正整数周期 N。");
            double endTime = validatedFirstZeroTimeSeconds + cycles / validatedFrequencyHz;
            double firstPosition = validatedFirstZeroTimeSeconds * actualFs;
            double endPosition = endTime * actualFs;
            if (!Finite(endTime) || !Finite(firstPosition) || !Finite(endPosition) ||
                endTime <= validatedFirstZeroTimeSeconds || endPosition > Int32.MaxValue)
                return Invalid(result, "周期窗口边界超出有效数值范围。");
            if (validatedFirstZeroTimeSeconds < availableStartSample / actualFs || endTime > availableEnd / actualFs)
                return Invalid(result, "可用定位段未覆盖从电压第一零相位起的完整 " + cycles + " 个周期，不允许缩短 N 或改用默认起点。");
            int start = (int)Math.Ceiling(firstPosition);
            int end = (int)Math.Ceiling(endPosition);
            if (start < availableStartSample || end > availableEnd || end <= start)
                return Invalid(result, "按实际采样率向上取整后无法取得完整且非空的 N 周期样点窗口。");
            result.Valid = true;
            result.StartSample = start;
            result.Count = end - start;
            result.EndExclusive = end;
            result.StartTimeSeconds = validatedFirstZeroTimeSeconds;
            result.EndTimeSeconds = endTime;
            result.Definition = "非零目标：经验证的电压第一零相位 " + validatedFirstZeroTimeSeconds.ToString("G9", CultureInfo.InvariantCulture) +
                " s 起，前 " + cycles + " 个完整周期（" + validatedFrequencyHz.ToString("G9", CultureInfo.InvariantCulture) +
                " Hz）；原始样点 [" + start + ", " + end + ")，两端按实际采样率 ceil 映射，窗口内不扣除过冲";
            return result;
        }

        private static LocationRangeWindowResult Invalid(LocationRangeWindowResult result, string reason)
        {
            result.Valid = false;
            result.FailureReason = reason;
            result.Definition = "无有效定位量程窗口：" + reason;
            return result;
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
