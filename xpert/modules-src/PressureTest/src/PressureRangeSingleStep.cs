using System;

namespace HVX.Controls
{
    // Pure range-index policy. No acquisition, data acceptance or HV permission.
    internal static class PressureRangeSingleStep
    {
        internal static int Next(int current,int recommended,int count)
        {
            return HVX.Acquisition.AdjacentHardwareRangePolicy.Next(current,recommended,count);
        }
    }
}
