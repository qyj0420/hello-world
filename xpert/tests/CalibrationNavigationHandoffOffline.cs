using System;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.OWTS.Desktop;

internal static class CalibrationNavigationHandoffOffline
{
    private static int checks;
    private sealed class Lease : IDisposable
    {
        internal int Disposals;
        public void Dispose() {Disposals++;}
    }
    private static void Check(bool value,string name)
    {if(!value)throw new Exception(name);checks++;}
    private static async Task Expect<T>(Task task,string name) where T:Exception
    {try{await task;}catch(T){checks++;return;}throw new Exception(name);}
    private static async Task Run()
    {
        int starts=0,drains=0;bool current=true;
        var released=new TaskCompletionSource<IDisposable>();
        var entered=new TaskCompletionSource<bool>();var lease=new Lease();
        Task pending=CalibrationNavigationHandoff.RunAsync(()=>current,token=>{drains++;return released.Task;},
            ()=>{starts++;return entered.Task;},CancellationToken.None);
        Check(drains==1 && starts==0 && !pending.IsCompleted,"pending environment release blocks startup");
        released.SetResult(lease);
        Check(starts==1 && lease.Disposals==0 && !pending.IsCompleted,"mount/start is not actual initialization completion");
        entered.SetResult(true);await pending;
        Check(lease.Disposals==1,"lease released once after actual entry completion");

        current=false;drains=0;starts=0;
        await CalibrationNavigationHandoff.RunAsync(()=>current,t=>{drains++;return Task.FromResult<IDisposable>(new Lease());},
            ()=>{starts++;return Task.CompletedTask;},CancellationToken.None);
        Check(drains==0 && starts==0,"stale request at entry has no side effects");
        current=true;released=new TaskCompletionSource<IDisposable>();lease=new Lease();
        pending=CalibrationNavigationHandoff.RunAsync(()=>current,t=>released.Task,()=>{starts++;return Task.CompletedTask;},CancellationToken.None);
        current=false;released.SetResult(lease);await pending;
        Check(starts==0 && lease.Disposals==1,"stale navigation after release never starts and disposes lease");

        current=true;released=new TaskCompletionSource<IDisposable>();lease=new Lease();
        using(var cancellation=new CancellationTokenSource()) {
            pending=CalibrationNavigationHandoff.RunAsync(()=>true,t=>released.Task,()=>{starts++;return Task.CompletedTask;},cancellation.Token);
            cancellation.Cancel();released.SetResult(lease);await Expect<OperationCanceledException>(pending,"cancellation must be observed");
            Check(starts==0 && lease.Disposals==1,"cancelled preparation never starts and disposes lease");
        }
        using(var cancellation=new CancellationTokenSource()) {
            cancellation.Cancel();drains=0;
            await Expect<OperationCanceledException>(CalibrationNavigationHandoff.RunAsync(()=>true,t=>{drains++;return Task.FromResult<IDisposable>(new Lease());},
                ()=>Task.CompletedTask,cancellation.Token),"pre-cancelled preparation");
            Check(drains==0,"pre-cancelled preparation does not drain or start");
        }

        starts=0;
        await Expect<InvalidOperationException>(CalibrationNavigationHandoff.RunAsync(()=>true,t=>{throw new InvalidOperationException("Stop/Clear failure");},
            ()=>{starts++;return Task.CompletedTask;},CancellationToken.None),"synchronous drain failure propagated");
        Check(starts==0,"Stop/Clear failure is not treated as released");
        released=new TaskCompletionSource<IDisposable>();
        pending=CalibrationNavigationHandoff.RunAsync(()=>true,t=>released.Task,()=>{starts++;return Task.CompletedTask;},CancellationToken.None);
        released.SetException(new InvalidOperationException("retained task"));await Expect<InvalidOperationException>(pending,"asynchronous drain failure propagated");
        Check(starts==0,"asynchronous release failure blocks native start");
        await Expect<InvalidOperationException>(CalibrationNavigationHandoff.RunAsync(()=>true,t=>Task.FromResult<IDisposable>(null),
            ()=>{starts++;return Task.CompletedTask;},CancellationToken.None),"null lease fails closed");
        Check(starts==0,"missing ownership lease blocks native start");

        lease=new Lease();
        await Expect<ArgumentException>(CalibrationNavigationHandoff.RunAsync(()=>true,t=>Task.FromResult<IDisposable>(lease),
            ()=>{throw new ArgumentException("startup failure");},CancellationToken.None),"startup error propagated");
        Check(lease.Disposals==1,"synchronous startup failure disposes lease");
        entered=new TaskCompletionSource<bool>();lease=new Lease();
        pending=CalibrationNavigationHandoff.RunAsync(()=>true,t=>Task.FromResult<IDisposable>(lease),()=>entered.Task,CancellationToken.None);
        entered.SetException(new ArgumentException("entry cleanup failure"));await Expect<ArgumentException>(pending,"async startup error propagated");
        Check(lease.Disposals==1,"asynchronous entry completion/failure disposes lease");

        entered=new TaskCompletionSource<bool>();lease=new Lease();
        using(var cancellation=new CancellationTokenSource()) {
            pending=CalibrationNavigationHandoff.RunAsync(()=>true,t=>Task.FromResult<IDisposable>(lease),()=>entered.Task,cancellation.Token);
            cancellation.Cancel();
            Check(!pending.IsCompleted && lease.Disposals==0,"cancellation after native ownership does not prematurely release lease");
            entered.SetResult(true);await pending;Check(lease.Disposals==1,"cancelled in-flight entry retains lease through completion");
        }
        var firstEntry=new TaskCompletionSource<bool>();var secondEntry=new TaskCompletionSource<bool>();
        var firstLease=new Lease();var secondLease=new Lease();
        Task first=CalibrationNavigationHandoff.RunAsync(()=>true,t=>Task.FromResult<IDisposable>(firstLease),()=>firstEntry.Task,CancellationToken.None);
        Task second=CalibrationNavigationHandoff.RunAsync(()=>true,t=>Task.FromResult<IDisposable>(secondLease),()=>secondEntry.Task,CancellationToken.None);
        firstEntry.SetResult(true);await first;
        Check(firstLease.Disposals==1 && secondLease.Disposals==0 && !second.IsCompleted,"old completion cannot release a newer handoff lease");
        secondEntry.SetResult(true);await second;Check(secondLease.Disposals==1,"new lease has independent completion");
    }
    private static int Main()
    {
        try {Run().GetAwaiter().GetResult();Console.WriteLine("CALIBRATION_HANDOFF_OFFLINE_PASS checks="+checks);return 0;}
        catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
}
