using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;

// Pure managed, synthetic evidence only. Never construct a product control,
// open a product window, call an analysis algorithm/worker, SDK or device.
static class LocationELedgerBudgetOfflineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    const int MiB=1024*1024;
    static int checks;
    static Type review,e;
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*MiB,RecursionLimit=128};}
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    static object Call(Type t,object instance,string name,params object[] args)
    {return t.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(instance,args);}
    static void Set(object instance,string name,object value){instance.GetType().GetField(name,F).SetValue(instance,value);}
    static void Reject(Action action,string why)
    {
        bool rejected=false;
        try{action();}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidDataException||ex.InnerException is InvalidOperationException||ex.InnerException is ArgumentException||ex.InnerException is FormatException;}
        Check(rejected,why);
    }
    static string Status(Dictionary<string,object> packed,int id)
    {return Json().Serialize(new Dictionary<string,object>{{"schema","HVX.LocationE.Adapter/1"},{"record",id},{"upstream_evidence",packed}});}
    static string MakeStatus(int decodedBytes,int id)
    {
        var evidence=new Dictionary<string,object>{{"payload",""}};
        int overhead=Encoding.UTF8.GetByteCount(Json().Serialize(evidence));
        evidence["payload"]=new String('x',decodedBytes-overhead);
        var packed=(Dictionary<string,object>)Call(e,null,"PackLedger",evidence);
        Check((int)packed["decoded_bytes"]==decodedBytes,"fixture exact decoded byte count");
        return Status(packed,id);
    }
    static Array Completed(Assembly assembly,string root,IEnumerable<KeyValuePair<string,string>> input)
    {
        var all=input.ToArray();Type completed=review.GetNestedType("CompletedFile",F);
        Array result=Array.CreateInstance(completed,all.Length);
        for(int i=0;i<all.Length;i++){
            var request=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);
            Set(request,"LocationFilePath",Path.Combine(root,all[i].Key));
            var analysis=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisResult",true),true);
            Set(analysis,"Status",all[i].Value);Set(analysis,"Success",true);
            var item=Activator.CreateInstance(completed,true);Set(item,"Request",request);Set(item,"Result",analysis);result.SetValue(item,i);
        }
        return result;
    }
    static int Main(string[] args)
    {
        try{
            if(args.Length!=2)throw new ArgumentException("Expected exact DataAnalysis DLL and owned QA directory.");
            string dll=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            string root=qa.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            Assembly assembly=Assembly.LoadFrom(dll);review=assembly.GetType("HVX.PdReview.PdReviewControl",true);e=assembly.GetType("HVX.PdReview.ReviewEAnalysis",true);
            var input=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<31;i++)input.Add("record-"+i+".LOC",MakeStatus(MiB,i));
            Check(input.Sum(entry=>(long)(int)Call(e,null,"ValidateLedgerStatus",entry.Value))==31L*MiB,"31-record fixture exceeds old 16 MiB aggregate limit");

            // Uninitialized managed carrier: skip every UI constructor, preference
            // loader and runtime initialization. Only dictionary merge code runs.
            object carrier=FormatterServices.GetUninitializedObject(review);
            Set(carrier,"projectPath",root);Set(carrier,"algorithmDiagnostics",new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
            Dictionary<string,string> split=null;
            foreach(int offset in new[]{0,10,20}){
                var batch=input.Skip(offset).Take(10+(offset==20?1:0));
                split=(Dictionary<string,string>)Call(review,carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,batch));
                Set(carrier,"algorithmDiagnostics",split);
            }
            Check(split.Count==31&&input.All(entry=>split[entry.Key]==entry.Value),"10+10+11 batches preserve all 31 complete statuses");
            string serialized=(string)Call(review,null,"SerializeAlgorithmDiagnostics",input);
            Check(serialized==(string)Call(review,null,"SerializeAlgorithmDiagnostics",split),"one batch and split batches serialize identically");
            string[] paths=input.Keys.Select(key=>Path.Combine(root,key)).ToArray();

            // Exercise the actual metadata table helpers and the same UTF-8
            // envelope/limits as ExportAnalysisSessionUtf8, without UI startup.
            string[,] settings={{"AlgorithmDiagnostics",serialized},{"Schema","HVX.AnalysisSession"},{"Version","1"}};
            var envelope=new Dictionary<string,object>{{"schema","HVX.AnalysisSession.Utf8"},{"version",1},{"files",new string[0][]},{"settings",Call(review,null,"SessionJagged",settings)}};
            byte[] metadata=Encoding.UTF8.GetBytes(new JavaScriptSerializer{MaxJsonLength=16*MiB,RecursionLimit=16}.Serialize(envelope));
            Check(metadata.Length<=16*MiB,"31 ledgers fit bounded UTF-8 metadata envelope");
            string saved=Path.Combine(qa,"synthetic-session.json");File.WriteAllBytes(saved,metadata);
            var loaded=Json().DeserializeObject(new UTF8Encoding(false,true).GetString(File.ReadAllBytes(saved))) as Dictionary<string,object>;
            var restoredTable=(string[,])Call(review,null,"SessionTable",loaded["settings"],2);
            var back=(Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",restoredTable[0,1],root,paths);
            Check(back.Count==31&&input.All(entry=>back[entry.Key]==entry.Value),"save/reload preserves every evidence status byte-for-byte");
            foreach(var entry in back)Check((int)Call(e,null,"ValidateLedgerStatus",entry.Value)==MiB,"reload verifies each complete E ledger");

            var replacement=new Dictionary<string,string>{{input.First().Key,input.Last().Value}};
            var overwritten=(Dictionary<string,string>)Call(review,carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,replacement));
            Check(overwritten.Count==31&&overwritten[replacement.First().Key]==replacement.First().Value,"reanalyzing same LOC replaces ledger without duplicate accumulation");
            var legacy=new Dictionary<string,string>{{"legacy.LOC","{\"ok\":true}"}};
            Check(((Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",Json().Serialize(legacy),root,new[]{Path.Combine(root,"legacy.LOC")}))["legacy.LOC"]==legacy["legacy.LOC"],"legacy uncompressed diagnostics remain readable");

            Check((int)Call(review,null,"AddCandidateLedgerBytes",511*MiB,MiB)==512*MiB,"finite aggregate budget permits exact boundary");
            Reject(()=>Call(review,null,"AddCandidateLedgerBytes",512*MiB,1),"aggregate work budget rejects boundary plus one");
            Reject(()=>Call(review,null,"AddCandidateLedgerBytes",0,16*MiB+1),"individual decoded ledger limit remains 16 MiB");
            Reject(()=>Call(review,null,"AddCandidateLedgerBytes",Int32.MaxValue,MiB),"budget arithmetic rejects overflow-sized total");
            Reject(()=>Call(review,null,"AddCandidateLedgerBytes",0,-1),"negative ledger count rejected");

            var packed=(Dictionary<string,object>)((Dictionary<string,object>)Json().DeserializeObject(input.First().Value))["upstream_evidence"];
            var bad=new Dictionary<string,object>(packed);bad["sha256"]=new String('0',64);Reject(()=>Call(e,null,"UnpackLedger",bad),"hash corruption rejected");
            bad=new Dictionary<string,object>(packed);bad["decoded_bytes"]=MiB-1;Reject(()=>Call(e,null,"UnpackLedger",bad),"false decoded length rejected");
            bad=new Dictionary<string,object>(packed);bad["decoded_bytes"]=16*MiB+1;Reject(()=>Call(e,null,"UnpackLedger",bad),"oversized declared ledger rejected before expansion");
            bad=new Dictionary<string,object>(packed);bad["data"]=new String('A',700000);Reject(()=>Call(e,null,"UnpackLedger",bad),"compressed input length protection retained");
            bad=new Dictionary<string,object>(packed);bad["data"]="!!!!";Reject(()=>Call(e,null,"UnpackLedger",bad),"invalid base64 rejected");
            using(var output=new MemoryStream()){
                using(var gzip=new GZipStream(output,CompressionLevel.Optimal,true)){var block=new byte[8192];for(int i=0;i<2049;i++)gzip.Write(block,0,block.Length);}
                bad=new Dictionary<string,object>(packed);bad["data"]=Convert.ToBase64String(output.ToArray());bad["decoded_bytes"]=16*MiB;
            }
            Reject(()=>Call(e,null,"UnpackLedger",bad),"gzip expansion beyond individual 16 MiB bound rejected");
            string unretained=Json().Serialize(new Dictionary<string,string>{{"unretained.LOC",Status(bad,99)}});
            Reject(()=>Call(review,null,"ParseAlgorithmDiagnostics",unretained,root,new string[0]),"nonretained ledger still validated against decompression bomb");

            // Both aggregate validation routes must enforce the NEW finite work
            // budget; all 33 entries are small compressed statuses, expanded one
            // record at a time and never added to a decoded-ledger collection.
            string maximum=MakeStatus(16*MiB,0);var overBudget=new Dictionary<string,string>();
            for(int i=0;i<33;i++)overBudget.Add("large-"+i+".LOC",maximum);
            Reject(()=>Call(review,null,"SerializeAlgorithmDiagnostics",overBudget),"serialization rejects 33 maximum ledgers above 512 MiB");
            Reject(()=>Call(review,null,"ParseAlgorithmDiagnostics",Json().Serialize(overBudget),root,new string[0]),"load enforces aggregate budget before excluding nonretained entries");
            Check(GC.GetTotalMemory(true)<160L*MiB,"expanded ledgers are temporary, not retained as a 512 MiB decoded collection");
            var oversizedStatus=new Dictionary<string,string>{{"x.LOC",new string('x',MiB+1)}};
            Reject(()=>Call(review,null,"SerializeAlgorithmDiagnostics",oversizedStatus),"per-status 1 MiB protection retained");
            Reject(()=>Call(review,null,"ParseAlgorithmDiagnostics",new string('x',64*MiB+1),root,new string[0]),"64 MiB legacy flat encoded input protection enforced");
            int decoded=512*MiB;object[] decodeArgs={"x",decoded};
            Reject(()=>Call(review,null,"DecodeAlgorithmStatus",decodeArgs),"512 MiB segmented decoded status protection retained");
            Console.WriteLine("LOCATION_E_LEDGER_BUDGET_OFFLINE_PASS="+checks+";records=31;decodedLedgerBytes="+(31L*MiB)+";metadataBytes="+metadata.Length+";hardwareUsed=false;workerUsed=false;productConstructed=false");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
    }
}
