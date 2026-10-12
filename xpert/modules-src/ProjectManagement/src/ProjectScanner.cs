using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.Controls
{
    /// <summary>
    /// OWTS project discovery aligned with task 19: a project directory must contain readable
    /// top-level ITEM.in and CABLE.in files, and nested project roots are not traversed again.
    /// </summary>
    public static class ProjectScanner
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex NumberPattern = new Regex(
            "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][-+]?\\d+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly string[] DateTimeFormats =
        {
            "yyyy年M月d日H时m分", "yyyy年M月d日 H时m分", "yyyy年M月d日H时m分s秒", "yyyy年M月d日 H时m分s秒",
            "yyyy年M月d日-H时m分", "yyyy-M-d-H时m分", "yyyy-M-d H:m", "yyyy-M-d H:m:s",
            "yyyy/M/d H:m", "yyyy/M/d H:m:s", @"yyyy-MM-dd-HH\'mm\'ss.ff", @"yyyy-MM-dd-HH\'mm\'ss",
            "yyyy-MM-dd_HH-mm-ss", "yyyy-M-d", "yyyy/M/d", "yyyy年M月d日"
        };

        public static ProjectScanResult Scan(string rootDirectory)
        {
            ProjectScanResult output = new ProjectScanResult();
            List<string> warnings = new List<string>();
            try
            {
                string root = NormalizeExistingDirectory(rootDirectory);
                output.RootDirectory = root;
                List<ProjectRecord> projects = new List<ProjectRecord>();
                foreach (string path in FindProjectDirectories(root, warnings))
                {
                    try { projects.Add(ReadProjectCore(path)); }
                    catch (Exception ex) { warnings.Add(path + "：" + OneLine(ex.Message)); }
                }
                output.Projects = projects
                    .OrderByDescending(p => p.HasTestTime)
                    .ThenByDescending(p => p.TestTime)
                    .ThenBy(p => p.ProjectName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(p => p.ProjectPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                output.Warnings = warnings.ToArray();
                output.Ok = true;
            }
            catch (Exception ex)
            {
                output.Error = OneLine(ex.Message);
                output.Warnings = warnings.ToArray();
            }
            return output;
        }

        public static bool IsProjectDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                if (IsUncommittedProjectDirectory(directory)) return false;
                string itemPath = Path.Combine(directory, "ITEM.in");
                string cablePath = Path.Combine(directory, "CABLE.in");
                if (!File.Exists(itemPath) || !File.Exists(cablePath)) return false;
                if (ReadKeyValueFile(itemPath).Count == 0) return false;
                foreach (string raw in ReadTextSmart(cablePath).Replace("\r", string.Empty).Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string[] parts = raw.Split('\t');
                    if (parts.Length >= 4 && (parts[0].Trim() == "0" || parts[0].Trim() == "1")) return true;
                }
            }
            catch { }
            return false;
        }

        // A interrupted save can leave both metadata files in its staging folder.
        // Never advertise that uncommitted directory as a user project, or descend
        // into it. Keep it on disk for recovery; discovery must never delete data.
        static bool IsUncommittedProjectDirectory(string directory)
        {
            string name = new DirectoryInfo(directory).Name;
            return name.StartsWith(".project-creating-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".project-exporting-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".project-deleted-", StringComparison.OrdinalIgnoreCase);
        }

        public static ProjectRecord ReadProject(string projectDirectory)
        {
            string project = NormalizeExistingDirectory(projectDirectory);
            if (!IsProjectDirectory(project))
                throw new InvalidDataException("所选目录不是有效项目；顶层必须包含可读的 ITEM.in 与 CABLE.in。");

            return ReadProjectCore(project);
        }

        // The scanner has already validated each discovered project. Avoid reading
        // ITEM.in and CABLE.in a third time when building its catalogue.
        static ProjectRecord ReadProjectCore(string project)
        {
            ProjectRecord record = new ProjectRecord();
            record.ProjectPath = project;
            record.ItemPath = Path.Combine(project, "ITEM.in");
            record.CablePath = Path.Combine(project, "CABLE.in");
            Dictionary<string, string> item = ReadKeyValueFile(record.ItemPath);
            record.StartTerminal = First(item, "StartName", "始端地点", "始端", "起点地点");
            record.EndTerminal = First(item, "EndName", "终端地点", "终端", "终点地点");
            FillEndpointFallback(project, record);
            record.ProjectName = BuildProjectName(record, project);
            record.RawDate = First(item, "Date", "日期", "测试日期");
            record.RawTime = First(item, "Time", "时间", "测试时间");
            ResolveProjectTime(record, project);
            record.CableLengthMetres = NumberFrom(item, "CableLength", "电缆长度");
            record.CableLengthSource = "ITEM.in";
            if (!Finite(record.CableLengthMetres))
            {
                record.CableLengthMetres = CableLengthFromStructure(record.CablePath);
                record.CableLengthSource = Finite(record.CableLengthMetres) ? "CABLE.in" : string.Empty;
            }
            foreach (string path in SafeFilesRecursive(project))
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                string fileName = Path.GetFileName(path);
                if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) continue;
                if (extension != ".doc" && extension != ".docx" && extension != ".pdf") continue;
                FileInfo info;
                try { info = new FileInfo(path); }
                catch { continue; }
                record.MutableReports.Add(new ProjectReportFile
                {
                    FullPath = info.FullName,
                    RelativePath = RelativePath(project, info.FullName),
                    Extension = extension,
                    LastWriteTime = SafeLastWriteTime(path),
                    Length = SafeLength(info)
                });
            }
            record.MutableReports.Sort(delegate(ProjectReportFile left, ProjectReportFile right)
            {
                int compared = right.LastWriteTime.CompareTo(left.LastWriteTime);
                return compared != 0 ? compared : StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
            });
            record.CompleteReports();
            record.InsulationStatus = ProjectInsulationStatusReader.Read(project);
            return record;
        }

        static IEnumerable<string> FindProjectDirectories(string root, List<string> warnings)
        {
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    if (IsUncommittedProjectDirectory(current)) continue;
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add("已跳过重解析目录：" + current);
                        continue;
                    }
                    if (IsProjectDirectory(current))
                    {
                        unique.Add(Path.GetFullPath(current));
                        continue;
                    }
                    string[] children;
                    try { children = Directory.GetDirectories(current); }
                    catch (Exception ex)
                    {
                        warnings.Add("无法枚举目录 " + current + "：" + OneLine(ex.Message));
                        continue;
                    }
                    Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                    for (int i = children.Length - 1; i >= 0; i--) pending.Push(children[i]);
                }
                catch (Exception ex) { warnings.Add("已跳过目录 " + current + "：" + OneLine(ex.Message)); }
            }
            return unique;
        }

        static void ResolveProjectTime(ProjectRecord record, string project)
        {
            string combined = JoinDateTime(record.RawDate, record.RawTime);
            DateTime parsed;
            if (TryParseTime(combined, out parsed))
            {
                record.TestTime = parsed;
                record.HasTestTime = true;
                record.TestTimeSource = "ITEM.in";
            }
            else
            {
                string folder = new DirectoryInfo(project).Name;
                if (TryParseTime(folder, out parsed))
                {
                    record.TestTime = parsed;
                    record.HasTestTime = true;
                    record.TestTimeSource = "项目目录名";
                }
            }
            record.DisplayTestTime = record.HasTestTime
                ? record.TestTime.ToString("yyyy-MM-dd HH:mm:ss", Inv)
                : (combined ?? string.Empty).Trim();
        }

        public static bool TryParseTime(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            // Parse complete ISO values first: whole seconds and 1..7 fractional
            // digits must retain their seconds, offset and round-trip kind.
            if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-](?:0\d|1[0-3]):[0-5]\d|[+-]14:00)?$", RegexOptions.CultureInvariant))
                return DateTime.TryParse(text, Inv, DateTimeStyles.RoundtripKind, out result);
            // Only strip a collision suffix from a known automatically generated directory name.
            // Arbitrary project names are neither rewritten nor treated as timestamps here.
            Match automatic = Regex.Match(text, @"^(?<stamp>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}|\d{4}年\d{1,2}月\d{1,2}日\d{1,2}时\d{1,2}分(?:\d{1,2}秒)?|\d{4}-\d{2}-\d{2}-\d{2}'\d{2}'\d{2}(?:\.\d{2})?)(?:-\d+)?$", RegexOptions.CultureInvariant);
            if (automatic.Success) text = automatic.Groups["stamp"].Value;
            text = Regex.Replace(text, "(?<=日)[-_ ]+", " ");
            // Exact formats consume the complete value; do not invent a timestamp
            // by extracting a date substring from a manually named project.
            return DateTime.TryParseExact(text, DateTimeFormats, Inv,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out result);
        }

        static string BuildProjectName(ProjectRecord record, string project)
        {
            if (!string.IsNullOrWhiteSpace(record.StartTerminal) && !string.IsNullOrWhiteSpace(record.EndTerminal))
                return record.StartTerminal.Trim() + " – " + record.EndTerminal.Trim();
            if (!string.IsNullOrWhiteSpace(record.StartTerminal)) return record.StartTerminal.Trim();
            if (!string.IsNullOrWhiteSpace(record.EndTerminal)) return record.EndTerminal.Trim();
            try
            {
                DirectoryInfo parent = Directory.GetParent(project);
                if (parent != null && !string.IsNullOrWhiteSpace(parent.Name)) return parent.Name;
            }
            catch { }
            return new DirectoryInfo(project).Name;
        }

        static void FillEndpointFallback(string project, ProjectRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.StartTerminal) && !string.IsNullOrWhiteSpace(record.EndTerminal)) return;
            DirectoryInfo parent;
            try { parent = Directory.GetParent(project); }
            catch { return; }
            if (parent == null) return;
            string name = parent.Name;
            int separator = name.IndexOf('-');
            if (separator <= 0 || separator >= name.Length - 1) return;
            if (string.IsNullOrWhiteSpace(record.StartTerminal)) record.StartTerminal = name.Substring(0, separator).Trim();
            if (string.IsNullOrWhiteSpace(record.EndTerminal)) record.EndTerminal = name.Substring(separator + 1).Trim();
        }

        static double CableLengthFromStructure(string path)
        {
            if (!File.Exists(path)) return double.NaN;
            double maximum = double.NaN;
            foreach (string raw in ReadTextSmart(path).Replace("\r", string.Empty).Split('\n'))
            {
                string[] parts = raw.Split('\t');
                if (parts.Length < 4 || parts[0].Trim() != "1" ||
                    parts[1].IndexOf("终端", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Match match = NumberPattern.Match(parts[3]);
                double position;
                if (match.Success && double.TryParse(match.Value, NumberStyles.Float, Inv, out position) && Finite(position))
                    maximum = Finite(maximum) ? Math.Max(maximum, position) : position;
            }
            return maximum;
        }

        public static Dictionary<string, string> ReadKeyValueFile(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return values;
            string text = ReadTextSmart(path);
            foreach (string raw in text.Replace("\r", string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int separator = line.IndexOf('\t');
                if (separator < 0) separator = line.IndexOf('=');
                if (separator < 0) separator = line.IndexOf('：');
                if (separator < 0) separator = line.IndexOf(':');
                if (separator <= 0) continue;
                string key = line.Substring(0, separator).Trim().Trim('\uFEFF').TrimEnd(':', '：').Trim();
                string value = line.Substring(separator + 1).Trim().Trim('"').Trim();
                if (key.Length > 0) values[key] = value;
            }
            return values;
        }

        public static string ReadTextSmart(string path)
        {
            if(new FileInfo(path).Length>2*1024*1024)throw new InvalidDataException("Project metadata exceeds the 2 MiB text budget.");
            byte[] bytes = File.ReadAllBytes(path);
            if(bytes.Length>2*1024*1024)throw new InvalidDataException("Project metadata exceeds the 2 MiB text budget.");
            string text;
            if(bytes.Length>=2&&bytes[0]==0xFF&&bytes[1]==0xFE)
                text=new UnicodeEncoding(false,true,true).GetString(bytes,2,bytes.Length-2);
            else if(bytes.Length>=2&&bytes[0]==0xFE&&bytes[1]==0xFF)
                text=new UnicodeEncoding(true,true,true).GetString(bytes,2,bytes.Length-2);
            else {
                try { text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF'); }
                catch(DecoderFallbackException) { text=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes); }
            }
            if(text.IndexOf('\0')>=0)throw new InvalidDataException("Project metadata contains invalid NUL characters.");
            return text;
        }

        static string First(Dictionary<string, string> values, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value;
                if (values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
        }

        static double NumberFrom(Dictionary<string, string> values, params string[] keys)
        {
            string raw = First(values, keys);
            Match match = NumberPattern.Match(raw ?? string.Empty);
            double value;
            return match.Success && double.TryParse(match.Value, NumberStyles.Float, Inv, out value) ? value : double.NaN;
        }

        static string JoinDateTime(string date, string time)
        {
            if (string.IsNullOrWhiteSpace(date)) return time ?? string.Empty;
            if (string.IsNullOrWhiteSpace(time)) return date ?? string.Empty;
            return date.Trim() + " " + time.Trim();
        }

        static IEnumerable<string> SafeFilesRecursive(string root)
        {
            List<string> files = new List<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                try { files.AddRange(Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)); }
                catch { }
                string[] children;
                try { children = Directory.GetDirectories(directory); }
                catch { continue; }
                foreach (string child in children)
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    }
                    catch { }
                }
            }
            return files;
        }

        public static string NormalizeExistingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("项目数据根目录不能为空。", "path");
            string full = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("目录不存在：" + full);
            return NormalizeDirectoryPath(full);
        }

        // Preserve volume roots: D:\ is absolute; D: is drive-relative.
        internal static string NormalizeDirectoryPath(string path)
        {
            string full = Path.GetFullPath(path);
            string volume = Path.GetPathRoot(full);
            return full.Length <= volume.Length ? volume : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static string RelativePath(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : Path.GetFileName(path);
        }

        static long SafeLength(FileInfo info) { try { return info.Length; } catch { return 0; } }
        static DateTime SafeLastWriteTime(string path) { try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; } }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static string OneLine(string text) { return (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim(); }
    }
}
