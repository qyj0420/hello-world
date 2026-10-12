using System;
using System.ComponentModel;
using System.Linq;
using HVX.PD.MatrixCharts.EqualBarsV114;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool statisticsEnabled;
        double statisticsStrength;
        bool includeUnacceptedRows = true;
        bool[] reviewMask = new bool[0];
        double[] countFilter = new double[0], chargeFilter = new double[0], voltageFilter = new double[0];
        double[] positionFilter = new double[0], bandwidthFilter = new double[0], widthFilter = new double[0], phaseFilter = new double[0];
        readonly MatrixChartArrayBuilderV114 statisticsBuilder = new MatrixChartArrayBuilderV114();
        int[] filteredRows = new int[0];

        [Category("Statistics"), DefaultValue(false)]
        public bool StatisticsFilterEnabled
        {
            get { return statisticsEnabled; }
            set { if (statisticsEnabled == value) return; statisticsEnabled = value; RefreshStatistics(); }
        }
        [Category("Statistics"), DefaultValue(0.0)]
        [Description("Minimum confidence in percent, 0..100. Setting enables statistics filtering. Zero disables only the confidence threshold.")]
        public double StatisticsFilterStrength
        {
            get { return statisticsStrength; }
            set { if (!Finite(value) || value < 0 || value > 100) throw new ArgumentOutOfRangeException("StatisticsFilterStrength");
                if (statisticsEnabled && statisticsStrength.Equals(value)) return;
                statisticsStrength = value; statisticsEnabled = true; RefreshStatistics(); }
        }
        [Category("Statistics"), DefaultValue(0.0)]
        public double MinimumDischargeProbability
        {
            get { return statisticsStrength / 100.0; }
            set { ValidateConfidenceThreshold(value); StatisticsFilterStrength = value * 100.0; }
        }
        [Category("Statistics"), DefaultValue(true)]
        public bool IncludeUnacceptedRows
        {
            get { return includeUnacceptedRows; }
            set { if (statisticsEnabled && includeUnacceptedRows == value) return; includeUnacceptedRows = value; statisticsEnabled = true; RefreshStatistics(); }
        }
        [Browsable(false)] public double[] DischargeCountFilter { get { return CloneFilter(countFilter); } set { SetStatisticsRange(ref countFilter, value); } }
        [Browsable(false)] public double[] DischargeChargeFilterPc { get { return CloneFilter(chargeFilter); } set { SetStatisticsRange(ref chargeFilter, value); } }
        [Browsable(false)] public double[] DischargeVoltageFilterKv { get { return CloneFilter(voltageFilter); } set { SetStatisticsRange(ref voltageFilter, value); } }
        [Browsable(false)] public double[] DischargePositionFilterM { get { return CloneFilter(positionFilter); } set { SetStatisticsRange(ref positionFilter, value); } }
        [Browsable(false)] public double[] EquivalentBandwidthFilterHz { get { return CloneFilter(bandwidthFilter); } set { SetStatisticsRange(ref bandwidthFilter, value); } }
        [Browsable(false)] public double[] EquivalentPulseWidthFilterNs { get { return CloneFilter(widthFilter); } set { SetStatisticsRange(ref widthFilter, value); } }
        [Browsable(false)] public double[] PhaseFilterDegrees { get { return CloneFilter(phaseFilter); } set { SetStatisticsRange(ref phaseFilter, value); } }
        [Browsable(false)] public int[] FilteredSourceRowIndices { get { return (int[])filteredRows.Clone(); } }
        [Browsable(false)] public int FilterRefreshSequence { get; private set; }
        [Browsable(false)] public string FilteringDiagnosticsJson { get; private set; } = "{}";

        /// <summary>Task 7 array conventions. Count filtering gates N-L histogram bins, not source rows.
        /// Confidence is a 0..1 threshold for column 33; it is not the unrelated spatial-cluster score.</summary>
        public void SetStatisticsFilters(double[] dischargeCountFilter, double[] dischargeChargeFilterPc,
            double[] dischargeVoltageFilterKv, double[] dischargePositionFilterM,
            double[] equivalentBandwidthFilterHz, double[] equivalentPulseWidthFilterNs,
            double[] phaseFilterDegrees, bool includeUnacceptedRows, double minimumDischargeProbability)
        {
            ValidateConfidenceThreshold(minimumDischargeProbability);
            if (statisticsEnabled && this.includeUnacceptedRows == includeUnacceptedRows &&
                statisticsStrength.Equals(minimumDischargeProbability * 100.0) &&
                SameFilter(countFilter, dischargeCountFilter) && SameFilter(chargeFilter, dischargeChargeFilterPc) &&
                SameFilter(voltageFilter, dischargeVoltageFilterKv) && SameFilter(positionFilter, dischargePositionFilterM) &&
                SameFilter(bandwidthFilter, equivalentBandwidthFilterHz) && SameFilter(widthFilter, equivalentPulseWidthFilterNs) &&
                SameFilter(phaseFilter, phaseFilterDegrees)) return;
            countFilter = CloneFilter(dischargeCountFilter); chargeFilter = CloneFilter(dischargeChargeFilterPc);
            voltageFilter = CloneFilter(dischargeVoltageFilterKv); positionFilter = CloneFilter(dischargePositionFilterM);
            bandwidthFilter = CloneFilter(equivalentBandwidthFilterHz); widthFilter = CloneFilter(equivalentPulseWidthFilterNs);
            phaseFilter = CloneFilter(phaseFilterDegrees); this.includeUnacceptedRows = includeUnacceptedRows;
            statisticsStrength = minimumDischargeProbability * 100.0; statisticsEnabled = true;
            RefreshStatistics();
        }

        void SetStatisticsRange(ref double[] target, double[] values)
        {
            if (statisticsEnabled && SameFilter(target, values)) return;
            target = CloneFilter(values); statisticsEnabled = true; RefreshStatistics();
        }
        static bool SameFilter(double[] a, double[] b)
        {
            int length = a == null ? 0 : a.Length;
            if (length != (b == null ? 0 : b.Length)) return false;
            for (int i = 0; i < length; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }
        static double[] CloneFilter(double[] values) { return values == null ? new double[0] : (double[])values.Clone(); }
        static void ValidateConfidenceThreshold(double value)
        {
            if (!Finite(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException("minimumDischargeProbability", "Expected confidence fraction in [0,1], not percent.");
        }
        void ResetStatisticsForNewData() { InvalidateLocationScores(); RefreshStatistics(); QueueDisplayAdvice(); }
        void RefreshStatisticsAfterEdit() { InvalidateLocationScores(); RefreshStatistics(true); QueueDisplayAdvice(); }

        void RefreshStatistics(bool preserveExplicitPreview=false)
        {
            RefreshTimeFrequencyDisplayIfChanged();
            // Changing filters starts a new filtered navigation context. Editing
            // or accepting an explicitly inspected row must not replace its waveform.
            if(!preserveExplicitPreview)previewFilteredRow=-1;
            int rows = working.GetLength(0), columns = working.GetLength(1);
            // Remove insufficient raw acquisitions before either statistics algorithm.
            // This physical eligibility set is independent of every optional filter.
            var completeMask=BuildWaveformCompletenessMask();
            var completeRows=Enumerable.Range(0,rows).Where(r=>completeMask[r]).ToArray();
            var mask = new bool[rows];
            if (!statisticsEnabled || capacitorProject)
            {
                for (int r = 0; r < rows; r++) mask[r] = completeMask[r];
                FilteringDiagnosticsJson = "{\"enabled\":false,\"source\":\"Task7-v1.1.7\"}";
            }
            else
            {
                // A private extra column carries exact source-row identity across Task 7's row-preserving output.
                // No caller column, acceptance flag, confidence value or waveform matrix is modified.
                var tagged = new double[completeRows.Length, columns + 1];var displayParameters=SpectrumDisplayParameters();
                for (int i = 0; i < completeRows.Length; i++)
                {
                    int r=completeRows[i];
                    for (int c = 0; c < columns; c++) tagged[i, c] = displayParameters[r, c];
                    tagged[i, columns] = r;
                }
                var output = statisticsBuilder.Build(tagged, countFilter, chargeFilter, voltageFilter,
                    positionFilter, bandwidthFilter, widthFilter, phaseFilter, includeUnacceptedRows, statisticsStrength / 100.0);
                double[,] retained = output.FilteredLocationMatrix;
                if (retained.GetLength(1) != columns + 1) throw new InvalidOperationException("Task 7 did not preserve the source-row identity column.");
                for (int r = 0; r < retained.GetLength(0); r++)
                {
                    double source = retained[r, columns];
                    if (!Finite(source) || source < 0 || source >= rows || source != Math.Floor(source))
                        throw new InvalidOperationException("Invalid source-row identity from Task 7.");
                    mask[(int)source] = true;
                }
                FilteringDiagnosticsJson = output.DiagnosticsJson;
            }
            ApplyLocationFilter(mask,completeMask);
            ApplyReviewRangeMask(mask);
            AppendWaveformCompletenessDiagnostics();
            reviewMask = mask;
            filteredRows = Enumerable.Range(0, rows).Where(r => mask[r]).ToArray();
            FilterRefreshSequence++;
            spectrum.SetReviewCountFilter(statisticsEnabled ? countFilter : null);
            ApplyTreeSpectrumVisibility();
            EnsureVisibleSelection();
            RefreshReviewUi();
            QueueAutomaticEvaluation();
        }
    }
}
