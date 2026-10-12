using System;
using System.Linq;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryZeroTriggerTests
    {
        internal static string Run()
        {
            int n=0; Action<bool,string> check=(ok,text)=>{if(!ok)throw new Exception("Zero optical: "+text);n++;};
            var f=Fixture();var result=Run(f);
            check(result.Completed && result.ZeroTriggerDiagnostic && !result.ReadOnlyAcquisition && !result.ResidualSafe && result.CompleteCaptureAvailable(),"diagnostic complete without discharge claim");
            check(f.Backend.HvHighCount==0 && f.Backend.PositiveAoWrites==0 && !f.Backend.Events.Any(e=>e.Name=="HV_LOW" || e.Name=="AO_ZERO"),"no HV ON/OFF or AO command");
            check(!f.Backend.FreeRun && f.Backend.OpticalHighCount==1 && f.Backend.ReadCount==1,"external mode, exactly one optical command, complete data");
            check(f.Backend.OpticalAt-f.Backend.Events.First(e=>e.Name=="ARM_EXT").At==500,"500 ms starts at arming before optical, no HV OFF");
            check(f.Backend.Events.First(e=>e.Name=="OPTICAL_LOW" && e.At>=f.Backend.OpticalAt+1000).At-f.Backend.OpticalAt==1000,"zero optical diagnostic holds exactly 1000 ms in fake clock");
            check(result.Capture.Runtime.Contains("ZERO_EXTERNAL_OPTICAL_DIAGNOSTIC"),"capture mode persisted");
            result.Settings.TargetKv=1;check(!result.CompleteCaptureAvailable(),"diagnostic cannot authorize nonzero safety result");
            f=Fixture();f.Settings.ZeroSynchronizedOptical=false;Fails(()=>Run(f));
            check(f.Backend.InitializeCount==0,"explicit opt-in required");
            f=Fixture();f.Settings.TargetKv=1;Fails(()=>Run(f));check(f.Backend.InitializeCount==0,"nonzero rejected before initialization");
            f=Fixture();f.Settings.Simulation=false;f.Settings.FeedbackWiringConfirmed=false;Fails(()=>Run(f));check(f.Backend.InitializeCount==0,"real optical mode requires separate safety acknowledgement");
            f=Fixture();f.Backend.EmergencyRule=b=>false;Fails(()=>Run(f));check(f.Backend.OpticalHighCount==0 && f.Backend.DisposeCount==1,"initial low forbids light and cleans resources");
            f=Fixture();f.Backend.FeedbackRule=(b,v)=>{v.DividerRawV=1;return v;};Fails(()=>Run(f));check(f.Backend.OpticalHighCount==0,"unsafe residual cannot be discharged through background mode");
            f=Fixture();f.Backend.FailArm=true;Fails(()=>Run(f));check(f.Backend.OpticalHighCount==0,"failed configuration never emits light");
            f=Fixture();f.Backend.PrematureComplete=true;Fails(()=>Run(f));check(f.Backend.OpticalHighCount==0 && f.Backend.ReadCount==1,"early complete diagnostic retained without another light");
            f=Fixture();f.Backend.NeverComplete=true;Fails(()=>Run(f));check(f.Backend.OpticalHighCount==1 && f.Backend.ReadCount==0 && !f.Backend.Optical,"timeout does not repeat pulse or fabricate data");
            f=Fixture();var cancel=f;f.Clock.AfterPause=()=>{if(cancel.Backend.Armed)cancel.Cancellation.Cancel();};Fails(()=>Run(cancel));check(f.Backend.OpticalHighCount==0,"cancel during 500 ms wait emits no light");
            f=Fixture();var during=f;f.Clock.AfterPause=()=>{if(during.Backend.Optical)during.Cancellation.Cancel();};Fails(()=>Run(during));check(f.Backend.OpticalHighCount==1 && !f.Backend.Optical && f.Backend.DisposeCount==1,"cancel during light returns low and releases");
            f=Fixture();f.Backend.EmergencyRule=b=>!b.Optical;Fails(()=>Run(f));check(f.Backend.OpticalHighCount==1 && !f.Backend.Optical,"emergency during light returns low without retry");
            f=Fixture();f.Backend.FailRead=true;Fails(()=>Run(f));check(f.Backend.OpticalHighCount==1 && f.Backend.DisposeCount==1,"read failure never re-fires");
            f=Fixture();f.Backend.FailDispose=true;Fails(()=>Run(f));check(f.Backend.OpticalHighCount==1,"cleanup failure not accepted or repeated");
            return "R15 zero external optical: "+n+" offline checks PASS; HardwareAccessed=False";
        }
        private static FactoryTests.Fixture Fixture()
        {var f=new FactoryTests.Fixture();f.Settings.TargetKv=0;f.Settings.ZeroSynchronizedOptical=true;return f;}
        private static FactoryResult Run(FactoryTests.Fixture f)
        {return FactoryZeroTriggerRunner.Run(f.Settings,f.Backend,f.Clock,null,f.Cancellation.Token);}
        private static void Fails(Action action)
        {try{action();}catch{return;}throw new Exception("Expected zero optical rejection.");}
    }
}
