using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

// A bounded, stateless review preference, not an acceptance or evidence scorer.
// The caller supplies only hard-gate-eligible alternatives for one ambiguous
// incident and independently qualified anchors, with the incident itself removed.
internal static class AstraSixthSpatialPreference
{
    internal const double NoConcentrationFactor = .8;
    internal const double HighScoreFloor = 70;
    internal const double CloseScoreGap = 5;

    internal sealed class Anchor
    {
        internal readonly double Position;
        internal readonly string Key;
        internal Anchor(double position, string key) { Position = position; Key = key; }
    }

    internal sealed class Candidate
    {
        internal readonly int Id;
        internal readonly double Position, Score;
        internal Candidate(int id, double position, double score)
        { Id = id; Position = position; Score = score; }
    }

    internal sealed class CandidateSupport
    {
        internal readonly int Id, SupportCount;
        internal readonly double Position, RawScore, InCableWindowLength, UniformExpected;
        internal readonly bool IsConcentrated;
        internal CandidateSupport(Candidate candidate, int support, double window, double expected)
        {
            Id = candidate.Id; Position = candidate.Position; RawScore = candidate.Score;
            SupportCount = support; InCableWindowLength = window; UniformExpected = expected;
            IsConcentrated = support >= 3 && support >= 2 * expected;
        }
    }

    internal sealed class Decision
    {
        internal readonly int PreferredId, PreferredSupport, RunnerUpSupport, AnchorCount, CandidateCount;
        internal readonly double Factor, Radius;
        internal readonly string Reason;
        internal readonly CandidateSupport[] Supports;
        internal bool HasConcentrationPreference { get { return Factor == 1; } }
        internal Decision(int preferredId, double factor, string reason, double radius,
            int preferredSupport, int runnerUpSupport, int anchorCount, CandidateSupport[] supports)
        {
            PreferredId = preferredId; Factor = factor; Reason = reason; Radius = radius;
            PreferredSupport = preferredSupport; RunnerUpSupport = runnerUpSupport;
            AnchorCount = anchorCount; CandidateCount = supports.Length; Supports = supports;
        }
    }

    internal static Decision Evaluate(IEnumerable<Candidate> candidates, IEnumerable<Anchor> anchors,
        double cableLength, double metersPerSample)
    {
        bool geometryValid = Finite(cableLength) && cableLength > 0 &&
            Finite(metersPerSample) && metersPerSample >= 0;
        double radius = geometryValid
            ? Math.Max(2 * metersPerSample, Math.Min(10, Math.Max(2, .01 * cableLength)))
            : double.NaN;
        geometryValid = geometryValid && Finite(radius);

        // The helper cannot certify hard gates. This defensive numeric/high-score
        // filter is additional to, never a substitute for, caller-side eligibility.
        var ranked = (candidates ?? Enumerable.Empty<Candidate>())
            .Where(c => c != null && c.Id >= 0 && Finite(c.Score) && c.Score >= HighScoreFloor &&
                Finite(c.Position) && c.Position >= 0 && (!geometryValid || c.Position <= cableLength))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Id).ThenBy(c => c.Position)
            .GroupBy(c => c.Id).Select(g => g.First()).ToArray();
        if (ranked.Length > 0)
            ranked = ranked.Where(c => ranked[0].Score - c.Score <= CloseScoreGap).ToArray();

        if (!geometryValid)
        {
            var unknown = ranked.Select(c => new CandidateSupport(c, 0, double.NaN, double.NaN)).ToArray();
            return Result(ranked, unknown, 0, radius, "invalid_spatial_geometry");
        }

        // One independent event contributes at most once. Identical duplicates
        // collapse; conflicting positions sharing a key are discarded entirely
        // rather than arbitrarily choosing whichever position wins a cluster.
        var independent = (anchors ?? Enumerable.Empty<Anchor>())
            .Where(a => a != null && !String.IsNullOrWhiteSpace(a.Key) &&
                Finite(a.Position) && a.Position >= 0 && a.Position <= cableLength)
            .GroupBy(a => a.Key, StringComparer.Ordinal)
            .Where(g => g.All(a => a.Position == g.First().Position))
            .Select(g => g.First()).OrderBy(a => a.Key, StringComparer.Ordinal).ToArray();
        var supports = ranked.Select(c =>
        {
            double window = Math.Min(cableLength, c.Position + radius) - Math.Max(0, c.Position - radius);
            int support = independent.Count(a => Math.Abs(a.Position - c.Position) <= radius);
            double expected = independent.Length * (window / cableLength);
            return new CandidateSupport(c, support, window, expected);
        }).ToArray();

        if (ranked.Length == 0) return Result(ranked, supports, independent.Length, radius, "no_eligible_close_candidate");
        if (ranked.Length == 1)
            return Result(ranked, supports, independent.Length, radius, "single_eligible_candidate_ambiguity_unresolved");
        if (independent.Length == 0)
            return Result(ranked, supports, 0, radius, "no_independent_spatial_anchors");

        var bySupport = supports.OrderByDescending(s => s.SupportCount)
            .ThenByDescending(s => s.RawScore).ThenBy(s => s.Id).ToArray();
        var best = bySupport[0]; var runnerUp = bySupport[1];
        if (!best.IsConcentrated)
            return Result(ranked, supports, independent.Length, radius, "no_concentrated_spatial_group");
        if (best.SupportCount < 1.5 * runnerUp.SupportCount || best.SupportCount < runnerUp.SupportCount + 2)
            return Result(ranked, supports, independent.Length, radius, "spatial_support_not_clearly_dominant");

        return new Decision(best.Id, 1, "independent_spatial_concentration_preference", radius,
            best.SupportCount, runnerUp.SupportCount, independent.Length, supports);
    }

    private static Decision Result(Candidate[] ranked, CandidateSupport[] supports, int anchorCount,
        double radius, string reason)
    {
        int id = ranked.Length == 0 ? -1 : ranked[0].Id;
        int selected = supports.Length == 0 ? 0 : supports[0].SupportCount;
        int runner = supports.Where(s => s.Id != id).Select(s => s.SupportCount).DefaultIfEmpty(0).Max();
        return new Decision(id, NoConcentrationFactor, reason, radius, selected, runner, anchorCount, supports);
    }

    private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
}
