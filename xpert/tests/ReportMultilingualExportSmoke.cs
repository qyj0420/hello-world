using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;

internal static class ReportMultilingualExportSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Field(object item, string name) { return item.GetType().GetField(name, All).GetValue(item); }
    static void Set(object item, string name, object value) { item.GetType().GetProperty(name, All).SetValue(item, value, null); }
    static object Call(object item, string name, params object[] values)
    { return item.GetType().GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(item, values); }
    static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    static string WordText(string path)
    {
        using (var zip = ZipFile.OpenRead(path))
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            return string.Join("\n", zip.Entries.Where(e => e.FullName.StartsWith("word/") && e.FullName.EndsWith(".xml")).Select(e => {
                using (var stream = e.Open()) return string.Concat(XDocument.Load(stream).Descendants(w + "t").Select(t => t.Value));
            }));
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string build = Path.GetFullPath(args[0]), qa = Path.GetFullPath(args[1]); Directory.CreateDirectory(qa);
            string project = Path.Combine(qa, "synthetic-project"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "ITEM.in"), "[ITEM]\r\n始端地点=QA Start\r\n终端地点=QA End\r\n电缆长度=414.8\r\n相电压=8.7\r\n电缆芯数=一体三芯\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(project, "CABLE.in"), "0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t接头1\t户内冷缩\t80\r\n1\t终端\t户内冷缩\t414.8\r\n", Encoding.UTF8);
            var originals = Directory.GetFiles(project).ToDictionary(p => p, Hash);
            string moduleFolder = Path.Combine(build, "modules", "ReportGenerator");
            string template = Path.Combine(moduleFolder, "templates", "EPFT_OWTS_Report_Template_v1.0.docx"); string templateHash = Hash(template);
            var module = Assembly.LoadFrom(Path.Combine(moduleFolder, "Xiongxin.OWTS.ReportGenerator.dll"));
            var languages = module.GetType("HVX.ReportGenerator.ReportLanguages", true);
            string[] codes = (string[])languages.GetField("Codes", All).GetValue(null);
            var getText = languages.GetMethod("GetText", All);
            var exports = new List<Dictionary<string, object>>();
            using (var control = (Control)Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGeneratorControl", true)))
            {
                Set(control, "PromptAnalysisOnLoad", false); Check((bool)Call(control, "LoadProject", project), "synthetic project loads");
                Set(control, "ReportEnginePath", Path.Combine(moduleFolder, "runtime", "HVX.Report.Engine.dll"));
                var matrix = new double[3, 36];
                for (int r = 0; r < 3; r++) { matrix[r, 0] = r + 1; matrix[r, 1] = r + 1; matrix[r, 2] = 8.7; matrix[r, 5] = 414.8; matrix[r, 13] = 80; matrix[r, 15] = 120 + r; matrix[r, 16] = 120 + r; matrix[r, 35] = 1; }
                Call(control, "SetLocationMatrix", matrix);
                var tree = (TreeView)Field(control, "tree"); foreach (TreeNode node in tree.Nodes) node.Checked = false; tree.Nodes[0].Checked = true;
                var diagnostic = new List<string>(); Set(control, "DiagnosticSink", new Action<string>(diagnostic.Add));
                foreach (int lang in new[] { 0, 1, 18 })
                {
                    Set(control, "LanguageIndex", lang);
                    string message = (string)Call(control, "ReportOutputMessage", "System.IO.IOException: private implementation detail", "报告未完成，请重试。", "The report was not completed. Please retry.");
                    Check(!message.Contains("System.") && !message.Contains("private"), "operator error omits implementation details " + lang);
                    Check(message.Contains(lang == 1 ? "report" : "報告") || lang == 0 && message.Contains("报告"), "operator error uses current language " + lang);
                    Check(diagnostic.Last().Contains("System.IO.IOException"), "full diagnostic retained " + lang);
                }
                Set(control, "DiagnosticSink", new Action<string>(message => { throw new IOException("QA diagnostic sink failure"); }));
                Check((string)Call(control, "ReportOutputMessage", "details", "已保留。", "Retained.") == "已保留。", "diagnostic failure cannot replace operator outcome");
                Set(control, "DiagnosticSink", new Action<string>(diagnostic.Add));
                foreach (int lang in Enumerable.Range(0, codes.Length))
                {
                    Set(control, "LanguageIndex", lang); Set(control, "ConclusionText", "Synthetic verification only. No field assessment.");
                    string docx = Path.Combine(qa, "report-" + codes[lang] + ".docx"), pdf = Path.ChangeExtension(docx, ".pdf");
                    object plan = Call(control, "BuildGenerationPlan", docx, pdf, false);
                    plan.GetType().GetField("GeneratePdf", All).SetValue(plan, false);
                    var values = (Dictionary<string, string>)Field(plan, "Values");
                    Check(!values["label.generated_by"].Contains("EPFT"), "neutral generated-by label " + codes[lang]);
                    Check(values["cable.length"].Contains("414.8") && values["project.phase_voltage"].Contains("8.7"), "language preserves synthetic measurement values " + codes[lang]);
                    object result = Call(Field(control, "documentService"), "Generate", plan);
                    Check((bool)Field(result, "Success"), "actual DOCX export " + codes[lang] + ": " + Field(result, "Error"));
                    string text = WordText(docx), title = (string)getText.Invoke(null, new object[] { lang, "ReportTitle" });
                    Check(text.Contains(title), "exported report title follows selected language " + codes[lang]);
                    Check(!text.Contains("EPFT"), "built-in template rendered text is neutral " + codes[lang]);
                    Check(text.Contains("QA Start") && text.Contains("QA End") && text.Contains("414.8"), "export contains source metadata " + codes[lang]);
                    Check(!text.Contains("{{report.") && !text.Contains("{{label.") && !text.Contains("{{project."), "report scalar labels resolved " + codes[lang]);
                    Check(!(bool)Field(result, "PdfCreated"), "word-only path does not claim a PDF " + codes[lang]);
                    exports.Add(new Dictionary<string, object> { { "Language", codes[lang] }, { "Docx", docx }, { "Pdf", pdf }, { "Title", title }, { "DocxSHA256", Hash(docx) } });
                }
                // The service rejects template/PDF and DOCX/PDF aliases before
                // invoking a converter; none of these cases launches Office.
                foreach (bool aliasTemplate in new[] { false, true })
                {
                    string docx = Path.Combine(qa, aliasTemplate ? "alias-template.docx" : "alias-docx.docx");
                    object plan = Call(control, "BuildGenerationPlan", docx, aliasTemplate ? template : docx, true);
                    object result = Call(Field(control, "documentService"), "Generate", plan);
                    Check(!(bool)Field(result, "Success") && !File.Exists(docx), "service rejects PDF/input alias before generating Word");
                    Check(Hash(template) == templateHash, "source template retained after invalid output plan");
                }
                string custom = Path.Combine(qa, "customer-template.docx"); File.Copy(template, custom);
                string customHash = Hash(custom); Set(control, "TemplatePath", custom);
                string customOutput = Path.Combine(qa, "customer-output.docx");
                object customPlan = Call(control, "BuildGenerationPlan", customOutput, Path.ChangeExtension(customOutput, ".pdf"), false);
                customPlan.GetType().GetField("GeneratePdf", All).SetValue(customPlan, false);
                object customResult = Call(Field(control, "documentService"), "Generate", customPlan);
                Check((bool)Field(customResult, "Success") && WordText(customOutput).Contains("EPFT"), "separately selected user template header is not rebranded");
                Check(Hash(custom) == customHash, "user template source is retained byte-for-byte");
            }
            Check(Hash(template) == templateHash, "template never changed across all languages");
            Check(originals.All(pair => Hash(pair.Key) == pair.Value) && Directory.GetFiles(project).Length == originals.Count, "synthetic project preserved");
            File.WriteAllText(Path.Combine(qa, "exports.json"), Json.Serialize(exports), new UTF8Encoding(false));
            Console.WriteLine("REPORT_MULTILINGUAL_EXPORT_PASS=" + checks + ";Languages=" + codes.Length + ";RealWordExport=True;PDFConversionInSeparateStage=True;RealUserProject=False");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ((TargetInvocationException)ex).InnerException : ex); return 1; }
    }
}
