using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

// Non-runnable host-library QA: only test-owned configuration and offline
// controls. Never invokes product Main, a module worker, an SDK or an installer.
internal static class BetaHostPresentationSmoke
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static Assembly host;
    static Type Languages { get { return host.GetType("Xiongxin.OWTS.Desktop.AppLanguages", true); } }
    static void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException(name); }
    static object Get(object value, string name)
    {
        var type = value as Type ?? value.GetType(); var property = type.GetProperty(name, Flags);
        return property != null ? property.GetValue(value is Type ? null : value, null) : type.GetField(name, Flags).GetValue(value is Type ? null : value);
    }
    static void Set(object value, string name, object next)
    {
        var property = value.GetType().GetProperty(name, Flags);
        if (property != null) property.SetValue(value, next, null); else value.GetType().GetField(name, Flags).SetValue(value, next);
    }
    static object Call(object value, string name, params object[] args)
    {
        var type = value as Type ?? value.GetType();
        return type.GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value is Type ? null : value, args);
    }
    static void Reject(Action action, Type error, string name)
    {
        try { action(); throw new InvalidOperationException("Expected rejection: " + name); }
        catch (TargetInvocationException actual) { Check(error.IsInstanceOfType(actual.InnerException), name); }
    }
    static string Code(object item) { return (string)Get(item, "Code"); }
    static object[] Items { get { return ((Array)Get(Languages, "SelectionItems")).Cast<object>().ToArray(); } }
    static System.Collections.Generic.IEnumerable<Control> Tree(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls) foreach (var next in Tree(child)) yield return next;
    }

    public sealed class FilterProbe : Control
    {
        string filter = "A";
        public int Writes;
        public string LocationFilterAlgorithmId { get { return filter; } set { filter = value; Writes++; } }
        public string LoadedAnalysisId { get; set; } = "";
        public int SourceRowCount { get; set; }
        public bool DataSaved { get; set; }
        public bool IsAnalysisRunning { get; set; }
        public bool IsBusy { get; set; }
        public bool HasActiveHardwareSession { get; set; }
        public bool ActionPending { get; set; }
        public double LocationFilterStrength { get; set; } = 63;
        public double LocationFilterBChargeThresholdPc { get; set; } = 250;
    }

    static void CheckLanguages(bool beta)
    {
        var all = ((Array)Get(Languages, "All")).Cast<object>().ToArray();
        Check(all.Length == 40 && Code(all[0]) == "zh-CN" && Code(all[1]) == "en" && Code(all[18]) == "zh-TW", "Forty historic locale indices remain stable");
        Check(Items.Select(Code).SequenceEqual(new[] { "zh-CN", "en" }), "Both formal and beta builds offer only adapted Chinese/English");
        foreach (var item in all)
        {
            string code = Code(item);
            string expected = code != "zh-CN" && code != "en" ? "en" : code;
            Check((string)Call(Languages, "ResolveCode", code) == expected, "Runtime locale resolution " + code);
            using (var selector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList })
            {
                selector.Items.AddRange(Items); Call(Languages, "InitializeSelection", selector, code);
                Check(Code(selector.SelectedItem) == expected, "Visible locale selection " + code);
                Check((string)Call(Languages, "CaptureSelectionCode", selector, code) == code, "Unrelated save preserves original locale " + code);
            }
        }
        using (var selector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList })
        {
            selector.Items.AddRange(Items); Call(Languages, "InitializeSelection", selector, "vi");
            selector.SelectedIndex = (int)Call(Languages, "SelectionIndex", "zh-CN");
            Check((string)Call(Languages, "CaptureSelectionCode", selector, "vi") == "zh-CN", "Explicit different selection is saved");
            Call(Languages, "InitializeSelection", selector, "vi");
            typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", Flags).Invoke(selector, new object[] { EventArgs.Empty });
            Check((string)Call(Languages, "CaptureSelectionCode", selector, "vi") == "en", "Explicit selection of displayed fallback replaces original");
            Call(Languages, "InitializeSelection", selector, "system");
            Check((string)Call(Languages, "CaptureSelectionCode", selector, "system") == "system", "Unrelated save preserves follow-system preference");
        }
        Check((string)Call(Languages, "Text", "CompetitionDefect", "zh-CN") == "AI缺陷识别", "Chinese home title uses AI defect recognition");
        Check((string)Call(Languages, "Text", "CompetitionDefect", "en") == "AI defect recognition", "English home title uses AI defect recognition");
    }

    static object FreshSettings(string output)
    {
        var value = Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.AppSettings", true));
        Set(value, "DataRoot", output); Set(Get(value, "Hardware"), "Enabled", false); return value;
    }
    static void CheckSettings(string output, bool beta)
    {
        var settings = FreshSettings(output); var type = settings.GetType();
        Check((string)Get(settings, "LocationFilterAlgorithmId") == "B", "New maintenance settings default B");
        Set(settings, "LanguageCode", "vi"); Set(Get(settings, "TaskMinus3"), "AlgorithmIndex", 7);
        string path = Path.Combine(output, "settings.xml"); Call(settings, "Save", path);
        var xml = new XmlDocument(); xml.Load(path);
        xml.DocumentElement.SelectSingleNode("LocationFilterAlgorithmId").InnerText = "A"; xml.Save(path);
        var loaded = Call(type, "Load", path);
        Check((string)Get(loaded, "LocationFilterAlgorithmId") == "B", "Legacy explicit A preference migrates to B");
        Check((int)Get(Get(loaded, "TaskMinus3"), "AlgorithmIndex") == 7 && (string)Get(loaded, "LanguageCode") == "vi", "Filter migration leaves locator and locale unchanged");
        xml.DocumentElement.RemoveChild(xml.DocumentElement.SelectSingleNode("LocationFilterAlgorithmId")); xml.Save(path);
        Check((string)Get(Call(type, "Load", path), "LocationFilterAlgorithmId") == "B", "Missing legacy field defaults B");
        Set(settings, "LocationFilterAlgorithmId", " a "); Call(settings, "Save", path);
        Check((string)Get(settings, "LocationFilterAlgorithmId") == "B", "Save canonicalizes retired A only");
        Set(settings, "LocationFilterAlgorithmId", "C");
        Reject(() => Call(settings, "Save", Path.Combine(output, "invalid.xml")), typeof(InvalidDataException), "Unknown explicit filter is rejected, not silently changed");
        Set(settings, "LocationFilterAlgorithmId", "B");
        using (var form = (Form)Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.SettingsForm", true), new[] { settings }))
        {
            var selector = (ComboBox)Get(form, "language");
            Check(selector.Items.Count == 2, "Settings language options use supported public list");
            Check((string)Get(Call(form, "CaptureValues"), "LanguageCode") == "vi", "Settings unrelated capture does not replace legacy language");
            using (var panel = (Control)Call(form, "BuildPlusDataPanel"))
            {
                Call(form, "LoadPlusDataValues", settings);
                Check(!Tree(panel).Any(c => c.Name == "PlusDataLocationFilterAlgorithm"), "Maintenance filter selector removed");
                Check(!Tree(panel).Any(c => c.Text.Contains("B 参数") || c.Text.Contains("B参数")), "No B parameter popup entry in host settings");
                var locator = (ComboBox)Get(form, "minus3AlgorithmIndex");
                Check(locator.Items.Cast<object>().Select(x => x.ToString()).SequenceEqual(new[] { "A", "B", "E", "E+" }), "Real locator A/B/E/E+ remains distinct from sole filter B");
                Set(form, "plusDataUnlocked", true);
                int[] indices={5,7,8,9};
                foreach (int index in new[] { 0, 1, 2, 3 })
                {
                    locator.SelectedIndex = index; var captured = FreshSettings(output); Call(form, "CapturePlusDataValues", captured);
                    Check((string)Get(captured, "LocationFilterAlgorithmId") == "B" && (int)Get(Get(captured, "TaskMinus3"), "AlgorithmIndex") == indices[index], "Each real locator method retains sole filter B");
                }
            }
        }
        var preferences = Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.CompetitionDefectPreferences", true));
        Set(preferences, "LanguageCode", "vi");
        Check((int)Get(preferences, "LanguageIndex") == 1, "Independent tool uses supported fallback without changing stored code");
        using (var dialog = (Form)Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.CompetitionDefectPreferencesDialog", true), Flags, null, new[] { preferences }, CultureInfo.InvariantCulture))
        {
            Check(dialog.Text.Contains("AI"), "Independent tool settings title renamed");
            Check(((ComboBox)Get(dialog, "language")).Items.Count == 2, "Independent settings use same supported public language list");
            var save = (Button)dialog.AcceptButton;
            typeof(Button).GetMethod("OnClick", Flags).Invoke(save, new object[] { EventArgs.Empty });
            Check((string)Get(Get(dialog, "Result"), "LanguageCode") == "vi", "Independent settings unrelated save preserves legacy language");
        }
    }

    static void CheckFilterBoundaries()
    {
        var helper = host.GetType("Xiongxin.OWTS.Desktop.LocationFilterSettingsDefaults", true);
        using (var probe = new FilterProbe())
        {
            for (int i = 0; i < 100; i++) Call(helper, "Apply", probe, "LocationFilterAlgorithmId", "A", "first");
            Check(probe.LocationFilterAlgorithmId == "B" && probe.Writes == 1, "Retired A migrates once; repeated contexts do not redraw");
            Check(probe.LocationFilterStrength == 63 && probe.LocationFilterBChargeThresholdPc == 250, "Default migration does not touch explicit numeric filter parameters");
            probe.LoadedAnalysisId = "legacy-analysis"; probe.LocationFilterAlgorithmId = "A"; int writes = probe.Writes;
            Call(helper, "Apply", probe, "LocationFilterAlgorithmId", "B", "first");
            Check(probe.Writes == writes && probe.LocationFilterAlgorithmId == "A", "Already loaded result metadata is not overwritten by a host refresh");
            Call(helper, "Apply", probe, "LocationFilterAlgorithmId", "B", "second");
            Check(probe.LocationFilterAlgorithmId == "B", "Explicit new project takes B default");
        }
        foreach (string guard in new[] { "IsBusy", "ActionPending", "HasActiveHardwareSession", "IsAnalysisRunning" })
            using (var probe = new FilterProbe())
            {
                Set(probe, guard, true); Call(helper, "Apply", probe, "LocationFilterAlgorithmId", "B", "test");
                Check(probe.Writes == 0, "Busy/session guard prevents default mutation: " + guard);
                Set(probe, guard, false); Call(helper, "Apply", probe, "LocationFilterAlgorithmId", "B", "test");
                Check(probe.Writes == 1, "Idle applies deferred B default: " + guard);
            }
        using (var probe = new FilterProbe()) Reject(() => Call(helper, "Apply", probe, "LocationFilterAlgorithmId", "C", "test"), typeof(ArgumentException), "Invalid filter cannot reach control");
        using (var legacy = new Control()) Reject(() => Call(helper, "Apply", legacy, "LocationFilterAlgorithmId", "A", "test"), typeof(InvalidOperationException), "A-only legacy component cannot falsely claim B support");
    }

    [STAThread] static int Main(string[] args)
    {
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-TW");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            host = Assembly.LoadFrom(Path.GetFullPath(args[0])); bool beta = Boolean.Parse(args[1]);
            string output = Path.GetFullPath(args[2]);
            if (Directory.Exists(output)) throw new InvalidOperationException("Fresh test-owned fixture required.");
            Directory.CreateDirectory(output);
            CheckLanguages(beta); CheckSettings(output, beta); CheckFilterBoundaries();
            Console.WriteLine("BETA_HOST_PRESENTATION_PASS=" + checks + ";BETA=" + beta + ";HARDWARE=False;SDK=False;PRODUCT_MAIN=False;USER_PROJECT_WRITES=False"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error); return 1; }
        finally { Thread.CurrentThread.CurrentUICulture = previous; }
    }
}
