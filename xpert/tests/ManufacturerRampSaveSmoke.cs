using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Xiongxin.OWTS.Desktop;

internal static class ManufacturerRampSaveSmoke
{
    static int checks;
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool value,string why) { checks++; if(!value)throw new Exception(why); }
    static bool Reject(Action action) { try { action(); return false; } catch(InvalidOperationException) { return true; } }
    static AppSettings Capture(SettingsForm form)
    { return (AppSettings)typeof(SettingsForm).GetMethod("CaptureValues",Private).Invoke(form,null); }
    static IEnumerable<Control> Controls(Control parent)
    { foreach(Control c in parent.Controls) { yield return c; foreach(var nested in Controls(c))yield return nested; } }
    // Fake authorization of this isolated test window only. This does not read,
    // guess, reset, or verify a real maintenance credential.
    static void GrantForTest(SettingsForm form,SettingsSection section)
    {
        var grants=(HashSet<SettingsSection>)typeof(SettingsForm).GetField("maintenanceGrants",Private).GetValue(form);
        grants.Add(section);
        typeof(SettingsForm).GetMethod("OpenSection",Private).Invoke(form,new object[]{section});
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Check(SettingsAccessControl.Enabled,"test compiled with maintenance guard");
            string root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            string path=Path.Combine(root,"settings.xml");
            var actual=new HardwareStatusSnapshot { HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,
                FromLiveIntegrationProcess=true,StatusCode="Ready",ControllerSerial="0000000001",AcquisitionSerial="QA000/0001",
                HubMac="001122334455",CheckedUtc=DateTime.UtcNow };
            var settings=new AppSettings { DataRoot=root };
            settings.Hardware.Enabled=true;settings.Hardware.SelectedHubMac=actual.HubMac;
            settings.Hardware.ControllerSerial=actual.ControllerSerial;settings.Hardware.AcquisitionSerial=actual.AcquisitionSerial;
            settings.Hardware.ControllerLocation="TEST-ART";settings.Hardware.AcquisitionLocation="TEST-PICO";
            settings.MaximumVoltageKv=30;settings.DividerRatio=11000;
            DeviceParameterProfiles.Confirm(settings,actual);settings.Save(path);
            Check(!settings.DeviceProfileEditAuthorized&&!settings.ExpertDeviceProfileEditAuthorized,"save clears transient grants");
            var original=AppSettings.Load(path);
            string before=AppSettings.FileDigest(path);
            var unauthorized=original.Clone();unauthorized.PressureRampStepV=.05;
            Check(Reject(()=>unauthorized.Save(path)),"ordinary save cannot change pair ramp");
            Check(AppSettings.FileDigest(path)==before,"rejection preserves prior file");
            var inherited=original.Clone();inherited.DeviceProfileEditAuthorized=true;inherited.ExpertDeviceProfileEditAuthorized=true;
            using(var routine=new SettingsForm(inherited)) {
                var captured=Capture(routine);
                Check(!captured.DeviceProfileEditAuthorized&&!captured.ExpertDeviceProfileEditAuthorized,"routine capture discards inherited grants");
            }
            using(var form=new SettingsForm(original,SettingsSection.Expert)) {
                Check(!Controls(form).Any(c=>c.Name=="PressureRampStepV"),"locked expert entry exposes no editable step");
                GrantForTest(form,SettingsSection.Expert);
                ((NumericUpDown)Controls(form).Single(c=>c.Name=="PressureRampStepV")).Value=.05m;
                ((NumericUpDown)Controls(form).Single(c=>c.Name=="PressureRampIntervalMs")).Value=1m;
                var captured=Capture(form);
                Check(captured.ExpertDeviceProfileEditAuthorized&&!captured.DeviceProfileEditAuthorized,"expert-only grant is field scoped");
                captured.Save(path);
                Check(captured.PressureRampStepV==.05&&captured.PressureRampIntervalMs==1,"legal screenshot values saved unchanged");
                Check(!captured.ExpertDeviceProfileEditAuthorized,"successful save clears expert grant");
            }
            var saved=AppSettings.Load(path);
            Check(saved.PressureRampStepV==.05&&saved.PressureRampIntervalMs==1,"speed survives reload");
            Check(saved.MaximumVoltageKv==30&&saved.DividerRatio==11000,"critical parameters unchanged");
            Check(DeviceParameterProfiles.IsConfigured(saved,actual),"pair and reviewed parameter hash remain consistent");
            Check(!File.ReadAllText(path).Contains("ExpertDeviceProfileEditAuthorized"),"grant not serialized");
            var stale=saved.Clone();
            saved.PressureRampIntervalMs=7;saved.ExpertDeviceProfileEditAuthorized=true;saved.Save(path);
            stale.PressureRampIntervalMs=9;stale.ExpertDeviceProfileEditAuthorized=true;
            before=AppSettings.FileDigest(path);
            Check(Reject(()=>stale.Save(path)),"stale expert window cannot overwrite newer pair");
            Check(AppSettings.FileDigest(path)==before,"stale rejection is atomic");
            var limits=AppSettings.Load(path);limits.PressureRampStepV=.02;limits.MaximumVoltageKv=60;limits.ExpertDeviceProfileEditAuthorized=true;
            Check(Reject(()=>limits.Save(path)),"expert grant cannot change device limit");
            Check(AppSettings.FileDigest(path)==before,"limit rejection preserves settings");
            var unrelated=AppSettings.Load(path);unrelated.EndResidualPercent=5;unrelated.ExpertDeviceProfileEditAuthorized=true;
            Check(Reject(()=>unrelated.Save(path)),"speed grant does not authorize unrelated profile fields");
            var record=AppSettings.Load(path);var xml=new XmlDocument();xml.LoadXml(record.DeviceProfilesXml);
            ((XmlElement)xml.DocumentElement.FirstChild).SetAttribute("MaximumVoltageKv","60");
            record.DeviceProfilesXml=xml.OuterXml;record.MaximumVoltageKv=60;record.PressureRampStepV=.02;record.ExpertDeviceProfileEditAuthorized=true;
            Check(Reject(()=>record.Save(path)),"expert grant cannot replace its comparison record");
            Check(AppSettings.FileDigest(path)==before,"record tamper rejection preserves settings");
            var device=AppSettings.Load(path);device.MaximumVoltageKv=60;device.DeviceProfileEditAuthorized=true;device.Save(path);
            Check(AppSettings.Load(path).MaximumVoltageKv==60,"separate critical-device grant still works");
            Console.WriteLine("MANUFACTURER_RAMP_SAVE_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;ProductEntryStarted=false;RealCredentialsRead=false;UserSettingsWritten=false");
            return 0;
        } catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
