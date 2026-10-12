using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class LegacyCalibrationBindingSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Type api;static int checks;
    static object Get(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(string n,params object[] args){return api.GetMethod(n,B).Invoke(null,args);}
    static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(TargetInvocationException e){rejected=e.InnerException is IOException||e.InnerException is InvalidDataException||e.InnerException is InvalidOperationException||e.InnerException is ArgumentException;}Check(rejected,why);}
    internal static string Geometry(bool unequal)
    {return String.Join("",new[]{"A","B","C"}.Select((p,i)=>"0\t"+p+"\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t"+(unequal&&i>0?"406.3":"405.3")+"m\r\n"));}
    internal static string Cal(double coefficient=1845.1965311409,int samples=96,string charge="5000")
    {
        var text=new StringBuilder("Date\t2026/08/26\r\nTime\t19:48\r\nOld path\t旧项目\\原文件\r\nCable length\t405.0\r\nV (m/usec)\t169.60\r\nCal.value\t"+charge+"\r\nWideband\t0\r\nStart_point\t0.000000936\r\nFinal_point\t0.000005712\r\nSR(N)\t125000004\r\nNI_Calib_factor\t"+coefficient.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"\r\nFilterstate\t12\r\nCalib_Attenuation\t1092.335777\r\n");
        for(int i=0;i<samples;i++)text.AppendLine((Math.Sin(i*.31)*.123).ToString("R",System.Globalization.CultureInfo.InvariantCulture));return text.ToString();
    }
    static object Confirm(string p,object candidate,string phase="A",bool accepted=true)
    {return Call("Confirm",p,candidate,phase,"PICO-QA","ART-QA",true,accepted,"操作者已确认系数及历史参考波速适用于当前设备、接线及回路。旧设备身份未知；历史长度保持405.0，当前长度405.3。");}
    static object Find(string p,string phase="A",bool first=true,string pico="PICO-QA",string art="ART-QA")
    {return Call("FindForProject",p,phase,first,pico,art);}
    static object Load(string p,string path){return Call("LoadAndValidate",p,path,"PICO-QA","ART-QA",true);}
    static void WriteCal(string p,string text){File.WriteAllText(Path.Combine(p,"ALL_5nC.cal"),text,Encoding.GetEncoding(936));}
    [STAThread] static int Main(string[] args)
    {
        try {
            string build=Path.GetFullPath(args[0]),p=Path.Combine(Path.GetFullPath(args[1]),"legacy-binding");Directory.CreateDirectory(p);
            File.WriteAllText(Path.Combine(p,"ITEM.in"),"start\tQA\r\n");File.WriteAllText(Path.Combine(p,"CABLE.in"),Geometry(false));WriteCal(p,Cal());
            var asm=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));api=asm.GetType("HVX.CalibrationStorage.LegacyCalibrationBinding",true);
            string source=Path.Combine(p,"ALL_5nC.cal");byte[] original=File.ReadAllBytes(source);string originalGeometry=File.ReadAllText(Path.Combine(p,"CABLE.in"));
            object c=Call("ReadCandidate",p,source);
            Check((double)Get(c,"CoefficientPcPerV")==1845.1965311409,"coefficient precision preserved");
            Check((double)Get(c,"HistoricalLengthM")==405&& (double)Get(c,"HistoricalVelocityMPerUs")==169.60,"historical length and reference velocity preserved");
            Check((string)Get(c,"SourcePhase")=="ALL"&&(string)Get(c,"Level")=="5nC"&&(double)Get(c,"ChargePc")==5000,"filename and header agree on phase/charge");
            Check((int)Get(c,"WaveformSampleCount")==96&&(double)Get(c,"SampleRateHz")==125000000,"known historical rate error is corrected without changing sample count");
            Check((double)Get(c,"RecordedSampleRateHz")==125000004,"recorded rate retained independently from effective time base");
            Check((string)Get(c,"HistoricalFilterText")=="12"&&(string)Get(c,"HistoricalAttenuationText")=="1092.335777","unverified old filter and attenuation retained verbatim");
            Check(Find(p)==null,"candidate alone never enables calibration");
            Reject(()=>Confirm(p,c,accepted:false),"explicit operator confirmation required");Check(!Directory.Exists(Path.Combine(p,"Calibration")),"cancelled confirmation writes nothing");
            Reject(()=>Call("Confirm",p,c,"A","","ART-QA",true,true,"yes"),"missing actual serial rejected");
            Reject(()=>Call("Confirm",p,c,"A","PICO-QA","ART-QA",true,true,""),"empty acknowledgement rejected");
            Set(c,"CoefficientPcPerV",1d);Reject(()=>Confirm(p,c),"mutable candidate cannot replace coefficient");c=Call("ReadCandidate",p,source);
            File.SetAttributes(source,File.GetAttributes(source)|FileAttributes.ReadOnly);
            object bound=Confirm(p,c);string path=(string)Get(bound,"BindingPath");
            Check(File.Exists(path)&&path.EndsWith(".hvlegacy")&&!Directory.GetFiles(p,"*.hvcal",SearchOption.AllDirectories).Any(),"confirmation uses separate sidecar and never fabricates a live acquisition");
            Check((string)Get(bound,"HistoricalDeviceIdentity")=="Unknown"&&(string)Get(bound,"QualityStatus")=="NotReassessed","historical identity and quality not fabricated");
            Check((bool)Get(bound,"OperatorConfirmed")&&(string)Get(bound,"PicoSerial")=="PICO-QA"&&(string)Get(bound,"ArtSerial")=="ART-QA","current applicability is tied to explicit current device pair");
            Check((double)Get(bound,"CurrentLengthM")==405.3&&(double)Get(bound,"HistoricalLengthM")==405,"current and old lengths remain distinct");
            Check((double)Get(bound,"SourceSampleRateHz")==125000004&&
                (double)bound.GetType().GetProperty("EffectiveSourceSampleRateHz").GetValue(bound,null)==125000000,
                "sidecar retains raw historical rate and exposes corrected effective rate");
            Check(original.SequenceEqual(File.ReadAllBytes(source))&&originalGeometry==File.ReadAllText(Path.Combine(p,"CABLE.in")),"original CAL and CABLE remain byte-identical");
            Check((File.GetAttributes(source)&FileAttributes.ReadOnly)!=0,"original attributes preserved");
            Check((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,"saved decision is marked read-only");
            Check(Load(p,path)!=null&&Find(p)!=null,"saved confirmation validates and is discoverable");
            Check(Find(p,"B")!=null&&Find(p,"ALL")!=null,"equal-length project shares confirmed applicable coefficient");
            Check(Find(p,first:false)==null&&Find(p,pico:"OTHER")==null&&Find(p,art:"OTHER")==null,"timing and either changed serial invalidate applicability");
            Reject(()=>Call("LoadAndValidate",p,path,"PICO-QA","ART-QA",false),"direct consumer also verifies timing");
            object b2=Confirm(p,c);Check((string)Get(b2,"BindingPath")!=path&&File.Exists(path),"later decision appends and preserves earlier decision");
            Check((string)Get(Find(p),"BindingPath")== (string)Get(b2,"BindingPath"),"latest same-scope decision selected");
            byte[] broken=File.ReadAllBytes(path);broken[broken.Length-8]^=1;string corrupt=Path.Combine(p,"corrupt.hvlegacy");File.WriteAllBytes(corrupt,broken);Reject(()=>Load(p,corrupt),"altered confirmation payload rejected");
            File.WriteAllText(Path.Combine(p,"ITEM.in"),"start\tchanged\r\n");Check(Find(p)==null,"project metadata change invalidates old decision");Reject(()=>Confirm(p,c),"stale displayed project candidate cannot be confirmed");File.WriteAllText(Path.Combine(p,"ITEM.in"),"start\tQA\r\n");
            File.SetAttributes(source,FileAttributes.Normal);WriteCal(p,Cal().Replace("0.123","0.124"));
            // Ensure a harmless exact source change, even if the waveform contains no particular decimal text.
            File.AppendAllText(source,"\r\n",Encoding.GetEncoding(936));Check(Find(p)==null,"any original source change invalidates decision hash");Reject(()=>Confirm(p,c),"source changed after display cannot be confirmed");File.WriteAllBytes(source,original);
            File.WriteAllText(Path.Combine(p,"CABLE.in"),Geometry(true));c=Call("ReadCandidate",p,source);Reject(()=>Confirm(p,c,"ALL"),"unequal phase lengths cannot receive ALL decision");
            object separate=Confirm(p,c,"A");Check(Find(p,"A")!=null&&Find(p,"B")==null&&Find(p,"ALL")==null,"unequal phase project requires phase-specific applicability");
            Reject(()=>Call("ReadCandidate",p,Path.Combine(p,"..","outside.cal")),"outside-project paths rejected before reading");
            WriteCal(p,Cal(double.NaN));Reject(()=>Call("ReadCandidate",p,source),"nonfinite coefficient rejected");WriteCal(p,Cal(-1));Reject(()=>Call("ReadCandidate",p,source),"negative coefficient rejected");
            WriteCal(p,Cal(samples:5));Reject(()=>Call("ReadCandidate",p,source),"incomplete waveform rejected");WriteCal(p,Cal(charge:"1000"));Reject(()=>Call("ReadCandidate",p,source),"header/filename charge conflict rejected");
            WriteCal(p,Cal()+"NaN\r\n");Reject(()=>Call("ReadCandidate",p,source),"invalid waveform value rejected");
            WriteCal(p,Cal().Replace("Old path\t旧项目\\原文件","NI_Calib_factor\t1"));Reject(()=>Call("ReadCandidate",p,source),"duplicate coefficient rejected");
            WriteCal(p,Cal());Check(Call("ReadCandidate",p,source)!=null,"valid GB936 source still readable after negative cases");
            Console.WriteLine("LEGACY_CALIBRATION_BINDING_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
