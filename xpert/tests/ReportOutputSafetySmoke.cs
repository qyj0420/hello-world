using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using HVX.Report.Engine;

internal static class ReportOutputSafetySmoke
{
    static int checks;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static bool Ok(string value) { return (bool)Json.Deserialize<Dictionary<string, object>>(value)["ok"]; }
    static int Main(string[] args)
    {
        try
        {
            string qa = Path.Combine(args[0], "output-safety-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            string input = Path.Combine(qa, "retained.docx"); File.Copy(args[1], input);
            byte[] original = File.ReadAllBytes(input);
            var engine = new ReportGenerator();
            string response = engine.ConvertDocxToPdf(Json.Serialize(new { input_docx_path = input, output_pdf_path = input, overwrite = true, soffice_path = args[2] }));
            bool changed = !original.SequenceEqual(File.ReadAllBytes(input));
            if (args.Length > 3 && args[3] == "baseline")
            {
                Check(Ok(response) && changed, "baseline must prove converter replaced its own DOCX input");
                Console.WriteLine("BASELINE_INPUT_OVERWRITTEN=True;SyntheticOnly=True;Evidence=" + qa);
                return 0;
            }
            Check(!Ok(response) && !changed, "PDF conversion rejects identical input/output before converter and retains input");
            response = engine.GenerateReport(Json.Serialize(new { template_path = input, output_docx_path = input, overwrite = true }));
            Check(!Ok(response) && original.SequenceEqual(File.ReadAllBytes(input)), "existing DOCX same-template protection remains");
            var helper = typeof(ReportGenerator).Assembly.GetType("HVX.Report.Security.ReportOutputSafety", true);
            var publish = helper.GetMethod("Publish", BindingFlags.Static | BindingFlags.NonPublic);
            string destination = Path.Combine(qa, "previous.docx"), temporary = Path.Combine(qa, "staged.docx");
            byte[] previous = Encoding.UTF8.GetBytes("previous retained output"); File.WriteAllBytes(destination, previous);
            bool failed = false;
            try { publish.Invoke(null, new object[] { temporary, destination, true }); } catch (TargetInvocationException) { failed = true; }
            Check(failed && previous.SequenceEqual(File.ReadAllBytes(destination)), "missing staged output cannot delete previous report");
            File.WriteAllText(temporary, "new completed output"); failed = false;
            try { publish.Invoke(null, new object[] { temporary, destination, false }); } catch (TargetInvocationException) { failed = true; }
            Check(failed && previous.SequenceEqual(File.ReadAllBytes(destination)) && File.Exists(temporary), "no-overwrite retains both files");
            using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
            { failed = false; try { publish.Invoke(null, new object[] { temporary, destination, true }); } catch (TargetInvocationException) { failed = true; } Check(failed, "locked destination reports publication failure"); }
            Check(previous.SequenceEqual(File.ReadAllBytes(destination)) && File.Exists(temporary), "failed atomic replace retains old report and completed staging");
            publish.Invoke(null, new object[] { temporary, destination, true });
            Check(File.ReadAllText(destination) == "new completed output" && !File.Exists(temporary), "successful publication replaces complete output atomically");
            foreach (string name in new[] { "invalid", "truncated", "ok" })
            {
                string docx = Path.Combine(qa, name + ".docx"), pdf = Path.Combine(qa, name + ".pdf"); File.Copy(input, docx); File.WriteAllBytes(pdf, previous);
                response = engine.ConvertDocxToPdf(Json.Serialize(new { input_docx_path = docx, output_pdf_path = pdf, overwrite = true, soffice_path = args[2] }));
                Check(Ok(response) == (name == "ok"), "converter output envelope status " + name);
                Check(name == "ok" ? File.ReadAllText(pdf).StartsWith("%PDF-") : previous.SequenceEqual(File.ReadAllBytes(pdf)), "invalid/truncated converter bytes never replace prior output " + name);
                Check(original.SequenceEqual(File.ReadAllBytes(docx)), "converter retains input " + name);
            }
            Console.WriteLine("REPORT_OUTPUT_SAFETY_PASS=" + checks + ";SyntheticOnly=True;PDFRenderingClaim=False;Evidence=" + qa);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
