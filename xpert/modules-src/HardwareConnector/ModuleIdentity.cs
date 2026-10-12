using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Xiongxin.HvDacXpert
{
    sealed class ModuleIdentityResult
    {
        public string Serial = String.Empty;
        public string TransportSerial = String.Empty;
        public string InstanceId = String.Empty;
        public string DeviceName = String.Empty;
    }

    sealed class PicoInspectionBusyException : IOException
    {
        internal PicoInspectionBusyException(string operation, long status)
            : base(operation + " temporarily unavailable; private bridge status=" +
                status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".") { }
    }

    /// <summary>
    /// Resolves identities without opening a scope or starting acquisition.
    /// ART serials come from the selected Silex module's PnP instance.  A Pico
    /// serial is accepted only when the selected module owns every present Pico
    /// PnP instance and PicoSDK enumerates exactly one physical unit.  This
    /// deliberately refuses direct/multiple-device ambiguity.
    /// </summary>
    static class ModuleIdentity
    {
        internal delegate uint PicoEnumeration(out short count, StringBuilder serials, ref short length);
        const ushort ArtVid = 0x3063, ArtPid = 0x3135;
        const ushort PicoVid = 0x0CE9, PicoPid = 0x1218;
        static readonly Regex ArtSerial = new Regex("^[0-9]{10}$", RegexOptions.CultureInvariant);
        static readonly Regex PicoSerial = new Regex("^[A-Z0-9]{5}/[0-9]{4}$", RegexOptions.CultureInvariant);

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceInfo { public uint Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }

        [DllImport("Sxuptp.dll", CharSet = CharSet.Ansi)]
        static extern uint SxuptpGetInstanceId(uint ip, string location, out IntPtr ids);
        [DllImport("Sxuptp.dll")]
        static extern bool SxuptpFreeInstanceId(IntPtr ids);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr window, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr list, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInstanceIdW(IntPtr list, ref DeviceInfo info, StringBuilder id, int length, out uint needed);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CharSet=CharSet.Unicode, ExactSpelling=true, CallingConvention=CallingConvention.StdCall)]
        static extern int HvxConfigurePicoPathW(string path);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ps5000aEnumerateUnits", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)]
        static extern uint EnumerateUnits(out short count, StringBuilder serials, ref short length);

        public static ModuleIdentityResult Resolve(Usb module)
        {
            if (module == null) return new ModuleIdentityResult();
            List<string> selected = SelectedInstanceIds(module);
            List<string> present = PresentInstances(module.Vid, module.Pid);
            List<string> pico = module.Vid == PicoVid && module.Pid == PicoPid
                ? EnumeratePicoSerials()
                : new List<string>();
            var result = ResolveEvidence(module.Vid, module.Pid, selected, present, pico);
            if (module.Vid == ArtVid && module.Pid == ArtPid && result.Serial.Length != 0)
            {
                // Read the existing DMC table only. Never refresh, rename, connect or
                // select another module to fill a missing alias.
                try { result.DeviceName = ResolveArtDeviceName(result.Serial, result.InstanceId, ArtAliases.Read()); }
                catch { result.DeviceName = String.Empty; }
            }
            return result;
        }

        internal static string ResolveArtDeviceName(string serial, string instance, IEnumerable<ArtRecord> records)
        {
            uint number;
            if (!UInt32.TryParse(serial, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out number) || number == 0 ||
                number.ToString(System.Globalization.CultureInfo.InvariantCulture) != serial || String.IsNullOrWhiteSpace(instance)) return String.Empty;
            var all = (records ?? Enumerable.Empty<ArtRecord>()).ToArray();
            var matches = all.Where(r => r.Supported && r.Serial == number).ToArray();
            if (matches.Length != 1) return String.Empty;
            var match = matches[0];
            if (!match.Started || !String.Equals(match.Instance, instance, StringComparison.OrdinalIgnoreCase) ||
                !Regex.IsMatch(match.Name ?? "", "^[A-Za-z0-9_-]{1,63}$", RegexOptions.CultureInvariant) ||
                all.Count(r => String.Equals(r.Name, match.Name, StringComparison.OrdinalIgnoreCase)) != 1) return String.Empty;
            return match.Name;
        }

        internal static ModuleIdentityResult ResolveEvidence(ushort vid, ushort pid,
            IEnumerable<string> selectedInstances, IEnumerable<string> presentInstances,
            IEnumerable<string> picoSerials)
        {
            var selected = Distinct(selectedInstances);
            var present = Distinct(presentInstances);
            var result = new ModuleIdentityResult();
            if (selected.Count != 1) return result;
            result.InstanceId = selected[0];
            result.TransportSerial = LastComponent(result.InstanceId);

            if (vid == ArtVid && pid == ArtPid)
            {
                if (ArtSerial.IsMatch(result.TransportSerial)) result.Serial = result.TransportSerial;
                return result;
            }
            if (vid != PicoVid || pid != PicoPid) return result;

            // Every present matching Pico PnP node must belong to the selected
            // network module.  A direct or second network scope makes identity
            // association ambiguous, even if PicoSDK happens to list one unit.
            if (present.Count != selected.Count || !new HashSet<string>(present, StringComparer.OrdinalIgnoreCase).SetEquals(selected))
                return result;
            string[] serials = (picoSerials ?? Enumerable.Empty<string>())
                .Select(NormalizePico).Where(value => PicoSerial.IsMatch(value))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (serials.Length == 1) result.Serial = serials[0];
            return result;
        }

        static List<string> SelectedInstanceIds(Usb module)
        {
            var result = new List<string>();
            IntPtr ids = IntPtr.Zero;
            try
            {
                uint count = SxuptpGetInstanceId(module.Server.RawIp, module.Location, out ids);
                if (count == 0 || count > 128 || ids == IntPtr.Zero) return result;
                string prefix = "USB\\VID_" + module.Vid.ToString("X4") + "&PID_" + module.Pid.ToString("X4");
                for (int index = 0; index < count; index++)
                {
                    string id = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(ids, index * IntPtr.Size));
                    if (!String.IsNullOrWhiteSpace(id) &&
                        (id.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) ||
                         id.StartsWith(prefix + "&", StringComparison.OrdinalIgnoreCase)))
                        result.Add(id.Trim());
                }
                return result;
            }
            finally { if (ids != IntPtr.Zero) SxuptpFreeInstanceId(ids); }
        }

        internal static List<string> PresentInstances(ushort vid, ushort pid)
        {
            var result = new List<string>();
            string prefix = "USB\\VID_" + vid.ToString("X4") + "&PID_" + pid.ToString("X4") + "\\";
            IntPtr list = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, 6);
            if (list == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; ; index++)
                {
                    var info = new DeviceInfo { Size = (uint)Marshal.SizeOf(typeof(DeviceInfo)) };
                    if (!SetupDiEnumDeviceInfo(list, index, ref info))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259) throw new Win32Exception(error);
                        break;
                    }
                    var id = new StringBuilder(1024); uint needed;
                    if (!SetupDiGetDeviceInstanceIdW(list, ref info, id, id.Capacity, out needed))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (id.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(id.ToString());
                }
                return result;
            }
            finally { SetupDiDestroyDeviceInfoList(list); }
        }

        internal static List<string> EnumeratePicoSerials()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pico Technology", "SDK", "lib", "ps5000a.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pico Technology", "PicoScope 7 T&M Stable", "ps5000a.dll")
            };
            return EnumeratePicoSerials(candidates, File.Exists, HvxConfigurePicoPathW, EnumerateUnits);
        }

        // The injected overload is also used by offline tests. Neither path opens
        // a unit or changes a task. A denied SDK-family lease is not no-device evidence.
        internal static List<string> EnumeratePicoSerials(IEnumerable<string> candidates,
            Func<string, bool> exists, Func<string, int> configure, PicoEnumeration enumerate)
        {
            int lastConfigurationFailure = 0;
            foreach (string path in candidates)
            {
                if (!exists(path)) continue;
                int configured = configure(path);
                RequireInspectionLease(configured, "HvxConfigurePicoPathW");
                if (configured != 0) { lastConfigurationFailure = configured; continue; }
                short count, length = 4096; var serials = new StringBuilder(4096);
                uint status = enumerate(out count, serials, ref length);
                RequireInspectionLease(status, "ps5000aEnumerateUnits");
                if (status != 0) throw new IOException("Pico enumeration failed; status=" +
                    status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                if (count < 0) throw new IOException("Pico enumeration returned an invalid unit count.");
                if (count == 0) return new List<string>();
                return serials.ToString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            }
            if (lastConfigurationFailure != 0) throw new IOException("Pico inspection configuration failed; status=" +
                lastConfigurationFailure.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            return new List<string>();
        }

        static void RequireInspectionLease(long status, string operation)
        {
            // Exact private codes from HvxAcquisitionNative.h, not Pico vendor
            // statuses or exception-message matching. Configuration uses int32;
            // Pico operations use the separate unsigned private status range.
            if (status == -4850003L || status == -4850005L ||
                status == 0xE4850003L || status == 0xE4850005L)
                throw new PicoInspectionBusyException(operation, status);
        }

        static List<string> Distinct(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>()).Where(value => !String.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string LastComponent(string instanceId)
        {
            if (String.IsNullOrWhiteSpace(instanceId)) return String.Empty;
            int separator = instanceId.LastIndexOf('\\');
            return (separator < 0 ? instanceId : instanceId.Substring(separator + 1)).Trim().ToUpperInvariant();
        }

        static string NormalizePico(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? String.Empty : value.Trim().ToUpperInvariant().Replace('／', '/');
        }
    }
}
