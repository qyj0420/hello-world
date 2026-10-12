using System;
using System.Threading.Tasks;

namespace HVX.Controls
{
    // Only independent derived-file work overlaps. The caller establishes a
    // pending-publication marker first and commits the pair only after this join.
    // Even a failing branch must drain its peer before cleanup or any save ACK.
    internal static class PressureRoundPairPreparation
    {
        internal static async Task<bool> RunAsync(Func<Task<bool>> location,
            Func<Task> detected, Action<bool> locationCompleted)
        {
            if(location==null||detected==null||locationCompleted==null)
                throw new ArgumentNullException();
            Task<bool> loc;
            Task dem;
            try { loc=location(); }
            catch(Exception error) { var failed=new TaskCompletionSource<bool>();failed.SetException(error);loc=failed.Task; }
            try { dem=detected(); }
            catch(Exception error) { var failed=new TaskCompletionSource<bool>();failed.SetException(error);dem=failed.Task; }
            try { await Task.WhenAll(loc,dem).ConfigureAwait(false); }
            finally {
                if(loc.Status==TaskStatus.RanToCompletion)locationCompleted(loc.Result);
            }
            return loc.Result;
        }
    }
}
