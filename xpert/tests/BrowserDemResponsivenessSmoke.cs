using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class BrowserDemResponsivenessSmoke
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static Assembly assembly;
    static void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException(name); }
    static object Field(object value, string name) { return value.GetType().GetField(name, Flags).GetValue(value); }
    static void Call(object value, string method, params object[] args) { value.GetType().GetMethod(method, Flags).Invoke(value, args); }
    static object Wave(double[] pd, double[] voltage, double rate, double referenceKv = double.NaN)
    {
        object wave = Activator.CreateInstance(assembly.GetType("HVX.Controls.BrowserWaveform", true));
        foreach (var entry in new object[][] { new object[] { "Y", pd }, new object[] { "VoltageKv", voltage },
            new object[] { "SampleRateHz", rate }, new object[] { "VoltageSampleRateHz", rate }, new object[] { "VoltageReferenceKv", referenceKv } })
            wave.GetType().GetField((string)entry[0], Flags).SetValue(wave, entry[1]);
        return wave;
    }
    static double Measure(Action action) { var timer = Stopwatch.StartNew(); action(); timer.Stop(); return timer.Elapsed.TotalMilliseconds; }
    static ulong Bits(double[] data)
    { unchecked { ulong hash = 1469598103934665603; foreach (double n in data) { hash ^= (ulong)BitConverter.DoubleToInt64Bits(n); hash *= 1099511628211; } return hash; } }
    static ulong Pixels(Bitmap image)
    { unchecked { ulong hash = 1469598103934665603; for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) { hash ^= (uint)image.GetPixel(x, y).ToArgb(); hash *= 1099511628211; } return hash; } }
    static void Paint(Control control, Bitmap image)
    { using (var graphics = Graphics.FromImage(image)) { graphics.Clear(control.BackColor); Call(control, "OnPaint", new PaintEventArgs(graphics, control.ClientRectangle)); } }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool baseline = args.Contains("--baseline");
            string root = Path.GetFullPath(args.FirstOrDefault(a => a != "--baseline") ?? AppDomain.CurrentDomain.BaseDirectory);
            string dll = Path.Combine(root, "modules", "DataBrowser", "Xiongxin.OWTS.DataBrowser.dll");
            if (!File.Exists(dll)) dll = Path.Combine(root, "Xiongxin.OWTS.DataBrowser.dll");
            assembly = Assembly.LoadFrom(dll);
            const int count = 1000003;
            var pd = new double[count]; var voltage = new double[count];
            for (int i = 0; i < count; i++) { pd[i] = (i % 71 == 0 ? 200 : 0); voltage[i] = 9.2 * Math.Sin(i * .0012); }
            pd[333333] = 999; pd[333334] = -777; voltage[777777] = 13;
            // Each pixel bucket contains an actual gap; baseline renders roughly
            // a million voltage points. These are synthetic samples, not a project.
            for (int i = 512; i < count; i += 512) voltage[i] = double.NaN;
            ulong pdBits = Bits(pd), voltageBits = Bits(voltage);
            var waveform = Wave(pd, voltage, 1000000, 9.2);
            Type type = assembly.GetType("HVX.Controls.BrowserTestWaveformControl", true);
            using (Control control = (Control)Activator.CreateInstance(type, true))
            using (var image = new Bitmap(1280, 720))
            {
                control.Size = image.Size;
                double projectMs = Measure(() => Call(control, "SetWaveform", waveform, 0));
                double firstPaintMs = Measure(() => Paint(control, image));
                double repeatedMs = Measure(() => { for (int i = 0; i < 30; i++) Paint(control, image); });
                double moveMs = Measure(() => { for (int i = 0; i < 1000; i++) Call(control, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 100 + i % 900, 200, 0)); });
                Console.WriteLine("DEM synthetic million: projection={0:F2} ms; first paint={1:F2} ms; 30 repeat paints={2:F2} ms ({3:F2}/frame); 1000 mouse moves={4:F2} ms", projectMs, firstPaintMs, repeatedMs, repeatedMs / 30, moveMs);
                Check(pdBits == Bits(pd) && voltageBits == Bits(voltage), "rendering retains all raw signed samples and NaN positions bit for bit");
                object data = type.GetProperty("DisplayData", Flags).GetValue(control, null);
                Check(ReferenceEquals(pd, Field(data, "Pd")) && ReferenceEquals(voltage, Field(data, "Voltage")), "display uses the original arrays");
                Check(Math.Abs((double)type.GetProperty("DisplayVoltageScaleKv", Flags).GetValue(control, null) - 13 / 1.2) < 1e-10, "voltage ruler retains true narrow positive peak");
                if (!baseline)
                {
                    int renders = (int)type.GetProperty("StaticRenderCount", Flags).GetValue(control, null);
                    Check(renders == 1, "repeat paints never rebuild the million point static scene");
                    object index = Field(data, "PdIndex");
                    Call(control, "SetWaveform", waveform, 1);
                    Check(ReferenceEquals(index, Field(type.GetProperty("DisplayData", Flags).GetValue(control, null), "PdIndex")), "language refresh reuses the prepared source index");
                    Paint(control, image);
                    Check((int)type.GetProperty("StaticRenderCount", Flags).GetValue(control, null) == renders + 1, "language refresh rebuilds labels once");
                    Call(control, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 100, 200, 0)); Paint(control, image);
                    Check((int)type.GetProperty("StaticRenderCount", Flags).GetValue(control, null) == renders + 1, "ordinary mouse movement keeps static layer");
                    var plot = (Rectangle)type.GetProperty("PlotBounds", Flags).GetValue(control, null);
                    Call(control, "ApplyWheelZoom", new MouseEventArgs(MouseButtons.None, 0, plot.Left + plot.Width / 2, plot.Top + plot.Height / 2, 120), false);
                    Paint(control, image);
                    Check((int)type.GetProperty("StaticRenderCount", Flags).GetValue(control, null) == renders + 2, "actual zoom rebuilds the changed viewport once");
                    Check(pdBits == Bits(pd) && voltageBits == Bits(voltage), "zoom keeps raw waveform untouched");
                    Type dataType = assembly.GetType("HVX.Controls.BrowserTestWaveformData", true);
                    MethodInfo prepare = dataType.GetMethod("Prepare", Flags);
                    var fresh = Wave(pd, voltage, 1000000);
                    object prepared = null; Exception workerError = null;
                    var worker = new Thread(() => { try { prepared = prepare.Invoke(null, new object[] { fresh, CancellationToken.None }); } catch (Exception ex) { workerError = ex; } });
                    worker.Start(); worker.Join();
                    Check(workerError == null && prepared != null, "DEM display can be fully prepared away from the UI thread");
                    Call(control, "SetWaveform", fresh, 0);
                    Check(ReferenceEquals(prepared, type.GetProperty("DisplayData", Flags).GetValue(control, null)), "UI binding reuses completed background preparation");
                    var canceled = Wave(pd, voltage, double.NaN);
                    using (var cancellation = new CancellationTokenSource())
                    {
                        cancellation.Cancel(); bool stopped = false;
                        try { prepare.Invoke(null, new object[] { canceled, cancellation.Token }); }
                        catch (TargetInvocationException ex) { stopped = ex.InnerException is OperationCanceledException; }
                        Check(stopped, "canceled selection cannot publish a prepared display");
                    }
                    Check(prepare.Invoke(null, new object[] { canceled, CancellationToken.None }) != null, "a subsequent uncanceled load can prepare the same source");
                }
                var shortWave = Wave(new double[] { 0, 300, -900, 0, 100, 0, 0, 0, 0 },
                    new double[] { 0, 5, 7, double.NaN, double.NaN, -7, -5, 0, 5 }, 1000, 9.2);
                Call(control, "SetWaveform", shortWave, 0); Paint(control, image);
                ulong shortHash = Pixels(image);
                Console.WriteLine("short signed/gapped DEM image: {0:X16}", shortHash);
                if (!baseline)
                {
                    using (var direct = new Bitmap(image.Width, image.Height))
                    {
                        using (var graphics = Graphics.FromImage(direct)) { graphics.Clear(control.BackColor); Call(control, "RenderScene", graphics); }
                        Check(Pixels(direct) == shortHash, "cached short DEM is pixel identical to direct rendering including the true NaN gap");
                    }
                    object previousLayer = Field(control, "staticLayer");
                    control.BackColor = Color.FromArgb(243, 248, 251); Paint(control, image);
                    Check(!ReferenceEquals(previousLayer, Field(control, "staticLayer")), "appearance change replaces cached scene");
                    control.Size = new Size(1000, 600); Paint(control, image);
                    Check(((Bitmap)Field(control, "staticLayer")).Size == control.ClientSize, "resize regenerates scene at actual client pixels");
                    control.Dispose(); Check(Field(control, "staticLayer") == null, "dispose releases the static bitmap");
                }
                Console.WriteLine("PASS {0} ({1} checks)", baseline ? "baseline measurement" : "BrowserDemResponsivenessSmoke", checks);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
