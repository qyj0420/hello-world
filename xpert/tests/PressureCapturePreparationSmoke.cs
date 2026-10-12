using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

internal static class PressureCapturePreparationSmoke
{
    static int checks;
    static MethodInfo run;
    static void Check(bool value, string message) { Interlocked.Increment(ref checks); if (!value) throw new Exception(message); }
    static Task Start(Func<int> display, Func<string> review, CancellationToken cancel)
    { return (Task)run.Invoke(null, new object[] { display, review, cancel }); }
    static object Result(Task task) { return task.GetType().GetProperty("Result").GetValue(task, null); }
    static object Field(object value, string name)
    { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    static void Finish(Task task) { task.GetAwaiter().GetResult(); }
    static void MustFail(Task task, string message)
    {
        bool rejected = false; try { Finish(task); } catch (Exception) { rejected = true; }
        Check(rejected, message);
    }
    static int Main(string[] args)
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            if (args.Length > 0)
                assembly = Assembly.LoadFrom(Path.Combine(args[0], "modules", "PressureTest", "Xiongxin.OWTS.PressureTest.dll"));
            Type helper = assembly.GetType("HVX.Controls.PressureCapturePreparation", true);
            run = helper.GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(int), typeof(string));
            int caller = Thread.CurrentThread.ManagedThreadId, displayThread = caller, reviewThread = caller;
            using (var displayEntered = new ManualResetEventSlim())
            using (var reviewEntered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Task task = Start(delegate { displayThread = Thread.CurrentThread.ManagedThreadId; displayEntered.Set(); Check(release.Wait(5000), "display release timeout"); return 17; },
                    delegate { reviewThread = Thread.CurrentThread.ManagedThreadId; reviewEntered.Set(); Check(release.Wait(5000), "review release timeout"); return "quality-original"; }, CancellationToken.None);
                Check(displayEntered.Wait(3000) && reviewEntered.Wait(3000), "display and review must start before either finishes");
                Check(!task.IsCompleted, "no early success while tasks are pending");
                release.Set(); Finish(task); object value = Result(task);
                Check((int)Field(value, "Display") == 17 && (string)Field(value, "Review") == "quality-original", "exact independent results retained");
                Check(displayThread != caller && reviewThread != caller, "preparation cannot execute on caller UI thread");
                Check((long)Field(value, "TotalMilliseconds") >= (long)Field(value, "DisplayMilliseconds"), "display telemetry bounded by total");
                Check((long)Field(value, "TotalMilliseconds") >= (long)Field(value, "ReviewMilliseconds"), "review telemetry bounded by total");
            }
            Task zero = Start(() => 23, null, CancellationToken.None); Finish(zero);
            Check((int)Field(Result(zero), "Display") == 23 && Field(Result(zero), "Review") == null, "zero-input display retains null quality callback");
            Check((long)Field(Result(zero), "ReviewMilliseconds") == 0, "zero-input does not fabricate range elapsed time");
            int starts = 0;
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); Task task = Start(() => { starts++; return 1; }, () => { starts++; return "bad"; }, canceled.Token);
                MustFail(task, "pre-cancellation must reject"); Check(starts == 0, "no callback after pre-cancellation");
            }
            using (var reviewEntered = new ManualResetEventSlim())
            using (var displayFailed = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                bool reviewFinished = false;
                Task task = Start(delegate { Check(reviewEntered.Wait(3000), "review entered before display failure"); displayFailed.Set(); throw new InvalidDataException("synthetic display failure"); },
                    delegate { reviewEntered.Set(); Check(release.Wait(5000), "failed sibling release timeout"); reviewFinished = true; return "held"; }, CancellationToken.None);
                Check(displayFailed.Wait(3000), "display failure observed");
                Check(!task.IsCompleted && !reviewFinished, "failed display must still observe pending review");
                release.Set(); MustFail(task, "display failure must never produce a successful result");
                Check(reviewFinished, "review is drained after display failure");
            }
            using (var displayEntered = new ManualResetEventSlim())
            using (var reviewFailed = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                bool displayFinished = false;
                Task task = Start(delegate { displayEntered.Set(); Check(release.Wait(5000), "display drain timeout"); displayFinished = true; return 8; },
                    delegate { Check(displayEntered.Wait(3000), "display entered before review failure"); reviewFailed.Set(); throw new InvalidDataException("synthetic quality failure"); }, CancellationToken.None);
                Check(reviewFailed.Wait(3000), "review failure observed"); Check(!task.IsCompleted, "failed review cannot leave an unobserved display task");
                release.Set(); MustFail(task, "review failure must not bypass quality"); Check(displayFinished, "display task drained after review failure");
            }
            using (var canceled = new CancellationTokenSource())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Task task = Start(() => 7, delegate { entered.Set(); Check(release.Wait(5000), "cancel drain timeout"); return "review"; }, canceled.Token);
                Check(entered.Wait(3000), "review running before cancel"); canceled.Cancel(); Check(!task.IsCompleted, "cancel cannot publish or orphan pending review");
                release.Set(); MustFail(task, "cancel after preparation started must reject its result");
            }
            Console.WriteLine("CAPTURE_PREPARATION_PASS=" + checks + "; HARDWARE=False; SDK=False; PRODUCT_ENTRY=False; CASE=read-only-overlap-drain-cancel");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
