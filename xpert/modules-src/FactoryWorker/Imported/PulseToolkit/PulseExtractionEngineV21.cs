// Source snapshot: D:/PD_AI_LabVIEW2026_Project/PD_TimeFrequency_DLL/src/PulseExtractionEngineV21.cs
using System;
using System.Collections.Generic;

namespace HVX.PD.PulseToolkit
{
    /// <summary>Flat LabVIEW output for ROI-constrained isolated-pulse extraction.</summary>
    public sealed class PulseExtractionResult
    {
        public bool Success { get; internal set; }
        public string Error { get; internal set; }
        public string ApiVersion { get; internal set; }
        public string BoundaryStatus { get; internal set; }
        public string Summary { get; internal set; }
        public int ApproximatePulseStartIndex { get; internal set; }
        public int ApproximatePulseEndIndex { get; internal set; }
        public int SearchStartIndex { get; internal set; }
        public int SearchEndIndex { get; internal set; }
        public int StartIndex { get; internal set; }
        public int EndIndex { get; internal set; }
        public int ActiveStartIndex { get; internal set; }
        public int ActiveEndIndex { get; internal set; }
        public int PeakIndex { get; internal set; }
        public int PeakIndexInPulse { get; internal set; }
        public int SampleCount { get; internal set; }
        public int QuietSampleCount { get; internal set; }
        public double PeakValue { get; internal set; }
        public double PeakCorrectedValue { get; internal set; }
        public double Baseline { get; internal set; }
        public double NoiseSigma { get; internal set; }
        public double DetectionThreshold { get; internal set; }
        public double BoundaryThreshold { get; internal set; }
        public double PeakEnvelope { get; internal set; }
        public double SnrDb { get; internal set; }
        public double BoundaryQuality { get; internal set; }
        public double SampleRateHz { get; internal set; }
        public double SampleIntervalNs { get; internal set; }
        public double[] Pulse { get; internal set; }
        public double[] BaselineCorrectedPulse { get; internal set; }
        public double[] Envelope { get; internal set; }
        public double[] TimeAxisNs { get; internal set; }

        public PulseExtractionResult()
        {
            Error = string.Empty;
            ApiVersion = "hvx-pd-pulse-toolkit-dotnet-2.1";
            BoundaryStatus = string.Empty;
            Summary = string.Empty;
            ApproximatePulseStartIndex = ApproximatePulseEndIndex = -1;
            SearchStartIndex = SearchEndIndex = -1;
            StartIndex = EndIndex = ActiveStartIndex = ActiveEndIndex = -1;
            PeakIndex = PeakIndexInPulse = -1;
            PeakValue = PeakCorrectedValue = Baseline = NoiseSigma = double.NaN;
            DetectionThreshold = BoundaryThreshold = PeakEnvelope = SnrDb = double.NaN;
            SampleRateHz = SampleIntervalNs = double.NaN;
            Pulse = BaselineCorrectedPulse = Envelope = TimeAxisNs = new double[0];
        }
    }

    /// <summary>
    /// Function 1. The supplied inclusive interval is an approximate target-pulse
    /// ROI, not merely a peak cursor. Exact boundaries are constrained to a local
    /// context around that ROI and can never expand to the complete record.
    /// </summary>
    public sealed class PulseExtractionEngine
    {
        private const double MadToSigma = 1.482602218505602;
        public string Version { get { return "2.1.0"; } }
        public string Health() { return "OK|hvx-pd-pulse-toolkit-dotnet-2.1|function-1|approximate-pulse-roi|local-boundary-only"; }

        public PulseExtractionResult Extract(
            double[] waveform,
            double sampleRateHz,
            int approximatePulseStartIndex,
            int approximatePulseEndIndex)
        {
            PulseExtractionResult r = new PulseExtractionResult();
            r.ApproximatePulseStartIndex = approximatePulseStartIndex;
            r.ApproximatePulseEndIndex = approximatePulseEndIndex;
            try
            {
                InternalPulseDsp.ValidateArray(waveform, 32, "waveform");
                InternalPulseDsp.ValidateSampleRate(sampleRateHz);
                if (approximatePulseStartIndex < 0 || approximatePulseEndIndex >= waveform.Length ||
                    approximatePulseEndIndex <= approximatePulseStartIndex)
                    throw new ArgumentOutOfRangeException("approximatePulseStartIndex",
                        "The inclusive approximate pulse ROI must satisfy 0 <= start < end < waveform.Length.");
                int roiWidth = approximatePulseEndIndex - approximatePulseStartIndex + 1;
                if (roiWidth < 8) throw new ArgumentException("The approximate pulse ROI must contain at least eight samples.");

                int quietSamples = Math.Max(3, (int)Math.Round(sampleRateHz * 48.0e-9));
                quietSamples = Math.Min(quietSamples, Math.Max(3, roiWidth / 4));
                int margin = Math.Max(quietSamples * 3, roiWidth / 2);
                int searchStart = Math.Max(0, approximatePulseStartIndex - margin);
                int searchEnd = Math.Min(waveform.Length - 1, approximatePulseEndIndex + margin);
                r.SearchStartIndex = searchStart;
                r.SearchEndIndex = searchEnd;

                InternalPulseDsp.NoiseEstimate noise = EstimateLocalNoise(
                    waveform, searchStart, searchEnd, approximatePulseStartIndex, approximatePulseEndIndex);
                double[] centered = InternalPulseDsp.Center(waveform, noise.Baseline);
                int smoothRadius = InternalPulseDsp.ClampInt((int)Math.Round(sampleRateHz * 16.0e-9), 1, 8);
                double[] envelope = InternalPulseDsp.MovingRms(centered, smoothRadius);
                int peak = InternalPulseDsp.FindLargestMagnitude(
                    centered, approximatePulseStartIndex, approximatePulseEndIndex);
                double peakEnvelope = 0.0;
                for (int i = Math.Max(searchStart, peak - smoothRadius); i <= Math.Min(searchEnd, peak + smoothRadius); i++)
                    peakEnvelope = Math.Max(peakEnvelope, envelope[i]);
                double highThreshold = Math.Max(5.0 * noise.Sigma, 0.05 * peakEnvelope);
                double lowThreshold = Math.Max(3.0 * noise.Sigma, 0.015 * peakEnvelope);
                if (peakEnvelope < highThreshold)
                    throw new InvalidOperationException("No target pulse above the local detection threshold exists inside the approximate ROI.");

                int activeStart;
                int activeEnd;
                if (!FindQuietBefore(envelope, peak, searchStart, lowThreshold, quietSamples, out activeStart))
                    throw new InvalidOperationException("No reliable quiet boundary exists before the target pulse inside the local ROI context; no full-record fallback is allowed.");
                if (!FindQuietAfter(envelope, peak, searchEnd, lowThreshold, quietSamples, out activeEnd))
                    throw new InvalidOperationException("No reliable quiet boundary exists after the target pulse inside the local ROI context; no full-record fallback is allowed.");
                if (activeStart >= peak || activeEnd <= peak || activeEnd <= activeStart)
                    throw new InvalidOperationException("The local pulse boundary is inconsistent with the target peak.");

                int edgePadding = Math.Max(1, quietSamples / 3);
                int start = Math.Max(searchStart, activeStart - edgePadding);
                int end = Math.Min(searchEnd, activeEnd + edgePadding);
                int maximumAllowed = searchEnd - searchStart + 1;
                if (end - start + 1 >= maximumAllowed)
                    throw new InvalidOperationException("The local boundary reached the complete search context; no pulse is returned.");
                if (start <= searchStart && searchStart > 0 || end >= searchEnd && searchEnd < waveform.Length - 1)
                    throw new InvalidOperationException("The local boundary touches a search guard; enlarge or shift the approximate pulse ROI.");

                r.StartIndex = start; r.EndIndex = end;
                r.ActiveStartIndex = activeStart; r.ActiveEndIndex = activeEnd;
                r.PeakIndex = peak; r.PeakIndexInPulse = peak - start;
                r.SampleCount = end - start + 1; r.QuietSampleCount = quietSamples;
                r.PeakValue = waveform[peak]; r.PeakCorrectedValue = centered[peak];
                r.Baseline = noise.Baseline; r.NoiseSigma = noise.Sigma;
                r.DetectionThreshold = highThreshold; r.BoundaryThreshold = lowThreshold;
                r.PeakEnvelope = peakEnvelope;
                r.SnrDb = 20.0 * Math.Log10(Math.Max(Math.Abs(centered[peak]), 1e-300) / Math.Max(noise.Sigma, 1e-300));
                double startSeparation = 1.0 - InternalPulseDsp.Clamp(envelope[Math.Max(searchStart, activeStart - 1)] / Math.Max(lowThreshold, 1e-300), 0, 1);
                double endSeparation = 1.0 - InternalPulseDsp.Clamp(envelope[Math.Min(searchEnd, activeEnd + 1)] / Math.Max(lowThreshold, 1e-300), 0, 1);
                double snrScore = InternalPulseDsp.Clamp((r.SnrDb - 8.0) / 22.0, 0, 1);
                r.BoundaryQuality = InternalPulseDsp.Clamp(0.45 * snrScore + 0.275 * startSeparation + 0.275 * endSeparation, 0, 1);
                r.SampleRateHz = sampleRateHz; r.SampleIntervalNs = 1.0e9 / sampleRateHz;
                r.Pulse = InternalPulseDsp.CopyRange(waveform, start, end);
                r.BaselineCorrectedPulse = InternalPulseDsp.CopyRange(centered, start, end);
                r.Envelope = InternalPulseDsp.CopyRange(envelope, start, end);
                r.TimeAxisNs = new double[r.SampleCount];
                for (int i = 0; i < r.SampleCount; i++) r.TimeAxisNs[i] = i * r.SampleIntervalNs;
                r.BoundaryStatus = "LOCAL_ROI_ISOLATED_BY_QUIET_GAPS";
                r.Success = true;
                r.Summary = "ROI=[" + approximatePulseStartIndex + "," + approximatePulseEndIndex +
                    "], local-search=[" + searchStart + "," + searchEnd + "], pulse=[" + start + "," + end +
                    "], peak=" + peak + ", samples=" + r.SampleCount;
            }
            catch (Exception e)
            {
                r.Error = InternalPulseDsp.SingleLine(e.Message);
                r.BoundaryStatus = "LOCAL_ROI_NOT_ISOLATED";
                r.Summary = "ROI pulse extraction failed: " + r.Error;
            }
            return r;
        }

        private static bool FindQuietBefore(double[] envelope, int peak, int limit, double threshold, int required, out int activeStart)
        {
            int run = 0;
            for (int i = peak; i >= limit; i--)
            {
                if (envelope[i] < threshold) run++; else run = 0;
                if (run >= required) { activeStart = i + required; return true; }
            }
            activeStart = -1; return false;
        }

        private static bool FindQuietAfter(double[] envelope, int peak, int limit, double threshold, int required, out int activeEnd)
        {
            int run = 0;
            for (int i = peak; i <= limit; i++)
            {
                if (envelope[i] < threshold) run++; else run = 0;
                if (run >= required) { int quietBegin = i - required + 1; activeEnd = quietBegin - 1; return true; }
            }
            activeEnd = -1; return false;
        }

        private static InternalPulseDsp.NoiseEstimate EstimateLocalNoise(double[] x, int searchStart, int searchEnd, int roiStart, int roiEnd)
        {
            List<double> samples = new List<double>();
            for (int i = searchStart; i < roiStart; i++) samples.Add(x[i]);
            for (int i = roiEnd + 1; i <= searchEnd; i++) samples.Add(x[i]);
            if (samples.Count < 16)
                for (int i = searchStart; i <= searchEnd; i++) samples.Add(x[i]);
            double[] values = samples.ToArray();
            double baseline = Median(values);
            double[] deviations = new double[values.Length];
            double maximum = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                deviations[i] = Math.Abs(values[i] - baseline);
                maximum = Math.Max(maximum, deviations[i]);
            }
            double sigma = MadToSigma * Median(deviations);
            if (!(sigma > 0.0) || double.IsNaN(sigma) || double.IsInfinity(sigma))
                sigma = Math.Max(maximum * 1e-12, 1e-300);
            return new InternalPulseDsp.NoiseEstimate(baseline, sigma);
        }

        private static double Median(double[] values)
        {
            double[] copy = (double[])values.Clone();
            Array.Sort(copy);
            int m = copy.Length / 2;
            return copy.Length % 2 == 1 ? copy[m] : 0.5 * (copy[m - 1] + copy[m]);
        }
    }
}
