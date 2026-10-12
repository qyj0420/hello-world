using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private readonly Button openCapture = Button("查看已采集波形（只读）", 210);
        private string activeCaptureDirectory;
        private FactoryRunKind activeCaptureKind;
        private int capturePreviewGeneration;
        private bool capturePreviewSuspended;
        private FactoryCapturePreview displayedPreview;
        private bool historicalCapturePreview;
        private Task capturePreviewLoad = Task.FromResult(0);
        private const string PreviewSafety = "采集数据已保存 · 残压未确认 · 仅波形查看，不代表试验合格或已放电";

        private void ResetCapturePreview()
        { ++capturePreviewGeneration; capturePreviewSuspended = false; displayedPreview = null; historicalCapturePreview = false; activeCaptureDirectory = null; resultSummary.ForeColor = Color.Black; }

        private void RequestCapturePreview(string directory)
        {
            if (capturePreviewSuspended) return;
            if (!FactoryCapturePreview.BelongsTo(activeCaptureDirectory, directory))
            { diagnostics.AppendText("忽略非本轮采集通知：" + directory + Environment.NewLine); return; }
            if (latest != null) return; // Late preview must never replace an accepted result.
            // The same capture is persisted again on fault. Keep the already rendered
            // preview and its current failure state, without repeating heavy filtering.
            if (displayedPreview != null) return;
            int generation = ++capturePreviewGeneration;
            capturePreviewLoad = LoadCapturePreview(directory, generation, false);
        }

        private async Task LoadCapturePreview(string directory, int generation, bool imported)
        {
            if (!imported && capturePreviewSuspended) return;
            try
            {
                var preview = await Task.Run(() => FactoryCapturePreview.Load(directory));
                if (IsDisposed || Disposing || generation != capturePreviewGeneration || latest != null || !imported && capturePreviewSuspended) return;
                if (!imported && (stageSettings == null ||
                    !FactoryCapturePreview.Matches(stageSettings, activeCaptureKind, preview.Result)))
                    throw new InvalidDataException("采集通知与本轮目标、模式或设备不一致。");
                var r = preview.Result; var c = r.Capture; var d = preview.Detected;
                combined.VoltageReferenceKv = Math.Max(5, r.Settings.TargetKv);
                string label = (r.Settings.Simulation ? "仿真" : "实测") + (imported ? "历史只读" : "本轮采集");
                combined.Title = label + " · 电压/检波局放 · 残压未确认";
                combined.SetCombined(d.VoltageKv, d.PdEnvelopePc, d.OutputRateHz, d.DurationSeconds * 1000);
                rawPd.Title = label + " · 定位局放整段 · " + (c.Overflow ? "ADC溢出，不可定量" : "非合格结论");
                rawPd.SetFilteredCapture(c,c.Settings.PdCalibrationPcPerV);
                displayedPreview = preview;
                await LoadProcessTimeline(directory,r);
                if(IsDisposed || generation!=capturePreviewGeneration || latest!=null || !imported && capturePreviewSuspended)return;
                historicalCapturePreview = imported;
                if (imported) RefreshMode();
                resultSummary.ForeColor = Color.DarkRed;
                resultSummary.Text = PreviewSafety + "\r\nART " + c.VoltageRaw.Length.ToString("N0") + " 点 / Pico " + c.PdAdc.Length.ToString("N0") +
                    " 点 · " + (d.DurationSeconds * 1000).ToString("0.###") + " ms；" +
                    (r.ReadOnlyAcquisition || r.Kind == FactoryRunKind.Calibration ? "自由段起点未同步；" : "同步接线与波形质量仍需核验；") +
                    (roundFailureDetail ?? (imported ? "历史记录仅供查看，不操作设备。" : r.PostCaptureMonitoringSkipped ?
                        "采后AI不重启；正在保存数据和释放资源，放电未确认。" : "残压检查继续，停止按钮仍可用。"));
                referenceSummary.Text = "仅展示已采集波形；合格保存、校准应用和自动量程操作未解锁。原始记录：" + directory;
                if (r.PostCaptureMonitoringSkipped && !imported && roundFailureDetail == null)
                {
                    combined.Title = label + " · 电压 / 检波局放";
                    rawPd.Title = label + " · 定位局放整段";
                    resultSummary.Text = ""; resultSummary.ForeColor = Color.Black;
                    referenceSummary.Text = "原始采集记录：" + directory;
                }
                applyCalibration.Enabled = applyRange.Enabled = export.Enabled = false;
                if(r.TriggerRecovery!=null && r.TriggerRecovery.DiagnosticCaptureSaved)
                {
                    combined.Title=label+" · 补发诊断电压/局放";rawPd.Title=label+" · 补发诊断局放";
                    resultSummary.Text="联动触发错误：当前为单次补发取得的诊断段，不是原轮次有效结果；未计入完成次数。";
                }
                UpdatePostProcessingButtons();
                tabs.SelectedIndex = r.Kind == FactoryRunKind.Calibration ? 1 : 0;
                if (r.Kind == FactoryRunKind.Calibration)
                {
                    calibrationRaw.Title="校准长段 · 10 kHz高通";
                    calibrationRaw.SetFilteredCapture(c,1);
                    calibrationSummary.Text = PreviewSafety + "；候选校准系数尚不可应用。";
                }
                if (imported) status.Text = PreviewSafety + "；历史记录：" + directory;
                diagnostics.AppendText("CAPTURE_DISPLAYED · " + PreviewSafety + " · " + directory + Environment.NewLine);
            }
            catch (Exception ex)
            {
                if (!IsDisposed && generation == capturePreviewGeneration)
                    diagnostics.AppendText("波形预览读取失败（不改变安全判定）：" + ex.Message + Environment.NewLine);
            }
        }

        private async Task OpenSavedCapture()
        {
            if (busy || scanning || exporting) return;
            using (var dialog = new OpenFileDialog { Title = "只读查看采集记录 · 选择 capture.bin（不连接设备）",
                Filter = "采集原始文件 (capture.bin)|capture.bin", CheckFileExists = true,
                InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records") })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                latest = null; ResetCapturePreview(); ResetTf(false);
                combined.ClearWave(); rawPd.ClearWave();
                // Clear previous round's green states before displaying a historical file.
                ResetStages(); ResetEmergency();
                applyCalibration.Enabled = applyRange.Enabled = export.Enabled = false;
                UpdatePostProcessingButtons();
                int generation = ++capturePreviewGeneration;
                capturePreviewLoad = LoadCapturePreview(Path.GetDirectoryName(dialog.FileName), generation, true);
                await capturePreviewLoad;
            }
        }
    }
}
