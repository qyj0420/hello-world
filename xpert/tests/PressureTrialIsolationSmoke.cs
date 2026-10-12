using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

// Reflection fixtures only: never constructs a runtime backend or opens devices.
internal static class PressureTrialIsolationSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Assembly host;static object state,settings,owner,log;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static void Until(Func<bool> done){var clock=Stopwatch.StartNew();while(!done()&&clock.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(1);}Check(done(),"bounded managed completion");}
    static object New(string name,params object[] args){return host.GetType("Xiongxin.OWTS.Desktop."+name,true).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==args.Length).Invoke(args);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);}
    static object Field(object target,string name){return target.GetType().GetField(name,F).GetValue(target);}
    static void Set(object target,string name,object value){target.GetType().GetProperty(name,F).SetValue(target,value,null);}
    static string[] Paths(object trial){return (string[])Call(trial,"Snapshot");}
    static object Prepared(object trial,string id,string path){var task=(Task)Call(trial,"PrepareCommitAsync",id,path);Until(()=>task.IsCompleted);if(task.IsFaulted)throw task.Exception.GetBaseException();return task.GetType().GetProperty("Result").GetValue(task,null);}
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException||e.InnerException is InvalidOperationException;}Check(rejected,why);}
    public sealed class Page:Control
    {
        public event EventHandler SaveRequested,AnalysisRequested,OverRangeDecisionRequested;
        string acquisition;public int IdentityReads;public Action IdentityRead;
        public string LocationAcquisitionId{get{IdentityReads++;IdentityRead?.Invoke();return acquisition;}set{acquisition=value;}}
        public string PostProcessStateCode{get;set;}="SAVE_REQUESTED";
        public string PressureStateCode{get;set;}="COMPLETE";
        public bool HasActiveHardwareSession{get;set;}
        public bool AutomaticPressureStopped{get;set;}
        public string EmergencyStopStatusCode{get;set;}="SIMULATION";
        public TaskCompletionSource<string> Pending=new TaskCompletionSource<string>();
        public int Saves,Acks;public bool LastSuccess;
        public void ResetP04FeedbackSession(){}
        public Task<string> SaveCurrentPressureRoundAsync(double velocity){Saves++;return Pending.Task;}
        public void CompleteSave(bool success,string error){Acks++;LastSuccess=success;}
        public void RaiseSave(){if(SaveRequested!=null)SaveRequested(this,EventArgs.Empty);}
    }
    static object Session(Page page,object trial,string project)
    {
        object session=New("FactoryPressureSession",page,typeof(object),"",settings,project,trial,(Action)(()=>{}),log,false);
        ((IDictionary)Field(owner,"factoryPressureSessions"))[page]=session;
        Call(owner,"ConnectPressureRoundSaving",page,session,170d);return session;
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string project=Path.Combine(qa,"p");Directory.CreateDirectory(project);
            string p1=Path.Combine(project,"A_1kV_1.loc"),p2=Path.Combine(project,"B_1kV_2.loc"),p3=Path.Combine(project,"A_1kV_retry.loc");
            foreach(string path in new[]{p1,p2,p3})File.WriteAllText(path,"offline synthetic record");
            host=Assembly.LoadFrom(Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));settings=New("AppSettings");state=New("ApplicationState");
            state.GetType().GetField("projectPath",F).SetValue(state,project);
            log=New("Diagnostics",Path.Combine(qa,"diagnostics"));
            owner=New("ModuleHost",settings,state,log,Path.Combine(args[0],"modules"));
            object staged=New("PressureAnalysisTrial",project),other=New("PressureAnalysisTrial",project);
            object prepared=Prepared(staged,"staged",p1);
            Check(Paths(staged).Length==0,"background file verification alone never commits or ACKs");
            Reject(()=>Call(other,"CommitPrepared",prepared),"prepared receipt cannot cross trial ownership");
            Check((bool)Call(staged,"CommitPrepared",prepared)&&Paths(staged).Length==1,"verified receipt commits on owner exactly once");
            Check(!(bool)Call(staged,"CommitPrepared",prepared)&&Paths(staged).Length==1,"receipt replay cannot double count");
            object changed=Prepared(other,"changed",p3);File.AppendAllText(p3,"changed");
            Reject(()=>Call(other,"CommitPrepared",changed),"file change after background hash cannot commit stale evidence");
            object ended=Prepared(other,"ended",p2);Call(other,"End");
            Reject(()=>Call(other,"CommitPrepared",ended),"cleanup wins while file worker completes; no late commit");
            using(var stopped=new Page{LocationAcquisitionId="stop-during-hash"}){
                object trial=Call(owner,"GetPressureAnalysisTrial",stopped,project);Session(stopped,trial,project);
                stopped.IdentityRead=()=>{if(stopped.IdentityReads==3){stopped.AutomaticPressureStopped=true;stopped.PressureStateCode="STOPPED";}};
                stopped.RaiseSave();stopped.Pending.SetResult(p2);Until(()=>stopped.Acks==1);
                Check(!stopped.LastSuccess&&Paths(trial).Length==0,"Stop between file hash and commit cannot ACK success or enter analysis");
            }
            using(var finished=new Page{LocationAcquisitionId="finish-valid-round",AutomaticPressureStopped=true}){
                object trial=Call(owner,"GetPressureAnalysisTrial",finished,project);Session(finished,trial,project);
                finished.RaiseSave();finished.Pending.SetResult(p2);Until(()=>finished.Acks==1);
                Check(finished.LastSuccess&&Paths(trial).Length==1,"finish-future-rounds alone preserves a genuinely complete saved current round");
            }
            using(var unreadable=new Page{LocationAcquisitionId="hash-read-failure"})
            using(var exclusive=new FileStream(p2,FileMode.Open,FileAccess.Read,FileShare.None)){
                object trial=Call(owner,"GetPressureAnalysisTrial",unreadable,project);Session(unreadable,trial,project);
                unreadable.RaiseSave();unreadable.Pending.SetResult(p2);Until(()=>unreadable.Acks==1);
                Check(!unreadable.LastSuccess&&Paths(trial).Length==0,"background record read failure cannot commit, ACK success or enter next-round analysis");
            }
            using(var switched=new Page{LocationAcquisitionId="old-during-hash"}){
                object trial=Call(owner,"GetPressureAnalysisTrial",switched,project);Session(switched,trial,project);
                switched.IdentityRead=()=>{if(switched.IdentityReads==3)switched.LocationAcquisitionId="new-round";};
                switched.RaiseSave();switched.Pending.SetResult(p2);Until(()=>switched.IdentityReads>=4);
                Check(switched.Acks==0&&Paths(trial).Length==0,"ID switch during background verification cannot ACK or commit into the new round");
            }
            using(var exiting=new Page{LocationAcquisitionId="exit-before-hash"}){
                object trial=Call(owner,"GetPressureAnalysisTrial",exiting,project);Session(exiting,trial,project);
                // Deterministic lifecycle race at the saved-file handoff. The
                // real Close path is not called: no backend exists in this test.
                exiting.IdentityRead=()=>{if(exiting.IdentityReads==2)Call(owner,"BeginPressurePageExit",exiting);};
                exiting.RaiseSave();exiting.Pending.SetResult(p2);
                string timing=(string)log.GetType().GetProperty("CurrentLogPath",F).GetValue(log,null);
                Until(()=>File.Exists(timing)&&File.ReadAllText(timing).Contains("acquisition=exit-before-hash; stage=save"));
                Check(exiting.Acks==0&&Paths(trial).Length==0,"explicit page exit rejects pending file handoff without success/failure ACK or continuation");
            }
            using(var first=new Page()){
                object trial=Call(owner,"GetPressureAnalysisTrial",first,project);
                Check((bool)Call(trial,"Commit","round-1",p1),"first committed round joins trial");
                Check(!(bool)Call(trial,"Commit","round-1",p3)&&Paths(trial).Length==1,"same acquisition under new filename cannot count twice");
                Check(!(bool)Call(trial,"Commit","round-2",p1.ToUpperInvariant())&&Paths(trial).Length==1,"path casing cannot duplicate record");
                Check((bool)Call(trial,"Commit","round-2",p2),"next phase joins same trial");
                string[] snapshot=Paths(trial);snapshot[0]="changed";Check(Paths(trial)[0]==p1,"batch snapshot cannot mutate trial");
                Check(Object.ReferenceEquals(trial,Call(owner,"GetPressureAnalysisTrial",first,project+"\\")),"phase/backend reconfigure preserves trial");
                Reject(()=>Call(trial,"Commit","bad",Path.Combine(project,"missing.loc")),"failed or missing save cannot join");
                string outside=Path.Combine(qa,"outside.loc");File.WriteAllText(outside,"synthetic");
                Reject(()=>Call(trial,"Commit","outside",outside),"different project's files cannot join");
                Reject(()=>Call(trial,"Commit","",p1),"missing acquisition identity cannot join");
                first.Dispose();Reject(()=>Paths(trial),"disposed page ends trial");
                Check(((IDictionary)Field(owner,"pressureAnalysisTrials")).Count==0,"ended trial released from host");
                Check(File.Exists(p1)&&File.Exists(p2),"ending trial retains saved source files");
            }
            using(var next=new Page{LocationAcquisitionId="current"}){
                object trial=Call(owner,"GetPressureAnalysisTrial",next,project);Check(Paths(trial).Length==0,"reopening SAME project begins empty cumulative trial");
                object session=Session(next,trial,project);next.RaiseSave();Check(next.Saves==1&&next.Acks==0,"save acknowledgement waits for actual completion");
                next.Pending.SetResult(p1);Until(()=>next.Acks==1);Check(next.LastSuccess&&Paths(trial).Length==1,"successful saved round committed once");
                next.RaiseSave();Check(next.Saves==1&&next.Acks==2&&Paths(trial).Length==1,"retry does not write or count the same acquisition again");
                ((IDisposable)session).Dispose();next.RaiseSave();Check(next.Acks==2,"replaced session subscriptions detached");
                var newSession=Session(next,trial,project);next.LocationAcquisitionId="late";next.Pending=new TaskCompletionSource<string>();next.RaiseSave();
                ((IDisposable)newSession).Dispose();Session(next,trial,project);next.Pending.SetResult(p2);Application.DoEvents();
                Check(next.Acks==2&&Paths(trial).Length==1,"late old-session save cannot commit or acknowledge the new session");
                next.LocationAcquisitionId="failure";next.Pending=new TaskCompletionSource<string>();next.RaiseSave();next.Pending.SetException(new IOException("injected save failure"));Until(()=>next.Acks==3);
                Check(next.Acks==3&&!next.LastSuccess&&Paths(trial).Length==1,"failed save stays out of analysis and reports failure");
                object replacement=Call(owner,"GetPressureAnalysisTrial",next,qa);Check(!Object.ReferenceEquals(trial,replacement)&&Paths(replacement).Length==0,"project change resets trial even on same page");
                Reject(()=>Paths(trial),"project change invalidates old trial");
            }
            Check(((IDictionary)Field(owner,"factoryPressureSessions")).Count==0,"disposed page releases backend dictionary entry");
            ((IDisposable)owner).Dispose();Console.WriteLine("PRESSURE_TRIAL_ISOLATION_PASS="+checks+"; HardwareAccessed=false");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
