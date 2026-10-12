using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Pure managed control-flow fixtures. Never create a native backend or SDK handle.
    internal static class FactoryFinalAi4WorkflowTests
    {
        internal static string Run() { return FactoryOfflineAsyncTest.Run(Check); }

        private static async Task<string> Check()
        {
            int n = 0;
            Action<bool,string> check = (ok, message) => {
                if (!ok) throw new InvalidOperationException("Final AI4 workflow: " + message);
                ++n;
            };
            check(!new FactorySettings().FinalSequenceAi4Observation, "legacy/default settings do not request a measurement");
            foreach (int round in new[] { 0, 1, 2 })
            {
                var f = Fixture(false);
                if (round > 0) { f.Settings.SequenceId = "fake-sequence"; f.Settings.SequenceRound = round; f.Settings.SequenceCount = 3; }
                var backend = new FinalBackend(f);
                var result = Run(f, backend);
                check(backend.FinalReads == 0 && result.FinalAi4Observation == null, "single/intermediate " + round + " adds no AI4 read");
                check(result.CaptureOnlyAvailable() && !result.ResidualSafe && !result.Completed, "single/intermediate does not invent a safety result");
            }

            var normal = Fixture(true);
            var normalBackend = new FinalBackend(normal);
            var stages = new List<string>();
            var complete = Run(normal, normalBackend, (key, text) => stages.Add(key));
            check(normalBackend.FinalReads == 1, "explicit final round reads exactly once");
            check(normalBackend.OutputsOffAtRead && normalBackend.CaptureStoppedAtRead, "AI4 runs only after optical LOW, HV OFF, AO zero and finite stop");
            check(normalBackend.ReadAt - normal.Backend.OpticalAt >= 1000, "AI4 starts after the full normal optical hold");
            int stopped = normal.Backend.Events.FindLastIndex(e => e.Name == "STOP_CAPTURE");
            int final = normal.Backend.Events.FindIndex(e => e.Name == "FINAL_AI4");
            int off = normal.Backend.Events.FindLastIndex(final, e => e.Name == "SHUTDOWN");
            check(stopped >= 0 && off > stopped && final > off, "finite StopCapture then Shutdown precede final observation");
            check(complete.FinalAi4Observation.Available && complete.FinalAi4Observation.AbsMeanBelowThreshold, "valid mean diagnostic retained");
            check(complete.CaptureOnlyAvailable() && !complete.ResidualSafe && !complete.Completed, "low mean never becomes safety completion");
            check(stages.Contains("FINAL_AI4_MEASUREMENT") && !stages.Contains("STEP_COMPLETE_PASSED"), "reporting distinguishes observation from safety pass");
            check(normal.Backend.HvHighCount == 1 && normal.Backend.OpticalHighCount == 1, "final observation never reenergizes or retriggers");
            check(complete.Settings.Clone().FinalSequenceAi4Observation, "final opt-in survives settings clone");

            foreach (string invalidMode in new[] { "no-sequence", "middle", "calibration", "zero" })
            {
                var f = Fixture(true);
                if (invalidMode == "no-sequence") f.Settings.SequenceId = null;
                if (invalidMode == "middle") f.Settings.SequenceRound = 2;
                if (invalidMode == "zero") f.Settings.TargetKv = 0;
                var backend = new FinalBackend(f);
                Exception error = null;
                try { Run(f, backend, null, invalidMode == "calibration" ? FactoryRunKind.Calibration : FactoryRunKind.Pressure); }
                catch (Exception ex) { error = ex; }
                check(error != null && f.Backend.InitializeCount == 0 && backend.FinalReads == 0, invalidMode + " rejects before initialization");
            }

            foreach (string fault in new[] { "arm", "capture", "stop", "shutdown" })
            {
                var f = Fixture(true);
                f.Backend.FailArm = fault == "arm";
                f.Backend.FailRead = fault == "capture";
                f.Backend.FailStop = fault == "stop";
                f.Backend.FailShutdown = fault == "shutdown";
                var backend = new FinalBackend(f);
                var failure = Failure(() => Run(f, backend));
                check(backend.FinalReads == 0 && failure.PartialResult.FinalAi4Observation == null, fault + " cannot start a later measurement");
                check(!failure.PartialResult.AcquisitionCompleted && !failure.PartialResult.ResidualSafe, fault + " stays failed");
            }

            FactoryResult retained = null;
            foreach (string fault in new[] { "timeout", "partial-cleanup", "partial-cancel", "return-cancel", "invalid", "null" })
            {
                var f = Fixture(true);
                var backend = new FinalBackend(f);
                backend.Read = token => {
                    if (fault == "timeout") throw new TimeoutException("fake final AI4 read timeout");
                    var data = Observation(f.Settings.Input.DividerVPerV);
                    if (fault == "partial-cleanup") throw new FactoryFinalAi4ObservationException(data, new IOException("fake final task clear failure"));
                    if (fault == "partial-cancel") { f.Cancellation.Cancel(); throw new FactoryFinalAi4ObservationException(data, new OperationCanceledException(token)); }
                    if (fault == "return-cancel") f.Cancellation.Cancel();
                    if (fault == "invalid") { data.Available = false; data.AbsMeanBelowThreshold = false; data.Error = "fake incomplete window"; }
                    return fault == "null" ? null : data;
                };
                var failure = Failure(() => Run(f, backend));
                var record = failure.PartialResult.FinalAi4Observation;
                check(backend.FinalReads == 1 && record != null && record.Requested && !record.Available && !record.AbsMeanBelowThreshold, fault + " records requested but unavailable");
                check(!String.IsNullOrEmpty(record.Error) && !failure.PartialResult.AcquisitionCompleted && !failure.PartialResult.Completed && !failure.PartialResult.ResidualSafe, fault + " keeps failure and denies completion");
                check(f.Backend.HvHighCount == 1 && f.Backend.OpticalHighCount == 1 && !f.Backend.Hv && !f.Backend.Optical && f.Backend.Ao == 0, fault + " cleanup does not emit another output");
                if (fault.StartsWith("partial-", StringComparison.Ordinal) || fault == "return-cancel" || fault == "invalid")
                    check(record.RawVolts.Length == 800 && record.RawVolts[11] == .002, fault + " preserves already acquired raw evidence");
                if (fault == "partial-cleanup") retained = failure.PartialResult;
            }
            var beforeRead = Fixture(true);
            var beforeBackend = new FinalBackend(beforeRead);
            var cancelledBefore = Failure(() => Run(beforeRead, beforeBackend, (key, text) => {
                if (key == "STEP_FINAL_AI4_RUNNING") beforeRead.Cancellation.Cancel();
            }));
            check(beforeBackend.FinalReads == 0 && cancelledBefore.PartialResult.FinalAi4Observation != null &&
                !cancelledBefore.PartialResult.FinalAi4Observation.Available, "cancel before AI4 records omitted read without invoking it");

            string directory = Path.Combine(Path.GetTempPath(), "Xiongxin-Final-AI4-Workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string settingsPath = Path.Combine(directory, "final-settings.xml");
            normal.Settings.Save(settingsPath);
            var loadedSettings = FactorySettings.Load(settingsPath);
            check(loadedSettings.FinalSequenceAi4Observation && loadedSettings.SequenceRound == loadedSettings.SequenceCount, "final settings XML round trip");
            ArchiveRoundTrip(complete, Path.Combine(directory, "complete"), false, check);
            ArchiveRoundTrip(retained, Path.Combine(directory, "partial"), true, check);

            var wrapped = Fixture(true);
            var wrappedBackend = new FinalBackend(wrapped);
            string wrappedDirectory = Path.Combine(directory, "decorated");
            Directory.CreateDirectory(wrappedDirectory);
            var reporter = new FactoryEmergencyReportingBackend(wrappedBackend, (key, text) => { }, true);
            var recorder = new FactoryAiRecordingBackend(reporter, wrappedDirectory, wrapped.Settings, wrapped.Clock);
            var decoratedResult = Run(wrapped, recorder);
            check(wrappedBackend.FinalReads == 1 && decoratedResult.FinalAi4Observation.Available, "both production decorators forward one final read");

            var first = new FactoryTests.Fixture().Settings;
            first.Input.ArtSerial = "SIM-ART"; first.Input.PicoSerial = "SIM-PICO";
            first.Input.OscillationCircuitConnected = true; first.EndAfterPressureCapture = true; first.Input.RangeIndex = 10;
            var issued = new List<FactorySettings>();
            int count = await FactoryPressureSequence.Run(first, 3, next => {
                issued.Add(next.Clone());
                return Task.FromResult(FactoryPressureSequenceTests.Capture(next));
            }, null, null, CancellationToken.None);
            check(count == 3 && issued.Count == 3, "three-round simulation sequence completes");
            check(!issued[0].FinalSequenceAi4Observation && !issued[1].FinalSequenceAi4Observation && issued[2].FinalSequenceAi4Observation, "sequence sets observation only on final issued round");
            check(!first.FinalSequenceAi4Observation, "sequence does not contaminate caller defaults");

            foreach (string interruption in new[] { "CONNECTION_LOST", "EMERGENCY_ACTIVE" })
            {
                var ui = first.Clone();
                var progress = new FactorySequenceProgress(ui, 3);
                progress.Begin(ui, 3);
                for (int round = 1; round <= 3; ++round)
                {
                    var pending = ui.Clone();
                    pending.SequenceId = progress.Id; pending.SequenceCount = 3;
                    pending.SequenceRound = pending.Input.RunNumber = round;
                    pending.FinalSequenceAi4Observation = round == 3;
                    progress.Starting(pending);
                    if (round < 3) progress.Captured();
                }
                progress.Observe(interruption); progress.End(); progress.RememberStoppedUi(ui);
                check(!ui.FinalSequenceAi4Observation && progress.Matches(ui, 3), interruption + " final checkpoint matches unchanged UI without issued-only flag");
                check(progress.Completed == 2 && progress.Remaining == 1 && progress.CanResume && !progress.Running,
                    interruption + " final incomplete round remains pending, not automatically running");
                var request = progress.ResumeRequest(ui, 3);
                check(request.FinalSequenceAi4Observation && request.SequenceRound == 3 &&
                    !request.FeedbackWiringConfirmed && !request.IndependentDischargeConfirmed,
                    interruption + " explicit resume preserves final diagnostic flag but not prior permission");
                var resumed = new List<FactorySettings>();
                int resumedCount = await FactoryPressureSequence.Run(request, 3, next => {
                    resumed.Add(next.Clone());
                    return Task.FromResult(FactoryPressureSequenceTests.Capture(next));
                }, null, null, CancellationToken.None, null, progress);
                check(resumedCount == 3 && resumed.Count == 1 && resumed[0].SequenceRound == 3 &&
                    resumed[0].FinalSequenceAi4Observation && resumed[0].SequenceAttempt == 2,
                    interruption + " explicitly resumed dispatch repeats only unfinished final round with fresh attempt");
                check(!progress.CanResume && !progress.Running && progress.Remaining == 0,
                    interruption + " finished resumed sequence cannot be replayed again");
            }
            var middleProgress = new FactorySequenceProgress(first, 3);
            middleProgress.Begin(first, 3);
            var middleIssued = first.Clone(); middleIssued.SequenceId = middleProgress.Id; middleIssued.SequenceCount = 3;
            middleProgress.Starting(middleIssued); middleProgress.Captured();
            middleIssued.SequenceRound = middleIssued.Input.RunNumber = 2;
            middleProgress.Starting(middleIssued); middleProgress.Observe("CONNECTION_LOST");
            middleProgress.End(); middleProgress.RememberStoppedUi(first);
            check(middleProgress.Matches(first, 3) && !middleProgress.ResumeRequest(first, 3).FinalSequenceAi4Observation,
                "interrupted middle-round checkpoint remains a non-final observation request");
            return "Final AI4 workflow: " + n + " checks PASS; managed fixtures only; HardwareAccessed=False";
        }

        private static FactoryTests.Fixture Fixture(bool final)
        {
            var f = new FactoryTests.Fixture();
            f.Settings.EndAfterPressureCapture = true;
            if (final) { f.Settings.SequenceId = "fake-final-ai4"; f.Settings.SequenceRound = 3; f.Settings.SequenceCount = 3; f.Settings.FinalSequenceAi4Observation = true; }
            return f;
        }

        private static FactoryResult Run(FactoryTests.Fixture f, IFactoryBackend backend,
            Action<string,string> report = null, FactoryRunKind kind = FactoryRunKind.Pressure)
        { return FactoryRunner.Run(f.Settings, kind, backend, f.Clock, report ?? ((key, text) => { }), f.Cancellation.Token); }

        private static FactoryRunException Failure(Action run)
        {
            try { run(); } catch (FactoryRunException ex) { return ex; }
            throw new InvalidOperationException("Expected final AI4 workflow failure, but run succeeded.");
        }

        private static FactoryFinalAi4Observation Observation(double divider)
        {
            var raw = Enumerable.Repeat(.002, 800).ToArray();
            var start = DateTime.UtcNow;
            return FactoryFinalAi4Observation.Evaluate(raw, 80000, divider, true, start, start.AddMilliseconds(10));
        }

        private static void ArchiveRoundTrip(FactoryResult source, string directory, bool partial, Action<bool,string> check)
        {
            Directory.CreateDirectory(directory);
            Program.SaveResult(source, directory, partial);
            FactoryResult loaded;
            using (var stream = File.OpenRead(Path.Combine(directory, partial ? "partial-result.xml" : "result.xml")))
                loaded = (FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(stream);
            check(loaded.FinalAi4Observation != null && loaded.FinalAi4Observation.RawVolts.SequenceEqual(source.FinalAi4Observation.RawVolts), "archive restores exact " + (partial ? "partial" : "final") + " raw window");
            check(loaded.FinalAi4Observation.Available == source.FinalAi4Observation.Available &&
                (loaded.FinalAi4Observation.Error ?? "").Replace("\r\n", "\n") == (source.FinalAi4Observation.Error ?? "").Replace("\r\n", "\n") &&
                !loaded.ResidualSafe && !loaded.Completed, "archive preserves diagnostic/error and unverified safety flags");
        }

        private sealed class FinalBackend : IFactoryBackend, IFactoryFinalAi4Observation
        {
            private readonly FactoryTests.Fixture f;
            internal int FinalReads;
            internal long ReadAt;
            internal bool OutputsOffAtRead, CaptureStoppedAtRead;
            internal Func<CancellationToken,FactoryFinalAi4Observation> Read;
            internal FinalBackend(FactoryTests.Fixture fixture) { f = fixture; }
            public FactoryFinalAi4Observation ReadFinalAi4Observation(CancellationToken token)
            {
                ++FinalReads; ReadAt = f.Clock.ElapsedMilliseconds;
                OutputsOffAtRead = !f.Backend.Hv && !f.Backend.Optical && f.Backend.Ao == 0;
                CaptureStoppedAtRead = !f.Backend.Armed && f.Backend.Events.Any(e => e.Name == "STOP_CAPTURE");
                f.Backend.Events.Add(new FactoryTests.FakeEvent("FINAL_AI4", ReadAt));
                return Read == null ? Observation(f.Settings.Input.DividerVPerV) : Read(token);
            }
            public void Initialize(FactorySettings settings, bool calibration) { f.Backend.Initialize(settings, calibration); }
            public FactoryFeedback ReadFeedback() { return f.Backend.ReadFeedback(); }
            public bool ReadEmergencyReleased() { return f.Backend.ReadEmergencyReleased(); }
            public void WriteAo(double volts) { f.Backend.WriteAo(volts); }
            public void SetHv(bool on) { f.Backend.SetHv(on); }
            public void SetOptical(bool on) { f.Backend.SetOptical(on); }
            public void Arm(bool freeRun) { f.Backend.Arm(freeRun); }
            public void ArmBeforeShutdown() { f.Backend.ArmBeforeShutdown(); }
            public bool ArtComplete { get { return f.Backend.ArtComplete; } }
            public bool PicoComplete { get { return f.Backend.PicoComplete; } }
            public BenchCapture ReadCapture() { return f.Backend.ReadCapture(); }
            public void StopCapture() { f.Backend.StopCapture(); }
            public void Shutdown() { f.Backend.Shutdown(); }
            public void Dispose() { f.Backend.Dispose(); }
        }
    }
}
