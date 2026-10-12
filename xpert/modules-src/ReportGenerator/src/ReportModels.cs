using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.ReportGenerator
{
    /// <summary>On-disk analysis package family understood by the report control.</summary>
    public enum AnalysisPackageKind
    {
        Unknown = 0,
        LegacyLabView = 1,
        NamedAnalysis = 2
    }

    /// <summary>Physical phase codes used by the unified location-result matrix.</summary>
    public enum ReportPhase
    {
        All = 0,
        A = 1,
        B = 2,
        C = 3
    }

    /// <summary>Semantic sections used by the checkable report-content tree.</summary>
    public enum ReportTreeNodeKind
    {
        Root = 0,
        ProjectInformation = 1,
        LocationResults = 2,
        LocationInterval = 3,
        LocationPhase = 4,
        CalibrationData = 5,
        TestData = 6,
        RegionStatistics = 7,
        PdMap = 8,
        TrendStatistics = 9,
        AnalysisAttachment = 10,
        Conclusion = 11,
        Item = 12
    }

    /// <summary>Chart families that can be selected for a generated report.</summary>
    public enum ReportImageCategory
    {
        Unknown = 0,
        LocationChargeDistance = 1,
        LocationCountDistance = 2,
        Calibration = 3,
        TestData = 4,
        RegionStatistics = 5,
        PdMap = 6,
        TrendStatistics = 7,
        AnalysisAttachment = 8
    }

    public enum ReportTestMetricKind
    {
        MaximumCharge = 0,
        AverageCharge = 1,
        DischargeCount = 2
    }

    /// <summary>
    /// A closed voltage interval expressed as a multiple of the device operating voltage U0.
    /// Report intervals are deliberately constrained to the supported 0..2U0 domain.
    /// </summary>
    public sealed class VoltageInterval
    {
        public VoltageInterval()
            : this("all", "0–2U0", 0.0, 2.0, false, true)
        {
        }

        public VoltageInterval(string id, string title, double minimumU0, double maximumU0)
            : this(id, title, minimumU0, maximumU0, true, false)
        {
        }

        public VoltageInterval(string id, string title, double minimumU0, double maximumU0,
            bool canDelete, bool isAllResults)
        {
            ValidateBounds(minimumU0, maximumU0);
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
            Title = title ?? string.Empty;
            MinimumU0 = minimumU0;
            MaximumU0 = maximumU0;
            CanDelete = canDelete;
            IsAllResults = isAllResults;
        }

        public string Id { get; set; }
        public string Title { get; set; }
        public double MinimumU0 { get; private set; }
        public double MaximumU0 { get; private set; }
        public bool CanDelete { get; set; }
        public bool IsAllResults { get; set; }

        public void SetBounds(double minimumU0, double maximumU0)
        {
            ValidateBounds(minimumU0, maximumU0);
            MinimumU0 = minimumU0;
            MaximumU0 = maximumU0;
        }

        public bool ContainsRatio(double value)
        {
            return IsFinite(value) && value >= MinimumU0 && value <= MaximumU0;
        }

        public bool ContainsVoltage(double voltageKv, double deviceU0Kv)
        {
            return IsFinite(voltageKv) && IsFinite(deviceU0Kv) && deviceU0Kv > 0.0 &&
                ContainsRatio(voltageKv / deviceU0Kv);
        }

        public static VoltageInterval AllResults()
        {
            return new VoltageInterval("all", "0–2U0", 0.0, 2.0, false, true);
        }

        private static void ValidateBounds(double minimumU0, double maximumU0)
        {
            if (!IsFinite(minimumU0) || !IsFinite(maximumU0) || minimumU0 < 0.0 ||
                maximumU0 > 2.0 || minimumU0 > maximumU0)
                throw new ArgumentOutOfRangeException("minimumU0", "Voltage interval must be a closed subset of 0..2U0.");
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>UI-neutral node model. The control may bind this to a WinForms TreeNode.</summary>
    public sealed class ReportTreeNodeModel
    {
        private readonly List<ReportTreeNodeModel> children = new List<ReportTreeNodeModel>();

        public ReportTreeNodeModel()
        {
            Key = string.Empty;
            Text = string.Empty;
            SourcePath = string.Empty;
            Phase = ReportPhase.All;
            IsAvailable = true;
        }

        public string Key { get; set; }
        public string Text { get; set; }
        public ReportTreeNodeKind Kind { get; set; }
        public ReportPhase Phase { get; set; }
        public VoltageInterval VoltageInterval { get; set; }
        public ReportImageCategory ImageCategory { get; set; }
        public string SourcePath { get; set; }
        public object Tag { get; set; }
        public bool IsChecked { get; set; }
        public bool IsAvailable { get; set; }
        public bool IsCheckLocked { get; set; }
        public bool CanAddChild { get; set; }
        public bool CanDelete { get; set; }
        public IList<ReportTreeNodeModel> Children { get { return children; } }

        public void AddChild(ReportTreeNodeModel child)
        {
            if (child == null) throw new ArgumentNullException("child");
            children.Add(child);
        }

        public bool RemoveChild(ReportTreeNodeModel child)
        {
            return child != null && child.CanDelete && children.Remove(child);
        }
    }

    /// <summary>A chart attachment recovered from an analysis package.</summary>
    public sealed class ReportImageInfo
    {
        private byte[] pngData = new byte[0];
        private bool includedInReport;

        public ReportImageInfo()
        {
            ChartKind = string.Empty;
            Title = string.Empty;
            RegionSummary = string.Empty;
            ImagePath = string.Empty;
            CreatedUtc = string.Empty;
            StatusReason = string.Empty;
        }

        public int Sequence { get; set; }
        public string ChartKind { get; set; }
        public int ChartMode { get; set; }
        public ReportImageCategory Category { get; set; }
        public string Title { get; set; }
        public string RegionSummary { get; set; }
        public string ImagePath { get; set; }
        public string CreatedUtc { get; set; }
        public int LanguageIndex { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IncludedInReport { get{return includedInReport&&!AcquisitionReferenceOnly&&!SourceUnavailable;} set{includedInReport=value&&!AcquisitionReferenceOnly&&!SourceUnavailable;} }
        public bool AcquisitionReferenceOnly { get; internal set; }
        public bool SourceUnavailable { get; internal set; }
        public bool IsHistoricalOrStale { get; set; }
        public string StatusReason { get; set; }
        public bool HasPngData { get { return pngData.Length > 0; } }
        public byte[] PngData { get { return (byte[])pngData.Clone(); } }

        internal void SetPngData(byte[] value)
        {
            pngData = value == null ? new byte[0] : (byte[])value.Clone();
        }
    }

    /// <summary>Resolved analysis package consumed by the report UI and document generator.</summary>
    public sealed class ReportAnalysisPackage
    {
        // Full file-relative engine statuses retain source-identity and packet
        // spectrum evidence. Engine row indices remain local to that file call.
        public string AlgorithmDiagnosticsJson {get;internal set;} = "";
        private double[,] locationMatrix = new double[0, 0];
        private double[,] statisticsMatrix = new double[0, 0];
        public bool HasEffectiveSelectionSnapshot {get;internal set;}
        public string EffectiveSelectionStatus {get;internal set;} = "not_available";
        internal bool[] EffectiveRows;
        public double[,] StatisticsMatrix {get{return (double[,])(Kind==AnalysisPackageKind.NamedAnalysis?statisticsMatrix:locationMatrix).Clone();}}
        internal void SetStatisticsMatrix(double[,] value){statisticsMatrix=value;HasEffectiveSelectionSnapshot=true;EffectiveSelectionStatus="saved_current_selection_v1";}
        private ReportImageInfo[] images = new ReportImageInfo[0];
        private string[] warnings = new string[0];

        public ReportAnalysisPackage()
        {
            AnalysisId = string.Empty;
            AnalysisName = string.Empty;
            CreatedUtc = string.Empty;
            SourcePath = string.Empty;
            PackageDirectory = string.Empty;
            ProjectPath = string.Empty;
            LegacyDataPath = string.Empty;
            CompanionPath = string.Empty;
            ConclusionText = string.Empty;
        }

        public AnalysisPackageKind Kind { get; set; }
        public string AnalysisId { get; set; }
        public string AnalysisName { get; set; }
        public string CreatedUtc { get; set; }
        public string SourcePath { get; set; }
        public string PackageDirectory { get; set; }
        public string ProjectPath { get; set; }
        public string LegacyDataPath { get; set; }
        public string CompanionPath { get; set; }
        public string ConclusionText { get; set; }
        public bool HasSavedConclusionSnapshot { get; internal set; }
        public string ConclusionApi { get; internal set; }
        public bool UsesEvidenceV3 { get { return (ConclusionApi??"").StartsWith("hvx-conclusion-dotnet-3.",StringComparison.Ordinal); } }
        internal double[,] ConclusionInput;
        internal double[][] ConclusionJoints;
        internal double[,] ConclusionRegions;
        internal double ConclusionOperatingVoltage;
        public bool HasReportInputSnapshot { get; internal set; }
        public bool ReportInputContextPresent { get; internal set; }
        public bool ReportJointApproximationEnabled { get; internal set; }
        public double ReportJointTolerancePercent { get; internal set; }
        public double ReportEndpointTolerancePercent { get; internal set; }
        public string ReportInputStatus { get; internal set; }
        public int PendingSelectedRecordCount { get; internal set; }
        public int SkippedSourceRecordCount { get; internal set; }
        public int UnavailableResultRowCount { get; internal set; }
        internal int[] UnavailableSourceRows = new int[0];
        internal int SkippedPendingSelectedRecordCount;
        public int[] AcquisitionReferenceOnlyRows { get; internal set; } = new int[0];
        public string AcquisitionQualityJson { get; internal set; } = "{}";
        public bool HasLocationMatrix { get { return locationMatrix.GetLength(0) > 0 && locationMatrix.GetLength(1) > 0; } }
        public int LocationRowCount { get { return locationMatrix.GetLength(0); } }
        public int LocationColumnCount { get { return locationMatrix.GetLength(1); } }
        public double[,] LocationMatrix { get { return (double[,])locationMatrix.Clone(); } }
        public ReportImageInfo[] ReportImages { get { return (ReportImageInfo[])images.Clone(); } }
        public ReportImageInfo[] SelectedReportImages
        {
            get { return images.Where(item => item != null && item.IncludedInReport).OrderBy(item => item.Sequence).ToArray(); }
        }
        public string[] Warnings { get { return (string[])warnings.Clone(); } }

        internal void SetLocationMatrix(double[,] value)
        {
            locationMatrix = value == null ? new double[0, 0] : value;
        }

        internal void SetReportImages(IEnumerable<ReportImageInfo> value)
        {
            images = value == null ? new ReportImageInfo[0] : value.Where(item => item != null).OrderBy(item => item.Sequence).ToArray();
        }

        internal void SetWarnings(IEnumerable<string> value)
        {
            warnings = value == null ? new string[0] : value.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().ToArray();
        }
    }

    /// <summary>Column mapping for the unified location matrix. Negative columns mean unavailable.</summary>
    public sealed class LocationMatrixColumnMap
    {
        public LocationMatrixColumnMap()
        {
            PhaseColumn = 0;
            VoltageKvColumn = 2;
            ChargePcColumn = 16;
            FallbackChargePcColumn = 15;
            AcceptedColumn = 35;
            AcceptedValue = 1.0;
            OnlyAcceptedRows = true;
        }

        public int PhaseColumn { get; set; }
        public int VoltageKvColumn { get; set; }
        public int ChargePcColumn { get; set; }
        public int FallbackChargePcColumn { get; set; }
        public int AcceptedColumn { get; set; }
        public double AcceptedValue { get; set; }
        public bool OnlyAcceptedRows { get; set; }

        public static LocationMatrixColumnMap UnifiedLocationMatrix()
        {
            return new LocationMatrixColumnMap();
        }
    }

    /// <summary>Maximum/average/count values for one phase and one voltage interval.</summary>
    public sealed class PhaseTestMetrics
    {
        public PhaseTestMetrics()
        {
            MaximumChargePc = double.NaN;
            AverageChargePc = double.NaN;
        }

        public ReportPhase Phase { get; set; }
        public int Count { get; set; }
        public int ChargeSampleCount { get; set; }
        public double MaximumChargePc { get; set; }
        public double AverageChargePc { get; set; }
        public bool HasRows { get { return Count > 0; } }
        public bool HasChargeValues { get { return ChargeSampleCount > 0; } }
    }

    /// <summary>ALL/A/B/C statistics for a single closed U0 interval.</summary>
    public sealed class ThreePhaseTestMetrics
    {
        public ThreePhaseTestMetrics()
        {
            Interval = VoltageInterval.AllResults();
            All = new PhaseTestMetrics { Phase = ReportPhase.All };
            A = new PhaseTestMetrics { Phase = ReportPhase.A };
            B = new PhaseTestMetrics { Phase = ReportPhase.B };
            C = new PhaseTestMetrics { Phase = ReportPhase.C };
            Status = string.Empty;
        }

        public double DeviceU0Kv { get; set; }
        public VoltageInterval Interval { get; set; }
        public PhaseTestMetrics All { get; set; }
        public PhaseTestMetrics A { get; set; }
        public PhaseTestMetrics B { get; set; }
        public PhaseTestMetrics C { get; set; }
        public bool IsValid { get; set; }
        public string Status { get; set; }

        public PhaseTestMetrics ForPhase(ReportPhase phase)
        {
            if (phase == ReportPhase.A) return A;
            if (phase == ReportPhase.B) return B;
            if (phase == ReportPhase.C) return C;
            return All;
        }

        public PhaseTestMetrics[] ToArray()
        {
            return new[] { All, A, B, C };
        }
    }
}
