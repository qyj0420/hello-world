using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Explicit offline harness: only immutable data readers, the pinned algorithm
// Python process and managed result adaptation. No product/UI/worker/SDK.
static class R58SingleLocAlgorithmPerformance
{
    const BindingFlags S=BindingFlags.Static|BindingFlags.NonPublic;
    const BindingFlags I=BindingFlags.Instance|BindingFlags.NonPublic;
    static Assembly module;
    static object Call(string type,string method,params object[] values)
    {try{return module.GetType("HVX.PdReview."+type,true).GetMethod(method,S).Invoke(null,values);}catch(TargetInvocationException e){throw e.InnerException??e;}}
    static object Field(object value,string name){return value.GetType().GetField(name,I).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,I).SetValue(value,data);}
    static string Hash(string path){using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128};}
    static int Main(string[] args)
    {
        try{
            if(args.Length!=4)throw new ArgumentException("module, readonly LOC, runtime parent, fresh QA output required");
            Console.OutputEncoding=new UTF8Encoding(false);
            string dll=Path.GetFullPath(args[0]),loc=Path.GetFullPath(args[1]),runtime=Path.GetFullPath(args[2]),qa=Path.GetFullPath(args[3]);
            if(Directory.Exists(qa))throw new IOException("Fresh output required");Directory.CreateDirectory(qa);
            string before=Hash(loc);module=Assembly.LoadFrom(dll);
            string project=Path.GetDirectoryName(loc),cable=Path.Combine(project,"CABLE.in");
            var trials=new List<object>();string samplesHash=null;int rows=0;
            for(int trial=0;trial<3;trial++){
                var stages=new Dictionary<string,double>();var totalTimer=Stopwatch.StartNew();var timer=Stopwatch.StartNew();
                object data=Call("ReviewLocationFileReader","Read",loc,project);
                stages["strict_read_ms"]=timer.Elapsed.TotalMilliseconds;timer.Restart();
                double[] samples=(double[])Field(data,"ChargeSamples");
                double fs=(double)Field(data,"SampleRateHz"),speed=(double)Field(data,"VelocityMPerUs");
                var geometry=new object[]{cable,"C",speed,0d,0d};
                module.GetType("HVX.PdReview.ReviewEProjectInput",true).GetMethod("ReadGeometry",S).Invoke(null,geometry);
                double length=(double)geometry[3];speed=(double)geometry[4];
                var files=Directory.GetFiles(project,"*",SearchOption.TopDirectoryOnly);
                string dem=(string)Call("ReviewEProjectInput","MatchedDem",files,loc,"C",(double)Field(data,"VoltageKv"));
                double frequency=String.IsNullOrEmpty(dem)?Double.NaN:(double)Call("ReviewEProjectInput","ReadDemFrequency",dem);
                stages["project_inputs_ms"]=timer.Elapsed.TotalMilliseconds;timer.Restart();
                var settings=new Dictionary<string,object>{{"manual_threshold_enabled",false},{"manual_threshold_pc",100d},{"detection_factor",3},{"envelope_tolerance",15d}};
                var config=new Dictionary<string,object>{{"sample_rate_hz",fs},{"cable_length_m",length},{"velocity_m_per_us",speed},{"settings",settings}};
                var payload=new Dictionary<string,object>{{"algorithm","E+"},{"config",config},{"records",new object[]{new Dictionary<string,object>{{"record_id",Path.GetFileName(loc)},{"samples",samples}}}}};
                var task=(Task<string>)Call("ReviewERuntime","RunAsync",runtime,null,payload,CancellationToken.None,null,180);
                string result=task.GetAwaiter().GetResult();
                stages["pinned_runtime_process_and_envelope_ms"]=timer.Elapsed.TotalMilliseconds;timer.Restart();
                var evidence=Json().DeserializeObject(result) as Dictionary<string,object>;
                stages["managed_evidence_json_decode_ms"]=timer.Elapsed.TotalMilliseconds;timer.Restart();
                object request=Activator.CreateInstance(module.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);
                Set(request,"ProjectPath",project);Set(request,"LocationFilePath",loc);Set(request,"LocationDataId",47L);Set(request,"AlgorithmIndex",9);
                Set(request,"AutomaticThresholdMultiplier",3d);Set(request,"AttenuationTolerancePercent",15d);Set(request,"MaxPulses",30);
                object adapted=Call("ReviewEAnalysis","Adapt",request,data,3,length,speed,frequency,evidence);
                stages["managed_adapt_FT_and_pack_ledger_ms"]=timer.Elapsed.TotalMilliseconds;
                double measuredTotalMs=totalTimer.Elapsed.TotalMilliseconds;
                rows=((double[,])Field(adapted,"Parameters")).GetLength(0);
                byte[] binary=new byte[samples.Length*8];Buffer.BlockCopy(samples,0,binary,0,binary.Length);
                using(var sha=SHA256.Create())samplesHash=BitConverter.ToString(sha.ComputeHash(binary)).Replace("-","");
                if(trial==0){
                    File.WriteAllBytes(Path.Combine(qa,"samples.f64le"),binary);
                    File.WriteAllText(Path.Combine(qa,"request.json"),Json().Serialize(new Dictionary<string,object>{{"algorithm","E+"},{"config",config},{"records",new object[]{new Dictionary<string,object>{{"record_id",Path.GetFileName(loc)},{"sample_count",samples.Length}}}}}),new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(qa,"compiled-result.json"),result,new UTF8Encoding(false));
                }
                trials.Add(new{trial=trial,stages=stages,total_ms=measuredTotalMs,stage_sum_ms=stages.Values.Sum(),samples=samples.Length,fs=fs,length_m=length,speed_m_per_us=speed,frequency_hz=frequency,rows=rows,events=((object[])evidence["events"]).Length,attempts=((object[])evidence["attempts"]).Length,status_chars=((string)Field(adapted,"Status")).Length,result_chars=result.Length});
                Console.WriteLine("trial="+trial+" "+Json().Serialize(stages));
            }
            if(before!=Hash(loc))throw new IOException("Readonly source changed");
            File.WriteAllText(Path.Combine(qa,"managed-timings.json"),Json().Serialize(new{schema="HVX.R58.SingleLOC.Performance/1",passed=true,module=dll,module_sha256=Hash(dll),source=loc,source_sha256=before,samples_sha256=samplesHash,trials=trials,measured_scope="Strict LOC read through project input resolution, runtime, result decode and Adapt/FT/ledger packaging. Does not include UI rendering or archive persistence.",ui_end_to_end_measured=false,product_started=false,ui_constructed=false,sdk_called=false,hardware_used=false,user_data_written=false}),new UTF8Encoding(false));
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{if(module!=null)try{Call("ReviewERuntime","Shutdown");}catch{ }}
    }
}
