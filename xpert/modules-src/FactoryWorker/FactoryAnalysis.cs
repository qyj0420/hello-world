using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    public sealed class CalibrationResult
    {
        public bool Valid;
        public double CoefficientPcPerV = Double.NaN;
        public double MeanPeakV = Double.NaN;
        public double NoiseRmsV = Double.NaN;
        public double AmplitudeCv = Double.NaN;
        public double PeriodHz = Double.NaN;
        public int PulseCount;
        public int CompleteWindowCount;
        public string Detail = "";
        public double[] AveragePulseV = new double[0];
        public double AveragePulseRateHz;
        public double BaselineV = Double.NaN;
        public double PeakSnr = Double.NaN;
    }

    public sealed class FactoryMetrics
    {
        public double OscillationPeakKv = Double.NaN;
        public double OscillationPeakAfterTransientKv = Double.NaN;
        public double RawDividerPeakV = Double.NaN;
        public double MaxPdPc = Double.NaN;
        public double FullSegmentEquivalentPeakPc = Double.NaN;
        public double FrequencyHz = Double.NaN;
        public double CapacitanceNf = Double.NaN;
        public double EstimatedLossTangent = Double.NaN;
        public double DampingAlphaPerSecond = Double.NaN;
        public double MeasuredPressureVPerV = Double.NaN;
        public double MeasuredDividerVPerV = Double.NaN;
        public bool FrequencyValid, PressureRatioValid, DividerRatioValid, PdClipped;
        public string Detail = "";
        public DetectedWaveformResult Detected;
        public int RecommendedRangeIndex;
        public string RangeDetail = "";
        public FactoryReferenceResult ReferenceAnalysis;
        public FactoryRangePhaseResult RangePhase;
        public LocationRangeResult RangeQuality;
    }

    /// <summary>Offline diagnostics and candidate calibration, not an automatic metrology certificate.</summary>
    public static class FactoryAnalysis
    {
        private sealed class AdcStatistics
        {
            internal double BaselineCodes, NoiseCodes, Scale;
            internal int MaximumAbsoluteCode;
            internal bool Clipped;
        }
        private sealed class Peak
        {
            internal int Index;
            internal double Amplitude;
        }

        public static CalibrationResult Calibrate(BenchCapture capture, double knownPc, double freqHz, int requiredCount)
        {
            var result = new CalibrationResult();
            try
            {
                if (!Positive(knownPc) || !Positive(freqHz) || freqHz < 1 || freqHz > 10000 ||
                    requiredCount < 3 || requiredCount > 100)
                    return Reject(result, "校准源参数无效：需独立已知电量、1～10000 Hz 频率及至少 3 次脉冲。");
                if (capture == null) return Reject(result, "没有本轮原始采集数据。");
                capture.ValidateCapture();
                AdcStatistics stats = Statistics(capture);
                result.BaselineV = stats.BaselineCodes * stats.Scale;
                result.NoiseRmsV = stats.NoiseCodes * stats.Scale;
                result.AveragePulseRateHz = capture.PicoRateHz;
                if (stats.Clipped) return Reject(result, "原始 ADC 削顶或硬件溢出，真实脉冲峰值未知，不能计算校准系数。");

                double periodSamples = capture.PicoRateHz / freqHz;
                if (periodSamples < 12) return Reject(result, "每周期实际采样点不足，不能区分脉冲与振铃。");
                double detectionCodes = Math.Max(8, stats.NoiseCodes * 6);
                int groupWidth = Math.Max(1, (int)Math.Floor(periodSamples * .4));
                int isolation = Math.Max(1, (int)Math.Floor(periodSamples * .6));
                int pre = Math.Max(2, (int)Math.Ceiling(Math.Min(200e-6, .1 / freqHz) * capture.PicoRateHz));
                int post = Math.Max(4, (int)Math.Ceiling(Math.Min(1000e-6, .25 / freqHz) * capture.PicoRateHz));
                if ((long)pre + post + 1 > 1000000)
                    return Reject(result, "脉冲平均窗口过大，需调整采样率或校准源频率。");
                var peaks = new List<Peak>();
                int partial = 0;
                for (int i = 0; i < capture.PdAdc.Length; )
                {
                    double amplitude = Math.Abs(capture.PdAdc[i] - stats.BaselineCodes);
                    if (amplitude < detectionCodes) { i++; continue; }
                    int end = Math.Min(capture.PdAdc.Length, i + groupWidth);
                    int index = i;
                    for (int j = i + 1; j < end; j++)
                    {
                        double current = Math.Abs(capture.PdAdc[j] - stats.BaselineCodes);
                        if (current > amplitude) { amplitude = current; index = j; }
                    }
                    i = Math.Max(end, index + isolation);
                    if (index - pre < 0 || index + post >= capture.PdAdc.Length) { partial++; continue; }
                    if (peaks.Count >= 5000) return Reject(result, "候选脉冲过多，不能将非周期信号或噪声当作校准源。");
                    peaks.Add(new Peak { Index = index, Amplitude = amplitude * stats.Scale });
                }
                result.PulseCount = peaks.Count;
                result.CompleteWindowCount = peaks.Count;
                if (peaks.Count < requiredCount)
                    return Reject(result, "完整周期脉冲不足：检测 " + peaks.Count + " 次，要求 " + requiredCount +
                        " 次；边界不完整脉冲 " + partial + " 次。不能用平直噪声代替校准脉冲。");

                // Validate ALL observed complete pulses. Do not select a favourable N
                // while silently ignoring missing periods, erratic amplitudes or noise.
                double mean = 0, squares = 0, minimum = Double.PositiveInfinity, maximum = 0;
                var periods = new List<double>();
                foreach (Peak peak in peaks)
                {
                    mean += peak.Amplitude; squares += peak.Amplitude * peak.Amplitude;
                    minimum = Math.Min(minimum, peak.Amplitude); maximum = Math.Max(maximum, peak.Amplitude);
                }
                mean /= peaks.Count;
                result.MeanPeakV = mean;
                result.AmplitudeCv = Math.Sqrt(Math.Max(0, squares / peaks.Count - mean * mean)) / mean;
                result.PeakSnr = minimum / result.NoiseRmsV;
                if (!Finite(result.PeakSnr) || result.PeakSnr < 10)
                    return Reject(result, "脉冲信噪比不足 10，不能给出可信候选校准系数。");
                if (result.AmplitudeCv > .10 || (maximum - minimum) / mean > .25)
                    return Reject(result, "脉冲幅值不一致：CV 或幅值变化超过允许范围，检查校准源与接线。");
                for (int i = 1; i < peaks.Count; i++)
                    periods.Add((peaks[i].Index - peaks[i - 1].Index) / capture.PicoRateHz);
                double medianPeriod = Median(periods.ToArray());
                result.PeriodHz = 1 / medianPeriod;
                if (Math.Abs(result.PeriodHz / freqHz - 1) > .05)
                    return Reject(result, "实测脉冲频率与设置相差超过 5%，不能确认校准源周期。");
                foreach (double period in periods)
                    if (Math.Abs(period / medianPeriod - 1) > .05)
                        return Reject(result, "脉冲间隔不一致或存在漏脉冲，不能将振铃重复计作校准脉冲。");

                double quietCodes = Math.Max(3, 4 * stats.NoiseCodes);
                int tailCount = Math.Max(2, post / 5), headCount = Math.Max(2, pre / 5);
                foreach (Peak peak in peaks)
                {
                    int noisyTail = 0, noisyHead = 0;
                    for (int j = peak.Index - pre; j < peak.Index - pre + headCount; j++)
                        if (Math.Abs(capture.PdAdc[j] - stats.BaselineCodes) > quietCodes) noisyHead++;
                    for (int j = peak.Index + post - tailCount + 1; j <= peak.Index + post; j++)
                        if (Math.Abs(capture.PdAdc[j] - stats.BaselineCodes) > quietCodes) noisyTail++;
                    if (noisyTail > tailCount / 3 || noisyHead > headCount / 3)
                        return Reject(result, "脉冲窗口首端或末端仍有显著信号，窗口不完整或振铃过长，不能形成完整平均脉冲。");
                }

                // Mean of the requested N physical pulse peaks is independent of the
                // previous coefficient. Charge is supplied by the external calibrator.
                double selectedMean = 0;
                result.AveragePulseV = new double[pre + post + 1];
                for (int pulse = 0; pulse < requiredCount; pulse++)
                {
                    Peak peak = peaks[pulse]; selectedMean += peak.Amplitude;
                    // Keep polarity consistent for the displayed aligned pulse, but
                    // never alter or replace the native ADC in the saved record.
                    double polarity = capture.PdAdc[peak.Index] >= stats.BaselineCodes ? 1 : -1;
                    for (int j = 0; j < result.AveragePulseV.Length; j++)
                        result.AveragePulseV[j] += polarity * (capture.PdAdc[peak.Index - pre + j] - stats.BaselineCodes) * stats.Scale / requiredCount;
                }
                result.MeanPeakV = selectedMean / requiredCount;
                result.CoefficientPcPerV = knownPc / result.MeanPeakV;
                if (!Positive(result.CoefficientPcPerV)) return Reject(result, "候选校准系数非有限或非正数。");
                result.Valid = true;
                result.Detail = "候选系数 " + F(result.CoefficientPcPerV) + " pC/V = 独立已知电量 " + F(knownPc) +
                    " pC / " + requiredCount + " 次完整脉冲平均峰值 " + F(result.MeanPeakV) +
                    " V；SNR " + F(result.PeakSnr) + "，幅值 CV " + F(result.AmplitudeCv) +
                    "，周期 " + F(result.PeriodHz) + " Hz。噪声 RMS 为 MAD 换算的鲁棒等效估计。仅数据质量检查，不是自动校准认证。";
                return result;
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is InvalidDataException) && !(error is OverflowException)) throw;
                return Reject(result, "校准数据未通过验证：" + error.Message);
            }
        }

        public static FactoryMetrics Analyze(FactoryResult result)
        {
            if (result == null || result.Settings == null || result.Capture == null)
                throw new ArgumentException("缺少本轮设置或实际采集记录。");
            BenchCapture capture = result.Capture;
            capture.ValidateCapture();
            var metrics = new FactoryMetrics { RecommendedRangeIndex = capture.Settings.RangeIndex };
            var details = new List<string>();
            AdcStatistics adc = Statistics(capture);
            double filteredFullPeakPc, filteredPostTransientPeakPc;
            metrics.Detected = FactoryDetectedWaveforms.Build(capture, out filteredFullPeakPc, out filteredPostTransientPeakPc);
            metrics.PdClipped = adc.Clipped;
            metrics.FullSegmentEquivalentPeakPc = filteredFullPeakPc;
            if (!metrics.PdClipped)
                metrics.MaxPdPc = filteredPostTransientPeakPc;
            else details.Add("局放 ADC 削顶或溢出，不能据饱和电平码推断真实最大放电量。");
            RecommendRange(capture, adc, metrics);

            int voltageExclusion = (int)Math.Min(capture.VoltageRaw.Length,
                Math.Ceiling((capture.Settings.PreTriggerDurationMs+capture.Settings.TransientExclusionMs) * capture.ArtRateHz / 1000));
            double afterPeak = 0, rawPeak = 0;
            for (int i = 0; i < capture.VoltageRaw.Length; i++)
            {
                rawPeak = Math.Max(rawPeak, Math.Abs(capture.VoltageRaw[i]));
                if (i >= voltageExclusion) afterPeak = Math.Max(afterPeak, Math.Abs(capture.VoltageRaw[i]));
            }
            metrics.RawDividerPeakV = rawPeak;
            metrics.OscillationPeakKv = rawPeak * capture.Settings.DividerVPerV / 1000;
            metrics.OscillationPeakAfterTransientKv = afterPeak * capture.Settings.DividerVPerV / 1000;
            details.Add("分压器完整段峰值保留起始过冲；另列排除前 " + F(capture.Settings.TransientExclusionMs) + " ms 的振荡峰值。");
            AnalyzeOscillation(capture, voltageExclusion, metrics, details);
            metrics.RangePhase = FactoryRangePhase.Analyze(capture);
            details.Add(metrics.RangePhase.Detail);
            // Phase and measured attenuation depend on the verified waveform,
            // not on whether a standard capacitor is configured or agrees.
            var postTriggerVoltage=new double[capture.VoltageRaw.Length-capture.Settings.ArtPreTriggerCount];
            Array.Copy(capture.VoltageRaw,capture.Settings.ArtPreTriggerCount,postTriggerVoltage,0,postTriggerVoltage.Length);
            metrics.ReferenceAnalysis = FactoryReferenceAnalysis.Analyze(postTriggerVoltage, capture.ArtRateHz,
                capture.Settings.DividerVPerV, capture.Settings.TransientExclusionMs,
                metrics.FrequencyHz, metrics.DampingAlphaPerSecond, metrics.FrequencyValid,
                result.Settings.ReferenceCapacitanceNf, result.Settings.ReferenceLossTangent,
                capture.Settings.ResistanceOhms, result.Settings.DecayCycleCount, result.Settings.ReferenceCapacitorEnabled);
            if(metrics.ReferenceAnalysis.PhaseValid)
            {
                metrics.ReferenceAnalysis.FirstZeroMs+=capture.Settings.PreTriggerDurationMs;
                for(int i=0;i<metrics.ReferenceAnalysis.CyclePeakMs.Length;i++)metrics.ReferenceAnalysis.CyclePeakMs[i]+=capture.Settings.PreTriggerDurationMs;
            }
            details.Add(metrics.ReferenceAnalysis.Detail);
            VerifyRatios(result, metrics, details);
            details.Add("局放与校准使用同一 10 kHz 对称高通；全段滤波后按 80 kS/s 每窗保留原始采样点绝对峰值。原始 ADC、削顶标志和量程判断保持不变；最大值为按该滤波条件校准的输入等效量。");
            metrics.Detail = String.Join("\r\n", details.ToArray());
            return metrics;
        }

        private static void RecommendRange(BenchCapture capture, AdcStatistics adc, FactoryMetrics metrics)
        {
            int current = capture.Settings.RangeIndex;
            if (adc.Clipped)
            {
                metrics.RecommendedRangeIndex = Math.Min(current + 1, BenchSettings.Ranges.Length - 1);
                metrics.RangeDetail = current == BenchSettings.Ranges.Length - 1
                    ? "最高量程仍削顶/溢出，真实峰值未知。信号过大，继续试验可能使设备老化或损坏；应停止并核查接线与设备。"
                    : "完整原始段 ADC 削顶/溢出：真实峰值未知，暂建议提高一档，不能保证下一档一定合格；不自动重新加压。";
                return;
            }
            // Range is an absolute terminal-voltage limit, so DC offset and all
            // acquired overshoots count. A corrected PD baseline must not hide them.
            double rawPeak = adc.MaximumAbsoluteCode * adc.Scale;
            double required = rawPeak / .70;
            int recommended = BenchSettings.Ranges.Length - 1;
            for (int i = 0; i < BenchSettings.Ranges.Length; i++)
                if (BenchSettings.Ranges[i] >= required) { recommended = i; break; }
            metrics.RecommendedRangeIndex = recommended;
            metrics.RangeDetail = "全段原始输入峰值 " + F(rawPeak) + " V；按峰值不超过量程 70% 预留余量，建议 ±" +
                F(BenchSettings.Ranges[recommended]) + " V（当前 ±" + F(capture.PicoFullScaleV) +
                " V）。仅建议下一轮设置，不自动加压；最低档仍保留可辨噪声。";
        }

        private static void VerifyRatios(FactoryResult result, FactoryMetrics metrics, List<string> details)
        {
            FactorySettings settings = result.Settings;
            if (Positive(settings.ReferenceDcKv) && result.Ramp != null && result.Ramp.Count >= 3)
            {
                int count = Math.Min(5, result.Ramp.Count), begin = result.Ramp.Count - count;
                double mean = 0, minimum = Double.PositiveInfinity, maximum = Double.NegativeInfinity;
                bool finite = true;
                for (int i = begin; i < result.Ramp.Count; i++)
                {
                    double ao = result.Ramp[i].AoV;
                    finite &= Positive(ao); mean += ao; minimum = Math.Min(minimum, ao); maximum = Math.Max(maximum, ao);
                }
                mean /= count;
                if (finite && maximum - minimum <= Math.Max(.002, mean * .01))
                {
                    metrics.MeasuredPressureVPerV = settings.ReferenceDcKv * 1000 / mean;
                    metrics.PressureRatioValid = Positive(settings.PressureVPerV) &&
                        Math.Abs(metrics.MeasuredPressureVPerV / settings.PressureVPerV - 1) <= settings.RatioTolerancePercent / 100;
                    details.Add("加压比独立参考 / 稳定末端 AO = " + F(metrics.MeasuredPressureVPerV) +
                        " V/V，" + (metrics.PressureRatioValid ? "在设置容差内（候选验证）" : "不在设置容差内，未通过") + "。");
                }
                else details.Add("加压比未验证：末端 AO 非有限、非正值或不稳定，不能用命令斜坡推算标定比例。");
            }
            else details.Add("加压比未验证：缺少独立直流参考值及至少三次稳定 AO 记录。");

            if (Finite(metrics.RawDividerPeakV) && metrics.RawDividerPeakV >= 9.95)
                details.Add("分压比未验证：完整原始 AI4 段达到 ±10 V 饱和边界（绝对峰值≥9.95 V），包含起始过冲；真实峰值未知。排除过冲后的频率拟合或独立参考输入不能恢复削顶峰值。");
            else if (Positive(settings.ReferenceOscillationPeakKv) && Positive(metrics.RawDividerPeakV) && metrics.FrequencyValid)
            {
                metrics.MeasuredDividerVPerV = settings.ReferenceOscillationPeakKv * 1000 / metrics.RawDividerPeakV;
                metrics.DividerRatioValid = Positive(result.Capture.Settings.DividerVPerV) &&
                    Math.Abs(metrics.MeasuredDividerVPerV / result.Capture.Settings.DividerVPerV - 1) <= settings.RatioTolerancePercent / 100;
                details.Add("分压比独立完整段峰值参考 / 原始 AI4 绝对峰值（含过冲） = " +
                    F(metrics.MeasuredDividerVPerV) + " V/V，" +
                    (metrics.DividerRatioValid ? "在设置容差内（候选验证）" : "不在设置容差内，未通过") + "。参考读数必须覆盖同一峰值口径。");
            }
            else details.Add("分压比未验证：缺少独立峰值参考或没有可信振荡信号；不能用已配置分压比反算参考再证明自身。");
        }

        private static void AnalyzeOscillation(BenchCapture capture, int begin, FactoryMetrics metrics, List<string> details)
        {
            double[] raw = capture.VoltageRaw;
            // Always evaluate captured oscillation evidence. The historical
            // feature checkbox is neither signal evidence nor an HV permit.
            if (begin >= raw.Length - 20)
            { details.Add("频率、电容、介损无效：有效振荡记录过短。"); return; }
            int count = raw.Length - begin;
            var values = new double[count];
            Array.Copy(raw, begin, values, 0, count);
            double baseline = Median((double[])values.Clone());
            double peak = 0, rms = 0;
            for (int i = 0; i < count; i++) { values[i] -= baseline; peak = Math.Max(peak, Math.Abs(values[i])); rms += values[i] * values[i]; }
            rms = Math.Sqrt(rms / count);
            var curvature = new double[Math.Max(1, count - 2)];
            for (int i = 1; i < count - 1; i++) curvature[i - 1] = Math.Abs(values[i + 1] - 2 * values[i] + values[i - 1]);
            double noise = Median(curvature) / (.67448975 * Math.Sqrt(6));
            if (!(peak > Math.Max(1e-6, noise * 10)) || !(rms > 0))
            { details.Add("频率、电容、介损无效：无可信振荡或电压信噪比不足，未把背景噪声强行拟合为高压振荡。"); return; }

            double threshold = Math.Max(peak * .05, noise * 3);
            var crossings = new List<double>();
            double candidate = Double.NaN;
            int state = values[0] > threshold ? 1 : values[0] < -threshold ? -1 : 0;
            for (int i = 1; i < count; i++)
            {
                double left = values[i - 1], right = values[i];
                if ((left <= 0 && right > 0) || (left >= 0 && right < 0))
                    candidate = (i - 1 + (-left / (right - left))) / capture.ArtRateHz;
                int next = right > threshold ? 1 : right < -threshold ? -1 : state;
                if (next != state && state != 0 && Finite(candidate)) crossings.Add(candidate);
                if (next != 0) state = next;
            }
            if (crossings.Count < 7) { details.Add("频率、电容、介损无效：不足三个完整周期的可靠过零。"); return; }
            var halves = new double[crossings.Count - 1];
            for (int i = 0; i < halves.Length; i++) halves[i] = crossings[i + 1] - crossings[i];
            // Same-polarity crossings cancel the alternating timing bias caused
            // by a finite record's nonzero median. Do not fit a short record with
            // a biased half-cycle frequency and then reject its clean sinusoid.
            var periods = new double[crossings.Count - 2];
            for(int i=0;i<periods.Length;i++) periods[i]=crossings[i+2]-crossings[i];
            double halfPeriod = Median(periods) / 2;
            double frequency = .5 / halfPeriod;
            if (!Positive(frequency) || frequency < 5 || frequency > capture.ArtRateHz / 8)
            { details.Add("频率无效：超出诊断算法可靠频带。"); return; }
            double sumDeviation = 0;
            foreach (double half in halves)
            {
                double deviation = Math.Abs(half / halfPeriod - 1);
                if (deviation > .20) { details.Add("频率无效：过零间隔不一致，可能是脉冲、噪声或多频振荡。"); return; }
                sumDeviation += deviation;
            }
            if (sumDeviation / halves.Length > .05)
            { details.Add("频率无效：过零周期抖动超过 5%。"); return; }

            var times = new List<double>(); var amplitudes = new List<double>();
            for (int i = 0; i + 1 < crossings.Count; i++)
            {
                int first = Math.Max(0, (int)Math.Ceiling(crossings[i] * capture.ArtRateHz));
                int last = Math.Min(count - 1, (int)Math.Floor(crossings[i + 1] * capture.ArtRateHz));
                int maximumIndex = first; double amplitude = 0;
                for (int sample = first; sample <= last; sample++)
                    if (Math.Abs(values[sample]) > amplitude) { amplitude = Math.Abs(values[sample]); maximumIndex = sample; }
                if (amplitude >= peak * .15 && amplitude > noise * 5)
                { times.Add(maximumIndex / capture.ArtRateHz); amplitudes.Add(amplitude); }
            }
            if (times.Count < 5) { details.Add("频率无效：可用振荡峰不足，衰减模型不能确认。"); return; }
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < times.Count; i++)
            { double x = times[i], y = Math.Log(amplitudes[i]); sx += x; sy += y; sxx += x * x; sxy += x * y; }
            double denominator = times.Count * sxx - sx * sx;
            if (!(denominator > 0)) return;
            double alpha = -(times.Count * sxy - sx * sy) / denominator;
            // A growing oscillation is not a passive damped RLC calibration record.
            if (!Finite(alpha) || alpha < -1 || alpha > 10000)
            { details.Add("振荡模型无效：幅值增长或衰减参数不可信。"); return; }
            alpha = Math.Max(0, alpha);
            double omega = 2 * Math.PI * frequency;
            double cc = 0, ss = 0, cs = 0, yc = 0, ys = 0;
            for (int i = 0; i < count; i++)
            {
                double time = i / capture.ArtRateHz, envelope = Math.Exp(-alpha * time);
                double co = envelope * Math.Cos(omega * time), si = envelope * Math.Sin(omega * time);
                cc += co * co; ss += si * si; cs += co * si; yc += values[i] * co; ys += values[i] * si;
            }
            double determinant = cc * ss - cs * cs;
            if (!(determinant > 0)) return;
            double a = (yc * ss - ys * cs) / determinant, b = (ys * cc - yc * cs) / determinant, residual = 0;
            for (int i = 0; i < count; i++)
            {
                double time = i / capture.ArtRateHz;
                double fit = Math.Exp(-alpha * time) * (a * Math.Cos(omega * time) + b * Math.Sin(omega * time));
                residual += (values[i] - fit) * (values[i] - fit);
            }
            if (Math.Sqrt(residual / count) / rms > .20)
            { details.Add("频率、电容、介损无效：单频指数衰减模型拟合残差超过 20%，不自动接受多频或畸变信号。"); return; }
            metrics.FrequencyHz = frequency; metrics.DampingAlphaPerSecond = alpha; metrics.FrequencyValid = true;
            double inductance = capture.Settings.InductanceH, resistance = capture.Settings.ResistanceOhms;
            if (Positive(inductance))
            {
                // Same topology as 100: series coil R+L, capacitor shunted by
                // dielectric conductance. Do not mix the pure-series LC formula.
                double capacitanceDenominator = inductance * (omega * omega + alpha * alpha) -
                    resistance * (2 * alpha - resistance / inductance);
                if (Positive(capacitanceDenominator)) metrics.CapacitanceNf = 1e9 / capacitanceDenominator;
                else details.Add("估算电容无效：扣除线圈电阻后的模型分母非正值。");
                double loss = 2 * alpha / omega - resistance / (omega * inductance);
                if (Finite(loss) && loss >= 0) metrics.EstimatedLossTangent = loss;
                else details.Add("估算介损无效：扣除设置的线圈电阻后为负值，不能用零值掩盖模型或电阻不匹配。");
            }
            details.Add("振荡诊断：f=" + F(frequency) + " Hz，α=" + F(alpha) +
                " 1/s；与 100 一致采用线圈 R+L 和电容并联介质电导模型，C=1/[L(ω²+α²)−R(2α−R/L)]。介损估计为 2α/ω−R/(ωL)，包含其他回路损耗的不确定性，不是标准介损測量或自动合格结论。");
        }

        private static AdcStatistics Statistics(BenchCapture capture)
        {
            var histogram = new int[65536];
            int max = 0; bool clipped = capture.Overflow;
            foreach (short code in capture.PdAdc)
            {
                histogram[code + 32768]++;
                int absolute = Math.Abs((int)code); max = Math.Max(max, absolute);
                if (absolute >= capture.MaximumAdc * .995) clipped = true;
                if (absolute > capture.MaximumAdc) clipped = true;
            }
            double median = WeightedMedian(histogram, capture.PdAdc.Length) - 32768;
            var deviations = new int[131071];
            for (int i = 0; i < histogram.Length; i++)
                if (histogram[i] != 0) deviations[(int)Math.Round(2 * Math.Abs(i - 32768 - median))] += histogram[i];
            double mad = WeightedMedian(deviations, capture.PdAdc.Length) / 2;
            return new AdcStatistics { BaselineCodes = median, NoiseCodes = Math.Max(1 / Math.Sqrt(12), 1.4826022185 * mad),
                Scale = capture.PicoFullScaleV / capture.MaximumAdc, MaximumAbsoluteCode = max, Clipped = clipped };
        }

        private static double WeightedMedian(int[] histogram, int count)
        {
            if (count <= 0) throw new InvalidDataException("ADC 记录为空。");
            int lower = (count - 1) / 2, upper = count / 2, cumulative = 0, lo = -1;
            for (int i = 0; i < histogram.Length; i++)
            {
                cumulative += histogram[i];
                if (lo < 0 && cumulative > lower) lo = i;
                if (cumulative > upper) return .5 * (lo + i);
            }
            throw new InvalidDataException("ADC 统计不完整。");
        }
        private static double Median(double[] values)
        {
            if (values.Length == 0) return Double.NaN;
            Array.Sort(values); int middle = values.Length / 2;
            return (values.Length & 1) == 1 ? values[middle] : .5 * (values[middle - 1] + values[middle]);
        }
        private static CalibrationResult Reject(CalibrationResult result, string detail)
        { result.Valid = false; result.CoefficientPcPerV = Double.NaN; result.Detail = detail; return result; }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        private static bool Positive(double v) { return Finite(v) && v > 0; }
        private static string F(double v) { return v.ToString("G6", CultureInfo.InvariantCulture); }
    }
}
