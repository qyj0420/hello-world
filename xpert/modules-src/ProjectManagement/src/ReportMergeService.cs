using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using HVX.Report.Security;

namespace HVX.Controls
{
    public sealed class MergeReportItem
    {
        public string ProjectName { get; set; }
        public string TestTime { get; set; }
        public string Remark { get; set; }
        public string ReportPath { get; set; }
        public string ProjectPath { get; set; }

        public MergeReportItem()
        {
            ProjectName = TestTime = Remark = ReportPath = ProjectPath = string.Empty;
        }
    }

    public sealed class MergeReportRequest
    {
        public string OutputPath { get; set; }
        public string Introduction { get; set; }
        public MergeReportItem[] Items { get; set; }

        public MergeReportRequest()
        {
            OutputPath = Introduction = string.Empty;
            Items = new MergeReportItem[0];
        }
    }

    /// <summary>
    /// Merges Word/PDF reports through late-bound Word COM. No Office interop
    /// assembly is referenced at compile time.
    /// </summary>
    public static class ReportMergeService
    {
        const int WdAlertsNone = 0;
        const int WdDoNotSaveChanges = 0;
        const int WdFormatXmlDocument = 12;
        const int WdPageBreak = 7;
        const int WdStyleNormal = -1;
        const int WdStyleHeading1 = -2;
        const int WdStyleHeading2 = -3;
        const int WdStyleCaption = -35;
        const int WdStyleTableGrid = -155;
        const int WdFieldEmpty = -1;
        const int WdListApplyToWholeList = 0;
        const int WdWord10ListBehavior = 1;
        const int MsoAutomationSecurityForceDisable = 3;
        internal const int MaxMergeReports = 64;
        internal const long MaxSourceBytes = 64L * 1024 * 1024;
        internal const long MaxBatchBytes = 256L * 1024 * 1024;
        internal const long MaxBatchExpandedBytes = 256L * 1024 * 1024;

        static readonly Regex HeadingNumberPattern = new Regex(
            @"^\s*\d+(?:\s*[\.．]\s*\d+)*(?:\s*[、\.．]\s*|\s+)(?<title>\S.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static readonly Regex CaptionPattern = new Regex(
            @"^\s*(?<label>图|表)\s*\d+(?:\s*[\.．\-]\s*\d+)*\s*(?:[：:、\.．\-—－]\s*)?(?<title>.*?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static readonly Regex CaptionSeqFieldPattern = new Regex(
            @"^\s*SEQ\s+""?(?:图|表|Figure|Table)""?(?=\s|\\|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Creates one merged DOCX. Word automation always runs on a dedicated
        /// STA thread, including calls made from LabVIEW worker threads.
        /// </summary>
        public static ProjectOperationResult Merge(MergeReportRequest request)
        {
            MergeReportRequest snapshot;
            string validationError;
            if (!TryCreateSnapshot(request, out snapshot, out validationError))
                return ProjectOperationResult.Failure(validationError);

            string outputPath = snapshot.OutputPath;
            string outputDirectory = Path.GetDirectoryName(outputPath);
            string temporaryPath = Path.Combine(
                outputDirectory,
                "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp.docx");

            Exception failure = null;
            Thread worker = new Thread(delegate()
            {
                try
                {
                    // Validate before starting Office and hold read-only source
                    // handles until conversion ends. A path cannot be replaced
                    // with an unchecked file between validation and Word.Open.
                    using (OpenValidatedSources(snapshot.Items))
                    {
                        MergeWithWord(snapshot, temporaryPath);
                        PublishAtomically(temporaryPath, outputPath);
                    }
                }
                catch (Exception ex)
                {
                    failure = Unwrap(ex);
                    // Do not depend on the optional TRACE compiler symbol.
                    foreach (TraceListener listener in Trace.Listeners)
                    {
                        try { listener.WriteLine("Report merge failed: " + failure); }
                        catch { /* Diagnostics must not replace the failure. */ }
                    }
                }
                finally
                {
                    TryDeleteFile(temporaryPath);
                }
            });

            worker.Name = "HVX Report Merge STA";
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            worker.Join();

            if (failure != null)
                return new ProjectOperationResult { Ok = false,
                    Error = "合并报告失败。请检查报告文件后重试。", Message = failure.ToString() };

            return new ProjectOperationResult
            {
                Ok = true,
                Error = string.Empty,
                Message = "已合并 " + snapshot.Items.Length.ToString(CultureInfo.InvariantCulture) + " 个项目报告",
                OutputPaths = new[] { outputPath },
                ProcessedCount = snapshot.Items.Length,
                SkippedCount = 0
            };
        }

        static bool TryCreateSnapshot(
            MergeReportRequest request,
            out MergeReportRequest snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;

            if (request == null)
            {
                error = "合并请求不能为空。";
                return false;
            }

            if (request.Items == null || request.Items.Length < 2)
            {
                error = "合并报告至少需要选择两个存在报告的项目。";
                return false;
            }
            if (request.Items.Length > MaxMergeReports)
            {
                error = "选择的报告过多。请分批合并。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                error = "必须指定汇总报告输出路径。";
                return false;
            }

            string outputPath;
            try
            {
                outputPath = Path.GetFullPath(request.OutputPath.Trim());
            }
            catch (Exception ex)
            {
                error = "汇总报告输出路径无效：" + ex.Message;
                return false;
            }

            if (!string.Equals(Path.GetExtension(outputPath), ".docx", StringComparison.OrdinalIgnoreCase))
            {
                error = "汇总报告输出路径必须使用 .docx 扩展名。";
                return false;
            }

            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                error = "无法确定汇总报告输出目录。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception ex)
            {
                error = "无法创建汇总报告输出目录：" + ex.Message;
                return false;
            }

            List<MergeReportItem> items = new List<MergeReportItem>(request.Items.Length);
            for (int i = 0; i < request.Items.Length; i++)
            {
                MergeReportItem item = request.Items[i];
                if (item == null || string.IsNullOrWhiteSpace(item.ReportPath))
                {
                    error = "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 个项目没有可合并的报告路径。";
                    return false;
                }

                string reportPath;
                try
                {
                    reportPath = Path.GetFullPath(item.ReportPath.Trim());
                }
                catch (Exception ex)
                {
                    error = "第 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 个报告路径无效：" + ex.Message;
                    return false;
                }

                string extension = Path.GetExtension(reportPath);
                if (!string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    error = "不支持的报告格式：" + reportPath + "。仅支持 DOC、DOCX 和 PDF。";
                    return false;
                }

                if (!File.Exists(reportPath))
                {
                    error = "报告文件不存在：" + reportPath;
                    return false;
                }

                if (string.Equals(reportPath, outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    error = "汇总报告输出路径不能与源报告相同。";
                    return false;
                }

                string projectName = SafeTrim(item.ProjectName);
                if (projectName.Length == 0)
                    projectName = Path.GetFileNameWithoutExtension(reportPath);

                items.Add(new MergeReportItem
                {
                    ProjectName = projectName,
                    TestTime = SafeTrim(item.TestTime),
                    Remark = SafeTrim(item.Remark),
                    ReportPath = reportPath,
                    ProjectPath = SafeTrim(item.ProjectPath)
                });
            }

            snapshot = new MergeReportRequest
            {
                OutputPath = outputPath,
                Introduction = SafeTrim(request.Introduction),
                Items = items.ToArray()
            };
            return true;
        }

        static void MergeWithWord(MergeReportRequest request, string temporaryPath)
        {
            Type wordType = Type.GetTypeFromProgID("Word.Application", true);
            object application = null;
            object documents = null;
            object destination = null;

            try
            {
                application = Activator.CreateInstance(wordType);
                SetProperty(application, "Visible", false);
                SetProperty(application, "DisplayAlerts", WdAlertsNone);
                TrySetProperty(application, "ScreenUpdating", false);
                RequireMacroProtection(application);

                documents = GetProperty(application, "Documents");
                destination = Invoke(documents, "Add");
                TrySetProperty(destination, "UpdateStylesOnOpen", false);

                AppendParagraph(destination, "汇总报告", WdStyleHeading1);
                AppendParagraph(destination, "简介", WdStyleHeading2);
                if (request.Introduction.Length > 0)
                    AppendParagraph(destination, request.Introduction, WdStyleNormal);
                CreateIntroductionTable(destination, request.Items);

                for (int i = 0; i < request.Items.Length; i++)
                {
                    AppendPageBreak(destination);
                    AppendParagraph(destination, request.Items[i].ProjectName, WdStyleHeading2);
                    // An earlier conversion must not silently change Office's
                    // protection for a subsequent external document.
                    RequireMacroProtection(application);
                    AppendSourceDocument(documents, destination, request.Items[i].ReportPath);
                }

                ClearSeqFields(destination);
                NormalizeCaptions(destination);
                ApplyUnifiedHeadingNumbering(destination);
                UpdateAllFields(destination);
                SaveAsDocx(destination, temporaryPath);
            }
            finally
            {
                if (destination != null)
                {
                    TryInvoke(destination, "Close", WdDoNotSaveChanges);
                    SafeRelease(destination);
                }

                SafeRelease(documents);
                if (application != null)
                {
                    TryInvoke(application, "Quit", WdDoNotSaveChanges);
                    SafeRelease(application);
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        internal static void RequireMacroProtection(object application)
        {
            // Security is mandatory, unlike optional display preferences. Both
            // a rejected assignment and an unverifiable readback abort before
            // Documents.Add/Open. The caller's finally still closes Word.
            SetProperty(application, "AutomationSecurity", MsoAutomationSecurityForceDisable);
            if (GetIntProperty(application, "AutomationSecurity") != MsoAutomationSecurityForceDisable)
                throw new InvalidOperationException("Word did not confirm AutomationSecurity=ForceDisable.");
        }

        internal static IDisposable OpenValidatedSources(MergeReportItem[] items)
        {
            if (items == null || items.Length < 2 || items.Length > MaxMergeReports)
                throw new InvalidDataException("Report batch count is outside the supported bounds.");
            var held = new HeldReportSources();
            try
            {
                long totalBytes = 0, totalExpanded = 0;
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.ReportPath))
                        throw new InvalidDataException("Report source path is missing.");
                    var stream = new FileStream(item.ReportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    held.Streams.Add(stream);
                    if (stream.Length <= 0 || stream.Length > MaxSourceBytes ||
                        stream.Length > MaxBatchBytes - totalBytes)
                        throw new InvalidDataException("Report source byte budget exceeded.");
                    totalBytes += stream.Length;
                    string extension = Path.GetExtension(item.ReportPath);
                    if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                        {
                            BoundedDocx.ValidateArchive(archive);
                            if (archive.GetEntry("[Content_Types].xml") == null || archive.GetEntry("word/document.xml") == null)
                                throw new InvalidDataException("A required DOCX part is missing.");
                            long expanded = 0;
                            var buffer = new byte[64 * 1024];
                            var watch = Stopwatch.StartNew();
                            foreach (var entry in archive.Entries)
                            {
                                if (watch.ElapsedMilliseconds > BoundedDocx.MaxReadMilliseconds)
                                    throw new InvalidDataException("DOCX source validation timed out.");
                                if (entry.Length > MaxBatchExpandedBytes - totalExpanded)
                                    throw new InvalidDataException("Report batch expanded-byte budget exceeded.");
                                totalExpanded += entry.Length;
                                if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                                    entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                                {
                                    BoundedDocx.LoadXml(archive, entry.FullName, LoadOptions.None);
                                    expanded += entry.Length;
                                    continue;
                                }
                                using (var part = entry.Open())
                                {
                                    long actual = 0;
                                    int count;
                                    while ((count = part.Read(buffer, 0, buffer.Length)) != 0)
                                    {
                                        actual += count;
                                        if (actual > entry.Length || count > BoundedDocx.MaxExpandedBytes - expanded)
                                            throw new InvalidDataException("DOCX actual expanded-byte budget exceeded.");
                                        expanded += count;
                                        if (watch.ElapsedMilliseconds > BoundedDocx.MaxReadMilliseconds)
                                            throw new InvalidDataException("DOCX source validation timed out.");
                                    }
                                    if (actual != entry.Length) throw new InvalidDataException("DOCX part length mismatch.");
                                }
                            }
                        }
                    }
                    else
                    {
                        if (stream.Length > MaxBatchExpandedBytes - totalExpanded)
                            throw new InvalidDataException("Report batch byte budget exceeded.");
                        totalExpanded += stream.Length;
                        var signature = new byte[8];
                        int count = stream.Read(signature, 0, signature.Length);
                        if (string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase))
                        {
                            byte[] expected = { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 };
                            if (count != expected.Length) throw new InvalidDataException("Invalid legacy DOC signature.");
                            for (int i = 0; i < expected.Length; i++)
                                if (signature[i] != expected[i]) throw new InvalidDataException("Invalid legacy DOC signature.");
                        }
                        else if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            if (count < 5 || signature[0] != '%' || signature[1] != 'P' || signature[2] != 'D' ||
                                signature[3] != 'F' || signature[4] != '-') throw new InvalidDataException("Invalid PDF signature.");
                        }
                        else throw new InvalidDataException("Unsupported report source format.");
                    }
                }
                return held;
            }
            catch
            {
                held.Dispose();
                throw;
            }
        }

        sealed class HeldReportSources : IDisposable
        {
            internal readonly List<FileStream> Streams = new List<FileStream>();
            public void Dispose()
            {
                foreach (var stream in Streams) stream.Dispose();
                Streams.Clear();
            }
        }

        static void AppendSourceDocument(object documents, object destination, string sourcePath)
        {
            object source = null;
            object sourceContent = null;
            object formattedText = null;
            object destinationContent = null;
            object insertionRange = null;

            try
            {
                // ConfirmConversions=false, ReadOnly=true, AddToRecentFiles=false.
                // Word converts PDF input to an editable temporary document.
                source = Invoke(documents, "Open", sourcePath, false, true, false);
                ClearSeqFields(source);
                ShiftSourceHeadings(source);

                sourceContent = GetProperty(source, "Content");
                int sourceEnd = GetIntProperty(sourceContent, "End");
                if (sourceEnd > 0)
                    SetProperty(sourceContent, "End", sourceEnd - 1);

                formattedText = GetProperty(sourceContent, "FormattedText");
                destinationContent = GetProperty(destination, "Content");
                int destinationEnd = Math.Max(0, GetIntProperty(destinationContent, "End") - 1);
                insertionRange = Invoke(destination, "Range", destinationEnd, destinationEnd);
                SetProperty(insertionRange, "FormattedText", formattedText);
            }
            finally
            {
                SafeRelease(insertionRange);
                SafeRelease(destinationContent);
                SafeRelease(formattedText);
                SafeRelease(sourceContent);
                if (source != null)
                {
                    TryInvoke(source, "Close", WdDoNotSaveChanges);
                    SafeRelease(source);
                }
            }
        }

        static void ShiftSourceHeadings(object document)
        {
            object paragraphs = null;
            try
            {
                paragraphs = GetProperty(document, "Paragraphs");
                int count = GetIntProperty(paragraphs, "Count");
                for (int i = 1; i <= count; i++)
                {
                    object paragraph = null;
                    object range = null;
                    try
                    {
                        paragraph = GetIndexedProperty(paragraphs, "Item", i);
                        range = GetProperty(paragraph, "Range");
                        int oldLevel = GetHeadingLevel(range);
                        if (oldLevel < 1 || oldLevel > 9)
                            continue;

                        RemoveManualHeadingNumber(range);
                        int newLevel = Math.Min(9, oldLevel + 2);
                        SetProperty(range, "Style", -(newLevel + 1));
                        SetHeadingColorBlack(range);
                    }
                    finally
                    {
                        SafeRelease(range);
                        SafeRelease(paragraph);
                    }
                }
            }
            finally
            {
                SafeRelease(paragraphs);
            }
        }

        static int GetHeadingLevel(object range)
        {
            object paragraphFormat = null;
            try
            {
                paragraphFormat = GetProperty(range, "ParagraphFormat");
                int outlineLevel = Convert.ToInt32(
                    GetProperty(paragraphFormat, "OutlineLevel"),
                    CultureInfo.InvariantCulture);
                if (outlineLevel >= 1 && outlineLevel <= 9)
                    return outlineLevel;
            }
            catch
            {
                // Imported PDF paragraphs may not expose every Word property.
            }
            finally
            {
                SafeRelease(paragraphFormat);
            }

            object style = null;
            try
            {
                style = GetProperty(range, "Style");
                string name;
                if (style != null && Marshal.IsComObject(style))
                    name = Convert.ToString(GetProperty(style, "NameLocal"), CultureInfo.InvariantCulture);
                else
                    name = Convert.ToString(style, CultureInfo.InvariantCulture);

                Match match = Regex.Match(name ?? string.Empty, @"(?:Heading|标题)\s*([1-9])", RegexOptions.IgnoreCase);
                if (match.Success)
                    return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

                if (string.Equals(name, "Title", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "标题", StringComparison.OrdinalIgnoreCase))
                    return 1;
            }
            catch
            {
                // Unknown and custom body styles remain body text.
            }
            finally
            {
                SafeRelease(style);
            }
            return 0;
        }

        static void RemoveManualHeadingNumber(object range)
        {
            string text = Convert.ToString(GetProperty(range, "Text"), CultureInfo.InvariantCulture);
            Match match = HeadingNumberPattern.Match(TrimWordMarkers(text));
            if (!match.Success)
                return;

            int start = GetIntProperty(range, "Start");
            int end = GetIntProperty(range, "End");
            int markerCount = CountTrailingWordMarkers(text);
            object editRange = null;
            try
            {
                editRange = GetProperty(range, "Duplicate");
                SetProperty(editRange, "Start", start);
                SetProperty(editRange, "End", Math.Max(start, end - markerCount));
                SetProperty(editRange, "Text", match.Groups["title"].Value.Trim());
            }
            finally
            {
                SafeRelease(editRange);
            }
        }

        static void AppendParagraph(object document, string text, int style)
        {
            object content = null;
            object insertionRange = null;
            object paragraphRange = null;
            try
            {
                content = GetProperty(document, "Content");
                int start = Math.Max(0, GetIntProperty(content, "End") - 1);
                insertionRange = Invoke(document, "Range", start, start);
                string value = (text ?? string.Empty).Replace("\0", string.Empty) + "\r";
                SetProperty(insertionRange, "Text", value);

                paragraphRange = Invoke(document, "Range", start, start + value.Length);
                SetProperty(paragraphRange, "Style", style);
                if (style <= WdStyleHeading1 && style >= -10)
                    SetHeadingColorBlack(paragraphRange);
            }
            finally
            {
                SafeRelease(paragraphRange);
                SafeRelease(insertionRange);
                SafeRelease(content);
            }
        }

        static void AppendPageBreak(object document)
        {
            object content = null;
            object range = null;
            try
            {
                content = GetProperty(document, "Content");
                int end = Math.Max(0, GetIntProperty(content, "End") - 1);
                range = Invoke(document, "Range", end, end);
                Invoke(range, "InsertBreak", WdPageBreak);
            }
            finally
            {
                SafeRelease(range);
                SafeRelease(content);
            }
        }

        static void CreateIntroductionTable(object document, MergeReportItem[] items)
        {
            object content = null;
            object range = null;
            object tables = null;
            object table = null;
            try
            {
                content = GetProperty(document, "Content");
                int end = Math.Max(0, GetIntProperty(content, "End") - 1);
                range = Invoke(document, "Range", end, end);
                tables = GetProperty(document, "Tables");
                table = Invoke(tables, "Add", range, items.Length + 1, 4);

                TrySetProperty(table, "Style", WdStyleTableGrid);
                TrySetProperty(table, "TopPadding", 4.0f);
                TrySetProperty(table, "BottomPadding", 4.0f);
                TrySetProperty(table, "LeftPadding", 5.0f);
                TrySetProperty(table, "RightPadding", 5.0f);
                TryInvoke(table, "AutoFitBehavior", 2);

                SetTableCell(table, 1, 1, "序号", true, true);
                SetTableCell(table, 1, 2, "试验时间", true, true);
                SetTableCell(table, 1, 3, "项目名称", true, false);
                SetTableCell(table, 1, 4, "备注", true, false);

                for (int i = 0; i < items.Length; i++)
                {
                    int row = i + 2;
                    SetTableCell(table, row, 1, (i + 1).ToString(CultureInfo.InvariantCulture), false, true);
                    SetTableCell(table, row, 2, items[i].TestTime, false, true);
                    SetTableCell(table, row, 3, items[i].ProjectName, false, false);
                    SetTableCell(table, row, 4, items[i].Remark, false, false);
                }

                FormatHeaderRow(table);
                EnsureParagraphAfterTable(document, table);
            }
            finally
            {
                SafeRelease(table);
                SafeRelease(tables);
                SafeRelease(range);
                SafeRelease(content);
            }
        }

        static void SetTableCell(object table, int row, int column, string value, bool header, bool centered)
        {
            object cell = null;
            object range = null;
            object font = null;
            object paragraphFormat = null;
            try
            {
                cell = Invoke(table, "Cell", row, column);
                TrySetProperty(cell, "VerticalAlignment", 1);
                range = GetProperty(cell, "Range");
                SetProperty(range, "Text", value ?? string.Empty);
                font = GetProperty(range, "Font");
                SetProperty(font, "Bold", header ? -1 : 0);
                if (header)
                    TrySetProperty(font, "Color", 16777215);

                paragraphFormat = GetProperty(range, "ParagraphFormat");
                SetProperty(paragraphFormat, "Alignment", centered ? 1 : 0);
            }
            finally
            {
                SafeRelease(paragraphFormat);
                SafeRelease(font);
                SafeRelease(range);
                SafeRelease(cell);
            }
        }

        static void FormatHeaderRow(object table)
        {
            object rows = null;
            object row = null;
            object cells = null;
            object shading = null;
            try
            {
                rows = GetProperty(table, "Rows");
                row = GetIndexedProperty(rows, "Item", 1);
                TrySetProperty(row, "HeadingFormat", -1);
                cells = GetProperty(row, "Cells");
                shading = GetProperty(cells, "Shading");
                // RGB(0, 84, 115): restrained dark electrical blue.
                TrySetProperty(shading, "BackgroundPatternColor", 7558144);
            }
            catch
            {
                // The table remains usable with the built-in grid style.
            }
            finally
            {
                SafeRelease(shading);
                SafeRelease(cells);
                SafeRelease(row);
                SafeRelease(rows);
            }
        }

        static void EnsureParagraphAfterTable(object document, object table)
        {
            object tableRange = null;
            object insertionRange = null;
            try
            {
                tableRange = GetProperty(table, "Range");
                int end = GetIntProperty(tableRange, "End");
                insertionRange = Invoke(document, "Range", end, end);
                SetProperty(insertionRange, "Text", "\r");
            }
            finally
            {
                SafeRelease(insertionRange);
                SafeRelease(tableRange);
            }
        }

        static void ClearSeqFields(object document)
        {
            object fields = null;
            try
            {
                fields = GetProperty(document, "Fields");
                int count = GetIntProperty(fields, "Count");
                for (int i = count; i >= 1; i--)
                {
                    object field = null;
                    object code = null;
                    try
                    {
                        field = GetIndexedProperty(fields, "Item", i);
                        code = GetProperty(field, "Code");
                        string codeText = Convert.ToString(GetProperty(code, "Text"), CultureInfo.InvariantCulture);
                        if (CaptionSeqFieldPattern.IsMatch(codeText ?? string.Empty))
                            Invoke(field, "Unlink");
                    }
                    catch
                    {
                        // A malformed imported field must not abort the merge.
                    }
                    finally
                    {
                        SafeRelease(code);
                        SafeRelease(field);
                    }
                }
            }
            finally
            {
                SafeRelease(fields);
            }
        }

        static void ApplyUnifiedHeadingNumbering(object document)
        {
            object listTemplates = null;
            object listTemplate = null;
            object levels = null;
            object styles = null;
            object paragraphs = null;

            try
            {
                listTemplates = GetProperty(document, "ListTemplates");
                listTemplate = Invoke(
                    listTemplates,
                    "Add",
                    true,
                    "HVX Unified Headings " + Guid.NewGuid().ToString("N"));
                levels = GetProperty(listTemplate, "ListLevels");
                styles = GetProperty(document, "Styles");

                for (int level = 1; level <= 9; level++)
                {
                    object listLevel = null;
                    object headingStyle = null;
                    try
                    {
                        listLevel = GetIndexedProperty(levels, "Item", level);
                        SetProperty(listLevel, "NumberFormat", BuildHeadingNumberFormat(level));
                        SetProperty(listLevel, "TrailingCharacter", 0);
                        SetProperty(listLevel, "NumberStyle", 0);
                        SetProperty(listLevel, "NumberPosition", (level - 1) * 18.0f);
                        SetProperty(listLevel, "Alignment", 0);
                        SetProperty(listLevel, "TextPosition", level * 18.0f);
                        SetProperty(listLevel, "StartAt", 1);
                        SetProperty(listLevel, "ResetOnHigher", level == 1 ? 0 : level - 1);

                        headingStyle = GetIndexedProperty(styles, "Item", -(level + 1));
                        string styleName = Convert.ToString(
                            GetProperty(headingStyle, "NameLocal"),
                            CultureInfo.InvariantCulture);
                        if (!string.IsNullOrWhiteSpace(styleName))
                            TrySetProperty(listLevel, "LinkedStyle", styleName);
                    }
                    finally
                    {
                        SafeRelease(headingStyle);
                        SafeRelease(listLevel);
                    }
                }

                paragraphs = GetProperty(document, "Paragraphs");
                int paragraphCount = GetIntProperty(paragraphs, "Count");
                for (int i = 1; i <= paragraphCount; i++)
                {
                    object paragraph = null;
                    object range = null;
                    object listFormat = null;
                    try
                    {
                        paragraph = GetIndexedProperty(paragraphs, "Item", i);
                        range = GetProperty(paragraph, "Range");
                        int level = GetHeadingLevel(range);
                        if (level < 1 || level > 9)
                            continue;

                        listFormat = GetProperty(range, "ListFormat");
                        Invoke(
                            listFormat,
                            "ApplyListTemplateWithLevel",
                            listTemplate,
                            true,
                            WdListApplyToWholeList,
                            WdWord10ListBehavior,
                            level);
                        SetHeadingColorBlack(range);
                    }
                    finally
                    {
                        SafeRelease(listFormat);
                        SafeRelease(range);
                        SafeRelease(paragraph);
                    }
                }
            }
            finally
            {
                SafeRelease(paragraphs);
                SafeRelease(styles);
                SafeRelease(levels);
                SafeRelease(listTemplate);
                SafeRelease(listTemplates);
            }
        }

        static string BuildHeadingNumberFormat(int level)
        {
            string[] tokens = new string[level];
            for (int i = 0; i < level; i++)
                tokens[i] = "%" + (i + 1).ToString(CultureInfo.InvariantCulture);
            return string.Join(".", tokens);
        }

        static void NormalizeCaptions(object document)
        {
            object paragraphs = null;
            try
            {
                paragraphs = GetProperty(document, "Paragraphs");
                int count = GetIntProperty(paragraphs, "Count");
                for (int i = 1; i <= count; i++)
                {
                    object paragraph = null;
                    object range = null;
                    try
                    {
                        paragraph = GetIndexedProperty(paragraphs, "Item", i);
                        range = GetProperty(paragraph, "Range");
                        string text = Convert.ToString(GetProperty(range, "Text"), CultureInfo.InvariantCulture);
                        Match match = CaptionPattern.Match(TrimWordMarkers(text));
                        if (!match.Success)
                            continue;

                        ReplaceCaptionWithSeqField(
                            document,
                            range,
                            match.Groups["label"].Value,
                            match.Groups["title"].Value.Trim());
                    }
                    finally
                    {
                        SafeRelease(range);
                        SafeRelease(paragraph);
                    }
                }
            }
            finally
            {
                SafeRelease(paragraphs);
            }
        }

        static void ReplaceCaptionWithSeqField(object document, object paragraphRange, string label, string title)
        {
            int start = GetIntProperty(paragraphRange, "Start");
            int end = GetIntProperty(paragraphRange, "End");
            string text = Convert.ToString(GetProperty(paragraphRange, "Text"), CultureInfo.InvariantCulture);
            int markerCount = CountTrailingWordMarkers(text);
            object contentRange = null;
            object fieldRange = null;
            object fields = null;
            object field = null;
            object refreshedParagraph = null;

            try
            {
                contentRange = Invoke(document, "Range", start, Math.Max(start, end - markerCount));
                string replacement = label + "  " + title;
                SetProperty(contentRange, "Text", replacement);

                fieldRange = Invoke(document, "Range", start + 2, start + 2);
                fields = GetProperty(document, "Fields");
                field = Invoke(fields, "Add", fieldRange, WdFieldEmpty, "SEQ " + label + " \\* ARABIC", true);

                refreshedParagraph = Invoke(document, "Range", start, start + replacement.Length);
                TrySetProperty(refreshedParagraph, "Style", WdStyleCaption);
            }
            finally
            {
                SafeRelease(refreshedParagraph);
                SafeRelease(field);
                SafeRelease(fields);
                SafeRelease(fieldRange);
                SafeRelease(contentRange);
            }
        }

        static void UpdateAllFields(object document)
        {
            object fields = null;
            object storyRanges = null;
            try
            {
                fields = GetProperty(document, "Fields");
                TryInvoke(fields, "Update");

                storyRanges = GetProperty(document, "StoryRanges");
                for (int storyType = 1; storyType <= 17; storyType++)
                {
                    object story = null;
                    try
                    {
                        story = GetIndexedProperty(storyRanges, "Item", storyType);
                        int guard = 0;
                        while (story != null && guard++ < 100)
                        {
                            object storyFields = null;
                            object nextStory = null;
                            try
                            {
                                storyFields = GetProperty(story, "Fields");
                                TryInvoke(storyFields, "Update");
                                nextStory = GetProperty(story, "NextStoryRange");
                            }
                            catch
                            {
                                SafeRelease(nextStory);
                                nextStory = null;
                            }
                            finally
                            {
                                SafeRelease(storyFields);
                            }

                            SafeRelease(story);
                            story = nextStory;
                        }
                    }
                    catch
                    {
                        SafeRelease(story);
                    }
                }
            }
            finally
            {
                SafeRelease(storyRanges);
                SafeRelease(fields);
            }
        }

        static void SaveAsDocx(object document, string path)
        {
            try
            {
                Invoke(document, "SaveAs2", path, WdFormatXmlDocument);
            }
            catch (MissingMethodException)
            {
                Invoke(document, "SaveAs", path, WdFormatXmlDocument);
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException is MissingMethodException)
                    Invoke(document, "SaveAs", path, WdFormatXmlDocument);
                else
                    throw;
            }
        }

        static void PublishAtomically(string temporaryPath, string outputPath)
        {
            if (!File.Exists(temporaryPath))
                throw new IOException("Microsoft Word 未生成临时汇总报告。");

            string backupPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".bak";
            bool hadExisting = File.Exists(outputPath);
            try
            {
                if (hadExisting)
                {
                    try
                    {
                        File.Replace(temporaryPath, outputPath, backupPath, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Move(outputPath, backupPath);
                        File.Move(temporaryPath, outputPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, outputPath);
                }
                TryDeleteFile(backupPath);
            }
            catch
            {
                if (!File.Exists(outputPath) && File.Exists(backupPath))
                    File.Move(backupPath, outputPath);
                throw;
            }
            finally
            {
                if (File.Exists(outputPath))
                    TryDeleteFile(backupPath);
            }
        }

        static void SetHeadingColorBlack(object range)
        {
            object font = null;
            try
            {
                font = GetProperty(range, "Font");
                TrySetProperty(font, "Color", 0);
            }
            finally
            {
                SafeRelease(font);
            }
        }

        static string SafeTrim(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        static string TrimWordMarkers(string value)
        {
            return (value ?? string.Empty).TrimEnd('\r', '\a', '\n');
        }

        static int CountTrailingWordMarkers(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            int count = 0;
            for (int i = value.Length - 1; i >= 0; i--)
            {
                char c = value[i];
                if (c != '\r' && c != '\a' && c != '\n')
                    break;
                count++;
            }
            return count;
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort cleanup; preserve the primary operation result.
            }
        }

        static Exception Unwrap(Exception exception)
        {
            Exception current = exception;
            while (current is TargetInvocationException && current.InnerException != null)
                current = current.InnerException;
            return current;
        }

        static object Invoke(object target, string member, params object[] arguments)
        {
            if (target == null)
                throw new ArgumentNullException("target");

            return target.GetType().InvokeMember(
                member,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod |
                    BindingFlags.OptionalParamBinding,
                null,
                target,
                arguments,
                CultureInfo.InvariantCulture);
        }

        static void TryInvoke(object target, string member, params object[] arguments)
        {
            if (target == null)
                return;
            try { Invoke(target, member, arguments); }
            catch { }
        }

        static object GetProperty(object target, string property)
        {
            if (target == null)
                throw new ArgumentNullException("target");

            return target.GetType().InvokeMember(
                property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty |
                    BindingFlags.OptionalParamBinding,
                null,
                target,
                null,
                CultureInfo.InvariantCulture);
        }

        static object GetIndexedProperty(object target, string property, params object[] indexes)
        {
            if (target == null)
                throw new ArgumentNullException("target");

            return target.GetType().InvokeMember(
                property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.InvokeMethod |
                    BindingFlags.OptionalParamBinding,
                null,
                target,
                indexes,
                CultureInfo.InvariantCulture);
        }

        static int GetIntProperty(object target, string property)
        {
            return Convert.ToInt32(GetProperty(target, property), CultureInfo.InvariantCulture);
        }

        static void SetProperty(object target, string property, object value)
        {
            if (target == null)
                throw new ArgumentNullException("target");

            target.GetType().InvokeMember(
                property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty |
                    BindingFlags.OptionalParamBinding,
                null,
                target,
                new[] { value },
                CultureInfo.InvariantCulture);
        }

        static void TrySetProperty(object target, string property, object value)
        {
            if (target == null)
                return;
            try { SetProperty(target, property, value); }
            catch { }
        }

        static void SafeRelease(object value)
        {
            if (value == null || !Marshal.IsComObject(value))
                return;
            try { Marshal.FinalReleaseComObject(value); }
            catch { }
        }
    }
}
