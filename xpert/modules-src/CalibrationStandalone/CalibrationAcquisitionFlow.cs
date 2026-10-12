using System;

namespace Xiongxin.Calibration
{
    internal enum CalibrationAcquisitionStage { Idle, SelfCheck, Sampling, Review, Finished }

    // Only an explicit new acquisition can leave Finished. Persistence and redraws
    // are not permitted to issue further hardware capture commands.
    internal sealed class CalibrationAcquisitionFlow
    {
        internal CalibrationAcquisitionStage Stage { get; private set; }
        internal void Begin()
        {
            if(Stage!=CalibrationAcquisitionStage.Idle&&Stage!=CalibrationAcquisitionStage.Finished)
                throw new InvalidOperationException("本次采集尚未结束。");
            Stage=CalibrationAcquisitionStage.SelfCheck;
        }
        internal void RangeAndTriggerReady()
        {
            Require(CalibrationAcquisitionStage.SelfCheck);
            Stage=CalibrationAcquisitionStage.Sampling;
        }
        internal void RequireCapture(bool preflight)
        {
            Require(preflight?CalibrationAcquisitionStage.SelfCheck:CalibrationAcquisitionStage.Sampling);
        }
        internal void StopForReview()
        {
            Require(CalibrationAcquisitionStage.Sampling);
            Stage=CalibrationAcquisitionStage.Review;
        }
        internal void Finish(){Stage=CalibrationAcquisitionStage.Finished;}
        internal void ResumeForSupplement()
        {
            // Called only after the operator explicitly chooses another capture.
            Require(CalibrationAcquisitionStage.Review);
            Stage=CalibrationAcquisitionStage.Sampling;
        }
        void Require(CalibrationAcquisitionStage expected)
        {
            if(Stage!=expected)throw new InvalidOperationException("采集阶段不允许此操作："+Stage+"。");
        }
    }
}
