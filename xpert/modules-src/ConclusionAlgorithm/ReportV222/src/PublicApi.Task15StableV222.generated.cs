using System;
using System.Collections.Generic;

namespace HVX.Location
{
    /// <summary>
    /// LabVIEW-facing result. All matrices are row-major flattened arrays.
    /// Use Reshape Array in LabVIEW with the supplied Rows/Columns values.
    /// </summary>
    public sealed class AnalysisResult
    {
        public bool Ok { get; internal set; }
        public string Error { get; internal set; }
        public string ApiVersion { get; internal set; }
        public string ModelVersion { get; internal set; }
        public int TotalPulses { get; internal set; }
        public int ValidPulses { get; internal set; }
        public int InputRows { get; internal set; }
        public int AcceptedRows { get; internal set; }
        public int RejectedIndex35Rows { get; internal set; }
        public double OperatingPhaseVoltageKv { get; internal set; }
        public bool EnglishOutput { get; internal set; }
        public string OutputLanguage { get; internal set; }
        public string RecommendationBasis { get; internal set; }
        public int ConclusionAudienceType { get; internal set; }
        public string ConclusionAudience { get; internal set; }
        public int AlgorithmMode { get; internal set; }
        public string AlgorithmId { get; internal set; }
        public string OriginalSummaryText { get; internal set; }

        public double[] FilteredScatter { get; internal set; }
        public int FilteredScatterRows { get; internal set; }
        public int FilteredScatterColumns { get { return 3; } }

        public double[] CandidateMatrix { get; internal set; }
        public int CandidateRows { get; internal set; }
        public int CandidateColumns { get { return 15; } }

        public double[] LocationMatrix { get; internal set; }
        public int LocationRows { get; internal set; }
        public int LocationColumns { get { return 11; } }

        internal int[][][] LocationPointRows = new int[0][][];

        // One row per merged location: phase mask, position m, PDIV kV,
        // operating phase voltage kV, PDIV/U0, inception pulse count,
        // inception application groups, voltage-response score, voltage risk code (0/1/2).
        public double[] VoltageAssessmentMatrix { get; internal set; }
        public int VoltageAssessmentRows { get; internal set; }
        public int VoltageAssessmentColumns { get { return 9; } }

        public string[] StatusArray { get; internal set; }
        public string SummaryText { get; internal set; }

        // Company-side continuous-learning data. One row per internal candidate.
        internal double[] LearningFeatureMatrix { get; set; }
        internal int LearningFeatureRows { get; set; }
        internal int LearningFeatureColumns { get { return 16; } }
        internal string[] CandidateIds { get; set; }

        // Final multi-mode conclusion diagnostics. FactorMatrix and LocationMatrix
        // preserve the stable runtime's location-row order; none of these arrays
        // are severity-sorted.
        public double[] FactorMatrix { get; internal set; }
        public int FactorRows { get; internal set; }
        public int FactorColumns { get; internal set; }
        public string[] FactorColumnNames { get; internal set; }
        public double[] MarkerMatrix { get; internal set; }
        public int MarkerRows { get; internal set; }
        public int MarkerColumns { get { return 4; } }
        public string[] ActionColors { get; internal set; }
        public string[] DefectTypeArray { get; internal set; }
        public double[] DefectProbabilityArray { get; internal set; }
        public string[] DefectStatusArray { get; internal set; }
        public string[] DiagnosticArray { get; internal set; }

        // PulseEnvelopeMatrix rows map one-to-one to accepted location rows.
        // Energy is calculated after a robust baseline subtraction; Q squared is
        // retained only as a charge index and is never presented as waveform energy.
        public bool PulseEnergyUsed { get; internal set; }
        public int PulseEnergyRows { get; internal set; }
        public double[] PulseEnergyMatrix { get; internal set; }
        public int PulseEnergyColumns { get; internal set; }
        public string[] PulseEnergyColumnNames { get; internal set; }
        public string PulseEnergyStatus { get; internal set; }

        // Row map columns: input row, accepted row (-1 if rejected), PDmap ID,
        // index-35 acceptance state, phase code, pulse-energy row (-1 if unused).
        public double[] InputToAcceptedRowMap { get; internal set; }
        public int InputToAcceptedRowMapRows { get; internal set; }
        public int InputToAcceptedRowMapColumns { get { return 6; } }
        public string[] InputToAcceptedRowMapColumnNames { get; internal set; }

        internal AnalysisResult()
        {
            Ok = false;
            Error = string.Empty;
            ApiVersion = LocatorEngine.ApiVersion;
            ModelVersion = "none";
            OutputLanguage = "zh-CN";
            RecommendationBasis = "DL/T 1576-2016; DL/T 417-2019; Q/GDW 11838-2018 (配电电缆场景适用时)";
            ConclusionAudienceType = 1;
            ConclusionAudience = "professional-technical";
            AlgorithmMode = 0;
            AlgorithmId = "HVX-STABLE-15";
            OriginalSummaryText = string.Empty;
            FilteredScatter = new double[0];
            CandidateMatrix = new double[0];
            LocationMatrix = new double[0];
            VoltageAssessmentMatrix = new double[0];
            StatusArray = new string[0];
            SummaryText = string.Empty;
            LearningFeatureMatrix = new double[0];
            CandidateIds = new string[0];
            FactorMatrix = new double[0];
            FactorColumnNames = new string[0];
            MarkerMatrix = new double[0];
            ActionColors = new string[0];
            DefectTypeArray = new string[0];
            DefectProbabilityArray = new double[0];
            DefectStatusArray = new string[0];
            DiagnosticArray = new string[0];
            PulseEnergyMatrix = new double[0];
            PulseEnergyColumnNames = new string[0];
            PulseEnergyStatus = "DISABLED|legacy-api";
            InputToAcceptedRowMap = new double[0];
            InputToAcceptedRowMapColumnNames = new[] {
                "input_row_index", "accepted_row_index", "pdmap_id",
                "accepted_index35", "phase_code", "pulse_energy_row_index" };
        }
    }

    /// <summary>
    /// Project-level TDR adjudication result. Matrices are row-major flattened
    /// arrays and are intentionally separate from the legacy AnalysisResult.
    /// FinalEngineeringScore is an engineering confidence score, not a
    /// statistically calibrated probability.
    /// </summary>
    internal sealed class TdrProjectAnalysisResult
    {
        public bool Ok { get; internal set; }
        public string Error { get; internal set; }
        public string ApiVersion { get; internal set; }
        public string ModelVersion { get; internal set; }
        public int TotalInputRows { get; internal set; }
        public int EligibleCandidateRows { get; internal set; }
        public int GlobalTotalValidLocCount { get; internal set; }
        public int ValidLocCountA { get; internal set; }
        public int ValidLocCountB { get; internal set; }
        public int ValidLocCountC { get; internal set; }
        public double DistanceResolutionM { get; internal set; }
        public double MaxTdrInfluencePoints { get; internal set; }
        public double OperatingPhaseVoltageKv { get; internal set; }
        public bool IsCalibratedProbability { get; internal set; }

        public double[] RegionMatrix { get; internal set; }
        public int RegionRows { get; internal set; }
        public int RegionColumns { get { return 35; } }

        public double[] SelectedVoteMatrix { get; internal set; }
        public int SelectedVoteRows { get; internal set; }
        public int SelectedVoteColumns { get { return 11; } }

        public string[] RegionStatusArray { get; internal set; }
        public string SummaryText { get; internal set; }

        internal TdrProjectAnalysisResult()
        {
            Ok = false;
            Error = string.Empty;
            ApiVersion = LocatorEngine.ApiVersion;
            ModelVersion = "none";
            IsCalibratedProbability = false;
            RegionMatrix = new double[0];
            SelectedVoteMatrix = new double[0];
            RegionStatusArray = new string[0];
            SummaryText = string.Empty;
        }
    }

    /// <summary>
    /// Direct .NET API for LabVIEW 2026 x64. No Python and no JSON parser are used.
    /// </summary>
    public sealed class LocatorEngine
    {
        public const string ApiVersion = "hvx-conclusion-dotnet-2.2.2-task17-schema";
        private readonly RegionModel _model;

        public LocatorEngine()
        {
            _model = EmbeddedModelLoader.Load();
        }

        public string Health()
        {
            return "OK|" + ApiVersion + "|" + _model.Version + "|encrypted-embedded-model";
        }


        internal static string RecommendationBasisForLanguage(bool englishOutput)
        {
            return englishOutput
                ? "IEEE Std 400.2 (VLF field testing); IEEE Std 400.4 (DAC field testing)"
                : "DL/T 1576-2016; DL/T 417-2019; Q/GDW 11838-2018 (配电电缆场景适用时)";
        }

        internal static string ConclusionAudienceName(int conclusionAudienceType)
        {
            if (conclusionAudienceType == 0) return "field-simple";
            if (conclusionAudienceType == 1) return "professional-technical";
            return "invalid";
        }

        internal static void ApplyPresentationMetadata(AnalysisResult result, bool englishOutput, int conclusionAudienceType)
        {
            result.EnglishOutput = englishOutput; result.OutputLanguage = englishOutput ? "en-US" : "zh-CN";
            result.ConclusionAudienceType = conclusionAudienceType; result.ConclusionAudience = ConclusionAudienceName(conclusionAudienceType);
            result.RecommendationBasis = RecommendationBasisForLanguage(englishOutput);
        }
        /// <summary>
        /// Analyze three phase matrices.
        /// Each phase is a row-major N x 7 array:
        /// charge_pc, position_m, phase_degree, voltage_kv,
        /// application_order, equivalent_width, equivalent_bandwidth.
        /// Joint arrays include 0 m and the cable end.
        /// </summary>
        private AnalysisResult Analyze(
            double[] dataA, int rowsA,
            double[] dataB, int rowsB,
            double[] dataC, int rowsC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double cableLengthM,
            double locationErrorPercent,
            double cursorStepM)
        {
            try
            {
                Analyzer analyzer = new Analyzer(_model);
                return analyzer.Run(
                    dataA, rowsA, dataB, rowsB, dataC, rowsC,
                    jointsA, jointsB, jointsC,
                    cableLengthM, locationErrorPercent, cursorStepM, 0.0, false);
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.Error = SingleLine(ex.Message);
                result.SummaryText = "分析失败：" + result.Error;
                return result;
            }
        }

        /// <summary>
        /// Analyze three phase matrices and assess PD inception voltage against
        /// the cable's normal single-phase operating voltage U0 (kV).
        /// This is a separate method so existing LabVIEW VIs using Analyze remain compatible.
        /// </summary>
        private AnalysisResult AnalyzeWithOperatingVoltage(
            double[] dataA, int rowsA,
            double[] dataB, int rowsB,
            double[] dataC, int rowsC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double cableLengthM,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv)
        {
            try
            {
                Analyzer analyzer = new Analyzer(_model);
                return analyzer.Run(
                    dataA, rowsA, dataB, rowsB, dataC, rowsC,
                    jointsA, jointsB, jointsC,
                    cableLengthM, locationErrorPercent, cursorStepM,
                    operatingPhaseVoltageKv, false);
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                result.Error = SingleLine(ex.Message);
                result.SummaryText = "分析失败：" + result.Error;
                return result;
            }
        }

        /// <summary>
        /// Analyze three phase matrices with operating phase voltage and a
        /// LabVIEW Boolean language selector. false=Chinese conclusion with
        /// applicable DL/T/Q/GDW guidance; true=English conclusion with IEEE guidance.
        /// English conclusions use IEEE Std 400.2 and IEEE Std 400.4 guidance.
        /// Existing language APIs default to the professional-technical conclusion profile.
        /// </summary>
        private AnalysisResult AnalyzeWithOperatingVoltageAndLanguage(
            double[] dataA, int rowsA,
            double[] dataB, int rowsB,
            double[] dataC, int rowsC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double cableLengthM,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            bool englishOutput)
        {
            try
            {
                Analyzer analyzer = new Analyzer(_model);
                return analyzer.Run(
                    dataA, rowsA, dataB, rowsB, dataC, rowsC,
                    jointsA, jointsB, jointsC,
                    cableLengthM, locationErrorPercent, cursorStepM,
                    operatingPhaseVoltageKv, englishOutput);
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                ApplyPresentationMetadata(result, englishOutput, 1);
                result.Error = SingleLine(ex.Message);
                result.SummaryText = englishOutput
                    ? "Analysis failed: " + result.Error
                    : "分析失败：" + result.Error;
                return result;
            }
        }

        /// <summary>
        /// LabVIEW-friendly matrix API. Data A, B and C are connected directly as
        /// N x 7 two-dimensional DBL arrays. The row count is inferred inside the
        /// DLL, so LabVIEW does not need Array Size, row-count inputs or Reshape Array.
        /// Cable length is taken from the last element of the non-empty joint arrays.
        /// Column order: charge_pc, position_m, phase_degree, voltage_kv,
        /// application_order, equivalent_width, equivalent_bandwidth.
        /// </summary>
        private AnalysisResult AnalyzeMatricesWithOperatingVoltageAndLanguage(
            double[,] dataA,
            double[,] dataB,
            double[,] dataC,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            bool englishOutput)
        {
            try
            {
                int rowsA;
                int rowsB;
                int rowsC;
                double[] flatA = FlattenPhaseMatrix(dataA, "dataA", out rowsA);
                double[] flatB = FlattenPhaseMatrix(dataB, "dataB", out rowsB);
                double[] flatC = FlattenPhaseMatrix(dataC, "dataC", out rowsC);

                Analyzer analyzer = new Analyzer(_model);
                return analyzer.Run(
                    flatA, rowsA, flatB, rowsB, flatC, rowsC,
                    jointsA, jointsB, jointsC,
                    0.0, locationErrorPercent, cursorStepM,
                    operatingPhaseVoltageKv, englishOutput);
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                ApplyPresentationMetadata(result, englishOutput, 1);
                result.Error = SingleLine(ex.Message);
                result.SummaryText = englishOutput
                    ? "Analysis failed: " + result.Error
                    : "\u5206\u6790\u5931\u8d25\uff1a" + result.Error;
                return result;
            }
        }
        /// <summary>
        /// Preferred LabVIEW API for the application's complete positioning-data matrix.
        /// The DLL routes rows by column 0 (A=1, B=2, C=3; ALL=0 is ignored) and maps:
        /// data ID=1, voltage=2, phase angle=3, position=13, charge=15,
        /// pulse-level discharge probability=22, equivalent width=23 and equivalent bandwidth=24.
        /// Column 22 accepts either 0..1 or 0..100 and is treated only as supporting evidence.
        /// Application order is derived inside each rounded 0.1 kV voltage level by
        /// sorting unique positioning-data IDs from low to high.
        /// </summary>
        private AnalysisResult AnalyzeLocationDataMatrixWithOperatingVoltageAndLanguage(
            double[,] locationDataMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            bool englishOutput)
        {
            try
            {
                double[] flatA;
                double[] flatB;
                double[] flatC;
                double[] probabilityA;
                double[] probabilityB;
                double[] probabilityC;
                int rowsA;
                int rowsB;
                int rowsC;
                SplitLocationDataMatrix(
                    locationDataMatrix,
                    out flatA, out rowsA, out probabilityA,
                    out flatB, out rowsB, out probabilityB,
                    out flatC, out rowsC, out probabilityC);

                Analyzer analyzer = new Analyzer(_model);
                AnalysisResult result = analyzer.RunWithPulseProbability(
                    flatA, rowsA, probabilityA,
                    flatB, rowsB, probabilityB,
                    flatC, rowsC, probabilityC,
                    jointsA, jointsB, jointsC,
                    0.0, locationErrorPercent, cursorStepM,
                    operatingPhaseVoltageKv, englishOutput, 1);
                ApplyLocationMatrixAcceptanceCounters(result, locationDataMatrix);
                return result;
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                ApplyPresentationMetadata(result, englishOutput, 1);
                result.Error = SingleLine(ex.Message);
                result.SummaryText = englishOutput
                    ? "Analysis failed: " + result.Error
                    : "分析失败：" + result.Error;
                ApplyLocationMatrixAcceptanceCounters(result, locationDataMatrix);
                return result;
            }
        }

        /// <summary>
        /// Preferred LabVIEW API with a conclusion-audience selector.
        /// conclusionAudienceType: 0=field-simple for users without specialist PD knowledge;
        /// 1=professional-technical for engineers and researchers.
        /// The language Boolean also selects the standard family automatically:
        /// Chinese=applicable DL/T and Q/GDW references;
        /// English=IEEE Std 400.2 and IEEE Std 400.4 guidance.
        /// </summary>
        private AnalysisResult AnalyzeLocationDataMatrixWithOperatingVoltageLanguageAndAudience(
            double[,] locationDataMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            bool englishOutput,
            int conclusionAudienceType)
        {
            try
            {
                double[] flatA;
                double[] flatB;
                double[] flatC;
                double[] probabilityA;
                double[] probabilityB;
                double[] probabilityC;
                int rowsA;
                int rowsB;
                int rowsC;
                SplitLocationDataMatrix(
                    locationDataMatrix,
                    out flatA, out rowsA, out probabilityA,
                    out flatB, out rowsB, out probabilityB,
                    out flatC, out rowsC, out probabilityC);

                Analyzer analyzer = new Analyzer(_model);
                AnalysisResult result = analyzer.RunWithPulseProbability(
                    flatA, rowsA, probabilityA,
                    flatB, rowsB, probabilityB,
                    flatC, rowsC, probabilityC,
                    jointsA, jointsB, jointsC,
                    0.0, locationErrorPercent, cursorStepM,
                    operatingPhaseVoltageKv, englishOutput, conclusionAudienceType);
                ApplyLocationMatrixAcceptanceCounters(result, locationDataMatrix);
                return result;
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                ApplyPresentationMetadata(result, englishOutput, conclusionAudienceType);
                result.Error = SingleLine(ex.Message);
                result.SummaryText = englishOutput
                    ? "Analysis failed: " + result.Error
                    : "分析失败：" + result.Error;
                ApplyLocationMatrixAcceptanceCounters(result, locationDataMatrix);
                return result;
            }
        }

        /// <summary>
        /// Preferred conclusion API with DL/T 1576 cable profile inputs.
        /// cableServiceYears: <=1.0=new cable; >1.0=existing cable.
        /// cableBodyType: 0=XLPE, 1=PILC.
        /// intermediateJointType: 0=automatic/same as cable,
        /// 1=XLPE-XLPE, 2=PILC-PILC, 3=XLPE-PILC.
        /// The legacy API remains compatible and defaults to an existing XLPE cable.
        /// </summary>
        private AnalysisResult AnalyzeLocationDataMatrixWithStandardProfile(
            double[,] locationDataMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            double cableServiceYears,
            int cableBodyType,
            int intermediateJointType,
            bool englishOutput,
            int conclusionAudienceType)
        {
            try
            {
                double[] flatA;
                double[] flatB;
                double[] flatC;
                double[] probabilityA;
                double[] probabilityB;
                double[] probabilityC;
                int rowsA;
                int rowsB;
                int rowsC;
                SplitLocationDataMatrix(
                    locationDataMatrix,
                    out flatA, out rowsA, out probabilityA,
                    out flatB, out rowsB, out probabilityB,
                    out flatC, out rowsC, out probabilityC);

                Analyzer analyzer = new Analyzer(_model);
                analyzer.ConfigureStandardProfile(cableServiceYears, cableBodyType, intermediateJointType);
                AnalysisResult result = analyzer.RunWithPulseProbability(
                    flatA, rowsA, probabilityA,
                    flatB, rowsB, probabilityB,
                    flatC, rowsC, probabilityC,
                    jointsA, jointsB, jointsC,
                    0.0, locationErrorPercent, cursorStepM,
                    operatingPhaseVoltageKv, englishOutput, conclusionAudienceType);
                ApplyLocationMatrixAcceptanceCounters(result, locationDataMatrix);
                return result;
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                ApplyPresentationMetadata(result, englishOutput, conclusionAudienceType);
                result.Error = SingleLine(ex.Message);
                result.SummaryText = englishOutput
                    ? "Analysis failed: " + result.Error
                    : "分析失败：" + result.Error;
                ApplyLocationMatrixAcceptanceCounters(result, locationDataMatrix);
                return result;
            }
        }

        /// <summary>
        /// Final Task-15 conclusion entry for the existing Figure-1 LabVIEW call chain:
        /// construct LocatorEngine, invoke this method, then read AnalysisResult properties.
        /// algorithmMode: 0=stable legacy conclusion with strict index-35 filtering;
        /// 1=multi-factor conclusion; 2=stable/multi-factor hybrid with one-to-one row diagnostics.
        /// Every mode accepts only rows whose locationDataMatrix column index 35 equals 1.
        /// </summary>
        private AnalysisResult AnalyzeLocationDataMatrixWithAlgorithmMode(
            double[,] locationDataMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            bool englishOutput,
            int conclusionAudienceType,
            int algorithmMode)
        {
            return AnalyzeFinalModes(locationDataMatrix, null,
                jointsA, jointsB, jointsC, locationErrorPercent, cursorStepM,
                operatingPhaseVoltageKv, 2.0, 0, 0, englishOutput,
                conclusionAudienceType, algorithmMode, false);
        }

        /// <summary>
        /// Compatibility entry for a caller-preselected compact location matrix.
        /// N x 5 columns are: phase_code, position_m, voltage_kv, phase_degree,
        /// charge_pc. N x 6 adds accepted_flag at column index 5; only finite
        /// values exactly equal to 1 are accepted. Because the compact schema has
        /// no location-data/application ID, all rows use one conservative
        /// application group and this entry never invents repeat-application
        /// evidence. Formal Task-17 conclusions must continue to use the strict
        /// 36-column AnalyzeLocationDataMatrixWithAlgorithmMode entry.
        /// </summary>
        private AnalysisResult AnalyzeCompactLocationDataMatrixWithAlgorithmMode(
            double[,] compactLocationDataMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            bool englishOutput,
            int conclusionAudienceType,
            int algorithmMode)
        {
            try
            {
                int compactColumns;
                double[,] expanded = ExpandCompactLocationDataMatrix(
                    compactLocationDataMatrix, out compactColumns);
                AnalysisResult result = AnalyzeFinalModes(expanded, null,
                    jointsA, jointsB, jointsC, locationErrorPercent, cursorStepM,
                    operatingPhaseVoltageKv, 2.0, 0, 0, englishOutput,
                    conclusionAudienceType, algorithmMode, false);
                result.AlgorithmId = (result.AlgorithmId ?? string.Empty) +
                    "-COMPACT" + compactColumns + "-CALLER-PREACCEPTED";
                return result;
            }
            catch (Exception ex)
            {
                AnalysisResult result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                ApplyPresentationMetadata(result, englishOutput, conclusionAudienceType);
                result.AlgorithmMode = algorithmMode;
                result.AlgorithmId = "HVX-COMPACT-15-CALLER-PREACCEPTED";
                result.Error = SingleLine(ex.Message);
                result.SummaryText = englishOutput
                    ? "Analysis failed: " + result.Error
                    : "\u5206\u6790\u5931\u8d25\uff1a" + result.Error;
                return result;
            }
        }

        /// <summary>
        /// Final Task-15 conclusion entry with optional real pulse windows.
        /// pulseEnvelopeMatrix may be null/empty; true waveform-energy factors are then
        /// disabled and the remaining multi-factor weights are renormalized without error.
        /// A non-empty matrix may contain either one row per original input row or one
        /// row per accepted input row. Samples are columns and retain accepted-row order.
        /// </summary>
        private AnalysisResult AnalyzeWithPulseEnergyAndAlgorithmMode(
            double[,] locationDataMatrix,
            double[,] pulseEnvelopeMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            bool englishOutput,
            int conclusionAudienceType,
            int algorithmMode)
        {
            return AnalyzeFinalModes(locationDataMatrix, pulseEnvelopeMatrix,
                jointsA, jointsB, jointsC, locationErrorPercent, cursorStepM,
                operatingPhaseVoltageKv, 2.0, 0, 0, englishOutput,
                conclusionAudienceType, algorithmMode, false);
        }

        private AnalysisResult AnalyzeLocationDataMatrixWithStandardProfileAndAlgorithmMode(
            double[,] locationDataMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            double cableServiceYears,
            int cableBodyType,
            int intermediateJointType,
            bool englishOutput,
            int conclusionAudienceType,
            int algorithmMode)
        {
            return AnalyzeFinalModes(locationDataMatrix, null,
                jointsA, jointsB, jointsC, locationErrorPercent, cursorStepM,
                operatingPhaseVoltageKv, cableServiceYears, cableBodyType,
                intermediateJointType, englishOutput, conclusionAudienceType,
                algorithmMode, true);
        }

        public AnalysisResult AnalyzeWithStandardProfilePulseEnergyAndAlgorithmMode(
            double[,] locationDataMatrix,
            double[,] pulseEnvelopeMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            double cableServiceYears,
            int cableBodyType,
            int intermediateJointType,
            bool englishOutput,
            int conclusionAudienceType,
            int algorithmMode,
            string defectModelPath)
        {
            return AnalyzeFinalModes(locationDataMatrix, pulseEnvelopeMatrix,
                jointsA, jointsB, jointsC, locationErrorPercent, cursorStepM,
                operatingPhaseVoltageKv, cableServiceYears, cableBodyType,
                intermediateJointType, englishOutput, conclusionAudienceType,
                algorithmMode, true, defectModelPath);
        }

        private AnalysisResult AnalyzeFinalModes(
            double[,] locationDataMatrix,
            double[,] pulseEnvelopeMatrix,
            double[] jointsA, double[] jointsB, double[] jointsC,
            double locationErrorPercent,
            double cursorStepM,
            double operatingPhaseVoltageKv,
            double cableServiceYears,
            int cableBodyType,
            int intermediateJointType,
            bool englishOutput,
            int conclusionAudienceType,
            int algorithmMode,
            bool standardProfile,
            string defectModelPath = null)
        {
            AnalysisResult result = null;
            AcceptedInput intake = null;
            try
            {
                if (algorithmMode < 0 || algorithmMode > 2)
                    throw new ArgumentOutOfRangeException("algorithmMode", "algorithmMode must be 0, 1 or 2.");
                intake = BuildStrictAcceptedInput(locationDataMatrix, pulseEnvelopeMatrix);
                result = standardProfile
                    ? AnalyzeLocationDataMatrixWithStandardProfile(intake.AcceptedMatrix,
                        jointsA, jointsB, jointsC, locationErrorPercent, cursorStepM,
                        operatingPhaseVoltageKv, cableServiceYears, cableBodyType,
                        intermediateJointType, englishOutput, conclusionAudienceType)
                    : AnalyzeLocationDataMatrixWithOperatingVoltageLanguageAndAudience(
                        intake.AcceptedMatrix, jointsA, jointsB, jointsC,
                        locationErrorPercent, cursorStepM, operatingPhaseVoltageKv,
                        englishOutput, conclusionAudienceType);

                ApplyAcceptedInputDiagnostics(result, intake);
                result.AlgorithmMode = algorithmMode;
                result.AlgorithmId = algorithmMode == 0
                    ? "HVX-STABLE-15-ACCEPTED35"
                    : (algorithmMode == 1 ? "HVX-MULTIFACTOR-15-ENERGY" : "HVX-HYBRID-15-ROW-MAPPED");
                if (!result.Ok)
                    result.SummaryText = englishOutput
                        ? "Analysis could not be completed; no conclusion is available."
                        : "本次分析未能完成，暂无法形成有效结论。";
                result.OriginalSummaryText = result.SummaryText;
                if (result.Ok && (intake.AcceptedRows == 0 || result.ValidPulses == 0))
                {
                    string reason = intake.InputRows == 0 ? "NO_INPUT" :
                        (intake.AcceptedRows == 0 ? "NO_ACCEPTED_ROWS" : "NO_VALID_ENERGIZED_ROWS");
                    result.SummaryText = englishOutput
                        ? "Insufficient usable measurement records; no discharge or equipment-condition conclusion can be established."
                        : "本次无足够有效测试记录，暂不能判断是否存在局部放电，也不能据此判定电缆状态正常。";
                    result.OriginalSummaryText = result.SummaryText;
                    result.StatusArray = new[] { reason };
                    result.DiagnosticArray = new[] { reason };
                    result.PulseEnergyStatus = "DISABLED|" + reason;
                    return result;
                }
                if (!result.Ok || algorithmMode == 0)
                {
                    result.PulseEnergyUsed = false;
                    result.PulseEnergyStatus = algorithmMode == 0
                        ? "DISABLED|algorithm-mode-0"
                        : "DISABLED|stable-analysis-failed";
                    if (result.Ok)
                        MultiFactorCore.AttachDefectLabels(result, intake.AcceptedMatrix,
                            ResolveLocationSourceRows(intake.AcceptedMatrix, result.LocationPointRows),
                            defectModelPath, englishOutput);
                    return result;
                }

                MultiFactorResult extra = MultiFactorCore.Evaluate(
                    intake.AcceptedMatrix, intake.AcceptedPulseEnvelope,
                    result.LocationMatrix, result.LocationRows, result.LocationColumns,
                    result.VoltageAssessmentMatrix, result.VoltageAssessmentRows,
                    result.VoltageAssessmentColumns, jointsA, jointsB, jointsC,
                    cursorStepM, operatingPhaseVoltageKv, cableServiceYears,
                    cableBodyType, intermediateJointType, algorithmMode, englishOutput,
                    ResolveLocationSourceRows(intake.AcceptedMatrix, result.LocationPointRows));
                result.FactorMatrix = extra.FactorMatrix;
                result.FactorRows = extra.Rows;
                result.FactorColumns = extra.Columns;
                result.FactorColumnNames = extra.ColumnNames;
                result.MarkerMatrix = extra.MarkerMatrix;
                result.MarkerRows = extra.MarkerRows;
                result.StatusArray = extra.ActionLabels;
                result.ActionColors = extra.ActionColors;
                result.DefectTypeArray = extra.DefectTypes;
                result.DefectProbabilityArray = extra.DefectProbabilities;
                result.DefectStatusArray = extra.DefectStatuses;
                result.DiagnosticArray = extra.Diagnostics;
                result.LocationMatrix = extra.LocationMatrix;
                result.SummaryText = extra.SummaryText;
                result.RecommendationBasis = extra.RecommendationBasis;
                result.PulseEnergyUsed = extra.PulseEnergyUsed;
                result.PulseEnergyRows = extra.PulseEnergyRows;
                result.PulseEnergyColumns = extra.PulseEnergyColumns;
                result.PulseEnergyMatrix = extra.PulseEnergyMatrix;
                result.PulseEnergyColumnNames = extra.PulseEnergyColumnNames;
                result.PulseEnergyStatus = extra.PulseEnergyStatus;
                MultiFactorCore.AppendRuntimeMessage(result, extra.RuntimeMessage);
                MultiFactorCore.AttachDefectLabels(result, intake.AcceptedMatrix,
                    ResolveLocationSourceRows(intake.AcceptedMatrix, result.LocationPointRows),
                    defectModelPath, englishOutput);
                return result;
            }
            catch (Exception ex)
            {
                result = new AnalysisResult();
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                ApplyPresentationMetadata(result, englishOutput, conclusionAudienceType);
                result.AlgorithmMode = algorithmMode;
                result.AlgorithmId = algorithmMode == 0 ? "HVX-STABLE-15-ACCEPTED35" :
                    (algorithmMode == 1 ? "HVX-MULTIFACTOR-15-ENERGY" : "HVX-HYBRID-15-ROW-MAPPED");
                result.Error = SingleLine(ex.Message);
                result.SummaryText = englishOutput
                    ? "Analysis could not be completed; no conclusion is available."
                    : "本次分析未能完成，暂无法形成有效结论。";
                if (intake != null) ApplyAcceptedInputDiagnostics(result, intake);
                return result;
            }
        }

        /// <summary>
        /// Finalize independently analyzed single-LOC TDR candidates at project level.
        /// Each phase matrix is row-major N x 25. Each phase valid LOC count must
        /// include energized records with no candidate rows in that phase.
        /// </summary>
        private TdrProjectAnalysisResult AnalyzeTdrProjectV2(
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
            try
            {
                TdrProjectAnalyzer analyzer = new TdrProjectAnalyzer(_model);
                return analyzer.Run(
                    dataA, rowsA, dataB, rowsB, dataC, rowsC,
                    jointsA, jointsB, jointsC,
                    cableLengthM, locationErrorPercent, distanceResolutionM,
                    validLocCountA, validLocCountB, validLocCountC, operatingPhaseVoltageKv,
                    maxTdrInfluencePoints);
            }
            catch (Exception ex)
            {
                TdrProjectAnalysisResult result = new TdrProjectAnalysisResult();
                result.ValidLocCountA = validLocCountA;
                result.ValidLocCountB = validLocCountB;
                result.ValidLocCountC = validLocCountC;
                result.GlobalTotalValidLocCount = validLocCountA + validLocCountB + validLocCountC;
                result.DistanceResolutionM = distanceResolutionM;
                result.OperatingPhaseVoltageKv = operatingPhaseVoltageKv;
                result.MaxTdrInfluencePoints = maxTdrInfluencePoints;
                result.Error = SingleLine(ex.Message);
                result.SummaryText = "\u9879\u76EE\u7EC8\u5BA1\u5931\u8D25\uFF1A" + result.Error;
                return result;
            }
        }

        /// <summary>
        /// Append one human-reviewed candidate to a company-side feedback CSV.
        /// label: 1=confirmed PD, 0=confirmed interference.
        /// The client release does not need to call this method.
        /// </summary>
        private string AppendFeedback(
            string csvPath,
            AnalysisResult result,
            int candidateIndex,
            int label,
            string sessionId,
            string cableId,
            string reviewer)
        {
            try
            {
                if (result == null) throw new ArgumentNullException("result");
                if (label != 0 && label != 1) throw new ArgumentException("label必须为0或1");
                if (candidateIndex < 0 || candidateIndex >= result.LearningFeatureRows)
                    throw new ArgumentOutOfRangeException("candidateIndex");
                FeedbackCsv.Append(csvPath, result, candidateIndex, label, sessionId, cableId, reviewer);
                return "OK";
            }
            catch (Exception ex)
            {
                return "ERROR|" + SingleLine(ex.Message);
            }
        }

        private static AcceptedInput BuildStrictAcceptedInput(
            double[,] locationDataMatrix, double[,] pulseEnvelopeMatrix)
        {
            AcceptedInput output = new AcceptedInput();
            int inputRows = locationDataMatrix == null ? 0 : locationDataMatrix.GetLength(0);
            int columns = locationDataMatrix == null ? 36 : locationDataMatrix.GetLength(1);
            if (inputRows > 0 && columns <= 35)
                throw new ArgumentException(
                    "Final conclusion modes require locationDataMatrix column index 35 (at least 36 columns).",
                    "locationDataMatrix");

            bool[] accepted = new bool[inputRows];
            int acceptedRows = 0;
            for (int row = 0; row < inputRows; row++)
            {
                accepted[row] = IsFiniteInput(locationDataMatrix[row, 35]) &&
                    locationDataMatrix[row, 35] == 1.0;
                if (accepted[row]) acceptedRows++;
            }

            output.InputRows = inputRows;
            output.AcceptedRows = acceptedRows;
            output.RejectedRows = inputRows - acceptedRows;
            output.AcceptedMatrix = new double[acceptedRows, Math.Max(36, columns)];
            output.RowMap = new double[inputRows * 6];
            int acceptedIndex = 0;
            for (int row = 0; row < inputRows; row++)
            {
                int map = row * 6;
                output.RowMap[map] = row;
                output.RowMap[map + 1] = accepted[row] ? acceptedIndex : -1.0;
                // Task 17 v2.4.x uses column 9 as the incremental PDmap ID.
                // Fall back to the location-data ID (column 1) only for older matrices.
                output.RowMap[map + 2] = columns > 9 && IsFiniteInput(locationDataMatrix[row, 9])
                    ? locationDataMatrix[row, 9]
                    : (columns > 1 && IsFiniteInput(locationDataMatrix[row, 1])
                        ? locationDataMatrix[row, 1] : row);
                output.RowMap[map + 3] = accepted[row] ? 1.0 : 0.0;
                output.RowMap[map + 4] = columns > 0 && IsFiniteInput(locationDataMatrix[row, 0])
                    ? locationDataMatrix[row, 0] : 0.0;
                output.RowMap[map + 5] = -1.0;
                if (!accepted[row]) continue;
                for (int column = 0; column < columns; column++)
                    output.AcceptedMatrix[acceptedIndex, column] = locationDataMatrix[row, column];
                acceptedIndex++;
            }

            int waveRows = pulseEnvelopeMatrix == null ? 0 : pulseEnvelopeMatrix.GetLength(0);
            int waveColumns = pulseEnvelopeMatrix == null ? 0 : pulseEnvelopeMatrix.GetLength(1);
            output.PulseEnvelopeProvided = waveRows > 0 && waveColumns > 0;
            if (!output.PulseEnvelopeProvided) return output;
            if (waveRows != inputRows && waveRows != acceptedRows)
                throw new ArgumentException(
                    "pulseEnvelopeMatrix rows must equal either input rows or accepted index-35 rows.",
                    "pulseEnvelopeMatrix");

            output.AcceptedPulseEnvelope = new double[acceptedRows, waveColumns];
            acceptedIndex = 0;
            for (int row = 0; row < inputRows; row++)
            {
                if (!accepted[row]) continue;
                int sourceRow = waveRows == inputRows ? row : acceptedIndex;
                for (int column = 0; column < waveColumns; column++)
                    output.AcceptedPulseEnvelope[acceptedIndex, column] = pulseEnvelopeMatrix[sourceRow, column];
                output.RowMap[row * 6 + 5] = acceptedIndex;
                acceptedIndex++;
            }
            return output;
        }


        private static int[][] ResolveLocationSourceRows(double[,] input, int[][][] locations)
        {
            var phases = new List<int>[] { new List<int>(), new List<int>(), new List<int>() };
            for (int r = 0; r < input.GetLength(0); r++)
            {
                int p = (int)Math.Round(input[r, 0]);
                if (p >= 1 && p <= 3) phases[p - 1].Add(r);
            }
            var result = new int[locations.Length][];
            for (int i = 0; i < locations.Length; i++)
            {
                var rows = new List<int>();
                foreach (int[] key in locations[i]) rows.Add(phases[key[0]][key[1]]);
                result[i] = rows.ToArray();
            }
            return result;
        }

        private static double[,] ExpandCompactLocationDataMatrix(
            double[,] compact, out int compactColumns)
        {
            int rows = compact == null ? 0 : compact.GetLength(0);
            compactColumns = compact == null ? 5 : compact.GetLength(1);
            if (rows <= 0) return new double[0, 36];
            if (compactColumns != 5 && compactColumns != 6)
                throw new ArgumentException(
                    "compactLocationDataMatrix must have exactly 5 columns " +
                    "[phase, position_m, voltage_kv, phase_degree, charge_pc] " +
                    "or 6 columns with accepted_flag at index 5.",
                    "compactLocationDataMatrix");

            double[,] expanded = new double[rows, 36];
            for (int row = 0; row < rows; row++)
            {
                double phase = compact[row, 0];
                double position = compact[row, 1];
                double voltage = compact[row, 2];
                double phaseDegree = compact[row, 3];
                double charge = compact[row, 4];
                if (!IsFiniteInput(phase) || !IsFiniteInput(position) ||
                    !IsFiniteInput(voltage) || !IsFiniteInput(phaseDegree) ||
                    !IsFiniteInput(charge))
                    throw new ArgumentException(
                        "compactLocationDataMatrix row " + row +
                        " contains NaN or Infinity in a required column.",
                        "compactLocationDataMatrix");

                // Compact input intentionally carries no application/data ID.
                // Use one shared conservative group so the conclusion cannot
                // fabricate repeated-pressure evidence from individual pulses.
                expanded[row, 0] = phase;
                expanded[row, 1] = 1.0;
                expanded[row, 2] = voltage;
                expanded[row, 3] = phaseDegree;
                expanded[row, 9] = row;
                expanded[row, 13] = position;
                expanded[row, 15] = charge;
                expanded[row, 16] = Math.Abs(charge);
                // Compact legacy data has no upstream matching-confidence field.
                expanded[row, 33] = double.NaN;
                expanded[row, 35] = compactColumns == 5
                    ? 1.0
                    : (IsFiniteInput(compact[row, 5]) &&
                        Math.Abs(compact[row, 5] - 1.0) <= 1e-9 ? 1.0 : 0.0);
            }
            return expanded;
        }

        private static void ApplyAcceptedInputDiagnostics(AnalysisResult result, AcceptedInput intake)
        {
            if (result == null || intake == null) return;
            result.InputRows = intake.InputRows;
            result.AcceptedRows = intake.AcceptedRows;
            result.RejectedIndex35Rows = intake.RejectedRows;
            result.InputToAcceptedRowMap = intake.RowMap;
            result.InputToAcceptedRowMapRows = intake.InputRows;
            int invalidConfidence = 0;
            for (int row = 0; row < intake.AcceptedRows; row++)
            {
                double confidence = intake.AcceptedMatrix[row, 33];
                if (!IsFiniteInput(confidence) || confidence < 0.0 || confidence > 1.0)
                    invalidConfidence++;
            }
            if (invalidConfidence > 0)
                MultiFactorCore.AppendRuntimeMessage(result,
                    "TASK17_MATCH_CONFIDENCE_MISSING|" + invalidConfidence +
                    " accepted rows have invalid column33; evidence disabled for these rows; column22 amplitude ratio is not a fallback.");
            if (!intake.PulseEnvelopeProvided)
                result.PulseEnergyStatus = "DISABLED|empty-pulse-envelope";
        }

        private static void SplitLocationDataMatrix(
            double[,] matrix,
            out double[] flatA, out int rowsA, out double[] probabilityA,
            out double[] flatB, out int rowsB, out double[] probabilityB,
            out double[] flatC, out int rowsC, out double[] probabilityC)
        {
            flatA = new double[0]; rowsA = 0; probabilityA = null;
            flatB = new double[0]; rowsB = 0; probabilityB = null;
            flatC = new double[0]; rowsC = 0; probabilityC = null;
            // LabVIEW may pass an uninitialized 2-D DBL array as a null reference,
            // or as a 0 x 0 / 0 x N matrix. These are valid no-discharge inputs.
            if (matrix == null) return;
            int rowCount = matrix.GetLength(0);
            int columnCount = matrix.GetLength(1);
            if (rowCount <= 0) return;
            if (columnCount < 25)
                throw new ArgumentException("定位数据二维矩阵至少需要25列（最高使用索引24）", "locationDataMatrix");
            bool hasAcceptanceColumn = columnCount > 35;

            int[] rowPhases = new int[rowCount];
            int[] rowGroups = new int[rowCount];
            int row;
            for (row = 0; row < rowCount; row++)
            {
                rowPhases[row] = -1;
                rowGroups[row] = -1;
            }

            List<double> groupIds = new List<double>();
            List<double> groupVoltageLevels = new List<double>();
            int phaseRowCount = 0;
            bool hasProbabilityData = false;
            for (row = 0; row < rowCount; row++)
            {
                // The unified positioning matrix uses index 35 as the final
                // acceptance decision. When that column is present, rejected,
                // missing and non-finite rows must not influence application
                // ordering, pulse counts, noise estimates, clustering or the
                // published conclusion. Matrices with 25..35 columns retain
                // the historical compatibility behaviour.
                if (hasAcceptanceColumn &&
                    (!IsFiniteInput(matrix[row, 35]) || Math.Abs(matrix[row, 35] - 1.0) > 1e-9))
                    continue;

                double phaseValue = matrix[row, 0];
                if (!IsFiniteInput(phaseValue))
                    throw new ArgumentException("定位数据矩阵第" + (row + 1) + "行的相别索引0不是有效数值");
                double roundedPhase = Math.Round(phaseValue);
                if (Math.Abs(phaseValue - roundedPhase) > 1e-6)
                    throw new ArgumentException("定位数据矩阵第" + (row + 1) + "行的相别必须为0、1、2或3");
                int phaseCode = (int)roundedPhase;
                if (phaseCode == 0) continue;
                if (phaseCode < 1 || phaseCode > 3)
                    throw new ArgumentException("定位数据矩阵第" + (row + 1) + "行的相别必须为0、1、2或3");

                double dataId = matrix[row, 1];
                double voltage = matrix[row, 2];
                if (!IsFiniteInput(dataId))
                    throw new ArgumentException("定位数据矩阵第" + (row + 1) + "行的定位数据ID无效");
                if (!IsFiniteInput(voltage))
                    throw new ArgumentException("定位数据矩阵第" + (row + 1) + "行的当前电压无效");

                double voltageLevel = Math.Round(voltage, 1, MidpointRounding.AwayFromZero);
                int groupIndex = -1;
                int group;
                for (group = 0; group < groupIds.Count; group++)
                {
                    if (SameDataId(groupIds[group], dataId) &&
                        Math.Abs(groupVoltageLevels[group] - voltageLevel) <= 1e-9)
                    {
                        groupIndex = group;
                        break;
                    }
                }
                if (groupIndex < 0)
                {
                    groupIndex = groupIds.Count;
                    groupIds.Add(dataId);
                    groupVoltageLevels.Add(voltageLevel);
                }

                rowPhases[row] = phaseCode - 1;
                rowGroups[row] = groupIndex;
                double inputEvidence = ReadLocationMatchingEvidence(matrix, row, columnCount);
                if (IsFiniteInput(inputEvidence) &&
                    (columnCount >= 36 || Math.Abs(inputEvidence) > 1e-12)) hasProbabilityData = true;
                phaseRowCount++;
            }
            // A non-empty matrix may legitimately contain no accepted rows.
            // Return the initialized empty phase arrays so Analyzer.Run emits
            // the normal no-discharge result instead of treating this as an
            // input error.
            if (phaseRowCount <= 0) return;

            int[] applicationOrders = new int[groupIds.Count];
            int currentGroup;
            for (currentGroup = 0; currentGroup < groupIds.Count; currentGroup++)
            {
                int order = 1;
                int otherGroup;
                for (otherGroup = 0; otherGroup < groupIds.Count; otherGroup++)
                {
                    if (Math.Abs(groupVoltageLevels[otherGroup] - groupVoltageLevels[currentGroup]) > 1e-9) continue;
                    if (groupIds[otherGroup] < groupIds[currentGroup] &&
                        !SameDataId(groupIds[otherGroup], groupIds[currentGroup])) order++;
                }
                applicationOrders[currentGroup] = order;
            }

            List<double>[] phaseValues = new List<double>[] {
                new List<double>(), new List<double>(), new List<double>()
            };
            List<double>[] phaseProbabilities = new List<double>[] {
                new List<double>(), new List<double>(), new List<double>()
            };
            for (row = 0; row < rowCount; row++)
            {
                int phase = rowPhases[row];
                if (phase < 0) continue;
                List<double> values = phaseValues[phase];
                values.Add(ReadLocationCharge(matrix, row, columnCount));
                values.Add(matrix[row, 13]);
                values.Add(matrix[row, 3]);
                values.Add(matrix[row, 2]);
                values.Add(applicationOrders[rowGroups[row]]);
                values.Add(matrix[row, 23]);
                values.Add(matrix[row, 24]);
                phaseProbabilities[phase].Add(ReadLocationMatchingEvidence(matrix, row, columnCount));
            }

            flatA = phaseValues[0].ToArray();
            flatB = phaseValues[1].ToArray();
            flatC = phaseValues[2].ToArray();
            probabilityA = hasProbabilityData ? phaseProbabilities[0].ToArray() : null;
            probabilityB = hasProbabilityData ? phaseProbabilities[1].ToArray() : null;
            probabilityC = hasProbabilityData ? phaseProbabilities[2].ToArray() : null;
            rowsA = flatA.Length / 7;
            rowsB = flatB.Length / 7;
            rowsC = flatC.Length / 7;
        }

        internal static double ReadLocationCharge(double[,] matrix, int row, int columnCount)
        {
            double rawCharge = matrix[row, 15];
            if (columnCount > 16 && IsFiniteInput(matrix[row, 16]))
            {
                double compensatedMagnitude = Math.Abs(matrix[row, 16]);
                // Some legacy 25-column matrices allocate the compensated-charge
                // column but leave it at zero. Preserve their original non-zero
                // charge while still preferring a populated compensated value.
                if (compensatedMagnitude <= 1e-12 &&
                    IsFiniteInput(rawCharge) && Math.Abs(rawCharge) > 1e-12)
                {
                    return rawCharge;
                }
                return IsFiniteInput(rawCharge) && rawCharge < 0.0
                    ? -compensatedMagnitude
                    : compensatedMagnitude;
            }
            return rawCharge;
        }

        private static void ApplyLocationMatrixAcceptanceCounters(AnalysisResult result, double[,] matrix)
        {
            if (result == null) return;
            int inputRows = matrix == null ? 0 : matrix.GetLength(0);
            result.InputRows = inputRows;
            result.AcceptedRows = inputRows;
            result.RejectedIndex35Rows = 0;
            if (matrix == null || matrix.GetLength(1) <= 35) return;

            int acceptedRows = 0;
            int rejectedRows = 0;
            for (int row = 0; row < inputRows; row++)
            {
                double accepted = matrix[row, 35];
                if (IsFiniteInput(accepted) && Math.Abs(accepted - 1.0) <= 1e-9)
                    acceptedRows++;
                else
                    rejectedRows++;
            }
            result.AcceptedRows = acceptedRows;
            result.RejectedIndex35Rows = rejectedRows;
        }

        private static double ReadLocationMatchingEvidence(double[,] matrix, int row, int columnCount)
        {
            // Explicit matrix contracts, not value-based schema inference:
            // Task17 unified >=36 columns: index22 is an amplitude ratio and
            // index33 is engineering matching confidence, already in [0,1].
            // It is evidence, not a statistically calibrated PD probability.
            // The final public API requires index35; older 25..35-column input
            // is supported only by the hidden legacy API and keeps index22.
            if (columnCount >= 36)
            {
                double confidence = matrix[row, 33];
                return IsFiniteInput(confidence) && confidence >= 0.0 && confidence <= 1.0
                    ? confidence : double.NaN;
            }
            return NormalizeLocationProbability(matrix[row, 22]);
        }

        private static double NormalizeLocationProbability(double value)
        {
            if (!IsFiniteInput(value)) return double.NaN;
            if (value > 1.0) value /= 100.0;
            return Math.Max(0.0, Math.Min(1.0, value));
        }

        private static bool IsFiniteInput(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool SameDataId(double left, double right)
        {
            double tolerance = Math.Max(1e-9, Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-12);
            return Math.Abs(left - right) <= tolerance;
        }
        internal static double ResolveCableLengthFromJointEndpoints(
            double[] jointsA, double[] jointsB, double[] jointsC)
        {
            double[][] allJoints = new double[][] { jointsA, jointsB, jointsC };
            double cableLength = 0.0;
            bool found = false;
            int phase;
            for (phase = 0; phase < allJoints.Length; phase++)
            {
                double[] values = allJoints[phase];
                if (values == null || values.Length == 0) continue;
                double endpoint = values[values.Length - 1];
                if (double.IsNaN(endpoint) || double.IsInfinity(endpoint) || endpoint <= 0.0)
                    throw new ArgumentException("Joint " + PhaseLetter(phase) + "数组最后一个元素必须为大于0的电缆末端位置");
                double tolerance = Math.Max(0.001, endpoint * 1e-6);
                int index;
                for (index = 0; index < values.Length - 1; index++)
                {
                    double value = values[index];
                    if (!double.IsNaN(value) && !double.IsInfinity(value) && value > endpoint + tolerance)
                        throw new ArgumentException("Joint " + PhaseLetter(phase) + "数组最后一个元素必须为电缆末端最大位置");
                }
                if (!found)
                {
                    cableLength = endpoint;
                    found = true;
                }
                else
                {
                    // Single-core A/B/C cables may have different physical lengths.
                    cableLength = Math.Max(cableLength, endpoint);
                }
            }
            if (!found) throw new ArgumentException("至少一个Joint数组必须包含0 m和电缆末端位置");
            return cableLength;
        }

        private static string PhaseLetter(int phase)
        {
            return phase == 0 ? "A" : phase == 1 ? "B" : "C";
        }
        private static double[] FlattenPhaseMatrix(double[,] matrix, string parameterName, out int rows)
        {
            if (matrix == null)
            {
                rows = 0;
                return new double[0];
            }

            rows = matrix.GetLength(0);
            int columns = matrix.GetLength(1);
            if (rows == 0)
            {
                if (columns != 0 && columns != 7)
                    throw new ArgumentException(parameterName + " must be an N x 7 two-dimensional DBL array.", parameterName);
                return new double[0];
            }
            if (columns != 7)
                throw new ArgumentException(parameterName + " must be an N x 7 two-dimensional DBL array.", parameterName);

            double[] flat = new double[rows * 7];
            int row;
            int column;
            for (row = 0; row < rows; row++)
            {
                for (column = 0; column < 7; column++)
                    flat[row * 7 + column] = matrix[row, column];
            }
            return flat;
        }

        private sealed class AcceptedInput
        {
            internal int InputRows;
            internal int AcceptedRows;
            internal int RejectedRows;
            internal bool PulseEnvelopeProvided;
            internal double[,] AcceptedMatrix = new double[0, 36];
            internal double[,] AcceptedPulseEnvelope;
            internal double[] RowMap = new double[0];
        }

        internal static string SingleLine(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
        }
    }
}

