using System;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Range planning only: no output permission, hardware command or mutation of raw samples.
    internal static class FactoryTransientRangePolicy
    {
        internal static void Apply(BenchCapture capture, LocationRangeResult quality, bool nonzeroPressure)
        {
            quality.WindowRangeQualityPassed = quality.LocationQualityPassed;
            if (!nonzeroPressure || !quality.WindowValid || quality.WindowStartSample <= 0 ||
                quality.FullClippedSamples <= 0 || quality.LocationClippedSamples != 0) return;
            if(quality.FactoryCycleWindow)
            {
                // The selected N-cycle window is the sole range input. Known
                // clipping outside it is logged, not labelled as window clipping.
                // This does not assert that all outside clipping is switch-on.
                var local=LocationRangeAdvisor.Evaluate(capture.PdAdc,quality.WindowStartSample,
                    quality.LocationSampleCount,capture.MaximumAdc,capture.PicoFullScaleV,
                    capture.Settings.RangeIndex,0,capture.Settings.PdCalibrationPcPerV,false);
                quality.OutsideCycleClippingExcluded=true;
                quality.WindowRangeQualityPassed=local.LocationQualityPassed;
                quality.RecommendedIndex=local.RecommendedIndex;
                quality.RecommendedFullScaleVolts=local.RecommendedFullScaleVolts;
                quality.Change=local.Change;quality.MaximumRangeInsufficient=local.MaximumRangeInsufficient;
                quality.Reason=local.Reason+"；定位N周期窗外 "+quality.OutsideClippedSamples+
                    " 点削顶不参与量程推荐；全段ADC、削顶和驱动溢出标志原样保留，未声明原始全段合格。";
                quality.LocationQuality+="；下一轮量程独立按定位保存N周期窗口评估。";
                return;
            }
            // The caller's validated voltage FIRST ZERO is the range window boundary.
            // Do not replace it with the separate pulse-analysis transient-time setting.
            // Never attribute arbitrary late/outside-window clipping to switch-on overshoot.
            int end = quality.WindowStartSample;
            int clipped = 0, last = -1;
            for (int i = 0; i < capture.PdAdc.Length; i++)
                if (Math.Abs((int)capture.PdAdc[i]) >= capture.MaximumAdc)
                {
                    if (i >= end || i<capture.Settings.PicoPreTriggerCount) return;
                    ++clipped; last = i;
                }
            if (clipped != quality.FullClippedSamples || last < 0) return;
            // Re-evaluate the SAME validated raw window for range planning. Keep the original
            // full-segment/global overflow uncertainty and all downstream analysis restrictions.
            var window = LocationRangeAdvisor.Evaluate(capture.PdAdc, quality.WindowStartSample,
                quality.LocationSampleCount, capture.MaximumAdc, capture.PicoFullScaleV,
                capture.Settings.RangeIndex, capture.Settings.CalibrationPulsePc,
                capture.Settings.PdCalibrationPcPerV, false);
            quality.SwitchingTransientExcluded = true;
            quality.SwitchingTransientEndSample = end;
            quality.SwitchingTransientClippedSamples = clipped;
            quality.SwitchingTransientLastClippedSample = last;
            quality.WindowRangeQualityPassed = window.LocationQualityPassed;
            quality.RecommendedIndex = window.RecommendedIndex;
            quality.RecommendedFullScaleVolts = window.RecommendedFullScaleVolts;
            quality.Change = window.Change;
            quality.MaximumRangeInsufficient = window.MaximumRangeInsufficient;
            quality.Reason = window.Reason + "；电压第一过零点之前 [0," + end + ") 内 " + clipped +
                " 点削顶已排除于量程评估，最后削顶样点 " + last + "；有效窗口未改动。" +
                "全段削顶/驱动溢出及原有质量标记保留；此决定不证明模拟前端恢复、设备放电或允许再次加压。";
            quality.LocationQuality += "；下一轮量程单独按第一过零点起的有效周期窗口评估，过零点前转换过冲不作为队列中断依据。";
        }

        internal static bool BlocksForOverflow(LocationRangeResult q, int current)
        {
            // Unlocalized overflow remains blocking; maximum input range risk is never hidden.
            return q.DriverOverflowUncertain && !q.SwitchingTransientExcluded && !q.OutsideCycleClippingExcluded ||
                current == BenchSettings.Ranges.Length - 1 && q.DriverOverflow;
        }

        internal static bool BlocksDownrange(LocationRangeResult q)
        { return (q.RecommendationUncertain || q.FullClippedSamples > 0) && !q.SwitchingTransientExcluded && !q.OutsideCycleClippingExcluded; }

        internal static bool WindowQualityPassed(LocationRangeResult q)
        { return q.SwitchingTransientExcluded || q.FactoryCycleWindow ? q.WindowRangeQualityPassed : q.LocationQualityPassed; }
    }
}
