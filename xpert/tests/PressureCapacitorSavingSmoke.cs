using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Collections.Generic;

// Real managed pressure save and trial contracts, synthetic owned captures only.
internal static class PressureCapacitorSavingSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;static Assembly module;static Type controlType,settingsType,captureType;
    static void Check(bool b,string why){checks++;if(!b)throw new Exception(why);}
    static object Get(object o,string p){return o.GetType().GetProperty(p,F).GetValue(o,null);}
    static void Set(object o,string p,object v){o.GetType().GetProperty(p,F).SetValue(o,v,null);}
    static object Field(object o,string p){return o.GetType().GetField(p,F).GetValue(o);}
    static void Field(object o,string p,object v){o.GetType().GetField(p,F).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Pump(){Application.DoEvents();Thread.Sleep(1);}
    static T Wait<T>(Task<T> task){var w=Stopwatch.StartNew();while(!task.IsCompleted&&w.ElapsedMilliseconds<10000)Pump();Check(task.IsCompleted,"owned offline operation completes within deadline");return task.GetAwaiter().GetResult();}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
    static object Settings(){var s=Activator.CreateInstance(settingsType);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART");Field(s,"PicoSerial","OFFLINE-PICO");Field(s,"CaptureDurationMs",1d);Field(s,"PdCalibrationPcPerV",1000d);Field(s,"CalibrationPulsePc",100d);Field(s,"RangeIndex",5);return s;}
    static string Xml(object settings){using(var sw=new StringWriter()){new XmlSerializer(settingsType).Serialize(sw,settings);return sw.ToString();}}
    static string Capture(string project,string xml)
    {
        object settings;using(var sr=new StringReader(xml))settings=new XmlSerializer(settingsType).Deserialize(sr);
        var c=Activator.CreateInstance(captureType);Field(c,"Settings",settings);
        Field(c,"VoltageRaw",new double[(int)Get(settings,"ExpectedArtCount")]);
        var adc=new short[(int)Get(settings,"RequestedPicoCount")];for(int i=adc.Length/3;i<adc.Length/3+8;i++)adc[i]=1000;
        Field(c,"PdAdc",adc);Field(c,"PicoRateHz",(double)Field(settings,"RequestedPicoRateHz"));
        Field(c,"PicoFullScaleV",new[]{.01,.02,.05,.1,.2,.5,1d,2,5,10,20}[(int)Field(settings,"RangeIndex")]);
        var config=module.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration",true);
        Field(c,"HardwareBits",config.GetMethod("HardwareBits",F).Invoke(null,new[]{Field(settings,"ResolutionBits"),Field(settings,"RangeIndex")}));
        Field(c,"ArtSerial",Field(settings,"ArtSerial"));Field(c,"PicoSerial",Field(settings,"PicoSerial"));Field(c,"Runtime","OFFLINE SYNTHETIC — NOT DEVICE DATA");Field(c,"CapturedUtc",DateTime.UtcNow);
        string directory=Path.Combine(project,"Acquisition","Pressure","r"+Guid.NewGuid().ToString("N").Substring(0,6));
        try{Call(c,"Save",directory);}catch(TargetInvocationException error){Console.WriteLine("OFFLINE_SOURCE_ERROR="+error.InnerException);throw;}
        return directory;
    }
    static void Run(string root,string name,bool capacitor,Action<Control,string,string> test)
    {
        string project=Path.Combine(root,name);Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project,"ITEM.in"),"负载类型 = \""+(capacitor?"电容局放":"单芯电缆")+"\"\n所有相长度 = 0\n",new System.Text.UTF8Encoding(true));
        using(var form=new Form{ClientSize=new Size(1280,760),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
        using(var page=(Control)Activator.CreateInstance(controlType))
        using(var feedback=new System.Windows.Forms.Timer{Interval=20}){
            form.Controls.Add(page);page.Dock=DockStyle.Fill;Set(page,"ProjectPath",project);Set(page,"FrequencyReminderEnabled",false);
            Set(page,"MaximumPeakVoltageKv",20d);Set(page,"AutomaticRangeReminderEnabled",false);Set(page,"AnalyzeZeroVoltageData",true);Set(page,"AutomaticPostProcessEnabled",true);Set(page,"BackgroundPressureAnalysisEnabled",true);
            Call(page,"SetPressurePlan",new[]{0d,1d},new[]{1,1},new[]{0d,1d});Call(page,"SetPressureProgramMode","PLAN");
            Call(page,"SetSelectedPlanStep",0);
            Call(page,"SetDischargeCalibrationWithEvidence",1000d,"fixture.cal",DateTimeOffset.UtcNow.AddMinutes(-1),"A",false,100d,Convert.ToBase64String(new byte[32]));
            Call(page,"SetSelectedRange","PICO:5");Call(page,"SetAutomaticRange",false);form.Show();Pump();
            string raw=null;
            Call(page,"ConfigureZeroVoltageHardwareBackend",Xml(Settings()),new Func<string,CancellationToken,Task<string>>((xml,token)=>{raw=Capture(project,xml);return Task.FromResult(raw);}));
            Call(page,"ReportP04Status",true,true,DateTime.UtcNow);Set(page,"AdvancedMode",false);
            feedback.Tick+=delegate{if(!page.IsDisposed&&(bool)Get(page,"ActionHandshakeEnabled"))Call(page,"ReportP04Status",true,true,DateTime.UtcNow);};feedback.Start();
            Call(page,"RequestPressure");Check((bool)Get(page,"ActionPending"),"actual page publishes owned acquisition action");
            bool captured=Wait((Task<bool>)Call(page,"ExecuteZeroVoltageHardwareActionAsync",(long)Get(page,"ActionSequence")));
            if(!captured)Console.WriteLine("OFFLINE_CAPTURE_ERROR="+Get(page,"LastActionError"));
            Check(captured,"synthetic capture enters actual validated managed save path");
            try{test(page,project,raw);}finally{feedback.Stop();}
        }
    }
    static void TrialTests(string qa,string dem,string id)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(qa,"PressureTrialContract.dll"));var type=assembly.GetType("Xiongxin.OWTS.Desktop.PressureAnalysisTrial",true);
        object trial=Activator.CreateInstance(type,F,null,new object[]{Path.GetDirectoryName(dem)},CultureInfo.InvariantCulture);
        Check((bool)Call(trial,"Commit",id,dem),"actual host trial accepts fully published capacitor DEM and binds acquisition");
        Check(!(bool)Call(trial,"Commit",id,dem)&&((string[])Call(trial,"Snapshot")).Length==1,"same capacitor save cannot duplicate trial count");
        Check((bool)Call(trial,"HasCommitted",id),"formal capacitor commit supports real save ACK");
        string pending=Path.ChangeExtension(dem,".record.pending");File.WriteAllText(pending,"synthetic interrupted publication");
        Reject(()=>Call(trial,"HasCommitted",id),"pending publication cannot supply a repeated save ACK");File.Delete(pending);
        string wrong=Path.Combine(Path.GetDirectoryName(dem),"A_1kV_wrong.dem");File.Copy(dem,wrong);
        Reject(()=>Call(trial,"Commit","other-acquisition",wrong),"DEM must bind exact acquisition identity");
        File.WriteAllText(wrong,"incomplete DEM");Reject(()=>Call(trial,"Commit","other-acquisition",wrong),"incomplete DEM is not a transaction ACK");
        string loc=Path.ChangeExtension(dem,".loc");File.WriteAllText(loc,"no invented location");
        Reject(()=>Call(trial,"Commit","loc",loc),"capacitor trial does not accept fabricated LOC");File.Delete(loc);
        string cable=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dem)),"cable-trial");Directory.CreateDirectory(cable);
        File.WriteAllText(Path.Combine(cable,"ITEM.in"),"负载类型 = \"单芯电缆\"",new System.Text.UTF8Encoding(true));
        string cd=Path.Combine(cable,Path.GetFileName(dem));File.Copy(dem,cd);trial=Activator.CreateInstance(type,F,null,new object[]{cable},CultureInfo.InvariantCulture);
        Reject(()=>Call(trial,"Commit",id,cd),"Cable trial cannot use capacitor DEM instead of the existing LOC contract");
    }
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException||e.InnerException is InvalidOperationException;}Check(rejected,why);}
    [STAThread]static int Main(string[] args)
    {
        try{
            string qa=Path.GetFullPath(args[0]),root=Path.Combine(qa,"fixture");Directory.CreateDirectory(root);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(qa,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            module=Assembly.LoadFrom(Path.Combine(qa,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            controlType=module.GetType("HVX.Controls.PressureTestControl",true);settingsType=module.GetType("Xiongxin.ZeroBench.BenchSettings",true);captureType=module.GetType("Xiongxin.ZeroBench.BenchCapture",true);
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string savedDem=null,savedId=null;
            Run(root,"cap",true,(p,project,raw)=>{
                string before=Hash(Path.Combine(raw,"capture.bin"));
                Check(!Wait((Task<bool>)Call(p,"ContinueCompletedPressurePlanAsync")),"auto next cannot precede actual disk save ACK");
                savedDem=Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",0d));savedId=(string)Get(p,"LocationAcquisitionId");
                Check(Path.GetExtension(savedDem)==".dem"&&File.Exists(savedDem)&&Directory.GetFiles(project,"*.loc").Length==0,"capacitor publishes DEM only with legitimate zero wave speed");
                Check(!File.Exists(Path.ChangeExtension(savedDem,".record.pending")),"formal DEM transaction removes pending marker only after publish");
                var context=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllLines(savedDem)[12].Substring(21));
                Check((string)context["measurementKind"]=="Capacitor"&&Object.Equals(context["locationApplicable"],false),"all consumers receive explicit measurement kind and no location claim");
                Check((string)context["locationAcquisitionId"]==savedId&&(string)context["rawSha256"]==before,"formal record binds exact acquisition and original ADC hash");
                Check((string)context["rawCaptureDirectory"]==raw.Substring(project.Length+1)&&Convert.ToDouble(context["picoRateHz"])>80000,"original capture location and independent full rate are retained, not DEM rate substituted");
                Check((string)context["rawSettingsSha256"]==Hash(Path.Combine(raw,"settings.xml")),"formal DEM binds exact original configuration bytes, not only ADC bytes");
                Check(Object.Equals(context["zeroVoltageAnalysisOptIn"],true),"zero-voltage analysis permission is the actual accepted round opt-in, not filename inference");
                Check(File.Exists(Path.Combine(raw,"settings.xml"))&&Hash(Path.Combine(raw,"capture.bin"))==before,"unique capacitor raw/settings remain untouched after formal save");
                TrialTests(qa,savedDem,savedId);
                Check((bool)Call(p,"CompleteSave",true,""),"actual DEM disk commit may acknowledge successful save");
                // This is the managed next-plan handshake only, never a worker
                // or device call. Analysis remains asynchronous and separate.
                Call(p,"ReportP04Status",true,true,DateTime.UtcNow);
                Check(Wait((Task<bool>)Call(p,"ContinueCompletedPressurePlanAsync")),"real capacitor save ACK allows next existing automatic plan row without fake LOC");
                Check((bool)Get(p,"ActionPending")&&!(bool)Get(p,"HasActiveHardwareSession"),"next plan preparation is not a physical start");
            });
            Run(root,"cap-settings-mismatch",true,(p,project,raw)=>{
                string settingsPath=Path.Combine(raw,"settings.xml"),original=File.ReadAllText(settingsPath),rawHash=Hash(Path.Combine(raw,"capture.bin"));
                object changed;using(var reader=new StringReader(original))changed=new XmlSerializer(settingsType).Deserialize(reader);
                Field(changed,"InductanceH",Convert.ToDouble(Field(changed,"InductanceH"))+1);
                File.WriteAllText(settingsPath,Xml(changed));
                bool rejected=false;try{Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",0d));}catch(InvalidDataException){rejected=true;}
                Check(rejected&&Directory.GetFiles(project,"*.dem").Length==0&&!(bool)Get(p,"DataSaved"),"edited source configuration cannot become a formal capacitor save or ACK");
                Check(Hash(Path.Combine(raw,"capture.bin"))==rawHash,"configuration mismatch retains the untouched original ADC for recovery");
                File.WriteAllText(settingsPath,original);
                string saved=Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",0d));
                var context=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllLines(saved)[12].Substring(21));
                Check((string)context["rawSettingsSha256"]==Hash(settingsPath),"explicit retry binds restored actual settings bytes");
            });
            Run(root,"cap-fail",true,(p,project,raw)=>{
                string hash=Hash(Path.Combine(raw,"capture.bin"));Field(p,"BeforeDetectedRecordPublishForTests",new Action(()=>{Check(Directory.GetFiles(project,"*.record.pending").Length==1,"actual transaction intent precedes final DEM publish");throw new IOException("synthetic publish failure");}));
                bool failed=false;try{Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",0d));}catch(IOException){failed=true;}
                Check(failed&&!(bool)Get(p,"DataSaved")&&Directory.GetFiles(project,"*.dem").Length==0&&Directory.GetFiles(project,"*.loc").Length==0,"failed capacitor publish cannot ACK/save/count an invented location");
                Check(Directory.GetFiles(project,"*.record.pending").Length==0&&Hash(Path.Combine(raw,"capture.bin"))==hash,"failed publish keeps raw and only removes owned transaction intent");
                Field(p,"BeforeDetectedRecordPublishForTests",null);string path=Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",0d));string demHash=Hash(path);
                bool overwrite=false;try{Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",0d));}catch(IOException){overwrite=true;}
                Check(overwrite&&Hash(path)==demHash,"retry may publish once but never overwrites an existing formal DEM");
            });
            Run(root,"cable",false,(p,project,raw)=>{
                string originalRawHash=Hash(Path.Combine(raw,"capture.bin"));
                bool rejected=false;try{Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",0d));}catch(ArgumentOutOfRangeException){rejected=true;}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&Directory.GetFiles(project,"*.loc").Length==0&&Directory.GetFiles(project,"*.dem").Length==0,"Cable still rejects zero velocity and leaves no exposed partial pair");
                string loc=Wait((Task<string>)Call(p,"SaveCurrentPressureRoundAsync",170d));
                Check(Path.GetExtension(loc)==".loc"&&File.Exists(loc)&&File.Exists(Path.ChangeExtension(loc,".dem")),"Cable retains real LOC plus DEM atomic pair");
                Check(!File.Exists(Path.ChangeExtension(loc,".record.pending")),"Cable pair completion marker remains enforced");
                Check(Hash(Path.Combine(raw,"capture.bin"))==originalRawHash,"N-cycle LOC and detected DEM never delete the unique full-segment ADC source");
            });
            Console.WriteLine("PRESSURE_CAPACITOR_SAVE_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;ProductExecuted=false;UserProjectsWritten=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
