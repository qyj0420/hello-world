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
    /// <summary>Pure System.Drawing Q-L and N-L report renderer for accepted location rows.</summary>
    public static class LocationChartRenderer
    {
        public const int CanvasWidth = 2400;
        public const int CanvasHeight = 1250;
        public const float OutputDpi = 300f;
        private const int HistogramBins = 100;

        public static string RenderChargeLocation(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            double cableLengthM, double deviceU0Kv, int languageIndex, string title, string outputPath)
        {
            return Render(matrix, interval, phase, cableLengthM, deviceU0Kv, languageIndex, title, outputPath, true, null);
        }

        public static string RenderCountLocation(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            double cableLengthM, double deviceU0Kv, int languageIndex, string title, string outputPath)
        {
            return Render(matrix, interval, phase, cableLengthM, deviceU0Kv, languageIndex, title, outputPath, false, null);
        }

        public static string RenderChargeLocation(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            double cableLengthM, double deviceU0Kv, string title, string outputPath)
        {
            return RenderChargeLocation(matrix, interval, phase, cableLengthM, deviceU0Kv, 0, title, outputPath);
        }

        public static string RenderCountLocation(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            double cableLengthM, double deviceU0Kv, string title, string outputPath)
        {
            return RenderCountLocation(matrix, interval, phase, cableLengthM, deviceU0Kv, 0, title, outputPath);
        }

        public static string RenderChargeLocation(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            ProjectReportData project, int languageIndex, string title, string outputPath)
        {
            if (project == null) throw new ArgumentNullException("project");
            return Render(matrix, interval, phase, project.CableLengthM, project.DeviceU0Kv, languageIndex, title, outputPath, true, project.JointPositions);
        }

        public static string RenderCountLocation(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            ProjectReportData project, int languageIndex, string title, string outputPath)
        {
            if (project == null) throw new ArgumentNullException("project");
            return Render(matrix, interval, phase, project.CableLengthM, project.DeviceU0Kv, languageIndex, title, outputPath, false, project.JointPositions);
        }

        public static string RenderQL(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            double cableLengthM, double deviceU0Kv, int languageIndex, string title, string outputPath)
        {
            return RenderChargeLocation(matrix, interval, phase, cableLengthM, deviceU0Kv, languageIndex, title, outputPath);
        }

        public static string RenderNL(double[,] matrix, VoltageInterval interval, ReportPhase phase,
            double cableLengthM, double deviceU0Kv, int languageIndex, string title, string outputPath)
        {
            return RenderCountLocation(matrix, interval, phase, cableLengthM, deviceU0Kv, languageIndex, title, outputPath);
        }

        private static string Render(double[,] matrix, VoltageInterval interval, ReportPhase phase, double requestedLength,
            double deviceU0Kv, int languageIndex, string title, string outputPath, bool chargeChart, double[] joints)
        {
            if (matrix == null) throw new ArgumentNullException("matrix");
            int columns = matrix.GetLength(1);
            if (columns != 36 && columns != 40) throw new ArgumentException("Location matrix must contain exactly 36 or 40 columns.", "matrix");
            if (phase < ReportPhase.All || phase > ReportPhase.C) throw new ArgumentOutOfRangeException("phase");
            ReportLanguages.Validate(languageIndex);
            interval = interval ?? VoltageInterval.AllResults();
            if (!interval.IsAllResults && (!Finite(deviceU0Kv) || deviceU0Kv <= 0.0))
                throw new ArgumentException("A finite positive deviceU0Kv is required for a voltage interval.", "deviceU0Kv");

            List<LocationPoint> candidates = ReadAccepted(matrix, interval, phase, deviceU0Kv);
            double cableLength = ResolveCableLength(requestedLength, candidates);
            List<LocationPoint> points = candidates.Where(p => p.PositionM >= 0.0 && p.PositionM <= cableLength).ToList();
            string fullPath = ResolvePngPath(outputPath);
            string chartTitle = String.IsNullOrWhiteSpace(title) ? DefaultTitle(languageIndex, chargeChart, phase) : title.Trim();

            using (var bitmap = new Bitmap(CanvasWidth, CanvasHeight, PixelFormat.Format24bppRgb))
            {
                bitmap.SetResolution(OutputDpi, OutputDpi);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    Configure(graphics);
                    if (chargeChart) DrawScatter(graphics, points, cableLength, joints, interval, phase, languageIndex, chartTitle);
                    else DrawHistogram(graphics, points, cableLength, joints, interval, phase, languageIndex, chartTitle);
                }
                using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None)) bitmap.Save(stream, ImageFormat.Png);
            }
            return fullPath;
        }

        internal static bool HasData(double[,] matrix, VoltageInterval interval, ReportPhase phase, double length, double u0)
        {
            if (matrix == null || (matrix.GetLength(1) != 36 && matrix.GetLength(1) != 40)) return false;
            interval = interval ?? VoltageInterval.AllResults();
            if (!interval.IsAllResults && (!Finite(u0) || u0 <= 0)) return false;
            var points = ReadAccepted(matrix, interval, phase, u0);
            double cableLength = ResolveCableLength(length, points);
            return points.Any(p => p.PositionM >= 0 && p.PositionM <= cableLength);
        }

        private static List<LocationPoint> ReadAccepted(double[,] matrix, VoltageInterval interval, ReportPhase requestedPhase, double deviceU0Kv)
        {
            var points = new List<LocationPoint>(); int rows = matrix.GetLength(0);
            for (int row = 0; row < rows; row++)
            {
                double accepted = matrix[row, 35];
                if (!Finite(accepted) || Math.Abs(accepted - 1.0) > 1e-9) continue;
                double phaseValue = matrix[row, 0]; if (!Finite(phaseValue)) continue;
                int physicalPhase = (int)Math.Round(phaseValue, MidpointRounding.AwayFromZero);
                if (physicalPhase < 1 || physicalPhase > 3 || Math.Abs(phaseValue - physicalPhase) > 1e-6) continue;
                if (requestedPhase != ReportPhase.All && physicalPhase != (int)requestedPhase) continue;
                double voltage = matrix[row, 2];
                if (!interval.IsAllResults && !interval.ContainsVoltage(voltage, deviceU0Kv)) continue;
                double position = matrix[row, 13]; if (!Finite(position)) continue;
                double charge = matrix[row, 16];
                if (!Finite(charge)) charge = matrix[row, 15];
                if (!Finite(charge)) continue;
                points.Add(new LocationPoint { Phase = physicalPhase, PositionM = position, ChargePc = Math.Abs(charge), VoltageKv = voltage });
            }
            return points;
        }

        private static double ResolveCableLength(double requested, List<LocationPoint> points)
        {
            if (Finite(requested) && requested > 0.0) return requested;
            double maximum = points.Where(p => Finite(p.PositionM) && p.PositionM > 0.0).Select(p => p.PositionM).DefaultIfEmpty(0.0).Max();
            return maximum > 0.0 ? Math.Ceiling(maximum) : 1.0;
        }

        private static string ResolvePngPath(string outputPath)
        {
            if (String.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("PNG output path cannot be blank.", "outputPath");
            string full = Path.GetFullPath(outputPath.Trim());
            if (!String.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output path must end in .png.", "outputPath");
            string directory = Path.GetDirectoryName(full); if (String.IsNullOrEmpty(directory)) throw new ArgumentException("Output path has no parent directory.", "outputPath");
            Directory.CreateDirectory(directory); return full;
        }

        private static void Configure(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(Color.White);
        }

        private static readonly RectangleF Plot = new RectangleF(215f, 215f, 2050f, 820f);

        private static void DrawScatter(Graphics g, List<LocationPoint> points, double cableLength, double[] joints,
            VoltageInterval interval, ReportPhase phase, int language, string title)
        {
            Labels labels = new Labels(language);
            double yMaximum = NiceMaximum(points.Select(p => p.ChargePc).DefaultIfEmpty(0.0).Max() * 1.08, 6);
            DrawHeader(g, title, Subtitle(labels, interval, phase, points.Count), labels.ChargeLocation);
            DrawAxes(g, Plot, cableLength, yMaximum, labels.PositionAxis, labels.ChargeAxis, false);
            DrawJoints(g, Plot, cableLength, joints, labels);
            foreach (LocationPoint point in points)
            {
                float x = MapX(point.PositionM, Plot, cableLength), y = MapY(point.ChargePc, Plot, yMaximum);
                DrawMarker(g, point.Phase, x, y, 16f);
            }
            DrawLegend(g, phase, points.Select(p => p.Phase).Distinct().ToArray(), labels, false);
            if (points.Count == 0) DrawEmptyState(g, Plot, labels.NoAcceptedData, labels.ChangeSelection);
            DrawPlotBorder(g, Plot);
        }

        private static void DrawHistogram(Graphics g, List<LocationPoint> points, double cableLength, double[] joints,
            VoltageInterval interval, ReportPhase phase, int language, string title)
        {
            Labels labels = new Labels(language); double[,] counts = new double[3, HistogramBins];
            foreach (LocationPoint point in points)
            {
                int bin = point.PositionM >= cableLength ? HistogramBins - 1 : (int)Math.Floor(point.PositionM / cableLength * HistogramBins);
                if (bin >= 0 && bin < HistogramBins) counts[point.Phase - 1, bin]++;
            }
            double rawMaximum = 0.0;
            for (int p = 0; p < 3; p++) for (int b = 0; b < HistogramBins; b++) rawMaximum = Math.Max(rawMaximum, counts[p, b]);
            double yMaximum = NiceMaximum(rawMaximum * 1.1, 6);
            DrawHeader(g, title, Subtitle(labels, interval, phase, points.Count), labels.CountLocation);
            DrawAxes(g, Plot, cableLength, yMaximum, labels.PositionAxis, labels.CountAxis, true);
            DrawJoints(g, Plot, cableLength, joints, labels);
            int[] phases = phase == ReportPhase.All ? new[] { 1, 2, 3 } : new[] { (int)phase };
            float groupWidth = Plot.Width / HistogramBins * 0.82f, barWidth = Math.Max(1.5f, groupWidth / phases.Length);
            using (var border = new Pen(Color.FromArgb(120, ReportTheme.Navy), 0.8f))
            {
                for (int bin = 0; bin < HistogramBins; bin++)
                {
                    float center = Plot.Left + (bin + 0.5f) * Plot.Width / HistogramBins;
                    for (int index = 0; index < phases.Length; index++)
                    {
                        int physical = phases[index]; double count = counts[physical - 1, bin]; if (count <= 0.0) continue;
                        float height = (float)(count / yMaximum * Plot.Height);
                        float left = center - groupWidth / 2f + index * barWidth;
                        using (var brush = new SolidBrush(PhaseColor(physical)))
                        {
                            var bar = new RectangleF(left, Plot.Bottom - height, Math.Max(1f, barWidth - 1f), height);
                            g.FillRectangle(brush, bar); g.DrawRectangle(border, bar.X, bar.Y, bar.Width, bar.Height);
                        }
                    }
                }
            }
            DrawLegend(g, phase, phases.Where(p => Enumerable.Range(0, HistogramBins).Any(b => counts[p - 1, b] > 0.0)).ToArray(), labels, true);
            if (points.Count == 0) DrawEmptyState(g, Plot, labels.NoAcceptedData, labels.ChangeSelection);
            DrawPlotBorder(g, Plot);
        }

        private static void DrawHeader(Graphics g, string title, string subtitle, string badge)
        {
            using (Font titleFont = NewFont(61f, FontStyle.Bold))
            using (Font subtitleFont = NewFont(38f, FontStyle.Regular))
            using (Font badgeFont = NewFont(36f, FontStyle.Bold))
            using (var titleBrush = new SolidBrush(ReportTheme.Navy))
            using (var mutedBrush = new SolidBrush(ReportTheme.Muted))
            using (var cyanBrush = new SolidBrush(ReportTheme.Cyan))
            using (var badgeBrush = new SolidBrush(Color.FromArgb(225, ReportTheme.Selection)))
            using (var badgePen = new Pen(ReportTheme.Border, 1.2f))
            {
                g.FillRectangle(cyanBrush, 85f, 47f, 9f, 118f);
                using (StringFormat ellipsis = EllipsisFormat())
                {
                    g.DrawString(title, titleFont, titleBrush, new RectangleF(120f, 24f, 1800f, 78f), ellipsis);
                    g.DrawString(subtitle, subtitleFont, mutedBrush, new RectangleF(122f, 108f, 1850f, 54f), ellipsis);
                }
                SizeF size = g.MeasureString(badge, badgeFont); float width = Math.Max(138f, size.Width + 34f);
                var badgeRect = new RectangleF(CanvasWidth - width - 84f, 67f, width, 62f);
                g.FillRectangle(badgeBrush, badgeRect); g.DrawRectangle(badgePen, badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height);
                using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(badge, badgeFont, titleBrush, badgeRect, centered);
            }
        }

        private static void DrawAxes(Graphics g, RectangleF plot, double xMaximum, double yMaximum,
            string xName, string yName, bool integerY)
        {
            using (Font tickFont = NewFont(42f, FontStyle.Regular))
            using (Font axisFont = NewFont(47f, FontStyle.Bold))
            using (var tickBrush = new SolidBrush(ReportTheme.Ink))
            using (var gridPen = new Pen(ReportTheme.Grid, 1.2f))
            using (var axisPen = new Pen(ReportTheme.Navy, 2.2f))
            {
                for (int i = 0; i <= 10; i++)
                {
                    float x = plot.Left + plot.Width * i / 10f; g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    string text = (xMaximum * i / 10.0).ToString(xMaximum < 20 ? "0.0" : "0", CultureInfo.InvariantCulture);
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
                        g.DrawString(text, tickFont, tickBrush, new RectangleF(x - 85f, plot.Bottom + 12f, 170f, 62f), sf);
                }
                foreach (double value in BuildYAxisTicks(yMaximum, integerY))
                {
                    float y = MapY(value, plot, yMaximum); g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    string text = integerY ? value.ToString("0", CultureInfo.InvariantCulture) : FormatAxis(value);
                    using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                        g.DrawString(text, tickFont, tickBrush, new RectangleF(36f, y - 32f, 156f, 66f), sf);
                }
                g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom); g.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
                using (var centered = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(xName, axisFont, tickBrush, new RectangleF(plot.Left, plot.Bottom + 78f, plot.Width, 68f), centered);
                GraphicsState state = g.Save(); g.TranslateTransform(18f, plot.Top + plot.Height / 2f); g.RotateTransform(-90f);
                using (var centered = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(yName, axisFont, tickBrush, new RectangleF(-plot.Height / 2f, 0f, plot.Height, 66f), centered);
                g.Restore(state);
            }
        }

        private static void DrawJoints(Graphics g, RectangleF plot, double cableLength, double[] joints, Labels labels)
        {
            double[] visible = (joints ?? new double[0]).Where(Finite).Where(x => x > 0.0 && x < cableLength).Distinct().OrderBy(x => x).ToArray();
            using (Font font = NewFont(31f, FontStyle.Regular))
            using (var pen = new Pen(Color.FromArgb(150, ReportTheme.Cyan), 2.5f))
            using (var brush = new SolidBrush(ReportTheme.Primary))
            {
                pen.DashStyle = DashStyle.Dash;
                for (int i = 0; i < visible.Length; i++)
                {
                    float x = MapX(visible[i], plot, cableLength); g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                    string text = labels.Joint + " " + (i + 1).ToString(CultureInfo.InvariantCulture) + "  " + visible[i].ToString("0.#", CultureInfo.InvariantCulture) + " m";
                    GraphicsState state = g.Save(); g.TranslateTransform(x + 5f, plot.Top + 10f); g.RotateTransform(90f);
                    g.DrawString(text, font, brush, 0f, 0f); g.Restore(state);
                }
            }
        }

        private static void DrawPlotBorder(Graphics g, RectangleF plot)
        {
            using (var pen = new Pen(ReportTheme.Border, 3.0f)) g.DrawRectangle(pen, plot.X, plot.Y, plot.Width, plot.Height);
        }

        private static void DrawMarker(Graphics g, int phase, float x, float y, float radius)
        {
            Color color = phase == 1 ? Color.FromArgb(184, 135, 22) : PhaseColor(phase);
            using (var white = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.FromArgb(230, color), 3.4f))
            {
                if (phase == 1)
                {
                g.FillEllipse(white, x - radius, y - radius, radius * 2f, radius * 2f);
                    g.DrawEllipse(pen, x - radius, y - radius, radius * 2f, radius * 2f);
                }
                else if (phase == 2)
                {
                    var rect = new RectangleF(x - radius, y - radius, radius * 2f, radius * 2f);
                    g.FillRectangle(white, rect); g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                }
                else
                {
                    PointF[] triangle = { new PointF(x, y - radius), new PointF(x + radius, y + radius), new PointF(x - radius, y + radius) };
                    g.FillPolygon(white, triangle); g.DrawPolygon(pen, triangle);
                }
            }
        }

        private static void DrawLegend(Graphics g, ReportPhase requested, int[] active, Labels labels, bool bars)
        {
            int[] phases = (active ?? new int[0]).Where(p => p >= 1 && p <= 3).Distinct().OrderBy(p => p).ToArray();
            if (phases.Length == 0) return;
            float width = 280f * phases.Length + 42f, left = Plot.Right - width - 18f, top = Plot.Top + 18f;
            var box = new RectangleF(left, top, width, 84f);
            using (var background = new SolidBrush(Color.FromArgb(238, Color.White)))
            using (var border = new Pen(ReportTheme.Border, 1.1f))
            using (var font = NewFont(34f, FontStyle.Bold))
            using (var text = new SolidBrush(ReportTheme.Ink))
            {
                g.FillRectangle(background, box); g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
                for (int i = 0; i < phases.Length; i++)
                {
                    float x = left + 34f + i * 280f, y = top + 42f;
                    if (bars)
                    {
                        using (var brush = new SolidBrush(PhaseColor(phases[i]))) g.FillRectangle(brush, x - 10f, y - 9f, 20f, 18f);
                    }
                    else DrawMarker(g, phases[i], x, y, 10f);
                    g.DrawString(labels.PhaseName(phases[i]), font, text, x + 27f, y - 23f);
                }
            }
        }

        private static void DrawEmptyState(Graphics g, RectangleF plot, string heading, string detail)
        {
            var card = new RectangleF(plot.Left + plot.Width * 0.27f, plot.Top + plot.Height * 0.34f, plot.Width * 0.46f, 190f);
            using (var shadow = new SolidBrush(Color.FromArgb(22, ReportTheme.Navy))) g.FillRectangle(shadow, card.X + 7f, card.Y + 8f, card.Width, card.Height);
            using (var background = new SolidBrush(ReportTheme.Surface))
            using (var border = new Pen(ReportTheme.Border, 1.6f))
            using (var accent = new SolidBrush(ReportTheme.Cyan))
            using (var headingFont = NewFont(43f, FontStyle.Bold))
            using (var detailFont = NewFont(32f, FontStyle.Regular))
            using (var headingBrush = new SolidBrush(ReportTheme.Navy))
            using (var detailBrush = new SolidBrush(ReportTheme.Muted))
            using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.FillRectangle(background, card); g.DrawRectangle(border, card.X, card.Y, card.Width, card.Height);
                g.FillRectangle(accent, card.X, card.Y, card.Width, 6f);
                g.DrawString(heading, headingFont, headingBrush, new RectangleF(card.X + 24f, card.Y + 31f, card.Width - 48f, 58f), centered);
                g.DrawString(detail, detailFont, detailBrush, new RectangleF(card.X + 38f, card.Y + 96f, card.Width - 76f, 62f), centered);
            }
        }

        private static string Subtitle(Labels labels, VoltageInterval interval, ReportPhase phase, int count)
        {
            string range = interval.IsAllResults ? labels.AllLocationResults :
                (!String.IsNullOrWhiteSpace(interval.Title) ? interval.Title : interval.MinimumU0.ToString("0.##", CultureInfo.InvariantCulture) + "–" + interval.MaximumU0.ToString("0.##", CultureInfo.InvariantCulture) + "U0");
            return range + "   ·   " + labels.PhaseName((int)phase) + "   ·   " + labels.AcceptedRows + " " + count.ToString(CultureInfo.InvariantCulture);
        }

        private static string DefaultTitle(int language, bool charge, ReportPhase phase)
        {
            bool chinese = language == 0; string prefix = phase == ReportPhase.All ? "ALL " : (chinese ? phase + "相" : "L" + (int)phase + " ");
            return prefix + (charge ? (ReportLanguages.Ui(language, "定位结果 Q-L 图", "location Q-L chart")) : (ReportLanguages.Ui(language, "定位结果 N-L 图", "location N-L chart")));
        }

        private static Color PhaseColor(int phase)
        {
            return phase == 1 ? ReportTheme.PhaseA : phase == 2 ? ReportTheme.PhaseB : ReportTheme.PhaseC;
        }
        private static float MapX(double value, RectangleF plot, double maximum) { return plot.Left + (float)(value / maximum) * plot.Width; }
        private static float MapY(double value, RectangleF plot, double maximum) { return plot.Bottom - (float)(value / maximum) * plot.Height; }
        internal static double[] BuildYAxisTicks(double maximum, bool integerY)
        {
            if (!integerY) return Enumerable.Range(0, 7).Select(i => maximum * i / 6.0).ToArray();
            // Counts are discrete: choose whole-count tick positions before
            // drawing, rather than rounding fractional labels after drawing.
            // Keep the existing plot headroom, so a count maximum of one has
            // ticks 0 and 1 even when the display upper bound is 1.2.
            double rawStep = Math.Max(1.0, maximum / 6.0);
            double power = Math.Pow(10.0, Math.Floor(Math.Log10(rawStep)));
            double scaled = rawStep / power;
            double nice = scaled <= 1.0 ? 1.0 : scaled <= 2.0 ? 2.0 : scaled <= 5.0 ? 5.0 : 10.0;
            double step = Math.Max(1.0, Math.Ceiling(nice * power));
            int intervals = (int)Math.Floor(maximum / step + 1e-9);
            return Enumerable.Range(0, intervals + 1).Select(i => step * i).ToArray();
        }
        private static double NiceMaximum(double raw, int targetTicks)
        {
            if (!Finite(raw) || raw <= 0.0) return 1.0;
            double step = raw / Math.Max(2, targetTicks), power = Math.Pow(10.0, Math.Floor(Math.Log10(step))), scaled = step / power;
            double nice = scaled <= 1.0 ? 1.0 : scaled <= 2.0 ? 2.0 : scaled <= 5.0 ? 5.0 : 10.0;
            return Math.Ceiling(raw / (nice * power)) * nice * power;
        }
        private static string FormatAxis(double value)
        {
            double absolute = Math.Abs(value);
            if (absolute >= 10000.0 || (absolute > 0.0 && absolute < 0.01)) return value.ToString("0.##E+0", CultureInfo.InvariantCulture);
            if (absolute >= 100.0) return value.ToString("0", CultureInfo.InvariantCulture);
            if (absolute >= 10.0) return value.ToString("0.#", CultureInfo.InvariantCulture);
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
        private static Font NewFont(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei", size, style, GraphicsUnit.Pixel); } catch { return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Pixel); }
        }
        private static StringFormat EllipsisFormat() { return new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private sealed class Labels
        {
            private readonly bool chinese;
            private readonly int language;
            internal Labels(int language) { this.language = language; chinese = ReportLanguages.IsChinese(language); }
            internal string PositionAxis { get { return ReportLanguages.Ui(language, "位置 / m", "Position / m"); } }
            internal string ChargeAxis { get { return ReportLanguages.Ui(language, "放电量 / pC", "Discharge / pC"); } }
            internal string CountAxis { get { return ReportLanguages.Ui(language, "放电脉冲数 / 次", "Discharge pulse count"); } }
            internal string ChargeLocation { get { return "Q-L"; } }
            internal string CountLocation { get { return "N-L"; } }
            internal string NoAcceptedData { get { return ReportLanguages.Ui(language, "当前选择无已接受的定位结果", "No accepted location results for this selection"); } }
            internal string ChangeSelection { get { return ReportLanguages.Ui(language, "请调整相别或电压区间后重新生成。", "Choose another phase or voltage interval and generate again."); } }
            internal string AcceptedRows { get { return ReportLanguages.Ui(language, "已接受", "Accepted"); } }
            internal string AllLocationResults { get { return ReportLanguages.Ui(language, "所有定位结果", "All location results"); } }
            internal string Joint { get { return ReportLanguages.Ui(language, "接头", "Joint"); } }
            internal string PhaseName(int phase)
            {
                if (phase == 0) return "ALL";
                return chinese ? ((ReportPhase)phase).ToString() + "相" : "L" + phase.ToString(CultureInfo.InvariantCulture);
            }
        }

        private sealed class LocationPoint
        {
            internal int Phase;
            internal double PositionM, ChargePc, VoltageKv;
        }
    }
}
