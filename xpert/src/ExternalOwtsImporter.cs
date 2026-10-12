using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class ExternalOwtsLimits
    {
        public int MaximumDirectories = 4096, MaximumDepth = 12, MaximumBatches = 512, MaximumFilesPerBatch = 2048;
        public long MaximumFileBytes = 64L * 1024 * 1024, MaximumBatchBytes = 256L * 1024 * 1024;
        public int MaximumSamplesPerRecord = 2000000, MaximumSamplesPerBatch = 8000000;
        internal void Validate()
        {
            if (MaximumDirectories < 1 || MaximumDirectories > 100000 || MaximumDepth < 0 || MaximumDepth > 32 ||
                MaximumBatches < 1 || MaximumBatches > 10000 || MaximumFilesPerBatch < 1 || MaximumFilesPerBatch > 10000 ||
                MaximumFileBytes < 1 || MaximumFileBytes > 256L * 1024 * 1024 || MaximumBatchBytes < 1 || MaximumBatchBytes > 1024L * 1024 * 1024 ||
                MaximumSamplesPerRecord < 1 || MaximumSamplesPerRecord > 8000000 || MaximumSamplesPerBatch < 1 || MaximumSamplesPerBatch > 16000000)
                throw new ArgumentOutOfRangeException("limits", "Import limits are outside the supported safety bounds.");
        }
    }
    internal sealed class ExternalOwtsIssue
    {
        public string Code { get; private set; }
        public string RelativePath { get; private set; }
        public string Detail { get; private set; }
        public bool IsError { get; private set; }
        internal ExternalOwtsIssue(string code, string path, string detail, bool error)
        { Code = code; RelativePath = path; Detail = detail; IsError = error; }
    }
    internal sealed class ExternalOwtsDiscovery
    {
        public ReadOnlyCollection<string> BatchDirectories { get; private set; }
        public ReadOnlyCollection<ExternalOwtsIssue> Issues { get; private set; }
        public bool Complete { get; private set; }
        internal ExternalOwtsDiscovery(List<string> directories, List<ExternalOwtsIssue> issues, bool complete)
        { BatchDirectories = directories.AsReadOnly(); Issues = issues.AsReadOnly(); Complete = complete; }
    }
    internal sealed class ExternalOwtsSource
    {
        public string FullPath { get; private set; }
        public string RelativePath { get; private set; }
        public long Bytes { get; private set; }
        public string Sha256 { get; private set; }
        internal ExternalOwtsSource(string fullPath, long bytes, string hash)
        { FullPath = fullPath; RelativePath = Path.GetFileName(fullPath); Bytes = bytes; Sha256 = hash; }
    }
    internal sealed class ExternalOwtsJoint
    {
        public string Phase { get; private set; }
        public int Sequence { get; private set; }
        public double PositionMetres { get; private set; }
        public string Type { get; private set; }
        internal ExternalOwtsJoint(string phase, int sequence, double position, string type)
        { Phase = phase; Sequence = sequence; PositionMetres = position; Type = type; }
    }
    internal sealed class ExternalOwtsRecord
    {
        public string Id { get; private set; }
        public string Kind { get; private set; }
        public string Status { get; private set; }
        public string Phase { get; private set; }
        public string Reason { get; private set; }
        public double SampleIntervalSeconds { get; private set; }
        public double SampleRateHz { get { return SampleIntervalSeconds > 0 ? 1.0 / SampleIntervalSeconds : double.NaN; } }
        public double TestVoltageKv { get; private set; }
        public string CalibrationRecordId { get; private set; }
        public string ValueUnit { get; private set; }
        public ReadOnlyCollection<double> Values { get; private set; }
        public ReadOnlyCollection<double> VoltageKv { get; private set; }
        public ReadOnlyCollection<ExternalOwtsSource> Sources { get; private set; }
        public ReadOnlyDictionary<string, string> Headers { get; private set; }
        public bool CanBrowseWaveform { get { return Status == "valid" && Kind == "oscillation"; } }
        public bool HasDecodedLocationPulses { get { return false; } }
        internal ExternalOwtsRecord(string id, string kind, string status, string phase, string reason, double dt,
            double voltage, string calibrationId, string unit, double[] values, double[] volts,
            IEnumerable<ExternalOwtsSource> sources, Dictionary<string, string> headers)
        {
            Id = id; Kind = kind; Status = status; Phase = phase; Reason = reason; SampleIntervalSeconds = dt;
            TestVoltageKv = voltage; CalibrationRecordId = calibrationId; ValueUnit = unit;
            Values = Array.AsReadOnly(values ?? new double[0]); VoltageKv = Array.AsReadOnly(volts ?? new double[0]);
            Sources = new List<ExternalOwtsSource>(sources).AsReadOnly();
            Headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
        }
    }
    internal sealed class ExternalOwtsBatch
    {
        public string SourceDirectory { get; private set; }
        public ExternalOwtsSource MetadataSource { get; private set; }
        public string MetadataEncoding { get; private set; }
        public ReadOnlyDictionary<string, string> Metadata { get; private set; }
        public double CableLengthMetres { get; private set; }
        // Do not silently relabel this source field U0 until the host has verified its intended meaning.
        public double NominalVoltageKv { get; private set; }
        public string CableConstructionCode { get; private set; }
        public ReadOnlyCollection<ExternalOwtsJoint> Joints { get; private set; }
        public ReadOnlyCollection<ExternalOwtsRecord> Records { get; private set; }
        public ReadOnlyCollection<ExternalOwtsIssue> Issues { get; private set; }
        public bool HasImportableData { get { return Records.Any(r => r.Status == "valid"); } }
        public bool IsComplete { get { return !Issues.Any(issue => issue.IsError); } }
        internal ExternalOwtsBatch(string root, ExternalOwtsSource metadataSource, string encoding,
            Dictionary<string, string> metadata, double length, double nominalVoltage, List<ExternalOwtsJoint> joints,
            List<ExternalOwtsRecord> records, List<ExternalOwtsIssue> issues)
        {
            SourceDirectory = root; MetadataSource = metadataSource; MetadataEncoding = encoding;
            Metadata = new ReadOnlyDictionary<string, string>(metadata); CableLengthMetres = length; NominalVoltageKv = nominalVoltage;
            CableConstructionCode = ExternalOwtsImporter.Get(metadata, "CableConstruction"); Joints = joints.AsReadOnly();
            Records = records.AsReadOnly(); Issues = issues.AsReadOnly();
        }
        // Bridge for a native project transaction: metadata only. Sample vectors are
        // stored separately from Records with their explicit units and time interval.
        public Dictionary<string, object> CreateManifest()
        {
            Func<ExternalOwtsSource, object> source = s => new Dictionary<string, object> { { "file", s.RelativePath }, { "bytes", s.Bytes }, { "sha256", s.Sha256 } };
            return new Dictionary<string, object> {
                { "schema", "external-owts-import-v1" }, { "source_directory_name", Path.GetFileName(SourceDirectory) },
                { "complete", IsComplete },
                { "metadata_encoding", MetadataEncoding }, { "metadata_source", source(MetadataSource) },
                { "source_metadata", Metadata }, { "cable_length_m", FiniteValue(CableLengthMetres) },
                { "source_nominal_voltage_kv", FiniteValue(NominalVoltageKv) }, { "source_cable_construction_code", CableConstructionCode },
                { "joints", Joints }, { "issues", Issues },
                { "records", Records.Select(r => new Dictionary<string, object> {
                    { "id", r.Id }, { "kind", r.Kind }, { "status", r.Status }, { "reason", r.Reason }, { "phase", r.Phase },
                    { "sample_count", r.Values.Count }, { "sample_interval_seconds", FiniteValue(r.SampleIntervalSeconds) },
                    { "value_unit", r.ValueUnit }, { "voltage_unit", r.VoltageKv.Count > 0 ? "kV" : "" },
                    { "test_voltage_kv", FiniteValue(r.TestVoltageKv) }, { "calibration_record_id", r.CalibrationRecordId },
                    { "source_headers", r.Headers }, { "sources", r.Sources.Select(source).ToArray() },
                    { "binary_location_decoded", false }
                }).ToArray() }
            };
        }
        static object FiniteValue(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? null : (object)value; }
    }

    /// <summary>Read-only adapter for observed StartMask/CAL/TOT/TAN exports. No LabVIEW and no guessed LOC decoder.</summary>
    internal static class ExternalOwtsImporter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex RecordName = new Regex("^(L[123])_([0-9]+)kV([0-9]+)_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        sealed class FileData { internal ExternalOwtsSource Source; internal byte[] Bytes; }
        sealed class ParsedWave { internal Dictionary<string, string> Headers; internal double Dt; internal double[] Value, Voltage; }

        public static ExternalOwtsDiscovery Discover(string directory, ExternalOwtsLimits limits = null, CancellationToken cancellation = default(CancellationToken))
        {
            limits = limits ?? new ExternalOwtsLimits(); limits.Validate(); string root = SafeRoot(directory);
            var batches = new List<string>(); var issues = new List<ExternalOwtsIssue>();
            var pending = new Queue<Tuple<string, int>>(); pending.Enqueue(Tuple.Create(root, 0)); int visited = 0; bool complete = true;
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++visited > limits.MaximumDirectories) { issues.Add(Issue("directory_limit", "", "Directory discovery limit reached.", true)); complete = false; break; }
                var item = pending.Dequeue();
                try
                {
                    RejectReparse(item.Item1);
                    string mask = Path.Combine(item.Item1, "StartMask.dat");
                    if (File.Exists(mask))
                    {
                        RejectFileReparse(mask);
                        if (batches.Count == limits.MaximumBatches) { issues.Add(Issue("batch_limit", "", "Batch discovery limit reached.", true)); complete = false; break; }
                        batches.Add(item.Item1);
                        // Test batches are leaves; never recursively adopt a nested project.
                        continue;
                    }
                    string[] children = Directory.EnumerateDirectories(item.Item1).Take(limits.MaximumDirectories + 1).ToArray();
                    if (item.Item2 >= limits.MaximumDepth && children.Length > 0) { complete = false; issues.Add(Issue("depth_limit", Relative(root, item.Item1), "Subdirectories exceed the discovery depth limit.", true)); continue; }
                    foreach (string child in children.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (pending.Count + visited >= limits.MaximumDirectories) { complete = false; issues.Add(Issue("directory_limit", Relative(root, child), "Directory discovery limit reached.", true)); break; }
                        try { RejectReparse(child); pending.Enqueue(Tuple.Create(child, item.Item2 + 1)); }
                        catch (IOException) { complete = false; issues.Add(Issue("redirected_directory", Relative(root, child), "Linked directories are not followed.", true)); }
                    }
                }
                catch (IOException) { complete = false; issues.Add(Issue("unreadable_directory", Relative(root, item.Item1), "The directory could not be inspected safely.", true)); }
                catch (UnauthorizedAccessException) { complete = false; issues.Add(Issue("unreadable_directory", Relative(root, item.Item1), "The directory could not be read.", true)); }
            }
            return new ExternalOwtsDiscovery(batches, issues, complete);
        }

        public static ExternalOwtsBatch ReadBatch(string directory, ExternalOwtsLimits limits = null, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            limits = limits ?? new ExternalOwtsLimits(); limits.Validate(); string root = SafeRoot(directory);
            string[] names = Directory.EnumerateFiles(root).Take(limits.MaximumFilesPerBatch + 1).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length > limits.MaximumFilesPerBatch) throw new InvalidDataException("The batch exceeds the file-count limit.");
            string maskPath = names.FirstOrDefault(p => string.Equals(Path.GetFileName(p), "StartMask.dat", StringComparison.OrdinalIgnoreCase));
            if (maskPath == null) throw new InvalidDataException("Select a batch containing StartMask.dat.");
            RejectFileReparse(maskPath);
            if (new FileInfo(maskPath).Length > 1024 * 1024) throw new InvalidDataException("StartMask.dat exceeds its metadata limit.");
            var issues = new List<ExternalOwtsIssue>(); long bytesRead = 0; int samplesRead = 0;
            var files = new Dictionary<string, FileData>(StringComparer.OrdinalIgnoreCase);
            // Load/hash under a read-only share. Parsing and hashes use the exact same bytes.
            FileData mask = ReadFile(maskPath, limits, ref bytesRead, cancellation);
            if (mask.Source.Bytes > 1024 * 1024) throw new InvalidDataException("StartMask.dat exceeds its metadata limit.");
            string encoding; string maskText = Decode(mask.Bytes, out encoding);
            var metadata = ParseMask(maskText, issues); double length = Number(Get(metadata, "CableLength"));
            double nominalVoltage = Number(Get(metadata, "NominalVoltage"));
            if (!Finite(length) || length <= 0) issues.Add(Issue("invalid_cable_length", "StartMask.dat", "Cable length is missing or invalid; no value was invented.", true));
            if (!Finite(nominalVoltage) || nominalVoltage < 0) issues.Add(Issue("invalid_nominal_voltage", "StartMask.dat", "Source nominal voltage is missing or invalid.", true));
            var joints = ParseJoints(metadata, length, issues);
            foreach (string path in names.Where(p => !string.Equals(p, maskPath, StringComparison.OrdinalIgnoreCase)))
            {
                cancellation.ThrowIfCancellationRequested();
                try { files.Add(Path.GetFileName(path), ReadFile(path, limits, ref bytesRead, cancellation)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
                { issues.Add(Issue("source_not_read", Path.GetFileName(path), e.Message, true)); }
            }
            var records = new List<ExternalOwtsRecord>();
            var calibrationIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Values.Where(f => Path.GetExtension(f.Source.RelativePath).Equals(".cal", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    ParsedWave wave = ParseWave(file.Bytes, true, limits, ref samplesRead, cancellation);
                    string stem = Path.GetFileNameWithoutExtension(file.Source.RelativePath), phase = PhaseFromName(stem);
                    if (phase.Length == 0 && stem.StartsWith("All_", StringComparison.OrdinalIgnoreCase)) phase = "ALL";
                    if (phase.Length == 0) throw new InvalidDataException("Calibration phase is not recognized.");
                    string id = "calibration:" + stem; calibrationIds[file.Source.RelativePath] = id;
                    records.Add(new ExternalOwtsRecord(id, "calibration", "valid", phase, "", wave.Dt, double.NaN, "", "V", wave.Value, new double[0], new[] { file.Source }, wave.Headers));
                }
                catch (Exception e) when (e is InvalidDataException || e is FormatException || e is OverflowException)
                { records.Add(InvalidRecord(file, "calibration", e.Message)); issues.Add(Issue("invalid_calibration", file.Source.RelativePath, e.Message, true)); }
            }
            var waveFiles = files.Values.Where(f => new[] { ".tot", ".tan" }.Contains(Path.GetExtension(f.Source.RelativePath).ToLowerInvariant()));
            foreach (var pair in waveFiles.GroupBy(f => Path.GetFileNameWithoutExtension(f.Source.RelativePath), StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested(); string stem = pair.Key;
                FileData primary = pair.FirstOrDefault(f => Path.GetExtension(f.Source.RelativePath).Equals(".tot", StringComparison.OrdinalIgnoreCase)) ?? pair.First();
                FileData tan = pair.FirstOrDefault(f => Path.GetExtension(f.Source.RelativePath).Equals(".tan", StringComparison.OrdinalIgnoreCase));
                try
                {
                    Match name = RecordName.Match(stem);
                    if (!name.Success) throw new InvalidDataException("The test phase or filename voltage is not recognized.");
                    ParsedWave wave = ParseWave(primary.Bytes, false, limits, ref samplesRead, cancellation);
                    var headers = wave.Headers;
                    if (tan != null && !ReferenceEquals(tan, primary))
                    {
                        try
                        {
                            ParsedWave tanWave = ParseWave(tan.Bytes, false, limits, ref samplesRead, cancellation);
                            if (wave.Dt != tanWave.Dt || !wave.Value.SequenceEqual(tanWave.Value) || !wave.Voltage.SequenceEqual(tanWave.Voltage) ||
                                !SameCalibrationContext(wave.Headers, tanWave.Headers))
                                issues.Add(Issue("tan_tot_mismatch", tan.Source.RelativePath, "TOT and TAN differ. TOT waveform is retained; TAN physical parameters are not merged.", true));
                            else foreach (var header in tanWave.Headers.Where(h => !headers.ContainsKey(h.Key))) headers.Add(header.Key, header.Value);
                        }
                        catch (InvalidDataException e) { issues.Add(Issue("invalid_tan", tan.Source.RelativePath, e.Message, true)); }
                    }
                    string referenced = Get(headers, "Cal.File-path").Replace('/', '\\');
                    string localName = Path.GetFileName(referenced); string calId;
                    if (!calibrationIds.TryGetValue(localName, out calId))
                    {
                        calId = ""; issues.Add(Issue("calibration_unavailable", primary.Source.RelativePath, "No validated calibration with this basename exists in this batch; the historical absolute path was not followed.", false));
                    }
                    double voltage = Number(name.Groups[2].Value + "." + name.Groups[3].Value);
                    records.Add(new ExternalOwtsRecord("oscillation:" + stem, "oscillation", "valid", name.Groups[1].Value.ToUpperInvariant(),
                        "", wave.Dt, voltage, calId, "pC", wave.Value, wave.Voltage, pair.Select(f => f.Source), headers));
                }
                catch (Exception e) when (e is InvalidDataException || e is FormatException || e is OverflowException)
                { records.Add(InvalidRecord(primary, "oscillation", e.Message, pair.Select(f => f.Source))); issues.Add(Issue("invalid_waveform", primary.Source.RelativePath, e.Message, true)); }
            }
            foreach (var file in files.Values.Where(f => !new[] { ".cal", ".tot", ".tan" }.Contains(Path.GetExtension(f.Source.RelativePath).ToLowerInvariant())))
            {
                bool location = Path.GetExtension(file.Source.RelativePath).Equals(".loc", StringComparison.OrdinalIgnoreCase);
                string reason = location ? "Location payload decoding is unsupported; no locations, pulse matches or sample format have been inferred." : "Unrecognized source file retained by its provenance reference; no parser has been applied.";
                var header = location ? ParseHeadersBeforeBinary(file.Bytes) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                records.Add(new ExternalOwtsRecord((location ? "location:" + Path.GetFileNameWithoutExtension(file.Source.RelativePath) : "source:" + file.Source.RelativePath),
                    location ? "location" : "other", "unsupported", PhaseFromName(file.Source.RelativePath), reason, double.NaN, double.NaN, "", "", null, null, new[] { file.Source }, header));
                issues.Add(Issue(location ? "unsupported_location" : "unsupported_file", file.Source.RelativePath, reason, false));
            }
            return new ExternalOwtsBatch(root, mask.Source, encoding, metadata, length, nominalVoltage, joints, records, issues);
        }

        static ParsedWave ParseWave(byte[] bytes, bool calibration, ExternalOwtsLimits limits, ref int totalSamples, CancellationToken cancellation)
        {
            string encoding; string text;
            try { text = Decode(bytes, out encoding); }
            catch (DecoderFallbackException) { throw new InvalidDataException("The waveform text encoding is invalid."); }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var values = new List<double>(); var voltage = new List<double>(); bool inData = false, columnsRead = calibration;
            using (var reader = new StringReader(text))
            {
                string line; int lines = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((lines++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    if (line.Length > 16384) throw new InvalidDataException("A text row exceeds the import limit; binary content is not accepted as a waveform.");
                    if (!inData)
                    {
                        if (line.Trim().Equals(calibration ? "Data:" : "DATA-TOTAL:", StringComparison.OrdinalIgnoreCase)) { inData = true; continue; }
                        if (lines > 256) throw new InvalidDataException("Waveform data header was not found.");
                        ParseHeaderLine(line, headers); continue;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!columnsRead)
                    {
                        string normalized = Regex.Replace(line, "\\s+", "").ToLowerInvariant();
                        if (normalized != "voltage(kv):pd(pc):") throw new InvalidDataException("Expected explicit Voltage (kV) and PD (pC) columns.");
                        columnsRead = true; continue;
                    }
                    string[] fields = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length != (calibration ? 1 : 2)) throw new InvalidDataException("Unexpected waveform column count.");
                    if (values.Count >= limits.MaximumSamplesPerRecord || totalSamples >= limits.MaximumSamplesPerBatch)
                        throw new InvalidDataException("The waveform exceeds the sample-count limit.");
                    double first = Number(fields[0]); double second = calibration ? 0 : Number(fields[1]);
                    if (!Finite(first) || !Finite(second)) throw new InvalidDataException("Waveform samples must be finite numeric values.");
                    // CAL samples are explicitly mV in this export profile; normalize to V once.
                    if (calibration) values.Add(first / 1000.0); else { voltage.Add(first); values.Add(second); }
                    totalSamples++;
                }
            }
            if (!inData || !columnsRead || values.Count < 2) throw new InvalidDataException("No complete waveform samples were found.");
            double dt = Number(Get(headers, calibration ? "Delta_x" : "Delta-x"));
            if (!Finite(dt) || dt <= 0 || !Finite(1.0 / dt)) throw new InvalidDataException("A finite positive sample interval in seconds is required.");
            if (calibration && !headers.ContainsKey("Cal.max (mV)")) throw new InvalidDataException("The CAL voltage unit is not confirmed as mV; conversion is refused.");
            if (!calibration)
            {
                int expected; if (!int.TryParse(Get(headers, "Number of Samples"), NumberStyles.Integer, Inv, out expected) || expected != values.Count)
                    throw new InvalidDataException("Declared sample count does not match the waveform rows.");
            }
            return new ParsedWave { Headers = headers, Dt = dt, Value = values.ToArray(), Voltage = voltage.ToArray() };
        }
        static Dictionary<string, string> ParseMask(string text, List<ExternalOwtsIssue> issues)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); bool inside = false;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim(); if (line.StartsWith("[")) { inside = line.Equals("[MaskData]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inside || line.Length == 0 || line.StartsWith(";")) continue;
                    int equal = line.IndexOf('='); if (equal <= 0) continue;
                    string key = line.Substring(0, equal).Trim(), value = line.Substring(equal + 1).Trim().Trim('"');
                    if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate StartMask field: " + key);
                    result.Add(key, value);
                }
            }
            if (result.Count == 0) throw new InvalidDataException("StartMask.dat has no MaskData section.");
            return result;
        }
        static List<ExternalOwtsJoint> ParseJoints(Dictionary<string, string> metadata, double length, List<ExternalOwtsIssue> issues)
        {
            var result = new List<ExternalOwtsJoint>();
            foreach (var field in metadata)
            {
                Match match = Regex.Match(field.Key, "^JointLocation(L[123])[0-9]{2,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) continue;
                string[] cells = field.Value.Split(','); int order; double position = cells.Length >= 2 ? Number(cells[1]) : double.NaN;
                string phase = match.Groups[1].Value.ToUpperInvariant();
                if (cells.Length < 2 || !int.TryParse(cells[0], out order) || order < 1 || !Finite(position) || position <= 0 ||
                    !Finite(length) || position >= length || result.Any(j => j.Phase == phase && (j.Sequence == order || Math.Abs(j.PositionMetres - position) < 0.000001)))
                { issues.Add(Issue("invalid_joint", "StartMask.dat", "Invalid or duplicate joint entry: " + field.Key, true)); continue; }
                result.Add(new ExternalOwtsJoint(phase, order, position, cells.Length > 2 ? string.Join(",", cells.Skip(2)).Trim() : ""));
            }
            return result.OrderBy(j => j.Phase).ThenBy(j => j.PositionMetres).ToList();
        }
        static FileData ReadFile(string path, ExternalOwtsLimits limits, ref long total, CancellationToken cancellation)
        {
            RejectReparse(Path.GetDirectoryName(path)); RejectFileReparse(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > limits.MaximumFileBytes || stream.Length > limits.MaximumBatchBytes - total)
                    throw new InvalidDataException("The source exceeds the file or batch byte limit.");
                long size = stream.Length; total += size; var bytes = new byte[(int)size]; int offset = 0;
                while (offset < bytes.Length) { cancellation.ThrowIfCancellationRequested(); int count = stream.Read(bytes, offset, Math.Min(65536, bytes.Length - offset)); if (count == 0) throw new EndOfStreamException(); offset += count; }
                if (stream.Length != size) throw new IOException("Source changed during import.");
                using (var sha = SHA256.Create()) return new FileData { Bytes = bytes, Source = new ExternalOwtsSource(path, size, BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "")) };
            }
        }
        static string Decode(byte[] bytes, out string encoding)
        {
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = "UTF-16LE"; return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2); }
            if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) { encoding = "UTF-8"; return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3); }
            try { string text = new UTF8Encoding(false, true).GetString(bytes); encoding = "UTF-8"; return text; }
            catch (DecoderFallbackException) { encoding = "GB936"; return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
        }
        static Dictionary<string, string> ParseHeadersBeforeBinary(byte[] bytes)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int maximum = Math.Min(bytes.Length, 8192), start = 0;
            for (int i = 0; i < maximum; i++)
            {
                if (bytes[i] != 10) continue;
                string line = Encoding.GetEncoding(936).GetString(bytes, start, i - start).TrimEnd('\r'); start = i + 1;
                if (line.Trim().Equals("BINARY:", StringComparison.OrdinalIgnoreCase)) break;
                if (line.IndexOf('\0') >= 0 || line.Length > 2048) break;
                ParseHeaderLine(line, headers);
            }
            return headers;
        }
        static void ParseHeaderLine(string line, Dictionary<string, string> headers)
        {
            int tab = line.IndexOf('\t'); if (tab < 0) return;
            string key = line.Substring(0, tab).Trim().TrimEnd(':', '=');
            if (key.Length == 0) return;
            string value = line.Substring(tab + 1).Trim(), existing;
            if (headers.TryGetValue(key, out existing) && existing != value) throw new InvalidDataException("Conflicting duplicate header: " + key);
            headers[key] = value;
        }
        static bool SameCalibrationContext(Dictionary<string, string> left, Dictionary<string, string> right)
        {
            foreach (string field in new[] { "Cal.value (pC)", "Cal.max (mV)" })
            {
                string a = Get(left, field), b = Get(right, field);
                if (a == b) continue;
                double x = Number(a), y = Number(b); if (!Finite(x) || !Finite(y) || x != y) return false;
            }
            return string.Equals(Path.GetFileName(Get(left, "Cal.File-path").Replace('/', '\\')),
                Path.GetFileName(Get(right, "Cal.File-path").Replace('/', '\\')), StringComparison.OrdinalIgnoreCase);
        }
        static ExternalOwtsRecord InvalidRecord(FileData file, string kind, string reason, IEnumerable<ExternalOwtsSource> sources = null)
        { return new ExternalOwtsRecord(kind + ":" + Path.GetFileNameWithoutExtension(file.Source.RelativePath), kind, "invalid", PhaseFromName(file.Source.RelativePath), reason, double.NaN, double.NaN, "", "", null, null, sources ?? new[] { file.Source }, new Dictionary<string, string>()); }
        static string PhaseFromName(string name) { Match match = Regex.Match(name, "^(L[123])_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); return match.Success ? match.Groups[1].Value.ToUpperInvariant() : ""; }
        internal static string Get(IDictionary<string, string> dictionary, string key) { string value; return dictionary.TryGetValue(key, out value) ? value : ""; }
        static double Number(string text) { double value; return double.TryParse(text, NumberStyles.Float, Inv, out value) ? value : double.NaN; }
        static bool Finite(double number) { return !double.IsNaN(number) && !double.IsInfinity(number); }
        static ExternalOwtsIssue Issue(string code, string path, string detail, bool error) { return new ExternalOwtsIssue(code, path, detail, error); }
        static string SafeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Select a specific source folder.");
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, Path.GetPathRoot(full).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) throw new ArgumentException("Select an existing project collection or test batch, not a drive root.");
            RejectReparse(full); return full;
        }
        static void RejectReparse(string path)
        { for (var current = new DirectoryInfo(path); current != null; current = current.Parent) if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked source paths are not followed."); }
        static void RejectFileReparse(string path)
        { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked source files are not followed."); }
        static string Relative(string root, string path) { return path.Length > root.Length ? path.Substring(root.Length).TrimStart('\\', '/') : ""; }
    }
}
