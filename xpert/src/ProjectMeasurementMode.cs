using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace HVX.ProjectModel
{
    // Persisted field names are a language-neutral protocol, not translated UI.
    // Keep the legacy Chinese spellings on read, but write one English key.
    internal static class ProjectMetadataKeys
    {
        static readonly Dictionary<string, string> Aliases = BuildAliases();
        internal static string Canonical(string key)
        {
            string trimmed = (key ?? String.Empty).Trim().Trim('\uFEFF').TrimEnd(':', '：').Trim();
            string value;
            return Aliases.TryGetValue(MatchKey(trimmed), out value) ? value : trimmed;
        }
        internal static bool IsKnown(string key)
        {
            return Aliases.ContainsKey(MatchKey((key ?? String.Empty).Trim().Trim('\uFEFF').TrimEnd(':', '：').Trim()));
        }
        static string MatchKey(string key)
        {
            var text = new StringBuilder();
            foreach (char c in key) if (!Char.IsWhiteSpace(c) && c != '_') text.Append(c);
            return text.ToString();
        }
        static Dictionary<string, string> BuildAliases()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string field in new[] {
                "TestingOrganization|所属单位|检测单位|试验单位|测试单位",
                "TestPersonnel|测试人员|试验人员|检测人员", "TestLocation|测试地点|试验地点|检测地点",
                "Date|日期", "Time|时间", "StartName|始端地点|始端名称|始端|起点地点",
                "EndName|终端地点|终端名称|终端|终点地点|末端名称",
                "StartType|始端开关类型|始端类型", "EndType|终端开关类型|终端类型|末端类型",
                "CableLength|电缆长度|电缆长度(m)|电缆长度[m]", "CableLengthA|A相电缆长度",
                "CableLengthB|B相电缆长度", "CableLengthC|C相电缆长度",
                "InstallationDate|铺设时间|敷设时间|敷设日期|铺设日期",
                "PhaseVoltage|相电压|相电压(kV)|相电压[kV]",
                "CoreCount|电缆芯数|芯数|负载类型|Load type|LoadType|Cable mode",
                "Remarks|备注", "A1|A相绝缘电阻1|A相测试前绝缘电阻|A相试验前绝缘电阻|A相绝缘1|A相绝缘[MΩ]1|A相绝缘电阻[MΩ]1",
                "A2|A相绝缘电阻2|A相测试后绝缘电阻|A相试验后绝缘电阻|A相绝缘2|A相绝缘[MΩ]2|A相绝缘电阻[MΩ]2",
                "B1|B相绝缘电阻1|B相测试前绝缘电阻|B相试验前绝缘电阻|B相绝缘1|B相绝缘[MΩ]1|B相绝缘电阻[MΩ]1",
                "B2|B相绝缘电阻2|B相测试后绝缘电阻|B相试验后绝缘电阻|B相绝缘2|B相绝缘[MΩ]2|B相绝缘电阻[MΩ]2",
                "C1|C相绝缘电阻1|C相测试前绝缘电阻|C相试验前绝缘电阻|C相绝缘1|C相绝缘[MΩ]1|C相绝缘电阻[MΩ]1",
                "C2|C相绝缘电阻2|C相测试后绝缘电阻|C相试验后绝缘电阻|C相绝缘2|C相绝缘[MΩ]2|C相绝缘电阻[MΩ]2",
                "InsulationReferenceProfile|绝缘参考标准", "InsulationConditionsComparable|绝缘测量条件一致",
                "LineName|线路名称", "CableModel|电缆型号", "EndDispatch|调度号2|调度号末|终端调度号|末端调度号",
                "StartDispatch|调度号1|调度号始|始端调度号", "Temperature|温度", "Weather|天气",
                "WeatherCode|天气代码", "WeatherProvider|天气来源", "WeatherUpdatedAt|天气更新时间", "Humidity|湿度",
                "Latitude|北斗纬度", "Longitude|北斗经度", "Altitude|北斗海拔", "GnssFixStatus|北斗定位状态",
                "HardwareSampleTime|硬件采样时间", "EnvironmentSampleValid|温湿度数据有效",
                "EnvironmentSampleTime|温湿度采样时间", "GnssFixValid|北斗数据有效", "GnssAltitudeValid|北斗海拔有效",
                "GnssSampleTime|北斗UTC时间", "DualEnded|双端", "TestMode|加压模式",
                "PressureMultipliers|加压倍数", "PressureRepetitions|加压次数", "ProjectEditorVersion|项目建立控件版本",
                "AdoptedVelocityALL|ALL相采用波速", "AdoptedVelocityA|A相采用波速",
                "AdoptedVelocityB|B相采用波速", "AdoptedVelocityC|C相采用波速"
            }) {
                string[] names = field.Split('|');
                foreach (string name in names) result[MatchKey(name)] = names[0];
            }
            return result;
        }
    }

    // Only an explicit project load type selects capacitor semantics. A zero
    // cable length, missing reflection or capacitance reading never does so.
    public static class ProjectMeasurementMode
    {
        public static bool IsCapacitor(string projectRoot)
        {
            if (String.IsNullOrWhiteSpace(projectRoot)) return false;
            string path = Path.Combine(Path.GetFullPath(projectRoot), "ITEM.in");
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length > 2 * 1024 * 1024) throw new InvalidDataException("项目元数据文件超过 2 MB。");
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("项目类型文件不能是链接。");
            byte[] bytes = File.ReadAllBytes(path); string content;
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) content = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) content = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) content = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            else { try { content = new UTF8Encoding(false, true).GetString(bytes); } catch (DecoderFallbackException) { content = Encoding.GetEncoding(936).GetString(bytes); } }
            bool capacitor = false, cable = false;
            foreach (string raw in content.Replace("\r", "").Split('\n'))
            {
                int eq = raw.IndexOf('='); if (eq < 0) continue;
                string key = ProjectMetadataKeys.Canonical(raw.Substring(0, eq));
                if (!key.Equals("CoreCount", StringComparison.OrdinalIgnoreCase)) continue;
                string value = raw.Substring(eq + 1).Trim().Trim('"').Trim();
                if (value == "电容局放" || value == "电容" || value == "电容器" || value.Equals("Capacitor PD", StringComparison.OrdinalIgnoreCase) || value.Equals("Capacitor", StringComparison.OrdinalIgnoreCase)) capacitor = true;
                else if (value == "三芯" || value == "一体三芯" || value == "单芯" || value == "三相单芯" || value == "单相" || value == "单相单芯" || value == "三相单芯电缆" ||
                    value == "一体三芯电缆" || value == "单芯三相电缆" || value == "单芯电缆" ||
                    value == "一體三芯電纜" || value == "單芯三相電纜" || value == "單芯電纜" ||
                    value.Equals("Three-core", StringComparison.OrdinalIgnoreCase) || value.Equals("Integrated three-core", StringComparison.OrdinalIgnoreCase) || value.Equals("Integrated three-core cable", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Single-phase", StringComparison.OrdinalIgnoreCase) || value.Equals("Single-phase single-core", StringComparison.OrdinalIgnoreCase) || value.Equals("Single-core cable", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("Three-phase single-core", StringComparison.OrdinalIgnoreCase) || value.Equals("Three-phase single-core cable", StringComparison.OrdinalIgnoreCase)) cable = true;
            }
            if (capacitor && cable) throw new InvalidDataException("项目负载类型互相矛盾，请核对电容/电缆类型。");
            return capacitor;
        }
    }
}
