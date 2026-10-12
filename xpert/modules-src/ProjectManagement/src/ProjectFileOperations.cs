using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace HVX.Controls
{
    public static class ProjectFileOperations
    {
        internal static Action<string> BeforeRecoverableMoveForTests;
        public static ProjectOperationResult ImportProject(string sourceProjectDirectory, string storageRoot)
        {
            try
            {
                string source = ProjectScanner.NormalizeExistingDirectory(sourceProjectDirectory);
                string root = ProjectScanner.NormalizeExistingDirectory(storageRoot);
                if (!ProjectScanner.IsProjectDirectory(source))
                    throw new InvalidDataException("导入源不是有效项目目录；请选择包含 ITEM.in 与 CABLE.in 的文件夹或其 ITEM.in。");
                if (IsSameOrChild(root, source))
                    throw new IOException("所选项目已位于当前项目数据根目录中，无需重复导入。");
                if (IsSameOrChild(source, root))
                    throw new IOException("项目数据根目录不能位于导入源内部：" + root);
                EnsureProjectIsIdle(source);
                EnsureNoReparsePoints(source);
                ProjectRecord record = ProjectScanner.ReadProject(source);
                string group = SanitizeComponent(record.ProjectName);
                if (string.IsNullOrWhiteSpace(group)) group = SanitizeComponent(new DirectoryInfo(source).Parent == null
                    ? "导入项目" : new DirectoryInfo(source).Parent.Name);
                string leaf = SanitizeComponent(new DirectoryInfo(source).Name);
                if (string.IsNullOrWhiteSpace(leaf)) leaf = "项目_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                string groupPath = SafeCombine(root, group);
                Directory.CreateDirectory(groupPath);
                string destination = UniqueDirectoryPath(SafeCombine(groupPath, leaf));
                CopyDirectory(source, destination);
                if (!ProjectScanner.IsProjectDirectory(destination))
                    throw new InvalidDataException("导入后的目录未通过项目完整性复核。");
                return new ProjectOperationResult
                {
                    Ok = true,
                    ProcessedCount = 1,
                    OutputPaths = new[] { destination },
                    Message = "项目已导入：" + destination
                };
            }
            catch (Exception ex) { return ProjectOperationResult.Failure(BaseMessage(ex)); }
        }

        public static ProjectOperationResult ExportProjects(IEnumerable<ProjectRecord> projects, string destinationRoot)
        {
            var outputs = new List<string>();
            string staging = null;
            try
            {
                ProjectRecord[] selected = ValidProjects(projects);
                if (selected.Length == 0) throw new InvalidOperationException("请至少选择一个项目。");
                string destination = NormalizeDestination(destinationRoot);
                foreach (ProjectRecord project in selected)
                {
                    EnsureProjectIsIdle(project.ProjectPath);
                    RejectDestinationInsideProject(project.ProjectPath, destination);
                    if (!ProjectScanner.IsProjectDirectory(project.ProjectPath))
                        throw new InvalidDataException("导出前复核失败，目录已不是有效项目：" + project.ProjectPath);
                    EnsureNoReparsePoints(project.ProjectPath);
                }
                Directory.CreateDirectory(destination);
                foreach (ProjectRecord project in selected)
                {
                    string group = SanitizeComponent(project.ProjectName);
                    if (string.IsNullOrWhiteSpace(group)) group = "项目";
                    string groupPath = SafeCombine(destination, group);
                    Directory.CreateDirectory(groupPath);
                    string leaf = SanitizeComponent(project.ProjectFolderName);
                    if (string.IsNullOrWhiteSpace(leaf)) leaf = "数据";
                    string target = UniqueDirectoryPath(SafeCombine(groupPath, leaf));
                    staging = SafeCombine(groupPath, ".project-exporting-" + Guid.NewGuid().ToString("N"));
                    CopyDirectory(project.ProjectPath, staging);
                    // Publish only a complete project; report earlier completed
                    // outputs accurately if a later batch item fails.
                    Directory.Move(staging, target);
                    staging = null;
                    outputs.Add(target);
                }
                return new ProjectOperationResult
                {
                    Ok = true,
                    ProcessedCount = outputs.Count,
                    OutputPaths = outputs.ToArray(),
                    Message = "已导出 " + outputs.Count.ToString(CultureInfo.InvariantCulture) + " 个项目。"
                };
            }
            catch (Exception ex) { return ExportFailure(ex, outputs, staging, 0); }
        }

        public static ProjectOperationResult ExportReports(IEnumerable<ProjectRecord> projects, string destinationRoot)
        {
            var outputs = new List<string>();
            string staging = null;
            int skipped = 0;
            try
            {
                ProjectRecord[] selected = ValidProjects(projects);
                if (selected.Length == 0) throw new InvalidOperationException("请至少选择一个项目。");
                string destination = NormalizeDestination(destinationRoot);
                foreach (ProjectRecord project in selected)
                {
                    EnsureProjectIsIdle(project.ProjectPath);
                    RejectDestinationInsideProject(project.ProjectPath, destination);
                    if (!ProjectScanner.IsProjectDirectory(project.ProjectPath))
                        throw new InvalidDataException("导出前复核失败，目录已不是有效项目：" + project.ProjectPath);
                    EnsureNoReparsePoints(project.ProjectPath);
                    foreach (ProjectReportFile report in project.Reports)
                    {
                        if (report == null || string.IsNullOrWhiteSpace(report.FullPath) || !File.Exists(report.FullPath))
                            throw new FileNotFoundException("报告文件不存在或已被移动。", report == null ? string.Empty : report.FullPath);
                        EnsureFileInsideProject(project.ProjectPath, report.FullPath);
                    }
                }
                Directory.CreateDirectory(destination);
                foreach (ProjectRecord project in selected)
                {
                    if (!project.HasReport) { skipped++; continue; }
                    string name = SanitizeComponent(project.ProjectName + "_" +
                        (project.HasTestTime ? project.TestTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) : project.ProjectFolderName));
                    if (string.IsNullOrWhiteSpace(name)) name = "项目报告";
                    string targetRoot = UniqueDirectoryPath(SafeCombine(destination, name));
                    staging = SafeCombine(destination, ".project-exporting-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    var relativeOutputs = new List<string>();
                    foreach (ProjectReportFile report in project.Reports)
                    {
                        string relative = string.IsNullOrWhiteSpace(report.RelativePath)
                            ? Path.GetFileName(report.FullPath) : report.RelativePath;
                        string output = SafeCombineRelative(staging, relative);
                        string parent = Path.GetDirectoryName(output);
                        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                        File.Copy(report.FullPath, output, false);
                        relativeOutputs.Add(relative);
                    }
                    Directory.Move(staging, targetRoot);
                    staging = null;
                    outputs.AddRange(relativeOutputs.Select(relative => SafeCombineRelative(targetRoot, relative)));
                }
                return new ProjectOperationResult
                {
                    Ok = true,
                    ProcessedCount = outputs.Count,
                    SkippedCount = skipped,
                    OutputPaths = outputs.ToArray(),
                    Message = "已导出 " + outputs.Count.ToString(CultureInfo.InvariantCulture) + " 份报告" +
                        (skipped > 0 ? "，" + skipped.ToString(CultureInfo.InvariantCulture) + " 个项目无报告。" : "。")
                };
            }
            catch (Exception ex) { return ExportFailure(ex, outputs, staging, skipped); }
        }

        static ProjectOperationResult ExportFailure(Exception error, List<string> outputs, string staging, int skipped)
        {
            // A leftover staging copy is not a completed export. Preserve and
            // identify it instead of swallowing failed cleanup of locked files.
            string incomplete = !string.IsNullOrEmpty(staging) && Directory.Exists(staging)
                ? "\r\n未完成临时副本（未发布，可人工检查后清理）：" + staging : string.Empty;
            string completed = outputs.Count == 0 ? string.Empty : "\r\n已完整导出的路径：\r\n" + string.Join("\r\n", outputs);
            return new ProjectOperationResult { Ok = false, ProcessedCount = outputs.Count, OutputPaths = outputs.ToArray(),
                SkippedCount = skipped, Message = "导出未全部完成。",
                Error = "导出未全部完成；已完整导出 " + outputs.Count.ToString(CultureInfo.InvariantCulture) +
                    " 项。原因：" + BaseMessage(error) + completed + incomplete };
        }

        public static ProjectOperationResult DeleteProjects(string storageRoot, IEnumerable<ProjectRecord> projects)
        {
            string root;
            ProjectRecord[] selected;
            List<string> paths = new List<string>();
            try
            {
                root = ProjectScanner.NormalizeExistingDirectory(storageRoot);
                selected = ValidProjects(projects)
                    .OrderByDescending(p => p.ProjectPath.Length).ToArray();
                if (selected.Length == 0) throw new InvalidOperationException("请至少选择一个项目。");
                foreach (ProjectRecord project in selected)
                {
                    string path = ProjectScanner.NormalizeExistingDirectory(project.ProjectPath);
                    EnsureStrictChild(root, path);
                    if (!ProjectScanner.IsProjectDirectory(path))
                        throw new InvalidDataException("删除前复核失败，目录已不是有效项目：" + path);
                    EnsureProjectIsIdle(path);
                    EnsureNoReparsePoints(path);
                    paths.Add(path);
                }
                for (int i = 0; i < paths.Count; i++) for (int j = i + 1; j < paths.Count; j++)
                    if (IsSameOrChild(paths[i], paths[j]) || IsSameOrChild(paths[j], paths[i]))
                        throw new IOException("同批操作不能同时选择包含关系的项目目录，请分别处理。");
            }
            catch (Exception ex)
            {
                return ProjectOperationResult.Failure("删除预检失败，尚未删除任何项目：" + BaseMessage(ex));
            }

            List<string> removed = new List<string>();
            List<string> recovery = new List<string>();
            foreach (string path in paths)
            {
                try
                {
                    EnsureStrictChild(root, path);
                    if (!ProjectScanner.IsProjectDirectory(path))
                        throw new InvalidDataException("删除执行前复核失败，目录已不是有效项目：" + path);
                    EnsureProjectIsIdle(path);
                    EnsureNoReparsePoints(path);
                    // An atomic same-parent rename cannot partially delete the
                    // metadata before failing on a read-only raw file. Preserve
                    // original attributes and do not purge recovery directories.
                    string parent = Path.GetDirectoryName(path);
                    string quarantine = SafeCombine(parent, ".project-deleted-" +
                        DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                    EnsureStrictChild(root, quarantine);
                    // Write the restore mapping outside the original directory
                    // before the rename. After a crash check path existence; this
                    // record alone must not be taken as proof the move completed.
                    File.WriteAllText(quarantine + ".restore.json", new JavaScriptSerializer().Serialize(new {
                        Schema = "HVX.RecoverableProjectRemoval.1", OriginalPath = path, RecoveryPath = quarantine,
                        RequestedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        Instruction = "Check which directory exists. Close project applications. Restore by same-parent rename only when OriginalPath does not exist; never overwrite. Original file contents and attributes are preserved."
                    }));
                    if (BeforeRecoverableMoveForTests != null) BeforeRecoverableMoveForTests(path);
                    Directory.Move(path, quarantine);
                    removed.Add(path);
                    recovery.Add(quarantine);
                }
                catch (Exception ex)
                {
                    return new ProjectOperationResult
                    {
                        Ok = false,
                        Error = "移出项目列表发生部分失败；已可恢复移出 " + removed.Count.ToString(CultureInfo.InvariantCulture) +
                            " 个，当前项目未移动：" + path + "；原因：" + BaseMessage(ex) + RecoveryMessage(removed, recovery),
                        Message = "未全部完成；原始数据未永久删除。",
                        ProcessedCount = removed.Count,
                        SkippedCount = paths.Count - removed.Count,
                        OutputPaths = removed.ToArray(), RecoveryPaths = recovery.ToArray()
                    };
                }
            }
            return new ProjectOperationResult
            {
                Ok = true,
                ProcessedCount = removed.Count,
                OutputPaths = removed.ToArray(),
                RecoveryPaths = recovery.ToArray(),
                Message = "已从列表移出 " + removed.Count.ToString(CultureInfo.InvariantCulture) + " 个项目。" + RecoveryMessage(removed, recovery)
            };
        }

        static string RecoveryMessage(List<string> original, List<string> recovery)
        {
            string result = "\r\n原始数据与只读属性保留，未释放磁盘空间。恢复时先关闭使用该项目的程序，确认原路径不存在，再将隔离文件夹重命名为原文件夹名称；不要覆盖已有目录。";
            for (int i = 0; i < recovery.Count; i++) result += "\r\n恢复来源：" + recovery[i] + "\r\n原路径：" + original[i];
            return result;
        }

        public static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            string source = ProjectScanner.NormalizeExistingDirectory(sourceDirectory);
            string destination = Path.GetFullPath(destinationDirectory);
            if (Directory.Exists(destination))
                throw new IOException("目标目录已存在：" + destination);
            if (IsSameOrChild(source, destination))
                throw new IOException("目标目录不能位于复制源内部：" + destination);
            EnsureNoReparsePoints(source);
            Directory.CreateDirectory(destination);
            Stack<Tuple<string, string>> pending = new Stack<Tuple<string, string>>();
            pending.Push(Tuple.Create(source, destination));
            try
            {
                while (pending.Count > 0)
                {
                    Tuple<string, string> pair = pending.Pop();
                    foreach (string file in Directory.GetFiles(pair.Item1, "*", SearchOption.TopDirectoryOnly))
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("复制源包含不支持的重解析文件：" + file);
                        string target = Path.Combine(pair.Item2, Path.GetFileName(file));
                        File.Copy(file, target, false);
                        try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file)); } catch { }
                    }
                    foreach (string child in Directory.GetDirectories(pair.Item1, "*", SearchOption.TopDirectoryOnly))
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("复制源包含不支持的重解析目录：" + child);
                        string target = Path.Combine(pair.Item2, Path.GetFileName(child));
                        Directory.CreateDirectory(target);
                        pending.Push(Tuple.Create(child, target));
                    }
                }
            }
            catch
            {
                try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
                throw;
            }
        }

        public static string ProjectDirectoryFromSelection(string selectedPath)
        {
            if (string.IsNullOrWhiteSpace(selectedPath)) throw new ArgumentException("未选择导入路径。");
            string full = Path.GetFullPath(selectedPath.Trim());
            if (File.Exists(full))
            {
                if (!string.Equals(Path.GetFileName(full), "ITEM.in", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("文件导入仅接受 ITEM.in。");
                return Path.GetDirectoryName(full);
            }
            if (Directory.Exists(full)) return full;
            throw new FileNotFoundException("导入路径不存在。", full);
        }

        public static string SanitizeComponent(string value)
        {
            string text = (value ?? string.Empty).Trim();
            foreach (char character in Path.GetInvalidFileNameChars()) text = text.Replace(character, '_');
            text = text.TrimEnd(' ', '.');
            if (text.Length > 120) text = text.Substring(0, 120).TrimEnd(' ', '.');
            return text;
        }

        static ProjectRecord[] ValidProjects(IEnumerable<ProjectRecord> projects)
        {
            return (projects ?? Enumerable.Empty<ProjectRecord>()).Where(p => p != null &&
                !string.IsNullOrWhiteSpace(p.ProjectPath) && Directory.Exists(p.ProjectPath))
                .GroupBy(p => Path.GetFullPath(p.ProjectPath), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToArray();
        }

        static string NormalizeDestination(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("目标文件夹不能为空。");
            string full = Path.GetFullPath(path.Trim());
            return ProjectScanner.NormalizeDirectoryPath(full);
        }

        static void EnsureFileInsideProject(string projectPath, string filePath)
        {
            string project = ProjectScanner.NormalizeExistingDirectory(projectPath);
            string file = Path.GetFullPath(filePath);
            EnsureStrictChild(project, file);
        }

        static void RejectDestinationInsideProject(string projectPath, string destination)
        {
            if (IsSameOrChild(projectPath, destination))
                throw new IOException("导出目标不能位于被导出的项目内部：" + destination);
        }

        static void EnsureNoReparsePoints(string root)
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("项目目录是重解析点，操作已停止：" + root);
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                foreach (string file in Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("项目内包含重解析文件，操作已停止：" + file);
                }
                foreach (string child in Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("项目内包含重解析目录，操作已停止：" + child);
                    pending.Push(child);
                }
            }
        }

        static void EnsureProjectIsIdle(string projectPath)
        {
            string lockPath = Path.Combine(
                ProjectScanner.NormalizeExistingDirectory(projectPath),
                ".DataBrowser.write.lock");
            if (!File.Exists(lockPath)) return;

            try
            {
                using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
            catch (IOException ex)
            {
                throw new IOException("项目正在写入，当前操作已停止：" + projectPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException("无法独占检查项目写锁，当前操作已停止：" + projectPath, ex);
            }
        }

        static string UniqueDirectoryPath(string desired)
        {
            if (!Directory.Exists(desired) && !File.Exists(desired)) return desired;
            for (int index = 1; index <= 9999; index++)
            {
                string candidate = desired + "_" + index.ToString("00", CultureInfo.InvariantCulture);
                if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
            }
            throw new IOException("无法创建唯一目标目录：" + desired);
        }

        static string SafeCombine(string root, string component)
        {
            string fullRoot = ProjectScanner.NormalizeDirectoryPath(root);
            string full = Path.GetFullPath(Path.Combine(fullRoot, component));
            EnsureStrictChild(fullRoot, full);
            return full;
        }

        static string SafeCombineRelative(string root, string relative)
        {
            string fullRoot = ProjectScanner.NormalizeDirectoryPath(root);
            string normalized = (relative ?? string.Empty).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(fullRoot, normalized));
            EnsureStrictChild(fullRoot, full);
            return full;
        }

        static void EnsureStrictChild(string parent, string child)
        {
            string root = ProjectScanner.NormalizeDirectoryPath(parent);
            string candidate = ProjectScanner.NormalizeDirectoryPath(child);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) || !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("路径超出允许范围：" + candidate);
        }

        static bool IsSameOrChild(string parent, string candidate)
        {
            string root = ProjectScanner.NormalizeDirectoryPath(parent);
            string path = ProjectScanner.NormalizeDirectoryPath(candidate);
            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static string BaseMessage(Exception ex)
        {
            Exception current = ex;
            while (current != null && current.InnerException != null) current = current.InnerException;
            return ((current == null ? string.Empty : current.Message) ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
