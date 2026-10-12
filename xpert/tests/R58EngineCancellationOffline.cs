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
using System.Web.Script.Serialization;

// Engine-only cancellation classification QA. Uses real read-only LOC/project
// inputs, never constructs a UI or starts Python, a product, SDK or device.
static class R58EngineCancellationOffline
{
    const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type requestType,engine,runtime;static MethodInfo analyze;
    static readonly List<object> checks=new List<object>();
    static string Hash(string path){using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");}
    static void Check(bool value,string label){if(!value)throw new Exception(label);}
    static void Set(object request,string field,object value){requestType.GetField(field,Flags).SetValue(request,value);}
    static object Request(string project,string loc,CancellationToken cancellation,int maxPulses=30)
    {
        object request=Activator.CreateInstance(requestType);Set(request,"ProjectPath",project);Set(request,"LocationFilePath",loc);
        Set(request,"AlgorithmIndex",9);Set(request,"LocationDataId",92L);Set(request,"MaxPulses",maxPulses);Set(request,"Cancellation",cancellation);
        return request;
    }
    static void Case(string name,object request,bool expectedCanceled,string expectedStatus=null)
    {
        var clock=Stopwatch.StartNew();object result;
        try{result=analyze.Invoke(null,new[]{request});}catch(TargetInvocationException error){throw error.InnerException??error;}
        clock.Stop();Type resultType=result.GetType();bool success=(bool)resultType.GetField("Success",Flags).GetValue(result),canceled=(bool)resultType.GetField("Canceled",Flags).GetValue(result);
        string status=(string)resultType.GetField("Status",Flags).GetValue(result);
        Check(!success,name+": not success");Check(canceled==expectedCanceled,name+": explicit cancellation classification");
        Check(!String.IsNullOrEmpty(status),name+": actionable status preserved");
        if(expectedStatus!=null)Check(status.IndexOf(expectedStatus,StringComparison.OrdinalIgnoreCase)>=0,name+": expected underlying status");
        Check(runtime.GetField("worker",Flags).GetValue(null)==null&&runtime.GetField("pendingRelease",Flags).GetValue(null)==null,name+": no algorithm process owner created");
        checks.Add(new Dictionary<string,object>{{"case",name},{"success",success},{"canceled",canceled},{"status",status},{"elapsed_ms",clock.Elapsed.TotalMilliseconds},{"worker_started",false}});
        Console.WriteLine("CASE_PASS="+name+";Success="+success+";Canceled="+canceled+";elapsedMs="+clock.Elapsed.TotalMilliseconds.ToString("F3",CultureInfo.InvariantCulture));
    }
    static int Main(string[] args)
    {
        try{
            if(args.Length!=5)throw new ArgumentException("module.dll, project root, selected LOC, source root, owned evidence JSON required");
            string module=Path.GetFullPath(args[0]),project=Path.GetFullPath(args[1]),loc=Path.GetFullPath(args[2]),source=Path.GetFullPath(args[3]),evidence=Path.GetFullPath(args[4]);
            var originals=new[]{loc,Path.ChangeExtension(loc,".dem"),Path.Combine(project,"CABLE.in"),Path.Combine(project,"ITEM.in")}.Where(File.Exists).ToDictionary(path=>path,path=>Hash(path),StringComparer.OrdinalIgnoreCase);
            Check(originals.ContainsKey(loc)&&File.Exists(Path.Combine(project,"CABLE.in")),"real input exists");
            var assembly=Assembly.LoadFrom(module);requestType=assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true);engine=assembly.GetType("HVX.PdReview.ReviewAnalysisEngine",true);runtime=assembly.GetType("HVX.PdReview.ReviewERuntime",true);analyze=engine.GetMethod("Analyze",Flags);
            Check(Assembly.GetEntryAssembly().GetManifestResourceInfo("HVX.Research.Pins")!=null,"test host embeds runtime pins");
            var adapter=assembly.GetType("HVX.PdReview.ReviewEAnalysis",true);adapter.GetMethod("CheckEnvironment",Flags).Invoke(null,new object[0]);
            using(var canceled=new CancellationTokenSource()){
                canceled.Cancel();
                Case("pre_canceled_real_valid_Eplus",Request(project,loc,canceled.Token),true,"canceled");
                string missing=Path.Combine(Path.GetDirectoryName(evidence),"definitely-absent.loc");Check(!File.Exists(missing),"missing fixture is absent");
                Case("uncanceled_missing_LOC",Request(project,missing,CancellationToken.None),false);
                Case("canceled_missing_LOC_before_OCE",Request(project,missing,canceled.Token),false);
                Case("uncanceled_invalid_parameter",Request(project,loc,CancellationToken.None,0),false,"MaxPulses");
                Case("canceled_invalid_parameter_before_OCE",Request(project,loc,canceled.Token,0),false,"MaxPulses");
            }
            string loop=File.ReadAllText(Path.Combine(source,"modules-src","DataAnalysis","src","ReviewProjectAnalysis.cs"));
            Check(loop.Contains("if(result.Canceled)break;"),"batch loop breaks only on explicit Canceled result");
            Check(!loop.Contains("cancellation.IsCancellationRequested&&!result.Success"),"batch loop does not hide any failed result when token happens to be canceled");
            foreach(var pair in originals)Check(Hash(pair.Key)==pair.Value,"read-only source input unchanged: "+Path.GetFileName(pair.Key));
            var payload=new Dictionary<string,object>{{"schema","HVX.EngineCancellation.Offline/1"},{"module_sha256",Hash(module)},{"cases",checks},{"original_file_sha256",originals},{"explicit_batch_classification",true},{"product_ui_constructed",false},{"python_started",false},{"sdk_called",false},{"hardware_used",false},{"source_project_written",false}};
            Directory.CreateDirectory(Path.GetDirectoryName(evidence));File.WriteAllText(evidence,new JavaScriptSerializer{MaxJsonLength=1024*1024}.Serialize(payload),new UTF8Encoding(false));
            Console.WriteLine("R58_ENGINE_CANCEL_CLASSIFICATION_PASS="+checks.Count+";moduleSha256="+Hash(module)+";evidence="+evidence);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
