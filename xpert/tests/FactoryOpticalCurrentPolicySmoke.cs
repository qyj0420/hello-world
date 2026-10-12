using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Xiongxin.FactoryTest;
using Xiongxin.ZeroBench;

// Test-only entry: existing managed fake suites, never Program/worker Main or SDK.
internal static class FactoryOpticalCurrentPolicySmoke
{
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static string Xml<T>(T value){using(var w=new StringWriter(CultureInfo.InvariantCulture)){new XmlSerializer(typeof(T)).Serialize(w,value);return w.ToString();}}
    static T Read<T>(string xml){using(var r=new StringReader(xml))return (T)new XmlSerializer(typeof(T)).Deserialize(r);}
    static void Expect(Action action,string text){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,text);}
    static void HoldCompatibilityAndBridge()
    {
        Check(BenchSettings.OpticalHoldMilliseconds==1000,"explicit new hold exactly 1000");
        Check(new BenchSettings().TriggerPulseMs==1000,"new input defaults 1000");
        var history=new BenchSettings{AutomaticSharedTrigger=true,TriggerPulseMs=1500,TriggerTimeoutMs=6000};
        history.Validate();Check(history.TriggerPulseMs==1500,"historical 1500 validates without rewriting");
        Expect(()=>history.ValidateForNewRun(),"historical shared trigger cannot authorize new 1500 pulse");
        Check(history.TriggerPulseMs==1500,"rejection does not rewrite recorded pulse width");
        history.TriggerPulseMs=1000;history.ValidateForNewRun();Check(true,"new shared 1000 is valid");
        history.TriggerPulseMs=1501;Expect(()=>history.Validate(),"beyond historical boundary rejected");
        history.TriggerPulseMs=0;Expect(()=>history.Validate(),"invalid width rejected");
        Check(!new FactorySettings().DesktopPostProcessing,"default factory full-analysis path retained");
        string input=Xml(new BenchSettings());
        var standard=Read<FactorySettings>(UnifiedPressureBridge.CreateConfiguration(input,6000,30));
        Check(!standard.DesktopPostProcessing,"historical bridge API stays full analysis");
        var desktop=Read<FactorySettings>(UnifiedPressureBridge.CreateConfigurationWithoutResidual(input,6000,30,1,false,4.5,"DIV"));
        Check(desktop.DesktopPostProcessing,"explicit desktop bridge enables saved-record pipeline");
        Check(desktop.Input.TriggerPulseMs==1000&&standard.Input.TriggerPulseMs==1000,"both new bridges carry actual one-second policy");
        Check(desktop.Clone().DesktopPostProcessing&&!standard.Clone().DesktopPostProcessing,"cloning preserves explicit/default distinction");
    }
    static void InterruptDuringCurrentHold()
    {
        foreach(string mode in new[]{"estop","cancel","read-failure"})
        {
            var f=new FactoryTests.Fixture();
            // Existing R30 injections at +1100 now exercise post-hold WAIT.
            // Independently retain coverage INSIDE the newly authorized hold.
            if(mode=="estop")f.Backend.EmergencyRule=b=>b.OpticalHighCount==0||f.Clock.ElapsedMilliseconds<b.OpticalAt+700;
            if(mode=="read-failure")f.Backend.EmergencyRule=b=>{if(b.OpticalHighCount>0&&f.Clock.ElapsedMilliseconds>=b.OpticalAt+700)throw new IOException("synthetic hold DI read failure");return true;};
            if(mode=="cancel")f.Clock.AfterPause=()=>{if(f.Backend.OpticalHighCount>0&&f.Clock.ElapsedMilliseconds>=f.Backend.OpticalAt+700)f.Cancellation.Cancel();};
            FactoryRunException error=null;try{f.Run();}catch(FactoryRunException e){error=e;}
            Check(error!=null,mode+" cannot become successful capture");
            Check(f.Backend.OpticalHighCount==1&&!f.Backend.Optical&&!f.Backend.Hv&&f.Backend.Ao==0,mode+" no second positive request and fake outputs low");
            var lows=f.Backend.Events.Where(e=>e.Name=="OPTICAL_LOW"&&e.At>=f.Backend.OpticalAt).ToArray();
            Check(lows.Length>0&&lows[lows.Length-1].At-f.Backend.OpticalAt>=700&&lows[lows.Length-1].At-f.Backend.OpticalAt<1000,mode+" terminates inside current hold");
            Check(!error.PartialResult.Completed&&!error.PartialResult.ResidualSafe&&f.Backend.DisposeCount==1,mode+" preserves failure/no-discharge and cleanup attempt");
        }
    }
    [STAThread] public static int Main(string[] args)
    {
        try{
            bool currentOnly=args.Length==1&&args[0]=="--current-optical-only";
            if(args.Length>0&&!currentOnly)throw new ArgumentException("Unknown test-only mode.");
            // Synthetic archives stay in the isolated QA directory, not a
            // machine-global temporary location or any user's project.
            string temporary=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"synthetic-temp");
            Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP",temporary,EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TMP",temporary,EnvironmentVariableTarget.Process);
            HoldCompatibilityAndBridge();InterruptDuringCurrentHold();
            Console.WriteLine("OPTICAL_CURRENT_POLICY_FOCUSED_PASS="+checks+";HardwareAccessed=false;SDKCalled=false");
            if(currentOnly){
                Console.WriteLine(FactoryR30Tests.Run());
                Console.WriteLine("FACTORY_OPTICAL_CURRENT_R30_PARTIAL_PASS;ExcludedLegacySuites=4;FullRegression=false;WorkerMain=false;ProductMain=false;PhysicalAcceptance=false");
                return 0;
            }
            int failed=0;
            Action<string,Action> suite=(name,run)=>{try{run();}catch(Exception e){failed++;Console.Error.WriteLine("SUITE_FAILED="+name+";"+e);}};
            suite("FactoryTests",()=>FactoryTests.Run(Console.Out));
            suite("FactoryManualTests",()=>Console.WriteLine(FactoryManualTests.Run()));
            suite("FactoryZeroTriggerTests",()=>Console.WriteLine(FactoryZeroTriggerTests.Run()));
            suite("FactoryFinalAi4WorkflowTests",()=>Console.WriteLine(FactoryFinalAi4WorkflowTests.Run()));
            suite("FactoryR30Tests",()=>Console.WriteLine(FactoryR30Tests.Run()));
            if(failed>0){Console.Error.WriteLine("FACTORY_OPTICAL_SUITE_FAILURE_COUNT="+failed);return 1;}
            Console.WriteLine("FACTORY_OPTICAL_LEGACY_DEFAULT_SUITES_PASS;WorkerMain=false;ProductMain=false;PhysicalAcceptance=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
