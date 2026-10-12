using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Actual immutable inputs copied byte-for-byte into owned QA; managed frozen
// A/B only, no product Main, device, preference writes or acceptance decisions.
internal static class AnalysisActualCalibrationLocReadonly
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly asm; static Type store; static int checks;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new InvalidOperationException(text); }
    static object Call(object owner, string name, params object[] args) { Type type = owner as Type ?? owner.GetType(); return type.GetMethods(F).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner is Type ? null : owner, args); }
    static object Get(object owner, string name) { var p = owner.GetType().GetProperty(name,F); return p != null ? p.GetValue(owner,null) : owner.GetType().GetField(name,F).GetValue(owner); }
    static void Set(object owner, string name, object value) { var p = owner.GetType().GetProperty(name,F); if(p != null) p.SetValue(owner,value,null); else owner.GetType().GetField(name,F).SetValue(owner,value); }
    static string Hash(string path) { using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-",""); }
    static Dictionary<string,object> Record(params object[] parts) { var result = new Dictionary<string,object>(); for(int i=0;i<parts.Length;i+=2) result.Add((string)parts[i], parts[i+1]); return result; }
    static object Number(double value) { return Double.IsNaN(value)||Double.IsInfinity(value)?null:(object)value; }
    static IEnumerable<string> Values(object value,string key) {
        var d=value as Dictionary<string,object>; if(d!=null) foreach(var pair in d) { if(pair.Key==key&&pair.Value is string) yield return (string)pair.Value; foreach(var child in Values(pair.Value,key)) yield return child; }
        else { var list=value as object[]; if(list!=null) foreach(var entry in list) foreach(var child in Values(entry,key)) yield return child; }
    }
    static object[] Catalog(string root) {
        Type selector=asm.GetType("LabVIEWPdMap.ProjectCalibrationImplementation.Selector",true); object[] args={root,null};
        return ((IEnumerable)Call(selector,"ReadCatalog",args)).Cast<object>().ToArray();
    }
    static double Frequency(string dem) {
        foreach(string line in File.ReadLines(dem).Take(30)) { var parts=line.Split('\t'); double value;
            if(parts.Length>1&&parts[0].Trim().TrimEnd(':').Equals("Hz",StringComparison.OrdinalIgnoreCase)&&Double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out value)&&value>0&&value<1000000)return value; }
        throw new InvalidDataException("Actual DEM does not declare finite oscillation frequency; no inferred frequency was used.");
    }
    static bool Finite(double value) { return !Double.IsNaN(value)&&!Double.IsInfinity(value); }
    static int Main(string[] args) {
        var evidence=Record("schema","HVX.Actual.CalibrationLoc.Readonly/1","hardwareAccessed",false,"sdkCalled",false,"productEntryExecuted",false,"userProjectWritten",false,"physicalAccuracyVerified",false,"acceptanceChanged",false,"fullRegression",false);
        string qa=null;
        try {
            if(args.Length!=3)throw new ArgumentException("Pass published build, QA directory and explicitly named readonly project.");
            string build=Path.GetFullPath(args[0]), source=Path.GetFullPath(args[2]); qa=Path.GetFullPath(args[1]); Directory.CreateDirectory(qa);
            asm=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll")); store=asm.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);
            asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"prefs.json")));
            asm.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
            string[] catalogFiles=Directory.GetFiles(source,"*.cal",SearchOption.TopDirectoryOnly);
            string loc=Directory.GetFiles(source,"*.loc",SearchOption.TopDirectoryOnly).Where(p=>!Path.GetFileName(p).StartsWith("A_0kV_",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>new FileInfo(p).Length).ThenBy(p=>p,StringComparer.Ordinal).First();
            Check(new FileInfo(loc).Length<=4*1024*1024,"chosen nonzero actual LOC is bounded below4MiB");
            string dem=Path.ChangeExtension(loc,".dem"); Check(File.Exists(dem),"actual exact-name DEM exists");
            string[] originals=new[]{Path.Combine(source,"ITEM.in"),Path.Combine(source,"CABLE.in"),loc,dem}.Concat(catalogFiles).ToArray();
            var fingerprints=originals.ToDictionary(p=>p,p=>Record("sha256",Hash(p),"length",new FileInfo(p).Length,"writeTimeUtc",File.GetLastWriteTimeUtc(p).ToString("o"),"attributes",File.GetAttributes(p).ToString()),StringComparer.OrdinalIgnoreCase);
            evidence["sourceFiles"]=fingerprints; evidence["moduleSha256"]=Hash(asm.Location); evidence["algorithmSha256"]=Hash(Path.Combine(Path.GetDirectoryName(asm.Location),"fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"));
            var sourceCatalog=Catalog(source); evidence["originalCalibrationCatalog"]=sourceCatalog.Select(c=>Record("file",Path.GetFileName((string)Get(c,"Path")),"valid",Get(c,"Valid"),"reason",Get(c,"Reason"),"live",Get(c,"IsLiveArchive"),"rangeIndex",Get(c,"RangeIndex"),"phase",Get(c,"Phase"),"hasMeasuredReflection",Get(c,"HasMeasuredReflection"),"rateHz",Number((double)Get(c,"Rate")),"coefficientPcPerV",Number((double)Get(c,"Coefficient")))).ToArray();
            Check(sourceCatalog.Any(c=>(bool)Get(c,"Valid")&&(bool)Get(c,"IsLiveArchive")&&(string)Get(c,"Phase")=="ALL"),"actual valid ALL charge calibration catalogued");
            Check(!sourceCatalog.Any(c=>(bool)Get(c,"Valid")&&Path.GetFileName((string)Get(c,"Path")).IndexOf("TDR",StringComparison.OrdinalIgnoreCase)>=0),"TDR is not substituted for charge CAL");
            string clone=Path.Combine(qa,"exact-input-copy"); Directory.CreateDirectory(clone);
            foreach(string file in originals) { string target=Path.Combine(clone,Path.GetFileName(file)); File.Copy(file,target,false); Check(Hash(target)==(string)fingerprints[file]["sha256"],"byte exact QA copy "+Path.GetFileName(file)); }
            string copiedLoc=Path.Combine(clone,Path.GetFileName(loc)); var copiedCatalog=Catalog(clone); Check(copiedCatalog.Any(c=>(bool)Get(c,"Valid")),"self-contained actual CAL validates in exact metadata clone");
            object live=Call(store,"Load",clone,Path.Combine(clone,"ALL_5nC.cal"),true); double[] average=(double[])Get(live,"Average");
            Type scope=asm.GetType("HVX.PdReview.ReviewCalibrationInputScope",true);
            string projection;
            using(var input=(IDisposable)Call(scope,"Create",clone,copiedLoc)) {
                projection=(string)Get(input,"AlgorithmProjectPath"); Check((bool)Get(input,"IsProjected")&&projection!=clone,"actual CAL projected for frozen legacy reader");
                string cal=Path.Combine(projection,"ALL_5nC.cal"); string[] lines=File.ReadAllLines(cal);
                Check(lines.Length==13+average.Length&&lines.Skip(13).Select(v=>Double.Parse(v,CultureInfo.InvariantCulture)).SequenceEqual(average),"actual average projected byte-value exact V without scaling/resampling");
                Check(File.Exists(Path.Combine(projection,Path.GetFileName(dem))),"actual DEM retained for frozen analysis");
                evidence["projectionManifest"]=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue}.DeserializeObject(File.ReadAllText(Path.Combine(projection,"calibration-input-source.json")));
                Call(input,"VerifySources");
            }
            Check(!Directory.Exists(projection),"controlled CAL projection cleaned only own files");
            Type reader=asm.GetType("HVX.PdReview.ReviewLocationFileReader",true); object data=Call(reader,"Read",copiedLoc,clone);
            double[] charge=(double[])Get(data,"ChargeSamples"); double frequency=Frequency(Path.Combine(clone,Path.GetFileName(dem)));
            evidence["locMetadata"]=Record("file",Path.GetFileName(loc),"sampleCount",charge.Length,"sampleRateHz",Get(data,"SampleRateHz"),"engineeringPcDecodedOnce",true,"observedPeakPc",charge.Select(Math.Abs).Max(),"declaredVelocityMPerUs",Get(data,"VelocityMPerUs"),"targetPeakKv",Get(data,"VoltageKv"),"demFrequencyHz",frequency,"calLengthM",Get(live,"LengthM"),"calVelocityMPerUs",Get(live,"VelocityMPerUs"));
            var results=new List<object>(); var json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=256};
            foreach(int method in new[]{5,7}) {
                object request=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewAnalysisRequest",true)); Set(request,"ProjectPath",clone);Set(request,"LocationFilePath",copiedLoc);Set(request,"AlgorithmIndex",method);Set(request,"LocationDataId",100L+method);
                Set(request,"AutomaticAcceptance",false);Set(request,"MaxPulses",10);Set(request,"OscillationFrequencyHz",frequency);Set(request,"UseFirstArrivalTiming",(bool)Get(live,"FirstArrival"));
                object result=Call(asm.GetType("HVX.PdReview.ReviewAnalysisEngine",true),"Analyze",request); string status=(string)Get(result,"Status"); var detail=json.DeserializeObject(status);
                string resolved=Values(detail,"resolved_calibration").FirstOrDefault(p=>p.EndsWith("ALL_5nC.cal",StringComparison.OrdinalIgnoreCase));
                File.WriteAllText(Path.Combine(qa,"method-"+method+"-status.json"),status,new UTF8Encoding(false));
                Check(status.Contains("HVX.CalibrationInputProjection/1"),"actual frozen result retains projection provenance "+method);
                double[,] matrix=(double[,])Get(result,"Parameters"); int rows=matrix.GetLength(0),paired=Enumerable.Range(0,rows).Count(i=>Finite(matrix[i,11])&&matrix[i,11]>=matrix[i,31]&&matrix[i,11]<=matrix[i,32]);
                Check(Enumerable.Range(0,rows).All(i=>matrix[i,35]==0),"audit cannot accept pulses or produce physical verdict "+method);
                if(resolved!=null) Check(!Directory.Exists(Path.GetDirectoryName(resolved)),"actual frozen scope released after invocation "+method);
                results.Add(Record("method",method==5?"A":"B","success",Get(result,"Success"),"calibrationResolvedInFrozenAlgorithm",resolved!=null&&resolved.Contains("HVXAnalysisCalibrationInputs"),"rows",rows,"pairedReflectionRows",paired,"finiteInsideLengthLocationRows",Enumerable.Range(0,rows).Count(i=>Finite(matrix[i,13])&&matrix[i,13]>=0&&matrix[i,13]<=matrix[i,5]),"positiveChargeRows",Enumerable.Range(0,rows).Count(i=>Finite(matrix[i,15])&&Math.Abs(matrix[i,15])>0),"status",detail));
            }
            evidence["analysisResults"]=results.ToArray();
            foreach(string file in originals) { var before=fingerprints[file]; Check(Hash(file)==(string)before["sha256"]&&File.GetLastWriteTimeUtc(file).ToString("o")== (string)before["writeTimeUtc"]&&File.GetAttributes(file).ToString()==(string)before["attributes"],"actual source bytes/time/attributes immutable "+Path.GetFileName(file)); }
            Check(!File.Exists(Path.Combine(qa,"prefs.json")),"no preferences written"); evidence["status"]="PASS"; evidence["checks"]=checks;
            File.WriteAllText(Path.Combine(qa,"actual-cal-loc-readonly.json"),json.Serialize(evidence),new UTF8Encoding(false));
            Console.WriteLine("ACTUAL_CAL_LOC_READONLY_PASS="+checks+"; SOURCE_WRITES=0; HARDWARE=0; PHYSICAL_ACCURACY=false; EVIDENCE="+Path.Combine(qa,"actual-cal-loc-readonly.json")); return 0;
        } catch(Exception error) {
            evidence["status"]="FAIL"; evidence["checks"]=checks; evidence["error"]=error.ToString();
            if(qa!=null)File.WriteAllText(Path.Combine(qa,"actual-cal-loc-readonly.json"),new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue}.Serialize(evidence),new UTF8Encoding(false));
            Console.Error.WriteLine(error);return 1;
        }
    }
}
