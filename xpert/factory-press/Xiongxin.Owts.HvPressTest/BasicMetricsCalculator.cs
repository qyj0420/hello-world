using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 加压采集后的公开简化量。对照 dac-source PressureTestAnalysis / Task39：
    ///   频率：分压器波形上升过零中位周期 → f=1/T（20–5000 Hz）
    ///   电容：C = 1/(ω²·L)（μF）；无有效 f 或 L→NotAvailable
    ///   放电量：峰电压 × PdCalibrationPcPerVolt；无校准系数→NotAvailable（不伪造）
    ///   接榫/接头：本程序不做测距定位 → 恒 NotAvailable（配置透传位置另记）
    /// </summary>
    public sealed class BasicMetricsResult
    {
        public string FrequencyStatus { get; set; }
        public double OscillationFrequencyHz { get; set; }
        public string CapacitanceStatus { get; set; }
        public double EquivalentCapacitanceUf { get; set; }
        public string DischargeStatus { get; set; }
        public double ApparentChargePc { get; set; }
        public double PdPeakVolts { get; set; }
        public string JointStatus { get; set; }
        public string Diagnostic { get; set; }

        public BasicMetricsResult()
        {
            FrequencyStatus = "NotAvailable";
            OscillationFrequencyHz = double.NaN;
            CapacitanceStatus = "NotAvailable";
            EquivalentCapacitanceUf = double.NaN;
            DischargeStatus = "NotAvailable";
            ApparentChargePc = double.NaN;
            PdPeakVolts = double.NaN;
            JointStatus = "NotAvailable";
            Diagnostic = string.Empty;
        }

        public string ToCsvFragment()
        {
            return string.Join(",",
                Csv(FrequencyStatus),
                Num(OscillationFrequencyHz),
                Csv(CapacitanceStatus),
                Num(EquivalentCapacitanceUf),
                Csv(DischargeStatus),
                Num(ApparentChargePc),
                Num(PdPeakVolts),
                Csv(JointStatus));
        }

        public static string CsvHeaderFragment()
        {
            return "frequencyStatus,oscillationFrequencyHz,capacitanceStatus,equivalentCapacitanceUf,"
                + "dischargeStatus,apparentChargePc,pdPeakVolts,jointStatus";
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            return v.ToString("G9", CultureInfo.InvariantCulture);
        }

        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }

    public static class BasicMetricsCalculator
    {
        public const double MinFrequencyHz = 20.0;
        public const double MaxFrequencyHz = 5000.0;

        public static BasicMetricsResult Compute(AcquisitionCycleResult cycle, PressTestConfig config)
        {
            var r = new BasicMetricsResult();
            var notes = new StringBuilder();

            // —— 接榫：无定位分析 ——
            r.JointStatus = "NotAvailable";
            notes.Append("joint=NotAvailable(no location analysis);");

            if (cycle == null)
            {
                notes.Append("cycle=null;");
                r.Diagnostic = notes.ToString();
                return r;
            }

            // —— 频率：AI4 分压器上升过零 ——
            double freq;
            string freqDiag;
            if (TryEstimateFrequency(cycle.UsbDividerRawVolts, cycle.UsbSampleRateHz, out freq, out freqDiag))
            {
                r.FrequencyStatus = "Ok";
                r.OscillationFrequencyHz = freq;
            }
            else
            {
                r.FrequencyStatus = "NotAvailable";
                r.OscillationFrequencyHz = double.NaN;
            }
            notes.Append(freqDiag).Append(';');

            // —— 电容：C=1/(ω²L) ——
            if (r.FrequencyStatus == "Ok"
                && config != null
                && config.InductanceH > 0
                && !double.IsNaN(config.InductanceH)
                && !double.IsInfinity(config.InductanceH))
            {
                double omega = 2.0 * Math.PI * r.OscillationFrequencyHz;
                double cF = 1.0 / (omega * omega * config.InductanceH);
                double cUf = cF * 1e6;
                if (!double.IsNaN(cUf) && !double.IsInfinity(cUf) && cUf >= 0)
                {
                    r.CapacitanceStatus = "Ok";
                    r.EquivalentCapacitanceUf = cUf;
                    notes.Append("C=1/(w^2*L) L=").Append(config.InductanceH.ToString("G6", CultureInfo.InvariantCulture)).Append(';');
                }
                else
                {
                    r.CapacitanceStatus = "NotAvailable";
                    notes.Append("capacitance=invalid_result;");
                }
            }
            else
            {
                r.CapacitanceStatus = "NotAvailable";
                notes.Append(config == null || !(config.InductanceH > 0)
                    ? "capacitance=no_inductance;"
                    : "capacitance=no_frequency;");
            }

            // —— 放电量：峰电压 × 校准；无校准不伪造 pC ——
            double peakV;
            if (TryPdPeakVolts(cycle, out peakV))
            {
                r.PdPeakVolts = peakV;
                if (config != null && config.HasPdCalibration)
                {
                    r.DischargeStatus = "Ok";
                    r.ApparentChargePc = peakV * config.PdCalibrationPcPerVolt;
                    notes.Append("discharge=peakV*cal;");
                }
                else
                {
                    r.DischargeStatus = "NotAvailable";
                    notes.Append("discharge=NotAvailable(no pdCalibrationPcPerVolt; peakV reported);");
                }
            }
            else
            {
                r.DischargeStatus = "NotAvailable";
                notes.Append("discharge=no_pico_peak;");
            }

            r.Diagnostic = notes.ToString();
            return r;
        }

        /// <summary>
        /// 上升过零中位周期 → 频率。对照 OscillationPdControl / Task39 简化公开路径。
        /// 防倍频：相邻上升过零之间须有正负峰（完整周期）；并合并过近噪声双过零。
        /// </summary>
        public static bool TryEstimateFrequency(double[] samples, double sampleRateHz, out double frequencyHz, out string diagnostic)
        {
            frequencyHz = double.NaN;
            if (samples == null || samples.Length < 8 || !(sampleRateHz > 0))
            {
                diagnostic = "freq=insufficient_divider";
                return false;
            }

            double center = 0, yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
            for (int i = 0; i < samples.Length; i++)
            {
                double y = samples[i];
                center += y;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
            center /= samples.Length;
            double peakAbs = Math.Max(Math.Abs(yMin - center), Math.Abs(yMax - center));
            double ampGate = peakAbs > 1e-12 ? peakAbs * 0.12 : 0;

            var crossings = new List<double>();
            var crossIdx = new List<int>();
            for (int i = 1; i < samples.Length; i++)
            {
                double a = samples[i - 1] - center;
                double b = samples[i] - center;
                if (a <= 0 && b > 0)
                {
                    double den = b - a;
                    double frac = Math.Abs(den) < 1e-20 ? 0 : -a / den;
                    if (frac < 0) frac = 0;
                    if (frac > 1) frac = 1;
                    double t = (i - 1 + frac) / sampleRateHz;
                    // 合并过近噪声双上升过零（< 1/fMax）
                    if (crossings.Count > 0 && (t - crossings[crossings.Count - 1]) < (1.0 / MaxFrequencyHz))
                    {
                        crossings[crossings.Count - 1] = t;
                        crossIdx[crossIdx.Count - 1] = i;
                        continue;
                    }
                    crossings.Add(t);
                    crossIdx.Add(i);
                }
            }
            if (crossings.Count < 2)
            {
                diagnostic = "freq=no_rising_zero_crossings";
                return false;
            }

            double fMax = Math.Min(MaxFrequencyHz, sampleRateHz / 4.0);
            var periods = new List<double>();
            for (int i = 1; i < crossings.Count; i++)
            {
                double p = crossings[i] - crossings[i - 1];
                if (!(p > 0)) continue;
                double fCand = 1.0 / p;
                if (fCand < MinFrequencyHz || fCand > fMax) continue;
                // 完整周期校验：两上升过零之间须冲到 +ampGate 与 -ampGate（拒半周→倍频）
                int i0 = crossIdx[i - 1];
                int i1 = crossIdx[i];
                double segMax = double.NegativeInfinity, segMin = double.PositiveInfinity;
                for (int k = i0; k <= i1 && k < samples.Length; k++)
                {
                    double y = samples[k] - center;
                    if (y > segMax) segMax = y;
                    if (y < segMin) segMin = y;
                }
                if (!(segMax >= ampGate && segMin <= -ampGate)) continue;
                periods.Add(p);
            }
            if (periods.Count == 0)
            {
                diagnostic = "freq=no_inband_full_periods";
                return false;
            }
            periods.Sort();
            double period = periods[periods.Count / 2];
            double f = 1.0 / period;
            if (double.IsNaN(f) || double.IsInfinity(f) || f < MinFrequencyHz || f > fMax)
            {
                diagnostic = "freq=out_of_range f=" + f.ToString("G6", CultureInfo.InvariantCulture);
                return false;
            }
            frequencyHz = f;
            diagnostic = "freq=Ok f=" + f.ToString("G6", CultureInfo.InvariantCulture)
                + " crossings=" + crossings.Count + " fullPeriods=" + periods.Count;
            return true;
        }

        public static bool TryPdPeakVolts(AcquisitionCycleResult cycle, out double peakVolts)
        {
            peakVolts = double.NaN;
            if (cycle.PicoRawAdc == null || cycle.PicoRawAdc.Length == 0) return false;
            if (!(cycle.PicoMaximumAdc > 0) || !(cycle.PicoFullScaleVolts > 0)) return false;
            int peak = 0;
            for (int i = 0; i < cycle.PicoRawAdc.Length; i++)
            {
                int a = Math.Abs((int)cycle.PicoRawAdc[i]);
                if (a > peak) peak = a;
            }
            peakVolts = (peak / (double)cycle.PicoMaximumAdc) * cycle.PicoFullScaleVolts;
            return !double.IsNaN(peakVolts) && peakVolts >= 0;
        }
    }
}
