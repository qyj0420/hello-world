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

// Diagnostic repro, not a regression pass: it asserts the currently observed
// inconsistent status after a completed save and a post-write context change.
internal static class LegacyCalibrationPostWriteContextAudit
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static T Field<T>(object value,string name){return (T)value.GetType().GetField(name,B).GetValue(value);}
    static void Wait(Func<bool> done){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<10000){Application.DoEvents();System.Threading.Thread.Sleep(5);}Check(done(),"bounded synthetic UI operation completed");}
    static string CreateProject(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"start\tQA\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t405.3m\r\n");
        var text=new StringBuilder("Date\t2026/08/26\r\nTime\t19:48\r\nOld path\tfixture\r\nCable length\t405.0\r\nV (m/usec)\t169.60\r\nCal.value\t5000\r\nWideband\t0\r\nStart_point\t0.000000936\r\nFinal_point\t0.000005712\r\nSR(N)\t125000004\r\nNI_Calib_factor\t1845.1965311409\r\nFilterstate\t12\r\nCalib_Attenuation\t1092.335777\r\n");
        for(int i=0;i<96;i++)text.AppendLine((Math.Sin(i*.31)*.123).ToString("R",CultureInfo.InvariantCulture));
        string path=Path.Combine(root,"ALL_5nC.cal");File.WriteAllText(path,text.ToString(),Encoding.GetEncoding(936));return path;
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]);Directory.CreateDirectory(qa);
            typeof(AppPaths).GetField("LocalDataDirectory",B).SetValue(null,Path.Combine(qa,"profile"));
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string project=Path.Combine(qa,"post-write-project"),cal=CreateProject(project);byte[] original=File.ReadAllBytes(cal);
            int validateAfterArmed=0;bool armed=false,savedBeforeFailure=false;
            Action validate=()=> {
                if(!armed)return;
                validateAfterArmed++;
                if(validateAfterArmed==2){
                    savedBeforeFailure=Directory.GetFiles(Path.Combine(project,"Calibration"),"*.hvlegacy",SearchOption.AllDirectories).Length==1;
                    throw new InvalidOperationException("QA: current device context changed after the completed write");
                }
            };
            using(var form=new LegacyCalibrationImportDialog(project,"A","PICO-QA","ART-QA",true,true,validate)) {
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-24000,-24000);form.ShowInTaskbar=false;form.Show();
                Wait(()=>Field<ComboBox>(form,"candidates").Items.Count>0);
                Field<CheckBox>(form,"confirmation").Checked=true;armed=true;
                var task=(Task)form.GetType().GetMethod("ConfirmSelectedAsync",B).Invoke(form,null);Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();
                string status=Field<Label>(form,"status").Text;
                Check(validateAfterArmed==2&&savedBeforeFailure,"context failure occurred exactly at post-write validation");
                Check(form.AcceptedBinding==null&&form.DialogResult!=DialogResult.OK,"dialog returns no accepted decision or success");
                Check(status.Contains("确认未完成"),"current UI says confirmation did not complete");
                string[] files=Directory.GetFiles(Path.Combine(project,"Calibration"),"*.hvlegacy",SearchOption.AllDirectories);
                Check(files.Length==1,"one completed append-only binding still exists");
                var found=LegacyCalibrationBinding.FindForProject(project,"A",true,"PICO-QA","ART-QA");
                Check(found!=null&&found.BindingPath==files[0],"returning to original device pair can load this saved binding");
                Check(LegacyCalibrationBinding.FindForProject(project,"A",true,"OTHER-PICO","ART-QA")==null,"different Pico cannot use the binding");
                Check(LegacyCalibrationBinding.FindForProject(project,"A",true,"PICO-QA","OTHER-ART")==null,"different ART cannot use the binding");
                Check(original.SequenceEqual(File.ReadAllBytes(cal)),"synthetic original CAL stays byte-identical");
                File.WriteAllText(Path.Combine(qa,"observed-status.txt"),status+Environment.NewLine+"BindingPath="+files[0]+Environment.NewLine+"OriginalPairFind=true; ChangedPairFind=false",Encoding.UTF8);
                Console.WriteLine("P2_LEGACY_POST_WRITE_CONTEXT_REPRODUCED="+checks+"; UI_STATUS="+status+"; BINDING_COUNT=1; ORIGINAL_PAIR_FIND=true; CHANGED_PAIR_FIND=false; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");
            }
            return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
