using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Managed delayed-work fixtures only. Product entry and hardware are never run.
internal static class PressureAnalysisStopResumeSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly host;static int checks;
    static object New(string name,params object[] args){return host.GetType("Xiongxin.OWTS.Desktop."+name,true).GetConstructors(B).Single(c=>!c.IsStatic&&c.GetParameters().Length==args.Length).Invoke(args);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static void Pump(int ms=20){var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(1);}}
    static void Until(Func<bool> condition){var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<3000)Pump();Check(condition(),"bounded managed completion");}
    public sealed class Page:Control
    {
        public event EventHandler FinishCurrentRoundRequested;
        public void ResetP04FeedbackSession(){}
        public void Stop(){FinishCurrentRoundRequested?.Invoke(this,EventArgs.Empty);}
    }
    public static class Bridge {public static int Revocations;public static void RevokeBatch(){Revocations++;}}
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();host=Assembly.LoadFrom(Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string first=Path.Combine(qa,"A_12kV_1.loc"),second=Path.Combine(qa,"B_12kV_2.loc");
            File.WriteAllText(first,"SYNTHETIC SAVED ROUND A");File.WriteAllText(second,"SYNTHETIC SAVED ROUND B");
            object settings=New("AppSettings"),state=New("ApplicationState");state.GetType().GetField("projectPath",B).SetValue(state,qa);
            object owner=New("ModuleHost",settings,state,New("Diagnostics",Path.Combine(qa,"logs")),Path.Combine(args[0],"modules"));
            using(var page=new Page()) {
                var handle=page.Handle;object trial=Call(owner,"GetPressureAnalysisTrial",page,qa);
                Call(trial,"Commit","a",first);Call(trial,"Commit","b",second);Call(trial,"SnapshotThresholds",first,true,123d,true);
                var hashes=(IDictionary)Call(trial,"SnapshotHashes");string firstHash=(string)hashes[first],secondHash=(string)hashes[second];
                object old=Get(trial,"AnalysisQueue"),session=New("FactoryPressureSession",page,typeof(Bridge),"",settings,qa,trial,(Action)(()=>{}),New("Diagnostics"));
                ((IDictionary)Get(owner,"factoryPressureSessions"))[page]=session;
                var delayedCpu=new TaskCompletionSource<bool>();int entered=0,lateApplied=0,pendingEntered=0;
                Task current=(Task)Call(old,"Enqueue","a",(Func<Task>)(async()=>{
                    Interlocked.Increment(ref entered);await delayedCpu.Task.ConfigureAwait(false);
                    if((bool)Call(owner,"IsCurrentPressureAnalysisEpoch",page,trial,old))Interlocked.Increment(ref lateApplied);
                }));
                Task pending=(Task)Call(old,"Enqueue","b",(Func<Task>)(()=>{pendingEntered++;return Task.CompletedTask;}));
                Until(()=>entered==1);var timing=Stopwatch.StartNew();page.Stop();timing.Stop();
                object resumed=Get(trial,"AnalysisQueue");
                Check(timing.ElapsedMilliseconds<500&&!delayedCpu.Task.IsCompleted,"Stop does not await delayed pure computation");
                Check(current.IsCanceled&&pending.IsCanceled&&pendingEntered==0,"Stop cancels logical active/pending jobs, never acknowledges success");
                Check((int)Get(old,"PendingCount")==0&&((CancellationToken)Get(old,"Cancellation")).IsCancellationRequested,"old epoch exposes stopped logical status");
                Check(!Object.ReferenceEquals(old,resumed)&&!(bool)Get(trial,"Ended")&&Bridge.Revocations==1,"specific Stop replaces analysis epoch while retaining the trial and revoking the batch");
                Check(!(bool)Call(owner,"IsCurrentPressureAnalysisEpoch",page,trial,old)&&(bool)Call(owner,"IsCurrentPressureAnalysisEpoch",page,trial,resumed),"old epoch cannot pass the actual Host publication guard");
                Check(((string[])Call(trial,"Snapshot")).SequenceEqual(new[]{first,second})&&File.Exists(first)&&File.Exists(second),"all committed saved records survive Stop unchanged");
                var keptHashes=(IDictionary)Call(trial,"SnapshotHashes");var thresholds=(IDictionary)Call(trial,"SnapshotThresholds",first,true,123d,false);
                Check((string)keptHashes[first]==firstHash&&(string)keptHashes[second]==secondHash&&(double)Get(thresholds[second],"Pc")==123d,"source hashes and thresholds survive analysis cancellation");
                Check(!(bool)Call(resumed,"Contains","a")&&!(bool)Call(resumed,"Contains","b"),"canceled admission identities do not prevent explicit recomputation");
                int recomputed=0;Task restart=(Task)Call(resumed,"Enqueue","a",(Func<Task>)(()=>{recomputed=((string[])Call(trial,"Snapshot")).Length;return Task.CompletedTask;}));
                Until(()=>restart.IsCompleted);Check(restart.Status==TaskStatus.RanToCompletion&&recomputed==2,"explicit resumed job can recompute the full saved trial");
                delayedCpu.SetResult(true);Pump(80);Check(lateApplied==0&&current.IsCanceled,"late old computation cannot resurrect canceled completion or apply results");
                var review=new Control();var reviewHandle=review.Handle;int disposedThread=0;review.Disposed+=(s,e)=>disposedThread=Thread.CurrentThread.ManagedThreadId;
                object ownership=New("PressureAnalysisReviewOwnership",page,review);var engine=new TaskCompletionSource<bool>();var cancel=new CancellationTokenSource();
                Call(ownership,"Watch",engine.Task,cancel.Token);cancel.Cancel();Check(!review.IsDisposed,"cancel does not dispose review while its active engine still owns computation");
                int uiThread=Thread.CurrentThread.ManagedThreadId;Task.Run(()=>engine.SetResult(true)).Wait();Until(()=>review.IsDisposed);
                Check(disposedThread==uiThread,"late engine completion reclaims review on actual UI owner, independent of canceled dispatcher");
                ((IDisposable)ownership).Dispose();cancel.Dispose();
                using(var disposableOwner=new Control()) {
                    var ownerHandle=disposableOwner.Handle;var ownedReview=new Control();var ownedHandle=ownedReview.Handle;var heldEngine=new TaskCompletionSource<bool>();
                    object ownerLease=New("PressureAnalysisReviewOwnership",disposableOwner,ownedReview);Call(ownerLease,"Watch",heldEngine.Task,CancellationToken.None);
                    disposableOwner.Dispose();Check(ownedReview.IsDisposed&&!heldEngine.Task.IsCompleted,"UI owner disposal also reclaims temporary review without waiting for pure engine");
                    heldEngine.SetResult(true);((IDisposable)ownerLease).Dispose();
                }
                ((IDisposable)session).Dispose();
            }
            ((IDisposable)owner).Dispose();Console.WriteLine("PRESSURE_ANALYSIS_STOP_RESUME_PASS="+checks+"; HARDWARE_CALLS=0; SDK_CALLS=0; PRODUCT_ENTRY=FALSE; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
