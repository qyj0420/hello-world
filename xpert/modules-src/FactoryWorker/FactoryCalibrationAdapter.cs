using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HVX.SignalProcessing;
using Xiongxin.ZeroBench;
using Xiongxin.FactoryTest.Compatibility100;

namespace Xiongxin.FactoryTest
{
    // Coefficient-only capacitor route from 100 CalibrationStandalone:
    // full segment 10 kHz high-pass -> periodic incident windows -> shape
    // rejection -> signed, aligned average -> Q / baseline-corrected peak.
    // No cable geometry, reflection search, or distance requirement is used.
    public static class FactoryCalibrationAdapter
    {
        public const string Method = "100 电容时段校准：10 kHz 对称高通、周期开窗、形态剔除、全段有效脉冲对齐平均入射峰值";
        internal const string LegacyFixedCountMethod = "100 电容校准：10 kHz 对称高通、周期开窗、形态剔除、对齐平均入射峰值";

        public static CalibrationResult Calibrate(BenchCapture capture, double knownPc,
            double frequencyHz, int requiredCount, FactoryTfAnnotationDocument labels = null, FactoryTfResult nativeCandidates = null)
        {
            var result = new CalibrationResult();
            try
            {
                if (capture == null) return Reject(result, "没有完整的本轮校准原始段。");
                capture.ValidateCapture();
                if (!Positive(knownPc) || !Positive(frequencyHz))
                    return Reject(result, "请输入手动校准源的已知正电量和有效频率；时段校准使用本段全部有效脉冲。");
                result.AveragePulseRateHz = capture.PicoRateHz;
                if (capture.Overflow) return Reject(result, "驱动报告校准原始段溢出，不能应用系数；请增大量程复采。");
                double scale = capture.PicoFullScaleV / capture.MaximumAdc;
                bool rawClipped = false;
                for (int i = 0; i < capture.PdAdc.Length; i++)
                {
                    rawClipped |= Math.Abs((int)capture.PdAdc[i]) >= capture.MaximumAdc;
                }
                if (rawClipped) return Reject(result, "校准原始 ADC 发生削顶，滤波不能恢复真实峰值；请增大量程复采。");
                var filtered = FactoryPdSignal.Get(capture);
                double peak = 0;
                foreach (double v in filtered) peak = Math.Max(peak, Math.Abs(v));
                int noiseCount = Math.Min(10001, filtered.Length);
                var background = new double[noiseCount];
                for (int i = 0; i < noiseCount; i++)
                    background[i] = Math.Abs(filtered[(int)((long)i * (filtered.Length - 1) / (noiseCount - 1))]);
                Array.Sort(background);
                double noise = background[noiseCount / 2] / .67448975;
                // These trigger/signal gates are the actual 100 preflight gates.
                if (peak <= Math.Max(noise * 8, scale * 16))
                    return Reject(result, "没有检测到足够高于噪声的校准脉冲；请核对校准器并按原始段推荐量程复采。");
                double threshold = Math.Max(Math.Max(noise * 6, peak * .20), scale * 8);
                var settings = new CalibrationWindowSettings
                {
                    SampleRateHz = capture.PicoRateHz,
                    CapacitiveLoad = true,
                    CableLengthM = 0,
                    VelocityMPerUs = 0,
                    CapacitorEnvelopeUs = 40,
                    FrequencyHz = frequencyHz,
                    ThresholdV = threshold,
                    FullScaleV = capture.PicoFullScaleV,
                    PrePeakSamples = 400,
                    TailSamples = 256,
                    PeakSearchSamples = 128
                };
                settings.Validate();
                var rejectedTemplates = new List<FactoryTfPulse>();
                if (labels != null && nativeCandidates != null && nativeCandidates.ChainKey == FactoryTfAnalysis.ChainKey(capture) &&
                    nativeCandidates.CapturedUtc == capture.CapturedUtc && nativeCandidates.CoefficientPcPerV == capture.Settings.PdCalibrationPcPerV)
                {
                    foreach (var pulse in nativeCandidates.Pulses.Where(p => p.Valid))
                    {
                        var point = new FactoryTfDisplayPoint { Round = nativeCandidates, Pulse = pulse };
                        var match = FactoryPulseLabels.Match(labels, point, FactoryPulseLabels.Create(point, pulse.SamplesV));
                        if (match.Matched && !match.Conflict && match.Filter) rejectedTemplates.Add(pulse);
                    }
                }
                var extracted = CalibrationWindowing.FromFilteredSegment(filtered, settings,
                    PdLinearHighPass.GuardSamples(capture.PicoRateHz), rejectedTemplates.Count == 0 ? (Func<int,bool>)null : peakSample => rejectedTemplates.Any(p => peakSample >= p.StartSample && peakSample <= p.EndSample));
                if (rejectedTemplates.Count > 0) extracted.Message += "；已授权干扰模板（四项≤1%）剔除 " + rejectedTemplates.Count + " 个候选；原始ADC保留；周期辨识/形态/SNR质量门限不放宽。";
                if (extracted.PeakSampleIndices != null && extracted.PeakSampleIndices.Length >= 2)
                {
                    int first = extracted.PeakSampleIndices[0], last = extracted.PeakSampleIndices[extracted.PeakSampleIndices.Length - 1];
                    double spanCycles = Math.Round((last - first) / (capture.PicoRateHz / frequencyHz));
                    if (last > first && spanCycles > 0)
                        result.PeriodHz = spanCycles * capture.PicoRateHz / (last - first);
                }
                result.PulseCount = extracted.CandidateCount;
                result.CompleteWindowCount = extracted.Windows.Length;
                // Keep the argument for source/history compatibility only. It
                // is no longer a target, a retry trigger or a truncation count.
                if(extracted.Windows.Length==0)return Reject(result,"本段没有完整有效校准脉冲；原始段和质量标记保留，不补采。");
                extracted = RejectOutliers(extracted);
                extracted.Message += "；时段模式使用本段全部通过质量检查的脉冲，不按目标次数拒绝、截断或补采。";
                // Same 200-point baseline and 336..464 incident search used by
                // 100. Its fixed old cable-circuit amplitude reference is omitted
                // only for this coefficient-only factory capacitor context.
                var assessment = CalibrationIncidentQuality.Analyze(extracted.Windows, 200, 336,
                    Math.Min(settings.WindowSamples, 464), knownPc, capture.PicoFullScaleV,
                    false, capture.Settings.RangeIndex + "/" + PdLinearHighPass.FilterId, null, false);
                result.AveragePulseV = extracted.Average;
                result.MeanPeakV = assessment.IncidentPeakV;
                result.NoiseRmsV = assessment.NoiseRmsV;
                result.BaselineV = assessment.BaselineV;
                result.AmplitudeCv = assessment.PeakVariationFraction;
                result.PeakSnr = Positive(assessment.NoiseRmsV)
                    ? assessment.IncidentPeakV / assessment.NoiseRmsV : Double.NaN;
                result.CompleteWindowCount = assessment.PulseCount;
                result.CoefficientPcPerV = assessment.CoefficientPcPerV;
                result.Valid = assessment.SaveRecommended;
                result.Detail = Method + "。\r\n" + extracted.Message + "\r\n质量：" + assessment.Quality + "。" +
                    assessment.Message + "\r\n系数 " + Format(result.CoefficientPcPerV) + " pC/V = 已知 " +
                    Format(knownPc) + " pC / 扣基线平均入射峰值 " + Format(result.MeanPeakV) +
                    " V；SNR " + Format(assessment.SnrDb) + " dB，峰值 CV " + Format(result.AmplitudeCv * 100) +
                    "%；原始样点实测周期频率 " + Format(result.PeriodHz) + " Hz。\r\n电容回路：不使用电缆长度、波速、反射或定位验证；未应用旧电缆回路 100 pC → 0.150 V 参考。";
                return result;
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is InvalidOperationException) &&
                    !(error is System.IO.InvalidDataException) && !(error is OverflowException)) throw;
                return Reject(result, "100 校准流程未通过：" + error.Message);
            }
        }

        // Ported without threshold changes from 100 CalibrationStandalone /
        // AcquisitionPlanning.RejectOutliers; no scope driver is referenced.
        private static CalibrationWindowResult RejectOutliers(CalibrationWindowResult input)
        {
            var all = input.Windows;
            if (all.Length < 3) throw new InvalidOperationException("周期/形态质量核验至少需要 3 个完整窗口（与旧目标次数无关）；本段无法完成独立质量判断，不补采。");
            int length = all[0].Length;
            var median = new double[length];
            var column = new double[all.Length];
            for (int j = 0; j < length; j++)
            {
                for (int i = 0; i < all.Length; i++) column[i] = all[i][j];
                Array.Sort(column); median[j] = column[column.Length / 2];
            }
            double medPeak = 0;
            foreach (double v in median) medPeak = Math.Max(medPeak, Math.Abs(v));
            var accepted = new List<double[]>();
            foreach (var wave in all)
            {
                double xy = 0, xx = 0, yy = 0, peak = 0;
                for (int j = 0; j < length; j++)
                {
                    xy += wave[j] * median[j]; xx += wave[j] * wave[j]; yy += median[j] * median[j];
                    peak = Math.Max(peak, Math.Abs(wave[j]));
                }
                double correlation = xx > 0 && yy > 0 ? xy / Math.Sqrt(xx * yy) : 0;
                if (correlation >= .90 && peak >= medPeak * .5 && peak <= medPeak * 1.5) accepted.Add(wave);
            }
            if (accepted.Count < 3 || accepted.Count < all.Length * .5)
                throw new InvalidOperationException("脉冲形态不一致或异常比例过高；原始记录已保留，请检查源与干扰。");
            var average = new double[length];
            foreach (var wave in accepted)
                for (int j = 0; j < length; j++) average[j] += wave[j] / accepted.Count;
            return new CalibrationWindowResult { Windows = accepted.ToArray(), Average = average,
                CandidateCount = input.CandidateCount, RejectedCount = input.RejectedCount + all.Length - accepted.Count,
                Message = input.Message + "；形态/幅值异常另剔除 " + (all.Length - accepted.Count) + "，最终平均 " + accepted.Count + " 次。" };
        }
        private static CalibrationResult Reject(CalibrationResult r, string message)
        { r.Valid = false; r.Detail = Method + "。\r\n" + message; return r; }
        private static bool Positive(double v) { return v > 0 && !Double.IsNaN(v) && !Double.IsInfinity(v); }
        private static string Format(double v) { return Double.IsNaN(v) || Double.IsInfinity(v) ? "未确定" : v.ToString("G6", CultureInfo.InvariantCulture); }
    }
}
