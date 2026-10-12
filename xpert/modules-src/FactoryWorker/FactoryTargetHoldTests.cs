using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryTargetHoldTests
    {
        internal static string Run()
        {
            int n=0; Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("R17 hold: "+why);n++;};
            var defaults=new FactorySettings();
            check(defaults.VmonFullScaleKv==31 && defaults.ImonFullScaleMa==13 && defaults.FeedbackFullScaleV==5,"confirmed feedback scaling");
            check(defaults.CurrentLimitMa==10,"full-scale 13mA does not increase 10mA limit");
            check(defaults.TargetHoldChecks==0 && defaults.TargetHoldIntervalMs==10 && defaults.TargetHoldTimeoutMs==500,"first-reach default; optional hold remains bounded");
            foreach(double sign in new[]{1.0,-1.0})
            {
                var f=Fixture(); bool hold=false; int writes=-1;
                f.Backend.FeedbackRule=(b,x)=>{if(b.Hv && b.Ao>0)x.DividerRawV=sign*5000/11000;return x;};
                var r=f.Run((k,t)=>{if(k=="TARGET_HOLD" && !hold){hold=true;writes=f.Backend.PositiveAoWrites;}});
                check(r.Completed && r.TargetHold.Passed && r.TargetHold.Reads==5 && r.TargetHold.Consecutive==5,"five verifications after first reach "+sign);
                check(writes==f.Backend.PositiveAoWrites && r.TargetHoldFeedback.All(x=>x.AoV==r.TargetHold.FrozenAoV),"AO never changes during hold "+sign);
                check(r.TargetHold.ElapsedMilliseconds==50 && f.Backend.ArmCount==1 && f.Backend.OpticalHighCount==1,"hold precedes normal capture "+sign);
                using(var stream=new MemoryStream())
                {
                    var xml=new XmlSerializer(typeof(FactoryResult));xml.Serialize(stream,r);stream.Position=0;
                    var back=(FactoryResult)xml.Deserialize(stream);
                    check(back.TargetHold.Passed && back.TargetHoldFeedback.Count==5 && back.TargetHold.FrozenAoV==r.TargetHold.FrozenAoV,"hold and raw readings survive XML");
                }
            }
            {
                var f=Fixture(); bool hold=false; int reads=0,writes=0;
                f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0){double v=hold && ++reads==3?4800:5000;x.DividerRawV=v/11000;}return x;};
                var r=f.Run((k,t)=>{if(k=="TARGET_HOLD"&&!hold){hold=true;writes=f.Backend.PositiveAoWrites;}});
                check(r.TargetHold.Passed && r.TargetHold.Reads==8 && r.TargetHold.Consecutive==5,"low sample resets consecutive count");
                check(f.Backend.PositiveAoWrites==writes,"low hold sample cannot restart AO increments");
            }
            foreach(int fault in Enumerable.Range(0,9))
            {
                var f=Fixture();bool hold=false;int writes=0;
                if(fault==8)f.Settings.TargetKv=4;
                f.Backend.FeedbackRule=(b,x)=>{
                    if(b.Hv && b.Ao>0)
                    {
                        x.DividerRawV=f.Settings.TargetKv*1000/11000;
                        if(hold)
                        {
                            if(fault==0)x.DividerRawV=4800.0/11000;
                            if(fault==1)x.EmergencyReleased=false;
                            if(fault==2)x.ImonRawV=4; // 10.4mA under confirmed 13mA/5V scaling
                            if(fault==3)x.DividerRawV=Double.NaN;
                            if(fault==4)x.DividerRawV=5200.0/11000;
                            if(fault==5)f.Clock.Advance(500);
                            if(fault==6)f.Cancellation.Cancel();
                            if(fault==7)x.VmonRawV=-.1;
                            if(fault==8)x.DividerRawV=4200.0/11000; // below independent 5.1kV, outside target band
                        }
                    }
                    return x;};
                var ex=Fail(()=>f.Run((k,t)=>{if(k=="TARGET_HOLD"&&!hold){hold=true;writes=f.Backend.PositiveAoWrites;}})) as FactoryRunException;
                check(ex!=null && ex.PartialResult.RampStatistics.ExitCode=="Reached" && ex.PartialResult.TargetHold.Started && !ex.PartialResult.TargetHold.Passed,"hold failure distinguished from first reach "+fault);
                check(!f.Backend.Hv && f.Backend.Ao==0 && f.Backend.ArmCount==0 && f.Backend.OpticalHighCount==0 && writes==f.Backend.PositiveAoWrites,"hold fault shuts down, no new AO/arm/light "+fault);
                check(ex.PartialResult.TargetHold.ElapsedMilliseconds>=0 && ex.PartialResult.TargetHold.Detail.Length>0,"hold failure statistics retained "+fault);
            }
            {
                var f=Fixture();f.Settings.TargetKv=4;
                f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0){x.DividerRawV=-4078.69/11000;x.SampleCount=1;
                    x.DividerMinimumRawV=x.DividerMaximumRawV=x.DividerRawV;x.DividerHalfCodeV=2.68/11000;
                    x.VmonMinimumRawV=x.VmonMaximumRawV=x.VmonRawV;}return x;};
                var r=f.Run();
                check(r.Completed && r.TargetHold.Passed,"4kV screenshot: 4078.69V latest within band, 4081.37V bound below configured independent ceiling");
            }
            {
                var f=Fixture();var r=new FactoryResult();
                check(Fail(()=>FactoryTargetHold.Verify(f.Settings,f.Backend,f.Clock,r,(k,t)=>{},CancellationToken.None)) is InvalidOperationException,"hold cannot run without first reach");
                check(f.Backend.PositiveAoWrites==0 && f.Backend.ArmCount==0,"invalid hold state cannot create output");
            }
            foreach(int invalid in new[]{0,1,2,3})
            {
                var f=Fixture();
                if(invalid==0)f.Settings.TargetHoldChecks=-1;
                if(invalid==1)f.Settings.TargetHoldIntervalMs=0;
                if(invalid==2)f.Settings.TargetHoldTimeoutMs=50;
                if(invalid==3)f.Settings.TargetHoldTimeoutMs=2001;
                Fail(()=>f.Run());check(f.Backend.InitializeCount==0,"invalid verification timing rejected before initialize "+invalid);
            }
            return "R17 frozen AO / confirmed feedback: "+n+" offline checks PASS; HardwareAccessed=False";
        }
        private static FactoryTests.Fixture Fixture()
        {var f=new FactoryTests.Fixture();f.Settings.TargetKv=f.Settings.MaximumKv=5;f.Settings.MaximumFeedbackKv=5.1;f.Settings.DividerOnlyFeedback=true;f.Settings.TargetHoldChecks=5;return f;}
        private static Exception Fail(Action a)
        {try{a();}catch(Exception ex){return ex;}throw new Exception("Expected R17 hold rejection.");}
    }
}
