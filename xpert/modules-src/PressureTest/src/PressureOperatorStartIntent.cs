using System;
using System.Globalization;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        // UI intent only. It neither sets electrical permission nor acknowledges
        // a worker/SDK operation. Direct RequestPressure calls do not create it.
        bool operatorPressureClickInProgress, operatorPressureIntentConsumed;
        long operatorPressureIntentSequence;
        string operatorPressureIntentContext="", operatorPressurePlanContext="";

        string CaptureOperatorPressurePlanContextNoLock()
        {
            return ProjectPath+"|"+selectedPhaseCode+"|"+positivePolarity+"|"+planMode+"|"+advancedMode+"|"+
                maximumPeakVoltageKv.ToString("R",CultureInfo.InvariantCulture)+"|"+planVoltagesArePeak+"|"+
                String.Join(",",Array.ConvertAll(planVoltagesKv,v=>v.ToString("R",CultureInfo.InvariantCulture)))+"|"+
                String.Join(",",planRepetitions)+"|"+calibrationSource+"|"+calibrationAcquiredUtc.ToString("O")+"|"+
                calibrationPcPerVolt.ToString("R",CultureInfo.InvariantCulture)+"|"+simulationMode;
        }
        void RecordOperatorPressureIntentNoLock()
        {
            string plan=CaptureOperatorPressurePlanContextNoLock();
            bool inherited=dispatchingAutomaticPlanContinuation&&operatorPressureIntentConsumed&&
                operatorPressureIntentSequence==pressureRequestSequence-1&&operatorPressurePlanContext==plan;
            bool accepted=operatorPressureClickInProgress||inherited||HasReviewedRangeStartIntentNoLock();
            // Keep the original explicit start across retries and repetitions;
            // a fresh operator start begins a new row-range history.
            if(operatorPressureClickInProgress)operatorPressureStartEpoch=pressureRequestSequence;
            else if(!accepted||!inherited&&!dispatchingReviewedRangeRetry)operatorPressureStartEpoch=0;
            operatorPressureIntentSequence=accepted?pressureRequestSequence:0;
            operatorPressureIntentContext=accepted?CaptureAutomaticPressureContext():"";
            operatorPressurePlanContext=accepted?plan:"";
            operatorPressureIntentConsumed=false;
            BindPressurePlanRangeRowNoLock();
        }
        public bool IsPressureOperatorStartIntentCurrent(long sequence,double targetKv)
        {
            lock(stateLock)return sequence>0&&sequence==pressureRequestSequence&&sequence==operatorPressureIntentSequence&&
                targetKv==requestedTargetPeakVoltageKv&&targetKv==CalculateTargetPeakVoltageKvNoLock()&&
                operatorPressureIntentContext==CaptureAutomaticPressureContext()&&operatorPressurePlanContext==CaptureOperatorPressurePlanContextNoLock()&&
                !finishCurrentRoundRequested&&!automaticPlanInterrupted&&!pressureEmergencyLatched&&
                !IsDisposed&&!Disposing&&Visible&&IsHandleCreated&&ActionPending&&ActionCode=="PRESSURE"&&CanRequestVoltageNoLock(targetKv);
        }
        public bool TryConsumePressureOperatorStartIntent(long sequence,double targetKv)
        {
            bool accepted=false;
            RunOnUiThread(delegate {
                lock(stateLock) {
                    if(operatorPressureIntentConsumed||!IsPressureOperatorStartIntentCurrent(sequence,targetKv))return;
                    operatorPressureIntentConsumed=true;accepted=true;
                }
            });
            return accepted;
        }
        // Same-action 0 kV range retries are authorized only by the preceding
        // recorded range choice, after its capture delegate returned successfully.
        public bool TryConsumePressureCaptureIntent(long sequence,double targetKv)
        {
            if(TryConsumePressureOperatorStartIntent(sequence,targetKv))return true;
            bool accepted=false;
            RunOnUiThread(delegate {lock(stateLock)lock(zeroHardwareLock) {
                var owner=zeroActionOwner;
                if(targetKv!=0||sequence!=pressureRequestSequence||sequence!=operatorPressureIntentSequence||
                    !operatorPressureIntentConsumed||operatorPressurePlanContext!=CaptureOperatorPressurePlanContextNoLock()||
                    owner==null||owner.Sequence!=ActionSequence||owner.RetryPermitRange<0||
                    owner.RetryPermitRange!=SelectedPicoRangeCode||owner.Cancellation.IsCancellationRequested||
                    !ActionPending||ActionCode!="PRESSURE"||pressureEmergencyLatched||automaticPlanInterrupted||finishCurrentRoundRequested||
                    !Visible||!IsHandleCreated||!CanRequestVoltageNoLock(0))return;
                owner.RetryPermitRange=-1;owner.DispatchCommitted=false;
                operatorPressureIntentContext=CaptureAutomaticPressureContext();accepted=true;
            }});
            return accepted;
        }
    }
}
