using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class CableJointCommitWarningSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Get(object owner, string name) { var type = owner.GetType(); var p = type.GetProperty(name, All); return p != null ? p.GetValue(owner, null) : type.GetField(name, All).GetValue(owner); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, All).SetValue(owner, value, null); }
    static object Call(object owner, string name, params object[] args)
    { var type = owner as Type ?? owner.GetType(); return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner is Type ? null : owner, args); }
    static void Pump(Func<bool> done)
    { DateTime end = DateTime.UtcNow.AddSeconds(20); while (!done() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); } Application.DoEvents(); Check(done(), "async UI completed"); }
    static void ThrowRestore(string path, FileAttributes attributes) { throw new IOException("QA injected post-commit attribute restore failure"); }
    static void Inject(Assembly module, bool enabled)
    { module.GetType("HVX.ProjectCables.CableJointStore", true).GetField("RestoreAttributesForTests", All).SetValue(null, enabled ? new Action<string, FileAttributes>(ThrowRestore) : null); }
    static string Fixture(string root, string name)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "[ITEM]\r\n始端地点 = QA start\r\n终端地点 = QA end\r\n电缆芯数 = 单相\r\n电缆长度 = 400\r\nA相电缆长度 = 400\r\n相电压 = 8.7kV\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path, "CABLE.in"), "0\tA\t\t\r\n1\t人工始端\t冷缩\t0m\r\n2\t原本体\tXLPE\t\r\n1\t人工终端\t冷缩\t400m\r\n", new UTF8Encoding(true));
        File.WriteAllBytes(Path.Combine(path, "raw.dem"), new byte[] { 7, 2, 9 });
        File.SetAttributes(Path.Combine(path, "CABLE.in"), FileAttributes.ReadOnly | FileAttributes.Archive); return path;
    }
    static object Plan(Assembly module, string root, double position)
    { return Call(Call(module.GetType("HVX.ProjectCables.CableJointStore", true), "Read", root), "Prepare", new[] { "A" }, position, "接头 1", "冷缩", "", ""); }
    static void DiskSaved(string root, byte[] before, byte[] item)
    {
        string[] backups = Directory.GetFiles(root, "CABLE.before-joint-*.bak");
        Check(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(before), "one exact backup after warning, no retry");
        Check(File.ReadAllText(Path.Combine(root, "CABLE.in")).Contains("80m"), "actual joint already committed");
        Check(File.ReadAllBytes(Path.Combine(root, "ITEM.in")).SequenceEqual(item), "ITEM untouched");
        Check(File.ReadAllBytes(Path.Combine(root, "raw.dem")).SequenceEqual(new byte[] { 7, 2, 9 }), "raw measurements untouched");
        Check(Directory.GetFiles(root, ".cj-*.tmp").Length == 0, "staging cleaned");
    }
    static Form Host(Control control)
    {
        var form = new Form { ClientSize = new Size(1150, 720), StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
        form.Controls.Add(control); control.Dock = DockStyle.Fill; form.Show(); Application.DoEvents(); return form;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string build = Path.GetFullPath(args[0]), qa = Path.Combine(Path.GetFullPath(args[1]), "cw-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            string[] names = { "ProjectCreation", "DataBrowser", "DataAnalysis", "CalibrationTest" };
            string[] dlls = { "Xiongxin.OWTS.ProjectCreation.dll", "Xiongxin.OWTS.DataBrowser.dll", "HVX.PdReview.V1.0.dll", "Xiongxin.OWTS.CalibrationTest.dll" };
            string[] types = { "HVX.Controls.ProjectCreationControl", "HVX.Controls.ProjectDataBrowserControl", "HVX.PdReview.PdReviewControl", "HVX.Controls.CalibrationProgramControl" };
            for (int index = 0; index < names.Length; index++)
            {
                var module = Assembly.LoadFrom(Path.Combine(build, "modules", names[index], dlls[index]));
                string path = Fixture(qa, names[index]), cable = Path.Combine(path, "CABLE.in");
                byte[] before = File.ReadAllBytes(cable), item = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
                using (var control = (Control)Activator.CreateInstance(module.GetType(types[index], true)))
                {
                    if (index == 0)
                    {
                        Set(control, "ShowDialogMessages", false); Set(control, "EmbeddedMode", true); Set(control, "OnlineWeatherEnabled", false);
                        Set(control, "AutoRefreshWeatherOnHardwareSample", false); Set(control, "SettingsPath", Path.Combine(qa, "catalog.ini"));
                        Check((bool)Call(control, "LoadProject", path), "creation loads measured fixture");
                    }
                    else if (index == 1) { Check((bool)Call(control, "LoadProject", path), "browser loads fixture"); Pump(() => !(bool)Get(control, "IsBusy")); }
                    else if (index == 2) { Set(control, "PromptSavedAnalysisOnProjectLoad", false); Set(control, "ProjectPath", path); }
                    else { Set(control, "SimulationMode", false); Set(control, "ProjectPath", path); Set(control, "SelectedPhaseIndex", 0); }
                    using (var host = Host(control))
                    {
                        int changed = 0; control.GetType().GetEvent("CableStructureChanged").AddEventHandler(control, new EventHandler(delegate { changed++; }));
                        object plan = Plan(module, path, 80); Inject(module, true);
                        try
                        {
                            if (index == 0) Call(control, "CommitMeasuredJoint", plan);
                            else if (index == 1) { Check((bool)Call(control, "RunJointSave", plan), "browser queued write"); Pump(() => !(bool)Get(control, "IsBusy")); }
                            else if (index == 2) Call(control, "ApplySharedCableJoint", plan);
                            else Call(control, "CommitSharedJointPlan", plan);
                        }
                        finally { Inject(module, false); }
                        DiskSaved(path, before, item); Check(changed == 1, names[index] + " broadcasts committed change despite restore warning");
                        if (index == 0)
                        {
                            Check(((DataGridView)Get(control, "cableGrid")).Rows.Count == 1, "creation reloads committed joint row");
                            Check((string)Get(control, "loadedJointCableHash") == (string)Call(module.GetType("HVX.ProjectCables.CableJointStore"), "Hash", File.ReadAllBytes(cable)), "creation advances baseline hash");
                            Check(((Control)Get(control, "statusLabel")).Text.Contains("QA injected"), "creation warning remains visible");
                        }
                        else if (index == 1)
                        {
                            Check((int)Get(control, "LastChangedFileCount") == 1, "browser reports committed file count");
                            Check(((TextBox)Get(control, "information")).Text.Contains("QA injected") && !((Label)Get(control, "status")).Text.Contains("接头未保存"), "browser success-with-warning not false failure");
                        }
                        else if (index == 2)
                        {
                            Check(((double[])Get(Get(control, "Spectrum"), "CurrentJointPositionsM")).SequenceEqual(new[] { 80d }), "analysis markers refresh after warning");
                            Check(((string)Get(control, "CableStructureEditStatus")).Contains("QA injected"), "analysis exposes saved-with-warning");
                        }
                        else
                        {
                            Check(((Array)Call(control, "GetPhaseJoints", 0)).Length == 1, "calibration publishes committed marker");
                            Check(((string)Get(control, "LastJointCommitWarning")).Contains("QA injected"), "calibration returns warning for explicit saved notice");
                        }
                        host.Close();
                    }
                }
                // Legacy callers get an explicit committed exception, with the
                // recovery path, rather than an ambiguous ordinary IOException.
                string legacy = Fixture(qa, names[index] + "-legacy"); Inject(module, true);
                try
                {
                    Exception failure = null;
                    try { Call(module.GetType("HVX.ProjectCables.CableJointStore"), "Commit", Plan(module, legacy, 80)); }
                    catch (TargetInvocationException ex) { failure = ex.InnerException; }
                    Check(failure != null && failure.GetType().Name == "CableJointCommittedException", "legacy contract explicitly identifies committed outcome");
                    object result = Get(failure, "Result"); Check((bool)Get(result, "Committed") && File.Exists((string)Get(result, "BackupPath")), "legacy exception retains committed result and backup");
                }
                finally { Inject(module, false); }
            }
            Console.WriteLine("CABLE_JOINT_COMMIT_WARNING_PASS=" + checks + "; HARDWARE=False; EVIDENCE=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error); return 1; }
    }
}
