using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HVX.ReportGenerator
{
    /// <summary>Draws readable report figures from the archived region's source rows.</summary>
    internal static class RegionStatisticsChartRenderer
    {
        internal const int Width = 3000;
        internal const int Height = 1650;
        static readonly RectangleF Plot = new RectangleF(260, 335, 2480, 1040);
        static readonly Color[] Colors = { ReportTheme.PhaseA, ReportTheme.PhaseB, ReportTheme.PhaseC };

        internal static bool TryRender(ReportImageInfo image, ProjectReportData project, string projectPath,
            int language, string outputPath)
        {
            if (image == null || string.IsNullOrWhiteSpace(image.ImagePath)) return false;
            string csv = Path.ChangeExtension(image.ImagePath, ".csv");
            if (!File.Exists(csv) || new FileInfo(csv).Length > 64L * 1024 * 1024) return false;
            double[,] matrix = ReadMatrix(csv);
            if (matrix.GetLength(0) == 0) return false;
            if (image.ChartMode == 9 || image.ChartMode == 10)
            {
                var trend = new TrendSelection
                {
                    Kind = image.ChartMode == 9 ? TrendChartKind.ChargeVoltage : TrendChartKind.CountVoltage,
                    Phase = ReportPhase.All,
                    Title = image.Title
                };
                TrendStatisticsChartRenderer.Render(matrix, projectPath, trend, language, outputPath);
                return true;
            }
            if (image.ChartMode != 0 && image.ChartMode != 2 && image.ChartMode != 5 && image.ChartMode != 12) return false;
            bool phaseAxis = image.ChartMode == 2 || image.ChartMode == 12;
            double[] xs = Enumerable.Range(0, matrix.GetLength(0)).Select(i => matrix[i, phaseAxis ? 3 : 13]).ToArray();
            double[] ys = Enumerable.Range(0, matrix.GetLength(0)).Select(i =>
                image.ChartMode == 12 && matrix[i, 16] >= 0 && matrix[i, 3] >= 180 ?
                    -matrix[i, 16] : Math.Abs(matrix[i, 16])).ToArray();
            double xMax = phaseAxis ? 360.0 : Math.Max(1.0, xs.Where(Finite).DefaultIfEmpty(0).Max());
            if (!phaseAxis)
                xMax = Math.Max(xMax, Enumerable.Range(0, matrix.GetLength(0))
                    .Select(i => matrix[i, 5]).Where(Finite).DefaultIfEmpty(0).Max());
            if (!phaseAxis && project != null && ProjectReportData.IsFinite(project.CableLengthM))
                xMax = Math.Max(xMax, project.CableLengthM);
            double yMax = NiceMaximum(ys.Where(Finite).Select(Math.Abs).DefaultIfEmpty(0).Max() * 1.05);
            string full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                bitmap.SetResolution(300f, 300f);
                using (Graphics g = Graphics.FromImage(bitmap))
                    Draw(g, image, matrix, xs, ys, xMax, yMax, language);
                bitmap.Save(full, ImageFormat.Png);
            }
            return true;
        }

        internal static bool HasData(ReportImageInfo image, string projectPath)
        {
            if (image == null || string.IsNullOrWhiteSpace(image.ImagePath)) return false;
            try
            {
                string csv = Path.ChangeExtension(image.ImagePath, ".csv");
                if (!File.Exists(csv) || new FileInfo(csv).Length > 64L * 1024 * 1024) return false;
                double[,] matrix = ReadMatrix(csv);
                if (image.ChartMode == 9 || image.ChartMode == 10)
                    return TrendStatisticsChartRenderer.HasData(matrix, projectPath, new TrendSelection { Kind = image.ChartMode == 9 ? TrendChartKind.ChargeVoltage : TrendChartKind.CountVoltage, Phase = ReportPhase.All });
                if (image.ChartMode != 0 && image.ChartMode != 2 && image.ChartMode != 5 && image.ChartMode != 12) return false;
                int x = image.ChartMode == 2 || image.ChartMode == 12 ? 3 : 13;
                return Enumerable.Range(0, matrix.GetLength(0)).Any(row => Finite(matrix[row, x]) && Finite(matrix[row, 16]));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static double[,] ReadMatrix(string path)
        {
            var rows = new List<double[]>();
            using (var reader = new StreamReader(path))
            {
                string header = reader.ReadLine();
                if (header == null || !header.StartsWith("SourceRow,PhaseLabel,PDmapID,Column0,", StringComparison.OrdinalIgnoreCase))
                    return new double[0, 36];
                string line;
                while ((line = reader.ReadLine()) != null && rows.Count < 1000000)
                {
                    string[] fields = line.Split(',');
                    if (fields.Length < 39) continue;
                    var values = new double[36];
                    for (int column = 0; column < 36; column++)
                    {
                        double value;
                        values[column] = double.TryParse(fields[column + 3], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out value) ? value : double.NaN;
                    }
                    rows.Add(values);
                }
            }
            var matrix = new double[rows.Count, 36];
            for (int row = 0; row < rows.Count; row++)
                for (int column = 0; column < 36; column++) matrix[row, column] = rows[row][column];
            return matrix;
        }

        static void Draw(Graphics g, ReportImageInfo image, double[,] matrix, double[] xs, double[] ys,
            double xMax, double yMax, int language)
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bool chinese = language == 0;
            bool phaseAxis = image.ChartMode == 2 || image.ChartMode == 12;
            bool encodedColor = image.ChartMode == 5 || image.ChartMode == 12;
            double colorMax = image.ChartMode == 5 ?
                Enumerable.Range(0, matrix.GetLength(0)).Select(i => matrix[i, 2]).Where(Finite).DefaultIfEmpty(1).Max() :
                Enumerable.Range(0, matrix.GetLength(0)).Select(i => matrix[i, 13]).Where(Finite).DefaultIfEmpty(1).Max();
            colorMax = Math.Max(1, colorMax);
            double yMin = image.ChartMode == 12 ? -yMax : 0;
            using (Font titleFont = Font(64f, FontStyle.Bold))
            using (Font subtitleFont = Font(38f, FontStyle.Regular))
            using (Font tickFont = Font(40f, FontStyle.Regular))
            using (Font axisFont = Font(47f, FontStyle.Bold))
            using (Brush ink = new SolidBrush(ReportTheme.Ink))
            using (Brush muted = new SolidBrush(ReportTheme.Muted))
            using (Pen accent = new Pen(ReportTheme.Cyan, 7f))
            using (Pen grid = new Pen(ReportTheme.Grid, 2f))
            using (Pen border = new Pen(ReportTheme.Border, 3f))
            {
                g.DrawString(image.Title, titleFont, ink, new RectangleF(115, 45, 2750, 95));
                g.DrawLine(accent, 115, 155, Width - 115, 155);
                string summary = image.RegionSummary;
                if (string.IsNullOrWhiteSpace(summary))
                    summary = matrix.GetLength(0).ToString(CultureInfo.InvariantCulture) + " " +
                        ReportLanguages.Text(language, ReportLanguages.UiKeys.RegionRecords);
                if (summary.IndexOf("当前数据", StringComparison.OrdinalIgnoreCase) < 0 &&
                    summary.IndexOf("record", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !string.IsNullOrWhiteSpace(image.RegionSummary))
                    summary += "  ·  " + matrix.GetLength(0).ToString(CultureInfo.InvariantCulture) +
                        (ReportLanguages.Ui(language, " 条数据", " records"));
                g.DrawString(summary, subtitleFont, muted, new RectangleF(115, 176, 2730, 115));
                for (int tick = 0; tick <= 5; tick++)
                {
                    float x = Plot.Left + tick * Plot.Width / 5f;
                    float y = Plot.Bottom - tick * Plot.Height / 5f;
                    g.DrawLine(grid, x, Plot.Top, x, Plot.Bottom);
                    g.DrawLine(grid, Plot.Left, y, Plot.Right, y);
                    string xt = (xMax * tick / 5.0).ToString(phaseAxis ? "0" : "0.#", CultureInfo.InvariantCulture);
                    using (var centered = new StringFormat { Alignment = StringAlignment.Center })
                        g.DrawString(xt, tickFont, ink, new RectangleF(x - 110, Plot.Bottom + 16, 220, 62), centered);
                    using (var right = new StringFormat { Alignment = StringAlignment.Far })
                        g.DrawString((yMin + (yMax - yMin) * tick / 5.0).ToString("0.#", CultureInfo.InvariantCulture), tickFont,
                            ink, new RectangleF(8, y - 31, 220, 64), right);
                }
                // Normalized phase guide only, not a measured charge/voltage series.
                // Draw behind the observations in both Q-PHI and signed L-PRPD.
                if (phaseAxis)
                {
                    using (var sinePen = new Pen(Color.FromArgb(150, 168, 185), 4f))
                    {
                        PointF[] sine = Enumerable.Range(0, 361).Select(degrees => new PointF(
                            Plot.Left + Plot.Width * degrees / 360f,
                            Plot.Top + Plot.Height * (float)(0.5 - 0.47 * Math.Sin(degrees * Math.PI / 180.0)))).ToArray();
                        g.DrawLines(sinePen, sine);
                        g.DrawLine(sinePen, 260, 1573, 365, 1573);
                    }
                    g.DrawString(ReportLanguages.Ui(language, "正弦参考（归一化）", "Sine reference (normalized)"),
                        subtitleFont, muted, new RectangleF(385, 1542, 710, 65));
                }
                for (int row = 0; row < matrix.GetLength(0); row++)
                {
                    if (!Finite(xs[row]) || !Finite(ys[row]) || xs[row] < 0) continue;
                    int phase = (int)matrix[row, 0];
                    if (phase < 1 || phase > 3) continue;
                    float x = Plot.Left + (float)(xs[row] / xMax) * Plot.Width;
                    float y = Plot.Bottom - (float)((ys[row] - yMin) / (yMax - yMin)) * Plot.Height;
                    double colorValue = image.ChartMode == 5 ? matrix[row, 2] : matrix[row, 13];
                    Color color = encodedColor && Finite(colorValue) ? Gradient(colorValue / colorMax) : Colors[phase - 1];
                    using (var pen = new Pen(color, 3.2f))
                        g.DrawEllipse(pen, x - 7, y - 7, 14, 14);
                }
                g.DrawRectangle(border, Plot.X, Plot.Y, Plot.Width, Plot.Height);
                using (var centered = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(phaseAxis ? (ReportLanguages.Ui(language, "相位 / °", "Phase / °")) :
                        (ReportLanguages.Ui(language, "位置 / m", "Position / m")), axisFont, ink,
                        new RectangleF(Plot.Left, Plot.Bottom + 92, Plot.Width, 75), centered);
                GraphicsState state = g.Save();
                g.TranslateTransform(42, Plot.Top + Plot.Height / 2f);
                g.RotateTransform(-90);
                using (var centered = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(ReportLanguages.Ui(language, "放电量 / pC", "Discharge / pC"), axisFont, ink,
                        new RectangleF(-Plot.Height / 2f, 0, Plot.Height, 75), centered);
                g.Restore(state);
                if (encodedColor)
                {
                    string colorLabel = image.ChartMode == 5 ? (ReportLanguages.Ui(language, "颜色：电压 / kV", "Color: voltage / kV")) :
                        (ReportLanguages.Ui(language, "颜色：位置 / m", "Color: position / m"));
                    g.DrawString(colorLabel, tickFont, ink, 1100, 1550);
                    for (int step = 0; step < 600; step++)
                        using (var brush = new SolidBrush(Gradient(step / 599.0)))
                            g.FillRectangle(brush, 1510 + step, 1557, 1, 30);
                    g.DrawString("0", tickFont, ink, 1460, 1544);
                    g.DrawString(colorMax.ToString("0.#", CultureInfo.InvariantCulture), tickFont, ink, 2130, 1544);
                }
                else for (int phase = 1; phase <= 3; phase++)
                {
                    float x = 1760 + (phase - 1) * 330;
                    using (Brush marker = new SolidBrush(Colors[phase - 1])) g.FillEllipse(marker, x, 1560, 26, 26);
                    g.DrawString(chinese ? ((char)('A' + phase - 1)) + "相" : "L" + phase.ToString(CultureInfo.InvariantCulture),
                        tickFont, ink, x + 43, 1542);
                }
            }
        }

        static Color Gradient(double fraction)
        {
            double t = Math.Max(0, Math.Min(1, fraction));
            Color low = Color.FromArgb(20, 125, 156), high = Color.FromArgb(214, 94, 55);
            return Color.FromArgb((int)(low.R + (high.R - low.R) * t),
                (int)(low.G + (high.G - low.G) * t), (int)(low.B + (high.B - low.B) * t));
        }

        static double NiceMaximum(double value)
        {
            if (!Finite(value) || value <= 0) return 1;
            double unit = Math.Pow(10, Math.Floor(Math.Log10(value)));
            return Math.Ceiling(value / unit) * unit;
        }

        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static Font Font(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei", size, style, GraphicsUnit.Pixel); }
            catch { return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Pixel); }
        }
    }
}
