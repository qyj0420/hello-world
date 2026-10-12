using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class HomeDeviceConnectionSmoke
{
    static int checks;
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Field(object obj, string name) { return obj.GetType().GetField(name, Hidden).GetValue(obj); }
    static void Set(object obj, string name, object value) { obj.GetType().GetField(name, Hidden).SetValue(obj, value); }
    static object Call(object obj, string name, params object[] values) { return obj.GetType().GetMethod(name, Hidden).Invoke(obj, values); }
    static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    sealed class LivePage : UserControl
    {
        public int Stops; public bool SimulationMode { get; set; }
        public bool RequestHardwareStop() { Stops++; return true; }
    }
    static HardwareStatusSnapshot Ready() { return new HardwareStatusSnapshot { HardwareEnabled = true, DriverInstalled = true, Connected = true, IdentityValid = true, StatusCode = "Ready", CheckedUtc = DateTime.UtcNow }; }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var connected=Ready();connected.ControllerSerial="ART-fixture";connected.AcquisitionSerial="PICO-fixture";
            connected.FromLiveIntegrationProcess=true;connected.ConnectionReadbackVerified=true;connected.IntegrationRequestMode="--connect";
            var detached=MainForm.LocalCommunicationDisconnect(connected);
            Check(connected.Ready&&connected.FromLiveIntegrationProcess,"local detach never mutates an in-flight old snapshot");
            Check(!detached.Ready&&!detached.Connected&&!detached.IdentityValid&&detached.StatusCode=="Disconnected","local detach withdraws permission");
            Check(!detached.HasLiveIdentityReadback&&!detached.ConnectionReadbackVerified&&detached.IntegrationRequestMode=="","no old successful readback survives local detach");
            Check(detached.ControllerSerial==connected.ControllerSerial&&detached.AcquisitionSerial==connected.AcquisitionSerial,"identity retained without changing vendor binding");
            Check(detached.Message.Contains("不代表")&&detached.Diagnostic.Contains("no DS600"),"disconnect does not assert electrical safety or DS600 unbinding");
            string qa = Path.Combine(Path.GetFullPath(args[0]), "connection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            var settings = new AppSettings { DataRoot = qa }; settings.Hardware.Enabled = true;
            var state = new ApplicationState(); state.SetHardware(Ready());
            using (var form = new MainForm(settings, state, true))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show(); Application.DoEvents();
                Check(((Timer)Field(form, "devicePollTimer")).Interval == 1000, "one-second timer cadence");
                Check((bool)Call(form, "CanPollHomeDevice"), "idle home can inspect");
                var closures=(List<Task>)Field(Field(form,"moduleHost"),"pressureSessionClosures");
                var closing=new TaskCompletionSource<bool>();closures.Add(closing.Task);
                int overlappingInspections=0;form.RefreshDeviceForTests=(connect,done)=>{overlappingInspections++;return false;};
                Check(!(bool)Call(form,"CanPollHomeDevice"),"pending device cleanup blocks home polling after page exit");
                Call(form,"BeginHardwareRefresh",true,true,null);
                Check(overlappingInspections==0&&!form.TryDisconnectCurrentDevice().Result,"pending cleanup blocks explicit connect and disconnect too");
                closing.SetResult(true);Check((bool)Call(form,"CanPollHomeDevice"),"successful cleanup releases home polling ownership");
                var failedClose=new TaskCompletionSource<bool>();failedClose.SetException(new IOException("synthetic cleanup failure"));closures.Add(failedClose.Task);
                Check(!(bool)Call(form,"CanPollHomeDevice"),"failed cleanup does not release ownership");closures.Clear();form.RefreshDeviceForTests=null;
                foreach (string flag in new[] { "settingsDialogActive", "hardwareRefreshInProgress", "hardwareDisconnectInProgress", "hardwareManuallyDisconnected" })
                { Set(form, flag, true); Check(!(bool)Call(form, "CanPollHomeDevice"), "poll gate: " + flag); Set(form, flag, false); }
                settings.Hardware.Enabled = false; Check(!(bool)Call(form, "CanPollHomeDevice"), "offline does not inspect"); settings.Hardware.Enabled = true;
                int notifications = 0, layouts = 0; state.Changed += delegate { notifications++; }; form.Layout += delegate { layouts++; };
                for (int i = 0; i < 30; i++) Call(form, "PublishHardwareInspection", Ready(), true);
                Check(notifications == 0 && layouts == 0, "unchanged heartbeat neither broadcasts nor lays out");
                var tiles = (Dictionary<string, ModuleTile>)Field(form, "tiles");
                Check(tiles["CalibrationTest"].ModeBadge == "HV DAC", "connected calibration identifies HV DAC");
                var refreshModes = new List<bool>(); var callbacks = new List<Action<HardwareStatusSnapshot>>();
                var entries = new List<bool>(); form.CalibrationNavigationForTests = entries.Add;
                form.RefreshDeviceForTests = (connect, done) => { refreshModes.Add(connect); callbacks.Add(done); return true; };
                Call(form, "BeginHardwareRefresh", false, false, null);
                Check(refreshModes.Count == 1 && !refreshModes[0], "home heartbeat is inspect-only");
                Call(form, "ReconnectCurrentDevice");
                Check(refreshModes.Count == 1 && (bool)Field(form, "hardwareConnectionQueued"), "explicit connect during inspect is queued, not lost or concurrent");
                long navigation = (long)Field(form, "navigationRequestSequence");
                Call(form, "BeginLiveCalibrationNavigation", navigation);
                Check(entries.Count == 0 && refreshModes.Count == 1, "live calibration waits for in-flight inspection");
                callbacks[0](Ready());
                Check(entries.Count == 0 && (bool)Field(form, "hardwareConnectionQueued"), "inspect-only completion does not consume pending connect or admit page early");
                Call(form, "BeginHardwareRefresh", false, false, null);
                Check(refreshModes.Count == 2 && refreshModes[1] && !(bool)Field(form, "hardwareConnectionQueued"), "next idle request consumes connect once");
                callbacks[1](Ready());
                Check(entries.Count == 1 && !entries[0] && !(bool)Field(form, "hardwareRefreshInProgress"), "latest ready completion admits live page only after check ends");
                Call(form,"BeginLiveCalibrationNavigation",navigation);
                Check(entries.Count==2&&!entries[1]&&refreshModes.Count==2,"recent completed check reused without duplicate hardware inspection");
                entries.RemoveAt(1);
                var staleHardware = state.Hardware; staleHardware.CheckedUtc = DateTime.UtcNow.AddSeconds(-11); state.SetHardware(staleHardware);
                Call(form, "BeginLiveCalibrationNavigation", navigation);
                Check(callbacks.Count == 3, "expired completed check requests a fresh inspection");
                Set(form, "navigationRequestSequence", navigation + 1);
                callbacks[2](Ready());
                Check(entries.Count == 1, "superseded navigation cannot open a late live page");
                staleHardware = state.Hardware; staleHardware.CheckedUtc = DateTime.UtcNow.AddSeconds(-11); state.SetHardware(staleHardware);
                Call(form, "BeginLiveCalibrationNavigation", navigation + 1);
                Check(callbacks.Count == 4, "new navigation with expired status requests inspection");
                callbacks[3](new HardwareStatusSnapshot { HardwareEnabled = true, StatusCode = "Disconnected" });
                Check(entries.Count == 2 && entries[1], "new failed check yields explicit simulation path instead of cached Ready");
                var identified=Ready(); identified.FromLiveIntegrationProcess=true;
                identified.HubMac="001122334455"; identified.ControllerSerial="ART-TEST"; identified.AcquisitionSerial="PICO-TEST";
                identified.ControllerInstanceId=@"USB\TEST_ART\ART-TEST"; identified.AcquisitionInstanceId=@"USB\TEST_PICO\PICO-TEST";
                identified.CheckedUtc=DateTime.UtcNow.AddHours(-2);
                state.SetHardware(identified); Call(form,"RememberValidatedDevice",identified);
                var probes=new List<string>();form.DevicePresentForTests=id=>{probes.Add(id);return true;};
                int entryBefore=entries.Count,refreshBefore=refreshModes.Count;
                ((Timer)Field(form,"devicePollTimer")).Start();
                var watch=System.Diagnostics.Stopwatch.StartNew();
                for(int i=0;i<20;i++)Call(form,"BeginLiveCalibrationNavigation",navigation+1);
                watch.Stop();
                Check(entries.Count==entryBefore+20&&entries.Skip(entryBefore).All(x=>!x),"identified still-present connection directly opens live calibration after hours");
                Check(refreshModes.Count==refreshBefore,"repeated navigation starts no connector or SDK inspection");
                Check(probes.Count==40&&probes.All(id=>id==identified.ControllerInstanceId||id==identified.AcquisitionInstanceId),"only two exact device instances are queried");
                Check(state.Hardware.CheckedUtc==identified.CheckedUtc,"presence check does not forge a new safety inspection timestamp");
                Check(((Timer)Field(form,"devicePollTimer")).Enabled,"navigation retains home monitoring timer");
                for(int i=0;i<30;i++)Call(form,"PollHomeDeviceConnection");
                Check(refreshModes.Count==refreshBefore&&probes.Count==100,"stable home polls do not repeat full inspection");
                using(var page=new LivePage()) {
                    Set(form,"activeModule",page);Call(form,"PollHomeDeviceConnection");
                    Check(probes.Count==100&&refreshModes.Count==refreshBefore,"presence polling is suspended inside module");
                    Set(form,"activeModule",null);Call(form,"PollHomeDeviceConnection");
                    Check(probes.Count==102,"presence polling resumes on return home");
                }
                string originalLocation=settings.Hardware.ControllerLocation;
                settings.Hardware.ControllerLocation="changed";
                Check(!(bool)Call(form,"TryReuseValidatedDevice"),"configuration change invalidates session");
                settings.Hardware.ControllerLocation=originalLocation;
                Check(!(bool)Call(form,"TryReuseValidatedDevice"),"reverting configuration cannot resurrect invalidated identity");
                Call(form,"RememberValidatedDevice",identified);
                var different=identified.Clone();different.AcquisitionSerial="OTHER";state.SetHardware(different);
                Check(!(bool)Call(form,"TryReuseValidatedDevice"),"changed serial is never reused");
                state.SetHardware(identified);Call(form,"RememberValidatedDevice",identified);
                form.DevicePresentForTests=id=>id!=identified.AcquisitionInstanceId;
                Check(!(bool)Call(form,"TryReuseValidatedDevice")&&!state.DeviceConnected,"removed exact acquisition node invalidates connection");
                state.SetHardware(identified);Call(form,"RememberValidatedDevice",identified);
                form.DevicePresentForTests=id=>{throw new InvalidOperationException("query failed");};
                Check(!(bool)Call(form,"TryReuseValidatedDevice"),"presence query error cannot authorize reuse");
                var legacy=identified.Clone();legacy.FromLiveIntegrationProcess=false;
                Call(form,"RememberValidatedDevice",legacy);form.DevicePresentForTests=id=>true;
                Check(!(bool)Call(form,"TryReuseValidatedDevice"),"status-file provenance cannot create persistent session");
                var emptyIdentity=identified.Clone();emptyIdentity.AcquisitionInstanceId="";
                Call(form,"RememberValidatedDevice",emptyIdentity);
                Check(!(bool)Call(form,"TryReuseValidatedDevice"),"missing instance identity cannot create persistent session");
                form.DevicePresentForTests=null;
                Console.WriteLine("IDENTIFIED_NAVIGATION_20_CALLS_MS="+watch.ElapsedMilliseconds+"; REAL_DEVICE_PROBES=0");
                form.RefreshDeviceForTests = null; form.CalibrationNavigationForTests = null; state.SetHardware(Ready());
                var warnings = new List<string>(); form.DeviceConnectionNoticeForTests = warnings.Add;
                int sends = 0; form.ConfirmDeviceDisconnectForTests = () => false;
                form.DisconnectDeviceForTests = () => { sends++; return Task.FromResult(new HardwareStatusSnapshot { HardwareEnabled = true, StatusCode = "Disconnected" }); };
                Check(!form.TryDisconnectCurrentDevice().Result && sends == 0, "cancel does not disconnect");
                form.ConfirmDeviceDisconnectForTests = () => true;
                var completion = new TaskCompletionSource<HardwareStatusSnapshot>(); form.DisconnectDeviceForTests = () => { sends++; return completion.Task; };
                long epoch = (long)Field(form, "hardwareRefreshRequestEpoch");
                Task<bool> request = form.TryDisconnectCurrentDevice();
                Check(!request.IsCompleted && (bool)Field(form, "hardwareDisconnectInProgress"), "disconnect async and bounded by in-progress state");
                Check((long)Field(form, "hardwareRefreshRequestEpoch") > epoch, "old inspection callback epoch invalidated");
                Check(!(bool)Call(form, "CanPollHomeDevice") && !(bool)Call(form, "CanOpenLiveCalibration"), "no reconnect or live entry during disconnect");
                Check(tiles["PressureTest"].ModeBadge == "仿真" && tiles["CalibrationTest"].ModeBadge == "仿真", "both future entry badges switch to simulation");
                Check(!form.TryDisconnectCurrentDevice().Result && sends == 1, "double click cannot issue another command");
                completion.SetResult(new HardwareStatusSnapshot { HardwareEnabled = true, StatusCode = "Disconnected", CheckedUtc = DateTime.UtcNow });
                Pump(request); Check(request.Result && !state.DeviceConnected, "confirmed disconnect updates state");
                Check((bool)Field(form, "hardwareManuallyDisconnected") && !((Timer)Field(form, "devicePollTimer")).Enabled, "manual disconnect stays paused");
                Call(form, "ReconnectCurrentDevice"); Check(!(bool)Field(form, "hardwareManuallyDisconnected"), "explicit refresh resumes session only");
                Check(sends == 1, "verification never invokes actual connector");
                state.SetHardware(Ready());
                form.DisconnectDeviceForTests = () => { sends++; return Task.FromResult(new HardwareStatusSnapshot { HardwareEnabled = true, Connected = true, StatusCode = "Error", Message = "stub failure" }); };
                Check(!form.TryDisconnectCurrentDevice().Result && state.Hardware.StatusCode == "DisconnectFailed", "failed command is not confirmed disconnection");
                Check(state.Hardware.Connected && !state.DeviceConnected && warnings.Count == 1, "physical flag retained, live permission withdrawn, explanation shown");
                Check(((Button)Field(form, "disconnectDeviceButton")).Enabled, "failed disconnect can be retried without reconnecting first");
                form.DisconnectDeviceForTests = () => Task.FromResult(new HardwareStatusSnapshot { HardwareEnabled = true, StatusCode = "Disconnected" });
                Check(form.TryDisconnectCurrentDevice().Result, "retry can confirm release");
                using (var page = new LivePage { Tag = ModuleHost.CalibrationTest })
                {
                    Set(form, "activeModule", page);
                    Check(!(bool)Call(form, "CanPollHomeDevice"), "home inspection does not compete with a module");
                    int beforeChecks=refreshModes.Count;Call(form,"BeginHardwareRefresh",true,true,null);
                    Check(refreshModes.Count==beforeChecks,"direct refresh also refuses a live module instead of relying only on timer gate");
                    Check(!form.TryDisconnectCurrentDevice().Result, "manual disconnect unavailable inside active module");
                    int before = warnings.Count;
                    Call(form, "HandleLiveConnectionLoss", page); Call(form, "HandleLiveConnectionLoss", page);
                    Check(page.Stops == 1 && warnings.Count == before + 1, "one loss requests one stop and one notice");
                    Check(!page.IsDisposed && !page.SimulationMode, "loss preserves real page/data and never switches its stream to SIM");
                    Set(form, "activeModule", null);
                }
                Check(settings.Hardware.Enabled, "manual disconnect does not overwrite saved hardware settings");
                // Execute the real UI detach path with no owned worker/SDK
                // session. An absent connector is deliberate: DS600 is not used.
                state.SetHardware(Ready());Set(form,"hardwareManuallyDisconnected",false);
                form.ConfirmDeviceDisconnectForTests=()=>true;form.DisconnectDeviceForTests=null;
                var local=form.TryDisconnectCurrentDevice();Pump(local);
                Check(local.Result&&state.Hardware.Diagnostic.Contains("no DS600"),"actual home disconnect path succeeds without a connector or unbind request");
                Check((bool)Field(form,"hardwareManuallyDisconnected")&&!state.Hardware.HasLiveIdentityReadback,"local disconnect retains pause and removes old readiness");
                form.Close();
            }
            Console.WriteLine("HOME_DEVICE_CONNECTION_PASS=" + checks + "; HARDWARE_COMMANDS=0; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void Pump(Task task) { var until = DateTime.UtcNow.AddSeconds(5); while (!task.IsCompleted && DateTime.UtcNow < until) Application.DoEvents(); Check(task.IsCompleted, "async disconnect finishes"); }
}
