using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Explicit offline test entry: no product Main, native SDK or hardware worker.
internal static class PressurePageExitManagedTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
    static object Get(object owner,string name){return owner.GetType().GetField(name,Hidden).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Hidden).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args);}
    static object Worker(Assembly a,string type,string method,params object[] args)
    {return a.GetType("Xiongxin.FactoryTest."+type,true).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);}
    static void PumpUntil(Func<bool> condition)
    {var watch=System.Diagnostics.Stopwatch.StartNew();while(!condition()){Application.DoEvents();if(watch.ElapsedMilliseconds>8000)throw new TimeoutException("managed fixture timeout");Thread.Sleep(1);}Application.DoEvents();}
    static void Pump(){for(int n=0;n<10;n++){Application.DoEvents();Thread.Sleep(1);}}
    public sealed class Page:Control
    {
        long source;
        public bool Hardware,Busy,Pending;
        public int Stops,LowSamples,HighSamples;
        public bool OutputLatched,PhysicalHigh;
        public DateTime P04CapturedUtc{get;private set;}
        public bool HasActiveHardwareSession{get{return Hardware;}}
        public bool IsBusy{get{return Busy;}}
        public bool ActionPending{get{return Pending;}}
        public bool SimulationMode{get{return false;}}
        public bool AdvancedMode{get{return false;}}
        public bool IsPressureNoiseInitializing{get{return false;}}
        public double CalculatedTargetPeakVoltageKv{get{return 6.2;}}
        public bool RequestCurrentPressureStop(){Stops++;Hardware=false;return true;}
        public bool RequestFinishCurrentRound(){Stops++;return true;}
        public bool RequestHardwareStop(){Stops++;Hardware=false;return true;}
        public void InterruptPressureContinuation(){Stops++;}
        public void ResetP04FeedbackSession(){source++;P04CapturedUtc=DateTime.MinValue;}
        public long BeginP04FeedbackSource(){return ++source;}
        public void ReportLatchedP04StatusForSource(long generation,bool high,DateTime utc)
        {if(generation!=source||utc<=P04CapturedUtc)return;Check(utc.Kind==DateTimeKind.Utc,"real callback UTC retained");OutputLatched=true;PhysicalHigh=high;P04CapturedUtc=utc;if(high)HighSamples++;else LowSamples++;}
        public void ReportP04StatusForSource(long generation,bool known,bool high,DateTime utc)
        {if(generation!=source||utc<=P04CapturedUtc)return;PhysicalHigh=high;P04CapturedUtc=utc;if(high)HighSamples++;else LowSamples++;}
        public void ReportP04Status(bool known,bool high,DateTime utc){throw new Exception("legacy DI callback forbidden");}
    }
    public static class Bridge
    {
        public static int Closes,Starts,Revokes;
        public static volatile Action<string,string> Progress;
        public static TaskCompletionSource<bool> CloseCompletion;
        public static bool HasDeviceSession{get{return Progress!=null;}}
        public static void RevokeBatch(){Revokes++;}
        public static Task CloseSessionAsync(){Interlocked.Increment(ref Closes);return CloseCompletion==null?Task.CompletedTask:CloseCompletion.Task;}
        public static Task MonitorManualZeroAsync(string c,string d,Action<string,string> p,CancellationToken t){return MonitorAsync(c,d,p,t);}
        public static async Task MonitorAsync(string c,string d,Action<string,string> p,CancellationToken t)
        {Interlocked.Increment(ref Starts);Progress=p;await Task.Delay(Timeout.Infinite,t);}
        public static void Reset(){Closes=Starts=Revokes=0;Progress=null;CloseCompletion=null;}
    }
    static HardwareStatusSnapshot Ready()
    {return new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",CheckedUtc=DateTime.UtcNow,
        FromLiveIntegrationProcess=true,ConnectionReadbackVerified=true,IntegrationRequestMode="--connect",HubMac="020000000050",ControllerSerial="0000000050",AcquisitionSerial="FAKE0/0050",
        ControllerInstanceId=@"USB\OFFLINE_ART\fixture",AcquisitionInstanceId=@"USB\OFFLINE_PICO\fixture"};}
    sealed class Fixture:IDisposable
    {
        internal readonly MainForm Form;
        internal readonly Page Page;
        internal readonly ModuleHost Host;
        internal readonly FactoryPressureSession Session;
        internal readonly ApplicationState State=new ApplicationState();
        internal readonly AppSettings Settings;
        internal int Notices,Probes,Refreshes;
        internal string LastNotice;
        internal Fixture(string root)
        {
            Bridge.Reset();Settings=new AppSettings{DataRoot=root};Settings.Hardware.Enabled=true;State.SetHardware(Ready());
            Form=new MainForm(Settings,State,true);Page=new Page{Tag=ModuleHost.PressureTest};var ignored=Form.Handle;ignored=Page.Handle;
            Form.DevicePresentForTests=id=>{Probes++;return true;};Form.RefreshDeviceForTests=(connect,done)=>{Refreshes++;throw new Exception("unexpected SDK refresh");};
            Form.DeviceConnectionNoticeForTests=message=>{Notices++;LastNotice=message;};Call(Form,"RememberValidatedDevice",State.Hardware);
            Host=(ModuleHost)Get(Form,"moduleHost");
            Session=new FactoryPressureSession(Page,typeof(Bridge),"",Settings,root,new PressureAnalysisTrial(root),()=>{},new Diagnostics(Path.Combine(root,"logs")));
            ((Dictionary<Control,FactoryPressureSession>)Get(Host,"factoryPressureSessions")).Add(Page,Session);
            ((Dictionary<string,Control>)Get(Host,"controls")).Add(ModuleHost.PressureTest,Page);
            Set(Form,"activeModule",Page);Session.StartMonitor();PumpUntil(()=>Bridge.Progress!=null);
        }
        public void Dispose(){Session.Dispose();Form.Dispose();Page.Dispose();}
    }
    internal static int Run(string[] args)
    {
        try{
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            var worker=Assembly.LoadFrom(Path.GetFullPath(args[1]));
            worker.GetType("Xiongxin.FactoryTest.FactoryResidentDeviceServerTests",true).GetField("FixtureRoot",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,root);
            using(var f=new Fixture(Path.Combine(root,"success"))){
                bool receipt=false,pageRetained=false;
                Console.WriteLine(Worker(worker,"FactoryResidentDeviceServerTests","RunUnifiedEmergencyRoundStop",Bridge.Progress,(Action<Exception>)(error=>{
                    Check(ModuleHost.IsVerifiedEmergencyRoundStop(error,worker),"bound emergency stop receipt remains distinct from disconnect");receipt=true;
                }),(Action)(()=>{
                    Check(receipt&&f.Page.OutputLatched&&f.Page.LowSamples==1&&f.Page.HighSamples==1,"HV UP physical LOW reaches host latch and later actual HIGH");
                    Check(f.State.DeviceConnected&&!f.Page.IsDisposed&&f.Page.Visible&&Bridge.Closes==0&&Bridge.Starts==1,"in-page ordinary LOW retains sole DI and identity; no Close or restart");pageRetained=true;
                }),(Action)(()=>{
                    Check(pageRetained&&f.Page.LowSamples==2&&f.Page.HighSamples==2&&f.Page.OutputLatched,"same unique DI continues LOW/HIGH in page after HV OFF");
                    Check(f.State.DeviceConnected&&Bridge.Closes==0&&Bridge.Starts==1,"new HIGH cannot reopen owner or clear output latch");
                })));
                var preparation=new TaskCompletionSource<bool>();var phasePreparation=new TaskCompletionSource<bool>();var settled=new TaskCompletionSource<bool>();
                Set(f.Form,"pressureInputPreparationTask",preparation.Task);
                ((Dictionary<Control,List<Task>>)Get(f.Host,"pressurePagePreparations")).Add(f.Page,new List<Task>{phasePreparation.Task});
                f.Page.Hardware=f.Page.Busy=f.Page.Pending=true;Bridge.CloseCompletion=new TaskCompletionSource<bool>();
                Task<bool> refused=f.Form.ShowHomeAsync();
                Check(refused.IsCompleted&&!refused.Result&&f.Page.Stops==0&&f.Page.Hardware&&f.Page.Visible&&Bridge.Closes==0,
                    "Home during active pressure cannot issue implicit Stop, hide the page or close the owner");
                f.Page.RequestCurrentPressureStop();
                refused=f.Form.ShowHomeAsync();
                Check(refused.IsCompleted&&!refused.Result&&f.Page.Stops==1&&Bridge.Closes==0,
                    "Home remains blocked until capture/save/mailbox acknowledgements end after explicit Stop");
                f.Page.Busy=f.Page.Pending=false;
                int delays=0;f.Form.PressureExitDelayForTests=ms=>{delays++;Check(ms==50&&Bridge.Closes==1&&!f.Host.HasPressureDeviceOwner&&f.Page.IsDisposed,"50ms starts only after true Close and page disposal");return settled.Task;};
                Task<bool> exit=f.Form.ShowHomeAsync();Task<bool> duplicate=f.Form.ShowHomeAsync();Pump();
                Check(Object.ReferenceEquals(exit,duplicate)&&f.Page.Stops>=3&&!f.Page.Hardware&&!exit.IsCompleted&&Bridge.Closes==0,"idle exit retires current/future tasks once and joins current preparation");
                Call(f.Form,"PollHomeDeviceConnection");Check(f.Probes==0&&f.Refreshes==0,"no home connection monitoring while initialization or stop pending");
                preparation.SetResult(true);Pump();Check(Bridge.Closes==0,"phase-change preparation also joined before Close");
                phasePreparation.SetResult(true);PumpUntil(()=>Bridge.Closes==1);
                Check(!exit.IsCompleted&&f.Host.HasPressureDeviceOwner&&!f.Page.IsDisposed&&delays==0,"pending original Close retains owner; no premature delay or disposal");
                Call(f.Form,"OpenModule",ModuleHost.ProjectCreation);Call(f.Form,"ReconnectCurrentDevice");
                Check(Object.ReferenceEquals(Get(f.Form,"activeModule"),f.Page)&&f.Refreshes==0,"concurrent navigation/reconnect blocked until original release");
                Bridge.CloseCompletion.SetResult(true);PumpUntil(()=>delays==1);
                Check(!exit.IsCompleted&&Object.ReferenceEquals(Get(f.Form,"activeModule"),f.Page),"Home waits for explicit post-close settle interval");
                Call(f.Form,"PollHomeDeviceConnection");Check(f.Probes==0,"home polling still suppressed during post-close interval");
                settled.SetResult(true);PumpUntil(()=>exit.IsCompleted);Check(exit.Result&&Get(f.Form,"activeModule")==null&&!f.Host.HasPressureDeviceOwner,"Home displayed only after joined tasks/Close/50ms");
                Call(f.Form,"PollHomeDeviceConnection");Check(f.Probes==2&&f.Refreshes==0&&f.State.DeviceConnected,"post-exit home resumes only passive exact-node presence monitoring");
                int starts=Bridge.Starts;f.Session.StartMonitor();Pump();Check(Bridge.Starts==starts,"retired page cannot resurrect its monitor");
            }
            using(var f=new Fixture(Path.Combine(root,"stop-unconfirmed-navigation"))){
                var uncertain=f.State.Hardware.Clone();uncertain.Connected=false;uncertain.StatusCode="StopReleaseUnconfirmed";
                f.State.SetHardware(uncertain,false);
                Task<bool> exit=f.Form.ShowHomeAsync();
                Check(exit.IsCompleted&&!exit.Result&&f.Page.Stops==0&&Bridge.Closes==0&&!f.Page.IsDisposed&&f.Page.Visible,
                    "unconfirmed Stop retains the original visible owner and cannot navigate or attempt another cleanup");
                Check(f.LastNotice.Contains("停止")&&!f.LastNotice.Contains("断线"),"Stop evidence warning does not assert a physical disconnect");
                Call(f.Form,"UpdateDeviceStatusActions");
                var lamp=(Panel)Get(f.Form,"deviceDot");var detail=(Label)Get(f.Form,"deviceDetail");
                Check(lamp.BackColor==AppTheme.Warning&&lamp.AccessibleName.Contains("停止")&&!lamp.AccessibleName.Contains("未连接"),
                    "unconfirmed Stop has an amber evidence status, not a disconnected lamp");
                Check(detail.Text.Contains("加压已禁用")&&!f.State.DeviceConnected,
                    "accurate presentation does not restore readiness or voltage permission");
                Check(!((Button)Get(f.Form,"disconnectDeviceButton")).Enabled,"retained owner blocks competing Home device actions");
                Set(f.Form,"activeModule",null);Call(f.Form,"UpdateDeviceStatusActions");
                Check(!((Button)Get(f.Form,"disconnectDeviceButton")).Enabled,"hidden retained owner also blocks reconnect/disconnect actions");
                Set(f.Form,"activeModule",f.Page);
                foreach(string code in new[]{"ResourceCleanupFailed","SessionEndedAfterFailedRelease","WorkerSessionExited"}){
                    uncertain=f.State.Hardware.Clone();uncertain.StatusCode=code;f.State.SetHardware(uncertain,false);Call(f.Form,"UpdateDeviceStatusActions");
                    Check(lamp.BackColor==AppTheme.Warning&&!lamp.AccessibleName.Contains("未连接"),"control/cleanup state stays distinct from disconnected: "+code);
                }
                Set(f.Form,"hardwareManuallyDisconnected",true);uncertain=f.State.Hardware.Clone();uncertain.StatusCode="ResourceCleanupFailed";
                f.State.SetHardware(uncertain,false);Call(f.Form,"UpdateDeviceStatusActions");
                Check(lamp.BackColor==AppTheme.Warning&&lamp.AccessibleName.Contains("收尾"),"a paused reconnect flag cannot relabel failed cleanup as manual disconnect");
                Set(f.Form,"hardwareManuallyDisconnected",false);
                uncertain=f.State.Hardware.Clone();uncertain.StatusCode="Offline";f.State.SetHardware(uncertain,false);Call(f.Form,"UpdateDeviceStatusActions");
                Check(lamp.AccessibleName.Contains("未连接"),"actual disconnected state keeps its original meaning");
            }
            using(var f=new Fixture(Path.Combine(root,"close-failure"))){
                Bridge.CloseCompletion=new TaskCompletionSource<bool>();int delays=0;f.Form.PressureExitDelayForTests=ms=>{delays++;return Task.CompletedTask;};
                var exit=f.Form.ShowHomeAsync();PumpUntil(()=>Bridge.Closes==1);Bridge.CloseCompletion.SetException(new IOException("injected actual Close failure"));PumpUntil(()=>exit.IsCompleted);
                Check(!exit.Result&&f.Host.HasPressureDeviceOwner&&!f.Page.IsDisposed&&Object.ReferenceEquals(Get(f.Form,"activeModule"),f.Page),"failed Close retains original page/session ownership");
                Check(delays==0,"failed Close cannot substitute a settle delay for release success");
                Check(f.Notices==1,"failed Close gives exactly one visible notice");
                Check(f.LastNotice=="停止尚未确认，请保持停止并检查设备。","failed Close uses the current concise customer action without exposing cleanup internals");
                Call(f.Form,"PollHomeDeviceConnection");Check(f.Probes==0&&f.Refreshes==0,"failed release never resumes polling or automatically reconnects");
                var retry=f.Form.ShowHomeAsync();PumpUntil(()=>retry.IsCompleted);Check(!retry.Result&&Bridge.Closes==1&&f.Host.HasPressureDeviceOwner,"repeat exit retains same failure, never opens or recloses an uncertain owner");
            }
            using(var f=new Fixture(Path.Combine(root,"retired-failure"))){
                Bridge.CloseCompletion=new TaskCompletionSource<bool>();Call(f.Host,"RetirePressureSession",f.Session);
                ((Dictionary<Control,FactoryPressureSession>)Get(f.Host,"factoryPressureSessions")).Remove(f.Page);
                f.Page.Dispose();Set(f.Form,"activeModule",null);
                var exit=f.Form.ShowHomeAsync();PumpUntil(()=>Bridge.Closes==1);Bridge.CloseCompletion.SetException(new IOException("injected retired owner failure"));PumpUntil(()=>exit.IsCompleted);
                Check(!exit.Result&&f.Host.HasPressureDeviceOwner&&((List<Task>)Get(f.Host,"pressureSessionClosures")).Count==1,"retired failed Close cannot be lost when page is already gone");
                Call(f.Form,"PollHomeDeviceConnection");Check(f.Probes==0&&f.Refreshes==0,"retired pending/failed ownership blocks competing monitoring");
            }
            using(var f=new Fixture(Path.Combine(root,"duplicate-notice"))){
                var preparing=new TaskCompletionSource<bool>();f.Form.PressureInputPreparationForTests=page=>preparing.Task;
                Call(f.Form,"ConfigurePressureInputPage",f.Page);
                var failed=f.State.Hardware.Clone();failed.Connected=false;failed.StatusCode="PresenceUnknown";
                f.State.SetHardware(failed,false);Call(f.Form,"HandleLiveConnectionLoss",f.Page);Check(f.Notices==0,"initialization state failure defers duplicate connection-loss modal to its owner");
                preparing.SetException(new InvalidOperationException("injected acquisition setup failure"));
                PumpUntil(()=>((Task)Get(f.Form,"pressureInputPreparationTask")).IsCompleted);
                Check(f.Notices==1&&!String.IsNullOrWhiteSpace(f.LastNotice),"awaited initialization failure is presented once; detailed exception remains in diagnostics");
                Call(f.Form,"HandleLiveConnectionLoss",f.Page);Check(f.Notices==1,"reported initialization failure does not queue a second modal");
            }
            using(var f=new Fixture(Path.Combine(root,"closing-navigation"))){
                Bridge.CloseCompletion=new TaskCompletionSource<bool>();f.Form.PressureExitDelayForTests=ms=>Task.CompletedTask;
                var closing=new FormClosingEventArgs(CloseReason.UserClosing,false);
                Call(f.Form,"MainFormClosing",f.Form,closing);Check(closing.Cancel,"window close cannot dispose an unclosed pressure owner");
                PumpUntil(()=>Bridge.Closes==1);Check(!f.Page.IsDisposed&&f.Host.HasPressureDeviceOwner,"closing joins same actual Close task before releasing the page");
                Bridge.CloseCompletion.SetResult(true);Task<bool> exit=(Task<bool>)Get(f.Form,"pressureHomeExitTask");PumpUntil(()=>exit.IsCompleted);
                Check(exit.Result&&f.Page.IsDisposed&&Get(f.Form,"activeModule")==null,"user window-close navigates Home only after successful release");
            }
            using(var f=new Fixture(Path.Combine(root,"late-prepare"))){
                Bridge.CloseCompletion=new TaskCompletionSource<bool>();f.Form.PressureExitDelayForTests=ms=>Task.CompletedTask;
                Task preparing=f.Host.ConfigureTask39PressureAsync(f.Page);PumpUntil(()=>Bridge.Closes==1);
                Check(!preparing.IsCompleted,"phase reconfiguration is waiting for its original owner Close");
                Task<bool> exit=f.Form.ShowHomeAsync();Bridge.CloseCompletion.SetResult(true);PumpUntil(()=>exit.IsCompleted);
                Check(exit.Result&&preparing.IsCompleted&&!preparing.IsFaulted&&Bridge.Closes==1&&Bridge.Starts==1,
                    "exit during phase-owner handoff prevents late replacement initialization or monitor creation");
            }
            Console.WriteLine(Worker(worker,"FactoryResidentDeviceServerTests","RunUnifiedIdleEmergency"));
            Console.WriteLine(Worker(worker,"FactoryResidentDeviceServerTests","RunEmergencyRoundStop"));
            Console.WriteLine(Worker(worker,"FactoryResidentDeviceServerTests","RunHealthyRoundStop"));
            Console.WriteLine(Worker(worker,"FactoryEmergencyDisplayTests","Run"));
            Console.WriteLine(Worker(worker,"FactoryBufferEmergencyCurrentTests","Run"));
            Console.WriteLine("DI timing/deadline: "+Worker(worker,"FactoryEmergencyTimingContractTests","Run"));
            Console.WriteLine("PRESSURE_PAGE_EXIT_MANAGED_PASS="+checks+";ProductMain=False;WorkerMain=False;SDKCalled=False;HardwareAccessed=False;PhysicalAcceptance=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
