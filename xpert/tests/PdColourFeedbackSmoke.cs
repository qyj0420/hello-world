using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class PdColourFeedbackSmoke
{
    const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static int checks;
    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    static void Set(object target, string name, object value) { target.GetType().GetField(name, Fields).SetValue(target, value); }
    static Color Colour(Assembly assembly, string type, string name)
    { return (Color)assembly.GetType(type, true).GetField(name, Fields).GetValue(null); }
    static int Pixels(Bitmap image, Color expected, Rectangle area)
    {
        int result = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                Color value = image.GetPixel(x, y);
                if (Math.Abs(value.R - expected.R) <= 3 && Math.Abs(value.G - expected.G) <= 3 && Math.Abs(value.B - expected.B) <= 3) result++;
            }
        return result;
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string release = Path.GetFullPath(args[0]);
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pd-colour");
            Directory.CreateDirectory(output);
            Assembly browser = Assembly.LoadFrom(Path.Combine(release, "modules", "DataBrowser", "Xiongxin.OWTS.DataBrowser.dll"));
            Color pd = Colour(browser, "HVX.Controls.BrowserTheme", "Pd");
            Color voltage = Colour(browser, "HVX.Controls.BrowserTheme", "Voltage");
            Check(pd.ToArgb() == Color.FromArgb(44, 133, 110).ToArgb(), "PD uses the existing LOC green.");
            Check(voltage.ToArgb() != pd.ToArgb(), "Voltage remains visually distinct.");
            Check(Colour(browser, "HVX.Controls.BrowserTheme", "Error").ToArgb() == Color.FromArgb(183, 81, 75).ToArgb(), "Error colour is preserved.");
            Check(Colour(browser, "HVX.Controls.BrowserTheme", "PhaseC").ToArgb() == Color.FromArgb(175, 68, 59).ToArgb(), "Phase C is not recoloured.");
            Assembly pressure = Assembly.LoadFrom(Path.Combine(release, "modules", "PressureTest", "Xiongxin.OWTS.PressureTest.dll"));
            Check(Colour(pressure, "HVX.Controls.PressureTestTheme", "Pd").ToArgb() == pd.ToArgb(), "Pressure uses the same PD colour.");
            Check(Colour(pressure, "HVX.Controls.PressureTestTheme", "Danger").ToArgb() == Color.FromArgb(196, 71, 71).ToArgb(), "Pressure alarm colour is preserved.");
            double[] samples = Enumerable.Range(0, 12000).Select(i => 250 + 160 * Math.Sin(i * .002) + 30 * Math.Sin(i * 1.35)).ToArray();
            double[] volts = Enumerable.Range(0, 12000).Select(i => 13 * Math.Sin(i * .002 - .4)).ToArray();
            double[] original = (double[])samples.Clone();
            double[] originalVolts = (double[])volts.Clone();
            object wave = Activator.CreateInstance(browser.GetType("HVX.Controls.BrowserWaveform", true));
            Set(wave, "Y", samples); Set(wave, "VoltageKv", volts);
            Set(wave, "SampleRateHz", 500000d); Set(wave, "VoltageSampleRateHz", 500000d); Set(wave, "VoltageReferenceKv", 15d);
            foreach (int width in new[] { 1065, 1440 })
            foreach (int language in new[] { 0, 1 })
            using (Control plot = (Control)Activator.CreateInstance(browser.GetType("HVX.Controls.BrowserTestWaveformControl", true), true))
            {
                plot.Size = new Size(width, 480);
                plot.GetType().GetMethod("SetWaveform", Fields).Invoke(plot, new[] { wave, (object)language });
                plot.CreateControl();
                using (var image = new Bitmap(plot.Width, plot.Height))
                {
                    plot.DrawToBitmap(image, plot.ClientRectangle);
                    Check(Pixels(image, pd, new Rectangle(0, 0, width, 50)) >= 15, "PD legend is green.");
                    Check(Pixels(image, pd, new Rectangle(0, 90, width, 330)) > 1000, "Synthetic noise stems are green.");
                    Check(Pixels(image, voltage, plot.ClientRectangle) > 30, "Voltage trace uses a separate colour.");
                    Check(Pixels(image, Color.FromArgb(214, 93, 82), plot.ClientRectangle) == 0, "No old red PD trace remains.");
                    image.Save(Path.Combine(output, "noise-" + width + "-" + language + ".png"), ImageFormat.Png);
                }
            }
            using (Control location = (Control)Activator.CreateInstance(browser.GetType("HVX.Controls.BrowserLocationPlot", true), true))
            {
                location.Size = new Size(1065, 480);
                location.GetType().GetMethod("SetData", Fields, null, new[] { typeof(string), typeof(double[]), typeof(double) }, null)
                    .Invoke(location, new object[] { "Synthetic LOC (not measured)", samples, 500000d });
                location.CreateControl();
                using (var image = new Bitmap(location.Width, location.Height))
                {
                    location.DrawToBitmap(image, location.ClientRectangle);
                    Check(Pixels(image, pd, location.ClientRectangle) > 50, "LOC and PD waveform colours match.");
                    image.Save(Path.Combine(output, "location.png"), ImageFormat.Png);
                }
            }
            Check(samples.SequenceEqual(original), "Display changes do not alter PD samples.");
            Check(volts.SequenceEqual(originalVolts), "Display changes do not alter voltage samples.");
            Console.WriteLine("PD_COLOUR_FEEDBACK_PASS=" + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
