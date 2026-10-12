using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Xiongxin.HvDacXpert
{
    static partial class Integration
    {
        // Discovery never connects, changes bindings, installs a driver, probes
        // PnP readiness or initializes an acquisition device. Backend.Scan is
        // DS600-filtered; its only write is its existing bounded IP cache.
        internal static XDocument Discover(Func<string, List<Server>> scan = null,
            Func<uint, string, uint> readState = null, Func<Usb, bool> isBound = null,
            Func<DateTime> utcNow = null)
        {
            scan = scan ?? Backend.Scan;
            readState = readState ?? Native.SxuptpGetDeviceStatus;
            isBound = isBound ?? Backend.IsBound;
            utcNow = utcNow ?? (() => DateTime.UtcNow);
            try
            {
                List<Server> hubs = scan("");
                if (hubs == null || hubs.Count > 256) throw new InvalidDataException("Invalid discovery count.");
                foreach (Server hub in hubs)
                {
                    IPAddress address;
                    if (hub == null || !Regex.IsMatch(Normalize(hub.Mac), "^[0-9A-F]{12}$") ||
                        !IPAddress.TryParse(hub.Ip, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                        hub.Devices == null || hub.Devices.Count > 256 || hub.Devices.Any(module => module == null))
                        throw new InvalidDataException("Invalid discovery identity.");
                }
                var duplicates = new HashSet<string>(hubs.GroupBy(hub => Normalize(hub.Mac), StringComparer.Ordinal)
                    .Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.Ordinal);
                var root = DiscoveryResult("Complete", utcNow(), "Devices discovered; connection and acquisition readiness have not been verified.");
                foreach (Server hub in hubs)
                    root.Root.Add(DescribeArchitecture(hub, duplicates.Contains(Normalize(hub.Mac)), readState, isBound));
                // A fresh timestamp describes completion, not the start of a
                // potentially slow SDK enumeration.
                root.Root.SetAttributeValue("Utc", utcNow().ToUniversalTime().ToString("O"));
                return root;
            }
            catch (Exception)
            {
                // Never return an actionable subset when enumeration, state or
                // persistent-binding inspection failed. Vendor details belong
                // in support diagnostics, not customer device names.
                return DiscoveryResult("Error", utcNow(), "Device discovery could not be completed. Check the connection and refresh.");
            }
        }

        static XDocument DiscoveryResult(string state, DateTime utc, string message)
        {
            return new XDocument(new XElement("HardwareDiscovery", new XAttribute("Schema", 1),
                new XAttribute("Utc", utc.ToUniversalTime().ToString("O")), new XAttribute("MaxAgeSeconds", 30),
                new XAttribute("State", state), new XElement("Message", message)));
        }

        static XElement DescribeArchitecture(Server hub, bool duplicateMac,
            Func<uint, string, uint> readState, Func<Usb, bool> isBound)
        {
            Usb[] controllers = hub.Devices.Where(module => module.Vid == 0x3063 && module.Pid == 0x3135).ToArray();
            Usb[] acquisitions = hub.Devices.Where(module => module.Vid == 0x0CE9 && module.Pid == 0x1218).ToArray();
            string state = "Incomplete", reason = "Both required modules have not been discovered.";
            string controllerLocation = "", acquisitionLocation = "";
            bool complete = false, bound = false;
            bool duplicateLocation = hub.Devices.GroupBy(module => (module.Location ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Key.Length > 0 && group.Count() > 1);
            bool duplicatePort = hub.Devices.SelectMany(module => (module.Port ?? "").Split(','))
                .Where(port => port.Length > 0).GroupBy(port => port, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
            if (duplicateMac || duplicateLocation || duplicatePort || controllers.Length > 1 || acquisitions.Length > 1)
            {
                state = "Ambiguous";
                reason = "The device or module identity is ambiguous; no module pair has been selected.";
            }
            else if (controllers.Length == 1 && acquisitions.Length == 1)
            {
                Usb[] pair = { controllers[0], acquisitions[0] };
                string[] locations = pair.Select(module => (module.Location ?? "").Trim().ToUpperInvariant()).ToArray();
                bool valid = pair.All(module => module.Server == hub && ValidDiscoveryPort(module.Port)) &&
                    locations.All(ValidDisconnectLocation) && locations[0] != locations[1];
                string[] ports = valid ? pair.SelectMany(module => module.Port.Split(',')).ToArray() : new string[0];
                if (!valid || ports.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ports.Length)
                {
                    state = "Ambiguous";
                    reason = "The module locations or transport ports are invalid or overlap.";
                }
                else
                {
                    controllerLocation = locations[0]; acquisitionLocation = locations[1]; complete = true;
                    uint[] states = pair.SelectMany(module => module.Port.Split(',')
                        .Select(port => readState(hub.RawIp, port))).ToArray();
                    if (states.Any(value => value == 3))
                    { state = "InUse"; reason = "A required module is in use by another computer."; }
                    else if (states.Any(value => value != 1 && value != 2))
                    { state = "Incomplete"; reason = "A required module's connection state is unknown; refresh before connecting."; }
                    else
                    { state = "Available"; reason = "The module pair is available; driver and acquisition readiness must still be checked."; }
                    // Read both bindings: short-circuiting can conceal a failed
                    // second registry inspection behind an unbound first card.
                    bool controllerBound = isBound(pair[0]), acquisitionBound = isBound(pair[1]);
                    bound = controllerBound && acquisitionBound;
                }
            }
            return new XElement("Device", new XAttribute("Mac", Normalize(hub.Mac)), new XAttribute("Ip", hub.Ip),
                new XAttribute("ControllerLocation", controllerLocation), new XAttribute("AcquisitionLocation", acquisitionLocation),
                new XAttribute("PersistentBound", bound), new XAttribute("Complete", complete),
                new XAttribute("State", state), new XAttribute("Reason", reason));
        }

        static bool ValidDiscoveryPort(string port)
        {
            return !String.IsNullOrEmpty(port) && port.Length <= 128 &&
                Regex.IsMatch(port, "^[A-Za-z0-9_.:-]+(,[A-Za-z0-9_.:-]+)*$");
        }

        internal static bool TryDiscoveryCommand(string[] args, out int exitCode,
            Func<string, List<Server>> scan = null, Func<uint, string, uint> readState = null,
            Func<Usb, bool> isBound = null, Action initializeLibraryPath = null)
        {
            exitCode = 40;
            if (args == null || !args.Any(argument => String.Equals(argument, "--discover", StringComparison.OrdinalIgnoreCase))) return false;
            // Accept one mode and one output only. In particular, conflicting
            // connect/offline/install options are rejected before any SDK call.
            if (args.Length != 2 || args.Count(argument => String.Equals(argument, "--discover", StringComparison.OrdinalIgnoreCase)) != 1)
                return true;
            string output = args.SingleOrDefault(argument => argument != null && argument.StartsWith("--output=", StringComparison.OrdinalIgnoreCase));
            if (output == null) return true;
            string path;
            try { path = ValidateDiscoveryOutput(output.Substring(9)); }
            catch (Exception) { return true; }
            try
            {
                if (initializeLibraryPath == null) Native.SetDllDirectory(Path.Combine(Root, "runtime"));
                else initializeLibraryPath();
                XDocument result = Discover(scan, readState, isBound);
                WriteDiscoveryOutput(path, result);
                exitCode = (string)result.Root.Attribute("State") == "Complete" ? 0 : 40;
            }
            catch (Exception) { exitCode = 40; }
            return true;
        }

        static string ValidateDiscoveryOutput(string output)
        {
            if (String.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output) ||
                !Regex.IsMatch(output, "^(?:[A-Za-z]:[\\\\/]|\\\\\\\\)"))
                throw new ArgumentException("An absolute output path is required.");
            string path = Path.GetFullPath(output);
            if (!String.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase) ||
                path.Length >= 260 || File.Exists(path) || Directory.Exists(path))
                throw new IOException("A new XML output is required.");
            string parent = Path.GetDirectoryName(path);
            if (String.IsNullOrEmpty(parent) || parent.Length >= 210 || !Directory.Exists(parent))
                throw new IOException("The output directory must already exist.");
            for (var directory = new DirectoryInfo(parent); directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Output directory links are not allowed.");
            return path;
        }

        static void WriteDiscoveryOutput(string path, XDocument result)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".discovery-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, CloseOutput = false })) result.Save(writer);
                    stream.Flush(true);
                }
                // Same-directory rename publishes one complete snapshot and
                // refuses a destination created by another request meanwhile.
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
