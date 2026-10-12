using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryArchiveFile
    { public string Name = "", Sha256 = ""; public long Length; }
    public sealed class FactoryArchiveManifest
    {
        public string DeviceNumber = "", OriginalSourceId = "", ArtSerial = "", PicoSerial = "", Kind = "", CapturedChinaTime = "";
        public DateTime SavedUtc, CapturedUtc;
        public bool Simulation, Completed, ResidualSafe, ReadOnlyAcquisition, ZeroTriggerDiagnostic;
        public double PicoRateHz, CoefficientAtCapturePcPerV, FullScaleV, DividerVPerV;
        public int ResolutionBits, HardwareBits;
        public List<FactoryArchiveFile> Files = new List<FactoryArchiveFile>();
        public string Boundary = "保存采集时参数及质量，不代表设备检定合格或当前已放电；TF仅为脉冲候选特征。";
    }
    public static class FactoryDataArchive
    {
        public static string Save(FactoryResult result, string rootDirectory, string deviceNumber, FactoryTfResult current, FactoryTfSession session, FactoryTfAnnotationDocument annotations = null)
        {
            if (result == null || !result.CompleteCaptureAvailable() || result.Capture == null || result.Settings == null || result.Metrics == null || result.Metrics.Detected == null)
                throw new InvalidOperationException("只能归档完整结束并通过本轮残压检查的数据，不把中断结果标为完整。");
            result.Capture.ValidateCapture();
            if (String.IsNullOrWhiteSpace(rootDirectory) || String.IsNullOrWhiteSpace(deviceNumber)) throw new ArgumentException("请填写设备编号并选择保存根目录。");
            string source = Path.GetFullPath(result.DirectoryPath), root = Path.GetFullPath(rootDirectory);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("原始记录不存在，未创建假数据档案：" + source);
            string[] required = { "capture.bin", "settings.xml", "factory-settings.xml", "result.xml" };
            foreach (string name in required) if (!File.Exists(Path.Combine(source, name))) throw new FileNotFoundException("缺少本轮原始记录：" + name);
            var published = Xiongxin.ZeroBench.BenchCapture.Load(source);
            var c0 = result.Capture;
            if (published.ArtSerial != c0.ArtSerial || published.PicoSerial != c0.PicoSerial || published.CapturedUtc != c0.CapturedUtc ||
                published.Simulation != c0.Simulation || published.PicoRateHz != c0.PicoRateHz || published.Settings.PdCalibrationPcPerV != c0.Settings.PdCalibrationPcPerV ||
                published.Settings.RangeIndex != c0.Settings.RangeIndex || !published.PdAdc.SequenceEqual(c0.PdAdc) || !published.VoltageRaw.SequenceEqual(c0.VoltageRaw))
                throw new InvalidDataException("原始目录与当前结果不一致；未把其他轮次的文件归入本设备。");
            if (current != null)
            {
                current.Validate();
                if (!current.SourceId.Equals(source, StringComparison.OrdinalIgnoreCase) || current.CapturedUtc != c0.CapturedUtc ||
                    current.ChainKey != FactoryTfAnalysis.ChainKey(c0) || current.CoefficientPcPerV != c0.Settings.PdCalibrationPcPerV)
                    throw new InvalidDataException("本轮TF与原始采集来源不一致，未混入归档。");
            }
            if (session != null && session.RoundCount > 0 && session.ChainKey != FactoryTfAnalysis.ChainKey(c0))
                throw new InvalidDataException("累计TF属于另一设备或采集条件；请先保存并清空累计，再保存当前试验。");
            string id = SafeSegment(deviceNumber), parent = Path.Combine(root, id);
            DateTime china = ChinaTime(result.Capture.CapturedUtc);
            string directory = Path.GetFullPath(Path.Combine(parent, china.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) +
                "_" + result.Kind + (result.Settings.Simulation ? "_SIM_" : "_HW_") + Guid.NewGuid().ToString("N").Substring(0, 8)));
            if (!directory.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(directory))
                throw new InvalidOperationException("归档目标不是根目录内的全新目录，未覆盖已有数据。");
            Directory.CreateDirectory(directory);
            try
            {
                var c = result.Capture;
                var manifest = new FactoryArchiveManifest { DeviceNumber = deviceNumber.Trim(), OriginalSourceId = source, ArtSerial = c.ArtSerial,
                    PicoSerial = c.PicoSerial, Kind = result.Kind.ToString(), CapturedUtc = c.CapturedUtc, SavedUtc = DateTime.UtcNow,
                    CapturedChinaTime = china.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " +08:00", Simulation = c.Simulation,
                    Completed = result.Completed, ResidualSafe = result.ResidualSafe, ReadOnlyAcquisition = result.ReadOnlyAcquisition, ZeroTriggerDiagnostic = result.ZeroTriggerDiagnostic, PicoRateHz = c.PicoRateHz, ResolutionBits = c.Settings.ResolutionBits,
                    HardwareBits = c.HardwareBits, CoefficientAtCapturePcPerV = c.Settings.PdCalibrationPcPerV, FullScaleV = c.PicoFullScaleV, DividerVPerV = c.Settings.DividerVPerV };
                foreach (string name in new[] { "capture.bin", "settings.xml", "factory-settings.xml", "result.xml", "progress.log", "factory-range-decision.xml", "factory-acceptance-evidence.xml", "location-window.xml", "location-ncycles.bin" })
                {
                    string original = Path.Combine(source, name); if (!File.Exists(original)) continue;
                    string copy = Path.Combine(directory, name); File.Copy(original, copy, false);
                    string hash = Hash(original); if (hash != Hash(copy)) throw new IOException("复制校验失败：" + name);
                    manifest.Files.Add(new FactoryArchiveFile { Name = name, Length = new FileInfo(copy).Length, Sha256 = hash });
                }
                Program.Export(result, Path.Combine(directory, "detected-80k.csv"));
                foreach (string original in Directory.GetFiles(source, "pulse-recognition-*.xml").Concat(Directory.GetFiles(source, "calibration-template-review-*.xml"))) File.Copy(original, Path.Combine(directory, Path.GetFileName(original)), false);
                if (annotations != null && annotations.ChainKey == FactoryTfAnalysis.ChainKey(c))
                    FactoryTfAnnotationDocument.Clone(annotations).Save(Path.Combine(directory, "pulse-labels.tfann.xml"));
                if (current != null && current.SourceId.Equals(source, StringComparison.OrdinalIgnoreCase))
                { current.Validate(); SaveXml(current, Path.Combine(directory, "tf-current.xml")); ExportTf(new[] { current }, Path.Combine(directory, "tf-current.csv")); }
                if (session != null && session.RoundCount > 0)
                {
                    ExportTf(session.Rounds, Path.Combine(directory, "tf-cumulative.csv"));
                    string history = Path.Combine(directory, "tf-history"); Directory.CreateDirectory(history); int index = 0;
                    foreach (var round in session.Rounds) SaveXml(round, Path.Combine(history, "tf-analysis-" + (++index).ToString("000") + ".xml"));
                }
                foreach (string derived in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string relative = derived.Substring(directory.Length + 1);
                    if (!manifest.Files.Exists(f => f.Name == relative)) manifest.Files.Add(new FactoryArchiveFile { Name = relative, Length = new FileInfo(derived).Length, Sha256 = Hash(derived) });
                }
                SaveXml(manifest, Path.Combine(directory, "archive-manifest.xml"));
                using (var stream = new FileStream(Path.Combine(directory, "SAVE_COMPLETE.txt"), FileMode.CreateNew))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.WriteLine("COMPLETE; original hashes verified; no original data overwritten; no physical acceptance claim.");
                return directory;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(directory, "SAVE_FAILED.txt"), "保存不完整，不作为完整档案。\r\n" + ex.Message, Encoding.UTF8); } catch { }
                throw new IOException("归档未完整成功，未删除原始记录；部分文件保留在 " + directory, ex);
            }
        }
        public static string SafeSegment(string value)
        {
            string text = (value ?? "").Trim(); if (text.Length == 0 || text.Length > 80) throw new ArgumentException("设备编号须为1～80个字符。");
            var bad = Path.GetInvalidFileNameChars(); var result = new StringBuilder();
            foreach (char c in text) result.Append(Array.IndexOf(bad, c) >= 0 || c == '/' || c == '\\' ? '_' : c);
            text = result.ToString().Trim(' ', '.');
            if (text.Length == 0 || text == "." || text == "..") throw new ArgumentException("设备编号不能只含点或空白。");
            string head = text.Split('.')[0].ToUpperInvariant();
            if (head == "CON" || head == "PRN" || head == "AUX" || head == "NUL" || head == "CLOCK$" ||
                (head.Length == 4 && (head.StartsWith("COM") || head.StartsWith("LPT")) && head[3] >= '1' && head[3] <= '9')) text = "设备_" + text;
            return text;
        }
        private static DateTime ChinaTime(DateTime utc)
        { return DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(8); }
        private static string Hash(string path)
        { using (var stream = File.OpenRead(path)) using (var algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", ""); }
        public static void SaveXml<T>(T value, string path)
        { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) new XmlSerializer(typeof(T)).Serialize(stream, value); }
        public static FactoryTfResult LoadTf(string path)
        {
            var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4000000 };
            using (var reader = XmlReader.Create(path, options))
            {
                var result = (FactoryTfResult)new XmlSerializer(typeof(FactoryTfResult)).Deserialize(reader); result.Validate();
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                result.RawDataDirectory = Path.GetFileName(folder).Equals("tf-history", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder) : folder;
                return result;
            }
        }
        public static void ExportTf(IEnumerable<FactoryTfResult> rounds, string path)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.WriteLine("source_id,captured_utc,simulation,art_serial,pico_serial,threshold_pC,time_ms,start_sample,end_sample,peak_sample,charge_pC,Teq_us,Weq_MHz,snr_dB,clipped,valid,quality,pico_rate_Hz,coefficient_at_capture_pC_per_V,pd_filter_protocol");
                foreach (var round in rounds) foreach (var p in round.Pulses)
                    writer.WriteLine(String.Join(",", Csv(round.SourceId), Csv(round.CapturedUtc.ToString("o")), round.Simulation, Csv(round.ArtSerial), Csv(round.PicoSerial),
                        F(round.ThresholdPc), F(p.TimeMs), p.StartSample, p.EndSample, p.PeakSample, F(p.ChargePc), F(p.EquivalentTimeUs), F(p.EquivalentBandwidthMHz),
                        F(p.SnrDb), p.Clipped, p.Valid, Csv(p.Detail), F(round.PicoRateHz), F(round.CoefficientPcPerV),Csv(round.PdFilterProtocol)));
            }
        }
        private static string F(double value) { return value.ToString("G17", CultureInfo.InvariantCulture); }
        private static string Csv(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }
    }
    internal sealed class FactoryDataSaveDialog : Form
    {
        private readonly TextBox number = new TextBox { Dock = DockStyle.Fill }, root = new TextBox { Dock = DockStyle.Fill };
        internal string DeviceNumber { get { return number.Text.Trim(); } }
        internal string RootDirectory { get { return root.Text.Trim(); } }
        internal FactoryDataSaveDialog(Icon icon, string suggestedNumber, string suggestedRoot, string identities)
        {
            Text = "雄新电力科技 · 保存出厂试验数据"; Icon = icon; Font = new Font("Microsoft YaHei UI", 10);
            Size = new Size(760, 360); MinimumSize = Size; MaximumSize = Size; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false; number.Text = suggestedNumber; root.Text = suggestedRoot;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 3, RowCount = 5 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            foreach (int h in new[] { 50, 50, 45, 80, 45 }) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h)); Controls.Add(grid);
            grid.Controls.Add(new Label { Text = "设备出厂编号 / 序列号", Dock = DockStyle.Fill }, 0, 0); grid.Controls.Add(number, 1, 0); grid.SetColumnSpan(number, 2);
            grid.Controls.Add(new Label { Text = "保存根目录", Dock = DockStyle.Fill }, 0, 1); grid.Controls.Add(root, 1, 1);
            var browse = new Button { Text = "选择…", Dock = DockStyle.Fill }; grid.Controls.Add(browse, 2, 1);
            browse.Click += (s,e) => { using (var dialog = new FolderBrowserDialog { Description = "选择出厂数据保存根目录", SelectedPath = root.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) root.Text = dialog.SelectedPath; };
            var ids = new Label { Text = identities, Dock = DockStyle.Fill }; grid.Controls.Add(ids, 0, 2); grid.SetColumnSpan(ids, 3);
            var note = new Label { Text = "按“根目录 / 设备编号 / 采集北京时间_类型_唯一编号”保存。保留原始ADC、电压、采集时参数、诊断和TF数据；不覆盖原始记录。仿真目录明确带SIM标识。", Dock = DockStyle.Fill }; grid.Controls.Add(note, 0, 3); grid.SetColumnSpan(note, 3);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var save = new Button { Text = "保存", Width = 100, Height = 36 }; var cancel = new Button { Text = "取消", Width = 100, Height = 36, DialogResult = DialogResult.Cancel };
            save.Click += (s,e) => { try { FactoryDataArchive.SafeSegment(number.Text); if (String.IsNullOrWhiteSpace(root.Text)) throw new ArgumentException("请选择根目录。"); Path.GetFullPath(root.Text); DialogResult = DialogResult.OK; }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存参数", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            actions.Controls.Add(save); actions.Controls.Add(cancel); grid.Controls.Add(actions, 0, 4); grid.SetColumnSpan(actions, 3); AcceptButton = save; CancelButton = cancel;
        }
    }
}
