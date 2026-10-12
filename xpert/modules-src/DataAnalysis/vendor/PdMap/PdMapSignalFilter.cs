using System;

namespace LabVIEWPdMap
{
    /// <summary>
    /// Dependency-free zero-phase filters used by the PDmap display.  The implementation
    /// intentionally stays inside the control DLL so LabVIEW only has one assembly to load.
    /// </summary>
    internal static class PdMapSignalFilter
    {
        // Keep this implementation byte-for-byte equivalent in its numerical steps to
        // Task 13's PdBandpassFilter: fourth-order Butterworth high-pass followed by
        // fourth-order Butterworth low-pass, odd reflection, and forward/backward passes.
        private const int PrototypeOrder = 4;
        private static readonly double[] ButterworthQ =
        {
            0.541196100146197,
            1.306562964876377
        };

        internal static double[] Apply(double[] source, double sampleRateHz, double[] settings, out string status)
        {
            source = source ?? Array.Empty<double>();
            if (source.Length == 0)
            {
                status = "empty";
                return Array.Empty<double>();
            }

            int mode = ReadInt(settings, 0, 0);
            try
            {
                // Missing acquisition samples must not enter any filter,
                // including wavelet padding/thresholding, as fabricated zero.
                // The existing bypass returns a clone with every gap intact.
                for(int i=0;i<source.Length;i++)if(!IsFinite(source[i]))
                    throw new ArgumentException("Waveform contains NaN or Infinity at index " + i + ".", "waveform");
                if (mode == 1)
                {
                    int order = NormalizeOrder(ReadInt(settings, 3, 4));
                    int levels = ReadInt(settings, 4, 5);
                    int ignore = ReadInt(settings, 5, 2);
                    int appliedLevels;
                    int appliedIgnore;
                    double[] result = WaveletDenoise(source, order, levels, ignore, out appliedLevels, out appliedIgnore);
                    status = "wavelet-db" + order + ";levels=" + appliedLevels +
                        ";ignore=" + appliedIgnore + ";soft-threshold=50%";
                    return result;
                }

                double low = Read(settings, 1, 50000.0);
                double highDefault = IsFinite(sampleRateHz) && sampleRateHz > 0 ? Math.Min(7000000.0, sampleRateHz * 0.45) : 7000000.0;
                double high = Read(settings, 2, highDefault);
                double[] filtered = Task13ZeroPhaseBandpass(source, sampleRateHz, low, high);
                status = "task13-zero-phase-bandpass;" + low.ToString("0.###") + "-" + high.ToString("0.###") + "Hz";
                return filtered;
            }
            catch (Exception ex)
            {
                status = "bypass: " + ex.Message;
                return (double[])source.Clone();
            }
        }

        static double[] Task13ZeroPhaseBandpass(double[] source, double sampleRateHz, double lowHz, double highHz)
        {
            ValidateBandpassArguments(source, sampleRateHz, lowHz, highHz);
            int length = source.Length;
            if (length == 0) return new double[0];

            int padLength = ComputePadLength(length, sampleRateHz, lowHz);
            double[] work = CreateOddReflection(source, padLength);
            Biquad[] sections = DesignSections(sampleRateHz, lowHz, highHz);
            FilterForwardInPlace(work, sections);
            Array.Reverse(work);
            FilterForwardInPlace(work, sections);
            Array.Reverse(work);

            double[] output = new double[length];
            Array.Copy(work, padLength, output, 0, length);
            return output;
        }

        static void ValidateBandpassArguments(double[] waveform, double sampleRateHz, double lowCutoffHz, double highCutoffHz)
        {
            if (waveform == null) throw new ArgumentNullException("waveform", "Waveform cannot be null.");
            if (!IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
                throw new ArgumentOutOfRangeException("sampleRateHz", "Sample rate must be a finite positive value in Hz.");
            if (!IsFinite(lowCutoffHz) || lowCutoffHz <= 0.0)
                throw new ArgumentOutOfRangeException("lowCutoffHz", "Low cutoff must be a finite positive value in Hz.");
            if (!IsFinite(highCutoffHz) || highCutoffHz <= lowCutoffHz)
                throw new ArgumentOutOfRangeException("highCutoffHz", "High cutoff must be finite and greater than the low cutoff.");
            if (highCutoffHz >= sampleRateHz * 0.5)
                throw new ArgumentOutOfRangeException("highCutoffHz", "High cutoff must be lower than the Nyquist frequency (sampleRateHz / 2). ");
            for (int i = 0; i < waveform.Length; i++)
            {
                if (!IsFinite(waveform[i]))
                    throw new ArgumentException("Waveform contains NaN or Infinity at index " + i + ".", "waveform");
            }
        }

        static int ComputePadLength(int length, double sampleRateHz, double lowCutoffHz)
        {
            if (length <= 1) return 0;
            double desired = Math.Ceiling(3.0 * sampleRateHz / lowCutoffHz);
            int pad = desired >= int.MaxValue ? int.MaxValue : (int)desired;
            if (pad < 48) pad = 48;
            return Math.Min(length - 1, pad);
        }

        static double[] CreateOddReflection(double[] source, int padLength)
        {
            int length = source.Length;
            double[] padded = new double[length + 2 * padLength];
            Array.Copy(source, 0, padded, padLength, length);
            if (padLength == 0) return padded;

            double first = source[0];
            double last = source[length - 1];
            for (int k = 1; k <= padLength; k++)
            {
                padded[padLength - k] = 2.0 * first - source[k];
                padded[padLength + length - 1 + k] = 2.0 * last - source[length - 1 - k];
            }
            return padded;
        }

        static Biquad[] DesignSections(double sampleRateHz, double lowCutoffHz, double highCutoffHz)
        {
            double cornerFactor = Math.Pow(Math.Sqrt(2.0) - 1.0, 1.0 / (2.0 * PrototypeOrder));
            double lowK = Math.Tan(Math.PI * lowCutoffHz / sampleRateHz) * cornerFactor;
            double highK = Math.Tan(Math.PI * highCutoffHz / sampleRateHz) / cornerFactor;
            if (!IsFinite(lowK) || !IsFinite(highK) || lowK <= 0.0 || highK <= lowK)
                throw new ArgumentException("The requested cutoff frequencies are too close to the numerical limits for this sample rate.");

            Biquad[] sections = new Biquad[ButterworthQ.Length * 2];
            for (int i = 0; i < ButterworthQ.Length; i++)
            {
                sections[i] = DesignHighPass(lowK, ButterworthQ[i]);
                sections[i + ButterworthQ.Length] = DesignLowPass(highK, ButterworthQ[i]);
            }
            return sections;
        }

        static Biquad DesignLowPass(double k, double q)
        {
            double norm = 1.0 / (1.0 + k / q + k * k);
            double b0 = k * k * norm;
            return new Biquad(b0, 2.0 * b0, b0, 2.0 * (k * k - 1.0) * norm, (1.0 - k / q + k * k) * norm);
        }

        static Biquad DesignHighPass(double k, double q)
        {
            double norm = 1.0 / (1.0 + k / q + k * k);
            return new Biquad(norm, -2.0 * norm, norm, 2.0 * (k * k - 1.0) * norm, (1.0 - k / q + k * k) * norm);
        }

        static void FilterForwardInPlace(double[] samples, Biquad[] sections)
        {
            double constantInput = samples[0];
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                Biquad section = sections[sectionIndex];
                double constantOutput = constantInput * section.DcGain;
                double state1 = constantOutput - section.B0 * constantInput;
                double state2 = section.B2 * constantInput - section.A2 * constantOutput;
                for (int i = 0; i < samples.Length; i++)
                {
                    double input = samples[i];
                    double output = section.B0 * input + state1;
                    state1 = section.B1 * input - section.A1 * output + state2;
                    state2 = section.B2 * input - section.A2 * output;
                    samples[i] = output;
                }
                constantInput = constantOutput;
            }
        }

        static double[] WaveletDenoise(
            double[] source,
            int order,
            int requestedLevels,
            int requestedIgnore,
            out int appliedLevels,
            out int appliedIgnore)
        {
            int n = source.Length;
            if (n < 4)
            {
                appliedLevels = 0;
                appliedIgnore = 0;
                return (double[])source.Clone();
            }
            int maxLevels = 0;
            for (int x = n; x >= 2 && maxLevels < 12; x >>= 1) maxLevels++;
            maxLevels = Math.Max(1, maxLevels - 1);
            int levels = Clamp(requestedLevels, 1, maxLevels);
            appliedLevels = levels;
            int multiple = 1 << levels;
            int paddedLength = ((n + multiple - 1) / multiple) * multiple;
            double[] current = new double[paddedLength];
            for (int i = 0; i < paddedLength; i++)
            {
                int index = i < n ? i : Math.Max(0, n - 2 - (i - n) % Math.Max(1, n - 1));
                current[i] = IsFinite(source[index]) ? source[index] : 0;
            }

            double[] h = ScalingCoefficients(order);
            double[] g = new double[h.Length];
            for (int i = 0; i < h.Length; i++) g[i] = ((i & 1) == 0 ? 1 : -1) * h[h.Length - 1 - i];
            double[][] details = new double[levels][];
            int[] lengths = new int[levels];
            for (int level = 0; level < levels; level++)
            {
                lengths[level] = current.Length;
                int half = current.Length / 2;
                double[] approximation = new double[half];
                double[] detail = new double[half];
                for (int k = 0; k < half; k++)
                    for (int i = 0; i < h.Length; i++)
                    {
                        int index = (2 * k + i) % current.Length;
                        approximation[k] += h[i] * current[index];
                        detail[k] += g[i] * current[index];
                    }
                details[level] = detail;
                current = approximation;
            }

            int ignore = Clamp(requestedIgnore, 0, levels);
            appliedIgnore = ignore;
            for (int level = 0; level < ignore; level++) Array.Clear(details[level], 0, details[level].Length);
            for (int level = ignore; level < levels; level++) ApplySoftThreshold50(details[level]);
            for (int level = levels - 1; level >= 0; level--)
            {
                double[] reconstructed = new double[lengths[level]];
                for (int k = 0; k < current.Length; k++)
                    for (int i = 0; i < h.Length; i++)
                    {
                        int index = (2 * k + i) % reconstructed.Length;
                        reconstructed[index] += h[i] * current[k] + g[i] * details[level][k];
                    }
                current = reconstructed;
            }

            double[] output = new double[n];
            Array.Copy(current, output, n);
            return output;
        }

        static void ApplySoftThreshold50(double[] detail)
        {
            if (detail == null || detail.Length == 0) return;
            double[] absolute = new double[detail.Length];
            for (int i = 0; i < detail.Length; i++) absolute[i] = Math.Abs(detail[i]);
            Array.Sort(absolute);
            double median = (absolute.Length & 1) == 1
                ? absolute[absolute.Length / 2]
                : 0.5 * (absolute[absolute.Length / 2 - 1] + absolute[absolute.Length / 2]);
            double sigma = median / 0.6745;
            double threshold = 0.5 * sigma * Math.Sqrt(2.0 * Math.Log(detail.Length));
            if (!IsFinite(threshold) || threshold <= 0.0) return;
            for (int i = 0; i < detail.Length; i++)
            {
                double magnitude = Math.Abs(detail[i]);
                detail[i] = magnitude <= threshold ? 0.0 : Math.Sign(detail[i]) * (magnitude - threshold);
            }
        }

        static double[] ScalingCoefficients(int order)
        {
            if (order == 2) return new[]
            {
                -0.12940952255126037, 0.2241438680420134,
                 0.8365163037378079, 0.48296291314453416
            };
            if (order == 8) return new[]
            {
                -0.00011747678412476953, 0.0006754494064505693,
                -0.00039174037337694705, -0.004870352993451574,
                 0.008746094047405777, 0.013981027917398282,
                -0.044088253930794755, -0.017369301001807547,
                 0.12874742662047847, 0.0004724845739132828,
                -0.2840155429615469, -0.015829105256349306,
                 0.5853546836542067, 0.6756307362972898,
                 0.31287159091429995, 0.05441584224308161
            };
            return new[]
            {
                -0.010597401785069032, 0.032883011666982945,
                 0.030841381835560764, -0.18703481171888114,
                -0.02798376941698385, 0.6308807679298587,
                 0.7148465705529154, 0.2303778133088964
            };
        }

        static int NormalizeOrder(int value) { return value <= 2 ? 2 : value >= 8 ? 8 : 4; }

        private sealed class Biquad
        {
            internal readonly double B0;
            internal readonly double B1;
            internal readonly double B2;
            internal readonly double A1;
            internal readonly double A2;

            internal Biquad(double b0, double b1, double b2, double a1, double a2)
            {
                B0 = b0;
                B1 = b1;
                B2 = b2;
                A1 = a1;
                A2 = a2;
            }

            internal double DcGain
            {
                get { return (B0 + B1 + B2) / (1.0 + A1 + A2); }
            }
        }

        static double Read(double[] values, int index, double fallback)
        {
            return values != null && index >= 0 && index < values.Length && IsFinite(values[index]) ? values[index] : fallback;
        }
        static int ReadInt(double[] values, int index, int fallback) { return (int)Math.Round(Read(values, index, fallback)); }
        static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static int Clamp(int value, int minimum, int maximum) { return value < minimum ? minimum : value > maximum ? maximum : value; }
    }
}
