using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ReportFeedbackSmoke
{
    static int checks;
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static Assembly report;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception("Check " + checks + ": " + message); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, Hidden).GetValue(owner); }
    static object Call(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, Hidden | BindingFlags.Public).Invoke(owner, args); }
    static object Static(string type, string method, params object[] args) { return report.GetType("HVX.ReportGenerator." + type).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, args); }
    static IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes) { foreach (TreeNode n in nodes) { yield return n; foreach (TreeNode c in Nodes(n.Nodes)) yield return c; } }
    static bool Available(TreeNode node) { return (bool)node.Tag.GetType().GetProperty("IsAvailable").GetValue(node.Tag, null); }
    static void Pump() { for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); } }
    static void Screenshot(Control view, string path) { using (var bitmap = new Bitmap(view.Width, view.Height)) { view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size)); bitmap.Save(path); } }
    static IEnumerable<Control> Controls(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Controls(child)) yield return nested; } }
    static void Throws(Action action, string name) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, name); }
    static Dictionary<string, object>[] Entries(string root) { return (Dictionary<string, object>[])Static("ReportConclusionLearning", "Read", root); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string release = Path.GetFullPath(args[0]);
            string qa = Path.Combine(Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(release, "ReportFeedbackSmoke")), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            report = Assembly.LoadFrom(Path.Combine(release, "modules", "ReportGenerator", "Xiongxin.OWTS.ReportGenerator.dll"));
            string project = Path.Combine(qa, "SyntheticProject"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "ITEM.in"), "电缆长度\t400\r\n相电压\t8.7\r\n始端地点\tSmoke\r\n终端地点\tOnly\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(project, "CABLE.in"), "0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t400\r\n", Encoding.UTF8);
            byte[] itemBefore = File.ReadAllBytes(Path.Combine(project, "ITEM.in"));
            byte[] cableBefore = File.ReadAllBytes(Path.Combine(project, "CABLE.in"));
            using (var module = (Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGeneratorControl")))
            using (var host = new Form { AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(1256, 620) })
            {
                module.GetType().GetProperty("PromptAnalysisOnLoad").SetValue(module, false, null);
                module.GetType().GetProperty("EmbeddedMode").SetValue(module, true, null);
                module.Dock = DockStyle.Fill; host.Controls.Add(module); host.Show(); Pump();
                Check((bool)Call(module, "LoadProject", project), "fixture load");
                var matrix = new double[1, 36]; matrix[0, 0] = 1; matrix[0, 2] = 8.7; matrix[0, 13] = 120; matrix[0, 15] = double.NaN; matrix[0, 35] = 1;
                Call(module, "SetLocationMatrix", matrix); Pump();
                var tree = (TreeView)Field(module, "tree");
                TreeNode[] locations = Nodes(tree.Nodes).Where(n => n.Name.StartsWith("metric_all_") && n.Nodes.Count == 0).ToArray();
                Check(locations.Length == 8, "phase matrix selectors");
                Check(locations.Where(n => n.Name.EndsWith("_All") || n.Name.EndsWith("_A")).All(Available), "valid zero-charge rows preserved");
                Check(locations.Where(n => n.Name.EndsWith("_B") || n.Name.EndsWith("_C")).All(n => !Available(n) && !n.Checked), "empty phases disabled unchecked");
                TreeNode missing = locations.First(n => n.Name.EndsWith("_B")); missing.Checked = true;
                Check(!missing.Checked, "direct invalid check cancelled");
                var parent = tree.Nodes.Cast<TreeNode>().First(n => n.Name == "location"); parent.Checked = false; parent.Checked = true;
                Check(Nodes(parent.Nodes).Where(n => !Available(n)).All(n => !n.Checked), "group selection excludes disabled descendants");
                // Force stale UI state to ensure generation re-validates against current data.
                foreach (TreeNode n in Nodes(tree.Nodes)) n.Checked = false;
                missing.Tag.GetType().GetProperty("IsAvailable").SetValue(missing.Tag, true, null); missing.Checked = true;
                object plan = Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGenerationPlan"));
                Call(module, "AddSelectedLocationSections", plan);
                Check(((ICollection)Field(plan, "Sections")).Count == 0, "backend refuses empty phase despite stale checked UI");
                Call(module, "BuildTree");
                var zeroTan = Activator.CreateInstance(report.GetType("HVX.ReportGenerator.TrendSelection"));
                zeroTan.GetType().GetField("Kind", Hidden).SetValue(zeroTan, Enum.Parse(report.GetType("HVX.ReportGenerator.TrendChartKind"), "DacTanDelta"));
                zeroTan.GetType().GetField("Phase", Hidden).SetValue(zeroTan, Enum.Parse(report.GetType("HVX.ReportGenerator.ReportPhase"), "A"));
                File.WriteAllText(Path.Combine(project, "A_0.00kV_Synthetic.dem"), "D-losses\t0\r\n", Encoding.UTF8);
                Check((bool)Static("TrendStatisticsChartRenderer", "HasData", matrix, project, zeroTan), "measured zero dielectric loss retained");
                Check((string)Static("ReportLanguages", "GetText", 18, "GenerateReport") == "生成報告", "traditional report resource is not English fallback");
                Check((int)Static("ReportLanguages", "FindCode", "zh-HK") == 18 && (int)Static("ReportLanguages", "FindCode", "zh-Hant") == 18, "traditional aliases map to appended index");
                foreach (int lang in Enumerable.Range(0, 19))
                    foreach (Size size in new[] { new Size(1065, 500), new Size(1256, 620), new Size(1850, 990) })
                    {
                        Call(module, "SetLanguageIndex", lang); host.ClientSize = size; Pump();
                        var split = (SplitContainer)Field(module, "previewConclusionSplit");
                        var generate = (Control)Field(module, "generateButton");
                        var review = (Control)Field(module, "conclusionFeedbackButton");
                        Check(split.Orientation == Orientation.Horizontal && !split.IsSplitterFixed && split.SplitterWidth >= 10, "visible draggable horizontal divider");
                        Check(split.Panel1.Height >= split.Panel1MinSize && split.Panel2.Height >= split.Panel2MinSize, "split bounds");
                        int desired = Math.Min(split.Height - split.SplitterWidth - split.Panel2MinSize, split.Panel1MinSize + 20);
                        split.SplitterDistance = desired; Pump();
                        Check(split.SplitterDistance == desired, "operator can move separator");
                        Check(module.RectangleToScreen(module.ClientRectangle).Contains(generate.RectangleToScreen(generate.ClientRectangle)), "footer visible after drag");
                        Check(review.Parent.ClientRectangle.Contains(review.Bounds), "feedback button inside header");
                        Check(generate.Top >= 0 && generate.Height >= generate.Font.Height, "footer caption fits");
                        Check(review.Height >= review.Font.Height + 4, "review caption fits");
                        foreach (Control action in Controls(module).Where(c => c.Visible && (c is Button || c is CheckBox)))
                        {
                            int width = Math.Max(1, action.ClientSize.Width - (action is CheckBox ? 26 : 12));
                            Size required = TextRenderer.MeasureText(action.Text, action.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                            Check(required.Width <= width && required.Height + 4 <= action.ClientSize.Height,
                                "lang=" + lang + " size=" + size + " full caption fits: " + action.Text + " required=" + required + " actual=" + action.ClientSize);
                        }
                        Screenshot(host, Path.Combine(qa, "report-" + lang + "-" + size.Width + ".png"));
                    }
                using (Graphics graphics = host.CreateGraphics()) Console.WriteLine("DPI=" + graphics.DpiX + ";DEVICE=" + host.DeviceDpi);
                TestLearning(qa, module);
            }
            Check(itemBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(project, "ITEM.in"))), "project unchanged");
            Check(cableBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(project, "CABLE.in"))), "cable unchanged");
            Console.WriteLine("REPORT_FEEDBACK_PASS=" + checks); Console.WriteLine("EVIDENCE=" + qa); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void TestLearning(string qa, Control module)
    {
        string root = Path.Combine(qa, "Learning");
        string original = "Synthetic original conclusion", correction = "Synthetic corrected conclusion";
        var context = new Dictionary<string, object> { { "matrix_sha256", "synthetic-audit-hash" }, { "project", "SyntheticProject" } };
        double[] features = { 400, 8.7, 0, 1, 1, 0, 0, 2 };
        var formType = report.GetType("HVX.ReportGenerator.ReportConclusionFeedbackDialog");
        foreach (int lang in Enumerable.Range(0, 19))
        {
            using (var dialog = (Form)Activator.CreateInstance(formType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { lang, root, original, original, context, features }, null))
            {
                dialog.Show(); Pump();
                foreach (Button button in Controls(dialog).OfType<Button>().Where(b => b.Visible))
                {
                    int needed = TextRenderer.MeasureText(button.Text, button.Font, new Size(Math.Max(1, button.Width - 12), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    Check(button.Height >= needed + 4, "feedback button text fits: " + button.Text);
                }
                Screenshot(dialog, Path.Combine(qa, "feedback-" + lang + ".png"));
                ((TabControl)Field(dialog, "pages")).SelectedIndex = 1; Pump();
                var historyGrid = (DataGridView)Field(dialog, "history");
                Check(historyGrid.ColumnHeadersHeight >= historyGrid.Font.Height + 8, "history header glyphs fit");
                foreach (Button button in Controls(dialog).OfType<Button>().Where(b => b.Visible))
                {
                    int needed = TextRenderer.MeasureText(button.Text, button.Font, new Size(Math.Max(1, button.Width - 12), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    Check(button.Height >= needed + 4, "history button text fits: " + button.Text);
                }
                Screenshot(dialog, Path.Combine(qa, "feedback-history-" + lang + ".png"));
                Call(dialog, "Close");
                Check(!Directory.Exists(root), "open/cancel no sample or storage");
            }
        }
        string saved = (string)Static("ReportConclusionLearning", "Save", root, original, original, correction, 2, "Synthetic review note", "en", context, features, false);
        var samples = Entries(root); Check(samples.Length == 1, "one local sample saved");
        Check((string)samples[0]["original_conclusion"] == original && (string)samples[0]["corrected_conclusion"] == correction, "original/correction retained");
        Check((string)samples[0]["language"] == "en" && Convert.ToInt32(samples[0]["rating"]) == 2 && samples[0].ContainsKey("data_context"), "language rating data context audit");
        Check(samples[0]["production_model_modified"].Equals(false), "no model mutation claim");
        Check(Static("ReportConclusionLearning", "Suggest", root, "en", features) != null, "same-context local suggestion");
        Check(Static("ReportConclusionLearning", "Suggest", root, "zh-CN", features) == null, "no cross-language recommendation");
        double[] other = (double[])features.Clone(); other[7] = 4;
        Check(Static("ReportConclusionLearning", "Suggest", root, "en", other) == null, "different phases excluded");
        string id = (string)samples[0]["transaction_id"];
        Throws(() => Static("ReportConclusionLearning", "Save", root, original, original, correction, 0, "", "en", context, features, false), "invalid rating blocked");
        Throws(() => Static("ReportConclusionLearning", "Save", root, original, original, "", 3, "", "en", context, features, false), "empty correction blocked");
        Throws(() => Static("ReportConclusionLearning", "Remove", root, "..\\" + id), "traversal deletion blocked");
        byte[] before = File.ReadAllBytes(Path.Combine(saved, "manifest.json"));
        using (var locked = new FileStream(Path.Combine(saved, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws(() => Static("ReportConclusionLearning", "Remove", root, id), "failed atomic removal preserves active sample");
        Check(Entries(root).Length == 1 && before.SequenceEqual(File.ReadAllBytes(Path.Combine(saved, "manifest.json"))), "failed removal does not alter original audit");
        string archived = (string)Static("ReportConclusionLearning", "Remove", root, id);
        Check(Entries(root).Length == 0, "removed sample excluded from active read");
        Check(Static("ReportConclusionLearning", "Suggest", root, "en", features) == null, "removed sample excluded from suggestions");
        Check(File.Exists(Path.Combine(archived, "deletion.json")) && before.SequenceEqual(File.ReadAllBytes(Path.Combine(archived, "sample", "manifest.json"))), "removal receipt and original audit retained");
        string unrelated = Path.Combine(qa, "Unowned"); Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated, "customer.txt"), "keep");
        Throws(() => Static("ReportConclusionLearning", "Save", unrelated, original, original, correction, 3, "", "en", context, features, false), "unowned directory rejected");
        Check(File.ReadAllText(Path.Combine(unrelated, "customer.txt")) == "keep", "unrelated file unchanged");
        TestLearningPathLengths(qa, context, features);
    }
    static string LearningRootAtLength(string qa, string label, int length)
    {
        string root = Path.Combine(qa, label + "_");
        Check(root.Length <= length, "deep learning fixture fits below the caller's isolated QA directory");
        return root + new string('x', length - root.Length);
    }
    static void TestLearningPathLengths(string qa, Dictionary<string, object> context, double[] features)
    {
        string root = LearningRootAtLength(qa, "DeepLearning", 170);
        string saved = (string)Static("ReportConclusionLearning", "Save", root, "Original", "Original", "Correction", 3, "", "en", context, features, false);
        string id = Path.GetFileName(saved);
        byte[] original = File.ReadAllBytes(Path.Combine(saved, "manifest.json"));
        byte[] owner = File.ReadAllBytes(Path.Combine(saved, "owner.txt"));
        Check(Path.Combine(root, "history", "deleted_" + id + "_" + new string('0', 32)).Length >= 248,
            "deep fixture reproduces the old deletion-directory overflow");
        string archived = (string)Static("ReportConclusionLearning", "Remove", root, id);
        Guid archiveGuid;
        Check(Path.GetFileName(archived).Length == 40 && Path.GetFileName(archived).StartsWith("deleted_", StringComparison.Ordinal) &&
            Guid.TryParseExact(Path.GetFileName(archived).Substring(8), "N", out archiveGuid), "short archive name retains a full unique GUID");
        Check(Entries(root).Length == 0 && File.ReadAllBytes(Path.Combine(archived, "sample", "manifest.json")).SequenceEqual(original) &&
            File.ReadAllBytes(Path.Combine(archived, "sample", "owner.txt")).SequenceEqual(owner), "deep deletion preserves original record and ownership bytes");
        string receipt = File.ReadAllText(Path.Combine(archived, "deletion.json"));
        Check(receipt.Contains("\"transaction_id\":\"" + id + "\"") && receipt.Contains("exclude_from_active_local_learning"),
            "receipt retains the full ID needed for manual recovery");
        // There is no restore UI/API. Exercise manual recovery in this synthetic
        // store using the ID retained in the receipt, without editing the sample.
        Directory.Move(Path.Combine(archived, "sample"), saved);
        Check(Entries(root).Length == 1 && Static("ReportConclusionLearning", "Suggest", root, "en", features) != null &&
            File.ReadAllBytes(Path.Combine(saved, "manifest.json")).SequenceEqual(original), "manual recovery restores readable and retrievable original record");
        string secondArchive = (string)Static("ReportConclusionLearning", "Remove", root, id);
        Check(secondArchive != archived && File.ReadAllText(Path.Combine(archived, "deletion.json")) == receipt,
            "repeated deletion creates a unique backup without overwriting its first receipt");

        foreach (int rootLength in new[] { 190, 192, 184 })
        {
            string rejectedRoot = LearningRootAtLength(qa, "RejectedLearning" + rootLength, rootLength);
            // The reader accepts earlier compact IDs ending in a full GUID.
            // Seed an owned synthetic legacy record whose active paths fit but
            // whose full recovery target hits the file/directory path boundary.
            string legacyId = "r_" + Guid.NewGuid().ToString("N");
            string active = Path.Combine(rejectedRoot, "active", legacyId);
            Directory.CreateDirectory(active);
            File.Copy(Path.Combine(root, "owner.txt"), Path.Combine(rejectedRoot, "owner.txt"));
            File.WriteAllBytes(Path.Combine(active, "owner.txt"), owner);
            File.WriteAllText(Path.Combine(active, "manifest.json"), Encoding.UTF8.GetString(original).Replace(id, legacyId), new UTF8Encoding(false));
            if (rootLength == 184)
            {
                Directory.CreateDirectory(Path.Combine(active, "nested1"));
                File.WriteAllText(Path.Combine(active, "nested1", "x"), "preserve nested payload");
            }
            var before = Directory.GetFiles(rejectedRoot, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            Check(Entries(rejectedRoot).Length == 1, "boundary fixture is a valid readable active record");
            Exception failure = null;
            try { Static("ReportConclusionLearning", "Remove", rejectedRoot, legacyId); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failure = error;
            }
            Check(failure is InvalidOperationException && failure.Message.Contains("Learning path is too long"),
                "actual excessive recovery path is rejected with a clear diagnostic: " + rootLength);
            Check(!Directory.Exists(Path.Combine(rejectedRoot, "history")) && Entries(rejectedRoot).Length == 1 &&
                Directory.GetFiles(rejectedRoot, "*", SearchOption.AllDirectories).Length == before.Count &&
                before.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "path preflight writes no receipt and preserves every active byte: " + rootLength);
        }
    }
}
