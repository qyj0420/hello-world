using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace Xiongxin.ZeroBench
{
    internal static class CoreTests
    {
        internal static string Run()
        {
            var settings = new BenchSettings(); settings.Validate();
            var unsafeSettings = new BenchSettings { TargetVoltageV = 1 };
            RequireRejected(() => unsafeSettings.Validate(), "nonzero target");
            RequireRejected(() => new BenchSettings { RealHardware = true }.Validate(), "unspecified identities");
            RequireRejected(() => new BenchSettings { ArtTrigger = "/Dev2/PFI0" }.Validate(), "other ART trigger");
            RequireRejected(() => new BenchSettings { ArtTerminal = -1 }.Validate(), "implicit input terminal");
            RequireRejected(() => new BenchSettings { AutomaticSharedTrigger = true, ArtTrigger = "/Dev1/PFI1" }.Validate(), "active trigger must use PFI0");
            RequireRejected(() => new BenchSettings { AutomaticSharedTrigger = true, FallingTrigger = true }.Validate(), "active trigger must be rising");
            RequireRejected(() => new BenchSettings { AutomaticSharedTrigger = true, ExternalTriggerVolts = 0 }.Validate(), "positive active EXT threshold");
            Require(PicoInputCapture.ConvertExternalThreshold(1.5) == 9830, "EXT 1.5V code independent of A range");
            Require(PicoInputCapture.ConvertExternalThreshold(-1.5) == -9830, "EXT negative code");
            RequireRejected(() => PicoInputCapture.ConvertExternalThreshold(Double.NaN), "EXT NaN");
            RequireRejected(() => PicoInputCapture.ConvertExternalThreshold(5), "EXT boundary");
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var events = new List<string>();
            settings.SimulationOscillation = true;
            var simulated = BenchRunner.Simulate(settings, directory, (stage, message) => events.Add(stage), CancellationToken.None);
            var copy = BenchCapture.Load(directory);
            Require(events.IndexOf("HV_UP") < events.IndexOf("HV_OFF") && events.IndexOf("HV_OFF") < events.IndexOf("ARM") && events.IndexOf("ARM") < events.IndexOf("WAIT_TRIGGER"), "zero target sequence ordering");
            Require(copy.Simulation && copy.VoltageRaw.Length == 4800 && copy.PdAdc.Length == 7500000, "complete round trip");
            Require(copy.PdAdc[700000] == simulated.PdAdc[700000] && copy.PicoFullScaleV == simulated.PicoFullScaleV, "raw data unchanged");
            string settingsPath = Path.Combine(directory, "settings.xml");
            string currentXml = File.ReadAllText(settingsPath);
            string legacyXml = currentXml;
            foreach (string element in new[] { "RequestedPicoRateHz", "ResolutionBits", "CaptureDurationMs", "PicoChannelAEnabled", "PicoCoupling", "PicoBandwidth20MHz", "PicoAnalogOffsetVolts" })
                legacyXml = System.Text.RegularExpressions.Regex.Replace(legacyXml, @"\s*<" + element + @">[^<]*</" + element + @">", String.Empty);
            try
            {
                File.WriteAllText(settingsPath, legacyXml);
                var legacy = BenchCapture.Load(directory);
                Require(legacy.Settings.RequestedPicoRateHz == BenchSettings.PicoRate && legacy.Settings.ResolutionBits == 14 &&
                    legacy.Settings.CaptureDurationMs == 60 && legacy.Settings.PicoChannelAEnabled && legacy.Settings.PicoCoupling == 1 &&
                    legacy.PdAdc[700000] == simulated.PdAdc[700000], "legacy XML and v1 capture defaults and raw samples preserved");
            }
            finally { File.WriteAllText(settingsPath, currentXml); }
            double savedRate = copy.PicoRateHz;
            copy.PicoRateHz = Double.NaN;
            RequireInvalidData(() => copy.ValidateCapture(), "NaN sample rate");
            copy.PicoRateHz = savedRate;
            int savedAdc = copy.MaximumAdc;
            copy.MaximumAdc = 0;
            RequireInvalidData(() => copy.ValidateCapture(), "zero ADC upper bound");
            copy.MaximumAdc = savedAdc;
            double savedValue = copy.VoltageRaw[0]; copy.VoltageRaw[0] = Double.PositiveInfinity;
            RequireInvalidData(() => copy.ValidateCapture(), "nonfinite voltage sample");
            copy.VoltageRaw[0] = savedValue;
            bool savedSource = copy.Simulation; copy.Simulation = false;
            RequireInvalidData(() => copy.ValidateCapture(), "source mismatch");
            copy.Simulation = savedSource;
            string savedSerial = copy.PicoSerial; copy.PicoSerial = "OTHER-CARD";
            RequireInvalidData(() => copy.ValidateCapture(), "record identity mismatch");
            copy.PicoSerial = savedSerial;
            string rawPath = Path.Combine(directory, "capture.bin");
            long rawLength = new FileInfo(rawPath).Length;
            using (var stream = new FileStream(rawPath, FileMode.Append, FileAccess.Write)) stream.WriteByte(1);
            RequireInvalidData(() => BenchCapture.Load(directory), "undeclared trailing bytes");
            using (var stream = new FileStream(rawPath, FileMode.Open, FileAccess.Write)) stream.SetLength(rawLength);
            copy.ValidateCapture();
            var v = SignalAnalysis.AnalyzeVoltage(copy.VoltageRaw, copy.ArtRateHz, 11000, .7, 22, true);
            Require(v.Valid && Math.Abs(v.FrequencyHz - 600) < 2, "simulation metric");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool didCancel = false;
                try { BenchRunner.Simulate(settings, directory, (a, b) => { }, cancelled.Token); } catch (OperationCanceledException) { didCancel = true; }
                Require(didCancel, "cancelled workflow must not complete");
            }
            using (var child = Process.Start(new ProcessStartInfo { FileName = System.Reflection.Assembly.GetExecutingAssembly().Location, Arguments = "--job-probe", UseShellExecute = false, CreateNoWindow = true }))
            {
                try { using (var job = new ProcessJob()) { job.Attach(child); } Require(child.WaitForExit(3000), "job close terminates owned worker"); }
                finally { if (!child.HasExited) child.Kill(); }
            }
            return "CORE_TESTS=PASS: zero-target bound, serial lock, trigger ownership, explicit terminal, sequence, full raw round-trip, corrupt metadata rejection, cancellation, worker job lifetime; no hardware accessed.";
        }
        private static void Require(bool condition, string name) { if (!condition) throw new Exception("Test failed: " + name); }
        private static void RequireRejected(Action action, string name) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Require(rejected, name); }
        private static void RequireInvalidData(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Require(rejected, name); }
    }
}
