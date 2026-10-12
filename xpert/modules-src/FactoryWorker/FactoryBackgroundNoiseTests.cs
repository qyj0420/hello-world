using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // R23 numerical replay and fake backend only. Never opens devices.
    internal static class FactoryBackgroundNoiseTests
    {
        internal static string Run()
        {
            int n=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("R23: "+why);n++;};
            var s=Setup().Settings;
            // Saved HW_20261006_173844_241_1618d3 ramp batch, signed raw volts.
            var replay=new FactoryFeedback { TimestampUtc=DateTime.UtcNow, EmergencyReleased=true,
                SampleCount=32, VmonRawV=.0069061741, VmonMinimumRawV=-.0256378833, VmonMaximumRawV=.0236712946,
                VmonHalfCodeV=4.0/4096/2, DividerRawV=-.0132980689,
                DividerMinimumRawV=-.0162566196, DividerMaximumRawV=.0236838145, DividerHalfCodeV=2.0/4096/2,
                ImonRawV=0, ImonMinimumRawV=-.05, ImonMaximumRawV=.05 };
            var old=s.Clone();old.ZeroBackgroundLimitV=0;
            check(Failure(()=>FactoryRunner.CheckRampVoltage(replay,old,30000)).Message.Contains("-50.00"),"saved batch reproduces legacy 50 V coupling");
            FactoryRunner.CheckRampVoltage(replay,s,30000);
            check(replay.VmonMinimumRawV<0 && replay.DividerRawV<0,"500 V allowance passes saved batch without modifying signed data");
            check(!FactoryRunner.ResidualOk(replay,s),"same batch is NOT residual-safe at 50 V");
            s.SafeResidualV=1;FactoryRunner.CheckRampVoltage(replay,s,30000);
            check(s.VmonNegativeLimitV==500,"ramp polarity independent of residual setting");s.SafeResidualV=50;
            foreach(double v in new[]{-499.0,-500.0,-501.0,30001.0})
            {
                var x=new FactoryFeedback {VmonRawV=v/6200};
                if(v>=-500 && v<=30000){FactoryRunner.CheckRampVoltage(x,s,30000);check(true,"polarity boundary allowed "+v);}
                else check(Failure(()=>FactoryRunner.CheckRampVoltage(x,s,30000))!=null,"raw polarity/overvoltage rejected "+v);
            }
            var half=new FactoryFeedback {VmonRawV=-499/6200.0,VmonHalfCodeV=2/6200.0};
            check(Failure(()=>FactoryRunner.CheckRampVoltage(half,s,30000)).Message.Contains("VMON 负向"),"half-code retained at negative limit");
            foreach(double sign in new[]{-1.0,1.0})
            {
                var x=new FactoryFeedback {DividerRawV=sign*30001/11000.0};
                check(Failure(()=>FactoryRunner.CheckRampVoltage(x,s,30000)).Message.Contains("AI4"),"both AI4 overvoltage polarities retained");
            }
            foreach(int channel in new[]{0,1}) foreach(double v in new[]{-501.0,-500.0,-499.0,499.0,500.0,501.0})
            {
                var f=Setup();int preReads=0;
                f.Backend.FeedbackRule=(b,x)=>{if(b.HvHighCount==0)preReads++;if(b.Hv&&b.Ao==0){if(channel==0)x.VmonRawV=v/6200;else x.DividerRawV=v/11000;}return x;};
                if(Math.Abs(v)<=500)
                {
                    var r=f.Run();check(r.Completed && f.Backend.OpticalHighCount==1,"500 V startup/first ramp passes channel="+channel+" value="+v);
                    check(preReads==0 && f.Backend.MaximumAo<=f.Settings.AoCommandCeilingV,"no pre-HV AI or AO envelope widening");
                }
                else
                {
                    var e=Failure(()=>f.Run());
                    check(e.ToString().Contains(channel==0?"VMON批次绝对上界":"AI4批次绝对上界"),"startup noise boundary rejected channel="+channel+" value="+v);
                    check(f.Backend.PositiveAoWrites==0 && f.Backend.OpticalHighCount==0 && !f.Backend.Hv && f.Backend.Ao==0,"startup fault shuts off without light");
                }
            }
            foreach(int channel in new[]{0,1})
            {
                var f=Setup();f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao==0){if(channel==0){x.VmonRawV=499/6200.0;x.VmonHalfCodeV=2/6200.0;}else{x.DividerRawV=-499/11000.0;x.DividerHalfCodeV=2/11000.0;}}return x;};
                Failure(()=>f.Run());check(f.Backend.PositiveAoWrites==0 && f.Backend.OpticalHighCount==0,"startup half-code boundary retained "+channel);
            }
            {
                var f=Setup();f.Backend.FeedbackRule=(b,x)=>{if(b.Hv){x.VmonRawV=-100/6200.0;x.DividerRawV=-x.DividerRawV;}return x;};
                var r=f.Run();
                check(r.Completed && r.RampStatistics.ExitCode=="Reached","negative VMON background does not affect AI4 target");
                check(r.Ramp.All(x=>x.VmonRawV<0) && r.Ramp.Last().DividerRawV<0,"recorded VMON/AI4 remain signed");
                check(r.Settings.TargetTolerancePercent==2 && r.Settings.TargetHoldChecks==0,"2 percent first-reach unchanged");
            }
            foreach(int fault in new[]{0,1,2,3})
            {
                var f=Setup();f.Backend.FeedbackRule=(b,x)=>{if(b.Hv&&b.Ao>0){if(fault==0)x.VmonRawV=-501/6200.0;if(fault==1)x.ImonRawV=10.01/2.6;if(fault==2)x.DividerRawV=double.NaN;if(fault==3)x.EmergencyReleased=false;}return x;};
                Failure(()=>f.Run());check(f.Backend.PositiveAoWrites==1 && f.Backend.OpticalHighCount==0 && !f.Backend.Hv,"negative/overcurrent/invalid/emergency preserved "+fault);
            }
            foreach(double v in new[]{-501.0,-500.0,-100.0,0,100.0})
                check(s.VmonWorkingVoltageV(v)==(v>=-500&&v<0?0:v),"working zero is bounded, signed raw input unchanged "+v);
            check(double.IsNaN(s.VmonWorkingVoltageV(double.NaN)),"NaN never coerced to zero");
            {
                string encoded=null;var p=new FactoryFeedbackTrendPublisher(s,new FactoryTests.FakeClock(),(k,v)=>encoded=v);
                var x=new FactoryFeedback {VmonRawV=-.02,DividerRawV=-.03};p.Observe(x);p.Flush();
                var t=FactoryFeedbackTrendSample.Parse(encoded);
                check(t.Values[1]<0 && t.Values[3]<0 && t.VmonDisplayText.Contains("工作 0.000") && t.VmonDisplayText.Contains("原始窗口"),"working display retains signed original curve/transport");
                check(FactoryFeedbackTrendSample.Parse("0;-0.1;0;-0.2;0;-0.1;-0.1;0;0;-0.2;-0.2;10").VmonDisplayText.StartsWith("-"),"old display payload not silently zeroed");
            }
            foreach(int channel in new[]{0,1})
            {
                var x=new FactoryFeedback();if(channel==0)x.VmonRawV=-100/6200.0;else x.DividerRawV=-100/11000.0;
                check(!FactoryRunner.ResidualOk(x,s),"100 V raw residual still fails 50 V despite 500 V background "+channel);
            }
            var copy=s.Clone();check(copy.ZeroBackgroundLimitV==500 && copy.StartupVmonLimitV==500 && copy.SafeResidualV==50,"independent limits XML roundtrip");
            var legacy=(FactorySettings)new XmlSerializer(typeof(FactorySettings)).Deserialize(new StringReader("<FactorySettings><SafeResidualV>50</SafeResidualV><StartupVmonLimitV>300</StartupVmonLimitV></FactorySettings>"));
            check(legacy.ZeroBackgroundLimitV==0 && legacy.StartupVmonLimitV==300 && legacy.VmonNegativeLimitV==50,"old XML retains old threshold meanings");
            foreach(double invalid in new[]{-1.0,1001.0,double.NaN,double.PositiveInfinity})
            { var bad=s.Clone();bad.ZeroBackgroundLimitV=invalid;check(Failure(()=>bad.Validate(FactoryRunKind.Pressure)) is ArgumentException,"invalid background setting rejected"); }
            {
                var f=Setup();f.Backend.FeedbackRule=(b,x)=>{x.DividerRawV=100/11000.0;return x;};
                Failure(()=>f.Run(null,FactoryRunKind.Calibration));
                check(f.Backend.HvHighCount==0 && f.Backend.OpticalHighCount==0,"calibration still uses 50 V, not background allowance");
            }
            return "R23 independent 500 V zero/background / signed replay / residual50: "+n+" offline checks PASS; HardwareAccessed=False";
        }
        private static FactoryTests.Fixture Setup()
        {
            var f=new FactoryTests.Fixture();f.Settings.VmonAfterHvStartup=true;f.Settings.DividerOnlyFeedback=true;
            f.Settings.StartupVmonLimitV=f.Settings.ZeroBackgroundLimitV=500;
            f.Settings.MaximumKv=f.Settings.MaximumFeedbackKv=30;f.Settings.DischargeTimeoutSeconds=.12;
            return f;
        }
        private static Exception Failure(Action action)
        {try{action();}catch(Exception e){return e;}throw new Exception("Expected R23 guard failure");}
    }
}
