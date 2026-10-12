using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Managed fakes only: never load a driver or operate physical hardware.
    internal static class FactoryFaultResidualTests
    {
        internal static string Run()
        {
            int n=0; Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("R18: "+why);n++;};
            foreach(double sign in new[]{1.0,-1.0})
            {
                var f=new FactoryTests.Fixture(); f.Settings.DividerOnlyFeedback=true;
                f.Backend.FeedbackRule=(b,x)=>{if(b.Hv && b.Ao>0)x.DividerRawV=sign*f.Settings.TargetKv*1000/f.Settings.Input.DividerVPerV;return x;};
                var r=f.Run();
                check(r.Completed && !r.TargetHold.Started && r.TargetHold.Reads==0,"first reach skips optional hold "+sign);
                check(f.Backend.PositiveAoWrites==1 && f.Backend.ArmCount==1 && f.Backend.OpticalHighCount==1,"no more AO writes after first reach "+sign);
                check(!r.FaultResidual.Started,"normal round does not enter fault monitor "+sign);
            }
            foreach(int fault in Enumerable.Range(0,6))
            {
                var f=new FactoryTests.Fixture(); f.Settings.DischargeTimeoutSeconds=.12;
                if(fault==0)f.Backend.FailArm=true;
                if(fault==1)f.Backend.EmergencyRule=b=>b.PositiveAoWrites==0;
                if(fault==2){f.Settings.RampTimeoutSeconds=.003; f.Backend.FeedbackRule=(b,x)=>{if(b.Hv)x.DividerRawV=x.VmonRawV=0;return x;};}
                if(fault==3)f.Backend.FeedbackRule=(b,x)=>{if(b.Hv && b.Ao>0)x.DividerRawV=.2;return x;};
                if(fault==4)f.Backend.FailPositiveAo=true;
                if(fault==5)f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0)x.ImonRawV=5;return x;};
                var error=Fail(()=>f.Run()) as FactoryRunException;
                check(error!=null && error.PartialResult.FaultResidual.Started,"post-fault observation starts "+fault);
                int read=f.Backend.Events.FindIndex(e=>e.Name=="FAULT_READ"),off=f.Backend.Events.FindIndex(e=>e.Name=="SHUTDOWN"),dispose=f.Backend.Events.FindIndex(e=>e.Name=="DISPOSE");
                check(read>off && dispose>read,"disable before read before disposal "+fault);
                check(f.Backend.HvHighCount==1 && f.Backend.OpticalHighCount==0 && f.Backend.InitializeCount==1,"no retry, reconnect or fault light "+fault);
                check(!error.PartialResult.Completed && !error.PartialResult.ResidualSafe,"diagnostic cannot make failed round pass "+fault);
                check(f.Backend.Events.Skip(read).All(e=>e.Name!="AO_POSITIVE"&&e.Name!="HV_HIGH"&&e.Name!="OPTICAL_HIGH"),"observation cannot enable output "+fault);
            }
            foreach(int mode in Enumerable.Range(0,7))
            {
                var f=new FactoryTests.Fixture(); f.Settings.DischargeTimeoutSeconds=.12; f.Backend.Initialize(f.Settings,false); f.Backend.Shutdown();
                f.Backend.EmergencyRule=b=>false;
                f.Backend.FeedbackRule=(b,x)=>{
                    if(mode==0)x.DividerRawV=.1;
                    if(mode==2)x.DividerRawV=Double.NaN;
                    if(mode==3)x.TimestampUtc=DateTime.UtcNow.AddSeconds(-3);
                    if(mode==4)f.Clock.Advance(200);
                    if(mode==5)x.BacklogAfter=21;
                    if(mode==6)throw new IOException("device disconnected");
                    return x;};
                var r=new FactoryResult();
                FactoryFaultResidual.Observe(f.Settings,f.Backend,f.Clock,r,(k,t)=>{if(mode==1)throw new IOException("report failed");});
                check(r.FaultResidual.WithinThreshold==(mode==1),"only valid low feedback meets diagnostic threshold "+mode);
                check(!r.Completed && !r.ResidualSafe && f.Backend.OpticalHighCount==0 && f.Backend.HvHighCount==0,"low P04 does not confer output permission "+mode);
                check(r.FaultResidual.Status.Length>0 && r.SafetyDetail==r.FaultResidual.Status,"explicit terminal status "+mode);
                if(mode==0)check(r.FaultResidual.Feedback.Count>1 && r.FaultResidual.Status.Contains("未确认释放"),"residual retained until bounded timeout");
                if(mode==1)
                {
                    using(var ms=new MemoryStream())
                    {var xml=new XmlSerializer(typeof(FactoryResult));xml.Serialize(ms,r);ms.Position=0;var copy=(FactoryResult)xml.Deserialize(ms);
                        check(copy.FaultResidual.Feedback.Count==5 && copy.FaultResidual.WithinThreshold && !copy.ResidualSafe,"fault observations survive serialization without success flag");}
                }
            }
            {
                var f=new FactoryTests.Fixture(); f.Backend.Initialize(f.Settings,false);f.Backend.FailShutdown=true;
                Fail(()=>f.Backend.Shutdown());var r=new FactoryResult();
                FactoryFaultResidual.Observe(f.Settings,f.Backend,f.Clock,r,(k,t)=>{});
                check(r.FaultResidual.Started && !r.ResidualSafe && f.Backend.InitializeCount==1,"shutdown failure does not reconnect or suppress input attempt");
            }
            {
                var f=new FactoryTests.Fixture(); var plan=new FactoryArtFeedbackPlan(f.Settings);var source=new InputBuffer();
                var value=FactoryFeedbackStream.Read(plan,source,0,()=>0,false);
                check(source.Reads==1 && source.Requested==1 && source.DiReads==0 && value.SampleCount==1,"Available=0 actively reads one sample without DI permission");
                check(!value.EmergencyReleased,"diagnostic value is never an output permit");
                Fail(()=>FactoryFeedbackStream.Read(plan,source,0,()=>0));
                check(source.DiReads==1 && source.Reads==1,"normal output path still blocks on P04 low");
            }
            {
                var f=new FactoryTests.Fixture(); var d=new FactoryShutdownDiagnostic(f.Settings,f.Clock);
                d.Monitor(true);d.Raw(new double[3],1);d.Trigger(0,0,"OFF",true);f.Clock.Advance(500);d.Finish("elapsed",true);
                check(!d.Record.PostReadFinished && d.Record.Status.Contains("残压未知"),"elapsed time without post-off data is not completed diagnosis");
            }
            foreach(double divider in new[]{11000.0,12000.0})
            {
                var f=new FactoryTests.Fixture();var s=f.Settings;
                s.TargetKv=s.MaximumKv=s.MaximumFeedbackKv=30;s.MaximumAoV=5;s.Input.DividerVPerV=divider;
                s.Validate(FactoryRunKind.Pressure);
                check(s.RampUpperV==30000 && s.TargetKv*1000/s.PressureVPerV==5,"30kV ceiling cannot become 30.6/31kV "+divider);
                var plan=new FactoryArtFeedbackPlan(s);
                check(plan.VmonRangeV==5.5 && Math.Abs(plan.DividerRangeV-s.MaximumKv*1000/s.Input.DividerVPerV*1.5)<1e-12,"highest-voltage divider range and fixed DC range "+divider);
                var high=plan.Parse(new[]{0.0,30000.0/6200,-30000.0/divider},1,0,true,5);
                FactoryRunner.ValidateFeedback(high,s,true);
                check(high.DividerRawV<0 && high.VmonRawV>0,"30kV input scales retain polarity "+divider);
                // Half-code guard is preserved at the independent ceiling.
                Fail(()=>FactoryRunner.CheckRampVoltage(high,s,s.FeedbackCeilingKv*1000));n++;
                s.TargetKv=30.01;Fail(()=>s.Validate(FactoryRunKind.Pressure));n++;
                s.TargetKv=30;s.MaximumAoV=4.5;Fail(()=>s.Validate(FactoryRunKind.Pressure));n++;
                s.MaximumAoV=5;s.PressureVPerV=5900;Fail(()=>s.Validate(FactoryRunKind.Pressure));n++;
            }
            {
                var f=new FactoryTests.Fixture();f.Settings.TargetKv=f.Settings.MaximumKv=f.Settings.MaximumFeedbackKv=30;
                f.Settings.MaximumAoV=5;f.Settings.DividerOnlyFeedback=true;
                var r=f.Run();
                check(r.Completed && r.RampStatistics.ExitCode=="Reached" && !r.TargetHold.Started,"30kV managed fake completes first-reach sequence");
                check(f.Backend.MaximumAo<=5 && f.Backend.OpticalHighCount==1,"30kV fake never exceeds 5V AO or retries light");
            }
            foreach(double volts in new[]{0.0,49.0,50.0,51.0,-51.0,300.0})
            {
                var data=Enumerable.Repeat(volts/12000,4800).ToArray();
                var r=FactoryZeroVoltageObservation.Analyze(data,80000,12000);
                check(Math.Abs(r.MeanV-volts)<1e-8 && Math.Abs(r.RmsV-Math.Abs(volts))<1e-8,"zero-voltage signed DC/RMS "+volts);
                check(r.Warning==(Math.Abs(volts)>50) && r.ThresholdV==50 && r.DurationMs==60,"fixed50V post-capture warning "+volts);
                check(data.All(v=>v==volts/12000),"raw waveform not modified "+volts);
            }
            {
                var r=FactoryZeroVoltageObservation.Analyze(new[]{-.006,.006},80000,10000);
                check(r.MeanV==0 && r.Warning && r.AbsolutePeakV==60,"zero mean does not hide alternating voltage/noise peak");
                Fail(()=>FactoryZeroVoltageObservation.Analyze(new[]{Double.NaN},80000,12000));n++;
                Fail(()=>FactoryZeroVoltageObservation.Analyze(new[]{10.0},80000,12000));n++;
                Fail(()=>FactoryZeroVoltageObservation.Analyze(new double[0],80000,12000));n++;
            }
            return "R18 first-reach / 30kV envelope / fault residual: "+n+" offline checks PASS; HardwareAccessed=False";
        }
        private sealed class InputBuffer:IFactoryFeedbackBuffer
        {
            internal int Reads,Requested,DiReads;
            public uint Available(){return 1;}
            public double[] Read(int count){Reads++;Requested=count;return new double[3*count];}
            public bool EmergencyReleased(){DiReads++;return false;}
        }
        private static Exception Fail(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new Exception("Expected R18 rejection.");}
    }
}
