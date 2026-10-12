using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HVX.PD.MatrixCharts.EqualBarsV114
{
    /// <summary>
    /// LabVIEW-friendly result. Each chart family is returned as a rectangular DBL array.
    /// </summary>
    public sealed class MatrixChartArrayResultV114
    {
        public MatrixChartArrayResultV114()
        {
            PositionChargeA = new double[0, 2];
            PositionChargeB = new double[0, 2];
            PositionChargeC = new double[0, 2];
            PositionCountABC = new double[0, 4];
            WidthBandwidthA = new double[0, 2];
            WidthBandwidthB = new double[0, 2];
            WidthBandwidthC = new double[0, 2];
            SequenceSummaryA = new double[0, 4];
            SequenceSummaryB = new double[0, 4];
            SequenceSummaryC = new double[0, 4];
            PressurizationTrendA = new double[0, 6];
            PressurizationTrendB = new double[0, 6];
            PressurizationTrendC = new double[0, 6];
            PhaseChargeA = new double[0, 2];
            PhaseChargeB = new double[0, 2];
            PhaseChargeC = new double[0, 2];
            PhaseCountHistogramABC = new double[0, 4];
            PrpdA = new double[0, 3];
            PrpdB = new double[0, 3];
            PrpdC = new double[0, 3];
            PrpdDensityGridA = new double[0, 3];
            PrpdDensityGridB = new double[0, 3];
            PrpdDensityGridC = new double[0, 3];
            FilteredLocationMatrix = new double[0, 0];
            DiagnosticsJson = "{}";
        }

        public double[,] PositionChargeA { get; internal set; }
        public double[,] PositionChargeB { get; internal set; }
        public double[,] PositionChargeC { get; internal set; }
        public double[,] PositionCountABC { get; internal set; }
        public double[,] DischargeCountPositionHistogramABC { get { return PositionCountABC; } }
        public int HistogramBinCount { get; internal set; }
        public double HistogramBinWidthM { get; internal set; }
        public double[,] WidthBandwidthA { get; internal set; }
        public double[,] WidthBandwidthB { get; internal set; }
        public double[,] WidthBandwidthC { get; internal set; }
        public double[,] SequenceSummaryA { get; internal set; }
        public double[,] SequenceSummaryB { get; internal set; }
        public double[,] SequenceSummaryC { get; internal set; }
        public double[,] PressurizationTrendA { get; internal set; }
        public double[,] PressurizationTrendB { get; internal set; }
        public double[,] PressurizationTrendC { get; internal set; }
        public double[,] PhaseChargeA { get; internal set; }
        public double[,] PhaseChargeB { get; internal set; }
        public double[,] PhaseChargeC { get; internal set; }
        public double[,] PhaseCountHistogramABC { get; internal set; }
        public int PhaseHistogramBinCount { get; internal set; }
        public double PhaseHistogramBinWidthDegrees { get; internal set; }
        public double[,] PrpdA { get; internal set; }
        public double[,] PrpdB { get; internal set; }
        public double[,] PrpdC { get; internal set; }
        public double[,] PrpdDensityGridA { get; internal set; }
        public double[,] PrpdDensityGridB { get; internal set; }
        public double[,] PrpdDensityGridC { get; internal set; }
        public double PrpdAmplitudeReferencePcA { get; internal set; }
        public double PrpdAmplitudeReferencePcB { get; internal set; }
        public double PrpdAmplitudeReferencePcC { get; internal set; }
        public double PrpdDensityScaleMinimum { get { return -3.0; } }
        public double PrpdDensityScaleMaximum { get { return 0.0; } }
        public int PrpdPhaseBinCount { get; internal set; }
        public int PrpdChargeBinCount { get; internal set; }
        public double[,] FilteredLocationMatrix { get; internal set; }
        public int FilteredMatrixRows { get { return FilteredLocationMatrix == null ? 0 : FilteredLocationMatrix.GetLength(0); } }
        public int FilteredMatrixColumns { get { return FilteredLocationMatrix == null ? 0 : FilteredLocationMatrix.GetLength(1); } }
        public string DiagnosticsJson { get; internal set; }
    }

    /// <summary>
    /// Builds all A/B/C chart arrays directly from the 36/37-column location matrix.
    /// Public names and signatures are kept stable for LabVIEW .NET nodes.
    /// </summary>
    public sealed class MatrixChartArrayBuilderV114
    {
        private const int MinimumColumns = 36;

        /// <summary>
        /// Main LabVIEW method. Seven numeric filter ports are one-dimensional DBL arrays; userSelectionFilter is I32.
        /// count/charge/voltage/position/bandwidth/pulse-width use [lower, upper]. Phase uses [a0,a1,b0,b1].
        /// Empty, all-zero, incomplete or invalid arrays disable that filter.
        /// </summary>
        private MatrixChartArrayResultV114 Build(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            int userSelectionFilter)
        {
            return BuildConfigured(
                locationMatrix,
                dischargeCountFilter,
                dischargeChargeFilterPc,
                dischargeVoltageFilterKv,
                dischargePositionFilterM,
                equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs,
                phaseFilterDegrees,
                0.0,
                userSelectionFilter);
        }

        /// <summary>
        /// Extended LabVIEW method. minimumDischargeProbability reads matrix column index 33.
        /// A value of 0 disables probability filtering; a positive value removes pulses below
        /// that confidence score. userSelectionFilter retains the existing index-35 semantics.
        /// </summary>
        private MatrixChartArrayResultV114 Build(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            int userSelectionFilter,
            double minimumDischargeProbability)
        {
            return BuildConfigured(locationMatrix, dischargeCountFilter, dischargeChargeFilterPc,
                dischargeVoltageFilterKv, dischargePositionFilterM, equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs, phaseFilterDegrees, 0.0, userSelectionFilter,
                minimumDischargeProbability);
        }

        /// <summary>
        /// Boolean acceptance overload retained for LabVIEW. false keeps only index-35 accepted
        /// rows; true includes accepted and unaccepted rows. Probability filtering is disabled.
        /// </summary>
        private MatrixChartArrayResultV114 Build(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            bool includeUnacceptedRows)
        {
            return Build(locationMatrix, dischargeCountFilter, dischargeChargeFilterPc,
                dischargeVoltageFilterKv, dischargePositionFilterM, equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs, phaseFilterDegrees, includeUnacceptedRows, 0.0);
        }

        /// <summary>
        /// Recommended LabVIEW overload: Boolean index-35 acceptance control plus the index-33
        /// minimum confidence-score threshold (not a calibrated probability). Use 0 to disable probability filtering.
        /// </summary>
        public MatrixChartArrayResultV114 Build(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            bool includeUnacceptedRows,
            double minimumDischargeProbability)
        {
            return BuildConfigured(locationMatrix, dischargeCountFilter, dischargeChargeFilterPc,
                dischargeVoltageFilterKv, dischargePositionFilterM, equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs, phaseFilterDegrees, 0.0, includeUnacceptedRows,
                minimumDischargeProbability);
        }

        /// <summary>
        /// Configured variant. positionBinWidthM <= 0 lets the algorithm recommend the bin count.
        /// A positive requested width is clamped so no bar is narrower than 1% of cable length.
        /// </summary>
        private MatrixChartArrayResultV114 BuildConfigured(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            double positionBinWidthM,
            int userSelectionFilter)
        {
            if (userSelectionFilter != 0 && userSelectionFilter != 1)
                throw new ArgumentOutOfRangeException("userSelectionFilter", "userSelectionFilter must be 0 or 1.");
            return BuildConfiguredCore(locationMatrix, dischargeCountFilter, dischargeChargeFilterPc,
                dischargeVoltageFilterKv, dischargePositionFilterM, equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs, phaseFilterDegrees, positionBinWidthM,
                userSelectionFilter, userSelectionFilter == 1, 0.0, false);
        }

        /// <summary>
        /// Extended configured overload with the matrix-index-33 minimum probability filter.
        /// </summary>
        private MatrixChartArrayResultV114 BuildConfigured(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            double positionBinWidthM,
            int userSelectionFilter,
            double minimumDischargeProbability)
        {
            if (userSelectionFilter != 0 && userSelectionFilter != 1)
                throw new ArgumentOutOfRangeException("userSelectionFilter", "userSelectionFilter must be 0 or 1.");
            if (!IsFinite(minimumDischargeProbability) || minimumDischargeProbability < 0.0)
                throw new ArgumentOutOfRangeException("minimumDischargeProbability", "minimumDischargeProbability must be finite and non-negative; use 0 to disable.");
            return BuildConfiguredCore(locationMatrix, dischargeCountFilter, dischargeChargeFilterPc,
                dischargeVoltageFilterKv, dischargePositionFilterM, equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs, phaseFilterDegrees, positionBinWidthM,
                userSelectionFilter, userSelectionFilter == 1, minimumDischargeProbability, false);
        }

        private MatrixChartArrayResultV114 BuildConfigured(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            double positionBinWidthM,
            bool includeUnacceptedRows)
        {
            return BuildConfigured(locationMatrix, dischargeCountFilter, dischargeChargeFilterPc,
                dischargeVoltageFilterKv, dischargePositionFilterM, equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs, phaseFilterDegrees, positionBinWidthM,
                includeUnacceptedRows, 0.0);
        }

        public MatrixChartArrayResultV114 BuildConfigured(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            double positionBinWidthM,
            bool includeUnacceptedRows,
            double minimumDischargeProbability)
        {
            if (!IsFinite(minimumDischargeProbability) || minimumDischargeProbability < 0.0)
                throw new ArgumentOutOfRangeException("minimumDischargeProbability", "minimumDischargeProbability must be finite and non-negative; use 0 to disable.");
            return BuildConfiguredCore(locationMatrix, dischargeCountFilter, dischargeChargeFilterPc,
                dischargeVoltageFilterKv, dischargePositionFilterM, equivalentBandwidthFilterHz,
                equivalentPulseWidthFilterNs, phaseFilterDegrees, positionBinWidthM,
                includeUnacceptedRows ? 0 : 1, !includeUnacceptedRows,
                minimumDischargeProbability, true);
        }

        private MatrixChartArrayResultV114 BuildConfiguredCore(
            double[,] locationMatrix,
            double[] dischargeCountFilter,
            double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv,
            double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz,
            double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees,
            double positionBinWidthM,
            int userSelectionFilter,
            bool userSelectionFilterEnabled,
            double minimumDischargeProbability,
            bool booleanSelectionPort)
        {
            if (locationMatrix == null) throw new ArgumentNullException("locationMatrix");
            int rowCount = locationMatrix.GetLength(0);
            int columnCount = locationMatrix.GetLength(1);
            if (columnCount < MinimumColumns)
                throw new ArgumentException("locationMatrix must have at least 36 columns.", "locationMatrix");
            NumericRange countRange = NumericRange.FromPair(dischargeCountFilter);
            NumericRange chargeRange = NumericRange.FromPair(dischargeChargeFilterPc);
            NumericRange voltageRange = NumericRange.FromPair(dischargeVoltageFilterKv);
            NumericRange positionRange = NumericRange.FromPair(dischargePositionFilterM);
            NumericRange bandwidthRange = NumericRange.FromPair(equivalentBandwidthFilterHz);
            NumericRange pulseWidthRange = NumericRange.FromPair(equivalentPulseWidthFilterNs);
            PhaseRanges phaseRanges = PhaseRanges.FromArray(phaseFilterDegrees);

            var positionCharge = NewPhaseLists();
            var widthBandwidth = NewPhaseLists();
            var phaseCharge = NewPhaseLists();
            var acceptedPositionRows = NewPhaseLists();
            var sequenceGroups = NewSequenceGroups();
            var stableOrderIds = new List<double>();
            var acceptedSourceRows = new List<int>();
            var diagnostics = new Diagnostics(rowCount, columnCount);
            diagnostics.CountFilterEnabled = countRange.Enabled;
            diagnostics.UserSelectionFilterMode = userSelectionFilter;
            diagnostics.UserSelectionFilterEnabled = userSelectionFilterEnabled;
            diagnostics.ProbabilityFilterEnabled = minimumDischargeProbability > 0.0;
            diagnostics.MinimumDischargeProbability = minimumDischargeProbability;
            diagnostics.BooleanSelectionPort = booleanSelectionPort;
            diagnostics.IncludeUnacceptedRows = !userSelectionFilterEnabled;
            diagnostics.ChargeFilterEnabled = chargeRange.Enabled;
            diagnostics.VoltageFilterEnabled = voltageRange.Enabled;
            diagnostics.PositionFilterEnabled = positionRange.Enabled;
            diagnostics.BandwidthFilterEnabled = bandwidthRange.Enabled;
            diagnostics.PulseWidthFilterEnabled = pulseWidthRange.Enabled;
            diagnostics.PhaseFilterEnabled = phaseRanges.Enabled;

            double cableLengthM = 0.0;
            for (int r = 0; r < rowCount; r++)
            {
                double candidateLength = locationMatrix[r, 5];
                if (IsFinite(candidateLength) && candidateLength > cableLengthM) cableLengthM = candidateLength;
            }

            for (int row = 0; row < rowCount; row++)
            {
                if (minimumDischargeProbability > 0.0 &&
                    (!IsFinite(locationMatrix[row, 33]) || locationMatrix[row, 33] < minimumDischargeProbability))
                {
                    diagnostics.RejectedProbability++;
                    continue;
                }
                if (userSelectionFilterEnabled && (!IsFinite(locationMatrix[row, 35]) || Math.Abs(locationMatrix[row, 35] - 1.0) > 1e-9))
                {
                    diagnostics.RejectedUserSelection++;
                    continue;
                }

                int phase = DecodePhase(locationMatrix[row, 0]);
                if (phase == 0)
                {
                    diagnostics.RejectedInvalidPhase++;
                    continue;
                }

                double widthNs = locationMatrix[row, 23];
                double bandwidthHz = locationMatrix[row, 24];
                if (bandwidthRange.Enabled && (!IsFinite(bandwidthHz) || !bandwidthRange.Contains(bandwidthHz)))
                {
                    diagnostics.RejectedBandwidth++;
                    continue;
                }
                if (pulseWidthRange.Enabled && (!IsFinite(widthNs) || !pulseWidthRange.Contains(widthNs)))
                {
                    diagnostics.RejectedPulseWidth++;
                    continue;
                }

                double voltageKv = locationMatrix[row, 2];
                if (voltageRange.Enabled && (!IsFinite(voltageKv) || !voltageRange.Contains(voltageKv)))
                {
                    diagnostics.RejectedVoltage++;
                    continue;
                }

                double positionM = locationMatrix[row, 13];
                if (positionRange.Enabled && (!IsFinite(positionM) || !positionRange.Contains(positionM)))
                {
                    diagnostics.RejectedPositionFilter++;
                    continue;
                }

                double phaseDeg = locationMatrix[row, 3];
                if (phaseRanges.Enabled && (!IsFinite(phaseDeg) || !phaseRanges.Contains(phaseDeg)))
                {
                    diagnostics.RejectedPhase++;
                    continue;
                }

                double chargePc;
                bool hasCharge = TryResolveCharge(locationMatrix[row, 15], locationMatrix[row, 16], out chargePc);
                if (chargeRange.Enabled && (!hasCharge || !chargeRange.Contains(chargePc)))
                {
                    diagnostics.RejectedCharge++;
                    continue;
                }

                diagnostics.AcceptedRows++;
                acceptedSourceRows.Add(row);
                if (hasCharge && IsFinite(phaseDeg))
                    phaseCharge[phase].Add(new double[] { NormalizePhase(phaseDeg), chargePc });
               bool positionValid = IsFinite(positionM) && positionM >= 0.0;
                if (positionValid && cableLengthM > 0.0 && positionM > cableLengthM) positionValid = false;
                if (positionValid)
                {
                    acceptedPositionRows[phase].Add(new double[] { positionM, 1.0 });
                    if (positionM > cableLengthM) cableLengthM = positionM;
                    if (hasCharge) positionCharge[phase].Add(new double[] { positionM, chargePc });
                }
                else
                {
                    diagnostics.RejectedInvalidPosition++;
                }
                if (IsFinite(widthNs) && widthNs > 0.0 && IsFinite(bandwidthHz) && bandwidthHz > 0.0)
                    widthBandwidth[phase].Add(new double[] { widthNs, bandwidthHz });
                else
                    diagnostics.RejectedInvalidFingerprint++;

                if (hasCharge && IsFinite(voltageKv))
                {
                    int order = ResolveOrder(locationMatrix, row, columnCount, stableOrderIds, diagnostics);
                    if (order > 0)
                    {
                        SequenceAggregate aggregate = FindOrCreateAggregate(sequenceGroups[phase], order);
                        aggregate.Add(chargePc, voltageKv);
                    }
                }
            }

            var result = new MatrixChartArrayResultV114();
            result.FilteredLocationMatrix = CopyRows(locationMatrix, acceptedSourceRows, columnCount);
            result.PositionChargeA = ToMatrix(SortByFirst(positionCharge[1]), 2);
            result.PositionChargeB = ToMatrix(SortByFirst(positionCharge[2]), 2);
            result.PositionChargeC = ToMatrix(SortByFirst(positionCharge[3]), 2);
            result.WidthBandwidthA = ToMatrix(widthBandwidth[1], 2);
            result.WidthBandwidthB = ToMatrix(widthBandwidth[2], 2);
            result.WidthBandwidthC = ToMatrix(widthBandwidth[3], 2);
            result.PositionCountABC = BuildPositionCounts(
                acceptedPositionRows, cableLengthM, positionBinWidthM, countRange, diagnostics);
            result.HistogramBinCount = diagnostics.PositionBinCountBeforeCountFilter;
            result.HistogramBinWidthM = diagnostics.BinWidthM;
            result.SequenceSummaryA = BuildSequenceSummary(sequenceGroups[1]);
            result.SequenceSummaryB = BuildSequenceSummary(sequenceGroups[2]);
            result.SequenceSummaryC = BuildSequenceSummary(sequenceGroups[3]);
            result.PressurizationTrendA = BuildPressurizationTrend(sequenceGroups[1]);
            result.PressurizationTrendB = BuildPressurizationTrend(sequenceGroups[2]);
            result.PressurizationTrendC = BuildPressurizationTrend(sequenceGroups[3]);
            result.PhaseChargeA = ToMatrix(SortByFirst(phaseCharge[1]), 2);
            result.PhaseChargeB = ToMatrix(SortByFirst(phaseCharge[2]), 2);
            result.PhaseChargeC = ToMatrix(SortByFirst(phaseCharge[3]), 2);
            BuildPhaseAndPrpd(phaseCharge, result);
            diagnostics.CableLengthM = cableLengthM;
            diagnostics.PositionChargeRows = new int[] {
                result.PositionChargeA.GetLength(0), result.PositionChargeB.GetLength(0), result.PositionChargeC.GetLength(0) };
            diagnostics.PositionCountRows = result.PositionCountABC.GetLength(0);
            diagnostics.FingerprintRows = new int[] {
                result.WidthBandwidthA.GetLength(0), result.WidthBandwidthB.GetLength(0), result.WidthBandwidthC.GetLength(0) };
            diagnostics.SequenceRows = new int[] {
                result.SequenceSummaryA.GetLength(0), result.SequenceSummaryB.GetLength(0), result.SequenceSummaryC.GetLength(0) };
            result.DiagnosticsJson = diagnostics.ToJson();
            return result;
        }

        private static List<double[]>[] NewPhaseLists()
        {
            return new List<double[]>[] { new List<double[]>(), new List<double[]>(),
                new List<double[]>(), new List<double[]>() };
        }

        private static List<SequenceAggregate>[] NewSequenceGroups()
        {
            return new List<SequenceAggregate>[] { new List<SequenceAggregate>(),
                new List<SequenceAggregate>(), new List<SequenceAggregate>(), new List<SequenceAggregate>() };
        }

        private static SequenceAggregate FindOrCreateAggregate(List<SequenceAggregate> groups, int order)
        {
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Order == order) return groups[i];
            var created = new SequenceAggregate(order);
            groups.Add(created);
            return created;
        }
        private static int DecodePhase(double value)
        {
            if (!IsFinite(value)) return 0;
            int phase = (int)Math.Round(value);
            return phase >= 1 && phase <= 3 && Math.Abs(value - phase) < 1e-9 ? phase : 0;
        }

        private static bool TryResolveCharge(double rawPc, double compensatedPc, out double chargePc)
        {
            if (IsFinite(compensatedPc))
            {
                chargePc = Math.Abs(compensatedPc);
                return true;
            }
            if (IsFinite(rawPc))
            {
                chargePc = Math.Abs(rawPc);
                return true;
            }
            chargePc = double.NaN;
            return false;
        }

        private static int ResolveOrder(
            double[,] matrix, int row, int columnCount, List<double> stableOrderIds, Diagnostics diagnostics)
        {
            if (columnCount >= 37)
            {
                double explicitValue = matrix[row, 36];
                if (IsFinite(explicitValue) && explicitValue > 0.0)
                {
                    diagnostics.ExplicitOrderRows++;
                    return (int)Math.Round(explicitValue);
                }
            }
            double id = matrix[row, 1];
            if (!IsFinite(id))
            {
                diagnostics.RowsWithoutOrder++;
                return 0;
            }
            for (int i = 0; i < stableOrderIds.Count; i++)
            {
                if (stableOrderIds[i].Equals(id))
                {
                    diagnostics.DerivedOrderRows++;
                    return i + 1;
                }
            }
            stableOrderIds.Add(id);
            diagnostics.DerivedOrderRows++;
            return stableOrderIds.Count;
        }

        private static double[,] BuildPositionCounts(
            List<double[]>[] positions,
            double cableLengthM,
            double requestedBinWidthM,
            NumericRange countRange,
            Diagnostics diagnostics)
        {
            double maximumPosition = 0.0;
            for (int phase = 1; phase <= 3; phase++)
                foreach (double[] item in positions[phase])
                    if (item[0] > maximumPosition) maximumPosition = item[0];
            double length = Math.Max(cableLengthM, maximumPosition);
            if (!(length > 0.0)) return new double[0, 4];
            int validPointCount = positions[1].Count + positions[2].Count + positions[3].Count;
            if (validPointCount == 0) return new double[0, 4];
            double minimumBinWidthM = length * 0.01;
            bool automatic = !IsFinite(requestedBinWidthM) || requestedBinWidthM <= 0.0;
            int binCount;
            if (automatic)
                binCount = RecommendHistogramBinCount(validPointCount);
            else
            {
                double safeRequestedWidthM = Math.Max(requestedBinWidthM, minimumBinWidthM);
                binCount = Math.Max(1, Math.Min(100, (int)Math.Ceiling(length / safeRequestedWidthM)));
            }
            double binWidthM = length / binCount;
            diagnostics.BinWidthM = binWidthM;
            diagnostics.MinimumAllowedBinWidthM = minimumBinWidthM;
            diagnostics.HistogramAutomatic = automatic;
            diagnostics.ValidHistogramPointCount = validPointCount;
            var counts = new double[3, binCount];
            for (int phase = 1; phase <= 3; phase++)
            {
                foreach (double[] item in positions[phase])
                {
                    int bin = item[0] >= length ? binCount - 1 : (int)Math.Floor(item[0] / binWidthM);
                    if (bin >= 0 && bin < binCount) counts[phase - 1, bin] += 1.0;
                }
            }

            var rows = new List<double[]>();
            for (int bin = 0; bin < binCount; bin++)
            {
                double a = FilterCount(counts[0, bin], countRange, diagnostics);
                double b = FilterCount(counts[1, bin], countRange, diagnostics);
                double c = FilterCount(counts[2, bin], countRange, diagnostics);
                double left = bin * binWidthM;
                double right = Math.Min(length, (bin + 1) * binWidthM);
                rows.Add(new double[] { (left + right) / 2.0, a, b, c });
            }
            diagnostics.PositionBinCountBeforeCountFilter = binCount;
            return ToMatrix(rows, 4);
        }

        private static int RecommendHistogramBinCount(int validPointCount)
        {
            int recommended = (int)Math.Ceiling(2.5 * Math.Sqrt(Math.Max(1, validPointCount)));
            return Math.Max(4, Math.Min(100, recommended));
        }

        private static double FilterCount(double count, NumericRange range, Diagnostics diagnostics)
        {
            if (!range.Enabled || range.Contains(count)) return count;
            if (count > 0.0) diagnostics.RejectedCountBins++;
            return 0.0;
        }

        private static double[,] BuildSequenceSummary(List<SequenceAggregate> groups)
        {
            var rows = new List<double[]>();
            groups.Sort(delegate(SequenceAggregate left, SequenceAggregate right) { return left.Order.CompareTo(right.Order); });
            foreach (SequenceAggregate item in groups)
            {
                rows.Add(new double[] { item.Order, item.MaximumChargePc, item.MeanVoltageKv, item.Count });
            }
            return ToMatrix(rows, 4);
        }

        private static double[,] BuildPressurizationTrend(List<SequenceAggregate> groups)
        {
            var rows = new List<double[]>();
            groups.Sort(delegate(SequenceAggregate left, SequenceAggregate right) { return left.Order.CompareTo(right.Order); });
            foreach (SequenceAggregate item in groups)
            {
                rows.Add(new double[] { item.Order, item.MeanVoltageKv, item.MaximumChargePc,
                    item.MeanChargePc, item.MinimumChargePc, item.Count });
            }
            return ToMatrix(rows, 6);
        }

        private static void BuildPhaseAndPrpd(
            List<double[]>[] phaseCharge, MatrixChartArrayResultV114 result)
        {
            int pointCount = phaseCharge[1].Count + phaseCharge[2].Count + phaseCharge[3].Count;
            if (pointCount == 0) return;
            int phaseBins = Math.Max(18, Math.Min(72, RecommendHistogramBinCount(pointCount)));
            double phaseWidth = 360.0 / phaseBins;
            var phaseCounts = new double[3, phaseBins];
            for (int phase = 1; phase <= 3; phase++)
                foreach (double[] point in phaseCharge[phase])
                {
                    int bin = Math.Min(phaseBins - 1, (int)Math.Floor(point[0] / phaseWidth));
                    phaseCounts[phase - 1, bin] += 1.0;
                }
            var phaseRows = new List<double[]>();
            for (int bin = 0; bin < phaseBins; bin++)
                phaseRows.Add(new double[] { (bin + 0.5) * phaseWidth,
                    phaseCounts[0, bin], phaseCounts[1, bin], phaseCounts[2, bin] });
            result.PhaseCountHistogramABC = ToMatrix(phaseRows, 4);
            result.PhaseHistogramBinCount = phaseBins;
            result.PhaseHistogramBinWidthDegrees = phaseWidth;

            int chargeBins = Math.Max(8, Math.Min(64,
                (int)Math.Ceiling(2.0 * Math.Sqrt(Math.Max(1, pointCount)))));
            result.PrpdPhaseBinCount = phaseBins;
            result.PrpdChargeBinCount = chargeBins;
            double[,] gridA, gridB, gridC;
            double referenceA, referenceB, referenceC;
            result.PrpdA = BuildPrpdPulsePoints(phaseCharge[1], phaseBins, chargeBins, out gridA, out referenceA);
            result.PrpdB = BuildPrpdPulsePoints(phaseCharge[2], phaseBins, chargeBins, out gridB, out referenceB);
            result.PrpdC = BuildPrpdPulsePoints(phaseCharge[3], phaseBins, chargeBins, out gridC, out referenceC);
            result.PrpdDensityGridA = gridA; result.PrpdDensityGridB = gridB; result.PrpdDensityGridC = gridC;
            result.PrpdAmplitudeReferencePcA = referenceA;
            result.PrpdAmplitudeReferencePcB = referenceB;
            result.PrpdAmplitudeReferencePcC = referenceC;
        }

        private static double[,] BuildPrpdPulsePoints(List<double[]> points, int phaseBins, int amplitudeBins,
            out double[,] densityGrid, out double amplitudeReferencePc)
        {
            amplitudeReferencePc = 0.0;
            for (int i = 0; i < points.Count; i++)
                if (points[i][1] > amplitudeReferencePc) amplitudeReferencePc = points[i][1];
            if (!(amplitudeReferencePc > 0.0)) amplitudeReferencePc = 1.0;
            double phaseWidth = 360.0 / phaseBins;
            double amplitudeWidth = 100.0 / amplitudeBins;
            var counts = new int[phaseBins, amplitudeBins];
            int maximumCellCount = 0;
            for (int i = 0; i < points.Count; i++)
            {
                double relativeAmplitude = Math.Max(0.0, Math.Min(100.0,
                    points[i][1] / amplitudeReferencePc * 100.0));
                int phaseBin = Math.Min(phaseBins - 1, (int)Math.Floor(points[i][0] / phaseWidth));
                int amplitudeBin = relativeAmplitude >= 100.0 ? amplitudeBins - 1
                    : Math.Max(0, (int)Math.Floor(relativeAmplitude / amplitudeWidth));
                counts[phaseBin, amplitudeBin]++;
                if (counts[phaseBin, amplitudeBin] > maximumCellCount)
                    maximumCellCount = counts[phaseBin, amplitudeBin];
            }
            if (maximumCellCount < 1) maximumCellCount = 1;
            var pulseRows = new List<double[]>();
            for (int i = 0; i < points.Count; i++)
            {
                double relativeAmplitude = Math.Max(0.0, Math.Min(100.0,
                    points[i][1] / amplitudeReferencePc * 100.0));
                int phaseBin = Math.Min(phaseBins - 1, (int)Math.Floor(points[i][0] / phaseWidth));
                int amplitudeBin = relativeAmplitude >= 100.0 ? amplitudeBins - 1
                    : Math.Max(0, (int)Math.Floor(relativeAmplitude / amplitudeWidth));
                double logDensity = Math.Log10((double)counts[phaseBin, amplitudeBin] / maximumCellCount);
                if (logDensity < -3.0) logDensity = -3.0;
                if (logDensity > 0.0) logDensity = 0.0;
                pulseRows.Add(new double[] { points[i][0], relativeAmplitude, logDensity });
            }
            var gridRows = new List<double[]>();
            for (int p = 0; p < phaseBins; p++)
                for (int a = 0; a < amplitudeBins; a++)
                    if (counts[p, a] > 0)
                        gridRows.Add(new double[] { (p + 0.5) * phaseWidth,
                            (a + 0.5) * amplitudeWidth, counts[p, a] });
            densityGrid = ToMatrix(gridRows, 3);
            return ToMatrix(pulseRows, 3);
        }

        private static double NormalizePhase(double phaseDegrees)
        {
            double normalized = phaseDegrees % 360.0;
            if (normalized < 0.0) normalized += 360.0;
            return normalized;
        }
        private static List<double[]> SortByFirst(List<double[]> values)
        {
            values.Sort(delegate(double[] left, double[] right) { return left[0].CompareTo(right[0]); });
            return values;
        }

        private static double[,] ToMatrix(List<double[]> rows, int columns)
        {
            var result = new double[rows.Count, columns];
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < columns; c++)
                    result[r, c] = rows[r][c];
            return result;
        }

        private static double[,] CopyRows(double[,] source, List<int> rowIndexes, int columns)
        {
            var result = new double[rowIndexes.Count, columns];
            for (int r = 0; r < rowIndexes.Count; r++)
                for (int c = 0; c < columns; c++)
                    result[r, c] = source[rowIndexes[r], c];
            return result;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private sealed class NumericRange
        {
            internal bool Enabled;
            internal double Lower;
            internal double Upper;

            internal static NumericRange FromPair(double[] values)
            {
                var result = new NumericRange();
                if (values == null || values.Length < 2) return result;
                if (!IsFinite(values[0]) || !IsFinite(values[1])) return result;
                if (values[0] == values[1]) return result;
                result.Enabled = true;
                result.Lower = Math.Min(values[0], values[1]);
                result.Upper = Math.Max(values[0], values[1]);
                return result;
            }

            internal bool Contains(double value)
            {
                return value >= Lower && value <= Upper;
            }
        }

        private sealed class PhaseRanges
        {
            internal bool Enabled;
            internal NumericRange First;
            internal NumericRange Second;

            internal static PhaseRanges FromArray(double[] values)
            {
                var result = new PhaseRanges();
                if (values == null || values.Length == 0 || values.Length % 2 != 0) return result;

                if (values.Length < 2) return result;
                int used = Math.Min(values.Length, 4);
                for (int i = 0; i < used; i++) if (!IsFinite(values[i])) return result;
                result.First = NumericRange.FromPair(new double[] { values[0], values[1] });
                if (values.Length >= 4)
                    result.Second = NumericRange.FromPair(new double[] { values[2], values[3] });
                result.Enabled = (result.First != null && result.First.Enabled)
                    || (result.Second != null && result.Second.Enabled);
                return result;
            }

            internal bool Contains(double value)
            {
                return (First != null && First.Enabled && First.Contains(value))
                    || (Second != null && Second.Enabled && Second.Contains(value));
            }
        }

        private sealed class SequenceAggregate
        {
            internal readonly int Order;
            internal int Count;
            internal double MaximumChargePc;
            internal double MinimumChargePc;
            private double _chargeSum;
            private double _voltageSum;

            internal SequenceAggregate(int order) { Order = order; }
            internal void Add(double chargePc, double voltageKv)
            {
                Count++;
                if (Count == 1 || chargePc > MaximumChargePc) MaximumChargePc = chargePc;
                if (Count == 1 || chargePc < MinimumChargePc) MinimumChargePc = chargePc;
                _chargeSum += chargePc;
                _voltageSum += voltageKv;
            }
            internal double MeanVoltageKv { get { return Count == 0 ? 0.0 : _voltageSum / Count; } }
            internal double MeanChargePc { get { return Count == 0 ? 0.0 : _chargeSum / Count; } }
        }

        private sealed class Diagnostics
        {
            internal readonly int InputRows;
            internal readonly int InputColumns;
            internal double BinWidthM;
            internal double MinimumAllowedBinWidthM;
            internal bool HistogramAutomatic;
            internal int ValidHistogramPointCount;
            internal bool CountFilterEnabled, ChargeFilterEnabled, VoltageFilterEnabled, PositionFilterEnabled, BandwidthFilterEnabled, PulseWidthFilterEnabled, PhaseFilterEnabled, UserSelectionFilterEnabled;
            internal bool ProbabilityFilterEnabled;
            internal bool BooleanSelectionPort, IncludeUnacceptedRows;
            internal double MinimumDischargeProbability;
            internal int UserSelectionFilterMode;
            internal int AcceptedRows, RejectedUserSelection, RejectedProbability, RejectedInvalidPhase, RejectedVoltage, RejectedPositionFilter, RejectedBandwidth, RejectedPulseWidth, RejectedPhase, RejectedCharge;
            internal int RejectedInvalidPosition, RejectedInvalidFingerprint, RejectedCountBins;
            internal int ExplicitOrderRows, DerivedOrderRows, RowsWithoutOrder;
            internal int PositionBinCountBeforeCountFilter, PositionCountRows;
            internal double CableLengthM;
            internal int[] PositionChargeRows, FingerprintRows, SequenceRows;

            internal Diagnostics(int rows, int columns)
            { InputRows = rows; InputColumns = columns; }

            internal string ToJson()
            {
                var b = new StringBuilder();
                b.Append('{');
                Add(b, "ok", true); Add(b, "api_version", "hvx-pd-matrix-charts-equal-bars-dotnet-1.1.5");
                Add(b, "input_rows", InputRows); Add(b, "input_columns", InputColumns);
                Add(b, "accepted_rows", AcceptedRows); Add(b, "cable_length_m", CableLengthM);
                Add(b, "position_bin_width_m", BinWidthM);
                Add(b, "histogram_bin_count_source", HistogramAutomatic ? "automatic" : "configured_clamped");
                Add(b, "minimum_allowed_bin_width_m", MinimumAllowedBinWidthM);
                Add(b, "valid_histogram_point_count", ValidHistogramPointCount);
                Add(b, "histogram_zero_bins_preserved", true);
                Add(b, "count_filter_enabled", CountFilterEnabled); Add(b, "charge_filter_enabled", ChargeFilterEnabled);
                Add(b, "voltage_filter_enabled", VoltageFilterEnabled); Add(b, "position_filter_enabled", PositionFilterEnabled);
                Add(b, "bandwidth_filter_enabled", BandwidthFilterEnabled); Add(b, "pulse_width_filter_enabled", PulseWidthFilterEnabled);
                Add(b, "phase_filter_enabled", PhaseFilterEnabled);
                Add(b, "rejected_invalid_phase", RejectedInvalidPhase); Add(b, "rejected_voltage", RejectedVoltage);
                Add(b, "rejected_position_filter", RejectedPositionFilter);
                Add(b, "rejected_bandwidth", RejectedBandwidth); Add(b, "rejected_pulse_width", RejectedPulseWidth);
                Add(b, "user_selection_filter_mode", UserSelectionFilterMode);
                Add(b, "user_selection_filter_enabled", UserSelectionFilterEnabled);
                Add(b, "user_selection_column_index", 35);
                Add(b, "boolean_selection_port", BooleanSelectionPort);
                Add(b, "include_unaccepted_rows", IncludeUnacceptedRows);
                Add(b, "probability_filter_enabled", ProbabilityFilterEnabled);
                Add(b, "minimum_discharge_probability", MinimumDischargeProbability);
                Add(b, "probability_column_index", 33);
                Add(b, "rejected_invalid_position", RejectedInvalidPosition);
                Add(b, "rejected_user_selection", RejectedUserSelection);
                Add(b, "rejected_probability", RejectedProbability);
                Add(b, "rejected_invalid_fingerprint", RejectedInvalidFingerprint);
                Add(b, "rejected_count_bins", RejectedCountBins);
                Add(b, "explicit_order_rows", ExplicitOrderRows); Add(b, "derived_order_rows", DerivedOrderRows);
                Add(b, "rows_without_order", RowsWithoutOrder);
                Add(b, "position_bins_before_count_filter", PositionBinCountBeforeCountFilter);
                Add(b, "position_count_rows", PositionCountRows);
                AddArray(b, "position_charge_rows_abc", PositionChargeRows);
                AddArray(b, "fingerprint_rows_abc", FingerprintRows);
                AddArray(b, "sequence_rows_abc", SequenceRows);
                if (b[b.Length - 1] == ',') b.Length--;
                b.Append('}');
                return b.ToString();
            }

            private static void Add(StringBuilder b, string name, string value)
            { b.Append('"').Append(name).Append("\":\"").Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\","); }
            private static void Add(StringBuilder b, string name, bool value)
            { b.Append('"').Append(name).Append("\":").Append(value ? "true" : "false").Append(','); }
            private static void Add(StringBuilder b, string name, int value)
            { b.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture)).Append(','); }
            private static void Add(StringBuilder b, string name, double value)
            { b.Append('"').Append(name).Append("\":").Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(','); }
            private static void AddArray(StringBuilder b, string name, int[] values)
            {
                b.Append('"').Append(name).Append("\":[");
                if (values != null) for (int i = 0; i < values.Length; i++)
                { if (i > 0) b.Append(','); b.Append(values[i].ToString(CultureInfo.InvariantCulture)); }
                b.Append("],");
            }
        }
    }
}


