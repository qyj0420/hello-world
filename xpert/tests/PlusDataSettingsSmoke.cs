using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class PlusDataSettingsSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    // Supplied test credential is never logged or saved into a settings fixture.
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Field(object obj, string name) { return obj.GetType().GetField(name, Hidden).GetValue(obj); }
    static object Call(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, Hidden).Invoke(obj, args); }
    static AppSettings Capture(SettingsForm form) { return (AppSettings)Call(form, "CaptureValues"); }
    static bool Unlock(SettingsForm form) { Call(form, "OpenSection", SettingsSection.PlusData); return (bool)Field(form, "plusDataUnlocked"); }
    static bool Within(Control control, Control parent) { for (var c = control; c != null; c = c.Parent) if (c == parent) return true; return false; }
    static void Fonts(Control control, Font font) { control.Font = font; foreach (Control child in control.Controls) Fonts(child, font); }
    static string[] protectedControls = { "enableAiFeatures", "enableDefectRecognition", "enableCompetitionDefectModule", "pdSampleRate", "pdSampleBits", "minus3MaximumPulses", "calibrationAverageCount", "noiseSimilarityTolerance", "minus3ReflectionSuppression", "minus3AlgorithmIndex", "endpointWidth", "endpointRatio", "endpointFirstOperator", "endpointSecondOperator", "minus3ModelPath", "unifiedModel", "task35Model", "task36Model", "task37Model", "task38Model" };
    static AppSettings Fixture()
    {
        var value = new AppSettings { EnableAiFeatures = false, EnableDefectRecognition = false, EnableCompetitionDefectModule = true,
            PdSampleRateHz = 62500000, PdSampleBits = 14, NoiseSimilarityTolerancePercent = 2.5,
            Task35ModelPath = "a.onnx", Task36ModelPath = "b.onnx", Task37ModelPath = "c.onnx", Task38ModelPath = "d.onnx",
            UnifiedAnalysisModelPath = "unified.pdm", DividerRatio = 19, MaximumVoltageKv = 27 };
        value.TaskMinus3.MaximumPulseCount = 41; value.TaskMinus3.CalibrationAverageCount = 19;
        value.TaskMinus3.MultipleReflectionSuppressionCount = -1; value.TaskMinus3.AlgorithmIndex = 5;
        value.TaskMinus3.EndpointPolicy = "3;2;AND;OR"; value.TaskMinus3.ModelLibraryPath = "analysis.pdm";
        return value;
    }
    static void Preserved(AppSettings actual, AppSettings expected, string label)
    {
        Check(actual.EnableAiFeatures == expected.EnableAiFeatures && actual.EnableDefectRecognition == expected.EnableDefectRecognition && actual.EnableCompetitionDefectModule == expected.EnableCompetitionDefectModule, label + " feature flags");
        Check(actual.PdSampleRateHz == expected.PdSampleRateHz && actual.PdSampleBits == expected.PdSampleBits, label + " acquisition settings");
        Check(actual.NoiseSimilarityTolerancePercent == expected.NoiseSimilarityTolerancePercent && actual.TaskMinus3.MaximumPulseCount == expected.TaskMinus3.MaximumPulseCount && actual.TaskMinus3.CalibrationAverageCount == expected.TaskMinus3.CalibrationAverageCount, label + " pulse settings");
        Check(actual.TaskMinus3.MultipleReflectionSuppressionCount == expected.TaskMinus3.MultipleReflectionSuppressionCount && actual.TaskMinus3.AlgorithmIndex == expected.TaskMinus3.AlgorithmIndex && actual.TaskMinus3.EndpointPolicy == expected.TaskMinus3.EndpointPolicy, label + " algorithm settings");
        Check(actual.Task35ModelPath == expected.Task35ModelPath && actual.Task36ModelPath == expected.Task36ModelPath && actual.Task37ModelPath == expected.Task37ModelPath && actual.Task38ModelPath == expected.Task38ModelPath && actual.UnifiedAnalysisModelPath == expected.UnifiedAnalysisModelPath && actual.TaskMinus3.ModelLibraryPath == expected.TaskMinus3.ModelLibraryPath, label + " model settings");
    }
    sealed class Probe : UserControl
    {
        public bool EnableAiFeatures { get; set; }
        public bool EnableDefectRecognition { get; set; }
        public bool ProtectedAnalysisSettingsManaged { get; set; }
        public string EndpointPolicy { get; set; }
    }
    sealed class AnalysisProbe
    {
        public double AnalysisAttenuationTolerancePercent { get { return 12; } }
        public double AnalysisCorrelationErrorPercent { get { return 22; } }
        public double AnalysisAutomaticThresholdMultiplier { get { return 4; } }
        public int AnalysisMaximumPulseCount { get { return 99; } }
        public int AnalysisMultipleReflectionSuppressionCount { get { return 99; } }
        public int AnalysisAlgorithmIndex { get { return 1; } }
        public bool AnalysisUseFirstArrivalTiming { get { return true; } }
        public bool AnalysisPhaseFilterEnabled { get { return false; } }
        public bool[] AnalysisQuadrants { get { return new[] { true, true, true, true }; } }
        public string ModelLibraryPath { get { return "must-not-copy.pdm"; } }
    }
    static void Layout(SettingsForm form, string qa)
    {
        form.Opacity = 0; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
        form.Show(); Call(form, "SelectSection", SettingsSection.PlusData); Application.DoEvents();
        var pages = (TabControl)((TabPage)Field(form, "plusDataTab")).Controls[0];
        foreach (float size in new[] { 9f, 14f, 18f })
        {
            Fonts(form, new Font("Microsoft YaHei UI", size)); form.ClientSize = new Size(900, 640);
            foreach (TabPage page in pages.TabPages)
            {
                pages.SelectedTab = page; form.PerformLayout(); Application.DoEvents();
                foreach (var name in protectedControls)
                {
                    var control = (Control)Field(form, name); if (!Within(control, page)) continue;
                    for (Control parent = control.Parent; parent != null; parent = parent.Parent)
                    {
                        var scroll = parent as ScrollableControl;
                        if (scroll != null && scroll.AutoScroll) scroll.ScrollControlIntoView(control);
                    }
                    Application.DoEvents();
                    Check(control.Parent.ClientRectangle.Contains(control.Bounds), "protected editor stays in parent: " + name + " / " + size + "; editor=" + control.Bounds + "; parent=" + control.Parent.ClientRectangle);
                }
                using (var bmp = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size)); bmp.Save(Path.Combine(qa, "plus-" + pages.SelectedIndex + "-" + size + ".png")); }
            }
        }
        form.Hide();
    }
    static void RealHost(string build, string qa)
    {
        // Runtime integration is optional for the fast source-only test. The full
        // suite supplies BuildRoot,QA and exercises the actual staged module ABI.
        var assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(build), "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
        var preferences = assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences", true).GetField("PathProvider", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        object previous = preferences.GetValue(null);
        preferences.SetValue(null, (Func<string>)(() => Path.Combine(qa, "module-preferences.json")));
        try
        {
            var settings = Fixture(); settings.EnableAiFeatures = true; settings.EnableDefectRecognition = false;
            using (var host = new ModuleHost(settings, new ApplicationState(), new Diagnostics(Path.Combine(qa, "diagnostics")), Path.Combine(Path.GetFullPath(build), "modules")))
            {
                Control module; string error;
                Check(host.TryCreateControl(ModuleHost.DataAnalysis, out module, out error), "real DataAnalysis module loads through host: " + error);
                using (module)
                {
                    Func<string, object> get = name => module.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(module, null);
                    Check((bool)get("ProtectedAnalysisSettingsManaged"), "real module receives protected-settings management contract");
                    Check((int)get("AnalysisMaximumPulseCount") == settings.TaskMinus3.MaximumPulseCount && (int)get("AnalysisAlgorithmIndex") == settings.TaskMinus3.AlgorithmIndex, "real module receives unchanged protected algorithm values");
                    foreach (var pair in new[] { new[] { true, false }, new[] { false, true }, new[] { false, false }, new[] { true, true } })
                    {
                        settings.EnableAiFeatures = pair[0]; settings.EnableDefectRecognition = pair[1]; host.RefreshContext();
                        Check((bool)get("EnableAiFeatures") == pair[0] && (bool)get("EnableDefectRecognition") == pair[1], "real host propagates separate AI and defect switches");
                        Check((bool)get("DefectRecognitionEnabled") == (pair[0] && pair[1]), "real recognition effective gate requires both enabled");
                        if (pair[0] && pair[1]) continue;
                        foreach (string method in new[] { "RecognizeCurrentDischarge", "RecognizeCurrentDischargeAsync" })
                        {
                            bool rejected = false;
                            try { module.GetType().GetMethod(method, Type.EmptyTypes).Invoke(module, null); }
                            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException; }
                            Check(rejected, "real disabled recognition rejects before model/runtime use: " + method);
                        }
                        Check(Field(module, "task12Runtime") == null && get("CurrentDischargeRecognition") == null, "disabled runtime never constructed or retaining recognition");
                    }
                }
            }
        }
        finally { preferences.SetValue(null, previous); }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args.Length > 1 ? args[1] : args[0]), "plus-data-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            Check(typeof(SettingsForm).GetMethod("PromptForPassword", Hidden) == null, "password prompt removed");
            var expected = Fixture();
            string file = Path.Combine(qa, "legacy.xml"); expected.Save(file);
            var xml = new System.Xml.XmlDocument(); xml.Load(file); var field = xml.DocumentElement.SelectSingleNode("EnableDefectRecognition"); Check(field != null, "new feature is persisted"); field.ParentNode.RemoveChild(field); xml.Save(file);
            var legacy = AppSettings.Load(file); Check(legacy.EnableDefectRecognition, "old configurations retain previous recognition availability");
            expected.EnableDefectRecognition = true; Preserved(legacy, expected, "legacy load");
            using (var form = new SettingsForm(legacy))
            {
                form.AuditEnabled = false;
                foreach (string name in protectedControls) Check(Field(form, name) == null, "protected editor not constructed before authorization: " + name);
                Check(((TabPage)Field(form, "plusDataTab")).Text == "HV DAC Xpert Plus Data", "new entry has exact name");
                Preserved(Capture(form), legacy, "unauthorized save");
                using (var device = (Control)Call(form, "BuildAcquisitionPanel"))
                using (var expert = (Control)Call(form, "BuildMinus3Tab"))
                using (var paths = (Control)Call(form, "BuildModelsTab"))
                {
                    Call(form, "LoadDeviceValues", legacy); Call(form, "LoadExpertValues", legacy);
                    foreach (string name in protectedControls) Check(Field(form, name) == null, "old entry no longer exposes protected editor: " + name);
                    form.GetType().GetField("deviceUnlocked", Hidden).SetValue(form, true); form.GetType().GetField("expertUnlocked", Hidden).SetValue(form, true);
                    Preserved(Capture(form), legacy, "old roles cannot change migrated settings");
                    Check(Unlock(form), "Plus Data authorizes its own editors");
                    foreach (string name in protectedControls) Check(Within((Control)Field(form, name), (Control)Field(form, "plusDataTab")), "editor has unique protected parent: " + name);
                    Preserved(Capture(form), legacy, "authorized unchanged save");
                    ((CheckBox)Field(form, "enableAiFeatures")).Checked = true; ((CheckBox)Field(form, "enableDefectRecognition")).Checked = false; ((CheckBox)Field(form, "enableCompetitionDefectModule")).Checked = false;
                    ((NumericUpDown)Field(form, "pdSampleRate")).Value = 125000000; ((NumericUpDown)Field(form, "pdSampleBits")).Value = 15;
                    ((NumericUpDown)Field(form, "minus3MaximumPulses")).Value = 50; ((NumericUpDown)Field(form, "calibrationAverageCount")).Value = 32; ((NumericUpDown)Field(form, "noiseSimilarityTolerance")).Value = 1;
                    ((NumericUpDown)Field(form, "minus3ReflectionSuppression")).Value = 2; ((ComboBox)Field(form, "minus3AlgorithmIndex")).SelectedIndex = 1;
                    ((NumericUpDown)Field(form, "endpointWidth")).Value = 4; ((NumericUpDown)Field(form, "endpointRatio")).Value = 3;
                    ((ComboBox)Field(form, "endpointFirstOperator")).SelectedIndex = 0; ((ComboBox)Field(form, "endpointSecondOperator")).SelectedIndex = 1;
                    ((TextBox)Field(form, "minus3ModelPath")).Text = "new-analysis.pdm"; ((TextBox)Field(form, "task35Model")).Text = "new-a.onnx";
                    var changed = Capture(form);
                    Check(changed.EnableAiFeatures && !changed.EnableDefectRecognition && !changed.EnableCompetitionDefectModule, "three feature choices are independent");
                    Check(changed.PdSampleRateHz == 125000000 && changed.PdSampleBits == 15 && changed.TaskMinus3.MaximumPulseCount == 50 && changed.TaskMinus3.CalibrationAverageCount == 32 && changed.NoiseSimilarityTolerancePercent == 1, "sampling and pulse choices captured");
                    Check(changed.TaskMinus3.MultipleReflectionSuppressionCount == 2 && changed.TaskMinus3.AlgorithmIndex == 7 && changed.TaskMinus3.EndpointPolicy == "4;3;OR;AND", "B method and combination values captured without semantic change");
                    Check(changed.TaskMinus3.ModelLibraryPath == "new-analysis.pdm" && changed.Task35ModelPath == "new-a.onnx", "model choices captured");
                    Check(changed.MaximumVoltageKv == 27 && changed.DividerRatio == 19 && !changed.Hardware.Enabled, "hardware limits and offline mode unchanged");
                    string edited = Path.Combine(qa, "edited.xml"); changed.Save(edited); Preserved(AppSettings.Load(edited), changed, "edited round trip");
                    Check(!File.ReadAllText(edited).Contains("PlusDataUnlocked"), "authorization never persisted in configuration");
                    Preserved(legacy, expected, "cancel/source remains untouched");
                    Layout(form, qa);
                }
            }
            var persisted = AnalysisSettingsPersistence.Capture(new AnalysisProbe(), legacy);
            Preserved(persisted, legacy, "ordinary analysis confirmation cannot overwrite protected values");
            Check(persisted.TaskMinus3.AttenuationTolerancePercent == 12 && persisted.TaskMinus3.CorrelationErrorPercent == 22, "ordinary analysis parameters still editable");
            using (var probe = new Probe()) using (var host = new ModuleHost(legacy, new ApplicationState(), new Diagnostics(qa), Path.Combine(qa, "modules")))
            {
                var descriptor = ModuleHost.Descriptors.Single(d => d.Id == ModuleHost.DataAnalysis);
                Call(host, "ApplyContext", descriptor, probe, false); Check(!probe.EnableAiFeatures && probe.EnableDefectRecognition && probe.ProtectedAnalysisSettingsManaged, "host maps distinct AI/recognition/managed settings");
                legacy.EnableDefectRecognition = false; Call(host, "ApplyContext", descriptor, probe, false); Check(!probe.EnableDefectRecognition && !probe.EnableAiFeatures, "recognition-only change reaches live module");
            }
            if (args.Length > 1) RealHost(args[0], qa);
            Console.WriteLine("PLUS_DATA_SETTINGS_PASS=" + checks + ";HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False;EVIDENCE=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
