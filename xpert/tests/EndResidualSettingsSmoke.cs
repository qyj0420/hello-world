using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Xiongxin.OWTS.Desktop;

internal static class EndResidualSettingsSmoke
{
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static bool Valid(AppSettings s){string m;return s.TryValidate(out m);}
    static HardwareStatusSnapshot Pair(string a,string p){return new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",FromLiveIntegrationProcess=true,ControllerSerial=a,AcquisitionSerial=p,HubMac="001122334455",CheckedUtc=DateTime.UtcNow};}
    static void Bind(AppSettings s,HardwareStatusSnapshot p){s.Hardware.Enabled=true;s.Hardware.SelectedHubMac=p.HubMac;s.Hardware.ControllerSerial=p.ControllerSerial;s.Hardware.AcquisitionSerial=p.AcquisitionSerial;s.Hardware.ControllerLocation="QA-ART";s.Hardware.AcquisitionLocation="QA-PICO";}
    static System.Collections.Generic.IEnumerable<Control> All(Control c){foreach(Control x in c.Controls){yield return x;foreach(Control y in All(x))yield return y;}}
    [STAThread] static int Main(string[] args)
    {
        try{
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.xml");
            var s=new AppSettings{DataRoot=root};Check(s.EndResidualPercent==4,"confirmed default 4 percent");
            string originalHash=DeviceSetupStatus.ParameterHash(s);
            foreach(double v in new[]{.01,4,100}){s.EndResidualPercent=v;Check(Valid(s),"valid percent "+v);}
            foreach(double v in new[]{0,-1,.0099,100.01,Double.NaN,Double.PositiveInfinity}){s.EndResidualPercent=v;Check(!Valid(s),"reject invalid percent "+v);}
            s.EndResidualPercent=4;Check(originalHash==DeviceSetupStatus.ParameterHash(s),"old default parameter hash preserved");
            s.EndResidualPercent=3.17;Check(originalHash!=DeviceSetupStatus.ParameterHash(s),"changing end percent invalidates prepared configuration");
            var a=Pair("0000000001","QA000/0001");var b=Pair("0000000002","QA000/0002");Bind(s,a);DeviceParameterProfiles.Confirm(s,a);s.Save(path);s=AppSettings.Load(path);
            Check(s.EndResidualPercent==3.17&&s.Clone().EndResidualPercent==3.17,"serialized clone exact percent");
            Bind(s,b);s.EndResidualPercent=5.43;DeviceParameterProfiles.Confirm(s,b);Bind(s,a);Check(DeviceParameterProfiles.Load(s,a)&&s.EndResidualPercent==3.17,"first device isolated");
            Bind(s,b);Check(DeviceParameterProfiles.Load(s,b)&&s.EndResidualPercent==5.43,"second device isolated");
            var xml=new XmlDocument();xml.LoadXml(s.DeviceProfilesXml);foreach(XmlElement e in xml.DocumentElement.ChildNodes)e.RemoveAttribute("EndResidualPercent");s.DeviceProfilesXml=xml.OuterXml;Bind(s,a);
            Check(DeviceParameterProfiles.Load(s,a)&&s.EndResidualPercent==4,"missing legacy field defaults to 4 not other device");
            using(var form=new SettingsForm(s,SettingsSection.Expert)){
                var field=(NumericUpDown)All(form).Single(c=>c.Name=="EndResidualPercent");
                Check(field.Value==4&&field.Minimum==.01m&&field.Maximum==100&&field.DecimalPlaces==2,"manufacturer setting visible and bounded");field.Value=2.34m;
                var captured=(AppSettings)typeof(SettingsForm).GetMethod("CaptureValues",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
                Check(captured.EndResidualPercent==2.34&&s.EndResidualPercent==4,"editor captures without mutating unsaved live settings");
            }
            Console.WriteLine("END_RESIDUAL_SETTINGS_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_SETTINGS_WRITTEN=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
