using System;
using System.IO;

namespace HVX.Controls
{
    // Storage policy only: does not change acquisition, filtering or the source buffer.
    internal static class ZeroVoltageLocationWindow
    {
        internal const double MaximumSeconds = .020;

        internal static int SampleCount(int available, double rateHz)
        {
            if (available < 1 || Double.IsNaN(rateHz) || Double.IsInfinity(rateHz) || rateHz <= 0)
                throw new InvalidDataException("Invalid zero-voltage location sampling clock or buffer.");
            double budget = Math.Floor(rateHz * MaximumSeconds);
            if (budget < 1) throw new InvalidDataException("Sampling clock cannot represent the zero-voltage location window.");
            // Compare before casting: an unusually large finite rate must not overflow an int.
            return budget >= available ? available : (int)budget;
        }
    }
}
