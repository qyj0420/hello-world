using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ProjectLoadIntegritySmoke
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type pageType; static int checks;
    static void Check(bool value, string name) { ++checks; if (!value) throw new Exception(name); }
    static object Get(object owner, string name) { return owner.GetType().GetProperty(name, Flags).GetValue(owner, null); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, Flags).SetValue(owner, value, null); }
    static bool Call(object owner, string name, params object[] args) { return (bool)owner.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner, args); }
    static Control Page(string qa) {
        var page = (Control)Activator.CreateInstance(pageType);
        Set(page, "ShowDialogMessages", false); Set(page, "OnlineWeatherEnabled", false);
        Set(page, "AutoRefreshWeatherOnHardwareSample", false); Set(page, "SettingsPath", Path.Combine(qa, "refs-" + Guid.NewGuid().ToString("N") + ".ini"));
        return page;
    }
    static string Fixture(string qa, string name, string extra, bool measured = false) {
        string p = Path.Combine(qa, name); Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "ITEM.in"), "[ITEM]\r\n所属单位 = OriginalOrg\r\n始端地点 = Start\r\n终端地点 = End\r\n电缆芯数 = 单相\r\n相电压 = 8.7kV\r\nA相电缆长度 = 400\r\n" + extra, Encoding.UTF8);
        File.WriteAllText(Path.Combine(p, "CABLE.in"), "0\tA相\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t终端\t户内冷缩\t400m\r\n", Encoding.UTF8);
        if (measured) File.WriteAllBytes(Path.Combine(p, "raw.dem"), new byte[] { 1, 2, 3 });
        return p;
    }
    static string Read(string p) { return File.ReadAllText(Path.Combine(p, "ITEM.in"), Encoding.GetEncoding(936)); }
    [STAThread] static int Main(string[] args) {
        try {
            Application.EnableVisualStyles();
            string qa = Path.Combine(Path.GetFullPath(args[1]), "project-load-integrity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            pageType = Assembly.LoadFrom(Path.Combine(args[0], "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll")).GetType("HVX.Controls.ProjectCreationControl", true);
            int index = 0;
            foreach (string badValue in new[] { "温度 = NaN", "温度 = Infinity", "温度 = 999", "湿度 = -1", "湿度 = 101", "湿度 = 1E100", "加压倍数 = NaN\r\n加压次数 = 1" }) {
                string old = Fixture(qa, "old-" + index, ""), incoming = Fixture(qa, "bad-" + index++, badValue + "\r\n");
                File.WriteAllText(Path.Combine(incoming, "ITEM.in"), File.ReadAllText(Path.Combine(incoming, "ITEM.in")).Replace("OriginalOrg", "IncomingBadOrg"), Encoding.UTF8);
                byte[] before = File.ReadAllBytes(Path.Combine(old, "ITEM.in"));
                using (var page = Page(qa)) {
                    Check(Call(page, "LoadProject", old), "load valid old project");
                    Set(page, "Notes", "unsaved existing draft");
                    Check(!Call(page, "LoadProject", incoming), "reject bad incoming value: " + badValue);
                    Check((string)Get(page, "CurrentProjectPath") == old, "old path retained");
                    Check((string)Get(page, "Organization") == "OriginalOrg" && (string)Get(page, "Notes") == "unsaved existing draft", "old fields retained");
                    Check((bool)Get(page, "IsDirty"), "unsaved dirty flag retained");
                    Check(File.ReadAllBytes(Path.Combine(old, "ITEM.in")).SequenceEqual(before), "failed load never writes old project");
                    Check(Call(page, "SaveProject"), "old draft still saves");
                    Check(!Read(old).Contains("IncomingBadOrg"), "rejected project fields cannot leak into saved old project");
                }
            }
            foreach (bool measured in new[] { false, true }) {
                string p = Fixture(qa, "quotes-" + measured, "VendorLiteral = \"A \"quoted\" B\"\r\nVendorTabs = \"left\tright\"\r\n", measured);
                byte[] cable = File.ReadAllBytes(Path.Combine(p, "CABLE.in"));
                using (var page = Page(qa)) {
                    for (int pass = 0; pass < 2; pass++) {
                        Check(Call(page, "LoadProject", p), "load opaque values"); Set(page, "Notes", "notes " + pass);
                        Check(Call(page, "SaveProject"), "save opaque values: " + Get(page, "LastError"));
                        string text = Read(p);
                        Check(text.Contains("A \"quoted\" B") && text.Contains("left\tright"), "unknown quotes/tabs preserved");
                        Check(File.ReadAllBytes(Path.Combine(p, "CABLE.in")).SequenceEqual(cable), "unrelated cable bytes unchanged");
                    }
                    if (measured) Check(File.ReadAllBytes(Path.Combine(p, "raw.dem")).SequenceEqual(new byte[] { 1, 2, 3 }), "raw acquisition unchanged");
                }
            }
            Console.WriteLine("PROJECT_LOAD_INTEGRITY_PASS=" + checks + "; hardware=false; QA=" + qa); return 0;
        } catch (Exception e) { Console.WriteLine(e); return 1; }
    }
}
