using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class SettingsIntegritySmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string description) { checks++; if (!value) throw new Exception(description); }
    static object Field(object obj, string name) { return obj.GetType().GetField(name, Hidden).GetValue(obj); }
    static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, Hidden).Invoke(obj, args); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[0]), "integrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            foreach (string invalid in new[] { "NaN;1.5;OR;OR", "2;Infinity;AND;OR", "0.9;2;OR;OR",
                "2;101;AND;AND", "2;2;XOR;OR", "2;2;OR", "", null })
            {
                var settings = new AppSettings(); settings.TaskMinus3.EndpointPolicy = invalid;
                string diagnostic;
                Check(!settings.TryValidate(out diagnostic) && diagnostic.Length > 0, "invalid policy rejected without substitution");
                string path = Path.Combine(qa, "rejected.xml"); bool rejected = false;
                try { settings.Save(path); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && !File.Exists(path), "invalid policy cannot be persisted");
                using (var form = new SettingsForm(settings))
                {
                    int previousControls = ((TabPage)Field(form, "plusDataTab")).Controls.Count;
                    bool failed = false;
                    try { Call(form, "OpenSection", SettingsSection.PlusData); }
                    catch (TargetInvocationException ex) { failed = ex.InnerException is InvalidOperationException; }
                    Check(failed && !(bool)Field(form, "plusDataUnlocked"), "invalid policy cannot initialize editor");
                    Check(((TabPage)Field(form, "plusDataTab")).Controls.Count == previousControls, "invalid policy never creates default-value editor");
                    var captured = (AppSettings)Call(form, "CaptureValues");
                    Check(captured.TaskMinus3.EndpointPolicy == invalid, "capture cannot replace corrupt policy with editor minimums");
                }
            }
            foreach (string policy in new[] { "1;100;AND;OR", "100;1;OR;AND", "2;1.5;OR;OR" })
            {
                var settings = new AppSettings(); settings.TaskMinus3.EndpointPolicy = policy;
                string diagnostic; Check(settings.TryValidate(out diagnostic), "valid boundary and legacy policies retained");
                string path = Path.Combine(qa, "valid.xml"); settings.Save(path);
                AppSettings loaded; Check(AppSettings.TryLoad(path, out loaded, out diagnostic), "valid configuration reloads");
                Check(loaded.TaskMinus3.EndpointPolicy == policy, "policy round trip unchanged");
            }
            string broken = Path.Combine(qa, "broken.xml"); File.WriteAllText(broken, "<HVXUnifiedDesktopSettings><bad>");
            byte[] before = File.ReadAllBytes(broken); AppSettings fallback; string message;
            Check(!AppSettings.TryLoad(broken, out fallback, out message), "startup loader reports corrupt configuration");
            Check(!fallback.Hardware.Enabled && !String.IsNullOrWhiteSpace(message), "failure returns offline state and visible diagnostic");
            Check(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(broken)), "damaged original is preserved");
            Console.WriteLine("SETTINGS_INTEGRITY_PASS=" + checks + ";HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False;EVIDENCE=" + qa);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
