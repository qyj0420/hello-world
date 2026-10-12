using System;
using System.Collections.Generic;
using System.Globalization;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryReferenceResult
    {
        public bool Valid, PhaseValid, DecayValid;
        public double InductanceH = Double.NaN;
        public double MeasuredTotalSeriesResistanceOhms = Double.NaN;
        public double ExpectedCapacitorEsrOhms = Double.NaN;
        public double OtherSeriesResistanceOhms = Double.NaN;
        public double ExpectedAlphaPerSecond = Double.NaN;
        public double MeasuredDecayPercent = Double.NaN;
        public double ExpectedDecayPercent = Double.NaN;
        public double SingleCycleDecayPercent = Double.NaN;
        public double AmplitudeRatio = Double.NaN;
        public double FirstZeroMs = Double.NaN;
        public double BaselineV = Double.NaN;
        public double RequiredDecaySpanMs = Double.NaN, ActualDecaySpanMs = Double.NaN;
        public int CompleteCycles;
        public int RequestedCycles = 8;
        public double[] CyclePeakKv = new double[0];
        public double[] CyclePeakMs = new double[0];
        public string Detail = "";
    }

    /// <summary>
    /// Read-only diagnostics for the same topology as 100: coil series R + L,
    /// with a capacitor and parallel dielectric conductance. All non-dielectric
    /// loss remains explicitly attributed to the coil and rest of the circuit.
    /// </summary>
    public static class FactoryReferenceAnalysis
    {
        public static FactoryReferenceResult Analyze(double[] raw, double rateHz, double dividerVPerV,
            double exclusionMs, double frequencyHz, double alphaPerSecond, bool fitValid,
            double capacitanceNf, double capacitorTanDelta, double coilResistanceOhms, int decayCycles = 8,
            bool referenceEnabled = true)
        {
            var result = new FactoryReferenceResult { RequestedCycles = decayCycles };
            if (raw == null || raw.Length < 32 || !Positive(rateHz) || !Positive(dividerVPerV) ||
                !Finite(exclusionMs) || exclusionMs < 0 || decayCycles < 1 || decayCycles > 100)
                return Invalid(result, "实际电压记录或相位/衰减参数无效。");
            if (!fitValid || !Positive(frequencyHz) || !Finite(alphaPerSecond) || alphaPerSecond < 0)
                return Invalid(result, "没有通过单频阻尼模型验证的振荡电压，不能反算电感或补造衰减。");
            double periodSamples = rateHz / frequencyHz;
            if (periodSamples < 16) return Invalid(result, "实际采样率不足以支持逐周期峰值与第一零相位。");
            int begin = (int)Math.Min(raw.Length, Math.Ceiling(exclusionMs * rateHz / 1000));
            if (raw.Length - begin < 32) return Invalid(result, "排除起始过冲后记录不足。");
            for (int i = 0; i < raw.Length; i++)
                if (!Finite(raw[i]) || Math.Abs(raw[i]) >= 9.95)
                    return Invalid(result, "分压器原始电压非有限或触及 ±10 V 输入边界，标准电容结果无效。");

            double omega = 2 * Math.PI * frequencyHz;
            double baseline, residualRatio;
            if (!FitBaseline(raw, begin, rateHz, omega, alphaPerSecond, out baseline, out residualRatio) || residualRatio > .25)
                return Invalid(result, "电压相位与阻尼模型不一致，不能可靠提取标准电容结果。");

            result.BaselineV = baseline;
            string modelDetail = "未启用标准电容反算；振荡相位与实测衰减仍由本轮已验证电压计算。";
            if (referenceEnabled)
            {
                if (!Positive(capacitanceNf) || !Finite(capacitorTanDelta) || capacitorTanDelta < 0 ||
                    capacitorTanDelta > .2 || !Finite(coilResistanceOhms) || coilResistanceOhms < 0)
                    modelDetail = "标准电容参数无效，未反算电感；独立相位和实测衰减仍保留。";
                else
                {
                    double capacitanceF = capacitanceNf * 1e-9;
                    // alpha=(R/L+G/C)/2; D=G/C=omega_d*tan(delta).
                    // Eliminate R/L from omega_d^2+alpha^2=1/(LC)+(R/L)*D.
                    double dielectricRate = omega * capacitorTanDelta;
                    double inductance = 1 / (capacitanceF * (omega * omega +
                        (alphaPerSecond - dielectricRate) * (alphaPerSecond - dielectricRate)));
                    double equivalentR = (2 * alphaPerSecond - dielectricRate) * inductance;
                    if (!Positive(inductance)) modelDetail = "标准电容反算电感非有限或非正数，参考模型无效。";
                    else if (equivalentR < -1e-9)
                        modelDetail = "实测衰减低于所填标准电容介质损耗，反算串联电阻为负；标准参考模型无效，独立相位和实测衰减仍保留。";
                    else
                    {
                        result.Valid = true;
                        result.InductanceH = inductance;
                        result.MeasuredTotalSeriesResistanceOhms = equivalentR;
                        result.ExpectedCapacitorEsrOhms = capacitorTanDelta / (omega * capacitanceF);
                        result.OtherSeriesResistanceOhms = equivalentR - coilResistanceOhms;
                        result.ExpectedAlphaPerSecond = (coilResistanceOhms / inductance + dielectricRate) / 2;
                        result.ExpectedDecayPercent = 100 * (1 - Math.Exp(-result.ExpectedAlphaPerSecond * decayCycles / frequencyHz));
                        modelDetail = BaseDetail(result, capacitanceNf, capacitorTanDelta);
                    }
                }
            }

            // Same attenuation definition as 100 DataBrowser BrowserDamping:
            // 100 * (1 - peak[8] / peak[0]); eight elapsed cycles require nine
            // same-polarity peaks. Never use eight peaks (only seven intervals).
            var smooth = new double[raw.Length];
            for (int i = begin; i < raw.Length; i++)
                smooth[i] = ((i > begin ? raw[i - 1] : raw[i]) + raw[i] +
                    (i + 1 < raw.Length ? raw[i + 1] : raw[i])) / 3 - baseline;
            double maximum = 0;
            var curvature = new List<double>();
            for (int i = begin; i < raw.Length; i++)
            {
                maximum = Math.Max(maximum, Math.Abs(smooth[i]));
                if (i > begin && i + 1 < raw.Length) curvature.Add(Math.Abs(raw[i + 1] - 2 * raw[i] + raw[i - 1]));
            }
            double noise = Median(curvature.ToArray()) / (.67448975 * Math.Sqrt(6));
            double gate = Math.Max(maximum * .015, noise * 4);
            if (!(maximum > Math.Max(1e-6, noise * 10)))
                return Invalid(result, "电压信噪比不足，不能将背景噪声作为标准电容振荡。");
            var crossings = new List<double>();
            int state = smooth[begin] > gate ? 1 : smooth[begin] < -gate ? -1 : 0;
            double candidate = Double.NaN;
            for (int i = begin + 1; i < smooth.Length; i++)
            {
                double left = smooth[i - 1], right = smooth[i];
                if ((left <= 0 && right > 0) || (left >= 0 && right < 0))
                    candidate = i - 1 + (-left / (right - left));
                int next = right > gate ? 1 : right < -gate ? -1 : state;
                if (next != state && state != 0 && Finite(candidate))
                {
                    if (crossings.Count > 0)
                    {
                        double half = candidate - crossings[crossings.Count - 1];
                        if (Math.Abs(half / (periodSamples * .5) - 1) > .20)
                            return Invalid(result, "第一零相位后的过零间隔异常，未改用后续片段补齐周期。");
                    }
                    crossings.Add(candidate);
                }
                if (next != 0) state = next;
            }
            if (crossings.Count == 0)
                return Invalid(result, "未找到经过幅值门槛确认的第一零相位。");
            result.FirstZeroMs = crossings[0] * 1000 / rateHz;
            result.PhaseValid = true;
            result.CompleteCycles = (crossings.Count - 1) / 2;
            var peakKv = new List<double>();
            var peakMs = new List<double>();
            for (int cycle = 0; cycle < result.CompleteCycles && peakKv.Count <= decayCycles; cycle++)
            {
                // First half of each complete cycle has the same polarity.
                int first = (int)Math.Ceiling(crossings[2 * cycle]);
                int last = (int)Math.Floor(crossings[2 * cycle + 1]);
                int index = first;
                double peak = 0;
                for (int i = first; i <= last; i++)
                    if (Math.Abs(smooth[i]) > peak) { peak = Math.Abs(smooth[i]); index = i; }
                if (!(peak > Math.Max(noise * 8, maximum * .005))) break;
                peakKv.Add(peak * dividerVPerV / 1000);
                peakMs.Add(index * 1000 / rateHz);
            }
            result.CyclePeakKv = peakKv.ToArray();
            result.CyclePeakMs = peakMs.ToArray();
            result.RequiredDecaySpanMs = decayCycles * 1000.0 / frequencyHz;
            result.ActualDecaySpanMs = peakMs.Count < 2 ? 0 : peakMs[peakMs.Count - 1] - peakMs[0];
            string durationDetail = "衰减所需同极性峰跨度 " + F(result.RequiredDecaySpanMs) +
                " ms / 实际可靠峰跨度 " + F(result.ActualDecaySpanMs) +
                " ms；记录 " + F(raw.Length * 1000.0 / rateHz) + " ms。此条件不限制量程评估或连续轮次。";
            if (peakKv.Count >= 2) result.SingleCycleDecayPercent = 100 * (1 - peakKv[1] / peakKv[0]);
            if (peakKv.Count <= decayCycles)
            {
                result.Detail = modelDetail + "\r\n" +
                    "实测 " + decayCycles + " 周期衰减无有效值：第一零相位后仅 " + peakKv.Count +
                    " 个可靠同极性周期峰，需要 " + (decayCycles + 1) + " 个；不外推或缩短周期数。" + durationDetail;
                return result;
            }
            double actualSpan = peakMs[decayCycles] - peakMs[0];
            if (Math.Abs(actualSpan / (1000 * decayCycles / frequencyHz) - 1) > .03)
                return Invalid(result, "同极性峰未覆盖要求的完整周期跨度，衰减无效。");
            result.AmplitudeRatio = peakKv[decayCycles] / peakKv[0];
            result.MeasuredDecayPercent = 100 * (1 - result.AmplitudeRatio);
            result.DecayValid = Finite(result.MeasuredDecayPercent);
            result.Detail = modelDetail + "\r\n" +
                "实测 " + decayCycles + " 周期衰减 = 100×(1−第 " + (decayCycles + 1) +
                " 个/第 1 个同极性峰) = " + F(result.MeasuredDecayPercent) +
                " %；幅值保留 " + F(result.AmplitudeRatio * 100) + " %。从第一零相位后的首个同极性峰起算。" + durationDetail;
            if (result.OtherSeriesResistanceOhms < -Math.Max(1, coilResistanceOhms * .1))
                result.Detail += "\r\n实测总损耗低于所填线圈电阻与标准电容损耗之和，请核对标准参数、接线和阻尼模型。";
            if (result.MeasuredDecayPercent < 0)
                result.Detail += "\r\n同极性幅值增长，不能作为被动衰减回路通过结论。";
            return result;
        }

        private static string BaseDetail(FactoryReferenceResult r, double capacitanceNf, double tanDelta)
        {
            return "标准电容 " + F(capacitanceNf) + " nF、tanδ " + F(tanDelta * 100) +
                " %；按线圈 R+L、电容并联介质电导模型，D=2πf·tanδ，L=1/[C((2πf)²+(α−D)²)] = " + F(r.InductanceH) +
                " H。扣除已知介质损耗后的绕组/回路等效串联电阻 " + F(r.MeasuredTotalSeriesResistanceOhms) +
                " Ω；标准电容等效 ESR " + F(r.ExpectedCapacitorEsrOhms) +
                " Ω（仅说明）；扣除设置线圈电阻后的其他等效损耗 " + F(r.OtherSeriesResistanceOhms) +
                " Ω。理论 " + r.RequestedCycles + " 周期衰减 " + F(r.ExpectedDecayPercent) +
                " % 仅含这两项已知损耗，用于比较，未把所有阻尼当作电容介损。";
        }

        private static bool FitBaseline(double[] raw, int begin, double rate, double omega, double alpha,
            out double baseline, out double residualRatio)
        {
            baseline = residualRatio = Double.NaN;
            var matrix = new double[3, 4];
            for (int i = begin; i < raw.Length; i++)
            {
                double t = (i - begin) / rate, env = Math.Exp(-alpha * t);
                double[] row = { env * Math.Cos(omega * t), env * Math.Sin(omega * t), 1 };
                for (int a = 0; a < 3; a++)
                {
                    for (int b = 0; b < 3; b++) matrix[a, b] += row[a] * row[b];
                    matrix[a, 3] += row[a] * raw[i];
                }
            }
            for (int pivot = 0; pivot < 3; pivot++)
            {
                int best = pivot;
                for (int row = pivot + 1; row < 3; row++)
                    if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
                if (Math.Abs(matrix[best, pivot]) < 1e-12) return false;
                for (int col = pivot; col < 4; col++)
                { double tmp = matrix[pivot, col]; matrix[pivot, col] = matrix[best, col]; matrix[best, col] = tmp; }
                double scale = matrix[pivot, pivot];
                for (int col = pivot; col < 4; col++) matrix[pivot, col] /= scale;
                for (int row = 0; row < 3; row++) if (row != pivot)
                {
                    double factor = matrix[row, pivot];
                    for (int col = pivot; col < 4; col++) matrix[row, col] -= factor * matrix[pivot, col];
                }
            }
            baseline = matrix[2, 3];
            double residual = 0, total = 0;
            for (int i = begin; i < raw.Length; i++)
            {
                double t = (i - begin) / rate;
                double fit = Math.Exp(-alpha * t) * (matrix[0, 3] * Math.Cos(omega * t) +
                    matrix[1, 3] * Math.Sin(omega * t)) + baseline;
                residual += (raw[i] - fit) * (raw[i] - fit);
                total += (raw[i] - baseline) * (raw[i] - baseline);
            }
            residualRatio = total > 0 ? Math.Sqrt(residual / total) : Double.NaN;
            return Finite(baseline) && Finite(residualRatio);
        }
        private static FactoryReferenceResult Invalid(FactoryReferenceResult r, string detail)
        { r.Valid = r.PhaseValid = r.DecayValid = false; r.Detail = detail; return r; }
        private static double Median(double[] v)
        { if (v.Length == 0) return 0; Array.Sort(v); int m = v.Length / 2; return (v.Length & 1) != 0 ? v[m] : .5 * (v[m - 1] + v[m]); }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        private static bool Positive(double v) { return Finite(v) && v > 0; }
        private static string F(double v) { return v.ToString("G6", CultureInfo.InvariantCulture); }
    }
}
