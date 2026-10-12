// Internal adaptation of tools/task24_project_selector_audit_20260907/src/ProjectCalibrationSelector.cs (audited Task24 2.0.24.0).
// Original CAL validation, charge/phase selection priorities, encoding handling, and provenance are retained.
// PDmap adapter removes LOC discovery; a bounded catalog is cached by the control until an explicit reload.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;


namespace LabVIEWPdMap.ProjectCalibrationImplementation
{
    internal sealed class SelectionFailure : Exception
    {
        internal readonly string Code;
        internal SelectionFailure(string code, string message) : base(message) { Code = code; }
    }
    internal sealed class Candidate
    {
        internal string Path, Phase = "", Reason = "InvalidFileName", AttenuationSource = "", TimeSource = "";
        internal bool Valid;
        // Archive waveforms remain in their saved voltage units. Timing mode and
        // capture range are provenance, not inferred from a current LOC row.
        internal bool IsLiveArchive, HasMeasuredReflection = true, FirstArrival;
        internal int RangeIndex = -1, IncidentPeak = -1, ReflectionPeak = -1;
        internal double Value = double.NaN, Length = double.NaN, Velocity = double.NaN, Coefficient = double.NaN,
            Rate = double.NaN, Quality = double.NaN, Loss = double.NaN, HeaderLoss = double.NaN;
        internal int Incident, Reflection;
        internal DateTime Time;
        internal double[] Wave;
    }
    internal static class Selector
    {
        internal static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        private static bool Positive(double x) { return Finite(x) && x > 0; }
        internal static object Number(double x) { return Finite(x) ? (object)x : null; }
        private static void Require(bool condition, string code, string message)
        { if (!condition) throw new SelectionFailure(code, message); }
        private static string Phase(string text)
        {
            switch ((text ?? "").Trim().ToUpperInvariant())
            {
                case "A": case "A相": case "L1": return "A";
                case "B": case "B相": case "L2": return "B";
                case "C": case "C相": case "L3": return "C";
                case "ALL": case "ALL相": case "COMMON": case "全相": case "三相": case "共用": return "ALL";
                default: return "";
            }
        }
        private static string[] ReadLines(string path)
        {
            Require(new FileInfo(path).Length <= 32L * 1024 * 1024, "CalibrationTooLarge", "CAL exceeds the 32 MiB per-file limit.");
            byte[] b = File.ReadAllBytes(path); string text;
            if (b.Length >= 2 && b[0] == 255 && b[1] == 254) text = new UnicodeEncoding(false, true, true).GetString(b);
            else if (b.Length >= 2 && b[0] == 254 && b[1] == 255) text = new UnicodeEncoding(true, true, true).GetString(b);
            else
            {
                try { text = new UTF8Encoding(false, true).GetString(b); }
                catch (DecoderFallbackException) { text = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(b); }
            }
            var lines = new List<string>();
            using (var reader = new StringReader(text.TrimStart('\ufeff')))
            { string line; while ((line = reader.ReadLine()) != null) lines.Add(line); }
            return lines.ToArray();
        }
        private static string Key(string x) { return Regex.Replace(x.Trim().TrimEnd(':', '：').ToLowerInvariant(), @"\s+", " "); }
        private static Dictionary<string, string> Header(IEnumerable<string> lines)
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                int tab = line.IndexOf('\t'), colon = line.IndexOfAny(new[] { ':', '：' });
                int i = tab >= 0 ? tab : colon;
                if (i < 0) continue;
                string k = Key(line.Substring(0, i)), v = line.Substring(i + 1).Trim();
                string existing;
                Require(!h.TryGetValue(k, out existing) || existing == v, "ConflictingHeader", "Conflicting duplicate field: " + k);
                h[k] = v;
            }
            return h;
        }
        private static string FieldKey(Dictionary<string, string> h, string prefix)
        {
            string p = Key(prefix);
            string[] keys = h.Keys.Where(k => k == p || (k.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                k.Length > p.Length && " （([".IndexOf(k[p.Length]) >= 0)).ToArray();
            Require(keys.Length <= 1, "AmbiguousHeader", "Ambiguous field: " + prefix);
            return keys.Length == 0 ? null : keys[0];
        }
        private static double Parse(string value)
        {
            double x; return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : double.NaN;
        }
        private static double Get(Dictionary<string, string> h, string name)
        { string k = FieldKey(h, name); return k == null ? double.NaN : Parse(h[k]); }
        private static double Alias(Dictionary<string, string> h, string primary, string fallback)
        { return FieldKey(h, primary) != null ? Get(h, primary) : Get(h, fallback); }
        private static double Rate(double x)
        { return !Positive(x) ? double.NaN : x < .001 ? 1 / x : x < 1000 ? 1e9 / x : x; }
        private static double TimeSeconds(Dictionary<string, string> h, string name)
        {
            string k = FieldKey(h, name); if (k == null) return double.NaN;
            double value = Parse(h[k]);
            if (Regex.IsMatch(k, @"(?:usec|μs|µs|\bus\b)")) return value * 1e-6;
            if (Regex.IsMatch(k, @"[（(]\s*s\s*[)）]")) return value;
            return value > 1e-3 ? value * 1e-6 : value;
        }
        private static int Index(double seconds, double fs, int count)
        {
            double exact = seconds * fs;
            Require(Finite(exact) && exact >= 0 && exact < count, "TimingOutOfBounds", "CAL timing is outside its waveform.");
            double rounded = Math.Round(exact, MidpointRounding.ToEven);
            Require(rounded >= 0 && rounded < count, "TimingOutOfBounds", "Rounded CAL timing is outside its waveform.");
            return (int)rounded;
        }
        private static double Median(double[] values)
        {
            if (values.Length == 0) return 0;
            double[] x = (double[])values.Clone(); Array.Sort(x); int n = x.Length;
            return n % 2 != 0 ? x[n / 2] : x[n / 2 - 1] / 2 + x[n / 2] / 2;
        }
        private static int PeakIndex(double[] wave, int start, int end, double baseline)
        {
            int best = start;
            for (int i = start + 1; i <= end; i++) if (Math.Abs(wave[i] - baseline) > Math.Abs(wave[best] - baseline)) best = i;
            return best;
        }
        private static double WindowPeak(double[] wave, int index, int radius)
        {
            return Math.Abs(wave[PeakIndex(wave, Math.Max(0, index - radius), Math.Min(wave.Length - 1, index + radius), 0)]);
        }
        private static double Clamp01(double x) { return Math.Max(0, Math.Min(1, x)); }
        private static double Quality(Candidate c)
        {
            int radius = (int)Math.Min(c.Wave.Length, Math.Max(8, Math.Round(c.Rate * 1.5e-7)));
            double direct = WindowPeak(c.Wave, c.Incident, radius), reflected = c.HasMeasuredReflection ? WindowPeak(c.Wave, c.Reflection, radius) : double.NaN;
            double[] quiet = c.Wave.Where((v, i) => Math.Abs((long)i - c.Incident) > (long)radius * 2 && Math.Abs((long)i - c.Reflection) > (long)radius * 2).ToArray();
            double median = quiet.Length < 16 ? 0 : Median(quiet);
            double noise = quiet.Length < 16 ? 0 : 1.4826 * Median(quiet.Select(v => Math.Abs(v - median)).ToArray());
            double lossPercent = direct > 0 ? 100 * (1 - reflected / direct) : double.NaN;
            double snr1 = noise > 0 ? direct / noise : 0, snr2 = noise > 0 ? reflected / noise : 0;
            if (!c.HasMeasuredReflection) return Math.Round(20 + 25 * Clamp01((snr1 - 3) / 17), 1);
            return Math.Round(20 + 25 * Clamp01((snr1 - 3) / 17) + 25 * Clamp01((snr2 - 3) / 17) +
                15 * (Finite(lossPercent) && lossPercent > -20 && lossPercent < 99 ? 1 : 0) +
                15 * (c.Reflection > c.Incident + 5 ? 1 : 0), 1);
        }
        private static void Attenuation(Candidate c)
        {
            double center = Median(c.Wave); int radius = Math.Max(2, Math.Min(64, (c.Reflection - c.Incident) / 8));
            int a = PeakIndex(c.Wave, Math.Max(0, c.Incident - radius), Math.Min(c.Reflection - 1, c.Incident + radius), center);
            int b = PeakIndex(c.Wave, Math.Max(a + 1, c.Reflection - radius), Math.Min(c.Wave.Length - 1, c.Reflection + radius), center);
            double va = Math.Abs(c.Wave[a] - center), vb = Math.Abs(c.Wave[b] - center);
            Require(Finite(va) && Finite(vb) && va > 1e-12 && vb > 1e-12, "MissingCalibrationPulses", "Both CAL pulse neighborhoods need a nonzero baseline-relative amplitude.");
            // Difference of logs is equivalent to Task17's ratio, without intermediate overflow.
            double loss = 20 * (Math.Log10(va) - Math.Log10(vb));
            if (Finite(loss) && loss >= -60 && loss <= 120) { c.Loss = loss; c.AttenuationSource = "calibration_waveform_peak_ratio_db"; }
            else if (Finite(c.HeaderLoss) && c.HeaderLoss >= -60 && c.HeaderLoss <= 120)
            { c.Loss = c.HeaderLoss; c.AttenuationSource = "bounded_header_fallback_db"; }
            else throw new SelectionFailure("InvalidAttenuation", "Neither waveform nor bounded CAL header yields a valid dB reference.");
        }
        private static DateTime Stamp(Dictionary<string, string> h, string path, out string source)
        {
            string dateKey = FieldKey(h, "Date"), timeKey = FieldKey(h, "Time");
            string stamp = ((dateKey == null ? "" : h[dateKey]) + " " + (timeKey == null ? "" : h[timeKey])).Replace('：', ':').Trim();
            DateTime t;
            if (dateKey != null && (DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out t) ||
                DateTime.TryParse(stamp, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AllowWhiteSpaces, out t)))
            {
                // OWTS project timestamps are China local time; make ordering independent of host timezone.
                source = "cal_header_Asia_Shanghai";
                return DateTime.SpecifyKind(t, DateTimeKind.Unspecified).AddHours(-8);
            }
            source = "file_last_write_utc"; return File.GetLastWriteTimeUtc(path);
        }
        private static void ReadLiveCandidate(string root, string path, Candidate c, double named)
        {
            Require(new FileInfo(path).Length <= 96L * 1024 * 1024, "CalibrationTooLarge", "Live CAL exceeds the archive's 96 MiB per-file limit.");
            // Reuse the archive's checksum, project scope, waveform and timing
            // validation. Do not deserialize untrusted metadata separately or
            // turn TDR into an electrical calibration with an invented factor.
            var record = HVX.CalibrationStorage.LiveCalibrationRecord.Load(root, path, true);
            Require(!record.IsTdr && !record.IsCapacitor, "NotCableChargeCalibration", "TDR or capacitor records are not cable charge references.");
            Require(c.Phase == Phase(record.Phase) &&
                System.IO.Path.GetFileNameWithoutExtension(path).Equals(record.Phase + "_" + record.Level, StringComparison.OrdinalIgnoreCase),
                "NameArchiveIdentityMismatch", "Archive phase/level must agree with its file name.");
            Require(Positive(named) && Positive(record.ChargePc) && Math.Abs(named - record.ChargePc) <= Math.Max(.01, Math.Max(named, record.ChargePc) * .001),
                "NameHeaderChargeMismatch", "Positive filename/archive charges must agree within max(0.01 pC, 0.1%).");
            c.IsLiveArchive = true; c.Value = record.ChargePc; c.Length = record.LengthM;
            c.Velocity = record.VelocityMPerUs; c.Coefficient = record.CoefficientPcPerV;
            c.Rate = record.SampleRateHz; c.Wave = record.Average; c.RangeIndex = record.RangeIndex;
            c.FirstArrival = record.FirstArrival; c.Incident = Index(record.IncidentSeconds, c.Rate, c.Wave.Length);
            c.IncidentPeak = record.IncidentPeakIndex; c.HasMeasuredReflection = record.HasMeasuredReflection;
            c.Reflection = c.HasMeasuredReflection ? Index(record.ReflectionSeconds, c.Rate, c.Wave.Length) : -1;
            c.ReflectionPeak = c.HasMeasuredReflection ? record.ReflectionPeakIndex : -1;
            c.HeaderLoss = record.AttenuationDb;
            if (c.HasMeasuredReflection) Attenuation(c);
            else c.AttenuationSource = "unavailable_no_measured_reflection";
            c.Quality = Quality(c);
            DateTime created;
            if (DateTime.TryParse(record.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out created))
            { c.Time = created.ToUniversalTime(); c.TimeSource = "archive_created_utc"; }
            else { c.Time = File.GetLastWriteTimeUtc(path); c.TimeSource = "file_last_write_utc"; }
            c.Valid = true; c.Reason = "Valid";
        }
        private static Candidate ReadCandidate(string root, string path)
        {
            var c = new Candidate { Path = Path.GetFullPath(path) };
            try
            {
                Match m = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(path), @"^(A|B|C|L1|L2|L3|ALL)_([0-9]+(?:\.[0-9]+)?)(PC|NC)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Require(m.Success, "InvalidFileName", "Expected Phase_ValuepC.cal or Phase_ValuenC.cal.");
                c.Phase = Phase(m.Groups[1].Value);
                Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "LinkedCalibrationNotAllowed", "CAL must be a direct project file.");
                double named = Parse(m.Groups[2].Value) * (m.Groups[3].Value.Equals("NC", StringComparison.OrdinalIgnoreCase) ? 1000 : 1);
                if (HVX.CalibrationStorage.LiveCalibrationRecord.IsArchiveFile(path))
                { ReadLiveCandidate(root, path, c, named); return c; }
                string[] lines = ReadLines(path);
                Require(lines.Length >= 45, "DataTruncated", "CAL needs 13 header lines and at least 32 samples.");
                var h = Header(lines.Take(13));
                c.Value = Get(h, "Cal.value");
                Require(Positive(named) && Positive(c.Value) && Math.Abs(c.Value - named) <= Math.Max(.01, Math.Max(c.Value, named) * .001),
                    "NameHeaderChargeMismatch", "Positive filename/header charges must agree within max(0.01 pC, 0.1%).");
                c.Length = Get(h, "Cable length"); c.Velocity = Alias(h, "V", "Velocity");
                c.Coefficient = Alias(h, "NI_Calib_factor", "Calib_factor"); c.Rate = Rate(Get(h, "SR"));
                c.HeaderLoss = Get(h, "Calib_Attenuation");
                Require(Positive(c.Length) && Positive(c.Coefficient) && Positive(c.Rate) && Finite(c.Velocity) && c.Velocity >= 80 && c.Velocity <= 250,
                    "InvalidCalibrationHeader", "CAL length, coefficient, sample rate or velocity is invalid.");
                int last = lines.Length - 1; while (last >= 13 && string.IsNullOrWhiteSpace(lines[last])) last--;
                var wave = new List<double>();
                for (int i = 13; i <= last; i++)
                {
                    string[] cells = lines[i].Split('\t'); double sample = Parse(cells[0].Trim());
                    Require(Finite(sample) && cells.Skip(1).All(string.IsNullOrWhiteSpace), "InvalidWaveformSample", "Invalid/blank sample or unsupported additional data columns at line " + (i + 1));
                    wave.Add(sample); // Never delete an interior sample and shift timing indices.
                }
                Require(wave.Count >= 32, "DataTruncated", "CAL has fewer than 32 samples."); c.Wave = wave.ToArray();
                c.Incident = Index(TimeSeconds(h, "Start_point"), c.Rate, c.Wave.Length);
                c.Reflection = Index(TimeSeconds(h, "Final_point"), c.Rate, c.Wave.Length);
                Require(c.Reflection > c.Incident, "InvalidPulseOrder", "Reflection index must follow incident index.");
                Attenuation(c); c.Quality = Quality(c);
                // Legacy quality is only a deterministic catalog rank. Raw
                // identifiability is decided by the shared guarded reference;
                // a small reflection ratio must not hide observable CAL data.
                c.Time = Stamp(h, path, out c.TimeSource); c.Valid = true; c.Reason = "Valid";
            }
            catch (SelectionFailure e) { c.Reason = e.Code; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException || e is InvalidOperationException || e is System.Xml.XmlException)
            { c.Reason = "CalibrationReadError: " + e.GetType().Name; }
            return c;
        }

        // Adapted for the PDmap matrix contract: physical phase and length are supplied
        // directly from the current positioning row. No LOC identity is invented.
        internal static List<Candidate> ReadCatalog(string projectPath, out string normalizedRoot)
        {
            Require(!string.IsNullOrWhiteSpace(projectPath), "InvalidProjectPath", "ProjectPath is required.");
            string root = System.IO.Path.GetFullPath(projectPath.Trim());
            if (File.Exists(root))
            {
                Require(System.IO.Path.GetFileName(root).Equals("ITEM.in", StringComparison.OrdinalIgnoreCase),
                    "InvalidProjectPath", "A file ProjectPath must identify ITEM.in.");
                root = System.IO.Path.GetDirectoryName(root);
            }
            Require(Directory.Exists(root), "InvalidProjectPath", "Project directory does not exist.");
            normalizedRoot = root;
            string[] rootFiles = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                .Where(f => System.IO.Path.GetExtension(f).Equals(".cal", StringComparison.OrdinalIgnoreCase))
                .Take(257).ToArray();
            var rootNames = new HashSet<string>(rootFiles.Select(System.IO.Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            string oldFolder = System.IO.Path.Combine(root, "Calibration", "Live");
            IEnumerable<string> oldFiles = Directory.Exists(oldFolder) ? Directory.EnumerateFiles(oldFolder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => System.IO.Path.GetExtension(f).Equals(".hvcal", StringComparison.OrdinalIgnoreCase) && !rootNames.Contains(System.IO.Path.GetFileNameWithoutExtension(f))) : Enumerable.Empty<string>();
            string[] files = rootFiles.Concat(oldFiles).Take(257).ToArray();
            Require(files.Length <= 256, "TooManyCalibrationFiles", "Project contains more than 256 top-level CAL files.");
            Require(files.Length > 0, "NoCalibrationFiles", "No CAL files in the project directory.");
            long total = 0;
            foreach (string file in files)
            {
                total += new FileInfo(file).Length;
                Require(total <= 128L * 1024 * 1024, "CalibrationCatalogTooLarge",
                    "Top-level CAL files exceed the 128 MiB catalog limit.");
            }
            return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => ReadCandidate(root, f)).ToList();
        }

        internal static Candidate SelectForMap(List<Candidate> catalog, int physicalPhase, double targetLength, out string status)
        {
            Require(physicalPhase >= 0 && physicalPhase <= 3, "InvalidTestPhase",
                "Positioning column 0 must identify phase 0 (ALL), 1 (A), 2 (B), or 3 (C).");
            string phase = physicalPhase == 0 ? "ALL" : physicalPhase == 1 ? "A" : physicalPhase == 2 ? "B" : "C";
            var valid = catalog.Where(c => c.Valid).ToList();
            Require(valid.Count > 0, "NoValidCalibration", "No CAL passed all field, waveform and quality checks.");
            Func<IEnumerable<Candidate>, IOrderedEnumerable<Candidate>> order = values => values
                .OrderByDescending(c => c.Value).ThenByDescending(c => c.Quality)
                .ThenByDescending(c => c.Time).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Path, StringComparer.Ordinal);
            Candidate chosen = order(valid.Where(c => c.Phase == phase)).FirstOrDefault();
            status = "SelectedExactPhase";
            Require(physicalPhase != 0 || chosen != null, "NoAllCalibration", "An ALL positioning row requires a valid ALL CAL.");
            if (chosen == null) { chosen = order(valid.Where(c => c.Phase == "ALL")).FirstOrDefault(); status = "SelectedAllFallback"; }
            if (chosen == null)
            {
                Require(Positive(targetLength), "MissingCableLengthForCrossPhaseFallback",
                    "Cross-phase selection requires the current positioning row's positive cable length.");
                chosen = valid.OrderBy(c => Math.Abs(c.Length - targetLength)).ThenByDescending(c => c.Value)
                    .ThenByDescending(c => c.Quality).ThenByDescending(c => c.Time)
                    .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Path, StringComparer.Ordinal).First();
                status = "SelectedNearestOtherPhase";
            }
            return chosen;
        }
    }
}
