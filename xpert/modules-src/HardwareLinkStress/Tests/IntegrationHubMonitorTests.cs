using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HVX.HardwareLinkStress.Standalone;

namespace HVX.HardwareLinkStress.Tests
{
    // Fake-only tests: the injected runner never starts HardwareIntegration.exe
    // and never opens an ART, Pico or SX driver.
    internal static class IntegrationHubMonitorTests
    {
        private const string Mac = "1CBCEC5A9C69";
        private const string ArtSerial = "1234567890";
        private const string PicoSerial = "AB123/4567";

        internal static int RunAll()
        {
            int checks = 0;
            Check(Observe(ReadyXml(), 0).AllOnline, "complete exact pair is online", ref checks);

            HubObservation portLoss = Observe(PairXml("NotReady", false, false, true, true), 10);
            Check(portLoss.Hub == LinkState.Online && portLoss.ControllerPort == LinkState.Offline &&
                portLoss.AcquisitionPort == LinkState.Online, "explicit selected-port loss", ref checks);

            HubObservation offline = Observe(StatusXml("Offline", String.Empty), 10);
            Check(offline.Hub == LinkState.Offline && offline.ControllerPort == LinkState.Unknown &&
                offline.AcquisitionPort == LinkState.Unknown, "missing selected hub does not invent port state", ref checks);

            Check(AllUnknown(Observe(ReadyXml().Replace(PicoSerial, "OTHER/0001"), 0)),
                "wrong acquisition serial", ref checks);
            Check(AllUnknown(Observe(ReadyXml().Replace("Location=\"S51\"", "Location=\"H11\""), 0)),
                "duplicate port", ref checks);
            Check(AllUnknown(Observe(ReadyXml().Replace("TransportSerial=\"PICO-PORT\"", "TransportSerial=\"OTHER\""), 0)),
                "wrong transport instance", ref checks);
            Check(AllUnknown(Observe(ReadyXml(), 10)), "state/exit disagreement", ref checks);
            Check(AllUnknown(Observe(ReadyXml().Replace("Connected=\"true\" Ready=\"true\"",
                "Connected=\"false\" Ready=\"true\""), 0)), "inconsistent readiness", ref checks);
            Check(AllUnknown(Observe("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/private.txt'>]>" + ReadyXml(), 0)),
                "DTD is rejected", ref checks);

            DateTime old = DateTime.UtcNow.AddMinutes(-1);
            Check(AllUnknown(Observe(ReadyXml(old), 0, old.AddSeconds(-1), old)),
                "stale receipt", ref checks);

            var fake = new FakeRunner(Receipt(ReadyXml(), 0));
            var monitor = new IntegrationHubMonitor("C:\\fake\\HardwareIntegration.exe", Selection(), fake);
            HubObservation actual = monitor.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(actual.AllOnline && fake.Calls == 1 && fake.Mac == Mac && fake.Controller == "H11" &&
                fake.Acquisition == "S51", "runner received only the exact selected pair", ref checks);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                bool rejected = false;
                try { monitor.ObserveAsync(cancelled.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected && fake.Calls == 1, "cancel before inspection starts", ref checks);
            }
            bool unverifiedDefaultRejected = false;
            try { new IntegrationHubMonitor("C:\\fake\\HardwareIntegration.exe", Selection()); }
            catch (ArgumentNullException) { unverifiedDefaultRejected = true; }
            Check(unverifiedDefaultRejected, "real process runner requires manifest/hash verifier", ref checks);
            return checks;
        }

        private static HubObservation Observe(string xml, int code)
        { return Observe(xml, code, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow); }

        private static HubObservation Observe(string xml, int code, DateTime start, DateTime end)
        {
            var fake = new FakeRunner(new IntegrationInspectReceipt
            { Xml = xml, ExitCode = code, StartedUtc = start, FinishedUtc = end });
            return new IntegrationHubMonitor("C:\\fake\\HardwareIntegration.exe", Selection(), fake)
                .ObserveAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        private static IntegrationInspectReceipt Receipt(string xml, int code)
        {
            return new IntegrationInspectReceipt { Xml = xml, ExitCode = code,
                StartedUtc = DateTime.UtcNow.AddSeconds(-1), FinishedUtc = DateTime.UtcNow };
        }

        private static StressRunOptions Selection()
        {
            return new StressRunOptions { DeviceKey = Mac + "/H11/S51", HubMac = Mac,
                ControllerLocation = "H11", AcquisitionLocation = "S51",
                ExpectedArtSerial = ArtSerial, ExpectedPicoSerial = PicoSerial };
        }

        private static string ReadyXml()
        { return ReadyXml(DateTime.UtcNow.AddMilliseconds(-250)); }

        private static string ReadyXml(DateTime utc)
        { return PairXml("Ready", true, true, true, true, utc); }

        private static string PairXml(string state, bool artConnected, bool artReady, bool picoConnected, bool picoReady)
        { return PairXml(state, artConnected, artReady, picoConnected, picoReady, DateTime.UtcNow.AddMilliseconds(-250)); }

        private static string PairXml(string state, bool artConnected, bool artReady, bool picoConnected, bool picoReady, DateTime utc)
        {
            return StatusXml(state,
                "<Module Kind=\"Controller\" Location=\"H11\" Serial=\"" + ArtSerial +
                "\" TransportSerial=\"1234567890\" InstanceId=\"USB\\VID_3063&amp;PID_3135\\1234567890\" Connected=\"" +
                Bool(artConnected) + "\" Ready=\"" + Bool(artReady) + "\"/>" +
                "<Module Kind=\"Acquisition\" Location=\"S51\" Serial=\"" + PicoSerial +
                "\" TransportSerial=\"PICO-PORT\" InstanceId=\"USB\\VID_0CE9&amp;PID_1218\\PICO-PORT\" Connected=\"" +
                Bool(picoConnected) + "\" Ready=\"" + Bool(picoReady) + "\"/>", utc);
        }

        private static string StatusXml(string state, string modules)
        { return StatusXml(state, modules, DateTime.UtcNow.AddMilliseconds(-250)); }

        private static string StatusXml(string state, string modules, DateTime utc)
        {
            return "<HardwareStatus Schema=\"1\" State=\"" + state + "\" Mac=\"" + Mac +
                "\" Utc=\"" + utc.ToString("O", CultureInfo.InvariantCulture) +
                "\" MaxAgeSeconds=\"30\">" + modules + "</HardwareStatus>";
        }

        private static string Bool(bool value) { return value ? "true" : "false"; }

        private static bool AllUnknown(HubObservation value)
        {
            return value != null && value.Hub == LinkState.Unknown &&
                value.ControllerPort == LinkState.Unknown && value.AcquisitionPort == LinkState.Unknown;
        }

        private static void Check(bool condition, string name, ref int checks)
        {
            if (!condition) throw new InvalidOperationException("IntegrationHubMonitor fake test failed: " + name);
            checks++;
        }

        private sealed class FakeRunner : IIntegrationInspectRunner
        {
            private readonly IntegrationInspectReceipt receipt;
            internal int Calls;
            internal string Mac, Controller, Acquisition;

            internal FakeRunner(IntegrationInspectReceipt receipt) { this.receipt = receipt; }

            public Task<IntegrationInspectReceipt> InspectAsync(string executable, string mac,
                string controllerLocation, string acquisitionLocation, TimeSpan timeout, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls++;
                Mac = mac; Controller = controllerLocation; Acquisition = acquisitionLocation;
                return Task.FromResult(receipt);
            }
        }
    }
}
