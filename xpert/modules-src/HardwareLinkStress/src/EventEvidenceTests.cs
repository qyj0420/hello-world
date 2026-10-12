using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using HVX.HardwareLinkStress;

// Offline only. The injected source never opens Event Log, drivers, or devices.
internal static class EventEvidenceTests
{
    sealed class FakeSource : IEventEvidenceSource
    {
        public readonly Dictionary<string, List<EventEvidenceRecord>> Events =
            new Dictionary<string, List<EventEvidenceRecord>>(StringComparer.Ordinal);
        public string FailingChannel;
        public IEnumerable<EventEvidenceRecord> Read(string channel, DateTime fromUtc, DateTime toUtc, int maxRecords)
        {
            if (channel == FailingChannel) throw new UnauthorizedAccessException("synthetic denied channel");
            return Events.ContainsKey(channel) ? Events[channel] : new List<EventEvidenceRecord>();
        }
    }

    static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static Dictionary<string, object> Parse(string line)
    {
        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line);
    }

    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "hvx-stress-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Console.WriteLine("offline session-log test");
            TestSessionLog(root);
            Console.WriteLine("offline event-evidence test");
            TestEvidence(root);
            Console.WriteLine("offline limit test");
            TestLimit(root);
            Console.WriteLine("HardwareLinkStress diagnostics offline tests: PASS");
        }
        catch (Exception error)
        {
            Console.WriteLine("OFFLINE TEST FAILED: " + error.GetType().FullName + ": " + error.Message);
            throw;
        }
        finally { Directory.Delete(root, true); }
    }

    static void TestSessionLog(string root)
    {
        string dir = Path.Combine(root, "segments");
        using (var log = new SessionLog(dir, "offline-test", 1024, 2))
        {
            log.Append("sample", "info", new Dictionary<string, string> { { "text", "a\n\"b\\c" } },
                new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc), true);
            for (int i = 0; i < 5; i++)
                log.Append("sample", "info", new Dictionary<string, string> { { "payload", new string('X', 200) } },
                    DateTime.UtcNow);
            var files = Directory.GetFiles(dir, "*.jsonl");
            Require(files.Length == 2, "A full segment must rotate without overwriting prior evidence.");
            string first = File.ReadAllLines(files.OrderBy(x => x, StringComparer.Ordinal).First())[0];
            Require((string)Parse(first)["text"] == "a\n\"b\\c", "JSONL must preserve escaped diagnostic text.");
            bool stopped = false;
            try
            {
                for (int i = 0; i < 10; i++)
                    log.Append("sample", "info", new Dictionary<string, string> { { "payload", new string('X', 200) } },
                        DateTime.UtcNow);
            }
            catch (IOException) { stopped = true; }
            Require(stopped, "Capacity must stop rather than delete old evidence.");
        }
        string transitionPath;
        using (var log = new SessionLog(Path.Combine(root, "transitions"), "offline-recovery"))
        {
            bool rejected = false;
            try { log.AppendLinkTransition(LinkTransitionKind.ResumeNewCycle,
                new Dictionary<string, string>(), DateTime.UtcNow); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "A recovered link must not imply a valid resumed cycle without a new cycle ID.");
            log.AppendLinkTransition(LinkTransitionKind.ReconnectAttempt,
                new Dictionary<string, string> { { "attempt", "1" } }, DateTime.UtcNow);
            log.AppendLinkTransition(LinkTransitionKind.ResumeNewCycle,
                new Dictionary<string, string> { { "newCycleId", "cycle-2" } }, DateTime.UtcNow);
            transitionPath = log.CurrentPath;
        }
        string text = File.ReadAllText(transitionPath);
        Require(text.Contains("reconnect_attempt") && text.Contains("resume_new_cycle"),
            "Recovery stages must remain distinguishable.");
    }

    static void TestEvidence(string root)
    {
        DateTime at = new DateTime(2026, 10, 7, 2, 0, 0, DateTimeKind.Utc);
        string xml = "<Event><EventData><Data>USB\\VID_3063&amp;PID_3135\\ABC</Data></EventData></Event>";
        var fake = new FakeSource { FailingChannel = "Microsoft-Windows-DeviceSetupManager/Admin" };
        fake.Events["Microsoft-Windows-Kernel-PnP/Configuration"] = new List<EventEvidenceRecord>
        {
            new EventEvidenceRecord { Channel = "Microsoft-Windows-Kernel-PnP/Configuration",
                Provider = "Microsoft-Windows-Kernel-PnP", EventId = 410, RecordId = 10,
                TimeCreatedUtc = at, OriginalXml = xml }
        };
        string sx = Path.Combine(root, "user-selected-sx.log");
        File.WriteAllText(sx, "synthetic SX evidence only");
        var request = new IncidentEvidenceRequest
        {
            IncidentId = Guid.NewGuid(), IncidentUtc = at,
            Before = TimeSpan.FromSeconds(30), After = TimeSpan.FromSeconds(60),
            Channels = new List<string> { "Microsoft-Windows-Kernel-PnP/Configuration",
                "Microsoft-Windows-DeviceSetupManager/Admin" },
            SelectedPnPInstanceIds = new List<string> { "USB\\VID_3063&PID_3135\\ABC" },
            ExplicitSxLogFile = sx
        };
        var result = EventEvidenceExporter.Capture(Path.Combine(root, "evidence"), request, fake,
            () => at.AddMinutes(2));
        Require(result.WindowElapsed && result.WindowsEventCount == 1 && result.ExplicitSxLogCopied,
            "Evidence should report elapsed window, one event, and explicit SX copy.");
        Require(result.Limited && !result.AllChannelsRead,
            "An elapsed time window must not hide a denied event channel.");
        Require(result.Warnings.Any(w => w.Contains("synthetic denied channel")),
            "Channel failure must remain visible.");
        string line = File.ReadAllLines(Directory.GetFiles(result.DirectoryPath, "session-*.jsonl")[0])
            .First(x => x.Contains("windows_event"));
        var parsed = Parse(line);
        Require((string)parsed["originalEventRecordXml"] == xml,
            "EventRecord.ToXml text must survive export exactly.");
        Require((string)parsed["relation"] == "selected-pnp-instance-text-match-only",
            "XML-escaped PnP IDs must correlate without implying a cause.");
        Require((string)parsed["causality"] == "unproven", "A relation must not imply a cause.");
        Require(File.ReadAllText(Path.Combine(result.DirectoryPath, "explicit-sx-file.log")) ==
            "synthetic SX evidence only", "Only the explicit SX file should be copied.");
        var summary = Parse(File.ReadAllLines(Path.Combine(result.DirectoryPath, "evidence-summary.jsonl"))[0]);
        Require((string)summary["windowElapsed"] == "True" &&
            (string)summary["allChannelsRead"] == "False",
            "Summary must separate elapsed time from source completeness.");
        Require(((string)summary["ds600FirmwareLog"]).StartsWith("Unavailable"),
            "Do not invent a DS600 firmware-log source.");
    }

    static void TestLimit(string root)
    {
        DateTime at = new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);
        var fake = new FakeSource();
        fake.Events["System"] = Enumerable.Range(0, 201).Select(index => new EventEvidenceRecord
        {
            Channel = "System", Provider = "Synthetic", EventId = 1,
            RecordId = index, TimeCreatedUtc = at, OriginalXml = "<Event/>"
        }).ToList();
        var result = EventEvidenceExporter.Capture(Path.Combine(root, "limited"),
            new IncidentEvidenceRequest { IncidentId = Guid.NewGuid(), IncidentUtc = at,
                Before = TimeSpan.FromSeconds(1), After = TimeSpan.FromSeconds(1),
                Channels = new List<string> { "System" } }, fake, () => at.AddSeconds(2));
        Require(result.Limited && result.WindowsEventCount == 200,
            "Record limit must be explicit, with no silently missing event.");
        bool rejected = false;
        try
        {
            EventEvidenceExporter.Capture(Path.Combine(root, "rejected"),
                new IncidentEvidenceRequest { IncidentId = Guid.NewGuid(), IncidentUtc = at,
                    Channels = new List<string> { "Security" } }, fake, () => at);
        }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "Security log is not on the allowlist.");
    }
}
