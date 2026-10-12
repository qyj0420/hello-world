using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class PressurePreferencesSettingsSmoke
{
    static int checks;
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Field(object value,string name){return value.GetType().GetField(name,Hidden).GetValue(value);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Hidden).Invoke(value,args);}
    sealed class Probe:UserControl
    {
        public double FrequencyReminderMinHz{get;set;}public double FrequencyReminderMaxHz{get;set;}public double FrequencyDangerThresholdHz{get;set;}
        public double CapacitanceNormalMinUf{get;set;}public double CapacitanceNormalMaxUf{get;set;}public bool FrequencyReminderEnabled{get;set;}
        public int AutomaticPressureCountdownSeconds{get;set;}public bool AutomaticPressureCountdownEnabled{get;set;}public int LocationSaveCycleCount{get;set;}
        public bool UseCompensationCapacitance{get;set;}public double CompensationCapacitanceUf{get;set;}
        public bool AutomaticRangeReminderEnabled{get;set;}public bool AnalyzeZeroVoltageData{get;set;}
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string qa=Path.Combine(Path.GetFullPath(args[0]),"pressure-settings-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            string path=Path.Combine(qa,"settings.xml");File.WriteAllText(path,"<HVXUnifiedDesktopSettings/>");
            AppSettings legacy;string error;Check(AppSettings.TryLoad(path,out legacy,out error),"legacy file accepted");Defaults(legacy);Defaults(new AppSettings());Defaults(new AppSettings().Clone());
            var current=new AppSettings{FrequencyReminderMinHz=25,FrequencyReminderMaxHz=450,FrequencyDangerThresholdHz=800,CapacitanceNormalMinUf=.03,CapacitanceNormalMaxUf=9,FrequencyReminderEnabled=false,AutomaticPressureCountdownSeconds=0,AutomaticPressureCountdownEnabled=false,LocationSaveCycleCount=5,UseCompensationCapacitance=true,CompensationCapacitanceUf=.3};
            current.Save(path);var saved=AppSettings.Load(path);Check(saved.FrequencyReminderMinHz==25&&saved.FrequencyReminderMaxHz==450&&saved.FrequencyDangerThresholdHz==800,"frequency thresholds persisted");
            Check(saved.CapacitanceNormalMinUf==.03&&saved.CapacitanceNormalMaxUf==9,"capacitance thresholds persisted");
            Check(!saved.FrequencyReminderEnabled&&!saved.AutomaticPressureCountdownEnabled&&saved.AutomaticPressureCountdownSeconds==1&&saved.LocationSaveCycleCount==5,"legacy zero normalized to one; reminder restoration and cycles persisted");
            Check(saved.UseCompensationCapacitance&&saved.CompensationCapacitanceUf==.3,"compensation choice and amount persisted");
            Check(!saved.AutomaticRangeReminderEnabled,"automatic range defaults to applying ordinary suggestions");
            foreach(var mutate in new Action<AppSettings>[] {s=>s.FrequencyReminderMinHz=Double.NaN,s=>s.FrequencyReminderMaxHz=s.FrequencyReminderMinHz,s=>s.FrequencyDangerThresholdHz=s.FrequencyReminderMaxHz,s=>s.CapacitanceNormalMinUf=s.CapacitanceNormalMaxUf,s=>s.CompensationCapacitanceUf=-.1,s=>s.AutomaticPressureCountdownSeconds=-1,s=>s.AutomaticPressureCountdownSeconds=11,s=>s.LocationSaveCycleCount=0,s=>s.LocationSaveCycleCount=101})
            {var bad=saved.Clone();mutate(bad);Check(!bad.TryValidate(out error),"invalid preference rejected before save");}
            foreach(int seconds in new[]{1,2,10}){var value=saved.Clone();value.AutomaticPressureCountdownSeconds=seconds;value.LocationSaveCycleCount=5;Check(value.TryValidate(out error),"inclusive delay bounds and integer cycles valid");}
            using(var probe=new Probe())using(var host=new ModuleHost(current,new ApplicationState(),new Diagnostics(qa),Path.Combine(qa,"modules")))
            {
                var descriptor=ModuleHost.Descriptors.Single(d=>d.Id==ModuleHost.PressureTest);
                Call(host,"ApplyContext",descriptor,probe,false);
                Check(probe.FrequencyReminderMinHz==25&&probe.FrequencyReminderMaxHz==450&&probe.FrequencyDangerThresholdHz==800,"host maps frequency thresholds");
                Check(probe.CapacitanceNormalMinUf==.03&&probe.CapacitanceNormalMaxUf==9&&probe.UseCompensationCapacitance&&probe.CompensationCapacitanceUf==.3,"host maps capacitance and compensation");
                Check(probe.LocationSaveCycleCount==5&&probe.AutomaticPressureCountdownSeconds==1&&!probe.FrequencyReminderEnabled&&!probe.AutomaticPressureCountdownEnabled,"host maps cycle/delay/reminder settings without forcing a hidden window on");
                current.FrequencyReminderMaxHz=490;current.LocationSaveCycleCount=3;Call(host,"ApplyContext",descriptor,probe,false);Check(probe.FrequencyReminderMaxHz==490&&probe.LocationSaveCycleCount==3,"preference change alone invalidates context cache");
                current.AutomaticRangeReminderEnabled=true;current.AnalyzeZeroVoltageData=true;Call(host,"ApplyContext",descriptor,probe,false);
                Check(probe.AutomaticRangeReminderEnabled&&probe.AnalyzeZeroVoltageData,"new options alone invalidate cached host context");
                probe.FrequencyReminderEnabled=true;probe.AutomaticPressureCountdownEnabled=true;probe.UseCompensationCapacitance=false;
                PressurePreferencesPersistence.Save(probe,current,s=>s.Save(path));Check(current.FrequencyReminderEnabled&&current.AutomaticPressureCountdownEnabled&&!current.UseCompensationCapacitance,"explicit choices updated after persistence");
                saved=AppSettings.Load(path);Check(saved.FrequencyReminderEnabled&&saved.AutomaticPressureCountdownEnabled&&!saved.UseCompensationCapacitance&&saved.CompensationCapacitanceUf==.3,"both restore and suppress captured without changing amount");
                Check(saved.AutomaticRangeReminderEnabled&&current.AutomaticRangeReminderEnabled&&saved.AnalyzeZeroVoltageData,"range reminder and zero-data choice persist to local configuration");
                byte[] savedBytes=File.ReadAllBytes(path);
                probe.FrequencyReminderEnabled=false;probe.AutomaticPressureCountdownEnabled=false;probe.AutomaticRangeReminderEnabled=false;bool failed=false;try{PressurePreferencesPersistence.Save(probe,current,s=>{throw new IOException("isolated injected write failure");});}catch(IOException){failed=true;}
                Check(failed&&current.FrequencyReminderEnabled&&current.AutomaticPressureCountdownEnabled&&current.AutomaticRangeReminderEnabled,"failed persistence cannot claim saved preference");
                Check(savedBytes.SequenceEqual(File.ReadAllBytes(path)),"failed suppression preserves exact committed configuration bytes");
                Call(host,"ApplyPressurePreferences",probe);
                Check(probe.FrequencyReminderEnabled&&probe.AutomaticPressureCountdownEnabled&&probe.AutomaticRangeReminderEnabled,"host rollback helper restores all three reminder choices after failed suppression");
            }
            ReminderRoundTrips(qa);
            foreach(string language in new[]{"zh-CN","en","zh-TW"})foreach(float fontSize in new[]{9.5f,15f,20f})
            using(var font=new Font("Microsoft YaHei UI",fontSize))using(var form=new SettingsForm(new AppSettings{LanguageCode=language}))
            {
                form.AuditEnabled=false;SetFont(form,font);form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Size=new Size(900,640);form.Show();Application.DoEvents();
                var tabs=(TabControl)Field(form,"tabs");tabs.SelectedIndex=0;Application.DoEvents();
                var rows=(TableLayoutPanel)((Control)Field(form,"frequencyReminderMin")).Parent;
                var captured=(AppSettings)Call(form,"CaptureValues");Defaults(captured);
                foreach(Control child in rows.Controls)
                {
                    if(child is Label&&rows.GetColumnSpan(child)==1)
                    {Check(child.Right<=250,"general label wraps before editor "+language+" "+fontSize+" "+child.Text);Check(child.Height>=child.GetPreferredSize(new Size(child.Width,0)).Height,"complete wrapped label height");}
                    if(child is CheckBox&&child.Name.Length>0)
                    {int height=TextRenderer.MeasureText(child.Text,child.Font,new Size(Math.Max(100,child.Width-28),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+4;Check(child.Height>=height,"checkbox full text fits "+child.Name);}
                }
                var saveCycles=(NumericUpDown)Field(form,"locationSaveCycles");Check(saveCycles.Minimum==1&&saveCycles.Maximum==5&&saveCycles.DecimalPlaces==0,"location cycles integer-only input with memory guard");
                var countdown=(NumericUpDown)Field(form,"pressureCountdownSeconds");Check(countdown.Minimum==1&&countdown.Maximum==10&&countdown.DecimalPlaces==0,"UI offers a one-second minimum without removing the hide preference");
                Check(((CheckBox)Field(form,"pressureCountdownCheck")).Enabled,"countdown visibility remains editable in general settings");
                saveCycles.Value=5;((CheckBox)Field(form,"frequencyReminderCheck")).Checked=false;((CheckBox)Field(form,"useCompensationCapacitance")).Checked=true;((NumericUpDown)Field(form,"compensationCapacitance")).Value=.4m;
                ((CheckBox)Field(form,"automaticRangeReminder")).Checked=true;((CheckBox)Field(form,"analyzeZeroVoltageData")).Checked=true;
                captured=(AppSettings)Call(form,"CaptureValues");Check(captured.LocationSaveCycleCount==5&&!captured.FrequencyReminderEnabled&&captured.UseCompensationCapacitance&&captured.CompensationCapacitanceUf==.4,"UI captures ordinary settings");
                Check(captured.AutomaticRangeReminderEnabled&&captured.AnalyzeZeroVoltageData,"settings UI captures both explicit opt-in choices");
                Check(!captured.Hardware.Enabled&&!DeviceSetupStatus.ParametersSaved(captured)&&String.IsNullOrEmpty(captured.DeviceProfilesXml),"ordinary reminder edits do not enable hardware or fabricate device configuration");
                tabs.TabPages[0].ScrollControlIntoView(saveCycles);Application.DoEvents();var bounds=saveCycles.RectangleToScreen(saveCycles.ClientRectangle);Check(tabs.TabPages[0].RectangleToScreen(tabs.TabPages[0].ClientRectangle).Contains(bounds),"last numeric control can scroll fully into view");
                using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(qa,language+"-"+fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));}
                form.Close();
            }
            Console.WriteLine("PRESSURE_SETTINGS_PASS="+checks);Console.WriteLine("HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False;EVIDENCE="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static void SetFont(Control c,Font f){c.Font=f;foreach(Control child in c.Controls)SetFont(child,f);}
    static int ReminderMask(AppSettings s){return (s.FrequencyReminderEnabled?1:0)|(s.AutomaticPressureCountdownEnabled?2:0)|(s.AutomaticRangeReminderEnabled?4:0);}
    static int ReminderMask(Probe p){return (p.FrequencyReminderEnabled?1:0)|(p.AutomaticPressureCountdownEnabled?2:0)|(p.AutomaticRangeReminderEnabled?4:0);}
    static void ReminderRoundTrips(string qa)
    {
        foreach(string language in new[]{"zh-CN","en"})foreach(int mask in Enumerable.Range(0,8))
        {
            string path=Path.Combine(qa,"reminders-"+language+"-"+mask+".xml");
            var source=new AppSettings {LanguageCode=language,AutomaticPressureCountdownSeconds=7,FrequencyReminderEnabled=(mask&1)!=0,AutomaticPressureCountdownEnabled=(mask&2)!=0,AutomaticRangeReminderEnabled=(mask&4)!=0};
            source.Save(path);AppSettings loaded;string message;
            Check(AppSettings.TryLoad(path,out loaded,out message)&&ReminderMask(loaded)==mask,"all independent reminder choices persist "+language+" "+mask);
            using(var form=new SettingsForm(loaded))
            {
                form.AuditEnabled=false;
                var frequency=(CheckBox)Field(form,"frequencyReminderCheck");var countdown=(CheckBox)Field(form,"pressureCountdownCheck");var range=(CheckBox)Field(form,"automaticRangeReminder");
                Check(frequency.Checked==source.FrequencyReminderEnabled&&countdown.Checked==source.AutomaticPressureCountdownEnabled&&range.Checked==source.AutomaticRangeReminderEnabled,"general settings load saved suppression/restoration exactly "+mask);
                Check(frequency.Enabled&&countdown.Enabled&&range.Enabled,"ordinary reminder visibility can be restored without a password "+mask);
                frequency.Checked=!frequency.Checked;countdown.Checked=!countdown.Checked;range.Checked=!range.Checked;
                var edited=(AppSettings)Call(form,"CaptureValues");
                Check(ReminderMask(edited)==(mask^7)&&edited.AutomaticPressureCountdownSeconds==7,"changing visibility preserves the longer configured wait "+mask);
                Check(!edited.Hardware.Enabled&&!DeviceSetupStatus.ParametersSaved(edited),"visibility edit grants no device permission "+mask);
                edited.Save(path);
            }
            Check(AppSettings.TryLoad(path,out loaded,out message)&&ReminderMask(loaded)==(mask^7)&&loaded.AutomaticPressureCountdownSeconds==7,"opposite reminder choices survive restart "+mask);
            using(var reopened=new SettingsForm(loaded))
            {
                reopened.AuditEnabled=false;
                Check(((CheckBox)Field(reopened,"frequencyReminderCheck")).Checked==loaded.FrequencyReminderEnabled&&((CheckBox)Field(reopened,"pressureCountdownCheck")).Checked==loaded.AutomaticPressureCountdownEnabled&&((CheckBox)Field(reopened,"automaticRangeReminder")).Checked==loaded.AutomaticRangeReminderEnabled,"reopened general page displays restored state "+mask);
            }
            using(var probe=new Probe())using(var host=new ModuleHost(loaded,new ApplicationState(),new Diagnostics(qa),Path.Combine(qa,"modules")))
            {
                var descriptor=ModuleHost.Descriptors.Single(d=>d.Id==ModuleHost.PressureTest);
                Call(host,"ApplyContext",descriptor,probe,false);
                Check(ReminderMask(probe)==ReminderMask(loaded)&&probe.AutomaticPressureCountdownSeconds==7,"host sends persisted visibility and unchanged wait to module "+mask);
                loaded.AutomaticPressureCountdownEnabled=!loaded.AutomaticPressureCountdownEnabled;Call(host,"ApplyContext",descriptor,probe,false);
                Check(ReminderMask(probe)==ReminderMask(loaded),"countdown-only change invalidates host context cache "+mask);
                loaded.AutomaticRangeReminderEnabled=!loaded.AutomaticRangeReminderEnabled;Call(host,"ApplyContext",descriptor,probe,false);
                Check(ReminderMask(probe)==ReminderMask(loaded),"range-only change invalidates host context cache "+mask);
                loaded.FrequencyReminderEnabled=!loaded.FrequencyReminderEnabled;Call(host,"ApplyContext",descriptor,probe,false);
                Check(ReminderMask(probe)==ReminderMask(loaded),"frequency-only change invalidates host context cache "+mask);
                int originalMask=ReminderMask(loaded);byte[] before=File.ReadAllBytes(path);
                probe.FrequencyReminderEnabled=!loaded.FrequencyReminderEnabled;probe.AutomaticPressureCountdownEnabled=!loaded.AutomaticPressureCountdownEnabled;probe.AutomaticRangeReminderEnabled=!loaded.AutomaticRangeReminderEnabled;
                bool failed=false;try{PressurePreferencesPersistence.Save(probe,loaded,s=>{throw new IOException("synthetic no-write failure");});}catch(IOException){failed=true;}
                Check(failed&&ReminderMask(loaded)==originalMask&&before.SequenceEqual(File.ReadAllBytes(path)),"failed suppression or restoration changes neither memory nor saved bytes "+mask);
                Call(host,"ApplyPressurePreferences",probe);
                Check(ReminderMask(probe)==originalMask&&probe.AutomaticPressureCountdownSeconds==7,"failure restoration helper restores module state without shortening wait "+mask);
                Check(!loaded.Hardware.Enabled,"all preference mapping stays offline "+mask);
            }
        }
        using(var oldZero=new SettingsForm(new AppSettings{AutomaticPressureCountdownSeconds=0,AutomaticPressureCountdownEnabled=false}))
        {
            oldZero.AuditEnabled=false;
            Check(((NumericUpDown)Field(oldZero,"pressureCountdownSeconds")).Value==1,"legacy zero displays new minimum one second");
            Check(!((CheckBox)Field(oldZero,"pressureCountdownCheck")).Checked,"legacy hidden countdown is not silently re-enabled");
            var edited=(AppSettings)Call(oldZero,"CaptureValues");Check(edited.AutomaticPressureCountdownSeconds==1&&!edited.AutomaticPressureCountdownEnabled,"explicit settings capture upgrades old zero without showing hidden countdown");
            Call(oldZero,"OpenSection",SettingsSection.Expert);Call(oldZero,"OpenSection",SettingsSection.Device);
            Check((bool)Field(oldZero,"expertUnlocked")&&(bool)Field(oldZero,"deviceUnlocked"),"this validation edition still opens parameter editors without obsolete password gates");
            Check(!((AppSettings)Call(oldZero,"CaptureValues")).Hardware.Enabled,"opening password-free editors still grants no hardware operation");
        }
    }
    static void Defaults(AppSettings s)
    {
        Check(s.FrequencyReminderMinHz==20&&s.FrequencyReminderMaxHz==500&&s.FrequencyDangerThresholdHz==850,"default frequency range 20/500/850");
        Check(s.CapacitanceNormalMinUf==.02&&s.CapacitanceNormalMaxUf==8,"default capacitance .02–8 μF");
        Check(s.AutomaticPressureCountdownSeconds==1&&s.AutomaticPressureCountdownEnabled&&s.FrequencyReminderEnabled,"default reminder and countdown");
        Check(s.LocationSaveCycleCount==1&&!s.UseCompensationCapacitance&&s.CompensationCapacitanceUf==.2,"default cycles and 200 nF compensation disabled");
        Check(!s.AutomaticRangeReminderEnabled,"ordinary automatic range advice uses automatic next-round setting by default");
    }
}
