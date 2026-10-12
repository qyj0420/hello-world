using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 局放校准系数本地存取（与 HvPressTest/PdCalibrationStore 同 JSON schema）。
    /// 路径默认 {AppBase}/config/pd-calibration.json；无效/缺失 → DefaultPdCalibrationPcPerVolt=2000。
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
        public const double DefaultPdCalibrationPcPerVolt = 2000.0;

        public static string DefaultPath()
        {
            string dir = Path.Combine(AppContext.BaseDirectory ?? ".", "config");
            return Path.Combine(dir, FileName);
        }

        /// <summary>读取落盘系数；失败或不存在返回 false（调用方回落默认 2000）。</summary>
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
            if (!(r.PdCalibrationPcPerVolt > 0) || double.IsNaN(r.PdCalibrationPcPerVolt) || double.IsInfinity(r.PdCalibrationPcPerVolt))
                return false;
            record = r;
            return true;
        }

        /// <summary>加载有效系数，否则 DefaultPdCalibrationPcPerVolt。</summary>
        public static double LoadPcPerVoltOrDefault(out string pathUsed, out bool fromFile)
        {
            PdCalibrationRecord rec;
            if (TryLoad(out rec, out pathUsed) && rec != null)
            {
                fromFile = true;
                return rec.PdCalibrationPcPerVolt;
            }
            pathUsed = DefaultPath();
            fromFile = false;
            return DefaultPdCalibrationPcPerVolt;
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
                char c = json[q2];
                if (c == '\\' && q2 + 1 < json.Length) { sb.Append(json[q2 + 1]); q2 += 2; continue; }
                if (c == '"') break;
                sb.Append(c);
                q2++;
            }
            return sb.ToString();
        }
    }
}
