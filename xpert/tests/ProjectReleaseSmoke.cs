using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ProjectReleaseSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static string project, qa;
    static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, Hidden).GetValue(owner); }
    static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Hidden).SetValue(owner, value); }
    static object Call(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, Hidden).Invoke(owner, args); }
    static ApplicationState State() { var state = new ApplicationState(); string error; Check(state.TryLoadProject(project, out error), "isolated fixture validates: " + error); return state; }
    static AppSettings Settings(string language = "zh-CN") { var settings = new AppSettings { EnableCompetitionDefectModule = true, LanguageCode = language }; settings.Hardware.Enabled = false; return settings; }
    sealed class Probe : UserControl
    {
        public bool IsBusy { get; set; }
        public bool IsAnalysisRunning { get; set; }
        public bool IsRunning { get; set; }
        public bool HasActiveHardwareSession { get; set; }
        public bool EvaluationBusy { get; set; }
        public bool AutomaticEvaluationPending { get; set; }
        public bool DischargeRecognitionBusy { get; set; }
        public bool IsDirty { get; set; }
        public bool HasUnsavedAnalysisChanges { get; set; }
        public bool HasUnsavedCalibrationChanges { get; set; }
        public bool IsCalibrationSavedValid { get; set; } = true;
        public bool HasCalibrationData { get; set; }
        public bool IsEditingCalibration { get; set; }
        public string PressureStateCode { get; set; } = "WAITING";
        public bool SaveResult = true;
        public int SaveCalls, DiscardCalls;
        public bool SaveProject() { SaveCalls++; if (SaveResult) IsDirty = false; return SaveResult; }
        public bool ShowSaveAnalysisDialog() { SaveCalls++; if (SaveResult) HasUnsavedAnalysisChanges = false; return SaveResult; }
        public bool SaveCalibrationFile() { SaveCalls++; if (SaveResult) HasUnsavedCalibrationChanges = false; return SaveResult; }
        public void DiscardCalibrationChanges() { DiscardCalls++; HasUnsavedCalibrationChanges = false; }
    }
    sealed class ThrowingProbe : UserControl { public bool IsBusy { get { throw new IOException("fixture state unavailable"); } } }
    sealed class CleanupProbe : UserControl
    {
        public bool ThrowBeforeDispose, ThrowAfterDispose;
        protected override void Dispose(bool disposing)
        {
            if (disposing && ThrowBeforeDispose) throw new IOException("fixture cleanup not complete");
            base.Dispose(disposing);
            if (disposing && ThrowAfterDispose) throw new IOException("fixture late cleanup notification");
        }
    }
    static void Attach(MainForm form, Control probe, string id, bool active)
    {
        probe.Tag = id;
        var host = (ModuleHost)Field(form, "moduleHost");
        Set(host, "diagnostics", new Diagnostics(Path.Combine(qa, "diagnostics")));
        ((Dictionary<string, Control>)Field(host, "controls"))[id] = probe;
        if (active) Set(form, "activeModule", probe);
    }
    static void BusyCase(string flag)
    {
        var state = State(); using (var form = new MainForm(Settings(), state, true)) using (var probe = new Probe())
        {
            typeof(Probe).GetProperty(flag).SetValue(probe, true, null); Attach(form, probe, ModuleHost.DataAnalysis, false);
            string error = null; form.ProjectReleaseError = value => error = value;
            Check(!form.TryReleaseCurrentProject() && state.ProjectLoaded && !probe.IsDisposed, "busy operation blocks release: " + flag);
            Check(!String.IsNullOrEmpty(error), "busy operation produces actionable reason: " + flag);
        }
    }
    static void DirtyCase(string id, DialogResult choice, bool saveSuccess)
    {
        var state = State(); using (var form = new MainForm(Settings(), state, true)) using (var probe = new Probe())
        {
            probe.SaveResult = saveSuccess;
            if (id == ModuleHost.ProjectCreation) probe.IsDirty = true;
            if (id == ModuleHost.DataAnalysis) probe.HasUnsavedAnalysisChanges = true;
            if (id == ModuleHost.CalibrationTest) probe.HasUnsavedCalibrationChanges = true;
            Attach(form, probe, id, true);
            int prompts = 0; Func<DialogResult> prompt = () => { prompts++; return choice; };
            form.UnsavedProjectPrompt = prompt; form.UnsavedAnalysisPrompt = prompt; form.UnsavedCalibrationPrompt = prompt;
            bool expected = choice == DialogResult.No || (choice == DialogResult.Yes && saveSuccess);
            Check(form.TryReleaseCurrentProject() == expected, "dirty release result: " + id + "/" + choice + "/" + saveSuccess);
            Check(state.ProjectLoaded != expected && probe.IsDisposed == expected, "dirty release preserves state or unloads only after consent: " + id);
            Check(prompts == 1, "single save/discard/cancel prompt, not repeated: " + id);
            Check(probe.SaveCalls == (choice == DialogResult.Yes ? 1 : 0), "only explicit save invokes save: " + id);
        }
    }
    static Dictionary<string,string> ProjectHashes()
    {
        using (var sha = SHA256.Create()) return Directory.GetFiles(project, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(p))));
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            qa = Path.Combine(Path.GetFullPath(args.Length > 1 ? args[1] : args[0]), "project-release-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            project = (string)typeof(Program).GetMethod("CreateFixtureProject", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { qa });
            var original = ProjectHashes();
            foreach (string flag in new[] { "IsBusy", "IsAnalysisRunning", "IsRunning", "HasActiveHardwareSession", "EvaluationBusy", "AutomaticEvaluationPending", "DischargeRecognitionBusy" }) BusyCase(flag);
            foreach (string code in new[] { "RUNNING", "RAMPING", "HOLDING", "FAULT", "UNRECOGNIZED" })
            {
                var state = State(); using (var form = new MainForm(Settings(), state, true)) using (var probe = new Probe { PressureStateCode = code })
                { Attach(form, probe, ModuleHost.PressureTest, true); Check(!form.TryReleaseCurrentProject() && state.ProjectLoaded, "pressure state cannot be released: " + code); }
            }
            foreach (string id in new[] { ModuleHost.ProjectCreation, ModuleHost.DataAnalysis, ModuleHost.CalibrationTest })
                foreach (var choice in new[] { DialogResult.Cancel, DialogResult.No, DialogResult.Yes })
                    DirtyCase(id, choice, true);
            foreach (string id in new[] { ModuleHost.ProjectCreation, ModuleHost.DataAnalysis, ModuleHost.CalibrationTest }) DirtyCase(id, DialogResult.Yes, false);
            foreach (var choice in new[] { DialogResult.Cancel, DialogResult.No, DialogResult.Yes })
            {
                var state = new ApplicationState(); using (var form = new MainForm(Settings(), state, true)) using (var probe = new Probe { IsDirty = true })
                {
                    Attach(form, probe, ModuleHost.ProjectCreation, true); form.UnsavedProjectPrompt = () => choice;
                    Check((bool)Call(form, "TryReleaseProjectContext", true) == (choice != DialogResult.Cancel), "new unsaved project editor can exit through original guarded flow: " + choice);
                    Check(probe.IsDisposed == (choice != DialogResult.Cancel), "new draft cancel stays open and authorized exit closes: " + choice);
                }
            }
            {
                var state = State(); using (var form = new MainForm(Settings(), state, true)) using (var probe = new ThrowingProbe())
                { Attach(form, probe, ModuleHost.CalibrationTest, false); Check(!form.TryReleaseCurrentProject() && state.ProjectLoaded && !probe.IsDisposed, "unreadable runtime state fails closed"); }
            }
            {
                var state = State(); using (var form = new MainForm(Settings(), state, true)) using (var bridge = new OfflineSimulationBridge(true))
                {
                    Set(bridge, "processing", true); Set(form, "offlineSimulationBridge", bridge);
                    Check(!form.TryReleaseCurrentProject() && state.ProjectLoaded, "bridge computation blocks release even before a visible mailbox becomes busy");
                    Set(bridge, "processing", false); Set(form, "offlineSimulationBridge", null);
                }
            }
            foreach (bool before in new[] { false, true })
            {
                var state = State(); var settings = Settings();
                using (var form = new MainForm(settings, state, true))
                {
                    var failed = new CleanupProbe { ThrowBeforeDispose = before, ThrowAfterDispose = !before };
                    var remaining = new Probe();
                    Attach(form, failed, ModuleHost.DataBrowser, false); Attach(form, remaining, ModuleHost.DataAnalysis, false);
                    var launcher = new CompetitionDefectLauncher(form); Set(form, "competitionDefectLauncher", launcher); launcher.UpdateContext(settings, state);
                    Check(form.TryReleaseCurrentProject() == !before && state.ProjectLoaded == before, "cleanup failure retains context only while a control remains live");
                    Check(remaining.IsDisposed, "cleanup error cannot skip remaining idle controls");
                    Check(CompetitionDefectProtocol.Deserialize((string)Field(launcher, "latest")).ProjectPath == (before ? state.ProjectPath : ""), "cleanup result publishes matching independent viewer context");
                    failed.ThrowBeforeDispose = failed.ThrowAfterDispose = false; failed.Dispose();
                }
            }
            {
                var state = State(); var settings = Settings();
                EventHandler observer = delegate { if (!state.ProjectLoaded) throw new IOException("fixture observer notification failure"); };
                state.Changed += observer;
                using (var form = new MainForm(settings, state, true))
                {
                    Attach(form, new Probe(), ModuleHost.DataBrowser, false);
                    var launcher = new CompetitionDefectLauncher(form); Set(form, "competitionDefectLauncher", launcher); launcher.UpdateContext(settings, state);
                    string error = null; form.ProjectReleaseError = value => error = value;
                    Check(!form.TryReleaseCurrentProject() && !state.ProjectLoaded && error != null, "observer error reports actual unloaded state without claiming project retained");
                    Check(CompetitionDefectProtocol.Deserialize((string)Field(launcher, "latest")).ProjectPath == "", "empty context delivered even when earlier state observer throws");
                    state.Changed -= observer;
                }
            }
            {
                var state = State(); using (var form = new MainForm(Settings(), state, true)) using (var probe = new Probe { IsEditingCalibration = true })
                { Attach(form, probe, ModuleHost.DataBrowser, true); Check(!form.TryReleaseCurrentProject() && state.ProjectLoaded, "browser unsaved cursor edit not discarded"); }
            }
            {
                var state = State(); using (var form = new MainForm(Settings(), state, true)) using (var probe = new Probe { HasUnsavedAnalysisChanges = true })
                { Attach(form, probe, ModuleHost.DataAnalysis, false); Check(!form.TryReleaseCurrentProject() && state.ProjectLoaded, "inactive dirty analysis not discarded"); }
            }
            foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
            {
                var state = State(); var settings = Settings(language);
                using (var form = new MainForm(settings, state, true))
                {
                    form.Opacity = 0; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show(); Application.DoEvents();
                    var tiles = (Dictionary<string, ModuleTile>)Field(form, "tiles");
                    Check(tiles[ModuleHost.ProjectCreation].Title == (language == "en" ? "Edit project" : language == "zh-TW" ? "編輯項目" : "编辑项目"), "selected project uses editing card caption: " + language);
                    Check((string)Call(form, "BuildModuleTitle", ModuleHost.ProjectCreation) == tiles[ModuleHost.ProjectCreation].Title, "inner title agrees with card caption");
                    foreach (var size in new[] { new Size(1100, 720), new Size(1920, 1080) })
                    {
                        form.ClientSize = size; Call(form, "ApplyResponsiveChrome"); Application.DoEvents();
                        var button = (Button)Field(form, "releaseProjectButton"); var card = (Panel)Field(form, "projectStatusCard");
                        Check(button.Visible && button.Enabled && button.Parent == card && card.ClientRectangle.Contains(button.Bounds), "release button inside project card: " + language + "/" + size);
                        Check(((Label)Field(form, "projectDetail")).Right < button.Left && ((Label)Field(form, "projectStatus")).Right < button.Left, "release button cannot cover project labels");
                        Check(button.Width >= TextRenderer.MeasureText(button.Text, button.Font).Width + 12, "release button label complete");
                    }
                    var launcher = new CompetitionDefectLauncher(form); Set(form, "competitionDefectLauncher", launcher); launcher.UpdateContext(settings, state);
                    Check(form.TryReleaseCurrentProject() && !state.ProjectLoaded, "explicit release unloads current project");
                    Check(!((Button)Field(form, "releaseProjectButton")).Enabled, "released project button disabled");
                    foreach (string id in new[] { "DataBrowser", "DataAnalysis", "ReportGenerator", "CalibrationTest", "PressureTest", "WithstandTest", "CompetitionDefect" }) Check(!tiles[id].Enabled, "dependent tile disabled after release: " + id);
                    string frame = (string)Field(launcher, "latest"); var context = CompetitionDefectProtocol.Deserialize(frame);
                    Check(context.ProjectPath == "" && !(bool)Field(launcher, "disposed"), "live independent launcher receives empty project and remains independent");
                    Check(tiles[ModuleHost.ProjectCreation].Title == AppLanguages.Text("ProjectCreation", language), "unloaded project restores create caption");
                    Check(!form.TryReleaseCurrentProject(), "repeat release is harmless");
                    string ignored; Check(state.TryLoadProject(project, out ignored), "released files reload successfully");
                    using (var bmp = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size)); bmp.Save(Path.Combine(qa, "release-home-" + language + ".png")); }
                    form.Close();
                }
            }
            var final = ProjectHashes(); Check(original.Count == final.Count && original.All(p => final.ContainsKey(p.Key) && final[p.Key] == p.Value), "release never deletes or alters any project fixture bytes");
            Console.WriteLine("PROJECT_RELEASE_PASS=" + checks + ";HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False;EVIDENCE=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
