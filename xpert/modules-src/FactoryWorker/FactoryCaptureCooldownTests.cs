using System;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryCaptureCooldownTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Capture cooldown: "+why);checks++;};
            check(FactoryCaptureCooldown.CooldownForDurationMilliseconds(1)==2000 &&
                FactoryCaptureCooldown.CooldownForDurationMilliseconds(60)==2000,"short and 60 ms records both require at least 2 s");
            check(FactoryCaptureCooldown.CooldownForDurationMilliseconds(60.001)==2500 &&
                FactoryCaptureCooldown.CooldownForDurationMilliseconds(70)==2500 &&
                FactoryCaptureCooldown.CooldownForDurationMilliseconds(70.001)==3000 &&
                FactoryCaptureCooldown.CooldownForDurationMilliseconds(80)==3000,"historical long duration uses upward 10 ms bands");
            foreach(double invalid in new[]{0,-1,Double.NaN,Double.PositiveInfinity})
                check(Expect(()=>FactoryCaptureCooldown.CooldownForDurationMilliseconds(invalid)) is ArgumentOutOfRangeException,
                    "unknown/nonpositive duration cannot fabricate a cooldown: "+invalid);
            FactoryCaptureCooldown.RequireSupportedDuration(60);check(true,"60 ms remains accepted for new capture");
            check(Expect(()=>FactoryCaptureCooldown.RequireSupportedDuration(60.001)) is ArgumentOutOfRangeException,
                "long-duration cooldown formula never permits a longer new segment");

            long ticks=10000;
            var gate=new FactoryCaptureCooldownState(()=>ticks,1000);
            check(gate.RemainingMilliseconds==0 && !gate.ReleasePending,"constructing a clock/state neither waits nor opens a device");
            gate.MarkOpened();check(gate.RemainingMilliseconds==2000,"each new successful Open creates a conservative 2 s guard");
            ticks+=1999;check(gate.RemainingMilliseconds==1 && Expect(gate.EnsureReady) is InvalidOperationException,"1999 ms cannot pass a 2 s guard");
            ticks++;gate.EnsureReady();check(gate.RemainingMilliseconds==0,"exact 2 s monotonic boundary permits another arm");
            gate.MarkCaptureStarted(60);ticks+=60000;
            check(gate.ReleasePending && gate.RemainingMilliseconds==Int32.MaxValue &&
                Expect(gate.EnsureReady) is InvalidOperationException,"elapsed time alone never replaces Stop and actual buffer release");
            check(Expect(()=>gate.MarkCaptureStarted(60)) is InvalidOperationException,"another session cannot overlap an unreleased segment");
            using(var cancel=new CancellationTokenSource())
            {
                var failure=Expect(()=>gate.Wait(cancel.Token,remaining=>cancel.Cancel()));
                check(failure is OperationCanceledException && gate.ReleasePending,"pending-release wait is cancelable and does not release ownership");
            }
            gate.MarkReleased(60);check(!gate.ReleasePending && gate.RemainingMilliseconds==2000,"cooldown starts only at the supplied successful release boundary");
            ticks+=1999;check(Expect(()=>gate.MarkCaptureStarted(60)) is InvalidOperationException,"new Arm is rejected throughout cooldown");
            ticks++;gate.MarkCaptureStarted(30);check(gate.ReleasePending,"short next segment still owns the common gate");
            gate.MarkReleased(30);check(gate.RemainingMilliseconds==2000,"short segments never get less than 2 s");
            gate.MarkReleased(80);check(gate.RemainingMilliseconds==3000,"abnormal/historical 80 ms release conservatively sets 3 s");
            gate.MarkOpened();check(gate.RemainingMilliseconds==3000,"new Open cannot shorten an existing longer cooldown");
            ticks+=2000;gate.MarkOpened();check(gate.RemainingMilliseconds==2000,"another opened handle cannot reuse the old remaining 1 s");
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel();check(Expect(()=>gate.Wait(cancel.Token)) is OperationCanceledException && gate.RemainingMilliseconds==2000,
                    "already canceled wait returns without clearing the time boundary");
            }
            int reports=0;
            gate.Wait(CancellationToken.None,remaining=>{reports++;ticks+=remaining;throw new InvalidOperationException("fake progress report failure");});
            check(reports==1 && gate.RemainingMilliseconds==0,"progress failure cannot grant permission; next loop still checks the monotonic deadline");
            gate.MarkOpened();
            var safetyFailure=new InvalidOperationException("injected P0.4 protection fault");
            check(Object.ReferenceEquals(Expect(()=>gate.Wait(CancellationToken.None,null,()=>{throw safetyFailure;})),safetyFailure) &&
                gate.RemainingMilliseconds==2000,"safety checks are not optional progress and their exact failure is never swallowed");
            int safetyChecks=0;
            gate.Wait(CancellationToken.None,null,()=>{safetyChecks++;ticks+=2000;});
            check(safetyChecks==1 && gate.RemainingMilliseconds==0,"safety is checked before the satisfied deadline can authorize another segment");

            long highResolutionTicks=0;
            var fine=new FactoryCaptureCooldownState(()=>highResolutionTicks,1000000);
            fine.MarkOpened();highResolutionTicks=1999999;
            check(fine.RemainingMilliseconds==1,"fractional millisecond remaining is rounded upward");
            highResolutionTicks=2000000;fine.EnsureReady();check(fine.RemainingMilliseconds==0,"fine clock exact boundary is accepted");
            check(Expect(()=>new FactoryCaptureCooldownState(()=>0,0)) is ArgumentException,"invalid monotonic frequency is rejected");
            return "Capture cooldown: "+checks+" managed checks PASS; fake monotonic clock, release ownership, startup guard, 60 ms limit and cancellation; HardwareAccessed=False";
        }
        private static Exception Expect(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new Exception("Expected cooldown rejection.");}
    }
}
