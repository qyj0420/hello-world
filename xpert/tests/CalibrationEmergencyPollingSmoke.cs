using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.Calibration;
using Xiongxin.Scope;

internal static class CalibrationEmergencyPollingSmoke
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
    static void Until(Func<bool> ready,string name,int limit=2500){var sw=Stopwatch.StartNew();while(!ready()&&sw.ElapsedMilliseconds<limit)Thread.Sleep(2);Check(ready(),name);}
    static void Reject(Action action,string name){try{action();}catch{checks++;return;}throw new Exception("Expected rejection: "+name);}
    static void RejectTask(Task<bool> task,string name){Until(()=>task.IsCompleted,name+" completes");Reject(()=>task.GetAwaiter().GetResult(),name);}
    static object Get(object value,string name){return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,data);}
    static void Call(object value,string name,params object[] args){value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,args);}
    static void FreshRequestsAndCadence()
    {
        var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
        var starts=new List<long>();var finishes=new List<long>();var clock=Stopwatch.StartNew();int calls=0,inside=0,maximum=0,cancels=0;
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{
            int depth=Interlocked.Increment(ref inside);maximum=Math.Max(maximum,depth);int ordinal=Interlocked.Increment(ref calls);
            lock(starts)starts.Add(clock.ElapsedTicks);
            if(ordinal==1){entered.Set();release.WaitOne();}else Thread.Sleep(15);
            lock(starts)finishes.Add(clock.ElapsedTicks);Interlocked.Decrement(ref inside);return false;
        },()=>true,e=>Interlocked.Increment(ref cancels),null,null)){
            monitor.Start();Check(entered.WaitOne(1000),"first read entered");
            Task<bool> fresh=monitor.ReadFreshAsync();Check(!fresh.IsCompleted,"cached or earlier in-flight LOW cannot complete fresh check");
            release.Set();Until(()=>fresh.IsCompleted,"post-request read finishes");Check(!fresh.Result&&calls>=2,"fresh request used a later read");
            Until(()=>Volatile.Read(ref calls)>=5,"multiple cycles run");Check(monitor.Stop(1000),"reader stops with acknowledgement");
            int stopped=calls;Thread.Sleep(35);Check(calls==stopped,"no scheduling after stop");
            lock(starts)for(int i=1;i<starts.Count;i++)Check((starts[i]-finishes[i-1])*1000.0/Stopwatch.Frequency>=9.8,"read completion precedes next call by at least 10 ms");
            Check(maximum==1&&cancels==0,"one input owner and no false fault");
        }
        entered.Dispose();release.Dispose();
    }
    static void RetryBudgetAndNoReset()
    {
        var budgets=new List<int>();int calls=0,cancels=0;bool unknownSeen=false;var clock=Stopwatch.StartNew();
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{
            lock(budgets)budgets.Add(ms);int ordinal=Interlocked.Increment(ref calls);
            if(ordinal==1){Thread.Sleep(285);throw new CalibrationArtException(-26802);}Thread.Sleep(15);return false;
        },()=>true,e=>Interlocked.Increment(ref cancels),null,s=>{if(s.RetryPending&&s.High==null)unknownSeen=true;})){
            monitor.Start();var fresh=monitor.ReadFreshAsync();Until(()=>fresh.IsCompleted,"finite retry resolves");
            Check(!fresh.Result&&unknownSeen,"first timeout publishes UNKNOWN, not old LOW");
            Check(monitor.Stop(1000),"retry monitor stopped");
            lock(budgets){Check(budgets[0]>0&&budgets[0]<=300,"first SDK budget is at most 300 ms");Check(budgets[1]<=215&&budgets[1]>0,"retry uses remaining original 500 ms budget");}
            Check(cancels==0&&clock.ElapsedMilliseconds<1000,"successful bounded retry does not claim fault or restart round");
        }
    }
    static void CadenceStress()
    {
        int calls=0,cancels=0;var starts=new List<long>();var ends=new List<long>();var clock=Stopwatch.StartNew();
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{
            lock(starts){starts.Add(clock.ElapsedTicks);ends.Add(clock.ElapsedTicks);}
            Interlocked.Increment(ref calls);return false;
        },()=>true,e=>Interlocked.Increment(ref cancels),null,null)){
            monitor.Start();Until(()=>Volatile.Read(ref calls)>=100,"one hundred paced reads complete",5000);Check(monitor.Stop(1000),"stress reader stops");
            lock(starts)for(int i=1;i<starts.Count;i++)Check((starts[i]-ends[i-1])*1000.0/Stopwatch.Frequency>=9.95,"stress reads honour real monotonic 10 ms floor");
            Check(cancels==0,"normal paced stress does not false fault");
        }
    }
    static void DoubleTimeoutRecovery()
    {
        var order=new List<string>();int calls=0,cancels=0,rebuilds=0;bool recoveryConcurrent=false;int inside=0;
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{
            Interlocked.Increment(ref inside);Interlocked.Increment(ref calls);Thread.Sleep(35);Interlocked.Decrement(ref inside);
            throw new CalibrationArtException(-26802);
        },()=>true,e=>{lock(order)order.Add("cancel");Interlocked.Increment(ref cancels);},()=>{
            recoveryConcurrent=Volatile.Read(ref inside)!=0;lock(order)order.Add("rebuild");Interlocked.Increment(ref rebuilds);
        },null)){
            monitor.Start();var pending=monitor.ReadFreshAsync();RejectTask(pending,"two timeouts reject permission");
            Until(()=>Volatile.Read(ref rebuilds)==1,"one DI rebuild after failure");Check(monitor.Stop(1000),"double-timeout owner exits");
            Check(calls==2&&rebuilds==1&&cancels==1&&!recoveryConcurrent,"two serialized reads, one cancel and one nonconcurrent rebuild");
            lock(order)Check(String.Join(",",order)=="cancel,rebuild","cancellation delivered before recovery");
            Check(monitor.Snapshot.High==null&&monitor.Snapshot.Error!=null&&monitor.Snapshot.RecoveryAttempted,"rebuild retains unknown/fault latch");
            RejectTask(monitor.ReadFreshAsync(),"recovery does not resume or authorize a capture");
        }
    }
    static void LateRetryIsRejected()
    {
        int calls=0,cancels=0,rebuilds=0;var secondEntered=new ManualResetEvent(false);
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{
            if(Interlocked.Increment(ref calls)==1){Thread.Sleep(280);throw new CalibrationArtException(-26802);}
            secondEntered.Set();Thread.Sleep(270);return false;
        },()=>true,e=>Interlocked.Increment(ref cancels),()=>Interlocked.Increment(ref rebuilds),null)){
            monitor.Start();var fresh=monitor.ReadFreshAsync();Check(secondEntered.WaitOne(1000),"late retry began only after first timeout");
            RejectTask(fresh,"retry cannot restart 500 ms deadline");Until(()=>Volatile.Read(ref cancels)==1,"late retry cancels once");
            Check(monitor.Stop(1000),"late retry eventually returns and exits");
            Check(calls==2&&rebuilds==0&&monitor.Snapshot.High==null,"late LOW not accepted and does not trigger timeout-code recovery");
        }
        secondEntered.Dispose();
    }
    static void IdleGapStillExpires()
    {
        int calls=0,cancels=0;var notificationEntered=new ManualResetEvent(false);var releaseNotification=new ManualResetEvent(false);
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{Interlocked.Increment(ref calls);return false;},()=>true,
            e=>Interlocked.Increment(ref cancels),null,s=>{if(s.Error==null&&s.Sequence==1){notificationEntered.Set();releaseNotification.WaitOne();}})){
            try{
                monitor.Start();Check(notificationEntered.WaitOne(1000),"valid read completed before artificial scheduling gap");
                var fresh=monitor.ReadFreshAsync();Check(!monitor.Snapshot.ReadInFlight,"between-read gap is not a native call");
                Until(()=>Volatile.Read(ref cancels)==1,"500 ms watchdog covers successful-read notification gap",1500);
                RejectTask(fresh,"gap timeout completes fresh waiters without a new read");
                Check(monitor.Snapshot.High==null&&calls==1,"historical LOW expired even though no read is in flight");
            }finally{releaseNotification.Set();Check(monitor.Stop(1000),"blocked notification released reader");}
        }
        notificationEntered.Dispose();releaseNotification.Dispose();
    }
    static void InterReadDelayConsumesBudget()
    {
        int calls=0,cancels=0,secondBudget=0;
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{
            if(Interlocked.Increment(ref calls)==1)return false;
            secondBudget=ms;Thread.Sleep(ms+35);return false;
        },()=>true,e=>Interlocked.Increment(ref cancels),null,s=>{if(s.Error==null&&s.Sequence==1)Thread.Sleep(270);})){
            monitor.Start();Until(()=>Volatile.Read(ref calls)==2,"read after long scheduling gap starts");
            var fresh=monitor.ReadFreshAsync();Until(()=>Volatile.Read(ref cancels)==1,"late post-gap LOW rejected");
            RejectTask(fresh,"waiter rejected when last-valid deadline expires");Check(monitor.Stop(1000),"late post-gap read returned");
            Check(secondBudget>0&&secondBudget<=230,"new read budget subtracts gap from last valid sample, not fresh 500 ms");
            Check(monitor.Snapshot.Sequence==1&&monitor.Snapshot.High==null,"late LOW cannot refresh last-valid time or sequence");
        }
    }
    static void ReaderExceptionCompletesWaiters()
    {
        var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);int cancels=0;
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{entered.Set();release.WaitOne();throw new IOException("fake reader failure");},()=>true,
            e=>Interlocked.Increment(ref cancels),null,null)){
            monitor.Start();Check(entered.WaitOne(1000),"exception fixture read entered");var fresh=monitor.ReadFreshAsync();release.Set();
            RejectTask(fresh,"unexpected reader error completes queued requests");Check(monitor.Stop(1000),"exception reader joined");
            RejectTask(monitor.ReadFreshAsync(),"ended reader cannot accept new waiters");Check(cancels==1,"exception cancelled once");
        }
        entered.Dispose();release.Dispose();
    }
    static void HungReadKeepsOwnership()
    {
        var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);int cancels=0,rebuilds=0,calls=0;
        var monitor=new CalibrationEmergencyMonitor(ms=>{Interlocked.Increment(ref calls);entered.Set();release.WaitOne();return false;},()=>true,
            e=>Interlocked.Increment(ref cancels),()=>Interlocked.Increment(ref rebuilds),null);
        try{
            monitor.Start();Check(entered.WaitOne(1000),"blocked SDK delegate entered");var clock=Stopwatch.StartNew();var pending=monitor.ReadFreshAsync();
            Until(()=>Volatile.Read(ref cancels)==1,"watchdog cancels without native return",1500);
            Check(clock.ElapsedMilliseconds>=440&&clock.ElapsedMilliseconds<1400,"managed watchdog observes 500 ms deadline with scheduling tolerance");
            Check(monitor.Snapshot.ReadInFlight&&monitor.Snapshot.High==null,"timeout not misreported as completed native call");
            RejectTask(pending,"blocked read does not reuse historical permission");Check(!monitor.Stop(10),"cannot acknowledge stop while delegate still owns handle");
            Check(calls==1&&rebuilds==0,"no overlapping retry or cleanup on stuck read");
            release.Set();Check(monitor.Stop(1000),"old call actually returns before stop acknowledgement");
            Check(monitor.Snapshot.High==null&&monitor.Snapshot.Error!=null,"late LOW never clears failure");
        }finally{release.Set();monitor.Stop(1000);monitor.Dispose();entered.Dispose();release.Dispose();}
    }
    static void OfficialErrorsAndHigh()
    {
        foreach(int code in new[]{-200284,-200474,-50401,-200088,-229779,-987654}){
            int reads=0,cancels=0,rebuilds=0;
            using(var monitor=new CalibrationEmergencyMonitor(ms=>{Interlocked.Increment(ref reads);throw new CalibrationArtException(code);},()=>true,
                e=>Interlocked.Increment(ref cancels),()=>Interlocked.Increment(ref rebuilds),null)){
                monitor.Start();Until(()=>Volatile.Read(ref cancels)==1,"non-timeout error cancelled");Check(monitor.Stop(1000),"error reader exited");
                var error=monitor.Snapshot.Error as CalibrationArtException;
                Check(reads==1&&rebuilds==0&&error!=null&&error.Status==code,"official/nonclassified code preserved without timeout retry");
            }
        }
        int highReads=0,highCancels=0;
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{Interlocked.Increment(ref highReads);return true;},()=>true,
            e=>Interlocked.Increment(ref highCancels),null,null)){
            monitor.Start();Until(()=>Volatile.Read(ref highReads)>=2,"HIGH continues independent input polling");
            Check(monitor.ReadFreshAsync().GetAwaiter().GetResult(),"fresh HIGH is accepted as actual external-input state");
            Check(highCancels==0&&monitor.Snapshot.Error==null&&monitor.Snapshot.High==true,"calibration HIGH is not a session fault or HV permission");
            Check(monitor.Stop(1000),"healthy HIGH reader stops only on explicit close");
        }
        int missingReads=0,missingCancel=0;
        using(var monitor=new CalibrationEmergencyMonitor(ms=>{Interlocked.Increment(ref missingReads);throw new CalibrationArtException(-26802);},()=>false,
            e=>Interlocked.Increment(ref missingCancel),null,null)){
            monitor.Start();Until(()=>Volatile.Read(ref missingCancel)==1,"missing session evidence fails closed");Check(monitor.Stop(1000),"missing context stopped");
            Check(missingReads==1&&monitor.Snapshot.High==null,"no trustworthy context cannot create permission or speculative retry");
        }
        Check(new CalibrationArtException(-200284).OfficialName=="ArtDAQError_SamplesNotYetAvailable"&&!new CalibrationArtException(-200284).IsReadTimeout,"not-yet-available distinguished from official Timeout");
    }
    sealed class NativeFake : IArtControlBoxNative,IArtTimedEmergencyNative
    {
        internal readonly Dictionary<IntPtr,string> Owned=new Dictionary<IntPtr,string>();
        internal readonly List<string> Calls=new List<string>();
        internal int Mode,Reads,Inside,ClearFailures;int next;
        internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Release=new ManualResetEvent(false);
        public void Load(string path){Calls.Add("load-fake");}
        public int ReadDeviceSerial(string device,out uint serial){serial=1023417009;Calls.Add("identity");return 0;}
        public int CreateTask(string name,out IntPtr task){task=new IntPtr(++next);Owned.Add(task,name);Calls.Add("create:"+name);return 0;}
        public int CreateInput(IntPtr task,string lines){Calls.Add("di:"+lines);return 0;}
        public int CreateOutput(IntPtr task,string lines){throw new Exception("No output allowed in this test");}
        public int Start(IntPtr task){Calls.Add("start:"+task);return 0;}
        public int Clear(IntPtr task){Check(Volatile.Read(ref Inside)==0,"Clear never overlaps input read");Calls.Add("clear:"+task);if(ClearFailures>0){ClearFailures--;return -99;}Owned.Remove(task);return 0;}
        public int Read(IntPtr task,out byte value,out int read,out int bytes){return Read(task,.3,out value,out read,out bytes);}
        public int Read(IntPtr task,double seconds,out byte value,out int read,out int bytes){
            Interlocked.Increment(ref Reads);Interlocked.Increment(ref Inside);
            try{if(Volatile.Read(ref Mode)==2){Entered.Set();Release.WaitOne();}value=0;read=bytes=1;return Volatile.Read(ref Mode)==1?-26802:0;}
            finally{Interlocked.Decrement(ref Inside);}
        }
        public int Write(IntPtr task,byte value,out int written){written=0;throw new Exception("No output allowed in this test");}
    }
    static void NativeTaskOwnership()
    {
        var native=new NativeFake();var io=new ArtControlBoxIo("FakeArt","DO_NOT_LOAD",native,"1023417009");var box=new ControlBox(io);int cancels=0;
        box.ConnectMonitored(e=>Interlocked.Increment(ref cancels),null);
        Check(box.Connected&&box.EmergencySignalHigh==false,"monitored connect obtains fresh low");
        int reads=native.Reads;box.RequireInputReady();Check(native.Reads>reads,"immediate input check requests new hardware result");
        native.ClearFailures=1;Volatile.Write(ref native.Mode,1);
        Until(()=>Volatile.Read(ref cancels)==1,"task timeout cancels round");
        Until(()=>box.EmergencySnapshot.RecoveryError!=null,"failed rebuild retains error");
        Check(io.CleanupPending&&io.HasOwnedTasks&&native.Owned.Count==1,"failed Clear retains original input handle");
        Check(native.Calls.Count(x=>x=="create:Calibration_P04")==1,"failed clear cannot create second input task");
        Reject(box.RequireInputReady,"rebuild failure cannot authorize capture");Volatile.Write(ref native.Mode,0);box.Disconnect();
        Check(!box.Connected&&!io.HasOwnedTasks&&native.Owned.Count==0,"explicit successful cleanup clears ownership");

        native=new NativeFake();io=new ArtControlBoxIo("FakeArt","DO_NOT_LOAD",native,"1023417009");box=new ControlBox(io);cancels=0;
        box.ConnectMonitored(e=>Interlocked.Increment(ref cancels),null);Volatile.Write(ref native.Mode,2);
        Check(native.Entered.WaitOne(1000),"owned input entered blocking fake read");
        try{
            Reject(box.Disconnect,"disconnect cannot clear blocked native handle");
            Check(box.Connected&&box.CleanupPending&&io.HasOwnedTasks&&!native.Calls.Any(x=>x.StartsWith("clear:")),"unreturned read retains resources without Clear");
        }finally{native.Release.Set();}
        Until(()=>Volatile.Read(ref native.Inside)==0,"blocked fake read returned");box.Disconnect();
        Check(!box.Connected&&!io.HasOwnedTasks,"retry cleanup only after true read completion");
        Check(!native.Calls.Any(x=>x.StartsWith("do:")||x.Contains("line5")||x.Contains("line6")||x.Contains("line7")),"no AI/AO/HV/optical/lamp routes introduced");
    }
    static void AsyncEntryGuards(string root)
    {
        var native=new NativeFake();var box=new ControlBox(new ArtControlBoxIo("FakeArt","DO_NOT_LOAD",native,"1023417009"));
        box.ConnectMonitored(e=>{},null);Volatile.Write(ref native.Mode,2);Check(native.Entered.WaitOne(1000),"entry guard fake read blocked");
        var first=box.BeginCaptureAsync();var second=box.BeginCaptureAsync();var impedance=box.SetShortCableAsync(true);
        Check(!first.IsCompleted&&second.IsFaulted&&impedance.IsFaulted,"begin/impedance transitions cannot overlap a fresh permission wait");
        Reject(()=>second.GetAwaiter().GetResult(),"duplicate begin denied");Reject(()=>impedance.GetAwaiter().GetResult(),"impedance denied during pending begin");
        Volatile.Write(ref native.Mode,0);native.Release.Set();Until(()=>{Application.DoEvents();return first.IsCompleted;},"first begin completes once");first.GetAwaiter().GetResult();
        Check(box.Capturing,"only first begin acquired capture state");box.EndCapture();box.Disconnect();
        Directory.CreateDirectory(root);
        foreach(string method in new[]{"Acquire","CaptureQualityOnly"})using(var form=new CalibrationForm(null,true)){
            ((System.Windows.Forms.Timer)Get(form,"safetyTimer")).Stop();form.HostErrorReported+=e=>{};
            ((TextBox)Get(form,"projectPath")).Text=root;
            native=new NativeFake();box=new ControlBox(new ArtControlBoxIo("FakeArt","DO_NOT_LOAD",native,"1023417009"));box.ConnectMonitored(e=>{},null);
            Set(form,"box",box);Set(form,"client",new FakeWorker());Set(form,"picoConnected",true);
            Volatile.Write(ref native.Mode,2);Check(native.Entered.WaitOne(1000),"form fresh permission read blocked");
            var one=(Task)form.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
            var two=(Task)form.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(form,null);
            Check(!one.IsCompleted&&two.IsCompleted&&(bool)Get(form,"capturing"),method+" reserves UI busy before first await; duplicate cannot clear owner");
            form.HostCancel();Volatile.Write(ref native.Mode,0);native.Release.Set();Until(()=>{Application.DoEvents();return one.IsCompleted;},method+" cancellation completes after fresh read");one.GetAwaiter().GetResult();
            Check(!(bool)Get(form,"capturing")&&!box.Capturing,method+" failure finally releases only its own pending/capture state");
            Set(form,"hostEntryActive",1);var denied=form.HostAcquireAsync(180,170,100,false);
            Check(denied.IsFaulted&&(int)Get(form,"hostEntryActive")==1,"duplicate host entry cannot clear first host owner's reservation");
            Reject(()=>denied.GetAwaiter().GetResult(),"duplicate host wrapper denied before I/O");Set(form,"hostEntryActive",0);box.Disconnect();
        }
    }
    sealed class FakeWorker : ICalibrationWorker
    {
        internal int Cancels;public bool Broken{get{return false;}}
        public void Cancel(){Interlocked.Increment(ref Cancels);}public Task<Reply> Send(Command c,int ms){throw new Exception("No worker command permitted");}
        public Task Close(int c=5000,int e=2000){return Task.FromResult(true);}public Task<bool> RetireUnresponsive(){return Task.FromResult(false);}
    }
    sealed class PlainIo : IControlBoxIo
    {public void Open(){}public bool ReadEmergencySignalHigh(){return false;}public void WriteShortCable(bool value){throw new Exception("No impedance in UI test");}public void Close(){}}
    static void FormEpochAndNoUiWait()
    {
        using(var form=new CalibrationForm(null,true)){
            ((System.Windows.Forms.Timer)Get(form,"safetyTimer")).Stop();
            var io=new PlainIo();var box=new ControlBox(io);box.Connect();var worker=new FakeWorker();
            Set(form,"box",box);Set(form,"client",worker);Set(form,"emergencySessionEpoch",7L);
            var oldBox=new ControlBox(new PlainIo());oldBox.Connect();
            Call(form,"LatchEmergencyStop",oldBox,7L);Call(form,"LatchEmergencyStop",box,6L);
            Check(worker.Cancels==0&&!(bool)Get(form,"stopRequested"),"old epoch or old owner cannot update current session");
            Call(form,"RequireUncancelledWorker");Check(true,"normal completed segment can retain worker");
            Task.Run(()=>Call(form,"LatchEmergencyStop",box,7L)).GetAwaiter().GetResult();
            Check(worker.Cancels==1&&(bool)Get(form,"stopRequested"),"background fault cancels without UI message pumping");
            Reject(()=>Call(form,"RequireUncancelledWorker"),"safety-cancelled worker is not silently reused");
            form.Dispose();Call(form,"LatchEmergencyStop",box,7L);Check(worker.Cancels==1,"disposed coordinator ignores old callback");
            box.Disconnect();oldBox.Disconnect();
        }
        using(var form=new CalibrationForm(null,false)){
            ((System.Windows.Forms.Timer)Get(form,"safetyTimer")).Stop();var box=new ControlBox(new PlainIo());box.Connect();
            var worker=new FakeWorker();Set(form,"box",box);Set(form,"client",worker);Set(form,"picoConnected",true);Set(form,"geometryConfirmed",true);
            Call(form,"RefreshState");Check(((Button)Get(form,"acquire")).Enabled,"uncancelled standalone session exposes acquire");
            form.HostCancel();Call(form,"RefreshState");
            Check(worker.Cancels==1&&!((Button)Get(form,"acquire")).Enabled&&!((Button)Get(form,"qualityCapture")).Enabled,"ordinary explicit stop disables reuse without closing any handle");
            Check(box.Connected&&Object.ReferenceEquals(Get(form,"client"),worker),"Stop preserves owners pending explicit release");
            Reject(()=>Call(form,"RequireUncancelledWorker"),"ordinary cancelled worker requires explicit release/reconnect");
            Set(form,"client",new FakeWorker());Call(form,"RequireUncancelledWorker");Check(true,"new explicitly connected worker does not inherit old stop latch");box.Disconnect();
        }
    }
    sealed class ConcurrentWriter : StringWriter
    {
        int inside;internal int Maximum;
        public override void WriteLine(string value){int count=Interlocked.Increment(ref inside);Maximum=Math.Max(Maximum,count);Thread.Sleep(1);base.WriteLine(value);Interlocked.Decrement(ref inside);}
    }
    static void CancellationWriter()
    {
        var output=new ConcurrentWriter();var writer=new CalibrationCommandWriter(output);
        writer.Send(new Command{Op="CAPTURE"});writer.Send(new Command{Op="CAPTURE"});
        Check(output.ToString().Split(new[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries).Length==2,"normal sequential rounds do not latch cancellation");
        output.GetStringBuilder().Clear();
        var send=Task.Run(()=>{for(int i=0;i<20;i++)writer.Send(new Command{Op="PING"});});
        var cancel=Task.Run(()=>{for(int i=0;i<20;i++)writer.Cancel();});Task.WaitAll(send,cancel);
        Check(output.Maximum==1,"cancel and commands never concurrently write the pipe");
        Reject(()=>writer.Send(new Command{Op="CAPTURE"}),"cancel latch prevents later CAPTURE resetting worker cancellation");
        writer.Retire();string[] lines=output.ToString().Split(new[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length==42&&lines.Last()!="CANCEL"&&Wire.Decode<Command>(lines.Last()).Op=="EXIT","cancelled owner can still request orderly EXIT");
        Check(lines.Count(x=>x=="CANCEL")==21&&lines.Where(x=>x!="CANCEL").All(x=>Wire.Decode<Command>(x).Op!="CAPTURE"),"commands remain complete and contain no forbidden capture");
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            FreshRequestsAndCadence();CadenceStress();RetryBudgetAndNoReset();DoubleTimeoutRecovery();LateRetryIsRejected();IdleGapStillExpires();InterReadDelayConsumesBudget();ReaderExceptionCompletesWaiters();HungReadKeepsOwnership();
            OfficialErrorsAndHigh();NativeTaskOwnership();AsyncEntryGuards(Path.GetFullPath(args[0]));FormEpochAndNoUiWait();CancellationWriter();
            Check((IntPtr)typeof(Art).GetField("module",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==IntPtr.Zero,"real ART driver was never loaded");
            Console.WriteLine("CALIBRATION_EMERGENCY_POLLING_PASS="+checks+"; fake delegates/native boundary only; no SDK, product, process worker or physical hardware started.");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
