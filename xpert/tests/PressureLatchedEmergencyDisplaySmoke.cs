using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Managed fake input reports only. No SDK, native worker or hardware access.
internal static class PressureLatchedEmergencyDisplaySmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static Type type;static int checks;
    static object Call(object c,string name,params object[] a){return type.GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==a.Length).Invoke(c,a);}
    static object Get(object c,string name){return type.GetProperty(name,Flags).GetValue(c,null);}
    static object Field(object c,string name){return type.GetField(name,Flags).GetValue(c);}
    static void Field(object c,string name,object value){type.GetField(name,Flags).SetValue(c,value);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static DateTime NewSample(){Thread.Sleep(2);return DateTime.UtcNow;}
    [STAThread] static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);
            string dll=String.Equals(Path.GetExtension(root),".dll",StringComparison.OrdinalIgnoreCase)?root:Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll");
            string folder=Path.GetDirectoryName(dll);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            var assembly=Assembly.LoadFrom(dll);
            type=assembly.GetType("HVX.Controls.PressureTestControl",true);
            var theme=assembly.GetType("HVX.Controls.PressureTestTheme",true);
            Color green=(Color)theme.GetField("Success",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).GetValue(null);
            Color red=(Color)theme.GetField("Danger",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).GetValue(null);
            Color neutral=(Color)theme.GetField("SecondaryInk",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).GetValue(null);
            using(var form=new Form{ClientSize=new Size(1366,768),Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
            using(var c=(Control)Activator.CreateInstance(type)) {
                c.Dock=DockStyle.Fill;form.Controls.Add(c);form.Show();Application.DoEvents();
                long source=(long)Call(c,"BeginP04FeedbackSource");
                long requests=Convert.ToInt64(Get(c,"PressureRequestSequence"));
                var label=(Control)Field(c,"highVoltageLabel");
                Call(c,"ReportLatchedP04StatusForSource",source,false,NewSample());Application.DoEvents();
                Check(label.Text.Contains("已按下")&&label.BackColor==green,"fresh fault-latched LOW is displayed as physical LOW");
                Check(label.ForeColor==Color.White,"fault-latched status remains white text");
                Check(!(bool)Get(c,"IsHighVoltageReady"),"fault-latched LOW never permits output");
                Call(c,"ReportLatchedP04StatusForSource",source,true,NewSample());Application.DoEvents();
                Check(label.Text.Contains("已解除")&&label.Text.Contains("仍锁存")&&label.BackColor==red,"fresh physical HIGH displayed without erasing fault latch");
                Check(!(bool)Get(c,"IsHighVoltageReady"),"display-only HIGH never renews output permission");
                Check((DateTime)Get(c,"P04CapturedUtc")==DateTime.MinValue,"display sample not ordinary permission sample");
                Check(!(bool)Call(c,"AcknowledgePressureEmergency"),"display-only HIGH cannot acknowledge into output permission");
                Field(c,"p04LatchedDisplayReceived",Stopwatch.GetTimestamp()-3*Stopwatch.Frequency);
                Call(c,"RenderCurrentP04Feedback");
                Check(label.Text.Contains("反馈待确认")&&label.BackColor==neutral,"expired physical display is honestly unconfirmed");
                Check(!(bool)Get(c,"IsHighVoltageReady"),"expired display cannot permit output");
                Call(c,"ResetP04FeedbackSession");
                long next=(long)Call(c,"BeginP04FeedbackSource");
                Call(c,"ReportLatchedP04StatusForSource",source,true,NewSample());Application.DoEvents();
                Check(!(bool)Field(c,"p04LatchedDisplayKnown"),"old source cannot publish into a new session");
                Call(c,"ReportP04StatusForSource",next,true,true,NewSample());Application.DoEvents();
                Check(!(bool)Get(c,"IsHighVoltageReady"),"new ordinary HIGH alone still cannot clear emergency latch");
                Check((bool)Call(c,"AcknowledgePressureEmergency"),"explicit acknowledgement requires new ordinary current sample");
                Check(Convert.ToInt64(Get(c,"PressureRequestSequence"))==requests,"display and acknowledgement never issue a pressure request");
            }
            Console.WriteLine("PASS "+checks+" | HardwareAccessed=False; UserProjectsWritten=False");return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
