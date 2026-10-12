namespace Xiongxin.Calibration
{
    internal sealed class CalibrationCaptureResult
    {
        internal string CoefficientProcessing="HP10K_NO_LP";
        internal double[] Average;
        internal double[][] Windows;
        internal double SampleRateHz, ThresholdV;
        internal double ScreeningThresholdV;
        internal bool ManualScreeningThreshold;
        internal HVX.OWTS.Task38.CalibrationIncidentAssessment Assessment;
        internal TdrCorrelationResult Reflection;
        internal string[] RawPaths;
        // Set only after saving or loading a verified formal record. V1 still
        // depends on its original raw references; V2/V3 are self-contained.
        internal string SavedRecordPath;
        // A withdrawn file is evidence for an explicit re-save, never a formal
        // calibration receipt. Hash-check it before reuse; keep waveform/coefficient.
        internal string WithdrawnRecordPath, WithdrawnRecordSha256, WithdrawnLevelSignature;
        internal string Folder, DeviceSerial, DeviceModel, AcquisitionMode;
        // RequestedCount is zero for a single segment; Windows/Assessment carry
        // the measured count. Triggered mode retains its explicit requested N.
        internal int RangeIndex, RequestedCount;
        internal int PicoCoupling=-1;
        internal string DcRemoval="";
        internal bool ShortCable;
        internal bool CapacitiveLoad;
        internal bool QualityOverrideForReview;
        internal string QualityReviewReason;
        internal HVX.SignalProcessing.ReflectionBandwidthResult Bandwidth;
    }
}
