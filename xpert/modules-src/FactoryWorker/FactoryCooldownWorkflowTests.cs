using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryCooldownWorkflowTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Cooldown workflow: "+why);checks++;};
            // These are managed fakes even where Simulation=false exercises a
            // production workflow branch. No native backend or SDK is created.
            FactoryCaptureCooldown.Wait(CancellationToken.None);
            try
            {
                var manual=RealFixture();
                using(var engine=new FactoryManualEngine(manual.Backend,on=>{},manual.Clock,(k,t)=>{},CancellationToken.None))
                {
                    engine.Initialize(manual.Settings);engine.HvOn(.5);engine.HvUp(.5);
                    FactoryCaptureCooldown.MarkOpened();long before=manual.Clock.ElapsedMilliseconds;
                    var elapsed=Stopwatch.StartNew();var fault=Expect(engine.HvOff);
                    check(fault is AggregateException && manual.Backend.ArmCount==0 && manual.Backend.OpticalHighCount==0,
                        "manual energized handoff refuses cooldown instead of arming or emitting light");
                    check(!manual.Backend.Hv && manual.Backend.Ao==0 && manual.Clock.ElapsedMilliseconds==before && elapsed.ElapsedMilliseconds<1000,
                        "manual refusal still attempts HV OFF and AO zero without a cooldown sleep or 500 ms extension");
                }
                FactoryCaptureCooldown.MarkOpened();
                var manualStart=RealFixture();
                using(var engine=new FactoryManualEngine(manualStart.Backend,on=>{},manualStart.Clock,(k,t)=>{},CancellationToken.None))
                {
                    engine.Initialize(manualStart.Settings);long before=manualStart.Clock.ElapsedMilliseconds;
                    check(Expect(()=>engine.HvOn(.5)) is InvalidOperationException && manualStart.Backend.HvHighCount==0 &&
                        manualStart.Clock.ElapsedMilliseconds==before,"manual HV ON refuses immediately while the common cooldown remains");
                }
                foreach(var kind in new[]{FactoryRunKind.Pressure,FactoryRunKind.Calibration})
                {
                    FactoryCaptureCooldown.MarkOpened();var f=RealFixture();bool sawWait=false;
                    var fault=Expect(()=>f.Run((code,detail)=>
                    {
                        if(code=="CAPTURE_COOLDOWN")
                        {
                            sawWait=true;
                            check(f.Backend.InitializeCount==1 && f.Backend.ShutdownCount>=1 && f.Backend.HvHighCount==0 && f.Backend.ArmCount==0,
                                kind+" waits after initialization/zeroing but before any positive output or arm");
                            f.Cancellation.Cancel();
                        }
                    },kind));
                    check(sawWait && ContainsCancellation(fault) && f.Backend.HvHighCount==0 && f.Backend.ArmCount==0 && f.Backend.DisposeCount==1,
                        kind+" cancellation during shared cooldown cannot advance the workflow");
                }
                FactoryCaptureCooldown.MarkOpened();var zero=RealFixture();zero.Settings.TargetKv=0;
                var input=new Input();bool zeroWait=false;
                var zeroFailure=Expect(()=>FactoryZeroInputRunner.Run(zero.Settings,input,zero.Clock,(code,detail)=>
                {if(code=="CAPTURE_COOLDOWN"){zeroWait=true;zero.Cancellation.Cancel();}},zero.Cancellation.Token));
                check(zeroWait && ContainsCancellation(zeroFailure) && input.Initialized && input.Disposed && !input.Armed,
                    "0 kV input-only path waits after open and cancels before Arm, with no output capability");

                FactoryCaptureCooldown.MarkOpened();var optical=RealFixture();optical.Settings.TargetKv=0;optical.Settings.ZeroSynchronizedOptical=true;
                bool opticalWait=false;
                var opticalFailure=Expect(()=>FactoryZeroTriggerRunner.Run(optical.Settings,optical.Backend,optical.Clock,(code,detail)=>
                {if(code=="CAPTURE_COOLDOWN"){opticalWait=true;optical.Cancellation.Cancel();}},optical.Cancellation.Token));
                check(opticalWait && ContainsCancellation(opticalFailure) && optical.Backend.ArmCount==0 && optical.Backend.OpticalHighCount==0 &&
                    optical.Backend.HvHighCount==0,"explicit 0 kV optical diagnostic cancels cooldown before arming or light");

                FactoryCaptureCooldown.MarkOpened();var retry=RealFixture();retry.Settings.OpticalTimeoutRetry=true;retry.Settings.EndAfterPressureCapture=true;
                retry.Backend.TriggerCompletesOnPulse=2;retry.Backend.Initialize(retry.Settings,false);retry.Backend.Shutdown();
                retry.Backend.Arm(false);retry.Backend.SetOptical(true);retry.Backend.SetOptical(false);retry.Clock.Advance(3000);
                var result=new FactoryResult {Settings=retry.Settings,Kind=FactoryRunKind.Pressure};bool retryWait=false;
                var retryFailure=Expect(()=>FactoryTriggerRecovery.WaitOrRecover(retry.Settings,retry.Backend,retry.Clock,result,0,(code,detail)=>
                {
                    if(code=="CAPTURE_COOLDOWN")
                    {
                        retryWait=true;
                        check(!retry.Backend.Armed && !retry.Backend.Hv && !retry.Backend.Optical && retry.Backend.Ao==0 &&
                            retry.Backend.Events.Any(e=>e.Name=="STOP_CAPTURE") && retry.Backend.RetryCount==0,
                            "supplement cooldown is strictly after old Stop and shutdown, before rearm");
                        retry.Cancellation.Cancel();
                    }
                },retry.Cancellation.Token));
                check(retryWait && ContainsCancellation(retryFailure) && retry.Backend.RetryCount==0 && retry.Backend.OpticalHighCount==1 &&
                    !result.TriggerRecovery.SupplementAttempted,"canceling supplement cooldown cannot rearm or send a second optical pulse");
                FactoryRunner.WaitForCaptureCooldown(true,(code,detail)=>{throw new Exception("Simulation must skip physical cooldown");},CancellationToken.None);
                check(FactoryCaptureCooldown.RemainingMilliseconds>0,"simulation bypass does not reset or consume the physical gate");
            }
            finally { FactoryCaptureCooldown.Wait(CancellationToken.None); }
            return "Cooldown workflows: "+checks+" managed checks PASS; canceled before HV/Arm/optical, manual immediate refusal and shutdown; HardwareAccessed=False";
        }
        private static FactoryTests.Fixture RealFixture()
        {
            var f=new FactoryTests.Fixture();f.Settings.Simulation=false;f.Settings.FeedbackWiringConfirmed=true;
            f.Settings.IndependentDischargeConfirmed=true;f.Settings.CalibrationSourceConfirmed=true;
            return f;
        }
        private static Exception Expect(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new Exception("Expected cooldown workflow rejection.");}
        private static bool ContainsCancellation(Exception ex)
        {
            if(ex is OperationCanceledException)return true;
            var aggregate=ex as AggregateException;
            return aggregate!=null?aggregate.InnerExceptions.Any(ContainsCancellation):ex.InnerException!=null && ContainsCancellation(ex.InnerException);
        }
        private sealed class Input : IFactoryZeroInput
        {
            internal bool Initialized,Armed,Disposed;
            public void Initialize(FactorySettings settings){Initialized=true;}
            public bool EmergencyHigh {get{return false;}}
            public void Arm(){Armed=true;throw new Exception("Cancellation must prevent Arm");}
            public bool ArtComplete {get{return false;}}
            public bool PicoComplete {get{return false;}}
            public BenchCapture ReadCapture(){throw new Exception("Cancellation must prevent Read");}
            public void Dispose(){Disposed=true;}
        }
    }
}
