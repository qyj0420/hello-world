using System;
using System.Collections.Generic;
using System.Threading;

internal static class TestState
{
    internal static int Reads, ReadThread;
    internal static Func<string> Reader;
    internal static string Read()
    {
        Reads++; ReadThread = Thread.CurrentThread.ManagedThreadId;
        return Reader == null ? "bridge=private; injected offline detail" : Reader();
    }
}

internal static class PicoBridgeStatusOfflineTests
{
    private static int checks;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); checks++; }
    private static Exception Catch(Action action)
    { try { action(); } catch (Exception error) { return error; } throw new Exception("Expected failure."); }
    private static void Main()
    {
        var create = new Func<uint,string,Func<string>,bool,InvalidOperationException>[] {
            Scope0.Pico5000aNative.CreateStatusException, Scope1.Pico5000aNative.CreateStatusException,
            Scope2.Pico5000aNative.CreateStatusException, Calibration.Native.CreateStatusException
        };
        uint[] vendors = { 3u, 12u, 14u, 0x27u, 0x119u, 0x11Au, 0x11Eu, 0x10000001u, 0xF0000001u, UInt32.MaxValue };
        foreach (var factory in create)
        {
            foreach (uint status in vendors)
            {
                int reads = 0;
                var failure = factory(status, "original-op", () => { reads++; return "unexpected"; }, true);
                Check(reads == 0, "Vendor errors do not read bridge thread-local state.");
                Check((uint)failure.Data["RawStatus"] == status && (string)failure.Data["StatusDomain"] == "PicoSdk", "Vendor uint32 is preserved.");
                Check(failure.InnerException.Message.Contains("PICO_STATUS=0x" + status.ToString("X8")), "Vendor details retain exact status.");
                Check(!failure.Message.Contains("PICO_STATUS") && !failure.Message.Contains("original-op"), "Customer message omits internal details.");
            }
            for (uint kind = 1; kind <= 13; kind++)
            {
                uint status = 0xE4850000u + kind;
                int reads = 0, thread = 0;
                var failure = factory(status, "original-op", () => { reads++; thread = Thread.CurrentThread.ManagedThreadId; return "injected-path; rawSDK=unchanged"; }, false);
                Check(reads == 1 && thread == Thread.CurrentThread.ManagedThreadId, "Bridge details read once synchronously on caller thread.");
                Check((uint)failure.Data["RawStatus"] == status && (uint)failure.Data["BridgeKind"] == kind && (string)failure.Data["StatusDomain"] == "HvxBridge", "Bridge raw status and kind retained.");
                Check(failure.InnerException.Message.Contains("HVX_BRIDGE_STATUS=0x" + status.ToString("X8")) && failure.InnerException.Message.Contains("injected-path"), "Bridge details retained without vendor alias.");
                Check(!failure.Message.Contains("injected-path") && !failure.ToString().Contains("PICO_STATUS="), "Bridge code never labelled PICO_STATUS; customer has no paths.");
                if (kind == 3 || kind == 5) Check(failure.Message.Contains("in use") && !failure.Message.Contains("disconnect"), "Occupancy is not physical disconnect.");
                if (kind == 13) Check(failure.Message.Contains("Stop the test") && !failure.Message.Contains("disconnect"), "Unconfirmed lease cleanup has a conservative stop fallback, not physical disconnect.");
            }
            var unknown = factory(0xE485F000u, "unknown", () => "unknown private kind", true);
            Check((string)unknown.Data["StatusDomain"] == "HvxBridge" && unknown.Message.Contains("停止试验"), "Unknown reserved bridge status fails closed.");
            var unavailable = factory(0xE4850003u, "diagnostic-failure", () => { throw new Exception("injected reader failure"); }, true);
            Check((uint)unavailable.Data["RawStatus"] == 0xE4850003u && unavailable.ToString().Contains("injected reader failure"), "Diagnostic failure cannot replace original status.");
        }

        var imported = new ImportedInput.CheckHost();
        var checksToCall = new Action<uint>[] { status => Scope0.CheckHost.Check(status), status => Scope1.CheckHost.Check(status), status => Scope2.CheckHost.Check(status),
            status => Calibration.Native.Check(status), status => imported.Check(status, "input-operation"), status => StandaloneInput.CheckHost.Check(status, "input-operation") };
        foreach (var call in checksToCall)
        {
            TestState.Reads = 0; TestState.Reader = null;
            call(0);
            Check(TestState.Reads == 0, "Check success performs no diagnostic or SDK call.");
            var vendor = Catch(() => call(0xF1234567u));
            Check(TestState.Reads == 0 && (uint)vendor.Data["RawStatus"] == 0xF1234567u, "Actual Check preserves high vendor uint32.");
            var bridge = Catch(() => call(0xE4850003u));
            Check(TestState.Reads == 1 && TestState.ReadThread == Thread.CurrentThread.ManagedThreadId && (string)bridge.Data["StatusDomain"] == "HvxBridge", "Actual Check uses same-thread bridge diagnostic.");
            Check(!bridge.ToString().Contains("PICO_STATUS="), "Actual Check does not alias bridge as vendor status.");
            var cleanup = Catch(() => call(0xE485000Du));
            Check((string)cleanup.Data["StatusDomain"] == "HvxBridge" && (uint)cleanup.Data["RawStatus"] == 0xE485000Du &&
                !cleanup.ToString().Contains("PICO_STATUS="), "Actual Check classifies new lease-cleanup status as private, preserving uint32.");
        }
        string logged = null;
        TestState.Reader = () => "original thread-local bridge detail";
        imported.CommunicationError = detail => { logged = detail; TestState.Reader = () => "later callback detail"; };
        var recorded = Catch(() => imported.Check(0xE4850005u, "capture-op"));
        Check(logged.Contains("original thread-local bridge detail") && recorded.ToString().Contains("original thread-local bridge detail") && !recorded.ToString().Contains("later callback detail"), "Snapshot precedes reporting callback.");
        Check(logged.Contains("HVX_BRIDGE_STATUS=0xE4850005") && !logged.Contains("PICO_STATUS="), "Log retains technical bridge domain.");
        var loggerError = new Exception("injected logger error");
        imported.CommunicationError = detail => { throw loggerError; };
        var combined = Catch(() => imported.Check(0x11u, "capture-op")) as AggregateException;
        Check(combined != null && combined.InnerExceptions.Count == 2 && Object.ReferenceEquals(combined.InnerExceptions[1], loggerError) && combined.InnerExceptions[0].ToString().Contains("PICO_STATUS=0x00000011"), "Logger failure retains both original failures.");
        Console.WriteLine("Pico bridge status: " + checks + " checks PASS; 4 production helper bodies / 6 Check bodies; SDK/production DLL loaded=False; HardwareAccessed=False");
    }
}
