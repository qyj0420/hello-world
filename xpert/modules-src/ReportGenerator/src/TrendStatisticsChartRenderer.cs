using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.ReportGenerator
{
    internal enum TrendChartKind { CountVoltage, ChargeVoltage, DacTanDelta }

    internal sealed class TrendSelection
    {
        internal TrendChartKind Kind;
        internal ReportPhase Phase;
        internal string Title = string.Empty;
    }

    internal static class TrendStatisticsChartRenderer
    {
        internal const int Width = 3000;
        internal const int Height = 1650;
        // A near full-bleed 16:9 plotting field keeps labels readable after Word/PDF scaling.
        static readonly RectangleF Plot = new RectangleF(250f, 245f, 2500f, 1120f);
        static readonly Color[] PhaseColors = { ReportTheme.PhaseA, ReportTheme.PhaseB, ReportTheme.PhaseC };

        internal static string Render(double[,] matrix, string projectPath, TrendSelection selection,
            int languageIndex, string outputPath)
        {
            if (selection == null) throw new ArgumentNullException("selection");
            string full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                bitmap.SetResolution(300f, 300f);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    Configure(g);
                    if (selection.Kind == TrendChartKind.DacTanDelta)
                        DrawTanDelta(g, ReadTanDelta(projectPath), selection, languageIndex);
                    else
                        DrawMatrixTrend(g, matrix, selection, languageIndex);
                }
                bitmap.Save(full, ImageFormat.Png);
            }
            return full;
        }

        static void DrawMatrixTrend(Graphics g, double[,] matrix, TrendSelection selection, int language)
        {
            if (matrix == null || matrix.GetLength(0) == 0 || matrix.GetLength(1) < 36)
            { DrawEmpty(g, selection.Title, language); return; }
            bool charge = selection.Kind == TrendChartKind.ChargeVoltage;
            int[] phases = selection.Phase == ReportPhase.All ? new[] { 1, 2, 3 } : new[] { (int)selection.Phase };
            var series = phases.ToDictionary(p => p, p => BuildGroups(matrix, p));
            int count = series.Values.Select(items => items.Count).DefaultIfEmpty(0).Max();
            if (count == 0) { DrawEmpty(g, selection.Title, language); return; }
            double leftMax = series.Values.SelectMany(items => items).Select(item => charge ? item.MaximumCharge : item.Count).DefaultIfEmpty(0).Max();
            double voltageMax = series.Values.SelectMany(items => items).Select(item => item.Voltage).DefaultIfEmpty(0).Max();
            leftMax = NiceMax(leftMax); voltageMax = NiceMax(voltageMax);
            string leftAxis = language == 0 ? (charge ? "放电量 / pC" : "放电次数") : (charge ? "Discharge / pC" : "Discharge count");
            DrawFrame(g, selection.Title, ReportLanguages.Ui(language, "加压次序", "Energization sequence"), leftAxis,
                ReportLanguages.Ui(language, "电压 / kV", "Voltage / kV"), count, leftMax, voltageMax);
            float slot = Plot.Width / Math.Max(1, count), group = slot * 0.66f;
            float barWidth = Math.Max(6f, group / phases.Length);
            for (int pIndex = 0; pIndex < phases.Length; pIndex++)
            {
                int phase = phases[pIndex]; List<Group> items = series[phase];
                using (var fill = new SolidBrush(Color.FromArgb(92, PhaseColors[phase - 1])))
                using (var border = new Pen(PhaseColors[phase - 1], 4.0f))
                using (var voltageFill = new SolidBrush(Color.FromArgb(48, ReportTheme.Navy)))
                using (var voltageBorder = new Pen(Color.FromArgb(125, ReportTheme.Navy), 2.5f))
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        double value = charge ? items[i].MaximumCharge : items[i].Count;
                        float h = (float)(value / leftMax * Plot.Height);
                        float x = Plot.Left + (i + 0.5f) * slot - group / 2f + pIndex * barWidth;
                        float voltageHeight = (float)(items[i].Voltage / voltageMax * Plot.Height);
                        RectangleF voltageBar = new RectangleF(x, Plot.Bottom - voltageHeight,
                            Math.Max(4f, barWidth - 4f), voltageHeight);
                        g.FillRectangle(voltageFill, voltageBar);
                        g.DrawRectangle(voltageBorder, voltageBar.X, voltageBar.Y, voltageBar.Width, voltageBar.Height);
                        if (charge && items[i].Charges.Count > 0)
                            DrawChargeBox(g, items[i].Charges, x + barWidth * 0.17f,
                                Math.Max(8f, barWidth * 0.66f), leftMax, fill, border);
                        else if (!charge)
                        {
                            RectangleF rect = new RectangleF(x + barWidth * 0.17f, Plot.Bottom - h,
                                Math.Max(4f, barWidth * 0.66f - 4f), h);
                            g.FillRectangle(fill, rect); g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
                        }
                    }
                }
            }
            if (charge) DrawBoxLegend(g, language);
            DrawLegend(g, phases, language, true, true);
        }

        static void DrawTanDelta(Graphics g, Dictionary<int, List<TanPoint>> source, TrendSelection selection, int language)
        {
            int[] phases = selection.Phase == ReportPhase.All ? new[] { 1, 2, 3 } : new[] { (int)selection.Phase };
            int count = phases.Select(p => source.ContainsKey(p) ? source[p].Count : 0).DefaultIfEmpty(0).Max();
            if (count == 0) { DrawEmpty(g, selection.Title, language); return; }
            double tdMax = phases.SelectMany(p => source.ContainsKey(p) ? source[p] : new List<TanPoint>()).Select(x => x.TanDelta).DefaultIfEmpty(0).Max();
            double voltageMax = phases.SelectMany(p => source.ContainsKey(p) ? source[p] : new List<TanPoint>()).Select(x => x.Voltage).DefaultIfEmpty(0).Max();
            tdMax = NiceMax(tdMax); voltageMax = NiceMax(voltageMax);
            DrawFrame(g, selection.Title, ReportLanguages.Ui(language, "加压次序", "Energization sequence"),
                ReportLanguages.Ui(language, "介损 / %", "Tan delta / %"), ReportLanguages.Ui(language, "电压 / kV", "Voltage / kV"),
                count, tdMax, voltageMax);
            float slot = Plot.Width / Math.Max(1, count);
            for (int phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
            {
                int phase = phases[phaseIndex];
                List<TanPoint> items = source.ContainsKey(phase) ? source[phase] : new List<TanPoint>();
                using (var voltageFill = new SolidBrush(Color.FromArgb(76, PhaseColors[phase - 1])))
                using (var voltageBorder = new Pen(Color.FromArgb(150, PhaseColors[phase - 1]), 3f))
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        float x = Plot.Left + (i + 0.5f) * slot;
                        float barWidth = Math.Max(12f, slot * 0.56f / phases.Length);
                        float barX = x - slot * 0.28f + phaseIndex * barWidth;
                        float voltageHeight = (float)(items[i].Voltage / voltageMax * Plot.Height);
                        RectangleF voltageBar = new RectangleF(barX, Plot.Bottom - voltageHeight, barWidth - 3f, voltageHeight);
                        g.FillRectangle(voltageFill, voltageBar);
                        g.DrawRectangle(voltageBorder, voltageBar.X, voltageBar.Y, voltageBar.Width, voltageBar.Height);
                    }
                }
            }
            foreach (int phase in phases)
            {
                List<TanPoint> items = source.ContainsKey(phase) ? source[phase] : new List<TanPoint>();
                PointF? previous = null;
                using (var line = new Pen(PhaseColors[phase - 1], 8f))
                using (var marker = new SolidBrush(PhaseColors[phase - 1]))
                    for (int i = 0; i < items.Count; i++)
                    {
                        float x = Plot.Left + (i + 0.5f) * slot;
                        PointF td = new PointF(x, Plot.Bottom - (float)(items[i].TanDelta / tdMax * Plot.Height));
                        if (previous.HasValue) g.DrawLine(line, previous.Value, td);
                        g.FillEllipse(marker, td.X - 11f, td.Y - 11f, 22f, 22f);
                        previous = td;
                    }
            }
            DrawLegend(g, phases, language, true, true);
        }

        static void DrawChargeBox(Graphics g, List<double> charges, float x, float width, double axisMax,
            Brush fill, Pen border)
        {
            double[] sorted = charges.OrderBy(value => value).ToArray();
            float minimum = ChargeY(sorted[0], axisMax), maximum = ChargeY(sorted[sorted.Length - 1], axisMax);
            float q1 = ChargeY(Quantile(sorted, 0.25), axisMax), median = ChargeY(Quantile(sorted, 0.5), axisMax);
            float q3 = ChargeY(Quantile(sorted, 0.75), axisMax), mean = ChargeY(sorted.Average(), axisMax);
            float center = x + width / 2f, cap = Math.Min(width * 0.28f, 18f);
            g.DrawLine(border, center, maximum, center, minimum);
            g.DrawLine(border, center - cap, maximum, center + cap, maximum);
            g.DrawLine(border, center - cap, minimum, center + cap, minimum);
            RectangleF box = new RectangleF(x, Math.Min(q1, q3), width, Math.Max(4f, Math.Abs(q1 - q3)));
            g.FillRectangle(fill, box); g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
            g.DrawLine(border, x, median, x + width, median);
            using (var marker = new SolidBrush(Color.White)) g.FillEllipse(marker, center - 7f, mean - 7f, 14f, 14f);
            g.DrawEllipse(border, center - 7f, mean - 7f, 14f, 14f);
        }

        static float ChargeY(double charge, double axisMax)
        { return Plot.Bottom - (float)(charge / axisMax * Plot.Height); }

        static double Quantile(double[] sorted, double fraction)
        {
            if (sorted.Length == 1) return sorted[0];
            double index = (sorted.Length - 1) * fraction;
            int lower = (int)Math.Floor(index), upper = (int)Math.Ceiling(index);
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
        }

        static void DrawBoxLegend(Graphics g, int language)
        {
            using (Font font = Font(33f, FontStyle.Regular))
            using (Brush ink = new SolidBrush(ReportTheme.Ink))
                g.DrawString(ReportLanguages.Ui(language, "箱体 Q1–Q3   横线 中位数   圆点 均值   须线 最小–最大", "Box Q1–Q3   Line median   Dot mean   Whiskers min–max"), font, ink,
                    new RectangleF(Plot.Left, 176f, 1560f, 52f));
        }

        internal static bool HasData(double[,] matrix, string projectPath, TrendSelection selection)
        {
            if (selection == null) return false;
            int[] phases = selection.Phase == ReportPhase.All ? new[] { 1, 2, 3 } : new[] { (int)selection.Phase };
            if (selection.Kind == TrendChartKind.DacTanDelta)
            {
                var points = ReadTanDelta(projectPath);
                return phases.Any(p => points.ContainsKey(p) && points[p].Count > 0);
            }
            if (matrix == null || matrix.GetLength(1) < 36) return false;
            return phases.Any(p => BuildGroups(matrix, p).Any(g => selection.Kind == TrendChartKind.CountVoltage || g.Charges.Count > 0));
        }

        static List<Group> BuildGroups(double[,] matrix, int phase)
        {
            var ids = new List<double>(); var groups = new List<Group>();
            for (int row = 0; row < matrix.GetLength(0); row++)
            {
                if (!Finite(matrix[row, 0]) || Math.Abs(matrix[row, 0] - phase) > 1e-6) continue;
                if (!Finite(matrix[row, 35]) || Math.Abs(matrix[row, 35] - 1.0) > 1e-9) continue;
                double id = matrix[row, 1], voltage = matrix[row, 2], charge = matrix[row, 16];
                if (!Finite(id) || !Finite(voltage)) continue;
                if (!Finite(charge)) charge = matrix[row, 15];
                int index = ids.FindIndex(value => value.Equals(id));
                if (index < 0) { ids.Add(id); groups.Add(new Group { Voltage = voltage }); index = groups.Count - 1; }
                groups[index].Count++;
                if (Finite(charge))
                {
                    double magnitude = Math.Abs(charge);
                    groups[index].MaximumCharge = Math.Max(groups[index].MaximumCharge, magnitude);
                    groups[index].Charges.Add(magnitude);
                }
            }
            return groups;
        }

        static Dictionary<int, List<TanPoint>> ReadTanDelta(string projectPath)
        {
            var result = new Dictionary<int, List<TanPoint>> { { 1, new List<TanPoint>() }, { 2, new List<TanPoint>() }, { 3, new List<TanPoint>() } };
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return result;
            var pattern = new Regex("^([ABC])_([-+]?[0-9]+(?:\\.[0-9]+)?)kV_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (string path in Directory.GetFiles(projectPath, "*.dem", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Match match = pattern.Match(Path.GetFileNameWithoutExtension(path)); if (!match.Success) continue;
                int phase = "ABC".IndexOf(match.Groups[1].Value.ToUpperInvariant(), StringComparison.Ordinal) + 1;
                double voltage; if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out voltage)) continue;
                double tan = ReadHeaderNumber(path, "D-losses"); if (!Finite(tan)) continue;
                result[phase].Add(new TanPoint { Voltage = voltage, TanDelta = tan });
            }
            return result;
        }

        static double ReadHeaderNumber(string path, string key)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path); Encoding encoding = DetectEncoding(bytes);
                string[] lines = encoding.GetString(bytes).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                foreach (string line in lines.Take(24)) if (line.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Match m = Regex.Match(line, "[-+]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][-+]?[0-9]+)?");
                    double value; if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
                }
            }
            catch { }
            return double.NaN;
        }

        static Encoding DetectEncoding(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(false, false);
            try { var utf8 = new UTF8Encoding(false, true); utf8.GetString(bytes); return utf8; }
            catch { return Encoding.GetEncoding(936); }
        }

        static void DrawFrame(Graphics g, string title, string xName, string leftName, string rightName,
            int count, double leftMax, double rightMax)
        {
            using (Font titleFont = Font(64f, FontStyle.Bold))
            using (Font axisFont = Font(46f, FontStyle.Bold))
            using (Font tickFont = Font(40f, FontStyle.Regular))
            using (Brush ink = new SolidBrush(ReportTheme.Ink))
            using (Brush plotFill = new SolidBrush(Color.FromArgb(252, 253, 254)))
            using (Pen grid = new Pen(Color.FromArgb(224, 232, 239), 2f))
            using (Pen axis = new Pen(ReportTheme.Navy, 5f))
            using (Pen accent = new Pen(ReportTheme.Cyan, 8f))
            {
                g.DrawString(title ?? string.Empty, titleFont, ink, new RectangleF(120, 56, Width - 240, 82));
                g.DrawLine(accent, 120f, 154f, Width - 120f, 154f);
                g.FillRectangle(plotFill, Plot);
                for (int i = 0; i <= 6; i++)
                {
                    float y = Plot.Bottom - Plot.Height * i / 6f; g.DrawLine(grid, Plot.Left, y, Plot.Right, y);
                    g.DrawString((leftMax * i / 6d).ToString("0.##", CultureInfo.InvariantCulture), tickFont, ink, new RectangleF(20, y - 27, 210, 58), RightFormat());
                    g.DrawString((rightMax * i / 6d).ToString("0.##", CultureInfo.InvariantCulture), tickFont, ink, new RectangleF(Plot.Right + 20, y - 27, 210, 58));
                }
                int step = Math.Max(1, (int)Math.Ceiling(count / 14d));
                for (int i = 0; i < count; i += step)
                {
                    float x = Plot.Left + (i + 0.5f) * Plot.Width / count;
                    g.DrawString(i.ToString(CultureInfo.InvariantCulture), tickFont, ink, new RectangleF(x - 54, Plot.Bottom + 20, 108, 54), CenterFormat());
                }
                g.DrawLine(axis, Plot.Left, Plot.Top, Plot.Left, Plot.Bottom);
                g.DrawLine(axis, Plot.Left, Plot.Bottom, Plot.Right, Plot.Bottom);
                g.DrawLine(axis, Plot.Right, Plot.Top, Plot.Right, Plot.Bottom);
                g.DrawString(xName, axisFont, ink, new RectangleF(Plot.Left, Plot.Bottom + 82, Plot.Width, 64), CenterFormat());
                DrawVertical(g, leftName, axisFont, ink, 62, Plot.Top + Plot.Height / 2f);
                DrawVertical(g, rightName, axisFont, ink, Width - 62, Plot.Top + Plot.Height / 2f);
            }
        }

        static void DrawLegend(Graphics g, int[] phases, int language, bool voltage, bool voltageBars)
        {
            using (Font font = Font(38f, FontStyle.Bold))
            using (Brush ink = new SolidBrush(ReportTheme.Ink))
            {
                float x = Plot.Right - phases.Length * 215f - (voltage ? 300f : 0f), y = 177f;
                foreach (int phase in phases)
                {
                    using (var brush = new SolidBrush(PhaseColors[phase - 1])) g.FillRectangle(brush, x, y + 9, 38, 24);
                    g.DrawString(language == 0 ? (char)('A' + phase - 1) + "相" : "L" + phase.ToString(CultureInfo.InvariantCulture), font, ink, x + 50, y); x += 215f;
                }
                if (voltage)
                {
                    if (voltageBars) using (var brush = new SolidBrush(Color.FromArgb(90, ReportTheme.PhaseB))) g.FillRectangle(brush, x, y + 9, 50, 24);
                    else using (var pen = new Pen(ReportTheme.Navy, 6f)) g.DrawLine(pen, x, y + 22, x + 62, y + 22);
                    g.DrawString(ReportLanguages.Ui(language, "电压", "Voltage"), font, ink, x + 74, y);
                }
            }
        }

        static void DrawEmpty(Graphics g, string title, int language)
        {
            using (Font titleFont = Font(54f, FontStyle.Bold)) using (Font body = Font(38f, FontStyle.Regular)) using (Brush ink = new SolidBrush(ReportTheme.Ink)) using (Brush muted = new SolidBrush(ReportTheme.Muted))
            { g.DrawString(title ?? string.Empty, titleFont, ink, 120, 70); g.DrawString(ReportLanguages.Ui(language, "当前选择没有可用于统计的有效数据。", "No valid data are available for this chart."), body, muted, new RectangleF(260, 650, 2480, 100), CenterFormat()); }
        }

        static void Configure(Graphics g) { g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality; }
        static void DrawVertical(Graphics g, string text, Font font, Brush brush, float x, float y) { GraphicsState s = g.Save(); g.TranslateTransform(x, y); g.RotateTransform(-90); g.DrawString(text, font, brush, new RectangleF(-Plot.Height / 2, -30, Plot.Height, 60), CenterFormat()); g.Restore(s); }
        static StringFormat CenterFormat() { return new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; }
        static StringFormat RightFormat() { return new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center }; }
        static Font Font(float size, FontStyle style) { try { return new Font("Microsoft YaHei", size, style, GraphicsUnit.Pixel); } catch { return new Font("Arial", size, style, GraphicsUnit.Pixel); } }
        static double NiceMax(double value) { if (!Finite(value) || value <= 0) return 1; double p = Math.Pow(10, Math.Floor(Math.Log10(value))); return Math.Ceiling(value / p * 1.08) * p; }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        sealed class Group { internal int Count; internal double MaximumCharge; internal double Voltage; internal readonly List<double> Charges = new List<double>(); }
        sealed class TanPoint { internal double Voltage; internal double TanDelta; }
    }
}
