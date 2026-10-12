using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Text.RegularExpressions;
using HVX.Report.Engine;

internal static class SmokeTest
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    private static int Main(string[] args)
    {
        try
        {
            string parent = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "hvx-report-smoke");
            Directory.CreateDirectory(parent);
            string root = Path.Combine(parent, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            string project = Path.Combine(root, "project");
            Directory.CreateDirectory(project);
            WriteProjectFiles(project);
            string template = Path.Combine(project, "vendor-template.dotx");
            CreateTemplate(template);
            string countImage = Path.Combine(root, "all-position-count.png");
            string chargeImage = Path.Combine(root, "all-position-charge.png");
            CreateChartImage(countImage, "ALL - N / L", Color.FromArgb(220, 80, 55));
            CreateChartImage(chargeImage, "ALL - Q / L", Color.FromArgb(45, 135, 95));
            string output = Path.Combine(root, "generated.docx");

            ReportGenerator engine = new ReportGenerator();
            RequireOk(engine.Health(), "health");
            string inspectProject = engine.InspectProjectFolder(project);
            RequireOk(inspectProject, "project inspection");
            if (inspectProject.IndexOf("826.9", StringComparison.Ordinal) < 0) throw new Exception("Project inspection did not expose cable length.");
            string analyze = engine.AnalyzeTemplate(template);
            RequireOk(analyze, "template analysis");
            if (analyze.IndexOf("report.title", StringComparison.Ordinal) < 0) throw new Exception("Template marker was not discovered.");

            Dictionary<string, object> request = new Dictionary<string, object>();
            request["project_path"] = project;
            request["template_path"] = template;
            request["output_docx_path"] = output;
            request["overwrite"] = true;
            request["values"] = new Dictionary<string, object> {
                { "report.title", "振荡波局部放电测试报告" },
                { "custom.conclusion", "重点关注A相123.4 m位置。" }
            };
            request["images"] = new Dictionary<string, object> {
                { "count_chart", new Dictionary<string, object> { { "path", countImage }, { "caption", "\u56fe 5.1. ALL-\u653e\u7535\u4f4d\u7f6e-\u653e\u7535\u6b21\u6570" } } },
                { "charge_chart", new Dictionary<string, object> { { "path", chargeImage }, { "caption", "\u56fe 5.2. ALL-\u653e\u7535\u4f4d\u7f6e-\u653e\u7535\u91cf" } } }
            };
            request["sections"] = new object[] {
                new Dictionary<string, object> {
                    { "anchor", "sections" }, { "title", "0-1.0 U0 · ALL · Q-L" },
                    { "subtitle", "Charge versus distance" }, { "image_path", chargeImage },
                    { "caption", "Dynamic Q-L" }, { "page_break_before", false }
                },
                new Dictionary<string, object> {
                    { "anchor", "sections" }, { "title", "0-1.0 U0 · ALL · N-L" },
                    { "subtitle", "Pulse count versus distance" }, { "image_path", countImage },
                    { "caption", "Dynamic N-L" }, { "page_break_before", true }
                }
            };
            request["image_layout"] = new Dictionary<string, object> {
                { "one_image_per_page", true },
                { "center", true },
                { "caption_from_filename", true },
                { "replace_following_caption", true }
            };
            double[,] matrix = new double[3, 36];
            matrix[0, 0] = 1; matrix[0, 1] = 1001; matrix[0, 2] = 12.2; matrix[0, 3] = 45; matrix[0, 13] = 123.4; matrix[0, 15] = 800; matrix[0, 16] = 820; matrix[0, 22] = 0.95; matrix[0, 23] = 120; matrix[0, 24] = 3000000; matrix[0, 34] = 1; matrix[0, 35] = 1;
            matrix[1, 0] = 2; matrix[1, 1] = 1002; matrix[1, 2] = 13.4; matrix[1, 3] = 225; matrix[1, 13] = 400.5; matrix[1, 15] = 500; matrix[1, 16] = 510; matrix[1, 22] = 72; matrix[1, 23] = 90; matrix[1, 24] = 2500000; matrix[1, 34] = 1;
            string generated = engine.GenerateReportWithMatrix(Json.Serialize(request), matrix);
            RequireOk(generated, "report generation");
            if (!File.Exists(output)) throw new Exception("Generated DOCX was not created.");
            ValidateOutput(output);
            string repeatOutput = Path.Combine(root, "generated-repeat.docx");
            request["output_docx_path"] = repeatOutput;
            string repeated = engine.GenerateReportWithMatrix(Json.Serialize(request), matrix);
            RequireOk(repeated, "repeat report generation in the same process");
            if (!File.Exists(repeatOutput)) throw new Exception("Repeated DOCX was not created.");
            ValidateOutput(repeatOutput);

            string pdf = Path.Combine(root, "sample.pdf");
            File.WriteAllText(pdf, "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/Resources<<>>/Contents 4 0 R>>endobj\n4 0 obj<</Length 20>>stream\nBT (Cable ID:) Tj ET\nendstream endobj\n%%EOF", Encoding.ASCII);
            RequireOk(engine.AnalyzeTemplate(pdf), "PDF analysis");

            Console.WriteLine("PASS");
            Console.WriteLine("OUTPUT=" + output);
            Console.WriteLine("PROJECT_JSON=" + inspectProject);
            Console.WriteLine("GENERATION_JSON=" + generated);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void CreateChartImage(string path, string title, Color color)
    {
        using (Bitmap bitmap = new Bitmap(2400, 1350))
        {
            bitmap.SetResolution(200, 200);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Pen axis = new Pen(Color.FromArgb(65, 75, 85), 4.0f))
            using (Pen grid = new Pen(Color.FromArgb(225, 230, 235), 2.0f))
            using (Pen series = new Pen(color, 8.0f))
            using (Font titleFont = new Font("Arial", 42.0f, FontStyle.Bold))
            using (Font labelFont = new Font("Arial", 24.0f, FontStyle.Regular))
            using (Brush textBrush = new SolidBrush(Color.FromArgb(30, 35, 40)))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.White);
                Rectangle plot = new Rectangle(190, 170, 2040, 980);
                for (int i = 0; i <= 8; i++)
                {
                    int x = plot.Left + plot.Width * i / 8;
                    graphics.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                }
                for (int i = 0; i <= 5; i++)
                {
                    int y = plot.Top + plot.Height * i / 5;
                    graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
                graphics.DrawRectangle(axis, plot);
                int[] xs = { 390, 800, 1260, 1700, 2200 };
                int[] ys = { 940, 760, 390, 610, 260 };
                for (int i = 0; i < xs.Length; i++) graphics.DrawLine(series, xs[i], plot.Bottom, xs[i], ys[i]);
                graphics.DrawString(title, titleFont, textBrush, 190, 70);
                graphics.DrawString("0", labelFont, textBrush, plot.Left - 15, plot.Bottom + 25);
                graphics.DrawString("415", labelFont, textBrush, plot.Right - 40, plot.Bottom + 25);
            }
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void WriteProjectFiles(string project)
    {
        Encoding gbk = Encoding.GetEncoding(936);
        string item = "[ITEM]\r\n所属单位: = \"测试中心\"\r\n测试人员: = \"张工\"\r\n测试地点: = \"试验站\"\r\n日期: = \"2026年08月07日\"\r\n时间: = \"13时54分\"\r\n始端地点: = \"始端柜\"\r\n终端地点: = \"终端柜\"\r\n始端开关类型: = \"户内冷缩\"\r\n终端开关类型: = \"户内冷缩\"\r\n电缆长度: = \"826.9\"\r\n相电压: = \"8.7kV\"\r\n电缆芯数: = \"三芯\"\r\n线路名称： = \"一总降线路\"\r\n电缆型号： = \"YJV22-3*150\"\r\n加压模式： = \"一年以上\"\r\n";
        File.WriteAllBytes(Path.Combine(project, "ITEM.in"), gbk.GetBytes(item));
        string cable = "0\tA相\t\t\r\n1\t始端\t户内冷缩\t0m\r\n1\t中间接头\t直通接头\t264.7m\r\n1\t终端\t户内冷缩\t826.9m\r\n0\tB相\t\t\r\n1\t始端\t户内冷缩\t0m\r\n1\t终端\t户内冷缩\t826.9m\r\n";
        File.WriteAllBytes(Path.Combine(project, "CABLE.in"), gbk.GetBytes(cable));
        File.WriteAllBytes(Path.Combine(project, "ALL_5nC.cal"), gbk.GetBytes("Date:\t2026/08/07\r\nCable length (m):\t827.0\r\nV (m/usec):\t169.05\r\nData:\r\n0.1\t\r\n"));
        File.WriteAllBytes(Path.Combine(project, "A_12.20kV_2026-08-07-13'57'53.dem"), gbk.GetBytes("Date:\t2026/08/07\r\nDATA-TOTAL:\r\nPD (pC):\tVoltage (kV):\r\n10\t12.2\r\n"));
        File.WriteAllBytes(Path.Combine(project, "A_12.20kV_2026-08-07-13'57'53.loc"), gbk.GetBytes("Date:\t2026/08/07\r\nData:\r\n123\t\r\n"));
    }

    private static void CreateTemplate(string path)
    {
        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add(zip, "word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add(zip, "word/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>");
            string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                + "<w:p><w:r><w:t>{{report.</w:t></w:r><w:r><w:t>title}}</w:t></w:r></w:p>"
                + "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>检测单位</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>旧值</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>电缆长度</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>0</w:t></w:r></w:p></w:tc></w:tr></w:tbl>"
                + "<w:sdt><w:sdtPr><w:tag w:val=\"custom.conclusion\"/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>结论占位</w:t></w:r></w:p></w:sdtContent></w:sdt>"
                + "<w:p><w:r><w:t>{{image:count_chart}}</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>\u56fe 5.1. \u65e7\u6b21\u6570\u56fe\u6ce8</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>{{image:charge_chart}}</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>\u56fe 5.2. \u65e7\u56fe\u6ce8\u4e0e\u591a\u4e2a\u6587\u4ef6\u540d\u4e0d\u5e94\u4e32\u8054</w:t></w:r></w:p>"
                + "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>相别</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>位置</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>放电量</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>{{row:matrix_points.phase}}</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>{{row:matrix_points.position_m}}</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>{{row:matrix_points.pd_pc}}</w:t></w:r></w:p></w:tc></w:tr></w:tbl>"
                + "<w:p><w:r><w:t>{{dynamic:sections}}</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>{{template:remove_field_map_page}}</w:t></w:r></w:p><w:p><w:r><w:t>SHOULD_REMOVE</w:t></w:r></w:p>"
                + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>";
            Add(zip, "word/document.xml", document);
        }
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (Stream stream = entry.Open())
        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
    }

    private static void ValidateOutput(string output)
    {
        using (ZipArchive zip = ZipFile.OpenRead(output))
        {
            string xml = Read(zip, "word/document.xml");
            string types = Read(zip, "[Content_Types].xml");
            if (xml.IndexOf("振荡波局部放电测试报告", StringComparison.Ordinal) < 0) throw new Exception("Title was not replaced.");
            if (xml.IndexOf("测试中心", StringComparison.Ordinal) < 0) throw new Exception("Project label binding was not filled.");
            if (xml.IndexOf("826.9", StringComparison.Ordinal) < 0) throw new Exception("Cable length was not filled.");
            if (xml.IndexOf("重点关注A相123.4 m位置", StringComparison.Ordinal) < 0) throw new Exception("Content control was not filled.");
            if (xml.IndexOf("123.4", StringComparison.Ordinal) < 0 || xml.IndexOf("820", StringComparison.Ordinal) < 0) throw new Exception("Repeating matrix row was not created.");
            if (xml.IndexOf("{{", StringComparison.Ordinal) >= 0) throw new Exception("Template markers remain in output.");
            if (types.IndexOf("template.main+xml", StringComparison.OrdinalIgnoreCase) >= 0) throw new Exception("DOTX content type was not converted to DOCX.");
            if (xml.IndexOf("0-1.0 U0 · ALL · Q-L", StringComparison.Ordinal) < 0 || xml.IndexOf("Dynamic N-L", StringComparison.Ordinal) < 0) throw new Exception("Dynamic sections were not expanded.");
            if (xml.IndexOf("SHOULD_REMOVE", StringComparison.Ordinal) >= 0) throw new Exception("Template-only tail was not removed.");
            int mediaCount = zip.Entries.Count(e => e.FullName.StartsWith("word/media/report_", StringComparison.OrdinalIgnoreCase));
            if (mediaCount != 4) throw new Exception("Expected exactly four embedded report images, actual=" + mediaCount);
            if (xml.IndexOf("\u56fe 5.1. ALL-\u653e\u7535\u4f4d\u7f6e-\u653e\u7535\u6b21\u6570", StringComparison.Ordinal) < 0) throw new Exception("Count chart caption was not replaced.");
            if (xml.IndexOf("\u56fe 5.2. ALL-\u653e\u7535\u4f4d\u7f6e-\u653e\u7535\u91cf", StringComparison.Ordinal) < 0) throw new Exception("Charge chart caption was not replaced.");
            if (xml.IndexOf("\u65e7\u6b21\u6570\u56fe\u6ce8", StringComparison.Ordinal) >= 0 || xml.IndexOf("\u4e0d\u5e94\u4e32\u8054", StringComparison.Ordinal) >= 0) throw new Exception("An obsolete caption remains.");
            if (xml.IndexOf("w:type=\"page\"", StringComparison.Ordinal) < 0) throw new Exception("The second chart does not start on a new page.");
            if (Regex.Matches(xml, "<w:jc w:val=\"center\"").Count < 4) throw new Exception("Image and caption paragraphs are not centered.");
            MatchCollection extents = Regex.Matches(xml, "<extent[^>]*cx=\"([0-9]+)\"[^>]*cy=\"([0-9]+)\"");
            if (extents.Count != 4) throw new Exception("Expected four image extents, actual=" + extents.Count);
            foreach (Match extent in extents)
            {
                long cx = long.Parse(extent.Groups[1].Value, CultureInfo.InvariantCulture);
                long cy = long.Parse(extent.Groups[2].Value, CultureInfo.InvariantCulture);
                if (cx > 5700000L || cy > 6700000L) throw new Exception("Image exceeds usable page bounds: " + cx + "x" + cy);
                if (cx < 5000000L) throw new Exception("Image was not expanded to the usable page width: " + cx);
            }
        }
    }

    private static string Read(ZipArchive zip, string name)
    {
        using (Stream stream = zip.GetEntry(name).Open())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
    }

    private static void RequireOk(string json, string operation)
    {
        Dictionary<string, object> obj = Json.DeserializeObject(json) as Dictionary<string, object>;
        if (obj == null || !obj.ContainsKey("ok") || !(bool)obj["ok"]) throw new Exception(operation + " failed: " + json);
    }
}
