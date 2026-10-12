using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.HardwareLinkStress
{
    public enum LinkTransitionKind
    {
        Disconnect,
        ReconnectAttempt,
        IdentityMismatch,
        LeaseConflict,
        ResumeNewCycle,
        GiveUp
    }

    /// <summary>
    /// Append-only diagnostic JSONL. The caller records counters and errors, not raw samples.
    /// Old segments are never deleted: reaching the bounded capacity is a test-stopping error.
    /// </summary>
    public sealed class SessionLog : IDisposable
    {
        readonly object sync = new object();
        readonly string directory;
        readonly string sessionId;
        readonly long maxSegmentBytes;
        readonly int maxSegments;
        FileStream stream;
        int segment;
        long bytes;
        bool disposed;

        public SessionLog(string directory, string sessionId, long maxSegmentBytes = 8 * 1024 * 1024, int maxSegments = 32)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A diagnostic directory is required.", "directory");
            if (String.IsNullOrWhiteSpace(sessionId) || !Regex.IsMatch(sessionId, "^[A-Za-z0-9_-]{1,64}$"))
                throw new ArgumentException("Invalid session identifier.", "sessionId");
            if (maxSegmentBytes < 1024 || maxSegmentBytes > 64L * 1024 * 1024 || maxSegments < 1 || maxSegments > 256)
                throw new ArgumentOutOfRangeException("Log capacity is outside supported bounds.");
            this.directory = Path.GetFullPath(directory);
            this.sessionId = sessionId;
            this.maxSegmentBytes = maxSegmentBytes;
            this.maxSegments = maxSegments;
            Directory.CreateDirectory(this.directory);
            OpenNextSegment();
        }

        public string CurrentPath { get { lock (sync) { return stream.Name; } } }

        /// <summary>
        /// Records a recovery transition only. ResumeNewCycle says that a new
        /// acquisition cycle was started; it does not claim the old data block
        /// is continuous, complete, or that the root cause was determined.
        /// </summary>
        public void AppendLinkTransition(LinkTransitionKind kind,
            IDictionary<string, string> fields, DateTime utc)
        {
            string label;
            switch (kind)
            {
                case LinkTransitionKind.Disconnect: label = "disconnect"; break;
                case LinkTransitionKind.ReconnectAttempt: label = "reconnect_attempt"; break;
                case LinkTransitionKind.IdentityMismatch: label = "identity_mismatch"; break;
                case LinkTransitionKind.LeaseConflict: label = "lease_conflict"; break;
                case LinkTransitionKind.ResumeNewCycle: label = "resume_new_cycle"; break;
                case LinkTransitionKind.GiveUp: label = "give_up"; break;
                default: throw new ArgumentOutOfRangeException("kind");
            }
            if (kind == LinkTransitionKind.ResumeNewCycle &&
                (fields == null || !fields.ContainsKey("newCycleId") ||
                 String.IsNullOrWhiteSpace(fields["newCycleId"])))
                throw new ArgumentException("A resumed acquisition requires a distinct newCycleId.", "fields");
            Append(label, kind == LinkTransitionKind.ResumeNewCycle ? "info" : "warning",
                fields, utc, true);
        }

        public void Append(string kind, string severity, IDictionary<string, string> fields,
            DateTime utc, bool durable = false)
        {
            if (String.IsNullOrWhiteSpace(kind) || kind.Length > 64 ||
                String.IsNullOrWhiteSpace(severity) || severity.Length > 32)
                throw new ArgumentException("Invalid diagnostic event type or severity.");
            var values = new List<KeyValuePair<string, string>>();
            values.Add(new KeyValuePair<string, string>("schema", "1"));
            values.Add(new KeyValuePair<string, string>("sessionId", sessionId));
            values.Add(new KeyValuePair<string, string>("utc", utc.ToUniversalTime().ToString("O")));
            values.Add(new KeyValuePair<string, string>("kind", kind));
            values.Add(new KeyValuePair<string, string>("severity", severity));
            if (fields != null)
            {
                if (fields.Count > 64) throw new ArgumentException("Too many diagnostic fields.", "fields");
                foreach (var field in fields)
                {
                    if (String.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 128 ||
                        field.Key == "schema" || field.Key == "sessionId" || field.Key == "utc" ||
                        field.Key == "kind" || field.Key == "severity" ||
                        (field.Value ?? String.Empty).Length > 1024 * 1024)
                        throw new ArgumentException("Invalid diagnostic field.", "fields");
                    values.Add(new KeyValuePair<string, string>(field.Key, field.Value ?? String.Empty));
                }
            }
            byte[] line = Encoding.UTF8.GetBytes(JsonLine.Encode(values) + "\n");
            if (line.LongLength > maxSegmentBytes) throw new IOException("A diagnostic record exceeds the segment capacity.");
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("SessionLog");
                if (bytes + line.LongLength > maxSegmentBytes) OpenNextSegment();
                stream.Write(line, 0, line.Length);
                bytes += line.LongLength;
                if (durable) stream.Flush(true);
            }
        }

        public void Flush()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("SessionLog");
                stream.Flush(true);
            }
        }

        void OpenNextSegment()
        {
            if (segment >= maxSegments) throw new IOException("Diagnostic capacity reached; stop the stress run. Existing logs were retained.");
            int next = segment + 1;
            string path = Path.Combine(directory, "session-" + sessionId + "-" + next.ToString("D4") + ".jsonl");
            // Create the new segment before closing the old one; a collision or
            // permission failure must not discard the current writable segment.
            var nextStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            if (stream != null) { stream.Flush(true); stream.Dispose(); }
            stream = nextStream;
            segment = next;
            bytes = 0;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (stream != null) { stream.Flush(true); stream.Dispose(); }
            }
        }
    }

    internal static class JsonLine
    {
        internal static string Encode(IEnumerable<KeyValuePair<string, string>> values)
        {
            var text = new StringBuilder("{");
            bool first = true;
            foreach (var value in values)
            {
                if (!first) text.Append(',');
                first = false;
                Quote(text, value.Key);
                text.Append(':');
                Quote(text, value.Value);
            }
            return text.Append('}').ToString();
        }

        static void Quote(StringBuilder text, string value)
        {
            text.Append('"');
            foreach (char c in value ?? String.Empty)
            {
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4"));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
    }
}
