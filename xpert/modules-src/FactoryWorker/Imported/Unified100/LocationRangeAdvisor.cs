// Source snapshot: D:/codex/HVXUnifiedDesktop/modules-src/PressureTest/vendor/Task39/LocationRangeAdvisor.cs
using System;
using System.Globalization;

namespace Xiongxin.ZeroBench
{
    /// <summary>Pure, serializable diagnostics. Recommendations apply only to a subsequent capture.</summary>
    public sealed class LocationRangeResult
    {
        public int RequestedLocationSampleCount;
        public int LocationSampleCount;
        public int FullSampleCount;
        public int OutsideSampleCount;
        public int WindowStartSample;
        public int WindowEndExclusive;
        public string WindowDefinition = "触发起点开始的定位原始窗口";
        public bool WindowValid = true;
        public bool WindowShortened;
        public int ClippingAdcThreshold;
        public double LocationPeakVolts;
        public double LocationPeakFraction;
        public int LocationClippedSamples;
        public int LocationLongestClippedRun;
        public double FullPeakVolts;
        public int FullClippedSamples;
        public int FullLongestClippedRun;
        public double OutsidePeakVolts;
        public int OutsideClippedSamples;
        public int OutsideLongestClippedRun;
        public bool LocationHighOccupancy;
        public bool LocationLowOccupancy;
        public bool LocationQualityPassed;
        public string LocationQuality = "未评价";
        public bool DriverOverflow;
        public bool DriverOverflowUncertain;
        // Factory-only window policy. Raw/global overflow flags above are never cleared.
        public bool SwitchingTransientExcluded;
        public int SwitchingTransientEndSample;
        public int SwitchingTransientClippedSamples;
        public int SwitchingTransientLastClippedSample = -1;
        public bool WindowRangeQualityPassed;
        public bool FactoryCycleWindow, OutsideCycleClippingExcluded;
        public int EvaluationCycles;
        public double ExpectedCalibrationVolts;
        public int RecommendedIndex;
        public double RecommendedFullScaleVolts;
        public bool Change;
        public string Reason = "";
        public bool RecommendationUncertain;
        public bool MaximumRangeInsufficient;
        public bool AtMaximumAndClipped;
        public string SafetyNote = "ADC 削顶仅表示采集量程边界，不证明模拟输入保护状态、安全电压或设备已放电。建议仅作用于下一轮采集。";
    }

    /// <summary>
    /// Evaluates the raw, uncorrected Pico samples from trigger time through the supplied location window.
    /// No baseline removal, pulse classifier, or 80 kS/s detector can hide saturation from this decision.
    /// Caller supplies the samples for min(20 ms, capture duration) as locationCount.
    /// </summary>
    public static class LocationRangeAdvisor
    {
        public const double HighOccupancyFraction = .80;
        public const double LowOccupancyFraction = .10;
        public const double TargetPeakFraction = .65;
        public const double ClippedExpansionFactor = 4.0;

        public static LocationRangeResult Evaluate(short[] fullSamples, int locationCount, int maxAdc,
            double fullScaleV, int currentRangeIndex, double calibrationPulsePc, double coefficient,
            bool driverOverflow)
        {
            if (fullSamples == null || fullSamples.Length == 0) throw new ArgumentException("缺少原始 Pico ADC 数据。", "fullSamples");
            if (locationCount <= 0) throw new ArgumentOutOfRangeException("locationCount", "定位窗口必须至少包含一个采样点。");
            var result = Evaluate(fullSamples, 0, Math.Min(locationCount, fullSamples.Length), maxAdc,
                fullScaleV, currentRangeIndex, calibrationPulsePc, coefficient, driverOverflow);
            result.RequestedLocationSampleCount = locationCount;
            result.WindowShortened = locationCount > fullSamples.Length;
            if (result.WindowShortened)
                result.LocationQuality += "；记录短于请求窗口，已使用全部 " + result.LocationSampleCount + " 个原始采样点";
            return result;
        }

        public static LocationRangeResult Evaluate(short[] fullSamples, LocationRangeWindowResult window, int maxAdc,
            double fullScaleV, int currentRangeIndex, double calibrationPulsePc, double coefficient,
            bool driverOverflow)
        {
            if (window == null) throw new ArgumentNullException("window");
            if (!window.Valid) return InvalidWindow(fullSamples, window.Definition, window.FailureReason, driverOverflow);
            if (fullSamples == null || window.StartSample < 0 || window.Count <= 0 ||
                (long)window.StartSample + window.Count > fullSamples.Length ||
                (long)window.StartSample + window.Count != window.EndExclusive)
                return InvalidWindow(fullSamples, window.Definition, "完整分析窗口不在本轮原始数据范围内，禁止缩短或改用默认起点。", driverOverflow);
            var result = Evaluate(fullSamples, window.StartSample, window.Count, maxAdc,
                fullScaleV, currentRangeIndex, calibrationPulsePc, coefficient, driverOverflow);
            result.WindowDefinition = window.Definition;
            return result;
        }

        // This explicit-window overload is strict: a partial N-cycle window is never silently accepted.
        public static LocationRangeResult Evaluate(short[] fullSamples, int startIndex, int count, int maxAdc,
            double fullScaleV, int currentRangeIndex, double calibrationPulsePc, double coefficient,
            bool driverOverflow)
        {
            if (fullSamples == null || fullSamples.Length == 0) throw new ArgumentException("缺少原始 Pico ADC 数据。", "fullSamples");
            if (startIndex < 0 || count <= 0 || (long)startIndex + count > fullSamples.Length)
                throw new ArgumentOutOfRangeException("count", "定位分析窗口必须完整包含在原始记录内，不允许静默缩短。");
            if (maxAdc <= 0 || maxAdc > Int16.MaxValue) throw new ArgumentOutOfRangeException("maxAdc");
            if (currentRangeIndex < 0 || currentRangeIndex >= BenchSettings.Ranges.Length) throw new ArgumentOutOfRangeException("currentRangeIndex");
            if (!Finite(fullScaleV) || fullScaleV <= 0 ||
                Math.Abs(fullScaleV - BenchSettings.Ranges[currentRangeIndex]) > Math.Max(1e-12, fullScaleV * 1e-9))
                throw new ArgumentException("本轮满量程与量程索引不一致。", "fullScaleV");
            if (!Finite(calibrationPulsePc) || calibrationPulsePc < 0) throw new ArgumentOutOfRangeException("calibrationPulsePc");
            if (!Finite(coefficient) || coefficient <= 0) throw new ArgumentOutOfRangeException("coefficient");
            double expected = calibrationPulsePc / coefficient;
            if (!Finite(expected) || !Finite(expected / TargetPeakFraction))
                throw new ArgumentException("校准脉冲预期幅值超出有效数值范围。");

            var result = new LocationRangeResult
            {
                RequestedLocationSampleCount = count,
                LocationSampleCount = count,
                FullSampleCount = fullSamples.Length,
                WindowStartSample = startIndex,
                WindowEndExclusive = startIndex + count,
                WindowDefinition = "原始样点 [" + startIndex + ", " + (startIndex + count) + ")；窗口内不扣除过冲",
                ClippingAdcThreshold = maxAdc,
                DriverOverflow = driverOverflow,
                ExpectedCalibrationVolts = expected
            };
            result.OutsideSampleCount = result.FullSampleCount - result.LocationSampleCount;
            int fullRun = 0, locationRun = 0, outsideRun = 0;
            int fullPeakCode = 0, locationPeakCode = 0, outsidePeakCode = 0;
            for (int i = 0; i < fullSamples.Length; i++)
            {
                // Widen first: Math.Abs(Int16.MinValue) must not overflow or conceal negative saturation.
                int absolute = Math.Abs((int)fullSamples[i]);
                bool clipped = absolute >= maxAdc;
                fullPeakCode = Math.Max(fullPeakCode, absolute);
                if (clipped)
                {
                    result.FullClippedSamples++;
                    result.FullLongestClippedRun = Math.Max(result.FullLongestClippedRun, ++fullRun);
                }
                else fullRun = 0;
                if (i >= result.WindowStartSample && i < result.WindowEndExclusive)
                {
                    // Prefix and suffix are disjoint: a clipped run may not jump across the window.
                    outsideRun = 0;
                    locationPeakCode = Math.Max(locationPeakCode, absolute);
                    if (clipped)
                    {
                        result.LocationClippedSamples++;
                        result.LocationLongestClippedRun = Math.Max(result.LocationLongestClippedRun, ++locationRun);
                    }
                    else locationRun = 0;
                }
                else
                {
                    locationRun = 0;
                    outsidePeakCode = Math.Max(outsidePeakCode, absolute);
                    if (clipped)
                    {
                        result.OutsideClippedSamples++;
                        result.OutsideLongestClippedRun = Math.Max(result.OutsideLongestClippedRun, ++outsideRun);
                    }
                    else outsideRun = 0;
                }
            }
            result.LocationPeakFraction = (double)locationPeakCode / maxAdc;
            result.LocationPeakVolts = result.LocationPeakFraction * fullScaleV;
            result.FullPeakVolts = (double)fullPeakCode / maxAdc * fullScaleV;
            result.OutsidePeakVolts = (double)outsidePeakCode / maxAdc * fullScaleV;
            result.LocationHighOccupancy = result.LocationPeakFraction >= HighOccupancyFraction;
            result.LocationLowOccupancy = result.LocationPeakFraction < LowOccupancyFraction;
            result.DriverOverflowUncertain = driverOverflow && result.LocationClippedSamples == 0;
            result.RecommendationUncertain = result.LocationClippedSamples > 0 || result.DriverOverflowUncertain;
            result.LocationQualityPassed = result.LocationClippedSamples == 0 && !result.LocationLowOccupancy && !result.LocationHighOccupancy && !result.DriverOverflowUncertain;

            if (result.LocationClippedSamples > 0)
                result.LocationQuality = "定位窗口削顶：峰值仅为下限，真实峰值未知，本轮定位幅值不合格";
            else if (result.DriverOverflowUncertain)
                result.LocationQuality = "定位窗口未见边界采样，但驱动报告全段溢出：质量不确定，需核验，不能判为通过";
            else if (result.LocationHighOccupancy)
                result.LocationQuality = "定位窗口未削顶，但原始峰值达到或超过 80% 满幅：余量不足";
            else if (result.LocationLowOccupancy)
                result.LocationQuality = "量程偏大：未见削顶，但占用低于 10%，建议缩小下一轮量程（背景不能证明定位信噪比合格）";
            else
                result.LocationQuality = "定位窗口量程通过：未见 ADC 削顶，原始峰值占用在 10% 至低于 80% 满幅（不等于定位算法、信噪比或放电性质通过）";
            if (result.OutsideClippedSamples > 0)
                result.LocationQuality += "；窗外另有 " + result.OutsideClippedSamples + " 点削顶，单独记录，不直接替代定位窗口结论";

            double requiredFullScale = Math.Max(result.LocationPeakVolts, expected) / TargetPeakFraction;
            if (result.LocationClippedSamples > 0)
                requiredFullScale = Math.Max(requiredFullScale, fullScaleV * ClippedExpansionFactor);
            int index = FitRange(requiredFullScale);
            // A global overflow flag has no timestamp. Do not reduce the range based on apparently
            // clean window samples until the ambiguity is checked; increasing for local demand is allowed.
            if (result.DriverOverflowUncertain) index = Math.Max(currentRangeIndex, index);
            result.RecommendedIndex = index;
            result.RecommendedFullScaleVolts = BenchSettings.Ranges[index];
            result.Change = index != currentRangeIndex;
            result.MaximumRangeInsufficient = requiredFullScale > BenchSettings.Ranges[BenchSettings.Ranges.Length - 1];
            result.AtMaximumAndClipped = currentRangeIndex == BenchSettings.Ranges.Length - 1 && result.LocationClippedSamples > 0;
            if (result.LocationClippedSamples > 0)
            {
                result.Reason = result.AtMaximumAndClipped
                    ? "当前已为 ±20 V 最大量程且定位窗口仍削顶：没有可用的更大量程，不能恢复本轮真实峰值"
                    : "定位窗口削顶，真实峰值未知；按至少当前满幅 4 倍并结合校准预期幅值，保守跨档建议下一轮 ±" + Format(result.RecommendedFullScaleVolts) + " V；此建议不保证消除削顶，必须复核下一轮";
            }
            else if (result.DriverOverflowUncertain)
                result.Reason = "驱动全段溢出而定位窗内无边界点，溢出位置/原因需核验；不据此自动降档。下一轮建议 ±" + Format(result.RecommendedFullScaleVolts) + " V，质量仍不确定";
            else
                result.Reason = "以定位原始峰值 " + Format(result.LocationPeakVolts) + " V 与校准脉冲预期 " + Format(expected) + " V 的较大者，按 65% 满幅目标直接选档：下一轮 ±" + Format(result.RecommendedFullScaleVolts) + " V；无需逐档试探，不依赖检出脉冲候选";
            if (result.MaximumRangeInsufficient)
                result.Reason += "；计算所需满幅超过 ±20 V，当前推荐已受上限限制，不能满足所需余量或保证无削顶";
            if (result.OutsideClippedSamples > 0)
                result.Reason += "；窗外削顶及过冲未用于决定定位量程，但仍保留全段风险提示";
            return result;
        }

        private static LocationRangeResult InvalidWindow(short[] samples, string definition, string reason, bool overflow)
        {
            return new LocationRangeResult
            {
                WindowValid = false,
                WindowDefinition = definition ?? "无有效定位量程分析窗口",
                FullSampleCount = samples == null ? 0 : samples.Length,
                LocationPeakVolts = Double.NaN,
                LocationPeakFraction = Double.NaN,
                FullPeakVolts = Double.NaN,
                OutsidePeakVolts = Double.NaN,
                RecommendedIndex = -1,
                RecommendedFullScaleVolts = Double.NaN,
                Change = false,
                RecommendationUncertain = true,
                LocationQualityPassed = false,
                LocationQuality = "定位量程窗口无效：" + reason,
                Reason = "没有完整且经验证的分析窗口，禁止自动量程建议或应用：" + reason,
                DriverOverflow = overflow,
                DriverOverflowUncertain = overflow
            };
        }

        private static int FitRange(double requiredFullScale)
        {
            for (int i = 0; i < BenchSettings.Ranges.Length; i++)
                if (BenchSettings.Ranges[i] >= requiredFullScale) return i;
            return BenchSettings.Ranges.Length - 1;
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static string Format(double value) { return value.ToString("G5", CultureInfo.InvariantCulture); }
    }
}


