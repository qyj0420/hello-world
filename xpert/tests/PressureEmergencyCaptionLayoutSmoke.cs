using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureEmergencyCaptionLayoutSmoke
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static void Set(object value,string name,object setting){value.GetType().GetProperty(name).SetValue(value,setting,null);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(value,args);}
    static void Pump(Control control){for(int n=0;n<3;n++){Call(control,"RefreshPressureSidebarLayout");control.PerformLayout();Application.DoEvents();}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string root=Path.GetFullPath(args[0]);
            Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            foreach(var size in new[]{new Size(1280,600),new Size(1280,480),new Size(1366,648)})
            using(var form=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=size,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
            using(var control=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.PressureTestControl",true)))
            {
                Set(control,"EmbeddedMode",true);Set(control,"LanguageIndex",1);Set(control,"SimulationMode",false);control.Size=size;form.Controls.Add(control);form.Show();Pump(control);
                var status=(Label)control.Controls.Find("HighVoltageStatusLabel",true).Single();
                var measured=TextRenderer.MeasureText(status.Text,status.Font,new Size(status.ClientSize.Width,Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding);
                Check(status.Height>=measured.Height,"unknown emergency caption fits "+size+" actual="+status.Size+" measured="+measured);
                Check(status.Parent.ClientRectangle.Contains(status.Bounds),"status inside action area "+size);
                Check(status.ForeColor==Color.White,"white emergency text retained");
                Check(status.Text.Contains("unknown")||status.Text.Contains("unconfirmed")||status.Text.Contains("pending"),"unknown state not disguised");
                Check((long)control.GetType().GetProperty("PressureRequestSequence").GetValue(control,null)==0L,"layout issues no pressure request");form.Close();
            }
            Console.WriteLine("PRESSURE_EMERGENCY_CAPTION_LAYOUT_PASS="+checks+";HARDWARE=False;SDK=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
