using System;
using Xiongxin.Owts.Contracts.Ai;

namespace Xiongxin.Owts.Ai
{
    /// <summary>编译占位。加压不调用缺陷模型。</summary>
    public static class OnnxFeatureStubBuilder
    {
        public const int DefaultFeatureLength = 16;
        public const string SchemaVersion = "onnx-feature-stub";

        public static float[] FromSamples(float[] samples, int length)
        {
            int n = length > 0 ? length : DefaultFeatureLength;
            var vector = new float[n];
            if (samples == null || samples.Length == 0) return vector;
            int copy = Math.Min(n, samples.Length);
            Array.Copy(samples, vector, copy);
            return vector;
        }

        public static string Describe(float[] vector)
        {
            return SchemaVersion + " len=" + (vector == null ? 0 : vector.Length);
        }

        public static DefectRecognizerInput BuildInput(float[] samples, int length, string contextKey)
        {
            return new DefectRecognizerInput
            {
                FeatureVector = FromSamples(samples, length),
                InputMode = DefectRecognizerInputModes.FeatureVector,
                ContextKey = contextKey ?? string.Empty
            };
        }
    }

    public sealed class OnnxDefectRecognizer : IDefectRecognizer
    {
        readonly string _path;

        public OnnxDefectRecognizer(string modelPath)
        {
            _path = modelPath ?? string.Empty;
        }

        public OnnxModelStatus GetModelStatus()
        {
            return new OnnxModelStatus
            {
                ConfiguredPath = _path,
                PathConfigured = !string.IsNullOrWhiteSpace(_path),
                FileExists = false,
                SessionLoaded = false,
                Detail = "模型运行时不在出厂加压包内。"
            };
        }

        public DefectRecognitionOutcome Recognize(DefectRecognizerInput input)
        {
            return DefectRecognitionOutcome.MissingModel(_path, "模型运行时不在出厂加压包内。");
        }

        public void Dispose() { }
    }

    public sealed class OnnxBackedDefectRecognitionService : IDefectRecognitionService
    {
        readonly IDefectRecognizer _recognizer;

        public OnnxBackedDefectRecognitionService(IDefectRecognizer recognizer)
        {
            _recognizer = recognizer;
        }

        public event EventHandler<DischargeRecognitionResultDto> RecognitionCompleted;

        public DefectModelHandle OpenModel(DefectModelOpenRequest request)
        {
            return new DefectModelHandle { IsOpen = false, Format = "missing" };
        }

        public DischargeRecognitionResultDto Recognize(DefectRecognitionRequest request)
        {
            var result = DischargeRecognitionResultDto.ModelMissing("加压路径不加载缺陷模型。");
            var handler = RecognitionCompleted;
            if (handler != null) handler(this, result);
            return result;
        }

        public bool TryAddLearningSample(LearningSampleRequest request, out string message)
        {
            message = "学习样本不在出厂加压包内。";
            return false;
        }

        public void Dispose()
        {
            if (_recognizer != null) _recognizer.Dispose();
        }
    }
}
