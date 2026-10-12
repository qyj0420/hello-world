using System;
using System.Globalization;
using System.Text;
using HVX.Task15.Factors;
using Xiongxin.Owts.Ai;
using Xiongxin.Owts.Contracts.Ai;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Ui;
using Xiongxin.Owts.Contracts.Waveform;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// Task15 结论路径快照：因子骨架 + ONNX stub 特征 + 推理候选。
    /// OfficialConclusion 恒 false（缺完整 Task15/Task12）。供 DesktopUi 接线，不伪造成功结论。
    /// </summary>
    public sealed class ConclusionPathSnapshot
    {
        public ConclusionResult Conclusion { get; set; }
        public DefectRecognitionOutcome OnnxOutcome { get; set; }
        public OnnxModelStatus OnnxStatus { get; set; }
        public string FactorNote { get; set; }
        public string FeatureSchema { get; set; }
        public float[] FeatureVector { get; set; }
        public bool LevelTrendsApplied { get; set; }
        public bool OfficialConclusion { get; set; }
        public string SummaryLine { get; set; }

        public ConclusionPathSnapshot()
        {
            Conclusion = new ConclusionResult { Success = false };
            OnnxOutcome = null;
            OnnxStatus = new OnnxModelStatus();
            FactorNote = string.Empty;
            FeatureSchema = OnnxFeatureStubBuilder.SchemaVersion;
            FeatureVector = new float[0];
            OfficialConclusion = false;
            SummaryLine = string.Empty;
        }
    }

    /// <summary>
    /// 组装「定位已接受行 → Task15 因子骨架 → ONNX stub 特征 → 结论引擎」完整路径。
    /// 现网壳不改；Success 恒 false 直至完整 MultiFactorCore + Task12。
    /// </summary>
    public static class ConclusionPathEvaluator
    {
        public static ConclusionPathSnapshot Evaluate(
            IConclusionEngine engine,
            IDefectRecognizer recognizer,
            ParameterMatrixDto accepted,
            double voltageMinKv,
            double voltageMaxKv,
            float[] engOrPulseSamples,
            double[] pulseVoltageKv,
            double[] pulseChargeAbsPc,
            double[] pulsePhaseDeg,
            double[] pulseRealEnergy,
            string contextKey)
        {
            var snap = new ConclusionPathSnapshot();
            snap.FeatureSchema = OnnxFeatureStubBuilder.SchemaVersion;
            snap.OfficialConclusion = false;

            Task15FactorMath.FactorSkeletonResult skeleton = Task15FactorMath.BuildSkeletonFromAccepted(
                accepted, voltageMinKv, voltageMaxKv,
                pulseVoltageKv, pulseChargeAbsPc, pulsePhaseDeg, pulseRealEnergy);
            snap.FactorNote = skeleton != null ? skeleton.Note : Task15FactorMath.Provenance;
            snap.LevelTrendsApplied = skeleton != null && skeleton.LevelTrendsApplied;

            int featureLen = OnnxFeatureStubBuilder.DefaultFeatureLength;
            if (recognizer != null)
            {
                snap.OnnxStatus = recognizer.GetModelStatus() ?? new OnnxModelStatus();
                // Prefer model input length when shape ends with N
                featureLen = TryParseFeatureLength(snap.OnnxStatus.InputShape, featureLen);
            }
            else
            {
                snap.OnnxStatus = new OnnxModelStatus
                {
                    Detail = "IDefectRecognizer unset"
                };
            }

            snap.FeatureVector = OnnxFeatureStubBuilder.FromSamples(engOrPulseSamples, featureLen);

            if (recognizer != null && snap.OnnxStatus.FileExists)
            {
                var input = OnnxFeatureStubBuilder.BuildInput(
                    engOrPulseSamples, featureLen,
                    string.IsNullOrEmpty(contextKey) ? "conclusion-path" : contextKey);
                // Ensure vector matches what we recorded
                input.FeatureVector = snap.FeatureVector;
                snap.OnnxOutcome = recognizer.Recognize(input);
            }
            else if (recognizer != null)
            {
                snap.OnnxOutcome = recognizer.Recognize(new DefectRecognizerInput
                {
                    FeatureVector = snap.FeatureVector,
                    InputMode = DefectRecognizerInputModes.FeatureVector,
                    ContextKey = string.IsNullOrEmpty(contextKey) ? "conclusion-path" : contextKey
                });
            }

            var request = new ConclusionRequest
            {
                AcceptedParameters = accepted ?? new ParameterMatrixDto(),
                VoltageMinKv = voltageMinKv,
                VoltageMaxKv = voltageMaxKv,
                OnnxCandidate = snap.OnnxOutcome,
                PulseVoltageKv = pulseVoltageKv,
                PulseChargeAbsPc = pulseChargeAbsPc,
                PulsePhaseDeg = pulsePhaseDeg,
                PulseRealEnergy = pulseRealEnergy
            };

            if (engine != null)
                snap.Conclusion = engine.Evaluate(request) ?? new ConclusionResult { Success = false };
            else
            {
                snap.Conclusion = new ConclusionResult
                {
                    Success = false,
                    StatusMessage = "结论引擎未接线; ConclusionNotMigrated; " + snap.FactorNote
                };
            }

            // Hard guarantee: never claim official conclusion from this path.
            if (snap.Conclusion.Success)
            {
                snap.Conclusion.Success = false;
                snap.Conclusion.StatusMessage = (snap.Conclusion.StatusMessage ?? "")
                    + "; ForcedSuccessFalse=pathIncomplete(Task15/Task12);";
            }
            snap.OfficialConclusion = false;
            snap.SummaryLine = FormatSummary(snap);
            return snap;
        }

        static int TryParseFeatureLength(string inputShape, int fallback)
        {
            if (string.IsNullOrWhiteSpace(inputShape)) return fallback;
            // shapes like "1,8" or "[1,8]" — take last positive dim
            string s = inputShape.Trim().Trim('[', ']');
            string[] parts = s.Split(new[] { ',', 'x', 'X', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                int n;
                if (int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0)
                    return n;
            }
            return fallback;
        }

        static string FormatSummary(ConclusionPathSnapshot snap)
        {
            var sb = new StringBuilder();
            sb.Append(StatusCopy.BrandCompany);
            sb.Append(" · 结论路径 stub;");
            sb.Append(" OfficialConclusion=false;");
            sb.Append(" Success=").Append(snap.Conclusion != null && snap.Conclusion.Success ? "true" : "false");
            sb.Append("; levelTrends=").Append(snap.LevelTrendsApplied ? "1" : "0");
            sb.Append("; feature=").Append(OnnxFeatureStubBuilder.Describe(snap.FeatureVector));
            if (snap.OnnxStatus != null)
            {
                sb.Append("; onnxConfigured=").Append(snap.OnnxStatus.PathConfigured ? "1" : "0");
                sb.Append("; onnxExists=").Append(snap.OnnxStatus.FileExists ? "1" : "0");
                sb.Append("; onnxLoaded=").Append(snap.OnnxStatus.SessionLoaded ? "1" : "0");
            }
            if (snap.OnnxOutcome != null)
            {
                sb.Append("; onnxStatus=").Append(snap.OnnxOutcome.StatusCode ?? "");
                if (!string.IsNullOrEmpty(snap.OnnxOutcome.ClassLabel))
                    sb.Append("; class=").Append(snap.OnnxOutcome.ClassLabel);
            }
            return sb.ToString();
        }
    }
}
