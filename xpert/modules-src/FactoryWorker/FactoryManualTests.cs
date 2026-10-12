using System;
using System.Linq;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryManualTests
    {
        internal static string Run()
        {
            int n = 0; Action<bool,string> check = (ok,text) => { if (!ok) throw new Exception("Manual regression: " + text); n++; };
            var f = new FactoryTests.Fixture(); int indicatorHigh = 0, indicatorLow = 0;
            using (var e = new FactoryManualEngine(f.Backend,high => { if (high) indicatorHigh++; else indicatorLow++; },f.Clock,(key,text) => { },f.Cancellation.Token))
            {
                e.Initialize(f.Settings); e.Tick();
                check(f.Backend.HvHighCount == 0 && f.Backend.PositiveAoWrites == 0 && f.Backend.OpticalHighCount == 0,"init/tick never advances to HV or optical");
                Expect(() => e.HvUp(1)); Expect(() => e.TriggerCapture()); n += 2;
                e.HvOn(1);
                check(f.Backend.Hv && f.Backend.HvHighCount == 1 && f.Backend.PositiveAoWrites == 0,"manual ON enables only source, no automatic ramp");
                check(indicatorHigh == 0,"ON does not silently turn on indicator");
                e.Indicator(true); e.Indicator(false);
                check(indicatorHigh == 1 && indicatorLow == 1,"indicator changes only explicit commands");
                e.HvUp(1);
                check(f.Backend.Hv && f.Backend.Ao > 0 && f.Backend.OpticalHighCount == 0 && f.Backend.ReadCount == 0,"manual UP holds source/AO and never automatically OFF or capture");
                check(e.Result.Ramp.Count > 5,"voltage/current feedback stored during ramp");
                e.HvOff(); var low = f.Backend.Events.FindIndex(ev => ev.Name == "HV_LOW");
                check(low > 0 && f.Backend.Events[low + 1].Name == "AO_ZERO" && !f.Backend.Hv && f.Backend.Ao == 0,"manual OFF is HV OFF then AO zero");
                check(f.Backend.Events[low].At-f.Backend.Events.First(ev=>ev.Name=="ARM_EXT").At==500,"manual arming start overlaps 500 ms before OFF");
                check(f.Backend.OpticalHighCount == 0 && f.Backend.ReadCount == 0,"OFF never triggers on its own");
                e.TriggerCapture();
                check(f.Backend.OpticalAt==f.Backend.Events[low].At,"manual trigger adds no post-OFF delay");
                check(f.Backend.OpticalHighCount == 1 && f.Backend.ReadCount == 1 && !f.Backend.Optical,"explicit trigger obtains one complete capture then returns light LOW");
                check(f.Backend.Events.First(ev=>ev.Name=="OPTICAL_LOW" && ev.At>=f.Backend.OpticalAt+1000).At-f.Backend.OpticalAt==1000,"manual normal optical high holds exactly 1000 ms in fake clock");
                check(e.Result.Capture != null && !e.Result.Completed && !e.Result.ResidualSafe,"manual record not mislabeled full safety test or discharged");
                Expect(() => e.TriggerCapture()); n++;
                Expect(() => e.HvOn(1)); n++;
            }
            check(f.Backend.DisposeCount == 1 && !f.Backend.Hv && f.Backend.Ao == 0,"owner cleanup stops and disposes");
            f = new FactoryTests.Fixture();
            using (var e = New(f))
            { e.Initialize(f.Settings); e.HvOn(0); check(f.Backend.HvHighCount == 0,"zero target cannot start source"); }
            f = new FactoryTests.Fixture();
            using (var e = New(f))
            { e.Initialize(f.Settings); e.HvOn(1); e.HvUp(0); check(!f.Backend.Hv && f.Backend.Ao == 0 && f.Backend.PositiveAoWrites == 0,"zero UP request stops instead of ramping"); }
            f = new FactoryTests.Fixture(); f.Backend.EmergencyRule = b => false;
            using (var e = New(f))
            { e.Initialize(f.Settings); Expect(() => e.HvOn(1)); check(f.Backend.HvHighCount == 0,"initial low still prevents source start"); }
            f = new FactoryTests.Fixture(); f.Backend.FeedbackRule = (b,value) => { value.DividerRawV = -.01974; return value; };
            using (var e = New(f))
            { e.Initialize(f.Settings); Expect(() => e.HvOn(1)); check(f.Backend.HvHighCount == 0,"manual mode does not convert persistent residual into zero"); }
            f = new FactoryTests.Fixture();
            using (var e = New(f))
            { e.Initialize(f.Settings); e.HvOn(1); f.Backend.EmergencyRule = b => false; Expect(e.Tick); n++; }
            check(!f.Backend.Hv && f.Backend.DisposeCount == 1,"idle emergency failure followed by owner cleanup cannot leave HV high");
            f = new FactoryTests.Fixture();
            using (var e = New(f))
            {
                e.Initialize(f.Settings); e.HvOn(1); f.Cancellation.Cancel(); Expect(() => e.HvUp(1));
                check(f.Backend.PositiveAoWrites == 0,"stop token before UP prevents positive AO");
            }
            f = new FactoryTests.Fixture(); f.Backend.NeverComplete = true;
            using (var e = New(f))
            {
                e.Initialize(f.Settings); e.HvOn(1); e.HvUp(1); e.HvOff(); Expect(e.TriggerCapture);
                check(f.Backend.OpticalHighCount == 1 && !f.Backend.Optical && f.Backend.ReadCount == 0,"capture timeout has no repeat trigger or partial read");
            }
            return "Manual stepped engine: " + n + " offline checks PASS; fake SDK only; no hardware.";
        }
        private static FactoryManualEngine New(FactoryTests.Fixture f)
        { return new FactoryManualEngine(f.Backend,high => { },f.Clock,(key,text) => { },f.Cancellation.Token); }
        private static void Expect(Action work)
        { try { work(); } catch { return; } throw new Exception("Expected manual rejection."); }
    }
}
