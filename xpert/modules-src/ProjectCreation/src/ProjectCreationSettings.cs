using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HVX.Controls
{
    internal sealed class ReferenceCatalog
    {
        internal string LoadWarning { get; private set; }
        string sourcePath, sourceHash;
        internal bool RecoveryRequired { get { return !String.IsNullOrEmpty(LoadWarning); } }
        internal readonly List<string> TerminalTypes = new List<string>();
        internal readonly List<string> AttachmentTypes = new List<string>();
        internal readonly List<string> PhaseVoltages = new List<string>();
        internal readonly List<string> Organizations = new List<string>();
        internal readonly List<string> Operators = new List<string>();
        internal string LastPressureMode = ProjectPressureModes.NewCable;
        internal readonly List<MaterialData> Materials = new List<MaterialData>();
        internal readonly Dictionary<string, List<PressureStep>> PressurePresets =
            new Dictionary<string, List<PressureStep>>(StringComparer.OrdinalIgnoreCase);

        internal static ReferenceCatalog CreateDefaults()
        {
            ReferenceCatalog catalog = new ReferenceCatalog();
            catalog.TerminalTypes.AddRange(new[] { "户内冷缩", "户外冷缩", "户内热缩", "户外热缩", "套管", "油终端", "高压插拔头" });
            catalog.AttachmentTypes.AddRange(new[] { "户内冷缩", "户外冷缩", "户内热缩", "户外热缩" });
            catalog.PhaseVoltages.AddRange(new[] { "21.0kV", "8.7kV", "6.0kV" });
            catalog.Materials.Add(new MaterialData { Name = "塑料绝缘", Velocity = 170.0 });
            catalog.Materials.Add(new MaterialData { Name = "橡胶绝缘", Velocity = 140.0 });
            catalog.Materials.Add(new MaterialData { Name = "油纸绝缘", Velocity = 160.0 });
            catalog.PressurePresets["旧电缆"] = Steps(
                "0.0:1", "0.5:1", "1.0:3", "1.1:1", "1.3:1", "1.5:3", "1.7:3", "1.0:1", "0.0:1");
            catalog.PressurePresets["新电缆"] = Steps(
                "0.0:1", "0.5:1", "1.0:3", "1.1:1", "1.3:1", "1.5:3", "1.7:3", "1.8:1", "2.0:3", "1.0:1", "0.0:1");
            catalog.PressurePresets["自定义"] = Steps("0.0:1", "0.5:1", "1.0:1", "0.0:1");
            catalog.PressurePresets["任意值"] = Steps();
            return catalog;
        }

        static List<PressureStep> Steps(params string[] values)
        {
            List<PressureStep> steps = new List<PressureStep>();
            foreach (string value in values)
            {
                string[] parts = value.Split(':');
                steps.Add(new PressureStep
                {
                    Multiplier = double.Parse(parts[0], CultureInfo.InvariantCulture),
                    Repetitions = int.Parse(parts[1], CultureInfo.InvariantCulture)
                });
            }
            return steps;
        }

        internal static ReferenceCatalog Load(string path)
        {
            ReferenceCatalog catalog = CreateDefaults();
            if (string.IsNullOrWhiteSpace(path)) return catalog;
            // Absence is a baseline too: another editor may create the catalog
            // before this form saves. Do not silently replace that new file.
            catalog.sourcePath = Path.GetFullPath(path);
            if (!File.Exists(path)) return catalog;
            try
            {
                byte[] original = File.ReadAllBytes(path);
                catalog.sourceHash = HVX.ProjectCables.CableJointStore.Hash(original);
                if (original.Length > 2 * 1024 * 1024) throw new InvalidDataException("配置缓存过大。");
                Dictionary<string, List<string>> sections = ReadSections(path);
                if (sections.Count == 0 || !sections.Keys.Any(key => new[] { "TerminalTypes", "AttachmentTypes", "PhaseVoltages", "Organizations", "Operators", "Materials" }.Contains(key, StringComparer.OrdinalIgnoreCase) || key.StartsWith("Pressure.", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("无法识别配置缓存内容。");
                ReplaceStrings(catalog.TerminalTypes, sections, "TerminalTypes");
                if(!catalog.TerminalTypes.Contains("高压插拔头"))catalog.TerminalTypes.Add("高压插拔头");
                ReplaceStrings(catalog.AttachmentTypes, sections, "AttachmentTypes");
                ReplaceStrings(catalog.PhaseVoltages, sections, "PhaseVoltages");
                ReplaceStrings(catalog.Organizations, sections, "Organizations");
                ReplaceStrings(catalog.Operators, sections, "Operators");
                List<string> lastModes;
                if (sections.TryGetValue("LastPressureMode", out lastModes) && lastModes.Count == 1)
                    catalog.LastPressureMode = ProjectPressureModes.Normalize(lastModes[0]);
                List<string> materialRows;
                if (sections.TryGetValue("Materials", out materialRows) && materialRows.Count > 0)
                {
                    List<MaterialData> parsed = new List<MaterialData>();
                    foreach (string row in materialRows)
                    {
                        string[] parts = row.Split('|');
                        double velocity;
                        if (parts.Length == 2 &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out velocity) && IsValidMaterial(parts[0].Trim(), velocity) &&
                            !parsed.Any(item => string.Equals(item.Name, parts[0].Trim(), StringComparison.OrdinalIgnoreCase)))
                            parsed.Add(new MaterialData { Name = parts[0].Trim(), Velocity = velocity });
                        else catalog.LoadWarning = "材料条目有损坏或重复值。";
                    }
                    if (parsed.Count > 0) { catalog.Materials.Clear(); catalog.Materials.AddRange(parsed); }
                }
                else if (sections.ContainsKey("Materials")) catalog.LoadWarning = "材料字典为空。";
                foreach (string mode in new[] { "旧电缆", "新电缆", "自定义", "任意值" })
                {
                    List<string> rows;
                    if (!sections.TryGetValue("Pressure." + mode, out rows)) continue;
                    List<PressureStep> parsed = new List<PressureStep>();
                    foreach (string row in rows)
                    {
                        string[] parts = row.Split('|');
                        double multiplier; int repetitions;
                        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out multiplier) && !double.IsNaN(multiplier) && !double.IsInfinity(multiplier) && multiplier >= 0 &&
                            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out repetitions) && repetitions > 0)
                            parsed.Add(new PressureStep { Multiplier = multiplier, Repetitions = repetitions });
                        else catalog.LoadWarning = "加压预设条目有损坏。";
                    }
                    if (parsed.Count > 0) catalog.PressurePresets[mode] = parsed;
                }
                if (catalog.sourceHash != HVX.ProjectCables.CableJointStore.Hash(File.ReadAllBytes(path)))
                    throw new IOException("读取期间配置缓存发生变化，请重新加载。");
            }
            catch (Exception error)
            {
                catalog.LoadWarning = error.Message;
            }
            return catalog;
        }

        static Dictionary<string, List<string>> ReadSections(string path)
        {
            Dictionary<string, List<string>> sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string current = string.Empty;
            foreach (string raw in File.ReadAllLines(path, new UTF8Encoding(false, true)))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    if (!sections.ContainsKey(current)) sections[current] = new List<string>();
                    continue;
                }
                if (current.Length == 0) throw new InvalidDataException("配置条目缺少节名称。");
                int equals = line.IndexOf('=');
                string value = equals >= 0 ? line.Substring(equals + 1).Trim() : line;
                if (value.Length > 0) sections[current].Add(value.Replace("\\n", "\n").Replace("\\r", "\r"));
            }
            return sections;
        }

        static void ReplaceStrings(List<string> target, Dictionary<string, List<string>> sections, string key)
        {
            List<string> values;
            if (!sections.TryGetValue(key, out values)) return;
            values = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (values.Count == 0) return;
            target.Clear();
            target.AddRange(values);
        }

        internal void Save(string path)
        { SaveCore(path, false); }

        internal string RecoverAndSave(string path)
        { return SaveCore(path, true); }

        string SaveCore(string path, bool recover)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings path is required.", "path");
            path = Path.GetFullPath(path);
            using (HVX.ProjectCables.JointMaterialCatalog.AcquireWrite(path))
            {
            if (RecoveryRequired && !recover)
                throw new InvalidDataException("配置缓存读取异常，原件已保留，未自动覆盖；请先修复文件或在材料菜单明确恢复。 " + LoadWarning);
            if (recover && (!RecoveryRequired || String.IsNullOrEmpty(sourceHash) || !String.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("没有可确认的损坏原件，请重新加载配置后再恢复。");
            Action unchanged = delegate {
                if (String.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase) &&
                    (sourceHash == null ? File.Exists(path) :
                        !File.Exists(path) || HVX.ProjectCables.CableJointStore.Hash(File.ReadAllBytes(path)) != sourceHash))
                    throw new IOException("配置缓存已被其他操作修改，请重新加载；未覆盖其他修改。");
            };
            unchanged();
            if (Materials.Count == 0 || Materials.Any(item => item == null || !IsValidMaterial(item.Name, item.Velocity)) ||
                Materials.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Materials.Count)
                throw new InvalidDataException("Invalid material catalog.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            List<string> lines = new List<string>();
            AddStringSection(lines, "TerminalTypes", TerminalTypes);
            AddStringSection(lines, "AttachmentTypes", AttachmentTypes);
            AddStringSection(lines, "PhaseVoltages", PhaseVoltages);
            AddStringSection(lines, "Organizations", Organizations);
            AddStringSection(lines, "Operators", Operators);
            AddStringSection(lines, "LastPressureMode", new[] { ProjectPressureModes.Normalize(LastPressureMode) });
            lines.Add("[Materials]");
            foreach (MaterialData material in Materials)
                lines.Add("item=" + material.Name + "|" + material.Velocity.ToString("R", CultureInfo.InvariantCulture));
            lines.Add(string.Empty);
            foreach (string mode in new[] { "旧电缆", "新电缆", "自定义", "任意值" })
            {
                lines.Add("[Pressure." + mode + "]");
                List<PressureStep> steps;
                if (PressurePresets.TryGetValue(mode, out steps))
                    foreach (PressureStep step in steps)
                        lines.Add("step=" + ProjectText.Number(step.Multiplier) + "|" + step.Repetitions.ToString(CultureInfo.InvariantCulture));
                lines.Add(string.Empty);
            }
            // Commit a complete cache, never truncate a previously saved catalog
            // before all new bytes are on disk. Read-only and lock failures leave
            // the original untouched and propagate to the editor.
            string pending = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            string backup = recover ? Path.Combine(directory, "catalog-damaged-" + Guid.NewGuid().ToString("N") + ".bak") : null;
            try
            {
                File.WriteAllLines(pending, lines.ToArray(), new UTF8Encoding(false));
                unchanged();
                if (File.Exists(path)) File.Replace(pending, path, backup);
                else File.Move(pending, path);
                sourcePath = path; sourceHash = HVX.ProjectCables.CableJointStore.Hash(File.ReadAllBytes(path)); LoadWarning = string.Empty;
                return backup ?? string.Empty;
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            }
        }

        internal static bool IsValidMaterial(string name, double velocity)
        {
            return !string.IsNullOrWhiteSpace(name) && name == name.Trim() && name.Length <= 200 &&
                !name.Any(char.IsControl) && name.IndexOf('|') < 0 && !name.Contains("\\n") && !name.Contains("\\r") &&
                velocity > 0 && !double.IsNaN(velocity) && !double.IsInfinity(velocity);
        }

        internal ReferenceCatalog Clone()
        {
            ReferenceCatalog copy = new ReferenceCatalog();
            copy.LoadWarning = LoadWarning; copy.sourcePath = sourcePath; copy.sourceHash = sourceHash;
            copy.LastPressureMode = LastPressureMode;
            copy.TerminalTypes.AddRange(TerminalTypes); copy.AttachmentTypes.AddRange(AttachmentTypes);
            copy.PhaseVoltages.AddRange(PhaseVoltages); copy.Organizations.AddRange(Organizations); copy.Operators.AddRange(Operators);
            copy.Materials.AddRange(Materials.Select(item => item.Clone()));
            foreach (var pair in PressurePresets) copy.PressurePresets[pair.Key] = pair.Value.Select(item => item.Clone()).ToList();
            return copy;
        }

        internal void AcceptPersistenceFrom(ReferenceCatalog saved)
        { sourcePath = saved.sourcePath; sourceHash = saved.sourceHash; LoadWarning = saved.LoadWarning; }

        static void AddStringSection(List<string> lines, string name, IEnumerable<string> values)
        {
            lines.Add("[" + name + "]");
            foreach (string value in values) lines.Add("item=" + value.Replace("\r", "\\r").Replace("\n", "\\n"));
            lines.Add(string.Empty);
        }

        internal List<PressureStep> GetPressureSteps(string mode)
        {
            if (ProjectPressureModes.Normalize(mode) == ProjectPressureModes.Arbitrary) return new List<PressureStep>();
            List<PressureStep> source;
            if (!PressurePresets.TryGetValue(mode ?? string.Empty, out source)) source = PressurePresets["新电缆"];
            return source.Select(step => step.Clone()).ToList();
        }
    }
}
