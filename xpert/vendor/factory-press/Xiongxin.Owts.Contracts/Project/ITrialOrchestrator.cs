
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.Owts.Contracts.Project
{
    /// <summary>← PressureTest / Calibration 流程编排；确认闸门禁止自动代签</summary>
    public interface ITrialOrchestrator
    {
        Task<TrialRoundResult> RunPressureRoundAsync(TrialRoundRequest request, CancellationToken ct);
        Task<CalibrationRoundResult> RunCalibrationRoundAsync(CalibrationRoundRequest request, CancellationToken ct);
        TrialSessionState GetState();
        void ConfirmOperatorGate(string gateId);
    }
}
