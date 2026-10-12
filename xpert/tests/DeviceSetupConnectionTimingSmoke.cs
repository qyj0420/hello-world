using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class DeviceSetupConnectionTimingSmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Call(object o,string name,params object[] values){return o.GetType().GetMethod(name,Hidden).Invoke(o,values);}
    static object Get(object o,string name){return o.GetType().GetField(name,Hidden).GetValue(o);}
    static void Set(object o,string name,object value){o.GetType().GetField(name,Hidden).SetValue(o,value);}
    static void Pump(){for(int i=0;i<8;i++)Application.DoEvents();}
    static HardwareStatusSnapshot Actual(){return new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,
        StatusCode="Ready",ControllerSerial="0000000001",AcquisitionSerial="QA000/0001",HubMac="001122334455",CheckedUtc=DateTime.UtcNow,FromLiveIntegrationProcess=true};}
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string root=Path.Combine(args[0],"device-setup-timing");Directory.CreateDirectory(root);
        var actual=Actual();var settings=new AppSettings{DataRoot=root};settings.Hardware.Enabled=true;
        settings.Hardware.ControllerSerial=actual.ControllerSerial;settings.Hardware.AcquisitionSerial=actual.AcquisitionSerial;
        settings.Hardware.SelectedHubMac=actual.HubMac;settings.Hardware.ControllerLocation="1-1";settings.Hardware.AcquisitionLocation="1-2";
        string path=Path.Combine(root,"settings.xml");settings.Save(path);
        var state=new ApplicationState();state.SetHardware(actual);
        using(var host=new MainForm(settings,state,true)){
            host.StartPosition=FormStartPosition.Manual;host.Location=new Point(-25000,-25000);host.ShowInTaskbar=false;
            host.DeviceParameterSettingsPathForTests=path;
            int edits=0,refreshes=0,navigations=0,notices=0;bool save=false,connect=true;
            host.RefreshDeviceForTests=(connectRequested,done)=>{refreshes++;throw new Exception("Unexpected native-inspection path");};
            host.DeviceConnectionNoticeForTests=message=>notices++;
            host.TestNavigationForTests=(id,simulation)=>navigations++;
            host.DeviceParameterEditorForTests=(draft,key,first)=>{
                edits++;Check(!(bool)Get(host,"detectionSelectionBusy")&&!(bool)Get(host,"hardwareRefreshInProgress"),"editor opens only after connection is idle");
                if(!save)return null;draft.MaximumVoltageKv=30;draft.DividerRatio=11000;return draft;
            };
            host.DiscoverDetectorsForTests=token=>Task.FromResult(new DetectionDeviceList(DateTime.UtcNow,new System.Collections.Generic.List<DetectionDevice>{
                new DetectionDevice(actual.HubMac,"192.0.2.1","1-1","1-2",true,true,"Available")}));
            host.ConnectDetectorForTests=(device,token)=>{if(connect)state.SetHardware(actual);return Task.FromResult(connect);};
            host.Show();Pump();
            Check(!host.Text.Contains("1.1.9.6 / R6"),"title does not report a hard-coded previous build");
            Check(host.Text.Contains(System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(MainForm).Assembly.Location).FileVersion),"title follows actual executable file version");
            long request=(long)Get(host,"navigationRequestSequence");
            Call(host,"CompleteLiveTestNavigation",ModuleHost.PressureTest,request,false,actual);Pump();
            Check(edits==0&&navigations==0&&notices==1,"pressure navigation gates missing setup but never opens editor");
            var result=(Task<bool>)Call(host,"SearchAndConnectDevices",true);Pump();
            Check(result.IsCompleted&&result.Result&&edits==1,"first successful connection opens configuration immediately");
            Check(!DeviceParameterProfiles.IsConfigured((AppSettings)Get(host,"settings"),actual),"cancel does not configure device");
            request=(long)Get(host,"navigationRequestSequence");Call(host,"CompleteLiveTestNavigation",ModuleHost.PressureTest,request,false,actual);Pump();
            Check(edits==1&&navigations==0&&notices==2,"cancelled setup is not reopened by entering pressure page");
            connect=false;result=(Task<bool>)Call(host,"SearchAndConnectDevices",true);Pump();
            Check(result.IsCompleted&&!result.Result&&edits==1,"failed connection does not prompt for stale connected device");
            connect=true;save=true;result=(Task<bool>)Call(host,"SearchAndConnectDevices",true);Pump();
            Check(result.Result&&edits==2,"explicit reconnection offers cancelled setup again");
            Check(DeviceParameterProfiles.IsConfigured(AppSettings.Load(path),actual),"connection setup save completes persistent configuration");
            Call(host,"CompleteDeviceConnectionSetup");Pump();Check(edits==2,"configured device does not repeatedly prompt");
            request=(long)Get(host,"navigationRequestSequence");Call(host,"CompleteLiveTestNavigation",ModuleHost.PressureTest,request,false,actual);Pump();
            Check(navigations==1&&edits==2,"configured device can enter pressure without another editor");
            Check(refreshes==0,"connection completion reuses exact identity without inspection");
            var tiles=(System.Collections.Generic.Dictionary<string,ModuleTile>)Get(host,"tiles");
            Check(tiles[ModuleHost.PressureTest].ModeBadge=="HV DAC"&&tiles[ModuleHost.CalibrationTest].ModeBadge=="HV DAC","both connected test tiles use HV DAC badge");
            var pendingSettings=new AppSettings{DataRoot=root};pendingSettings.Hardware=settings.Hardware.Clone();
            Set(host,"settings",pendingSettings);Set(host,"pendingFirstDeviceSetupKey",String.Empty);
            ((System.Collections.Generic.HashSet<string>)Get(host,"unlistedDeviceNoticeQueued")).Clear();
            save=false;Call(host,"CompleteDeviceConnectionSetup");Set(host,"settingsDialogActive",true);Pump();
            Check(edits==2,"busy settings window defers initial configuration");
            Set(host,"settingsDialogActive",false);Call(host,"TryShowPendingDeviceSetup");Pump();
            Check(edits==3,"deferred setup appears once on returning idle");
            Call(host,"TryShowPendingDeviceSetup");Pump();Check(edits==3,"cancelled editor is not repeatedly shown by polling");
            Action<HardwareStatusSnapshot> finish=null;
            host.RefreshDeviceForTests=(connectRequested,done)=>{Check(connectRequested,"explicit reconnect requested");finish=done;return true;};
            Call(host,"ReconnectCurrentDevice");finish(actual);Pump();
            Check(edits==4,"explicit reconnect path offers setup again after cancel");
            Call(host,"CompleteDeviceConnectionSetup");var changed=actual.Clone();changed.AcquisitionSerial="QA000/0002";state.SetHardware(changed);Pump();
            Call(host,"TryShowPendingDeviceSetup");Pump();
            Check(edits==4&&(string)Get(host,"pendingFirstDeviceSetupKey")==String.Empty,"queued setup for old pair cannot open for changed identity");
        }
        Console.WriteLine("DEVICE_SETUP_CONNECTION_TIMING_PASS="+checks+"; HARDWARE=False; USER_SETTINGS_WRITTEN=False");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
