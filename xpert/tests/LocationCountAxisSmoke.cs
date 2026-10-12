using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HVX.ReportGenerator;

internal static class LocationCountAxisSmoke
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static double Maximum(double raw)
    {
        return (double)typeof(LocationChartRenderer).GetMethod("NiceMaximum", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { raw * 1.1, 6 });
    }
    static int Main(string[] args)
    {
        try
        {
            string qa = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            Check(LocationChartRenderer.BuildYAxisTicks(Maximum(1), true).SequenceEqual(new[] { 0d, 1d }), "A single event labels only 0 and 1, at their true count positions");
            Check(LocationChartRenderer.BuildYAxisTicks(Maximum(0), true).SequenceEqual(new[] { 0d, 1d }), "Empty count chart keeps readable integer axis");
            foreach (int count in new[] { 1, 2, 3, 4, 5, 6, 7, 9, 10, 25, 100, 1000, 1000000, int.MaxValue })
            {
                double maximum = Maximum(count);
                double[] ticks = LocationChartRenderer.BuildYAxisTicks(maximum, true);
                Check(ticks.Length >= 2 && ticks.Length <= 7 && ticks[0] == 0, "Integer axis has a bounded readable tick count: " + count);
                Check(ticks.All(v => v == Math.Truncate(v) && v >= 0 && v <= maximum + 1e-8), "All tick positions are whole counts within axis: " + count);
                Check(ticks.Zip(ticks.Skip(1), (a, b) => b > a).All(v => v), "Ticks are strictly increasing: " + count);
                Check(ticks.Select(v => v.ToString("0", CultureInfo.InvariantCulture)).Distinct().Count() == ticks.Length, "Formatted labels never repeat: " + count);
            }
            foreach (double maximum in new[] { .001, 1.2, 12.0, 600.0, 12000.0 })
                Check(LocationChartRenderer.BuildYAxisTicks(maximum, false).SequenceEqual(Enumerable.Range(0, 7).Select(i => maximum * i / 6.0)), "Q-L fractional tick positions remain unchanged: " + maximum);

            // Exercise the production drawing path and inspect dark tick ink in
            // the label gutter. Axis title/header/plot are outside this region.
            var matrix = new double[1, 36];
            matrix[0, 0] = 1; matrix[0, 5] = 170; matrix[0, 13] = 80;
            matrix[0, 15] = matrix[0, 16] = 100; matrix[0, 35] = 1;
            string single = LocationChartRenderer.RenderNL(matrix, VoltageInterval.AllResults(), ReportPhase.A, 170, 8.7, 0, "单事件整数刻度回归", Path.Combine(qa, "NL-one.png"));
            using (var image = new Bitmap(single))
            {
                int topOne = (int)Math.Round(1035 - 820 / Maximum(1));
                Check(Ink(image, 90, 192, topOne - 24, topOne + 30) > 0, "One-count label is rendered at count=1 coordinate");
                Check(Ink(image, 90, 192, 1015, 1065) > 0, "Zero-count label is rendered at baseline");
                Check(Ink(image, 90, 192, 400, 950) == 0, "No rounded fractional count labels remain in middle gutter");
            }
            LocationChartRenderer.RenderNL(new double[0, 36], VoltageInterval.AllResults(), ReportPhase.All, 170, 8.7, 1, "Empty count axis", Path.Combine(qa, "NL-empty.png"));
            LocationChartRenderer.RenderQL(matrix, VoltageInterval.AllResults(), ReportPhase.A, 170, 8.7, 0, "Q-L刻度未改变", Path.Combine(qa, "QL-unchanged.png"));
            Check(matrix[0, 35] == 1 && matrix[0, 16] == 100, "Chart rendering does not alter accepted state or charge");
            Console.WriteLine("LOCATION_COUNT_AXIS_PASS=" + checks + ";hardware=false");
            Console.WriteLine("EVIDENCE=" + qa); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static int Ink(Bitmap image, int left, int right, int top, int bottom)
    {
        int count = 0;
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
        {
            Color p = image.GetPixel(x, y);
            if (p.R < 150 && p.G < 150 && p.B < 170) count++;
        }
        return count;
    }
}
