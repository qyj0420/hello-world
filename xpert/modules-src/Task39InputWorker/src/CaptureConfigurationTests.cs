using System;
using System.IO;

namespace Xiongxin.ZeroBench
{
    internal static class CaptureConfigurationTests
    {
        internal static string Run()
        {
            var defaults = new BenchSettings(); defaults.Validate();
            Require(defaults.ExpectedArtCount == 4800 && defaults.RequestedPicoCount == 7500000 && defaults.ResolutionBits == 14, "compatible defaults");
            Require(PicoCaptureConfiguration.TimebaseFor(14, 125e6) == 3, "14bit125M");
            Require(PicoCaptureConfiguration.TimebaseFor(15, 125e6) == 3, "15bit125M");
            Require(PicoCaptureConfiguration.TimebaseFor(16, 62.5e6) == 4, "16bit62.5M");
            Require(PicoCaptureConfiguration.TimebaseFor(8, 1e9) == 0, "8bit1G");
            Require(PicoCaptureConfiguration.TimebaseFor(12, 500e6) == 1, "12bit500M");
            Require(PicoCaptureConfiguration.TimebaseFor(12, 125e6) == 3, "12bit125M");
            Require(PicoCaptureConfiguration.TimebaseFor(12, 31.25e6) == 5, "12bit slow timebase");
            Require(PicoCaptureConfiguration.TimebaseFor(14, 25e6) == 7, "14bit25M");
            Require(PicoCaptureConfiguration.HardwareBits(8, 0) == 8 && PicoCaptureConfiguration.HardwareBits(12, 0) == 11 &&
                PicoCaptureConfiguration.HardwareBits(12, 1) == 12 && PicoCaptureConfiguration.HardwareBits(15, 0) == 13 &&
                PicoCaptureConfiguration.HardwareBits(16, 1) == 15, "5000D low-range hardware resolution footnote");
            Rejected(() => new BenchSettings { RequestedPicoRateHz = 250e6 }.Validate(), "14bit250M unsupported");
            Rejected(() => new BenchSettings { ResolutionBits = 16 }.Validate(), "16bit125M unsupported");
            Rejected(() => new BenchSettings { ResolutionBits = 13 }.Validate(), "unsupported resolution mode");
            Rejected(() => new BenchSettings { RequestedPicoRateHz = 50e6 }.Validate(), "unrepresentable timebase rejected");
            Rejected(() => new BenchSettings { RequestedPicoRateHz = 79e3 }.Validate(), "cannot upsample under80k as detection");
            Rejected(() => new BenchSettings { CaptureDurationMs = Double.NaN }.Validate(), "duration NaN");
            Rejected(() => new BenchSettings { CaptureDurationMs = 1001 }.Validate(), "duration bound");
            Rejected(() => new BenchSettings { CaptureDurationMs = 1.001 }.Validate(), "fractional ART final sample rejected 1.001ms");
            Rejected(() => new BenchSettings { CaptureDurationMs = 1.01 }.Validate(), "fractional ART final sample rejected 1.01ms");
            var alignedFraction = new BenchSettings { CaptureDurationMs = 1.025 };
            alignedFraction.Validate();
            Require(alignedFraction.ExpectedArtCount == 82 && alignedFraction.RequestedPicoCount == 128125, "aligned fractional duration 1.025ms preserved");
            Rejected(() => new BenchSettings { CaptureDurationMs = 1.025, RequestedPicoRateHz = 62.5e6 }.Validate(), "integer ART but fractional Pico final sample rejected");
            Rejected(() => new BenchSettings { CaptureDurationMs = 401 }.Validate(), "sample cap before allocation");
            Rejected(() => new BenchSettings { PicoChannelAEnabled = false }.Validate(), "no substitute B for disabled A");
            Rejected(() => new BenchSettings { PicoCoupling = 2 }.Validate(), "coupling enum");
            Rejected(() => new BenchSettings { PicoAnalogOffsetVolts = .1 }.Validate(), "unimplemented offset rejected");
            Rejected(() => new BenchSettings { CaptureDurationMs = 100, TriggerTimeoutMs = 100 }.Validate(), "wait covers full segment");
            new BenchSettings { CaptureDurationMs = 1, TransientExclusionMs = .5 }.Validate();
            Rejected(() => new BenchSettings { CaptureDurationMs = 1, TransientExclusionMs = 1 }.Validate(), "exclusion must not remove entire capture");
            Rejected(() => new BenchSettings { CaptureDurationMs = 1, TransientExclusionMs = 2 }.Validate(), "exclusion must not exceed capture");
            PicoCaptureConfiguration.ValidateSdkTimebase(125e6, 8, 1000000, 125000);
            InvalidOperation(() => PicoCaptureConfiguration.ValidateSdkTimebase(125e6, 16, 1000000, 125000), "SDK lower rate rejected");
            InvalidOperation(() => PicoCaptureConfiguration.ValidateSdkTimebase(125e6, Double.NaN, 1000000, 125000), "SDK invalid interval rejected");
            InvalidOperation(() => PicoCaptureConfiguration.ValidateSdkTimebase(125e6, 8, 1000, 125000), "SDK insufficient memory rejected");
            var settings = new BenchSettings { CaptureDurationMs = 10, RequestedPicoRateHz = 62.5e6, ResolutionBits = 16, RangeIndex = 0,
                PicoCoupling = 0, PicoBandwidth20MHz = true, DividerVPerV = 12000, PdCalibrationPcPerV = 2300 };
            settings.Validate();
            Require(settings.ExpectedArtCount == 800 && settings.RequestedPicoCount == 625000, "dynamic counts");
            var capture = new BenchCapture { Settings = settings, VoltageRaw = new double[800], PdAdc = new short[625000],
                ArtRateHz = BenchSettings.ArtRate, PicoRateHz = settings.RequestedPicoRateHz, MaximumAdc = 32767,
                PicoFullScaleV = .01, HardwareBits = 14, Simulation = true, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO",
                Runtime = "configuration offline test", CapturedUtc = DateTime.UtcNow };
            capture.PdAdc[123] = 200; capture.VoltageRaw[1] = .25;
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification", "config_" + Guid.NewGuid().ToString("N"));
            capture.Save(directory);
            var restored = BenchCapture.Load(directory);
            Require(restored.PdAdc[123] == 200 && restored.VoltageRaw[1] == .25 && restored.Settings.ResolutionBits == 16 &&
                restored.Settings.CaptureDurationMs == 10 && restored.Settings.PicoBandwidth20MHz && restored.Settings.PicoCoupling == 0 &&
                restored.Settings.DividerVPerV == 12000 && restored.Settings.PdCalibrationPcPerV == 2300, "dynamic v1 raw and XML roundtrip");
            restored.PicoRateHz = 125e6;
            InvalidData(() => restored.ValidateCapture(), "capture cannot claim a requested instead of actual rate");
            restored.PicoRateHz = 62.5e6; restored.HardwareBits = 16;
            InvalidData(() => restored.ValidateCapture(), "range bits remain independent from mode");
            restored.HardwareBits = 14; restored.PdAdc = new short[625001];
            InvalidData(() => restored.ValidateCapture(), "wrong dynamic count");
            return "CAPTURE_CONFIGURATION_TESTS=PASS: defaults; 8/12/14/15/16 bit timebases; true range resolution; rate/memory/duration bounds; channel validation; SDK response rejection; dynamic settings/raw roundtrip; no hardware accessed.";
        }
        private static void Require(bool value, string name) { if (!value) throw new Exception("Configuration test failed: " + name); }
        private static void Rejected(Action action, string name) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Require(rejected, name); }
        private static void InvalidOperation(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Require(rejected, name); }
        private static void InvalidData(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Require(rejected, name); }
    }
}
