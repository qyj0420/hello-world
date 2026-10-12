using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class ReportPickerFeedbackSmoke
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Members).GetValue(target); }
    static System.Collections.Generic.IEnumerable<Control> Children(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Children(child)) yield return nested; }
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string release = Path.GetFullPath(args[0]);
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "report-picker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string named = Path.Combine(root, "20260928_063936_964_9140cdd6"), broken = Path.Combine(root, "Old_analysis");
            Directory.CreateDirectory(named); Directory.CreateDirectory(broken);
            string title = "Circuit A — " + new string('X', 130);
            File.WriteAllText(Path.Combine(named, "analysis.json"), "{\"Name\":\"" + title + "\"}");
            File.WriteAllText(Path.Combine(broken, "analysis.json"), "{malformed");
            byte[] before = File.ReadAllBytes(Path.Combine(named, "analysis.json"));
            Assembly module = Assembly.LoadFrom(Path.Combine(release, "modules", "ReportGenerator", "Xiongxin.OWTS.ReportGenerator.dll"));
            Type type = module.GetType("HVX.ReportGenerator.SavedAnalysisPicker", true);
            Check((string)type.GetMethod("DisplayName", Members).Invoke(null, new object[] { named }) == title, "Named analysis metadata is shown instead of folder ID.");
            Check((string)type.GetMethod("DisplayName", Members).Invoke(null, new object[] { broken }) == "Old_analysis", "Malformed metadata falls back without loading a package.");
            using (var font = new Font("Microsoft YaHei UI", 9.5f))
            using (var stream = module.GetManifestResourceStream("XIONGXIN.Brand.ico"))
            using (var icon = new Icon(stream))
            foreach (int language in new[] { 0, 1 })
            foreach (Size size in new[] { new Size(680, 430), new Size(1000, 590) })
            using (var form = (Form)Activator.CreateInstance(type, Members, null,
                new object[] { language, language == 0 ? "加载分析" : "Load analysis", language == 0 ? "请选择要加载的已保存分析。" : "Select a saved analysis to load.", language == 0 ? "跳过" : "Skip", language == 0 ? "加载分析" : "Load analysis", new[] { named, broken }, font, icon }, null))
            {
                form.Opacity = 0; form.ShowInTaskbar = false; form.ClientSize = size; form.Show(); Application.DoEvents();
                var grid = Field<DataGridView>(form, "Grid");
                var detail = Field<Label>(form, "SelectionInfo");
                Check(form.Icon != null, "Picker has the brand icon.");
                Check(!Field<Button>(form, "LoadButton").Enabled, "No row is loaded by opening the picker.");
                grid.Rows[0].Selected = true; Application.DoEvents();
                Check((string)type.GetProperty("SelectedPath", Members).GetValue(form, null) == named, "Selection retains the exact analysis path.");
                Check(detail.Text.Contains(title), "Full selected name is retained in the detail panel.");
                int needed = TextRenderer.MeasureText(detail.Text, font, new Size(detail.ClientSize.Width - detail.Padding.Horizontal, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                Check(needed <= detail.ClientSize.Height - detail.Padding.Vertical, "Long selected name fits the detail panel.");
                Check((GetWindowLong(grid.Handle, -16) & 0x100000) == 0, "No horizontal scrollbar.");
                Check(grid.Columns.Cast<DataGridViewColumn>().Sum(c => c.Width) <= grid.ClientSize.Width, "Both columns fit the available width.");
                foreach (Button button in Children(form).OfType<Button>())
                {
                    Rectangle position = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                    Check(form.ClientRectangle.Contains(position), "Footer button remains in the dialog.");
                    Check(TextRenderer.MeasureText(button.Text, button.Font).Width + 12 <= button.Width, "Footer caption is not truncated: " + button.Text);
                }
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(root, "load-analysis-" + language + "-" + size.Width + ".png"), ImageFormat.Png);
                }
                form.Close();
            }
            Check(File.ReadAllBytes(Path.Combine(named, "analysis.json")).SequenceEqual(before), "Browsing does not modify analysis metadata.");
            Console.WriteLine("REPORT_PICKER_FEEDBACK_PASS=" + checks + " screenshots=" + root);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
