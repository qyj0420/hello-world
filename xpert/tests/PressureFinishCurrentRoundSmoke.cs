using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

// Synthetic callbacks/files and an injected worker only: no hardware or SDK.
internal static class PressureFinishCurrentRoundSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type type,settingsType,captureType; static Assembly module; static string qa; static int checks,rounds;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Get(object o,string p){return o.GetType().GetProperty(p,All).GetValue(o,null);}
    static void Set(object o,string p,object v){o.GetType().GetProperty(p,All).SetValue(o,v,null);}
    static object Field(object o,string p){return o.GetType().GetField(p,All).GetValue(o);}
    static void Field(object o,string p,object v){o.GetType().GetField(p,All).SetValue(o,v);}
    static object Call(object o,string m,params object[] a){return o.GetType().GetMethods(All).Single(x=>x.Name==m&&x.GetParameters().Length==a.Length&&x.GetParameters().Select((p,i)=>a[i]==null||p.ParameterType.IsInstanceOfType(a[i])).All(v=>v)).Invoke(o,a);}
    static void Pump(int ms=20){var w=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(1);}while(w.ElapsedMilliseconds<ms);}
    static void Wait(Task task){var w=Stopwatch.StartNew();while(!task.IsCompleted&&w.ElapsedMilliseconds<10000)Pump(5);Check(task.IsCompleted,"bounded offline task finishes");}
    static void Click(object c,string field){var button=(Control)Field(c,field);Check(button.Enabled,field+" effectively enabled");typeof(Control).GetMethod("OnClick",All).Invoke(button,new object[]{EventArgs.Empty});Pump();}
    static object Settings(string xml=null){if(xml!=null)using(var r=new StringReader(xml))return new XmlSerializer(settingsType).Deserialize(r);var s=Activator.CreateInstance(settingsType);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART");Field(s,"PicoSerial","OFFLINE-PICO");Field(s,"CaptureDurationMs",1d);Field(s,"PdCalibrationPcPerV",1000d);Field(s,"CalibrationPulsePc",100d);Field(s,"RangeIndex",5);return s;}
    static string Xml(object s){using(var w=new StringWriter()){new XmlSerializer(settingsType).Serialize(w,s);return w.ToString();}}
    static string Capture(string p,string xml)
    {
        var s=Settings(xml);var capture=Activator.CreateInstance(captureType);Field(capture,"Settings",s);
        Field(capture,"VoltageRaw",Enumerable.Repeat(.001,(int)Get(s,"ExpectedArtCount")).ToArray());
        Field(capture,"PdAdc",Enumerable.Repeat((short)16000,(int)Get(s,"RequestedPicoCount")).ToArray());
        Field(capture,"PicoRateHz",Field(s,"RequestedPicoRateHz"));Field(capture,"PicoFullScaleV",.5d);
        Field(capture,"HardwareBits",module.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration").GetMethod("HardwareBits",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{Field(s,"ResolutionBits"),Field(s,"RangeIndex")}));
        Field(capture,"ArtSerial",Field(s,"ArtSerial"));Field(capture,"PicoSerial",Field(s,"PicoSerial"));Field(capture,"Runtime","OFFLINE SYNTHETIC FIXTURE");Field(capture,"CapturedUtc",DateTime.UtcNow);
        var result=Path.Combine(p,"Acquisition","Task39","r"+(++rounds));Call(capture,"Save",result);return result;
    }
    static void Run(string name,Action<Control,string> action)
    {
        string p=Path.Combine(qa,name);Directory.CreateDirectory(p);
        using(var f=new Form{ClientSize=new Size(1280,760),Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
        using(var c=(Control)Activator.CreateInstance(type))using(var feedback=new System.Windows.Forms.Timer{Interval=20})
        {
            c.Dock=DockStyle.Fill;f.Controls.Add(c);Set(c,"ProjectPath",p);Set(c,"FrequencyReminderEnabled",false);Set(c,"MaximumPeakVoltageKv",20d);
            Call(c,"SetPressurePlan",new[]{0d,1d},new[]{3,2},new[]{0d,5d});Call(c,"SetSelectedPlanStep",0);Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetDischargeCalibration",1000d,"offline-A.cal",DateTimeOffset.UtcNow,"A",false);Call(c,"SetSelectedRange","PICO:5");
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Call(c,"SetAdvancedMode",false);
            feedback.Tick+=delegate{Call(c,"ReportP04Status",true,true,DateTime.UtcNow);};
            f.Show();Pump();feedback.Start();try{action(c,p);}finally{feedback.Stop();}
        }
        Pump();
    }
    static void LockedParameters(Control c,string p)
    {
        Check((bool)Get(c,"PressureParametersLocked"),"physical parameters locked");
        var target=Get(c,"CalculatedTargetPeakVoltageKv");var sequence=Get(c,"ActionSequence");var json=Get(c,"ActionParametersJson");var phase=Get(c,"SelectedPhaseCode");
        var range=Get(c,"SelectedRangeCode");var repetition=Get(c,"CurrentPlanRepetition");var mode=Get(c,"AdvancedMode");var path=Get(c,"ProjectPath");var automatic=Get(c,"AutomaticRange");
        Call(c,"SetSelectedPhase","B");Call(c,"SetPolarity",false);Call(c,"SetAdvancedMode",!(bool)mode);Call(c,"SetAutomaticRange",false);Call(c,"SetSelectedRange","PICO:6");
        Call(c,"SetPressureProgramMode","CUSTOM");Call(c,"SetCustomVoltageValue",1d);Call(c,"SetSelectedPlanStep",1);Call(c,"SetCurrentPlanRepetition",2);
        Call(c,"SetPressurePlan",new[]{2d},new[]{1},new[]{9d});Set(c,"BaseU0VoltageKv",12d);Set(c,"PlanVoltagesArePeak",true);Set(c,"ProjectPath",Path.Combine(p,"other"));Call(c,"ClearDisplayData");
        Check(Equals(target,Get(c,"CalculatedTargetPeakVoltageKv"))&&Equals(phase,Get(c,"SelectedPhaseCode"))&&Equals(range,Get(c,"SelectedRangeCode")),"target phase range remain immutable");
        Check(Equals(mode,Get(c,"AdvancedMode"))&&Equals(repetition,Get(c,"CurrentPlanRepetition"))&&Equals(path,Get(c,"ProjectPath"))&&Equals(automatic,Get(c,"AutomaticRange")),"mode repetition project and range mode remain immutable");
        Check((bool)Get(c,"PositivePolarity")&&(string)Get(c,"PressureProgramModeCode")=="PLAN","polarity/program unchanged");
        Check(Equals(sequence,Get(c,"ActionSequence"))&&Equals(json,Get(c,"ActionParametersJson")),"setters never replace current mailbox");
        Click(c,"twoCycleButton");Check((string)Get(c,"WaveformWindowCode")=="TWO","cycle display can change while locked");
        var dashboard=(Control)Field(c,"spectrumDashboardView");var review=(Control)Field(dashboard,"Review");Check(dashboard.Enabled&&review.Enabled,"spectrum remains available");
        Call(c,"HandleSpectrumUserAction","SPECTRUM_VIEW");
        Check(Equals(sequence,Get(c,"ActionSequence"))&&Equals(json,Get(c,"ActionParametersJson")),"display actions preserve immutable mailbox and ACK");
    }
    static void Mailbox(Control c){Set(c,"ActionHandshakeEnabled",true);Call(c,"RequestPressure");Check((bool)Get(c,"ActionPending"),"current pressure publishes");Call(c,"SetPressureState","COMPLETE",0d);}
    static void Tests()
    {
        Run("queued-start",(c,p)=>{
            // Queue a background caller while the UI is still idle, then start
            // the first round before that queued callback can commit.
            var queued=Task.Run(()=>Call(c,"RequestPressure"));var w=Stopwatch.StartNew();
            while(((System.Collections.ICollection)Field(c,"outstandingUiRequests")).Count==0&&!queued.IsCompleted&&w.ElapsedMilliseconds<2000)Thread.Sleep(1);
            Check(!queued.IsCompleted,"background request is waiting for UI commit");Call(c,"RequestPressure");string id=(string)Get(c,"LocationAcquisitionId");Wait(queued);
            Check((long)Get(c,"PressureRequestSequence")==1&&(string)Get(c,"LocationAcquisitionId")==id,"queued request cannot replace a round that started before its UI commit");
        });
        Run("auto-idle",(c,p)=>{
            Check(!(bool)Get(c,"PressureNavigationLocked")&&!(bool)Get(c,"CanFinishCurrentRound"),"AUTO selected idle does not lock navigation");
            Check((bool)Get(c,"AutomaticRange")&&!((Control)Field(c,"manualRangeButton")).Enabled,"AUTO forces automatic range and disables manual range");
            Call(c,"SetAutomaticRange",false);Check((bool)Get(c,"AutomaticRange"),"public setter cannot enable manual range in AUTO");
            Call(c,"SetAdvancedMode",true);Click(c,"manualRangeButton");Check(!(bool)Get(c,"AutomaticRange"),"manual mode restores manual range");
        });
        Run("save-ack",(c,p)=>{
            Mailbox(c);Check((bool)Call(c,"ReportAcquisitionCompleted",false),"synthetic current acquisition reports save needed");
            long seq=(long)Get(c,"ActionSequence");string json=(string)Get(c,"ActionParametersJson");
            LockedParameters(c,p);Click(c,"pressureActionButton");
            Check((bool)Get(c,"AutomaticPressureStopped")&&!(bool)Get(c,"AdvancedMode")&&(bool)Get(c,"AutomaticPressureEnabled"),"finish preserves AUTO and auto-save preference while revoking scheduling");
            Check((bool)Get(c,"FinishCurrentRoundPending")&&(bool)Get(c,"PressureNavigationLocked"),"current save and ACK still lock navigation");
            Check((long)Get(c,"ActionSequence")==seq&&(string)Get(c,"ActionParametersJson")==json&&!(bool)Get(c,"DataSaved"),"finish neither fabricates save nor replaces action");
            LockedParameters(c,p);Check((bool)Call(c,"CompleteAction",seq,true,""),"actual mailbox ACK accepted");
            Check((bool)Get(c,"PressureParametersLocked"),"ACK alone does not finish pending save");Call(c,"SetAdvancedMode",true);Check(!(bool)Get(c,"AdvancedMode"),"SAVE pending cannot change selected mode");
            Check((bool)Call(c,"CompleteSave",true,""),"actual synthetic save completion accepted");Pump();
            Check((bool)Get(c,"DataSaved")&&(bool)Get(c,"FinishCurrentRoundPending")&&(bool)Get(c,"PressureNavigationLocked"),"save ACK waits for actual analysis completion");
            Field(c,"cycleAcquisitionId","progress-fixture");
            Call(c,"ReportPressureAnalysisProgress","progress-fixture",1,3);
            Check(((Control)Field(c,"pressureActionButton")).Text.Contains("1/3"),"analysis record progress remains visible while finishing queue");
            Call(c,"ReportPressureAnalysisProgress","stale-fixture",3,3);
            Check((int)Field(c,"analyzedRecordCount")==1,"stale acquisition cannot change progress");
            Call(c,"ReportPressureAnalysisProgress","progress-fixture",4,3);
            Check((int)Field(c,"analyzedRecordCount")==1&&!(bool)Get(c,"DataAnalyzed"),"invalid counts rejected and progress never acknowledges analysis");
            Check((bool)Call(c,"CompleteAnalysis",true,""),"actual synthetic analysis completion accepted");Pump();
            Call(c,"ReportPressureAnalysisProgress","progress-fixture",2,3);
            Check((int)Field(c,"analyzedRecordCount")==1,"late progress ignored after analysis completion");
            Check((bool)Get(c,"DataSaved")&&(bool)Get(c,"DataAnalyzed")&&!(bool)Get(c,"FinishCurrentRoundPending")&&!(bool)Get(c,"PressureNavigationLocked"),"save+analysis+ACK finish only current round");
            Check((int)Get(c,"SelectedPlanStepIndex")==0&&(int)Get(c,"CurrentPlanRepetition")==1&&(long)Get(c,"PressureRequestSequence")==1,"no repeat or next plan row started");
            var next=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Wait(next);Check(!next.Result,"finish latch blocks explicit next automatic schedule");
            Check((bool)Get(c,"AutomaticRange")&&!((Control)Field(c,"manualRangeButton")).Enabled,"finish does not silently change automatic range mode");
            Call(c,"SetAdvancedMode",true);Click(c,"manualRangeButton");Check(!(bool)Get(c,"AutomaticRange"),"explicit idle mode change permits manual range");
            Call(c,"CompleteAction",(long)Get(c,"ActionSequence"),true,"");
            Call(c,"SetSelectedPhase","B");Check(!(bool)Get(c,"AutomaticPressureStopped"),"new phase clears finish context");
        });
        Run("overrange-save-failure",(c,p)=>{
            Mailbox(c);Call(c,"ReportAcquisitionCompleted",true);Call(c,"CompleteAction",(long)Get(c,"ActionSequence"),true,"");
            Check((bool)Get(c,"OverRangeDecisionPending")&&(bool)Get(c,"PressureNavigationLocked"),"overrange decision belongs to current round");
            Click(c,"pressureActionButton");LockedParameters(c,p);Check((bool)Call(c,"ResolveOverRangeDecision",true),"current overrange save decision remains available");
            Check((bool)Call(c,"CompleteSave",false,"offline save failed"),"actual failed save accepted");Pump();
            Check(!(bool)Get(c,"DataSaved")&&(string)Get(c,"PostProcessStateCode")=="SAVE_FAILED"&&(string)Get(c,"LastPostProcessError")=="offline save failed","finish never masks save failure");
            Check(!(bool)Get(c,"PressureNavigationLocked")&&(long)Get(c,"PressureRequestSequence")==1,"failed terminal round starts no retry");
        });
        Run("countdown-finish",(c,p)=>{
            Call(c,"SetPressureState","COMPLETE",0d);Set(c,"AutomaticPressureCountdownSeconds",1);Set(c,"AutomaticPressureCountdownEnabled",false);
            var next=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check((bool)Get(c,"AutomaticPressureCountdownPending"),"countdown running");LockedParameters(c,p);
            Pump(150);Check(!next.IsCompleted,"display changes do not cancel countdown");Click(c,"pressureActionButton");Wait(next);
            Check(!next.Result&&(long)Get(c,"PressureRequestSequence")==0&&!(bool)Get(c,"PressureNavigationLocked"),"finish during countdown starts no new round");
        });
        Run("countdown-display",(c,p)=>{
            Call(c,"SetPressureState","COMPLETE",0d);Set(c,"AutomaticPressureCountdownSeconds",1);Set(c,"AutomaticPressureCountdownEnabled",false);
            var next=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Click(c,"twoCycleButton");Call(c,"HandleSpectrumUserAction","SPECTRUM_VIEW");Wait(next);
            Check(next.Result&&(long)Get(c,"PressureRequestSequence")==1,"display viewing permits exactly one authorized countdown request");
            Call(c,"SetPressureState","FAULT",Double.NaN);
        });
        Run("manual-host-countdown",(c,p)=>{
            Call(c,"SetAdvancedMode",true);Call(c,"SetPressureState","COMPLETE",0d);Set(c,"AutomaticPressureCountdownSeconds",1);Set(c,"AutomaticPressureCountdownEnabled",false);
            var next=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Check((bool)Get(c,"CanFinishCurrentRound"),"explicit legacy host countdown can be stopped even in MANUAL");Click(c,"pressureActionButton");Wait(next);Check(!next.Result&&(long)Get(c,"PressureRequestSequence")==0,"hidden manual-host countdown finishes without pressure start");
        });
        foreach(bool fail in new[]{false,true})Run(fail?"worker-failure":"worker-success",(c,p)=>{
            var gate=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);CancellationToken seen=default(CancellationToken);string xml=null;int calls=0;
            Func<string,CancellationToken,Task<string>> worker=(x,token)=>{xml=x;seen=token;Interlocked.Increment(ref calls);return gate.Task;};
            Call(c,"ConfigureZeroVoltageHardwareBackend",Xml(Settings()),worker);Call(c,"RequestPressure");long seq=(long)Get(c,"ActionSequence");
            var task=(Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",seq);var wait=Stopwatch.StartNew();while(calls==0&&wait.ElapsedMilliseconds<3000)Pump(10);Check(calls==1,"offline worker started");
            Check((bool)Call(c,"RequestFinishCurrentRound"),"explicit legacy finish-current API accepted");LockedParameters(c,p);Pump(250);Check(!seen.IsCancellationRequested&&!task.IsCompleted&&(bool)Get(c,"HasActiveHardwareSession"),"finish-current API remains distinct from main-button immediate stop and never fakes completion");
            if(fail)gate.SetException(new IOException("offline capture failure"));else gate.SetResult(Capture(p,xml));Wait(task);Pump();
            Check(task.Result==!fail&&(bool)Get(c,"DataSaved")==!fail,"truthful worker outcome and saved flag");
            Check(calls==1&&(long)Get(c,"PressureRequestSequence")==1&&!(bool)Get(c,"HasActiveHardwareSession")&&!(bool)Get(c,"PressureNavigationLocked"),"owned round completes without next worker or residual navigation lease");
            Check((string)Get(c,"PressureStateCode")== (fail?"FAULT":"COMPLETE"),"real callback state preserved");
            var next=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Wait(next);Check(!next.Result,"worker completion cannot restart AUTO after finish");
        });
        Run("delete-host",(c,p)=>{
            Call(c,"SetAdvancedMode",true);Call(c,"ApplyRealtimeMetrics",2d,.2d,50d,.1d);Set(c,"ActionHandshakeEnabled",true);Call(c,"BeginAction");Call(c,"PublishAction","DELETE");Call(c,"ClearDisplayData");
            Check((string)Get(c,"AnalysisStatus")=="CLEARED","authorized DELETE clears display before ACK");Call(c,"CompleteAction",(long)Get(c,"ActionSequence"),true,"");
        });
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            string root=Path.GetFullPath(args[0]);qa=Path.Combine(Path.GetFullPath(args[1]),"finish-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=module.GetType("HVX.Controls.PressureTestControl",true);settingsType=module.GetType("Xiongxin.ZeroBench.BenchSettings",true);captureType=module.GetType("Xiongxin.ZeroBench.BenchCapture",true);
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Tests();Console.WriteLine("PASS "+checks+" | No hardware invoked | "+qa);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
