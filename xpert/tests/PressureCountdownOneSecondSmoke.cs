using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Offline current-source controls only. No worker or SDK entry is called.
static class PressureCountdownOneSecondSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Type pressure;
    static void Check(bool yes,string why){checks++;if(!yes)throw new Exception(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){var t=o as Type??o.GetType();var m=t.GetMethods(B).Single(x=>x.Name==n&&x.GetParameters().Length==a.Length&&x.GetParameters().Select((p,i)=>a[i]==null||p.ParameterType.IsInstanceOfType(a[i])).All(y=>y));return m.Invoke(o is Type?null:o,a);}
    static IEnumerable<Control> Tree(Control c){yield return c;foreach(Control x in c.Controls)foreach(var y in Tree(x))yield return y;}
    static void Pump(int ms){var w=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(2);}while(w.ElapsedMilliseconds<ms);}
    static void Wait(Task<bool> t){var w=Stopwatch.StartNew();while(!t.IsCompleted&&w.ElapsedMilliseconds<4000)Pump(10);Check(t.IsCompleted,"countdown has a bounded terminal task");}
    static void Run(string qa,Action<Control> a)
    {
        using(var f=new Form{ClientSize=new Size(1280,760),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
        using(var c=(Control)Activator.CreateInstance(pressure))using(var feedback=new System.Windows.Forms.Timer{Interval=20}){
            c.Dock=DockStyle.Fill;f.Controls.Add(c);Set(c,"EmbeddedMode",true);Set(c,"FrequencyReminderEnabled",false);Set(c,"ProjectPath",qa);
            Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetPressureState","COMPLETE",0d);
            // Synthetic fresh DI drives managed permission tests; no device is opened.
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);feedback.Tick+=delegate{if(!c.IsDisposed&&(bool)Get(c,"AutomaticPressureCountdownPending"))Call(c,"ReportP04Status",true,true,DateTime.UtcNow);};
            f.Show();Pump(30);feedback.Start();try{a(c);}finally{feedback.Stop();f.Close();}
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);if(Directory.Exists(qa))throw new Exception("fresh fixture required");Directory.CreateDirectory(qa);
            var host=Assembly.LoadFrom(Path.Combine(root,"CurrentUiHost.dll"));var settingsType=host.GetType("Xiongxin.OWTS.Desktop.AppSettings",true);
            var settings=Activator.CreateInstance(settingsType);Set(settings,"DataRoot",qa);Check((int)Get(settings,"AutomaticPressureCountdownSeconds")==1,"new settings default is one second");
            string file=Path.Combine(qa,"settings.xml");File.WriteAllText(file,"<HVXUnifiedDesktopSettings/>");var old=Call(settingsType,"Load",file);Check((int)Get(old,"AutomaticPressureCountdownSeconds")==1,"missing historical XML field uses one-second default");
            foreach(int seconds in new[]{1,2,7,10}){
                Set(settings,"AutomaticPressureCountdownSeconds",seconds);Call(settings,"Save",file);var saved=Call(settingsType,"Load",file);
                Check((int)Get(saved,"AutomaticPressureCountdownSeconds")==seconds,"explicit saved wait is preserved: "+seconds);Check((int)Get(Call(saved,"Clone"),"AutomaticPressureCountdownSeconds")==seconds,"clone preserves explicit wait: "+seconds);
            }
            Set(settings,"AutomaticPressureCountdownSeconds",0);Call(settings,"Save",file);Check((int)Get(Call(settingsType,"Load",file),"AutomaticPressureCountdownSeconds")==1,"only historical zero migrates to one");
            foreach(int invalid in new[]{-1,11}){Set(settings,"AutomaticPressureCountdownSeconds",invalid);object[] validate={null};Check(!(bool)settingsType.GetMethod("TryValidate",B).Invoke(settings,validate),"invalid countdown rejected: "+invalid);}
            Set(settings,"AutomaticPressureCountdownSeconds",1);
            var formType=host.GetType("Xiongxin.OWTS.Desktop.SettingsForm",true);
            foreach(string language in new[]{"zh-CN","en","zh-TW"}){
                Set(settings,"LanguageCode",language);using(var form=(Form)Activator.CreateInstance(formType,new[]{settings})){
                    Set(form,"AuditEnabled",false);form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();Pump(30);
                    var numeric=(NumericUpDown)Get(form,"pressureCountdownSeconds");Check(numeric.Minimum==1&&numeric.Maximum==10&&numeric.Value==1,"settings control has one-second integer range: "+language);
                    Check(!Tree(form).Any(c=>c.Name=="PressurePreferencesNote"),"removed explanation has no control/row placeholder: "+language);
                    var grid=(TableLayoutPanel)numeric.Parent;Check(grid.Controls.Cast<Control>().Where(c=>c is Label).Any(c=>c.Text.Contains("1–10")),"localized countdown caption reflects new range: "+language);
                    numeric.Value=2;Check((int)Get(Call(form,"CaptureValues"),"AutomaticPressureCountdownSeconds")==2,"old explicit two seconds remains selectable: "+language);form.Close();
                }
            }
            var cadence=host.GetType("HVX.Acquisition.PicoCaptureCadence",true);
            Check((int)cadence.GetField("MinimumGapMs",B).GetRawConstantValue()==2000,"independent Pico post-capture minimum remains two seconds");
            foreach(double duration in new[]{1d,40d,60d})Check((int)Call(cadence,"CooldownMilliseconds",duration)==2000,"actual finite capture cooldown unchanged: "+duration);
            pressure=Assembly.LoadFrom(Path.Combine(root,"modules/PressureTest/Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
            foreach(bool visible in new[]{true,false})Run(qa,c=>{
                Check((int)Get(c,"AutomaticPressureCountdownSeconds")==1,"new pressure control defaults to one second");Set(c,"AutomaticPressureCountdownEnabled",visible);
                long sequence=(long)Get(c,"PressureRequestSequence");var watch=Stopwatch.StartNew();var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");
                Pump(500);Check(!task.IsCompleted&&(long)Get(c,"PressureRequestSequence")==sequence,"neither visible nor hidden countdown skips required wait");Check((Get(c,"pressureCountdownDialog")!=null)==visible,"visibility only affects countdown window");
                Wait(task);Check(task.Result&&watch.ElapsedMilliseconds>=1000,"one complete second elapsed before next mailbox request");Check((long)Get(c,"PressureRequestSequence")==sequence+1,"exactly one authorized mailbox request, no output driver called");
            });
            Run(qa,c=>{
                Set(c,"AutomaticPressureCountdownEnabled",false);var source=new CancellationTokenSource();long sequence=(long)Get(c,"PressureRequestSequence");var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync",source.Token);Pump(200);source.Cancel();Wait(task);
                Check(!task.Result&&(long)Get(c,"PressureRequestSequence")==sequence,"one-second countdown is cancellable and produces no request after cancel");source.Dispose();
            });
            Run(qa,c=>{Set(c,"AutomaticPressureCountdownSeconds",2);Set(c,"AutomaticPressureCountdownEnabled",false);var watch=Stopwatch.StartNew();var task=(Task<bool>)Call(c,"RequestAutomaticNextPressureAsync");Pump(1100);Check(!task.IsCompleted,"explicit two-second wait is not shortened to new default");Wait(task);Check(task.Result&&watch.ElapsedMilliseconds>=2000,"explicit two-second wait honored in runtime");});
            Console.WriteLine("PRESSURE_COUNTDOWN_ONE_SECOND_PASS="+checks);Console.WriteLine("HARDWARE_ACCESSED=False;SDK_CALLED=False;PRODUCT_ENTRY_STARTED=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
