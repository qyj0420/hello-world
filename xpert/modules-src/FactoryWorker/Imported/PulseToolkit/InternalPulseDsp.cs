// Source snapshot: D:/PD_AI_LabVIEW2026_Project/PD_TimeFrequency_DLL/src/InternalPulseDsp.cs
using System;
using System.Collections.Generic;

namespace HVX.PD.PulseToolkit
{
    internal static class InternalPulseDsp
    {
        internal const string ApiVersion = "hvx-pd-pulse-toolkit-dotnet-2.0";
        private const double MadToSigma = 1.482602218505602;

        internal sealed class BoundaryData
        {
            internal int Start;
            internal int End;
            internal int ActiveStart;
            internal int ActiveEnd;
            internal int Peak;
            internal int QuietSamples;
            internal string Status;
        }

        internal sealed class SpectrumData
        {
            internal int FftLength;
            internal double[] Frequency;
            internal double[] Power;
        }

        internal struct NoiseEstimate
        {
            internal readonly double Baseline;
            internal readonly double Sigma;

            internal NoiseEstimate(double baseline, double sigma)
            {
                Baseline = baseline;
                Sigma = sigma;
            }
        }

        internal static void ValidateArray(double[] values, int minimumLength, string name)
        {
            if (values == null || values.Length < minimumLength)
                throw new ArgumentException(name + " must contain at least " + minimumLength + " samples.", name);
            for (int i = 0; i < values.Length; i++)
                if (!Finite(values[i]))
                    throw new ArgumentException(name + " contains NaN or Infinity at index " + i + ".", name);
        }

        internal static void ValidateSampleRate(double sampleRateHz)
        {
            if (!Finite(sampleRateHz) || sampleRateHz <= 0.0)
                throw new ArgumentOutOfRangeException("sampleRateHz", "sampleRateHz must be finite and greater than zero.");
        }

        internal static BoundaryData FindIsolatedBoundary(
            double[] waveform,
            double sampleRateHz,
            int approximatePeakStartIndex,
            int approximatePeakEndIndex,
            out NoiseEstimate noise,
            out double[] centered,
            out double[] rmsEnvelope,
            out double detectionThreshold,
            out double boundaryThreshold)
        {
            ValidateArray(waveform, 32, "waveform");
            ValidateSampleRate(sampleRateHz);
            if (approximatePeakStartIndex < 0 || approximatePeakEndIndex >= waveform.Length ||
                approximatePeakEndIndex < approximatePeakStartIndex)
                throw new ArgumentOutOfRangeException(
                    "approximatePeakStartIndex",
                    "The inclusive approximate peak interval must satisfy 0 <= start <= end < waveform.Length.");

            noise = EstimateNoise(waveform);
            centered = Center(waveform, noise.Baseline);
            int peak = FindLargestMagnitude(centered, approximatePeakStartIndex, approximatePeakEndIndex);
            int smoothRadius = ClampInt((int)Math.Round(sampleRateHz * 16.0e-9), 1, 8);
            rmsEnvelope = MovingRms(centered, smoothRadius);

            double detectionFactor = Clamp(Math.Sqrt(2.0 * Math.Log(Math.Max(2, waveform.Length))), 5.0, 6.5);
            detectionThreshold = detectionFactor * noise.Sigma;
            boundaryThreshold = 3.0 * noise.Sigma;
            if (Math.Abs(centered[peak]) < detectionThreshold)
                throw new InvalidOperationException("No pulse above the robust detection threshold exists inside the approximate peak interval.");

            int quietSamples = Math.Max(4, (int)Math.Round(sampleRateHz * 80.0e-9));
            quietSamples = Math.Min(quietSamples, Math.Max(4, waveform.Length / 8));
            int activeStart = 0;
            bool quietBefore = false;
            int run = 0;
            for (int i = peak; i >= 0; i--)
            {
                if (rmsEnvelope[i] < boundaryThreshold) run++;
                else run = 0;
                if (run >= quietSamples)
                {
                    activeStart = i + quietSamples;
                    quietBefore = true;
                    break;
                }
            }

            int activeEnd = waveform.Length - 1;
            bool quietAfter = false;
            run = 0;
            for (int i = peak; i < waveform.Length; i++)
            {
                if (rmsEnvelope[i] < boundaryThreshold) run++;
                else run = 0;
                if (run >= quietSamples)
                {
                    int quietBegin = i - quietSamples + 1;
                    activeEnd = Math.Max(peak, quietBegin - 1);
                    quietAfter = true;
                    break;
                }
            }

            if (!quietBefore || !quietAfter)
                throw new InvalidOperationException(
                    "The target pulse is not isolated by sustained quiet samples on both sides. Tighten the approximate peak interval or acquire a longer record; no mixed pulse is returned.");

            int pad = Math.Max(2, quietSamples / 2);
            int start = Math.Max(0, activeStart - pad);
            int end = Math.Min(waveform.Length - 1, activeEnd + pad);
            if (end - start + 1 < 8)
                throw new InvalidOperationException("The isolated pulse interval is shorter than eight samples.");

            BoundaryData boundary = new BoundaryData();
            boundary.Start = start;
            boundary.End = end;
            boundary.ActiveStart = activeStart;
            boundary.ActiveEnd = activeEnd;
            boundary.Peak = peak;
            boundary.QuietSamples = quietSamples;
            boundary.Status = "ISOLATED_BY_QUIET_GAPS";
            return boundary;
        }

        internal static NoiseEstimate EstimateNoise(double[] values)
        {
            double baseline = Median(values);
            double[] deviations = new double[values.Length];
            double maximumDeviation = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                deviations[i] = Math.Abs(values[i] - baseline);
                maximumDeviation = Math.Max(maximumDeviation, deviations[i]);
            }
            double sigma = MadToSigma * Median(deviations);
            if (!(sigma > 0.0) || !Finite(sigma))
                sigma = Math.Max(maximumDeviation * 1.0e-12, 1.0e-300);
            return new NoiseEstimate(baseline, sigma);
        }

        internal static NoiseEstimate EstimateNoiseFromEdges(double[] pulse)
        {
            int edge = ClampInt(pulse.Length / 8, 4, 64);
            List<double> samples = new List<double>(2 * edge);
            for (int i = 0; i < edge; i++) samples.Add(pulse[i]);
            for (int i = pulse.Length - edge; i < pulse.Length; i++) samples.Add(pulse[i]);
            NoiseEstimate estimate = EstimateNoise(samples.ToArray());
            if (estimate.Sigma <= 1.0e-299)
            {
                NoiseEstimate fallback = EstimateNoise(pulse);
                return new NoiseEstimate(estimate.Baseline, fallback.Sigma);
            }
            return estimate;
        }

        internal static double[] Center(double[] values, double baseline)
        {
            double[] output = new double[values.Length];
            for (int i = 0; i < values.Length; i++) output[i] = values[i] - baseline;
            return output;
        }

        internal static double[] CopyRange(double[] values, int start, int end)
        {
            double[] output = new double[end - start + 1];
            Array.Copy(values, start, output, 0, output.Length);
            return output;
        }

        internal static double[] MovingRms(double[] values, int radius)
        {
            double[] prefix = new double[values.Length + 1];
            for (int i = 0; i < values.Length; i++) prefix[i + 1] = prefix[i] + values[i] * values[i];
            double[] output = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                int start = Math.Max(0, i - radius);
                int end = Math.Min(values.Length - 1, i + radius);
                output[i] = Math.Sqrt(Math.Max(0.0, (prefix[end + 1] - prefix[start]) / (end - start + 1)));
            }
            return output;
        }

        internal static double[] AnalyticEnvelope(double[] values)
        {
            int fftLength = NextPowerOfTwo(values.Length);
            double[] real = new double[fftLength];
            double[] imaginary = new double[fftLength];
            Array.Copy(values, real, values.Length);
            FftInPlace(real, imaginary, false);
            for (int i = 0; i < fftLength; i++)
            {
                double multiplier;
                if (i == 0 || (fftLength % 2 == 0 && i == fftLength / 2)) multiplier = 1.0;
                else if (i < (fftLength + 1) / 2) multiplier = 2.0;
                else multiplier = 0.0;
                real[i] *= multiplier;
                imaginary[i] *= multiplier;
            }
            FftInPlace(real, imaginary, true);
            double[] envelope = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
                envelope[i] = Math.Sqrt(real[i] * real[i] + imaginary[i] * imaginary[i]);
            return envelope;
        }

        internal static SpectrumData ComputeOneSidedSpectrum(double[] values, double sampleRateHz)
        {
            long desired = (long)values.Length * 4L;
            int fftLength = desired <= 1048576L ? NextPowerOfTwo((int)desired) : NextPowerOfTwo(values.Length);
            double[] real = new double[fftLength];
            double[] imaginary = new double[fftLength];
            Array.Copy(values, real, values.Length);
            FftInPlace(real, imaginary, false);
            int bins = fftLength / 2 + 1;
            double[] frequency = new double[bins];
            double[] power = new double[bins];
            for (int i = 0; i < bins; i++)
            {
                double weight = i == 0 || i == fftLength / 2 ? 1.0 : 2.0;
                frequency[i] = i * sampleRateHz / fftLength;
                power[i] = weight * (real[i] * real[i] + imaginary[i] * imaginary[i]);
            }
            SpectrumData result = new SpectrumData();
            result.FftLength = fftLength;
            result.Frequency = frequency;
            result.Power = power;
            return result;
        }

        internal static double[] ApplyEnergyCoreTaper(double[] values, out int coreStart, out int coreEnd)
        {
            coreStart = EnergyQuantile(values, 0.0005);
            coreEnd = EnergyQuantile(values, 0.9995);
            if (coreEnd <= coreStart) { coreStart = 0; coreEnd = values.Length - 1; }
            double[] output = (double[])values.Clone();
            if (coreStart > 0)
                for (int i = 0; i < coreStart; i++)
                {
                    double phase = (i + 1.0) / (coreStart + 1.0);
                    output[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * phase);
                }
            if (coreEnd < output.Length - 1)
            {
                int length = output.Length - 1 - coreEnd;
                for (int i = coreEnd + 1; i < output.Length; i++)
                {
                    double phase = (output.Length - i) / (length + 1.0);
                    output[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * phase);
                }
            }
            return output;
        }

        internal static int FindFirstArrival(double[] corrected, double sampleRateHz, double noiseSigma, int mainPeakIndex, out double threshold)
        {
            int radius = ClampInt((int)Math.Round(sampleRateHz * 8.0e-9), 1, 6);
            double[] envelope = MovingRms(corrected, radius);
            double peak = 0.0;
            for (int i = 0; i <= Math.Min(mainPeakIndex, envelope.Length - 1); i++) peak = Math.Max(peak, envelope[i]);
            threshold = Math.Max(5.0 * noiseSigma, 0.05 * peak);
            double sustain = 0.60 * threshold;
            for (int i = 0; i <= mainPeakIndex; i++)
            {
                if (envelope[i] < threshold) continue;
                int hits = 0;
                for (int k = i; k <= Math.Min(mainPeakIndex, i + 3); k++) if (envelope[k] >= sustain) hits++;
                if (hits >= 2) return i;
            }
            return mainPeakIndex;
        }

        internal static int FindLargestMagnitude(double[] values, int start, int end)
        {
            int best = start;
            double magnitude = Math.Abs(values[start]);
            for (int i = start + 1; i <= end; i++)
            {
                double current = Math.Abs(values[i]);
                if (current > magnitude) { magnitude = current; best = i; }
            }
            return best;
        }

        internal static int IndexOfMaximum(double[] values)
        {
            int index = 0;
            for (int i = 1; i < values.Length; i++) if (values[i] > values[index]) index = i;
            return index;
        }

        internal static int IndexOfMinimum(double[] values)
        {
            int index = 0;
            for (int i = 1; i < values.Length; i++) if (values[i] < values[index]) index = i;
            return index;
        }

        internal static double LeftCrossing(double[] values, int peak, double threshold)
        {
            for (int i = Math.Min(peak - 1, values.Length - 2); i >= 0; i--)
                if (values[i] <= threshold && values[i + 1] >= threshold)
                    return Interpolate(i, values[i], i + 1, values[i + 1], threshold);
            return double.NaN;
        }

        internal static double RightCrossing(double[] values, int peak, double threshold)
        {
            for (int i = Math.Max(0, peak); i < values.Length - 1; i++)
                if (values[i] >= threshold && values[i + 1] <= threshold)
                    return Interpolate(i, values[i], i + 1, values[i + 1], threshold);
            return double.NaN;
        }

        internal static double DurationNs(double startPoint, double endPoint, double sampleRateHz)
        {
            return Finite(startPoint) && Finite(endPoint) && endPoint >= startPoint
                ? (endPoint - startPoint) * 1.0e9 / sampleRateHz : double.NaN;
        }

        internal static string SingleLine(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        internal static double Clamp(double value, double low, double high) { return Math.Max(low, Math.Min(high, value)); }
        internal static int ClampInt(int value, int low, int high) { return Math.Max(low, Math.Min(high, value)); }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private static double Median(double[] values)
        {
            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[middle] : 0.5 * (sorted[middle - 1] + sorted[middle]);
        }

        private static int EnergyQuantile(double[] values, double quantile)
        {
            double total = 0.0;
            for (int i = 0; i < values.Length; i++) total += values[i] * values[i];
            double target = Clamp(quantile, 0.0, 1.0) * total;
            double cumulative = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                cumulative += values[i] * values[i];
                if (cumulative >= target) return i;
            }
            return values.Length - 1;
        }

        private static double Interpolate(double x0, double y0, double x1, double y1, double target)
        {
            double denominator = y1 - y0;
            return Math.Abs(denominator) < 1.0e-300 ? x0 : x0 + (target - y0) * (x1 - x0) / denominator;
        }

        private static int NextPowerOfTwo(int value)
        {
            if (value < 2) return 2;
            int power = 1;
            while (power < value)
            {
                if (power > 1073741823) throw new ArgumentOutOfRangeException("value", "FFT length is too large.");
                power <<= 1;
            }
            return power;
        }

        private static void FftInPlace(double[] real, double[] imaginary, bool inverse)
        {
            int n = real.Length;
            int j = 0;
            for (int i = 1; i < n; i++)
            {
                int bit = n >> 1;
                while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
                j ^= bit;
                if (i < j)
                {
                    double t = real[i]; real[i] = real[j]; real[j] = t;
                    t = imaginary[i]; imaginary[i] = imaginary[j]; imaginary[j] = t;
                }
            }
            for (int length = 2; length <= n; length <<= 1)
            {
                double angle = (inverse ? 2.0 : -2.0) * Math.PI / length;
                double stepReal = Math.Cos(angle);
                double stepImaginary = Math.Sin(angle);
                int half = length >> 1;
                for (int offset = 0; offset < n; offset += length)
                {
                    double wReal = 1.0;
                    double wImaginary = 0.0;
                    for (int k = 0; k < half; k++)
                    {
                        int even = offset + k;
                        int odd = even + half;
                        double oddReal = real[odd] * wReal - imaginary[odd] * wImaginary;
                        double oddImaginary = real[odd] * wImaginary + imaginary[odd] * wReal;
                        double evenReal = real[even];
                        double evenImaginary = imaginary[even];
                        real[even] = evenReal + oddReal;
                        imaginary[even] = evenImaginary + oddImaginary;
                        real[odd] = evenReal - oddReal;
                        imaginary[odd] = evenImaginary - oddImaginary;
                        double nextReal = wReal * stepReal - wImaginary * stepImaginary;
                        wImaginary = wReal * stepImaginary + wImaginary * stepReal;
                        wReal = nextReal;
                    }
                }
                if (length == n) break;
            }
            if (inverse)
                for (int i = 0; i < n; i++) { real[i] /= n; imaginary[i] /= n; }
        }
    }
}
