// Pure injected discovery and filesystem fixtures. Native defaults always
// throw in this executable; no device discovery, binding or output is allowed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xiongxin.HvDacXpert;

static class HardwareArchitectureDiscoverySmoke
{
    static int passed, scanned, statesRead, bindingsRead, initialized;
    static readonly DateTime Now = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; }
    static string State(XDocument result) { return (string)result.Root.Attribute("State"); }
    static XElement Device(XDocument result) { return result.Root.Elements("Device").Single(); }
    static Server Hub(string mac = "001122334455")
    {
        var hub = new Server { Mac = mac, Ip = "192.0.2.1", RawIp = 1 };
        hub.Devices.Add(new Usb { Server = hub, Vid = 0x3063, Pid = 0x3135, Location = "1-1", Port = "CTRL" });
        hub.Devices.Add(new Usb { Server = hub, Vid = 0x0CE9, Pid = 0x1218, Location = "1-2", Port = "PICO" });
        return hub;
    }
    static XDocument Discover(List<Server> hubs, Func<uint, string, uint> read = null, Func<Usb, bool> bound = null)
    {
        return Integration.Discover(ignored => { scanned++; return hubs; }, (ip, port) => {
            statesRead++; return read == null ? 1u : read(ip, port);
        }, module => { bindingsRead++; return bound != null && bound(module); }, () => Now);
    }
    static void DiscoveryFixtures()
    {
        var result = Discover(new List<Server> { Hub() }); var device = Device(result);
        Check(State(result) == "Complete", "enumeration complete");
        Check(result.Root.Name == "HardwareDiscovery" && (int)result.Root.Attribute("Schema") == 1, "root contract");
        Check((int)result.Root.Attribute("MaxAgeSeconds") == 30 && (DateTime)result.Root.Attribute("Utc") == Now, "fresh completion UTC and age contract");
        Check((string)device.Attribute("State") == "Available" && (bool)device.Attribute("Complete"), "one exact pair available");
        Check((string)device.Attribute("ControllerLocation") == "1-1" && (string)device.Attribute("AcquisitionLocation") == "1-2", "one pair locations; no cartesian product");
        Check(!(bool)device.Attribute("PersistentBound") && bindingsRead == 2, "both binding records read even first unbound");
        Check(!result.Descendants().Any(element => element.Attribute("Ready") != null), "discovery never declares PnP/acquisition Ready");
        result = Discover(new List<Server> { Hub() }, null, ignored => true);
        Check((bool)Device(result).Attribute("PersistentBound"), "two persisted bindings reflected");
        result = Discover(new List<Server> { Hub() }, null, module => module.Vid == 0x3063);
        Check(!(bool)Device(result).Attribute("PersistentBound"), "one binding not considered bound architecture");
        foreach (uint state in new uint[] { 1, 2 })
            Check((string)Device(Discover(new List<Server> { Hub() }, (ip, port) => state)).Attribute("State") == "Available", "local/available transport " + state);
        result = Discover(new List<Server> { Hub() }, (ip, port) => port == "PICO" ? 3u : 1u);
        Check((string)Device(result).Attribute("State") == "InUse" && (bool)Device(result).Attribute("Complete"), "other PC pair not actionable, architecture still complete");
        foreach (uint state in new uint[] { 0, 4, UInt32.MaxValue })
            Check((string)Device(Discover(new List<Server> { Hub() }, (ip, port) => state)).Attribute("State") == "Incomplete", "unknown transport never available " + state);
        var hub = Hub(); hub.Devices[1].Port = "PICO,PICO2";
        Check((string)Device(Discover(new List<Server> { hub }, (ip, port) => port == "PICO2" ? 3u : 1u)).Attribute("State") == "InUse", "every composite port inspected");
        foreach (int count in new[] { 0, 1 })
        {
            hub = Hub(); hub.Devices.RemoveRange(count, hub.Devices.Count - count); int before = statesRead;
            device = Device(Discover(new List<Server> { hub }));
            Check((string)device.Attribute("State") == "Incomplete" && !(bool)device.Attribute("Complete"), "missing modules " + count);
            Check((string)device.Attribute("ControllerLocation") == "" && (string)device.Attribute("AcquisitionLocation") == "" && statesRead == before, "no guessed partial pair " + count);
        }
        foreach (bool controller in new[] { false, true })
        {
            hub = Hub(); var original = hub.Devices[controller ? 0 : 1];
            hub.Devices.Add(new Usb { Server = hub, Vid = original.Vid, Pid = original.Pid, Location = "1-3", Port = "OTHER" });
            int before = statesRead; device = Device(Discover(new List<Server> { hub }));
            Check((string)device.Attribute("State") == "Ambiguous" && !(bool)device.Attribute("Complete"), "multiple matching role refuses pair " + controller);
            Check(statesRead == before && (string)device.Attribute("ControllerLocation") == "", "ambiguous role never probed or guessed " + controller);
        }
        result = Discover(new List<Server> { Hub(), Hub("00:11:22:33:44:55") });
        Check(result.Root.Elements("Device").Count() == 2 && result.Root.Elements("Device").All(element => (string)element.Attribute("State") == "Ambiguous"), "normalized duplicate MAC remains nonactionable");
        foreach (Action<Server> alter in new Action<Server>[] {
            item => item.Devices[1].Location = "1-1", item => item.Devices[1].Port = "ctrl",
            item => item.Devices[0].Port = "CTRL,CTRL", item => item.Devices[1].Server = Hub(),
            item => item.Devices[1].Location = "", item => item.Devices[1].Port = "PICO /C",
            item => item.Devices[1].Port = "", item => item.Devices[1].Location = "1 /C",
            item => item.Devices[1].Location = new string('X', 65), item => item.Devices[1].Port = new string('X', 129) })
        {
            hub = Hub(); alter(hub); int before = statesRead;
            device = Device(Discover(new List<Server> { hub }));
            Check((string)device.Attribute("State") == "Ambiguous" && !(bool)device.Attribute("Complete") && statesRead == before, "unsafe/overlapping metadata refuses pair");
        }
        hub = Hub(); hub.Devices.Add(new Usb { Server = hub, Vid = 1, Pid = 2, Location = "1-3", Port = "CTRL" });
        Check((string)Device(Discover(new List<Server> { hub })).Attribute("State") == "Ambiguous", "unsupported module cannot overlap selected port");
        hub = Hub(); hub.Devices.Add(new Usb { Server = hub, Vid = 1, Pid = 2, Location = "1-3", Port = "UNRELATED" });
        Check((string)Device(Discover(new List<Server> { hub })).Attribute("State") == "Available", "unrelated nonoverlapping module does not create pair");
        hub = Hub(); hub.Devices[1].Pid = 0xFFFF;
        Check((string)Device(Discover(new List<Server> { hub })).Attribute("State") == "Incomplete", "same manufacturer wrong PID not accepted");
        result = Discover(new List<Server>());
        Check(State(result) == "Complete" && !result.Root.Elements("Device").Any(), "honest complete empty enumeration");
        result = Discover(Enumerable.Range(0, 256).Select(index => Hub(index.ToString("X12"))).ToList());
        Check(State(result) == "Complete" && result.Root.Elements("Device").Count() == 256, "maximum 256 devices accepted");
        foreach (List<Server> invalid in new[] { (List<Server>)null, new List<Server> { null },
            Enumerable.Range(0, 257).Select(index => Hub(index.ToString("X12"))).ToList() })
        {
            result = Discover(invalid);
            Check(State(result) == "Error" && !result.Root.Elements("Device").Any(), "invalid list never publishes actionable partial rows");
        }
        foreach (Action<Server> alter in new Action<Server>[] {
            item => item.Mac = "bad", item => item.Ip = "::1", item => item.Ip = "bad",
            item => item.Devices = null, item => item.Devices.Add(null),
            item => item.Devices = Enumerable.Range(0, 257).Select(index => new Usb()).ToList() })
        {
            hub = Hub(); alter(hub); result = Discover(new List<Server> { Hub("FFEEDDCCBBAA"), hub });
            Check(State(result) == "Error" && !result.Root.Elements("Device").Any(), "invalid hub invalidates complete snapshot");
        }
        foreach (int stage in new[] { 0, 1, 2 })
        {
            result = Integration.Discover(ignored => { if (stage == 0) throw new IOException("ART private detail"); return new List<Server> { Hub() }; },
                (ip, port) => { if (stage == 1) throw new IOException("PICO private detail"); return 1; },
                module => { if (stage == 2) throw new IOException("DS600 private detail"); return false; }, () => Now);
            Check(State(result) == "Error" && !result.Root.Elements("Device").Any(), "inspection exception drops whole snapshot " + stage);
            Check(!result.ToString().Contains("private detail"), "customer output omits native detail " + stage);
        }
    }
    static bool Command(string[] args, out int code, Action scanAction = null)
    {
        return Integration.TryDiscoveryCommand(args, out code, ignored => { scanned++; if (scanAction != null) scanAction(); return new List<Server> { Hub() }; },
            (ip, port) => 1, ignored => false, () => initialized++);
    }
    static void CommandFixtures(string root)
    {
        string path = Path.Combine(root, "discovery.xml"); int code;
        Check(Command(new[] { "--DISCOVER", "--OUTPUT=" + path }, out code) && code == 0 && File.Exists(path), "case-consistent command produces XML");
        var result = XDocument.Load(path);
        Check(State(result) == "Complete" && result.Root.Elements("Device").Count() == 1, "atomically published complete XML");
        byte[] original = File.ReadAllBytes(path); int before = scanned, initBefore = initialized;
        Check(Command(new[] { "--discover", "--output=" + path }, out code) && code == 40 && scanned == before && initialized == initBefore, "existing output rejected before discovery/library call");
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "prior output never overwritten");
        var invalid = new List<string[]> {
            new[] { "--discover" }, new[] { "--discover", "--discover" }, new[] { "--discover", "--output=" },
            new[] { "--discover", "--output=relative.xml" }, new[] { "--discover", "--output=C:relative.xml" },
            new[] { "--discover", "--output=\\root-relative.xml" }, new[] { "--discover", "--output=" + Path.Combine(root, "wrong.txt") },
            new[] { "--discover", "--output=" + Path.Combine(root, "missing", "new.xml") },
            new[] { "--discover", "--output=" + root }, new[] { "--discover", null },
            new[] { "--discover", "--output=" + Path.Combine(root, new string('x', 260) + ".xml") } };
        foreach (string option in new[] { "--connect", "--inspect", "--disconnect", "--offline", "--enabled", "--scan", "--mac=001122334455", "--first-run" })
            invalid.Add(new[] { "--discover", "--output=" + Path.Combine(root, "rejected.xml"), option });
        invalid.Add(new[] { "--discover", "--output=" + Path.Combine(root, "one.xml"), "--output=" + Path.Combine(root, "two.xml") });
        foreach (string[] args in invalid)
        {
            before = scanned; initBefore = initialized;
            Check(Command(args, out code) && code == 40 && before == scanned && initBefore == initialized, "invalid/conflicting CLI rejected before SDK");
        }
        Check(!Command(new[] { "--inspect" }, out code), "legacy non-discovery mode not intercepted");
        string race = Path.Combine(root, "race.xml");
        Check(Command(new[] { "--discover", "--output=" + race }, out code, () => File.WriteAllText(race, "other request")) && code == 40, "destination race refuses atomic commit");
        Check(File.ReadAllText(race) == "other request", "destination race preserves other request");
        Check(!Directory.EnumerateFiles(root, ".discovery-*.tmp").Any(), "owned staging outputs cleaned after success/race");
        string failed = Path.Combine(root, "failed.xml");
        Check(Command(new[] { "--discover", "--output=" + failed }, out code, () => { throw new IOException("vendor private"); }) && code == 40, "failed enumeration exit is not success");
        result = XDocument.Load(failed);
        Check(State(result) == "Error" && !result.Root.Elements("Device").Any(), "failed enumeration safe error XML no partial selection");
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Pass an isolated QA directory.");
            string root = Path.Combine(Path.GetFullPath(args[0]), "discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); DiscoveryFixtures(); CommandFixtures(root);
            Console.WriteLine("HardwareArchitectureDiscoverySmoke " + passed + " PASS; HARDWARE_COMMANDS=0; " + root); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
namespace Xiongxin.HvDacXpert
{
    static partial class Integration
    { internal static System.Xml.Linq.XDocument CheckLocal(bool enabled,string mac,string c,string a,string art,string pico){throw new Exception("Local inspection not part of discovery fixture");} }
    sealed class Server { public string Mac, Ip; public uint RawIp; public List<Usb> Devices = new List<Usb>(); }
    sealed class Usb { public Server Server; public ushort Vid, Pid; public string Location, Port; }
    static class Backend
    {
        public static List<Server> Scan(string ip) { throw new Exception("Native discovery forbidden in offline test"); }
        public static List<Server> InspectSingle(string ip) { throw new Exception("Native targeted discovery forbidden in offline test"); }
        public static bool IsBound(Usb usb) { throw new Exception("Native binding registry forbidden in offline test"); }
        public static string FormatMac(string mac) { return mac; }
    }
    static class Native
    {
        public static uint SxuptpGetDeviceStatus(uint ip, string port) { throw new Exception("Native SDK forbidden in offline test"); }
        public static void SetDllDirectory(string path) { throw new Exception("Native library setup forbidden in offline test"); }
    }
    static class ConnectionHealth
    {
        public static bool Connected(Usb usb) { throw new Exception("Native connected probe forbidden"); }
        public static bool Ready(Usb usb) { throw new Exception("Native PnP probe forbidden"); }
    }
    sealed class ModuleIdentity
    {
        public string Serial = "", TransportSerial = "", InstanceId = "", DeviceName = "";
        public static ModuleIdentity Resolve(Usb usb) { throw new Exception("Native identity probe forbidden"); }
    }
}
