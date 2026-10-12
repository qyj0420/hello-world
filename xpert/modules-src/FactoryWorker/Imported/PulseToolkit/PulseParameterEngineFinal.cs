// Source snapshot: D:/PD_AI_LabVIEW2026_Project/PD_TimeFrequency_DLL/src/PulseParameterEngineFinal.cs
using System;

namespace HVX.PD.PulseToolkit
{
    public sealed class PulseAnalysisResult
    {
        public bool Success { get; internal set; }
        public string Error { get; internal set; }
        public string ApiVersion { get; internal set; }
        public string Summary { get; internal set; }
        public int SampleCount { get; internal set; }
        public int FftLength { get; internal set; }
        public int CoreStartIndex { get; internal set; }
        public int CoreEndIndex { get; internal set; }
        public double SampleRateHz { get; internal set; }
        public double SampleIntervalNs { get; internal set; }
        public double RecordDurationNs { get; internal set; }
        public double Baseline { get; internal set; }
        public double NoiseSigma { get; internal set; }
        public double SnrDb { get; internal set; }
        public int FirstArrivalIndex { get; internal set; }
        public double FirstArrivalValue { get; internal set; }
        public double FirstArrivalCorrectedValue { get; internal set; }
        public double FirstArrivalTimeNs { get; internal set; }
        public double FirstArrivalThreshold { get; internal set; }
        public int MaximumIndex { get; internal set; }
        public double MaximumValue { get; internal set; }
        public int MinimumIndex { get; internal set; }
        public double MinimumValue { get; internal set; }
        public int MainPeakIndex { get; internal set; }
        public double MainPeakValue { get; internal set; }
        public double MainPeakCorrectedValue { get; internal set; }
        public int PositivePeakIndex { get; internal set; }
        public double PositivePeak { get; internal set; }
        public int NegativePeakIndex { get; internal set; }
        public double NegativePeak { get; internal set; }
        public double PeakToPeak { get; internal set; }
        public int DominantPolarity { get; internal set; }
        public double Mean { get; internal set; }
        public double MeanAbsolute { get; internal set; }
        public double Rms { get; internal set; }
        public double StandardDeviation { get; internal set; }
        public double Skewness { get; internal set; }
        public double Kurtosis { get; internal set; }
        public double ExcessKurtosis { get; internal set; }
        public double Peakedness { get; internal set; }
        public double PolarityFactor { get; internal set; }
        public double ShapeFactor { get; internal set; }
        public double CrestFactor { get; internal set; }
        public double ImpulseFactor { get; internal set; }
        public double ClearanceFactor { get; internal set; }
        public double StandardMarginFactor { get; internal set; }
        public double PulseWidth50Ns { get; internal set; }
        public double PulseWidth10Ns { get; internal set; }
        public double RiseTime10To90Ns { get; internal set; }
        public double FallTime90To10Ns { get; internal set; }
        public double EnergySum { get; internal set; }
        public double EnergyIntegral { get; internal set; }
        public double TimeCentroidNs { get; internal set; }
        public double EquivalentTimeNs { get; internal set; }
        public double EquivalentBandwidthHz { get; internal set; }
        public double EquivalentBandwidthMHz { get; internal set; }
        public double TimeBandwidthProduct { get; internal set; }
        public double CenterFrequencyHz { get; internal set; }
        public double CenterFrequencyMHz { get; internal set; }
        public double SpectralCentroidHz { get; internal set; }
        public double SpectralCentroidMHz { get; internal set; }
        public double SpectralRmsSpreadHz { get; internal set; }
        public double SpectralRmsSpreadMHz { get; internal set; }
        public double PeakFrequencyHz { get; internal set; }
        public double PeakFrequencyMHz { get; internal set; }
        public double[] Pulse { get; internal set; }
        public double[] BaselineCorrectedPulse { get; internal set; }
        public double[] AnalysisPulse { get; internal set; }
        public double[] Envelope { get; internal set; }
        public double[] TimeAxisNs { get; internal set; }
        public double[] FrequencyAxisHz { get; internal set; }
        public double[] PowerSpectrum { get; internal set; }

        public PulseAnalysisResult()
        {
            Error = string.Empty; ApiVersion = InternalPulseDsp.ApiVersion; Summary = string.Empty;
            CoreStartIndex = CoreEndIndex = FirstArrivalIndex = MaximumIndex = MinimumIndex = -1;
            MainPeakIndex = PositivePeakIndex = NegativePeakIndex = -1;
            Pulse = BaselineCorrectedPulse = AnalysisPulse = Envelope = TimeAxisNs = FrequencyAxisHz = PowerSpectrum = new double[0];
            double nan = double.NaN;
            SampleRateHz = SampleIntervalNs = RecordDurationNs = Baseline = NoiseSigma = SnrDb = nan;
            FirstArrivalValue = FirstArrivalCorrectedValue = FirstArrivalTimeNs = FirstArrivalThreshold = nan;
            MaximumValue = MinimumValue = MainPeakValue = MainPeakCorrectedValue = PositivePeak = NegativePeak = PeakToPeak = nan;
            Mean = MeanAbsolute = Rms = StandardDeviation = Skewness = Kurtosis = ExcessKurtosis = Peakedness = nan;
            PolarityFactor = ShapeFactor = CrestFactor = ImpulseFactor = ClearanceFactor = StandardMarginFactor = nan;
            PulseWidth50Ns = PulseWidth10Ns = RiseTime10To90Ns = FallTime90To10Ns = nan;
            EnergySum = EnergyIntegral = TimeCentroidNs = EquivalentTimeNs = nan;
            EquivalentBandwidthHz = EquivalentBandwidthMHz = TimeBandwidthProduct = nan;
            CenterFrequencyHz = CenterFrequencyMHz = SpectralCentroidHz = SpectralCentroidMHz = nan;
            SpectralRmsSpreadHz = SpectralRmsSpreadMHz = PeakFrequencyHz = PeakFrequencyMHz = nan;
        }
    }

    public sealed class PulseAnalysisEngine
    {
        public string Version { get { return "2.0.0"; } }
        public string Health() { return "OK|" + InternalPulseDsp.ApiVersion + "|function-2|flat-pulse-parameters|no-reflection"; }

        public PulseAnalysisResult Analyze(double[] pulse, double sampleRateHz)
        {
            PulseAnalysisResult r = new PulseAnalysisResult();
            try
            {
                InternalPulseDsp.ValidateArray(pulse, 8, "pulse");
                InternalPulseDsp.ValidateSampleRate(sampleRateHz);
                r.SampleCount = pulse.Length; r.SampleRateHz = sampleRateHz;
                r.SampleIntervalNs = 1.0e9 / sampleRateHz; r.RecordDurationNs = (pulse.Length - 1) * r.SampleIntervalNs;
                r.Pulse = (double[])pulse.Clone();
                InternalPulseDsp.NoiseEstimate noise = InternalPulseDsp.EstimateNoiseFromEdges(pulse);
                r.Baseline = noise.Baseline; r.NoiseSigma = noise.Sigma;
                double[] x = InternalPulseDsp.Center(pulse, noise.Baseline); r.BaselineCorrectedPulse = x;
                r.MaximumIndex = InternalPulseDsp.IndexOfMaximum(pulse); r.MaximumValue = pulse[r.MaximumIndex];
                r.MinimumIndex = InternalPulseDsp.IndexOfMinimum(pulse); r.MinimumValue = pulse[r.MinimumIndex];
                r.PositivePeakIndex = InternalPulseDsp.IndexOfMaximum(x); r.PositivePeak = x[r.PositivePeakIndex];
                r.NegativePeakIndex = InternalPulseDsp.IndexOfMinimum(x); r.NegativePeak = x[r.NegativePeakIndex];
                r.MainPeakIndex = InternalPulseDsp.FindLargestMagnitude(x, 0, x.Length - 1);
                r.MainPeakValue = pulse[r.MainPeakIndex]; r.MainPeakCorrectedValue = x[r.MainPeakIndex];
                r.DominantPolarity = r.MainPeakCorrectedValue > 0 ? 1 : r.MainPeakCorrectedValue < 0 ? -1 : 0;
                r.PeakToPeak = r.MaximumValue - r.MinimumValue;
                r.SnrDb = 20.0 * Math.Log10(Math.Max(Math.Abs(r.MainPeakCorrectedValue), 1e-300) / Math.Max(noise.Sigma, 1e-300));
                double arrivalThreshold;
                r.FirstArrivalIndex = InternalPulseDsp.FindFirstArrival(x, sampleRateHz, noise.Sigma, r.MainPeakIndex, out arrivalThreshold);
                r.FirstArrivalThreshold = arrivalThreshold; r.FirstArrivalValue = pulse[r.FirstArrivalIndex];
                r.FirstArrivalCorrectedValue = x[r.FirstArrivalIndex]; r.FirstArrivalTimeNs = r.FirstArrivalIndex * r.SampleIntervalNs;
                Statistics(r, x); EnvelopeTiming(r, x, sampleRateHz); TimeFrequency(r, x, sampleRateHz);
                r.TimeAxisNs = new double[x.Length];
                for (int i = 0; i < x.Length; i++) r.TimeAxisNs[i] = i * r.SampleIntervalNs;
                r.Success = true;
                r.Summary = "samples=" + r.SampleCount + ", first=" + r.FirstArrivalIndex +
                    ", max=" + r.MaximumValue.ToString("G8") + "@" + r.MaximumIndex +
                    ", min=" + r.MinimumValue.ToString("G8") + "@" + r.MinimumIndex +
                    ", width50=" + r.PulseWidth50Ns.ToString("F3") + " ns, Teq=" + r.EquivalentTimeNs.ToString("F3") +
                    " ns, Weq=" + r.EquivalentBandwidthMHz.ToString("F3") + " MHz";
            }
            catch (Exception e) { r.Error = InternalPulseDsp.SingleLine(e.Message); r.Summary = "pulse analysis failed: " + r.Error; }
            return r;
        }

        private static void Statistics(PulseAnalysisResult r, double[] x)
        {
            int n = x.Length; double sum = 0, abs = 0, sq = 0, rootAbs = 0, ep = 0, en = 0, maxAbs = 0;
            for (int i = 0; i < n; i++)
            {
                double v = x[i], a = Math.Abs(v); sum += v; abs += a; sq += v * v; rootAbs += Math.Sqrt(a); maxAbs = Math.Max(maxAbs, a);
                if (v >= 0) ep += v * v; else en += v * v;
            }
            r.Mean = sum / n; r.MeanAbsolute = abs / n; r.Rms = Math.Sqrt(sq / n); r.EnergySum = sq;
            double m2 = 0, m3 = 0, m4 = 0;
            for (int i = 0; i < n; i++) { double d = x[i] - r.Mean, d2 = d * d; m2 += d2; m3 += d2 * d; m4 += d2 * d2; }
            m2 /= n; m3 /= n; m4 /= n; r.StandardDeviation = Math.Sqrt(Math.Max(0, m2));
            if (r.StandardDeviation > 0) { r.Skewness = m3 / Math.Pow(r.StandardDeviation, 3); r.Kurtosis = m4 / (m2 * m2); r.ExcessKurtosis = r.Kurtosis - 3; r.Peakedness = r.Kurtosis; }
            r.PolarityFactor = ep + en > 0 ? (ep - en) / (ep + en) : double.NaN;
            r.ShapeFactor = Divide(r.Rms, r.MeanAbsolute); r.CrestFactor = Divide(maxAbs, r.Rms); r.ImpulseFactor = Divide(maxAbs, r.MeanAbsolute);
            double meanRootAbs = rootAbs / n; r.ClearanceFactor = Divide(maxAbs, meanRootAbs * meanRootAbs); r.StandardMarginFactor = r.ClearanceFactor;
        }

        private static void EnvelopeTiming(PulseAnalysisResult r, double[] x, double fs)
        {
            int c0, c1; double[] tapered = InternalPulseDsp.ApplyEnergyCoreTaper(x, out c0, out c1);
            double[] env = InternalPulseDsp.AnalyticEnvelope(tapered); r.Envelope = env;
            int peak = InternalPulseDsp.IndexOfMaximum(env); double a = env[peak];
            double l10 = InternalPulseDsp.LeftCrossing(env, peak, .10 * a), l50 = InternalPulseDsp.LeftCrossing(env, peak, .50 * a), l90 = InternalPulseDsp.LeftCrossing(env, peak, .90 * a);
            double q90 = InternalPulseDsp.RightCrossing(env, peak, .90 * a), q50 = InternalPulseDsp.RightCrossing(env, peak, .50 * a), q10 = InternalPulseDsp.RightCrossing(env, peak, .10 * a);
            r.PulseWidth50Ns = InternalPulseDsp.DurationNs(l50, q50, fs); r.PulseWidth10Ns = InternalPulseDsp.DurationNs(l10, q10, fs);
            r.RiseTime10To90Ns = InternalPulseDsp.DurationNs(l10, l90, fs); r.FallTime90To10Ns = InternalPulseDsp.DurationNs(q90, q10, fs);
            if (!InternalPulseDsp.Finite(r.RiseTime10To90Ns) || !InternalPulseDsp.Finite(r.FallTime90To10Ns))
            {
                int radius = InternalPulseDsp.ClampInt((int)Math.Round(fs * 8e-9), 1, 6);
                env = InternalPulseDsp.MovingRms(x, radius); r.Envelope = env; peak = InternalPulseDsp.IndexOfMaximum(env); a = env[peak];
                l10 = InternalPulseDsp.LeftCrossing(env, peak, .10 * a); l50 = InternalPulseDsp.LeftCrossing(env, peak, .50 * a); l90 = InternalPulseDsp.LeftCrossing(env, peak, .90 * a);
                q90 = InternalPulseDsp.RightCrossing(env, peak, .90 * a); q50 = InternalPulseDsp.RightCrossing(env, peak, .50 * a); q10 = InternalPulseDsp.RightCrossing(env, peak, .10 * a);
                r.PulseWidth50Ns = InternalPulseDsp.DurationNs(l50, q50, fs); r.PulseWidth10Ns = InternalPulseDsp.DurationNs(l10, q10, fs);
                r.RiseTime10To90Ns = InternalPulseDsp.DurationNs(l10, l90, fs); r.FallTime90To10Ns = InternalPulseDsp.DurationNs(q90, q10, fs);
            }
        }

        private static void TimeFrequency(PulseAnalysisResult r, double[] x, double fs)
        {
            int c0, c1; double[] a = InternalPulseDsp.ApplyEnergyCoreTaper(x, out c0, out c1); r.AnalysisPulse = a; r.CoreStartIndex = c0; r.CoreEndIndex = c1;
            double e = 0, t1 = 0;
            for (int i = 0; i < a.Length; i++) { double q = a[i] * a[i]; e += q; t1 += (i / fs) * q; }
            if (!(e > 0)) throw new InvalidOperationException("The pulse has zero energy after baseline correction.");
            r.EnergySum = e; r.EnergyIntegral = e / fs; double tc = t1 / e; r.TimeCentroidNs = tc * 1e9; double tv = 0;
            for (int i = 0; i < a.Length; i++) { double d = i / fs - tc; tv += d * d * a[i] * a[i]; }
            r.EquivalentTimeNs = Math.Sqrt(Math.Max(0, tv / e)) * 1e9;
            InternalPulseDsp.SpectrumData s = InternalPulseDsp.ComputeOneSidedSpectrum(a, fs); r.FftLength = s.FftLength; r.FrequencyAxisHz = s.Frequency; r.PowerSpectrum = s.Power;
            double se = 0, f1 = 0, f2 = 0; int fp = s.Power.Length > 1 ? 1 : 0;
            for (int i = 0; i < s.Power.Length; i++) { se += s.Power[i]; f1 += s.Frequency[i] * s.Power[i]; f2 += s.Frequency[i] * s.Frequency[i] * s.Power[i]; if (i > 0 && s.Power[i] > s.Power[fp]) fp = i; }
            if (!(se > 0)) throw new InvalidOperationException("The pulse spectrum has zero energy.");
            double fc = f1 / se, bw = Math.Sqrt(Math.Max(0, f2 / se)), spread2 = 0;
            for (int i = 0; i < s.Power.Length; i++) { double d = s.Frequency[i] - fc; spread2 += d * d * s.Power[i]; }
            double spread = Math.Sqrt(Math.Max(0, spread2 / se)); r.EquivalentBandwidthHz = bw; r.EquivalentBandwidthMHz = bw * 1e-6;
            r.TimeBandwidthProduct = r.EquivalentTimeNs * 1e-9 * bw; r.CenterFrequencyHz = r.SpectralCentroidHz = fc; r.CenterFrequencyMHz = r.SpectralCentroidMHz = fc * 1e-6;
            r.SpectralRmsSpreadHz = spread; r.SpectralRmsSpreadMHz = spread * 1e-6; r.PeakFrequencyHz = s.Frequency[fp]; r.PeakFrequencyMHz = r.PeakFrequencyHz * 1e-6;
        }

        private static double Divide(double x, double y) { return Math.Abs(y) > 1e-300 ? x / y : double.NaN; }
    }
}
