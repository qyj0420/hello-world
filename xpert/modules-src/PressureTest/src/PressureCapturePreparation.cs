using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.Controls
{
    // The native capture and original-data validation have already completed.
    // Only independent, read-only display preparation and range review overlap.
    // No UI publication, save acknowledgement or next-round permission is made
    // here. Both tasks are observed to completion even if either fails.
    internal static class PressureCapturePreparation
    {
        internal sealed class Result<TDisplay, TReview>
        {
            internal TDisplay Display;
            internal TReview Review;
            internal long DisplayMilliseconds, ReviewMilliseconds, TotalMilliseconds;
        }

        internal static async Task<Result<TDisplay, TReview>> RunAsync<TDisplay, TReview>(
            Func<TDisplay> display, Func<TReview> review, CancellationToken cancel)
        {
            if (display == null) throw new ArgumentNullException("display");
            cancel.ThrowIfCancellationRequested();
            var result = new Result<TDisplay, TReview>();
            var total = Stopwatch.StartNew();
            var displayTask = Task.Run(delegate {
                cancel.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                try { return display(); }
                finally { result.DisplayMilliseconds = watch.ElapsedMilliseconds; }
            }, cancel);
            Task<TReview> reviewTask = null;
            if (review != null)
                reviewTask = Task.Run(delegate {
                    cancel.ThrowIfCancellationRequested();
                    var watch = Stopwatch.StartNew();
                    try { return review(); }
                    finally { result.ReviewMilliseconds = watch.ElapsedMilliseconds; }
                }, cancel);
            try
            {
                if (reviewTask == null) result.Display = await displayTask.ConfigureAwait(false);
                else
                {
                    await Task.WhenAll(displayTask, reviewTask).ConfigureAwait(false);
                    result.Display = displayTask.Result; result.Review = reviewTask.Result;
                }
                cancel.ThrowIfCancellationRequested();
                return result;
            }
            finally { result.TotalMilliseconds = total.ElapsedMilliseconds; }
        }
    }
}
