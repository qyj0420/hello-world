using System;
using System.Collections.Generic;
using System.Linq;

namespace Xiongxin.FactoryTest
{
    // Managed-only command-envelope regressions; never constructs a native driver.
    internal static class FactoryAoEnvelopeTests
    {
        internal static string Run()
        {
            int n = 0;
            Action<bool,string> check = (ok,why) => { if (!ok) throw new Exception("AO +5%: " + why); n++; };
            var s = new FactorySettings { TargetKv = 15, PressureVPerV = 15000, MaximumAoV = 5 };
            check(s.NominalAoV == 1 && s.AoCommandCeilingV == 1.05, "user example: 15kV / 15000 gives 1V nominal, 1.05V limit");
            s.ValidateAoCommand(1.05);
            check(Rejects(() => s.ValidateAoCommand(1.050001)), "reject even a small command beyond 5 percent");
            foreach (double invalid in new[] { -0.001, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
                check(Rejects(() => s.ValidateAoCommand(invalid)), "invalid AO rejected: " + invalid);
            s.MaximumAoV = 1.02;
            check(s.AoCommandCeilingV == 1.02 && Rejects(() => s.ValidateAoCommand(1.03)), "configured smaller AO maximum wins");
            s.TargetKv = 30; s.PressureVPerV = 6000; s.MaximumAoV = 5; s.MaximumKv = s.MaximumFeedbackKv = 30;
            check(s.AoCommandCeilingV == 5 && Rejects(() => s.ValidateAoCommand(5.000001)), "5V ceiling wins over theoretical 5.25V");
            check(s.RampUpperV == 30000 && s.CurrentLimitMa == 10 && s.TargetTolerancePercent == 2, "voltage ceiling, current limit and reach tolerance not relaxed");
            s.TargetKv = 0;
            s.ValidateAoCommand(0);
            check(s.AoCommandCeilingV == 0 && Rejects(() => s.ValidateAoCommand(.0001)), "zero target cannot acquire positive AO headroom");
            s.TargetKv = 15; s.PressureVPerV = 15000;
            check(s.Clone().AoCommandCeilingV == 1.05, "settings clone retains calculation inputs");
            s.PressureVPerV = Double.NaN;
            check(Rejects(() => s.ValidateAoCommand(.1)), "nonfinite ceiling fails closed");

            foreach (double configuredMax in new[] { 5.0, 1.02 })
            {
                var f = Fixture(); f.Settings.MaximumAoV = configuredMax; f.Settings.Input.AoStepV = .1;
                f.Backend.FeedbackRule = (b,x) => { x.VmonRawV = x.DividerRawV = 0; return x; };
                var result = new FactoryResult { Settings = f.Settings }; var messages = new List<string>();
                Exception error = Direct(f,result,(k,t) => { if (k == "AO_ENVELOPE") messages.Add(t); });
                check(error is TimeoutException, "low feedback reaches timeout instead of exceeding AO ceiling");
                check(Math.Abs(f.Backend.MaximumAo - Math.Min(1.05,configuredMax)) < 1e-12, "last fixed increment is clipped at exact ceiling");
                check(result.Ramp.All(x => x.AoV <= f.Settings.AoCommandCeilingV), "every recorded command respects ceiling");
                check(messages.Count == 1 && messages[0].Contains("1.05"), "nominal/limit rule recorded at ramp start");
                check(f.Backend.Ao == 0 && !f.Backend.Hv && f.Backend.OpticalHighCount == 0, "timeout still shuts down without optical");
            }
            {
                var f = Fixture(); f.Settings.RampTimeoutSeconds = 1;
                f.Backend.FeedbackRule = (b,x) => { x.DividerRawV *= .95; return x; };
                var result = new FactoryResult { Settings = f.Settings };
                check(Direct(f,result,(k,t) => {}) == null && result.RampStatistics.ExitCode == "Reached", "allowed headroom can accommodate lower divider response");
                check(f.Backend.MaximumAo > 1 && f.Backend.MaximumAo <= 1.05, "headroom is available but never exceeded");
            }
            {
                var f = Fixture(); f.Settings.RampTimeoutSeconds = 1;
                var result = new FactoryResult { Settings = f.Settings };
                check(Direct(f,result,(k,t) => {}) == null && f.Backend.MaximumAo <= 1, "normal first reach stops without consuming headroom");
            }
            {
                var f = Fixture(); f.Settings.TargetKv = .5; f.Settings.PressureVPerV = 6000; f.Settings.RampTimeoutSeconds = 1;
                var result = new FactoryResult { Settings = f.Settings };
                check(Direct(f,result,(k,t) => {}) == null && Math.Abs(f.Backend.MaximumAo - .5*1000/6000) < 1e-12,
                    "coarse final increment lands at nominal first instead of jumping to +5 percent outside target tolerance");
            }
            {
                var f = Fixture(); f.Settings.RampTimeoutSeconds = 1;
                f.Backend.FeedbackRule = (b,x) => { if (b.Hv && b.Ao > 0) x.ImonRawV = 4; return x; };
                var result = new FactoryResult { Settings = f.Settings };
                var error = Direct(f,result,(k,t) => {});
                check(error != null && error.Message.Contains("IMON") && f.Backend.PositiveAoWrites == 1, "10.4mA raw overcurrent still stops immediately");
            }
            {
                var f = Fixture(); f.Settings.RampTimeoutSeconds = 1;
                using (var engine = new FactoryManualEngine(f.Backend,on => {},f.Clock,(k,t) => {},f.Cancellation.Token))
                {
                    engine.Initialize(f.Settings); engine.HvOn(1);
                    f.Backend.FeedbackRule = (b,x) => { x.DividerRawV *= .95; return x; };
                    engine.HvUp(1);
                    check(engine.Result.RampStatistics.ExitCode == "Reached" && f.Backend.MaximumAo > 1 && f.Backend.MaximumAo <= 1.05,
                        "manual ramp uses selected 1kV target, not its 5kV initialization target");
                }
            }
            return "AO +5 percent envelope: " + n + " offline checks PASS; HardwareAccessed=False";
        }
        private static FactoryTests.Fixture Fixture()
        {
            var f = new FactoryTests.Fixture();
            f.Settings.TargetKv = 1; f.Settings.PressureVPerV = 1000; f.Settings.MaximumAoV = 5;
            f.Settings.DividerOnlyFeedback = true; f.Settings.RampTimeoutSeconds = .05;
            return f;
        }
        private static Exception Direct(FactoryTests.Fixture f, FactoryResult result, Action<string,string> report)
        {
            try
            {
                f.Settings.Validate(FactoryRunKind.Pressure); f.Backend.Initialize(f.Settings,false); f.Backend.SetHv(true);
                FactoryRunner.Ramp(f.Settings,f.Backend,f.Clock,result,report,f.Cancellation.Token); return null;
            }
            catch (Exception ex) { return ex; }
            finally { f.Backend.Shutdown(); f.Backend.Dispose(); }
        }
        private static bool Rejects(Action action)
        { try { action(); return false; } catch (ArgumentOutOfRangeException) { return true; } }
    }
}
