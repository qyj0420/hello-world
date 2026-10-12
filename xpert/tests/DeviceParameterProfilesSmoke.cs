using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Reflection;
using Xiongxin.OWTS.Desktop;

static class DeviceParameterProfilesSmoke
{
    static int n;
    static void Check(bool ok,string message){n++;if(!ok)throw new Exception(message);}
    static bool Reject(Action a){try{a();return false;}catch(Exception e){return e is InvalidOperationException||e is ArgumentException||e is System.Xml.XmlException||e is InvalidDataException||e is FormatException;}}
    static HardwareStatusSnapshot Actual(string art="0000000001",string pico="QA000/0001")
    {return new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",FromLiveIntegrationProcess=true,
        ControllerSerial=art,AcquisitionSerial=pico,HubMac="001122334455",CheckedUtc=DateTime.UtcNow};}
    static void Bind(AppSettings s,HardwareStatusSnapshot a){s.Hardware.Enabled=true;s.Hardware.SelectedHubMac=a.HubMac;s.Hardware.ControllerSerial=a.ControllerSerial;s.Hardware.AcquisitionSerial=a.AcquisitionSerial;s.Hardware.ControllerLocation="TEST-ART";s.Hardware.AcquisitionLocation="TEST-PICO";}
    static System.Collections.Generic.IEnumerable<Control> Controls(Control parent){foreach(Control c in parent.Controls){yield return c;foreach(var d in Controls(c))yield return d;}}
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string root=Path.Combine(args[0],"device-profiles");Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.xml");
        var a=Actual();var s=new AppSettings{DataRoot=root};Bind(s,a);
        Check(!DeviceParameterProfiles.IsConfigured(s,a),"first pair needs explicit configuration");
        s.MaximumVoltageKv=30;s.DividerRatio=11000;s.DeviceTriggerVolts=1.6;s.CompensationCapacitanceUf=.23456789;
        DeviceParameterProfiles.Confirm(s,a);s.Save(path);
        Check(DeviceParameterProfiles.IsConfigured(s,a),"first Save immediately completes configuration");
        Check(s.PressureRatio==6000 && 15000/s.PressureRatio==2.5,"30kV/5V ratio and 15kV AO");
        var loaded=AppSettings.Load(path);Check(DeviceParameterProfiles.IsConfigured(loaded,a),"restart retains completion");
        var stale=loaded.Clone();loaded.MaximumVoltageKv=60;loaded.DividerRatio=12000;loaded.DeviceProfileEditAuthorized=true;loaded.Save(path);
        Check(loaded.PressureRatio==12000,"ratio follows limit");
        Check(Reject(()=>stale.Save(path)),"stale save cannot overwrite newer profile");
        var latest=AppSettings.Load(path);Check(latest.DividerRatio==12000 && DeviceParameterProfiles.ValuesMatch(latest,DeviceParameterProfiles.Key(a)),"ordinary settings synchronize latest pair");
        var b=Actual("0000000002","QA000/0002");Bind(latest,b);latest.Save(path);
        Check(!DeviceParameterProfiles.IsConfigured(latest,a)&&!DeviceParameterProfiles.IsConfigured(latest,b),"pair switch invalidates old confirmation");
        Check(!DeviceParameterProfiles.Load(latest,b),"different pair cannot borrow old values");
        latest.MaximumVoltageKv=20;latest.DividerRatio=9000;latest.DeviceProfileEditAuthorized=true;DeviceParameterProfiles.Confirm(latest,b);latest.Save(path);
        latest=AppSettings.Load(path);Bind(latest,a);Check(DeviceParameterProfiles.Load(latest,a),"return to first pair");latest.Save(path);
        Check(latest.MaximumVoltageKv==60&&latest.DividerRatio==12000&&latest.DeviceTriggerVolts==1.6,"first pair restores latest exact values");
        Check(latest.CompensationCapacitanceUf==.23456789,"unmodified decimal precision preserved");
        Check(DeviceParameterProfiles.Key(a)==DeviceParameterProfiles.Pair(a.AcquisitionSerial,a.ControllerSerial),"board order normalized");
        var otherHub=a.Clone();otherHub.HubMac="FFEEDDCCBBAA";Bind(latest,otherHub);
        Check(DeviceParameterProfiles.Load(latest,otherHub)&&DeviceParameterProfiles.IsConfigured(latest,otherHub),"same board pair moves hubs without duplicate profile");
        Check(Reject(()=>DeviceParameterProfiles.PressureRatioFor(Double.NaN))&&Reject(()=>DeviceParameterProfiles.PressureRatioFor(0)),"invalid limits rejected");
        var invalid=latest.Clone();invalid.DeviceProfilesXml="<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///unknown'>]><DeviceProfiles version='1'>&ext;</DeviceProfiles>";
        Check(!DeviceParameterProfiles.IsConfigured(invalid,otherHub),"malformed profile fails closed");
        var clone=latest.Clone();clone.Hardware.AcquisitionSerial="QA000/9999";
        Check(Reject(()=>DeviceParameterProfiles.Confirm(clone,a)),"identity change during editing rejected");
        foreach(bool zh in new[]{true,false})using(var form=new DeviceParametersForm(latest,DeviceParameterProfiles.Key(a),false,zh)){
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
            Check(Controls(form).Count(c=>c is NumericUpDown)==7&&Controls(form).Count(c=>c is ComboBox)==1&&Controls(form).Count(c=>c is CheckBox)==2,"device parameter controls include opt-in fine ramp and automatic threshold");
            Check(Controls(form).OfType<Label>().Any(c=>c.Text==(zh?"分压比（V/V）":"Divider ratio (V/V)")),"divider ratio label uses an unambiguous V/V unit");
            Check(!Controls(form).OfType<Label>().Any(c=>c.Text.Contains("/ V/V")),"no duplicate separator before divider ratio unit");
            Check(!Controls(form).Any(c=>c.Name=="PressureRatio"),"ratio not editable");
            var captured=form.Capture();Check(DeviceParameterProfiles.SameValues(latest,captured),"unchanged values preserve exact precision");
            ((NumericUpDown)Controls(form).Single(c=>c.Name=="MaximumVoltageKv")).Value=25;
            Check(form.Capture().PressureRatio==5000,"editor derives ratio from new limit");
        }
        var firstSettings=new AppSettings{DataRoot=root};Bind(firstSettings,a);
        var firstState=new ApplicationState();firstState.SetHardware(a);
        string firstPath=Path.Combine(root,"first-connection-settings.xml");firstSettings.Save(firstPath);
        using(var host=new MainForm(firstSettings,firstState,true)){
            host.DeviceParameterSettingsPathForTests=firstPath;
            int refreshes=0;host.RefreshDeviceForTests=(connect,done)=>{refreshes++;return true;};
            var edit=typeof(MainForm).GetMethod("EditDeviceParameters",BindingFlags.Instance|BindingFlags.NonPublic);
            string before=AppSettings.FileDigest(firstPath);
            host.DeviceParameterEditorForTests=(draft,pair,first)=>{Check(first,"first connection editor identified");return null;};
            Check(!((System.Threading.Tasks.Task<bool>)edit.Invoke(host,null)).GetAwaiter().GetResult(),"cancelled first configuration remains incomplete");
            Check(AppSettings.FileDigest(firstPath)==before&&!DeviceParameterProfiles.IsConfigured(AppSettings.Load(firstPath),a),"cancel does not write or authorize configuration");
            host.DeviceParameterEditorForTests=(draft,pair,first)=>{draft.MaximumVoltageKv=30;draft.DividerRatio=11000;return draft;};
            Check(((System.Threading.Tasks.Task<bool>)edit.Invoke(host,null)).GetAwaiter().GetResult(),"actual host Save finishes first configuration");
            Check(DeviceParameterProfiles.IsConfigured(AppSettings.Load(firstPath),a),"host result survives reload without another confirmation");
            Check(refreshes==0,"first parameter Save never reconnects or drives hardware");
        }
        Console.WriteLine("DEVICE_PARAMETER_PROFILES_PASS="+n+"; HARDWARE_COMMANDS=0; USER_SETTINGS_WRITTEN=0");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
