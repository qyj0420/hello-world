using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

// Pure metadata fixtures. No desktop host, device, acquisition, or report export.
internal static class ProjectEnglishConsumerSmoke
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static object Property(object instance, string name) { return instance.GetType().GetProperty(name, All).GetValue(instance, null); }
    private static object Field(object instance, string name) { return instance.GetType().GetField(name, All).GetValue(instance); }
    private static object Read(Type type, string method, string path)
    { return type.GetMethod(method, All, null, new[] { typeof(string) }, null).Invoke(null, new object[] { path }); }
    private static string Item(bool english, bool omitTotal)
    {
        string[] cn = { "所属单位", "测试人员", "测试地点", "始端地点", "终端地点", "电缆长度", "相电压", "日期", "时间", "电缆芯数", "加压倍数", "加压次数", "备注", "A相绝缘电阻1", "A相绝缘电阻2", "A相电缆长度" };
        string[] en = { "TestingOrganization", "TestPersonnel", "TestLocation", "StartName", "EndName", "CableLength", "PhaseVoltage", "Date", "Time", "CoreCount", "PressureMultipliers", "PressureRepetitions", "Remarks", "A1", "A2", "CableLengthA" };
        string[] values = { "雄新电力", "测试人员甲", "试验地点乙", "始端厂区", "终端厂区", "1000.5", "8.7kV", "2026-10-11", "15:29:33", "Three-phase single-core", "0,0.5,1,0", "1,1,3,1", "保留用户原文", "1234.5", "2345.6", "900.25" };
        var result = new StringBuilder("[ITEM]\r\n");
        for (int i = 0; i < values.Length; i++)
            if (!omitTotal || i != 5) result.Append(english ? en[i] : cn[i]).Append(": = \"").Append(values[i]).Append("\"\r\n");
        result.Append("CustomVendorField: = \"未知字段保留\"\r\n");
        return result.ToString();
    }
    private static string Cable(bool english)
    {
        return "0\tA\t\t\r\n1\t" + (english ? "Start" : "始端") + "\t终端类型\t0m\r\n2\t" + (english ? "Body" : "本体") +
            "\t塑料绝缘\t\r\n1\t自定义接头名称\t中间接头\t234.5m\r\n2\t" + (english ? "Body" : "本体") +
            "\t塑料绝缘\t\r\n1\t" + (english ? "End" : "终端") + "\t终端类型\t1000.5m\r\n";
    }
    private static string Make(string root, string name, bool english, Encoding encoding, bool omitTotal)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), Item(english, omitTotal), encoding);
        File.WriteAllText(Path.Combine(path, "CABLE.in"), Cable(english), encoding);
        return path;
    }
    private static void Verify(Assembly module, string path)
    {
        string itemPath = Path.Combine(path, "ITEM.in"), cablePath = Path.Combine(path, "CABLE.in");
        byte[] itemBefore = File.ReadAllBytes(itemPath), cableBefore = File.ReadAllBytes(cablePath);
        object browser = Read(module.GetType("HVX.Controls.BrowserReader", true), "LoadProject", path);
        Check((double)Field(browser, "CableLengthMetres") == 1000.5, "browser total length is invariant / not the phase-specific length");
        Check((double)Field(browser, "PhaseVoltageKv") == 8.7, "browser rated voltage is invariant");
        Check(((string)Field(browser, "Title")).Contains("始端厂区—终端厂区"), "browser retains Unicode user labels");
        Check(((double[])Field(browser, "JointPositionsMetres")).SequenceEqual(new[] { 234.5 }), "browser cable topology survives English labels");
        var metadata = (Dictionary<string, string>)Field(browser, "Metadata");
        Check(metadata["CustomVendorField"] == "未知字段保留", "reader keeps unknown project metadata");
        object report = Read(module.GetType("HVX.ReportGenerator.ProjectReportDataReader", true), "Read", path);
        Check((string)Property(report, "InspectionOrganization") == "雄新电力", "report organization alias");
        Check((string)Property(report, "TestPersonnel") == "测试人员甲", "report personnel alias");
        Check((string)Property(report, "TestLocation") == "试验地点乙", "report location alias");
        Check((string)Property(report, "StartName") == "始端厂区" && (string)Property(report, "EndName") == "终端厂区", "report endpoints alias");
        Check((double)Property(report, "CableLengthM") == 1000.5 && (double)Property(report, "DeviceU0Kv") == 8.7, "report invariant numbers");
        Check(((double[])Property(report, "JointPositions")).SequenceEqual(new[] { 234.5 }), "report custom joint positions");
        Check(((string)Property(report, "JointSummary")).Contains("自定义接头名称"), "report keeps user-defined cable label");
        Check((string)Property(report, "Notes") == "保留用户原文", "report Unicode remarks");
        var values = (Dictionary<string, string>)Property(report, "Values");
        Check(values["project.date"] == "2026-10-11" && values["project.time"] == "15:29:33", "report fixed date/time keys");
        Check(values["test.voltage_multipliers"] == "0,0.5,1,0" && values["test.voltage_repeat_counts"] == "1,1,3,1", "report pressure schedule aliases");
        object phaseA = Property(report, "PhaseA");
        Check((string)Property(phaseA, "InsulationBeforeMohm") == "1234.5" && (string)Property(phaseA, "InsulationAfterMohm") == "2345.6", "report insulation aliases");
        Check(itemBefore.SequenceEqual(File.ReadAllBytes(itemPath)) && cableBefore.SequenceEqual(File.ReadAllBytes(cablePath)), "read-only metadata does not migrate originals");
    }
    private static int Main(string[] args)
    {
        CultureInfo oldCulture = Thread.CurrentThread.CurrentCulture, oldUi = Thread.CurrentThread.CurrentUICulture;
        try
        {
            string root = Path.Combine(Path.GetFullPath(args[1]), "english-consumers-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Assembly module = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ReportGenerator", "Xiongxin.OWTS.ReportGenerator.dll"));
            string[] projects = {
                Make(root, "english-utf8", true, new UTF8Encoding(true, true), false),
                Make(root, "legacy-cn-utf8", false, new UTF8Encoding(false, true), false),
                Make(root, "legacy-cn-gbk", false, Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), false),
                Make(root, "english-no-total", true, new UTF8Encoding(true, true), true)
            };
            foreach (string culture in new[] { "en-US", "de-DE", "fr-FR", "zh-CN" })
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture); Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                foreach (string project in projects) Verify(module, project);
            }
            Console.WriteLine("PROJECT_ENGLISH_CONSUMERS_PASS=" + checks + "; HARDWARE_ACCESSED=False; USER_PROJECTS_WRITTEN=False; EVIDENCE=" + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ((TargetInvocationException)ex).InnerException : ex); return 1; }
        finally { Thread.CurrentThread.CurrentCulture = oldCulture; Thread.CurrentThread.CurrentUICulture = oldUi; }
    }
}
