using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace HVX.Report.Engine
{
    public sealed class ReportGenerator
    {
        private const string ApiVersion = "hvx-report-dotnet-1.2";
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace A14 = "http://schemas.microsoft.com/office/drawing/2010/main";
        private static readonly XNamespace PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
        private static readonly Regex MarkerRegex = new Regex(@"\{\{\s*([^{}]+?)\s*\}\}|\[\[\s*([^\[\]]+?)\s*\]\]|\$\{\s*([^{}]+?)\s*\}", RegexOptions.Compiled);
        private static readonly Regex RowMarkerRegex = new Regex(@"\{\{\s*row:([A-Za-z0-9_.-]+)\.([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ImageMarkerRegex = new Regex(@"\{\{\s*image:([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DynamicMarkerRegex = new Regex(@"\{\{\s*dynamic:([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TemplateTailMarkerRegex = new Regex(@"\{\{\s*template:remove_field_map_page\s*\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        public string Health()
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = true;
            data["api_version"] = ApiVersion;
            data["runtime"] = ".NET Framework 4.8 x64";
            data["docx"] = "OOXML clone, scalar fields, content controls, label bindings, responsive report images, repeating table rows, ordered dynamic sections";
            data["image_layout_release"] = "2026-08-23-r1";
            data["image_layout"] = "page-aware fit, centered image, one image per page by default, per-image caption";
            data["pdf"] = "structural preflight plus optional LibreOffice conversion";
            data["project_folder"] = "ITEM.in, CABLE.in, CAL/DEM/LOC inventory, GBK/UTF text detection";
            data["matrix"] = "double[,] with 36-column OWTS schema";
            return SerializeAscii(data);
        }

        public string AnalyzeTemplate(string templatePath)
        {
            try
            {
                return SerializeAscii(AnalyzeTemplateInternal(templatePath, null));
            }
            catch (Exception ex)
            {
                return Error("TEMPLATE_ANALYZE_FAILED", ex);
            }
        }

        public string AnalyzeTemplateJson(string requestJson)
        {
            try
            {
                Dictionary<string, object> request = DeserializeObject(requestJson);
                string path = RequiredString(request, "template_path");
                string ocrText = OptionalString(request, "ocr_text");
                return SerializeAscii(AnalyzeTemplateInternal(path, ocrText));
            }
            catch (Exception ex)
            {
                return Error("TEMPLATE_ANALYZE_FAILED", ex);
            }
        }

        public string InspectProjectFolder(string projectPath)
        {
            try
            {
                ProjectData project = ReadProjectFolder(projectPath);
                Dictionary<string, object> reply = new Dictionary<string, object>();
                reply["ok"] = true;
                reply["api_version"] = ApiVersion;
                reply["project_path"] = project.ProjectPath;
                reply["fields"] = project.Fields;
                reply["repeat_tables"] = project.Repeats;
                reply["warnings"] = project.Warnings;
                return SerializeAscii(reply);
            }
            catch (Exception ex)
            {
                return Error("PROJECT_INSPECT_FAILED", ex);
            }
        }

        public string SummarizeLocationMatrix(double[,] locationMatrix)
        {
            try
            {
                MatrixData matrix = ReadLocationMatrix(locationMatrix);
                Dictionary<string, object> reply = new Dictionary<string, object>();
                reply["ok"] = true;
                reply["api_version"] = ApiVersion;
                reply["fields"] = matrix.Fields;
                reply["repeat_tables"] = matrix.Repeats;
                reply["warnings"] = matrix.Warnings;
                return SerializeAscii(reply);
            }
            catch (Exception ex)
            {
                return Error("MATRIX_SUMMARY_FAILED", ex);
            }
        }

        public string GenerateReport(string requestJson)
        {
            try
            {
                Dictionary<string, object> request = DeserializeObject(requestJson);
                return SerializeAscii(GenerateReportInternal(request, null));
            }
            catch (Exception ex)
            {
                return Error("REPORT_GENERATION_FAILED", ex);
            }
        }

        public string GenerateReportWithMatrix(string requestJson, double[,] locationMatrix)
        {
            try
            {
                Dictionary<string, object> request = DeserializeObject(requestJson);
                MatrixData matrix = ReadLocationMatrix(locationMatrix);
                return SerializeAscii(GenerateReportInternal(request, matrix));
            }
            catch (Exception ex)
            {
                return Error("REPORT_GENERATION_FAILED", ex);
            }
        }

        public string ConvertDocxToPdf(string requestJson)
        {
            try
            {
                Dictionary<string, object> request = DeserializeObject(requestJson);
                string input = FullExistingFile(RequiredString(request, "input_docx_path"));
                HVX.Report.Security.BoundedDocx.ValidateFile(input);
                string output = Path.GetFullPath(RequiredString(request, "output_pdf_path"));
                HVX.Report.Security.ReportOutputSafety.EnsureDifferent(input, output);
                bool overwrite = OptionalBool(request, "overwrite", false);
                int timeoutMs = OptionalInt(request, "timeout_ms", 120000);
                if(timeoutMs <= 0 || timeoutMs > 600000) throw new ArgumentOutOfRangeException("timeout_ms");
                string soffice = OptionalString(request, "soffice_path");
                if (string.IsNullOrWhiteSpace(soffice)) soffice = FindSoffice();
                if (string.IsNullOrWhiteSpace(soffice) || !File.Exists(soffice))
                    throw new InvalidOperationException("LibreOffice soffice.exe was not found. Provide soffice_path or install LibreOffice.");
                if (!string.Equals(Path.GetFileName(soffice), "soffice.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("soffice_path must point to soffice.exe.");
                if (File.Exists(output) && !overwrite) throw new IOException("Output PDF already exists and overwrite=false: " + output);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                string work = Path.Combine(Path.GetDirectoryName(output), ".hvx_pdf_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                // LibreOffice creates deeply nested profile files. A short,
                // private temp root avoids leaving them behind when the report
                // output folder itself is already near the Windows path limit.
                string profilePath = Path.Combine(Path.GetTempPath(), "hvxp-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = soffice;
                    // Isolate this conversion from an operator's running Office
                    // session and settings. The profile belongs to this staging
                    // and is removed after process completion.
                    string profile = new Uri(profilePath).AbsoluteUri;
                    psi.Arguments = "-env:UserInstallation=\"" + profile + "\" --headless --convert-to pdf --outdir \"" + work + "\" \"" + input + "\"";
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    using (Process process = Process.Start(psi))
                    {
                        // Drain both pipes concurrently with bounded diagnostics.
                        // Waiting for exit first can deadlock a verbose converter.
                        Task<string> stdout = DrainConverterOutput(process.StandardOutput);
                        Task<string> stderr = DrainConverterOutput(process.StandardError);
                        if (!process.WaitForExit(timeoutMs))
                        {
                            try { process.Kill(); } catch { }
                            process.WaitForExit(2000);
                            process.StandardOutput.Close(); process.StandardError.Close();
                            throw new TimeoutException("LibreOffice conversion timed out.");
                        }
                        if(!Task.WaitAll(new Task[]{stdout,stderr},2000)) {
                            process.StandardOutput.Close(); process.StandardError.Close();
                            throw new IOException("LibreOffice output streams did not close.");
                        }
                        if (process.ExitCode != 0)
                            throw new InvalidOperationException("LibreOffice conversion failed: " + stderr.Result);
                    }
                    string generated = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".pdf");
                    if (!File.Exists(generated)) throw new IOException("LibreOffice did not create the expected PDF.");
                    HVX.Report.Security.ReportOutputSafety.ValidatePdfEnvelope(generated);
                    HVX.Report.Security.ReportOutputSafety.Publish(generated, output, overwrite);
                }
                finally
                {
                    try { Directory.Delete(work, true); } catch { }
                    try { if (Directory.Exists(profilePath)) Directory.Delete(profilePath, true); } catch { }
                }
                Dictionary<string, object> reply = new Dictionary<string, object>();
                reply["ok"] = true;
                reply["output_pdf_path"] = output;
                reply["sha256"] = Sha256(output);
                return SerializeAscii(reply);
            }
            catch (Exception ex)
            {
                return Error("PDF_CONVERSION_FAILED", ex);
            }
        }

        private static Task<string> DrainConverterOutput(StreamReader reader)
        {
            return Task.Run(() => {
                var diagnostic = new StringBuilder(); var buffer = new char[4096];
                try { int n; while((n=reader.Read(buffer,0,buffer.Length))>0) {
                    int remaining=4096-diagnostic.Length; if(remaining>0)diagnostic.Append(buffer,0,Math.Min(n,remaining));
                } } catch(IOException) { } catch(ObjectDisposedException) { }
                return diagnostic.ToString();
            });
        }

        private static Dictionary<string, object> GenerateReportInternal(Dictionary<string, object> request, MatrixData matrix)
        {
            string projectPath = OptionalString(request, "project_path");
            ProjectData project = null;
            if (!string.IsNullOrWhiteSpace(projectPath)) project = ReadProjectFolder(projectPath);

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<Dictionary<string, string>>> repeats = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            List<string> warnings = new List<string>();
            if (project != null)
            {
                MergeStringDictionary(values, project.Fields, false);
                MergeRepeats(repeats, project.Repeats, false);
                warnings.AddRange(project.Warnings);
            }
            if (matrix != null)
            {
                MergeStringDictionary(values, matrix.Fields, true);
                MergeRepeats(repeats, matrix.Repeats, true);
                warnings.AddRange(matrix.Warnings);
            }
            Dictionary<string, object> userValues = GetDictionary(request, "values", false);
            if (userValues != null) FlattenObject(userValues, string.Empty, values, true);
            Dictionary<string, object> userRepeats = GetDictionary(request, "repeat_tables", false);
            if (userRepeats != null) MergeUserRepeats(repeats, userRepeats);

            string templatePath = OptionalString(request, "template_path");
            if (string.IsNullOrWhiteSpace(templatePath) && project != null)
            {
                string inferred;
                if (project.Fields.TryGetValue("files.template_path", out inferred)) templatePath = inferred;
            }
            if (string.IsNullOrWhiteSpace(templatePath)) throw new ArgumentException("template_path is required when the project folder has no DOCX/DOTX template.");
            templatePath = FullExistingFile(templatePath);
            string ext = Path.GetExtension(templatePath).ToLowerInvariant();
            if (ext != ".docx" && ext != ".dotx") throw new NotSupportedException("Report generation requires a DOCX or DOTX template. Analyze PDF first, then create a calibrated DOCX/DOTX template.");
            HVX.Report.Security.BoundedDocx.ValidateFile(templatePath);
            string output = Path.GetFullPath(RequiredString(request, "output_docx_path"));
            HVX.Report.Security.ReportOutputSafety.EnsureDifferent(templatePath, output);
            bool overwrite = OptionalBool(request, "overwrite", false);
            if (File.Exists(output) && !overwrite) throw new IOException("Output DOCX already exists and overwrite=false: " + output);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            string temp = Path.Combine(Path.GetDirectoryName(output), ".hvx_report_" + Guid.NewGuid().ToString("N") + ".docx");
            File.Copy(templatePath, temp, true);

            Dictionary<string, List<string>> bindings = DefaultBindings();
            Dictionary<string, object> userBindings = GetDictionary(request, "bindings", false);
            if (userBindings != null) MergeBindings(bindings, userBindings);
            Dictionary<string, object> images = GetDictionary(request, "images", false) ?? new Dictionary<string, object>();
            Dictionary<string, object> imageLayout = GetDictionary(request, "image_layout", false) ?? new Dictionary<string, object>();
            List<DynamicSectionSpec> dynamicSections = ParseDynamicSections(request, images);
            int replaced = 0;
            int labelsFilled = 0;
            int repeatedRows = 0;
            int imagesInserted = 0;
            int dynamicSectionsInserted = 0;
            List<string> unresolved;
            try
            {
                using (ZipArchive archive = ZipFile.Open(temp, ZipArchiveMode.Update))
                {
                    HVX.Report.Security.BoundedDocx.ValidateArchive(archive);
                    PatchTemplateContentType(archive);
                    PatchImageQualitySettings(archive);
                    ImageManager imageManager = new ImageManager(archive, images, imageLayout);
                    string[] partNames = archive.Entries.Select(e => e.FullName).Where(IsTextPart).ToArray();
                    for (int i = 0; i < partNames.Length; i++)
                    {
                        XDocument xml = LoadXml(archive, partNames[i]);
                        bool main = string.Equals(partNames[i], "word/document.xml", StringComparison.OrdinalIgnoreCase);
                        if (main)
                        {
                            NormalizeStandalonePageBreakParagraphs(xml);
                            RemoveTemplateOnlyTail(xml);
                            repeatedRows += ExpandRepeatRows(xml, repeats);
                            labelsFilled += FillBoundLabels(xml, values, bindings);
                            dynamicSectionsInserted += ExpandDynamicSections(xml, dynamicSections);
                            imagesInserted += imageManager.InsertImages(xml);
                        }
                        replaced += FillContentControls(xml, values);
                        replaced += ReplaceScalarMarkers(xml, values);
                        SaveXml(archive, partNames[i], xml);
                    }
                    imageManager.Save();
                    unresolved = FindUnresolvedMarkers(archive);
                }
                HVX.Report.Security.ReportOutputSafety.Publish(temp, output, overwrite);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }

            Dictionary<string, object> reply = new Dictionary<string, object>();
            reply["ok"] = true;
            reply["api_version"] = ApiVersion;
            reply["template_path"] = templatePath;
            reply["output_docx_path"] = output;
            reply["sha256"] = Sha256(output);
            reply["field_count"] = values.Count;
            reply["scalar_replacements"] = replaced;
            reply["label_fields_filled"] = labelsFilled;
            reply["repeated_rows_created"] = repeatedRows;
            reply["images_inserted"] = imagesInserted;
            reply["dynamic_sections_inserted"] = dynamicSectionsInserted;
            reply["image_layout_release"] = "2026-08-23-r1";
            reply["image_layout_defaults"] = "centered, page-aware fit, one image per page, caption from filename unless caption is supplied";
            reply["unresolved_placeholders"] = unresolved;
            reply["warnings"] = warnings;
            return reply;
        }

        private static Dictionary<string, object> AnalyzeTemplateInternal(string templatePath, string ocrText)
        {
            string path = FullExistingFile(templatePath);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".docx" || ext == ".dotx") return AnalyzeDocx(path);
            if (ext == ".pdf") return AnalyzePdf(path, ocrText);
            throw new NotSupportedException("Supported template types are DOCX, DOTX and PDF.");
        }

        private static Dictionary<string, object> AnalyzeDocx(string path)
        {
            if (new FileInfo(path).Length > HVX.Report.Security.BoundedDocx.MaxPackageBytes)
                throw new InvalidDataException("DOCX resource limit exceeded: compressed package.");
            HashSet<string> placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> controls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> labels = new List<Dictionary<string, object>>();
            int paragraphs = 0, tables = 0, images = 0, sections = 0;
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                HVX.Report.Security.BoundedDocx.ValidateArchive(archive);
                string[] partNames = archive.Entries.Select(e => e.FullName).Where(IsTextPart).ToArray();
                foreach (string partName in partNames)
                {
                    XDocument xml = LoadXml(archive, partName);
                    foreach (XElement paragraph in xml.Descendants(W + "p"))
                    {
                        paragraphs++;
                        string text = ElementText(paragraph);
                        foreach (Match match in MarkerRegex.Matches(text))
                        {
                            string marker = FirstGroup(match);
                            if (!string.IsNullOrWhiteSpace(marker)) placeholders.Add(marker.Trim());
                        }
                        AddParagraphLabelCandidate(labels, partName, text);
                    }
                    tables += xml.Descendants(W + "tbl").Count();
                    images += xml.Descendants(W + "drawing").Count() + xml.Descendants(W + "pict").Count();
                    sections += xml.Descendants(W + "sectPr").Count();
                    foreach (XElement tag in xml.Descendants(W + "tag"))
                    {
                        XAttribute value = tag.Attribute(W + "val");
                        if (value != null && !string.IsNullOrWhiteSpace(value.Value)) controls.Add(value.Value.Trim());
                    }
                    foreach (XElement row in xml.Descendants(W + "tr")) AddTableLabelCandidates(labels, partName, row);
                }
            }
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["ok"] = true;
            result["api_version"] = ApiVersion;
            result["template_path"] = path;
            result["format"] = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            result["sha256"] = Sha256(path);
            result["paragraph_count"] = paragraphs;
            result["table_count"] = tables;
            result["image_count"] = images;
            result["section_count"] = sections;
            result["placeholders"] = placeholders.OrderBy(s => s).ToArray();
            result["content_control_tags"] = controls.OrderBy(s => s).ToArray();
            result["label_candidates"] = labels.Take(500).ToArray();
            result["generation_supported"] = true;
            result["recommended_mode"] = placeholders.Count > 0 || controls.Count > 0 ? "marker_or_content_control" : "label_binding_first_time_calibration";
            return result;
        }

        private static Dictionary<string, object> AnalyzePdf(string path, string ocrText)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string raw = Encoding.GetEncoding(28591).GetString(bytes);
            int pages = Regex.Matches(raw, @"/Type\s*/Page(?!s)").Count;
            int imageObjects = Regex.Matches(raw, @"/Subtype\s*/Image").Count;
            int fontObjects = Regex.Matches(raw, @"/Type\s*/Font").Count;
            int textOperators = Regex.Matches(raw, @"\b(Tj|TJ)\b").Count;
            bool acroForm = raw.IndexOf("/AcroForm", StringComparison.Ordinal) >= 0;
            bool xfa = raw.IndexOf("/XFA", StringComparison.Ordinal) >= 0;
            bool encrypted = raw.IndexOf("/Encrypt", StringComparison.Ordinal) >= 0;
            List<string> rawStrings = new List<string>();
            foreach (Match match in Regex.Matches(raw, @"\((?<s>(?:\\.|[^\\)]){2,160})\)\s*Tj"))
            {
                string value = DecodePdfLiteral(match.Groups["s"].Value);
                if (!string.IsNullOrWhiteSpace(value)) rawStrings.Add(value.Trim());
                if (rawStrings.Count >= 300) break;
            }
            if (!string.IsNullOrWhiteSpace(ocrText)) rawStrings.AddRange(SplitUsefulLines(ocrText));
            bool likelyScanned = pages > 0 && imageObjects >= pages && textOperators < pages;
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["ok"] = true;
            result["api_version"] = ApiVersion;
            result["template_path"] = path;
            result["format"] = "pdf";
            result["sha256"] = Sha256(path);
            result["page_count_estimate"] = pages;
            result["has_acroform"] = acroForm;
            result["has_xfa"] = xfa;
            result["encrypted"] = encrypted;
            result["image_object_count"] = imageObjects;
            result["font_object_count"] = fontObjects;
            result["text_operator_count"] = textOperators;
            result["likely_scanned"] = likelyScanned;
            result["raw_text_samples"] = rawStrings.Take(100).ToArray();
            result["generation_supported"] = false;
            result["recommended_mode"] = acroForm ? "pdf_form_field_mapping" : (likelyScanned ? "ocr_then_calibrated_docx_template" : "extract_layout_then_calibrated_docx_template");
            result["boundary"] = "PDF is a fixed-layout delivery format. High-fidelity automatic generation requires a one-time field/coordinate profile or a DOCX/DOTX report template.";
            return result;
        }

        private static ProjectData ReadProjectFolder(string projectPath)
        {
            string root = Path.GetFullPath(projectPath);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            ProjectData result = new ProjectData(root);
            string item = Path.Combine(root, "ITEM.in");
            if (File.Exists(item)) ReadItemFile(item, result.Fields);
            else result.Warnings.Add("ITEM.in was not found.");
            string cable = Path.Combine(root, "CABLE.in");
            if (File.Exists(cable)) ReadCableFile(cable, result.Fields, result.Repeats);
            else result.Warnings.Add("CABLE.in was not found.");

            string[] calFiles = Directory.GetFiles(root, "*.cal", SearchOption.TopDirectoryOnly);
            string[] demFiles = Directory.GetFiles(root, "*.dem", SearchOption.TopDirectoryOnly);
            string[] locFiles = Directory.GetFiles(root, "*.loc", SearchOption.TopDirectoryOnly);
            string[] dotxFiles = Directory.GetFiles(root, "*.dotx", SearchOption.TopDirectoryOnly);
            string[] docxFiles = Directory.GetFiles(root, "*.docx", SearchOption.TopDirectoryOnly);
            string[] pdfFiles = Directory.GetFiles(root, "*.pdf", SearchOption.TopDirectoryOnly);
            result.Fields["files.cal_count"] = calFiles.Length.ToString(CultureInfo.InvariantCulture);
            result.Fields["files.dem_count"] = demFiles.Length.ToString(CultureInfo.InvariantCulture);
            result.Fields["files.loc_count"] = locFiles.Length.ToString(CultureInfo.InvariantCulture);
            result.Fields["files.template_path"] = dotxFiles.Concat(docxFiles).FirstOrDefault() ?? string.Empty;
            result.Fields["files.reference_pdf_path"] = pdfFiles.FirstOrDefault() ?? string.Empty;
            long totalBytes = calFiles.Concat(demFiles).Concat(locFiles).Select(f => new FileInfo(f).Length).Sum();
            result.Fields["files.measurement_bytes"] = totalBytes.ToString(CultureInfo.InvariantCulture);
            result.Fields["files.measurement_size_mb"] = (totalBytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture);

            if (calFiles.Length > 0)
            {
                Dictionary<string, string> calHeader = ReadHeader(calFiles[0], 80);
                foreach (KeyValuePair<string, string> pair in calHeader)
                    result.Fields["cal." + NormalizeFieldName(pair.Key)] = pair.Value;
            }

            List<Dictionary<string, string>> tests = new List<Dictionary<string, string>>();
            HashSet<double> voltages = new HashSet<double>();
            Dictionary<string, int> phaseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in demFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                Dictionary<string, string> record = ParseMeasurementFileName(file);
                tests.Add(record);
                string phase = record.ContainsKey("phase") ? record["phase"] : "UNKNOWN";
                phaseCounts[phase] = phaseCounts.ContainsKey(phase) ? phaseCounts[phase] + 1 : 1;
                double voltage;
                if (record.ContainsKey("voltage_kv") && double.TryParse(record["voltage_kv"], NumberStyles.Float, CultureInfo.InvariantCulture, out voltage)) voltages.Add(voltage);
            }
            result.Repeats["test_files"] = tests;
            result.Fields["test.voltage_levels_kv"] = string.Join(", ", voltages.OrderBy(v => v).Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)).ToArray());
            result.Fields["test.max_voltage_kv"] = voltages.Count == 0 ? string.Empty : voltages.Max().ToString("0.00", CultureInfo.InvariantCulture);
            foreach (KeyValuePair<string, int> pair in phaseCounts) result.Fields["files.phase_" + pair.Key.ToLowerInvariant() + "_dem_count"] = pair.Value.ToString(CultureInfo.InvariantCulture);

            result.Fields["project.path"] = root;
            result.Fields["project.folder_name"] = new DirectoryInfo(root).Name;
            result.Fields["project.parent_name"] = Directory.GetParent(root) == null ? string.Empty : Directory.GetParent(root).Name;
            result.Warnings.Add("The sample CAL/DEM/LOC files are ANSI/GBK text and use tab-separated columns. The parser auto-detects UTF/GBK and accepts tab, comma or semicolon delimiters.");
            result.Warnings.Add("LOC files can contain more than one million samples each. Project inspection reads headers and file indexes only; use the LabVIEW double[,] matrix input for processed 36-column report data.");
            return result;
        }

        private static MatrixData ReadLocationMatrix(double[,] matrix)
        {
            if (matrix == null) throw new ArgumentNullException("locationMatrix");
            int rows = matrix.GetLength(0);
            int cols = matrix.GetLength(1);
            if (cols < 36) throw new ArgumentException("The OWTS location matrix must have at least 36 columns; actual columns=" + cols.ToString(CultureInfo.InvariantCulture));
            MatrixData result = new MatrixData();
            result.Fields["matrix.total_rows"] = rows.ToString(CultureInfo.InvariantCulture);
            result.Fields["matrix.column_count"] = cols.ToString(CultureInfo.InvariantCulture);
            int valid = 0, selected = 0, automatic = 0;
            int[] phaseCounts = new int[4];
            double maxPd = double.NaN, maxPosition = double.NaN, maxVoltage = double.NaN;
            List<MatrixPoint> points = new List<MatrixPoint>();
            for (int i = 0; i < rows; i++)
            {
                int phase = SafeInt(matrix[i, 0]);
                if (phase < 1 || phase > 3) continue;
                double position = matrix[i, 13];
                double pd = IsFinite(matrix[i, 16]) && Math.Abs(matrix[i, 16]) > 0 ? matrix[i, 16] : matrix[i, 15];
                double voltage = matrix[i, 2];
                if (!IsFinite(position) || !IsFinite(pd)) continue;
                valid++;
                phaseCounts[phase]++;
                if (IsFinite(matrix[i, 34]) && matrix[i, 34] >= 0.5) automatic++;
                bool isSelected = IsFinite(matrix[i, 35]) && matrix[i, 35] >= 0.5;
                if (isSelected) selected++;
                maxPd = !IsFinite(maxPd) ? Math.Abs(pd) : Math.Max(maxPd, Math.Abs(pd));
                maxPosition = !IsFinite(maxPosition) ? position : Math.Max(maxPosition, position);
                if (IsFinite(voltage)) maxVoltage = !IsFinite(maxVoltage) ? voltage : Math.Max(maxVoltage, voltage);
                if (isSelected || (IsFinite(matrix[i, 34]) && matrix[i, 34] >= 0.5))
                {
                    MatrixPoint point = new MatrixPoint();
                    point.RowIndex = i;
                    point.Phase = phase;
                    point.LocationDataId = matrix[i, 1];
                    point.Voltage = voltage;
                    point.PhaseAngle = matrix[i, 3];
                    point.Position = position;
                    point.Pd = pd;
                    point.Probability = matrix[i, 22];
                    point.Width = matrix[i, 23];
                    point.Bandwidth = matrix[i, 24];
                    point.Auto = matrix[i, 34];
                    point.Selected = matrix[i, 35];
                    points.Add(point);
                }
            }
            result.Fields["matrix.valid_phase_rows"] = valid.ToString(CultureInfo.InvariantCulture);
            result.Fields["matrix.auto_rows"] = automatic.ToString(CultureInfo.InvariantCulture);
            result.Fields["matrix.selected_rows"] = selected.ToString(CultureInfo.InvariantCulture);
            result.Fields["matrix.phase_a_count"] = phaseCounts[1].ToString(CultureInfo.InvariantCulture);
            result.Fields["matrix.phase_b_count"] = phaseCounts[2].ToString(CultureInfo.InvariantCulture);
            result.Fields["matrix.phase_c_count"] = phaseCounts[3].ToString(CultureInfo.InvariantCulture);
            result.Fields["matrix.max_pd_pc"] = IsFinite(maxPd) ? maxPd.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
            result.Fields["matrix.max_position_m"] = IsFinite(maxPosition) ? maxPosition.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
            result.Fields["matrix.max_voltage_kv"] = IsFinite(maxVoltage) ? maxVoltage.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
            List<Dictionary<string, string>> records = points
                .OrderByDescending(p => NormalizeProbability(p.Probability))
                .ThenByDescending(p => Math.Abs(p.Pd))
                .Take(200)
                .Select(p => p.ToDictionary())
                .ToList();
            result.Repeats["matrix_points"] = records;
            if (points.Count > 200) result.Warnings.Add("Only the top 200 automatically analyzed/selected matrix rows are exposed to repeating report tables.");
            if (selected == 0) result.Warnings.Add("No row has column 35 selection flag=1. Matrix summary still includes rows marked by column 34 automatic analysis.");
            return result;
        }

        private static void ReadItemFile(string path, Dictionary<string, string> fields)
        {
            string[] lines = ReadTextLines(path);
            Dictionary<string, string> aliases = ItemFieldAliases();
            foreach (string raw in lines)
            {
                Match match = Regex.Match(raw, "^\\s*(.*?)\\s*=\\s*\"(.*)\"\\s*$");
                if (!match.Success) continue;
                string sourceKey = CleanLabel(match.Groups[1].Value);
                string value = match.Groups[2].Value;
                fields["item." + NormalizeFieldName(sourceKey)] = value;
                string target;
                if (aliases.TryGetValue(sourceKey, out target)) fields[target] = value;
            }
        }

        private static void ReadCableFile(string path, Dictionary<string, string> fields, Dictionary<string, List<Dictionary<string, string>>> repeats)
        {
            string[] lines = ReadTextLines(path);
            string phase = string.Empty;
            List<Dictionary<string, string>> segments = new List<Dictionary<string, string>>();
            List<double> jointPositions = new List<double>();
            foreach (string raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] parts = SplitDataLine(raw);
                if (parts.Length < 2) continue;
                if (parts[0].Trim() == "0")
                {
                    phase = parts[1].Trim();
                    continue;
                }
                Dictionary<string, string> record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                record["phase"] = phase;
                record["kind_code"] = parts[0].Trim();
                record["name"] = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                record["type"] = parts.Length > 2 ? parts[2].Trim() : string.Empty;
                record["position"] = parts.Length > 3 ? parts[3].Trim() : string.Empty;
                segments.Add(record);
                double position;
                if (TryParseMeasurement(parts.Length > 3 ? parts[3] : string.Empty, out position) && position > 0) jointPositions.Add(position);
            }
            repeats["cable_segments"] = segments;
            double length;
            if (fields.ContainsKey("cable.length_m") && TryParseMeasurement(fields["cable.length_m"], out length))
                jointPositions = jointPositions.Where(p => Math.Abs(p - length) > 0.5).ToList();
            fields["cable.joint_positions_m"] = string.Join(", ", jointPositions.Distinct().OrderBy(p => p).Select(p => p.ToString("0.###", CultureInfo.InvariantCulture)).ToArray());
        }

        private static Dictionary<string, string> ItemFieldAliases()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map["所属单位"] = "project.inspection_organization";
            map["检测单位"] = "project.inspection_organization";
            map["测试人员"] = "project.test_personnel";
            map["测试地点"] = "project.test_location";
            map["日期"] = "project.date";
            map["时间"] = "project.time";
            map["始端地点"] = "cable.start_name";
            map["始端名称"] = "cable.start_name";
            map["终端地点"] = "cable.end_name";
            map["终端名称"] = "cable.end_name";
            map["始端开关类型"] = "cable.start_type";
            map["始端类型"] = "cable.start_type";
            map["终端开关类型"] = "cable.end_type";
            map["终端类型"] = "cable.end_type";
            map["电缆长度"] = "cable.length_m";
            map["铺设时间"] = "cable.installation_date";
            map["相电压"] = "test.phase_voltage";
            map["电缆芯数"] = "cable.core_count";
            map["备注"] = "project.notes";
            map["A相测试前绝缘电阻"] = "insulation.a.before_mohm";
            map["B相测试前绝缘电阻"] = "insulation.b.before_mohm";
            map["C相测试前绝缘电阻"] = "insulation.c.before_mohm";
            map["A相测试后绝缘电阻"] = "insulation.a.after_mohm";
            map["B相测试后绝缘电阻"] = "insulation.b.after_mohm";
            map["C相测试后绝缘电阻"] = "insulation.c.after_mohm";
            map["线路名称"] = "cable.line_name";
            map["电缆型号"] = "cable.model";
            map["调度号1"] = "cable.start_dispatch_no";
            map["调度号2"] = "cable.end_dispatch_no";
            map["温度"] = "environment.temperature_c";
            map["天气"] = "environment.weather";
            map["湿度"] = "environment.humidity_percent";
            map["加压模式"] = "test.voltage_mode";
            map["加压倍数"] = "test.voltage_multipliers";
            map["加压次数"] = "test.voltage_repeat_counts";
            // Stable English storage keys; existing Chinese files remain aliases.
            string[,] english = {
                {"TestingOrganization","所属单位"},{"TestPersonnel","测试人员"},{"TestLocation","测试地点"},
                {"Date","日期"},{"Time","时间"},{"StartName","始端名称"},{"EndName","终端名称"},
                {"StartType","始端类型"},{"EndType","终端类型"},{"CableLength","电缆长度"},
                {"InstallationDate","铺设时间"},{"PhaseVoltage","相电压"},{"CoreCount","电缆芯数"},
                {"Remarks","备注"},{"A1","A相测试前绝缘电阻"},{"A2","A相测试后绝缘电阻"},
                {"B1","B相测试前绝缘电阻"},{"B2","B相测试后绝缘电阻"},
                {"C1","C相测试前绝缘电阻"},{"C2","C相测试后绝缘电阻"},
                {"LineName","线路名称"},{"CableModel","电缆型号"},{"StartDispatch","调度号1"},
                {"EndDispatch","调度号2"},{"Temperature","温度"},{"Weather","天气"},{"Humidity","湿度"},
                {"TestMode","加压模式"},{"PressureMultipliers","加压倍数"},{"PressureRepetitions","加压次数"}
            };
            for(int i=0;i<english.GetLength(0);i++)map[english[i,0]]=map[english[i,1]];
            return map;
        }

        private static Dictionary<string, List<string>> DefaultBindings()
        {
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            AddBinding(map, "project.inspection_organization", "检测单位", "所属单位");
            AddBinding(map, "project.test_personnel", "测试人员", "检测人员");
            AddBinding(map, "project.test_location", "测试地点", "检测地点");
            AddBinding(map, "project.date", "测试日期", "日期");
            AddBinding(map, "project.time", "测试时间", "时间");
            AddBinding(map, "cable.start_name", "始端名称", "始端地点");
            AddBinding(map, "cable.end_name", "终端名称", "终端地点");
            AddBinding(map, "cable.start_type", "始端类型", "始端附件类型", "始端开关类型");
            AddBinding(map, "cable.end_type", "终端类型", "终端附件类型", "终端开关类型");
            AddBinding(map, "cable.start_dispatch_no", "调度号始", "始端调度号");
            AddBinding(map, "cable.end_dispatch_no", "调度号末", "终端调度号");
            AddBinding(map, "cable.line_name", "线路名称");
            AddBinding(map, "cable.installation_date", "铺设时间", "投运时间");
            AddBinding(map, "cable.model", "电缆型号");
            AddBinding(map, "cable.length_m", "电缆长度");
            AddBinding(map, "cable.core_count", "电缆芯数", "芯数");
            AddBinding(map, "test.phase_voltage", "相电压");
            AddBinding(map, "test.voltage_mode", "加压模式");
            AddBinding(map, "insulation.a.before_mohm", "A相绝缘[MΩ] 1", "A相测试前绝缘电阻");
            AddBinding(map, "insulation.a.after_mohm", "A相绝缘[MΩ] 2", "A相测试后绝缘电阻");
            AddBinding(map, "insulation.b.before_mohm", "B相绝缘[MΩ] 1", "B相测试前绝缘电阻");
            AddBinding(map, "insulation.b.after_mohm", "B相绝缘[MΩ] 2", "B相测试后绝缘电阻");
            AddBinding(map, "insulation.c.before_mohm", "C相绝缘[MΩ] 1", "C相测试前绝缘电阻");
            AddBinding(map, "insulation.c.after_mohm", "C相绝缘[MΩ] 2", "C相测试后绝缘电阻");
            AddBinding(map, "environment.temperature_c", "温度");
            AddBinding(map, "environment.humidity_percent", "湿度");
            AddBinding(map, "environment.weather", "天气");
            AddBinding(map, "project.notes", "备注");
            return map;
        }

        private static void AddBinding(Dictionary<string, List<string>> map, string field, params string[] labels)
        {
            map[field] = new List<string>(labels);
        }

        private static int FillBoundLabels(XDocument xml, Dictionary<string, string> values, Dictionary<string, List<string>> bindings)
        {
            Dictionary<string, string> labelToField = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<string>> pair in bindings)
                foreach (string label in pair.Value) labelToField[CleanLabel(label)] = pair.Key;
            int count = 0;
            foreach (XElement row in xml.Descendants(W + "tr"))
            {
                List<XElement> cells = row.Elements(W + "tc").ToList();
                for (int i = 0; i + 1 < cells.Count; i++)
                {
                    string label = CleanLabel(ElementText(cells[i]));
                    string field;
                    string value;
                    if (labelToField.TryGetValue(label, out field) && values.TryGetValue(field, out value))
                    {
                        SetCellText(cells[i + 1], value);
                        count++;
                    }
                }
            }
            foreach (XElement paragraph in xml.Descendants(W + "p").Where(p => p.Ancestors(W + "tc").FirstOrDefault() == null))
            {
                string text = ElementText(paragraph).Trim();
                foreach (KeyValuePair<string, string> pair in labelToField)
                {
                    if (!StartsWithLabel(text, pair.Key)) continue;
                    string value;
                    if (!values.TryGetValue(pair.Value, out value)) continue;
                    SetParagraphText(paragraph, DisplayLabel(text, pair.Key) + "：" + value);
                    count++;
                    break;
                }
            }
            return count;
        }

        private sealed class DynamicSectionSpec
        {
            public string Anchor;
            public string Title;
            public string Subtitle;
            public string Body;
            public string ImageKey;
            public bool PageBreakBefore;
        }

        private static List<DynamicSectionSpec> ParseDynamicSections(Dictionary<string, object> request, Dictionary<string, object> images)
        {
            List<DynamicSectionSpec> result = new List<DynamicSectionSpec>();
            object raw;
            if (request == null || !request.TryGetValue("sections", out raw) || raw == null) return result;
            IEnumerable enumerable = raw as IEnumerable;
            if (enumerable == null || raw is string || raw is Dictionary<string, object>)
                throw new ArgumentException("sections must be a JSON array.");
            int ordinal = 0;
            foreach (object item in enumerable)
            {
                Dictionary<string, object> obj = item as Dictionary<string, object>;
                if (obj == null) continue;
                DynamicSectionSpec section = new DynamicSectionSpec();
                section.Anchor = OptionalString(obj, "anchor");
                if (string.IsNullOrWhiteSpace(section.Anchor)) section.Anchor = "sections";
                section.Title = OptionalString(obj, "title");
                section.Subtitle = OptionalString(obj, "subtitle");
                section.Body = OptionalString(obj, "body");
                section.PageBreakBefore = OptionalBool(obj, "page_break_before", ordinal > 0);
                section.ImageKey = OptionalString(obj, "image_key");
                string imagePath = OptionalString(obj, "image_path");
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    string key = "__dynamic_section_" + ordinal.ToString("0000", CultureInfo.InvariantCulture);
                    Dictionary<string, object> image = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    image["path"] = imagePath;
                    image["caption"] = OptionalString(obj, "caption");
                    image["page_break_before"] = false;
                    image["center"] = true;
                    image["caption_from_filename"] = false;
                    double maxWidth = OptionalDouble(obj, "max_width_in", 0.0);
                    double maxHeight = OptionalDouble(obj, "max_height_in", 0.0);
                    if (maxWidth > 0) image["max_width_in"] = maxWidth;
                    if (maxHeight > 0) image["max_height_in"] = maxHeight;
                    images[key] = image;
                    section.ImageKey = key;
                }
                result.Add(section);
                ordinal++;
            }
            return result;
        }

        private static int ExpandDynamicSections(XDocument xml, List<DynamicSectionSpec> sections)
        {
            int inserted = 0;
            List<XElement> anchors = xml.Descendants(W + "p").Where(p => DynamicMarkerRegex.IsMatch(ElementText(p))).ToList();
            foreach (XElement anchorParagraph in anchors)
            {
                Match marker = DynamicMarkerRegex.Match(ElementText(anchorParagraph));
                if (!marker.Success) continue;
                string anchorName = marker.Groups[1].Value;
                List<DynamicSectionSpec> matching = sections.Where(s => string.Equals(s.Anchor, anchorName, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (DynamicSectionSpec section in matching)
                {
                    bool needsBreak = section.PageBreakBefore;
                    if (!string.IsNullOrWhiteSpace(section.Title))
                    {
                        anchorParagraph.AddBeforeSelf(BuildDynamicParagraph(section.Title, "Heading2", true, needsBreak));
                        needsBreak = false;
                    }
                    if (needsBreak) anchorParagraph.AddBeforeSelf(BuildDynamicParagraph(string.Empty, string.Empty, false, true));
                    if (!string.IsNullOrWhiteSpace(section.Subtitle)) anchorParagraph.AddBeforeSelf(BuildDynamicParagraph(section.Subtitle, string.Empty, true, false));
                    if (!string.IsNullOrWhiteSpace(section.Body))
                    {
                        string[] paragraphs = section.Body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                        foreach (string paragraph in paragraphs) if (!string.IsNullOrWhiteSpace(paragraph)) anchorParagraph.AddBeforeSelf(BuildDynamicParagraph(paragraph, string.Empty, false, false));
                    }
                    if (!string.IsNullOrWhiteSpace(section.ImageKey)) anchorParagraph.AddBeforeSelf(BuildDynamicParagraph("{{image:" + section.ImageKey + "}}", string.Empty, false, false));
                    inserted++;
                }
                anchorParagraph.Remove();
            }
            return inserted;
        }

        private static XElement BuildDynamicParagraph(string text, string style, bool bold, bool pageBreakBefore)
        {
            XElement properties = new XElement(W + "pPr",
                new XElement(W + "keepLines"),
                new XElement(W + "spacing", new XAttribute(W + "before", bold ? "120" : "40"), new XAttribute(W + "after", bold ? "80" : "60")));
            if (!string.IsNullOrWhiteSpace(style)) properties.AddFirst(new XElement(W + "pStyle", new XAttribute(W + "val", style)));
            if (pageBreakBefore)
            {
                XElement spacing = properties.Element(W + "spacing");
                spacing.AddBeforeSelf(new XElement(W + "pageBreakBefore"));
            }
            XElement paragraph = new XElement(W + "p", properties);
            if (!string.IsNullOrEmpty(text))
            {
                XElement runProperties = new XElement(W + "rPr");
                if (bold) runProperties.Add(new XElement(W + "b"));
                XElement value = new XElement(W + "t", text);
                value.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                paragraph.Add(new XElement(W + "r", runProperties, value));
            }
            return paragraph;
        }

        private static void NormalizeStandalonePageBreakParagraphs(XDocument xml)
        {
            List<XElement> paragraphs = xml.Descendants(W + "p").ToList();
            foreach (XElement paragraph in paragraphs)
            {
                List<XElement> pageBreaks = paragraph.Descendants(W + "br")
                    .Where(br => string.Equals((string)br.Attribute(W + "type"), "page", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (pageBreaks.Count == 0) continue;

                List<XElement> payload = paragraph.Elements().Where(element => element.Name != W + "pPr").ToList();
                bool onlyStandalonePageBreak = payload.Count > 0 && payload.All(run =>
                    run.Name == W + "r" &&
                    run.Elements().All(child =>
                        child.Name == W + "rPr" ||
                        (child.Name == W + "br" && string.Equals((string)child.Attribute(W + "type"), "page", StringComparison.OrdinalIgnoreCase))));
                if (!onlyStandalonePageBreak) continue;

                XElement nextParagraph = paragraph.ElementsAfterSelf().FirstOrDefault();
                if (nextParagraph == null || nextParagraph.Name != W + "p") continue;
                XElement properties = nextParagraph.Element(W + "pPr");
                if (properties == null)
                {
                    properties = new XElement(W + "pPr");
                    nextParagraph.AddFirst(properties);
                }
                if (properties.Element(W + "pageBreakBefore") == null)
                {
                    XElement spacing = properties.Element(W + "spacing");
                    if (spacing != null) spacing.AddBeforeSelf(new XElement(W + "pageBreakBefore"));
                    else properties.Add(new XElement(W + "pageBreakBefore"));
                }
                paragraph.Remove();
            }
        }

        private static void RemoveTemplateOnlyTail(XDocument xml)
        {
            XElement marker = xml.Descendants(W + "p").FirstOrDefault(p => TemplateTailMarkerRegex.IsMatch(ElementText(p)));
            if (marker == null) return;
            XElement body = marker.Ancestors(W + "body").FirstOrDefault();
            if (body == null) { marker.Remove(); return; }
            XElement current = marker;
            while (current != null)
            {
                XElement next = current.ElementsAfterSelf().FirstOrDefault();
                if (current.Name != W + "sectPr") current.Remove();
                current = next;
            }
        }

        private static int FillContentControls(XDocument xml, Dictionary<string, string> values)
        {
            int count = 0;
            foreach (XElement sdt in xml.Descendants(W + "sdt"))
            {
                XElement tag = sdt.Descendants(W + "tag").FirstOrDefault();
                if (tag == null) continue;
                XAttribute attr = tag.Attribute(W + "val");
                if (attr == null) continue;
                string value;
                if (!values.TryGetValue(attr.Value, out value)) continue;
                XElement content = sdt.Element(W + "sdtContent");
                if (content == null) continue;
                XElement text = content.Descendants(W + "t").FirstOrDefault();
                foreach (XElement node in content.Descendants(W + "t")) node.Value = string.Empty;
                if (text == null)
                {
                    XElement paragraph = content.Descendants(W + "p").FirstOrDefault();
                    if (paragraph == null)
                    {
                        paragraph = new XElement(W + "p");
                        content.Add(paragraph);
                    }
                    text = new XElement(W + "t");
                    paragraph.Add(new XElement(W + "r", text));
                }
                text.Value = value ?? string.Empty;
                text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
                count++;
            }
            return count;
        }

        private static int ReplaceScalarMarkers(XDocument xml, Dictionary<string, string> values)
        {
            int count = 0;
            foreach (XElement paragraph in xml.Descendants(W + "p"))
            {
                string original = ElementText(paragraph);
                if (original.IndexOf("{{", StringComparison.Ordinal) < 0 && original.IndexOf("[[", StringComparison.Ordinal) < 0 && original.IndexOf("${", StringComparison.Ordinal) < 0) continue;
                int local = 0;
                string replaced = MarkerRegex.Replace(original, delegate(Match match)
                {
                    string key = FirstGroup(match).Trim();
                    if (key.StartsWith("image:", StringComparison.OrdinalIgnoreCase) || key.StartsWith("row:", StringComparison.OrdinalIgnoreCase)) return match.Value;
                    string value;
                    if (values.TryGetValue(key, out value)) { local++; return value ?? string.Empty; }
                    return match.Value;
                });
                if (local > 0)
                {
                    SetParagraphText(paragraph, replaced);
                    count += local;
                }
            }
            return count;
        }

        private static int ExpandRepeatRows(XDocument xml, Dictionary<string, List<Dictionary<string, string>>> repeats)
        {
            int created = 0;
            List<XElement> templateRows = xml.Descendants(W + "tr").Where(r => RowMarkerRegex.IsMatch(ElementText(r))).ToList();
            foreach (XElement templateRow in templateRows)
            {
                Match first = RowMarkerRegex.Match(ElementText(templateRow));
                if (!first.Success) continue;
                string tableName = first.Groups[1].Value;
                List<Dictionary<string, string>> rows;
                if (!repeats.TryGetValue(tableName, out rows)) continue;
                foreach (Dictionary<string, string> rowData in rows)
                {
                    XElement clone = new XElement(templateRow);
                    foreach (XElement paragraph in clone.Descendants(W + "p"))
                    {
                        string text = ElementText(paragraph);
                        string replacement = RowMarkerRegex.Replace(text, delegate(Match match)
                        {
                            if (!string.Equals(match.Groups[1].Value, tableName, StringComparison.OrdinalIgnoreCase)) return match.Value;
                            string value;
                            return rowData.TryGetValue(match.Groups[2].Value, out value) ? (value ?? string.Empty) : string.Empty;
                        });
                        if (!string.Equals(text, replacement, StringComparison.Ordinal)) SetParagraphText(paragraph, replacement);
                    }
                    templateRow.AddBeforeSelf(clone);
                    created++;
                }
                templateRow.Remove();
            }
            return created;
        }

        private static List<string> FindUnresolvedMarkers(ZipArchive archive)
        {
            HashSet<string> unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in archive.Entries.Where(e => IsTextPart(e.FullName)).ToArray())
            {
                XDocument xml = LoadXml(archive, entry.FullName);
                foreach (Match match in MarkerRegex.Matches(ElementText(xml.Root))) unresolved.Add(match.Value);
            }
            return unresolved.OrderBy(s => s).ToList();
        }

        private sealed class ImageManager
        {
            private readonly ZipArchive _archive;
            private readonly Dictionary<string, object> _specs;
            private readonly Dictionary<string, object> _layout;
            private readonly XDocument _rels;
            private readonly XDocument _contentTypes;
            private int _nextId;
            private int _bodyImageCount;

            public ImageManager(ZipArchive archive, Dictionary<string, object> specs, Dictionary<string, object> layout)
            {
                _archive = archive;
                _specs = specs ?? new Dictionary<string, object>();
                _layout = layout ?? new Dictionary<string, object>();
                _rels = LoadOrCreateRelationships(archive, "word/_rels/document.xml.rels");
                _contentTypes = LoadXml(archive, "[Content_Types].xml");
                _nextId = NextRelationshipNumber(_rels);
                _bodyImageCount = 0;
            }

            public int InsertImages(XDocument document)
            {
                int count = 0;
                bool mainDocument = document.Descendants(W + "body").Any();
                double pageWidthIn;
                double pageHeightIn;
                PageContentSize(document, out pageWidthIn, out pageHeightIn);
                foreach (XElement paragraph in document.Descendants(W + "p").ToList())
                {
                    string text = ElementText(paragraph);
                    MatchCollection matches = ImageMarkerRegex.Matches(text);
                    if (matches.Count == 0) continue;
                    foreach (Match match in matches)
                    {
                        string key = match.Groups[1].Value;
                        object raw;
                        if (!_specs.TryGetValue(key, out raw)) continue;
                        ImageSpec spec = ParseImageSpec(raw);
                        string path = FullExistingFile(spec.Path);
                        string relationshipId = AddImagePart(path);
                        double maxWidthIn = spec.MaxWidthIn > 0 ? Math.Min(spec.MaxWidthIn, pageWidthIn) : OptionalDouble(_layout, "max_width_in", pageWidthIn);
                        if (maxWidthIn <= 0 || maxWidthIn > pageWidthIn) maxWidthIn = pageWidthIn;
                        double defaultMaxHeight = Math.Max(3.0, Math.Min(7.25, pageHeightIn - 1.0));
                        double maxHeightIn = spec.MaxHeightIn > 0 ? Math.Min(spec.MaxHeightIn, pageHeightIn) : OptionalDouble(_layout, "max_height_in", defaultMaxHeight);
                        if (maxHeightIn <= 0 || maxHeightIn > pageHeightIn) maxHeightIn = defaultMaxHeight;
                        long cx, cy;
                        ImageSize(path, spec.WidthIn, maxWidthIn, maxHeightIn, out cx, out cy);
                        string remaining = ImageMarkerRegex.Replace(text, string.Empty);
                        SetParagraphText(paragraph, remaining.Trim());
                        bool inTable = paragraph.Ancestors(W + "tc").Any();
                        bool onePerPage = OptionalBool(_layout, "one_image_per_page", true);
                        bool pageBreakBefore = spec.PageBreakBefore.HasValue ? spec.PageBreakBefore.Value : (mainDocument && !inTable && onePerPage && _bodyImageCount > 0);
                        bool centered = spec.Centered.HasValue ? spec.Centered.Value : OptionalBool(_layout, "center", true);
                        bool captionFromFilename = spec.CaptionFromFilename.HasValue ? spec.CaptionFromFilename.Value : OptionalBool(_layout, "caption_from_filename", true);
                        bool replaceFollowingCaption = spec.ReplaceFollowingCaption.HasValue ? spec.ReplaceFollowingCaption.Value : OptionalBool(_layout, "replace_following_caption", true);
                        string caption = spec.Caption;
                        bool automaticCaption = false;
                        if (string.IsNullOrWhiteSpace(caption) && captionFromFilename) { caption = Path.GetFileNameWithoutExtension(path); automaticCaption = true; }
                        ConfigureImageParagraph(paragraph, centered, !string.IsNullOrWhiteSpace(caption));
                        if (pageBreakBefore) paragraph.Add(new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page"))));
                        paragraph.Add(new XElement(W + "r", BuildDrawing(relationshipId, Path.GetFileName(path), cx, cy, _nextId + count + 1000)));
                        if (!string.IsNullOrWhiteSpace(caption)) SetOrInsertCaption(paragraph, caption, automaticCaption, replaceFollowingCaption);
                        count++;
                        if (mainDocument) _bodyImageCount++;
                    }
                }
                return count;
            }

            public void Save()
            {
                SaveXml(_archive, "word/_rels/document.xml.rels", _rels);
                SaveXml(_archive, "[Content_Types].xml", _contentTypes);
            }

            private static void ConfigureImageParagraph(XElement paragraph, bool centered, bool keepWithCaption)
            {
                XElement properties = paragraph.Element(W + "pPr");
                if (properties == null) { properties = new XElement(W + "pPr"); paragraph.AddFirst(properties); }
                if (centered) SetParagraphFlag(properties, "jc", "center");
                if (keepWithCaption) SetParagraphFlag(properties, "keepNext", null);
                SetParagraphFlag(properties, "keepLines", null);
                SetParagraphFlag(properties, "spacing", null, new XAttribute(W + "before", "0"), new XAttribute(W + "after", "80"));
            }

            private static void SetOrInsertCaption(XElement imageParagraph, string caption, bool automaticCaption, bool replaceFollowing)
            {
                XElement next = imageParagraph.ElementsAfterSelf(W + "p").FirstOrDefault();
                bool nextIsCaption = next != null && LooksLikeCaption(ElementText(next));
                string value = caption.Trim();
                if (nextIsCaption && automaticCaption) value = PreserveCaptionPrefix(ElementText(next), value);
                XElement target;
                if (nextIsCaption && replaceFollowing) { target = next; SetParagraphText(target, value); }
                else { target = BuildCaptionParagraph(value); imageParagraph.AddAfterSelf(target); }
                ConfigureCaptionParagraph(target);
            }

            private static bool LooksLikeCaption(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                return Regex.IsMatch(value.Trim(), @"^(\u56fe\s*\d+|Figure\s*\d+)", RegexOptions.IgnoreCase);
            }

            private static string PreserveCaptionPrefix(string original, string caption)
            {
                Match prefix = Regex.Match(original ?? string.Empty, @"^\s*((?:\u56fe|Figure)\s*\d+(?:\.\d+)*\s*[\.\u3002\uff1a:\-]?\s*)", RegexOptions.IgnoreCase);
                return prefix.Success ? prefix.Groups[1].Value + caption : caption;
            }

            private static XElement BuildCaptionParagraph(string caption)
            {
                return new XElement(W + "p", new XElement(W + "pPr"), new XElement(W + "r",
                    new XElement(W + "rPr", new XElement(W + "rFonts", new XAttribute(W + "ascii", "Times New Roman"), new XAttribute(W + "eastAsia", "\u5b8b\u4f53")),
                        new XElement(W + "sz", new XAttribute(W + "val", "21")), new XElement(W + "szCs", new XAttribute(W + "val", "21"))),
                    new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), caption)));
            }

            private static void ConfigureCaptionParagraph(XElement paragraph)
            {
                XElement properties = paragraph.Element(W + "pPr");
                if (properties == null) { properties = new XElement(W + "pPr"); paragraph.AddFirst(properties); }
                SetParagraphFlag(properties, "jc", "center");
                SetParagraphFlag(properties, "keepLines", null);
                SetParagraphFlag(properties, "spacing", null, new XAttribute(W + "before", "40"), new XAttribute(W + "after", "120"));
            }

            private static void SetParagraphFlag(XElement properties, string name, string value, params XAttribute[] extra)
            {
                XElement existing = properties.Element(W + name);
                if (existing != null) existing.Remove();
                XElement element = new XElement(W + name);
                if (value != null) element.SetAttributeValue(W + "val", value);
                if (extra != null) foreach (XAttribute attribute in extra) element.Add(new XAttribute(attribute));
                properties.Add(element);
            }

            private static void PageContentSize(XDocument document, out double widthIn, out double heightIn)
            {
                widthIn = 6.25; heightIn = 9.2;
                XElement section = document.Descendants(W + "sectPr").LastOrDefault();
                if (section == null) return;
                XElement page = section.Element(W + "pgSz");
                XElement margins = section.Element(W + "pgMar");
                double pageWidth = Twips(page, "w", 11906.0), pageHeight = Twips(page, "h", 16838.0);
                double left = Twips(margins, "left", 1440.0), right = Twips(margins, "right", 1440.0);
                double top = Twips(margins, "top", 1440.0), bottom = Twips(margins, "bottom", 1440.0), gutter = Twips(margins, "gutter", 0.0);
                widthIn = Math.Max(2.0, (pageWidth - left - right - gutter) / 1440.0 - 0.08);
                heightIn = Math.Max(3.0, (pageHeight - top - bottom) / 1440.0 - 0.12);
            }

            private static double Twips(XElement element, string name, double fallback)
            {
                if (element == null) return fallback;
                XAttribute attribute = element.Attribute(W + name);
                double value;
                return attribute != null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
            }

            private string AddImagePart(string sourcePath)
            {
                string ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
                if (ext == "jpg") ext = "jpeg";
                if (ext != "png" && ext != "jpeg" && ext != "gif" && ext != "bmp" && ext != "tif" && ext != "tiff")
                    throw new NotSupportedException("Unsupported report image type: " + Path.GetExtension(sourcePath));
                string relId = "rId" + (_nextId++).ToString(CultureInfo.InvariantCulture);
                string fileName = "report_" + Guid.NewGuid().ToString("N") + "." + ext;
                string packagePath = "word/media/" + fileName;
                ZipArchiveEntry imageEntry = _archive.CreateEntry(packagePath, CompressionLevel.Optimal);
                using (Stream output = imageEntry.Open())
                using (FileStream input = File.OpenRead(sourcePath)) input.CopyTo(output);
                _rels.Root.Add(new XElement(PR + "Relationship", new XAttribute("Id", relId),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"), new XAttribute("Target", "media/" + fileName)));
                string contentType = ext == "png" ? "image/png" : (ext == "jpeg" ? "image/jpeg" : "image/" + ext);
                bool exists = _contentTypes.Root.Elements(CT + "Default").Any(x => string.Equals((string)x.Attribute("Extension"), ext, StringComparison.OrdinalIgnoreCase));
                if (!exists) _contentTypes.Root.Add(new XElement(CT + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", contentType)));
                return relId;
            }
        }

        private sealed class ImageSpec
        {
            public string Path;
            public double WidthIn;
            public double MaxWidthIn;
            public double MaxHeightIn;
            public string Caption;
            public bool? PageBreakBefore;
            public bool? Centered;
            public bool? CaptionFromFilename;
            public bool? ReplaceFollowingCaption;
        }

        private static XElement BuildDrawing(string relId, string name, long cx, long cy, int id)
        {
            XElement picture = new XElement(PIC + "pic",
                new XElement(PIC + "nvPicPr",
                    new XElement(PIC + "cNvPr", new XAttribute("id", "0"), new XAttribute("name", name)),
                    new XElement(PIC + "cNvPicPr")),
                new XElement(PIC + "blipFill",
                    new XElement(A + "blip", new XAttribute(R + "embed", relId),
                        new XElement(A + "extLst",
                            new XElement(A + "ext", new XAttribute("uri", "{28A0092B-C50C-407E-A947-70E740481C1C}"),
                                new XElement(A14 + "useLocalDpi", new XAttribute("val", "0"))))),
                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(PIC + "spPr",
                    new XElement(A + "xfrm",
                        new XElement(A + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                        new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
            XElement graphic = new XElement(A + "graphic",
                new XElement(A + "graphicData",
                    new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                    picture));
            XElement inline = new XElement(WP + "inline",
                new XAttribute("distT", "0"),
                new XAttribute("distB", "0"),
                new XAttribute("distL", "0"),
                new XAttribute("distR", "0"),
                new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(WP + "effectExtent", new XAttribute("l", "0"), new XAttribute("t", "0"), new XAttribute("r", "0"), new XAttribute("b", "0")),
                new XElement(WP + "docPr", new XAttribute("id", id), new XAttribute("name", "Report image " + id), new XAttribute("descr", name)),
                new XElement(WP + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", "1"))),
                graphic);
            return new XElement(W + "drawing", inline);
        }

        private static void ImageSize(string path, double widthIn, double maxWidthIn, double maxHeightIn, out long cx, out long cy)
        {
            using (Image image = Image.FromFile(path))
            {
                double ratio = image.Width > 0 ? (double)image.Height / image.Width : 0.75;
                double width = widthIn > 0 ? Math.Min(widthIn, maxWidthIn) : maxWidthIn;
                double height = width * ratio;
                if (height > maxHeightIn && height > 0)
                {
                    double scale = maxHeightIn / height;
                    width *= scale;
                    height = maxHeightIn;
                }
                cx = (long)Math.Round(width * 914400.0);
                cy = (long)Math.Round(height * 914400.0);
            }
        }

        private static ImageSpec ParseImageSpec(object raw)
        {
            ImageSpec spec = new ImageSpec();
            spec.Path = string.Empty;
            spec.WidthIn = 0.0;
            string direct = raw as string;
            if (direct != null) { spec.Path = direct; return spec; }
            Dictionary<string, object> obj = raw as Dictionary<string, object>;
            if (obj == null) throw new ArgumentException("Image specification must be a path string or object.");
            spec.Path = OptionalString(obj, "path");
            spec.WidthIn = OptionalDouble(obj, "width_in", 0.0);
            spec.MaxWidthIn = OptionalDouble(obj, "max_width_in", 0.0);
            spec.MaxHeightIn = OptionalDouble(obj, "max_height_in", 0.0);
            spec.Caption = OptionalString(obj, "caption");
            spec.PageBreakBefore = OptionalNullableBool(obj, "page_break_before");
            spec.Centered = OptionalNullableBool(obj, "center");
            spec.CaptionFromFilename = OptionalNullableBool(obj, "caption_from_filename");
            spec.ReplaceFollowingCaption = OptionalNullableBool(obj, "replace_following_caption");
            return spec;
        }

        private static void PatchTemplateContentType(ZipArchive archive)
        {
            XDocument contentTypes = LoadXml(archive, "[Content_Types].xml");
            foreach (XElement item in contentTypes.Root.Elements(CT + "Override"))
            {
                XAttribute partName = item.Attribute("PartName");
                XAttribute contentType = item.Attribute("ContentType");
                if (partName != null && contentType != null && partName.Value == "/word/document.xml" && contentType.Value.IndexOf("template.main+xml", StringComparison.OrdinalIgnoreCase) >= 0)
                    contentType.Value = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
            }
            SaveXml(archive, "[Content_Types].xml", contentTypes);
        }

        private static void PatchImageQualitySettings(ZipArchive archive)
        {
            const string settingsPart = "word/settings.xml";
            ZipArchiveEntry entry = archive.GetEntry(settingsPart);
            if (entry == null) return;
            XDocument settings = LoadXml(archive, settingsPart);
            XElement root = settings.Root;
            if (root == null) return;
            if (root.Element(W + "doNotAutoCompressPictures") == null)
                root.Add(new XElement(W + "doNotAutoCompressPictures"));
            SaveXml(archive, settingsPart, settings);
        }

        private static string[] ReadTextLines(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Encoding encoding = DetectTextEncoding(bytes);
            return encoding.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static Encoding DetectTextEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
            try { new UTF8Encoding(false, true).GetString(bytes); return new UTF8Encoding(false); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(936); }
        }

        private static string[] SplitDataLine(string line)
        {
            char delimiter = line.IndexOf('\t') >= 0 ? '\t' : (line.IndexOf(',') >= 0 ? ',' : (line.IndexOf(';') >= 0 ? ';' : '\t'));
            return line.Split(new char[] { delimiter }, StringSplitOptions.None);
        }

        private static Dictionary<string, string> ReadHeader(string path, int maxLines)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = ReadTextLines(path);
            for (int i = 0; i < lines.Length && i < maxLines; i++)
            {
                string line = lines[i];
                if (line.Trim().Equals("Data:", StringComparison.OrdinalIgnoreCase) || line.Trim().Equals("DATA-TOTAL:", StringComparison.OrdinalIgnoreCase)) break;
                string[] parts = SplitDataLine(line);
                if (parts.Length >= 2) result[CleanLabel(parts[0])] = parts[1].Trim();
            }
            return result;
        }

        private static Dictionary<string, string> ParseMeasurementFileName(string path)
        {
            Dictionary<string, string> record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            record["file_name"] = Path.GetFileName(path);
            record["path"] = path;
            record["size_bytes"] = new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture);
            Match match = Regex.Match(Path.GetFileName(path), @"^(?<phase>ALL|A|B|C)_(?<voltage>[0-9.]+)kV_(?<date>\d{4}-\d{2}-\d{2})-(?<time>.+)\.(?<ext>dem|loc)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                record["phase"] = match.Groups["phase"].Value.ToUpperInvariant();
                record["voltage_kv"] = match.Groups["voltage"].Value;
                record["date"] = match.Groups["date"].Value;
                record["time_token"] = match.Groups["time"].Value;
                record["extension"] = match.Groups["ext"].Value.ToLowerInvariant();
            }
            return record;
        }

        private static Dictionary<string, object> DeserializeObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Request JSON is empty.");
            object value = Json.DeserializeObject(json);
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict == null) throw new ArgumentException("Request JSON root must be an object.");
            return dict;
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 64 * 1024 * 1024;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private static string SerializeAscii(object value)
        {
            string json = Json.Serialize(value);
            StringBuilder builder = new StringBuilder(json.Length + 64);
            foreach (char ch in json)
            {
                if (ch > 0x7E)
                {
                    builder.Append("\\u");
                    builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                }
                else builder.Append(ch);
            }
            return builder.ToString();
        }

        private static string Error(string code, Exception ex)
        {
            Dictionary<string, object> reply = new Dictionary<string, object>();
            reply["ok"] = false;
            reply["api_version"] = ApiVersion;
            reply["error_code"] = code;
            reply["error"] = ex.Message;
            reply["exception_type"] = ex.GetType().FullName;
            return SerializeAscii(reply);
        }

        private static string RequiredString(Dictionary<string, object> dict, string key)
        {
            string value = OptionalString(dict, key);
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(key + " is required.");
            return value;
        }

        private static string OptionalString(Dictionary<string, object> dict, string key)
        {
            object value;
            return dict != null && dict.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
        }

        private static bool OptionalBool(Dictionary<string, object> dict, string key, bool fallback)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null) return fallback;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        private static int OptionalInt(Dictionary<string, object> dict, string key, int fallback)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null) return fallback;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static double OptionalDouble(Dictionary<string, object> dict, string key, double fallback)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null) return fallback;
            double parsed;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static bool? OptionalNullableBool(Dictionary<string, object> dict, string key)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null) return null;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? (bool?)parsed : null;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> dict, string key, bool required)
        {
            object value;
            if (dict == null || !dict.TryGetValue(key, out value) || value == null)
            {
                if (required) throw new ArgumentException(key + " object is required.");
                return null;
            }
            Dictionary<string, object> result = value as Dictionary<string, object>;
            if (result == null) throw new ArgumentException(key + " must be a JSON object.");
            return result;
        }

        private static void FlattenObject(Dictionary<string, object> source, string prefix, Dictionary<string, string> target, bool overwrite)
        {
            foreach (KeyValuePair<string, object> pair in source)
            {
                string key = string.IsNullOrWhiteSpace(prefix) ? pair.Key : prefix + "." + pair.Key;
                Dictionary<string, object> nested = pair.Value as Dictionary<string, object>;
                if (nested != null) { FlattenObject(nested, key, target, overwrite); continue; }
                string value = pair.Value == null ? string.Empty : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                if (overwrite || !target.ContainsKey(key)) target[key] = value;
            }
        }

        private static void MergeStringDictionary(Dictionary<string, string> target, Dictionary<string, string> source, bool overwrite)
        {
            foreach (KeyValuePair<string, string> pair in source) if (overwrite || !target.ContainsKey(pair.Key)) target[pair.Key] = pair.Value;
        }

        private static void MergeRepeats(Dictionary<string, List<Dictionary<string, string>>> target, Dictionary<string, List<Dictionary<string, string>>> source, bool overwrite)
        {
            foreach (KeyValuePair<string, List<Dictionary<string, string>>> pair in source) if (overwrite || !target.ContainsKey(pair.Key)) target[pair.Key] = pair.Value;
        }

        private static void MergeUserRepeats(Dictionary<string, List<Dictionary<string, string>>> target, Dictionary<string, object> source)
        {
            foreach (KeyValuePair<string, object> pair in source)
            {
                ArrayList rows = pair.Value as ArrayList;
                object[] array = pair.Value as object[];
                IEnumerable enumerable = rows != null ? (IEnumerable)rows : array;
                if (enumerable == null) throw new ArgumentException("repeat_tables." + pair.Key + " must be an array.");
                List<Dictionary<string, string>> records = new List<Dictionary<string, string>>();
                foreach (object item in enumerable)
                {
                    Dictionary<string, object> obj = item as Dictionary<string, object>;
                    if (obj == null) continue;
                    Dictionary<string, string> record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    FlattenObject(obj, string.Empty, record, true);
                    records.Add(record);
                }
                target[pair.Key] = records;
            }
        }

        private static void MergeBindings(Dictionary<string, List<string>> target, Dictionary<string, object> source)
        {
            foreach (KeyValuePair<string, object> pair in source)
            {
                List<string> labels = new List<string>();
                string direct = pair.Value as string;
                if (direct != null) labels.Add(direct);
                else
                {
                    IEnumerable enumerable = pair.Value as IEnumerable;
                    if (enumerable != null) foreach (object item in enumerable) if (item != null) labels.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                }
                if (labels.Count > 0) target[pair.Key] = labels;
            }
        }

        private static string FullExistingFile(string path)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("File not found", full);
            return full;
        }

        private static string FindSoffice()
        {
            string[] candidates = new string[] {
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"
            };
            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static bool IsTextPart(string name)
        {
            return string.Equals(name, "word/document.xml", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(name, @"^word/(header|footer)\d*\.xml$", RegexOptions.IgnoreCase);
        }

        private static XDocument LoadXml(ZipArchive archive, string name)
        {
            return HVX.Report.Security.BoundedDocx.LoadXml(archive, name, LoadOptions.PreserveWhitespace);
        }

        private static XDocument LoadOrCreateRelationships(ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name);
            if (entry != null) return LoadXml(archive, name);
            return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(PR + "Relationships"));
        }

        private static void SaveXml(ZipArchive archive, string name, XDocument document)
        {
            byte[] bytes;
            using (MemoryStream memory = new MemoryStream())
            {
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Encoding = new UTF8Encoding(false);
                settings.Indent = false;
                settings.OmitXmlDeclaration = false;
                using (XmlWriter writer = XmlWriter.Create(memory, settings)) document.Save(writer);
                bytes = memory.ToArray();
            }
            ZipArchiveEntry old = archive.GetEntry(name);
            if (old != null) old.Delete();
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
        }

        private static int NextRelationshipNumber(XDocument rels)
        {
            int max = 0;
            foreach (XElement rel in rels.Root.Elements(PR + "Relationship"))
            {
                string id = (string)rel.Attribute("Id") ?? string.Empty;
                Match match = Regex.Match(id, @"rId(\d+)", RegexOptions.IgnoreCase);
                int number;
                if (match.Success && int.TryParse(match.Groups[1].Value, out number)) max = Math.Max(max, number);
            }
            return max + 1;
        }

        private static string ElementText(XElement element)
        {
            if (element == null) return string.Empty;
            return string.Concat(element.DescendantsAndSelf(W + "t").Select(t => t.Value).ToArray());
        }

        private static void SetParagraphText(XElement paragraph, string value)
        {
            XElement firstRun = paragraph.Elements(W + "r").FirstOrDefault(candidateRun => candidateRun.Descendants(W + "t").Any(textNode => !string.IsNullOrEmpty(textNode.Value)))
                ?? paragraph.Elements(W + "r").FirstOrDefault(candidateRun => candidateRun.Element(W + "rPr") != null)
                ?? paragraph.Elements(W + "r").FirstOrDefault();
            XElement runProperties = firstRun == null ? null : firstRun.Element(W + "rPr");
            paragraph.Elements(W + "r").Remove();
            paragraph.Elements(W + "hyperlink").Remove();
            XElement text = new XElement(W + "t", value ?? string.Empty);
            text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            XElement run = new XElement(W + "r");
            if (runProperties != null) run.Add(new XElement(runProperties));
            run.Add(text);
            paragraph.Add(run);
        }

        private static void SetCellText(XElement cell, string value)
        {
            XElement paragraph = cell.Elements(W + "p").FirstOrDefault();
            if (paragraph == null)
            {
                paragraph = new XElement(W + "p");
                cell.Add(paragraph);
            }
            SetParagraphText(paragraph, value);
            foreach (XElement extra in cell.Elements(W + "p").Skip(1).ToList()) extra.Remove();
        }

        private static string FirstGroup(Match match)
        {
            for (int i = 1; i < match.Groups.Count; i++) if (match.Groups[i].Success) return match.Groups[i].Value;
            return string.Empty;
        }

        private static string CleanLabel(string value)
        {
            if (value == null) return string.Empty;
            string cleaned = value.Replace("：", ":").Trim();
            cleaned = Regex.Replace(cleaned, @"\s+", string.Empty);
            cleaned = cleaned.TrimEnd(':', '=', '：');
            return cleaned;
        }

        private static string NormalizeFieldName(string value)
        {
            string cleaned = CleanLabel(value).ToLowerInvariant();
            cleaned = Regex.Replace(cleaned, @"[^a-z0-9\u4e00-\u9fff]+", "_");
            return cleaned.Trim('_');
        }

        private static bool StartsWithLabel(string text, string cleanLabel)
        {
            string normalized = CleanLabel(text);
            return normalized.StartsWith(cleanLabel, StringComparison.OrdinalIgnoreCase) && normalized.Length <= cleanLabel.Length + 80;
        }

        private static string DisplayLabel(string source, string fallback)
        {
            int colon = source.IndexOfAny(new char[] { ':', '：', '=' });
            return colon > 0 ? source.Substring(0, colon).Trim() : fallback;
        }

        private static void AddParagraphLabelCandidate(List<Dictionary<string, object>> labels, string partName, string text)
        {
            string trimmed = text == null ? string.Empty : text.Trim();
            int colon = trimmed.IndexOfAny(new char[] { ':', '：' });
            if (colon <= 0 || colon > 30) return;
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["part"] = partName;
            item["kind"] = "paragraph";
            item["label"] = trimmed.Substring(0, colon).Trim();
            item["sample_value"] = trimmed.Substring(colon + 1).Trim();
            labels.Add(item);
        }

        private static void AddTableLabelCandidates(List<Dictionary<string, object>> labels, string partName, XElement row)
        {
            List<XElement> cells = row.Elements(W + "tc").ToList();
            for (int i = 0; i + 1 < cells.Count; i++)
            {
                string label = ElementText(cells[i]).Trim();
                string value = ElementText(cells[i + 1]).Trim();
                if (label.Length == 0 || label.Length > 30 || IsMostlyNumber(label)) continue;
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["part"] = partName;
                item["kind"] = "table_adjacent_cell";
                item["label"] = CleanLabel(label);
                item["sample_value"] = value;
                labels.Add(item);
            }
        }

        private static bool IsMostlyNumber(string value)
        {
            int digits = value.Count(char.IsDigit);
            return value.Length > 0 && digits >= value.Length * 0.7;
        }

        private static bool TryParseMeasurement(string text, out double value)
        {
            string cleaned = Regex.Replace(text ?? string.Empty, @"[^0-9+\-.Ee]", string.Empty);
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static int SafeInt(double value)
        {
            return IsFinite(value) ? (int)Math.Round(value) : -1;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double NormalizeProbability(double value)
        {
            if (!IsFinite(value)) return -1;
            return value > 1.0 ? value / 100.0 : value;
        }

        private static string Sha256(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string DecodePdfLiteral(string value)
        {
            return value.Replace("\\n", " ").Replace("\\r", " ").Replace("\\t", " ").Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
        }

        private static IEnumerable<string> SplitUsefulLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0 && s.Length < 300).Take(1000);
        }

        private sealed class ProjectData
        {
            public readonly string ProjectPath;
            public readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<Dictionary<string, string>>> Repeats = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Warnings = new List<string>();
            public ProjectData(string path) { ProjectPath = path; }
        }

        private sealed class MatrixData
        {
            public readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<Dictionary<string, string>>> Repeats = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Warnings = new List<string>();
        }

        private sealed class MatrixPoint
        {
            public int RowIndex;
            public int Phase;
            public double LocationDataId;
            public double Voltage;
            public double PhaseAngle;
            public double Position;
            public double Pd;
            public double Probability;
            public double Width;
            public double Bandwidth;
            public double Auto;
            public double Selected;

            public Dictionary<string, string> ToDictionary()
            {
                Dictionary<string, string> item = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                item["row_index"] = RowIndex.ToString(CultureInfo.InvariantCulture);
                item["phase"] = Phase == 1 ? "A" : (Phase == 2 ? "B" : "C");
                item["location_data_id"] = Format(LocationDataId);
                item["voltage_kv"] = Format(Voltage);
                item["phase_angle_deg"] = Format(PhaseAngle);
                item["position_m"] = Format(Position);
                item["pd_pc"] = Format(Pd);
                item["probability"] = IsFinite(Probability) ? (NormalizeProbability(Probability) * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%" : string.Empty;
                item["width_ns"] = Format(Width);
                item["bandwidth_hz"] = Format(Bandwidth);
                item["auto_flag"] = Format(Auto);
                item["selected_flag"] = Format(Selected);
                return item;
            }

            private static string Format(double value)
            {
                return IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
            }
        }
    }
}
