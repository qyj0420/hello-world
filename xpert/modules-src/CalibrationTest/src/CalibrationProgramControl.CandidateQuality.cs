using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        // Display-only evidence. It is intentionally not a MarkerData, an analysis
        // result, or part of the calibration archive / coefficient saving contract.
        private sealed class ReflectionCandidateEvidence
        {
            internal WaveformData Source;
            internal int Index;
            internal double Seconds, Score, Ratio, Correlation;
            internal string Reason;
        }

        private static string ActualBandwidthText(PhaseState phase)
        {
            return phase.WideBand ? String.Format(CultureInfo.InvariantCulture,BuiltIn("实际截止 {0} MHz"),phase.BandwidthCutoffMHz.ToString("0.0", CultureInfo.InvariantCulture))
                : BuiltIn("实际截止：未施加低通");
        }

        private string ApplyAutomaticBandwidthOutcome(PhaseState phase, Xiongxin.Calibration.CalibrationCaptureResult capture)
        {
            if (IsCapacitiveLoad || capture == null || capture.Bandwidth == null) return String.Empty;
            if(capture.Bandwidth.Sufficient) {
                phase.ManualBandwidthPanelVisible=false;
                return String.Empty;
            }
            phase.ManualBandwidthPanelVisible = true;
            return CalibrationText("请核对波形，必要时手动调整显示滤波。", "Please review the waveform and adjust the display filter manually if needed.");
        }

        private void UpdateReflectionCandidateNoLock(PhaseState phase, string level, WaveformData wave, TdrAutoAnalysisResult result)
        {
            phase.ReflectionCandidates.Remove(level);
            if (IsCapacitiveLoad || wave == null || result == null ||
                result.SearchStatus == TdrSearchStatus.Ok || result.IncidentIndex < 0 ||
                result.IncidentIndex >= wave.Samples.Length || !IsFinite(result.IncidentMagnitude) ||
                result.IncidentMagnitude <= 0 || !IsFinite(phase.CableLengthMeters) || phase.CableLengthMeters <= 0 ||
                !IsFinite(phase.VelocityMPerUs) || phase.VelocityMPerUs <= 0 || !(wave.DxSeconds > 0)) return;
            double baseline, sigma;
            TdrReflectionAnalyzer.RawTimingStatistics(wave.Samples, out baseline, out sigma);
            if (!IsFinite(baseline) || !IsFinite(sigma) || sigma < 0) return;
            double travel = 2 * phase.CableLengthMeters / (phase.VelocityMPerUs * 1e6 * wave.DxSeconds);
            if (!IsFinite(travel) || travel < 16 || travel > wave.Samples.Length * 2d) return;
            int incident = result.IncidentIndex;
            int radius = Math.Max(4, Math.Min(64, (int)(travel / 12)));
            if (incident < radius || incident + radius >= wave.Samples.Length) return;
            // Broad fallback only inside the plausible 50–150% cable round-trip
            // interval; it never searches a flat trace or invents the predicted peak.
            int first = Math.Max(incident + radius * 2 + 1, (int)Math.Ceiling(incident + travel * .5));
            int last = Math.Min(wave.Samples.Length - radius - 1, (int)Math.Floor(incident + travel * 1.5));
            first = Math.Max(radius, first);
            double threshold = Math.Max(3 * sigma, Math.Max(1e-12, result.IncidentMagnitude * .002));
            ReflectionCandidateEvidence best = null;
            for (int i = first; i <= last; i++)
            {
                double peak = Math.Abs(wave.Samples[i] - baseline);
                if (peak <= threshold || peak < Math.Abs(wave.Samples[i - 1] - baseline) ||
                    peak <= Math.Abs(wave.Samples[i + 1] - baseline)) continue;
                // Reject isolated one-sample spikes and unresolved noise shoulders.
                if (Math.Abs(wave.Samples[i - 1] - baseline) < peak * .25 ||
                    Math.Abs(wave.Samples[i + 1] - baseline) < peak * .25) continue;
                double edge = Math.Max(Math.Abs(wave.Samples[i - radius] - baseline), Math.Abs(wave.Samples[i + radius] - baseline));
                if (peak - edge < Math.Max(2 * sigma, peak * .1)) continue;
                double correlation = Math.Abs(PacketCorrelation(wave.Samples, incident, i, radius));
                if (!IsFinite(correlation)) continue;
                var evidence = new TdrAutoAnalysisResult {
                    IncidentSnr = result.IncidentMagnitude / Math.Max(1e-15, sigma), ReflectionSnr = peak / Math.Max(1e-15, sigma),
                    TimingErrorSeconds = (i - incident - travel) * wave.DxSeconds, FitR2 = Double.NaN
                };
                // Reuse the existing SNR/timing/fit weighting; correlation limits
                // weak shape matches. Unconfirmed candidates can never score >=80.
                double score = Math.Min(79, 100 * TdrReflectionAnalyzer.CalculateConfidence(evidence, travel * .5 * wave.DxSeconds) * correlation);
                if (best != null && score <= best.Score) continue;
                best = new ReflectionCandidateEvidence { Source = wave, Index = i, Seconds = wave.X0Seconds + i * wave.DxSeconds,
                    Score = score, Ratio = peak / result.IncidentMagnitude, Correlation = correlation };
                best.Reason = "传播范围 50%–150%；形状相关 " + (correlation * 100).ToString("0.0", CultureInfo.InvariantCulture) +
                    "% / 幅比 " + (best.Ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) +
                    "% / 峰值噪声比 " + evidence.ReflectionSnr.ToString("0.0", CultureInfo.InvariantCulture) +
                    "；未通过可靠反射判据，只供人工核对，不用于保存反射参数；入射校准系数可单独保存并保留质量提示。";
            }
            if (best != null) phase.ReflectionCandidates[level] = best;
        }

        private void DrawReflectionCandidate(Graphics g, RectangleF plot, WaveformData wave, double x0, double x1, double y0, double y1)
        {
            if (IsCapacitiveLoad || !(x1 > x0)) return;
            ReflectionCandidateEvidence candidate;
            if (!GetPhaseStateNoLock(SelectedPhaseIndex).ReflectionCandidates.TryGetValue(SelectedLevelCode, out candidate) ||
                !Object.ReferenceEquals(candidate.Source, wave) || candidate.Seconds < x0 || candidate.Seconds > x1) return;
            float x = plot.Left + (float)((candidate.Seconds - x0) / (x1 - x0)) * plot.Width;
            float y = plot.Bottom - (float)((wave.Samples[candidate.Index] - y0) / Math.Max(1e-18, y1 - y0)) * plot.Height;
            var color = Color.FromArgb(148, 142, 114);
            DrawMarkerLine(g, plot, candidate.Seconds, x0, x1, Color.FromArgb(140, color));
            if (y >= plot.Top && y <= plot.Bottom) using (var pen = new Pen(color, 1.5f)) g.DrawEllipse(pen, x - 5, y - 5, 10, 10);
            string label = "反射候选 · 低可信 / 待确认 " + candidate.Score.ToString("0", CultureInfo.InvariantCulture) + "/100";
            float width = Math.Min(300, plot.Width - 12);
            ThemeText(g, label, new RectangleF(Math.Max(plot.Left + 6, Math.Min(plot.Right - width - 6, x + 6)), plot.Bottom - 30, width, 28), color, 8.5f);
        }

        private static double IncidentQualityEngineeringScore(Xiongxin.Calibration.CalibrationCaptureResult capture)
        {
            // Old archives do not preserve repeat-to-repeat variance. Do not infer
            // a numeric score from their default zero-valued assessment members.
            var q = capture == null ? null : capture.Assessment;
            if (q == null || capture.Windows == null || capture.Windows.Length < 2 || q.PulseCount < 2 ||
                Double.IsNaN(q.SnrDb) || !IsFinite(q.PeakVariationFraction) || q.PeakVariationFraction < 0 || !(q.IncidentPeakV > 0)) return Double.NaN;
            double snr = Math.Max(0, Math.Min(1, q.SnrDb / 30));
            double repeat = Math.Min(1, q.PulseCount / 8d);
            double variation = q.PeakVariationFraction <= .1 ? 1 : Math.Max(0, 1 - (q.PeakVariationFraction - .1) / .2);
            double score = 100 * Math.Min(snr, Math.Min(repeat, variation));
            return Math.Min(q.CircuitSuspected ? 49 : q.SaveRecommended ? 100 : 59, score);
        }

        private static string MeasurementScore(double score)
        { return IsFinite(score) ? score.ToString("0.0", CultureInfo.InvariantCulture) + " / 100" : "未评估（证据不足）"; }

        private static double ReflectionEngineeringScore(PhaseState phase, WaveformData wave, TdrAutoAnalysisResult result)
        {
            if (result == null || wave == null || result.SearchStatus != TdrSearchStatus.Ok || result.ReflectionIndex < 0 ||
                !(result.ReflectionMagnitude > 0) || !(result.IncidentMagnitude > 0)) return Double.NaN;
            double baseline, sigma=result.NoiseSigma;
            if(!IsFinite(sigma))TdrReflectionAnalyzer.RawTimingStatistics(wave.Samples,out baseline,out sigma);
            if (!IsFinite(sigma)) return Double.NaN;
            var evidence=new TdrAutoAnalysisResult { IncidentSnr=result.IncidentMagnitude/Math.Max(1e-15,sigma),
                ReflectionSnr=result.ReflectionMagnitude/Math.Max(1e-15,sigma),TimingErrorSeconds=result.TimingErrorSeconds,FitR2=result.FitR2 };
            double travel=2*phase.CableLengthMeters/(phase.VelocityMPerUs*1e6);
            return 100*TdrReflectionAnalyzer.CalculateConfidence(evidence,Math.Max(8*wave.DxSeconds,travel*.15));
        }

        private Form CreateMeasurementInformationDialog()
        {
            var dialog = new Form { Name = "CalibrationAssessmentDialog", Text = CalibrationText("校准信号质量", "Calibration signal quality"),
                StartPosition = FormStartPosition.CenterParent, Size = new Size(740, 590), MinimumSize = new Size(420, 330),
                BackColor = Color.White, Font = new Font("Microsoft YaHei UI", Math.Max(10, Font.SizeInPoints)), ShowInTaskbar = false };
            HVX.Presentation.OemUiBranding.Apply(dialog);
            var text = new TextBox { Name = "CalibrationMeasurementEvidence", Dock = DockStyle.Fill, Multiline = true,
                ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None,
                BackColor = Color.White, ForeColor = Theme.Ink, Text = measurementInformationText };
            var close = new HVX.Presentation.R2RoundedButton { Text = CalibrationText("关闭", "Close"), Dock = DockStyle.Bottom, Height = Math.Max(44, dialog.Font.Height + 18), DialogResult = DialogResult.OK };
            dialog.Padding = new Padding(16); dialog.Controls.Add(text); dialog.Controls.Add(close); dialog.AcceptButton = close; dialog.CancelButton = close;
            dialog.Shown += delegate { text.SelectionStart=0; text.SelectionLength=0; close.Focus(); };
            return dialog;
        }
    }
}
