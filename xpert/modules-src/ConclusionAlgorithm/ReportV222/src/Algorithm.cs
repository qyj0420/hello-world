using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HVX.Location
{
    internal sealed class PulsePoint
    {
        internal int SourceRow;
        internal int Phase;
        internal double Charge;
        internal double Position;
        internal double Angle;
        internal double Voltage;
        internal double Application;
        internal double Width;
        internal double Bandwidth;
        internal double InputDischargeProbability;
    }

    internal sealed class Candidate
    {
        internal int Index;
        internal int Phase;
        internal string Id;
        internal List<PulsePoint> Points;
        internal double Center;
        internal double Low;
        internal double High;
        internal double ErrorM;
        internal double MaxCharge;
        internal double MeanCharge;
        internal double Possibility;
        internal int StatusCode;
        internal string Status;
        internal double NearestJoint;
        internal double JointDistance;
        internal bool HasNearestIntermediateJoint;
        internal double NearestIntermediateJoint;
        internal double NearestIntermediateJointDistance;
        internal string LocationPart;
        internal int ApplicationGroups;
        internal double FeatureCoherence;
        internal double Trapezoid;
        internal double RuleScore;
        internal double[] Features;
        internal double InceptionVoltage;
        internal int InceptionPulseCount;
        internal int InceptionApplicationGroups;
        internal double VoltageResponseScore;
        internal double InputProbabilityScore;
        internal double InputProbabilityCoverage;
        internal double SequenceEvidenceScore;
        internal double VerticalColumnScore;
        internal double FloatingPatternScore;
        internal double NoiseSeparationScore;
        internal double MorphologyScore;
    }

    internal sealed class LocationGroup
    {
        internal List<Candidate> Members = new List<Candidate>();
        internal double Center;
        internal double Low;
        internal double High;
        internal int PulseCount;
        internal double MaxCharge;
        internal double MeanCharge;
        internal double Possibility;
        internal int StatusCode;
        internal string LocationPart;
        internal int PhaseMask;
        internal double InceptionVoltage;
        internal int InceptionPulseCount;
        internal int InceptionApplicationGroups;
        internal int InceptionPhaseMask;
        internal double OperatingPhaseVoltage;
        internal double InceptionVoltageRatio;
        internal int VoltageRiskCode;
        internal int VoltageRiskPhaseMask;
        internal double VoltageResponseScore;
        internal int ApplicationGroups;
        internal double InputProbabilityScore;
        internal double InputProbabilityCoverage;
        internal double SequenceEvidenceScore;
        internal double FeatureEvidenceScore;
        internal double SparseEvidenceScore;
        internal int ActionCode;
        internal int ServiceVoltagePulseCount;
        internal int ServiceVoltageApplicationGroups;
        internal double ServiceVoltageMaxCharge;
        internal double VerticalColumnScore;
        internal double FloatingPatternScore;
        internal double NoiseSeparationScore;
        internal double MorphologyScore;
        internal double TrapezoidScore;
        internal double StandardLimitPc;
        internal bool StandardExceeded;
        internal string StandardComponent;
        internal bool HiddenJointCheck;
        internal bool NearbyIntermediateJointCheck;
        internal double NearbyIntermediateJointPositionM;
        internal double NearbyIntermediateJointDistanceM;
        internal bool ServiceVoltageJointPriority;
        internal bool ServiceVoltageSurgeImmediate;
        internal double VoltageChargeGrowthRatio;
        internal double VoltageCountGrowthRatio;
        internal int InceptionVoltageDowngradeLevels;
    }

    internal sealed class CableStandardProfile
    {
        internal double ServiceYears;
        internal int CableBodyType;
        internal int IntermediateJointType;

        internal static CableStandardProfile ExistingXlpe()
        {
            return new CableStandardProfile { ServiceYears = 1.000001, CableBodyType = 0, IntermediateJointType = 1 };
        }
    }

    internal sealed class Analyzer
    {
        private const int InputColumns = 7;
        private readonly RegionModel _model;
        private double[][] _pulseProbabilityOverride;
        private CableStandardProfile _standardProfile = CableStandardProfile.ExistingXlpe();

        internal Analyzer(RegionModel model) { _model = model; }

        internal void ConfigureStandardProfile(double cableServiceYears, int cableBodyType, int intermediateJointType)
        {
            if (!IsFinite(cableServiceYears) || cableServiceYears < 0.0 || cableServiceYears > 200.0)
                throw new ArgumentOutOfRangeException("cableServiceYears", "电缆投运年限必须在0～200年之间");
            if (cableBodyType < 0 || cableBodyType > 1)
                throw new ArgumentOutOfRangeException("cableBodyType", "电缆本体类型仅支持0（XLPE）或1（PILC）");
            if (intermediateJointType < 0 || intermediateJointType > 3)
                throw new ArgumentOutOfRangeException("intermediateJointType", "中间接头类型仅支持0～3");
            _standardProfile = new CableStandardProfile
            {
                ServiceYears = cableServiceYears,
                CableBodyType = cableBodyType,
                IntermediateJointType = intermediateJointType
            };
        }

        internal AnalysisResult RunWithPulseProbability(
            double[] dataA, int rowsA, double[] probabilityA,
            double[] dataB, int rowsB, double[] probabilityB,
            double[] dataC, int rowsC, double[] probabilityC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double cableLengthM, double errorPercent, double cursorStepM,
            double operatingPhaseVoltageKv, bool englishOutput, int conclusionAudienceType)
        {
            _pulseProbabilityOverride = new double[][] { probabilityA, probabilityB, probabilityC };
            try
            {
                return Run(dataA, rowsA, dataB, rowsB, dataC, rowsC, jointsA, jointsB, jointsC,
                    cableLengthM, errorPercent, cursorStepM, operatingPhaseVoltageKv, englishOutput, conclusionAudienceType);
            }
            finally
            {
                _pulseProbabilityOverride = null;
            }
        }

        internal AnalysisResult Run(
            double[] dataA, int rowsA,
            double[] dataB, int rowsB,
            double[] dataC, int rowsC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double cableLengthM, double errorPercent, double cursorStepM,
            double operatingPhaseVoltageKv, bool englishOutput)
        {
            return Run(
                dataA, rowsA, dataB, rowsB, dataC, rowsC,
                jointsA, jointsB, jointsC,
                cableLengthM, errorPercent, cursorStepM, operatingPhaseVoltageKv, englishOutput, 1);
        }

        internal AnalysisResult Run(
            double[] dataA, int rowsA,
            double[] dataB, int rowsB,
            double[] dataC, int rowsC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double cableLengthM, double errorPercent, double cursorStepM,
            double operatingPhaseVoltageKv, bool englishOutput, int conclusionAudienceType)
        {
            ValidateScalar(errorPercent, "locationErrorPercent", 0.0, 20.0, false);
            ValidateScalar(cursorStepM, "cursorStepM", 0.0, 1000.0, false);
            ValidateScalar(operatingPhaseVoltageKv, "operatingPhaseVoltageKv", 0.0, 1000.0, true);
            if (conclusionAudienceType != 0 && conclusionAudienceType != 1)
                throw new ArgumentOutOfRangeException("conclusionAudienceType", "结论用户类型仅支持0（现场简明版）或1（专业技术版）");
            if (errorPercent <= 0.0) errorPercent = 1.0;
            if (cursorStepM <= 0.0) cursorStepM = 0.1;

            List<PulsePoint>[] raw = new List<PulsePoint>[3];
            raw[0] = ReadPhase(dataA, rowsA, 0, ProbabilityForPhase(0));
            raw[1] = ReadPhase(dataB, rowsB, 1, ProbabilityForPhase(1));
            raw[2] = ReadPhase(dataC, rowsC, 2, ProbabilityForPhase(2));
            int totalPulses = rowsA + rowsB + rowsC;
            if (totalPulses <= 0)
            {
                AnalysisResult emptyResult = new AnalysisResult();
                emptyResult.Ok = true;
                emptyResult.ModelVersion = _model.Version;
                emptyResult.TotalPulses = 0;
                emptyResult.ValidPulses = 0;
                emptyResult.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                LocatorEngine.ApplyPresentationMetadata(emptyResult, englishOutput, conclusionAudienceType);
                emptyResult.StatusArray = new string[] { englishOutput ? "No discharge" : "无放电" };
                emptyResult.SummaryText = BuildNoDischargeSummary(englishOutput);
                return emptyResult;
            }

            cableLengthM = LocatorEngine.ResolveCableLengthFromJointEndpoints(jointsA, jointsB, jointsC);
            double[][] inputJoints = new[] { jointsA, jointsB, jointsC };
            double[] phaseLengths = inputJoints.Select(values => values != null && values.Length > 0
                ? values[values.Length - 1] : cableLengthM).ToArray();
            double[][] joints = new double[3][];
            joints[0] = NormalizeJoints(jointsA, phaseLengths[0]);
            joints[1] = NormalizeJoints(jointsB, phaseLengths[1]);
            joints[2] = NormalizeJoints(jointsC, phaseLengths[2]);

            List<PulsePoint>[] valid = new List<PulsePoint>[3];
            double[] noise = new double[3];
            int phase;
            int validCount = 0;
            for (phase = 0; phase < 3; phase++)
            {
                noise[phase] = NoiseThreshold(raw[phase]);
                valid[phase] = FilterValid(raw[phase], phaseLengths[phase], noise[phase]);
                validCount += valid[phase].Count;
            }

            List<Candidate> candidates = new List<Candidate>();
            for (phase = 0; phase < 3; phase++)
            {
                List<Candidate> phaseCandidates = AnalyzePhase(
                    valid[phase], phase, phaseLengths[phase], errorPercent,
                    cursorStepM, noise[phase], joints[phase]);
                candidates.AddRange(phaseCandidates);
            }
            candidates.Sort(delegate(Candidate a, Candidate b)
            {
                int positionCompare = a.Center.CompareTo(b.Center);
                return positionCompare != 0 ? positionCompare : a.Phase.CompareTo(b.Phase);
            });
            int index;
            for (index = 0; index < candidates.Count; index++)
            {
                candidates[index].Index = index;
                candidates[index].Id = CandidateId(candidates[index]);
            }

            List<Candidate> retained = candidates.Where(delegate(Candidate c) { return c.StatusCode >= 1; }).ToList();
            // cursorStepM is the explicit absolute cross-phase merge tolerance.
            // locationErrorPercent is retained only for uncertainty reporting and
            // must never enlarge the physical grouping radius.
            List<LocationGroup> locations = MergeLocations(retained, operatingPhaseVoltageKv, cursorStepM, englishOutput);

            AnalysisResult result = new AnalysisResult();
            result.Ok = true;
            result.ModelVersion = _model.Version;
            result.TotalPulses = totalPulses;
            result.ValidPulses = validCount;
            result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
            LocatorEngine.ApplyPresentationMetadata(result, englishOutput, conclusionAudienceType);
            FillFilteredScatter(result, retained);
            FillCandidateMatrix(result, retained, englishOutput);
            FillLocationMatrix(result, locations);
            FillVoltageAssessmentMatrix(result, locations);
            FillLearning(result, candidates);
            result.SummaryText = BuildSummary(totalPulses, validCount, locations, operatingPhaseVoltageKv, englishOutput, conclusionAudienceType, _standardProfile);
            return result;
        }

        private double[] ProbabilityForPhase(int phase)
        {
            return _pulseProbabilityOverride != null && phase >= 0 && phase < _pulseProbabilityOverride.Length
                ? _pulseProbabilityOverride[phase] : null;
        }

        private static List<PulsePoint> ReadPhase(double[] flat, int rows, int phase, double[] probabilities)
        {
            if (rows < 0) throw new ArgumentOutOfRangeException("rows");
            if (flat == null) flat = new double[0];
            if (flat.Length < rows * InputColumns)
                throw new ArgumentException(PhaseName(phase) + "相数组长度小于rows×7");
            if (probabilities != null && probabilities.Length < rows)
                throw new ArgumentException(PhaseName(phase) + "相第22列放电可能性数组长度小于rows");
            List<PulsePoint> points = new List<PulsePoint>(rows);
            int row;
            for (row = 0; row < rows; row++)
            {
                int offset = row * InputColumns;
                PulsePoint point = new PulsePoint();
                point.SourceRow = row;
                point.Phase = phase;
                point.Charge = Math.Abs(flat[offset + 0]);
                point.Position = flat[offset + 1];
                point.Angle = flat[offset + 2];
                point.Voltage = flat[offset + 3];
                point.Application = flat[offset + 4];
                point.Width = flat[offset + 5];
                point.Bandwidth = flat[offset + 6];
                point.InputDischargeProbability = probabilities == null ? double.NaN : probabilities[row];
                points.Add(point);
            }
            return points;
        }

        private static double[] NormalizeJoints(double[] values, double cableLength)
        {
            List<double> joints = new List<double>();
            joints.Add(0.0);
            if (values != null)
            {
                int i;
                for (i = 0; i < values.Length; i++)
                    if (IsFinite(values[i]) && values[i] >= 0.0 && values[i] <= cableLength) joints.Add(values[i]);
            }
            joints.Add(cableLength);
            joints.Sort();
            List<double> unique = new List<double>();
            int j;
            for (j = 0; j < joints.Count; j++)
                if (unique.Count == 0 || Math.Abs(unique[unique.Count - 1] - joints[j]) > 1e-6) unique.Add(joints[j]);
            return unique.ToArray();
        }

        private static List<PulsePoint> FilterValid(List<PulsePoint> raw, double cableLength, double noise)
        {
            List<PulsePoint> result = new List<PulsePoint>();
            int i;
            for (i = 0; i < raw.Count; i++)
            {
                PulsePoint p = raw[i];
                if (!IsFinite(p.Charge) || !IsFinite(p.Position) || !IsFinite(p.Voltage) ||
                    !IsFinite(p.Application) || !IsFinite(p.Width) || !IsFinite(p.Bandwidth) || !IsFinite(p.Angle)) continue;
                if (p.Position < 0.0 || p.Position > cableLength || p.Charge <= noise || p.Voltage <= 0.05) continue;
                if (p.Width < 0.0 || p.Bandwidth < 0.0) continue;
                result.Add(p);
            }
            return result;
        }

        private static double NoiseThreshold(List<PulsePoint> raw)
        {
            List<double> background = raw.Where(delegate(PulsePoint p)
            {
                return IsFinite(p.Charge) && IsFinite(p.Voltage) && p.Voltage <= 0.05 && p.Charge >= 0.0;
            }).Select(delegate(PulsePoint p) { return p.Charge; }).ToList();
            if (background.Count >= 3)
            {
                double med = Median(background);
                double mad = Median(background.Select(delegate(double q) { return Math.Abs(q - med); }).ToList());
                return Math.Max(0.0, med + 4.5 * 1.4826 * mad);
            }
            List<double> charges = raw.Where(delegate(PulsePoint p)
            {
                return IsFinite(p.Charge) && p.Charge > 0.0;
            }).Select(delegate(PulsePoint p) { return p.Charge; }).OrderBy(delegate(double q) { return q; }).ToList();
            if (charges.Count == 0) return 0.0;
            // With no explicit 0 kV background, retain low-amplitude dense populations.
            return Math.Max(0.0, Quantile(charges, 0.05) * 0.50);
        }

        private List<Candidate> AnalyzePhase(
            List<PulsePoint> population, int phase, double cableLength,
            double errorPercent, double cursorStep, double noise,
            double[] joints)
        {
            List<Candidate> output = new List<Candidate>();
            if (population.Count < 2) return output;
            List<PulsePoint> sorted = population.OrderBy(delegate(PulsePoint p) { return p.Position; }).ToList();
            List<List<PulsePoint>> positionGroups = new List<List<PulsePoint>>();
            List<PulsePoint> current = new List<PulsePoint>();
            int i;
            for (i = 0; i < sorted.Count; i++)
            {
                PulsePoint point = sorted[i];
                if (current.Count == 0) current.Add(point);
                else
                {
                    PulsePoint previous = current[current.Count - 1];
                    // A local concentration is evaluated inside a window equal to
                    // 2% of the cable length. Adjacent points still need a bounded
                    // gap so a long chain of unrelated scatter cannot form a cluster.
                    double concentrationWindow = Math.Max(cableLength * 0.02, cursorStep * 2.0);
                    double tolerance = Math.Max(cursorStep, Math.Min(concentrationWindow * 0.25, 10.0));
                    double maximumSpan = concentrationWindow;
                    if (point.Position - previous.Position <= tolerance && point.Position - current[0].Position <= maximumSpan)
                        current.Add(point);
                    else
                    {
                        positionGroups.Add(current);
                        current = new List<PulsePoint>();
                        current.Add(point);
                    }
                }
            }
            if (current.Count > 0) positionGroups.Add(current);

            for (i = 0; i < positionGroups.Count; i++)
            {
                double coherence;
                List<PulsePoint> refined = RefineFeatureCluster(positionGroups[i], out coherence);
                if (refined.Count < 2) continue;
                // Two to four pulses may enter the multi-evidence scorer when they
                // repeat across independent voltage-data IDs/applications or have
                // supporting pulse probability. Count alone never establishes PD.
                if (refined.Count < 3 && !HasSparseEntryEvidence(refined)) continue;
                Candidate candidate = BuildCandidate(
                    refined, positionGroups[i].Count, population, phase,
                    cableLength, errorPercent, cursorStep, noise, joints, coherence);
                output.Add(candidate);
            }
            return output;
        }

        private static List<PulsePoint> RefineFeatureCluster(List<PulsePoint> points, out double coherence)
        {
            coherence = 1.0;
            if (points.Count < 3) return new List<PulsePoint>(points);
            List<double> logWidth = points.Select(delegate(PulsePoint p) { return Math.Log(1.0 + Math.Abs(p.Width)); }).ToList();
            List<double> logBandwidth = points.Select(delegate(PulsePoint p) { return Math.Log(1.0 + Math.Abs(p.Bandwidth)); }).ToList();
            double mw = Median(logWidth);
            double mb = Median(logBandwidth);
            double sw = Math.Max(1.4826 * Median(logWidth.Select(delegate(double v) { return Math.Abs(v - mw); }).ToList()), 0.08);
            double sb = Math.Max(1.4826 * Median(logBandwidth.Select(delegate(double v) { return Math.Abs(v - mb); }).ToList()), 0.08);
            List<double> distances = new List<double>();
            int i;
            for (i = 0; i < points.Count; i++)
            {
                double zw = (logWidth[i] - mw) / sw;
                double zb = (logBandwidth[i] - mb) / sb;
                distances.Add(Math.Sqrt(0.5 * (zw * zw + zb * zb)));
            }
            double threshold = Math.Max(1.45, Quantile(distances.OrderBy(delegate(double x) { return x; }).ToList(), 0.70));
            List<PulsePoint> kept = new List<PulsePoint>();
            double sum = 0.0;
            for (i = 0; i < points.Count; i++)
            {
                if (distances[i] <= threshold)
                {
                    kept.Add(points[i]);
                    sum += Math.Exp(-0.5 * distances[i] * distances[i]);
                }
            }
            if (kept.Count < 3) kept = new List<PulsePoint>(points);
            coherence = kept.Count == 0 ? 0.0 : Clamp(sum / Math.Max(kept.Count, 1), 0.0, 1.0);
            return kept;
        }

        private Candidate BuildCandidate(
            List<PulsePoint> points, int sourceCount, List<PulsePoint> population,
            int phase, double cableLength, double errorPercent,
            double cursorStep, double noise, double[] joints, double coherence)
        {
            List<double> q = points.Select(delegate(PulsePoint p) { return p.Charge; }).ToList();
            List<double> positions = points.Select(delegate(PulsePoint p) { return p.Position; }).ToList();
            double center = WeightedMedian(points);
            List<double> deviations = positions.Select(delegate(double x) { return Math.Abs(x - center); }).ToList();
            double positionError = Math.Max(cursorStep, center * errorPercent / 100.0);
            double madM = 1.4826 * Median(deviations);
            double p90M = Quantile(deviations.OrderBy(delegate(double x) { return x; }).ToList(), 0.90);
            double low = Math.Max(0.0, center - Math.Max(positionError, p90M));
            double high = Math.Min(cableLength, center + Math.Max(positionError, p90M));
            double window = Math.Max(cableLength * 0.02, cursorStep * 2.0);
            double expected = Math.Max(population.Count * window / cableLength, 1e-6);
            double contrast = (points.Count + 1.0) / (expected + 1.0);
            double density = Clamp(Math.Log(1.0 + contrast) / Math.Log(8.0), 0.0, 1.0);
            double concentrationRadius = Math.Max(window * 0.50, cursorStep);
            double concentration = Clamp(Math.Exp(-Math.Pow(p90M / Math.Max(concentrationRadius, 1e-6), 2.0)), 0.0, 1.0);
            int groups = UniqueApplications(points);
            int populationGroups = Math.Max(UniqueApplications(population), 1);
            double groupFraction = (double)groups / populationGroups;
            double repeatability = populationGroups <= 1 ? 0.50 : Clamp((double)groups / Math.Max(Math.Min(populationGroups, 4), 2), 0.0, 1.0);
            int voltageCount = UniqueVoltages(points);
            int populationVoltageCount = Math.Max(UniqueVoltages(population), 1);
            double voltageCoverage = Math.Min((double)voltageCount / populationVoltageCount, 1.0);
            double widthConsistency = Consistency(points.Select(delegate(PulsePoint p) { return p.Width; }).ToList());
            double bandwidthConsistency = Consistency(points.Select(delegate(PulsePoint p) { return p.Bandwidth; }).ToList());
            double trapezoid = TrapezoidScore(q, noise);
            double voltageResponse = VoltageResponseScore(points);
            double inputProbabilityCoverage;
            double inputProbability = InputProbabilityEvidence(points, out inputProbabilityCoverage);
            double sequenceEvidence = SequenceEvidenceScore(points, population, repeatability);
            double resultant1;
            double resultant2;
            CircularResultants(points, out resultant1, out resultant2);
            double medianQ = Median(q);
            double meanQ = q.Average();
            double maxQ = q.Max();
            double floor = Math.Max(noise, 1e-9);
            double chargeRatio = medianQ / floor;
            double chargeScore = Clamp(Math.Log(1.0 + chargeRatio) / Math.Log(11.0), 0.0, 1.0);
            double aboveFraction = q.Count(delegate(double value) { return value > noise; }) / (double)q.Count;
            double noiseSeparation = NoiseSeparationScore(q, noise);
            double verticalColumn = VerticalColumnScore(concentration, q);
            double floatingPattern = FloatingDischargePatternScore(points);
            double morphology = Math.Max(verticalColumn, floatingPattern);
            double rule = Clamp(
                0.20 * density + 0.16 * concentration + 0.16 * repeatability +
                0.12 * coherence + 0.06 * trapezoid + 0.05 * chargeScore +
                0.04 * voltageResponse + 0.13 * morphology + 0.08 * noiseSeparation, 0.0, 1.0);

            double[] features = new double[] {
                Math.Min(Math.Log(1.0 + points.Count) / Math.Log(51.0), 1.5),
                Math.Min(madM / Math.Max(positionError, 1e-6), 4.0),
                Math.Min(p90M / Math.Max(positionError, 1e-6), 8.0),
                density,
                repeatability,
                Math.Min(groupFraction, 1.0),
                coherence,
                widthConsistency,
                bandwidthConsistency,
                trapezoid,
                Math.Min(Math.Log(1.0 + chargeRatio) / 5.0, 2.0),
                Math.Min(Math.Log(1.0 + maxQ / Math.Max(medianQ, 1e-9)) / 3.0, 2.0),
                voltageCoverage,
                resultant1,
                resultant2,
                aboveFraction
            };
            double neural = _model.Predict(features);
            double possibility = inputProbabilityCoverage > 0.0
                ? Clamp(0.30 * rule + 0.50 * neural + 0.20 * inputProbability, 0.0, 1.0)
                : Clamp(0.35 * rule + 0.65 * neural, 0.0, 1.0);
            possibility = Clamp(possibility * (0.85 + 0.15 * morphology) * (0.75 + 0.25 * noiseSeparation), 0.0, 1.0);
            int evidenceKinds = 0;
            if (density >= 0.55 && concentration >= 0.45) evidenceKinds++;
            if (groups >= 2 || voltageCount >= 2) evidenceKinds++;
            if (inputProbabilityCoverage >= 0.50 && inputProbability >= 0.58) evidenceKinds++;
            if (morphology >= 0.45) evidenceKinds++;
            if (noiseSeparation >= 0.35 && chargeScore >= 0.35) evidenceKinds++;
            int statusCode = 0;
            string status = "背景/干扰";
            if (possibility >= 0.72 && evidenceKinds >= 3 && morphology >= 0.45 && noiseSeparation >= 0.35)
            { statusCode = 2; status = "较高可信"; }
            else if (possibility >= 0.45 && evidenceKinds >= 2)
            { statusCode = 1; status = "疑似复核"; }

            double nearest;
            double jointDistance;
            bool hasNearestIntermediateJoint;
            double nearestIntermediateJoint;
            double nearestIntermediateJointDistance;
            string part;
            // Joint/terminal attribution uses an absolute distance and is not
            // allowed to inherit a percentage error that grows with cable length.
            double attributionTolerance = Math.Max(cursorStep, Math.Min(positionError, 5.0));
            JointAttribution(center, cableLength, attributionTolerance, joints,
                out nearest, out jointDistance, out part,
                out hasNearestIntermediateJoint, out nearestIntermediateJoint, out nearestIntermediateJointDistance);
            Candidate result = new Candidate();
            result.Phase = phase;
            result.Points = points;
            result.Center = center;
            result.Low = low;
            result.High = high;
            result.ErrorM = positionError;
            result.MaxCharge = maxQ;
            result.MeanCharge = meanQ;
            result.Possibility = possibility;
            result.StatusCode = statusCode;
            result.Status = status;
            result.NearestJoint = nearest;
            result.JointDistance = jointDistance;
            result.HasNearestIntermediateJoint = hasNearestIntermediateJoint;
            result.NearestIntermediateJoint = nearestIntermediateJoint;
            result.NearestIntermediateJointDistance = nearestIntermediateJointDistance;
            result.LocationPart = part;
            result.ApplicationGroups = groups;
            result.FeatureCoherence = coherence;
            result.Trapezoid = trapezoid;
            result.RuleScore = rule;
            result.Features = features;
            DetermineInceptionVoltage(points, out result.InceptionVoltage, out result.InceptionPulseCount, out result.InceptionApplicationGroups);
            result.VoltageResponseScore = voltageResponse;
            result.InputProbabilityScore = inputProbability;
            result.InputProbabilityCoverage = inputProbabilityCoverage;
            result.SequenceEvidenceScore = sequenceEvidence;
            result.VerticalColumnScore = verticalColumn;
            result.FloatingPatternScore = floatingPattern;
            result.NoiseSeparationScore = noiseSeparation;
            result.MorphologyScore = morphology;
            return result;
        }

        private static double VoltageResponseScore(List<PulsePoint> points)
        {
            List<IGrouping<double, PulsePoint>> voltageGroups = points
                .GroupBy(delegate(PulsePoint p) { return Math.Round(p.Voltage, 1, MidpointRounding.AwayFromZero); })
                .OrderBy(delegate(IGrouping<double, PulsePoint> g) { return g.Key; })
                .ToList();
            if (voltageGroups.Count < 2) return 0.50;

            List<double> overallCharge = voltageGroups.Select(delegate(IGrouping<double, PulsePoint> g)
            {
                List<double> charges = g.Select(delegate(PulsePoint p) { return p.Charge; }).OrderBy(delegate(double q) { return q; }).ToList();
                return Math.Log(1.0 + Quantile(charges, 0.75));
            }).ToList();
            double overallTrend = 0.5 + 0.5 * SpearmanByOrderedVoltage(overallCharge);

            List<double> applicationTrends = new List<double>();
            foreach (IGrouping<double, PulsePoint> application in points.GroupBy(delegate(PulsePoint p) { return Math.Round(p.Application, 3); }))
            {
                List<IGrouping<double, PulsePoint>> levels = application
                    .GroupBy(delegate(PulsePoint p) { return Math.Round(p.Voltage, 1, MidpointRounding.AwayFromZero); })
                    .OrderBy(delegate(IGrouping<double, PulsePoint> g) { return g.Key; })
                    .ToList();
                if (levels.Count < 2) continue;
                List<double> branchCharge = levels.Select(delegate(IGrouping<double, PulsePoint> g)
                {
                    return Math.Log(1.0 + Median(g.Select(delegate(PulsePoint p) { return p.Charge; }).ToList()));
                }).ToList();
                applicationTrends.Add(0.5 + 0.5 * SpearmanByOrderedVoltage(branchCharge));
            }

            int persistentLevels = voltageGroups.Count(delegate(IGrouping<double, PulsePoint> g)
            {
                List<PulsePoint> level = g.ToList();
                return level.Count >= 2 && UniqueApplications(level) >= Math.Min(2, UniqueApplications(points));
            });
            double persistence = persistentLevels / (double)voltageGroups.Count;
            double score;
            if (applicationTrends.Count > 0)
                score = 0.55 * Median(applicationTrends) + 0.30 * overallTrend + 0.15 * persistence;
            else
                score = 0.75 * overallTrend + 0.25 * persistence;
            return Clamp(score, 0.0, 1.0);
        }

        private static double SpearmanByOrderedVoltage(List<double> values)
        {
            int count = values.Count;
            if (count < 2) return 0.0;
            List<double> ranks = AverageRanks(values);
            double meanX = (count + 1.0) * 0.5;
            double meanY = ranks.Average();
            double covariance = 0.0;
            double varianceX = 0.0;
            double varianceY = 0.0;
            int i;
            for (i = 0; i < count; i++)
            {
                double dx = (i + 1.0) - meanX;
                double dy = ranks[i] - meanY;
                covariance += dx * dy;
                varianceX += dx * dx;
                varianceY += dy * dy;
            }
            if (varianceX <= 1e-12 || varianceY <= 1e-12) return 0.0;
            return Clamp(covariance / Math.Sqrt(varianceX * varianceY), -1.0, 1.0);
        }

        private static List<double> AverageRanks(List<double> values)
        {
            List<int> order = Enumerable.Range(0, values.Count)
                .OrderBy(delegate(int index) { return values[index]; })
                .ToList();
            double[] ranks = new double[values.Count];
            int start = 0;
            while (start < order.Count)
            {
                int end = start;
                while (end + 1 < order.Count && Math.Abs(values[order[end + 1]] - values[order[start]]) <= 1e-12) end++;
                double rank = 0.5 * ((start + 1.0) + (end + 1.0));
                int i;
                for (i = start; i <= end; i++) ranks[order[i]] = rank;
                start = end + 1;
            }
            return ranks.ToList();
        }

        private static void DetermineInceptionVoltage(
            List<PulsePoint> points,
            out double inceptionVoltage,
            out int inceptionPulseCount,
            out int inceptionApplicationGroups)
        {
            SortedDictionary<double, List<PulsePoint>> levels = new SortedDictionary<double, List<PulsePoint>>();
            foreach (PulsePoint point in points)
            {
                double level = Math.Round(point.Voltage, 1, MidpointRounding.AwayFromZero);
                List<PulsePoint> atLevel;
                if (!levels.TryGetValue(level, out atLevel))
                {
                    atLevel = new List<PulsePoint>();
                    levels.Add(level, atLevel);
                }
                atLevel.Add(point);
            }

            int minimumEvidenceCount = Math.Max(2, Math.Min(5, (int)Math.Ceiling(points.Count * 0.10)));
            int allApplicationGroups = UniqueApplications(points);
            KeyValuePair<double, List<PulsePoint>> selected = levels.First();
            bool found = false;
            foreach (KeyValuePair<double, List<PulsePoint>> level in levels)
            {
                int applicationGroups = UniqueApplications(level.Value);
                bool repeatedApplicationEvidence = allApplicationGroups < 2 || applicationGroups >= 2;
                if (level.Value.Count >= minimumEvidenceCount && repeatedApplicationEvidence)
                {
                    selected = level;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                foreach (KeyValuePair<double, List<PulsePoint>> level in levels)
                {
                    if (level.Value.Count >= minimumEvidenceCount)
                    {
                        selected = level;
                        break;
                    }
                }
            }
            inceptionVoltage = selected.Key;
            inceptionPulseCount = selected.Value.Count;
            inceptionApplicationGroups = UniqueApplications(selected.Value);
        }

        private static void JointAttribution(
            double center, double cableLength, double tolerance,
            double[] joints, out double nearest, out double distance, out string part,
            out bool hasNearestIntermediateJoint, out double nearestIntermediateJoint,
            out double nearestIntermediateJointDistance)
        {
            const double terminalBoundaryM = 2.0;
            hasNearestIntermediateJoint = false;
            nearestIntermediateJoint = 0.0;
            nearestIntermediateJointDistance = double.PositiveInfinity;
            int j;
            for (j = 0; j < joints.Length; j++)
            {
                if (Math.Abs(joints[j]) <= 1e-6 || Math.Abs(joints[j] - cableLength) <= 1e-6) continue;
                double current = Math.Abs(center - joints[j]);
                if (current < nearestIntermediateJointDistance)
                {
                    hasNearestIntermediateJoint = true;
                    nearestIntermediateJoint = joints[j];
                    nearestIntermediateJointDistance = current;
                }
            }

            double testEndDistance = Math.Abs(center);
            double farEndDistance = Math.Abs(center - cableLength);
            if (testEndDistance <= terminalBoundaryM || farEndDistance <= terminalBoundaryM)
            {
                if (testEndDistance <= farEndDistance)
                {
                    nearest = 0.0;
                    distance = testEndDistance;
                    part = "测试端附近";
                }
                else
                {
                    nearest = cableLength;
                    distance = farEndDistance;
                    part = "末端终端附近";
                }
                return;
            }

            nearest = hasNearestIntermediateJoint
                ? nearestIntermediateJoint
                : (testEndDistance <= farEndDistance ? 0.0 : cableLength);
            distance = hasNearestIntermediateJoint
                ? nearestIntermediateJointDistance
                : Math.Min(testEndDistance, farEndDistance);
            if (hasNearestIntermediateJoint && distance <= tolerance)
                part = FormatNumber(nearest, 1) + " m接头附近";
            else
                part = "电缆本体";
        }
        private List<LocationGroup> MergeLocations(
            List<Candidate> retained, double operatingPhaseVoltageKv, double crossPhaseMergeToleranceM,
            bool englishOutput)
        {
            List<LocationGroup> groups = new List<LocationGroup>();
            double mergeTolerance = Math.Max(crossPhaseMergeToleranceM, 0.1);
            foreach (Candidate candidate in retained.OrderBy(delegate(Candidate c) { return c.Center; }))
            {
                LocationGroup match = null;
                double bestDistance = double.PositiveInfinity;
                int i;
                for (i = 0; i < groups.Count; i++)
                {
                    // A physical location may contain at most one candidate from
                    // each phase. Same-phase candidates are already formed above.
                    if ((groups[i].PhaseMask & (1 << candidate.Phase)) != 0) continue;
                    bool uncertaintyIntervalsOverlap = candidate.Low <= groups[i].High && candidate.High >= groups[i].Low;
                    bool completeLink = groups[i].Members.All(delegate(Candidate member)
                    {
                        return Math.Abs(candidate.Center - member.Center) <= mergeTolerance;
                    });
                    double distance = Math.Abs(candidate.Center - groups[i].Center);
                    if (completeLink && uncertaintyIntervalsOverlap && distance < bestDistance)
                    {
                        bestDistance = distance;
                        match = groups[i];
                    }
                }
                if (match == null) { match = new LocationGroup(); groups.Add(match); }
                match.Members.Add(candidate);
                RecalculateLocation(match, operatingPhaseVoltageKv, englishOutput);
            }
            return groups.OrderBy(delegate(LocationGroup group) { return group.Center; }).ToList();
        }

        private void RecalculateLocation(LocationGroup group, double operatingPhaseVoltageKv, bool englishOutput)
        {
            double weight = group.Members.Sum(delegate(Candidate c) { return (double)c.Points.Count; });
            // Each represented phase contributes equally to the reported physical
            // location. A phase with more pulses must not drag the shared position
            // away while its phase bit remains in the conclusion.
            List<double> phaseCenters = group.Members
                .GroupBy(delegate(Candidate c) { return c.Phase; })
                .Select(delegate(IGrouping<int, Candidate> phaseGroup)
                {
                    double phaseWeight = phaseGroup.Sum(delegate(Candidate c) { return (double)c.Points.Count; });
                    return phaseGroup.Sum(delegate(Candidate c) { return c.Center * c.Points.Count; }) / Math.Max(phaseWeight, 1.0);
                })
                .OrderBy(delegate(double center) { return center; })
                .ToList();
            group.Center = Median(phaseCenters);
            group.Low = group.Members.Min(delegate(Candidate c) { return c.Low; });
            group.High = group.Members.Max(delegate(Candidate c) { return c.High; });
            group.PulseCount = group.Members.Sum(delegate(Candidate c) { return c.Points.Count; });
            group.MaxCharge = group.Members.Max(delegate(Candidate c) { return c.MaxCharge; });
            group.MeanCharge = group.Members.Sum(delegate(Candidate c) { return c.MeanCharge * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.Possibility = group.Members.Max(delegate(Candidate c) { return c.Possibility; });
            int rawStatusCode = group.Members.Max(delegate(Candidate c) { return c.StatusCode; });
            group.PhaseMask = 0;
            foreach (Candidate c in group.Members) group.PhaseMask |= 1 << c.Phase;
            group.InceptionVoltage = group.Members.Min(delegate(Candidate c) { return c.InceptionVoltage; });
            List<Candidate> inceptionMembers = group.Members.Where(delegate(Candidate c)
            {
                return Math.Abs(c.InceptionVoltage - group.InceptionVoltage) <= 0.05;
            }).ToList();
            group.InceptionPulseCount = inceptionMembers.Sum(delegate(Candidate c) { return c.InceptionPulseCount; });
            group.InceptionApplicationGroups = inceptionMembers.Max(delegate(Candidate c) { return c.InceptionApplicationGroups; });
            group.InceptionPhaseMask = 0;
            foreach (Candidate c in inceptionMembers) group.InceptionPhaseMask |= 1 << c.Phase;
            group.OperatingPhaseVoltage = operatingPhaseVoltageKv;
            group.InceptionVoltageRatio = operatingPhaseVoltageKv > 0.0 ? group.InceptionVoltage / operatingPhaseVoltageKv : 0.0;
            group.VoltageRiskCode = 0;
            group.VoltageRiskPhaseMask = 0;
            group.VoltageResponseScore = group.Members.Sum(delegate(Candidate c) { return c.VoltageResponseScore * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.ApplicationGroups = group.Members.Max(delegate(Candidate c) { return c.ApplicationGroups; });
            group.InputProbabilityCoverage = group.Members.Sum(delegate(Candidate c) { return c.InputProbabilityCoverage * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.InputProbabilityScore = group.Members.Sum(delegate(Candidate c) { return c.InputProbabilityScore * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.SequenceEvidenceScore = group.Members.Sum(delegate(Candidate c) { return c.SequenceEvidenceScore * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.FeatureEvidenceScore = group.Members.Sum(delegate(Candidate c) { return c.FeatureCoherence * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.VerticalColumnScore = group.Members.Sum(delegate(Candidate c) { return c.VerticalColumnScore * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.FloatingPatternScore = group.Members.Sum(delegate(Candidate c) { return c.FloatingPatternScore * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.TrapezoidScore = group.Members.Max(delegate(Candidate c) { return c.Trapezoid; });
            group.NoiseSeparationScore = group.Members.Sum(delegate(Candidate c) { return c.NoiseSeparationScore * c.Points.Count; }) / Math.Max(weight, 1.0);
            group.MorphologyScore = Math.Max(Math.Max(group.VerticalColumnScore, group.FloatingPatternScore), group.TrapezoidScore);
            if (operatingPhaseVoltageKv > 0.0)
            {
                foreach (Candidate c in group.Members)
                {
                    if (c.InceptionVoltage <= operatingPhaseVoltageKv + 1e-6)
                    {
                        group.VoltageRiskPhaseMask |= 1 << c.Phase;
                        group.VoltageRiskCode = Math.Max(group.VoltageRiskCode, c.StatusCode >= 2 ? 2 : 1);
                    }
                }
            }
            List<PulsePoint> allPoints = group.Members.SelectMany(delegate(Candidate c) { return c.Points; }).ToList();
            List<PulsePoint> servicePoints = operatingPhaseVoltageKv > 0.0
                ? allPoints.Where(delegate(PulsePoint p) { return p.Voltage <= operatingPhaseVoltageKv + 0.05; }).ToList()
                : new List<PulsePoint>();
            group.ServiceVoltagePulseCount = servicePoints.Count;
            group.ServiceVoltageApplicationGroups = servicePoints.Select(delegate(PulsePoint p)
            {
                return p.Phase.ToString(CultureInfo.InvariantCulture) + "|" + Math.Round(p.Voltage, 1).ToString("F1", CultureInfo.InvariantCulture) + "|" + Math.Round(p.Application, 3).ToString("F3", CultureInfo.InvariantCulture);
            }).Distinct().Count();
            group.ServiceVoltageMaxCharge = servicePoints.Count == 0 ? 0.0 : servicePoints.Max(delegate(PulsePoint p) { return p.Charge; });
            group.ServiceVoltageSurgeImmediate = false;
            group.VoltageChargeGrowthRatio = 0.0;
            group.VoltageCountGrowthRatio = 0.0;
            double serviceVoltageEvidence = 0.50;
            if (operatingPhaseVoltageKv > 0.0)
            {
                double pulseScore = Clamp(group.ServiceVoltagePulseCount / 5.0, 0.0, 1.0);
                double applicationScore = Clamp(group.ServiceVoltageApplicationGroups / 3.0, 0.0, 1.0);
                double chargeScore = group.MaxCharge <= 0.0 ? 0.0 : Clamp(group.ServiceVoltageMaxCharge / group.MaxCharge, 0.0, 1.0);
                serviceVoltageEvidence = Clamp(0.35 * pulseScore + 0.45 * applicationScore + 0.20 * chargeScore, 0.0, 1.0);
            }
            double probabilityEvidence = group.InputProbabilityCoverage > 0.0 ? group.InputProbabilityScore : 0.50;
            group.SparseEvidenceScore = Clamp(0.26 * probabilityEvidence + 0.20 * group.SequenceEvidenceScore +
                0.14 * group.FeatureEvidenceScore + 0.13 * group.VoltageResponseScore +
                0.12 * serviceVoltageEvidence + 0.09 * group.MorphologyScore +
                0.06 * group.NoiseSeparationScore, 0.0, 1.0);
            List<Candidate> attributed = group.Members.Where(delegate(Candidate c) { return c.LocationPart != "电缆本体"; }).ToList();
            group.LocationPart = attributed.Count > 0 ? attributed.OrderBy(delegate(Candidate c) { return c.JointDistance; }).First().LocationPart : "电缆本体";
            bool terminalBoundary = group.LocationPart == "测试端附近" || group.LocationPart == "末端终端附近";
            bool intermediateJoint = !terminalBoundary && group.LocationPart != "电缆本体";
            ApplyStandardAssessment(group, terminalBoundary);
            // IEEE 400.2/400.4 provide diagnostic guidance rather than the
            // DL/T 1576 component-specific apparent-charge limits. English
            // mode cannot be elevated solely by a DL/T threshold and instead
            // continues through the combined-evidence decision path.
            if (englishOutput) group.StandardExceeded = false;
            bool sparseEvidence = group.PulseCount < 8 || (BitCount(group.PhaseMask) == 1 && group.PulseCount < 10);
            if (sparseEvidence) group.Possibility = Clamp(0.55 * group.Possibility + 0.45 * group.SparseEvidenceScore, 0.0, 1.0);
            bool morphologyAccepted = group.MorphologyScore >= 0.45 && group.NoiseSeparationScore >= 0.35;
            group.ActionCode = 1;
            if (!sparseEvidence && rawStatusCode >= 2 && (!terminalBoundary || englishOutput) && morphologyAccepted) group.ActionCode = 2;
            else if (group.VoltageRiskCode >= 2 && (!terminalBoundary || englishOutput) && morphologyAccepted) group.ActionCode = 2;
            else if (sparseEvidence)
            {
                if ((!terminalBoundary || englishOutput) && group.SparseEvidenceScore >= 0.72 && morphologyAccepted) group.ActionCode = 2;
                else if (group.SparseEvidenceScore >= 0.58 || group.VoltageRiskCode >= 1) group.ActionCode = 1;
                else group.ActionCode = 0;
            }
            bool repeatedIndependentEvidence = group.ApplicationGroups >= 2 || BitCount(group.PhaseMask) >= 2;
            bool concentrationAccepted = morphologyAccepted && repeatedIndependentEvidence;
            bool columnOrTrapezoidConcentration = group.VerticalColumnScore >= 0.60 || group.TrapezoidScore >= 0.60;
            bool serviceVoltageSurge = HasServiceVoltageChargeAndCountSurge(
                allPoints, operatingPhaseVoltageKv,
                out group.VoltageChargeGrowthRatio, out group.VoltageCountGrowthRatio);
            group.ServiceVoltageSurgeImmediate = concentrationAccepted && columnOrTrapezoidConcentration &&
                group.InceptionVoltageRatio > 0.0 && group.InceptionVoltageRatio <= 1.05 &&
                group.ServiceVoltagePulseCount >= 3 && group.ServiceVoltageApplicationGroups >= 2 &&
                group.VoltageResponseScore >= 0.70 && serviceVoltageSurge;
            if (!concentrationAccepted && group.ActionCode < 3) group.ActionCode = 0;
            group.HiddenJointCheck = group.LocationPart == "电缆本体" && group.MaxCharge > 100.0;
            group.NearbyIntermediateJointCheck = false;
            group.NearbyIntermediateJointPositionM = 0.0;
            group.NearbyIntermediateJointDistanceM = 0.0;
            if (group.LocationPart == "电缆本体")
            {
                Candidate nearestIntermediate = group.Members
                    .Where(delegate(Candidate c) { return c.HasNearestIntermediateJoint; })
                    .OrderBy(delegate(Candidate c) { return Math.Abs(group.Center - c.NearestIntermediateJoint); })
                    .FirstOrDefault();
                if (nearestIntermediate != null)
                {
                    double nearbyDistance = Math.Abs(group.Center - nearestIntermediate.NearestIntermediateJoint);
                    if (nearbyDistance <= 10.0)
                    {
                        group.NearbyIntermediateJointCheck = true;
                        group.NearbyIntermediateJointPositionM = nearestIntermediate.NearestIntermediateJoint;
                        group.NearbyIntermediateJointDistanceM = nearbyDistance;
                    }
                }
            }
            group.ServiceVoltageJointPriority = intermediateJoint && group.VoltageRiskCode >= 2 && concentrationAccepted;

            // DL/T component limits remain a reference, but cable body and
            // intermediate-joint actions require location/morphology evidence.
            // A joint that is already clearly active at service voltage is more
            // urgent than an isolated threshold exceedance.
            if (group.ServiceVoltageSurgeImmediate) group.ActionCode = 3;
            else if (terminalBoundary && group.StandardExceeded) group.ActionCode = 3;
            else if (group.ServiceVoltageJointPriority) group.ActionCode = 3;
            else if (intermediateJoint && group.StandardExceeded && concentrationAccepted)
                group.ActionCode = Math.Max(group.ActionCode, 2);
            else if (group.HiddenJointCheck && concentrationAccepted)
                group.ActionCode = Math.Max(group.ActionCode, 2);
            if (!morphologyAccepted && group.NoiseSeparationScore < 0.20 && sparseEvidence && !group.StandardExceeded) group.ActionCode = 0;
            ApplyInceptionVoltageDowngrade(group);
            // Two isolated pulses are not enough to establish a physical PD
            // region, even when their apparent charge is high or the provisional
            // component threshold is exceeded. Keep the location as an internal
            // record (ActionCode 0) so it can contribute to later trend analysis,
            // but never publish an Observe / Attention / Action marker from only
            // two pulses.
            if (group.PulseCount <= 2) group.ActionCode = 0;
            group.StatusCode = group.ActionCode >= 2 ? 2 : 1;
        }

        private static bool HasServiceVoltageChargeAndCountSurge(
            List<PulsePoint> points, double operatingPhaseVoltageKv,
            out double chargeGrowthRatio, out double countGrowthRatio)
        {
            chargeGrowthRatio = 0.0;
            countGrowthRatio = 0.0;
            if (points == null || points.Count < 8 || operatingPhaseVoltageKv <= 0.0) return false;

            List<IGrouping<double, PulsePoint>> levels = points
                .Where(delegate(PulsePoint p) { return p.Voltage > 0.0; })
                .GroupBy(delegate(PulsePoint p) { return Math.Round(p.Voltage, 1, MidpointRounding.AwayFromZero); })
                .OrderBy(delegate(IGrouping<double, PulsePoint> g) { return g.Key; })
                .ToList();
            if (levels.Count < 3) return false;

            IGrouping<double, PulsePoint> serviceLevel = levels
                .Where(delegate(IGrouping<double, PulsePoint> g) { return g.Key <= operatingPhaseVoltageKv + 0.05; })
                .OrderByDescending(delegate(IGrouping<double, PulsePoint> g) { return g.Key; })
                .FirstOrDefault();
            if (serviceLevel == null) return false;

            List<IGrouping<double, PulsePoint>> evaluated = levels
                .Where(delegate(IGrouping<double, PulsePoint> g) { return g.Key >= serviceLevel.Key - 0.05; })
                .ToList();
            if (evaluated.Count < 3) return false;

            List<double> chargeLevels = new List<double>();
            List<double> countPerApplicationLevels = new List<double>();
            foreach (IGrouping<double, PulsePoint> level in evaluated)
            {
                List<PulsePoint> levelPoints = level.ToList();
                chargeLevels.Add(Median(levelPoints.Select(delegate(PulsePoint p) { return p.Charge; }).ToList()));
                countPerApplicationLevels.Add(levelPoints.Count / (double)Math.Max(UniqueApplications(levelPoints), 1));
            }

            double serviceCharge = Math.Max(chargeLevels[0], 1e-9);
            double serviceCountPerApplication = Math.Max(countPerApplicationLevels[0], 1e-9);
            chargeGrowthRatio = chargeLevels[chargeLevels.Count - 1] / serviceCharge;
            countGrowthRatio = countPerApplicationLevels[countPerApplicationLevels.Count - 1] / serviceCountPerApplication;
            double chargeTrend = SpearmanByOrderedVoltage(chargeLevels);
            double countTrend = SpearmanByOrderedVoltage(countPerApplicationLevels);

            return chargeGrowthRatio >= 1.50 && countGrowthRatio >= 1.50 &&
                chargeTrend >= 0.70 && countTrend >= 0.70;
        }

        private static void ApplyInceptionVoltageDowngrade(LocationGroup group)
        {
            group.InceptionVoltageDowngradeLevels = 0;
            if (group.OperatingPhaseVoltage <= 0.0 || group.InceptionVoltageRatio <= 1.70) return;

            int levels = group.InceptionVoltageRatio > 2.0 ? 2 : 1;
            group.InceptionVoltageDowngradeLevels = levels;
            int minimumAction = group.StandardExceeded || group.HiddenJointCheck ? 1 : 0;
            group.ActionCode = Math.Max(minimumAction, group.ActionCode - levels);
        }

        private void ApplyStandardAssessment(LocationGroup group, bool terminalBoundary)
        {
            bool isNew = _standardProfile.ServiceYears <= 1.0;
            if (terminalBoundary)
            {
                group.StandardLimitPc = isNew ? 3000.0 : 5000.0;
                group.StandardComponent = "电缆终端（" + (isNew ? "投运1年及以内" : "投运超过1年") + "）";
            }
            else if (group.LocationPart != "电缆本体")
            {
                int jointType = _standardProfile.IntermediateJointType;
                if (jointType == 0) jointType = _standardProfile.CableBodyType == 1 ? 2 : 1;
                if (jointType == 2)
                {
                    group.StandardLimitPc = isNew ? 2000.0 : 3000.0;
                    group.StandardComponent = "PILC-PILC中间接头（" + (isNew ? "投运1年及以内" : "投运超过1年") + "）";
                }
                else
                {
                    group.StandardLimitPc = isNew ? 300.0 : 500.0;
                    group.StandardComponent = (jointType == 3 ? "XLPE-PILC" : "XLPE-XLPE") + "中间接头（" + (isNew ? "投运1年及以内" : "投运超过1年") + "）";
                }
            }
            else
            {
                group.StandardLimitPc = _standardProfile.CableBodyType == 1 ? 1000.0 : 100.0;
                group.StandardComponent = (_standardProfile.CableBodyType == 1 ? "PILC" : "XLPE") + "电缆本体";
            }
            group.StandardExceeded = group.MaxCharge > group.StandardLimitPc;
        }

        private static void FillFilteredScatter(AnalysisResult result, List<Candidate> retained)
        {
            List<double> values = new List<double>();
            foreach (Candidate candidate in retained)
                foreach (PulsePoint p in candidate.Points)
                {
                    values.Add(p.Phase);
                    values.Add(p.Position);
                    values.Add(p.Charge);
                }
            result.FilteredScatter = values.ToArray();
            result.FilteredScatterRows = values.Count / 3;
        }

        private static void FillCandidateMatrix(AnalysisResult result, List<Candidate> retained, bool englishOutput)
        {
            List<double> values = new List<double>();
            List<string> statuses = new List<string>();
            foreach (Candidate c in retained)
            {
                values.Add(c.Phase); values.Add(c.Center); values.Add(c.Low); values.Add(c.High);
                values.Add(c.Points.Count); values.Add(c.MaxCharge); values.Add(c.MeanCharge);
                values.Add(c.Possibility); values.Add(c.StatusCode); values.Add(c.NearestJoint);
                values.Add(c.JointDistance); values.Add(c.ApplicationGroups); values.Add(c.FeatureCoherence);
                values.Add(c.Trapezoid); values.Add(c.Index);
                statuses.Add(englishOutput
                    ? (c.StatusCode >= 2 ? "Higher confidence" : "Suspected - retest")
                    : c.Status);
            }
            result.CandidateMatrix = values.ToArray();
            result.CandidateRows = retained.Count;
            result.StatusArray = statuses.ToArray();
        }

        private static void FillLocationMatrix(AnalysisResult result, List<LocationGroup> locations)
        {
            result.LocationPointRows = locations.Select(g => g.Members
                .SelectMany(c => c.Points.Select(p => new int[] { p.Phase, p.SourceRow }))
                .ToArray()).ToArray();
            List<double> values = new List<double>();
            foreach (LocationGroup g in locations)
            {
                values.Add(g.PhaseMask); values.Add(g.Center); values.Add(g.Low); values.Add(g.High);
                values.Add(g.PulseCount); values.Add(g.MaxCharge); values.Add(g.MeanCharge);
                // Column 8 is the published action tier used by the textual conclusion.
                // Task 11 consumes this same value so chart annotations can never diverge
                // from Task 15''s final Observe / Attention / Action recommendation.
                values.Add(g.Possibility); values.Add(g.ActionCode); values.Add(g.Members.Count);
                values.Add(BitCount(g.PhaseMask));
            }
            result.LocationMatrix = values.ToArray();
            result.LocationRows = locations.Count;
        }

        private static void FillVoltageAssessmentMatrix(AnalysisResult result, List<LocationGroup> locations)
        {
            List<double> values = new List<double>();
            foreach (LocationGroup g in locations)
            {
                values.Add(g.PhaseMask);
                values.Add(g.Center);
                values.Add(g.InceptionVoltage);
                values.Add(g.OperatingPhaseVoltage);
                values.Add(g.InceptionVoltageRatio);
                values.Add(g.InceptionPulseCount);
                values.Add(g.InceptionApplicationGroups);
                values.Add(g.VoltageResponseScore);
                values.Add(g.VoltageRiskCode);
            }
            result.VoltageAssessmentMatrix = values.ToArray();
            result.VoltageAssessmentRows = locations.Count;
        }

        private static void FillLearning(AnalysisResult result, List<Candidate> candidates)
        {
            List<double> features = new List<double>();
            List<string> ids = new List<string>();
            foreach (Candidate c in candidates)
            {
                features.AddRange(c.Features);
                ids.Add(c.Id);
            }
            result.LearningFeatureMatrix = features.ToArray();
            result.LearningFeatureRows = candidates.Count;
            result.CandidateIds = ids.ToArray();
        }

        private static string BuildSummary(int total, int valid, List<LocationGroup> locations, double operatingPhaseVoltageKv, bool englishOutput, int conclusionAudienceType, CableStandardProfile standardProfile)
        {
            if (conclusionAudienceType == 0)
                return englishOutput ? BuildEnglishFieldSummary(total, valid, locations, operatingPhaseVoltageKv) : BuildChineseFieldSummary(total, valid, locations, operatingPhaseVoltageKv);
            return englishOutput
                ? BuildEnglishSummary(total, valid, locations, operatingPhaseVoltageKv)
                : BuildChineseSummary(total, valid, locations, operatingPhaseVoltageKv);
        }

        private static string BuildNoDischargeSummary(bool englishOutput)
        {
            if (englishOutput)
                return "The two-dimensional positioning-data matrix is empty; no discharge pulses were recorded and the result is No discharge.\r\nReference: IEEE Std 400.2 for VLF field testing and IEEE Std 400.4 for DAC field testing. This automated result supports field-test review and is not a stand-alone pass/fail decision.";
            return "定位数据二维矩阵为空，本次未记录到放电脉冲，判定为无放电。\r\n标准参考：振荡波测试按DL/T 1576-2016，现场局放测量与干扰判断参照DL/T 417-2019；本结果仅供现场试验和检修安排参考。";
        }

        private static string BuildChineseFieldSummary(int total, int valid, List<LocationGroup> locations, double operatingPhaseVoltageKv)
        {
            StringBuilder text = new StringBuilder();
            text.Append("本次测试HV-Xpert系统为您分析").Append(total).Append("个放电脉冲，剔除无效数据后保留")
                .Append(valid).Append("个有效脉冲，");
            if (locations.Count == 0)
            {
                text.Append("未发现明显的局部放电集中点。")
                    .Append("\r\n现场建议：保存本次记录并结合0 kV背景和原始波形确认；后续例行试验继续观察是否在同一位置重复出现。")
                    .Append("\r\n标准参考：振荡波测试按DL/T 1576-2016，现场局放测量与干扰判断参照DL/T 417-2019；国网配电电缆场景可结合Q/GDW 11838-2018。终端、接头和本体采用分部位联合评估，DL/T放电量限值仅作为参考之一，不作为单一结论依据。本结果仅供现场试验和检修安排参考。");
                return text.ToString();
            }

            List<LocationGroup> orderedAll = OrderLocationsForReport(locations);
            int recordOnlyCount = orderedAll.Count(delegate(LocationGroup g) { return g.ActionCode == 0; });
            List<LocationGroup> ordered = orderedAll.Where(delegate(LocationGroup g) { return g.ActionCode > 0; }).ToList();
            if (ordered.Count == 0)
            {
                text.Append("未发现满足联合证据条件的局部放电集中点；另有").Append(recordOnlyCount)
                    .Append("处弱证据或离散散点按无需处理记录，不生成处置标识，也不建议观察或单独复测。")
                    .Append("\r\n现场建议：保存本次记录并结合0 kV背景和原始波形确认；后续例行试验仅在同一位置形成重复、密集且形态一致的证据时再升级处置。")
                    .Append("\r\n标准参考：振荡波测试按DL/T 1576-2016，现场局放测量与干扰判断参照DL/T 417-2019。终端、接头和本体采用分部位联合评估，DL/T放电量限值仅作为参考之一，不作为单一结论依据。本结果仅供现场试验和检修安排参考。");
                return text.ToString();
            }
            int urgentCount = ordered.Count(delegate(LocationGroup g) { return g.ActionCode >= 3; });
            int priorityCount = ordered.Count(delegate(LocationGroup g) { return g.ActionCode == 2; });
            int observeCount = ordered.Count(delegate(LocationGroup g) { return g.ActionCode == 1; });
            text.Append("共发现").Append(ordered.Count).Append("处具有联合证据的位置，其中及时处理")
                .Append(urgentCount).Append("处、重点关注").Append(priorityCount).Append("处、建议观察")
                .Append(observeCount).Append("处。")
                .Append("\r\n现场简明结论：");
            int number = 1;
            foreach (LocationGroup group in ordered)
            {
                text.Append("\r\n").Append(number).Append(". ");
                AppendChineseFieldLocation(text, group, operatingPhaseVoltageKv);
                number++;
            }
            if (recordOnlyCount > 0)
                text.Append("\r\n另有").Append(recordOnlyCount).Append("处弱证据或离散散点按无需处理记录，不生成处置标识，也不列入建议观察或复测清单。");
            text.Append("\r\n现场建议：重点位置先核查原始波形、0 kV背景、校准记录和历史结果；证据较弱的位置可在下一次例行试验继续观察，不默认要求单独安排高成本复测。")
                .Append("\r\n标准参考：振荡波测试按DL/T 1576-2016，现场局放测量与干扰判断参照DL/T 417-2019；国网配电电缆场景可结合Q/GDW 11838-2018。终端、接头和本体采用分部位联合评估，DL/T放电量限值仅作为参考之一，不作为单一结论依据。本结果为HV-Xpert自动分析结果，仅供现场试验和检修安排参考。");
            return text.ToString();
        }

        private static void AppendChineseFieldLocation(StringBuilder text, LocationGroup group, double operatingPhaseVoltageKv)
        {
            bool priority = IsPriorityLocation(group);
            string level = group.ActionCode >= 3 ? "及时处理" : (priority ? "重点关注" : (group.ActionCode == 0 ? "暂作记录" : "建议观察"));
            text.Append(level).Append("：").Append(PhaseText(group.PhaseMask)).Append("约")
                .Append(FormatNumber(group.Center, 1)).Append(" m，").Append(group.LocationPart)
                .Append("，有效脉冲").Append(group.PulseCount).Append("个，最大放电量")
                .Append(FormatNumber(group.MaxCharge, 0)).Append(" pC，起始放电电压约")
                .Append(FormatNumber(group.InceptionVoltage, 1)).Append(" kV");
            if (operatingPhaseVoltageKv > 0.0)
                text.Append("（运行相电压").Append(FormatNumber(operatingPhaseVoltageKv, 1)).Append(" kV）");
            text.Append("。判断：");
            if (group.ActionCode >= 3)
            {
                if (group.ServiceVoltageSurgeImmediate)
                {
                    text.Append("运行相电压即出现柱状或梯形集中性放电，升压后放电量和单位加压次数分别跃增约")
                        .Append(FormatNumber(group.VoltageChargeGrowthRatio, 1)).Append("倍和")
                        .Append(FormatNumber(group.VoltageCountGrowthRatio, 1)).Append("倍，形成多级电压联合证据，应及时处理");
                    if (group.LocationPart == "测试端附近" || group.LocationPart == "末端终端附近")
                        text.Append("；先排除端部周围放电干扰，排除后仍满足上述特征再执行处置");
                }
                else if (group.ServiceVoltageJointPriority)
                    text.Append("中间接头在运行相电压附近已出现明显、重复且集中性放电，不以DL/T 1576放电量阈值作为唯一判据，应及时工程核查处理");
                else
                {
                    text.Append(group.StandardComponent).Append("最大放电量超过DL/T 1576参考限值")
                        .Append(FormatNumber(group.StandardLimitPc, 0)).Append(" pC");
                    if (group.LocationPart == "测试端附近" || group.LocationPart == "末端终端附近")
                        text.Append("；先排除试验绝缘距离不足等端部周围放电干扰，排除干扰后仍超限应及时处理");
                    else
                        text.Append("，且具有集中性联合证据，应及时处理");
                }
            }
            else if (group.ActionCode == 0 && group.NoiseSeparationScore < 0.35)
                text.Append("放电量与背景噪声接近，目前不能确认该处存在明确局放");
            else if (group.ActionCode == 0 && group.MorphologyScore < 0.45)
                text.Append("放电点排列尚不稳定，目前不能确认该处存在明确局放");
            else if (priority)
                text.Append("该位置重复出现集中放电，建议优先核查");
            else
                text.Append("该位置有重复集中现象，建议结合已有记录继续确认");
            if (group.FloatingPatternScore >= 0.72)
                text.Append("；相位间隔和幅值特征符合悬浮放电表现");
            AppendChineseComponentAndPdivNotes(text, group);
            if (group.VoltageRiskCode >= 2)
                text.Append("；运行相电压下已经出现明显记录，电缆正常运行时可能反复放电，应提高处理优先级");
            else if (group.VoltageRiskCode == 1)
                text.Append("；运行相电压下已有疑似记录，建议加强后续观察");
            text.Append("。");
        }

        private static string BuildEnglishFieldSummary(int total, int valid, List<LocationGroup> locations, double operatingPhaseVoltageKv)
        {
            StringBuilder text = new StringBuilder();
            text.Append("HV-Xpert reviewed ").Append(total).Append(" discharge pulses; ").Append(valid)
                .Append(" valid pulses remained after invalid records were removed. ");
            if (locations.Count == 0)
            {
                text.Append("No clear concentrated PD location was identified.")
                    .Append("\r\nField action: retain the record and confirm it against the 0 kV background, calibration record, and original waveform; continue routine trending.")
                    .Append("\r\nReference: IEEE Std 400.2 for VLF field testing and IEEE Std 400.4 for DAC field testing. This automated result supports field decisions and is not a stand-alone pass/fail decision.");
                return text.ToString();
            }

            List<LocationGroup> orderedAll = OrderLocationsForReport(locations);
            int recordOnlyCount = orderedAll.Count(delegate(LocationGroup g) { return g.ActionCode == 0; });
            List<LocationGroup> ordered = orderedAll.Where(delegate(LocationGroup g) { return g.ActionCode > 0; }).ToList();
            if (ordered.Count == 0)
            {
                text.Append("No concentrated PD location met the combined-evidence criteria; ")
                    .Append(recordOnlyCount).Append(" scattered location(s) are retained for record only, without an observation or stand-alone retest recommendation.")
                    .Append("\r\nField action: retain the test record and escalate only if repeated, dense and morphologically consistent evidence forms at the same location.")
                    .Append("\r\nReference: IEEE Std 400.2 for VLF field testing and IEEE Std 400.4 for DAC field testing. This automated result supports field decisions and is not a stand-alone pass/fail decision.");
                return text.ToString();
            }
            int urgentCount = ordered.Count(delegate(LocationGroup g) { return g.ActionCode >= 3; });
            int priorityCount = ordered.Count(delegate(LocationGroup g) { return g.ActionCode == 2; });
            int observeCount = ordered.Count(delegate(LocationGroup g) { return g.ActionCode == 1; });
            text.Append(ordered.Count).Append(ordered.Count == 1 ? " location has" : " locations have")
                .Append(" combined evidence: ").Append(urgentCount).Append(" timely action, ")
                .Append(priorityCount).Append(" priority and ").Append(observeCount).Append(" observation.")
                .Append("\r\nField summary:");
            int number = 1;
            foreach (LocationGroup group in ordered)
            {
                text.Append("\r\n").Append(number).Append(". ");
                AppendEnglishFieldLocation(text, group, operatingPhaseVoltageKv);
                number++;
            }
            if (recordOnlyCount > 0)
                text.Append("\r\nA further ").Append(recordOnlyCount).Append(" scattered location(s) are retained for record only and are excluded from the observation/retest list.");
            text.Append("\r\nField action: review priority locations against the original waveform, 0 kV background, calibration record, and prior results. Lower-evidence locations may be trended at the next routine test without defaulting to a costly stand-alone retest.")
                .Append("\r\nReference: IEEE Std 400.2 for VLF field testing and IEEE Std 400.4 for DAC field testing. This automated HV-Xpert result supports field decisions and is not a stand-alone pass/fail decision.");
            return text.ToString();
        }

        private static void AppendEnglishFieldLocation(StringBuilder text, LocationGroup group, double operatingPhaseVoltageKv)
        {
            bool priority = IsPriorityLocation(group);
            string level = group.ActionCode >= 3 ? "Timely action" : (priority ? "Priority" : (group.ActionCode == 0 ? "Record only" : "Observe"));
            text.Append(level).Append(": ").Append(PhaseTextEnglish(group.PhaseMask)).Append(" near ")
                .Append(FormatNumber(group.Center, 1)).Append(" m, ").Append(LocationPartEnglish(group))
                .Append(", ").Append(group.PulseCount).Append(" valid pulse(s), maximum apparent charge ")
                .Append(FormatNumber(group.MaxCharge, 0)).Append(" pC, estimated PDIV ")
                .Append(FormatNumber(group.InceptionVoltage, 1)).Append(" kV");
            if (operatingPhaseVoltageKv > 0.0)
                text.Append(" (service phase-to-ground voltage ").Append(FormatNumber(operatingPhaseVoltageKv, 1)).Append(" kV)");
            text.Append(". Assessment: ");
            if (group.ActionCode >= 3)
            {
                if (group.ServiceVoltageSurgeImmediate)
                {
                    text.Append("a column-like or trapezoidal concentration is present from service voltage, while apparent charge and pulse count per application rise by about ")
                        .Append(FormatNumber(group.VoltageChargeGrowthRatio, 1)).Append("x and ")
                        .Append(FormatNumber(group.VoltageCountGrowthRatio, 1)).Append("x across higher voltage levels; this combined evidence warrants timely engineering action");
                    if (group.LocationPart == "测试端附近" || group.LocationPart == "末端终端附近")
                        text.Append("; first exclude external termination discharge and act only if the combined pattern remains");
                }
                else if (group.ServiceVoltageJointPriority)
                    text.Append("clear, repeated and concentrated PD is already present at this intermediate joint at or below service voltage; timely engineering action is warranted without relying on a single apparent-charge threshold");
                else
                {
                    text.Append("combined magnitude, location concentration, repeated-application and voltage-response evidence warrants timely engineering action");
                    if (group.LocationPart == "测试端附近" || group.LocationPart == "末端终端附近")
                        text.Append("; first exclude external termination discharge caused by insufficient test insulation clearance, then reassess the remaining evidence");
                }
            }
            else if (group.ActionCode == 0 && group.NoiseSeparationScore < 0.35)
                text.Append("apparent charge is comparable with background noise; clear PD is not established");
            else if (group.ActionCode == 0 && group.MorphologyScore < 0.45)
                text.Append("the location pattern is not stable enough to establish clear PD");
            else if (priority)
                text.Append("repeated concentrated activity warrants priority review");
            else
                text.Append("repeated activity warrants observation against existing records");
            if (group.FloatingPatternScore >= 0.72)
                text.Append("; phase spacing and similar amplitudes are consistent with a floating-potential pattern");
            AppendEnglishComponentAndPdivNotes(text, group);
            if (group.VoltageRiskCode >= 2)
                text.Append("; activity at service voltage may recur in operation and raises the action priority");
            else if (group.VoltageRiskCode == 1)
                text.Append("; suspected service-voltage records warrant closer trending");
            text.Append(".");
        }
        private static string BuildChineseSummary(int total, int valid, List<LocationGroup> locations, double operatingPhaseVoltageKv)
        {
            StringBuilder text = new StringBuilder();
            text.Append("本次测试HV-Xpert系统为您分析").Append(total).Append("个放电脉冲，剔除无效数据后保留")
                .Append(valid).Append("个有效脉冲，");
            if (locations.Count == 0)
            {
                text.Append("综合各试验电压、重复加压及定位分布，未发现明显的局部放电集中点。")
                    .Append("\r\n").Append(ChineseRecommendation());
                return text.ToString();
            }

            List<LocationGroup> orderedAll = OrderLocationsForReport(locations);
            int recordOnlyCount = orderedAll.Count(delegate(LocationGroup g) { return g.ActionCode == 0; });
            List<LocationGroup> ordered = orderedAll.Where(delegate(LocationGroup g) { return g.ActionCode > 0; }).ToList();
            if (ordered.Count == 0)
            {
                text.Append("综合各试验电压、重复加压及定位分布，未发现满足联合证据条件的局部放电集中点；另有")
                    .Append(recordOnlyCount).Append("处离散散点仅作记录，不建议观察或单独复测。")
                    .Append("\r\n").Append(ChineseRecommendation());
                return text.ToString();
            }
            List<LocationGroup> priority = ordered.Where(delegate(LocationGroup g) { return IsPriorityLocation(g); }).ToList();
            List<LocationGroup> review = ordered.Where(delegate(LocationGroup g) { return !IsPriorityLocation(g); }).ToList();
            bool sharedInception = HasSharedInceptionVoltage(ordered);
            bool hasInputProbability = ordered.Any(delegate(LocationGroup g) { return g.InputProbabilityCoverage > 0.0; });
            text.Append(hasInputProbability ? "综合各试验电压、重复加压、工程匹配证据及定位分布，共识别" : "综合各试验电压、重复加压及定位分布，共识别").Append(ordered.Count)
                .Append("处需要关注的位置，其中重点关注").Append(priority.Count)
                .Append("处、计划复核/跟踪").Append(review.Count).Append("处。");

            int number = 1;
            if (priority.Count > 0)
            {
                text.Append("\r\n重点关注位置：");
                foreach (LocationGroup g in priority)
                {
                    text.Append("\r\n").Append(number).Append(". ");
                    AppendChineseLocation(text, g, operatingPhaseVoltageKv, sharedInception, true);
                    number++;
                }
            }
            if (review.Count > 0)
            {
                text.Append("\r\n计划复核/跟踪位置：");
                foreach (LocationGroup g in review)
                {
                    text.Append("\r\n").Append(number).Append(". ");
                    AppendChineseLocation(text, g, operatingPhaseVoltageKv, sharedInception, false);
                    number++;
                }
            }
            if (recordOnlyCount > 0)
                text.Append("\r\n另有").Append(recordOnlyCount).Append("处离散散点仅作记录，不列入观察或复测清单。");
            AppendChineseVoltageOverview(text, ordered, operatingPhaseVoltageKv, sharedInception);
            text.Append("\r\n").Append(ChineseRecommendation());
            return text.ToString();
        }

        private static string BuildEnglishSummary(int total, int valid, List<LocationGroup> locations, double operatingPhaseVoltageKv)
        {
            StringBuilder text = new StringBuilder();
            text.Append("HV-Xpert analyzed ").Append(total).Append(" discharge pulses during this test; ")
                .Append(valid).Append(" valid pulses remained after invalid records were removed. ");
            if (locations.Count == 0)
            {
                text.Append("No distinct concentrated PD location was identified from the DAC voltage levels, repeated applications, and location distribution.")
                    .Append("\r\n").Append(EnglishRecommendation());
                return text.ToString();
            }

            List<LocationGroup> orderedAll = OrderLocationsForReport(locations);
            int recordOnlyCount = orderedAll.Count(delegate(LocationGroup g) { return g.ActionCode == 0; });
            List<LocationGroup> ordered = orderedAll.Where(delegate(LocationGroup g) { return g.ActionCode > 0; }).ToList();
            if (ordered.Count == 0)
            {
                text.Append("No concentrated PD location met the combined-evidence criteria; ")
                    .Append(recordOnlyCount).Append(" scattered location(s) are retained for record only, without an observation or stand-alone retest recommendation.")
                    .Append("\r\n").Append(EnglishRecommendation());
                return text.ToString();
            }
            List<LocationGroup> priority = ordered.Where(delegate(LocationGroup g) { return IsPriorityLocation(g); }).ToList();
            List<LocationGroup> review = ordered.Where(delegate(LocationGroup g) { return !IsPriorityLocation(g); }).ToList();
            bool sharedInception = HasSharedInceptionVoltage(ordered);
            text.Append(ordered.Count).Append(ordered.Count == 1 ? " location requires" : " locations require")
                .Append(" attention: ").Append(priority.Count).Append(" priority and ").Append(review.Count).Append(" for planned review/trending.");

            int number = 1;
            if (priority.Count > 0)
            {
                text.Append("\r\nPriority locations:");
                foreach (LocationGroup g in priority)
                {
                    text.Append("\r\n").Append(number).Append(". ");
                    AppendEnglishLocation(text, g, operatingPhaseVoltageKv, sharedInception, true);
                    number++;
                }
            }
            if (review.Count > 0)
            {
                text.Append("\r\nLocations for planned review/trending:");
                foreach (LocationGroup g in review)
                {
                    text.Append("\r\n").Append(number).Append(". ");
                    AppendEnglishLocation(text, g, operatingPhaseVoltageKv, sharedInception, false);
                    number++;
                }
            }
            if (recordOnlyCount > 0)
                text.Append("\r\nA further ").Append(recordOnlyCount).Append(" scattered location(s) are retained for record only and excluded from the observation/retest list.");
            AppendEnglishVoltageOverview(text, ordered, operatingPhaseVoltageKv, sharedInception);
            text.Append("\r\n").Append(EnglishRecommendation());
            return text.ToString();
        }

        private static List<LocationGroup> OrderLocationsForReport(List<LocationGroup> locations)
        {
            return locations.OrderByDescending(delegate(LocationGroup g) { return IsPriorityLocation(g); })
                .ThenByDescending(delegate(LocationGroup g) { return g.VoltageRiskCode; })
                .ThenByDescending(delegate(LocationGroup g) { return g.Possibility; })
                .ThenByDescending(delegate(LocationGroup g) { return g.PulseCount; })
                .ThenBy(delegate(LocationGroup g) { return g.Center; }).ToList();
        }

        private static void AppendChineseComponentAndPdivNotes(StringBuilder text, LocationGroup group)
        {
            if (group.NearbyIntermediateJointCheck)
            {
                text.Append("；该本体放电位置前后10 m范围内存在已登记的")
                    .Append(FormatNumber(group.NearbyIntermediateJointPositionM, 1)).Append(" m中间接头（相距")
                    .Append(FormatNumber(group.NearbyIntermediateJointDistanceM, 1)).Append(" m），优先排查该接头及其连接界面");
                if (group.HiddenJointCheck)
                    text.Append("；该位置当前按电缆本体归类且最大放电量超过100 pC，接头与本体不能采用同一放电量评估体系");
            }
            else if (group.HiddenJointCheck)
                text.Append("；该位置当前按电缆本体归类且最大放电量超过100 pC，接头与本体不能采用同一放电量评估体系，优先核实该处是否存在未录入或未显示的中间接头");
            if (group.InceptionVoltageDowngradeLevels >= 2)
                text.Append("；PDIV明显高于2.0U0，综合提醒已降低两个等级");
            else if (group.InceptionVoltageDowngradeLevels == 1)
                text.Append("；PDIV明显高于1.7U0，综合提醒已降低一个等级");
        }

        private static void AppendEnglishComponentAndPdivNotes(StringBuilder text, LocationGroup group)
        {
            if (group.NearbyIntermediateJointCheck)
            {
                text.Append("; a registered intermediate joint at ")
                    .Append(FormatNumber(group.NearbyIntermediateJointPositionM, 1)).Append(" m lies within the +/-10 m body-discharge review zone (distance ")
                    .Append(FormatNumber(group.NearbyIntermediateJointDistanceM, 1)).Append(" m); inspect that joint and its interfaces first");
                if (group.HiddenJointCheck)
                    text.Append("; this location is currently classified as cable body and exceeds 100 pC, while body and joint evidence require different evaluation");
            }
            else if (group.HiddenJointCheck)
                text.Append("; this location is currently classified as cable body and exceeds 100 pC; because body and joint evidence require different evaluation, first confirm whether an unrecorded or undisplayed intermediate joint exists here");
            if (group.InceptionVoltageDowngradeLevels >= 2)
                text.Append("; PDIV is clearly above 2.0 U0, so the combined alert has been reduced by two levels");
            else if (group.InceptionVoltageDowngradeLevels == 1)
                text.Append("; PDIV is clearly above 1.7 U0, so the combined alert has been reduced by one level");
        }

        private static bool IsPriorityLocation(LocationGroup group)
        {
            return group.ActionCode >= 2;
        }

        private static bool HasSharedInceptionVoltage(List<LocationGroup> locations)
        {
            if (locations.Count < 2) return false;
            double reference = locations[0].InceptionVoltage;
            return locations.All(delegate(LocationGroup g) { return Math.Abs(g.InceptionVoltage - reference) <= 0.11; });
        }

        private static void AppendChineseLocation(StringBuilder text, LocationGroup g, double operatingPhaseVoltageKv, bool sharedInception, bool priority)
        {
            bool sparse = g.PulseCount < 8 || (BitCount(g.PhaseMask) == 1 && g.PulseCount < 10);
            text.Append(PhaseText(g.PhaseMask)).Append("约").Append(FormatNumber(g.Center, 1)).Append(" m（")
                .Append(FormatNumber(g.Low, 1)).Append("～").Append(FormatNumber(g.High, 1)).Append(" m，")
                .Append(g.LocationPart).Append("）：").Append(g.PulseCount).Append("个有效脉冲；最大/平均放电量")
                .Append(FormatNumber(g.MaxCharge, 0)).Append("/").Append(FormatNumber(g.MeanCharge, 0))
                .Append(" pC；局部放电可能性").Append(FormatNumber(g.Possibility * 100.0, 0)).Append("%");
            if (sparse)
            {
                text.Append("，少量脉冲综合证据").Append(FormatNumber(g.SparseEvidenceScore * 100.0, 0)).Append("%")
                    .Append("（重复加压").Append(g.ApplicationGroups).Append("组");
                if (g.InputProbabilityCoverage > 0.0)
                    text.Append("，工程匹配证据").Append(FormatNumber(g.InputProbabilityScore * 100.0, 0)).Append("%");
                text.Append("，定位分布判据").Append(FormatNumber(g.MorphologyScore * 100.0, 0)).Append("%")
                    .Append("，与背景噪声区分度").Append(FormatNumber(g.NoiseSeparationScore * 100.0, 0)).Append("%）");
                if (g.FloatingPatternScore >= 0.72) text.Append("，符合悬浮放电型相位间隔及幅值规律");
            }
            if (!sharedInception)
            {
                text.Append("；PDIV约").Append(FormatNumber(g.InceptionVoltage, 1)).Append(" kV");
                if (operatingPhaseVoltageKv > 0.0)
                    text.Append("（").Append(FormatNumber(g.InceptionVoltageRatio, 2)).Append("×运行相电压").Append(FormatNumber(operatingPhaseVoltageKv, 1)).Append(" kV）");
            }
            if (g.ServiceVoltagePulseCount > 0)
                text.Append("；运行相电压及以下").Append(g.ServiceVoltagePulseCount).Append("个，最大")
                    .Append(FormatNumber(g.ServiceVoltageMaxCharge, 0)).Append(" pC");
            if (g.VoltageResponseScore >= 0.70) text.Append("；升压响应总体增强");
            text.Append("。处理：");
            if (g.ActionCode >= 3)
            {
                if (g.ServiceVoltageSurgeImmediate)
                {
                    text.Append("运行相电压即出现柱状或梯形集中性放电，升压后放电量和单位加压次数分别跃增约")
                        .Append(FormatNumber(g.VoltageChargeGrowthRatio, 1)).Append("倍和")
                        .Append(FormatNumber(g.VoltageCountGrowthRatio, 1)).Append("倍，形成多级电压联合证据，应及时处理");
                    if (g.LocationPart == "测试端附近" || g.LocationPart == "末端终端附近")
                        text.Append("；先排除试验绝缘距离不足等端部周围放电干扰，排除干扰后仍满足上述特征再执行处置");
                }
                else if (g.ServiceVoltageJointPriority)
                    text.Append("中间接头在运行相电压附近已出现明显、重复且集中性放电，不以DL/T 1576放电量阈值作为唯一判据，应及时工程核查处理");
                else
                {
                    text.Append(g.StandardComponent).Append("最大放电量超过DL/T 1576参考限值")
                        .Append(FormatNumber(g.StandardLimitPc, 0)).Append(" pC");
                    if (g.LocationPart == "测试端附近" || g.LocationPart == "末端终端附近")
                        text.Append("；先排除试验绝缘距离不足等端部周围放电干扰，排除干扰后仍超限应及时处理");
                    else
                        text.Append("，并已形成集中性联合证据，应及时处理");
                }
            }
            else if (priority && sparse)
                text.Append("记录虽少但多项证据一致，列为重点关注；先核查已有波形、校准及历史记录，不以单独复测作为唯一措施");
            else if (priority) text.Append("局部放电集中明显，列为重点关注并优先开展工程复核");
            else if (g.ActionCode == 0)
            {
                if (g.NoiseSeparationScore < 0.35) text.Append("放电量与背景噪声接近，当前不支持列为明确局放位置");
                else if (g.MorphologyScore < 0.45) text.Append("未形成稳定纵向柱状分布，且不符合悬浮放电型相位/幅值规律");
                else text.Append("现有证据偏弱");
                text.Append("；暂不单独安排高成本复测，下次例行试验作趋势跟踪，证据增强时升级处置");
            }
            else if (g.LocationPart == "测试端附近" || g.LocationPart == "末端终端附近")
                text.Append("定位在终端边界，结合校准和原始波形作计划复核");
            else if (sparse)
            {
                if (g.NoiseSeparationScore < 0.35) text.Append("放电量与背景噪声接近，证据有限");
                else if (g.MorphologyScore < 0.45) text.Append("纵向柱状或悬浮放电型规律不足，证据有限");
                else text.Append("证据中等");
                text.Append("；纳入计划复核或下一次例行试验跟踪，暂不要求单独复测");
            }
            else text.Append("存在疑似集中，纳入计划复核");
            AppendChineseComponentAndPdivNotes(text, g);
            if (g.VoltageRiskCode >= 2)
                text.Append("；运行相电压下已有明显记录，正常运行时可能反复发生局部放电，不以本次放电量是否超过限值为前提，应提高处置优先级");
            else if (g.VoltageRiskCode == 1)
                text.Append("；运行相电压下已有疑似记录，建议加强趋势跟踪");
            text.Append("。");
        }

        private static void AppendChineseVoltageOverview(StringBuilder text, List<LocationGroup> locations, double operatingPhaseVoltageKv, bool sharedInception)
        {
            if (sharedInception)
            {
                LocationGroup reference = locations[0];
                text.Append("\r\n电压特征：上述位置的PDIV均约").Append(FormatNumber(reference.InceptionVoltage, 1)).Append(" kV");
                if (operatingPhaseVoltageKv > 0.0)
                    text.Append("，为运行相电压").Append(FormatNumber(operatingPhaseVoltageKv, 1)).Append(" kV的")
                        .Append(FormatNumber(reference.InceptionVoltageRatio, 2)).Append("倍");
                text.Append("。");
            }
            if (operatingPhaseVoltageKv > 0.0 && locations.All(delegate(LocationGroup g) { return g.VoltageRiskCode == 0; }))
                text.Append("本次保留记录中，运行相电压及以下未形成上述位置的明显集中记录。");
        }

        private static void AppendEnglishLocation(StringBuilder text, LocationGroup g, double operatingPhaseVoltageKv, bool sharedInception, bool priority)
        {
            bool sparse = g.PulseCount < 8 || (BitCount(g.PhaseMask) == 1 && g.PulseCount < 10);
            text.Append(PhaseTextEnglish(g.PhaseMask)).Append(" near ").Append(FormatNumber(g.Center, 1)).Append(" m (")
                .Append(FormatNumber(g.Low, 1)).Append("-").Append(FormatNumber(g.High, 1)).Append(" m, ")
                .Append(LocationPartEnglish(g)).Append("): ").Append(g.PulseCount)
                .Append(" valid pulses; maximum apparent charge / mean apparent charge ").Append(FormatNumber(g.MaxCharge, 0)).Append("/")
                .Append(FormatNumber(g.MeanCharge, 0)).Append(" pC; PD confidence ")
                .Append(FormatNumber(g.Possibility * 100.0, 0)).Append("%");
            if (sparse)
            {
                text.Append("; sparse-event evidence ").Append(FormatNumber(g.SparseEvidenceScore * 100.0, 0)).Append("%")
                    .Append(" from ").Append(g.ApplicationGroups).Append(" applications");
                if (g.InputProbabilityCoverage > 0.0)
                    text.Append(" and engineering matching evidence ").Append(FormatNumber(g.InputProbabilityScore * 100.0, 0)).Append("%");
                text.Append("; location-distribution evidence ").Append(FormatNumber(g.MorphologyScore * 100.0, 0)).Append("%")
                    .Append(" and separation from background noise ").Append(FormatNumber(g.NoiseSeparationScore * 100.0, 0)).Append("%");
                if (g.FloatingPatternScore >= 0.72) text.Append("; floating-potential pattern with regular phase spacing and similar amplitudes");
            }
            if (!sharedInception)
            {
                text.Append("; estimated PD inception voltage (PDIV) ").Append(FormatNumber(g.InceptionVoltage, 1)).Append(" kV");
                if (operatingPhaseVoltageKv > 0.0) text.Append(" (").Append(FormatNumber(g.InceptionVoltageRatio, 2)).Append(" x service voltage)");
            }
            if (g.ServiceVoltagePulseCount > 0)
                text.Append("; ").Append(g.ServiceVoltagePulseCount).Append(" pulse(s) at/below service voltage, max ")
                    .Append(FormatNumber(g.ServiceVoltageMaxCharge, 0)).Append(" pC");
            if (g.VoltageResponseScore >= 0.70) text.Append("; response generally increases with voltage");
            text.Append(". Action: ");
            if (g.ActionCode >= 3)
            {
                if (g.ServiceVoltageSurgeImmediate)
                {
                    text.Append("a column-like or trapezoidal concentration is present from service voltage, while apparent charge and pulse count per application rise by about ")
                        .Append(FormatNumber(g.VoltageChargeGrowthRatio, 1)).Append("x and ")
                        .Append(FormatNumber(g.VoltageCountGrowthRatio, 1)).Append("x across higher voltage levels; this combined multi-level evidence warrants timely engineering action");
                    if (g.LocationPart == "测试端附近" || g.LocationPart == "末端终端附近")
                        text.Append("; first exclude external termination discharge caused by insufficient test insulation clearance, and act only if the combined pattern remains");
                }
                else if (g.ServiceVoltageJointPriority)
                    text.Append("clear, repeated and concentrated PD is already present at this intermediate joint at or below service voltage; timely engineering action is warranted without relying on a single apparent-charge threshold");
                else
                {
                    text.Append("combined magnitude, location concentration, repeated-application and voltage-response evidence warrants timely engineering action");
                    if (g.LocationPart == "测试端附近" || g.LocationPart == "末端终端附近")
                        text.Append("; first exclude external termination discharge caused by insufficient test insulation clearance, then reassess the remaining evidence");
                }
            }
            else if (priority && sparse) text.Append("limited count but convergent evidence; prioritize review of existing waveforms, calibration, and history without treating a dedicated retest as the only option");
            else if (priority) text.Append("distinct concentration; prioritize engineering review");
            else if (g.ActionCode == 0)
            {
                if (g.NoiseSeparationScore < 0.35) text.Append("apparent charge is comparable with background noise");
                else if (g.MorphologyScore < 0.45) text.Append("no stable vertical location column or floating-potential phase/amplitude pattern");
                else text.Append("weak current evidence");
                text.Append("; do not schedule a costly stand-alone retest by default, and trend at the next routine test");
            }
            else if (g.LocationPart == "测试端附近" || g.LocationPart == "末端终端附近") text.Append("termination boundary; plan review against calibration and original waveforms");
            else if (sparse) text.Append("moderate evidence; include in planned review or the next routine test without requiring an immediate stand-alone retest");
            else text.Append("suspected concentration; include in planned review");
            AppendEnglishComponentAndPdivNotes(text, g);
            if (g.VoltageRiskCode >= 2) text.Append("; possible repetitive PD at service voltage raises action priority");
            else if (g.VoltageRiskCode == 1) text.Append("; suspected service-voltage records warrant closer trending");
            text.Append(".");
        }

        private static void AppendEnglishVoltageOverview(StringBuilder text, List<LocationGroup> locations, double operatingPhaseVoltageKv, bool sharedInception)
        {
            if (sharedInception)
            {
                LocationGroup reference = locations[0];
                text.Append("\r\nVoltage characteristic: all listed locations have an estimated PD inception voltage (PDIV) near ")
                    .Append(FormatNumber(reference.InceptionVoltage, 1)).Append(" kV");
                if (operatingPhaseVoltageKv > 0.0)
                    text.Append(", ").Append(FormatNumber(reference.InceptionVoltageRatio, 2)).Append(" times the ")
                        .Append(FormatNumber(operatingPhaseVoltageKv, 1)).Append(" kV operating phase-to-ground voltage");
                text.Append(".");
            }
            if (operatingPhaseVoltageKv > 0.0 && locations.All(delegate(LocationGroup g) { return g.VoltageRiskCode == 0; }))
                text.Append(" No distinct concentration at or below service voltage was established in the retained records.");
        }

        private static string ChineseRecommendation()
        {
            return "处置建议：重点位置优先利用原始波形、0 kV背景、校准和历史记录核查；低证据位置可纳入下一次例行试验趋势跟踪，不默认要求单独安排高成本复测。标准依据：振荡波试验方法按DL/T 1576-2016，现场局放测量与干扰判断参照DL/T 417-2019；国网配电电缆场景可结合Q/GDW 11838-2018。终端、接头和本体采用分部位联合评估，DL/T放电量限值仅作为参考之一，不作为单一结论依据。本结果为HV-Xpert自动分析结果，仅供现场试验和检修决策参考。";
        }

        private static string StandardComponentEnglish(LocationGroup group)
        {
            bool isNew = group.StandardComponent != null && group.StandardComponent.IndexOf("1年及以内", StringComparison.Ordinal) >= 0;
            string age = isNew ? "commissioned for no more than one year" : "commissioned for more than one year";
            if (group.LocationPart == "测试端附近" || group.LocationPart == "末端终端附近")
                return "Cable termination (" + age + ")";
            if (group.LocationPart == "电缆本体")
                return (group.StandardLimitPc >= 1000.0 ? "PILC" : "XLPE") + " cable body";
            if (group.StandardComponent != null && group.StandardComponent.IndexOf("PILC-PILC", StringComparison.Ordinal) >= 0)
                return "PILC-PILC intermediate joint (" + age + ")";
            if (group.StandardComponent != null && group.StandardComponent.IndexOf("XLPE-PILC", StringComparison.Ordinal) >= 0)
                return "XLPE-PILC intermediate joint (" + age + ")";
            return "XLPE-XLPE intermediate joint (" + age + ")";
        }

        private static string EnglishRecommendation()
        {
            return "Recommendation: confirm priority locations against the 0 kV/background record, on-site calibration, voltage-level repeatability, and original time-domain waveforms. IEEE Std 400.2 supports VLF field-test evaluation, and IEEE Std 400.4 supports DAC evaluation using PD magnitude, phase-related behavior, voltage dependence, PDIV/PDEV, and PD site mapping. These guides do not establish a universal apparent-charge acceptance limit for every cable system. This automated HV-Xpert result supports field-test review and asset decisions and is not a stand-alone pass/fail decision.";
        }
        private static double NoiseSeparationScore(List<double> charges, double noise)
        {
            if (charges.Count == 0) return 0.0;
            if (noise <= 1e-9) return 1.0;
            double median = Median(charges);
            double ratioScore = Clamp(Math.Log(Math.Max(median / noise, 1.0)) / Math.Log(2.0), 0.0, 1.0);
            double separatedFraction = charges.Count(delegate(double value) { return value >= noise * 1.20; }) / (double)charges.Count;
            return Clamp(0.60 * ratioScore + 0.40 * separatedFraction, 0.0, 1.0);
        }

        private static double VerticalColumnScore(double positionConcentration, List<double> charges)
        {
            if (charges.Count < 3) return 0.0;
            List<double> sorted = charges.OrderBy(delegate(double value) { return value; }).ToList();
            double q10 = Math.Max(Quantile(sorted, 0.10), 1e-9);
            double q90 = Math.Max(Quantile(sorted, 0.90), q10);
            double verticalSpan = Clamp(Math.Log(q90 / q10) / Math.Log(1.80), 0.0, 1.0);
            return Clamp(positionConcentration * (0.25 + 0.75 * verticalSpan), 0.0, 1.0);
        }

        private static double FloatingDischargePatternScore(List<PulsePoint> points)
        {
            List<IGrouping<string, PulsePoint>> applicationGroups = points.GroupBy(delegate(PulsePoint p)
            {
                return Math.Round(p.Voltage, 1).ToString("F1", CultureInfo.InvariantCulture) + "|" +
                    Math.Round(p.Application, 3).ToString("F3", CultureInfo.InvariantCulture);
            }).Where(delegate(IGrouping<string, PulsePoint> group) { return group.Count() >= 3; }).ToList();
            if (applicationGroups.Count == 0) return 0.0;
            List<double> groupScores = new List<double>();
            foreach (IGrouping<string, PulsePoint> group in applicationGroups)
            {
                List<double> angles = group.Select(delegate(PulsePoint p)
                {
                    double angle = p.Angle % 360.0;
                    return angle < 0.0 ? angle + 360.0 : angle;
                }).OrderBy(delegate(double angle) { return angle; }).ToList();
                double expectedGap = 360.0 / angles.Count;
                double normalizedGapError = 0.0;
                int i;
                for (i = 0; i < angles.Count; i++)
                {
                    double next = i == angles.Count - 1 ? angles[0] + 360.0 : angles[i + 1];
                    normalizedGapError += Math.Abs((next - angles[i]) - expectedGap) / Math.Max(expectedGap, 1e-9);
                }
                normalizedGapError /= angles.Count;
                double phaseRegularity = Clamp(Math.Exp(-2.5 * normalizedGapError), 0.0, 1.0);
                List<double> amplitudes = group.Select(delegate(PulsePoint p) { return p.Charge; }).ToList();
                double median = Math.Max(Median(amplitudes), 1e-9);
                double relativeMad = 1.4826 * Median(amplitudes.Select(delegate(double value) { return Math.Abs(value - median); }).ToList()) / median;
                double amplitudeSimilarity = Clamp(Math.Exp(-4.0 * relativeMad), 0.0, 1.0);
                groupScores.Add(phaseRegularity * amplitudeSimilarity);
            }
            double support = Clamp(applicationGroups.Count / 2.0, 0.0, 1.0);
            return Clamp(groupScores.Average() * (0.75 + 0.25 * support), 0.0, 1.0);
        }
        private static bool HasSparseEntryEvidence(List<PulsePoint> points)
        {
            if (points.Count < 2) return false;
            double coverage;
            double score = InputProbabilityEvidence(points, out coverage);
            bool probabilityEvidence = coverage >= 0.67 && score >= 0.58;
            bool repeatedIndependentIds = UniqueApplications(points) >= 2;
            return probabilityEvidence || repeatedIndependentIds;
        }

        private static double InputProbabilityEvidence(List<PulsePoint> points, out double coverage)
        {
            List<double> valid = points.Where(delegate(PulsePoint p) { return IsFinite(p.InputDischargeProbability); })
                .Select(delegate(PulsePoint p) { return Clamp(p.InputDischargeProbability, 0.0, 1.0); }).ToList();
            coverage = points.Count == 0 ? 0.0 : (double)valid.Count / points.Count;
            if (valid.Count == 0) return 0.50;
            double median = Median(valid);
            double mad = 1.4826 * Median(valid.Select(delegate(double value) { return Math.Abs(value - median); }).ToList());
            double consistency = Clamp(Math.Exp(-mad / 0.20), 0.0, 1.0);
            return Clamp(0.75 * median + 0.15 * consistency + 0.10 * coverage, 0.0, 1.0);
        }

        private static double SequenceEvidenceScore(List<PulsePoint> points, List<PulsePoint> population, double repeatability)
        {
            List<double> applications = points.Select(delegate(PulsePoint p) { return Math.Round(p.Application, 3); })
                .Distinct().OrderBy(delegate(double value) { return value; }).ToList();
            List<double> populationApplications = population.Select(delegate(PulsePoint p) { return Math.Round(p.Application, 3); })
                .Distinct().OrderBy(delegate(double value) { return value; }).ToList();
            double span = 0.20;
            if (applications.Count >= 2 && populationApplications.Count >= 2)
            {
                double denominator = Math.Max(populationApplications[populationApplications.Count - 1] - populationApplications[0], 1.0);
                span = Clamp((applications[applications.Count - 1] - applications[0]) / denominator, 0.0, 1.0);
            }
            return Clamp(0.70 * repeatability + 0.30 * span, 0.0, 1.0);
        }

        private static int UniqueApplications(List<PulsePoint> points)
        {
            return points.Select(delegate(PulsePoint p) { return Math.Round(p.Application, 3); }).Distinct().Count();
        }

        private static int UniqueVoltages(List<PulsePoint> points)
        {
            return points.Select(delegate(PulsePoint p) { return Math.Round(p.Voltage, 2); }).Distinct().Count();
        }

        private static double Consistency(List<double> values)
        {
            if (values.Count < 2) return 0.5;
            double med = Median(values.Select(delegate(double x) { return Math.Abs(x); }).ToList());
            double mad = 1.4826 * Median(values.Select(delegate(double x) { return Math.Abs(Math.Abs(x) - med); }).ToList());
            return Clamp(Math.Exp(-mad / Math.Max(med, 1e-9)), 0.0, 1.0);
        }

        private static double TrapezoidScore(List<double> q, double noise)
        {
            if (q.Count < 5) return 0.0;
            double floor = Math.Max(noise, Math.Max(q.Min() * 0.8, 1e-9));
            double max = q.Max();
            if (max <= floor * 1.05) return 0.0;
            double logFloor = Math.Log(floor);
            double logSpan = Math.Log(max) - logFloor;
            int[] bins = new int[4];
            foreach (double value in q)
            {
                int bin = (int)Math.Floor(4.0 * (Math.Log(Math.Max(value, floor)) - logFloor) / Math.Max(logSpan, 1e-9));
                if (bin < 0) bin = 0; if (bin > 3) bin = 3; bins[bin]++;
            }
            double monotonic = 0.0;
            int i;
            for (i = 0; i < 3; i++) if (bins[i] >= bins[i + 1]) monotonic += 1.0 / 3.0;
            double baseShare = (bins[0] + bins[1]) / (double)q.Count;
            double tail = (bins[2] + bins[3]) > 0 ? 1.0 : 0.0;
            return Clamp(0.55 * monotonic + 0.30 * baseShare + 0.15 * tail, 0.0, 1.0);
        }

        private static void CircularResultants(List<PulsePoint> points, out double r1, out double r2)
        {
            double c1 = 0.0, s1 = 0.0, c2 = 0.0, s2 = 0.0;
            foreach (PulsePoint p in points)
            {
                double radians = p.Angle * Math.PI / 180.0;
                c1 += Math.Cos(radians); s1 += Math.Sin(radians);
                c2 += Math.Cos(2.0 * radians); s2 += Math.Sin(2.0 * radians);
            }
            r1 = Math.Sqrt(c1 * c1 + s1 * s1) / Math.Max(points.Count, 1);
            r2 = Math.Sqrt(c2 * c2 + s2 * s2) / Math.Max(points.Count, 1);
        }

        private static double WeightedMedian(List<PulsePoint> points)
        {
            List<PulsePoint> sorted = points.OrderBy(delegate(PulsePoint p) { return p.Position; }).ToList();
            double medianCharge = Median(points.Select(delegate(PulsePoint p) { return p.Charge; }).ToList());
            double total = 0.0;
            List<double> weights = new List<double>();
            foreach (PulsePoint p in sorted)
            {
                double w = Math.Min(2.0, 1.0 + 0.20 * Math.Log(1.0 + p.Charge / Math.Max(medianCharge, 1e-9)));
                weights.Add(w); total += w;
            }
            double cumulative = 0.0;
            int i;
            for (i = 0; i < sorted.Count; i++)
            {
                cumulative += weights[i];
                if (cumulative >= total * 0.5) return sorted[i].Position;
            }
            return sorted[sorted.Count - 1].Position;
        }

        private static string CandidateId(Candidate candidate)
        {
            string raw = candidate.Phase.ToString(CultureInfo.InvariantCulture) + "|" +
                candidate.Center.ToString("F3", CultureInfo.InvariantCulture) + "|" + candidate.Points.Count.ToString(CultureInfo.InvariantCulture);
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(raw)) { hash ^= b; hash *= 16777619; }
            return PhaseName(candidate.Phase) + "-" + hash.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            List<double> sorted = values.OrderBy(delegate(double x) { return x; }).ToList();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : 0.5 * (sorted[middle - 1] + sorted[middle]);
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

        private static double Clamp(double value, double low, double high) { return Math.Max(low, Math.Min(high, value)); }
        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static void ValidateScalar(double value, string name, double low, double high, bool inclusive)
        {
            if (!IsFinite(value) || value < low || value > high) throw new ArgumentOutOfRangeException(name);
        }
        private static string PhaseName(int phase) { return phase == 0 ? "A" : phase == 1 ? "B" : "C"; }
        private static string PhaseText(int mask)
        {
            List<string> names = new List<string>();
            if ((mask & 1) != 0) names.Add("A"); if ((mask & 2) != 0) names.Add("B"); if ((mask & 4) != 0) names.Add("C");
            if (names.Count == 1) return names[0] + "相";
            if (names.Count == 2) return string.Join("、", names.ToArray()) + "两相";
            return string.Join("、", names.ToArray()) + "三相";
        }
        private static string PhaseTextEnglish(int mask)
        {
            List<string> names = new List<string>();
            if ((mask & 1) != 0) names.Add("A"); if ((mask & 2) != 0) names.Add("B"); if ((mask & 4) != 0) names.Add("C");
            if (names.Count == 0) return "No phase";
            return string.Join("/", names.ToArray()) + (names.Count == 1 ? " phase" : " phases");
        }
        private static string LocationPartEnglish(LocationGroup group)
        {
            if (group.LocationPart == "测试端附近") return "at/near the test-end termination";
            if (group.LocationPart == "末端终端附近") return "at/near the remote-end termination";
            if (group.LocationPart != "电缆本体" && group.LocationPart.EndsWith("m接头附近", StringComparison.Ordinal))
            {
                string jointPosition = group.LocationPart.Substring(0, group.LocationPart.Length - "m接头附近".Length).Trim();
                return "at/near the " + jointPosition + " m joint";
            }
            return "within the cable section";
        }
        private static int BitCount(int value) { int count = 0; while (value != 0) { count += value & 1; value >>= 1; } return count; }
        private static string FormatNumber(double value, int decimals) { return value.ToString("F" + decimals, CultureInfo.InvariantCulture); }
    }

    internal static class FeedbackCsv
    {
        private static readonly string[] FeatureNames = new string[] {
            "log_pulse_count", "position_mad_over_error", "position_p90_over_error", "density_score",
            "repeatability_score", "application_group_fraction", "feature_coherence", "width_consistency",
            "bandwidth_consistency", "trapezoid_score", "log_median_charge_ratio", "log_max_to_median_charge",
            "voltage_coverage", "phase_resultant_1", "phase_resultant_2", "charge_above_noise_fraction"
        };

        internal static void Append(string path, AnalysisResult result, int row, int label, string session, string cable, string reviewer)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("反馈CSV路径为空");
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            bool writeHeader = !File.Exists(fullPath) || new FileInfo(fullPath).Length == 0;
            using (StreamWriter writer = new StreamWriter(fullPath, true, new UTF8Encoding(false)))
            {
                if (writeHeader)
                {
                    writer.Write("candidate_id,session_id,cable_id,reviewer,label");
                    foreach (string name in FeatureNames) writer.Write("," + name);
                    writer.WriteLine();
                }
                writer.Write(Csv(result.CandidateIds[row])); writer.Write("," + Csv(session)); writer.Write("," + Csv(cable)); writer.Write("," + Csv(reviewer)); writer.Write("," + label.ToString(CultureInfo.InvariantCulture));
                int i;
                for (i = 0; i < 16; i++) writer.Write("," + result.LearningFeatureMatrix[row * 16 + i].ToString("R", CultureInfo.InvariantCulture));
                writer.WriteLine();
            }
        }

        private static string Csv(string text)
        {
            if (text == null) text = string.Empty;
            return "\"" + text.Replace("\"", "\"\"").Replace("\r", string.Empty).Replace("\n", string.Empty) + "\"";
        }
    }
}


