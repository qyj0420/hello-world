namespace Xiongxin.FactoryTest
{
    internal static class FactoryStartConfirmation
    {
        // Called only for the existing start dialog's explicit OK. Never store
        // these assertions in the form defaults or reuse them for another run.
        internal static FactorySettings Accept(FactorySettings pending, FactoryRunKind kind, bool accepted)
        {
            if (!accepted) return null;
            var round = pending.Clone();
            round.BatchPermit = null;
            round.BatchContinuation = false;
            round.BatchStartConfirmed = round.AutomaticBatch;
            if (!round.Simulation)
            {
                round.FeedbackWiringConfirmed = kind == FactoryRunKind.Calibration || round.TargetKv > 0 || round.ZeroSynchronizedOptical;
                round.IndependentDischargeConfirmed = kind == FactoryRunKind.Pressure && (round.UsePostHvVmonStartup || round.SkipPressureResidualMeasurement);
            }
            round.Validate(kind);
            return round;
        }
    }
}
