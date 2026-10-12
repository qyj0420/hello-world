using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Actual immutable metadata and synthetic boundary cases only. No control
// constructor, source-project writes, algorithm execution, worker, SDK or device.
static class R58CandidateEvidenceMemoOfflineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    const int MiB=1024*1024;
    static Type review,e;static Assembly assembly;static int checks;static bool modern;
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=64*MiB,RecursionLimit=128};}
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    static object Call(object target,string method,params object[] args)
    {
        var type=target as Type??target.GetType();
        try{return type.GetMethods(F).Single(m=>m.Name==method&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}
        catch(TargetInvocationException error){throw error.InnerException;}
    }
    static void Set(object value,string field,object data){value.GetType().GetField(field,F).SetValue(value,data);}
    static object Get(object value,string field){return value.GetType().GetField(field,F).GetValue(value);}
    static string Decode(string text)
    {
        const string prefix="HVX.Diagnostics.Gzip.v1:";if(!text.StartsWith(prefix,StringComparison.Ordinal))return text;
        using(var input=new MemoryStream(Convert.FromBase64String(text.Substring(prefix.Length))))
        using(var gzip=new GZipStream(input,CompressionMode.Decompress))
        using(var reader=new StreamReader(gzip,new UTF8Encoding(false,true)))return reader.ReadToEnd();
    }
    static string Hash(byte[] bytes){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","");}
    static Array Completed(string root,Dictionary<string,string> statuses)
    {
        var type=review.GetNestedType("CompletedFile",F);var result=Array.CreateInstance(type,statuses.Count);int i=0;
        foreach(var pair in statuses){
            var request=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);Set(request,"LocationFilePath",Path.Combine(root,pair.Key));
            var output=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisResult",true),true);Set(output,"Success",true);Set(output,"Status",pair.Value);
            var item=Activator.CreateInstance(type,true);Set(item,"Request",request);Set(item,"Result",output);result.SetValue(item,i++);
        }return result;
    }
    static Dictionary<string,string> Merge(object carrier,string root,Dictionary<string,string> updates)
    {return (Dictionary<string,string>)Call(carrier,"WithCompletedAlgorithmDiagnostics",Completed(root,updates));}
    static string SerializeInstance(object carrier,Dictionary<string,string> values)
    {
        Set(carrier,"algorithmDiagnostics",values);
        return (string)review.GetProperty("AlgorithmDiagnosticsJson",F).GetValue(carrier,null);
    }
    static void Rejected(Action action,string why)
    {Exception error=null;try{action();}catch(Exception ex){error=ex;while(error is TargetInvocationException)error=error.InnerException;}Check(error is ArgumentException||error is InvalidDataException||error is InvalidOperationException||error is FormatException,why);}
    static Dictionary<string,object> Time(string label,Action action)
    {
        var samples=new List<double>();for(int i=0;i<4;i++){GC.Collect();GC.WaitForPendingFinalizers();var watch=Stopwatch.StartNew();action();watch.Stop();if(i>0)samples.Add(watch.Elapsed.TotalMilliseconds);}
        double median=samples.OrderBy(x=>x).ElementAt(1);Console.WriteLine(label+" medianMs="+median.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
        return new Dictionary<string,object>{{"Name",label},{"SamplesMilliseconds",samples.ToArray()},{"MedianMilliseconds",median}};
    }
    static string StatusWithMarker(string status,int value)
    {var parsed=(Dictionary<string,object>)Json().DeserializeObject(status);parsed["offline_timing_marker"]=value;return Json().Serialize(parsed);}
    static void Contract(object carrier,string root,Dictionary<string,string> statuses,string location,string encoded)
    {
        string actual=SerializeInstance(carrier,statuses);Check(actual==encoded,"memoized encoding is byte-identical to uncached encoding");
        var loaded=(Dictionary<string,string>)Call(review,"ParseAlgorithmDiagnostics",actual,root,statuses.Keys.Select(p=>Path.Combine(root,p)).ToArray());
        Check(loaded.Count==statuses.Count&&statuses.All(p=>loaded[p.Key]==p.Value),"roundtrip retains every original status character");
        if(!modern)return;
        var cache=Get(carrier,"encodedCandidateStatuses");Check(((IDictionary)cache).Count==31,"cache is bounded to current 31 records");
        string original=statuses[location];int sha=original.IndexOf("\"sha256\":\"",StringComparison.Ordinal)+10;Check(sha>=10,"actual status contains ledger SHA");
        char different=original[sha]=='0'?'1':'0';string damaged=original.Substring(0,sha)+different+original.Substring(sha+1);
        Check(damaged.Length==original.Length,"same-length corrupt fixture");
        Rejected(()=>Merge(carrier,root,new Dictionary<string,string>{{location,damaged}}),"same key and length do not reuse a corrupted ledger proof");
        Check(Object.ReferenceEquals(cache,Get(carrier,"encodedCandidateStatuses")),"failed merge does not publish cache");
        Check(Object.ReferenceEquals(statuses,Get(carrier,"algorithmDiagnostics")),"failed merge does not replace metadata");
        string malformed=original.Substring(0,original.Length-1)+" ";
        Rejected(()=>Merge(carrier,root,new Dictionary<string,string>{{location,malformed}}),"same-length malformed JSON is revalidated");
        Check(Object.ReferenceEquals(cache,Get(carrier,"encodedCandidateStatuses")),"malformed failure preserves prior memo snapshot");
        Rejected(()=>Merge(carrier,root,new Dictionary<string,string>{{location,new String('x',MiB+1)}}),"individual 1 MiB limit not relaxed by warm cache");
        Check(Object.ReferenceEquals(cache,Get(carrier,"encodedCandidateStatuses")),"oversized failure does not publish cache");
        string changed=StatusWithMarker(original,1);var updated=Merge(carrier,root,new Dictionary<string,string>{{location,changed}});
        Check(updated[location]==changed&&updated.Count==31,"changed valid status replaces, never duplicates, record");
        string updatedEncoded=SerializeInstance(carrier,updated);
        Check(updatedEncoded==(string)Call(review,"SerializeAlgorithmDiagnostics",updated),"changed status uses exact uncached encoding");
        string other=Path.Combine(root,"other-project");Directory.CreateDirectory(other);Set(carrier,"projectPath",other);
        var one=new Dictionary<string,string>{{"one.loc",original}};SerializeInstance(carrier,one);
        Check(((IDictionary)Get(carrier,"encodedCandidateStatuses")).Count==1,"project replacement drops all unrelated cache entries");
        SerializeInstance(carrier,new Dictionary<string,string>());
        Check(((IDictionary)Get(carrier,"encodedCandidateStatuses")).Count==0,"empty snapshot retains no old statuses");
        Set(carrier,"projectPath",root);SerializeInstance(carrier,statuses);cache=Get(carrier,"encodedCandidateStatuses");
        var largeDecoded=new Dictionary<string,string>();string large=new String('x',MiB);for(int i=0;i<65;i++)largeDecoded.Add("decoded-"+i+".loc",large);
        Rejected(()=>SerializeInstance(carrier,largeDecoded),"identical status memo still charges each occurrence against 64 MiB decoded limit");
        Check(Object.ReferenceEquals(cache,Get(carrier,"encodedCandidateStatuses")),"decoded aggregate failure preserves old cache");
        var evidence=new Dictionary<string,object>{{"payload",new String('x',16*MiB-14)}};
        // Adjust exact size independent of serialization overhead.
        int bytes=Encoding.UTF8.GetByteCount(Json().Serialize(evidence));evidence["payload"]=new String('x',((string)evidence["payload"]).Length+16*MiB-bytes);
        var packed=(Dictionary<string,object>)Call(e,"PackLedger",evidence);
        Check((int)packed["decoded_bytes"]==16*MiB,"maximum-sized ledger fixture exact boundary");
        string maximum=Json().Serialize(new Dictionary<string,object>{{"schema","HVX.LocationE.Adapter/1"},{"upstream_evidence",packed}});
        var over=new Dictionary<string,string>();for(int i=0;i<33;i++)over.Add("ledger-"+i+".loc",maximum);
        Rejected(()=>SerializeInstance(carrier,over),"memoized duplicate ledgers still charge all 33 x16 MiB against 512 MiB budget");
        Check(Object.ReferenceEquals(cache,Get(carrier,"encodedCandidateStatuses")),"ledger aggregate failure preserves old cache");
        var random=new byte[760000];new Random(238).NextBytes(random);string noise=Json().Serialize(new{opaque=Convert.ToBase64String(random)});
        var overEncoded=new Dictionary<string,string>();for(int i=0;i<34;i++)overEncoded.Add("encoded-"+i+".loc",noise);
        Rejected(()=>SerializeInstance(carrier,overEncoded),"memoized duplicated representations still enforce final 32 MiB byte quota");
        Check(Object.ReferenceEquals(cache,Get(carrier,"encodedCandidateStatuses")),"encoded aggregate failure preserves old cache");
        Set(carrier,"algorithmDiagnostics",statuses);
        Check(SerializeInstance(carrier,statuses)==encoded,"valid original metadata unchanged after all rejection probes");
    }
    static int Main(string[] args)
    {
        try{
            Check(args.Length==6,"Expected module, actual read-only session, owned QA root, report, LOC, old/new mode");
            string module=Path.GetFullPath(args[0]),session=Path.GetFullPath(args[1]),root=Path.GetFullPath(args[2]);Directory.CreateDirectory(root);root=root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            modern=args[5]=="new";Check(modern||args[5]=="old","mode");byte[] originalBytes=File.ReadAllBytes(session);
            var envelope=(Dictionary<string,object>)Json().DeserializeObject(new UTF8Encoding(false,true).GetString(originalBytes));
            string stored=(string)((object[])((object[])envelope["settings"]).Single(p=>Object.Equals(((object[])p)[0],"AlgorithmDiagnostics")))[1];
            var statuses=((Dictionary<string,object>)Json().DeserializeObject(stored)).ToDictionary(p=>p.Key,p=>Decode((string)p.Value),StringComparer.OrdinalIgnoreCase);
            Check(statuses.Count==31&&statuses.ContainsKey(args[4]),"actual 023, same screenshot LOC");assembly=Assembly.LoadFrom(module);review=assembly.GetType("HVX.PdReview.PdReviewControl",true);e=assembly.GetType("HVX.PdReview.ReviewEAnalysis",true);
            object carrier=FormatterServices.GetUninitializedObject(review);Set(carrier,"projectPath",root);Set(carrier,"algorithmDiagnostics",statuses);
            string encoded=(string)Call(review,"SerializeAlgorithmDiagnostics",statuses);Contract(carrier,root,statuses,args[4],encoded);
            Set(carrier,"algorithmDiagnostics",statuses);Set(carrier,"projectPath",root);int update=0;Dictionary<string,string> output=null;
            var variants=new[]{StatusWithMarker(statuses[args[4]],0),StatusWithMarker(statuses[args[4]],1)};
            var cold=Time("single_update_31_cold",()=>{if(modern)Set(carrier,"encodedCandidateStatuses",null);output=Merge(carrier,root,new Dictionary<string,string>{{args[4],variants[(update++)%2]}});});
            var warm=Time("single_update_31_warm_changed_status",()=>output=Merge(carrier,root,new Dictionary<string,string>{{args[4],variants[(update++)%2]}}));
            string warmEncoded=null;Set(carrier,"algorithmDiagnostics",output);
            var exported=Time("metadata_property_31_warm",()=>warmEncoded=(string)review.GetProperty("AlgorithmDiagnosticsJson",F).GetValue(carrier,null));
            Check(warmEncoded==(string)Call(review,"SerializeAlgorithmDiagnostics",output),"timed warm encoding matches strict uncached serialization");
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(session)),"original session bytes unchanged");
            var report=new Dictionary<string,object>{{"Schema","HVX.R58.CandidateEvidenceMemo/1"},{"Mode",args[5]},{"ModuleSha256",Hash(File.ReadAllBytes(module))},
                {"Checks",checks},{"Location",args[4]},{"Records",statuses.Count},{"OriginalStatusCharacters",statuses.Sum(p=>(long)p.Value.Length)},
                {"EncodedSha256",Hash(Encoding.UTF8.GetBytes(encoded))},{"EncodedBytes",Encoding.UTF8.GetByteCount(encoded)},{"Measurements",new[]{cold,warm,exported}},
                {"OriginalSessionUnchanged",true},{"ProductConstructed",false},{"Algorithm",false},{"Worker",false},{"SDK",false},{"Hardware",false},
                {"Notes","Actual 023 immutable statuses. Updated single status adds a harmless offline-only timing marker to vary content, not ledger evidence. 1 warm-up +3 measured calls; GC before timed action. Cold cache intentionally cleared each iteration; warm update alternates same-length marker values."}};
            File.WriteAllText(args[3],Json().Serialize(report),new UTF8Encoding(false));Console.WriteLine("R58_CANDIDATE_EVIDENCE_MEMO_PASS="+checks+";mode="+args[5]+";encodedSha256="+report["EncodedSha256"]);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
