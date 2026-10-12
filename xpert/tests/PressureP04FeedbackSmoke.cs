using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class PressureP04FeedbackSmoke
{
    static Type type;
    static int checks;
    static string qa;
    static void Check(bool value,string message) { ++checks; if(!value) throw new Exception(message); }
    static object Get(object c,string name) { return type.GetProperty(name).GetValue(c,null); }
    static void Set(object c,string name,object value) { type.GetProperty(name).SetValue(c,value,null); }
    static object Call(object c,string name,params object[] args) { return type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(c,args); }
    static object Field(object c,string name) { return type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c); }
    static void Field(object c,string name,object value) { type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,value); }
    static void Pump(int milliseconds=20) { var clock=Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); } while(clock.ElapsedMilliseconds<milliseconds); }
    static void Report(object c,bool connected,bool high,DateTime time) { Call(c,"ReportP04Status",connected,high,time); }
    static void FinishWithoutUiPump(Task task,string message)
    {
        var watch=Stopwatch.StartNew();while(!task.IsCompleted&&watch.ElapsedMilliseconds<1500)Thread.Sleep(2);
        Check(task.IsCompleted&&!task.IsFaulted,message);
        if(task.IsCompleted)task.GetAwaiter().GetResult();
    }
    static void ExpireMonotonically(object c)
    { Field(c,"p04ReceivedTimestamp",Stopwatch.GetTimestamp()-3*Stopwatch.Frequency); }
    static void Run(Action<Control> test)
    {
        using(var form=new Form()) using(var c=(Control)Activator.CreateInstance(type))
        {
            try {
            form.ClientSize=new System.Drawing.Size(1280,800); c.Dock=DockStyle.Fill;form.Controls.Add(c);
            Set(c,"ProjectPath",qa);Set(c,"FrequencyReminderEnabled",false);Set(c,"MaximumPeakVoltageKv",20d);
            Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{5d});Call(c,"SetPressureProgramMode","PLAN");
            Set(c,"AutomaticPressureCountdownEnabled",false);Set(c,"AutomaticPressureCountdownSeconds",10);
            Call(c,"SetPressureState","COMPLETE",0d);form.Show();Pump();test(c);
            if(checks==21)DumpCreatingHandles(c);
            } catch(Exception error) { Console.Error.WriteLine("P04_PRIMARY_FAILURE_AFTER_CHECK="+checks+"; "+error);throw; }
        }
    }
    static void DumpCreatingHandles(Control control)
    {
        const BindingFlags flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic;
        var constant=typeof(Control).GetField("STATE_CREATINGHANDLE",flags);
        var state=typeof(Control).GetField("state",flags);
        if(constant!=null&&state!=null) {
            int mask=Convert.ToInt32(constant.GetRawConstantValue());
            if((Convert.ToInt32(state.GetValue(control))&mask)!=0)
                Console.Error.WriteLine("P04_CREATING_HANDLE="+control.GetType().FullName+"; NAME="+control.Name+"; THREAD="+Thread.CurrentThread.ManagedThreadId);
        }
        foreach(Control child in control.Controls)DumpCreatingHandles(child);
    }
    static Task<bool> Countdown(object c) { return (Task<bool>)Call(c,"RequestAutomaticNextPressureAsync"); }
    // Only inspect/close the native warning owned by this isolated QA host on
    // this UI thread. Never enumerate another application's windows, hide the
    // product warning, or suppress acquisition validation.
    static bool CompleteWithoutReferenceWithWarning(Control c)
    {
        var owner=c.FindForm();
        if(owner==null||!owner.IsHandleCreated)throw new InvalidOperationException("QA warning requires the visible host owner.");
        IntPtr ownerHandle=owner.Handle;
        uint thread=GetCurrentThreadId(),process=(uint)Process.GetCurrentProcess().Id;
        IntPtr warning=IntPtr.Zero,ok=IntPtr.Zero;
        string title="",body="",guardError="",buttonText="";
        int buttonId=0,buttons=0;
        long savesBefore=(long)Get(c,"SaveRequestSequence");
        bool scoped=false,posted=false;
        var clock=Stopwatch.StartNew();
        bool accepted;
        using(var timer=new System.Windows.Forms.Timer{Interval=25})
        {
            timer.Tick+=delegate {
                if(warning!=IntPtr.Zero)return;
                try
                {
                    EnumThreadWindows(thread,delegate(IntPtr window,IntPtr unused) {
                        uint pid;
                        if(GetWindowThreadProcessId(window,out pid)!=thread||pid!=process||GetWindow(window,4)!=ownerHandle||NativeClass(window)!="#32770")return true;
                        warning=window;scoped=true;title=NativeText(window);
                        var text=new StringBuilder();
                        EnumChildWindows(window,delegate(IntPtr child,IntPtr ignored) {
                            if(NativeClass(child)=="Button") {++buttons;ok=child;buttonId=GetDlgCtrlID(child);buttonText=NativeText(child);}
                            if(NativeClass(child)=="Static") {string value=NativeText(child);if(value.Length>0)text.AppendLine(value);}
                            return true;
                        },IntPtr.Zero);
                        body=text.ToString();timer.Stop();
                        // An unexpected owned prompt must fail after dismissal,
                        // rather than hanging the suite inside its modal loop.
                        // This Windows version exposes its sole labelled OK
                        // button as ID 2; older MessageBox versions use ID 1.
                        bool expectedText=title=="加压／采集异常"&&body.Contains("未收到本次电压参考波形")&&body.Contains("定位数据未作为完整周期保存");
                        bool expectedButton=buttons==1&&ok!=IntPtr.Zero&&(buttonId==1||buttonId==2)&&GetDlgItem(window,buttonId)==ok&&GetParent(ok)==window&&(buttonText=="确定"||buttonText=="OK");
                        if(expectedText&&expectedButton)posted=PostMessage(ok,0x00F5,IntPtr.Zero,IntPtr.Zero);
                        else {guardError="unexpected owned warning content or confirmation button";PostMessage(window,0x0010,IntPtr.Zero,IntPtr.Zero);}
                        return false;
                    },IntPtr.Zero);
                    if(clock.ElapsedMilliseconds>10000&&warning==IntPtr.Zero) {guardError="no owned warning observed before guard deadline";timer.Stop();}
                }
                catch(Exception error) {guardError=error.ToString();timer.Stop();if(warning!=IntPtr.Zero)PostMessage(warning,0x0010,IntPtr.Zero,IntPtr.Zero);}
            };
            timer.Start();
            accepted=(bool)Call(c,"ReportAcquisitionCompleted",false);
            timer.Stop();
        }
        Console.WriteLine("P04_SYNTHETIC_WARNING_TITLE="+title);
        Console.WriteLine("P04_SYNTHETIC_WARNING_BODY="+body.Replace("\r","").Replace("\n"," | "));
        Console.WriteLine("P04_WARNING_BUTTON_ID="+buttonId+"; BUTTON_TEXT="+buttonText+"; COUNT="+buttons+"; POSTED="+posted+"; GUARD_ERROR="+guardError);
        Check(warning!=IntPtr.Zero&&scoped,"synthetic acquisition warning appeared on this QA thread and host owner");
        Check(title=="加压／采集异常","synthetic acquisition warning title is the real acquisition abnormality");
        Check(body.Contains("未收到本次电压参考波形")&&body.Contains("定位数据未作为完整周期保存"),"synthetic acquisition warning explains missing reference and refuses complete-cycle data");
        Check(guardError.Length==0&&ok!=IntPtr.Zero&&posted,"only the owned sole labelled OK button received a click, not fallback close");
        Check(!IsWindow(warning)&&(string)Get(c,"PostProcessStateCode")=="ACQUISITION_INVALID"&&!(bool)Get(c,"DataSaved")&&(long)Get(c,"SaveRequestSequence")==savesBefore,"warning closed after OK and real acquisition validation still refuses saving");
        return accepted;
    }
    delegate bool EnumWindowProc(IntPtr window,IntPtr parameter);
    static string NativeText(IntPtr window) {int size=GetWindowTextLength(window);if(size>16384)throw new InvalidDataException("QA native text unexpectedly long.");var text=new StringBuilder(size+1);GetWindowText(window,text,text.Capacity);return text.ToString();}
    static string NativeClass(IntPtr window) {var text=new StringBuilder(256);GetClassName(window,text,text.Capacity);return text.ToString();}
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread,EnumWindowProc callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,EnumWindowProc callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder text,int maximum);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int maximum);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr window,int item);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            qa=Path.Combine(Path.GetFullPath(args[1]),"p04-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            type=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
            Run(c=>{
                Set(c,"LanguageIndex",0);
                Check((string)Get(c,"HighVoltageStatusCode")=="UNKNOWN"&&!(bool)Get(c,"IsHighVoltageReady"),"no producer starts unknown");
                Check((string)Get(c,"EmergencyStopStatusCode")=="UNKNOWN","unread emergency stop is not fabricated");
                Call(c,"ReportHighVoltageState",true,true);Set(c,"HighVoltageClosed",false);
                Check(!(bool)Get(c,"IsHighVoltageReady"),"initial untimed APIs cannot create live permission");
                Call(c,"ResetP04FeedbackSession");Call(c,"ReportHighVoltageState",true,true);
                Check(!(bool)Get(c,"IsHighVoltageReady"),"reset before first P04 sample cannot enable legacy bypass");
                Check(!((System.Windows.Forms.Timer)Field(c,"p04ExpiryTimer")).Enabled,"unpublished bridge does not poll hardware");
                long count=(long)Get(c,"PressureRequestSequence");Call(c,"RequestPressure");Check((long)Get(c,"PressureRequestSequence")==count,"unknown blocks nonzero direct API");
                Report(c,true,false,DateTime.UtcNow);Check((string)Get(c,"HighVoltageStatusCode")=="NOT_STARTED"&&(bool)Get(c,"HighVoltageClosed"),"physical low known and prohibited");
                Check((string)Get(c,"EmergencyStopStatusCode")=="ACTIVE","physical low presented as active emergency stop");
                Call(c,"RequestPressure");Check((long)Get(c,"PressureRequestSequence")==count,"physical low blocks request");
                Report(c,true,true,DateTime.UtcNow);Check((bool)Get(c,"IsHighVoltageReady")&&(string)Get(c,"HighVoltageStatusCode")=="STARTED","physical high allowed");
                Check((string)Get(c,"EmergencyStopStatusCode")=="RELEASED","physical high means emergency stop released, not actual voltage on");
                Call(c,"RequestPressure");Check((long)Get(c,"PressureRequestSequence")==count+1,"fresh high may publish request only");
                Report(c,false,true,DateTime.UtcNow);Check(!(bool)Get(c,"IsHighVoltageReady")&&(string)Get(c,"HighVoltageStatusCode")=="UNKNOWN","failed read discards last true");
                Call(c,"ReportHighVoltageState",true,true);Set(c,"HighVoltageClosed",false);Check(!(bool)Get(c,"IsHighVoltageReady"),"untimed compatibility APIs cannot bypass P04 ownership");
                // The published synthetic attempt has no waveform. Complete its
                // real validation failure before changing the next target.
                Check(!CompleteWithoutReferenceWithWarning(c),"previous synthetic request without waveform terminates as invalid, not success");
                Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});
                Check((bool)Call(c,"CanRequestVoltageNoLock",0d),"manual zero accepts unknown DI without creating a nonzero output permission");
            });
            Run(c=>{
                Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});
                Report(c,true,false,DateTime.UtcNow);
                Check((bool)Call(c,"CanRequestVoltageNoLock",0d),"fresh pressed emergency permits input-only zero capture");
                Check(!(bool)Call(c,"CanRequestVoltageNoLock",5d),"same LOW never permits nonzero voltage");
                var zeroCountdown=Countdown(c);
                Check(zeroCountdown.IsCompleted&&!zeroCountdown.Result,"pressed emergency prohibits automatic countdown even at zero voltage");
                long request=(long)Get(c,"PressureRequestSequence");
                Set(c,"AdvancedMode",false);
                Check(!(bool)Get(c,"AdvancedMode"),"pressed emergency permits choosing plan mode, not starting pressure");
                Check(((Control)Field(c,"normalModeButton")).Enabled,"idle plan-mode choice does not depend on electrical permission");
                zeroCountdown=Countdown(c);
                Check(zeroCountdown.IsCompleted&&!zeroCountdown.Result&&!(bool)Call(c,"CanRequestVoltageNoLock",0d),"selected automatic zero still cannot start while emergency pressed");
                Call(c,"RequestPressure");
                Check((long)Get(c,"PressureRequestSequence")==request,"mode selection plus pressed emergency cannot publish zero pressure");
                Set(c,"AdvancedMode",true);
                Report(c,true,false,DateTime.UtcNow);
                Field(c,"pressureStateCode","RUNNING");Report(c,true,false,DateTime.UtcNow.AddMilliseconds(1));
                Check(!(bool)Field(c,"pressureEmergencyLatched"),"zero input LOW is not mislabelled as a nonzero round emergency");
                Field(c,"pressureStateCode","COMPLETE");
                Report(c,false,false,DateTime.UtcNow);
                Check((bool)Call(c,"CanRequestVoltageNoLock",0d)&&!(bool)Call(c,"CanRequestVoltageNoLock",5d),"unknown DI permits manual zero input only, never nonzero output");
                Field(c,"automaticPlanInterrupted",false);Field(c,"pressureStateCode","RUNNING");Field(c,"requestedTargetPeakVoltageKv",0d);
                Report(c,false,false,DateTime.UtcNow);Call(c,"ApplyP04FeedbackToUi");
                Check(!(bool)Field(c,"automaticPlanInterrupted"),"unknown DI does not interrupt the pending manual zero input round");
                Field(c,"advancedMode",false);Report(c,false,false,DateTime.UtcNow);
                Check(!(bool)Call(c,"CanRequestVoltageNoLock",0d)&&(bool)Field(c,"automaticPlanInterrupted"),"automatic zero remains fail-closed on unknown DI");
                Field(c,"advancedMode",true);Field(c,"automaticPlanInterrupted",false);Field(c,"pressureStateCode","COMPLETE");
                Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{5d});
                foreach(var invalid in new[]{DateTime.UtcNow.AddSeconds(-3),DateTime.UtcNow.AddSeconds(3),DateTime.SpecifyKind(DateTime.UtcNow,DateTimeKind.Unspecified),DateTime.Now})
                {
                    Report(c,true,true,DateTime.UtcNow);Check((bool)Get(c,"IsHighVoltageReady"),"fresh recovery before invalid timestamp");
                    Report(c,true,true,invalid);Check(!(bool)Get(c,"IsHighVoltageReady")&&(string)Get(c,"HighVoltageStatusCode")=="UNKNOWN","invalid/stale/future/nonUTC timestamp fails closed");
                }
                Report(c,true,true,DateTime.UtcNow.AddMilliseconds(100));Check((bool)Get(c,"IsHighVoltageReady"),"small specified clock tolerance accepted");
                ExpireMonotonically(c);
                Check(!(bool)Get(c,"IsHighVoltageReady")&&(string)Get(c,"HighVoltageStatusCode")=="UNKNOWN","monotonic expiry denies before UI timer fires");
                Check(((string[])Call(c,"ReadControlState"))[13]=="HV_UNKNOWN","public state read has no stale permission");
                long sequence=(long)Get(c,"PressureRequestSequence");Call(c,"RequestPressure");Check((long)Get(c,"PressureRequestSequence")==sequence,"nonzero request rechecks fresh feedback even when UI blocked");
                Call(c,"ApplyP04FeedbackToUi");Check(!(bool)Field(c,"highVoltageFeedbackKnown"),"timer clears stored feedback status");
            });
            Run(c=>{
                Call(c,"SetPressurePlan",new[]{0d,1d},new[]{1,1},new[]{0d,5d});
                Check((bool)Get(c,"AdvancedMode")&&(string)Get(c,"EmergencyStopStatusCode")=="UNKNOWN","DI-free manual zero begins with unknown emergency status");
                Check(((Control)Field(c,"normalModeButton")).Enabled,"manual zero can select automatic plan before DI exists");
                long sequence=(long)Get(c,"PressureRequestSequence");
                Set(c,"AdvancedMode",false);
                Check(!(bool)Get(c,"AdvancedMode")&&(long)Get(c,"PressureRequestSequence")==sequence,"mode transition alone cannot issue or resume pressure");
                var pending=Countdown(c);
                Check(pending.IsCompleted&&!pending.Result,"automatic zero countdown remains denied while DI unknown");
                Call(c,"RequestPressure");
                Check((long)Get(c,"PressureRequestSequence")==sequence&&!(bool)Call(c,"CanRequestVoltageNoLock",5d),"unknown feedback blocks automatic zero and every nonzero request");
                long oldSource=(long)Call(c,"BeginP04FeedbackSource");
                Call(c,"ResetP04FeedbackSession");
                long currentSource=(long)Call(c,"BeginP04FeedbackSource");
                Call(c,"ReportP04StatusForSource",oldSource,true,true,DateTime.UtcNow);
                Check(!(bool)Get(c,"IsHighVoltageReady"),"old input-only source cannot restore permission after mode ownership rotation");
                Call(c,"ReportP04StatusForSource",currentSource,true,true,DateTime.UtcNow);
                Check((bool)Get(c,"IsHighVoltageReady")&&(bool)Call(c,"CanRequestVoltageNoLock",5d),"only new-source fresh HIGH restores request permission");
                pending=Countdown(c);Check(!pending.IsCompleted,"new-source HIGH permits an explicitly requested automatic countdown");
                Set(c,"AdvancedMode",true);
                Check(!(bool)Get(c,"AdvancedMode"),"active automatic countdown locks mode changes");
                Call(c,"CancelAutomaticPressureScheduling");
                Check(pending.IsCompleted&&!pending.Result&&(long)Get(c,"PressureRequestSequence")==sequence,"cancelled countdown does not publish an output request");
                Field(c,"actionBusy",true);Set(c,"AdvancedMode",true);
                Check(!(bool)Get(c,"AdvancedMode"),"busy action locks mode changes even without an active countdown");
                Field(c,"actionBusy",false);Set(c,"AdvancedMode",true);
                Check((bool)Get(c,"AdvancedMode"),"idle mode choice recovers when the action finishes");
                Call(c,"SetPressureProgramMode","CUSTOM");Set(c,"AdvancedMode",false);
                Check((bool)Get(c,"AdvancedMode")&&!((Control)Field(c,"normalModeButton")).Enabled,"custom voltage cannot select automatic plan mode");
            });
            Run(c=>{
                DateTime high=DateTime.UtcNow;Report(c,true,true,high);Pump(4);DateTime low=DateTime.UtcNow;Report(c,true,false,low);
                Report(c,true,true,high);Check(!(bool)Get(c,"IsHighVoltageReady")&&(string)Get(c,"HighVoltageStatusCode")=="NOT_STARTED","late high cannot overwrite newer low");
                long received=(long)Field(c,"p04ReceivedTimestamp");Report(c,true,true,low);Check((long)Field(c,"p04ReceivedTimestamp")==received&&!(bool)Get(c,"IsHighVoltageReady"),"same timestamp does not renew or change bit");
                Report(c,true,true,DateTime.UtcNow);var pending=Countdown(c);Check(!pending.IsCompleted,"fresh high permits countdown");
                object clock=Field(c,"pressureCountdownClock");Report(c,true,true,DateTime.UtcNow);
                Check(!pending.IsCompleted&&Object.ReferenceEquals(clock,Field(c,"pressureCountdownClock")),"same high heartbeat preserves countdown and clock");
                Report(c,true,false,DateTime.UtcNow);Check(pending.IsCompleted&&!pending.Result,"low cancels countdown");
                Report(c,true,true,DateTime.UtcNow);pending=Countdown(c);ExpireMonotonically(c);Call(c,"ApplyP04FeedbackToUi");
                Check(pending.IsCompleted&&!pending.Result,"expiry cancels countdown");
                Report(c,true,true,DateTime.UtcNow);pending=Countdown(c);ExpireMonotonically(c);Report(c,true,true,DateTime.UtcNow);
                Check(pending.IsCompleted&&!pending.Result&&(bool)Get(c,"IsHighVoltageReady"),"new fresh high cannot bridge lost continuity in old countdown");
                Check((long)Get(c,"PressureRequestSequence")==0,"status updates never themselves request pressure");
            });
            Run(c=>{
                Report(c,true,true,DateTime.UtcNow);DateTime old=(DateTime)Get(c,"P04CapturedUtc");Call(c,"ResetP04FeedbackSession");
                Check(!(bool)Get(c,"IsHighVoltageReady"),"new session invalidates feedback");Report(c,true,true,old);Check(!(bool)Get(c,"IsHighVoltageReady"),"old sample rejected after reset");
                Report(c,true,true,DateTime.UtcNow);Set(c,"ProjectPath",Path.Combine(qa,"changed"));Check(!(bool)Get(c,"IsHighVoltageReady"),"project change invalidates feedback");
                Report(c,true,true,DateTime.UtcNow);Set(c,"SimulationMode",true);Check((string)Get(c,"HighVoltageStatusCode")=="SIMULATION","simulation explicitly identified");
                Report(c,true,true,DateTime.UtcNow);Set(c,"SimulationMode",false);Check(!(bool)Get(c,"IsHighVoltageReady"),"simulation cannot carry feedback into live session");
                Report(c,true,true,DateTime.UtcNow);c.Dispose();Check(Field(c,"p04ExpiryTimer")==null&&!(bool)Get(c,"IsHighVoltageReady"),"dispose releases timer and permission");
                Report(c,true,true,DateTime.UtcNow);Check(!(bool)Get(c,"IsHighVoltageReady"),"disposed callback cannot revive feedback");
            });
            Run(c=>{
                var timer=(System.Windows.Forms.Timer)Field(c,"p04ExpiryTimer");Check(timer.Interval==250,"expiry UI refresh is 250ms");
                Report(c,true,true,DateTime.UtcNow);var pending=Countdown(c);ExpireMonotonically(c);Pump(400);
                Check(pending.IsCompleted&&!pending.Result&&!(bool)Field(c,"highVoltageFeedbackKnown"),"actual message-loop timer expires and cancels");
                Report(c,true,true,DateTime.UtcNow);DateTime sample=DateTime.UtcNow;
                var worker=Task.Run(()=>Report(c,true,true,sample));FinishWithoutUiPump(worker,"background HIGH report does not wait for UI");
                Check((bool)Field(c,"p04UiRefreshQueued"),"one background render is queued");Call(c,"ResetP04FeedbackSession");
                Pump();Check(!(bool)Get(c,"IsHighVoltageReady"),"queued old HIGH render cannot restore permission after session reset");
            });
            foreach(bool failedRead in new[]{false,true}) Run(c=>{
                Report(c,true,true,DateTime.UtcNow);
                var worker=Task.Run(()=>Report(c,!failedRead,false,DateTime.UtcNow));FinishWithoutUiPump(worker,"LOW/failure producer returns without UI: "+failedRead);
                Check((bool)Field(c,"p04UiRefreshQueued")&&!(bool)Get(c,"IsHighVoltageReady"),"worker low/failure denies before UI dispatch: "+failedRead);
                Pump();Check(!(bool)Get(c,"IsHighVoltageReady"),"queued render preserves denial: "+failedRead);
            });
            Run(c=>{
                Report(c,true,true,DateTime.UtcNow);var pending=Countdown(c);
                var low=Task.Run(()=>Report(c,true,false,DateTime.UtcNow));FinishWithoutUiPump(low,"LOW producer does not wait for cancellation rendering");
                var high=Task.Run(()=>Report(c,true,true,DateTime.UtcNow));
                FinishWithoutUiPump(high,"following HIGH producer also returns without UI");
                Check((bool)Field(c,"p04UiRefreshQueued")&&!(bool)Get(c,"IsHighVoltageReady"),"low-high coalescing stays prohibited until interruption handled");
                Pump();Check(pending.IsCompleted&&!pending.Result,"coalesced low-high still cancels old countdown");
                Check((bool)Get(c,"IsHighVoltageReady")&&(long)Get(c,"PressureRequestSequence")==0,"fresh high may recover only for a new explicit request");
            });
            Run(c=>{
                Report(c,true,true,DateTime.UtcNow);
                var reset=Task.Run(()=>Call(c,"ResetP04FeedbackSession"));FinishWithoutUiPump(reset,"session reset does not wait for UI");
                Check((bool)Field(c,"p04UiRefreshQueued")&&!(bool)Get(c,"IsHighVoltageReady"),"worker session reset closes permission before its UI callback");
                Report(c,true,true,DateTime.UtcNow);Check((bool)Get(c,"IsHighVoltageReady"),"new sample may recover while reset UI is queued");
                Pump();Check((bool)Get(c,"IsHighVoltageReady")&&((System.Windows.Forms.Timer)Field(c,"p04ExpiryTimer")).Enabled,"late reset UI preserves new sample and expiry timer");
                ExpireMonotonically(c);Pump(400);Check(!(bool)Field(c,"highVoltageFeedbackKnown"),"new sample still expires visibly after late reset UI");
            });
            Run(c=>{
                Report(c,true,true,DateTime.UtcNow);
                var close=Task.Run(()=>Call(c,"ReportHighVoltageState",false,false));FinishWithoutUiPump(close,"legacy negative report does not wait for UI");
                Check((bool)Field(c,"p04UiRefreshQueued")&&!(bool)Get(c,"IsHighVoltageReady"),"legacy negative report also closes safety cache synchronously before UI");
                Pump();Check(!(bool)Get(c,"IsHighVoltageReady"),"legacy close rendering cannot recover permission");
            });
            Run(c=>{
                long source=(long)Call(c,"BeginP04FeedbackSource");Call(c,"ReportP04StatusForSource",source,true,true,DateTime.UtcNow);
                int reads=0;var producer=Task.Run(()=>{
                    var elapsed=Stopwatch.StartNew();
                    while(elapsed.ElapsedMilliseconds<3000){Call(c,"ReportP04StatusForSource",source,true,true,DateTime.UtcNow);Interlocked.Increment(ref reads);Thread.Sleep(25);}
                });
                // Deliberately do not pump the UI for longer than the 2-second
                // TTL. Same-bit physical reads must keep coming independently.
                Thread.Sleep(3100);FinishWithoutUiPump(producer,"continuous heartbeat producer finishes despite a 3-second stalled UI");
                Check(reads>=30,"producer performs repeated same-HIGH reads instead of waiting for one render");
                Check((DateTime.UtcNow-(DateTime)Get(c,"P04CapturedUtc")).TotalSeconds<1&&(bool)Get(c,"IsHighVoltageReady"),"fresh same-value heartbeats renew TTL while UI is stalled");
                Check((bool)Field(c,"p04UiRefreshQueued")&&((System.Collections.ICollection)Field(c,"outstandingUiRequests")).Count==0,"P04 posts one combined render, not synchronous generic UI requests");
                Pump();Check(!(bool)Field(c,"p04UiRefreshQueued")&&(string)Get(c,"EmergencyStopStatusCode")=="RELEASED","one render displays latest heartbeat");
                Check((long)Get(c,"PressureRequestSequence")==0,"heartbeat never starts or resumes pressure");
            });
            foreach(bool failure in new[]{false,true})Run(c=>{
                Report(c,true,true,DateTime.UtcNow);
                FinishWithoutUiPump(Task.Run(()=>Report(c,true,true,DateTime.UtcNow)),"HIGH rendering is queued without holding producer");
                FinishWithoutUiPump(Task.Run(()=>Report(c,!failure,false,DateTime.UtcNow)),"later LOW/unknown updates before any UI rendering");
                Check(!(bool)Get(c,"IsHighVoltageReady"),"later LOW/unknown immediately wins over queued HIGH");
                Pump();Check(!(bool)Get(c,"IsHighVoltageReady")&&(string)Get(c,"EmergencyStopStatusCode")== (failure?"UNKNOWN":"ACTIVE"),"queued HIGH never overwrites later LOW/unknown display");
            });
            Run(c=>{
                long zero=(long)Call(c,"BeginP04FeedbackSource");
                Call(c,"ReportP04StatusForSource",zero,true,false,DateTime.UtcNow);
                long resident=(long)Call(c,"BeginP04FeedbackSource");
                Call(c,"ReportP04StatusForSource",resident,true,true,DateTime.UtcNow);
                DateTime sample=(DateTime)Get(c,"P04CapturedUtc");long sequence=(long)Get(c,"PressureRequestSequence");
                foreach(var old in new[]{DateTime.MinValue,DateTime.UtcNow,DateTime.UtcNow.AddSeconds(-10)}){
                    Call(c,"ReportP04StatusForSource",zero,false,false,old);
                    Call(c,"ReportP04StatusForSource",zero,true,false,old);
                    Check((bool)Get(c,"IsHighVoltageReady")&&(DateTime)Get(c,"P04CapturedUtc")==sample,"old zero-session LOW/UNKNOWN cannot erase resident sample");
                }
                Call(c,"ReportP04StatusForSource",resident,false,false,DateTime.UtcNow);
                Check(!(bool)Get(c,"IsHighVoltageReady"),"current source failure still denies immediately");
                Call(c,"ReportP04StatusForSource",zero,true,true,DateTime.UtcNow);
                Check(!(bool)Get(c,"IsHighVoltageReady"),"old source HIGH cannot revive failed resident");
                Call(c,"ReportP04StatusForSource",resident,true,true,DateTime.UtcNow);
                Check((bool)Get(c,"IsHighVoltageReady")&&(long)Get(c,"PressureRequestSequence")==sequence,"new current read recovers display only, never starts pressure");
                Call(c,"ReportP04StatusForSource",resident,true,true,DateTime.UtcNow.AddMinutes(1));
                Check(!(bool)Get(c,"IsHighVoltageReady"),"current source future timestamp fails closed");
                Call(c,"ReportP04StatusForSource",resident,true,true,DateTime.UtcNow.AddSeconds(-10));
                Check(!(bool)Get(c,"IsHighVoltageReady"),"current source stale timestamp fails closed");
                Call(c,"ResetP04FeedbackSession");Call(c,"ReportP04StatusForSource",resident,true,true,DateTime.UtcNow);
                Check(!(bool)Get(c,"IsHighVoltageReady"),"explicit reset invalidates producer token");
            });
            Console.WriteLine("PRESSURE_P04_FEEDBACK_PASS="+checks);Console.WriteLine("HARDWARE_ACCESSED=False;PUBLISHER_NOT_INVOKED_BY_THIS_TEST=True;QA="+qa);return 0;
        }
        catch(Exception error) { Console.Error.WriteLine("P04_FAILURE_AFTER_CHECK="+checks+"; "+error);return 1; }
    }
}
