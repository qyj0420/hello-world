using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HVX.ProjectCables;

namespace HVX.ReportGenerator
{
    public sealed class ReportPhaseSummary
    {
        private double pdiv = double.NaN, pdev = double.NaN;
        internal Action<ReportPhaseSummary> Changed;

        internal ReportPhaseSummary(ReportPhase phase)
        {
            Phase = phase;
            NoisePc = FrequencyHz = CapacitanceUf = DielectricLossPercent = double.NaN;
            InsulationBeforeMohm = InsulationAfterMohm = ProjectReportData.Missing;
        }

        public ReportPhase Phase { get; private set; }
        public double NoisePc { get; internal set; }
        public double FrequencyHz { get; internal set; }
        public double CapacitanceUf { get; internal set; }
        public double DielectricLossPercent { get; internal set; }
        public string InsulationBeforeMohm { get; internal set; }
        public string InsulationAfterMohm { get; internal set; }
        public int DemFileCount { get; internal set; }
        public double PDIV { get { return pdiv; } set { pdiv = FiniteOrNaN(value); Notify(); } }
        public double PDEV { get { return pdev; } set { pdev = FiniteOrNaN(value); Notify(); } }
        public double PDIVKv { get { return PDIV; } set { PDIV = value; } }
        public double PDEVKv { get { return PDEV; } set { PDEV = value; } }

        private void Notify() { Action<ReportPhaseSummary> handler = Changed; if (handler != null) handler(this); }
        private static double FiniteOrNaN(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? double.NaN : value; }
    }

    /// <summary>UI-neutral, report-ready data read from one OWTS project.</summary>
    public sealed class ProjectReportData
    {
        public const string Missing = "—";
        private readonly ReportPhaseSummary[] phases;
        private double[] jointPositions = new double[0];
        private string[] warnings = new string[0];
        internal CableJointSnapshot CableStructure;
        internal string CableStructureError = "Saved cable structure is unavailable.";
        internal string ItemHash, CableHash;
        internal bool MetadataCaptured;

        internal void RequireCurrentMetadata()
        {
            if (!MetadataCaptured || ItemHash != ProjectReportDataReader.MetadataHash(Path.Combine(ProjectPath, "ITEM.in")) ||
                CableHash != ProjectReportDataReader.MetadataHash(Path.Combine(ProjectPath, "CABLE.in")))
                throw new InvalidOperationException("项目资料或电缆结构已变化，请重新加载项目并核对分析后生成报告。 / Project metadata changed; reload and review the analysis before generating a report.");
        }

        public ProjectReportData()
        {
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            phases = new[] { new ReportPhaseSummary(ReportPhase.A), new ReportPhaseSummary(ReportPhase.B), new ReportPhaseSummary(ReportPhase.C) };
            foreach (ReportPhaseSummary phase in phases) phase.Changed = SyncPhaseValues;
            ProjectPath = ProjectName = InspectionOrganization = TestPersonnel = TestLocation = Missing;
            StartName = EndName = StartAccessory = EndAccessory = Missing;
            CableModel = CableCoreCount = CableInsulation = AccessorySummary = JointSummary = Missing;
            LineName = InstallationDate = VoltageMode = CableAgeCategory = Notes = Missing;
            CableLengthM = DeviceU0Kv = double.NaN;
        }

        public Dictionary<string, string> Values { get; private set; }
        public string ProjectPath { get; internal set; }
        public string ProjectName { get; internal set; }
        public string InspectionOrganization { get; internal set; }
        public string TestPersonnel { get; internal set; }
        public string TestLocation { get; internal set; }
        public string StartName { get; internal set; }
        public string EndName { get; internal set; }
        public string StartAccessory { get; internal set; }
        public string EndAccessory { get; internal set; }
        public string CableModel { get; internal set; }
        public string CableCoreCount { get; internal set; }
        public string CableInsulation { get; internal set; }
        public string AccessorySummary { get; internal set; }
        public string JointSummary { get; internal set; }
        public string LineName { get; internal set; }
        public string InstallationDate { get; internal set; }
        public string VoltageMode { get; internal set; }
        public string CableAgeCategory { get; internal set; }
        public string Notes { get; internal set; }
        public double CableLengthM { get; internal set; }
        public double DeviceU0Kv { get; internal set; }
        public bool OlderThanOneYear { get; internal set; }
        public double[] JointPositions { get { return (double[])jointPositions.Clone(); } }
        public string[] Warnings { get { return (string[])warnings.Clone(); } }
        public ReportPhaseSummary[] PhaseSummaries { get { return (ReportPhaseSummary[])phases.Clone(); } }
        public ReportPhaseSummary PhaseA { get { return phases[0]; } }
        public ReportPhaseSummary PhaseB { get { return phases[1]; } }
        public ReportPhaseSummary PhaseC { get { return phases[2]; } }

        public ReportPhaseSummary ForPhase(ReportPhase phase)
        {
            if (phase == ReportPhase.A) return PhaseA;
            if (phase == ReportPhase.B) return PhaseB;
            if (phase == ReportPhase.C) return PhaseC;
            throw new ArgumentOutOfRangeException("phase", "Project phase summary must be A, B or C.");
        }

        public void RefreshValues() { foreach (ReportPhaseSummary phase in phases) SyncPhaseValues(phase); }
        internal void SetJointPositions(IEnumerable<double> positions)
        {
            jointPositions = (positions ?? Enumerable.Empty<double>()).Where(IsFinite).Where(x => x >= 0.0)
                .Distinct(new PositionComparer()).OrderBy(x => x).ToArray();
        }
        internal void SetWarnings(IEnumerable<string> value)
        {
            warnings = (value ?? Enumerable.Empty<string>()).Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        }
        private void SyncPhaseValues(ReportPhaseSummary value)
        {
            string prefix = "phase." + value.Phase.ToString().ToLowerInvariant() + ".";
            Values[prefix + "noise_pc"] = Display(value.NoisePc);
            Values[prefix + "frequency_hz"] = Display(value.FrequencyHz);
            Values[prefix + "capacitance_uf"] = Display(value.CapacitanceUf);
            Values[prefix + "dielectric_loss_percent"] = Display(value.DielectricLossPercent);
            Values[prefix + "insulation_before_mohm"] = Safe(value.InsulationBeforeMohm);
            Values[prefix + "insulation_after_mohm"] = Safe(value.InsulationAfterMohm);
            Values[prefix + "pdiv_kv"] = Display(value.PDIV);
            Values[prefix + "pdev_kv"] = Display(value.PDEV);
            Values[prefix + "dem_file_count"] = value.DemFileCount.ToString(CultureInfo.InvariantCulture);
        }
        internal static string Safe(string value) { return String.IsNullOrWhiteSpace(value) ? Missing : value.Trim(); }
        internal static string Display(double value) { return IsFinite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : Missing; }
        internal static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private sealed class PositionComparer : IEqualityComparer<double>
        {
            public bool Equals(double x, double y) { return Math.Abs(x - y) <= 0.001; }
            public int GetHashCode(double value) { return Math.Round(value, 3).GetHashCode(); }
        }
    }

    /// <summary>Reads only top-level project files; paths recorded inside those files are never followed.</summary>
    public static class ProjectReportDataReader
    {
        private const long MaximumMetadataBytes = 8L * 1024 * 1024;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Regex NumberPattern = new Regex(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);
        private static readonly Regex MeasurementName = new Regex(@"^(ALL|A|B|C)_([-+]?[0-9]+(?:\.[0-9]+)?)kV_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Dictionary<string, string> ItemAliases = BuildItemAliases();
        internal static Action MetadataReadCompletedForTests;

        internal static string MetadataHash(string path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumMetadataBytes) throw new InvalidDataException("Metadata file exceeds 8 MiB.");
            return CableJointStore.Hash(File.ReadAllBytes(path));
        }

        public static ProjectReportData Read(string projectPath) { return Load(projectPath); }
        public static ProjectReportData Load(string projectPath)
        {
            string root = ResolveProjectRoot(projectPath);
            var data = new ProjectReportData { ProjectPath = root, ProjectName = ProjectReportData.Safe(new DirectoryInfo(root).Name) };
            data.ItemHash = MetadataHash(Path.Combine(root, "ITEM.in"));
            data.CableHash = MetadataHash(Path.Combine(root, "CABLE.in"));
            data.MetadataCaptured = true;
            var warnings = new List<string>();
            Dictionary<string, string> item = ReadItem(Path.Combine(root, "ITEM.in"), warnings);
            ApplyItem(data, item);
            ReadCable(Path.Combine(root, "CABLE.in"), data, warnings);
            if (MetadataReadCompletedForTests != null) MetadataReadCompletedForTests();
            ReadMeasurements(root, data, warnings);
            PopulateValues(data, item);
            data.SetWarnings(warnings);
            // Do not publish a model composed from metadata versions that
            // changed while the readers were working.
            data.RequireCurrentMetadata();
            return data;
        }
        public static bool TryRead(string projectPath, out ProjectReportData data)
        {
            try { data = Load(projectPath); return true; } catch { data = null; return false; }
        }

        private static string ResolveProjectRoot(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Project path cannot be blank.", "projectPath");
            string full = Path.GetFullPath(path.Trim());
            if (File.Exists(full)) full = Path.GetDirectoryName(full);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
            string candidate = full;
            for (int depth = 0; depth < 4 && !String.IsNullOrEmpty(candidate); depth++)
            {
                if (File.Exists(Path.Combine(candidate, "ITEM.in")) || File.Exists(Path.Combine(candidate, "CABLE.in"))) return candidate;
                DirectoryInfo parent = Directory.GetParent(candidate); candidate = parent == null ? null : parent.FullName;
            }
            return full;
        }

        private static Dictionary<string, string> ReadItem(string path, List<string> warnings)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) { warnings.Add("ITEM.in was not found; unavailable report fields use —."); return result; }
            try
            {
                bool itemSection = true;
                foreach (string raw in SplitLines(ReadText(path)))
                {
                    string trimmed = raw.Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                    { itemSection = trimmed.Equals("[ITEM]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!itemSection || trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#') continue;
                    int split = raw.IndexOf('='); if (split < 0) split = raw.IndexOf('\t'); if (split < 0) continue;
                    string canonical;
                    if (ItemAliases.TryGetValue(NormalizeKey(raw.Substring(0, split)), out canonical)) result[canonical] = Unquote(raw.Substring(split + 1));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException || ex is InvalidDataException)
            { warnings.Add("ITEM.in: " + ex.Message); }
            return result;
        }

        private static void ApplyItem(ProjectReportData data, Dictionary<string, string> item)
        {
            data.InspectionOrganization = Value(item, "project.inspection_organization"); data.TestPersonnel = Value(item, "project.test_personnel");
            data.TestLocation = Value(item, "project.test_location"); data.StartName = Value(item, "cable.start_name"); data.EndName = Value(item, "cable.end_name");
            data.StartAccessory = Value(item, "cable.start_type"); data.EndAccessory = Value(item, "cable.end_type");
            data.CableModel = Value(item, "cable.model"); data.CableCoreCount = Value(item, "cable.core_count"); data.LineName = Value(item, "cable.line_name");
            data.InstallationDate = Value(item, "cable.installation_date"); data.VoltageMode = Value(item, "test.voltage_mode"); data.Notes = Value(item, "project.notes");
            data.CableLengthM = Number(Raw(item, "cable.length_m")); data.DeviceU0Kv = Number(Raw(item, "test.phase_voltage"));
            string mode = Raw(item, "test.voltage_mode");
            data.OlderThanOneYear = mode.IndexOf("一年以上", StringComparison.OrdinalIgnoreCase) >= 0 || mode.IndexOf("over one year", StringComparison.OrdinalIgnoreCase) >= 0 || mode.IndexOf("older", StringComparison.OrdinalIgnoreCase) >= 0;
            data.CableAgeCategory = data.OlderThanOneYear ? "旧电缆" : "新电缆";
            foreach (ReportPhaseSummary phase in data.PhaseSummaries)
            {
                string letter = phase.Phase.ToString().ToLowerInvariant();
                phase.InsulationBeforeMohm = Value(item, "insulation." + letter + ".before_mohm");
                phase.InsulationAfterMohm = Value(item, "insulation." + letter + ".after_mohm");
                phase.PDIV = Number(Raw(item, "phase." + letter + ".pdiv_kv"));
                phase.PDEV = Number(Raw(item, "phase." + letter + ".pdev_kv"));
            }
        }

        private static void ReadCable(string path, ProjectReportData data, List<string> warnings)
        {
            if (!File.Exists(path)) { warnings.Add("CABLE.in was not found; cable structure fields use —."); return; }
            var joints = new List<double>(); var terminalPositions = new List<double>();
            var insulation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var accessories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jointDescriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string raw in SplitLines(ReadText(path)))
                {
                    string[] cells = raw.Split('\t'); if (cells.Length < 2) cells = Regex.Split(raw.Trim(), @"\s+"); if (cells.Length < 2) continue;
                    string code = cells[0].Trim(), name = cells[1].Trim(), type = cells.Length > 2 ? cells[2].Trim() : String.Empty;
                    double position = cells.Length > 3 ? Number(cells[3]) : double.NaN;
                    if (code == "2" && !String.IsNullOrWhiteSpace(type)) insulation.Add(type);
                    if (code != "1") continue;
                    string description = name + (String.IsNullOrWhiteSpace(type) ? String.Empty : "：" + type) +
                        (ProjectReportData.IsFinite(position) ? " @ " + position.ToString("0.###", Inv) + " m" : String.Empty);
                    accessories.Add(description);
                    bool joint = name.IndexOf("接头", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("joint", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("splice", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool start = name.IndexOf("始端", StringComparison.OrdinalIgnoreCase) >= 0 || name.Equals("start", StringComparison.OrdinalIgnoreCase);
                    bool end = name.IndexOf("终端", StringComparison.OrdinalIgnoreCase) >= 0 || name.Equals("end", StringComparison.OrdinalIgnoreCase) || name.IndexOf("terminal", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (joint)
                    {
                        jointDescriptions.Add(description);
                        if (ProjectReportData.IsFinite(position) && position >= 0.0) joints.Add(position);
                    }
                    if (end && ProjectReportData.IsFinite(position) && position > 0.0) terminalPositions.Add(position);
                    if (start && data.StartAccessory == ProjectReportData.Missing && !String.IsNullOrWhiteSpace(type)) data.StartAccessory = type;
                    if (end && data.EndAccessory == ProjectReportData.Missing && !String.IsNullOrWhiteSpace(type)) data.EndAccessory = type;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException || ex is InvalidDataException)
            { warnings.Add("CABLE.in: " + ex.Message); }
            // User-defined accessory names are not type identifiers. For the
            // supported saved structure, every interior accessory is a joint;
            // the first and last accessory are endpoints even if named "joint".
            try
            {
                CableJointSnapshot topology = CableJointStore.Read(Path.GetDirectoryName(path));
                data.CableStructure = topology;
                data.CableStructureError = "";
                bool commonGeometry = topology.Mode == CableJointMode.SharedThreeCore &&
                    topology.Blocks.All(block => block.Positions.SequenceEqual(topology.Blocks[0].Positions));
                if (topology.Mode == CableJointMode.SharedThreeCore && !commonGeometry)
                    warnings.Add("CABLE.in: shared three-core blocks disagree; positions are shown per original block, not as common joints.");
                joints.Clear(); jointDescriptions.Clear();
                foreach (CableJointBlock block in topology.Blocks)
                {
                    for (int i = 1; i < block.Positions.Count - 1; i++)
                    {
                        string[] cells = topology.Lines[block.AccessoryLines[i]].Split('\t');
                        string phase = commonGeometry ? "ALL" : block.Phase;
                        string name = cells[1].Trim(), type = cells[2].Trim();
                        joints.Add(block.Positions[i]);
                        jointDescriptions.Add(phase + " · " + name +
                            (String.IsNullOrWhiteSpace(type) ? String.Empty : "：" + type) +
                            " @ " + block.Positions[i].ToString("0.###", Inv) + " m");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException || ex is InvalidDataException || ex is ArgumentException)
            {
                data.CableStructureError = ex.Message;
                // Keep the legacy reader for older, non-four-column layouts,
                // but make its name-based limitation explicit to the caller.
                warnings.Add("CABLE.in: legacy name-based joint lookup; topology was not available: " + ex.Message);
            }
            data.SetJointPositions(joints);
            data.CableInsulation = insulation.Count == 0 ? ProjectReportData.Missing : String.Join("；", insulation.OrderBy(x => x).ToArray());
            data.AccessorySummary = accessories.Count == 0 ? ProjectReportData.Missing : String.Join("；", accessories.OrderBy(x => x).ToArray());
            data.JointSummary = jointDescriptions.Count == 0 ? ProjectReportData.Missing : String.Join("；", jointDescriptions.OrderBy(x => x).ToArray());
            if (!ProjectReportData.IsFinite(data.CableLengthM) && terminalPositions.Count > 0) data.CableLengthM = terminalPositions.Max();
        }

        private static void ReadMeasurements(string root, ProjectReportData data, List<string> warnings)
        {
            var aggregates = new Dictionary<ReportPhase, DemAggregate>
            {
                {ReportPhase.A, new DemAggregate()}, {ReportPhase.B, new DemAggregate()}, {ReportPhase.C, new DemAggregate()}
            };
            var common = new DemAggregate(); string[] files;
            try { files = Directory.GetFiles(root, "*.dem", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { warnings.Add("DEM inventory: " + ex.Message); files = new string[0]; }
            foreach (string path in files)
            {
                try
                {
                    Match match = MeasurementName.Match(Path.GetFileNameWithoutExtension(path)); if (!match.Success) continue;
                    HVX.Storage.PressureRecordPublication.RequireReadable(path);
                    ReportPhase phase = ParsePhase(match.Groups[1].Value); double voltage = Number(match.Groups[2].Value);
                    Dictionary<string, string> header = ReadHeader(path, 20); DemAggregate aggregate = phase == ReportPhase.All ? common : aggregates[phase];
                    aggregate.FileCount++;
                    double scale = HeaderNumber(header, "PD-scale max");
                    if (ProjectReportData.IsFinite(voltage) && Math.Abs(voltage) <= 0.01 && ProjectReportData.IsFinite(scale) && scale >= 0.0) aggregate.Noise.Add(scale);
                    AddFinitePositive(aggregate.Frequency, HeaderNumber(header, "Hz"));
                    AddFiniteNonnegative(aggregate.Capacitance, HeaderNumber(header, "C[uF]"));
                    AddFiniteNonnegative(aggregate.DielectricLoss, HeaderNumber(header, "D-losses"));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DecoderFallbackException || ex is InvalidDataException)
                { warnings.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            foreach (ReportPhaseSummary summary in data.PhaseSummaries)
            {
                DemAggregate own = aggregates[summary.Phase];
                summary.NoisePc = Median(own.Noise.Count > 0 ? own.Noise : common.Noise);
                summary.FrequencyHz = Median(own.Frequency.Count > 0 ? own.Frequency : common.Frequency);
                summary.CapacitanceUf = Median(own.Capacitance.Count > 0 ? own.Capacitance : common.Capacitance);
                summary.DielectricLossPercent = Median(own.DielectricLoss.Count > 0 ? own.DielectricLoss : common.DielectricLoss);
                summary.DemFileCount = own.FileCount > 0 ? own.FileCount : common.FileCount;
            }
        }

        private static void PopulateValues(ProjectReportData data, Dictionary<string, string> item)
        {
            foreach (KeyValuePair<string, string> pair in item) data.Values[pair.Key] = ProjectReportData.Safe(pair.Value);
            Put(data, "project.path", data.ProjectPath); Put(data, "project.name", data.ProjectName);
            Put(data, "project.inspection_organization", data.InspectionOrganization); Put(data, "project.test_personnel", data.TestPersonnel);
            Put(data, "project.test_location", data.TestLocation); Put(data, "project.notes", data.Notes);
            Put(data, "cable.start_name", data.StartName); Put(data, "cable.end_name", data.EndName);
            Put(data, "cable.start_type", data.StartAccessory); Put(data, "cable.end_type", data.EndAccessory);
            Put(data, "cable.model", data.CableModel); Put(data, "cable.core_count", data.CableCoreCount);
            Put(data, "cable.line_name", data.LineName); Put(data, "cable.installation_date", data.InstallationDate);
            Put(data, "cable.insulation", data.CableInsulation); Put(data, "cable.accessories", data.AccessorySummary); Put(data, "cable.joints", data.JointSummary);
            data.Values["cable.length_m"] = ProjectReportData.Display(data.CableLengthM);
            data.Values["cable.joint_positions_m"] = data.JointPositions.Length == 0 ? ProjectReportData.Missing : String.Join(", ", data.JointPositions.Select(x => x.ToString("0.###", Inv)).ToArray());
            data.Values["test.phase_voltage"] = ProjectReportData.Display(data.DeviceU0Kv); data.Values["test.device_u0_kv"] = ProjectReportData.Display(data.DeviceU0Kv);
            Put(data, "test.voltage_mode", data.VoltageMode); Put(data, "test.cable_age", data.CableAgeCategory);
            data.Values["test.older_than_one_year"] = data.OlderThanOneYear ? "true" : "false"; data.RefreshValues();
        }

        private static void Put(ProjectReportData data, string key, string value) { data.Values[key] = ProjectReportData.Safe(value); }
        private static string Raw(Dictionary<string, string> values, string key) { string value; return values.TryGetValue(key, out value) ? value : String.Empty; }
        private static string Value(Dictionary<string, string> values, string key) { return ProjectReportData.Safe(Raw(values, key)); }

        private static Dictionary<string, string> ReadHeader(string path, int lineLimit)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (StreamReader reader = OpenReader(path))
            {
                for (int i = 0; i < lineLimit; i++)
                {
                    string line = reader.ReadLine(); if (line == null) break;
                    int tab = line.IndexOf('\t'), colon = line.IndexOfAny(new[] { ':', '：' }), equal = line.IndexOf('=');
                    int split = FirstPositive(tab, colon, equal); if (split < 0) continue;
                    string key = line.Substring(0, split).Trim().TrimEnd(':', '：', '=');
                    string value = line.Substring(split + 1).Trim().TrimStart(':', '：', '=').Trim();
                    if (key.Length > 0) result[key] = value;
                }
            }
            return result;
        }

        private static StreamReader OpenReader(string path)
        {
            var info = new FileInfo(path);
            if (info.Length > 1024L * 1024 * 1024) throw new InvalidDataException("Measurement file exceeds the 1 GiB safety limit.");
            byte[] probe = new byte[Math.Min(8192, (int)Math.Min(Int32.MaxValue, info.Length))];
            int count; using (FileStream stream = File.OpenRead(path)) count = stream.Read(probe, 0, probe.Length);
            return new StreamReader(path, DetectEncoding(probe, count), true, 4096);
        }

        private static string ReadText(string path)
        {
            var info = new FileInfo(path); if (info.Length > MaximumMetadataBytes) throw new InvalidDataException("Metadata file exceeds 8 MiB.");
            byte[] bytes = File.ReadAllBytes(path); Encoding encoding = DetectEncoding(bytes, bytes.Length);
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 :
                bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)) ? 2 : 0;
            return encoding.GetString(bytes, offset, bytes.Length - offset);
        }

        private static Encoding DetectEncoding(byte[] bytes, int count)
        {
            if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(false, true);
            if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return new UnicodeEncoding(false, false, true);
            if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return new UnicodeEncoding(true, false, true);
            if (count >= 4 && bytes[1] == 0 && bytes[3] == 0) return new UnicodeEncoding(false, false, true);
            if (count >= 4 && bytes[0] == 0 && bytes[2] == 0) return new UnicodeEncoding(true, false, true);
            var utf8 = new UTF8Encoding(false, true);
            try { utf8.GetDecoder().GetCharCount(bytes, 0, count, false); return utf8; }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        }

        private static string[] SplitLines(string text) { return Regex.Split(text ?? String.Empty, "\r\n|\r|\n"); }
        private static string Unquote(string value)
        {
            string text = (value ?? String.Empty).Trim();
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"') text = text.Substring(1, text.Length - 2);
            return text.Replace("\\\"", "\"").Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\").Trim();
        }

        private static double HeaderNumber(Dictionary<string, string> header, string requested)
        {
            string normalized = NormalizeKey(requested);
            KeyValuePair<string, string> pair = header.FirstOrDefault(x => NormalizeKey(x.Key).Contains(normalized));
            return pair.Key == null ? double.NaN : Number(pair.Value);
        }
        private static int FirstPositive(params int[] values) { return values.Where(x => x >= 0).DefaultIfEmpty(-1).Min(); }
        private static string NormalizeKey(string value) { return Regex.Replace((value ?? String.Empty).Trim().Trim('\uFEFF').TrimEnd(':', '：'), @"[\s_\-:=：\[\]\(\)]", String.Empty).ToLowerInvariant(); }
        private static double Number(string value)
        {
            Match match = NumberPattern.Match(value ?? String.Empty); double result;
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, Inv, out result) && ProjectReportData.IsFinite(result) ? result : double.NaN;
        }
        private static ReportPhase ParsePhase(string value)
        {
            string phase = (value ?? String.Empty).Trim().ToUpperInvariant();
            return phase == "A" ? ReportPhase.A : phase == "B" ? ReportPhase.B : phase == "C" ? ReportPhase.C : ReportPhase.All;
        }
        private static void AddFinitePositive(List<double> target, double value) { if (ProjectReportData.IsFinite(value) && value > 0.0) target.Add(value); }
        private static void AddFiniteNonnegative(List<double> target, double value) { if (ProjectReportData.IsFinite(value) && value >= 0.0) target.Add(value); }
        private static double Median(List<double> values)
        {
            if (values == null || values.Count == 0) return double.NaN;
            double[] sorted = values.OrderBy(x => x).ToArray(); int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2.0 : sorted[middle];
        }

        private static Dictionary<string, string> BuildItemAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Add(map, "project.inspection_organization", "所属单位", "检测单位", "试验单位", "测试单位", "TestingOrganization");
            Add(map, "project.test_personnel", "测试人员", "试验人员", "检测人员", "TestPersonnel");
            Add(map, "project.test_location", "测试地点", "试验地点", "检测地点", "TestLocation");
            Add(map, "project.date", "日期", "Date"); Add(map, "project.time", "时间", "Time");
            Add(map, "cable.start_name", "始端地点", "始端名称", "始端", "起点地点", "StartName");
            Add(map, "cable.end_name", "终端地点", "终端名称", "终端", "终点地点", "末端名称", "EndName");
            Add(map, "cable.start_type", "始端开关类型", "始端类型", "StartType"); Add(map, "cable.end_type", "终端开关类型", "终端类型", "末端类型", "EndType");
            Add(map, "cable.length_m", "电缆长度", "电缆长度(m)", "电缆长度[m]", "CableLength");
            Add(map, "cable.installation_date", "铺设时间", "敷设时间", "敷设日期", "铺设日期", "InstallationDate");
            Add(map, "cable.model", "电缆型号", "CableModel"); Add(map, "cable.core_count", "电缆芯数", "芯数", "CoreCount");
            Add(map, "cable.line_name", "线路名称", "LineName"); Add(map, "project.notes", "备注", "Remarks");
            Add(map, "test.phase_voltage", "相电压", "相电压(kV)", "相电压[kV]", "PhaseVoltage"); Add(map, "test.voltage_mode", "加压模式", "TestMode");
            Add(map, "test.voltage_multipliers", "加压倍数", "PressureMultipliers"); Add(map, "test.voltage_repeat_counts", "加压次数", "PressureRepetitions");
            foreach (string phase in new[] { "A", "B", "C" })
            {
                string lower = phase.ToLowerInvariant();
                Add(map, "insulation." + lower + ".before_mohm", phase + "相测试前绝缘电阻", phase + "相绝缘1", phase + "相绝缘电阻1", phase + "相绝缘[MΩ]1", phase + "1");
                Add(map, "insulation." + lower + ".after_mohm", phase + "相测试后绝缘电阻", phase + "相绝缘2", phase + "相绝缘电阻2", phase + "相绝缘[MΩ]2", phase + "2");
                Add(map, "phase." + lower + ".pdiv_kv", phase + "相PDIV", phase + "相局放起始电压", phase + "相起始放电电压", phase + "PhasePDIV");
                Add(map, "phase." + lower + ".pdev_kv", phase + "相PDEV", phase + "相局放熄灭电压", phase + "相熄灭放电电压", phase + "PhasePDEV");
            }
            return map;
        }
        private static void Add(Dictionary<string, string> map, string canonical, params string[] aliases)
        {
            map[NormalizeKey(canonical)] = canonical;
            foreach (string alias in aliases) map[NormalizeKey(alias)] = canonical;
        }
        private sealed class DemAggregate
        {
            internal readonly List<double> Noise = new List<double>(), Frequency = new List<double>(), Capacitance = new List<double>(), DielectricLoss = new List<double>();
            internal int FileCount;
        }
    }
}
