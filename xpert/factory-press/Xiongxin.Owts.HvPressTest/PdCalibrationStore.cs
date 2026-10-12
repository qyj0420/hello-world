using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 局放校准系数本地存取。公式与加压一致：Q_pc = peakV × pdCalibrationPcPerVolt
    /// ⇒ pdCalibrationPcPerVolt = injectedChargePc / peakV（峰值来自筛选+平均后的校准脉冲）。
    /// </summary>
    public sealed class PdCalibrationRecord
    {
        public double InjectedChargePc { get; set; }
        public double PeakVolts { get; set; }
        public double PdCalibrationPcPerVolt { get; set; }
        public int PicoResolutionBits { get; set; }
        public double PicoSampleIntervalNanoseconds { get; set; }
        public int PicoRangeIndex { get; set; }
        public string SavedUtc { get; set; }
        public string Notes { get; set; }

        public PdCalibrationRecord()
        {
            InjectedChargePc = 100.0;
            PeakVolts = double.NaN;
            PdCalibrationPcPerVolt = double.NaN;
            PicoResolutionBits = 14;
            PicoSampleIntervalNanoseconds = 8.0;
            PicoRangeIndex = -1;
            SavedUtc = string.Empty;
            Notes = string.Empty;
        }
    }

    public static class PdCalibrationStore
    {
        public const string FormulaId = "pdCalibrationPcPerVolt=injectedChargePc/peakV; Q_pc=peakV*pdCalibrationPcPerVolt";
        public const string FileName = "pd-calibration.json";
        /// <summary>无可调/无效落盘时加压默认系数（pC/V）。路径固定 {AppBase}/config/pd-calibration.json。</summary>
        public const double DefaultPdCalibrationPcPerVolt = 2000.0;
        /// <summary>常见校准器注入档（pC）。5000 pC = 5 nC（现场常用）；勿把 5 pC 当成 5 nC。</summary>
        public static readonly double[] CommonInjectedChargesPc =
        {
            5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000
        };
        /// <summary>无 UI 选择时的回落默认（pC）。GUI 下拉优先选 5000（5 nC）。</summary>
        public const double DefaultInjectedChargePc = 100.0;

        /// <summary>系数 = 注入电荷(pC) / 峰电压(V)。</summary>
        public static bool TryComputePcPerVolt(double injectedChargePc, double peakVolts, out double pcPerVolt, out string error)
        {
            pcPerVolt = double.NaN;
            error = null;
            if (!(injectedChargePc > 0) || double.IsNaN(injectedChargePc) || double.IsInfinity(injectedChargePc))
            {
                error = "注入放电量须为正有限值（pC）。";
                return false;
            }
            if (!(peakVolts > 1e-12) || double.IsNaN(peakVolts) || double.IsInfinity(peakVolts))
            {
                error = "校准脉冲峰电压无效或过小，无法计算系数。";
                return false;
            }
            pcPerVolt = injectedChargePc / peakVolts;
            if (!(pcPerVolt > 0) || double.IsNaN(pcPerVolt) || double.IsInfinity(pcPerVolt))
            {
                error = "计算出的校准系数非法。";
                return false;
            }
            return true;
        }

        public static string DefaultPath()
        {
            string dir = Path.Combine(AppContext.BaseDirectory ?? ".", "config");
            return Path.Combine(dir, FileName);
        }

        public static string Save(PdCalibrationRecord record, string path = null)
        {
            if (record == null) throw new ArgumentNullException("record");
            string full = string.IsNullOrWhiteSpace(path) ? DefaultPath() : Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (string.IsNullOrEmpty(record.SavedUtc))
                record.SavedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"formula\": \"" + FormulaId + "\",");
            sb.AppendLine("  \"injectedChargePc\": " + Num(record.InjectedChargePc) + ",");
            sb.AppendLine("  \"peakVolts\": " + Num(record.PeakVolts) + ",");
            sb.AppendLine("  \"pdCalibrationPcPerVolt\": " + Num(record.PdCalibrationPcPerVolt) + ",");
            sb.AppendLine("  \"picoResolutionBits\": " + record.PicoResolutionBits + ",");
            sb.AppendLine("  \"picoSampleIntervalNanoseconds\": " + Num(record.PicoSampleIntervalNanoseconds) + ",");
            sb.AppendLine("  \"picoRangeIndex\": " + record.PicoRangeIndex + ",");
            sb.AppendLine("  \"savedUtc\": \"" + Escape(record.SavedUtc) + "\",");
            sb.AppendLine("  \"notes\": \"" + Escape(record.Notes ?? "") + "\"");
            sb.AppendLine("}");
            File.WriteAllText(full, sb.ToString(), new UTF8Encoding(false));
            return full;
        }

        public static bool TryLoad(out PdCalibrationRecord record, out string pathUsed, string path = null)
        {
            record = null;
            pathUsed = string.IsNullOrWhiteSpace(path) ? DefaultPath() : Path.GetFullPath(path);
            if (!File.Exists(pathUsed)) return false;
            string json = File.ReadAllText(pathUsed, Encoding.UTF8);
            var r = new PdCalibrationRecord();
            r.InjectedChargePc = ExtractDouble(json, "injectedChargePc", r.InjectedChargePc);
            r.PeakVolts = ExtractDouble(json, "peakVolts", r.PeakVolts);
            r.PdCalibrationPcPerVolt = ExtractDouble(json, "pdCalibrationPcPerVolt", r.PdCalibrationPcPerVolt);
            r.PicoResolutionBits = (int)ExtractDouble(json, "picoResolutionBits", r.PicoResolutionBits);
            r.PicoSampleIntervalNanoseconds = ExtractDouble(json, "picoSampleIntervalNanoseconds", r.PicoSampleIntervalNanoseconds);
            r.PicoRangeIndex = (int)ExtractDouble(json, "picoRangeIndex", r.PicoRangeIndex);
            r.SavedUtc = ExtractString(json, "savedUtc") ?? r.SavedUtc;
            r.Notes = ExtractString(json, "notes") ?? r.Notes;
            if (!(r.PdCalibrationPcPerVolt > 0) || double.IsNaN(r.PdCalibrationPcPerVolt))
                return false;
            record = r;
            return true;
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("G17", CultureInfo.InvariantCulture);
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
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
            if (j + 4 <= json.Length && string.Compare(json, j, "null", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return fallback;
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
                }
                if (ch == '"') break;
                sb.Append(ch);
                q2++;
            }
            return sb.ToString();
        }
    }
}
