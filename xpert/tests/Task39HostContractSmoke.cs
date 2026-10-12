using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using HVX.CalibrationStorage;
using Xiongxin.OWTS.Desktop;

// Driver-free host/worker contract checks. CaptureAsync is never directly invoked.
// Callback rejection probes carry deliberately invalid XML as a second fail-closed
// guard; the only child command this test permits is --worker-self-test.
internal static class Task39HostContractSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    const string Art = "3333333333", Pico = "TEST1/0001", Hub = "AABBCCDDEEFF";
    static int checks;
    static string qa, project, config, workerPath, modules;
    static Assembly worker;
    static string validXml;
    static void Check(bool result, string text) { checks++; if (!result) throw new Exception(text); }
    static Exception Root(Exception error)
    { while (error is TargetInvocationException || error is AggregateException) error = error.InnerException; return error; }
    static Exception Refused(Action action, string text)
    {
        try { action(); } catch (Exception error) { checks++; return Root(error); }
        throw new Exception("Expected refusal: " + text);
    }
    static void Wait(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(30)) { Application.DoEvents(); Thread.Sleep(1); }
        Check(task.IsCompleted, "managed operation completes within bounded QA watchdog"); task.GetAwaiter().GetResult();
    }
    static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Hidden).SetValue(owner, value); }
    static object Call(object owner, string name) { return owner.GetType().GetMethod(name, Hidden).Invoke(owner, null); }
    static void Value(object value, string field, object setting) { value.GetType().GetField(field).SetValue(value, setting); }
    static void WriteConfig(string xml) { File.WriteAllText(config, xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\""), new UTF8Encoding(false)); }
    static string Changed(string xml, string field, string value)
    {
        var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(xml);
        doc.DocumentElement[field].InnerText = value; return doc.OuterXml;
    }
    static HardwareStatusSnapshot Actual()
    { return new HardwareStatusSnapshot { HardwareEnabled = true, DriverInstalled = true, Connected = true, IdentityValid = true,
        StatusCode = "Ready", HubMac = Hub, ControllerSerial = Pico, AcquisitionSerial = Art, CheckedUtc = DateTime.UtcNow }; }
    static AppSettings Settings()
    {
        var value = new AppSettings { DataRoot = qa, PdSampleBits = 14, PdSampleRateHz = 125000000,
            OscillatingPdCaptureDurationMs = 60, DividerRatio = 11000, InductanceHenry = .7, InductorDcResistanceOhm = 22 };
        value.Hardware.Enabled = true; value.Hardware.SelectedHubMac = Hub;
        value.Hardware.ControllerLocation = "H01"; value.Hardware.AcquisitionLocation = "S01";
        value.Hardware.Task39InputSettingsPath = config; DeviceSetupStatus.ConfirmParameters(value, Actual()); return value;
    }
    static ApplicationState State()
    { var state = new ApplicationState(); string error; Check(state.TryLoadProject(project, out error), "QA project accepted: " + error); state.SetHardware(Actual()); return state; }
    static Task39InputContract Prepare(AppSettings settings = null, HardwareStatusSnapshot actual = null)
    { return Task39InputContract.Prepare(workerPath, settings ?? Settings(), actual ?? Actual(), project); }

    static void ExplicitXml()
    {
        object input = Activator.CreateInstance(worker.GetType("Xiongxin.ZeroBench.BenchSettings", true));
        Value(input, "RealHardware", true); Value(input, "ArtSerial", Art); Value(input, "PicoSerial", Pico);
        validXml = Task39InputContract.Serialize(input); WriteConfig(validXml);
        Check(Task39InputContract.Field<double>(Task39InputContract.ReadSettings(worker, validXml), "TargetVoltageV") == 0, "explicit zero input is accepted without drivers");
        string[] required = { "RealHardware", "AutomaticSharedTrigger", "TargetVoltageV", "ArtDevice", "ArtSerial", "PicoSerial", "ArtTrigger", "ArtTerminal", "FallingTrigger", "ExternalTriggerVolts", "RequestedPicoRateHz", "ResolutionBits", "CaptureDurationMs", "DividerVPerV", "InductanceH", "ResistanceOhms", "RangeIndex", "PdCalibrationPcPerV", "PicoChannelAEnabled", "PicoCoupling", "PicoAnalogOffsetVolts", "TriggerTimeoutMs" };
        foreach (string field in required)
        {
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(validXml);
            XmlNode node = doc.DocumentElement[field]; doc.DocumentElement.RemoveChild(node);
            Refused(() => Task39InputContract.ReadSettings(worker, doc.OuterXml), "missing explicit " + field);
            doc.LoadXml(validXml); doc.DocumentElement.AppendChild(doc.DocumentElement[field].CloneNode(true));
            Refused(() => Task39InputContract.ReadSettings(worker, doc.OuterXml), "duplicate explicit " + field);
        }
        foreach (string xml in new[] { "", new String('x', 65537), "<WrongRoot />", "<!DOCTYPE BenchSettings [<!ENTITY x 'unsafe'>]><BenchSettings>&x;</BenchSettings>" })
            Refused(() => Task39InputContract.ReadSettings(worker, xml), "empty/oversized/root/DTD rejected");
        foreach (var change in new[] { new[] { "TargetVoltageV", "1" }, new[] { "TargetVoltageV", "NaN" }, new[] { "RealHardware", "false" },
            new[] { "PicoChannelAEnabled", "false" }, new[] { "PicoAnalogOffsetVolts", "1" }, new[] { "ArtTrigger", "/Dev2/PFI0" } })
            Refused(() => Task39InputContract.ReadSettings(worker, Changed(validXml, change[0], change[1])), "invalid or nonzero XML " + change[0]);
    }

    static void IdentityAndRequests()
    {
        var contract = Prepare();
        Check(contract.ArtSerial == Art && contract.PicoSerial == Pico && contract.Identity == Hub + "|" + Pico + "|" + Art, "actual identity preserved and separated from transport IDs");
        var swapped = Actual(); swapped.ControllerSerial = Art; swapped.AcquisitionSerial = Pico;
        Check(Prepare(actual: swapped).Identity == contract.Identity, "board identity classification accepts swapped transport fields");
        foreach (Action<HardwareStatusSnapshot> mutate in new Action<HardwareStatusSnapshot>[] {
            h => h.ControllerSerial = "DS600-123456", h => h.AcquisitionSerial = "DS600-123456", h => h.ControllerSerial = "TEST2/0002",
            h => h.HubMac = "001122334455", h => h.Connected = false, h => h.IdentityValid = false,
            h => h.StatusCode = "NeedsConfiguration", h => h.CheckedUtc = DateTime.UtcNow.AddMinutes(-2), h => h.CheckedUtc = DateTime.UtcNow.AddMinutes(2) })
        { var actual = Actual(); mutate(actual); Refused(() => Prepare(actual: actual), "invalid/stale/mismatched actual identity"); }
        foreach (string field in new[] { "ArtSerial", "PicoSerial" })
        {
            WriteConfig(Changed(validXml, field, "DS600-123456")); Refused(() => Prepare(), "DS600 transport number is not " + field);
            WriteConfig(Changed(validXml, field, field == "ArtSerial" ? "4444444444" : "TEST2/0002")); Refused(() => Prepare(), "other-board XML identity");
        }
        WriteConfig(validXml);
        var oldSnapshot=Actual();oldSnapshot.CheckedUtc=DateTime.UtcNow.AddHours(-1);
        var pageContract=Task39InputContract.PrepareForPage(workerPath,Settings(),oldSnapshot,project);
        Check(pageContract.Identity==DeviceSetupStatus.ActualIdentity(oldSnapshot),"page preparation reuses identity without refreshing hardware");
        Refused(()=>Task39InputContract.RequireFreshIdentity(Settings(),oldSnapshot,pageContract.Identity),"explicit action cannot reuse old inspection");
        Task39InputContract.RequireFreshIdentity(Settings(),Actual(),pageContract.Identity);
        Check(true,"action still accepts only current matching identity");
        foreach (Action<AppSettings> mutate in new Action<AppSettings>[] { s => s.DividerRatio += 1, s => s.PdSampleRateHz = 62500000,
            s => s.PdSampleBits = 12, s => s.OscillatingPdCaptureDurationMs = 40, s => s.InductanceHenry += .1, s => s.InductorDcResistanceOhm += 1 })
        {
            var app = Settings(); mutate(app); Refused(() => Prepare(app), "changed reviewed main settings");
            DeviceSetupStatus.ConfirmParameters(app, Actual()); Refused(() => Prepare(app), "reviewed main settings still must match explicit XML");
        }
        var missing = Settings(); missing.Hardware.Task39InputSettingsPath = ""; Refused(() => Prepare(missing), "configuration is explicit, not guessed");
        missing = Settings(); missing.DeviceParametersReviewedIdentity = ""; Refused(() => Prepare(missing), "review confirmation required");
        Refused(() => Task39InputContract.Prepare(workerPath, Settings(), Actual(), Path.Combine(qa, "missing-project")), "invalid project rejected");
        contract.ValidateRequest(validXml); Check(true, "unchanged request validates");
        foreach (var item in new[] { new[] { "RangeIndex", "9" }, new[] { "PdCalibrationPcPerV", "3200" }, new[] { "RunNumber", "2" }, new[] { "AutoRange", "false" } })
        { contract.ValidateRequest(Changed(validXml, item[0], item[1])); Check(true, "consumer-controlled range/calibration field " + item[0]); }
        foreach (var item in new[] { new[] { "ArtSerial", "4444444444" }, new[] { "PicoSerial", "TEST2/0002" }, new[] { "CaptureDurationMs", "40" },
            new[] { "ArtDevice", "Dev2" }, new[] { "AutomaticSharedTrigger", "true" }, new[] { "TriggerTimeoutMs", "6000" }, new[] { "DividerVPerV", "12000" } })
            Refused(() => contract.ValidateRequest(Changed(validXml, item[0], item[1])), "locked request field " + item[0]);
        WriteConfig(validXml + "\r\n"); Refused(() => contract.ValidateRequest(validXml), "edited original XML invalidates even identical request"); WriteConfig(validXml);
        var clone = Settings().Clone(); Check(clone.Hardware.Task39InputSettingsPath == config, "XML path survives settings Clone");
        string settingsPath = Path.Combine(qa, "isolated-settings.xml"); clone.Save(settingsPath);
        Check(AppSettings.Load(settingsPath).Hardware.Task39InputSettingsPath == config, "XML path survives isolated Save/Load");
    }

    static LiveCalibrationRecord Calibration(string phase, string serial, string level)
    {
        var raw = new List<CalibrationRawReference>();
        for (int i = 0; i < 3; i++)
        {
            string relative = "qa-raw-" + i + ".bin", path = Path.Combine(project, relative);
            if (!File.Exists(path)) File.WriteAllBytes(path, new byte[] { 1, 2, (byte)i, 4 });
            raw.Add(new CalibrationRawReference { RelativePath = relative, Sha256 = LiveCalibrationRecord.HashFile(path) });
        }
        var average = new double[256]; average[20] = 1; average[22] = .5;
        return new LiveCalibrationRecord { Phase = phase, Level = level, DeviceSerial = serial, DeviceModel = "QA synthetic",
            FilterId = HVX.SignalProcessing.PdLinearHighPass.FilterId, ProjectHash = LiveCalibrationRecord.Identity(project),
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O"), AcquisitionMode = "QA synthetic pulse",
            LengthM = 170, VelocityMPerUs = 170, SampleRateHz = 1000000, IncidentSeconds = .000020, ReflectionSeconds = .000022,
            IncidentPeakIndex = 20, ReflectionPeakIndex = 22, CoefficientPcPerV = 2345, ChargePc = 100, SnrDb = 30,
            Correlation = .99, PulseCount = 3, RangeIndex = 9, Average = average, RawFiles = raw.ToArray() };
    }
    public sealed class PressurePage : UserControl
    {
        public bool SimulationMode { get; set; }
        public bool ActionHandshakeEnabled { get; set; }
        public string SelectedPhaseCode { get; set; } = "A";
        public bool IsDischargeCalibrated { get; set; }
        public bool IsBusy { get; set; }
        public bool HasActiveHardwareSession { get; set; }
        public bool ActionPending { get; set; }
        public long ActionSequence { get; set; }
        public string ActionCode { get; set; }
        public bool LastActionSucceeded;
        public int Stops, CompleteCalls, ConfigureCalls, ClearCalls;
        public readonly List<string> Operations = new List<string>();
        public event EventHandler ActionPublished;
        public event EventHandler PressureRequested;
        public event EventHandler PhaseChanged;
        public double Coefficient;
        public string Source, Xml;
        public Func<string, CancellationToken, Task<string>> CaptureRequest;
        public void SetDischargeCalibration(double coefficient, string source, DateTimeOffset utc, string phase, bool simulation)
        { Coefficient = coefficient; Source = source; IsDischargeCalibrated = !simulation && phase == SelectedPhaseCode && utc < DateTimeOffset.UtcNow; }
        public void ClearDischargeCalibration() { IsDischargeCalibrated = false; }
        public void ConfigureZeroVoltageHardwareBackend(string xml, Func<string, CancellationToken, Task<string>> capture)
        { Xml = xml; CaptureRequest = capture; ConfigureCalls++; Operations.Add("CONFIGURE:" + SelectedPhaseCode); }
        public void ClearZeroVoltageHardwareBackend() { CaptureRequest = null; ClearCalls++; Operations.Add("CLEAR:" + SelectedPhaseCode); }
        public bool CompleteAction(long sequence, bool succeeded, string error)
        {
            if (!ActionPending || sequence != ActionSequence) return false;
            CompleteCalls++; ActionPending = IsBusy = false; LastActionSucceeded = succeeded; Operations.Add("ACK:" + ActionCode); return true;
        }
        public void SetPhaseForQa(string phase)
        { SelectedPhaseCode = phase; IsDischargeCalibrated = false; var changed = PhaseChanged; if (changed != null) changed(this, EventArgs.Empty); }
        public void PublishForQa(string code)
        { ActionSequence++; ActionCode = code; ActionPending = IsBusy = true; NotifyPublishedForQa(); }
        public void NotifyPublishedForQa() { var action = ActionPublished; if (action != null) action(this, EventArgs.Empty); }
        public void NotifyPressureForQa() { var action = PressureRequested; if (action != null) action(this, EventArgs.Empty); }
        public void RequestHardwareStop() { Stops++; }
    }
    public sealed class ThrowingPage : UserControl { public bool IsBusy { get { throw new IOException("QA unknown state"); } } }
    static Action ValidationAction(PressurePage page)
    {
        // Invoke only the pure current-session guard, never the capture delegate.
        var fields = page.CaptureRequest.Target.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo field = fields.FirstOrDefault(f => f.FieldType == typeof(Action) && f.Name == "validate");
        Check(field != null, "host capture exposes its captured pure session validation action"); return (Action)field.GetValue(page.CaptureRequest.Target);
    }
    static void HostHandoff()
    {
        var app = Settings(); var state = State();
        using (var host = new ModuleHost(app, state, new Diagnostics(Path.Combine(qa, "diagnostics")), modules))
        using (var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
        using (var page = new PressurePage())
        {
            form.Controls.Add(page); form.Show(); Application.DoEvents();
            Refused(() => Wait(host.ConfigureTask39PressureAsync(page)), "no formal live calibration refuses preparation");
            Check(page.ActionHandshakeEnabled && page.CaptureRequest == null, "handshake is fail closed while unconfigured");
            Calibration("B", "TEST2/0002", "100pC").Save(project);
            Refused(() => Wait(host.ConfigureTask39PressureAsync(page)), "shared phase still requires actual Pico identity");
            string sharedSource=Calibration("B", Pico, "100pC").Save(project);
            Wait(host.ConfigureTask39PressureAsync(page));
            Check(page.IsDischargeCalibrated&&page.Source==sharedSource,"equal-length A consumes B without relabelling source");
            Calibration("A", "TEST2/0002", "200pC").Save(project);
            Wait(host.ConfigureTask39PressureAsync(page));
            Check(page.Source==sharedSource,"mismatched own-phase device does not shadow valid shared source");
            string commonSource=Calibration("ALL",Pico,"100pC").Save(project);
            Wait(host.ConfigureTask39PressureAsync(page));
            Check(page.IsDischargeCalibrated&&page.Source==commonSource,"ALL preferred over other phase in actual consumer");
            var record = Calibration("A", Pico, "300pC"); string source = record.Save(project);
            string sourceHash = LiveCalibrationRecord.HashFile(source);
            string originalConfig = app.Hardware.Task39InputSettingsPath;
            app.Hardware.Task39InputSettingsPath = "";
            Refused(() => Wait(host.ConfigureTask39PressureAsync(page)), "missing backend configuration stays unavailable");
            Check(page.IsDischargeCalibrated && page.Coefficient == 2345 && page.Source == source && page.CaptureRequest == null,
                "saved calibration and ranges remain available independently of missing backend XML");
            app.Hardware.Task39InputSettingsPath = originalConfig;
            Wait(host.ConfigureTask39PressureAsync(page));
            Check(page.CaptureRequest != null && page.IsDischargeCalibrated && page.Coefficient == 2345 && page.Source == source, "exact phase/device formal calibration handed to consumer");
            Check(Task39InputContract.Field<double>(Task39InputContract.ReadSettings(worker, page.Xml), "TargetVoltageV") == 0, "backend handoff stays explicit zero input");
            var validate = ValidationAction(page); validate(); Check(true, "unchanged prepared session valid");
            double divider = app.DividerRatio; app.DividerRatio += 1; Refused(validate, "changed current reviewed parameters invalidate prepared session"); app.DividerRatio = divider;
            string originalPath = app.Hardware.Task39InputSettingsPath; app.Hardware.Task39InputSettingsPath += ".changed";
            Refused(validate, "configuration path change invalidates prepared session"); app.Hardware.Task39InputSettingsPath = originalPath;
            string reviewed = app.DeviceParametersReviewedIdentity; app.DeviceParametersReviewedIdentity = ""; Refused(validate, "review binding removed"); app.DeviceParametersReviewedIdentity = reviewed;
            page.SelectedPhaseCode = "B"; Refused(validate, "phase changed"); page.SelectedPhaseCode = "A";
            page.SimulationMode = true; Refused(validate, "live page switched to simulation"); page.SimulationMode = false;
            state.ClearProject(); Refused(validate, "project release invalidates current session"); string error; state.TryLoadProject(project, out error);
            var another = Actual(); another.ControllerSerial = "TEST2/0002"; state.SetHardware(another); Refused(validate, "device change invalidates current session"); state.SetHardware(Actual());
            validate(); Check(LiveCalibrationRecord.HashFile(source) == sourceHash, "configuration/guard checks never modify formal source");
            using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); Refused(() => Wait(page.CaptureRequest("DELIBERATELY_INVALID_XML", cancelled.Token)), "already cancelled callback never reaches worker"); }
            record.CoefficientPcPerV = 4567; record.Save(project);
            Exception changed = Refused(() => Wait(page.CaptureRequest("DELIBERATELY_INVALID_XML", CancellationToken.None)), "formal record changed");
            Check(changed.Message.Contains("Calibration changed"), "formal source identity is checked before even parsing rejected capture XML");
            record.CoefficientPcPerV = 2345; record.Save(project); Wait(host.ConfigureTask39PressureAsync(page));
            string rawPath = Path.Combine(project, record.RawFiles[0].RelativePath); byte[] raw = File.ReadAllBytes(rawPath); File.AppendAllText(rawPath, "QA changed");
            Exception rawChanged = Refused(() => Wait(page.CaptureRequest("DELIBERATELY_INVALID_XML", CancellationToken.None)), "raw reference changed");
            Check(rawChanged.Message.Contains(record.RawFiles[0].RelativePath), "formal source validates raw evidence before worker invocation"); File.WriteAllBytes(rawPath, raw);
            page.SelectedPhaseCode = "A"; Task preparing = host.ConfigureTask39PressureAsync(page); page.SelectedPhaseCode = "B";
            Refused(() => Wait(preparing), "asynchronous preparation must not install into a changed phase");
            Check(!Directory.Exists(Path.Combine(project, "Acquisition")), "all host tests remained before raw-directory creation and native capture");
            form.Close();
        }
    }

    static void Navigation()
    {
        var app = Settings(); app.Hardware.Enabled = false;
        using (var form = new MainForm(app, State(), true))
        using (var page = new PressurePage { Tag = ModuleHost.PressureTest })
        {
            form.DeviceConnectionNoticeForTests = ignored => { }; Set(form, "activeModule", page); Set(form, "activeTestSimulationMode", false);
            foreach (string flag in new[] { "IsBusy", "HasActiveHardwareSession", "ActionPending" })
            {
                typeof(PressurePage).GetProperty(flag).SetValue(page, true, null); int prior = page.Stops;
                Check(!(bool)Call(form, "CanLeaveActivePressure") && page.Stops == prior && page.Stops == 0,
                    flag + " still blocks navigation without implicitly requesting hardware stop");
                typeof(PressurePage).GetProperty(flag).SetValue(page, false, null);
            }
            Check((bool)Call(form, "CanLeaveActivePressure"), "idle released input page permits navigation");
            page.IsBusy = true; Set(form, "activeTestSimulationMode", true); int stops = page.Stops;
            Check(!(bool)Call(form, "CanLeaveActivePressure") && page.Stops == stops, "simulation processing blocks navigation without a hardware stop");
            page.IsBusy = false; Set(form, "activeModule", null);
            using (var throwing = new ThrowingPage { Tag = ModuleHost.PressureTest })
            { Set(form, "activeModule", throwing); Check(!(bool)Call(form, "CanLeaveActivePressure"), "unknown busy state fails closed"); Set(form, "activeModule", null); }
            Set(form,"activeModule",page);form.PressureExitDelayForTests=ms=>Task.CompletedTask;
            Task<bool> exit=form.ShowHomeAsync();Wait(exit);
            Check(exit.Result&&page.IsDisposed&&page.Stops==1,"explicit Home now requests page stop and awaits release; full active/failed-owner cases are covered by PressurePageExitManagedTests");
        }
    }
    static void HostActionHandshake()
    {
        var app = Settings(); var state = State();
        using (var host = new ModuleHost(app, state, new Diagnostics(Path.Combine(qa, "actions-diagnostics")), modules))
        using (var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
        using (var page = new PressurePage())
        {
            form.Controls.Add(page); form.Show(); Application.DoEvents();
            MethodInfo connect = typeof(ModuleHost).GetMethod("ConnectPressureInputActions", Hidden);
            connect.Invoke(host, new object[] { page }); connect.Invoke(host, new object[] { page });
            host.SetPressureInputFailClosed(page); Check(page.ActionHandshakeEnabled, "live host installs mailbox handshake before actions");
            Wait(host.ConfigureTask39PressureAsync(page));
            foreach (string code in new[] { "POLARITY", "PROGRAM_MODE", "PLAN_STEP", "VOLTAGE_MODE", "VOLTAGE_VALUE", "VOLTAGE_DECREASE", "VOLTAGE_INCREASE",
                "CONTROL_MODE", "RANGE_MODE", "RANGE", "AUTOMATIC_POST_PROCESS", "WAVEFORM_WINDOW", "SELECTION", "SPECTRUM", "SPECTRUM_VIEW",
                "STATISTICS_FILTER", "SELECTION_SHAPE", "REGION_ACTION", "REGION_STATISTICS", "REGION_UNDO", "REGION_SELECTION" })
            {
                int completed = page.CompleteCalls; page.PublishForQa(code);
                Check(!page.ActionPending && !page.IsBusy && page.LastActionSucceeded, "published local parameter ACK leaves no busy latch: " + code);
                Check(page.CompleteCalls == completed + 1, "duplicate host connection does not duplicate ACK: " + code);
            }
            foreach (string code in new[] { "DELETE", "UNKNOWN_HARDWARE_OUTPUT" })
            { page.PublishForQa(code); Check(!page.ActionPending && !page.IsBusy && !page.LastActionSucceeded, "unhandled command rejects without lock or raw mutation: " + code); }
            int acks = page.CompleteCalls; page.PublishForQa("PRESSURE");
            Check(page.ActionPending && page.IsBusy && page.CompleteCalls == acks, "generic ActionPublished never prematurely ACKs owned PRESSURE");
            page.CompleteAction(page.ActionSequence, false, "QA releases unexecuted mailbox");
            acks = page.CompleteCalls; page.NotifyPublishedForQa(); Check(page.CompleteCalls == acks, "already completed action not re-ACKed");
            page.SimulationMode = true; page.PublishForQa("RANGE");
            Check(page.ActionPending && page.CompleteCalls == acks, "live host does not consume simulated actions");
            page.CompleteAction(page.ActionSequence, false, "QA"); page.SimulationMode = false;
            int previousConfig = page.ConfigureCalls, previousClear = page.ClearCalls; acks = page.CompleteCalls; page.Operations.Clear();
            page.SetPhaseForQa("B");
            Check(page.CompleteCalls == acks && page.ClearCalls == previousClear, "setter PhaseChanged before publication does not read an unpublished mailbox");
            page.PublishForQa("PHASE");
            Check(!page.IsBusy && !page.ActionPending && page.LastActionSucceeded, "PHASE ACK releases parameter busy before asynchronous configuration");
            Check(page.Operations.Count >= 2 && page.Operations[0] == "ACK:PHASE" && page.Operations[1] == "CLEAR:B", "phase order is setter then publication/ACK then old backend clear");
            var timer = Stopwatch.StartNew();
            while (page.ConfigureCalls == previousConfig && timer.ElapsedMilliseconds < 15000) { Application.DoEvents(); Thread.Sleep(1); }
            Check(page.ConfigureCalls == previousConfig + 1 && page.IsDischargeCalibrated && page.Source.EndsWith("B_100pC.hvcal", StringComparison.Ordinal), "phase switch configures exact B record once without stale A callback");
            Check(page.CaptureRequest != null && !Directory.Exists(Path.Combine(project, "Acquisition")), "phase handoff prepares only, never launches capture");
            host.Dispose(); acks = page.CompleteCalls; page.PublishForQa("RANGE");
            Check(page.ActionPending && page.CompleteCalls == acks, "disposing host removes action subscriptions");
            page.CompleteAction(page.ActionSequence, false, "QA"); form.Close();
        }
    }
    static void ManagedWorkerProbe()
    {
        MethodInfo run = worker.GetType("Xiongxin.ZeroBench.BenchRunner", true).GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Static);
        string path = Path.Combine(qa, "driver-free-worker"); Directory.CreateDirectory(path);
        var messages = new List<string>(); Action<string, string> progress = (stage, text) => { lock (messages) messages.Add(stage + "|" + text); };
        Wait((Task)run.Invoke(null, new object[] { "--worker-self-test", "success", path, 5000, progress, CancellationToken.None }));
        Check(File.Exists(Path.Combine(path, "worker-ok.txt")), "original standalone worker executes safe owned-child self-test");
        int pid = Int32.Parse(File.ReadAllText(Path.Combine(path, "worker.pid")));
        Check(pid != Process.GetCurrentProcess().Id, "worker remains an independent executable, not merged GUI main");
        lock (messages) Check(messages.Any(x => x.Contains("未加载驱动")), "safe child explicitly reports no driver load");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            modules = Path.Combine(Path.GetFullPath(args[0]), "modules"); workerPath = Path.Combine(modules, "Task39", "Xiongxin.Task39.InputWorker.exe");
            Check(File.Exists(workerPath), "packaged standalone Task39 worker exists"); worker = Assembly.LoadFrom(workerPath);
            qa = Path.Combine(Path.GetFullPath(args[1]), "contract-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(qa);
            project = Path.Combine(qa, "synthetic-project"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "ITEM.in"), "[ITEM]\r\nName=Task39 synthetic offline QA\r\n");
            File.WriteAllText(Path.Combine(project, "CABLE.in"),String.Concat(new[]{"A","B","C"}.Select(p=>
                "0\t"+p+"\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n")));
            string initialIdentity = LiveCalibrationRecord.Identity(project); config = Path.Combine(qa, "explicit-task39.xml");
            ExplicitXml(); IdentityAndRequests(); HostHandoff(); HostActionHandshake(); Navigation(); ManagedWorkerProbe();
            Check(LiveCalibrationRecord.Identity(project) == initialIdentity, "ITEM/CABLE unchanged throughout host tests");
            Check(!Directory.Exists(Path.Combine(project, "Acquisition")), "zero hardware capture/output invocations");
            Console.WriteLine("TASK39_HOST_CONTRACT_PASS=" + checks + "; HARDWARE_CAPTURE=0; DISCOVERY=0; SAFE_WORKER_SELF_TEST=1; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(Root(error)); return 1; }
    }
}
