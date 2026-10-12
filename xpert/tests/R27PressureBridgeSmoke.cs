using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

internal static class R27PressureBridgeSmoke
{
    static int checks;
    static void Check(bool value,string message){++checks;if(!value)throw new Exception(message);}
    static void Set(object value,string name,object next){value.GetType().GetField(name).SetValue(value,next);}
    static object Get(object value,string name){return value.GetType().GetField(name).GetValue(value);}
    static string Xml(object value){using(var text=new StringWriter()){new XmlSerializer(value.GetType()).Serialize(text,value);return text.ToString();}}
    static object Read(Type type,string text){using(var reader=new StringReader(text))return new XmlSerializer(type).Deserialize(reader);}
    static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe"));
            var input=Activator.CreateInstance(assembly.GetType("Xiongxin.ZeroBench.BenchSettings",true));
            Set(input,"RealHardware",false);Set(input,"ArtSerial","QA-ONLY-ART");Set(input,"PicoSerial","QA-ONLY-PICO");
            Set(input,"OscillationCircuitConnected",true);Set(input,"PicoCoupling",0);Set(input,"ArmDelayMs",500);Set(input,"RangeIndex",4);
            string inputXml=Xml(input);
            Type bridge=assembly.GetType("Xiongxin.FactoryTest.UnifiedPressureBridge",true);
            string config=(string)bridge.GetMethod("CreateConfiguration").Invoke(null,new object[]{inputXml,15000d,20d});
            var settingsType=assembly.GetType("Xiongxin.FactoryTest.FactorySettings",true);
            object settings=Read(settingsType,config);
            Check((bool)Get(settings,"Simulation"),"integration test strictly uses simulation");
            Check(!(bool)Get(settings,"IndependentDischargeConfirmed")&&!(bool)Get(settings,"FeedbackWiringConfirmed"),"shared config never contains operator approval");
            Check((bool)Get(settings,"VmonAfterHvStartup")&&(double)Get(settings,"StartupVmonLimitV")==500d&&(double)Get(settings,"ZeroBackgroundLimitV")==500d,"R27 post-HV 500 V background mode");
            Check((double)Get(settings,"SafeResidualV")==50d&&(bool)Get(settings,"EndAfterPressureCapture"),"R27 normal capture-only and fault 50 V retained");
            Check((double)Get(settings,"MaximumAoV")==5d,"AO ceiling retained");
            Check((double)Get(settings,"TargetTolerancePercent")==2d,"explicit target tolerance is two percent");
            Set(settings,"TargetKv",15d);
            Check((double)settingsType.GetProperty("RampUpperV").GetValue(settings,null)==15300d &&
                (double)Get(settings,"TargetKv")*1000*(1-(double)Get(settings,"TargetTolerancePercent")/100)==14700d,"15 kV target accepts 14.7 to 15.3 kV");
            Set(settings,"TargetKv",20d);
            Check((double)settingsType.GetProperty("RampUpperV").GetValue(settings,null)==20000d,"target tolerance never overrides independent ceiling");
            var kind=Enum.Parse(assembly.GetType("Xiongxin.FactoryTest.FactoryRunKind"),"Pressure");
            var accept=assembly.GetType("Xiongxin.FactoryTest.FactoryStartConfirmation").GetMethod("Accept",BindingFlags.Static|BindingFlags.NonPublic);
            // Pure settings validation only: no backend is constructed for real mode.
            Set(settings,"Simulation",false);Set(settings,"TargetKv",5d);
            Check(accept.Invoke(null,new[]{settings,kind,(object)false})==null,"decline returns no authorized settings");
            var approved=accept.Invoke(null,new[]{settings,kind,(object)true});
            Check((bool)Get(approved,"FeedbackWiringConfirmed")&&(bool)Get(approved,"IndependentDischargeConfirmed"),"one explicit confirmation supplies both R27 assertions");
            Check(!(bool)Get(settings,"FeedbackWiringConfirmed")&&!(bool)Get(settings,"IndependentDischargeConfirmed"),"accept uses isolated clone");
            var capture=bridge.GetMethod("CaptureAsync");
            var progress=new List<string>();Action<string,string> report=(key,text)=>{lock(progress)progress.Add(key);};
            bool rejected=false;
            try { ((Task<string>)capture.Invoke(null,new object[]{config,inputXml,5d,Path.Combine(qa,"declined"),false,report,CancellationToken.None})).GetAwaiter().GetResult(); }
            catch(OperationCanceledException){rejected=true;}
            Check(rejected&&!Directory.Exists(Path.Combine(qa,"declined")),"decline creates no worker or raw round");
            using(var stop=new CancellationTokenSource()){
                stop.Cancel();rejected=false;
                try { ((Task<string>)capture.Invoke(null,new object[]{config,inputXml,5d,Path.Combine(qa,"cancelled"),true,report,stop.Token})).GetAwaiter().GetResult(); }
                catch(OperationCanceledException){rejected=true;}
                Check(rejected&&!Directory.Exists(Path.Combine(qa,"cancelled")),"cancel before worker creates no round");
            }
            string directory=Path.Combine(qa,"simulation-capture");
            string returned=((Task<string>)capture.Invoke(null,new object[]{config,inputXml,5d,directory,true,report,CancellationToken.None})).GetAwaiter().GetResult();
            Check(returned==directory&&File.Exists(Path.Combine(directory,"capture.bin")),"simulated subprocess launched runtime assembly not QA host and returned raw capture");
            var resultType=assembly.GetType("Xiongxin.FactoryTest.FactoryResult",true);
            object result=Read(resultType,File.ReadAllText(Path.Combine(directory,"result.xml")));
            Check((bool)Get(result,"AcquisitionCompleted")&&(bool)Get(result,"PostCaptureMonitoringSkipped"),"R27 capture-only completion propagated");
            Check(!(bool)Get(result,"Completed")&&!(bool)Get(result,"ResidualSafe"),"data completion never becomes safety acceptance");
            Check(progress.Contains("STEP_DISCHARGE_SKIPPED")&&!progress.Contains("STEP_DISCHARGE_PASSED"),"no fabricated residual measurement");
            Check(!progress.Contains("EMERGENCY_RELEASED"),"simulation never publishes real emergency-stop permission");
            Check(!(bool)Get(Read(settingsType,config),"IndependentDischargeConfirmed"),"later rounds have no inherited confirmation");
            string reviewXml=(string)bridge.GetMethod("ReviewCapturedRound").Invoke(null,new object[]{directory,inputXml,5d});
            var review=Read(assembly.GetType("Xiongxin.FactoryTest.UnifiedPressureRangeReview",true),reviewXml);
            var quality=Get(review,"Quality");
            Check((string)Get(review,"Schema")=="HVX.Pressure.RangeReview/R27","explicit frozen review protocol");
            Check((bool)Get(quality,"WindowValid")&&(int)Get(quality,"WindowStartSample")>0,"nonzero actual first-zero window reaches unified review");
            Check((int)Get(quality,"LocationSampleCount")<(int)Get(quality,"FullSampleCount"),"nonzero review is not whole zero-voltage segment");
            bool mismatch=false;
            try { bridge.GetMethod("ReviewCapturedRound").Invoke(null,new object[]{directory,inputXml,6d}); }
            catch(TargetInvocationException e){mismatch=e.InnerException is InvalidDataException;}
            Check(mismatch,"mismatched target rejected without SDK access");
            string transient=(string)assembly.GetType("Xiongxin.FactoryTest.FactoryTransientRangeTests",true).GetMethod("Run",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            Check(transient.Contains("PASS"),"frozen R27 prefix/late-clipping/maximum-range/emergency policy cases");
            Console.WriteLine(transient);
            Console.WriteLine("R27_PRESSURE_BRIDGE_PASS checks="+checks+"; hardwareAccessed=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
