using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Xiongxin.OWTS.Desktop;

internal static class DeviceFinePressureSmoke
{
    static int checks;
    static void Check(bool ok,string message){++checks;if(!ok)throw new Exception(message);}
    static bool Reject(Action action){try{action();return false;}catch(Exception){return true;}}
    static HardwareStatusSnapshot Actual(string art="0000000001",string pico="QA000/0001")
    {return new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",FromLiveIntegrationProcess=true,
        ControllerSerial=art,AcquisitionSerial=pico,HubMac="001122334455",CheckedUtc=DateTime.UtcNow};}
    static void Bind(AppSettings s,HardwareStatusSnapshot a){s.Hardware.Enabled=true;s.Hardware.SelectedHubMac=a.HubMac;s.Hardware.ControllerSerial=a.ControllerSerial;s.Hardware.AcquisitionSerial=a.AcquisitionSerial;s.Hardware.ControllerLocation="TEST-ART";s.Hardware.AcquisitionLocation="TEST-PICO";}
    static System.Collections.Generic.IEnumerable<Control> Controls(Control p){foreach(Control c in p.Controls){yield return c;foreach(var d in Controls(c))yield return d;}}
    static bool Valid(AppSettings s){string message;return s.TryValidate(out message);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.xml");
            var s=new AppSettings{DataRoot=root};var a=Actual();Bind(s,a);
            Check(!s.EnableFinePressureRamp&&s.FinePressureThresholdAutomatic&&s.FinePressureThresholdKv==4.5,"new configuration off,automatic15percent30kV");
            s.MaximumVoltageKv=60;Check(s.FinePressureThresholdKv==9,"automatic follows changed maximum60");
            s.MaximumVoltageKv=300;Check(s.FinePressureThresholdKv==45&&Valid(s),"selector above30 not silently clamped");
            foreach(double max in new[]{.000001,1,30,60,200,1000000}) {
                s.MaximumVoltageKv=max;Check(Valid(s)&&Math.Abs(s.FinePressureThresholdKv/max-.15)<1e-12,"finite automatic edge "+max);
            }
            s.MaximumVoltageKv=30;s.EnableFinePressureRamp=true;
            DeviceParameterProfiles.Confirm(s,a);s.Save(path);
            var loaded=AppSettings.Load(path);
            Check(loaded.EnableFinePressureRamp&&loaded.FinePressureThresholdAutomatic&&loaded.FinePressureThresholdKv==4.5,"save/reopen auto");
            loaded.MaximumVoltageKv=60;loaded.DeviceProfileEditAuthorized=true;loaded.Save(path);
            Check(AppSettings.Load(path).FinePressureThresholdKv==9,"new maximum auto synced into pair");
            loaded=AppSettings.Load(path);loaded.DeviceProfileEditAuthorized=true;loaded.FinePressureThresholdAutomatic=false;loaded.FinePressureThresholdKv=2.123456789;loaded.Save(path);
            loaded.MaximumVoltageKv=30;loaded.DeviceProfileEditAuthorized=true;loaded.Save(path);
            var manual=AppSettings.Load(path);
            Check(!manual.FinePressureThresholdAutomatic&&manual.FinePressureThresholdKv==2.123456789,"manual exact threshold not overwritten by maximum changes");
            string hash=DeviceSetupStatus.ParameterHash(manual);var modified=manual.Clone();modified.EnableFinePressureRamp=false;
            Check(hash!=DeviceSetupStatus.ParameterHash(modified),"mode change invalidates prepared session hash");
            modified=manual.Clone();modified.FinePressureThresholdKv=2;
            Check(hash!=DeviceSetupStatus.ParameterHash(modified),"threshold change invalidates prepared session hash");
            modified=manual.Clone();modified.FinePressureThresholdAutomatic=true;
            Check(hash!=DeviceSetupStatus.ParameterHash(modified),"auto/manual mode tracked independently");
            Check(!DeviceParameterProfiles.IsConfigured(modified,a),"unsaved changed parameters cannot authorize pressure");
            foreach(double value in new[]{Double.NaN,Double.PositiveInfinity,0,-1,30.000001}) {
                modified=manual.Clone();modified.FinePressureThresholdKv=value;Check(!Valid(modified),"invalid explicit value rejected "+value);
            }
            var b=Actual("0000000002","QA000/0002");Bind(manual,b);
            manual.FinePressureThresholdAutomatic=true;manual.MaximumVoltageKv=60;manual.EnableFinePressureRamp=false;
            DeviceParameterProfiles.Confirm(manual,b);manual.Save(path);
            Bind(manual,a);Check(DeviceParameterProfiles.Load(manual,a),"restore first pair");
            Check(manual.EnableFinePressureRamp&&!manual.FinePressureThresholdAutomatic&&manual.FinePressureThresholdKv==2.123456789,"first pair exact manual values restored");
            Bind(manual,b);Check(DeviceParameterProfiles.Load(manual,b),"restore second pair");
            Check(!manual.EnableFinePressureRamp&&manual.FinePressureThresholdAutomatic&&manual.FinePressureThresholdKv==9,"second pair auto value independent");
            var legacy=manual.Clone();var doc=new XmlDocument();doc.LoadXml(legacy.DeviceProfilesXml);
            foreach(XmlElement e in doc.DocumentElement.ChildNodes)foreach(string name in new[]{"EnableFinePressureRamp","FinePressureThresholdAutomatic","FinePressureThresholdKv"})e.RemoveAttribute(name);
            legacy.DeviceProfilesXml=doc.OuterXml;legacy.EnableFinePressureRamp=true;legacy.FinePressureThresholdAutomatic=false;legacy.FinePressureThresholdKv=27;
            Bind(legacy,a);Check(DeviceParameterProfiles.Load(legacy,a),"old record loads without new attributes");
            Check(!legacy.EnableFinePressureRamp&&legacy.FinePressureThresholdAutomatic&&legacy.FinePressureThresholdKv==4.5,"legacy30kV derives4.5 disabled, not previous pair27");
            Bind(legacy,b);Check(DeviceParameterProfiles.Load(legacy,b)&&legacy.FinePressureThresholdKv==9,"legacy60kV derives9");
            var corrupt=legacy.Clone();doc.LoadXml(corrupt.DeviceProfilesXml);((XmlElement)doc.DocumentElement.FirstChild).SetAttribute("EnableFinePressureRamp","true");corrupt.DeviceProfilesXml=doc.OuterXml;
            Check(Reject(()=>DeviceParameterProfiles.Has(corrupt,DeviceParameterProfiles.Key(a))),"partial new fields fail closed");
            corrupt=manual.Clone();doc.LoadXml(corrupt.DeviceProfilesXml);((XmlElement)doc.DocumentElement.LastChild).SetAttribute("FinePressureThresholdKv","NaN");corrupt.DeviceProfilesXml=doc.OuterXml;
            Check(Reject(()=>DeviceParameterProfiles.Has(corrupt,DeviceParameterProfiles.Key(b))),"invalid serialized automatic snapshot rejected, not hidden by derived getter");
            foreach(bool zh in new[]{true,false})using(var form=new DeviceParametersForm(new AppSettings{DataRoot=root},DeviceParameterProfiles.Key(a),false,zh)) {
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
                var all=Controls(form).ToList();var enabled=(CheckBox)all.Single(c=>c.Name=="EnableFinePressureRamp");
                var automatic=(CheckBox)all.Single(c=>c.Name=="FinePressureThresholdAutomatic");var threshold=(NumericUpDown)all.Single(c=>c.Name=="FinePressureThresholdKv");
                var maximum=(NumericUpDown)all.Single(c=>c.Name=="MaximumVoltageKv");
                Check(!enabled.Checked&&automatic.Checked&&!threshold.Enabled,"editor default off/automatic");
                enabled.Checked=true;Check(!threshold.Enabled&&automatic.Enabled,"auto threshold not manually editable");
                maximum.Value=60;Check(form.Capture().FinePressureThresholdKv==9&&threshold.Value==9,"editor displays changed15percent");
                automatic.Checked=false;threshold.Value=7.123456m;maximum.Value=100;
                Check(form.Capture().FinePressureThresholdKv==7.123456&&!form.Capture().FinePressureThresholdAutomatic,"editor manual override persists when maximum changes");
                enabled.Checked=false;Check(!threshold.Enabled&&form.Capture().FinePressureThresholdKv==7.123456,"disable preserves explicit value");
                enabled.Checked=true;automatic.Checked=true;Check(form.Capture().FinePressureThresholdKv==15,"operator may restore automatic mode");
                Check(all.OfType<Label>().Any(c=>c.Text.Contains(zh?"目标峰值":"target peak")),"peak kV explicit in label");
            }
            var state=new ApplicationState();state.SetHardware(b);
            using(var host=new MainForm(manual,state,true)) {
                var draft=(AppSettings)typeof(MainForm).GetMethod("DeviceParameterDraft",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(host,new object[]{Actual("0000000003","QA000/0003")});
                Check(!draft.EnableFinePressureRamp&&draft.FinePressureThresholdAutomatic&&draft.FinePressureThresholdKv==draft.MaximumVoltageKv*.15,"new pair gets automatic neutral defaults, not previous pair");
            }
            Console.WriteLine("DEVICE_FINE_PRESSURE_PASS="+checks+"; HardwareAccessed=False; UserSettingsWritten=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
