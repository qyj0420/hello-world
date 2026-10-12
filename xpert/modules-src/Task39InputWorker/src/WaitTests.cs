using System;
using System.Collections.Generic;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    internal static class WaitTests
    {
        private sealed class TestClock : IInputCaptureClock
        {
            internal double Now;
            internal Action AfterPause;
            internal int Pauses;
            public double Milliseconds { get { return Now; } }
            public void Pause(int milliseconds, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                Now += milliseconds; Pauses++;
                if (AfterPause != null) AfterPause();
                cancellation.ThrowIfCancellationRequested();
            }
        }

        internal static string Run()
        {
            int checks = 0;
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); int queries = 0;
                var clock = new TestClock();
                var wait = New(clock, () => { queries++; return 0; }, () => false);
                RequireThrows<OperationCanceledException>(() => wait.WaitQuiet(200, cancelled.Token), "immediate quiet cancellation");
                RequireThrows<OperationCanceledException>(() => wait.WaitComplete(5000, null, cancelled.Token), "immediate capture cancellation");
                Require(queries == 0 && clock.Pauses == 0, "cancellation must precede status/native queries"); checks++;
            }
            using (var cancelled = new CancellationTokenSource())
            {
                var clock = new TestClock(); clock.AfterPause = () => { if (clock.Now >= 30) cancelled.Cancel(); };
                var wait = New(clock, () => 0, () => false);
                RequireThrows<OperationCanceledException>(() => wait.WaitComplete(60000, null, cancelled.Token), "cancel while waiting");
                Require(clock.Now == 30 && clock.Pauses == 3, "stop within one polling interval without long native wait"); checks++;
            }
            {
                var clock = new TestClock(); var wait = New(clock, () => 0, () => false);
                RequireThrows<TimeoutException>(() => wait.WaitComplete(100, null, CancellationToken.None), "no-trigger timeout");
                Require(clock.Now == 100, "timeout bound"); checks++;
            }
            {
                var clock = new TestClock(); var wait = New(clock, () => clock.Now >= 20 ? 1u : 0u, () => false);
                RequireThrows<InvalidOperationException>(() => wait.WaitQuiet(200, CancellationToken.None), "partial ART data during quiet gate");
                Require(clock.Now == 20, "quiet data must reject immediately"); checks++;
            }
            {
                var clock = new TestClock(); var wait = New(clock, () => 0, () => clock.Now >= 20);
                RequireThrows<InvalidOperationException>(() => wait.WaitComplete(5000, null, CancellationToken.None), "Pico completes too soon after WAIT");
                Require(clock.Now == 20, "early completion rejection"); checks++;
            }
            {
                var clock = new TestClock(); double completeAt = Double.PositiveInfinity;
                var wait = New(clock, () => clock.Now >= completeAt ? 4800u : 0u, () => clock.Now >= completeAt);
                wait.WaitQuiet(200, CancellationToken.None);
                int notifications = 0;
                wait.WaitComplete(5000, () => { notifications++; completeAt = clock.Now + 70; }, CancellationToken.None);
                Require(clock.Now == 270 && notifications == 1, "quiet then shared full capture completion"); checks++;
            }
            {
                var clock = new TestClock(); var wait = New(clock, () => clock.Now >= 60 ? 4800u : 0u, () => false);
                RequireThrows<TimeoutException>(() => wait.WaitComplete(100, null, CancellationToken.None), "ART alone must not complete pair"); checks++;
            }
            using (var cancelled = new CancellationTokenSource())
            {
                var clock = new TestClock(); int queries = 0;
                var wait = New(clock, () => { queries++; return 0; }, () => false);
                RequireThrows<OperationCanceledException>(() => wait.WaitComplete(5000, () => cancelled.Cancel(), cancelled.Token), "cancellation inside WAIT notification");
                Require(queries == 0, "notification cancellation precedes queries"); checks++;
            }
            {
                var clock = new TestClock(); var expected = new System.IO.IOException("injected native status error");
                var wait = new InputCaptureWait(() => 0, () => false, () => { throw expected; }, 4800, 60, clock);
                try { wait.WaitComplete(5000, null, CancellationToken.None); throw new Exception("driver status error was swallowed"); }
                catch (System.IO.IOException ex) { Require(Object.ReferenceEquals(ex, expected), "preserve original status failure"); }
                checks++;
            }
            {
                var clock = new TestClock(); double completeAt = Double.PositiveInfinity;
                var calls = new List<string>(); int triggers = 0;
                var wait = new InputCaptureWait(
                    () => { calls.Add("ART"); return clock.Now >= completeAt ? 4800u : 0u; },
                    () => { calls.Add("PICO"); return clock.Now >= completeAt; },
                    () => calls.Add("STATUS"), 4800, 60, clock);
                wait.WaitQuiet(200, CancellationToken.None);
                calls.Clear();
                wait.WaitComplete(5000, () => calls.Add("WAIT"), () =>
                {
                    calls.Add("TRG"); triggers++;
                    completeAt = clock.Now + 60;
                    clock.Now += 20; // Synchronous pulse high/hold/low helper.
                }, CancellationToken.None);
                Require(String.Join(",", calls.GetRange(0, 5).ToArray()) == "STATUS,PICO,ART,WAIT,TRG",
                    "check both cards before publishing WAIT and issuing exactly one edge");
                Require(triggers == 1 && clock.Now == 260,
                    "20 ms pulse duration counts in the 60 ms acquisition gate"); checks++;
            }
            using (var cancelled = new CancellationTokenSource())
            {
                var clock = new TestClock(); int queries = 0, triggers = 0;
                var wait = New(clock, () => { queries++; return 0; }, () => false);
                cancelled.Cancel();
                RequireThrows<OperationCanceledException>(() => wait.WaitComplete(5000, null,
                    () => triggers++, cancelled.Token), "active trigger pre-cancel");
                Require(queries == 0 && triggers == 0, "pre-cancel suppresses both queries and trigger"); checks++;
            }
            using (var cancelled = new CancellationTokenSource())
            {
                var clock = new TestClock(); int queries = 0, triggers = 0;
                var wait = New(clock, () => { queries++; return 0; }, () => false);
                RequireThrows<OperationCanceledException>(() => wait.WaitComplete(5000, () => cancelled.Cancel(),
                    () => triggers++, cancelled.Token), "active cancellation in WAIT notification");
                Require(queries == 1 && triggers == 0, "cancel after preflight but before output prevents TRG"); checks++;
            }
            {
                foreach (bool picoReady in new[] { false, true })
                {
                    var clock = new TestClock(); int triggers = 0, notifications = 0;
                    var wait = New(clock, () => picoReady ? 0u : 1u, () => picoReady);
                    RequireThrows<InvalidOperationException>(() => wait.WaitComplete(5000, () => notifications++,
                        () => triggers++, CancellationToken.None), "active trigger rejects preexisting data");
                    Require(triggers == 0 && notifications == 0 && clock.Pauses == 0,
                        "preexisting partial ART data or ready Pico prevents output");
                }
                checks++;
            }
            {
                var clock = new TestClock(); int triggers = 0;
                var expected = new System.IO.IOException("injected TRG output failure");
                var wait = New(clock, () => 0, () => false);
                try
                {
                    wait.WaitComplete(5000, null, () => { triggers++; throw expected; }, CancellationToken.None);
                    throw new Exception("TRG failure was swallowed");
                }
                catch (System.IO.IOException ex) { Require(Object.ReferenceEquals(ex, expected), "preserve original TRG failure"); }
                Require(triggers == 1 && clock.Pauses == 0, "never retry failed TRG implicitly"); checks++;
            }
            {
                foreach (bool artOnly in new[] { false, true })
                {
                    var clock = new TestClock(); int triggers = 0;
                    var wait = New(clock, () => clock.Now >= 60 && artOnly ? 4800u : 0u,
                        () => clock.Now >= 60 && !artOnly);
                    string diagnostic = null;
                    try { wait.WaitComplete(100, null, () => triggers++, CancellationToken.None); }
                    catch (TimeoutException ex) { diagnostic = ex.Message; }
                    Require(triggers == 1 && diagnostic != null, "active one-card completion still times out");
                    Require(diagnostic.Contains(artOnly ? "ART 4800/4800 点；Pico 等待完成" : "ART 0/4800 点；Pico 完成"),
                        "timeout identifies each card progress");
                    Require(diagnostic.Contains(artOnly ? "Pico EXT" : "ART PFI0"),
                        "timeout suggests checking the incomplete side, not claiming synchronization");
                }
                checks++;
            }
            {
                var clock = new TestClock(); var reports = new List<uint>();
                var wait = New(clock, () => (uint)clock.Now, () => false);
                RequireThrows<TimeoutException>(() => wait.WaitComplete(500, null, null, CancellationToken.None,
                    (samples, ready) => reports.Add(samples)), "partial data progress timeout");
                Require(reports.Count == 3 && reports[0] == 0 && reports[1] == 250 && reports[2] == 500,
                    "partial progress is throttled to 250 ms"); checks++;
            }
            {
                var clock = new TestClock(); var reports = new List<string>();
                var wait = New(clock, () => clock.Now >= 70 ? 4800u : 0u, () => clock.Now >= 60);
                wait.WaitComplete(100, null, () => { }, CancellationToken.None,
                    (samples, ready) => reports.Add(clock.Now + ":" + samples + ":" + ready));
                Require(reports.Count == 3 && reports[1] == "60:0:True" && reports[2] == "70:4800:True",
                    "completion transitions bypass partial-progress throttle"); checks++;
            }
            return "WAIT_TESTS=PASS: " + checks + " deterministic checks; cancellation, timeout, quiet/early-trigger gates, one shared TRG, pulse-inclusive timing, per-card diagnostics and throttled status; no hardware or real waits.";
        }

        private static InputCaptureWait New(TestClock clock, Func<uint> samples, Func<bool> ready)
        { return new InputCaptureWait(samples, ready, delegate { }, 4800, 60, clock); }
        private static void Require(bool value, string name) { if (!value) throw new Exception("Wait test failed: " + name); }
        private static void RequireThrows<T>(Action action, string name) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Wait test expected " + typeof(T).Name + ": " + name);
        }
    }
}
