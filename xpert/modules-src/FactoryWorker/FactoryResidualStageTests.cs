using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryResidualStageTests
    {
        internal static string Run()
        {
            int count=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Residual stage: "+why);count++;};
            var s=new FactorySettings { TargetKv=5, InitialResidualV=300, SafeResidualV=50 };
            foreach(double voltage in new[]{0.0,49.9,50,50.1,299.9,300,300.1,-49.9,-50,-50.1,-299.9,-300,-300.1})
            {
                var f=new FactoryFeedback { TimestampUtc=DateTime.UtcNow, EmergencyReleased=true,
                    DividerRawV=voltage/s.Input.DividerVPerV,
                    VmonRawV=voltage/(s.VmonFullScaleKv*1000/s.FeedbackFullScaleV) };
                check(FactoryRunner.InitialResidualOk(f,s)==(Math.Abs(voltage)<=300),"initial boundary "+voltage);
                check(FactoryRunner.ResidualOk(f,s)==(Math.Abs(voltage)<=50),"end boundary "+voltage);
            }
            var peak=new FactoryFeedback { SampleCount=2, DividerMaximumRawV=301/s.Input.DividerVPerV };
            check(!FactoryRunner.InitialResidualOk(peak,s),"raw peak cannot hide behind zero final/median value");
            peak.DividerMaximumRawV=299/s.Input.DividerVPerV;
            peak.DividerHalfCodeV=2/s.Input.DividerVPerV;
            check(!FactoryRunner.InitialResidualOk(peak,s),"half-code margin retained");
            check(s.Clone().InitialResidualV==300 && s.Clone().SafeResidualV==50,"both limits survive serialization");
            var legacy=new FactorySettings { SafeResidualV=40 };
            check(legacy.InitialResidualLimitV==40,"old settings preserve previous startup policy");
            s.ZeroSynchronizedOptical=true;
            check(s.InitialResidualLimitV==50,"zero optical diagnostic not widened");
            s.ZeroSynchronizedOptical=false;s.TargetKv=0;
            check(s.InitialResidualLimitV==50,"zero/calibration does not inherit pressure startup threshold");
            foreach(double invalid in new[]{-1.0,Double.NaN,Double.PositiveInfinity,1001})
            {
                var bad=new FactorySettings { InitialResidualV=invalid };
                bool rejected=false;try{bad.Validate(FactoryRunKind.Pressure);}catch(ArgumentException){rejected=true;}
                check(rejected,"invalid startup limit rejected "+invalid);
            }
            var fixture=new FactoryTests.Fixture();fixture.Settings.InitialResidualV=300;
            fixture.Backend.FeedbackRule=(b,f)=>{if(!b.Hv && b.PositiveAoWrites==0)f.DividerRawV=200/fixture.Settings.Input.DividerVPerV;return f;};
            var result=fixture.Run();
            check(result.Completed && result.StartupFeedback.Any(f=>f.DividerRawV>0),"stable 200 V startup admitted in fake round");
            check(result.ResidualSafe && result.Settings.SafeResidualV==50,"completed round still requires final 50 V");
            fixture=new FactoryTests.Fixture();fixture.Settings.InitialResidualV=300;
            fixture.Backend.FeedbackRule=(b,f)=>{f.DividerRawV=301/fixture.Settings.Input.DividerVPerV;return f;};
            bool blocked=false;try{fixture.Run();}catch(FactoryRunException){blocked=true;}
            check(blocked && fixture.Backend.HvHighCount==0,"above 300 V cannot enable HV");
            return "Residual stage limits: "+count+" offline checks PASS; HardwareAccessed=False";
        }
    }
    // Synthetic feedback only. No native backend, discovery or device entry.
    internal static class FactoryEndResidualTests
    {
        internal static string Run()
        {
            int count=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("End residual: "+why);count++;};
            var fixture=new FactoryTests.Fixture();
            string input=Xml(fixture.Settings.Input);
            var settings=Read(UnifiedPressureBridge.CreateConfigurationWithEndResidual(input,6000,30,8,true,4.5,4));
            check(settings.EndResidualCheckEnabled && settings.EndResidualThresholdV==1200,"30 kV highest peak times 4 percent is 1200 V");
            check(settings.SafeResidualV==50 && settings.StartupVmonLimitV==500 && settings.ZeroBackgroundLimitV==500 && settings.VmonNegativeLimitV==500,"new end limit cannot change startup, polarity or historical residual values");
            check(settings.TargetKv==0 && settings.EndResidualLimitV==50 && !settings.UseEndResidualThreshold,"manual zero does not use percentage");
            settings.TargetKv=5;
            // Pure pressure-stage assertions use the runtime measurement
            // context; XML alone never grants it or any output permission.
            settings.PressureFeedbackContext=true;
            check(settings.ShouldCheckEndResidual && settings.UseCaptureOnlyEnd && settings.UseOpticalTimeoutRetry && !settings.UseFinePressureRamp,"new residual does not turn off the old timeout protocol");
            settings.TargetKv=3;
            check(settings.UseFinePressureRamp && settings.UseCaptureOnlyEnd,"fine ramp remains eligible with new residual enabled");
            settings.TargetKv=5;
            check(settings.Clone().EndResidualThresholdV==1200 && settings.Clone().EndResidualCheckEnabled,"end configuration survives XML ownership clone");
            var uncapped=Read(UnifiedPressureBridge.CreateConfigurationWithEndResidual(input,6000,40,8,false,6,4));
            check(uncapped.MaximumKv==30 && uncapped.EndResidualThresholdV==1600,"residual base is actual device ceiling, not min30 internal output cap");
            foreach(double value in new[]{1199.0,1200.0,1201.0,-1199.0,-1200.0,-1201.0})
            {
                var sample=Feedback(settings,value);
                check(FactoryRunner.EndResidualOk(sample,settings)==(Math.Abs(value)<=1200),"end boundary "+value);
                check(!FactoryRunner.ResidualOk(sample,settings),"historical raw residual remains 50 V "+value);
            }
            var peak=Feedback(settings,0);peak.SampleCount=2;peak.DividerMaximumRawV=1201/settings.Input.DividerVPerV;
            check(!FactoryRunner.EndResidualOk(peak,settings),"raw extrema cannot hide behind zero median/end sample");
            peak.DividerMaximumRawV=1199/settings.Input.DividerVPerV;peak.DividerHalfCodeV=2/settings.Input.DividerVPerV;
            check(!FactoryRunner.EndResidualOk(peak,settings),"raw half-code safety margin remains in new limit");
            foreach(double bad in new[]{0.0,-1.0,Double.NaN,Double.PositiveInfinity,101.0})
            {
                bool refused=false;try{UnifiedPressureBridge.CreateConfigurationWithEndResidual(input,6000,30,8,false,4.5,bad);}catch(ArgumentException){refused=true;}
                check(refused,"invalid percentage denied "+bad);
            }
            var old=Read(UnifiedPressureBridge.CreateConfigurationWithFineRamp(input,6000,30,8,true,4.5));old.TargetKv=3;
            check(!old.EndResidualCheckEnabled && !old.ShouldCheckEndResidual && old.EndResidualLimitV==50 && old.UseFinePressureRamp && old.UseOpticalTimeoutRetry,"old 6-argument bridge keeps old protocol");
            var legacy=Read("<FactorySettings><EndAfterPressureCapture>true</EndAfterPressureCapture><TargetKv>5</TargetKv><SafeResidualV>40</SafeResidualV></FactorySettings>");
            check(!legacy.EndResidualCheckEnabled && legacy.EndResidualLimitV==40 && !legacy.ShouldCheckEndResidual,"legacy XML never silently enables percentage checks");
            check(NativeFactoryBackend.RestoreMonitorAfterCapture(settings,false,false) && !NativeFactoryBackend.RestoreMonitorAfterCapture(old,false,false),"monitor restoration follows independent new setting");
            foreach(bool excessive in new[]{false,true})
            {
                var normal=new FactoryTests.Fixture();Enable(normal.Settings);normal.Settings.DischargeTimeoutSeconds=.12;
                normal.Backend.FeedbackRule=(b,f)=>{if(b.ReadCount>0)SetVoltage(f,normal.Settings,excessive?1201:500);return f;};
                FactoryResult result=null;FactoryRunException failure=null;
                try{result=normal.Run();}catch(FactoryRunException error){failure=error;}
                check(excessive ? failure!=null && !failure.PartialResult.Completed && !failure.PartialResult.ResidualSafe :
                    result!=null && result.Completed && result.ResidualSafe && !result.PostCaptureMonitoringSkipped && result.CompleteCaptureAvailable(),
                    "normal end new threshold success/failure "+excessive);
                check(normal.Backend.HvHighCount==1 && normal.Backend.OpticalHighCount==1 && normal.Backend.DisposeCount==1,"normal residual does not restart outputs "+excessive);
                if(excessive)check(failure.PartialResult.FaultResidual.Started && !failure.PartialResult.FaultResidual.WithinThreshold,"failed end check observes but stays failed");
            }
            foreach(double volts in new[]{500.0,1201.0})
            {
                var fault=new FactoryTests.Fixture();Enable(fault.Settings);fault.Settings.DischargeTimeoutSeconds=.12;fault.Backend.FailArm=true;
                fault.Backend.FeedbackRule=(b,f)=>{if(!b.Hv && b.PositiveAoWrites>0)SetVoltage(f,fault.Settings,volts);return f;};
                FactoryRunException failure=null;try{fault.Run();}catch(FactoryRunException error){failure=error;}
                check(failure!=null && failure.PartialResult.FaultResidual.Started && failure.PartialResult.FaultResidual.WithinThreshold==(volts<=1200),"fault observation uses independent end limit "+volts);
                check(!failure.PartialResult.Completed && !failure.PartialResult.ResidualSafe && !failure.PartialResult.CompleteCaptureAvailable() && !failure.PartialResult.CaptureOnlyAvailable(),"fault within threshold cannot authorize batch continuation "+volts);
                check(fault.Backend.OpticalHighCount==0 && fault.Backend.HvHighCount==1,"fault observation cannot re-arm outputs "+volts);
            }
            var calibration=new FactoryTests.Fixture();Enable(calibration.Settings);
            var calibrated=calibration.Run(kind:FactoryRunKind.Calibration);
            check(!calibrated.Settings.EndResidualCheckEnabled && calibrated.Settings.EndResidualLimitV==50,"calibration explicitly keeps legacy residual, not pressure percentage");
            check(calibration.Backend.HvHighCount==0 && calibration.Backend.PositiveAoWrites==0 && calibration.Backend.OpticalHighCount==0,"calibration does not gain output capability");
            var final=new FactoryTests.Fixture();Enable(final.Settings);final.Settings.SequenceId=Guid.NewGuid().ToString("N");final.Settings.FinalSequenceAi4Observation=true;
            var keys=new List<string>();var complete=final.Run((k,t)=>keys.Add(k));
            check(complete.Completed && complete.ResidualSafe && complete.FinalAi4Observation==null && keys.Contains("STEP_FINAL_AI4_SKIPPED"),"real end checks supersede old isolated mean diagnostic without fabricating observation");
            foreach(string mutation in new[]{"none","threshold","disabled","fault"})
            {
                var request=FactoryBatchAuthorizationTests.Request(2);Enable(request);
                string session="end-residual-fixture-"+mutation;var guard=new FactoryBatchAuthorization(session);
                var issued=guard.Authorize(request);issued.BatchPermit.ConsumeForNative(session,issued);issued.BatchPermit.MarkNativeEnded();
                var record=FactoryBatchAuthorizationTests.Capture(issued);
                check(record.CompleteCaptureAvailable() && !record.CaptureOnlyAvailable(),"new batch has actual complete-policy receipt "+mutation);
                if(mutation=="fault")
                {
                    record.Completed=false;record.ResidualSafe=false;record.FaultResidual.WithinThreshold=true;
                    bool denied=false;try{guard.Complete(issued,record);}catch(InvalidOperationException){denied=true;}
                    check(denied && !issued.BatchPermit.Matches(issued),"fault diagnostic within threshold revokes batch, never advances");continue;
                }
                guard.Complete(issued,record);
                var next=FactoryBatchAuthorizationTests.Next(issued,2);
                if(mutation=="threshold")next.EndResidualThresholdV+=1;
                if(mutation=="disabled")next.EndResidualCheckEnabled=false;
                if(mutation=="none")check(guard.Authorize(next).BatchPermit.Matches(next),"complete end-checked receipt permits only owned next round");
                else
                {
                    bool denied=false;try{guard.Authorize(next);}catch(InvalidOperationException){denied=true;}
                    check(denied,"batch residual context cannot change mid sequence: "+mutation);
                }
            }
            return "End residual percentage: "+count+" offline checks PASS; HardwareAccessed=False";
        }
        private static void Enable(FactorySettings s)
        {s.EndAfterPressureCapture=true;s.EndResidualCheckEnabled=true;s.EndResidualThresholdV=1200;}
        private static FactoryFeedback Feedback(FactorySettings s,double volts)
        {var f=new FactoryFeedback{TimestampUtc=DateTime.UtcNow,EmergencyReleased=true};SetVoltage(f,s,volts);return f;}
        private static void SetVoltage(FactoryFeedback f,FactorySettings s,double volts)
        {f.DividerRawV=volts/s.Input.DividerVPerV;f.VmonRawV=volts/(s.VmonFullScaleKv*1000/s.FeedbackFullScaleV);}
        private static string Xml(object value)
        {using(var writer=new StringWriter()){new XmlSerializer(value.GetType()).Serialize(writer,value);return writer.ToString();}}
        private static FactorySettings Read(string value)
        {using(var reader=new StringReader(value))return (FactorySettings)new XmlSerializer(typeof(FactorySettings)).Deserialize(reader);}
    }
}
