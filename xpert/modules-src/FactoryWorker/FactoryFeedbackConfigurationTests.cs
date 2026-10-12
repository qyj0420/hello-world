using System;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryFeedbackConfigurationTests
    {
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Feedback configuration: "+why);n++;};
            string input=UnifiedPressureBridge.Xml(new FactoryTests.Fixture().Settings.Input);
            foreach(string source in new[]{"DIV","VMON"})
            {
                string xml=UnifiedPressureBridge.CreateConfigurationWithFeedbackSource(input,6000,30,8,false,4.5,4,source);
                var s=UnifiedPressureBridge.Read<FactorySettings>(xml);
                check(s.DividerOnlyFeedback==(source=="DIV"),"selected source reaches serialized worker configuration "+source);
                check(s.EndResidualCheckEnabled&&s.EndResidualThresholdV==1200,"end percentage independent of source "+source);
                check(!s.PressureFeedbackContext,"XML configuration is not an active pressure context "+source);
                s.PressureFeedbackContext=true;s.TargetKv=0;
                var clone=s.Clone();check(clone.PressureFeedbackContext,"in-process clone retains actual context "+source);
                var loaded=UnifiedPressureBridge.Read<FactorySettings>(UnifiedPressureBridge.Xml(s));
                check(!loaded.PressureFeedbackContext,"saved/XML settings cannot claim runtime pressure context "+source);
                var forged=UnifiedPressureBridge.Read<FactorySettings>(xml.Replace("</FactorySettings>","<PressureFeedbackContext>true</PressureFeedbackContext></FactorySettings>"));
                check(!forged.PressureFeedbackContext,"unknown XML runtime flag is ignored "+source);
                var raw=new double[s.FeedbackWindowMs*3];
                for(int i=0;i<s.FeedbackWindowMs;i++)
                {raw[i*3]=10.06;raw[i*3+1]=source=="DIV"?10.06:0;raw[i*3+2]=source=="DIV"?0:10.06;}
                var plan=new FactoryArtFeedbackPlan(clone);var f=plan.Parse(raw,s.FeedbackWindowMs,0,true,0);
                FactoryRunner.ValidateFeedback(f,clone,true);
                check(FactoryRunner.SelectedVoltage(f,clone)==0,"zero plan uses selected source, floating diagnostic 10.06 V retained "+source);
                check(f.ImonRawV==10.06,"IMON 10.06 V is not a single-point current stop "+source);
                f.EmergencyReleased=false;Reject(()=>FactoryRunner.ValidateFeedback(f,clone,true));n++;
                raw[(s.FeedbackWindowMs-1)*3+(source=="DIV"?2:1)]=Double.NaN;
                Reject(()=>plan.Parse(raw,s.FeedbackWindowMs,0,true,0));n++;
                raw[(s.FeedbackWindowMs-1)*3+(source=="DIV"?2:1)]=10.06;
                Reject(()=>plan.Parse(raw,s.FeedbackWindowMs,0,true,0));n++;
                var zero=new FactoryTests.Fixture();zero.Settings.TargetKv=0;
                var result=FactoryZeroInputRunner.Run(zero.Settings,new SimulationFactoryZeroInput(zero.Clock),zero.Clock,(k,t)=>{},CancellationToken.None);
                check(result.Completed&&result.ReadOnlyAcquisition&&result.Settings.PressureFeedbackContext,"actual supervised zero-input runner derives pressure context "+source);
                zero=new FactoryTests.Fixture();zero.Settings.TargetKv=0;zero.Settings.ManualZeroInputWithoutEmergency=true;
                result=FactoryZeroInputRunner.Run(zero.Settings,new SimulationFactoryZeroInput(zero.Clock),zero.Clock,(k,t)=>{},CancellationToken.None);
                check(result.Completed&&!result.Settings.PressureFeedbackContext,"manual zero retains independent input-only context "+source);
            }
            foreach(string invalid in new[]{null,"","DIV ","div","Vmp","Divider","VMonitor","OTHER"})
            {Reject(()=>UnifiedPressureBridge.CreateConfigurationWithFeedbackSource(input,6000,30,8,false,4.5,4,invalid));n++;}
            foreach(string oldXml in new[]{UnifiedPressureBridge.CreateConfiguration(input,6000,30),
                UnifiedPressureBridge.CreateConfigurationWithReview(input,6000,30,8),
                UnifiedPressureBridge.CreateConfigurationWithFineRamp(input,6000,30,8,false,4.5),
                UnifiedPressureBridge.CreateConfigurationWithEndResidual(input,6000,30,8,false,4.5,4)})
                check(UnifiedPressureBridge.Read<FactorySettings>(oldXml).DividerOnlyFeedback,"legacy bridge names keep DIV contract");
            check(typeof(UnifiedPressureBridge).GetMethod("CreateConfigurationWithFeedbackSource").GetParameters().Length==8,"new distinct reflected method is unambiguous");
            check(typeof(UnifiedPressureBridge).GetMethod("CreateConfigurationWithEndResidual").GetParameters().Length==7,"old reflected method unchanged");
            var real=new FactoryTests.Fixture().Settings;real.Simulation=false;
            real.VmonAfterHvStartup=true;real.FeedbackWiringConfirmed=real.IndependentDischargeConfirmed=real.CalibrationSourceConfirmed=true;
            check(!FactoryNativeSessionPolicy.PrepareOpen(real).PressureFeedbackContext,"idle native opening cannot claim pressure run");
            check(FactoryNativeSessionPolicy.PrepareRound(real,false).PressureFeedbackContext,"native pressure round derives context");
            real.PressureFeedbackContext=true;
            Reject(()=>FactoryNativeSessionPolicy.PrepareRound(real,true));n++;
            real.TargetKv=0;
            check(!FactoryNativeSessionPolicy.PrepareRound(real,true).PressureFeedbackContext,"zero calibration clears caller-supplied pressure context");
            check(FactoryNativeSessionPolicy.PrepareZeroInputRound(real).PressureFeedbackContext,"queued supervised zero has pressure context");
            real.ManualZeroInputWithoutEmergency=true;
            check(!FactoryNativeSessionPolicy.PrepareZeroInputRound(real).PressureFeedbackContext,"queued manual zero does not change semantics");
            var calibration=new FactoryTests.Fixture();calibration.Settings.PressureFeedbackContext=true;
            calibration.Backend.FeedbackRule=(b,x)=>{x.ImonRawV=10.06;return x;};
            Reject(()=>calibration.Run(null,FactoryRunKind.Calibration));
            check(calibration.Backend.PositiveAoWrites==0&&calibration.Backend.HvHighCount==0&&calibration.Backend.OpticalHighCount==0,"calibration never uses pressure diagnostics exception or activates output");
            return "Bridge feedback source + runtime pressure context: "+n+" offline checks PASS; HardwareAccessed=False; SDKCalled=False";
        }
        private static void Reject(Action action)
        {try{action();}catch(Exception){return;}throw new Exception("Expected feedback configuration rejection.");}
    }
}
