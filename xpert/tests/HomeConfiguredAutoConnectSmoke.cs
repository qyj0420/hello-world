using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class HomeConfiguredAutoConnectSmoke
{
    const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static int checks;
    static void Check(bool pass,string message){checks++;if(!pass)throw new Exception(message);}
    static object F(object value,string name){return value.GetType().GetField(name,Hidden).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,Hidden).SetValue(value,data);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Hidden).Invoke(value,args);}
    static AppSettings Settings(string root)
    {
        var s=new AppSettings{DataRoot=root};s.Hardware.Enabled=true;s.Hardware.SelectedHubMac="001122334455";
        s.Hardware.ControllerLocation="H11";s.Hardware.AcquisitionLocation="S51";
        s.Hardware.ControllerSerial="1023417009";s.Hardware.AcquisitionSerial="ABCDE/1234";
        DeviceParameterProfiles.Confirm(s,Ready());return s;
    }
    static HardwareStatusSnapshot Ready()
    {
        return new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",
            HubMac="001122334455",HubIp="192.0.2.1",ControllerSerial="1023417009",AcquisitionSerial="ABCDE/1234",ControllerDeviceName="Dev7",
            ControllerInstanceId=@"USB\VID_3063&PID_3135\1023417009",AcquisitionInstanceId=@"USB\VID_0CE9&PID_1218\FAKE-PORT",
            ControllerTransportSerial="1023417009",AcquisitionTransportSerial="FAKE-PORT",CheckedUtc=DateTime.UtcNow};
    }
    static void Provenance(string root)
    {
        var s=Ready();HardwareStatusAdapter.MarkLiveReadback(s,0,true,"fake/connector.exe","001122334455","H11","S51");
        Check(s.HasLiveIdentityReadback&&s.IntegrationRequestMode=="--connect"&&s.ConnectionReadbackVerified,"complete connect readback retains truthful source mode");
        Check(CalibrationHardwareIdentity.Read(Settings(root),s)[1]=="Dev7","calibration accepts complete readback, not a duplicate scan");
        Check(HardwareStatusAdapter.VerifiedDisconnectAddressHint(s,"001122334455","H11","S51","fake/connector.exe")=="192.0.2.1","matching connect evidence supplies same address hint");
        foreach(string bad in new[]{"missing-serial","missing-instance","same-instance","failed-exit","not-ready"}){
            var x=Ready();if(bad=="missing-serial")x.AcquisitionSerial="";if(bad=="missing-instance")x.AcquisitionInstanceId="";
            if(bad=="same-instance")x.AcquisitionInstanceId=x.ControllerInstanceId;if(bad=="not-ready")x.Connected=false;
            HardwareStatusAdapter.MarkLiveReadback(x,bad=="failed-exit"?40:0,true,"fake/connector.exe","001122334455","H11","S51");
            Check(!x.HasLiveIdentityReadback,"invalid connect never establishes readback: "+bad);
        }
        var noReadback=Ready();noReadback.FromLiveIntegrationProcess=true;noReadback.IntegrationRequestMode="--connect";
        Check(!noReadback.HasLiveIdentityReadback,"command success alone cannot authorize identity");
        s.FromLiveIntegrationProcess=false;Check(!s.HasLiveIdentityReadback,"cached/status-file identity stays untrusted");
        Check(HardwareStatusAdapter.VerifiedDisconnectAddressHint(s,"001122334455","H11","S51","fake/connector.exe")=="","cached flag cannot authorize address hint");
    }
    static void ParseIdentity(string root)
    {
        var settings=Settings(root);var h=typeof(HardwareStatusAdapter);var parse=h.GetMethod("ParseStatusFile",Hidden);var validate=h.GetMethod("ValidateStatus",Hidden);
        using(var adapter=new HardwareStatusAdapter(settings)){
            foreach(bool wrong in new[]{false,true}){
                string path=Path.Combine(root,wrong?"wrong.xml":"matching.xml");
                File.WriteAllText(path,"<HardwareStatus Schema=\"1\" State=\"Ready\" Mac=\"001122334455\" Utc=\""+DateTime.UtcNow.ToString("O")+"\" MaxAgeSeconds=\"30\"><Module Kind=\"Controller\" Location=\"H11\" Serial=\"1023417009\" Connected=\"true\" Ready=\"true\"/><Module Kind=\"Acquisition\" Location=\"S51\" Serial=\""+(wrong?"OTHER/4321":"ABCDE/1234")+"\" Connected=\"true\" Ready=\"true\"/></HardwareStatus>");
                var result=(HardwareStatusSnapshot)validate.Invoke(adapter,new object[]{parse.Invoke(adapter,new object[]{path}),0,"001122334455","H11","S51","",true});
                Check(wrong?!result.Ready&&result.StatusCode=="IdentityMismatch":result.Ready,"saved serial check: "+wrong);
                Check(!result.HasLiveIdentityReadback,"parsing files cannot manufacture live provenance");
            }
        }
    }
    [STAThread] static int Main(string[] args){try{
        string root=Path.Combine(args[0],"home-configured-autoconnect");Directory.CreateDirectory(root);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        Provenance(root);ParseIdentity(root);
        var settings=Settings(root);var state=new ApplicationState();
        using(var f=new MainForm(settings,state,true)){
            f.ShowInTaskbar=false;f.Opacity=0;f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-20000,-20000);f.Show();Application.DoEvents();
            f.DeviceParameterSettingsPathForTests=Path.Combine(root,"settings.xml");f.DevicePresentForTests=id=>true;f.DeviceConnectionNoticeForTests=x=>{};
            DateTime now=DateTime.UtcNow;f.HomeConnectionUtcForTests=()=>now;
            var requests=new List<bool>();var callbacks=new List<Action<HardwareStatusSnapshot>>();
            f.RefreshDeviceForTests=(connect,done)=>{requests.Add(connect);callbacks.Add(done);return true;};
            Action poll=()=>Call(f,"PollHomeDeviceConnection");
            state.SetHardware(new HardwareStatusSnapshot{HardwareEnabled=true,StatusCode="Offline"});poll();
            Check(requests.Count==1&&requests[0],"configured offline target connects without a manual click");
            for(int i=0;i<20;i++)poll();Check(requests.Count==1,"only one in-flight connection request");
            callbacks[0](new HardwareStatusSnapshot{HardwareEnabled=true,StatusCode="Offline"});now=now.AddSeconds(1);poll();Check(requests.Count==1,"first 2-second backoff");
            now=now.AddSeconds(1);poll();Check(requests.Count==2&&requests[1],"second attempt after minimum delay");
            callbacks[1](new HardwareStatusSnapshot{HardwareEnabled=true,StatusCode="NotReady"});now=now.AddSeconds(3);poll();Check(requests.Count==2,"second 4-second backoff");
            now=now.AddSeconds(1);poll();Check(requests.Count==3,"third attempt due");
            var good=Ready();HardwareStatusAdapter.MarkLiveReadback(good,0,true,"fake/connector.exe","001122334455","H11","S51");callbacks[2](good);
            Check(state.DeviceConnected&&F(f,"validatedDeviceSession")!=null,"connect readback immediately establishes session");
            DateTime original=state.Hardware.CheckedUtc;
            for(int i=0;i<30;i++)poll();Check(requests.Count==3&&state.Hardware.CheckedUtc==original,"stable home does not scan or forge time");
            var oldAdapter=F(f,"hardwareAdapter");var oldHost=F(f,"moduleHost");long generation=(long)F(f,"hardwareRefreshGeneration");
            var before=settings.Clone();var edited=settings.Clone();edited.LanguageCode="en";Set(f,"settings",edited);Call(f,"FinishSettingsWorkflow",true,before);
            Check(ReferenceEquals(oldAdapter,F(f,"hardwareAdapter"))&&generation==(long)F(f,"hardwareRefreshGeneration"),"ordinary saved parameters preserve communication session");
            Check(!ReferenceEquals(oldHost,F(f,"moduleHost"))&&ReferenceEquals(F(F(f,"moduleHost"),"settings"),edited),"new module host receives new parameter object");
            foreach(string code in new[]{"PresenceUnknown","IdentityMismatch","InvalidStatus","SelectionUnverified","DisconnectFailed","IntegrationError","Error","MissingDriver","NeedsConfiguration"}){
                state.SetHardware(new HardwareStatusSnapshot{HardwareEnabled=true,StatusCode=code});now=now.AddMinutes(2);poll();Check(requests.Count==3,"no automatic recovery for "+code);
            }
            foreach(string flag in new[]{"hardwareManuallyDisconnected","hardwareDisconnectInProgress","settingsDialogActive","detectionSelectionBusy","firstBindingInProgress"}){
                state.SetHardware(new HardwareStatusSnapshot{HardwareEnabled=true,StatusCode="Disconnected"});Set(f,flag,true);poll();Check(requests.Count==3,"autoconnect gate: "+flag);Set(f,flag,false);
            }
            using(var page=new UserControl()){Set(f,"activeModule",page);poll();Check(requests.Count==3,"no connector while a calibration/pressure/module page is active");Set(f,"activeModule",null);}
            poll();Check(requests.Count==4&&requests[3],"return home resumes only communication retry");
            var late=callbacks[3];f.ConfirmDeviceDisconnectForTests=()=>true;f.DisconnectDeviceForTests=()=>Task.FromResult(new HardwareStatusSnapshot{HardwareEnabled=true,StatusCode="Disconnected"});
            Check(f.TryDisconnectCurrentDevice().GetAwaiter().GetResult(),"explicit disconnect completes on fake transport");
            late(good);poll();Check(!state.DeviceConnected&&requests.Count==4&&(bool)F(f,"hardwareManuallyDisconnected"),"late old callback cannot undo manual disconnect or reconnect");
            f.RefreshDeviceForTests=null;f.Close();
        }
        Console.WriteLine("HOME_CONFIGURED_AUTOCONNECT_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_SETTINGS_WRITTEN=0; FAKE_CLOCK=true");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
