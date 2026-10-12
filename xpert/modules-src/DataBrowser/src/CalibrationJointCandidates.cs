using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace HVX.Controls
{
    // All algorithm coordinates are seconds. Only the plot adapter uses microseconds.
    internal sealed class CalibrationTraceGeometry
    {
        internal string Name, Phase, Kind, SourceDescription;
        internal double IncidentSeconds, LengthM, VelocityMPerUs, SampleIntervalSeconds;
        internal double ReflectionSeconds = Double.NaN;
        internal double EndSeconds { get { return IncidentSeconds + 2 * LengthM / VelocityMPerUs * 1e-6; } }
        internal double DistanceAt(double seconds) { return (seconds - IncidentSeconds) * 1e6 * VelocityMPerUs / 2; }
        internal static CalibrationTraceGeometry Compatible(IList<CalibrationTraceGeometry> sources, bool align)
        {
            if (sources == null || sources.Count == 0) throw new InvalidDataException("没有完整的校准几何参数。 / No calibration geometry.");
            var first = sources[0];
            foreach (var g in sources)
            {
                if (g == null || !Finite(g.IncidentSeconds) || !Finite(g.SampleIntervalSeconds) || g.SampleIntervalSeconds <= 0)
                    throw new InvalidDataException("校准入射时刻或采样时基无效，不能平均定位。 / Invalid incident or sample time.");
                if (!String.Equals(g.Phase, first.Phase, StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(g.Phase) ||
                    !String.Equals(g.Kind, first.Kind, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("不同相别或不同负载的校准不能平均定位；请分组选择。 / Select matching phases and load types.");
                if (!String.Equals(g.Kind, "Capacitor", StringComparison.OrdinalIgnoreCase) &&
                    (!Finite(g.LengthM) || !Finite(g.VelocityMPerUs) || g.LengthM <= 0 || g.VelocityMPerUs <= 0 ||
                     Math.Abs(g.LengthM - first.LengthM) > Math.Max(.001, first.LengthM * 1e-6) ||
                     Math.Abs(g.VelocityMPerUs - first.VelocityMPerUs) > Math.Max(.0001, first.VelocityMPerUs * 1e-6)))
                    throw new InvalidDataException("所选校准长度或波速不相容，不能平均定位；请分组选择。 / Incompatible cable length or velocity.\r\n" +
                        "基准 / Reference: " + (first.Name ?? "(first)") + "; 长度容差 / length tolerance = " + Number(Math.Max(.001, first.LengthM * 1e-6)) +
                        " m; 波速容差 / velocity tolerance = " + Number(Math.Max(.0001, first.VelocityMPerUs * 1e-6)) + " m/μs\r\n" +
                        DescribeSources(sources, first) + "\r\n缺失或无效参数不能用于统一距离和接头定位；不影响时间轴波形平均，原档参数保持不变。 / Missing or invalid geometry disables common distance and joint location, not time-domain averaging; source metadata is unchanged.");
                if (!align && Math.Abs(g.IncidentSeconds - first.IncidentSeconds) > Math.Min(g.SampleIntervalSeconds, first.SampleIntervalSeconds) * .1)
                    throw new InvalidDataException("所选入射时刻不同，不能共用距离起点；如需距离定位，请勾选“入射游标对齐”。 / Different incident times cannot share a distance origin; align incident cursors for location.");
            }
            return new CalibrationTraceGeometry { Name=first.Name, SourceDescription=DescribeSources(sources,first), Phase = first.Phase, Kind = String.Equals(first.Kind,"Capacitor",StringComparison.OrdinalIgnoreCase)?"Capacitor":"Cable", LengthM = first.LengthM,
                VelocityMPerUs = first.VelocityMPerUs, IncidentSeconds = align ? 0 : first.IncidentSeconds,
                SampleIntervalSeconds = sources.Min(g => g.SampleIntervalSeconds) };
        }
        static string DescribeSources(IList<CalibrationTraceGeometry> sources, CalibrationTraceGeometry first)
        {
            return "源档参数（原始时间轴） / Source metadata (original time axis):\r\n" + String.Join("\r\n", sources.Select((g,i)=>g==null?"["+(i+1)+"] 无几何参数 / No geometry":
                "["+(i+1)+"] "+(String.IsNullOrWhiteSpace(g.Name)?"(unnamed)":g.Name)+": L="+Number(g.LengthM)+" m; v="+Number(g.VelocityMPerUs)+" m/μs; " +
                "ΔL="+Number(g.LengthM-first.LengthM)+" m; Δv="+Number(g.VelocityMPerUs-first.VelocityMPerUs)+" m/μs; " +
                "入射 / incident="+Number(g.IncidentSeconds*1e6)+" μs; 保存反射游标 / saved reflection cursor="+Number(g.ReflectionSeconds*1e6)+" μs"));
        }
        internal static string Number(double value) { return Finite(value)?value.ToString("G10",CultureInfo.InvariantCulture):"未知/无效 (unknown/invalid)"; }
        internal static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
    }

    internal sealed class CalibrationJointCandidate
    {
        internal double Seconds, DistanceM, Score, Correlation, SignalToNoise;
        internal string Reason;
    }

    internal static class CalibrationJointCandidates
    {
        // Screening evidence, NOT a calibrated probability or automatic acceptance.
        // No file writes; reject terminal/later traversals and incident ringing.
        internal static CalibrationJointCandidate[] Find(double[] x, double[] y, CalibrationTraceGeometry geometry, CancellationToken token)
        {
            if (geometry == null || geometry.Kind == "Capacitor") throw new InvalidDataException("电容或无几何参数的波形不进行接头查找。 / No cable geometry for joint search.");
            CalibrationComparisonMath.Inspect(x, y, 0, token);
            if (x.Length < 32 || geometry.IncidentSeconds < x[0] || geometry.EndSeconds > x[x.Length - 1])
                throw new InvalidDataException("共同波形未覆盖完整电缆传播区间。 / The mean does not cover a complete cable traversal.");
            int incident = Nearest(x, geometry.IncidentSeconds), end = Nearest(x, geometry.EndSeconds);
            if (incident < 8 || end - incident < 24) throw new InvalidDataException("入射前噪声或电缆区间采样不足。 / Insufficient pre-incident noise or cable samples.");
            int pre = Math.Min(incident / 2, 8192); double[] noise = y.Take(pre).OrderBy(v => v).ToArray();
            double baseline = noise[noise.Length / 2];
            double[] deviations = noise.Select(v => Math.Abs(v - baseline)).OrderBy(v => v).ToArray();
            double sigma = deviations[deviations.Length / 2] * 1.4826;
            int reach = Math.Max(2, Math.Min(64, (end - incident) / 100));
            incident = Peak(y, Math.Max(1, incident - reach), Math.Min(y.Length - 2, incident + reach), baseline);
            double amplitude = Math.Abs(y[incident] - baseline);
            if (amplitude <= Math.Max(1e-14, sigma * 8)) throw new InvalidDataException("入射脉冲不足以支持可靠形态比较。 / Incident pulse is not reliable above noise.");
            int half = 1;
            while (half < 128 && incident - half >= 0 && incident + half < y.Length &&
                   (Math.Abs(y[incident - half] - baseline) > amplitude * .25 || Math.Abs(y[incident + half] - baseline) > amplitude * .25)) half++;
            int radius = Math.Max(3, half * 3), guard = Math.Max(radius * 2, reach);
            if (incident < radius || end + radius >= y.Length || incident + guard >= end - guard)
                throw new InvalidDataException("入射/末端包络不完整或区间过短，不能推断接头。 / Incomplete pulse envelope or short cable interval.");
            // A real pulse must be present at the metadata-predicted terminal, but
            // it is never itself a joint candidate. Do not fabricate an endpoint.
            int terminal = Peak(y, Math.Max(incident + guard, end - reach), Math.Min(y.Length - radius - 1, end + reach), baseline);
            if (Math.Abs(y[terminal] - baseline) < Math.Max(sigma * 6, amplitude * .005))
                throw new InvalidDataException("电缆末端附近没有可核验反射脉冲，接头查找已停止。 / No verifiable terminal reflection.\r\n" +
                    "当前图时间轴 / Current displayed time axis: 预测末端 / predicted terminal = "+CalibrationTraceGeometry.Number(geometry.EndSeconds*1e6)+" μs (t入射 + 2L/v); " +
                    "核查窗口 / checked window = "+CalibrationTraceGeometry.Number(x[Math.Max(incident+guard,end-reach)]*1e6)+" ～ "+
                    CalibrationTraceGeometry.Number(x[Math.Min(y.Length-radius-1,end+reach)]*1e6)+" μs\r\n" +
                    "窗口实测绝对峰值 / measured absolute peak = "+CalibrationTraceGeometry.Number(Math.Abs(y[terminal]-baseline))+
                    "; 所需阈值 / required threshold = "+CalibrationTraceGeometry.Number(Math.Max(sigma*6,amplitude*.005))+
                    " (max(6σ, 0.005 × incident); 当前显示幅值单位 / current displayed amplitude units)\r\n" +
                    (geometry.SourceDescription??"")+"\r\n预测位置及保存游标不是已检出反射的证据。请核对上述实际记录参数及该窗口原始波形，不会外推、移动末端或补造反射。 / Predictions and saved cursors are not detected reflections; check these source parameters and the actual waveform. No endpoint is moved or fabricated.");
            var peaks = new List<int>(); double threshold = Math.Max(sigma * 6, amplitude * .015);
            for (int i = incident + guard; i < Math.Min(end, terminal) - guard; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                double a = Math.Abs(y[i] - baseline);
                if (a >= threshold && a >= Math.Abs(y[i - 1] - baseline) && a > Math.Abs(y[i + 1] - baseline)) peaks.Add(i);
            }
            var accepted = new List<CalibrationJointCandidate>();
            foreach (int peak in peaks.OrderByDescending(i => Math.Abs(y[i] - baseline)))
            {
                token.ThrowIfCancellationRequested();
                if (accepted.Any(c => Math.Abs(c.Seconds - x[peak]) < radius * 2 * geometry.SampleIntervalSeconds)) continue;
                double dot = 0, aa = 0, bb = 0;
                int stride = Math.Max(1, radius / 32);
                for (int k = -radius; k <= radius; k += stride)
                { double a = y[incident + k] - baseline, b = y[peak + k] - baseline; dot += a * b; aa += a * a; bb += b * b; }
                double correlation = aa > 0 && bb > 0 ? Math.Abs(dot / Math.Sqrt(aa * bb)) : 0;
                if (!CalibrationTraceGeometry.Finite(correlation) || correlation < .65) continue;
                double snr = Math.Abs(y[peak] - baseline) / Math.Max(sigma, amplitude * 1e-9);
                double distance = geometry.DistanceAt(x[peak]);
                if (distance <= 0 || distance >= geometry.LengthM) continue;
                accepted.Add(new CalibrationJointCandidate { Seconds = x[peak], DistanceM = distance, Correlation = correlation,
                    SignalToNoise = snr, Score = Math.Min(95, 60 * correlation + 35 * Math.Min(1, snr / 20)),
                    Reason = "首轮电缆区间内的真实局部峰；与入射形态相关，超过噪声阈值。评分不是概率，可能是噪声/其他反射，必须人工复核。 / Local pulse inside the first cable traversal; shape and noise screening only. Score is not probability; manual review required." });
                if (accepted.Count == 32) break;
            }
            return accepted.OrderBy(c => c.DistanceM).ToArray();
        }
        static int Nearest(double[] x, double target)
        { int i = Array.BinarySearch(x, target); if (i >= 0) return i; i = ~i; if (i == 0) return 0; if (i == x.Length) return i - 1; return target - x[i - 1] < x[i] - target ? i - 1 : i; }
        static int Peak(double[] y, int first, int last, double baseline)
        { int peak = first; for (int i = first + 1; i <= last; i++) if (Math.Abs(y[i] - baseline) > Math.Abs(y[peak] - baseline)) peak = i; return peak; }
    }
}
