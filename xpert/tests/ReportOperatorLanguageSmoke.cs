using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class ReportOperatorLanguageSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Get(object item, string name) { return item.GetType().GetProperty(name, All).GetValue(item, null); }
    static void Set(object item, string name, object value) { item.GetType().GetProperty(name, All).SetValue(item, value, null); }
    static bool Generate(object service, double[,] matrix, double voltage, bool english)
    {
        var method = service.GetType().GetMethods(All).Single(m => m.Name == "TryGenerate" && m.GetParameters().Length == 7);
        object[] values = { matrix, new double[0], new double[0], new double[0], voltage, english, null };
        bool result = (bool)method.Invoke(service, values);
        Check(!result && String.IsNullOrEmpty((string)values[6]), "invalid input returns no fabricated conclusion");
        return result;
    }
    static void CheckUserText(object service, string expected)
    {
        string text = (string)Get(service, "LastError");
        Check(text.Contains(expected), "operator guidance describes the input problem");
        foreach (string forbidden in new[] { "Task-15", "36 columns", "ArgumentException", "System.", "Parameter name:" })
            Check(!text.Contains(forbidden), "developer detail absent from operator error: " + forbidden);
        Check(!String.IsNullOrWhiteSpace((string)Get(service, "LastDiagnostic")), "full diagnostic remains available");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);
            Assembly module = Assembly.LoadFrom(Path.Combine(root, "modules", "ReportGenerator", "Xiongxin.OWTS.ReportGenerator.dll"));
            object service = Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ConclusionService", true));
            int calls = 0; string detail = null;
            Set(service, "DiagnosticSink", new Action<string>(message => { calls++; detail = message; }));
            Generate(service, null, 10, false); CheckUserText(service, "没有可用于");
            Check(calls == 1 && detail.Contains("36 columns"), "one detailed failure reaches the diagnostic sink");
            Generate(service, new double[0, 36], 10, true); CheckUserText(service, "No valid location");
            Generate(service, new double[1, 35], 10, false); CheckUserText(service, "没有可用于");
            foreach (double value in new[] { Double.NaN, Double.PositiveInfinity, -1d })
            {
                Generate(service, new double[1, 36], value, true); CheckUserText(service, "phase voltage is invalid");
                Check(((string)Get(service, "LastDiagnostic")).Contains("operatingPhaseVoltageKv"), "new diagnostic replaces previous matrix error");
            }
            Check(calls == 6, "one diagnostic per failure");
            Set(service, "DiagnosticSink", new Action<string>(message => { throw new IOException("test log unavailable"); }));
            Generate(service, null, 10, true); CheckUserText(service, "No valid location");
            var saved = service.GetType().GetMethod("TryRenderSavedPresentation", All);
            object[] savedArgs = { null, true, 1, null };
            Check(!(bool)saved.Invoke(service, savedArgs), "missing saved evidence does not produce text");
            Check(((string)Get(service, "LastError")).Contains("original text is retained"), "saved evidence retains existing presentation on failure");
            Check(!((string)Get(service, "LastError")).Contains("Value cannot") && ((string)Get(service, "LastDiagnostic")).Contains("ArgumentNullException"), "saved error is neutral while diagnostic remains complete");

            Assembly host = Assembly.LoadFrom(Path.Combine(root, "Xiongxin.OWTS.Desktop.exe"));
            Type settingsType = host.GetType("Xiongxin.OWTS.Desktop.AppSettings", true);
            Type helpType = host.GetType("Xiongxin.OWTS.Desktop.HelpForm", true);
            foreach (string language in new[] { "zh-CN", "en" })
            {
                object settings = Activator.CreateInstance(settingsType, true);
                Set(settings, "LanguageCode", language);
                using (var help = (Form)Activator.CreateInstance(helpType, All, null, new[] { settings }, null))
                {
                    string text = ((TextBox)helpType.GetField("information", All).GetValue(help)).Text;
                    Check(!text.Contains("LabVIEW") && !text.Contains("非零高压暂未") && !text.Contains("Integration"), "help excludes obsolete implementation language");
                    Check(text.Contains(language == "zh-CN" ? "确认本轮并开始" : "Confirm this round and start"), "help explains each-round inline confirmation");
                    Check(text.Contains(language == "zh-CN" ? "无残压" : "residual voltage"), "help does not equate completion with residual voltage safety");
                }
            }
            Console.WriteLine("REPORT_OPERATOR_LANGUAGE_PASS=" + checks + ";hardware=false;user_projects_written=false");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error);
            return 1;
        }
    }
}
