using System;

namespace Xiongxin.ZeroBench
{
    internal static class WaveScaleTests
    {
        internal static string Run()
        {
            double minimum, maximum;
            WavePanel.CalculateBounds(-.024, .044, 1, out minimum, out maximum);
            Require(minimum == -1 && maximum == 1, "near-zero kV data uses at least +/- 1 kV");
            WavePanel.CalculateBounds(0, 0, 1, out minimum, out maximum);
            Require(minimum == -1 && maximum == 1, "zero data retains meaningful engineering scale");
            WavePanel.CalculateBounds(-.01, 2.5, 1, out minimum, out maximum);
            Require(minimum == -maximum && maximum > 2.5, "positive excursion expands axis, never clipped");
            WavePanel.CalculateBounds(-4.8, .01, 1, out minimum, out maximum);
            Require(minimum == -maximum && minimum < -4.8, "negative excursion expands axis, never clipped");
            WavePanel.CalculateBounds(-.024, .044, 0, out minimum, out maximum);
            double originalPad = Math.Max((.044 - (-.024)) * .12, Math.Max(Math.Abs(.044), .001) * .02);
            Require(minimum == -.024 - originalPad && maximum == .044 + originalPad, "default retains original adaptive scale");
            WavePanel.CalculateBounds(0, 0, 0, out minimum, out maximum);
            Require(minimum == -.00002 && maximum == .00002, "default flat trace remains nondegenerate");
            RequireRejected(Double.NaN, 1, 1, "NaN minimum");
            RequireRejected(0, Double.NaN, 1, "NaN maximum");
            RequireRejected(0, 1, Double.NaN, "NaN scale floor");
            RequireRejected(Double.NegativeInfinity, 1, 1, "infinite data");
            RequireRejected(0, 1, Double.PositiveInfinity, "infinite scale floor");
            RequireRejected(1, 0, 1, "reversed bounds");
            RequireRejected(0, 1, -1, "negative scale floor");
            RequireRejected(-Double.MaxValue, Double.MaxValue, 1, "overflowed display span");
            return "WAVE_SCALE_TESTS=PASS: display-only +/- 1 kV floor, real positive/negative excursions preserved, default scale unchanged, invalid numeric bounds rejected; no hardware accessed.";
        }

        private static void Require(bool condition, string name)
        { if (!condition) throw new Exception("Wave scale test failed: " + name); }

        private static void RequireRejected(double lower, double upper, double floor, string name)
        {
            bool rejected = false;
            try { double minimum, maximum; WavePanel.CalculateBounds(lower, upper, floor, out minimum, out maximum); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, name);
        }
    }
}
