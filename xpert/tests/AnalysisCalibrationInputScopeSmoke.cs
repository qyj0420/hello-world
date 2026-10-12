using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Actual frozen A/B managed analysis entry; no SDK, worker or product Main.
internal static class AnalysisCalibrationInputScopeSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static Assembly asm;static Type store,scope,engine;static string qa;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,F);return p!=null?p.GetValue(o,null):Field(o,n);}
    static void Set(object o,string n,object value){var p=o.GetType().GetProperty(n,F);if(p!=null)p.SetValue(o,value,null);else o.GetType().GetField(n,F).SetValue(o,value);}
    static object Call(object o,string n,params object[] a){var t=o as Type??o.GetType();return t.GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o is Type?null:o,a);}
    static string Hash(string path){return (string)Call(store,"HashFile",path);}
    static double[] Wave(int peak,int reflected,double amplitude,bool paired=true)
    {return Enumerable.Range(0,4096).Select(i=>{
        double a=(i-peak)/10d,b=(i-reflected)/10d;return amplitude*(Math.Exp(-a*a/2)-.6*Math.Exp(-(a-3)*(a-3)/2))+
            (paired?amplitude*.6*(Math.Exp(-b*b/2)-.6*Math.Exp(-(b-3)*(b-3)/2)):0)+.00001*amplitude*Math.Sin(i*.73);}).ToArray();}
    static double[] CalibrationWave(bool paired)
    {return Enumerable.Range(0,4096).Select(i=>.1*Math.Exp(-Math.Pow((i-500)/5d,2))+
        (paired?.06*Math.Exp(-Math.Pow((i-750)/5d,2)):0)+.0001*Math.Sin(i*.73)).ToArray();}
    static void Project(string p)
    {
        Directory.CreateDirectory(p);File.WriteAllText(Path.Combine(p,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        string[] header={"Cable length\t170","Velocity (m/us)\t170","Delta x\t8E-9","Phase\t0","Voltage\t12.0",
            "Reserved1\t0","Reserved2\t0","Reserved3\t0","Reserved4\t0","Reserved5\t0","Data"};
        File.WriteAllText(Path.Combine(p,"A_12.0kV_fixture.loc"),String.Join("\r\n",header.Concat(Wave(500,700,1000).Select(v=>(v*100000).ToString("R",CultureInfo.InvariantCulture)))),new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(p,"A_12.0kV_fixture.dem"),"Hz\t50\r\nPhase\t40\r\n",new UTF8Encoding(true));
    }
    static object Live(string p,bool paired=true,string phase="ALL")
    {
        object r=Activator.CreateInstance(store);Set(r,"Version",3);Set(r,"RangeIndex",8);Set(r,"PulseCount",3);Set(r,"FirstArrival",true);
        foreach(var v in new[]{new[]{"Phase",phase},new[]{"Level","5nC"},new[]{"DeviceSerial","MANAGED-FIXTURE"},new[]{"DeviceModel","offline"},
            new[]{"CreatedUtc",DateTime.UtcNow.ToString("o")},new[]{"ProjectHash",(string)Call(store,"Identity",p)},new[]{"ProjectScopeHash",(string)Call(store,"ScopeIdentity",p)},
            new[]{"AcquisitionMode","Pulse"},new[]{"CoefficientProcessing","HP10K_NO_LP"},new[]{"ReflectionStatus",paired?"Detected":"NotDetected"},new[]{"VelocitySource",paired?"Measured":"Configured"}})Set(r,v[0],v[1]);
        Set(r,"FilterId",asm.GetType("HVX.SignalProcessing.PdLinearHighPass",true).GetField("FilterId",F).GetValue(null));
        foreach(var v in new[]{Tuple.Create("LengthM",170d),Tuple.Create("VelocityMPerUs",170d),Tuple.Create("SampleRateHz",125e6),
            Tuple.Create("IncidentSeconds",4e-6),Tuple.Create("ReflectionSeconds",paired?6e-6:Double.NaN),Tuple.Create("CoefficientPcPerV",50000d),
            Tuple.Create("ChargePc",5000d),Tuple.Create("SnrDb",40d),Tuple.Create("Correlation",paired?.9:Double.NaN),Tuple.Create("AttenuationDb",paired?20*Math.Log10(1/.6):Double.NaN)})Set(r,v.Item1,v.Item2);
        Set(r,"IncidentPeakIndex",500);Set(r,"ReflectionPeakIndex",paired?750:-1);Set(r,"Average",CalibrationWave(paired));
        Type reference=asm.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);Array refs=Array.CreateInstance(reference,3);
        for(int i=0;i<3;i++){string name="raw"+i+".bin",path=Path.Combine(p,name);File.WriteAllBytes(path,new byte[]{1,2,(byte)i});object v=Activator.CreateInstance(reference);Set(v,"RelativePath",name);Set(v,"Sha256",Hash(path));refs.SetValue(v,i);}
        Set(r,"RawFiles",refs);return r;
    }
    static string Save(object r,string p){return (string)Call(r,"Save",p);}
    static object Analyze(string p,int algorithm)
    {
        object r=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewAnalysisRequest",true));Set(r,"ProjectPath",p);
        Set(r,"LocationFilePath",Path.Combine(p,"A_12.0kV_fixture.loc"));Set(r,"AlgorithmIndex",algorithm);Set(r,"LocationDataId",42L);
        Set(r,"OscillationFrequencyHz",50d);Set(r,"ManualThresholdEnabled",true);Set(r,"ManualThreshold",100d);Set(r,"MaxPulses",10);Set(r,"AutomaticAcceptance",false);
        return Call(engine,"Analyze",r);
    }
    static IEnumerable<string> Values(object json,string key)
    {
        var dict=json as Dictionary<string,object>;if(dict!=null){foreach(var pair in dict){if(pair.Key==key&&pair.Value is string)yield return (string)pair.Value;foreach(string v in Values(pair.Value,key))yield return v;}}
        else if(json is object[])foreach(object item in (object[])json)foreach(string v in Values(item,key))yield return v;
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);Application.EnableVisualStyles();
            asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));store=asm.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);
            scope=asm.GetType("HVX.PdReview.ReviewCalibrationInputScope",true);engine=asm.GetType("HVX.PdReview.ReviewAnalysisEngine",true);
            asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"prefs.json")));
            asm.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
            string p=Path.Combine(qa,"p");Project(p);object live=Live(p);string saved=Save(live,p);Save(Live(p,true,"B"),p);
            var originals=Directory.GetFiles(p).ToDictionary(x=>x,Hash);
            string scratch;
            using(var input=(IDisposable)Call(scope,"Create",Path.Combine(p,"ITEM.in"),Path.Combine(p,"A_12.0kV_fixture.loc"))) {
                scratch=(string)Get(input,"AlgorithmProjectPath");Check((bool)Get(input,"IsProjected")&&scratch!=p,"isolated projection is used for new archive");
                Check(Path.GetDirectoryName(scratch)==Path.Combine(Path.GetTempPath(),"HVXAnalysisCalibrationInputs"),"scratch is confined to controlled absolute parent");
                string[] lines=File.ReadAllLines(Path.Combine(scratch,"ALL_5nC.cal"));
                Check(lines.Length==13+4096,"frozen 13-line CAL protocol retains all samples");
                Check(lines[8]=="SR\t125000000"&&lines[6]=="Start_point (s)\t4E-06"&&lines[7]=="Final_point (s)\t6E-06","real Hz and seconds are not inferred from LOC points");
                Check(lines.Skip(13).Select(x=>Double.Parse(x,CultureInfo.InvariantCulture)).SequenceEqual((double[])Get(live,"Average")),"projection uses exact original V waveform without coefficient or resampling");
                Check(!File.Exists(Path.Combine(scratch,"B_5nC.cal")),"other-phase CAL is not substituted for A");
                Check(File.ReadAllBytes(Path.Combine(scratch,"ITEM.in")).SequenceEqual(File.ReadAllBytes(Path.Combine(p,"ITEM.in")))&&
                    File.ReadAllBytes(Path.Combine(scratch,"CABLE.in")).SequenceEqual(File.ReadAllBytes(Path.Combine(p,"CABLE.in"))),"project geometry bytes retained");
                Check(File.Exists(Path.Combine(scratch,"A_12.0kV_fixture.dem")),"actual matching DEM companion retained");
                string manifest=File.ReadAllText(Path.Combine(scratch,"calibration-input-source.json"));Check(manifest.Contains(Hash(saved))&&manifest.Contains("captureRangeIndex\":8"),"source hash and saved range are traceable");
            }
            Check(!Directory.Exists(scratch),"only owned scratch is cleaned after scope");
            foreach(int method in new[]{5,7}) {
                object result=Analyze(p,method);string status=(string)Field(result,"Status");File.WriteAllText(Path.Combine(qa,"method-"+method+".json"),status,new UTF8Encoding(false));
                Check((bool)Field(result,"Success"),"actual frozen analysis "+method+" completes: "+status);
                var json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=256}.DeserializeObject(status);
                string cal=Values(json,"resolved_calibration").FirstOrDefault(x=>x.Length>0);
                Check(cal!=null&&cal.Contains("HVXAnalysisCalibrationInputs")&&cal.EndsWith("ALL_5nC.cal"),"actual frozen function resolved projected CAL for method "+method);
                Check(status.Contains("HVX.CalibrationInputProjection/1")&&status.Contains(Hash(saved)),"actual result retains original CAL source for method "+method);
                var matrix=(double[,])Field(result,"Parameters");Check(matrix.GetLength(0)>0&&Enumerable.Range(0,matrix.GetLength(0)).All(i=>matrix[i,1]==42&&matrix[i,35]==0),"real pulses keep identity and are not falsely accepted for method "+method);
                Check(!Directory.Exists(Path.GetDirectoryName(cal)),"actual invocation disposes scoped projection");
            }
            using(var review=(Control)Activator.CreateInstance(asm.GetType("HVX.PdReview.PdReviewControl",true))) {
                Set(review,"ProjectPath",p);object map=Get(review,"PdMap");int before=(int)Get(map,"ProjectCalibrationReadCount");
                Set(live,"CoefficientPcPerV",25000d);Save(live,p);Call(review,"ReloadProjectCalibration");
                Check((int)Get(map,"ProjectCalibrationReadCount")==before+1&&(int)Get(review,"SourceRowCount")==0,"explicit same-project reload refreshes only catalog and does not fabricate LOC rows");
            }
            // Restore the explicit fixture edit so byte checks concern algorithm writes only.
            Set(live,"CoefficientPcPerV",50000d);Save(live,p);
            foreach(var original in originals)Check(original.Key==saved||Hash(original.Key)==original.Value,"analysis never writes source project: "+Path.GetFileName(original.Key));
            string incident=Path.Combine(qa,"incident");Project(incident);Save(Live(incident,false),incident);
            using(var input=(IDisposable)Call(scope,"Create",incident,Path.Combine(incident,"A_12.0kV_fixture.loc"))) {
                string path=(string)Get(input,"AlgorithmProjectPath");Check(!File.Exists(Path.Combine(path,"ALL_5nC.cal")),"incident-only CAL does not gain an invented reflection");
                Check(File.ReadAllText(Path.Combine(path,"calibration-input-source.json")).Contains("no_measured_reflection"),"unavailable reference reason retained");
            }
            Console.WriteLine("ANALYSIS_CALIBRATION_INPUT_SCOPE_PASS="+checks+"; ACTUAL_FROZEN_A_B=true; HARDWARE_CALLS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
