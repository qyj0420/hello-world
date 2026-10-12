using System;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    // UI callers keep their synchronization context. No native API is called
    // here; the production startup delegate is admitted only after drain.
    internal static class CalibrationNavigationHandoff
    {
        internal static async Task RunAsync(Func<bool> current,
            Func<CancellationToken,Task<IDisposable>> drain,Func<Task> start,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!current())return;
            using(IDisposable lease=await drain(token))
            {
                if(lease==null)throw new InvalidOperationException("Environment handoff did not return an ownership lease.");
                token.ThrowIfCancellationRequested();
                if(!current())return;
                await start();
                // Once startup owns hardware, cancellation is not permission to
                // dispose its lease before the real operation has completed.
            }
        }
    }
}
