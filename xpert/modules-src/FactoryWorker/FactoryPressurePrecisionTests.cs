using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Managed backend/clock and synthetic feedback only. No SDK, native entry
    // point, discovery, device or physical high-voltage acceptance is invoked.
    internal static class FactoryPressurePrecisionTests
    {
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Pressure precision: "+why);n++;};
            string input=UnifiedPressureBridge.Xml(new FactoryTests.Fixture().Settings.Input);
            foreach(bool div in new[]{true,false})
            {
                var fresh=UnifiedPressureBridge.Read<FactorySettings>(UnifiedPressureBridge.CreateConfigurationWithoutResidual(input,6000,30,8,false,4.5,div?"DIV":"VMON"));
                check(fresh.PrecisionRampControl&&fresh.RelativeOvervoltagePercent==20&&!fresh.UsePrecisionRamp,"new entry explicit opt-in, XML is not active pressure purpose");
                fresh.TargetKv=5;fresh.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                check(fresh.UsePrecisionRamp&&fresh.EffectiveTargetTolerancePercent==5&&fresh.RampUpperV==5250&&fresh.PrecisionProtectionUpperV==6000,"independent precision/protection windows");
                check(Math.Abs(fresh.AoCommandCeilingV-5.0/6*1.05)<1e-12,"AO remains nominal plus 5%, not abnormal feedback limit");
                fresh.TargetKv=0;fresh.ManualZeroInputWithoutEmergency=true;fresh.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                check(!fresh.UsePrecisionRamp&&fresh.EffectiveTargetTolerancePercent==2,"manual zero never inherits precision permission");
                fresh.ManualZeroInputWithoutEmergency=false;fresh.ValidateBeforeOperatorConfirmation(FactoryRunKind.Calibration);
                check(!fresh.UsePrecisionRamp&&!fresh.PressureFeedbackContext,"calibration never inherits pressure flow");

                var warning=Fixture(div);int index=0;DateTime utc=DateTime.UtcNow.AddMilliseconds(-500);
                Feed(warning,div,()=>new[]{0.0,5350,5400,5410,5390}[Math.Min(index++,4)],()=>utc.AddMilliseconds(index));
                var result=warning.Run();
                check(result.CaptureOnlyAvailable()&&!result.Completed&&!result.ResidualSafe,"warning retains acquisition-only receipt, never discharge clearance");
                check(result.Ramp.Count==5&&result.RampStatistics.PrecisionRecheckReads==3,"detecting feedback excluded, exactly three later new reads");
                check(result.RampStatistics.ExitCode=="ReachedWithPrecisionWarning"&&result.RampStatistics.PrecisionWarning&&
                    result.RampStatistics.PrecisionEvaluated&&!result.RampStatistics.WithinPrecision,"warning never reports precision passed");
                check(warning.Backend.PositiveAoWrites==1&&result.Ramp.Skip(1).All(x=>x.AoV==.01),"AO frozen immediately at initial upper-band deviation");
                check(warning.Backend.ArmCount==1&&warning.Backend.OpticalHighCount==1&&!warning.Backend.Hv&&warning.Backend.Ao==0,"same normal arm/off/capture chain, no second HV activation");
                check(!result.TargetHold.Passed&&result.TargetHold.Detail.Contains("未声明精度合格"),"target-hold does not erase warning");
                FactoryResult loaded;
                using(var stream=new MemoryStream())
                {
                    var serializer=new XmlSerializer(typeof(FactoryResult));serializer.Serialize(stream,result);stream.Position=0;
                    loaded=(FactoryResult)serializer.Deserialize(stream);
                }
                check(loaded.RampStatistics.PrecisionWarning&&!loaded.RampStatistics.WithinPrecision&&loaded.RampStatistics.PrecisionRecheckReads==3,"archived warning and count preserved");

                var recovered=Fixture(div);index=0;utc=DateTime.UtcNow.AddMilliseconds(-500);
                Feed(recovered,div,()=>new[]{0.0,5350,5360,5000}[Math.Min(index++,3)],()=>utc.AddMilliseconds(index));
                result=recovered.Run();
                check(result.Ramp.Count==4&&result.RampStatistics.PrecisionRecheckReads==2&&result.RampStatistics.ExitCode=="Reached","second new recheck back inside window ends ramp");
                check(result.RampStatistics.WithinPrecision&&!result.RampStatistics.PrecisionWarning&&recovered.Backend.PositiveAoWrites==1,"recovery truthful, AO cannot resume");

                var optionalHold=Fixture(div);optionalHold.Settings.TargetHoldChecks=2;
                index=0;utc=DateTime.UtcNow.AddMilliseconds(-500);
                Feed(optionalHold,div,()=>++index==1?0:5000,()=>utc.AddMilliseconds(index));
                result=optionalHold.Run();
                check(result.TargetHold.Passed&&result.TargetHoldFeedback.Count==2&&result.RampStatistics.WithinPrecision,"optional hold still verifies actual window without AO changes");
                check(optionalHold.Backend.PositiveAoWrites==1,"optional hold never increases AO");

                var holdWarning=Fixture(div);holdWarning.Settings.TargetHoldChecks=5;
                bool holding=false;index=0;utc=DateTime.UtcNow.AddMilliseconds(-500);
                Feed(holdWarning,div,()=>++index==1?0:holding?5350:5000,()=>utc.AddMilliseconds(index));
                result=holdWarning.Run((key,detail)=>{if(key=="TARGET_HOLD")holding=true;});
                check(result.TargetHoldFeedback.Count==4&&result.RampStatistics.PrecisionRecheckReads==3,"hold detecting read also excluded from three later readings");
                check(result.CaptureOnlyAvailable()&&result.RampStatistics.PrecisionWarning&&!result.RampStatistics.WithinPrecision&&!result.TargetHold.Passed,"hold warning is archived without claiming precision passed");
                check(holdWarning.Backend.PositiveAoWrites==1&&holdWarning.Backend.OpticalHighCount==1,"hold deviation freezes existing AO and uses original capture path");

                var alreadyWarning=Fixture(div);alreadyWarning.Settings.TargetHoldChecks=5;
                index=0;utc=DateTime.UtcNow.AddMilliseconds(-500);
                Feed(alreadyWarning,div,()=>++index==1?0:5350,()=>utc.AddMilliseconds(index));
                result=alreadyWarning.Run();
                check(result.Ramp.Count==5&&result.TargetHoldFeedback.Count==0&&result.RampStatistics.PrecisionWarning,"already completed three-read warning cannot restart a second verification gate");

                foreach(double accepted in new[]{4750.0,5250.0})
                {
                    var inclusive=Fixture(div);Feed(inclusive,div,()=>accepted,()=>DateTime.UtcNow);
                    result=inclusive.Run();check(result.Ramp.Count==1&&result.RampStatistics.WithinPrecision&&inclusive.Backend.PositiveAoWrites==0,"inclusive 5% band "+accepted);
                }
                foreach(string cause in new[]{"relative","independent","raw","quantization","invalid","stale","emergency","cancel","io"})
                {
                    var fault=Fixture(div);index=0;utc=DateTime.UtcNow.AddMilliseconds(-500);
                    if(cause=="independent")fault.Settings.MaximumFeedbackKv=5.4;
                    Feed(fault,div,()=>++index==1?0:cause=="independent"?5400:cause=="relative"?6000:5350,()=>utc.AddMilliseconds(index));
                    var original=fault.Backend.FeedbackRule;
                    fault.Backend.FeedbackRule=(b,x)=>{
                        x=original(b,x);
                        if(b.Ao>0)
                        {
                            if(cause=="raw"||cause=="quantization")SetRawBound(x,div,fault.Settings,cause=="raw"?6100:6000, cause=="quantization"?1:0);
                            if(cause=="invalid"){if(div)x.DividerRawV=Double.NaN;else x.VmonRawV=Double.NaN;}
                            if(cause=="stale")x.TimestampUtc=DateTime.UtcNow.AddSeconds(-3);
                            if(cause=="emergency")x.EmergencyReleased=false;
                            if(cause=="cancel")fault.Cancellation.Cancel();
                            if(cause=="io")throw new InvalidOperationException("fake IO failure");
                        }
                        return x;
                    };
                    var error=Fail(()=>fault.Run()) as FactoryRunException;
                    check(error!=null&&!error.PartialResult.CaptureOnlyAvailable()&&!error.PartialResult.RampStatistics.PrecisionWarning,"genuine "+cause+" never becomes precision warning "+div);
                    check(fault.Backend.PositiveAoWrites==1&&fault.Backend.ArmCount==0&&fault.Backend.OpticalHighCount==0&&!fault.Backend.Hv&&fault.Backend.Ao==0,"genuine "+cause+" existing shutdown prevents capture/output retry");
                }

                var duplicate=Fixture(div);index=0;utc=DateTime.UtcNow.AddMilliseconds(-500);
                duplicate.Settings.RampTimeoutSeconds=.07;
                Feed(duplicate,div,()=>++index==1?0:5350,()=>utc);
                var duplicateError=Fail(()=>duplicate.Run()) as FactoryRunException;
                check(duplicateError!=null&&duplicateError.PartialResult.RampStatistics.ExitCode=="Timeout"&&
                    duplicateError.PartialResult.RampStatistics.PrecisionRecheckReads==0,"cached UTC cannot count three reads or wait beyond total deadline");
                check(duplicate.Backend.PositiveAoWrites==1&&duplicate.Backend.ArmCount==0&&duplicate.Clock.ElapsedMilliseconds>=70,"duplicate feedback AO frozen and timeout not relaxed");

                var repeatThenFresh=Fixture(div);index=0;utc=DateTime.UtcNow.AddMilliseconds(-500);
                int timeIndex=0;int[] stamps={0,1,1,2,2,3,4};
                Feed(repeatThenFresh,div,()=>++index==1?0:5350,()=>utc.AddMilliseconds(stamps[Math.Min(timeIndex++,6)]));
                result=repeatThenFresh.Run();
                check(result.Ramp.Count==7&&result.RampStatistics.PrecisionRecheckReads==3&&result.RampStatistics.PrecisionWarning,"duplicate segments excluded between actual three new reads");

                var old=Fixture(div);old.Settings.PrecisionRampControl=false;index=0;
                Feed(old,div,()=>++index==1?0:5200,()=>DateTime.UtcNow);
                var oldError=Fail(()=>old.Run()) as FactoryRunException;
                check(oldError!=null&&oldError.PartialResult.RampStatistics.ExitCode=="Fault"&&old.Backend.ArmCount==0,"legacy 2% window retains old fault semantics");
            }
            foreach(string oldXml in new[]{UnifiedPressureBridge.CreateConfiguration(input,6000,30),
                UnifiedPressureBridge.CreateConfigurationWithReview(input,6000,30,8),
                UnifiedPressureBridge.CreateConfigurationWithFineRamp(input,6000,30,8,true,4.5),
                UnifiedPressureBridge.CreateConfigurationWithEndResidual(input,6000,30,8,false,4.5,4),
                UnifiedPressureBridge.CreateConfigurationWithFeedbackSource(input,6000,30,8,false,4.5,4,"VMON")})
            {
                var old=UnifiedPressureBridge.Read<FactorySettings>(oldXml);old.TargetKv=5;old.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                check(!old.PrecisionRampControl&&!old.UsePrecisionRamp&&old.RampUpperV==5100,"all historical APIs unchanged");
            }
            foreach(double value in new[]{Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,-1,0,5,50.0001})
            {
                var f=Fixture(true);f.Settings.RelativeOvervoltagePercent=value;
                check(Fail(()=>f.Run()) is ArgumentException&&f.Backend.InitializeCount==0,"invalid abnormal ratio rejected before initialization "+value);
            }
            foreach(double value in new[]{5.0001,20.0,50.0})
            {
                var f=Fixture(true);f.Settings.RelativeOvervoltagePercent=value;f.Settings.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                check(f.Settings.UsePrecisionRamp&&Math.Abs(f.Settings.PrecisionProtectionUpperV-5000*(1+value/100))<1e-8,"valid explicit ratio "+value);
            }
            var guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<3000;t+=50)guard.Observe(t,1000,new FactoryFeedback{ImonRawV=5},true);
            check(Fail(()=>guard.Observe(3000,1000,new FactoryFeedback{ImonRawV=5},true)) is InvalidOperationException,"original current plus no-growth 3s guard unchanged");
            guard=new FactoryRampCurrentGuard(5000,0);
            for(int t=0;t<=5500;t+=50)guard.Observe(t,1000,new FactoryFeedback{ImonRawV=4.9},true);
            check(true,"no new 5s voltage-only trip");
            return "Explicit new pressure precision/frozen AO/raw protection: "+n+" offline checks PASS; HardwareAccessed=False; SDKCalled=False";
        }
        private static FactoryTests.Fixture Fixture(bool div)
        {
            var f=new FactoryTests.Fixture();f.Settings.TargetKv=5;f.Settings.MaximumKv=f.Settings.MaximumFeedbackKv=10;
            f.Settings.DividerOnlyFeedback=div;f.Settings.EndAfterPressureCapture=true;f.Settings.VmonAfterHvStartup=true;
            f.Settings.PrecisionRampControl=true;f.Settings.ZeroBackgroundLimitV=500;return f;
        }
        private static void Feed(FactoryTests.Fixture fixture,bool div,Func<double> volts,Func<DateTime> timestamp)
        {
            fixture.Backend.FeedbackRule=(b,x)=>{
                double v=volts();x.TimestampUtc=timestamp();x.ImonRawV=10.06;
                // Unselected floating/high channel is diagnostic, never a stop.
                x.DividerRawV=div?-v/fixture.Settings.Input.DividerVPerV:10.06;
                x.VmonRawV=div?10.06:v*fixture.Settings.FeedbackFullScaleV/(fixture.Settings.VmonFullScaleKv*1000);
                return x;
            };
        }
        private static void SetRawBound(FactoryFeedback f,bool div,FactorySettings s,double peak,double halfCode)
        {
            f.SampleCount=2;
            f.ImonMinimumRawV=f.ImonMaximumRawV=f.ImonRawV;
            f.VmonMinimumRawV=f.VmonMaximumRawV=f.VmonRawV;
            f.DividerMinimumRawV=f.DividerMaximumRawV=f.DividerRawV;
            if(div){f.DividerMinimumRawV=-peak/s.Input.DividerVPerV;f.DividerHalfCodeV=halfCode/s.Input.DividerVPerV;}
            else{f.VmonMaximumRawV=peak*s.FeedbackFullScaleV/(s.VmonFullScaleKv*1000);f.VmonHalfCodeV=halfCode*s.FeedbackFullScaleV/(s.VmonFullScaleKv*1000);}
        }
        private static Exception Fail(Action action)
        {try{action();}catch(Exception error){return error;}throw new Exception("Expected pressure precision rejection.");}
    }
}
