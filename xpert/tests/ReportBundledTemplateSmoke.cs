using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;

// Runs the real report control/engine from the distribution, not a substitute
// engine or a generated test template. Writes only a new synthetic QA project.
internal static class ReportBundledTemplateSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, Hidden).GetValue(owner); }
    static object Call(object owner, string name, params object[] args)
    { return owner.GetType().GetMethod(name, Hidden | BindingFlags.Public).Invoke(owner, args); }
    static string Text(Control control, string property) { return Convert.ToString(control.GetType().GetProperty(property).GetValue(control, null)); }
    static void Set(Control control, string property, object value) { control.GetType().GetProperty(property).SetValue(control, value, null); }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            string release = Path.GetFullPath(args[0]);
            string qa = Path.Combine(Path.GetFullPath(args[1]), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            string module = Path.Combine(release, "modules", "ReportGenerator");
            string bundled = Path.Combine(module, "templates", "EPFT_OWTS_Report_Template_v1.0.docx");
            string engine = Path.Combine(module, "runtime", "HVX.Report.Engine.dll");
            Check(File.Exists(bundled), "Distribution must include the genuine default template.");
            Check(File.Exists(engine), "Distribution must include the report engine.");
            byte[] templateBefore = File.ReadAllBytes(bundled);
            ValidateDocx(bundled, false);
            string project = Path.Combine(qa, "SyntheticProject"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "ITEM.in"), "电缆长度\t414.8\r\n相电压\t8.7\r\n始端地点\tSmokeStart\r\n终端地点\tSmokeEnd\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(project, "CABLE.in"), "0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t接头1\t户内冷缩\t80\r\n1\t终端\t户内冷缩\t414.8\r\n", Encoding.UTF8);
            var before = Directory.GetFiles(project).ToDictionary(Path.GetFileName, File.ReadAllBytes);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(module, "Xiongxin.OWTS.ReportGenerator.dll"));
            using (var control = (Control)Activator.CreateInstance(assembly.GetType("HVX.ReportGenerator.ReportGeneratorControl", true)))
            {
                // Do not explicitly set the template or engine here: the user's
                // failing scenario depends on normal distribution discovery.
                Check(String.Equals(Text(control, "TemplatePath"), bundled, StringComparison.OrdinalIgnoreCase), "Default template resolves inside the report module.");
                Check(String.IsNullOrEmpty(Text(control, "ReportEnginePath")), "Exercise default engine discovery.");
                Set(control, "PromptAnalysisOnLoad", false);
                Check((bool)Call(control, "LoadProject", project), "Synthetic project loads: " + Text(control, "LastError"));
                var matrix = new double[2, 36];
                for (int i = 0; i < 2; i++)
                { matrix[i, 0] = 1; matrix[i, 1] = i + 1; matrix[i, 2] = 8.7; matrix[i, 13] = 80; matrix[i, 15] = 100 + i * 20; matrix[i, 16] = 110 + i * 20; matrix[i, 22] = .95; matrix[i, 35] = 1; }
                Call(control, "SetLocationMatrix", matrix);
                Set(control, "ConclusionText", "Synthetic report regression only; not a field conclusion.");
                var tree = (TreeView)Field(control, "tree");
                foreach (TreeNode node in tree.Nodes) node.Checked = false;
                tree.Nodes[0].Checked = true;

                string generated = Path.Combine(qa, "bundled.docx");
                Run(control, generated, true);
                ValidateDocx(generated, true);
                // Repeat verifies the packaged engine remains usable in one process.
                Run(control, Path.Combine(qa, "bundled-repeat.docx"), true);

                string missing = Path.Combine(qa, "operator-selected-missing.docx");
                Set(control, "TemplatePath", missing);
                Run(control, Path.Combine(qa, "must-not-exist-missing.docx"), false);
                Check(Text(control, "TemplatePath") == missing, "Explicit missing user template is not silently replaced.");

                string corrupt = Path.Combine(qa, "corrupt.docx"); File.WriteAllText(corrupt, "not a Word package", Encoding.UTF8);
                Set(control, "TemplatePath", corrupt);
                Run(control, Path.Combine(qa, "must-not-exist-corrupt.docx"), false);
                Check(File.ReadAllText(corrupt, Encoding.UTF8) == "not a Word package", "Invalid custom template remains unchanged.");

                string unsupported = Path.Combine(qa, "operator-template.html"); File.WriteAllText(unsupported, "<html>not an accepted Word template</html>");
                Set(control, "TemplatePath", unsupported);
                Run(control, Path.Combine(qa, "must-not-exist-extension.docx"), false);

                string custom = Path.Combine(qa, "operator-selected.docx"); File.Copy(bundled, custom);
                byte[] customBefore = File.ReadAllBytes(custom);
                Set(control, "TemplatePath", custom);
                Run(control, Path.Combine(qa, "custom.docx"), true);
                Check(Text(control, "TemplatePath") == custom, "Valid custom template selection is preserved.");
                Check(customBefore.SequenceEqual(File.ReadAllBytes(custom)), "Custom template bytes are unchanged.");
            }
            Check(templateBefore.SequenceEqual(File.ReadAllBytes(bundled)), "Bundled template bytes are unchanged.");
            Check(before.Count == Directory.GetFiles(project).Length, "No project file is added or deleted.");
            foreach (var file in before) Check(file.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(project, file.Key))), "Source project unchanged: " + file.Key);
            Console.WriteLine("REPORT_BUNDLED_TEMPLATE_PASS=" + checks);
            Console.WriteLine("WORD_ONLY_VERIFIED=True;PDF_CONVERSION_NOT_TESTED=True;HARDWARE_ACCESSED=False");
            Console.WriteLine("EVIDENCE=" + qa);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ((TargetInvocationException)ex).InnerException : ex); return 1; }
    }

    static void Run(Control control, string output, bool expectedSuccess)
    {
        object plan = Call(control, "BuildGenerationPlan", output, Path.ChangeExtension(output, ".pdf"), false);
        Check((string)Field(plan, "TemplatePath") == Text(control, "TemplatePath"), "Generation plan retains selected template.");
        plan.GetType().GetField("GeneratePdf", Hidden).SetValue(plan, false);
        object service = Field(control, "documentService");
        var stages = new List<string>();
        Action<int, string> progress = delegate(int percent, string stage) { stages.Add(stage); };
        object result = service.GetType().GetMethod("Generate", Hidden, null, new[] { plan.GetType(), typeof(Action<int, string>) }, null).Invoke(service, new object[] { plan, progress });
        bool success = (bool)Field(result, "Success");
        Check(success == expectedSuccess, "Generation result: " + output + ": " + Field(result, "Error"));
        if (expectedSuccess)
        {
            Check(File.Exists(output) && new FileInfo(output).Length > 0, "Real DOCX output is present.");
            Check(stages.Contains("docx") && stages.Last() == "complete", "Real document pipeline completed.");
        }
        else
        {
            Check(!String.IsNullOrWhiteSpace(Convert.ToString(Field(result, "Error"))), "Invalid template reports a concrete error.");
            Check(!File.Exists(output), "Invalid template does not leave a report output.");
        }
        Check(!(bool)Field(result, "PdfCreated") && !File.Exists(Path.ChangeExtension(output, ".pdf")) && !stages.Contains("pdf"), "Word-only run does not claim or invoke PDF conversion.");
    }

    static void ValidateDocx(string path, bool generated)
    {
        using (var zip = ZipFile.OpenRead(path))
        {
            Check(zip.GetEntry("[Content_Types].xml") != null && zip.GetEntry("_rels/.rels") != null, "Valid Word package structure.");
            ZipArchiveEntry entry = zip.GetEntry("word/document.xml"); Check(entry != null, "Word document XML exists.");
            using (var stream = entry.Open())
            {
                XDocument xml = XDocument.Load(stream);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                Check(xml.Root != null && xml.Root.Name == w + "document", "Document XML is well formed.");
                if (generated)
                {
                    string text = String.Concat(xml.Descendants(w + "t").Select(e => e.Value));
                    Check(text.Contains("SmokeStart") && text.Contains("SmokeEnd"), "Genuine template fields replaced with synthetic project values.");
                    Check(!text.Contains("{{project.start_name}}") && !text.Contains("{{project.end_name}}"), "Project placeholders are resolved.");
                    Check(zip.Entries.Any(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)), "Cable topology image is embedded.");
                }
            }
        }
    }
}
