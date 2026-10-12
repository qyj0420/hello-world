using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace HVX.HardwareLinkStress.Standalone
{
    // The host must supply the manifest-verified HardwareIntegration.exe from
    // FirstHardwareProvisioning, and must hold its cross-module hardware lease.
    // This adapter never requests a connection or changes a persistent binding.
    public sealed class IntegrationHubMonitor : IHubMonitor
    {
        private readonly string executable;
        private readonly StressIdentity identity;
        private readonly IIntegrationInspectRunner runner;
        private readonly Action<string> verifyExecutable;
        private readonly TimeSpan timeout;
        private readonly SemaphoreSlim requestGate = new SemaphoreSlim(1, 1);

        public IntegrationHubMonitor(string verifiedIntegrationExe, StressRunOptions exactSelection,
            IIntegrationInspectRunner inspectRunner = null, TimeSpan? requestTimeout = null,
            Action<string> verifyManifestAndHash = null)
        {
            if (String.IsNullOrWhiteSpace(verifiedIntegrationExe))
                throw new ArgumentException("A manifest-verified HardwareIntegration.exe path is required.", "verifiedIntegrationExe");
            executable = Path.GetFullPath(verifiedIntegrationExe);
            if (!String.Equals(Path.GetFileName(executable), "HardwareIntegration.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only the existing HardwareIntegration.exe inspection command is supported.", "verifiedIntegrationExe");
            identity = StressIdentity.FromOptions(exactSelection);
            if (inspectRunner == null && verifyManifestAndHash == null)
                throw new ArgumentNullException("verifyManifestAndHash",
                    "Production inspection requires the host's manifest/hash verification before every process launch.");
            runner = inspectRunner ?? new BoundedIntegrationInspectRunner();
            verifyExecutable = verifyManifestAndHash;
            timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
            if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromSeconds(15))
                throw new ArgumentOutOfRangeException("requestTimeout");
        }

        public async Task<HubObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (verifyExecutable != null) verifyExecutable(executable);
                IntegrationInspectReceipt receipt = await runner.InspectAsync(executable, identity.HubMac,
                    identity.ControllerLocation, identity.AcquisitionLocation, timeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return ParseReceipt(receipt, identity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // An inspection error is not proof of disconnection. The engine
                // stops transfers on Unknown and records this separate failure.
                return Unknown("Read-only inspection failed: " + error.GetType().Name);
            }
            finally { requestGate.Release(); }
        }

        internal static HubObservation ParseReceipt(IntegrationInspectReceipt receipt, StressIdentity selected)
        {
            if (receipt == null || selected == null || String.IsNullOrWhiteSpace(receipt.Xml) ||
                receipt.Xml.Length > 1024 * 1024 || receipt.StartedUtc.Kind != DateTimeKind.Utc ||
                receipt.FinishedUtc.Kind != DateTimeKind.Utc || receipt.FinishedUtc < receipt.StartedUtc)
                return Unknown("Inspection receipt is missing, oversized or has invalid timing.");
            try
            {
                XmlDocument document = new XmlDocument { XmlResolver = null };
                XmlReaderSettings xmlOptions = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1024 * 1024
                };
                using (StringReader source = new StringReader(receipt.Xml))
                using (XmlReader reader = XmlReader.Create(source, xmlOptions)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "HardwareStatus" || root.GetAttribute("Schema") != "1" ||
                    !String.Equals(root.GetAttribute("Mac"), selected.HubMac, StringComparison.Ordinal) ||
                    root.GetAttribute("MaxAgeSeconds") != "30")
                    return Unknown("Inspection schema or selected hub identity did not match.");
                DateTime checkedUtc;
                if (!DateTime.TryParse(root.GetAttribute("Utc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out checkedUtc) || checkedUtc.Kind != DateTimeKind.Utc ||
                    checkedUtc < receipt.StartedUtc.AddSeconds(-2) ||
                    checkedUtc > receipt.FinishedUtc.AddSeconds(2) ||
                    DateTime.UtcNow - checkedUtc > TimeSpan.FromSeconds(30) ||
                    checkedUtc - DateTime.UtcNow > TimeSpan.FromSeconds(5))
                    return Unknown("Inspection result is stale or has an invalid timestamp.");

                string state = root.GetAttribute("State");
                int messages = 0;
                foreach (XmlNode child in root.ChildNodes)
                {
                    if (child.NodeType != XmlNodeType.Element) continue;
                    if (child.Name == "Message")
                    {
                        if (++messages > 1 || child.ChildNodes.Count > 1)
                            return Unknown("Inspection message structure is ambiguous.");
                    }
                    else if (child.Name != "Module") return Unknown("Inspection contains an unknown child element.");
                }
                XmlNodeList modules = root.SelectNodes("Module");
                int count = modules == null ? 0 : modules.Count;
                if (state == "Offline" && receipt.ExitCode == 10 && count == 0)
                    return new HubObservation { Hub = LinkState.Offline, ControllerPort = LinkState.Unknown,
                        AcquisitionPort = LinkState.Unknown, Detail = "Selected hub was not found by the read-only inspection." };
                if ((state != "Ready" && state != "NotReady") ||
                    (state == "Ready" ? receipt.ExitCode != 0 : receipt.ExitCode != 10) || count != 2)
                    return Unknown("Inspection did not return a complete selected pair.");

                XmlElement controller = null, acquisition = null;
                foreach (XmlNode node in modules)
                {
                    XmlElement module = node as XmlElement;
                    if (module == null || module.ChildNodes.Count != 0) return Unknown("Module result is ambiguous.");
                    if (module.GetAttribute("Kind") == "Controller" && controller == null) controller = module;
                    else if (module.GetAttribute("Kind") == "Acquisition" && acquisition == null) acquisition = module;
                    else return Unknown("Module roles are duplicated or unknown.");
                }
                if (!ExactModule(controller, selected.ControllerLocation, selected.ExpectedArtSerial, "3063", "3135") ||
                    !ExactModule(acquisition, selected.AcquisitionLocation, selected.ExpectedPicoSerial, "0CE9", "1218") ||
                    String.Equals(controller.GetAttribute("InstanceId"), acquisition.GetAttribute("InstanceId"), StringComparison.OrdinalIgnoreCase))
                    return Unknown("Selected module port or serial identity is ambiguous.");

                LinkState art = PortState(controller);
                LinkState pico = PortState(acquisition);
                if (state == "Ready" && (art != LinkState.Online || pico != LinkState.Online))
                    return Unknown("Ready disagreed with the two selected module states.");
                if (state == "NotReady" && art == LinkState.Online && pico == LinkState.Online)
                    return Unknown("NotReady disagreed with the two selected module states.");
                return new HubObservation { Hub = LinkState.Online, ControllerPort = art,
                    AcquisitionPort = pico, Detail = "Read-only inspection: selected hub and exact module identities verified; state=" + state + "." };
            }
            catch (XmlException) { return Unknown("Inspection XML is invalid."); }
            catch (ArgumentException) { return Unknown("Inspection XML contains invalid identity fields."); }
        }

        private static bool ExactModule(XmlElement module, string expectedLocation, string expectedSerial,
            string vid, string pid)
        {
            if (module == null || !String.Equals(module.GetAttribute("Location"), expectedLocation, StringComparison.Ordinal) ||
                !String.Equals(module.GetAttribute("Serial"), expectedSerial, StringComparison.OrdinalIgnoreCase)) return false;
            string instance = module.GetAttribute("InstanceId");
            string transport = module.GetAttribute("TransportSerial");
            string prefix = "USB\\VID_" + vid + "&PID_" + pid;
            int separator = instance.LastIndexOf('\\');
            if (instance.Length > 512 || transport.Length == 0 || transport.Length > 512 || separator < 0 ||
                !instance.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) &&
                !instance.StartsWith(prefix + "&", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(instance.Substring(separator + 1), transport, StringComparison.OrdinalIgnoreCase))
                return false;
            return BooleanAttribute(module, "Connected").HasValue && BooleanAttribute(module, "Ready").HasValue;
        }

        private static LinkState PortState(XmlElement module)
        {
            bool? connected = BooleanAttribute(module, "Connected"), ready = BooleanAttribute(module, "Ready");
            if (connected == false && ready == true) return LinkState.Unknown;
            if (connected == false) return LinkState.Offline;
            return connected == true && ready == true ? LinkState.Online : LinkState.Unknown;
        }

        private static bool? BooleanAttribute(XmlElement node, string name)
        {
            string text = node.GetAttribute(name);
            if (text == "true") return true;
            if (text == "false") return false;
            return null;
        }

        private static HubObservation Unknown(string detail)
        {
            return new HubObservation { Hub = LinkState.Unknown, ControllerPort = LinkState.Unknown,
                AcquisitionPort = LinkState.Unknown, Detail = detail };
        }
    }

    public sealed class IntegrationInspectReceipt
    {
        public int ExitCode;
        public string Xml;
        public DateTime StartedUtc;
        public DateTime FinishedUtc;
    }

    public interface IIntegrationInspectRunner
    {
        Task<IntegrationInspectReceipt> InspectAsync(string verifiedExecutable, string mac,
            string controllerLocation, string acquisitionLocation, TimeSpan timeout, CancellationToken cancellationToken);
    }

    // This is the only process-launching implementation. It starts the exact
    // integration program with --inspect; no connection or driver command exists.
    public sealed class BoundedIntegrationInspectRunner : IIntegrationInspectRunner
    {
        private const int MaximumXmlBytes = 1024 * 1024;

        public Task<IntegrationInspectReceipt> InspectAsync(string verifiedExecutable, string mac,
            string controllerLocation, string acquisitionLocation, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => InspectCore(verifiedExecutable, mac, controllerLocation,
                acquisitionLocation, timeout, cancellationToken), cancellationToken);
        }

        private static IntegrationInspectReceipt InspectCore(string executable, string mac,
            string controllerLocation, string acquisitionLocation, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!String.Equals(Path.GetFileName(executable), "HardwareIntegration.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(executable) || !Regex.IsMatch(mac ?? String.Empty, "^[0-9A-F]{12}$") ||
                !Location(controllerLocation) || !Location(acquisitionLocation) || controllerLocation == acquisitionLocation ||
                timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromSeconds(15))
                throw new InvalidOperationException("The read-only inspection target or request is invalid.");
            string directory = Path.Combine(Path.GetTempPath(), "HVX-readonly-inspect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string output = Path.Combine(directory, "status.xml");
            Process process = null;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(executable),
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)),
                    Arguments = "--inspect --enabled --mac=" + mac +
                        " --controller-location=" + controllerLocation +
                        " --acquisition-location=" + acquisitionLocation +
                        " --output=\"" + output + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var receipt = new IntegrationInspectReceipt { StartedUtc = DateTime.UtcNow };
                process = Process.Start(start);
                if (process == null) throw new IOException("Read-only inspection process did not start.");
                Stopwatch clock = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (token.IsCancellationRequested) { StopOwnedProcess(process); token.ThrowIfCancellationRequested(); }
                    if (clock.Elapsed > timeout)
                    {
                        StopOwnedProcess(process);
                        throw new TimeoutException("Read-only inspection exceeded its deadline.");
                    }
                }
                token.ThrowIfCancellationRequested();
                receipt.FinishedUtc = DateTime.UtcNow;
                receipt.ExitCode = process.ExitCode;
                FileInfo info = new FileInfo(output);
                if (!info.Exists || info.Length <= 0 || info.Length > MaximumXmlBytes ||
                    (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Read-only inspection output is missing, oversized or redirected.");
                using (FileStream stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    receipt.Xml = reader.ReadToEnd();
                return receipt;
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) StopOwnedProcess(process); } catch { }
                    process.Dispose();
                }
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                try { Directory.Delete(directory); } catch { }
            }
        }

        private static bool Location(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                Regex.IsMatch(value, "^[A-Z0-9_.:-]+$");
        }

        private static void StopOwnedProcess(Process process)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { process.WaitForExit(2000); } catch { }
        }
    }
}
