
using System;
using Xiongxin.Owts.Contracts.Waveform;

namespace Xiongxin.Owts.Contracts.Algorithms
{
    /// <summary>← ReviewAnalysisRequest / ProjectLocationAnalyzer.AnalyzeProjectLocationIncremental 入参</summary>
    public sealed class LocationAnalysisRequest
    {
        public string ProjectPath { get; set; }
        public string LocationFilePath { get; set; }
        public long LocationDataId { get; set; }
        public double SampleRateHz { get; set; }
        public double[] LocData { get; set; }
        public bool UseLocData { get; set; }
        public int AlgorithmIndex { get; set; }
        public int MaxPulses { get; set; }
        public int MultipleReflectionSuppressionCount { get; set; }
        public bool AutomaticAcceptance { get; set; }
        public bool ApplyLinearFilter { get; set; }
        public double[] LinearFilterCutoffsHz { get; set; }
        public bool[] PhaseQuadrants { get; set; }
        public double ConnectingCableLengthM { get; set; }
        public string EndpointPolicy { get; set; }
        public double AttenuationTolerancePercent { get; set; }
        public double OscillationFrequencyHz { get; set; }
        public double ManualThreshold { get; set; }
        public bool ManualThresholdEnabled { get; set; }
        public double AutomaticThresholdMultiplier { get; set; }
        /// <summary>← TaskMinus3.UseFirstArrivalTiming / ReviewAnalysisEngine；默认 false（峰时计时）。</summary>
        public bool UseFirstArrivalTiming { get; set; }
        /// <summary>← Sixth minimumCorrelationPercent；原软件 = 100 − CorrelationErrorPercent（默认 30 → 70）。</summary>
        public double MinimumCorrelationPercent { get; set; }
        /// <summary>← Sixth useAttenuationReference；校准不可靠时可关以避免 attenuation gate 全灭。</summary>
        public bool UseAttenuationReference { get; set; }
        /// <summary>← Sixth usePulseWidthComparison；FWHM/SNR 不足时可关。</summary>
        public bool UsePulseWidthComparison { get; set; }
        /// <summary>← Sixth pulseWidthErrorPercent；-1 = 引擎默认（Sixth→60 / Seventh→35）。</summary>
        public double PulseWidthErrorPercent { get; set; }
        public ParameterHistory BundleHistory { get; set; }
        /// <summary>Optional path; Task17Worker writes full statusMessage / pairs / threshold diagnostics JSON here.</summary>
        public string StatusDumpPath { get; set; }

        public LocationAnalysisRequest()
        {
            ProjectPath = string.Empty;
            LocationFilePath = string.Empty;
            SampleRateHz = 125e6;
            LocData = new double[0];
            AlgorithmIndex = 5;
            MaxPulses = 30;
            MultipleReflectionSuppressionCount = 1;
            AutomaticAcceptance = true;
            // 原软件 PressureBatch：ApplyLinearFilter = (!IsMethodE) && WaveformFilterMode==1；默认 Mode=0 → false
            ApplyLinearFilter = false;
            LinearFilterCutoffsHz = new double[] { 10000, 3000000 };
            PhaseQuadrants = new bool[] { true, true, true, true };
            EndpointPolicy = "2;1.5;OR;OR";
            AttenuationTolerancePercent = 15;
            AutomaticThresholdMultiplier = 2; // Compare default thr2 (sweep 2026-10-12)
            UseFirstArrivalTiming = false;
            MinimumCorrelationPercent = 70;
            UseAttenuationReference = true;
            UsePulseWidthComparison = true;
            PulseWidthErrorPercent = -1;
            BundleHistory = new ParameterHistory();
        }
    }

    public sealed class ParameterHistory
    {
        public ParameterMatrixDto PreviousParameters { get; set; }
        public ParameterMatrixDto PreviousRaw { get; set; }
        public ParameterMatrixDto PreviousFiltered { get; set; }
        public ParameterMatrixDto PreviousEquivalent { get; set; }

        public ParameterHistory()
        {
            PreviousParameters = new ParameterMatrixDto();
            PreviousRaw = new ParameterMatrixDto();
            PreviousFiltered = new ParameterMatrixDto();
            PreviousEquivalent = new ParameterMatrixDto();
        }
    }

    /// <summary>← ReviewAnalysisResult</summary>
    public sealed class LocationAnalysisResult
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public ParameterMatrixDto Parameters { get; set; }
        public ParameterMatrixDto Raw { get; set; }
        public ParameterMatrixDto Filtered { get; set; }
        public ParameterMatrixDto Equivalent { get; set; }
        public double AppliedThreshold { get; set; }

        public LocationAnalysisResult()
        {
            Status = string.Empty;
            Parameters = new ParameterMatrixDto();
            Raw = new ParameterMatrixDto();
            Filtered = new ParameterMatrixDto();
            Equivalent = new ParameterMatrixDto();
            AppliedThreshold = double.NaN;
        }
    }

    /// <summary>← HVX.SignalEvidence.SeventhPipelineInput</summary>
    public sealed class SeventhPipelineInputDto
    {
        public bool IndependentPackets { get; set; }
        public bool SamePolarity { get; set; }
        public bool GeometryValid { get; set; }
        public double IncidentMain { get; set; }
        public double ReflectionMain { get; set; }
        public double PairCorrelation { get; set; }
        public double CorrelationFloor { get; set; }
        public double AttenuationTolerancePercent { get; set; }
        public double WidthTolerancePercent { get; set; }
        public double RawPositionM { get; set; }
        public double CableLengthM { get; set; }
        public double SamplePeriodNs { get; set; }

        public SeventhPipelineInputDto()
        {
            CorrelationFloor = 0.7;
            AttenuationTolerancePercent = 15;
            WidthTolerancePercent = 35;
            IncidentMain = double.NaN;
            ReflectionMain = double.NaN;
            PairCorrelation = double.NaN;
            RawPositionM = double.NaN;
            CableLengthM = double.NaN;
            SamplePeriodNs = double.NaN;
        }
    }

    /// <summary>← HVX.SignalEvidence.SeventhPipelineResult</summary>
    public sealed class SeventhPipelineResultDto
    {
        public bool Available { get; set; }
        public bool CandidatePassed { get; set; }
        public bool AutoAccept { get; set; }
        public double Score { get; set; }
        public double ProjectedPositionM { get; set; }
        public string EndpointDecision { get; set; }
        public string[] Reasons { get; set; }

        public SeventhPipelineResultDto()
        {
            Score = double.NaN;
            ProjectedPositionM = double.NaN;
            EndpointDecision = "Indeterminate";
            Reasons = new string[0];
        }
    }

    public sealed class ConclusionRequest
    {
        public string ProjectPath { get; set; }
        public ParameterMatrixDto AcceptedParameters { get; set; }
        public double VoltageMinKv { get; set; }
        public double VoltageMaxKv { get; set; }
        public string OperatorOverrideLabel { get; set; }
        /// <summary>
        /// 可选：现场 ONNX 推理结果作为缺陷证据<strong>候选</strong>。
        /// 有候选也不等于 Task12/正式结论；引擎仍须 Success=false。
        /// </summary>
        public Xiongxin.Owts.Contracts.Ai.DefectRecognitionOutcome OnnxCandidate { get; set; }

        /// <summary>可选脉冲电压 kV（与 Charge/Phase/Energy 等长）→ Task15 Level 趋势因子。</summary>
        public double[] PulseVoltageKv { get; set; }
        /// <summary>可选脉冲表观电荷绝对值 pC。</summary>
        public double[] PulseChargeAbsPc { get; set; }
        /// <summary>可选脉冲相位度。</summary>
        public double[] PulsePhaseDeg { get; set; }
        /// <summary>可选真实波形能量；缺测/NaN 则能量项关闭。</summary>
        public double[] PulseRealEnergy { get; set; }

        public ConclusionRequest()
        {
            ProjectPath = string.Empty;
            AcceptedParameters = new ParameterMatrixDto();
            OperatorOverrideLabel = string.Empty;
            VoltageMinKv = double.NaN;
            VoltageMaxKv = double.NaN;
            OnnxCandidate = null;
            PulseVoltageKv = null;
            PulseChargeAbsPc = null;
            PulsePhaseDeg = null;
            PulseRealEnergy = null;
        }
    }

    /// <summary>← Task15 Algorithm；失败不伪造结论</summary>
    public sealed class ConclusionResult
    {
        public bool Success { get; set; }
        public string DisplayText { get; set; }
        public string[] EvidenceCodes { get; set; }
        public string StatusMessage { get; set; }

        public ConclusionResult()
        {
            DisplayText = string.Empty;
            EvidenceCodes = new string[0];
            StatusMessage = string.Empty;
        }
    }
}
