using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private void VerifyMainToolbarVisible()
        {
            foreach(var control in new System.Windows.Forms.Control[]{start,continuous,repeatCount,remainingCount,automaticRange,stop,calibrate,saveData,export,openFolder,reset})
            {
                var bounds=control.RectangleToScreen(control.ClientRectangle);
                for(var parent=control.Parent;parent!=null && parent!=this;parent=parent.Parent)
                {
                    if(!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                        throw new Exception("顶部控件被裁切："+control.Text+" / "+parent.GetType().Name+" "+parent.ClientRectangle+" / "+bounds);
                    var scroll=parent as System.Windows.Forms.ScrollableControl;
                    if(scroll!=null && (scroll.HorizontalScroll.Visible || scroll.VerticalScroll.Visible))
                        throw new Exception("顶部控件出现滚动条："+control.Text);
                }
            }
            if(notice.Text.Contains("\n"))throw new Exception("流程介绍必须单行。");
        }

        private async Task SmokeIntegratedKernel(string directory)
        {
            Directory.CreateDirectory(directory);
            var s=ReadSettings();s.Simulation=true;s.TargetKv=1;s.Input.RequestedPicoRateHz=1000000;
            s.Input.AutoRange=false;s.Input.ArtSerial="SIM-ART";s.Input.PicoSerial="SIM-PICO";
            if(!s.EndAfterPressureCapture)throw new Exception("R25 UI default must stop after finite capture");
            if(wiring.Parent!=null || s.IndependentDischargeConfirmed || s.FeedbackWiringConfirmed)
                throw new Exception("R25 acknowledgement checkbox not removed or prior-round approval reused");
            s.EndAfterPressureCapture=false; // Keep explicit coverage of the legacy residual workflow.
            await RunWithRangeReview(s,FactoryRunKind.Pressure,CancellationToken.None);
            if(latest==null||!latest.Completed||!latest.ResidualSafe||!latest.Settings.DividerOnlyFeedback||
                !latest.Settings.UsePostHvVmonStartup||latest.Settings.StartupVmonLimitV!=500||latest.Settings.ZeroBackgroundLimitV!=500||latest.Settings.SafeResidualV!=50||feedbackTrend.SampleCount==0||
                tabs.SelectedIndex!=0||!combined.HasWaveform||!rawPd.HasWaveform)
                throw new Exception("Integrated full-page worker/monitor/capture contract failed");
            SaveImage(Path.Combine(directory,"full-worker-result.png"));
            tabs.SelectedTab=monitorPage;await Task.Delay(80);SaveImage(Path.Combine(directory,"full-worker-monitor.png"));
            string pressureRecord=latest.DirectoryPath;
            s.EndAfterPressureCapture=true;
            await RunWithRangeReview(s,FactoryRunKind.Pressure,CancellationToken.None);
            if(latest!=null || displayedPreview==null || !displayedPreview.Result.PostCaptureMonitoringSkipped ||
                !combined.HasWaveform || !rawPd.HasWaveform || tabs.SelectedIndex!=0 || stages["DISCHARGE"].Visible ||
                ((StageButton)stages["COMPLETE"]).StateText!="已结束" || stages["COMPLETE"].BackColor!=Color.Gainsboro ||
                resultSummary.Text!="" || combined.Title.Contains("残压") || rawPd.Title.Contains("未确认"))
                throw new Exception("R25 quiet end/neutral status/display contract failed");
            string captureOnlyRecord=activeCaptureDirectory;
            string captureLog=File.ReadAllText(Path.Combine(captureOnlyRecord,"progress.log"));
            if(captureLog.Contains("\tRESIDUAL\t") || captureLog.Contains("STEP_DISCHARGE_RUNNING") ||
                !captureLog.Contains("STEP_COMPLETE_UNVERIFIED"))throw new Exception("R25 worker re-entered residual acquisition");
            SaveImage(Path.Combine(directory,"r25-capture-end.png"));
            if(pressureTrend.SampleCount!=feedbackTrend.SampleCount || pressureTrend.LastEncoded!=feedbackTrend.LastEncoded ||
                !pressureTrend.OverlayChannels || !pressureTrend.Visible || pressureTrend.Parent!=pressureWaveSplit.Panel1 ||
                combined.PointToScreen(Point.Empty).X<=pressureTrend.PointToScreen(Point.Empty).X)
                throw new Exception("R28 monitor/voltage/PD three-pane source/layout mismatch");
            SaveImage(Path.Combine(directory,"r28-three-pane-capture.png"));
            if(continuous.Checked || repeatCount.Value!=1 || repeatCount.Enabled)
                throw new Exception("Continuous mode must default off");
            continuous.Checked=true;repeatCount.Value=2;
            if(!repeatCount.Enabled || continuous.Left<=start.Left || repeatCount.PointToScreen(Point.Empty).X<=continuous.PointToScreen(Point.Empty).X ||
                automaticRange.PointToScreen(Point.Empty).X<=repeatCount.PointToScreen(Point.Empty).X || stop.Left<=automaticRange.Left || !automaticRange.Checked ||
                remainingCount.Parent!=repeatCount.Parent || remainingCount.Top<=repeatCount.Top || remainingCount.Text!="剩余 2 次")
                throw new Exception("Continuous/count controls must follow start and precede stop");
            int completedSequence=await FactoryPressureSequence.Run(s,2,
                next=>RunWithRangeReview(next,FactoryRunKind.Pressure,CancellationToken.None),null,null,CancellationToken.None);
            if(completedSequence!=2 || stageSettings.SequenceRound!=2 || stageSettings.SequenceCount!=2 ||
                !displayedPreview.Result.PostCaptureMonitoringSkipped || latest!=null)
                throw new Exception("Two-round simulated worker/UI sequence failed");
            SaveImage(Path.Combine(directory,"r25-continuous.png"));
            automaticRange.Checked=false;
            if(ReadSettings().Input.AutoRange)throw new Exception("Unchecked range reminder did not enter request");
            automaticRange.Checked=true;
            if(!ReadSettings().Input.AutoRange)throw new Exception("Checked range reminder did not enter request");
            s.Input.OscillationCircuitConnected=false;
            bool stopped=false;
            try { await FactoryPressureSequence.Run(s,6,async next=>{
                var result=await RunWithRangeReview(next,FactoryRunKind.Pressure,CancellationToken.None);
                Array.Clear(result.Capture.VoltageRaw,0,result.Capture.VoltageRaw.Length);
                return result;
            },null,null,CancellationToken.None); }
            catch(FactorySequenceStoppedException ex) { stopped=true;SequenceStopped(ex); }
            SetBusy(false);
            if(!stopped || stages["DISCHARGE"].Visible || roundFailureDetail!=null ||
                ((StageButton)stages["COMPLETE"]).StateText!="已结束" || !combined.HasWaveform || !rawPd.HasWaveform || !status.Text.Contains("连续队列停止"))
                throw new Exception("R26 queue failure misreported finished measurement or restored residual tile");
            SaveImage(Path.Combine(directory,"r26-sequence-stopped.png"));
            s.Input.OscillationCircuitConnected=true;
            continuous.Checked=false;repeatCount.Value=1;
            s.TargetKv=0;
            await RunWithRangeReview(s,FactoryRunKind.Pressure,CancellationToken.None);
            if(latest==null||!latest.ReadOnlyAcquisition||latest.ResidualSafe||!latest.CompleteCaptureAvailable()||tabs.SelectedIndex!=0)
                throw new Exception("Integrated zero read-only capture contract failed");
            RefreshMode();
            if(!stages["SAFE"].Tag.ToString().Contains("50 V"))
                throw new Exception("Zero/calibration stage label must not show 500 V");
            string zeroRecord=latest.DirectoryPath;
            ResetStages();
            var fault=new FactoryTests.Fixture(); fault.Settings.VmonAfterHvStartup=true;
            fault.Settings.DividerOnlyFeedback=true; fault.Settings.DischargeTimeoutSeconds=.12;
            stageSettings=fault.Settings.Clone(); RefreshResidualStageLabels();
            fault.Backend.FeedbackRule=(b,x)=>{if(b.Hv && b.Ao>0)x.ImonRawV=5;return x;};
            bool errorVisibleBeforeResidual=false;
            try
            {
                fault.Run((k,v)=>{
                    Progress(k,v);
                    if(k=="STEP_DISCHARGE_RUNNING")
                        errorVisibleBeforeResidual=((StageButton)stages["HV_UP"]).StateText=="错误" &&
                            ((StageButton)stages["HV_OFF"]).StateText=="通过" && status.Text.Contains("IMON");
                });
                throw new Exception("Missing simulated ramp fault");
            }
            catch(FactoryRunException) { }
            if(!errorVisibleBeforeResidual) throw new Exception("Full UI hid ramp error during residual observation");
            SaveImage(Path.Combine(directory,"full-fault-status.png"));
            busy=true;stageSettings=fault.Settings.Clone();ResetEmergency();
            Progress("EMERGENCY_ACTIVE","仿真事件：P0.4低");
            Progress("EMERGENCY_RELEASED","仿真事件：随后复位");
            Progress("STEP_COMPLETE_ERROR","仿真事件：本轮失败");
            busy=false;FreezeEmergency();
            if(!emergencyNotice.Active || emergency.ForeColor!=Color.DarkRed || !emergency.Text.Contains("本轮锁存") || !resultSummary.Text.Contains("复位不自动恢复"))
                throw new Exception("R27 full emergency notice overwritten by release/final error");
            tabs.SelectedIndex=0;SaveImage(Path.Combine(directory,"full-r27-emergency.png"));
            ResetEmergency();
            File.WriteAllText(Path.Combine(directory,"integrated-worker.txt"),
                "INTEGRATED_FULL_UI=PASS\nPRESSURE_WORKERS=2\nZERO_WORKERS=1\nAUTOMATIC_RANGE_RETRIES=0\nPRE_HV_AI=False\nPOST_HV_VMON_LIMIT_V=500\nZERO_BACKGROUND_LIMIT_V=500\nFINAL_LIMIT_V=50\n"+
                "FAULT_UI_ERROR_BEFORE_RESIDUAL=PASS\nR25_NO_POST_AI=PASS\nR25_QUIET_UI=PASS\nR25_NEUTRAL_NOT_SAFETY_PASS=PASS\n"+
                "R25_SEQUENCE_TWO_SIMULATION_WORKERS=PASS\nR25_SEQUENCE_REAL_NEXT_REQUIRES_OPERATOR=True\n"+
                "R26_RANGE_REMINDER_PLACEMENT_SNAPSHOT=PASS\nR26_QUEUE_STOP_IS_NOT_CAPTURE_FAILURE=PASS\nR26_END_RESIDUAL_TILE_HIDDEN=PASS\n"+
                "R27_EMERGENCY_MESSAGE_SURVIVES_RELEASE_AND_FINAL_ERROR=PASS\n"+
                "R28_THREE_PANES_SHARE_UNMODIFIED_FEEDBACK=PASS\n"+
                "HARDWARE_ACCESSED=False\nPRESSURE_RECORD="+pressureRecord+"\nZERO_RECORD="+zeroRecord+"\nCAPTURE_ONLY_RECORD="+captureOnlyRecord+"\n");
            await SmokeResumeStart(Path.Combine(directory,"resume-start"));
            latest=null;ResetStages();tabs.SelectedIndex=0;
        }
    }
}
