// Read-only audit of production code. The seven reproductions below deliberately
// exercise known deficiencies against new, isolated fixtures; this is not a
// release acceptance test and must not be mistaken for seven fixed defects.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ProjectUiAudit117Probe
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly module; static string qa; static int reproduced;
    static object Get(object o, string name) { var p = o.GetType().GetProperty(name, All); return p != null ? p.GetValue(o, null) : o.GetType().GetField(name, All).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetProperty(name, All).SetValue(o, value, null); }
    static object Call(object o, string name, params object[] args) { return o.GetType().GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args); }
    static string Text(string path) { var bytes = File.ReadAllBytes(path); try { return new UTF8Encoding(false, true).GetString(bytes); } catch { return Encoding.GetEncoding(936).GetString(bytes); } }
    static void Finding(int id, bool yes, string detail) { if (!yes) throw new Exception("Not reproduced F" + id + ": " + detail); reproduced++; Console.WriteLine("F" + id + " REPRODUCED: " + detail); }
    static string Block(string material, string length) { return "0\tA\t\t\r\n1\t人工始端\t冷缩\t0m\r\n2\t原段\t" + material + "\t\r\n1\t人工终端\t冷缩\t" + length + "m\r\n"; }
    static string Fixture(string name, bool measured = false, string length = "400", int blocks = 1)
    {
        string root = Path.Combine(qa, name); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ITEM.in"), "[ITEM]\r\n始端地点 = audit start\r\n终端地点 = audit end\r\n电缆芯数 = 单相\r\nA相电缆长度 = " + length + "\r\nB相电缆长度 = 0\r\nC相电缆长度 = 0\r\n电缆长度 = " + length + "\r\n相电压 = 8.7kV\r\n备注 = original\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "CABLE.in"), Block("XLPE", length) + (blocks > 1 ? Block("PVC", length) : ""), new UTF8Encoding(false));
        if (measured) File.WriteAllBytes(Path.Combine(root, "raw.dem"), new byte[] { 1, 2, 3 }); return root;
    }
    static Control Load(string root)
    {
        var c = (Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectCreationControl", true));
        Set(c, "ShowDialogMessages", false); Set(c, "OnlineWeatherEnabled", false); Set(c, "AutoRefreshWeatherOnHardwareSample", false);
        Set(c, "SettingsPath", Path.Combine(qa, "catalog.ini"));
        if (!(bool)Call(c, "LoadProject", root)) throw new Exception("Fixture load failed: " + ((Label)Get(c, "statusLabel")).Text);
        string[] validation = (string[])Call(c, "ValidateProject"); if (validation.Length > 0) throw new Exception("Invalid fixture: " + String.Join(";", validation));
        return c;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            qa = Path.Combine(Path.GetFullPath(args[1]), "audit117-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            module = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            string path = Fixture("F1-catalog", true);
            using (var c = Load(path))
            {
                int saved = 0; c.GetType().GetEvent("ProjectSaved").AddEventHandler(c, new EventHandler(delegate { saved++; }));
                string blocked = Path.Combine(qa, "catalog-parent-file"); File.WriteAllText(blocked, "This is deliberately a file, not a directory.");
                Set(c, "SettingsPath", Path.Combine(blocked, "settings.ini")); Set(c, "Notes", "F1 committed note");
                bool result = (bool)Call(c, "SaveProject");
                Finding(1, !result && saved == 0 && Text(Path.Combine(path, "ITEM.in")).Contains("F1 committed note"), "catalog failure reports failed/no saved event after ITEM replacement completed");
            }
            path = Fixture("F2-concurrent");
            using (var c = Load(path))
            {
                string item = Path.Combine(path, "ITEM.in"); File.WriteAllText(item, Text(item).Replace("original", "another editor revision"), new UTF8Encoding(false));
                Set(c, "OperatorName", "audit operator"); bool result = (bool)Call(c, "SaveProject");
                Finding(2, result && Text(item).Contains("original") && !Text(item).Contains("another editor revision"), "unmeasured metadata save silently replaces newer external values");
            }
            path = Fixture("F3-pair");
            using (var c = Load(path))
            {
                string cable = Path.Combine(path, "CABLE.in"); byte[] before = File.ReadAllBytes(cable); File.SetAttributes(cable, FileAttributes.ReadOnly);
                Set(c, "Notes", "F3 new ITEM only"); bool result = (bool)Call(c, "SaveProject");
                Finding(3, !result && Text(Path.Combine(path, "ITEM.in")).Contains("F3 new ITEM only") && before.SequenceEqual(File.ReadAllBytes(cable)), "ITEM replaced but CABLE failed; partial project update survives failed result");
            }
            path = Fixture("F4-blocks", false, "400", 2);
            using (var c = Load(path))
            {
                Set(c, "Notes", "F4 notes only"); bool result = (bool)Call(c, "SaveProject"); string cable = Text(Path.Combine(path, "CABLE.in"));
                Finding(4, result && cable.Split(new[] { "0\tA" }, StringSplitOptions.None).Length == 2 && !cable.Contains("PVC"), "two real A blocks collapse to one and second material vanishes on unmeasured notes save");
            }
            path = Fixture("F5-unknown"); File.AppendAllText(Path.Combine(path, "ITEM.in"), "VendorCalibrationVersion = preserve-me\r\n", new UTF8Encoding(false));
            using (var c = Load(path))
            {
                Set(c, "Notes", "F5 known notes only"); bool result = (bool)Call(c, "SaveProject");
                Finding(5, result && !Text(Path.Combine(path, "ITEM.in")).Contains("preserve-me"), "unmeasured save drops existing unknown ITEM fields without concurrent edits");
            }
            path = Fixture("F6-length", true, "400.123");
            using (var c = Load(path))
            {
                bool changed = (bool)Call(c, "GeometryChangedFromLoaded"); bool added = (bool)Call(c, "AddJoint", "A相", "冷缩", "", 80d);
                Finding(6, changed && !added && (double)Get(c, "CableLengthA") == 400.1 && Text(Path.Combine(path, "CABLE.in")).Contains("400.123m"), "loading precise locked length rounds UI then falsely flags geometry changed and blocks joint append");
            }
            path = Fixture("F7-iso", true); File.AppendAllText(Path.Combine(path, "ITEM.in"), "硬件采样时间 = 2026-05-05T08:31:17Z\r\n", new UTF8Encoding(false));
            using (var c = Load(path))
            {
                DateTime loaded = (DateTime)Get(c, "HardwareSampleTime"); Set(c, "Notes", "F7 metadata only"); bool result = (bool)Call(c, "SaveProject");
                Finding(7, loaded == DateTime.MinValue && result && !Text(Path.Combine(path, "ITEM.in")).Contains("2026-05-05T08:31:17Z"), "valid ISO UTC timestamp without seven fractional digits is treated as missing and erased on save");
            }
            Console.WriteLine("AUDIT117_REPRODUCED=" + reproduced + "; NOT_FIXED; HARDWARE=False; EVIDENCE=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error); return 1; }
    }
}
