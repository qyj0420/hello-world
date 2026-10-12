using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace HVX.Location
{
    internal sealed class MultiFactorResult
    {
        internal double[] FactorMatrix = new double[0];
        internal int Rows;
        internal int Columns;
        internal string[] ColumnNames = new string[0];
        internal double[] MarkerMatrix = new double[0];
        internal int MarkerRows;
        internal double[] LocationMatrix = new double[0];
        internal string[] ActionLabels = new string[0];
        internal string[] ActionColors = new string[0];
        internal string[] DefectTypes = new string[0];
        internal double[] DefectProbabilities = new double[0];
        internal string[] DefectStatuses = new string[0];
        internal string[] Diagnostics = new string[0];
        internal string SummaryText = string.Empty;
        internal string RuntimeMessage = string.Empty;
        internal string RecommendationBasis = string.Empty;
        internal bool PulseEnergyUsed;
        internal int PulseEnergyRows;
        internal int PulseEnergyColumns;
        internal double[] PulseEnergyMatrix = new double[0];
        internal string[] PulseEnergyColumnNames = new string[0];
        internal string PulseEnergyStatus = string.Empty;
    }

    internal static class MultiFactorCore
    {
        private static readonly string[] Columns = {
            "phase_mask", "center_m", "low_m", "high_m", "pulse_count", "application_groups",
            "max_q_pc", "mean_q_pc", "relative_energy_q2", "pdiv_kv", "pdiv_over_u0",
            "qu_score", "defect_score", "count_score", "energy_score", "standard_score",
            "pdiv_score", "q_jump_score", "energy_growth_score", "phase_broadening_score",
            "growth_morphology_score", "multifactor_score", "decision_score", "action_code",
            "component_code", "defect_probability", "stable_action_code", "location_row_index",
            "real_waveform_energy_used", "real_waveform_integrated_energy", "real_waveform_mean_energy" };

        private static readonly string[] PulseEnergyColumns = {
            "accepted_row_index", "pdmap_id", "voltage_kv", "baseline", "peak_abs",
            "integral_abs", "integrated_energy", "valid_sample_count" };

        internal static MultiFactorResult Evaluate(double[,] matrix,
            double[] location, int locationRows, int locationColumns,
            double[] voltage, int voltageRows, int voltageColumns,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double mergeToleranceM, double operatingVoltageKv,
            double serviceYears, int bodyType, int jointType,
            int mode, bool english)
        {
            return Evaluate(matrix, null, location, locationRows, locationColumns,
                voltage, voltageRows, voltageColumns, jointsA, jointsB, jointsC,
                mergeToleranceM, operatingVoltageKv, serviceYears, bodyType, jointType,
                mode, english);
        }

        internal static MultiFactorResult Evaluate(double[,] matrix, double[,] pulseEnvelopeMatrix,
            double[] location, int locationRows, int locationColumns,
            double[] voltage, int voltageRows, int voltageColumns,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double mergeToleranceM, double operatingVoltageKv,
            double serviceYears, int bodyType, int jointType,
            int mode, bool english, int[][] locationInputRows = null)
        {
            MultiFactorResult result = new MultiFactorResult();
            result.ColumnNames = Columns;
            result.Columns = Columns.Length;
            result.Rows = locationRows;
            result.LocationMatrix = (double[])(location ?? new double[0]).Clone();
            PulseEnergySet pulseEnergy = AnalyzePulseEnergy(matrix, pulseEnvelopeMatrix);
            result.PulseEnergyUsed = pulseEnergy.Used;
            result.PulseEnergyRows = pulseEnergy.Rows;
            result.PulseEnergyColumns = PulseEnergyColumns.Length;
            result.PulseEnergyMatrix = pulseEnergy.Matrix;
            result.PulseEnergyColumnNames = PulseEnergyColumns;
            result.PulseEnergyStatus = pulseEnergy.Status;
            List<Pulse> pulses = ReadPulses(matrix, pulseEnergy);
            if (locationInputRows != null)
            {
                if (locationInputRows.Length != locationRows)
                    throw new ArgumentException("Location membership must match the location row count.");
                HashSet<int> retainedRows = new HashSet<int>(locationInputRows.SelectMany(rows => rows));
                pulses = pulses.Where(p => retainedRows.Contains(p.RowIndex)).ToList();
            }
            double maximumRealEnergy = pulses.Where(delegate(Pulse p) { return p.HasRealEnergy; })
                .Select(delegate(Pulse p) { return p.RealEnergy; }).DefaultIfEmpty(0.0).Max();
            double cableLength = CableLength(matrix, jointsA, jointsB, jointsC);
            Task12Bridge defect = new Task12Bridge();
            List<Factor> factors = new List<Factor>();
            for (int row = 0; row < locationRows; row++)
                factors.Add(Build(row, location, locationColumns, voltage, voltageRows, voltageColumns,
                    pulses, mergeToleranceM, operatingVoltageKv, serviceYears, bodyType, jointType,
                    jointsA, jointsB, jointsC, cableLength, defect, maximumRealEnergy,
                    pulseEnergy.Used, mode, english,
                    locationInputRows == null ? null : new HashSet<int>(locationInputRows[row])));

            List<double> values = new List<double>();
            List<double> markers = new List<double>();
            foreach (Factor f in factors)
            {
                values.AddRange(new[] { (double)f.PhaseMask, f.Center, f.Low, f.High, f.Count, f.Applications,
                    f.MaxQ, f.MeanQ, f.Energy, f.Pdiv, f.PdivRatio,
                    f.Qu, f.Defect, f.CountScore, f.EnergyScore, f.StandardScore,
                    f.PdivScore, f.QJump, f.EnergyGrowth, f.PhaseBroadening,
                    f.Morphology, f.MultiScore, f.DecisionScore, f.Action, f.Component,
                    f.DefectProbability, f.StableAction, rowIndex(factors, f),
                    f.RealEnergyUsed ? 1.0 : 0.0, f.IntegratedEnergy, f.MeanRealEnergy });
                if (locationColumns > 8 && rowIndex(factors, f) * locationColumns + 8 < result.LocationMatrix.Length)
                    result.LocationMatrix[rowIndex(factors, f) * locationColumns + 8] = f.Action;
                if (f.Action > 0) markers.AddRange(new[] { (double)f.PhaseMask, f.Center, (double)f.Action, f.DecisionScore });
            }
            result.FactorMatrix = values.ToArray();
            result.MarkerMatrix = markers.ToArray();
            result.MarkerRows = markers.Count / 4;
            result.ActionLabels = factors.Select(delegate(Factor f) { return ActionLabel(f.Action, english); }).ToArray();
            result.ActionColors = factors.Select(delegate(Factor f) { return ActionColor(f.Action); }).ToArray();
            result.DefectTypes = factors.Select(delegate(Factor f) { return f.DefectType; }).ToArray();
            result.DefectProbabilities = factors.Select(delegate(Factor f) { return f.DefectProbability; }).ToArray();
            result.DefectStatuses = factors.Select(delegate(Factor f) { return f.DefectStatus; }).ToArray();
            result.Diagnostics = factors.Select(delegate(Factor f) { return f.Diagnostic; }).ToArray();
            result.RecommendationBasis = english
                ? "Joint engineering reference: DL/T 1576-2016, Q/GDW 11838-2018, DL/T 596, IEEE Std 400.2 and IEEE Std 400.4. Apparent charge, true baseline-subtracted waveform energy (when supplied), PDIV and trend factors are combined; no single limit determines the conclusion."
                : "联合工程参考：DL/T 1576-2016、Q/GDW 11838-2018、DL/T 596、IEEE Std 400.2及IEEE Std 400.4。综合放电量、真实波形基线扣除积分能量（有输入时）、PDIV及趋势因素，不以单一限值作结论。";
            if (!pulseEnergy.Used)
                result.RuntimeMessage = english
                    ? "[Info] No usable pulse-window energy was provided; energy factors were disabled and the remaining weights were renormalized."
                    : "[提示] 未提供可用的脉冲窗口能量，真实波形能量因素已禁用，其余因素权重已重新归一化。";
            result.SummaryText = Summary(factors, mode, english, result.RecommendationBasis);
            return result;
        }

        // Classification is a post-conclusion annotation. No action/count/charge
        // or scoring member is changed by the external model or its failures.
        internal static void AttachDefectLabels(AnalysisResult result, double[,] matrix,
            int[][] locationInputRows, string defectModelPath, bool english)
        {
            int rows = result.LocationRows;
            result.DefectTypeArray = new string[rows];
            result.DefectProbabilityArray = new double[rows];
            result.DefectStatusArray = new string[rows];
            string[] diagnostics = new string[rows];
            bool enabled = !string.IsNullOrWhiteSpace(defectModelPath);
            // Empty path never constructs or loads an external model.
            Task12Bridge classifier = enabled && Enumerable.Range(0, rows).Any(r =>
                Cell(result.LocationMatrix, r, result.LocationColumns, 8) > 0.0)
                ? new Task12Bridge(defectModelPath) : null;
            try
            {
            List<Pulse> pulses = ReadPulses(matrix, null);
            StringBuilder labels = new StringBuilder();
            for (int row = 0; row < rows; row++)
            {
                result.DefectTypeArray[row] = string.Empty;
                diagnostics[row] = result.DiagnosticArray != null && row < result.DiagnosticArray.Length
                    ? result.DiagnosticArray[row] : string.Empty;
                bool needsLabel = Cell(result.LocationMatrix, row, result.LocationColumns, 8) > 0.0;
                if (!enabled || !needsLabel)
                {
                    result.DefectStatusArray[row] = !enabled
                        ? "DISABLED|empty-defect-model-path" : "SKIPPED|no-action-location";
                    continue;
                }
                if (locationInputRows == null || row >= locationInputRows.Length)
                {
                    result.DefectStatusArray[row] = "UNAVAILABLE|missing-location-member-map";
                    AppendRuntimeMessage(result, result.DefectStatusArray[row]);
                    continue;
                }
                HashSet<int> selected = new HashSet<int>(locationInputRows[row]);
                List<Pulse> members = pulses.Where(p => selected.Contains(p.RowIndex)).ToList();
                List<string> phaseTypes = new List<string>();
                List<string> phaseStatuses = new List<string>();
                // A merged location can contain several phases, but each PRPD
                // classification must use only the pulses of one physical phase.
                foreach (IGrouping<int, Pulse> phase in members.GroupBy(p => p.Phase).OrderBy(g => g.Key))
                {
                    string phaseName = PhaseText(1 << (phase.Key - 1), english);
                    Task12Result prediction = classifier.Classify(
                        phase.Select(p => p.PhaseDegree).ToArray(), phase.Select(p => p.Charge).ToArray());
                    bool accepted = prediction.Available && !prediction.Rejected;
                    phaseStatuses.Add(phaseName + ": " + prediction.Status);
                    string detail = accepted
                        ? prediction.Type + " " + (prediction.Probability * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                        : prediction.Status;
                    string text = (english ? "Defect label: " : "缺陷类型：") + detail;
                    diagnostics[row] = (diagnostics[row] + " " + phaseName + ": " + text).Trim();
                    if (accepted)
                    {
                        phaseTypes.Add(phaseName + (english ? ": " : "：") + detail);
                        result.DefectProbabilityArray[row] = Math.Max(result.DefectProbabilityArray[row], prediction.Probability);
                        labels.AppendLine().Append(phaseName).Append(english ? " near " : "约")
                            .Append(Cell(result.LocationMatrix, row, result.LocationColumns, 1).ToString("0.0", CultureInfo.InvariantCulture))
                            .Append(" m ").Append(text);
                    }
                    else
                        AppendRuntimeMessage(result, (english ? "[Defect recognition] " : "[缺陷识别] ") + phaseName + ": " + prediction.Status);
                }
                result.DefectTypeArray[row] = string.Join(english ? "; " : "；", phaseTypes.ToArray());
                result.DefectStatusArray[row] = string.Join(english ? "; " : "；", phaseStatuses.ToArray());
                if (result.FactorColumns > 25 && result.FactorRows > row)
                    result.FactorMatrix[row * result.FactorColumns + 25] = result.DefectProbabilityArray[row];
            }
            result.DiagnosticArray = diagnostics;
            if (labels.Length > 0) result.SummaryText += labels.ToString();
            }
            finally
            {
                if (classifier != null) classifier.Dispose();
            }
        }

        internal static void AppendRuntimeMessage(AnalysisResult result, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            message = LocatorEngine.SingleLine(message).Trim();
            string current = result.Error ?? string.Empty;
            if (current.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Contains(message)) return;
            result.Error = current.Length == 0 ? message : current + Environment.NewLine + message;
        }

        private static int rowIndex(List<Factor> list, Factor item) { return list.IndexOf(item); }

        private static Factor Build(int row, double[] location, int locationColumns,
            double[] voltage, int voltageRows, int voltageColumns, List<Pulse> all,
            double tolerance, double u0, double serviceYears, int bodyType, int jointType,
            double[] jointsA, double[] jointsB, double[] jointsC, double cableLength,
            Task12Bridge task12, double maximumRealEnergy, bool pulseEnergyUsed,
            int mode, bool english, HashSet<int> selectedRows = null)
        {
            Factor f = new Factor();
            f.PhaseMask = (int)Math.Round(Cell(location, row, locationColumns, 0));
            f.Center = Cell(location, row, locationColumns, 1);
            f.Low = Cell(location, row, locationColumns, 2);
            f.High = Cell(location, row, locationColumns, 3);
            f.StableAction = (int)Math.Round(Cell(location, row, locationColumns, 8));
            double expand = Math.Max(0.25, tolerance > 0.0 ? tolerance : 0.5);
            List<Pulse> members = all.Where(delegate(Pulse p)
            {
                if (selectedRows != null) return selectedRows.Contains(p.RowIndex);
                return (f.PhaseMask & (1 << (p.Phase - 1))) != 0 && p.Position >= f.Low - expand && p.Position <= f.High + expand;
            }).ToList();
            f.Count = members.Count > 0 ? members.Count : (int)Math.Round(Cell(location, row, locationColumns, 4));
            f.Applications = members.Select(delegate(Pulse p) { return p.DataId; }).Distinct().Count();
            List<double> q = members.Select(delegate(Pulse p) { return Math.Abs(p.Charge); }).ToList();
            f.MaxQ = q.Count > 0 ? q.Max() : Cell(location, row, locationColumns, 5);
            f.MeanQ = q.Count > 0 ? q.Average() : Cell(location, row, locationColumns, 6);
            List<Pulse> energyMembers = members.Where(delegate(Pulse p) { return p.HasRealEnergy; }).ToList();
            f.RealEnergyUsed = pulseEnergyUsed && energyMembers.Count > 0;
            f.IntegratedEnergy = energyMembers.Sum(delegate(Pulse p) { return p.RealEnergy; });
            f.MeanRealEnergy = energyMembers.Count > 0 ? f.IntegratedEnergy / energyMembers.Count : 0.0;
            f.Energy = members.Sum(delegate(Pulse p) { return p.Charge * p.Charge; });
            f.Pdiv = Cell(voltage, row, voltageColumns, 2);
            if (!(f.Pdiv > 0.0)) f.Pdiv = EstimatePdiv(members);
            f.PdivRatio = u0 > 0.0 && f.Pdiv > 0.0 ? f.Pdiv / u0 : 0.0;
            SortedDictionary<double, Level> levels = Levels(members);
            f.Qu = QuScore(levels);
            f.QJump = JumpScore(levels);
            f.EnergyGrowth = RealEnergyGrowth(levels, f.RealEnergyUsed);
            f.PhaseBroadening = PhaseBroadening(levels);
            f.Morphology = Morphology(levels);
            f.CountScore = Clamp(Math.Log(1.0 + Math.Max(0, f.Count)) / Math.Log(81.0), 0.0, 1.0);
            f.Component = Component(f.Center, f.PhaseMask, cableLength, tolerance, jointsA, jointsB, jointsC);
            double reference = ReferenceCharge(f.Component, serviceYears, bodyType, jointType);
            f.EnergyScore = f.RealEnergyUsed && maximumRealEnergy > 0.0
                ? Clamp(Math.Log(1.0 + f.MeanRealEnergy) / Math.Log(1.0 + maximumRealEnergy), 0.0, 1.0)
                : 0.0;
            f.StandardScore = Clamp(Math.Log10(1.0 + f.MaxQ / reference) / Math.Log10(11.0), 0.0, 1.0);
            f.PdivScore = PdivRisk(f.PdivRatio, u0 > 0.0);
            Task12Result dr = task12.Classify(members.Select(delegate(Pulse p) { return p.PhaseDegree; }).ToArray(),
                members.Select(delegate(Pulse p) { return p.Charge; }).ToArray());
            f.Defect = dr.Score;
            f.DefectProbability = dr.Probability;
            f.DefectType = dr.Type;
            f.DefectStatus = dr.Status;

            double nonEnergySum = 0.14 * f.Qu + 0.10 * f.CountScore + 0.06 * f.StandardScore +
                0.14 * f.PdivScore + 0.08 * f.QJump + 0.05 * f.PhaseBroadening + 0.08 * f.Morphology;
            double baseSum = f.RealEnergyUsed
                ? nonEnergySum + 0.09 * f.EnergyScore + 0.08 * f.EnergyGrowth
                : nonEnergySum * (0.82 / 0.65);
            f.MultiScore = dr.Available ? baseSum + 0.18 * f.Defect : baseSum / 0.82;
            double stableScore = f.StableAction >= 3 ? 0.90 : (f.StableAction == 2 ? 0.68 : (f.StableAction == 1 ? 0.44 : 0.18));
            f.DecisionScore = mode == 1 ? f.MultiScore : 0.55 * stableScore + 0.45 * f.MultiScore;
            f.Action = f.DecisionScore >= 0.78 ? 3 : (f.DecisionScore >= 0.56 ? 2 : (f.DecisionScore >= 0.38 ? 1 : 0));
            ApplyGates(f, dr);
            f.Diagnostic = Diagnostic(f, mode, english);
            return f;
        }

        private static void ApplyGates(Factor f, Task12Result defect)
        {
            // Two pulses from two independent pressure/location-data groups are
            // weak evidence, but they are still repeat evidence. Keep their
            // conclusion capped at Observe and preserve that Observe floor after
            // the PDIV downgrade. A single pulse, or two pulses from only one
            // group, may still be suppressed to no marker.
            bool repeatedJointEvidence = f.Count >= 2 && f.Applications >= 2 && !defect.Rejected;
            if (f.Count <= 2)
            {
                f.Action = repeatedJointEvidence ? Math.Max(1, Math.Min(f.Action, 1)) : 0;
            }
            else if (f.Count <= 4 && !(defect.Available && !defect.Rejected && defect.Probability >= 0.85 && f.PdivRatio > 0.0 && f.PdivRatio <= 1.5))
                f.Action = Math.Min(f.Action, 1);
            if (defect.Rejected) f.Action = f.Count < 8 ? 0 : Math.Min(f.Action, 1);
            if (f.PdivRatio > 2.0) f.Action = Math.Max(0, f.Action - 2);
            else if (f.PdivRatio > 1.7) f.Action = Math.Max(0, f.Action - 1);
            if (repeatedJointEvidence) f.Action = Math.Max(1, f.Action);
        }

        private static string Summary(List<Factor> factors, int mode, bool english, string basis)
        {
            if (factors.Count == 0)
            {
                string empty = english
                    ? "No suspicious discharge was found; no marker is required."
                    : "未发现可疑放电，无需标识。";
                return (empty + Environment.NewLine + basis).Trim();
            }
            int immediate = factors.Count(delegate(Factor f) { return f.Action == 3; });
            int attention = factors.Count(delegate(Factor f) { return f.Action == 2; });
            int track = factors.Count(delegate(Factor f) { return f.Action == 1; });
            int none = factors.Count - immediate - attention - track;
            StringBuilder s = new StringBuilder();
            if (english)
                s.Append(mode == 1 ? "Multi-factor mode" : "Stable/multi-factor hybrid mode").Append(" evaluated ").Append(factors.Count)
                    .Append(" locations: immediate ").Append(immediate).Append(", attention ").Append(attention)
                    .Append(", tracking ").Append(track).Append(", no marker ").Append(none).Append(".");
            else
                s.Append(mode == 1 ? "综合多因素模式" : "稳定算法+多因素融合模式").Append("共评估").Append(factors.Count)
                    .Append("处位置：及时处理").Append(immediate).Append("处、重点关注").Append(attention)
                    .Append("处、建议观察/跟踪").Append(track).Append("处、无需标识").Append(none).Append("处。");
            int index = 1;
            // Preserve LocationMatrix row order. The report numbering, marker rows,
            // FactorMatrix rows and diagnostics therefore remain one-to-one with
            // the stable runtime output instead of silently re-sorting by severity.
            foreach (Factor f in factors.Where(delegate(Factor x) { return x.Action > 0; }))
                s.Append("\r\n").Append(index++).Append(". ").Append(ActionLabel(f.Action, english)).Append(english ? ": " : "：")
                    .Append(PhaseText(f.PhaseMask, english)).Append(english ? " near " : "约").Append(f.Center.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(" m；").Append(CustomerDiagnostic(f, english));
            return s.Append("\r\n").Append(basis).ToString().Trim();
        }

        private static PulseEnergySet AnalyzePulseEnergy(double[,] matrix, double[,] pulseEnvelopeMatrix)
        {
            PulseEnergySet output = new PulseEnergySet();
            int matrixRows = matrix == null ? 0 : matrix.GetLength(0);
            int matrixColumns = matrix == null ? 0 : matrix.GetLength(1);
            int waveRows = pulseEnvelopeMatrix == null ? 0 : pulseEnvelopeMatrix.GetLength(0);
            int waveColumns = pulseEnvelopeMatrix == null ? 0 : pulseEnvelopeMatrix.GetLength(1);
            bool hasWaveMatrix = waveRows > 0 && waveColumns > 0;
            if (hasWaveMatrix && matrixRows != waveRows)
                throw new ArgumentException("pulseEnvelopeMatrix row count must equal the accepted location-data row count.", "pulseEnvelopeMatrix");

            output.Rows = matrixRows;
            output.Energy = new double[matrixRows];
              output.Peak = new double[matrixRows];
              output.Integral = new double[matrixRows];
              output.ValidSamples = new int[matrixRows];
              output.Matrix = new double[matrixRows * PulseEnergyColumns.Length];
              // Pulse energy is optional and is enabled only when the caller
              // supplies the two-dimensional pulse-window matrix. An empty
              // input must disable all energy factors instead of silently
              // falling back to task 17 column 18.
              if (!hasWaveMatrix)
              {
                  output.Used = false;
                  output.Status = "DISABLED|empty-pulse-envelope";
                  return output;
              }
              bool hasEnergy = false;
            int task17EnergyRows = 0;
            int fallbackEnergyRows = 0;
            for (int row = 0; row < matrixRows; row++)
            {
                double baseline = double.NaN, peak = double.NaN, integral = double.NaN;
                double fallbackEnergy = double.NaN;
                int valid = 0;
                if (hasWaveMatrix)
                    MeasurePhysicalIncidentWindow(matrix, row, pulseEnvelopeMatrix, waveColumns,
                        out baseline, out peak, out integral, out fallbackEnergy, out valid);

                // Task 17 v2.4.2 column 18 already uses the causal incident/overlap
                // physical window, so it is authoritative. This prevents Task 15
                // from integrating the complete 3x-propagation display window.
                bool fromTask17 = matrixColumns > 18 && Finite(matrix[row, 18]) && matrix[row, 18] > 0.0;
                double energy = fromTask17 ? matrix[row, 18] : fallbackEnergy;
                if (fromTask17) task17EnergyRows++;
                else if (Finite(fallbackEnergy) && fallbackEnergy > 0.0) fallbackEnergyRows++;
                output.Energy[row] = Finite(energy) && energy > 0.0 ? energy : 0.0;
                output.Peak[row] = peak;
                output.Integral[row] = integral;
                output.ValidSamples[row] = valid;
                if (output.Energy[row] > 0.0) hasEnergy = true;
                int offset = row * PulseEnergyColumns.Length;
                output.Matrix[offset] = row;
                output.Matrix[offset + 1] = matrix != null && matrix.GetLength(1) > 9 && Finite(matrix[row, 9])
                    ? matrix[row, 9]
                    : (matrix != null && matrix.GetLength(1) > 1 && Finite(matrix[row, 1]) ? matrix[row, 1] : row);
                output.Matrix[offset + 2] = matrix != null && matrix.GetLength(1) > 2 && Finite(matrix[row, 2]) ? matrix[row, 2] : 0.0;
                output.Matrix[offset + 3] = baseline;
                output.Matrix[offset + 4] = peak;
                output.Matrix[offset + 5] = integral;
                output.Matrix[offset + 6] = output.Energy[row];
                output.Matrix[offset + 7] = valid;
            }
            output.Used = hasEnergy;
            output.Status = hasEnergy
                ? "USED|task17-col18-physical-window=" + task17EnergyRows.ToString(CultureInfo.InvariantCulture) +
                    "|pulse-envelope-quiet-boundary-fallback=" + fallbackEnergyRows.ToString(CultureInfo.InvariantCulture)
                : (hasWaveMatrix ? "DISABLED|no-finite-nonzero-physical-window-energy" :
                    "DISABLED|empty-pulse-envelope-and-no-task17-col18-energy");
            return output;
        }

        private static void MeasurePhysicalIncidentWindow(double[,] matrix, int row,
            double[,] waves, int waveColumns, out double baseline, out double peak,
            out double integral, out double energy, out int valid)
        {
            int columns = matrix == null ? 0 : matrix.GetLength(1);
            int direct = columns > 20 && Finite(matrix[row, 20])
                ? (int)Math.Round(matrix[row, 20]) : -1;
            if (direct < 0 && columns > 31 && columns > 10 &&
                Finite(matrix[row, 10]) && Finite(matrix[row, 31]))
                direct = (int)Math.Round(matrix[row, 10] - matrix[row, 31]);
            direct = Math.Max(0, Math.Min(waveColumns - 1, direct < 0 ? 0 : direct));

            int reflection = waveColumns;
            if (columns > 31 && columns > 11 &&
                Finite(matrix[row, 11]) && Finite(matrix[row, 31]))
                reflection = (int)Math.Round(matrix[row, 11] - matrix[row, 31]);
            reflection = Math.Max(direct + 1, Math.Min(waveColumns, reflection));

            List<double> before = new List<double>();
            for (int c = Math.Max(0, direct - 64); c < direct; c++)
                if (Finite(waves[row, c])) before.Add(waves[row, c]);
            if (before.Count == 0)
                for (int c = 0; c < Math.Max(1, Math.Min(waveColumns, waveColumns / 20)); c++)
                    if (Finite(waves[row, c])) before.Add(waves[row, c]);
            baseline = Median(before);
            double baselineValue = baseline;
            double sigma = 1.4826 * Median(before.Select(
                delegate(double sample) { return Math.Abs(sample - baselineValue); }).ToList());

            int preReflectionEnd = Math.Max(direct, reflection - 1);
            int amplitudePeak = direct;
            double peakMagnitude = 0.0;
            for (int c = direct; c <= preReflectionEnd && c < waveColumns; c++)
            {
                if (!Finite(waves[row, c])) continue;
                double magnitude = Math.Abs(waves[row, c] - baseline);
                if (magnitude > peakMagnitude) { peakMagnitude = magnitude; amplitudePeak = c; }
            }
            double fs = columns > 7 && Finite(matrix[row, 7]) && matrix[row, 7] > 0.0
                ? matrix[row, 7] : 0.0;
            int quietRun = fs > 0.0
                ? Math.Max(4, Math.Min(256, (int)Math.Round(fs * 80.0e-9)))
                : Math.Max(4, Math.Min(32, waveColumns / 100));
            double threshold = Math.Max(3.0 * sigma, peakMagnitude * 0.01);
            int end = FindQuietBoundary(waves, row, amplitudePeak,
                preReflectionEnd, baseline, threshold, quietRun);
            if (end < 0 && reflection < waveColumns)
                end = FindQuietBoundary(waves, row, reflection,
                    waveColumns - 1, baseline, threshold, quietRun);
            if (end < 0) end = preReflectionEnd;
            end = Math.Max(direct, Math.Min(waveColumns - 1, end));

            peak = 0.0; integral = 0.0; double squared = 0.0; valid = 0;
            for (int c = direct; c <= end; c++)
            {
                if (!Finite(waves[row, c])) continue;
                double centered = waves[row, c] - baseline;
                double magnitude = Math.Abs(centered);
                if (magnitude > peak) peak = magnitude;
                integral += magnitude; squared += centered * centered; valid++;
            }
            energy = valid > 0 && squared > 0.0
                ? (fs > 0.0 ? squared / fs : squared) : double.NaN;
        }

        private static int FindQuietBoundary(double[,] samples, int row, int start,
            int end, double baseline, double threshold, int quietRun)
        {
            int quiet = 0;
            for (int c = Math.Max(0, start); c <= end; c++)
            {
                double value = samples[row, c];
                if (Finite(value) && Math.Abs(value - baseline) <= threshold) quiet++;
                else quiet = 0;
                if (quiet >= quietRun) return Math.Max(start, c - quietRun);
            }
            return -1;
        }

        private static double Median(List<double> values)
        {
            if (values == null || values.Count == 0) return 0.0;
            double[] sorted = values.OrderBy(delegate(double value) { return value; }).ToArray();
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0 ? 0.5 * (sorted[middle - 1] + sorted[middle]) : sorted[middle];
        }

        private static List<Pulse> ReadPulses(double[,] matrix, PulseEnergySet pulseEnergy)
        {
            List<Pulse> output = new List<Pulse>();
            if (matrix == null || matrix.GetLength(1) < 16) return output;
            int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
            for (int r = 0; r < rows; r++)
            {
                int phase = (int)Math.Round(matrix[r, 0]);
                if (phase < 1 || phase > 3 || !Finite(matrix[r, 13])) continue;
                if (cols > 35 && (!Finite(matrix[r, 35]) || Math.Abs(matrix[r, 35] - 1.0) > 1e-9)) continue;
                // Use the same charge and fallback policy as the location stage.
                double charge = LocatorEngine.ReadLocationCharge(matrix, r, cols);
                if (!Finite(charge)) continue;
                output.Add(new Pulse {
                    RowIndex = r,
                    Phase = phase,
                    DataId = Finite(matrix[r, 1]) ? matrix[r, 1] : 0.0,
                    Voltage = Finite(matrix[r, 2]) ? matrix[r, 2] : 0.0,
                    PhaseDegree = Finite(matrix[r, 3]) ? Phase(matrix[r, 3]) : 0.0,
                    Position = matrix[r, 13], Charge = charge,
                    HasRealEnergy = pulseEnergy != null && pulseEnergy.Used && r < pulseEnergy.Energy.Length && pulseEnergy.Energy[r] > 0.0,
                    RealEnergy = pulseEnergy != null && r < pulseEnergy.Energy.Length ? pulseEnergy.Energy[r] : 0.0
                });
            }
            return output;
        }

        private static SortedDictionary<double, Level> Levels(List<Pulse> pulses)
        {
            SortedDictionary<double, Level> levels = new SortedDictionary<double, Level>();
            foreach (Pulse p in pulses)
            {
                double key = Math.Round(p.Voltage * 10.0) / 10.0;
                Level level;
                if (!levels.TryGetValue(key, out level)) { level = new Level(); levels.Add(key, level); }
                level.Q.Add(Math.Abs(p.Charge));
                level.E.Add(p.Charge * p.Charge);
                level.Phi.Add(p.PhaseDegree);
                if (p.HasRealEnergy) level.RealE.Add(p.RealEnergy);
            }
            return levels;
        }

        private static double QuScore(SortedDictionary<double, Level> levels)
        {
            if (levels.Count < 2) return 0.20;
            double[] x = levels.Keys.ToArray();
            double[] y = levels.Values.Select(delegate(Level l) { return Math.Log(1.0 + l.Q.Average()); }).ToArray();
            double correlation = Math.Max(0.0, Corr(x, y));
            double growth = Clamp((y[y.Length - 1] - y[0]) / Math.Max(Math.Abs(y[0]), 0.5), 0.0, 1.0);
            return Clamp(0.60 * correlation + 0.40 * growth, 0.0, 1.0);
        }

        private static double JumpScore(SortedDictionary<double, Level> levels)
        {
            if (levels.Count < 2) return 0.0;
            double[] q = levels.Values.Select(delegate(Level l) { return Math.Max(l.Q.Average(), 1e-9); }).ToArray();
            double ratio = 1.0;
            for (int i = 1; i < q.Length; i++) ratio = Math.Max(ratio, q[i] / q[i - 1]);
            return Clamp(Math.Log(ratio, 2.0) / 3.0, 0.0, 1.0);
        }

        private static double RealEnergyGrowth(SortedDictionary<double, Level> levels, bool enabled)
        {
            if (!enabled) return 0.0;
            var usable = levels.Where(delegate(KeyValuePair<double, Level> pair) { return pair.Value.RealE.Count > 0; }).ToArray();
            if (usable.Length < 2) return 0.0;
            double[] x = usable.Select(delegate(KeyValuePair<double, Level> pair) { return pair.Key; }).ToArray();
            double[] y = usable.Select(delegate(KeyValuePair<double, Level> pair) { return Math.Log(1.0 + pair.Value.RealE.Average()); }).ToArray();
            double correlation = Math.Max(0.0, Corr(x, y));
            double growth = Clamp((y[y.Length - 1] - y[0]) / Math.Max(Math.Abs(y[0]), 1.0), 0.0, 1.0);
            return Clamp(0.55 * correlation + 0.45 * growth, 0.0, 1.0);
        }

        private static double PhaseBroadening(SortedDictionary<double, Level> levels)
        {
            if (levels.Count < 2) return 0.0;
            return Clamp((CircularSpan(levels.Last().Value.Phi) - CircularSpan(levels.First().Value.Phi)) / 90.0, 0.0, 1.0);
        }

        private static double Morphology(SortedDictionary<double, Level> levels)
        {
            if (levels.Count < 2) return 0.20;
            double[] x = levels.Keys.ToArray();
            double[] y = levels.Values.Select(delegate(Level l) { return Math.Log(1.0 + l.Q.Average()); }).ToArray();
            int positive = 0;
            for (int i = 1; i < y.Length; i++) if (y[i] >= y[i - 1] * 0.98) positive++;
            double r = Corr(x, y);
            return Clamp(0.55 * positive / Math.Max(1.0, y.Length - 1.0) + 0.45 * Math.Max(0.0, r * r), 0.0, 1.0);
        }

        private static double Corr(double[] x, double[] y)
        {
            if (x.Length != y.Length || x.Length < 2) return 0.0;
            double ax = x.Average(), ay = y.Average(), xx = 0.0, yy = 0.0, xy = 0.0;
            for (int i = 0; i < x.Length; i++)
            {
                double dx = x[i] - ax, dy = y[i] - ay;
                xx += dx * dx; yy += dy * dy; xy += dx * dy;
            }
            return xx <= 1e-12 || yy <= 1e-12 ? 0.0 : xy / Math.Sqrt(xx * yy);
        }

        private static double CircularSpan(List<double> values)
        {
            if (values.Count < 2) return 0.0;
            double[] sorted = values.Select(Phase).OrderBy(delegate(double x) { return x; }).ToArray();
            double gap = 360.0 - sorted[sorted.Length - 1] + sorted[0];
            for (int i = 1; i < sorted.Length; i++) gap = Math.Max(gap, sorted[i] - sorted[i - 1]);
            return 360.0 - gap;
        }

        private static double EstimatePdiv(List<Pulse> pulses)
        {
            if (pulses.Count == 0) return 0.0;
            var levels = pulses.GroupBy(delegate(Pulse p) { return Math.Round(p.Voltage * 10.0) / 10.0; }).OrderBy(delegate(IGrouping<double, Pulse> g) { return g.Key; });
            int required = Math.Max(2, (int)Math.Ceiling(pulses.Count * 0.05));
            foreach (var level in levels) if (level.Count() >= required) return level.Key;
            return levels.First().Key;
        }

        private static double PdivRisk(double ratio, bool hasU0)
        {
            if (!hasU0 || ratio <= 0.0) return 0.50;
            if (ratio <= 1.0) return 1.00;
            if (ratio <= 1.3) return 0.85;
            if (ratio <= 1.5) return 0.70;
            if (ratio <= 1.7) return 0.55;
            if (ratio <= 2.0) return 0.30;
            return 0.10;
        }

        private static int Component(double center, int phaseMask, double cableLength, double tolerance, double[] a, double[] b, double[] c)
        {
            double[][] phaseJoints = new[] { a, b, c };
            int component = 0;
            for (int phase = 0; phase < 3; phase++)
            {
                if ((phaseMask & (1 << phase)) == 0) continue;
                double[] joints = Safe(phaseJoints[phase]);
                double length = joints.Length > 0 ? joints[joints.Length - 1] : cableLength;
                double window = Math.Max(2.0, Math.Max(tolerance, length * 0.01));
                if (Math.Abs(center) <= window || (length > 0.0 && Math.Abs(center - length) <= window))
                    return 2;
                foreach (double joint in joints)
                    if (Finite(joint) && joint > window && joint < length - window && Math.Abs(center - joint) <= window)
                        component = 1;
            }
            return component;
        }

        private static double ReferenceCharge(int component, double years, int bodyType, int jointType)
        {
            bool old = years > 1.0;
            if (component == 2) return old ? 5000.0 : 3000.0;
            if (component == 1)
            {
                bool pilc = jointType == 2 || (jointType == 0 && bodyType == 1);
                return pilc ? (old ? 3000.0 : 2000.0) : (old ? 500.0 : 300.0);
            }
            return bodyType == 1 ? 1000.0 : 100.0;
        }

        private static double CableLength(double[,] matrix, double[] a, double[] b, double[] c)
        {
            List<double> values = Safe(a).Concat(Safe(b)).Concat(Safe(c)).Where(delegate(double x) { return Finite(x) && x > 0.0; }).ToList();
            if (values.Count > 0) return values.Max();
            if (matrix != null && matrix.GetLength(1) > 5)
                for (int r = 0; r < matrix.GetLength(0); r++) if (Finite(matrix[r, 5]) && matrix[r, 5] > 0.0) values.Add(matrix[r, 5]);
            return values.Count > 0 ? values.Max() : 0.0;
        }

        private static string CustomerDiagnostic(Factor f, bool english)
        {
            string energy = !f.RealEnergyUsed ? string.Empty : (english
                ? string.Format(CultureInfo.InvariantCulture, "; waveform energy {0:0.###}, growth {1:0}%", f.IntegratedEnergy, f.EnergyGrowth * 100.0)
                : string.Format(CultureInfo.InvariantCulture, "、波形积分能量{0:0.###}、增长率{1:0}%", f.IntegratedEnergy, f.EnergyGrowth * 100.0));
            return string.Format(CultureInfo.InvariantCulture, english
                ? "score {0:0}%; {1} pulses/{2} applications; max/mean Q {3:0}/{4:0} pC; PDIV {5:0.0} kV ({6:0.00}U0); Q-U {7:0}%, phase broadening {8:0}%, morphology {9:0}%{10}."
                : "综合分{0:0}%；{1}个脉冲/{2}组加压，最大/平均放电量{3:0}/{4:0} pC，PDIV {5:0.0} kV（{6:0.00}U0）；Q-U {7:0}%、相位展宽{8:0}%、增长形态{9:0}%{10}。",
                f.DecisionScore * 100.0, f.Count, f.Applications, f.MaxQ, f.MeanQ, f.Pdiv, f.PdivRatio,
                f.Qu * 100.0, f.PhaseBroadening * 100.0, f.Morphology * 100.0, energy);
        }

        private static string Diagnostic(Factor f, int mode, bool english)
        {
            string type = string.IsNullOrEmpty(f.DefectType) ? (english ? "unavailable" : "不可用") : f.DefectType;
            string energyText = f.RealEnergyUsed
                ? (english
                    ? string.Format(CultureInfo.InvariantCulture, "true waveform energy {0:0.###}, growth {1:0}%", f.IntegratedEnergy, f.EnergyGrowth * 100.0)
                    : string.Format(CultureInfo.InvariantCulture, "真实波形积分能量{0:0.###}、增长率{1:0}%", f.IntegratedEnergy, f.EnergyGrowth * 100.0))
                : (english ? "true waveform energy disabled" : "真实波形能量未启用");
            if (english) return string.Format(CultureInfo.InvariantCulture,
                "score {0:0}%; {1} pulses/{2} applications; max/mean Q {3:0}/{4:0} pC; PDIV {5:0.0} kV ({6:0.00}U0); defect labels excluded from tier scoring; Q-U {9:0}%, {10}, phase broadening {11:0}%, morphology {12:0}%.",
                f.DecisionScore * 100.0, f.Count, f.Applications, f.MaxQ, f.MeanQ, f.Pdiv, f.PdivRatio,
                type, f.DefectProbability * 100.0, f.Qu * 100.0, energyText,
                f.PhaseBroadening * 100.0, f.Morphology * 100.0);
            return string.Format(CultureInfo.InvariantCulture,
                "综合分{0:0}%；{1}个脉冲/{2}组加压，最大/平均放电量{3:0}/{4:0} pC，PDIV {5:0.0} kV（{6:0.00}U0）；类型不参与处置评分；Q-U {9:0}%、{10}、相位展宽{11:0}%、增长形态{12:0}%。",
                f.DecisionScore * 100.0, f.Count, f.Applications, f.MaxQ, f.MeanQ, f.Pdiv, f.PdivRatio,
                type, f.DefectProbability * 100.0, f.Qu * 100.0, energyText,
                f.PhaseBroadening * 100.0, f.Morphology * 100.0);
        }

        private static string ActionLabel(int code, bool english)
        {
            if (english) return code == 3 ? "Immediate action" : (code == 2 ? "Priority attention" : (code == 1 ? "Planned tracking" : "No marker"));
            return code == 3 ? "及时处理" : (code == 2 ? "重点关注" : (code == 1 ? "建议观察/计划跟踪" : "无需标识"));
        }

        private static string ActionColor(int code) { return code == 3 ? "#D93025" : (code == 2 ? "#F57C00" : (code == 1 ? "#2B6CB0" : "")); }

        private static string PhaseText(int mask, bool english)
        {
            List<string> names = new List<string>();
            if ((mask & 1) != 0) names.Add("A"); if ((mask & 2) != 0) names.Add("B"); if ((mask & 4) != 0) names.Add("C");
            string joined = string.Join("/", names.ToArray());
            return english ? joined + " phase" + (names.Count > 1 ? "s" : "") : joined + "相";
        }

        private static double Cell(double[] values, int row, int columns, int column)
        {
            int index = row * columns + column;
            return values != null && column < columns && index >= 0 && index < values.Length ? values[index] : 0.0;
        }

        private static double Phase(double value) { value %= 360.0; return value < 0.0 ? value + 360.0 : value; }
        private static double Clamp(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static double[] Safe(double[] values) { return values ?? new double[0]; }

        private sealed class Pulse
        {
            internal int RowIndex, Phase;
            internal double DataId;
            internal double Voltage, PhaseDegree, Position, Charge;
            internal bool HasRealEnergy;
            internal double RealEnergy;
        }

        private sealed class Level
        {
            internal readonly List<double> Q = new List<double>();
            internal readonly List<double> E = new List<double>();
            internal readonly List<double> RealE = new List<double>();
            internal readonly List<double> Phi = new List<double>();
        }

        private sealed class PulseEnergySet
        {
            internal bool Used;
            internal int Rows;
            internal double[] Matrix = new double[0];
            internal double[] Energy = new double[0];
            internal double[] Peak = new double[0];
            internal double[] Integral = new double[0];
            internal int[] ValidSamples = new int[0];
            internal string Status = string.Empty;
        }

        private sealed class Factor
        {
            internal int PhaseMask, Count, Applications, Action, Component, StableAction;
            internal double Center, Low, High, MaxQ, MeanQ, Energy, Pdiv, PdivRatio;
            internal double Qu, Defect, CountScore, EnergyScore, StandardScore, PdivScore;
            internal double QJump, EnergyGrowth, PhaseBroadening, Morphology, MultiScore, DecisionScore, DefectProbability;
            internal bool RealEnergyUsed;
            internal double IntegratedEnergy, MeanRealEnergy;
            internal string DefectType = string.Empty, DefectStatus = string.Empty, Diagnostic = string.Empty;
        }

        private sealed class Task12Result
        {
            internal bool Available, Rejected;
            internal string Type = string.Empty, Status = string.Empty;
            internal double Probability, Score;
        }

        private sealed class Task12Bridge
        {
            private readonly object engine;
            private readonly string loadStatus;

            internal Task12Bridge(string modelPath = null)
            {
                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    loadStatus = "Task12 disabled: defectModelPath is empty; external classifier was not loaded.";
                    return;
                }
                try
                {
                    string model = Path.GetFullPath(modelPath);
                    if (!File.Exists(model)) throw new FileNotFoundException("Task12 model path does not exist.", model);
                    string baseDir = Path.GetDirectoryName(typeof(MultiFactorCore).Assembly.Location);
                    List<string> directories = new List<string> {
                        Path.GetDirectoryName(model), Path.Combine(baseDir, "task12") };
                    DirectoryInfo support = new DirectoryInfo(baseDir);
                    if (string.Equals(support.Name, "Support", StringComparison.OrdinalIgnoreCase) &&
                        support.Parent != null && support.Parent.Parent != null)
                    {
                        directories.Add(Path.Combine(support.Parent.Parent.FullName,
                            "12_quexianshibiesuanfa", "Support"));
                    }
                    // Deterministic priority: explicit model folder, local
                    // task12, then the current bundle's standard sibling.
                    // Never recurse into archives or substitute another model.
                    string dll = null;
                    foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!Directory.Exists(directory)) continue;
                        string[] candidates = Directory.GetFiles(directory, "*.V1.0.12.dll");
                        if (candidates.Length > 1)
                            throw new InvalidOperationException("Task12 dependency is ambiguous in " + directory);
                        if (candidates.Length == 1) { dll = candidates[0]; break; }
                    }
                    if (string.IsNullOrEmpty(dll) || !File.Exists(dll)) throw new FileNotFoundException("Task12 DLL not found.", dll);
                    Type type = Assembly.LoadFrom(dll).GetType("PDDefect.Product.PDDefectEngine", true, false);
                    engine = Activator.CreateInstance(type, new object[] { model });
                    loadStatus = "OK";
                }
                catch (Exception ex) { loadStatus = "Task12 unavailable: " + OneLine(ex); }
            }

            internal Task12Result Classify(double[] phase, double[] charge)
            {
                Task12Result output = new Task12Result();
                if (engine == null) { output.Status = loadStatus; return output; }
                if (phase == null || charge == null || phase.Length < 3 || phase.Length != charge.Length)
                {
                    // Insufficient evidence is not a classifier rejection.
                    output.Status = "Task12 unavailable: skipped; fewer than 3 valid points or mismatched inputs.";
                    return output;
                }
                try
                {
                    MethodInfo method = engine.GetType().GetMethod("ClassifyPDData", new[] { typeof(double[]), typeof(double[]) });
                    object result = method.Invoke(engine, new object[] { phase, charge });
                    bool success = Get(result, "Success", false);
                    bool noise = Get(result, "IsNoise", false);
                    bool invalid = Get(result, "IsInvalid", false);
                    bool rejected = Get(result, "IsRejected", false) || Get(result, "Rejected", false);
                    string bestType = Get(result, "BestType", string.Empty);
                    double rawProbability = Get(result, "BestProbability", double.NaN);
                    bool validProbability = Finite(rawProbability) && rawProbability >= 0.0 && rawProbability <= 100.0;
                    double probability = Probability(rawProbability);
                    string[] keys = Get(result, "TypeKeys", new string[0]);
                    double[] probabilities = Get(result, "Probabilities", new double[0]);
                    bool validVector = keys.Length == probabilities.Length;
                    for (int i = 0; i < probabilities.Length; i++)
                        if (!Finite(probabilities[i]) || probabilities[i] < 0.0 || probabilities[i] > 100.0)
                            validVector = false;
                    string key = bestType;
                    if (keys.Length > 0 && validVector)
                    {
                        int best = 0;
                        for (int i = 1; i < probabilities.Length; i++) if (probabilities[i] > probabilities[best]) best = i;
                        key = keys[best];
                    }
                    // Current task 12 exposes the thresholded decision through
                    // Result. BestType is only an argmax (including noise), not
                    // evidence that any defect passed its per-class threshold.
                    PropertyInfo decisionProperty = result == null ? null : result.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                    bool hasDecision = decisionProperty != null;
                    bool thresholdRejected = false;
                    bool validDecisionMapping = true;
                    string confirmedType = string.IsNullOrEmpty(bestType) ? key : bestType;
                    if (hasDecision)
                    {
                        confirmedType = Get(result, "Result", string.Empty).Trim();
                        string decision = confirmedType.ToLowerInvariant();
                        thresholdRejected = string.IsNullOrWhiteSpace(confirmedType) ||
                            decision.Contains("未知") || decision.Contains("未识别") || decision.Contains("无效") ||
                            decision.Contains("噪声") || decision.Contains("失败") || decision.Contains("未达到") ||
                            decision.Contains("unknown") || decision.Contains("unrecognized") || decision.Contains("invalid") ||
                            decision.Contains("noise") || decision.Contains("fail");
                        probability = 0.0;
                        if (!thresholdRejected)
                        {
                            string[] names = Get(result, "TypeNames", new string[0]);
                            string[] passed = confirmedType.Split('+');
                            validDecisionMapping = validVector && passed.Length > 0;
                            foreach (string item in passed)
                            {
                                string token = item.Trim();
                                int match = -1;
                                if (token.Length == 0) validDecisionMapping = false;
                                for (int i = 0; i < keys.Length; i++)
                                {
                                    bool sameKey = string.Equals(token, keys[i], StringComparison.OrdinalIgnoreCase);
                                    bool sameName = names.Length == keys.Length && string.Equals(token, names[i], StringComparison.OrdinalIgnoreCase);
                                    if (!sameKey && !sameName) continue;
                                    if (match >= 0) { validDecisionMapping = false; break; }
                                    match = i;
                                }
                                if (match < 0 || match >= probabilities.Length) validDecisionMapping = false;
                                else if (validVector) probability = Math.Max(probability, Probability(probabilities[match]));
                            }
                        }
                    }
                    output.Rejected = noise || invalid || rejected || thresholdRejected;
                    output.Available = success && validProbability && validVector && validDecisionMapping;
                    output.Type = output.Available && !output.Rejected ? confirmedType : string.Empty;
                    output.Probability = output.Available && !output.Rejected ? probability : 0.0;
                    output.Score = !output.Available || output.Rejected ? 0.0 : Clamp(0.65 * probability + 0.35 * TypeRisk(key), 0.0, 1.0);
                    string status = Get(result, "Status", success ? "OK" : Get(result, "ErrorMessage", "Task12 failed."));
                    output.Status = !success ? "Task12 unavailable: execution failed; " + status
                        : (!validProbability || !validVector || !validDecisionMapping ? "Task12 unavailable: malformed probability or unmatched thresholded Result; " + status
                        : (output.Rejected ? "Task12 rejected: " + status : status));
                    return output;
                }
                catch (Exception ex)
                {
                    if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                    output.Status = "Task12 unavailable: execution error; " + OneLine(ex);
                    return output;
                }
            }

            internal void Dispose()
            {
                IDisposable disposable = engine as IDisposable;
                if (disposable == null) return;
                try { disposable.Dispose(); }
                catch { /* Optional cleanup must preserve the established conclusion. */ }
            }

            private static double TypeRisk(string key)
            {
                string value = (key ?? string.Empty).ToLowerInvariant();
                if (value.Contains("internal")) return 1.00;
                if (value.Contains("floating")) return 0.92;
                if (value.Contains("tip")) return 0.88;
                if (value.Contains("missing_stress")) return 0.86;
                if (value.Contains("surface")) return 0.82;
                if (value.Contains("wrong_tape")) return 0.78;
                return 0.65;
            }

            private static double Probability(double value)
            {
                if (!Finite(value)) return 0.0;
                if (value > 1.0) value /= 100.0;
                return Clamp(value, 0.0, 1.0);
            }

            private static T Get<T>(object target, string name, T fallback)
            {
                if (target == null) return fallback;
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null) return fallback;
                object value = property.GetValue(target, null);
                if (value == null) return fallback;
                if (value is T) return (T)value;
                return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            }

            private static string OneLine(Exception ex)
            {
                return (ex == null ? "Unknown error" : ex.Message).Replace('\r', ' ').Replace('\n', ' ').Trim();
            }
        }
    }
}
