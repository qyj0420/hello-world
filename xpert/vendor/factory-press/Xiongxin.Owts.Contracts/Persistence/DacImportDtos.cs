using System;
using System.Collections.Generic;

namespace Xiongxin.Owts.Contracts.Persistence
{
    /// <summary>只读打开 .dac（SQLite）后的工程元数据 + 波形 + 原 pd_map 基线。</summary>
    public sealed class DacImportResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string SourcePath { get; set; }
        public double CableLengthM { get; set; }
        public double ConfiguredVelocityMPerUs { get; set; }
        public double CalibratedVelocityMPerUs { get; set; }
        public string CalibrationPeaks { get; set; }
        /// <summary>首条 calibration_points.calibration_factor_loc（兼容旧调用；逐测次以波形上的为准）。</summary>
        public double CalibrationFactorLoc { get; set; }
        public IList<DacCalibrationPoint> CalibrationPoints { get; set; }
        public IList<DacMeasurementWaveform> Measurements { get; set; }
        public IList<DacPdMapEntry> PdMapBaseline { get; set; }
        public IList<string> Warnings { get; set; }

        public DacImportResult()
        {
            Message = string.Empty;
            SourcePath = string.Empty;
            CalibrationPeaks = string.Empty;
            CalibrationPoints = new List<DacCalibrationPoint>();
            Measurements = new List<DacMeasurementWaveform>();
            PdMapBaseline = new List<DacPdMapEntry>();
            Warnings = new List<string>();
        }
    }

    /// <summary>calibration_points 一行（电量校准系数）。</summary>
    public sealed class DacCalibrationPoint
    {
        public long Id { get; set; }
        public int Phase { get; set; }
        /// <summary>校准器注入量，库内为 pC（如 1000=1 nC，150000=150 nC）。</summary>
        public double ChargePc { get; set; }
        /// <summary>V→pC：Q_pc = eng_V * CalibrationFactorLoc。</summary>
        public double CalibrationFactorLoc { get; set; }
        public double Range { get; set; }
        public double VelocityMPerUs { get; set; }
        public string Peaks { get; set; }
        /// <summary>calibration_plot ChannelsArray → eng_V（与 LOC 同源解码）。</summary>
        public double[] CalibrationPlotEng { get; set; }
        public double CalibrationPlotSampleRateHz { get; set; }
        /// <summary>peaks 字段解析：入射/反射样点索引。</summary>
        public int PeakIncidentSample { get; set; }
        public int PeakReflectionSample { get; set; }

        public DacCalibrationPoint()
        {
            Peaks = string.Empty;
            CalibrationPlotEng = new double[0];
            PeakIncidentSample = -1;
            PeakReflectionSample = -1;
        }
    }

    /// <summary>单次 measurement 的高速通道工程单位波形（ChannelsArray → eng）。</summary>
    public sealed class DacMeasurementWaveform
    {
        public long ContainerId { get; set; }
        public long MeasurementId { get; set; }
        public int Channel { get; set; }
        public double Gain { get; set; }
        public double Offset { get; set; }
        public int DeclaredSamples { get; set; }
        public double XincrementSeconds { get; set; }
        public double SampleRateHz { get; set; }
        /// <summary>eng = raw * Gain + Offset，单位伏特（ChannelsArray）。勿当作 LOC pC。</summary>
        public double[] EngineeringSamples { get; set; }
        /// <summary>containers.high_speed_data 原始 zlib 字节（可重放）；解码后保留，落盘 hs.zlib。</summary>
        public byte[] CompressedHsZlib { get; set; }
        /// <summary>绑定的 calibration_points.id；0=未绑定。</summary>
        public long CalibrationPointId { get; set; }
        /// <summary>该测次 V→pC 系数；≤0 表示未确认。</summary>
        public double CalibrationFactorLoc { get; set; }
        /// <summary>绑定校准点的 charge（pC）。</summary>
        public double CalibrationChargePc { get; set; }
        public double HvMaxKv { get; set; }
        public int Phase { get; set; }

        public DacMeasurementWaveform()
        {
            EngineeringSamples = new double[0];
            CompressedHsZlib = null;
            HvMaxKv = double.NaN;
        }
    }

    /// <summary>原软件 pd_map 一行（对照真值，不覆盖）。</summary>
    public sealed class DacPdMapEntry
    {
        public long Id { get; set; }
        public long ResultsId { get; set; }
        public long MeasurementId { get; set; }
        public double PdLocationM { get; set; }
        public double PdAmplitudeIec { get; set; }
        public double PdAmplitudeMax { get; set; }
        public int HighSpeedIndexOrig { get; set; }
        public int HighSpeedIndexRefl { get; set; }
        public double PdPhaseAngle { get; set; }
        /// <summary>0=未标终端类；2=远端/终端（样例位置常=电缆全长）。</summary>
        public int TerminationPdLoc { get; set; }
        public bool ReflectionFound { get; set; }
        public bool TerminationReflFound { get; set; }
        public double HvMaxKv { get; set; }

        public DacPdMapEntry()
        {
            HvMaxKv = double.NaN;
        }
    }

    /// <summary>单次 measurement：新定位结果 vs 原 pd_map 摘要（不伪造点）。</summary>
    public sealed class DacCompareSummary
    {
        public long MeasurementId { get; set; }
        public bool AnalyzerSuccess { get; set; }
        public string AnalyzerStatus { get; set; }
        public int NewRowCount { get; set; }
        public double[] NewPositionsM { get; set; }
        public double[] BaselinePositionsM { get; set; }
        public int BaselineCount { get; set; }
        public string Note { get; set; }
        /// <summary>校准换算状态（units=pC / eng_V_unconfirmed）。</summary>
        public string UnitStatus { get; set; }
        public double CalibrationFactorUsed { get; set; }
        public bool CalibrationScaled { get; set; }
        /// <summary>对照对齐后位置（±2% 投影 + excess&gt;L→L）；raw 仍在 NewPositionsM。</summary>
        public double[] AlignedPositionsM { get; set; }
        /// <summary>与 BaselinePositionsM 同行的 termination_pd_loc。</summary>
        public int[] BaselineTerminationCodes { get; set; }
        /// <summary>最近邻 |new_raw−pd_map|（m）；无则 NaN。</summary>
        public double NnRawM { get; set; }
        /// <summary>最近邻 |new_aligned−pd_map|（m）。</summary>
        public double NnAlignedM { get; set; }
        /// <summary>剔 terminal=2 后最近邻（体点齐位）。</summary>
        public double NnBodyM { get; set; }
        /// <summary>原远端(term=2)是否有对齐点落在 L±tol。</summary>
        public bool FarEndpointHit { get; set; }
        public string AlignNote { get; set; }

        public DacCompareSummary()
        {
            AnalyzerStatus = string.Empty;
            NewPositionsM = new double[0];
            BaselinePositionsM = new double[0];
            AlignedPositionsM = new double[0];
            BaselineTerminationCodes = new int[0];
            Note = string.Empty;
            UnitStatus = string.Empty;
            CalibrationFactorUsed = double.NaN;
            NnRawM = double.NaN;
            NnAlignedM = double.NaN;
            NnBodyM = double.NaN;
            AlignNote = string.Empty;
        }
    }
}
