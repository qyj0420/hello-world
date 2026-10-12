using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.UnifiedSpectrum
{
    /// <summary>Cached, phase-aware ITEM.in/CABLE.in metadata; no dependency on an older control DLL.</summary>
    internal sealed class ProjectMetadata
    {
        private const long MaximumBytes = 2L * 1024 * 1024;
        private sealed class CableBlock
        {
            internal int Phase;
            internal bool Valid = true;
            internal readonly List<double> Positions = new List<double>();
            internal readonly List<string> Names = new List<string>();
            internal readonly List<string> Records = new List<string>();
        }

        private readonly List<CableBlock> blocks = new List<CableBlock>();
        private readonly CableBlock[] selected = new CableBlock[4];
        private double itemLength = double.NaN;
        public int ReadCount;
        public double RatedPhaseVoltageKv;
        public string Status = "Disabled", Details = "";

        /// <summary>The owning control calls only on a changed path or explicit reload.</summary>
        public void Load(string path)
        {
            ReadCount++;
            blocks.Clear(); Array.Clear(selected, 0, selected.Length);
            itemLength = double.NaN; RatedPhaseVoltageKv=0; Status = "Disabled"; Details = "";
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string root = Path.GetFullPath(path.Trim());
                if (File.Exists(root))
                {
                    if (!Path.GetFileName(root).Equals("ITEM.in", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("A project file path must identify ITEM.in.");
                    root = Path.GetDirectoryName(root);
                }
                if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Project directory does not exist.");
                string item = Path.Combine(root, "ITEM.in"), cable = Path.Combine(root, "CABLE.in");
                bool any = File.Exists(item) || File.Exists(cable), readError = false;
                if (File.Exists(item))
                {
                    try { var lines=ReadLines(item); RatedPhaseVoltageKv=ReadRatedPhaseVoltage(lines); itemLength = ReadItemLength(lines); }
                    catch (Exception ex) { if (!IsReadException(ex)) throw; readError = true; AddDetail("ITEM.in: " + ex.Message); }
                }
                if (File.Exists(cable))
                {
                    try { blocks.AddRange(ParseCable(ReadLines(cable))); }
                    catch (Exception ex) { if (!IsReadException(ex)) throw; readError = true; blocks.Clear(); AddDetail("CABLE.in: " + ex.Message); }
                }
                bool invalid = ResolveGeometry();
                Status = readError ? "MetadataReadError" : invalid ? "InvalidCableGeometry" : any ? "MetadataLoaded" : "MetadataUnavailable";
            }
            catch (Exception ex)
            {
                if (!IsReadException(ex)) throw;
                blocks.Clear(); Array.Clear(selected, 0, selected.Length); itemLength = double.NaN;
                Status = ex is ArgumentException || ex is DirectoryNotFoundException ? "InvalidProjectPath" : "MetadataReadError";
                Details = ex.Message;
            }
        }

        public double LengthForPhase(int phase)
        {
            if (phase < 0 || phase > 3) return double.NaN;
            CableBlock b = selected[phase];
            return b == null ? itemLength : b.Positions[b.Positions.Count - 1];
        }

        public double[] JointsForPhase(int phase)
        {
            if (phase < 0 || phase > 3 || selected[phase] == null) return new double[0];
            CableBlock b = selected[phase]; double length = b.Positions[b.Positions.Count - 1];
            // In CABLE.in a level-1 accessory between the cable endpoints is a
            // structural joint boundary. Its user-defined name (for example
            // J001) must not decide whether it is visible or used in analysis.
            return b.Positions.Where(p => p > 0 && p < length).Distinct().OrderBy(p => p).ToArray();
        }

        private bool ResolveGeometry()
        {
            bool invalid = false;
            var declared = new bool[4];
            var resolved = new CableBlock[4];
            for (int phase = 0; phase <= 3; phase++)
            {
                CableBlock[] same = blocks.Where(b => b.Phase == phase).ToArray();
                declared[phase] = same.Length > 0;
                if (same.Length == 0) continue;
                CableBlock first = same[0];
                bool valid = same.All(b => b.Valid && b.Positions.Count > 0 &&
                    b.Positions[b.Positions.Count - 1] > 0 && SameGeometry(first, b));
                if (!valid) { invalid = true; AddDetail("Invalid or conflicting cable blocks for " + PhaseName(phase) + "."); continue; }
                resolved[phase] = first;
                if (same.Length > 1) AddDetail("Identical " + PhaseName(phase) + " cable blocks collapsed: " + (same.Length - 1) + ".");
            }
            for (int phase = 1; phase <= 3; phase++)
                selected[phase] = declared[phase] ? resolved[phase] : resolved[0];
            if (declared[0]) selected[0] = resolved[0];
            else if (resolved[1] != null && resolved[2] != null && resolved[3] != null &&
                SameGeometry(resolved[1], resolved[2]) && SameGeometry(resolved[1], resolved[3]))
                selected[0] = resolved[1];
            else if (blocks.Count > 0) AddDetail("ALL joints unavailable: no explicit shared cable or identical A/B/C geometry.");
            return invalid;
        }

        private static bool SameGeometry(CableBlock a, CableBlock b)
        {
            return a.Positions.SequenceEqual(b.Positions) && a.Names.SequenceEqual(b.Names) && a.Records.SequenceEqual(b.Records);
        }
        private static string PhaseName(int phase) { return phase == 0 ? "ALL" : ((char)('A' + phase - 1)).ToString(); }
        private void AddDetail(string value) { Details += (Details.Length == 0 ? "" : "; ") + value; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool IsReadException(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is NotSupportedException || ex is System.Security.SecurityException;
        }

        private static string[] ReadLines(string path)
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked metadata files are not accepted.");
            if (info.Length > MaximumBytes) throw new IOException("Metadata file exceeds 2 MiB.");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength > MaximumBytes) throw new IOException("Metadata file exceeds 2 MiB.");
            string text;
            if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 254 && bytes[2] == 0 && bytes[3] == 0)
                text = new UTF32Encoding(false, true, true).GetString(bytes);
            else if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 254 && bytes[3] == 255)
                text = new UTF32Encoding(true, true, true).GetString(bytes);
            else if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254)
                text = new UnicodeEncoding(false, true, true).GetString(bytes);
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255)
                text = new UnicodeEncoding(true, true, true).GetString(bytes);
            else
            {
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { text = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            }
            var lines = new List<string>();
            using (var reader = new StringReader(text.TrimStart('\ufeff')))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (lines.Count >= 20000) throw new IOException("Metadata file exceeds 20000 lines.");
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        private static double ReadItemLength(string[] lines)
        {
            double length = double.NaN;
            foreach (string raw in lines)
            {
                int equals = raw.IndexOf('='); if (equals < 0) continue;
                string key = raw.Substring(0, equals).Trim().TrimEnd(':', '：').Trim();
                if (HVX.ProjectModel.ProjectMetadataKeys.Canonical(key) != "CableLength") continue;
                string value = raw.Substring(equals + 1).Trim().Trim('"').Trim();
                Match m = Regex.Match(value, @"^([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(?:m|米)?$", RegexOptions.IgnoreCase);
                double parsed;
                if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || !Finite(parsed) || parsed <= 0)
                    throw new IOException("Invalid ITEM.in 电缆长度 value.");
                if (Finite(length) && length != parsed) throw new IOException("Conflicting ITEM.in 电缆长度 values.");
                length = parsed;
            }
            return length;
        }

        private double ReadRatedPhaseVoltage(string[] lines)
        {
            double value=0;
            foreach(string raw in lines){
                int equals=raw.IndexOf('=');if(equals<0)continue;
                string key=raw.Substring(0,equals).Trim().TrimEnd(':','：').Trim();
                if(HVX.ProjectModel.ProjectMetadataKeys.Canonical(key)!="PhaseVoltage")continue;
                string text=raw.Substring(equals+1).Trim().Trim('"').Trim();
                var match=Regex.Match(text,@"^([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(kV|V|千伏|伏)?$",RegexOptions.IgnoreCase);
                double parsed;
                if(!match.Success||!double.TryParse(match.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out parsed)||!Finite(parsed)||parsed<=0){AddDetail("Invalid ITEM.in 相电压; U0 disabled.");return 0;}
                if(match.Groups[2].Value.Equals("V",StringComparison.OrdinalIgnoreCase)||match.Groups[2].Value=="伏")parsed/=1000;
                if(value>0&&Math.Abs(value-parsed)>1e-9){AddDetail("Conflicting ITEM.in 相电压; U0 disabled.");return 0;}
                value=parsed;
            }
            if(value==0)AddDetail("ITEM.in 相电压 unavailable; U0 disabled.");
            return value;
        }

        private static int ParsePhase(string text)
        {
            switch ((text ?? "").Trim().ToUpperInvariant())
            {
                case "A": case "A相": case "L1": return 1;
                case "B": case "B相": case "L2": return 2;
                case "C": case "C相": case "L3": return 3;
                case "ALL": case "ALL相": case "COMMON": case "全相": case "三相": case "共用": return 0;
                default: return -1;
            }
        }

        private static List<CableBlock> ParseCable(string[] lines)
        {
            var result = new List<CableBlock>(); CableBlock active = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim(); if (line.Length == 0) continue;
                Match record = Regex.Match(line, @"^([012])\s+(.*)$");
                if (record.Success && record.Groups[1].Value == "0")
                {
                    int phase = ParsePhase(record.Groups[2].Value);
                    active = phase < 0 ? null : new CableBlock { Phase = phase };
                    if (active != null)
                    {
                        if (result.Count >= 128) throw new IOException("CABLE.in exceeds 128 phase blocks.");
                        result.Add(active);
                    }
                    continue;
                }
                if (active == null) continue;
                active.Records.Add(Regex.Replace(line, @"\s+", " "));
                if (!record.Success || record.Groups[1].Value != "1") continue;
                string payload = record.Groups[2].Value;
                Match endpoint = Regex.Match(payload, @"([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(?:m|米)?\s*$", RegexOptions.IgnoreCase);
                double position;
                if (!endpoint.Success || !double.TryParse(endpoint.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out position) || !Finite(position) || position < 0)
                { active.Valid = false; continue; }
                if (active.Positions.Count >= 4096) throw new IOException("A cable block exceeds 4096 accessory endpoints.");
                if (active.Positions.Count > 0 && position <= active.Positions[active.Positions.Count - 1]) active.Valid = false;
                string label = payload.Substring(0, endpoint.Index).Trim();
                string[] cells = Regex.Split(label, @"\s+");
                string name = cells.Length > 1 ? string.Join(" ", cells.Take(cells.Length - 1)) : label;
                active.Positions.Add(position); active.Names.Add(name);
            }
            return result;
        }
    }
}
