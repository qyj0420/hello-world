using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HVX.Controls
{
    internal static class BrowserDamping
    {
        internal static double[] FromVoltage(double[] voltage, double sampleRateHz, double frequencyHz)
        {
            if (voltage == null || voltage.Length < 32 || !BrowserPlot.Finite(sampleRateHz) || !BrowserPlot.Finite(frequencyHz)
                || sampleRateHz <= 0 || frequencyHz <= 0) return new[] { double.NaN, double.NaN };
            double expected = sampleRateHz / frequencyHz;
            if (expected < 8 || expected > voltage.Length / 2.0) return new[] { double.NaN, double.NaN };
            double maximum = double.NegativeInfinity, minimum = double.PositiveInfinity;
            int finiteCount = 0;
            foreach (double value in voltage)
                if (BrowserPlot.Finite(value)) { maximum = Math.Max(maximum, value); minimum = Math.Min(minimum, value); finiteCount++; }
            if (finiteCount < 32) return new[] { double.NaN, double.NaN };
            double amplitude = Math.Max(Math.Abs(maximum), Math.Abs(minimum));
            if (minimum >= 0 || maximum <= 0 || amplitude <= 0) return new[] { double.NaN, double.NaN };
            double center = (maximum + minimum) / 2.0;
            double hysteresis = amplitude * .03;
            var crossings = new List<int>(); bool armed = false;
            for (int i = 1; i < voltage.Length; i++)
            {
                double a = voltage[i - 1], b = voltage[i];
                if (!BrowserPlot.Finite(a) || !BrowserPlot.Finite(b)) { armed = false; continue; }
                if (a < center - hysteresis) armed = true;
                if (armed && a <= center && b > center)
                {
                    if (crossings.Count == 0 || i - crossings[crossings.Count - 1] >= expected * .65)
                        crossings.Add(i);
                    armed = false;
                }
            }
            var peaks = new List<double>();
            for (int c = 0; c + 1 < crossings.Count; c++)
            {
                int start = crossings[c], end = crossings[c + 1];
                double span = end - start;
                if (span < expected * .65 || span > expected * 1.35) { peaks.Clear(); continue; }
                double peak = double.NegativeInfinity;
                for (int i = start; i < end; i++) if (BrowserPlot.Finite(voltage[i]) && voltage[i] > peak) peak = voltage[i];
                if (peak > Math.Max(amplitude * .005, 1e-12)) peaks.Add(peak);
                else peaks.Clear();
            }
            double single = peaks.Count >= 2 ? 100.0 * (1.0 - peaks[1] / peaks[0]) : double.NaN;
            double eight = peaks.Count >= 9 ? 100.0 * (1.0 - peaks[8] / peaks[0]) : double.NaN;
            return new[] { single, eight };
        }
    }

    public sealed partial class ProjectDataBrowserControl
    {
        void AdjustInformationHeight()
        {
            if (rightLayout.RowStyles.Count < 4) return;
            bool statistics = SelectedNodeKind.StartsWith("stats", StringComparison.Ordinal);
            bool recordList = SelectedNodeKind.EndsWith("Group", StringComparison.Ordinal);
            informationHost.Visible = !statistics && !recordList;
            if (statistics || recordList)
            {
                rightLayout.RowStyles[3].Height = 0;
                return;
            }
            int desired = SelectedNodeKind == "dem" ? 180 : SelectedNodeKind == "cal" ? 132 : 130;
            int available = rightLayout.Height - 39 - (int)rightLayout.RowStyles[2].Height - 245;
            int height = Math.Max(90, Math.Min(desired, available));
            if (Math.Abs(rightLayout.RowStyles[3].Height - height) > 1) rightLayout.RowStyles[3].Height = height;
        }

        string ProjectSummary()
        {
            return string.Join(Environment.NewLine, new[] {
                T("项目：", "Project: ") + DisplayProjectTitle(),
                T("项目路径：", "Folder: ") + projectPath,
                project.MeasurementKind=="Capacitor"?T("电容局放 · 无定位", "Capacitor PD · no localization"):T("电缆长度：", "Cable length: ") + F(project.CableLengthMetres, "0.0") + " m",
                T("校准：", "Calibration: ") + project.Calibrations.Count.ToString(CultureInfo.InvariantCulture),
                T("测试：", "Tests: ") + project.Tests.Count.ToString(CultureInfo.InvariantCulture),
                T("定位：", "Locations: ") + project.Locations.Count.ToString(CultureInfo.InvariantCulture)
            });
        }

        string CalibrationSummary(BrowserRecord record, BrowserWaveform wave)
        {
            if(wave.MeasurementKind=="Capacitor") {
                string coefficient;record.Metadata.TryGetValue("校准系数 pC/V",out coefficient);
                return T("电容局放校准：","Capacitor PD calibration: ")+record.Name+Environment.NewLine+
                    T("相别：","Phase: ")+record.Phase+Environment.NewLine+
                    T("入射波：","Incident: ")+F(wave.IncidentSeconds*1e6,"0.###")+" μs"+Environment.NewLine+
                    T("校准系数：","Calibration coefficient: ")+(coefficient??"—")+" pC/V"+Environment.NewLine+
                    T("仅测量放电量；无反射定位或电缆几何。","Charge measurement only; no reflection localization or cable geometry.");
            }
            double loss = double.NaN;
            if (wave != null && wave.Y != null && wave.Y.Length > 0 && BrowserPlot.Finite(wave.SampleRateHz) && wave.SampleRateHz > 0
                && BrowserPlot.Finite(wave.IncidentSampleIndex) && BrowserPlot.Finite(wave.ReflectionSampleIndex))
            {
                int radius = Math.Max(8, (int)Math.Round(wave.SampleRateHz * .00000015));
                double direct = CalibrationWindowPeak(wave.Y, wave.IncidentSampleIndex, radius);
                double reflection = CalibrationWindowPeak(wave.Y, wave.ReflectionSampleIndex, radius);
                if (direct > 0 && BrowserPlot.Finite(reflection)) loss = 100.0 * (1.0 - reflection / direct);
            }
            return string.Join(Environment.NewLine, new[] {
                T("校准：", "Calibration: ") + record.Name,
                T("相别：", "Phase: ") + record.Phase,
                T("电缆长度：", "Cable length: ") + F(wave.CableLengthMetres, "0.0") + " m",
                T("波速：", "Velocity: ") + F(wave.VelocityMPerUs, "0.00") + " m/μs",
                T("衰减：", "Attenuation: ") + F(loss, "0.0") + " %"
            });
        }

        static double CalibrationWindowPeak(double[] values, double index, int radius)
        {
            int center = (int)Math.Round(index);
            int start = Math.Max(0, center - radius), end = Math.Min(values.Length - 1, center + radius);
            double peak = double.NaN;
            for (int i = start; i <= end; i++)
                if (BrowserPlot.Finite(values[i])) peak = BrowserPlot.Finite(peak) ? Math.Max(peak, Math.Abs(values[i])) : Math.Abs(values[i]);
            return peak;
        }

        string TestSummary(BrowserRecord record, BrowserWaveform wave)
        {
            double[] decay = BrowserDamping.FromVoltage(wave.VoltageKv, wave.VoltageSampleRateHz, record.FrequencyHz);
            var lines = new List<string> {
                T("电压：", "Voltage: ") + F(record.VoltageKv, "0.00") + " kV",
                T("次序：", "Sequence: ") + (record.Order > 0 ? record.Order.ToString(CultureInfo.InvariantCulture) : "—"),
                T("频率：", "Frequency: ") + F(record.FrequencyHz, "0.0") + " Hz",
                T("电容：", "Capacitance: ") + F(record.CapacitanceUf, "0.00") + " μF",
                T("介损：", "tan δ: ") + F(record.TanDelta, "0.00") + " %",
                T("最大放电量：", "Maximum PD: ") + F(record.MaxPdPc, "0.0") + " pC",
                T("振荡阻尼单周期衰减：", "One-cycle damping: ") + F(decay[0], "0.0") + " %"
            };
            if (BrowserPlot.Finite(decay[1])) lines.Add(T("8 周期衰减：", "Eight-cycle damping: ") + F(decay[1], "0.0") + " %");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
