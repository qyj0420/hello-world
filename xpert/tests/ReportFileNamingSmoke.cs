using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Metadata and placeholder files only; no Office, printer, real project or device.
internal static class ReportFileNamingSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    static object Call(object value, string name, params object[] args) { Type type = value as Type ?? value.GetType(); return type.GetMethods(All).Single(x => x.Name == name && x.GetParameters().Length == args.Length).Invoke(value is Type ? null : value, args); }
    static void Set(object value, string name, object content) { value.GetType().GetProperty(name, All).SetValue(value, content, null); }
    static void Field(object value, string name, object content) { value.GetType().GetField(name, All).SetValue(value, content); }
    static string Available(Type names, string output, string project, string start, string end, int language, DateTime time, bool grid = false)
    { return (string)Call(names, "AvailableBase", output, project, start, end, language, time, grid); }
    static string Find(Type names, string output, string project, string start, string end, int language)
    { return (string)Call(names, "FindLatest", output, project, start, end, language); }
    static void Touch(string path, DateTime time) { File.WriteAllText(path, "Synthetic filename fixture; not a report.", Encoding.UTF8); File.SetLastWriteTimeUtc(path, time); }
    static void Throws<T>(Action action, string message) where T : Exception
    { bool caught = false; try { action(); } catch (TargetInvocationException ex) { caught = ex.InnerException is T; } catch (T) { caught = true; } Check(caught, message); }

    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[1]), "naming-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            string output = Path.Combine(qa, "Reports"); Directory.CreateDirectory(output);
            Assembly module = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ReportGenerator", "Xiongxin.OWTS.ReportGenerator.dll"));
            Type names = module.GetType("HVX.ReportGenerator.ReportFileNames", true);
            DateTime time = new DateTime(2026, 9, 29, 21, 42, 17), utc = new DateTime(2026, 9, 29, 13, 42, 17, DateTimeKind.Utc);
            string project = "项目甲", start = "始端甲", end = "Terminal Ω";
            string first = Available(names, output, project, start, end, 0, time);
            Check(Path.GetFileName(first) == "项目甲_始端甲—Terminal Ω_局放检测报告_20260929_214217", "Chinese exact requested naming and unmodified operator labels");
            Check(Path.GetFileNameWithoutExtension(first + ".docx") == Path.GetFileNameWithoutExtension(first + ".pdf"), "Word and PDF share one stem");
            Touch(first + ".docx", utc);
            Check(Available(names, output, project, start, end, 0, time) == first + "_001", "Word-only collision reserves the complete pair");
            Touch(first + "_001.pdf", utc.AddSeconds(1));
            Check(Available(names, output, project, start, end, 0, time) == first + "_002", "PDF-only collision reserves the complete pair");
            Directory.CreateDirectory(first + "_002.docx");
            Check(Available(names, output, project, start, end, 0, time) == first + "_003", "directory occupying a document filename is not overwritten");
            Touch(first + "_003_国网附录M.pdf", utc.AddSeconds(2));
            Check(Available(names, output, project, start, end, 0, time, true) == first + "_004", "supplementary collision advances ordinary and State Grid pair together");
            string grid = (string)Call(names, "AvailableGridBase", first, 0);
            Check(grid == first + "_国网附录M", "State Grid adds suffix to normal timestamp without a second timestamp");
            Touch(grid + ".docx", utc);
            Check((string)Call(names, "AvailableGridBase", first, 0) == grid + "_001", "supplementary duplicate does not overwrite");
            Check(Find(names, output, project, start, end, 0) == first + "_001.pdf", "new timestamp+sequence reports remain openable and ordinary report is preferred");
            string later = Available(names, output, project, start, end, 0, time.AddSeconds(1));
            Touch(later + ".pdf", utc.AddSeconds(4)); Touch(later + ".docx", utc.AddSeconds(5));
            Check(Find(names, output, project, start, end, 0) == later + ".docx", "new Word does not reopen older same-name PDF");
            File.SetLastWriteTimeUtc(later + ".pdf", utc.AddSeconds(6));
            Check(Find(names, output, project, start, end, 0) == later + ".pdf", "new paired PDF opens when current");
            for (int language = 0; language < 19; language++)
            {
                string path = Available(names, output, project, start, end, language, time.AddMinutes(language + 1));
                string kind = (string)Call(names, "Kind", language);
                Check(Path.GetFileName(path).StartsWith("项目甲_始端甲—Terminal Ω_", StringComparison.Ordinal), "language " + language + " preserves user text");
                Check(Path.GetFileName(path).Contains("_" + kind + "_"), "language " + language + " report kind is localized");
                Check(Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "language " + language + " valid Windows filename");
                Touch(path + ".docx", utc.AddMinutes(language + 1));
                Check(Find(names, output, project, start, end, language) == path + ".docx", "language " + language + " resolves its own generated report");
            }
            Check((string)Call(names, "Kind", 18) == "局放檢測報告" && (string)Call(names, "Kind", 1) == "Partial Discharge Test Report", "traditional Chinese and English report labels");
            foreach (string culture in new[] { "zh-CN", "en-US", "tr-TR", "ar-SA" })
            {
                CultureInfo prior = Thread.CurrentThread.CurrentCulture;
                try { Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture); Check((string)Call(names, "Stem", output, project, start, end, 0, time) == first.Substring(output.Length + 1), "timestamp invariant under " + culture); }
                finally { Thread.CurrentThread.CurrentCulture = prior; }
            }
            string invalid = Available(names, output, "..\\甲:/?*\"<>|", "甲\n乙", "终端. ", 0, time);
            Check(Path.GetDirectoryName(invalid) == output && Path.GetFileName(invalid).IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "untrusted metadata cannot escape output directory");
            Check(Available(names, output, "项目:A", start, end, 0, time) != Available(names, output, "项目_A", start, end, 0, time), "sanitization retains identity using hash");
            string huge = new string('长', 500) + "😀";
            string longBase = Available(names, output, huge, huge + "a", huge + "b", 1, time, true);
            Check((longBase + "_999999_StateGrid_AppendixM.docx").Length <= 240, "full path leaves room for extension, state-grid suffix and sequence");
            Check(!Path.GetFileName(longBase).Contains("\uFFFD") && !char.IsHighSurrogate(Path.GetFileName(longBase).Last()), "shortening does not split a surrogate");
            Touch(longBase + ".pdf", utc);
            Check(Find(names, output, huge, huge + "a", huge + "b", 1) == longBase + ".pdf", "shortened identity can be reopened");
            Check(longBase != Available(names, output, huge + "different", huge + "a", huge + "b", 1, time, true), "long names retain distinct project identity");
            Throws<PathTooLongException>(() => Call(names, "Stem", Path.Combine(qa, new string('d', 130)), project, start, end, 0, time), "overlong destination explicitly rejected without unsafe shortening");

            string legacyDir = Path.Combine(qa, "Legacy"); Directory.CreateDirectory(legacyDir);
            string legacy = Path.Combine(legacyDir, project + "_OWTS_Report_zh-CN.pdf"); Touch(legacy, utc);
            Touch(Path.Combine(legacyDir, "Other_OWTS_Report_zh-CN.pdf"), utc.AddDays(1));
            Check(Find(names, legacyDir, project, start, end, 0) == legacy, "legacy report remains discoverable without borrowing another project");
            string legacyGridDir = Path.Combine(qa, "OldGrid"); Directory.CreateDirectory(legacyGridDir);
            string legacyGrid = Path.Combine(legacyGridDir, project + "_OWTS_Report_zh-CN_国网附录M_20260929_214217_abcd1234.docx"); Touch(legacyGrid, utc);
            Check(Find(names, legacyGridDir, project, start, end, 0) == legacyGrid, "old timestamped State Grid supplement remains discoverable");
            string newer = Available(names, legacyDir, project, start, end, 0, time); Touch(newer + ".docx", utc.AddDays(2));
            Check(Find(names, legacyDir, project, start, end, 0) == newer + ".docx", "new naming supersedes older legacy report");
            string gridOnlyDir = Path.Combine(qa, "GridOnly"); Directory.CreateDirectory(gridOnlyDir);
            string gridOnly = Available(names, gridOnlyDir, project, start, end, 0, time) + "_国网附录M.docx"; Touch(gridOnly, utc);
            Check(Find(names, gridOnlyDir, project, start, end, 0) == gridOnly, "State Grid-only successful output remains openable");

            string fixture = Path.Combine(qa, "NameFixture"); Directory.CreateDirectory(fixture);
            string item = Path.Combine(fixture, "ITEM.in"), cable = Path.Combine(fixture, "CABLE.in");
            File.WriteAllText(item, "始端名称=原文端甲\r\n终端名称=Raw End\r\n电缆长度=100\r\n相电压=8.7\r\n", Encoding.UTF8);
            File.WriteAllText(cable, "0\tA相\t\t\r\n1\t原文端甲\t终端\t0\r\n2\t本体\t塑料绝缘\t\r\n1\tRaw End\t终端\t100\r\n", Encoding.UTF8);
            byte[] beforeItem = File.ReadAllBytes(item), beforeCable = File.ReadAllBytes(cable);
            using (Control control = (Control)Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGeneratorControl", true)))
            {
                Set(control, "PromptAnalysisOnLoad", false); Set(control, "OutputDirectory", output);
                Check((bool)Call(control, "LoadProject", fixture), "synthetic report project loads");
                Call(control, "SetLanguageIndex", 1);
                string actual = (string)Call(control, "BuildDefaultOutputBase", output, time);
                Check(Path.GetFileName(actual) == "NameFixture_原文端甲—Raw End_Partial Discharge Test Report_20260929_214217", "actual control maps project and endpoint metadata to new naming");
                Touch(actual + ".docx", utc.AddDays(3));
                Check((string)Call(control, "FindOpenableReportPath") == actual + ".docx", "actual control opens newly named own report");
                Call(control, "SetLanguageIndex", 18);
                Check(((string)Call(control, "BuildDefaultOutputBase", output, time)).Contains("_局放檢測報告_"), "actual control applies current UI language without altering names");
            }
            Check(beforeItem.SequenceEqual(File.ReadAllBytes(item)) && beforeCable.SequenceEqual(File.ReadAllBytes(cable)), "all report naming is read-only to project metadata");
            object plan = Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGenerationPlan", true));
            string placeholderTemplate = Path.Combine(qa, "template.docx"); Touch(placeholderTemplate, utc);
            Field(plan, "ProjectPath", fixture); Field(plan, "TemplatePath", placeholderTemplate); Field(plan, "OutputDocxPath", first + ".docx");
            Field(plan, "OutputPdfPath", first + ".pdf"); Field(plan, "GeneratePdf", false); Field(plan, "Overwrite", false);
            byte[] beforeOutput = File.ReadAllBytes(first + ".docx");
            Throws<IOException>(() => Call(module.GetType("HVX.ReportGenerator.ReportDocumentService", true), "ValidatePlan", plan), "writer rejects a file appearing after name selection when overwrite is false");
            Check(beforeOutput.SequenceEqual(File.ReadAllBytes(first + ".docx")), "existing report remains byte-identical after rejected conflict");
            Console.WriteLine("REPORT_FILE_NAMING_PASS=" + checks + "; PRINTED=False; HARDWARE_ACCESSED=False; EVIDENCE=" + qa); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ((TargetInvocationException)ex).InnerException : ex); return 1; }
    }
}
