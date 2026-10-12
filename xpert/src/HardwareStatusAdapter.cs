using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    /// <summary>
    /// Narrow bridge to the independently installed DS600 integration utility.
    /// The ordinary refresh path only inspects an existing vendor binding; it never binds/connects modules.
    /// The legacy release API is not used by the desktop disconnect action. This class can never install a driver,
    /// restart a service, rename/reorder a device, start acquisition, or energize output.
    /// </summary>
    internal sealed class HardwareStatusAdapter : IDisposable
    {
        private const int MaximumStatusBytes = 1024 * 1024;
        private readonly object settings;
        private readonly Diagnostics diagnostics;
        private readonly object refreshGate = new object();
        private readonly object processGate = new object();
        private readonly CancellationTokenSource disposeCancellation = new CancellationTokenSource();
        private HardwareStatusSnapshot lastSnapshot = new HardwareStatusSnapshot();
        private Process activeProcess;
        private int backgroundRefresh;
        private int disposed;
        // Only the explicit vendor-management import may inspect network binding.
        // Routine checks always use the saved physical ART/Pico identities locally.
        internal bool BindingImportInspection { get; set; }

        public HardwareStatusAdapter(object appSettings)
            : this(appSettings, null)
        {
        }

        public HardwareStatusAdapter(object appSettings, Diagnostics integrationDiagnostics)
        {
            settings = appSettings;
            diagnostics = integrationDiagnostics ?? new Diagnostics();
        }

        public HardwareStatusSnapshot LastSnapshot
        {
            get { lock (refreshGate) return lastSnapshot.Clone(); }
        }

        public HardwareStatusSnapshot Refresh()
        {
            return Refresh(false);
        }

        public HardwareStatusSnapshot Refresh(bool requestConnection)
        {
            if (IsDisposed) throw new ObjectDisposedException(GetType().FullName);
            return Refresh(requestConnection, disposeCancellation.Token);
        }

        private HardwareStatusSnapshot Refresh(bool requestConnection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (refreshGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HardwareStatusSnapshot result;
                try { result = RefreshCore(requestConnection, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    diagnostics.Error("HardwareStatus", "Unhandled status refresh failure.", ex);
                    result = Failure(true, "Error", "硬件状态检查失败。", ex.Message);
                }
                lastSnapshot = result.Clone();
                return result;
            }
        }

        public bool BeginRefresh(Action<HardwareStatusSnapshot> completed)
        {
            return BeginRefresh(false, completed);
        }

        // Explicit user action only. Shares the request gate with discovery so a
        // pending connect cannot race a release. The caller owns acquisition and
        // page guards; this transport operation cannot declare electrical safety.
        public Task<HardwareStatusSnapshot> DisconnectAsync()
        { return DisconnectAsync(null); }

        public async Task<HardwareStatusSnapshot> DisconnectAsync(HardwareStatusSnapshot currentSession)
        {
            HardwareStatusSnapshot session = currentSession == null ? null : currentSession.Clone();
            if (IsDisposed) throw new ObjectDisposedException(GetType().FullName);
            if (Interlocked.CompareExchange(ref backgroundRefresh, 1, 0) != 0)
                throw new InvalidOperationException("A hardware status request is already in progress.");
            try
            {
                CancellationToken token = disposeCancellation.Token;
                token.ThrowIfCancellationRequested();
                return await Task.Run(delegate
                {
                    lock (refreshGate)
                    {
                        token.ThrowIfCancellationRequested();
                        HardwareStatusSnapshot result;
                        try { result = DisconnectCore(token, session); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            diagnostics.Error("HardwareStatus", "Explicit disconnect failed.", error);
                            result = Failure(true, "DisconnectFailed", "无法确认指定设备已断开。", error.Message);
                        }
                        token.ThrowIfCancellationRequested();
                        lastSnapshot = result.Clone();
                        return result;
                    }
                }, token).ConfigureAwait(false);
            }
            finally { Interlocked.Exchange(ref backgroundRefresh, 0); }
        }

        private HardwareStatusSnapshot DisconnectCore(CancellationToken token, HardwareStatusSnapshot session)
        {
            bool enabled = LateBound.ReadBoolean(settings, false, "Hardware.Enabled", "HardwareEnabled");
            string mac = NormalizeMac(LateBound.ReadString(settings, "Hardware.SelectedHubMac", "SelectedHubMac"));
            string controller = NormalizeLocation(LateBound.ReadString(settings, "Hardware.ControllerLocation", "ControllerLocation"));
            string acquisition = NormalizeLocation(LateBound.ReadString(settings, "Hardware.AcquisitionLocation", "AcquisitionLocation"));
            string excluded = NormalizeIdentity(LateBound.ReadString(settings, "Hardware.ExcludedAcquisitionSerial", "ExcludedAcquisitionSerial"));
            if (!enabled || !Regex.IsMatch(mac, "^[0-9A-F]{12}$", RegexOptions.CultureInvariant) ||
                !ValidLocation(controller) || !ValidLocation(acquisition) || controller == acquisition)
                return Failure(enabled, "DisconnectFailed", "硬件模式或指定设备身份配置无效，未执行断开。",
                    "Disconnect requires enabled hardware and an explicit MAC with distinct module locations.");
            string executable = ResolveIntegrationExecutable();
            if (String.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
                !String.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
                return Failure(true, "DisconnectFailed", "缺少支持断开操作的硬件联调程序。", executable);
            bool driverInstalled = CheckDriverMarker(ResolveConfiguredPath("Hardware.DriverMarkerPath", "DriverMarkerPath", "Hardware.DriverInstalledPath"), executable);
            return RunIntegration(executable, false, mac, controller, acquisition, excluded, driverInstalled, token, true,
                VerifiedDisconnectAddressHint(session, mac, controller, acquisition, executable));
        }

        internal static string VerifiedDisconnectAddressHint(HardwareStatusSnapshot session, string mac,
            string controller, string acquisition, string executable)
        {
            // Cached files and configuration do not authorize an address shortcut.
            // The helper still checks the current MAC, VID/PID, location and port ownership.
            IPAddress address;
            if (session == null || !session.Ready || !session.HasLiveIdentityReadback ||
                NormalizeMac(session.HubMac) != mac || NormalizeMac(session.RequestedHubMac) != mac ||
                NormalizeLocation(session.RequestedControllerLocation) != controller ||
                NormalizeLocation(session.RequestedAcquisitionLocation) != acquisition ||
                !String.Equals(session.IntegrationExecutablePathUsed, executable, StringComparison.OrdinalIgnoreCase) ||
                !IPAddress.TryParse(session.HubIp, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast)) return String.Empty;
            return address.ToString();
        }

        public bool BeginRefresh(bool requestConnection, Action<HardwareStatusSnapshot> completed)
        {
            if (IsDisposed) return false;
            if (Interlocked.CompareExchange(ref backgroundRefresh, 1, 0) != 0) return false;
            if (IsDisposed)
            {
                Interlocked.Exchange(ref backgroundRefresh, 0);
                return false;
            }

            CancellationToken cancellationToken = disposeCancellation.Token;
            SynchronizationContext callbackContext = SynchronizationContext.Current;
            Task.Run(delegate { return Refresh(requestConnection, cancellationToken); }, cancellationToken).ContinueWith(task =>
            {
                Action release = delegate { Interlocked.Exchange(ref backgroundRefresh, 0); };
                if (task.IsCanceled || cancellationToken.IsCancellationRequested || IsDisposed)
                {
                    release();
                    return;
                }

                HardwareStatusSnapshot snapshot;
                snapshot = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : Failure(true, "Error", "硬件状态检查失败。", task.Exception == null ? String.Empty : task.Exception.GetBaseException().Message);
                if (completed == null)
                {
                    release();
                    return;
                }

                SendCallback(callbackContext, delegate(HardwareStatusSnapshot value)
                {
                    if (!cancellationToken.IsCancellationRequested && !IsDisposed) completed(value);
                }, snapshot, release);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }

        private void SendCallback(SynchronizationContext context, Action<HardwareStatusSnapshot> callback,
            HardwareStatusSnapshot snapshot, Action callbackCompleted)
        {
            SendOrPostCallback invoke = delegate
            {
                try { callback(snapshot.Clone()); }
                catch (Exception ex) { diagnostics.Error("HardwareStatus", "Refresh callback failed.", ex); }
                finally { callbackCompleted(); }
            };
            if (context != null)
            {
                try { context.Post(invoke, null); return; }
                catch (Exception ex) { diagnostics.Error("HardwareStatus", "Could not post refresh result to the captured context.", ex); }
            }
            invoke(null);
        }

        private bool IsDisposed
        {
            get { return Volatile.Read(ref disposed) != 0; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { disposeCancellation.Cancel(); } catch { }

            Process process;
            lock (processGate)
            {
                process = activeProcess;
                activeProcess = null;
            }
            StopOwnedProcess(process);
            Interlocked.Exchange(ref backgroundRefresh, 0);
        }

        private HardwareStatusSnapshot RefreshCore(bool requestConnection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool enabled = LateBound.ReadBoolean(settings, false, "Hardware.Enabled", "HardwareEnabled");
            if (!enabled)
            {
                // Offline means no executable launch, no driver/path probe and no network enumeration.
                return new HardwareStatusSnapshot
                {
                    HardwareEnabled = false,
                    DriverInstalled = false,
                    Connected = false,
                    IdentityValid = false,
                    StatusCode = "Offline",
                    Message = "硬件模式未启用；未执行设备发现或连接。",
                    Diagnostic = "Hardware.Enabled=false",
                    CheckedUtc = DateTime.UtcNow
                };
            }

            string selectedMac = NormalizeMac(LateBound.ReadString(settings, "Hardware.SelectedHubMac", "SelectedHubMac"));
            string expectedControllerLocation = NormalizeLocation(LateBound.ReadString(settings, "Hardware.ControllerLocation", "ControllerLocation"));
            string expectedAcquisitionLocation = NormalizeLocation(LateBound.ReadString(settings, "Hardware.AcquisitionLocation", "AcquisitionLocation"));
            string excludedAcquisition = NormalizeIdentity(LateBound.ReadString(settings, "Hardware.ExcludedAcquisitionSerial", "ExcludedAcquisitionSerial"));
            if (!Regex.IsMatch(selectedMac, "^[0-9A-F]{12}$", RegexOptions.CultureInvariant) ||
                !ValidLocation(expectedControllerLocation) || !ValidLocation(expectedAcquisitionLocation) ||
                String.Equals(expectedControllerLocation, expectedAcquisitionLocation, StringComparison.Ordinal))
            {
                HardwareStatusSnapshot invalid = Failure(true, "NeedsConfiguration", "硬件身份配置不完整。", "MAC、ControllerLocation 和 AcquisitionLocation 必须明确且不能相同。");
                invalid.HubMac = selectedMac;
                return invalid;
            }
            string integrationExecutable = ResolveIntegrationExecutable();
            string statusPath = ResolveConfiguredPath("Hardware.StatusJsonPath", "StatusJsonPath", "Hardware.StatusPath");
            string markerPath = ResolveConfiguredPath("Hardware.DriverMarkerPath", "DriverMarkerPath", "Hardware.DriverInstalledPath");
            bool driverInstalled = CheckDriverMarker(markerPath, integrationExecutable);
            if (!driverInstalled)
            {
                HardwareStatusSnapshot missing = Failure(true, "MissingDriver", "未检测到已安装的硬件驱动组件，请使用首次硬件配置工具处理。", "Driver marker/runtime not found.");
                missing.HubMac = selectedMac;
                return missing;
            }

            if (!String.IsNullOrWhiteSpace(integrationExecutable))
            {
                if (!File.Exists(integrationExecutable) || !String.Equals(Path.GetExtension(integrationExecutable), ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    HardwareStatusSnapshot missing = Failure(true, "MissingComponents", "硬件联调程序不存在。", integrationExecutable);
                    missing.HubMac = selectedMac;
                    missing.DriverInstalled = driverInstalled;
                    return missing;
                }
                // "Connect device" means verify communication with the device
                // already bound by the vendor manager. Never issue /C or Apply
                // from a refresh, automatic reconnect, or navigation request.
                string savedArt=NormalizeIdentity(LateBound.ReadString(settings,"Hardware.ControllerSerial","ControllerSerial"));
                string savedPico=NormalizeIdentity(LateBound.ReadString(settings,"Hardware.AcquisitionSerial","AcquisitionSerial"));
                if(!BindingImportInspection&&(!Regex.IsMatch(savedArt,"^[0-9]{10}$")||!Regex.IsMatch(savedPico,"^[A-Z0-9]{5}/[0-9]{4}$")))
                    return Failure(true,"NeedsConfiguration","请先在管理设备中完成驱动包绑定并导入设备身份。","Saved ART/Pico pair is missing; no network scan attempted.");
                return RunIntegration(integrationExecutable, false, selectedMac,
                    expectedControllerLocation, expectedAcquisitionLocation,
                    excludedAcquisition, driverInstalled,
                    cancellationToken);
            }

            // Legacy/read-only provider support.  It is never accepted unless its own
            // timestamp is fresh; unlike connector output, this path is not deleted.
            if (!String.IsNullOrWhiteSpace(statusPath) && File.Exists(statusPath))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ParsedStatus parsed = ParseStatusFile(statusPath);
                    return ValidateStatus(parsed, null, selectedMac,
                        expectedControllerLocation, expectedAcquisitionLocation,
                        excludedAcquisition, driverInstalled);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    diagnostics.Error("HardwareStatus", "Configured status document is invalid.", ex);
                    HardwareStatusSnapshot failure = Failure(true, "InvalidStatus", "硬件状态文件无效。", ex.Message);
                    failure.HubMac = selectedMac;
                    failure.DriverInstalled = driverInstalled;
                    return failure;
                }
            }

            HardwareStatusSnapshot notConfigured = Failure(true, "NeedsConfiguration", "未配置硬件联调程序路径。", "IntegrationExecutablePath is empty.");
            notConfigured.HubMac = selectedMac;
            notConfigured.DriverInstalled = driverInstalled;
            return notConfigured;
        }

        private HardwareStatusSnapshot RunIntegration(string executable, bool connect, string mac,
            string controllerLocation, string acquisitionLocation, string excluded, bool driverInstalled,
            CancellationToken cancellationToken, bool disconnect = false, string disconnectIp = "")
        {
            string outputDirectory = Path.Combine(AppPaths.LocalDataDirectory, "hardware-status");
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, "request-" + Guid.NewGuid().ToString("N") + ".xml");
            int? exitCode = null;
            Process process = null;
            Stopwatch total = Stopwatch.StartNew();
            bool localOnly=!disconnect&&!connect&&!BindingImportInspection;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(executable),
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)),
                    Arguments = (disconnect ? "--disconnect" : connect ? "--connect" : "--inspect") + " --enabled --mac=" + mac +
                        " --controller-location=" + controllerLocation + " --acquisition-location=" + acquisitionLocation +
                        (localOnly ? " --local-only --art-serial="+QuoteArgument(NormalizeIdentity(LateBound.ReadString(settings,"Hardware.ControllerSerial","ControllerSerial")))+
                            " --pico-serial="+QuoteArgument(NormalizeIdentity(LateBound.ReadString(settings,"Hardware.AcquisitionSerial","AcquisitionSerial"))) : String.Empty) +
                        (disconnect && !String.IsNullOrEmpty(disconnectIp) ? " --hub-ip=" + disconnectIp : String.Empty) +
                        " --output=" + QuoteArgument(outputPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };
                diagnostics.Info("HardwareStatus", "Starting " + (disconnect ? "explicit selected-module disconnect" : connect ? "connect/check" : "inspection") + " for configured hub " + mac + ".");
                process = Process.Start(start);
                if (process == null) throw new InvalidOperationException("HardwareIntegration.exe did not start.");
                if (!TryRegisterActiveProcess(process, cancellationToken))
                {
                    StopOwnedProcess(process);
                    throw new OperationCanceledException(cancellationToken);
                }

                int timeout = disconnect ? 40000 : connect ? 35000 : 15000;
                Stopwatch wait = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        StopOwnedProcess(process);
                        throw new OperationCanceledException(cancellationToken);
                    }
                    if (wait.ElapsedMilliseconds >= timeout)
                    {
                        StopOwnedProcess(process);
                        throw new TimeoutException("HardwareIntegration.exe timed out.");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                exitCode = process.ExitCode;
                if (!File.Exists(outputPath)) throw new InvalidDataException("Hardware integration output was not created.");
                cancellationToken.ThrowIfCancellationRequested();
                ParsedStatus parsed = ParseStatusFile(outputPath);
                if(localOnly&&parsed.InspectionMode!="LocalBoundPair")
                    throw new InvalidDataException("设备检查组件版本不匹配，未接受旧的网络扫描结果，请修复安装。");
                if (disconnect) diagnostics.Info("HardwareDisconnect", "Release evidence: " + parsed.Timing);
                if (disconnect) return ValidateDisconnectStatus(parsed, exitCode.Value, mac, controllerLocation, acquisitionLocation, driverInstalled);
                HardwareStatusSnapshot result = ValidateStatus(parsed, exitCode, mac, controllerLocation, acquisitionLocation,
                    excluded, driverInstalled);
                if(localOnly)result.Diagnostic+="; InspectionMode=LocalBoundPair; hub association configured, not probed; acquisition interlocks required";
                MarkLiveReadback(result, exitCode.Value, connect, start.FileName, mac, controllerLocation, acquisitionLocation);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                diagnostics.Error("HardwareStatus", "Hardware integration request failed.", ex);
                HardwareStatusSnapshot failure = Failure(true, disconnect ? "DisconnectFailed" : "Error",
                    disconnect ? "无法确认指定设备已断开。" : "硬件联调请求失败。", ex.Message);
                failure.HubMac = mac;
                failure.DriverInstalled = driverInstalled;
                return failure;
            }
            finally
            {
                if (disconnect) diagnostics.Info("HardwareDisconnect", "Request finished; elapsedMs=" + total.ElapsedMilliseconds + "; exit=" + exitCode);
                UnregisterActiveProcess(process);
                if (process != null)
                {
                    try { process.Dispose(); } catch { }
                }
                try { if (File.Exists(outputPath)) File.Delete(outputPath); }
                catch (Exception ex) { diagnostics.Warning("HardwareStatus", "Could not remove unique status output: " + ex.Message); }
            }
        }

        internal static void MarkLiveReadback(HardwareStatusSnapshot result, int exitCode, bool connect,
            string executable, string mac, string controllerLocation, string acquisitionLocation)
        {
            if (result == null || exitCode != 0 || !result.Ready) return;
            if (connect && (String.IsNullOrWhiteSpace(result.ControllerSerial) || String.IsNullOrWhiteSpace(result.AcquisitionSerial) ||
                String.IsNullOrWhiteSpace(result.ControllerInstanceId) || String.IsNullOrWhiteSpace(result.AcquisitionInstanceId) ||
                String.Equals(result.ControllerInstanceId,result.AcquisitionInstanceId,StringComparison.OrdinalIgnoreCase))) return;
            result.FromLiveIntegrationProcess = true;
            result.ConnectionReadbackVerified = connect;
            result.IntegrationExecutablePathUsed = executable;
            result.IntegrationRequestMode = connect ? "--connect" : "--inspect";
            result.RequestedHubMac = mac;
            result.RequestedControllerLocation = controllerLocation;
            result.RequestedAcquisitionLocation = acquisitionLocation;
        }

        private bool TryRegisterActiveProcess(Process process, CancellationToken cancellationToken)
        {
            lock (processGate)
            {
                if (IsDisposed || cancellationToken.IsCancellationRequested) return false;
                activeProcess = process;
                return true;
            }
        }

        private void UnregisterActiveProcess(Process process)
        {
            if (process == null) return;
            lock (processGate)
            {
                if (Object.ReferenceEquals(activeProcess, process)) activeProcess = null;
            }
        }

        private static void StopOwnedProcess(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch { }
            try { process.WaitForExit(2000); } catch { }
        }

        private HardwareStatusSnapshot ValidateStatus(ParsedStatus status, int? exitCode, string expectedMac,
            string expectedControllerLocation, string expectedAcquisitionLocation,
            string excludedAcquisition, bool driverInstalled)
        {
            if (status == null) throw new InvalidDataException("Status document is empty.");
            if (status.Schema != 1) throw new InvalidDataException("Unsupported hardware status schema: " + status.Schema);
            string actualMac = NormalizeMac(status.Mac);
            if (!String.Equals(actualMac, expectedMac, StringComparison.Ordinal))
                throw new InvalidDataException("Hardware status MAC does not match SelectedHubMac.");
            ValidateFreshness(status.Utc, status.MaxAgeSeconds);
            if (exitCode.HasValue && !ExitCodeMatches(exitCode.Value, status.State))
                throw new InvalidDataException("Connector exit code and status state do not agree.");

            ParsedModule controller = null;
            ParsedModule acquisition = null;
            bool excludedSeen = false;
            bool allModulesConnected = status.Modules.Count > 0;
            bool allModulesReady = status.Modules.Count > 0;
            bool invalidRoles = false;
            for (int i = 0; i < status.Modules.Count; i++)
            {
                ParsedModule module = status.Modules[i];
                if (IdentityEquals(module.Identity, excludedAcquisition) ||
                    TransportIdentityEquals(module.TransportIdentity, excludedAcquisition)) excludedSeen = true;
                if (String.Equals(module.Kind, "Controller", StringComparison.OrdinalIgnoreCase))
                {
                    if (controller != null) invalidRoles = true;
                    else controller = module;
                }
                else if (String.Equals(module.Kind, "Acquisition", StringComparison.OrdinalIgnoreCase))
                {
                    if (acquisition != null) invalidRoles = true;
                    else acquisition = module;
                }
                else invalidRoles = true;
                allModulesConnected = allModulesConnected && module.Connected;
                allModulesReady = allModulesReady && module.Ready;
            }
            if (invalidRoles) throw new InvalidDataException("Status contains a duplicate or unknown module Kind.");

            bool stateReady = String.Equals(status.State, "Ready", StringComparison.OrdinalIgnoreCase);
            if (stateReady && (controller == null || acquisition == null))
                throw new InvalidDataException("Ready status must contain Controller and Acquisition modules.");
            string actualControllerLocation = controller == null ? String.Empty : NormalizeLocation(controller.Location);
            string actualAcquisitionLocation = acquisition == null ? String.Empty : NormalizeLocation(acquisition.Location);
            bool identityValid = controller != null && acquisition != null && !excludedSeen &&
                String.Equals(actualControllerLocation, expectedControllerLocation, StringComparison.Ordinal) &&
                String.Equals(actualAcquisitionLocation, expectedAcquisitionLocation, StringComparison.Ordinal);
            // A saved pair may reconnect only as that pair. SelectionProbe clears
            // serials for an explicit new selection, which has its own review.
            string savedController = NormalizeIdentity(LateBound.ReadString(settings, "Hardware.ControllerSerial", "ControllerSerial"));
            string savedAcquisition = NormalizeIdentity(LateBound.ReadString(settings, "Hardware.AcquisitionSerial", "AcquisitionSerial"));
            identityValid = identityValid && (savedController.Length == 0 || (controller != null && IdentityEquals(controller.Identity,savedController))) &&
                (savedAcquisition.Length == 0 || (acquisition != null && IdentityEquals(acquisition.Identity,savedAcquisition)));
            bool connected = stateReady && identityValid && allModulesConnected && allModulesReady;
            bool moduleReady = allModulesReady;

            HardwareStatusSnapshot snapshot = new HardwareStatusSnapshot
            {
                HardwareEnabled = true,
                DriverInstalled = driverInstalled,
                Connected = connected,
                IdentityValid = identityValid,
                StatusCode = status.State,
                // The connector's free-form message may contain driver names or
                // internal location labels. Keep the customer-facing state stable.
                Message = StatusMessage(status.State),
                Diagnostic = "Schema=1; State=" + status.State + "; MaxAgeSeconds=" + status.MaxAgeSeconds.ToString(CultureInfo.InvariantCulture) +
                    "; ControllerLocation=" + actualControllerLocation + "; AcquisitionLocation=" + actualAcquisitionLocation,
                HubMac = actualMac,
                HubIp = status.Ip,
                // These are the real connector-reported identities. Configured notes
                // and module locations must never masquerade as actual serial numbers.
                ControllerSerial = NormalizeIdentity(controller == null ? String.Empty : controller.Identity),
                AcquisitionSerial = NormalizeIdentity(acquisition == null ? String.Empty : acquisition.Identity),
                ControllerTransportSerial = controller == null ? String.Empty : controller.TransportSerial,
                ControllerInstanceId = controller == null ? String.Empty : controller.InstanceId,
                AcquisitionTransportSerial = acquisition == null ? String.Empty : acquisition.TransportSerial,
                AcquisitionInstanceId = acquisition == null ? String.Empty : acquisition.InstanceId,
                ControllerDeviceName = controller == null ? String.Empty : controller.DeviceName,
                AcquisitionDeviceName = acquisition == null ? String.Empty : acquisition.DeviceName,
                CheckedUtc = status.Utc
            };

            if (excludedSeen)
            {
                snapshot.StatusCode = "ExcludedDevice";
                snapshot.Message = "检测到非指定采集设备，请检查硬件配置。";
            }
            else if (stateReady && !identityValid)
            {
                snapshot.StatusCode = "IdentityMismatch";
                snapshot.Message = "设备连接位置与配置不一致，请检查硬件配置。";
            }
            else if (String.Equals(status.State, "Ready", StringComparison.OrdinalIgnoreCase) && (!connected || !moduleReady))
            {
                snapshot.StatusCode = "InvalidStatus";
                snapshot.Message = "设备尚未完全就绪，请检查连接和驱动。";
                snapshot.Connected = false;
            }
            diagnostics.Info("HardwareStatus", "Result " + snapshot.StatusCode + "; ready=" + snapshot.Ready.ToString(CultureInfo.InvariantCulture) + ".");
            return snapshot;
        }

        private HardwareStatusSnapshot ValidateDisconnectStatus(ParsedStatus status, int exitCode, string mac,
            string controllerLocation, string acquisitionLocation, bool driverInstalled)
        {
            if (status == null || status.Schema != 1 || NormalizeMac(status.Mac) != mac)
                throw new InvalidDataException("Disconnect status schema or target MAC is invalid.");
            ValidateFreshness(status.Utc, status.MaxAgeSeconds);
            if (exitCode == 40 && String.Equals(status.State, "DisconnectFailed", StringComparison.Ordinal))
            {
                HardwareStatusSnapshot failed = Failure(true, "DisconnectFailed", "无法确认指定设备已断开。", status.Message);
                failed.HubMac = mac; failed.DriverInstalled = driverInstalled;
                return failed;
            }
            if (exitCode != 0 || !String.Equals(status.State, "Disconnected", StringComparison.Ordinal) || status.Modules.Count != 2)
                throw new InvalidDataException("Connector did not confirm both selected modules were disconnected.");
            bool controller = false, acquisition = false;
            foreach (ParsedModule module in status.Modules)
            {
                if (module.Connected || module.Ready || module.ConnectionState != 1)
                    throw new InvalidDataException("Disconnect result lacks a verified available connection state.");
                if (!controller && module.Kind == "Controller" && NormalizeLocation(module.Location) == controllerLocation) controller = true;
                else if (!acquisition && module.Kind == "Acquisition" && NormalizeLocation(module.Location) == acquisitionLocation) acquisition = true;
                else throw new InvalidDataException("Disconnect status contains an unexpected module identity/location.");
            }
            if (!controller || !acquisition) throw new InvalidDataException("Disconnect result has incomplete selected modules.");
            return new HardwareStatusSnapshot
            {
                HardwareEnabled = true, DriverInstalled = driverInstalled, Connected = false, IdentityValid = true,
                StatusCode = "Disconnected", Message = "指定设备已断开网络连接；未更改设备绑定。", HubMac = mac,
                Diagnostic = "Verified SDK connection state=1 for ControllerLocation=" + controllerLocation + "; AcquisitionLocation=" + acquisitionLocation,
                CheckedUtc = status.Utc
            };
        }

        private static void ValidateFreshness(DateTime utc, int maxAgeSeconds)
        {
            if (utc.Kind != DateTimeKind.Utc) utc = utc.ToUniversalTime();
            if (maxAgeSeconds < 1 || maxAgeSeconds > 30) throw new InvalidDataException("MaxAgeSeconds must be in the range 1..30.");
            TimeSpan age = DateTime.UtcNow - utc;
            if (age.TotalSeconds < -5.0 || age.TotalSeconds > maxAgeSeconds)
                throw new InvalidDataException("Hardware status is stale or has a future timestamp.");
        }

        private static bool ExitCodeMatches(int exitCode, string state)
        {
            string normalized = (state ?? String.Empty).Trim();
            if (exitCode == 0) return String.Equals(normalized, "Ready", StringComparison.OrdinalIgnoreCase);
            if (exitCode == 10)
                return String.Equals(normalized, "Offline", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(normalized, "NotReady", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(normalized, "Busy", StringComparison.OrdinalIgnoreCase) ||
                       String.Equals(normalized, "NeedsConfiguration", StringComparison.OrdinalIgnoreCase);
            if (exitCode == 20) return String.Equals(normalized, "Disabled", StringComparison.OrdinalIgnoreCase);
            if (exitCode == 30) return String.Equals(normalized, "MissingComponents", StringComparison.OrdinalIgnoreCase);
            if (exitCode == 40) return String.Equals(normalized, "Error", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private ParsedStatus ParseStatusFile(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumStatusBytes)
                throw new InvalidDataException("Hardware status file size is invalid.");
            string text;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(stream, true)) text = reader.ReadToEnd();
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("<", StringComparison.Ordinal)) return ParseXml(text);
            if (trimmed.StartsWith("{", StringComparison.Ordinal)) return ParseJson(text);
            throw new InvalidDataException("Hardware status must be XML or JSON.");
        }

        private static ParsedStatus ParseXml(string text)
        {
            XmlDocument document = new XmlDocument { XmlResolver = null };
            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumStatusBytes
            };
            using (StringReader source = new StringReader(text))
            using (XmlReader reader = XmlReader.Create(source, settings)) document.Load(reader);
            XmlElement root = document.DocumentElement;
            if (root == null || !String.Equals(root.Name, "HardwareStatus", StringComparison.Ordinal))
                throw new InvalidDataException("Expected HardwareStatus root element.");
            ParsedStatus status = new ParsedStatus
            {
                Schema = RequiredInt(root.GetAttribute("Schema"), "Schema"),
                State = Required(root.GetAttribute("State"), "State"),
                Mac = Required(root.GetAttribute("Mac"), "Mac"),
                Ip = root.GetAttribute("Ip"),
                InspectionMode = root.GetAttribute("InspectionMode"),
                Utc = RequiredUtc(root.GetAttribute("Utc")),
                MaxAgeSeconds = RequiredInt(root.GetAttribute("MaxAgeSeconds"), "MaxAgeSeconds")
            };
            XmlNode message = root.SelectSingleNode("Message");
            status.Message = message == null ? String.Empty : message.InnerText;
            XmlNode timing = root.SelectSingleNode("DisconnectTiming");
            status.Timing = timing == null ? String.Empty : timing.OuterXml;
            XmlNodeList modules = root.SelectNodes("Module");
            if (modules != null)
            {
                foreach (XmlNode node in modules)
                {
                    XmlElement element = node as XmlElement;
                    if (element == null) continue;
                    string location = Required(element.GetAttribute("Location"), "Module.Location");
                    string identity = FirstNonEmpty(element.GetAttribute("Serial"), element.GetAttribute("SerialNumber"),
                        element.GetAttribute("DeviceSerial"));
                    status.Modules.Add(new ParsedModule
                    {
                        Kind = Required(element.GetAttribute("Kind"), "Module.Kind"),
                        Location = location,
                        Identity = NormalizeIdentity(identity),
                        TransportSerial = element.GetAttribute("TransportSerial"),
                        InstanceId = element.GetAttribute("InstanceId"),
                        DeviceName = element.GetAttribute("DeviceName"),
                        TransportIdentity = FirstNonEmpty(element.GetAttribute("TransportSerial"), element.GetAttribute("InstanceId")),
                        Connected = RequiredBoolean(element.GetAttribute("Connected"), "Module.Connected"),
                        Ready = RequiredBoolean(element.GetAttribute("Ready"), "Module.Ready"),
                        ConnectionState = OptionalConnectionState(element.GetAttribute("ConnectionState"))
                    });
                }
            }
            return status;
        }

        private static ParsedStatus ParseJson(string text)
        {
            object decoded = new JavaScriptSerializer { MaxJsonLength = MaximumStatusBytes }.DeserializeObject(text);
            IDictionary<string, object> root = decoded as IDictionary<string, object>;
            if (root == null) throw new InvalidDataException("Hardware JSON root must be an object.");
            object nested;
            if (TryDictionaryValue(root, "HardwareStatus", out nested) && nested is IDictionary<string, object>) root = (IDictionary<string, object>)nested;
            ParsedStatus status = new ParsedStatus
            {
                Schema = RequiredInt(JsonString(root, "Schema"), "Schema"),
                State = Required(JsonString(root, "State"), "State"),
                Mac = Required(FirstNonEmpty(JsonString(root, "Mac"), JsonString(root, "HubMac")), "Mac"),
                Ip = JsonString(root, "Ip"),
                Utc = RequiredUtc(FirstNonEmpty(JsonString(root, "Utc"), JsonString(root, "CheckedUtc"))),
                MaxAgeSeconds = RequiredInt(JsonString(root, "MaxAgeSeconds"), "MaxAgeSeconds"),
                Message = JsonString(root, "Message")
            };
            object modulesValue;
            if (TryDictionaryValue(root, "Modules", out modulesValue))
            {
                IEnumerable modules = modulesValue as IEnumerable;
                if (modules != null && !(modulesValue is string))
                {
                    foreach (object value in modules)
                    {
                        IDictionary<string, object> module = value as IDictionary<string, object>;
                        if (module == null) continue;
                        string location = Required(JsonString(module, "Location"), "Module.Location");
                        status.Modules.Add(new ParsedModule
                        {
                            Kind = Required(JsonString(module, "Kind"), "Module.Kind"),
                            Location = location,
                            Identity = NormalizeIdentity(FirstNonEmpty(JsonString(module, "Serial"), JsonString(module, "SerialNumber"),
                                JsonString(module, "DeviceSerial"))),
                            TransportSerial = JsonString(module, "TransportSerial"),
                            InstanceId = JsonString(module, "InstanceId"),
                            DeviceName = JsonString(module, "DeviceName"),
                            TransportIdentity = FirstNonEmpty(JsonString(module, "TransportSerial"), JsonString(module, "InstanceId")),
                            Connected = RequiredBoolean(JsonString(module, "Connected"), "Module.Connected"),
                            Ready = RequiredBoolean(JsonString(module, "Ready"), "Module.Ready"),
                            ConnectionState = OptionalConnectionState(JsonString(module, "ConnectionState"))
                        });
                    }
                }
            }
            return status;
        }

        private bool CheckDriverMarker(string configuredMarker, string integrationExecutable)
        {
            if (!String.IsNullOrWhiteSpace(configuredMarker)) return File.Exists(configuredMarker) || Directory.Exists(configuredMarker);
            if (String.IsNullOrWhiteSpace(integrationExecutable) || !File.Exists(integrationExecutable)) return false;
            string root = Path.GetDirectoryName(Path.GetFullPath(integrationExecutable));
            return File.Exists(Path.Combine(root, "runtime", "Sxuptp.dll")) &&
                   File.Exists(Path.Combine(root, "runtime", "SxConnectDev.exe"));
        }

        private string ResolveConfiguredPath(params string[] names)
        {
            string value = LateBound.ReadString(settings, names).Trim();
            if (value.Length == 0) return String.Empty;
            try
            {
                if (!Path.IsPathRooted(value)) value = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, value);
                return Path.GetFullPath(value);
            }
            catch (Exception ex)
            {
                diagnostics.Warning("HardwareStatus", "Configured path is invalid: " + ex.Message);
                return value;
            }
        }

        private string ResolveIntegrationExecutable()
        {
            // Embedded releases validate their immutable payload; never fall
            // back to an old external connector retained in user settings.
            if(EmbeddedModulePayload.HasEmbeddedPayload)return FirstHardwareProvisioning.GetVerifiedConnectorPath();
            // The customer 1.4.1 payload is staged with the desktop.  Prefer it over
            // any legacy standalone-tool path retained in an older settings file.
            string staged = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "modules", "HardwareIntegration", "HardwareIntegration.exe"));
            if (File.Exists(staged)) return staged;
            return ResolveConfiguredPath(
                "Hardware.IntegrationExecutablePath", "Hardware.ConnectorExecutablePath",
                "IntegrationExecutablePath", "ConnectorExecutablePath");
        }

        private static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.IndexOf('\"') >= 0) throw new ArgumentException("Argument paths cannot contain a quote.", "value");
            return "\"" + value + "\"";
        }

        private static string NormalizeMac(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;
            return Regex.Replace(value.ToUpperInvariant(), "[:\\-\\s.]", String.Empty, RegexOptions.CultureInvariant);
        }

        private static string NormalizeIdentity(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim().ToUpperInvariant();
        }

        private static string NormalizeLocation(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim().ToUpperInvariant();
        }

        private static bool ValidLocation(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                Regex.IsMatch(value, "^[A-Z0-9_.:-]+$", RegexOptions.CultureInvariant);
        }

        private static bool IdentityEquals(string source, string expected)
        {
            string actual = NormalizeIdentity(source);
            string configured = NormalizeIdentity(expected);
            return actual.Length > 0 && configured.Length > 0 &&
                String.Equals(actual, configured, StringComparison.Ordinal);
        }

        private static bool TransportIdentityEquals(string source, string expected)
        {
            string transport = NormalizeIdentity(source);
            string configured = NormalizeIdentity(expected);
            if (transport.Length == 0 || configured.Length == 0) return false;
            if (String.Equals(transport, configured, StringComparison.Ordinal)) return true;
            int separator = Math.Max(transport.LastIndexOf('\\'), Math.Max(transport.LastIndexOf('/'), transport.LastIndexOf('#')));
            return separator >= 0 && separator + 1 < transport.Length &&
                String.Equals(transport.Substring(separator + 1), configured, StringComparison.Ordinal);
        }

        private static string StatusMessage(string state)
        {
            if (String.Equals(state, "Ready", StringComparison.OrdinalIgnoreCase)) return "指定设备已连接。";
            if (String.Equals(state, "MissingComponents", StringComparison.OrdinalIgnoreCase)) return "硬件组件缺失，请打开首次硬件配置工具。";
            if (String.Equals(state, "NotReady", StringComparison.OrdinalIgnoreCase)) return "硬件已发现但尚未就绪。";
            if (String.Equals(state, "Busy", StringComparison.OrdinalIgnoreCase)) return "设备正在使用，暂时无法检查。";
            if (String.Equals(state, "NeedsConfiguration", StringComparison.OrdinalIgnoreCase)) return "设备尚未完成配置，请检查硬件设置。";
            if (String.Equals(state, "Offline", StringComparison.OrdinalIgnoreCase)) return "尚未连接指定设备，请检查连接。";
            return "设备当前不可用，请检查连接和硬件设置。";
        }

        private static HardwareStatusSnapshot Failure(bool enabled, string code, string message, string diagnostic)
        {
            return new HardwareStatusSnapshot
            {
                HardwareEnabled = enabled,
                DriverInstalled = false,
                Connected = false,
                IdentityValid = false,
                StatusCode = code,
                Message = message,
                Diagnostic = diagnostic ?? String.Empty,
                CheckedUtc = DateTime.UtcNow
            };
        }

        private static string Required(string value, string name)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException(name + " is required.");
            return value.Trim();
        }

        private static int RequiredInt(string value, string name)
        {
            int parsed;
            if (!Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                throw new InvalidDataException(name + " must be an integer.");
            return parsed;
        }

        private static int? OptionalConnectionState(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            int state = RequiredInt(value, "Module.ConnectionState");
            if (state < 0 || state > 3) throw new InvalidDataException("Module.ConnectionState must be 0..3.");
            return state;
        }

        private static bool RequiredBoolean(string value, string name)
        {
            bool parsed;
            if (!Boolean.TryParse(value, out parsed)) throw new InvalidDataException(name + " must be true or false.");
            return parsed;
        }

        private static DateTime RequiredUtc(string value)
        {
            DateTime parsed;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
                throw new InvalidDataException("Utc must be an ISO-8601 timestamp.");
            return parsed.ToUniversalTime();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return String.Empty;
            for (int i = 0; i < values.Length; i++) if (!String.IsNullOrWhiteSpace(values[i])) return values[i];
            return String.Empty;
        }

        private static bool TryDictionaryValue(IDictionary<string, object> dictionary, string name, out object value)
        {
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if (String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        private static string JsonString(IDictionary<string, object> dictionary, string name)
        {
            object value;
            return TryDictionaryValue(dictionary, name, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : String.Empty;
        }

        private sealed class ParsedStatus
        {
            public int Schema;
            public string State = String.Empty;
            public string Mac = String.Empty;
            public string Ip = String.Empty;
            public string Timing = String.Empty;
            public string InspectionMode = String.Empty;
            public string Message = String.Empty;
            public DateTime Utc;
            public int MaxAgeSeconds;
            public readonly List<ParsedModule> Modules = new List<ParsedModule>();
        }

        private sealed class ParsedModule
        {
            public string Kind = String.Empty;
            public string Location = String.Empty;
            public string Identity = String.Empty;
            public string TransportSerial = String.Empty;
            public string DeviceName = String.Empty;
            public string InstanceId = String.Empty;
            public string TransportIdentity = String.Empty;
            public bool Connected;
            public bool Ready;
            public int? ConnectionState;
        }
    }
}
