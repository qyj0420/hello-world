using System;
using System.Collections.Generic;

namespace Xiongxin.OWTS.Localization
{
    // Only immutable program-owned captions enter this catalog. No substring
    // translation of project data, user conclusions, paths or protocol values.
    internal static partial class InternationalText
    {
        static readonly Dictionary<string, Dictionary<string, string>> Packs;
        static readonly Dictionary<string, string> Chinese;
        static InternationalText() { Packs = BuildPacks(); Chinese = BuildChinese(); }
        static readonly HashSet<string> InvariantTerms = new HashSet<string>(StringComparer.Ordinal)
        {
            "PD","PDIV","PDEV","PRPD","PRPS","HV","MV","LV","DAC","VLF","HFCT","UHF","VHF","TEV",
            "TDR","RMS","AC","DC","XLPE","PE","PVC","EPR","DFR","FDS","PDC","CT","VT",
            "pC","nC","kV","μs","μF","nF","pF","MΩ","GΩ","Hz","kHz","MHz","MS/s","tan δ","U0"
        };
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Pressure test", "Voltage test" }, { "Pressure testing", "Voltage test" },
            { "Project Creation", "Create project" }, { "Report generator", "Report generation" },
            { "Sample rate", "Sampling rate" }, { "Withstand voltage test", "Withstand test" },
            { "PDIV", "PDIV" }, { "PDEV", "PDEV" }, { "Damped alternating current", "Damped AC" },
            { "High-voltage test", "Voltage test" }, { "Parameter settings", "Settings" },
            { "Open project", "Load project" }, { "Browse data", "Data browser" },
            { "Data browsing", "Data browser" }, { "Report Generation", "Report generation" },
            { "Show manual controls", "Manual controls" }, { "Propagation velocity", "Wave velocity" },
            { "Review phase\nvelocity", "Review phase velocity" }
        };
        static Dictionary<string, Dictionary<string, string>> BuildPacks()
        {
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string packed in CoreRows)
            {
                string[] row = packed.Split('|');
                if (row.Length != CoreRows[0].Split('|').Length) throw new InvalidOperationException("Invalid translation row: " + row[0]);
                string[] keys = CoreRows[0].Split('|');
                var pack = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < row.Length; i++) pack.Add(keys[i], row[i]);
                result.Add(row[0], pack);
            }
            foreach (string packed in ExtraRows)
            {
                string[] row = packed.Split('|');
                if (row.Length != 7) throw new InvalidOperationException("Invalid extended caption: " + row[0]);
                string[] codes = { "en", "zh-CN", "pt-BR", "ja", "sv", "nl" };
                for (int i = 0; i < codes.Length; i++) result[codes[i]][row[0]] = row[i + 1];
            }
            string[] calibrationKeys=CalibrationRows[0].Split('|');
            for(int rowIndex=1;rowIndex<CalibrationRows.Length;rowIndex++)
            {
                string[] row=CalibrationRows[rowIndex].Split('|');Dictionary<string,string> pack;
                if(row.Length!=calibrationKeys.Length)throw new InvalidOperationException("Invalid calibration caption row: "+row[0]);
                if(!result.TryGetValue(row[0],out pack)){pack=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);result.Add(row[0],pack);}
                for(int i=1;i<row.Length;i++)pack[calibrationKeys[i]]=row[i];
            }
            foreach(string[] rows in new[]{CalibrationSafetyRows,CalibrationFlowRows,CalibrationShortCableRows})
            {
                string[] codes=rows[0].Split('|');
                for(int rowIndex=1;rowIndex<rows.Length;rowIndex++) {
                    string[] row=rows[rowIndex].Split('|');
                    if(row.Length!=codes.Length)throw new InvalidOperationException("Invalid calibration feedback caption: "+row[0]);
                    for(int i=1;i<row.Length;i++) {
                        Dictionary<string,string> pack;
                        if(!result.TryGetValue(codes[i],out pack)) {
                            pack=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);result.Add(codes[i],pack);
                        }
                        pack[row[0]]=row[i];
                    }
                }
            }
            return result;
        }
        static Dictionary<string, string> BuildChinese()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in Packs["zh-CN"]) result[item.Value] = item.Key;
            result["参数设置"] = "Settings"; result["项目建立"] = "Create project";
            // The Chinese caption for an Off state can also be 关闭. A bare
            // program-owned command caption uses Close, never the state Off.
            result["关闭"] = "Close"; result["确定"] = "Confirm";
            result["导出图片"] = "Export image"; result["局放"] = "Partial discharge";
            result["局放起始电压"] = "PD inception voltage"; result["局放熄灭电压"] = "PD extinction voltage";
            result["相别"]="Phase";result["传播波速"]="Wave velocity";
            result["采集自检验"]="Acquisition self-check";
            result["内部参数适配中..."]="Checking input settings…";
            return result;
        }
        internal static string Canonical(string text)
        {
            if (String.IsNullOrEmpty(text)) return text;
            string alias;
            return Aliases.TryGetValue(text, out alias) ? alias : text;
        }
        internal static string English(string text)
        { string english; return text != null && Chinese.TryGetValue(text, out english) ? english : text; }

        static string PackCode(string code)
        {
            switch (code)
            {
                case "en-GB": return "en";
                case "es-419": return "es";
                case "de-CH": return "de";
                case "fr-CH": return "fr";
                case "it-CH": return "it";
                case "pt-PT": return "pt-BR";
                default: return code;
            }
        }
        internal static bool TryGet(string code, string text, out string translated)
        {
            translated = null;
            if (String.IsNullOrEmpty(text)) return false;
            if (InvariantTerms.Contains(text)) { translated = text; return true; }
            Dictionary<string, string> pack;
            if (!Packs.TryGetValue(PackCode(code), out pack)) return false;
            string key = Canonical(text.Trim());
            string value;
            if (pack.TryGetValue(key, out value))
            {
                if (code == "pt-PT")
                {
                    if (key == "Save") value = "Guardar";
                    else if (key == "Delete") value = "Eliminar";
                    else if (key == "Project management") value = "Gestão de projetos";
                    else if (key == "Settings") value = "Definições";
                    else if (key == "Condition monitoring") value = "Monitorização da condição";
                    else if (key == "Cable insulation assessment") value = "Avaliação do isolamento do cabo";
                }
                translated = text.Substring(0, text.Length - text.TrimStart().Length) + value + text.Substring(text.TrimEnd().Length);
                return true;
            }
            // Known labels with explicit unit delimiters only. Preserve units,
            // symbols, formatting arguments and numeric values verbatim.
            foreach (string separator in new[] { " / ", " (", ": " })
            {
                int p = text.IndexOf(separator, StringComparison.Ordinal);
                if (p > 0 && pack.TryGetValue(Canonical(text.Substring(0, p)), out value))
                { translated = value + text.Substring(p); return true; }
            }
            return false;
        }
    }
}
