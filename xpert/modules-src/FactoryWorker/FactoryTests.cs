using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // All tests execute managed fakes only. Neither creating a fake nor running
    // these tests loads a native driver, enumerates a device, or writes a port.
    internal static class FactoryTests
    {
        internal static void Run(TextWriter writer)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            int cases = 0;
            Action<string, Action> test = (name, run) =>
            {
                try { run(); }
                catch (Exception ex) { throw new InvalidOperationException("Factory fake regression failed: " + name, ex); }
                writer.WriteLine("PASS fake: " + name); cases++;
            };

            test("0 V cannot enter the output-bearing pressure state machine", () =>
            {
                var f = new Fixture(); f.Settings.TargetKv = 0; ExpectFailure(() => f.Run());
                Require(f.Backend.InitializeCount == 0 && f.Backend.HvHighCount == 0 && f.Backend.PositiveAoWrites == 0 &&
                    f.Backend.OpticalHighCount == 0 && f.Backend.ShutdownCount == 0,"zero requires isolated input-only runner, never output initialization/shutdown");
            });
            test("nonzero target has bounded ramp then HV OFF before optical", () =>
            {
                var f = new Fixture(); f.Settings.TargetKv = 1;
                var result = f.Run();
                Require(result.Completed && f.Backend.PositiveAoWrites > 0, "nonzero ramp must produce AO requests");
                Require(f.Backend.MaximumAo <= f.Settings.MaximumAoV &&
                    f.Backend.MaximumAo <= f.Settings.AoCommandCeilingV, "AO envelope");
                Require(result.Ramp.Count >= 5 && f.Backend.ArmedBeforeShutdown, "target reached, arm before shutdown");
                AssertOrder(f.Backend,"HV_HIGH","ARM_EXT","SHUTDOWN","OPTICAL_HIGH");
                AssertSingleOptical(f.Backend, 1000); AssertFinalOff(f.Backend);
            });
            test("slow completed-data read cannot extend optical HIGH", () =>
            {
                var f = new Fixture(); f.Backend.ReadDelayMs = 1700;
                Require(f.Run().Completed, "slow fake completed read should finish");
                AssertSingleOptical(f.Backend, 1000);
                Require(f.Backend.ReadStartedWhenOff && f.Clock.ElapsedMilliseconds >= 3200, "read happens only after low");
            });
            test("cancellation while optical HIGH immediately returns LOW without retry", () =>
            {
                var f = new Fixture();
                f.Clock.AfterPause = () => { if (f.Backend.Optical && f.Clock.ElapsedMilliseconds - f.Backend.OpticalAt >= 130) f.Cancellation.Cancel(); };
                var error = ExpectFailure(() => f.Run());
                Require(Contains<OperationCanceledException>(error), "cancellation reason retained: " + error);
                AssertEarlyOptical(f.Backend); AssertFinalOff(f.Backend);
                Require(f.Backend.ReadCount == 0 && f.Backend.DisposeCount == 1, "cancel cannot read or relaunch");
            });
            test("P0.4 drops while optical HIGH returns LOW and cannot fire again", () =>
            {
                var f = new Fixture();
                f.Backend.EmergencyRule = b => !b.Optical || f.Clock.ElapsedMilliseconds - b.OpticalAt < 130;
                ExpectFailure(() => f.Run()); AssertEarlyOptical(f.Backend); AssertFinalOff(f.Backend);
                Require(f.Backend.ReadCount == 0, "emergency abort does not manufacture complete data");
            });
            test("extended optical interval still honours cancellation after the former 1-second end", () =>
            {
                var f = new Fixture();
                f.Clock.AfterPause = () => { if (f.Backend.Optical && f.Clock.ElapsedMilliseconds - f.Backend.OpticalAt >= 1200) f.Cancellation.Cancel(); };
                var error = ExpectFailure(() => f.Run());
                Require(Contains<OperationCanceledException>(error), "cancellation remains active throughout the added 500 ms");
                AssertEarlyOptical(f.Backend); AssertFinalOff(f.Backend);
                Require(f.Backend.ReadCount == 0 && f.Backend.OpticalHighCount == 1, "cancelled extended hold neither reads nor retries");
            });
            test("P0.4 initially low blocks HV ON and optical", () =>
            {
                var f = new Fixture(); f.Backend.EmergencyRule = b => false;
                ExpectFailure(() => f.Run()); AssertNoActivation(f.Backend); AssertFinalOff(f.Backend);
            });
            test("arm failure cannot emit optical trigger", () =>
            {
                var f = new Fixture(); f.Backend.FailArm = true;
                ExpectFailure(() => f.Run());
                Require(f.Backend.OpticalHighCount == 0 && f.Backend.ReadCount == 0, "failed arm cannot fire or read");
                AssertFinalOff(f.Backend);
            });
            test("early completion before optical rejects pre-triggered capture", () =>
            {
                var f = new Fixture(); f.Backend.PrematureComplete = true;
                ExpectFailure(() => f.Run());
                Require(f.Backend.OpticalHighCount == 0 && f.Backend.ReadCount == 1, "early-trigger complete data retained without light");
                AssertFinalOff(f.Backend);
            });
            test("trigger timeout has no automatic trigger or second optical command", () =>
            {
                var f = new Fixture(); f.Backend.NeverComplete = true;
                var error = ExpectFailure(() => f.Run());
                Require(Contains<TimeoutException>(error), "timeout reason retained");
                AssertSingleOptical(f.Backend, 1000); AssertFinalOff(f.Backend);
                Require(f.Backend.ArmCount == 1 && f.Backend.ReadCount == 0, "timeout cannot rearm or accept partial segment");
            });
            test("initialization identity failure attempts cleanup and never activates", () =>
            {
                var f = new Fixture(); f.Backend.FailInitialize = true;
                ExpectFailure(() => f.Run());
                Require(f.Backend.ShutdownCount >= 1 && f.Backend.DisposeCount == 1, "failed init still gets shutdown and dispose");
                AssertNoActivation(f.Backend); AssertFinalOff(f.Backend);
            });
            test("initial IMON limit blocks nonzero pressure before HV ON", () =>
            {
                var f = new Fixture(); f.Backend.FeedbackRule = (b, value) => { value.ImonRawV = 5; return value; };
                ExpectFailure(() => f.Run()); AssertNoActivation(f.Backend); AssertFinalOff(f.Backend);
            });
            test("IMON fault during HV startup delay blocks ramp and optical", () =>
            {
                var f = new Fixture(); f.Settings.TargetKv = 1;
                f.Backend.FeedbackRule = (b, value) => { if (b.Hv) value.ImonRawV = 5; return value; };
                ExpectFailure(() => f.Run());
                Require(f.Backend.HvHighCount == 1 && f.Backend.PositiveAoWrites == 0 && f.Backend.OpticalHighCount == 0, "startup limit abort");
                AssertFinalOff(f.Backend);
            });
            test("first ramp sample enforces IMON limit before AO increment", () =>
            {
                var f = new Fixture();
                f.Backend.FeedbackRule = (b, value) => { if (b.Hv && f.Clock.ElapsedMilliseconds >= f.Settings.Input.HvDelayMs) value.ImonRawV = 5; return value; };
                ExpectFailure(() => f.Run());
                Require(f.Backend.OpticalHighCount == 0 && f.Backend.PositiveAoWrites == 0, "ramp entry cannot skip current protection");
                AssertFinalOff(f.Backend);
            });
            test("stale feedback blocks output", () =>
            {
                var f = new Fixture(); f.Backend.FeedbackRule = (b, value) => { value.TimestampUtc = DateTime.UtcNow.AddSeconds(-5); return value; };
                ExpectFailure(() => f.Run()); AssertNoActivation(f.Backend);
            });
            test("NaN feedback blocks output", () =>
            {
                var f = new Fixture(); f.Backend.FeedbackRule = (b, value) => { value.DividerRawV = Double.NaN; return value; };
                ExpectFailure(() => f.Run()); AssertNoActivation(f.Backend);
            });
            test("saturated raw feedback blocks output", () =>
            {
                var f = new Fixture(); f.Backend.FeedbackRule = (b, value) => { value.VmonRawV = 6; return value; };
                ExpectFailure(() => f.Run()); AssertNoActivation(f.Backend);
            });
            test("initial divider residual blocks HV ON", () =>
            {
                var f = new Fixture(); f.Backend.FeedbackRule = (b, value) => { value.DividerRawV = .02; return value; };
                ExpectFailure(() => f.Run()); AssertNoActivation(f.Backend);
            });
            test("unexpected voltage while AO zero aborts startup", () =>
            {
                var f = new Fixture();
                f.Backend.FeedbackRule = (b, value) => { if (b.Hv) value.VmonRawV = .03; return value; };
                ExpectFailure(() => f.Run());
                Require(f.Backend.PositiveAoWrites == 0 && f.Backend.OpticalHighCount == 0, "unexpected zero-command voltage cannot continue");
                AssertFinalOff(f.Backend);
            });
            test("VMON/divider mismatch stops nonzero ramp", () =>
            {
                var f = new Fixture(); f.Settings.TargetKv = 1;
                f.Backend.FeedbackRule = (b, value) => { if (b.Hv && b.Ao > 0) value.DividerRawV *= .5; return value; };
                var error = ExpectFailure(() => f.Run());
                Require(error.ToString().Contains("不一致") && f.Backend.PositiveAoWrites > 0, "feedback mismatch must be the rejection");
                Require(f.Backend.OpticalHighCount == 0, "mismatch cannot proceed to optical"); AssertFinalOff(f.Backend);
            });
            test("ramp timeout with no feedback growth cannot relaunch", () =>
            {
                var f = new Fixture(); f.Settings.TargetKv = 1; f.Settings.RampTimeoutSeconds = .05;
                f.Backend.FeedbackRule = (b, value) => { if (b.Hv) { value.VmonRawV = 0; value.DividerRawV = 0; } return value; };
                var error = ExpectFailure(() => f.Run());
                Require(Contains<TimeoutException>(error) && f.Backend.OpticalHighCount == 0 && f.Backend.HvHighCount == 1, "stalled ramp fails without retry");
                AssertFinalOff(f.Backend);
            });
            test("AO write acknowledgement failure cannot advance to optical", () =>
            {
                var f = new Fixture(); f.Settings.TargetKv = 1; f.Backend.FailPositiveAo = true;
                ExpectFailure(() => f.Run()); Require(f.Backend.OpticalHighCount == 0, "failed AO cannot fire"); AssertFinalOff(f.Backend);
            });
            test("read failure preserves optical LOW and never repeats HV sequence", () =>
            {
                var f = new Fixture(); f.Backend.FailRead = true; f.Backend.ReadDelayMs = 600;
                ExpectFailure(() => f.Run()); AssertSingleOptical(f.Backend, 1000); AssertFinalOff(f.Backend);
                Require(f.Backend.ReadStartedWhenOff && f.Backend.HvHighCount == 1, "read failure cannot extend light or retry");
            });
            test("post-capture residual timeout cannot emit another optical trigger", () =>
            {
                var f = new Fixture(); f.Settings.DischargeTimeoutSeconds = .04;
                f.Backend.FeedbackRule = (b, value) => { if (b.Captured) { value.DividerRawV = .02; value.VmonRawV = .04; } return value; };
                var error = ExpectFailure(() => f.Run());
                var partial = (error as FactoryRunException)?.PartialResult;
                Require(partial != null && !partial.Completed && !partial.ResidualSafe && partial.Capture != null, "capture retained but not marked safe or complete");
                AssertSingleOptical(f.Backend, 1000); AssertFinalOff(f.Backend);
            });
            test("cleanup failure propagates and Dispose is still attempted", () =>
            {
                var f = new Fixture(); f.Backend.FailShutdown = true; f.Backend.FailDispose = true;
                var error = ExpectFailure(() => f.Run());
                Require(f.Backend.ShutdownCount >= 2 && f.Backend.DisposeCount == 1, "cleanup attempts cannot stop at first failure");
                Require(error.ToString().Contains("fake shutdown") && error.ToString().Contains("fake dispose"), "both cleanup failures retained");
                AssertNoActivation(f.Backend);
            });
            test("throwing report cannot skip Shutdown or Dispose", () =>
            {
                var f = new Fixture(); bool failLogging = false;
                Action<string, string> report = (stage, detail) => { if (stage == "STEP_HV_ON_PASSED") failLogging = true; if (failLogging) throw new IOException("fake report failure"); };
                var error = ExpectFailure(() => f.Run(report));
                Require(error.ToString().Contains("fake report failure") && f.Backend.ShutdownCount >= 2 && f.Backend.DisposeCount == 1, "logging failure still cleans all resources");
                Require(f.Backend.OpticalHighCount == 0, "logging failure cannot continue stages"); AssertFinalOff(f.Backend);
            });
            test("query crosses deadline by 1 ms without negative or infinite pause", () =>
            {
                var f = new Fixture(); f.Backend.HvZeroFeedbackLatencyMs = 51;
                f.Backend.ArmedEmergencyLatencyMs = 201; f.Backend.OpticalEmergencyLatencyMs = 1501;
                Require(f.Run().Completed, "deadline-crossing queries must finish safely");
                Require(f.Clock.NegativePauseCount == 0 && f.Backend.HvLatencyUsed && f.Backend.ArmLatencyUsed && f.Backend.OpticalLatencyUsed, "all three deadline boundaries exercised");
                AssertSingleOptical(f.Backend, 1501); AssertFinalOff(f.Backend);
            });
            test("manual calibration has no HV/AO increment/optical and keeps the requested time segment", () =>
            {
                var f = new Fixture();
                var result = f.Run(null, FactoryRunKind.Calibration);
                Require(result.Completed && result.Settings.Input.CaptureDurationMs == 60 && f.Settings.Input.CaptureDurationMs == 60,
                    "calibration neither extends the <=60 ms segment nor modifies the request");
                Require(f.Backend.FreeRun && f.Backend.OpticalHighCount == 0 && f.Backend.HvHighCount == 0 && f.Backend.PositiveAoWrites == 0, "calibration must be read-only");
                Require(result.Calibration != null && !result.Calibration.Valid, "fake flat segment cannot manufacture a valid coefficient");
                AssertFinalOff(f.Backend);
            });

            test("requested target above AO envelope rejected before Initialize", () =>
            {
                var f = new Fixture(); f.Settings.TargetKv = 28;
                ExpectFailure(() => f.Run()); Require(f.Backend.InitializeCount == 0, "invalid AO envelope cannot initialize");
            });
            test("14 bit 250 MS/s and invalid timebase rejected before Initialize", () =>
            {
                var f = new Fixture(); f.Settings.Input.RequestedPicoRateHz = 250000000;
                ExpectFailure(() => f.Run()); Require(f.Backend.InitializeCount == 0, "14 bit rate must not be silently reduced");
                var g = new Fixture(); g.Settings.Input.RequestedPicoRateHz = 123456;
                ExpectFailure(() => g.Run()); Require(g.Backend.InitializeCount == 0, "nonrepresentable timebase rejected");
            });
            test("real hardware missing identity or feedback acknowledgement rejected", () =>
            {
                var f = new Fixture(); f.Settings.Simulation = false; f.Settings.Input.ArtSerial = ""; f.Settings.FeedbackWiringConfirmed = true;
                ExpectFailure(() => f.Run()); Require(f.Backend.InitializeCount == 0, "missing real identity");
                var g = new Fixture(); g.Settings.Simulation = false; g.Settings.FeedbackWiringConfirmed = false;
                ExpectFailure(() => g.Run()); Require(g.Backend.InitializeCount == 0, "missing per-run feedback confirmation");
                var h = new Fixture(); h.Settings.Simulation = false; h.Settings.FeedbackWiringConfirmed = true; h.Settings.CalibrationSourceConfirmed = false;
                ExpectFailure(() => h.Run(null, FactoryRunKind.Calibration)); Require(h.Backend.InitializeCount == 0, "manual calibration source confirmation required");
            });
            test("native static wiring contract rejects zero/wrong-format serial and alternate ports", () =>
            {
                var s = Settings(); NativeFactoryBackend.ValidatePhysicalContract(s.Input); // Pure managed validator only.
                var bad = Settings(); bad.Input.ArtSerial = "0"; ExpectFailure(() => NativeFactoryBackend.ValidatePhysicalContract(bad.Input));
                bad = Settings(); bad.Input.ArtSerial = "wrong-serial"; ExpectFailure(() => NativeFactoryBackend.ValidatePhysicalContract(bad.Input));
                bad = Settings(); bad.Input.ArtDevice = "Dev1,Dev2"; ExpectFailure(() => NativeFactoryBackend.ValidatePhysicalContract(bad.Input));
                bad = Settings(); bad.Input.ArtTrigger = "/Dev1/PFI1"; ExpectFailure(() => NativeFactoryBackend.ValidatePhysicalContract(bad.Input));
                bad = Settings(); bad.Input.FallingTrigger = true; ExpectFailure(() => NativeFactoryBackend.ValidatePhysicalContract(bad.Input));
                bad = Settings(); bad.Input.ArtTerminal = 10078; ExpectFailure(() => NativeFactoryBackend.ValidatePhysicalContract(bad.Input));
                bad = Settings(); bad.Input.ExternalTriggerVolts = 0; ExpectFailure(() => NativeFactoryBackend.ValidatePhysicalContract(bad.Input));
            });
            test("EXT threshold conversion independent of channel A scale", () =>
            {
                Require(PicoInputCapture.ConvertExternalThreshold(1.5) == 9830, "1.5 V EXT conversion");
                Require(PicoInputCapture.ConvertExternalThreshold(4) == 26214, "4 V EXT conversion");
                ExpectFailure(() => PicoInputCapture.ConvertExternalThreshold(5));
                ExpectFailure(() => PicoInputCapture.ConvertExternalThreshold(Double.NaN));
            });
            test("short/false-rate/wrong-serial capture metadata cannot pass", () =>
            {
                var f = new Fixture(); f.Run(); var c = f.Backend.LastCapture;
                c.PdAdc = new short[c.Settings.RequestedPicoCount - 1]; ExpectFailure(c.ValidateCapture);
                f = new Fixture(); f.Run(); c = f.Backend.LastCapture; c.PicoRateHz *= .5; ExpectFailure(c.ValidateCapture);
                f = new Fixture(); f.Run(); c = f.Backend.LastCapture; c.PicoSerial = "OTHER-CARD"; ExpectFailure(c.ValidateCapture);
            });
            test("cancel before initialization invokes no enabling command", () =>
            {
                var f = new Fixture(); f.Cancellation.Cancel(); ExpectFailure(() => f.Run());
                Require(f.Backend.InitializeCount == 0 && f.Backend.DisposeCount == 1, "cancelled start still releases backend object");
                AssertNoActivation(f.Backend);
            });

            writer.WriteLine("Factory runner/contract passed " + cases + " managed fake cases; HardwareAccessed=False; not physical acceptance.");
        }

        private static FactorySettings Settings()
        {
            return new FactorySettings { Simulation = true, TargetKv = .5,
                Input = new BenchSettings { ArtDevice = "Dev1", ArtSerial = "123456789", PicoSerial = "FAKE/0001",
                    ArtTerminal = 10083, ArtTrigger = "/Dev1/PFI0", ResolutionBits = 14,
                    RequestedPicoRateHz = 125000, CaptureDurationMs = 60, RangeIndex = 5,
                    HvDelayMs = 50, ArmDelayMs = 200, TriggerTimeoutMs = 3000,
                    LoopDelayMs = 1, AoStepV = .01, PdCalibrationPcPerV = 2100, DividerVPerV = 11000 } };
        }
        internal sealed class Fixture
        {
            internal readonly FactorySettings Settings = FactoryTests.Settings();
            internal readonly FakeClock Clock = new FakeClock();
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal readonly FakeBackend Backend;
            internal Fixture() { Backend = new FakeBackend(Clock); }
            internal FactoryResult Run(Action<string, string> report = null, FactoryRunKind kind = FactoryRunKind.Pressure)
            { return FactoryRunner.Run(Settings, kind, Backend, Clock, report ?? ((stage, detail) => { }), Cancellation.Token); }
        }
        internal sealed class FakeClock : IFactoryClock
        {
            private long milliseconds;
            internal Action AfterPause;
            internal int NegativePauseCount;
            public long ElapsedMilliseconds { get { return milliseconds; } }
            internal void Advance(int value) { if (value < 0) throw new ArgumentOutOfRangeException("value"); milliseconds += value; }
            public void Pause(int value, CancellationToken cancellation)
            {
                if (value < 0) { NegativePauseCount++; throw new InvalidOperationException("fake detected negative/infinite wait"); }
                cancellation.ThrowIfCancellationRequested(); Advance(value);
                if (AfterPause != null) AfterPause(); cancellation.ThrowIfCancellationRequested();
            }
        }
        internal sealed class FakeEvent
        {
            internal string Name;
            internal long At;
            internal FakeEvent(string name, long at) { Name = name; At = at; }
        }
        internal sealed class FakeBackend : IFactoryBackend, IFactoryFaultMonitor, IFactoryOpticalRecovery
        {
            internal readonly List<FakeEvent> Events = new List<FakeEvent>();
            private readonly FakeClock clock;
            private FactorySettings settings;
            private long captureAt = Int64.MaxValue;
            private double chargedV;
            private bool discharged;
            internal bool Hv, Optical, Armed, FreeRun, Captured, ReadStartedWhenOff, OffShutdownBeforeArm;
            internal double Ao, MaximumAo;
            internal int InitializeCount, ShutdownCount, DisposeCount, ArmCount, ReadCount, PositiveAoWrites, HvHighCount, OpticalHighCount;
            internal long OpticalAt;
            internal bool FailInitialize, FailArm, NeverComplete, PrematureComplete, FailPositiveAo, FailRead, FailShutdown, FailDispose;
            internal int TriggerCompletesOnPulse=1, RetryCount;
            internal bool OnlyArtCompletes, FailRetry, FailStop;
            private bool retryPrepared, retryStoppedEligible;
            internal int ReadDelayMs, HvZeroFeedbackLatencyMs, ArmedEmergencyLatencyMs, OpticalEmergencyLatencyMs, ArmLatencyMs;
            internal bool HvLatencyUsed, ArmLatencyUsed, OpticalLatencyUsed;
            internal Func<FakeBackend, bool> EmergencyRule;
            internal Func<FakeBackend, FactoryFeedback, FactoryFeedback> FeedbackRule;
            internal BenchCapture LastCapture;
            internal FakeBackend(FakeClock value) { clock = value; }
            private void Event(string name) { Events.Add(new FakeEvent(name, clock.ElapsedMilliseconds)); }
            public void Initialize(FactorySettings requested, bool calibration)
            {
                InitializeCount++; Event("INIT"); settings = requested.Clone();
                if (FailInitialize) throw new InvalidOperationException("fake exact-card identity initialization failure");
            }
            public FactoryFeedback ReadFeedback()
            {
                if (Armed) throw new InvalidOperationException("fake AI4 finite ownership: monitoring during capture is prohibited");
                if (Hv && Ao == 0 && !HvLatencyUsed && HvZeroFeedbackLatencyMs > 0)
                { HvLatencyUsed = true; clock.Advance(HvZeroFeedbackLatencyMs); }
                double volts = Hv ? Ao * settings.PressureVPerV : discharged ? 0 : chargedV;
                var f = new FactoryFeedback { TimestampUtc = DateTime.UtcNow, EmergencyReleased = ReadEmergencyReleased(), AoV = Ao,
                    VmonRawV = volts / (settings.VmonFullScaleKv * 1000) * settings.FeedbackFullScaleV,
                    DividerRawV = volts / settings.Input.DividerVPerV, ImonRawV = 0 };
                return FeedbackRule == null ? f : FeedbackRule(this, f);
            }
            public bool ReadEmergencyReleased()
            {
                if (Optical && !OpticalLatencyUsed && OpticalEmergencyLatencyMs > 0)
                { OpticalLatencyUsed = true; clock.Advance(OpticalEmergencyLatencyMs); }
                else if (Armed && OpticalHighCount == 0 && !ArmLatencyUsed && ArmedEmergencyLatencyMs > 0)
                { ArmLatencyUsed = true; clock.Advance(ArmedEmergencyLatencyMs); }
                return EmergencyRule == null || EmergencyRule(this);
            }
            public void WriteAo(double volts)
            {
                if (volts == 0) Event("AO_ZERO");
                if (volts > 0)
                {
                    PositiveAoWrites++; Event("AO_POSITIVE");
                    if (!Hv || Optical || volts > settings.MaximumAoV) throw new InvalidOperationException("fake positive AO safety contract");
                    if (FailPositiveAo) throw new InvalidOperationException("fake AO acknowledgement failure");
                }
                Ao = volts; MaximumAo = Math.Max(MaximumAo, volts);
                if (Hv) chargedV = volts * settings.PressureVPerV;
            }
            public void SetHv(bool on)
            {
                if (on) { HvHighCount++; Event("HV_HIGH"); if (Armed || Optical || !ReadEmergencyReleased()) throw new InvalidOperationException("fake HV safety contract"); }
                else Event("HV_LOW"); Hv = on;
            }
            public void SetOptical(bool on)
            {
                if (on)
                {
                    if (!Armed || Hv || Ao != 0 || FreeRun || (OpticalHighCount != 0 && !retryPrepared)) throw new InvalidOperationException("fake optical safety contract");
                    retryPrepared=false;
                    OpticalHighCount++; OpticalAt = clock.ElapsedMilliseconds; captureAt = OpticalAt; discharged = true;
                    Event("OPTICAL_HIGH");
                }
                else Event("OPTICAL_LOW"); Optical = on;
            }
            public void Arm(bool freeRun)
            {
                ArmCount++; Event(freeRun ? "ARM_FREE" : "ARM_EXT");
                OffShutdownBeforeArm = !Hv && !Optical && Ao == 0 && ShutdownCount > 0;
                if (!OffShutdownBeforeArm) throw new InvalidOperationException("fake arm requires safe outputs");
                if (FailArm) throw new InvalidOperationException("fake arm failure");
                Armed = true; FreeRun = freeRun; if (freeRun) captureAt = clock.ElapsedMilliseconds;
            }
            private bool Complete { get { return Armed && !NeverComplete && (PrematureComplete || captureAt != Int64.MaxValue && (FreeRun || OpticalHighCount>=TriggerCompletesOnPulse) && clock.ElapsedMilliseconds - captureAt >= settings.Input.CaptureDurationMs); } }
            internal bool ArmedBeforeShutdown;
            public void ArmBeforeShutdown()
            {
                ArmCount++; Event("ARM_EXT");
                if (!Hv || Optical || Armed) throw new InvalidOperationException("fake pre-off arm state");
                if (FailArm) throw new InvalidOperationException("fake arm failure");
                clock.Advance(ArmLatencyMs);
                Armed=true; FreeRun=false; ArmedBeforeShutdown=true;
                Event("ARM_READY");
            }
            public bool ArtComplete { get { return Complete || (OnlyArtCompletes && Armed && OpticalHighCount>0); } }
            public bool PicoComplete { get { return !OnlyArtCompletes && Complete; } }
            public BenchCapture ReadCapture()
            {
                ReadCount++; Event("READ"); ReadStartedWhenOff = !Hv && !Optical && Ao == 0;
                if (!ReadStartedWhenOff || !Complete) throw new InvalidOperationException("fake complete-read contract");
                clock.Advance(ReadDelayMs);
                if (FailRead) throw new InvalidOperationException("fake completed-data read failure");
                Captured = true;
                LastCapture = new BenchCapture { Settings = settings.Input, Simulation = true, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO",
                    CapturedUtc = DateTime.UtcNow, Runtime = "managed-fake-only; no native API or hardware access",
                    PicoRateHz = settings.Input.RequestedPicoRateHz, ArtRateHz = BenchSettings.ArtRate,
                    PicoFullScaleV = BenchSettings.Ranges[settings.Input.RangeIndex], MaximumAdc = 32767,
                    HardwareBits = PicoCaptureConfiguration.HardwareBits(settings.Input.ResolutionBits, settings.Input.RangeIndex),
                    VoltageRaw = new double[settings.Input.ExpectedArtCount], PdAdc = new short[settings.Input.RequestedPicoCount] };
                return LastCapture;
            }
            public void StopCapture()
            {
                Event("STOP_CAPTURE");
                bool eligible=settings.UseOpticalTimeoutRetry && RetryCount==0 && OpticalHighCount==1 &&
                    !Hv && !Optical && Ao==0 && Armed && !ArtComplete && !PicoComplete;
                retryStoppedEligible=false;
                if(FailStop)throw new InvalidOperationException("fake stop failed");
                Armed=false;retryStoppedEligible=eligible;
            }
            public void PrepareOpticalRetry()
            {
                Event("RETRY_PREPARE");
                if(!settings.UseOpticalTimeoutRetry || RetryCount!=0 || OpticalHighCount!=1 || Hv || Optical || Ao!=0 || Armed || !retryStoppedEligible)
                    throw new InvalidOperationException("fake retry safety contract");
                retryStoppedEligible=false;++RetryCount;if(FailRetry)throw new InvalidOperationException("fake retry failed");
                captureAt=Int64.MaxValue;Arm(false);retryPrepared=true;
            }
            public void PrepareFaultMonitoring() { Event("FAULT_MONITOR"); StopCapture(); }
            public FactoryFeedback ReadFaultFeedback() { Event("FAULT_READ"); return ReadFeedback(); }
            public void Shutdown()
            {
                ShutdownCount++; Event("SHUTDOWN"); Hv = false; Optical = false; Ao = 0;
                if (FailShutdown) throw new InvalidOperationException("fake shutdown failure");
            }
            public void Dispose()
            {
                DisposeCount++; Event("DISPOSE"); Hv = false; Optical = false; Ao = 0; Armed = false;
                if (FailDispose) throw new InvalidOperationException("fake dispose failure");
            }
        }
        private static void AssertOrder(FakeBackend backend, params string[] ordered)
        {
            int last = -1;
            foreach (string name in ordered)
            {
                int found = backend.Events.FindIndex(last + 1, e => e.Name == name);
                Require(found > last, "missing/out-of-order event " + name); last = found;
            }
        }
        private static void AssertSingleOptical(FakeBackend backend, long requestedDuration)
        {
            Require(backend.OpticalHighCount == 1, "only one optical HIGH may be emitted");
            int high = backend.Events.FindIndex(e => e.Name == "OPTICAL_HIGH");
            int low = backend.Events.FindIndex(high + 1, e => e.Name == "OPTICAL_LOW");
            Require(high >= 0 && low > high && backend.Events[low].At - backend.Events[high].At == requestedDuration, "optical hold duration in fake clock");
        }
        private static void AssertEarlyOptical(FakeBackend backend)
        {
            Require(backend.OpticalHighCount == 1, "exactly one optical HIGH before abort");
            int high = backend.Events.FindIndex(e => e.Name == "OPTICAL_HIGH");
            int low = backend.Events.FindIndex(high + 1, e => e.Name == "OPTICAL_LOW");
            Require(low > high && backend.Events[low].At - backend.Events[high].At < 1000, "abort must lower optical before normal end");
        }
        private static void AssertNoActivation(FakeBackend backend)
        { Require(backend.HvHighCount == 0 && backend.OpticalHighCount == 0 && backend.PositiveAoWrites == 0, "invalid initial state cannot activate any positive output"); }
        private static void AssertFinalOff(FakeBackend backend)
        { Require(!backend.Hv && !backend.Optical && backend.Ao == 0 && backend.DisposeCount == 1, "all fake outputs off and disposal attempted"); }
        private static Exception ExpectFailure(Action run)
        {
            try { run(); } catch (Exception ex) { return ex; }
            throw new InvalidOperationException("Expected rejection, but the run succeeded.");
        }
        private static bool Contains<T>(Exception error) where T : Exception
        {
            if (error is T) return true;
            var aggregate = error as AggregateException;
            if (aggregate != null) return aggregate.InnerExceptions.Any(Contains<T>);
            return error.InnerException != null && Contains<T>(error.InnerException);
        }
        private static void Require(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException("Assertion failed: " + detail); }
    }
}
