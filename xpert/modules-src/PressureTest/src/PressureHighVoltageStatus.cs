using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        bool highVoltageFeedbackKnown;
        [Browsable(false)] public bool IsHighVoltageReady { get { lock(stateLock) return IsHighVoltagePermissionNoLock(); } }
        [Browsable(false)] public string HighVoltageStatusCode { get { lock(stateLock) return simulationMode ? "SIMULATION" : !IsHighVoltageFeedbackKnownNoLock() ? "UNKNOWN" : IsHighVoltagePermissionNoLock() ? "STARTED" : "NOT_STARTED"; } }
        // The digital input describes emergency/interlock permission, not actual
        // HV output. Preserve the legacy status code for existing consumers.
        [Browsable(false)] public string EmergencyStopStatusCode
        { get { lock(stateLock) return simulationMode ? "SIMULATION" : pressureEmergencyLatched ? "ACTIVE_LATCHED" : HasPendingP04ReadNoLock() ? "PENDING" : !HasCurrentP04FeedbackNoLock() ? "UNKNOWN" : p04BitHigh ? "RELEASED" : "ACTIVE"; } }

        // Explicit operator acknowledgement only. It never starts or resumes a
        // queue and never treats a released emergency input as discharged HV.
        public bool AcknowledgePressureEmergency()
        {
            bool accepted=false;
            RunOnUiThread(()=>{
                lock(stateLock){
                    if(!pressureEmergencyLatched||PressureParametersLocked||IsBusy||!HasCurrentP04FeedbackNoLock()||!p04BitHigh)return;
                    pressureEmergencyLatched=false;finishCurrentRoundRequested=true;advancedMode=true;accepted=true;
                }
                CancelAutomaticPressureSchedulingCore();UpdateAdvancedUi();ApplyP04FeedbackToUi();UpdatePressureUi();
            });return accepted;
        }
        void ShowPressureEmergencyAcknowledgement()
        {
            if(!pressureEmergencyLatched||PressureParametersLocked||IsBusy)return;
            string text=PressureUiText("本轮因急停已停止，不会自动恢复加压。\r\n请由合格人员按现场规程隔离设备、放电、独立验电并接地。解除急停不代表试品已安全。\r\n\r\n是否已完成现场安全处置并结束本轮？",
                "This round stopped after an emergency stop. Voltage will not resume automatically.\r\nQualified personnel must isolate, discharge, independently verify voltage and ground the equipment under site procedures. Releasing the stop does not prove the test object is safe.\r\n\r\nHas site safety handling been completed, and should this round be ended?");
            if(System.Windows.Forms.MessageBox.Show(FindForm(),text,PressureUiText("急停处置确认","Emergency acknowledgement"),System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Warning,System.Windows.Forms.MessageBoxDefaultButton.Button2)==System.Windows.Forms.DialogResult.Yes)
                if(!AcknowledgePressureEmergency())System.Windows.Forms.MessageBox.Show(FindForm(),
                    PressureUiText("设备状态尚未确认。请保持高压停止，按现场规程检查设备；如仍无法继续，请联系维护人员。",
                        "The equipment state is not confirmed. Keep high voltage stopped and check the equipment under site procedures. If you still cannot continue, contact support."),
                    PressureUiText("急停仍锁存","Emergency remains latched"),System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Warning);
        }

        // Compatibility closure input only. An untimed positive report cannot
        // establish permission; use timestamped physical ReportP04Status.
        // This does not issue a command or imply that the cable is discharged.
        public void ReportHighVoltageState(bool connected, bool highVoltageStarted)
        {
            if (!connected || !highVoltageStarted) { ResetP04FeedbackSession(); return; }
            RunOnUiThread(ApplyP04FeedbackToUi);
        }

        void ApplyHighVoltageStateCore(bool connected, bool highVoltageStarted,bool preservePendingSchedule=false)
        {
            bool changed;
            lock(stateLock) {
                changed = highVoltageFeedbackKnown != connected || (connected && highVoltageClosed == highVoltageStarted);
                highVoltageFeedbackKnown = connected;
                highVoltageClosed = !connected || !highVoltageStarted;
            }
            if (changed) { if(!preservePendingSchedule)CancelAutomaticPressureSchedulingCore(); UpdatePressureUi(); }
        }
        bool CanRequestVoltageNoLock(double peakKv)
        { return !PressureNoiseInitializationBlocksStart && (simulationMode || (IsManualZeroInputNoLock(peakKv) ? !pressureEmergencyLatched : IsHighVoltagePermissionNoLock())); }
        // An explicit manual 0 kV request owns AI/Pico inputs only. Unknown or
        // pressed DI is not an acquisition prerequisite and is never an output permit.
        bool IsManualZeroInputNoLock(double peakKv) { return peakKv==0 && advancedMode; }

        void UpdateHighVoltageStatusUi()
        {
            UpdateAdvancedUi();
            if (highVoltageLabel == null) return;
            string state = EmergencyStopStatusCode;
            bool latchedPhysicalKnown=false,latchedPhysicalHigh=false;
            lock(stateLock) {
                if(state=="ACTIVE_LATCHED") {
                    latchedPhysicalKnown=HasCurrentLatchedP04DisplayNoLock()||HasCurrentP04FeedbackNoLock();
                    latchedPhysicalHigh=HasCurrentLatchedP04DisplayNoLock()?p04LatchedDisplayHigh:p04BitHigh;
                }
            }
            highVoltageLabel.Text = state == "SIMULATION" ? PressureUiText("仿真输出", "Simulated output") :
                state == "ACTIVE_LATCHED" ? !latchedPhysicalKnown ? PressureUiText("● 反馈待确认 · 本轮已锁存","● Feedback unconfirmed · fault latched") :
                    latchedPhysicalHigh ? PressureUiText("● 急停已解除 · 本轮仍锁存","● Stop released · fault remains latched") :
                    PressureUiText("● 急停已按下 · 本轮已锁存","● Stop pressed · fault latched") :
                state == "RELEASED" ? PressureUiText("● 急停已解除", "● Stop released") :
                state == "ACTIVE" ? PressureUiText("● 急停已按下 · 加压禁用", "● Stop pressed · HV disabled") :
                state == "PENDING" ? PressureUiText("● 等待急停回读 · 加压禁用", "● Waiting for stop input · HV disabled") :
                PressureUiText("● 急停反馈待确认 · 加压禁用", "● Stop feedback unconfirmed · HV disabled");
            highVoltageLabel.BackColor = state=="ACTIVE_LATCHED" ? !latchedPhysicalKnown ? PressureTestTheme.SecondaryInk :
                latchedPhysicalHigh ? PressureTestTheme.Danger : PressureTestTheme.Success :
                state=="RELEASED" ? PressureTestTheme.Danger : state=="ACTIVE" ? PressureTestTheme.Success : PressureTestTheme.SecondaryInk;
            highVoltageLabel.ForeColor=System.Drawing.Color.White;
            highVoltageLabel.Cursor=state=="ACTIVE_LATCHED"?System.Windows.Forms.Cursors.Hand:System.Windows.Forms.Cursors.Default;
        }
    }
}
