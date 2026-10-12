using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Real UI/control state with synthetic ownership only. No acquisition delegate,
// pressure backend, native SDK, worker, or device monitor is configured.
internal static class PressurePreparationCommitSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Type type;static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Get(object o,string name){return type.GetProperty(name,B).GetValue(o,null);}
    static void Set(object o,string name,object value){type.GetProperty(name,B).SetValue(o,value,null);}
    static object Field(object o,string name){return type.GetField(name,B).GetValue(o);}
    static void Field(object o,string name,object value){type.GetField(name,B).SetValue(o,value);}
    static object Call(object o,string name,params object[] args)
    {return type.GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Pump(int ms){var timer=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(2);}while(timer.ElapsedMilliseconds<ms);}
    static void Wait(Task<bool> task)
    {var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.ElapsedMilliseconds<6000)Pump(5);Check(task.IsCompleted,"bounded countdown completion");}
    static string Hash(string path)
    {using(var hash=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");}

    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            string dll=Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll");
            string before=Hash(dll);
            type=Assembly.LoadFrom(dll).GetType("HVX.Controls.PressureTestControl",true);
            foreach(bool manual in new[]{false,true})foreach(bool hidden in new[]{false,true})
            using(var form=new Form{Location=new Point(-30000,-30000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false,Size=new Size(1200,750)})
            using(var control=(Control)Activator.CreateInstance(type))
            using(var owner=new CancellationTokenSource())using(var foreign=new CancellationTokenSource())
            using(var heartbeat=new System.Windows.Forms.Timer{Interval=100})
            {
                form.Controls.Add(control);control.Dock=DockStyle.Fill;
                Set(control,"ProjectPath",qa);
                Call(control,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});
                Call(control,"SetPressureProgramMode","PLAN");
                Set(control,"ActionHandshakeEnabled",true);
                Set(control,"AutomaticPressureCountdownEnabled",!hidden);
                Set(control,"AutomaticPressureCountdownSeconds",0);
                form.Show();Pump(30);
                Call(control,"ReportP04Status",true,true,DateTime.UtcNow);
                Call(control,"SetAdvancedMode",manual); // Automatic mode requires fresh synthetic HIGH.
                Call(control,"RequestPressure");
                long sequence=(long)Get(control,"ActionSequence");
                Check((bool)Get(control,"IsBusy")&&(bool)Get(control,"ActionPending")&&(string)Get(control,"ActionCode")=="PRESSURE","real mailbox owns a first-round request");
                Check(Field(control,"zeroHardwareCapture")==null&&Field(control,"pressureHardwareCapture")==null,"no capture or pressure delegate configured");
                Field(control,"zeroHardwareRunning",true);Field(control,"zeroHardwareCancellation",owner);
                heartbeat.Tick+=(s,e)=>Call(control,"ReportP04Status",true,true,DateTime.UtcNow);heartbeat.Start();
                try
                {
                    var timer=Stopwatch.StartNew();
                    var countdown=(Task<bool>)Call(control,"RequestAutomaticCaptureCountdownAsync",owner.Token);
                    if(manual)Check(countdown.IsCompleted&&Field(control,"pressureCountdownDialog")==null,"manual mode establishes preparation without automatic countdown");
                    else Check(!countdown.IsCompleted&&(hidden?Field(control,"pressureCountdownDialog")==null:Field(control,"pressureCountdownDialog")!=null),"automatic first round honors selected window visibility");
                    Wait(countdown);
                    Check(countdown.Result&&!owner.IsCancellationRequested,"eligible first-round preparation completes");
                    if(!manual)Check(timer.ElapsedMilliseconds>=2000,"zero/hidden preference cannot remove the two-second automatic wait");
                    Check((int)Field(control,"automaticCapturePreparation")==1,"completed countdown retains preparation through later asynchronous work");

                    Call(control,"ReportCaptureCooldown",2000);
                    Call(control,"ReportCaptureCooldown",0);
                    Check((int)Get(control,"CaptureCooldownRemainingMilliseconds")==0&&Field(control,"captureCooldownDialog")==null,"zero remaining closes only the waiting display");
                    Check((int)Field(control,"automaticCapturePreparation")==1,"cooldown completion does not deliver ownership to worker");

                    Check(!(bool)Call(control,"TryBeginPreparedHardwareCapture",foreign.Token),"foreign live token cannot consume preparation");
                    Check((int)Field(control,"automaticCapturePreparation")==1&&!owner.IsCancellationRequested,"foreign token leaves real preparation intact");
                    foreign.Cancel();
                    Check(!(bool)Call(control,"TryBeginPreparedHardwareCapture",foreign.Token),"foreign cancelled token cannot consume preparation");
                    Check(!(bool)Call(control,"TryBeginPreparedHardwareCapture",CancellationToken.None),"unowned default token cannot consume preparation");
                    Check((int)Field(control,"automaticCapturePreparation")==1,"failed token checks do not consume preparation");

                    Check((bool)Call(control,"TryBeginPreparedHardwareCapture",owner.Token),"correct owned token commits after cooldown zero");
                    Check((int)Field(control,"automaticCapturePreparation")==0,"successful commit consumes preparation exactly once");
                    Check(!(bool)Call(control,"TryBeginPreparedHardwareCapture",owner.Token),"same token cannot commit twice");
                    Check(!owner.IsCancellationRequested&&(bool)Get(control,"HasActiveHardwareSession"),"commit is neither cancellation nor hardware release");
                    Check((bool)Get(control,"ActionPending")&&(bool)Get(control,"IsBusy")&&(long)Get(control,"ActionSequence")==sequence,"commit leaves the original mailbox busy until actual completion");

                    if(!manual)
                    {
                        Check((bool)Call(control,"RequestFinishCurrentRound"),"running automatic mode accepts finish-current-round");
                        Check((bool)Get(control,"AutomaticPressureStopped"),"finish stops future automatic scheduling");
                        Check(!owner.IsCancellationRequested,"finish after worker handoff does not cancel the owned current round");
                    }
                    else Check(!(bool)Call(control,"RequestFinishCurrentRound"),"manual mode does not expose an automatic finish-current-round command");
                    Check((bool)Get(control,"ActionPending")&&(bool)Get(control,"HasActiveHardwareSession"),"finish never fabricates acknowledgement or release");
                    Check(Field(control,"zeroHardwareCapture")==null&&Field(control,"pressureHardwareCapture")==null,"fixture never installs a hardware execution delegate");
                }
                finally
                {
                    heartbeat.Stop();
                    // Remove only the synthetic ownership before normal UI
                    // disposal. No real worker or native handle exists here.
                    Field(control,"zeroHardwareRunning",false);Field(control,"zeroHardwareCancellation",null);
                }
            }
            Check(before==Hash(dll),"tested module remains byte-identical");
            File.WriteAllText(Path.Combine(qa,"module-sha256.txt"),before+Environment.NewLine+dll+Environment.NewLine);
            Console.WriteLine("PRESSURE_PREPARATION_COMMIT_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0; CaptureDelegatesConfigured=0");
            Console.WriteLine("PRESSURE_DLL_SHA256="+before);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
