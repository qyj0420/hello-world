using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CableJointEditingSmoke
{
    static int checks;
    static Type store;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
    static object Call(object target, string name, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        MethodInfo method = type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length);
        return method.Invoke(target is Type ? null : target, args);
    }
    static object Field(object target, string name) { return target.GetType().GetField(name, All).GetValue(target); }
    static void Set(object target, string name, object value) { target.GetType().GetProperty(name, All).SetValue(target, value, null); }
    static object Property(object target, string name) { return target.GetType().GetProperty(name, All).GetValue(target, null); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, message); }
    static bool Equal(byte[] a, byte[] b) { return a.SequenceEqual(b); }
    static string Block(string phase, int length = 400)
    { return "0\t" + phase + "\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t接头_1\t冷缩接头\t100m\r\n2\t本体\tPVC\t\r\n1\t终端\t户内冷缩\t" + length + "m\r\n"; }
    static string Fixture(string root, string name, string cable, Encoding encoding = null)
    {
        string dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ITEM.in"), "[ITEM]\r\n电缆长度: = \"400\"\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "CABLE.in"), cable, encoding ?? new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(dir, "RawAcquisition.bin"), new byte[] { 1, 4, 9, 16, 25 });
        return dir;
    }
    static object Prepare(string dir, string phase, double position) { return Call(Call(store, "Read", dir), "Prepare", phase, position, "Cold-shrink joint"); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string release = Path.GetFullPath(args[0]);
            string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CableJointEditingSmoke");
            string root = Path.Combine(output, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Assembly library = Assembly.LoadFrom(Path.Combine(release, "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
            store = library.GetType("HVX.PdReview.ReviewCableJointStore", true);
            string dir = Fixture(root, "common", Block("ALL相"));
            string cable = Path.Combine(dir, "CABLE.in"); byte[] before = File.ReadAllBytes(cable);
            byte[] item = File.ReadAllBytes(Path.Combine(dir, "ITEM.in")), raw = File.ReadAllBytes(Path.Combine(dir, "RawAcquisition.bin"));
            foreach (string extension in new[] { ".cal", ".dem", ".loc" }) File.WriteAllBytes(Path.Combine(dir, "protected-source" + extension), raw);
            object plan = Prepare(dir, "ALL", 200);
            Check(Equal(before, File.ReadAllBytes(cable)), "preparing/cancelling makes no file change");
            foreach (double pos in new[] { 0d, 400, -1, 401, double.NaN, double.PositiveInfinity, 100, 100.0001, .0001 })
                Reject(() => Prepare(dir, "ALL", pos), "out of range/duplicate/rounded endpoint rejected: " + pos);
            Reject(() => Prepare(dir, "B", 200), "nonexistent phase rejected");
            string backup = (string)Call(store, "Commit", plan);
            Check(File.Exists(backup) && Equal(before, File.ReadAllBytes(backup)), "exact pre-edit backup retained");
            string text = File.ReadAllText(cable);
            Check(text.Contains("\t200m\r\n2\t本体\tPVC\t"), "inserted joint inherits containing segment material");
            Check(text.Contains("\t100m") && text.EndsWith("\t400m\r\n"), "old joint/endpoints retained");
            Check(Equal(item, File.ReadAllBytes(Path.Combine(dir, "ITEM.in"))) && Equal(raw, File.ReadAllBytes(Path.Combine(dir, "RawAcquisition.bin"))), "metadata and raw acquisition unchanged");
            foreach (string extension in new[] { ".cal", ".dem", ".loc" })
                Check(Equal(raw, File.ReadAllBytes(Path.Combine(dir, "protected-source" + extension))), extension + " source bytes unchanged");
            Call(store, "Read", dir); checks++;
            Reject(() => Call(store, "Commit", plan), "stale edit snapshot rejected");
            Check(!Directory.GetFiles(dir, ".cable-edit-*.tmp").Any(), "no staging file left");

            dir = Fixture(root, "phases", Block("A相") + Block("B相", 500) + Block("C相"));
            cable = Path.Combine(dir, "CABLE.in");
            Call(store, "Commit", Prepare(dir, "B", 250)); text = File.ReadAllText(cable);
            Check(text.StartsWith(Block("A相")) && text.EndsWith(Block("C相")), "single phase leaves other phases byte-equivalent");
            Check(text.Contains("\t250m"), "B phase insertion");
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.GetEncoding(936), new UnicodeEncoding(false, true) })
            {
                dir = Fixture(root, "encoding" + encoding.CodePage + "-" + encoding.GetPreamble().Length, Block("ALL"), encoding);
                before = File.ReadAllBytes(Path.Combine(dir, "CABLE.in"));
                Call(store, "Commit", Prepare(dir, "ALL", 250));
                Check(encoding.GetString(File.ReadAllBytes(Path.Combine(dir, "CABLE.in"))).Contains("250m"), "encoding retained: " + encoding.CodePage);
                Check(File.ReadAllBytes(Path.Combine(dir, "CABLE.in")).Take(encoding.GetPreamble().Length).SequenceEqual(before.Take(encoding.GetPreamble().Length)), "preamble retained");
            }
            dir = Fixture(root, "failures", Block("ALL")); cable = Path.Combine(dir, "CABLE.in");
            before = File.ReadAllBytes(cable); plan = Prepare(dir, "ALL", 250);
            File.SetAttributes(cable, File.GetAttributes(cable) | FileAttributes.ReadOnly);
            try { Reject(() => Call(store, "Commit", plan), "read-only write safely fails"); }
            finally { File.SetAttributes(cable, File.GetAttributes(cable) & ~FileAttributes.ReadOnly); }
            Check(Equal(before, File.ReadAllBytes(cable)), "read-only failure retains original");
            using (var locked = new FileStream(cable, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => Call(store, "Commit", plan), "sharing violation safely fails");
            Check(Equal(before, File.ReadAllBytes(cable)), "sharing failure retains original");
            using (var locked = new FileStream(cable, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => Call(store, "Commit", plan), "atomic replacement failure after staging safely fails");
            Check(Equal(before, File.ReadAllBytes(cable)), "failed atomic replacement retains original");
            Check(!Directory.GetFiles(dir, ".cable-edit-*.tmp").Any(), "failed replacement removes only its own staged file");
            using (var locked = new FileStream(Path.Combine(dir, ".DataBrowser.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Reject(() => Call(store, "Commit", plan), "cooperates with data-browser edit lock");
            File.AppendAllText(cable, "\r\n", new UTF8Encoding(false)); byte[] external = File.ReadAllBytes(cable);
            Reject(() => Call(store, "Commit", plan), "concurrent external modification rejected");
            Check(Equal(external, File.ReadAllBytes(cable)), "concurrent edit not overwritten");
            dir = Fixture(root, "unknown", Block("ALL").Replace("2\t本体\tXLPE\t\r\n", ""));
            Reject(() => Call(store, "Read", dir), "missing material safely rejected");
            dir = Fixture(root, "duplicate", Block("ALL") + Block("ALL"));
            Reject(() => Call(store, "Read", dir), "duplicate phase blocks safely rejected");
            Check((double)Call(store, "ProjectPosition", 100d, 420d, 20d, false) == 120, "lead offset mapping");
            Check((double)Call(store, "ProjectPosition", 100d, 420d, 20d, true) == 320, "reverse and lead mapping");
            Reject(() => Call(store, "ProjectPosition", 400d, 420d, 20d, true), "DUT endpoint is not a joint");

            dir = Fixture(root, "review", Block("ALL")); cable = Path.Combine(dir, "CABLE.in");
            using (var review = (Control)Activator.CreateInstance(library.GetType("HVX.PdReview.PdReviewControl", true)))
            using (var host = new Form { ShowInTaskbar = false, ClientSize = new Size(1150, 700) })
            {
                Set(review, "PromptSavedAnalysisOnProjectLoad", false); Set(review, "EnglishVersion", true);
                Set(review, "ProjectPath", dir); Set(review, "AnalysisAlgorithmIndex", 4);
                Set(review, "AnalysisAttenuationTolerancePercent", 22d);
                host.Controls.Add(review); review.Dock = DockStyle.Fill; host.Show(); Application.DoEvents();
                before = File.ReadAllBytes(cable);
                int tick = 0; bool cancelled = false;
                using (var timer = new Timer { Interval = 50 })
                {
                    timer.Tick += (s, e) =>
                    {
                        tick++;
                        Form dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Name == "CableJointDialog");
                        if (dialog != null)
                        {
                            using (var image = new Bitmap(dialog.Width, dialog.Height))
                            { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size)); image.Save(Path.Combine(root, "AddJointDialog.png")); }
                            dialog.DialogResult = DialogResult.Cancel; dialog.Close(); cancelled = true; timer.Stop();
                        }
                        if (tick > 40) { timer.Stop(); throw new InvalidOperationException("joint dialog did not appear"); }
                    };
                    timer.Start(); Call(review, "ShowAddCableJoint", 200d);
                }
                Check(cancelled && Equal(before, File.ReadAllBytes(cable)), "cancel dialog leaves cable unchanged");
                object[] exported = { null, null, null };
                Check((bool)CallWithArgs(review, "ExportAnalysisSessionUtf8", exported), "export pre-edit analysis session");
                // Synthetic stale in-memory result must be cleared before further use.
                review.GetType().GetField("working", All).SetValue(review, new double[1, 36]);
                Call(review, "ApplyConfirmedCableJoint", Prepare(dir, "ALL", 250));
                Check((int)Property(review, "SourceRowCount") == 0, "current pre-edit analysis is invalidated");
                Check((int)Property(review, "PendingReportChartCount") == 0, "old current report attachments cleared");
                Check((int)Property(review, "AnalysisAlgorithmIndex") == 4 && (double)Property(review, "AnalysisAttenuationTolerancePercent") == 22, "expert algorithm settings retained");
                Check(((string)Property(review, "CableStructureEditStatus")).Contains("out of date"), "customer sees reanalysis requirement");
                object spectrum = Property(review, "Spectrum");
                Check(((double[])Property(spectrum, "CurrentJointPositionsM")).Contains(250), "refreshed spectrum includes real new joint");
                Check((bool)Call(review, "LoadAnalysisSessionUtf8", dir, exported[0], exported[1], exported[2]), "historical session can be examined safely");
                Check(((string)Property(review, "AnalysisSessionStatus")).Contains("reanalysis required"), "old session explicitly invalidated by cable fingerprint");
                Set(spectrum, "DisplayModeIndex", 0);
                var chart = (Control)spectrum; chart.Size = new Size(800, 500);
                Rectangle area = (Rectangle)Call(spectrum, "PlotArea", chart.ClientRectangle);
                object[] positionArgs = { new Point(area.Left + area.Width / 2, area.Top + area.Height / 2), null };
                Check((bool)CallWithArgs(spectrum, "TryCableJointPosition", positionArgs), "Q-L plot coordinate is accepted");
                Check((double)positionArgs[1] > 0 && (double)positionArgs[1] < 400, "Q-L pixel maps inside cable metres");
                Set(spectrum, "DisplayModeIndex", 2);
                Check(!(bool)CallWithArgs(spectrum, "TryCableJointPosition", positionArgs), "phase plots cannot add cable joints");
                host.Hide();
            }
            Console.WriteLine("CABLE_JOINT_EDITING_PASS=" + checks);
            Console.WriteLine("QA=" + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static object CallWithArgs(object target, string name, object[] args)
    { return target.GetType().GetMethod(name, All).Invoke(target, args); }
}
