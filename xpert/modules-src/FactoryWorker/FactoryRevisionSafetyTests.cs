using System;
using System.Collections.Generic;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryRevisionSafetyTests
    {
        internal static string Run()
        {
            int checks = 0;
            var settings = Settings();
            var firstClock = new Clock(); var firstBackend = new Backend(firstClock) { ClipCapture = true };
            FactoryResult first = FactoryRunner.Run(settings, FactoryRunKind.Pressure, firstBackend,
                firstClock, null, CancellationToken.None);
            Require(first.Completed && first.ResidualSafe && first.Metrics.PdClipped && Double.IsNaN(first.Metrics.MaxPdPc),
                "range prompt may follow a safely completed clipped capture, but clipped PD is not quantitative"); checks++;
            Require(firstBackend.HvHighCount == 1 && firstBackend.OpticalCount == 1 && firstBackend.Disposed,
                "one pressure round emits only one HV start and one optical pulse before release"); checks++;
            int measuredRange = first.Capture.Settings.RangeIndex;
            FactorySettings next = first.Settings.Clone(); next.Input.RangeIndex = first.Metrics.RecommendedRangeIndex;
            Require(next.TargetKv == first.Settings.TargetKv && next.Input.RangeIndex != measuredRange &&
                first.Capture.Settings.RangeIndex == measuredRange,
                "next range changes a cloned same-target snapshot and cannot reinterpret old ADC"); checks++;
            var secondClock = new Clock(); var secondBackend = new Backend(secondClock) { InitialDividerV = .01 };
            FactoryRunException secondFailure = Failure(() => FactoryRunner.Run(next, FactoryRunKind.Pressure,
                secondBackend, secondClock, null, CancellationToken.None));
            Require(secondBackend.Initializations == 1 && secondBackend.HvHighCount == 0 && secondBackend.OpticalCount == 0 && secondBackend.Disposed,
                "a repeated target still reinitializes and blocks all activation when fresh initial residual is unsafe"); checks++;
            Require(!secondFailure.PartialResult.Completed && !secondFailure.PartialResult.ResidualSafe,
                "previous round safety cannot be inherited by the rejected retry"); checks++;

            var lateClock = new Clock(); var lateBackend = new Backend(lateClock);
            using (var cancel = new CancellationTokenSource())
            {
                FactoryRunException failure = Failure(() => FactoryRunner.Run(settings, FactoryRunKind.Pressure,
                    lateBackend, lateClock, (stage, detail) => { if (stage == "STEP_READ_PASSED") cancel.Cancel(); }, cancel.Token));
                Require(failure.PartialResult.Capture != null && !failure.PartialResult.Completed && !failure.PartialResult.ResidualSafe,
                    "cancel after complete ADC read preserves partial data and does not authorize automatic repressure"); checks++;
                Require(lateBackend.HvHighCount == 1 && lateBackend.OpticalCount == 1 && lateBackend.Disposed &&
                    !lateBackend.Hv && !lateBackend.Optical && lateBackend.Ao == 0,
                    "late cancel lowers all owned outputs and never starts a second round"); checks++;
            }

            var residualClock = new Clock(); var residualBackend = new Backend(residualClock) { PostCaptureDividerV = .01 };
            FactorySettings residualSettings = settings.Clone(); residualSettings.DischargeTimeoutSeconds = .05;
            FactoryRunException residualFailure = Failure(() => FactoryRunner.Run(residualSettings, FactoryRunKind.Pressure,
                residualBackend, residualClock, null, CancellationToken.None));
            Require(residualFailure.PartialResult.Capture != null && !residualFailure.PartialResult.ResidualSafe &&
                !residualFailure.PartialResult.Completed && residualBackend.OpticalCount == 1,
                "VMON zero does not overrule independently unsafe AI4 residual or trigger another optical pulse"); checks++;

            var calibrationClock = new Clock(); var calibrationBackend = new Backend(calibrationClock);
            FactoryResult calibration = FactoryRunner.Run(settings, FactoryRunKind.Calibration,
                calibrationBackend, calibrationClock, null, CancellationToken.None);
            Require(calibration.Completed && calibration.ResidualSafe && !calibration.Calibration.Valid &&
                calibrationBackend.HvHighCount == 0 && calibrationBackend.OpticalCount == 0 && calibrationBackend.PositiveAoCount == 0,
                "calibration free-run background does not fabricate coefficient or activate pressure outputs"); checks++;
            return "Factory revision safety passed " + checks + " offline checks; no hardware accessed.";
        }

        private static FactorySettings Settings()
        {
            return new FactorySettings { Simulation = true, TargetKv = .5,
                Input = new BenchSettings { RequestedPicoRateHz = 125000, CaptureDurationMs = 60,
                    RangeIndex = 5, ResolutionBits = 14, RealHardware = false, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO" } };
        }
        private sealed class Clock : IFactoryClock
        {
            private long milliseconds;
            public long ElapsedMilliseconds { get { return milliseconds; } }
            public void Pause(int duration, CancellationToken cancellation)
            { if (duration < 0) throw new ArgumentOutOfRangeException(); cancellation.ThrowIfCancellationRequested(); milliseconds += duration; }
        }
        private sealed class Backend : IFactoryBackend
        {
            private readonly Clock clock;
            private FactorySettings settings;
            private bool armed, freeRun, read;
            private long captureAt = Int64.MaxValue;
            internal int Initializations, HvHighCount, OpticalCount, PositiveAoCount;
            internal bool Hv, Optical, Disposed, ClipCapture;
            internal double Ao, InitialDividerV, PostCaptureDividerV;
            internal Backend(Clock value) { clock = value; }
            public void Initialize(FactorySettings requested, bool calibration)
            { Initializations++; settings = requested.Clone(); }
            public FactoryFeedback ReadFeedback()
            {
                if (armed) throw new InvalidOperationException("finite AI owns monitor");
                double voltage = Hv ? Ao * settings.PressureVPerV : 0;
                return new FactoryFeedback { TimestampUtc = DateTime.UtcNow, EmergencyReleased = true,
                    ImonRawV = 0, VmonRawV = voltage / (settings.VmonFullScaleKv * 1000) * settings.FeedbackFullScaleV,
                    DividerRawV = read ? PostCaptureDividerV : Hv ? voltage / settings.Input.DividerVPerV : InitialDividerV, AoV = Ao };
            }
            public bool ReadEmergencyReleased() { return true; }
            public void WriteAo(double value)
            { if (value > 0) { if (!Hv || Optical) throw new InvalidOperationException("unsafe AO state"); PositiveAoCount++; } Ao = value; }
            public void SetHv(bool value)
            { if (value) { if (armed || Optical || Ao != 0) throw new InvalidOperationException("unsafe HV state"); HvHighCount++; } Hv = value; }
            public void SetOptical(bool value)
            {
                if (value)
                {
                    if (!armed || freeRun || Hv || Ao != 0 || OpticalCount != 0) throw new InvalidOperationException("unsafe optical state");
                    OpticalCount++; captureAt = clock.ElapsedMilliseconds;
                }
                Optical = value;
            }
            public void Arm(bool free)
            { if (Hv || Ao != 0 || Optical) throw new InvalidOperationException("unsafe arm state"); armed = true; freeRun = free; if (free) captureAt = clock.ElapsedMilliseconds; }
            public void ArmBeforeShutdown()
            { if (!Hv || Optical || armed) throw new InvalidOperationException("unsafe pre-off arm state"); armed=true; freeRun=false; }
            private bool Complete { get { return armed && captureAt != Int64.MaxValue && clock.ElapsedMilliseconds - captureAt >= settings.Input.CaptureDurationMs; } }
            public bool ArtComplete { get { return Complete; } }
            public bool PicoComplete { get { return Complete; } }
            public BenchCapture ReadCapture()
            {
                if (!Complete || Hv || Ao != 0 || Optical) throw new InvalidOperationException("unsafe read state");
                read = true;
                var capture = new BenchCapture { Settings = settings.Clone().Input, Simulation = true,
                    CapturedUtc = DateTime.UtcNow, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO", Runtime = "revision-fake-only",
                    ArtRateHz = 80000, PicoRateHz = settings.Input.RequestedPicoRateHz,
                    PicoFullScaleV = BenchSettings.Ranges[settings.Input.RangeIndex], MaximumAdc = 32767,
                    HardwareBits = PicoCaptureConfiguration.HardwareBits(settings.Input.ResolutionBits, settings.Input.RangeIndex),
                    VoltageRaw = new double[settings.Input.ExpectedArtCount], PdAdc = new short[settings.Input.RequestedPicoCount] };
                if (ClipCapture) capture.PdAdc[0] = capture.PdAdc[1] = 32767;
                return capture;
            }
            public void StopCapture() { armed = false; }
            public void Shutdown() { Hv = Optical = false; Ao = 0; }
            public void Dispose() { Disposed = true; Shutdown(); armed = false; }
        }
        private static FactoryRunException Failure(Func<FactoryResult> run)
        { try { run(); } catch (FactoryRunException failure) { return failure; } throw new InvalidOperationException("Expected unsafe result rejection."); }
        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException("Factory revision safety check failed: " + detail); }
    }
}
