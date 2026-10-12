using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    internal sealed partial class MainForm
    {
        private sealed class ReviewCaptureEvidence
        {
            internal BenchCapture Capture;
            internal short[] AdcBefore;
            internal string RawHash;
            internal string SettingsText;
        }

        // Calls the production review loop, but injects only synthetic arrays and explicit
        // decisions. No device enumeration, native SDK, TRG, AO or HV command is permitted.
        internal async Task RangeReviewSmokeAsync(string imagePath)
        {
            smokeMode = true;
            string absoluteImage = Path.GetFullPath(imagePath);
            string output = Path.GetDirectoryName(absoluteImage);
            Directory.CreateDirectory(output);
            string root = Path.Combine(output, "range-review-cases_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string stem = Path.Combine(output, Path.GetFileNameWithoutExtension(absoluteImage));
            var summary = new List<string> { "RANGE_REVIEW_UI=START", "DRIVERS=not loaded", "HARDWARE_OUTPUTS=none", "RECORD_ROOT=" + root };
            CancellationTokenSource oldCancellation = cancellation;
            cancellation = new CancellationTokenSource();
            try
            {
                silentRangeSession = false;
                mode.SelectedIndex = 0; numbers["picorate"].Value = 25; numbers["duration"].Value = 8; resolution.SelectedIndex = 3;
                numbers["pdcal"].Value = 2100; numbers["pulse"].Value = 100; numbers["divider"].Value = 11000;
                numbers["skip"].Value = .5M;
                var captures = new List<ReviewCaptureEvidence>();
                int calls = 0, decisions = 0;
                var original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings =>
                {
                    calls++;
                    var evidence = CreateReviewCapture(settings, Path.Combine(root, "ignore"), 1, true);
                    captures.Add(evidence); return Task.FromResult(evidence.Capture);
                };
                rangeDecisionTestHook = context => { decisions++; CheckReview(!context.IsAutomaticFlow && !context.HasRealPressurization, "production dialog capabilities"); return RangeDecisionAction.Ignore; };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 1 && decisions == 1 && range.SelectedIndex == original.RangeIndex, "Ignore must not recapture or change range");
                CheckReview(analysis.RangeDecision.OperatorAcceptedData && !analysis.RangeDecision.OriginalQualityPassed &&
                    analysis.RangeDecision.QualitySnapshot.LocationClippedSamples > 0 && analysis.Discharge.Clipped, "Ignore retains measured clipping");
                CheckReviewEvidence(captures[0]);
                CheckReview(ReadReviewAnalysis(captures[0].Capture.DirectoryPath).RangeDecision.Action == RangeDecisionAction.Ignore, "Ignore persisted");
                summary.Add("PASS Ignore: one capture, range/ADC/settings unchanged, quality flags and decision persisted");

                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "dismiss"), 1, true); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context => { decisions++; return RangeDecisionAction.Dismiss; };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 1 && decisions == 1 && !analysis.RangeDecision.OperatorAcceptedData && !analysis.RangeDecision.RequestedRetry,
                    "Dismiss must not accept or repeat");
                CheckReviewEvidence(captures[0]);
                summary.Add("PASS Dismiss: no implicit acceptance/retry; raw evidence remains saved");

                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                var originalFields = SnapshotSettings(original);
                var inputSettings = new List<BenchSettings>();
                captureTestHook = settings =>
                {
                    inputSettings.Add(settings); calls++;
                    var e = CreateReviewCapture(settings, Path.Combine(root, "adjust_" + calls), calls == 1 ? .95 : .55, false);
                    captures.Add(e); return Task.FromResult(e.Capture);
                };
                rangeDecisionTestHook = context => { decisions++; CheckReview(context.RetriesUsed == 0, "second suitable result should not prompt"); return RangeDecisionAction.AdjustRetry; };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 2 && decisions == 1, "one retry reaches suitable range");
                CheckOnlyRangeRunChanged(originalFields, inputSettings[1]);
                CheckReview(original.RangeIndex == 3 && original.RunNumber == 40 && inputSettings[1].RangeIndex == 4 && inputSettings[1].RunNumber == 41, "retry cloned exact range/run");
                CheckReviewEvidence(captures[0]); CheckReviewEvidence(captures[1]);
                var parentAnalysis = ReadReviewAnalysis(captures[0].Capture.DirectoryPath);
                CheckReview(parentAnalysis.RangeDecision.Action == RangeDecisionAction.AdjustRetry && parentAnalysis.RetryCaptureDirectory == captures[1].Capture.DirectoryPath &&
                    analysis.ParentCaptureDirectory == captures[0].Capture.DirectoryPath && analysis.RangeRetryNumber == 1, "retry parent/child records linked");
                summary.Add("PASS AdjustRetry: exactly two captures, only range/run changed, original target/identities/calibration retained, parent-child records linked");

                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "still_clipped_" + calls), 1, true); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context =>
                {
                    decisions++; string reason;
                    if (context.RetriesUsed == 0) return RangeDecisionAction.AdjustRetry;
                    CheckReview(context.RetriesUsed == 1 && !RangeDecisionPolicy.CanAdjustAndRetry(context, out reason), "second clipped capture cannot retry again");
                    return RangeDecisionAction.Dismiss;
                };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 2 && decisions == 2 && analysis.LocationRange.LocationClippedSamples > 0, "no third capture after repeated clipping");
                CheckReviewEvidence(captures[0]); CheckReviewEvidence(captures[1]);
                summary.Add("PASS Retry limit: second clipped result remains flagged; adjustment disabled; no third capture");

                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings =>
                {
                    calls++;
                    if (calls == 2)
                    {
                        var failure = new TaskCompletionSource<BenchCapture>(); failure.SetException(new InvalidOperationException("OFFLINE_INJECTED_RETRY_FAILURE")); return failure.Task;
                    }
                    var e = CreateReviewCapture(settings, Path.Combine(root, "retry_failure_parent"), 1, true); captures.Add(e); return Task.FromResult(e.Capture);
                };
                rangeDecisionTestHook = context => { decisions++; return RangeDecisionAction.AdjustRetry; };
                bool failed = false;
                try { await RunWithRangeReviewAsync(original); } catch (InvalidOperationException ex) { failed = ex.Message.Contains("OFFLINE_INJECTED_RETRY_FAILURE"); }
                CheckReview(failed && calls == 2 && decisions == 1 && captures.Count == 1, "injected retry failure propagated");
                parentAnalysis = ReadReviewAnalysis(captures[0].Capture.DirectoryPath);
                CheckReview(parentAnalysis.RetryOutcome.Contains("OFFLINE_INJECTED_RETRY_FAILURE") && parentAnalysis.RangeDecision.RequestedRetry, "first analysis records retry failure");
                CheckReviewEvidence(captures[0]);
                summary.Add("PASS Retry failure: parent analysis records failure; first raw ADC/settings remain intact and loadable");

                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); original.AutoRange = false; range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "auto_disabled"), 1, true); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context => { decisions++; throw new Exception("AutoRange=false must not prompt"); };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 1 && decisions == 0 && analysis.RangeDecision == null, "auto-range disabled no popup");
                CheckReviewEvidence(captures[0]);
                summary.Add("PASS AutoRange=false: no popup or adjustment; quality remains reported");

                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); original.AutoRange = false; original.RangeIndex = 10; range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "auto_disabled_maximum_warning"), 1, true); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context =>
                {
                    decisions++; string reason;
                    CheckReview(RangeDecisionPolicy.IsMaximumRangeRisk(context) && !RangeDecisionPolicy.CanAdjustAndRetry(context, out reason),
                        "maximum warning remains mandatory with AutoRange disabled");
                    return RangeDecisionAction.Dismiss;
                };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 1 && decisions == 1 && analysis.RangeDecision.Action == RangeDecisionAction.Dismiss &&
                    analysis.LocationRange.LocationClippedSamples > 0 && !analysis.RangeDecision.RequestedRetry,
                    "AutoRange=false maximum clipping must warn without retry");
                CheckReviewEvidence(captures[0]);
                summary.Add("PASS AutoRange=false maximum clipping: mandatory warning still called, no retry and clipping evidence preserved");

                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "already_suitable"), .30, false); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context => { decisions++; throw new Exception("Suitable unchanged range must not prompt"); };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 1 && decisions == 0 && analysis.LocationRange.LocationQualityPassed && !analysis.LocationRange.Change, "suitable unchanged result no popup");
                CheckReviewEvidence(captures[0]);
                summary.Add("PASS Suitable range: no popup, no repeat, data keeps current metadata");
                viewTabs.SelectedIndex = 0; await Task.Delay(40); SaveSmokeImage(stem + ".workflow.png");

                silentRangeSession = false;
                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); original.RangeIndex = 10; original.CalibrationPulsePc = .001;
                range.SelectedIndex = original.RangeIndex; originalFields = SnapshotSettings(original); inputSettings.Clear();
                captureTestHook = settings =>
                {
                    calls++; inputSettings.Add(settings);
                    var fractions = new[] { .06, .04, .02, .01 };
                    CheckReview(calls <= fractions.Length, "automatic lowering must terminate at minimum range");
                    var e = CreateReviewCapture(settings, Path.Combine(root, "automatic_lower_" + calls), fractions[calls - 1], false);
                    captures.Add(e); return Task.FromResult(e.Capture);
                };
                rangeDecisionTestHook = context =>
                {
                    decisions++; CheckReview(decisions == 1 && context.RetriesUsed == 0, "ordinary prompts suppressed after explicit opt-in");
                    silentRangeSession = true; return RangeDecisionAction.AdjustRetry;
                };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 4 && decisions == 1 && latest.Settings.RangeIndex == 0 && analysis.RangeDecision == null &&
                    analysis.RangeReviewStatus.Contains("已停止"), "automatic lowering reaches minimum without silent quality acceptance");
                for (int i = 0; i < captures.Count; i++)
                {
                    CheckReviewEvidence(captures[i]); CheckOnlyRangeRunChanged(originalFields, inputSettings[i]);
                    if (i < captures.Count - 1)
                    {
                        var saved = ReadReviewAnalysis(captures[i].Capture.DirectoryPath);
                        CheckReview(saved.RangeDecision.AutomaticRangeRetriesEnabled && saved.RangeDecision.MaximumRetries == 10 &&
                            saved.RangeDecision.WasAutomaticDecision == (i > 0), "persisted human opt-in versus automatic follow-up");
                    }
                }
                CheckReview(original.RangeIndex == 10 && original.RunNumber == 40, "automatic chain preserves original settings");
                summary.Add("PASS Session opt-in: first explicit choice, three range changes, later ordinary prompts suppressed; minimum boundary stops without acceptance; each raw/settings preserved");

                silentRangeSession = true;
                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "automatic_clip_" + calls), 1, true); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context =>
                {
                    decisions++; string reason;
                    CheckReview(context.CurrentRangeIndex == 10 && context.IsAutomaticRangeRetry &&
                        RangeDecisionPolicy.IsMaximumRangeRisk(context) && !RangeDecisionPolicy.CanAdjustAndRetry(context, out reason),
                        "maximum clipping warning bypasses suppression but cannot auto retry");
                    return RangeDecisionAction.Dismiss;
                };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 5 && decisions == 1 && latest.Settings.RangeIndex == 10 && analysis.RangeDecision.Action == RangeDecisionAction.Dismiss &&
                    !analysis.RangeDecision.WasAutomaticDecision && analysis.LocationRange.LocationClippedSamples > 0,
                    "automatic clipping stops at maximum with explicit unresolved warning");
                foreach (var evidence in captures) CheckReviewEvidence(evidence);
                summary.Add("PASS Automatic clipping: .1/.5/2/10/20 V ranges; last max attempt allowed, max still clipped forces warning despite suppression; no sixth capture");

                silentRangeSession = true;
                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "automatic_cycle_" + calls), calls == 1 ? .95 : .05, false); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context => { decisions++; throw new Exception("Repeated range must stop without an ordinary popup"); };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 2 && decisions == 0 && analysis.RangeDecision == null && analysis.RangeReviewStatus.Contains("已使用"), "visited range breaks oscillation");
                foreach (var evidence in captures) CheckReviewEvidence(evidence);
                summary.Add("PASS Visited-range guard: .1 to .2 V recommendation back to .1 V stops; no oscillation or automatic data acceptance");

                silentRangeSession = true;
                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); original.RangeIndex = 6; range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings => { calls++; var e = CreateReviewCapture(settings, Path.Combine(root, "automatic_suitable_with_change"), .70, false); captures.Add(e); return Task.FromResult(e.Capture); };
                rangeDecisionTestHook = context => { decisions++; throw new Exception("Suitable automatic range must stop even when a 65-percent optimization suggests another range"); };
                await RunWithRangeReviewAsync(original);
                CheckReview(calls == 1 && decisions == 0 && analysis.LocationRange.LocationQualityPassed && analysis.LocationRange.Change &&
                    latest.Settings.RangeIndex == 6 && analysis.LocationRange.RecommendedIndex == 7 && analysis.RangeDecision == null,
                    "automatic suitable occupancy stops before optional optimization");
                CheckReviewEvidence(captures[0]);
                summary.Add("PASS Suitable automatic range: 70-percent occupancy at 1 V stops despite 65-percent target recommending 2 V; no unnecessary capture");

                silentRangeSession = true;
                calls = decisions = 0; captures.Clear(); original = ReviewSettings(); range.SelectedIndex = original.RangeIndex;
                captureTestHook = settings =>
                {
                    calls++;
                    if (calls == 2)
                    {
                        cancellation.Cancel(); var cancelled = new TaskCompletionSource<BenchCapture>(); cancelled.SetCanceled(); return cancelled.Task;
                    }
                    var e = CreateReviewCapture(settings, Path.Combine(root, "automatic_cancel_parent"), 1, true); captures.Add(e); return Task.FromResult(e.Capture);
                };
                rangeDecisionTestHook = context => { decisions++; throw new Exception("Automatic cancellation case should not prompt"); };
                bool wasCancelled = false;
                try { await RunWithRangeReviewAsync(original); } catch (OperationCanceledException) { wasCancelled = true; }
                CheckReview(wasCancelled && calls == 2 && decisions == 0 && captures.Count == 1, "automatic retry cancellation propagated");
                parentAnalysis = ReadReviewAnalysis(captures[0].Capture.DirectoryPath);
                CheckReview(parentAnalysis.RetryOutcome.Contains("取消") && parentAnalysis.RangeDecision.WasAutomaticDecision,
                    "cancelled automatic retry updates saved parent outcome");
                CheckReviewEvidence(captures[0]); cancellation.Dispose(); cancellation = new CancellationTokenSource();
                summary.Add("PASS Automatic cancellation: parent raw/settings preserved, saved outcome says cancelled, no fabricated retry result");
                silentRangeSession = false;

                var testQuality = new LocationRangeResult { RecommendedIndex = 5, RecommendedFullScaleVolts = .5, Change = true,
                    LocationClippedSamples = 8, OutsideClippedSamples = 3, DriverOverflow = true,
                    LocationQuality = "定位窗口削顶，真实峰值未知，本轮幅值不合格。", LocationPeakFraction = 1,
                    Reason = "按定位原始峰值及校准预期保留余量；建议只供确认后使用，连续调档须本次会话明确选择。" };
                await RenderReviewDialog(new RangeDecisionContext(testQuality, 3, 5, 0, 0, false, false), absoluteImage, true, false, RangeDecisionAction.Ignore);
                await RenderReviewDialog(new RangeDecisionContext(testQuality, 3, 5, 0, 1, false, false), stem + ".retry-limit.png", false, false, RangeDecisionAction.Dismiss);
                await RenderReviewDialog(new RangeDecisionContext(testQuality, 3, 5, 0, 1, false, false), stem + ".automatic-opt-in.png", false, false, RangeDecisionAction.AdjustRetry, true, true);
                await RenderReviewDialog(new RangeDecisionContext(testQuality, 3, 5, 0, 0, false, false), stem + ".checked-ignore.png", true, false, RangeDecisionAction.Ignore, true, true);
                await RenderReviewDialog(new RangeDecisionContext(testQuality, 3, 5, 0, 0, false, false), stem + ".checked-close.png", true, false, RangeDecisionAction.Dismiss, true, true);
                var maximum = new LocationRangeResult { RecommendedIndex = 10, RecommendedFullScaleVolts = 20, Change = false,
                    LocationClippedSamples = 20, AtMaximumAndClipped = true, MaximumRangeInsufficient = true, LocationPeakFraction = 1,
                    LocationQuality = "±20 V 最大档仍削顶，无法恢复本轮真实峰值。", Reason = "没有可用的更大量程，先检查输入和保护。" };
                await RenderReviewDialog(new RangeDecisionContext(maximum, 10, 10, 0, 0, false, false), stem + ".max-range.png", false, false, RangeDecisionAction.Dismiss);
                await RenderReviewDialog(new RangeDecisionContext(maximum, 10, 10, 0, 4, false, false, true), stem + ".automatic-max-warning.png", false, false, RangeDecisionAction.Dismiss);
                await RenderReviewDialog(new RangeDecisionContext(testQuality, 3, 5, 10000, 0, true, true), stem + ".automatic-component.png", true, true, RangeDecisionAction.StopAutomatic);
                summary.Add("PASS Dialog rendering/buttons: XIONGXIN icon; manual hides automatic stop; checkbox switches manual limit1 to automatic limit10 only on Adjust; checked Ignore/Close never opts in; maximum-risk warning remains visible and retry disabled; automatic-pressure component stop only rendered");
                summary.Add("RANGE_REVIEW_UI=PASS");
                File.WriteAllLines(absoluteImage + ".test-summary.txt", summary.ToArray(), System.Text.Encoding.UTF8);
            }
            finally
            {
                rangeDecisionTestHook = null; captureTestHook = null; silentRangeSession = false;
                cancellation.Dispose(); cancellation = oldCancellation;
                // Keep smokeMode set for the remainder of this dedicated offline UI process.
            }
        }

        private async Task RenderReviewDialog(RangeDecisionContext context, string path, bool retryEnabled, bool stopVisible,
            RangeDecisionAction click, bool? setSuppression = null, bool? retryAfterToggle = null)
        {
            using (var dialog = new RangeDecisionDialog(context, Icon))
            {
                dialog.Show(this); dialog.PerformLayout(); await Task.Delay(60);
                Button[] buttons = ReviewButtons(dialog).ToArray();
                Button retry = buttons.Single(b => b.Text.StartsWith("调整并", StringComparison.Ordinal));
                Button stopButton = buttons.SingleOrDefault(b => b.Text.StartsWith("停止加压", StringComparison.Ordinal));
                CheckBox suppression = ReviewControls(dialog).OfType<CheckBox>().Single();
                CheckReview(dialog.Icon != null && retry.Enabled == retryEnabled && (stopButton != null && stopButton.Visible) == stopVisible, "dialog brand/visible actions");
                CheckReview(context.HasRealPressurization ? retry.Text.Contains("重新加压") : retry.Text.Contains("重新采集"), "accurate retry capability caption");
                if (setSuppression.HasValue)
                {
                    CheckReview(suppression.Enabled, "ordinary retry can opt in"); suppression.Checked = setSuppression.Value;
                    CheckReview(dialog.Record == null && dialog.Decision == RangeDecisionAction.Dismiss && !dialog.SuppressFurtherRangePrompts,
                        "checkbox alone cannot accept or trigger retry");
                    if (retryAfterToggle.HasValue) CheckReview(retry.Enabled == retryAfterToggle.Value, "checkbox immediately recomputes retry eligibility");
                    if (suppression.Checked) CheckReview(ReviewControls(dialog).OfType<TextBox>().Any(t => t.Text.Contains(" / 10")), "automatic limit shown after opt-in");
                }
                if (RangeDecisionPolicy.IsMaximumRangeRisk(context))
                    CheckReview(!suppression.Enabled && !retry.Enabled && ReviewControls(dialog).OfType<Label>().Any(label =>
                        label.Visible && label.Text.Contains("老化或损坏风险") && label.Text.Contains("不证明")), "maximum risk warning visible with conditional physical-risk wording");
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(path); }
                if (!retry.Enabled) { retry.PerformClick(); CheckReview(dialog.Decision == RangeDecisionAction.Dismiss && dialog.Record == null, "disabled retry cannot select action"); }
                if (click == RangeDecisionAction.Ignore) buttons.Single(b => b.Text.StartsWith("忽略", StringComparison.Ordinal)).PerformClick();
                else if (click == RangeDecisionAction.AdjustRetry) retry.PerformClick();
                else if (click == RangeDecisionAction.StopAutomatic) stopButton.PerformClick();
                else dialog.Close();
                CheckReview(dialog.Decision == click && dialog.Record != null && dialog.Record.Action == click, "real button/close decision matches");
                if (click == RangeDecisionAction.Ignore) CheckReview(!dialog.Record.OriginalQualityPassed && dialog.Record.QualitySnapshot.LocationClippedSamples > 0, "dialog Ignore preserves quality");
                CheckReview(dialog.SuppressFurtherRangePrompts == (click == RangeDecisionAction.AdjustRetry && suppression.Checked), "only explicit Adjust activates session suppression");
                if (dialog.SuppressFurtherRangePrompts) CheckReview(dialog.Record.AutomaticRangeRetriesEnabled && dialog.Record.MaximumRetries == 10, "opt-in decision records effective automatic limit");
            }
        }

        private static IEnumerable<Button> ReviewButtons(Control control)
        {
            foreach (Control child in control.Controls)
            {
                Button button = child as Button; if (button != null) yield return button;
                foreach (Button nested in ReviewButtons(child)) yield return nested;
            }
        }

        private static IEnumerable<Control> ReviewControls(Control control)
        {
            foreach (Control child in control.Controls)
            {
                yield return child;
                foreach (Control nested in ReviewControls(child)) yield return nested;
            }
        }

        private static BenchSettings ReviewSettings()
        {
            return new BenchSettings { RequestedPicoRateHz = 25e6, ResolutionBits = 15, CaptureDurationMs = 8,
                ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO", RangeIndex = 3, RunNumber = 40,
                AutoRange = true, CalibrationPulsePc = 100, PdCalibrationPcPerV = 2100, DividerVPerV = 11000 };
        }

        private static ReviewCaptureEvidence CreateReviewCapture(BenchSettings settings, string directory, double peakFraction, bool clipped)
        {
            CheckReview(!settings.RealHardware, "offline hook must never request hardware"); settings.Validate();
            var capture = new BenchCapture { Settings = settings, VoltageRaw = new double[settings.ExpectedArtCount],
                PdAdc = new short[settings.RequestedPicoCount], ArtRateHz = BenchSettings.ArtRate, PicoRateHz = settings.RequestedPicoRateHz,
                MaximumAdc = 32767, PicoFullScaleV = BenchSettings.Ranges[settings.RangeIndex],
                HardwareBits = PicoCaptureConfiguration.HardwareBits(settings.ResolutionBits, settings.RangeIndex),
                Overflow = clipped, Simulation = true, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO",
                Runtime = "OFFLINE RANGE REVIEW: synthesized arrays, no native driver", CapturedUtc = DateTime.UtcNow, DirectoryPath = directory };
            for (int i = 0; i < capture.VoltageRaw.Length; i++) capture.VoltageRaw[i] = (i % 2 == 0 ? 1 : -1) * .000001;
            for (int i = 0; i < capture.PdAdc.Length; i++) capture.PdAdc[i] = (short)(i % 2 == 0 ? 10 : -10);
            int start = capture.PdAdc.Length / 2;
            for (int i = start; i < start + 8; i++) capture.PdAdc[i] = (short)Math.Round((clipped ? 1 : peakFraction) * capture.MaximumAdc);
            capture.Save(directory);
            return new ReviewCaptureEvidence { Capture = capture, AdcBefore = (short[])capture.PdAdc.Clone(),
                RawHash = ReviewHash(Path.Combine(directory, "capture.bin")), SettingsText = File.ReadAllText(Path.Combine(directory, "settings.xml")) };
        }

        private static void CheckReviewEvidence(ReviewCaptureEvidence evidence)
        {
            string directory = evidence.Capture.DirectoryPath;
            CheckReview(evidence.RawHash == ReviewHash(Path.Combine(directory, "capture.bin")) &&
                evidence.SettingsText == File.ReadAllText(Path.Combine(directory, "settings.xml")), "capture.bin/settings.xml immutable");
            CheckReview(evidence.AdcBefore.SequenceEqual(evidence.Capture.PdAdc), "in-memory native ADC immutable");
            var restored = BenchCapture.Load(directory);
            CheckReview(restored.PdAdc.SequenceEqual(evidence.AdcBefore) && restored.Settings.RangeIndex == evidence.Capture.Settings.RangeIndex, "saved raw roundtrip remains valid");
        }

        private static Dictionary<string, object> SnapshotSettings(BenchSettings settings)
        {
            return typeof(BenchSettings).GetFields(BindingFlags.Public | BindingFlags.Instance).ToDictionary(f => f.Name, f => f.GetValue(settings));
        }
        private static void CheckOnlyRangeRunChanged(Dictionary<string, object> before, BenchSettings after)
        {
            foreach (FieldInfo field in typeof(BenchSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (field.Name != "RangeIndex" && field.Name != "RunNumber") CheckReview(Object.Equals(before[field.Name], field.GetValue(after)), "retry preserves " + field.Name);
        }
        private static string ReviewHash(string path)
        { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)); }
        private static BenchAnalysisRecord ReadReviewAnalysis(string directory)
        { using (var stream = File.OpenRead(Path.Combine(directory, "analysis.xml"))) return (BenchAnalysisRecord)new XmlSerializer(typeof(BenchAnalysisRecord)).Deserialize(stream); }
        private static void CheckReview(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Range review offline UI test failed: " + message); }
    }
}
