using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace HVX.HardwareLinkStress
{
    public sealed class IncidentEvidenceRequest
    {
        public Guid IncidentId { get; set; }
        public DateTime IncidentUtc { get; set; }
        public TimeSpan Before { get; set; }
        public TimeSpan After { get; set; }
        public IList<string> Channels { get; set; }
        public IList<string> SelectedPnPInstanceIds { get; set; }
        public string HubMac { get; set; }
        public string HubIp { get; set; }
        // Only a user-configured local file may be copied. No DS600 firmware-log
        // or SX service-log endpoint is assumed to exist.
        public string ExplicitSxLogFile { get; set; }
    }

    public sealed class EventEvidenceRecord
    {
        public string Channel { get; set; }
        public string Provider { get; set; }
        public int EventId { get; set; }
        public long? RecordId { get; set; }
        public DateTime TimeCreatedUtc { get; set; }
        public string OriginalXml { get; set; }
    }

    public interface IEventEvidenceSource
    {
        // Return at most maxRecords + 1 events; the extra item proves that the
        // result is limited. The exporter still checks timestamps and limits.
        IEnumerable<EventEvidenceRecord> Read(string channel, DateTime fromUtc,
            DateTime toUtc, int maxRecords);
    }

    public sealed class WindowsEventEvidenceSource : IEventEvidenceSource
    {
        public IEnumerable<EventEvidenceRecord> Read(string channel, DateTime fromUtc,
            DateTime toUtc, int maxRecords)
        {
            using (var configuration = new EventLogConfiguration(channel))
            {
                if (!configuration.IsEnabled)
                    throw new InvalidOperationException("The selected Windows event channel is disabled: " + channel);
            }
            string from = fromUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            string to = toUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            string query = "*[System[TimeCreated[@SystemTime >= '" + from +
                "' and @SystemTime <= '" + to + "']]]";
            var result = new List<EventEvidenceRecord>();
            using (var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, query)))
            {
                for (int i = 0; i <= maxRecords; i++)
                {
                    using (EventRecord record = reader.ReadEvent())
                    {
                        if (record == null) break;
                        result.Add(new EventEvidenceRecord
                        {
                            Channel = channel,
                            Provider = record.ProviderName,
                            EventId = record.Id,
                            RecordId = record.RecordId,
                            TimeCreatedUtc = record.TimeCreated.HasValue
                                ? record.TimeCreated.Value.ToUniversalTime() : DateTime.MinValue,
                            // Store this exact XML string, not a formatted Message
                            // (which may be unavailable in another locale).
                            OriginalXml = record.ToXml()
                        });
                    }
                }
            }
            return result;
        }
    }

    public sealed class EventEvidenceResult
    {
        public string DirectoryPath { get; internal set; }
        public int WindowsEventCount { get; internal set; }
        // This says only that the requested time interval has elapsed. A
        // denied, disabled or truncated channel still makes evidence limited.
        public bool WindowElapsed { get; internal set; }
        public bool Limited { get; internal set; }
        public bool AllChannelsRead { get { return !Limited; } }
        public bool ExplicitSxLogCopied { get; internal set; }
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// An incident bundle is temporal evidence, not a root-cause determination.
    /// The fixed channel allowlist prevents exporting unrelated Security logs.
    /// </summary>
    public static class EventEvidenceExporter
    {
        static readonly string[] AllowedChannels =
        {
            "System",
            "Microsoft-Windows-Kernel-PnP/Configuration",
            "Microsoft-Windows-DeviceSetupManager/Admin",
            "Microsoft-Windows-DeviceSetupManager/Operational",
            "Microsoft-Windows-NetworkProfile/Operational"
        };
        public static string[] DefaultChannels { get { return (string[])AllowedChannels.Clone(); } }
        const int MaxRecordsPerChannel = 200;
        const long MaxSxFileBytes = 32L * 1024 * 1024;

        public static EventEvidenceResult Capture(string parentDirectory,
            IncidentEvidenceRequest request, IEventEvidenceSource source = null,
            Func<DateTime> utcNow = null)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (String.IsNullOrWhiteSpace(parentDirectory)) throw new ArgumentException("An evidence directory is required.", "parentDirectory");
            if (request.IncidentId == Guid.Empty || request.IncidentUtc.Kind != DateTimeKind.Utc ||
                request.Before < TimeSpan.Zero || request.After < TimeSpan.Zero ||
                request.Before > TimeSpan.FromMinutes(10) || request.After > TimeSpan.FromMinutes(10))
                throw new ArgumentException("Invalid incident identity or UTC time window.", "request");
            string[] channels = (request.Channels == null || request.Channels.Count == 0
                ? AllowedChannels : request.Channels).ToArray();
            if (channels.Length > AllowedChannels.Length || channels.Distinct(StringComparer.Ordinal).Count() != channels.Length ||
                channels.Any(c => !AllowedChannels.Contains(c, StringComparer.Ordinal)))
                throw new ArgumentException("An event channel is outside the diagnostic allowlist.", "request");
            source = source ?? new WindowsEventEvidenceSource();
            utcNow = utcNow ?? (() => DateTime.UtcNow);
            DateTime fromUtc = request.IncidentUtc - request.Before;
            DateTime toUtc = request.IncidentUtc + request.After;
            var result = new EventEvidenceResult { WindowElapsed = utcNow().ToUniversalTime() >= toUtc };
            Directory.CreateDirectory(parentDirectory);
            string directory = Path.Combine(Path.GetFullPath(parentDirectory),
                "incident-" + request.IncidentUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) +
                "-" + request.IncidentId.ToString("N"));
            if (Directory.Exists(directory)) throw new IOException("The incident evidence directory already exists.");
            Directory.CreateDirectory(directory);
            result.DirectoryPath = directory;
            using (var log = new SessionLog(directory, "evidence-" + request.IncidentId.ToString("N"),
                16L * 1024 * 1024, 4))
            {
                foreach (string channel in channels)
                {
                    int count = 0;
                    try
                    {
                        IEnumerable<EventEvidenceRecord> records = source.Read(channel, fromUtc, toUtc, MaxRecordsPerChannel);
                        if (records == null) throw new InvalidDataException("The event source returned no result.");
                        foreach (EventEvidenceRecord record in records)
                        {
                            if (record == null || record.OriginalXml == null ||
                                record.TimeCreatedUtc.Kind != DateTimeKind.Utc ||
                                record.TimeCreatedUtc < fromUtc || record.TimeCreatedUtc > toUtc)
                                throw new InvalidDataException("An event record is incomplete or outside the incident window.");
                            if (++count > MaxRecordsPerChannel)
                            {
                                result.Limited = true;
                                result.Warnings.Add(channel + ": event-count limit reached; this is not a complete export.");
                                break;
                            }
                            log.Append("windows_event", "evidence", new Dictionary<string, string>
                            {
                                { "channel", channel }, { "provider", record.Provider ?? String.Empty },
                                { "eventId", record.EventId.ToString(CultureInfo.InvariantCulture) },
                                { "recordId", record.RecordId.HasValue ? record.RecordId.Value.ToString(CultureInfo.InvariantCulture) : String.Empty },
                                { "relation", Relation(record.OriginalXml, request) },
                                { "causality", "unproven" }, { "originalEventRecordXml", record.OriginalXml }
                            }, record.TimeCreatedUtc);
                            result.WindowsEventCount++;
                        }
                        log.Append("source_status", "info", new Dictionary<string, string>
                        {
                            { "channel", channel }, { "exportedRecords", Math.Min(count, MaxRecordsPerChannel).ToString(CultureInfo.InvariantCulture) },
                            { "complete", (count <= MaxRecordsPerChannel).ToString() }
                        }, utcNow().ToUniversalTime(), true);
                    }
                    catch (Exception error)
                    {
                        result.Limited = true;
                        result.Warnings.Add(channel + ": " + SafeError(error));
                        // A channel access or parser failure must not masquerade
                        // as a healthy no-event interval.
                        try
                        {
                            log.Append("source_error", "warning", new Dictionary<string, string>
                            {
                                { "channel", channel }, { "error", SafeError(error) }
                            }, utcNow().ToUniversalTime(), true);
                        }
                        catch (IOException)
                        {
                            result.Limited = true;
                            break;
                        }
                    }
                }
                log.Flush();
            }
            if (!String.IsNullOrWhiteSpace(request.ExplicitSxLogFile))
            {
                try { CopyExplicitSxLog(request.ExplicitSxLogFile, directory); result.ExplicitSxLogCopied = true; }
                catch (Exception error) { result.Warnings.Add("Explicit SX file: " + SafeError(error)); }
            }
            WriteSummary(directory, request, fromUtc, toUtc, result);
            return result;
        }

        static string Relation(string xml, IncidentEvidenceRequest request)
        {
            if (request.SelectedPnPInstanceIds != null && request.SelectedPnPInstanceIds.Any(id =>
                !String.IsNullOrWhiteSpace(id) &&
                (xml.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 xml.IndexOf(SecurityElement.Escape(id), StringComparison.OrdinalIgnoreCase) >= 0)))
                return "selected-pnp-instance-text-match-only";
            if (!String.IsNullOrWhiteSpace(request.HubMac) &&
                xml.IndexOf(request.HubMac, StringComparison.OrdinalIgnoreCase) >= 0)
                return "hub-mac-text-match-only";
            if (!String.IsNullOrWhiteSpace(request.HubIp) &&
                xml.IndexOf(request.HubIp, StringComparison.OrdinalIgnoreCase) >= 0)
                return "hub-ip-text-match-only";
            if (xml.IndexOf("SX Virtual Link Lite", StringComparison.OrdinalIgnoreCase) >= 0)
                return "sx-service-name-text-match-only";
            return "time-window-only";
        }

        static void CopyExplicitSxLog(string sourcePath, string directory)
        {
            if (!Path.IsPathRooted(sourcePath)) throw new IOException("An absolute user-selected SX file path is required.");
            var source = new FileInfo(Path.GetFullPath(sourcePath));
            if (!source.Exists || (source.Attributes & FileAttributes.ReparsePoint) != 0 ||
                source.Length > MaxSxFileBytes)
                throw new IOException("The explicit SX log is absent, linked, or too large.");
            string target = Path.Combine(directory, "explicit-sx-file.log");
            string temporary = Path.Combine(directory, "explicit-sx-file.partial");
            try
            {
                using (var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] buffer = new byte[64 * 1024]; long copied = 0; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copied += count;
                        if (copied > MaxSxFileBytes) throw new IOException("The explicit SX file grew beyond the limit while being copied.");
                        output.Write(buffer, 0, count);
                    }
                    output.Flush(true);
                }
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        static string SafeError(Exception error)
        {
            string message = error.GetType().Name + ": " + error.Message;
            return message.Length > 2048 ? message.Substring(0, 2048) : message;
        }

        static void WriteSummary(string directory, IncidentEvidenceRequest request, DateTime fromUtc,
            DateTime toUtc, EventEvidenceResult result)
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("schema", "1"),
                new KeyValuePair<string, string>("incidentId", request.IncidentId.ToString("D")),
                new KeyValuePair<string, string>("fromUtc", fromUtc.ToString("O")),
                new KeyValuePair<string, string>("toUtc", toUtc.ToString("O")),
                new KeyValuePair<string, string>("windowElapsed", result.WindowElapsed.ToString()),
                new KeyValuePair<string, string>("windowsEventCount", result.WindowsEventCount.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("limited", result.Limited.ToString()),
                new KeyValuePair<string, string>("allChannelsRead", result.AllChannelsRead.ToString()),
                new KeyValuePair<string, string>("hubMac", request.HubMac ?? String.Empty),
                new KeyValuePair<string, string>("hubIp", request.HubIp ?? String.Empty),
                new KeyValuePair<string, string>("selectedPnPInstanceIds", request.SelectedPnPInstanceIds == null ?
                    String.Empty : String.Join(";", request.SelectedPnPInstanceIds)),
                new KeyValuePair<string, string>("explicitSxLogCopied", result.ExplicitSxLogCopied.ToString()),
                new KeyValuePair<string, string>("ds600FirmwareLog", "Unavailable: no verified device-log API configured"),
                new KeyValuePair<string, string>("warnings", String.Join(" | ", result.Warnings))
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonLine.Encode(fields) + "\n");
            using (var stream = new FileStream(Path.Combine(directory, "evidence-summary.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }
    }
}
