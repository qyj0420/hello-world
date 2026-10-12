using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPostHvStartupTests
    {
        internal static string Run()
        {
            int n = 0;
            Action<bool,string> check = (ok, why) => { if (!ok) throw new Exception("R22: " + why); n++; };
            {
                var f = Setup(); int readsBeforeOn = 0, readsAtZero = 0;
                f.Backend.FeedbackRule = (b,x) => {
                    if (b.HvHighCount == 0) readsBeforeOn++;
                    if (b.Hv && b.Ao == 0) { readsAtZero++; x.VmonRawV = 200 / 6200.0; x.DividerRawV = -500 / 11000.0; }
                    return x;
                };
                var stages = new List<string>(); var r = f.Run((k,v) => stages.Add(k));
                check(r.Completed && r.ResidualSafe, "VMON200 V / AI4-500 V startup passes without using AI4 residual50");
                check(readsBeforeOn == 0 && readsAtZero > 0, "no AI feedback before HV ON; samples at AO zero after ON");
                check(!stages.Contains("INITIAL_FEEDBACK") && stages.Contains("HV_ZERO_FEEDBACK"), "correct measurement stage");
                check(stages.IndexOf("STEP_SAFE_PASSED") < stages.IndexOf("STEP_HV_ON_RUNNING") &&
                    stages.IndexOf("HV_ZERO_FEEDBACK") < stages.IndexOf("STEP_HV_UP_RUNNING"), "permit then HV ON then VMON then ramp");
                check(f.Backend.OpticalHighCount == 1 && r.RampStatistics.ExitCode == "Reached", "normal single shared-trigger sequence");
                check(f.Backend.MaximumAo <= f.Settings.AoCommandCeilingV, "AO 105 percent envelope unchanged");
                check(r.Settings.StartupVmonLimitV == 300 && r.Settings.SafeResidualV == 50, "distinct persisted thresholds");
            }
            foreach (double v in new[] { 299.0, 300.0, 301.0, -301.0 })
            {
                var f = Setup(); f.Backend.FeedbackRule = (b,x) => { if (b.Hv && b.Ao == 0) x.VmonRawV = v / 6200; return x; };
                if (Math.Abs(v) <= 300) check(f.Run().Completed, "VMON boundary allowed " + v);
                else
                {
                    var e = Failure(() => f.Run());
                    check(e.ToString().Contains("VMON批次绝对上界"), "VMON boundary fault " + v);
                    check(f.Backend.HvHighCount == 1 && f.Backend.PositiveAoWrites == 0 && f.Backend.OpticalHighCount == 0, "no ramp/light after VMON startup fault");
                    check(e.PartialResult.FaultResidual.Started && !e.PartialResult.Completed, "post-HV fault residual retained");
                }
            }
            foreach (int mode in new[] { 0, 1, 2, 3 })
            {
                var f = Setup(); f.Backend.FeedbackRule = (b,x) => {
                    if (b.Hv && b.Ao == 0)
                    {
                        if (mode == 0) x.ImonRawV = 10.01 / 2.6;
                        if (mode == 1) x.DividerRawV = 30001.0 / 11000;
                        if (mode == 2) x.VmonRawV = Double.NaN;
                        if (mode == 3) { x.VmonRawV = 299 / 6200.0; x.VmonHalfCodeV = 2 / 6200.0; }
                    }
                    return x;
                };
                var e = Failure(() => f.Run());
                check(e != null && f.Backend.PositiveAoWrites == 0 && f.Backend.OpticalHighCount == 0, "raw protection retained " + mode);
                check(e.PartialResult.FaultResidual.Started && f.Backend.DisposeCount == 1, "fault cleanup retained " + mode);
            }
            {
                var f = Setup(); int reads = 0;
                f.Backend.EmergencyRule = b => false;
                f.Backend.FeedbackRule = (b,x) => { reads++; return x; };
                Failure(() => f.Run());
                check(reads == 0 && f.Backend.HvHighCount == 0, "P04 low prevents ON without measuring AI");
                check(f.Backend.OpticalHighCount == 0 && f.Backend.DisposeCount == 1, "P04 failure cleanup no light");
            }
            {
                var f = Setup(); f.Settings.Simulation = false; f.Settings.FeedbackWiringConfirmed = true;
                Exception error = null; try { f.Settings.Validate(FactoryRunKind.Pressure); } catch (ArgumentException ex) { error = ex; }
                check(error != null && error.Message.Contains("独立确认"), "real startup requires external discharge confirmation");
                f.Settings.IndependentDischargeConfirmed = true; f.Settings.Validate(FactoryRunKind.Pressure);
                check(f.Backend.InitializeCount == 0, "confirmation validation invokes no hardware or fake initialization");
                var copy = f.Settings.Clone();
                check(copy.UsePostHvVmonStartup && copy.StartupVmonLimitV == 300 && copy.IndependentDischargeConfirmed, "settings roundtrip");
                copy.TargetKv = 0; check(!copy.UsePostHvVmonStartup, "zero mode not changed by automatic startup policy");
                var legacy = (FactorySettings)new XmlSerializer(typeof(FactorySettings)).Deserialize(new StringReader("<FactorySettings><TargetKv>5</TargetKv></FactorySettings>"));
                check(!legacy.UsePostHvVmonStartup && legacy.InitialResidualV == 0, "old files retain old startup behavior");
            }
            {
                var f = Setup(); int preReads = 0;
                f.Backend.FeedbackRule = (b,x) => { if (b.HvHighCount == 0) preReads++; return x; };
                var r = f.Run(null, FactoryRunKind.Calibration);
                check(preReads > 0 && !r.Settings.UsePostHvVmonStartup, "calibration does not inherit startup bypass");
                check(f.Backend.HvHighCount == 0 && f.Backend.OpticalHighCount == 0, "calibration never activates HV/light");
            }
            {
                var f = Setup(); var stages = new List<string>(); bool offAtError = false;
                f.Backend.FeedbackRule = (b,x) => { if (b.Hv && b.Ao > 0) x.ImonRawV = 5; return x; };
                var e = Failure(() => f.Run((k,v) => {
                    stages.Add(k);
                    if (k == "STEP_HV_UP_ERROR") offAtError = !f.Backend.Hv && f.Backend.Ao == 0;
                }));
                check(offAtError, "outputs disabled before original ramp error reported");
                check(stages.IndexOf("STEP_HV_UP_ERROR") >= 0 && stages.IndexOf("STEP_HV_UP_ERROR") < stages.IndexOf("STEP_DISCHARGE_RUNNING"), "ramp error shown before residual observation");
                check(stages.IndexOf("STEP_HV_OFF_PASSED") < stages.IndexOf("STEP_DISCHARGE_RUNNING"), "fault OFF return shown separately");
                check(e.PartialResult.FaultResidual.Started && f.Backend.OpticalHighCount == 0, "read-only observation not automatic light");
            }
            {
                var f = Setup(); f.Backend.FailArm = true;
                var e = Failure(() => f.Run((k,v) => { if (k == "STEP_ARM_ERROR") throw new IOException("UI report unavailable"); }));
                check(e.PartialResult.FaultResidual.Started && f.Backend.DisposeCount == 1, "error reporter failure cannot skip observation/disposal");
                check(!f.Backend.Hv && f.Backend.Ao == 0 && !f.Backend.Optical, "outputs remain off after report failure");
            }
            return "R22 post-HV VMON startup / fault UI order: " + n + " offline checks PASS; HardwareAccessed=False";
        }
        private static FactoryTests.Fixture Setup()
        {
            var f = new FactoryTests.Fixture();
            f.Settings.VmonAfterHvStartup = true; f.Settings.DividerOnlyFeedback = true;
            f.Settings.MaximumKv = f.Settings.MaximumFeedbackKv = 30;
            f.Settings.DischargeTimeoutSeconds = .12;
            return f;
        }
        private static FactoryRunException Failure(Action action)
        {
            try { action(); } catch (FactoryRunException e) { return e; }
            throw new Exception("Expected failed factory round");
        }
    }
}
