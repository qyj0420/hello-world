using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

// Complete synthetic worker ownership, not a mocked completion flag. No SDK,
// hardware worker executable, device enumeration or output command is invoked.
internal static class PressureRoundReleaseUiSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module; static Type type,settingsType,captureType; static string qa; static int checks,failures,rounds;
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool ok,string text){checks++;if(!ok){failures++;Console.WriteLine("FAIL "+text);}}
    static void Pump(int ms=20){var w=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(1);}while(w.ElapsedMilliseconds<ms);}
    static void Wait(Task t){var w=Stopwatch.StartNew();while(!t.IsCompleted&&w.ElapsedMilliseconds<15000)Pump(5);if(!t.IsCompleted)throw new TimeoutException("Offline callback did not complete.");t.GetAwaiter().GetResult();Pump(60);}
    static object Settings(string xml=null){if(xml!=null)using(var r=new StringReader(xml))return new XmlSerializer(settingsType).Deserialize(r);var s=Activator.CreateInstance(settingsType);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART");Field(s,"PicoSerial","OFFLINE-PICO");Field(s,"CaptureDurationMs",1d);Field(s,"PdCalibrationPcPerV",1000d);Field(s,"RangeIndex",5);return s;}
    static string Xml(object s){using(var w=new StringWriter()){new XmlSerializer(settingsType).Serialize(w,s);return w.ToString();}}
    static string Capture(string p,string xml,bool overflow){
        var s=Settings(xml);var x=Activator.CreateInstance(captureType);Field(x,"Settings",s);
        Field(x,"VoltageRaw",Enumerable.Repeat(.001,(int)Get(s,"ExpectedArtCount")).ToArray());
        Field(x,"PdAdc",Enumerable.Repeat((short)(overflow?32767:16000),(int)Get(s,"RequestedPicoCount")).ToArray());
        Field(x,"PicoRateHz",Field(s,"RequestedPicoRateHz"));Field(x,"PicoFullScaleV",new[]{.01,.02,.05,.1,.2,.5,1d,2d,5d,10d,20d}[(int)Field(s,"RangeIndex")]);
        Field(x,"HardwareBits",module.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration").GetMethod("HardwareBits",F).Invoke(null,new[]{Field(s,"ResolutionBits"),Field(s,"RangeIndex")}));
        Field(x,"ArtSerial",Field(s,"ArtSerial"));Field(x,"PicoSerial",Field(s,"PicoSerial"));Field(x,"Runtime","SYNTHETIC NO HARDWARE");Field(x,"CapturedUtc",DateTime.UtcNow);
        if(overflow)Field(x,"Overflow",true);
        string path=Path.Combine(p,"Acquisition","Task39","r"+(++rounds));Call(x,"Save",path);return path;
    }
    public static object Dismiss(object context){var v=Activator.CreateInstance(module.GetType("HVX.Controls.PressureRangeChoice"),true);Field(v,"Action",Enum.Parse(module.GetType("Xiongxin.ZeroBench.RangeDecisionAction"),"Dismiss"));return v;}
    static void HookDismiss(Control c){var f=type.GetField("ZeroRangeDecisionForTests",F);var invoke=f.FieldType.GetMethod("Invoke");var p=Expression.Parameter(invoke.GetParameters()[0].ParameterType);f.SetValue(c,Expression.Lambda(f.FieldType,Expression.Convert(Expression.Call(typeof(PressureRoundReleaseUiSmoke).GetMethod("Dismiss"),Expression.Convert(p,typeof(object))),invoke.ReturnType),p).Compile());}
    static bool Enabled(Control c,string n){return ((Control)Field(c,n)).Enabled;}
    static void Run(string name){
        string project=Path.Combine(qa,name);Directory.CreateDirectory(project);
        using(var form=new Form{ClientSize=new Size(1180,720),StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ShowInTaskbar=false})
        using(var c=(Control)Activator.CreateInstance(type)){
            c.Dock=DockStyle.Fill;form.Controls.Add(c);Set(c,"ProjectPath",project);Set(c,"FrequencyReminderEnabled",false);Set(c,"MaximumPeakVoltageKv",20d);
            Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetDischargeCalibration",1000d,"offline.cal",DateTimeOffset.UtcNow,"A",false);Call(c,"SetSelectedRange","PICO:5");Call(c,"SetAutomaticRange",false);
            if(name=="range-dismiss")Call(c,"SetSelectedRange","PICO:10");
            form.Show();Pump();HookDismiss(c);
            var gate=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;string xml=null;CancellationToken seen=default(CancellationToken);
            Func<string,CancellationToken,Task<string>> worker=(x,t)=>{xml=x;seen=t;Interlocked.Increment(ref calls);return gate.Task;};
            Call(c,"ConfigureZeroVoltageHardwareBackend",Xml(Settings()),worker);
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            Call(c,"RequestPressure");
            var capture=(Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",Get(c,"ActionSequence"));var start=Stopwatch.StartNew();while(calls==0&&start.ElapsedMilliseconds<3000)Pump(5);
            Check(calls==1,name+": exactly one injected worker");Call(c,"SetPressureState","RUNNING",Double.NaN);
            if(name=="emergency-zero")Call(c,"ReportP04Status",true,false,DateTime.UtcNow);
            if(name=="cancel-owner") {Call(c,"RequestHardwareStop");Pump(80);Check(seen.IsCancellationRequested,name+": cancellation requested, not completion");}
            Check((bool)Get(c,"IsBusy")&&(bool)Get(c,"HasActiveHardwareSession")&&(bool)Get(c,"PressureNavigationLocked"),name+": live owner cannot unlock");
            Check(!Enabled(c,"customModeButton"),name+": physical settings disabled while owned");
            Check(Enabled(c,"pressureActionButton"),name+": real stop remains reachable on the main button while owned");
            if(name=="failed-owner"||name=="cancel-owner")gate.SetException(new OperationCanceledException("Synthetic cancellation/failure"));
            string retainedDirectory=null;
            if(name!="failed-owner"&&name!="cancel-owner") {retainedDirectory=Capture(project,xml,name=="range-dismiss");gate.SetResult(retainedDirectory);}
            Wait(capture);
            Console.WriteLine(name+": result="+capture.Result+" state="+Get(c,"PressureStateCode")+" post="+Get(c,"PostProcessStateCode")+" busy="+Get(c,"IsBusy")+" owner="+Get(c,"HasActiveHardwareSession")+" navigation="+Get(c,"PressureNavigationLocked")+" customEnabled="+Enabled(c,"customModeButton")+" emergencyEnabled="+Enabled(c,"highVoltageLabel")+" emergency="+Get(c,"EmergencyStopStatusCode"));
            Check(!(bool)Get(c,"IsBusy")&&!(bool)Get(c,"ActionPending")&&!(bool)Get(c,"HasActiveHardwareSession")&&!(bool)Get(c,"PressureNavigationLocked")&&!(bool)Get(c,"PressureParametersLocked"),name+": owner and navigation terminal");
            Check(Enabled(c,"customModeButton"),name+": mode selector actually reenabled");
            Check(Enabled(c,"highVoltageLabel"),name+": emergency detail remains reachable after completion");
            Check((long)Get(c,"PressureRequestSequence")==1&&calls==1,name+": no automatic retry/resume");
            if(name=="normal-zero"||name=="emergency-zero")Check(capture.Result&&(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),name+": raw zero saved, not LOC analysis");
            else if(name=="range-dismiss") {
                Check(capture.Result&&(string)Get(c,"PressureStateCode")=="COMPLETE"&&(string)Get(c,"PostProcessStateCode")=="OVERRANGE_REJECTED",
                    name+": successful raw capture is distinct from rejected quality terminal");
                Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),name+": rejected quality is not saved or analyzed as valid data");
                Check(retainedDirectory!=null&&Directory.Exists(retainedDirectory)&&Directory.GetFiles(retainedDirectory).Length>0,
                    name+": original capture remains available for review");
            }
            else Check(!capture.Result,name+": truthful failed/canceled result");
            if(name=="emergency-zero")Check((string)Get(c,"EmergencyStopStatusCode")=="ACTIVE"&&!(bool)Get(c,"IsHighVoltageReady"),name+": manual zero input stays permitted; nonzero pressure prohibited");
        }
    }
    [STAThread]static int Main(string[] args){try{
        string root=Path.GetFullPath(args[0]);qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=module.GetType("HVX.Controls.PressureTestControl",true);settingsType=module.GetType("Xiongxin.ZeroBench.BenchSettings",true);captureType=module.GetType("Xiongxin.ZeroBench.BenchCapture",true);
        Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        foreach(string name in new[]{"normal-zero","emergency-zero","failed-owner","cancel-owner","range-dismiss"})Run(name);
        Console.WriteLine("PRESSURE_ROUND_RELEASE_UI_CHECKS="+checks+";FAILURES="+failures+";HardwareAccessed=false;WorkersStarted=0");return failures==0?0:1;
    }catch(Exception e){Console.Error.WriteLine(e);return 2;}}
}
