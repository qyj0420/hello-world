using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class ProjectManagementControl
    {
        string LocalizedInsulationStatus(ProjectRecord project)
        {
            ProjectInsulationStatus value = project == null ? null : project.InsulationStatus;
            if (value == null || value.StateCode == "Unanalyzed") return T("未分析", "Not analyzed");
            if (value.StateCode == "NoConclusion") return T("未生成结论", "No conclusion");
            if (value.StateCode != "Ready") return T("结论不可用", "Conclusion unavailable");
            if (value.UsesEvidenceV3)
            {
                switch (value.SeverityCode)
                {
                    case 0: return T("证据不足", "Insufficient evidence");
                    case 1: return T("计划跟踪", "Planned follow-up");
                    case 2: return T("重点关注", "Priority attention");
                    case 3: return T("优先核查", "Priority verification");
                    default: return T("结论不可用", "Conclusion unavailable");
                }
            }
            switch (value.SeverityCode)
            {
                // Task15 action codes are not a pass/fail certificate. Code 0
                // deliberately remains "Record", never "Normal" or "Passed".
                case 0: return T("记录", "Record");
                case 1: return T("观察", "Observe");
                case 2: return T("重点关注", "Priority attention");
                case 3: return T("及时处理", "Timely action");
                default: return T("结论不可用", "Conclusion unavailable");
            }
        }

        string InsulationStatusToolTip(ProjectRecord project)
        {
            string text = LocalizedInsulationStatus(project);
            ProjectInsulationStatus value = project == null ? null : project.InsulationStatus;
            if (value == null) return text;
            if (!String.IsNullOrWhiteSpace(value.AnalysisName))
                text += Environment.NewLine + T("来源分析", "Source analysis") + ": " + value.AnalysisName;
            if (value.AnalysisSavedUtc != DateTime.MinValue)
                text += Environment.NewLine + T("保存时间", "Saved at") + ": " + value.AnalysisSavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (!String.IsNullOrWhiteSpace(value.Detail)) text += Environment.NewLine + value.Detail;
            if (!String.IsNullOrWhiteSpace(value.ApiVersion)) text += Environment.NewLine + "API: " + value.ApiVersion;
            return text;
        }

        void FitInsulationStatusColumn()
        {
            DataGridViewColumn column = grid.Columns["InsulationStatus"];
            Font bodyFont = grid.DefaultCellStyle.Font ?? grid.Font;
            Font headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
            var labels = new[] { T("未分析", "Not analyzed"), T("未生成结论", "No conclusion"),
                T("结论不可用", "Conclusion unavailable"), T("记录", "Record"), T("观察", "Observe"), T("重点关注", "Priority attention"), T("及时处理", "Timely action"),
                T("证据不足", "Insufficient evidence"), T("计划跟踪", "Planned follow-up"), T("优先核查", "Priority verification") };
            int width = Math.Max(148, labels.Max(label => TextRenderer.MeasureText(label, bodyFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width) + 24);
            width = Math.Max(width, TextRenderer.MeasureText(column.HeaderText, headerFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 28);
            // Preserve the widths of the existing columns. On small windows
            // the grid scrolls horizontally rather than truncating this status.
            column.MinimumWidth = width;
            column.Width = width;
            int rowHeight = Math.Max(38, TextRenderer.MeasureText("Ag国", bodyFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height + 20);
            grid.RowTemplate.Height = rowHeight;
            foreach (DataGridViewRow row in grid.Rows) row.Height = rowHeight;
        }

        void FitReportStatusColumn()
        {
            DataGridViewColumn column = grid.Columns["Report"];
            Font bodyFont = grid.DefaultCellStyle.Font ?? grid.Font;
            Font headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
            // The badge has an 8 px outer margin and 10 px inner padding on
            // each side. Size for the actual DOCX/PDF counts, not an estimate.
            int width = Math.Max(136, TextRenderer.MeasureText(T("无报告", "No report"), bodyFont,
                Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 36);
            foreach (DataGridViewRow row in grid.Rows)
                width = Math.Max(width, TextRenderer.MeasureText(Convert.ToString(row.Cells[column.Index].Value) ?? String.Empty,
                    bodyFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 36);
            width = Math.Max(width, TextRenderer.MeasureText(column.HeaderText, headerFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 28);
            column.MinimumWidth = width;
            column.Width = Math.Max(width, column.Width);
        }
    }
}
