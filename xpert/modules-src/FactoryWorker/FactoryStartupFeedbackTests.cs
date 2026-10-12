using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryStartupFeedbackTests
    {
        internal static string Run()
        {
            int n = 0; Action<bool,string> check = (ok, detail) => { if (!ok) throw new Exception("R9 feedback regression: " + detail); n++; };
            var settings = new FactorySettings { TargetKv = 5, MaximumKv = 5 };
            var plan = new FactoryArtFeedbackPlan(settings);
            check(plan.ImonRangeV == 5.5 && plan.VmonRangeV == 5.5 && Math.Abs(plan.DividerRangeV-5000.0/11000*1.5)<1e-12,"fixed DC range and highest voltage divider headroom");
            check(Math.Abs(2 * FactoryArtFeedbackPlan.HalfCode(1) * 11000 - 5.37109375) < 1e-9,"12-bit divider code size");
            settings.MaximumKv = settings.TargetKv = 28; plan = new FactoryArtFeedbackPlan(settings);
            check(plan.VmonRangeV == 5.5 && Math.Abs(plan.DividerRangeV-28000.0/11000*1.5)<1e-12,"28kV divider range; DC request unchanged");
            Expect(() => FactoryArtFeedbackPlan.SelectRange(11)); n++;
            settings.MaximumKv = settings.TargetKv = 5; plan = new FactoryArtFeedbackPlan(settings);
            var feedback = plan.Parse(new[] { .001,.02,.003, .004,.005,.006 },2,2,true,0);
            check(feedback.SampleCount == 2 && feedback.ImonRawV == .004 && feedback.VmonRawV == .005 && feedback.DividerRawV == .006,"scan interleave mapping, latest sample");
            check(feedback.VmonMaximumRawV == .02 && feedback.VmonMinimumRawV == .005 && feedback.ImonMinimumRawV == .001,"earlier batch extrema retained");
            Expect(() => plan.Parse(new[] { 0.0,0.0,.999 },1,0,true,0)); n++;
            Expect(() => plan.Parse(new[] { 0.0,Double.NaN,0 },1,0,true,0)); n++;
            Expect(() => plan.Parse(new[] { 0.0,0.0,0 },1,21,true,0)); n++;
            Expect(() => plan.Parse(new[] { 0.0,0.0 },1,0,true,0)); n++;
            var f = new FactoryTests.Fixture(); int reads = 0;
            f.Backend.FeedbackRule = (b,value) => { if (!b.Hv && b.PositiveAoWrites == 0 && reads++ < 5) { value.VmonRawV = value.DividerRawV = -.01974; } return value; };
            var result = f.Run();
            check(result.Completed && f.Backend.HvHighCount == 1 && result.StartupFeedback.Any(v => v.DividerRawV == -.01974),"initial-only transient recorded, then stable confirmation permits one round");
            check(f.Backend.Events.Find(e => e.Name == "HV_HIGH").At >= f.Settings.InitialFeedbackSettleMs + 40,"HV cannot start on first-point/early safe readings");
            check(f.Backend.Events.FindIndex(e => e.Name == "SHUTDOWN") < f.Backend.Events.FindIndex(e => e.Name == "HV_HIGH"),"safe zero before startup measurements and HV");
            f = new FactoryTests.Fixture(); f.Backend.FeedbackRule = (b,value) => { value.VmonRawV = value.DividerRawV = -.01974; return value; };
            var error = Expect(() => f.Run());
            var failed = error as FactoryRunException;
            check(f.Backend.HvHighCount == 0 && failed != null && failed.PartialResult.StartupFeedback.Count > 10 && error.ToString().Contains("-19.740"),"persistent negative offset is not auto-zeroed or bypassed; diagnostic retained");
            check(f.Clock.ElapsedMilliseconds >= f.Settings.InitialFeedbackTimeoutMs && f.Backend.DisposeCount == 1,"bounded initial wait and cleanup");
            f = new FactoryTests.Fixture(); int initial = 0;
            f.Backend.FeedbackRule = (b,value) => { if (initial++ % 4 == 0) value.DividerRawV = .02; return value; };
            Expect(() => f.Run());
            check(f.Backend.HvHighCount == 0,"intermittent unsafe voltage resets consecutive count");
            f = new FactoryTests.Fixture();
            f.Clock.AfterPause = () => { if (f.Clock.ElapsedMilliseconds >= 60) f.Cancellation.Cancel(); };
            Expect(() => f.Run());
            check(f.Backend.HvHighCount == 0 && f.Backend.DisposeCount == 1,"cancel during settle cannot enable HV");
            f = new FactoryTests.Fixture();
            f.Backend.FeedbackRule = (b,value) => { value.SampleCount = 2; value.ImonMaximumRawV = 5; return value; };
            Expect(() => f.Run());
            check(f.Backend.HvHighCount == 0,"batch overcurrent cannot be hidden by last zero sample");
            f = new FactoryTests.Fixture();
            f.Backend.FeedbackRule = (b,value) => { value.SampleCount = 2; value.DividerMaximumRawV = .02; return value; };
            Expect(() => f.Run());
            check(f.Backend.HvHighCount == 0,"batch residual peak cannot be hidden by last zero sample");
            f = new FactoryTests.Fixture(); f.Settings.TargetKv = 1; f.Settings.MaximumFeedbackKv = 1.02;
            f.Backend.FeedbackRule = (b,value) =>
            {
                if (b.Hv && b.Ao > 0) { value.SampleCount = 2; value.ImonMaximumRawV = value.ImonRawV;
                    value.VmonMaximumRawV = value.VmonRawV; value.DividerMaximumRawV = Math.Max(value.DividerRawV,.2); }
                return value;
            };
            Expect(() => f.Run());
            check(f.Backend.OpticalHighCount == 0 && f.Backend.PositiveAoWrites == 1,"batch overvoltage in ramp aborts before next AO/light");
            settings.InitialFeedbackSettleMs = 0; Expect(() => settings.Validate(FactoryRunKind.Pressure)); n++;
            settings.InitialFeedbackSettleMs = 200; settings.InitialFeedbackTimeoutMs = 200; Expect(() => settings.Validate(FactoryRunKind.Pressure)); n++;
            settings.InitialFeedbackTimeoutMs = 2000;
            string temp = Path.Combine(Path.GetTempPath(),"Factory-AI-Diag-" + Guid.NewGuid().ToString("N") + ".csv");
            settings.Simulation = false; settings.Input.RealHardware = true; settings.Input.ArtSerial = "123"; settings.Input.ArtDevice = "Dev1";
            var fake = new DiagnosticFake();
            string text = FactoryAiDiagnostic.Run(settings,fake,temp,null,CancellationToken.None);
            check(fake.Reads == 10 && fake.Disposes == 1 && text.Contains("高 5/10") && File.ReadAllLines(temp).Length == 1001,"AI-only diagnostic accepts DI high and low, correct complete CSV, releases");
            using (var cancel = new CancellationTokenSource())
            {
                fake = new DiagnosticFake(); cancel.Cancel();
                Expect(() => FactoryAiDiagnostic.Run(settings,fake,temp,null,cancel.Token));
                check(fake.Reads == 0 && fake.Initializations == 0 && fake.Disposes == 1,"pre-cancelled diagnostic cannot initialize");
            }
            check(!typeof(IFactoryAiReadOnly).GetMethods().Any(m => m.Name.Contains("Write") || m.Name.Contains("Hv") || m.Name.Contains("Optical")),"diagnostic contract has no output methods");
            return "R9 startup/ART feedback: " + n + " offline checks PASS; fake SDK only; no hardware.";
        }
        private sealed class DiagnosticFake : IFactoryAiReadOnly
        {
            internal int Reads, Initializations, Disposes;
            public string Initialize(FactorySettings s) { Initializations++; return "fake only"; }
            public double[] ReadBlock(out bool emergencyReleased)
            { Reads++; emergencyReleased = Reads % 2 == 0; return new double[300]; }
            public void Dispose() { Disposes++; }
        }
        private static Exception Expect(Action action)
        { try { action(); } catch (Exception ex) { return ex; } throw new Exception("Expected R9 rejection."); }
    }
}
