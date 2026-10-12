using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        long pressureVoltageFeedbackSequence=-1;
        double pressureVoltageFeedbackKv=Double.NaN;
        string pressureVoltageFeedbackSource="";
        long pressureWorkerDisplaySequence=-1;
        string pressureWorkerDisplayStage="";
        long pressureVoltageReachedSequence=-1;
        public void ReportPressureWorkerStage(long sequence,string stage)
        {
            RunOnUiThread(delegate {
                lock(stateLock) {
                    if(sequence!=pressureRequestSequence||operatorPressureStopRequested||
                        (pressureStateCode!="RUNNING"&&pressureStateCode!="RAMPING"&&pressureStateCode!="HOLDING"))return;
                    pressureWorkerDisplaySequence=sequence;pressureWorkerDisplayStage=stage??"";
                    // This is a request-bound display acknowledgement only. The
                    // actual backend has completed HV UP; later save/analysis
                    // quality does not change whether this target was reached.
                    if(stage=="STEP_HV_UP_PASSED"&&requestedTargetPeakVoltageKv>0)
                        pressureVoltageReachedSequence=sequence;
                }
                UpdatePressureFeedbackFill(PressureStateCode);
            });
        }
        string PressureWorkerDisplayTitle()
        {
            if(pressureWorkerDisplaySequence!=pressureRequestSequence)return PressureUiText("正在准备","Preparing");
            switch(pressureWorkerDisplayStage) {
                case "STEP_HV_UP_RUNNING": return PressureUiText("正在升压","Ramping");
                case "STEP_ARM_RUNNING": case "STEP_ARM_DELAY_RUNNING":return PressureUiText("准备采集","Arming capture");
                case "STEP_HV_OFF_RUNNING":case "STEP_OPTICAL_RUNNING":case "STEP_WAIT_TRIGGER_RUNNING":case "STEP_READ_RUNNING":return PressureUiText("正在采集","Acquiring");
                case "STEP_DISCHARGE_RUNNING":case "STEP_FINAL_AI4_RUNNING":return PressureUiText("正在加压","Pressurizing");
                default:return PressureUiText("正在准备","Preparing");
            }
        }
        [Browsable(false)] public double CurrentPressureFeedbackKv
        { get { lock(stateLock)return pressureVoltageFeedbackSequence==pressureRequestSequence?pressureVoltageFeedbackKv:Double.NaN; } }

        // Display-only input. Never changes peak results, requests, permission,
        // output, action ownership or workflow completion.
        public void ReportPressureVoltageProgress(long sequence,double voltageKv,string source)
        {
            RunOnUiThread(delegate {
                lock(stateLock) {
                    if(sequence!=pressureRequestSequence||operatorPressureStopRequested||
                        (pressureStateCode!="RUNNING"&&pressureStateCode!="RAMPING"&&pressureStateCode!="HOLDING"))return;
                    pressureVoltageFeedbackSequence=sequence;
                    pressureVoltageFeedbackKv=IsFinite(voltageKv)&&voltageKv>=0?voltageKv:Double.NaN;
                    pressureVoltageFeedbackSource=source??"";
                }
                // The selected plan and all parameter/safety controls already
                // belong to this request. A display sample changes only its
                // measured fill, never the whole page or the selected grid row.
                UpdatePressureFeedbackFill(PressureStateCode);
            });
        }
    }
}
