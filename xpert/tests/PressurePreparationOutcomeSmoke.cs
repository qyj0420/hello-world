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

// Real control + injected delegates, synthetic DI only. No SDK/worker/Main.
internal static class PressurePreparationOutcomeSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module; static Type type,settingsType; static int checks; static string qa;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static T Ui<T>(Control c,Func<T> f){return c.InvokeRequired?(T)c.Invoke(f):f();}
    static void Pump(int ms){var t=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(2);}while(t.ElapsedMilliseconds<ms);}
    static bool Wait(Task<bool> t){var w=Stopwatch.StartNew();while(!t.IsCompleted&&w.ElapsedMilliseconds<6000)Pump(5);Check(t.IsCompleted,"bounded fake completion");return t.GetAwaiter().GetResult();}
    static string Settings(){var s=Activator.CreateInstance(settingsType);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART");Field(s,"PicoSerial","OFFLINE-PICO");Field(s,"CaptureDurationMs",1d);Field(s,"PdCalibrationPcPerV",1000d);Field(s,"RangeIndex",5);Field(s,"AutomaticSharedTrigger",true);using(var w=new StringWriter()){new XmlSerializer(settingsType).Serialize(w,s);return w.ToString();}}
    static void Run(string scenario)
    {
        string project=Path.Combine(qa,scenario);Directory.CreateDirectory(project);
        using(var form=new Form{Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false,Size=new Size(1200,760)})
        using(var c=(Control)Activator.CreateInstance(type))using(var heartbeat=new System.Windows.Forms.Timer{Interval=50})
        {
            form.Controls.Add(c);c.Dock=DockStyle.Fill;Set(c,"ProjectPath",project);Set(c,"FrequencyReminderEnabled",false);
            Set(c,"MaximumPeakVoltageKv",20d);double plannedTarget=scenario.StartsWith("request-rejected")||scenario.StartsWith("round-stopped")||scenario.StartsWith("outcome-")||scenario=="safety-after-click-nonzero"?6.2:0;
            Call(c,"SetPressurePlan",new[]{plannedTarget},new[]{1},new[]{plannedTarget});Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetDischargeCalibration",1000d,"offline.cal",DateTimeOffset.UtcNow,"A",false);Call(c,"SetSelectedRange","PICO:5");Call(c,"SetAutomaticRange",false);
            form.Show();Pump(20);Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            if(scenario=="countdown")Call(c,"SetAdvancedMode",false);
            heartbeat.Tick+=(s,e)=>Call(c,"ReportP04Status",true,true,DateTime.UtcNow);heartbeat.Start();
            Field(c,"ZeroTriggerConfirmationForTests",(Func<bool,bool>)(automatic=>{if(scenario=="legacy-stop")return true;throw new Exception("integrated backend must not show extra trigger confirmation");}));
            int calls=0;
            Func<string,double,CancellationToken,Task<string>> fake=async(xml,target,token)=>{
                Interlocked.Increment(ref calls);
                long request=Ui(c,()=>(long)Get(c,"PressureRequestSequence"));long action=Ui(c,()=>(long)Get(c,"ActionSequence"));
                bool consumed=Ui(c,()=>(bool)Call(c,"TryConsumePressureOperatorStartIntent",request,target));
                if(scenario=="direct") {Check(!consumed,"programmatic request cannot create operator intent");throw new InvalidOperationException("no actual start");}
                Check(consumed,"actual start binds current request/target");
                Check(!Ui(c,()=>(bool)Call(c,"TryConsumePressureOperatorStartIntent",request,target)),"start intent is single consume");
                Check(!Ui(c,()=>(bool)Call(c,"IsPressureOperatorStartIntentCurrent",request+1,target)),"old intent cannot authorize another request");
                Check(!Ui(c,()=>(bool)Call(c,"IsPressureOperatorStartIntentCurrent",request,target+1)),"different target rejected");
                if(scenario=="legacy-stop"){
                    Ui(c,()=>{Check((bool)Call(c,"RequestCurrentPressureStop"),"legacy input receives actual stop");return true;});
                    throw new OperationCanceledException("legacy backend native state remains unknown");
                }
                var prepare=Ui(c,()=>(Task<bool>)Call(c,"RequestAutomaticCaptureCountdownAsync",token));
                if(scenario=="countdown")Ui(c,()=>{((Form)Field(c,"pressureCountdownDialog")).Close();return true;});
                bool ready=await prepare.ConfigureAwait(false);
                if(scenario=="countdown"){
                    Check(!ready&&Ui(c,()=>(bool)Call(c,"WasPressurePreparationCanceled",action)),"explicit countdown close records same owned action");
                    throw new OperationCanceledException("operator countdown canceled before dispatch");
                }
                Check(ready,"manual preparation does not add a confirmation dialog");
                if(scenario.StartsWith("request-rejected"))Ui(c,()=>{
                    Check((bool)Call(c,"TryBeginPreparedHardwareCapture",token),"rejection receipt belongs to committed owned request");
                    Check(!(bool)Call(c,"ReportPressureRequestRejected",action-1),"stale rejection cannot change new request");
                    if(scenario!="request-rejected-unverified")Check((bool)Call(c,"ReportPressureRequestRejected",action),"exact host-verified rejection delivered to current owner");
                    if(scenario=="request-rejected-di-error"){heartbeat.Stop();Call(c,"ReportP04Status",false,false,DateTime.UtcNow);}
                    return true;
                });
                if(scenario.StartsWith("outcome-"))Ui(c,()=>{
                    Check((bool)Call(c,"TryBeginPreparedHardwareCapture",token),"fake failure belongs to committed nonzero request");
                    string status=scenario=="outcome-session-ended"?"SessionEndedAfterFailedRelease":scenario=="outcome-release-unconfirmed"?"StopReleaseUnconfirmed":"WorkerSessionExited";
                    Check(!(bool)Call(c,"ReportPressureCaptureFailureStatus",action-1,status),"stale classification cannot overwrite new owner display");
                    Check(!(bool)Call(c,"ReportPressureCaptureFailureStatus",action,"FactoryFailedRoundReleasedException"),"arbitrary exception text cannot manufacture typed display outcome");
                    Check((bool)Call(c,"ReportPressureCaptureFailureStatus",action,status),"current Host typed failure changes display only");
                    return true;
                });
                if(scenario=="zero-retry-permit")Ui(c,()=>{
                    Check((bool)Call(c,"TryBeginPreparedHardwareCapture",token),"first zero capture commits");
                    Check(!(bool)Call(c,"TryConsumePressureCaptureIntent",request,0d),"without recorded range permit no second capture");
                    var owner=Field(c,"zeroActionOwner");Field(owner,"RetryPermitRange",6);
                    Check(!(bool)Call(c,"TryConsumePressureCaptureIntent",request,0d),"mismatched selected range cannot consume permit");
                    string original=(string)Field(c,"selectedRangeCode");Field(c,"selectedRangeCode",((string[])type.GetField("RangeCodes",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).GetValue(null))[6]);
                    Check((bool)Call(c,"TryConsumePressureCaptureIntent",request,0d),"same-action zero range permit is consumed once");
                    Check(!(bool)Call(c,"TryConsumePressureCaptureIntent",request,0d),"range permit cannot replay");
                    Field(c,"selectedRangeCode",original);return true;
                });
                if(scenario=="stop-preparation")Check(Ui(c,()=>(bool)Call(c,"RequestCurrentPressureStop")),"actual main-button stop cancels uncommitted preparation");
                if(scenario=="user")Check(Ui(c,()=>(bool)Call(c,"ReportPressurePreparationCanceled",action)),"explicit same-action cancellation accepted");
                if(scenario=="stale")Check(!Ui(c,()=>(bool)Call(c,"ReportPressurePreparationCanceled",action-1)),"stale cancellation cannot touch new action");
                if(scenario=="committed"||scenario=="verified-stop"||scenario=="unverified-stop"||scenario=="safety-proof"||scenario.StartsWith("input-stopped")||scenario.StartsWith("round-stopped")){
                    Check(Ui(c,()=>(bool)Call(c,"TryBeginPreparedHardwareCapture",token)),"fake dispatch commits boundary");
                    Check(!Ui(c,()=>(bool)Call(c,"ReportPressurePreparationCanceled",action)),"post-dispatch exception cannot become harmless cancellation");
                    if(scenario!="committed")Ui(c,()=>{
                        if(scenario!="safety-proof"&&scenario!="input-stopped-not-operator"&&scenario!="round-stopped-not-operator")Check((bool)Call(c,"ReportPressureOperatorStop",action),"explicit stop belongs to this action");
                        Call(c,"RequestHardwareStop");Call(c,"InterruptPressureContinuation");
                        if(scenario.StartsWith("input-stopped")){
                            Check(!(bool)Call(c,"ReportPressureInputStopConfirmed",action-1),"old input-stop proof cannot mark new action");
                            Check((bool)Call(c,"ReportPressureInputStopConfirmed",action),"synthetic exact current input-stop proof");
                            if(scenario=="input-stopped-di-error"){heartbeat.Stop();Call(c,"ReportP04Status",false,false,DateTime.UtcNow);}
                        }else if(scenario.StartsWith("round-stopped")){
                            Check(!(bool)Call(c,"ReportPressureRoundStopConfirmed",action-1),"old nonzero stop receipt cannot mark a new owner");
                            if(scenario!="round-stopped-unverified")Check((bool)Call(c,"ReportPressureRoundStopConfirmed",action),"synthetic host-verified receipt bound to current nonzero owner");
                            if(scenario=="round-stopped-di-error"){heartbeat.Stop();Call(c,"ReportP04Status",false,false,DateTime.UtcNow);}
                        }else if(scenario!="unverified-stop")Check((bool)Call(c,"ReportPressureCaptureNeverStarted",action),"synthetic verified pre-native protocol delivered to owned action");
                        return true;
                    });
                }
                if(scenario=="safety-after-click"||scenario=="safety-after-click-nonzero"){
                    Check(Ui(c,()=>(bool)Call(c,"ReportPressurePreparationCanceled",action)),"operator click precedes later unknown DI");
                    Ui(c,()=>{heartbeat.Stop();Call(c,"ReportP04Status",false,false,DateTime.UtcNow);return true;});
                }
                throw new OperationCanceledException("synthetic outcome, not evidence of SDK release");
            };
            if(scenario=="legacy-stop")Call(c,"ConfigureZeroVoltageHardwareBackend",Settings(),(Func<string,CancellationToken,Task<string>>)((xml,token)=>fake(xml,0,token)));
            else Call(c,"ConfigurePressureHardwareBackend",Settings(),fake);
            if(scenario=="direct")Call(c,"RequestPressure");else Call(c,"PressureActionClicked");
            Check((bool)Get(c,"ActionPending"),"one owned request published");
            Check(!Wait((Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",Get(c,"ActionSequence"))),"cancellation/fault never returns successful acquisition");
            // Unknown DI is display-only for an explicitly manual input-only
            // zero action, not an SDK communication/release success. Keep the
            // nonzero counterpart below as a strict FAULT negative control.
            // The old manual-zero expectation failed at r43/ui-repair-c9fa0bbb14;
            // retained receipt is not rewritten or silently excluded.
            bool rejected=scenario=="request-rejected";
            bool normal=scenario=="user"||scenario=="countdown"||scenario=="stop-preparation"||scenario=="verified-stop"||scenario=="safety-after-click";
            bool confirmedStop=scenario=="input-stopped"||scenario=="input-stopped-di-error"||scenario=="round-stopped";
            Console.WriteLine("TERMINAL="+scenario+"; PRESSURE="+Get(c,"PressureStateCode")+"; POST="+Get(c,"PostProcessStateCode"));
            Check((string)Get(c,"PressureStateCode")== (confirmedStop?"STOPPED":normal||rejected?"WAITING":"FAULT"),scenario+" correct terminal classification");
            Check((string)Get(c,"PostProcessStateCode")== (normal||confirmedStop?"CANCELED":rejected?"READY":"ACQUISITION_FAILED"),scenario+" correct postprocess classification");
            if(rejected){
                Check((string)Field(c,"lastActionCode")=="PRESSURE_REQUEST_REJECTED","rejection is distinct from successful/stop/cancelled acquisition");
                Check(!(bool)Field(c,"dataSaved")&&!(bool)Field(c,"dataAnalyzed"),"not-started request cannot become accepted data");
                Check((bool)Field(c,"automaticPlanInterrupted"),"rejection revokes continuation; explicit new start required");
                Check(((string)Field(c,"lastError")).Contains("未开始"),"simple not-started message replaces generic device fault");
                Check(!(bool)Get(c,"PressureParametersLocked"),"rejected request releases UI parameter lock without starting another round");
            }
            // The earlier caption assertion failed in qa-healthy-stop-b8ee25d581.
            // Superseded by the operator's three-caption contract: the primary
            // button offers the next explicit start, while terminal status and
            // the actual owned stop receipt remain separate and factual.
            if(scenario=="input-stopped"){
                Check(((Button)Field(c,"pressureActionButton")).Text.Split('\n')[0].Trim()=="开始加压","confirmed input stop shows the explicit next-start caption, not a live/fault caption");
                Check((string)Field(c,"lastActionCode")=="INPUT_CAPTURE_STOPPED","confirmed stop retains its actual input-only terminal status");
                Check(!(bool)Field(c,"dataSaved")&&!(bool)Field(c,"dataAnalyzed"),"stopped input capture is not accepted as saved or analyzed");
            }
            Check(calls==1&&!(bool)Get(c,"ActionPending")&&!(bool)Get(c,"HasActiveHardwareSession"),"fake owner terminal, no automatic next request");
            if(scenario.StartsWith("round-stopped")){
                Check(!(bool)Field(c,"dataSaved")&&!(bool)Field(c,"dataAnalyzed"),"stopped or uncertain round cannot become saved/analyzed");
                Check(!(bool)Call(c,"WasPressureOperatorRoundStopped",Get(c,"ActionSequence")),"terminal receipt cannot carry over to a later owner");
            }
            if(scenario.StartsWith("outcome-")){
                string message=(string)Field(c,"lastError");
                Check(!message.Contains("OperationCanceledException")&&!message.Contains("FactoryFailedRoundReleasedException"),"operator text omits technical exception but original thrown failure is unchanged");
                Check(message.Contains(scenario=="outcome-session-ended"?"需要重新连接":scenario=="outcome-release-unconfirmed"?"尚未核验":"清理状态待核验"),"exact failure state remains factual");
                Check(!(bool)Field(c,"dataSaved")&&!(bool)Field(c,"dataAnalyzed"),"pure display classification cannot grant saving/analysis");
            }
            Check(!Ui(c,()=>(bool)Call(c,"WasPressurePreparationCanceled",(long)Get(c,"ActionSequence"))),"terminal owner marker is not retained for later rounds");
            var label=(Control)Field(c,"highVoltageLabel");
            Check(!label.Text.Contains("可加压")&&!label.Text.Contains("HV permitted"),"DI label is factual, not a full readiness permit");
            if(scenario=="request-rejected-di-error")Check(!label.Text.Contains("急停已解除"),"not-started receipt never repairs unknown DI");
            if(scenario!="safety-after-click"&&scenario!="safety-after-click-nonzero"&&scenario!="input-stopped-di-error"&&scenario!="round-stopped-di-error"&&scenario!="request-rejected-di-error")Check(label.Text.Contains("急停已解除")&&label.BackColor.R>label.BackColor.G,"released DI retains requested red color");
            heartbeat.Stop();
            Console.WriteLine("SCENARIO_PASS="+scenario);
        }
    }
    static void AutomaticIntent()
    {
        using(var form=new Form{Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
        using(var c=(Control)Activator.CreateInstance(type))using(var pulse=new System.Windows.Forms.Timer{Interval=50}) {
            form.Controls.Add(c);c.Dock=DockStyle.Fill;Set(c,"ProjectPath",qa);Set(c,"FrequencyReminderEnabled",false);Set(c,"MaximumPeakVoltageKv",20d);
            Call(c,"SetPressurePlan",new[]{0d,0d},new[]{2,1},new[]{0d,0d});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetSelectedPlanStep",0);Set(c,"ActionHandshakeEnabled",true);
            form.Show();Pump(20);Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Call(c,"SetAdvancedMode",false);Set(c,"AutomaticPressureCountdownEnabled",false);
            pulse.Tick+=(s,e)=>Call(c,"ReportP04Status",true,true,DateTime.UtcNow);pulse.Start();
            Call(c,"PressureActionClicked");long first=(long)Get(c,"PressureRequestSequence");
            Check((bool)Call(c,"TryConsumePressureOperatorStartIntent",first,0d),"first automatic request consumes actual start");
            Action fakeComplete=()=>{
                // Synthetic postprocessing completion only: no backend is configured.
                Field(c,"hasAcquisitionResult",true);Field(c,"currentAcquisitionReferenceOnly",true);
                Call(c,"SetPressureState","COMPLETE",0d);Call(c,"SetPostProcessStateNoLock","COMPLETE");Call(c,"CompleteAction",Get(c,"ActionSequence"),true,"");
            };
            fakeComplete();
            bool continued=Wait((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync"));
            if(!continued){foreach(string n in new[]{"advancedMode","planMode","finishCurrentRoundRequested","automaticPlanInterrupted","pressureEmergencyLatched","pressureStateCode","postProcessStateCode","issuedPlanStep","selectedPlanStepIndex","issuedPlanRepetition","currentPlanRepetition","currentAcquisitionReferenceOnly","currentAcquisitionReferencePreview","continuedPressureSequence","pressureRequestSequence"})Console.WriteLine(n+"="+Field(c,n));Console.WriteLine("may="+Call(c,"MayContinueIssuedPressurePlanNoLock")+"; locked="+Get(c,"PressureParametersLocked")+"; cancel="+Get(c,"LastCountdownCancellationSource"));}
            Check(continued,"legal same-plan continuation issues after countdown");
            long second=(long)Get(c,"PressureRequestSequence");Check(second==first+1&&(bool)Get(c,"PressureRequestIsAutomaticContinuation"),"continuation sequence advances once");
            Check((bool)Call(c,"TryConsumePressureOperatorStartIntent",second,0d),"normal continuation inherits the explicit plan start");
            fakeComplete();Check(Wait((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync")),"normal next plan row uses same explicitly started plan");
            long third=(long)Get(c,"PressureRequestSequence");Check((bool)Call(c,"TryConsumePressureOperatorStartIntent",third,0d),"next row intent bound to new sequence");
            Call(c,"InterruptPressureContinuation");Check(!(bool)Call(c,"IsPressureOperatorStartIntentCurrent",third,0d),"stop revokes old intent even with fresh HIGH");
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Check(!(bool)Call(c,"IsPressureOperatorStartIntentCurrent",third,0d),"HIGH does not restore stopped request");
            Check(!Wait((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync")),"stopped plan cannot restart itself");
            pulse.Stop();
        }
    }
    [STAThread]static int Main(string[] args){try{
        string root=Path.GetFullPath(args[0]);qa=Path.Combine(Path.GetFullPath(args[1]),"prep-outcomes");Directory.CreateDirectory(qa);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=module.GetType("HVX.Controls.PressureTestControl",true);settingsType=module.GetType("Xiongxin.ZeroBench.BenchSettings",true);
        Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        string[] scenarios=args.Length>2?args[2].Split(','):new[]{"user","countdown","stop-preparation","direct","unknown","stale","committed","safety-after-click","safety-after-click-nonzero","verified-stop","unverified-stop","safety-proof","legacy-stop","zero-retry-permit","input-stopped","input-stopped-di-error","input-stopped-not-operator","round-stopped","round-stopped-unverified","round-stopped-not-operator","round-stopped-di-error"};
        if(args.Length>2)Console.WriteLine("EXPLICIT_FOCUSED_SCENARIOS="+String.Join(",",scenarios)+"; NOT_FULL_HISTORICAL_SUITE");
        foreach(string s in scenarios)Run(s);
        if(args.Length<=2)AutomaticIntent();
        Console.WriteLine("PRESSURE_PREPARATION_OUTCOME_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0; ProductStarted=false");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
