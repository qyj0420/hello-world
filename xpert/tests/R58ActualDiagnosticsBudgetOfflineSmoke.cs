using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;

// Reads only the supplied archive metadata. No UI constructor, product entry
// point, algorithm execution, worker, SDK, device, or source-project write.
static class R58ActualDiagnosticsBudgetOfflineSmoke
{
    const int MiB=1024*1024;
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static Type review;
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=64*MiB,RecursionLimit=128};}
    static void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
    static object Call(object instance,string name,params object[] args)
    {return review.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(instance,args);}
    static void Set(object instance,string name,object value){instance.GetType().GetField(name,F).SetValue(instance,value);}
    static Exception Rejected(Action action)
    {try{action();return null;}catch(TargetInvocationException error){return error.InnerException;}catch(Exception error){return error;}}
    static string Decode(string text)
    {
        const string prefix="HVX.Diagnostics.Gzip.v1:";
        if(!text.StartsWith(prefix,StringComparison.Ordinal))return text;
        using(var input=new MemoryStream(Convert.FromBase64String(text.Substring(prefix.Length))))
        using(var gzip=new GZipStream(input,CompressionMode.Decompress))
        using(var reader=new StreamReader(gzip,new UTF8Encoding(false,true)))return reader.ReadToEnd();
    }
    static Array Completed(Assembly assembly,string root,Dictionary<string,string> values)
    {
        Type completed=review.GetNestedType("CompletedFile",F);
        Array result=Array.CreateInstance(completed,values.Count);int index=0;
        foreach(var pair in values){
            var request=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);
            Set(request,"LocationFilePath",Path.Combine(root,pair.Key));
            var analyzed=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisResult",true),true);
            Set(analyzed,"Status",pair.Value);Set(analyzed,"Success",true);
            var item=Activator.CreateInstance(completed,true);Set(item,"Request",request);Set(item,"Result",analyzed);result.SetValue(item,index++);
        }return result;
    }
    static int Main(string[] args)
    {
        try{
            if(args.Length!=4)throw new ArgumentException("Expected module, read-only session.json, owned QA root, old/new mode.");
            string module=Path.GetFullPath(args[0]),sessionPath=Path.GetFullPath(args[1]),qa=Path.GetFullPath(args[2]);
            bool old=args[3]=="old";if(!old&&args[3]!="new")throw new ArgumentException("Invalid mode.");
            Directory.CreateDirectory(qa);string root=qa.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            var document=(Dictionary<string,object>)Json().DeserializeObject(File.ReadAllText(sessionPath,new UTF8Encoding(false,true)));
            var settings=(object[])document["settings"];
            string encoded=(string)((object[])settings.Single(r=>(string)((object[])r)[0]=="AlgorithmDiagnostics"))[1];
            var originals=(Dictionary<string,object>)Json().DeserializeObject(encoded);
            var decoded=originals.ToDictionary(p=>p.Key,p=>Decode((string)p.Value),StringComparer.OrdinalIgnoreCase);
            Check(decoded.Count==31,"actual archive 023 contains 31 record statuses");
            Check(decoded.All(p=>p.Value.Length<=MiB),"actual statuses obey the unchanged 1 MiB per-record cap");
            var batch=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            for(int replica=0;replica<3;replica++)foreach(var pair in decoded)batch.Add("copy-"+replica+"-"+Path.GetFileName(pair.Key),pair.Value);
            long decodedBytes=batch.Sum(p=>(long)Encoding.UTF8.GetByteCount(p.Value));
            Check(decodedBytes<=64L*MiB,"93-record extension obeys the unchanged 64 MiB decoded-status cap");
            Assembly assembly=Assembly.LoadFrom(module);review=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            object carrier=FormatterServices.GetUninitializedObject(review);
            Set(carrier,"projectPath",root);Set(carrier,"algorithmDiagnostics",new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
            Dictionary<string,string> merged=null;
            Exception failure=Rejected(()=>merged=(Dictionary<string,string>)Call(carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,batch)));
            if(old){
                Check(failure is InvalidOperationException,"old bounded commit reproduces the serialization exception");
                Check(failure.StackTrace.Contains("JavaScriptSerializer.Serialize")&&failure.StackTrace.Contains("SerializeAlgorithmDiagnostics"),"old failure occurs in diagnostic serialization before matrix publication");
                Console.WriteLine("R58_ACTUAL_DIAGNOSTICS_OLD_REPRODUCED;records="+batch.Count+";decodedStatusBytes="+decodedBytes+";exception="+failure.GetType().Name);
                Console.WriteLine(failure.StackTrace);return 0;
            }
            if(failure!=null)throw failure;
            Check(merged.Count==batch.Count&&batch.All(p=>merged[p.Key]==p.Value),"bounded project merge retains every status byte-for-byte");
            string serialized=(string)Call(null,"SerializeAlgorithmDiagnostics",merged);
            int encodedBytes=Encoding.UTF8.GetByteCount(serialized);
            Check(encodedBytes>8*MiB&&encodedBytes<=32*MiB,"actual expanded project crosses the old quota but fits the new finite quota");
            var reloaded=(Dictionary<string,string>)Call(null,"ParseAlgorithmDiagnostics",serialized,root,batch.Keys.Select(p=>Path.Combine(root,p)).ToArray());
            Check(reloaded.Count==batch.Count&&batch.All(p=>reloaded[p.Key]==p.Value),"bounded save/reload preserves all actual evidence");
            Set(carrier,"algorithmDiagnostics",merged);
            var overwrite=(Dictionary<string,string>)Call(carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,batch));
            Check(overwrite.Count==batch.Count,"repeat analysis replaces records without duplicate accumulation");
            Check(Rejected(()=>Call(null,"ParseAlgorithmDiagnostics",new string('x',64*MiB+1),root,new string[0])) is ArgumentException,"legacy flat encoded character quota rejects boundary plus one");
            Check(Rejected(()=>Call(null,"ParseAlgorithmDiagnostics",new string('\u6c49',22*MiB),root,new string[0])) is ArgumentException,"UTF-8 byte quota rejects multibyte input before parsing");
            var randomBytes=new byte[760000];new Random(238).NextBytes(randomBytes);
            string noise=Json().Serialize(new{opaque=Convert.ToBase64String(randomBytes)});
            Check(noise.Length<=MiB,"oversized aggregate fixture obeys individual limit");
            var excessive=new Dictionary<string,string>();for(int i=0;i<34;i++)excessive.Add("large-"+i+".loc",noise);
            Check(excessive.Sum(p=>(long)Encoding.UTF8.GetByteCount(p.Value))<=64L*MiB,"oversized encoded fixture does not violate decoded limit first");
            string segmented=(string)Call(null,"SerializeAlgorithmDiagnostics",excessive);
            Check(segmented.IndexOf("HVX.AlgorithmDiagnostics.Segmented/1",StringComparison.Ordinal)>=0,"payload above 32 MiB spills into segmented envelope");
            var segmentedBack=(Dictionary<string,string>)Call(null,"ParseAlgorithmDiagnostics",segmented,root,excessive.Keys.Select(p=>Path.Combine(root,p)).ToArray());
            Check(segmentedBack.Count==excessive.Count&&excessive.All(p=>segmentedBack[p.Key]==p.Value),"segmented save/reload preserves oversized evidence");
            Console.WriteLine("R58_ACTUAL_DIAGNOSTICS_HOTFIX_PASS="+checks+";records="+batch.Count+";decodedStatusBytes="+decodedBytes+";encodedBytes="+encodedBytes+";ProductConstructed=false;SDKCalled=false;HardwareUsed=false;SourceProjectWritten=false");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error.ToString());return 1;}
    }
}
