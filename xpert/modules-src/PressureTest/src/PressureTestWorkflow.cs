using System;
using System.ComponentModel;
using System.Globalization;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        bool overRangeReminderEnabled = true;
        bool deleteEnabled;
        bool hasAcquisitionResult;
        bool lastAcquisitionOverRange;
        bool overRangeIgnored;
        bool dataSaved;
        bool dataAnalyzed;
        int consecutiveOverRangeCount;
        string postProcessStateCode = "READY";
        string lastPostProcessError = String.Empty;
        // Enabled by a host that owns a saved-record analysis queue. Legacy
        // consumers keep their explicit analysis acknowledgement contract.
        [Browsable(false)] public bool BackgroundPressureAnalysisEnabled { get; set; }

        long saveRequestSequence;
        long analysisRequestSequence;
        long overRangeDecisionRequestSequence;
        long repressureRequestSequence;
        long postProcessStateChangedSequence;
        long overRangeReminderChangedSequence;

        public event EventHandler AutomaticPostProcessChanged;
        public event EventHandler OverRangeReminderChanged;
        public event EventHandler SaveRequested;
        public event EventHandler AnalysisRequested;
        public event EventHandler OverRangeDecisionRequested;
        public event EventHandler RepressureRequested;
        public event EventHandler PostProcessStateChanged;

        [Category("自动后处理")]
        [DefaultValue(true)]
        public bool AutomaticPostProcessEnabled
        {
            get { lock (stateLock) return automaticPressureEnabled; }
            set { SetAutomaticPostProcess(value); }
        }

        [Category("自动后处理")]
        [DefaultValue(true)]
        public bool OverRangeReminderEnabled
        {
            get { lock (stateLock) return overRangeReminderEnabled; }
            set { SetOverRangeReminder(value); }
        }

        [Browsable(false)] public string PostProcessStateCode { get { lock (stateLock) return postProcessStateCode; } }
        [Browsable(false)] public int ConsecutiveOverRangeCount { get { lock (stateLock) return consecutiveOverRangeCount; } }
        [Browsable(false)] public bool OverRangeDecisionPending { get { lock (stateLock) return postProcessStateCode == "OVERRANGE_DECISION_REQUIRED"; } }
        [Browsable(false)] public bool DeleteEnabled { get { lock (stateLock) return deleteEnabled; } }
        [Browsable(false)] public bool LastAcquisitionOverRange { get { lock (stateLock) return lastAcquisitionOverRange; } }
        [Browsable(false)] public bool OverRangeIgnored { get { lock (stateLock) return overRangeIgnored; } }
        [Browsable(false)] public bool DataSaved { get { lock (stateLock) return dataSaved; } }
        [Browsable(false)] public bool DataAnalyzed { get { lock (stateLock) return dataAnalyzed; } }
        [Browsable(false)] public string LastPostProcessError { get { lock (stateLock) return lastPostProcessError; } }

        [Browsable(false)] public long AutomaticPostProcessChangedSequence { get { lock (stateLock) return automaticPressureChangedSequence; } }
        [Browsable(false)] public long OverRangeReminderChangedSequence { get { lock (stateLock) return overRangeReminderChangedSequence; } }
        [Browsable(false)] public long SaveRequestSequence { get { lock (stateLock) return saveRequestSequence; } }
        [Browsable(false)] public long AnalysisRequestSequence { get { lock (stateLock) return analysisRequestSequence; } }
        [Browsable(false)] public long OverRangeDecisionRequestSequence { get { lock (stateLock) return overRangeDecisionRequestSequence; } }
        [Browsable(false)] public long RepressureRequestSequence { get { lock (stateLock) return repressureRequestSequence; } }
        [Browsable(false)] public long PostProcessStateChangedSequence { get { lock (stateLock) return postProcessStateChangedSequence; } }

        public void SetAutomaticPostProcess(bool enabled)
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                bool changed;
                bool stateChanged;
                lock (stateLock)
                {
                    if (postProcessStateCode == "SAVE_REQUESTED")
                    { lastActionCode = "AUTO_SAVE_TOGGLE_BLOCKED_SAVING"; return; }
                    changed = automaticPressureEnabled != enabled;
                    if (!changed)
                    {
                        stateChanged = false;
                    }
                    else
                    {
                        automaticPressureEnabled = enabled;
                        automaticPressureChangedSequence++;
                        // This preference is for future saves only. Never erase
                        // the saved/deletable state of the current acquisition.
                        stateChanged = !hasAcquisitionResult && SetPostProcessStateNoLock(enabled ? "READY" : "MANUAL");
                        lastActionCode = enabled ? "AUTO_SAVE_ON" : "AUTO_SAVE_OFF";
                    }
                }
                UpdateAutomaticPressureUi();
                UpdatePostProcessUi();
                if (stateChanged) Raise(PostProcessStateChanged);
                if (changed)
                {
                    Raise(AutomaticPressureChanged);
                    Raise(AutomaticPostProcessChanged);
                }
            });
        }

        public void SetOverRangeReminder(bool enabled)
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                bool changed;
                lock (stateLock)
                {
                    changed = overRangeReminderEnabled != enabled;
                    overRangeReminderEnabled = enabled;
                    if (changed)
                    {
                        overRangeReminderChangedSequence++;
                        lastActionCode = enabled ? "OVERRANGE_REMINDER_ON" : "OVERRANGE_REMINDER_OFF";
                    }
                }
                if (changed) Raise(OverRangeReminderChanged);
            });
        }

        public bool ReportAcquisitionCompleted(bool overRange)
        {
            bool accepted = false;
            bool raiseSave = false;
            bool raiseDecision = false;
            bool stateChanged = false;
            RunOnUiThread(delegate
            {
                // Zero-voltage noise and reference previews are not PD rounds.
                // Old/external completion callbacks must not promote them into
                // the save/analysis chain or change their persistence evidence.
                if (operatorPressureStopRequested || currentAcquisitionReferenceOnly || currentAcquisitionReferencePreview) return;
                if (RejectInvalidRangeEvaluation() || RejectIncompleteLocationAcquisition()) return;
                lock (stateLock)
                {
                    if(postProcessStateCode!="READY"&&postProcessStateCode!="WAITING_FOR_ACQUISITION"&&
                        !(postProcessStateCode=="MANUAL"&&!automaticPressureEnabled))return;
                    if (!automaticPressureEnabled && (!overRange || automaticRange))
                    {
                        hasAcquisitionResult = true;
                        lastAcquisitionOverRange = overRange;
                        dataSaved = false; dataAnalyzed = false; deleteEnabled = false;
                        accepted=true;
                        lastActionCode = "ACQUISITION_REPORTED_MANUAL";
                        SetPostProcessStateNoLock("AWAITING_MANUAL_SAVE");
                        UpdatePostProcessUi();
                        return;
                    }
                    if (postProcessStateCode != "READY" && postProcessStateCode != "WAITING_FOR_ACQUISITION")
                        return;

                    accepted = true;
                    hasAcquisitionResult = true;
                    lastAcquisitionOverRange = overRange;
                    overRangeIgnored = false;
                    dataSaved = false;
                    dataAnalyzed = false;
                    deleteEnabled = false;
                    lastPostProcessError = String.Empty;
                    lastError = String.Empty;

                    if (!overRange)
                    {
                        consecutiveOverRangeCount = 0;
                        stateChanged = SetPostProcessStateNoLock("SAVE_REQUESTED");
                        saveRequestSequence++;
                        lastActionCode = "AUTO_SAVE_REQUESTED";
                        raiseSave = true;
                    }
                    else
                    {
                        // Auto-save is not authorization to repeat an HV cycle
                        // or to silently save clipped acquisition as normal.
                        consecutiveOverRangeCount++;
                        stateChanged = SetPostProcessStateNoLock("OVERRANGE_DECISION_REQUIRED");
                        overRangeDecisionRequestSequence++;
                        lastActionCode = "OVERRANGE_DECISION_REQUIRED";
                        raiseDecision = true;
                    }
                }
                UpdatePostProcessUi();
                if (stateChanged) Raise(PostProcessStateChanged);
                if (raiseDecision) Raise(OverRangeDecisionRequested);
                if (raiseSave) Raise(SaveRequested);
            });
            return accepted;
        }

        public bool ResolveOverRangeDecision(bool ignoreOverRange)
        {
            bool accepted = false;
            bool raiseSave = false;
            bool stateChanged = false;
            RunOnUiThread(delegate
            {
                if (currentAcquisitionReferenceOnly || currentAcquisitionReferencePreview) return;
                lock (stateLock)
                {
                    if (postProcessStateCode != "OVERRANGE_DECISION_REQUIRED") return;
                    accepted = true;
                    deleteEnabled = false;
                    dataSaved = false;
                    dataAnalyzed = false;
                    lastPostProcessError = String.Empty;
                    lastError = String.Empty;
                    if (ignoreOverRange)
                    {
                        overRangeIgnored = true;
                        consecutiveOverRangeCount = 0;
                        stateChanged = SetPostProcessStateNoLock("SAVE_REQUESTED");
                        saveRequestSequence++;
                        lastActionCode = "IGNORE_OVERRANGE";
                        raiseSave = true;
                    }
                    else
                    {
                        overRangeIgnored = false;
                        stateChanged = SetPostProcessStateNoLock("OVERRANGE_REJECTED");
                        lastActionCode = "OVERRANGE_NOT_SAVED";
                        lastPostProcessError = "数据超量程，未保存；重新采集须由操作人员启动。";
                    }
                }
                UpdatePostProcessUi();
                if (stateChanged) Raise(PostProcessStateChanged);
                if (raiseSave) Raise(SaveRequested);
            });
            return accepted;
        }

        public bool CompleteSave(bool success, string errorMessage)
        {
            bool accepted = false;
            bool stateChanged = false;
            RunOnUiThread(delegate
            {
                if (currentAcquisitionReferenceOnly || currentAcquisitionReferencePreview) return;
                lock (stateLock)
                {
                    if (postProcessStateCode != "SAVE_REQUESTED") return;
                    accepted = true;
                    if (success)
                    {
                        dataSaved = true;
                        lastPostProcessError = String.Empty;
                        lastError = String.Empty;
                        // A queue-capable host admits only the committed file.
                        // Saving, not analysis completion, is this round's gate;
                        // all device/cadence/range checks still precede a start.
                        dataAnalyzed = false;
                        deleteEnabled = true;
                        stateChanged = SetPostProcessStateNoLock(BackgroundPressureAnalysisEnabled ? "COMPLETE" : "ANALYSIS_REQUESTED");
                        analysisRequestSequence++;
                        lastActionCode = "AUTO_SAVE_COMPLETE";
                        if(CurrentRoundManualOverRangeWarning){
                            lastActionCode="MANUAL_OVERRANGE_SAVED_WARNING";
                            lastPostProcessError=ManualOverRangeWarningText();maxDischargePc=Double.NaN;locationConfidencePercent=Double.NaN;
                        }
                    }
                    else
                    {
                        dataSaved = false;
                        dataAnalyzed = false;
                        deleteEnabled = false;
                        lastPostProcessError = String.IsNullOrWhiteSpace(errorMessage) ? "保存失败" : errorMessage.Trim();
                        lastError = lastPostProcessError;
                        stateChanged = SetPostProcessStateNoLock("SAVE_FAILED");
                        lastActionCode = "AUTO_SAVE_FAILED";
                    }
                }
                if(accepted&&success&&CurrentRoundManualOverRangeWarning){ShowPressureRangeReview(lastPressureCapture.RangeReview,ManualOverRangeWarningText());UpdateMetricsUi();}
                UpdatePostProcessUi();
                if (stateChanged) Raise(PostProcessStateChanged);
                if(accepted&&success)Raise(AnalysisRequested);
            });
            return accepted;
        }

        public bool CompleteAnalysis(bool success, string errorMessage)
        {
            bool accepted = false;
            bool stateChanged = false;
            RunOnUiThread(delegate
            {
                if (currentAcquisitionReferenceOnly || currentAcquisitionReferencePreview) return;
                lock (stateLock)
                {
                    if (postProcessStateCode != "ANALYSIS_REQUESTED") return;
                    accepted = true;
                    if (success)
                    {
                        dataAnalyzed = true;
                        deleteEnabled = true;
                        lastPostProcessError = CurrentRoundManualOverRangeWarning?ManualOverRangeWarningText():String.Empty;
                        lastError = String.Empty;
                        stateChanged = SetPostProcessStateNoLock("COMPLETE");
                        lastActionCode = overRangeIgnored ? "AUTO_COMPLETE_OVERRANGE_IGNORED" : "AUTO_COMPLETE";
                    }
                    else
                    {
                        dataAnalyzed = false;
                        deleteEnabled = false;
                        lastPostProcessError = String.IsNullOrWhiteSpace(errorMessage) ? "分析失败" : errorMessage.Trim();
                        lastError = lastPostProcessError;
                        stateChanged = SetPostProcessStateNoLock("ANALYSIS_FAILED");
                        lastActionCode = "AUTO_ANALYSIS_FAILED";
                    }
                }
                UpdatePostProcessUi();
                if (stateChanged) Raise(PostProcessStateChanged);
            });
            return accepted;
        }

        public bool CompleteBackgroundPressureAnalysis(string acquisitionId, bool success, string errorMessage)
        {
            bool accepted = false;
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    // Never acknowledge or rewrite a newer capture mailbox.
                    if (!BackgroundPressureAnalysisEnabled || String.IsNullOrWhiteSpace(acquisitionId) ||
                        acquisitionId != cycleAcquisitionId || !dataSaved || IsBusy || HasActiveHardwareSession ||
                        postProcessStateCode != "COMPLETE" || pressureStateCode != "COMPLETE" ||
                        currentAcquisitionReferenceOnly || currentAcquisitionReferencePreview) return;
                    accepted = true;
                    dataAnalyzed = success;
                    if (!success) backgroundPressureAnalysisError = String.IsNullOrWhiteSpace(errorMessage) ? "后台分析未完成" : errorMessage.Trim();
                }
                UpdatePostProcessUi();
            });
            return accepted;
        }

        public string[] ReadPostProcessState()
        {
            lock (stateLock)
            {
                return new[]
                {
                    postProcessStateCode,
                    automaticPressureEnabled ? "AUTO" : "MANUAL",
                    overRangeReminderEnabled ? "REMIND" : "DO_NOT_REMIND",
                    consecutiveOverRangeCount.ToString(CultureInfo.InvariantCulture),
                    !hasAcquisitionResult ? "NOT_REPORTED" : (lastAcquisitionOverRange ? "OVERRANGE" : "IN_RANGE"),
                    overRangeIgnored ? "OVERRANGE_IGNORED" : "NOT_IGNORED",
                    deleteEnabled ? "DELETE_ENABLED" : "DELETE_DISABLED",
                    dataSaved ? "SAVED" : "NOT_SAVED",
                    dataAnalyzed ? "ANALYZED" : "NOT_ANALYZED",
                    lastPostProcessError
                };
            }
        }

        public bool TryRequestDelete()
        {
            if(endpointInspectionBusy)return false;
            bool requested = false;
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    if (IsBusy) return;
                    if (!deleteEnabled || !dataSaved || HasActiveHardwareSession || IsPressureRequestBlockedNoLock())
                    {
                        lastActionCode = "DELETE_BLOCKED";
                        return;
                    }
                    if (!BeginAction()) return;
                    deleteRequestSequence++;
                    lastActionCode = "DELETE";
                    requested = true;
                }
                if (requested) { PublishAction("DELETE"); Raise(DeleteRequested); }
            });
            return requested;
        }

        bool PreparePostProcessForPressureNoLock()
        {
            spectrumDashboardView.SetEndpointReviewAvailable(false);
            ResetManualRoundSave();
            deleteEnabled = false;
            hasAcquisitionResult = false;
            lastAcquisitionOverRange = false;
            overRangeIgnored = false;
            dataSaved = false;
            dataAnalyzed = false;
            lastPostProcessError = String.Empty;
            lastError = String.Empty;
            return SetPostProcessStateNoLock("WAITING_FOR_ACQUISITION");
        }

        bool RejectInvalidRangeEvaluation()
        {
            var source=lastPressureCapture;
            // Only the current installed real/synthetic capture may supply this
            // quality. Legacy state-only completion APIs keep their contract.
            if(source==null||source.Quality==null||source.Quality.WindowValid||
                !cycleIdentityBound||!cycleIdentityAccepted||lastPressureAcquisitionId!=cycleAcquisitionId||
                !Object.ReferenceEquals(source.ChargePc,lastDischargePc))return false;
            RecordPressureDiagnostic("RangeWindowInvalid",source.Quality.WindowDefinition+"; "+source.Quality.Reason);
            string message=PressureUiText("无法评估本轮数据，未保存为有效试验。请检查采集设置后重新开始。",
                "This round could not be evaluated and was not saved as a valid test. Check the acquisition settings, then start again.");
            lock(stateLock){
                hasAcquisitionResult=true;dataSaved=dataAnalyzed=deleteEnabled=overRangeIgnored=false;
                lastPostProcessError=message;lastActionCode="RANGE_WINDOW_INVALID";
                // Capture/release/ACK already completed. A missing calculation
                // window does not retrospectively declare a hardware fault.
                SetPostProcessStateNoLock("ACQUISITION_INVALID");
            }
            InterruptPressureContinuation();UpdatePostProcessUi();
            Raise(PostProcessStateChanged);
            return true;
        }

        bool IsPressureRequestBlockedNoLock()
        {
            return postProcessStateCode == "SAVE_REQUESTED" ||
                postProcessStateCode == "ANALYSIS_REQUESTED" ||
                postProcessStateCode == "OVERRANGE_DECISION_REQUIRED";
        }

        public void RequestSaveCurrentRound()
        {
            if(endpointInspectionBusy)return;
            RunOnUiThread(delegate {
                if(operatorPressureStopRequested||IsBusy||!hasAcquisitionResult||dataSaved||currentAcquisitionReferenceOnly||
                    currentAcquisitionReferencePreview||IsPressureRequestBlockedNoLock()||RejectInvalidRangeEvaluation()||RejectIncompleteLocationAcquisition())return;
                lock(stateLock){
                    lastPostProcessError="";lastActionCode="MANUAL_SAVE_REQUESTED";
                    manualExplicitSaveAcquisition=cycleAcquisitionId;
                    // The explicit Save button adopts the current completed
                    // segment, not a new pressure request. A manual risk segment
                    // still needs the current identity and safety guards.
                    bool manualRisk=lastAcquisitionOverRange&&!automaticRange;
                    if(manualRisk&&!AdoptCurrentManualOverRangeForSave())return;
                    SetPostProcessStateNoLock(lastAcquisitionOverRange&&!overRangeIgnored?"OVERRANGE_DECISION_REQUIRED":"SAVE_REQUESTED");
                    if(postProcessStateCode=="SAVE_REQUESTED")saveRequestSequence++;else overRangeDecisionRequestSequence++;
                }
                UpdatePostProcessUi();Raise(PostProcessStateChanged);
                if(OverRangeDecisionPending)Raise(OverRangeDecisionRequested);else Raise(SaveRequested);
            });
        }

        public bool CompleteCurrentRoundDeleted(string acquisitionId)
        {
            bool accepted=false;
            RunOnUiThread(()=>{
                lock(stateLock){
                    if(acquisitionId!=cycleAcquisitionId||!dataSaved||HasActiveHardwareSession||ActionCode!="DELETE"||!ActionPending)return;
                    dataSaved=dataAnalyzed=deleteEnabled=false;lastActionCode="CURRENT_ROUND_DELETED";lastPostProcessError="";
                    SetPostProcessStateNoLock("AWAITING_MANUAL_SAVE");accepted=true;
                }
                if(accepted){spectrumDashboardView.ClearData();spectrumDashboardView.SetEndpointReviewAvailable(false);UpdatePostProcessUi();Raise(PostProcessStateChanged);}
            });
            return accepted;
        }

        bool SetPostProcessStateNoLock(string stateCode)
        {
            if (String.Equals(postProcessStateCode, stateCode, StringComparison.Ordinal)) return false;
            postProcessStateCode = stateCode;
            postProcessStateChangedSequence++;
            return true;
        }
    }
}
