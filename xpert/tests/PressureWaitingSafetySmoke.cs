using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Owned-token/UI fixtures only; no acquisition delegate, SDK or output.
internal static class PressureWaitingSafetySmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type type;static int checks;static string qa;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Get(object o,string n){return type.GetProperty(n,B).GetValue(o,null);}
    static void Set(object o,string n,object v){type.GetProperty(n,B).SetValue(o,v,null);}
    static void Field(object o,string n,object v){type.GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] args){return type.GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Pump(int ms){var w=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(2);}while(w.ElapsedMilliseconds<ms);}
    static void Wait(Task<bool> task){var w=Stopwatch.StartNew();while(!task.IsCompleted&&w.ElapsedMilliseconds<4000)Pump(5);Check(task.IsCompleted,"bounded completion");}
    [STAThread]static int Main(string[] args)
    {try{
        Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
        type=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
        foreach(string reason in new[]{"low","unknown","reset","finish"})foreach(bool hidden in new[]{false,true})
        foreach(bool manual in new[]{false,true})foreach(int remaining in new[]{2000,0})
        using(var form=new Form{Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false,Size=new Size(1200,750)})
        using(var c=(Control)Activator.CreateInstance(type))using(var source=new CancellationTokenSource())
        using(var heartbeat=new System.Windows.Forms.Timer{Interval=100}){
            form.Controls.Add(c);c.Dock=DockStyle.Fill;Set(c,"ProjectPath",qa);Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetAdvancedMode",manual);Set(c,"ActionHandshakeEnabled",true);Set(c,"AutomaticPressureCountdownEnabled",!hidden);form.Show();Pump(30);
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Call(c,"RequestPressure");long seq=(long)Get(c,"PressureRequestSequence");
            Field(c,"zeroHardwareRunning",true);Field(c,"zeroHardwareCancellation",source);
            heartbeat.Tick+=(s,e)=>Call(c,"ReportP04Status",true,true,DateTime.UtcNow);heartbeat.Start();
            var countdown=(Task<bool>)Call(c,"RequestAutomaticCaptureCountdownAsync",source.Token);Wait(countdown);
            Check(countdown.Result,"eligible first-round countdown completes: "+reason+" hidden="+hidden+" cancellation="+Get(c,"LastCountdownCancellationSource")+" sourceCanceled="+source.IsCancellationRequested+" sequence="+seq+" busy="+Get(c,"IsBusy")+" action="+Get(c,"ActionCode")+" interrupted="+type.GetField("automaticPlanInterrupted",B).GetValue(c)+" manual="+type.GetField("advancedMode",B).GetValue(c));
            Call(c,"ReportCaptureCooldown",2000);if(remaining==0)Call(c,"ReportCaptureCooldown",0);heartbeat.Stop();
            if(reason=="low")Call(c,"ReportP04Status",true,false,DateTime.UtcNow.AddMilliseconds(1));
            else if(reason=="unknown")Call(c,"ReportP04Status",false,false,DateTime.UtcNow);
            else if(reason=="reset")Call(c,"ResetP04FeedbackSession");
            else if(manual)Call(c,"RequestHardwareStop"); // Manual mode exposes Stop, not Finish Current Round.
            else Check((bool)Call(c,"RequestFinishCurrentRound"),"automatic wait accepts finish/stop request");
            Check(source.IsCancellationRequested,"wait cancellation reaches token: "+reason+" hidden="+hidden+" manual="+manual+" remaining="+remaining);
            Check(!(bool)Call(c,"TryBeginPreparedHardwareCapture",source.Token),"cancelled preparation cannot be delivered to worker");
            Pump(5);Call(c,"ReportP04Status",true,true,DateTime.UtcNow.AddMilliseconds(2));
            Check(source.IsCancellationRequested&&(long)Get(c,"PressureRequestSequence")==seq,"released stop cannot revive previous request");
            Call(c,"ReportCaptureCooldown",0);Check((int)Get(c,"CaptureCooldownRemainingMilliseconds")==0,"waiting UI clears");
            Field(c,"zeroHardwareRunning",false);Field(c,"zeroHardwareCancellation",null);
        }
        Console.WriteLine("PRESSURE_WAITING_SAFETY_PASS="+checks+";HardwareAccessed=false;WorkersStarted=0");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
