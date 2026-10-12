using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;

// Uses the actual distributed report control and engine; all outputs are new QA fixtures.
internal static class ReportAppendixLayoutSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static int checks;
    static Assembly module;
    static string qa;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr value, IntPtr parameter);
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, Hidden).GetValue(owner); }
    static void SetField(object owner, string name, object value) { owner.GetType().GetField(name, Hidden).SetValue(owner, value); }
    static object Call(object owner, string name, params object[] args)
    { return owner.GetType().GetMethod(name, Hidden | BindingFlags.Public).Invoke(owner, args); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name).SetValue(owner, value, null); }
    static string Plain(XElement element) { return string.Concat(element.Descendants(W + "t").Select(e => e.Value)); }
    static IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes)
    { foreach (TreeNode node in nodes) { yield return node; foreach (var child in Nodes(node.Nodes)) yield return child; } }
    static void Pump() { Application.DoEvents(); }

    [STAThread] static int Main(string[] args)
    {
        try
        {
            string release = Path.GetFullPath(args[0]);
            qa = Path.Combine(Path.GetFullPath(args[1]), "report-appendix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            string folder = Path.Combine(release, "modules", "ReportGenerator");
            module = Assembly.LoadFrom(Path.Combine(folder, "Xiongxin.OWTS.ReportGenerator.dll"));
            Application.EnableVisualStyles();
            TestTree(); TestOptionalChapter(); TestRealGeneration(folder);
            Console.WriteLine("REPORT_APPENDIX_LAYOUT_PASS=" + checks);
            Console.WriteLine("EVIDENCE=" + qa);
            Console.WriteLine("PDF_CONVERSION_NOT_RUN=True;HARDWARE_ACCESSED=False;REAL_PROJECT_WRITTEN=False");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ex.InnerException : ex); return 1; }
    }

    static void TestTree()
    {
        Type treeType = module.GetType("HVX.ReportGenerator.ReportTreeView", true);
        Type theme = module.GetType("HVX.ReportGenerator.ReportTheme", true);
        using (var form = new Form { Text = "Report tree QA — synthetic only", ClientSize = new Size(640, 560), StartPosition = FormStartPosition.CenterScreen })
        using (var tree = (TreeView)Activator.CreateInstance(treeType, true))
        {
            tree.Dock = DockStyle.Fill; tree.CheckBoxes = true; tree.ShowNodeToolTips = true;
            theme.GetMethod("StyleTree", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { tree });
            form.Controls.Add(tree); form.Show(); Pump();
            Check(tree.DrawMode == TreeViewDrawMode.Normal, "Native checkbox/text clipping owns the scrolled paint region; no out-of-row owner draw.");
            int customPaints = 0; tree.DrawNode += (s, e) => customPaints++;
            string[] titles = { "测试数据 · A相 · 2026-09-30 · 定位结果", "Test data · Phase A · 2026-09-30 · location result", "測試數據 · A相 · 2026-09-30 · 定位結果" };
            foreach (int language in new[] { 0, 1, 2 })
            foreach (float size in new[] { 9.5f, 14f, 20f })
            {
                using (var font = new Font("Microsoft YaHei UI", size, FontStyle.Regular))
                {
                    tree.Font = font;
                    tree.BeginUpdate(); tree.Nodes.Clear();
                    var root = tree.Nodes.Add(titles[language]);
                    for (int i = 0; i < 140; i++)
                        root.Nodes.Add(new TreeNode(string.Format("#{0:000} · {1}", i, titles[language])) { Checked = i % 3 == 0, ForeColor = i % 7 == 0 ? Color.Gray : Color.FromArgb(24, 51, 77) });
                    root.Expand(); tree.EndUpdate(); Pump();
                    Check(tree.ItemHeight >= tree.Font.Height + 6, "Row height follows enlarged font: " + size);
                    foreach (int index in new[] { 0, 49, 112, 30, 138, 0 })
                    {
                        var target = root.Nodes[index]; target.EnsureVisible(); tree.TopNode = target;
                        SendMessage(tree.Handle, 0x115, new IntPtr(1), IntPtr.Zero); // WM_VSCROLL / SB_LINEDOWN
                        Pump(); CheckVisibleRows(tree);
                    }
                    root.Nodes[49].EnsureVisible(); tree.TopNode = root.Nodes[49]; tree.SelectedNode = root.Nodes[51]; Pump();
                    bool original = root.Nodes[51].Checked; root.Nodes[51].Checked = !original; Pump();
                    Check(root.Nodes[51].Checked != original, "Scrolling preserves independent checkbox selection.");
                    using (var bitmap = new Bitmap(tree.Width, tree.Height))
                    { tree.DrawToBitmap(bitmap, tree.ClientRectangle); bitmap.Save(Path.Combine(qa, "tree-" + language + "-" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"), ImageFormat.Png); }
                    tree.Font = SystemFonts.MessageBoxFont;
                }
            }
            Check(customPaints == 0, "Scrolling and screenshots never run the former owner-draw path.");
            form.Close();
        }
    }

    static void CheckVisibleRows(TreeView tree)
    {
        var node = tree.TopNode; int bottom = int.MinValue, count = 0;
        while (node != null && node.Bounds.Top < tree.ClientSize.Height)
        {
            Rectangle bounds = node.Bounds;
            if (bounds.Bottom > 0)
            {
                Check(bounds.Top >= bottom && bounds.Height >= tree.Font.Height, "Native row label stays within its own non-overlapping item bounds.");
                if (bounds.Top >= 0 && bounds.Bottom <= tree.ClientSize.Height)
                    Check(tree.GetNodeAt(Math.Max(2, Math.Min(tree.Width - 22, bounds.Left + 4)), bounds.Top + bounds.Height / 2) == node, "Scrolled text hit test returns the displayed row.");
                bottom = bounds.Bottom; count++;
            }
            node = node.NextVisibleNode;
        }
        Check(count > 1, "Several scrolled rows remain visible.");
    }

    static XElement Paragraph(string text) { return new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", text))); }
    static XDocument Fixture(bool ordinaryReference)
    {
        var heading = Paragraph("06 {{label.appendix_figures}}");
        heading.AddFirst(new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", "Heading1")), new XElement(W + "pageBreakBefore")));
        heading.AddFirst(new XElement(W + "bookmarkStart", new XAttribute(W + "id", "1"), new XAttribute(W + "name", "AppendixHeading")));
        heading.Add(new XElement(W + "bookmarkEnd", new XAttribute(W + "id", "1")));
        var toc = new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", "TOC1"))),
            new XElement(W + "hyperlink", new XAttribute(W + "anchor", "AppendixHeading"), new XElement(W + "r", new XElement(W + "t", "06 Appendix"))));
        var body = new XElement(W + "body", toc, Paragraph("05 Conclusion remains"),
            new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))),
            heading, Paragraph("{{report.appendix_intro}}"), Paragraph("{{dynamic:attachment_sections}}"), new XElement(W + "sectPr"));
        if (ordinaryReference) body.AddFirst(new XElement(W + "p", new XElement(W + "fldSimple", new XAttribute(W + "instr", " REF AppendixHeading "), new XElement(W + "r", new XElement(W + "t", "See Appendix")))));
        return new XDocument(new XElement(W + "document", body));
    }

    static void TestOptionalChapter()
    {
        var method = module.GetType("HVX.ReportGenerator.ReportOptionalAppendix", true).GetMethod("RemoveEmptyAppendix", BindingFlags.Static | BindingFlags.NonPublic);
        XDocument document = Fixture(false); method.Invoke(null, new object[] { document });
        string text = Plain(document.Root);
        Check(text == "05 Conclusion remains", "Empty appendix title, introduction, anchor and TOC entry are removed together.");
        Check(!document.Descendants(W + "pageBreakBefore").Any() && !document.Descendants(W + "br").Any(), "No separate or heading-owned empty page break survives.");
        Check(document.Descendants(W + "sectPr").Count() == 1, "Page setup is retained.");
        Check(!document.Descendants(W + "bookmarkStart").Any(), "No orphan appendix bookmark remains.");
        method.Invoke(null, new object[] { document });
        Check(Plain(document.Root) == text, "Removal is idempotent.");
        bool rejected = false;
        try { method.Invoke(null, new object[] { Fixture(true) }); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
        Check(rejected, "Custom non-TOC cross-reference is rejected rather than left dangling.");
        var boundary = Fixture(false);
        boundary.Root.Element(W + "body").Elements(W + "p").First(p => Plain(p).Contains("report.appendix_intro"))
            .AddFirst(new XElement(W + "pPr", new XElement(W + "sectPr")));
        rejected = false;
        try { method.Invoke(null, new object[] { boundary }); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
        Check(rejected, "A custom section-boundary crossing cannot silently change report pagination.");
    }

    static void TestRealGeneration(string folder)
    {
        string template = Path.Combine(folder, "templates", "EPFT_OWTS_Report_Template_v1.0.docx");
        byte[] retainedTemplate = File.ReadAllBytes(template);
        string project = Path.Combine(qa, "SyntheticProject"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ITEM.in"), "电缆长度\t414.8\r\n相电压\t8.7\r\n始端地点\tAppendixStart\r\n终端地点\tAppendixEnd\r\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(project, "CABLE.in"), "0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t接头1\t户内冷缩\t80\r\n1\t终端\t户内冷缩\t414.8\r\n", Encoding.UTF8);
        var projectBefore = Directory.GetFiles(project).ToDictionary(path => path, File.ReadAllBytes);
        using (var control = (Control)Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGeneratorControl", true)))
        {
            Set(control, "PromptAnalysisOnLoad", false);
            Check((bool)Call(control, "LoadProject", project), "Synthetic report project loads.");
            var matrix = new double[3, 36];
            for (int i = 0; i < 3; i++)
            { matrix[i, 0] = 1; matrix[i, 1] = i + 1; matrix[i, 2] = 8.7 + i; matrix[i, 5] = 414.8; matrix[i, 13] = 80; matrix[i, 15] = 100 + i * 20; matrix[i, 16] = 110 + i * 20; matrix[i, 22] = .95; matrix[i, 35] = 1; }
            Call(control, "SetLocationMatrix", matrix);
            Set(control, "ConclusionText", "Synthetic test conclusion only.");
            var tree = (TreeView)Field(control, "tree");
            foreach (TreeNode node in tree.Nodes) node.Checked = false;
            tree.Nodes[0].Checked = true;
            object emptyPlan = Plan(control, "no-appendix.docx");
            Check((bool)Field(emptyPlan, "OmitEmptyAppendix"), "No selected valid figure requests whole chapter omission.");
            var values = (IDictionary)Field(emptyPlan, "Values");
            string appendixTitle = Convert.ToString(values["label.appendix_figures"]);
            Check(!string.IsNullOrWhiteSpace(appendixTitle), "Use the actual localized appendix heading for verification.");
            var noAppendix = Generate(control, emptyPlan);
            Check(!Headings(noAppendix).Any(t => t.Contains(appendixTitle)), "Generated DOCX has no empty 06 appendix heading.");
            Check(!Plain(noAppendix.Root).Contains("本次未选择附图") && !Plain(noAppendix.Root).Contains("No appendix"), "No empty appendix placeholder survives.");
            Check(Headings(noAppendix).Any(t => t.StartsWith("05")), "Preceding conclusion numbering is unchanged.");

            TreeNode available = Nodes(tree.Nodes).FirstOrDefault(n => n.Nodes.Count == 0 && n.Tag != null &&
                Convert.ToString(n.Tag.GetType().GetProperty("Key").GetValue(n.Tag, null)).StartsWith("trend_ChargeVoltage") &&
                (bool)Call(control, "IsSelectionAvailable", n.Tag));
            Check(available != null, "A real source-backed trend attachment is available.");
            available.Checked = true;
            Check(available.Checked, "Actual UI checkbox accepts source-backed attachment.");
            object withPlan = Plan(control, "with-appendix.docx");
            Check(!(bool)Field(withPlan, "OmitEmptyAppendix"), "A valid selected figure retains the chapter.");
            var withAppendix = Generate(control, withPlan);
            Check(Headings(withAppendix).Count(t => t.Contains(appendixTitle)) == 1, "Populated 06 chapter occurs once.");
            Check(withAppendix.Descendants(W + "drawing").Count() > noAppendix.Descendants(W + "drawing").Count(), "Real selected attachment adds a drawing to the DOCX.");
            Check(!Plain(withAppendix.Root).Contains("{{dynamic:attachment_sections}}"), "Attachment anchor was expanded.");
            Check(Plain(withAppendix.Root).Contains("趋势统计图 · n-Q-U") && !Plain(withAppendix.Root).Contains("ChargeVoltage"), "Chinese exported attachment uses localized category, not internal enum name.");

            // A stale checked item can no longer supply data at generation time.
            Call(control, "SetLocationMatrix", new double[0, 36]);
            SetField(control, "changingChecks", true);
            try { foreach (var node in Nodes(tree.Nodes).Where(n => n.Name.StartsWith("trend_"))) node.Checked = true; }
            finally { SetField(control, "changingChecks", false); }
            object unavailablePlan = Plan(control, "unavailable-appendix.docx");
            Check((bool)Field(unavailablePlan, "OmitEmptyAppendix"), "Checked but unavailable figures cannot create an empty appendix.");
            Check(!Headings(Generate(control, unavailablePlan)).Any(t => t.Contains(appendixTitle)), "Unavailable-only appendix is omitted in actual DOCX.");

            Set(control, "LanguageIndex", 1);
            object englishPlan = Plan(control, "no-appendix-english.docx");
            string englishHeading = Convert.ToString(((IDictionary)Field(englishPlan, "Values"))["label.appendix_figures"]);
            Check(!Headings(Generate(control, englishPlan)).Any(t => t.Contains(englishHeading)), "Semantic omission works in English without guessing heading text.");
            Type trendKind = module.GetType("HVX.ReportGenerator.TrendChartKind", true);
            string[] kinds = { "CountVoltage", "ChargeVoltage", "DacTanDelta" };
            string[] identifiers = { "n-N-U", "n-Q-U", "DAC-TD" };
            for (int language = 0; language < 20; language++)
            {
                Set(control, "LanguageIndex", language);
                for (int index = 0; index < kinds.Length; index++)
                {
                    string subtitle = Convert.ToString(Call(control, "LocalizedTrendSubtitle", Enum.Parse(trendKind, kinds[index])));
                    Check(subtitle.EndsWith(" · " + identifiers[index], StringComparison.Ordinal) && !subtitle.Contains(kinds[index]) && subtitle.Length > identifiers[index].Length + 3,
                        "Localized trend caption preserves chart identifier without leaking enum: " + language + "/" + kinds[index]);
                }
            }
        }
        Check(retainedTemplate.SequenceEqual(File.ReadAllBytes(template)), "Original bundled template is byte-for-byte unchanged.");
        Check(projectBefore.Count == Directory.GetFiles(project).Length, "No report data added to the source project.");
        foreach (var file in projectBefore) Check(file.Value.SequenceEqual(File.ReadAllBytes(file.Key)), "Synthetic source remains byte-for-byte unchanged.");
    }

    static IEnumerable<string> Headings(XDocument doc)
    { return doc.Descendants(W + "p").Where(p => ((string)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "").StartsWith("Heading", StringComparison.OrdinalIgnoreCase)).Select(Plain); }
    static object Plan(Control control, string name)
    {
        object plan = Call(control, "BuildGenerationPlan", Path.Combine(qa, name), Path.Combine(qa, Path.ChangeExtension(name, ".pdf")), false);
        SetField(plan, "GeneratePdf", false); return plan;
    }
    static XDocument Generate(Control control, object plan)
    {
        object service = Field(control, "documentService");
        var method = service.GetType().GetMethod("Generate", Hidden, null, new[] { plan.GetType() }, null);
        object result = method.Invoke(service, new[] { plan });
        Check((bool)Field(result, "Success"), "Actual report generation succeeds: " + Convert.ToString(Field(result, "Error")));
        foreach (string temporary in (IEnumerable)Field(plan, "TemporaryFiles")) Check(!File.Exists(temporary), "Private template/images are cleaned after generation.");
        string output = (string)Field(plan, "OutputDocxPath");
        Check(!File.Exists(Path.ChangeExtension(output, ".pdf")), "Word-only regression does not invoke a PDF backend.");
        using (var archive = ZipFile.OpenRead(output))
        using (var stream = archive.GetEntry("word/document.xml").Open()) return XDocument.Load(stream);
    }
}
