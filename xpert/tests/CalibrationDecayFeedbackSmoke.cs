using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CalibrationDecayFeedbackSmoke
{
    static int checks;
    static Type controlType;
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static double[] times;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass release root and QA output directory.");
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Assembly browser = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "DataBrowser", "Xiongxin.OWTS.DataBrowser.dll"));
            controlType = browser.GetType("CalibrationWaveform.CalibrationWaveformControl", true);
            times = Enumerable.Range(0, 4001).Select(i => i * 0.002).ToArray();
            using (Control control = (Control)Activator.CreateInstance(controlType))
            {
                controlType.GetProperty("EnglishVersion").SetValue(control, true, null);
                controlType.GetProperty("PropagationVelocityMPerUs").SetValue(control, 170d, null);
                double[] clean = Wave(1, 0.4, 2, 0);
                Set(control, clean, 0.94, 2.92);
                double distance = Value(control, "CalculatedDistance");
                double difference = Value(control, "TimeDifference");
                Check(Near(difference, 1.98), "First-arrival time difference remains cursor based");
                Check(Near(distance, 168.3), "First-arrival distance remains cursor based");
                double[] first = Anchors(control);
                CheckAnchors(first, clean, 1, 3, "First-arrival cursors");
                Check(Near(first[4], 2) && Near(first[5], 2), "DC baseline excluded from decay magnitudes");
                Check(Near(Curve(first, 2), 2 + Math.Sqrt(0.4)), "Baseline-aware exponential midpoint");
                Check(Near(Value(control, "IncidentCursorX"), 0.94) && Near(Value(control, "ReflectionCursorX"), 2.92), "Drawing never snaps cursors to peaks");
                Check(Near(Value(control, "CalculatedDistance"), distance), "Anchor calculation does not change length");
                Render(control, output, "first-arrival-peak-to-peak.png");
                Set(control, clean, 1, 3);
                double[] peaked = Anchors(control);
                CheckAnchors(peaked, clean, 1, 3, "Peak cursors");
                Check(Near(peaked[0], first[0]) && Near(peaked[1], first[1]) && Near(peaked[2], first[2]) && Near(peaked[3], first[3]), "First-arrival and peak cursors select same pulse peaks");
                Check(Near(Value(control, "TimeDifference"), 2), "Peak cursor time difference follows cursors");
                double[] negative = Wave(-1, -0.4, 5, 0);
                Set(control, negative, 0.94, 2.92);
                double[] inverted = Anchors(control);
                CheckAnchors(inverted, negative, 1, 3, "Negative polarity with positive offset");
                Check(Near(Curve(inverted, 2), 5 - Math.Sqrt(0.4)), "Negative decay on correct baseline side");
                Render(control, output, "negative-polarity-decay.png");
                double[] sloped = Wave(1, 0.4, -0.3, 0.02);
                Set(control, sloped, 0.94, 2.92);
                double[] drift = Anchors(control);
                CheckAnchors(drift, sloped, 1, 3, "Slow baseline drift");
                Check(Math.Abs(drift[4] + .28) < 0.002 && Math.Abs(drift[5] + .24) < 0.002, "Local baseline follows drift");
                double[] noisy = Wave(1, .4, 0, 0);
                for (int i = 0; i < noisy.Length; i++) noisy[i] += Triangle(times[i], 6, .06, 100) + Triangle(times[i], 3.26, .025, 20);
                Set(control, noisy, 0.94, 2.92);
                CheckAnchors(Anchors(control), noisy, 1, 3, "Larger unrelated global and nearby noise peaks ignored");
                Render(control, output, "unrelated-noise-not-selected.png");
                double[] modestNoise = Wave(1, .4, 0, 0);
                for (int i = 0; i < modestNoise.Length; i++) modestNoise[i] += 0.002 * Math.Sin(i * 1.73);
                Set(control, modestNoise, .94, 2.92);
                double[] noiseAnchors = Anchors(control);
                Check(noiseAnchors != null && Math.Abs(noiseAnchors[0] - 1) <= .004 && Math.Abs(noiseAnchors[2] - 3) <= .004, "Resolved pulses stable with small noise");
                CheckRejected(control, Wave(1, -.4, 0, 0), .94, 2.92, "Opposite polarity not presented as signed exponential");
                CheckRejected(control, Wave(1, 1.4, 0, 0), .94, 2.92, "Growing reflection not fabricated as decay");
                CheckRejected(control, Wave(1, 0, 0, 0), .94, 2.92, "Missing reflection gives no fabricated line");
                CheckRejected(control, Wave(0, 0, 4, 0), .94, 2.92, "DC-only signal gives no line");
                CheckRejected(control, Wave(0, 0, 0, .02), .94, 2.92, "Baseline ramp gives no line");
                CheckRejected(control, Wave(0, 0, 0, 0), .94, 2.92, "All-zero data gives no line");
                CheckRejected(control, Wave(1, .4, 0, 0), 3, 1, "Reversed cursors give no line");
                CheckRejected(control, Wave(1, .4, 0, 0), 1, 1, "Equal cursors give no line");
                CheckRejected(control, times.Select((t, i) => .01 * Math.Sin(i * 1.73)).ToArray(), .94, 2.92, "Noise floor alone gives no line");
                double[] glitch = Wave(1, 0, 0, 0); glitch[1500] = 100;
                CheckRejected(control, glitch, .94, 2.92, "Single-sample glitch is not reflection");
                double[] pulseGlitch = Wave(1, .4, 0, 0); pulseGlitch[1500] = 100;
                CheckRejected(control, pulseGlitch, .94, 2.92, "Unresolved spike within pulse does not fabricate attenuation");
                double[] invalid = Wave(1, .4, 0, 0); invalid[1499] = double.NaN;
                CheckRejected(control, invalid, .94, 2.92, "Invalid reflection samples suppress curve");
                invalid = Wave(1, .4, 0, 0); invalid[480] = double.PositiveInfinity;
                CheckRejected(control, invalid, .94, 2.92, "Infinite incident samples suppress curve");
                double[] copyX = (double[])times.Clone(); copyX[1500] = copyX[1499];
                SetXY(control, copyX, clean, .94, 2.92);
                Check(Anchors(control) == null, "Duplicate times rejected");
                copyX = (double[])times.Clone(); copyX[1500] = 0;
                SetXY(control, copyX, clean, .94, 2.92);
                Check(Anchors(control) == null, "Unsorted times rejected");
                double[] irregularX = times.Select(t => t + t * t * .001).ToArray();
                double[] irregularY = irregularX.Select(t => Triangle(t, 1, .06, 1) + Triangle(t, 3, .08, .4)).ToArray();
                SetXY(control, irregularX, irregularY, .94, 2.92);
                double[] irregular = Anchors(control);
                Check(irregular != null && Math.Abs(irregular[0] - 1) < .003 && Math.Abs(irregular[2] - 3) < .003, "Nonuniform ordered sampling supported");
                Set(control, clean, .94, 2.92);
                var cached = Anchors(control);
                Check(ReferenceEquals(cached, Anchors(control)), "Render cache avoids repeated waveform scans");
                controlType.GetField("viewXMin", Hidden).SetValue(control, .9);
                controlType.GetField("viewXMax", Hidden).SetValue(control, 3.1);
                Check(ReferenceEquals(cached, Anchors(control)), "Zoom cannot shift anchors");
                Set(control, Wave(2, .2, 0, 0), .94, 2.92);
                Check(!ReferenceEquals(cached, Anchors(control)), "New waveform invalidates cache");
                Check(Near(Value(control, "TimeDifference"), 1.98), "All calculations leave timing unchanged");
            }
            Console.WriteLine("CALIBRATION_DECAY_FEEDBACK_PASS=" + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void CheckRejected(Control control, double[] values, double a, double b, string label)
    { Set(control, values, a, b); Check(Anchors(control) == null, label); Check(Near(Value(control, "TimeDifference"), b - a), label + "; timing retained"); }
    static double[] Wave(double a, double b, double baseline, double slope)
    { return times.Select(t => baseline + slope * t + Triangle(t, 1, .06, a) + Triangle(t, 3, .08, b)).ToArray(); }
    static double Triangle(double x, double center, double halfWidth, double amplitude)
    { return amplitude * Math.Max(0, 1 - Math.Abs(x - center) / halfWidth); }
    static void Set(Control control, double[] values, double a, double b) { SetXY(control, times, values, a, b); }
    static void SetXY(Control control, double[] x, double[] y, double a, double b)
    {
        controlType.GetMethod("SetWaveformXY").Invoke(control, new object[] { x, y });
        controlType.GetMethod("SetCursorPositions").Invoke(control, new object[] { a, b });
    }
    static double[] Anchors(Control control) { return (double[])controlType.GetMethod("DecayAnchors", Hidden).Invoke(control, null); }
    static double Curve(double[] anchors, double x) { return (double)controlType.GetMethod("DecayAt", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { anchors, x }); }
    static double Value(Control control, string name) { return (double)controlType.GetProperty(name).GetValue(control, null); }
    static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-8; }
    static void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException(label); }
    static void CheckAnchors(double[] anchors, double[] values, double a, double b, string label)
    {
        Check(anchors != null && anchors.Length == 6, label + ": valid peak anchors");
        Check(Near(anchors[0], a) && Near(anchors[2], b), label + ": both x coordinates are pulse peaks");
        Check(Near(anchors[1], values[(int)Math.Round(a / .002)]) && Near(anchors[3], values[(int)Math.Round(b / .002)]), label + ": endpoints touch actual waveform");
        Check(Near(Curve(anchors, a), anchors[1]) && Near(Curve(anchors, b), anchors[3]), label + ": curve passes both endpoints");
    }
    static void Render(Control control, string output, string name)
    { controlType.GetMethod("SavePng").Invoke(control, new object[] { Path.Combine(output, name), 1200, 550 }); }
}
