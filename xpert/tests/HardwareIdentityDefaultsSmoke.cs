using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Xiongxin.OWTS.Desktop;

// Compile with all src/*.cs, selecting this entry point. No main window is
// constructed, no provider/worker is launched, and all fixture I/O is local QA.
internal static class HardwareIdentityDefaultsSmoke
{
    static readonly string[] IdentityNames = { "SelectedHubMac", "ControllerLocation", "AcquisitionLocation",
        "ControllerSerial", "AcquisitionSerial", "ExcludedAcquisitionSerial" };
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static string qa;

    static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }

    static string Identity(HardwareSettings hardware, string name)
    { return (string)typeof(HardwareSettings).GetProperty(name).GetValue(hardware, null); }

    static void Blank(HardwareSettings hardware, string label)
    {
        Check(hardware != null && !hardware.Enabled, label + ": hardware remains offline");
        foreach (string name in IdentityNames)
            Check(Identity(hardware, name) == String.Empty, label + ": no implicit " + name);
    }

    static AppSettings Configured(bool enabled)
    {
        var value = new AppSettings { DataRoot = qa };
        // Existing persisted identities are data, not replacement defaults.
        value.Hardware.Enabled = enabled;
        value.Hardware.SelectedHubMac = "1CBCEC5A9C5C";
        value.Hardware.ControllerLocation = "H11";
        value.Hardware.AcquisitionLocation = "S51";
        value.Hardware.ControllerSerial = "1023417034";
        value.Hardware.AcquisitionSerial = "130000030";
        value.Hardware.ExcludedAcquisitionSerial = "130000018";
        return value;
    }

    static void InvalidFile(string name, string xml)
    {
        string path = Path.Combine(qa, name + ".xml");
        File.WriteAllText(path, xml, new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(path);
        AppSettings value; string message;
        Check(!AppSettings.TryLoad(path, out value, out message), name + ": invalid settings rejected");
        Check(!String.IsNullOrWhiteSpace(message), name + ": diagnostic provided");
        Blank(value.Hardware, name);
        Blank(AppSettings.Load(path).Hardware, name + " Load fallback");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), name + ": original bytes preserved");
        Check(!File.Exists(path + ".bak"), name + ": read does not create backups");
    }

    static void StartupGate(AppSettings settings, string label)
    {
        // Invoke only the managed, early-return entry gates on a shell that was
        // never constructed. This cannot run Shown, timers, services or a GUI.
        var form = (MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
        typeof(MainForm).GetField("settings", Hidden).SetValue(form, settings);
        int requests = 0, callbacks = 0;
        form.RefreshDeviceForTests = (connect, complete) => { requests++; return false; };
        PropertyInfo enabled = typeof(MainForm).GetProperty("IsHardwareEnabled", Hidden);
        Check(!(bool)enabled.GetValue(form, null), label + ": startup sees offline mode");
        Check(!(bool)typeof(MainForm).GetMethod("CanPollHomeDevice", Hidden).Invoke(form, null),
            label + ": home poll gate remains closed");
        MethodInfo begin = typeof(MainForm).GetMethod("BeginHardwareRefresh", Hidden);
        foreach (bool connect in new[] { false, true })
            begin.Invoke(form, new object[] { connect, true, new Action<HardwareStatusSnapshot>(s => callbacks++) });
        Check(requests == 0 && callbacks == 0, label + ": startup and poll request no inspection or connection");
    }

    static void TransportGate(AppSettings settings, bool enabled, string expected)
    {
        var copy = settings.Clone(); copy.Hardware.Enabled = enabled;
        // Additional fail-closed fixture paths: even a gate regression cannot
        // reach a real integration executable or an installed driver marker.
        copy.Hardware.IntegrationExecutablePath = Path.Combine(qa, "not-installed.exe");
        copy.Hardware.ConnectorExecutablePath = Path.Combine(qa, "not-installed-connector.exe");
        copy.Hardware.DriverMarkerPath = Path.Combine(qa, "not-installed-driver.marker");
        copy.Hardware.DriverPackagePath = Path.Combine(qa, "not-installed-driver-package");
        copy.Hardware.StatusJsonPath = Path.Combine(qa, "not-published.json");
        var diagnostics = new Diagnostics(Path.Combine(qa, "diagnostics"));
        using (var adapter = new HardwareStatusAdapter(copy, diagnostics))
            foreach (bool connect in new[] { false, true })
            {
                // Offline/blank-identity branches return before provider path
                // resolution, driver checks, process launch or enumeration.
                HardwareStatusSnapshot state = adapter.Refresh(connect);
                Check(state.StatusCode == expected, "transport early return: " + expected);
                Check(!state.DriverInstalled && !state.Connected && !state.IdentityValid && !state.Ready,
                    "no fabricated driver, connection or identity");
                Check(state.HardwareEnabled == enabled, "transport retains explicit enable state");
            }
        Check(!Directory.Exists(Path.Combine(qa, "diagnostics")), "early return performs no diagnostics writes");
    }

    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Pass an isolated QA output directory beneath build.");
            string parent = Path.GetFullPath(args[0]);
            if (parent.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) < 0)
                throw new ArgumentException("QA must remain beneath the project build directory.");
            qa = Path.Combine(parent, "identity-defaults-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            Blank(new HardwareSettings(), "constructor");
            var defaults = new AppSettings();
            Blank(defaults.Hardware, "AppSettings constructor");
            Blank(defaults.Clone().Hardware, "default clone");
            var missingHardware = new AppSettings { Hardware = null };
            Blank(missingHardware.Clone().Hardware, "null hardware clone fallback");
            typeof(AppSettings).GetMethod("Normalize", Hidden).Invoke(missingHardware, null);
            Blank(missingHardware.Hardware, "null hardware normalization fallback");
            string message;
            Check(defaults.TryValidate(out message), "offline defaults remain valid");
            StartupGate(defaults, "new profile");
            TransportGate(defaults, false, "Offline");

            string absent = Path.Combine(qa, "absent", "settings.xml"); AppSettings loaded;
            Check(AppSettings.TryLoad(absent, out loaded, out message), "missing file keeps existing successful-default semantics");
            Blank(loaded.Hardware, "missing file"); StartupGate(loaded, "missing file");
            Blank(AppSettings.Load(absent).Hardware, "missing file Load");
            Check(!Directory.Exists(Path.GetDirectoryName(absent)), "missing file creates no directory or config");
            Check(!AppSettings.TryLoad(null, out loaded, out message), "null path diagnosed"); Blank(loaded.Hardware, "null path");
            Check(!AppSettings.TryLoad("", out loaded, out message), "empty path diagnosed"); Blank(loaded.Hardware, "empty path");

            InvalidFile("malformed", "<HVXUnifiedDesktopSettings><Hardware>");
            InvalidFile("wrong-root", "<OtherSettings><Hardware><Enabled>true</Enabled></Hardware></OtherSettings>");
            InvalidFile("invalid-mac", "<HVXUnifiedDesktopSettings><Hardware><SelectedHubMac>NOT-A-MAC</SelectedHubMac></Hardware></HVXUnifiedDesktopSettings>");
            InvalidFile("enabled-only", "<HVXUnifiedDesktopSettings><Hardware><Enabled>true</Enabled></Hardware></HVXUnifiedDesktopSettings>");
            InvalidFile("missing-locations", "<HVXUnifiedDesktopSettings><Hardware><Enabled>true</Enabled><SelectedHubMac>AABBCCDDEEFF</SelectedHubMac></Hardware></HVXUnifiedDesktopSettings>");
            string legacy = Path.Combine(qa, "legacy-without-hardware.xml");
            File.WriteAllText(legacy, "<HVXUnifiedDesktopSettings><LanguageCode>en</LanguageCode></HVXUnifiedDesktopSettings>", new UTF8Encoding(false));
            Check(AppSettings.TryLoad(legacy, out loaded, out message), "legacy file without hardware section remains loadable");
            Blank(loaded.Hardware, "legacy missing section");

            foreach (bool enabled in new[] { false, true })
            {
                AppSettings saved = Configured(enabled);
                string path = Path.Combine(qa, enabled ? "existing-enabled.xml" : "existing-offline.xml");
                saved.Save(path); byte[] before = File.ReadAllBytes(path);
                Check(AppSettings.TryLoad(path, out loaded, out message), "explicit existing identity reloads");
                Check(loaded.Hardware.Enabled == enabled, "saved enable state not migrated");
                foreach (string name in IdentityNames)
                    Check(Identity(saved.Hardware, name) == Identity(loaded.Hardware, name), "existing " + name + " remains exact");
                Check(before.SequenceEqual(File.ReadAllBytes(path)), "loading does not rewrite existing user config");
                loaded.Save(path);
                AppSettings reopened = AppSettings.Load(path);
                Check(reopened.Hardware.Enabled == enabled, "explicit save retains enable state");
                foreach (string name in IdentityNames)
                    Check(Identity(saved.Hardware, name) == Identity(reopened.Hardware, name), "explicit save retains " + name);
                AppSettings copied = loaded.Clone(); copied.Hardware.SelectedHubMac = "AABBCCDDEEFF";
                Check(loaded.Hardware.SelectedHubMac == saved.Hardware.SelectedHubMac, "clone identity edits do not alter original");
            }

            var onlyEnabled = new AppSettings { DataRoot = qa }; onlyEnabled.Hardware.Enabled = true;
            Check(!onlyEnabled.TryValidate(out message) && message.Contains("网络地址"), "enable-only requires explicit MAC");
            string preserved = Path.Combine(qa, "existing-enabled.xml"); byte[] preservedBytes = File.ReadAllBytes(preserved);
            bool refused = false;
            try { onlyEnabled.Save(preserved); } catch (InvalidDataException) { refused = true; }
            Check(refused && preservedBytes.SequenceEqual(File.ReadAllBytes(preserved)), "enable-only cannot overwrite saved user config");
            onlyEnabled.Hardware.SelectedHubMac = "AABBCCDDEEFF";
            Check(!onlyEnabled.TryValidate(out message) && message.Contains("Controller Location"), "MAC alone still requires controller location");
            onlyEnabled.Hardware.ControllerLocation = "H01";
            Check(!onlyEnabled.TryValidate(out message) && message.Contains("Acquisition Location"), "each module location must be explicit");
            TransportGate(defaults, true, "NeedsConfiguration");
            Check(!File.Exists(preserved + ".bak"), "no failed-save backup side effect");
            Console.WriteLine("HARDWARE_IDENTITY_DEFAULTS_PASS=" + checks + "; HARDWARE_COMMANDS=0; NETWORK_REQUESTS=0; USER_CONFIG_IO=0; QA=" + qa);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
