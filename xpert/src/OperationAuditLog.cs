using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Xiongxin.OWTS.Desktop
{
    internal enum AuditLogKind { Project, Operation }
    internal enum AuditModule { None, Home, ProjectManagement, ProjectCreation, DataBrowser, DataAnalysis, ReportGenerator, PressureTest, CalibrationTest, WithstandTest, Settings }
    internal enum ProjectAuditAction { ProjectCreated, ProjectUpdated }
    internal enum OperationAuditAction
    {
        ApplicationStart, ApplicationExit, ModuleOpen, ReturnHome, SettingsOpened, SettingsSaved, SettingsSaveFailed,
        AuthorizationAccepted, AuthorizationRejected, DeviceCheckRequested, DeviceCheckCompleted, DeviceCheckFailed,
        ProjectLoaded, ProjectLoadFailed, PressureRequested, CalibrationRequested, WithstandRequested, WithstandStopped, AnalysisSaveRequested, AnalysisSaved, AnalysisSettingsConfirmed, ReportGenerated, CalibrationSaved,
        DisplayAutomatic, DisplayManual, StatisticsFilterOn, StatisticsFilterOff, ClassificationOn, ClassificationOff
    }
    internal enum AuditOutcome { Requested, Succeeded, Failed, Cancelled, Simulation, Unknown, Partial }

    internal sealed class OperationAuditOptions
    {
        internal long MaxFileBytes = 1024 * 1024;
        internal int MaxFilesPerKind = 30;
        internal long MaxQueryBytes = 8 * 1024 * 1024;
        internal int MaxQueryLines = 30000;
        internal int MaxQueueEntries = 256;
        internal OperationAuditOptions CopyValidated()
        {
            if (MaxFileBytes < 1024 || MaxFileBytes > 8 * 1024 * 1024 || MaxFilesPerKind < 2 || MaxFilesPerKind > 90 ||
                MaxQueryBytes < 1024 || MaxQueryBytes > 32 * 1024 * 1024 || MaxQueryLines < 1 || MaxQueryLines > 100000 || MaxQueueEntries < 1 || MaxQueueEntries > 4096)
                throw new ArgumentOutOfRangeException("options");
            return (OperationAuditOptions)MemberwiseClone();
        }
    }
    internal sealed class OperationAuditEntry
    {
        internal string EventId { get; set; }
        internal DateTimeOffset TimeUtc { get; set; }
        internal AuditLogKind Kind { get; set; }
        internal string Action { get; set; }
        internal AuditModule Module { get; set; }
        internal AuditOutcome Outcome { get; set; }
        internal string ProjectPath { get; set; }
        internal bool ProjectPathOmitted { get; set; }
        internal string Description { get; set; }
    }
    internal sealed class OperationAuditQueryResult
    {
        internal ReadOnlyCollection<OperationAuditEntry> Entries { get; set; }
        internal int InvalidLines { get; set; }
        internal int PartialLines { get; set; }
        internal bool IsLimited { get; set; }
        internal string ReadFailureCode { get; set; }
        internal string LastWriteFailureCode { get; set; }
        internal DateTimeOffset? LastWriteFailureUtc { get; set; }
        internal int PendingEntries { get; set; }
    }

    // No free-form message, exception, credential or control-value API.
    // Descriptions come exclusively from the closed action set.
    internal sealed class OperationAuditLog
    {
        const string Schema = "hvx-operation-audit-v1", OwnerMarker = "hvx-audit-store-v1";
        const int MaxLineBytes = 8192;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly Regex FileName = new Regex(@"^(project|operation)_\d{8}_\d{4}\.jsonl$", RegexOptions.CultureInvariant);
        static readonly Regex SensitivePath = new Regex(@"(?i)(password|passwd|pwd|token|secret|credential|authorization|bearer|api[-_]?key|[?&#=])", RegexOptions.CultureInvariant);
        static readonly Lazy<OperationAuditLog> DefaultStore = new Lazy<OperationAuditLog>(() => new OperationAuditLog(Path.Combine(AppPaths.LocalDataDirectory, "Logs")));
        readonly object stateGate = new object();
        readonly object queueGate = new object();
        readonly Queue<PendingEntry> queue = new Queue<PendingEntry>();
        Thread writer;
        bool activeWrite, closing;
        readonly string root, mutexName;
        readonly OperationAuditOptions options;
        string lastWriteFailureCode = "";
        DateTimeOffset? lastWriteFailureUtc;
        internal static OperationAuditLog Default { get { return DefaultStore.Value; } }
        internal string DirectoryPath { get { return root; } }
        internal string LastWriteFailureCode { get { lock (stateGate) return lastWriteFailureCode; } }
        internal DateTimeOffset? LastWriteFailureUtc { get { lock (stateGate) return lastWriteFailureUtc; } }
        internal int PendingEntries { get { lock (queueGate) return queue.Count + (activeWrite ? 1 : 0); } }
        sealed class PendingEntry
        {
            internal AuditLogKind Kind; internal string Action, ProjectPath, EventId;
            internal AuditOutcome Outcome; internal AuditModule Module;
            internal bool PathOmitted; internal DateTimeOffset TimeUtc;
        }

        internal OperationAuditLog(string directory, OperationAuditOptions limits = null)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("An application-owned log directory is required.", "directory");
            root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(root, Path.GetPathRoot(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A drive root is not an audit directory.");
            options = (limits ?? new OperationAuditOptions()).CopyValidated();
            using (var sha = SHA256.Create()) mutexName = @"Local\HVX.Audit." + BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(root.ToUpperInvariant()))).Replace("-", "");
        }
        internal bool TryWriteProject(ProjectAuditAction action, AuditOutcome outcome, string projectPath = null)
        {
            return Enum.IsDefined(typeof(ProjectAuditAction), action) ? TryWrite(AuditLogKind.Project, action.ToString(), outcome, projectPath, AuditModule.None) : FailWrite("invalid_action");
        }
        internal bool TryWriteOperation(OperationAuditAction action, AuditOutcome outcome, string projectPath = null, AuditModule module = AuditModule.None)
        {
            return Enum.IsDefined(typeof(OperationAuditAction), action) ? TryWrite(AuditLogKind.Operation, action.ToString(), outcome, projectPath, module) : FailWrite("invalid_action");
        }
        // True means accepted into the bounded queue, not persisted. Existing
        // TryWrite methods retain their synchronous durable-write semantics.
        internal bool EnqueueProject(ProjectAuditAction action, AuditOutcome outcome, string projectPath = null)
        {
            return Enum.IsDefined(typeof(ProjectAuditAction), action) ? Enqueue(AuditLogKind.Project, action.ToString(), outcome, projectPath, AuditModule.None) : FailWrite("invalid_action");
        }
        internal bool EnqueueOperation(OperationAuditAction action, AuditOutcome outcome, string projectPath = null, AuditModule module = AuditModule.None)
        {
            return Enum.IsDefined(typeof(OperationAuditAction), action) ? Enqueue(AuditLogKind.Operation, action.ToString(), outcome, projectPath, module) : FailWrite("invalid_action");
        }
        PendingEntry Entry(AuditLogKind kind, string action, AuditOutcome outcome, string path, AuditModule module)
        {
            string safePath = SafeProjectPath(path);
            return new PendingEntry { Kind = kind, Action = action, Outcome = outcome, Module = module, ProjectPath = safePath,
                PathOmitted = !string.IsNullOrWhiteSpace(path) && safePath.Length == 0, TimeUtc = DateTimeOffset.UtcNow, EventId = Guid.NewGuid().ToString("N") };
        }
        bool Enqueue(AuditLogKind kind, string action, AuditOutcome outcome, string path, AuditModule module)
        {
            if (!Enum.IsDefined(typeof(AuditOutcome), outcome)) return FailWrite("invalid_outcome");
            if (!Enum.IsDefined(typeof(AuditModule), module)) return FailWrite("invalid_module");
            try
            {
                lock (queueGate)
                {
                    if (closing) return FailWrite("queue_closed");
                    if (queue.Count >= options.MaxQueueEntries) return FailWrite("queue_full");
                    var entry = Entry(kind, action, outcome, path, module);
                    if (writer == null)
                    {
                        writer = new Thread(WriteQueued) { IsBackground = true, Name = "HVX local audit writer" };
                        try { writer.Start(); } catch { writer = null; return FailWrite("worker_unavailable"); }
                    }
                    queue.Enqueue(entry); Monitor.PulseAll(queueGate); return true;
                }
            }
            catch (Exception) { return FailWrite("queue_unavailable"); }
        }
        void WriteQueued()
        {
            try
            {
                while (true)
                {
                    PendingEntry entry;
                    lock (queueGate)
                    {
                        while (queue.Count == 0 && !closing) Monitor.Wait(queueGate);
                        if (queue.Count == 0 && closing) return;
                        entry = queue.Dequeue(); activeWrite = true;
                    }
                    try { WriteEntry(entry); } catch (Exception) { FailWrite("worker_unavailable"); }
                    finally { lock (queueGate) { activeWrite = false; Monitor.PulseAll(queueGate); } }
                }
            }
            finally { lock (queueGate) { writer = null; activeWrite = false; Monitor.PulseAll(queueGate); } }
        }
        internal bool Flush(TimeSpan budget)
        {
            if (budget < TimeSpan.Zero || budget > TimeSpan.FromSeconds(5)) return FailWrite("invalid_flush_budget");
            var clock = Stopwatch.StartNew();
            lock (queueGate)
            {
                while (queue.Count > 0 || activeWrite)
                {
                    int remaining = (int)Math.Ceiling((budget - clock.Elapsed).TotalMilliseconds);
                    if (remaining <= 0) return FailWrite("flush_timeout");
                    Monitor.Wait(queueGate, remaining);
                }
                return true;
            }
        }
        internal bool Shutdown(TimeSpan budget)
        {
            lock (queueGate) { closing = true; Monitor.PulseAll(queueGate); }
            return Flush(budget);
        }
        bool TryWrite(AuditLogKind kind, string action, AuditOutcome outcome, string projectPath, AuditModule module)
        {
            if (!Enum.IsDefined(typeof(AuditOutcome), outcome)) return FailWrite("invalid_outcome");
            if (!Enum.IsDefined(typeof(AuditModule), module)) return FailWrite("invalid_module");
            return WriteEntry(Entry(kind, action, outcome, projectPath, module));
        }
        bool WriteEntry(PendingEntry entry)
        {
            try
            {
                DateTimeOffset now = entry.TimeUtc; AuditLogKind kind = entry.Kind; string action = entry.Action;
                var record = new Dictionary<string, object> {
                    { "schema", Schema }, { "eventId", entry.EventId },
                    { "timeUtc", now.ToString("o", CultureInfo.InvariantCulture) },
                    { "type", kind.ToString() }, { "action", action }, { "module", entry.Module.ToString() }, { "result", entry.Outcome.ToString() },
                    { "description", Description(kind, action) }, { "projectPath", entry.ProjectPath },
                    { "projectPathOmitted", entry.PathOmitted }
                };
                byte[] line = Utf8.GetBytes(new JavaScriptSerializer().Serialize(record) + "\n");
                if (line.Length > MaxLineBytes || line.Length + 1 > options.MaxFileBytes) return FailWrite("entry_too_large");
                using (var mutex = new Mutex(false, mutexName))
                {
                    bool held = false;
                    try
                    {
                        try { held = mutex.WaitOne(250); } catch (AbandonedMutexException) { held = true; }
                        if (!held) return FailWrite("write_busy");
                        EnsureOwnedDirectory(true);
                        string prefix = Prefix(kind), day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                        var files = OwnedFiles(kind);
                        string current = files.Where(file => Path.GetFileName(file).StartsWith(prefix + "_" + day + "_", StringComparison.Ordinal)).OrderByDescending(file => file, StringComparer.Ordinal).FirstOrDefault();
                        if (current == null || new FileInfo(current).Length + line.Length + 1 > options.MaxFileBytes)
                        {
                            int sequence = current == null ? 0 : int.Parse(Path.GetFileNameWithoutExtension(current).Substring(prefix.Length + 10), CultureInfo.InvariantCulture) + 1;
                            if (sequence > 9999) return FailWrite("rotation_limit");
                            current = Path.Combine(root, prefix + "_" + day + "_" + sequence.ToString("0000", CultureInfo.InvariantCulture) + ".jsonl");
                            using (new FileStream(current, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                            files.Add(current);
                        }
                        // Retention applies only to exact owned log filenames.
                        foreach (string expired in files.OrderByDescending(file => file, StringComparer.Ordinal).Skip(options.MaxFilesPerKind))
                        { RejectRedirects(expired); File.Delete(expired); }
                        RejectRedirects(current);
                        using (var stream = new FileStream(current, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        {
                            // Keep a crashed partial line, but separate the next complete entry.
                            if (stream.Length > 0) { stream.Position = stream.Length - 1; if (stream.ReadByte() != '\n') { stream.Position = stream.Length; stream.WriteByte((byte)'\n'); } }
                            stream.Position = stream.Length; stream.Write(line, 0, line.Length); stream.Flush(true);
                        }
                        return true;
                    }
                    finally { if (held) mutex.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { return FailWrite(FailureCode(ex)); }
        }
        internal OperationAuditQueryResult Query(AuditLogKind kind, DateTimeOffset? from = null, DateTimeOffset? to = null, string keyword = null, int maxRows = 500, CancellationToken cancellation = default(CancellationToken))
        {
            var result = new OperationAuditQueryResult { Entries = new List<OperationAuditEntry>().AsReadOnly(), ReadFailureCode = "", LastWriteFailureCode = LastWriteFailureCode, LastWriteFailureUtc = LastWriteFailureUtc, PendingEntries = PendingEntries };
            var entries = new List<OperationAuditEntry>();
            if (!Enum.IsDefined(typeof(AuditLogKind), kind) || maxRows < 1 || maxRows > 2000 || (from.HasValue && to.HasValue && from.Value > to.Value) || (keyword ?? "").Length > 128)
            { result.ReadFailureCode = "invalid_query"; return result; }
            try
            {
                if (!Directory.Exists(root)) return result;
                EnsureOwnedDirectory(false);
                long remaining = options.MaxQueryBytes; int lines = 0;
                foreach (string file in OwnedFiles(kind).OrderByDescending(path => path, StringComparer.Ordinal))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (remaining <= 0 || lines >= options.MaxQueryLines) { result.IsLimited = true; break; }
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long length = stream.Length, take = Math.Min(stream.Length, remaining);
                        bool skipFirst = take < length;
                        if (skipFirst) { stream.Position = length - take; result.IsLimited = true; }
                        byte[] buffer = new byte[8192]; var line = new List<byte>(512); bool tooLong = false;
                        while (take > 0 && lines < options.MaxQueryLines)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, take));
                            if (read <= 0) break; take -= read; remaining -= read;
                            for (int i = 0; i < read; i++)
                            {
                                byte value = buffer[i];
                                if (value != '\n') { if (!skipFirst && !tooLong) { if (line.Count >= MaxLineBytes) { tooLong = true; line.Clear(); } else line.Add(value); } continue; }
                                if (skipFirst) { skipFirst = false; line.Clear(); continue; }
                                lines++; OperationAuditEntry entry = tooLong ? null : Parse(line.ToArray(), kind);
                                if (entry == null) result.InvalidLines++;
                                else if ((!from.HasValue || entry.TimeUtc >= from.Value) && (!to.HasValue || entry.TimeUtc <= to.Value) && Matches(entry, keyword)) entries.Add(entry);
                                line.Clear(); tooLong = false;
                                if (entries.Count > maxRows * 2) { result.IsLimited = true; entries = entries.OrderByDescending(e => e.TimeUtc).ThenByDescending(e => e.EventId, StringComparer.Ordinal).Take(maxRows).ToList(); }
                                if (lines >= options.MaxQueryLines) { result.IsLimited = true; break; }
                            }
                        }
                        if (!skipFirst && (line.Count > 0 || tooLong)) result.PartialLines++;
                    }
                }
                if (entries.Count > maxRows) result.IsLimited = true;
                result.Entries = entries.OrderByDescending(e => e.TimeUtc).ThenByDescending(e => e.EventId, StringComparer.Ordinal).Take(maxRows).ToList().AsReadOnly();
            }
            catch (OperationCanceledException) { result.ReadFailureCode = "cancelled"; }
            catch (Exception ex) { result.ReadFailureCode = FailureCode(ex); }
            return result;
        }
        List<string> OwnedFiles(AuditLogKind kind)
        {
            var result = new List<string>(); int inspected = 0;
            foreach (string path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (++inspected > 200) throw new IOException("Log file count exceeds bounded scan.");
                string name = Path.GetFileName(path);
                if (!FileName.IsMatch(name) || !name.StartsWith(Prefix(kind) + "_", StringComparison.Ordinal)) continue;
                RejectRedirects(path); result.Add(path);
            }
            return result;
        }
        void EnsureOwnedDirectory(bool create)
        {
            RejectRedirects(root);
            if (!Directory.Exists(root)) { if (!create) return; Directory.CreateDirectory(root); }
            string marker = Path.Combine(root, ".hvx-audit-owner");
            if (!File.Exists(marker))
            {
                if (!create || Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Not an owned audit directory.");
                using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { byte[] bytes = Utf8.GetBytes(OwnerMarker); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            }
            RejectRedirects(marker);
            if (new FileInfo(marker).Length != Utf8.GetByteCount(OwnerMarker) || File.ReadAllText(marker, Utf8) != OwnerMarker) throw new IOException("Audit owner marker is invalid.");
        }
        static void RejectRedirects(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected audit path is not allowed.");
                current = Path.GetDirectoryName(current);
            }
        }
        static string SafeProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (path.Length > 768 || path.Any(char.IsControl) || SensitivePath.IsMatch(path) || !Path.IsPathRooted(path) || path.IndexOf("://", StringComparison.Ordinal) >= 0) return "";
            try { string full = Path.GetFullPath(path); return full.Length <= 768 ? full : ""; } catch { return ""; }
        }
        static string Prefix(AuditLogKind kind) { return kind == AuditLogKind.Project ? "project" : "operation"; }
        bool FailWrite(string code) { lock (stateGate) { lastWriteFailureCode = code; lastWriteFailureUtc = DateTimeOffset.UtcNow; } return false; }
        static string FailureCode(Exception ex) { return ex is UnauthorizedAccessException ? "access_denied" : ex is IOException ? "storage_unavailable" : ex is ArgumentException ? "invalid_path" : "audit_unavailable"; }
        internal static string Description(AuditLogKind kind, string action)
        {
            switch (action)
            {
                case "ProjectCreated": return "Project creation";
                case "ProjectUpdated": return "Project update";
                case "ApplicationStart": return "Application started";
                case "ApplicationExit": return "Application exited";
                case "ModuleOpen": return "Module opened";
                case "ReturnHome": return "Returned to home";
                case "SettingsOpened": return "Settings opened";
                case "SettingsSaved": return "Settings saved";
                case "SettingsSaveFailed": return "Settings save failed";
                case "AuthorizationAccepted": return "Protected access accepted";
                case "AuthorizationRejected": return "Protected access rejected";
                case "DeviceCheckRequested": return "Device check requested";
                case "DeviceCheckCompleted": return "Device check completed";
                case "DeviceCheckFailed": return "Device check failed";
                case "ProjectLoaded": return "Project loaded";
                case "ProjectLoadFailed": return "Project load failed";
                case "PressureRequested": return "Voltage test requested";
                case "CalibrationRequested": return "Calibration requested";
                case "CalibrationSaved": return "Calibration saved";
                case "WithstandRequested": return "Withstand test requested";
                case "WithstandStopped": return "Withstand test stopped";
                case "AnalysisSaveRequested": return "Analysis save requested";
                case "AnalysisSaved": return "Analysis saved";
                case "AnalysisSettingsConfirmed": return "Analysis settings confirmed";
                case "ReportGenerated": return "Report generation completed";
                case "DisplayAutomatic": return "Display options set automatically";
                case "DisplayManual": return "Display options set manually";
                case "StatisticsFilterOn": return "Statistics filtering enabled";
                case "StatisticsFilterOff": return "Statistics filtering disabled";
                case "ClassificationOn": return "Classification enabled";
                case "ClassificationOff": return "Classification disabled";
                default: return "";
            }
        }
        static OperationAuditEntry Parse(byte[] bytes, AuditLogKind kind)
        {
            try
            {
                var data = new JavaScriptSerializer { MaxJsonLength = MaxLineBytes, RecursionLimit = 5 }.DeserializeObject(Utf8.GetString(bytes)) as Dictionary<string, object>;
                if (data == null || Value(data, "schema") != Schema || Value(data, "type") != kind.ToString()) return null;
                string action = Value(data, "action");
                bool valid = kind == AuditLogKind.Project ? Enum.GetNames(typeof(ProjectAuditAction)).Contains(action) : Enum.GetNames(typeof(OperationAuditAction)).Contains(action);
                AuditOutcome outcome; AuditModule module; DateTimeOffset time; Guid id;
                string moduleText = Value(data, "module"); if (moduleText.Length == 0) moduleText = "None";
                if (!valid || !Enum.TryParse(Value(data, "result"), out outcome) || !Enum.IsDefined(typeof(AuditOutcome), outcome) ||
                    !Enum.TryParse(moduleText, out module) || !Enum.IsDefined(typeof(AuditModule), module) ||
                    !DateTimeOffset.TryParseExact(Value(data, "timeUtc"), "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out time) || time.Offset != TimeSpan.Zero ||
                    !Guid.TryParseExact(Value(data, "eventId"), "N", out id)) return null;
                string raw = Value(data, "projectPath"), path = SafeProjectPath(raw); object omitted;
                return new OperationAuditEntry { EventId = id.ToString("N"), Kind = kind, TimeUtc = time, Action = action, Module = module, Outcome = outcome,
                    ProjectPath = path, ProjectPathOmitted = (raw.Length > 0 && path.Length == 0) || (data.TryGetValue("projectPathOmitted", out omitted) && omitted is bool && (bool)omitted), Description = Description(kind, action) };
            }
            catch { return null; }
        }
        static string Value(Dictionary<string, object> values, string key) { object value; return values.TryGetValue(key, out value) ? value as string ?? "" : ""; }
        static bool Matches(OperationAuditEntry entry, string keyword)
        {
            return string.IsNullOrWhiteSpace(keyword) || (entry.Action + " " + entry.Module + " " + entry.Outcome + " " + entry.Description + " " + entry.ProjectPath).IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
