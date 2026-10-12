using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Xiongxin.OWTS.Desktop;

internal static class PressureRampSettingsSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static bool Valid(AppSettings s){string message;return s.TryValidate(out message);}
    static HardwareStatusSnapshot Pair(string art,string pico){return new HardwareStatusSnapshot{
        HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",FromLiveIntegrationProcess=true,
        ControllerSerial=art,AcquisitionSerial=pico,HubMac="001122334455",CheckedUtc=DateTime.UtcNow};}
    static void Bind(AppSettings s,HardwareStatusSnapshot a){s.Hardware.Enabled=true;s.Hardware.SelectedHubMac=a.HubMac;
        s.Hardware.ControllerSerial=a.ControllerSerial;s.Hardware.AcquisitionSerial=a.AcquisitionSerial;
        s.Hardware.ControllerLocation="TEST-ART";s.Hardware.AcquisitionLocation="TEST-PICO";}
    static System.Collections.Generic.IEnumerable<Control> Controls(Control p){foreach(Control c in p.Controls){yield return c;foreach(var d in Controls(c))yield return d;}}
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.xml");
            var s=new AppSettings{DataRoot=root};Check(s.PressureRampStepV==.01&&s.PressureRampIntervalMs==1,"legacy runtime defaults preserved");
            string originalHash=DeviceSetupStatus.ParameterHash(s);
            foreach(double v in new[]{.0001,.01,.1}){s.PressureRampStepV=v;Check(Valid(s),"valid step "+v);}
            foreach(double v in new[]{Double.NaN,Double.PositiveInfinity,0,-1,.0000999,.100001}){s.PressureRampStepV=v;Check(!Valid(s),"invalid step "+v);}
            s.PressureRampStepV=.01;
            foreach(int v in new[]{1,10,1000}){s.PressureRampIntervalMs=v;Check(Valid(s),"valid interval "+v);}
            foreach(int v in new[]{0,-1,1001}){s.PressureRampIntervalMs=v;Check(!Valid(s),"invalid interval "+v);}
            s.PressureRampIntervalMs=1;Check(originalHash==DeviceSetupStatus.ParameterHash(s),"default hash stable");
            s.PressureRampStepV=.0234;Check(originalHash!=DeviceSetupStatus.ParameterHash(s),"step invalidates prepared configuration");
            string stepHash=DeviceSetupStatus.ParameterHash(s);s.PressureRampIntervalMs=23;
            Check(stepHash!=DeviceSetupStatus.ParameterHash(s),"interval invalidates prepared configuration");
            var a=Pair("0000000001","QA000/0001");var b=Pair("0000000002","QA000/0002");Bind(s,a);
            DeviceParameterProfiles.Confirm(s,a);s.Save(path);s=AppSettings.Load(path);
            Check(s.PressureRampStepV==.0234&&s.PressureRampIntervalMs==23,"round trip exact settings");
            Check(s.Clone().PressureRampStepV==.0234&&s.Clone().PressureRampIntervalMs==23,"clone retains values");
            s.PressureRampStepV=.02;s.PressureRampIntervalMs=7;s.DeviceProfileEditAuthorized=true;s.Save(path);s=AppSettings.Load(path);
            Check(DeviceParameterProfiles.IsConfigured(s,a)&&DeviceParameterProfiles.ValuesMatch(s,DeviceParameterProfiles.Key(a)),"authorized settings edit updates pair confirmation");
            Bind(s,b);s.PressureRampStepV=.03;s.PressureRampIntervalMs=9;DeviceParameterProfiles.Confirm(s,b);
            Bind(s,a);Check(DeviceParameterProfiles.Load(s,a)&&s.PressureRampStepV==.02&&s.PressureRampIntervalMs==7,"first pair restored");
            Bind(s,b);Check(DeviceParameterProfiles.Load(s,b)&&s.PressureRampStepV==.03&&s.PressureRampIntervalMs==9,"second pair isolated");
            var legacy=s.Clone();var doc=new XmlDocument();doc.LoadXml(legacy.DeviceProfilesXml);
            foreach(XmlElement e in doc.DocumentElement.ChildNodes){e.RemoveAttribute("PressureRampStepV");e.RemoveAttribute("PressureRampIntervalMs");}
            legacy.DeviceProfilesXml=doc.OuterXml;Bind(legacy,a);
            Check(DeviceParameterProfiles.Load(legacy,a)&&legacy.PressureRampStepV==.01&&legacy.PressureRampIntervalMs==1,"legacy pair resets defaults rather than borrowing previous pair");
            ((XmlElement)doc.DocumentElement.FirstChild).SetAttribute("PressureRampStepV","0.02");legacy.DeviceProfilesXml=doc.OuterXml;
            bool rejected=false;try{DeviceParameterProfiles.Load(legacy,a);}catch(InvalidDataException){rejected=true;}
            Check(rejected,"partial ramp profile rejected");
            using(var form=new SettingsForm(s,SettingsSection.Expert)){
                var all=Controls(form).ToArray();var step=(NumericUpDown)all.Single(c=>c.Name=="PressureRampStepV");
                var interval=(NumericUpDown)all.Single(c=>c.Name=="PressureRampIntervalMs");
                Check(step.Value==.03m&&interval.Value==9,"manufacturer controls load current settings");
                Check(step.DecimalPlaces==4&&step.Minimum==.0001m&&step.Maximum==.1m,"step precision and bounds");
                Check(interval.DecimalPlaces==0&&interval.Minimum==1&&interval.Maximum==1000,"interval integer bounds");
                step.Value=.0123m;interval.Value=17;
                var captured=(AppSettings)typeof(SettingsForm).GetMethod("CaptureValues",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
                Check(captured.PressureRampStepV==.0123&&captured.PressureRampIntervalMs==17,"manufacturer controls capture exact values");
                Check(s.PressureRampStepV==.03&&s.PressureRampIntervalMs==9,"unsaved editor does not mutate live configuration");
            }
            Console.WriteLine("PRESSURE_RAMP_SETTINGS_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_SETTINGS_WRITTEN=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
