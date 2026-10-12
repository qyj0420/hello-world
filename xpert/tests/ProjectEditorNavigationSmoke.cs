using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ProjectEditorNavigationSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static object Field(object o, string n) { return o.GetType().GetField(n, Hidden).GetValue(o); }
    static void Set(object o, string n, object v) { o.GetType().GetField(n, Hidden).SetValue(o, v); }
    static object Call(object o, string n, params object[] args) { return o.GetType().GetMethod(n, Hidden).Invoke(o, args); }
    static void Check(bool ok, string label) { checks++; if (!ok) throw new Exception(label); }
    sealed class Probe : UserControl
    {
        public bool IsDirty { get; set; }
        public bool SaveResult = true;
        public bool ThrowOnSave;
        public int Saves;
        public string LastSavedProjectOperationCode { get; set; }
        public bool SaveProject() { Saves++; if (ThrowOnSave) throw new IOException("synthetic save failure"); if (SaveResult) IsDirty = false; return SaveResult; }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); string qa = Path.Combine(Path.GetFullPath(args[0]), "editor-navigation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            foreach (string route in new[] { "home", "close", "switch" }) foreach (DialogResult choice in new[] { DialogResult.Cancel, DialogResult.No, DialogResult.Yes }) foreach (bool saveSuccess in new[] { false, true })
            {
                var settings = new AppSettings(); settings.Hardware.Enabled = false;
                using (var form = new MainForm(settings, new ApplicationState(), true)) using (var probe = new Probe { IsDirty = true, SaveResult = saveSuccess }) using (var next = new UserControl())
                {
                    probe.Tag = ModuleHost.ProjectCreation; Set(form, "activeModule", probe);
                    var host = (ModuleHost)Field(form, "moduleHost"); Set(host, "diagnostics", new Diagnostics(Path.Combine(qa, "diagnostics"))); ((Dictionary<string, Control>)Field(host, "controls"))[ModuleHost.ProjectCreation] = probe;
                    int prompts = 0; form.UnsavedProjectPrompt = () => { prompts++; return choice; };
                    bool allow = choice == DialogResult.No || choice == DialogResult.Yes && saveSuccess;
                    if (route == "home") Call(form, "ShowHome");
                    else if (route == "switch") Call(form, "ShowEmbeddedModule", ModuleHost.DataBrowser, next, null);
                    else { var e = new FormClosingEventArgs(CloseReason.UserClosing, false); Call(form, "MainFormClosing", form, e); Check(e.Cancel, "child-page close never closes shell"); }
                    Check(prompts == 1, "exactly one draft prompt: " + route);
                    Check(probe.Saves == (choice == DialogResult.Yes ? 1 : 0), "only explicit save calls writer");
                    Check(probe.IsDisposed == allow, "dispose only after allowed outcome: " + route + "/" + choice + "/" + saveSuccess);
                    if (!allow) Check(Object.ReferenceEquals(Field(form, "activeModule"), probe), "cancel/failure retains exact editor instance");
                    else Check(Object.ReferenceEquals(Field(form, "activeModule"), route == "switch" ? next : null), "authorized destination active");
                }
            }
            foreach (string route in new[] { "home", "close", "switch" })
            {
                var settings = new AppSettings(); settings.Hardware.Enabled = false;
                using (var form = new MainForm(settings, new ApplicationState(), true)) using (var probe = new Probe { IsDirty = true, ThrowOnSave = true }) using (var next = new UserControl())
                {
                    probe.Tag = ModuleHost.ProjectCreation; Set(form, "activeModule", probe);
                    var host = (ModuleHost)Field(form, "moduleHost"); Set(host, "diagnostics", new Diagnostics(Path.Combine(qa, "diagnostics"))); ((Dictionary<string, Control>)Field(host, "controls"))[ModuleHost.ProjectCreation] = probe;
                    form.UnsavedProjectPrompt = () => DialogResult.Yes; string notice = null; form.ProjectReleaseError = value => notice = value;
                    if (route == "home") Call(form, "ShowHome");
                    else if (route == "switch") Call(form, "ShowEmbeddedModule", ModuleHost.DataBrowser, next, null);
                    else Call(form, "MainFormClosing", form, new FormClosingEventArgs(CloseReason.UserClosing, false));
                    Check(!probe.IsDisposed && Object.ReferenceEquals(Field(form, "activeModule"), probe), "save exception keeps editor alive: " + route);
                    Check(notice != null && probe.Saves == 1, "save exception has actionable message and no duplicate save");
                }
            }
            string project = Path.Combine(qa,"joint-updates");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"[ITEM]\r\n");File.WriteAllText(Path.Combine(project,"CABLE.in"),"[CABLE]\r\n");
            foreach(string operation in new[]{"JOINT_UPDATE","CREATE","UPDATE"}) {
                var settings=new AppSettings();settings.Hardware.Enabled=false;var state=new ApplicationState();
                using(var form=new MainForm(settings,state,true))using(var probe=new Probe{LastSavedProjectOperationCode=operation}) {
                    probe.Tag=ModuleHost.ProjectCreation;Set(form,"activeModule",probe);
                    var host=(ModuleHost)Field(form,"moduleHost");Set(host,"diagnostics",new Diagnostics(Path.Combine(qa,"diagnostics")));((Dictionary<string,Control>)Field(host,"controls"))[ModuleHost.ProjectCreation]=probe;
                    Call(form,"ProjectLoadRequested",project);
                    Check(state.ProjectLoaded,"saved joint/editor notification still updates project state");
                    Check((Field(form,"activeModule")==probe)==(operation=="JOINT_UPDATE"),"joint update stays editor while ordinary completed save returns home: "+operation);
                }
            }
            Console.WriteLine("PASS " + checks + "; " + qa); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
