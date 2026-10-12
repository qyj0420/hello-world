using System;
using LabVIEWPdMap;

namespace HVX.PdReview
{
    /// <summary>Immutable, in-memory input snapshot for Task 11 RenderPDmap (PDpulse).
    /// All X arrays contain zero-based local sample indices, not nanoseconds. No file is written.</summary>
    public sealed class PdPulseReportParameters
    {
        readonly double[] calibrationX, calibrationY, dischargeX, dischargeY, attenuationX, attenuationY;
        readonly bool[] auxiliaryCurveVisible;
        public string SchemaVersion { get { return "1.0"; } }
        public string SourceControlVersion { get; private set; }
        public string TargetMethod { get { return "OWTS.ReportCharts.ReportChartEngine.RenderPDmap"; } }
        public string HorizontalCoordinateUnit { get { return "LocalSampleIndex"; } }
        public double[] CalibrationX { get { return (double[])calibrationX.Clone(); } }
        public double[] CalibrationY { get { return (double[])calibrationY.Clone(); } }
        public double[] DischargeX { get { return (double[])dischargeX.Clone(); } }
        public double[] DischargeY { get { return (double[])dischargeY.Clone(); } }
        public double[] AttenuationX { get { return (double[])attenuationX.Clone(); } }
        public double[] AttenuationY { get { return (double[])attenuationY.Clone(); } }
        public bool[] AuxiliaryCurveVisible { get { return (bool[])auxiliaryCurveVisible.Clone(); } }
        public int IncidentIndex { get; private set; }
        public int ReflectionIndex { get; private set; }
        public double SamplingRateHz { get; private set; }
        public double DischargePositionM { get; private set; }
        public double CanonicalDischargePositionM { get; private set; }
        public bool PositionReversed { get; private set; }
        public double ConnectingCableLengthM { get; private set; }
        public string PositionCoordinateReference { get { return PositionReversed?"DUT far-end display origin":"DUT near-end canonical origin"; } }
        public double DischargeAmplitudePc { get; private set; }
        public double CompensatedAmplitudePc { get; private set; }
        public int PdMapId { get; private set; }
        public int SourceRowIndex { get; private set; }
        public double RecordId { get; private set; }
        public double LocId { get; private set; }
        public int PhysicalPhaseIndex { get; private set; }
        public double VoltageKv { get; private set; }
        public double PhaseDegrees { get; private set; }
        public bool FilteredWaveform { get; private set; }
        public bool EnglishVersion { get; private set; }
        public int LanguageIndex { get; private set; }
        string L(string en,string zh){return ReviewLanguages.Text(LanguageIndex,en,zh);}
        public string PulseName { get; private set; }
        public string XName { get { return L("Sample point","采样点"); } }
        public string LeftYName { get { return L("Discharge / pC","放电量 / pC"); } }
        public string CalibrationName { get { return L("Calibration waveform","校准波形"); } }
        public string DischargeName { get { return LanguageIndex<2?(EnglishVersion ? (FilteredWaveform ? "Filtered discharge pulse" : "Original discharge pulse") : (FilteredWaveform ? "滤波放电脉冲" : "原始放电脉冲")):L(FilteredWaveform?"Filtered":"Raw","")+" · "+L("Discharge",""); } }
        public string AttenuationName { get { return L("Damping decay curve","阻尼衰减曲线"); } }
        public double TickFontSizePx { get { return 0; } }

        private PdPulseReportParameters(PdMapControl control, string pulseName)
        {
            double[] row = control.GetReviewParameterRow();
            if (row.Length < 36 || control.CurrentParameterRowIndex < 0)
                throw new InvalidOperationException("No selected PDmap result is available for report output.");
            bool[] visible = control.GetReportCurveVisibility();
            if (visible.Length != 6) throw new InvalidOperationException("Unsupported PDmap curve visibility contract.");
            FilteredWaveform = visible[3];
            int group = FilteredWaveform ? 3 : 0;
            if (!visible[group]) throw new InvalidOperationException("No discharge curve is selected for report output.");
            SplitCurve(control.GetReportCurve(group), true, out dischargeX, out dischargeY);
            SplitCurve(control.GetReportCurve(group + 1), false, out calibrationX, out calibrationY);
            SplitCurve(control.GetReportCurve(group + 2), false, out attenuationX, out attenuationY);
            if (dischargeY.Length == 0) throw new InvalidOperationException("The selected discharge waveform is empty.");
            IncidentIndex = control.CurrentIncidentIndex;
            ReflectionIndex = control.CurrentReflectionIndex;
            if (IncidentIndex < 0 || IncidentIndex >= dischargeY.Length || ReflectionIndex < 0 || ReflectionIndex >= dischargeY.Length)
                throw new InvalidOperationException("Incident or reflection index is outside the report waveform.");
            double dt = control.CurrentSampleIntervalNs;
            SamplingRateHz = 1e9 / dt;
            if (!Finite(dt) || dt <= 0 || !Finite(SamplingRateHz) || SamplingRateHz <= 0)
                throw new InvalidOperationException("The report sample interval must be finite and positive.");
            CanonicalDischargePositionM=row[13];PositionReversed=control.ReversePositionDisplay;
            ConnectingCableLengthM=control.ConnectingCableLengthM;
            DischargePositionM = control.PresentedLocationM;
            DischargeAmplitudePc = control.CurrentRawChargePc;
            CompensatedAmplitudePc = control.CurrentCompensatedChargePc;
            if (!Finite(DischargePositionM) || !Finite(DischargeAmplitudePc) || !Finite(CompensatedAmplitudePc))
                throw new InvalidOperationException("The selected result contains non-finite report values.");
            auxiliaryCurveVisible = new[] { visible[group + 1] && calibrationY.Length > 0, visible[group + 2] && attenuationY.Length > 0 };
            PdMapId = control.CurrentPdMapId;
            SourceRowIndex = control.CurrentParameterRowIndex;
            RecordId = row[1]; LocId = row[8];
            PhysicalPhaseIndex = Finite(row[0]) && row[0] >= 0 && row[0] <= 3 && row[0] == Math.Floor(row[0]) ? (int)row[0] : -1;
            VoltageKv = control.CurrentVoltageKv; PhaseDegrees = control.CurrentPhaseDeg;
            EnglishVersion = control.EnglishVersion;
            LanguageIndex = control.LanguageIndex;
            PulseName = string.IsNullOrWhiteSpace(pulseName) ? (EnglishVersion ? "Partial discharge pulse" : "局部放电脉冲") : pulseName.Trim();
            SourceControlVersion = control.ControlBuildVersion;
        }

        public static PdPulseReportParameters Capture(PdMapControl control, string pulseName)
        {
            if (control == null) throw new ArgumentNullException("control");
            if (control.IsDisposed) throw new ObjectDisposedException("control");
            return new PdPulseReportParameters(control, pulseName);
        }

        static void SplitCurve(double[,] curve, bool completePulse, out double[] x, out double[] y)
        {
            if (curve == null || curve.GetLength(1) != 2) throw new InvalidOperationException("Report curves require two columns: sample index and amplitude.");
            int count = curve.GetLength(0); x = new double[count]; y = new double[count];
            for (int i = 0; i < count; i++)
            {
                double sample = curve[i, 0], value = curve[i, 1];
                if (!Finite(sample) || sample < 0 || sample != Math.Floor(sample) || !Finite(value) ||
                    (completePulse ? sample != i : i > 0 && sample <= x[i - 1]))
                    throw new InvalidOperationException("Invalid report curve sample sequence.");
                x[i] = sample; y[i] = value;
            }
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
