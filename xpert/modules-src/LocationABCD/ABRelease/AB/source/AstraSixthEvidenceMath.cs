using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed class A6ToleranceResult
{
    internal bool Available, Passed, BasePassed;
    internal double EffectiveTolerancePercent=double.NaN,PropagatedUncertaintyPercent=double.NaN;
    internal double Observed = double.NaN, Reference = double.NaN;
    internal double ErrorPercent = double.NaN, Score = double.NaN;
    internal string Reason = "invalid_input";
}

internal sealed class A6LobeResult
{
    internal bool Valid;
    internal string Reason = "invalid_input";
    // Bounds are inclusive. First/Second mean time order, NOT amplitude rank.
    internal int FirstStart = -1, FirstEnd = -1, FirstPeak = -1;
    internal int SecondStart = -1, SecondEnd = -1, SecondPeak = -1;
    internal int MainStart = -1, MainEnd = -1, MainPeak = -1;
    internal int SecondaryStart = -1, SecondaryEnd = -1, SecondaryPeak = -1;
    internal double FirstEnergy = double.NaN, SecondEnergy = double.NaN;
    internal double FirstFraction = double.NaN, SecondToFirstRatio = double.NaN;
}

// Pure deterministic measurements. Two lobes belong to ONE supplied pulse packet;
// they are not the incident/reflected packets. Endpoint interpretation is an
// auxiliary, calibration-dependent morphology hypothesis, not a physical law.
internal static class A6EvidenceMath
{
    internal const double ZeroToleranceRoundoffPercent = 1e-9;
    private const double LobeSigmaThreshold = 4.5;

    private static bool Finite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal static double NormalizePercent(double value, double defaultValue)
    {
        if (double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Percentage cannot be infinite.");
        if (double.IsNaN(value) || value == -1)
        {
            if (!Finite(defaultValue))
                throw new ArgumentOutOfRangeException(nameof(defaultValue), "Default percentage must be finite.");
            value = defaultValue;
        }
        return Math.Max(0, Math.Min(100, value));
    }

    internal static A6ToleranceResult RelativeError(double observed, double reference, double tolerancePercent)
    {
        var shared = HVX.SignalEvidence.ConfidenceMath.RelativeError(observed, reference, tolerancePercent);
        return new A6ToleranceResult { Observed = observed, Reference = reference,
            Available = shared.Available, Passed = shared.Passed, ErrorPercent = shared.ErrorPercent,
            Score = shared.Score, Reason = shared.Reason };
    }

    private sealed class Lobe
    {
        internal int Start, End, Peak, Count;
        internal double PeakAmplitude, SumSquares, Energy;
    }

    private static bool Significant(Lobe lobe, double noise)
    {
        if (!(lobe.PeakAmplitude > LobeSigmaThreshold * noise)) return false;
        // Noise-only squared samples have mean N*sigma^2 and standard
        // deviation sqrt(2*N)*sigma^2 under the Gaussian-noise reference.
        // This is a conservative measurement guard, not a fitted confidence.
        double noiseEnergyLimit = noise * noise *
            (lobe.Count + LobeSigmaThreshold * Math.Sqrt(2.0 * lobe.Count));
        return Finite(noiseEnergyLimit) && lobe.SumSquares > noiseEnergyLimit;
    }

    internal static A6LobeResult Lobes(double[] raw, int start, int end, int canonicalPeak,
        double baseline, double noise, double fs)
    {
        var result = new A6LobeResult();
        if (raw == null || start < 0 || end >= raw.Length || start > end ||
            canonicalPeak < start || canonicalPeak > end || !Finite(baseline) ||
            !Finite(noise) || noise < 0 || !Finite(fs) || fs <= 0)
            return result;

        // Partition the raw baseline-corrected packet into disjoint nonzero
        // sign runs. Zero-valued samples carry no energy and belong to no run.
        // No smoothing or absolute envelope can merge opposite-polarity lobes.
        var lobes = new List<Lobe>();
        Lobe active = null;
        int previousSign = 0;
        double compensation = 0;
        for (int i = start; i <= end; i++)
        {
            double value = raw[i] - baseline;
            if (!Finite(raw[i]) || !Finite(value))
            {
                result.Reason = "nonfinite_raw_sample_or_baseline_difference";
                return result;
            }
            int sign = value > 0 ? 1 : value < 0 ? -1 : 0;
            if (sign == 0)
            {
                active = null;
                previousSign = 0;
                continue;
            }
            if (active == null || sign != previousSign)
            {
                active = new Lobe { Start = i, End = i, Peak = i };
                lobes.Add(active);
                compensation = 0;
            }
            previousSign = sign;
            active.End = i;
            active.Count++;
            double amplitude = Math.Abs(value);
            if (amplitude > active.PeakAmplitude)
            {
                active.PeakAmplitude = amplitude;
                active.Peak = i;
            }
            // Compensated rectangular integral: dt * sum((raw-baseline)^2).
            double corrected = value * value - compensation;
            double accumulated = active.SumSquares + corrected;
            compensation = (accumulated - active.SumSquares) - corrected;
            active.SumSquares = accumulated;
            if (!Finite(active.SumSquares))
            {
                result.Reason = "nonfinite_lobe_squared_integral";
                return result;
            }
        }

        Lobe main = null;
        foreach (Lobe lobe in lobes)
        {
            lobe.Energy = lobe.SumSquares / fs;
            if (!Finite(lobe.Energy) || lobe.Energy <= 0)
            {
                result.Reason = "nonpositive_or_nonfinite_lobe_energy";
                return result;
            }
            if (canonicalPeak >= lobe.Start && canonicalPeak <= lobe.End) main = lobe;
        }
        if (main == null)
        {
            result.Reason = "canonical_peak_on_baseline_no_main_lobe";
            return result;
        }
        result.MainStart = main.Start;
        result.MainEnd = main.End;
        result.MainPeak = main.Peak;
        if (!Significant(main, noise))
        {
            result.Reason = "main_lobe_below_peak_or_integrated_noise_significance";
            return result;
        }

        Lobe secondary = null;
        foreach (Lobe lobe in lobes)
        {
            if (ReferenceEquals(lobe, main) || !Significant(lobe, noise)) continue;
            // Secondary MAIN PEAK: largest absolute peak, then energy, then
            // earliest position. This is deterministic even for exact ties.
            if (secondary == null || lobe.PeakAmplitude > secondary.PeakAmplitude ||
                (lobe.PeakAmplitude == secondary.PeakAmplitude && lobe.Energy > secondary.Energy) ||
                (lobe.PeakAmplitude == secondary.PeakAmplitude && lobe.Energy == secondary.Energy && lobe.Peak < secondary.Peak))
                secondary = lobe;
        }
        if (secondary == null)
        {
            result.Reason = "no_significant_second_lobe";
            return result;
        }

        result.SecondaryStart = secondary.Start;
        result.SecondaryEnd = secondary.End;
        result.SecondaryPeak = secondary.Peak;
        Lobe first = main.Start < secondary.Start ? main : secondary;
        Lobe second = ReferenceEquals(first, main) ? secondary : main;
        double ratio = second.Energy / first.Energy;
        double fraction = first.Energy >= second.Energy
            ? 1 / (1 + ratio)
            : (first.Energy / second.Energy) / (1 + first.Energy / second.Energy);
        if (!Finite(ratio) || !Finite(fraction))
        {
            result.Reason = "lobe_energy_ratio_not_representable";
            return result;
        }
        result.FirstStart = first.Start;
        result.FirstEnd = first.End;
        result.FirstPeak = first.Peak;
        result.SecondStart = second.Start;
        result.SecondEnd = second.End;
        result.SecondPeak = second.Peak;
        result.FirstEnergy = first.Energy;
        result.SecondEnergy = second.Energy;
        result.FirstFraction = fraction;
        result.SecondToFirstRatio = ratio;
        result.Valid = true;
        result.Reason = "valid_two_disjoint_significant_raw_lobes";
        return result;
    }
}
