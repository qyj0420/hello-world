using System;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>会话故障分类（精简自 FactorySessionFaultCategory）。无弹窗；落盘供出厂加压诊断。</summary>
    public enum SessionFaultCategory
    {
        Unknown = 0,
        EmergencyInputLow = 1,
        ReadTimeout = 2,
        DriverError = 3,
        PicoError = 4,
        CleanupError = 5,
        BusyUnexpected = 6
    }

    /// <summary>诊断身份；不授予输出许可。CleanupError 落盘后禁止自动重开。</summary>
    public sealed class SessionFault
    {
        public const string DirectoryEnvVar = "OWTS_SESSION_FAULT_DIR";
        public const string SessionFileName = "session-fault.json";
        public const string CleanupFileName = "cleanup-fault.json";
        public const string CleanupErrorTxtName = "cleanup-error.txt";

        public string SessionId { get; set; }
        public string Device { get; set; }
        public SessionFaultCategory Category { get; set; }
        public string Phase { get; set; }
        public DateTime OccurredUtc { get; set; }
        public string Detail { get; set; }
        public string PriorDetail { get; set; }

        public SessionFault()
        {
            SessionId = string.Empty;
            Device = "Unknown";
            Phase = "Unknown";
            OccurredUtc = DateTime.UtcNow;
            Detail = string.Empty;
        }

        /// <summary>默认落盘目录：环境变量或 %TEMP%/xiongxin-owts-session-fault。</summary>
        public static string DefaultDirectory()
        {
            string env = Environment.GetEnvironmentVariable(DirectoryEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            return Path.Combine(Path.GetTempPath(), "xiongxin-owts-session-fault");
        }

        public static SessionFault Create(string sessionId, SessionFaultCategory category, string phase, string detail, string device = null)
        {
            return new SessionFault
            {
                SessionId = sessionId ?? string.Empty,
                Device = string.IsNullOrEmpty(device) ? "Unknown" : device,
                Category = category,
                Phase = phase ?? "Unknown",
                OccurredUtc = DateTime.UtcNow,
                Detail = detail ?? string.Empty
            };
        }

        /// <summary>退出原因 → 精简分类（首因冻结用）。</summary>
        public static SessionFaultCategory FromExitReason(AcquisitionExitReason exit)
        {
            switch (exit)
            {
                case AcquisitionExitReason.EmergencyStop:
                    return SessionFaultCategory.EmergencyInputLow;
                case AcquisitionExitReason.HardwareFault:
                    return SessionFaultCategory.DriverError;
                case AcquisitionExitReason.CaptureFault:
                    return SessionFaultCategory.PicoError;
                case AcquisitionExitReason.RampTimeout:
                case AcquisitionExitReason.CurrentLimitStall:
                    return SessionFaultCategory.BusyUnexpected;
                default:
                    return SessionFaultCategory.Unknown;
            }
        }

        /// <summary>写 session-fault.json（UTF-8，无 XML/弹窗）。cleanup=true 时写 cleanup 优先语义并禁止自动重开。</summary>
        public static void WriteJson(string directory, SessionFault fault, bool cleanup = false)
        {
            if (string.IsNullOrWhiteSpace(directory) || fault == null) return;
            Directory.CreateDirectory(directory);
            string name = cleanup ? CleanupFileName : SessionFileName;
            string path = Path.Combine(directory, name);
            string pending = path + ".pending." + Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"sessionId\": " + JsonString(fault.SessionId) + ",");
            sb.AppendLine("  \"device\": " + JsonString(fault.Device) + ",");
            sb.AppendLine("  \"category\": " + JsonString(fault.Category.ToString()) + ",");
            sb.AppendLine("  \"phase\": " + JsonString(fault.Phase) + ",");
            sb.AppendLine("  \"occurredUtc\": " + JsonString(fault.OccurredUtc.ToString("o", CultureInfo.InvariantCulture)) + ",");
            sb.AppendLine("  \"detail\": " + JsonString(fault.Detail) + ",");
            sb.AppendLine("  \"priorDetail\": " + JsonString(fault.PriorDetail ?? string.Empty) + ",");
            sb.AppendLine("  \"cleanup\": " + (cleanup ? "true" : "false"));
            sb.AppendLine("}");
            File.WriteAllText(pending, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(pending, path);
            if (cleanup)
            {
                string txt = Path.Combine(directory, CleanupErrorTxtName);
                File.WriteAllText(txt, "旧设备会话清理失败，禁止自动重开：" + (fault.Detail ?? ""), new UTF8Encoding(false));
            }
        }

        /// <summary>便捷：写到 <see cref="DefaultDirectory"/>。</summary>
        public static void WriteDefault(SessionFault fault, bool cleanup = false)
        {
            WriteJson(DefaultDirectory(), fault, cleanup);
        }

        /// <summary>是否存在 CleanupError 门闩（cleanup-error.txt 或 cleanup-fault.json）。存在则禁止 Open。</summary>
        public static bool HasCleanupErrorForbiddingReopen(string directory = null)
        {
            string dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (File.Exists(Path.Combine(dir, CleanupErrorTxtName))) return true;
                if (File.Exists(Path.Combine(dir, CleanupFileName))) return true;
            }
            catch { /* ignore IO */ }
            return false;
        }

        /// <summary>读 cleanup 门闩（优先 cleanup-fault.json，否则由 cleanup-error.txt 合成）。</summary>
        public static bool TryReadCleanupLatch(out SessionFault fault, string directory = null)
        {
            fault = null;
            string dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
            if (TryReadJson(Path.Combine(dir, CleanupFileName), out fault))
                return true;
            try
            {
                string txt = Path.Combine(dir, CleanupErrorTxtName);
                if (!File.Exists(txt)) return false;
                string detail = File.ReadAllText(txt, new UTF8Encoding(false));
                fault = Create("disk", SessionFaultCategory.CleanupError, "Cleanup", detail ?? string.Empty, "Latch");
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读最近一次非 cleanup 的 session-fault.json。</summary>
        public static bool TryReadSessionFault(out SessionFault fault, string directory = null)
        {
            fault = null;
            string dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
            return TryReadJson(Path.Combine(dir, SessionFileName), out fault);
        }

        /// <summary>优先 cleanup 门闩，其次 session-fault；供 UI/Host 摘要。</summary>
        public static bool TryReadAnyLatch(out SessionFault fault, out bool cleanupForbidsReopen, string directory = null)
        {
            cleanupForbidsReopen = HasCleanupErrorForbiddingReopen(directory);
            if (TryReadCleanupLatch(out fault, directory))
                return true;
            return TryReadSessionFault(out fault, directory);
        }

        /// <summary>单行摘要（无弹窗）；供 DesktopUi / OwtsServices 日志。</summary>
        public static string FormatLatchSummary(string directory = null)
        {
            SessionFault fault;
            bool forbid;
            if (!TryReadAnyLatch(out fault, out forbid, directory))
                return "session-fault门闩=无；cleanupFailed落盘=" + (forbid ? "有" : "无");
            string cat = fault.Category.ToString();
            string phase = fault.Phase ?? "";
            string detail = fault.Detail ?? "";
            if (detail.Length > 120) detail = detail.Substring(0, 117) + "...";
            return "session-fault门闩=" + cat
                + "；phase=" + phase
                + "；cleanup禁重开=" + (forbid || fault.Category == SessionFaultCategory.CleanupError ? "是" : "否")
                + "；detail=" + detail;
        }

        /// <summary>清除落盘清理门（Smoke / 操作员确认复位）。不自动调用。</summary>
        public static void ClearCleanupLatchFiles(string directory = null)
        {
            ClearLatchFilesForTests(directory);
        }

        public static void ClearLatchFilesForTests(string directory = null)
        {
            string dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory() : directory;
            try
            {
                if (!Directory.Exists(dir)) return;
                TryDelete(Path.Combine(dir, SessionFileName));
                TryDelete(Path.Combine(dir, CleanupFileName));
                TryDelete(Path.Combine(dir, CleanupErrorTxtName));
            }
            catch { /* ignore */ }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static bool TryReadJson(string path, out SessionFault fault)
        {
            fault = null;
            try
            {
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path, new UTF8Encoding(false));
                if (string.IsNullOrWhiteSpace(json)) return false;
                fault = new SessionFault
                {
                    SessionId = ExtractJsonString(json, "sessionId"),
                    Device = ExtractJsonString(json, "device"),
                    Phase = ExtractJsonString(json, "phase"),
                    Detail = ExtractJsonString(json, "detail"),
                    PriorDetail = ExtractJsonString(json, "priorDetail")
                };
                string cat = ExtractJsonString(json, "category");
                SessionFaultCategory parsed;
                fault.Category = Enum.TryParse(cat, true, out parsed) ? parsed : SessionFaultCategory.Unknown;
                string utc = ExtractJsonString(json, "occurredUtc");
                DateTime dt;
                if (DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                    fault.OccurredUtc = dt;
                return true;
            }
            catch
            {
                fault = null;
                return false;
            }
        }

        /// <summary>极简 JSON 字符串字段提取（本文件自写格式；无完整解析器依赖）。</summary>
        static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return string.Empty;
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return string.Empty;
            int colon = json.IndexOf(':', i + needle.Length);
            if (colon < 0) return string.Empty;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return string.Empty;
            var sb = new StringBuilder();
            for (int p = q1 + 1; p < json.Length; p++)
            {
                char c = json[p];
                if (c == '\\' && p + 1 < json.Length)
                {
                    char n = json[p + 1];
                    if (n == 'n') { sb.Append('\n'); p++; }
                    else if (n == 'r') { sb.Append('\r'); p++; }
                    else if (n == 't') { sb.Append('\t'); p++; }
                    else if (n == '"' || n == '\\') { sb.Append(n); p++; }
                    else if (n == 'u' && p + 5 < json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(p + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            p += 5;
                        }
                    }
                    else sb.Append(n);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string JsonString(string s)
        {
            if (s == null) s = string.Empty;
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') { sb.Append('\\'); sb.Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
