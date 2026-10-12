using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    internal sealed partial class MainForm
    {
        // Offline UI regression injects decisions/capture failures without opening a device.
        private Func<RangeDecisionContext, RangeDecisionAction> rangeDecisionTestHook;
        private Func<BenchSettings, Task<BenchCapture>> captureTestHook;
        // Session-only: restarting never opts into repeated hardware pulses.
        private bool silentRangeSession;
        private bool rangeDecisionWasAutomatic;

        private static bool NeedsRangeDecision(BenchCapture capture, LocationRangeResult quality)
        {
            bool maximumRisk = capture.Settings.RangeIndex == BenchSettings.Ranges.Length - 1 &&
                (quality.LocationClippedSamples > 0 || quality.LocationHighOccupancy || quality.DriverOverflowUncertain);
            bool expectedBeyondMaximum = quality.ExpectedCalibrationVolts > BenchSettings.Ranges[BenchSettings.Ranges.Length - 1];
            // Serious limit warnings must not be suppressed by any range preference.
            return maximumRisk || expectedBeyondMaximum ||
                capture.Settings.AutoRange && (quality.Change || !quality.LocationQualityPassed || quality.MaximumRangeInsufficient);
        }

        private static void SaveAnalysis(BenchCapture capture, BenchAnalysisRecord record)
        {
            // Close the complete temporary file before replacing only this derived analysis.
            // capture.bin/settings.xml and all ADC/overflow evidence are never rewritten here.
            string path = Path.Combine(capture.DirectoryPath, "analysis.xml");
            string pending = path + ".pending";
            using (var stream = File.Create(pending)) new XmlSerializer(typeof(BenchAnalysisRecord)).Serialize(stream, record);
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }

        private RangeDecisionAction AskRangeDecision(RangeDecisionContext context)
        {
            rangeDecisionWasAutomatic = false;
            string reason;
            if (silentRangeSession && !RangeDecisionPolicy.IsMaximumRangeRisk(context) && !RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context) &&
                RangeDecisionPolicy.CanAdjustAndRetry(context, out reason))
            {
                rangeDecisionWasAutomatic = true;
                return RangeDecisionAction.AdjustRetry;
            }
            if (rangeDecisionTestHook != null) return rangeDecisionTestHook(context);
            if (smokeMode) return RangeDecisionAction.Dismiss;
            using (var dialog = new RangeDecisionDialog(context, Icon))
            {
                dialog.ShowDialog(this);
                if (dialog.Decision == RangeDecisionAction.AdjustRetry && dialog.SuppressFurtherRangePrompts)
                    silentRangeSession = true;
                return dialog.Decision;
            }
        }

        private bool ConfirmRetryTrigger(BenchSettings settings, bool allowAutomaticChain)
        {
            if (!settings.RealHardware || !settings.AutomaticSharedTrigger) return true;
            return MessageBox.Show(this,
                "即将按新量程重采一次，仍须确认高压源未上电、设备无储能。\nART " + settings.ArtDevice + " / " + settings.ArtSerial +
                "；Pico " + settings.PicoSerial + "。\n" +
                (allowAutomaticChain ? "本次允许根据量程连续重采，最多遍历 11 档；每轮输出一次 P0.7 TRG，量程合适、到边界或重复档位即停止。" : "仅再次输出一次 P0.7 TRG。") +
                "\n不操作 AO、HV ON/OFF 或其他数字线。确认后才会修改量程并启动。",
                "确认重采的单次 TRG 输出", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }

        private async Task RunWithRangeReviewAsync(BenchSettings original)
        {
            BenchSettings nextSettings = original;
            BenchCapture parentCapture = null;
            BenchAnalysisRecord parentRecord = null;
            var visitedRanges = new HashSet<int> { original.RangeIndex };
            bool automaticTriggerAuthorized = false;
            try
            {
                for (int attempt = 0; attempt < BenchSettings.Ranges.Length; attempt++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    activeSettings = nextSettings;
                    if (attempt > 0)
                    {
                        latest = null; analysis = null; locationAdc = null; export.Enabled = false;
                        result.Text = "正在按确认后的量程重采；上轮原始记录已单独保留。";
                        rangeDiagnosticText.Text = "等待重采结果；不沿用上轮量程合格状态。";
                        voltage.ClearWave(); discharge.ClearWave(); location.ClearWave();
                        ResetConditions(); ResetStageDiagnostics();
                    }
                    latest = captureTestHook == null
                        ? await BenchRunner.RunAsync(activeSettings, Progress, cancellation.Token)
                        : await captureTestHook(activeSettings);
                    // RunAsync returns only after its tasks, worker and capture validation have finished.
                    // Analysis is needed to show quality; it never constitutes operator acceptance.
                    DisplayCapture(latest);
                    analysis.RangeRetryNumber = attempt;
                    if (parentCapture != null)
                    {
                        analysis.ParentCaptureDirectory = parentCapture.DirectoryPath;
                        parentRecord.RetryCaptureDirectory = latest.DirectoryPath;
                        parentRecord.RetryOutcome = "重采已取得完整数据；量程质量须看新轮诊断";
                        SaveAnalysis(parentCapture, parentRecord);
                    }
                    SaveAnalysis(latest, analysis);
                    if (silentRangeSession && analysis.LocationRange.LocationQualityPassed && !analysis.LocationRange.MaximumRangeInsufficient)
                    {
                        analysis.RangeReviewStatus = "自动调档已结束：本轮定位段量程占用合适；保留该档位，不再为细调多采一轮";
                        SaveAnalysis(latest, analysis); ShowRangeReviewStatus(analysis.RangeReviewStatus);
                        export.Enabled = true; return;
                    }
                    if (!NeedsRangeDecision(latest, analysis.LocationRange))
                    {
                        ShowRangeReviewStatus(analysis.RangeReviewStatus);
                        export.Enabled = true;
                        return;
                    }
                    stop.Enabled = false; // No device task remains while the operator decides.
                    var context = new RangeDecisionContext(analysis.LocationRange, latest.Settings.RangeIndex,
                        analysis.LocationRange.RecommendedIndex, latest.Settings.TargetVoltageV, attempt, false, false, silentRangeSession);
                    string cannotRetry;
                    bool repeatRange = analysis.LocationRange.Change && visitedRanges.Contains(analysis.LocationRange.RecommendedIndex);
                    if (silentRangeSession && !RangeDecisionPolicy.IsMaximumRangeRisk(context) && !RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context) &&
                        (repeatRange || !RangeDecisionPolicy.CanAdjustAndRetry(context, out cannotRetry)))
                    {
                        analysis.RangeReviewStatus = repeatRange
                            ? "自动调档已停止：推荐返回已使用的档位，避免量程来回切换；质量未自动认可"
                            : "自动调档已停止：已到档位边界、次数上限或无可执行建议；质量未自动认可";
                        SaveAnalysis(latest, analysis); ShowRangeReviewStatus(analysis.RangeReviewStatus);
                        export.Enabled = true; return;
                    }
                    RangeDecisionAction decision = AskRangeDecision(context);
                    // The checkbox takes effect only with an explicit adjustment choice.
                    context = new RangeDecisionContext(analysis.LocationRange, latest.Settings.RangeIndex,
                        analysis.LocationRange.RecommendedIndex, latest.Settings.TargetVoltageV, attempt, false, false, silentRangeSession);
                    analysis.RangeDecision = RangeDecisionPolicy.CreateRecord(context, decision);
                    analysis.RangeDecision.WasAutomaticDecision = rangeDecisionWasAutomatic;
                    analysis.RangeDecision.SuppressFurtherRangePrompts = silentRangeSession;
                    analysis.RangeReviewStatus = decision == RangeDecisionAction.Ignore
                        ? "操作者已忽略量程建议并接受保存/分析；原始质量标志不变"
                        : decision == RangeDecisionAction.AdjustRetry ? (rangeDecisionWasAutomatic ? "按本次会话授权，自动按推荐量程重采" : "操作者请求按推荐量程重采")
                        : "已关闭量程弹窗，待处理；未确认接受，不重采";
                    SaveAnalysis(latest, analysis);
                    ShowRangeReviewStatus(analysis.RangeReviewStatus);
                    export.Enabled = true; // Export always carries measured flags plus decision status.
                    if (decision != RangeDecisionAction.AdjustRetry) return;

                    cancellation.Token.ThrowIfCancellationRequested();
                    if (repeatRange) throw new InvalidOperationException("推荐档位已在本轮使用，已停止调档，避免往复重采。");
                    if (!automaticTriggerAuthorized && !ConfirmRetryTrigger(latest.Settings, silentRangeSession))
                    {
                        analysis.RetryOutcome = "操作者取消了共享 TRG 重采确认；未改量程，未重采";
                        SaveAnalysis(latest, analysis); ShowRangeReviewStatus(analysis.RetryOutcome);
                        return;
                    }
                    automaticTriggerAuthorized = silentRangeSession;
                    cancellation.Token.ThrowIfCancellationRequested();
                    nextSettings = RangeDecisionPolicy.CreateRetrySettings(latest.Settings, context);
                    visitedRanges.Add(nextSettings.RangeIndex);
                    // Persist first, then update the UI. Never change the completed record's settings.
                    if (!smokeMode) nextSettings.Save(LastSettingsPath);
                    applyingAutomaticRange = true;
                    try { range.SelectedIndex = nextSettings.RangeIndex; }
                    finally { applyingAutomaticRange = false; }
                    runNumber = Math.Max(runNumber, nextSettings.RunNumber);
                    parentCapture = latest; parentRecord = analysis;
                    parentRecord.RetryOutcome = "已确认重采，等待新轮结果";
                    SaveAnalysis(parentCapture, parentRecord);
                    Progress("INFO", "已确认量程 " + range.Items[nextSettings.RangeIndex] + "；保持原目标 0 V、原两卡序列号和其他参数。重采序号 " + (attempt + 1) + "；原记录：" + parentCapture.DirectoryPath);
                    stop.Enabled = true;
                }
                throw new InvalidOperationException("已达到全部量程档位的重采上限；未继续采集。");
            }
            catch (Exception ex)
            {
                if (parentCapture != null)
                {
                    parentRecord.RetryOutcome = ex is OperationCanceledException ? "重采被取消，未得到认可的重采结果" : "重采或后续处理失败：" + ex.Message;
                    try { SaveAnalysis(parentCapture, parentRecord); }
                    catch (Exception saveError) { Progress("ERROR", "无法更新重采状态：" + saveError.Message); }
                    Progress("INFO", "首轮原始记录仍保留，不视作重采成功：" + parentCapture.DirectoryPath);
                }
                throw;
            }
        }

        private void ShowRangeReviewStatus(string text)
        {
            rangeDiagnosticText.AppendText("\r\n\r\n处理结果：" + text);
            status.Text = text + "。原始记录已保存；导出保留测量质量与操作者决定。";
            Progress("RANGE_REVIEW", status.Text);
        }
    }
}
