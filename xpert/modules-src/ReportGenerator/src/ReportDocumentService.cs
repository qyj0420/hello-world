using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace HVX.ReportGenerator
{
    internal sealed class ReportDynamicSection
    {
        internal string Anchor = string.Empty;
        internal string Title = string.Empty;
        internal string Subtitle = string.Empty;
        internal string Body = string.Empty;
        internal string ImagePath = string.Empty;
        internal string Caption = string.Empty;
        internal bool PageBreakBefore;
    }

    internal sealed class ReportGenerationPlan
    {
        internal string ProjectPath = string.Empty;
        internal string TemplatePath = string.Empty;
        internal string OutputDocxPath = string.Empty;
        internal string OutputPdfPath = string.Empty;
        internal string EngineDllPath = string.Empty;
        internal bool Overwrite;
        internal bool GeneratePdf = true;
        internal bool OmitEmptyAppendix;
        internal double[,] LocationMatrix;
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, object> Images = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<Dictionary<string, string>> U0StatisticsRows = new List<Dictionary<string, string>>();
        internal readonly List<ReportDynamicSection> Sections = new List<ReportDynamicSection>();
        internal readonly List<string> TemporaryFiles = new List<string>();
    }

    internal sealed class ReportGenerationResult
    {
        internal bool Success;
        internal bool PdfCreated;
        internal string DocxPath = string.Empty;
        internal string PdfPath = string.Empty;
        internal string PdfProvider = string.Empty;
        internal string Error = string.Empty;
        internal string Warning = string.Empty;
        internal string EngineReply = string.Empty;
    }

    internal sealed class ReportDocumentService
    {
        static readonly JavaScriptSerializer Json = CreateSerializer();

        internal ReportGenerationResult Generate(ReportGenerationPlan plan)
        { return Generate(plan, null); }

        internal ReportGenerationResult Generate(ReportGenerationPlan plan, Action<int, string> progress)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            var result = new ReportGenerationResult
            {
                DocxPath = plan.OutputDocxPath,
                PdfPath = plan.GeneratePdf ? plan.OutputPdfPath : string.Empty
            };
            try
            {
                ValidatePlan(plan);
                if (progress != null) progress(45, "prepare");
                string enginePath = ResolveEngine(plan.EngineDllPath);
                Assembly assembly = Assembly.LoadFrom(enginePath);
                Type type = assembly.GetType("HVX.Report.Engine.ReportGenerator", true);
                object engine = Activator.CreateInstance(type);
                Dictionary<string, object> request = BuildRequest(plan);
                string effectiveTemplate = ReportDefaultTemplate.Prepare(plan.TemplatePath, plan.TemporaryFiles);
                if (plan.OmitEmptyAppendix && !plan.Sections.Any(section => string.Equals(section.Anchor, "attachment_sections", StringComparison.OrdinalIgnoreCase)))
                    effectiveTemplate = ReportOptionalAppendix.CreateTemplateCopy(effectiveTemplate, plan.TemporaryFiles);
                request["template_path"] = effectiveTemplate;
                string json = Json.Serialize(request);
                if (progress != null) progress(60, "docx");
                MethodInfo method;
                object reply;
                if (plan.LocationMatrix != null && plan.LocationMatrix.GetLength(0) > 0)
                {
                    method = type.GetMethod("GenerateReportWithMatrix", new[] { typeof(string), typeof(double[,]) });
                    reply = method.Invoke(engine, new object[] { json, plan.LocationMatrix });
                }
                else
                {
                    method = type.GetMethod("GenerateReport", new[] { typeof(string) });
                    reply = method.Invoke(engine, new object[] { json });
                }
                result.EngineReply = Convert.ToString(reply, CultureInfo.InvariantCulture);
                EnsureEngineOk(result.EngineReply);
                if (!File.Exists(plan.OutputDocxPath)) throw new IOException("The report engine did not create the DOCX output.");
                result.Success = true;
                if (!plan.GeneratePdf)
                {
                    if (progress != null) progress(100, "complete");
                    return result;
                }
                if (progress != null) progress(80, "pdf");

                string provider;
                string pdfError;
                if (TryConvertWithOfficeOrWps(plan.OutputDocxPath, plan.OutputPdfPath, plan.Overwrite, out provider, out pdfError))
                {
                    result.PdfCreated = true;
                    result.PdfProvider = provider;
                }
                else if (TryConvertWithEngine(engine, type, plan.OutputDocxPath, plan.OutputPdfPath, plan.Overwrite, out pdfError))
                {
                    result.PdfCreated = true;
                    result.PdfProvider = "LibreOffice fallback";
                }
                else
                {
                    result.Warning = pdfError;
                    result.PdfPath = string.Empty;
                }
                if (progress != null) progress(100, "complete");
            }
            catch (TargetInvocationException ex)
            {
                result.Error = (ex.InnerException ?? ex).Message;
                result.Success = false;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;
            }
            finally
            {
                foreach (string path in plan.TemporaryFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
                foreach (string directory in plan.TemporaryFiles.Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory); } catch { }
                }
            }
            return result;
        }

        static Dictionary<string, object> BuildRequest(ReportGenerationPlan plan)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in plan.Values) values[pair.Key] = pair.Value ?? string.Empty;
            object[] rows = plan.U0StatisticsRows.Select(row => {
                var item = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> pair in row) item[pair.Key] = pair.Value ?? string.Empty;
                return (object)item;
            }).ToArray();
            object[] sections = plan.Sections.Select(section => (object)new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) {
                { "anchor", section.Anchor }, { "title", section.Title }, { "subtitle", section.Subtitle },
                { "body", section.Body }, { "image_path", section.ImagePath }, { "caption", section.Caption },
                { "page_break_before", section.PageBreakBefore }, { "max_width_in", 6.7 }, { "max_height_in", 7.0 }
            }).ToArray();
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) {
                { "project_path", plan.ProjectPath },
                { "template_path", plan.TemplatePath },
                { "output_docx_path", plan.OutputDocxPath },
                { "overwrite", plan.Overwrite },
                { "values", values },
                { "images", plan.Images },
                { "repeat_tables", new Dictionary<string, object> { { "u0_statistics", rows } } },
                { "sections", sections },
                { "image_layout", new Dictionary<string, object> {
                    { "one_image_per_page", true }, { "center", true },
                    { "caption_from_filename", false }, { "replace_following_caption", true },
                    { "max_width_in", 6.7 }, { "max_height_in", 7.6 }
                } }
            };
        }

        static void ValidatePlan(ReportGenerationPlan plan)
        {
            if (string.IsNullOrWhiteSpace(plan.ProjectPath) || !Directory.Exists(plan.ProjectPath)) throw new DirectoryNotFoundException("Project folder was not found: " + plan.ProjectPath);
            if (string.IsNullOrWhiteSpace(plan.TemplatePath) || !File.Exists(plan.TemplatePath)) throw new FileNotFoundException("Report template was not found.", plan.TemplatePath);
            if (!string.Equals(Path.GetExtension(plan.TemplatePath), ".docx", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetExtension(plan.TemplatePath), ".dotx", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Report template must be .docx or .dotx.");
            if (string.IsNullOrWhiteSpace(plan.OutputDocxPath)) throw new ArgumentException("A DOCX output path is required.");
            if (plan.GeneratePdf && string.IsNullOrWhiteSpace(plan.OutputPdfPath)) throw new ArgumentException("A PDF output path is required when PDF generation is enabled.");
            HVX.Report.Security.ReportOutputSafety.EnsureDifferent(plan.TemplatePath, plan.OutputDocxPath);
            if (plan.GeneratePdf)
            {
                HVX.Report.Security.ReportOutputSafety.EnsureDifferent(plan.TemplatePath, plan.OutputPdfPath);
                HVX.Report.Security.ReportOutputSafety.EnsureDifferent(plan.OutputDocxPath, plan.OutputPdfPath);
            }
            if (File.Exists(plan.OutputDocxPath) && !plan.Overwrite) throw new IOException("DOCX output already exists.");
            if (plan.GeneratePdf && File.Exists(plan.OutputPdfPath) && !plan.Overwrite) throw new IOException("PDF output already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(plan.OutputDocxPath)));
            foreach (ReportDynamicSection section in plan.Sections)
                if (!string.IsNullOrWhiteSpace(section.ImagePath) && !File.Exists(section.ImagePath)) throw new FileNotFoundException("Report image was not found.", section.ImagePath);
            foreach (object raw in plan.Images.Values)
            {
                var image = raw as Dictionary<string, object>;
                object value;
                string path = image != null && image.TryGetValue("path", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Report image was not found.", path);
            }
        }

        static void EnsureEngineOk(string response)
        {
            Dictionary<string, object> result = Json.DeserializeObject(response) as Dictionary<string, object>;
            object ok;
            if (result == null || !result.TryGetValue("ok", out ok) || !(ok is bool) || !(bool)ok)
            {
                object error;
                string message = result != null && result.TryGetValue("error", out error) ? Convert.ToString(error, CultureInfo.InvariantCulture) : response;
                throw new InvalidOperationException(message);
            }
        }

        static string ResolveEngine(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string controlDirectory = Path.GetDirectoryName(typeof(ReportDocumentService).Assembly.Location);
            string[] candidates = {
                Path.Combine(baseDirectory, "HVX.Report.Engine.dll"),
                Path.Combine(baseDirectory, "runtime", "HVX.Report.Engine.dll"),
                Path.Combine(controlDirectory, "HVX.Report.Engine.dll"),
                Path.Combine(controlDirectory, "runtime", "HVX.Report.Engine.dll")
            };
            return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("HVX.Report.Engine.dll was not found. Set ReportEnginePath or deploy the runtime next to the control.");
        }

        static bool TryConvertWithEngine(object engine, Type type, string docx, string pdf, bool overwrite, out string error)
        {
            error = string.Empty;
            try
            {
                MethodInfo method = type.GetMethod("ConvertDocxToPdf", new[] { typeof(string) });
                if (method == null) throw new MissingMethodException(type.FullName, "ConvertDocxToPdf");
                string request = Json.Serialize(new Dictionary<string, object> {
                    { "input_docx_path", docx }, { "output_pdf_path", pdf }, { "overwrite", overwrite }, { "timeout_ms", 120000 }
                });
                string response = Convert.ToString(method.Invoke(engine, new object[] { request }), CultureInfo.InvariantCulture);
                EnsureEngineOk(response);
                return File.Exists(pdf);
            }
            catch (TargetInvocationException ex) { error = (ex.InnerException ?? ex).Message; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        internal static bool TryConvertWithOfficeOrWps(string docx, string pdf, bool overwrite, out string provider, out string error)
        {
            provider = string.Empty;
            error = string.Empty;
            // Also protect callers of this conversion entry point that did not
            // just generate their document through the local report engine.
            try {
                HVX.Report.Security.ReportOutputSafety.EnsureDifferent(docx, pdf);
                HVX.Report.Security.BoundedDocx.ValidateFile(docx);
            }
            catch (Exception ex) { error = ex.Message; return false; }
            string[] progIds = { "Word.Application", "KWps.Application", "wps.Application" };
            foreach (string progId in progIds)
            {
                string localError;
                if (TryConvertCom(progId, docx, pdf, overwrite, out localError))
                {
                    provider = progId.StartsWith("Word", StringComparison.OrdinalIgnoreCase) ? "Microsoft Office" : "WPS Office";
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(localError)) error = localError;
            }
            if (string.IsNullOrWhiteSpace(error)) error = "Neither Microsoft Office nor WPS Office is available for PDF conversion.";
            return false;
        }

        static bool TryConvertCom(string progId, string docx, string pdf, bool overwrite, out string error)
        {
            error = string.Empty;
            object application = null;
            object documents = null;
            object document = null;
            string directory = Path.GetDirectoryName(Path.GetFullPath(pdf));
            string temporary = Path.Combine(directory, ".hvx_pdf_" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                Type type = Type.GetTypeFromProgID(progId, false);
                if (type == null) return false;
                application = Activator.CreateInstance(type);
                SetProperty(application, "Visible", false);
                SetProperty(application, "DisplayAlerts", 0);
                documents = GetProperty(application, "Documents");
                document = Invoke(documents, "Open", docx, false, true);
                try { Invoke(document, "ExportAsFixedFormat", temporary, 17); }
                catch { Invoke(document, "SaveAs", temporary, 17); }
                Invoke(document, "Close", false);
                ReleaseCom(document); document = null;
                Invoke(application, "Quit");
                ReleaseCom(documents); documents = null;
                ReleaseCom(application); application = null;
                if (!File.Exists(temporary)) throw new IOException(progId + " did not create a PDF file.");
                HVX.Report.Security.ReportOutputSafety.ValidatePdfEnvelope(temporary);
                HVX.Report.Security.ReportOutputSafety.Publish(temporary, pdf, overwrite);
                return true;
            }
            catch (Exception ex)
            {
                error = progId + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (document != null) { try { Invoke(document, "Close", false); } catch { } ReleaseCom(document); }
                if (documents != null) ReleaseCom(documents);
                if (application != null) { try { Invoke(application, "Quit"); } catch { } ReleaseCom(application); }
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        static object GetProperty(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
        }

        static void SetProperty(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);
        }

        static object Invoke(object target, string name, params object[] arguments)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding, null, target, arguments, CultureInfo.InvariantCulture);
        }

        static void ReleaseCom(object value)
        {
            try { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { }
        }

        static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 100 };
            return serializer;
        }
    }
}
