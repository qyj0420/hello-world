using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

internal static class R26PressureBridgeSmoke
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
            Set(input,"PicoCoupling",0);Set(input,"ArmDelayMs",500);Set(input,"RangeIndex",4);
            string inputXml=Xml(input);
            Type bridge=assembly.GetType("Xiongxin.FactoryTest.UnifiedPressureBridge",true);
            string config=(string)bridge.GetMethod("CreateConfiguration").Invoke(null,new object[]{inputXml,15000d,20d});
            var settingsType=assembly.GetType("Xiongxin.FactoryTest.FactorySettings",true);
            object settings=Read(settingsType,config);
            Check((bool)Get(settings,"Simulation"),"integration test strictly uses simulation");
            Check(!(bool)Get(settings,"IndependentDischargeConfirmed")&&!(bool)Get(settings,"FeedbackWiringConfirmed"),"shared config never contains operator approval");
            Check((bool)Get(settings,"VmonAfterHvStartup")&&(double)Get(settings,"StartupVmonLimitV")==500d&&(double)Get(settings,"ZeroBackgroundLimitV")==500d,"R26 post-HV 500 V background mode");
            Check((double)Get(settings,"SafeResidualV")==50d&&(bool)Get(settings,"EndAfterPressureCapture"),"R26 normal capture-only and fault 50 V retained");
            Check((double)Get(settings,"MaximumAoV")==5d,"AO ceiling retained");
            var kind=Enum.Parse(assembly.GetType("Xiongxin.FactoryTest.FactoryRunKind"),"Pressure");
            var accept=assembly.GetType("Xiongxin.FactoryTest.FactoryStartConfirmation").GetMethod("Accept",BindingFlags.Static|BindingFlags.NonPublic);
            // Pure settings validation only: no backend is constructed for real mode.
            Set(settings,"Simulation",false);Set(settings,"TargetKv",5d);
            Check(accept.Invoke(null,new[]{settings,kind,(object)false})==null,"decline returns no authorized settings");
            var approved=accept.Invoke(null,new[]{settings,kind,(object)true});
            Check((bool)Get(approved,"FeedbackWiringConfirmed")&&(bool)Get(approved,"IndependentDischargeConfirmed"),"one explicit confirmation supplies both R26 assertions");
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
            Check((bool)Get(result,"AcquisitionCompleted")&&(bool)Get(result,"PostCaptureMonitoringSkipped"),"R26 capture-only completion propagated");
            Check(!(bool)Get(result,"Completed")&&!(bool)Get(result,"ResidualSafe"),"data completion never becomes safety acceptance");
            Check(progress.Contains("STEP_DISCHARGE_SKIPPED")&&!progress.Contains("STEP_DISCHARGE_PASSED"),"no fabricated residual measurement");
            Check(!progress.Contains("EMERGENCY_RELEASED"),"simulation never publishes real emergency-stop permission");
            Check(!(bool)Get(Read(settingsType,config),"IndependentDischargeConfirmed"),"later rounds have no inherited confirmation");
            Console.WriteLine("R26_PRESSURE_BRIDGE_PASS checks="+checks+"; hardwareAccessed=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
