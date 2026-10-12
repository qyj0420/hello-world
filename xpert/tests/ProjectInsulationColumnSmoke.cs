using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;
using HVX.Controls;

internal static class ProjectInsulationColumnSmoke
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static object Call(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, All).Invoke(owner, args); }
    static void SetStatusValue(object status, string name, object value)
    {
        var property = status.GetType().GetProperty(name, All);
        if (property != null && property.GetSetMethod(true) != null) { property.SetValue(status, value, null); return; }
        var field = status.GetType().GetField(name, All) ?? status.GetType().GetField("<" + name + ">k__BackingField", All);
        if (field == null) throw new Exception("Cannot compose test-only status: " + name);
        field.SetValue(status, value);
    }
    // Synthetic immutable reader result for UI tests only. Parsing and latest-
    // analysis selection are covered separately by the reader's own fixture.
    static ProjectInsulationStatus Status(string state, int code, string text)
    {
        var value = (ProjectInsulationStatus)FormatterServices.GetUninitializedObject(typeof(ProjectInsulationStatus));
        SetStatusValue(value, "StateCode", state); SetStatusValue(value, "SeverityCode", code); SetStatusValue(value, "StatusText", text);
        SetStatusValue(value, "AnalysisId", "test-analysis"); SetStatusValue(value, "AnalysisName", "QA最新分析");
        SetStatusValue(value, "AnalysisSavedUtc", new DateTime(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc));
        SetStatusValue(value, "Detail", "synthetic UI fixture; no hardware"); return value;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string qa = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            string root = Path.Combine(qa, "Unanalyzed"); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "ITEM.in"), "始端地点 = QA起点\r\n终端地点 = QA终点\r\n电缆长度 = 170\r\n日期 = 2026-09-29\r\n时间 = 12:30\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "CABLE.in"), "0\tALL\r\n1\t始端\t冷缩\t0\r\n1\t终端\t冷缩\t170\r\n", Encoding.UTF8);
            var bytes = Directory.GetFiles(root).ToDictionary(p => p, File.ReadAllBytes);
            ProjectRecord scanned = ProjectScanner.ReadProject(root);
            Check(scanned.InsulationStatus != null && scanned.InsulationStatus.StateCode == "Unanalyzed", "Project scanner reads status without requiring analysis files");
            Check(scanned.CableLengthMetres == 170 && scanned.ProjectName.Contains("QA起点") && !scanned.HasReport, "Original metadata/report fields preserved");
            string[] expected = { "未分析", "未生成结论", "结论不可用", "记录", "观察", "重点关注", "及时处理", "结论不可用" };
            var states = new[] { Status("Unanalyzed", -1, expected[0]), Status("NoConclusion", -1, expected[1]), Status("Invalid", -1, expected[2]),
                Status("Ready", 0, expected[3]), Status("Ready", 1, expected[4]), Status("Ready", 2, expected[5]), Status("Ready", 3, expected[6]), Status("Ready", 9, expected[7]) };
            var records = states.Select((status, index) => new ProjectRecord { ProjectPath = Path.Combine(qa, "row-" + index),
                ProjectName = "项目-" + index, CableLengthMetres = 100 + index, HasTestTime = true,
                TestTime = new DateTime(2026, 9, 29).AddMinutes(index), DisplayTestTime = "2026-09-29", InsulationStatus = status }).ToArray();
            for (int report = 0; report < 46; report++) records[7].MutableReports.Add(new ProjectReportFile {
                FullPath = Path.Combine(qa, "synthetic-report-" + report), RelativePath = "synthetic-report-" + report,
                Extension = report < 12 ? ".docx" : ".pdf" });
            records[7].CompleteReports();
            Check(ProjectQuery.Matches(records[6], "处理") && ProjectQuery.Matches(records[6], "QA最新分析"), "Search supports status and source analysis name");
            Check(ProjectQuery.Matches(records[6], "项目-6") && ProjectQuery.Matches(records[6], "106"), "Existing name and length searches unchanged");
            foreach (ProjectSortField sort in Enum.GetValues(typeof(ProjectSortField)))
                Check(ProjectQuery.FilterAndSort(records, "", sort, ProjectSortDirection.Ascending).SequenceEqual(records), "Existing ascending sort unchanged: " + sort);
            using (var control = new ProjectManagementControl { EmbeddedMode = true, Dock = DockStyle.Fill })
            using (var host = new Form { ClientSize = new Size(1200, 700), ShowInTaskbar = false })
            {
                host.Controls.Add(control); host.Show(); Application.DoEvents();
                Call(control, "FinishScan", new ProjectScanResult { Ok = true, RootDirectory = qa, Projects = records });
                var grid = (DataGridView)Field(control, "grid");
                Check(grid.Columns.Count == 6 && grid.Columns["InsulationStatus"].DisplayIndex == grid.Columns["Report"].DisplayIndex + 1, "New column follows report without replacing existing columns");
                control.SetSort(ProjectSortField.ProjectName, ProjectSortDirection.Ascending);
                Check(grid.Rows.Cast<DataGridViewRow>().Select(r => Convert.ToString(r.Cells["InsulationStatus"].Value)).SequenceEqual(expected), "All reader states map correctly; unknown code never maps to normal");
                Check(!grid.Rows.Cast<DataGridViewRow>().Any(r => Convert.ToString(r.Cells["InsulationStatus"].Value).Contains("正常")), "Action code zero is record, not normal/pass");
                string tip = grid.Rows[6].Cells["InsulationStatus"].ToolTipText;
                Check(tip.Contains("QA最新分析") && tip.Contains("2026-09-29") && tip.Contains("synthetic UI fixture"), "Tooltip retains latest analysis name, time and source detail");
                control.SearchKeyword = "处理";
                Check(grid.Rows.Count == 1 && Convert.ToString(grid.Rows[0].Cells["ProjectName"].Value) == "项目-6", "Visible grid status search works");
                control.SearchKeyword = "";
                foreach (float fontSize in new[] { 9.5f, 15f, 20f })
                using (var font = new Font("Microsoft YaHei UI", fontSize))
                using (var header = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold))
                {
                    grid.Font = font; grid.DefaultCellStyle.Font = font; grid.ColumnHeadersDefaultCellStyle.Font = header;
                    foreach (int width in new[] { 720, 1065, 1600 })
                    for (int language = 0; language < 19; language++)
                    {
                        host.ClientSize = new Size(width, 700); control.LanguageIndex = language;
                        Call(control, "FitGridColumns"); Application.DoEvents();
                        var column = grid.Columns["InsulationStatus"];
                        var reportColumn = grid.Columns["Report"];
                        string scenario = fontSize + "pt/" + width + "/lang" + language;
                        Check(column.DefaultCellStyle.WrapMode == DataGridViewTriState.False && column.ReadOnly, "Status is a read-only single line: " + scenario);
                        Check(reportColumn.DefaultCellStyle.WrapMode == DataGridViewTriState.False, "Report status remains a single line: " + scenario);
                        Check(TextRenderer.MeasureText(reportColumn.HeaderText, header, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 20 <= reportColumn.Width, "Full report heading fits: " + scenario);
                        Check(TextRenderer.MeasureText(column.HeaderText, header, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 20 <= column.Width, "Full status heading fits: " + scenario);
                        foreach (DataGridViewRow row in grid.Rows)
                        {
                            string text = Convert.ToString(row.Cells[column.Index].Value);
                            Size size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                            Check(size.Width + 12 <= column.Width && size.Height + 8 <= row.Height, "Status text is not clipped: " + scenario + "/" + text);
                            Size reportSize = TextRenderer.MeasureText(Convert.ToString(row.Cells[reportColumn.Index].Value), font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                            Check(reportSize.Width + 36 <= reportColumn.Width && reportSize.Height + 16 <= row.Height, "Full report badge fits: " + scenario);
                        }
                        if (grid.Columns.Cast<DataGridViewColumn>().Sum(c => c.Width) > grid.ClientSize.Width)
                        {
                            Check(grid.ScrollBars == ScrollBars.Both || grid.ScrollBars == ScrollBars.Horizontal, "Small screen permits horizontal navigation: " + scenario);
                            grid.FirstDisplayedScrollingColumnIndex = column.Index; Application.DoEvents();
                            Rectangle visible = grid.GetColumnDisplayRectangle(column.Index, true);
                            Check(visible.Width >= column.Width - 2, "Whole status column is accessible by scrolling: " + scenario);
                        }
                        if ((fontSize == 9.5f && width == 1600 && language == 0) || (fontSize == 20f && width == 720 && language == 1))
                            using (var bitmap = new Bitmap(host.Width, host.Height))
                            { host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(qa, "insulation-" + fontSize + "pt-" + width + "-lang" + language + ".png")); }
                        if (fontSize == 20f && width == 720 && language == 1)
                        {
                            grid.FirstDisplayedScrollingColumnIndex = reportColumn.Index;
                            grid.FirstDisplayedScrollingRowIndex = 3; Application.DoEvents();
                            Check(grid.GetColumnDisplayRectangle(reportColumn.Index, true).Width >= reportColumn.Width - 2, "Full report badge accessible on narrow viewport");
                            using (var bitmap = new Bitmap(host.Width, host.Height))
                            { host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(qa, "report-20pt-720-lang1.png")); }
                        }
                    }
                }
                host.Close();
            }
            foreach (var item in bytes) Check(item.Value.SequenceEqual(File.ReadAllBytes(item.Key)), "Project source stays byte-for-byte unchanged");
            Console.WriteLine("PROJECT_INSULATION_COLUMN_PASS=" + checks + ";hardware=false;source_read_only=true"); Console.WriteLine("EVIDENCE=" + qa); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ((TargetInvocationException)ex).InnerException : ex); return 1; }
    }
}
