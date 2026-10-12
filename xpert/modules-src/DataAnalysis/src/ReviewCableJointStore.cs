using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.PdReview
{
    // Strict writer for the tab-separated format produced by ProjectCreation.
    // Unknown/ambiguous formats are read-only; waveform and ITEM files are never written.
    internal sealed class ReviewCableJointStore
    {
        internal sealed class Block
        {
            internal string Phase;
            internal readonly List<int> Lines = new List<int>();
            internal readonly List<double> Positions = new List<double>();
            internal double Length { get { return Positions[Positions.Count - 1]; } }
            public override string ToString() { return Phase == "ALL" ? "ALL" : Phase + " / L" + (Phase[0] - 'A' + 1); }
        }
        internal sealed class Plan
        {
            internal string CablePath, ExpectedHash, Phase;
            internal byte[] Original, Replacement;
            internal double PositionM;
        }
        internal string CablePath;
        internal byte[] Original;
        internal readonly List<Block> Blocks = new List<Block>();
        string[] lines;
        string newline;
        Encoding encoding;
        byte[] preamble;

        static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        static string Hash(byte[] data) { using (var h = SHA256.Create()) return Convert.ToBase64String(h.ComputeHash(data)); }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); }
        static string PhaseName(string phase)
        {
            switch (phase.Trim().ToUpperInvariant())
            {
                case "A": case "A相": case "L1": return "A";
                case "B": case "B相": case "L2": return "B";
                case "C": case "C相": case "L3": return "C";
                case "ALL": case "ALL相": case "COMMON": case "全相": case "三相": case "共用": return "ALL";
                default: throw new InvalidDataException("无法识别电缆相别 / Unknown cable phase.");
            }
        }
        internal static ReviewCableJointStore Read(string projectDirectory)
        {
            string root = Path.GetFullPath(projectDirectory);
            Require(Directory.Exists(root), "项目目录不存在 / Project directory is unavailable.");
            for (var d = new DirectoryInfo(root); d != null; d = d.Parent)
                Require((d.Attributes & FileAttributes.ReparsePoint) == 0, "链接目录不支持结构编辑 / Linked directories cannot be edited.");
            string cable = Path.Combine(root, "CABLE.in");
            var info = new FileInfo(cable);
            Require(info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.Length > 0 && info.Length <= 2 * 1024 * 1024,
                "电缆结构文件不可用 / Cable structure file is unavailable.");
            var result = new ReviewCableJointStore { CablePath = cable, Original = File.ReadAllBytes(cable) };
            Require(result.Original.Length <= 2 * 1024 * 1024, "电缆文件过大 / Cable file is too large.");
            byte[] data = result.Original;
            int skip = 0;
            if (data.Length >= 3 && data[0] == 239 && data[1] == 187 && data[2] == 191) { result.encoding = new UTF8Encoding(false, true); skip = 3; }
            else if (data.Length >= 2 && data[0] == 255 && data[1] == 254) { result.encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (data.Length >= 2 && data[0] == 254 && data[1] == 255) { result.encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            else
            {
                result.encoding = new UTF8Encoding(false, true);
                try { result.encoding.GetString(data); }
                catch (DecoderFallbackException) { result.encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            }
            result.preamble = data.Take(skip).ToArray();
            string text = result.encoding.GetString(data, skip, data.Length - skip);
            Require(text.IndexOf('\0') < 0, "未知电缆文件编码 / Unsupported cable encoding.");
            result.newline = text.Contains("\r\n") ? "\r\n" : "\n";
            result.lines = text.Replace("\r\n", "\n").Split('\n');
            Require(!result.lines.Any(line => line.Contains("\r")), "未知换行格式 / Unsupported line endings.");
            Block block = null; bool needBody = false, sawBody = false;
            for (int i = 0; i < result.lines.Length; i++)
            {
                string line = result.lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] cells = line.Split('\t');
                Require(cells.Length == 4, "仅支持标准四列电缆结构 / Only standard four-column cable structures can be edited.");
                string level = cells[0].Trim();
                if (level == "0")
                {
                    if (block != null) ValidateBlock(block, needBody, sawBody);
                    Require(string.IsNullOrWhiteSpace(cells[2]) && string.IsNullOrWhiteSpace(cells[3]), "未知电缆相别记录 / Unsupported phase record.");
                    string phase = PhaseName(cells[1]);
                    Require(result.Blocks.All(b => b.Phase != phase), "重复相别需要先整理 / Duplicate phase blocks require review.");
                    block = new Block { Phase = phase }; result.Blocks.Add(block); needBody = false; sawBody = false;
                }
                else if (level == "1")
                {
                    Require(block != null, "缺少相别记录 / Missing phase record.");
                    var match = Regex.Match(cells[3].Trim(), @"^([+]?(?:\d+(?:\.\d*)?|\.\d+))\s*(?:m|米)?$", RegexOptions.IgnoreCase);
                    double pos;
                    Require(match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pos),
                        "附件位置格式错误 / Invalid accessory position.");
                    pos = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    Require(Finite(pos) && pos <= 1000000 && !string.IsNullOrWhiteSpace(cells[1]), "附件位置无效 / Invalid accessory position.");
                    if (block.Positions.Count == 0) Require(pos == 0, "始端位置必须为0 / Start position must be zero.");
                    else Require(needBody && sawBody && pos > block.Positions.Last(), "电缆区段不连续 / Cable segment order or material is invalid.");
                    block.Positions.Add(pos); block.Lines.Add(i); needBody = true; sawBody = false;
                    Require(block.Positions.Count <= 4096, "附件数量过多 / Too many cable accessories.");
                }
                else if (level == "2")
                {
                    Require(block != null && needBody && !sawBody && !string.IsNullOrWhiteSpace(cells[2]) && string.IsNullOrWhiteSpace(cells[3]),
                        "区段材料记录不明确 / Ambiguous segment material record.");
                    sawBody = true;
                }
                else throw new InvalidDataException("未知电缆记录 / Unsupported cable record.");
            }
            Require(block != null, "没有可编辑的电缆相别 / No editable cable phase.");
            ValidateBlock(block, needBody, sawBody);
            return result;
        }
        static void ValidateBlock(Block b, bool needBody, bool sawBody)
        { Require(b.Positions.Count >= 2 && b.Length > 0 && needBody && !sawBody, "电缆端点或区段不完整 / Incomplete cable endpoints or segments."); }
        internal Plan Prepare(string phase, double physicalPositionM, string attachmentType)
        {
            physicalPositionM = Math.Round(physicalPositionM, 3, MidpointRounding.AwayFromZero);
            Block block = Blocks.SingleOrDefault(b => b.Phase == phase);
            Require(block != null, "请选择文件中的具体电缆相别 / Select an explicit cable phase.");
            Require(Finite(physicalPositionM) && physicalPositionM > 0 && physicalPositionM < block.Length,
                "接头位置须在电缆两端之间 / The joint must be strictly between the cable endpoints.");
            Require(block.Positions.All(p => Math.Abs(p - physicalPositionM) >= .001),
                "该位置已有附件 / An accessory already exists at this position.");
            Require(!string.IsNullOrWhiteSpace(attachmentType) && attachmentType.Length <= 80 && attachmentType.IndexOfAny(new[] { '\r', '\n', '\t', '\0' }) < 0,
                "接头类型无效 / Invalid joint type.");
            int next = block.Positions.FindIndex(p => p > physicalPositionM), insert = block.Lines[next];
            string body = lines.Skip(block.Lines[next - 1] + 1).Take(insert - block.Lines[next - 1] - 1)
                .Single(line => !string.IsNullOrWhiteSpace(line));
            int number = block.Positions.Count - 1;
            string label;
            do { label = "接头_" + (number++).ToString(CultureInfo.InvariantCulture); }
            while (block.Lines.Any(i => string.Equals(lines[i].Split('\t')[1], label, StringComparison.OrdinalIgnoreCase)));
            var output = lines.ToList();
            output.InsertRange(insert, new[] { "1\t" + label + "\t" + attachmentType.Trim() + "\t" + physicalPositionM.ToString("0.###", CultureInfo.InvariantCulture) + "m", body });
            byte[] text = encoding.GetBytes(string.Join(newline, output));
            return new Plan { CablePath = CablePath, Phase = phase, PositionM = physicalPositionM, Original = (byte[])Original.Clone(),
                ExpectedHash = Hash(Original), Replacement = preamble.Concat(text).ToArray() };
        }
        // Conversion is kept separate/testable. CABLE stores the full project cable;
        // the chart displays DUT length after lead subtraction, optionally mirrored.
        internal static double ProjectPosition(double displayedM, double projectLengthM, double leadM, bool reversed)
        {
            Require(Finite(projectLengthM) && Finite(leadM) && leadM >= 0 && leadM < projectLengthM,
                "连接线或电缆长度无效 / Invalid lead or cable length.");
            double dut = projectLengthM - leadM;
            Require(Finite(displayedM) && displayedM > 0 && displayedM < dut,
                "请选择电缆内部位置 / Choose a position inside the cable.");
            return (reversed ? dut - displayedM : displayedM) + leadM;
        }
        internal static string Commit(Plan plan)
        {
            Require(plan != null, "缺少保存内容 / Missing edit plan.");
            string root = Path.GetDirectoryName(plan.CablePath);
            string suffix = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            string stage = Path.Combine(root, ".cable-edit-" + suffix + ".tmp");
            string backup = Path.Combine(root, "CABLE.before-joint-" + suffix + ".bak");
            // Cooperate with the existing data-browser metadata editor.
            string lockPath = Path.Combine(root, ".DataBrowser.write.lock");
            Require(!File.Exists(lockPath) || (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) == 0,
                "编辑锁不安全 / Unsafe metadata edit lock.");
            using (var editLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    var current = Read(root);
                    Require(current.CablePath == plan.CablePath && Hash(current.Original) == plan.ExpectedHash,
                        "电缆结构已变化，请重新打开编辑窗口 / Cable structure changed. Reopen the editor.");
                    Require((File.GetAttributes(plan.CablePath) & FileAttributes.ReadOnly) == 0,
                        "电缆结构为只读 / Cable structure is read-only.");
                    using (var writer = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { writer.Write(plan.Replacement, 0, plan.Replacement.Length); writer.Flush(true); }
                    Require(Hash(File.ReadAllBytes(plan.CablePath)) == plan.ExpectedHash,
                        "电缆结构已变化，保存已取消 / Cable structure changed. Save cancelled.");
                    // Atomic replacement supplies an exact pre-edit backup. No
                    // delete/move fallback: unsupported filesystems fail safely.
                    File.Replace(stage, plan.CablePath, backup, false);
                    return backup;
                }
                finally { if (File.Exists(stage)) File.Delete(stage); }
            }
        }
    }
}
