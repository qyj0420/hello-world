using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ProjectActivationSmoke
{
    private static int checks;
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException(message);
    }
    private static object Field(object target, string name) { return target.GetType().GetField(name, Hidden).GetValue(target); }
    private static object Call(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, args);
    }
    private static void Pump(int milliseconds)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { Application.DoEvents(); Thread.Sleep(5); } while (DateTime.UtcNow < until);
    }
    private static Control Open(MainForm form)
    {
        Call(form, "OpenModule", "ProjectManagement");
        Control manager = (Control)Field(form, "activeModule");
        Check(manager != null, "manager opens");
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while ((bool)manager.GetType().GetProperty("IsBusy").GetValue(manager, null) && DateTime.UtcNow < until) Pump(20);
        Check(!(bool)manager.GetType().GetProperty("IsBusy").GetValue(manager, null), "scan finished");
        foreach (string labelName in new[] { "productTitle", "productSubtitle" })
        {
            Label label = (Label)Field(form, labelName);
            if (!label.Visible) continue;
            int requiredHeight = TextRenderer.MeasureText("Ag中", label.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
            Check(label.Height >= requiredHeight, labelName + " needs " + requiredHeight + " px but has " + label.Height + " px");
            Check(label.Parent.ClientRectangle.Contains(label.Bounds), labelName + " stays inside the brand panel: " + label.Bounds + "; parent=" + label.Parent.ClientRectangle);
        }
        return manager;
    }
    private static string CurrentPath(DataGridView grid)
    {
        object record = grid.CurrentRow.Tag;
        return (string)record.GetType().GetProperty("ProjectPath").GetValue(record, null);
    }
    private static void DoubleClick(DataGridView grid, int column, int row)
    {
        Call(grid, "OnCellDoubleClick", new DataGridViewCellEventArgs(column, row));
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string releaseRoot = Path.GetFullPath(args[0]);
            string outputRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ProjectActivationSmoke");
            string fixtureRoot = Path.Combine(outputRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            foreach (string name in new[] { "One", "Two" })
            {
                string project = Path.Combine(fixtureRoot, name);
                Directory.CreateDirectory(project);
                File.WriteAllText(Path.Combine(project, "ITEM.in"), "始端地点\t" + name + "\r\n终端地点\tTest\r\n电缆长度\t400\r\n", Encoding.UTF8);
                File.WriteAllText(Path.Combine(project, "CABLE.in"), "0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t400\r\n", Encoding.UTF8);
            }
            var settings = new AppSettings { DataRoot = fixtureRoot, LanguageCode = "en" };
            settings.Hardware.Enabled = false;
            var state = new ApplicationState();
            using (var form = new MainForm(settings, state, true))
            {
                ((ModuleHost)Field(form, "moduleHost")).Dispose();
                var host = new ModuleHost(settings, state, null, Path.Combine(releaseRoot, "modules"));
                host.ProjectLoadRequested += (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), form,
                    typeof(MainForm).GetMethod("ProjectLoadRequested", Hidden));
                typeof(MainForm).GetField("moduleHost", Hidden).SetValue(form, host);
                form.ShowInTaskbar = false;
                form.Show();
                Control manager = Open(form);
                DataGridView grid = (DataGridView)Field(manager, "grid");
                Check(grid.Rows.Count == 2, "two fixtures listed");
                grid.CurrentCell = grid.Rows[1].Cells[2];
                Pump(300);
                Check(!state.ProjectLoaded, "single selection does not load");
                DoubleClick(grid, 2, -1);
                DoubleClick(grid, 0, 1);
                Pump(300);
                Check(!state.ProjectLoaded, "header and checkbox double click do not activate");
                string path = CurrentPath(grid);
                string cable = Path.Combine(path, "CABLE.in");
                File.Move(cable, cable + ".held");
                DoubleClick(grid, 2, 1);
                Pump(300);
                Check(Field(form, "activeModule") == manager && !state.ProjectLoaded, "invalid project keeps page and current project");
                Check(!String.IsNullOrEmpty((string)manager.GetType().GetProperty("LastError").GetValue(manager, null)), "failed activation shows error");
                File.Move(cable + ".held", cable);
                // Deliberately activate a different row from the old selection.
                string clickedPath = (string)grid.Rows[0].Tag.GetType().GetProperty("ProjectPath").GetValue(grid.Rows[0].Tag, null);
                DoubleClick(grid, 2, 0);
                Pump(450);
                Check(String.Equals(state.ProjectPath, clickedPath, StringComparison.OrdinalIgnoreCase), "double click activates the clicked row");
                Check(Field(form, "activeModule") == null, "successful double click returns home");
                Check(((Panel)Field(form, "homePanel")).Visible, "home is visible");
                manager = Open(form);
                grid = (DataGridView)Field(manager, "grid");
                grid.CurrentCell = grid.Rows[1].Cells[2];
                path = CurrentPath(grid);
                ((Button)Field(manager, "loadButton")).PerformClick();
                Pump(450);
                Check(String.Equals(state.ProjectPath, path, StringComparison.OrdinalIgnoreCase), "button uses same current-project flow");
                Check(Field(form, "activeModule") == null, "successful load button returns home");
                manager = Open(form);
                grid = (DataGridView)Field(manager, "grid");
                grid.CurrentCell = grid.Rows[0].Cells[2];
                path = CurrentPath(grid);
                Check((bool)Call(manager, "RequestLoadSelectedProject"), "valid request accepted");
                cable = Path.Combine(path, "CABLE.in");
                File.Move(cable, cable + ".held");
                Call(Field(form, "moduleHost"), "PollProjectModules", null, EventArgs.Empty);
                Check(Field(form, "activeModule") == manager, "host revalidation failure remains on page");
                Check(!String.IsNullOrEmpty((string)manager.GetType().GetProperty("LastError").GetValue(manager, null)), "host failure is visible in manager");
                File.Move(cable + ".held", cable);
                Call(form, "ShowHome");
                Check(Field(form, "activeModule") == null, "final pending request can be safely completed");
                state.ClearProject();
                Call(form, "OpenModule", "ProjectCreation");
                Control editor = (Control)Field(form, "activeModule");
                Check(editor != null, "creation page opens");
                var editorType = editor.GetType();
                editorType.GetProperty("ShowDialogMessages").SetValue(editor, false, null);
                editorType.GetProperty("SettingsPath").SetValue(editor, Path.Combine(fixtureRoot, "creation-settings.ini"), null);
                editorType.GetProperty("StartName").SetValue(editor, "", null);
                Check(!(bool)Call(editor, "SaveProject"), "invalid save fails");
                Pump(300);
                Check(Field(form, "activeModule") == editor && !state.ProjectLoaded, "failed save stays editor");
                editorType.GetProperty("StartName").SetValue(editor, "QA new start", null);
                editorType.GetProperty("EndName").SetValue(editor, "QA new end", null);
                editorType.GetProperty("CableLengthA").SetValue(editor, 180d, null);
                editorType.GetProperty("CableLengthB").SetValue(editor, 180d, null);
                editorType.GetProperty("CableLengthC").SetValue(editor, 180d, null);
                Check((bool)Call(editor, "SaveProject"), "valid creation saves");
                Pump(500);
                Check(Field(form, "activeModule") == null && state.ProjectLoaded, "successful creation save returns home");
                Check(state.ProjectPath.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase), "saved project is QA fixture, not user data");
                form.Close();
            }
            Console.WriteLine("PROJECT_ACTIVATION_PASS=" + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
