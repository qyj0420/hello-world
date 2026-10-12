using System;
using System.Collections.Generic;
using System.IO;

namespace Xiongxin.ZeroBench
{
    public static class DetectedWaveformTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            BenchCapture c = Capture(60, 80000, 125000000);
            c.VoltageRaw[0] = -.002;
            c.VoltageRaw[1] = .003;
            c.PdAdc[1562] = 100; // Last timestamp before the first 12.5us boundary.
            c.PdAdc[1563] = -200; // First timestamp after the boundary.
            c.PdAdc[3124] = 300;
            c.PdAdc[3125] = -400; // Exactly the 25us boundary belongs to the next bin.
            c.PdAdc[c.PdAdc.Length - 1] = -500;
            short[] unchanged = (short[])c.PdAdc.Clone();
            double[] voltageUnchanged = (double[])c.VoltageRaw.Clone();
            DetectedWaveformResult r = DetectedWaveforms.Build(c);
            Require(r.OutputCount == 4800 && r.OutputRateHz == 80000 && r.DurationSeconds == .060, "60ms has exactly 4800 uniform samples");
            Require(r.ConsumedPdSamples == 7500000 && r.UnusedPdSamples == 0 && !r.Truncated, "all original Pico samples covered exactly once");
            Equal(r.PdEnvelopePc[0], 100, "first half-open bin includes sample1562");
            Equal(r.PdEnvelopePc[1], 300, "second half-open bin ends at3125");
            Equal(r.PdEnvelopePc[2], 400, "negative pulse exactly on boundary belongs to next bin");
            Equal(r.PdEnvelopePc[4799], 500, "final native sample survives detection");
            Equal(r.VoltageKv[0], -.022, "negative divider voltage preserved");
            Equal(r.VoltageKv[1], .033, "positive divider voltage preserved");
            for (int i = 0; i < unchanged.Length; i++) Require(c.PdAdc[i] == unchanged[i], "raw ADC immutable");
            for (int i = 0; i < voltageUnchanged.Length; i++) Require(c.VoltageRaw[i] == voltageUnchanged[i], "raw ART immutable");
            passed.Add("80k: 125M fractional-stride bins, edge/negative/final narrow pulses, signed voltage and immutable originals");

            // Put a pulse at every native index of a reduced record. No native sample is skipped,
            // and no sample is counted twice across alternating integer/fractional bin boundaries.
            c = Capture(1, 80000, 200000);
            for (int sample = 0; sample < c.PdAdc.Length; sample++)
            {
                c.PdAdc[sample] = 100;
                r = DetectedWaveforms.Build(c, 0);
                int nonzero = 0, selected = -1;
                for (int bin = 0; bin < r.OutputCount; bin++) if (r.PdEnvelopePc[bin] != 0) { nonzero++; selected = bin; }
                Require(nonzero == 1 && selected == (int)Math.Floor(sample * 80000.0 / 200000), "each native sample contributes to exactly its timestamp bin");
                c.PdAdc[sample] = 0;
            }
            passed.Add("80k: exhaustive non-integer-ratio sample-to-bin coverage without loss or duplication");

            c = Capture(2, 80000, 1000000);
            for (int i = 0; i < c.PdAdc.Length; i++) c.PdAdc[i] = 1000;
            for (int i = 0; i < c.VoltageRaw.Length; i++) c.VoltageRaw[i] = .1;
            r = DetectedWaveforms.Build(c);
            Equal(r.BaselineV, 1, "robust constant ADC DC baseline");
            for (int i = 0; i < r.OutputCount; i++)
            { Equal(r.PdEnvelopePc[i], 0, "constant PD DC removed"); Equal(r.VoltageKv[i], 1.1, "real DC voltage is not erased"); }
            for (int i = 0; i < c.PdAdc.Length; i++) c.PdAdc[i] = (short)(1000 + (i % 5) - 2);
            r = DetectedWaveforms.Build(c);
            Equal(r.BaselineV, 1, "small alternating noise robust baseline");
            for (int i = 0; i < r.OutputCount; i++) Equal(r.PdEnvelopePc[i], 2, "background is preserved, not thresholded to zero");
            passed.Add("80k: constant PD DC removed, background peak retained, genuine signed voltage DC not erased");

            foreach (double milliseconds in new[] { 1.0, 20.0, 60.0, 1000.0 })
            {
                c = Capture(milliseconds, 80000, 1000000);
                c.PdAdc[c.PdAdc.Length - 1] = 200;
                r = DetectedWaveforms.Build(c, 0);
                Require(r.OutputCount == (int)(milliseconds * 80) && r.ConsumedPdSamples == c.PdAdc.Length, "configurable duration actual record fully covered");
                Equal(r.PdEnvelopePc[r.OutputCount - 1], 200, "dynamic duration final sample survives");
            }
            c = Capture(2, 160000, 250000);
            for (int i = 0; i < c.VoltageRaw.Length; i++) c.VoltageRaw[i] = i * .001;
            r = DetectedWaveforms.Build(c, 0);
            Equal(r.VoltageKv[10], .220, "actual ART rate mapped onto true80k timestamps");
            c = Capture(2, 120000, 250000);
            for (int i = 0; i < c.VoltageRaw.Length; i++) c.VoltageRaw[i] = i * .001;
            r = DetectedWaveforms.Build(c, 0);
            Equal(r.VoltageKv[1], .0165, "noninteger ART ratio linear interpolation preserves signed scaling");
            passed.Add("80k: 1/20/60/1000ms and alternate actual ART/Pico rates use real timestamps");

            c = Capture(2, 80000, 1000000);
            Array.Resize(ref c.PdAdc, 1999);
            r = DetectedWaveforms.Build(c, 0);
            Require(r.Truncated && r.OutputCount == 159 && r.PdEnvelopePc.Length == r.VoltageKv.Length, "partial tail omitted, never zero padded");
            Require(r.ConsumedPdSamples == 1988 && r.UnusedPdSamples == 11, "partial trailing native samples explicitly reported");
            c = Capture(2, 80000, 1000000); c.Overflow = true;
            Require(DetectedWaveforms.Build(c).Overflow, "overflow metadata retained");
            c.PicoRateHz = 79999;
            Reject(() => DetectedWaveforms.Build(c), "Pico rate below80k rejected");
            c = Capture(2, 80000, 1000000); c.VoltageRaw[20] = Double.NaN;
            Reject(() => DetectedWaveforms.Build(c), "nonfinite displayed voltage rejected");
            passed.Add("80k: short/partial input is explicitly truncated without padding; overflow retained; low rate and invalid data rejected");
            return String.Join(Environment.NewLine, passed);
        }

        private static BenchCapture Capture(double milliseconds, double artRate, double picoRate)
        {
            return new BenchCapture {
                Settings = new BenchSettings { CaptureDurationMs = milliseconds, DividerVPerV = 11000,
                    PdCalibrationPcPerV = 1000, TransientExclusionMs = 0 },
                VoltageRaw = new double[(int)Math.Round(milliseconds * artRate / 1000)],
                PdAdc = new short[(int)Math.Round(milliseconds * picoRate / 1000)],
                ArtRateHz = artRate, PicoRateHz = picoRate, MaximumAdc = 1000, PicoFullScaleV = 1
            };
        }
        private static void Equal(double value, double expected, string name)
        { Require(Math.Abs(value - expected) <= 1e-10 * Math.Max(1, Math.Abs(expected)), name + ": " + value + " != " + expected); }
        private static void Require(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Detected waveform test failed: " + name); }
        private static void Reject(Action action, string name)
        {
            try { action(); } catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Detected waveform test failed: " + name);
        }
    }
}
