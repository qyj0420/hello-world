using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.ProjectCables
{
    // One metadata budget for all project readers. Check the opened stream and
    // every actual read, rather than trusting a path-length probe before allocation.
    internal sealed class ProjectMetadataLimitException : IOException
    {
        internal ProjectMetadataLimitException() : base("项目文件过大或内容过多，请检查文件后重试。 / Project metadata exceeds the supported size; check the file and retry.") { }
    }
    internal static class BoundedProjectMetadata
    {
        internal const int MaximumBytes = 2 * 1024 * 1024;
        internal const int MaximumCharacters = 2 * 1024 * 1024;
        internal const int MaximumLines = 32768;
        internal const int MaximumLineCharacters = 65536;

        internal static byte[] ReadBytes(string path)
        {
            RejectLink(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ReadBytes(stream);
        }
        // Stream seam is also used by offline growth/shrink fixtures; callers
        // own the stream. Only this bounded output is ever retained in memory.
        internal static byte[] ReadBytes(Stream stream)
        {
            long expected = stream.CanSeek ? stream.Length - stream.Position : -1;
            if (expected > MaximumBytes) throw new ProjectMetadataLimitException();
            using (var output = new MemoryStream(expected >= 0 ? (int)expected : 0))
            {
                CopyChecked(stream, expected, delegate(byte[] buffer, int count) { output.Write(buffer, 0, count); });
                return output.ToArray();
            }
        }
        internal static string HashFile(string path)
        {
            RejectLink(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
            {
                CopyChecked(stream, stream.Length, delegate(byte[] buffer, int count) { hash.TransformBlock(buffer, 0, count, buffer, 0); });
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return Convert.ToBase64String(hash.Hash);
            }
        }
        static void CopyChecked(Stream stream, long expected, Action<byte[], int> consume)
        {
            if (expected > MaximumBytes) throw new ProjectMetadataLimitException();
            byte[] buffer = new byte[8192]; int total = 0;
            while (true)
            {
                // At the limit read one extra byte only: never allocate for or
                // silently accept an arbitrarily growing file.
                int count = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumBytes - total + 1));
                if (count == 0) break;
                total = checked(total + count);
                if (total > MaximumBytes) throw new ProjectMetadataLimitException();
                if (expected >= 0 && total > expected) throw new IOException("项目文件在读取时改变，请重新加载。 / Project metadata changed while reading; reload it.");
                consume(buffer, count);
            }
            if (expected >= 0 && total != expected)
                throw new IOException("项目文件在读取时改变，请重新加载。 / Project metadata changed while reading; reload it.");
        }
        static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("项目文件不能是链接。 / Project metadata must not be a linked file.");
        }
        internal static string ValidateText(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (text.Length > MaximumCharacters) throw new ProjectMetadataLimitException();
            int lines = 1, length = 0;
            for (int index = 0; index < text.Length; index++)
            {
                char value = text[index];
                if (value == '\0') throw new InvalidDataException("项目文件编码无效。 / Project metadata encoding is invalid.");
                if (value == '\r' || value == '\n')
                {
                    if (value == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    if (++lines > MaximumLines) throw new ProjectMetadataLimitException();
                    length = 0;
                }
                else if (++length > MaximumLineCharacters) throw new ProjectMetadataLimitException();
            }
            return text;
        }
    }
    // Shared material dictionary, using the existing project-creation cache so
    // every joint editor sees the same names and suggested velocities.
    internal sealed class JointMaterialEntry
    {
        internal string Name;
        internal double Velocity;
    }
    internal sealed class JointMaterialCatalog
    {
        internal string Path, ExpectedHash;
        internal byte[] Original;
        internal readonly List<JointMaterialEntry> Entries = new List<JointMaterialEntry>();
        internal static string DefaultPath { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HVX", "ProjectCreation", "project-creation.ini"); } }
        internal static JointMaterialCatalog Read(string path)
        {
            var result = new JointMaterialCatalog { Path = System.IO.Path.GetFullPath(path) };
            if (!File.Exists(result.Path))
            {
                result.Entries.AddRange(new[] { new JointMaterialEntry { Name = "塑料绝缘", Velocity = 170 }, new JointMaterialEntry { Name = "橡胶绝缘", Velocity = 140 }, new JointMaterialEntry { Name = "油纸绝缘", Velocity = 160 } });
                return result;
            }
            result.Original = ReadBounded(result.Path);
            result.ExpectedHash = CableJointStore.Hash(result.Original);
            string text = new UTF8Encoding(false, true).GetString(result.Original).TrimStart('\uFEFF');
            bool material = false, seen = false;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { material = line.Equals("[Materials]", StringComparison.OrdinalIgnoreCase); if (material && seen) throw new InvalidDataException("材料节重复 / Duplicate material section."); seen |= material; continue; }
                if (!material || line.Length == 0 || line.StartsWith(";")) continue;
                int equals = line.IndexOf('='); if (equals >= 0) line = line.Substring(equals + 1).Trim();
                string[] parts = line.Split('|'); double velocity;
                if (parts.Length != 2 || !Double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out velocity) || !Valid(parts[0], velocity) || result.Entries.Any(e => String.Equals(e.Name, parts[0], StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("材料字典有无效或重复条目，原件未修改 / Invalid or duplicate material entry; original cache retained.");
                result.Entries.Add(new JointMaterialEntry { Name = parts[0], Velocity = velocity });
            }
            if (!seen)
            {
                if (!new[] { "[TerminalTypes]", "[AttachmentTypes]", "[PhaseVoltages]", "[Organizations]", "[Operators]", "[Pressure." }.Any(key => text.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
                    throw new InvalidDataException("无法识别材料配置，原件未修改 / Unrecognized reference cache; original retained.");
                result.Entries.AddRange(ReadDefaults());
            }
            else if (result.Entries.Count == 0) throw new InvalidDataException("材料字典为空 / Material catalog is empty.");
            return result;
        }
        static JointMaterialEntry[] ReadDefaults()
        { return new[] { new JointMaterialEntry { Name = "塑料绝缘", Velocity = 170 }, new JointMaterialEntry { Name = "橡胶绝缘", Velocity = 140 }, new JointMaterialEntry { Name = "油纸绝缘", Velocity = 160 } }; }
        internal static byte[] ReadBounded(string path)
        {
            const int limit = 2 * 1024 * 1024;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > limit) throw new InvalidDataException("材料配置过大 / Material cache is too large.");
                byte[] bytes = new byte[(int)stream.Length]; int offset = 0;
                while (offset < bytes.Length) {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new IOException("材料配置读取期间发生变化 / Material cache changed while reading.");
                    offset += count;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("材料配置读取期间增长 / Material cache grew while reading.");
                return bytes;
            }
        }
        internal static bool Valid(string name, double velocity)
        { return !String.IsNullOrWhiteSpace(name) && name == name.Trim() && name.Length <= 200 && !name.Any(Char.IsControl) && !name.Contains("|") && !name.Contains("\\n") && !name.Contains("\\r") && CableJointStore.Finite(velocity) && velocity > 0; }
        internal static FileStream AcquireWrite(string path)
        {
            string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)); Directory.CreateDirectory(directory);
            return new FileStream(path + ".write.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        internal void Save()
        {
            if (Entries.Count == 0 || Entries.Any(e => !Valid(e.Name, e.Velocity)) || Entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Entries.Count)
                throw new InvalidDataException("无效材料字典 / Invalid material catalog.");
            using (AcquireWrite(Path))
            {
                Action unchanged = delegate { if (Original == null ? File.Exists(Path) : !File.Exists(Path) || CableJointStore.Hash(ReadBounded(Path)) != ExpectedHash) throw new IOException("材料字典已变化，请重新打开后重试 / Material catalog changed; reload before retrying."); };
                unchanged();
                string text = Original == null ? "" : new UTF8Encoding(false, true).GetString(Original).TrimStart('\uFEFF');
                string newline = text.Contains("\r\n") ? "\r\n" : "\n";
                var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
                int start = lines.FindIndex(l => l.Trim().Equals("[Materials]", StringComparison.OrdinalIgnoreCase));
                int end = start < 0 ? -1 : lines.FindIndex(start + 1, l => l.Trim().StartsWith("[") && l.Trim().EndsWith("]"));
                if (start < 0) start = lines.Count; else lines.RemoveRange(start, (end < 0 ? lines.Count : end) - start);
                lines.InsertRange(start, new[] { "[Materials]" }.Concat(Entries.Select(e => "item=" + e.Name + "|" + e.Velocity.ToString("R", CultureInfo.InvariantCulture))).Concat(new[] { "" }));
                var encoding = new UTF8Encoding(Original != null && Original.Length >= 3 && Original[0] == 239 && Original[1] == 187 && Original[2] == 191);
                byte[] output = encoding.GetPreamble().Concat(encoding.GetBytes(String.Join(newline, lines))).ToArray();
                if (output.Length > 2 * 1024 * 1024) throw new InvalidDataException("材料配置过大 / Material cache is too large.");
                string pending = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), Guid.NewGuid().ToString("N") + ".materials.tmp");
                try { File.WriteAllBytes(pending, output); unchanged(); if (File.Exists(Path)) File.Replace(pending, Path, null); else File.Move(pending, Path); Original = output; ExpectedHash = CableJointStore.Hash(output); }
                finally { if (File.Exists(pending)) File.Delete(pending); }
            }
        }
    }

    internal enum CableJointMode { SinglePhase, SharedThreeCore, SeparatePhases, Unknown }

    internal sealed class CableJointBlock
    {
        internal int Index;
        internal string Phase;
        internal readonly List<double> Positions = new List<double>();
        internal readonly List<int> AccessoryLines = new List<int>();
        internal readonly List<int> BodyLines = new List<int>();
        internal double Length { get { return Positions.Last(); } }
    }

    internal sealed class CableJointTarget
    {
        internal string Phase, Name, AttachmentType, MaterialBefore, MaterialAfter;
        internal int BlockIndex;
        internal double PositionM;
    }

    internal sealed class CableJointPlan
    {
        internal string OperationCode = "AddJoint";
        internal double OriginalPositionM = Double.NaN;
        internal bool SharedThreeCore;
        internal string CablePath, ExpectedHash, ExpectedItemHash;
        internal byte[] Original, Replacement;
        internal string[] Phases;
        internal double PositionM;
        internal string Name, AttachmentType, MaterialBefore, MaterialAfter;
        internal bool AutomaticName;
        internal CableJointTarget[] ResolvedTargets;
    }

    internal sealed class CableJointCommitResult
    {
        internal string BackupPath, Warning;
        internal bool Committed { get { return true; } }
    }

    // Legacy callers must never mistake a completed replacement for a safe-to-retry
    // failure. Interactive callers use CommitWithResult and show the warning after
    // updating their metadata, markers and host notifications.
    internal sealed class CableJointCommittedException : IOException
    {
        internal readonly CableJointCommitResult Result;
        internal CableJointCommittedException(CableJointCommitResult result) : base(result.Warning) { Result = result; }
    }

    internal sealed class CableJointSnapshot
    {
        internal string CablePath, ItemHash, ScopeDescription;
        internal byte[] Original, Preamble;
        internal string[] Lines, PhaseChoices, LineEndings;
        internal string Newline;
        internal Encoding Encoding;
        internal CableJointMode Mode;
        internal readonly List<CableJointBlock> Blocks = new List<CableJointBlock>();

        internal string SuggestedJointName(string[] phases, double position)
        {
            var block = Blocks.FirstOrDefault(b => Mode == CableJointMode.SharedThreeCore || (phases ?? new string[0]).Contains(b.Phase));
            return CableJointStore.AutomaticJointName(block == null ? 1 : 1 + block.Positions.Skip(1).Take(block.Positions.Count - 2).Count(p => p < position));
        }
        internal string[] InsertionMaterials(string[] phases, double position)
        {
            return Blocks.Where(b => Mode == CableJointMode.SharedThreeCore || (phases ?? new string[0]).Contains(b.Phase)).Select(b => {
                int next = b.Positions.FindIndex(p => p > position);
                int segment = Math.Max(0, Math.Min(b.BodyLines.Count - 1, next < 0 ? b.BodyLines.Count - 1 : next - 1));
                return Lines[b.BodyLines[segment]].Split('\t')[2];
            }).Distinct(StringComparer.Ordinal).ToArray();
        }

        internal CableJointPlan Prepare(string[] phases, double position, string name,
            string attachment, string beforeMaterial, string afterMaterial)
        { return PrepareWithNamePolicy(phases, position, name, attachment, beforeMaterial, afterMaterial, null); }

        internal CableJointPlan PrepareWithNamePolicy(string[] phases, double position, string name,
            string attachment, string beforeMaterial, string afterMaterial, bool? automaticName)
        { return PrepareAddition(phases, position, name, attachment, beforeMaterial, afterMaterial, automaticName, false); }

        internal CableJointPlan PrepareCandidateWithNamePolicy(string[] phases, double position, string name,
            string attachment, string beforeMaterial, string afterMaterial, bool? automaticName)
        { return PrepareAddition(phases, position, name, attachment, beforeMaterial, afterMaterial, automaticName, true); }

        private CableJointPlan PrepareAddition(string[] phases, double position, string name,
            string attachment, string beforeMaterial, string afterMaterial, bool? automaticName, bool preservePosition)
        {
            CableJointStore.Require(phases != null && phases.Length > 0 && phases.All(p => PhaseChoices.Contains(p)),
                "请选择项目实际存在的相别 / Select existing project phases.");
            phases = phases.Distinct().ToArray();
            if (!preservePosition) position = Math.Round(position, 3, MidpointRounding.AwayFromZero);
            CableJointStore.Require(CableJointStore.Finite(position) && position > 0, "接头位置无效 / Invalid joint position.");
            name = CableJointStore.Field(name, false, "接头名称");
            bool autoName = automaticName ?? CableJointStore.IsAutomaticJointName(name);
            attachment = CableJointStore.Field(attachment, false, "附件类型");
            beforeMaterial = CableJointStore.Field(beforeMaterial, true, "接头前材料");
            afterMaterial = CableJointStore.Field(afterMaterial, true, "接头后材料");
            var selected = Blocks.Where(b => Mode == CableJointMode.SharedThreeCore || phases.Contains(b.Phase)).ToArray();
            CableJointStore.Require(selected.Length > 0, "没有选中的电缆结构 / No selected cable structure.");
            var output = Lines.ToList();
            var targets = new List<CableJointTarget>();
            // Work from the end so every index still refers to the original snapshot.
            foreach (var block in selected.OrderByDescending(b => b.AccessoryLines[0]))
            {
                CableJointStore.Require(position < block.Length,
                    block.Phase + " 相接头必须在两端之间 / Joint must lie inside every selected cable.");
                CableJointStore.Require(block.Positions.All(p => Math.Abs(p - position) >= .001),
                    block.Phase + " 相该位置已有附件，未修改任何相 / An accessory already exists; no phases were changed.");
                CableJointStore.Require(autoName || !block.AccessoryLines.Any(i => String.Equals(Lines[i].Split('\t')[1], name, StringComparison.OrdinalIgnoreCase)),
                    block.Phase + " 相已有同名附件，请修改名称 / Accessory name already exists in this phase.");
                int next = block.Positions.FindIndex(p => p > position);
                string targetName = autoName ? CableJointStore.AutomaticJointName(next) : name;
                // Only reserved generated names are renumbered; descriptions,
                // phase headers and existing coordinates stay intact.
                for (int j = 1; j < block.AccessoryLines.Count - 1; j++)
                {
                    int line = block.AccessoryLines[j]; string[] columns = Lines[line].Split('\t');
                    if (!preservePosition && CableJointStore.IsAutomaticJointName(columns[1]))
                    { columns[1] = CableJointStore.AutomaticJointName(j >= next ? j + 1 : j); output[line] = String.Join("\t", columns); }
                }
                int insert = block.AccessoryLines[next], bodyLine = block.BodyLines[next - 1];
                string[] before = Lines[bodyLine].Split('\t'), after = (string[])before.Clone();
                if (beforeMaterial.Length > 0) before[2] = beforeMaterial;
                if (afterMaterial.Length > 0) after[2] = afterMaterial;
                output[bodyLine] = String.Join("\t", before);
                output.InsertRange(insert, new[] {
                    "1\t" + targetName + "\t" + attachment + "\t" + position.ToString(preservePosition ? "R" : "0.###", CultureInfo.InvariantCulture) + "m",
                    String.Join("\t", after) });
                targets.Add(new CableJointTarget { Phase = block.Phase, BlockIndex = block.Index, PositionM = position,
                    Name = targetName, AttachmentType = attachment, MaterialBefore = before[2], MaterialAfter = after[2] });
            }
            return new CableJointPlan { SharedThreeCore = Mode == CableJointMode.SharedThreeCore, CablePath = CablePath, ExpectedHash = CableJointStore.Hash(Original), ExpectedItemHash = ItemHash,
                Original = (byte[])Original.Clone(), Replacement = Preamble.Concat(Encoding.GetBytes(String.Join(Newline, output))).ToArray(),
                Phases = phases, PositionM = position, Name = name, AutomaticName = autoName, AttachmentType = attachment,
                MaterialBefore = beforeMaterial, MaterialAfter = afterMaterial, ResolvedTargets = targets.OrderBy(t => t.BlockIndex).ToArray() };
        }

        internal CableJointTarget[] ExistingTargets(int blockIndex, int accessoryIndex)
        {
            CableJointStore.Require(blockIndex >= 0 && blockIndex < Blocks.Count, "接头来源段已失效，请重新加载 / Invalid source block.");
            CableJointBlock source = Blocks[blockIndex];
            CableJointStore.Require(accessoryIndex > 0 && accessoryIndex < source.Positions.Count - 1,
                "只能编辑或删除中间接头，不能修改始终端 / Only interior joints can be edited or removed.");
            var blocks = Mode == CableJointMode.SharedThreeCore ? Blocks.ToArray() : new[] { source };
            foreach (var block in blocks)
                CableJointStore.Require(block.Positions.Count == source.Positions.Count && block.Positions.Zip(source.Positions, (a, b) => Math.Abs(a - b) < .000001).All(x => x),
                    "三芯各段位置不一致，不能确定共用接头；请先核对结构，未修改任何段 / Shared cable blocks disagree; no block was changed.");
            return blocks.Select(block => {
                string[] columns = Lines[block.AccessoryLines[accessoryIndex]].Split('\t');
                return new CableJointTarget { BlockIndex = block.Index, Phase = block.Phase, PositionM = block.Positions[accessoryIndex],
                    Name = columns[1], AttachmentType = columns[2], MaterialBefore = Lines[block.BodyLines[accessoryIndex - 1]].Split('\t')[2],
                    MaterialAfter = Lines[block.BodyLines[accessoryIndex]].Split('\t')[2] };
            }).ToArray();
        }

        internal CableJointPlan PrepareRemove(int blockIndex, int accessoryIndex, bool confirmMaterialMerge)
        { return PrepareExisting(blockIndex, accessoryIndex, true, 0, "", "", "", "", confirmMaterialMerge); }

        internal CableJointPlan PrepareEdit(int blockIndex, int accessoryIndex, double position, string name,
            string attachment, string afterMaterial, bool confirmMaterialMerge)
        { return PrepareEdit(blockIndex, accessoryIndex, position, name, attachment, "", afterMaterial, confirmMaterialMerge); }

        internal CableJointPlan PrepareEdit(int blockIndex, int accessoryIndex, double position, string name,
            string attachment, string beforeMaterial, string afterMaterial, bool confirmMaterialMerge)
        { return PrepareExisting(blockIndex, accessoryIndex, false, position, name, attachment, beforeMaterial, afterMaterial, confirmMaterialMerge); }

        sealed class SourceLine { internal string Text, Ending; internal int Index; }
        CableJointPlan PrepareExisting(int blockIndex, int accessoryIndex, bool remove, double position, string name,
            string attachment, string beforeMaterial, string afterMaterial, bool confirmMaterialMerge)
        {
            CableJointTarget[] targets = ExistingTargets(blockIndex, accessoryIndex);
            double originalPosition = targets[0].PositionM;
            if (!remove) CableJointStore.Require(CableJointStore.Finite(position) && position > 0,
                "接头位置必须是电缆内部有限值 / Joint must lie inside the cable.");
            name = CableJointStore.Field(name, true, "接头名称");
            attachment = CableJointStore.Field(attachment, true, "附件类型");
            beforeMaterial = CableJointStore.Field(beforeMaterial, true, "接头前材料");
            afterMaterial = CableJointStore.Field(afterMaterial, true, "接头后材料");
            var output = Lines.Select((line, i) => new SourceLine { Text = line, Index = i,
                Ending = LineEndings == null ? (i + 1 == Lines.Length ? "" : Newline) : LineEndings[i] }).ToList();
            foreach (CableJointTarget target in targets)
            {
                var block = Blocks[target.BlockIndex];
                bool moved = !remove && Math.Abs(position - target.PositionM) > .0000001;
                if (remove || moved)
                    CableJointStore.Require(confirmMaterialMerge || String.Equals(target.MaterialBefore, target.MaterialAfter, StringComparison.Ordinal),
                        "接头前后材料不同，必须明确确认原位置合并后保留前段材料 / Confirm retaining the preceding material when segments merge.");
                if (!remove)
                {
                    CableJointStore.Require(position < block.Length && !block.Positions.Where((p, i) => i != accessoryIndex).Any(p => Math.Abs(p - position) < .001),
                        "位置必须在始终端之间且不能与其他附件重复 / Position must be interior and distinct.");
                    CableJointStore.Require(name.Length == 0 || CableJointStore.IsAutomaticJointName(name) || !block.AccessoryLines.Where((line, i) => i != accessoryIndex)
                        .Any(line => String.Equals(Lines[line].Split('\t')[1], name, StringComparison.OrdinalIgnoreCase)), "同段已有同名附件 / Duplicate accessory name.");
                }
                SourceLine node = output.Single(line => line.Index == block.AccessoryLines[accessoryIndex]);
                SourceLine body = output.Single(line => line.Index == block.BodyLines[accessoryIndex]);
                if (remove || moved) { output.Remove(node); output.Remove(body); }
                if (!remove)
                {
                    string[] columns = node.Text.Split('\t');
                    if (name.Length > 0) columns[1] = name;
                    if (attachment.Length > 0) columns[2] = attachment;
                    // Keep untouched coordinates byte-for-byte. A changed value
                    // is invariant and lossless, independent of one-decimal UI.
                    if (moved) columns[3] = position.ToString("R", CultureInfo.InvariantCulture) + "m";
                    node.Text = String.Join("\t", columns);
                    if (afterMaterial.Length > 0) { columns = body.Text.Split('\t'); columns[2] = afterMaterial; body.Text = String.Join("\t", columns); }
                    if (moved)
                    {
                        int next = block.Positions.FindIndex(p => p > position);
                        if (next == accessoryIndex) next++;
                        int insertion = output.FindIndex(line => line.Index == block.AccessoryLines[next]);
                        output.InsertRange(insertion, new[] { node, body });
                    }
                    // Resolve the preceding body only after a move is ordered.
                    // Empty input preserves each block's actual material at the
                    // destination, not the material from the old joint position.
                    SourceLine preceding = output.Take(output.IndexOf(node)).Last(line => block.BodyLines.Contains(line.Index));
                    if (beforeMaterial.Length > 0) { columns = preceding.Text.Split('\t'); columns[2] = beforeMaterial; preceding.Text = String.Join("\t", columns); }
                    target.PositionM = position; target.Name = node.Text.Split('\t')[1]; target.AttachmentType = node.Text.Split('\t')[2];
                    target.MaterialBefore = preceding.Text.Split('\t')[2];
                    target.MaterialAfter = body.Text.Split('\t')[2];
                }
                var ordered = block.AccessoryLines.Skip(1).Take(block.AccessoryLines.Count - 2)
                    .Where(line => !remove || line != node.Index)
                    .OrderBy(line => line == node.Index ? position : block.Positions[block.AccessoryLines.IndexOf(line)]).ToArray();
                for (int i = 0; i < ordered.Length; i++)
                {
                    SourceLine line = output.Single(row => row.Index == ordered[i]); string[] columns = line.Text.Split('\t');
                    if (!CableJointStore.IsAutomaticJointName(columns[1])) continue;
                    columns[1] = CableJointStore.AutomaticJointName(i + 1); line.Text = String.Join("\t", columns);
                    if (line == node) target.Name = columns[1];
                }
            }
            string text = String.Concat(output.Select(line => line.Text + line.Ending));
            return new CableJointPlan { OperationCode = remove ? "RemoveJoint" : "EditJoint", OriginalPositionM = originalPosition, SharedThreeCore = Mode == CableJointMode.SharedThreeCore,
                CablePath = CablePath, Original = (byte[])Original.Clone(), ExpectedHash = CableJointStore.Hash(Original), ExpectedItemHash = ItemHash,
                Replacement = Preamble.Concat(Encoding.GetBytes(text)).ToArray(), Phases = targets.Select(t => t.Phase).Distinct().ToArray(),
                PositionM = remove ? targets[0].PositionM : position, Name = targets[0].Name,
                MaterialBefore = beforeMaterial, MaterialAfter = afterMaterial, ResolvedTargets = targets };
        }
    }

    // Shared by analysis, calibration and data browser. Writes only CABLE.in;
    // ITEM mode and cable bytes are optimistic-concurrency checked under the existing metadata lock.
    internal static class CableJointStore
    {
        internal static bool IsAutomaticJointName(string name)
        { return Regex.IsMatch(name ?? "", @"^(?:接头[ _]|Joint[ _])\d+(?:\.\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        internal static string AutomaticJointName(int number) { return "接头 " + number.ToString(CultureInfo.InvariantCulture); }
        internal static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
        internal static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(bytes)); }
        internal static void RequireWritePathLengths(params string[] paths)
        {
            // Callers construct these paths from an already normalized absolute
            // project root. Check before creating even the metadata lock: the
            // supported .NET 4.8 deployment must not depend on long-path policy.
            foreach (string path in paths)
                if (path.Length >= 260)
                    throw new InvalidOperationException("项目目录路径过长，无法安全创建临时文件、备份或编辑锁。请将项目移至较短路径后重试；源文件未修改。 / Project path is too long for a safe backup or write; use a shorter project path. Source files are unchanged.");
        }
        internal static string Field(string value, bool allowEmpty, string name)
        {
            value = (value ?? "").Trim();
            Require((allowEmpty || value.Length > 0) && value.Length <= 80 && !value.Any(Char.IsControl), name + "无效 / Invalid field: " + name);
            return value;
        }
        internal static string PhaseName(string text)
        {
            switch ((text ?? "").Trim().ToUpperInvariant())
            {
                case "A": case "A相": case "L1": return "A";
                case "B": case "B相": case "L2": return "B";
                case "C": case "C相": case "L3": return "C";
                case "ALL": case "ALL相": case "COMMON": case "全相": case "全部相": case "三相": case "共用": return "ALL";
                default: throw new InvalidDataException("无法识别文件相别，未修改 / Unrecognized cable phase.");
            }
        }
        static byte[] ReadBytes(string path)
        {
            var bytes = BoundedProjectMetadata.ReadBytes(path);
            Require(bytes.Length > 0, "项目元数据不可用 / Project metadata is unavailable: " + Path.GetFileName(path));
            return bytes;
        }
        static string Decode(byte[] bytes, out Encoding encoding, out byte[] preamble)
        {
            int skip = 0;
            if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) { encoding = new UTF8Encoding(false, true); skip = 3; }
            else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            else {
                encoding = new UTF8Encoding(false, true);
                try { encoding.GetString(bytes); }
                catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            }
            preamble = bytes.Take(skip).ToArray();
            string text = encoding.GetString(bytes, skip, bytes.Length - skip);
            return BoundedProjectMetadata.ValidateText(text);
        }
        internal static CableJointSnapshot Read(string projectDirectory)
        { return ReadSnapshot(projectDirectory, true); }
        // Display-only geometry uses the same validated cable parser, without
        // requiring unrelated legacy ITEM encoding or changing the write contract.
        internal static CableJointSnapshot ReadGeometry(string projectDirectory)
        { return ReadSnapshot(projectDirectory, false); }
        static CableJointSnapshot ReadSnapshot(string projectDirectory, bool readItem)
        {
            string root = Path.GetFullPath(projectDirectory);
            Require(Directory.Exists(root), "项目不存在 / Project not found.");
            for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
                Require((d.Attributes & FileAttributes.ReparsePoint) == 0, "不支持链接目录编辑 / Linked project directories cannot be edited.");
            var s = new CableJointSnapshot { CablePath = Path.Combine(root, "CABLE.in") };
            s.Original = ReadBytes(s.CablePath);
            string text = Decode(s.Original, out s.Encoding, out s.Preamble);
            s.Newline = text.Contains("\r\n") ? "\r\n" : "\n";
            s.Lines = text.Replace("\r\n", "\n").Split('\n');
            s.LineEndings = Regex.Matches(text, @"\r\n|\n").Cast<Match>().Select(m => m.Value).Concat(new[] { "" }).ToArray();
            Require(!s.Lines.Any(l => l.Contains("\r")), "不支持的换行格式 / Unsupported line endings.");
            CableJointBlock block = null;
            bool bodyPending = false;
            bool? capacitor = null;
            // Consult explicit ITEM load metadata only when a zero-length
            // endpoint pair needs it. Ordinary cable geometry remains strict
            // and display-only reads do not require unrelated ITEM fields.
            Func<bool> explicitCapacitor = () => {
                if (!capacitor.HasValue) capacitor = HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root);
                return capacitor.Value;
            };
            for (int i = 0; i < s.Lines.Length; i++)
            {
                string line = s.Lines[i]; if (String.IsNullOrWhiteSpace(line)) continue;
                var c = line.Split('\t'); Require(c.Length == 4, "仅支持四列电缆结构 / Expected four-column cable structure.");
                string kind = c[0].Trim();
                if (kind == "0") {
                    if (block != null) ValidateBlock(block, bodyPending, block.Positions.Count == 2 && block.Length == 0 && explicitCapacitor());
                    Require(String.IsNullOrWhiteSpace(c[2]) && String.IsNullOrWhiteSpace(c[3]), "无法识别相别头 / Invalid phase header.");
                    block = new CableJointBlock { Index = s.Blocks.Count, Phase = PhaseName(c[1]) };
                    s.Blocks.Add(block); bodyPending = false;
                } else if (kind == "1") {
                    Require(block != null && !String.IsNullOrWhiteSpace(c[1]), "缺少相别或附件名称 / Missing phase or accessory name.");
                    var m = Regex.Match(c[3].Trim(), @"^([+]?(?:\d+(?:\.\d*)?|\.\d+))\s*(?:m|米)?$", RegexOptions.IgnoreCase);
                    double pos;
                    Require(m.Success && Double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pos), "附件位置无效 / Invalid position.");
                    pos = Double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    bool capacitorEnd = block.Positions.Count == 1 && pos == 0 && explicitCapacitor();
                    Require(Finite(pos) && pos <= 1000000 && (block.Positions.Count == 0 ? pos == 0 && !bodyPending : bodyPending &&
                        (pos > block.Positions.Last() && !(block.Positions.Count >= 2 && block.Length == 0) || capacitorEnd)),
                        "附件必须从 0 开始并按位置递增，区段材料须完整 / Invalid accessory order or missing material.");
                    block.Positions.Add(pos); block.AccessoryLines.Add(i); bodyPending = false;
                    Require(block.Positions.Count <= 4096, "附件过多 / Too many accessories.");
                } else if (kind == "2") {
                    Require(block != null && block.Positions.Count > 0 && !bodyPending && !String.IsNullOrWhiteSpace(c[2]) && String.IsNullOrWhiteSpace(c[3]),
                        "区段材料不完整 / Invalid segment material.");
                    block.BodyLines.Add(i); bodyPending = true;
                } else throw new InvalidDataException("未知电缆记录，未改写 / Unsupported cable record.");
            }
            Require(block != null, "没有电缆结构 / No cable structure.");
            ValidateBlock(block, bodyPending, block.Positions.Count == 2 && block.Length == 0 && explicitCapacitor());
            string item = Path.Combine(root, "ITEM.in"), mode = "";
            s.ItemHash = "";
            if (readItem && File.Exists(item)) {
                byte[] data = ReadBytes(item), prefix; Encoding enc; s.ItemHash = Hash(data);
                var values = new List<string>();
                foreach (string row in Decode(data, out enc, out prefix).Split('\n')) {
                    int eq = row.IndexOf('='); if (eq < 0) continue;
                    string key = row.Substring(0, eq).Trim().TrimEnd(':', '：').Trim();
                    if (HVX.ProjectModel.ProjectMetadataKeys.Canonical(key) == "CoreCount") values.Add(row.Substring(eq + 1).Trim().Trim('"'));
                }
                var modes = values.Select(ParseMode).Where(x => x != CableJointMode.Unknown).Distinct().ToArray();
                if (modes.Length == 1) mode = modes[0].ToString();
            }
            CableJointMode parsed;
            s.Mode = Enum.TryParse(mode, out parsed) ? parsed : CableJointMode.Unknown;
            string[] actual = s.Blocks.Select(b => b.Phase).Distinct().ToArray();
            if (s.Mode == CableJointMode.SinglePhase && actual.Length != 1) s.Mode = CableJointMode.Unknown;
            // Never infer shared geometry merely from equal lengths or duplicate A labels.
            s.PhaseChoices = s.Mode == CableJointMode.SharedThreeCore ? new[] { "ALL" } : actual;
            s.ScopeDescription = (s.Mode == CableJointMode.SharedThreeCore ? "三芯共用结构，不分相；" :
                s.Mode == CableJointMode.SinglePhase ? "单相结构；" : s.Mode == CableJointMode.SeparatePhases ? "单芯三相，按勾选相别添加；" : "负载类型未明确或与结构不一致，仅按文件实际相别添加；") +
                String.Join("，", actual.Select(p => p + "：" + s.Blocks.Count(b => b.Phase == p) + " 段")) +
                "。同名相的各段同时处理，原相别标签不改名。";
            return s;
        }
        static CableJointMode ParseMode(string mode)
        {
            mode = (mode ?? "").Trim().ToLowerInvariant();
            if (new[] { "三芯", "一体三芯", "一体三芯电缆", "一體三芯電纜", "three-core", "integrated three-core", "integrated three-core cable" }.Contains(mode)) return CableJointMode.SharedThreeCore;
            if (new[] { "单相", "单相单芯", "单芯电缆", "單芯電纜", "single-phase", "single-phase single-core", "single-core cable" }.Contains(mode)) return CableJointMode.SinglePhase;
            if (new[] { "三相单芯", "单芯三相", "三相单芯电缆", "单芯三相电缆", "單芯三相電纜", "单芯", "three-phase single-core", "three-phase single-core cable" }.Contains(mode)) return CableJointMode.SeparatePhases;
            return CableJointMode.Unknown;
        }
        static void ValidateBlock(CableJointBlock block, bool bodyPending, bool zeroLengthCapacitor = false)
        {
            Require(block.Positions.Count >= 2 && (block.Length > 0 || zeroLengthCapacitor && block.Positions.Count == 2 && block.Positions.All(position => position == 0)) &&
                !bodyPending && block.BodyLines.Count == block.Positions.Count - 1,
                "电缆端点或区段不完整 / Incomplete cable endpoints or segments.");
        }
        internal static CableJointSnapshot CreatePreview(string phase, double length, string material)
        { return CreatePreview(new[] { phase }, new[] { length }, new[] { material }, CableJointMode.SinglePhase); }
        internal static CableJointSnapshot CreatePreview(string[] phases, double[] lengths, string[] materials, CableJointMode mode)
        {
            Require(phases != null && lengths != null && materials != null && phases.Length > 0 && phases.Length == lengths.Length && phases.Length == materials.Length,
                "仿真电缆参数不完整 / Incomplete preview geometry.");
            var s = new CableJointSnapshot { CablePath = "", ItemHash = "", Mode = mode,
                Encoding = new UTF8Encoding(false, true), Preamble = new byte[0], Newline = "\r\n" };
            var lines = new List<string>();
            for (int i = 0; i < phases.Length; i++) {
                string phase = PhaseName(phases[i]), material = Field(materials[i], false, "材料");
                Require(Finite(lengths[i]) && lengths[i] > 0 && lengths[i] <= 1000000, "仿真长度无效 / Invalid preview length.");
                int start = lines.Count;
                lines.Add("0\t" + phase + "\t\t"); lines.Add("1\t始端\t终端\t0m");
                lines.Add("2\t本体\t" + material + "\t"); lines.Add("1\t终端\t终端\t" + lengths[i].ToString("R", CultureInfo.InvariantCulture) + "m");
                var b = new CableJointBlock { Index = i, Phase = phase };
                b.Positions.AddRange(new[] { 0.0, lengths[i] }); b.AccessoryLines.AddRange(new[] { start + 1, start + 3 }); b.BodyLines.Add(start + 2); s.Blocks.Add(b);
            }
            s.Lines = lines.ToArray(); s.Original = s.Encoding.GetBytes(String.Join(s.Newline, lines));
            s.PhaseChoices = mode == CableJointMode.SharedThreeCore ? new[] { "ALL" } : s.Blocks.Select(b => b.Phase).Distinct().ToArray();
            s.ScopeDescription = "仿真：仅修改显示标记，不写项目文件 / Preview only; project files remain unchanged.";
            return s;
        }
        internal static string Commit(CableJointPlan plan)
        {
            var result = CommitWithResult(plan);
            if (!String.IsNullOrEmpty(result.Warning)) throw new CableJointCommittedException(result);
            return result.BackupPath;
        }

        // Null in production. Isolated tests can reproduce a filesystem failure at
        // the exact post-replacement boundary without altering a real project's ACL.
        internal static Action<string, FileAttributes> RestoreAttributesForTests;

        internal static CableJointCommitResult CommitWithResult(CableJointPlan plan)
        {
            Require(plan != null && !String.IsNullOrWhiteSpace(plan.CablePath) && plan.ResolvedTargets != null && plan.ResolvedTargets.Length > 0,
                "缺少可写项目计划；仿真不能提交 / Preview cannot be committed without a project plan.");
            string root = Path.GetDirectoryName(Path.GetFullPath(plan.CablePath));
            var beforeLock = Read(root);
            Require(String.Equals(beforeLock.CablePath, plan.CablePath, StringComparison.OrdinalIgnoreCase), "保存路径越界 / Invalid metadata path.");
            string lockPath = Path.Combine(root, ".DataBrowser.write.lock");
            string tag = Guid.NewGuid().ToString("N");
            string stage = Path.Combine(root, ".cj-" + tag + ".tmp"), backup = Path.Combine(root, "CABLE.before-joint-" + tag + ".bak");
            var result = new CableJointCommitResult { BackupPath = backup, Warning = "" };
            RequireWritePathLengths(stage, backup, lockPath);
            Require(!File.Exists(lockPath) || (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) == 0, "编辑锁不能是链接 / Invalid metadata lock.");
            using (var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                try {
                    var current = Read(root);
                    Require(Hash(current.Original) == plan.ExpectedHash && current.ItemHash == plan.ExpectedItemHash,
                        "项目已被其他操作修改，请重新打开接头窗口 / Project changed; reopen the joint dialog.");
                    using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(plan.Replacement, 0, plan.Replacement.Length); stream.Flush(true); }
                    current = Read(root);
                    Require(Hash(current.Original) == plan.ExpectedHash && current.ItemHash == plan.ExpectedItemHash, "项目已变化，已取消保存 / Project changed; save cancelled.");
                    // Legacy projects deliberately seal CABLE.in as read-only.
                    // Unlock only this validated file at the actual commit, not
                    // the project directory or its ACL; keep all original flags.
                    FileAttributes originalAttributes = File.GetAttributes(plan.CablePath);
                    bool replaced = false;
                    Exception writeFailure = null;
                    try {
                        if ((originalAttributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(plan.CablePath, originalAttributes & ~FileAttributes.ReadOnly);
                        current = Read(root);
                        Require(Hash(current.Original) == plan.ExpectedHash && current.ItemHash == plan.ExpectedItemHash, "项目已变化，已取消保存 / Project changed; save cancelled.");
                        File.Replace(stage, plan.CablePath, backup, false);
                        replaced = true;
                    } catch (Exception ex) { writeFailure = ex; throw; }
                    finally {
                        try {
                            if (File.GetAttributes(plan.CablePath) != originalAttributes)
                            {
                                var restore = RestoreAttributesForTests;
                                if (restore == null) File.SetAttributes(plan.CablePath, originalAttributes);
                                else restore(plan.CablePath, originalAttributes);
                            }
                        } catch (Exception restoreFailure) {
                            string reason = replaced
                                ? "接头已保存，但无法恢复电缆文件原属性。请检查文件权限；修改前备份： / Joint saved, but original file attributes could not be restored. Check file permissions. Backup: " + backup
                                : "保存未完成，且无法恢复电缆文件原属性。请检查文件权限： / Save did not complete and original file attributes could not be restored. Check file permissions: " + plan.CablePath;
                            if (replaced)
                                result.Warning = reason + Environment.NewLine + restoreFailure.Message + Environment.NewLine +
                                    "请勿重复提交；数据已写入，请检查并恢复文件属性。 / Do not repeat this operation; data was saved. Check and restore the file attributes.";
                            else
                            {
                                if (writeFailure != null) throw new AggregateException(reason, writeFailure, restoreFailure);
                                throw new IOException(reason, restoreFailure);
                            }
                        }
                    }
                    return result;
                } finally { if (File.Exists(stage)) File.Delete(stage); }
            }
        }
    }
}
