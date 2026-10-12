using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class SettingsFeedbackSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static object Field(object owner,string name) { return owner.GetType().GetField(name,Hidden).GetValue(owner); }
    static object Call(object owner,string name,params object[] args) { return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args); }
    static void Check(bool ok,string message) { checks++; if(!ok)throw new Exception(message); }
    static System.Collections.Generic.IEnumerable<Control> Tree(Control root) { yield return root; foreach(Control c in root.Controls)foreach(Control nested in Tree(c))yield return nested; }
    static bool Han(string text) { return text!=null&&text.Any(c=>c>='\u4e00'&&c<='\u9fff'); }
    static AppSettings Capture(SettingsForm form) { return (AppSettings)Call(form,"CaptureValues"); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            var source=new AppSettings { LanguageCode="en",DataRoot=Path.Combine(qa,"项目資料"),ReportOutputPath=Path.Combine(qa,"用户 reports"),DividerRatio=12.345678901,PressureRatio=6000.123456789,InductanceHenry=1.150000123,InductorDcResistanceOhm=26.123456789,MaximumVoltageKv=30.123456789,PdCalibrationCorrectionFactor=1.00000123,OscillatingPdCaptureDurationMs=59.123456789,CompensationCapacitanceUf=.200000123 };
            source.PressureRatio=DeviceParameterProfiles.PressureRatioFor(source.MaximumVoltageKv);
            source.TaskMinus3.EndpointPolicy="2.123456;1.234567;OR;AND";
            using(var form=new SettingsForm(source))
            {
                form.AuditEnabled=false;
                Call(form,"OpenSection",SettingsSection.PlusData);Check((bool)Field(form,"plusDataUnlocked"),"Plus Data opens");
                Call(form,"OpenSection",SettingsSection.SystemAdmin);Check((bool)Field(form,"systemAdminUnlocked"),"administration opens");
                Console.WriteLine("OBSERVED_EN_SAVE="+((Button)form.AcceptButton).Text);
                Console.WriteLine("OBSERVED_EN_PLUS_HAN="+Tree((Control)Field(form,"plusDataTab")).Count(c=>(c is Label||c is ButtonBase||c is TabPage)&&Han(c.Text)));
                Check(((Button)form.AcceptButton).Text=="Save settings","English save caption");
                // Isolated fixture-only editor construction; does not alter production authorization.
                var panel=(Control)Call(form,"BuildAcquisitionPanel");var device=(TabPage)Field(form,"deviceTab");device.Controls.Clear();device.Controls.Add(panel);
                form.GetType().GetField("deviceUnlocked",Hidden).SetValue(form,true);Call(form,"LoadDeviceValues",source);
                Call(form,"ApplySettingsLanguage");
                File.WriteAllLines(Path.Combine(qa,"english-untranslated.txt"),new[]{(Control)form,device,(Control)Field(form,"systemAdminTab")}.SelectMany(Tree).Where(c=>(c is Label||c is ButtonBase||c is TabPage||c is Form)&&Han(c.Text)).Select(c=>c.Text).Distinct(),System.Text.Encoding.UTF8);
                foreach(var page in new[]{(Control)form,device,(Control)Field(form,"systemAdminTab")})
                    foreach(var c in Tree(page).Where(c=>c is Label||c is ButtonBase||c is TabPage||c is Form))Check(!Han(c.Text),"English program caption: "+c.Text);
                var selector=Field(form,"minus3AlgorithmIndex") as ComboBox;
                int[] indices={5,7,8,9};string[] methodNames={"A","B","E","E+"};
                Check(selector!=null&&selector.DropDownStyle==ComboBoxStyle.DropDownList&&selector.Items.Count==4,"real A/B/E/E+ methods are selectable");
                Check(selector.Items.Cast<object>().Select(x=>x.ToString()).SequenceEqual(methodNames),"operator method names are A/B/E/E+");
                var language=(ComboBox)Field(form,"language");
                foreach(string code in new[]{"zh-CN","en","zh-TW","en","fr","en"})
                {
                    language.SelectedIndex=AppLanguages.SelectionIndex(code);Application.DoEvents();
                    for(int i=0;i<indices.Length;i++){selector.SelectedIndex=i;Check(Capture(form).TaskMinus3.AlgorithmIndex==indices[i],"method mapping preserved "+code+" "+i);}
                    var captured=Capture(form);Check(captured.DataRoot==source.DataRoot&&captured.ReportOutputPath==source.ReportOutputPath,"user paths not translated");
                    Check(captured.PressureRatio==DeviceParameterProfiles.PressureRatioFor(captured.MaximumVoltageKv),"pressure ratio follows exact 0-5 V limit mapping "+code);
                    Check(captured.DividerRatio==source.DividerRatio&&captured.PressureRatio==source.PressureRatio&&captured.InductanceHenry==source.InductanceHenry&&captured.InductorDcResistanceOhm==source.InductorDcResistanceOhm&&captured.MaximumVoltageKv==source.MaximumVoltageKv&&captured.PdCalibrationCorrectionFactor==source.PdCalibrationCorrectionFactor&&captured.OscillatingPdCaptureDurationMs==source.OscillatingPdCaptureDurationMs&&captured.CompensationCapacitanceUf==source.CompensationCapacitanceUf,"unmodified double precision retained "+code);
                    Check(captured.TaskMinus3.EndpointPolicy==source.TaskMinus3.EndpointPolicy,"unmodified endpoint policy retained exactly");
                    if(code=="en")Check(((Button)form.AcceptButton).Text=="Save settings","English survives repeated switching");
                }
                Check(((NumericUpDown)Field(form,"dividerRatio")).DecimalPlaces==2&&((NumericUpDown)Field(form,"inductance")).DecimalPlaces==2,"normal settings display two decimals");
                ((NumericUpDown)Field(form,"dividerRatio")).Value=14.25m;
                Check(Capture(form).DividerRatio==14.25&&Capture(form).InductanceHenry==source.InductanceHenry,"editing one value does not round other values");
                var feedback=(ComboBox)Field(form,"hvupVoltageSource");Check(feedback.Items[0].ToString()=="DIV"&&feedback.Items[1].ToString()=="Vmp","feedback display identifiers exact");
                for(int i=0;i<2;i++){feedback.SelectedIndex=i;Check(Capture(form).HvupVoltageSource==(i==0?HvupVoltageSource.Divider:HvupVoltageSource.VMonitor),"feedback mapping preserved");}
                for(int i=0;i<indices.Length;i++)
                {
                    selector.SelectedIndex=i;var value=Capture(form);string path=Path.Combine(qa,"method-"+i+".xml");value.Save(path);var loaded=AppSettings.Load(path);Check(loaded.TaskMinus3.AlgorithmIndex==indices[i],"method persists "+i);
                    using(var reopened=new SettingsForm(loaded)){reopened.AuditEnabled=false;Call(reopened,"OpenSection",SettingsSection.PlusData);Check((bool)Field(reopened,"plusDataUnlocked"),"reopened settings");Check(((ComboBox)Field(reopened,"minus3AlgorithmIndex")).SelectedIndex==i,"reopened method "+i);}
                }
                var invalid=source.Clone();invalid.TaskMinus3.AlgorithmIndex=10;string error;Check(!invalid.TryValidate(out error),"unsupported method refused");
                invalid.TaskMinus3.AlgorithmIndex=6;Check(invalid.TryValidate(out error),"legacy method selection retained for unrelated settings edits");
                using(var legacy=new SettingsForm(invalid)){legacy.AuditEnabled=false;Call(legacy,"OpenSection",SettingsSection.PlusData);Check(((ComboBox)Field(legacy,"minus3AlgorithmIndex")).SelectedIndex==-1,"legacy method7 is not relabeled B");}
                Check(!source.Hardware.Enabled&&!Capture(form).Hardware.Enabled,"hardware remains offline");
                var tabs=(TabControl)Field(form,"tabs");tabs.SelectedTab=(TabPage)Field(form,"plusDataTab");((TabControl)tabs.SelectedTab.Controls[0]).SelectedIndex=1;
                selector.SelectedIndex=1;
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();Application.DoEvents();
                Check(selector.Text=="B","selected B caption is retained after native control creation");
                var number=(NumericUpDown)Field(form,"inductance");number.Focus();Application.DoEvents();selector.Focus();Application.DoEvents();Check(Capture(form).InductanceHenry==source.InductanceHenry,"focusing and leaving does not truncate source precision");
                using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(qa,"settings-en.png"));}form.Close();
            }
            Console.WriteLine("SETTINGS_FEEDBACK_PASS="+checks+";HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
