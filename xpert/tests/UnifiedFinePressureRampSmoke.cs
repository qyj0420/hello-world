using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using Xiongxin.FactoryTest;
using Xiongxin.ZeroBench;

internal static class UnifiedFinePressureRampSmoke
{
    static int checks;
    static void Check(bool ok,string message) { ++checks;if(!ok)throw new Exception(message); }
    static bool Reject(Action action) { try{action();return false;}catch(Exception){return true;} }
    static string Xml(object value) { using(var w=new StringWriter()){new XmlSerializer(value.GetType()).Serialize(w,value);return w.ToString();} }
    static FactorySettings Read(string xml) { using(var r=new StringReader(xml))return (FactorySettings)new XmlSerializer(typeof(FactorySettings)).Deserialize(r); }
    static FactoryTests.Fixture Fixture(double target=1,bool fine=true)
    {
        var f=new FactoryTests.Fixture(); var s=f.Settings;
        s.TargetKv=target;s.MaximumKv=s.MaximumFeedbackKv=30;s.MaximumAoV=5;
        s.DividerOnlyFeedback=true;s.EndAfterPressureCapture=true;s.EnableFinePressureRamp=fine;
        s.FinePressureThresholdKv=4.5;s.Input.ArmDelayMs=500;s.Input.CaptureDurationMs=60;
        s.FeedbackWindowMs=10;
        f.Backend.FeedbackRule=(b,x)=>{f.Clock.Advance(10);return x;};
        return f;
    }
    static FactoryResult Ramp(FactoryTests.Fixture f)
    {
        f.Settings.Validate(FactoryRunKind.Pressure);
        var r=new FactoryResult{Settings=f.Settings,Kind=FactoryRunKind.Pressure};
        f.Backend.Initialize(f.Settings,false);f.Backend.SetHv(true);
        try{FactoryRunner.Ramp(f.Settings,f.Backend,f.Clock,r,(k,t)=>{},f.Cancellation.Token);return r;}
        finally{f.Backend.Shutdown();f.Backend.Dispose();}
    }
    static int Main()
    {
        try {
            var s=Fixture().Settings;
            Check(!new FactorySettings().EnableFinePressureRamp,"historical worker settings remain opt-in");
            foreach(double t in new[]{0,4.5-1e-8,4.5,4.5+1e-8}) {
                s.TargetKv=t;Check(s.UseFinePressureRamp==(t>0&&t<=4.5),"threshold is positive peak, inclusive: "+t);
            }
            s.TargetKv=1;s.EnableFinePressureRamp=false;Check(!s.UseFinePressureRamp,"disabled uses fixed-step path");
            s.EnableFinePressureRamp=true;s.ZeroSynchronizedOptical=true;Check(!s.UseFinePressureRamp,"zero diagnostic never fine ramps");
            s.ZeroSynchronizedOptical=false;s.EndAfterPressureCapture=false;Check(!s.UseFinePressureRamp,"legacy manual protocol unaffected");
            s.EndAfterPressureCapture=true;
            foreach(double invalid in new[]{0d,-1,Double.NaN,Double.PositiveInfinity,1000001}) {
                s.FinePressureThresholdKv=invalid;Check(Reject(()=>s.Validate(FactoryRunKind.Pressure)),"invalid threshold rejected "+invalid);
            }
            s.FinePressureThresholdKv=45;s.Validate(FactoryRunKind.Pressure);
            Check(s.MaximumKv==30&&s.FeedbackCeilingKv==30,"threshold above30 does not change hard target limit");
            s.TargetKv=31;Check(Reject(()=>s.Validate(FactoryRunKind.Pressure)),"target above independent30 remains rejected");
            s.TargetKv=1;s.RampTimeoutSeconds=3;Check(Reject(()=>s.Validate(FactoryRunKind.Pressure)),"planned3s must fit original deadline");
            s.RampTimeoutSeconds=10;
            var clone=s.Clone();Check(clone.EnableFinePressureRamp&&clone.FinePressureThresholdKv==45,"clone retains exact frozen mode and threshold");
            string fingerprint=FactoryBatchAuthorization.Fingerprint(s);s.FinePressureThresholdKv=9;
            Check(fingerprint!=FactoryBatchAuthorization.Fingerprint(s),"batch freezes threshold");
            fingerprint=FactoryBatchAuthorization.Fingerprint(s);s.EnableFinePressureRamp=false;
            Check(fingerprint!=FactoryBatchAuthorization.Fingerprint(s),"batch freezes enable switch");

            var input=new BenchSettings();string inputXml=Xml(input);
            var bridge=Read(UnifiedPressureBridge.CreateConfigurationWithFineRamp(inputXml,12000,60,8,true,9));
            Check(bridge.EnableFinePressureRamp&&bridge.FinePressureThresholdKv==9,"host bridge carries device threshold");
            Check(bridge.MaximumKv==30&&bridge.MaximumFeedbackKv==30&&bridge.TargetTolerancePercent==2&&bridge.MaximumAoV==5,"bridge preserves all original voltage protections");
            bridge.TargetKv=15;Check(bridge.RampUpperV==15300&&bridge.AoCommandCeilingV==1.3125,"2 percent feedback and5 percentAO envelope remain separate");
            Check(!Read(UnifiedPressureBridge.CreateConfigurationWithReview(inputXml,6000,30,8)).EnableFinePressureRamp,"legacy bridge stays disabled");
            Check(Read(UnifiedPressureBridge.CreateConfigurationWithReview(inputXml,6000,30,8)).FinePressureThresholdKv==4.5,"legacy bridge receives15percent default but not enabled");
            Check(Reject(()=>UnifiedPressureBridge.CreateConfigurationWithFineRamp(inputXml,6000,30,8,true,31)),"threshold above original device maximum rejected");
            foreach(double target in new[]{.1,.5,1,3,4.5}) {
                var f=Fixture(target);var r=Ramp(f);
                Check(r.RampStatistics.ExitCode=="Reached"&&r.RampStatistics.FineRampApplied,"fine ramp reaches unchanged band "+target);
                Check(Math.Abs(r.RampStatistics.LastDividerV)>=target*980&&Math.Abs(r.RampStatistics.LastDividerV)<=target*1020,"within original2percent "+target);
                Check(r.RampStatistics.ElapsedMilliseconds>=2500&&r.RampStatistics.ElapsedMilliseconds<10000,"bounded gradual timing "+target);
                Check(r.RampStatistics.FineRampMaximumIncrementV<=.010000000001,"never expands original step "+target);
                Check(r.Ramp.All(x=>x.AoV<=f.Settings.AoCommandCeilingV),"AO ceiling "+target);
                Check(!f.Backend.Hv&&f.Backend.Ao==0&&f.Backend.OpticalHighCount==0,"direct ramp fake always cleaned, never acquired "+target);
            }
            foreach(double gain in new[]{.97,1.03}) {
                var f=Fixture();f.Backend.FeedbackRule=(b,x)=>{f.Clock.Advance(10);x.DividerRawV*=gain;return x;};
                var r=Ramp(f);Check(r.RampStatistics.ExitCode=="Reached","nominal ratio variation "+gain);
            }
            var old=Fixture(1,false);var oldResult=Ramp(old);
            var above=Fixture(4.6);var aboveResult=Ramp(above);
            Check(!oldResult.RampStatistics.FineRampApplied&&!aboveResult.RampStatistics.FineRampApplied,"disabled/above threshold fixed-step unchanged");
            var late=Fixture();late.Backend.FeedbackRule=(b,x)=>{late.Clock.Advance(25);return x;};
            var lateResult=Ramp(late);Check(lateResult.RampStatistics.FineRampSkippedPeriods>0,"late cycles do not burst to catch up");
            var settings=Fixture().Settings;var stats=new FactoryRampStatistics();var schedule=new UnifiedFinePressureRamp(settings,stats,0);
            double jump=schedule.NextAo(0,0,1000);
            Check(jump<=settings.NominalAoV*20/3000+1e-12,"one second stall credits at most two periods");
            Check(schedule.FinishCycle(1000)>=0&&stats.FineRampSkippedPeriods>0,"missed periods skipped");
            settings.Input.AoStepV=.0001;var cap=new UnifiedFinePressureRamp(settings,new FactoryRampStatistics(),0);
            Check(cap.NextAo(0,0,1000)<=.0001,"smaller preexisting step respected");
            Check(cap.NextAo(.1,1000,1010)==.1,"reached target cannot request moreAO");
            // Electrical response lag remains real feedback, never filled from
            // target. This bounded fake is not a claim about a physical supply.
            var lag=Fixture();double output=0;long observed=0;
            lag.Backend.FeedbackRule=(b,x)=>{lag.Clock.Advance(10);long now=lag.Clock.ElapsedMilliseconds;
                output+=(b.Ao*lag.Settings.PressureVPerV-output)*(1-Math.Exp(-(now-observed)/75.0));observed=now;
                x.DividerRawV=output/lag.Settings.Input.DividerVPerV;return x;};
            var lagResult=Ramp(lag);Check(lagResult.RampStatistics.ExitCode=="Reached","synthetic75ms first-order response retains2percent reach");
            using(var reader=new StringReader(Xml(lagResult))) {
                var saved=(FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(reader);
                Check(saved.Settings.EnableFinePressureRamp&&saved.Settings.FinePressureThresholdKv==4.5&&
                    saved.RampStatistics.FineRampApplied&&saved.RampStatistics.FineRampPlannedSeconds==3&&
                    saved.RampStatistics.TargetMinimumV==980&&saved.RampStatistics.TargetMaximumV==1020,"result archive retains actual fine settings and original acceptance band");
            }
            foreach(string fault in new[]{"soft-over","raw-over","overcurrent","emergency","cancel","flat","late-deadline"}) {
                var f=Fixture();int reads=0;
                f.Backend.FeedbackRule=(b,x)=> {
                    ++reads;f.Clock.Advance(fault=="late-deadline"?10000:10);
                    if(fault=="soft-over")x.DividerRawV=1021/f.Settings.Input.DividerVPerV;
                    if(fault=="raw-over")x.DividerRawV=31000/f.Settings.Input.DividerVPerV;
                    if(fault=="overcurrent")x.ImonRawV=5;
                    if(fault=="flat")x.DividerRawV=0;
                    if(fault=="emergency"&&reads>2)f.Backend.EmergencyRule=b2=>false;
                    if(fault=="cancel"&&reads>2)f.Cancellation.Cancel();
                    return x;
                };
                Check(Reject(()=>Ramp(f)),"original protection rejects "+fault);
                Check(!f.Backend.Hv&&f.Backend.Ao==0&&f.Backend.OpticalHighCount==0,"fault does not continue to capture "+fault);
            }
            // Existing independent fake suites; no worker/native backend entry.
            Console.WriteLine(FactoryRampCompletionTests.Run());
            Console.WriteLine(FactoryAoEnvelopeTests.Run());
            Console.WriteLine(FactoryDividerTraceTests.Run());
            Console.WriteLine(FactoryFeedbackWindowTests.Run());
            Console.WriteLine(FactoryFaultResidualTests.Run());
            Console.WriteLine("UNIFIED_FINE_PRESSURE_PASS="+checks+"; HardwareAccessed=False; ProductEntryExecuted=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
