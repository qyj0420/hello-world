using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace HVX.ReportGenerator
{
    /// <summary>Rebuilds a saved PDmap pulse from its archived samples instead of enlarging a small PNG.</summary>
    internal static class PdmapChartRenderer
    {
        internal const int Width = 3000;
        internal const int Height = 1500;
        static readonly RectangleF Plot = new RectangleF(270, 390, 2460, 780);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Color Wave = Color.FromArgb(28, 109, 96);
        static readonly Color Incident = Color.FromArgb(0, 135, 173);
        static readonly Color Reflection = Color.FromArgb(213, 125, 36);

        internal static bool TryRender(ReportImageInfo image, ProjectReportData project, int language, string outputPath)
        {
            if (image == null || string.IsNullOrWhiteSpace(image.ImagePath) ||
                (image.ChartKind ?? string.Empty).IndexOf("PDmap", StringComparison.OrdinalIgnoreCase) < 0) return false;
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(image.ImagePath));
                string csvPath = Path.ChangeExtension(image.ImagePath, ".csv");
                string[] lines = File.ReadLines(csvPath, Encoding.UTF8).Take(2).ToArray();
                if (lines.Length < 2 || !lines[0].StartsWith("PDmap_ID,Parameter_Row,", StringComparison.OrdinalIgnoreCase)) return false;
                string[] fields = lines[1].Split(',');
                if (fields.Length < 11) return false;
                int row = int.Parse(fields[1], NumberStyles.Integer, Inv);
                double[] parameters = ReadRow(Path.Combine(directory, "parameters.bin"), row, 36, 128);
                double[] samples = ReadRow(Path.Combine(directory, "pdmap.bin"), row, 64, 200000);
                if (samples.Length < 64 || parameters.Length < 36) return false;
                double[][] references = ReadReferences(csvPath, samples.Length);
                double sampleRate = parameters[7], incidentIndex = parameters[20];
                double delayNs = double.Parse(fields[7], NumberStyles.Float, Inv);
                double locationM = double.Parse(fields[4], NumberStyles.Float, Inv);
                double chargePc = double.Parse(fields[6], NumberStyles.Float, Inv);
                bool rawCharge = !Finite(chargePc);
                if (rawCharge) chargePc = double.Parse(fields[5], NumberStyles.Float, Inv);
                double cableLength = project != null && ProjectReportData.IsFinite(project.CableLengthM) && project.CableLengthM > 0
                    ? project.CableLengthM : parameters[5];
                double velocity = parameters[6];
                if (!Finite(sampleRate) || sampleRate <= 0 || !Finite(incidentIndex) || incidentIndex < 0 ||
                    !Finite(cableLength) || cableLength <= 0 || !Finite(velocity) || velocity <= 0) return false;
                double incidentUs = incidentIndex * 1e6 / sampleRate;
                double reflectionUs = incidentUs + delayNs / 1000.0;
                double durationUs = (samples.Length - 1) * 1e6 / sampleRate;
                if (incidentUs >= durationUs) return false;
                bool hasReflection = Finite(delayNs) && delayNs > 0 && Finite(locationM) && reflectionUs < durationUs;
                string full = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
                {
                    bitmap.SetResolution(300, 300);
                    using (Graphics g = Graphics.FromImage(bitmap))
                        Draw(g, image, project, language, parameters, samples, cableLength, velocity,
                            incidentUs, reflectionUs, durationUs, locationM, chargePc, delayNs, hasReflection, rawCharge, references);
                    bitmap.Save(full, ImageFormat.Png);
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        internal static bool HasData(ReportImageInfo image, ProjectReportData project)
        {
            if (image == null || string.IsNullOrWhiteSpace(image.ImagePath)) return false;
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(image.ImagePath));
                string[] lines = File.ReadLines(Path.ChangeExtension(image.ImagePath, ".csv"), Encoding.UTF8).Take(2).ToArray();
                if (lines.Length < 2 || !lines[0].StartsWith("PDmap_ID,Parameter_Row,", StringComparison.OrdinalIgnoreCase)) return false;
                string[] fields = lines[1].Split(',');
                int row; if (fields.Length < 11 || !int.TryParse(fields[1], NumberStyles.Integer, Inv, out row)) return false;
                double[] parameters = ReadRow(Path.Combine(directory, "parameters.bin"), row, 36, 128);
                double[] samples = ReadRow(Path.Combine(directory, "pdmap.bin"), row, 64, 200000);
                if (parameters.Length < 36 || samples.Length < 64 || !samples.Any(Finite)) return false;
                double length = project != null && Finite(project.CableLengthM) && project.CableLengthM > 0 ? project.CableLengthM : parameters[5];
                return Finite(parameters[7]) && parameters[7] > 0 && Finite(parameters[20]) && parameters[20] >= 0 &&
                    parameters[20] < samples.Length - 1 && Finite(length) && length > 0 && Finite(parameters[6]) && parameters[6] > 0;
            }
            catch (Exception) { return false; }
        }

        static double[][] ReadReferences(string csvPath, int count)
        {
            double[][] values = { Enumerable.Repeat(double.NaN, count).ToArray(), Enumerable.Repeat(double.NaN, count).ToArray() };
            bool samplesStarted = false;
            foreach (string line in File.ReadLines(csvPath, Encoding.UTF8))
            {
                if (!samplesStarted) { samplesStarted = line.StartsWith("Index,Time_ns,RawPulse_pC,RawCalibration,RawDamping,", StringComparison.Ordinal); continue; }
                string[] fields = line.Split(',');
                int index;
                if (fields.Length < 5 || !int.TryParse(fields[0], out index) || index < 0 || index >= count) continue;
                for (int series = 0; series < 2; series++)
                {
                    double value;
                    if (double.TryParse(fields[series + 3], NumberStyles.Float, Inv, out value)) values[series][index] = value;
                }
            }
            return values;
        }

        static double[] ReadRow(string path, int row, int minimumColumns, int maximumColumns)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length < 16 || Encoding.ASCII.GetString(reader.ReadBytes(8)) != "HVXMAT01")
                    throw new InvalidDataException("Invalid archived matrix.");
                int rows = reader.ReadInt32(), columns = reader.ReadInt32();
                if (rows <= 0 || row < 0 || row >= rows || columns < minimumColumns || columns > maximumColumns ||
                    stream.Length != 16L + (long)rows * columns * sizeof(double))
                    throw new InvalidDataException("Invalid archived matrix dimensions.");
                stream.Position = 16L + (long)row * columns * sizeof(double);
                var values = new double[columns];
                for (int i = 0; i < columns; i++) values[i] = reader.ReadDouble();
                return values;
            }
        }

        static void Draw(Graphics g, ReportImageInfo image, ProjectReportData project, int language,
            double[] parameters, double[] samples, double cableLength, double velocity,
            double incidentUs, double reflectionUs, double durationUs, double locationM, double chargePc, double delayNs,
            bool hasReflection, bool rawCharge, double[][] references)
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bool chinese = language == 0;
            int phaseIndex = Math.Max(0, Math.Min(2, (int)Math.Round(parameters[0]) - 1));
            string phase = chinese ? "ABC"[phaseIndex] + "相" : "L" + (phaseIndex + 1).ToString(Inv);
            double maxAbs = samples.Where(Finite).Select(Math.Abs).DefaultIfEmpty(1).Max();
            maxAbs = Math.Max(maxAbs, references.SelectMany(series => series).Where(Finite).Select(Math.Abs).DefaultIfEmpty(0).Max());
            double bound = Math.Max(25, Math.Ceiling(maxAbs * 1.08 / 25.0) * 25.0);
            using (var titleFont = Font(70, FontStyle.Bold))
            using (var detailFont = Font(44, FontStyle.Regular))
            using (var metricFont = Font(43, FontStyle.Bold))
            using (var tickFont = Font(47, FontStyle.Regular))
            using (var axisFont = Font(55, FontStyle.Bold))
            using (var noteFont = Font(38, FontStyle.Regular))
            using (var ink = new SolidBrush(ReportTheme.Ink))
            using (var muted = new SolidBrush(ReportTheme.Muted))
            using (var plotFill = new SolidBrush(Color.FromArgb(252, 253, 254)))
            using (var grid = new Pen(Color.FromArgb(222, 232, 239), 2))
            using (var axis = new Pen(ReportTheme.Navy, 5))
            using (var rule = new Pen(ReportTheme.Cyan, 8))
            using (var wavePen = new Pen(Wave, 5))
            using (var incidentPen = new Pen(Incident, 5))
            using (var reflectionPen = new Pen(Reflection, 5))
            using (var right = Right())
            using (var center = Center())
            {
                g.DrawString(ReportLanguages.Ui(language, "PDmap 局放脉冲定位波形", "PDmap pulse-location waveform"),
                    titleFont, ink, new RectangleF(135, 35, 2120, 95));
                g.DrawString(phase + "  ·  " + parameters[2].ToString("0.#", Inv) + " kV",
                    detailFont, muted, new RectangleF(2240, 55, 605, 70), right);
                g.DrawLine(rule, 135, 155, Width - 135, 155);
                string metrics = hasReflection ? (ReportLanguages.Ui(language, "反射时差 ", "Reflection delay ")) + (delayNs / 1000).ToString("0.###", Inv) +
                    " µs     " + (ReportLanguages.Ui(language, "定位 ", "Location ")) + locationM.ToString("0.#", Inv) +
                    " m" : (ReportLanguages.Ui(language, "反射波未确定 · 定位 —", "Reflection undetermined · Location —"));
                metrics += "     " + (rawCharge ? (ReportLanguages.Ui(language, "原始放电量 ", "Raw charge ")) : (ReportLanguages.Ui(language, "放电量 ", "Charge "))) +
                    (Finite(chargePc) ? chargePc.ToString("0.#", Inv) + " pC" : "—");
                g.DrawString(metrics, metricFont, ink, new RectangleF(270, 192, 2460, 72));
                Color[] referenceColors = { Color.FromArgb(193, 101, 90), Color.FromArgb(126, 169, 199) };
                for (int series = 0; series < references.Length; series++)
                {
                    if (!references[series].Any(value => Finite(value) && value != 0)) continue;
                    using (var referencePen = new Pen(referenceColors[series], 3f))
                        g.DrawLine(referencePen, 1250 + series * 700, 289, 1350 + series * 700, 289);
                    g.DrawString(series == 0 ? (ReportLanguages.Ui(language, "校准参考", "Calibration reference")) :
                        (ReportLanguages.Ui(language, "阻尼衰减参考", "Damping reference")), noteFont, muted, 1370 + series * 700, 263);
                }
                g.FillRectangle(plotFill, Plot);
                for (int i = 0; i <= 6; i++)
                {
                    float y = Plot.Bottom - i * Plot.Height / 6f;
                    g.DrawLine(grid, Plot.Left, y, Plot.Right, y);
                    g.DrawString((-bound + 2 * bound * i / 6).ToString("0.#", Inv),
                        tickFont, ink, new RectangleF(10, y - 35, 235, 72), right);
                }
                for (int i = 0; i <= 6; i++)
                {
                    float x = Plot.Left + i * Plot.Width / 6f;
                    g.DrawLine(grid, x, Plot.Top, x, Plot.Bottom);
                    g.DrawString((durationUs * i / 6).ToString("0.##", Inv),
                        tickFont, ink, new RectangleF(x - 125, Plot.Bottom + 15, 250, 72), center);
                }
                g.DrawLine(axis, Plot.Left, Plot.Top, Plot.Left, Plot.Bottom);
                g.DrawLine(axis, Plot.Left, Plot.Bottom, Plot.Right, Plot.Bottom);
                for (int series = 0; series < references.Length; series++)
                    if (references[series].Any(value => Finite(value) && value != 0))
                        using (var referencePen = new Pen(referenceColors[series], 3f))
                            DrawWave(g, references[series], bound, referencePen);
                DrawWave(g, samples, bound, wavePen);
                incidentPen.DashStyle = DashStyle.Dash;
                reflectionPen.DashStyle = DashStyle.Dash;
                DrawCursor(g, incidentUs, durationUs, incidentPen,
                    ReportLanguages.Ui(language, "入射波", "Incident"), detailFont, Incident);
                if (hasReflection)
                    DrawCursor(g, reflectionUs, durationUs, reflectionPen,
                        ReportLanguages.Ui(language, "反射波", "Reflection"), detailFont, Reflection);
                g.DrawString(ReportLanguages.Ui(language, "时间 / µs", "Time / µs"), axisFont, ink,
                    new RectangleF(Plot.Left, Plot.Bottom + 80, Plot.Width, 76), center);
                DrawVertical(g, ReportLanguages.Ui(language, "幅值 / pC", "Amplitude / pC"), axisFont, ink);
                double[] joints = project == null ? new double[0] : project.JointPositions;
                double[] positions = new[] { cableLength }.Concat(joints.Where(x => x > 0 && x < cableLength))
                    .Concat(new[] { 0.0 }).Distinct().OrderByDescending(x => x).ToArray();
                using (var markerBrush = new SolidBrush(ReportTheme.Cyan))
                foreach (double position in positions)
                {
                    double time = incidentUs + 2 * (cableLength - position) / velocity;
                    if (time < 0 || time > durationUs) continue;
                    float x = Plot.Left + (float)(time / durationUs * Plot.Width);
                    g.FillPolygon(markerBrush,
                        new[] { new PointF(x, 1375), new PointF(x - 9, 1390), new PointF(x + 9, 1390) });
                    g.DrawString(position.ToString("0.#", Inv) + " m", noteFont, muted,
                        new RectangleF(x - 110, 1392, 220, 55), center);
                }
            }
        }

        static void DrawWave(Graphics g, double[] samples, double bound, Pen pen)
        {
                using (var curve = new GraphicsPath())
                {
                    for (int i = 1; i < samples.Length; i++)
                    {
                        if (!Finite(samples[i - 1]) || !Finite(samples[i])) { curve.StartFigure(); continue; }
                        float x1 = Plot.Left + (float)((i - 1.0) / (samples.Length - 1) * Plot.Width);
                        float x2 = Plot.Left + (float)((double)i / (samples.Length - 1) * Plot.Width);
                        float y1 = Plot.Top + (float)((bound - samples[i - 1]) / (2 * bound) * Plot.Height);
                        float y2 = Plot.Top + (float)((bound - samples[i]) / (2 * bound) * Plot.Height);
                        curve.AddLine(x1, y1, x2, y2);
                    }
                    if (curve.PointCount > 0) g.DrawPath(pen, curve);
                }
        }

        static void DrawCursor(Graphics g, double timeUs, double durationUs, Pen pen,
            string label, Font font, Color color)
        {
            float x = Plot.Left + (float)(timeUs / durationUs * Plot.Width);
            g.DrawLine(pen, x, Plot.Top, x, Plot.Bottom);
            using (var brush = new SolidBrush(color))
            using (var center = Center())
                g.DrawString(label, font, brush, new RectangleF(x - 150, Plot.Top - 67, 300, 60), center);
        }

        static Font Font(float size, FontStyle style) { return new Font("Microsoft YaHei", size, style, GraphicsUnit.Pixel); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static StringFormat Center() { return new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; }
        static StringFormat Right() { return new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center }; }
        static void DrawVertical(Graphics g, string text, Font font, Brush brush)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(70, Plot.Top + Plot.Height / 2);
            g.RotateTransform(-90);
            using (var center = Center())
                g.DrawString(text, font, brush, new RectangleF(-Plot.Height / 2, -55, Plot.Height, 90), center);
            g.Restore(state);
        }
    }
}
