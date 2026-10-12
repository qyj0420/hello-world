using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HVX.Controls
{
    public sealed class BrowserProject
    {
        public string MeasurementKind = "Cable";
        public string ProjectPath = "", ItemPath = "", CablePath = "", Title = "";
        public double CableLengthMetres = double.NaN, PhaseVoltageKv = double.NaN;
        public double[] JointPositionsMetres = new double[0];
        public readonly Dictionary<string,double[]> JointPositionsByPhase = new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
        public List<BrowserRecord> Records = new List<BrowserRecord>();
        public List<BrowserRecord> Calibrations = new List<BrowserRecord>();
        public List<BrowserRecord> Tests = new List<BrowserRecord>();
        public List<BrowserRecord> Locations = new List<BrowserRecord>();
        public Dictionary<string, string> Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings = new List<string>();
    }

    public sealed class BrowserRecord
    {
        public string MeasurementKind = "Cable";
        public string Path = "", Name = "", Kind = "", Phase = "", Information = "", Warning = "";
        public DateTime Timestamp = DateTime.MinValue;
        // One-based, chronological, within each phase. LOC inherits a same-name DEM order.
        public int Order, Repeat;
        public double VoltageKv = double.NaN, MaxPdPc = double.NaN, FirstCycleMaxPdPc = double.NaN;
        // D-losses is already a percent. Never multiply this value by 100.
        public double TanDelta = double.NaN, FrequencyHz = double.NaN, CapacitanceUf = double.NaN;
        public double SampleRateHz = double.NaN, VoltageSampleRateHz = double.NaN;
        // Unitless legacy Delta-x is not a measured time unit; a project-level stable-wave consensus may estimate the rate.
        internal double ValidatedLegacyDeltaValue = double.NaN, ValidatedProjectRateHz = double.NaN;
        public double VelocityMPerUs = double.NaN, CableLengthMetres = double.NaN;
        public bool IsValid, SamplingRateEstimated, ColumnsCorrected, FrontBlankingApplied;
        public double SwitchOnTimeSeconds, StatisticsStartSeconds = double.NaN, RawMaxPdPc = double.NaN;
        public int StatisticsStartSample = -1;
        public int SampleCount, VoltageSampleCount, PdPeakCount;
        public double PdMinimumPc = double.NaN, PdQ1Pc = double.NaN, PdMedianPc = double.NaN;
        public double PdQ3Pc = double.NaN, PdMaximumPc = double.NaN, PdMeanPc = double.NaN;
        public double PdPeakThresholdPc = double.NaN;
        public double[] PdPeakValuesPc = new double[0];
        public Dictionary<string, string> Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class BrowserWaveform
    {
        public string MeasurementKind = "Cable";
        public double[] X = new double[0], Y = new double[0], VoltageKv = new double[0], VoltageX = new double[0];
        public string XLabel = "时间 / s", YLabel = "放电量 / pC", Information = "";
        public double SampleRateHz = double.NaN, VoltageSampleRateHz = double.NaN;
        public double VoltageReferenceKv = double.NaN;
        public double VelocityMPerUs = double.NaN, CableLengthMetres = double.NaN;
        public double IncidentSeconds = double.NaN, ReflectionSeconds = double.NaN;
        public double IncidentSampleIndex = double.NaN, ReflectionSampleIndex = double.NaN;
        public bool SamplingRateEstimated, FrontBlankingApplied;
        public double SwitchOnTimeSeconds, StatisticsStartSeconds = double.NaN, RawMaxPdPc = double.NaN;
        public int StatisticsStartSample = -1;
    }

    /// <summary>Reads only the selected project's files; stored header paths are never followed.</summary>
    public static class BrowserReader
    {
        private const int MaximumSamples = 16000000;
        private const long MaximumFileBytes = 512L * 1024 * 1024;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string NumberPattern = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
        private static readonly Regex NamePattern = new Regex(@"^(ALL|A|B|C)_([-+]?[0-9]+(?:\.[0-9]+)?)kV_(\d{4}-\d{2}-\d{2}-\d{2}'\d{2}'\d{2}(?:\.\d{1,7})?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static BrowserProject LoadProject(string projectPath) { return LoadProject(projectPath, 0, CancellationToken.None); }
        public static BrowserProject LoadProject(string projectPath, CancellationToken cancellation) { return LoadProject(projectPath, 0, cancellation); }
        public static BrowserProject LoadProject(string projectPath, double switchOnTimeSeconds) { return LoadProject(projectPath, switchOnTimeSeconds, CancellationToken.None); }
        public static BrowserProject LoadProject(string projectPath, double switchOnTimeSeconds, CancellationToken cancellation)
        {
            ValidateSwitchOn(switchOnTimeSeconds);
            if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("项目路径不能为空。", "projectPath");
            string root = Path.GetFullPath(projectPath.Trim());
            if (File.Exists(root) && string.Equals(Path.GetFileName(root), "ITEM.in", StringComparison.OrdinalIgnoreCase)) root = Path.GetDirectoryName(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("项目路径不存在：" + root);
            var project = new BrowserProject { ProjectPath = root, ItemPath = Path.Combine(root, "ITEM.in"), CablePath = Path.Combine(root, "CABLE.in"), Title = Path.GetFileName(root) };
            cancellation.ThrowIfCancellationRequested();
            ReadProjectMetadata(project);
            if(HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root)) {
                project.MeasurementKind="Capacitor";project.CableLengthMetres=Double.NaN;
                project.JointPositionsMetres=new double[0];project.JointPositionsByPhase.Clear();
            }
            var paths = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(p => new[] { ".cal", ".dem", ".loc" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .Concat(HVX.CalibrationStorage.LiveCalibrationRecord.EnumerateRecordFiles(root)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                var record = NewRecord(path); record.SwitchOnTimeSeconds = switchOnTimeSeconds;
                try
                {
                    if(HVX.CalibrationStorage.LiveCalibrationRecord.IsArchiveFile(path)){record.Kind="cal";record.Metadata["LiveProjectPath"]=root;}
                    if (record.Kind == "dem") ReadDem(record, cancellation);
                    else if (record.Kind == "cal") ReadCal(record, cancellation,false);
                    else ReadLocHeader(record);
                    record.IsValid = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException || ex is InvalidOperationException)
                {
                    record.IsValid = false; AddWarning(record, ex.Message);
                }
                project.Records.Add(record);
                (record.Kind == "cal" ? project.Calibrations : record.Kind == "dem" ? project.Tests : project.Locations).Add(record);
            }
            ApplyProjectLegacyTimebase(project, cancellation);
            foreach (var phase in project.Tests.GroupBy(r => r.Phase))
            {
                var repeats = new Dictionary<string, int>(); int order = 0;
                foreach (var record in phase.OrderBy(r => r.Timestamp == DateTime.MinValue ? DateTime.MaxValue : r.Timestamp).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    record.Order = ++order;
                    string voltageKey = record.VoltageKv.ToString("R", Inv); int repeat;
                    repeats.TryGetValue(voltageKey, out repeat); record.Repeat = repeat + 1; repeats[voltageKey] = record.Repeat;
                }
            }
            var testMap = project.Tests.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var record in project.Locations)
            {
                BrowserRecord dem;
                if (testMap.TryGetValue(record.Name, out dem)) { record.Order = dem.Order; record.Repeat = dem.Repeat; }
                else AddWarning(record, "缺少同名 DEM，未分配测试加压次序。");
            }
            foreach (var record in project.Records)
            {
                record.Information = BuildInformation(record);
                if (!string.IsNullOrEmpty(record.Warning)) project.Warnings.Add(record.Name + "：" + record.Warning);
            }
            project.Tests = project.Tests.OrderBy(r => r.Phase).ThenBy(r => r.Order).ToList();
            project.Locations = project.Locations.OrderBy(r => r.Phase).ThenBy(r => r.Timestamp).ThenBy(r => r.Name).ToList();
            return project;
        }

        private static double UnitlessDelta(Dictionary<string, string> metadata)
        {
            string key = metadata.Keys.FirstOrDefault(k => NormalizeKey(k).Contains("deltax"));
            if (key == null) return double.NaN;
            string unit = (key + " " + metadata[key]).ToLowerInvariant();
            if (Regex.IsMatch(unit, @"\b(ghz|mhz|khz|hz|ns|nsec|us|usec|μs|µs|ms|msec|s|sec)\b"))
                return double.NaN;
            double value = Numeric(metadata[key]);
            return Finite(value) && value > 0 && value <= 10000 ? value : double.NaN;
        }

        private static void ApplyProjectLegacyTimebase(BrowserProject project, CancellationToken cancellation)
        {
            // The old DEM format writes Delta-x without a trustworthy unit. Derive
            // a rate only from other files with the same header value, stable bipolar
            // cycles, and plausible recorded oscillation frequency.
            var groups = project.Tests.Where(r => r.IsValid && Finite(UnitlessDelta(r.Metadata)))
                .GroupBy(r => UnitlessDelta(r.Metadata));
            foreach (var group in groups)
            {
                cancellation.ThrowIfCancellationRequested();
                double[] witnessedRates = group.Where(r => r.SamplingRateEstimated && Finite(r.VoltageSampleRateHz)
                    && r.VoltageSampleRateHz > 0 && r.FrequencyHz >= 20 && r.FrequencyHz <= 2000)
                    .Select(r => r.VoltageSampleRateHz).OrderBy(v => v).ToArray();
                if (witnessedRates.Length < 2) continue;
                double consensus = Quantile(witnessedRates, .5);
                int corroborating = witnessedRates.Count(rate => Math.Abs(rate - consensus) <= consensus * .05);
                if (corroborating < 2 || corroborating < witnessedRates.Length * .8) continue;
                foreach (var old in group.Where(r => !Finite(r.SampleRateHz)).ToArray())
                {
                    cancellation.ThrowIfCancellationRequested();
                    var updated = NewRecord(old.Path);
                    updated.SwitchOnTimeSeconds = old.SwitchOnTimeSeconds;
                    updated.ValidatedLegacyDeltaValue = group.Key;
                    updated.ValidatedProjectRateHz = consensus;
                    try
                    {
                        ReadDem(updated, cancellation);
                        updated.IsValid = true;
                        int allIndex = project.Records.IndexOf(old), testIndex = project.Tests.IndexOf(old);
                        if (allIndex >= 0) project.Records[allIndex] = updated;
                        if (testIndex >= 0) project.Tests[testIndex] = updated;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException)
                    {
                        AddWarning(old, "同项目时间基准交叉验证后重读失败：" + ex.Message);
                    }
                }
            }
        }
        public static BrowserWaveform LoadWaveform(BrowserRecord record) { return LoadWaveform(record, record == null ? 0 : record.SwitchOnTimeSeconds, CancellationToken.None); }
        public static BrowserWaveform LoadWaveform(BrowserRecord record, CancellationToken cancellation) { return LoadWaveform(record, record == null ? 0 : record.SwitchOnTimeSeconds, cancellation); }
        public static BrowserWaveform LoadWaveform(BrowserRecord record, double switchOnTimeSeconds) { return LoadWaveform(record, switchOnTimeSeconds, CancellationToken.None); }
        public static BrowserWaveform LoadWaveform(BrowserRecord record, double switchOnTimeSeconds, CancellationToken cancellation)
        {
            if (record == null) throw new ArgumentNullException("record");
            ValidateSwitchOn(switchOnTimeSeconds);
            cancellation.ThrowIfCancellationRequested();
            // Parse afresh so the waveform always reflects on-disk edits after calibration saves.
            var current = NewRecord(record.Path); current.SwitchOnTimeSeconds = switchOnTimeSeconds;
            if(HVX.CalibrationStorage.LiveCalibrationRecord.IsArchiveFile(record.Path)) {
                string root;if(!record.Metadata.TryGetValue("LiveProjectPath",out root)||String.IsNullOrWhiteSpace(root))
                    throw new InvalidDataException("实机校准缺少所属项目上下文，请重新加载项目。");
                current.Kind="cal";current.Metadata["LiveProjectPath"]=root;
            }
            current.ValidatedLegacyDeltaValue = record.ValidatedLegacyDeltaValue;
            current.ValidatedProjectRateHz = record.ValidatedProjectRateHz;
            BrowserWaveform result;
            if (current.Kind == "cal") result = ReadCal(current, cancellation);
            else if (current.Kind == "dem") result = ReadDem(current, cancellation);
            else if (current.Kind == "loc") result = ReadLoc(current, cancellation);
            else throw new InvalidDataException("不支持的波形文件扩展名。");
            current.Order = record.Order; current.Repeat = record.Repeat;
            result.Information = BuildInformation(current);
            return result;
        }

        private static BrowserRecord NewRecord(string path)
        {
            var record = new BrowserRecord { Path = Path.GetFullPath(path), Name = Path.GetFileNameWithoutExtension(path), Kind = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() };
            record.Phase = NormalizePhase(record.Name.Split('_')[0]);
            var match = NamePattern.Match(record.Name);
            if (match.Success)
            {
                record.Phase = match.Groups[1].Value.ToUpperInvariant(); record.VoltageKv = Numeric(match.Groups[2].Value);
                DateTime stamp;
                if (DateTime.TryParseExact(match.Groups[3].Value.Replace((char)39, (char)45), new[]{"yyyy-MM-dd-HH-mm-ss","yyyy-MM-dd-HH-mm-ss.FFFFFFF"}, Inv, DateTimeStyles.None, out stamp)) record.Timestamp = stamp;
                else AddWarning(record, "文件名日期时间无效，排序置于已知时间之后。");
            }
            else if (record.Kind != "cal") AddWarning(record, "无法从文件名读取电压及原始测试时间。");
            return record;
        }

        private static void ReadProjectMetadata(BrowserProject project)
        {
            if (File.Exists(project.ItemPath))
            {
                try
                {
                    using (var reader = Open(project.ItemPath))
                    {
                        string line; while ((line = reader.ReadLine()) != null)
                        {
                            int eq = line.IndexOf('='); if (eq < 0) continue;
                            string key = HVX.ProjectModel.ProjectMetadataKeys.Canonical(line.Substring(0, eq).Trim().TrimEnd(':', '：').Trim());
                            project.Metadata[key] = line.Substring(eq + 1).Trim().Trim('"');
                        }
                    }
                    // Fixed storage identifiers are independent of the selected UI
                    // language. Legacy Chinese aliases are normalized on input.
                    // Exact lookup keeps per-phase lengths from becoming the total.
                    project.CableLengthMetres = Numeric(Get(project.Metadata, "CableLength"));
                    project.PhaseVoltageKv = Numeric(Get(project.Metadata, "PhaseVoltage"));
                    string start = Get(project.Metadata, "StartName"), end = Get(project.Metadata, "EndName");
                    if (!string.IsNullOrEmpty(start + end)) project.Title += "：" + start + "—" + end;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { project.Warnings.Add("ITEM.in：" + ex.Message); }
            }
            else project.Warnings.Add("未找到 ITEM.in。");
            if (File.Exists(project.CablePath))
            {
                try
                {
                    var joints = new List<double>(); var ends = new List<double>();var positions=new List<double>();string phase="ALL";
                    Action flush=()=>{if(positions.Count<2){positions.Clear();return;}var inner=positions.Skip(1).Take(positions.Count-2).ToArray();joints.AddRange(inner);ends.Add(positions.Last());double[] prior;project.JointPositionsByPhase.TryGetValue(phase,out prior);project.JointPositionsByPhase[phase]=(prior??new double[0]).Concat(inner).Distinct().OrderBy(v=>v).ToArray();positions.Clear();};
                    using (var reader = Open(project.CablePath))
                    {
                        string line; while ((line = reader.ReadLine()) != null)
                        {
                            var cells = line.Split('\t'); if(cells.Length>=2&&cells[0].Trim()=="0"){flush();phase=NormalizePhase(cells[1]);continue;}if (cells.Length < 4 || cells[0].Trim() != "1") continue;
                            double position = Numeric(cells[3]); if (!Finite(position)) continue;
                            positions.Add(position);
                        }
                    }
                    flush();project.JointPositionsMetres = joints.Distinct().OrderBy(v => v).ToArray();
                    if (!Finite(project.CableLengthMetres) && ends.Count > 0) project.CableLengthMetres = ends.Max();
                    if (ends.Count > 0 && Finite(project.CableLengthMetres) && ends.Any(e => Math.Abs(e - project.CableLengthMetres) > .05))
                        project.Warnings.Add("ITEM 电缆长度与 CABLE 终端位置不一致；保留各源文件原值。");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { project.Warnings.Add("CABLE.in：" + ex.Message); }
            }
            else project.Warnings.Add("未找到 CABLE.in。");
        }

        private static BrowserWaveform ReadCal(BrowserRecord record, CancellationToken cancellation,bool verifyRaw=true)
        {
            CheckFile(record.Path);
            if(HVX.CalibrationStorage.LiveCalibrationRecord.IsArchiveFile(record.Path)) {
                string root;if(!record.Metadata.TryGetValue("LiveProjectPath",out root))throw new InvalidDataException("实机校准缺少项目上下文。");
                var r=HVX.CalibrationStorage.LiveCalibrationRecord.Load(root,record.Path,verifyRaw);cancellation.ThrowIfCancellationRequested();
                record.MeasurementKind=r.MeasurementKind;record.Phase=r.Phase;record.SampleRateHz=r.SampleRateHz;record.SampleCount=r.Average.Length;record.CableLengthMetres=r.IsCapacitor?double.NaN:r.LengthM;record.VelocityMPerUs=r.IsCapacitor?double.NaN:r.VelocityMPerUs;
                if(r.IsTdr)record.Metadata["记录类型"]="TDR测距；采用波速，不是电量校准";
                else record.Metadata["校准系数 pC/V"]=r.CoefficientPcPerV.ToString("G8",Inv);
                record.Metadata["滤波"]=r.FilterId+(r.BandwidthMHz>0?" / "+r.LowPassFilterId+" / "+r.BandwidthMHz+" MHz":"");
                DateTime.TryParse(r.CreatedUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out record.Timestamp);
                double[] values=r.BandwidthMHz>0?HVX.SignalProcessing.PdLinearLowPass.Apply(r.Average,1/r.SampleRateHz,r.BandwidthMHz):r.Average;
                var result=BaseWave(record,values,r.IsTdr?"测距处理后幅值 / V":"校准处理后幅值 / V");result.MeasurementKind=r.MeasurementKind;result.IncidentSeconds=r.IncidentSeconds;result.ReflectionSeconds=r.HasMeasuredReflection?r.ReflectionSeconds:double.NaN;result.IncidentSampleIndex=r.IncidentPeakIndex;result.ReflectionSampleIndex=r.HasMeasuredReflection?r.ReflectionPeakIndex:double.NaN;
                return result;
            }
            var data = new List<double>();
            using (var reader = Open(record.Path))
            {
                string[] head = ReadHeader(reader, 13); record.Metadata = Header(head,true);
                record.SampleRateHz = HVX.CalibrationStorage.LegacyCalibrationBinding.LegacySampleRate(record.Metadata);
                record.VelocityMPerUs = HeaderNumber(record.Metadata, "V (m/usec)");
                // Legacy CAL uses Delta_x as samples/second (not seconds/sample).
                // Restrict this alias to its identifiable header; never replace a
                // present but invalid SR or invent a rate from another file.
                if (!HasHeaderNumberKey(record.Metadata, "SR") && record.Metadata.ContainsKey("Delta_x") &&
                    record.Metadata.ContainsKey("NI_Calib_factor") && record.Metadata.ContainsKey("Velocity (m/usec)"))
                {
                    double legacyRate = HeaderNumber(record.Metadata, "Delta_x");
                    if (Finite(legacyRate) && legacyRate >= 1 && legacyRate <= 10000000000.0)
                    {
                        record.SampleRateHz = HVX.CalibrationStorage.LegacyCalibrationBinding.NormalizeLegacySampleRateHz(legacyRate);
                        AddWarning(record, "旧版 CAL：采样率读取自 Delta_x（Hz），未修改原文件。");
                    }
                }
                if (!HasHeaderNumberKey(record.Metadata, "V (m/usec)"))
                    record.VelocityMPerUs = HeaderNumber(record.Metadata, "Velocity (m/usec)");
                record.CableLengthMetres = HeaderNumber(record.Metadata, "Cable length (m)");
                // The positional LocationAlgorithm CAL contract also names these
                // fields exactly "Cable length" (metres) / "Velocity (m/us)".
                // Do not infer units from a generic Velocity key, and do not let
                // the alias conceal a present but invalid established field.
                if (!HasHeaderNumberKey(record.Metadata, "V (m/usec)") &&
                    !HasHeaderNumberKey(record.Metadata, "Velocity (m/usec)"))
                    record.VelocityMPerUs = ExactCalAliasNumber(record.Metadata, "Velocity (m/us)");
                if (!HasHeaderNumberKey(record.Metadata, "Cable length (m)"))
                    record.CableLengthMetres = ExactCalAliasNumber(record.Metadata, "Cable length");
                ReadSingleColumn(reader, data, 1, cancellation, 13);
            }
            if (data.Count < 16) throw new InvalidDataException("CAL 有效采样点少于16。");
            RequireRate(record.SampleRateHz, "CAL");
            record.SampleCount = data.Count;
            double incident = HVX.CalibrationStorage.LegacyCalibrationBinding.LegacyTimeSeconds(record.Metadata,"Start_point"),
                reflection = HVX.CalibrationStorage.LegacyCalibrationBinding.LegacyTimeSeconds(record.Metadata,"Final_point");
            var wave = BaseWave(record, data.ToArray(), "原始校准幅值 / V");
            wave.IncidentSeconds = incident; wave.ReflectionSeconds = reflection;
            wave.IncidentSampleIndex = Finite(incident) ? incident * record.SampleRateHz : double.NaN;
            wave.ReflectionSampleIndex = Finite(reflection) ? reflection * record.SampleRateHz : double.NaN;
            if (!Finite(incident) || !Finite(reflection) || incident < 0 || reflection <= incident || reflection * record.SampleRateHz > data.Count - 1)
                AddWarning(record, "校准游标缺失或超出波形，须重新选取入射及反射点后保存。");
            return wave;
        }

        private static BrowserWaveform ReadDem(BrowserRecord record, CancellationToken cancellation)
        {
            CheckFile(record.Path);
            var first = new List<double>(); var second = new List<double>();
            string[] head;
            using (var reader = Open(record.Path))
            {
                head = ReadHeader(reader, 16); record.Metadata = Header(head);
                string line; int row = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((row++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (row > MaximumSamples) throw new InvalidDataException("DEM 超过1600万采样点限制。");
                    string[] cells = line.Contains("\t") ? line.Split('\t') : line.Split(',');
                    first.Add(Cell(cells, 0)); second.Add(Cell(cells, 1));
                }
            }
            TrimTrailingMissing(first); TrimTrailingMissing(second);
            record.FrequencyHz = HeaderNumber(record.Metadata, "Hz");
            record.CapacitanceUf = HeaderNumber(record.Metadata, "C[uF]");
            record.TanDelta = HeaderNumber(record.Metadata, "D-losses");
            if (!Finite(record.VoltageKv)) record.VoltageKv = HeaderNumber(record.Metadata, "V-scale max");
            int voltageColumn = head[15].TrimStart().StartsWith("Voltage", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            double[] a = first.ToArray(), b = second.ToArray();
            double voltageScale = HeaderNumber(record.Metadata, "V-scale max"), pdScale = HeaderNumber(record.Metadata, "PD-scale max");
            double periodA = StablePeriod(a), periodB = StablePeriod(b);
            bool aVoltage = IsVoltageEvidence(a, b, voltageScale, pdScale, periodA);
            bool bVoltage = IsVoltageEvidence(b, a, voltageScale, pdScale, periodB);
            bool aCharge = StrongChargeScaleEvidence(a, b, pdScale, voltageScale);
            bool bCharge = StrongChargeScaleEvidence(b, a, pdScale, voltageScale);
            if ((voltageColumn == 1 && ((aVoltage && !bVoltage) || (bCharge && !aCharge)))
                || (voltageColumn == 0 && ((bVoltage && !aVoltage) || (aCharge && !bCharge))))
            {
                voltageColumn = 1 - voltageColumn; record.ColumnsCorrected = true;
                AddWarning(record, "DEM 表头列名与内容相反；依据稳定双极振荡、零压背景或两列量程强分离证据修正列映射。");
            }
            double[] voltage = voltageColumn == 0 ? a : b, pd = voltageColumn == 0 ? b : a;
            double declaredPdPeriod = voltageColumn == 0 ? periodB : periodA;
            double declaredVoltagePeriod = voltageColumn == 0 ? periodA : periodB;
            double declaredPdMaximum = AbsoluteMaximum(pd, pd.Length);
            if (!record.ColumnsCorrected && Finite(declaredPdPeriod) && !Finite(declaredVoltagePeriod)
                && voltageScale > .1 && declaredPdMaximum > voltageScale * .45 && declaredPdMaximum < voltageScale * 1.5)
                throw new InvalidDataException("DEM 列名与稳定振荡内容疑似冲突，但缺少足够量程证据；未猜测列映射，暂不纳入统计。");
            if (pd.Count(Finite) < 16) throw new InvalidDataException("DEM 有效局放采样点少于16。");
            record.SampleCount = pd.Length; record.VoltageSampleCount = voltage.Length;
            // No samples are removed from the middle: missing values retain their time slots.
            if (pd.Any(v => !Finite(v)) || voltage.Any(v => !Finite(v))) AddWarning(record, "波形含缺失采样点，已保留 NaN 及原始样点位置。");
            ResolveDemRates(record, voltage, pd, voltageColumn == 0 ? periodA : periodB);
            CalculateStoredDataStatistics(record, pd);
            var wave = BaseWave(record, pd, "放电量 / pC");
            wave.VoltageKv = voltage; wave.VoltageSampleRateHz = record.VoltageSampleRateHz;
            wave.VoltageReferenceKv = Finite(voltageScale) && voltageScale > 0 ? voltageScale : record.VoltageKv;
            wave.VoltageX = Axis(voltage.Length, record.VoltageSampleRateHz);
            return wave;
        }

        private static bool StrongChargeScaleEvidence(double[] charge, double[] other, double pdScale, double voltageScale)
        {
            // Some legacy DEMs contain one short, positive charge trace and a longer
            // low-amplitude bipolar voltage trace. A complete oscillation is not
            // available there, so use only an unambiguous separation of both scales.
            if (!Finite(pdScale) || pdScale <= 0 || !Finite(voltageScale) || voltageScale <= 0
                || charge.Count(Finite) < 16 || other.Count(Finite) < 16) return false;
            double chargeMax = AbsoluteMaximum(charge, charge.Length);
            double otherMax = AbsoluteMaximum(other, other.Length);
            int negatives = charge.Count(v => Finite(v) && v < -chargeMax * .01);
            return chargeMax >= pdScale * .25 && chargeMax <= pdScale * 2.25
                && chargeMax > voltageScale * 2.5
                && otherMax < Math.Max(voltageScale * .2, chargeMax * .1)
                && negatives < charge.Count(Finite) * .01;
        }
        private static bool IsVoltageEvidence(double[] voltage, double[] pd, double voltageScale, double pdScale, double period)
        {
            if (!Finite(voltageScale) || !Finite(pdScale) || pdScale <= 0 || pd.Count(Finite) < 16) return false;
            double vmax = AbsoluteMaximum(voltage, voltage.Length), pmax = AbsoluteMaximum(pd, pd.Length);
            bool pdScaleMatches = pmax >= pdScale * .35 && pmax <= pdScale * 1.7;
            bool mostlyPositivePd = pd.Count(v => Finite(v) && v < -pmax * .01) < pd.Count(Finite) * .01;
            // A zero-voltage background may omit voltage entirely or retain tiny ADC noise.
            if (Math.Abs(voltageScale) < .001)
                return pdScaleMatches && mostlyPositivePd && (voltage.Count(Finite) == 0 || vmax < .2) && pmax > .5;
            // Stable bipolar cycles + independently matching scales + unipolar PD evidence are required.
            // V-scale max is the selected test voltage; the first oscillation crest can be higher.
            // Retain the independent bipolar and PD-scale checks before correcting columns.
            return Finite(period) && vmax >= voltageScale * .45 && vmax <= voltageScale * 2.5
                && pdScaleMatches && mostlyPositivePd;
        }

        private static void ResolveDemRates(BrowserRecord record, double[] voltage, double[] pd, double voltagePeriod)
        {
            string key = record.Metadata.Keys.FirstOrDefault(k => NormalizeKey(k).Contains("deltax"));
            string raw = key == null ? "" : record.Metadata[key];
            string unit = (key + " " + raw).ToLowerInvariant();
            double value = Numeric(raw), rate = double.NaN;
            if (unit.Contains("mhz")) rate = value * 1e6;
            else if (unit.Contains("khz")) rate = value * 1e3;
            else if (unit.Contains("hz")) rate = value;
            else if (Regex.IsMatch(unit, @"\b(ns|nsec)\b")) rate = 1e9 / value;
            else if (Regex.IsMatch(unit, @"\b(us|usec|μs|µs)\b")) rate = 1e6 / value;
            else if (Regex.IsMatch(unit, @"\b(ms|msec)\b")) rate = 1e3 / value;
            else if (Regex.IsMatch(unit, @"\b(s|sec)\b") || (value > 0 && value < .001)) rate = 1 / value;
            if (Finite(rate) && rate > 0) { record.SampleRateHz = record.VoltageSampleRateHz = rate; return; }
            if (Finite(record.ValidatedProjectRateHz) && record.ValidatedProjectRateHz > 0
                && Finite(record.ValidatedLegacyDeltaValue) && Finite(value)
                && Math.Abs(value - record.ValidatedLegacyDeltaValue) <= Math.Max(1e-9, value * 1e-9))
            {
                record.SampleRateHz = record.VoltageSampleRateHz = record.ValidatedProjectRateHz;
                record.SamplingRateEstimated = true;
                AddWarning(record, "DEM Delta-x 未注明单位；采样率由本项目至少两条稳定振荡记录的周期与频率交叉验证后估算。");
                return;
            }
            if (Finite(voltagePeriod) && Finite(record.FrequencyHz) && record.FrequencyHz > 0 && voltage.Length > 1 && pd.Length > 1)
            {
                record.VoltageSampleRateHz = voltagePeriod * record.FrequencyHz;
                // Both values in one DEM row share the sample interval; different
                // column lengths do not imply that their durations are equal.
                record.SampleRateHz = record.VoltageSampleRateHz;
                record.SamplingRateEstimated = true;
                AddWarning(record, "DEM Delta-x 未注明单位；时间轴由稳定电压过零周期和 Hz 估算，各列按自身有效长度显示（估算值）。");
            }
            else AddWarning(record, "DEM 未提供明确采样单位且不能可靠估计周期；横轴使用采样点，首周期指标不可用。");
        }

        private static double StablePeriod(double[] values)
        {
            var finite = values.Where(Finite).ToArray(); if (finite.Length < 64) return double.NaN;
            double max = finite.Max(), min = finite.Min(); if (max <= 0 || min >= 0 || Math.Min(max, -min) < Math.Max(max, -min) * .15) return double.NaN;
            var crossings = new List<double>(); bool armed = false;
            double hysteresis = Math.Max(max, -min) * .05;
            for (int i = 1; i < values.Length; i++)
            {
                if (!Finite(values[i - 1]) || !Finite(values[i])) { armed = false; continue; }
                if (values[i - 1] < -hysteresis) armed = true;
                if (armed && values[i - 1] <= 0 && values[i] > 0)
                {
                    crossings.Add(i - 1 + (-values[i - 1]) / (values[i] - values[i - 1])); armed = false;
                }
            }
            if (crossings.Count < 5) return double.NaN;
            var periods = crossings.Skip(1).Select((v, i) => v - crossings[i]).ToArray();
            double median = Quantile(periods.OrderBy(v => v).ToArray(), .5);
            if (median < 8 || periods.Count(p => Math.Abs(p - median) <= median * .04) < periods.Length * .9) return double.NaN;
            return median;
        }

        private static void CalculateStoredDataStatistics(BrowserRecord record, double[] pd)
        {
            // Input files already contain the user's preprocessing. Never apply a second front gate.
            // SwitchOnTimeSeconds remains metadata for API compatibility and does not crop statistics.
            record.FrontBlankingApplied = false;
            record.StatisticsStartSeconds = 0; record.StatisticsStartSample = 0;
            record.RawMaxPdPc = AbsoluteMaximum(pd, pd.Length);
            record.MaxPdPc = record.RawMaxPdPc;
            FillPeakStatistics(record, pd);
            if (Finite(record.SampleRateHz) && record.SampleRateHz > 0 && Finite(record.FrequencyHz) && record.FrequencyHz > 0)
            {
                double cycleSamples = Math.Ceiling(record.SampleRateHz / record.FrequencyHz);
                if (Finite(cycleSamples) && cycleSamples > 0)
                    record.FirstCycleMaxPdPc = AbsoluteMaximum(pd, (int)Math.Min(pd.Length, cycleSamples));
            }
        }
        private static void ValidateSwitchOn(double switchOnTimeSeconds)
        {
            if (!Finite(switchOnTimeSeconds) || switchOnTimeSeconds < 0)
                throw new ArgumentOutOfRangeException("switchOnTimeSeconds", "导通时间必须为非负的有限秒数。");
        }
        private static void FillPeakStatistics(BrowserRecord record, double[] pd)
        {
            var amplitudes = pd.Where(Finite).Select(Math.Abs).OrderBy(v => v).ToArray();
            if (amplitudes.Length == 0) return;
            double median = Quantile(amplitudes, .5);
            double mad = Quantile(amplitudes.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray(), .5);
            double threshold = median + 6 * 1.4826 * mad; record.PdPeakThresholdPc = threshold;
            var peaks = new List<double>(); double pending = double.NaN;
            foreach (double sample in pd)
            {
                double value = Math.Abs(sample);
                if (Finite(value) && value > threshold) pending = Finite(pending) ? Math.Max(pending, value) : value;
                else if (Finite(pending)) { peaks.Add(pending); pending = double.NaN; }
            }
            if (Finite(pending)) peaks.Add(pending);
            record.PdPeakValuesPc = peaks.ToArray(); record.PdPeakCount = peaks.Count;
            if (peaks.Count == 0) return;
            var sorted = peaks.OrderBy(v => v).ToArray();
            record.PdMinimumPc = sorted[0]; record.PdMaximumPc = sorted[sorted.Length - 1];
            record.PdQ1Pc = Quantile(sorted, .25); record.PdMedianPc = Quantile(sorted, .5); record.PdQ3Pc = Quantile(sorted, .75);
            record.PdMeanPc = sorted.Average();
        }

        private static void ReadLocHeader(BrowserRecord record)
        {
            CheckFile(record.Path);
            using (var reader = Open(record.Path))
            {
                record.Metadata = Header(ReadHeader(reader, 11),true);
                record.CableLengthMetres = HeaderNumber(record.Metadata, "Cable-length");
                record.VelocityMPerUs = HeaderNumber(record.Metadata, "Velocity");
                record.FrequencyHz = HeaderNumber(record.Metadata, "F(Hz)");
                record.TanDelta = HeaderNumber(record.Metadata, "Loss");
                record.SampleRateHz = LocRate(record.Metadata);
                RequireRate(record.SampleRateHz, "LOC");
                // Probe only 16 samples at project load; the large body is loaded on selection.
                for (int i = 0; i < 16; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null || !Finite(StrictNumber(FirstCell(line)))) throw new InvalidDataException("LOC 波形不足16个有效采样点。");
                }
            }
        }

        private static BrowserWaveform ReadLoc(BrowserRecord record, CancellationToken cancellation)
        {
            ReadLocHeader(record); var samples = new List<double>();
            using (var reader = Open(record.Path))
            {
                ReadHeader(reader, 11);
                // Reuses PdReview's storage convention: calibrated charge * 100000, no CAL reapplication.
                ReadSingleColumn(reader, samples, 100000, cancellation, 11);
            }
            if (samples.Count < 16) throw new InvalidDataException("LOC 波形不足16个有效采样点。");
            record.SampleCount = samples.Count;
            return BaseWave(record, samples.ToArray(), "原始定位波形 / pC");
        }

        private static double LocRate(Dictionary<string, string> header)
        {
            double rate=double.NaN;
            foreach(var entry in header){
                string key=NormalizeKey(entry.Key);
                if(!key.Contains("deltax")&&!key.Contains("samplerate")&&!key.Contains("samplingrate")&&key!="sr")continue;
                double candidate=ParseLocRate(entry.Key,entry.Value);
                if(!Finite(candidate)||candidate<=0)throw new InvalidDataException("LOC采样率字段无效。");
                if(Finite(rate)&&Math.Abs(rate-candidate)>1e-9*Math.Max(rate,candidate))
                    throw new InvalidDataException("LOC采样率字段冲突，未选择其中一个时间轴。");
                rate=candidate;
            }
            return rate;
        }
        private static double ParseLocRate(string key,string raw)
        {
            string unit = (key + " " + raw).ToLowerInvariant(); double value = Numeric(raw);
            if(!Finite(value)||value<=0)return double.NaN;
            if (unit.Contains("ghz")) return value * 1e9;
            if (unit.Contains("mhz")) return value * 1e6;
            if (unit.Contains("khz")) return value * 1e3;
            if (unit.Contains("hz")) return value;
            if (Regex.IsMatch(unit, @"\b(ns|nsec)\b")) return 1e9 / value;
            if (Regex.IsMatch(unit, @"\b(us|usec|μs|µs)\b")) return 1e6 / value;
            if (Regex.IsMatch(unit, @"\b(ms|msec)\b")) return 1e3 / value;
            if (Regex.IsMatch(unit, @"\b(s|sec)\b")) return 1 / value;
            return value < .001 ? 1 / value : value < 1000 ? 1e9 / value : value;
        }

        private static BrowserWaveform BaseWave(BrowserRecord record, double[] samples, string yLabel)
        {
            return new BrowserWaveform { Y = samples, X = Axis(samples.Length, record.SampleRateHz), SampleRateHz = record.SampleRateHz,
                VoltageSampleRateHz = record.VoltageSampleRateHz, CableLengthMetres = record.CableLengthMetres, VelocityMPerUs = record.VelocityMPerUs,
                SamplingRateEstimated = record.SamplingRateEstimated, FrontBlankingApplied = record.FrontBlankingApplied,
                SwitchOnTimeSeconds = record.SwitchOnTimeSeconds, StatisticsStartSeconds = record.StatisticsStartSeconds, StatisticsStartSample = record.StatisticsStartSample, RawMaxPdPc = record.RawMaxPdPc, XLabel = Finite(record.SampleRateHz) ? (record.SamplingRateEstimated ? "估算时间 / s" : "时间 / s") : "采样点", YLabel = yLabel };
        }

        private static string BuildInformation(BrowserRecord record)
        {
            var text = new StringBuilder(); text.AppendLine(record.Name);
            text.AppendLine("相别：" + record.Phase + (record.Timestamp == DateTime.MinValue ? "" : "；时间：" + record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", Inv)));
            if (record.Kind == "cal") text.AppendLine("校准长度：" + Fmt(record.CableLengthMetres) + " m；波速：" + Fmt(record.VelocityMPerUs) + " m/μs；采样率：" + Fmt(record.SampleRateHz) + " Hz");
            else
            {
                text.AppendLine("加压次序：" + (record.Order > 0 ? record.Order.ToString(Inv) : "未分配") + "；电压：" + Fmt(record.VoltageKv) + " kV；该电压第 " + record.Repeat + " 次");
                text.AppendLine("频率：" + Fmt(record.FrequencyHz) + " Hz；介损：" + Fmt(record.TanDelta) + " %");
                if (record.Kind == "dem")
                {
                    text.AppendLine("最大放电量：" + Fmt(record.MaxPdPc) + " pC");
                }
            }
            if (!string.IsNullOrEmpty(record.Warning)) text.AppendLine("提示：" + record.Warning);
            text.Append("文件：" + record.Path); return text.ToString();
        }

        private static void ReadSingleColumn(StreamReader reader, List<double> output, double divisor, CancellationToken cancellation, int headerLines)
        {
            string line; int lineNumber = headerLines;bool trailingBlank=false;
            while ((line = reader.ReadLine()) != null)
            {
                if ((lineNumber++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)){trailingBlank=true;continue;}
                if(trailingBlank)throw new InvalidDataException("第 "+lineNumber+" 行之前缺少采样值，未移动后续数据的时间位置。");
                if (output.Count >= MaximumSamples) throw new InvalidDataException("波形超过1600万采样点限制。");
                double value = StrictNumber(FirstCell(line));
                if (!Finite(value)) throw new InvalidDataException("第 " + lineNumber + " 行波形采样值无效。");
                value /= divisor; if (Math.Abs(value) > 1e100) throw new InvalidDataException("波形超出显示数值范围。");
                output.Add(value);
            }
        }
        private static void CheckFile(string path) { var file = new FileInfo(path); if (file.Length == 0) throw new InvalidDataException("文件为空。"); if (file.Length > MaximumFileBytes) throw new InvalidDataException("文件超过512 MiB限制。"); }
        private static string[] ReadHeader(StreamReader reader, int count) { var lines = new string[count]; for (int i = 0; i < count; i++) { lines[i] = reader.ReadLine(); if (lines[i] == null) throw new InvalidDataException("文件头不足 " + count + " 行。"); } return lines; }
        private static Dictionary<string, string> Header(string[] lines,bool rejectDuplicates=false)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines) { int split = line.IndexOf('\t'); if (split < 0) split = line.IndexOfAny(new[] { ':', '：', '=' }); if (split < 0) continue;
                string key=line.Substring(0, split).Trim().TrimEnd(':', '：', '=').Trim();
                if(rejectDuplicates&&result.ContainsKey(key))throw new InvalidDataException("数据头含重复字段，不能确认其含义。");
                result[key] = line.Substring(split + 1).Trim(); }
            return result;
        }
        private static StreamReader Open(string path)
        {
            HVX.Storage.PressureRecordPublication.RequireReadable(path);
            byte[] probe = new byte[8192]; int count; using (var stream = File.OpenRead(path)) count = stream.Read(probe, 0, probe.Length);
            Encoding encoding = new UTF8Encoding(false, true);
            try { encoding.GetDecoder().GetCharCount(probe, 0, count, false); }
            catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            return new StreamReader(path, encoding, true);
        }
        private static double HeaderNumber(Dictionary<string, string> header, string requested)
        {
            string value; if (header.TryGetValue(requested, out value)) return Numeric(value);
            string normalized = NormalizeKey(requested), key = header.Keys.FirstOrDefault(k => NormalizeKey(k).Contains(normalized));
            return key == null ? double.NaN : Numeric(header[key]);
        }
        private static bool HasHeaderNumberKey(Dictionary<string, string> header, string requested)
        { return header.ContainsKey(requested) || header.Keys.Any(k => NormalizeKey(k).Contains(NormalizeKey(requested))); }
        private static double ExactCalAliasNumber(Dictionary<string, string> header, string key)
        { string value; return header.TryGetValue(key, out value) ? StrictNumber(value) : double.NaN; }
        private static string Get(Dictionary<string, string> values, string key) { string value; return values.TryGetValue(key, out value) ? value : ""; }
        private static string NormalizeKey(string value) { return Regex.Replace((value ?? "").ToLowerInvariant(), @"[\s_\-:=：]", ""); }
        private static string NormalizePhase(string value) { string phase = (value ?? "").Trim().ToUpperInvariant(); return phase.StartsWith("ALL") ? "ALL" : phase.StartsWith("A") ? "A" : phase.StartsWith("B") ? "B" : phase.StartsWith("C") ? "C" : phase; }
        private static string FirstCell(string value) { value = value.Trim(); int split = value.IndexOfAny(new[] { '\t', ',', ' ' }); return (split < 0 ? value : value.Substring(0, split)).Trim(); }
        private static double Cell(string[] cells, int index) { return index < cells.Length ? StrictNumber(cells[index]) : double.NaN; }
        private static double StrictNumber(string value) { double result; return double.TryParse((value ?? "").Trim(), NumberStyles.Float, Inv, out result) && Finite(result) ? result : double.NaN; }
        private static double Numeric(string value) { Match match = Regex.Match(value ?? "", NumberPattern); return match.Success ? StrictNumber(match.Value) : double.NaN; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string Fmt(double value) { return Finite(value) ? value.ToString("0.###", Inv) : "—"; }
        private static void RequireRate(double rate, string kind) { if (!Finite(rate) || rate <= 0) throw new InvalidDataException(kind + " 采样率无效或缺失。"); }
        private static void AddWarning(BrowserRecord record, string warning) { if (record.Warning.IndexOf(warning, StringComparison.Ordinal) < 0) record.Warning += (record.Warning.Length == 0 ? "" : "；") + warning; }
        private static void TrimTrailingMissing(List<double> data) { while (data.Count > 0 && !Finite(data[data.Count - 1])) data.RemoveAt(data.Count - 1); }
        private static double[] Axis(int count, double rate) { var result = new double[count]; bool time = Finite(rate) && rate > 0; for (int i = 0; i < count; i++) result[i] = time ? i / rate : i; return result; }
        private static double AbsoluteMaximum(double[] values, int count) { double result = double.NaN; for (int i = 0; i < Math.Min(values.Length, count); i++) if (Finite(values[i])) result = Finite(result) ? Math.Max(result, Math.Abs(values[i])) : Math.Abs(values[i]); return result; }
        private static double Quantile(double[] sorted, double p) { if (sorted.Length == 0) return double.NaN; double index = (sorted.Length - 1) * p; int lo = (int)Math.Floor(index), hi = (int)Math.Ceiling(index); return sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo); }
    }
}
