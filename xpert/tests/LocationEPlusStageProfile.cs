using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Independent algorithm-only host: no product/control/SDK/worker construction.
internal static class LocationEPlusStageProfile
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static object Get(object obj,string field){return obj.GetType().GetField(field,Flags).GetValue(obj);}
    static void Set(object obj,string field,object value){obj.GetType().GetField(field,Flags).SetValue(obj,value);}
    static object Call(Type type,string name,params object[] args){return type.GetMethod(name,Flags).Invoke(null,args);}
    static string Hash(string path){using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
    static int Main(string[] args)
    {
        try {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);
            Console.InputEncoding=new System.Text.UTF8Encoding(false);
            if(args.Length!=3)throw new ArgumentException("Expected QA module, frozen runtime modules and read-only LOC.");
            string dll=Path.GetFullPath(args[0]),modules=Path.GetFullPath(args[1]),loc=Path.GetFullPath(args[2]),root=Path.GetDirectoryName(loc);
            var inputs=new Dictionary<string,string>();
            foreach(string path in new[]{dll,loc,Path.Combine(root,"CABLE.in")})inputs[path]=Hash(path);
            var assembly=Assembly.LoadFrom(dll);
            var reader=assembly.GetType("HVX.PdReview.ReviewLocationFileReader",true);
            var project=assembly.GetType("HVX.PdReview.ReviewEProjectInput",true);
            var runtime=assembly.GetType("HVX.PdReview.ReviewERuntime",true);
            var adapter=assembly.GetType("HVX.PdReview.ReviewEAnalysis",true);
            object request=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);
            Set(request,"ProjectPath",root);Set(request,"LocationFilePath",loc);Set(request,"AlgorithmIndex",9);
            Set(request,"MaxPulses",30);Set(request,"AutomaticThresholdMultiplier",5d);Set(request,"AttenuationTolerancePercent",16d);
            var stages=new Dictionary<string,double>();var total=Stopwatch.StartNew();var watch=Stopwatch.StartNew();
            using(var guard=new FileStream(loc,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var cableGuard=new FileStream(Path.Combine(root,"CABLE.in"),FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var cancellation=new CancellationTokenSource(60000)) {
                object data=Call(reader,"Read",loc,root);stages["strict_LOC_read_ms"]=watch.Elapsed.TotalMilliseconds;watch.Restart();
                double[] samples=(double[])Get(data,"ChargeSamples");double rate=(double)Get(data,"SampleRateHz");
                Set(data,"VoltageKv",Call(project,"Voltage",loc,Get(data,"VoltageKv")));
                string phase=Path.GetFileNameWithoutExtension(loc).Split('_')[0].ToUpperInvariant();
                phase=phase=="L1"?"A":phase=="L2"?"B":phase=="L3"?"C":phase=="COMMON"?"ALL":phase;
                int phaseCode=phase=="A"?1:phase=="B"?2:phase=="C"?3:phase=="ALL"?0:-1;
                if(phaseCode<0)throw new InvalidDataException("Explicit phase required.");
                object[] geometry={Path.Combine(root,"CABLE.in"),phase,Get(data,"VelocityMPerUs"),0d,0d};
                project.GetMethod("ReadGeometry",Flags).Invoke(null,geometry);double length=(double)geometry[3],speed=(double)geometry[4];
                string[] files=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly);
                string dem=(string)Call(project,"MatchedDem",files,loc,phase,Get(data,"VoltageKv"));double frequency=Double.NaN;
                if(!String.IsNullOrEmpty(dem)) {inputs[dem]=Hash(dem);using(var demGuard=new FileStream(dem,FileMode.Open,FileAccess.Read,FileShare.Read))frequency=(double)Call(project,"ReadDemFrequency",dem);}
                Call(project,"ValidatePhaseClock",frequency,rate,samples.Length);
                stages["geometry_and_phase_inputs_ms"]=watch.Elapsed.TotalMilliseconds;watch.Restart();
                var settings=new Dictionary<string,object>{{"manual_threshold_enabled",false},{"manual_threshold_pc",0d},{"detection_factor",5},{"envelope_tolerance",16d}};
                var payload=new Dictionary<string,object>{{"algorithm","E+"},{"config",new Dictionary<string,object>{{"sample_rate_hz",rate},{"cable_length_m",length},{"velocity_m_per_us",speed},{"settings",settings}}},
                    {"records",new object[]{new Dictionary<string,object>{{"record_id",Path.GetFileName(loc)},{"samples",samples}}}}};
                var task=(Task<string>)runtime.GetMethod("RunAsync",Flags).Invoke(null,new object[]{modules,null,payload,cancellation.Token,null,60});
                string json=task.GetAwaiter().GetResult();stages["pinned_runtime_process_and_Eplus_compute_ms"]=watch.Elapsed.TotalMilliseconds;watch.Restart();
                var serializer=(JavaScriptSerializer)Call(runtime,"Serializer");
                var evidence=(Dictionary<string,object>)serializer.DeserializeObject(json);
                stages["evidence_json_decode_ms"]=watch.Elapsed.TotalMilliseconds;watch.Restart();
                object result=Call(adapter,"Adapt",request,data,phaseCode,length,speed,frequency,evidence);
                stages["matrix_adaptation_and_full_ledger_pack_ms"]=watch.Elapsed.TotalMilliseconds;total.Stop();
                if(!(bool)Get(result,"Success"))throw new InvalidDataException("Adaptation unsuccessful.");
                foreach(var input in inputs)if(Hash(input.Key)!=input.Value)throw new IOException("Read-only input changed.");
                Console.WriteLine(serializer.Serialize(new{status="PASS",stages,totalMs=total.Elapsed.TotalMilliseconds,
                    samples=samples.Length,rate,length,speed,frequency,rows=((double[,])Get(result,"Parameters")).GetLength(0),
                    events=((object[])evidence["events"]).Length,attempts=((object[])evidence["attempts"]).Length,
                    responseCharacters=json.Length,statusCharacters=((string)Get(result,"Status")).Length,sourceHashes=inputs,
                    productStarted=false,uiConstructed=false,hardwareWorkerStarted=false,sdkCalled=false,hardwareUsed=false,
                    independentPinnedAlgorithmProcessStarted=true,physicalAcceptance=false,
                    scope="Single-read E+ file pipeline stages; runtime phase includes all original pins checks, isolated Python startup/transport and original algorithm. Excludes UI publication and engine snapshot."}));
            }
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
