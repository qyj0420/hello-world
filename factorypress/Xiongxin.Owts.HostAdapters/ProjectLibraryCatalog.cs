using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Contracts.Project;
using Xiongxin.Owts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>工程库种类：SQLite 真源，或可导入的遗留目录。</summary>
    public enum ProjectLibraryKind
    {
        SqliteDb = 0,
        LegacyFolder = 1
    }

    /// <summary>目录/最近列表中的一条工程库摘要（HostAdapters 层，无 UI）。</summary>
    public sealed class ProjectLibraryEntry
    {
        public string Path { get; set; }
        public string DbPath { get; set; }
        public string WorkDirectory { get; set; }
        public string DisplayName { get; set; }
        public string LineName { get; set; }
        public string Organization { get; set; }
        public string OperatorName { get; set; }
        public int AcquisitionCount { get; set; }
        public DateTime? LastCapturedUtc { get; set; }
        public DateTime FileUtc { get; set; }
        public ProjectLibraryKind Kind { get; set; }
        public bool SummaryLoaded { get; set; }
        public string SummaryError { get; set; }
        public bool IsCurrent { get; set; }

        public ProjectLibraryEntry()
        {
            Path = string.Empty;
            DbPath = string.Empty;
            WorkDirectory = string.Empty;
            DisplayName = string.Empty;
            LineName = string.Empty;
            Organization = string.Empty;
            OperatorName = string.Empty;
            SummaryError = string.Empty;
        }

        public override string ToString()
        {
            string mark = IsCurrent ? "* " : "  ";
            string acq = SummaryLoaded ? ("acq=" + AcquisitionCount) : (string.IsNullOrEmpty(SummaryError) ? "…" : "err");
            string line = string.IsNullOrWhiteSpace(LineName) ? DisplayName : LineName;
            string when = LastCapturedUtc.HasValue
                ? LastCapturedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : FileUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return mark + line + "  [" + acq + "]  " + when + "  " + ShortPath(Path);
        }

        static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Length <= 64) return path;
            return "…" + path.Substring(path.Length - 60);
        }
    }

    /// <summary>当前库内测量/采集历史一行摘要。</summary>
    public sealed class AcquisitionHistorySummary
    {
        public Guid Id { get; set; }
        public string Phase { get; set; }
        public double TargetVoltageKv { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string ExitReason { get; set; }
        public int WaveformFileCount { get; set; }

        public AcquisitionHistorySummary()
        {
            Phase = string.Empty;
            ExitReason = string.Empty;
        }

        public string FormatLine()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:u}  {1}  {2:F2}kV  exit={3}  wf={4}  id={5}",
                CapturedUtc.ToLocalTime(),
                Phase,
                TargetVoltageKv,
                ExitReason,
                WaveformFileCount,
                Id.ToString("N").Substring(0, 8));
        }
    }

    /// <summary>
    /// 工程库发现与最近列表（SQLite <c>project.owts.db</c> / <c>*.owts.db</c> / <c>*.owts</c>，或含 ITEM.in+CABLE.in 的目录）。
    /// 供 DesktopUi「现场」页与 Smoke 共用；不碰 WinForms。
    /// </summary>
    public static class ProjectLibraryCatalog
    {
        public const string DefaultDbFileName = "project.owts.db";
        public const string RecentFileName = "recent-project-libraries.txt";
        public const string LibrariesRootEnvVar = "OWTS_PROJECT_LIBRARIES_ROOT";

        public static string DefaultRecentFilePath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
                root = Path.GetTempPath();
            return Path.Combine(root, "Xiongxin.Owts", RecentFileName);
        }

        public static string ResolveLibrariesRoot(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured.Trim());
            string env = Environment.GetEnvironmentVariable(LibrariesRootEnvVar);
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                return Path.GetFullPath(env.Trim());
            return string.Empty;
        }

        /// <summary>是否为约定工程库路径（文件或可导入遗留目录）。</summary>
        public static bool IsProjectLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full = Path.GetFullPath(path.Trim());
            if (File.Exists(full))
                return IsSqliteLibraryFileName(full);
            if (Directory.Exists(full))
            {
                if (File.Exists(Path.Combine(full, DefaultDbFileName))) return true;
                return File.Exists(Path.Combine(full, "ITEM.in")) && File.Exists(Path.Combine(full, "CABLE.in"));
            }
            return false;
        }

        public static bool IsSqliteLibraryFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            if (string.Equals(name, DefaultDbFileName, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".owts.db", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".owts", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".owts.db", StringComparison.OrdinalIgnoreCase)) return true;
            // 兼容既有 *.db 工程库（DesktopHost/Smoke 临时库）
            if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                && name.IndexOf("owts", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static ProjectLibraryEntry Describe(string path, bool loadSummary = true)
        {
            var entry = new ProjectLibraryEntry();
            if (string.IsNullOrWhiteSpace(path))
            {
                entry.SummaryError = "路径为空";
                return entry;
            }
            string full = Path.GetFullPath(path.Trim());
            entry.Path = full;

            if (File.Exists(full) && IsSqliteLibraryFileName(full))
            {
                entry.Kind = ProjectLibraryKind.SqliteDb;
                entry.DbPath = full;
                entry.WorkDirectory = Path.GetDirectoryName(full) ?? full;
                entry.DisplayName = Path.GetFileName(entry.WorkDirectory);
                entry.FileUtc = File.GetLastWriteTimeUtc(full);
                if (loadSummary) FillSqliteSummary(entry);
                return entry;
            }

            if (Directory.Exists(full))
            {
                string dbBeside = Path.Combine(full, DefaultDbFileName);
                if (File.Exists(dbBeside))
                {
                    entry.Kind = ProjectLibraryKind.SqliteDb;
                    entry.Path = dbBeside;
                    entry.DbPath = dbBeside;
                    entry.WorkDirectory = full;
                    entry.DisplayName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    entry.FileUtc = File.GetLastWriteTimeUtc(dbBeside);
                    if (loadSummary) FillSqliteSummary(entry);
                    return entry;
                }

                string item = Path.Combine(full, "ITEM.in");
                string cable = Path.Combine(full, "CABLE.in");
                if (File.Exists(item) && File.Exists(cable))
                {
                    entry.Kind = ProjectLibraryKind.LegacyFolder;
                    entry.DbPath = dbBeside; // 切换时将创建
                    entry.WorkDirectory = full;
                    entry.DisplayName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    entry.FileUtc = File.GetLastWriteTimeUtc(item);
                    entry.SummaryLoaded = true;
                    entry.LineName = entry.DisplayName + " (遗留)";
                    return entry;
                }
            }

            entry.SummaryError = "无法识别的工程库路径";
            return entry;
        }

        static void FillSqliteSummary(ProjectLibraryEntry entry)
        {
            try
            {
                using (var repo = new SqliteProjectRepository())
                {
                    repo.OpenOrCreate(entry.DbPath);
                    ProjectDraftDto draft = repo.LoadProject() ?? new ProjectDraftDto();
                    entry.LineName = draft.LineName ?? string.Empty;
                    entry.Organization = draft.Organization ?? string.Empty;
                    entry.OperatorName = draft.OperatorName ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(entry.DisplayName))
                        entry.DisplayName = string.IsNullOrWhiteSpace(entry.LineName)
                            ? Path.GetFileName(entry.DbPath)
                            : entry.LineName;
                    IReadOnlyList<AcquisitionRecordDto> acqs = repo.ListAcquisitions(null);
                    entry.AcquisitionCount = acqs != null ? acqs.Count : 0;
                    if (acqs != null && acqs.Count > 0 && acqs[0].CapturedUtc != DateTime.MinValue)
                        entry.LastCapturedUtc = acqs[0].CapturedUtc;
                    entry.SummaryLoaded = true;
                }
            }
            catch (Exception ex)
            {
                entry.SummaryError = ex.Message;
                entry.SummaryLoaded = false;
            }
        }

        /// <summary>扫描根目录下工程库（默认深度 2：根内文件 + 一级子目录）。</summary>
        public static IReadOnlyList<ProjectLibraryEntry> ScanRoot(string root, int maxDepth = 2, bool loadSummaries = true)
        {
            var list = new List<ProjectLibraryEntry>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return list;
            string fullRoot = Path.GetFullPath(root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanRecursive(fullRoot, 0, Math.Max(0, maxDepth), loadSummaries, list, seen);
            list.Sort((a, b) =>
            {
                DateTime ta = a.LastCapturedUtc ?? a.FileUtc;
                DateTime tb = b.LastCapturedUtc ?? b.FileUtc;
                int cmp = tb.CompareTo(ta);
                return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        static void ScanRecursive(string dir, int depth, int maxDepth, bool loadSummaries, List<ProjectLibraryEntry> list, HashSet<string> seen)
        {
            if (depth > maxDepth || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (!IsSqliteLibraryFileName(file)) continue;
                    string key = Path.GetFullPath(file);
                    if (!seen.Add(key)) continue;
                    list.Add(Describe(key, loadSummaries));
                }

                // 遗留目录：本层若有 ITEM+CABLE 且尚无 project.owts.db
                string item = Path.Combine(dir, "ITEM.in");
                string cable = Path.Combine(dir, "CABLE.in");
                string dbBeside = Path.Combine(dir, DefaultDbFileName);
                if (depth > 0 && File.Exists(item) && File.Exists(cable) && !File.Exists(dbBeside))
                {
                    string key = Path.GetFullPath(dir);
                    if (seen.Add(key))
                        list.Add(Describe(key, loadSummary: false));
                }

                if (depth >= maxDepth) return;
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase))
                        continue;
                    ScanRecursive(sub, depth + 1, maxDepth, loadSummaries, list, seen);
                }
            }
            catch (UnauthorizedAccessException) { /* skip */ }
            catch (IOException) { /* skip */ }
        }

        public static IReadOnlyList<string> LoadRecentPaths(string recentFilePath)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(recentFilePath) || !File.Exists(recentFilePath))
                return list;
            try
            {
                foreach (string line in File.ReadAllLines(recentFilePath, Encoding.UTF8))
                {
                    string t = (line ?? string.Empty).Trim();
                    if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    try { list.Add(Path.GetFullPath(t)); }
                    catch { /* skip bad line */ }
                }
            }
            catch { /* ignore */ }
            return list;
        }

        public static void Remember(string recentFilePath, string libraryPath, int maxEntries = 24)
        {
            if (string.IsNullOrWhiteSpace(recentFilePath) || string.IsNullOrWhiteSpace(libraryPath)) return;
            string full = Path.GetFullPath(libraryPath.Trim());
            var existing = new List<string>();
            foreach (string p in LoadRecentPaths(recentFilePath))
            {
                if (!string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
                    existing.Add(p);
            }
            existing.Insert(0, full);
            if (existing.Count > maxEntries)
                existing.RemoveRange(maxEntries, existing.Count - maxEntries);
            string dir = Path.GetDirectoryName(recentFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("# Xiongxin.Owts recent project libraries");
            foreach (string p in existing) sb.AppendLine(p);
            File.WriteAllText(recentFilePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>合并最近 + 目录扫描；当前库标 IsCurrent；去重（DbPath/Path）。</summary>
        public static IReadOnlyList<ProjectLibraryEntry> MergeRecentAndScan(
            string recentFilePath,
            string scanRoot,
            string currentDbPath = null,
            bool loadSummaries = true)
        {
            var byKey = new Dictionary<string, ProjectLibraryEntry>(StringComparer.OrdinalIgnoreCase);
            void Add(ProjectLibraryEntry e)
            {
                if (e == null || string.IsNullOrEmpty(e.Path)) return;
                string key = !string.IsNullOrEmpty(e.DbPath) ? e.DbPath : e.Path;
                if (byKey.ContainsKey(key)) return;
                byKey[key] = e;
            }

            foreach (string p in LoadRecentPaths(recentFilePath))
            {
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                if (!IsProjectLibraryPath(p) && !(File.Exists(p) && IsSqliteLibraryFileName(p))) continue;
                Add(Describe(p, loadSummaries));
            }

            string root = ResolveLibrariesRoot(scanRoot);
            if (!string.IsNullOrEmpty(root))
            {
                foreach (var e in ScanRoot(root, maxDepth: 2, loadSummaries: loadSummaries))
                    Add(e);
            }

            if (!string.IsNullOrWhiteSpace(currentDbPath))
            {
                string cur = Path.GetFullPath(currentDbPath.Trim());
                if (File.Exists(cur) || Directory.Exists(cur))
                    Add(Describe(cur, loadSummaries));
                foreach (var e in byKey.Values)
                {
                    string key = !string.IsNullOrEmpty(e.DbPath) ? e.DbPath : e.Path;
                    e.IsCurrent = string.Equals(key, cur, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.Path, cur, StringComparison.OrdinalIgnoreCase);
                }
            }

            var list = byKey.Values.ToList();
            list.Sort((a, b) =>
            {
                if (a.IsCurrent != b.IsCurrent) return a.IsCurrent ? -1 : 1;
                DateTime ta = a.LastCapturedUtc ?? a.FileUtc;
                DateTime tb = b.LastCapturedUtc ?? b.FileUtc;
                int cmp = tb.CompareTo(ta);
                return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        public static IReadOnlyList<AcquisitionHistorySummary> ListAcquisitionSummaries(IProjectRepository repository, int take = 200)
        {
            var result = new List<AcquisitionHistorySummary>();
            if (repository == null) return result;
            IReadOnlyList<AcquisitionRecordDto> list = repository.ListAcquisitions(null);
            if (list == null) return result;
            int n = 0;
            foreach (var a in list)
            {
                if (n++ >= take) break;
                result.Add(new AcquisitionHistorySummary
                {
                    Id = a.Id,
                    Phase = a.Phase ?? string.Empty,
                    TargetVoltageKv = a.TargetVoltageKv,
                    CapturedUtc = a.CapturedUtc,
                    ExitReason = a.ExitReason ?? string.Empty,
                    WaveformFileCount = a.WaveformRelPaths != null ? a.WaveformRelPaths.Length : 0
                });
            }
            return result;
        }

        public static IReadOnlyList<string> ListReportFiles(string workDirectory)
        {
            var files = new List<string>();
            if (string.IsNullOrWhiteSpace(workDirectory)) return files;
            string reportsDir = Path.Combine(workDirectory, "reports");
            if (!Directory.Exists(reportsDir)) return files;
            try
            {
                foreach (string f in Directory.GetFiles(reportsDir, "*.html")) files.Add(f);
                foreach (string f in Directory.GetFiles(reportsDir, "*.md")) files.Add(f);
                files.Sort(StringComparer.OrdinalIgnoreCase);
                files.Reverse();
            }
            catch { /* ignore */ }
            return files;
        }
    }
}
