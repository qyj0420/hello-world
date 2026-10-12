using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using HVX.PdReview;

// Test executable only: never opens a product window, a worker or a device.
static class LocationEIntegrationSmoke
{
    static readonly BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool value,string detail){if(!value)throw new Exception(detail);checks++;}
    static object Get(object value,string name){return value.GetType().GetField(name,Flags).GetValue(value);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Flags).Invoke(value,args);}
    static string Hash(string path){using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128};}
    static void Matrix(string path,double[,] matrix)
    {using(var writer=new BinaryWriter(File.Create(path))){writer.Write(matrix.GetLength(0));writer.Write(matrix.GetLength(1));foreach(double d in matrix)writer.Write(d);}}
    static double[,] Matrix(string path)
    {using(var reader=new BinaryReader(File.OpenRead(path))){var m=new double[reader.ReadInt32(),reader.ReadInt32()];for(int r=0;r<m.GetLength(0);r++)for(int c=0;c<m.GetLength(1);c++)m[r,c]=reader.ReadDouble();Check(reader.BaseStream.Position==reader.BaseStream.Length,"trailing matrix bytes");return m;}}
    static void Reject(Type e,Dictionary<string,object> packed,string detail)
    {try{e.GetMethod("UnpackLedger",Flags).Invoke(null,new object[]{packed});throw new Exception("accepted "+detail);}catch(TargetInvocationException ex){Check(ex.InnerException is InvalidDataException||ex.InnerException is FormatException,"specific rejection: "+detail);}}
    [STAThread] static int Main(string[] args)
    {
        try {
            Console.OutputEncoding=new UTF8Encoding(false);
            Console.InputEncoding=new UTF8Encoding(false);
            if(args.Length!=3)throw new ArgumentException("Expected modules, LOC and owned fixture paths.");
            string dll=Path.Combine(Path.GetFullPath(args[0]),"DataAnalysis","HVX.PdReview.V1.0.dll");
            string name=AssemblyName.GetAssemblyName(dll).Name;
            // The harness lives only in QA. Resolve the exact tested original
            // module before JIT sees the typed UI route in Run; no module copy.
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,eventArgs)=>
                String.Equals(new AssemblyName(eventArgs.Name).Name,name,StringComparison.Ordinal)?Assembly.LoadFrom(dll):null;
            return Run(args);
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Run(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]),source=Path.GetFullPath(args[1]),destination=Path.GetFullPath(args[2]);
            Directory.CreateDirectory(destination);var originals=new Dictionary<string,string>();
            foreach(string path in new[]{source,Path.Combine(Path.GetDirectoryName(source),"CABLE.in"),Path.Combine(Path.GetDirectoryName(source),"ITEM.in")})
                if(File.Exists(path)){originals[path]=Hash(path);File.Copy(path,Path.Combine(destination,Path.GetFileName(path)),false);}
            string loc=Path.Combine(destination,Path.GetFileName(source));
            Assembly assembly=typeof(PdReviewControl).Assembly;
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",Flags).SetValue(null,new Func<string>(()=>Path.Combine(destination,"test-preferences.json")));
            Type engine=assembly.GetType("HVX.PdReview.ReviewAnalysisEngine",true),e=assembly.GetType("HVX.PdReview.ReviewEAnalysis",true);
            var summaries=new List<object>();
            foreach(int index in new[]{8,9})using(var control=new PdReviewControl()) {
                control.ProjectPath=destination;
                Check(control.SetProjectLocationIds(new[]{loc},new double[]{1}),control.LastError);
                control.AnalysisAlgorithmIndex=index;control.AnalysisAutomaticThresholdMultiplier=5;
                control.AnalysisAttenuationTolerancePercent=16;control.AnalysisMaximumPulseCount=30;
                control.AnalysisUseFirstArrivalTiming=true;control.WaveformFilterMode=1;
                object request=Call(control,"CreateFileAnalysisRequest",loc,1L,true,1);
                Check(!(bool)Get(request,"UseFirstArrivalTiming")&&!(bool)Get(request,"ApplyLinearFilter"),"shared builder routes E raw-peak options");
                object result=engine.GetMethod("Analyze",Flags).Invoke(null,new[]{request});
                string status=(string)Get(result,"Status");Check((bool)Get(result,"Success"),"E route failed: "+status);
                var parameters=(double[,])Get(result,"Parameters");Check(parameters.GetLength(0)>0&&parameters.GetLength(0)<=30,"nonempty bounded E rows");
                Check(Enumerable.Range(0,parameters.GetLength(0)).All(r=>parameters[r,26]==index),"method index persisted in output");
                var parsed=Json().DeserializeObject(status) as Dictionary<string,object>;
                var packed=(Dictionary<string,object>)parsed["upstream_evidence"];
                var ledger=(Dictionary<string,object>)e.GetMethod("UnpackLedger",Flags).Invoke(null,new object[]{packed});
                int events=((object[])ledger["events"]).Length;
                Check(events>parameters.GetLength(0),"full ledger is not reduced to matrix cap");
                int eligible=((object[])ledger["events"]).Cast<Dictionary<string,object>>().Count(x=>Object.Equals(x["engineering_eligible"],true));
                Type completed=typeof(PdReviewControl).GetNestedType("CompletedFile",Flags);object item=Activator.CreateInstance(completed,true);
                completed.GetField("Request",Flags).SetValue(item,request);completed.GetField("Result",Flags).SetValue(item,result);
                Array completedArray=Array.CreateInstance(completed,1);completedArray.SetValue(item,0);
                object diagnostics=Call(control,"WithCompletedAlgorithmDiagnostics",completedArray);
                Array results=Array.CreateInstance(result.GetType(),1);results.SetValue(result,0);object[] merge={results,null,null};Call(control,"MergeAnalysisResults",merge);
                Check(control.LoadDataMatrices((double[,])merge[1],(double[,])merge[2]),"matrix UI load: "+control.LastError);
                typeof(PdReviewControl).GetField("algorithmDiagnostics",Flags).SetValue(control,diagnostics);
                object tree=Get(control,"analysisTree");Check((bool)Call(tree,"MarkAnalyzed",new object[]{new[]{loc}}),"tree analyzed status");
                double[,] p,w;byte[] metadata;Check(control.ExportAnalysisSessionUtf8(out p,out w,out metadata),"session export: "+control.LastError);
                string prefix=Path.Combine(destination,"method-"+index);Matrix(prefix+".p",p);Matrix(prefix+".w",w);File.WriteAllBytes(prefix+".json",metadata);
                using(var restored=new PdReviewControl()){
                    Check(restored.LoadAnalysisSessionUtf8(destination,Matrix(prefix+".p"),Matrix(prefix+".w"),File.ReadAllBytes(prefix+".json")),"session reload: "+restored.LastError);
                    Check(restored.AnalysisAlgorithmIndex==index&&restored.SourceRowCount==p.GetLength(0),"settings and rows reload");
                    var after=(Dictionary<string,string>)Get(restored,"algorithmDiagnostics");
                    Check(after.Count==1&&after.Single().Value==status,"full original ledger retained byte-for-byte in status");
                    Check(restored.LocationFilterAlgorithmId=="B","only B after reload");
                }
                var bad=new Dictionary<string,object>(packed);bad["sha256"]=new String('0',64);Reject(e,bad,"wrong hash");
                bad=new Dictionary<string,object>(packed);bad["decoded_bytes"]=2;Reject(e,bad,"false expanded length");
                bad=new Dictionary<string,object>(packed);bad["data"]="!!!!";Reject(e,bad,"invalid Base64");
                bad=new Dictionary<string,object>(packed);bad["data"]=Convert.ToBase64String(new byte[]{31,139,8,0,0});Reject(e,bad,"truncated gzip");
                bad=new Dictionary<string,object>(packed);bad["decoded_bytes"]=16*1024*1024+1;Reject(e,bad,"oversized declared length");
                bad=new Dictionary<string,object>(packed);bad["data"]=new String('A',700000);Reject(e,bad,"oversized compressed input");
                using(var memory=new MemoryStream()){
                    using(var gzip=new GZipStream(memory,CompressionLevel.Optimal,true)) {var block=new byte[8192];for(int i=0;i<2049;i++)gzip.Write(block,0,block.Length);}
                    bad=new Dictionary<string,object>(packed);bad["decoded_bytes"]=16*1024*1024;bad["data"]=Convert.ToBase64String(memory.ToArray());Reject(e,bad,"actual expanded gzip bomb");
                }
                // Session parser must inspect the E descriptor, not merely the outer string size.
                var forged=new Dictionary<string,object>(parsed);forged["upstream_evidence"]=bad;
                var diagnosticJson=Json().Serialize(new Dictionary<string,string>{{Path.GetFileName(loc),Json().Serialize(forged)}});
                try{typeof(PdReviewControl).GetMethod("ParseAlgorithmDiagnostics",Flags).Invoke(null,new object[]{diagnosticJson,destination+Path.DirectorySeparatorChar,new[]{loc}});throw new Exception("session accepted bomb");}
                catch(TargetInvocationException ex){Check(ex.InnerException is InvalidDataException,"session decoder rejects actual bomb");}
                var repeated=new Dictionary<string,string>();for(int k=0;k<24;k++)repeated.Add("copy-"+k+".loc",status);
                string repeatedJson=(string)typeof(PdReviewControl).GetMethod("SerializeAlgorithmDiagnostics",Flags).Invoke(null,new object[]{repeated});
                string[] repeatedPaths=repeated.Keys.Select(key=>Path.Combine(destination,key)).ToArray();
                var repeatedBack=(Dictionary<string,string>)typeof(PdReviewControl).GetMethod("ParseAlgorithmDiagnostics",Flags).Invoke(null,new object[]{repeatedJson,destination+Path.DirectorySeparatorChar,repeatedPaths});
                Check(repeatedBack.Count==24&&repeatedBack.All(pair=>pair.Value==status),"multiple complete per-record ledgers survive session round trip");
                summaries.Add(new{method=index==8?"E":"E+",rows=parameters.GetLength(0),ledgerEvents=events,eligible=eligible,statusBytes=Encoding.UTF8.GetByteCount(status),metadataBytes=metadata.Length});
            }
            foreach(var original in originals)Check(Hash(original.Key)==original.Value,"original input unchanged");
            Console.WriteLine(Json().Serialize(new{passed=true,checks=checks,cases=summaries,uiSha256=Hash(assembly.Location),sourceHashes=originals,hardwareUsed=false,productStarted=false}));return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
    }
}
