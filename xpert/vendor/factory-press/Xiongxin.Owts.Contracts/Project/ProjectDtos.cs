
using System;
using System.Collections.Generic;

namespace Xiongxin.Owts.Contracts.Project
{
    /// <summary>← HVX.Controls.ProjectModes</summary>
    public static class ProjectModeNames
    {
        public const string ThreeCore = "三芯";
        public const string SinglePhase = "单相";
        public const string ThreePhaseSingleCore = "三相单芯";
        public const string Capacitor = "电容局放";
    }

    /// <summary>← ProjectPressureModes</summary>
    public static class ProjectPressureModeNames
    {
        public const string ExistingCable = "旧电缆";
        public const string NewCable = "新电缆";
        public const string Custom = "自定义";
        public const string Arbitrary = "任意值";
    }

    /// <summary>← JointData</summary>
    public sealed class JointDataDto
    {
        public string Phase { get; set; }
        public string Name { get; set; }
        public string AttachmentType { get; set; }
        public string Material { get; set; }
        public double Position { get; set; }
        public int SourceBlockIndex { get; set; }
        public int SourceAccessoryIndex { get; set; }

        public JointDataDto()
        {
            Phase = "A相";
            Name = string.Empty;
            AttachmentType = "户内冷缩";
            Material = "塑料绝缘";
            SourceBlockIndex = -1;
            SourceAccessoryIndex = -1;
        }
    }

    /// <summary>← PressureStep</summary>
    public sealed class PressureStepDto
    {
        public double Multiplier { get; set; }
        public int Repetitions { get; set; }
    }

    /// <summary>← HVX.Controls.ProjectDraft</summary>
    public sealed class ProjectDraftDto
    {
        public string Organization { get; set; }
        public string OperatorName { get; set; }
        public string TestLocation { get; set; }
        public DateTime TestDateTime { get; set; }
        public bool DualEnded { get; set; }
        public double Humidity { get; set; }
        public double Temperature { get; set; }
        public string Weather { get; set; }
        public int WeatherCode { get; set; }
        public string WeatherProvider { get; set; }
        public DateTime WeatherUpdatedAt { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public string BeidouFixStatus { get; set; }
        public DateTime HardwareSampleTime { get; set; }
        public bool EnvironmentSampleValid { get; set; }
        public DateTime EnvironmentSampleTime { get; set; }
        public bool GnssFixValid { get; set; }
        public bool GnssAltitudeValid { get; set; }
        public DateTime GnssSampleTime { get; set; }
        public string StartName { get; set; }
        public string StartType { get; set; }
        public string DispatchNumberStart { get; set; }
        public string EndName { get; set; }
        public string EndType { get; set; }
        public string DispatchNumberEnd { get; set; }
        public string LineName { get; set; }
        public string CableModel { get; set; }
        public string CableMode { get; set; }
        public string DefaultMaterial { get; set; }
        public double LengthA { get; set; }
        public double LengthB { get; set; }
        public double LengthC { get; set; }
        public string PhaseVoltage { get; set; }
        public string PressureMode { get; set; }
        public DateTime LayingDate { get; set; }
        public string InsulationA1 { get; set; }
        public string InsulationA2 { get; set; }
        public string InsulationB1 { get; set; }
        public string InsulationB2 { get; set; }
        public string InsulationC1 { get; set; }
        public string InsulationC2 { get; set; }
        public string InsulationReferenceProfile { get; set; }
        public bool InsulationConditionsComparable { get; set; }
        public string Notes { get; set; }
        public List<JointDataDto> Joints { get; private set; }
        public Dictionary<string, string> FirstSegmentMaterials { get; private set; }
        public List<PressureStepDto> PressureSteps { get; private set; }
        public Dictionary<string, string> UnknownItemValues { get; private set; }

        public ProjectDraftDto()
        {
            Organization = string.Empty;
            OperatorName = string.Empty;
            TestLocation = string.Empty;
            TestDateTime = DateTime.Now;
            Weather = string.Empty;
            WeatherCode = -1;
            WeatherProvider = string.Empty;
            BeidouFixStatus = "未采集";
            StartName = string.Empty;
            StartType = "户内冷缩";
            DispatchNumberStart = string.Empty;
            EndName = string.Empty;
            EndType = "户内冷缩";
            DispatchNumberEnd = string.Empty;
            LineName = string.Empty;
            CableModel = string.Empty;
            CableMode = ProjectModeNames.ThreeCore;
            DefaultMaterial = "塑料绝缘";
            PhaseVoltage = "8.7kV";
            PressureMode = ProjectPressureModeNames.NewCable;
            LayingDate = DateTime.Today;
            InsulationA1 = string.Empty;
            InsulationA2 = string.Empty;
            InsulationB1 = string.Empty;
            InsulationB2 = string.Empty;
            InsulationC1 = string.Empty;
            InsulationC2 = string.Empty;
            InsulationReferenceProfile = string.Empty;
            Notes = string.Empty;
            Joints = new List<JointDataDto>();
            FirstSegmentMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PressureSteps = new List<PressureStepDto>();
            UnknownItemValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public double SummaryLength
        {
            get { return Math.Max(LengthA, Math.Max(LengthB, LengthC)); }
        }
    }

    public sealed class PressurePlanRowDto
    {
        public int Sequence { get; set; }
        public double U0Multiplier { get; set; }
        public int RepetitionCount { get; set; }
        public double VoltageKv { get; set; }
    }

    public sealed class ProjectPressurePlanDto
    {
        public List<PressurePlanRowDto> Rows { get; private set; }
        public ProjectPressurePlanDto() { Rows = new List<PressurePlanRowDto>(); }
    }

    public sealed class TrialRoundRequest
    {
        public string Phase { get; set; }
        public double TargetVoltageKv { get; set; }
        public double U0Multiplier { get; set; }
        public int PdRangeIndex { get; set; }
        public bool Simulation { get; set; }

        public TrialRoundRequest()
        {
            Phase = "A相";
            Simulation = true;
        }
    }

    /// <summary>← PressureTestAnalysisResult 关键字段</summary>
    public sealed class PressureMetricsDto
    {
        public double PartialDischargeMaximumPc { get; set; }
        public double OscillationFrequencyHz { get; set; }
        public double DissipationFactorPercent { get; set; }
        public string Status { get; set; }

        public PressureMetricsDto()
        {
            PartialDischargeMaximumPc = double.NaN;
            OscillationFrequencyHz = double.NaN;
            DissipationFactorPercent = double.NaN;
            Status = string.Empty;
        }
    }

    public sealed class TrialRoundResult
    {
        public bool Success { get; set; }
        public string StatusMessage { get; set; }
        public Acquisition.AcquisitionCycleResult Acquisition { get; set; }
        public string[] WaveformIds { get; set; }
        public PressureMetricsDto PressureMetrics { get; set; }

        public TrialRoundResult()
        {
            StatusMessage = string.Empty;
            WaveformIds = new string[0];
            PressureMetrics = new PressureMetricsDto();
        }
    }

    public sealed class CalibrationRoundRequest
    {
        public string Phase { get; set; }
        public bool Simulation { get; set; }
        public CalibrationRoundRequest() { Phase = "A相"; Simulation = true; }
    }

    public sealed class CalibrationRoundResult
    {
        public bool Success { get; set; }
        public string StatusMessage { get; set; }
        public string CalibrationId { get; set; }
        public CalibrationRoundResult()
        {
            StatusMessage = string.Empty;
            CalibrationId = string.Empty;
        }
    }

    public enum TrialSessionState
    {
        Idle = 0,
        Armed = 1,
        Running = 2,
        WaitingOperatorGate = 3,
        Faulted = 4,
        Completed = 5
    }
}
