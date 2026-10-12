using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HVX.ProjectModel;

namespace HVX.ReportGenerator
{
    // Metadata-only picker: listing analyses must not load waveform matrices or
    // change the project. The original path remains the identity of every row.
    internal sealed class SavedAnalysisPicker : Form
    {
        internal readonly DataGridView Grid = new DataGridView();
        internal readonly Label Message = new Label();
        internal readonly Label SelectionInfo = new Label();
        internal readonly ReportButton LoadButton = new ReportButton();
        internal readonly ReportButton AttachmentsButton = new ReportButton();
        internal readonly ReportButton DeleteButton = new ReportButton();
        internal string[] SelectedPaths {get{return Grid.SelectedRows.Cast<DataGridViewRow>().Select(r=>Convert.ToString(r.Tag)).ToArray();}}
        readonly TableLayoutPanel layout = new TableLayoutPanel();
        readonly bool chinese;
        readonly int language;
        internal string SelectedPath { get { return Grid.SelectedRows.Count == 1 ? Convert.ToString(Grid.SelectedRows[0].Tag) : ""; } }

        internal SavedAnalysisPicker(int language, string title, string prompt, string closeText,
            string loadText, string[] paths, Font uiFont, Icon icon)
        {
            this.language = language; chinese = ReportLanguages.IsChinese(language);
            Text = title; Font = uiFont; AutoScaleMode = AutoScaleMode.None;
            BackColor = ReportTheme.Surface; ForeColor = ReportTheme.Ink;
            Padding = new Padding(16); StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.Sizable;
            Icon = icon; ShowIcon = icon != null;
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            ClientSize = new Size(Math.Min(1000, Math.Max(560, work.Width - 64)), Math.Min(590, Math.Max(340, work.Height - 96)));
            MinimumSize = new Size(Math.Min(680, work.Width - 24), Math.Min(420, work.Height - 24));
            layout.Dock = DockStyle.Fill; layout.ColumnCount = 1; layout.RowCount = 4;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height * 2 + 12));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height * 3 + 16));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 34)); Controls.Add(layout);
            Message.Name = "AnalysisPickerMessage"; Message.Text = prompt; Message.Dock = DockStyle.Fill;
            Message.TextAlign = ContentAlignment.MiddleLeft; Message.ForeColor = ReportTheme.Muted;
            layout.Controls.Add(Message, 0, 0);
            Grid.Name = "SavedAnalysisList"; Grid.Dock = DockStyle.Fill; Grid.ReadOnly = true;
            Grid.AllowUserToAddRows = false; Grid.AllowUserToDeleteRows = false; Grid.AllowUserToResizeRows = false;
            Grid.AllowUserToResizeColumns = false; Grid.RowHeadersVisible = false; Grid.MultiSelect = true;
            Grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; Grid.ScrollBars = ScrollBars.Vertical;
            Grid.BackgroundColor = ReportTheme.Surface; Grid.BorderStyle = BorderStyle.FixedSingle;
            Grid.EnableHeadersVisualStyles = false; Grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            Grid.ColumnHeadersDefaultCellStyle.BackColor = ReportTheme.Header;
            Grid.ColumnHeadersDefaultCellStyle.ForeColor = ReportTheme.Ink;
            Grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True; Grid.DefaultCellStyle.Padding = new Padding(6, 5, 6, 5);
            Grid.DefaultCellStyle.SelectionBackColor = ReportTheme.Selection; Grid.DefaultCellStyle.SelectionForeColor = Color.White;
            Grid.AlternatingRowsDefaultCellStyle.BackColor = ReportTheme.SurfaceAlt;
            Grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells; Grid.GridColor = ReportTheme.Border;
            int timeWidth = TextRenderer.MeasureText("2026-09-28 23:59:59", Font).Width + 24;
            Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = ReportLanguages.Ui(language, "时间", "Time"), Width = timeWidth, MinimumWidth = timeWidth, SortMode = DataGridViewColumnSortMode.NotSortable });
            Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AnalysisName", HeaderText = ReportLanguages.Ui(language, "分析名称", "Analysis name"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (string path in paths)
            {
                string name = DisplayName(path); DateTime date = DateTime.MinValue;
                try { date = Directory.Exists(path) ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                int row = Grid.Rows.Add(date == DateTime.MinValue ? "" : AnalysisDateText.FormatLocalTime(date), name);
                Grid.Rows[row].Tag = path; Grid.Rows[row].Cells[1].ToolTipText = name;
            }
            layout.Controls.Add(Grid, 0, 1);
            SelectionInfo.Name = "SelectedAnalysisDetails"; SelectionInfo.Dock = DockStyle.Fill;
            SelectionInfo.Padding = new Padding(4, 8, 4, 4); SelectionInfo.UseMnemonic = false;
            SelectionInfo.ForeColor = ReportTheme.Ink; SelectionInfo.BackColor = ReportTheme.SurfaceAlt;
            layout.Controls.Add(SelectionInfo, 0, 2);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Padding = new Padding(0, 10, 0, 0) };
            var close = new ReportButton { Text = closeText, DialogResult = DialogResult.Cancel };
            LoadButton.Text = loadText; AttachmentsButton.Text = ReportLanguages.Ui(language, "报告附件", "Report attachments");
            DeleteButton.Text=ReportLanguages.Ui(language,"删除所选","Delete selected");ReportTheme.StyleSecondaryButton(DeleteButton);DeleteButton.ForeColor=ReportTheme.Error;
            ReportTheme.StyleSecondaryButton(close); ReportTheme.StylePrimaryButton(LoadButton); ReportTheme.StyleSecondaryButton(AttachmentsButton);
            foreach (ReportButton button in new[] { close, LoadButton, AttachmentsButton, DeleteButton })
            {
                button.Font = Font; button.AutoEllipsis = false;
                button.Size = new Size(Math.Max(110, TextRenderer.MeasureText(button.Text, Font).Width + 32), Font.Height + 18);
                button.Margin = new Padding(6, 0, 0, 0); footer.Controls.Add(button);
            }
            layout.Controls.Add(footer, 0, 3); AcceptButton = LoadButton; CancelButton = close;
            ReportTheme.BindDialogFooter(this, footer, height => layout.RowStyles[3].Height = height);
            LoadButton.Enabled = AttachmentsButton.Enabled = false;
            Grid.SelectionChanged += (s, e) => UpdateSelection();
            layout.SizeChanged += (s, e) => FitDetails();
            Shown += (s, e) => { Grid.ClearSelection(); Grid.CurrentCell = null; UpdateSelection(); };
            UpdateSelection();
        }
        void UpdateSelection()
        {
            bool selected = Grid.SelectedRows.Count == 1;
            DeleteButton.Enabled=Grid.SelectedRows.Count>0;
            LoadButton.Enabled = AttachmentsButton.Enabled = selected;
            SelectionInfo.Text = selected ? (ReportLanguages.Ui(language, "当前选择：", "Selected: ")) + Convert.ToString(Grid.SelectedRows[0].Cells[1].Value)
                : (ReportLanguages.Ui(language, "请选择一条分析记录。完整名称会显示在这里。", "Select an analysis. Its complete name will appear here."));
            FitDetails();
        }
        void FitDetails()
        {
            int width = Math.Max(120, layout.ClientSize.Width - 16);
            int height = TextRenderer.MeasureText(SelectionInfo.Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 24;
            layout.RowStyles[2].Height = Math.Max(Font.Height * 2 + 20, height);
        }
        internal static string DisplayName(string path)
        {
            string fallback = AnalysisDateText.DisplayAutomaticName(Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path));
            if (!Directory.Exists(path)) return fallback;
            string manifest = Path.Combine(path, "analysis.json");
            try
            {
                const int limit = 262144;
                using (var reader = new StreamReader(manifest))
                {
                    char[] text = new char[limit + 1]; int length = reader.ReadBlock(text, 0, text.Length);
                    if (length > limit) return fallback;
                    var json = new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 32 };
                    var values = json.DeserializeObject(new string(text, 0, length)) as Dictionary<string, object>;
                    if (values == null) return fallback;
                    foreach (string key in new[] { "Name", "name", "AnalysisName", "analysis_name" })
                    {
                        object value;
                        if (!values.TryGetValue(key, out value)) continue;
                        string name = value as string;
                        if (!string.IsNullOrWhiteSpace(name) && name.Length <= 160 && !name.Any(char.IsControl)) return AnalysisDateText.DisplayAutomaticName(name.Trim());
                    }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            return fallback;
        }
    }
}
