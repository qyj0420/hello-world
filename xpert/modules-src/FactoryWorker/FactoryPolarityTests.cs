using System;
using System.Linq;

namespace Xiongxin.FactoryTest
{
    // Synthetic backends and saved numerical examples only. No driver/port access.
    internal static class FactoryPolarityTests
    {
        internal static string Run()
        {
            int count = 0;
            Action<bool,string> check = (ok,reason) => { if (!ok) throw new Exception("R11 polarity: " + reason); count++; };
            foreach (double polarity in new[] { 1.0,-1.0 })
            {
                var f = new FactoryTests.Fixture(); f.Settings.TargetKv = 1;
                f.Backend.FeedbackRule = (b,v) => { v.DividerRawV *= polarity; return v; };
                var r = f.Run();
                check(r.Completed && r.Ramp.Count >= 5 && f.Backend.OpticalHighCount == 1,"both divider polarities complete same automatic sequence");
                check(r.Ramp.Where(v => v.AoV > 0).All(v => Math.Sign(v.DividerRawV) == polarity),"raw recorded polarity preserved");
                check(r.Ramp.Last().VmonRawV > 0 && Math.Abs(r.Ramp.Last().DividerRawV) * f.Settings.Input.DividerVPerV >= 980,
                    "magnitude target confirmation with positive VMON");
                check(f.Backend.MaximumAo <= f.Settings.AoCommandCeilingV && !f.Backend.Hv && f.Backend.Ao == 0,"AO ceiling and final shutdown enforced");
            }
            var manual = new FactoryTests.Fixture();
            manual.Backend.FeedbackRule = (b,v) => { v.DividerRawV = -v.DividerRawV; return v; };
            using (var e = new FactoryManualEngine(manual.Backend,high => { },manual.Clock,(key,text) => { },manual.Cancellation.Token))
            {
                e.Initialize(manual.Settings); e.HvOn(1); e.HvUp(1);
                check(manual.Backend.Hv && manual.Backend.OpticalHighCount == 0 && e.Result.Ramp.Last().DividerRawV < 0,
                    "manual negative HV reaches target and holds for explicit OFF, without auto trigger");
                e.HvOff(); e.TriggerCapture();
                check(e.Result.Capture != null && !e.Result.Completed && !e.Result.ResidualSafe,"manual negative HV capture does not claim complete safety acceptance");
            }
            var s = new FactorySettings { TargetKv = 5, MaximumKv = 5, SafeResidualV = 500 };
            var replay = new FactoryFeedback { TimestampUtc = DateTime.UtcNow, EmergencyReleased = true, AoV = .1,
                VmonRawV = .03451931368339254, DividerRawV = -.05126613581436687,
                SampleCount = 33, VmonMinimumRawV = -.01873459836182895, VmonMaximumRawV = .0897455928414,
                DividerMinimumRawV = -.05126613581436687, DividerMaximumRawV = .021711447358714431 };
            FactoryRunner.CheckRampVoltage(replay,s,5000);
            check(replay.DividerRawV < 0,"saved -563.93 V AI4 example no longer misclassified as wrong HV polarity");
            check(!FactoryRunner.ResidualOk(replay,s),"same charged sample still fails residual test; magnitude is not discharge clearance");
            foreach (double sign in new[] { -1.0,1.0 })
            {
                var f = new FactoryTests.Fixture(); f.Settings.TargetKv = 1; f.Settings.MaximumFeedbackKv = 1.02;
                f.Backend.FeedbackRule = (b,v) =>
                {
                    v.DividerRawV *= sign;
                    if (b.Hv && b.Ao > 0)
                    {
                        v.SampleCount = 2;
                        v.VmonMinimumRawV = v.VmonMaximumRawV = v.VmonRawV;
                        v.DividerMinimumRawV = Math.Min(v.DividerRawV,sign * .2);
                        v.DividerMaximumRawV = Math.Max(v.DividerRawV,sign * .2);
                    }
                    return v;
                };
                var ex = Expect(() => f.Run());
                check(ex.ToString().Contains("AI4 绝对值批次上界") && f.Backend.PositiveAoWrites == 1 && f.Backend.OpticalHighCount == 0,
                    "earlier positive/negative batch overvoltage survives safe latest sample and aborts before next AO");
            }
            var negativeVmon = new FactoryTests.Fixture();
            negativeVmon.Backend.FeedbackRule = (b,v) => { if (b.Hv && b.Ao > 0) v.VmonRawV = -.1; return v; };
            var failure = Expect(() => negativeVmon.Run());
            check(failure.ToString().Contains("VMON 负向异常") && negativeVmon.Backend.OpticalHighCount == 0,"VMON is never rectified into valid pressure");
            var mismatch = new FactoryTests.Fixture(); mismatch.Settings.TargetKv = 1;
            mismatch.Backend.FeedbackRule = (b,v) => { v.DividerRawV *= -.5; return v; };
            failure = Expect(() => mismatch.Run());
            check(failure.ToString().Contains("不一致") && mismatch.Backend.OpticalHighCount == 0,"opposite sign cannot conceal amplitude mismatch");
            var residual = new FactoryTests.Fixture();
            residual.Backend.FeedbackRule = (b,v) => { v.DividerRawV = -.02; return v; };
            Expect(() => residual.Run());
            check(residual.Backend.HvHighCount == 0 && residual.Settings.SafeResidualV == 50,"initial negative residual still blocks HV without widening defaults");
            var stale = new FactoryTests.Fixture();
            stale.Backend.FeedbackRule = (b,v) => { v.TimestampUtc = DateTime.UtcNow.AddSeconds(-3); return v; };
            failure = Expect(() => stale.Run());
            check(failure.ToString().Contains("时间戳") && failure.ToString().Contains("年龄=") && failure.ToString().Contains("缓存="),"stale feedback has concrete diagnostic cause");
            var invalid = new FactoryTests.Fixture();
            invalid.Backend.FeedbackRule = (b,v) => { v.DividerRawV = Double.NaN; return v; };
            failure = Expect(() => invalid.Run());
            check(failure.ToString().Contains("NaN/无穷") && invalid.Backend.HvHighCount == 0,"nonfinite raw feedback is not accepted by abs");
            var bound = new FactoryFeedback { VmonRawV = .1, DividerRawV = -4999.0 / 11000, DividerHalfCodeV = .0002 };
            failure = Expect(() => FactoryRunner.CheckRampVoltage(bound,s,5000));
            check(failure.Message.Contains("AI4 绝对值批次上界"),"negative divider quantization margin retained at overvoltage limit");
            check(FactoryRunner.FeedbackSnapshot(replay,s).Contains("AI4有符号=-563.93") && FactoryRunner.FeedbackSnapshot(replay,s).Contains("|AI4|=563.93"),
                "diagnostic distinguishes signed and control magnitudes");
            return "R11 bipolar divider / positive VMON: " + count + " offline checks PASS; no hardware accessed.";
        }
        private static Exception Expect(Action work)
        { try { work(); } catch (Exception ex) { return ex; } throw new Exception("Expected polarity protection failure."); }
    }
}
