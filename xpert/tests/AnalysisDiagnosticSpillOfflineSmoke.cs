using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;

// Pure managed, synthetic evidence only. Never construct a product control,
// open a product window, call an analysis algorithm/worker, SDK or device.
// Usage: AnalysisDiagnosticSpillOfflineSmoke.exe <HVX.PdReview.V1.0.dll> <owned QA directory>
static class AnalysisDiagnosticSpillOfflineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    const int MiB=1024*1024;
    const string SpillPrefix="HVX.Diagnostics.Spill.v1:";
    static int checks;
    static Type review,spill,pack;
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    static object Call(Type t,object instance,string name,params object[] args)
    {return t.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(instance,args);}
    static void Set(object instance,string name,object value){instance.GetType().GetField(name,F).SetValue(instance,value);}
    static Exception Rejected(Action action)
    {
        try{action();}catch(TargetInvocationException ex){return ex.InnerException;}
        return null;
    }
    static void Reject(Action action,string why,string message=null)
    {
        var error=Rejected(action);
        Check(error!=null&&(error is InvalidDataException||error is InvalidOperationException||error is ArgumentException)&&
            (message==null||error.Message.Contains(message)),why+(error==null?" (accepted)":" ("+error.GetType().Name+": "+error.Message+")"));
    }
    // Deterministic, poorly compressible ~0.9 MiB status: regenerated for
    // verification so the test itself never holds the whole batch.
    static string Status(int id)
    {
        var builder=new StringBuilder(940*1024);uint x=2166136261u^(uint)id;
        builder.Append("{\"record\":").Append(id).Append(",\"source_identity_requires_review\":true,\"rows\":\"");
        while(builder.Length<900*1024){x^=x<<13;x^=x>>17;x^=x<<5;builder.Append(x.ToString("x8"));}
        return builder.Append("\",\"note\":\"完整复核证据\"}").ToString();
    }
    static string Resolve(string stored){return (string)Call(review,null,"ResolveAlgorithmStatus",stored);}
    static bool IsReference(string value){return value!=null&&value.StartsWith(SpillPrefix,StringComparison.Ordinal);}
    static long Stored(){return (long)spill.GetProperty("StoredBytes",F).GetValue(null,null);}
    static int Entries(){return (int)spill.GetProperty("EntryCount",F).GetValue(null,null);}
    static Array Completed(Assembly assembly,string root,IEnumerable<int> ids,bool spillInWorker)
    {
        var all=ids.ToArray();Type completed=review.GetNestedType("CompletedFile",F);
        Array result=Array.CreateInstance(completed,all.Length);
        for(int i=0;i<all.Length;i++){
            var request=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);
            Set(request,"LocationFilePath",Path.Combine(root,"record-"+all[i]+".LOC"));
            var analysis=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisResult",true),true);
            Set(analysis,"Success",true);
            var item=Activator.CreateInstance(completed,true);Set(item,"Request",request);Set(item,"Result",analysis);
            // Mirrors the worker: spill immediately, then drop the status.
            if(spillInWorker)Set(item,"Diagnostics",(string)Call(review,null,"RetainCompletedStatus",Status(all[i])));
            else Set(analysis,"Status",Status(all[i]));
            result.SetValue(item,i);
        }
        return result;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SpillPhase(Assembly assembly,string qa,string root,string spillRoot,int files,out long decodedTotal,out long storedAfterSpill,out long resident,
        out long spillMs,out long resolveMs,out long packBytes,out string index,out string archive,out string member,out string spillDirectory,out string[] paths)
    {
        // 1. 100 files x 0.9 MiB = 90 MiB decoded: above the old 64 MiB
        //    decoded session ceiling that blocked multi-file analysis.
        decodedTotal=Enumerable.Range(0,files).Sum(id=>(long)Encoding.UTF8.GetByteCount(Status(id)));
        Check(decodedTotal>64L*MiB,"fixture exceeds the old 64 MiB decoded diagnostics ceiling");
        GC.Collect();GC.WaitForPendingFinalizers();long baseline=GC.GetTotalMemory(true);
        object carrier=FormatterServices.GetUninitializedObject(review);
        Set(carrier,"projectPath",root);Set(carrier,"algorithmDiagnostics",new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
        Dictionary<string,string> merged=null;
        var timer=Stopwatch.StartNew();
        foreach(int offset in new[]{0,40,80}){
            var batch=Enumerable.Range(offset,offset==80?20:40);
            merged=(Dictionary<string,string>)Call(review,carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,batch,true));
            Set(carrier,"algorithmDiagnostics",merged);
        }
        spillMs=timer.ElapsedMilliseconds;
        Check(merged.Count==files&&merged.Values.All(IsReference),"three batches merge as 100 spill references");
        long mapChars=merged.Sum(entry=>(long)entry.Key.Length+entry.Value.Length);
        Check(mapChars<files*200,"session map holds ~100-character references, not statuses ("+mapChars+" chars)");
        GC.Collect();GC.WaitForPendingFinalizers();resident=GC.GetTotalMemory(true)-baseline;
        Check(resident<16L*MiB,"managed memory after 90 MiB of evidence stays below 16 MiB ("+resident+" bytes)");
        spillDirectory=(string)spill.GetProperty("DirectoryPath",F).GetValue(null,null);
        Check(spillDirectory.StartsWith(spillRoot,StringComparison.Ordinal)&&Directory.GetFiles(spillDirectory,"*.gz").Length==files,"each status is one gzip sidecar in the bounded spill folder");
        Check(File.Exists(Path.Combine(spillDirectory,".owner")),"spill folder carries an owner lock for stale cleanup");
        storedAfterSpill=Stored();
        Check(storedAfterSpill>0&&storedAfterSpill<decodedTotal&&storedAfterSpill<=1024L*MiB,"spill store bytes are compressed and below the 1 GiB ceiling ("+storedAfterSpill+")");

        // 2. Lossless, on-demand restore of every file.
        timer.Restart();
        for(int id=0;id<files;id++)Check(Resolve(merged["record-"+id+".LOC"])==Status(id),"record "+id+" restores byte-for-byte");
        resolveMs=timer.ElapsedMilliseconds;
        // The same status re-analysed into a new batch is content-addressed.
        var again=(Dictionary<string,string>)Call(review,carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,new[]{5},false));
        Check(Object.ReferenceEquals(again["record-5.LOC"],merged["record-5.LOC"])&&Stored()==storedAfterSpill,"identical status reuses the canonical reference without new spill bytes");

        // 3. Named-archive index + diagnostics.bin.
        index=(string)Call(review,null,"SerializeAlgorithmDiagnosticReferences",merged);
        Check(Encoding.UTF8.GetByteCount(index)<files*200,"archive session.json diagnostics index is tiny ("+index.Length+" chars)");
        archive=Path.Combine(qa,"archive");Directory.CreateDirectory(archive);
        member=Path.Combine(archive,(string)pack.GetField("MemberName",F).GetValue(null));
        packBytes=(long)Call(pack,null,"Write",member,((IEnumerable<string>)Call(review,null,"AlgorithmDiagnosticReferences",merged)).ToArray());
        Check(packBytes==new FileInfo(member).Length&&packBytes<storedAfterSpill+files*64,"diagnostics.bin stores the compressed payloads once");
        paths=merged.Keys.Select(key=>Path.Combine(root,key)).ToArray();
        var fromArchive=(Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",index,root,paths,archive,null);
        Check(fromArchive.Count==files&&fromArchive.All(entry=>Object.ReferenceEquals(entry.Value,merged[entry.Key])),"archive restore adopts live references");
        string json=index;string[] keys=paths;
        Reject(()=>Call(review,null,"ParseAlgorithmDiagnostics",json,root,keys),"references are rejected outside an archive or rollback","require their analysis archive");
        var inProcess=(Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",index,root,paths,null,null,true);
        Check(inProcess.Count==files,"in-process rollback restores references");
        Reject(()=>Call(review,null,"ParseAlgorithmDiagnostics",json,root,keys,Path.Combine(qa,"missing"),null),"archive without diagnostics.bin is rejected","diagnostics.bin");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool RestoresAll(string index,string root,string[] paths,string archive,int files)
    {
        var restored=(Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",index,root,paths,archive,null);
        return restored.Count==files&&Enumerable.Range(0,files).All(id=>Resolve(restored["record-"+id+".LOC"])==Status(id));
    }
    static int Main(string[] args)
    {
        try{
            if(args.Length!=2)throw new ArgumentException("Expected exact DataAnalysis DLL and owned QA directory.");
            string dll=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))Directory.Delete(qa,true);Directory.CreateDirectory(qa);
            string spillRoot=Path.Combine(qa,"spill");Environment.SetEnvironmentVariable("HVX_DIAGNOSTICS_SPILL_ROOT",spillRoot);
            string root=Path.Combine(qa,"project")+Path.DirectorySeparatorChar;Directory.CreateDirectory(root);
            Assembly assembly=Assembly.LoadFrom(dll);
            review=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            spill=assembly.GetType("HVX.PdReview.ReviewDiagnosticSpill",true);
            pack=assembly.GetType("HVX.PdReview.ReviewDiagnosticArchivePack",true);
            var budget=assembly.GetType("HVX.PdReview.ReviewAnalysisMetadataBudget",true);
            Check((long)budget.GetField("DiagnosticsSpillBytes",F).GetValue(null)==1024L*MiB,"absolute spill ceiling is 1 GiB");
            Check((long)budget.GetField("DecodedDiagnosticsBytes",F).GetValue(null)==2048L*MiB,"referenced decoded ceiling is 2 GiB");
            Check((long)budget.GetField("LedgerValidationWorkBytes",F).GetValue(null)==8192L*MiB,"ledger validation work ceiling is 8 GiB");

            const int files=100;
            long decodedTotal,storedAfterSpill,resident,spillMs,resolveMs,packBytes;string index,archive,member,spillDirectory;string[] paths;
            SpillPhase(assembly,qa,root,spillRoot,files,out decodedTotal,out storedAfterSpill,out resident,out spillMs,out resolveMs,out packBytes,out index,out archive,out member,out spillDirectory,out paths);
            object carrier=FormatterServices.GetUninitializedObject(review);
            Set(carrier,"projectPath",root);Set(carrier,"algorithmDiagnostics",new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
            // 4. Release: drop every reference, sweep, then restore from the archive.
            string firstKey="record-0.LOC";
            Call(spill,null,"CollectUnreferenced");
            Check(Entries()==0&&Stored()==0&&Directory.GetFiles(spillDirectory,"*.gz").Length==0,"unreferenced statuses are deleted from the spill store");
            Check(RestoresAll(index,root,paths,archive,files),"diagnostics.bin restores every status losslessly after release");
            Call(spill,null,"CollectUnreferenced");
            Check(Entries()==0,"restored references are released again");
            byte[] corrupt=File.ReadAllBytes(member);corrupt[corrupt.Length-20]^=0x5a;File.WriteAllBytes(member,corrupt);
            Reject(()=>Call(review,null,"ParseAlgorithmDiagnostics",index,root,new[]{paths[paths.Length-1]},archive,null),"corrupted diagnostics.bin payload is rejected");
            Check(Rejected(()=>Call(review,null,"ParseAlgorithmDiagnostics",index,root,new[]{Path.Combine(root,firstKey)},archive,null))==null,"undamaged entries in the same pack still restore");

            // 5. Self-contained host export above 32 MiB: inline segmented
            //    envelope, still bounded by the 64 MiB session-metadata ceiling.
            var selfContained=(Dictionary<string,string>)Call(review,carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,Enumerable.Range(0,60),true));
            string exported=(string)Call(review,null,"SerializeAlgorithmDiagnostics",selfContained);
            int exportedBytes=Encoding.UTF8.GetByteCount(exported);
            Check(exportedBytes>32*MiB&&exportedBytes<=2*64*MiB&&exported.Contains("HVX.AlgorithmDiagnostics.Segmented/1")&&exported.Contains("inlineParts"),"export above 32 MiB uses inline segments ("+exportedBytes+" bytes)");
            var selfPaths=selfContained.Keys.Select(key=>Path.Combine(root,key)).ToArray();
            var reparsed=(Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",exported,root,selfPaths);
            Check(reparsed.Count==60&&Enumerable.Range(0,60).All(id=>Resolve(reparsed["record-"+id+".LOC"])==Status(id)),"inline segmented export reloads losslessly");
            exported=null;reparsed=null;
            var tooLarge=(Dictionary<string,string>)Call(review,carrier,"WithCompletedAlgorithmDiagnostics",Completed(assembly,root,Enumerable.Range(0,files),true));
            Reject(()=>Call(review,null,"SerializeAlgorithmDiagnostics",tooLarge),"self-contained export stays capped at 64 MiB","64 MiB self-contained");
            tooLarge=null;

            // 6. Archives written by the 32 MiB segmentation build (part files).
            string legacyArchive=Path.Combine(qa,"segmented");Directory.CreateDirectory(legacyArchive);
            var serializer=new JavaScriptSerializer{MaxJsonLength=64*MiB};
            File.WriteAllText(Path.Combine(legacyArchive,"diagnostics-part-001.json"),serializer.Serialize(new Dictionary<string,string>{{"record-1.LOC",Status(1)}}),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(legacyArchive,"diagnostics-part-002.json"),serializer.Serialize(new Dictionary<string,string>{{"record-2.LOC",Status(2)}}),new UTF8Encoding(false));
            string envelope=serializer.Serialize(new Dictionary<string,object>{{"schema","HVX.AlgorithmDiagnostics.Segmented/1"},{"version",1},
                {"parts",new[]{"diagnostics-part-001.json","diagnostics-part-002.json"}},{"recordCount",2},{"encodedBytes",1}});
            var legacy=(Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",envelope,root,new[]{Path.Combine(root,"record-1.LOC"),Path.Combine(root,"record-2.LOC")},legacyArchive,null);
            Check(legacy.Count==2&&Resolve(legacy["record-1.LOC"])==Status(1)&&Resolve(legacy["record-2.LOC"])==Status(2),"segmented part-file archives remain readable");
            var flat=(Dictionary<string,string>)Call(review,null,"ParseAlgorithmDiagnostics",serializer.Serialize(new Dictionary<string,string>{{"legacy.LOC","{\"ok\":true}"}}),root,new[]{Path.Combine(root,"legacy.LOC")});
            Check(flat["legacy.LOC"]=="{\"ok\":true}","small legacy statuses stay inline");

            // 7. Finite ceilings, enforced from reference metadata.
            string fake=SpillPrefix+new string('A',64)+":"+(4*MiB)+":0";
            var overDecoded=Enumerable.Range(0,513).ToDictionary(i=>"f"+i+".LOC",i=>fake);
            Reject(()=>Call(review,null,"CheckAlgorithmDiagnosticsBudget",overDecoded),"referenced decoded evidence is capped at 2 GiB","2 GiB");
            Check(Rejected(()=>Call(review,null,"CheckAlgorithmDiagnosticsBudget",overDecoded.Take(512).ToDictionary(e=>e.Key,e=>e.Value)))==null,"exactly 2 GiB of referenced evidence is accepted");
            Reject(()=>Call(review,null,"RetainCompletedStatus",new string('x',MiB+1)),"per-file 1 Mi character status limit retained");
            Console.WriteLine("ANALYSIS_DIAGNOSTIC_SPILL_OFFLINE_PASS="+checks+";files="+files+";decodedBytes="+decodedTotal+";spillBytes="+storedAfterSpill+
                ";diagnosticsBin="+packBytes+";residentManagedBytes="+resident+";spillMs="+spillMs+";resolveMs="+resolveMs+";hardwareUsed=false;productConstructed=false");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}
    }
}
