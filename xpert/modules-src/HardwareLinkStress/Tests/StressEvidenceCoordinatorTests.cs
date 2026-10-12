using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HVX.HardwareLinkStress.Tests
{
    public static class StressEvidenceCoordinatorTests
    {
        public static string Run()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "hvx-stress-offline-" + Guid.NewGuid().ToString("N"));
            var source = new EmptyEventSource();
            using (var coordinator = new StressEvidenceCoordinator(root, "offline-test",
                "112233445566", "192.0.2.5", new[] { @"USB\VID_0000&PID_0000\FAKE" }, null, source))
            {
                coordinator.Record(new StressEvent
                {
                    RunId = Guid.NewGuid(), Sequence = 1, Cycle = 2,
                    Utc = DateTimeOffset.UtcNow, Kind = StressEventKind.LinkDropped,
                    Detail = "Offline fake link transition", ArtBytesTotal = 128,
                    PicoBytesTotal = 1024, ElapsedSeconds = 1.25
                });
            }
            string timeline = Directory.GetFiles(root, "session-offline-test-*.jsonl").Single();
            string contents = File.ReadAllText(timeline);
            Require(contents.Contains("LinkDropped") && contents.Contains("incident_bundle"),
                "incident and bundle recorded before completion");
            string incident = Directory.GetDirectories(Path.Combine(root, "incidents")).Single();
            Require(File.Exists(Path.Combine(incident, "evidence-summary.jsonl")), "summary persisted");
            string summary = File.ReadAllText(Path.Combine(incident, "evidence-summary.jsonl"));
            Require(summary.Contains("Unavailable: no verified device-log API configured"),
                "DS600 firmware log unavailable state explicit");
            Require(source.ReadCount == EventEvidenceExporter.DefaultChannels.Length,
                "only fake event source used for each allowed channel");
            return "STRESS_EVIDENCE_COORDINATOR=PASS; fake-only output=" + root;
        }

        private static void Require(bool condition, string label)
        { if (!condition) throw new Exception("Evidence coordinator test failed: " + label); }

        private sealed class EmptyEventSource : IEventEvidenceSource
        {
            internal int ReadCount;
            public IEnumerable<EventEvidenceRecord> Read(string channel, DateTime fromUtc,
                DateTime toUtc, int maxRecords)
            {
                ReadCount++;
                return new EventEvidenceRecord[0];
            }
        }
    }
}
