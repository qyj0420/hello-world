using System;
using System.IO;
using System.Threading;
using Xiongxin.OWTS.FactoryPressBridge;

static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--contract")
            return ContractCheck.Run(args[1]) ? 0 : 1;
        string root = Path.Combine(Path.GetTempPath(), "xpert-fp-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Action<string> log = s => Console.WriteLine("  | " + s);
        double kv = 2;
        string kvText = Environment.GetEnvironmentVariable("SMOKE_KV");
        if (!string.IsNullOrEmpty(kvText)) kv = double.Parse(kvText, System.Globalization.CultureInfo.InvariantCulture);

        Check(XpertFactoryPressBackend.Preflight().Length == 0, "preflight clean");

        bool refusedPlaceholder = false;
        try { XpertFactoryPressBackend.Open("", "", 1000, 12000, 30, 6, true, log).Dispose(); }
        catch (InvalidOperationException ex) { refusedPlaceholder = true; Console.WriteLine("  | " + ex.Message); }
        Check(refusedPlaceholder, "placeholder gain 1000 refused before open");

        using (var backend = XpertFactoryPressBackend.Open("XPERT-ART-SERIAL", "", 6000, 12000, 30, 6, true, log))
        {
            Check(backend.IsFakeLive, "FakeLive session opened");
            string msg;

            backend.SimulateFakeLiveEStop(true);
            string r = backend.PressRound(kv, 6, Path.Combine(root, "r0-uncal"), CancellationToken.None, log, out msg);
            Check(r == XpertFactoryPressBackend.OutcomeRefused, "uncalibrated target kV refused: " + msg);

            backend.MarkProjectCalibrated(true);
            r = backend.PressRound(kv, 6, Path.Combine(root, "r1-estop"), CancellationToken.None, log, out msg);
            Check(r == XpertFactoryPressBackend.OutcomeRefused, "e-stop pressed target kV refused: " + msg);

            backend.SimulateFakeLiveEStop(false);
            r = backend.PressRound(kv, 6, Path.Combine(root, "r2-target"), CancellationToken.None, log, out msg);
            Check(r == XpertFactoryPressBackend.OutcomeCompleted, "target kV round completed: " + msg);
            Check(File.Exists(Path.Combine(root, "r2-target", "owts-factory-round.json")), "round json written");
            Check(File.Exists(Path.Combine(root, "r2-target", "pico-raw-adc.i16le")), "pico raw adc written");

            r = backend.PressRound(0, 6, Path.Combine(root, "r3-0kv"), CancellationToken.None, log, out msg);
            Check(r == XpertFactoryPressBackend.OutcomeCompleted, "0 kV noise round completed: " + msg);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                r = backend.PressRound(kv, 6, Path.Combine(root, "r4-stop"), cts.Token, log, out msg);
                Check(r == XpertFactoryPressBackend.OutcomeOperatorStopped, "operator stop: " + msg);
            }

            r = backend.PressRound(kv, 6, Path.Combine(root, "r5-after-stop"), CancellationToken.None, log, out msg);
            Check(r == XpertFactoryPressBackend.OutcomeCompleted, "operator stop does not latch: " + msg);

            r = backend.PressRound(31, 6, Path.Combine(root, "r5b-over-max"), CancellationToken.None, log, out msg);
            Check(r == XpertFactoryPressBackend.OutcomeRefused, "target above device maximum refused: " + msg);

            var press = System.Threading.Tasks.Task.Run(() =>
            {
                string m;
                string o = backend.PressRound(20, 6, Path.Combine(root, "r6-estop-mid-ramp"), CancellationToken.None, log, out m);
                return o + "|" + m;
            });
            Thread.Sleep(250);
            backend.SimulateFakeLiveEStop(true);
            string estop = press.Result;
            Check(estop.StartsWith(XpertFactoryPressBackend.OutcomeSafetyAbort + "|", StringComparison.Ordinal), "e-stop mid-ramp → safety abort: " + estop);
            Check(backend.RequiresReopen, "safety abort requires reopen");
            backend.SimulateFakeLiveEStop(false);
            r = backend.PressRound(kv, 6, Path.Combine(root, "r7-after-estop"), CancellationToken.None, log, out msg);
            Check(r == XpertFactoryPressBackend.OutcomeRefused, "no automatic re-press after e-stop: " + msg);

            Console.WriteLine("  | " + backend.StatusFiveStates);
            string close = backend.Close();
            Check(close.Length == 0, "close released lease" + (close.Length == 0 ? "" : ": " + close));
        }

        Console.WriteLine(failures == 0 ? "SMOKE OK" : "SMOKE FAILED " + failures);
        Console.WriteLine("records: " + root);
        return failures == 0 ? 0 : 1;
    }
}
