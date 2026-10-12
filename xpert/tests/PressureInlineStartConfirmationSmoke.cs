using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class PressureInlineStartConfirmationSmoke
{
    static Type type;
    static int checks;
    static string qa;
    static object Get(object c, string name) { return type.GetProperty(name).GetValue(c, null); }
    static void Set(object c, string name, object value) { type.GetProperty(name).SetValue(c, value, null); }
    static object Call(object c, string name, params object[] args) { return type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(c,args); }
    static object Field(object c,string name) { return type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c); }
    static void Check(bool value,string message) { ++checks; if(!value)throw new Exception(message); }
    static void Pump(int ms=25) { var watch=Stopwatch.StartNew();do {Application.DoEvents();Thread.Sleep(2);}while(watch.ElapsedMilliseconds<ms); }
    static Button Button(Control c,string name) { return c.Controls.Find(name,true).OfType<Button>().Single(); }
    static void Report(Control c,bool high) { Call(c,"ReportP04Status",true,high,DateTime.UtcNow); }
    static Task<bool> Ask(Control c,CancellationToken token=default(CancellationToken)) { return (Task<bool>)Call(c,"RequestPressureStartConfirmationAsync",Get(c,"CalculatedTargetPeakVoltageKv"),"本轮目标峰值 7.071 kV。\r\n独立确认试品已放电，核对接地、反馈接线、急停和隔离条件，人员已撤离。\r\n正常采集结束不测残压，不能据此接近设备。",token); }
    static void Run(Action<Control> test)
    {
        using(var form=new Form())using(var c=(Control)Activator.CreateInstance(type))
        {
            form.ClientSize=new Size(1280,800);c.Dock=DockStyle.Fill;form.Controls.Add(c);
            Set(c,"ProjectPath",qa);Set(c,"SimulationMode",false);Set(c,"MaximumPeakVoltageKv",20d);
            Set(c,"FrequencyReminderEnabled",false);Set(c,"AutomaticPressureCountdownEnabled",false);
            Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{5d});Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetPressureState","COMPLETE",0d);Set(c,"ActionHandshakeEnabled",true);
            form.Show();Pump();Report(c,true);Call(c,"RequestPressure");
            Check((bool)Get(c,"ActionPending")&&(string)Get(c,"ActionCode")=="PRESSURE","synthetic request published without any backend");
            test(c);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            string modules=Path.Combine(args[0],"modules");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                string name=new AssemblyName(e.Name).Name+".dll";
                string path=Directory.GetFiles(modules,name,SearchOption.AllDirectories).FirstOrDefault();
                return path==null?null:Assembly.LoadFrom(path);
            };
            string dll=Path.Combine(modules,"PressureTest","Xiongxin.OWTS.PressureTest.dll");
            type=Assembly.LoadFrom(dll).GetType("HVX.Controls.PressureTestControl",true);
            Application.EnableVisualStyles();
            Run(c=>{
                var task=Ask(c);Pump(200);Check(!task.IsCompleted,"waiting does not automatically authorize");
                Check(Application.OpenForms.Count==1,"no modal confirmation form");
                Check(Ask(c).Result==false&&!task.IsCompleted,"duplicate request cannot steal original confirmation");
                Call(c,"UpdateActionBusyUi");Check(Button(c,"ConfirmPressureRoundButton").Enabled,"confirmation remains clickable under busy lock");
                Check(c.Controls.Find("AutomaticPostProcessCheckBox",true).Single().Text=="自动保存","auto-save checkbox is not incorrectly removed");
                using(var image=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(image,c.ClientRectangle);image.Save(Path.Combine(qa,"inline-confirmation.png"));}
                Report(c,true);Button(c,"ConfirmPressureRoundButton").PerformClick();
                Check(task.IsCompleted&&task.Result,"explicit current-round click accepts");
                Check(c.Controls.Find("PressureStartConfirmationPanel",true).Length==0,"panel removed after completion");
                var next=Ask(c);Check(!next.IsCompleted,"later confirmation is not inherited");
                Button(c,"CancelPressureRoundButton").PerformClick();Check(next.IsCompleted&&!next.Result,"cancel does not authorize");
            });
            Run(c=>{
                var task=Ask(c);Report(c,false);Pump(150);
                Check(!Button(c,"ConfirmPressureRoundButton").Enabled&&!task.IsCompleted,"active emergency stop disables confirmation");
                Button(c,"ConfirmPressureRoundButton").PerformClick();Check(!task.IsCompleted,"disabled confirmation never accepts");
                Report(c,true);Pump(150);Check(!task.IsCompleted,"fresh recovery never auto-signs");
                type.GetField("p04ReceivedTimestamp",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(c,Stopwatch.GetTimestamp()-3*Stopwatch.Frequency);
                Button(c,"ConfirmPressureRoundButton").PerformClick();Check(!task.IsCompleted,"click rechecks stale status even before UI timer");
                Report(c,true);Pump(150);Button(c,"ConfirmPressureRoundButton").PerformClick();Check(task.Result,"fresh status and fresh explicit click accepts");
            });
            Run(c=>{using(var stop=new CancellationTokenSource()){var task=Ask(c,stop.Token);stop.Cancel();Check(task.IsCompleted&&!task.Result,"cancellation resolves without UI deadlock");Pump(150);Check(Field(c,"pressureStartConfirmationTimer")==null,"cancel releases timer");}});
            Run(c=>{var task=Ask(c);c.Visible=false;Check(task.IsCompleted&&!task.Result,"leaving page cancels");});
            Run(c=>{var task=Ask(c);c.Parent=null;Check(task.IsCompleted&&!task.Result,"reparent cancels");});
            Run(c=>{var task=Ask(c);Call(c,"CompleteAction",Get(c,"ActionSequence"),false,"cancel");Pump(150);Check(task.IsCompleted&&!task.Result,"stale mailbox cancels");});
            Run(c=>{var task=Ask(c);c.Dispose();Check(task.IsCompleted&&!task.Result,"dispose resolves outstanding confirmation");Check(Field(c,"pressureStartConfirmationTimer")==null,"dispose releases timer");});
            Run(c=>{Call(c,"CompleteAction",Get(c,"ActionSequence"),false,"cancel");Check(Ask(c).Result==false,"no pending round cannot request confirmation");});
            Console.WriteLine("PRESSURE_INLINE_START_CONFIRMATION_PASS checks="+checks+"; hardwareAccessed=false");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
