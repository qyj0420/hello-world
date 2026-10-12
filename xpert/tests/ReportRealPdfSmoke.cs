using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Uses the existing LibreOffice converter on newly generated QA documents only.
internal static class ReportRealPdfSmoke
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static string Hash(string path) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]);
            string manifest = Path.Combine(root, "case-0", "exports.json");
            var exports = Json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(manifest));
            string enginePath = Path.Combine(root, "modules", "ReportGenerator", "runtime", "HVX.Report.Engine.dll");
            var assembly = Assembly.LoadFrom(enginePath); var type = assembly.GetType("HVX.Report.Engine.ReportGenerator", true); object engine = Activator.CreateInstance(type);
            var convert = type.GetMethod("ConvertDocxToPdf"); int checks = 0;
            var selected = exports.Where(e => new[] { "zh-CN", "en", "fr", "de", "ru", "zh-TW" }.Contains((string)e["Language"])).ToList();
            if (selected.Count != 6) throw new Exception("Expected six representative locales.");
            foreach (var entry in selected)
            {
                string input = (string)entry["Docx"], output = (string)entry["Pdf"];
                if (!Path.GetFullPath(input).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(output)) throw new Exception("Use fresh files inside QA.");
                string before = Hash(input);
                var profilesBefore = Directory.GetDirectories(Path.GetTempPath(), "hvxp-*");
                string response = (string)convert.Invoke(engine, new object[] { Json.Serialize(new { input_docx_path = input, output_pdf_path = output, overwrite = false, soffice_path = args[1], timeout_ms = 120000 }) });
                var reply = Json.Deserialize<Dictionary<string, object>>(response);
                if (!(bool)reply["ok"] || !File.Exists(output) || new FileInfo(output).Length < 1000) throw new Exception("Real PDF conversion failed: " + response);
                checks++;
                if (Hash(input) != before || before != (string)entry["DocxSHA256"]) throw new Exception("Conversion changed QA Word source.");
                checks++;
                if (Directory.GetDirectories(Path.GetDirectoryName(output), ".hvx_pdf_*").Length != 0) throw new Exception("Isolated converter staging/profile was not cleaned.");
                checks++;
                if (Directory.GetDirectories(Path.GetTempPath(), "hvxp-*").Except(profilesBefore, StringComparer.OrdinalIgnoreCase).Any()) throw new Exception("Short isolated converter profile was not cleaned.");
                checks++;
                entry["PdfSHA256"] = Hash(output); entry["PdfBytes"] = new FileInfo(output).Length;
                Console.WriteLine("REAL_PDF_PASS=" + entry["Language"] + ";Bytes=" + entry["PdfBytes"]);
            }
            File.WriteAllText(Path.Combine(root, "real-pdf-exports.json"), Json.Serialize(selected), new UTF8Encoding(false));
            Console.WriteLine("REPORT_REAL_PDF_PASS=" + checks + ";ExistingLibreOffice=True;OperatorOfficeProfile=False;HardwareAccess=False");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ((TargetInvocationException)ex).InnerException : ex); return 1; }
    }
}
