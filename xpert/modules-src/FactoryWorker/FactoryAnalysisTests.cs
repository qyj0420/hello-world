using System;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryAnalysisTests
    {
        internal static string Run()
        {
            int checks = 0;
            BenchCapture pulses = PulseCapture(100, 100, .10, 0, 0, false);
            CalibrationResult calibration = FactoryAnalysis.Calibrate(pulses, 210, 100, 8);
            Require(calibration.Valid, "known charge periodic pulse calibration: " + calibration.Detail);
            Near(calibration.CoefficientPcPerV, 2100, 1, "known charge divided by native voltage");
            Require(calibration.AveragePulseV.Length > 10 && calibration.PulseCount >= 8, "complete aligned N pulse window");
            pulses.Settings.PdCalibrationPcPerV = 999999;
            Near(FactoryAnalysis.Calibrate(pulses, 210, 100, 8).CoefficientPcPerV, calibration.CoefficientPcPerV, 1e-12,
                "previous coefficient cannot manufacture known charge"); checks += 4;

            BenchCapture biased = PulseCapture(100, 100, .10, .10, .0001, false);
            CalibrationResult withBias = FactoryAnalysis.Calibrate(biased, 210, 100, 8);
            Require(withBias.Valid, "DC offset and small noise retain valid physical pulse: " + withBias.Detail);
            Near(withBias.BaselineV, .10, .0001, "robust DC baseline");
            Near(withBias.CoefficientPcPerV, 2100, 10, "baseline corrected calibration"); checks += 3;

            CalibrationResult fastPulses = FactoryAnalysis.Calibrate(PulseCapture(60, 400, -.1, 0, 0, false), 210, 400, 16);
            Require(fastPulses.Valid && fastPulses.PulseCount >= 16, "400 Hz negative pulse source remains period-isolated: " + fastPulses.Detail);
            Near(fastPulses.CoefficientPcPerV, 2100, 20, "native absolute peak accepts negative polarity; low-rate fixture has sub-sample peak error");
            BenchCapture invalid = Empty(60); invalid.VoltageRaw[1] = Double.NaN;
            Require(!FactoryAnalysis.Calibrate(invalid, 210, 100, 3).Valid, "nonfinite measured voltage metadata rejected");
            invalid = Empty(60); invalid.PdAdc = new short[10];
            Require(!FactoryAnalysis.Calibrate(invalid, 210, 100, 3).Valid, "incomplete raw buffer rejected without padding"); checks += 4;

            Require(!FactoryAnalysis.Calibrate(Empty(100), 210, 100, 8).Valid, "flat background is not a pulse");
            Require(!FactoryAnalysis.Calibrate(PulseCapture(20, 100, .1, 0, 0, false), 210, 100, 8).Valid, "too few pulses");
            BenchCapture clipped = PulseCapture(100, 100, .5, 0, 0, false);
            Require(!FactoryAnalysis.Calibrate(clipped, 210, 100, 8).Valid, "ADC rail clipping rejects calibration");
            pulses.Overflow = true;
            Require(!FactoryAnalysis.Calibrate(pulses, 210, 100, 8).Valid, "hardware overflow rejects calibration");
            pulses.Overflow = false;
            Require(!FactoryAnalysis.Calibrate(pulses, Double.NaN, 100, 8).Valid, "nonfinite known charge");
            Require(!FactoryAnalysis.Calibrate(pulses, 210, 400, 8).Valid, "wrong expected pulse frequency"); checks += 6;

            BenchCapture jitter = PulseCapture(100, 100, .1, 0, 0, false);
            MovePulse(jitter, .045, .047);
            Require(!FactoryAnalysis.Calibrate(jitter, 210, 100, 8).Valid, "period slip is not hidden by N selection");
            BenchCapture amplitudes = PulseCapture(100, 100, .1, 0, 0, true);
            Require(!FactoryAnalysis.Calibrate(amplitudes, 210, 100, 8).Valid, "inconsistent pulse amplitudes");
            BenchCapture ringing = PulseCapture(100, 100, .1, 0, 0, false);
            AddPulse(ringing, .02595, .06);
            Require(!FactoryAnalysis.Calibrate(ringing, 210, 100, 8).Valid, "incomplete ringing window rejects averaging");
            BenchCapture noise = Empty(100); var random = new Random(124);
            for (int i = 0; i < noise.PdAdc.Length; i++) noise.PdAdc[i] = (short)(random.Next(-500, 501));
            Require(!FactoryAnalysis.Calibrate(noise, 210, 100, 8).Valid, "noise cannot produce calibration coefficient"); checks += 4;

            FactoryResult oscillation = Oscillation(400, 25, .05, .001);
            FactoryMetrics metrics = FactoryAnalysis.Analyze(oscillation);
            Require(metrics.FrequencyValid, "damped sine frequency: " + metrics.Detail);
            Near(metrics.FrequencyHz, 400, 1, "measured oscillation frequency");
            Near(metrics.DampingAlphaPerSecond, 25, 1.5, "log decrement amplitude decay");
            double omega = 2 * Math.PI * 400;
            Near(metrics.CapacitanceNf, 1e9 / (.7 * (omega * omega + 25 * 25) - 22 * (50 - 22 / .7)), .1,
                "100 coil R+L and capacitor parallel conductance capacitance");
            Near(metrics.EstimatedLossTangent, 50 / omega - 22 / (omega * .7), .0012, "coil resistance removed from estimate");
            Require(metrics.Detected.OutputRateHz == 80000 && metrics.Detected.OutputCount == 4800, "80k detected common segment"); checks += 6;

            oscillation.Settings.ReferenceDcKv = 12;
            oscillation.Settings.ReferenceOscillationPeakKv = metrics.RawDividerPeakV * 11;
            for (int i = 0; i < 5; i++) oscillation.Ramp.Add(new FactoryFeedback { AoV = 2, TimestampUtc = DateTime.UtcNow });
            metrics = FactoryAnalysis.Analyze(oscillation);
            Require(metrics.PressureRatioValid && metrics.DividerRatioValid, "independent correct references verify candidate ratios");
            Near(metrics.MeasuredPressureVPerV, 6000, 1e-9, "pressure uses independent DC reference");
            Near(metrics.MeasuredDividerVPerV, 11000, 1e-9, "divider uses independent full native peak reference");
            oscillation.Settings.ReferenceDcKv = 18;
            oscillation.Settings.ReferenceOscillationPeakKv *= 2;
            metrics = FactoryAnalysis.Analyze(oscillation);
            Require(!metrics.PressureRatioValid && !metrics.DividerRatioValid, "bad independent ratios cannot self-certify");
            oscillation.Settings.ReferenceDcKv = 12;
            oscillation.Ramp[3].AoV = .5;
            Require(!FactoryAnalysis.Analyze(oscillation).PressureRatioValid, "unstable command ramp is not reference calibration"); checks += 5;

            foreach (double spike in new[] { 10.0, -10.0, 9.95, -9.95 })
            {
                FactoryResult saturatedInitialPeak = Oscillation(400, 25, .05, .001);
                saturatedInitialPeak.Capture.VoltageRaw[0] = spike; // Within the excluded first 0.5 ms, not the fitted tail.
                saturatedInitialPeak.Settings.ReferenceOscillationPeakKv = Math.Abs(spike) * 11;
                FactoryMetrics clippedVoltageMetrics = FactoryAnalysis.Analyze(saturatedInitialPeak);
                Require(clippedVoltageMetrics.FrequencyValid, "clipped initial overshoot fixture still has a verified 400 Hz unclipped tail");
                Near(clippedVoltageMetrics.RawDividerPeakV, Math.Abs(spike), 1e-12, "full raw peak must retain excluded initial clipping");
                Require(!clippedVoltageMetrics.DividerRatioValid && Double.IsNaN(clippedVoltageMetrics.MeasuredDividerVPerV),
                    "native AI4 saturation must not self-validate a candidate divider ratio with a matching external input");
                Require(clippedVoltageMetrics.Detail.Contains("真实峰值未知"), "ratio saturation rejection remains explicit rather than hidden by filtering"); checks += 4;
            }
            FactoryResult closeToRail = Oscillation(400, 25, .05, .001);
            closeToRail.Capture.VoltageRaw[0] = 9.949;
            closeToRail.Settings.ReferenceOscillationPeakKv = 9.949 * 11;
            Require(FactoryAnalysis.Analyze(closeToRail).DividerRatioValid, "strictly in-range full raw peak retains independent candidate ratio evaluation"); checks++;

            FactoryResult flat = new FactoryResult { Settings = new FactorySettings(), Capture = Empty(60) };
            flat.Capture.Settings.OscillationCircuitConnected = true;
            flat.Settings.ReferenceOscillationPeakKv = 10;
            metrics = FactoryAnalysis.Analyze(flat);
            Require(!metrics.FrequencyValid && !metrics.DividerRatioValid && Double.IsNaN(metrics.CapacitanceNf), "no signal does not yield frequency capacitance or ratio");
            Require(metrics.RecommendedRangeIndex == 0, "flat raw input recommends minimum range without an automatic loop");
            flat.Capture.PdAdc[1] = flat.Capture.PdAdc[2] = 32767;
            metrics = FactoryAnalysis.Analyze(flat);
            Require(metrics.PdClipped && Double.IsNaN(metrics.MaxPdPc), "clipped native peak is not true maximum PD");
            Require(metrics.RecommendedRangeIndex == flat.Capture.Settings.RangeIndex + 1, "clipping recommends provisionally one larger range");
            flat.Capture.Settings.RangeIndex = 10; flat.Capture.PicoFullScaleV = 20;
            metrics = FactoryAnalysis.Analyze(flat);
            Require(metrics.RecommendedRangeIndex == 10 && metrics.RangeDetail.Contains("老化或损坏"), "highest rail remains explicit risk and bounded recommendation"); checks += 5;

            FactoryResult offset = new FactoryResult { Settings = new FactorySettings(), Capture = Empty(60) };
            for (int i = 0; i < offset.Capture.PdAdc.Length; i++) offset.Capture.PdAdc[i] = 20000;
            metrics = FactoryAnalysis.Analyze(offset);
            Require(metrics.RecommendedRangeIndex >= offset.Capture.Settings.RangeIndex, "DC baseline must not hide absolute-terminal range demand");
            FactoryResult noCircuit = Oscillation(400, 25, .05, 0);
            noCircuit.Capture.Settings.OscillationCircuitConnected = false;
            Require(FactoryAnalysis.Analyze(noCircuit).FrequencyValid && !noCircuit.Capture.Settings.OscillationCircuitConnected,
                "legacy disabled feature flag does not suppress measured oscillation or manufacture a wiring confirmation");
            FactoryResult inconsistent = Oscillation(400, 25, .05, 0);
            for (int i = 0; i < inconsistent.Capture.VoltageRaw.Length; i++)
                inconsistent.Capture.VoltageRaw[i] += .025 * Math.Sin(2 * Math.PI * 713 * i / 80000.0);
            Require(!FactoryAnalysis.Analyze(inconsistent).FrequencyValid, "multiple frequencies rejected rather than certified"); checks += 3;
            return "Factory analysis passed " + checks + " offline checks; no hardware accessed.";
        }

        private static BenchCapture Empty(double durationMs)
        {
            var settings = new BenchSettings { RequestedPicoRateHz = 125000, CaptureDurationMs = durationMs,
                RangeIndex = 5, ResolutionBits = 14, RealHardware = false };
            return new BenchCapture { Settings = settings, VoltageRaw = new double[settings.ExpectedArtCount],
                PdAdc = new short[settings.RequestedPicoCount], PicoRateHz = settings.RequestedPicoRateHz,
                ArtRateHz = 80000, PicoFullScaleV = .5, MaximumAdc = 32767, HardwareBits = 14,
                Simulation = true, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO", Runtime = "offline-synthetic-only", CapturedUtc = DateTime.UtcNow };
        }
        private static BenchCapture PulseCapture(double durationMs, double frequency, double amplitude, double offset, double noise, bool inconsistent)
        {
            BenchCapture capture = Empty(durationMs); var random = new Random(83);
            for (int i = 0; i < capture.PdAdc.Length; i++)
                capture.PdAdc[i] = Code(capture, offset + noise * (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5));
            int index = 0;
            for (double time = .005; time < durationMs / 1000; time += 1 / frequency)
                AddPulse(capture, time, amplitude * (inconsistent && (index++ & 1) == 1 ? .5 : 1));
            return capture;
        }
        private static void AddPulse(BenchCapture capture, double time, double amplitude)
        {
            int centre = (int)Math.Round(time * capture.PicoRateHz);
            int radius = (int)Math.Ceiling(.00012 * capture.PicoRateHz);
            for (int i = Math.Max(0, centre - radius); i <= Math.Min(capture.PdAdc.Length - 1, centre + radius); i++)
            {
                double x = (i / capture.PicoRateHz - time) / .000024;
                double value = capture.PdAdc[i] * capture.PicoFullScaleV / capture.MaximumAdc + amplitude * Math.Exp(-.5 * x * x);
                capture.PdAdc[i] = Code(capture, value);
            }
        }
        private static void MovePulse(BenchCapture capture, double oldTime, double newTime)
        {
            int centre = (int)Math.Round(oldTime * capture.PicoRateHz), radius = (int)Math.Ceiling(.00012 * capture.PicoRateHz);
            for (int i = Math.Max(0, centre - radius); i <= Math.Min(capture.PdAdc.Length - 1, centre + radius); i++) capture.PdAdc[i] = 0;
            AddPulse(capture, newTime, .1);
        }
        private static short Code(BenchCapture capture, double voltage)
        { return (short)Math.Max(-32767, Math.Min(32767, Math.Round(voltage * capture.MaximumAdc / capture.PicoFullScaleV))); }
        private static FactoryResult Oscillation(double frequency, double alpha, double amplitude, double offset)
        {
            BenchCapture capture = Empty(60); capture.Settings.OscillationCircuitConnected = true;
            for (int i = 0; i < capture.VoltageRaw.Length; i++)
            {
                double time = i / capture.ArtRateHz;
                capture.VoltageRaw[i] = offset + amplitude * Math.Exp(-alpha * time) * Math.Sin(2 * Math.PI * frequency * time);
            }
            return new FactoryResult { Settings = new FactorySettings(), Capture = capture };
        }
        private static void Require(bool pass, string detail) { if (!pass) throw new InvalidOperationException("Factory analysis offline test failed: " + detail); }
        private static void Near(double actual, double expected, double tolerance, string detail)
        { Require(!Double.IsNaN(actual) && Math.Abs(actual - expected) <= tolerance, detail + " actual=" + actual + " expected=" + expected); }
    }
}
