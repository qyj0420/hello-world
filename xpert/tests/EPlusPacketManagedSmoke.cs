using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using HVX.PdReview;

// Independent QA executable only; no application entry point, worker or SDK.
static class EPlusPacketManagedSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Assembly assembly;static int checks;
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Invoke(string t,string n,params object[] a){return assembly.GetType("HVX.PdReview."+t,true).GetMethod(n,F).Invoke(null,a);}
    static void Check(bool b,string m){if(!b)throw new Exception(m);checks++;}
    static string Hash(string p){using(var f=File.OpenRead(p))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128};}
    static void Save(string p,object o){using(var f=new StreamWriter(new FileStream(p,FileMode.CreateNew,FileAccess.Write),new UTF8Encoding(false)))f.Write(Json().Serialize(o));}
    static object Read(string p){return Invoke("ReviewLocationFileReader","Read",p,Path.GetDirectoryName(p));}
    static object Request(string p){var r=Activator.CreateInstance(assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true),true);Set(r,"ProjectPath",Path.GetDirectoryName(p));Set(r,"LocationFilePath",p);Set(r,"LocationDataId",1L);Set(r,"AlgorithmIndex",9);Set(r,"MaxPulses",100);Set(r,"AutomaticThresholdMultiplier",5.0);Set(r,"AttenuationTolerancePercent",16.0);return Invoke("ReviewAnalysisEngine","Snapshot",r);}
    static int Export(string list,string output)
    {
        Directory.CreateDirectory(output);var fixtures=new List<object>();var paths=(object[])Json().DeserializeObject(File.ReadAllText(list,Encoding.UTF8));int index=0;
        foreach(string path in paths){string p=Path.GetFullPath(path),before=Hash(p),cable=Path.Combine(Path.GetDirectoryName(p),"CABLE.in"),cb=Hash(cable);object data=Read(p),request=Request(p);double[] wave=(double[])Field(data,"ChargeSamples");double fs=(double)Field(data,"SampleRateHz"),v=(double)Field(data,"VelocityMPerUs");object[] args={cable,"A",v,0.0,0.0};Invoke("ReviewEProjectInput","ReadGeometry",args);double length=(double)args[3],speed=(double)args[4];string id=(++index).ToString("D2"),binary=Path.Combine(output,id+".f64");using(var w=new BinaryWriter(new FileStream(binary,FileMode.CreateNew,FileAccess.Write)))foreach(double value in wave)w.Write(value);
            var item=new Dictionary<string,object>{{"id",id},{"path",p},{"sourceSha256",before},{"cableSha256",cb},{"samples",wave.Length},{"waveformSha256",Hash(binary)},{"fs",fs},{"length",length},{"velocity",speed},{"qualityReferenceOnly",Field(Field(request,"AcquisitionQuality"),"ManualOverRangeSaved")},{"binary",binary}};
            item["fsBits"]=BitConverter.DoubleToInt64Bits(fs).ToString("X16");item["lengthBits"]=BitConverter.DoubleToInt64Bits(length).ToString("X16");item["velocityBits"]=BitConverter.DoubleToInt64Bits(speed).ToString("X16");
            Save(Path.Combine(output,id+".input.json"),item);fixtures.Add(item);Check(Hash(p)==before&&Hash(cable)==cb,"input changed during managed export");}
        Save(Path.Combine(output,"inputs.json"),fixtures);Console.WriteLine(Json().Serialize(new{passed=true,checks=checks,fixtures=fixtures.Count,reader="actual ReviewLocationFileReader and ReviewEProjectInput",hardwareUsed=false,productStarted=false}));return 0;
    }
    static int Adapt(string inputDirectory,string output)
    {
        Directory.CreateDirectory(output);assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,new Func<string>(()=>Path.Combine(output,"preferences.json")));var fixtures=(object[])Json().DeserializeObject(File.ReadAllText(Path.Combine(inputDirectory,"inputs.json"),Encoding.UTF8));var summaries=new List<object>();
        foreach(Dictionary<string,object> f in fixtures){string id=(string)f["id"],path=(string)f["path"];Check(Hash(path)==(string)f["sourceSha256"],"source hash before adaptation");object data=Read(path),request=Request(path);var evidence=(Dictionary<string,object>)Json().DeserializeObject(File.ReadAllText(Path.Combine(inputDirectory,id+".new-observed_spectral.json"),Encoding.UTF8));double fs=Convert.ToDouble(f["fs"]),length=Convert.ToDouble(f["length"]),speed=Convert.ToDouble(f["velocity"]);
            fs=BitConverter.Int64BitsToDouble(Convert.ToInt64((string)f["fsBits"],16));length=BitConverter.Int64BitsToDouble(Convert.ToInt64((string)f["lengthBits"],16));speed=BitConverter.Int64BitsToDouble(Convert.ToInt64((string)f["velocityBits"],16));
            object result=Invoke("ReviewEAnalysis","Adapt",request,data,1,length,speed,Double.NaN,evidence);result=Invoke("ReviewAcquisitionQuality","Apply",result,Field(request,"AcquisitionQuality"));var p=(double[,])Field(result,"Parameters");Check((bool)Field(result,"Success")&&p.GetLength(0)<=100&&p.GetLength(1)==36,"bounded matrix result");
            for(int r=0;r<p.GetLength(0);r++){Check(p[r,7]==fs&&p[r,26]==9&&p[r,27]==1,"sample rate and algorithm identity "+id+" row="+r+" fs="+fs.ToString("R")+" actual="+p[r,7].ToString("R")+" method="+p[r,26]+" plus="+p[r,27]);Check(p[r,10]==Math.Truncate(p[r,10]),"original integer sample time");if(!Double.IsNaN(p[r,11])){Check(p[r,12]==p[r,11]-p[r,10],"sample delay unchanged");Check(Math.Abs(p[r,13]-(length-p[r,12]/fs*1e6*speed/2))<1e-9,"raw geometry unchanged");}}
            string status=(string)Field(result,"Status");var parsed=(Dictionary<string,object>)Json().DeserializeObject(status);Check((string)parsed["version"]=="Eplus-final-raw-packet-score-r01"&&(bool)parsed["auxiliary_lowpass_applied"]==false,"named candidate/no fake auxiliary");var ledger=(Dictionary<string,object>)Invoke("ReviewEAnalysis","UnpackLedger",parsed["upstream_evidence"]);Check(((object[])ledger["events"]).Length==((object[])evidence["events"]).Length,"complete candidate ledger retained");
            var rows=((object[])evidence["events"]).Cast<Dictionary<string,object>>().OrderByDescending(e=>Object.Equals(e["engineering_eligible"],true)).ThenByDescending(e=>Math.Abs(Convert.ToDouble(e["q_pc"]))).ThenBy(e=>Convert.ToDouble(e["incident"])).Take(p.GetLength(0)).ToArray();
            for(int row=0;row<rows.Length;row++){var score=(Dictionary<string,object>)rows[row]["final_pair_score"];double expected=score["score"]==null?Double.NaN:Convert.ToDouble(score["score"]);Check(Double.IsNaN(expected)?Double.IsNaN(p[row,33]):p[row,33]==expected,"column33 is linear final S, including failed qualification diagnostic");if(!Object.Equals(score["qualified"],true))Check(p[row,35]==0,"failed qualification cannot autoaccept high score");}
            // Exercise real quality gate with synthetic risk metadata, not by changing source files.
            object risk=Invoke("ReviewAcquisitionQuality","FromContext",new Dictionary<string,object>{{"manualOverRangeSaved",true},{"overRange",true},{"peakValidity","clipped"},{"locationValidity","reference_only"}});Invoke("ReviewAcquisitionQuality","Apply",result,risk);Check(Enumerable.Range(0,p.GetLength(0)).All(r=>p[r,35]==0),"risk quality always clears column35");
            string uiRoot=Path.Combine(output,id+"-ui-project");Directory.CreateDirectory(uiRoot);
            foreach(string n in new[]{Path.GetFileName(path),"CABLE.in","ITEM.in"}){string from=Path.Combine(Path.GetDirectoryName(path),n);if(File.Exists(from))File.Copy(from,Path.Combine(uiRoot,n),false);}
            using(var control=new PdReviewControl()){control.ProjectPath=uiRoot;Check(control.SetProjectLocationIds(new[]{Path.Combine(uiRoot,Path.GetFileName(path))},new[]{1.0}),control.LastError);Check(control.LoadDataMatrices(p,(double[,])Field(result,"Raw")),control.LastError);Check(control.SourceRowCount==p.GetLength(0),"UI receives all bounded matrix rows");}
            var cache=assembly.GetType("HVX.PdReview.PressureAnalysisCache",true);string key=(string)cache.GetMethod("Key",F).Invoke(null,new[]{(object)"packet-test",request});Check(!String.IsNullOrEmpty(key),"known quality scalar cache identity supported");Set(request,"AutomaticThresholdMultiplier",6.0);string changed=(string)cache.GetMethod("Key",F).Invoke(null,new[]{(object)"packet-test",request});Check(changed!=key,"settings invalidate cached result");Set(request,"AcquisitionQuality",risk);string riskKey=(string)cache.GetMethod("Key",F).Invoke(null,new[]{(object)"packet-test",request});Check(riskKey!=changed,"risk quality invalidates cached result");
            Check(Hash(path)==(string)f["sourceSha256"],"source unchanged after UI adaptation");summaries.Add(new{id=id,rows=p.GetLength(0),ledgerEvents=((object[])ledger["events"]).Length,qualityColumn35Gated=true,originalSampleTiming=true});}
        Save(Path.Combine(output,"verification-managed.json"),new{schema="HVX.EPlusPacket.ManagedSameInput/1",passed=true,checks=checks,dllSha256=Hash(assembly.Location),fixtures=summaries,hardwareUsed=false,productStarted=false,physicalValidation=false});Console.WriteLine(Json().Serialize(new{passed=true,checks=checks,fixtures=summaries.Count}));return 0;
    }
    [STAThread]static int Main(string[] args){try{Console.OutputEncoding=new UTF8Encoding(false);if(args.Length!=4)throw new ArgumentException("dll, export/adapt, input, output required");string dll=Path.GetFullPath(args[0]);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(Path.GetDirectoryName(dll),new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};assembly=Assembly.LoadFrom(dll);return args[1]=="export"?Export(args[2],args[3]):Adapt(args[2],args[3]);}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
