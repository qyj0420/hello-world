using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.CalibrationStorage;
using Xiongxin.OWTS.Desktop;

internal static class LegacyCalibrationPostWriteContextSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,B).GetValue(value);}
    static void Wait(Func<bool> done){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<10000){Application.DoEvents();System.Threading.Thread.Sleep(5);}Check(done(),"bounded synthetic operation completed");}
    static void Confirm(LegacyCalibrationImportDialog form){var task=(Task)form.GetType().GetMethod("ConfirmSelectedAsync",B).Invoke(form,null);Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static int Count(string project){return Directory.Exists(Path.Combine(project,"Calibration"))?Directory.GetFiles(Path.Combine(project,"Calibration"),"*.hvlegacy",SearchOption.AllDirectories).Length:0;}
    static string Project(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"start\tQA\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t405.3m\r\n");
        var text=new StringBuilder("Date\t2026/08/26\r\nTime\t19:48\r\nOld path\tfixture\r\nCable length\t405.0\r\nV (m/usec)\t169.60\r\nCal.value\t5000\r\nWideband\t0\r\nStart_point\t0.000000936\r\nFinal_point\t0.000005712\r\nSR(N)\t125000004\r\nNI_Calib_factor\t1845.1965311409\r\nFilterstate\t12\r\nCalib_Attenuation\t1092.335777\r\n");
        for(int i=0;i<96;i++)text.AppendLine((Math.Sin(i*.31)*.123).ToString("R",CultureInfo.InvariantCulture));
        string path=Path.Combine(root,"ALL_5nC.cal");File.WriteAllText(path,text.ToString(),Encoding.GetEncoding(936));return path;
    }
    static LegacyCalibrationImportDialog Dialog(string project,bool zh,Action validate)
    {
        var form=new LegacyCalibrationImportDialog(project,"A","PICO-QA","ART-QA",true,zh,validate);
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-24000,-24000);form.ShowInTaskbar=false;form.Show();
        Wait(()=>Field<ComboBox>(form,"candidates").Items.Count>0);return form;
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);typeof(AppPaths).GetField("LocalDataDirectory",B).SetValue(null,Path.Combine(qa,"profile"));
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            foreach(bool zh in new[]{true,false}) {
                string project=Path.Combine(qa,zh?"post-cn":"post-en"),cal=Project(project);byte[] original=File.ReadAllBytes(cal);
                int calls=0;bool armed=false,saved=false;Action validate=()=>{if(armed&&++calls==2){saved=Count(project)==1;throw new InvalidOperationException("QA context changed after completed persistence");}};
                using(var form=Dialog(project,zh,validate)) {
                    Field<CheckBox>(form,"confirmation").Checked=true;armed=true;Confirm(form);string status=Field<Label>(form,"status").Text;
                    Check(calls==2&&saved,"context changed exactly after one completed write");
                    Check(form.AcceptedBinding==null&&form.PersistedBinding!=null&&form.DialogResult!=DialogResult.OK,"saved record is separate from current application");
                    Check(zh?status.Contains("已保存")&&status.Contains("未应用")&&!status.Contains("确认未完成"):status.Contains("was saved")&&status.Contains("not applied"),"operator sees saved but not applied status in each language");
                    Check(!Field<Button>(form,"apply").Enabled&&!Field<ComboBox>(form,"candidates").Enabled&&!Field<CheckBox>(form,"confirmation").Enabled,"saved stale dialog cannot confirm again");
                    Field<CheckBox>(form,"confirmation").Checked=true;Confirm(form);Check(Count(project)==1&&!Field<Button>(form,"apply").Enabled,"reentrant or stale checked event cannot append duplicate record");
                    var found=LegacyCalibrationBinding.FindForProject(project,"A",true,"PICO-QA","ART-QA");Check(found!=null&&found.BindingPath==form.PersistedBinding.BindingPath,"original pair can rediscover truthful saved binding");
                    Check(LegacyCalibrationBinding.FindForProject(project,"A",true,"OTHER-PICO","ART-QA")==null,"other Pico remains rejected");
                    Check(LegacyCalibrationBinding.FindForProject(project,"A",true,"PICO-QA","OTHER-ART")==null,"other ART remains rejected");
                    Check(original.SequenceEqual(File.ReadAllBytes(cal)),"original CAL unchanged");
                    File.WriteAllText(Path.Combine(qa,zh?"post-save-cn.txt":"post-save-en.txt"),status,Encoding.UTF8);
                    form.Close();Check(Count(project)==1,"closing preserves append-only audit record");
                }
            }
            string pre=Path.Combine(qa,"before");Project(pre);bool fail=false;using(var form=Dialog(pre,true,()=>{if(fail)throw new InvalidOperationException("QA pre-write context changed");})){
                Field<CheckBox>(form,"confirmation").Checked=true;fail=true;Confirm(form);
                Check(Count(pre)==0&&form.PersistedBinding==null&&form.AcceptedBinding==null,"pre-write change persists no record");
                Check(Field<Label>(form,"status").Text.Contains("确认未完成")&&!Field<CheckBox>(form,"confirmation").Checked,"pre-write failure asks for fresh review");
            }
            string io=Path.Combine(qa,"write-failure");Project(io);using(var form=Dialog(io,true,()=>{})){
                File.WriteAllText(Path.Combine(io,"Calibration"),"QA file deliberately occupies directory name");Field<CheckBox>(form,"confirmation").Checked=true;Confirm(form);
                Check(Count(io)==0&&form.PersistedBinding==null&&form.AcceptedBinding==null,"IO failure is not described as saved");
                Check(Field<Label>(form,"status").Text.Contains("确认未完成"),"write failure remains honest");
            }
            string success=Path.Combine(qa,"success");Project(success);using(var form=Dialog(success,true,()=>{})){
                Field<CheckBox>(form,"confirmation").Checked=true;Confirm(form);
                Check(form.DialogResult==DialogResult.OK&&form.PersistedBinding!=null&&Object.ReferenceEquals(form.AcceptedBinding,form.PersistedBinding)&&Count(success)==1,"success saves and applies once");
            }
            Console.WriteLine("LEGACY_POST_WRITE_CONTEXT_PASS="+checks+";HARDWARE_ACCESSED=False;USER_PROJECTS_WRITTEN=False;USER_SETTINGS_WRITTEN=False");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
