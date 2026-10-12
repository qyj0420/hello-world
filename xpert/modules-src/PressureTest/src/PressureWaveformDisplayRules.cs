using System;
using System.Threading;

namespace HVX.Controls
{
    internal sealed partial class PressureWaveformView
    {
        // Drawing rules only. Never select a Pico range, alter calibration,
        // replace measured peaks, or decide whether a record may be saved.
        static readonly double[] AutomaticPdDisplayStepsPc =
            { 100, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000, 150000 };

        internal static double AutomaticPdDisplayMaximum(double requiredPc)
        {
            if (!IsFinite(requiredPc) || requiredPc < 0) return AutomaticPdDisplayStepsPc[0];
            foreach (double step in AutomaticPdDisplayStepsPc)
                if (requiredPc <= step) return step;
            // The operator has not specified a higher display step. Retain a
            // non-clipping scale rather than silently truncate at 150 nC.
            return requiredPc;
        }

        internal static double VoltageDisplayMaximum(double measuredAbsoluteKv)
        {
            if (!IsFinitePositive(measuredAbsoluteKv)) return 5;
            double required = measuredAbsoluteKv;
            if (required == Math.Floor(required))
            {
                double expanded = required * 1.2;
                if (IsFinite(expanded)) required = expanded;
            }
            return Math.Max(5, Math.Ceiling(required));
        }

        internal static double VoltageDisplayYRatio(double voltageKv, double maximumKv)
        {
            // Full plot [-2 Umax, +Umax]; numerical labels stop at -Umax.
            // Divide before subtraction to avoid overflowing finite extremes.
            return (1 - voltageKv / maximumKv) / 3;
        }

        internal static double[] VoltageDisplayLabels(double maximumKv, int steps)
        {
            if (!IsFinitePositive(maximumKv) || steps < 2 || steps % 2 != 0)
                throw new ArgumentOutOfRangeException("steps");
            var values = new double[steps + 1];
            for (int i = 0; i <= steps; i++) values[i] = (1 - 2 * i / (double)steps) * maximumKv;
            return values;
        }

        internal static double[] PrepareDisplayVoltage(double[] measured, CancellationToken cancel)
        {
            if (measured == null || measured.Length == 0) return new double[0];
            var display = (double[])measured.Clone();
            // Fixed three-sample low-pass for presentation, not a frequency
            // estimate or fitted waveform. Native samples/rates stay intact.
            for (int i = 1; i < measured.Length - 1; i++)
            {
                if ((i & 4095) == 0) cancel.ThrowIfCancellationRequested();
                display[i] = measured[i - 1] * .25 + measured[i] * .5 + measured[i + 1] * .25;
            }
            cancel.ThrowIfCancellationRequested();
            return display;
        }
    }
}
