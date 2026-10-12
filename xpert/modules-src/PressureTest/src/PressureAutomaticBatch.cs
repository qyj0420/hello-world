using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        // This identifies the scheduler's request only; it neither persists an
        // operator confirmation nor grants a device output permit.
        bool dispatchingAutomaticPlanContinuation;
        bool pressureRequestIsAutomaticContinuation;
        bool pressureBatchCaptureEnabled;
        int pressureRequestBatchRound=1, pressureRequestBatchStep=-1;
        double pressureRequestBatchTarget;
        [Browsable(false)] public bool PressureRequestIsAutomaticContinuation
        { get { lock (stateLock) return pressureRequestIsAutomaticContinuation; } }
        // Host capability only, not operator consent or a device permit.
        [Browsable(false)] public bool PressureBatchCaptureEnabled
        { get { lock(stateLock)return pressureBatchCaptureEnabled; }
          set { lock(stateLock)pressureBatchCaptureEnabled=value; } }
        void RecordPressureBatchRequestNoLock()
        {
            pressureRequestIsAutomaticContinuation=dispatchingAutomaticPlanContinuation;
            bool sameGroup=dispatchingAutomaticPlanContinuation&&pressureRequestBatchStep==selectedPlanStepIndex&&
                pressureRequestBatchTarget==requestedTargetPeakVoltageKv;
            pressureRequestBatchRound=sameGroup?checked(pressureRequestBatchRound+1):1;
            pressureRequestBatchStep=selectedPlanStepIndex;pressureRequestBatchTarget=requestedTargetPeakVoltageKv;
        }
    }
}
