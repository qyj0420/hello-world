using System;

namespace HVX.CalibrationDisplay
{
    // A view contract only: seconds in/out, no resampling, padding or sample edits.
    internal static class CalibrationPulseDisplayWindow
    {
        internal static double[] Calculate(double dataStart, double dataEnd, double dx,
            double length, double velocity, double incident, double reflection)
        {
            if (!Finite(dataStart) || !Finite(dataEnd) || dataEnd <= dataStart || !Finite(dx) || dx <= 0)
                throw new ArgumentException("Invalid sampled range.");
            if (!Finite(length) || !Finite(velocity) || length <= 0 || velocity <= 0 ||
                !Finite(incident) || incident < dataStart || incident > dataEnd)
                return new[] { dataStart, dataEnd, Double.PositiveInfinity, 1d };
            double expected = 2 * length / velocity * 1e-6;
            bool measured = Finite(reflection) && reflection > incident && reflection <= dataEnd;
            double travel = measured ? reflection - incident : expected;
            double span = 7 * travel / 3;
            if (!Finite(span) || span <= 0) return new[] { dataStart, dataEnd, Double.PositiveInfinity, 1d };
            // Incident is at 1/10 of the desired view. The first round trip is
            // 3/7 of it; the second return remains visible with 0.1*travel tail.
            double start = incident - span / 10, end = incident + span * .9;
            double status = span < dx ? 4d : measured && travel > 5 * expected ? 2d :
                incident + 2 * travel > dataEnd ? 3d : start < dataStart ? 5d : end > dataEnd ? 6d : 0d;
            start = Math.Max(dataStart, start); end = Math.Min(dataEnd, end);
            // Missing pre-trigger/tail evidence is explicit; never shift/stretch
            // the observed waveform to make the desired proportions appear met.
            return new[] { start, end, span, status };
        }
        static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
    }
}
