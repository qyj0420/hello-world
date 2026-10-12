using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HVX.CalibrationStorage
{
    // Local input-adapter contract, not a signed archive or a quality/acceptance
    // proof. Peak indices associate observed packets; the original times remain
    // the independent travel clock. The frozen packet gates still decide quality.
    internal sealed class CalibrationPacketIdentityContract
    {
        internal const string Schema = "HVX.CalibrationPacketIdentity/1";
        internal string ProjectedCalSha256, SourceCalSha256, SampleSha256, Phase, TimingMode;
        internal int RangeIndex, SampleCount, IncidentPeakIndex, ReflectionPeakIndex;
        internal double ChargePc, SampleRateHz, CoefficientPcPerV, IncidentSeconds, ReflectionSeconds;
        string boundCalPath, boundSidecarSha256;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] Fields = { "Schema", "ProjectedCalSha256", "SourceCalSha256", "SampleSha256", "Phase", "ChargePc", "RangeIndex",
            "SampleRateHz", "SampleCount", "CoefficientPcPerV", "IncidentSeconds", "ReflectionSeconds", "IncidentPeakIndex", "ReflectionPeakIndex", "TimingMode" };
        internal static string SidecarPath(string calPath) { return calPath + ".packets.xml"; }
        internal static bool HasSidecar(string calPath)
        {
            try { File.GetAttributes(SidecarPath(calPath)); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        internal static string HashFile(string path)
        {
            CheckUnlinked(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }
        internal static string HashSamples(double[] samples)
        {
            if (samples == null || samples.Length > 2000000 || samples.Any(x => !Finite(x))) throw Bad("sample array");
            // Explicit little-endian IEEE-754 doubles, not text formatting or a
            // filtered/display copy. This also detects stale pre-parser samples.
            using (var sha = SHA256.Create())
            {
                byte[] block = new byte[8192]; int used = 0;
                foreach (double sample in samples)
                {
                    ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(sample));
                    for (int j = 0; j < 8; j++) block[used + j] = (byte)(bits >> (8 * j)); used += 8;
                    if (used == block.Length) { sha.TransformBlock(block, 0, used, block, 0); used = 0; }
                }
                sha.TransformFinalBlock(block, 0, used); return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        internal void ValidateSamples(double[] samples)
        {
            if (samples == null || samples.Length != SampleCount || HashSamples(samples) != SampleSha256) throw Bad("sample identity mismatch");
        }
        internal void ValidateBinding(string phase, double chargePc, double sampleRateHz, double[] samples,
            double coefficientPcPerV, double incidentSeconds, double reflectionSeconds)
        {
            Validate(); ValidateSamples(samples);
            if (Phase != NormalizePhase(phase) || ChargePc != chargePc || SampleRateHz != sampleRateHz ||
                CoefficientPcPerV != coefficientPcPerV || IncidentSeconds != incidentSeconds || ReflectionSeconds != reflectionSeconds)
                throw Bad("parsed CAL binding mismatch");
            if (boundCalPath != null && (HashFile(boundCalPath) != ProjectedCalSha256 || HashFile(SidecarPath(boundCalPath)) != boundSidecarSha256))
                throw Bad("CAL or packet contract changed after parsing");
        }
        internal static void CheckUnlinked(string path)
        {
            for (string p = Path.GetFullPath(path); !String.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("CAL packet contract rejects linked paths."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
        static bool Sha(string s) { return s != null && s.Length == 64 && s.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F'); }
        static double Number(string value)
        {
            double n; if (!Double.TryParse(value, NumberStyles.Float, Inv, out n) || !Finite(n)) throw Bad("nonfinite or invalid number"); return n;
        }
        static int Integer(string value)
        {
            int n; if (!Int32.TryParse(value, NumberStyles.None, Inv, out n)) throw Bad("invalid integer"); return n;
        }
        static InvalidDataException Bad(string why) { return new InvalidDataException("CAL packet contract invalid: " + why + "."); }
        static string NormalizePhase(string phase)
        {
            switch (phase) { case "L1": return "A"; case "L2": return "B"; case "L3": return "C"; case "COMMON": return "ALL"; default: return phase; }
        }
        void Validate()
        {
            if (!Sha(ProjectedCalSha256) || !Sha(SourceCalSha256) || !Sha(SampleSha256) || !(Phase == "A" || Phase == "B" || Phase == "C" || Phase == "ALL") ||
                !(TimingMode == "FirstArrival" || TimingMode == "Peak") || RangeIndex < 0 || RangeIndex > 10 || SampleCount < 64 || SampleCount > 2000000 ||
                !Finite(ChargePc) || ChargePc <= 0 || !Finite(SampleRateHz) || SampleRateHz < 6000000 || !Finite(CoefficientPcPerV) || CoefficientPcPerV <= 0 ||
                !Finite(IncidentSeconds) || IncidentSeconds < 0 || !Finite(ReflectionSeconds) || ReflectionSeconds <= IncidentSeconds ||
                ReflectionSeconds > (SampleCount - 1) / SampleRateHz || IncidentPeakIndex < 0 || ReflectionPeakIndex <= IncidentPeakIndex || ReflectionPeakIndex >= SampleCount)
                throw Bad("identity, timing, range or packet indices out of bounds");
        }
        internal void WriteNew(string calPath)
        {
            Validate(); if (HashFile(calPath) != ProjectedCalSha256) throw Bad("projected CAL hash mismatch");
            string path = SidecarPath(calPath); CheckUnlinked(path);
            string[] values = { Schema, ProjectedCalSha256, SourceCalSha256, SampleSha256, Phase, ChargePc.ToString("R", Inv), RangeIndex.ToString(Inv),
                SampleRateHz.ToString("R", Inv), SampleCount.ToString(Inv), CoefficientPcPerV.ToString("R", Inv), IncidentSeconds.ToString("R", Inv),
                ReflectionSeconds.ToString("R", Inv), IncidentPeakIndex.ToString(Inv), ReflectionPeakIndex.ToString(Inv), TimingMode };
            var root = new XElement("CalibrationPacketIdentity", Fields.Select((name, i) => new XElement(name, values[i])));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true })) root.Save(writer);
        }
        // Validate a present local contract before any algorithm entry, including
        // the unchanged B parser. Only contract-bearing canonical projections
        // use this reader; genuinely absent sidecars retain legacy parsing.
        internal static CalibrationPacketIdentityContract ReadOptionalFile(string calPath)
        {
            if (!HasSidecar(calPath)) return null;
            CheckUnlinked(calPath); CheckUnlinked(SidecarPath(calPath));
            if (new FileInfo(calPath).Length > 32L * 1024 * 1024) throw Bad("CAL size");
            string before = HashFile(calPath);
            string[] keys = { "Date", "Time", "Cable length", "Cal.value", "Velocity (m/us)", "NI_Calib_factor",
                "Start_point (s)", "Final_point (s)", "SR", "Calib_Attenuation", "Phase", "Units" };
            string[] values = new string[keys.Length]; var samples = new List<double>();
            using (var stream = new FileStream(calPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false))
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    string line = reader.ReadLine(); if (i == 0 && line != null) line = line.TrimStart('\ufeff');
                    string prefix = keys[i] + "\t";
                    if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal) || line.IndexOf('\t', prefix.Length) >= 0)
                        throw Bad("canonical CAL header at line " + (i + 1));
                    values[i] = line.Substring(prefix.Length);
                }
                if (values[11] != "V" || reader.ReadLine() != "Data") throw Bad("canonical CAL units or data boundary");
                string sample; while ((sample = reader.ReadLine()) != null)
                {
                    if (samples.Count >= 2000000 || String.IsNullOrWhiteSpace(sample) || sample.IndexOf('\t') >= 0)
                        throw Bad("canonical CAL waveform");
                    samples.Add(Number(sample));
                }
            }
            string namedPhase = NormalizePhase(Path.GetFileNameWithoutExtension(calPath).Split('_')[0].ToUpperInvariant());
            if (NormalizePhase(values[10]) != namedPhase) throw Bad("CAL filename/header phase mismatch");
            var result = ReadOptional(calPath, namedPhase, Number(values[3]), Number(values[8]), samples.ToArray(),
                Number(values[5]), Number(values[6]), Number(values[7]));
            if (result == null || before != HashFile(calPath)) throw Bad("CAL or present contract changed during parsing");
            return result;
        }
        // Null means a genuinely absent optional contract. A present bad contract
        // is never downgraded to legacy parsing, including a directory at its path.
        internal static CalibrationPacketIdentityContract ReadOptional(string calPath, string phase, double chargePc, double sampleRateHz,
            double[] samples, double coefficientPcPerV, double incidentSeconds, double reflectionSeconds)
        {
            string path = SidecarPath(calPath);
            if (!HasSidecar(calPath)) return null;
            CheckUnlinked(path); CheckUnlinked(calPath);
            if (Directory.Exists(path) || new FileInfo(path).Length > 16384) throw Bad("metadata size or file kind");
            string metadataHash = HashFile(path);
            try
            {
                XDocument doc;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 16384, MaxCharactersFromEntities = 0, IgnoreWhitespace = false })) doc = XDocument.Load(reader);
                XElement root = doc.Root;
                if (root == null || root.Name != "CalibrationPacketIdentity" || root.HasAttributes ||
                    doc.Nodes().Any(n => !(n is XElement) && !(n is XText && String.IsNullOrWhiteSpace(((XText)n).Value))) ||
                    root.Nodes().Any(n => !(n is XElement) && !(n is XText && String.IsNullOrWhiteSpace(((XText)n).Value)))) throw Bad("unexpected root or XML nodes");
                var elements = root.Elements().ToArray();
                if (elements.Length != Fields.Length || elements.Any(e => e.HasAttributes || e.HasElements || e.Nodes().Any(n => !(n is XText))) ||
                    elements.Select(e => e.Name.ToString()).Distinct().Count() != Fields.Length || elements.Any(e => !Fields.Contains(e.Name.ToString()))) throw Bad("missing, duplicate or unexpected fields");
                var v = elements.ToDictionary(e => e.Name.ToString(), e => e.Value, StringComparer.Ordinal);
                if (v["Schema"] != Schema) throw Bad("schema version");
                var c = new CalibrationPacketIdentityContract { ProjectedCalSha256 = v["ProjectedCalSha256"], SourceCalSha256 = v["SourceCalSha256"], SampleSha256 = v["SampleSha256"],
                    Phase = v["Phase"], TimingMode = v["TimingMode"], ChargePc = Number(v["ChargePc"]), RangeIndex = Integer(v["RangeIndex"]),
                    SampleRateHz = Number(v["SampleRateHz"]), SampleCount = Integer(v["SampleCount"]), CoefficientPcPerV = Number(v["CoefficientPcPerV"]),
                    IncidentSeconds = Number(v["IncidentSeconds"]), ReflectionSeconds = Number(v["ReflectionSeconds"]),
                    IncidentPeakIndex = Integer(v["IncidentPeakIndex"]), ReflectionPeakIndex = Integer(v["ReflectionPeakIndex"]) };
                c.ValidateBinding(phase, chargePc, sampleRateHz, samples, coefficientPcPerV, incidentSeconds, reflectionSeconds);
                if (c.ProjectedCalSha256 != HashFile(calPath)) throw Bad("CAL bytes or header binding mismatch");
                if (metadataHash != HashFile(path)) throw Bad("packet contract changed during parsing");
                c.boundCalPath = Path.GetFullPath(calPath); c.boundSidecarSha256 = metadataHash;
                return c;
            }
            catch (XmlException error) { throw new InvalidDataException("CAL packet contract XML rejected.", error); }
        }
    }
}
