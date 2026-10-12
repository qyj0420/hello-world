using System;
using System.Collections.Generic;

namespace Xiongxin.ZeroBench
{
    public sealed class VoltageAnalysisResult
    {
        public bool Valid;
        public string Status = "未分析";
        public double FrequencyHz = Double.NaN;
        public double DampingPerSecond = Double.NaN;
        public double CapacitanceF = Double.NaN;
        public double CorrectedTanDeltaEstimate = Double.NaN;
        public double FitRSquared = Double.NaN;
        public double RawPeakVolts;
        public double ScaledPeakVolts;
        public bool Clipped;
    }

    public sealed class PdAnalysisResult
    {
        public bool Valid;
        public bool Clipped;
        public string Status = "未分析";
        public double RawAbsolutePeakVolts;
        public double RawPeakTimeMs;
        public double BaselineVolts;
        public double BaselineCorrectedPeakVolts;
        public double PostExclusionPeakVolts;
        public double PostExclusionPeakTimeMs;
        public double MaximumEquivalentPc = Double.NaN;
        public double ExclusionMs;
        public int PulseCandidateCount;
        public double CandidateThresholdVolts;
        public bool HasPulseCandidate { get { return !Clipped && PulseCandidateCount > 0; } }
    }

    public sealed class RangeState
    {
        internal int PreviousIndex = -1;
        internal int LowCaptures;
        public void Reset() { PreviousIndex = -1; LowCaptures = 0; }
    }

    public sealed class RangeProposal
    {
        public int Index;
        public bool Change;
        public bool AtMaximumAndClipped;
        public string Reason;
    }

    /// <summary>Read-only analysis. Input arrays are never modified. Seconds/Hz/V/F/H/ohm are SI.
    /// Raw ART input volts and calibrated divider voltage must not be confused; division ratio is V/V.
    /// No waveform here establishes absence of hazardous voltage, physical PD, or discharge safety.</summary>
    public static class SignalAnalysis
    {
        public static readonly double[] Ranges = { .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 20 };

        public static VoltageAnalysisResult AnalyzeVoltage(double[] rawVolts, double fsHz,
            double dividerVPerV, double inductanceH, double resistanceOhm, bool circuitConnected,
            double skipMs = .5, double adcFullScaleVolts = 10)
        {
            var r = new VoltageAnalysisResult();
            if (rawVolts == null || rawVolts.Length < 64 || !Positive(fsHz) || !Positive(dividerVPerV) ||
                !Positive(inductanceH) || !Finite(resistanceOhm) || resistanceOhm < 0 ||
                !Positive(adcFullScaleVolts) || !Finite(skipMs) || skipMs < 0)
                return Invalid(r, "参数或数据长度无效");
            for (int i = 0; i < rawVolts.Length; i++)
            {
                if (!Finite(rawVolts[i])) return Invalid(r, "电压数据含非有限值");
                r.RawPeakVolts = Math.Max(r.RawPeakVolts, Math.Abs(rawVolts[i]));
            }
            r.ScaledPeakVolts = r.RawPeakVolts * dividerVPerV;
            r.Clipped = r.RawPeakVolts >= adcFullScaleVolts * .999;
            if (!circuitConnected) return Invalid(r, "控制箱/背景接线：频率、电容、介损无效");
            if (r.Clipped) return Invalid(r, "电压触及量程边界，参数无效");
            int begin = (int)Math.Min(rawVolts.Length, Math.Ceiling(skipMs * .001 * fsHz));
            int n = rawVolts.Length - begin;
            if (n < 64) return Invalid(r, "排除过冲后记录不足");
            // Keep no more than 12k points for fitting; block means reject high-frequency noise.
            int stride = Math.Max(1, (int)Math.Ceiling(n / 12000.0));
            var y = new double[n / stride];
            for (int i = 0; i < y.Length; i++)
                for (int j = 0; j < stride; j++) y[i] += rawVolts[begin + i * stride + j] / stride;
            double fs = fsHz / stride, min = Double.PositiveInfinity, max = Double.NegativeInfinity;
            for (int i = 0; i < y.Length; i++) { min = Math.Min(min, y[i]); max = Math.Max(max, y[i]); }
            double halfSpan = (max - min) * .5;
            if (halfSpan < .002) return Invalid(r, "输入幅值低于 2 mV 分析门槛，按背景处理");
            double[] smooth = new double[y.Length];
            // Three-point smoothing is only for frequency seeding; model fit uses original block means.
            for (int i = 1; i < y.Length - 1; i++) smooth[i] = (y[i - 1] + y[i] + y[i + 1]) / 3;
            smooth[0] = y[0]; smooth[y.Length - 1] = y[y.Length - 1];
            double[] tail = new double[Math.Max(16, y.Length / 5)];
            Array.Copy(y, y.Length - tail.Length, tail, 0, tail.Length);
            double baseline = Median(tail);
            var crossings = new List<double>();
            bool armed = false;
            for (int i = 1; i < smooth.Length; i++)
            {
                if (smooth[i] - baseline < -halfSpan * .06) armed = true;
                if (armed && smooth[i - 1] < baseline && smooth[i] >= baseline)
                {
                    double fraction = (baseline - smooth[i - 1]) / (smooth[i] - smooth[i - 1]);
                    crossings.Add((i - 1 + fraction) / fs); armed = false;
                }
            }
            if (crossings.Count < 4) return Invalid(r, "不足三个完整振荡周期");
            var periods = new double[crossings.Count - 1];
            for (int i = 0; i < periods.Length; i++) periods[i] = crossings[i + 1] - crossings[i];
            double period = Median(periods), frequency = 1 / period;
            if (!Positive(frequency) || frequency < 20 || frequency > Math.Min(5000, fs / 12))
                return Invalid(r, "频率超出 20～5000 Hz 或每周期采样点不足");
            int regularPeriods = 0;
            foreach (double p in periods) if (Math.Abs(p / period - 1) < .15) regularPeriods++;
            if (regularPeriods < 3 || regularPeriods < .8 * periods.Length)
                return Invalid(r, "周期不稳定，无法认定阻尼振荡");

            Fit best = null;
            double fLow = frequency * .9, fHigh = frequency * 1.1;
            double aLow = 0, aHigh = .1 * 2 * Math.PI * frequency;
            // Variable projection: for each frequency/decay solve cos/sin/DC linearly, refine grid.
            for (int round = 0; round < 6; round++)
            {
                double df = (fHigh - fLow) / 6, da = (aHigh - aLow) / 6;
                for (int ai = 0; ai <= 6; ai++) for (int fi = 0; fi <= 6; fi++)
                {
                    Fit fit = FitModel(y, fs, aLow + ai * da, fLow + fi * df);
                    if (fit != null && (best == null || fit.Error < best.Error)) best = fit;
                }
                if (best == null) return Invalid(r, "模型拟合失败");
                fLow = Math.Max(1, best.Frequency - df); fHigh = best.Frequency + df;
                aLow = Math.Max(0, best.Alpha - da); aHigh = best.Alpha + da;
            }
            double mean = 0, total = 0;
            foreach (double v in y) mean += v / y.Length;
            foreach (double v in y) total += (v - mean) * (v - mean);
            r.FitRSquared = total > 0 ? 1 - best.Error / total : Double.NaN;
            double residual = Math.Sqrt(best.Error / y.Length);
            double amplitude = Math.Sqrt(best.Cos * best.Cos + best.Sin * best.Sin);
            if (!Finite(r.FitRSquared) || r.FitRSquared < .97 || amplitude < Math.Max(.002, residual * 20))
                return Invalid(r, "单频指数衰减拟合质量不足，参数无效");
            if (best.Frequency * y.Length / fs < 3 || best.Alpha <= .01 || best.Alpha / (2 * Math.PI * best.Frequency) > .1)
                return Invalid(r, "衰减或周期数不满足参数估计条件");
            double omega = 2 * Math.PI * best.Frequency;
            // Same topology/formula as Task6 src/owts_wave_preprocess_fixed.cpp lines 862-870:
            // series coil R+L, capacitor plus parallel dielectric conductance.
            // C = 1 / [L*(omega_d^2 + alpha^2) - R*(2*alpha - R/L)].
            // Apparent loss estimate = 2*alpha/omega_d - R/(omega_d*L).
            // Only coil DC resistance is subtracted; fixture/divider/corona losses remain.
            // This is NOT a standards-compliant dielectric-loss measurement or real ENOB assessment.
            double denominator = inductanceH * (omega * omega + best.Alpha * best.Alpha)
                - resistanceOhm * (2 * best.Alpha - resistanceOhm / inductanceH);
            double loss = 2 * best.Alpha / omega - resistanceOhm / (omega * inductanceH);
            if (!Positive(denominator) || !Finite(loss) || loss < 0)
                return Invalid(r, "扣除电感电阻后损耗为负/模型不一致，不输出虚假介损");
            r.Valid = true; r.FrequencyHz = best.Frequency; r.DampingPerSecond = best.Alpha;
            r.CapacitanceF = 1 / denominator; r.CorrectedTanDeltaEstimate = loss;
            r.Status = "单频阻尼模型估计；仅扣除电感直流电阻，未扣夹具/分压器损耗";
            return r;
        }

        public static PdAnalysisResult AnalyzePd(short[] adc, double fsHz, int maxAdc, double fullScaleVolts,
            double calibrationPcPerV, double skipMs = .5, bool driverOverflow = false)
        {
            var r = new PdAnalysisResult { ExclusionMs = skipMs };
            if (adc == null || adc.Length == 0 || !Positive(fsHz) || maxAdc <= 0 || maxAdc > 32767 ||
                !Positive(fullScaleVolts) || !Positive(calibrationPcPerV) || !Finite(skipMs) || skipMs < 0)
            { r.Status = "局放输入参数或数据无效"; return r; }
            int begin = (int)Math.Min(adc.Length, Math.Ceiling(skipMs * .001 * fsHz));
            double scale = fullScaleVolts / maxAdc;
            // Robust DC estimate from up to 8192 evenly spread samples AFTER transient exclusion.
            int count = Math.Min(8192, adc.Length - begin);
            if (count == 0) { r.Status = "排除窗口后无数据"; return r; }
            var sample = new double[count];
            for (int i = 0; i < count; i++) sample[i] = adc[begin + (int)((long)i * (adc.Length - begin) / count)] * scale;
            r.BaselineVolts = Median(sample); r.Clipped = driverOverflow;
            for (int i = 0; i < sample.Length; i++) sample[i] = Math.Abs(sample[i] - r.BaselineVolts);
            // A candidate is not a physical PD classification. Require two adjacent samples above
            // robust 8-sigma background AND 8 ADC counts; this gate only permits range shrinking.
            double candidateThreshold = Math.Max(8 * 1.4826 * Median(sample), 8 * scale);
            r.CandidateThresholdVolts = candidateThreshold;
            int aboveRun = 0;
            for (int i = 0; i < adc.Length; i++)
            {
                int absolute = Math.Abs((int)adc[i]);
                if (absolute >= maxAdc) r.Clipped = true;
                double v = adc[i] * scale, rawAbs = Math.Abs(v), corrected = Math.Abs(v - r.BaselineVolts);
                if (rawAbs > r.RawAbsolutePeakVolts) { r.RawAbsolutePeakVolts = rawAbs; r.RawPeakTimeMs = i * 1000.0 / fsHz; }
                r.BaselineCorrectedPeakVolts = Math.Max(r.BaselineCorrectedPeakVolts, corrected);
                if (i >= begin && corrected > r.PostExclusionPeakVolts)
                { r.PostExclusionPeakVolts = corrected; r.PostExclusionPeakTimeMs = i * 1000.0 / fsHz; }
                if (i >= begin && corrected > candidateThreshold) aboveRun++;
                else { if (aboveRun >= 2) r.PulseCandidateCount++; aboveRun = 0; }
            }
            if (aboveRun >= 2) r.PulseCandidateCount++;
            r.Valid = !r.Clipped;
            if (r.Valid) r.MaximumEquivalentPc = r.PostExclusionPeakVolts * calibrationPcPerV;
            r.Status = r.Clipped ? "削顶/驱动溢出：峰值仅为下限，本轮等效电量无效" :
                "排除窗口后基线校正最大输入峰值×校准系数；含噪声/外部脉冲，未经物理局放识别";
            return r;
        }

        public static RangeProposal NextRange(double rawAbsolutePeakVolts, bool clipped, int currentRangeIndex, RangeState state, bool pulseDetected = false)
        {
            if (state == null) throw new ArgumentNullException("state");
            if (currentRangeIndex < 0 || currentRangeIndex >= Ranges.Length) throw new ArgumentOutOfRangeException("currentRangeIndex");
            if (state.PreviousIndex != currentRangeIndex) { state.LowCaptures = 0; state.PreviousIndex = currentRangeIndex; }
            var r = new RangeProposal { Index = currentRangeIndex, Reason = "保留当前量程" };
            if (!Finite(rawAbsolutePeakVolts) || rawAbsolutePeakVolts < 0)
            { state.LowCaptures = 0; r.Reason = "数据无效，不调整量程"; return r; }
            double current = Ranges[currentRangeIndex];
            if (clipped || rawAbsolutePeakVolts > current * .8)
            {
                state.LowCaptures = 0;
                r.Index = Math.Min(Ranges.Length - 1, Math.Max(currentRangeIndex + 1, FitRange(rawAbsolutePeakVolts / .7)));
                r.AtMaximumAndClipped = r.Index == currentRangeIndex;
                r.Reason = r.AtMaximumAndClipped ? "已到最大量程，仍过载/余量不足" : "全段原始峰值/过冲超过 80%，下一轮升档";
            }
            else if (pulseDetected && rawAbsolutePeakVolts > 0 && rawAbsolutePeakVolts < current * .2 && currentRangeIndex > 0)
            {
                state.LowCaptures++;
                r.Reason = "低占用 " + state.LowCaptures + "/3；连续三轮后才允许降一档";
                if (state.LowCaptures >= 3)
                {
                    r.Index = Math.Max(currentRangeIndex - 1, FitRange(rawAbsolutePeakVolts / .65));
                    r.Index = Math.Min(currentRangeIndex, r.Index); state.LowCaptures = 0;
                    r.Reason = "连续三轮低占用，下一轮最多降一档并保留余量";
                }
            }
            else { state.LowCaptures = 0; if (!pulseDetected) r.Reason = "未检出可分辨脉冲，仅背景时不自动降档"; }
            r.Change = r.Index != currentRangeIndex;
            return r;
        }

        public static int InitialRangeFromPulse(double pulsePc, double calibrationPcPerV)
        {
            if (!Positive(pulsePc) || !Positive(calibrationPcPerV)) throw new ArgumentException("校准脉冲和系数必须为正数");
            // Conservative starting estimate only. It cannot predict switching overshoot or DC offset.
            return FitRange(pulsePc / calibrationPcPerV / .5);
        }

        public static short[] CopyLocation20Ms(short[] adc, double fsHz, int triggerIndex = 0)
        {
            if (adc == null || !Positive(fsHz) || triggerIndex < 0 || triggerIndex > adc.Length) throw new ArgumentException("定位窗口参数无效");
            int count = (int)Math.Min(adc.Length - triggerIndex, Math.Floor(.020 * fsHz + 1e-9));
            var output = new short[count]; Array.Copy(adc, triggerIndex, output, 0, count); return output;
        }

        private static int FitRange(double minimum)
        { for (int i = 0; i < Ranges.Length; i++) if (Ranges[i] >= minimum) return i; return Ranges.Length - 1; }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        private static bool Positive(double v) { return Finite(v) && v > 0; }
        private static VoltageAnalysisResult Invalid(VoltageAnalysisResult r, string status) { r.Status = status; return r; }
        private static double Median(double[] values)
        { double[] copy = (double[])values.Clone(); Array.Sort(copy); int n = copy.Length; return n % 2 != 0 ? copy[n / 2] : (copy[n / 2 - 1] + copy[n / 2]) * .5; }
        private sealed class Fit { public double Frequency, Alpha, Cos, Sin, Offset, Error; }
        private static Fit FitModel(double[] y, double fs, double alpha, double frequency)
        {
            double cc = 0, ss = 0, cs = 0, c = 0, s = 0, cy = 0, sy = 0, sum = 0;
            double omega = 2 * Math.PI * frequency;
            for (int i = 0; i < y.Length; i++)
            {
                double t = i / fs, e = Math.Exp(-alpha * t), a = e * Math.Cos(omega * t), b = e * Math.Sin(omega * t);
                cc += a * a; ss += b * b; cs += a * b; c += a; s += b; cy += a * y[i]; sy += b * y[i]; sum += y[i];
            }
            double[,] m = { { cc, cs, c, cy }, { cs, ss, s, sy }, { c, s, y.Length, sum } };
            for (int k = 0; k < 3; k++)
            {
                int pivot = k; for (int j = k + 1; j < 3; j++) if (Math.Abs(m[j, k]) > Math.Abs(m[pivot, k])) pivot = j;
                if (Math.Abs(m[pivot, k]) < 1e-12) return null;
                if (pivot != k) for (int j = k; j < 4; j++) { double swap = m[k, j]; m[k, j] = m[pivot, j]; m[pivot, j] = swap; }
                double d = m[k, k]; for (int j = k; j < 4; j++) m[k, j] /= d;
                for (int row = 0; row < 3; row++) if (row != k)
                { double f = m[row, k]; for (int j = k; j < 4; j++) m[row, j] -= f * m[k, j]; }
            }
            var r = new Fit { Frequency = frequency, Alpha = alpha, Cos = m[0, 3], Sin = m[1, 3], Offset = m[2, 3] };
            for (int i = 0; i < y.Length; i++)
            {
                double t = i / fs, predicted = Math.Exp(-alpha * t) * (r.Cos * Math.Cos(omega * t) + r.Sin * Math.Sin(omega * t)) + r.Offset;
                double error = y[i] - predicted; r.Error += error * error;
            }
            return r;
        }
    }
}
