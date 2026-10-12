using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class PressureInitializationModeHandoffSmoke
{
    static int checks;
    static Type client,bridge;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Field(string name){return client.GetField(name,BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);}
    static void Field(string name,object value){client.GetField(name,BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,value);}
    static int Count(string name){return (int)Field(name);}
    static void PumpUntil(Func<bool> done){var t=System.Diagnostics.Stopwatch.StartNew();while(!done()){Application.DoEvents();Thread.Sleep(1);if(t.ElapsedMilliseconds>6000)throw new TimeoutException("fake handoff timeout");}Application.DoEvents();}
    static void Finish(Task task){PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static bool HandoffQueued(Control page)
    {
        // Observe the actual managed WinForms marshaling queue, without pumping
        // it. This proves cancellation occurs AFTER the host queued its Invoke.
        var field=typeof(Control).GetField("threadCallbackList",BindingFlags.Instance|BindingFlags.NonPublic);
        if(field==null)throw new Exception("WinForms managed callback queue changed; review fixture");
        var queue=field.GetValue(page) as System.Collections.ICollection;if(queue==null)return false;
        lock(queue.SyncRoot)foreach(object entry in queue){
            var method=entry.GetType().GetField("method",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            var callback=method==null?null:method.GetValue(entry) as Delegate;
            if(callback!=null&&callback.Method.Name.Contains("CompleteInitializationInputOwnerAsync"))return true;
        }
        return false;
    }
    static void WaitWithoutPumping(Func<bool> done)
    {var t=System.Diagnostics.Stopwatch.StartNew();while(!done()){Thread.Sleep(1);if(t.ElapsedMilliseconds>6000)throw new TimeoutException("fake UI queue did not receive the production handoff");}}
    public sealed class Page:Control
    {
        long source;
        public int Samples,Resets;
        public bool AdvancedMode {get;set;}
        public double CalculatedTargetPeakVoltageKv {get;set;}=6.2;
        public bool HasActiveHardwareSession {get{return false;}}
        public void ResetP04FeedbackSession(){++source;Resets++;}
        public long BeginP04FeedbackSource(){return ++source;}
        public void ReportP04StatusForSource(long id,bool known,bool high,DateTime utc){if(id==source&&known)Samples++;}
        public void ReportP04Status(bool known,bool high,DateTime utc){throw new Exception("unscoped feedback rejected");}
        public void InterruptPressureContinuation(){}
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[1]));client=assembly.GetType("Xiongxin.FactoryTest.FactoryResidentDeviceClient",true);
            bridge=assembly.GetType("Xiongxin.FactoryTest.UnifiedPressureBridge",true);
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);Field("Root",root);
            var settingsType=assembly.GetType("Xiongxin.ZeroBench.BenchSettings",true);var input=Activator.CreateInstance(settingsType);
            Action<string,object> set=(n,v)=>settingsType.GetField(n).SetValue(input,v);
            set("RealHardware",true);set("ArtSerial","123456789");set("PicoSerial","FAKE/0001");set("CaptureDurationMs",40d);
            set("RequestedPicoRateHz",125000d);set("RangeIndex",5);set("PicoCoupling",0);
            set("ArmDelayMs",500);set("TriggerPulseMs",1500);
            string xml=(string)bridge.GetMethod("Xml").Invoke(null,new[]{input});
            string config=(string)bridge.GetMethod("CreateConfigurationWithoutResidual").Invoke(null,new object[]{xml,11000d,30d,1,false,3d,"DIV"});
            foreach(string mode in new[]{"PLAN","CUSTOM0","CLOSE_FAIL"}){
                bool failure=mode=="CLOSE_FAIL",custom=mode=="CUSTOM0";
                Field("Opens",0);Field("Closes",0);Field("Rounds",0);Field("InputOpens",0);Field("NormalOpens",0);Field("FailClose",false);Field("HoldClose",null);
                using(var form=new Form{ShowInTaskbar=false,Location=new System.Drawing.Point(-20000,-20000)})using(var page=new Page{AdvancedMode=custom,CalculatedTargetPeakVoltageKv=custom?0:6.2}){
                    form.Controls.Add(page);form.Show();var handle=page.Handle;
                    var session=new FactoryPressureSession(page,bridge,config,new AppSettings(),root,new PressureAnalysisTrial(root),()=>{},new Diagnostics(Path.Combine(root,"logs")),true);
                    session.StartMonitor();PumpUntil(()=>Count("Opens")==1&&page.Samples>0);
                    Check(Count("InputOpens")==1&&Count("NormalOpens")==0&&Count("Closes")==0,"initial monitor opens input-only, never normal capability");
                    Finish(session.StopMonitor());
                    string directory=Path.Combine(root,"round-"+mode);
                    Finish((Task)bridge.GetMethod("CaptureManualZeroAsync").Invoke(null,new object[]{config,xml,0d,directory,false,session.CreateScopedProgressSource(true),CancellationToken.None}));
                    Check(Count("Rounds")==1&&Count("Opens")==1&&Count("Closes")==0,"actual bridge zero capture reuses production resident input owner without reopen");
                    session.StartMonitor();PumpUntil(()=>!((Task)typeof(FactoryPressureSession).GetField("monitorTask",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(session)).IsCompleted);
                    Finish(session.StopMonitor());Check(Count("Opens")==1&&Count("Closes")==0,"input monitor resume still uses same owner");
                    if(custom){Finish(session.CompleteInitializationInputOwnerAsync(CancellationToken.None));
                        Check(Count("Opens")==1&&Count("Closes")==0&&Count("NormalOpens")==0,"custom 0 kV keeps exact same input owner without a redundant handoff");
                        Finish(session.CloseAsync());session.Dispose();continue;}
                    var hold=new TaskCompletionSource<bool>();Field("HoldClose",hold);Field("FailClose",failure);
                    var handoff=session.CompleteInitializationInputOwnerAsync(CancellationToken.None);
                    PumpUntil(()=>Count("Closes")==1);session.StartMonitor();Application.DoEvents();
                    Check(!handoff.IsCompleted&&Count("Opens")==1&&Count("NormalOpens")==0,"pending release blocks replacement owner and monitor restart");
                    hold.SetResult(true);
                    if(failure){bool failed=false;try{Finish(handoff);}catch(IOException){failed=true;}
                        Check(failed&&session.MonitorRestartSuppressed,"cleanup failure preserved and restart suppressed");
                        session.StartMonitor();Application.DoEvents();Check(Count("Opens")==1&&Count("NormalOpens")==0&&(bool)bridge.GetProperty("HasDeviceSession").GetValue(null,null),"failed cleanup retains input owner; no replacement or automatic HV");
                    }else {Finish(handoff);PumpUntil(()=>Count("NormalOpens")==1);
                        Check(Count("Opens")==2&&Count("Closes")==1&&Count("InputOpens")==1,"one explicit input-to-normal handoff, not two implicit reopen cycles");
                        Check(Count("Rounds")==1&&page.Resets>=2,"handoff invalidates input feedback and does not start any HV round");}
                    Field("HoldClose",null);Field("FailClose",false);Finish(session.CloseAsync());session.Dispose();
                }
            }
            foreach(string interruption in new[]{"cancel-queued","context-queued","dispose-queued"}){
                Field("Opens",0);Field("Closes",0);Field("Rounds",0);Field("InputOpens",0);Field("NormalOpens",0);Field("FailClose",false);Field("HoldClose",null);
                using(var cancellation=new CancellationTokenSource())
                using(var form=new Form{ShowInTaskbar=false,Location=new System.Drawing.Point(-20000,-20000)})using(var page=new Page()){
                    form.Controls.Add(page);form.Show();var handle=page.Handle;bool contextCurrent=true;
                    var session=new FactoryPressureSession(page,bridge,config,new AppSettings(),root,new PressureAnalysisTrial(root),
                        ()=>{if(!contextCurrent||page.IsDisposed)throw new OperationCanceledException("fake page context retired");},new Diagnostics(Path.Combine(root,"queue-logs")),true);
                    session.StartMonitor();PumpUntil(()=>Count("Opens")==1&&page.Samples>0);Finish(session.StopMonitor());
                    var hold=new TaskCompletionSource<bool>();Field("HoldClose",hold);
                    var handoff=session.CompleteInitializationInputOwnerAsync(cancellation.Token);
                    PumpUntil(()=>Count("Closes")==1);
                    Task.Run(()=>hold.SetResult(true));
                    WaitWithoutPumping(()=>HandoffQueued(page));
                    Check(!handoff.IsCompleted&&Count("NormalOpens")==0,"production UI handoff is actually queued before interruption: "+interruption);
                    if(interruption=="cancel-queued")cancellation.Cancel();
                    else if(interruption=="context-queued")contextCurrent=false;
                    else page.Dispose();
                    bool rejected=false;try{Finish(handoff);}catch(Exception){rejected=true;}
                    Check(rejected&&session.MonitorRestartSuppressed,"late UI interruption fails closed: "+interruption);
                    Check((bool)typeof(FactoryPressureSession).GetField("initializationInputOnly",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(session),"late callback never clears input-only initialization guard: "+interruption);
                    session.StartMonitor();Application.DoEvents();
                    Check(Count("Opens")==1&&Count("NormalOpens")==0&&Count("Rounds")==0,"late callback never opens normal owner or starts HV: "+interruption);
                    Field("HoldClose",null);Finish(session.CloseAsync());session.Dispose();
                }
            }
            Console.WriteLine("PRESSURE_INITIALIZATION_HANDOFF_PASS="+checks+";ProductionBridge=True;ProductionResidentGate=True;ProductionModePolicy=True;InputCaptureSegments=1;ExpectedSuccessfulOpens=2;ExpectedSuccessfulClosesBeforeExit=1;ProductStarted=False;SDKCalled=False;ActualWorkerStarted=False;HardwareAccessed=False;AO_DO=0;PhysicalAcceptance=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
