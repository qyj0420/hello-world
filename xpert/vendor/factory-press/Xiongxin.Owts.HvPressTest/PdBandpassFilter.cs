// AUTHORITATIVE: D:\\PD_AI_LabVIEW2026_Project\\deliverables\\HVX_PD_BandpassFilter_算法移交包_20261012\\PdBandpassFilter.cs
// SHA256 afc3b6e2c8e135484d70b0ed7d12aa556e7444076f66bbb07d8bf79aefabc983
// Embedded into HvPressTest: namespace only; algorithm body unchanged. Do not invent filters.

using System;


namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// LabVIEW-friendly, stateless bandpass filter for partial-discharge waveforms.
    /// </summary>
    public sealed class PdBandpassFilter
    {
        private const int PrototypeOrder = 4;
        private static readonly double[] ButterworthQ =
        {
            0.541196100146197,
            1.306562964876377
        };

        public PdBandpassFilter()
        {
        }

        /// <summary>
        /// Gets the managed API version.
        /// </summary>
        public string Version
        {
            get { return "1.0.0"; }
        }

        /// <summary>
        /// Applies an offline zero-phase bandpass filter. The output length equals the input length.
        /// All frequency parameters are in Hz.
        /// </summary>
        public double[] Filter(
            double[] waveform,
            double sampleRateHz,
            double lowCutoffHz,
            double highCutoffHz)
        {
            ValidateArguments(waveform, sampleRateHz, lowCutoffHz, highCutoffHz);

            int length = waveform.Length;
            if (length == 0)
            {
                return new double[0];
            }

            // Odd reflection suppresses start/end transients without changing the physical filter.
            int padLength = ComputePadLength(length, sampleRateHz, lowCutoffHz);
            double[] work = CreateOddReflection(waveform, padLength);

            Biquad[] sections = DesignSections(sampleRateHz, lowCutoffHz, highCutoffHz);
            FilterForwardInPlace(work, sections);
            Array.Reverse(work);
            FilterForwardInPlace(work, sections);
            Array.Reverse(work);

            double[] output = new double[length];
            Array.Copy(work, padLength, output, 0, length);
            return output;
        }

        private static void ValidateArguments(
            double[] waveform,
            double sampleRateHz,
            double lowCutoffHz,
            double highCutoffHz)
        {
            if (waveform == null)
            {
                throw new ArgumentNullException("waveform", "Waveform cannot be null.");
            }

            if (!IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
            {
                throw new ArgumentOutOfRangeException("sampleRateHz", "Sample rate must be a finite positive value in Hz.");
            }

            if (!IsFinite(lowCutoffHz) || lowCutoffHz <= 0.0)
            {
                throw new ArgumentOutOfRangeException("lowCutoffHz", "Low cutoff must be a finite positive value in Hz.");
            }

            if (!IsFinite(highCutoffHz) || highCutoffHz <= lowCutoffHz)
            {
                throw new ArgumentOutOfRangeException("highCutoffHz", "High cutoff must be finite and greater than the low cutoff.");
            }

            if (highCutoffHz >= sampleRateHz * 0.5)
            {
                throw new ArgumentOutOfRangeException("highCutoffHz", "High cutoff must be lower than the Nyquist frequency (sampleRateHz / 2). ");
            }

            for (int i = 0; i < waveform.Length; i++)
            {
                if (!IsFinite(waveform[i]))
                {
                    throw new ArgumentException("Waveform contains NaN or Infinity at index " + i + ".", "waveform");
                }
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static int ComputePadLength(int length, double sampleRateHz, double lowCutoffHz)
        {
            if (length <= 1)
            {
                return 0;
            }

            // About three cycles at the low cutoff covers the slowest fourth-order pole decay.
            double desired = Math.Ceiling(3.0 * sampleRateHz / lowCutoffHz);
            int pad = desired >= int.MaxValue ? int.MaxValue : (int)desired;
            if (pad < 48)
            {
                pad = 48;
            }

            return Math.Min(length - 1, pad);
        }

        private static double[] CreateOddReflection(double[] source, int padLength)
        {
            int length = source.Length;
            double[] padded = new double[length + 2 * padLength];
            Array.Copy(source, 0, padded, padLength, length);

            if (padLength == 0)
            {
                return padded;
            }

            double first = source[0];
            double last = source[length - 1];
            for (int k = 1; k <= padLength; k++)
            {
                padded[padLength - k] = 2.0 * first - source[k];
                padded[padLength + length - 1 + k] = 2.0 * last - source[length - 1 - k];
            }

            return padded;
        }

        private static Biquad[] DesignSections(
            double sampleRateHz,
            double lowCutoffHz,
            double highCutoffHz)
        {
            // A forward/backward pass squares the single-pass magnitude. Compensate the
            // analog-prewarped corner so the final zero-phase response is -3 dB at the
            // requested physical cutoff frequencies.
            double cornerFactor = Math.Pow(Math.Sqrt(2.0) - 1.0, 1.0 / (2.0 * PrototypeOrder));
            double lowK = Math.Tan(Math.PI * lowCutoffHz / sampleRateHz) * cornerFactor;
            double highK = Math.Tan(Math.PI * highCutoffHz / sampleRateHz) / cornerFactor;

            if (!IsFinite(lowK) || !IsFinite(highK) || lowK <= 0.0 || highK <= lowK)
            {
                throw new ArgumentException("The requested cutoff frequencies are too close to the numerical limits for this sample rate.");
            }

            Biquad[] sections = new Biquad[ButterworthQ.Length * 2];
            for (int i = 0; i < ButterworthQ.Length; i++)
            {
                sections[i] = DesignHighPass(lowK, ButterworthQ[i]);
                sections[i + ButterworthQ.Length] = DesignLowPass(highK, ButterworthQ[i]);
            }

            return sections;
        }

        private static Biquad DesignLowPass(double k, double q)
        {
            double norm = 1.0 / (1.0 + k / q + k * k);
            double b0 = k * k * norm;
            return new Biquad(
                b0,
                2.0 * b0,
                b0,
                2.0 * (k * k - 1.0) * norm,
                (1.0 - k / q + k * k) * norm);
        }

        private static Biquad DesignHighPass(double k, double q)
        {
            double norm = 1.0 / (1.0 + k / q + k * k);
            return new Biquad(
                norm,
                -2.0 * norm,
                norm,
                2.0 * (k * k - 1.0) * norm,
                (1.0 - k / q + k * k) * norm);
        }

        private static void FilterForwardInPlace(double[] samples, Biquad[] sections)
        {
            double constantInput = samples[0];
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                Biquad section = sections[sectionIndex];
                double gain = section.DcGain;
                double constantOutput = constantInput * gain;
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
                get
                {
                    return (B0 + B1 + B2) / (1.0 + A1 + A2);
                }
            }
        }
    }
}
