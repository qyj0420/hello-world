using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class AnalysisZeroVoltageOptInSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static Assembly asm;static Type reader,engine;static string qa;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void SetField(object o,string n,object value){o.GetType().GetField(n,F).SetValue(o,value);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object value){o.GetType().GetProperty(n,F).SetValue(o,value,null);}
    static object Call(object o,string n,params object[] args){var t=o as Type??o.GetType();return t.GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o is Type?null:o,args);}
    static double[] Wave(int peak,int reflected,double amplitude,int count=4096){return Enumerable.Range(0,count).Select(i=>{
        double x=(i-peak)/10d,y=(i-reflected)/10d;return amplitude*(Math.Exp(-x*x/2)-.6*Math.Exp(-(x-3)*(x-3)/2))+
            amplitude*.6*(Math.Exp(-y*y/2)-.6*Math.Exp(-(y-3)*(y-3)/2))+.00001*amplitude*Math.Sin(i*.73);}).ToArray();}
    static void Write(string path,string[] headers,double[] values){File.WriteAllText(path,String.Join("\r\n",headers)+"\r\n"+String.Join("\r\n",values.Select(v=>(v*100000).ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));}
    static Dictionary<string,object> Metadata(int count=4096,double rate=125e6){return new Dictionary<string,object>{{"schema","HVX.ZeroVoltageAnalysis/1"},{"analysisOptIn",true},{"targetVoltageKv",0},{"phaseKnown",false},{"sampleRateHz",rate},{"sampleCount",count},{"captureStartSeconds",0},{"locationAcquisitionId","offline-zero"},{"noiseReferenceSourceId","offline-noise"},{"simulation",true}};}
    static string[] Header(Dictionary<string,object> metadata){return new[]{"HVX LOC zero-voltage record / engineering pC x 100000","Received UTC\t2026-10-07T00:00:00Z","Phase\tNaN","Voltage (kV)\t0","Cable-length\t170","Velocity (m/us)\t170","Delta x [s]\t"+(1/Convert.ToDouble(metadata["sampleRateHz"])).ToString("R",CultureInfo.InvariantCulture),"Charge unit\tpC x 100000","Capture start seconds\t0","Acquisition context\t{}","HVX_ZeroVoltageReference\t"+new JavaScriptSerializer().Serialize(metadata)};}
    static string Fixture(){string p=Path.Combine(qa,"p");Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",Encoding.UTF8);
        File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",Encoding.UTF8);
        Write(Path.Combine(p,"ALL_1nC.cal"),new[]{"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1","Start_point (s)\t0.000004","Final_point (s)\t0.000006","SR\t125000000","Calib_Attenuation\t4.436974992","Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"},Wave(500,750,.01).Select(v=>v/100000).ToArray());
        Write(Path.Combine(p,"A_0kV_1.loc"),Header(Metadata()),Wave(500,700,1000));
        File.WriteAllText(Path.Combine(p,"A_0kV_1.dem"),"Hz\t50\r\nPhase\t40\r\n");
        var legacy=Header(Metadata());legacy[10]="Data";
        Write(Path.Combine(p,"A_0kV_2.loc"),legacy,Wave(500,700,1000));
        var nonzero=(string[])legacy.Clone();nonzero[2]="Phase\t0";nonzero[3]="Voltage (kV)\t12";
        Write(Path.Combine(p,"A_12kV_3.loc"),nonzero,Wave(500,700,1000));return p;}
    static object Request(string root,string path,int algorithm=5,bool opt=true){var r=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewAnalysisRequest",true));SetField(r,"ProjectPath",root);SetField(r,"LocationFilePath",path);SetField(r,"LocationDataId",1L);SetField(r,"AlgorithmIndex",algorithm);SetField(r,"AnalyzeZeroVoltageData",opt);return r;}
    static void AssertZero(object result,string label){Check((bool)Field(result,"Success"),label+": "+Field(result,"Status"));var p=(double[,])Field(result,"Parameters");Check(p.GetLength(0)>0,label+" has real detected synthetic pulses");for(int i=0;i<p.GetLength(0);i++){Check(p[i,2]==0,label+" actual zero voltage");Check(Double.IsNaN(p[i,3]),label+" no fabricated voltage phase");}}
    static void Invalid(string root,string path,Dictionary<string,object> metadata,string[] changed=null){Write(path,changed??Header(metadata),Wave(500,700,1000));bool rejected=false;try{Call(reader,"Read",path,root);}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidDataException||ex.InnerException is ArgumentException;}Check(rejected,"malformed metadata rejected");}
    [STAThread]static int Main(string[] args){try{
        qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));reader=asm.GetType("HVX.PdReview.ReviewLocationFileReader",true);engine=asm.GetType("HVX.PdReview.ReviewAnalysisEngine",true);
        asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
        asm.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
        string root=Fixture(),zero=Path.Combine(root,"A_0kV_1.loc"),legacy=Path.Combine(root,"A_0kV_2.loc");var original=Directory.GetFiles(root).ToDictionary(p=>p,File.ReadAllBytes);
        Check((bool)Call(reader,"IsZeroVoltageAnalysisRecord",zero),"new explicit opt-in identified");Check(!(bool)Call(reader,"IsZeroVoltageAnalysisRecord",legacy),"legacy not automatically migrated");
        Check(!(bool)Field(Call(engine,"Analyze",Request(root,zero,5,false)),"Success"),"engine default off");Check(!(bool)Field(Call(engine,"Analyze",Request(root,legacy)),"Success"),"engine rejects legacy even opt-in enabled");
        object first=null;
        foreach(int algorithm in new[]{5,7}){var request=Request(root,zero,algorithm);SetField(request,"PhaseQuadrants",new[]{false,false,false,false});SetField(request,"OscillationFrequencyHz",50d);var result=Call(engine,"Analyze",request);AssertZero(result,"real algorithm "+algorithm);if(first==null)first=result;}
        Application.EnableVisualStyles();using(var host=new Form{ClientSize=new Size(1280,800),ShowInTaskbar=false,Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual})using(var view=(Control)Activator.CreateInstance(asm.GetType("HVX.PdReview.PdReviewControl",true))){
            host.Controls.Add(view);view.Dock=DockStyle.Fill;host.Show();Set(view,"PromptSavedAnalysisOnProjectLoad",false);Set(view,"ProjectPath",root);object tree=Field(view,"analysisTree");
            Check(!(bool)Get(view,"AnalyzeZeroVoltageData"),"review default off");Call(tree,"SetDataSelected",zero,true);Check(((string[])Call(tree,"GetSelectedDataPaths")).Length==0,"default tree excludes zero");
            Set(view,"AnalyzeZeroVoltageData",true);Call(tree,"SetDataSelected",zero,true);Call(tree,"SetDataSelected",legacy,true);Check(((string[])Call(tree,"GetSelectedDataPaths")).SequenceEqual(new[]{zero}),"tree selects only explicit new zero");
            var actual=((Task<double[,]>)Call(view,"AnalyzeSavedPressureFilesAsync",(object)new[]{zero})).GetAwaiter().GetResult();Check(actual.GetLength(0)>0,"pressure cumulative consumer accepts true zero");for(int r=0;r<actual.GetLength(0);r++)Check(actual[r,2]==0&&Double.IsNaN(actual[r,3]),"batch keeps zero and unknown phase");
            Call(view,"StartSelectedAnalysis",true);DateTime end=DateTime.UtcNow.AddSeconds(40);while((bool)Get(view,"IsAnalysisRunning")&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(10);}Check(!(bool)Get(view,"IsAnalysisRunning"),"interactive zero batch completes");Check((int)Get(view,"SourceRowCount")>0,"interactive route retains zero results");
            object[] export={null,null,null,null};Check((bool)Call(view,"ExportAnalysisSession",export),"zero session exports");
            using(var reload=(Control)Activator.CreateInstance(view.GetType())){Set(reload,"PromptSavedAnalysisOnProjectLoad",false);Check((bool)Call(reload,"LoadAnalysisSession",root,export[0],export[1],export[2],export[3]),"default-off session reload succeeds");Check((int)Get(reload,"SourceRowCount")==0,"default-off does not silently restore zero into statistics");Set(reload,"AnalyzeZeroVoltageData",true);Check((bool)Call(reload,"LoadAnalysisSession",root,export[0],export[1],export[2],export[3]),"opt-in session reload: "+Get(reload,"LastError"));Check((int)Get(reload,"SourceRowCount")>0,"opt-in reload retains zero rows");var forged=(double[,])((double[,])export[0]).Clone();forged[0,3]=0;Check(!(bool)Call(reload,"LoadAnalysisSession",root,forged,export[1],export[2],export[3]),"saved zero session cannot fabricate known phase");Set(reload,"AnalyzeZeroVoltageData",false);Check((int)Get(reload,"SourceRowCount")==0,"switch off excludes zero derived results");}
        }
        string bad=Path.Combine(root,"A_0kV_9.loc");foreach(string key in new[]{"analysisOptIn","phaseKnown","targetVoltageKv","sampleCount","schema","noiseReferenceSourceId"}){var m=Metadata();m[key]=key=="analysisOptIn"?(object)false:key=="phaseKnown"?(object)true:key=="targetVoltageKv"?(object)5:key=="sampleCount"?(object)4097:key=="schema"?"unknown":"";Invalid(root,bad,m);}
        var filter=Metadata();filter["pdAcquisitionFilterId"]="PD-HP10K-SYMMETRIC-FIR4-v1";filter["pdAcquisitionFilterScope"]="full-segment-before-zero-voltage-analysis";Write(bad,Header(filter),Wave(500,700,1000));var data=Call(reader,"Read",bad,root);var raw=Field(data,"ChargeSamples");Check(Object.ReferenceEquals(raw,Call(asm.GetType("HVX.PdReview.ReviewCapacitorAnalysis",true),"AcquisitionHighPassedSamples",data)),"filtered zero record does not receive the shared high-pass twice");filter["pdAcquisitionFilterScope"]="full-segment-before-cycle-crop";Invalid(root,bad,filter);
        foreach(int line in new[]{2,3,7,8}){var header=Header(Metadata());header[line]=line==2?"Phase\t0":line==3?"Voltage (kV)\t5":line==7?"Charge unit\tV":"Capture start seconds\tunknown";Invalid(root,bad,Metadata(),header);}
        var duplicate=Header(Metadata());duplicate[9]=duplicate[10];Invalid(root,bad,Metadata(),duplicate);
        var mismatch=Metadata();mismatch["sampleRateHz"]=250e6;var mismatchHeader=Header(mismatch);mismatchHeader[6]="Delta x [s]\t8e-9";Invalid(root,bad,mismatch,mismatchHeader);
        string cap=Path.Combine(qa,"cap");Directory.CreateDirectory(cap);File.WriteAllText(Path.Combine(cap,"ITEM.in"),"负载类型 = 电容局放\r\n",new UTF8Encoding(true));File.WriteAllText(Path.Combine(cap,"CABLE.in"),"0\tALL\r\n");string capPath=Path.Combine(cap,"A_0kV_1.loc");Write(capPath,Header(Metadata(20000,1e7)),Wave(5000,6000,1000,20000));File.WriteAllText(Path.ChangeExtension(capPath,".dem"),"Hz\t50\r\nPhase\t40\r\n");var capRequest=Request(cap,capPath);SetField(capRequest,"ManualThresholdEnabled",true);SetField(capRequest,"ManualThreshold",20d);SetField(capRequest,"OscillationFrequencyHz",50d);AssertZero(Call(engine,"Analyze",capRequest),"capacitor ignores invented cycle origin");
        foreach(var entry in original)Check(File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value),"source unchanged: "+Path.GetFileName(entry.Key));
        Console.WriteLine("ANALYSIS_ZERO_VOLTAGE_OPT_IN_PASS="+checks+"; HardwareAccessed=false; actual_A_B_engine=true");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
