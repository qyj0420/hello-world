using System;
using System.Collections.Generic;

namespace Xiongxin.ZeroBench
{
    public static class AnalysisTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            double fs = 80000, alpha = 50, frequency = 400, l = .7, resistance = 22;
            var wave = new double[4800];
            for (int i = 0; i < wave.Length; i++) { double t = i / fs; wave[i] = .8 * Math.Exp(-alpha * t) * Math.Cos(2 * Math.PI * frequency * t) + .003; }
            var v = SignalAnalysis.AnalyzeVoltage(wave, fs, 11000, l, resistance, true);
            Require(v.Valid, "Known damped wave valid: " + v.Status);
            Close(v.FrequencyHz, frequency, .002, "Frequency"); Close(v.DampingPerSecond, alpha, .015, "Decay");
            double omega = 2 * Math.PI * frequency;
            double expectedC = 1 / (l * (omega * omega + alpha * alpha) - resistance * (2 * alpha - resistance / l));
            Close(v.CapacitanceF, expectedC, .005, "Capacitance");
            Close(v.CorrectedTanDeltaEstimate, 2 * alpha / omega - resistance / (omega * l), .03, "Corrected apparent loss");
            passed.Add("Known damped model: frequency, decay, topology-corrected C and loss");
            foreach (double trialFrequency in new[] { 100.0, 1000.0 })
            {
                var trial = new double[4800];
                for (int i = 0; i < trial.Length; i++) { double t = i / fs; trial[i] = .3 * Math.Exp(-30 * t) * Math.Sin(2 * Math.PI * trialFrequency * t + .4) + .01; }
                var result = SignalAnalysis.AnalyzeVoltage(trial, fs, 11000, l, resistance, true);
                Require(result.Valid, "Frequency/phase/DC-offset variation " + trialFrequency + ": " + result.Status);
                Close(result.FrequencyHz, trialFrequency, .004, "Frequency variation");
            }
            var impossibleLoss = new double[4800];
            for (int i = 0; i < impossibleLoss.Length; i++) { double t = i / fs; impossibleLoss[i] = .4 * Math.Exp(-3 * t) * Math.Sin(2 * Math.PI * frequency * t); }
            var inconsistent = SignalAnalysis.AnalyzeVoltage(impossibleLoss, fs, 11000, l, resistance, true);
            Require(!inconsistent.Valid && Double.IsNaN(inconsistent.CorrectedTanDeltaEstimate), "Negative corrected loss stays invalid, not clamped");
            passed.Add("100/1000 Hz, phase/DC variation and negative-loss rejection");
            Require(!SignalAnalysis.AnalyzeVoltage(wave, fs, 11000, l, resistance, false).Valid, "Disconnected circuit invalid");
            Require(!SignalAnalysis.AnalyzeVoltage(new double[4800], fs, 11000, l, resistance, true).Valid, "Zero invalid");
            var noise = new double[4800]; var random = new Random(19);
            for (int i = 0; i < noise.Length; i++) noise[i] = (random.NextDouble() - .5) * .02;
            Require(!SignalAnalysis.AnalyzeVoltage(noise, fs, 11000, l, resistance, true).Valid, "Noise invalid");
            wave[4] = 10;
            Require(!SignalAnalysis.AnalyzeVoltage(wave, fs, 11000, l, resistance, true).Valid, "Voltage clipping invalid");
            passed.Add("Disconnected/background/noise/clipped wave rejected");
            var pd = new short[6000]; for (int i = 0; i < pd.Length; i++) pd[i] = 100;
            pd[2] = 20000; pd[100] = -9000; pd[101] = -9000;
            var p = SignalAnalysis.AnalyzePd(pd, 100000, 32767, 1, 2100, .5);
            Require(p.Valid, "Nonclipped PD input valid");
            Close(p.RawAbsolutePeakVolts, 20000.0 / 32767, 1e-10, "Raw peak includes transient/DC");
            Close(p.MaximumEquivalentPc, 9100.0 / 32767 * 2100, 1e-10, "Post-window equivalent peak");
            Require(p.PostExclusionPeakTimeMs == 1, "PD peak time");
            Require(p.HasPulseCandidate && p.PulseCandidateCount == 1, "Injected pulse candidate detected");
            pd[1] = 32767;
            Require(!SignalAnalysis.AnalyzePd(pd, 100000, 32767, 1, 2100).Valid, "ADC clipping invalid");
            pd[1] = 0;
            Require(!SignalAnalysis.AnalyzePd(pd, 100000, 32767, 1, 2100, .5, true).Valid, "Overflow invalid");
            var adcNoise = new short[6000];
            for (int i = 0; i < adcNoise.Length; i++) adcNoise[i] = (short)random.Next(-20, 21);
            Require(!SignalAnalysis.AnalyzePd(adcNoise, 100000, 32767, 1, 2100).HasPulseCandidate, "Background does not qualify for shrinking");
            passed.Add("Full-record overshoot and baseline-corrected peak separated; clipping invalidates equivalent charge");
            var fullSegment = new short[7500000];
            fullSegment[5000000] = fullSegment[5000001] = 10000;
            var latePeak = SignalAnalysis.AnalyzePd(fullSegment, 125000000, 32767, 1, 2100);
            Close(latePeak.RawPeakTimeMs, 40, 1e-12, "Full segment 40ms raw peak time");
            Close(latePeak.PostExclusionPeakTimeMs, 40, 1e-12, "Full segment 40ms corrected peak time");
            fullSegment[7499999] = 20000;
            latePeak = SignalAnalysis.AnalyzePd(fullSegment, 125000000, 32767, 1, 2100);
            Close(latePeak.RawPeakTimeMs, 59.999992, 1e-12, "Final sample raw time");
            Close(latePeak.PostExclusionPeakTimeMs, 59.999992, 1e-12, "Final sample corrected time");
            passed.Add("Full 7500000 point peak timestamps avoid integer overflow at 40ms and final sample");
            var range = new RangeState();
            Require(SignalAnalysis.NextRange(.81, false, 6, range).Index == 7, "Over 80% grows");
            Require(SignalAnalysis.NextRange(.5, true, 6, range).Index > 6, "Clipped grows even if bad observed peak");
            Require(SignalAnalysis.NextRange(20, true, 10, range).AtMaximumAndClipped, "Top range warns");
            range.Reset();
            Require(!SignalAnalysis.NextRange(.01, false, 6, range, true).Change, "Shrink wait 1");
            Require(!SignalAnalysis.NextRange(.01, false, 6, range, true).Change, "Shrink wait 2");
            Require(SignalAnalysis.NextRange(.01, false, 6, range, true).Index == 5, "Shrink one step after 3");
            range.Reset();
            for (int i = 0; i < 5; i++) Require(!SignalAnalysis.NextRange(.01, false, 6, range).Change, "Noise-only never shrinks");
            Require(!SignalAnalysis.NextRange(Double.NaN, false, 6, range).Change, "NaN retains");
            Require(!SignalAnalysis.NextRange(0, false, 0, range).Change, "Minimum range stays");
            Require(SignalAnalysis.InitialRangeFromPulse(100, 2100) == 3, "100pC -> conservative .1V start");
            passed.Add("Range boundaries, overflow growth, three-record shrink hysteresis and conservative initial pulse range");
            var location = SignalAnalysis.CopyLocation20Ms(pd, 100000);
            Require(location.Length == 2000 && location[100] == pd[100], "20ms raw window at trigger");
            location[100] = 123;
            Require(pd[100] == -9000, "Original raw record unmodified");
            passed.Add("20ms trigger-referenced copy preserves full raw capture");
            return "PASS " + passed.Count + " analysis groups\r\n" + String.Join("\r\n", passed.ToArray());
        }
        private static void Require(bool condition, string name) { if (!condition) throw new Exception("Analysis test failed: " + name); }
        private static void Close(double actual, double expected, double relative, string name)
        { Require(!Double.IsNaN(actual) && Math.Abs(actual - expected) <= Math.Abs(expected) * relative + 1e-12, name + " actual=" + actual + " expected=" + expected); }
    }
}
