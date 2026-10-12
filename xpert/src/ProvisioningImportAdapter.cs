using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    // Pure import gate for the one-time 1.4.8 provisioning result. No hardware or settings I/O here.
    internal sealed class ProvisioningCandidate
    {
        internal string Mac, IpAtExport, ControllerLocation, AcquisitionLocation;
        internal string ControllerInstanceId, AcquisitionInstanceId;
        internal bool PersistentMode;
        internal DateTime VerifiedUtc;
    }

    // This must be populated from the existing 100 HardwareStatusAdapter.Refresh(false),
    // using an in-memory candidate settings clone and its exact HardwareIntegration.exe.
    internal sealed class ExactInspection
    {
        internal bool FromExactConnectorInspect, Ready;
        internal string StatusCode, HubMac, RequestedMac, RequestedControllerLocation, RequestedAcquisitionLocation;
        internal string IntegrationExecutablePathUsed, ExpectedIntegrationExecutablePath, IntegrationRequestMode;
        internal string ControllerSerial, AcquisitionSerial;
        internal string ControllerInstanceId, AcquisitionInstanceId;
        internal string ControllerTransportSerial, AcquisitionTransportSerial;
        internal DateTime CheckedUtc;
    }

    internal sealed class ProvisioningActivation
    {
        internal ProvisioningCandidate Candidate;
        internal bool HardwareEnabled;
        internal bool StrongTransportIdentity;
        internal string Reason;
        internal string ControllerSerial, AcquisitionSerial;
        internal DateTime InspectionCheckedUtc, ActivatedUtc;
        internal string EvidenceDigest;
    }

    internal static class ProvisioningImportAdapter
    {
        private const int MaximumBytes = 65536;
        private static readonly Regex MacPattern = new Regex("^[0-9A-F]{12}$", RegexOptions.CultureInvariant);
        private static readonly Regex LocationPattern = new Regex("^[A-Z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant);

        internal static ProvisioningCandidate ReadFile(string path, DateTime utcNow)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                throw new InvalidDataException("Provisioning result path must be absolute.");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumBytes)
                throw new InvalidDataException("Provisioning result is missing or exceeds the size limit.");
            using (var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, true)) return Parse(reader.ReadToEnd(), utcNow);
        }

        internal static ProvisioningCandidate Parse(string xml, DateTime utcNow)
        {
            RequireUtc(utcNow);
            if (String.IsNullOrWhiteSpace(xml) || xml.Length > MaximumBytes)
                throw new InvalidDataException("Provisioning result size is invalid.");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumBytes, IgnoreComments = true };
            var document = new XmlDocument { XmlResolver = null };
            using (var source = new StringReader(xml))
            using (var reader = XmlReader.Create(source, settings)) document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.Name != "HardwareProvisioning" || root.GetAttribute("Schema") != "1" ||
                root.GetAttribute("State") != "ReadyAtExport")
                throw new InvalidDataException("Unsupported or unsuccessful provisioning result.");
            string mac = NormalizeMac(root.GetAttribute("Mac"));
            if (!MacPattern.IsMatch(mac) || mac == "000000000000" || mac == "FFFFFFFFFFFF")
                throw new InvalidDataException("Provisioning MAC is invalid.");
            string ip = root.GetAttribute("IpAtExport");
            IPAddress address;
            if (!IPAddress.TryParse(ip, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                !String.Equals(address.ToString(),ip,StringComparison.Ordinal))
                throw new InvalidDataException("Provisioning IPv4 address is invalid.");
            string persistent = root.GetAttribute("PersistentMode");
            if (persistent != "auto" && persistent != "manual")
                throw new InvalidDataException("Provisioning persistent mode is invalid.");
            DateTime verified = ParseUtc(root.GetAttribute("VerifiedUtc"));
            RequireRecent(verified, utcNow, TimeSpan.FromMinutes(5), "Provisioning result");

            var elements = root.ChildNodes.OfType<XmlElement>().ToArray();
            if (elements.Length != 2 || elements.Any(e => e.Name != "Module") ||
                root.ChildNodes.OfType<XmlNode>().Any(n => n.NodeType == XmlNodeType.Text && !String.IsNullOrWhiteSpace(n.Value)))
                throw new InvalidDataException("Provisioning result must contain exactly two modules.");
            var controllers=elements.Where(e => e.GetAttribute("Kind") == "Controller").ToArray();
            var acquisitions=elements.Where(e => e.GetAttribute("Kind") == "Acquisition").ToArray();
            if (controllers.Length != 1 || acquisitions.Length != 1)
                throw new InvalidDataException("Provisioning result contains duplicate or unknown module roles.");
            XmlElement controller = controllers[0];
            XmlElement acquisition = acquisitions[0];
            if (controller == acquisition ||
                controller.GetAttribute("Vid").ToUpperInvariant() != "3063" || controller.GetAttribute("Pid").ToUpperInvariant() != "3135" ||
                acquisition.GetAttribute("Vid").ToUpperInvariant() != "0CE9" || acquisition.GetAttribute("Pid").ToUpperInvariant() != "1218")
                throw new InvalidDataException("Provisioning result contains a duplicate or unexpected module identity.");
            string controllerLocation = NormalizeLocation(controller.GetAttribute("Location"));
            string acquisitionLocation = NormalizeLocation(acquisition.GetAttribute("Location"));
            if (!LocationPattern.IsMatch(controllerLocation) || !LocationPattern.IsMatch(acquisitionLocation) ||
                controllerLocation == acquisitionLocation)
                throw new InvalidDataException("Provisioning module locations are invalid or duplicate.");
            string controllerId = OptionalId(controller.GetAttribute("InstanceId"));
            string acquisitionId = OptionalId(acquisition.GetAttribute("InstanceId"));
            if ((controllerId.Length > 0 && !MatchingModuleId(controllerId,"3063","3135")) ||
                (acquisitionId.Length > 0 && !MatchingModuleId(acquisitionId,"0CE9","1218")))
                throw new InvalidDataException("Provisioning instance identity does not match its module VID/PID.");
            if (controllerId.Length > 0 && String.Equals(controllerId, acquisitionId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Provisioning instance identities are duplicate.");
            return new ProvisioningCandidate { Mac=mac, IpAtExport=ip, ControllerLocation=controllerLocation,
                AcquisitionLocation=acquisitionLocation, ControllerInstanceId=controllerId,
                AcquisitionInstanceId=acquisitionId, PersistentMode=persistent=="auto", VerifiedUtc=verified };
        }

        internal static ProvisioningActivation Pending(ProvisioningCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            return new ProvisioningActivation { Candidate=candidate, HardwareEnabled=false,
                Reason="Candidate only; the exact 100 connector has not confirmed Ready." };
        }

        internal static ProvisioningActivation Inspect(ProvisioningCandidate candidate,
            Func<ProvisioningCandidate, ExactInspection> exactInspect, DateTime utcNow)
        {
            return Inspect(candidate, exactInspect, () => utcNow);
        }

        internal static ProvisioningActivation Inspect(ProvisioningCandidate candidate,
            Func<ProvisioningCandidate, ExactInspection> exactInspect, Func<DateTime> utcClock)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (exactInspect == null) throw new ArgumentNullException("exactInspect");
            if (utcClock == null) throw new ArgumentNullException("utcClock");
            DateTime startedUtc = utcClock();
            RequireUtc(startedUtc);
            RequireRecent(candidate.VerifiedUtc, startedUtc, TimeSpan.FromMinutes(5), "Provisioning result");
            ExactInspection status = exactInspect(candidate);
            DateTime finishedUtc = utcClock();
            RequireUtc(finishedUtc);
            if (finishedUtc < startedUtc)
                throw new InvalidDataException("Inspection clock moved backwards.");
            RequireRecent(candidate.VerifiedUtc, finishedUtc, TimeSpan.FromMinutes(5), "Provisioning result");
            var result = Pending(candidate);
            if (status == null || !status.FromExactConnectorInspect || !status.Ready ||
                !String.Equals(status.StatusCode,"Ready",StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(status.IntegrationRequestMode,"--inspect",StringComparison.Ordinal) ||
                !SameAbsolutePath(status.IntegrationExecutablePathUsed,status.ExpectedIntegrationExecutablePath) ||
                NormalizeMac(status.HubMac) != candidate.Mac || NormalizeMac(status.RequestedMac) != candidate.Mac ||
                NormalizeLocation(status.RequestedControllerLocation) != candidate.ControllerLocation ||
                NormalizeLocation(status.RequestedAcquisitionLocation) != candidate.AcquisitionLocation)
            {
                result.Reason="Exact connector did not confirm Ready for this MAC and module pair.";
                return result;
            }
            try { RequireRecent(status.CheckedUtc, finishedUtc, TimeSpan.FromSeconds(30), "Connector inspection"); }
            catch (InvalidDataException)
            {
                result.Reason="Exact connector status is stale.";
                return result;
            }
            bool controllerStrong, acquisitionStrong;
            if (!MatchTransportIdentity(candidate.ControllerInstanceId,status.ControllerInstanceId,status.ControllerTransportSerial,out controllerStrong) ||
                !MatchTransportIdentity(candidate.AcquisitionInstanceId,status.AcquisitionInstanceId,status.AcquisitionTransportSerial,out acquisitionStrong))
            {
                result.Reason="The selected module PnP transport identity changed after provisioning.";
                return result;
            }
            result.StrongTransportIdentity=controllerStrong && acquisitionStrong;
            result.HardwareEnabled=result.StrongTransportIdentity;
            result.Reason=result.StrongTransportIdentity
                ? "Exact connector Ready and both PnP transport identities match; host may now save settings."
                : "At least one selected module lacks comparable PnP transport identity; hardware remains disabled.";
            result.ControllerSerial=status.ControllerSerial ?? String.Empty;
            result.AcquisitionSerial=status.AcquisitionSerial ?? String.Empty;
            if (result.HardwareEnabled)
            {
                result.InspectionCheckedUtc=status.CheckedUtc;
                result.ActivatedUtc=finishedUtc;
                result.EvidenceDigest=EvidenceDigest(result);
            }
            return result;
        }

        internal static bool IsCurrentActivation(ProvisioningActivation result, DateTime utcNow)
        {
            try
            {
                RequireUtc(utcNow);
                if (result == null || !result.HardwareEnabled || !result.StrongTransportIdentity ||
                    result.Candidate == null || String.IsNullOrEmpty(result.EvidenceDigest)) return false;
                RequireRecent(result.Candidate.VerifiedUtc,utcNow,TimeSpan.FromMinutes(5),"Provisioning result");
                RequireRecent(result.InspectionCheckedUtc,utcNow,TimeSpan.FromSeconds(30),"Connector inspection");
                RequireRecent(result.ActivatedUtc,utcNow,TimeSpan.FromSeconds(30),"Activation");
                return String.Equals(result.EvidenceDigest,EvidenceDigest(result),StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool SameAbsolutePath(string actual,string expected)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(actual) && !String.IsNullOrWhiteSpace(expected) &&
                    Path.IsPathRooted(actual) && Path.IsPathRooted(expected) &&
                    String.Equals(Path.GetFullPath(actual),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string EvidenceDigest(ProvisioningActivation value)
        {
            var candidate=value.Candidate;
            string[] fields = { candidate.Mac, candidate.IpAtExport, candidate.ControllerLocation,
                candidate.AcquisitionLocation, candidate.ControllerInstanceId, candidate.AcquisitionInstanceId,
                candidate.PersistentMode ? "auto" : "manual",
                candidate.VerifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                value.ControllerSerial, value.AcquisitionSerial,
                value.InspectionCheckedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                value.ActivatedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                value.HardwareEnabled ? "true" : "false", value.StrongTransportIdentity ? "true" : "false" };
            using (var sha=SHA256.Create())
            {
                byte[] bytes=Encoding.UTF8.GetBytes(String.Join("\u001f",fields.Select(s=>s??String.Empty)));
                return Convert.ToBase64String(sha.ComputeHash(bytes));
            }
        }

        private static string NormalizeMac(string text)
        { return (text ?? String.Empty).Trim().Replace(":",String.Empty).Replace("-",String.Empty).ToUpperInvariant(); }
        private static string NormalizeLocation(string text)
        { return (text ?? String.Empty).Trim().ToUpperInvariant(); }
        private static string OptionalId(string text)
        {
            string value=(text ?? String.Empty).Trim();
            if (value.Length > 512) throw new InvalidDataException("Provisioning instance identity is too long.");
            return value;
        }
        private static bool MatchingModuleId(string value,string vid,string pid)
        {
            string prefix="USB\\VID_"+vid+"&PID_"+pid;
            return value.StartsWith(prefix+"\\",StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(prefix+"&",StringComparison.OrdinalIgnoreCase);
        }
        private static bool MatchTransportIdentity(string exportedInstanceId, string inspectedInstanceId,
            string inspectedTransportSerial, out bool strong)
        {
            strong=false;
            string expected=NormalizeIdentity(exportedInstanceId);
            string actual=NormalizeIdentity(inspectedInstanceId);
            string transport=NormalizeIdentity(inspectedTransportSerial);
            if (expected.Length == 0) return true; // MAC + location still gate; mark weak.
            if (actual.Length > 0 && actual != expected) return false;
            if (transport.Length > 0 && transport != LastComponent(expected)) return false;
            strong=actual.Length > 0 || transport.Length > 0;
            return true;
        }
        private static string NormalizeIdentity(string value)
        { return (value ?? String.Empty).Trim().ToUpperInvariant(); }
        private static string LastComponent(string value)
        {
            // Mirror 100 ModuleIdentity.LastComponent: TransportSerial is the
            // final backslash-separated segment of the selected PnP InstanceId.
            int index=value.LastIndexOf('\\');
            return index >= 0 && index+1 < value.Length ? value.Substring(index+1) : value;
        }
        private static DateTime ParseUtc(string text)
        {
            DateTime result;
            if (String.IsNullOrWhiteSpace(text) || !text.EndsWith("Z",StringComparison.Ordinal) ||
                !DateTime.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out result) ||
                result.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("Provisioning VerifiedUtc must be a UTC timestamp.");
            return result;
        }
        private static void RequireUtc(DateTime utcNow)
        { if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("The validation clock must be UTC."); }
        private static void RequireRecent(DateTime timestamp, DateTime utcNow, TimeSpan limit, string label)
        {
            if (timestamp.Kind != DateTimeKind.Utc) throw new InvalidDataException(label + " timestamp is not UTC.");
            TimeSpan age=utcNow-timestamp;
            if (age < TimeSpan.FromSeconds(-5) || age > limit)
                throw new InvalidDataException(label + " is stale or from the future.");
        }
    }
}
