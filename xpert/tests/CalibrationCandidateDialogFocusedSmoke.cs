using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Test-only host boundaries. This executable never starts the desktop product.
namespace Xiongxin.OWTS.Desktop
{
    internal static class AppTheme {internal static Icon LoadIcon(){return null;}}
    internal sealed class Diagnostics {
        internal static readonly List<string> Failures=new List<string>();
        internal void Error(string area,string message,Exception error){Failures.Add(message+" "+error.Message);}
    }
}
internal static class CalibrationCandidateDialogFocusedSmoke
{
    [STAThread]static int Main(string[] args)
    {
        try {
            var type=typeof(Xiongxin.OWTS.Desktop.LegacyCalibrationImportDialog);
            using(var dialog=new Xiongxin.OWTS.Desktop.LegacyCalibrationImportDialog(args[0],"ALL","CURRENT-PICO","CURRENT-ART",false,true,()=>{})) {
                var run=(Task)type.GetMethod("LoadCandidatesAsync",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(dialog,null);
                DateTime until=DateTime.UtcNow.AddSeconds(20);
                while(!run.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException();Application.DoEvents();Thread.Sleep(1);}run.GetAwaiter().GetResult();
                var candidates=(ComboBox)type.GetField("candidates",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(dialog);
                var details=(TextBox)type.GetField("details",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(dialog);
                if(candidates.Items.Count!=2)throw new Exception("Formal charge archive and legacy text must both be offered; TDR/corrupt formal excluded.");
                if(!details.Text.Contains("不会修改旧文件")||!details.Text.Contains("未经本次核验")||details.Text.Contains("OFFLINE-SOURCE")||details.Text.Contains("CURRENT-PICO")||details.Text.Contains("首达点"))throw new Exception("Customer details must preserve the applicability caveat without internal identities or timing implementation.");
                if(details.Text.Contains("ALL_TDR.cal")||details.Text.Contains("A_5nC.cal"))throw new Exception("Internal per-file reject reasons must remain in diagnostics, not customer details.");
                if(!Xiongxin.OWTS.Desktop.Diagnostics.Failures.Exists(x=>x.Contains("ALL_TDR.cal"))||!Xiongxin.OWTS.Desktop.Diagnostics.Failures.Exists(x=>x.Contains("A_5nC.cal")))throw new Exception("Rejected filenames missing from diagnostics.");
                if(dialog.AcceptedBinding!=null||dialog.PersistedBinding!=null)throw new Exception("Listing cannot confirm parameter reuse.");
            }
            Console.WriteLine("CALIBRATION_CANDIDATE_DIALOG_PASS=5; PRODUCT_STARTED=0; SDK_CALLS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
