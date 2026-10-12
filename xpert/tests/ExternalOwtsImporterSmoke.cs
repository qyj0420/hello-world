using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Desktop;

internal static class ExternalOwtsImporterSmoke
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Throws(Action action, string message) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, message); }
    static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)); }
    static string Wave(string cal, string dt, bool tan, string rows = "1\t0\r\n0\t0\r\n-1\t0\r\n0\t0\r\n", int count = 4)
    {
        return "Date:\t28/09/2026\r\nTime:\t12:00\r\nFile-path:\tZ:\\DoNotFollow\\Other.tot\r\nCal.File-path:\t" + cal +
            "\r\nCal.value (pC):\t1000\r\nCal.max (mV):\t1000\r\nV-scale max (kV):\t12\r\nPD-scale max(pC):\t1250\r\nNumber of Samples:\t" + count +
            "\r\nDelta-x:\t" + dt + "\r\n" + (tan ? "Hz=\t100\r\nC[uF]=\t0.2\r\nD-losses=\t0\r\n" : "") + "DATA-TOTAL:\r\nVoltage (kV):\tPD (pC):\r\n" + rows;
    }
    static string Fixture(string parent, string name)
    {
        string path = Path.Combine(parent, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "StartMask.dat"), "[Directory]\r\nMeasurementDirectory=Z:\\NeverRead\r\n[MaskData]\r\nPlaceCity=合成测试\r\nOwner=实验室\r\nCableLength=100\r\nNominalVoltage=8.7\r\nCableConstruction=0\r\nJointLocationL100=1,30,\r\nJointLocationL101=2,60,冷缩\r\n", Encoding.GetEncoding(936));
        File.WriteAllText(Path.Combine(path, "All_1000.cal"), "Date:\t28/09/2026\r\nCal.max (mV):\t1000\r\nCal.value (pC):\t1000\r\nDelta_x:\t1E-8\r\nData:\r\n1000\r\n-1000\r\n0\r\n", Encoding.ASCII);
        string stem = "L1_11kV0_2026-09-28-12'00'00";
        File.WriteAllText(Path.Combine(path, stem + ".tot"), Wave("Z:\\NeverRead\\All_1000.cal", "1.25E-5", false), Encoding.ASCII);
        File.WriteAllText(Path.Combine(path, stem + ".tan"), Wave("Z:\\NeverRead\\All_1000.cal", "1.25E-5", true), Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(path, stem + ".loc"), Encoding.ASCII.GetBytes("Delta x:\t1E-8\r\nBINARY:\r\n").Concat(new byte[] { 0, 255, 20, 1 }).ToArray());
        return path;
    }
    static int Main(string[] args)
    {
        try
        {
            string basePath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetFullPath(args[0]), "ExternalOwtsImporterSmoke");
            string qa = Path.Combine(basePath, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            string project = Fixture(qa, "Batch");
            var before = Directory.GetFiles(project).ToDictionary(p => p, Hash);
            var found = ExternalOwtsImporter.Discover(qa);
            Check(found.Complete && found.BatchDirectories.SequenceEqual(new[] { project }), "discover StartMask batch");
            var batch = ExternalOwtsImporter.ReadBatch(project);
            Check(batch.IsComplete && batch.HasImportableData, "validated batch with unsupported LOC explicitly recorded");
            Check(batch.MetadataEncoding == "GB936" && batch.Metadata["PlaceCity"] == "合成测试", "Chinese metadata decode");
            Check(batch.CableLengthMetres == 100 && batch.NominalVoltageKv == 8.7, "physical metadata preserved");
            Check(batch.CableConstructionCode == "0", "construction code not guessed");
            Check(batch.Joints.Count == 2 && batch.Joints[0].Phase == "L1" && batch.Joints[0].PositionMetres == 30, "joint positions and source phase preserved");
            Check(!batch.Joints.Any(j => j.Phase == "L2" || j.Phase == "L3"), "no invented other phase joints");
            var cal = batch.Records.Single(r => r.Kind == "calibration");
            Check(cal.Status == "valid" && cal.Values[0] == 1 && cal.Values[1] == -1 && cal.ValueUnit == "V", "mV to V exactly once");
            Check(cal.SampleRateHz == 100000000 && cal.Phase == "ALL", "CAL seconds timebase");
            var wave = batch.Records.Single(r => r.Kind == "oscillation");
            Check(wave.Sources.Count == 2 && wave.Values.Count == 4 && wave.VoltageKv.Count == 4, "TOT/TAN paired into one waveform");
            Check(wave.SampleRateHz == 80000 && wave.TestVoltageKv == 11, "seconds timebase and filename voltage");
            Check(wave.Values.All(x => x == 0) && wave.CanBrowseWaveform, "valid zero PD samples preserved");
            Check(wave.Headers["D-losses"] == "0", "valid zero dielectric loss retained");
            Check(wave.CalibrationRecordId == cal.Id, "calibration resolved only from same batch basename");
            var loc = batch.Records.Single(r => r.Kind == "location");
            Check(loc.Status == "unsupported" && loc.Values.Count == 0 && !loc.HasDecodedLocationPulses, "binary LOC never guessed");
            Check(loc.Headers["Delta x"] == "1E-8" && loc.Sources[0].Sha256.Length == 64, "unknown LOC provenance and text header preserved");
            Check(batch.Records.All(r => r.Sources.All(s => s.FullPath.StartsWith(project + Path.DirectorySeparatorChar))), "historical absolute paths never followed");
            Check(before.All(p => p.Value == Hash(p.Key)), "source bytes unchanged");
            string json = new JavaScriptSerializer().Serialize(batch.CreateManifest());
            Check(json.Contains("external-owts-import-v1") && json.Contains("unsupported") && !json.Contains("NaN"), "metadata bridge serializes without false locations");
            Throws(() => ((IList<double>)wave.Values)[0] = 99, "sample arrays readonly");
            Throws(() => ((IDictionary<string, string>)batch.Metadata)["CableLength"] = "0", "metadata readonly");
            Check(!ExternalOwtsImporter.Discover(qa, new ExternalOwtsLimits { MaximumDepth = 0 }).Complete, "depth cap explicit");
            Throws(() => ExternalOwtsImporter.ReadBatch(project, new ExternalOwtsLimits { MaximumFilesPerBatch = 1 }), "file count limit");
            Throws(() => ExternalOwtsImporter.ReadBatch(project, new ExternalOwtsLimits { MaximumFileBytes = 8 }), "metadata byte limit");
            var limited = ExternalOwtsImporter.ReadBatch(project, new ExternalOwtsLimits { MaximumSamplesPerRecord = 2 });
            Check(limited.Records.Where(r => r.Kind == "oscillation" || r.Kind == "calibration").All(r => r.Status == "invalid"), "sample cap invalidates rather than truncates");
            Throws(() => ExternalOwtsImporter.ReadBatch(project, cancellation: new CancellationToken(true)), "cancellation");
            Throws(() => ExternalOwtsImporter.ReadBatch(Path.GetPathRoot(project)), "drive root rejected");
            string wrong = Fixture(qa, "CountMismatch");
            string tot = Directory.GetFiles(wrong, "*.tot").Single(); File.WriteAllText(tot, Wave("All_1000.cal", "1E-5", false, count: 99));
            var mismatch = ExternalOwtsImporter.ReadBatch(wrong);
            Check(!mismatch.IsComplete && mismatch.Records.Single(r => r.Kind == "oscillation").Status == "invalid", "declared sample mismatch rejected without TAN substitution");
            string disagreement = Fixture(qa, "TanMismatch");
            File.WriteAllText(Directory.GetFiles(disagreement, "*.tan").Single(), Wave("All_1000.cal", "1.25E-5", true, "1\t9\r\n0\t0\r\n-1\t0\r\n0\t0\r\n"));
            var different = ExternalOwtsImporter.ReadBatch(disagreement);
            Check(different.Issues.Any(i => i.Code == "tan_tot_mismatch") && !different.Records.Single(r => r.Kind == "oscillation").Headers.ContainsKey("D-losses"), "mismatched TAN physical values not merged");
            string nonfinite = Fixture(qa, "NonFinite");
            File.WriteAllText(Directory.GetFiles(nonfinite, "*.tot").Single(), Wave("All_1000.cal", "0", false));
            Check(ExternalOwtsImporter.ReadBatch(nonfinite).Records.Single(r => r.Kind == "oscillation").Status == "invalid", "zero timebase refused");
            string unlinked = Fixture(qa, "MissingCalibration");
            File.WriteAllText(Directory.GetFiles(unlinked, "*.tot").Single(), Wave("C:\\outside\\Missing.cal", "1.25E-5", false));
            var missing = ExternalOwtsImporter.ReadBatch(unlinked);
            Check(missing.Records.Single(r => r.Kind == "oscillation").CalibrationRecordId == "" && missing.Issues.Any(i => i.Code == "calibration_unavailable"), "unavailable calibration not invented");
            if (args.Length > 2)
            {
                // Optional user-named real batch, read only; never converts or writes to it.
                string real = Path.GetFullPath(args[2]);
                long bytes = Directory.GetFiles(real).Sum(p => new FileInfo(p).Length);
                Check(bytes < 16L * 1024 * 1024, "real smoke limited to a small batch");
                var sourceHashes = Directory.GetFiles(real).ToDictionary(p => p, Hash);
                var imported = ExternalOwtsImporter.ReadBatch(real, new ExternalOwtsLimits { MaximumBatchBytes = 16L * 1024 * 1024 });
                Check(imported.MetadataEncoding == "GB936" && imported.CableLengthMetres == 780, "real sample metadata");
                Check(imported.Records.Count(r => r.Kind == "calibration" && r.Status == "valid") == 9, "real sample calibration records");
                Check(imported.Records.Count(r => r.Kind == "oscillation" && r.Status == "valid") == 15, "real sample TOT/TAN records");
                Check(imported.Records.Count(r => r.Kind == "location" && r.Status == "unsupported") == 15, "real LOC intentionally unsupported");
                Check(imported.Records.Where(r => r.Kind == "oscillation" && r.Status == "valid").All(r => r.SampleIntervalSeconds > 0), "real explicit timebase");
                Check(sourceHashes.All(p => p.Value == Hash(p.Key)), "all real source hashes unchanged");
                Console.WriteLine("REAL_BATCH_BYTES=" + bytes + ";VALID=" + imported.Records.Count(r => r.Status == "valid") + ";ISSUES=" + imported.Issues.Count);
            }
            Console.WriteLine("EXTERNAL_OWTS_IMPORT_PASS=" + checks); Console.WriteLine("QA=" + qa); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
