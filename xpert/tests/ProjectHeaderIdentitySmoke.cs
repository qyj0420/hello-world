using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ProjectHeaderIdentitySmoke
{
    static int checks;
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static object Field(object value, string name) { return value.GetType().GetField(name, Hidden).GetValue(value); }
    static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, Hidden).Invoke(value, args); }
    static string Cable(string phase, double length) { return "0\t"+phase+"\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t"+length.ToString(System.Globalization.CultureInfo.InvariantCulture)+"m\r\n"; }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[0]), "identity-" + Guid.NewGuid().ToString("N"));
            string project = Path.Combine(qa, "2026年05月05日16时31分"); Directory.CreateDirectory(project);
            string item = Path.Combine(project, "ITEM.in"), cable = Path.Combine(project, "CABLE.in"); File.WriteAllText(cable, Cable("A",414.8)+Cable("B",414.8)+Cable("C",414.8));
            string metadata = "[ITEM]\r\n始端地点=\"变电站甲\"\r\n终端地点=\"电缆终端乙\"\r\n";
            var identity = new ProjectHeaderIdentity();
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.GetEncoding(936), new UnicodeEncoding(false, false) })
            {
                File.WriteAllText(item, metadata, encoding); identity.Invalidate(); identity.Load(project);
                Check(identity.StartName == "变电站甲" && identity.EndName == "电缆终端乙", "legacy encoding: " + encoding.WebName);
                int reads = identity.ReadCount; for (int i = 0; i < 20; i++) identity.Load(project);
                Check(identity.ReadCount == reads, "unchanged metadata does not reread on status refresh");
                int cableReads = identity.CableReadCount; identity.Load(project);
                Check(identity.CableReadCount == cableReads && identity.LengthCaption(false)=="电缆长度：414.8 m", "geometry cached and equal lengths collapse with units");
            }
            File.WriteAllText(item, "Start_Name=Start\r\nEnd Name=Terminal\r\n", Encoding.UTF8); identity.Invalidate(); identity.Load(project);
            Check(identity.StartName == "Start" && identity.EndName == "Terminal", "English key aliases");
            File.WriteAllText(item, "始端地點=甲\r\n終端地點=乙\r\n", Encoding.UTF8); identity.Invalidate(); identity.Load(project);
            Check(identity.StartName == "甲" && identity.EndName == "乙", "traditional aliases");
            File.WriteAllText(item, "[ITEM]\r\n", Encoding.UTF8); identity.Invalidate(); identity.Load(project);
            Check(identity.StartName.Length == 0 && identity.EndName.Length == 0, "missing endpoints not inferred from unrelated directory");
            File.WriteAllText(item, metadata, Encoding.UTF8); File.SetAttributes(item, File.GetAttributes(item) | FileAttributes.ReadOnly);
            byte[] original = File.ReadAllBytes(item); var attributes = File.GetAttributes(item);
            foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
            {
                var state = new ApplicationState(); string message; Check(state.TryLoadProject(project, out message), "fixture load");
                var settings = new AppSettings { LanguageCode = language, DataRoot = qa }; settings.Hardware.Enabled = false;
                using (var form = new MainForm(settings, state, true))
                {
                    form.ShowInTaskbar = false; form.Opacity = 0; form.Location = new Point(-20000, -20000); form.StartPosition = FormStartPosition.Manual; form.Show();
                    foreach (string id in new[] { "ProjectManagement", "ProjectCreation", "DataBrowser", "DataAnalysis", "ReportGenerator", "CalibrationTest", "PressureTest" })
                    {
                        Call(form, "ShowEmbeddedModule", id, new UserControl(), (bool?)false); Application.DoEvents();
                        var context = (Label)Field(form, "moduleContext"); var title = (Label)Field(form, "moduleTitle");
                        Check(context.Text.Contains("变电站甲 → 电缆终端乙") && context.Text.Contains("2026-05-05 16:31:00") && context.Text.Contains("414.8 m"), "route plus invariant project identity and cable length: " + id);
                        Check(((ToolTip)Field(form, "deviceSetupTip")).GetToolTip(context) == context.Text, "full identity tooltip");
                        foreach (int width in new[] { 1000, 1366, 1920 })
                        {
                            form.ClientSize = new Size(width, form.ClientSize.Height); Application.DoEvents();
                            Check(!context.Bounds.IntersectsWith(title.Bounds), "header labels do not overlap");
                            Check(context.Height >= TextRenderer.MeasureText("Ag中", context.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height * 2, "both header lines have height");
                        }
                        if (id == "DataAnalysis") form.RenderToPng(Path.Combine(qa, "header-" + language + ".png"));
                        Call(form, "ShowHome");
                    }
                    state.ClearProject(); Check(!((Label)Field(form, "moduleContext")).Text.Contains("变电站甲"), "release clears endpoint context");
                    form.Close();
                }
            }
            Check(Convert.ToBase64String(original) == Convert.ToBase64String(File.ReadAllBytes(item)) && File.GetAttributes(item) == attributes, "identity display preserves file bytes and readonly");
            File.SetAttributes(item, attributes & ~FileAttributes.ReadOnly); File.WriteAllText(item, "StartName=Changed\r\nEndName=New\r\n");
            identity.Load(project); Check(identity.StartName == "Changed" && identity.EndName == "New", "changed metadata refreshes");
            File.WriteAllText(cable, Cable("A",401.1)+Cable("B",402.2)+Cable("C",403.3)); identity.Load(project);
            Check(identity.LengthCaption(true)=="Cable length: A 401.1 m / B 402.2 m / C 403.3 m", "unequal phases show individual real lengths, not max");
            File.WriteAllText(cable, Cable("A",401.1)+Cable("A",402.2)+Cable("A",403.3)); identity.Invalidate(); identity.Load(project);
            Check(identity.LengthCaption(false).Contains("A#1 401.1 m / A#2 402.2 m / A#3 403.3 m"), "repeated historical phase labels not relabeled B/C");
            File.WriteAllText(cable,"broken"); identity.Load(project); int invalidReads=identity.CableReadCount; identity.Load(project);
            Check(identity.LengthCaption(false)=="电缆长度：—"&&identity.CableReadCount==invalidReads,"malformed cable clears old length and does not reread unchanged invalid data");
            identity.Load(""); Check(identity.StartName.Length == 0 && identity.EndName.Length == 0, "identity cache clears");
            Check(identity.LengthCaption(false)=="电缆长度：—","release clears geometry too");
            Console.WriteLine("PROJECT_HEADER_IDENTITY_PASS=" + checks + "; HARDWARE=False; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
