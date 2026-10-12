using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HVX.Controls;

internal static class ProjectCurrentPinSmoke
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, Hidden).GetValue(value); }
    static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }
    static void Wait(ProjectManagementControl control)
    {
        DateTime timeout = DateTime.UtcNow.AddSeconds(15);
        while (control.IsBusy && DateTime.UtcNow < timeout) { Application.DoEvents(); System.Threading.Thread.Sleep(1); }
        Check(!control.IsBusy, "isolated scan completes"); Pump();
    }
    static string Project(string parent, string name, string terminal, int day, double length)
    {
        string path = Path.Combine(parent, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "始端地点 = " + terminal + "\r\n终端地点 = 终端\r\n日期 = 2026-09-" + day.ToString("00") + "\r\n时间 = 12:31:17\r\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(path, "CABLE.in"), "0\tALL\r\n1\t始端\t冷缩\t0\r\n1\t终端\t冷缩\t" + length + "\r\n", Encoding.UTF8);
        return path;
    }
    static string[] Rows(ProjectManagementControl control)
    { return Field<DataGridView>(control, "grid").Rows.Cast<DataGridViewRow>().Select(r => ((ProjectRecord)r.Tag).ProjectPath).ToArray(); }
    static void CheckOrder(ProjectManagementControl control, string pinned)
    {
        ProjectRecord[] sorted = ProjectQuery.FilterAndSort(control.Projects, control.SearchKeyword, control.SortField, control.SortDirection);
        var expected = sorted.Select(p => p.ProjectPath).ToList();
        int index = expected.FindIndex(p => string.Equals(p, pinned, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) { string path = expected[index]; expected.RemoveAt(index); expected.Insert(0, path); }
        Check(Rows(control).SequenceEqual(expected, StringComparer.OrdinalIgnoreCase), "current row pinned, all remaining rows keep chosen sort and filter");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[0]), "project-pin-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(qa);
            string root = Path.Combine(qa, "scanned"); Directory.CreateDirectory(root);
            string current = Project(root, "older-project", "同名起点", 1, 240);
            string newest = Project(root, "newest-project", "同名起点", 29, 140);
            string alpha = Project(root, "alpha", "Alpha", 12, 740);
            string beta = Project(root, "beta", "Beta", 18, 440);
            string outside = Project(qa, "outside-current", "外部当前项目", 9, 540);
            string unrelated = Project(qa, "outside-current-suffix", "不应被枚举", 10, 640);
            Dictionary<string, byte[]> originals = Directory.GetFiles(qa, "*.in", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            using (var host = new Form { ClientSize = new Size(1280, 720), StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000), ShowInTaskbar = false })
            using (var control = new ProjectManagementControl { EmbeddedMode = true, Dock = DockStyle.Fill, ExitViOnProjectLoad = false })
            {
                host.Controls.Add(control); host.Show(); Pump();
                control.CurrentProjectPath = current.ToUpperInvariant() + Path.DirectorySeparatorChar;
                Check(control.LoadRoot(root), "root scan succeeds"); Pump();
                Check(control.ProjectCount == 4, "setting current before scan does not duplicate matching root project");
                CheckOrder(control, current);
                Check(Rows(control)[0].Equals(current, StringComparison.OrdinalIgnoreCase), "older current project outranks newest same-name project by exact path identity");
                foreach (ProjectSortField field in Enum.GetValues(typeof(ProjectSortField)))
                foreach (ProjectSortDirection direction in Enum.GetValues(typeof(ProjectSortDirection)))
                {
                    control.SetSort(field, direction); CheckOrder(control, current);
                    Check(control.RefreshProjects(), "refresh begins"); Wait(control);
                    Check(control.SortField == field && control.SortDirection == direction, "refresh preserves selected sorting"); CheckOrder(control, current);
                    Check(Field<DataGridView>(control, "grid").Rows.Cast<DataGridViewRow>().All(r => !(bool)r.Cells["Selected"].Value), "pin does not silently check a row for delete/export");
                }
                control.SearchKeyword = "Beta"; Check(control.VisibleProjectCount == 1 && Rows(control)[0] == beta, "search still filters out loaded project");
                foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
                {
                    control.SetLanguageCode(language); Pump();
                    string hint = Field<Label>(control, "status").Text;
                    Check(hint.Contains(language == "en" ? "hidden by the filter" : language == "zh-TW" ? "篩選" : "筛选"), "filter conflict visibly explained in " + language);
                    Check(control.SearchKeyword == "Beta" && Rows(control).SequenceEqual(new[] { beta }), "language change does not silently clear search or add a nonmatching row");
                }
                Check(control.RefreshProjects(), "filtered refresh starts"); Wait(control);
                Check(control.SearchKeyword == "Beta" && control.VisibleProjectCount == 1, "refresh preserves search");
                Check(Field<Label>(control, "status").Text.Contains("篩選"), "scan completion retains filter conflict explanation");
                control.SearchKeyword = ""; CheckOrder(control, current);
                Check(!Field<Label>(control, "status").Text.Contains("篩選隱藏"), "clearing filter removes stale conflict hint");
                Check(Field<DataGridView>(control, "grid").Rows[0].Cells["ProjectName"].ToolTipText.Contains(current), "pinned row tooltip contains full real project path");

                control.CurrentProjectPath = beta; CheckOrder(control, beta);
                Check(Rows(control)[0] == beta, "changing loaded project immediately changes first row");
                Check(control.BeginLoadRoot(root), "async rescan starts"); control.CurrentProjectPath = alpha; Wait(control);
                Check(Rows(control)[0] == alpha, "late scan respects latest host context rather than previous current project");
                var grid = Field<DataGridView>(control, "grid");
                DataGridViewRow pending = grid.Rows.Cast<DataGridViewRow>().Single(r => ((ProjectRecord)r.Tag).ProjectPath == newest);
                grid.CurrentCell = pending.Cells["ProjectName"]; pending.Selected = true;
                int requests = 0; control.ProjectLoadRequested += (sender, e) => requests++;
                Check(control.RequestLoadSelectedProject() && requests == 1, "pending load request emitted without pretending host accepted it");
                control.ReportProjectLoadFailure("isolated rejection");
                Check(control.CurrentProjectPath == alpha && Rows(control)[0] == alpha, "rejected load leaves actual loaded project pinned");

                control.CurrentProjectPath = outside; CheckOrder(control, outside);
                Check(control.ProjectCount == 5 && Rows(control)[0] == outside, "external current project is included once and pinned");
                Check(!Rows(control).Contains(unrelated), "external current does not scan sibling projects or use prefix identity");
                Check(control.RefreshProjects(), "external-context refresh starts"); Wait(control);
                Check(control.ProjectCount == 5 && Rows(control)[0] == outside, "refresh retains external current only once");
                Check(!control.LoadRoot(Path.Combine(qa, "missing-root")), "unavailable scan root still reports failure");
                Check(control.ProjectCount == 1 && Rows(control)[0] == outside, "valid current project remains visible even if configured root is unavailable");
                Check(!string.IsNullOrEmpty(control.LastError), "keeping current project does not hide the root scan error");
                Check(control.LoadRoot(root), "normal root can be scanned again after failure");
                Check(control.ProjectCount == 5 && Rows(control)[0] == outside, "recovery returns remaining projects without duplicating pinned current");
                control.SearchKeyword = "Alpha"; Check(control.VisibleProjectCount == 1 && Rows(control)[0] == alpha, "external current respects active search");
                control.SearchKeyword = "";
                control.CurrentProjectPath = string.Empty;
                Check(control.ProjectCount == 4 && !Rows(control).Contains(outside), "release removes external-only context row"); CheckOrder(control, "");
                Check(Field<Label>(control, "status").Text.Contains("4"), "release of external context also updates the displayed project count");
                Check(!Field<Label>(control, "status").Text.Contains("篩選隱藏"), "release clears hidden-current explanation");
                control.CurrentProjectPath = Path.Combine(qa, "missing-project");
                Check(control.ProjectCount == 4 && Field<Label>(control, "status").Text.Contains("不可讀取"), "missing current path is explicit and does not manufacture a project");
                control.CurrentProjectPath = current; control.SetLanguageCode("zh-CN"); control.SetSort(ProjectSortField.TestTime, ProjectSortDirection.Descending); CheckOrder(control, current);
                using (var image = new Bitmap(host.Width, host.Height))
                { host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size)); image.Save(Path.Combine(qa, "current-project-pinned.png")); }
                host.Controls.Remove(control); host.Close();
            }
            using (var reopened = new ProjectManagementControl { CurrentProjectPath = current })
            {
                Check(reopened.LoadRoot(root), "reopened manager scans"); CheckOrder(reopened, current);
                Check(Rows(reopened)[0] == current, "reopening with host current context pins same project");
                reopened.CurrentProjectPath = ""; CheckOrder(reopened, "");
            }
            foreach (var file in originals) Check(file.Value.SequenceEqual(File.ReadAllBytes(file.Key)), "project files unchanged: " + file.Key);
            Console.WriteLine("PROJECT_CURRENT_PIN_PASS=" + checks + "; HARDWARE=False; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error); return 1; }
    }
}
