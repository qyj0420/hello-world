using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class CycleSettingsLimitSmoke
{
    static int checks;
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Field(object target,string name){return target.GetType().GetField(name,Hidden).GetValue(target);}
    static Control[] Tree(Control control){return new[]{control}.Concat(control.Controls.Cast<Control>().SelectMany(Tree)).ToArray();}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var defaults=new AppSettings();Check(defaults.LocationSaveCycleCount==1&&defaults.PressureRangeEvaluationCycleCount==1,"both defaults are one");
            string path=Path.Combine(qa,"missing.xml");File.WriteAllText(path,"<HVXUnifiedDesktopSettings/>");
            AppSettings loaded;string message;Check(AppSettings.TryLoad(path,out loaded,out message),"legacy missing fields load");
            Check(loaded.LocationSaveCycleCount==1&&loaded.PressureRangeEvaluationCycleCount==1,"missing XML fields default one");
            foreach(int count in new[]{1,2,5})
            {
                var valid=new AppSettings{LocationSaveCycleCount=count,PressureRangeEvaluationCycleCount=count};
                Check(valid.TryValidate(out message),"valid cycle count "+count);valid.Save(path);
                loaded=AppSettings.Load(path);Check(loaded.LocationSaveCycleCount==count&&loaded.PressureRangeEvaluationCycleCount==count,"cycle count persists "+count);
                Check(valid.Clone().LocationSaveCycleCount==count&&valid.Clone().PressureRangeEvaluationCycleCount==count,"clone preserves valid cycles "+count);
            }
            foreach(int count in new[]{0,-1,6,101})foreach(bool range in new[]{false,true})
            {var invalid=new AppSettings();if(range)invalid.PressureRangeEvaluationCycleCount=count;else invalid.LocationSaveCycleCount=count;
                Check(!invalid.TryValidate(out message)&&message.Contains("1 至 5"),"new invalid edit rejected, not silently migrated "+count);
                bool rejected=false;try{invalid.Save(path);}catch(InvalidDataException){rejected=true;}Check(rejected,"invalid edit cannot be saved "+count);}
            foreach(int count in new[]{6,8,17,100})
            {
                string legacy=Path.Combine(qa,"legacy-"+count+".xml");
                File.WriteAllText(legacy,"<HVXUnifiedDesktopSettings><LocationSaveCycleCount>"+count+"</LocationSaveCycleCount><PressureRangeEvaluationCycleCount>"+count+"</PressureRangeEvaluationCycleCount></HVXUnifiedDesktopSettings>");
                byte[] before=File.ReadAllBytes(legacy);Check(AppSettings.TryLoad(legacy,out loaded,out message),"old valid cycle values migrate "+count);
                Check(loaded.LocationSaveCycleCount==5&&loaded.PressureRangeEvaluationCycleCount==5,"legacy values capped five "+count);
                Check(message.Contains("调整为 5")&&loaded.CycleCountMigrationHistory.Contains("LocationSaveCycleCount="+count+"->5")&&
                    loaded.CycleCountMigrationHistory.Contains("PressureRangeEvaluationCycleCount="+count+"->5"),"migration records actual fields and old values "+count);
                Check(before.SequenceEqual(File.ReadAllBytes(legacy))&&!loaded.Hardware.Enabled,"load performs no settings overwrite or hardware enable "+count);
                string history=loaded.CycleCountMigrationHistory;Check(loaded.Clone().CycleCountMigrationHistory==history,"clone retains migration history "+count);
                loaded.Save(legacy);loaded=AppSettings.Load(legacy);
                Check(loaded.LocationSaveCycleCount==5&&loaded.PressureRangeEvaluationCycleCount==5&&loaded.CycleCountMigrationHistory==history,"save persists exact history without repeated migration "+count);
            }
            foreach(string code in new[]{"zh-CN","en","zh-TW"})
            using(var form=new SettingsForm(new AppSettings{LanguageCode=code}))
            {
                form.AuditEnabled=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();
                foreach(string field in new[]{"locationSaveCycles"})
                {var numeric=(NumericUpDown)Field(form,field);Check(numeric.Minimum==1&&numeric.Maximum==5&&numeric.DecimalPlaces==0&&numeric.Value==1,"UI bounded/default cycle "+field+" "+code);
                    numeric.Value=5;Check(numeric.Value==5,"UI accepts maximum five "+field+" "+code);}
                Check(!Tree(form).Any(c=>c.Name=="PressureRangeEvaluationCycleCount"),"no independent range-cycle editor "+code);
                var captured=(AppSettings)typeof(SettingsForm).GetMethod("CaptureValues",Hidden).Invoke(form,null);
                Check(captured.LocationSaveCycleCount==5&&captured.PressureRangeEvaluationCycleCount==5,"single setting also updates legacy compatibility field "+code);
                var texts=Tree(form).Select(c=>c.Text??"").ToArray();
                Check(!texts.Any(t=>t.Contains("仅接入零压输入")||t.Contains("Only zero-voltage acquisition and range recapture")||t.Contains("僅接入零壓輸入")),"whole zero-input explanation removed "+code);
                var note=Tree(form).Single(c=>c.Name=="LocationCycleCountNote");
                Check(note.Text.Contains("1")&&note.Text.Contains("5"),"natural memory/count guidance present "+code);
                if(code=="en")Check(note.Text.Contains("memory")&&!note.Text.Any(c=>c>='\u4e00'&&c<='\u9fff'),"English new guidance has no untranslated Chinese");
                if(code=="zh-TW")Check(note.Text.Contains("記憶體")||note.Text.Contains("內存")||note.Text.Contains("佔用"),"traditional new guidance converted");
                form.Close();Application.DoEvents();
            }
            Console.WriteLine("Cycle settings: "+checks+" offline checks PASS; defaults1/max5/config/migration/three-language UI; HardwareAccessed=False; SDKCalled=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
