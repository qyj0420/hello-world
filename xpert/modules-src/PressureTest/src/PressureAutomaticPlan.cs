using System;
using System.Threading.Tasks;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        int issuedPlanStep=-1,issuedPlanRepetition;
        string issuedPlanProject="",issuedPlanPhase="";
        long continuedPressureSequence=-1;
        bool preparingPressureContinuation,automaticPlanInterrupted;
        // Presentation only: a completed round awaiting its already-issued
        // plan continuation must not repaint every parameter enabled/disabled.
        // This is deliberately NOT PressureParametersLocked: continuation and
        // all output authorization keep their existing independent guards.
        bool continuousPressureDisplayLock;

        bool HoldContinuousPressureDisplayNoLock()
        {
            if(!continuousPressureDisplayLock)return false;
            if(advancedMode||!planMode||!automaticPressureEnabled||finishCurrentRoundRequested||
                automaticPlanInterrupted||pressureEmergencyLatched||ProjectPath!=issuedPlanProject||
                selectedPhaseCode!=issuedPlanPhase||pressureStateCode=="FAULT"||pressureStateCode=="STOPPED"||
                postProcessStateCode=="SAVE_FAILED"||postProcessStateCode=="ANALYSIS_FAILED"||
                postProcessStateCode=="ACQUISITION_FAILED"||postProcessStateCode=="ACQUISITION_INVALID"||
                postProcessStateCode=="CANCELED"||postProcessStateCode=="OVERRANGE_REJECTED"||
                IsDisposed||Disposing||!Visible)
                continuousPressureDisplayLock=false;
            return continuousPressureDisplayLock;
        }
        void ReleaseContinuousPressureDisplay(long sequence)
        {
            lock(stateLock)if(sequence==pressureRequestSequence)continuousPressureDisplayLock=false;
            UpdateActionBusyUiForRound();
        }

        public void InterruptPressureContinuation()
        {
            lock(stateLock){automaticPlanInterrupted=true;finishCurrentRoundRequested=true;continuousPressureDisplayLock=false;pressurePlanRangeFloor.Reset();}
            UpdatePressureUi();UpdateActionBusyUiForRound();
        }

        void RememberIssuedPressurePlanNoLock()
        {
            issuedPlanStep=selectedPlanStepIndex;issuedPlanRepetition=currentPlanRepetition;
            issuedPlanProject=ProjectPath;issuedPlanPhase=selectedPhaseCode;automaticPlanInterrupted=false;
            continuousPressureDisplayLock=!advancedMode&&planMode&&automaticPressureEnabled&&
                (currentPlanRepetition<CurrentPlanRepetitionTotalNoLock()||selectedPlanStepIndex+1<planMultipliers.Length);
        }
        bool MayContinueIssuedPressurePlanNoLock()
        {
            return !advancedMode&&planMode&&!finishCurrentRoundRequested&&!automaticPlanInterrupted&&!pressureEmergencyLatched&&
                !IsBusy&&!PressureParametersLocked&&pressureStateCode=="COMPLETE"&&
                postProcessStateCode=="COMPLETE"&&Visible&&!IsDisposed&&!Disposing&&
                ProjectPath==issuedPlanProject&&selectedPhaseCode==issuedPlanPhase&&
                selectedPlanStepIndex==issuedPlanStep&&currentPlanRepetition==issuedPlanRepetition&&
                CalculateTargetPeakVoltageKvNoLock()==requestedTargetPeakVoltageKv&&
                (currentAcquisitionReferenceOnly?hasAcquisitionResult:dataSaved&&(BackgroundPressureAnalysisEnabled||dataAnalyzed));
        }
        // One serialized continuation per acknowledged acquisition. Failed save,
        // stop, unknown feedback or emergency never retries it. A queue-capable
        // host may advance after real save while managed analysis is pending.
        public async Task<bool> ContinueCompletedPressurePlanAsync()
        {
            // An explicit save adopts this completed segment only. It is not
            // permission to energize a subsequent round or adjust its range.
            if(CurrentRoundExplicitSave)return false;
            if(preparingPressureContinuation||continuedPressureSequence==pressureRequestSequence)return false;
            long sequence=pressureRequestSequence;
            preparingPressureContinuation=true;
            bool issued=false;
            try{
                if(!await WaitForPendingP04ReadAsync(System.Threading.CancellationToken.None))return false;
                // Manual operation also sees the R27 next-range advice, but
                // selecting a range is never a request to perform acquisition.
                // Zero-input captures already resolved their own whole-segment
                // range decision. They have no nonzero cycle-window review.
                if(!currentAcquisitionReferenceOnly&&!currentZeroVoltageAnalysis&&!await ReviewNextPressureRangeAsync())return false;
                if(!await WaitForPendingP04ReadAsync(System.Threading.CancellationToken.None))return false;
                lock(stateLock){
                    if(sequence!=pressureRequestSequence||!MayContinueIssuedPressurePlanNoLock())return false;
                    int nextStep=selectedPlanStepIndex,nextRepetition=currentPlanRepetition+1;
                    if(nextRepetition>CurrentPlanRepetitionTotalNoLock()){nextStep++;nextRepetition=1;}
                    if(nextStep<0||nextStep>=planMultipliers.Length){continuedPressureSequence=sequence;return false;}
                    double nextTarget=planVoltagesArePeak?planVoltagesKv[nextStep]:planVoltagesKv[nextStep]*Math.Sqrt(2);
                    if(!IsFinite(nextTarget)||nextTarget<0||nextTarget>maximumPeakVoltageKv||!CanRequestVoltageNoLock(nextTarget))return false;
                    continuedPressureSequence=sequence;
                    selectedPlanStepIndex=nextStep;currentPlanRepetition=nextRepetition;
                }
                SelectPlanGridRow(selectedPlanStepIndex);UpdatePressureUi();
                // The host may bind the current same-voltage repetitions to a
                // resident batch; changing voltage requires new authorization.
                dispatchingAutomaticPlanContinuation=true;
                try { issued=await RequestAutomaticNextPressureAsync();return issued; }
                finally { dispatchingAutomaticPlanContinuation=false; }
            }
            catch{InterruptPressureContinuation();throw;}
            finally{preparingPressureContinuation=false;if(!issued)ReleaseContinuousPressureDisplay(sequence);}
        }
    }
}
