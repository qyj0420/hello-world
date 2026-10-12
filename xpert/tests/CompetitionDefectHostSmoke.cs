using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CompetitionDefectHostSmoke
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly host; static int checks; static long revision; static string preferencesPath;
    static Type T(string name) { return host.GetType("Xiongxin.OWTS.Desktop." + name, true); }
    static void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
    static object Call(object instance, string name, params object[] values)
    { Type type = instance as Type ?? instance.GetType(); return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(instance is Type ? null : instance, values); }
    static void Set(object o, string name, object value) { o.GetType().GetProperty(name, All).SetValue(o, value, null); }
    static object Field(object o, string name) { return o.GetType().GetField(name, All).GetValue(o); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (Exception e) { if (e is TargetInvocationException) e = e.InnerException; rejected = e is InvalidDataException || e is IOException || e is InvalidOperationException || e is UnauthorizedAccessException || e is DecoderFallbackException; } Check(rejected, message); }
    static object Context(string project)
    {
        object context = Activator.CreateInstance(T("CompetitionDefectContext"), true);
        Set(context, "Protocol", 2); Set(context, "Revision", ++revision); Set(context, "Activation", 1L);
        Set(context, "ProjectPath", project); return context;
    }
    static object Preferences(string language = "zh-CN", bool ai = true, string model = "")
    {
        object value = Activator.CreateInstance(T("CompetitionDefectPreferences"), true);
        Set(value, "LanguageCode", language); Set(value, "EnableAiFeatures", ai); Set(value, "ModelLibraryPath", model); return value;
    }
    static Form Form(object context, Func<Control> factory)
    { return (Form)Activator.CreateInstance(T("CompetitionDefectHostForm"), All, null, new object[] { context, factory, Preferences(), preferencesPath }, null); }
    static void Pump(Func<bool> done)
    { Stopwatch clock = Stopwatch.StartNew(); while (!done() && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); System.Threading.Thread.Sleep(5); } Check(done(), "Asynchronous IPC update completes within five seconds"); }
    public sealed class FakeRecognition : UserControl
    {
        string project; public int Resets, Loads; public string Result;
        public string ProjectPath { get { return project; } set { project = value; Result = null; if (String.IsNullOrEmpty(value)) Resets++; else Loads++; } }
        public int LanguageIndex { get; set; }
        public bool EnableAiFeatures { get; set; }
        public string ModelLibraryPath { get; set; }
    }
    static void Framing()
    {
        Type protocol = T("CompetitionDefectProtocol");
        string pipeName = "HVX.Defect." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
        string token = new string('a', 64);
        Check((int)Call(protocol, "ValidateArguments", pipeName, token) == Process.GetCurrentProcess().Id, "Parent PID is bound to a restricted pipe name");
        foreach (string invalid in new[] { "other", "\\\\remote\\pipe\\foo", pipeName + "\n", "HVX.Defect.0." + new string('a', 32) })
            Reject(() => Call(protocol, "ValidateArguments", invalid, token), "Reject malformed/remote pipe argument");
        Reject(() => Call(protocol, "ValidateArguments", pipeName, "bad"), "Reject malformed launch token");
        using (var memory = new MemoryStream())
        {
            Call(protocol, "WriteText", memory, "项目A\r\nUnicode 测试"); memory.Position = 0;
            Check((string)Call(protocol, "ReadText", memory) == "项目A\r\nUnicode 测试", "Bounded length framing preserves Unicode and embedded newlines");
        }
        foreach (int length in new[] { 0, -1, 65537, Int32.MaxValue })
            using (var memory = new MemoryStream(BitConverter.GetBytes(length))) Reject(() => Call(protocol, "ReadText", memory), "Oversized or negative frame rejected before allocation");
        using (var memory = new MemoryStream(new byte[] { 5, 0, 0, 0, 1 })) Reject(() => Call(protocol, "ReadText", memory), "Truncated frame fails closed");
        using (var memory = new MemoryStream(new byte[] { 1, 0, 0, 0, 255 })) Reject(() => Call(protocol, "ReadText", memory), "Invalid UTF8 fails closed");
        Reject(() => Call(protocol, "Deserialize", "{\"Protocol\":1,\"Revision\":1}"), "Old shared-settings protocol is rejected");
        Check(T("CompetitionDefectContext").GetProperties().Select(p => p.Name).OrderBy(n => n).SequenceEqual(new[] { "Activation", "ProjectPath", "Protocol", "Revision" }), "IPC carries only project and synchronization metadata, not parent AI/model/language or entry switch");
        using (var server = (NamedPipeServerStream)Call(protocol, "CreateServer", pipeName))
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            var waiting = Task.Run(() => server.WaitForConnection()); client.Connect(1000); Check(waiting.Wait(2000), "Per-user local pipe connects");
            var acl = server.GetAccessControl(); var sid = WindowsIdentity.GetCurrent().User;
            Check(acl.AreAccessRulesProtected, "Pipe ACL disables inherited broad access");
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            Check(rules.Length > 0 && rules.All(rule => rule.IdentityReference.Equals(sid)), "Only current SID is granted pipe access");
            Call(protocol, "VerifyPeer", server, Process.GetCurrentProcess().Id, true);
            Call(protocol, "VerifyPeer", client, Process.GetCurrentProcess().Id, false); Check(true, "Both real pipe peer process IDs verified");
            Reject(() => Call(protocol, "VerifyPeer", server, 1, true), "Wrong child process is rejected");
            Reject(() => Call(protocol, "VerifyPeer", client, 1, false), "Wrong server process is rejected");
        }
    }
    static void HostUi(string a, string b)
    {
        int factories = 0; FakeRecognition recognition = null;
        Func<Control> factory = () => { factories++; return recognition = new FakeRecognition(); };
        using (Form form = Form(Context(""), factory))
        {
            Check(form.WindowState == FormWindowState.Maximized, "Independent window opens maximized");
            Call(form, "ApplyContext", Context("")); Check(factories == 0, "No project does not create the module");
            Call(form, "ApplyContext", Context(a)); Check(factories == 1 && recognition.ProjectPath == a && recognition.Enabled, "Valid project is passed to isolated control");
            recognition.Result = "A recognition"; int resets = recognition.Resets, loads = recognition.Loads;
            for (int i = 0; i < 20; i++) Call(form, "ApplyContext", Context(a));
            Check(recognition.Resets == resets && recognition.Loads == loads && recognition.Result != null, "Repeated state notifications/heartbeat preserve result and selection");
            Call(form, "ApplyPreferences", Preferences("en"), false);
            Check(recognition.LanguageIndex == 1 && recognition.Resets == resets && recognition.Result != null, "Language only updates UI without reloading data");
            Call(form, "ApplyPreferences", Preferences("hr"), false);
            Check(form.Text.Contains("Independent") && recognition.Resets == resets && ((Label)Field(form, "status")).Text.EndsWith(a), "Other supported foreign language uses English fallback without modifying the project path");
            Call(form, "ApplyPreferences", Preferences("zh-TW"), false);
            Check(form.Text.Contains("獨立") && recognition.Resets == resets && ((Label)Field(form, "status")).Text.EndsWith(a), "Traditional Chinese converts built-in UI only, never paths");
            object stale = Context(a); object newer = Context(b); Call(form, "ApplyContext", newer);
            Check(recognition.ProjectPath == b && recognition.Result == null, "Project switch clears old result before loading B");
            Call(form, "ApplyContext", stale); Check(recognition.ProjectPath == b, "Outdated IPC revision cannot restore project A");
            recognition.Result = "B recognition"; Call(form, "ApplyContext", Context(""));
            Check(recognition.ProjectPath == "" && !recognition.Enabled && recognition.Result == null, "Project unload clears and disables result/learning surface");
            Call(form, "ApplyContext", Context(b)); recognition.Result = "B";
            int loadsBeforePrefs = recognition.Loads;
            Call(form, "ApplyPreferences", Preferences("zh-CN", false), false); Check(!recognition.EnableAiFeatures && recognition.Loads == loadsBeforePrefs, "Independent AI setting reaches control without reloading the selected archive");
            string localModel = Path.Combine(a, "independent.pdm");
            Call(form, "ApplyPreferences", Preferences("zh-CN", false, localModel), false);
            Check(recognition.ModelLibraryPath == localModel && recognition.Loads == loadsBeforePrefs, "Independent model setting reaches control without project reload");
            Call(form, "ApplyContext", Context(Path.Combine(b, "missing"))); Check(!recognition.Enabled && recognition.ProjectPath == "", "Invalid project fails closed, never falls back to last project");
            Call(form, "ApplyContext", Context(b)); recognition.Result = "B";
            Call(form, "MarkDisconnected"); Check(recognition.Enabled && recognition.ProjectPath == b && recognition.Result == "B", "Disconnect preserves the last project and recognition state");
            Check(((Label)Field(form, "headerSubtitle")).Text.Contains("主程序已断开"), "Independent window clearly indicates loss of synchronization");
        }
        Check(recognition.IsDisposed, "Host owns and disposes its recognition control");
    }
    static void LiveChannel(string a, string b)
    {
        foreach (string last in new[] { a, b, String.Empty })
        {
        string pipeName = "HVX.Defect." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
        Type protocol = T("CompetitionDefectProtocol"); FakeRecognition recognition = null;
        using (var server = (NamedPipeServerStream)Call(protocol, "CreateServer", pipeName))
        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        using (Form form = Form(Context(a), () => recognition = new FakeRecognition()))
        {
            var waiting = Task.Run(() => server.WaitForConnection()); client.Connect(1000); Check(waiting.Wait(2000), "Host test channel established");
            form.WindowState = FormWindowState.Normal; form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show();
            Call(form, "BeginReceive", client);
            Call(protocol, "WriteText", server, Call(protocol, "Serialize", Context(b)));
            Pump(() => recognition.ProjectPath == b); Check(recognition.Enabled, "Background IPC marshals project update onto UI thread");
            Call(protocol, "WriteText", server, Call(protocol, "Serialize", Context("")));
            Pump(() => !recognition.Enabled); Check(recognition.ProjectPath == "" && form.Visible, "Remote project unload clears data but preserves a disabled window for next project");
            Call(protocol, "WriteText", server, Call(protocol, "Serialize", Context(last)));
            server.Dispose(); Pump(() => !(bool)Field(form, "syncConnected"));
            Check(!form.IsDisposed && form.Visible && recognition.ProjectPath == last, "Last queued project/clear update is applied before EOF; independent child remains open");
            Check(recognition.Enabled == (last.Length > 0), "Last explicit clear remains cleared after disconnect, without reviving an older project");
        }
        }
    }
    static void LauncherGate()
    {
        object settings = Activator.CreateInstance(T("AppSettings"), true), state = Activator.CreateInstance(T("ApplicationState"), true);
        using (var owner = new Form())
        using (var launcher = (IDisposable)Activator.CreateInstance(T("CompetitionDefectLauncher"), All, null, new object[] { owner }, null))
        {
            Set(settings, "EnableCompetitionDefectModule", false);
            Reject(() => Call(launcher, "Open", settings, state), "Disabled direct Open is rejected independently of home tile");
            Set(settings, "EnableCompetitionDefectModule", true);
            Reject(() => Call(launcher, "Open", settings, state), "No-project direct Open is rejected independently of home tile");
            Check(Field(launcher, "child") == null && Field(launcher, "channel") == null, "Rejected entry starts no child process or IPC server");
            Call(launcher, "UpdateContext", settings, state);
        }
        Check(!T("CompetitionDefectContext").GetProperties().Any(p => p.Name.IndexOf("Hardware", StringComparison.OrdinalIgnoreCase) >= 0), "IPC contract carries no hardware configuration");
        Check(!T("CompetitionDefectHostForm").GetFields(All).Any(f => f.FieldType.Name.Contains("Hardware") || f.FieldType.Name == "MainForm" || f.FieldType.Name == "ModuleHost"), "Independent host has no hardware service or main-form instance");
    }
    static object LoadPreferences(string path, out string warning)
    {
        object[] arguments = { path, null };
        object value = T("CompetitionDefectPreferences").GetMethod("Load", All).Invoke(null, arguments);
        warning = (string)arguments[1]; return value;
    }
    static object Get(object value, string property) { return value.GetType().GetProperty(property, All).GetValue(value, null); }
    static void PreferencesRoundtrip(string qa)
    {
        Type type = T("CompetitionDefectPreferences");
        string defaults = (string)type.GetProperty("DefaultPath", All).GetValue(null, null);
        string main = (string)T("AppPaths").GetProperty("SettingsFile", All).GetValue(null, null);
        Check(!String.Equals(defaults, main, StringComparison.OrdinalIgnoreCase) && defaults.Contains("CompetitionDefectRecognition"), "Independent preferences use a distinct application data directory");
        string warning;
        object fresh = LoadPreferences(preferencesPath, out warning);
        Check((string)Get(fresh, "LanguageCode") == "system" && (bool)Get(fresh, "EnableAiFeatures") && (string)Get(fresh, "ModelLibraryPath") == "" && warning == "", "Missing independent profile defaults to system language, own AI on and bundled model");
        Check(!File.Exists(preferencesPath), "Reading defaults does not create a configuration file");
        Call(Preferences("en"), "Save", preferencesPath);
        string original = File.ReadAllText(preferencesPath);
        string ownModel = Path.Combine(qa, "独立模型.pdm"); File.WriteAllText(ownModel, "offline model fixture");
        Call(Preferences("zh-TW", false, ownModel), "Save", preferencesPath);
        object loaded = LoadPreferences(preferencesPath, out warning);
        Check(warning == "" && (string)Get(loaded, "LanguageCode") == "zh-TW" && !(bool)Get(loaded, "EnableAiFeatures") && (string)Get(loaded, "ModelLibraryPath") == ownModel, "Independent language/AI/model persist and reload exactly");
        Check(File.ReadAllText(preferencesPath + ".bak") == original, "Atomic preference replacement preserves previous complete settings as backup");
        string beforeInvalid = File.ReadAllText(preferencesPath);
        Reject(() => Call(Preferences("not-a-language"), "Save", preferencesPath), "Invalid language cannot overwrite preferences");
        Reject(() => Call(Preferences("en", true, "relative-model.pdm"), "Save", preferencesPath), "Relative model path is rejected rather than resolved using a parent directory");
        Check(File.ReadAllText(preferencesPath) == beforeInvalid, "Failed validation leaves saved independent settings unchanged");
        string broken = Path.Combine(qa, "bad-preferences.json"); File.WriteAllText(broken, "{broken");
        LoadPreferences(broken, out warning); Check(warning.Length > 0 && File.ReadAllText(broken) == "{broken", "Corrupt independent preferences warn and remain untouched");
        File.WriteAllText(broken, new string('a', 40000)); LoadPreferences(broken, out warning);
        Check(warning.Length > 0 && new FileInfo(broken).Length == 40000, "Oversized preferences are bounded and preserved");
        using (var dialog = (Form)Activator.CreateInstance(T("CompetitionDefectPreferencesDialog"), All, null, new[] { Preferences("zh-TW") }, null))
        {
            Check(dialog.StartPosition == FormStartPosition.CenterParent && dialog.Controls.Find("CompetitionLanguage", true).Length == 1, "Independent settings dialog is centered and exposes its own language field");
            Check(File.ReadAllText(preferencesPath) == beforeInvalid, "Opening or cancelling a settings form cannot save implicitly");
        }
    }
    static void ParentSettingsIsolation(string a)
    {
        object settings = Activator.CreateInstance(T("AppSettings"), true), state = Activator.CreateInstance(T("ApplicationState"), true);
        Check((bool)Call(state, "TryLoadProject", a, null), "Parent settings isolation uses a valid isolated project");
        FakeRecognition recognition = null;
        using (Form form = Form(Context(a), () => recognition = new FakeRecognition()))
        {
            string ownModel = Path.Combine(a, "own-model.pdm");
            Call(form, "ApplyPreferences", Preferences("zh-TW", false, ownModel), false);
            recognition.Result = "independent result"; int resets = recognition.Resets;
            Set(settings, "LanguageCode", "en"); Set(settings, "EnableAiFeatures", true); Set(settings, "EnableCompetitionDefectModule", false);
            Set(Get(settings, "TaskMinus3"), "ModelLibraryPath", Path.Combine(a, "MAIN-model.pdm"));
            object context = Call(T("CompetitionDefectContext"), "Create", settings, state);
            Set(context, "Revision", ++revision); Set(context, "Activation", 1L);
            Call(form, "ApplyContext", context);
            Check(recognition.LanguageIndex == 18 && !recognition.EnableAiFeatures && recognition.ModelLibraryPath == ownModel, "Changing parent language, AI, model and factory entry switch cannot replace independent preferences");
            Check(recognition.Result == "independent result" && recognition.Resets == resets && recognition.Enabled, "Parent settings updates preserve independent result, selected archive and open window");
            Check(!form.IsDisposed, "Factory switch only controls launching; it does not terminate an existing independent program");
        }
    }
    static void RealControl(string a, string b, string qa)
    {
        using (Form form = Form(Context(a), null))
        {
            Check(form.WindowState == FormWindowState.Maximized, "Actual module defaults to maximized");
            var actual = Field(form, "recognition") as Control;
            Check(actual != null && actual.GetType().FullName == "HVX.PdReview.CompetitionDefectRecognitionControl", "Default host resolver loads the exact real analysis control");
            Check((string)actual.GetType().GetProperty("ProjectPath").GetValue(actual, null) == a && actual.Enabled, "Real control receives the current project without a main form or hardware service");
            form.WindowState = FormWindowState.Normal; form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ClientSize = new Size(920, 650); form.Show(); Application.DoEvents();
            Call(form, "ApplyContext", Context(b)); Call(form, "ApplyPreferences", Preferences("zh-TW"), false); Application.DoEvents();
            Check((string)actual.GetType().GetProperty("ProjectPath").GetValue(actual, null) == b, "Real control synchronizes a project switch");
            Check((int)actual.GetType().GetProperty("LanguageIndex").GetValue(actual, null) == 18, "Real control receives Traditional Chinese index unchanged");
            var status = (Label)Field(form, "status"); var content = (Panel)Field(form, "content");
            var projectHeader = (Panel)Field(form, "projectHeader");
            Check(projectHeader.Bottom <= content.Top && status.Width <= form.ClientSize.Width, "Project header remains separate from the actual analysis surface");
            Check(status.AutoEllipsis && (string)status.AccessibleDescription == b, "Long project path has a full accessible description without consuming plot height");
            var logo = (PictureBox)Field(form, "headerLogo");
            Check(logo.Image != null && logo.SizeMode == PictureBoxSizeMode.Zoom, "Original complete XIONGXIN logo is displayed without stretching or cropping");
            object reactivation = Context(b); Set(reactivation, "Activation", 2L);
            form.Opacity = 0; Call(form, "ApplyContext", reactivation);
            Check(form.WindowState == FormWindowState.Maximized, "Reopening existing module restores maximized view");
            form.WindowState = FormWindowState.Normal; form.ClientSize = new Size(920, 650); form.Location = new Point(-20000, -20000); Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(qa, "real-standalone-host.png")); }
            Call(form, "ApplyContext", Context("")); Check(!actual.Enabled && (string)actual.GetType().GetProperty("ProjectPath").GetValue(actual, null) == "", "Actual control clears and disables on project unload");
            Call(form, "ApplyContext", Context(b));
            Call(form, "ApplyPreferences", Preferences("zh-TW", false), true);
            Check(!(bool)actual.GetType().GetProperty("EnableAiFeatures").GetValue(actual, null), "Actual control uses its independent AI-off preference");
            Reject(() => Call(actual, "RecognizeCurrentDischargeAsync"), "Actual recognition API refuses work when this module's AI preference is off");
            string model = (string)actual.GetType().GetProperty("ModelLibraryPath").GetValue(actual, null);
            Check(model == Path.Combine(Path.GetDirectoryName(actual.GetType().Assembly.Location), "Task12", "pd_model_machine.pdm"), "Default model resolves to this program's bundled model, not the main TaskMinus3 preference");
            Call(form, "MarkDisconnected"); Check(!form.IsDisposed && actual.Enabled && (string)actual.GetType().GetProperty("ProjectPath").GetValue(actual, null) == b, "Actual standalone program preserves the last project after main application exit");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            host = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "Xiongxin.OWTS.Desktop.exe"));
            string qa = Path.Combine(Path.GetFullPath(args[1]), "defect-host-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            preferencesPath = Path.Combine(qa, "independent-settings", "preferences.json");
            string a = Path.Combine(qa, "项目 A"), b = Path.Combine(qa, "项目 B");
            foreach (string project in new[] { a, b }) { Directory.CreateDirectory(project); File.WriteAllText(Path.Combine(project, "ITEM.in"), "test"); File.WriteAllText(Path.Combine(project, "CABLE.in"), "test"); }
            Framing(); PreferencesRoundtrip(qa); LauncherGate(); HostUi(a, b); ParentSettingsIsolation(a); LiveChannel(a, b); RealControl(a, b, qa);
            foreach (string project in new[] { a, b }) Check(Directory.GetFiles(project).Length == 2 && File.ReadAllText(Path.Combine(project, "ITEM.in")) == "test" && File.ReadAllText(Path.Combine(project, "CABLE.in")) == "test", "Host never mutates current project files");
            Console.WriteLine("CompetitionDefectHostSmoke PASS: " + checks + " checks; " + qa); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
