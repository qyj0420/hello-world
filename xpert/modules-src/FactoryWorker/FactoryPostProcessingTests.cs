using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPostProcessingTests
    {
        private static int checks;
        internal static string Run()
        {
            checks = 0;
            Check(FactoryCalibrationLevels.Labels.Length == 15 && FactoryCalibrationLevels.Pc.Length == 15, "100 charge levels without TDR");
            Check(FactoryCalibrationLevels.Pc.SequenceEqual(new double[] { 200000,150000,100000,50000,20000,10000,5000,2000,1000,500,200,100,50,20,10 }), "exact physical pC levels");
            Check(FactoryCalibrationLevels.Pc[FactoryCalibrationLevels.DefaultIndex] == 100, "100 pC default");
            string workspace = Path.Combine(Path.GetTempPath(), "Xiongxin-Factory-PostTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(workspace);
            var round = Round(Path.Combine(workspace, "original"));
            AddPulse(round.Capture, 2000, 8000); AddPulse(round.Capture, 7000, -12000);
            short[] frozen = (short[])round.Capture.PdAdc.Clone();
            var estimate = FactoryTfAnalysis.Estimate(round.Capture);
            Check(estimate.ThresholdPc > 0 && estimate.ThresholdPc < 20, "MAD threshold separates noise from pulses");
            Check(Math.Abs(estimate.BaselineV) < .0001 && round.Capture.PdAdc[100]==frozen[100], "high-pass threshold uses processed baseline; native DC preserved in ADC");
            var tf = FactoryTfAnalysis.Extract(round, 10);
            Check(tf.CandidateCount == 2 && tf.Pulses.Count == 2, "two bounded independent pulses");
            Check(tf.Pulses.All(p => p.Valid), "energy moment TF valid: " + String.Join(";", tf.Pulses.Select(p => p.Detail)));
            Check(tf.Pulses[0].ChargePc > 0 && tf.Pulses[1].ChargePc < 0, "signed peak charge");
            Near(tf.Pulses[0].TimeMs, 2, .002, "native peak time");
            Near(tf.Pulses[0].ChargePc, FactoryPdSignal.Get(round.Capture)[tf.Pulses[0].PeakSample] * 2100, 1, "filtered peak uses capture-time calibration units");
            Check(tf.Pulses.All(p => p.EquivalentTimeUs > 0 && p.EquivalentBandwidthMHz > 0), "positive Teq and Weq");
            Check(round.Capture.PdAdc.SequenceEqual(frozen) && round.Capture.Settings.PdCalibrationPcPerV == 2100, "analysis does not rewrite ADC or coefficient");
            var faster = Round(Path.Combine(workspace, "faster"), 2500000);
            Array.Copy(round.Capture.PdAdc, faster.Capture.PdAdc, frozen.Length);
            var fastTf = FactoryTfAnalysis.Extract(faster, 10);
            Check(fastTf.Pulses.Count == 2 && fastTf.Pulses.All(p => p.Valid), "actual rate accepted by TF");
            var referenceAnalyzer=new HVX.PD.PulseToolkit.PulseAnalysisEngine();
            var fastReference=referenceAnalyzer.Analyze(fastTf.Pulses[0].SamplesV,2500000);
            Near(fastTf.Pulses[0].EquivalentTimeUs,fastReference.EquivalentTimeNs/1000,1e-9,"Teq uses actual sample interval after fixed-Hz filter");
            Near(fastTf.Pulses[0].EquivalentBandwidthMHz,fastReference.EquivalentBandwidthMHz,1e-9,"Weq uses actual sample rate after fixed-Hz filter");
            Throws(() => FactoryTfAnalysis.Extract(round, 0), "zero threshold rejected");
            Throws(() => FactoryTfAnalysis.Extract(round, Double.NaN), "NaN threshold rejected");
            round.Completed = false; Throws(() => FactoryTfAnalysis.Extract(round, 10), "incomplete round blocked"); round.Completed = true;
            round.ResidualSafe = false; Throws(() => FactoryTfAnalysis.Extract(round, 10), "unsafe round blocked"); round.ResidualSafe = true;
            round.Kind = FactoryRunKind.Calibration; Throws(() => FactoryTfAnalysis.Extract(round, 10), "calibration excluded from trial TF"); round.Kind = FactoryRunKind.Pressure;
            round.Capture.Overflow = true; var overflow = FactoryTfAnalysis.Extract(round, 10);
            Check(overflow.Pulses.Count == 2 && overflow.Pulses.All(p => !p.Valid), "driver overflow retained but not valid TF"); round.Capture.Overflow = false;
            var clipped = Round(Path.Combine(workspace, "clipped")); AddPulse(clipped.Capture, 2000, 32767);
            var clippedTf = FactoryTfAnalysis.Extract(clipped, 10);
            Check(clippedTf.Pulses.Count == 1 && clippedTf.Pulses[0].Clipped && !clippedTf.Pulses[0].Valid, "rail clipping rejected quantitatively");
            var edge = Round(Path.Combine(workspace, "edge")); AddPulse(edge.Capture, 0, 10000); AddPulse(edge.Capture, edge.Capture.PdAdc.Length - 1, 10000);
            var edgeTf = FactoryTfAnalysis.Extract(edge, 10);
            Check(edgeTf.Pulses.Count == 0 && edgeTf.RejectedCount > 0, "incomplete boundary windows excluded");
            var sustained = Round(Path.Combine(workspace, "sustained")); for (int i = 4000; i < 8000; i++) sustained.Capture.PdAdc[i] = 8000;
            var stepEdges=FactoryTfAnalysis.Extract(sustained,10);
            Check(stepEdges.Pulses.All(p=>p.TimeMs<4.3 || p.TimeMs>7.7),"high-pass step edges may be candidates, but plateau is not fabricated as PD");
            // Separate finite 10 kHz FIR tails; overlapping responses are not
            // evidence of 120 independently bounded pulses.
            var many = Round(Path.Combine(workspace, "many"),1000000,60); for (int i = 0; i < 120; i++) AddPulse(many.Capture, 500 + i * 450, 2000 + i * 100);
            var manyTf = FactoryTfAnalysis.Extract(many, 10);
            Check(manyTf.CandidateCount == 120 && manyTf.Pulses.Count == 100, "hard cap of 100 pulses per capture");
            Check(manyTf.Pulses[0].PeakSample >= 500 + 20 * 450, "keep largest 100 candidates");
            Check(manyTf.Pulses.Zip(manyTf.Pulses.Skip(1), (a,b) => a.TimeMs < b.TimeMs).All(x => x), "chronological features");
            var background = Round(Path.Combine(workspace, "background"));
            Check(FactoryTfAnalysis.Extract(background, FactoryTfAnalysis.Estimate(background.Capture).ThresholdPc).Pulses.Count == 0, "small background not fabricated as PD");
            for (int i = 0; i < background.Capture.PdAdc.Length; i++) background.Capture.PdAdc[i] = 1000;
            Check(FactoryTfAnalysis.Estimate(background.Capture).ThresholdPc > 0 && FactoryTfAnalysis.Extract(background, 10).Pulses.Count == 0, "constant biased input yields no pulses");

            var session = new FactoryTfSession(); session.Add(tf); session.Add(tf);
            Check(session.RoundCount == 1 && session.Rounds[0].Pulses.Count == 2, "reconfirm replaces same source, no duplicate");
            tf.Pulses[0].ChargePc = 123456;
            Check(session.Rounds[0].Pulses[0].ChargePc != 123456 && session.Rounds[0].Pulses[0].SamplesV.Length == 0, "immutable feature snapshot, no raw arrays in history");
            tf = FactoryTfAnalysis.Extract(round, 10); session.Add(tf);
            var selection = new FactoryPulseSelection();
            var display = tf.Pulses.Select(p => new FactoryTfDisplayPoint { Round = tf, Pulse = p }).ToList();
            selection.SetPoints(display);
            Check(selection.Index == 0 && selection.Count == 2, "first pulse selected after extraction");
            Check(!selection.Move(-1) && selection.Index == 0, "previous does not underflow or wrap");
            Check(selection.Move(1) && selection.Index == 1 && !selection.Move(1), "next stops at last pulse");
            var snapshot = session.Rounds[0];
            var frozenDisplay = snapshot.Pulses.Select(p => new FactoryTfDisplayPoint { Round = snapshot, Pulse = p }).ToList();
            selection.SetPoints(frozenDisplay);
            Check(selection.Index == 1 && FactoryPulseSelection.Same(selection.Current, display[1]), "selection survives immutable cumulative clone");
            Check(selection.Select(display[0]) && selection.Index == 0, "TF activation finds cloned pulse by source and ADC index");
            var otherSource = Clone(tf); otherSource.SourceId += "-different";
            Check(!selection.Select(new FactoryTfDisplayPoint { Round = otherSource, Pulse = otherSource.Pulses[0] }), "old or different-source TF activation blocked");
            var later = Clone(tf); later.CapturedUtc = later.CapturedUtc.AddMilliseconds(1);
            Check(!FactoryPulseSelection.Same(display[0], new FactoryTfDisplayPoint { Round = later, Pulse = later.Pulses[0] }), "same source index but different capture time is not same pulse");
            Check(!FactoryPulseSelection.Same(display[0], display[1]), "pulse identity does not depend on coincident TF coordinates");
            FactoryPulseRawCache pulseCache = null;
            var directPulse = FactoryTfPulseReader.Read(display[0], round, ref pulseCache);
            Check(directPulse.SequenceEqual(tf.Pulses[0].SamplesV), "current pulse keeps signed native voltage without interpolation");
            var reloadedPulse = FactoryTfPulseReader.Read(frozenDisplay[0], round, ref pulseCache);
            Check(reloadedPulse.SequenceEqual(directPulse), "cumulative feature-only snapshot reloads correct original ADC window");
            var legacy=Clone(tf);legacy.PdFilterProtocol="";legacy.ChainKey=FactoryTfAnalysis.ChainKey(round.Capture,"");legacy.Validate();
            Throws(()=>session.Add(legacy),"old unfiltered features cannot mix with new high-pass features");
            var legacySamples=FactoryTfPulseReader.Read(new FactoryTfDisplayPoint {Round=legacy,Pulse=legacy.Pulses[0]},round,ref pulseCache);
            Check(legacySamples[0]==round.Capture.PdAdc[legacy.Pulses[0].StartSample]*round.Capture.PicoFullScaleV/round.Capture.MaximumAdc,"historical missing-filter record replays original method without silent refiltering");
            Check(tf.PdFilterProtocol==FactoryPdSignal.FilterId && reloadedPulse.SequenceEqual(tf.Pulses[0].SamplesV),"new feature identity and full-segment filtered reload agree");
            Check(round.Capture.PdAdc.SequenceEqual(frozen) && round.Capture.Settings.PdCalibrationPcPerV == 2100, "pulse navigation leaves original ADC and coefficient unchanged");
            var badWindow = Clone(tf); badWindow.Pulses[0].TimeMs += 1;
            Throws(() => FactoryTfPulseReader.Read(new FactoryTfDisplayPoint { Round = badWindow, Pulse = badWindow.Pulses[0] }, round, ref pulseCache), "inconsistent peak time cannot display another ADC window");
            var badRaw = Clone(tf); badRaw.Pulses[0].SamplesV = new double[1];
            Throws(() => FactoryTfPulseReader.Read(new FactoryTfDisplayPoint { Round = badRaw, Pulse = badRaw.Pulses[0] }, round, ref pulseCache), "incomplete raw pulse not displayed");
            selection.SetPoints(new System.Collections.Generic.List<FactoryTfDisplayPoint>());
            Check(selection.Current == null && selection.Index == -1 && !selection.Move(1), "new round clears stale selection and navigation");
            Throws(() => session.Add(fastTf), "different rate chain cannot mix");
            var bad = Clone(tf); bad.Simulation = false; bad.ChainKey = "HW|different";
            Throws(() => session.Add(bad), "SIM and hardware cannot mix");
            bad = Clone(tf); bad.Pulses[0].EquivalentTimeUs = Double.NaN; Throws(() => bad.Validate(), "invalid valid-point Teq rejected");
            bad = Clone(tf); bad.Pulses[0].Clipped = true; Throws(() => bad.Validate(), "clipped valid flag rejected");
            bad = Clone(tf); bad.CapturedUtc = DateTime.SpecifyKind(bad.CapturedUtc, DateTimeKind.Unspecified); Throws(() => bad.Validate(), "ambiguous capture timestamp rejected");
            for (int i = 1; i < 100; i++) { var extra = Clone(tf); extra.SourceId += "-" + i; session.Add(extra); }
            Check(session.RoundCount == 100, "explicit session retention limit");
            var overLimit = Clone(tf); overLimit.SourceId += "-overflow"; Throws(() => session.Add(overLimit), "no silent loss beyond session limit");
            session.Add(tf); Check(session.RoundCount == 100, "same-source replacement at retention limit");
            session.Clear(); Check(session.RoundCount == 0 && session.ChainKey == null, "clear only in-memory cumulative"); session.Add(tf);

            round.Metrics = FactoryAnalysis.Analyze(round); Program.SaveResult(round, round.DirectoryPath, false);
            byte[] original = File.ReadAllBytes(Path.Combine(round.DirectoryPath, "capture.bin"));
            string archive = FactoryDataArchive.Save(round, Path.Combine(workspace, "archive"), "设备/001", tf, session);
            Check(archive.Contains("设备_001") && archive.Contains("_Pressure_SIM_"), "safe serial directory and simulation marker");
            Check(new[] { "capture.bin", "settings.xml", "factory-settings.xml", "result.xml", "detected-80k.csv", "tf-current.xml", "tf-current.csv", "tf-cumulative.csv", "archive-manifest.xml", "SAVE_COMPLETE.txt" }.All(n => File.Exists(Path.Combine(archive, n))), "complete archive contents");
            Check(File.ReadAllBytes(Path.Combine(archive, "capture.bin")).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(round.DirectoryPath, "capture.bin")).SequenceEqual(original), "byte-for-byte capture copy, original preserved");
            var archivedCapture = BenchCapture.Load(archive);
            Check(archivedCapture.Settings.PdCalibrationPcPerV == 2100 && archivedCapture.PdAdc.SequenceEqual(frozen), "old capture coefficient and native samples frozen");
            var loaded = FactoryDataArchive.LoadTf(Path.Combine(archive, "tf-current.xml")); session.Add(loaded);
            Check(session.RoundCount == 1 && loaded.SourceId == tf.SourceId && loaded.Pulses.Count == 2, "archive/history import no duplicate source");
            Check(loaded.RawDataDirectory == archive && session.Rounds[0].RawDataDirectory == archive, "archive raw location retained without altering original source ID");
            pulseCache = null;
            var archivedPulse = FactoryTfPulseReader.Read(new FactoryTfDisplayPoint { Round = loaded, Pulse = loaded.Pulses[0] }, null, ref pulseCache);
            Check(archivedPulse.SequenceEqual(directPulse) && pulseCache.SourceId == loaded.SourceId, "history pulse read from matching native archive");
            Check(FactoryTfPulseReader.Read(new FactoryTfDisplayPoint { Round = loaded, Pulse = loaded.Pulses[1] }, null, ref pulseCache).Length == loaded.Pulses[1].EndSample - loaded.Pulses[1].StartSample + 1, "next historical pulse uses bounded one-round cache");
            var missingRaw = Clone(tf); missingRaw.SourceDirectory = Path.Combine(workspace, "missing-raw"); missingRaw.RawDataDirectory = missingRaw.SourceDirectory; pulseCache = null;
            Throws(() => FactoryTfPulseReader.Read(new FactoryTfDisplayPoint { Round = missingRaw, Pulse = missingRaw.Pulses[0] }, null, ref pulseCache), "missing raw history remains explicit, not fabricated");
            missingRaw.SourceDirectory = fastTf.SourceDirectory;
            Throws(() => FactoryTfPulseReader.Read(new FactoryTfDisplayPoint { Round = missingRaw, Pulse = missingRaw.Pulses[0] }, null, ref pulseCache), "other sampling-rate raw data rejected before display");
            Check(!FactoryTfPulseReader.Matches(tf, faster.Capture), "different native capture metadata cannot reuse current or cached ADC");
            var history = Directory.GetFiles(Path.Combine(archive, "tf-history"), "*.xml");
            Check(history.Length == 1 && FactoryDataArchive.LoadTf(history[0]).Pulses.Count == 2, "cumulative history is reloadable");
            FactoryArchiveManifest manifest;
            using (var stream = File.OpenRead(Path.Combine(archive, "archive-manifest.xml"))) manifest = (FactoryArchiveManifest)new XmlSerializer(typeof(FactoryArchiveManifest)).Deserialize(stream);
            Check(manifest.DeviceNumber == "设备/001" && manifest.PicoSerial == "SIM-PICO" && manifest.ArtSerial == "SIM-ART", "chosen device number separate from actual card identities");
            Check(manifest.CoefficientAtCapturePcPerV == 2100 && manifest.PicoRateHz == 1000000 && manifest.CapturedUtc == round.Capture.CapturedUtc && manifest.CapturedChinaTime.EndsWith("+08:00"), "physical units and capture timestamp snapshot");
            Check(manifest.Files.All(f => f.Length >= 0 && f.Sha256.Length == 64) && manifest.Files.Exists(f => f.Name == "tf-current.xml") && manifest.Files.Exists(f => f.Name == "detected-80k.csv"), "hash provenance includes raw and derived files");
            string second = FactoryDataArchive.Save(round, Path.Combine(workspace, "archive"), "设备/001", tf, session);
            Check(second != archive && Directory.Exists(archive) && Directory.Exists(second), "repeat save never overwrites archive");
            Check(FactoryDataArchive.SafeSegment("CON") == "设备_CON" && FactoryDataArchive.SafeSegment("13439/0066") == "13439_0066", "reserved and slashed serial normalization");
            Throws(() => FactoryDataArchive.SafeSegment(".."), "parent directory name rejected");
            Throws(() => FactoryDataArchive.SafeSegment(" "), "empty serial rejected");
            Throws(() => FactoryDataArchive.SafeSegment(new string('x', 81)), "unbounded serial rejected");
            round.Completed = false; Throws(() => FactoryDataArchive.Save(round, workspace, "abc", tf, session), "partial trial not archived complete"); round.Completed = true;
            string before = round.DirectoryPath; round.DirectoryPath = Path.Combine(workspace, "missing");
            Throws(() => FactoryDataArchive.Save(round, Path.Combine(workspace, "must-not-exist"), "abc", tf, session), "missing original not manufactured");
            Check(!Directory.Exists(Path.Combine(workspace, "must-not-exist")), "failed prerequisite causes no destination write"); round.DirectoryPath = before;
            var wrongTf = Clone(tf); wrongTf.SourceId += "-other-round"; Throws(() => FactoryDataArchive.Save(round, workspace, "abc", wrongTf, session), "other round TF cannot enter device archive");
            var wrongSession = new FactoryTfSession(); wrongSession.Add(fastTf); Throws(() => FactoryDataArchive.Save(round, workspace, "abc", tf, wrongSession), "different chain cumulative cannot enter archive");
            short beforeSample = round.Capture.PdAdc[4]; round.Capture.PdAdc[4]++;
            Throws(() => FactoryDataArchive.Save(round, workspace, "abc", tf, session), "in-memory ADC cannot silently disagree with original"); round.Capture.PdAdc[4] = beforeSample;
            Throws(() => FactoryDataArchive.SaveXml(tf, Path.Combine(archive, "tf-current.xml")), "existing TF never overwritten");
            string hostile = Path.Combine(workspace, "dtd.xml"); File.WriteAllText(hostile, "<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><FactoryTfResult>&x;</FactoryTfResult>", Encoding.UTF8);
            Throws(() => FactoryDataArchive.LoadTf(hostile), "DTD/external entity forbidden");
            return "FactoryPostProcessingTests: " + checks + " checks PASS; TF/archive fixtures only; HardwareAccessed=False; artifacts=" + workspace;
        }
        internal static FactoryResult Round(string directory, double rate = 1000000,double durationMs=20)
        {
            var s = new FactorySettings(); s.Input.RequestedPicoRateHz = rate; s.Input.CaptureDurationMs = durationMs; s.Input.RangeIndex = 6;
            s.Input.ArtSerial = "SIM-ART"; s.Input.PicoSerial = "SIM-PICO"; s.Input.OscillationCircuitConnected = false;
            var c = new BenchCapture { Settings = s.Input, Simulation = true, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO", Runtime = "POSTPROCESS-FAKE-no-drivers",
                CapturedUtc = new DateTime(2026, 10, 2, 3, 4, 5, DateTimeKind.Utc), ArtRateHz = 80000, PicoRateHz = rate, MaximumAdc = 32767, PicoFullScaleV = 1, HardwareBits = 14,
                PdAdc = new short[s.Input.RequestedPicoCount], VoltageRaw = new double[s.Input.ExpectedArtCount] };
            var random = new Random(6721); for (int i = 0; i < c.PdAdc.Length; i++) c.PdAdc[i] = (short)(100 + random.Next(-3, 4));
            c.ValidateCapture(); return new FactoryResult { Settings = s, Capture = c, Kind = FactoryRunKind.Pressure, Completed = true, ResidualSafe = true, DirectoryPath = directory, SafetyDetail = "offline fixture, no physical measurements" };
        }
        internal static void AddPulse(BenchCapture c, int center, double amplitude)
        {
            for (int i = Math.Max(0, center - 22); i <= Math.Min(c.PdAdc.Length - 1, center + 22); i++)
                c.PdAdc[i] = (short)Math.Max(-32767, Math.Min(32767, c.PdAdc[i] + amplitude * Math.Exp(-.5 * Math.Pow((i - center) / 4.0, 2))));
        }
        private static FactoryTfResult Clone(FactoryTfResult r)
        { using (var stream = new MemoryStream()) { var xml = new XmlSerializer(typeof(FactoryTfResult)); xml.Serialize(stream, r); stream.Position = 0; return (FactoryTfResult)xml.Deserialize(stream); } }
        private static void Check(bool okay, string name) { if (!okay) throw new InvalidOperationException("Post-processing test: " + name); checks++; }
        private static void Near(double value, double expected, double tolerance, string name) { Check(Math.Abs(value - expected) <= tolerance, name + " actual=" + value); }
        private static void Throws(Action action, string name) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, name); }
    }
}
