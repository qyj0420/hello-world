using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Only injected delegates and an unshown Control. No product entry point,
// bridge process, native device library, monitor or output is invoked.
internal static class PressureCaptureBoundarySmoke
{
    sealed class BoundaryControl:Control
    {
        internal Func<CancellationToken,bool> Accept;
        public bool TryBeginPreparedHardwareCapture(CancellationToken token){return Accept(token);}
    }
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Cancelled(Action action,string message)
    {bool cancelled=false;try{action();}catch(OperationCanceledException){cancelled=true;}Check(cancelled,message);}

    [STAThread]static int Main(string[] args)
    {
        try
        {
            using(var c=new BoundaryControl())using(var source=new CancellationTokenSource())
            {
                var order=new List<string>();var result=Task.FromResult("synthetic-result");
                c.Accept=token=>{Check(token==source.Token,"same owned token reaches commit");order.Add("commit");return true;};
                var actual=ModuleHost.DispatchPreparedPressureCapture(c,source.Token,()=>order.Add("validate"),
                    ()=>order.Add("reserve"),()=>order.Add("abandon"),()=>{order.Add("worker");return result;});
                Check(Object.ReferenceEquals(actual,result),"actual worker task retained, not a fabricated completion");
                Check(String.Join(",",order)=="validate,reserve,commit,worker","validation/reservation/commit precede one dispatch without an intervening await");
            }
            foreach(string cancellation in new[]{"before","validate","reserve","commit"})
            using(var c=new BoundaryControl())using(var source=new CancellationTokenSource())
            {
                int reserved=0,abandoned=0,committed=0,invoked=0;
                c.Accept=token=>{committed++;if(cancellation=="commit")source.Cancel();return true;};
                if(cancellation=="before")source.Cancel();
                Cancelled(()=>ModuleHost.DispatchPreparedPressureCapture(c,source.Token,
                    ()=>{if(cancellation=="validate")source.Cancel();},
                    ()=>{reserved++;if(cancellation=="reserve")source.Cancel();},()=>abandoned++,
                    ()=>{invoked++;return Task.FromResult("must-not-run");}),"cancellation is observed at "+cancellation);
                Check(invoked==0,"worker never invoked after "+cancellation+" cancellation");
                Check(reserved==(cancellation=="reserve"||cancellation=="commit"?1:0),"reservation boundary at "+cancellation);
                Check(abandoned==reserved,"only an unused reservation is relinquished at "+cancellation);
                Check(committed==(cancellation=="commit"?1:0),"no premature preparation commit at "+cancellation);
            }
            using(var c=new BoundaryControl())
            {
                int reserved=0,abandoned=0,invoked=0;c.Accept=token=>false;
                Cancelled(()=>ModuleHost.DispatchPreparedPressureCapture(c,CancellationToken.None,()=>{},
                    ()=>reserved++,()=>abandoned++,()=>{invoked++;return Task.FromResult("must-not-run");}),"finish/stale request denied by the UI boundary");
                Check(reserved==1&&abandoned==1&&invoked==0,"denied preparation never dispatches or leaves an active unused reservation");
            }
            using(var c=new BoundaryControl())
            {
                int reserved=0,abandoned=0,invoked=0;c.Accept=token=>true;
                bool failed=false;
                try{ModuleHost.DispatchPreparedPressureCapture(c,CancellationToken.None,()=>{throw new InvalidDataException("synthetic calibration changed");},
                    ()=>reserved++,()=>abandoned++,()=>{invoked++;return Task.FromResult("must-not-run");});}
                catch(InvalidDataException){failed=true;}
                Check(failed&&reserved==0&&abandoned==0&&invoked==0,"final calibration failure occurs before any reservation/dispatch");
            }
            using(var c=new BoundaryControl())
            {
                int abandoned=0,invoked=0;c.Accept=token=>true;bool failed=false;
                try{ModuleHost.DispatchPreparedPressureCapture(c,CancellationToken.None,()=>{},()=>{},()=>abandoned++,
                    ()=>{invoked++;throw new IOException("synthetic synchronous worker error");});}
                catch(IOException){failed=true;}
                Check(failed&&invoked==1&&abandoned==0,"a synchronous worker failure cannot claim native cleanup");
            }
            using(var c=new BoundaryControl())
            {
                int abandoned=0,invoked=0;c.Accept=token=>true;
                var failed=new TaskCompletionSource<string>();failed.SetException(new IOException("synthetic asynchronous worker error"));
                var task=ModuleHost.DispatchPreparedPressureCapture(c,CancellationToken.None,()=>{},()=>{},()=>abandoned++,()=>{invoked++;return failed.Task;});
                Check(task.IsFaulted&&invoked==1&&abandoned==0,"an asynchronous worker failure retains owned fail-closed cleanup");
                Check(task.Exception.InnerExceptions.Count==1,"asynchronous error remains observable");
            }
            string host=File.ReadAllText(Path.Combine(args[0],"src","ModuleHost.FactoryPressure.cs"));
            int capture=host.IndexOf("Func<string,double,CancellationToken,Task<string>> capture=",StringComparison.Ordinal);
            int wait=host.IndexOf("await session.StopMonitor().ConfigureAwait(false);",capture,StringComparison.Ordinal);
            int dispatch=host.IndexOf("var task=DispatchPreparedPressureCapture",capture,StringComparison.Ordinal);
            int awaitCapture=host.IndexOf("string result=await task.ConfigureAwait(false);",capture,StringComparison.Ordinal);
            int release=host.IndexOf("cadence.Released(captureDuration);",capture,StringComparison.Ordinal);
            Check(capture>=0&&wait>capture&&dispatch>wait&&awaitCapture>dispatch&&release>awaitCapture,"real host commits after monitor exit and releases only after successful task completion");
            string preferences=File.ReadAllText(Path.Combine(args[0],"modules-src","PressureTest","src","PressureTestPreferences.cs"));
            int report=preferences.IndexOf("public void ReportCaptureCooldown",StringComparison.Ordinal);
            int close=preferences.IndexOf("void CloseCaptureCooldown",report,StringComparison.Ordinal);
            Check(!preferences.Substring(report,close-report).Contains("Interlocked.Exchange(ref automaticCapturePreparation,0)"),"UI zero remaining does not consume safety preparation");
            Console.WriteLine("PRESSURE_CAPTURE_BOUNDARY_PASS="+checks+"; HardwareAccessed=false; ProductStarted=false; WorkersStarted=0");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
