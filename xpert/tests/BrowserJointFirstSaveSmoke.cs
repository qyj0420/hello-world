using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Fresh-process first-save measurement. Synthetic files have just been created,
// so this is NOT a claim of cold disk/cache or physical-screen latency.
internal static class BrowserJointFirstSaveSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,F).Invoke(o,a);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Control.CheckForIllegalCrossThreadCalls=true;
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            string qa=Path.Combine(args[1],"first-save-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var type=typeof(BrowserMeanSourceIntegritySmoke).GetNestedType("Fixture",F);
            using(var fixture=(IDisposable)Activator.CreateInstance(type,F,null,new object[]{assembly,qa,true},null)) {
                typeof(BrowserJointAsyncResponsiveSmoke).GetMethod("Expand",F).Invoke(null,new object[]{assembly,fixture});
                Call(fixture,"Mean");
                var browser=(Control)Field(fixture,"Browser");object plan=Call(fixture,"Plan");
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var watch=new Stopwatch();double last=0,maxGap=0,first=-1;int ticks=0;
                using(var timer=new System.Windows.Forms.Timer{Interval=16}) {
                    timer.Tick+=(s,e)=>{double now=watch.Elapsed.TotalMilliseconds;if(first<0)first=now;maxGap=Math.Max(maxGap,now-last);last=now;ticks++;};
                    timer.Start();watch.Start();
                    var save=(Task)Call(browser,"CommitJointDialogAsync",plan,"AddJoint",qa,(int)Field(browser,"projectGeneration"),(int)Field(browser,"selectionGeneration"),CancellationToken.None);
                    double dispatch=watch.Elapsed.TotalMilliseconds;
                    while(!save.IsCompleted&&watch.ElapsedMilliseconds<30000){Application.DoEvents();Thread.Sleep(2);}
                    if(!save.IsCompleted)throw new TimeoutException("First save timeout");
                    save.GetAwaiter().GetResult();maxGap=Math.Max(maxGap,watch.Elapsed.TotalMilliseconds-last);
                    Console.WriteLine("FIRST_SAVE_MS={0:F3}; DISPATCH_MS={1:F3}; FIRST_TICK_MS={2:F3}; MAX_GAP_MS={3:F3}; TICKS={4}",watch.Elapsed.TotalMilliseconds,dispatch,first,maxGap,ticks);
                    // Explicit local regression budget, not a universal SLA.
                    if(dispatch>350||maxGap>350||ticks==0)throw new Exception("First-save UI heartbeat exceeds 350 ms local regression budget");
                    if(Directory.GetFiles(qa,"CABLE.before-joint-*.bak").Length!=1)throw new Exception("Expected exactly one committed backup");
                }
            }
            Console.WriteLine("FIRST_SAVE_PASS; HARDWARE=False; QA="+qa);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
