using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CreationDraftJointIntegritySmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks; static Type type; static string qa;
    static object Get(object o, string n) { var p = o.GetType().GetProperty(n, All); return p != null ? p.GetValue(o, null) : o.GetType().GetField(n, All).GetValue(o); }
    static void Set(object o, string n, object v) { o.GetType().GetProperty(n, All).SetValue(o, v, null); }
    static object Call(object o, string n, params object[] p) { return o.GetType().GetMethods(All).Single(m => m.Name == n && m.GetParameters().Length == p.Length).Invoke(o, p); }
    static void Check(bool ok, string n) { checks++; if (!ok) throw new Exception(n); }
    static string Block(string phase, string material, bool joint = true)
    { return "0\t" + phase + "\t\t\r\n1\t人工始端\t冷缩\t0m\r\n2\t本体\t" + material + "\t\r\n" + (joint ? "1\t人工名称\t冷缩\t80.123m\r\n2\t本体\t后" + material + "\t\r\n" : "") + "1\t人工终端\t冷缩\t400m\r\n"; }
    static string Fixture(string name, string mode, bool joint, bool measured, bool repeated = false)
    {
        string p = Path.Combine(qa, name); Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "ITEM.in"), "[ITEM]\r\n始端地点=QA start\r\n终端地点=QA end\r\n相电压=8.7kV\r\n电缆芯数=" + mode + "\r\nA相电缆长度=400\r\nB相电缆长度=" + (mode == "单相" ? "0" : "400") + "\r\nC相电缆长度=" + (mode == "单相" ? "0" : "400") + "\r\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(p, "CABLE.in"), Block("A相", "塑料", joint) + (mode == "单相" ? "" : Block(repeated ? "A相" : "B相", "油纸", joint) + Block(repeated ? "A相" : "C相", "橡胶", joint)), Encoding.UTF8);
        if (measured) File.WriteAllBytes(Path.Combine(p, "raw.dem"), new byte[] { 1, 3, 5 }); return p;
    }
    static Control Page()
    {
        var c = (Control)Activator.CreateInstance(type); Set(c, "ShowDialogMessages", false); Set(c, "EmbeddedMode", true); Set(c, "OnlineWeatherEnabled", false); Set(c, "AutoRefreshWeatherOnHardwareSample", false); Set(c, "SettingsPath", Path.Combine(qa, "settings.ini")); return c;
    }
    static void EqualPositions(Control c, params double[] expected)
    { foreach (int phase in new[] { 0, 1, 2 }) Check(((double[])Call(c, "GetPhaseJointPositions", phase)).SequenceEqual(expected), "all shared phase positions agree: " + phase); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); qa = Path.Combine(Path.GetFullPath(args[1]), "draft-joints-" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(qa);
            type = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll")).GetType("HVX.Controls.ProjectCreationControl", true);
            using (var c = Page())
            {
                string p = Fixture("shared", "一体三芯", true, false);
                Check((bool)Call(c, "LoadProject", p), "shared fixture loads");
                Check((bool)Call(c, "AddJointWithMaterials", "A相", "新增附件", "", "", 40d), "shared add"); EqualPositions(c, 40, 80.123);
                foreach (int phase in new[] { 0, 1, 2 }) { string material = new[] { "塑料", "油纸", "橡胶" }[phase]; Check(((string[])Call(c, "GetPhaseSegmentBodyTypes", phase)).SequenceEqual(new[] { material, material, "后" + material }), "blank add preserves each phase segment material"); }
                Check((bool)Call(c, "SaveProject") && (bool)Call(c, "LoadProject", p), "shared save/load"); EqualPositions(c, 40, 80.123);
                Check((bool)Call(c, "EditJoint", "B相", 0, 60d, "改附件", ""), "editing any shared row moves all physical phases"); EqualPositions(c, 60, 80.123);
                Check((bool)Call(c, "RemoveJoint", "C相", 0), "same-material shared delete needs no merge consent"); EqualPositions(c, 80.123);
                Check(!(bool)Call(c, "RemoveJoint", "A相", 0), "mixed delete without explicit decision rejected"); EqualPositions(c, 80.123);
                int decisions = 0; string text = "";
                Set(c, "ConfirmJointMaterialMerge", new Func<string, bool>(m => { decisions++; text = m; return false; }));
                Check(!(bool)Call(c, "RemoveJoint", "A相", 0) && decisions == 1, "cancel preserves all shared segments"); EqualPositions(c, 80.123);
                Set(c, "ConfirmJointMaterialMerge", new Func<string, bool>(m => { decisions++; text = m; return true; }));
                Check((bool)Call(c, "RemoveJoint", "A相", 0) && decisions == 2, "one explicit decision removes full shared scope"); EqualPositions(c);
                Check(text.Contains("塑料") && text.Contains("油纸") && text.Contains("橡胶") && text.Contains("→"), "confirmation enumerates all material merges");
                Check((bool)Call(c, "SaveProject") && (bool)Call(c, "LoadProject", p), "deletion persists consistently"); EqualPositions(c);
                string duplicate = Fixture("duplicate", "一体三芯", true, false, true); byte[] before = File.ReadAllBytes(Path.Combine(duplicate, "CABLE.in"));
                Check((bool)Call(c, "LoadProject", duplicate), "duplicate labels load faithfully");
                Check(!(bool)Call(c, "AddJoint", "A相", "附件", "", 40d), "ambiguous duplicate phase draft not flattened");
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(duplicate, "CABLE.in"))), "duplicate source bytes unchanged");
            }
            foreach (bool measured in new[] { false, true }) using (var c = Page())
            {
                string p = Fixture("mixed-" + measured, "单相", true, measured); Check((bool)Call(c, "LoadProject", p), "mixed fixture loads");
                byte[] before = File.ReadAllBytes(Path.Combine(p, "CABLE.in"));
                Check(!(bool)Call(c, "RemoveJoint", "A相", 0), "no-dialog API never silently merges different insulation");
                Check(!(bool)Call(c, "EditJoint", "A相", 0, 200d, "", ""), "no-dialog move also requires merge consent");
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(p, "CABLE.in"))), "rejected operations do not write");
                Set(c, "ConfirmJointMaterialMerge", new Func<string, bool>(m => m.Contains("后塑料") && m.Contains("→")));
                Check((bool)Call(c, "RemoveJoint", "A相", 0), "explicitly confirmed merge succeeds");
                if (!measured) Check((bool)Call(c, "SaveProject"), "draft merge commits on save: " + Get(c, "LastError"));
                Check((bool)Call(c, "LoadProject", p) && ((double[])Call(c, "GetPhaseJointPositions", 0)).Length == 0, "committed merge reloads without stale node");
                if (measured) Check(File.ReadAllBytes(Path.Combine(p, "raw.dem")).SequenceEqual(new byte[] { 1, 3, 5 }), "raw data preserved");
            }
            using (var c = Page())
            {
                string p = Fixture("environment", "单相", false, false); Check((bool)Call(c, "LoadProject", p), "environment fixture loads");
                DateTime time = new DateTime(2026, 9, 30, 1, 2, 3, DateTimeKind.Utc);
                Check((bool)Call(c, "ApplyHardwareSample", 23.4, 55.6, 39.9, 116.4, 50d, "定位有效", time), "legacy valid sample accepted");
                Check((bool)Get(c, "HasValidEnvironmentSample") && (bool)Get(c, "HasValidGnssFix") && (bool)Get(c, "HasGnssAltitude"), "legacy validity flags set");
                Check((DateTime)Get(c, "EnvironmentSampleTime") == time && (DateTime)Get(c, "GnssSampleTime") == time, "original sample time preserved");
                Check((bool)Call(c, "SaveProject") && (bool)Call(c, "LoadProject", p), "legacy sample persists/reloads");
                Check((bool)Get(c, "HasValidEnvironmentSample") && (bool)Get(c, "HasValidGnssFix") && ((Label)Get(c, "latitudeValue")).Text == "39.900000", "valid sample survives UI restoration");
                Check((bool)Call(c, "ApplyHardwareSample", 22d, 40d, 0d, 0d, 0d, "未知", time), "environment with unknown fix accepted independently");
                Check((bool)Get(c, "HasValidEnvironmentSample") && !(bool)Get(c, "HasValidGnssFix") && ((Label)Get(c, "latitudeValue")).Text == "—", "unknown fix is never fabricated");
                Check(!(bool)Call(c, "ApplyHardwareSample", Double.NaN, 40d, 0d, 0d, 0d, "定位有效", time), "invalid sample rejected before updates");
            }
            Console.WriteLine("PASS " + checks + "; HARDWARE=False; " + qa); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
