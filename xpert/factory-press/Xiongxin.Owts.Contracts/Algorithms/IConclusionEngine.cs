
namespace Xiongxin.Owts.Contracts.Algorithms
{
    /// <summary>← ConclusionAlgorithm Task15（确定性多因子，非 NN）</summary>
    public interface IConclusionEngine
    {
        ConclusionResult Evaluate(ConclusionRequest request);
    }
}
