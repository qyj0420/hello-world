using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class ReportOutputModeSmoke
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 4) throw new ArgumentException("Expected: report-module-directory project analysis output-directory");
            string module = Path.GetFullPath(args[0]);
            string project = Path.GetFullPath(args[1]);
            string analysis = Path.GetFullPath(args[2]);
            string output = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(output);
            string runRoot = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runRoot);

            Assembly assembly = Assembly.LoadFrom(Path.Combine(module, "Xiongxin.OWTS.ReportGenerator.dll"));
            Type controlType = assembly.GetType("HVX.ReportGenerator.ReportGeneratorControl", true);
            using (Control control = (Control)Activator.CreateInstance(controlType))
            {
                controlType.GetProperty("PromptAnalysisOnLoad").SetValue(control, false, null);
                controlType.GetProperty("TemplatePath").SetValue(control,
                    Path.Combine(module, "templates", "EPFT_OWTS_Report_Template_v1.0.docx"), null);
                controlType.GetProperty("ReportEnginePath").SetValue(control,
                    Path.Combine(module, "runtime", "HVX.Report.Engine.dll"), null);
                Require((bool)controlType.GetMethod("LoadProject").Invoke(control, new object[] { project }), Read(controlType, control, "LastError"));
                Require((bool)controlType.GetMethod("LoadAnalysis").Invoke(control, new object[] { analysis }), Read(controlType, control, "LastError"));

                // This smoke targets the document/output pipeline.  The archive fixture
                // intentionally contains a synthetic matrix, so omit chart sections that
                // have their own independent rendering regressions.
                TreeView tree = (TreeView)controlType.GetField("tree", PrivateInstance).GetValue(control);
                foreach (TreeNode root in tree.Nodes) root.Checked = false;
                if (tree.Nodes.Count > 0) tree.Nodes[0].Checked = true;

                string docx = Path.Combine(runRoot, "word-only.docx");
                string pdf = Path.Combine(runRoot, "word-only.pdf");
                object plan = controlType.GetMethod("BuildGenerationPlan", PrivateInstance).Invoke(control, new object[] { docx, pdf, false });
                FieldInfo generatePdf = plan.GetType().GetField("GeneratePdf", PrivateInstance);
                Require(generatePdf != null, "GeneratePdf plan flag is missing.");
                generatePdf.SetValue(plan, false);

                object service = controlType.GetField("documentService", PrivateInstance).GetValue(control);
                var stages = new List<string>();
                Action<int, string> progress = delegate(int percent, string stage) { stages.Add(stage ?? String.Empty); };
                object result = service.GetType().GetMethod("Generate", PrivateInstance, null,
                    new[] { plan.GetType(), typeof(Action<int, string>) }, null).Invoke(service, new object[] { plan, progress });
                Type resultType = result.GetType();
                bool success = (bool)resultType.GetField("Success", PrivateInstance).GetValue(result);
                bool pdfCreated = (bool)resultType.GetField("PdfCreated", PrivateInstance).GetValue(result);
                string warning = Convert.ToString(resultType.GetField("Warning", PrivateInstance).GetValue(result));

                Require(success, Convert.ToString(resultType.GetField("Error", PrivateInstance).GetValue(result)));
                Require(File.Exists(docx) && new FileInfo(docx).Length > 0, "Word-only generation did not create a DOCX file.");
                Require(!File.Exists(pdf) && !pdfCreated, "Word-only generation unexpectedly created or reported a PDF.");
                Require(String.IsNullOrWhiteSpace(warning), "Word-only generation reported a PDF warning: " + warning);
                Require(!stages.Contains("pdf"), "Word-only generation entered the PDF conversion stage.");
                Console.WriteLine("PASS WordOnly DOCX=" + docx);
                Console.WriteLine("ProgressStages=" + String.Join(",", stages.ToArray()));

                string dualDocx = Path.Combine(runRoot, "pdf-requested.docx");
                string dualPdf = Path.Combine(runRoot, "pdf-requested.pdf");
                object dualPlan = controlType.GetMethod("BuildGenerationPlan", PrivateInstance).Invoke(control, new object[] { dualDocx, dualPdf, false });
                var dualStages = new List<string>();
                Action<int, string> dualProgress = delegate(int percent, string stage) { dualStages.Add(stage ?? String.Empty); };
                object dualResult = service.GetType().GetMethod("Generate", PrivateInstance, null,
                    new[] { dualPlan.GetType(), typeof(Action<int, string>) }, null).Invoke(service, new object[] { dualPlan, dualProgress });
                Type dualType = dualResult.GetType();
                bool dualSuccess = (bool)dualType.GetField("Success", PrivateInstance).GetValue(dualResult);
                bool dualPdfCreated = (bool)dualType.GetField("PdfCreated", PrivateInstance).GetValue(dualResult);
                string dualWarning = Convert.ToString(dualType.GetField("Warning", PrivateInstance).GetValue(dualResult));
                Require(dualSuccess && File.Exists(dualDocx), "PDF-requested generation did not create its Word source.");
                Require(dualStages.Contains("pdf"), "PDF-requested generation never entered the PDF stage.");
                Require(dualPdfCreated == File.Exists(dualPdf), "PdfCreated does not match the actual PDF file.");
                if (!dualPdfCreated) Require(!String.IsNullOrWhiteSpace(dualWarning), "PDF failure was not reported explicitly.");
                Console.WriteLine("PASS PdfTruthful Created=" + dualPdfCreated + ";PDF=" + dualPdf);
                return 0;
            }
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine((ex.InnerException ?? ex).ToString());
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    static string Read(Type type, object target, string name)
    {
        return Convert.ToString(type.GetProperty(name).GetValue(target, null));
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
