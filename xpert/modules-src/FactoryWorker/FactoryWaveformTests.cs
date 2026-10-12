using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryWaveformTests
    {
        internal static string Run()
        {
            int passed = 0;
            var source = new short[7500000]; source[0] = -1234; source[1234567] = 32000; source[7499999] = -32760;
            var envelope = new FactorySampleEnvelope(source, .25);
            int lo, hi; bool missing;
            envelope.Query(0, source.Length, out lo, out hi, out missing);
            Require(lo == 7499999 && hi == 1234567 && !missing, "整段 ADC 中的单点正负峰值均必须保留"); passed++;
            envelope.Query(1234500, 1234600, out lo, out hi, out missing);
            Require(hi == 1234567 && envelope.Value(hi) == 8000, "放大范围中的窄峰不能被抽稀漏掉"); passed++;
            envelope.Query(7499999, 7500000, out lo, out hi, out missing);
            Require(lo == 7499999 && hi == 7499999, "最后一个实际采样点必须能查询"); passed++;
            Require(source[1234567] == 32000 && source[7499999] == -32760, "显示不能改变原始 ADC"); passed++;
            double[] pulseVolts = { -.02, 0, .05 };
            var calibratedPulse = new FactorySampleEnvelope(pulseVolts, 2100);
            calibratedPulse.Query(0, pulseVolts.Length, out lo, out hi, out missing);
            Require(!missing && calibratedPulse.Value(lo) == -42 && calibratedPulse.Value(hi) == 105, "脉冲图按采集时系数换算并保留正负极性"); passed++;
            Require(pulseVolts[0] == -.02 && pulseVolts[2] == .05, "脉冲显示换算不得覆盖原始电压"); passed++;

            double[] withGap = { -1, double.NaN, 2, double.PositiveInfinity, 7, -9 };
            var gaps = new FactorySampleEnvelope(withGap); gaps.Query(0, withGap.Length, out lo, out hi, out missing);
            Require(missing && lo == 5 && hi == 4, "无效样点须标出缺口，不能作为有效极值"); passed++;

            var random = new Random(1837); var varied = new double[10273];
            for (int i = 0; i < varied.Length; i++) varied[i] = random.NextDouble() * 200 - 100;
            var varying = new FactorySampleEnvelope(varied);
            for (int trial = 0; trial < 250; trial++)
            {
                int first = random.Next(varied.Length), end = random.Next(first + 1, varied.Length + 1);
                varying.Query(first, end, out lo, out hi, out missing);
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                for (int i = first; i < end; i++) { min = Math.Min(min, varied[i]); max = Math.Max(max, varied[i]); }
                Require(!missing && varied[lo] == min && varied[hi] == max, "跨索引块的缩放范围必须返回真实极值");
            }
            passed++;
            Require(FactoryWaveformControl.VoltageFraction(0, 1) == .35 &&
                Math.Abs(FactoryWaveformControl.VoltageFraction(1, 1) - .1) < 1e-12 &&
                Math.Abs(FactoryWaveformControl.VoltageFraction(-1, 1) - .6) < 1e-12,
                "组合图电压刻度与数据浏览的投影一致"); passed++;

            using (var control = new FactoryWaveformControl { Size = new Size(1000, 450) })
            {
                control.SetRaw(source, 125000000, 20, 32767, 2100, 60);
                Require(control.RawMode && control.SourceSampleCount == 7500000 && control.VisibleStartMs == 0 && control.VisibleEndMs == 60,
                    "原始局放默认显示完整 60 ms，不能截为 20 ms"); passed++;
                control.SetVisibleTime(12, 18); Require(control.VisibleStartMs == 12 && control.VisibleEndMs == 18, "缩放改变的只是显示范围"); passed++;
                control.ResetView(); Require(control.VisibleStartMs == 0 && control.VisibleEndMs == 60, "复位恢复整段"); passed++;
                var v = new double[4800]; var q = new double[4800]; v[600] = 5; q[600] = 800;
                control.VoltageReferenceKv = .0001; control.SetCombined(v, q, 80000, 60);
                Require(!control.RawMode && control.VoltageReferenceKv == 1 && control.SourceSampleCount == 4800,
                    "组合图为 80 kS/s，电压参考尺度至少 1 kV"); passed++;
                control.ClearWave(); Require(!control.HasWaveform, "清空后不保留上一轮波形"); passed++;
                control.SetPulse(pulseVolts, 125000000, 2100);
                Require(control.RawMode && control.SourceSampleCount == 3 && Math.Abs(control.VisibleEndMs - .000024) < 1e-12,
                    "选中脉冲使用原始实际采样率与完整独立窗口，不重采样"); passed++;
                control.SetVisibleTime(.000008, .000016); control.ResetView();
                Require(control.VisibleStartMs == 0 && Math.Abs(control.VisibleEndMs - .000024) < 1e-12 && pulseVolts[2] == .05,
                    "脉冲缩放/复位仅改变视窗，不影响数据"); passed++;
            }
            using (var control = new FactoryWaveformControl { Size = new Size(720, 175) })
            {
                var volts = new double[4800]; var charge = new double[4800];
                for (int i = 0; i < volts.Length; i++) volts[i] = 4 * Math.Cos(i * 2 * Math.PI / 800);
                charge[600] = 800; charge[2700] = 1000; charge[4500] = 600;
                control.SetCombined(volts, charge, 80000, 60);
                control.CreateControl();
                foreach (int canvasHeight in new[] { 105, 120, 140 })
                {
                    control.Height = canvasHeight + 35; control.PerformLayout();
                    int renders = control.StaticRenderCount;
                    using (var bitmap = control.Snapshot())
                    {
                        Rectangle plot = control.PlotBounds;
                        Require(plot.Height >= 40 && plot.Bottom + control.Font.Height < control.Height,
                            "紧凑图须为数据和时间刻度保留独立空间");
                        int pdPixels = CountColor(bitmap, plot, FactoryWaveformControl.PdColor), voltagePixels = CountColor(bitmap, plot, FactoryWaveformControl.VoltageColor);
                        Require(control.StaticRenderCount > renders && pdPixels > 20 && voltagePixels > 20,
                            "105/120/140 px 画布须绘制检波局放及电压，不能提前返回白图：height=" + canvasHeight + " plot=" + plot +
                            " render=" + renders + "→" + control.StaticRenderCount + " pd=" + pdPixels + " voltage=" + voltagePixels);
                        Require(CountColor(bitmap, new Rectangle(0, plot.Top, plot.Left - 5, plot.Height), FactoryWaveformControl.InkColor) > 4 &&
                            CountColor(bitmap, new Rectangle(plot.Right + 6, plot.Top, bitmap.Width - plot.Right - 6, plot.Height), FactoryWaveformControl.VoltageColor) > 4,
                            "紧凑双轴图仍须显示独立的 pC 与 kV 刻度");
                    }
                    Require(control.SourceSampleCount == 4800 && control.VisibleStartMs == 0 && control.VisibleEndMs == 60,
                        "紧凑布局不能改动完整数据或时间范围");
                    passed++;
                }
                control.SetRaw(source, 125000000, 20, 32767, 2100, 60);
                foreach (int canvasHeight in new[] { 105, 120, 140 })
                {
                    control.Height = canvasHeight + 35; control.PerformLayout();
                    int renders = control.StaticRenderCount;
                    using (var bitmap = control.Snapshot())
                    {
                        int pdPixels = CountColor(bitmap, control.PlotBounds, FactoryWaveformControl.PdColor);
                        Require(control.StaticRenderCount > renders && pdPixels > 40,
                            "紧凑原始局放图必须绘出峰值与有符号轨迹：height=" + canvasHeight + " render=" + renders + "→" + control.StaticRenderCount + " pd=" + pdPixels);
                    }
                    Require(control.SourceSampleCount == 7500000 && control.VisibleStartMs == 0 && control.VisibleEndMs == 60 && source[7499999] == -32760,
                        "紧凑原始图仍显示整段，不得裁去尾部样点或修改 ADC");
                    passed++;
                }
                control.SetVisibleTime(9, 10); control.Height = 140; control.PerformLayout();
                using (var bitmap = control.Snapshot()) Require(CountColor(bitmap, control.PlotBounds, FactoryWaveformControl.PdColor) > 10,
                    "紧凑原始图也能显示缩放窗口中的窄峰");
                Require(control.VisibleStartMs == 9 && control.VisibleEndMs == 10, "控件缩小不能复位用户的视窗"); passed++;
            }
            passed+=FactoryWaveformInteractionTests.Run();
            return "Factory waveform: " + passed + " checks passed (full-segment extrema, signed ADC, dual-axis convention, box zoom, charge ruler, independent ROI, view-only projection).";
        }

        private static int CountColor(Bitmap bitmap, Rectangle area, Color wanted)
        {
            area.Intersect(new Rectangle(Point.Empty, bitmap.Size)); int count = 0;
            for (int y = area.Top; y < area.Bottom; y++) for (int x = area.Left; x < area.Right; x++)
            {
                Color actual = bitmap.GetPixel(x, y);
                // GDI+ antialiasing blends thin traces with white. Recognize
                // their distinct green/cyan hues without counting pale grids.
                bool match = wanted == FactoryWaveformControl.PdColor ? actual.G - actual.R >= 15 && actual.G - actual.B >= 8 :
                    wanted == FactoryWaveformControl.VoltageColor ? actual.G - actual.R >= 15 && actual.B - actual.G >= 8 :
                    Math.Abs(actual.R - wanted.R) <= 35 && Math.Abs(actual.G - wanted.G) <= 35 && Math.Abs(actual.B - wanted.B) <= 35;
                if (match) count++;
            }
            return count;
        }

        private static void Require(bool condition, string text)
        { if (!condition) throw new InvalidOperationException("波形验证失败：" + text); }
    }
}
