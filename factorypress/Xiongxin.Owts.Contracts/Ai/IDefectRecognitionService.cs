
using System;

namespace Xiongxin.Owts.Contracts.Ai
{
    /// <summary>
    /// ← ReviewTask12Runtime / DefectModelCatalog / PdReviewControl.RecognizeCurrentDischarge。
    /// 缺模必须返回 StatusCode=ModelMissing，禁止静默成功或竞赛→标准回落。
    /// </summary>
    public interface IDefectRecognitionService : IDisposable
    {
        DefectModelHandle OpenModel(DefectModelOpenRequest request);
        DischargeRecognitionResultDto Recognize(DefectRecognitionRequest request);
        event EventHandler<DischargeRecognitionResultDto> RecognitionCompleted;
        bool TryAddLearningSample(LearningSampleRequest request, out string message);
    }
}
