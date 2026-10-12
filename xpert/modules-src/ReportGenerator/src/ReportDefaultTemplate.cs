using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace HVX.ReportGenerator
{
    internal static class ReportDefaultTemplate
    {
        // This is a compatibility adjustment for our bundled template only.
        // A separately selected template belongs to its author and is not rebranded.
        internal static string Prepare(string source, IList<string> temporaryFiles)
        {
            string bundled = Path.Combine(Path.GetDirectoryName(typeof(ReportDefaultTemplate).Assembly.Location), "templates", "EPFT_OWTS_Report_Template_v1.0.docx");
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(bundled), StringComparison.OrdinalIgnoreCase)) return source;
            HVX.Report.Security.BoundedDocx.ValidateFile(source);
            string directory = Path.Combine(Path.GetTempPath(), "HVXReportDefault-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "report-template.docx"); temporaryFiles.Add(target); File.Copy(source, target, false);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            using (var archive = ZipFile.Open(target, ZipArchiveMode.Update))
            {
                foreach (var name in archive.Entries.Where(e => e.FullName.StartsWith("word/header", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).Select(e => e.FullName).ToArray())
                {
                    var document = HVX.Report.Security.BoundedDocx.LoadXml(archive, name, LoadOptions.None); bool changed = false;
                    foreach (var text in document.Descendants(w + "t"))
                    {
                        if (text.Value != "EPFT  |  OWTS CABLE DIAGNOSTICS") continue;
                        text.Value = "{{report.header}}"; changed = true;
                    }
                    if (!changed) continue;
                    archive.GetEntry(name).Delete();
                    using (var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open()) document.Save(stream);
                }
            }
            return target;
        }
    }
}
