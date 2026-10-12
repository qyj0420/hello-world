using System;
using System.Collections.Generic;

namespace Xiongxin.OWTS.Desktop
{
    // A deterministic demonstration, not an electrical acceptance model. Every
    // plotted pulse and every statistics row comes from this same event list.
    internal sealed class DampedPressureSimulation
    {
        internal const double SampleRateHz = 200000;
        internal const double FrequencyHz = 500;
        internal const double LossPercent = 3.2;
        // Explicit virtual ADC settings for the offline generator only. These are
        // never device calibration, a PicoScope range, or measured metadata.
        internal const int VirtualResolutionBits = 16;
        internal const int VirtualRangeCode = 0;
        internal const double VirtualFullScaleVolts = 1.0;
        internal const double VirtualPicocoulombsPerVolt = 1000.0;
        internal readonly double[] Voltage = new double[12000];
        internal readonly double[] Discharge = new double[12000];
        internal readonly List<Pulse> Pulses = new List<Pulse>();
        internal double MaximumCharge;
        internal sealed class Pulse
        {
            internal int Sample, Cycle;
            internal double PhaseDegrees, Charge, Position;
        }

        internal static DampedPressureSimulation Create(double peakKv, double lengthM, int phaseCode, bool positive)
        {
            if (!FinitePositive(peakKv) || !FinitePositive(lengthM) || phaseCode < 1 || phaseCode > 3)
                throw new ArgumentOutOfRangeException("Invalid simulation parameters.");
            var result = new DampedPressureSimulation();
            double damping = Math.PI * FrequencyHz * LossPercent / 100;
            double sign = positive ? 1 : -1;
            for (int i = 0; i < result.Voltage.Length; i++)
            {
                double t = i / SampleRateHz;
                result.Voltage[i] = sign * peakKv * Math.Exp(-damping * t) * Math.Sin(2 * Math.PI * FrequencyHz * t);
            }
            const double extinctionFraction = .24;
            int cycles = (int)Math.Ceiling(result.Voltage.Length / SampleRateHz * FrequencyHz);
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                double envelope = Math.Exp(-damping * cycle / FrequencyHz);
                double activity = Math.Max(0, (envelope - extinctionFraction) / (1 - extinctionFraction));
                int countPerHalfCycle = (int)Math.Ceiling(7 * activity);
                for (int half = 0; half < 2; half++)
                for (int pulse = 0; pulse < countPerHalfCycle; pulse++)
                {
                    double degrees = 26 + 46.0 * (pulse + .5) / countPerHalfCycle + half * 180;
                    int sample = (int)Math.Round((cycle + degrees / 360) * SampleRateHz / FrequencyHz);
                    if (sample >= result.Voltage.Length) continue;
                    double time = sample / SampleRateHz;
                    double localEnvelope = Math.Exp(-damping * time);
                    if (localEnvelope <= extinctionFraction) continue;
                    double charge = 180 * (localEnvelope - extinctionFraction) / (1 - extinctionFraction);
                    // Alternating polarity and two stable synthetic locations.
                    charge = QuantizeVirtualCharge(charge * (half == 0 ? 1 : -.82) * sign);
                    double location = lengthM * (half == 0 ? .205 : .642);
                    location += lengthM * .003 * Math.Sin((cycle * 7 + pulse) * 1.913);
                    double phase = (sample * FrequencyHz / SampleRateHz * 360 + (positive ? 0 : 180)) % 360;
                    var entry = new Pulse { Sample = sample, Cycle = cycle, PhaseDegrees = phase,
                        Charge = charge, Position = location };
                    result.Pulses.Add(entry);
                    result.Discharge[sample] = charge;
                    result.MaximumCharge = Math.Max(result.MaximumCharge, Math.Abs(charge));
                }
            }
            return result;
        }

        internal static DampedPressureSimulation CreateZeroNoise(int phaseCode)
        {
            if (phaseCode < 1 || phaseCode > 3) throw new ArgumentOutOfRangeException("phaseCode");
            var result = new DampedPressureSimulation();
            // A repeatable, non-zero reference trace, not simulated PD events.
            // Voltage remains exactly zero and the event collection stays empty.
            for (int i = 0; i < result.Discharge.Length; i++)
            {
                double t = i / SampleRateHz;
                result.Discharge[i] = QuantizeVirtualCharge(
                    .6 * Math.Sin(2 * Math.PI * 13700 * t + phaseCode * .37) +
                    .25 * Math.Sin(2 * Math.PI * 31100 * t) +
                    .08 * Math.Cos(2 * Math.PI * 83 * t));
            }
            return result;
        }

        private static double QuantizeVirtualCharge(double charge)
        {
            double counts = (1 << (VirtualResolutionBits - 1)) - 1;
            double fullScaleCharge = VirtualFullScaleVolts * VirtualPicocoulombsPerVolt;
            return Math.Round(charge / fullScaleCharge * counts) / counts * fullScaleCharge;
        }

        internal double[,] Matrix(double lengthM, int phaseCode)
        {
            var matrix = new double[Pulses.Count, 36];
            for (int row = 0; row < Pulses.Count; row++)
            {
                var pulse = Pulses[row];
                for (int column = 0; column < 36; column++) matrix[row, column] = Double.NaN;
                matrix[row, 0] = phaseCode;
                matrix[row, 1] = pulse.Sample / SampleRateHz;
                matrix[row, 2] = Math.Abs(Voltage[pulse.Sample]);
                matrix[row, 3] = pulse.PhaseDegrees;
                matrix[row, 5] = lengthM;
                matrix[row, 13] = pulse.Position;
                matrix[row, 15] = pulse.Charge;
                matrix[row, 16] = pulse.Charge;
                matrix[row, 23] = FrequencyHz;
                matrix[row, 33] = .92;
                matrix[row, 35] = 1;
            }
            return matrix;
        }
        static bool FinitePositive(double n) { return !Double.IsNaN(n) && !Double.IsInfinity(n) && n > 0; }
    }
}
