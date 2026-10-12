using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HVX.Controls
{
    internal static partial class ProjectCreationStorage
    {
        static readonly Encoding Gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        internal static string SaveNew(ProjectDraft draft, string dataRoot)
        {
            if (string.IsNullOrWhiteSpace(dataRoot)) throw new InvalidOperationException("请先设置数据根目录。");
            string root = Path.GetFullPath(dataRoot);
            Directory.CreateDirectory(root);
            RejectReparsePoint(root);
            string lineFolder = SafeName((string.IsNullOrWhiteSpace(draft.StartName) ? "始端" : draft.StartName) + "-" +
                                         (string.IsNullOrWhiteSpace(draft.EndName) ? "终端" : draft.EndName));
            string parent = Path.Combine(root, lineFolder);
            Directory.CreateDirectory(parent);
            RejectReparsePoint(parent);
            string baseName = draft.TestDateTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string final = UniqueDirectory(parent, baseName);
            string staging = Path.Combine(parent, ".project-creating-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                WriteProjectFiles(staging, draft);
                Directory.Move(staging, final);
                return final;
            }
            catch
            {
                TryDeleteEmptyStaging(staging);
                throw;
            }
        }

        internal static string UpdateExisting(ProjectDraft draft, string projectPath)
        {
            LoadedProjectBaseline baseline;
            if (draft == null || !loadedBaselines.TryGetValue(draft, out baseline) ||
                !String.Equals(baseline.Directory, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("项目状态未确认，请重新加载后保存。");
            string cableHash, itemHash;
            string result = UpdateExisting(draft, projectPath, baseline.CableHash, baseline.ItemHash, out cableHash, out itemHash);
            baseline.CableHash = cableHash; baseline.ItemHash = itemHash;
            return result;
        }

        internal static string UpdateMeasuredMetadata(ProjectDraft draft, string projectPath, string expectedCableHash, string expectedItemHash, out string committedItemHash)
        {
            committedItemHash = null;
            string directory = Path.GetFullPath(projectPath), item = Path.Combine(directory, "ITEM.in");
            if (String.IsNullOrEmpty(expectedCableHash) || String.IsNullOrEmpty(expectedItemHash))
                throw new InvalidOperationException("项目状态未确认，请重新加载后保存。");
            HVX.ProjectCables.CableJointStore.Read(directory); // Reject linked directories and invalid metadata before creating a lock.
            string lockPath = Path.Combine(directory, ".DataBrowser.write.lock");
            string token = Guid.NewGuid().ToString("N");
            string stage = Path.Combine(directory, ".im-" + token + ".tmp");
            string backup = Path.Combine(directory, "ITEM.before-metadata-" + token + ".bak");
            HVX.ProjectCables.CableJointStore.RequireWritePathLengths(stage, backup, lockPath);
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("项目编辑锁不能是链接。");
            using (var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    Action unchanged = delegate
                    {
                        var current = HVX.ProjectCables.CableJointStore.Read(directory);
                        if (HVX.ProjectCables.CableJointStore.Hash(current.Original) != expectedCableHash || current.ItemHash != expectedItemHash)
                            throw new InvalidOperationException("项目已被其他操作修改，请重新加载，未覆盖其他修改。");
                    };
                    unchanged();
                    var currentDraft = Load(directory);
                    foreach (var pair in ParseItem(ReadCompatible(item)))
                        if (!draft.UnknownItemValues.ContainsKey(pair.Key)) draft.UnknownItemValues[pair.Key] = pair.Value;
                    File.WriteAllBytes(stage, BuildExistingItemBytes(directory, draft, currentDraft));
                    string replacementHash = ProjectFileHash(stage);
                    unchanged();
                    if (replacementHash == expectedItemHash) { committedItemHash = replacementHash; return directory; }
                    File.Replace(stage, item, backup, false);
                    committedItemHash = replacementHash;
                    return directory;
                }
                finally { if (File.Exists(stage)) File.Delete(stage); }
            }
        }

        static void WriteProjectFiles(string directory, ProjectDraft draft)
        {
            WriteText(Path.Combine(directory, "ITEM.in"), BuildItem(draft));
            WriteText(Path.Combine(directory, "CABLE.in"), BuildCable(draft));
        }

        static void WriteText(string path, string text)
        {
            byte[] bytes = EncodeProjectText(text);
            File.WriteAllBytes(path, bytes);
        }
        // New/edited metadata is explicitly Unicode, independent of Windows'
        // ANSI code page. English protocol keys are stable across UI languages;
        // old Chinese keys remain read aliases. Unedited legacy cable bytes stay untouched.
        static byte[] EncodeProjectText(string text)
        {
            HVX.ProjectCables.BoundedProjectMetadata.ValidateText(text);
            var encoding=new UTF8Encoding(true,true);
            if (encoding.GetByteCount(text) > HVX.ProjectCables.BoundedProjectMetadata.MaximumBytes - encoding.GetPreamble().Length)
                throw new HVX.ProjectCables.ProjectMetadataLimitException();
            return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        }

        // A formal calibration binds the original non-mutable ITEM lines, not
        // merely their parsed values. Renaming aliases, adding absent defaults
        // or reordering an unchanged legacy file would invalidate that evidence.
        // Preserve its syntax and apply actual editor deltas only. Detecting a
        // physical container here grants no calibration validity: all consumers
        // still validate its scope, payload, provenance and acquisition facts.
        static byte[] BuildExistingItemBytes(string directory, ProjectDraft draft, ProjectDraft currentDraft)
        {
            if (!HasFormalCalibrationContainer(directory)) return EncodeProjectText(BuildItem(draft));
            string item = Path.Combine(directory, "ITEM.in"); byte[] originalBytes;
            string originalText = ReadCompatible(item, out originalBytes);
            var before = ParseItem(BuildItem(currentDraft));
            var desired = ParseItem(BuildItem(draft));
            var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in desired) {
                string value;
                if (!before.TryGetValue(pair.Key, out value) || !String.Equals(value, pair.Value, StringComparison.Ordinal))
                    changed[pair.Key] = pair.Value;
            }
            if (changed.Count == 0) return originalBytes;
            string[] parts = System.Text.RegularExpressions.Regex.Split(originalText, "(\\r\\n|\\n|\\r)");
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < parts.Length; index += 2) {
                string line = parts[index], trimmed = line.Trim();
                if (trimmed.StartsWith(";", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('='); if (equals < 0) continue;
                string key = NormalizeKey(line.Substring(0, equals)), value;
                if (!changed.TryGetValue(key, out value)) continue;
                // Update every spelling of an edited duplicate key. Leaving an
                // older trailing alias would otherwise shadow the user's edit.
                parts[index] = line.Substring(0, equals + 1) + " \"" + value + "\"";
                found.Add(key);
            }
            string output = String.Concat(parts);
            string newline = originalText.Contains("\r\n") ? "\r\n" : originalText.Contains("\n") ? "\n" : "\r\n";
            foreach (var pair in changed.Where(pair => !found.Contains(pair.Key))) {
                if (output.Length > 0 && !output.EndsWith("\n", StringComparison.Ordinal) && !output.EndsWith("\r", StringComparison.Ordinal)) output += newline;
                output += pair.Key + ": = \"" + pair.Value + "\"" + newline;
            }
            return String.Equals(output, originalText, StringComparison.Ordinal) ? originalBytes : EncodeProjectText(output);
        }

        static bool HasFormalCalibrationContainer(string directory)
        {
            var paths = Directory.EnumerateFiles(directory, "*.cal", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, "*.hvcal", SearchOption.TopDirectoryOnly));
            string legacy = Path.Combine(directory, "Calibration", "Live");
            if (Directory.Exists(legacy)) {
                RejectReparsePoint(Path.GetDirectoryName(legacy)); RejectReparsePoint(legacy);
                paths = paths.Concat(Directory.EnumerateFiles(legacy, "*.hvcal", SearchOption.TopDirectoryOnly));
            }
            foreach (string path in paths) {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("校准文件不能是链接。");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    byte[] magic = new byte[8]; if (stream.Read(magic, 0, magic.Length) != magic.Length) continue;
                    string marker = Encoding.ASCII.GetString(magic);
                    if (marker == "HVCLIVE1" || marker == "HVCLIVE2" || marker == "HVCLIVE3") return true;
                }
            }
            return false;
        }

        static string BuildItem(ProjectDraft draft)
        {
            List<string> lines = new List<string> { "[ITEM]" };
            Add(lines, "所属单位", draft.Organization);
            Add(lines, "测试人员", draft.OperatorName);
            Add(lines, "测试地点", draft.TestLocation);
            Add(lines, "日期", draft.TestDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(lines, "时间", draft.TestDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            Add(lines, "始端地点", draft.StartName);
            Add(lines, "终端地点", draft.EndName);
            Add(lines, "始端开关类型", draft.StartType);
            Add(lines, "终端开关类型", draft.EndType);
            Add(lines, "电缆长度", ProjectText.Number(draft.SummaryLength));
            Add(lines, "A相电缆长度", ProjectText.Number(draft.LengthA));
            Add(lines, "B相电缆长度", ProjectText.Number(draft.LengthB));
            Add(lines, "C相电缆长度", ProjectText.Number(draft.LengthC));
            Add(lines, "铺设时间", draft.LayingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Add(lines, "相电压", draft.PhaseVoltage);
            Add(lines, "电缆芯数", draft.CableMode);
            Add(lines, "备注", draft.Notes);
            Add(lines, "A相绝缘电阻1", draft.InsulationA1);
            Add(lines, "A相绝缘电阻2", draft.InsulationA2);
            Add(lines, "B相绝缘电阻1", draft.InsulationB1);
            Add(lines, "B相绝缘电阻2", draft.InsulationB2);
            Add(lines, "C相绝缘电阻1", draft.InsulationC1);
            Add(lines, "C相绝缘电阻2", draft.InsulationC2);
            Add(lines, "绝缘参考标准", draft.InsulationReferenceProfile);
            Add(lines, "绝缘测量条件一致", draft.InsulationConditionsComparable ? "1" : "0");
            Add(lines, "线路名称：", draft.LineName);
            Add(lines, "电缆型号：", draft.CableModel);
            Add(lines, "调度号2：", draft.DispatchNumberEnd);
            Add(lines, "调度号1：", draft.DispatchNumberStart);
            Add(lines, "温度：", ProjectText.Number(draft.Temperature));
            Add(lines, "天气：", draft.Weather);
            Add(lines, "天气代码：", draft.WeatherCode < 0 ? string.Empty : draft.WeatherCode.ToString(CultureInfo.InvariantCulture));
            Add(lines, "天气来源：", draft.WeatherProvider);
            Add(lines, "天气更新时间：", draft.WeatherUpdatedAt == DateTime.MinValue ? string.Empty : draft.WeatherUpdatedAt.ToString("o", CultureInfo.InvariantCulture));
            Add(lines, "湿度：", ProjectText.Number(draft.Humidity));
            Add(lines, "北斗纬度：", draft.Latitude.ToString("0.000000", CultureInfo.InvariantCulture));
            Add(lines, "北斗经度：", draft.Longitude.ToString("0.000000", CultureInfo.InvariantCulture));
            Add(lines, "北斗海拔：", ProjectText.Number(draft.Altitude));
            Add(lines, "北斗定位状态：", draft.BeidouFixStatus);
            Add(lines, "硬件采样时间：", draft.HardwareSampleTime == DateTime.MinValue ? string.Empty : draft.HardwareSampleTime.ToString("o", CultureInfo.InvariantCulture));
            Add(lines, "温湿度数据有效：", draft.EnvironmentSampleValid ? "1" : "0");
            Add(lines, "温湿度采样时间：", draft.EnvironmentSampleTime == DateTime.MinValue ? string.Empty : draft.EnvironmentSampleTime.ToString("o", CultureInfo.InvariantCulture));
            Add(lines, "北斗数据有效：", draft.GnssFixValid ? "1" : "0");
            Add(lines, "北斗海拔有效：", draft.GnssAltitudeValid ? "1" : "0");
            Add(lines, "北斗UTC时间：", draft.GnssSampleTime == DateTime.MinValue ? string.Empty : draft.GnssSampleTime.ToString("o", CultureInfo.InvariantCulture));
            Add(lines, "双端：", draft.DualEnded ? "1" : "0");
            Add(lines, "加压模式：", draft.PressureMode);
            bool freeValue = ProjectPressureModes.Normalize(draft.PressureMode) == ProjectPressureModes.Arbitrary;
            Add(lines, "加压倍数：", freeValue ? string.Empty : string.Join(",", draft.PressureSteps.Select(step => ProjectText.Number(step.Multiplier))));
            Add(lines, "加压次数：", freeValue ? string.Empty : string.Join(",", draft.PressureSteps.Select(step => step.Repetitions.ToString(CultureInfo.InvariantCulture))));
            Add(lines, "项目建立控件版本", "1.1.0");
            HashSet<string> known = new HashSet<string>(lines.Skip(1).Select(LineKey), StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in draft.UnknownItemValues)
                if (!known.Contains(NormalizeKey(pair.Key)))
                {
                    // The ITEM reader removes only the outer quotes. Unknown
                    // values are opaque vendor data, not editable UI text: keep
                    // embedded quotes and tabs exactly as parsed.
                    string value = pair.Value ?? string.Empty;
                    if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                        throw new InvalidOperationException("原项目扩展字段包含换行，未改写该项目。");
                    lines.Add(NormalizeKey(pair.Key) + ": = \"" + value + "\"");
                }
            return string.Join("\r\n", lines) + "\r\n";
        }

        static string BuildCable(ProjectDraft draft)
        {
            List<string> lines = new List<string>();
            foreach (string phase in draft.ActivePhases)
            {
                double length = draft.LengthFor(phase);
                lines.Add("0\t" + phase.Replace("相", String.Empty) + "\t\t");
                lines.Add("1\tStart\t" + CleanCell(draft.StartType) + "\t0m");
                IEnumerable<JointData> phaseJoints = draft.Joints.Where(joint => joint.Phase == phase).OrderBy(joint => joint.Position);
                string firstMaterial = draft.FirstMaterialFor(phase);
                if (string.IsNullOrWhiteSpace(firstMaterial)) firstMaterial = "塑料绝缘";
                lines.Add("2\tBody\t" + CleanCell(firstMaterial) + "\t");
                int index = 1;
                foreach (JointData joint in phaseJoints)
                {
                    string name = string.IsNullOrWhiteSpace(joint.Name) ? "Joint " + index.ToString(CultureInfo.InvariantCulture) : joint.Name;
                    lines.Add("1\t" + CleanCell(name) + "\t" + CleanCell(joint.AttachmentType) + "\t" + ProjectText.Number(joint.Position) + "m");
                    lines.Add("2\tBody\t" + CleanCell(joint.Material) + "\t");
                    index++;
                }
                lines.Add("1\tEnd\t" + CleanCell(draft.EndType) + "\t" + ProjectText.Number(length) + "m");
            }
            return string.Join("\r\n", lines) + "\r\n";
        }

        static void Add(List<string> lines, string key, string value)
        {
            lines.Add(NormalizeKey(key) + ": = \"" + CleanValue(value) + "\"");
        }

        static string CleanValue(string value)
        {
            string text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0) throw new InvalidOperationException("项目字段不能包含制表符或换行符。");
            return text.Replace("\"", "''");
        }

        static string CleanCell(string value)
        {
            return CleanValue(value).Replace("\t", " ");
        }

        static string LineKey(string line)
        {
            int equals = line.IndexOf('=');
            return NormalizeKey(equals >= 0 ? line.Substring(0, equals) : line);
        }

        internal static ProjectDraft Load(string projectPath)
        {
            string directory = Path.GetFullPath(projectPath ?? string.Empty);
            RejectReparsePoint(directory);
            string itemPath = Path.Combine(directory, "ITEM.in");
            string cablePath = Path.Combine(directory, "CABLE.in");
            if (!File.Exists(itemPath) || !File.Exists(cablePath))
                throw new InvalidOperationException("所选目录缺少 ITEM.in 或 CABLE.in。");
            byte[] itemBytes, cableBytes;
            string itemText = ReadCompatible(itemPath, out itemBytes), cableText = ReadCompatible(cablePath, out cableBytes);
            string itemHash = HVX.ProjectCables.CableJointStore.Hash(itemBytes), cableHash = HVX.ProjectCables.CableJointStore.Hash(cableBytes);
            Dictionary<string, string> values = ParseItem(itemText);
            ProjectDraft draft = FromItem(values);
            ParseCable(cableText, draft);
            ApplyStrictCableProjection(directory, draft);
            InferMode(draft, values);
            if (draft.PressureMode == ProjectPressureModes.Arbitrary) draft.PressureSteps.Clear();
            else if (draft.PressureSteps.Count == 0) draft.PressureSteps.AddRange(ReferenceCatalog.CreateDefaults().GetPressureSteps(draft.PressureMode));
            if (itemHash != ProjectFileHash(itemPath) || cableHash != ProjectFileHash(cablePath))
                throw new IOException("项目在加载过程中被其他操作修改，请重新加载。");
            loadedBaselines.Add(draft, new LoadedProjectBaseline { Directory = directory, ItemHash = itemHash, CableHash = cableHash });
            return draft;
        }

        static string ReadCompatible(string path)
        {
            byte[] bytes; return ReadCompatible(path, out bytes);
        }
        static string ReadCompatible(string path, out byte[] bytes)
        {
            bytes = HVX.ProjectCables.BoundedProjectMetadata.ReadBytes(path);
            string text;
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) text = new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) text = new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
            else
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException)
            {
                text = Gbk.GetString(bytes);
            }
            return HVX.ProjectCables.BoundedProjectMetadata.ValidateText(text);
        }

        static void ApplyStrictCableProjection(string directory, ProjectDraft draft)
        {
            HVX.ProjectCables.CableJointSnapshot snapshot;
            try { snapshot = HVX.ProjectCables.CableJointStore.Read(directory); }
            catch (InvalidDataException) { return; } // Keep legacy browsing; strict edits still refuse it.
            catch (DecoderFallbackException) { return; }
            draft.Joints.Clear(); draft.FirstSegmentMaterials.Clear();
            var seenPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in snapshot.Blocks)
            {
                string phase = block.Phase == "ALL" ? "A相" : block.Phase + "相";
                draft.SetLength(phase, seenPhases.Add(phase) ? block.Length : Math.Max(draft.LengthFor(phase), block.Length));
                string material = snapshot.Lines[block.BodyLines[0]].Split('\t')[2];
                if (!draft.FirstSegmentMaterials.ContainsKey(phase)) draft.FirstSegmentMaterials[phase] = material;
                if (block.Index == 0) draft.DefaultMaterial = material;
                for (int i = 1; i < block.AccessoryLines.Count - 1; i++)
                {
                    string[] row = snapshot.Lines[block.AccessoryLines[i]].Split('\t');
                    draft.Joints.Add(new JointData { Phase = phase, Name = row[1], AttachmentType = row[2], Position = block.Positions[i],
                        Material = snapshot.Lines[block.BodyLines[i]].Split('\t')[2], SourceBlockIndex = block.Index, SourceAccessoryIndex = i, SourceCableLength = block.Length,
                        SourceMaterialBefore = snapshot.Lines[block.BodyLines[i - 1]].Split('\t')[2] });
                }
            }
        }

        static Dictionary<string, string> ParseItem(string text)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("[", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('=');
                if (equals < 0) continue;
                string key = NormalizeKey(line.Substring(0, equals));
                string value = line.Substring(equals + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
                result[key] = value;
            }
            return result;
        }

        static ProjectDraft FromItem(Dictionary<string, string> values)
        {
            ProjectDraft draft = new ProjectDraft();
            draft.Organization = Get(values, "所属单位");
            draft.OperatorName = Get(values, "测试人员");
            draft.TestLocation = Get(values, "测试地点");
            draft.StartName = Get(values, "始端地点");
            draft.EndName = Get(values, "终端地点");
            draft.StartType = Coalesce(Get(values, "始端开关类型"), draft.StartType);
            draft.EndType = Coalesce(Get(values, "终端开关类型"), draft.EndType);
            draft.DispatchNumberStart = Get(values, "调度号1");
            draft.DispatchNumberEnd = Get(values, "调度号2");
            draft.LineName = Get(values, "线路名称");
            draft.CableModel = Get(values, "电缆型号");
            draft.CableMode = ProjectModes.Normalize(Get(values, "电缆芯数"));
            double summary = ProjectText.ParseNumber(Get(values, "电缆长度"), 0);
            draft.LengthA = ProjectText.ParseNumber(Get(values, "A相电缆长度"), summary);
            draft.LengthB = ProjectText.ParseNumber(Get(values, "B相电缆长度"), summary);
            draft.LengthC = ProjectText.ParseNumber(Get(values, "C相电缆长度"), summary);
            draft.PhaseVoltage = Coalesce(Get(values, "相电压"), draft.PhaseVoltage);
            draft.PressureMode = ProjectPressureModes.Normalize(Coalesce(Get(values, "加压模式"), draft.PressureMode));
            draft.Notes = Get(values, "备注");
            draft.Weather = Get(values, "天气");
            int weatherCode;
            draft.WeatherCode = int.TryParse(Get(values, "天气代码"), NumberStyles.Integer, CultureInfo.InvariantCulture, out weatherCode) ? weatherCode : -1;
            draft.WeatherProvider = Get(values, "天气来源");
            DateTime weatherTime;
            if (TryReadSampleTime(Get(values, "天气更新时间"), out weatherTime))
                draft.WeatherUpdatedAt = weatherTime;
            draft.Humidity = ProjectText.ParseNumber(Get(values, "湿度"), 0);
            draft.Temperature = ProjectText.ParseNumber(Get(values, "温度"), 0);
            draft.Latitude = ProjectText.ParseNumber(Get(values, "北斗纬度"), 0);
            draft.Longitude = ProjectText.ParseNumber(Get(values, "北斗经度"), 0);
            draft.Altitude = ProjectText.ParseNumber(Get(values, "北斗海拔"), 0);
            draft.BeidouFixStatus = Coalesce(Get(values, "北斗定位状态"), "未采集");
            DateTime hardwareTime;
            if (TryReadSampleTime(Get(values, "硬件采样时间"), out hardwareTime))
                draft.HardwareSampleTime = hardwareTime;
            draft.EnvironmentSampleValid = Get(values, "温湿度数据有效") == "1";
            draft.GnssFixValid = Get(values, "北斗数据有效") == "1";
            draft.GnssAltitudeValid = Get(values, "北斗海拔有效") == "1";
            DateTime environmentTime;
            if (DateTime.TryParse(Get(values, "温湿度采样时间"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out environmentTime)) draft.EnvironmentSampleTime = environmentTime;
            if (DateTime.TryParse(Get(values, "北斗UTC时间"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out environmentTime)) draft.GnssSampleTime = environmentTime;
            draft.DualEnded = Get(values, "双端") == "1" || string.Equals(Get(values, "双端"), "true", StringComparison.OrdinalIgnoreCase);
            draft.InsulationA1 = Get(values, "A相绝缘电阻1"); draft.InsulationA2 = Get(values, "A相绝缘电阻2");
            draft.InsulationB1 = Get(values, "B相绝缘电阻1"); draft.InsulationB2 = Get(values, "B相绝缘电阻2");
            draft.InsulationC1 = Get(values, "C相绝缘电阻1"); draft.InsulationC2 = Get(values, "C相绝缘电阻2");
            draft.InsulationReferenceProfile = Get(values, "绝缘参考标准");
            draft.InsulationConditionsComparable = Get(values, "绝缘测量条件一致") == "1";
            DateTime parsed;
            string dateTimeText = (Get(values, "日期").Trim() + " " + Get(values, "时间").Trim()).Trim();
            if (DateTime.TryParseExact(dateTimeText, new[] { "yyyy-M-d H:m:s", "yyyy-M-d H:m", "yyyy/M/d H:m:s", "yyyy/M/d H:m",
                "yyyy年M月d日 H时m分s秒", "yyyy年M月d日 H时m分", "yyyy-M-d", "yyyy年M月d日" }, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                draft.TestDateTime = parsed;
            if (DateTime.TryParseExact(Get(values, "铺设时间"), new[] { "yyyy-M-d", "yyyy/M/d", "yyyy年M月d日" }, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                draft.LayingDate = parsed;
            string[] multipliers = Get(values, "加压倍数").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            string[] repetitions = Get(values, "加压次数").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < Math.Min(multipliers.Length, repetitions.Length); index++)
            {
                double multiplier; int count;
                if (double.TryParse(multipliers[index], NumberStyles.Float, CultureInfo.InvariantCulture, out multiplier) &&
                    int.TryParse(repetitions[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0)
                    draft.PressureSteps.Add(new PressureStep { Multiplier = multiplier, Repetitions = count });
            }
            foreach (KeyValuePair<string, string> pair in values) draft.UnknownItemValues[pair.Key] = pair.Value;
            return draft;
        }

        static bool TryReadSampleTime(string text, out DateTime value)
        {
            // ISO round-trip retains UTC/local/unspecified semantics; older files stored a local wall-clock value.
            return DateTime.TryParseExact(text, new[] { "o", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
        }

        static void ParseCable(string text, ProjectDraft draft)
        {
            string phase = string.Empty;
            JointData lastJoint = null;
            HashSet<string> terminalFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string[] cells = raw.Split('\t');
                if (cells.Length < 2) continue;
                int level;
                if (!int.TryParse(cells[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out level)) continue;
                string name = cells[1].Trim();
                if (level == 0)
                {
                    if (ProjectModes.Phases.Contains(name) || new[] { "A", "B", "C" }.Contains(name.ToUpperInvariant()))
                        phase = ProjectModes.NormalizePhaseOrDefault(name);
                    lastJoint = null;
                    continue;
                }
                if (phase.Length == 0) continue;
                string type = cells.Length > 2 ? cells[2].Trim() : string.Empty;
                string positionText = cells.Length > 3 ? cells[3].Trim() : string.Empty;
                if (level == 1 && (name.Contains("终端") || name.Equals("End", StringComparison.OrdinalIgnoreCase)))
                {
                    draft.SetLength(phase, ProjectText.ParseNumber(positionText, draft.LengthFor(phase)));
                    terminalFound.Add(phase);
                    if (!string.IsNullOrWhiteSpace(type)) draft.EndType = type;
                    lastJoint = null;
                }
                else if (level == 1 && (name.Contains("始端") || name.Equals("Start", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!string.IsNullOrWhiteSpace(type)) draft.StartType = type;
                    lastJoint = null;
                }
                else if (level == 1 && ProjectText.ParseNumber(positionText, 0) > 0)
                {
                    lastJoint = new JointData
                    {
                        Phase = phase,
                        Name = name,
                        AttachmentType = string.IsNullOrWhiteSpace(type) ? "户内冷缩" : type,
                        Position = ProjectText.ParseNumber(positionText, 0),
                        Material = "塑料绝缘"
                    };
                    draft.Joints.Add(lastJoint);
                }
                else if (level == 2 && (name.Contains("本体") || name.Equals("Body", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(type))
                {
                    if (lastJoint != null) lastJoint.Material = type;
                    else
                    {
                        draft.FirstSegmentMaterials[phase] = type;
                        if (phase == "A相" || draft.FirstSegmentMaterials.Count == 1) draft.DefaultMaterial = type;
                    }
                }
            }
        }

        static void InferMode(ProjectDraft draft, Dictionary<string, string> values)
        {
            string explicitMode = Get(values, "电缆芯数");
            if (!string.IsNullOrWhiteSpace(explicitMode)) { draft.CableMode = ProjectModes.Normalize(explicitMode); return; }
            // Legacy files may predate the explicit load-type field.  Preserve
            // their useful cable semantics, but never infer a capacitor from
            // zero length; capacitor mode is always an explicit choice.
            const double tolerance = 0.0005;
            bool aSet = Math.Abs(draft.LengthA) >= tolerance;
            bool bSet = Math.Abs(draft.LengthB) >= tolerance;
            bool cSet = Math.Abs(draft.LengthC) >= tolerance;
            if (!aSet && !bSet && !cSet)
            {
                draft.CableMode = ProjectModes.ThreeCore;
                return;
            }
            if (aSet && !bSet && !cSet)
            {
                draft.CableMode = ProjectModes.SinglePhase;
                return;
            }
            bool equalAB = Math.Abs(draft.LengthA - draft.LengthB) < tolerance;
            bool equalAC = Math.Abs(draft.LengthA - draft.LengthC) < tolerance;
            draft.CableMode = equalAB && equalAC ? ProjectModes.ThreeCore : ProjectModes.ThreePhaseSingleCore;
        }

        internal static bool HasExistingTestData(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
            if (!IsPhysicalDataDirectory(directory)) return false;
            try
            {
                if (HasPhysicalDataFile(directory, ".cal", ".dem", ".loc")) return true;
                string calibration = Path.Combine(directory, "Calibration");
                if (!IsPhysicalDataDirectory(calibration)) return false;
                string live = Path.Combine(calibration, "Live");
                if (IsPhysicalDataDirectory(live) && HasPhysicalDataFile(live, ".hvcal")) return true;
                string acquisition = Path.Combine(calibration, "Acquisition");
                if (!IsPhysicalDataDirectory(acquisition)) return false;
                // The acquisition writer stores .xxs data in exactly one session
                // folder. Do not recurse through unrelated project directories
                // or follow junctions/symlinks while deciding geometry protection.
                foreach (string session in Directory.EnumerateDirectories(acquisition, "*", SearchOption.TopDirectoryOnly))
                    if (IsPhysicalDataDirectory(session) && HasPhysicalDataFile(session, ".xxs")) return true;
                return false;
            }
            catch (IOException) { return true; } // Unknown state must not unlock measured geometry.
            catch (UnauthorizedAccessException) { return true; }
        }

        static bool IsPhysicalDataDirectory(string path)
        { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }

        static bool HasPhysicalDataFile(string directory, params string[] extensions)
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Any(path =>
                extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0);
        }

        internal static bool IsProjectDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return false;
                RejectReparsePoint(directory);
                string item = Path.Combine(directory, "ITEM.in");
                string cable = Path.Combine(directory, "CABLE.in");
                if (!File.Exists(item) || !File.Exists(cable) || ParseItem(ReadCompatible(item)).Count == 0) return false;
                return ReadCompatible(cable).Replace("\r\n", "\n").Split('\n').Any(line =>
                {
                    string[] cells = line.Split('\t');
                    int level;
                    return cells.Length >= 4 && int.TryParse(cells[0].Trim(), out level) && (level == 0 || level == 1);
                });
            }
            catch { return false; }
        }

        internal static string ApplyDoubleEndSuffix(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return fileName ?? string.Empty;
            string extension = Path.GetExtension(fileName);
            if (!extension.Equals(".cal", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".dem", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".loc", StringComparison.OrdinalIgnoreCase)) return fileName;
            string directory = Path.GetDirectoryName(fileName) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            if (!stem.EndsWith("D", StringComparison.OrdinalIgnoreCase)) stem += "D";
            return Path.Combine(directory, stem + extension);
        }

        static string Get(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(NormalizeKey(key), out value) ? value : string.Empty;
        }

        static string Coalesce(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        static string NormalizeKey(string key)
        {
            return HVX.ProjectModel.ProjectMetadataKeys.Canonical(key);
        }

        static string SafeName(string name)
        {
            string result = name;
            foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            result = result.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(result) ? "未命名线路" : result;
        }

        static string UniqueDirectory(string parent, string baseName)
        {
            string candidate = Path.Combine(parent, baseName);
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            for (int index = 1; index < 1000; index++)
            {
                candidate = Path.Combine(parent, baseName + "-" + index.ToString("00", CultureInfo.InvariantCulture));
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
            throw new IOException("无法生成唯一的项目目录名。");
        }

        static void RejectReparsePoint(string path)
        {
            DirectoryInfo info = new DirectoryInfo(path);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不允许在重解析点目录中读写项目：" + path);
        }

        static void TryDeleteEmptyStaging(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string file in Directory.GetFiles(path)) File.Delete(file);
                Directory.Delete(path, false);
            }
            catch { }
        }
    }
}
