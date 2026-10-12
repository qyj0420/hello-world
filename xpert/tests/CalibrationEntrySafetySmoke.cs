using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.Controls;
using Xiongxin.Calibration;
using Xiongxin.Scope;

internal static class CalibrationEntrySafetySmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static object Get(object owner,string name){return owner.GetType().GetField(name,Hidden).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Hidden).SetValue(owner,value);}
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static async Task Reject(Func<Task> action,string why){try{await action();}catch{checks++;return;}throw new Exception("Expected rejection: "+why);}
    sealed class Io:IControlBoxIo
    {
        internal int Opens,Reads,Closes,Writes;
        internal bool High,FailRead,FailClose;
        public void Open(){Interlocked.Increment(ref Opens);}
        public bool ReadEmergencySignalHigh(){Interlocked.Increment(ref Reads);if(FailRead)throw new IOException("fake no emergency data");return High;}
        public void WriteShortCable(bool value){Writes++;}
        public void Close(){Interlocked.Increment(ref Closes);if(FailClose)throw new IOException("fake retained DI handle");}
    }
    sealed class Worker:ICalibrationWorker
    {
        internal int Opens,Closes,Cancels;
        public bool Broken{get{return false;}}
        public Task<Reply> Send(Command command,int timeout){if(command.Op!="OPEN")throw new Exception("Only fake OPEN permitted");Opens++;return Task.FromResult(new Reply{Ok=true,Connected=true,Serial=command.Config.Serial,Model="fake"});}
        public void Cancel(){Cancels++;}
        public Task Close(int commandTimeout=5000,int exitTimeout=2000){Closes++;return Task.CompletedTask;}
        public Task<bool> RetireUnresponsive(){throw new Exception("No process retirement permitted");}
    }
    sealed class Context:IDisposable
    {
        internal readonly Io Io=new Io();
        internal readonly Worker Worker=new Worker();
        internal readonly CalibrationForm Form=new CalibrationForm(null,true);
        internal readonly CalibrationProgramControl Page=new CalibrationProgramControl();
        internal int Notices,Logs,Captures;
        internal Context(string root)
        {
            ((System.Windows.Forms.Timer)Get(Form,"safetyTimer")).Stop();
            Form.HostErrorReported+=error=>{};
            ((TextBox)Get(Form,"projectPath")).Text=root;
            Page.CalibrationHardwareIdentityProvider=()=>new[]{"verified-session","FakeArt","1023417009","ABCDE/1234"};
            Form.HostHardwareIdentityProvider=Page.CalibrationHardwareIdentityProvider;
            Form.HostControlBoxForTests=()=>Io;
            Form.HostWorkerForTests=()=>Worker;
            Form.HostParameterConfirmationForTests=()=>DialogResult.OK;
            Form.HostCaptureForTests=()=>{Captures++;return Task.FromResult(true);};
            Form.HostKeepSessionOpen=true;
            Page.ShowCalibrationEntryNoticeForTests=text=>{Notices++;Check(!text.Contains("请按下急停")&&(text.Contains("未确认")||text.Contains("解除")&&text.Contains("不会加压")),"state reminder distinguishes unknown from actual HIGH without requiring LOW");};
            Page.CalibrationEntryDiagnosticSink=text=>{Logs++;Check(text.Contains("unknown"),"unknown diagnostic kept internally");};
            Set(Page,"activeAcquisition",Form);
        }
        internal async Task Enter()
        {
            Page.NotifyCalibrationPageEntered();
            var task=(Task)Get(Page,"calibrationEntryCheckTask");
            if(task!=null)await task;
        }
        internal Task Capture(){return Form.HostAcquireAsync(180,170,5000,false);}
        public void Dispose(){Form.Dispose();Page.Dispose();}
    }
    static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        using(var c=new Context(root))
        {
            await c.Enter();
            Check(c.Notices==0&&c.Logs==0&&c.Page.CalibrationEntryEmergencySignalHigh==false,"actual LOW entry has no reminder");
            Check(c.Io.Opens==1&&c.Io.Reads>0&&c.Io.Closes==1&&c.Io.Writes==0,"one DI-only check closes without impedance/output writes");
            Check(c.Worker.Opens==0&&c.Captures==0,"entry never opens Pico or acquires samples");
            Check(!c.Page.HasActiveHardwareSession&&!c.Page.IsBusy&&c.Form.IsDisposed,"successful one-shot retires owner and permits normal navigation");
        }
        // Run repeatedly to cover HIGH arriving before the first waiter is attached.
        for(int i=0;i<12;i++)using(var c=new Context(root))
        {
            c.Io.High=true;await c.Enter();
            Check(c.Notices==1&&c.Logs==0&&c.Page.CalibrationEntryEmergencySignalHigh==true,"actual initial HIGH has one nonblocking input-only reminder");
            Check(c.Io.Opens==1&&c.Io.Closes==1&&c.Io.Writes==0&&c.Worker.Opens==0&&!c.Page.HasActiveHardwareSession,"HIGH check cleans only its DI owner");
        }
        using(var c=new Context(root))
        {
            c.Io.FailRead=true;await c.Enter();
            Check(c.Notices==1&&c.Logs==1&&!c.Page.CalibrationEntryEmergencySignalHigh.HasValue,"failed read visibly remains unknown, never press-emergency prompt or LOW permission");
            Check(c.Io.Closes==1&&c.Io.Writes==0&&c.Worker.Opens==0&&!c.Page.HasActiveHardwareSession,"unknown read cleans the actual owner");
        }
        using(var c=new Context(root))
        {
            c.Page.CalibrationHardwareIdentityProvider=()=>null;await c.Enter();
            Check(c.Logs==1&&c.Notices==1&&c.Io.Opens==0&&c.Worker.Opens==0,"missing selected identity opens no device and visibly remains unknown");
        }
        using(var c=new Context(root))
        {
            int identities=0;c.Page.CalibrationHardwareIdentityProvider=()=>new[]{Interlocked.Increment(ref identities)>=3?"different-session":"verified-session","FakeArt","1023417009","ABCDE/1234"};
            await c.Enter();
            Check(c.Logs==1&&c.Notices==1&&!c.Page.CalibrationEntryEmergencySignalHigh.HasValue,"identity drift cannot publish LOW from old session");
            Check(c.Io.Opens==1&&c.Io.Closes==1&&c.Io.Writes==0,"identity-drift owner is actually closed");
        }
        using(var c=new Context(root))
        {
            c.Io.FailClose=true;await c.Enter();
            Check(c.Logs==1&&c.Notices==1&&!c.Page.CalibrationEntryEmergencySignalHigh.HasValue,"failed DI close does not claim a finished check");
            Check(c.Page.HasActiveHardwareSession&&c.Page.IsBusy&&!c.Page.HasReusableHardwareSession&&!c.Form.IsDisposed,"failed close retains resource ownership and capture block");
            Check(c.Page.CanReleaseHardwareSession,"idle failed owner exposes explicit Home cleanup retry");
            int opens=c.Io.Opens;await c.Enter();
            Check(c.Io.Opens==opens,"retained cleanup forbids another DI owner");
            c.Io.FailClose=false;Check(await c.Page.ReleaseHardwareSessionAsync(),"explicit retry clears ownership only after native fake Close success");
            Check(!c.Page.HasActiveHardwareSession&&!c.Page.IsBusy&&c.Form.IsDisposed,"verified cleanup unblocks navigation");
        }
        using(var c=new Context(root))
        {
            await c.Capture();int reads=c.Io.Reads,writes=c.Io.Writes;
            Check(c.Form.HostSessionReusable,"fixture has a real managed reusable calibration session");
            await c.Enter();
            Check(c.Io.Opens==1&&c.Io.Closes==0&&c.Io.Reads>reads,"entry reuses the sole monitor with a fresh read");
            Check(c.Worker.Opens==1&&c.Worker.Closes==0&&c.Io.Writes==writes&&c.Captures==1,"entry makes no new Pico/output/capture calls");
            Check(c.Notices==0&&c.Page.HasReusableHardwareSession&&!c.Page.IsBusy,"reused LOW keeps idle ownership reusable");
            Check(await c.Page.ReleaseHardwareSessionAsync(),"reused owner still releases normally for Home");
        }
        using(var c=new Context(root))
        {
            c.Page.SimulationMode=true;await c.Enter();
            Check(c.Io.Opens==0&&c.Notices==0&&Get(c.Page,"calibrationEntryCheckTask")==null,"simulation does not probe hardware");
        }
        using(var page=new CalibrationProgramControl())
        {
            var pending=new TaskCompletionSource<bool?>();int notices=0,reads=0;
            page.ReadCalibrationEntryEmergencyForTests=()=>{reads++;return pending.Task;};
            page.ShowCalibrationEntryNoticeForTests=text=>notices++;
            page.NotifyCalibrationPageEntered();var task=(Task)Get(page,"calibrationEntryCheckTask");
            Check(page.IsBusy&&page.HasActiveHardwareSession&&!page.CanReleaseHardwareSession,"pending entry serializes against start/release");
            page.NotifyCalibrationPageEntered();page.Size=new System.Drawing.Size(1300,800);
            Check(reads==1&&notices==0,"duplicate notification and resize do not add a reader or early prompt");
            pending.SetResult(false);await task;
            Check(notices==0&&!page.IsBusy&&page.CalibrationEntryEmergencySignalHigh==false,"late current LOW commits no reminder");
        }
        using(var c=new Context(root))
        {
            c.Io.High=true;
            await c.Capture();
            Check(c.Form.LastCaptureSucceeded&&c.Captures==1&&c.Worker.Opens==1&&c.Io.Writes==0,"HIGH external signal capture uses DI/Pico only, not output switching");
            Check(!((ComboBox)Get(c.Form,"impedance")).Enabled,"integrated output operation stays disabled");
            ((ComboBox)Get(c.Form,"impedance")).SelectedIndex=1;
            Check(c.Io.Writes==0,"programmatic impedance selection cannot invoke output control");
            c.Io.High=false;await c.Capture();
            Check(c.Form.LastCaptureSucceeded&&c.Captures==2&&c.Worker.Opens==1&&c.Io.Writes==0,"LOW external signal capture reuses the same input-only lifecycle");
            Check(await c.Page.ReleaseHardwareSessionAsync(),"input-only owner releases explicitly");
        }
        using(var c=new Context(root))
        {
            c.Io.FailRead=true;
            await c.Capture();
            Check(!c.Form.LastCaptureSucceeded&&c.Captures==0&&c.Worker.Opens==0&&c.Io.Writes==0,"entry policy keeps actual input-read failure capture guard");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            var run=Run(args[0]);var until=DateTime.UtcNow.AddSeconds(35);
            while(!run.IsCompleted){if(DateTime.UtcNow>until)throw new TimeoutException("fake entry tests timed out");Application.DoEvents();Thread.Sleep(1);}
            run.GetAwaiter().GetResult();
            Console.WriteLine("CALIBRATION_ENTRY_SAFETY_PASS="+checks+"; SDK_CALLS=0; PRODUCT_STARTED=0");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
