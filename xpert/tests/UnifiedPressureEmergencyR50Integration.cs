using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Isolated test Main. I/O, UI page and bridge are fake. Actual managed ramp,
// restricted entry adapters, resident protocol, client proof classifier, host
// DI callback and Home navigation/presence logic run from current source.
internal static class UnifiedPressureEmergencyR50Integration
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static object Get(object value,string name){return value.GetType().GetField(name,Hidden).GetValue(value);}
    static void Set(object value,string name,object field){value.GetType().GetField(name,Hidden).SetValue(value,field);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Hidden).Invoke(value,args);}
    static object WorkerCall(Assembly worker,string type,string name,params object[] args)
    {return worker.GetType("Xiongxin.FactoryTest."+type,true).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);}
    public sealed class Page:Control
    {
        long source;
        public int Samples,LowSamples,HighSamples;
        public bool OutputLatched,PhysicalHigh;
        public DateTime P04CapturedUtc{get;private set;}
        bool advancedMode;
        double targetKv=6.2;
        public bool AdvancedMode{get{return advancedMode;}}
        public double CalculatedTargetPeakVoltageKv{get{return targetKv;}}
        public event EventHandler AdvancedModeChanged,VoltageSettingChanged;
        public void SelectDisplayMode(bool advanced,double kv){advancedMode=advanced;targetKv=kv;
            var mode=AdvancedModeChanged;if(mode!=null)mode(this,EventArgs.Empty);
            var voltage=VoltageSettingChanged;if(voltage!=null)voltage(this,EventArgs.Empty);}
        public bool HasActiveHardwareSession{get{return false;}}
        public bool IsBusy{get{return false;}}
        public bool SimulationMode{get{return false;}}
        public bool IsPressureNoiseInitializing{get{return false;}}
        public bool PressureNoiseInitializationReady{get{return false;}}
        public void InterruptPressureContinuation(){}
        public void ResetP04FeedbackSession(){++source;P04CapturedUtc=DateTime.MinValue;}
        public long BeginP04FeedbackSource(){return ++source;}
        public void ReportLatchedP04StatusForSource(long generation,bool high,DateTime utc)
        {
            if(generation!=source||utc<=P04CapturedUtc)return;
            Check(utc.Kind==DateTimeKind.Utc&&utc<=DateTime.UtcNow,"host sees actual UTC sample, not invented permission");
            OutputLatched=true;PhysicalHigh=high;P04CapturedUtc=utc;Samples++;if(high)HighSamples++;else LowSamples++;
        }
        public void ReportP04StatusForSource(long generation,bool known,bool high,DateTime utc)
        {throw new Exception("retained physical level must not be mapped to ordinary HV permission");}
        public void ReportP04Status(bool known,bool high,DateTime utc)
        {throw new Exception("retained source must not use legacy untimed callback");}
    }
    public static class Bridge
    {
        public static int Revokes,Closes,MonitorStarts,ManualMonitorStarts;
        public static volatile Action<string,string> Progress;
        public static bool HasDeviceSession{get{return Progress!=null;}}
        public static TaskCompletionSource<bool> CloseCompletion;
        public static void RevokeBatch(){Revokes++;}
        public static Task CloseSessionAsync(){Closes++;return CloseCompletion==null?Task.CompletedTask:CloseCompletion.Task;}
        public static async Task MonitorAsync(string config,string directory,Action<string,string> progress,CancellationToken token)
        {Interlocked.Increment(ref MonitorStarts);Progress=progress;await Task.Delay(Timeout.Infinite,token);}
        public static async Task MonitorManualZeroAsync(string config,string directory,Action<string,string> progress,CancellationToken token)
        {Interlocked.Increment(ref ManualMonitorStarts);Progress=progress;await Task.Delay(Timeout.Infinite,token);}
    }
    static void PumpUntil(Func<bool> condition)
    {var watch=System.Diagnostics.Stopwatch.StartNew();while(!condition()){Application.DoEvents();if(watch.ElapsedMilliseconds>5000)throw new Exception("fake asynchronous host fixture timed out");Thread.Sleep(1);}Application.DoEvents();}
    [STAThread] static int Main(string[] args)
    { return PressurePageExitManagedTests.Run(args); }

    // Kept as historical assertions for the superseded r50 retain-at-Home
    // policy. The current entry above exercises explicit acknowledged Close.
    static int LegacyRetainedHomeMain(string[] args)
    {
        try{
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            var worker=Assembly.LoadFrom(Path.GetFullPath(args[1]));
            string fixtureRoot=Path.Combine(qa,"resident-fixtures");Directory.CreateDirectory(fixtureRoot);
            worker.GetType("Xiongxin.FactoryTest.FactoryResidentDeviceServerTests",true).GetField("FixtureRoot",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,fixtureRoot);
            var settings=new AppSettings{DataRoot=qa};settings.Hardware.Enabled=true;
            var state=new ApplicationState();
            var ready=new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,
                StatusCode="Ready",CheckedUtc=DateTime.UtcNow,FromLiveIntegrationProcess=true,ConnectionReadbackVerified=true,
                IntegrationRequestMode="--connect",HubMac="020000000050",ControllerSerial="0000000050",AcquisitionSerial="FAKE0/0050",
                ControllerInstanceId=@"USB\OFFLINE_ART\fixture",AcquisitionInstanceId=@"USB\OFFLINE_PICO\fixture"};
            state.SetHardware(ready);int unavailable=0,probes=0,refreshes=0;bool receiptSeen=false,homeSeen=false;DateTime homeUtc=DateTime.MinValue;
            using(var form=new MainForm(settings,state,true))using(var page=new Page{Tag=ModuleHost.PressureTest})
            {
                var handle=page.Handle;var formHandle=form.Handle;
                form.DevicePresentForTests=id=>{probes++;return id==ready.ControllerInstanceId||id==ready.AcquisitionInstanceId;};
                form.RefreshDeviceForTests=(connect,done)=>{refreshes++;throw new Exception("Home must not reconnect after a healthy emergency stop");};
                form.DeviceConnectionNoticeForTests=message=>{throw new Exception("unexpected connection loss: "+message);};
                Call(form,"RememberValidatedDevice",ready);
                var session=new FactoryPressureSession(page,typeof(Bridge),"",settings,qa,new PressureAnalysisTrial(qa),()=>{},new Diagnostics(Path.Combine(qa,"host-logs")));
                session.ConnectionUnavailable=()=>unavailable++;
                var host=(ModuleHost)Get(form,"moduleHost");
                ((Dictionary<Control,FactoryPressureSession>)Get(host,"factoryPressureSessions")).Add(page,session);
                ((Dictionary<string,Control>)Get(host,"controls")).Add(ModuleHost.PressureTest,page);
                string identity=DeviceSetupStatus.ActualIdentity(ready);
                session.EmergencyObserved=(stage,utc)=>host.ObservePressureEmergency(session,identity,ready.CheckedUtc,stage,utc);
                Set(form,"activeModule",page);session.StartMonitor();PumpUntil(()=>Bridge.Progress!=null);
                var progress=Bridge.Progress;
                try{
                    Console.WriteLine(WorkerCall(worker,"FactoryResidentDeviceServerTests","RunUnifiedEmergencyRoundStop",progress,(Action<Exception>)(error=>{
                        Check(ModuleHost.IsVerifiedEmergencyRoundStop(error,worker),"actual client retained receipt reaches exact host emergency contract");
                        Check(!ModuleHost.IsVerifiedFailedRoundRelease(error,worker),"retained receipt is not cleanup-and-reconnect failure");
                        receiptSeen=true;
                    }),(Action)(()=>{
                        Check(receiptSeen&&page.LowSamples==1&&page.HighSamples==1&&page.OutputLatched&&page.PhysicalHigh,
                            "same real fake-DI reader reaches host LOW and later HIGH while output stays latched");
                        Check(unavailable==0&&state.DeviceConnected&&state.Hardware.CheckedUtc==ready.CheckedUtc,
                            "physical latch does not invalidate live identity or forge a new inspection");
                        Call(form,"ShowHome");Application.DoEvents();
                        homeUtc=page.P04CapturedUtc;
                        Check(Get(form,"activeModule")==null&&!page.IsDisposed&&!page.Visible&&host.HasPressureDeviceOwner&&Bridge.Closes==0,
                            "actual Home hides retained page without disposing the original DI/session owner");
                        Call(form,"PollHomeDeviceConnection");
                        Check(state.DeviceConnected&&state.Hardware.StatusCode=="Ready"&&unavailable==0&&refreshes==0&&probes==0,
                            "Home retains connected identity while original DI owns supervision; no competing reconnect/SDK probe");
                        var tiles=(Dictionary<string,ModuleTile>)Get(form,"tiles");
                        Check(tiles[ModuleHost.CalibrationTest].ModeBadge!="仿真"&&tiles[ModuleHost.PressureTest].ModeBadge!="仿真",
                            "both Home module badges remain live, not falsely downgraded simulation");
                        Check(Bridge.Revokes>0,"host revokes future batch despite healthy retained connection");homeSeen=true;
                    }),(Action)(()=>{
                        Application.DoEvents();
                        Check(homeSeen&&page.LowSamples==2&&page.HighSamples==2&&page.P04CapturedUtc>homeUtc&&page.OutputLatched,
                            "Home receives new LOW/HIGH from same sole DI after navigation, not merely a cached Ready value");
                        Check(host.PressureEmergencyCapturedUtc==page.P04CapturedUtc&&host.PressureEmergencyStage=="EMERGENCY_LATCHED_RELEASED",
                            "host home observation keeps the actual fresh sample UTC and retained latch");
                        var caption=(Label)Get(form,"deviceDetail");
                        Check(caption.Text.Contains("DI HIGH")&&caption.Text.Contains("锁存"),"visible Home connection caption reflects new DI HIGH plus separate output latch");
                        Check(state.DeviceConnected&&unavailable==0&&Bridge.Closes==0,"new samples never clear identity or prematurely close healthy owner");
                    })));
                    Check(homeSeen,"full retained-DI to Home scenario executed");
                    page.Visible=true;
                    var reentered=host.ConfigureTask39PressureAsync(page);PumpUntil(()=>reentered.IsCompleted);reentered.GetAwaiter().GetResult();
                    Check(page.OutputLatched&&session.EmergencyOutputLatched&&Bridge.Closes==0&&host.HasPressureDeviceOwner,
                        "same pressure page reentry preserves original latched owner without Close/reconnect or initialization capture");
                    Check(!page.PressureNoiseInitializationReady&&Bridge.Progress!=null,
                        "latched reentry does not bypass the latch with a new noise initialization despite not-ready initialization flag");
                    int normalStarts=Bridge.MonitorStarts,manualStarts=Bridge.ManualMonitorStarts;
                    DateTime heldUtc=page.P04CapturedUtc;var heldProgress=Bridge.Progress;
                    page.SelectDisplayMode(true,0);Application.DoEvents();
                    Check(Bridge.MonitorStarts==normalStarts&&Bridge.ManualMonitorStarts==manualStarts&&Bridge.Closes==0&&
                        Object.ReferenceEquals(heldProgress,Bridge.Progress)&&page.P04CapturedUtc==heldUtc,
                        "latched custom-zero mode event cannot reset feedback or replace/close the native DI owner");
                    Thread.Sleep(1);heldProgress("EMERGENCY_LATCHED_ACTIVE",DateTime.UtcNow.ToString("O"));Application.DoEvents();
                    Check(page.P04CapturedUtc>heldUtc&&page.OutputLatched&&!page.PhysicalHigh&&session.EmergencyOutputLatched,
                        "same held DI reader keeps delivering fresh LOW after latched custom-zero display selection");
                    heldUtc=page.P04CapturedUtc;page.SelectDisplayMode(false,6.2);Application.DoEvents();
                    Check(Bridge.MonitorStarts==normalStarts&&Bridge.ManualMonitorStarts==manualStarts&&Bridge.Closes==0&&
                        Object.ReferenceEquals(heldProgress,Bridge.Progress)&&page.P04CapturedUtc==heldUtc,
                        "switching back to plan does not reopen a fresh owner or clear the native output latch");
                    Thread.Sleep(1);heldProgress("EMERGENCY_LATCHED_RELEASED",DateTime.UtcNow.ToString("O"));Application.DoEvents();
                    Check(page.P04CapturedUtc>heldUtc&&page.OutputLatched&&page.PhysicalHigh&&state.DeviceConnected,
                        "fresh physical HIGH after display changes remains distinct from permission or explicit reconnect");
                    session.CreateProgressSource();DateTime last=host.PressureEmergencyCapturedUtc;
                    progress("EMERGENCY_LATCHED_ACTIVE",DateTime.UtcNow.ToString("O"));Application.DoEvents();
                    Check(host.PressureEmergencyCapturedUtc==last,"callback from retired feedback generation cannot change Home status");
                    Check(host.RetainPressurePageForHome(page)&&!page.Visible&&!page.IsDisposed,
                        "reentered latched page can return Home again without restarting output");
                    Bridge.CloseCompletion=new TaskCompletionSource<bool>();
                    var handoff=host.CloseRetainedPressureSessionsAsync();Application.DoEvents();
                    Check(!handoff.IsCompleted&&host.HasPressureDeviceOwner&&!page.IsDisposed,"explicit mode handoff awaits original owner's true Close; no premature release");
                    Bridge.CloseCompletion.SetResult(true);PumpUntil(()=>handoff.IsCompleted);handoff.GetAwaiter().GetResult();
                    Check(page.IsDisposed&&!host.HasPressureDeviceOwner&&Bridge.Closes==1,"verified handoff closes retained owner exactly once before new mode");
                }finally{session.Dispose();}
            }
            // Settings handoff has an explicit cleanup failure owner. The real
            // MainForm must retain it and must not replace the module host.
            Bridge.Progress=null;Bridge.CloseCompletion=new TaskCompletionSource<bool>();
            using(var form=new MainForm(settings,state,true))using(var page=new Page{Tag=ModuleHost.PressureTest})
            {
                var ignored=form.Handle;ignored=page.Handle;
                var host=(ModuleHost)Get(form,"moduleHost");
                var session=new FactoryPressureSession(page,typeof(Bridge),"",settings,qa,new PressureAnalysisTrial(qa),()=>{},new Diagnostics(Path.Combine(qa,"settings-failure-logs")));
                ((Dictionary<Control,FactoryPressureSession>)Get(host,"factoryPressureSessions")).Add(page,session);
                Set(form,"activeModule",page);session.StartMonitor();PumpUntil(()=>Bridge.Progress!=null);
                var workflow=(Task)Call(form,"FinishSettingsWorkflow",true,settings.Clone());Application.DoEvents();
                Check(!workflow.IsCompleted&&Object.ReferenceEquals(Get(form,"moduleHost"),host),"settings replacement waits until actual retained Close returns");
                Bridge.CloseCompletion.SetException(new IOException("fake original owner cleanup failed"));PumpUntil(()=>workflow.IsCompleted);workflow.GetAwaiter().GetResult();
                Check(Object.ReferenceEquals(Get(form,"moduleHost"),host)&&host.HasPressureDeviceOwner&&!page.IsDisposed,
                    "cleanup failure preserves original host and task owner; no competing replacement");
                Check(state.Hardware.StatusCode=="ResourceCleanupFailed"&&!state.DeviceConnected&&(bool)Get(form,"hardwareManuallyDisconnected"),
                    "cleanup failure remains a specific unverified state and forbids automatic navigation/reconnection");
            }
            // Disposed pages may have left only a retired Close task, not a
            // dictionary entry. That task is still the device owner.
            Bridge.Progress=null;Bridge.CloseCompletion=new TaskCompletionSource<bool>();
            using(var form=new MainForm(settings,state,true))using(var page=new Page{Tag=ModuleHost.PressureTest}){
                var ignored=form.Handle;ignored=page.Handle;
                var host=(ModuleHost)Get(form,"moduleHost");
                var session=new FactoryPressureSession(page,typeof(Bridge),"",settings,qa,new PressureAnalysisTrial(qa),()=>{},new Diagnostics(Path.Combine(qa,"retired-failure-logs")));
                var owned=(Dictionary<Control,FactoryPressureSession>)Get(host,"factoryPressureSessions");owned.Add(page,session);
                session.StartMonitor();PumpUntil(()=>Bridge.Progress!=null);
                long navigation=(long)Get(form,"navigationRequestSequence");int beforeGuardCloses=Bridge.Closes;
                foreach(string guard in new[]{"settingsDialogActive","settingsWorkflowActive","hardwareDisconnectInProgress"}){
                    Set(form,guard,true);
                    Call(form,"OpenModule",ModuleHost.CalibrationTest);Call(form,"ReconnectCurrentDevice");
                    Call(form,"BeginLiveTestNavigation",ModuleHost.CalibrationTest,navigation);
                    Check((long)Get(form,"navigationRequestSequence")==navigation&&owned.Count==1&&Bridge.Closes==beforeGuardCloses&&Object.ReferenceEquals(Get(form,"moduleHost"),host),
                        guard+" blocks old/new navigation and reconnect without new page/session or generation changes");
                    Set(form,guard,false);
                }
                int closesBefore=Bridge.Closes;Call(host,"RetirePressureSession",session);owned.Remove(page);page.Dispose();
                var cleanup=host.CloseRetainedPressureSessionsAsync();Application.DoEvents();
                Check(!cleanup.IsCompleted&&host.HasPressureDeviceOwner&&owned.Count==0,
                    "retired original Close is awaited even after its page/dictionary entry is gone");
                Bridge.CloseCompletion.SetException(new IOException("fake retired owner cleanup failed"));PumpUntil(()=>cleanup.IsCompleted);
                Check(cleanup.IsFaulted&&host.HasPressureDeviceOwner,"retired cleanup failure propagates and retains device ownership");
                var sameFailure=host.CloseRetainedPressureSessionsAsync();PumpUntil(()=>sameFailure.IsCompleted);
                Check(sameFailure.IsFaulted&&host.HasPressureDeviceOwner&&Bridge.Closes==closesBefore+1,
                    "failed retired owner is neither cleared nor disposed/closed a second time");
                Check(((List<Task>)Get(host,"pressureSessionClosures")).Count==1,
                    "failed retired Close remains in the original host for explicit error handling");
            }
            // A real terminal monitor category must invalidate Home readiness,
            // even when navigation retained the healthy sole DI owner earlier.
            Bridge.Progress=null;Bridge.CloseCompletion=null;state.SetHardware(ready);
            File.WriteAllText(Path.Combine(qa,"ITEM.in"),"[ITEM]\n");
            File.WriteAllText(Path.Combine(qa,"CABLE.in"),"offline synthetic fixture\n");
            Set(state,"projectPath",qa);
            using(var form=new MainForm(settings,state,true))using(var page=new Page{Tag=ModuleHost.PressureTest}){
                var ignored=form.Handle;ignored=page.Handle;
                var host=(ModuleHost)Get(form,"moduleHost");
                var session=new FactoryPressureSession(page,typeof(Bridge),"",settings,qa,new PressureAnalysisTrial(qa),()=>{},new Diagnostics(Path.Combine(qa,"home-terminal-failure-logs")));
                ((Dictionary<Control,FactoryPressureSession>)Get(host,"factoryPressureSessions")).Add(page,session);
                string identity=DeviceSetupStatus.ActualIdentity(ready);
                session.ConnectionUnavailable=()=>host.MarkPressureConnectionUnavailable(page,session,identity,ready.CheckedUtc);
                session.EmergencyObserved=(stage,utc)=>host.ObservePressureEmergency(session,identity,ready.CheckedUtc,stage,utc);
                Set(form,"activeModule",page);session.StartMonitor();PumpUntil(()=>Bridge.Progress!=null);
                Check(state.ProjectPath==qa&&identity.Length>0,"terminal fixture retains a valid synthetic project and fully bound fake identity");
                var progress=Bridge.Progress;progress("EMERGENCY_LATCHED_ACTIVE",DateTime.UtcNow.ToString("O"));Application.DoEvents();
                Call(form,"ShowHome");Check(!page.IsDisposed&&host.HasPressureDeviceOwner&&state.DeviceConnected,
                    "terminal-failure scenario first retains a genuinely identified session at Home");
                progress("PRESSURE_SESSION_UNAVAILABLE","injected terminal communication error");PumpUntil(()=>!state.DeviceConnected);
                Check(state.Hardware.StatusCode=="PresenceUnknown"&&host.HasPressureDeviceOwner,
                    "terminal DI/transport category invalidates readiness at Home without inventing cleanup or releasing owner");
                progress("EMERGENCY_LATCHED_RELEASED",DateTime.UtcNow.ToString("O"));Application.DoEvents();
                Check(!state.DeviceConnected&&page.OutputLatched,"later display-only HIGH cannot swallow the real communication failure or clear output latch");
                var closing=host.CloseRetainedPressureSessionsAsync();PumpUntil(()=>closing.IsCompleted);closing.GetAwaiter().GetResult();
                Check(!host.HasPressureDeviceOwner&&page.IsDisposed,"explicit verified Close remains required after actual terminal category");
            }
            Console.WriteLine(WorkerCall(worker,"FactoryResidentDeviceServerTests","RunUnifiedIdleEmergency"));
            Console.WriteLine(WorkerCall(worker,"FactoryResidentDeviceServerTests","RunEmergencyRoundStop"));
            Console.WriteLine(WorkerCall(worker,"FactoryResidentDeviceServerTests","RunHealthyRoundStop"));
            Console.WriteLine(WorkerCall(worker,"FactoryEmergencyDisplayTests","Run"));
            Console.WriteLine(WorkerCall(worker,"FactoryBufferEmergencyCurrentTests","Run"));
            Console.WriteLine("DI timing/deadline policy: "+WorkerCall(worker,"FactoryEmergencyTimingContractTests","Run")+" managed checks PASS");
            Console.WriteLine("R50_HOST_HOME_INTEGRATION_PASS="+checks+";ProductMain=False;SDKCalled=False;HardwareAccessed=False;PhysicalAcceptance=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
