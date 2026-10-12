using System;

namespace Xiongxin.FactoryTest
{
    // Step scheduler adapted from task39 precision-ramp-1.0.30.11/source-freeze/
    // FactoryPrecisionRamp.cs SHA256 9D10F71BA499A52C6838CD5C9E0B5E16C9F90E7E1AE41A2C7417391EAD962AFF.
    // Also checked against user-accepted task39 thresholds-1.0.30.13/source-freeze
    // helper SHA256 9CF2934AAD628A2DCA7C364EBC146E3F6658D092D5A61DF5EFBE96110E23C4B5:
    // NextAo/FinishCycle unchanged; only its excluded CheckProtection text differs.
    // This does not import the 1.0.30.13 tolerance/overvoltage policy.
    // Local differences: configurable threshold; disabled by default; no imported
    // CheckProtection, tolerance/precision warning or continuation branches;
    // additionally cap increments by the existing Input.AoStepV. This is a new,
    // separately tested opt-in control curve, not part of accepted 1.0.30.9.
    internal sealed class UnifiedFinePressureRamp
    {
        internal const double PlannedSeconds = 3;
        readonly FactorySettings settings;
        readonly FactoryRampStatistics statistics;
        readonly int period;
        long nextDeadline, lastCommandAt;
        internal UnifiedFinePressureRamp(FactorySettings value, FactoryRampStatistics stats, long start)
        {
            settings=value; statistics=stats;
            period=Math.Max(value.Input.LoopDelayMs,value.FeedbackWindowMs);
            nextDeadline=start+period; lastCommandAt=start;
            stats.FineRampApplied=true; stats.FineRampPeriodMs=period; stats.FineRampPlannedSeconds=PlannedSeconds;
        }
        // Called only after the runner's fresh feedback and unchanged guards.
        internal double NextAo(double ao, double measuredV, long now)
        {
            long elapsed=Math.Max(0,now-lastCommandAt);
            double creditedMs=Math.Min(elapsed,2.0*period);
            double step=settings.NominalAoV*creditedMs/(PlannedSeconds*1000);
            step=Math.Min(Math.Min(.05,settings.Input.AoStepV),step);
            double remaining=Math.Max(0,settings.TargetKv*1000-measuredV);
            step=Math.Min(step,remaining/settings.PressureVPerV*.25);
            double ceiling=ao<settings.NominalAoV ? Math.Min(settings.AoCommandCeilingV,settings.NominalAoV) : settings.AoCommandCeilingV;
            double next=Math.Min(ceiling,ao+step);
            settings.ValidateAoCommand(next);
            statistics.FineRampMaximumIncrementV=Math.Max(statistics.FineRampMaximumIncrementV,next-ao);
            lastCommandAt=now;
            return next;
        }
        internal int FinishCycle(long now)
        {
            if(now>nextDeadline) {
                long missed=(now-nextDeadline+period-1)/period;
                statistics.FineRampSkippedPeriods=checked(statistics.FineRampSkippedPeriods+(int)missed);
                nextDeadline+=missed*period;
            }
            int wait=checked((int)Math.Max(0,nextDeadline-now));
            nextDeadline+=period;
            return wait;
        }
    }
}
