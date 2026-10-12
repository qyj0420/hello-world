using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Real shipped Browser DLL, synthetic projects only. The shared fixture produces
// signed formal records and their raw dependencies, never user data or settings.
internal static class BrowserJointAsyncResponsiveSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr window);
    sealed class ActionUiState {internal Control Control;internal bool Visible,Created,NativeVisible;internal IntPtr Handle;}
    static System.Collections.Generic.IEnumerable<Control> ActionDescendants(Control control){yield return control;foreach(Control child in control.Controls)foreach(var nested in ActionDescendants(child))yield return nested;}
    static bool UserPaint(Control control){return (bool)typeof(Control).GetMethod("GetStyle",F).Invoke(control,new object[]{ControlStyles.UserPaint});}
    static object Field(object value,string name){return value.GetType().GetField(name,F).GetValue(value);}
    static object Get(object value,string name){var p=value.GetType().GetProperty(name,F);return p!=null?p.GetValue(value,null):Field(value,name);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks++;}
    static void Pump(Func<bool> finished){var timeout=Stopwatch.StartNew();while(!finished()&&timeout.ElapsedMilliseconds<30000){Application.DoEvents();Thread.Sleep(2);}Check(finished(),"bounded UI operation completes");}
    static void Complete(Task task){Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
    static Exception Failure(Task task){Pump(()=>task.IsCompleted);try{task.GetAwaiter().GetResult();return null;}catch(Exception ex){return ex;}}
    static Task Save(Control browser,object plan,string root,CancellationToken token)
    {return (Task)Call(browser,"CommitJointDialogAsync",plan,"AddJoint",root,(int)Field(browser,"projectGeneration"),(int)Field(browser,"selectionGeneration"),token);}
    static void NoWrite(object fixture,string label)
    {
        string root=(string)Field(fixture,"Root");Control browser=(Control)Field(fixture,"Browser");
        Check(((byte[])Field(fixture,"CableBytes")).SequenceEqual(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))),label+": exact cable bytes retained");
        Check(Directory.GetFiles(root,"CABLE.before-joint-*.bak").Length==0,label+": no transaction/backup claimed");
        Check(!(bool)Get(browser,"IsBusy")&&((Control)Field(browser,"tree")).Enabled&&((Control)Field(browser,"actions")).Enabled,label+": busy state and inputs restored");
    }
    static void Expand(Assembly assembly,object fixture)
    {
        string root=(string)Field(fixture,"Root");var paths=(string[])Field(fixture,"Paths");Type type=assembly.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);
        var records=paths.Select(path=>type.GetMethod("Load",F).Invoke(null,new object[]{root,path,true})).ToArray();
        for(int i=0;i<3;i++)using(var raw=new FileStream(Path.Combine(root,"raw"+i+".bin"),FileMode.Open,FileAccess.Write,FileShare.None))raw.SetLength(8L*1024*1024);
        foreach(var record in records) {
            var values=new double[1000000];Array.Copy((double[])Field(record,"Average"),values,3501);type.GetField("Average",F).SetValue(record,values);
            foreach(var raw in (Array)Field(record,"RawFiles"))raw.GetType().GetField("Sha256",F).SetValue(raw,type.GetMethod("HashFile",F).Invoke(null,new object[]{Path.Combine(root,(string)Field(raw,"RelativePath"))}));
            Call(record,"Save",root);
        }
    }
    static object AsyncCallback<T>(Func<CancellationToken,Task> action){return new Func<T,string,CancellationToken,Task>((plan,operation,token)=>action(token));}
    static void SetCallback(Form dialog,Type planType,Func<CancellationToken,Task> callback)
    {Call(dialog,"ConfigureCommitAsync",typeof(BrowserJointAsyncResponsiveSmoke).GetMethod("AsyncCallback",F).MakeGenericMethod(planType).Invoke(null,new object[]{callback}));}
    static Form Dialog(Assembly assembly,string root,string qa)
    {
        var snapshot=assembly.GetType("HVX.ProjectCables.CableJointStore",true).GetMethod("Read",F).Invoke(null,new object[]{root});
        var dialog=(Form)Activator.CreateInstance(assembly.GetType("HVX.ProjectCables.CableJointDialog",true),F,null,new object[]{snapshot,680d,"A",false,null},null);
        Call(dialog,"ConfigureMaterialCatalog",Path.Combine(qa,"private-materials.ini"));return dialog;
    }
    static void DialogLifecycle(Assembly assembly,string root,string qa)
    {
        Type planType=assembly.GetType("HVX.ProjectCables.CableJointPlan",true);
        using(var dialog=Dialog(assembly,root,qa)) {
            int calls=0;var done=new TaskCompletionSource<bool>();
            SetCallback(dialog,planType,token=>{calls++;return done.Task;});dialog.Show();Application.DoEvents();
            ((Button)dialog.Controls.Find("ConfirmJoint",true).Single()).PerformClick();
            Check((bool)Get(dialog,"IsCommitPending")&&!dialog.Controls.Find("JointPosition",true).Single().Enabled,"async confirmation disables inputs but leaves dialog open");
            Call(dialog,"SubmitPrepared");Check(calls==1,"duplicate confirmation does not enqueue a second save");
            done.SetException(new IOException("isolated source changed"));Pump(()=>!(bool)Get(dialog,"IsCommitPending"));
            Check(dialog.Visible&&dialog.DialogResult!=DialogResult.OK&&Get(dialog,"Plan")==null,"failed save stays in editor with no successful plan");
            Check(((NumericUpDown)dialog.Controls.Find("JointPosition",true).Single()).Value==680m&&
                !String.IsNullOrWhiteSpace(dialog.Controls.Find("JointValidation",true).Single().Text)&&
                !dialog.Controls.Find("JointValidation",true).Single().Text.Contains("isolated source changed")&&
                ((string)Get(dialog,"LastDiagnosticError")).Contains("isolated source changed"),"failure preserves input and short customer refusal; technical evidence stays in diagnostics");
            var canceled=new TaskCompletionSource<bool>();SetCallback(dialog,planType,token=>{token.Register(()=>canceled.TrySetCanceled());return canceled.Task;});
            ((Button)dialog.Controls.Find("ConfirmJoint",true).Single()).PerformClick();dialog.Close();Pump(()=>!(bool)Get(dialog,"IsCommitPending"));
            Check(dialog.Visible&&dialog.Controls.Find("JointValidation",true).Single().Text.Contains("取消"),"close during precommit work cancels but cannot hide its result");
            dialog.Close();
        }
        using(var dialog=Dialog(assembly,root,qa)) {
            var done=new TaskCompletionSource<bool>();CancellationToken observed=CancellationToken.None;
            SetCallback(dialog,planType,token=>{observed=token;return done.Task;});dialog.Show();Application.DoEvents();
            ((Button)dialog.Controls.Find("ConfirmJoint",true).Single()).PerformClick();dialog.Close();
            Check(observed.IsCancellationRequested&&dialog.Visible,"late cancel waits for transaction outcome");
            done.SetResult(true);Pump(()=>dialog.IsDisposed||!dialog.Visible);
            Check(dialog.DialogResult==DialogResult.OK,"callback reporting committed closes successfully despite late cancellation");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Control.CheckForIllegalCrossThreadCalls=true;
            string build=Path.GetFullPath(args[0]);string qa=Path.Combine(Path.GetFullPath(args[1]),"joint-responsive-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            Type fixtureType=typeof(BrowserMeanSourceIntegritySmoke).GetNestedType("Fixture",F);
            using(var fixture=(IDisposable)Activator.CreateInstance(fixtureType,F,null,new object[]{assembly,Path.Combine(qa,"formal"),true},null)) {
                string root=(string)Field(fixture,"Root");Control browser=(Control)Field(fixture,"Browser");var paths=(string[])Field(fixture,"Paths");
                Expand(assembly,fixture);Call(fixture,"Mean");DialogLifecycle(assembly,root,qa);
                // This console harness pumps windows without Application.Run;
                // modeless test-dialog teardown may uninstall its auto context.
                // Production handlers execute inside the normal WinForms loop.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                int ticks=0;Stopwatch finalSaveWatch=null;double firstTick=double.NaN,lastTick=0,maxHeartbeatGap=0,firstPaint=double.NaN;
                using(var timer=new System.Windows.Forms.Timer{Interval=16}) {
                    timer.Tick+=delegate{ticks++;if(finalSaveWatch!=null){double now=finalSaveWatch.Elapsed.TotalMilliseconds;if(double.IsNaN(firstTick))firstTick=now;maxHeartbeatGap=Math.Max(maxHeartbeatGap,now-lastTick);lastTick=now;}};timer.Start();
                    var watch=Stopwatch.StartNew();var ready=(Task)Call(browser,"PrepareJointEditorSnapshotAsync",CancellationToken.None);long dispatch=watch.ElapsedMilliseconds;int initial=ticks;
                    Complete(ready);Console.WriteLine("OPEN_MS="+watch.ElapsedMilliseconds+"; DISPATCH_MS="+dispatch+"; UI_TICKS="+(ticks-initial));
                    Check(dispatch<350&&ticks-initial>=3,"million-sample opening validation leaves UI heartbeat responsive");NoWrite(fixture,"open validation");
                    using(var cancel=new CancellationTokenSource()) {
                        var pending=(Task)Call(browser,"PrepareJointEditorSnapshotAsync",cancel.Token);cancel.Cancel();
                        var failure=Failure(pending);Check(failure is OperationCanceledException,"opening validation respects cancellation: "+failure);NoWrite(fixture,"cancelled opening");
                    }
                    object plan=Call(fixture,"Plan");
                    using(var cancel=new CancellationTokenSource()) {
                        var pending=Save(browser,plan,root,cancel.Token);cancel.Cancel();
                        Check(Failure(pending) is OperationCanceledException,"cancel before transaction refuses save");NoWrite(fixture,"cancelled save");
                    }
                    byte[] prior=File.ReadAllBytes(paths[1]);
                    var changed=Save(browser,plan,root,CancellationToken.None);File.AppendAllText(paths[1],"\n");
                    Check(Failure(changed) is InvalidDataException,"source changed while worker is validating cannot commit an old mean");NoWrite(fixture,"source changed while pending");File.WriteAllBytes(paths[1],prior);
                    string raw=Path.Combine(root,"raw1.bin");byte originalRawByte;
                    using(var stream=new FileStream(raw,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){originalRawByte=(byte)stream.ReadByte();stream.Position=0;stream.WriteByte(7);}
                    Check(Failure(Save(browser,plan,root,CancellationToken.None))!=null,"changed raw dependency refuses asynchronous save");NoWrite(fixture,"raw changed");
                    using(var stream=new FileStream(raw,FileMode.Open,FileAccess.Write,FileShare.None))stream.WriteByte(originalRawByte);
                    // Small project switch while the opening check is pending must
                    // not publish the old snapshot into the replacement project.
                    var pendingOpen=(Task)Call(browser,"PrepareJointEditorSnapshotAsync",CancellationToken.None);
                    string next=Path.Combine(qa,"next");Directory.CreateDirectory(next);File.Copy(Path.Combine(root,"ITEM.in"),Path.Combine(next,"ITEM.in"));File.Copy(Path.Combine(root,"CABLE.in"),Path.Combine(next,"CABLE.in"));
                    Check((bool)Call(browser,"LoadProject",next),"pending validation does not prevent explicit project replacement");
                    Check(Failure(pendingOpen) is OperationCanceledException,"old opening result is discarded after project switch");
                    Check(String.Equals((string)Get(browser,"ProjectPath"),next,StringComparison.OrdinalIgnoreCase),"old continuation preserves new project context");
                    Check((bool)Call(browser,"LoadProject",root),"return to isolated mean source");((CheckBox)Field(browser,"comparisonAverage")).Checked=true;Call(fixture,"Mean");
                    plan=Call(fixture,"Plan");Control actions=(Control)Field(browser,"actions");
                    var actionStates=ActionDescendants(actions).Select(c=>new ActionUiState {Control=c,Visible=c.Visible,Created=c.IsHandleCreated,Handle=c.IsHandleCreated?c.Handle:IntPtr.Zero,NativeVisible=c.IsHandleCreated&&IsWindowVisible(c.Handle)}).ToArray();
                    bool priorUserPaint=UserPaint(actions);Check(priorUserPaint,"action panel starts with its user-painted style");
                    PaintEventHandler observePaint=delegate{if(finalSaveWatch!=null&&double.IsNaN(firstPaint))firstPaint=finalSaveWatch.Elapsed.TotalMilliseconds;};actions.Paint+=observePaint;
                    initial=ticks;finalSaveWatch=watch;watch.Restart();
                    try {
                        var saved=Save(browser,plan,root,CancellationToken.None);dispatch=watch.ElapsedMilliseconds;
                        Check(!actions.Enabled&&!((Control)Field(browser,"tree")).Enabled,"save immediately disables action and navigation inputs");
                        Check(UserPaint(actions)==priorUserPaint,"action panel restores UserPaint before dispatch returns");
                        foreach(var state in actionStates)if(state.Created)Check(!IsWindowEnabled(state.Handle),"native action input is disabled while save is pending: "+state.Control.GetType().Name+"/"+state.Control.Name);
                        Complete(saved);maxHeartbeatGap=Math.Max(maxHeartbeatGap,watch.Elapsed.TotalMilliseconds-lastTick);finalSaveWatch=null;
                        Console.WriteLine("SAVE_MS="+watch.ElapsedMilliseconds+"; DISPATCH_MS="+dispatch+"; UI_TICKS="+(ticks-initial));
                        // Full-loop cold-frame latency is evidence, not a relaxed
                        // dispatch gate or an invented physical-GUI acceptance limit.
                        Console.WriteLine("SAVE_FIRST_TICK_MS="+firstTick.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; SAVE_MAX_HEARTBEAT_GAP_MS="+maxHeartbeatGap.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; SAVE_FIRST_ACTION_PAINT_MS="+firstPaint.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
                        Check(dispatch<350&&ticks-initial>=3,"full confirmation source/hash validation and transaction do not block UI heartbeat");
                        Check(UserPaint(actions)==priorUserPaint,"action panel retains UserPaint after completion");
                        foreach(var state in actionStates){Control c=state.Control;Check(c.Visible==state.Visible&&c.IsHandleCreated==state.Created&&(!state.Created||(c.Handle==state.Handle&&IsWindowVisible(c.Handle)==state.NativeVisible)),"enabled transition preserves hidden visibility and existing handle: "+c.GetType().Name+"/"+c.Name);}
                    }finally{finalSaveWatch=null;actions.Paint-=observePaint;}
                    Check((int)Get(browser,"LastChangedFileCount")==1&&!(bool)Get(browser,"IsBusy"),"successful await means one completed commit, not queued work");
                    string backup=Directory.GetFiles(root,"CABLE.before-joint-*.bak").Single();Check(File.ReadAllBytes(backup).SequenceEqual((byte[])Field(fixture,"CableBytes")),"saved edit retains exact pre-edit backup");
                    Check(((double[])Get(Get(browser,"project"),"JointPositionsMetres")).Contains(680d),"successful async commit refreshes current joint markers");
                    Check(File.ReadAllBytes(paths[1]).SequenceEqual(prior),"save does not rewrite source calibration");
                }
            }
            Console.WriteLine("BROWSER_JOINT_ASYNC_RESPONSIVE_PASS="+checks+"; QA="+qa);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
