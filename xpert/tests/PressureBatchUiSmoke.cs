using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class PressureBatchUiSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,All).SetValue(value,data);}
    static T Get<T>(object value,string name){return (T)value.GetType().GetField(name,All).GetValue(value);}
    static void Record(object value,bool continuation,int row,double target)
    {
        Set(value,"dispatchingAutomaticPlanContinuation",continuation);Set(value,"selectedPlanStepIndex",row);
        Set(value,"requestedTargetPeakVoltageKv",target);value.GetType().GetMethod("RecordPressureBatchRequestNoLock",All).Invoke(value,null);
    }
    [STAThread] static int Main(string[] args)
    {
        WindowsFormsSynchronizationContext.AutoInstall=false;
        SynchronizationContext.SetSynchronizationContext(null);
        Control.CheckForIllegalCrossThreadCalls=true;
        var host=Assembly.LoadFrom(System.IO.Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));var module=Assembly.LoadFrom(System.IO.Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        var pageType=module.GetType("HVX.Controls.PressureTestControl",true);
        using(var page=(Control)Activator.CreateInstance(pageType))
        {
            Record(page,false,0,5);Check(Get<int>(page,"pressureRequestBatchRound")==1,"first group round");
            Record(page,true,0,5);Check(Get<int>(page,"pressureRequestBatchRound")==2,"same row second");
            Record(page,true,0,5);Check(Get<int>(page,"pressureRequestBatchRound")==3,"same row third");
            Record(page,false,0,5);Check(Get<int>(page,"pressureRequestBatchRound")==1,"explicit restart resets");
            Record(page,true,1,5);Check(Get<int>(page,"pressureRequestBatchRound")==1,"same voltage new row resets");
            Record(page,true,1,6);Check(Get<int>(page,"pressureRequestBatchRound")==1,"changed voltage resets");
            Record(page,true,1,6);Check(Get<int>(page,"pressureRequestBatchRound")==2,"new voltage next normal round");
            Check((bool)pageType.GetProperty("PressureRequestIsAutomaticContinuation").GetValue(page,null),"scheduler provenance");
            Check(!(bool)pageType.GetProperty("PressureBatchCaptureEnabled").GetValue(page,null),"capability defaults off");
        }
        var read=host.GetType("Xiongxin.OWTS.Desktop.ModuleHost",true).GetMethod("ReadPressureUi",All).MakeGenericMethod(typeof(int));
        using(var ui=new Control())
        {
            ui.Text="immutable-test";var handle=ui.Handle;int ownerThread=Thread.CurrentThread.ManagedThreadId;
            var task=Task.Run(()=>{
                Func<int> body=()=>{Check(ui.Text=="immutable-test","cross-thread text access marshalled");return Thread.CurrentThread.ManagedThreadId;};
                return (int)read.Invoke(null,new object[]{ui,body});
            });
            var watch=Stopwatch.StartNew();
            while(!task.IsCompleted&&watch.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(1);}
            Check(task.IsCompleted,"UI marshal bounded");Check(task.GetAwaiter().GetResult()==ownerThread,"UI thread identity");
        }
        using(var disposed=new Control())
        {
            disposed.Dispose();bool rejected=false;
            try{read.Invoke(null,new object[]{disposed,(Func<int>)(()=>1)});}catch(TargetInvocationException error){rejected=error.InnerException is OperationCanceledException;}
            Check(rejected,"closed owner rejects stale callback");
        }
        Console.WriteLine("PASS "+checks+"; CheckForIllegalCrossThreadCalls=True; HardwareAccessed=False; WorkersStarted=0");
        return 0;
    }
}
