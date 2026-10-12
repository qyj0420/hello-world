using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace HVX.ReportGenerator
{
    public sealed partial class ReportGeneratorControl
    {
        string localAiLearningDirectory = "";
        string originalGeneratedConclusion = "";

        [Browsable(false)]
        public string LocalAiLearningDirectory
        {
            get { return string.IsNullOrWhiteSpace(localAiLearningDirectory) ? ReportConclusionLearning.DefaultRoot : localAiLearningDirectory; }
            set { localAiLearningDirectory = value ?? ""; }
        }

        double[] ConclusionLearningFeatures()
        {
            double accepted = 0, maximum = 0, sum = 0, samples = 0; int phaseMask = 0;
            if (locationMatrix != null && locationMatrix.GetLength(1) >= 36)
                for (int row = 0; row < locationMatrix.GetLength(0); row++)
                {
                    double phase = locationMatrix[row, 0];
                    if (phase == 1 || phase == 2 || phase == 3) phaseMask |= 1 << (int)phase;
                    if (locationMatrix[row, 35] != 1) continue;
                    accepted++;
                    double charge = locationMatrix[row, 16];
                    if (!ProjectReportData.IsFinite(charge)) charge = locationMatrix[row, 15];
                    if (ProjectReportData.IsFinite(charge)) { charge = Math.Abs(charge); maximum = Math.Max(maximum, charge); sum += charge; samples++; }
                }
            return new[] { FeatureNumber(projectData == null ? double.NaN : projectData.CableLengthM),
                FeatureNumber(projectData == null ? double.NaN : projectData.DeviceU0Kv),
                (double)(projectData == null ? 0 : projectData.JointPositions.Length),
                (double)(locationMatrix == null ? 0 : locationMatrix.GetLength(0)), accepted, maximum, samples > 0 ? sum / samples : 0, (double)phaseMask };
        }
        static double FeatureNumber(double value) { return ProjectReportData.IsFinite(value) ? value : 0; }

        Dictionary<string, object> ConclusionLearningContext()
        {
            return new Dictionary<string, object> {
                { "project", projectPath ?? "" }, { "analysis", analysisPath ?? "" },
                { "matrix_sha256", ReportConclusionLearning.MatrixHash(locationMatrix ?? new double[0, 0]) },
                { "matrix_rows", locationMatrix == null ? 0 : locationMatrix.GetLength(0) },
                { "matrix_columns", locationMatrix == null ? 0 : locationMatrix.GetLength(1) },
                { "acquisition_reference_only", HasAcquisitionRisk },
                { "acquisition_quality", analysisPackage==null?"{}":analysisPackage.AcquisitionQualityJson },
                { "cable_length_m", projectData != null && ProjectReportData.IsFinite(projectData.CableLengthM) ? (object)projectData.CableLengthM : null },
                { "operating_voltage_kv", projectData != null && ProjectReportData.IsFinite(projectData.DeviceU0Kv) ? (object)projectData.DeviceU0Kv : null },
                { "joint_positions_m", projectData == null ? new double[0] : projectData.JointPositions },
                { "brief_selected", briefConclusion.Checked }, { "professional_selected", professionalConclusion.Checked },
                { "algorithm", "local report conclusion" }, { "capture_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
        }

        void ShowConclusionFeedback()
        {
            if (busy || !HasAnalysisData) return;
            if(HasAcquisitionRisk){SetStatus(AcquisitionRiskShortWarning(),false);return;}
            string original = string.IsNullOrWhiteSpace(originalGeneratedConclusion) ? conclusion.Text : originalGeneratedConclusion;
            if (string.IsNullOrWhiteSpace(original)) return;
            // Context and original text are frozen at dialog entry. Opening/cancelling
            // the dialog never creates a sample or changes the report.
            using (var dialog = new ReportConclusionFeedbackDialog(languageIndex, LocalAiLearningDirectory,
                original, conclusion.Text, ConclusionLearningContext(), ConclusionLearningFeatures()))
            {
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.AcceptedConclusion != null)
                    SetConclusionText(dialog.AcceptedConclusion);
            }
        }
    }

    internal sealed class ReportConclusionFeedbackDialog : Form
    {
        readonly int language;
        readonly string root, original, current;
        readonly Dictionary<string, object> context;
        readonly double[] features;
        readonly TabControl pages = new TabControl { Dock = DockStyle.Fill };
        readonly RichTextBox revised = new RichTextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, MaxLength = 32000 };
        readonly NumericUpDown rating = new NumericUpDown { Minimum = 1, Maximum = 5, Value = 3, Width = 64 };
        readonly TextBox notes = new TextBox { Dock = DockStyle.Fill, MaxLength = 8000 };
        readonly DataGridView history = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical };
        readonly RichTextBox details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None };
        readonly Label message = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        internal string AcceptedConclusion { get; private set; }
        string T(string zh, string en) { return ReportLanguages.Ui(language, zh, en); }

        internal ReportConclusionFeedbackDialog(int languageIndex, string directory, string algorithmText, string reportText,
            Dictionary<string, object> dataContext, double[] vector)
        {
            language = languageIndex; root = directory; original = algorithmText; current = reportText; context = dataContext; features = vector;
            Text = T("本地 AI · 结论评分与修正", "Local AI · Conclusion review");
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Font = ReportTheme.UiFont(9.5f, FontStyle.Regular); BackColor = ReportTheme.Canvas;
            Size = new Size(880, 680); MinimumSize = new Size(650, 500); StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false;
            HVX.Presentation.OemUiBranding.Apply(this);
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var introduction = new Label { Dock = DockStyle.Fill, Text = T(
                "评分针对原结论（1 分较差，5 分很好）。反馈仅保存在本机；相似案例仅供参考，须人工确认。",
                "Rate the original conclusion (1 poor, 5 excellent). Feedback stays on this computer. Similar cases are references only and require human review."), ForeColor = ReportTheme.Muted };
            shell.Controls.Add(introduction, 0, 0);
            shell.SizeChanged += (s, e) => shell.RowStyles[0].Height = Math.Max(48, TextRenderer.MeasureText(introduction.Text, introduction.Font,
                new Size(Math.Max(100, shell.ClientSize.Width - 32), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 10);
            shell.Controls.Add(pages, 0, 1); shell.Controls.Add(message, 0, 2); Controls.Add(shell);
            BuildReviewPage(); BuildHistoryPage();
            Load += (s, e) =>
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - 30), Math.Min(MinimumSize.Height, area.Height - 30));
                Size = new Size(Math.Min(Width, area.Width - 30), Math.Min(Height, area.Height - 30));
                RefreshHistory();
            };
        }

        void BuildReviewPage()
        {
            var page = new TabPage(T("评分与修正", "Review and correction")) { BackColor = Color.White, Padding = new Padding(10) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var row in new[] { new RowStyle(SizeType.Absolute, 28), new RowStyle(SizeType.Percent, 40), new RowStyle(SizeType.Absolute, 28), new RowStyle(SizeType.Percent, 60), new RowStyle(SizeType.Absolute, 38), new RowStyle(SizeType.Absolute, 38), new RowStyle(SizeType.Absolute, 68) }) grid.RowStyles.Add(row);
            grid.Controls.Add(new Label { Dock = DockStyle.Fill, Text = T("原结论（保留审计）", "Original conclusion (retained for audit)") }, 0, 0);
            grid.Controls.Add(new RichTextBox { Dock = DockStyle.Fill, Text = original, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = ReportTheme.SurfaceAlt }, 0, 1);
            grid.Controls.Add(new Label { Dock = DockStyle.Fill, Text = T("修正后的结论 · 保存不等于应用", "Corrected conclusion · Saving alone does not apply it") }, 0, 2);
            revised.Text = current; revised.BorderStyle = BorderStyle.FixedSingle; grid.Controls.Add(revised, 0, 3);
            var scoreRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            scoreRow.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 5, 8, 0), Text = T("原结论评分 1–5", "Original conclusion rating 1–5") }); scoreRow.Controls.Add(rating);
            grid.Controls.Add(scoreRow, 0, 4);
            var noteRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            noteRow.Controls.Add(new Label { Dock = DockStyle.Fill, Text = T("修正说明", "Review notes"), TextAlign = ContentAlignment.MiddleLeft }, 0, 0); noteRow.Controls.Add(notes, 1, 0); grid.Controls.Add(noteRow, 0, 5);
            grid.Controls.Add(Buttons(
                Button(T("保存评分与修正", "Save feedback"), () => Save(false)),
                Button(T("保存并应用修正", "Save and apply correction"), () => Save(true)),
                Button(T("关闭", "Close"), () => Close())), 0, 6);
            page.Controls.Add(grid); pages.TabPages.Add(page);
        }
        void BuildHistoryPage()
        {
            var page = new TabPage(T("本地反馈记录", "Local feedback records")) { BackColor = Color.White, Padding = new Padding(10) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            history.Columns.Add("Time", T("时间（UTC）", "Time (UTC)")); history.Columns.Add("Language", T("语言", "Language")); history.Columns.Add("Score", T("评分", "Rating"));
            history.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            history.ColumnHeadersHeight = Math.Max(34, Font.Height + 12); history.RowTemplate.Height = Math.Max(32, Font.Height + 10);
            history.Columns[0].FillWeight = 60; history.Columns[1].FillWeight = 25; history.Columns[2].FillWeight = 15;
            history.SelectionChanged += (s, e) => ShowRecord();
            grid.Controls.Add(history, 0, 0); grid.Controls.Add(details, 0, 1);
            grid.Controls.Add(Buttons(Button(T("查找相似参考", "Find similar reference"), FindSuggestion),
                Button(T("复制到修正草稿", "Copy to correction draft"), CopyReference),
                Button(T("删除所选反馈", "Remove selected feedback"), RemoveSelected)), 0, 2);
            page.Controls.Add(grid); pages.TabPages.Add(page);
        }
        Button Button(string text, Action action)
        {
            var button = new ReportButton { Text = text, Dock = DockStyle.Fill, Margin = new Padding(4, 4, 4, 2) };
            ReportTheme.StyleSecondaryButton(button); button.Click += (s, e) => Guard(action); return button;
        }
        static TableLayoutPanel Buttons(params Button[] buttons)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = buttons.Length, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < buttons.Length; i++) { panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length)); panel.Controls.Add(buttons[i], i, 0); }
            bool arranging = false;
            Action fit = () =>
            {
                if (arranging || panel.Parent == null || panel.Width <= 0) return;
                arranging = true;
                try
                {
                    int height = ReportActionLayout.Arrange(panel, buttons, 3, 2, 1);
                    var grid = panel.Parent as TableLayoutPanel;
                    int row = grid == null ? -1 : grid.GetRow(panel);
                    if (row >= 0 && row < grid.RowStyles.Count && Math.Abs(grid.RowStyles[row].Height - height) > .1)
                        grid.RowStyles[row].Height = height;
                }
                finally { arranging = false; }
            };
            panel.SizeChanged += (s, e) => fit(); panel.ParentChanged += (s, e) => fit(); panel.Layout += (s, e) => fit();
            return panel;
        }
        void Guard(Action action)
        {
            try { action(); }
            catch (Exception) { MessageBox.Show(this, T("无法完成本地反馈操作。请确认本地学习组件及保存位置可用，原报告结论未改变。", "The local feedback operation could not be completed. Check the local learning component and storage access. The report conclusion has not changed."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        void Save(bool apply)
        {
            if (string.IsNullOrWhiteSpace(revised.Text)) { message.Text = T("请输入修正后的结论。", "Enter a corrected conclusion."); return; }
            ReportConclusionLearning.Save(root, original, current, revised.Text, (int)rating.Value, notes.Text,
                ReportLanguages.Codes[language], context, features, apply);
            message.Text = T("反馈已保存在本机。", "Feedback saved locally."); RefreshHistory();
            if (apply) { AcceptedConclusion = revised.Text; DialogResult = DialogResult.OK; Close(); }
        }
        Dictionary<string, object> Selected { get { return history.CurrentRow == null ? null : history.CurrentRow.Tag as Dictionary<string, object>; } }
        void RefreshHistory()
        {
            Guard(() =>
            {
                history.Rows.Clear();
                foreach (var entry in ReportConclusionLearning.Read(root).OrderByDescending(item => ReportConclusionLearning.Text(item, "created_utc")))
                {
                    int index = history.Rows.Add(ReportConclusionLearning.Text(entry, "created_utc"), ReportConclusionLearning.Text(entry, "language"), ReportConclusionLearning.Text(entry, "rating"));
                    history.Rows[index].Tag = entry;
                }
                ShowRecord();
                if (history.Rows.Count == 0) message.Text = T("本机尚无可用反馈记录。", "No active local feedback records are available.");
            });
        }
        void ShowRecord()
        {
            var item = Selected;
            if (item == null) { details.Clear(); return; }
            object data; item.TryGetValue("data_context", out data);
            details.Text = T("原结论", "Original conclusion") + "\r\n" + ReportConclusionLearning.Text(item, "original_conclusion") + "\r\n\r\n" +
                T("修正结论", "Corrected conclusion") + "\r\n" + ReportConclusionLearning.Text(item, "corrected_conclusion") + "\r\n\r\n" +
                T("修正说明", "Review notes") + "\r\n" + ReportConclusionLearning.Text(item, "notes") + "\r\n\r\n" +
                T("数据上下文（本机审计）", "Data context (local audit)") + "\r\n" + new JavaScriptSerializer().Serialize(data);
        }
        void FindSuggestion()
        {
            var suggestion = ReportConclusionLearning.Suggest(root, ReportLanguages.Codes[language], features);
            if (suggestion == null) { message.Text = T("未找到同语言且数据特征相近的本地参考。", "No same-language local reference with similar data features was found."); return; }
            string id = ReportConclusionLearning.Text(suggestion, "transaction_id");
            foreach (DataGridViewRow row in history.Rows)
                if (ReportConclusionLearning.Text(row.Tag as Dictionary<string, object>, "transaction_id") == id) { history.CurrentCell = row.Cells[0]; break; }
            message.Text = T("已选择相似参考；未修改报告，请核对当前数据后再复制草稿。", "Similar reference selected. The report is unchanged; review current data before copying a draft.");
        }
        void CopyReference()
        {
            var item = Selected; if (item == null) return;
            if (ReportConclusionLearning.Text(item, "language") != ReportLanguages.Codes[language]) { message.Text = T("请选择与报告语言一致的参考。", "Choose a reference in the report language."); return; }
            revised.Text = ReportConclusionLearning.Text(item, "corrected_conclusion"); pages.SelectedIndex = 0;
            message.Text = T("仅复制到草稿。应用前请核实电压、位置和诊断措辞。", "Copied to the draft only. Verify voltage, locations and diagnostic wording before applying.");
        }
        void RemoveSelected()
        {
            var item = Selected; if (item == null) return;
            if (MessageBox.Show(this, T("删除所选反馈？此条反馈将不再用于后续建议，原报告不变。", "Remove the selected feedback? It will no longer be used for future suggestions. The original report is unchanged."), Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            ReportConclusionLearning.Remove(root, ReportConclusionLearning.Text(item, "transaction_id")); RefreshHistory();
            message.Text = T("已移出本地学习样本，后续建议不再使用此条记录。", "Removed from active local samples; future suggestions will not use this record.");
        }
    }
}
