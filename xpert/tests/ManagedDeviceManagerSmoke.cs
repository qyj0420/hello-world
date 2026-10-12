// Managed UI only. Product Main, vendor processes, native SDKs and real
// settings are never entered; every explicit discovery/settings call is fake.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class ManagedDeviceManagerSmoke
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool pass,string text){checks++;if(!pass)throw new Exception(text);}
    static object Field(object o,string name){return o.GetType().GetField(name,Flags).GetValue(o);}
    static void Set(object o,string name,object value){o.GetType().GetField(name,Flags).SetValue(o,value);}
    static void Call(object o,string name){o.GetType().GetMethod(name,Flags).Invoke(o,null);}
    static void Pump(){for(int i=0;i<5;i++)Application.DoEvents();}
    static DataGridView Grid(Form f){return (DataGridView)f.Controls.Find("ManagedDeviceSnapshot",true)[0];}
    static string Values(Form f){string all="";foreach(DataGridViewRow row in Grid(f).Rows)all+=Convert.ToString(row.Cells[1].Value)+"\n";return all;}
    static void Hide(Form f){f.ShowInTaskbar=false;f.Opacity=0;f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-20000,-20000);}
    static void DialogFixtures(string qa)
    {
        var settings=new AppSettings {DataRoot=Path.Combine(qa,"NOT_PUBLIC_SECRET_PATH")};
        settings.Hardware.SelectedHubMac="CONFIGURED_ONLY_MAC";settings.Hardware.ControllerLocation="CONFIGURED_CTRL";settings.Hardware.AcquisitionLocation="CONFIGURED_PICO";
        var unknown=new HardwareStatusSnapshot {HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",ControllerSerial="UNVERIFIED_SERIAL",HubMac="UNVERIFIED_MAC"};
        foreach(string language in new[]{"zh-CN","zh-TW","en"}) using(var dialog=new ManagedDeviceManagerForm(settings,unknown,language))
        {
            Hide(dialog);dialog.Show();Pump();
            Check(dialog.RequestedAction==ManagedDeviceManagerAction.None&&dialog.AcceptButton==null,"Shown and Enter do not imply any operation: "+language);
            Check(Grid(dialog).ReadOnly&&!Grid(dialog).AllowUserToAddRows&&!Grid(dialog).AllowUserToDeleteRows,"snapshot is not a parameter editor: "+language);
            string text=Values(dialog);
            Check(!text.Contains("UNVERIFIED_SERIAL")&&!text.Contains("UNVERIFIED_MAC")&&!text.Contains("NOT_PUBLIC_SECRET_PATH"),"unverified identity and unrelated private configuration not exposed: "+language);
            Check(text.Contains("CONFIGURED_ONLY_MAC"),"configuration remains labeled separately: "+language);
            if(language=="zh-TW") Check(text.Contains("參數")&&!text.Contains("参数"),"dynamic parameter summary follows Traditional language");
            if(language=="en") Check(!System.Text.RegularExpressions.Regex.IsMatch(text,"[\\u3400-\\u9FFF]"),"English snapshot values contain no untranslated operator text");
            Check(dialog.Icon!=null,"owned dialog receives configured OEM icon: "+language);
            dialog.Close();Check(dialog.RequestedAction==ManagedDeviceManagerAction.None,"close is navigation only: "+language);
        }
        var known=new HardwareStatusSnapshot {HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",FromLiveIntegrationProcess=true,IntegrationRequestMode="--inspect",CheckedUtc=DateTime.UtcNow,HubMac="001122334455",ControllerSerial="REAL_ART",AcquisitionSerial="REAL_PICO"};
        using(var dialog=new ManagedDeviceManagerForm(settings,known,"en"))
        {
            known.ControllerSerial="REPLACED_AFTER_SNAPSHOT";
            Check(Values(dialog).Contains("REAL_ART")&&!Values(dialog).Contains("REPLACED_AFTER_SNAPSHOT"),"snapshot is copied without changing identity or timestamp");
            Hide(dialog);dialog.Show();Pump();((Button)dialog.Controls.Find("OpenManagedDeviceParameters",true)[0]).PerformClick();
            Check(dialog.RequestedAction==ManagedDeviceManagerAction.Parameters,"only explicit parameter click requests settings");
        }
        using(var dialog=new ManagedDeviceManagerForm(settings,null,"en"))
        {
            Hide(dialog);dialog.Show();Pump();((Button)dialog.Controls.Find("ConnectRefreshManagedDevice",true)[0]).PerformClick();
            Check(dialog.RequestedAction==ManagedDeviceManagerAction.ConnectRefresh,"only explicit connection click requests discovery");
        }
    }
    static void HostFixtures(string qa)
    {
        var settings=new AppSettings {DataRoot=qa};var state=new ApplicationState();
        using(var f=new MainForm(settings,state,true))
        {
            Hide(f);f.Show();Pump();int opened=0,scans=0,external=0,parameters=0;string notice="";
            var log=new Diagnostics(Path.Combine(qa,"managed-device-logs"));f.DeviceManagerDiagnosticsForTests=log;
            f.RunDeviceManagerWithStartForTests=started=>{external++;throw new Exception("external manager must not run");};
            f.DiscoverDetectorsForTests=token=>{scans++;Check(!(bool)Field(f,"deviceManagerOpening"),"viewer closes and unlocks before explicit discovery");return Task.FromResult(new DetectionDeviceList(DateTime.UtcNow,new List<DetectionDevice>()));};
            f.SelectDetectorForTests=receipt=>null;
            f.OpenManagedDeviceSettingsForTests=section=>{parameters++;Check(section==SettingsSection.Device&&!(bool)Field(f,"deviceManagerOpening"),"explicit settings uses existing Device section after viewer closes");};
            f.DeviceConnectionNoticeForTests=x=>{notice=x;Check(!(bool)Field(f,"deviceManagerOpening"),"viewer failure notice follows latch restoration");};
            string before=DeviceSetupStatus.ParameterHash(settings);DateTime timestamp=state.Hardware.CheckedUtc;
            f.ShowManagedDeviceDialogForTests=dialog=>{opened++;Check((bool)Field(f,"deviceManagerOpening")&&!f.CanRefreshDetectionDevices(),"viewer prevents concurrent management/discovery");Call(f,"ManageDetectionDevices");Check(opened==1,"duplicate viewer click rejected");Hide(dialog);dialog.Show();Pump();Check(dialog.RequestedAction==ManagedDeviceManagerAction.None,"actual default window starts no action");dialog.Close();return ManagedDeviceManagerAction.None;};
            Call(f,"ManageDetectionDevices");Pump();
            Check(opened==1&&scans==0&&external==0&&parameters==0,"default management opens only host viewer; no vendor, discovery or settings call");
            Check(!state.DeviceConnected&&state.Hardware.CheckedUtc==timestamp&&DeviceSetupStatus.ParameterHash(settings)==before,"default management cannot create Ready, refresh evidence or change configuration");
            Check(!(bool)Field(f,"deviceManagerOpening")&&((Button)Field(f,"refreshDeviceButton")).Text=="管理设备","default close restores button/latch without falsely opened driver label");
            f.ShowManagedDeviceDialogForTests=dialog=>{opened++;return ManagedDeviceManagerAction.ConnectRefresh;};
            Call(f,"ManageDetectionDevices");Pump();Check(scans==1&&external==0&&!state.DeviceConnected,"explicit connection delegates to existing guarded workflow and empty receipt remains unknown");
            f.ShowManagedDeviceDialogForTests=dialog=>{opened++;return ManagedDeviceManagerAction.Parameters;};
            Call(f,"ManageDetectionDevices");Pump();Check(parameters==1&&scans==1,"explicit parameter action does not imply discovery");
            int prior=opened;
            foreach(string flag in new[]{"firstBindingInProgress","settingsDialogActive","settingsWorkflowActive","hardwareRefreshInProgress","hardwareDisconnectInProgress","detectionSelectionBusy","hardwareConnectionQueued"})
            {Set(f,flag,true);Call(f,"ManageDetectionDevices");Set(f,flag,false);Check(opened==prior&&scans==1,"existing busy/ownership gate remains: "+flag);}
            f.ShowManagedDeviceDialogForTests=dialog=>{throw new IOException("fake host viewer failure");};
            Call(f,"ManageDetectionDevices");Pump();Check(notice.Contains("fake host viewer failure")&&File.ReadAllText(log.CurrentLogPath).Contains("fake host viewer failure"),"viewer exception remains logged after UI recovery");
            Check(external==0&&scans==1&&!state.DeviceConnected,"failed viewer never launches vendor, claims binding or auto-connects");
            f.Close();
        }
    }
    [STAThread]static int Main(string[]args)
    {try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);DialogFixtures(args[0]);HostFixtures(args[0]);Console.WriteLine("MANAGED_DEVICE_MANAGER_PASS="+checks+"; HARDWARE=0; DRIVER_PROCESS=0; SETTINGS_WRITE=0");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
