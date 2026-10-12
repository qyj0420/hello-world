using System;

namespace HVX.PdReview
{
    /// <summary>Immutable, display-only raw-waveform endpoint evidence. Engineering score is not a probability or acceptance decision.</summary>
    public sealed class EndpointIdentificationResult
    {
        public int SourceRowIndex { get; }
        public int PdMapId { get; }
        public string Direction { get; }
        public string Reason { get; }
        public string Summary { get; }
        public string EvidenceText { get; }
        public string PolicyVersion { get; }
        public string PhysicalPhase { get; }
        public string CalibrationPhase { get; }
        public string CalibrationSelection { get; }
        public string CalibrationSourcePath { get; }
        public double SamplingRateHz { get; }
        public double CalibrationSamplingRateHz { get; }
        public double CableLengthM { get; }
        public double SampleLocationM { get; }
        public double EndpointToleranceM { get; }
        public double IncidentFwhmNs { get; }
        public double CalibrationFwhmNs { get; }
        public double WidthMultiple { get; }
        public bool ReflectionPrincipalLobeSplit { get; }
        public bool ReflectionSplitEvidenceAvailable { get; }
        public double IncidentTemporalMainAmplitude { get; }
        public double IncidentFollowingOppositeAmplitude { get; }
        public double IncidentMainOppositeRatio { get; }
        public double CalibrationMainOppositeRatio { get; }
        public double RatioRelativeToCalibration { get; }
        public double FarEngineeringScore { get; }
        public bool IsProbability { get { return false; } }
        public bool ChangesAnalysis { get { return false; } }

        internal sealed class Builder
        {
            internal int SourceRowIndex = -1, PdMapId = -1;
            internal string Direction = "Indeterminate", Reason = "evidence_missing", Summary = "", EvidenceText = "", PolicyVersion = "",
                PhysicalPhase = "", CalibrationPhase = "", CalibrationSelection = "Unavailable", CalibrationSourcePath = "";
            internal double SamplingRateHz = double.NaN, CalibrationSamplingRateHz = double.NaN, CableLengthM = double.NaN,
                SampleLocationM = double.NaN, EndpointToleranceM = double.NaN, IncidentFwhmNs = double.NaN, CalibrationFwhmNs = double.NaN,
                WidthMultiple = double.NaN, IncidentTemporalMainAmplitude = double.NaN, IncidentFollowingOppositeAmplitude = double.NaN,
                IncidentMainOppositeRatio = double.NaN, CalibrationMainOppositeRatio = double.NaN, RatioRelativeToCalibration = double.NaN,
                FarEngineeringScore = double.NaN;
            internal bool ReflectionPrincipalLobeSplit, ReflectionSplitEvidenceAvailable;
        }
        internal EndpointIdentificationResult(Builder b)
        {
            SourceRowIndex=b.SourceRowIndex; PdMapId=b.PdMapId; Direction=b.Direction; Reason=b.Reason; Summary=b.Summary;
            EvidenceText=b.EvidenceText; PolicyVersion=b.PolicyVersion; PhysicalPhase=b.PhysicalPhase; CalibrationPhase=b.CalibrationPhase;
            CalibrationSelection=b.CalibrationSelection; SamplingRateHz=b.SamplingRateHz; CalibrationSamplingRateHz=b.CalibrationSamplingRateHz;
            CalibrationSourcePath=b.CalibrationSourcePath;
            CableLengthM=b.CableLengthM; SampleLocationM=b.SampleLocationM; EndpointToleranceM=b.EndpointToleranceM;
            IncidentFwhmNs=b.IncidentFwhmNs; CalibrationFwhmNs=b.CalibrationFwhmNs; WidthMultiple=b.WidthMultiple;
            ReflectionPrincipalLobeSplit=b.ReflectionPrincipalLobeSplit; ReflectionSplitEvidenceAvailable=b.ReflectionSplitEvidenceAvailable;
            IncidentTemporalMainAmplitude=b.IncidentTemporalMainAmplitude; IncidentFollowingOppositeAmplitude=b.IncidentFollowingOppositeAmplitude;
            IncidentMainOppositeRatio=b.IncidentMainOppositeRatio; CalibrationMainOppositeRatio=b.CalibrationMainOppositeRatio;
            RatioRelativeToCalibration=b.RatioRelativeToCalibration; FarEngineeringScore=b.FarEngineeringScore;
        }
    }
}
