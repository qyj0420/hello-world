using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

internal static class CalibrationPersistenceTimingSmoke
{
    private static int checks;
    private static Type type;
    private static string output;
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            var assembly = Assembly.LoadFrom(Path.Combine(args[0], "modules", "CalibrationTest", "Xiongxin.OWTS.CalibrationTest.dll"));
            type = assembly.GetType("HVX.Controls.CalibrationProgramControl", true);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (int language in new[] { 0, 1, 18 }) Exercise(language);
            TestUnreliableAndManual(); TestSaveFailure(); TestSimulationDisplayClear(); TestSimulationOrigin(); TestMergedSaveAction();
            Console.WriteLine("PASS " + checks + " calibration timing, persistence and phase-isolation checks"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TestMergedSaveAction()
    {
        using(Control c=New(Project("merged-save-action"),true,false))
        {
            Call(c,"SetSelectedLevel","5nC");
            Check(!(bool)Call(c,"RequestSaveOrDelete"),"No samples: merged action disabled");
            Call(c,"SetPhaseParameters",-1,170d,163d,false,false);
            Call(c,"SetPhaseWaveform",-1,"5nC",Wave(620,1,.4),0d,4e-9);
            Check(!(bool)Get(c,"SelectedCalibrationSaved"),"New waveform displays save action");
            Check((bool)Call(c,"SaveCalibrationFile"),"Merged action fixture saved");
            Check((bool)Get(c,"SelectedCalibrationSaved"),"Saved waveform displays delete action");
            Check((bool)Call(c,"AutoSaveQualifiedCalibration"),"Qualified signal can be saved automatically with read-back verification");
            Call(c,"SetPhaseWaveform",-1,"5nC",Wave(620,1,.5),0d,4e-9);
            Check(!(bool)Get(c,"SelectedCalibrationSaved"),"New acquisition restores save action");
            Call(c,"SetPhaseWaveform",-1,"5nC",new double[100],0d,4e-9);
            Check(!(bool)Call(c,"AutoSaveQualifiedCalibration"),"Flat signal never auto-saved");
        }
        using(Control c=New(Project("live-auto-save-guard"),false,false))
            Check(!(bool)Call(c,"AutoSaveQualifiedCalibration"),"Unverified live calibration never auto-saved");
    }
    private static void TestSimulationDisplayClear()
    {
        string project = Project("source-switch-clear");
        using (Control c = New(project, true, false))
        {
            Call(c, "SetPhaseParameters", -1, 170d, 163d, false, false);
            Call(c, "SetPhaseWaveform", -1, "5nC", Wave(620, 1, .4), 0d, 4e-9);
            Check(!(bool)Call(c, "ClearSimulationDisplay") && (bool)Get(c, "HasCalibrationData"), "source clear rejects unsaved data");
            Check((bool)Call(c, "SaveCalibrationFile"), "source clear fixture saved");
            string path = (string)Get(c, "CalibrationFilePath"); byte[] saved = File.ReadAllBytes(path);
            Set(c, "ActionHandshakeEnabled", true);
            type.GetField("actionBusy", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(c, true);
            Check(!(bool)Call(c, "ClearSimulationDisplay") && (bool)Get(c, "HasCalibrationData"), "source clear rejects busy");
            type.GetField("actionBusy", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(c, false);
            type.GetField("actionPending", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(c, true);
            Check(!(bool)Call(c, "ClearSimulationDisplay") && (bool)Get(c, "HasCalibrationData"), "source clear rejects pending");
            type.GetField("actionPending", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(c, false);
            Check((bool)Call(c, "ClearSimulationDisplay"), "clean idle simulation display cleared");
            Check(!(bool)Get(c, "HasCalibrationData") && !(bool)Get(c, "HasUnsavedCalibrationChanges"), "source clear does not manufacture save prompt");
            Check(saved.SequenceEqual(File.ReadAllBytes(path)), "source clear preserves disk archive byte for byte");
            Near((double)Call(c, "GetPhaseCableLengthMeters", -1), 170, 1e-9, "source clear retains cable geometry");
            Near((double)Call(c, "GetPhaseVelocityMPerUs", -1), 163, 1e-9, "source clear retains phase velocity");
            Check((long)Get(c, "CalibrationSavedSequence") == 1, "source clear does not claim another save");
            Check((bool)Call(c, "LoadCalibrationFile") && (bool)Get(c, "HasCalibrationData"), "saved source can explicitly reload");
        }
        using (Control real = New(project, false, false))
            Check(!(bool)Call(real, "ClearSimulationDisplay"), "live display cannot be cleared through simulation API");
    }
    private static void TestSimulationOrigin()
    {
        string project = Project("simulation-origin");
        using (Control c = New(project, true, false))
        {
            Call(c, "SetPhaseParameters", -1, 170d, 170d, false, false);
            Call(c, "SetPhaseWaveform", -1, "5nC", Wave(620, 1, .4), 0d, 4e-9);
            Call(c, "SetPhaseNoiseWaveform", -1, new double[64], 0d, 4e-9);
            Call(c, "SetPhaseNoiseWaveform", -1, new double[0], 0d, 4e-9);
            Check((bool)Call(c, "SaveCalibrationFile"), "origin fixture initially saved");
            string file = (string)Get(c, "CalibrationFilePath"); byte[] version2 = File.ReadAllBytes(file);
            Check(BitConverter.ToInt32(version2, 44) == 2 && version2[version2.Length - 1] == 0, "version 2 appends bounded origin field");
            Check((bool)Call(c, "ClearSimulationDisplay"), "origin fixture display clear");
            byte[] version1 = version2.Take(version2.Length - 1).ToArray();
            Array.Copy(BitConverter.GetBytes(version1.Length - 44), 0, version1, 8, 4);
            Array.Copy(BitConverter.GetBytes(1), 0, version1, 44, 4); Rehash(version1); File.WriteAllBytes(file, version1);
            Check((bool)Call(c, "LoadCalibrationFile"), "version 1 remains loadable");
            Check((string)Get(c, "SimulationDataOrigin") == "SIM: legacy source not recorded", "legacy origin remains explicitly unknown");
            Check(!(bool)Get(c, "HasUnsavedCalibrationChanges") && (bool)Get(c, "IsCalibrationSavedValid"), "legacy canonical migration is not an edit");
            string origin = "SIM: profile=rdfz-jmf-v1; 人大附中215-金茂府4#212; derived for current cable geometry, not measured";
            Check((bool)Call(c, "SetSimulationDataOrigin", origin) && (bool)Get(c, "HasUnsavedCalibrationChanges"), "origin update participates in dirty state");
            Check(!(bool)Call(c, "SetSimulationDataOrigin", "LIVE: no") && (string)Get(c, "SimulationDataOrigin") == origin, "origin cannot claim real acquisition");
            Check(!(bool)Call(c, "SetSimulationDataOrigin", "SIM:" + new string('x', 4093)) && (string)Get(c, "SimulationDataOrigin") == origin, "oversized origin rejected without replacing old value");
            Check(!(bool)Call(c, "SetSimulationDataOrigin", "SIM: bad\0value"), "control character origin rejected");
            Check((bool)Call(c, "SaveCalibrationFile"), "reference origin stored and verified");
            using (Control loaded = New(project, true, false))
            {
                Check((string)Get(loaded, "SimulationDataOrigin") == origin, "reference origin survives fresh load exactly");
                Check(!(bool)Get(loaded, "HasUnsavedCalibrationChanges"), "reloaded origin stays clean");
                Check((bool)Call(loaded, "ClearSimulationDisplay") && (string)Get(loaded, "SimulationDataOrigin") == "", "source switch clears only displayed origin");
                Check((bool)Call(loaded, "LoadCalibrationFile") && (string)Get(loaded, "SimulationDataOrigin") == origin, "explicit reload restores archived origin");
            }
        }
        using (Control live = New(project, false, false))
            Check(!(bool)Call(live, "SetSimulationDataOrigin", "SIM: blocked") && (string)Get(live, "SimulationDataOrigin") == "", "simulation origin API cannot relabel live data");
    }
    private static void Exercise(int language)
    {
        string project = Project("project-" + language); using (Control c = New(project, true, false))
        {
            Set(c, "LanguageIndex", language); int savedEvents = 0;
            type.GetEvent("CalibrationSaved").AddEventHandler(c, new EventHandler(delegate { savedEvents++; }));
            Check(!(bool)Get(c, "HasCalibrationData"), "initial empty");
            Check(!(bool)Get(c, "HasUnsavedCalibrationChanges"), "initial clean");
            double[] samples = Wave(620, 1, -0.4);
            Call(c, "SetPhaseParameters", -1, 170d, 170d, false, false);
            Call(c, "SetPhaseWaveform", -1, "5nC", samples, 0d, 4e-9);
            int peakI = (int)Call(c, "GetIncidentPeakIndex", -1, "5nC"), peakR = (int)Call(c, "GetReflectionPeakIndex", -1, "5nC");
            Check(Math.Abs(peakI - 120) <= 2 && Math.Abs(peakR - 620) <= 2, "physical pair not far noise");
            double peakVelocity = (double)Call(c, "GetMeasuredVelocityMPerUs", -1, "5nC");
            Near(peakVelocity, 170, 1, "peak velocity");
            double[] rawBefore = samples.ToArray();
            var decayBefore = (double[])Call(c, "GetFittedDecayCurve", -1, "5nC");
            Check(decayBefore.Length > 0, "decay exists for opposite polarity magnitudes");
            Set(c, "UseFirstArrivalTiming", true);
            int firstI = (int)Call(c, "GetIncidentFirstArrivalIndex", -1, "5nC"), firstR = (int)Call(c, "GetReflectionFirstArrivalIndex", -1, "5nC");
            Check(firstI >= 0 && firstI < peakI && firstR > firstI && firstR < peakR, "first arrivals precede peaks");
            Near((double)Call(c, "GetMeasuredVelocityMPerUs", -1, "5nC"), 340d / ((firstR - firstI) * 4e-3), 1e-8, "first-arrival velocity");
            Check(Math.Abs((double)Call(c, "GetMeasuredVelocityMPerUs", -1, "5nC") - peakVelocity) > 1, "timing not silently peak");
            Near((double)Call(c, "GetCalculatedDistanceMeters", -1, "5nC"), (firstR - firstI) * 4e-9 * 170 * 1e6 / 2, 1e-9, "first distance");
            Check(((double[])Call(c, "GetFittedDecayCurve", -1, "5nC")).SequenceEqual(decayBefore), "decay unchanged by timing mode");
            Call(c, "SetPhaseBandwidth", -1, true, 35d);
            Check((int)Call(c, "GetIncidentFirstArrivalIndex", -1, "5nC") == firstI && (int)Call(c, "GetReflectionFirstArrivalIndex", -1, "5nC") == firstR, "display filtering preserves raw first-arrival evidence: " + firstI + "," + firstR + " => " + Call(c, "GetIncidentFirstArrivalIndex", -1, "5nC") + "," + Call(c, "GetReflectionFirstArrivalIndex", -1, "5nC") + " peaks " + Call(c, "GetIncidentPeakIndex", -1, "5nC") + "," + Call(c, "GetReflectionPeakIndex", -1, "5nC"));
            Call(c, "SetPhaseBandwidth", -1, false, 35d);
            Check(samples.SequenceEqual(rawBefore), "caller samples unchanged");
            Check((bool)Get(c, "HasUnsavedCalibrationChanges"), "capture dirty");
            Check(!String.IsNullOrEmpty((string)Call(c, "GetCalibrationReadiness", "ALL", true, true)), "unsaved not ready");
            Check((bool)Call(c, "SaveCalibrationFile"), "actual save " + Get(c, "LastCalibrationError"));
            Check((long)Get(c, "CalibrationSavedSequence") == 1, "successful save advances sequence once");
            Check(savedEvents == 1, "one post-readback saved event");
            string path = (string)Get(c, "CalibrationFilePath"); Check(File.Exists(path), "file exists");
            byte[] firstSave = File.ReadAllBytes(path);
            Check((bool)Get(c, "IsCalibrationSavedValid") && !(bool)Get(c, "HasUnsavedCalibrationChanges"), "saved readback valid clean");
            Check((string)Call(c, "GetCalibrationReadiness", "ALL", true, true) == "", "sim ALL ready");
            Check((string)Call(c, "GetCalibrationReadiness", "A", true, true) == "", "equal-length ALL serves A without rewriting source phase");
            Check((string)Call(c, "GetCalibrationReadiness", "ALL", false, true) != "", "simulation never live ready");
            Check((string)Call(c, "GetCalibrationReadiness", "ALL", true, false) != "", "timing mismatch rejected");
            Call(c, "SetPhaseWaveform", -1, "100nC", Wave(645, 1, .5), 0d, 4e-9);
            Call(c, "SetPhaseParameters", 0, 100d, 161d, false, false);
            Call(c, "SetPhaseWaveform", 0, "5nC", Wave(430, -1, -.45), 0d, 4e-9);
            Array rows = (Array)Call(c, "GetPhaseVelocityReview", -1);
            Check(rows.Length == 2, "same-phase levels only");
            double suggested = (double)Call(c, "GetRecommendedVelocityMPerUs", -1);
            double[] measured = rows.Cast<object>().Select(row => (double)row.GetType().GetProperty("MeasuredVelocity").GetValue(row, null)).ToArray();
            Near(suggested, measured.Average(), 1e-9, "median recommendation");
            Check((bool)Call(c, "RequiresPhaseVelocityReview", -1), "different level velocities need review");
            CaptureReview(c, language);
            string token = (string)Call(c, "GetPhaseVelocityReviewToken", -1);
            Check(!(bool)Call(c, "ApplyPhaseVelocity", -1, suggested, token, false), "no apply without confirmation");
            Near((double)Call(c, "GetPhaseVelocityMPerUs", -1), 170, 1e-9, "cancel leaves velocity");
            Check((bool)Call(c, "ApplyPhaseVelocity", -1, suggested, token, true), "confirmed apply");
            Check(!(bool)Call(c, "RequiresPhaseVelocityReview", -1), "confirmed common velocity resolves review");
            Near((double)Call(c, "GetPhaseVelocityMPerUs", 0), 161, 1e-9, "A unaffected by ALL apply");
            Check(!(bool)Call(c, "ApplyPhaseVelocity", -1, suggested + 1, token, true), "stale review rejected");
            Check((bool)Call(c, "SaveCalibrationFile"), "save revisions");
            Check(Directory.GetFiles(Path.GetDirectoryName(path), "*.bak").Length > 0, "previous version backup");
            Check(!(bool)Get(c, "HasUnsavedCalibrationChanges"), "revised saved clean");
            using (Control loaded = New(project, true, true))
            {
                Check((bool)Get(loaded, "HasCalibrationData"), "auto reload simulation");
                Check(!(bool)Get(loaded, "HasUnsavedCalibrationChanges"), "reload same canonical content clean");
                Near((double)Call(loaded, "GetPhaseVelocityMPerUs", -1), suggested, 1e-9, "adopted value reloaded");
                Array reloadedRows = (Array)Call(loaded, "GetPhaseVelocityReview", -1);
                for (int i = 0; i < rows.Length; i++) Near(ReadRow(reloadedRows.GetValue(i), "OriginalMeasuredVelocity"), ReadRow(rows.GetValue(i), "OriginalMeasuredVelocity"), 1e-9, "original measurement retained");
                Check((string)Call(loaded, "GetCalibrationReadiness", "ALL", true, true) == "", "reload ready");
            }
            string moved = Project("moved-" + language); string movedFile = Path.Combine(moved, "Calibration", "Simulation", "calibration.hvcsim"); Directory.CreateDirectory(Path.GetDirectoryName(movedFile)); File.Copy(path, movedFile);
            using (Control movedControl = New(moved, true, true)) Check((bool)Get(movedControl, "IsCalibrationSavedValid"), "portable across folder identity");
            byte[] good = File.ReadAllBytes(path); File.Delete(path);
            Check(!(bool)Get(c, "IsCalibrationSavedValid") && (bool)Get(c, "HasUnsavedCalibrationChanges"), "deleted file not saved");
            File.WriteAllBytes(path, good); byte[] corrupt = good.ToArray(); corrupt[corrupt.Length - 1] ^= 1; File.WriteAllBytes(path, corrupt);
            Check(!(bool)Get(c, "IsCalibrationSavedValid") && (bool)Get(c, "HasUnsavedCalibrationChanges"), "corrupt file not saved");
            Check(!(bool)Call(c, "LoadCalibrationFile"), "corrupt load refused with active data");
            Check((bool)Get(c, "HasCalibrationData"), "failed load retains data");
            File.WriteAllBytes(path, good);
            Check((bool)Get(c, "IsCalibrationSavedValid"), "restored archive validation");
            File.AppendAllText(Path.Combine(project, "ITEM.in"), " metadata changed");
            Check(!(bool)Get(c, "IsCalibrationSavedValid"), "project identity recheck");
            using (Control mismatch = New(project, true, true)) Check(!(bool)Get(mismatch, "HasCalibrationData"), "different project metadata rejects load");
            File.WriteAllText(Path.Combine(project, "ITEM.in"), "fixture project identity");
            Call(c, "SetPhaseTimingCursors", -1, "5nC", firstI * 4e-9, (firstR + 1) * 4e-9);
            bool rejected = false; try { Set(c, "SimulationMode", false); } catch (TargetInvocationException) { rejected = true; }
            Check(rejected && (bool)Get(c, "SimulationMode"), "dirty mode switch rejected");
            Set(c, "EmbeddedMode", true); c.Size = new Size(1280, 720);
            using (var bitmap = new Bitmap(c.Width, c.Height)) { c.CreateControl(); c.DrawToBitmap(bitmap, c.ClientRectangle); bitmap.Save(Path.Combine(output, "calibration-timing-" + language + ".png")); }
            Call(c, "DiscardCalibrationChanges"); Set(c, "SimulationMode", false);
            Check(!(bool)Get(c, "HasCalibrationData"), "mode switch never relabels data");
            Check(!(bool)Call(c, "SaveCalibrationFile") && ((string)Get(c, "LastCalibrationError")).Length > 5, "Empty live save fails explicitly without manufacturing a record");
            Call(c,"SetPhaseWaveform",-1,"5nC",Wave(620,1,.4),0d,4e-9);
            Check(!(bool)Call(c,"SaveCalibrationFile"),"Injected waveform without acquisition provenance is not a formal live calibration");
        }
    }
    private static void TestUnreliableAndManual()
    {
        using (Control c = New(Project("unreliable"), true, true))
        {
            Call(c, "SetPhaseParameters", -1, 170d, 170d, false, false);
            double[] y = Wave(620, 1, .4); for (int i = 0; i < 120; i++) y[i] = 3 + .1 + .9 * i / 120;
            Call(c, "SetPhaseWaveform", -1, "5nC", y, 0d, 4e-9);
            Check((int)Call(c, "GetIncidentFirstArrivalIndex", -1, "5nC") == -1, "truncated arrival unavailable");
            Check(Double.IsNaN((double)Call(c, "GetMeasuredVelocityMPerUs", -1, "5nC")), "no silent peak fallback");
            Check(((string)Call(c, "GetTimingStatus", -1, "5nC")).Length > 0, "explain unavailable arrival");
            // Supersedes the old in-range noise-distance rejection at this
            // location. Its real r42 FAIL receipt is retained; manual selection
            // is an operator choice, not the automatic arrival detector.
            TestManualReflectionContract(c,y);
            Call(c, "SetPhaseWaveform", -1, "5nC", Wave(620, 1, .4), 0d, 4e-9);
            double[] decay = (double[])Call(c, "GetFittedDecayCurve", -1, "5nC");
            Check((bool)Call(c, "SetPhaseTimingCursors", -1, "5nC", 92 * 4e-9, 555 * 4e-9), "manual onset accepted without snap");
            Near((double)Call(c, "GetMeasuredVelocityMPerUs", -1, "5nC"), 340d / (463 * .004), 1e-8, "manual timing used");
            Check(((double[])Call(c, "GetFittedDecayCurve", -1, "5nC")).SequenceEqual(decay), "manual timing keeps decay peaks");
            Check((bool)Call(c, "SaveCalibrationFile"), "manual arrival saved");
            string file = (string)Get(c, "CalibrationFilePath"); byte[] valid = File.ReadAllBytes(file);
            Call(c, "DiscardCalibrationChanges"); Check((bool)Get(c, "HasCalibrationData"), "discard reloads saved calibration");
            File.WriteAllBytes(file, valid.Take(37).ToArray()); Call(c, "DiscardCalibrationChanges");
            Check(!(bool)Call(c, "LoadCalibrationFile") && !(bool)Get(c, "HasCalibrationData"), "truncated file rejected atomically");
            byte[] invalidParameter = valid.ToArray(); Array.Copy(BitConverter.GetBytes(Double.NaN), 0, invalidParameter, 44 + 51, 8); Rehash(invalidParameter); File.WriteAllBytes(file, invalidParameter);
            Check(!(bool)Call(c, "LoadCalibrationFile") && !(bool)Get(c, "HasCalibrationData"), "nonfinite payload rejected even with valid digest");
            byte[] excessiveLevels = valid.ToArray(); Array.Copy(BitConverter.GetBytes(Int32.MaxValue), 0, excessiveLevels, 44 + 77, 4); Rehash(excessiveLevels); File.WriteAllBytes(file, excessiveLevels);
            Check(!(bool)Call(c, "LoadCalibrationFile") && !(bool)Get(c, "HasCalibrationData"), "record count capped before allocation");
            File.WriteAllBytes(file, valid); Check((bool)Call(c, "LoadCalibrationFile"), "manual saved reload");
            Near((double)Call(c, "GetMeasuredVelocityMPerUs", -1, "5nC"), 340d / (463 * .004), 1e-8, "manual timing persisted exactly");
            Call(c, "ClearPhaseWaveform", -1, "5nC"); Check((bool)Get(c, "HasUnsavedCalibrationChanges"), "last record deletion dirty");
            Check((bool)Call(c, "SaveCalibrationFile"), "empty state saved prevents resurrection");
            Check((string)Call(c, "GetCalibrationReadiness", "ALL", true, true) != "", "empty saved not calibration ready");
            Call(c, "SetPhaseWaveform", -1, "5nC", Wave(645, 1, .4), 0d, 4e-9);
            Array recaptured = (Array)Call(c, "GetPhaseVelocityReview", -1);
            Near(ReadRow(recaptured.GetValue(0), "OriginalMeasuredVelocity"), (double)Call(c, "GetMeasuredVelocityMPerUs", -1, "5nC"), 1e-9, "new acquisition replaces original observation for that level");
        }
    }
    private static void TestSaveFailure()
    {
        string p = Project("save-failure"); File.WriteAllText(Path.Combine(p, "Calibration"), "blocking file");
        using (Control c = New(p, true, true))
        {
            Call(c, "SetPhaseParameters", -1, 170d, 170d, false, false); Call(c, "SetPhaseWaveform", -1, "5nC", Wave(620, 1, .4), 0d, 4e-9);
            Check(!(bool)Call(c, "SaveCalibrationFile"), "filesystem failure reported"); Check((bool)Get(c, "HasUnsavedCalibrationChanges"), "failed save retains dirty");
            Check((long)Get(c, "CalibrationSavedSequence") == 0, "failure never emits success sequence");
        }
    }
    private static object PrivateValue(object value,string name)
    {
        const BindingFlags all=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        var property=value.GetType().GetProperty(name,all);
        return property!=null?property.GetValue(value,null):value.GetType().GetField(name,all).GetValue(value);
    }
    private static void TestManualReflectionContract(Control c,double[] samples)
    {
        const BindingFlags all=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        object phase=type.GetMethod("GetPhaseStateNoLock",all).Invoke(c,new object[]{-1});
        var analyses=(System.Collections.IDictionary)PrivateValue(phase,"Analyses");
        object analysis=analyses["5nC"];
        int incident=(int)Call(c,"GetIncidentPeakIndex",-1,"5nC"),reflection=(int)Call(c,"GetReflectionPeakIndex",-1,"5nC");
        double incidentTime=incident*4e-9,coefficient=5000d/(double)PrivateValue(analysis,"IncidentMagnitude");
        double[] original=samples.ToArray();
        Check((bool)Call(c,"SetPhaseTimingCursors",-1,"5nC",incidentTime,5e-6),"operator chooses in-range distant reflection without moving original incident");
        var markers=(System.Collections.IDictionary)PrivateValue(phase,"Markers");
        Near((double)PrivateValue(markers["5nC"],"StartTimeSeconds"),incidentTime,1e-12,"manual reflection keeps fixed original incident");
        Near((double)PrivateValue(markers["5nC"],"DistanceMarkerSeconds"),5e-6,1e-12,"reflection uses actual pointer time without snapping");
        Near((double)Call(c,"GetCalculatedDistanceMeters",-1,"5nC"),(5e-6-incidentTime)*170e6/2,1e-9,"manual distance uses actual selected difference");
        Near((double)Call(c,"GetPhaseVelocityMPerUs",-1),170d,1e-12,"manual reflection does not change adopted velocity");
        Check((int)Call(c,"GetIncidentPeakIndex",-1,"5nC")==incident&&(int)Call(c,"GetReflectionPeakIndex",-1,"5nC")==reflection,"manual timing preserves original physical peak pair");
        Near(5000d/(double)PrivateValue(analyses["5nC"],"IncidentMagnitude"),coefficient,1e-12,"incident coefficient reference unchanged by timing edit");
        Check(((double[])Call(c,"GetDisplayDecayAnchors",-1,"5nC")).Length==0&&(string)Get(c,"DisplayDecayStatus")!="Ready","quiet selected reflection cannot manufacture display attenuation");
        foreach(var invalid in new[]{new[]{Double.NaN,5e-6},new[]{incidentTime,Double.PositiveInfinity},new[]{incidentTime,incidentTime-4e-9},new[]{-8e-9,5e-6},new[]{incidentTime,8.004e-6}})
            Check(!(bool)Call(c,"SetPhaseTimingCursors",-1,"5nC",invalid[0],invalid[1]),"nonfinite/reversed/outside manual timing rejected");
        Near((double)PrivateValue(markers["5nC"],"DistanceMarkerSeconds"),5e-6,1e-12,"rejected edit keeps selected reflection");
        var waveforms=(System.Collections.IDictionary)PrivateValue(phase,"Waveforms");
        Check(samples.SequenceEqual(original)&&((double[])PrivateValue(waveforms["5nC"],"Samples")).SequenceEqual(original),"manual and invalid edits preserve caller and stored original samples");
    }
    private static double[] Wave(int reflection, double a, double b)
    {
        var y = new double[2000]; for (int i = 0; i < y.Length; i++) y[i] = 3 + .00001 * Math.Sin(i * .49);
        Pulse(y, 120, 30, 18, a); Pulse(y, reflection, 70, 24, b); Pulse(y, 1400, 12, 12, 2); return y;
    }
    private static void Pulse(double[] y, int peak, int before, int after, double a)
    { for (int i = Math.Max(0, peak - before); i <= Math.Min(y.Length - 1, peak + after); i++) y[i] += a * (i <= peak ? (double)(i - peak + before) / before : (double)(peak + after - i) / after); }
    private static string Project(string name) { string p = Path.Combine(output, name); Directory.CreateDirectory(p); File.WriteAllText(Path.Combine(p, "ITEM.in"), "fixture project identity"); File.WriteAllText(Path.Combine(p, "CABLE.in"), "0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n"); return p; }
    private static void Rehash(byte[] envelope) { using (var sha = SHA256.Create()) { byte[] digest = sha.ComputeHash(envelope, 44, envelope.Length - 44); Array.Copy(digest, 0, envelope, 12, 32); } }
    private static void CaptureReview(Control c, int language)
    {
        bool captured = false; Exception failure = null;
        using (var timer = new Timer { Interval = 60 })
        {
            timer.Tick += delegate {
                Form dialog = Application.OpenForms.Cast<Form>().LastOrDefault(); if (dialog == null) return;
                timer.Stop();
                try
                {
                    Check(dialog.Icon != null, "review dialog brand icon");
                    Check(dialog.Text.Contains("ALL"), "review identifies exact phase scope");
                    Check(dialog.AutoScaleMode == AutoScaleMode.Dpi, "review DPI adaptation");
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(output, "velocity-review-" + language + ".png")); }
                    foreach (Control control in Descendants(dialog))
                        if (control is DataGridView || control is Button || control is Label)
                        { Rectangle bounds = dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle)); Check(bounds.Right <= dialog.ClientSize.Width + 1 && bounds.Bottom <= dialog.ClientSize.Height + 1, "review content inside dialog: " + control.GetType().Name); }
                    foreach (Button button in Descendants(dialog).OfType<Button>())
                    {
                        Size needed = TextRenderer.MeasureText(button.Text, button.Font);
                        Check(button.Width >= needed.Width + button.Padding.Horizontal, "review action caption fits");
                    }
                    captured = true;
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
            };
            timer.Start(); Check(!(bool)Call(c, "ShowPhaseVelocityReview"), "review cancel does not apply"); timer.Stop();
        }
        if (failure != null) throw failure; Check(captured, "review dialog rendered");
    }
    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (Control item in Descendants(child)) yield return item; } }
    private static Control New(string p, bool simulation, bool arrival) { var c = (Control)Activator.CreateInstance(type); Set(c, "UseFirstArrivalTiming", arrival); Set(c, "ProjectPath", p); Set(c, "SimulationMode", simulation); return c; }
    private static double ReadRow(object row, string property) { return (double)row.GetType().GetProperty(property).GetValue(row, null); }
    private static object Get(object o, string name) { return type.GetProperty(name).GetValue(o, null); }
    private static void Set(object o, string name, object value) { type.GetProperty(name).SetValue(o, value, null); }
    private static object Call(object o, string method, params object[] args) { return type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance).Invoke(o, args); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("Check failed: " + message); }
    private static void Near(double a, double b, double tolerance, string message) { Check(!Double.IsNaN(a) && Math.Abs(a - b) <= tolerance, message + " actual=" + a + " expected=" + b); }
}
