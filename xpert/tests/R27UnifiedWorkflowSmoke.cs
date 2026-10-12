using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class R27UnifiedWorkflowSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly module,host;static Type type;static string qa;static int checks,choice=1;
    static void Check(bool ok,string message){++checks;if(!ok)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static object NewHost(string name,params object[] args){return host.GetType("Xiongxin.OWTS.Desktop."+name,true).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==args.Length).Invoke(args);}
    static void Pump(){Application.DoEvents();Thread.Sleep(2);}
    static T Await<T>(Task<T> work){var clock=Stopwatch.StartNew();while(!work.IsCompleted&&clock.ElapsedMilliseconds<10000)Pump();Check(work.IsCompleted,"bounded offline completion");return work.GetAwaiter().GetResult();}
    static void Report(Control c,bool high){Call(c,"ReportP04Status",true,high,DateTime.UtcNow);Pump();}
    public static int Choose(object q){return choice;}
    static void Page(Action<Control> test){
        using(var form=new Form())using(var c=(Control)Activator.CreateInstance(type))using(var feedback=new System.Windows.Forms.Timer{Interval=20}){
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ClientSize=new Size(1280,800);c.Dock=DockStyle.Fill;form.Controls.Add(c);
            Set(c,"ProjectPath",qa);Set(c,"SimulationMode",false);Set(c,"MaximumPeakVoltageKv",20d);Set(c,"FrequencyReminderEnabled",false);
            Call(c,"SetPressurePlan",new[]{1d,2d},new[]{2,1},new[]{1d,2d});Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetSelectedPlanStep",0);
            Call(c,"SetDischargeCalibration",2100d,Path.Combine(qa,"calibration.json"),DateTimeOffset.UtcNow,"A",false);
            Set(c,"ActionHandshakeEnabled",true);Report(c,true);Set(c,"AdvancedMode",false);Call(c,"SetPressureState","COMPLETE",0d);
            var hook=type.GetField("NextRangeChoiceForTests",F);var p=Expression.Parameter(hook.FieldType.GetGenericArguments()[0]);
            hook.SetValue(c,Expression.Lambda(hook.FieldType,Expression.Call(typeof(R27UnifiedWorkflowSmoke).GetMethod("Choose"),Expression.Convert(p,typeof(object))),p).Compile());
            // The real idle monitor continues publishing current samples while
            // the two-second countdown runs; this delegate is synthetic only.
            feedback.Tick+=delegate{if((bool)Get(c,"AutomaticPressureCountdownPending"))Call(c,"ReportP04Status",true,true,DateTime.UtcNow);};
            form.Show();Pump();Report(c,true);feedback.Start();try{test(c);}finally{feedback.Stop();}
        }
    }
    static object Finished(Control c,bool blocked=false,bool change=false){
        Check((bool)Get(c,"ActionPending"),"one pending fake acquisition");
        Call(c,"CompleteAction",Get(c,"ActionSequence"),true,"");Call(c,"SetPressureState","COMPLETE",1d);
        Field(c,"postProcessStateCode","COMPLETE");Field(c,"dataSaved",true);Field(c,"dataAnalyzed",true);Field(c,"hasAcquisitionResult",true);
        string id=(string)Get(c,"LocationAcquisitionId");int current=(int)Get(c,"SelectedPicoRangeCode");
        var capture=Activator.CreateInstance(module.GetType("HVX.Controls.PressureZeroVoltageResult"),true);
        var review=Activator.CreateInstance(module.GetType("HVX.Controls.PressureRangeReviewSnapshot"));
        var quality=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.LocationRangeResult"));
        Field(quality,"WindowValid",!blocked);Field(quality,"Change",change);Field(quality,"RecommendedIndex",change?Math.Min(10,current+1):current);
        Field(review,"Schema","HVX.Pressure.RangeReview/R27");Field(review,"Quality",quality);Field(review,"CurrentRangeIndex",current);
        Field(review,"NextRangeIndex",change?Math.Min(10,current+1):current);Field(review,"NeedsPrompt",blocked||change);Field(review,"CanApply",change);Field(review,"NextRoundBlocked",blocked);
        Field(capture,"RangeReview",review);Field(capture,"Quality",quality);Field(capture,"RawSha256","SYNTHETIC-NO-HARDWARE");
        string dir=Path.Combine(qa,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);Field(capture,"Directory",dir);
        Field(c,"lastPressureCapture",capture);Field(c,"lastPressureAcquisitionId",id);Report(c,true);return capture;
    }
    [STAThread]static int Main(string[] args){try{
        qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string modules=Path.Combine(args[0],"modules");
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string path=Directory.GetFiles(modules,new AssemblyName(e.Name).Name+".dll",SearchOption.AllDirectories).FirstOrDefault();return path==null?null:Assembly.LoadFrom(path);};
        module=Assembly.LoadFrom(Path.Combine(modules,"PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=module.GetType("HVX.Controls.PressureTestControl",true);
        host=Assembly.LoadFrom(Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));Application.EnableVisualStyles();
        Page(c=>{
            var session=NewHost("FactoryPressureSession",c,typeof(object),"",NewHost("AppSettings"),qa,NewHost("PressureAnalysisTrial",qa),(Action)(()=>{}),NewHost("Diagnostics"));
            try{
                var progress=(Action<string,string>)Field(session,"Progress");
                progress("EMERGENCY_RELEASED",DateTime.UtcNow.ToString("O")+"|fake timestamped read");Pump();
                Check((string)Get(c,"EmergencyStopStatusCode")=="RELEASED","host timestamped physical-format report reaches UI");
                progress("EMERGENCY_CONFIG","not a physical reading");progress("SIM_EMERGENCY_ACTIVE",DateTime.UtcNow.ToString("O"));
                Check((string)Get(c,"EmergencyStopStatusCode")=="RELEASED","diagnostic and simulation reports cannot change permission");
                Call(c,"SetPressureState","RUNNING",Double.NaN);Pump();progress("EMERGENCY_ACTIVE",DateTime.UtcNow.ToString("O")+"|synthetic low");Pump();
                Check((string)Get(c,"EmergencyStopStatusCode")=="ACTIVE_LATCHED"&&!(bool)Get(c,"IsHighVoltageReady"),"active pressure emergency is latched and inhibits output");
                Pump();progress("EMERGENCY_RELEASED",DateTime.UtcNow.ToString("O"));Pump();
                Check((string)Get(c,"EmergencyStopStatusCode")=="ACTIVE_LATCHED","release cannot erase round emergency");
                Check(!(bool)Call(c,"AcknowledgePressureEmergency"),"cannot acknowledge while worker round still running");
                Call(c,"SetPressureState","FAULT",Double.NaN);progress("EMERGENCY_UNKNOWN",DateTime.UtcNow.ToString("O"));Pump();
                Check((string)Get(c,"EmergencyStopStatusCode")=="ACTIVE_LATCHED"&&!(bool)Call(c,"AcknowledgePressureEmergency"),"unknown feedback preserves latch and blocks acknowledgement");
                Pump();progress("EMERGENCY_RELEASED",DateTime.UtcNow.ToString("O"));Pump();
                Check((bool)Call(c,"AcknowledgePressureEmergency"),"explicit acknowledgement requires finished round and fresh released input");
                Check((bool)Get(c,"AdvancedMode")&&(bool)Get(c,"AutomaticPressureStopped")&&!(bool)Get(c,"ActionPending"),"acknowledgement never resumes or starts a queue");
            }finally{((IDisposable)session).Dispose();}
        });
        Page(c=>{
            Call(c,"RequestPressure");long first=(long)Get(c,"PressureRequestSequence");Finished(c);
            bool continued=Await((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync"));
            if(!continued){foreach(string n in new[]{"advancedMode","planMode","finishCurrentRoundRequested","automaticPlanInterrupted","pressureEmergencyLatched","pressureStateCode","postProcessStateCode","issuedPlanStep","selectedPlanStepIndex","issuedPlanRepetition","currentPlanRepetition","currentAcquisitionReferenceOnly","dataSaved","dataAnalyzed","reviewedRangeAcquisitionId","cycleAcquisitionId"})Console.WriteLine(n+"="+Field(c,n));Console.WriteLine("may="+Call(c,"MayContinueIssuedPressurePlanNoLock")+"; busy="+Get(c,"IsBusy")+"; parametersLocked="+Get(c,"PressureParametersLocked"));}
            Check(continued,"completed saved/analyzed round prepares next request");
            Check((int)Get(c,"SelectedPlanStepIndex")==0&&(int)Get(c,"CurrentPlanRepetition")==2,"first step repetition increments exactly once");
            Check(!Await((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync"))&&(long)Get(c,"PressureRequestSequence")==first+1,"duplicate callback cannot publish another request");
            var confirmation=(Task<bool>)Call(c,"RequestPressureStartConfirmationAsync",Get(c,"CalculatedTargetPeakVoltageKv"),"Offline fixture: confirm this new round",CancellationToken.None);
            Check(!confirmation.IsCompleted,"next automatic round waits for a new inline confirmation");
            c.Controls.Find("CancelPressureRoundButton",true).OfType<Button>().Single().PerformClick();Check(!Await(confirmation),"cancel does not authorize next hardware round");
            Finished(c);Check(Await((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync")),"second completed round advances next step");
            Check((int)Get(c,"SelectedPlanStepIndex")==1&&(int)Get(c,"CurrentPlanRepetition")==1,"plan advances without resetting prior repetition");
            Finished(c);Check(!Await((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync")),"final plan row never loops or replays");
            Check((long)Get(c,"PressureRequestSequence")==first+2,"three planned rounds exactly three requests, zero physical calls");
        });
        foreach(string stop in new[]{"save-failed","analysis-failed","operator-stop","unknown","invalid-window","manual","project-changed","phase-changed","continuation-failed"})Page(c=>{
            Call(c,"RequestPressure");Finished(c,stop=="invalid-window");long before=(long)Get(c,"PressureRequestSequence");
            if(stop=="save-failed")Field(c,"dataSaved",false);
            if(stop=="analysis-failed")Field(c,"dataAnalyzed",false);
            if(stop=="operator-stop")Field(c,"finishCurrentRoundRequested",true);
            if(stop=="unknown")Call(c,"ReportP04Status",false,false,DateTime.UtcNow);
            if(stop=="manual")Set(c,"AdvancedMode",true);
            if(stop=="project-changed")Set(c,"ProjectPath",Path.Combine(qa,"different-project"));
            if(stop=="phase-changed")Set(c,"SelectedPhaseCode","B");
            if(stop=="continuation-failed")Call(c,"InterruptPressureContinuation");
            Check(!Await((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync"))&&(long)Get(c,"PressureRequestSequence")==before,"no automatic retry/advance: "+stop);
        });
        Page(c=>{
            Call(c,"RequestPressure");var capture=Finished(c,false,true);long before=(long)Get(c,"PressureRequestSequence");
            // Fail the derived decision write without changing the raw record.
            Field(capture,"Directory",Path.Combine(qa,"missing-parent",Guid.NewGuid().ToString("N")));
            bool failed=false;try{Await((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync"));}catch(DirectoryNotFoundException){failed=true;}
            Check(failed&&(bool)Get(c,"AutomaticPressureStopped"),"decision write failure stops queue");
            Check((bool)Field(c,"dataSaved")&&(bool)Field(c,"dataAnalyzed")&&(string)Field(c,"postProcessStateCode")=="COMPLETE","next-round failure preserves successful save and analysis");
            Check((long)Get(c,"PressureRequestSequence")==before,"failed next-round setup never starts another acquisition");
        });
        choice=2;Page(c=>{Call(c,"RequestPressure");var capture=Finished(c,false,true);int before=(int)Get(c,"SelectedPicoRangeCode");Set(c,"AdvancedMode",true);
            Check(!Await((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync")),"manual advice does not start another request");
            Check((int)Get(c,"SelectedPicoRangeCode")==Math.Min(10,before+1),"accepted R27 recommendation changes next input range");
            string evidence=Directory.GetFiles((string)Field(capture,"Directory"),"next-range-decision-*.xml").Single();
            Check(File.ReadAllText(evidence).Contains("<StartsHardware>false</StartsHardware>"),"decision trace records no hardware start or safety assertion");});
        Console.WriteLine("R27_UNIFIED_WORKFLOW_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
