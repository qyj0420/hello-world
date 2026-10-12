using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.Controls
{
    public sealed class CableJoint
    {
        public double PositionMetres { get; set; }
        public string AttachmentType { get; set; }
        public string BodyType { get; set; }
        public bool IsEndpoint { get; set; }
    }

    public sealed class PersistenceResult
    {
        public string BackupDirectory { get; internal set; }
        public int ChangedFileCount { get; internal set; }
        public string[] Warnings { get; internal set; }
    }

    /// <summary>Transactional edits of supported, top-level OWTS project metadata. Waveform bytes are copied unchanged.</summary>
    public static class CalibrationPersistence
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly Encoding Gbk = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        static readonly string[] Supported = { ".cal", ".dem", ".loc" };
        internal static Action<int> BeforeReplaceForTests;

        public static PersistenceResult SaveCalibration(string projectPath, string calibrationPath,
            double incidentSeconds, double reflectionSeconds, double cableLengthMetres)
        {
            Positive(cableLengthMetres, "电缆长度");
            if (!Finite(incidentSeconds) || !Finite(reflectionSeconds) || incidentSeconds < 0 || reflectionSeconds <= incidentSeconds)
                throw new ArgumentException("反射时间必须大于入射时间，且入射时间不得小于零。");
            double velocity = 2 * cableLengthMetres / ((reflectionSeconds - incidentSeconds) * 1000000.0);
            Positive(velocity, "计算波速");
            return Edit(projectPath, delegate(Project p)
            {
                Document chosen = p.Calibration(calibrationPath);
                ValidateCalibrationRange(chosen, incidentSeconds, reflectionSeconds);
                chosen.Set("Start_point", Number(incidentSeconds));
                chosen.Set("Final_point", Number(reflectionSeconds));
                foreach (Document d in p.Files)
                {
                    if (d.Kind == ".cal" || d.Kind == ".loc") d.SetVelocity(velocity);
                    if (d.Kind == ".dem")
                    {
                        d.SetOptionalVelocity(velocity);
                        string link = d.Value("Cal.File-path");
                        d.Set("Cal.File-path", string.IsNullOrWhiteSpace(link) ? chosen.Path : p.LocalCalibrationLink(link));
                    }
                }
                p.Item.ChangeItem(null, delegate(string[] row) { if (!string.IsNullOrWhiteSpace(row[8]) && row[8] != "NaN") row[8] = EscapeLike(row[8], p.LocalCalibrationLink(row[8])); return velocity; });
                p.Warnings.Add("项目所有已识别波速字段已统一更新；仅当前CAL保存新的入射/反射标记，其他CAL原始时间标记保持不变。");
                p.Warnings.Add("旧版DEM没有波速字段，通过Cal.File-path引用校准文件；原始测试及定位波形保持逐字节不变。");
            });
        }

        public static PersistenceResult CorrectCableLength(string projectPath, double newLengthMetres, string referenceCalibrationPath = null)
        {
            Positive(newLengthMetres, "新电缆长度");
            return Edit(projectPath, delegate(Project p)
            {
                List<CableBlock> blocks = ParseCable(p.Cable);
                foreach (CableBlock b in blocks)
                    if (b.Joints.Any(j => !j.IsEndpoint && j.PositionMetres >= newLengthMetres))
                        throw new ArgumentException("新长度不得小于或等于现有内部接头位置；请先编辑接头位置。");
                Document reference = string.IsNullOrWhiteSpace(referenceCalibrationPath) ? null : p.Calibration(referenceCalibrationPath);
                List<Calibration> cals = p.Files.Where(d => d.Kind == ".cal").Select(d => new Calibration(d, newLengthMetres)).ToList();
                if (cals.Count == 0) throw new InvalidDataException("没有有效CAL文件，无法按原入射/反射时间差重新计算波速。");
                foreach (Document d in p.Files)
                {
                    if (d.Kind == ".cal")
                    {
                        Calibration c = cals.First(x => x.Doc == d); d.SetLength(newLengthMetres); d.SetVelocity(c.NewVelocity);
                    }
                    else if (d.Kind == ".loc" || d.Kind == ".dem")
                    {
                        string link = d.Kind == ".dem" ? d.Value("Cal.File-path") : p.PairedCalibration(d);
                        Calibration c = Resolve(cals, link, Phase(d.Path), d.OptionalVelocity(), reference);
                        d.SetOptionalLength(newLengthMetres); d.SetOptionalVelocity(c.NewVelocity);
                        if (d.Kind == ".dem") d.Set("Cal.File-path", c.Doc.Path);
                    }
                }
                p.Item.ChangeItem(newLengthMetres, delegate(string[] row)
                {
                                        if (row[0] == null)
                    {
                        if (reference != null) return cals.First(c => c.Doc == reference).NewVelocity;
                        if (cals.All(c => Math.Abs(c.NewVelocity - cals[0].NewVelocity) < 0.000001)) return cals[0].NewVelocity;
                        throw new InvalidDataException("ITEM含全局波速字段但存在多个不同波速校准，请指定参考校准。");
                    }
                    Calibration resolved = Resolve(cals, row[8], NormalizePhase(row[0]), Parse(row[3]), reference);
                    row[8] = EscapeLike(row[8], resolved.Doc.Path); return resolved.NewVelocity;
                });
                foreach (CableBlock b in blocks) b.SetEnd(newLengthMetres);
                p.Cable.ReplaceText(string.Concat(blocks.Select(b => b.Render())));
                p.Warnings.Add("每个CAL按原时间差计算新波速；LOC使用同名DEM引用、相别及旧波速匹配校准。内部接头物理距离保持不变。");
                p.Warnings.Add("旧版DEM没有长度/波速字段，保存其CAL引用，不增加头行或改动采样数据。");
            });
        }

        public static CableJoint[] ReadJoints(string projectPath)
        {
            string root = Root(projectPath);
            Document d = Document.Read(FindRequired(root, "CABLE.in"), "cable");
            List<CableBlock> blocks = ParseCable(d);
            RequireSharedGeometry(blocks);
            return blocks[0].Joints.Select(Clone).ToArray();
        }

        public static PersistenceResult SaveJoints(string projectPath, CableJoint[] joints)
        {
            if (joints == null || joints.Length < 2) throw new ArgumentException("接头列表必须包含始端和终端。");
            CableJoint[] copy = joints.Select(Clone).ToArray();
            return Edit(projectPath, delegate(Project p)
            {
                List<CableBlock> blocks = ParseCable(p.Cable); RequireSharedGeometry(blocks);
                CableJoint[] prior = blocks[0].Joints.ToArray();
                if (copy[0].PositionMetres != 0 || copy[copy.Length - 1].PositionMetres != prior[prior.Length - 1].PositionMetres)
                    throw new ArgumentException("始端0米和终端位置受到保护；修改长度请使用电缆长度修正。");
                for (int i = 0; i < copy.Length; i++)
                {
                    CableJoint j = copy[i];
                    if (!Finite(j.PositionMetres) || j.PositionMetres < 0 || (i > 0 && j.PositionMetres <= copy[i - 1].PositionMetres))
                        throw new ArgumentException("接头位置必须严格递增、不重复，且位于电缆范围内。");
                    if (!prior.Any(old => old.PositionMetres == j.PositionMetres) && Math.Abs(j.PositionMetres - Math.Round(j.PositionMetres, 1, MidpointRounding.AwayFromZero)) > 0.0000001)
                        throw new ArgumentException("接头位置最多保留1位小数。");
                    if (!prior.Any(old => old.PositionMetres == j.PositionMetres) && copy.Where((other, index) => index != i).Any(other => Math.Round(other.PositionMetres, 1, MidpointRounding.AwayFromZero) == Math.Round(j.PositionMetres, 1, MidpointRounding.AwayFromZero)))
                        throw new ArgumentException("新接头位置与列表现有显示位置重复。");
                    SafeCell(j.AttachmentType); SafeCell(j.BodyType);
                    if (i > 0 && i < copy.Length - 1 && (string.IsNullOrWhiteSpace(j.AttachmentType) || string.IsNullOrWhiteSpace(j.BodyType)))
                        throw new ArgumentException("内部接头必须指定附件类型和本体类型。");
                }
                foreach (CableBlock b in blocks) b.ReplaceJoints(copy, prior);
                p.Cable.ReplaceText(string.Concat(blocks.Select(b => b.Render())));
                p.Warnings.Add("已统一保存相同几何的电缆段；各段原有始端、终端及其类型保持不变。");
            }, false);
        }

        static CableJoint Clone(CableJoint j)
        {
            if (j == null) throw new ArgumentException("接头条目不得为空。");
            return new CableJoint { PositionMetres = j.PositionMetres, AttachmentType = j.AttachmentType ?? "", BodyType = j.BodyType ?? "", IsEndpoint = j.IsEndpoint };
        }
        static void SafeCell(string s) { if (s != null && s.IndexOfAny(new[] { '\t', '\r', '\n', '\0' }) >= 0) throw new ArgumentException("附件/本体类型不得包含制表符或换行。"); }
        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        static void Positive(double x, string name) { if (!Finite(x) || x <= 0) throw new ArgumentOutOfRangeException(name, name + "必须为正的有限数值。"); }
        static double Parse(string s) { double n; return double.TryParse((s ?? "").Trim().TrimEnd('m', 'M'), NumberStyles.Float, Inv, out n) ? n : double.NaN; }
        static string Number(double x) { return x.ToString("G15", Inv); }
        static string NormalizePhase(string s) { return (s ?? "").Trim().TrimEnd('_').Replace("相", "").ToUpperInvariant(); }
        static string Phase(string path) { return NormalizePhase(System.IO.Path.GetFileName(path).Split('_')[0]); }
        static string EscapeLike(string old, string path) { return (old ?? "").Contains("\\\\") ? path.Replace("\\", "\\\\") : path; }
        static string Basename(string s) { return (s ?? "").Trim().Trim('"').Replace("\\\\", "\\").Replace('/', '\\').Split('\\').Last(); }

        sealed class Calibration
        {
            internal Document Doc; internal double OldVelocity, NewVelocity; internal string Phase;
            internal Calibration(Document d, double length)
            {
                Doc = d; OldVelocity = d.OptionalVelocity(); Phase = CalibrationPersistence.Phase(d.Path);
                ValidateCalibrationRange(d, d.Number("Start_point"), d.Number("Final_point"));
                double delta = d.Number("Final_point") - d.Number("Start_point"); Positive(delta, "CAL入射/反射时间差：" + System.IO.Path.GetFileName(d.Path));
                NewVelocity = 2 * length / (delta * 1000000.0); Positive(NewVelocity, "新波速");
            }
        }
        static Calibration Resolve(List<Calibration> cals, string link, string phase, double oldVelocity, Document reference)
        {
            string name = Basename(link);
            if (!string.IsNullOrWhiteSpace(name))
            {
                List<Calibration> linked = cals.Where(c => string.Equals(System.IO.Path.GetFileName(c.Doc.Path), name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (linked.Count == 1) return linked[0];
                throw new InvalidDataException("校准引用未能对应项目根目录内的CAL：" + name);
            }
            List<Calibration> eligible = cals.Where(c => c.Phase == phase).ToList();
            if (eligible.Count == 0) eligible = cals.Where(c => c.Phase == "ALL").ToList();
            if (eligible.Count == 0) throw new InvalidDataException("相别 " + phase + " 没有对应或ALL校准。");
            if (reference != null)
            {
                Calibration explicitCal = eligible.FirstOrDefault(c => c.Doc == reference);
                if (explicitCal != null) return explicitCal;
            }
            if (Finite(oldVelocity))
            {
                List<Calibration> matching = eligible.Where(c => Math.Abs(c.OldVelocity - oldVelocity) <= 0.011).ToList();
                if (matching.Count > 0) eligible = matching;
            }
            if (eligible.Count == 1 || eligible.All(c => Math.Abs(c.NewVelocity - eligible[0].NewVelocity) < 0.000001)) return eligible[0];
            throw new InvalidDataException("相别 " + phase + " 对应多个不同波速CAL，无法唯一匹配；请指定参考校准文件后重试。");
        }

        static PersistenceResult Edit(string projectPath, Action<Project> edit, bool includeData = true)
        {
            string root = Root(projectPath);
            string lockPath = System.IO.Path.Combine(root, ".DataBrowser.write.lock"); RejectReparse(lockPath);
            using (FileStream gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Project project = new Project(root, includeData); edit(project);
                return Commit(project);
            }
        }

        sealed class Project
        {
            internal string RootPath; internal Document Item, Cable; internal List<Document> Files = new List<Document>();
            internal List<string> Warnings = new List<string>(); internal string[] Inventory; internal bool IncludeData;
            internal Project(string root, bool includeData)
            {
                RootPath = root; IncludeData = includeData; Inventory = InventoryOf(root, includeData);
                Item = Document.Read(FindRequired(root, "ITEM.in"), "item");
                Cable = Document.Read(FindRequired(root, "CABLE.in"), "cable"); ParseCable(Cable);
                if (!Item.Text.Contains("[ITEM]")) throw new InvalidDataException("ITEM.in不是支持的项目格式。");
                if (includeData)
                foreach (string path in Inventory.Where(x => Supported.Contains(System.IO.Path.GetExtension(x).ToLowerInvariant())))
                {
                    RejectReparse(path);
                    if (new FileInfo(path).Length == 0) { Warnings.Add("空文件无可更新字段，保持不变：" + System.IO.Path.GetFileName(path)); continue; }
                    Files.Add(Document.Read(path, System.IO.Path.GetExtension(path).ToLowerInvariant()));
                }
            }
            internal Document Calibration(string path)
            {
                string full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path ?? "") ? path : System.IO.Path.Combine(RootPath, path ?? ""));
                if (!string.Equals(System.IO.Path.GetDirectoryName(full), RootPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("校准文件必须位于当前项目根目录。");
                Document d = Files.FirstOrDefault(x => x.Kind == ".cal" && string.Equals(x.Path, full, StringComparison.OrdinalIgnoreCase));
                if (d == null) throw new InvalidDataException("未找到可用CAL：" + System.IO.Path.GetFileName(full)); return d;
            }
                        internal string LocalCalibrationLink(string link)
            {
                string name = Basename(link);
                Document d = Files.FirstOrDefault(x => x.Kind == ".cal" && string.Equals(System.IO.Path.GetFileName(x.Path), name, StringComparison.OrdinalIgnoreCase));
                if (d == null) throw new InvalidDataException("校准引用未能对应项目根目录内的CAL：" + name);
                return d.Path;
            }
            internal string PairedCalibration(Document d)
            {
                Document dem = Files.FirstOrDefault(x => x.Kind == ".dem" && string.Equals(System.IO.Path.GetFileNameWithoutExtension(x.Path), System.IO.Path.GetFileNameWithoutExtension(d.Path), StringComparison.OrdinalIgnoreCase));
                return dem == null ? "" : dem.Value("Cal.File-path");
            }
            internal IEnumerable<Document> All { get { return new[] { Item, Cable }.Concat(Files); } }
        }

        static string Root(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("项目路径为空。");
            string root = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            for (DirectoryInfo d = new DirectoryInfo(root); d != null; d = d.Parent) RejectReparse(d.FullName);
            return root;
        }
        static void RejectReparse(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("为防止越出项目范围，不支持符号链接/目录联接：" + path);
        }
        static string FindRequired(string root, string name)
        {
            string[] paths = Directory.GetFiles(root).Where(p => string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (paths.Length != 1) throw new InvalidDataException("项目必须含唯一的 " + name); RejectReparse(paths[0]); return paths[0];
        }
        static string[] InventoryOf(string root, bool data)
        {
            return Directory.GetFiles(root).Where(p => (data && Supported.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant())) || string.Equals(System.IO.Path.GetFileName(p), "ITEM.in", StringComparison.OrdinalIgnoreCase) || string.Equals(System.IO.Path.GetFileName(p), "CABLE.in", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        static byte[] Hash(string path) { using (SHA256 h = SHA256.Create()) using (FileStream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return h.ComputeHash(s); }
        static bool Same(byte[] a, byte[] b) { return a.SequenceEqual(b); }
        static PersistenceResult Commit(Project p)
        {
            List<Document> changed = p.All.Where(d => !Same(d.OriginalPrefix, d.NewPrefix())).ToList();
            if (changed.Count == 0) return new PersistenceResult { BackupDirectory = "", ChangedFileCount = 0, Warnings = p.Warnings.ToArray() };
            foreach (Document d in changed)
            {
                if ((File.GetAttributes(d.Path) & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("文件为只读，保存前未修改任何项目数据：" + System.IO.Path.GetFileName(d.Path));
                using (new FileStream(d.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { }
                d.CheckUnchanged();
            }
            string backupRoot = System.IO.Path.Combine(p.RootPath, ".DataBrowserBackups"); RejectReparse(backupRoot);
            string backup = System.IO.Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", Inv) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(backup);
            List<Document> committed = new List<Document>();
            try
            {
                foreach (Document d in changed)
                {
                    string name = System.IO.Path.GetFileName(d.Path);
                    d.Backup = System.IO.Path.Combine(backup, name); d.Stage = System.IO.Path.Combine(backup, name + ".pending");
                    File.Copy(d.Path, d.Backup, false);
                    if (!Same(Hash(d.Backup), d.OriginalHash)) throw new IOException("备份期间源文件变化：" + name);
                    using (FileStream source = new FileStream(d.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (FileStream target = new FileStream(d.Stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] prefix = d.NewPrefix(); target.Write(prefix, 0, prefix.Length); source.Position = d.OriginalPrefix.Length; source.CopyTo(target); target.Flush(true);
                    }
                }
                if (!p.Inventory.SequenceEqual(InventoryOf(p.RootPath, p.IncludeData), StringComparer.OrdinalIgnoreCase)) throw new IOException("保存期间项目文件列表发生变化，请重新加载。");
                foreach (Document d in p.All) d.CheckUnchanged();
                foreach (Document d in changed) { d.CheckUnchanged(); if (BeforeReplaceForTests != null) BeforeReplaceForTests(committed.Count); File.Replace(d.Stage, d.Path, null); committed.Add(d); }
                File.WriteAllText(System.IO.Path.Combine(backup, "transaction.txt"), "COMMITTED\r\n" + string.Join("\r\n", changed.Select(d => System.IO.Path.GetFileName(d.Path))), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                List<string> failures = new List<string>();
                foreach (Document d in committed.AsEnumerable().Reverse())
                {
                    try { string restore = d.Backup + ".restore"; File.Copy(d.Backup, restore, true); File.Replace(restore, d.Path, null); }
                    catch (Exception rollback) { failures.Add(System.IO.Path.GetFileName(d.Path) + ": " + rollback.Message); }
                }
                string detail = failures.Count == 0 ? "已回滚已提交文件。" : "以下回滚失败，请从备份恢复：" + string.Join("；", failures);
                throw new IOException("保存未完成；" + detail + " 备份：" + backup + "。原因：" + ex.Message, ex);
            }
            return new PersistenceResult { BackupDirectory = backup, ChangedFileCount = changed.Count, Warnings = p.Warnings.ToArray() };
        }

        sealed class Line
        {
            internal string Value, Ending;
            internal Line(string value, string ending) { Value = value; Ending = ending; }
            internal string Render() { return Value + Ending; }
        }
        sealed class Document
        {
            internal string Path, Kind, Stage, Backup; internal Encoding Encoding; internal byte[] Bom, OriginalPrefix, OriginalHash;
            internal List<Line> Lines; internal long Length; internal DateTime WriteTime;
            internal string Text { get { return string.Concat(Lines.Select(l => l.Render())); } }
            internal static Document Read(string path, string kind)
            {
                RejectReparse(path); FileInfo info = new FileInfo(path);
                int lineCount = kind == ".cal" ? 13 : kind == ".dem" ? 16 : kind == ".loc" ? 11 : int.MaxValue;
                if (lineCount == int.MaxValue && info.Length > 8 * 1024 * 1024) throw new InvalidDataException("元数据文件超过8MB，不是支持的格式：" + path);
                byte[] bytes;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int count = (int)Math.Min(info.Length, lineCount == int.MaxValue ? info.Length : 65536); bytes = new byte[count];
                    int offset = 0, n; while (offset < count && (n = stream.Read(bytes, offset, count - offset)) > 0) offset += n;
                    if (offset != count) throw new IOException("读取文件期间长度发生变化：" + path);
                }
                Encoding encoding; int bom = 0, unit = 1; bool big = false;
                if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) { encoding = Utf8; bom = 3; }
                else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = new UnicodeEncoding(false, false, true); bom = 2; unit = 2; }
                else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = new UnicodeEncoding(true, false, true); bom = 2; unit = 2; big = true; }
                else encoding = null;
                int end = bytes.Length, lines = 0;
                if (lineCount != int.MaxValue)
                {
                    for (int i = bom; i + unit <= bytes.Length; i += unit)
                    {
                        bool lf = unit == 1 ? bytes[i] == 10 : big ? bytes[i] == 0 && bytes[i + 1] == 10 : bytes[i] == 10 && bytes[i + 1] == 0;
                        if (lf && ++lines == lineCount) { end = i + unit; break; }
                    }
                    if (lines != lineCount) throw new InvalidDataException("非空文件头不完整/格式不支持：" + System.IO.Path.GetFileName(path));
                }
                if (encoding == null) { try { Utf8.GetString(bytes, 0, end); encoding = Utf8; } catch (DecoderFallbackException) { encoding = Gbk; } }
                string text = encoding.GetString(bytes, bom, end - bom);
                if (text.IndexOf('\0') >= 0) throw new InvalidDataException("不支持二进制元数据：" + path);
                Document d = new Document { Path = path, Kind = kind, Encoding = encoding, Bom = bytes.Take(bom).ToArray(), OriginalPrefix = bytes.Take(end).ToArray(), Length = info.Length, WriteTime = info.LastWriteTimeUtc };
                d.ReplaceText(text); d.OriginalHash = Hash(path); d.Validate(); return d;
            }
            internal void ReplaceText(string text)
            {
                Lines = new List<Line>(); foreach (Match m in Regex.Matches(text, @"([^\r\n]*)(\r\n|\n|\r|$)"))
                    if (m.Length != 0) Lines.Add(new Line(m.Groups[1].Value, m.Groups[2].Value));
            }
            internal byte[] NewPrefix() { return Bom.Concat(Encoding.GetBytes(Text)).ToArray(); }
            internal void CheckUnchanged()
            {
                RejectReparse(Path); FileInfo f = new FileInfo(Path);
                if (f.Length != Length || f.LastWriteTimeUtc != WriteTime || !Same(Hash(Path), OriginalHash)) throw new IOException("源文件已被其他程序修改，请重新加载：" + System.IO.Path.GetFileName(Path));
            }
            int Index(string key)
            {
                List<int> indexes = new List<int>();
                for (int i = 0; i < Lines.Count; i++)
                {
                    string cell = Lines[i].Value.Split('\t')[0].Trim().TrimEnd(':', '：', '=');
                    if (string.Equals(cell, key, StringComparison.OrdinalIgnoreCase) || ((key == "SR" || key == "Start_point" || key == "Final_point") && cell.StartsWith(key, StringComparison.OrdinalIgnoreCase))) indexes.Add(i);
                }
                if (indexes.Count > 1) throw new InvalidDataException("字段重复：" + key + " / " + Path); return indexes.Count == 0 ? -1 : indexes[0];
            }
            internal string Value(string key) { int i = Index(key); if (i < 0) return ""; int tab = Lines[i].Value.IndexOf('\t'); return tab < 0 ? "" : Lines[i].Value.Substring(tab + 1).Trim(); }
            internal double Number(string key) { return Parse(Value(key)); }
            internal void Set(string key, string value)
            {
                int i = Index(key); if (i < 0) throw new InvalidDataException("缺少支持的字段：" + key + " / " + System.IO.Path.GetFileName(Path));
                int tab = Lines[i].Value.IndexOf('\t'); if (tab < 0) throw new InvalidDataException("字段不是制表符格式：" + key);
                string original = Lines[i].Value.Substring(tab + 1); Match m = Regex.Match(original, @"^(\s*)(.*?)(\s*)$");
                Lines[i].Value = Lines[i].Value.Substring(0, tab + 1) + m.Groups[1].Value + value + m.Groups[3].Value;
            }
            internal double OptionalVelocity() { foreach (string key in new[] { "V (m/usec)", "Velocity (m/usec)" }) if (Index(key) >= 0) return Number(key); return double.NaN; }
            internal void SetVelocity(double n) { int count = SetOptionalVelocity(n); if (count == 0) throw new InvalidDataException("缺少波速字段：" + Path); }
            internal int SetOptionalVelocity(double n) { int count = 0; foreach (string key in new[] { "V (m/usec)", "Velocity (m/usec)" }) if (Index(key) >= 0) { Set(key, CalibrationPersistence.Number(n)); count++; } return count; }
            internal void SetLength(double n) { int count = SetOptionalLength(n); if (count == 0) throw new InvalidDataException("缺少电缆长度字段：" + Path); }
            internal int SetOptionalLength(double n) { int count = 0; foreach (string key in new[] { "Cable length (m)", "Cable-length (m)" }) if (Index(key) >= 0) { Set(key, CalibrationPersistence.Number(n)); count++; } return count; }
            void Validate()
            {
                if (Kind == ".cal")
                {
                    Positive(Number("Cable length (m)"), "CAL长度"); Positive(OptionalVelocity(), "CAL波速"); Positive(Number("SR"), "CAL采样率");
                    if (!Finite(Number("Start_point")) || !Finite(Number("Final_point"))) throw new InvalidDataException("CAL缺少时间标记：" + Path);
                }
                if (Kind == ".loc")
                {
                    Positive(Number("Cable-length (m)"), "LOC长度"); Positive(OptionalVelocity(), "LOC波速");
                    if (Index("Data") < 0) throw new InvalidDataException("LOC缺少Data头：" + Path);
                }
                if (Kind == ".dem" && (Index("Cal.File-path") < 0 || Index("DATA-TOTAL") < 0 || (!Lines.Last().Value.Contains("Voltage") || !Lines.Last().Value.Contains("PD"))))
                    throw new InvalidDataException("DEM头格式不支持：" + Path);
            }
            internal void ChangeItem(double? length, Func<string[], double> velocity)
            {
                bool lengthFound = false;
                foreach (Line line in Lines)
                {
                    Match m = Regex.Match(line.Value, "^(?<prefix>\\s*(?<key>[^=]+?)\\s*=\\s*\")(?<value>.*)(?<suffix>\"\\s*)$");
                    if (!m.Success) continue; string key = m.Groups["key"].Value.Trim().TrimEnd(':', '：').Trim(); string value = m.Groups["value"].Value; string replacement = value;
                    if (HVX.ProjectModel.ProjectMetadataKeys.Canonical(key) == "CableLength") { lengthFound = true; if (length.HasValue) replacement = CalibrationPersistence.Number(length.Value); }
                    else if (key == "波速" || key == "Velocity (m/usec)" || key == "V (m/usec)")
                    {

                        replacement = CalibrationPersistence.Number(velocity(new string[9]));
                    }
                    else
                    {
                        int rowId;
                        if (int.TryParse(key, NumberStyles.Integer, Inv, out rowId))
                        {
                            string[] cells = value.Split(',');
                            if (cells.Length < 9) throw new InvalidDataException("ITEM校准行结构未知：" + key);
                            if (Finite(Parse(cells[2])) || Finite(Parse(cells[3])))
                            {
                                double v = velocity(cells); if (length.HasValue) cells[2] = CalibrationPersistence.Number(length.Value); cells[3] = CalibrationPersistence.Number(v); replacement = string.Join(",", cells);
                            }
                        }
                    }
                    line.Value = m.Groups["prefix"].Value + replacement + m.Groups["suffix"].Value;
                }
                if (length.HasValue && !lengthFound) throw new InvalidDataException("ITEM没有电缆长度字段。");
            }
        }

        static void ValidateCalibrationRange(Document d, double incidentSeconds, double reflectionSeconds)
        {
            string name = System.IO.Path.GetFileName(d.Path);
            double rate = d.Number("SR"); Positive(rate, "CAL采样率：" + name);
            if (!Finite(incidentSeconds) || !Finite(reflectionSeconds) || incidentSeconds < 0 || reflectionSeconds <= incidentSeconds)
                throw new InvalidDataException("CAL时间标记无效：入射时间不得为负，反射时间必须大于入射时间。文件：" + name);
            long count = CountWaveSamples(d);
            if (count < 2) throw new InvalidDataException("CAL至少需要2个有效采样点。文件：" + name);
            double finalSampleTime = (count - 1) / rate;
            if (!Finite(finalSampleTime) || reflectionSeconds > finalSampleTime)
                throw new InvalidDataException("CAL入射/反射标记超过实际采样时间范围。文件：" + name);
        }
        static long CountWaveSamples(Document d)
        {
            long count = 0;
            using (StreamReader r = new StreamReader(d.Path, d.Encoding, true))
            {
                for (int i = 0; i < 13; i++) if (r.ReadLine() == null) throw new InvalidDataException("CAL头部被截断：" + d.Path);
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string first = line.Split('\t')[0]; double value;
                    if (!double.TryParse(first.Trim(), NumberStyles.Float, Inv, out value) || !Finite(value))
                        throw new InvalidDataException("CAL波形包含无效采样值，不能确认游标时间范围。文件：" + System.IO.Path.GetFileName(d.Path));
                    count++;
                }
            }
            return count;
        }
        sealed class CableBlock
        {
            internal List<Line> Lines = new List<Line>(); internal List<CableJoint> Joints = new List<CableJoint>(); internal List<int> AttachmentIndexes = new List<int>();
            internal string Render() { return string.Concat(Lines.Select(l => l.Render())); }
            internal void SetEnd(double length)
            {
                int index = AttachmentIndexes.Last(); string[] row = Lines[index].Value.Split('\t'); row[3] = Number(length) + "m"; Lines[index].Value = string.Join("\t", row);
            }
            internal void ReplaceJoints(CableJoint[] replacement, CableJoint[] displayedOriginal)
            {
                int first = AttachmentIndexes[0], last = AttachmentIndexes.Last();
                string eol = Lines.Select(l => l.Ending).FirstOrDefault(s => s.Length > 0) ?? "\r\n";
                int firstInterior = AttachmentIndexes.Count > 2 ? AttachmentIndexes[1] : last;
                List<Line> result = Lines.Take(firstInterior).ToList();
                HashSet<string> reservedNames = new HashSet<string>(AttachmentIndexes.Select(at => Lines[at].Value.Split('\t')[1]), StringComparer.Ordinal);
                for (int i = 1; i < replacement.Length - 1; i++)
                {
                    CableJoint j = replacement[i];
                    int oldIndex = Joints.FindIndex(x => x.PositionMetres == j.PositionMetres);
                    CableJoint shown = displayedOriginal.FirstOrDefault(x => x.PositionMetres == j.PositionMetres);
                    string position = j.PositionMetres.ToString("0.0", Inv) + "m";
                    string attachment = j.AttachmentType, body = j.BodyType;
                    if (oldIndex > 0 && oldIndex < Joints.Count - 1)
                    {
                        position = Lines[AttachmentIndexes[oldIndex]].Value.Split('\t')[3];
                        if (shown != null && shown.AttachmentType == j.AttachmentType) attachment = Joints[oldIndex].AttachmentType;
                        if (shown != null && shown.BodyType == j.BodyType) body = Joints[oldIndex].BodyType;
                    }
                    if (oldIndex > 0 && oldIndex < Joints.Count - 1)
                    {
                        int at = AttachmentIndexes[oldIndex]; string[] a = Lines[at].Value.Split('\t'); a[2] = attachment;
                        result.Add(new Line(string.Join("\t", a), Lines[at].Ending));
                        if (at + 1 < Lines.Count && Lines[at + 1].Value.StartsWith("2\t", StringComparison.Ordinal))
                        {
                            string[] bodyRow = Lines[at + 1].Value.Split('\t'); bodyRow[2] = body;
                            result.Add(new Line(string.Join("\t", bodyRow), Lines[at + 1].Ending));
                        }
                        else result.Add(new Line("2\tBody\t" + body + "\t", eol));
                    }
                    else
                    {
                        int suffix = i; while (reservedNames.Contains("Joint_" + suffix.ToString(Inv))) suffix++;
                        string name = "Joint_" + suffix.ToString(Inv); reservedNames.Add(name);
                        result.Add(new Line("1\t" + name + "\t" + attachment + "\t" + position, eol));
                        result.Add(new Line("2\tBody\t" + body + "\t", eol));
                    }
                }
                result.AddRange(Lines.Skip(last)); Lines = result;
            }
        }
        static List<CableBlock> ParseCable(Document d)
        {
            List<CableBlock> blocks = new List<CableBlock>(); CableBlock current = null;
            foreach (Line line in d.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Value)) { if (current != null) current.Lines.Add(new Line(line.Value, line.Ending)); else throw new InvalidDataException("CABLE头部格式未知。"); continue; }
                string[] row = line.Value.Split('\t'); if (row.Length < 4) throw new InvalidDataException("CABLE行必须包含4个制表符字段。");
                if (row[0].Trim() == "0") { current = new CableBlock(); blocks.Add(current); }
                if (current == null) throw new InvalidDataException("CABLE缺少相别段。");
                if (row[0].Trim() != "0" && row[0].Trim() != "1" && row[0].Trim() != "2") throw new InvalidDataException("CABLE存在未知行类型。");
                current.Lines.Add(new Line(line.Value, line.Ending));
                if (row[0].Trim() == "1")
                {
                    // Names are user content, not a locale-dependent row type.
                    // Endpoint roles follow each validated phase block's order.
                    if (String.IsNullOrWhiteSpace(row[1])) throw new InvalidDataException("CABLE附件名称为空。");
                    double position = Parse(row[3]); if (!Finite(position)) throw new InvalidDataException("CABLE附件位置无效。");
                    current.Joints.Add(new CableJoint { PositionMetres = position, AttachmentType = row[2], BodyType = "", IsEndpoint = false });
                    current.AttachmentIndexes.Add(current.Lines.Count - 1);
                }
                else if (row[0].Trim() == "2")
                {
                    if (current.Joints.Count == 0 || current.Joints.Last().BodyType.Length > 0) throw new InvalidDataException("CABLE本体行无法对应附件。");
                    current.Joints.Last().BodyType = row[2];
                }
            }
            if (blocks.Count == 0) throw new InvalidDataException("CABLE没有电缆段。");
            foreach (CableBlock b in blocks)
            {
                if (b.Joints.Count < 2 || b.Joints[0].PositionMetres != 0) throw new InvalidDataException("CABLE缺少有效始端/终端。");
                b.Joints[0].IsEndpoint = true; b.Joints.Last().IsEndpoint = true;
                for (int i = 1; i < b.Joints.Count; i++) if (b.Joints[i].PositionMetres <= b.Joints[i - 1].PositionMetres) throw new InvalidDataException("CABLE位置不是严格递增。");
            }
            return blocks;
        }
        static void RequireSharedGeometry(List<CableBlock> blocks)
        {
            double[] positions = blocks[0].Joints.Select(j => j.PositionMetres).ToArray();
            if (blocks.Any(b => !b.Joints.Select(j => j.PositionMetres).SequenceEqual(positions)))
                throw new InvalidDataException("不同电缆段接头位置不同，不能通过共用列表覆盖；请按相别分别编辑。");
        }
    }
}
