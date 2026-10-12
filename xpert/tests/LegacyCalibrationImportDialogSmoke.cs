using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class LegacyCalibrationImportDialogSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Type dialogType;static string qa;
    static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    static object Get(object o,string n){var f=o.GetType().GetField(n,B);return f!=null?f.GetValue(o):o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void Wait(Func<bool> ready){var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<10000){Application.DoEvents();System.Threading.Thread.Sleep(5);}Check(ready(),"asynchronous UI completes within offline test deadline");}
    static void CallTask(object o,string method){var task=(Task)o.GetType().GetMethod(method,B).Invoke(o,null);Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static string Project(string name)
    {
        string p=Path.Combine(qa,name);Directory.CreateDirectory(p);File.WriteAllText(Path.Combine(p,"ITEM.in"),"start\tQA\r\n");
        File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t405.3m\r\n");
        var text=new StringBuilder("Date\t2026/08/26\r\nTime\t19:48\r\nOld path\t旧项目\\原文件\r\nCable length\t405.0\r\nV (m/usec)\t169.60\r\nCal.value\t5000\r\nWideband\t0\r\nStart_point\t0.000000936\r\nFinal_point\t0.000005712\r\nSR(N)\t125000004\r\nNI_Calib_factor\t1845.1965311409\r\nFilterstate\t12\r\nCalib_Attenuation\t1092.335777\r\n");
        for(int i=0;i<96;i++)text.AppendLine((Math.Sin(i*.31)*.123).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(p,"ALL_5nC.cal"),text.ToString(),Encoding.GetEncoding(936));return p;
    }
    static Form Dialog(string p,bool zh,Action validate=null)
    {
        var form=(Form)Activator.CreateInstance(dialogType,B,null,new object[]{p,"A","PICO-QA","ART-QA",true,zh,validate??(()=>{})},null);
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-24000,-24000);form.ShowInTaskbar=false;form.Show();
        Wait(()=>((ComboBox)Get(form,"candidates")).Items.Count>0);return form;
    }
    static int Count(string p){return Directory.Exists(Path.Combine(p,"Calibration"))?Directory.GetFiles(Path.Combine(p,"Calibration"),"*.hvlegacy",SearchOption.AllDirectories).Length:0;}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            qa=Path.Combine(Path.GetFullPath(args[1]),"legacy-dialog");Directory.CreateDirectory(qa);
            var host=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"Xiongxin.OWTS.Desktop.exe"));dialogType=host.GetType("Xiongxin.OWTS.Desktop.LegacyCalibrationImportDialog",true);
            string p=Project("accept"),file=Path.Combine(p,"ALL_5nC.cal");byte[] original=File.ReadAllBytes(file);
            using(var form=Dialog(p,true)) {
                var check=(CheckBox)Get(form,"confirmation");var apply=(Button)Get(form,"apply");var details=(TextBox)Get(form,"details");
                Check(form.Icon!=null,"owned dialog carries project icon");Check(!check.Checked&&!apply.Enabled,"confirmation starts unchecked and use disabled");
                Check(details.Text.Contains("1845.19653114")&&details.Text.Contains("169.6")&&
                    details.Text.Contains("当前项目电缆长度：405.3 m")&&details.Text.Contains("旧记录电缆长度：405.0 m"),
                    "operator sees actual coefficient, reference speed and distinct one-decimal current/historical lengths");
                Check(details.Text.Contains("未经本次重新测量")&&details.Text.Contains("未经本次核验")&&details.Text.Contains("急停"),"operator sees limits of old evidence and retained safety conditions");
                Check(check.Text.Contains("系数")&&check.Text.Contains("参考波速")&&check.Text.Contains("当前设备"),"explicit decision covers coefficient and reference velocity for current pair/circuit");
                CallTask(form,"ConfirmSelectedAsync");Check(Count(p)==0&&Get(form,"AcceptedBinding")==null,"unchecked call cannot save or accept");
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(qa,"confirmation-cn.png"));}
                check.Checked=true;Check(apply.Enabled,"checked operator enables explicit confirmation button");CallTask(form,"ConfirmSelectedAsync");
                Check(form.DialogResult==DialogResult.OK&&Get(form,"AcceptedBinding")!=null&&Count(p)==1,"explicit confirmation persists one binding and returns success");
            }
            Check(original.SequenceEqual(File.ReadAllBytes(file)),"dialog never rewrites original CAL");
            string cancelled=Project("cancel");using(var form=Dialog(cancelled,false)) {
                var check=(CheckBox)Get(form,"confirmation");var details=(TextBox)Get(form,"details");
                Check(!check.Checked&&details.Text.Contains("not remeasured now")&&details.Text.Contains("not been verified"),"English confirmation retains evidence caveat and unchecked default");
                form.ClientSize=new Size(650,550);Application.DoEvents();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(qa,"confirmation-en.png"));}
                Check(((Button)Get(form,"apply")).Parent.ClientRectangle.Contains(((Button)Get(form,"apply")).Bounds),"confirm button remains visible at smaller layout");
                form.DialogResult=DialogResult.Cancel;form.Close();Check(Get(form,"AcceptedBinding")==null&&Count(cancelled)==0,"cancel leaves original-only project and no applicability");
            }
            string changed=Project("source-changed");using(var form=Dialog(changed,true)) {
                ((CheckBox)Get(form,"confirmation")).Checked=true;File.AppendAllText(Path.Combine(changed,"ALL_5nC.cal"),"\r\n");CallTask(form,"ConfirmSelectedAsync");
                Check(Get(form,"AcceptedBinding")==null&&Count(changed)==0&&!((CheckBox)Get(form,"confirmation")).Checked,"source change after display rejects save and resets confirmation");
            }
            bool current=true;string switched=Project("identity-changed");using(var form=Dialog(switched,true,()=>{if(!current)throw new InvalidOperationException("identity changed");})) {
                ((CheckBox)Get(form,"confirmation")).Checked=true;current=false;CallTask(form,"ConfirmSelectedAsync");
                Check(Get(form,"AcceptedBinding")==null&&Count(switched)==0,"device/context validation failure before save writes no decision");
            }
            Console.WriteLine("LEGACY_CALIBRATION_IMPORT_DIALOG_PASS="+checks+"; HARDWARE_COMMANDS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
