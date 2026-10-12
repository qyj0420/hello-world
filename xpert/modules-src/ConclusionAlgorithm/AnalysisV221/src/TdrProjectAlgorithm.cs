using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HVX.Location
{
    internal sealed class TdrProjectCandidate
    {
        internal int Phase;
        internal int InputRow;
        internal long RecordId;
        internal long DirectIndex;
        internal long ReflectedIndex;
        internal long EventFamilyRootIndex;
        internal double Charge;
        internal double Position;
        internal double UnclippedPosition;
        internal double Angle;
        internal double Voltage;
        internal double Application;
        internal double Width;
        internal double Bandwidth;
        internal double DeterministicPairScore;
        internal double ReflectionUniqueness;
        internal double BestSecondMargin;
        internal double MultipleReflectionAmbiguity;
        internal double DirectSnr;
        internal double ReflectedSnr;
        internal bool CalSystemResponseMatch;
        internal double MultipathRisk;
        internal bool SelectedAsEventPair;
        internal bool RescuedCandidate;
        internal double PositionResolution;
        internal double PairPhysics;
        internal int PreliminaryMode;
        internal double AssignmentScore;
    }

    internal sealed class TdrPreliminaryMode
    {
        internal int Id;
        internal List<TdrProjectCandidate> Candidates = new List<TdrProjectCandidate>();
        internal List<TdrProjectCandidate> Votes = new List<TdrProjectCandidate>();
        internal double Center;
        internal double EffectiveResolution;
        internal int PreliminarySupport;
    }

    internal sealed class TdrProjectRegion
    {
        internal int Id;
        internal List<TdrProjectCandidate> Votes = new List<TdrProjectCandidate>();
        internal List<TdrPreliminaryMode> Subpeaks = new List<TdrPreliminaryMode>();
        internal int PhaseMask;
        internal double Center;
        internal double Low;
        internal double High;
        internal double EffectiveResolution;
        internal int SupportRecordCount;
        internal int EffectiveValidLocCount;
        internal int StandardAnchorRecordCount;
        internal double SupportRate;
        internal double RescuedVoteFraction;
        internal double PositionMad;
        internal double PositionP90;
        internal double PositionConcentration;
        internal double LocalProminence;
        internal double BaseRegionScore;
        internal double TdrSoftDelta;
        internal double FinalEngineeringScore;
        internal double MedianPairPhysics;
        internal double Q25PairPhysics;
        internal double MedianReflectionUniqueness;
        internal double LowReflectedSnrFraction;
        internal double MedianAmbiguity;
        internal double CalResponseFraction;
        internal double MultipathRiskFraction;
        internal double MedianCharge;
        internal double MaxCharge;
        internal double NearestJoint;
        internal double DistanceToJoint;
        internal int EndpointCode;
        internal int StatusReasonBits;
        internal int StatusCode;
        internal int ColorCode;
        internal bool UnresolvedBand;
        internal int SubpeakCount;
        internal string Status;
        internal double BoundaryClippedFraction;
        internal bool LegacyBaseMatched;
    }

    internal sealed class LegacyLocationScore
    {
        internal int PhaseMask;
        internal double Center;
        internal double Low;
        internal double High;
        internal double Possibility;
    }

    /// <summary>
    /// Project-level adjudication for compact results produced by independent
    /// single-LOC TDR analysis. The established conclusion Analyzer supplies the
    /// base interval score; TDR physics can only make a bounded soft adjustment.
    /// </summary>
    internal sealed class TdrProjectAnalyzer
    {
        private const int InputColumns = 25;
        private const double MaxExactIntegerInDouble = 9007199254740992.0;
        private readonly RegionModel _model;

        internal TdrProjectAnalyzer(RegionModel model)
        {
            _model = model;
        }

        internal TdrProjectAnalysisResult Run(
            double[] dataA, int rowsA,
            double[] dataB, int rowsB,
            double[] dataC, int rowsC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double cableLengthM,
            double locationErrorPercent,
            double distanceResolutionM,
            int validLocCountA,
            int validLocCountB,
            int validLocCountC,
            double operatingPhaseVoltageKv,
            double maxTdrInfluencePoints)
        {
            cableLengthM = LocatorEngine.ResolveCableLengthFromJointEndpoints(jointsA, jointsB, jointsC);
            ValidateScalar(cableLengthM, "cableLengthM", 0.0, 1000000.0, false);
            ValidateScalar(locationErrorPercent, "locationErrorPercent", 0.0, 20.0, true);
            ValidateScalar(distanceResolutionM, "distanceResolutionM", 0.0, 100000.0, true);
            ValidateScalar(operatingPhaseVoltageKv, "operatingPhaseVoltageKv", 0.0, 1000.0, true);
            ValidateScalar(maxTdrInfluencePoints, "maxTdrInfluencePoints", 0.0, 8.0, true);
            if (cableLengthM <= 0.0) throw new ArgumentException("电缆长度必须大于0");
            int[] validLocCounts = new int[] { validLocCountA, validLocCountB, validLocCountC };
            if (validLocCounts.Any(delegate(int value) { return value < 0; }))
                throw new ArgumentOutOfRangeException("validLocCount", "valid LOC counts cannot be negative");
            int globalTotalValidLocCount = checked(validLocCountA + validLocCountB + validLocCountC);
            if (globalTotalValidLocCount <= 0) throw new ArgumentOutOfRangeException("validLocCount", "global valid LOC count must be positive");
            if (distanceResolutionM <= 0.0) distanceResolutionM = 0.1;
            if (maxTdrInfluencePoints <= 0.0) maxTdrInfluencePoints = 6.0;

            HashSet<long>[] inputRecordIds = new HashSet<long>[] {
                ValidateMatrixIdentifiers(dataA, rowsA, "A"),
                ValidateMatrixIdentifiers(dataB, rowsB, "B"),
                ValidateMatrixIdentifiers(dataC, rowsC, "C")
            };
            if (inputRecordIds[0].Overlaps(inputRecordIds[1]) ||
                inputRecordIds[0].Overlaps(inputRecordIds[2]) ||
                inputRecordIds[1].Overlaps(inputRecordIds[2]))
                throw new ArgumentException("record_id must be globally unique to one phase matrix");

            int phase;
            for (phase = 0; phase < 3; phase++)
                if (inputRecordIds[phase].Count > validLocCounts[phase])
                    throw new ArgumentException("validLocCount" + PhaseName(phase) + " is smaller than distinct input record count");

            List<TdrProjectCandidate> all = new List<TdrProjectCandidate>();
            all.AddRange(ReadPhase(dataA, rowsA, 0, cableLengthM, distanceResolutionM));
            all.AddRange(ReadPhase(dataB, rowsB, 1, cableLengthM, distanceResolutionM));
            all.AddRange(ReadPhase(dataC, rowsC, 2, cableLengthM, distanceResolutionM));

            // Eligibility is physical, not statistical: only event-family roots may
            // become incident pulses, and a row must be either a selected pair or an
            // explicitly rescued candidate. CAL/multipath evidence remains available
            // as a soft penalty instead of being used as a hard rejection here.
            List<TdrProjectCandidate> eligible = all.Where(delegate(TdrProjectCandidate c)
            {
                return (c.SelectedAsEventPair || c.RescuedCandidate) &&
                    c.Position >= 0.0 && c.Position <= cableLengthM;
            }).ToList();

            TdrProjectAnalysisResult result = new TdrProjectAnalysisResult();
            result.Ok = true;
            result.ModelVersion = _model.Version;
            result.TotalInputRows = rowsA + rowsB + rowsC;
            result.EligibleCandidateRows = eligible.Count;
            result.GlobalTotalValidLocCount = globalTotalValidLocCount;
            result.ValidLocCountA = validLocCountA;
            result.ValidLocCountB = validLocCountB;
            result.ValidLocCountC = validLocCountC;
            result.DistanceResolutionM = distanceResolutionM;
            result.MaxTdrInfluencePoints = maxTdrInfluencePoints;
            result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;

            if (eligible.Count == 0)
            {
                result.SummaryText = "项目终审未收到可用的事件族根配对候选；总有效LOC数为" +
                    globalTotalValidLocCount.ToString(CultureInfo.InvariantCulture) +
                    "。输出的工程可能性不是经标定的统计概率。";
                return result;
            }

            List<TdrPreliminaryMode> modes = BuildPreliminaryModes(
                eligible, cableLengthM, locationErrorPercent, distanceResolutionM);
            AssignUniqueVotes(modes, validLocCounts);
            List<TdrProjectRegion> regions = BuildFinalRegions(
                modes, cableLengthM, locationErrorPercent, distanceResolutionM);

            double[][] joints = new double[][] {
                NormalizeJoints(jointsA, cableLengthM),
                NormalizeJoints(jointsB, cableLengthM),
                NormalizeJoints(jointsC, cableLengthM)
            };
            ScoreRegions(
                regions, eligible, joints, cableLengthM, locationErrorPercent,
                distanceResolutionM, validLocCounts, maxTdrInfluencePoints);
            FillRegionMatrix(result, regions);
            FillSelectedVoteMatrix(result, regions);
            result.SummaryText = BuildSummary(result, regions);
            return result;
        }


        private static HashSet<long> ValidateMatrixIdentifiers(
            double[] flat, int rows, string phaseName)
        {
            if (rows < 0) throw new ArgumentOutOfRangeException("rows" + phaseName);
            if (flat == null) flat = new double[0];
            if (flat.Length < rows * InputColumns)
                throw new ArgumentException(phaseName + " phase array is shorter than rows x 25");
            HashSet<long> recordIds = new HashSet<long>();
            int row;
            for (row = 0; row < rows; row++)
            {
                int offset = row * InputColumns;
                long recordId = ExactInteger(flat[offset + 7], "record_id");
                long directIndex = ExactInteger(flat[offset + 9], "direct_index");
                long reflectedIndex = ExactInteger(flat[offset + 10], "reflected_index");
                ExactInteger(flat[offset + 11], "event_family_root_index");
                if (reflectedIndex <= directIndex)
                    throw new ArgumentException("reflected_index must be greater than direct_index");
                recordIds.Add(recordId);
            }
            return recordIds;
        }

        private static List<TdrProjectCandidate> ReadPhase(
            double[] flat, int rows, int phase, double cableLengthM, double defaultResolution)
        {
            if (rows < 0) throw new ArgumentOutOfRangeException("rows");
            if (flat == null) flat = new double[0];
            if (flat.Length < rows * InputColumns)
                throw new ArgumentException(PhaseName(phase) + "相数组长度小于rows×25");
            List<TdrProjectCandidate> output = new List<TdrProjectCandidate>();
            int row;
            for (row = 0; row < rows; row++)
            {
                int offset = row * InputColumns;
                double voltage = RequireFinite(flat[offset + 3], "voltage_kv");
                if (voltage <= 0.05) continue;
                bool isRoot = Flag(flat[offset + 20], "direct_is_event_family_root");
                bool previousEcho = Flag(flat[offset + 21], "direct_likely_previous_echo");
                if (!isRoot || previousEcho) continue;

                TdrProjectCandidate c = new TdrProjectCandidate();
                c.Phase = phase;
                c.InputRow = row;
                c.Charge = Math.Abs(RequireFinite(flat[offset + 0], "charge_pc"));
                c.Position = RequireFinite(flat[offset + 1], "position_m");
                c.Angle = RequireFinite(flat[offset + 2], "phase_degree");
                c.Voltage = voltage;
                c.Application = RequireFinite(flat[offset + 4], "application_order");
                c.Width = Math.Abs(RequireFinite(flat[offset + 5], "equivalent_width"));
                c.Bandwidth = Math.Abs(RequireFinite(flat[offset + 6], "equivalent_bandwidth"));
                c.RecordId = ExactInteger(flat[offset + 7], "record_id");
                c.UnclippedPosition = RequireFinite(flat[offset + 8], "position_unclipped_m");
                c.DirectIndex = ExactInteger(flat[offset + 9], "direct_index");
                c.ReflectedIndex = ExactInteger(flat[offset + 10], "reflected_index");
                if (c.ReflectedIndex <= c.DirectIndex)
                    throw new ArgumentException("reflected_index must be greater than direct_index");
                c.EventFamilyRootIndex = ExactInteger(flat[offset + 11], "event_family_root_index");
                c.DeterministicPairScore = Unit(flat[offset + 12]);
                c.ReflectionUniqueness = Unit(flat[offset + 13]);
                c.BestSecondMargin = Unit(flat[offset + 14]);
                c.MultipleReflectionAmbiguity = Unit(flat[offset + 15]);
                c.DirectSnr = Math.Max(0.0, RequireFinite(flat[offset + 16], "direct_snr"));
                c.ReflectedSnr = Math.Max(0.0, RequireFinite(flat[offset + 17], "reflected_snr"));
                c.CalSystemResponseMatch = Flag(flat[offset + 18], "cal_system_response_match");
                c.MultipathRisk = Unit(flat[offset + 19]);
                c.SelectedAsEventPair = Flag(flat[offset + 22], "selected_as_event_pair");
                c.RescuedCandidate = Flag(flat[offset + 23], "rescued_candidate");
                double resolution = RequireFinite(flat[offset + 24], "position_resolution_m");
                c.PositionResolution = resolution > 0.0 ? resolution : defaultResolution;
                c.PairPhysics = PairPhysics(c);
                if (c.Position < -Math.Max(defaultResolution, c.PositionResolution) ||
                    c.Position > cableLengthM + Math.Max(defaultResolution, c.PositionResolution)) continue;
                c.Position = Clamp(c.Position, 0.0, cableLengthM);
                output.Add(c);
            }
            return output;
        }

        private static double PairPhysics(TdrProjectCandidate c)
        {
            double snr = Clamp(c.ReflectedSnr / 10.0, 0.0, 1.0);
            return Clamp(
                0.45 * c.DeterministicPairScore +
                0.20 * c.ReflectionUniqueness +
                0.15 * c.BestSecondMargin +
                0.10 * snr +
                0.10 * (1.0 - c.MultipleReflectionAmbiguity),
                0.0, 1.0);
        }

        private static List<TdrPreliminaryMode> BuildPreliminaryModes(
            List<TdrProjectCandidate> eligible,
            double cableLengthM,
            double locationErrorPercent,
            double distanceResolutionM)
        {
            List<TdrProjectCandidate> sorted = eligible.OrderBy(delegate(TdrProjectCandidate c) { return c.Position; })
                .ThenBy(delegate(TdrProjectCandidate c) { return c.RecordId; })
                .ThenBy(delegate(TdrProjectCandidate c) { return c.EventFamilyRootIndex; })
                .ThenBy(delegate(TdrProjectCandidate c) { return c.ReflectedIndex; })
                .ThenBy(delegate(TdrProjectCandidate c) { return c.Phase; })
                .ThenByDescending(delegate(TdrProjectCandidate c) { return c.PairPhysics; }).ToList();
            List<TdrPreliminaryMode> modes = new List<TdrPreliminaryMode>();
            foreach (TdrProjectCandidate c in sorted)
            {
                double candidateResolution = EffectiveResolution(
                    c.Position, locationErrorPercent, distanceResolutionM, c.PositionResolution);
                TdrPreliminaryMode best = null;
                double bestDistance = double.MaxValue;
                foreach (TdrPreliminaryMode mode in modes)
                {
                    double tolerance = 0.45 * Math.Max(candidateResolution, mode.EffectiveResolution);
                    double distance = Math.Abs(c.Position - mode.Center);
                    double minPosition = mode.Candidates.Min(delegate(TdrProjectCandidate x) { return x.Position; });
                    double maxPosition = mode.Candidates.Max(delegate(TdrProjectCandidate x) { return x.Position; });
                    if (distance <= tolerance &&
                        Math.Max(maxPosition, c.Position) - Math.Min(minPosition, c.Position) <=
                            Math.Max(candidateResolution, mode.EffectiveResolution) &&
                        distance < bestDistance)
                    {
                        best = mode;
                        bestDistance = distance;
                    }
                }
                if (best == null)
                {
                    best = new TdrPreliminaryMode();
                    best.Id = modes.Count;
                    best.Center = c.Position;
                    best.EffectiveResolution = candidateResolution;
                    modes.Add(best);
                }
                best.Candidates.Add(c);
                best.Center = Median(best.Candidates.Select(delegate(TdrProjectCandidate x) { return x.Position; }).ToList());
                best.EffectiveResolution = Math.Max(
                    distanceResolutionM,
                    Median(best.Candidates.Select(delegate(TdrProjectCandidate x) { return x.PositionResolution; }).ToList()));
                best.EffectiveResolution = Math.Max(
                    best.EffectiveResolution,
                    best.Center * locationErrorPercent / 100.0);
                c.PreliminaryMode = best.Id;
            }
            modes = modes.OrderBy(delegate(TdrPreliminaryMode m) { return m.Center; }).ToList();
            int i;
            for (i = 0; i < modes.Count; i++)
            {
                modes[i].Id = i;
                modes[i].PreliminarySupport = modes[i].Candidates
                    .Select(delegate(TdrProjectCandidate c) { return c.RecordId; }).Distinct().Count();
                foreach (TdrProjectCandidate c in modes[i].Candidates) c.PreliminaryMode = i;
            }
            return modes;
        }

        private static void AssignUniqueVotes(List<TdrPreliminaryMode> modes, int[] validLocCounts)
        {
            Dictionary<int, TdrPreliminaryMode> byId = modes.ToDictionary(delegate(TdrPreliminaryMode m) { return m.Id; });
            List<TdrProjectCandidate> all = modes.SelectMany(delegate(TdrPreliminaryMode m) { return m.Candidates; }).ToList();
            foreach (IGrouping<long, TdrProjectCandidate> recordGroup in all.GroupBy(delegate(TdrProjectCandidate c) { return c.RecordId; }))
            {
                List<TdrProjectCandidate> ranked = recordGroup.ToList();
                foreach (TdrProjectCandidate c in ranked)
                {
                    TdrPreliminaryMode mode = byId[c.PreliminaryMode];
                    int modePhaseMask = PhaseMask(mode.Candidates);
                    int modeValidLocCount = RelevantValidLocCount(modePhaseMask, validLocCounts);
                    double supportStrength = Clamp(mode.PreliminarySupport /
                        Math.Max(3.0, Math.Ceiling(modeValidLocCount * 0.10)), 0.0, 1.0);
                    c.AssignmentScore = 0.80 * c.PairPhysics + 0.20 * supportStrength +
                        (c.SelectedAsEventPair && !c.RescuedCandidate ? 0.015 : 0.0) -
                        (c.RescuedCandidate ? 0.015 : 0.0);
                }
                ranked = ranked.OrderByDescending(delegate(TdrProjectCandidate c) { return c.AssignmentScore; })
                    .ThenByDescending(delegate(TdrProjectCandidate c) { return c.PairPhysics; })
                    .ThenBy(delegate(TdrProjectCandidate c) { return c.Position; })
                    .ThenBy(delegate(TdrProjectCandidate c) { return c.EventFamilyRootIndex; })
                    .ThenBy(delegate(TdrProjectCandidate c) { return c.ReflectedIndex; })
                    .ThenBy(delegate(TdrProjectCandidate c) { return c.Phase; })
                    .ThenBy(delegate(TdrProjectCandidate c) { return c.DirectIndex; }).ToList();

                HashSet<long> usedFamilies = new HashSet<long>();
                HashSet<long> usedReflections = new HashSet<long>();
                List<TdrProjectCandidate> uniquePhysical = new List<TdrProjectCandidate>();
                foreach (TdrProjectCandidate c in ranked)
                {
                    if (usedFamilies.Contains(c.EventFamilyRootIndex)) continue;
                    if (usedReflections.Contains(c.ReflectedIndex)) continue;
                    usedFamilies.Add(c.EventFamilyRootIndex);
                    usedReflections.Add(c.ReflectedIndex);
                    uniquePhysical.Add(c);
                }

                // The project vote is record+region, never a raw pair-row count.
                foreach (IGrouping<int, TdrProjectCandidate> regionGroup in uniquePhysical.GroupBy(delegate(TdrProjectCandidate c) { return c.PreliminaryMode; }))
                {
                    TdrProjectCandidate vote = regionGroup.OrderByDescending(delegate(TdrProjectCandidate c) { return c.AssignmentScore; })
                        .ThenByDescending(delegate(TdrProjectCandidate c) { return c.PairPhysics; })
                        .ThenBy(delegate(TdrProjectCandidate c) { return c.EventFamilyRootIndex; })
                        .ThenBy(delegate(TdrProjectCandidate c) { return c.ReflectedIndex; })
                        .ThenBy(delegate(TdrProjectCandidate c) { return c.Phase; }).First();
                    byId[vote.PreliminaryMode].Votes.Add(vote);
                }
            }
        }

        private static List<TdrProjectRegion> BuildFinalRegions(
            List<TdrPreliminaryMode> modes,
            double cableLengthM,
            double locationErrorPercent,
            double distanceResolutionM)
        {
            List<TdrPreliminaryMode> live = modes.Where(delegate(TdrPreliminaryMode m) { return m.Votes.Count > 0; })
                .OrderBy(delegate(TdrPreliminaryMode m) { return m.Center; }).ToList();
            List<List<TdrPreliminaryMode>> bands = new List<List<TdrPreliminaryMode>>();
            foreach (TdrPreliminaryMode mode in live)
            {
                if (bands.Count == 0)
                {
                    bands.Add(new List<TdrPreliminaryMode>(new TdrPreliminaryMode[] { mode }));
                    continue;
                }
                List<TdrPreliminaryMode> previousBand = bands[bands.Count - 1];
                TdrPreliminaryMode previous = previousBand[previousBand.Count - 1];
                double previousCenter = Median(previous.Votes.Select(delegate(TdrProjectCandidate c) { return c.Position; }).ToList());
                double currentCenter = Median(mode.Votes.Select(delegate(TdrProjectCandidate c) { return c.Position; }).ToList());
                double previousResolution = Math.Max(previous.EffectiveResolution,
                    previousCenter * locationErrorPercent / 100.0);
                double currentResolution = Math.Max(mode.EffectiveResolution,
                    currentCenter * locationErrorPercent / 100.0);
                double previousP90 = Quantile(previous.Votes.Select(delegate(TdrProjectCandidate c) { return Math.Abs(c.Position - previousCenter); }).OrderBy(delegate(double x) { return x; }).ToList(), 0.90);
                double currentP90 = Quantile(mode.Votes.Select(delegate(TdrProjectCandidate c) { return Math.Abs(c.Position - currentCenter); }).OrderBy(delegate(double x) { return x; }).ToList(), 0.90);
                double previousHigh = previousCenter + Math.Max(previousResolution, previousP90);
                double currentLow = currentCenter - Math.Max(currentResolution, currentP90);
                bool unresolved = currentCenter - previousCenter < Math.Max(previousResolution, currentResolution) ||
                    currentLow <= previousHigh;
                if (unresolved) previousBand.Add(mode);
                else bands.Add(new List<TdrPreliminaryMode>(new TdrPreliminaryMode[] { mode }));
            }

            List<TdrProjectRegion> regions = new List<TdrProjectRegion>();
            foreach (List<TdrPreliminaryMode> band in bands)
            {
                TdrProjectRegion region = new TdrProjectRegion();
                region.Subpeaks.AddRange(band);
                region.SubpeakCount = band.Count;
                region.UnresolvedBand = band.Count >= 2;
                List<TdrProjectCandidate> allVotes = band.SelectMany(delegate(TdrPreliminaryMode m) { return m.Votes; }).ToList();
                // An unresolved band still represents one displayed region, so a record
                // contributes only its strongest representative vote to that band.
                foreach (IGrouping<long, TdrProjectCandidate> group in allVotes.GroupBy(delegate(TdrProjectCandidate c) { return c.RecordId; }))
                {
                    region.Votes.Add(group.OrderByDescending(delegate(TdrProjectCandidate c) { return c.AssignmentScore; })
                        .ThenByDescending(delegate(TdrProjectCandidate c) { return c.PairPhysics; })
                        .ThenBy(delegate(TdrProjectCandidate c) { return c.Position; })
                        .ThenBy(delegate(TdrProjectCandidate c) { return c.EventFamilyRootIndex; })
                        .ThenBy(delegate(TdrProjectCandidate c) { return c.ReflectedIndex; }).First());
                }
                region.Center = Median(region.Votes.Select(delegate(TdrProjectCandidate c) { return c.Position; }).ToList());
                region.EffectiveResolution = Math.Max(distanceResolutionM,
                    Median(region.Votes.Select(delegate(TdrProjectCandidate c) { return c.PositionResolution; }).ToList()));
                region.EffectiveResolution = Math.Max(region.EffectiveResolution,
                    region.Center * locationErrorPercent / 100.0);
                regions.Add(region);
            }
            regions = regions.OrderBy(delegate(TdrProjectRegion r) { return r.Center; }).ToList();
            int i;
            for (i = 0; i < regions.Count; i++) regions[i].Id = i;
            return regions;
        }

        private void ScoreRegions(
            List<TdrProjectRegion> regions,
            List<TdrProjectCandidate> allEligible,
            double[][] joints,
            double cableLengthM,
            double locationErrorPercent,
            double distanceResolutionM,
            int[] validLocCounts,
            double maxTdrInfluencePoints)
        {
            int projectApplicationCount = Math.Max(allEligible.Select(delegate(TdrProjectCandidate c) { return Math.Round(c.Application, 3); }).Distinct().Count(), 1);
            int projectVoltageCount = Math.Max(allEligible.Select(delegate(TdrProjectCandidate c) { return Math.Round(c.Voltage, 2); }).Distinct().Count(), 1);
            foreach (TdrProjectRegion region in regions)
            {
                List<TdrProjectCandidate> votes = region.Votes;
                region.SupportRecordCount = votes.Select(delegate(TdrProjectCandidate c) { return c.RecordId; }).Distinct().Count();
                region.StandardAnchorRecordCount = votes.Where(delegate(TdrProjectCandidate c) { return c.SelectedAsEventPair && !c.RescuedCandidate; })
                    .Select(delegate(TdrProjectCandidate c) { return c.RecordId; }).Distinct().Count();
                region.RescuedVoteFraction = votes.Count == 0 ? 0.0 : votes.Count(delegate(TdrProjectCandidate c) { return c.RescuedCandidate; }) / (double)votes.Count;
                region.PhaseMask = PhaseMask(votes);
                region.EffectiveValidLocCount = RelevantValidLocCount(region.PhaseMask, validLocCounts);
                if (region.EffectiveValidLocCount <= 0)
                    throw new ArgumentException("region phase has no valid LOC denominator");
                region.SupportRate = region.SupportRecordCount / (double)region.EffectiveValidLocCount;
                List<double> positions = votes.Select(delegate(TdrProjectCandidate c) { return c.Position; }).ToList();
                region.Center = Median(positions);
                List<double> deviations = positions.Select(delegate(double x) { return Math.Abs(x - region.Center); }).OrderBy(delegate(double x) { return x; }).ToList();
                region.PositionMad = 1.4826 * Median(deviations);
                region.PositionP90 = Quantile(deviations, 0.90);
                region.EffectiveResolution = Math.Max(distanceResolutionM,
                    Median(votes.Select(delegate(TdrProjectCandidate c) { return c.PositionResolution; }).ToList()));
                region.EffectiveResolution = Math.Max(region.EffectiveResolution,
                    region.Center * locationErrorPercent / 100.0);
                if (votes.Count < 2) region.PositionConcentration = 0.50;
                else region.PositionConcentration = Clamp(Math.Exp(-Math.Pow(region.PositionMad /
                    Math.Max(region.EffectiveResolution, 1e-9), 2.0)), 0.0, 1.0);

                double intervalRadius = Math.Max(region.EffectiveResolution, region.PositionP90);
                if (region.UnresolvedBand)
                {
                    double minSubpeak = region.Subpeaks.Min(delegate(TdrPreliminaryMode m) { return m.Center - m.EffectiveResolution; });
                    double maxSubpeak = region.Subpeaks.Max(delegate(TdrPreliminaryMode m) { return m.Center + m.EffectiveResolution; });
                    region.Low = Math.Min(region.Center - intervalRadius, minSubpeak);
                    region.High = Math.Max(region.Center + intervalRadius, maxSubpeak);
                }
                else
                {
                    region.Low = region.Center - intervalRadius;
                    region.High = region.Center + intervalRadius;
                }
                region.Low = Clamp(region.Low, 0.0, cableLengthM);
                region.High = Clamp(region.High, 0.0, cableLengthM);
                region.MedianPairPhysics = Median(votes.Select(delegate(TdrProjectCandidate c) { return c.PairPhysics; }).ToList());
                region.Q25PairPhysics = Quantile(votes.Select(delegate(TdrProjectCandidate c) { return c.PairPhysics; }).OrderBy(delegate(double x) { return x; }).ToList(), 0.25);
                region.MedianReflectionUniqueness = Median(votes.Select(delegate(TdrProjectCandidate c) { return c.ReflectionUniqueness; }).ToList());
                region.LowReflectedSnrFraction = votes.Count(delegate(TdrProjectCandidate c) { return c.ReflectedSnr < 4.0; }) / (double)Math.Max(votes.Count, 1);
                region.MedianAmbiguity = Median(votes.Select(delegate(TdrProjectCandidate c) { return c.MultipleReflectionAmbiguity; }).ToList());
                region.CalResponseFraction = votes.Count(delegate(TdrProjectCandidate c) { return c.CalSystemResponseMatch; }) / (double)Math.Max(votes.Count, 1);
                region.MultipathRiskFraction = votes.Count(delegate(TdrProjectCandidate c) { return c.MultipathRisk >= 0.50; }) / (double)Math.Max(votes.Count, 1);
                region.MedianCharge = Median(votes.Select(delegate(TdrProjectCandidate c) { return c.Charge; }).ToList());
                region.MaxCharge = votes.Count == 0 ? 0.0 : votes.Max(delegate(TdrProjectCandidate c) { return c.Charge; });
                region.EndpointCode = region.Center <= region.EffectiveResolution ? 1 :
                    (cableLengthM - region.Center <= region.EffectiveResolution ? 2 : 0);
                region.BoundaryClippedFraction = votes.Count(delegate(TdrProjectCandidate c)
                {
                    return (c.Position <= 1e-9 && c.UnclippedPosition < -1e-9) ||
                        (Math.Abs(c.Position - cableLengthM) <= 1e-9 && c.UnclippedPosition > cableLengthM + 1e-9);
                }) / (double)Math.Max(votes.Count, 1);
                NearestJoint(region.Center, joints, region.PhaseMask, out region.NearestJoint, out region.DistanceToJoint);
            }
            List<LegacyLocationScore> legacyScores = BuildLegacyScores(regions, joints, cableLengthM, locationErrorPercent, distanceResolutionM);

            // Local prominence is computed after all project modes are frozen, so it
            // cannot depend on the LOC processing order.
            foreach (TdrProjectRegion region in regions)
            {
                double competingSupport = 0.0;
                foreach (TdrProjectRegion other in regions)
                {
                    if (object.ReferenceEquals(region, other)) continue;
                    double neighborhood = 4.0 * Math.Max(region.EffectiveResolution, other.EffectiveResolution);
                    if (Math.Abs(region.Center - other.Center) <= neighborhood)
                        competingSupport = Math.Max(competingSupport, other.SupportRecordCount);
                }
                double separation = competingSupport <= 0.0 ? 1.0 :
                    region.SupportRecordCount / Math.Max(region.SupportRecordCount + 0.50 * competingSupport, 1.0);
                double supportStrength = Clamp(region.SupportRecordCount /
                    Math.Max(3.0, Math.Ceiling(region.EffectiveValidLocCount * 0.10)), 0.0, 1.0);
                region.LocalProminence = Clamp(0.65 * supportStrength + 0.35 * separation, 0.0, 1.0);

                double applicationCoverage = votesDistinct(region.Votes, true) / (double)projectApplicationCount;
                double voltageCoverage = votesDistinct(region.Votes, false) / (double)projectVoltageCount;
                double transparentFallback = Clamp(
                    0.50 * Clamp(region.SupportRate / 0.20, 0.0, 1.0) +
                    0.25 * region.PositionConcentration +
                    0.15 * region.LocalProminence +
                    0.05 * Clamp(voltageCoverage, 0.0, 1.0) +
                    0.05 * Clamp(applicationCoverage, 0.0, 1.0), 0.0, 1.0);
                LegacyLocationScore legacy = MatchLegacyScore(region, legacyScores);
                if (legacy != null)
                {
                    region.BaseRegionScore = 100.0 * legacy.Possibility;
                    region.LegacyBaseMatched = true;
                }
                else
                {
                    region.BaseRegionScore = 100.0 * transparentFallback;
                    region.LegacyBaseMatched = false;
                }

                double softSignal = 2.0 * (region.MedianPairPhysics - 0.50) -
                    0.35 * region.CalResponseFraction -
                    0.25 * region.MultipathRiskFraction -
                    0.15 * region.LowReflectedSnrFraction;
                region.TdrSoftDelta = maxTdrInfluencePoints * Clamp(softSignal, -1.0, 1.0);
                region.TdrSoftDelta = Clamp(region.TdrSoftDelta, -8.0, 8.0);
                region.FinalEngineeringScore = Clamp(region.BaseRegionScore + region.TdrSoftDelta, 0.0, 100.0);
                SetStatus(region, region.EffectiveValidLocCount);
            }
        }



        private List<LegacyLocationScore> BuildLegacyScores(
            List<TdrProjectRegion> regions,
            double[][] joints,
            double cableLengthM,
            double locationErrorPercent,
            double distanceResolutionM)
        {
            List<double>[] phaseData = new List<double>[] {
                new List<double>(), new List<double>(), new List<double>()
            };
            foreach (TdrProjectRegion region in regions)
            {
                foreach (TdrProjectCandidate vote in region.Votes)
                {
                    List<double> values = phaseData[vote.Phase];
                    values.Add(vote.Charge);
                    values.Add(vote.Position);
                    values.Add(vote.Angle);
                    values.Add(vote.Voltage);
                    values.Add(vote.Application);
                    values.Add(vote.Width);
                    values.Add(vote.Bandwidth);
                }
            }
            int rowsA = phaseData[0].Count / 7;
            int rowsB = phaseData[1].Count / 7;
            int rowsC = phaseData[2].Count / 7;
            if (rowsA + rowsB + rowsC <= 0) return new List<LegacyLocationScore>();
            AnalysisResult legacy = new Analyzer(_model).Run(
                phaseData[0].ToArray(), rowsA,
                phaseData[1].ToArray(), rowsB,
                phaseData[2].ToArray(), rowsC,
                joints[0], joints[1], joints[2],
                cableLengthM, locationErrorPercent, distanceResolutionM, 0.0, false);
            List<LegacyLocationScore> scores = new List<LegacyLocationScore>();
            int row;
            for (row = 0; row < legacy.LocationRows; row++)
            {
                int offset = row * legacy.LocationColumns;
                LegacyLocationScore score = new LegacyLocationScore();
                score.PhaseMask = (int)Math.Round(legacy.LocationMatrix[offset + 0]);
                score.Center = legacy.LocationMatrix[offset + 1];
                score.Low = legacy.LocationMatrix[offset + 2];
                score.High = legacy.LocationMatrix[offset + 3];
                score.Possibility = Clamp(legacy.LocationMatrix[offset + 7], 0.0, 1.0);
                scores.Add(score);
            }
            return scores;
        }

        private static LegacyLocationScore MatchLegacyScore(
            TdrProjectRegion region,
            List<LegacyLocationScore> scores)
        {
            return scores.Where(delegate(LegacyLocationScore score)
                {
                    if ((score.PhaseMask & region.PhaseMask) == 0) return false;
                    double scoreRadius = Math.Max((score.High - score.Low) * 0.5, 0.0);
                    bool overlaps = score.Low <= region.High && score.High >= region.Low;
                    return overlaps || Math.Abs(score.Center - region.Center) <=
                        Math.Max(region.EffectiveResolution, scoreRadius);
                })
                .OrderByDescending(delegate(LegacyLocationScore score) { return score.Possibility; })
                .ThenBy(delegate(LegacyLocationScore score) { return Math.Abs(score.Center - region.Center); })
                .ThenBy(delegate(LegacyLocationScore score) { return score.Center; })
                .FirstOrDefault();
        }

        private static int PhaseMask(IEnumerable<TdrProjectCandidate> candidates)
        {
            int mask = 0;
            foreach (TdrProjectCandidate candidate in candidates) mask |= 1 << candidate.Phase;
            return mask;
        }

        private static int RelevantValidLocCount(int phaseMask, int[] validLocCounts)
        {
            int total = 0;
            int phase;
            for (phase = 0; phase < 3; phase++)
                if ((phaseMask & (1 << phase)) != 0) total = checked(total + validLocCounts[phase]);
            return total;
        }

        private static int votesDistinct(List<TdrProjectCandidate> votes, bool application)
        {
            if (application)
                return votes.Select(delegate(TdrProjectCandidate c) { return Math.Round(c.Application, 3); }).Distinct().Count();
            return votes.Select(delegate(TdrProjectCandidate c) { return Math.Round(c.Voltage, 2); }).Distinct().Count();
        }

        private static void SetStatus(TdrProjectRegion region, int effectiveValidLocCount)
        {
            int orangeSupport = Math.Max(3, (int)Math.Ceiling(effectiveValidLocCount * 0.06));
            int redSupport = Math.Max(6, (int)Math.Ceiling(effectiveValidLocCount * 0.10));
            int bits = 0;
            if (!region.LegacyBaseMatched) bits |= 512;
            if (region.SupportRecordCount < 3) bits |= 1;
            if (region.FinalEngineeringScore < 50.0) bits |= 2;
            if (region.UnresolvedBand) bits |= 4;
            if (region.RescuedVoteFraction > 0.50) bits |= 8;
            if (region.BoundaryClippedFraction >= 0.50) bits |= 16;
            if (region.CalResponseFraction >= 0.50) bits |= 32;
            if (region.MultipathRiskFraction >= 0.50) bits |= 64;
            if (region.LowReflectedSnrFraction >= 0.50) bits |= 128;

            if (region.FinalEngineeringScore < 50.0 || region.SupportRecordCount < 3)
            {
                region.StatusCode = 0;
                region.ColorCode = 0;
                region.Status = "排除/低支持";
            }
            else if (region.FinalEngineeringScore < 70.0 || region.SupportRecordCount < orangeSupport)
            {
                region.StatusCode = 1;
                region.ColorCode = 1;
                region.Status = "疑似复核";
            }
            else
            {
                bool red = region.LegacyBaseMatched &&
                    region.FinalEngineeringScore >= 80.0 &&
                    region.SupportRecordCount >= redSupport &&
                    region.PositionConcentration >= 0.85 &&
                    !region.UnresolvedBand &&
                    region.RescuedVoteFraction <= 0.50 &&
                    region.BoundaryClippedFraction < 0.50;
                if (red)
                {
                    region.StatusCode = 3;
                    region.ColorCode = 3;
                    region.Status = "确定";
                    bits |= 256;
                }
                else
                {
                    region.StatusCode = 2;
                    region.ColorCode = 2;
                    region.Status = "高可信候选";
                }
            }
            if (region.UnresolvedBand)
            {
                region.StatusCode = Math.Min(region.StatusCode, 2);
                region.ColorCode = 4;
                region.Status = "未分辨事件带（复核）";
            }
            // A region supported only by rescued votes may be reviewed but never red.
            if (region.RescuedVoteFraction >= 1.0 - 1e-12 && region.StatusCode >= 3)
            {
                region.StatusCode = 2;
                region.ColorCode = 2;
                region.Status = "高可信候选（补检）";
            }
            region.StatusReasonBits = bits;
        }

        private static void FillRegionMatrix(
            TdrProjectAnalysisResult result,
            List<TdrProjectRegion> regions)
        {
            List<double> values = new List<double>();
            List<string> statuses = new List<string>();
            foreach (TdrProjectRegion r in regions.OrderBy(delegate(TdrProjectRegion x) { return x.Id; }))
            {
                values.Add(r.Id);                         // 0
                values.Add(r.PhaseMask);                  // 1
                values.Add(r.Center);                     // 2
                values.Add(r.Low);                        // 3
                values.Add(r.High);                       // 4
                values.Add(r.FinalEngineeringScore);      // 5
                values.Add(r.StatusCode);                 // 6
                values.Add(r.ColorCode);                  // 7
                values.Add(r.UnresolvedBand ? 1.0 : 0.0); // 8
                values.Add(r.SubpeakCount);               // 9
                values.Add(r.SupportRecordCount);         // 10
                values.Add(r.EffectiveValidLocCount);     // 11
                values.Add(r.SupportRate);                // 12
                values.Add(r.StandardAnchorRecordCount);  // 13
                values.Add(r.RescuedVoteFraction);        // 14
                values.Add(r.PositionMad);                // 15
                values.Add(r.PositionP90);                // 16
                values.Add(r.PositionConcentration);      // 17
                values.Add(r.LocalProminence);             // 18
                values.Add(r.BaseRegionScore);            // 19
                values.Add(r.TdrSoftDelta);               // 20
                values.Add(r.MedianPairPhysics);          // 21
                values.Add(r.Q25PairPhysics);             // 22
                values.Add(r.MedianReflectionUniqueness); // 23
                values.Add(r.LowReflectedSnrFraction);    // 24
                values.Add(r.MedianAmbiguity);            // 25
                values.Add(r.CalResponseFraction);        // 26
                values.Add(r.MultipathRiskFraction);      // 27
                values.Add(r.MedianCharge);               // 28
                values.Add(r.MaxCharge);                  // 29
                values.Add(r.NearestJoint);               // 30
                values.Add(r.DistanceToJoint);            // 31
                values.Add(r.EndpointCode);               // 32
                values.Add(r.StatusReasonBits);           // 33
                values.Add(r.EffectiveResolution);        // 34
                statuses.Add(r.Status);
            }
            result.RegionMatrix = values.ToArray();
            result.RegionRows = regions.Count;
            result.RegionStatusArray = statuses.ToArray();
        }

        private static void FillSelectedVoteMatrix(
            TdrProjectAnalysisResult result,
            List<TdrProjectRegion> regions)
        {
            List<double> values = new List<double>();
            foreach (TdrProjectRegion region in regions.OrderBy(delegate(TdrProjectRegion r) { return r.Id; }))
            {
                foreach (TdrProjectCandidate c in region.Votes
                    .OrderBy(delegate(TdrProjectCandidate x) { return x.RecordId; })
                    .ThenBy(delegate(TdrProjectCandidate x) { return x.Phase; })
                    .ThenBy(delegate(TdrProjectCandidate x) { return x.EventFamilyRootIndex; })
                    .ThenBy(delegate(TdrProjectCandidate x) { return x.ReflectedIndex; }))
                {
                    values.Add(c.Phase);                         // 0
                    values.Add(c.RecordId);                      // 1
                    values.Add(c.InputRow);                      // 2
                    values.Add(region.Id);                       // 3
                    values.Add(c.EventFamilyRootIndex);          // 4
                    values.Add(c.ReflectedIndex);                // 5
                    values.Add(c.Position);                      // 6
                    values.Add(c.Charge);                        // 7
                    values.Add(c.PairPhysics);                   // 8
                    values.Add(c.SelectedAsEventPair ? 1.0 : 0.0); // 9
                    values.Add(c.RescuedCandidate ? 1.0 : 0.0);    // 10
                }
            }
            result.SelectedVoteMatrix = values.ToArray();
            result.SelectedVoteRows = values.Count / 11;
        }

        private static string BuildSummary(TdrProjectAnalysisResult result, List<TdrProjectRegion> regions)
        {
            StringBuilder text = new StringBuilder();
            text.Append("项目终审共接收").Append(result.TotalInputRows)
                .Append("条紧凑候选，保留").Append(result.EligibleCandidateRows)
                .Append("条事件族根候选；总有效LOC数为").Append(result.GlobalTotalValidLocCount)
                .Append("，形成").Append(regions.Count).Append("个显示区域。");
            if (regions.Count > 0)
            {
                text.Append("各区域工程可能性为：");
                int i;
                for (i = 0; i < regions.Count; i++)
                {
                    if (i > 0) text.Append("；");
                    TdrProjectRegion r = regions[i];
                    text.Append(r.Center.ToString("F1", CultureInfo.InvariantCulture)).Append(" m ")
                        .Append(r.FinalEngineeringScore.ToString("F0", CultureInfo.InvariantCulture)).Append("%（")
                        .Append(r.Status).Append("，")
                        .Append(r.SupportRecordCount).Append("/").Append(r.EffectiveValidLocCount).Append("条相关相LOC）");
                }
                text.Append("。");
            }
            text.Append("该百分数是工程评分/统计可信度，不是经标定的统计概率；紫色区域表示当前距离分辨率下无法可靠拆分的事件带。");
            return LocatorEngine.SingleLine(text.ToString());
        }

        private static double[] NormalizeJoints(double[] values, double cableLength)
        {
            List<double> output = new List<double>();
            output.Add(0.0);
            if (values != null)
            {
                foreach (double value in values)
                    if (IsFinite(value) && value >= 0.0 && value <= cableLength) output.Add(value);
            }
            output.Add(cableLength);
            return output.Distinct().OrderBy(delegate(double x) { return x; }).ToArray();
        }

        private static void NearestJoint(
            double position, double[][] joints, int phaseMask,
            out double nearestJoint, out double distance)
        {
            List<double> applicable = new List<double>();
            int phase;
            for (phase = 0; phase < 3; phase++)
                if ((phaseMask & (1 << phase)) != 0) applicable.AddRange(joints[phase]);
            if (applicable.Count == 0)
                for (phase = 0; phase < 3; phase++) applicable.AddRange(joints[phase]);
            nearestJoint = applicable.OrderBy(delegate(double x) { return Math.Abs(x - position); })
                .ThenBy(delegate(double x) { return x; }).First();
            distance = Math.Abs(nearestJoint - position);
        }

        private static double EffectiveResolution(
            double position, double locationErrorPercent,
            double distanceResolutionM, double rowResolution)
        {
            return Math.Max(Math.Max(distanceResolutionM, rowResolution),
                Math.Abs(position) * locationErrorPercent / 100.0);
        }

        private static double Consistency(List<double> values)
        {
            if (values.Count < 2) return 0.50;
            List<double> absolute = values.Select(delegate(double x) { return Math.Abs(x); }).ToList();
            double median = Median(absolute);
            double mad = 1.4826 * Median(absolute.Select(delegate(double x) { return Math.Abs(x - median); }).ToList());
            return Clamp(Math.Exp(-mad / Math.Max(median, 1e-9)), 0.0, 1.0);
        }

        private static double TrapezoidScore(List<double> charges)
        {
            if (charges.Count < 5) return 0.50;
            double minimum = Math.Max(charges.Min(), 1e-9);
            double maximum = Math.Max(charges.Max(), minimum);
            if (maximum <= minimum * 1.05) return 0.50;
            int[] bins = new int[4];
            double low = Math.Log(minimum);
            double span = Math.Max(Math.Log(maximum) - low, 1e-9);
            foreach (double charge in charges)
            {
                int bin = (int)Math.Floor(4.0 * (Math.Log(Math.Max(charge, minimum)) - low) / span);
                if (bin < 0) bin = 0;
                if (bin > 3) bin = 3;
                bins[bin]++;
            }
            double monotonic = 0.0;
            int i;
            for (i = 0; i < 3; i++) if (bins[i] >= bins[i + 1]) monotonic += 1.0 / 3.0;
            double baseShare = (bins[0] + bins[1]) / (double)charges.Count;
            return Clamp(0.65 * monotonic + 0.35 * baseShare, 0.0, 1.0);
        }

        private static void CircularResultants(List<TdrProjectCandidate> votes, out double r1, out double r2)
        {
            double c1 = 0.0;
            double s1 = 0.0;
            double c2 = 0.0;
            double s2 = 0.0;
            foreach (TdrProjectCandidate vote in votes)
            {
                double radians = vote.Angle * Math.PI / 180.0;
                c1 += Math.Cos(radians);
                s1 += Math.Sin(radians);
                c2 += Math.Cos(2.0 * radians);
                s2 += Math.Sin(2.0 * radians);
            }
            r1 = Math.Sqrt(c1 * c1 + s1 * s1) / Math.Max(votes.Count, 1);
            r2 = Math.Sqrt(c2 * c2 + s2 * s2) / Math.Max(votes.Count, 1);
        }

        private static long ExactInteger(double value, string name)
        {
            if (!IsFinite(value) || value < 0.0 || value > MaxExactIntegerInDouble ||
                Math.Abs(value - Math.Round(value)) > 1e-9)
                throw new ArgumentException(name + "必须是0到2^53以内可精确表示的整数");
            return checked((long)Math.Round(value));
        }

        private static bool Flag(double value, string name)
        {
            value = RequireFinite(value, name);
            if (Math.Abs(value) <= 1e-9) return false;
            if (Math.Abs(value - 1.0) <= 1e-9) return true;
            throw new ArgumentException(name + "必须为0或1");
        }

        private static double RequireFinite(double value, string name)
        {
            if (!IsFinite(value)) throw new ArgumentException(name + "必须是有限数值");
            return value;
        }

        private static double Unit(double value)
        {
            if (!IsFinite(value)) return 0.0;
            return Clamp(value, 0.0, 1.0);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            List<double> sorted = values.OrderBy(delegate(double x) { return x; }).ToList();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] :
                0.5 * (sorted[middle - 1] + sorted[middle]);
        }

        private static double Quantile(List<double> sortedValues, double p)
        {
            if (sortedValues.Count == 0) return 0.0;
            double index = Clamp(p, 0.0, 1.0) * (sortedValues.Count - 1);
            int low = (int)Math.Floor(index);
            int high = (int)Math.Ceiling(index);
            if (low == high) return sortedValues[low];
            return sortedValues[low] * (high - index) + sortedValues[high] * (index - low);
        }

        private static void ValidateScalar(double value, string name, double low, double high, bool inclusive)
        {
            if (!IsFinite(value) || value < low || value > high)
                throw new ArgumentOutOfRangeException(name);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Clamp(double value, double low, double high)
        {
            return Math.Max(low, Math.Min(high, value));
        }

        private static string PhaseName(int phase)
        {
            return phase == 0 ? "A" : phase == 1 ? "B" : "C";
        }

        private static int BitCount(int value)
        {
            int count = 0;
            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }
            return count;
        }
    }
}
