
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.Contracts.Waveform
{
    /// <summary>纯编解码；不依赖 UI/设备/SQLite。← LocationCycleReference 头读写。</summary>
    public interface IWaveformCodec
    {
        WaveformSegment FromAcquisition(AcquisitionCycleResult cycle);
        CycleReferenceDto ParseLocHeader(string[] lines, int sampleCount, double sampleRateHz);
        /// <summary>前缀 "HVX_CycleReference\t" + JSON</summary>
        string FormatLocHeader(CycleReferenceDto reference);
        Task6InputBundleDto ToTask6Input(AcquisitionCycleResult cycle, AcquisitionSessionOptions cfg);
    }
}
