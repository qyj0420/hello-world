
namespace Xiongxin.Owts.Contracts.Algorithms
{
    /// <summary>← PdLinearHighPass / PdLinearLowPass</summary>
    public interface ISignalFilter
    {
        string FilterId { get; }
        double[] ApplyHighPass10k(double[] raw, double sampleRateHz);
        double[] ApplyLowPass(double[] samples, double sampleIntervalSeconds, double cutoffMHz, int tapCount);
    }
}
