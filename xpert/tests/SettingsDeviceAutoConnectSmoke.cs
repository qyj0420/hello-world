using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class SettingsDeviceAutoConnectSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static object Field(object o, string name) { return o.GetType().GetField(name, Hidden).GetValue(o); }
    static void SetField(object o, string name, object value) { o.GetType().GetField(name, Hidden).SetValue(o, value); }
    static object Call(object o, string method, params object[] args) { return o.GetType().GetMethod(method, Hidden).Invoke(o, args); }
    static DetectionDeviceList Devices(int count, DateTime now, string state = "Available", bool bound = true)
    {
        string rows = String.Concat(Enumerable.Range(1, count).Select(i => "<Device Mac=\"00112233445" + i + "\" Ip=\"192.0.2." + i + "\" ControllerLocation=\"H11\" AcquisitionLocation=\"S51\" PersistentBound=\"" + bound.ToString().ToLowerInvariant() + "\" Complete=\"true\" State=\"" + state + "\"/>"));
        return HardwareDeviceDiscovery.Parse("<HardwareDiscovery Schema=\"1\" State=\"Complete\" MaxAgeSeconds=\"30\" Utc=\"" + now.ToString("O") + "\">" + rows + "</HardwareDiscovery>", now);
    }
    static void Selection()
    {
        DateTime now = DateTime.UtcNow;
        var one = Devices(1, now);
        Check(MainForm.UniqueAvailableDevice(one, now) == one.Devices[0], "One available device can be selected automatically");
        Check(MainForm.UniqueAvailableDevice(Devices(2, now), now) == null, "Two available devices require explicit selection");
        Check(MainForm.UniqueAvailableDevice(Devices(0, now), now) == null, "Empty discovery never invents a device");
        Check(MainForm.UniqueAvailableDevice(Devices(1, now, "InUse"), now) == null, "Busy device is not connected automatically");
        Check(MainForm.UniqueAvailableDevice(one, now.AddSeconds(31)) == null, "Stale discovery is never selected");
        Check(MainForm.UniqueAvailableDevice(null, now) == null, "No receipt is not availability");
        Check(!MainForm.UniqueAvailableDevice(Devices(1, now, "Available", false), now).PersistentBound, "Automatic selection does not fabricate binding");
    }
    static void Draft()
    {
        var basis = new AppSettings(); var draft = basis.Clone(); var latest = basis.Clone();
        draft.ReportOutputPath = "unsaved-report-folder"; draft.InductanceHenry = 9.125;
        draft.Hardware.Enabled = true; draft.TaskMinus3.CorrelationErrorPercent = 41.5;
        draft.Hardware.DriverPackagePath = "unsaved-driver-package";
        draft.Hardware.Task39InputSettingsPath = "unsaved-input-settings";
        draft.Hardware.IntegrationExecutablePath = "not-a-verified-connector";
        latest.DividerRatio = 12500; latest.Hardware.SelectedHubMac = "001122334451"; latest.Hardware.Enabled = true;
        latest.Hardware.ControllerSerial = "1234567890"; latest.Hardware.AcquisitionSerial = "AB123/4567";
        var merged = DeviceSearchSettingsDraft.Rebase(basis, draft, latest, true);
        Check(merged.DividerRatio == 12500, "Untouched parameter uses newly matched catalog value");
        Check(merged.InductanceHenry == 9.125 && merged.ReportOutputPath == draft.ReportOutputPath, "Unrelated explicit edits retained unsaved");
        Check(merged.TaskMinus3.CorrelationErrorPercent == 41.5, "Nested analysis edits retained");
        Check(merged.Hardware.SelectedHubMac == latest.Hardware.SelectedHubMac && merged.Hardware.ControllerSerial == latest.Hardware.ControllerSerial, "Connection identity replaces incomplete draft fields");
        Check(merged.Hardware.DriverPackagePath == draft.Hardware.DriverPackagePath && merged.Hardware.Task39InputSettingsPath == draft.Hardware.Task39InputSettingsPath, "Non-connection hardware path edits remain in the unsaved draft");
        Check(merged.Hardware.IntegrationExecutablePath == latest.Hardware.IntegrationExecutablePath, "Draft cannot replace the verified connection executable");
        Check(!DeviceSetupStatus.ParametersSaved(merged), "Discovery cannot confirm device parameters");
        Check(basis.DividerRatio == 12 && latest.InductanceHenry == basis.InductanceHenry, "Merge mutates neither baseline nor committed settings");
        var canceled = DeviceSearchSettingsDraft.Rebase(basis, draft, basis, false);
        Check(canceled.Hardware.Enabled && canceled.Hardware.SelectedHubMac == "", "Cancellation retains unfinished device edits");
        merged.TaskMinus3.CorrelationErrorPercent = 1; merged.Hardware.SelectedHubMac = "changed";
        Check(draft.TaskMinus3.CorrelationErrorPercent == 41.5 && latest.Hardware.SelectedHubMac == "001122334451", "Merged nested objects are independent");
    }
    static void Ui(string output)
    {
        foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
        using (var form = new SettingsForm(new AppSettings { LanguageCode = language }, SettingsSection.SystemAdmin))
        {
            form.AuditEnabled = false;
            var button = form.Controls.Find("SearchAndConnectAvailableDevice", true).Single() as Button;
            Check(button != null && button.Enabled, "Button available on hardware page: " + language);
            Check(language != "en" || button.Text == "Search and connect", "English button caption");
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            Check(button.Visible && button.Parent.ClientRectangle.Contains(button.Bounds), "Button fits hardware page");
            if (language == "zh-CN")
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, "settings-auto-connect.png")); }
            ((CheckBox)Field(form, "hardwareEnabled")).Checked = true;
            ((TextBox)Field(form, "selectedHubMac")).Text = "";
            ((TextBox)Field(form, "reportOutputPath")).Text = "retained-unsaved-path";
            button.PerformClick();
            Check(form.DialogResult == DialogResult.Retry && form.DeviceSearchDraft != null, "Click requests discovery without requiring a populated MAC");
            Check(form.DeviceSearchDraft.ReportOutputPath == "retained-unsaved-path", "Draft is captured before leaving editor");
            Check(!form.Settings.Hardware.Enabled, "Click does not save incomplete hardware settings");
            Call(form, "RequestDeviceSearch"); Check(!button.Enabled, "Repeated request cannot start duplicate discovery");
        }
        using (var form = new SettingsForm(new AppSettings()))
        {
            form.AuditEnabled = false; form.DeviceSearchAvailable = false;
            Call(form, "SelectSection", SettingsSection.SystemAdmin);
            Check(!form.Controls.Find("SearchAndConnectAvailableDevice", true).Single().Enabled, "Unavailable search remains disabled when hardware tab is opened later");
            Call(form, "RequestDeviceSearch"); Check(form.DeviceSearchDraft == null, "Disabled request does not exit settings");
        }
    }
    static void Workflow(string output, string mode)
    {
        Action<string> trace = message => File.AppendAllText(Path.Combine(output,"workflow-trace.txt"),mode+" "+message+Environment.NewLine);
        trace("construct");
        var initial = new AppSettings { DataRoot = output }; var state = new ApplicationState();
        using (var form = new MainForm(initial, state, true))
        using (var timer = new Timer { Interval = 30 })
        {
            int phase = 0, connects = 0, selections = 0, notices = 0;
            Exception failure = null;
            var receipt = Devices(mode == "multiple" || mode == "cancel" ? 2 : 1, DateTime.UtcNow);
            // Real discovery completes outside this UI timer. Do not inline its
            // modal-editor continuation inside Tick: WinForms suppresses reentry
            // of the same timer while the reopened dialog is waiting for input.
            var pending = new TaskCompletionSource<DetectionDeviceList>(TaskCreationOptions.RunContinuationsAsynchronously);
            form.DiscoverDetectorsForTests = token => pending.Task;
            form.SelectDetectorForTests = list => { trace("select");selections++; return mode == "cancel" ? null : list.Devices.Last(); };
            form.ConnectDetectorForTests = (selected, token) =>
            {
                trace("connect");connects++;
                var next = ((AppSettings)Field(form, "settings")).Clone(); next.Hardware.Enabled = true;
                next.Hardware.SelectedHubMac = selected.Mac; next.Hardware.ControllerLocation = selected.ControllerLocation; next.Hardware.AcquisitionLocation = selected.AcquisitionLocation;
                next.Hardware.ControllerSerial = "1234567890"; next.Hardware.AcquisitionSerial = "AB123/4567";
                SetField(form, "settings", next); return Task.FromResult(true);
            };
            form.DeviceConnectionNoticeForTests = message => { notices++; };
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            timer.Tick += (sender, args) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().OfType<SettingsForm>().FirstOrDefault();
                try
                {
                    if (phase<3&&DateTime.UtcNow > deadline) throw new Exception("Settings discovery workflow timed out");
                    if (phase == 0 && dialog != null)
                    {
                        trace("search click");
                        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000);
                        ((TextBox)Field(dialog, "reportOutputPath")).Text = "workflow-unsaved-path";
                        ((CheckBox)Field(dialog, "hardwareEnabled")).Checked = true;
                        var search=(Button)dialog.Controls.Find("SearchAndConnectAvailableDevice", true).Single();
                        trace("button enabled="+search.Enabled+" visible="+search.Visible+" owner="+(dialog.Owner==form));
                        Check(search.Enabled&&search.Visible,"Search action available in "+mode);
                        phase = 1; search.PerformClick();
                        trace("clicked result="+dialog.DialogResult+" draft="+(dialog.DeviceSearchDraft!=null));
                    }
                    else if (phase == 1 && dialog == null)
                    {
                        trace("complete discovery");
                        Check((bool)Field(form, "detectionSelectionBusy") && !form.Enabled && form.UseWaitCursor, "Search shows busy state and blocks duplicate commands");
                        phase = 2;
                        if (mode == "failure") pending.SetException(new IOException("synthetic discovery failure")); else pending.SetResult(receipt);
                    }
                    else if (phase == 2 && dialog != null)
                    {
                        trace("cancel reopened editor");
                        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000);
                        Check(((TextBox)Field(dialog, "reportOutputPath")).Text == "workflow-unsaved-path", "Reopened editor retains unsaved edits: " + mode);
                        bool connected = mode == "single" || mode == "multiple";
                        Check(((TextBox)Field(dialog, "selectedHubMac")).Text == (connected ? receipt.Devices.Last().Mac : ""), "Identity refill/cancel semantics: " + mode);
                        Check(!DeviceSetupStatus.ParametersSaved(dialog.Settings), "Return to editor does not approve parameters");
                        Check(((AppSettings)Field(form, "settings")).ReportOutputPath != "workflow-unsaved-path", "Unrelated draft has not been saved by connecting");
                        phase = 3; dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                    }
                    else if(phase==3&&!(bool)Field(form,"settingsWorkflowActive")) {timer.Stop();form.Close();}
                }
                catch (Exception error) { trace("error "+error);failure = error; phase = 3; if (dialog != null) {dialog.DialogResult=DialogResult.Cancel;dialog.Close();} }
            };
            trace("open settings");
            // Use the same top-level message loop as the application. A series of
            // DoEvents-only windows leaks modal-loop/async context between cases.
            form.Shown+=delegate {timer.Start();form.BeginInvoke(new MethodInvoker(()=>Call(form,"OpenSettings",SettingsSection.SystemAdmin)));};
            Application.Run(form);
            if (failure != null) throw failure;
            Check(phase == 3, "Settings workflow completed: " + mode);
            Check(connects == (mode == "single" || mode == "multiple" ? 1 : 0), "Only selected available device connects: " + mode);
            Check(selections == (mode == "multiple" || mode == "cancel" ? 1 : 0), "Only multiple devices need selection: " + mode);
            Check(notices == (mode == "failure" ? 1 : 0), "Failure visible without fake connection: " + mode);
            form.Close();
            trace("closed");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Console.WriteLine("STEP=selection");Selection(); Console.WriteLine("STEP=draft");Draft(); Console.WriteLine("STEP=ui");Ui(output);
            foreach (string mode in new[] { "single", "multiple", "cancel", "failure" }) {Console.WriteLine("STEP=workflow-"+mode);Workflow(output, mode);}
            Console.WriteLine("SETTINGS_AUTO_CONNECT_PASS=" + checks + "; HARDWARE_COMMANDS=0; LIVE_SETTINGS_WRITES=0; QA=" + output); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
