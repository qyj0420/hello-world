using System;
using System.Collections.Generic;
using System.Reflection;


namespace HVX.PD.TimeFrequencyClustering
{
    /// <summary>
    /// Task 27. Reads equivalent time from zero-based column 23 (ns) and
    /// equivalent frequency from zero-based column 24 (Hz) of a LabVIEW 2D matrix.
    /// </summary>
    public sealed partial class TimeFrequencyClusterEngine
    {
        private const int TimeColumn = 23;
        private const int FrequencyColumn = 24;
        private const int InvalidLabel = -2;
        private const int NoiseLabel = -1;
        private const int SummaryColumnCount = 12;
        private const double LogTwoPi = 1.8378770664093453;
        private const double NoiseMahalanobisSquared = 13.815510557964274;

        /// <summary>
        /// Automatic production entry point. clusterLabels: 0..K-1 cluster,
        /// -1 noise/outlier, -2 invalid source row.
        /// clusterSummary columns: id,count,share,centerHz,centerNs,p10Hz,p90Hz,
        /// p10Ns,p90Ns,meanMembership,stdHz,stdNs.
        /// </summary>
        public void GetTimeFrequencyClusters(
            double[,] parameterMatrix,
            out int[] clusterLabels,
            out double[] membershipStrength,
            out double[,] clusterSummary)
        {
            GetTimeFrequencyClustersAdvanced(
                parameterMatrix,
                8,
                0,
                out clusterLabels,
                out membershipStrength,
                out clusterSummary);
        }

        /// <summary>
        /// Advanced entry point. maximumClusterCount is clamped to 1..12.
        /// minimumClusterSize=0 selects an automatic value from sample count.
        /// </summary>
        public void GetTimeFrequencyClustersAdvanced(
            double[,] parameterMatrix,
            int maximumClusterCount,
            int minimumClusterSize,
            out int[] clusterLabels,
            out double[] membershipStrength,
            out double[,] clusterSummary)
        {
            if (parameterMatrix == null) throw new ArgumentNullException("parameterMatrix");
            if (parameterMatrix.GetLength(1) <= FrequencyColumn)
                throw new ArgumentException("Expected at least 25 columns; time is column 23 and frequency is column 24.", "parameterMatrix");
            ClusterCore(parameterMatrix, TimeColumn, FrequencyColumn,
                maximumClusterCount, minimumClusterSize,
                out clusterLabels, out membershipStrength, out clusterSummary);
        }

        private static void ClusterCore(
            double[,] parameterMatrix,
            int timeColumn,
            int frequencyColumn,
            int maximumClusterCount,
            int minimumClusterSize,
            out int[] clusterLabels,
            out double[] membershipStrength,
            out double[,] clusterSummary)
        {
            if (parameterMatrix == null) throw new ArgumentNullException("parameterMatrix");
            int rowCount = parameterMatrix.GetLength(0);
            int columnCount = parameterMatrix.GetLength(1);
            if (columnCount <= Math.Max(timeColumn, frequencyColumn))
                throw new ArgumentException("参数二维矩阵至少需要25列；算法按零基索引读取第23列时宽/ns和第24列频宽/Hz。", "parameterMatrix");

            clusterLabels = new int[rowCount];
            membershipStrength = new double[rowCount];
            for (int row = 0; row < rowCount; row++) clusterLabels[row] = InvalidLabel;
            clusterSummary = new double[0, SummaryColumnCount];
            if (rowCount == 0) return;

            List<int> sourceRows = new List<int>();
            List<double> rawFrequency = new List<double>();
            List<double> rawTime = new List<double>();
            for (int row = 0; row < rowCount; row++)
            {
                double timeNs = parameterMatrix[row, timeColumn];
                double frequencyHz = parameterMatrix[row, frequencyColumn];
                if (!IsFinite(timeNs) || !IsFinite(frequencyHz) || timeNs <= 0.0 || frequencyHz <= 0.0) continue;
                sourceRows.Add(row);
                rawFrequency.Add(frequencyHz);
                rawTime.Add(timeNs);
            }

            int validCount = sourceRows.Count;
            if (validCount == 0) return;
            if (validCount == 1)
            {
                int onlyRow = sourceRows[0];
                clusterLabels[onlyRow] = 0;
                membershipStrength[onlyRow] = 1.0;
                clusterSummary = BuildSinglePointSummary(rawFrequency[0], rawTime[0]);
                return;
            }

            maximumClusterCount = Clamp(maximumClusterCount, 1, 12);
            if (minimumClusterSize <= 0)
                minimumClusterSize = Clamp((int)Math.Round(Math.Sqrt(validCount)), 6, 50);
            minimumClusterSize = Clamp(minimumClusterSize, 2, Math.Max(2, validCount));
            int feasibleMaximum = Math.Max(1, validCount / minimumClusterSize);
            maximumClusterCount = Math.Min(maximumClusterCount, feasibleMaximum);

            double[] logFrequency = new double[validCount];
            double[] logTime = new double[validCount];
            for (int i = 0; i < validCount; i++)
            {
                logFrequency[i] = Math.Log10(rawFrequency[i]);
                logTime[i] = Math.Log10(rawTime[i]);
            }

            RobustAxis frequencyAxis = RobustAxis.Create(logFrequency);
            RobustAxis timeAxis = RobustAxis.Create(logTime);
            double[,] points = new double[validCount, 2];
            for (int i = 0; i < validCount; i++)
            {
                points[i, 0] = (logFrequency[i] - frequencyAxis.Center) / frequencyAxis.Scale;
                points[i, 1] = (logTime[i] - timeAxis.Center) / timeAxis.Scale;
            }

            GaussianMixtureModel best = null;
            for (int clusterCount = 1; clusterCount <= maximumClusterCount; clusterCount++)
            {
                GaussianMixtureModel candidate = FitGaussianMixture(points, clusterCount, minimumClusterSize);
                if (candidate == null) continue;
                if (best == null || candidate.Bic < best.Bic) best = candidate;
            }
            if (best == null) best = FitGaussianMixture(points, 1, 1);
            if (best == null) throw new InvalidOperationException("时频聚类模型未能收敛，请检查第23/24列的数据有效性。");

            ModelEvaluation evaluation = Evaluate(points, best.Components);
            int componentCount = best.Components.Length;
            int[] nonNoiseCounts = new int[componentCount];
            bool[] isNoise = new bool[validCount];
            for (int i = 0; i < validCount; i++)
            {
                isNoise[i] = evaluation.MahalanobisSquared[i] > NoiseMahalanobisSquared;
                if (!isNoise[i]) nonNoiseCounts[evaluation.Assignments[i]]++;
            }

            List<int> activeComponents = new List<int>();
            for (int component = 0; component < componentCount; component++)
                if (nonNoiseCounts[component] > 0) activeComponents.Add(component);
            activeComponents.Sort(delegate(int left, int right)
            {
                double lf = frequencyAxis.Unscale(best.Components[left].Mean0);
                double rf = frequencyAxis.Unscale(best.Components[right].Mean0);
                int compare = lf.CompareTo(rf);
                if (compare != 0) return compare;
                return timeAxis.Unscale(best.Components[left].Mean1).CompareTo(timeAxis.Unscale(best.Components[right].Mean1));
            });

            int[] componentToLabel = new int[componentCount];
            for (int component = 0; component < componentCount; component++) componentToLabel[component] = NoiseLabel;
            for (int label = 0; label < activeComponents.Count; label++) componentToLabel[activeComponents[label]] = label;

            for (int i = 0; i < validCount; i++)
            {
                int sourceRow = sourceRows[i];
                membershipStrength[sourceRow] = evaluation.Membership[i];
                clusterLabels[sourceRow] = isNoise[i] ? NoiseLabel : componentToLabel[evaluation.Assignments[i]];
            }

            clusterSummary = BuildSummary(
                clusterLabels,
                membershipStrength,
                sourceRows,
                rawFrequency,
                rawTime,
                activeComponents.Count,
                validCount);
        }

        private static double[,] BuildSinglePointSummary(double frequencyHz, double timeNs)
        {
            double[,] summary = new double[1, SummaryColumnCount];
            summary[0, 0] = 0.0;
            summary[0, 1] = 1.0;
            summary[0, 2] = 1.0;
            summary[0, 3] = frequencyHz;
            summary[0, 4] = timeNs;
            summary[0, 5] = frequencyHz;
            summary[0, 6] = frequencyHz;
            summary[0, 7] = timeNs;
            summary[0, 8] = timeNs;
            summary[0, 9] = 1.0;
            summary[0, 10] = 0.0;
            summary[0, 11] = 0.0;
            return summary;
        }

        private static double[,] BuildSummary(
            int[] labels,
            double[] membership,
            List<int> sourceRows,
            List<double> frequencies,
            List<double> times,
            int clusterCount,
            int validCount)
        {
            double[,] summary = new double[clusterCount, SummaryColumnCount];
            for (int label = 0; label < clusterCount; label++)
            {
                List<double> clusterFrequency = new List<double>();
                List<double> clusterTime = new List<double>();
                double membershipSum = 0.0;
                for (int i = 0; i < sourceRows.Count; i++)
                {
                    int sourceRow = sourceRows[i];
                    if (labels[sourceRow] != label) continue;
                    clusterFrequency.Add(frequencies[i]);
                    clusterTime.Add(times[i]);
                    membershipSum += membership[sourceRow];
                }

                double[] f = clusterFrequency.ToArray();
                double[] t = clusterTime.ToArray();
                Array.Sort(f);
                Array.Sort(t);
                int count = f.Length;
                summary[label, 0] = label;
                summary[label, 1] = count;
                summary[label, 2] = validCount > 0 ? count / (double)validCount : 0.0;
                summary[label, 3] = QuantileSorted(f, 0.5);
                summary[label, 4] = QuantileSorted(t, 0.5);
                summary[label, 5] = QuantileSorted(f, 0.1);
                summary[label, 6] = QuantileSorted(f, 0.9);
                summary[label, 7] = QuantileSorted(t, 0.1);
                summary[label, 8] = QuantileSorted(t, 0.9);
                summary[label, 9] = count > 0 ? membershipSum / count : 0.0;
                summary[label, 10] = StandardDeviation(f);
                summary[label, 11] = StandardDeviation(t);
            }
            return summary;
        }

        private static GaussianMixtureModel FitGaussianMixture(double[,] points, int clusterCount, int minimumClusterSize)
        {
            int count = points.GetLength(0);
            if (count < clusterCount || clusterCount < 1) return null;

            GaussianComponent[] components = InitializeComponents(points, clusterCount);
            if (components == null) return null;
            double previousLogLikelihood = double.NegativeInfinity;

            for (int iteration = 0; iteration < 160; iteration++)
            {
                ModelEvaluation evaluation = Evaluate(points, components);
                int trimCount = count >= 100 ? Math.Max(1, (int)Math.Floor(count * 0.02)) : 0;
                bool[] retained = RetainedByLogDensity(evaluation.LogDensity, trimCount);
                double logLikelihood = 0.0;
                for (int i = 0; i < count; i++) if (retained[i]) logLikelihood += evaluation.LogDensity[i];

                GaussianComponent[] updated = Maximize(points, evaluation.Responsibilities, retained, components);
                if (updated == null) return null;
                components = updated;
                if (iteration > 2 && Math.Abs(logLikelihood - previousLogLikelihood) <= 1e-7 * (1.0 + Math.Abs(previousLogLikelihood))) break;
                previousLogLikelihood = logLikelihood;
            }

            ModelEvaluation finalEvaluation = Evaluate(points, components);
            int[] hardCounts = new int[clusterCount];
            for (int i = 0; i < count; i++) hardCounts[finalEvaluation.Assignments[i]]++;
            if (clusterCount > 1)
            {
                for (int component = 0; component < clusterCount; component++)
                    if (hardCounts[component] < minimumClusterSize) return null;
            }

            int finalTrimCount = count >= 100 ? Math.Max(1, (int)Math.Floor(count * 0.02)) : 0;
            bool[] finalRetained = RetainedByLogDensity(finalEvaluation.LogDensity, finalTrimCount);
            double finalLogLikelihood = 0.0;
            int retainedCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (!finalRetained[i]) continue;
                finalLogLikelihood += finalEvaluation.LogDensity[i];
                retainedCount++;
            }
            int parameterCount = 6 * clusterCount - 1;
            double bic = -2.0 * finalLogLikelihood + parameterCount * Math.Log(Math.Max(2, retainedCount));
            return new GaussianMixtureModel(components, bic);
        }

        private static GaussianComponent[] InitializeComponents(double[,] points, int clusterCount)
        {
            int count = points.GetLength(0);
            int coreCount = Math.Max(clusterCount, (int)Math.Ceiling(count * 0.95));
            int[] byRadius = new int[count];
            for (int i = 0; i < count; i++) byRadius[i] = i;
            Array.Sort(byRadius, delegate(int left, int right)
            {
                double lr = points[left, 0] * points[left, 0] + points[left, 1] * points[left, 1];
                double rr = points[right, 0] * points[right, 0] + points[right, 1] * points[right, 1];
                int compare = lr.CompareTo(rr);
                return compare != 0 ? compare : left.CompareTo(right);
            });

            double[,] centers = new double[clusterCount, 2];
            int first = byRadius[0];
            centers[0, 0] = points[first, 0];
            centers[0, 1] = points[first, 1];
            for (int center = 1; center < clusterCount; center++)
            {
                int selected = byRadius[0];
                double selectedDistance = -1.0;
                for (int core = 0; core < coreCount; core++)
                {
                    int point = byRadius[core];
                    double nearest = double.PositiveInfinity;
                    for (int existing = 0; existing < center; existing++)
                    {
                        double d0 = points[point, 0] - centers[existing, 0];
                        double d1 = points[point, 1] - centers[existing, 1];
                        nearest = Math.Min(nearest, d0 * d0 + d1 * d1);
                    }
                    if (nearest > selectedDistance)
                    {
                        selectedDistance = nearest;
                        selected = point;
                    }
                }
                centers[center, 0] = points[selected, 0];
                centers[center, 1] = points[selected, 1];
            }

            int[] assignments = new int[count];
            for (int iteration = 0; iteration < 30; iteration++)
            {
                bool changed = false;
                for (int i = 0; i < count; i++)
                {
                    int best = 0;
                    double bestDistance = double.PositiveInfinity;
                    for (int center = 0; center < clusterCount; center++)
                    {
                        double d0 = points[i, 0] - centers[center, 0];
                        double d1 = points[i, 1] - centers[center, 1];
                        double distance = d0 * d0 + d1 * d1;
                        if (distance < bestDistance) { bestDistance = distance; best = center; }
                    }
                    if (iteration == 0 || assignments[i] != best) { assignments[i] = best; changed = true; }
                }

                double[] sum0 = new double[clusterCount];
                double[] sum1 = new double[clusterCount];
                int[] sizes = new int[clusterCount];
                for (int core = 0; core < coreCount; core++)
                {
                    int point = byRadius[core];
                    int cluster = assignments[point];
                    sum0[cluster] += points[point, 0];
                    sum1[cluster] += points[point, 1];
                    sizes[cluster]++;
                }
                for (int cluster = 0; cluster < clusterCount; cluster++)
                {
                    if (sizes[cluster] == 0) return null;
                    centers[cluster, 0] = sum0[cluster] / sizes[cluster];
                    centers[cluster, 1] = sum1[cluster] / sizes[cluster];
                }
                if (!changed) break;
            }

            double global00, global01, global11;
            Covariance(points, null, out global00, out global01, out global11);
            GaussianComponent[] components = new GaussianComponent[clusterCount];
            for (int cluster = 0; cluster < clusterCount; cluster++)
            {
                List<int> members = new List<int>();
                for (int core = 0; core < coreCount; core++)
                {
                    int point = byRadius[core];
                    if (assignments[point] == cluster) members.Add(point);
                }
                double c00, c01, c11;
                if (members.Count >= 4) Covariance(points, members.ToArray(), out c00, out c01, out c11);
                else { c00 = global00; c01 = global01; c11 = global11; }
                components[cluster] = GaussianComponent.Create(
                    members.Count / (double)coreCount,
                    centers[cluster, 0],
                    centers[cluster, 1],
                    c00, c01, c11);
            }
            NormalizeWeights(components);
            return components;
        }

        private static GaussianComponent[] Maximize(
            double[,] points,
            double[,] responsibilities,
            bool[] retained,
            GaussianComponent[] previous)
        {
            int count = points.GetLength(0);
            int clusterCount = previous.Length;
            GaussianComponent[] updated = new GaussianComponent[clusterCount];
            double[] mass = new double[clusterCount];
            double[] mean0 = new double[clusterCount];
            double[] mean1 = new double[clusterCount];
            int retainedCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (!retained[i]) continue;
                retainedCount++;
                for (int cluster = 0; cluster < clusterCount; cluster++)
                {
                    double r = responsibilities[i, cluster];
                    mass[cluster] += r;
                    mean0[cluster] += r * points[i, 0];
                    mean1[cluster] += r * points[i, 1];
                }
            }
            for (int cluster = 0; cluster < clusterCount; cluster++)
            {
                if (mass[cluster] < 1e-6) return null;
                mean0[cluster] /= mass[cluster];
                mean1[cluster] /= mass[cluster];
            }

            for (int cluster = 0; cluster < clusterCount; cluster++)
            {
                double c00 = 0.0, c01 = 0.0, c11 = 0.0;
                for (int i = 0; i < count; i++)
                {
                    if (!retained[i]) continue;
                    double r = responsibilities[i, cluster];
                    double d0 = points[i, 0] - mean0[cluster];
                    double d1 = points[i, 1] - mean1[cluster];
                    c00 += r * d0 * d0;
                    c01 += r * d0 * d1;
                    c11 += r * d1 * d1;
                }
                c00 /= mass[cluster];
                c01 /= mass[cluster];
                c11 /= mass[cluster];
                updated[cluster] = GaussianComponent.Create(
                    mass[cluster] / Math.Max(1, retainedCount),
                    mean0[cluster], mean1[cluster], c00, c01, c11);
            }
            NormalizeWeights(updated);
            return updated;
        }

        private static ModelEvaluation Evaluate(double[,] points, GaussianComponent[] components)
        {
            int count = points.GetLength(0);
            int clusterCount = components.Length;
            ModelEvaluation evaluation = new ModelEvaluation(count, clusterCount);
            double[] logTerms = new double[clusterCount];
            for (int i = 0; i < count; i++)
            {
                double maximum = double.NegativeInfinity;
                for (int cluster = 0; cluster < clusterCount; cluster++)
                {
                    GaussianComponent component = components[cluster];
                    double d0 = points[i, 0] - component.Mean0;
                    double d1 = points[i, 1] - component.Mean1;
                    double mahalanobis = d0 * (component.Inv00 * d0 + component.Inv01 * d1) +
                                         d1 * (component.Inv01 * d0 + component.Inv11 * d1);
                    double term = Math.Log(Math.Max(component.Weight, 1e-300)) - LogTwoPi -
                                  0.5 * Math.Log(component.Determinant) - 0.5 * mahalanobis;
                    logTerms[cluster] = term;
                    if (term > maximum) maximum = term;
                }
                double sum = 0.0;
                for (int cluster = 0; cluster < clusterCount; cluster++) sum += Math.Exp(logTerms[cluster] - maximum);
                double logDensity = maximum + Math.Log(Math.Max(sum, 1e-300));
                evaluation.LogDensity[i] = logDensity;
                int assignment = 0;
                double bestResponsibility = -1.0;
                for (int cluster = 0; cluster < clusterCount; cluster++)
                {
                    double responsibility = Math.Exp(logTerms[cluster] - logDensity);
                    evaluation.Responsibilities[i, cluster] = responsibility;
                    if (responsibility > bestResponsibility)
                    {
                        bestResponsibility = responsibility;
                        assignment = cluster;
                    }
                }
                evaluation.Assignments[i] = assignment;
                evaluation.Membership[i] = bestResponsibility;
                GaussianComponent assigned = components[assignment];
                double a0 = points[i, 0] - assigned.Mean0;
                double a1 = points[i, 1] - assigned.Mean1;
                evaluation.MahalanobisSquared[i] = a0 * (assigned.Inv00 * a0 + assigned.Inv01 * a1) +
                                                    a1 * (assigned.Inv01 * a0 + assigned.Inv11 * a1);
            }
            return evaluation;
        }

        private static bool[] RetainedByLogDensity(double[] logDensity, int trimCount)
        {
            int count = logDensity.Length;
            bool[] retained = new bool[count];
            for (int i = 0; i < count; i++) retained[i] = true;
            if (trimCount <= 0) return retained;
            int[] order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, delegate(int left, int right)
            {
                int compare = logDensity[left].CompareTo(logDensity[right]);
                return compare != 0 ? compare : left.CompareTo(right);
            });
            for (int i = 0; i < Math.Min(trimCount, count - 1); i++) retained[order[i]] = false;
            return retained;
        }

        private static void NormalizeWeights(GaussianComponent[] components)
        {
            double sum = 0.0;
            for (int i = 0; i < components.Length; i++) sum += components[i].Weight;
            if (!(sum > 0.0)) sum = components.Length;
            for (int i = 0; i < components.Length; i++) components[i].Weight /= sum;
        }

        private static void Covariance(double[,] points, int[] indices, out double c00, out double c01, out double c11)
        {
            int count = indices == null ? points.GetLength(0) : indices.Length;
            double mean0 = 0.0, mean1 = 0.0;
            for (int i = 0; i < count; i++)
            {
                int point = indices == null ? i : indices[i];
                mean0 += points[point, 0];
                mean1 += points[point, 1];
            }
            mean0 /= Math.Max(1, count);
            mean1 /= Math.Max(1, count);
            c00 = c01 = c11 = 0.0;
            for (int i = 0; i < count; i++)
            {
                int point = indices == null ? i : indices[i];
                double d0 = points[point, 0] - mean0;
                double d1 = points[point, 1] - mean1;
                c00 += d0 * d0;
                c01 += d0 * d1;
                c11 += d1 * d1;
            }
            double denominator = Math.Max(1, count - 1);
            c00 /= denominator;
            c01 /= denominator;
            c11 /= denominator;
        }

        private static double QuantileSorted(double[] sorted, double probability)
        {
            if (sorted == null || sorted.Length == 0) return double.NaN;
            if (sorted.Length == 1) return sorted[0];
            double position = probability * (sorted.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(sorted.Length - 1, lower + 1);
            double fraction = position - lower;
            return sorted[lower] * (1.0 - fraction) + sorted[upper] * fraction;
        }

        private static double StandardDeviation(double[] values)
        {
            if (values == null || values.Length <= 1) return 0.0;
            double mean = 0.0;
            for (int i = 0; i < values.Length; i++) mean += values[i];
            mean /= values.Length;
            double sum = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                double difference = values[i] - mean;
                sum += difference * difference;
            }
            return Math.Sqrt(sum / (values.Length - 1));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }

        private sealed class RobustAxis
        {
            public readonly double Center;
            public readonly double Scale;

            private RobustAxis(double center, double scale)
            {
                Center = center;
                Scale = scale;
            }

            public static RobustAxis Create(double[] values)
            {
                double[] sorted = (double[])values.Clone();
                Array.Sort(sorted);
                double center = QuantileSorted(sorted, 0.5);
                double[] deviations = new double[values.Length];
                for (int i = 0; i < values.Length; i++) deviations[i] = Math.Abs(values[i] - center);
                Array.Sort(deviations);
                double madScale = 1.4826 * QuantileSorted(deviations, 0.5);
                double iqrScale = (QuantileSorted(sorted, 0.75) - QuantileSorted(sorted, 0.25)) / 1.349;
                double scale = Math.Max(madScale, iqrScale);
                if (!(scale > 1e-12)) scale = StandardDeviation(values);
                if (!(scale > 1e-12)) scale = 1.0;
                return new RobustAxis(center, scale);
            }

            public double Unscale(double standardized)
            {
                return Center + Scale * standardized;
            }
        }

        private sealed class GaussianComponent
        {
            public double Weight;
            public double Mean0;
            public double Mean1;
            public double Inv00;
            public double Inv01;
            public double Inv11;
            public double Determinant;

            public static GaussianComponent Create(
                double weight,
                double mean0,
                double mean1,
                double c00,
                double c01,
                double c11)
            {
                const double regularization = 0.015;
                c00 = Math.Max(c00, 1e-6) + regularization;
                c11 = Math.Max(c11, 1e-6) + regularization;
                double maximumCross = 0.98 * Math.Sqrt(c00 * c11);
                c01 = Math.Max(-maximumCross, Math.Min(maximumCross, c01));
                double determinant = c00 * c11 - c01 * c01;
                if (!(determinant > 1e-10))
                {
                    c00 += regularization;
                    c11 += regularization;
                    determinant = c00 * c11 - c01 * c01;
                }
                GaussianComponent component = new GaussianComponent();
                component.Weight = Math.Max(weight, 1e-12);
                component.Mean0 = mean0;
                component.Mean1 = mean1;
                component.Determinant = Math.Max(determinant, 1e-12);
                component.Inv00 = c11 / component.Determinant;
                component.Inv01 = -c01 / component.Determinant;
                component.Inv11 = c00 / component.Determinant;
                return component;
            }
        }

        private sealed class GaussianMixtureModel
        {
            public readonly GaussianComponent[] Components;
            public readonly double Bic;

            public GaussianMixtureModel(GaussianComponent[] components, double bic)
            {
                Components = components;
                Bic = bic;
            }
        }

        private sealed class ModelEvaluation
        {
            public readonly double[,] Responsibilities;
            public readonly double[] LogDensity;
            public readonly int[] Assignments;
            public readonly double[] Membership;
            public readonly double[] MahalanobisSquared;

            public ModelEvaluation(int count, int clusterCount)
            {
                Responsibilities = new double[count, clusterCount];
                LogDensity = new double[count];
                Assignments = new int[count];
                Membership = new double[count];
                MahalanobisSquared = new double[count];
            }
        }
    }
}

namespace HVX.PD.TimeFrequencyClustering
{
    /// <summary>
    /// One variable-length set of original XY rows. A .NET object reference in
    /// LabVIEW; call GetData and Bundle its outputs into a native cluster.
    /// </summary>
    public sealed class ClusterPointSet
    {
        private readonly int clusterId;
        private readonly double[,] pointsXY;
        private readonly int[] sourceRowIndices;
        private readonly double[] membershipStrength;

        internal ClusterPointSet(int id, double[,] points, int[] rows, double[] membership)
        {
            clusterId = id;
            pointsXY = points;
            sourceRowIndices = rows;
            membershipStrength = membership;
        }

        public int ClusterId { get { return clusterId; } }
        public int PointCount { get { return sourceRowIndices.Length; } }
        public double[,] PointsXY { get { return (double[,])pointsXY.Clone(); } }
        public int[] SourceRowIndices { get { return (int[])sourceRowIndices.Clone(); } }
        public double[] MembershipStrength { get { return (double[])membershipStrength.Clone(); } }

        /// <summary>One Invoke Node yields plain arrays suitable for LabVIEW Bundle.</summary>
        public void GetData(
            out int clusterId,
            out double[,] pointsXY,
            out int[] sourceRowIndices,
            out double[] membershipStrength)
        {
            clusterId = this.clusterId;
            pointsXY = (double[,])this.pointsXY.Clone();
            sourceRowIndices = (int[])this.sourceRowIndices.Clone();
            membershipStrength = (double[])this.membershipStrength.Clone();
        }
    }

    public sealed partial class TimeFrequencyClusterEngine
    {
        /// <summary>
        /// N x 2 matrix, one point per row. X (column 0) = frequency in Hz;
        /// Y (column 1) = time width in ns. Both must be finite and positive.
        /// clusters contains K variable-length point sets, never padded.
        /// Noise (-1) and invalid rows (-2) are returned separately, including
        /// their original coordinates and zero-based input row indices.
        /// </summary>
        public void GetXYClusters(
            double[,] xyPoints,
            out ClusterPointSet[] clusters,
            out ClusterPointSet noisePoints,
            out ClusterPointSet invalidPoints,
            out int[] clusterLabels)
        {
            GetXYClustersAdvanced(xyPoints, 8, 0,
                out clusters, out noisePoints, out invalidPoints, out clusterLabels);
        }

        public void GetXYClustersAdvanced(
            double[,] xyPoints,
            int maximumClusterCount,
            int minimumClusterSize,
            out ClusterPointSet[] clusters,
            out ClusterPointSet noisePoints,
            out ClusterPointSet invalidPoints,
            out int[] clusterLabels)
        {
            if (xyPoints == null) throw new ArgumentNullException("xyPoints");
            if (xyPoints.GetLength(1) != 2)
                throw new ArgumentException(
                    "Expected an N x 2 matrix: column 0 = frequency X (Hz), column 1 = time Y (ns). Transpose a 2 x N input first.",
                    "xyPoints");

            double[] membership;
            double[,] summary;
            ClusterCore(xyPoints, 1, 0, maximumClusterCount, minimumClusterSize,
                out clusterLabels, out membership, out summary);

            int clusterCount = summary.GetLength(0);
            List<int>[] rowsByGroup = new List<int>[clusterCount + 2];
            for (int i = 0; i < rowsByGroup.Length; i++) rowsByGroup[i] = new List<int>();
            for (int row = 0; row < clusterLabels.Length; row++)
            {
                int label = clusterLabels[row];
                int group = label >= 0 ? label : (label == NoiseLabel ? clusterCount : clusterCount + 1);
                rowsByGroup[group].Add(row);
            }

            clusters = new ClusterPointSet[clusterCount];
            for (int label = 0; label < clusterCount; label++)
                clusters[label] = CopyPointSet(xyPoints, membership, label, rowsByGroup[label]);
            noisePoints = CopyPointSet(xyPoints, membership, NoiseLabel, rowsByGroup[clusterCount]);
            invalidPoints = CopyPointSet(xyPoints, membership, InvalidLabel, rowsByGroup[clusterCount + 1]);
        }

        private static ClusterPointSet CopyPointSet(
            double[,] xyPoints, double[] membership, int label, List<int> sourceRows)
        {
            int count = sourceRows.Count;
            double[,] points = new double[count, 2];
            int[] indices = sourceRows.ToArray();
            double[] strengths = new double[count];
            for (int i = 0; i < count; i++)
            {
                int row = indices[i];
                points[i, 0] = xyPoints[row, 0];
                points[i, 1] = xyPoints[row, 1];
                strengths[i] = membership[row];
            }
            return new ClusterPointSet(label, points, indices, strengths);
        }
    }
}

