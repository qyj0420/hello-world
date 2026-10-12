using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xiongxin.OWTS.Desktop;

internal static class ReferenceSimulationProfileSmoke
{
    static int checks;
    static void Check(bool success, string name) { checks++; if (!success) throw new Exception(name); }
    static void Reject(Action action, string name)
    { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, name); }
    static string Hash(string path)
    { using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""); }
    static int Main(string[] args)
    {
        try
        {
            string asset = Path.Combine(args[0], ReferenceSimulationProfile.RelativeAssetPath.Replace('/', Path.DirectorySeparatorChar));
            string qa = Path.GetFullPath(args[1]); Directory.CreateDirectory(qa);
            string originalHash = Hash(asset);
            var profile = ReferenceSimulationProfile.Load(asset);
            Check(profile.Id == "rdfz-jmf-v1" && profile.Title.Contains("人大附中215") && profile.Title.Contains("金茂府4#212"), "Source identity");
            Check(profile.SourceProjectRelativePath.Contains("2023年06月14日-15时24分"), "Explicit source session");
            Check(profile.AssetSha256 == ReferenceSimulationProfile.ExpectedSha256, "Pinned content");
            Check(profile.SourceLengthM == 810 && profile.NominalPhaseVoltageKv == 8.7, "Source geometry is separate from active project");
            Check(profile.Calibrations.Length == 4, "Four CAL examples");
            foreach (var cal in profile.Calibrations)
            {
                Check(cal.SourcePhase == "ALL", "Do not invent phase-specific calibration");
                Check(cal.SampleRateHz == 125000000 && cal.VoltageSamples.Length == 2475, "CAL sampling evidence");
                Check(cal.SourceReflectionSeconds > cal.SourceIncidentSeconds && cal.TimingEvidence.Contains("headers"), "Original cursor evidence");
                Check(cal.SourceVelocityMPerUs > 167 && cal.SourceVelocityMPerUs < 169, "Original velocity evidence");
                Check(cal.SourceFile.EndsWith(".cal") && cal.SourceSha256.Length == 64, "CAL provenance");
                Check(cal.VoltageSamples.All(v => !Double.IsNaN(v) && !Double.IsInfinity(v)), "Finite voltage waveform");
                Check(Math.Abs(2 * profile.SourceLengthM / ((cal.SourceReflectionSeconds - cal.SourceIncidentSeconds) * 1000000) - cal.SourceVelocityMPerUs) < .2,
                    "Header timing agrees with source length and velocity");
            }
            var oneNc = profile.Calibrations.Single(c => c.LevelCode == "1nC");
            Check(Math.Abs(oneNc.VoltageSamples.Max() - .570688104) < 1e-8, "mV source is converted once into V");
            Check(profile.PressureRecords.Length == 12, "Twelve representative DEM records");
            foreach (string phase in new[] { "A", "B", "C" })
            {
                var records = profile.PressureRecords.Where(p => p.Phase == phase).ToArray();
                Check(records.Length == 4 && records.Select(p => p.NominalVoltageKv).OrderBy(v => v).SequenceEqual(new[] { 0d, 3.69, 12.30, 20.91 }), "Per-phase levels");
                var zero = records.Single(r => r.NominalVoltageKv == 0);
                Check(zero.DischargePc.Any(v => Math.Abs(v) > .001), "Retain nonzero historical zero-voltage signal");
                Check(Double.IsNaN(zero.RecordedFrequencyHz) && !zero.RecordedCapacitanceUf.HasValue, "Do not invent zero-shot metrics");
            }
            foreach (var record in profile.PressureRecords)
            {
                Check(record.RateEstimated && record.RateEvidence.Length > 20, "Estimated timebase explicitly identified");
                Check(record.SampleRateHz > 79000 && record.SampleRateHz < 81000, "Do not mistake legacy Delta x for rate");
                Check(record.VoltageKv.Length == 3200 && record.DischargePc.Length == 3201, "Preserve differing original column lengths");
                Check(record.SourceFile.EndsWith(".dem") && record.SourceSha256.Length == 64, "DEM provenance");
                if (record.NominalVoltageKv > 0)
                {
                    Check(record.RecordedFrequencyHz > 340 && record.RecordedFrequencyHz < 350, "Use recorded oscillation frequency");
                    Check(record.RecordedCapacitanceUf == .28, "Keep original capacitance metadata");
                }
            }
            string bad = Path.Combine(qa, "tampered-profile.json");
            byte[] bytes = File.ReadAllBytes(asset); bytes[bytes.Length / 2] ^= 1; File.WriteAllBytes(bad, bytes);
            Reject(() => ReferenceSimulationProfile.Load(bad), "Reject modified fixture before parsing");
            string small = Path.Combine(qa, "small-profile.json"); File.WriteAllText(small, "{}");
            Reject(() => ReferenceSimulationProfile.Load(small), "Reject incomplete fixture");
            Reject(() => ReferenceSimulationProfile.Load(Path.Combine(qa, "not-present.json")), "Missing fixture fails, never silently fabricates reference data");
            Check(Hash(asset) == originalHash, "Installed asset unchanged");
            Check(ReferenceSimulationProfile.Load(asset).Calibrations[0].VoltageSamples.Length == 2475, "Repeated load remains independent");
            Console.WriteLine("PASS ReferenceSimulationProfileSmoke " + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
