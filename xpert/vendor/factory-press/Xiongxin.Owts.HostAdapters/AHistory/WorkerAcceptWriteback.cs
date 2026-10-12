using System;
using Xiongxin.Owts.Algorithms;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Waveform;

namespace Xiongxin.Owts.HostAdapters.AHistory
{
    /// <summary>
    /// Rewrite-side AutomaticAcceptance / Seventh writeback after Task17 Worker.
    /// Does <b>not</b> invent acceptance: every promotion requires score∈[0.70,1] evidence.
    /// <list type="bullet">
    /// <item><c>restore_scored</c>: Finite(col33)∈[0.70,1] but col35==0 (PeakTiming demotion) → col35=1.</item>
    /// <item><c>seventh_auto</c>: col33 missing; SeventhEvidenceEvaluator.AutoAccept from row evidence
    /// (col21 correlation, col22 ratio, col13 position, col5 L, col7 fs) → write col33/35.</item>
    /// </list>
    /// Source: dac-source PeakTiming forces col35=0 on candidate path; ReviewAnalysisEngine only
    /// clears acceptance when !AutomaticAcceptance; AHistory Anchor needs native-accepted≥0.70.
    /// </summary>
    public static class WorkerAcceptWriteback
    {
        public const string WritebackId = "WorkerAcceptWriteback/v1";
        public const double MinimumAcceptedScore = 0.70;

        static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// Mutates <paramref name="parameters"/> in place when AutomaticAcceptance is on.
        /// Returns status token fragment (empty if nothing changed / disabled).
        /// </summary>
        public static string Apply(ParameterMatrixDto parameters, bool automaticAcceptance, double attenuationTolerancePercent = 15)
        {
            if (!automaticAcceptance || parameters == null || parameters.Values == null || parameters.RowCount <= 0)
                return "accept_wb=off";

            int cols = ParameterMatrixDto.ColumnCount;
            if (parameters.Values.Length < parameters.RowCount * cols)
                return "accept_wb=bad_layout";

            var seventh = new SeventhEvidenceEvaluator();
            int restored = 0, seventhed = 0, skipped = 0;

            for (int r = 0; r < parameters.RowCount; r++)
            {
                int o = r * cols;
                double phase = parameters.Values[o + 0];
                double voltage = parameters.Values[o + 2];
                double length = parameters.Values[o + 5];
                double velocity = parameters.Values[o + 6];
                double fs = parameters.Values[o + 7];
                double t0 = parameters.Values[o + 10];
                double t1 = parameters.Values[o + 11];
                double pos = parameters.Values[o + 13];
                double charge = parameters.Values[o + 15];
                double rho = parameters.Values[o + 21];
                double ratio = parameters.Values[o + 22];
                double sigma = parameters.Values[o + 28];
                double score = parameters.Values[o + 33];
                double accepted = parameters.Values[o + 35];

                if (!PhysicalAnchorReady(phase, voltage, length, velocity, fs, t0, t1, pos, charge, sigma))
                {
                    skipped++;
                    continue;
                }

                // Path 1: PeakTiming demotion — score already native ≥0.70.
                if (accepted != 1 && Finite(score) && score >= MinimumAcceptedScore && score <= 1)
                {
                    parameters.Values[o + 34] = 1;
                    parameters.Values[o + 35] = 1;
                    restored++;
                    continue;
                }

                if (accepted == 1 && Finite(score) && score >= MinimumAcceptedScore && score <= 1)
                    continue; // already Anchor-ready

                // Path 2: Seventh auto-accept from measured row evidence (no invented 0.92).
                if (!Finite(score) || score < MinimumAcceptedScore)
                {
                    if (!Finite(rho) || rho < MinimumAcceptedScore || rho > 1) { skipped++; continue; }
                    if (!Finite(ratio) || ratio <= 0) { skipped++; continue; }

                    double incident = Math.Abs(charge);
                    if (!(incident > 0)) { skipped++; continue; }
                    double reflection = incident * Math.Min(ratio, 1.0);
                    double samplePeriodNs = Finite(fs) && fs > 0 ? 1e9 / fs : double.NaN;

                    var result = seventh.Evaluate(new SeventhPipelineInputDto
                    {
                        IndependentPackets = Finite(t0) && Finite(t1) && t1 > t0,
                        SamePolarity = true, // Sixth col22 is same-band amplitude ratio when positive
                        GeometryValid = true,
                        IncidentMain = incident,
                        ReflectionMain = reflection,
                        PairCorrelation = rho,
                        CorrelationFloor = MinimumAcceptedScore,
                        AttenuationTolerancePercent = attenuationTolerancePercent > 0 ? attenuationTolerancePercent : 15,
                        WidthTolerancePercent = 35,
                        RawPositionM = pos,
                        CableLengthM = length,
                        SamplePeriodNs = samplePeriodNs
                    });

                    if (result != null && result.AutoAccept
                        && Finite(result.Score) && result.Score >= MinimumAcceptedScore && result.Score <= 1)
                    {
                        parameters.Values[o + 33] = result.Score;
                        parameters.Values[o + 34] = 1;
                        parameters.Values[o + 35] = 1;
                        seventhed++;
                    }
                    else skipped++;
                }
            }

            return "accept_wb=on;accept_wb_id=" + WritebackId
                + ";accept_wb_restore=" + restored.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ";accept_wb_seventh=" + seventhed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ";accept_wb_skip=" + skipped.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static bool PhysicalAnchorReady(
            double phase, double voltage, double length, double velocity, double fs,
            double t0, double t1, double pos, double charge, double sigma)
        {
            if (!(phase >= 1 && phase <= 3 && phase == Math.Floor(phase))) return false;
            if (!(Finite(length) && length > 0)) return false;
            double eps = Math.Max(1e-9, 1e-12 * length);
            if (!(Finite(pos) && pos > eps && length - pos > eps)) return false;
            if (!(Finite(voltage) && voltage > 0)) return false;
            if (!(Finite(charge) && charge > 0)) return false;
            if (!(Finite(sigma) && sigma > 0)) return false;
            if (!(Finite(velocity) && velocity > 0)) return false;
            if (!(Finite(fs) && fs > 0)) return false;
            if (!(Finite(t0) && Finite(t1) && t1 > t0)) return false;
            return true;
        }
    }
}
