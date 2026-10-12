using System;
using System.Collections.Generic;

namespace HVX.OWTS.Task38
{
    public sealed class CalibrationLevelReference
    {
        public string ConfigurationKey { get; set; }
        public double ChargePc { get; set; }
        public double IncidentPeakV { get; set; }
    }

    public sealed class CalibrationIncidentAssessment
    {
        public double IncidentPeakV { get; internal set; }
        public int IncidentPeakIndex { get; internal set; }
        public double BaselineV { get; internal set; }
        public double NoiseRmsV { get; internal set; }
        public double SnrDb { get; internal set; }
        public double PeakVariationFraction { get; internal set; }
        public int PulseCount { get; internal set; }
        public double CoefficientPcPerV { get; internal set; }
        public double ExpectedPeakV { get; internal set; }
        public double ReferenceDeviationFactor { get; internal set; }
        public double CrossLevelDeviationFactor { get; internal set; }
        public double ComparedChargePc { get; internal set; }
        public bool CircuitReferenceChecked { get; internal set; }
        public bool CircuitSuspected { get; internal set; }
        public bool SaveRecommended { get; internal set; }
        public string Quality { get; internal set; }
        public string Message { get; internal set; }
    }

    public static class CalibrationIncidentQuality
    {
        // User-specified circuit reference, not a universal calibration standard.
        public const double ReferenceChargePc = 100;
        public const double ReferencePeakV = .150;
        public const double MaximumDeviationFactor = 3;

        // Windows must already be identified/aligned incident pulses. This routine never
        // chooses the largest pulse in a whole recording or claims to identify interference.
        public static CalibrationIncidentAssessment Analyze(double[][] windows, int baselineEnd,
            int incidentStart, int incidentEnd, double chargePc, double fullScaleV, bool hardwareClipped,
            string configurationKey, IEnumerable<CalibrationLevelReference> previousLevels)
        {
            // 2026-10-06: operator deferred circuit-reference experiments. Keep
            // acquisition quality gates, but do not invent a universal transfer gain.
            return AnalyzeCore(windows, baselineEnd, incidentStart, incidentEnd, chargePc, fullScaleV,
                hardwareClipped, configurationKey, previousLevels, false);
        }

        // Explicit research entry only; normal acquisition/saving does not call this.
        public static CalibrationIncidentAssessment AnalyzeWithCircuitReference(double[][] windows, int baselineEnd,
            int incidentStart, int incidentEnd, double chargePc, double fullScaleV, bool hardwareClipped,
            string configurationKey, IEnumerable<CalibrationLevelReference> previousLevels)
        {
            return AnalyzeCore(windows, baselineEnd, incidentStart, incidentEnd, chargePc, fullScaleV,
                hardwareClipped, configurationKey, previousLevels, true);
        }

        private static CalibrationIncidentAssessment AnalyzeCore(double[][] windows, int baselineEnd,
            int incidentStart, int incidentEnd, double chargePc, double fullScaleV, bool hardwareClipped,
            string configurationKey, IEnumerable<CalibrationLevelReference> previousLevels, bool checkReference)
        {
            var r = new CalibrationIncidentAssessment { Quality = "不可用", Message = "", CoefficientPcPerV = Double.NaN,
                SnrDb = Double.NaN, ReferenceDeviationFactor = Double.NaN, CrossLevelDeviationFactor = Double.NaN,
                ExpectedPeakV = Double.NaN, PeakVariationFraction = Double.NaN, IncidentPeakIndex = -1 };
            if (!Positive(chargePc) || !Positive(fullScaleV) || windows == null || windows.Length == 0 ||
                baselineEnd < 8 || incidentStart < baselineEnd || incidentEnd <= incidentStart)
            { r.Message = "放电量、量程或入射窗口无效。"; return r; }
            if (checkReference) {
                r.ExpectedPeakV = chargePc / ReferenceChargePc * ReferencePeakV;
                if (!Positive(r.ExpectedPeakV)) { r.Message = "参考峰值超出可计算范围。"; return r; }
            }
            r.PulseCount = windows.Length;
            double[] average = new double[incidentEnd - incidentStart];
            double[] peaks = new double[windows.Length];
            double sumBaseline = 0, sumNoiseVariance = 0;
            bool clipped = hardwareClipped, boundary = false;
            for (int p = 0; p < windows.Length; p++)
            {
                double[] w = windows[p];
                if (w == null || w.Length < incidentEnd) { r.Message = "入射波窗口不完整。"; return r; }
                for (int i = 0; i < w.Length; i++)
                {
                    if (!Finite(w[i])) { r.Message = "波形含非有限数据。"; return r; }
                    if (Math.Abs(w[i]) >= fullScaleV * .999) clipped = true;
                }
                double baseline = 0;
                for (int i = 0; i < baselineEnd; i++) baseline += w[i];
                baseline /= baselineEnd; sumBaseline += baseline;
                double noise = 0;
                for (int i = 0; i < baselineEnd; i++) noise += (w[i] - baseline) * (w[i] - baseline);
                sumNoiseVariance += noise / baselineEnd;
                int peakIndex = incidentStart;
                for (int i = incidentStart; i < incidentEnd; i++)
                {
                    double v = w[i] - baseline;
                    average[i - incidentStart] += v / windows.Length;
                    if (Math.Abs(v) > peaks[p]) { peaks[p] = Math.Abs(v); peakIndex = i; }
                }
                boundary |= peakIndex == incidentStart || peakIndex == incidentEnd - 1;
            }
            r.BaselineV = sumBaseline / windows.Length;
            r.NoiseRmsV = Math.Sqrt(sumNoiseVariance / windows.Length);
            for (int i = 0; i < average.Length; i++)
                if (Math.Abs(average[i]) > r.IncidentPeakV) { r.IncidentPeakV = Math.Abs(average[i]); r.IncidentPeakIndex = i + incidentStart; }
            if (!Positive(r.IncidentPeakV)) { r.Message = "未检测到有效入射波峰值。"; return r; }
            r.CoefficientPcPerV = chargePc / r.IncidentPeakV;
            if (!Positive(r.CoefficientPcPerV)) { r.Message = "校准系数超出可计算范围。"; return r; }
            // No ADC resolution/quantisation floor is supplied here. A flat
            // baseline cannot establish an infinite SNR or a fabricated lower
            // bound. Difference of logs also avoids intermediate ratio overflow.
            r.SnrDb = Positive(r.NoiseRmsV) ? 20 * (Math.Log10(r.IncidentPeakV) - Math.Log10(r.NoiseRmsV)) : Double.NaN;
            double mean = 0; foreach (double peak in peaks) mean += peak / peaks.Length;
            double variance = 0; foreach (double peak in peaks) variance += (peak - mean) * (peak - mean) / peaks.Length;
            r.PeakVariationFraction = mean > 0 ? Math.Sqrt(variance) / mean : Double.PositiveInfinity;
            // Engineering quality gates are explicit, not calibrated probabilities or standards.
            if (clipped) { r.Message = "入射波或原始采集已削顶，增大量程后重新校准；暂不判断回路幅值偏差。"; return r; }
            if (boundary) { r.Message = "峰值贴近入射窗口边缘，请重新识别完整入射波。"; return r; }
            if (windows.Length < 2) { r.Message = "仅有 1 个有效入射脉冲，已计算系数，但不能判断脉冲重复性，请核对结果。"; return r; }
            if (!Finite(r.SnrDb)) {
                r.Quality = "信噪比未能估计 / 待复测";
                r.Message = "基线噪声未分辨且缺少有效测量分辨率，信噪比未能估计；保留原始波形，请复测，不自动保存正式系数。";
                return r;
            }
            if (r.SnrDb < 20) { r.Message = "信噪比低于 20 dB，入射波质量不足，请排查干扰。"; return r; }
            if (r.PeakVariationFraction > .20) { r.Message = "入射峰值变异系数超过 20%，脉冲不稳定，请重测。"; return r; }
            if (checkReference) {
            r.CircuitReferenceChecked = true;
            if (r.ExpectedPeakV >= fullScaleV * .999) {
                r.Quality = "回路参考适用性待核对";
                r.Message = "指定回路参考外推的预期峰值超出当前可验证量程，不能据此诊断回路异常；请核对该档位的参考适用范围和输入设置。原始波形保留，暂不保存正式系数。";
                return r;
            }
            r.ReferenceDeviationFactor = Factor(r.IncidentPeakV, r.ExpectedPeakV);
            if (previousLevels != null && !String.IsNullOrEmpty(configurationKey))
            {
                foreach (CalibrationLevelReference previous in previousLevels)
                {
                    if (previous == null || previous.ConfigurationKey != configurationKey || !Positive(previous.ChargePc) ||
                        !Positive(previous.IncidentPeakV) || previous.ChargePc == chargePc) continue;
                    double factor = Factor(r.IncidentPeakV / chargePc, previous.IncidentPeakV / previous.ChargePc);
                    if (Double.IsNaN(r.CrossLevelDeviationFactor) || factor > r.CrossLevelDeviationFactor)
                    { r.CrossLevelDeviationFactor = factor; r.ComparedChargePc = previous.ChargePc; }
                }
            }
            if (r.ReferenceDeviationFactor >= MaximumDeviationFactor * (1 - 1e-12) || r.CrossLevelDeviationFactor >= MaximumDeviationFactor * (1 - 1e-12))
            {
                r.CircuitSuspected = true; r.Quality = "疑似回路异常";
                r.Message = "幅值相对参考或跨档归一化偏差达到 3 倍，禁止覆盖原系数；检查校准源电量、接线、阻抗、带宽和输入设置。";
                return r;
            }
            }
            r.SaveRecommended = true;
            r.Quality = windows.Length >= 8 && r.SnrDb >= 30 && r.PeakVariationFraction <= .10 ? "良好" : "可用 建议复测";
            r.Message = "系数按放电量 / 扣基线入射峰值计算；采集质量检查通过，应用仍需确认。" +
                (checkReference ? "" : "回路参考比对未启用。");
            return r;
        }
        private static double Factor(double a, double b) { return Math.Max(a / b, b / a); }
        private static bool Positive(double v) { return Finite(v) && v > 0; }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
    }
}
