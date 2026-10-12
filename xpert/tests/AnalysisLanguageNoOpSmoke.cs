using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class AnalysisLanguageNoOpSmoke
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks, layouts, resizes;
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static object Get(object owner, string name) { return owner.GetType().GetProperty(name, All).GetValue(owner, null); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, All).SetValue(owner, value, null); }
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }
    static void Watch(Control control)
    {
        control.Layout += (s, e) => layouts++;
        control.SizeChanged += (s, e) => resizes++;
        foreach (Control child in control.Controls) Watch(child);
    }
    static void AssertNoOp(Control control, TreeView tree, int language)
    {
        Pump();
        TreeNode root = tree.Nodes[0], selected = tree.SelectedNode, top = tree.TopNode;
        bool expanded = root.IsExpanded;
        int changes = (int)Get(control, "ResultChangeSequence"), selection = (int)Get(control, "SelectionSequence");
        object working = Field(control, "working"), matching = Field(control, "matching");
        layouts = resizes = 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 12; i++) Set(control, "LanguageIndex", language);
        watch.Stop();
        Check(layouts == 0 && resizes == 0, "same language must not lay out or resize the workspace: " + language);
        Check(Object.ReferenceEquals(root, tree.Nodes[0]), "same language must not rebuild the location tree");
        Check(Object.ReferenceEquals(selected, tree.SelectedNode) && Object.ReferenceEquals(top, tree.TopNode), "selection and scrolling survive repeated host context");
        Check(root.IsExpanded == expanded, "expansion state survives repeated context");
        Check((int)Get(control, "ResultChangeSequence") == changes && (int)Get(control, "SelectionSequence") == selection, "no data/selection notifications for same language");
        Check(Object.ReferenceEquals(working, Field(control, "working")) && Object.ReferenceEquals(matching, Field(control, "matching")), "source and waveform buffers are unchanged");
        Console.WriteLine("LANGUAGE=" + language + "; SAME_LANGUAGE_AVERAGE_MS=" + (watch.Elapsed.TotalMilliseconds / 12).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
            using (var control = (Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.PdReviewControl", true)))
            using (var host = new Form { ClientSize = new Size(1366, 768), Location = new Point(-20000, -20000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None })
            {
                Set(control, "PromptSavedAnalysisOnProjectLoad", false);
                Check((int)Get(control, "LanguageIndex") == 0 && ((Button)Field(control, "acceptButton")).Text == "接受", "constructor still initializes the default language");
                object treeControl = Field(control, "analysisTree");
                // Memory-only catalog: no project, waveform, hardware or archive I/O.
                int count = 300;
                string[] paths = Enumerable.Range(1, count).Select(i => Path.Combine(Path.GetFullPath(args[0]), "qa-memory-" + i + ".loc")).ToArray();
                bool loaded = (bool)treeControl.GetType().GetMethod("LoadRecords").Invoke(treeControl, new object[] {
                    paths, Enumerable.Range(1,count).Select(i=>1+i%3).ToArray(), Enumerable.Repeat(12.3,count).ToArray(),
                    Enumerable.Range(1,count).ToArray(), Enumerable.Range(1,count).Select(i=>(double)i).ToArray() });
                Check(loaded, "synthetic tree catalog loaded");
                var tree = (TreeView)Field(treeControl, "tree");
                host.Controls.Add(control); control.Dock = DockStyle.Fill; host.Show(); Pump();
                Watch(control);
                AssertNoOp(control, tree, 0);
                foreach (int language in new[] { 1, 18, 0 })
                {
                    Set(control, "LanguageIndex", language); Pump();
                    string expected = language == 1 ? "Accept" : "接受";
                    Check(((Button)Field(control, "acceptButton")).Text == expected, "real language changes still translate actions");
                    Check((int)Get(Get(control, "PdMap"), "LanguageIndex") == language && (int)Get(Get(control, "Spectrum"), "LanguageIndex") == language, "real language propagates to both charts");
                    tree.Nodes[0].Expand(); tree.Nodes[0].Nodes[0].Expand();
                    tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[3]; tree.TopNode = tree.Nodes[0].Nodes[0].Nodes[1];
                    AssertNoOp(control, tree, language);
                }
                bool rejected = false;
                try { Set(control, "LanguageIndex", 40); }
                catch (TargetInvocationException error) { rejected = error.InnerException is ArgumentOutOfRangeException; }
                Check(rejected && (int)Get(control, "LanguageIndex") == 0, "invalid language remains rejected without changing state");
                layouts = 0; control.GetType().GetMethod("SetLanguageIndex").Invoke(control, new object[] { 0 });
                Set(control, "EnglishVersion", false); Check(layouts == 0, "legacy same-language aliases are also no-ops");
                host.Hide();
            }
            Console.WriteLine("ANALYSIS_LANGUAGE_NOOP_PASS=" + checks + "; HARDWARE=False; PROJECT_WRITES=False"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
