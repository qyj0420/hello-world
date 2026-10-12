using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Managed fakes only: no driver enumeration, loading or output writes.
    internal static class FactoryRampCompletionTests
    {
        internal static string Run()
        {
            int n=0; Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("R16 ramp: "+why);n++;};
            check(new FactorySettings().RampTimeoutSeconds==10,"default ramp deadline 10 seconds");
            check(new FactorySettings().ManualHvOnTimeoutSeconds==300,"manual idle watchdog separate from HV UP");
            var defaults=new FactorySettings { TargetKv=5,MaximumKv=5 };
            check(defaults.RampUpperV==5000,"historical settings retain absolute ceiling without explicit authorization");
            defaults.MaximumFeedbackKv=5.1;
            check(defaults.RampUpperV==5100 && defaults.Clone().MaximumFeedbackKv==5.1,"explicit feedback tolerance independent of 5kV command ceiling");
            defaults.MaximumFeedbackKv=Double.NaN;check(Fail(()=>defaults.Validate(FactoryRunKind.Pressure))!=null,"nonfinite feedback limit rejected");

            foreach(double sign in new[]{1.0,-1.0}) foreach(double v in new[]{4900.0,4941.103,5000.0,5060.431,5100.0})
            {
                var f=Fixture(); int reads=0;
                f.Backend.FeedbackRule=(b,x)=>{reads++;x.DividerRawV=sign*v/11000;return x;};
                var r=Direct(f);
                check(reads==1 && r.Ramp.Count==1 && r.RampStatistics.ExitCode=="Reached","first sample within inclusive tolerance: "+sign*v);
                check(r.RampStatistics.Iterations==1 && r.RampStatistics.ValidFeedbackCount==1,"one loop, one valid feedback");
                check(r.Ramp[0].DividerRawV*sign>0 && f.Backend.PositiveAoWrites==0,"retain sign and do not add AO after reach");
            }
            {
                var f=Fixture();int index=0;
                // Last observations reproduced numerically from 08:07:54 R15.
                double[] latest={4724.14,4767.53,4745.84,4523.45,4941.103,4669.90};
                double[] peaks={4724.14,4767.53,4903.14,4523.45,4951.95,5060.431};
                f.Backend.FeedbackRule=(b,x)=>{
                    int i=index++;x.DividerRawV=-latest[i]/11000;
                    x.SampleCount=1;x.DividerMinimumRawV=-peaks[i]/11000;x.DividerMaximumRawV=x.DividerRawV;
                    x.VmonMinimumRawV=x.VmonMaximumRawV=x.VmonRawV;x.DividerHalfCodeV=1.0/4096;return x;
                };
                var r=Direct(f);
                check(index==5 && r.RampStatistics.Iterations==5,"R15 replay stops at 4.941kV, never reads next dip/peak batch");
                check(f.Backend.PositiveAoWrites==4 && Math.Abs(f.Backend.MaximumAo-.04)<1e-12,"fixed AO increments stop on first reach");
            }
            {
                var f=Fixture();f.Settings.Input.LoopDelayMs=7;int reads=0;
                f.Backend.FeedbackRule=(b,x)=>{reads++;f.Clock.Advance(11);x.DividerRawV=(reads==1?4899:4900)/11000.0;return x;};
                var r=Direct(f);
                check(r.RampStatistics.Iterations==2 && r.RampStatistics.ElapsedMilliseconds==29,"actual elapsed includes read latency, not count multiplied by interval");
            }
            foreach(int guard in new[]{0,1,2,3,4,5})
            {
                var f=Fixture();f.Backend.FeedbackRule=(b,x)=>{
                    if(b.Hv && b.Ao>0){
                        x.DividerRawV=-4950.0/11000;
                        if(guard==0){x.SampleCount=2;x.DividerMinimumRawV=-5110.0/11000;x.DividerMaximumRawV=x.DividerRawV;x.VmonMinimumRawV=x.VmonMaximumRawV=x.VmonRawV;}
                        if(guard==1)x.VmonRawV=-.1;
                        if(guard==2)x.ImonRawV=5;
                        if(guard==3)x.TimestampUtc=DateTime.UtcNow.AddSeconds(-10);
                        if(guard==4)x.DividerRawV=Double.NaN;
                        if(guard==5)x.EmergencyReleased=false;
                    }return x;};
                var ex=Fail(()=>f.Run()) as FactoryRunException;
                check(ex!=null && ex.PartialResult.RampStatistics.ExitCode=="Fault","in-range latest cannot bypass independent guard "+guard);
                check(!f.Backend.Hv && f.Backend.Ao==0 && f.Backend.ArmCount==0 && f.Backend.OpticalHighCount==0,"fault shutdown; no arm/light "+guard);
            }
            {
                var f=Fixture();f.Settings.RampTimeoutSeconds=.020;f.Settings.Input.LoopDelayMs=100;
                var ex=Fail(()=>f.Run()) as FactoryRunException;
                check(ex!=null && ex.PartialResult.RampStatistics.ExitCode=="Timeout","timeout identified separately");
                check(ex.PartialResult.RampStatistics.ElapsedMilliseconds==20,"requested sleep clamped to deadline");
                check(f.Backend.ArmCount==0 && f.Backend.OpticalHighCount==0 && !f.Backend.Hv && f.Backend.Ao==0,"timeout does not advance capture or optical");
                using(var stream=new MemoryStream()){
                    var xml=new XmlSerializer(typeof(FactoryResult));xml.Serialize(stream,ex.PartialResult);stream.Position=0;
                    var back=(FactoryResult)xml.Deserialize(stream);
                    check(back.RampStatistics.Iterations==ex.PartialResult.RampStatistics.Iterations && back.RampStatistics.ElapsedMilliseconds==20 && back.RampStatistics.ExitCode=="Timeout","failed statistics persist through result XML");
                }
            }
            {
                var f=Fixture();f.Settings.RampTimeoutSeconds=.010;
                f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0){f.Clock.Advance(10);x.DividerRawV=4950.0/11000;}return x;};
                var ex=Fail(()=>f.Run()) as FactoryRunException;
                check(ex!=null && ex.PartialResult.RampStatistics.ExitCode=="Timeout" && f.Backend.ArmCount==0,"late target read cannot override elapsed deadline");
            }
            {
                var f=Fixture();f.Backend.EmergencyRule=b=>b.Ao==0;
                var ex=Fail(()=>f.Run()) as FactoryRunException;
                check(ex!=null && ex.PartialResult.RampStatistics.ExitReason.Contains("P0.4") && f.Backend.OpticalHighCount==0,"emergency exit reason preserved and no optical");
            }
            {
                var f=Fixture();f.Clock.AfterPause=()=>{if(f.Backend.Ao>0)f.Cancellation.Cancel();};
                var ex=Fail(()=>f.Run()) as FactoryRunException;
                check(ex!=null && ex.PartialResult.RampStatistics.ExitCode=="Cancelled" && f.Backend.ArmCount==0 && f.Backend.Ao==0,"cancel records statistics and shuts down");
            }
            {
                var f=Fixture();bool finalAfterShutdown=false;
                f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0)x.DividerRawV=5110.0/11000;return x;};
                Fail(()=>f.Run((k,t)=>{if(k=="RAMP_SUMMARY")finalAfterShutdown=!f.Backend.Hv&&f.Backend.Ao==0;}));
                check(finalAfterShutdown,"failure summary published after shutdown");
            }
            {
                var f=Fixture();f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0)x.DividerRawV=-5060.431/11000;return x;};
                var r=f.Run();
                check(r.Completed && r.RampStatistics.ExitCode=="Reached" && f.Backend.OpticalHighCount==1,"5.060kV reaches normal arm/OFF/optical path");
                check(f.Backend.MaximumAo<=f.Settings.AoCommandCeilingV,"feedback tolerance never expands authorized AO command ceiling");
            }
            {
                var f=Fixture();f.Settings.ManualHvOnTimeoutSeconds=30;
                using(var e=new FactoryManualEngine(f.Backend,b=>{},f.Clock,(k,t)=>{},CancellationToken.None)){
                    e.Initialize(f.Settings);e.HvOn(5);f.Clock.Advance(11000);e.Tick();
                    check(f.Backend.Hv,"10 second HV UP default does not become a manual idle timeout");
                    f.Clock.Advance(20000);check(Fail(e.Tick) is TimeoutException,"independent manual HV ON watchdog retained");
                }
            }
            return "R16 first-reach/tolerance/ramp-statistics: "+n+" offline checks PASS; HardwareAccessed=False";
        }
        private static FactoryTests.Fixture Fixture()
        {var f=new FactoryTests.Fixture();f.Settings.TargetKv=f.Settings.MaximumKv=5;f.Settings.MaximumFeedbackKv=5.1;f.Settings.DividerOnlyFeedback=true;return f;}
        private static FactoryResult Direct(FactoryTests.Fixture f)
        {
            var r=new FactoryResult {Settings=f.Settings,Kind=FactoryRunKind.Pressure};
            try{f.Backend.Initialize(f.Settings,false);f.Backend.SetHv(true);FactoryRunner.Ramp(f.Settings,f.Backend,f.Clock,r,(k,t)=>{},f.Cancellation.Token);return r;}
            finally{f.Backend.Shutdown();f.Backend.Dispose();}
        }
        private static Exception Fail(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new Exception("Expected R16 failure did not occur");}
    }
}
