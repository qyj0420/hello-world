using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using HVX.CalibrationStorage;
using Xiongxin.OWTS.Desktop;

// Synthetic host seams only. No desktop product, worker or SDK is linked.
namespace Xiongxin.OWTS.Desktop
{
    internal static class AppTheme {internal static Icon LoadIcon(){return null;}}
    internal sealed class Diagnostics {
        internal static readonly List<string> Failures=new List<string>();
        internal void Error(string area,string message,Exception error){Failures.Add(message+" "+error.Message);}
    }
    internal sealed class CompatibilityState {internal string ProjectPath;internal object Hardware;}
    internal sealed class CompatibilityTiming {internal bool UseFirstArrivalTiming;}
    internal sealed class CompatibilitySettings {internal CompatibilityTiming TaskMinus3=new CompatibilityTiming();}
    internal static class DeviceSetupStatus {internal static string ActualIdentity(object hardware){return hardware as string??"";}}
    internal static class ModuleHost {internal const string CalibrationTest="CalibrationTest";}
    internal sealed partial class MainForm:Form {
        readonly CompatibilityState state=new CompatibilityState();
        readonly CompatibilitySettings settings=new CompatibilitySettings();
        readonly bool verificationMode=true;
        internal bool IsChinese=true,pressureStandalone;
        internal Action CalibrationRequiredNavigation {get;set;}
        internal string CurrentPhase="ALL";
        internal int NewCalibrationNavigationCount;
        internal readonly List<string> Requirements=new List<string>();
        string Localize(string value){return value;}
        void OpenModule(string id){NewCalibrationNavigationCount++;}
        void ShowRequirement(string message){Requirements.Add(message);}
        internal void ChangeContext(string project=null,string identity=null,bool? timing=null,string phase=null) {
            if(project!=null)state.ProjectPath=project;if(identity!=null)state.Hardware=identity;
            if(timing.HasValue)settings.TaskMinus3.UseFirstArrivalTiming=timing.Value;if(phase!=null)CurrentPhase=phase;
        }
        internal bool Select(string project,bool timing,string identity,out bool handled) {
            state.ProjectPath=project;state.Hardware=identity;settings.TaskMinus3.UseFirstArrivalTiming=timing;
            CurrentPhase="ALL";return TryLegacyCalibrationBeforeTest("ALL",out handled,()=>CurrentPhase=="ALL");
        }
    }
}

internal static class CalibrationCustomerCompatibilitySmoke
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static string Project(string root,string name) {
        string p=Path.Combine(root,name);Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,"ITEM.in"),"负载类型: = \"一体三芯电缆\"\r\n");
        File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n");
        return p;
    }
    static LiveCalibrationRecord Record(string p,bool firstArrival,string device="CURRENT-PICO",int version=3) {
        var wave=new double[512];wave[20]=1;wave[220]=.5;
        return new LiveCalibrationRecord {Version=version,Phase="ALL",Level="5nC",DeviceSerial=device,DeviceModel="fixture",
            FilterId=HVX.SignalProcessing.PdLinearHighPass.FilterId,ProjectHash=LiveCalibrationRecord.Identity(p),
            CreatedUtc=DateTime.UtcNow.AddSeconds(-30).ToString("o",CultureInfo.InvariantCulture),
            LengthM=170,VelocityMPerUs=170,SampleRateHz=100e6,IncidentSeconds=20/100e6,ReflectionSeconds=220/100e6,
            CoefficientPcPerV=5000,ChargePc=5000,SnrDb=40,Correlation=.99,RangeIndex=7,PulseCount=3,
            IncidentPeakIndex=20,ReflectionPeakIndex=220,FirstArrival=firstArrival,Average=wave,
            CoefficientProcessing="HP10K_NO_LP",ReflectionStatus="Detected",VelocitySource="Measured",
            RawFiles=new[]{new CalibrationRawReference{RelativePath="source.raw",Sha256=Convert.ToBase64String(new byte[32])}}};
    }
    static string LegacyText(string p) {
        var text=new StringBuilder("Date\t2026/10/11\r\nTime\t08:00\r\nOld path\tfixture\r\nCable length\t170\r\nV (m/usec)\t170\r\nCal.value\t5000\r\nWideband\t0\r\nStart_point\t0.0000002\r\nFinal_point\t0.0000022\r\nSR(N)\t100000000\r\nNI_Calib_factor\t5000\r\nFilterstate\t12\r\nCalib_Attenuation\t0\r\n");
        for(int i=0;i<512;i++)text.AppendLine((i==20?1d:i==220?.5d:0d).ToString("R",CultureInfo.InvariantCulture));
        string path=Path.Combine(p,"ALL_5nC.cal");File.WriteAllText(path,text.ToString(),Encoding.GetEncoding(936));return path;
    }
    static void HistoricalEnvelope(string p,LiveCalibrationRecord r,int version) {
        r.Version=version;
        if(version==1) {
            r.RawFiles=Enumerable.Range(0,3).Select(i=> {
                string relative="source-"+i+".raw";File.WriteAllBytes(Path.Combine(p,relative),new byte[]{1,2,(byte)i});
                return new CalibrationRawReference{RelativePath=relative,Sha256=LiveCalibrationRecord.HashFile(Path.Combine(p,relative))};
            }).ToArray();
        }
        byte[] xml;using(var buffer=new MemoryStream()){new XmlSerializer(typeof(LiveCalibrationRecord)).Serialize(buffer,r);xml=buffer.ToArray();}
        byte[] payload;
        if(version==1)payload=xml;
        else using(var buffer=new MemoryStream())using(var writer=new BinaryWriter(buffer)) {
            writer.Write(xml.Length);writer.Write(xml);writer.Write(r.Average.Length);foreach(double v in r.Average)writer.Write(v);
            writer.Write(r.PulseCount);for(int j=0;j<r.PulseCount;j++){writer.Write(r.Average.Length);foreach(double v in r.Average)writer.Write(v);}
            writer.Flush();payload=buffer.ToArray();
        }
        using(var output=new BinaryWriter(File.Create(Path.Combine(p,"ALL_5nC.cal")))) {
            output.Write(Encoding.ASCII.GetBytes("HVCLIVE"+version));output.Write(payload.Length);
            using(var sha=SHA256.Create())output.Write(sha.ComputeHash(payload));output.Write(payload);
        }
    }
    static int Bindings(string p){return Directory.GetFiles(p,"*.hvlegacy",SearchOption.AllDirectories).Length;}
    static void Wait(Task task) {
        DateTime until=DateTime.UtcNow.AddSeconds(15);while(!task.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException();Application.DoEvents();Thread.Sleep(1);}
        task.GetAwaiter().GetResult();
    }
    static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
    [STAThread]static int Main(string[] args) {
        try {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            string p=Project(root,"selection");string folder=Path.Combine(p,"Calibration","Live");Directory.CreateDirectory(folder);
            string oldRoot=Record(p,false).Save(p),old=Path.Combine(folder,"ALL_5nC.hvcal");File.Move(oldRoot,old);
            string oldHash=LiveCalibrationRecord.HashFile(old);
            string incompatible=Record(p,true,"OTHER-PICO").Save(p),incompatibleHash=LiveCalibrationRecord.HashFile(incompatible);
            var value=PressureCalibrationSelection.Find(p,"ALL",false,"CURRENT-PICO","CURRENT-ART");
            Check(value!=null&&!value.IsLegacy&&value.Source==old,"same-name incompatible root record cannot shadow validated historical formal archive");
            Check(Bindings(p)==0,"automatic formal compatibility creates no operator confirmation");
            Check(PressureCalibrationSelection.Find(p,"ALL",true,"CURRENT-PICO","CURRENT-ART")==null,"timing mismatch is never changed to fit");
            Check(PressureCalibrationSelection.Find(p,"ALL",false,"WRONG-PICO","CURRENT-ART")==null,"device mismatch is never silently rebound");
            Check(PressureCalibrationSelection.Find(p,"ALL",false,"CURRENT-PICO","")==null,"missing current ART identity cannot enter automatic selection");
            Check(LiveCalibrationRecord.HashFile(old)==oldHash&&LiveCalibrationRecord.HashFile(incompatible)==incompatibleHash,"automatic fallback preserves both original archives");
            var current=Record(p,false);current.CreatedUtc=DateTime.UtcNow.AddSeconds(-1).ToString("o",CultureInfo.InvariantCulture);string currentPath=current.Save(p);
            value=PressureCalibrationSelection.Find(p,"ALL",false,"CURRENT-PICO","CURRENT-ART");
            Check(value!=null&&!value.IsLegacy&&value.Source==currentPath,"current compatible new record wins over older equal-quality formal record");
            using(var host=new MainForm()) {
                bool handled;Check(host.Select(p,false,"HUB|CURRENT-PICO|CURRENT-ART",out handled)&&handled,"host automatically selects compatible current formal record without a reuse window");
            }
            foreach(int version in new[]{1,2}) {
                string compatible=Project(root,"formal-v"+version);HistoricalEnvelope(compatible,Record(compatible,false,"CURRENT-PICO",version),version);
                string path=Path.Combine(compatible,"ALL_5nC.cal"),hash=LiveCalibrationRecord.HashFile(path);
                value=PressureCalibrationSelection.Find(compatible,"ALL",false,"CURRENT-PICO","CURRENT-ART");
                Check(value!=null&&!value.IsLegacy&&value.CoefficientPcPerV==5000,"formal v"+version+" is decoded into the shared contract without a customer format choice");
                Check(LiveCalibrationRecord.HashFile(path)==hash&&Bindings(compatible)==0,"formal v"+version+" compatibility preserves original and creates no confirmation");
                if(version==1) {
                    File.AppendAllText(Path.Combine(compatible,"source-0.raw"),"changed");
                    Check(PressureCalibrationSelection.Find(compatible,"ALL",false,"CURRENT-PICO","CURRENT-ART")==null,"raw-reference v1 archive remains unusable after its required raw evidence changes");
                }
            }
            string unknown=Project(root,"unknown-text"),textPath=LegacyText(unknown),textHash=LiveCalibrationRecord.HashFile(textPath);
            byte[] textOriginal=File.ReadAllBytes(textPath);
            using(var host=new MainForm()) {
                bool handled;Check(!host.Select(unknown,false,"HUB|CURRENT-PICO|CURRENT-ART",out handled)&&handled,"unknown historical identity requires an explicit short decision instead of automatic customer confirmation");
            }
            Check(Bindings(unknown)==0&&LiveCalibrationRecord.HashFile(textPath)==textHash,"unknown text is neither overwritten nor automatically signed");
            var candidate=LegacyCalibrationBinding.ReadCandidate(unknown,textPath);
            var bound=LegacyCalibrationBinding.Confirm(unknown,candidate,"ALL","CURRENT-PICO","CURRENT-ART",false,true,"Explicit pre-existing operator applicability decision for this circuit.");
            value=PressureCalibrationSelection.Find(unknown,"ALL",false,"CURRENT-PICO","CURRENT-ART");
            Check(value!=null&&value.IsLegacy&&value.Source==bound.BindingPath,"existing explicit applicability decision remains usable after full revalidation");
            Check(LegacyCalibrationBinding.LoadAndValidate(unknown,bound.BindingPath,"CURRENT-PICO","CURRENT-ART",false).HistoricalDeviceIdentity=="Unknown","existing decision does not invent verified historical identity");
            Check(PressureCalibrationSelection.Find(unknown,"ALL",true,"CURRENT-PICO","CURRENT-ART")==null,"existing decision never follows a changed timing mode automatically");
            File.AppendAllText(textPath,"\r\n1");
            Check(PressureCalibrationSelection.Find(unknown,"ALL",false,"CURRENT-PICO","CURRENT-ART")==null,"existing decision never follows a changed original CAL automatically");
            File.WriteAllBytes(textPath,textOriginal);
            Check(LiveCalibrationRecord.HashFile(textPath)==textHash,"synthetic source restored byte-for-byte before dialog regression");
            foreach(bool zh in new[]{true,false})using(var dialog=new LegacyCalibrationImportDialog(unknown,"ALL","CURRENT-PICO","CURRENT-ART",false,zh,()=>{})) {
                Wait((Task)dialog.GetType().GetMethod("LoadCandidatesAsync",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(dialog,null));
                string details=Field<TextBox>(dialog,"details").Text;
                Check(!details.Contains("CURRENT-PICO")&&!details.Contains("CURRENT-ART")&&!details.Contains("Pico:")&&!details.Contains("ART:")&&!details.Contains("HVCLIVE")&&!details.Contains("计时方式")&&!details.Contains("timing"),"customer-compatible dialog has no serial/format/timing internals: "+zh);
                Check(!Field<CheckBox>(dialog,"confirmation").Checked&&!Field<Button>(dialog,"apply").Enabled&&dialog.AcceptedBinding==null,"maintenance reuse remains an explicit unchecked decision: "+zh);
            }
            string hostSource=File.ReadAllText(Path.Combine(args[1],"src","MainForm.LegacyCalibration.cs"));
            Check(!hostSource.Contains("new LegacyCalibrationImportDialog")&&hostSource.Contains("CreateLegacyCalibrationReuseDialog"),"customer navigation uses a short action dialog, never the detailed legacy reuse window");
            string nav=File.ReadAllText(Path.Combine(args[1],"src","MainForm.CalibrationNavigation.cs"));
            Check(nav.Contains("reason=IsChinese?Localize(\"请完成校准并保存后再试。\")"),"real-test navigation uses a short required-action message instead of internal reasons");
            Console.WriteLine("CALIBRATION_CUSTOMER_COMPATIBILITY_PASS="+checks+"; PRODUCT_STARTED=0; WORKER_STARTED=0; SDK_CALLS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
