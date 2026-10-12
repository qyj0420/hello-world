using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Uses isolated XML fixtures only; never saves the user's live profile or
// opens a report engine, project, driver, or hardware connection.
internal static class StateGridReportSettingsSmoke
{
    static int checks;
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static object Field(object value, string name) { return value.GetType().GetField(name, Hidden).GetValue(value); }
    static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, Hidden).Invoke(value, args); }

    sealed class ReportProbe : UserControl
    {
        bool enabled;
        public int Writes;
        public bool GenerateStateGridReport { get { return enabled; } set { enabled = value; Writes++; } }
        public string OutputDirectory { get; set; }
        public string OutputFormat { get; set; }
    }

    [STAThread] static int Main(string[] args)
    {
        try
        {
            string qa = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            Application.EnableVisualStyles();
            Check(!new AppSettings().GenerateStateGridReport, "New installations leave the companion report off");
            string legacy = Path.Combine(qa, "legacy.xml");
            File.WriteAllText(legacy, "<HVXUnifiedDesktopSettings></HVXUnifiedDesktopSettings>");
            AppSettings loaded; string message;
            Check(AppSettings.TryLoad(legacy, out loaded, out message) && !loaded.GenerateStateGridReport, "Legacy profile without field remains off");
            Check(!AppSettings.Load(Path.Combine(qa, "missing.xml")).GenerateStateGridReport, "Missing profile remains off");
            foreach (bool enabled in new[] { false, true })
            {
                var settings = new AppSettings { GenerateStateGridReport = enabled, NoiseSimilarityTolerancePercent = 2.25, ReportOutputPath = qa };
                Check(settings.Clone().GenerateStateGridReport == enabled, "Clone preserves optional report flag");
                string path = Path.Combine(qa, "settings-" + enabled + ".xml");
                settings.Save(path);
                Check(File.ReadAllText(path).Contains("<GenerateStateGridReport>" + enabled.ToString().ToLowerInvariant() + "</GenerateStateGridReport>"), "Flag explicitly serialized as a boolean");
                Check(AppSettings.TryLoad(path, out loaded, out message) && loaded.GenerateStateGridReport == enabled, "Flag persists on load");
                using (var form = new SettingsForm(loaded))
                {
                    var checkbox = (CheckBox)Field(form, "generateStateGridReport");
                    Check(checkbox.Checked == enabled && checkbox.Name == "GenerateStateGridReport", "Report checkbox loads flag");
                    Check(checkbox.Text == "同步生成国家电网标准格式报告", "Requested report label preserved");
                    Check(((TabControl)Field(form, "tabs")).TabPages[1].Contains(checkbox), "Optional setting is in ordinary report tab");
                    Check(Field(form, "noiseSimilarityTolerance") == null && !(bool)Field(form, "expertUnlocked") && !(bool)Field(form, "deviceUnlocked") && !(bool)Field(form, "systemAdminUnlocked"), "No maintenance or device authorization is opened");
                    checkbox.Checked = !enabled;
                    var captured = (AppSettings)Call(form, "CaptureValues");
                    Check(captured.GenerateStateGridReport == !enabled, "Ordinary report edit is captured");
                    Check(loaded.GenerateStateGridReport == enabled, "Settings editing does not mutate source before save");
                    Check(captured.NoiseSimilarityTolerancePercent == 2.25 && !captured.Hardware.Enabled && captured.MaximumVoltageKv == loaded.MaximumVoltageKv, "Report edit preserves manufacturer tolerance and hardware safety settings");
                    captured.Save(path);
                    Check(AppSettings.Load(path).GenerateStateGridReport == !enabled, "Both checking and unchecking survive save/reload");
                }
            }
            VerifyMapping(qa);
            foreach (float fontSize in new[] { 9.5f, 15f, 20f })
                foreach (int width in new[] { 900, 1200 }) VerifyLayout(qa, fontSize, width);
            Console.WriteLine("STATE_GRID_REPORT_SETTINGS_PASS=" + checks);
            Console.WriteLine("HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False");
            Console.WriteLine("EVIDENCE=" + qa);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void VerifyMapping(string qa)
    {
        var settings = new AppSettings { ReportOutputPath = qa };
        var descriptor = ModuleHost.Descriptors.Single(d => d.Id == ModuleHost.ReportGenerator);
        using (var probe = new ReportProbe())
        using (var host = new ModuleHost(settings, new ApplicationState(), new Diagnostics(qa), Path.Combine(qa, "modules")))
        {
            Call(host, "ApplyContext", descriptor, probe, false);
            Check(!probe.GenerateStateGridReport && probe.Writes == 1, "Host applies explicit false on first page activation");
            int writes = probe.Writes;
            Call(host, "ApplyContext", descriptor, probe, false);
            Check(probe.Writes == writes, "Unchanged context does not redundantly apply settings");
            settings.GenerateStateGridReport = true;
            Call(host, "ApplyContext", descriptor, probe, false);
            Check(probe.GenerateStateGridReport && probe.Writes == writes + 1, "Flag alone invalidates context and enables existing report page");
            settings.GenerateStateGridReport = false;
            Call(host, "ApplyContext", descriptor, probe, false);
            Check(!probe.GenerateStateGridReport && probe.Writes == writes + 2, "Turning flag off reaches existing report page");
            Check(probe.OutputDirectory == qa && probe.OutputFormat == "PdfAndWord", "Existing output configuration mapping remains intact");
        }
        using (var probe = new ReportProbe { GenerateStateGridReport = true })
        using (var host = new ModuleHost(new object(), new ApplicationState(), new Diagnostics(qa), Path.Combine(qa, "modules")))
        {
            Call(host, "ApplyReportSettings", probe);
            Check(!probe.GenerateStateGridReport, "Older settings contracts without property safely map to false");
        }
    }

    static void SetFont(Control control, Font font)
    {
        control.Font = font;
        foreach (Control child in control.Controls) SetFont(child, font);
    }

    static void VerifyLayout(string qa, float fontSize, int width)
    {
        using (var font = new Font("Microsoft YaHei UI", fontSize))
        using (var form = new SettingsForm(new AppSettings()))
        {
            SetFont(form, font);
            form.Size = new Size(width, 760);
            form.ShowInTaskbar = false;
            var tabs = (TabControl)Field(form, "tabs");
            tabs.SelectedIndex = 1;
            form.Show();
            Application.DoEvents(); form.PerformLayout(); Application.DoEvents();
            var checkbox = (CheckBox)Field(form, "generateStateGridReport");
            var note = form.Controls.Find("StateGridReportNote", true).Single();
            var grid = (TableLayoutPanel)checkbox.Parent;
            string scenario = fontSize + "pt/" + width;
            Check(checkbox.Visible && note.Visible && checkbox.Enabled, "Report setting remains accessible: " + scenario);
            Check(note.Text.Contains("Q/GDW 11262—2024 附录 M") && note.Text.Contains("不替换现有报告"), "Scope hint displayed: " + scenario);
            Check(!checkbox.Bounds.IntersectsWith(note.Bounds) && checkbox.Bottom < note.Top, "Auto-size rows do not overlap: " + scenario);
            Check(checkbox.Right <= grid.ClientSize.Width - grid.Padding.Right && note.Right <= grid.ClientSize.Width - grid.Padding.Right, "Wide report controls stay inside tab layout: " + scenario);
            Check(checkbox.Height >= checkbox.GetPreferredSize(new Size(checkbox.Width, 0)).Height, "Full checkbox text has room: " + scenario);
            Check(note.Height >= note.GetPreferredSize(new Size(note.Width, 0)).Height, "Wrapped note has room: " + scenario);
            var pathEditor = (Control)Field(form, "reportOutputPath");
            var browse = pathEditor.Parent.Controls.OfType<Button>().Single();
            foreach (var button in new[] { browse, (Button)form.AcceptButton, (Button)form.CancelButton })
            {
                Size preferred = button.GetPreferredSize(Size.Empty);
                Check(button.Width >= preferred.Width && button.Height >= preferred.Height, "Settings action text is not clipped: " + button.Text + "/" + scenario + "; actual=" + button.Size + "; preferred=" + preferred);
            }
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(qa, "report-settings-" + fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "pt-" + width + ".png"));
            }
            form.Close();
        }
    }
}
