using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

// Managed controls and read-only formal CAL only. No product/worker/native SDK.
static class BrowserArchivedDecaySmoke
{
    const BindingFlags B = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static Assembly browser;
    static object Get(object o, string n) { var p = o.GetType().GetProperty(n, B); return p != null ? p.GetValue(o, null) : o.GetType().GetField(n, B).GetValue(o); }
    static void Set(object o, string n, object v) { var p = o.GetType().GetProperty(n, B); if (p != null) p.SetValue(o, v, null); else o.GetType().GetField(n, B).SetValue(o, v); }
    static object Call(object o, string n, params object[] a) { return o.GetType().GetMethods(B).Single(m => m.Name == n && m.GetParameters().Length == a.Length).Invoke(o, a); }
    static void Check(bool ok, string why) { checks++; if (!ok) throw new InvalidOperationException(why); }
    static void Near(double a, double b, string why) { Check(Math.Abs(a - b) < 1e-10, why); }
    static string Hash(string p) { using (var h = SHA256.Create()) using (var s = File.OpenRead(p)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", ""); }
    static object New(string n) { return Activator.CreateInstance(browser.GetType(n, true), true); }
    static double[] Anchors(object c) { return (double[])Call(c, "DecayAnchors"); }
    static void Actual(string root, string qa, bool old)
    {
        string file = Path.Combine(root, "ALL_5nC.cal"), before = Hash(file);
        Check(before == "2B717210174AFAF1BDF168193DB0D9662ABBF43173E13830B006F9554AA9FAB7", "Exact reported CAL bytes, not another record");
        var record = New("HVX.Controls.BrowserRecord"); Set(record, "Path", file); Set(record, "Kind", "cal"); Set(record, "Name", "ALL_5nC");
        ((Dictionary<string, string>)Get(record, "Metadata"))["LiveProjectPath"] = root;
        var reader = browser.GetType("HVX.Controls.BrowserReader", true);
        object wave = reader.GetMethod("LoadWaveform", B, null, new[] { record.GetType() }, null).Invoke(null, new[] { record });
        var storage = browser.GetType("HVX.CalibrationStorage.LiveCalibrationRecord", true);
        object archived = storage.GetMethod("Load", B).Invoke(null, new object[] { root, file, true });
        Check((bool)Get(archived, "HasMeasuredReflection"), "Reported archive has measured reflection");
        Check((int)Get(archived, "IncidentPeakIndex") == 400 && (int)Get(archived, "ReflectionPeakIndex") == 658, "Original peak identity retained");
        double[] raw = (double[])Get(archived, "Average"), rawCopy = (double[])raw.Clone();
        using (var host = (Control)New("HVX.Controls.ProjectDataBrowserControl"))
        {
            Set(host, "selectedRecord", record); Set(host, "selectedWave", wave);
            Call(host, "RenderWave");
            var c = (Control)Get(host, "calibration");
            Near((double)Get(c, "IncidentCursorX"), 3.136, "Actual browser timing remains first arrival");
            Near((double)Get(c, "ReflectionCursorX"), 5.2, "Actual browser reflection timing unchanged");
            if (old)
            {
                Check(Anchors(c) == null, "Protected 07 reproduces missing curve despite valid archived peaks");
                Console.WriteLine("OLD07_REPRODUCED_PRERINGING_ANCHOR_MISMATCH=true");
            }
            else
            {
                Check(c.GetType().GetMethod("SetDecayPulsePeakReferences", B) != null, "Explicit archive-identity display route exists");
                foreach (double cutoff in new[] { 60d, 2.3d, 60d })
                {
                    double[] y = (double[])browser.GetType("HVX.SignalProcessing.PdLinearLowPass", true).GetMethod("Apply", B).Invoke(null, new object[] { raw, 8e-9, cutoff, 129 });
                    Set(wave, "Y", y); Call(host, "RenderWave");
                    double[] a = Anchors(c);
                    Check(a != null && a.Length == 6, "Actual formal render has damping at " + cutoff + "MHz");
                    Check(a[1] > a[4] && a[3] > a[5], "Actual positive physical packet, not absolute negative trough");
                    Check(a[3] - a[5] < a[1] - a[4], "Actual signed pair attenuates");
                    Check(Math.Abs(a[0] - 3.2) <= 43 * .008 && Math.Abs(a[2] - 5.264) <= 43 * .008, "Bounded same original packet identity");
                    Near((double)Get(c, "IncidentCursorX"), 3.136, "Filtering never shifts incident cursor");
                    Near((double)Get(c, "ReflectionCursorX"), 5.2, "Filtering never shifts reflection cursor");
                    Check(ReferenceEquals(a, Anchors(c)), "Same render uses cached anchors");
                    Console.WriteLine("ACTUAL_CUTOFF=" + cutoff + "; ANCHORS=" + String.Join(",", a.Select(v => v.ToString("R"))));
                    Call(c, "SavePng", Path.Combine(qa, "actual-" + cutoff + "MHz.png"), 1200, 650);
                    Near((double)Get(archived, "CoefficientPcPerV"), 2304.8904422400733, "Display leaves coefficient untouched");
                }
            }
        }
        Check(raw.SequenceEqual(rawCopy), "Source raw waveform not rewritten");
        Check(Hash(file) == before, "Source archived CAL unchanged");
    }
    static double[] Times() { return Enumerable.Range(0, 1001).Select(n => n * .008).ToArray(); }
    static double[] Wave(double sign, double reflection)
    {
        return Enumerable.Range(0, 1001).Select(n => sign * (Math.Abs(n - 200) < 20 ? Math.Exp(-Math.Pow((n - 200) / 7d, 2)) : 0) +
            sign * reflection * (Math.Abs(n - 500) < 20 ? Math.Exp(-Math.Pow((n - 500) / 7d, 2)) : 0)).ToArray();
    }
    static void Synthetic()
    {
        foreach (double sign in new[] { 1d, -1d }) foreach (double reflected in new[] { .4, -.4, 2d, 0d })
        using (var c = (Control)New("CalibrationWaveform.CalibrationWaveformControl"))
        {
            var y = Wave(sign, reflected); var original = (double[])y.Clone();
            // An opposite precursor around the first-arrival cursor is not the archived pulse.
            y[181] = -sign * .03; y[182] = -sign * .06; y[183] = -sign * .03;
            original = (double[])y.Clone();
            Call(c, "SetWaveformXY", Times(), y); Call(c, "SetCursorPositions", 1.456, 3.85);
            Call(c, "SetDecayPulsePeakReferences", 200d, 500d);
            var a = Anchors(c);
            if (reflected == .4)
            {
                Check(a != null && Math.Sign(a[1] - a[4]) == sign && Math.Sign(a[3] - a[5]) == sign, "Signed original physical pair retained");
                Near(a[0], 1.6, "First original peak, not preceding opposite precursor"); Near(a[2], 4, "Original reflected packet");
                c.GetType().GetField("viewXMin", B).SetValue(c, 1.4); c.GetType().GetField("viewXMax", B).SetValue(c, 4.1);
                Check(ReferenceEquals(a, Anchors(c)), "Zoom cannot reselect packet");
                Call(c, "SetCursorPositions", 1.5, 3.85); Check(Anchors(c) == null, "Old reference cannot follow a changed time cursor");
                Call(c, "SetCursorPositions", 1.456, 3.85); Call(c, "SetWaveformXY", Times(), Wave(sign, .2));
                Check(Anchors(c) == null, "Old reference cannot attach to a new waveform identity");
            }
            else Check(a == null, "Opposite, growing, or missing reflection never fabricated");
            Check(y.SequenceEqual(original), "Display does not modify input");
        }
        using (var c = (Control)New("CalibrationWaveform.CalibrationWaveformControl"))
        {
            foreach (double invalid in new[] { -1d, 1001d, 500.1, double.NaN, double.PositiveInfinity })
            {
                Call(c, "SetWaveformXY", Times(), Wave(1, .4)); Call(c, "SetCursorPositions", 1.45, 3.85);
                Call(c, "SetDecayPulsePeakReferences", 200d, invalid); Check(Anchors(c) == null, "Invalid original peak identity is not guessed");
            }
            var glitch = Wave(1, 0); glitch[500] = 3;
            Call(c, "SetWaveformXY", Times(), glitch); Call(c, "SetCursorPositions", 1.45, 3.85); Call(c, "SetDecayPulsePeakReferences", 200d, 500d);
            Check(Anchors(c) == null, "Isolated sample is not a complete reflected lobe");
            var bad = Wave(1, .4); bad[500] = double.NaN;
            Call(c, "SetWaveformXY", Times(), bad); Call(c, "SetCursorPositions", 1.45, 3.85); Call(c, "SetDecayPulsePeakReferences", 200d, 500d);
            Check(Anchors(c) == null, "Invalid physical anchor suppressed");
            Call(c, "SetWaveformXY", Times(), Wave(1, .4)); Call(c, "SetCursorPositions", 1.45, 3.85); Call(c, "ClearDecayPulsePeakReferences");
            Check(Anchors(c) != null, "Explicit legacy route retains prior behavior");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 4) throw new ArgumentException("Build root, QA output, read-only exact CAL snapshot, old07/new mode required.");
            string runtime = Path.Combine(Path.GetFullPath(args[0]), "modules/DataBrowser/Xiongxin.OWTS.DataBrowser.dll");
            string qa = Path.GetFullPath(args[1]); Directory.CreateDirectory(qa);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            browser = Assembly.LoadFrom(runtime); Console.WriteLine("BROWSER_SHA256=" + Hash(runtime));
            bool old = args[3] == "old07"; if (!old && args[3] != "new") throw new ArgumentException("Explicit mode required.");
            Actual(Path.GetFullPath(args[2]), qa, old); if (!old) Synthetic();
            Console.WriteLine("BROWSER_ARCHIVED_DECAY_PASS=" + checks + "; HardwareCommands=0; ProductMain=false; OriginalProjectWrites=0; Mode=" + args[3]); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
