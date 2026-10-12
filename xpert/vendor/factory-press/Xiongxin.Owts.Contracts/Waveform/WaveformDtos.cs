
using System;

namespace Xiongxin.Owts.Contracts.Waveform
{
    /// <summary>单通道一段采样；加压 PD / 分压器分通道各一条。</summary>
    public sealed class WaveformSegment
    {
        public string SegmentId { get; set; }
        public double SampleRateHz { get; set; }
        public double[] Samples { get; set; }
        public short[] RawAdc { get; set; }
        public double FullScaleVolts { get; set; }
        public int PicoRangeIndex { get; set; }
        /// <summary>← LocationCycleReference.PdAcquisitionFilterId / PdLinearHighPass.FilterId</summary>
        public string PdAcquisitionFilterId { get; set; }
        public DateTime CapturedUtc { get; set; }

        public WaveformSegment()
        {
            SegmentId = string.Empty;
            Samples = new double[0];
            RawAdc = new short[0];
            PdAcquisitionFilterId = string.Empty;
            CapturedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>← LocationCycleReference / schema HVX.LocationCycles/1|2</summary>
    public sealed class CycleReferenceDto
    {
        public string Schema { get; set; }
        public double[] CrossingSeconds { get; set; }
        public bool FirstCrossingPositive { get; set; }
        public double SampleRateHz { get; set; }
        public int SampleCount { get; set; }
        public double CaptureStartSeconds { get; set; }
        public double SavedDurationSeconds { get; set; }
        public string PdAcquisitionFilterId { get; set; }

        public CycleReferenceDto()
        {
            Schema = "HVX.LocationCycles/2";
            CrossingSeconds = new double[0];
            PdAcquisitionFilterId = string.Empty;
        }
    }

    public sealed class PulseEventDto
    {
        public const int ParameterColumnCount = 36;
        public long LocationDataId { get; set; }
        public double PositionM { get; set; }
        public double ChargePc { get; set; }
        public double PhaseDegrees { get; set; }
        public double Amplitude { get; set; }
        public double WidthNs { get; set; }
        public double[] ParameterRow { get; set; }

        public PulseEventDto() { ParameterRow = new double[ParameterColumnCount]; }
    }

    public sealed class ParameterMatrixDto
    {
        public const int ColumnCount = 36;
        public int RowCount { get; set; }
        public double[] Values { get; set; } // row-major RowCount * 36

        public ParameterMatrixDto() { Values = new double[0]; }
    }

    public sealed class PrpdPointDto
    {
        public double PhaseDegrees { get; set; }
        public double ChargePc { get; set; }
        public double? PositionM { get; set; }
    }

    /// <summary>← Task6InputBundle</summary>
    public sealed class Task6InputBundleDto
    {
        public double[] VoltageRawVolts { get; set; }
        public double VoltageSampleRateHz { get; set; }
        public double[] PdRawVolts { get; set; }
        public double PdSampleRateHz { get; set; }
        public double PretriggerMicroseconds { get; set; }
        public double TransientSkipMicroseconds { get; set; }
        public int CurrentPdRangeIndex { get; set; }
        public double[] PdRangeValuesVolts { get; set; }

        public Task6InputBundleDto()
        {
            VoltageRawVolts = new double[0];
            PdRawVolts = new double[0];
            PdRangeValuesVolts = new double[0];
        }
    }
}
