using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HVX.HardwareLinkStress
{
    // Wires the run timeline to bounded JSONL storage and captures Windows
    // event evidence on failure/reconnection. The bundle never claims that a
    // nearby Windows event caused the loss, nor that DS600 firmware was read.
    public sealed class StressEvidenceCoordinator : IStressEvidenceSink, IDisposable
    {
        private readonly object sync = new object();
        private readonly SessionLog timeline;
        private readonly string incidentDirectory;
        private readonly IEventEvidenceSource windows;
        private readonly string hubMac;
        private readonly string hubIp;
        private readonly string[] selectedPnPIds;
        private readonly string explicitSxLogFile;
        private bool disposed;

        public StressEvidenceCoordinator(string sessionDirectory, string sessionId,
            string hubMac, string hubIp, string[] selectedPnPIds,
            string explicitSxLogFile = null, IEventEvidenceSource windows = null)
        {
            if (String.IsNullOrWhiteSpace(sessionDirectory))
                throw new ArgumentException("A session evidence directory is required.", "sessionDirectory");
            this.timeline = new SessionLog(sessionDirectory, sessionId);
            incidentDirectory = Path.Combine(Path.GetFullPath(sessionDirectory), "incidents");
            this.windows = windows ?? new WindowsEventEvidenceSource();
            this.hubMac = hubMac ?? String.Empty;
            this.hubIp = hubIp ?? String.Empty;
            this.selectedPnPIds = selectedPnPIds == null ? new string[0] : (string[])selectedPnPIds.Clone();
            this.explicitSxLogFile = explicitSxLogFile;
        }

        public string CurrentTimelinePath { get { return timeline.CurrentPath; } }

        public void Record(StressEvent item)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (item.RunId == Guid.Empty || item.Sequence < 1 || item.Utc == DateTimeOffset.MinValue)
                throw new ArgumentException("The diagnostic event has no run identity, sequence or time.", "item");
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("StressEvidenceCoordinator");
                bool incident = IsIncident(item.Kind);
                timeline.Append("stress_event", incident ? "warning" : "info",
                    new Dictionary<string, string>
                    {
                        { "runId", item.RunId.ToString("D") },
                        { "sequence", item.Sequence.ToString(CultureInfo.InvariantCulture) },
                        { "cycle", item.Cycle.ToString(CultureInfo.InvariantCulture) },
                        { "event", item.Kind.ToString() },
                        { "detail", item.Detail ?? String.Empty },
                        { "artBytesTotal", item.ArtBytesTotal.ToString(CultureInfo.InvariantCulture) },
                        { "picoBytesTotal", item.PicoBytesTotal.ToString(CultureInfo.InvariantCulture) },
                        { "elapsedSeconds", item.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture) }
                    }, item.Utc.UtcDateTime, incident);
                if (!incident) return;

                IncidentEvidenceRequest request = new IncidentEvidenceRequest
                {
                    IncidentId = Guid.NewGuid(),
                    IncidentUtc = item.Utc.UtcDateTime,
                    Before = TimeSpan.FromMinutes(2),
                    After = TimeSpan.Zero,
                    Channels = EventEvidenceExporter.DefaultChannels,
                    SelectedPnPInstanceIds = selectedPnPIds,
                    HubMac = hubMac,
                    HubIp = hubIp,
                    ExplicitSxLogFile = explicitSxLogFile
                };
                EventEvidenceResult result = EventEvidenceExporter.Capture(incidentDirectory, request, windows);
                timeline.Append("incident_bundle", result.Limited ? "warning" : "info",
                    new Dictionary<string, string>
                    {
                        { "trigger", item.Kind.ToString() },
                        { "path", result.DirectoryPath },
                        { "windowsEvents", result.WindowsEventCount.ToString(CultureInfo.InvariantCulture) },
                        { "allChannelsRead", result.AllChannelsRead.ToString() },
                        { "explicitSxLogCopied", result.ExplicitSxLogCopied.ToString() },
                        { "warnings", String.Join(" | ", result.Warnings) }
                    }, DateTime.UtcNow, true);
            }
        }

        private static bool IsIncident(StressEventKind kind)
        {
            return kind == StressEventKind.LinkDropped || kind == StressEventKind.TransferFault ||
                kind == StressEventKind.MonitorFault || kind == StressEventKind.Reconnected ||
                kind == StressEventKind.ReconnectRejected;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                timeline.Dispose();
            }
        }
    }
}
