using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class FirstRunLayoutSmoke
{
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
            var flags=BindingFlags.Static|BindingFlags.NonPublic;
            MethodInfo prepare=typeof(DpiLayoutSmoke).GetMethod("Prepare",flags);
            MethodInfo check=typeof(DpiLayoutSmoke).GetMethod("CheckButtons",flags);
            MethodInfo save=typeof(DpiLayoutSmoke).GetMethod("Save",flags);
            foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f})
            using(var form=new FirstRunForm())
            {
                prepare.Invoke(null,new object[]{form,scale,new Size(600,420)});
                check.Invoke(null,new object[]{form,"first-run isolated scale="+scale});
                save.Invoke(null,new object[]{form,Path.Combine(output,"first-run-"+scale.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+".png")});
                Console.WriteLine("PASS first-run scale="+scale+" client="+form.ClientSize);
            }
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
