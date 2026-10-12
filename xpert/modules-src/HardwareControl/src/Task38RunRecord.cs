using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace HVX.OWTS.Task38
{
    public sealed class Task38RunRecord
    {
        public string RecordId { get; set; }
        public string RawCapturePath { get; set; }
        public string RawCaptureRecoveryPath { get; set; }
        public string FingerprintSha256 { get; set; }
        public string Nonce { get; set; }
        public string ArtSerial { get; set; }
        public string PicoSerial { get; set; }
        public bool Simulation { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string LocalTimeWithOffset { get; set; }
        public double DcDividerRatio { get; set; }
        public double AcDividerRatio { get; set; }
        public double VoltageGain { get; set; }
        public Task38Profile Profile { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public DateTime FinishedUtc { get; set; }
        public bool Success { get; set; }
        public bool IsZeroVoltageCheck { get; set; }
        public double MeasuredVoltagePeakV { get; set; }
        public bool PicoCaptureMetadataAvailable { get; set; }
        public double PicoActualSampleRateHz { get; set; }
        public double PicoActualIntervalNs { get; set; }
        public int PicoActualSampleCount { get; set; }
        public int PicoResolutionModeBits { get; set; }
        public int PicoRangeHardwareBits { get; set; }

        public static Task38RunRecord Create(string art, string pico, Task38Profile profile, bool simulation, DateTimeOffset timestamp)
        {
            if (String.IsNullOrWhiteSpace(art) || art.Trim() == "0" || String.IsNullOrWhiteSpace(pico))
                throw new InvalidOperationException("设备序列号未获取，无法生成可追溯试验编号。请重新连接并查看诊断信息。");
            if (!simulation && (art.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase) || pico.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("仿真设备身份不能用于真实试验编号。");
            if (profile == null || profile.Hardware == null) throw new ArgumentNullException("profile");
            if (!Positive(profile.Hardware.DividerRatioVPerV) || !Positive(profile.AcDividerRatioVPerV) || !Positive(profile.Hardware.VoltageGainVPerAoVolt))
                throw new ArgumentException("分压比和加压比必须是有限正数。");
            Task38Profile frozen = new Task38Profile { Hardware = profile.Hardware.Clone(), VoltageCalibration = profile.VoltageCalibration,
                AcDividerRatioVPerV = profile.AcDividerRatioVPerV, PdCalibrationPcPerVolt = profile.PdCalibrationPcPerVolt,
                InductanceH = profile.InductanceH, InductorDcrOhm = profile.InductorDcrOhm, Task6Directory = profile.Task6Directory };
            Task38RunRecord r = new Task38RunRecord { ArtSerial = art.Trim(), PicoSerial = pico.Trim(), Simulation = simulation,
                CreatedUtc = timestamp.UtcDateTime, LocalTimeWithOffset = timestamp.ToString("O", CultureInfo.InvariantCulture),
                DcDividerRatio = frozen.Hardware.DividerRatioVPerV, AcDividerRatio = frozen.AcDividerRatioVPerV,
                VoltageGain = frozen.Hardware.VoltageGainVPerAoVolt, Profile = frozen, Nonce = Guid.NewGuid().ToString("N"), Status = "Prepared", Detail = String.Empty };
            r.FingerprintSha256 = r.ComputeFingerprint();
            r.RecordId = (simulation ? "SIM" : "HV") + "-" + timestamp.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + r.FingerprintSha256.Substring(0, 12);
            return r;
        }
        public string ComputeFingerprint()
        {
            // Length-prefixed UTF-8 fields avoid separator ambiguity; invariant R keeps exact ratio values.
            using (MemoryStream bytes = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    foreach (string field in new[] { "Task38Run/v1", Simulation ? "SIM" : "HV", ArtSerial, PicoSerial,
                        DcDividerRatio.ToString("R", CultureInfo.InvariantCulture), AcDividerRatio.ToString("R", CultureInfo.InvariantCulture),
                        VoltageGain.ToString("R", CultureInfo.InvariantCulture), CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Nonce }) writer.Write(field ?? String.Empty);
                }
                using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", String.Empty);
            }
        }
        public void SaveNew(string directory)
        {
            Directory.CreateDirectory(directory);
            using (FileStream stream = new FileStream(Path.Combine(directory, RecordId + ".xml"), FileMode.CreateNew, FileAccess.Write))
                new XmlSerializer(typeof(Task38RunRecord)).Serialize(stream, this);
        }
        public void SaveUpdate(string directory)
        {
            string destination = Path.Combine(directory, RecordId + ".xml");
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) new XmlSerializer(typeof(Task38RunRecord)).Serialize(stream, this);
            // Never silently create an update for a record that was not successfully reserved.
            File.Replace(temp, destination, null);
        }
        private static bool Positive(double n) { return !Double.IsNaN(n) && !Double.IsInfinity(n) && n > 0; }
    }
}
