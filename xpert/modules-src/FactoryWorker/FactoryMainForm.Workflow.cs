using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private FactorySettings stageSettings;
        private bool residualStageStarted;

        private void RefreshResidualStageLabels()
        {
            var shown = stageSettings ?? ReadSettings();
            stages["SAFE"].Tag = shown.UsePostHvVmonStartup ? "急停许可 · 不采AI" : "初始反馈 ≤" + shown.InitialResidualLimitV.ToString("0.#") + " V / 联锁";
            stages["HV_DELAY"].Tag = shown.UsePostHvVmonStartup ? (shown.ZeroBackgroundLimitV > 0 ? "两路启动 ≤" : "VMON启动 ≤") + shown.StartupVmonLimitV.ToString("0.#") + " V" : "HV ON零输出监测";
            stages["DISCHARGE"].Tag = "结束/故障残压 ≤" + shown.SafeResidualV.ToString("0.#") + " V";
            // Normal capture-only mode has no residual measurement stage to display.
            // Fault monitoring restores the actual residual stage, never a green substitute.
            stages["DISCHARGE"].Visible = !shown.UseCaptureOnlyEnd || residualStageStarted;
            stages["SAFE"].Invalidate(); stages["HV_DELAY"].Invalidate(); stages["DISCHARGE"].Invalidate();
        }

        private async Task<FactoryResult> RunWithRangeReview(FactorySettings requested, FactoryRunKind kind, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // One round owns exactly one worker. Sequence resumption creates a
            // new worker from normal initialization, never a continuation of AO.
            var frozen = requested.Clone();
            latest = null; applyCalibration.Enabled = applyRange.Enabled = export.Enabled = false;
            combined.ClearWave(); rawPd.ClearWave(); calibrationRaw.ClearWave(); calibrationAverage.ClearWave(); ResetTf(false);
            resultSummary.Text = "正在执行单轮；量程弹窗不自动重新加压。";
            referenceSummary.Text = "等待本轮有效振荡数据；不沿用上轮参考分析。";
            ResetStages(); ResetEmergency(); feedbackTrend.SetLive(true);
            pressureTrend.SetLive(true);
            stageSettings = frozen;
            RefreshResidualStageLabels();
            if (kind == FactoryRunKind.Pressure) tabs.SelectedIndex = 0;
            Progress("ROUND", "本轮目标 " + frozen.TargetKv.ToString("0.000") + " kV；量程 ±" +
                BenchSettings.Ranges[frozen.Input.RangeIndex] + " V；第 " + frozen.SequenceRound + " / " + frozen.SequenceCount + " 轮；量程决定仅用于下一轮。");
            activeCaptureDirectory = NewDirectory(frozen.Simulation ? "SIM" : "HW");
            activeCaptureKind = kind;
            FactoryResult round;
            workerConnectionOwned=true;
            activeArtLinkFault=activePicoLinkFault=false;
            try
            {
                await SuspendConnectionMonitor();
                if(!frozen.Simulation)
                {
                    BindConnectionJournal(frozen);if(connectionLogFailed)throw new InvalidOperationException("连接日志不可用");
                    connectionJournal.TaskName=(kind==FactoryRunKind.Calibration?"校准":frozen.SequenceCount>1?"连续加压":"单次加压")+" / 第 "+frozen.SequenceRound+" / "+frozen.SequenceCount+" 轮 / 批次 "+frozen.SequenceAttempt;
                    connectionJournal.Progress="初始化";connectionJournal.Record=activeCaptureDirectory;
                    artSeen=picoSeen=DateTime.MinValue;artLink=picoLink="WAIT";
                }
                round = await Program.StartWorker(frozen, kind, activeCaptureDirectory, Progress, token);
            }
            finally
            {
                // Drain UI callbacks already posted by the child's final report
                // before checkpointing the round or showing the incident summary.
                if(IsHandleCreated && !IsDisposed)
                {var fence=new TaskCompletionSource<bool>();BeginInvoke(new Action(()=>fence.SetResult(true)));await fence.Task;}
                workerConnectionOwned=false;LogConnectionResult("本轮收尾已返回；正常会话保留连接，异常会话退出后才可只读重建，不自动恢复输出；原始记录目录："+activeCaptureDirectory);
                if(connectionJournal!=null)connectionJournal.EnterPostProcessing();
            }
            token.ThrowIfCancellationRequested();
            if (round.CaptureOnlyAvailable())
            {
                if (!FactoryCapturePreview.Matches(frozen, kind, round))
                    throw new InvalidOperationException("采集结果与本轮设备/目标不一致。");
                await WaitForRoundCapturePreview(round, token);
                token.ThrowIfCancellationRequested();
                rampStatistics.Text = round.DescribeRampAndHold();
                resultSummary.ForeColor = Color.Black;
                resultSummary.Text = "";
                referenceSummary.Text = "原始采集记录：" + round.DirectoryPath +
                    (round.FinalAi4Observation==null ? "" : " | "+round.FinalAi4Observation.Describe());
                status.Text = "";
                FreezeEmergency(); feedbackTrend.SetLive(false);
                pressureTrend.SetLive(false);
                PreparePulseWindow(round);
                if (frozen.SequenceCount==1 || frozen.SequenceRound==frozen.SequenceCount)
                    ReviewCapturedRange(round,FactoryRangeReview.EvaluateCapturedData(round),false);
                return round; // No accepted-result or calibration permissions.
            }
            if (!round.CompleteCaptureAvailable() || round.Kind != kind || round.Settings.TargetKv != requested.TargetKv ||
                round.Settings.Input.ArtSerial != requested.Input.ArtSerial || round.Settings.Input.PicoSerial != requested.Input.PicoSerial)
                throw new InvalidOperationException("完整结束、残压或同卡同目标条件未通过；不自动重做。");
            latest = round;
            if (kind == FactoryRunKind.Calibration)
            {
                FactoryTfAnnotationDocument labels;
                if (tfAnnotationFiles.TryGetValue(FactoryTfAnalysis.ChainKey(round.Capture), out labels) &&
                    labels.Regions.Exists(r => r.FilterInterference && r.Templates.Count > 0))
                {
                    Progress("CALIBRATION_TEMPLATE", "按已有人工干扰标签复核校准；保留原始数据及质量标记。");
                    var frozenLabels = FactoryTfAnnotationDocument.Clone(labels);
                    var candidate = await Task.Run(() => FactoryCalibrationAdapter.Calibrate(round.Capture, frozen.Input.CalibrationPulsePc,
                        frozen.CalibrationFrequencyHz, frozen.CalibrationPulseCount, frozenLabels,
                        FactoryTfAnalysis.ExtractForCalibrationLabels(round, FactoryTfAnalysis.Estimate(round.Capture).ThresholdPc)));
                    token.ThrowIfCancellationRequested(); round.Calibration = candidate;
                    FactoryDataArchive.SaveXml(candidate, System.IO.Path.Combine(round.DirectoryPath,
                        "calibration-template-review-" + Guid.NewGuid().ToString("N") + ".xml"));
                }
            }
            var quality = FactoryRangeReview.Evaluate(round);
            round.Metrics.RecommendedRangeIndex = quality.WindowValid ? quality.RecommendedIndex : -1;
            round.Metrics.RangeQuality = quality;
            round.Metrics.RangeDetail = quality.WindowDefinition + "；" + quality.Reason + "；" + quality.LocationQuality;
            DisplayResult(round); FreezeEmergency(); feedbackTrend.SetLive(false);
            pressureTrend.SetLive(false);
            if(kind==FactoryRunKind.Pressure)
            {
                if(frozen.SequenceCount==1 || frozen.SequenceRound==frozen.SequenceCount)ReviewCapturedRange(round,quality,false);
            }
            else if (FactoryRangeReview.NeedsReview(round, quality))
                ReviewRangeForNextRound(round);
            else calibrationAutoAccepted = kind == FactoryRunKind.Calibration;
            token.ThrowIfCancellationRequested();
            return round;
        }

        private async Task WaitForRoundCapturePreview(FactoryResult round, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await capturePreviewLoad;
            token.ThrowIfCancellationRequested();
            if (displayedPreview == null)
                await LoadCapturePreview(round.DirectoryPath, ++capturePreviewGeneration, false);
            token.ThrowIfCancellationRequested();
        }

        private bool ReviewCapturedRange(FactoryResult round, LocationRangeResult quality, bool automaticNext)
        {
            if (!FactoryOptionalPromptPolicy.NeedsRangePrompt(round,quality,repeatPrompts.Checked))
            {
                var silent = FactoryNextRangeReview.Silent(round,quality,automaticNext);
                if(!repeatPrompts.Checked && round.Settings.Input.AutoRange && FactoryNextRangeReview.NeedsPrompt(round,quality,true))
                {
                    silent.OptionalPromptSuppressed=true;
                    if(automaticNext)silent.Action="SuppressedKeepCurrent";
                    silent.Meaning="重复提示已关闭；未将隐藏提示视为同意调档。"+silent.Meaning;
                }
                FactoryNextRangeReview.Save(round,silent); // Persist before preparing/applying a different range.
                diagnostics.AppendText("NEXT_RANGE："+silent.Meaning+Environment.NewLine);
                return silent.ApplyNextRound;
            }
            using (var dialog=new FactoryNextRangeDialog(round,quality,round.Capture.Settings.RangeIndex,Icon,cancellation==null?null:(Action)RequestUserStop,true))
            {
                dialog.ShowDialog(this);
                var choice=FactoryNextRangeReview.Decide(round,quality,round.Capture.Settings.RangeIndex,dialog.ApplyNext);
                if(cancellation!=null && cancellation.IsCancellationRequested)
                {choice.Action="StopQueue";choice.Meaning="用户停止，未应用量程，也不启动下一轮。";FactoryNextRangeReview.Save(round,choice);cancellation.Token.ThrowIfCancellationRequested();}
                choice.DisableOptionalPromptsRequested=dialog.DisableOptionalPrompts;
                FactoryNextRangeReview.Save(round,choice);
                if(dialog.DisableOptionalPrompts)repeatPrompts.Checked=false;
                if(choice.ApplyNextRound)
                {
                    if(round.Settings.Simulation && !simulationRangeBeforeApply.HasValue) simulationRangeBeforeApply=range.SelectedIndex;
                    range.SelectedIndex=choice.RecommendedRangeIndex;
                }
                diagnostics.AppendText("NEXT_RANGE："+choice.Meaning+Environment.NewLine);
                return choice.ApplyNextRound;
            }
        }

        private void SequenceStopped(FactorySequenceStoppedException stopped)
        {
            // Do not manufacture STEP_COMPLETE_ERROR or a residual-monitoring step
            // after a finished round. The stopped queue is a separate outcome.
            Progress("SEQUENCE_STOPPED",stopped.Message);
            RefreshResidualStageLabels();
        }

        private void ReviewRangeForNextRound(FactoryResult round)
        {
            if (round != latest || round.Settings.Simulation != (mode.SelectedIndex == 0)) return;
            var quality = round.Metrics.RangeQuality ?? FactoryRangeReview.Evaluate(round);
            using (var dialog = new FactoryNextRangeDialog(round, quality, range.SelectedIndex, Icon,cancellation==null?null:(Action)RequestUserStop))
            {
                dialog.ShowDialog(this);
                var decision = FactoryNextRangeReview.Decide(round, quality, range.SelectedIndex, dialog.ApplyNext);
                if(cancellation!=null && cancellation.IsCancellationRequested)
                {decision.Action="StopQueue";decision.Meaning="用户停止，未应用校准或量程。";FactoryNextRangeReview.Save(round,decision);cancellation.Token.ThrowIfCancellationRequested();}
                FactoryNextRangeReview.Save(round, decision);
                ApplyRangeDecision(round, decision);
                calibrationAutoAccepted = round.Kind == FactoryRunKind.Calibration && dialog.KeepCurrent;
                Progress("RANGE_DECISION", decision.Meaning);
            }
        }

        private void ApplyRangeDecision(FactoryResult round, FactoryNextRangeDecision decision)
        {
            if (!decision.ApplyNextRound) return;
            if (round != latest || round.Settings.Simulation != (mode.SelectedIndex == 0) ||
                !FactoryNextRangeReview.CanApply(round, decision.OriginalQuality, range.SelectedIndex))
                throw new InvalidOperationException("量程上下文已改变，未应用建议。");
            if (round.Settings.Simulation && !simulationRangeBeforeApply.HasValue) simulationRangeBeforeApply = range.SelectedIndex;
            range.SelectedIndex = decision.RecommendedRangeIndex; applyRange.Enabled = false;
        }

        private void ResetEmergency()
        { emergencyNotice.Reset(); emergencyRead = false; emergency.Text = "急停 P0.4\n尚未读取"; emergency.BackColor = Color.FromArgb(226, 232, 236); emergency.ForeColor = Color.DarkSlateGray; }
        private void ShowLatchedEmergency()
        {
            emergency.Text = FactoryEmergencyNotice.Title;
            emergency.BackColor = Color.FromArgb(255,210,210); emergency.ForeColor = Color.DarkRed;
            resultSummary.Text = FactoryEmergencyNotice.Message;
            status.Text = "急停已动作；本次连续队列终止，复位不自动恢复。";
        }
        private void UpdateEmergency(bool released, string detail)
        {
            emergencyRead = true; lastEmergencyReleased = released; emergencyReadTime = DateTime.Now;
            emergency.Text = (mode.SelectedIndex == 0 ? "仿真 · " : "") + "急停 " + (released ? "已解除" : "不许可") + "\nP0.4 " + (released ? "高" : "低") + " · " + emergencyReadTime.ToString("HH:mm:ss");
            emergency.BackColor = released ? Color.FromArgb(196, 232, 208) : Color.FromArgb(255, 210, 210);
            emergency.ForeColor = released ? Color.DarkGreen : Color.DarkRed;
        }
        private void FreezeEmergency()
        {
            if (emergencyNotice.Active) { ShowLatchedEmergency(); return; }
            if (!emergencyRead) { MarkEmergencyUnknown(); return; }
            emergency.Text = (mode.SelectedIndex == 0 ? "仿真 " : "") + "末次 P0.4 " + (lastEmergencyReleased ? "高" : "低") + "\n已离线 · " + emergencyReadTime.ToString("HH:mm:ss");
            emergency.BackColor = Color.FromArgb(226, 232, 236); emergency.ForeColor = Color.DarkSlateGray;
        }
        private void MarkEmergencyUnknown()
        { if (emergencyNotice.Active) { ShowLatchedEmergency(); return; } emergencyRead = false; emergency.Text = "急停 / 联锁\n未知 · 不允许据此加压"; emergency.BackColor = Color.FromArgb(255, 210, 210); emergency.ForeColor = Color.DarkRed; }
        private void RefreshRangeLabels()
        {
            int selected = range.SelectedIndex;
            range.BeginUpdate();
            try
            {
                for (int i = 0; i < range.Items.Count; i++) range.Items[i] = FactoryRangeReview.RangeLabel(i, N("pd")) + "（±" + BenchSettings.Ranges[i].ToString("0.###") + " V）";
                range.SelectedIndex = selected;
            }
            finally { range.EndUpdate(); }
        }
        private void RequireAppliedCalibrationProfile(FactorySettings settings)
        {
            if (appliedCalibrationProfile == null) return; // Explicit manual coefficient, not a certified calibration claim.
            var prior = appliedCalibrationProfile.Input; var now = settings.Input;
            if (settings.Simulation != appliedCalibrationProfile.Simulation || now.PicoSerial != prior.PicoSerial || now.ArtSerial != prior.ArtSerial ||
                now.PicoCoupling != prior.PicoCoupling || now.PicoBandwidth20MHz != prior.PicoBandwidth20MHz ||
                now.ResolutionBits != prior.ResolutionBits || now.RequestedPicoRateHz != prior.RequestedPicoRateHz)
                throw new InvalidOperationException("已应用的校准系数属于先前设备/采集条件；请重新校准，或明确输入当前条件的有效手动系数。");
        }
        private static Panel BuildScrollableGroup(System.Windows.Forms.TableLayoutPanel table)
        {
            var host = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, AutoScroll = true };
            var group = WrapGroup(table); group.AutoSize = false; group.Dock = System.Windows.Forms.DockStyle.None;
            table.Dock = System.Windows.Forms.DockStyle.None; table.AutoSize = false; table.ColumnStyles[0].Width = 120;
            host.Controls.Add(group); bool arranging = false;
            Action arrange = () =>
            {
                if (arranging || host.ClientSize.Width < 300) return; arranging = true;
                try
                {
                    int width = Math.Max(285, host.ClientSize.Width - System.Windows.Forms.SystemInformation.VerticalScrollBarWidth - 12);
                    group.Location = new Point(6, 6 + host.AutoScrollPosition.Y); group.Width = width;
                    table.Location = new Point(group.Padding.Left, group.Padding.Top); table.Width = width - group.Padding.Horizontal;
                    int rightWidth = Math.Max(140, table.Width - table.Padding.Horizontal - 120 - 4);
                    foreach (System.Windows.Forms.Control control in table.Controls)
                    {
                        int available = table.GetColumn(control) == 0 ? 115 : rightWidth;
                        var label = control as System.Windows.Forms.Label;
                        if (label != null && label.AutoSize) label.MaximumSize = new Size(available, 0);
                        var check = control as System.Windows.Forms.CheckBox;
                        if (check != null)
                        {
                            check.AutoSize = false; check.Width = rightWidth;
                            check.Height = Math.Max(28, System.Windows.Forms.TextRenderer.MeasureText(check.Text, check.Font,
                                new Size(Math.Max(40, rightWidth - 23), 0), System.Windows.Forms.TextFormatFlags.WordBreak).Height + 8);
                        }
                    }
                    table.PerformLayout(); int height = table.GetPreferredSize(new Size(table.Width, 0)).Height;
                    table.Height = height; group.Height = height + group.Padding.Vertical + 6;
                    host.AutoScrollMinSize = new Size(0, group.Height + 12);
                }
                finally { arranging = false; }
            };
            host.SizeChanged += (s,e) => arrange(); host.FontChanged += (s,e) => arrange();
            host.VisibleChanged += (s,e) => { if (host.Visible) arrange(); }; return host;
        }
    }
}
