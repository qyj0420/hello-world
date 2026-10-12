
namespace Xiongxin.Owts.Contracts.Algorithms
{
    /// <summary>← HVX.Task17.Location.ProjectLocationAnalyzer / ReviewAnalysisEngine</summary>
    public interface ILocationAnalyzer
    {
        LocationAnalysisResult AnalyzeIncremental(LocationAnalysisRequest request);
    }
}
