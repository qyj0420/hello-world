using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HVX.ReportGenerator
{
    // Remove an explicitly empty appendix from a private template copy, before
    // scalar replacement loses its semantic markers. Never edit the retained template.
    internal static class ReportOptionalAppendix
    {
        static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static string Text(XElement element) { return string.Concat(element.Descendants(W + "t").Select(t => t.Value)); }
        static bool Marker(XElement element, string name)
        { return Regex.IsMatch(Text(element), @"\{\{\s*" + Regex.Escape(name) + @"\s*\}\}", RegexOptions.IgnoreCase); }
        internal static string CreateTemplateCopy(string source, IList<string> temporaryFiles)
        {
            HVX.Report.Security.BoundedDocx.ValidateFile(source);
            string directory = Path.Combine(Path.GetTempPath(), "HVXReportOptional-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "report-template" + Path.GetExtension(source));
            temporaryFiles.Add(target);
            File.Copy(source, target, false);
            using (var archive = ZipFile.Open(target, ZipArchiveMode.Update))
            {
                HVX.Report.Security.BoundedDocx.ValidateArchive(archive);
                var entry = archive.GetEntry("word/document.xml");
                if (entry == null) throw new InvalidDataException("Report template has no document XML.");
                XDocument document = HVX.Report.Security.BoundedDocx.LoadXml(archive, "word/document.xml", LoadOptions.None);
                RemoveEmptyAppendix(document);
                entry.Delete();
                using (var stream = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal).Open()) document.Save(stream);
            }
            return target;
        }
        internal static void RemoveEmptyAppendix(XDocument document)
        {
            var body = document.Root == null ? null : document.Root.Element(W + "body");
            if (body == null) throw new InvalidDataException("Report template has no document body.");
            // The field-map tail can contain a second anchor inside a table; it is
            // documentation, not the optional section, and remains the engine's job.
            var anchors = body.Elements(W + "p").Where(p => Marker(p, "dynamic:attachment_sections")).ToArray();
            var removedBookmarks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var anchor in anchors)
            {
                var heading = anchor.ElementsBeforeSelf().LastOrDefault(p => p.Name == W + "p" && Marker(p, "label.appendix_figures"));
                if (heading == null)
                {
                    // A custom template with only the anchor has no chapter to omit.
                    anchor.Remove(); continue;
                }
                var elements = heading.ElementsAfterSelf().TakeWhile(e => e != anchor).ToList();
                elements.Insert(0, heading); elements.Add(anchor);
                if (elements.Any(e => e.Name == W + "sectPr" || e.Descendants(W + "sectPr").Any()))
                    throw new InvalidDataException("The optional appendix crosses a Word section boundary; adjust the template before omitting it.");
                foreach (var bookmark in elements.SelectMany(e => e.Descendants(W + "bookmarkStart")))
                { string name = (string)bookmark.Attribute(W + "name"); if (!string.IsNullOrEmpty(name)) removedBookmarks.Add(name); }
                // A separate preceding page-break paragraph belongs to this removed
                // chapter. Do not remove a break embedded with preceding real content.
                var prior = heading.PreviousNode as XElement;
                while (prior != null && IsEmptyPagination(prior))
                { var previous = prior.PreviousNode as XElement; prior.Remove(); prior = previous; }
                foreach (var element in elements) element.Remove();
            }
            foreach (var paragraph in body.Descendants(W + "p").ToArray())
            {
                string style = (string)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
                bool toc = style.StartsWith("TOC", StringComparison.OrdinalIgnoreCase);
                bool target = paragraph.Descendants(W + "hyperlink").Any(e => removedBookmarks.Contains((string)e.Attribute(W + "anchor") ?? ""));
                string instructions = string.Join(" ", paragraph.Descendants(W + "instrText").Select(e => e.Value)
                    .Concat(paragraph.Descendants(W + "fldSimple").Select(e => (string)e.Attribute(W + "instr") ?? "")));
                bool reference = removedBookmarks.Any(name => Regex.IsMatch(instructions, @"\b(?:PAGE)?REF\s+" + Regex.Escape(name) + @"(?:\s|$)", RegexOptions.IgnoreCase));
                if (toc && (target || reference || Marker(paragraph, "label.appendix_figures"))) { paragraph.Remove(); continue; }
                if (target || reference) throw new InvalidDataException("A custom template cross-reference points to the omitted appendix. Remove that reference or select a valid appendix figure.");
            }
        }
        static bool IsEmptyPagination(XElement paragraph)
        {
            return paragraph.Name == W + "p" && string.IsNullOrWhiteSpace(Text(paragraph)) &&
                !paragraph.Descendants(W + "sectPr").Any() && !paragraph.Descendants(W + "drawing").Any() &&
                !paragraph.Descendants(W + "pict").Any() && !paragraph.Descendants(W + "bookmarkStart").Any() &&
                !paragraph.Descendants(W + "fldChar").Any() && !paragraph.Descendants(W + "instrText").Any() &&
                (paragraph.Descendants(W + "br").Any(e => (string)e.Attribute(W + "type") == "page") ||
                 paragraph.Element(W + "pPr")?.Element(W + "pageBreakBefore") != null);
        }
    }
}
