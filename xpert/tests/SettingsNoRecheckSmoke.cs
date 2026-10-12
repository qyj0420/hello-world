using System;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class SettingsNoRecheckSmoke
{
    static int checks;const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object F(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,Hidden).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,Hidden).Invoke(o,a);}
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string qa=Path.Combine(args[0],"settings-session");Directory.CreateDirectory(qa);
        var s=new AppSettings{DataRoot=qa};s.Hardware.Enabled=true;s.Hardware.ControllerSerial="0000000001";s.Hardware.AcquisitionSerial="QA000/0001";s.Hardware.SelectedHubMac="001122334455";s.Hardware.ControllerLocation="TEST-ART";s.Hardware.AcquisitionLocation="TEST-PICO";
        var ready=new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",
            FromLiveIntegrationProcess=true,ControllerSerial="0000000001",AcquisitionSerial="QA000/0001",HubMac="001122334455",ControllerInstanceId=@"USB\TEST\ART",AcquisitionInstanceId=@"USB\TEST\PICO",CheckedUtc=DateTime.UtcNow.AddMinutes(-30)};
        DeviceParameterProfiles.Confirm(s,ready); // Synthetic configured pair; no hardware or live settings writes.
        var state=new ApplicationState();state.SetHardware(ready);
        using(var f=new MainForm(s,state,true)){
            f.ShowInTaskbar=false;f.Opacity=0;f.Location=new Point(-20000,-20000);f.Show();Application.DoEvents();
            state.SetHardware(ready);Call(f,"RememberValidatedDevice",ready);f.DevicePresentForTests=id=>true;
            int refresh=0;var callbacks=new List<Action<HardwareStatusSnapshot>>();
            f.RefreshDeviceForTests=(connect,done)=>{refresh++;callbacks.Add(done);return true;};
            int entries=0;
            f.TestNavigationForTests=(id,simulation)=>{Check(id==ModuleHost.PressureTest&&!simulation,"pressure page reuses identified session");entries++;};
            for(int i=0;i<3;i++)Call(f,"OpenTestModule",ModuleHost.PressureTest,(long)F(f,"navigationRequestSequence"));
            Check(entries==3&&refresh==0,"repeated pressure entry does not start full inspection");
            Check(state.Hardware.CheckedUtc==ready.CheckedUtc,"page entry does not fabricate fresh safety/identity time");
            f.TestNavigationForTests=null;
            var adapter=F(f,"hardwareAdapter");var host=F(f,"moduleHost");long generation=(long)F(f,"hardwareRefreshGeneration");
            using(var closeEditor=new Timer{Interval=30}){
                closeEditor.Tick+=delegate {
                    var editor=Application.OpenForms.Cast<Form>().OfType<SettingsForm>().FirstOrDefault();
                    if(editor!=null){closeEditor.Stop();editor.DialogResult=DialogResult.Cancel;editor.Close();}
                };
                closeEditor.Start();Call(f,"OpenSettings",SettingsSection.General);
            }
            Check(!(bool)F(f,"settingsWorkflowActive")&&refresh==0,"actual settings dialog cancel completes without refresh");
            Call(f,"FinishSettingsWorkflow",false,s.Clone());
            Check(refresh==0&&ReferenceEquals(adapter,F(f,"hardwareAdapter"))&&ReferenceEquals(host,F(f,"moduleHost")),"cancel keeps services without a connection request");
            Check(generation==(long)F(f,"hardwareRefreshGeneration"),"cancel preserves session generation");
            for(int i=0;i<4;i++){
                var before=((AppSettings)F(f,"settings")).Clone();var edited=before.Clone();
                if(i==0)edited.LanguageCode="en";
                if(i==1)edited.TaskMinus3.CalibrationAverageCount=32;
                if(i==2)edited.Hardware.Task39InputSettingsPath="new-input.xml";
                if(i==3)edited.Hardware.DriverInstallerPath="new-installer.exe";
                Set(f,"settings",edited);Call(f,"FinishSettingsWorkflow",true,before);
                Check(refresh==0,"ordinary edit does not request refresh "+i);
                Check(ReferenceEquals(adapter,F(f,"hardwareAdapter")) && generation==(long)F(f,"hardwareRefreshGeneration"),"ordinary edits retain adapter and session generation "+i);
                Check(!ReferenceEquals(host,F(f,"moduleHost")),"ordinary edits rebuild parameter consumer "+i);
                Check((bool)Call(f,"TryReuseValidatedDevice"),"unchanged device identity preserved "+i);
                Check(state.Hardware.CheckedUtc==ready.CheckedUtc,"ordinary edit never fabricates a new check timestamp "+i);
            }
            var prior=((AppSettings)F(f,"settings")).Clone();var changed=prior.Clone();changed.Hardware.AcquisitionSerial="PICO-NEW";Set(f,"settings",changed);
            Call(f,"FinishSettingsWorkflow",true,prior);
            Check(refresh==1&&(bool)F(f,"hardwareRefreshInProgress"),"serial change starts exactly one refresh");
            Check(!state.DeviceConnected&&F(f,"validatedDeviceSession")==null,"old ready identity invalidated before callback");
            callbacks[0](new HardwareStatusSnapshot{HardwareEnabled=true,StatusCode="Disconnected"});Application.DoEvents();
            ((Timer)F(f,"devicePollTimer")).Stop();
            prior=changed.Clone();var redirected=changed.Clone();redirected.Hardware.SelectedHubMac="112233445566";Set(f,"settings",redirected);
            Call(f,"FinishSettingsWorkflow",true,prior);
            Check(refresh==1&&!state.DeviceConnected&&(bool)F(f,"hardwareManuallyDisconnected"),"target change invalidates old identity and asks reconnect without auto opening devices");
            Check(!((Timer)F(f,"devicePollTimer")).Enabled,"changed target cannot silently auto reconnect");
            Call(f,"FinishSettingsWorkflow",false,redirected.Clone());Check(refresh==1,"cancel does not undo manual disconnect");
            f.RefreshDeviceForTests=null;f.Close();
        }
        Console.WriteLine("SETTINGS_NO_RECHECK_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_SETTINGS_WRITTEN=0");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
