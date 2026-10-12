using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

static class PressureAnalysisHostOptionsSmoke
{
    const BindingFlags B=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly host;static int checks;
    static object New(string n,params object[] a){return host.GetType("Xiongxin.OWTS.Desktop."+n,true).GetConstructors(B).Single(c=>!c.IsStatic&&c.GetParameters().Length==a.Length).Invoke(a);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    public sealed class Page:Control
    {
        public event EventHandler AnalysisRequested,SaveRequested,OverRangeDecisionRequested,EndpointReviewRequested;
        public string LocationAcquisitionId{get;set;}
        public bool HasActiveHardwareSession{get;set;}
        public bool IsBusy{get;set;}
        public bool DataSaved{get;set;}=true;
        public bool BackgroundPressureAnalysisEnabled{get;set;}
        public string PostProcessStateCode{get;set;}="COMPLETE";
        public string PressureStateCode{get;set;}="COMPLETE";
        public int CapturedPicoRangeIndex{get;set;}=5;
        public bool ManualAnalysisThresholdEnabled{get;set;}
        public double ManualAnalysisThresholdPc{get;set;}
        public bool ApplyAnalysisThresholdToAllRounds{get;set;}
        public TaskCompletionSource<bool> NextPreparation;
        public int Acks,Continuations,ProgressCalls,Interrupts;public bool Success,EndpointAvailable,SetupBeforeContinuation;public string Error;public double[,] Matrix;public object Geometry;
        public void ResetP04FeedbackSession(){}
        public void SetEndpointReviewAvailable(bool b){EndpointAvailable=b;}
        public void SetPressureSpectrumGeometry(object g){Geometry=g;}
        public void SetSpectrumDataMatrix(double[,] m){Matrix=m;}
        public bool CompleteBackgroundPressureAnalysis(string id,bool ok,string error){if(id!=LocationAcquisitionId||IsBusy||HasActiveHardwareSession)return false;Acks++;Success=ok;Error=error;return true;}
        public void ReportBackgroundPressureAnalysis(string id,string group,int done,int total,int queued,string error){ProgressCalls++;if(Continuations==0)SetupBeforeContinuation=true;if(error.Length>0)Error=error;Console.WriteLine("BACKGROUND_STAGE="+id+"; "+done+"/"+total+"; error="+error);}
        public void InterruptPressureContinuation(){Interrupts++;}
        public Task<bool> ContinueCompletedPressurePlanAsync(){Continuations++;return NextPreparation==null?Task.FromResult(false):NextPreparation.Task;}
        public void RaiseAnalysis(){AnalysisRequested?.Invoke(this,EventArgs.Empty);}
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new Exception("fresh fixture required");Directory.CreateDirectory(qa);
            string project=(string)typeof(PressureCumulativeAnalysisSmoke).GetMethod("Fixture",B).Invoke(null,new object[]{qa});
            host=Assembly.LoadFrom(Path.Combine(build,"Xiongxin.OWTS.Desktop.exe"));
            object settings=New("AppSettings"),state=New("ApplicationState");Set(Get(settings,"TaskMinus3"),"AlgorithmIndex",5);Set(Get(settings,"TaskMinus3"),"PhaseFilterEnabled",false);
            state.GetType().GetField("projectPath",B).SetValue(state,project);
            object log=New("Diagnostics",Path.Combine(qa,"diagnostics"));
            object owner=New("ModuleHost",settings,state,log,Path.Combine(build,"modules"));
            var reviewAssembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            reviewAssembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            reviewAssembly.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
            using(var shell=new Form{StartPosition=FormStartPosition.Manual,Location=new System.Drawing.Point(-30000,-30000),ShowInTaskbar=false})
            using(var page=new Page{LocationAcquisitionId="a",ManualAnalysisThresholdEnabled=true,ManualAnalysisThresholdPc=1e9})
            using(var guard=new Timer{Interval=30000}) {
                shell.Controls.Add(page);shell.Show();
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                guard.Tick+=(s,e)=>{Console.Error.WriteLine("Host option integration timeout / unexpected modal");Environment.Exit(1);};guard.Start();
                page.CreateControl();object trial=Call(owner,"GetPressureAnalysisTrial",page,project);
                string record=Path.Combine(project,"A_12.0kV_0.loc");Call(trial,"Commit","a",record);
                object session=New("FactoryPressureSession",page,typeof(object),"",settings,project,trial,(Action)(()=>{}),log,false);
                ((IDictionary)Get(owner,"factoryPressureSessions"))[page]=session;Call(owner,"ConnectPressureRoundSaving",page,session,170d);
                object queue=Get(trial,"AnalysisQueue");var release=new TaskCompletionSource<bool>();page.NextPreparation=release;
                page.RaiseAnalysis();page.RaiseAnalysis();
                while(page.Continuations==0){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                for(int i=0;i<10;i++){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                Check(page.Continuations==1&&page.Acks==0&&page.ProgressCalls==0&&!release.Task.IsCompleted,"real host starts next preparation before queued synchronous setup can begin");
                Check(page.BackgroundPressureAnalysisEnabled,"host explicitly enables asynchronous saved-round contract");
                Check(!page.SetupBeforeContinuation,"next preparation wins before synchronous analysis setup");
                page.ManualAnalysisThresholdEnabled=false;page.ManualAnalysisThresholdPc=1;
                page.NextPreparation=null;release.SetResult(false);
                while(page.Acks<1||(int)Get(queue,"PendingCount")>0){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                Check(page.Acks==1&&page.Success,"host reports one actual analysis success: "+page.Error);
                Check(page.Matrix!=null&&page.Matrix.GetLength(0)==0,"manual 1e9 pC reaches real algorithm and excludes fixture pulses");
                Check(page.Geometry!=null&&page.EndpointAvailable,"host transfers geometry and enables readonly endpoint review after success");
                Check(page.ProgressCalls>=2,"progress callback reaches page");
                Check(page.Continuations==1,"duplicate analysis request cannot create a second continuation");
                var choices=(IDictionary)Call(trial,"SnapshotThresholds",record,true,1e9,false);
                Check((double)Get(choices[record],"Pc")==1e9,"trial holds actual manual threshold");
                var holdSecond=new TaskCompletionSource<bool>();
                Task secondGate=(Task)Call(queue,"Enqueue","offline-gate-2",(Func<Task>)(async()=>await holdSecond.Task));
                string second=Path.Combine(project,"A_12.0kV_1.loc");Call(trial,"Commit","b",second);
                page.LocationAcquisitionId="b";page.ManualAnalysisThresholdEnabled=true;page.ManualAnalysisThresholdPc=1e9;
                page.RaiseAnalysis();
                while(page.Continuations<2){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                object originalMatrix=page.Matrix;page.LocationAcquisitionId="new-active";page.IsBusy=page.HasActiveHardwareSession=true;
                holdSecond.SetResult(true);
                while((int)Get(queue,"PendingCount")>0){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                Check(page.Acks==1&&Object.ReferenceEquals(page.Matrix,originalMatrix)&&page.IsBusy&&page.HasActiveHardwareSession,"real engine old result does not repaint or ACK a new active round");
                Check(page.Continuations==2,"background completion cannot issue another continuation");
                page.IsBusy=page.HasActiveHardwareSession=false;
                var holdChanged=new TaskCompletionSource<bool>();
                Task changedGate=(Task)Call(queue,"Enqueue","offline-gate-3",(Func<Task>)(async()=>await holdChanged.Task));
                string changed=Path.Combine(project,"A_12.0kV_2.loc");Call(trial,"Commit","c",changed);page.LocationAcquisitionId="c";page.RaiseAnalysis();
                while(page.Continuations<3){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                File.AppendAllText(changed,"\r\nOFFLINE MUTATION\r\n");holdChanged.SetResult(true);
                while((int)Get(queue,"PendingCount")>0){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                Check(page.Interrupts>0&&page.Error!=null&&page.Acks==2&&!page.Success&&page.DataSaved,"changed committed input causes actual background failure, not successful result or native stop");
                string heldRecord=Path.Combine(project,"A_12.0kV_3.loc");File.Copy(record,heldRecord);
                Call(trial,"Commit","ended-setup",heldRecord);page.LocationAcquisitionId="ended-setup";
                page.NextPreparation=new TaskCompletionSource<bool>();page.RaiseAnalysis();
                while(page.Continuations<4){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                for(int i=0;i<10;i++){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                Check((int)Get(queue,"PendingCount")>0&&!page.NextPreparation.Task.IsCompleted,"actual host setup waits for the incomplete fake next preparation");
                Call(trial,"End");
                while((int)Get(queue,"PendingCount")>0){Application.DoEvents();System.Threading.Thread.Sleep(5);}
                Check(page.Acks==2&&!page.NextPreparation.Task.IsCompleted,"trial End cancels logical setup without waiting for abandoned continuation or fabricating analysis ACK");
                ((IDisposable)session).Dispose();guard.Stop();
            }
            ((IDisposable)owner).Dispose();Console.WriteLine("PRESSURE_ANALYSIS_HOST_OPTIONS_PASS="+checks+"; HARDWARE_CALLS=0; REAL_ENGINE=TRUE; NO_BACKEND_CREATED=TRUE");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
