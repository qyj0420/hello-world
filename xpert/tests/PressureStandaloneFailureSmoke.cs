using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PressureStandaloneFailureSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    [STAThread]static int Main(string[] args){try{
        string root=Path.GetFullPath(args[0]);
        var runtime=Assembly.LoadFrom(Path.Combine(root,"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe"));
        var factory=runtime.GetType("Xiongxin.FactoryTest.Program",true);
        var method=factory.GetMethod("CreateWorkerStartInfo",F);
        Check(method!=null,"worker process path must be an explicit testable adapter, not Application.ExecutablePath");
        var info=(ProcessStartInfo)method.Invoke(null,new object[]{"Pressure",@"D:\offline QA\factory-settings.xml",@"D:\offline QA",123});
        Check(info.FileName==runtime.Location&&info.FileName!=Application.ExecutablePath,"loaded task39 runtime launches itself, never this host");
        Check(info.Arguments=="--worker Pressure \"D:\\offline QA\\factory-settings.xml\" \"D:\\offline QA\" 123","unchanged task39 command and parent contract");
        Check(!info.UseShellExecute&&info.CreateNoWindow&&info.WindowStyle==ProcessWindowStyle.Hidden,"hidden supervised child");
        Application.EnableVisualStyles();
        var assembly=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        var type=assembly.GetType("HVX.Controls.PressureTestControl",true);
        using(var form=new Form{ClientSize=new Size(1380,860),Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false})
        using(var c=(Control)Activator.CreateInstance(type)){
            form.Controls.Add(c);c.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
            var button=(Control)Field(c,"pressureActionButton");var status=(Control)Field(c,"highVoltageLabel");
            Call(c,"SetPressureState","FAULT",Double.NaN);Call(c,"UpdatePressureUi");
            int changes=0;button.TextChanged+=delegate{changes++;};
            for(int i=0;i<10;i++)Call(c,"UpdatePressureUi");
            Check(changes==0,"unchanged fault refresh never toggles between two captions");
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Application.DoEvents();
            Check(status.BackColor==Color.FromArgb(status.BackColor.A,status.BackColor.R,status.BackColor.G,status.BackColor.B),"status color available");
            var theme=assembly.GetType("HVX.Controls.PressureTestTheme",true);
            Color red=(Color)theme.GetField("Danger",F).GetValue(null),green=(Color)theme.GetField("Success",F).GetValue(null);
            Check(status.BackColor==red,"released is red (HV permission not safe access)");
            Rectangle before=button.RectangleToScreen(button.ClientRectangle);
            Field(c,"pressureActionFullMessage","");Call(c,"ArrangePressureMessage");Call(c,"RefreshPressureSidebarLayout");Application.DoEvents();
            before=button.RectangleToScreen(button.ClientRectangle);
            foreach(string message in new[]{"急停状态未知",new string('错',1000),"", "采集失败，请查看调试信息"}){
                Field(c,"pressureActionFullMessage",message);Call(c,"ArrangePressureMessage");Call(c,"RefreshPressureSidebarLayout");Application.DoEvents();
                Check(button.RectangleToScreen(button.ClientRectangle)==before,"message content cannot move pressure button");
            }
            Call(c,"ReportP04Status",true,false,DateTime.UtcNow);Application.DoEvents();
            Check(status.BackColor==green,"pressed is green with pressure disabled");
            Call(c,"ReportP04Status",false,false,DateTime.UtcNow);Application.DoEvents();
            Check(status.BackColor!=red&&status.BackColor!=green,"unknown is neither permissive red nor pressed green");
            Field(c,"lastPostProcessError","SYNTHETIC_WORKER_START_FAILURE");Call(c,"SetPostProcessStateNoLock","ACQUISITION_FAILED");Call(c,"UpdatePostProcessUi");
            Check(((string)Field(c,"pressureActionFullMessage")).Contains("SYNTHETIC_WORKER_START_FAILURE"),"real capture error remains visible instead of generic emergency hint");
        }
        var host=Assembly.LoadFrom(Path.Combine(root,"Xiongxin.OWTS.Desktop.exe"));
        var debug=host.GetType("Xiongxin.OWTS.Desktop.PressureDebugForm",true);
        Directory.CreateDirectory(args[1]);string log=Path.Combine(args[1],"fake.log");
        File.WriteAllText(log,new string('a',150000)+"\r\nREAL_ERROR\r\n");
        string tail=(string)debug.GetMethod("ReadTail",F).Invoke(null,new object[]{log});
        Check(tail.Length<=131072&&tail.Contains("REAL_ERROR"),"bounded read-only log tail retains recent error");
        Console.WriteLine("PRESSURE_STANDALONE_FAILURE_CHECKS="+checks+";HardwareAccessed=false;ProductEntryExecuted=false;ProcessesStarted=0");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
