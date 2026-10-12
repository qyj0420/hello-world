using System;
namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {

        private readonly object actionLock = new object();
        private bool actionHandshakeEnabled, actionBusy, actionPending, actionClosed;
        private System.Windows.Forms.Timer busyVisualTimer;
        private bool busyVisualReady;
        private bool refreshingRoundLocks;
        private void UpdateActionBusyUiForRound()
        {
            if (refreshingRoundLocks) return;
            refreshingRoundLocks = true;
            try { UpdateActionBusyUi(); }
            finally { refreshingRoundLocks = false; }
        }
        private long actionSequence;
        private string actionCode = "", actionParametersJson = "", lastActionError = "";
        private bool lastActionSucceeded;
        // Parameter setters retain their original change-event order. A host
        // that acknowledges the mailbox should subscribe here: the immutable
        // sequence/code/parameters are now published and can be read coherently.
        public event EventHandler ActionPublished;
        [System.ComponentModel.DefaultValue(false)]
        public bool ActionHandshakeEnabled
        {
            get { lock (actionLock) return actionHandshakeEnabled; }
            set { RunOnUiThread(delegate {
                lock (actionLock) {
                    if (actionClosed) return;
                    if (actionBusy) throw new InvalidOperationException("Complete the pending action before changing handshake mode.");
                    actionHandshakeEnabled = value;
                }
                UpdateActionBusyUi();
            }); }
        }
        [System.ComponentModel.Browsable(false)] public bool IsBusy { get { lock (actionLock) return actionBusy; } }
        [System.ComponentModel.Browsable(false)] public bool ActionPending { get { lock (actionLock) return actionPending; } }
        [System.ComponentModel.Browsable(false)] public long ActionSequence { get { lock (actionLock) return actionSequence; } }
        [System.ComponentModel.Browsable(false)] public string ActionCode { get { lock (actionLock) return actionCode; } }
        [System.ComponentModel.Browsable(false)] public string ActionParametersJson { get { lock (actionLock) return actionParametersJson; } }
        [System.ComponentModel.Browsable(false)] public bool LastActionSucceeded { get { lock (actionLock) return lastActionSucceeded; } }
        [System.ComponentModel.Browsable(false)] public string LastActionError { get { lock (actionLock) return lastActionError; } }

        // One coherent, cached snapshot. Reading never consumes or acknowledges it.
        public bool ReadPendingAction(out long sequence, out string code, out string parametersJson)
        {
            lock (actionLock) {
                sequence = actionSequence; code = actionCode; parametersJson = actionParametersJson;
                return actionPending && !actionClosed;
            }
        }
        // The host owns execution. Complete only after the command/operation is finished.
        public bool CompleteAction(long sequence, bool succeeded, string error)
        {
            bool completed = false;
            RunOnUiThread(delegate {
                lock (actionLock) {
                    if (actionClosed || !actionPending || sequence != actionSequence) return;
                    actionPending = actionBusy = false;
                    lastActionSucceeded = succeeded;
                    lastActionError = succeeded ? "" : (error ?? "");
                    if (lastActionError.Length > 2048) lastActionError = lastActionError.Substring(0, 2048);
                    completed = true;
                }
                UpdateActionBusyUi();
                RefreshFinishCurrentRoundAfterAcknowledgement();
            });
            return completed;
        }
        private bool BeginAction()
        {
            lock (actionLock) {
                if (actionClosed) return false;
                if (!actionHandshakeEnabled) return true;
                if (actionBusy) return false;
                actionBusy = true;
                return true;
            }
        }
        private void PublishAction(string code)
        {
            if (!ActionHandshakeEnabled) return;
            string snapshot = CaptureActionParameters();
            lock (actionLock) {
                if (actionClosed) return;
                actionParametersJson = snapshot;
                actionCode = code;
                actionSequence = checked(actionSequence + 1);
                actionPending = true;
            }
            UpdateActionBusyUi();
            Raise(ActionPublished);
        }
        private void ExecuteParameterAction(string code, Action apply)
        {
            // Display changes must not replace or acknowledge the current
            // PRESSURE mailbox. They only change the view of existing data.
            if (IsDisplayAction(code) && (PressureParametersLocked || IsBusy)) { apply(); return; }
            if (PressureParametersLocked) return;
            if (!BeginAction()) return;
            parameterApplyDepth++;
            try { apply(); }
            finally { parameterApplyDepth--; PublishAction(code); }
        }
        private static bool IsDisplayAction(string code)
        {
            return code == "WAVEFORM_WINDOW" || code == "SPECTRUM" || code == "SPECTRUM_VIEW" ||
                code == "STATISTICS_FILTER" || code == "SELECTION_SHAPE" || code == "REGION_ACTION" ||
                code == "REGION_STATISTICS" || code == "REGION_UNDO" || code == "REGION_SELECTION" || code == "DISPLAY_VIEW";
        }
        private void CloseActionMailbox()
        {
            if (busyVisualTimer != null) { busyVisualTimer.Stop(); busyVisualTimer.Dispose(); busyVisualTimer = null; }
            lock (actionLock) {
                actionClosed = true;
                actionPending = actionBusy = false;
                actionParametersJson = lastActionError = "";
            }
        }
        private static string ActionJson(params object[] pairs)
        {
            var text = new System.Text.StringBuilder("{");
            for (int i = 0; i < pairs.Length; i += 2) {
                if (i != 0) text.Append(',');
                text.Append(JsonQuote((string)pairs[i])).Append(':');
                object value = pairs[i + 1];
                if (value is bool) text.Append((bool)value ? "true" : "false");
                else if (value is string[]) {
                    text.Append('[');
                    string[] values = (string[])value;
                    for (int k = 0; k < values.Length; k++) { if (k > 0) text.Append(','); text.Append(JsonQuote(values[k])); }
                    text.Append(']');
                }
                else if (value is string) text.Append(JsonQuote((string)value));
                else if (value == null) text.Append("null");
                else text.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            return text.Append('}').ToString();
        }
        private static string JsonQuote(string value)
        {
            var text = new System.Text.StringBuilder("\"");
            foreach (char c in value ?? "") {
                if (c == '\\' || c == '"') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4"));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }

        private readonly System.Collections.Generic.Dictionary<System.Windows.Forms.Control, bool> actionDisabledRoots =
            new System.Collections.Generic.Dictionary<System.Windows.Forms.Control, bool>();
        private string CaptureActionParameters()
        {
            lock (stateLock) {
                var chart = spectrumDashboardView.Review;
                bool[] shown = chart.PhaseVisibility;
                return ActionJson("locationAcquisitionId",cycleAcquisitionId,"locationSaveCycleCount",locationSaveCycleCount,
                    "phaseCode", selectedPhaseCode, "positivePolarity", positivePolarity,
                    "programMode", planMode ? "PLAN" : "CUSTOM", "advancedMode", advancedMode,
                    "automaticPostProcess", automaticPressureEnabled, "automaticRange", automaticRange,
                    "useCompensationCapacitance",useCompensationCapacitance,
                    "compensationCapacitanceUf",compensationCapacitanceUf,
                    "rawCapacitanceUf",metricCapacitanceAvailable?(object)RawEquivalentCapacitanceUf:null,
                    "displayedCapacitanceUf",IsDisplayedCapacitanceValid?(object)DisplayedEquivalentCapacitanceUf:null,
                    "rangeCode", selectedRangeCode, "baseU0VoltageKv", baseU0VoltageKv,
                    "picoRangeCode",SelectedPicoRangeCode<0?(object)null:SelectedPicoRangeCode,
                    "fullScaleVoltageV",SelectedPicoRangeCode<0?(object)null:SelectedFullScaleVoltageV,
                    "exactFullScalePc",SelectedPicoRangeCode<0?(object)null:SelectedExactFullScalePc,
                    "dischargeCalibrated",HasDischargeCalibrationNoLock(),
                    "calibrationPcPerVolt",HasDischargeCalibrationNoLock()?(object)calibrationPcPerVolt:null,
                    "calibrationSource",calibrationSource,"calibrationPhase",calibrationPhase,
                    "calibrationAcquiredUtc",HasDischargeCalibrationNoLock()?calibrationAcquiredUtc.ToString("O",System.Globalization.CultureInfo.InvariantCulture):null,
                    "calibrationSimulation",calibrationSimulation,
                    "cableLengthMeters", cableLengthMeters,
                    "customVoltageMode", customVoltageModeCode, "customVoltageValue", customVoltageValue,
                    "targetPeakVoltageKv", CalculateTargetPeakVoltageKvNoLock(),
                    "planStepIndex", selectedPlanStepIndex, "repetition", currentPlanRepetition,
                    "repetitionTotal", CurrentPlanRepetitionTotalNoLock(),
                    "waveformWindow", waveformWindowCode, "spectrumCode", selectedChannelCode,
                    "selectionShape", chart.SelectionShapeIndex, "regionActionCommand", chart.RegionActionCommand,
                    "selectionRegionCount", chart.SelectionRegionCount, "classificationEnabled", chart.ConfidenceClassificationEnabled,
                    "displayRecommendation",spectrumDashboardView.DisplayRecommendationJson,
                    "showPhaseA", shown[0], "showPhaseB", shown[1], "showPhaseC", shown[2]);
            }
        }
        private void UpdateActionBusyUi()
        {
            if (IsDisposed || Disposing) return;
            if(spectrumDashboardView!=null)spectrumDashboardView.SetAnalysisOptionsLocked(PressureControlsDisplayLocked||IsBusy);
            // The action mailbox blocks input immediately. Avoid repainting the
            // entire page disabled/enabled for a one-tick parameter acknowledgement.
            if (IsBusy && !PressureNavigationLocked && !busyVisualReady)
            {
                if (busyVisualTimer == null)
                {
                    busyVisualTimer = new System.Windows.Forms.Timer { Interval = 150 };
                    busyVisualTimer.Tick += delegate {
                        busyVisualTimer.Stop();
                        if (IsBusy) { busyVisualReady = true; UpdateActionBusyUi(); }
                    };
                }
                if (!busyVisualTimer.Enabled) busyVisualTimer.Start();
                return;
            }
            bool locked = PressureControlsDisplayLocked;
            if (!IsBusy && !locked)
            {
                if (busyVisualTimer != null) busyVisualTimer.Stop();
                busyVisualReady = false;
            }
            if (spectrumDashboardView != null) spectrumDashboardView.SetBusy(false);
            if (locked) {
                if (rangeMenu != null) rangeMenu.Close();
                LockActionBranches(this);
            } else {
                foreach (var entry in actionDisabledRoots)
                    if (!entry.Key.IsDisposed&&entry.Key.Enabled!=entry.Value) entry.Key.Enabled = entry.Value;
                actionDisabledRoots.Clear();
                UpdateRangeUi();
            }
            UpdatePressureUi();
            UpdatePressureNoiseInitializationUi();
        }
        // Disable sibling branches, never the root or any ancestor of the waveform.
        private void LockActionBranches(System.Windows.Forms.Control parent)
        {
            foreach (System.Windows.Forms.Control child in parent.Controls) {
                if (IsBusyViewControl(child)) continue;
                if ((CanStopCurrentPressure || CanFinishCurrentRound) && Object.ReferenceEquals(child, pressureActionButton)) { if(!child.Enabled)child.Enabled=true; continue; }
                if (ContainsBusyViewControl(child) ||
                    (CanStopCurrentPressure || CanFinishCurrentRound) && child.Contains(pressureActionButton))
                {
                    // The worker may become active after its mailbox first
                    // disabled this whole branch. Its Stop/Finish button now
                    // needs an enabled ancestor. Restore our previous branch
                    // lock before recording child states: Control.Enabled reads
                    // the effective parent state, not the child's own setting.
                    bool previousEnabled;
                    if (actionDisabledRoots.TryGetValue(child, out previousEnabled))
                    {
                        actionDisabledRoots.Remove(child);
                        if(child.Enabled!=previousEnabled)child.Enabled = previousEnabled;
                    }
                    LockActionBranches(child);
                }
                else {
                    if (!actionDisabledRoots.ContainsKey(child)) actionDisabledRoots.Add(child, child.Enabled);
                    if(child.Enabled)child.Enabled = false;
                }
            }
        }
        private bool IsBusyViewControl(System.Windows.Forms.Control control)
        {
            return Object.ReferenceEquals(control, pressureStartConfirmationPanel) ||
                Object.ReferenceEquals(control, highVoltageLabel) ||
                Object.ReferenceEquals(control, waveformView) || Object.ReferenceEquals(control, spectrumDashboardView) ||
                Object.ReferenceEquals(control, oneCycleButton) || Object.ReferenceEquals(control, twoCycleButton) ||
                Object.ReferenceEquals(control, maximumWindowButton) || control.Name == "WaveformWindowCycleInput" ||
                control.Name == "RealtimeMetricsTab" || control.Name == "RealtimeMoreMetricsTab" || control.Name == "RealtimeDisplayTab" ||
                control.Name == "CompactWaveformTab" || control.Name == "CompactSpectrumTab";
        }
        private bool ContainsBusyViewControl(System.Windows.Forms.Control control)
        {
            foreach (System.Windows.Forms.Control child in control.Controls)
                if (IsBusyViewControl(child) || ContainsBusyViewControl(child)) return true;
            return false;
        }
    }
}
