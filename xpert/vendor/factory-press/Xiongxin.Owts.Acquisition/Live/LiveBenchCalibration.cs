using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 台架标定增益/分压/电流比例。从工程配置 JSON 读写；默认占位增益（如 VoltageGain=1000）禁止真高压非零加压。
    /// 仿真可继续用占位；仅 AllowLiveHardware / Live 路径强制本门。
    /// </summary>
    public sealed class LiveBenchCalibration
    {
        /// <summary>构造函数默认占位：与 <see cref="LiveHardwareOptions"/> 历史默认一致，视为未台架标定。</summary>
        public const double DangerousPlaceholderVoltageGain = 1000.0;
        public const double DangerousPlaceholderDividerRatio = 1.0;
        public const double DangerousPlaceholderCurrentScale = 1.0;

        public int Version { get; set; }
        /// <summary>台架已写入并确认；false 时 Live 非零加压必须拒绝。</summary>
        public bool BenchCalibrated { get; set; }
        public double VoltageGainVPerAoVolt { get; set; }
        public double DividerRatioVPerV { get; set; }
        /// <summary>AI0：工程电流 A = rawV × CurrentScaleAPerVolt。</summary>
        public double CurrentScaleAPerVolt { get; set; }
        /// <summary>AI2 源反馈：工程 V = rawV × SourceVoltageScaleVPerV（可选；0=未配）。</summary>
        public double SourceVoltageScaleVPerV { get; set; }
        public string CalibratedBy { get; set; }
        public string CalibratedUtc { get; set; }
        public string Notes { get; set; }

        public LiveBenchCalibration()
        {
            Version = 1;
            BenchCalibrated = false;
            VoltageGainVPerAoVolt = DangerousPlaceholderVoltageGain;
            DividerRatioVPerV = DangerousPlaceholderDividerRatio;
            CurrentScaleAPerVolt = DangerousPlaceholderCurrentScale;
            SourceVoltageScaleVPerV = 0;
            CalibratedBy = string.Empty;
            CalibratedUtc = string.Empty;
            Notes = string.Empty;
        }

        /// <summary>VoltageGain 为危险占位（≤0 / NaN / 恰为 1000）。</summary>
        public static bool IsDangerousPlaceholderVoltageGain(double gain)
        {
            if (double.IsNaN(gain) || double.IsInfinity(gain) || gain <= 0) return true;
            return Math.Abs(gain - DangerousPlaceholderVoltageGain) < 1e-9;
        }

        /// <summary>DividerRatio 未有效标定（≤0 / NaN / 恰为占位 1.0）。</summary>
        public static bool IsDangerousPlaceholderDividerRatio(double ratio)
        {
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0) return true;
            return Math.Abs(ratio - DangerousPlaceholderDividerRatio) < 1e-12;
        }

        /// <summary>
        /// Live 非零加压是否允许：须显式 BenchCalibrated，且增益/分压不是危险占位。
        /// </summary>
        public bool AllowsLiveNonZeroEnergize()
        {
            return BenchCalibrated
                && !IsDangerousPlaceholderVoltageGain(VoltageGainVPerAoVolt)
                && DividerRatioVPerV > 0
                && !double.IsNaN(DividerRatioVPerV)
                && !double.IsInfinity(DividerRatioVPerV)
                && CurrentScaleAPerVolt > 0
                && !double.IsNaN(CurrentScaleAPerVolt);
        }

        public string RefuseReasonOrNull()
        {
            if (AllowsLiveNonZeroEnergize()) return null;
            return StatusCopyBenchRefuse();
        }

        public static string StatusCopyBenchRefuse()
        {
            return Xiongxin.Owts.Contracts.Ui.StatusCopy.BenchGainNotCalibrated;
        }

        public string ToJson()
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"version\":").Append(Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"benchCalibrated\":").Append(BenchCalibrated ? "true" : "false");
            AppendNum(sb, "voltageGainVPerAoVolt", VoltageGainVPerAoVolt);
            AppendNum(sb, "dividerRatioVPerV", DividerRatioVPerV);
            AppendNum(sb, "currentScaleAPerVolt", CurrentScaleAPerVolt);
            AppendNum(sb, "sourceVoltageScaleVPerV", SourceVoltageScaleVPerV);
            AppendStr(sb, "calibratedBy", CalibratedBy);
            AppendStr(sb, "calibratedUtc", CalibratedUtc);
            AppendStr(sb, "notes", Notes);
            sb.Append('}');
            return sb.ToString();
        }

        public static LiveBenchCalibration ParseJson(string json)
        {
            var c = new LiveBenchCalibration();
            if (string.IsNullOrWhiteSpace(json)) return c;
            c.Version = (int)ExtractDouble(json, "version", 1);
            c.BenchCalibrated = ExtractBool(json, "benchCalibrated", false);
            c.VoltageGainVPerAoVolt = ExtractDouble(json, "voltageGainVPerAoVolt", DangerousPlaceholderVoltageGain);
            c.DividerRatioVPerV = ExtractDouble(json, "dividerRatioVPerV", DangerousPlaceholderDividerRatio);
            c.CurrentScaleAPerVolt = ExtractDouble(json, "currentScaleAPerVolt", DangerousPlaceholderCurrentScale);
            c.SourceVoltageScaleVPerV = ExtractDouble(json, "sourceVoltageScaleVPerV", 0);
            c.CalibratedBy = ExtractString(json, "calibratedBy") ?? string.Empty;
            c.CalibratedUtc = ExtractString(json, "calibratedUtc") ?? string.Empty;
            c.Notes = ExtractString(json, "notes") ?? string.Empty;
            return c;
        }

        public static LiveBenchCalibration LoadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("台架标定文件路径为空。", "path");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new FileNotFoundException("台架标定文件不存在：" + full, full);
            return ParseJson(File.ReadAllText(full, Encoding.UTF8));
        }

        public void SaveFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("台架标定文件路径为空。", "path");
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(full, ToJson(), new UTF8Encoding(false));
        }

        /// <summary>构造一份可用于 FakeLive / 台架写入的有效标定（非占位）。</summary>
        public static LiveBenchCalibration CreateBenchSample(
            double voltageGainVPerAoVolt = 6000,
            double dividerRatioVPerV = 12000,
            double currentScaleAPerVolt = 0.002,
            double sourceVoltageScaleVPerV = 6000,
            string calibratedBy = "bench",
            string notes = "低压假负载台架样本")
        {
            if (IsDangerousPlaceholderVoltageGain(voltageGainVPerAoVolt))
                throw new ArgumentOutOfRangeException("voltageGainVPerAoVolt", "样本增益不可为危险占位 1000。");
            return new LiveBenchCalibration
            {
                Version = 1,
                BenchCalibrated = true,
                VoltageGainVPerAoVolt = voltageGainVPerAoVolt,
                DividerRatioVPerV = dividerRatioVPerV,
                CurrentScaleAPerVolt = currentScaleAPerVolt,
                SourceVoltageScaleVPerV = sourceVoltageScaleVPerV,
                CalibratedBy = calibratedBy ?? "bench",
                CalibratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Notes = notes ?? string.Empty
            };
        }

        static void AppendNum(StringBuilder sb, string name, double value)
        {
            sb.Append(",\"").Append(name).Append("\":");
            sb.Append(value.ToString("G17", CultureInfo.InvariantCulture));
        }

        static void AppendStr(StringBuilder sb, string name, string value)
        {
            sb.Append(",\"").Append(name).Append("\":\"");
            sb.Append(Escape(value));
            sb.Append('"');
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static bool ExtractBool(string json, string name, bool fallback)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return fallback;
            int j = colon + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j + 4 <= json.Length && string.Compare(json, j, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
            if (j + 5 <= json.Length && string.Compare(json, j, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                return false;
            return fallback;
        }

        static double ExtractDouble(string json, string name, double fallback)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return fallback;
            int j = colon + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            int k = j;
            if (k < json.Length && (json[k] == '-' || json[k] == '+')) k++;
            while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '.' || json[k] == 'e' || json[k] == 'E' || json[k] == '+' || json[k] == '-'))
                k++;
            if (k <= j) return fallback;
            double v;
            if (double.TryParse(json.Substring(j, k - j), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }

        static string ExtractString(string json, string name)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = q1 + 1;
            var sb = new StringBuilder();
            while (q2 < json.Length)
            {
                char ch = json[q2];
                if (ch == '\\' && q2 + 1 < json.Length)
                {
                    char n = json[q2 + 1];
                    if (n == '"' || n == '\\') { sb.Append(n); q2 += 2; continue; }
                    if (n == 'n') { sb.Append('\n'); q2 += 2; continue; }
                    if (n == 'r') { sb.Append('\r'); q2 += 2; continue; }
                }
                if (ch == '"') break;
                sb.Append(ch);
                q2++;
            }
            return sb.ToString();
        }
    }
}
