using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal sealed class CalibrationTimingObservation
    {
        internal double Length;
        internal int PeakIncident, PeakReflection, ArrivalIncident, ArrivalReflection;
    }
    internal sealed class CalibrationVelocityAdoption
    {
        internal long UtcTicks; internal bool FirstArrival; internal string Level;
        internal double MeasuredVelocity, AdoptedVelocity, IncidentSeconds, ReflectionSeconds;
    }
    public sealed class CalibrationVelocityReviewRow
    {
        public string Level { get; internal set; }
        public double OriginalMeasuredVelocity { get; internal set; }
        public double MeasuredVelocity { get; internal set; }
        public double AdoptedVelocity { get; internal set; }
        public double IncidentSeconds { get; internal set; }
        public double ReflectionSeconds { get; internal set; }
        public string Status { get; internal set; }
    }
    public sealed partial class CalibrationProgramControl
    {
        public CalibrationVelocityReviewRow[] GetPhaseVelocityReview(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex); var rows = new List<CalibrationVelocityReviewRow>();
            lock (stateLock)
            {
                PhaseState p = GetPhaseStateNoLock(phaseIndex);
                foreach (string level in LevelCodes)
                {
                    WaveformData wave; if (!p.Waveforms.TryGetValue(level, out wave)) continue;
                    var row = new CalibrationVelocityReviewRow { Level = level, OriginalMeasuredVelocity = Double.NaN, MeasuredVelocity = Double.NaN, AdoptedVelocity = Double.NaN, IncidentSeconds = Double.NaN, ReflectionSeconds = Double.NaN, Status = CalibrationText("需复核", "Review required") };
                    MarkerData marker;
                    if (p.Markers.TryGetValue(level, out marker) && marker.DistanceMarkerSeconds > marker.StartTimeSeconds)
                    {
                        row.IncidentSeconds = marker.StartTimeSeconds; row.ReflectionSeconds = marker.DistanceMarkerSeconds;
                        row.MeasuredVelocity = 2 * p.CableLengthMeters / ((row.ReflectionSeconds - row.IncidentSeconds) * 1e6);
                        row.Status = CalibrationText("可核对", "Available");
                    }
                    CalibrationTimingObservation original;
                    if (p.OriginalObservations.TryGetValue(level, out original))
                    {
                        int i = UseFirstArrivalTiming ? original.ArrivalIncident : original.PeakIncident, r = UseFirstArrivalTiming ? original.ArrivalReflection : original.PeakReflection;
                        if (i >= 0 && r > i) row.OriginalMeasuredVelocity = 2 * original.Length / ((r - i) * wave.DxSeconds * 1e6);
                    }
                    double value; if (p.VelocityAdoptions.TryGetValue(level, out value)) row.AdoptedVelocity = value; rows.Add(row);
                }
            }
            return rows.ToArray();
        }
        public double GetRecommendedVelocityMPerUs(int phaseIndex)
        {
            double[] values = GetPhaseVelocityReview(phaseIndex).Where(row => IsFinite(row.MeasuredVelocity) && row.MeasuredVelocity > 0).Select(row => row.MeasuredVelocity).OrderBy(v => v).ToArray();
            if (values.Length == 0) return Double.NaN; int mid = values.Length / 2;
            return values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid];
        }
        public bool RequiresPhaseVelocityReview(int phaseIndex)
        {
            var rows = GetPhaseVelocityReview(phaseIndex); if (rows.Length < 2) return false;
            double[] measured = rows.Where(row => IsFinite(row.MeasuredVelocity)).Select(row => row.MeasuredVelocity).ToArray();
            if (measured.Length != rows.Length) return true;
            double suggestion = GetRecommendedVelocityMPerUs(phaseIndex);
            if (measured.Max() - measured.Min() <= Math.Max(.5, suggestion * .01)) return false;
            return rows.Any(row => !IsFinite(row.AdoptedVelocity) || !NearlyEqual(row.AdoptedVelocity, GetPhaseVelocityMPerUs(phaseIndex)));
        }
        public string GetPhaseVelocityReviewToken(int phaseIndex)
        {
            var text = new StringBuilder(); text.Append(phaseIndex).Append('|').Append(UseFirstArrivalTiming).Append('|').Append(SimulationMode).Append('|').Append(projectPath);
            foreach (var row in GetPhaseVelocityReview(phaseIndex)) text.Append('|').Append(row.Level).Append(':').Append(row.IncidentSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(row.ReflectionSeconds.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(row.MeasuredVelocity.ToString("R", CultureInfo.InvariantCulture)).Append(':').Append(row.AdoptedVelocity.ToString("R", CultureInfo.InvariantCulture));
            return CalibrationHash(Encoding.UTF8.GetBytes(text.ToString()));
        }
        public bool ApplyPhaseVelocity(int phaseIndex, double velocity, string reviewToken, bool confirmed)
        {
            ValidatePhaseIndex(phaseIndex);
            if (!confirmed) return false;
            if (!IsFinite(velocity) || velocity <= 0 || velocity > 1000) return CalibrationFailure("波速必须是有效正数。", "Velocity must be a valid positive value.");
            lock (stateLock)
            {
                if (!String.Equals(GetPhaseVelocityReviewToken(phaseIndex), reviewToken, StringComparison.Ordinal)) return CalibrationFailure("校准数据已改变，请重新打开波速核对。", "Calibration changed. Open the velocity review again.");
                CalibrationVelocityReviewRow[] rows = GetPhaseVelocityReview(phaseIndex);
                if (rows.Length == 0 || rows.Any(row => !IsFinite(row.MeasuredVelocity))) return CalibrationFailure("该相有未确认的计时点，请先完成所有档位的校准核对。", "This phase contains unconfirmed timing points. Review every recorded level first.");
                PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                if (phase.VelocityHistory.Count + rows.Length > 10000) return CalibrationFailure("波速修订记录已达上限，请另存项目后继续。", "The velocity revision limit is reached. Save a new project before continuing.");
                foreach (var row in rows)
                {
                    phase.VelocityHistory.Add(new CalibrationVelocityAdoption { UtcTicks = DateTime.UtcNow.Ticks, FirstArrival = UseFirstArrivalTiming, Level = row.Level, MeasuredVelocity = row.MeasuredVelocity, AdoptedVelocity = velocity, IncidentSeconds = row.IncidentSeconds, ReflectionSeconds = row.ReflectionSeconds });
                    phase.VelocityAdoptions[row.Level] = velocity;
                    phase.Markers[row.Level] = new MarkerData(row.IncidentSeconds, row.ReflectionSeconds,
                        row.IncidentSeconds + 2 * phase.CableLengthMeters / (velocity * 1e6), (row.ReflectionSeconds - row.IncidentSeconds) * velocity * 1e6 / 2);
                    PublishLevelNoLock(phase, row.Level);
                }
                phase.VelocityMPerUs = velocity; System.Threading.Interlocked.Increment(ref parameterChangedSequence);
            }
            ResetViewInternal(); Invalidate(); var handler = ParameterChanged; if (handler != null) handler(this, EventArgs.Empty); return true;
        }
        public bool ShowPhaseVelocityReview()
        {
            int phase = SelectedPhaseIndex; var rows = GetPhaseVelocityReview(phase); string token = GetPhaseVelocityReviewToken(phase);
            double recommendation = GetRecommendedVelocityMPerUs(phase);
            using (var dialog = new Form())
            {
                dialog.Text = CalibrationText("波速核对", "Velocity review") + " · " + PhaseCodeFromIndex(phase);
                dialog.Icon = CalibrationBranding.LoadIcon(); dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.AutoScaleDimensions = new SizeF(96, 96); dialog.AutoScaleMode = AutoScaleMode.Dpi; dialog.Font = new Font("Microsoft YaHei UI", 9F);
                dialog.ClientSize = new Size(820, 490); dialog.MinimumSize = new Size(720, 450); dialog.MaximizeBox = false;
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14), BackColor = Color.White };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); dialog.Controls.Add(root);
                var notice = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 10), Text = CalibrationText("仅修改当前项目的本相全部档位；ALL不代表A/B/C。原始测量与修订记录保留。", "Only this project's current phase is affected. ALL does not mean A/B/C. Original measurements and revisions are retained.") }; root.Controls.Add(notice, 0, 0);
                var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, ScrollBars = ScrollBars.Vertical, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
                foreach (string header in new[] { CalibrationText("档位", "Level"), CalibrationText("原始测得", "Original"), CalibrationText("当前测得", "Measured"), CalibrationText("与建议差值", "Difference"), CalibrationText("已采用", "Adopted"), CalibrationText("状态", "Status") }) grid.Columns.Add("c" + grid.Columns.Count, header);
                foreach (var row in rows) grid.Rows.Add(row.Level, VelocityDisplay(row.OriginalMeasuredVelocity), VelocityDisplay(row.MeasuredVelocity), VelocityDisplay(row.MeasuredVelocity - recommendation), VelocityDisplay(row.AdoptedVelocity), row.Status);
                root.Controls.Add(grid, 0, 1);
                var summary = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 10), Text = CalibrationText("建议波速（中位数，不是重新实测）：", "Suggested velocity (median, not a new measurement): ") + VelocityDisplay(recommendation) + " m/µs\r\n" + CalibrationText("计时模式：", "Timing: ") + (UseFirstArrivalTiming ? CalibrationText("首达", "First arrival") : CalibrationText("波峰", "Peak")) }; root.Controls.Add(summary, 0, 2);
                root.SizeChanged += delegate { int width = Math.Max(200, root.ClientSize.Width - root.Padding.Horizontal - 8); notice.MaximumSize = new Size(width, 0); summary.MaximumSize = new Size(width, 0); };
                notice.MaximumSize = summary.MaximumSize = new Size(780, 0);
                var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                var cancel = new HVX.Presentation.R2RoundedButton { AutoSize = true, Padding = new Padding(14, 6, 14, 6), Text = CalibrationText("取消", "Cancel"), DialogResult = DialogResult.Cancel };
                var apply = new HVX.Presentation.R2RoundedButton { AutoSize = true, Padding = new Padding(14, 6, 14, 6), Text = CalibrationText("确认采用建议波速", "Confirm suggested velocity"), Enabled = rows.Length > 0 && rows.All(row => IsFinite(row.MeasuredVelocity)), DialogResult = DialogResult.OK };
                actions.Controls.Add(cancel); actions.Controls.Add(apply); root.Controls.Add(actions, 0, 3); dialog.CancelButton = cancel;
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return false;
            }
            return ApplyPhaseVelocity(phase, recommendation, token, true);
        }
        private static string VelocityDisplay(double value) { return IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "—"; }
    }
}
