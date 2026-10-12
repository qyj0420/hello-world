using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    internal static class TriggerOutputTests
    {
        private sealed class FakeBackend : IArtSharedTriggerBackend
        {
            internal readonly List<string> Events = new List<string>();
            internal string FailAt;
            internal int FailuresLeft = 1;
            internal bool ZeroHandle;
            private void Record(string operation)
            {
                Events.Add(operation);
                if (operation == FailAt && FailuresLeft-- > 0) throw new IOException("injected " + operation);
            }
            public void LoadAndVerify(BenchSettings settings) { Record("VERIFY:" + settings.ArtSerial); }
            public void CreateTask(out IntPtr task) { task = ZeroHandle ? IntPtr.Zero : new IntPtr(7); Record("CREATE"); }
            public void ConfigureLine(IntPtr task, string physicalLine) { Record("CONFIG:" + physicalLine); }
            public void Write(IntPtr task, bool high) { Record(high ? "HIGH" : "LOW"); }
            public void Stop(IntPtr task) { Record("STOP"); }
            public void Clear(IntPtr task) { Record("CLEAR"); }
        }
        private sealed class FakeClock : IArtSharedTriggerClock
        {
            internal int Waited;
            internal Action AfterPause;
            public void Pause(int milliseconds, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); Waited += milliseconds;
                if (AfterPause != null) AfterPause();
                cancellation.ThrowIfCancellationRequested();
            }
        }

        internal static string Run()
        {
            int checks = 0;
            {
                var b = new FakeBackend(); var clock = new FakeClock();
                using (var trigger = new ArtSharedTrigger(b, clock))
                {
                    RequireThrows<InvalidOperationException>(() => trigger.Pulse(CancellationToken.None), "not prepared");
                    Require(b.Events.Count == 0, "not ready has no side effect");
                    trigger.Prepare(Settings()); Require(trigger.Ready, "prepared ready");
                    Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW");
                    RequireThrows<InvalidOperationException>(() => trigger.Prepare(Settings()), "no repeat prepare");
                    trigger.Pulse(CancellationToken.None);
                    Require(!trigger.Ready && clock.Waited == 20, "one pulse width and ready consumed");
                    Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,HIGH,LOW,STOP,CLEAR");
                    RequireThrows<InvalidOperationException>(() => trigger.Pulse(CancellationToken.None), "no repeat pulse");
                }
                Require(b.Events.Count == 8, "dispose after successful pulse is no-op"); checks++;
            }
            Action<BenchSettings>[] invalid = {
                s => s.AutomaticSharedTrigger = false, s => s.RealHardware = false,
                s => s.TargetVoltageV = 1, s => s.TargetVoltageV = Double.NaN,
                s => s.ArtTrigger = "/Dev1/PFI1", s => s.FallingTrigger = true,
                s => s.ArtSerial = "", s => s.ArtSerial = "SIM-ART", s => s.ArtSerial = "0",
                s => { s.ArtDevice = "Dev1,Dev2"; s.ArtTrigger = "/Dev1,Dev2/PFI0"; },
                s => s.TriggerPulseMs = 0, s => s.TriggerPulseMs = 1001
            };
            foreach (var change in invalid)
            {
                var s = Settings(); change(s); var b = new FakeBackend();
                using (var trigger = new ArtSharedTrigger(b, new FakeClock()))
                    RequireThrows<ArgumentException>(() => trigger.Prepare(s), "reject unsafe/ambiguous configuration");
                Require(b.Events.Count == 0, "validation before any backend or hardware access"); checks++;
            }
            foreach (string invalidLine in new[] { "Dev1/port0", "Dev1/port0/line4", "Dev1/port0/line0:7", "Dev1,Dev2/port0/line7", "Dev1/port1/line7" })
            {
                // Invalid paths are rejected before the P/Invoke, so this test
                // does not load the native driver or touch a device.
                RequireThrows<ArgumentException>(() => new NativeArtSharedTriggerBackend().ConfigureLine(new IntPtr(7), invalidLine), "single-line output boundary");
                checks++;
            }
            {
                var b = new FakeBackend { FailAt = "VERIFY:1026216030" };
                using (var t = new ArtSharedTrigger(b, new FakeClock()))
                    RequireThrows<IOException>(() => t.Prepare(Settings()), "identity failure");
                Sequence(b, "VERIFY:1026216030"); checks++;
            }
            {
                var b = new FakeBackend { ZeroHandle = true };
                using (var t = new ArtSharedTrigger(b, new FakeClock()))
                    RequireThrows<InvalidOperationException>(() => t.Prepare(Settings()), "zero task handle");
                Sequence(b, "VERIFY:1026216030,CREATE"); checks++;
            }
            {
                var b = new FakeBackend { FailAt = "CREATE" };
                using (var t = new ArtSharedTrigger(b, new FakeClock()))
                    RequireThrows<IOException>(() => t.Prepare(Settings()), "partial create failure");
                Sequence(b, "VERIFY:1026216030,CREATE,CLEAR"); checks++;
            }
            {
                var b = new FakeBackend { FailAt = "CONFIG:Dev1/port0/line7" };
                using (var t = new ArtSharedTrigger(b, new FakeClock()))
                    RequireThrows<IOException>(() => t.Prepare(Settings()), "channel configuration failure");
                Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,CLEAR"); checks++;
            }
            {
                var b = new FakeBackend { FailAt = "LOW" };
                using (var t = new ArtSharedTrigger(b, new FakeClock()))
                    RequireThrows<IOException>(() => t.Prepare(Settings()), "initial low failure");
                Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,LOW,STOP,CLEAR"); checks++;
            }
            {
                var b = new FakeBackend(); var clock = new FakeClock();
                using (var canceled = new CancellationTokenSource())
                using (var t = new ArtSharedTrigger(b, clock))
                {
                    t.Prepare(Settings()); canceled.Cancel();
                    RequireThrows<OperationCanceledException>(() => t.Pulse(canceled.Token), "cancel before high");
                }
                Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,LOW,STOP,CLEAR");
                Require(clock.Waited == 0, "pre-canceled never waits or raises line"); checks++;
            }
            {
                var b = new FakeBackend(); var clock = new FakeClock();
                using (var canceled = new CancellationTokenSource())
                using (var t = new ArtSharedTrigger(b, clock))
                {
                    clock.AfterPause = canceled.Cancel; t.Prepare(Settings());
                    RequireThrows<OperationCanceledException>(() => t.Pulse(canceled.Token), "cancel during high");
                }
                Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,HIGH,LOW,STOP,CLEAR"); checks++;
            }
            {
                var b = new FakeBackend { FailAt = "HIGH" }; var clock = new FakeClock();
                using (var t = new ArtSharedTrigger(b, clock))
                {
                    t.Prepare(Settings());
                    RequireThrows<IOException>(() => t.Pulse(CancellationToken.None), "high write failure");
                }
                Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,HIGH,LOW,STOP,CLEAR");
                Require(clock.Waited == 0, "failed high does not delay cleanup"); checks++;
            }
            foreach (string failure in new[] { "LOW", "STOP", "CLEAR" })
            {
                var b = new FakeBackend();
                using (var t = new ArtSharedTrigger(b, new FakeClock()))
                {
                    t.Prepare(Settings()); b.FailAt = failure;
                    RequireThrows<AggregateException>(() => t.Pulse(CancellationToken.None), "cleanup failure cannot report success");
                    Require(!t.Ready, "failed cleanup never ready");
                    Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,HIGH,LOW,STOP,CLEAR");
                }
                if (failure == "CLEAR")
                    Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,HIGH,LOW,STOP,CLEAR,LOW,STOP,CLEAR");
                checks++;
            }
            {
                var b = new FakeBackend(); var t = new ArtSharedTrigger(b, new FakeClock());
                t.Prepare(Settings()); t.Dispose(); t.Dispose();
                Sequence(b, "VERIFY:1026216030,CREATE,CONFIG:Dev1/port0/line7,LOW,LOW,STOP,CLEAR");
                RequireThrows<InvalidOperationException>(() => t.Prepare(Settings()), "disposed cannot be reused"); checks++;
            }
            return "TRIGGER_OUTPUT_TESTS=PASS: " + checks + " deterministic checks; fixed P0.7 only, explicit opt-in, identity before output, low/high/low, cancellation and failure cleanup, no repeated pulse; fake backend, no driver loads or physical outputs.";
        }

        private static BenchSettings Settings()
        {
            return new BenchSettings { RealHardware = true, AutomaticSharedTrigger = true, TriggerPulseMs = 20,
                ArtDevice = "Dev1", ArtSerial = "1026216030", PicoSerial = "13439/0066", ArtTrigger = "/Dev1/PFI0" };
        }
        private static void Sequence(FakeBackend b, string expected)
        { Require(String.Join(",", b.Events) == expected, "exact call sequence: " + String.Join(",", b.Events)); }
        private static void Require(bool value, string message)
        { if (!value) throw new Exception("Trigger output test failed: " + message); }
        private static void RequireThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Trigger output test expected " + typeof(T).Name + ": " + message);
        }
    }
}
