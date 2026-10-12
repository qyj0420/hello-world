using System;

namespace Xiongxin.ZeroBench
{
    // Pico 5000 Series (A API), Programmer's Guide, section 3.6 Timebases.
    // https://www.picotech.com/download/manuals/ps5000abpg.en-1.pdf
    // Candidates are calculated here, then the live SDK must confirm the interval,
    // resolution and available memory before Arm. Never silently choose a slower rate.
    internal static class PicoCaptureConfiguration
    {
        internal static int DriverMode(int bits)
        {
            switch (bits) { case 8: return 0; case 12: return 1; case 14: return 2; case 15: return 3; case 16: return 4; }
            throw new ArgumentException("Pico 分辨率只支持 8、12、14、15、16 bit。");
        }

        internal static int HardwareBits(int bits, int rangeIndex)
        {
            DriverMode(bits);
            if (rangeIndex < 0 || rangeIndex >= BenchSettings.Ranges.Length) throw new ArgumentOutOfRangeException("rangeIndex");
            // 5000D specification footnote 2: 12-bit loses 1 bit at ±10 mV;
            // 14–16-bit loses 2 at ±10 mV, 1 at ±20 mV; 8-bit unchanged.
            if (bits == 12 && rangeIndex == 0) return 11;
            if (bits >= 14) return bits - (rangeIndex == 0 ? 2 : rangeIndex == 1 ? 1 : 0);
            return bits;
        }

        internal static bool RateMatches(double actual, double requested)
        {
            return !Double.IsNaN(actual) && !Double.IsInfinity(actual) && actual > 0 &&
                !Double.IsNaN(requested) && !Double.IsInfinity(requested) && requested > 0 &&
                Math.Abs(actual - requested) <= requested * 1e-9;
        }

        internal static void ValidateSdkTimebase(double requestedRateHz, double intervalNanoseconds, int availableSamples, int requestedSamples)
        {
            if (!(intervalNanoseconds > 0) || !RateMatches(1e9 / intervalNanoseconds, requestedRateHz) || availableSamples < requestedSamples)
                throw new InvalidOperationException("Pico SDK 回读时基/内存与请求不符：请求 " + requestedRateHz +
                    " S/s，间隔 " + intervalNanoseconds + " ns，可用 " + availableSamples + " 点。未静默降采样。");
        }

        internal static uint TimebaseFor(int bits, double rateHz)
        {
            DriverMode(bits);
            double maximum = bits == 8 ? 1e9 : bits == 12 ? 500e6 : bits == 16 ? 62.5e6 : 125e6;
            if (Double.IsNaN(rateHz) || Double.IsInfinity(rateHz) || rateHz < BenchSettings.ArtRate || rateHz > maximum)
                throw new ArgumentException(bits + " bit 单通道 A 段采集率须在 80 kS/s～" + (maximum / 1e6) + " MS/s 内。");
            double ns = 1e9 / rateHz;
            if (bits == 8 && RateMatches(rateHz, 1e9)) return 0;
            if ((bits == 8 || bits == 12) && RateMatches(rateHz, 500e6)) return 1;
            if ((bits == 8 || bits == 12) && RateMatches(rateHz, 250e6)) return 2;
            if (bits != 16 && RateMatches(rateHz, 125e6)) return 3;
            double step = bits == 12 || bits == 16 ? 16 : 8;
            uint candidate = checked((uint)Math.Round(ns / step, MidpointRounding.AwayFromZero) + (uint)(step == 16 ? 3 : 2));
            double actualNs = (candidate - (step == 16 ? 3 : 2)) * step;
            if (actualNs <= 0 || !RateMatches(1e9 / actualNs, rateHz))
                throw new ArgumentException("请求的 Pico 采样率不对应硬件时基；不会自动取整。请选 125、62.5、31.25、25、12.5 MS/s 等与当前位数兼容的值。");
            return candidate;
        }
    }
}
