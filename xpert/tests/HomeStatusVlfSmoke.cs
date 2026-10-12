using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class HomeStatusVlfSmoke
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            string project = Path.Combine(output, "fixture-project"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "ITEM.in"), "[ITEM]\r\n");
            File.WriteAllText(Path.Combine(project, "CABLE.in"), "[CABLE]\r\n");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (string language in new[] { "zh-CN", "en" })
            foreach (int flags in new[] { 0, 1, 2, 3 })
            {
                var settings = new AppSettings { LanguageCode = language, SupportsCosineVlf = (flags & 1) != 0, SupportsCombinedVlf = (flags & 2) != 0 };
                string settingsPath = Path.Combine(output, "settings-" + language + flags + ".xml");
                settings.Save(settingsPath); var loaded = AppSettings.Load(settingsPath);
                Check(loaded.SupportsCosineVlf == settings.SupportsCosineVlf && loaded.SupportsCombinedVlf == settings.SupportsCombinedVlf, "VLF options persist");
                var state = new ApplicationState();
                using (var form = new MainForm(settings, state, true))
                {
                    var tiles = Field<Dictionary<string, ModuleTile>>(form, "tiles");
                    Check(tiles.Count == 9, "Eight standard home cards plus the optional competition card are registered");
                    Check(form.StateSnapshot().Contains("CompetitionDefect:Visible=False;Enabled=False"), "Optional competition card stays hidden when not enabled");
                    Check(tiles["CalibrationTest"].ModeBadge == (language == "zh-CN" ? "仿真" : "SIM"), "Disconnected calibration card explicitly identifies simulation");
                    Check(!tiles["WithstandTest"].Enabled, "No project does not enable test");
                    string message; Check(state.TryLoadProject(project, out message), "Load fixture project");
                    Check(tiles["WithstandTest"].Enabled == (flags != 0), "Capabilities gate test entry");
                    Check(Field<Label>(form, "projectStatus").Text == (language == "zh-CN" ? "当前项目" : "Current project"), "Stable project title");
                    Check(Field<Label>(form, "deviceStatus").Text == (language == "zh-CN" ? "设备连接状态" : "Device connection status"), "Stable connection title");
                    state.SetHardware(new HardwareStatusSnapshot { HardwareEnabled = true, DriverInstalled = true, Connected = true, IdentityValid = true, StatusCode = "Ready" });
                    Check(Field<Label>(form, "deviceStatus").Text == (language == "zh-CN" ? "设备连接状态" : "Device connection status"), "Title does not become a state value");
                    Check(tiles["WithstandTest"].ModeBadge == (language == "zh-CN" ? "仿真" : "SIM"), "Unavailable live bridge never claims live withstand");
                    if (flags == 0 || flags == 3)
                    {
                        form.ClientSize = new Size(1600, 960);
                        form.RenderToPng(Path.Combine(output, "home-" + language + "-" + flags + ".png"));
                        foreach (string field in new[] { "projectStatus", "deviceStatus" })
                        {
                            var label = Field<Label>(form, field);
                            Check(TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.NoPadding).Width <= label.ClientSize.Width, "Status title fits: " + field);
                        }
                    }
                    state.ClearProject(); Check(!tiles["WithstandTest"].Enabled, "Clearing project disables test");
                }
            }
            Console.WriteLine("HOME_STATUS_VLF_PASS=" + checks); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
