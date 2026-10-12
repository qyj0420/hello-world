using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Loads the actual built Help control without starting product, browser or device.
static class HelpPackagedManualSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static string Resolve(object owner, string name) { return (string)owner.GetType().GetMethod(name, All).Invoke(owner, null); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var host = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "Xiongxin.OWTS.Desktop.exe"));
            var settingsType = host.GetType("Xiongxin.OWTS.Desktop.AppSettings", true);
            var helpType = host.GetType("Xiongxin.OWTS.Desktop.HelpForm", true);
            string packaged = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "manual", "index.html");
            string glossary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "international", "electrical-glossary.html");
            foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
            foreach (string configured in new[] { "", "   ", "  C:\\Customer Manual\\manual.pdf  ", "https://example.invalid/manual" })
            {
                var settings = Activator.CreateInstance(settingsType, true);
                settingsType.GetProperty("LanguageCode", All).SetValue(settings, language, null);
                settingsType.GetProperty("ManualPath", All).SetValue(settings, configured, null);
                using (var help = (Form)Activator.CreateInstance(helpType, All, null, new[] { settings }, null))
                {
                    string expected = String.IsNullOrWhiteSpace(configured) ? packaged : configured.Trim();
                    Check(Resolve(help, "ResolveManualPath") == expected, "Manual override or packaged fallback resolves exactly");
                    Check(((TextBox)Field(help, "manualPath")).Text == expected, "Help displays the same path used by its open action");
                    Check(Resolve(help, "ResolveGlossaryPath") == glossary, "Glossary resolves to actual deployed docs/international path");
                    Check(((Button)Field(help, "openManualButton")).Enabled, "Manual entry is available without hardware or maintenance authorization");
                    Check((string)settingsType.GetProperty("ManualPath", All).GetValue(settings, null) == configured, "Opening Help never rewrites user manual configuration");
                    Check(help.DialogResult == DialogResult.None, "Constructing Help performs no confirmation or action");
                }
            }
            Console.WriteLine("HELP_PACKAGED_MANUAL_PASS=" + checks + "; ProductExecuted=False; BrowserOpened=False; HardwareAccessed=False; UserSettingsWritten=False");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
