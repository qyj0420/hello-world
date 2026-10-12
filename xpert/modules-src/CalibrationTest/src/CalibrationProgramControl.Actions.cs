using System;
namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {

        private readonly object actionLock = new object();
        private bool actionHandshakeEnabled, actionBusy, actionPending, actionClosed;
        private bool calibrationStopRequested;
        private bool CanStopCalibration { get { return !hardwareCleanupInProgress && (HasPendingHardwareCleanup || (hostAcquisitionActive && activeAcquisition != null && !calibrationStopRequested)); } }
        private string CalibrationBusyCaption
        {
            get {
                if (calibrationEntryCheckActive) return CalibrationText("正在确认急停状态…", "Checking emergency-stop state…");
                if (hardwareCleanupInProgress) return CalibrationText("正在释放设备…", "Releasing devices…");
                if (HasPendingHardwareCleanup) return CalibrationText("设备释放未完成\n点击重试释放", "Device release incomplete\nSelect to retry release");
                if (hostAcquisitionActive && calibrationStopRequested) return CalibrationText("正在停止采集…\n等待设备释放", "Stopping acquisition…\nWaiting for release");
                if (CanStopCalibration) return CalibrationText("正在采集 · 点击停止", "Acquiring · Select to stop");
                return CalibrationText("忙碌中 · 请稍候", "Busy · Please wait");
            }
        }
        private async void RequestCalibrationStopFromUi()
        {
            if (!CanStopCalibration) return;
            if (HasPendingHardwareCleanup)
            {
                try { await RetryHardwareCleanupAsync(); }
                catch (Exception error) {
                    WriteCalibrationDiagnostic("Calibration release did not complete: "+error);
                    if(!IsDisposed&&!Disposing)System.Windows.Forms.MessageBox.Show(FindForm(),
                        CalibrationText("设备释放未完成，请检查设备状态后重试。", "Device release is incomplete. Check the device state and retry."),
                        CalibrationText("设备释放", "Device release"),System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Warning);
                }
                return;
            }
            calibrationStopRequested = true;
            activeAcquisition.HostCancel();
            Invalidate();
            AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.StateChange, -1);
        }
        private long actionSequence;
        private string actionCode = "", actionParametersJson = "", lastActionError = "";
        private bool lastActionSucceeded;
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
        [System.ComponentModel.Browsable(false)] public bool IsBusy { get { lock (actionLock) return actionBusy || HardwareOperationBusy; } }
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
                if (completed && succeeded && String.Equals(actionCode, "START", StringComparison.Ordinal) &&
                    IsHandleCreated && Visible && FindForm() != null && FindForm().Visible && RequiresPhaseVelocityReview(SelectedPhaseIndex))
                    ShowPhaseVelocityReview();
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
        }
        private void ExecuteParameterAction(string code, Action apply)
        {
            if (!BeginAction()) return;
            try { apply(); }
            finally { PublishAction(code); }
        }
        private void CloseActionMailbox()
        {
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

        private string CaptureActionParameters()
        {
            lock (stateLock) {
                PhaseState phase = GetPhaseStateNoLock(selectedPhaseIndex);
                return ActionJson("phaseIndex", selectedPhaseIndex, "levelCode", selectedLevelCode,
                    "allLevels", calibrateAllLevels, "levelCodes", calibrateAllLevels ? LevelCodes : new[] { selectedLevelCode },
                    "cableLengthMeters", phase.CableLengthMeters, "velocityMPerUs", phase.VelocityMPerUs,
                    "shortCable", phase.ShortCable, "bandwidthEnhancement", phase.WideBand,
                    "bandwidthCutoffMHz", phase.BandwidthCutoffMHz, "advancedMode", advancedMode,
                    "simulation", SimulationMode, "firstArrivalTiming", UseFirstArrivalTiming);
            }
        }
        private void UpdateActionBusyUi()
        {
            if (IsDisposed || Disposing) return;
            if (IsBusy) { CancelNumericEditor(); DisposeJointMenu(); Capture = false; }
            Invalidate();
        }
        private void PaintActionBusy(System.Drawing.Graphics graphics)
        {
            if (!IsBusy) return;
            var bounds = UseCompactLayout ? System.Drawing.Rectangle.Round(CompactLayout.Start) :
                ReferenceRectangleToClient(new System.Drawing.RectangleF(1398, 796, 243, 94));
            using (var brush = new System.Drawing.SolidBrush(CanStopCalibration ? Theme.Danger : System.Drawing.Color.FromArgb(96, 117, 138)))
                graphics.FillRectangle(brush, bounds);
            System.Windows.Forms.TextRenderer.DrawText(graphics, CalibrationBusyCaption, Font,
                bounds, System.Drawing.Color.White, System.Windows.Forms.TextFormatFlags.HorizontalCenter |
                System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.WordBreak);
        }
    }
}
