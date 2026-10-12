using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal async Task SmokeCapturePreview(string output, string recordedDirectory)
        {
            // Actual recorded bytes are read only: no backend instance or driver.
            var recorded = await Task.Run(() => FactoryCapturePreview.Load(recordedDirectory));
            ResetStages(); latest = null; stageSettings = recorded.Result.Settings.Clone();
            activeCaptureDirectory = recordedDirectory; activeCaptureKind = recorded.Result.Kind;
            busy = true; SetBusy(true);
            Progress("STEP_READ_PASSED", "完整两卡数据已读取（历史回放）");
            Progress("STEP_DISCHARGE_RUNNING", "50 V残压检查进行中（界面回放）");
            Progress("CAPTURE_READY_FILE", recordedDirectory); await capturePreviewLoad;
            RequirePreview("record loaded before residual completion");
            if (!stop.Enabled || start.Enabled || export.Enabled || saveData.Enabled || applyRange.Enabled || applyCalibration.Enabled)
                throw new Exception("Preview changed busy/safety/action permissions");
            SaveImage(Path.Combine(output,"preview-pending.png"));
            Progress("RESIDUAL", "原始残压未满足50 V，0/5；不作为已放电依据。");
            Progress("STEP_DISCHARGE_ERROR", "残压检查超时（回放）");
            Progress("FAULT_RESIDUAL", "异常只读观察仍不改判成功。");
            Progress("STEP_COMPLETE_ERROR", "本轮失败，保留已采集波形。");
            RequirePreview("fault does not hide waveforms");
            busy = false; SetBusy(false);
            if (saveData.Enabled || export.Enabled || applyRange.Enabled || applyCalibration.Enabled)
                throw new Exception("Fault preview enabled acceptance/export controls");
            SaveImage(Path.Combine(output,"preview-fault.png"));

            // A managed runner waits in residual monitoring while its background
            // publisher emits data. This verifies the full runner->publisher->UI path.
            ResetStages(); combined.ClearWave(); rawPd.ClearWave(); latest = null;
            var fixture = new FactoryTests.Fixture(); fixture.Settings.DischargeTimeoutSeconds = .12;
            fixture.Backend.FeedbackRule = (b,x) => { if (b.ReadCount > 0) x.DividerRawV = .05; return x; };
            activeCaptureDirectory = Path.Combine(output,"managed-round");
            Directory.CreateDirectory(activeCaptureDirectory); stageSettings = fixture.Settings.Clone();
            activeCaptureKind = FactoryRunKind.Pressure;
            using (var releaseResidual = new ManualResetEventSlim(false))
            using (var enteredResidual = new ManualResetEventSlim(false))
            {
                var run = Task.Run(() =>
                {
                    using (var publisher = new FactoryCapturePublisher(activeCaptureDirectory, Progress))
                    {
                        try
                        {
                            FactoryRunner.Run(fixture.Settings,FactoryRunKind.Pressure,fixture.Backend,fixture.Clock,
                                (key,text) => {
                                    Progress(key,text);
                                    if (key == "RESIDUAL" && !enteredResidual.IsSet)
                                    { enteredResidual.Set(); if (!releaseResidual.Wait(15000)) throw new TimeoutException("test gate"); }
                                }, CancellationToken.None, publisher.Queue);
                            throw new Exception("Missing simulated residual failure");
                        }
                        catch (FactoryRunException ex) { return ex.PartialResult; }
                    }
                });
                try
                {
                    for (int i = 0; i < 200 && (displayedPreview == null || !enteredResidual.IsSet); i++) await Task.Delay(50);
                    RequirePreview("managed runner published during residual wait");
                    if (run.IsCompleted) throw new Exception("Runner must still be in residual monitoring");
                }
                finally { releaseResidual.Set(); }
                var failed = await run; await Task.Delay(100);
                if (failed.Completed || failed.ResidualSafe || fixture.Backend.OpticalHighCount != 1 || fixture.Backend.HvHighCount != 1)
                    throw new Exception("Preview changed hardware-state-machine result");
                RequirePreview("managed residual failure retains display");
            }
            // Reject notifications for another round, and invalidate in-flight loads.
            ResetStages(); latest = null; combined.ClearWave(); rawPd.ClearWave();
            stageSettings = recorded.Result.Settings.Clone(); activeCaptureDirectory = recordedDirectory;
            activeCaptureKind = recorded.Result.Kind;
            RequestCapturePreview(Path.Combine(output,"not-this-round"));
            if (displayedPreview != null) throw new Exception("Foreign round accepted");
            RequestCapturePreview(recordedDirectory); var late = capturePreviewLoad; ResetStages(); await late;
            if (displayedPreview != null || combined.HasWaveform || rawPd.HasWaveform) throw new Exception("Late preview crossed reset");
            // Final read-only replay stays visible as the smoke's last screenshot.
            await LoadCapturePreview(recordedDirectory, ++capturePreviewGeneration, true);
            RequirePreview("old R23 record opened without hardware");
            SaveImage(Path.Combine(output,"preview-readonly.png"));
            File.WriteAllText(Path.Combine(output,"preview-tests.txt"),
                "R24_PREVIEW_UI=PASS\nREAL_RECORD="+recordedDirectory+"\nART_POINTS="+recorded.Result.Capture.VoltageRaw.Length+
                "\nPICO_POINTS="+recorded.Result.Capture.PdAdc.Length+
                "\nPENDING_DISPLAY=PASS\nFAULT_RETAINED=PASS\nMANAGED_RUNNER_PENDING=PASS\nSTALE_ROUND_REJECTED=PASS\n"+
                "ACCEPTANCE_LOCKED=PASS\nHARDWARE_ACCESSED=False\nORIGINAL_RECORD_WRITES=False\n");
        }
        private void RequirePreview(string phase)
        {
            if (latest != null || displayedPreview == null || displayedPreview.Result.Completed || displayedPreview.Result.ResidualSafe ||
                !combined.HasWaveform || !rawPd.HasWaveform || tabs.SelectedIndex != 0)
                throw new Exception("Capture preview contract failed: " + phase);
        }
    }
}
