using System;
using System.Collections.Generic;
using System.IO;

namespace Xiongxin.ZeroBench
{
    public static class DiscoveryTests
    {
        public static string Run()
        {
            var names = DeviceDiscovery.ParseArtNames(" Dev1, Dev2;Dev3\r\n");
            Require(names.Count == 3 && names[1] == "Dev2", "ART name parsing");
            var serials = DeviceDiscovery.ParsePicoSerials(" 13000/0018,3439/0066\0", 2);
            Require(serials.Count == 2 && serials[0] == "13000/0018", "Pico serial parsing");
            Rejected(() => DeviceDiscovery.ParsePicoSerials("13000/0018", 2), "count mismatch");
            Rejected(() => DeviceDiscovery.ParsePicoSerials("13000/0018,13000/0018", 2), "duplicate serial");
            Rejected(() => DeviceDiscovery.ParsePicoSerials("13000/0018 garbage", 1), "malformed serial");
            Rejected(() => DeviceDiscovery.ParseArtNames("Dev1/ai4"), "channel instead of module");
            var one = new DiscoveryResult();
            one.ArtDevices.Add(new ArtDeviceIdentity { Name = "Dev7", Model = "USB3135", Serial = "102617030" });
            one.PicoSerials.Add("13000/0018");
            var selected = DiscoverySelection.Resolve(one, "", "");
            Require(selected.Ready && selected.ArtDevice.Name == "Dev7" && selected.PicoSerial == "13000/0018", "unique pair suggested");
            var multiple = new DiscoveryResult
            {
                ArtDevices = new List<ArtDeviceIdentity>(one.ArtDevices), PicoSerials = new List<string>(one.PicoSerials)
            };
            multiple.ArtDevices.Add(new ArtDeviceIdentity { Name = "Dev8", Model = "USB3135", Serial = "1023417034" });
            multiple.PicoSerials.Add("3439/0066");
            Require(!DiscoverySelection.Resolve(multiple, "", "").Ready, "multiple unselected");
            selected = DiscoverySelection.Resolve(multiple, "102617030", "13000/0018");
            Require(selected.Ready && selected.ArtDevice.Name == "Dev7" && selected.PicoSerial == "13000/0018", "exact previous pair retained");
            selected = DiscoverySelection.Resolve(one, "99999", "3439/0066");
            Require(selected.ArtDevice == null && selected.PicoSerial == "", "missing old cards never replaced with new unique pair");
            one.ArtError = "One module metadata failed";
            selected = DiscoverySelection.Resolve(one, "", "");
            Require(selected.ArtDevice == null && selected.PicoSerial == "13000/0018", "independent errors, no false unique ART selection");
            selected = DiscoverySelection.Resolve(one, "102617030", "13000/0018");
            Require(selected.Ready, "exact found identity retained despite independent unrelated enumeration error");
            Require(DiscoverySelection.Resolve(new DiscoveryResult(), "", "").Ready == false, "empty lists are not ready");
            return "DISCOVERY_TESTS=PASS: pure parsing, count/duplicate rejection, unique suggestion, exact prior identity, missing-card nonreplacement, independent errors; no SDK or hardware called.";
        }
        private static void Require(bool condition, string name) { if (!condition) throw new Exception("Discovery test failed: " + name); }
        private static void Rejected(Action action, string name)
        { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Require(rejected, name); }
    }
}
