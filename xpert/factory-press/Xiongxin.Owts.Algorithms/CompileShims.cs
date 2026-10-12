using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Waveform;

namespace Xiongxin.Owts.Algorithms
{
    /// <summary>编译占位。不实现 E+ / Task17。定位公式仍在 Xpert 壳内。</summary>
    public sealed class SimplifiedLocationAnalyzer : ILocationAnalyzer
    {
        public LocationAnalysisResult AnalyzeIncremental(LocationAnalysisRequest request)
        {
            return new LocationAnalysisResult
            {
                Success = false,
                Status = "E+ 公式不在出厂加压包内；由 Xpert 定位模块计算。"
            };
        }
    }

    public sealed class Task17LocationAdapter : ILocationAnalyzer
    {
        readonly ILocationAnalyzer _inner;

        public Task17LocationAdapter(ILocationAnalyzer inner)
        {
            _inner = inner ?? new SimplifiedLocationAnalyzer();
        }

        public LocationAnalysisResult AnalyzeIncremental(LocationAnalysisRequest request)
        {
            return _inner.AnalyzeIncremental(request);
        }
    }

    public sealed class SeventhEvidenceEvaluator : ISeventhEvidenceEvaluator
    {
        public SeventhPipelineResultDto Evaluate(SeventhPipelineInputDto input)
        {
            return new SeventhPipelineResultDto { Available = false, AutoAccept = false };
        }
    }

    public sealed class PdLinearSignalFilter : ISignalFilter
    {
        public string FilterId { get { return "shim-not-press-path"; } }

        public double[] ApplyHighPass10k(double[] raw, double sampleRateHz)
        {
            return raw ?? new double[0];
        }

        public double[] ApplyLowPass(double[] samples, double sampleIntervalSeconds, double cutoffMHz, int tapCount)
        {
            return samples ?? new double[0];
        }
    }

    public sealed class NotImplementedConclusionEngine : IConclusionEngine
    {
        public ConclusionResult Evaluate(ConclusionRequest request)
        {
            return new ConclusionResult
            {
                Success = false,
                StatusMessage = "结论引擎不在出厂加压包内。"
            };
        }
    }
}

namespace HVX.Task15.Factors
{
    public static class Task15FactorMath
    {
        public const string Provenance = "compile-shim";

        public sealed class FactorSkeletonResult
        {
            public string Note { get; set; }
            public bool LevelTrendsApplied { get; set; }

            public FactorSkeletonResult()
            {
                Note = string.Empty;
            }
        }

        public static FactorSkeletonResult BuildSkeletonFromAccepted(
            ParameterMatrixDto accepted,
            double voltageMinKv,
            double voltageMaxKv,
            double[] pulseVoltageKv,
            double[] pulseChargeAbsPc,
            double[] pulsePhaseDeg,
            double[] pulseRealEnergy)
        {
            return new FactorSkeletonResult { Note = Provenance, LevelTrendsApplied = false };
        }
    }
}
