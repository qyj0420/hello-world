
using System;

namespace Xiongxin.Owts.Contracts.Ai
{
    /// <summary>← DefectModelCatalog.Standard / Competition</summary>
    public static class DefectModelKinds
    {
        public const string Standard = "standard";
        public const string Competition = "competition";
    }

    public sealed class DefectModelOpenRequest
    {
        public string Kind { get; set; }
        public string CustomPath { get; set; }
        public string ExpectedSha256 { get; set; }

        public DefectModelOpenRequest()
        {
            Kind = DefectModelKinds.Standard;
            CustomPath = string.Empty;
            ExpectedSha256 = string.Empty;
        }
    }

    public sealed class DefectModelHandle
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
        /// <summary>PDM2（现网）或 ONNX（目标）</summary>
        public string Format { get; set; }
        public bool IsOpen { get; set; }

        public DefectModelHandle()
        {
            Path = string.Empty;
            Sha256 = string.Empty;
            Format = string.Empty;
        }
    }

    /// <summary>← RecognitionSnapshot</summary>
    public sealed class DefectRecognitionRequest
    {
        public int[] Phases { get; set; }
        public int[] Rows { get; set; }
        public double[] Angles { get; set; }
        public double[] Charges { get; set; }
        public string SourceScope { get; set; }
        public string ContextKey { get; set; }
        public bool English { get; set; }

        public DefectRecognitionRequest()
        {
            Phases = new int[0];
            Rows = new int[0];
            Angles = new double[0];
            Charges = new double[0];
            SourceScope = string.Empty;
            ContextKey = string.Empty;
        }
    }

    /// <summary>← DischargeRecognitionResult；StatusCode=ModelMissing 表示缺模</summary>
    public sealed class DischargeRecognitionResultDto
    {
        public const string StatusModelMissing = "ModelMissing";

        public bool Success { get; set; }
        public string DisplayLabel { get; set; }
        public string StatusCode { get; set; }
        public string StatusMessage { get; set; }
        public string LabelKeys { get; set; }
        public string ModelSha256 { get; set; }
        public string SourceScope { get; set; }
        public int PointCount { get; set; }
        public string Diagnostic { get; set; }
        public string CreatedUtc { get; set; }

        public DischargeRecognitionResultDto()
        {
            DisplayLabel = string.Empty;
            StatusCode = string.Empty;
            StatusMessage = string.Empty;
            LabelKeys = string.Empty;
            ModelSha256 = string.Empty;
            SourceScope = string.Empty;
            Diagnostic = string.Empty;
            CreatedUtc = DateTime.UtcNow.ToString("o");
        }

        public static DischargeRecognitionResultDto ModelMissing(string diagnostic)
        {
            return new DischargeRecognitionResultDto
            {
                Success = false,
                StatusCode = StatusModelMissing,
                StatusMessage = "缺陷识别模型不可用 / Defect model is not available.",
                DisplayLabel = string.Empty,
                Diagnostic = diagnostic ?? string.Empty,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };
        }
    }

    public sealed class LearningSampleRequest
    {
        public string LabelKeys { get; set; }
        public string OperatorName { get; set; }
        public string OperatorLabelText { get; set; }

        public LearningSampleRequest()
        {
            LabelKeys = string.Empty;
            OperatorName = string.Empty;
            OperatorLabelText = string.Empty;
        }
    }

    // ---------- ONNX IDefectRecognizer DTOs（现场推理，不训练）----------

    /// <summary>输入模式：脉冲窗原始采样 或 已提取特征向量。</summary>
    public static class DefectRecognizerInputModes
    {
        public const string PulseWindow = "pulse";
        public const string FeatureVector = "features";
    }

    public sealed class DefectRecognizerInput
    {
        /// <summary>脉冲窗采样（float）；与 FeatureVector 二选一优先 FeatureVector。</summary>
        public float[] PulseWindow { get; set; }
        /// <summary>特征向量；长度须匹配模型输入维（可 pad/truncate）。</summary>
        public float[] FeatureVector { get; set; }
        /// <summary><see cref="DefectRecognizerInputModes"/>。</summary>
        public string InputMode { get; set; }
        public string ContextKey { get; set; }

        public DefectRecognizerInput()
        {
            PulseWindow = new float[0];
            FeatureVector = new float[0];
            InputMode = DefectRecognizerInputModes.FeatureVector;
            ContextKey = string.Empty;
        }
    }

    /// <summary>ONNX 模型路径与会话状态（Host/UI 可读）。</summary>
    public sealed class OnnxModelStatus
    {
        public string ConfiguredPath { get; set; }
        public string ResolvedPath { get; set; }
        public bool PathConfigured { get; set; }
        public bool FileExists { get; set; }
        public bool SessionLoaded { get; set; }
        public string InputName { get; set; }
        public string OutputName { get; set; }
        public string InputShape { get; set; }
        public string Detail { get; set; }

        public OnnxModelStatus()
        {
            ConfiguredPath = string.Empty;
            ResolvedPath = string.Empty;
            InputName = string.Empty;
            OutputName = string.Empty;
            InputShape = string.Empty;
            Detail = string.Empty;
        }
    }

    /// <summary>IDefectRecognizer 输出：类别 + 置信度。OfficialDefectLabel 恒 false（禁止当真结论）。</summary>
    public sealed class DefectRecognitionOutcome
    {
        public const string StatusModelMissing = "ModelMissing";
        public const string StatusOk = "OnnxInferenceOk";
        public const string StatusInferenceError = "OnnxInferenceError";

        public bool Success { get; set; }
        public string StatusCode { get; set; }
        /// <summary>原始类别标签，形如 onnx_class_0；非缺陷类型名。</summary>
        public string ClassLabel { get; set; }
        public int ClassIndex { get; set; }
        public float Confidence { get; set; }
        public float[] RawScores { get; set; }
        public string ModelPath { get; set; }
        public string Diagnostic { get; set; }
        /// <summary>恒 false：ONNX 原始类不可当作 Task12/正式缺陷标签。</summary>
        public bool OfficialDefectLabel { get; set; }
        public string CreatedUtc { get; set; }

        public DefectRecognitionOutcome()
        {
            StatusCode = string.Empty;
            ClassLabel = string.Empty;
            ClassIndex = -1;
            Confidence = float.NaN;
            RawScores = new float[0];
            ModelPath = string.Empty;
            Diagnostic = string.Empty;
            OfficialDefectLabel = false;
            CreatedUtc = DateTime.UtcNow.ToString("o");
        }

        public static DefectRecognitionOutcome MissingModel(string configuredPath, string diagnostic)
        {
            return new DefectRecognitionOutcome
            {
                Success = false,
                StatusCode = StatusModelMissing,
                ClassLabel = string.Empty,
                ClassIndex = -1,
                Confidence = float.NaN,
                ModelPath = configuredPath ?? string.Empty,
                OfficialDefectLabel = false,
                Diagnostic = string.IsNullOrEmpty(diagnostic)
                    ? ("ONNX model missing; configuredPath=" + (configuredPath ?? "(empty)"))
                    : diagnostic,
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };
        }
    }
}
