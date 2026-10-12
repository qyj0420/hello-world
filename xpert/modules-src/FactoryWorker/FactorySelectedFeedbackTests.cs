using System;
using System.Linq;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactorySelectedFeedbackTests
    {
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Selected feedback: "+why);n++;};
            foreach(bool div in new[]{true,false})
            {
                var stalled=new FactoryTests.Fixture();stalled.Settings.DividerOnlyFeedback=div;
                stalled.Settings.Input.LoopDelayMs=50;
                stalled.Backend.FeedbackRule=(b,x)=>{if(div)x.DividerRawV=0;else x.VmonRawV=0;x.ImonRawV=b.Ao>0?5:0;return x;};
                var stallFailure=Fail(()=>stalled.Run());
                check(stallFailure.ToString().Contains("连续3秒达到5 V"),"actual ramp trips joint sustained condition rather than arbitrary mA "+div);
                check(stalled.Clock.ElapsedMilliseconds>=3000&&!stalled.Backend.Hv&&stalled.Backend.Ao==0&&stalled.Backend.OpticalHighCount==0,"sustained current fault shuts down before optical "+div);
                var s=new FactoryTests.Fixture().Settings;s.DividerOnlyFeedback=div;s.TargetKv=5;s.MaximumKv=30;s.PressureFeedbackContext=true;
                var plan=new FactoryArtFeedbackPlan(s);
                var raw=new double[s.FeedbackWindowMs*3];
                for(int i=0;i<s.FeedbackWindowMs;i++)
                {raw[i*3]=i==0?9.216962581972636:5;raw[i*3+1]=div?Double.NaN:5000/s.VmonFullScaleKv/1000*s.FeedbackFullScaleV;
                    raw[i*3+2]=div?5000/s.Input.DividerVPerV:Double.NaN;}
                var f=plan.Parse(raw,s.FeedbackWindowMs,0,true,.24);
                FactoryRunner.ValidateFeedback(f,s,true);FactoryRunner.CheckRampVoltage(f,s,s.FeedbackCeilingKv*1000);
                check(Math.Abs(FactoryRunner.SelectedVoltage(f,s)-5000)<.001,"selected source independent of diagnostic NaN "+div);
                check(f.ImonMaximumRawV==9.216962581972636,"real high current retained, not clipped "+div);
                check(Double.IsNaN(div?f.VmonMedianRawV:f.DividerMedianRawV),"unknown diagnostic not invented "+div);
                f.EmergencyReleased=false;Fail(()=>FactoryRunner.ValidateFeedback(f,s,true));n++;
                f.EmergencyReleased=true;f.TimestampUtc=DateTime.UtcNow.AddSeconds(-3);Fail(()=>FactoryRunner.ValidateFeedback(f,s,true));n++;
                f.TimestampUtc=DateTime.UtcNow;
                raw[(s.FeedbackWindowMs-1)*3+(div?2:1)]=Double.NaN;
                Fail(()=>plan.Parse(raw,s.FeedbackWindowMs,0,true,.24));n++;
                raw[(s.FeedbackWindowMs-1)*3+(div?2:1)]=div?plan.DividerRangeV:plan.VmonRangeV;
                Fail(()=>plan.Parse(raw,s.FeedbackWindowMs,0,true,.24));n++;
                // Actual voltage is still used in ramp/hold; latest pressure
                // policy supersedes startup/end residual observations.
                var run=new FactoryTests.Fixture();run.Settings.DividerOnlyFeedback=div;
                run.Settings.TargetKv=run.Settings.MaximumKv=5;run.Settings.MaximumFeedbackKv=5.1;
                run.Settings.VmonAfterHvStartup=true;run.Settings.StartupVmonLimitV=500;run.Settings.ZeroBackgroundLimitV=500;
                run.Settings.EndResidualCheckEnabled=true;run.Settings.EndResidualThresholdV=1200;run.Settings.TargetHoldChecks=5;
                run.Backend.FeedbackRule=(b,x)=>{x.ImonRawV=9.216962581972636;if(div)x.VmonRawV=Double.NaN;else x.DividerRawV=Double.NaN;return x;};
                var result=run.Run();
                check(result.CaptureOnlyAvailable()&&!result.Completed&&!result.ResidualSafe&&result.TargetHold.Passed,"one source acquisition without invented discharge "+div);
                check(result.StartupFeedback.Count==0,"startup residual AI is not sampled "+div);
                check(result.Ramp.Any(x=>x.ImonRawV>5),"single high current cannot veto valid rising pressure "+div);
                check(run.Backend.Ao==0&&!run.Backend.Hv,"normal shutdown still mandatory "+div);
                check(!result.Settings.ShouldCheckEndResidual,"old persisted percentage does not enable residual AI "+div);
                check(!result.FaultResidual.Started&&result.FinalAi4Observation==null,"no post-pressure observations are fabricated "+div);
                // Actual SDK/IO exception cannot be turned into a valid source.
                var io=new FactoryTests.Fixture();io.Settings.DividerOnlyFeedback=div;
                io.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0)throw new InvalidOperationException("fake SDK IO failure");return x;};
                Fail(()=>io.Run());check(!io.Backend.Hv&&io.Backend.Ao==0&&io.Backend.OpticalHighCount==0,"IO failure shuts down "+div);
            }
            var high=new FactoryFeedback{ImonRawV=5};
            var guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<3000;t+=50)guard.Observe(t,1000,high,true);
            Fail(()=>guard.Observe(3000,1000,high,true));n++;
            guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<=4000;t+=50)guard.Observe(t,1000+t,high,true);
            check(true,"high current alone with growing selected voltage not overcurrent");
            guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<=4000;t+=50)guard.Observe(t,1000,new FactoryFeedback{ImonRawV=4.999999},true);
            check(true,"below exact 5 V never promoted by quantization margin");
            guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<=5000;t+=50)guard.Observe(t,1000,new FactoryFeedback{ImonRawV=t%1000==0?Double.NaN:5},true);
            check(true,"unknown current breaks sustained condition");
            guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<=5000;t+=50)guard.Observe(t,1000,new FactoryFeedback{SampleCount=2,ImonRawV=5,ImonMinimumRawV=4.99},true);
            check(true,"one lower raw sample breaks full-current continuity");
            guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<2500;t+=50)guard.Observe(t,1000,high,true);
            for(int t=2700;t<5700;t+=50)guard.Observe(t,1000,high,true);
            Fail(()=>guard.Observe(5700,1000,high,true));n++;
            guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<=4000;t+=50)guard.Observe(t,1000,high,false);
            check(true,"no output request cannot enter current condition");
            var strict=new FactoryTests.Fixture().Settings;strict.TargetKv=0;
            Fail(()=>new FactoryArtFeedbackPlan(strict).Parse(new[]{9.2,0.0,0.0},1,0,true,0));n++;
            check(true,"legacy zero/calibration feedback does not adopt pressure-only exception");
            foreach(bool div in new[]{true,false})
            {
                var stopped=new FactoryTests.Fixture();stopped.Settings.DividerOnlyFeedback=div;
                stopped.Backend.EmergencyRule=b=>!b.Hv;
                Fail(()=>stopped.Run());check(!stopped.Backend.Hv&&stopped.Backend.Ao==0&&stopped.Backend.OpticalHighCount==0,"real emergency still shuts down "+div);
                var cancelled=new FactoryTests.Fixture();cancelled.Settings.DividerOnlyFeedback=div;
                cancelled.Backend.FeedbackRule=(b,x)=>{if(b.Ao>0)cancelled.Cancellation.Cancel();return x;};
                Fail(()=>cancelled.Run());check(!cancelled.Backend.Hv&&cancelled.Backend.Ao==0&&cancelled.Backend.OpticalHighCount==0,"operator stop still shuts down "+div);
                var timeout=new FactoryTests.Fixture();timeout.Settings.DividerOnlyFeedback=div;timeout.Settings.RampTimeoutSeconds=.05;
                timeout.Backend.FeedbackRule=(b,x)=>{if(div)x.DividerRawV=0;else x.VmonRawV=0;return x;};
                Fail(()=>timeout.Run());check(!timeout.Backend.Hv&&timeout.Backend.Ao==0&&timeout.Backend.OpticalHighCount==0,"original sustained ramp timeout still shuts down "+div);
            }
            return "Selected DIV/VMON + sustained raw 5 V current: "+n+" offline checks PASS; HardwareAccessed=False; SDKCalled=False";
        }
        private static Exception Fail(Action action)
        {try{action();}catch(Exception error){return error;}throw new Exception("Expected selected-feedback rejection.");}
    }
}
