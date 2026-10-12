using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Synthetic plans only; no device discovery, acquisition, or output.
internal static class PressurePlanOperatorFeedbackSmoke
{
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
    static object Get(object c, string n) { return c.GetType().GetProperty(n).GetValue(c, null); }
    static void Set(object c, string n, object v) { c.GetType().GetProperty(n).SetValue(c, v, null); }
    static bool Load(object c, string path) { return (bool)c.GetType().GetMethod("LoadProjectPlan").Invoke(c, new object[] { path }); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]), qa = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(qa);
            string item = Path.Combine(qa, "ITEM.in");
            File.WriteAllText(item, "相电压 = 8.7kV\n加压倍数 = 0,1\n加压次数 = 1,1", new UTF8Encoding(false));
            var asm = Assembly.LoadFrom(Path.Combine(root, "modules", "PressureTest", "Xiongxin.OWTS.PressureTest.dll"));
            using (Control c = (Control)Activator.CreateInstance(asm.GetType("HVX.Controls.PressureTestControl", true)))
            {
                Set(c, "SimulationMode", true);
                foreach (int lang in new[] { 0, 1 })
                {
                    Set(c, "LanguageIndex", lang);
                    string log = null;
                    Set(c, "DiagnosticSink", new Action<string>(s => log = s));
                    Check(Load(c, qa), "Valid synthetic plan loads");
                    Check(!Load(c, Path.Combine(qa, "missing")), "Missing plan is rejected");
                    string message = (string)Get(c, "LastError");
                    Check(message.Contains(lang == 0 ? "请检查项目相电压" : "Check the project phase voltage"), "Operator gets an actionable message");
                    Check(!message.Contains("ITEM.in") && !message.Contains("Exception") && !message.Contains(qa), "Operator message does not expose internal file/exception details");
                    Check(!String.IsNullOrEmpty(log) && log.Contains("ITEM.in") && log.Contains("FileNotFoundException"), "Full cause reaches diagnostics");
                    Check((string)Get(c, "LastPlanDiagnostic") == log, "Diagnostic evidence is retained");
                    Check(String.IsNullOrEmpty((string)Get(c, "ProjectPath")), "Failed load clears prior project");
                    Check(!Load(c, ""), "No-project state is rejected");
                    Check(((string)Get(c, "LastError")).Contains(lang == 0 ? "请先加载项目" : "Load a project first"), "No-project instruction stays specific");
                    Check(Load(c, qa) && String.IsNullOrEmpty((string)Get(c, "LastPlanDiagnostic")), "Successful reload clears stale diagnostics");
                    Set(c, "DiagnosticSink", new Action<string>(s => { throw new IOException("synthetic log failure"); }));
                    Check(!Load(c, Path.Combine(qa, "missing")), "Logging failure does not hide invalid plan");
                    Check((long)Get(c, "PressureRequestSequence") == 0, "Plan feedback never requests output");
                }
            }
            Console.WriteLine("PASS PressurePlanOperatorFeedbackSmoke checks=" + checks + " HARDWARE_ACCESSED=False");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
