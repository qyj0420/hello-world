using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ReportSettingsHeaderLayoutSmoke
{
    static int checks;
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string detail) { checks++; if (!ok) throw new Exception(detail); }
    static void Pump() { Application.DoEvents(); System.Threading.Thread.Sleep(15); Application.DoEvents(); }
    static void Shot(Form form, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); }
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string root = Path.GetFullPath(args[0]), qa = Path.GetFullPath(args[1]);
            if (Directory.Exists(qa)) throw new Exception("Use a new evidence directory.");
            Directory.CreateDirectory(qa);
            Assembly report = Assembly.LoadFrom(Path.Combine(root, "modules", "ReportGenerator", "Xiongxin.OWTS.ReportGenerator.dll"));
            Type type = report.GetType("HVX.ReportGenerator.ReportSettingsDialog", true);
            Type optionType = report.GetType("HVX.ReportGenerator.StatisticsRangeOption", true);
            Type intervalType = report.GetType("HVX.ReportGenerator.VoltageInterval", true);
            bool probe = args.Length > 2 && args[2] == "--baseline-probe";
            foreach (int language in new[] { 0, 1, 18 })
            foreach (int rowCount in new[] { 0, 2 })
            {
                IList source = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(optionType));
                for (int i = 0; i < rowCount; i++)
                {
                    object option = Activator.CreateInstance(optionType, true);
                    object interval = Activator.CreateInstance(intervalType, new object[] { "range" + i, "0.5U0–1.5U0", 0.5, 1.5 });
                    optionType.GetField("Interval", Hidden).SetValue(option, interval);
                    source.Add(option);
                }
                using (var dialog = (Form)Activator.CreateInstance(type, Hidden, null, new object[] { language, source }, null))
                {
                    dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(20, 20);
                    dialog.Show(); Pump();
                    var grid = (DataGridView)type.GetField("grid", Hidden).GetValue(dialog);
                    Font originalHeaderFont = grid.ColumnHeadersDefaultCellStyle.Font;
                    if (probe)
                    {
                        using (var font = new Font("Microsoft YaHei UI", 14.25f, FontStyle.Bold))
                        {
                            grid.ColumnHeadersDefaultCellStyle.Font = font; Pump();
                            bool clipped = grid.ColumnHeadersHeight < font.Height + grid.ColumnHeadersDefaultCellStyle.Padding.Vertical + 3;
                            Shot(dialog, Path.Combine(qa, "baseline-clipped.png"));
                            Console.WriteLine("BASELINE_HEADER_HEIGHT=" + grid.ColumnHeadersHeight + ";FONT_HEIGHT=" + font.Height + ";CLIPPED=" + clipped);
                            grid.ColumnHeadersDefaultCellStyle.Font = originalHeaderFont;
                            Check(clipped, "The old binary did not reproduce the reported fixed-header clipping.");
                            Console.WriteLine("BASELINE_CLIPPING_REPRODUCED=True;NO_HARDWARE=True");
                            return 0;
                        }
                    }
                    Check(grid.ColumnCount == 5 && grid.Rows.Count == rowCount, "Columns or data changed.");
                    Check(grid.ColumnHeadersHeightSizeMode == DataGridViewColumnHeadersHeightSizeMode.AutoSize, "Header must resize after font changes.");
                    foreach (int width in new[] { 620, 720 })
                    foreach (float points in new[] { 9.5f, 12f, 14.25f, 18f, 24f, 9.5f })
                    using (var font = new Font("Microsoft YaHei UI", points, FontStyle.Bold))
                    {
                        dialog.ClientSize = new Size(width, 390);
                        grid.ColumnHeadersDefaultCellStyle.Font = font;
                        Pump();
                        Check(grid.ColumnHeadersHeight >= font.Height + grid.ColumnHeadersDefaultCellStyle.Padding.Vertical + 3, "Header is shorter than a complete glyph plus padding.");
                        foreach (DataGridViewColumn column in grid.Columns)
                        {
                            Size required = column.HeaderCell.PreferredSize;
                            Check(grid.ColumnHeadersHeight >= required.Height, "Wrapped header is clipped: " + column.Name + " required=" + required.Height + " actual=" + grid.ColumnHeadersHeight);
                            Rectangle header = grid.GetCellDisplayRectangle(column.Index, -1, false);
                            Check(header.Height == grid.ColumnHeadersHeight && header.Bottom <= grid.ClientSize.Height, "Header leaves the grid viewport.");
                        }
                        Check(grid.Parent.ClientRectangle.Contains(grid.Bounds), "Grid overlaps adjacent dialog rows.");
                        Check(dialog.AcceptButton != null && dialog.CancelButton != null, "Dialog actions missing.");
                        foreach (IButtonControl action in new[] { dialog.AcceptButton, dialog.CancelButton })
                        {
                            Control button = (Control)action;
                            Check(dialog.RectangleToScreen(dialog.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)), "Apply/cancel is outside dialog.");
                        }
                        if (language == 0 && (points == 14.25f || points == 18f) && width == 720)
                            Shot(dialog, Path.Combine(qa, "zh-rows" + rowCount + "-font" + points.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                        grid.ColumnHeadersDefaultCellStyle.Font = originalHeaderFont;
                    }
                    // Header layout must not alter independent inclusion/metric values,
                    // nor write caller-owned options before an explicit commit.
                    if (rowCount > 0)
                    {
                        grid.Rows[0].Cells["Maximum"].Value = false;
                        grid.Rows[1].Cells["Include"].Value = false;
                        type.GetMethod("CommitGrid", Hidden).Invoke(dialog, null);
                        IList result = (IList)type.GetProperty("Options", Hidden).GetValue(dialog, null);
                        Check(!(bool)optionType.GetField("Maximum", Hidden).GetValue(result[0]), "Maximum selection did not commit.");
                        Check(!(bool)optionType.GetField("Include", Hidden).GetValue(result[1]), "Include selection did not commit.");
                        Check((bool)optionType.GetField("Average", Hidden).GetValue(result[0]), "Other metric changed.");
                        Check((bool)optionType.GetField("Maximum", Hidden).GetValue(source[0]), "Caller data mutated.");
                        Check((bool)optionType.GetField("Include", Hidden).GetValue(source[1]), "Caller inclusion mutated.");
                    }
                    dialog.Close();
                }
            }
            Console.WriteLine("REPORT_SETTINGS_HEADER_PASS=" + checks + ";LANGUAGES=3;FONT_POINTS=9.5,12,14.25,18,24;CLIENT_WIDTHS=620,720;HARDWARE=False;USER_PROJECT_WRITES=False;QA=" + qa);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
