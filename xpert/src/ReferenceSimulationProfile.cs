using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Xiongxin.OWTS.Desktop
{
    // A small, pinned, read-only demonstration asset. It is never a project
    // reader or a device calibration provider, and no source paths are opened.
    internal sealed class ReferenceSimulationProfile
    {
        internal const string RelativeAssetPath = "assets/reference-simulation/rdfz-jmf-v1.json";
        internal const string ExpectedSha256 = "D27E8D8BE663D42F5A9D12830A1CD2B0552F22D1B2EBD505C7F45C4DEBDDC0A8";
        internal readonly string Id, Title, SourceProjectRelativePath, AssetSha256;
        internal readonly double SourceLengthM, NominalPhaseVoltageKv;
        internal readonly Calibration[] Calibrations;
        internal readonly PressureRecord[] PressureRecords;

        internal sealed class Calibration
        {
            internal string LevelCode, SourcePhase, SourceFile, SourceSha256, TimingEvidence;
            internal double ChargePc, SampleRateHz, SourceIncidentSeconds, SourceReflectionSeconds, SourceVelocityMPerUs;
            internal double[] VoltageSamples;
        }

        internal sealed class PressureRecord
        {
            internal string Id, Phase, SourceFile, SourceSha256, RateEvidence;
            internal double NominalVoltageKv, SampleRateHz, RecordedFrequencyHz;
            internal double? RecordedCapacitanceUf, RecordedLossPercent;
            internal bool RateEstimated;
            internal double[] VoltageKv, DischargePc;
        }

        internal static ReferenceSimulationProfile LoadDefault()
        {
            return Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                RelativeAssetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        internal static ReferenceSimulationProfile Load(string path)
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 128 || stream.Length > 8 * 1024 * 1024)
                    throw new InvalidDataException("Invalid reference simulation asset size.");
                bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("Incomplete reference simulation asset.");
                    offset += read;
                }
            }
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            if (!String.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Reference simulation asset integrity check failed. Restore it from the release package.");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 32 };
            string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            var root = Map(serializer.DeserializeObject(text));
            return new ReferenceSimulationProfile(root, hash);
        }

        private ReferenceSimulationProfile(IDictionary<string, object> root, string hash)
        {
            if (Text(root, "schema") != "HVX.ReferenceSimulation/1" || Text(root, "id") != "rdfz-jmf-v1")
                throw new InvalidDataException("Unsupported reference simulation schema.");
            Id = Text(root, "id"); Title = Text(root, "title");
            SourceProjectRelativePath = Text(root, "sourceProjectRelativePath"); AssetSha256 = hash;
            SourceLengthM = Number(root, "sourceLengthM", .1, 100000);
            NominalPhaseVoltageKv = Number(root, "nominalPhaseVoltageKv", .01, 500);
            var calibrations = new List<Calibration>();
            var calibrationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object entry in Items(root, "calibrations", 1, 16))
            {
                var item = Map(entry);
                var cal = new Calibration {
                    LevelCode = Text(item, "levelCode"), ChargePc = Number(item, "chargePc", .01, 10000000),
                    SourcePhase = Text(item, "sourcePhase"), SampleRateHz = Number(item, "sampleRateHz", 1, 10000000000),
                    VoltageSamples = Samples(item, "voltageSamples", 1000),
                    SourceIncidentSeconds = Number(item, "sourceIncidentSeconds", 0, 1),
                    SourceReflectionSeconds = Number(item, "sourceReflectionSeconds", 0, 1),
                    SourceVelocityMPerUs = Number(item, "sourceVelocityMPerUs", 1, 400),
                    SourceFile = Text(item, "sourceFile"), SourceSha256 = Sha(item, "sourceSha256"),
                    TimingEvidence = Text(item, "timingEvidence")
                };
                if (Text(item, "sourceUnit") != "mV" || cal.SourcePhase != "ALL" ||
                    !calibrationNames.Add(cal.LevelCode) || cal.SourceReflectionSeconds <= cal.SourceIncidentSeconds ||
                    cal.SourceReflectionSeconds > cal.VoltageSamples.Length / cal.SampleRateHz)
                    throw new InvalidDataException("Invalid reference calibration provenance or timing.");
                calibrations.Add(cal);
            }
            Calibrations = calibrations.ToArray();
            var records = new List<PressureRecord>();
            var recordNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (object entry in Items(root, "pressureRecords", 1, 64))
            {
                var item = Map(entry);
                var record = new PressureRecord {
                    Id = Text(item, "id"), Phase = Text(item, "phase"),
                    NominalVoltageKv = Number(item, "nominalVoltageKv", 0, 500),
                    SampleRateHz = Number(item, "sampleRateHz", 1, 1000000000),
                    RateEstimated = Flag(item, "rateEstimated"), RateEvidence = Text(item, "rateEvidence"),
                    RecordedFrequencyHz = OptionalNumber(item, "recordedFrequencyHz", 0, 100000) ?? Double.NaN,
                    RecordedCapacitanceUf = OptionalNumber(item, "recordedCapacitanceUf", 0, 1000000),
                    RecordedLossPercent = OptionalNumber(item, "recordedLossPercent", 0, 100),
                    VoltageKv = Samples(item, "voltageKv", 1000), DischargePc = Samples(item, "dischargePc", 1000000000),
                    SourceFile = Text(item, "sourceFile"), SourceSha256 = Sha(item, "sourceSha256")
                };
                if (!new[] { "A", "B", "C" }.Contains(record.Phase) || !recordNames.Add(record.Id) ||
                    Math.Abs(record.VoltageKv.Length - record.DischargePc.Length) > 1 ||
                    (record.NominalVoltageKv > 0 && !record.VoltageKv.Any(v => Math.Abs(v) > .001)))
                    throw new InvalidDataException("Invalid reference pressure record.");
                records.Add(record);
            }
            PressureRecords = records.ToArray();
            // This profile deliberately has no verified location events. A client
            // must not substitute synthetic positions and call them historical.
            if (Items(root, "locationEvents", 0, 0).Length != 0)
                throw new InvalidDataException("Unverified location events are not supported.");
            Text(root, "locationEvidence");
            foreach (string phase in new[] { "A", "B", "C" })
                if (!records.Any(r => r.Phase == phase && r.NominalVoltageKv == 0) ||
                    !records.Any(r => r.Phase == phase && r.NominalVoltageKv > 0))
                    throw new InvalidDataException("Missing phase-specific reference examples.");
        }

        private static IDictionary<string, object> Map(object value)
        {
            var result = value as IDictionary<string, object>;
            if (result == null) throw new InvalidDataException("Expected a reference asset object.");
            return result;
        }
        private static object Field(IDictionary<string, object> item, string key)
        {
            object value;
            if (!item.TryGetValue(key, out value)) throw new InvalidDataException("Missing reference asset field: " + key);
            return value;
        }
        private static string Text(IDictionary<string, object> item, string key)
        {
            string value = Field(item, key) as string;
            if (String.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Invalid reference text: " + key);
            return value;
        }
        private static string Sha(IDictionary<string, object> item, string key)
        {
            string value = Text(item, key);
            if (value.Length != 64 || !value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                throw new InvalidDataException("Invalid source checksum.");
            return value;
        }
        private static bool Flag(IDictionary<string, object> item, string key)
        {
            object value = Field(item, key);
            if (!(value is bool)) throw new InvalidDataException("Invalid reference flag: " + key);
            return (bool)value;
        }
        private static double Number(IDictionary<string, object> item, string key, double min, double max)
        { return Numeric(Field(item, key), key, min, max); }
        private static double? OptionalNumber(IDictionary<string, object> item, string key, double min, double max)
        {
            object value;
            if (!item.TryGetValue(key, out value) || value == null) return null;
            return Numeric(value, key, min, max);
        }
        private static double Numeric(object value, string key, double min, double max)
        {
            if (!(value is int || value is long || value is double || value is decimal))
                throw new InvalidDataException("Invalid reference number: " + key);
            double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(result) || Double.IsInfinity(result) || result < min || result > max)
                throw new InvalidDataException("Reference value out of range: " + key);
            return result;
        }
        private static object[] Items(IDictionary<string, object> item, string key, int min, int max)
        {
            var value = Field(item, key) as object[];
            if (value == null || value.Length < min || value.Length > max)
                throw new InvalidDataException("Invalid reference collection: " + key);
            return value;
        }
        private static double[] Samples(IDictionary<string, object> item, string key, double magnitude)
        {
            var source = Items(item, key, 32, 32768);
            var samples = new double[source.Length];
            for (int i = 0; i < source.Length; i++) samples[i] = Numeric(source[i], key, -magnitude, magnitude);
            return samples;
        }
    }
}
