using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Managed queue and real control only. No Product Main, SDK, native worker or
// user records. Delayed work and statuses are explicit offline fake inputs.
internal static class PressureBackgroundAnalysisSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,B);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
    static void Pump(int ms=10){var w=Stopwatch.StartNew();while(w.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(1);}}
    static void Wait(Task task){var w=Stopwatch.StartNew();while(!task.IsCompleted&&w.ElapsedMilliseconds<5000)Pump();Check(task.IsCompleted,"bounded managed completion");}
    static void Queue(Assembly host)
    {
        var type=host.GetType("Xiongxin.OWTS.Desktop.PressureBackgroundAnalysisQueue",true);
        object q=Activator.CreateInstance(type,true);int first=0,second=0,replayed=0;
        var release=new TaskCompletionSource<bool>();
        Task a=(Task)Call(q,"Enqueue","a",(Func<Task>)(async()=>{first++;await release.Task;}));
        Task b=(Task)Call(q,"Enqueue","b",(Func<Task>)(()=>{second++;return Task.CompletedTask;}));
        Task duplicate=(Task)Call(q,"Enqueue","a",(Func<Task>)(()=>{replayed++;return Task.CompletedTask;}));
        Check(!a.IsCompleted&&!b.IsCompleted,"admission returns before managed analysis completes");
        Check(Object.ReferenceEquals(a,duplicate)&&replayed==0,"same saved acquisition admitted exactly once");
        Pump();Check(first==1&&second==0&&(int)Get(q,"PendingCount")==2,"FIFO serializes fake work, never drops second capture");
        Check((string)Get(q,"ActiveAcquisitionId")=="a","status identifies the actual active group");
        release.SetResult(true);Wait(a);Wait(b);Pump();
        Check(second==1&&replayed==0&&(int)Get(q,"PendingCount")==0,"both admitted groups complete once");
        Task fail=(Task)Call(q,"Enqueue","failed",(Func<Task>)(()=>Task.FromException(new InvalidDataException("fake saved input changed"))));
        Task after=(Task)Call(q,"Enqueue","after",(Func<Task>)(()=>Task.CompletedTask));Wait(fail);Wait(after);
        Check(fail.IsFaulted&&after.Status==TaskStatus.RanToCompletion,"failed analysis is not a successful ACK; other saved work remains available");
        var retained=fail.Exception;Check(retained!=null&&retained.GetBaseException().Message.Contains("fake saved input"),"actual failure remains observable");
        var finish=new TaskCompletionSource<bool>();
        Task current=(Task)Call(q,"Enqueue","current",(Func<Task>)(async()=>await finish.Task));
        int discarded=0;Task pending=(Task)Call(q,"Enqueue","pending",(Func<Task>)(()=>{discarded++;return Task.CompletedTask;}));
        Pump();Call(q,"End");Check(pending.IsCanceled&&discarded==0&&current.IsCanceled&&!finish.Task.IsCompleted,"leaving trial cancels logical jobs without waiting for pure computation or fabricating success");
        finish.SetResult(true);Wait(current);Pump();
        bool ended=false;try{Call(q,"Enqueue","new",(Func<Task>)(()=>Task.CompletedTask));}catch(TargetInvocationException e){ended=e.InnerException is InvalidOperationException;}
        Check(ended,"ended trial cannot admit work into a new page");
    }
    static void Trial(Assembly host,string qa)
    {
        string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
        string first=Path.Combine(project,"A_1.0kV_1.loc");File.WriteAllText(first,"OFFLINE SYNTHETIC INPUT");
        object trial=Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.PressureAnalysisTrial",true),B,null,new object[]{project},null);
        Check((bool)Call(trial,"Commit","first",first),"only existing saved project input commits");
        var hashes=(IDictionary)Call(trial,"SnapshotHashes");string hash=(string)hashes[Path.GetFullPath(first)];
        Check(hash.Length==64,"immutable saved hash captured at commit");
        hashes[Path.GetFullPath(first)]="changed-test-copy";
        Check((string)((IDictionary)Call(trial,"SnapshotHashes"))[Path.GetFullPath(first)]==hash,"caller cannot mutate trial hash snapshot");
        string[] paths=(string[])Call(trial,"Snapshot");paths[0]="mutated-copy";
        Check(((string[])Call(trial,"Snapshot"))[0]==Path.GetFullPath(first),"queued paths do not share active catalog arrays");
        Check(!(bool)Call(trial,"Commit","first",first),"duplicate save identity is not duplicated in accumulated analysis");
        Call(trial,"End");Check((bool)Get(trial,"Ended"),"trial owns queue lifetime");
    }
    static void Dispatcher(Assembly host)
    {
        var queueType=host.GetType("Xiongxin.OWTS.Desktop.PressureBackgroundAnalysisQueue",true);
        Func<Control,CancellationToken,object> bind=(control,token)=>queueType.GetMethod("CreateDispatcher",B).Invoke(null,new object[]{control,token});
        using(var noHandle=new Control()){
            int created=0;object context=bind(noHandle,CancellationToken.None);
            Task missing=(Task)Call(context,"Run",(Func<Task>)(()=>{created++;return Task.CompletedTask;}));
            Check(missing.IsCanceled&&created==0,"missing handle cancels without constructing UI on the pool");
        }
        using(var dispatcher=new Control()){
            var handle=dispatcher.Handle;object context=bind(dispatcher,CancellationToken.None);int ran=0;
            Task posted=(Task)Call(context,"Run",(Func<Task>)(()=>{ran++;return Task.CompletedTask;}));
            dispatcher.GetType().GetMethod("DestroyHandle",B).Invoke(dispatcher,null);
            Pump();Check(posted.IsCanceled&&ran==0,"destroy after accepted BeginInvoke cancels the task, never runs stale callback");
        }
        using(var dispatcher=new Control()){
            var handle=dispatcher.Handle;object context=bind(dispatcher,CancellationToken.None);int ran=0;
            Task posted=(Task)Call(context,"Run",(Func<Task>)(()=>{ran++;return Task.CompletedTask;}));
            dispatcher.Dispose();Pump();Check(posted.IsCanceled&&ran==0,"disposing owner cancels queued dispatch even if its message is lost");
        }
        using(var dispatcher=new Control()){
            var handle=dispatcher.Handle;object q=Activator.CreateInstance(queueType,true);
            object context=bind(dispatcher,(CancellationToken)Get(q,"Cancellation"));var gate=new TaskCompletionSource<bool>();int ran=0;
            Task active=(Task)Call(q,"Enqueue","ended-dispatch",(Func<Task>)(()=>(Task)Call(context,"Run",(Func<Task>)(async()=>{ran++;await gate.Task;}))));
            var watch=Stopwatch.StartNew();while(ran==0&&watch.ElapsedMilliseconds<2000)Pump();
            Check(ran==1&&!active.IsCompleted,"offline setup gate is active before End");
            Call(q,"End");Wait(active);Check(active.IsCanceled&&((CancellationToken)Get(q,"Cancellation")).IsCancellationRequested,"End cancels active logical dispatch without awaiting setup gate");
            gate.SetResult(true);Pump();Check(active.IsCanceled,"late gate completion never resurrects ended logical job");
        }
    }
    static void Workflow(Assembly module,string qa)
    {
        using(var f=new Form{Size=new Size(1256,780),StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000),ShowInTaskbar=false})
        using(var c=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.PressureTestControl",true)))
        {
            f.Controls.Add(c);c.Dock=DockStyle.Fill;Set(c,"SimulationMode",true);Set(c,"ProjectPath",qa);Set(c,"FrequencyReminderEnabled",false);
            Set(c,"BackgroundPressureAnalysisEnabled",true);Set(c,"MaximumPeakVoltageKv",20d);
            Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetAdvancedMode",true);
            f.Show();Pump();int analyses=0;c.GetType().GetEvent("AnalysisRequested").AddEventHandler(c,(EventHandler)((s,e)=>analyses++));
            Set(c,"cycleAcquisitionId","saved-a");Set(c,"postProcessStateCode","SAVE_REQUESTED");Set(c,"pressureStateCode","COMPLETE");
            Check((bool)Call(c,"CompleteSave",true,""),"real save ACK accepted in queue-capable mode");
            Check((bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed")&&(string)Get(c,"PostProcessStateCode")=="COMPLETE","save admits next round without fabricating analysis success");
            Check(analyses==1&&!(bool)Get(c,"PressureParametersLocked")&&!(bool)Get(c,"PressureNavigationLocked"),"only managed analysis pending does not lock parameters/navigation");
            Check(!(bool)Call(c,"CompleteSave",true,"")&&analyses==1,"duplicate save ACK cannot enqueue duplicate work");
            Check(!(bool)Call(c,"CompleteBackgroundPressureAnalysis","old-a",true,""),"old acquisition result is rejected");
            Call(c,"ReportBackgroundPressureAnalysis","saved-a","A_6.2kV_1",0,2,1,"");
            Check(((string)Get(c,"BackgroundPressureAnalysisStatusText")).Contains("A_6.2kV_1")&&!((Control)Get(c,"pressureActionButton")).Text.Contains("正在分析"),"background group appears in status, not main pressure button");
            Set(c,"ActionHandshakeEnabled",true);long before=(long)Get(c,"PressureRequestSequence");Call(c,"RequestPressure");
            Check((long)Get(c,"PressureRequestSequence")==before+1&&(bool)Get(c,"ActionPending"),"next synthetic mailbox can be issued while saved analysis is unfinished");
            long seq=(long)Get(c,"ActionSequence");string json=(string)Get(c,"ActionParametersJson");
            Check(!(bool)Call(c,"CompleteBackgroundPressureAnalysis","saved-a",true,""),"late old result cannot complete new active round");
            Check((long)Get(c,"ActionSequence")==seq&&(string)Get(c,"ActionParametersJson")==json&&(bool)Get(c,"IsBusy"),"late result never changes new mailbox, context or busy state");
            Check((bool)Call(c,"CompleteAction",seq,true,""),"test explicitly acknowledges only its fake mailbox");
            Set(c,"cycleAcquisitionId","saved-b");Set(c,"dataSaved",true);Set(c,"postProcessStateCode","COMPLETE");Set(c,"pressureStateCode","STOPPED");
            Check(!(bool)Call(c,"CompleteBackgroundPressureAnalysis","saved-b",true,""),"STOPPED cannot be restored by analysis success");
            Set(c,"postProcessStateCode","CANCELED");Check(!(bool)Call(c,"CompleteBackgroundPressureAnalysis","saved-b",false,"fake failure"),"ABORTED/CANCELED stays separate from background failure");
            Check((string)Get(c,"PressureStateCode")=="STOPPED"&&(string)Get(c,"PostProcessStateCode")=="CANCELED"&&!(bool)Get(c,"IsBusy"),"background result does not reactivate stopped mailbox");
            Set(c,"postProcessStateCode","COMPLETE");Set(c,"pressureStateCode","COMPLETE");Set(c,"dataAnalyzed",false);
            Check((bool)Call(c,"CompleteBackgroundPressureAnalysis","saved-b",false,"fake saved analysis failure"),"same idle saved round records actual background failure");
            Check((bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed")&&(string)Get(c,"PostProcessStateCode")=="COMPLETE","analysis failure keeps real saved evidence, not false analyzed success");
            Check((bool)Call(c,"CompleteBackgroundPressureAnalysis","saved-b",true,""),"same idle saved round can receive genuine analysis completion");
            Call(c,"ReportBackgroundPressureAnalysis","saved-b","B_8.7kV_1",0,1,0,"fake retained failure");
            Call(c,"ReportBackgroundPressureAnalysis","saved-b","B_8.7kV_1",1,1,0,"");
            Check(((string)Get(c,"BackgroundPressureAnalysisStatusText")).Contains("另有分析未完成"),"later success does not hide earlier background failure");
            Set(c,"dataSaved",false);Set(c,"postProcessStateCode","SAVE_REQUESTED");
            Check((bool)Call(c,"CompleteSave",false,"fake save failure"),"save failure remains a failed save");
            Check(!(bool)Get(c,"DataSaved")&&(string)Get(c,"PostProcessStateCode")=="SAVE_FAILED"&&analyses==1,"failed save is never admitted to background analysis");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var host=Assembly.LoadFrom(Path.Combine(build,"Xiongxin.OWTS.Desktop.exe"));
            var module=Assembly.LoadFrom(Path.Combine(build,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            Queue(host);Trial(host,qa);Dispatcher(host);Workflow(module,qa);
            Console.WriteLine("PRESSURE_BACKGROUND_ANALYSIS_PASS="+checks+"; HARDWARE_CALLS=0; SDK_CALLS=0; PRODUCT_ENTRY=FALSE; USER_FILES_WRITTEN=FALSE");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
