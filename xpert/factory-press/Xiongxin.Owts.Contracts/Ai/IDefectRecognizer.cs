using System;

namespace Xiongxin.Owts.Contracts.Ai
{
    /// <summary>
    /// 现场 ONNX 推理接口（不训练）：脉冲窗或特征向量 → 类别索引 + 置信度。
    /// 与 <see cref="IDefectRecognitionService"/>（Task12/PDM2 形状）并列；缺模必须 Success=false。
    /// </summary>
    public interface IDefectRecognizer : IDisposable
    {
        /// <summary>当前配置的模型路径与加载状态（可读，供 DesktopUi/Host）。</summary>
        OnnxModelStatus GetModelStatus();

        /// <summary>
        /// 推理。文件缺失 → StatusCode=ModelMissing；有模型则跑 ONNX。
        /// 输出类别为原始索引标签（onnx_class_N），禁止当作正式缺陷结论。
        /// </summary>
        DefectRecognitionOutcome Recognize(DefectRecognizerInput input);
    }
}
