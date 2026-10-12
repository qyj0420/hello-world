using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using HVX.CalibrationStorage;

namespace Xiongxin.OWTS.Desktop
{
    // Managed adapter only. Native handles belong to the separately owned Task39
    // worker; a transport connection is never a P0.4/HV permission signal.
    internal sealed class Task39InputContract
    {
        internal string Xml, Project, Identity, ConfigurationPath, ConfigurationHash;
        internal string ArtSerial, PicoSerial;
        internal object Settings;
        internal Assembly Worker;

        internal static object ReadSettings(Assembly worker, string xml)
        {
            if (String.IsNullOrWhiteSpace(xml) || xml.Length > 65536) throw new InvalidDataException("Task39 settings must be bounded, explicit XML.");
            var document = new XmlDocument { XmlResolver = null };
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "BenchSettings") throw new InvalidDataException("Expected Task39 BenchSettings.");
            foreach (string field in new[] { "RealHardware", "AutomaticSharedTrigger", "TargetVoltageV", "ArtDevice", "ArtSerial", "PicoSerial", "ArtTrigger", "ArtTerminal", "FallingTrigger", "ExternalTriggerVolts", "RequestedPicoRateHz", "ResolutionBits", "CaptureDurationMs", "DividerVPerV", "InductanceH", "ResistanceOhms", "RangeIndex", "PdCalibrationPcPerV", "PicoChannelAEnabled", "PicoCoupling", "PicoAnalogOffsetVolts", "TriggerTimeoutMs" })
                if (document.DocumentElement.SelectNodes(field).Count != 1) throw new InvalidDataException("Missing or repeated explicit Task39 setting: " + field);
            Type type = worker.GetType("Xiongxin.ZeroBench.BenchSettings", true);
            object value;
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                value = new XmlSerializer(type).Deserialize(reader);
            type.GetMethod("Validate").Invoke(value, null); // Pure bounds/timebase validation, no SDK.
            double duration=Field<double>(value,"CaptureDurationMs"), rate=Field<double>(value,"RequestedPicoRateHz");
            HVX.Acquisition.PicoCaptureCadence.RequireDuration(duration);
            HVX.Acquisition.PicoCaptureCadence.RequireSamples(Convert.ToInt32(type.GetProperty("RequestedPicoCount").GetValue(value,null)),rate*(1-1e-9));
            if (!Field<bool>(value, "RealHardware") || Field<double>(value, "TargetVoltageV") != 0)
                throw new InvalidDataException("Only explicitly identified zero-voltage input capture is supported.");
            return value;
        }
        internal static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name).GetValue(value); }
        internal static string Serialize(object value)
        { using (var text = new StringWriter(CultureInfo.InvariantCulture)) { new XmlSerializer(value.GetType()).Serialize(text, value); return text.ToString(); } }

        internal static Task39InputContract Prepare(string workerPath, AppSettings settings, HardwareStatusSnapshot actual, string project)
        { return PrepareCore(workerPath,settings,actual,project,true); }

        // Page preparation only validates configuration and calibration. It is
        // not a device-ownership/electrical-safety check and cannot start a worker.
        internal static Task39InputContract PrepareForPage(string workerPath, AppSettings settings, HardwareStatusSnapshot actual, string project)
        { return PrepareCore(workerPath,settings,actual,project,false); }

        static Task39InputContract PrepareCore(string workerPath, AppSettings settings, HardwareStatusSnapshot actual, string project, bool fresh)
        {
            string identity = DeviceSetupStatus.ActualIdentity(actual);
            if (identity.Length == 0 || !DeviceSetupStatus.IdentityMatches(settings, actual)) throw new InvalidOperationException("请先核对保存当前实机身份和设备参数。 / Review the actual device identity and parameters first.");
            if (fresh) RequireFreshIdentity(settings, actual, identity);
            string normalized, message;
            if (!ProjectValidator.TryValidate(project, out normalized, out message)) throw new InvalidDataException(message);
            string path = settings.Hardware.Task39InputSettingsPath;
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请在设备设置中选择已核验的零电压采集设置文件。 / Select verified zero-voltage acquisition settings in device settings.");
            path = Path.GetFullPath(path); RequireNoLinks(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("Missing or oversized Task39 settings file.");
            RequireNoLinks(workerPath);
            Assembly worker = Assembly.LoadFrom(workerPath);
            string xml, configurationHash;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true)) { xml = reader.ReadToEnd(); configurationHash = LiveCalibrationRecord.HashFile(path); }
            object value = ReadSettings(worker, xml);
            string[] ids = identity.Split('|');
            if (Field<string>(value, "ArtSerial") != ids[2] || Field<string>(value, "PicoSerial") != ids[1]) throw new InvalidDataException("Task39 XML must match the actual ART/Pico serials, not a transport number or another device.");
            if (Field<double>(value, "RequestedPicoRateHz") != settings.PdSampleRateHz || Field<int>(value, "ResolutionBits") != settings.PdSampleBits ||
                Field<double>(value, "CaptureDurationMs") != settings.OscillatingPdCaptureDurationMs || Field<double>(value, "DividerVPerV") != settings.DividerRatio ||
                Field<double>(value, "InductanceH") != settings.InductanceHenry || Field<double>(value, "ResistanceOhms") != settings.InductorDcResistanceOhm)
                throw new InvalidDataException("Task39 sampling and circuit settings differ from the reviewed main-program settings; no silent override is allowed.");
            return new Task39InputContract { Worker = worker, Settings = value, Xml = Serialize(value), Project = normalized, Identity = identity,
                ArtSerial = ids[2], PicoSerial = ids[1], ConfigurationPath = path, ConfigurationHash = configurationHash };
        }

        internal static void RequireFreshIdentity(AppSettings settings, HardwareStatusSnapshot actual, string identity)
        {
            if(actual==null || !actual.Ready || DeviceSetupStatus.ActualIdentity(actual)!=identity ||
                !DeviceSetupStatus.IdentityMatches(settings,actual))
                throw new InvalidOperationException("当前设备身份无法确认，请检查连接。 / Device identity could not be confirmed.");
            if(actual.CheckedUtc < DateTime.UtcNow.AddSeconds(-30) || actual.CheckedUtc > DateTime.UtcNow.AddSeconds(1))
                throw new InvalidOperationException("设备连接检查已过期。 / Device inspection is stale.");
        }

        internal void ValidateRequest(string request)
        {
            object value = ReadSettings(Worker, request);
            foreach (FieldInfo field in Settings.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                // These values are explicitly supplied by the selected phase's
                // verified calibration and the current round's range decision.
                if (field.Name == "RangeIndex" || field.Name == "PdCalibrationPcPerV" || field.Name == "AutoRange" || field.Name == "RunNumber") continue;
                if (!Object.Equals(field.GetValue(Settings), field.GetValue(value))) throw new InvalidDataException("Task39 request changed the locked configuration: " + field.Name);
            }
            if (LiveCalibrationRecord.HashFile(ConfigurationPath) != ConfigurationHash) throw new InvalidDataException("Task39 configuration changed; reopen and review the session.");
        }

        internal static void RequireNoLinks(string path)
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked task configuration or output paths are not allowed.");
            var directory = Directory.Exists(full) ? new DirectoryInfo(full) : new DirectoryInfo(Path.GetDirectoryName(full));
            for (; directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Task39 path traverses a linked directory.");
        }

        internal async Task<string> CaptureAsync(string request, CancellationToken cancellation, Action validateCurrent)
        {
            cancellation.ThrowIfCancellationRequested(); validateCurrent(); ValidateRequest(request);
            string parent = Path.Combine(Project, "Acquisition", "Task39"); RequireNoLinks(parent);
            Directory.CreateDirectory(parent); RequireNoLinks(parent);
            string directory = Path.Combine(parent, "input-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); RequireNoLinks(directory);
            string configuration = Path.Combine(directory, "settings.xml");
            // Never replace another raw round, even if a caller reuses an identity.
            using (var stream = new FileStream(configuration, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(request.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\""));
            cancellation.ThrowIfCancellationRequested(); validateCurrent();
            Type runner = Worker.GetType("Xiongxin.ZeroBench.BenchRunner", true);
            MethodInfo run = runner.GetMethod("RunCommandAsync", BindingFlags.Static | BindingFlags.NonPublic);
            object value = ReadSettings(Worker, request);
            using (var log = new StreamWriter(new FileStream(Path.Combine(directory, "sequence.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
            {
                object gate = new object();
                Action<string, string> progress = (stage, detail) => { lock (gate) { log.WriteLine(DateTime.UtcNow.ToString("O") + " | " + stage + " | " + detail); log.Flush(); } };
                using(var cadence=await HVX.Acquisition.PicoCaptureCadence.Shared.EnterAsync(PicoSerial,cancellation,text=>progress("CADENCE",text)).ConfigureAwait(false))
                {
                cancellation.ThrowIfCancellationRequested();validateCurrent();ValidateRequest(request);
                // Runner's START handshake/job/lease is compiled in the worker EXE,
                // so ExecutingAssembly points at that EXE, never this UI assembly.
                cadence.BeginCapture();
                Task operation = (Task)run.Invoke(null, new object[] { "--hardware", configuration, directory, 45000 + Field<int>(value, "TriggerTimeoutMs"), progress, cancellation });
                await operation.ConfigureAwait(false);
                cadence.Released(Field<double>(value,"CaptureDurationMs"));
                }
            }
            cancellation.ThrowIfCancellationRequested(); validateCurrent(); ValidateRequest(request);
            return directory; // Consumer validates binary, identity, UTC and dual rates.
        }
    }

    internal sealed partial class ModuleHost
    {
        readonly List<ModuleAuditSubscription> pressureInputSubscriptions = new List<ModuleAuditSubscription>();
        readonly HashSet<Control> pressureInputConnected = new HashSet<Control>();
        long pressurePreparationSequence;

        internal void SetPressureInputFailClosed(Control control)
        { TrySet(control, "ActionHandshakeEnabled", true); }

        internal async Task ConfigureLegacyZeroPressureAsync(Control control)
        {
            if (disposed || control == null || control.IsDisposed || LateBound.ReadBoolean(control, true, "SimulationMode")) return;
            TrySet(control, "ActionHandshakeEnabled", true); // Pending requests fail closed during preparation.
            if (LateBound.ReadBoolean(control, false, "IsBusy") || LateBound.ReadBoolean(control, false, "HasActiveHardwareSession"))
                throw new InvalidOperationException("请等待当前采集完成。 / Wait for the current acquisition to finish.");
            long preparation = ++pressurePreparationSequence;
            TryInvoke(control, "ClearZeroVoltageHardwareBackend", new object[0]);
            TryInvoke(control, "ClearDischargeCalibration", new object[0]);
            string project = state.ProjectPath, phase = LateBound.ReadString(control, "SelectedPhaseCode");
            AppSettings app = (settings as AppSettings).Clone();
            HardwareStatusSnapshot actual = state.Hardware;
            string identity = DeviceSetupStatus.ActualIdentity(actual);
            if (identity.Length == 0) throw new InvalidOperationException("当前设备身份无法确认，请检查连接。 / Device identity could not be confirmed.");
            string picoSerial = identity.Split('|')[1];
            string worker = Path.Combine(modulesRoot, "Task39", "Xiongxin.Task39.InputWorker.exe");
            var prepared = await Task.Run(() => {
                var calibration = LiveCalibrationRecord.BestForProject(project, phase, app.TaskMinus3.UseFirstArrivalTiming, picoSerial);
                if (calibration == null) throw new InvalidDataException("缺少与当前项目、相别及 Pico 身份匹配的有效实机校准。 / No matching verified real calibration.");
                DateTimeOffset utc;
                if (!DateTimeOffset.TryParse(calibration.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out utc) || utc > DateTimeOffset.UtcNow || utc == default(DateTimeOffset)) throw new InvalidDataException("Invalid calibration acquisition UTC.");
                string path = calibration.SourcePath;
                return new { Calibration = calibration, Utc = utc, Source = path, Hash = LiveCalibrationRecord.HashFile(path) };
            });
            Action validateDisplay = () => {
                Action check = () => {
                    if (disposed || preparation != pressurePreparationSequence || control.IsDisposed || control.Disposing || state.ProjectPath != project || LateBound.ReadBoolean(control, true, "SimulationMode") ||
                        DeviceSetupStatus.ActualIdentity(state.Hardware) != identity || LateBound.ReadString(control, "SelectedPhaseCode") != phase ||
                        DeviceSetupStatus.ParameterHash(settings as AppSettings) != DeviceSetupStatus.ParameterHash(app) ||
                        LateBound.ReadString(settings, "Hardware.Task39InputSettingsPath") != app.Hardware.Task39InputSettingsPath)
                        throw new OperationCanceledException("The project, phase or connected device session changed.");
                };
                if (control.InvokeRequired) control.Invoke(check); else check();
            };
            validateDisplay();
            string source = prepared.Source, sourceHash = prepared.Hash;
            // Display a genuine saved calibration even when the acquisition backend
            // still needs configuration. This grants no hardware/output permission.
            TryInvoke(control, "SetDischargeCalibration", new object[] { prepared.Calibration.CoefficientPcPerV, source, prepared.Utc, phase, false });
            if (!LateBound.ReadBoolean(control, false, "IsDischargeCalibrated")) throw new InvalidOperationException("Pressure calibration handoff was refused.");
            var contract = await Task.Run(() => Task39InputContract.PrepareForPage(worker, app, actual, project));
            Action validate = () => {
                validateDisplay();
                if (!DeviceSetupStatus.IdentityMatches(settings as AppSettings, state.Hardware))
                    throw new OperationCanceledException("The reviewed device parameters changed.");
            };
            validate();
            Func<string, CancellationToken, Task<string>> capture = async (xml, token) => {
                // Formal source/raw hashes are rechecked before each range retry.
                token.ThrowIfCancellationRequested(); validate();
                var current = LiveCalibrationRecord.Load(project, source, true);
                if (LiveCalibrationRecord.HashFile(source) != sourceHash || current.CreatedUtc != prepared.Calibration.CreatedUtc || current.CoefficientPcPerV != prepared.Calibration.CoefficientPcPerV || current.DeviceSerial != contract.PicoSerial)
                    throw new InvalidDataException("Calibration changed during the capture session.");
                // Reject malformed or changed requests before any device inspection.
                contract.ValidateRequest(xml);
                // Perform the full transport/identity check only for an explicit
                // acquisition action. Never refresh CheckedUtc when opening a page.
                var fresh = await Task.Run(() => {
                    using(var adapter=new HardwareStatusAdapter(app,diagnostics))return adapter.Refresh(false);
                }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); validate();
                Task39InputContract.RequireFreshIdentity(app,fresh,contract.Identity);
                string directory = await contract.CaptureAsync(xml, token, validate).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); validate();
                LiveCalibrationRecord.Load(project, source, true);
                if (LiveCalibrationRecord.HashFile(source) != sourceHash) throw new InvalidDataException("Calibration changed while the worker was capturing; raw round is retained but not published.");
                return directory;
            };
            MethodInfo configure = control.GetType().GetMethod("ConfigureZeroVoltageHardwareBackend");
            if (configure == null) throw new MissingMethodException("Task39 input consumer is missing.");
            configure.Invoke(control, new object[] { contract.Xml, capture });
            diagnostics.Info("Task39", "Zero-voltage input consumer configured; nonzero HV remains disabled pending physical interlock integration.");
        }

        void ConnectPressureInputActions(Control control)
        {
            if (!pressureInputConnected.Add(control)) return;
            var capture = ModuleAuditSubscription.TryCreate(control, "PressureRequested", async () => {
                if (disposed || control.IsDisposed || LateBound.ReadBoolean(control, true, "SimulationMode")) return;
                long sequence = ReadInt64(control, "ActionSequence");
                try {
                    MethodInfo execute = control.GetType().GetMethod("ExecuteZeroVoltageHardwareActionAsync");
                    if (execute == null) throw new MissingMethodException("No Task39 input consumer.");
                    await (Task<bool>)execute.Invoke(control, new object[] { sequence });
                } catch (Exception error) {
                    TryInvoke(control, "CompleteAction", new object[] { sequence, false,
                        InputOperationFeedback.Describe(error, LateBound.ReadString(settings, "LanguageCode")) });
                    diagnostics.Error("Task39", "Input capture action failed.", error);
                }
            });
            if (capture != null) pressureInputSubscriptions.Add(capture);
            var parameters = ModuleAuditSubscription.TryCreate(control, "ActionPublished", async () => {
                if (disposed || control.IsDisposed || LateBound.ReadBoolean(control, true, "SimulationMode") || !LateBound.ReadBoolean(control, false, "ActionPending")) return;
                string code = LateBound.ReadString(control, "ActionCode");
                if (code == "PRESSURE") return; // Owned asynchronous consumer ACKs after release.
                var appliedLocally = new HashSet<string>(StringComparer.Ordinal) {
                    "POLARITY", "PROGRAM_MODE", "PLAN_STEP", "VOLTAGE_MODE", "VOLTAGE_VALUE", "VOLTAGE_DECREASE", "VOLTAGE_INCREASE",
                    "CONTROL_MODE", "RANGE_MODE", "RANGE", "AUTOMATIC_POST_PROCESS", "PHASE", "WAVEFORM_WINDOW", "SELECTION",
                    "SPECTRUM", "SPECTRUM_VIEW", "STATISTICS_FILTER", "SELECTION_SHAPE", "REGION_ACTION", "REGION_STATISTICS", "REGION_UNDO", "REGION_SELECTION"
                };
                bool accepted = appliedLocally.Contains(code);
                TryInvoke(control, "CompleteAction", new object[] { ReadInt64(control, "ActionSequence"), accepted,
                    accepted ? "" : InputOperationFeedback.Describe(new InvalidOperationException(), LateBound.ReadString(settings, "LanguageCode")) });
                if (code == "PHASE")
                {
                    TryInvoke(control, "ClearZeroVoltageHardwareBackend", new object[0]);
                    try { await ConfigureTask39PressureAsync(control); }
                    catch (Exception error) { diagnostics.Error("Task39", "New phase has no verified input/calibration contract.", error); }
                }
            });
            if (parameters == null) throw new MissingMethodException("Pressure ActionPublished handshake is missing.");
            pressureInputSubscriptions.Add(parameters);
        }
        void DisposePressureInputActions()
        {
            foreach (var pair in new List<KeyValuePair<Control,FactoryPressureSession>>(factoryPressureSessions)){
                RetirePressureSession(pair.Value);if(!pair.Key.IsDisposed)pair.Key.Dispose();
            }
            factoryPressureSessions.Clear();
            foreach(var pair in pressureAnalysisTrials){pair.Value.End();pair.Key.Disposed-=PressureAnalysisPageDisposed;}pressureAnalysisTrials.Clear();
            foreach (var item in pressureInputSubscriptions) item.Dispose();pressureInputSubscriptions.Clear();pressureInputConnected.Clear();
        }
    }
}
