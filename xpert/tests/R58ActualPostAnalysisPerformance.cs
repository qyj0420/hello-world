using System;
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
using System.Windows.Forms;

// Offline component timing. Never starts the product, algorithm, worker or SDK.
// Actual sources and archive are read only. All save fixtures live under qaRoot.
static class R58ActualPostAnalysisPerformance
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly assembly;static Type review;static string qa;static int sequence;
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=64*1024*1024,RecursionLimit=128};}
    static object Call(object target,string name,params object[] args)
    {
        Type type=target as Type??target.GetType();
        try{return type.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}
        catch(TargetInvocationException e){throw e.InnerException;}
    }
    static object Get(object target,string name){return target.GetType().GetField(name,F).GetValue(target);}
    static void Set(object target,string name,object value){target.GetType().GetField(name,F).SetValue(target,value);}
    static object Prop(object target,string name){return target.GetType().GetProperty(name,F).GetValue(target,null);}
    static void Put(object target,string name,object value){target.GetType().GetProperty(name,F).SetValue(target,value,null);}
    static void Check(bool yes,string why){if(!yes)throw new Exception(why);}
    static string Hash(string path)
    {using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");}
    static string Decode(string text)
    {
        const string prefix="HVX.Diagnostics.Gzip.v1:";if(!text.StartsWith(prefix,StringComparison.Ordinal))return text;
        using(var input=new MemoryStream(Convert.FromBase64String(text.Substring(prefix.Length))))
        using(var gzip=new GZipStream(input,CompressionMode.Decompress))
        using(var reader=new StreamReader(gzip,new UTF8Encoding(false,true)))return reader.ReadToEnd();
    }
    static Dictionary<string,object> Measure(string name,Action action,int repetitions=3)
    {
        var values=new List<double>();
        for(int i=0;i<repetitions+1;i++){
            GC.Collect();GC.WaitForPendingFinalizers();var watch=Stopwatch.StartNew();action();watch.Stop();
            if(i>0)values.Add(watch.Elapsed.TotalMilliseconds);
        }
        double median=values.OrderBy(x=>x).ElementAt(values.Count/2);
        Console.WriteLine(name+" medianMs="+median.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" samples="+String.Join(",",values.Select(x=>x.ToString("F3",System.Globalization.CultureInfo.InvariantCulture))));
        return new Dictionary<string,object>{{"Name",name},{"SamplesMilliseconds",values.ToArray()},{"MedianMilliseconds",median}};
    }
    static object Result(double[,] p,double[,] w,string status)
    {
        int rows=p.GetLength(0),width=w.GetLength(1);var r=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisResult",true),true);
        Set(r,"Success",true);Set(r,"Status",status);Set(r,"Parameters",p);
        for(int b=0;b<3;b++){
            var part=new double[rows,width];Buffer.BlockCopy(w,b*rows*width*8,part,0,part.Length*8);
            Set(r,new[]{"Raw","Filtered","Equivalent"}[b],part);
        }return r;
    }
    static Array Results(object r){var array=Array.CreateInstance(r.GetType(),1);array.SetValue(r,0);return array;}
    static Array Completed(string root,string relative,object r)
    {
        var request=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);
        Set(request,"ProjectPath",root);Set(request,"LocationFilePath",Path.Combine(root,relative));
        var item=Activator.CreateInstance(review.GetNestedType("CompletedFile",F),true);Set(item,"Request",request);Set(item,"Result",r);
        var array=Array.CreateInstance(item.GetType(),1);array.SetValue(item,0);return array;
    }
    static object Carrier(string root,Dictionary<string,string> diagnostics,double[,] p=null,double[,] w=null)
    {
        var c=FormatterServices.GetUninitializedObject(review);Set(c,"projectPath",root);Set(c,"algorithmDiagnostics",diagnostics);
        if(p!=null){Set(c,"working",p);Set(c,"original",p);Set(c,"matching",w);}return c;
    }
    static void Subset(double[,] p,double[,] w,int[] rows,out double[,] result,out double[,] waves)
    {
        int n=p.GetLength(0),width=w.GetLength(1);result=new double[rows.Length,p.GetLength(1)];waves=new double[3*rows.Length,width];
        for(int i=0;i<rows.Length;i++){
            for(int j=0;j<p.GetLength(1);j++)result[i,j]=p[rows[i],j];result[i,9]=i;
            for(int b=0;b<3;b++)Buffer.BlockCopy(w,(b*n+rows[i])*width*8,waves,(b*rows.Length+i)*width*8,width*8);
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Check(args.Length>=5,"Expected module, actual project, archive ID, owned QA root, report path, optional LOC file name.");
            string module=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]),id=args[2];qa=Path.GetFullPath(args[3]);Directory.CreateDirectory(qa);
            Check(!qa.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"QA must not be inside original project.");
            assembly=Assembly.LoadFrom(module);review=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,new Func<string>(()=>Path.Combine(qa,"preferences.json")));
            assembly.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,new Func<string>(()=>Path.Combine(qa,"journals")));
            string archive=Path.Combine(root,"Analysis report",id),sessionFile=Path.Combine(archive,"session.json");
            var sourceHashes=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly).Concat(Directory.GetFiles(archive,"*",SearchOption.AllDirectories)).ToDictionary(p=>p,Hash,StringComparer.OrdinalIgnoreCase);
            var payload=(Dictionary<string,object>)Json().DeserializeObject(File.ReadAllText(sessionFile,new UTF8Encoding(false,true)));
            var files=(object[])payload["files"];var settings=(object[])payload["settings"];
            string encoded=(string)((object[])settings.Single(x=>Object.Equals(((object[])x)[0],"AlgorithmDiagnostics")))[1];
            var diagnostics=((Dictionary<string,object>)Json().DeserializeObject(encoded)).ToDictionary(p=>p.Key,p=>Decode((string)p.Value),StringComparer.OrdinalIgnoreCase);
            var allP=(double[,])Call(review,"ReadMatrix",Path.Combine(archive,"parameters.bin"),10000000L);
            var allW=(double[,])Call(review,"ReadMatrix",Path.Combine(archive,"pdmap.bin"),32000000L);
            string relative=args.Length>5?args[5]:diagnostics.Keys.OrderBy(p=>p,StringComparer.Ordinal).First(p=>p.StartsWith("C_",StringComparison.OrdinalIgnoreCase));
            var metadata=(object[])files.Single(x=>String.Equals((string)((object[])x)[0],relative,StringComparison.OrdinalIgnoreCase));
            double phase=Double.Parse((string)metadata[1],System.Globalization.CultureInfo.InvariantCulture),fileId=Double.Parse((string)metadata[2],System.Globalization.CultureInfo.InvariantCulture);
            var selected=Enumerable.Range(0,allP.GetLength(0)).Where(r=>allP[r,0]==phase&&allP[r,1]==fileId).ToArray();
            Check(selected.Length>0&&diagnostics.ContainsKey(relative),"chosen LOC must have archived results and diagnostics");
            double[,] oneP,oneW,oldP,oldW;Subset(allP,allW,selected,out oneP,out oneW);
            Subset(allP,allW,Enumerable.Range(0,allP.GetLength(0)).Except(selected).ToArray(),out oldP,out oldW);
            object result=Result(oneP,oneW,diagnostics[relative]);Array resultArray=Results(result),completed=Completed(root,relative,result);
            var oneDiagnostics=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{relative,diagnostics[relative]}};
            var oneCarrier=Carrier(root,new Dictionary<string,string>());var fullCarrier=Carrier(root,diagnostics);
            var measurements=new List<Dictionary<string,object>>();object sink=null;
            measurements.Add(Measure("ledger_validate_single",()=>sink=Call(assembly.GetType("HVX.PdReview.ReviewEAnalysis",true),"ValidateLedgerStatus",diagnostics[relative])));
            measurements.Add(Measure("diagnostics_merge_single_into_empty",()=>sink=Call(oneCarrier,"WithCompletedAlgorithmDiagnostics",completed)));
            measurements.Add(Measure("diagnostics_merge_single_into_existing_31",()=>sink=Call(fullCarrier,"WithCompletedAlgorithmDiagnostics",completed)));
            string singleEncoded=null,fullEncoded=null;
            measurements.Add(Measure("diagnostics_serialize_single",()=>singleEncoded=(string)Call(review,"SerializeAlgorithmDiagnostics",oneDiagnostics)));
            measurements.Add(Measure("diagnostics_serialize_all_31",()=>fullEncoded=(string)Call(review,"SerializeAlgorithmDiagnostics",diagnostics)));
            measurements.Add(Measure("diagnostics_parse_validate_all_31",()=>sink=Call(review,"ParseAlgorithmDiagnostics",fullEncoded,root+Path.DirectorySeparatorChar,diagnostics.Keys.Select(p=>Path.Combine(root,p)).ToArray())));
            measurements.Add(Measure("matrices_merge_single_empty",()=>{object[] a={new double[0,36],new double[0,0],new double[0,36],resultArray,null,null};Call(review,"MergeAnalysisResultsCore",a);sink=a[5];}));
            measurements.Add(Measure("matrices_merge_single_into_remaining_644",()=>{object[] a={oldP,oldW,oldP,resultArray,null,null};Call(review,"MergeAnalysisResultsCore",a);sink=a[5];}));
            var subsetCarrier=Carrier(root,diagnostics,allP,allW);
            measurements.Add(Measure("matrices_export_subset_all_644",()=>{object[] a={Enumerable.Range(0,allP.GetLength(0)).ToArray(),null,null};Call(subsetCarrier,"SubsetWorkspace",a);sink=a[2];}));
            measurements.Add(Measure("archive_write_parameters_all_644",()=>Call(review,"WriteMatrix",Path.Combine(qa,"parameters-"+(sequence++)+".bin"),allP)));
            measurements.Add(Measure("archive_write_waveforms_all_644",()=>Call(review,"WriteMatrix",Path.Combine(qa,"waveforms-"+(sequence++)+".bin"),allW)));
            measurements.Add(Measure("archive_read_waveforms_all_644",()=>sink=Call(review,"ReadMatrix",Path.Combine(archive,"pdmap.bin"),32000000L)));
            measurements.Add(Measure("archive_hash_3_required_members",()=>{foreach(string name in new[]{"parameters.bin","pdmap.bin","session.json"})sink=Call(review,"SessionHash",Path.Combine(archive,name));}));
            long sourcesBytes=Directory.GetFiles(root,"*.loc").Sum(p=>new FileInfo(p).Length);
            measurements.Add(Measure("session_hash_all_loc_sources",()=>{foreach(string p in Directory.GetFiles(root,"*.loc"))sink=Call(review,"SessionHash",p);}));
            // A single-record derived fixture copies original bytes; the original is never written.
            string fixture=Path.Combine(qa,"single-record-project");Directory.CreateDirectory(fixture);
            File.Copy(Path.Combine(root,relative),Path.Combine(fixture,relative),false);
            foreach(string file in Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly).Where(p=>p.EndsWith(".cal",StringComparison.OrdinalIgnoreCase)||String.Equals(Path.GetFileName(p),"ITEM.in",StringComparison.OrdinalIgnoreCase)||String.Equals(Path.GetFileName(p),"CABLE.in",StringComparison.OrdinalIgnoreCase)))File.Copy(file,Path.Combine(fixture,Path.GetFileName(file)),false);
            using(var control=(Control)Activator.CreateInstance(review)){
                Put(control,"PromptSavedAnalysisOnProjectLoad",false);Put(control,"EnableAiFeatures",false);Put(control,"ProjectPath",fixture);
                Check((bool)Call(control,"SetProjectLocationIds",new[]{Path.Combine(fixture,relative)},new[]{fileId}),"fixture catalog mapping");
                Check((bool)Call(control,"ForceReloadDataMatrices",oneP,oneW),"fixture matrix load "+Prop(control,"LastError"));
                Check((bool)Call(Get(control,"analysisTree"),"MarkAnalyzed",new object[]{new[]{Path.Combine(fixture,relative)}}),"fixture analyzed state");
                Set(control,"algorithmDiagnostics",oneDiagnostics);
                var retained=Enumerable.Range(0,oneP.GetLength(0)).ToArray();
                measurements.Add(Measure("publish_single_result_review_control",()=>{Set(control,"algorithmDiagnostics",oneDiagnostics);Check((bool)Call(control,"ReloadWithRetainedReviewCore",oneP,oneW,retained,false,Get(control,"acquisitionQualities")),"fixture publish "+Prop(control,"LastError"));}));
                measurements.Add(Measure("session_export_single_record",()=>{object[] a={null,null,null};Check((bool)Call(control,"ExportAnalysisSessionUtf8",a),"fixture export "+Prop(control,"LastError"));sink=a[2];}));
                measurements.Add(Measure("archive_save_single_record_end_to_end",()=>Check((bool)Call(control,"SaveNamedAnalysis","Performance fixture "+(sequence++)),"fixture save "+Prop(control,"LastError"))));
            }
            Check(sourceHashes.All(p=>File.Exists(p.Key)&&Hash(p.Key)==p.Value),"original bytes changed");
            Check(sourceHashes.Count==Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly).Length+Directory.GetFiles(archive,"*",SearchOption.AllDirectories).Length,"original file list changed");
            var proof=new Dictionary<string,object>{{"Schema","HVX.R58.ActualPostAnalysis.Performance/1"},{"Module",module},{"ModuleSha256",Hash(module)},
                {"SourceArchive",id},{"SessionSha256",sourceHashes[sessionFile]},{"LocationFile",relative},{"LocationSha256",sourceHashes[Path.Combine(root,relative)]},
                {"LocationBytes",new FileInfo(Path.Combine(root,relative)).Length},{"SingleRows",selected.Length},{"AllRows",allP.GetLength(0)},{"WaveformWidth",allW.GetLength(1)},
                {"DiagnosticRecords",diagnostics.Count},{"SingleStatusCharacters",diagnostics[relative].Length},{"AllStatusCharacters",diagnostics.Sum(p=>(long)p.Value.Length)},
                {"AllLocSourceBytes",sourcesBytes},{"SingleEncodedBytes",Encoding.UTF8.GetByteCount(singleEncoded)},{"AllEncodedBytes",Encoding.UTF8.GetByteCount(fullEncoded)},
                {"Measurements",measurements.ToArray()},{"OriginalProjectUnchanged",true},{"ProductEntry",false},{"Algorithm",false},{"Worker",false},{"SDK",false},{"Hardware",false},
                {"Notes","One unreported warm-up per measurement; then 3 sequential samples. GC before, not within timed action. Source file cache is warm. End-to-end single save uses an owned single-record derived fixture, not the full original project. Components are timed separately, not a whole analysis latency claim."}};
            File.WriteAllText(args[4],Json().Serialize(proof),new UTF8Encoding(false));GC.KeepAlive(sink);
            Console.WriteLine("R58_ACTUAL_POST_ANALYSIS_PERFORMANCE_PASS;singleRows="+selected.Length+";allRows="+allP.GetLength(0)+";OriginalProjectWritten=false;SDK=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
