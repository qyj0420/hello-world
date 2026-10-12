using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class LegacyMigrationIssue
    {
        public string Code { get; set; }
        public string RelativePath { get; set; }
        public string Detail { get; set; }
        public bool BlocksConversion { get; set; }
    }

    internal sealed class LegacySourceFile
    {
        public string RelativePath { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public Guid RecordId { get; set; }
        public string Representation { get; set; }

        [ScriptIgnore]
        public string FullPath { get; set; }
    }

    internal sealed class LegacyMetadataField
    {
        public int Line { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public string RawLine { get; set; }
    }

    internal sealed class LegacyMetadataDocument
    {
        public string Schema { get; set; }
        public string SourceFile { get; set; }
        public string Encoding { get; set; }
        public string RawText { get; set; }
        public Dictionary<string, string> Normalized { get; set; }
        public Dictionary<string, string> Fields { get; set; }
        public List<LegacyMetadataField> Entries { get; set; }
        public List<string[]> Rows { get; set; }
        public List<string> OtherLines { get; set; }
    }

    internal sealed class LegacyProjectInspection
    {
        public string SourceRoot { get; set; }
        public List<LegacySourceFile> Files { get; set; }
        public List<LegacyMigrationIssue> Issues { get; set; }
        public LegacyMetadataDocument ProjectMetadata { get; set; }
        public LegacyMetadataDocument CableMetadata { get; set; }
        public bool CanConvert { get { return !Issues.Any(issue => issue.BlocksConversion); } }
    }

    internal sealed class LegacyMigrationIndex
    {
        public string Schema { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string SourceFolderName { get; set; }
        public Guid ProjectRecordId { get; set; }
        public Guid CableRecordId { get; set; }
        public List<LegacySourceFile> Files { get; set; }
        public List<LegacyMigrationIssue> Issues { get; set; }
    }

    internal sealed class LegacyProjectConversionResult
    {
        public string TargetPath { get; set; }
        public Guid ProjectId { get; set; }
        public Guid MigrationIndexId { get; set; }
        public int FileCount { get; set; }
        public int IssueCount { get; set; }
    }

    internal sealed class LegacyConversionProgress
    {
        public string Phase { get; set; }
        public int CompletedFiles { get; set; }
        public int TotalFiles { get; set; }
        public long CompletedBytes { get; set; }
        public long TotalBytes { get; set; }
        public string CurrentRelativePath { get; set; }
    }

    /// <summary>
    /// Standalone, read-only legacy importer. It intentionally does not alter the
    /// existing ITEM.in/CABLE.in readers or advertise imported records to a UI.
    /// Every source file is preserved as encrypted LegacySource (8 MiB chunks),
    /// including a marker record for a zero-byte file that cannot be streamed.
    /// </summary>
    internal static class LegacyProjectConverter
    {
        private const int ChunkBytes = 8 * 1024 * 1024;
        private const int MaximumMetadataBytes = 16 * 1024 * 1024;
        private const long MaximumStreamBytes = 512L * ChunkBytes;
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

        public static LegacyProjectInspection Inspect(string sourceDirectory)
        {
            return Inspect(sourceDirectory, CancellationToken.None, null);
        }

        public static LegacyProjectInspection Inspect(string sourceDirectory,
            CancellationToken cancellationToken, Action<LegacyConversionProgress> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = RequireSourceDirectory(sourceDirectory);
            var inspection = new LegacyProjectInspection
            {
                SourceRoot = root,
                Files = new List<LegacySourceFile>(),
                Issues = new List<LegacyMigrationIssue>()
            };
            if (HasReparseAncestor(root))
            {
                AddIssue(inspection, "UnsafeReparsePath", ".",
                    "源目录或上级目录是链接；为避免读取到目录外的数据，转换已禁用。", true);
                return inspection;
            }

            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(directory); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    AddIssue(inspection, "UnreadableDirectory", SafeRelative(root, prefix, directory),
                        "目录无法读取；必须先修复权限或介质错误。", true);
                    continue;
                }
                Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
                foreach (string entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = SafeRelative(root, prefix, entry);
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        AddIssue(inspection, "UnreadableEntry", relative, "条目无法读取。", true);
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        AddIssue(inspection, "UnsafeReparsePath", relative,
                            "链接或重解析点未被跟随；需人工核对。", true);
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }
                    var file = new LegacySourceFile
                    {
                        FullPath = entry,
                        RelativePath = relative,
                        Kind = FileKind(relative),
                        RecordId = Guid.Empty,
                        Representation = "stream"
                    };
                    try
                    {
                        using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            file.Size = input.Length;
                            file.Sha256 = HashStream(input, cancellationToken);
                            if (input.Length != file.Size)
                                throw new IOException("File changed while scanning.");
                        }
                        if (file.Size == 0)
                        {
                            file.Representation = "zero-marker";
                            AddIssue(inspection, "ZeroByteFile", relative,
                                "零字节源文件将用加密空文件标记保存。", false);
                        }
                        if (file.Size > MaximumStreamBytes)
                            AddIssue(inspection, "OversizedFile", relative,
                                "源文件超过当前流式记录容量，不能安全转换。", true);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        AddIssue(inspection, "UnreadableFile", relative,
                            "源文件无法完整读取；未静默跳过。", true);
                    }
                    inspection.Files.Add(file);
                    Report(progress, "扫描源文件", inspection.Files.Count, 0, 0, 0, relative);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            inspection.Files.Sort((left, right) => Names.Compare(left.RelativePath, right.RelativePath));
            CheckCompanions(inspection);
            inspection.ProjectMetadata = ReadMetadata(inspection, "ITEM.in", cancellationToken);
            inspection.CableMetadata = ReadMetadata(inspection, "CABLE.in", cancellationToken);
            Report(progress, "扫描完成", inspection.Files.Count, inspection.Files.Count,
                0, inspection.Files.Sum(file => Math.Max(0L, file.Size)), String.Empty);
            return inspection;
        }

        public static LegacyProjectConversionResult Convert(string sourceDirectory, string targetDirectory)
        {
            return Convert(sourceDirectory, targetDirectory, CancellationToken.None, null);
        }

        public static LegacyProjectConversionResult Convert(string sourceDirectory, string targetDirectory,
            CancellationToken cancellationToken, Action<LegacyConversionProgress> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourceRoot = RequireSourceDirectory(sourceDirectory);
            string target = RequireNewTarget(targetDirectory, sourceRoot);
            LegacyProjectInspection inspection = Inspect(sourceRoot, cancellationToken, progress);
            if (!inspection.CanConvert)
                throw new InvalidDataException("旧项目包含不安全或无法读取的条目；请先查看只读扫描的问题列表。", null);
            // SecureProjectStore creates its own nested .stage-* directory and
            // long authenticated filenames. Keep our sibling name short for
            // .NET Framework's legacy MAX_PATH limit.
            string staging = Path.Combine(Path.GetDirectoryName(target),
                ".owm-" + Guid.NewGuid().ToString("N").Substring(0, 16));
            if (Directory.Exists(staging) || File.Exists(staging))
                throw new IOException("迁移暂存目录已存在；请重试。");
            bool ownsStaging = false;
            Guid indexId;
            Guid projectId;
            long totalBytes = inspection.Files.Sum(file => Math.Max(0L, file.Size));
            long completedBytes = 0;
            int completedFiles = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] projectJson = JsonBytes(inspection.ProjectMetadata);
                byte[] cableJson = JsonBytes(inspection.CableMetadata);
                using (SecureProjectStore store = SecureProjectStore.Create(staging, projectJson, cableJson))
                {
                    ownsStaging = true;
                    projectId = store.ProjectId;
                    foreach (LegacySourceFile file in inspection.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Report(progress, "加密保存", completedFiles, inspection.Files.Count,
                            completedBytes, totalBytes, file.RelativePath);
                        if (file.Size == 0)
                        {
                            byte[] marker = JsonBytes(new { schema = "legacy-zero-file-v1",
                                relativePath = file.RelativePath, size = 0 });
                            using (var source = new FileStream(file.FullPath, FileMode.Open,
                                FileAccess.Read, FileShare.Read))
                                if (source.Length != 0) throw new IOException("源文件在转换期间改变：" + file.RelativePath);
                            file.RecordId = store.Append(ProjectRecordKind.LegacySource, marker, Guid.Empty);
                            completedFiles++;
                            continue;
                        }
                        using (var source = new FileStream(file.FullPath, FileMode.Open,
                            FileAccess.Read, FileShare.Read))
                        using (var tracked = new HashingReadStream(source, cancellationToken,
                            bytes => Report(progress, "加密保存", completedFiles, inspection.Files.Count,
                                completedBytes + bytes, totalBytes, file.RelativePath)))
                        {
                            if (source.Length != file.Size)
                                throw new IOException("源文件在转换期间改变：" + file.RelativePath);
                            file.RecordId = store.AppendStream(ProjectRecordKind.LegacySource, tracked, Guid.Empty);
                            if (tracked.BytesRead != file.Size ||
                                !String.Equals(tracked.FinishHash(), file.Sha256, StringComparison.Ordinal))
                                throw new IOException("源文件在转换期间改变：" + file.RelativePath);
                        }
                        completedBytes += file.Size;
                        completedFiles++;
                    }
                    var index = new LegacyMigrationIndex
                    {
                        Schema = "legacy-migration-index-v1",
                        CreatedUtc = DateTime.UtcNow,
                        SourceFolderName = Path.GetFileName(inspection.SourceRoot.TrimEnd(
                            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        ProjectRecordId = store.List(ProjectRecordKind.Project).Single(),
                        CableRecordId = store.List(ProjectRecordKind.Cable).Single(),
                        Files = inspection.Files,
                        Issues = inspection.Issues
                    };
                    indexId = store.Append(ProjectRecordKind.MigrationIndex, JsonBytes(index), Guid.Empty);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, "验证暂存项目", completedFiles, inspection.Files.Count,
                    completedBytes, totalBytes, String.Empty);
                VerifyStaging(staging, inspection.Files, indexId, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ConfirmSourceUnchanged(inspection, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(target) || File.Exists(target))
                    throw new IOException("目标项目目录已存在；不会覆盖任何已有数据。");
                Directory.Move(staging, target);
                ownsStaging = false;
                Report(progress, "完成", completedFiles, inspection.Files.Count,
                    completedBytes, totalBytes, String.Empty);
                return new LegacyProjectConversionResult
                {
                    TargetPath = target,
                    ProjectId = projectId,
                    MigrationIndexId = indexId,
                    FileCount = inspection.Files.Count,
                    IssueCount = inspection.Issues.Count
                };
            }
            finally
            {
                if (ownsStaging && Directory.Exists(staging))
                {
                    if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0)
                        Directory.Delete(staging);
                    else
                        Directory.Delete(staging, true);
                }
            }
        }

        private static void VerifyStaging(string staging, IList<LegacySourceFile> files,
            Guid indexId, CancellationToken cancellationToken)
        {
            using (SecureProjectStore validation = SecureProjectStore.Open(staging))
            {
                ProjectRecord indexRecord = validation.Read(ProjectRecordKind.MigrationIndex, indexId);
                LegacyMigrationIndex index = Serializer().Deserialize<LegacyMigrationIndex>(
                    Encoding.UTF8.GetString(indexRecord.Data));
                if (index == null || index.Schema != "legacy-migration-index-v1" ||
                    index.Files == null || index.Files.Count != files.Count ||
                    index.ProjectRecordId == Guid.Empty || index.CableRecordId == Guid.Empty)
                    throw new InvalidDataException("暂存迁移索引校验失败。");
                foreach (LegacySourceFile file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.RecordId == Guid.Empty ||
                        !index.Files.Any(item => item.RecordId == file.RecordId &&
                            item.RelativePath == file.RelativePath && item.Size == file.Size &&
                            item.Sha256 == file.Sha256))
                        throw new InvalidDataException("迁移源文件索引不完整：" + file.RelativePath);
                    if (file.Size == 0)
                    {
                        ProjectRecord marker = validation.Read(ProjectRecordKind.LegacySource, file.RecordId);
                        if (Encoding.UTF8.GetString(marker.Data).IndexOf("legacy-zero-file-v1",
                            StringComparison.Ordinal) < 0)
                            throw new InvalidDataException("空文件标记校验失败：" + file.RelativePath);
                    }
                    else
                    {
                        using (var sink = new HashingWriteStream(cancellationToken))
                        {
                            validation.ReadStream(ProjectRecordKind.LegacySource, file.RecordId, sink);
                            if (sink.BytesWritten != file.Size ||
                                !String.Equals(sink.FinishHash(), file.Sha256, StringComparison.Ordinal))
                                throw new InvalidDataException("源文件还原摘要不匹配：" + file.RelativePath);
                        }
                    }
                }
            }
        }

        private static void ConfirmSourceUnchanged(LegacyProjectInspection original,
            CancellationToken cancellationToken)
        {
            LegacyProjectInspection current = Inspect(original.SourceRoot, cancellationToken, null);
            if (!current.CanConvert || current.Files.Count != original.Files.Count)
                throw new IOException("源项目在转换期间发生变化；未发布目标，请重新扫描。");
            for (int index = 0; index < original.Files.Count; index++)
            {
                LegacySourceFile before = original.Files[index];
                LegacySourceFile after = current.Files[index];
                if (!String.Equals(before.RelativePath, after.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                    before.Size != after.Size ||
                    !String.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal))
                    throw new IOException("源项目在转换期间发生变化；未发布目标，请重新扫描。");
            }
        }

        private static LegacyMetadataDocument ReadMetadata(LegacyProjectInspection inspection,
            string name, CancellationToken cancellationToken)
        {
            var document = new LegacyMetadataDocument
            {
                Schema = "legacy-metadata-v1",
                SourceFile = name,
                Encoding = "missing",
                RawText = String.Empty,
                Normalized = new Dictionary<string, string>(Names),
                Fields = new Dictionary<string, string>(Names),
                Entries = new List<LegacyMetadataField>(),
                Rows = new List<string[]>(),
                OtherLines = new List<string>()
            };
            LegacySourceFile file = inspection.Files.FirstOrDefault(item =>
                String.Equals(item.RelativePath, name, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                AddIssue(inspection, "MissingMetadata", name,
                    "缺少项目元数据；已建立空的规范化记录供人工修复。", false);
                return document;
            }
            if (file.Size == 0) return document;
            if (file.Size > MaximumMetadataBytes)
            {
                AddIssue(inspection, "MetadataTooLarge", name,
                    "元数据过大，保留原始字节但未尝试解析。", false);
                return document;
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file.FullPath); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                AddIssue(inspection, "UnreadableMetadata", name,
                    "元数据无法解析；原始文件仍列入源清单。", true);
                return document;
            }
            using (SHA256 digest = SHA256.Create())
                if (bytes.LongLength != file.Size || !String.Equals(
                    BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", String.Empty),
                    file.Sha256, StringComparison.Ordinal))
                {
                    AddIssue(inspection, "ChangedDuringScan", name,
                        "元数据在扫描期间发生变化；请重新扫描。", true);
                    return document;
                }
            string content;
            try { content = DecodeMetadata(bytes, out string encodingName); document.Encoding = encodingName; }
            catch (DecoderFallbackException)
            {
                AddIssue(inspection, "UndecodableMetadata", name,
                    "元数据不是有效的 UTF-8 或 GBK；原始字节仍被保存。", false);
                document.Encoding = "undecodable";
                return document;
            }
            document.RawText = content;
            using (var lines = new StringReader(content))
            {
                string raw;
                int number = 0;
                while ((raw = lines.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    number++;
                    if (String.Equals(name, "CABLE.in", StringComparison.OrdinalIgnoreCase) &&
                        raw.IndexOf('\t') >= 0)
                    {
                        string[] cells = raw.Split('\t');
                        document.Rows.Add(cells);
                        if (cells.Any(LooksLikeAbsolutePath)) FlagAbsolutePath(inspection, name);
                        continue;
                    }
                    string key;
                    string value;
                    if (!TryParseField(raw, out key, out value))
                    {
                        document.OtherLines.Add(raw);
                        if (LooksLikeAbsolutePath(raw)) FlagAbsolutePath(inspection, name);
                        continue;
                    }
                    document.Entries.Add(new LegacyMetadataField
                    {
                        Line = number, Key = key, Value = value, RawLine = raw
                    });
                    document.Fields[key] = value;
                    string normalized = NormalizeKey(key);
                    if (normalized != null) document.Normalized[normalized] = value;
                    if (LooksLikeAbsolutePath(value)) FlagAbsolutePath(inspection, name);
                }
            }
            return document;
        }

        private static bool TryParseField(string raw, out string key, out string value)
        {
            key = null;
            value = null;
            if (String.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("[", StringComparison.Ordinal))
                return false;
            int colon = raw.IndexOfAny(new[] { ':', '：' });
            int equals = raw.IndexOf('=');
            int separator = colon >= 0 ? colon : equals;
            if (separator <= 0) return false;
            key = raw.Substring(0, separator).Trim().TrimEnd(':', '：');
            value = raw.Substring(separator + 1).Trim();
            if (value.StartsWith("=", StringComparison.Ordinal)) value = value.Substring(1).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
            return key.Length != 0;
        }

        private static string NormalizeKey(string key)
        {
            switch (key.Trim().TrimEnd('：', ':'))
            {
                case "所属单位": return "organization";
                case "测试人员": return "operator";
                case "测试地点": return "testLocation";
                case "日期": return "testDate";
                case "时间": return "testTime";
                case "始端地点": return "startLocation";
                case "终端地点": return "endLocation";
                case "始端开关类型": return "startTerminationType";
                case "终端开关类型": return "endTerminationType";
                case "电缆长度": return "cableLength";
                case "相电压": return "phaseVoltage";
                case "电缆芯数": return "coreCount";
                case "线路名称": return "lineName";
                case "电缆型号": return "cableModel";
                case "加压模式": return "pressureMode";
                default: return null;
            }
        }

        private static string DecodeMetadata(byte[] bytes, out string name)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                name = "UTF-8-BOM";
                return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            }
            try
            {
                string text = new UTF8Encoding(false, true).GetString(bytes);
                name = "UTF-8";
                return text;
            }
            catch (DecoderFallbackException)
            {
                name = "GBK";
                return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(bytes);
            }
        }

        private static bool LooksLikeAbsolutePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            string text = value.Trim();
            if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.StartsWith("\\\\", StringComparison.Ordinal) ||
                text.StartsWith("//", StringComparison.Ordinal)) return true;
            if (Path.IsPathRooted(text)) return true;
            return text.Length >= 3 && Char.IsLetter(text[0]) && text[1] == ':' &&
                (text[2] == '\\' || text[2] == '/');
        }

        private static void FlagAbsolutePath(LegacyProjectInspection inspection, string name)
        {
            if (!inspection.Issues.Any(issue => issue.Code == "AbsolutePathReference" &&
                String.Equals(issue.RelativePath, name, StringComparison.OrdinalIgnoreCase)))
                AddIssue(inspection, "AbsolutePathReference", name,
                    "元数据包含绝对路径；未跟随或复制项目外部文件。", false);
        }

        private static void CheckCompanions(LegacyProjectInspection inspection)
        {
            var dem = inspection.Files.Where(file => file.Kind == "DEM")
                .GroupBy(file => Stem(file), Names).ToDictionary(group => group.Key,
                    group => group.ToList(), Names);
            var loc = inspection.Files.Where(file => file.Kind == "LOC")
                .GroupBy(file => Stem(file), Names).ToDictionary(group => group.Key,
                    group => group.ToList(), Names);
            var analyses = new HashSet<string>(inspection.Files.Where(file => file.Kind == "Analysis")
                .Select(Stem), Names);
            var stems = new HashSet<string>(dem.Keys, Names);
            stems.UnionWith(loc.Keys);
            foreach (string stem in stems.OrderBy(item => item, Names))
            {
                List<LegacySourceFile> demFiles;
                List<LegacySourceFile> locFiles;
                bool hasDem = dem.TryGetValue(stem, out demFiles);
                bool hasLoc = loc.TryGetValue(stem, out locFiles);
                string representative = hasDem ? demFiles[0].RelativePath : locFiles[0].RelativePath;
                if (hasDem && !hasLoc)
                    AddIssue(inspection, "OrphanDEM", representative,
                        "该 DEM 找不到同名 LOC；原始文件仍保留。", false);
                if (hasLoc && !hasDem)
                    AddIssue(inspection, "OrphanLOC", representative,
                        "该 LOC 找不到同名 DEM；原始文件仍保留。", false);
                if (!analyses.Contains(stem))
                    AddIssue(inspection, "MissingAnalysis", representative,
                        "未找到同名 Analysis 结果；不会自动生成或臆造分析数据。", false);
            }
        }

        private static string Stem(LegacySourceFile file)
        {
            string stem = Path.GetFileNameWithoutExtension(file.RelativePath);
            if (stem.EndsWith(".analysis", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - ".analysis".Length);
            return stem;
        }

        private static string FileKind(string relative)
        {
            if (String.Equals(relative, "ITEM.in", StringComparison.OrdinalIgnoreCase)) return "ITEM";
            if (String.Equals(relative, "CABLE.in", StringComparison.OrdinalIgnoreCase)) return "CABLE";
            string extension = Path.GetExtension(relative);
            if (String.Equals(extension, ".dem", StringComparison.OrdinalIgnoreCase)) return "DEM";
            if (String.Equals(extension, ".loc", StringComparison.OrdinalIgnoreCase)) return "LOC";
            if (String.Equals(extension, ".cal", StringComparison.OrdinalIgnoreCase)) return "Calibration";
            if (String.Equals(extension, ".analysis", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".ana", StringComparison.OrdinalIgnoreCase) ||
                relative.Split('/').Any(part => String.Equals(part, "Analysis", StringComparison.OrdinalIgnoreCase)))
                return "Analysis";
            return "Other";
        }

        private static string RequireSourceDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("源项目目录不能为空。", "path");
            string full = Path.GetFullPath(path.Trim());
            if (String.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择具体旧项目目录，不要选择整个磁盘。", "path");
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("源项目目录不存在：" + full);
            return full;
        }

        private static string RequireNewTarget(string path, string source)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("目标项目目录不能为空。", "path");
            string full = Path.GetFullPath(path.Trim());
            if (Directory.Exists(full) || File.Exists(full))
                throw new IOException("目标项目目录已存在；不会覆盖任何已有数据。");
            string parent = Path.GetDirectoryName(full);
            if (String.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("目标项目上级目录不存在：" + parent);
            if (HasReparseAncestor(parent))
                throw new InvalidDataException("目标项目路径包含链接目录。");
            string sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (String.Equals(full, source, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("目标目录不得位于源项目内。");
            return full;
        }

        private static bool HasReparseAncestor(string fullPath)
        {
            string current = Path.GetFullPath(fullPath);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                string parent = Path.GetDirectoryName(current);
                if (String.IsNullOrEmpty(parent) || String.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return false;
        }

        private static string SafeRelative(string root, string prefix, string full)
        {
            if (String.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return ".";
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("目录条目不在源项目内。");
            string relative = full.Substring(prefix.Length).Replace('\\', '/');
            if (relative.Length == 0 || relative.StartsWith("/", StringComparison.Ordinal) ||
                relative.Split('/').Any(part => part == ".." || part == "." || part.Length == 0))
                throw new InvalidDataException("源文件相对路径无效。");
            return relative;
        }

        private static void AddIssue(LegacyProjectInspection inspection, string code,
            string relative, string detail, bool blocks)
        {
            inspection.Issues.Add(new LegacyMigrationIssue
            {
                Code = code, RelativePath = relative, Detail = detail, BlocksConversion = blocks
            });
        }

        private static string HashStream(Stream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[ChunkBytes];
            using (SHA256 sha = SHA256.Create())
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", String.Empty);
            }
        }

        private static byte[] JsonBytes(object value)
        {
            return Encoding.UTF8.GetBytes(Serializer().Serialize(value));
        }

        private static void Report(Action<LegacyConversionProgress> callback, string phase,
            int completedFiles, int totalFiles, long completedBytes, long totalBytes, string currentPath)
        {
            if (callback == null) return;
            callback(new LegacyConversionProgress
            {
                Phase = phase,
                CompletedFiles = completedFiles,
                TotalFiles = totalFiles,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes,
                CurrentRelativePath = currentPath
            });
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024,
                RecursionLimit = 128 };
        }

        private sealed class HashingReadStream : Stream
        {
            private readonly Stream source;
            private readonly SHA256 hash = SHA256.Create();
            private readonly CancellationToken cancellationToken;
            private readonly Action<long> onProgress;
            private bool complete;
            public long BytesRead { get; private set; }
            public HashingReadStream(Stream value, CancellationToken token, Action<long> progress)
            {
                source = value;
                cancellationToken = token;
                onProgress = progress;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, offset, count);
                if (read == 0)
                {
                    if (!complete) { hash.TransformFinalBlock(new byte[0], 0, 0); complete = true; }
                }
                else
                {
                    hash.TransformBlock(buffer, offset, read, buffer, offset);
                    BytesRead += read;
                    if (onProgress != null) onProgress(BytesRead);
                }
                return read;
            }
            public string FinishHash()
            {
                if (!complete) throw new InvalidOperationException("源流尚未读完。");
                return BitConverter.ToString(hash.Hash).Replace("-", String.Empty);
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return source.Length; } }
            public override long Position { get { return BytesRead; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { hash.Dispose(); source.Dispose(); }
                base.Dispose(disposing);
            }
        }

        private sealed class HashingWriteStream : Stream
        {
            private readonly SHA256 hash = SHA256.Create();
            private readonly CancellationToken cancellationToken;
            private bool complete;
            public long BytesWritten { get; private set; }
            public HashingWriteStream(CancellationToken token) { cancellationToken = token; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (complete) throw new InvalidOperationException("摘要已完成。");
                hash.TransformBlock(buffer, offset, count, buffer, offset);
                BytesWritten += count;
            }
            public string FinishHash()
            {
                if (!complete) { hash.TransformFinalBlock(new byte[0], 0, 0); complete = true; }
                return BitConverter.ToString(hash.Hash).Replace("-", String.Empty);
            }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { return BytesWritten; } }
            public override long Position { get { return BytesWritten; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing) hash.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
