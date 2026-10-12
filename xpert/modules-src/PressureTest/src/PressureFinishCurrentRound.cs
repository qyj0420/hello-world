using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        bool finishCurrentRoundRequested;
        bool operatorPressureStopRequested;
        long finishCurrentRoundRequestSequence;
        string interruptedPressureConfiguration;
        internal Func<DialogResult> InterruptedPressureChoiceForTests;

        string PressureRestartConfiguration()
        {
            // Exact values, not rounded captions. A changed project, phase,
            // mode, list or selection starts a new operator-selected sequence.
            lock(stateLock)return String.Join("|",new[]{ProjectPath,selectedPhaseCode,planMode.ToString(),advancedMode.ToString(),
                selectedPlanStepIndex.ToString(CultureInfo.InvariantCulture),currentPlanRepetition.ToString(CultureInfo.InvariantCulture),
                CalculateTargetPeakVoltageKvNoLock().ToString("R",CultureInfo.InvariantCulture),
                String.Join(",",planVoltagesKv.Select(x=>x.ToString("R",CultureInfo.InvariantCulture))),
                String.Join(",",planRepetitions.Select(x=>x.ToString(CultureInfo.InvariantCulture)))});
        }
        bool ConfirmInterruptedPressureRestart()
        {
            if(interruptedPressureConfiguration==null)return true;
            if(interruptedPressureConfiguration!=PressureRestartConfiguration()){interruptedPressureConfiguration=null;return true;}
            // Only an explicit new start may reach this choice. The automatic
            // scheduler remains revoked, and normal HV checks follow it.
            var choice=InterruptedPressureChoiceForTests!=null?InterruptedPressureChoiceForTests():MessageBox.Show(FindForm(),
                PressureUiText("上次试验已中断。\r\n是：从中断的计划步骤重新加压。\r\n否：从计划第一步重新开始。\r\n取消：暂不加压。", "The previous test was interrupted.\r\nYes: repeat the interrupted step.\r\nNo: restart the plan from its first step.\r\nCancel: do not start."),
                PressureUiText("继续试验", "Continue test"),MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question,MessageBoxDefaultButton.Button3);
            if(choice==DialogResult.Cancel)return false;
            if(interruptedPressureConfiguration!=PressureRestartConfiguration())return false;
            if(choice!=DialogResult.Yes&&choice!=DialogResult.No)return false;
            if(choice==DialogResult.No&&planMode){
                lock(stateLock){selectedPlanStepIndex=planVoltagesKv.Length>0?0:-1;currentPlanRepetition=1;}
                SelectPlanGridRow(SelectedPlanStepIndex);RaiseVoltageSettingChanged();
            }
            interruptedPressureConfiguration=null;return true;
        }
        public event EventHandler FinishCurrentRoundRequested;
        [Browsable(false)] public bool FinishCurrentRoundPending
        { get { lock (stateLock) return finishCurrentRoundRequested && CurrentPressureRoundPendingNoLock(); } }
        [Browsable(false)] public bool AutomaticPressureStopped
        { get { lock (stateLock) return finishCurrentRoundRequested; } }
        [Browsable(false)] public long FinishCurrentRoundRequestSequence
        { get { lock (stateLock) return finishCurrentRoundRequestSequence; } }
        [Browsable(false)] public bool PressureNavigationLocked
        { get { lock (stateLock) return IsPressureNoiseInitializing || endpointInspectionBusy || CurrentPressureRoundPendingNoLock() || AutomaticPressureCountdownPending; } }
        [Browsable(false)] public bool PressureParametersLocked
        { get { lock (stateLock) return IsPressureNoiseInitializing || endpointInspectionBusy || CurrentPressureRoundPendingNoLock() || AutomaticPressureCountdownPending || IsBusy && parameterApplyDepth == 0; } }
        bool PressureControlsDisplayLocked
        { get { lock(stateLock)return PressureParametersLocked||HoldContinuousPressureDisplayNoLock(); } }
        int parameterApplyDepth;
        [Browsable(false)] public bool CanStopCurrentPressure
        { get { return HasActiveHardwareSession; } }
        [Browsable(false)] public bool CanFinishCurrentRound
        { get { lock (stateLock) return AutomaticPressureCountdownPending || HoldContinuousPressureDisplayNoLock() ||
            (!advancedMode || finishCurrentRoundRequested || !IsBusy && !HasActiveHardwareSession &&
                (postProcessStateCode=="OVERRANGE_DECISION_REQUIRED"||postProcessStateCode=="WAITING_FOR_ACQUISITION")) && CurrentPressureRoundPendingNoLock(); } }

        bool CurrentPressureRoundPendingNoLock()
        {
            return IsBusy && ActionPending && ActionCode == "PRESSURE" || HasActiveHardwareSession ||
                pressureStateCode == "RUNNING" || pressureStateCode == "RAMPING" || pressureStateCode == "HOLDING" ||
                postProcessStateCode == "WAITING_FOR_ACQUISITION" || postProcessStateCode == "SAVE_REQUESTED" ||
                postProcessStateCode == "ANALYSIS_REQUESTED" || postProcessStateCode == "OVERRANGE_DECISION_REQUIRED";
        }

        // Stop future scheduling only. The current owner still reports its
        // actual acquisition, save and mailbox result without a fabricated ACK.
        public bool RequestFinishCurrentRound()
        {
            bool accepted = false;
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    if (!AutomaticPressureCountdownPending && !CanFinishCurrentRound) return;
                    finishCurrentRoundRequested = true;
                    continuousPressureDisplayLock = false;
                    finishCurrentRoundRequestSequence++;
                    // Stopping future work is a latch, not a mode change.
                    // Preserve the operator's plan and AUTO/MANUAL selection.
                    lastActionCode = "FINISH_CURRENT_ROUND_REQUESTED";
                    accepted = true;
                }
                CancelCapturePreparationForSafety();
                CancelAutomaticPressureSchedulingCore();
                CancelIdlePressureDecisionForStop();
                UpdateAdvancedUi(); UpdateRangeUi(); UpdatePressureUi(); UpdateActionBusyUi();
                Raise(FinishCurrentRoundRequested);
            });
            return accepted;
        }
        // A stopped/invalidated decision is not an active device operation.
        // Never fabricate a hardware or save ACK: only abandon an unaccepted
        // decision after the actual owner and action mailbox have both ended.
        void CancelIdlePressureDecisionForStop()
        {
            bool changed=false;
            lock(stateLock) {
                if(HasActiveHardwareSession||IsBusy||ActionPending||
                    !(finishCurrentRoundRequested||automaticPlanInterrupted||pressureEmergencyLatched)||
                    pressureStateCode!="COMPLETE"&&pressureStateCode!="STOPPED")return;
                if(postProcessStateCode!="OVERRANGE_DECISION_REQUIRED"&&postProcessStateCode!="WAITING_FOR_ACQUISITION")return;
                pressureStateCode="STOPPED";
                dataSaved=dataAnalyzed=deleteEnabled=false;overRangeIgnored=false;
                lastActionCode="IDLE_PRESSURE_DECISION_CANCELED";
                lastPostProcessError=PressureUiText("后续加压已停止；未接受的数据仅保留原始记录。","Future pressure stopped; unaccepted data retained as raw evidence only.");
                changed=SetPostProcessStateNoLock("CANCELED");
            }
            if(changed){UpdatePostProcessUi();UpdateActionBusyUi();Raise(PostProcessStateChanged);}
        }
        void PressureActionClicked()
        {
            if (CanStopCurrentPressure) { RequestCurrentPressureStop(); return; }
            if (CanFinishCurrentRound) { RequestFinishCurrentRound(); return; }
            operatorPressureClickInProgress=true;
            try { RequestPressure(); }
            finally { operatorPressureClickInProgress=false; }
        }
        // Cancel the existing owner, never issue an SDK write or fabricate its
        // termination. The owner and its safety checks decide the final state.
        public bool RequestCurrentPressureStop()
        {
            bool requested=false;
            RunOnUiThread(delegate {
                if(!HasActiveHardwareSession)return;
                ReportPressureOperatorStop(ActionSequence);
                lock(stateLock) {
                    interruptedPressureConfiguration=PressureRestartConfiguration();
                    operatorPressureStopRequested=true;
                    finishCurrentRoundRequested=true;
                    finishCurrentRoundRequestSequence++;
                    automaticPlanInterrupted=true;
                    continuousPressureDisplayLock=false;
                    lastActionCode="PRESSURE_STOP_REQUESTED";
                }
                CancelCapturePreparationForSafety();
                CancelAutomaticPressureSchedulingCore();
                // Revoke automatic authorization even if cancellation loses a
                // race with completion. A second click must never queue a start.
                Raise(FinishCurrentRoundRequested);
                requested=RequestHardwareStop();
                UpdateAdvancedUi();UpdateRangeUi();UpdatePressureUi();UpdateActionBusyUi();
            });
            return requested;
        }
        void ResetFinishCurrentRound()
        { lock (stateLock) { finishCurrentRoundRequested = false; operatorPressureStopRequested=false; } }
        void RefreshFinishCurrentRoundAfterAcknowledgement()
        {
            if (!AutomaticPressureStopped || !IsHandleCreated || IsDisposed || Disposing) return;
            // The owned input worker releases its lease just after ACK.
            try { BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing) { UpdatePressureUi(); UpdateActionBusyUi(); } }); }
            catch (InvalidOperationException) { }
        }
    }
}
