using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HVX.ReportGenerator
{
    /// <summary>Print-resolution PRPD chart from the selected chart's original matrix CSV.</summary>
    public static class PrpdChartRenderer
    {
        public const int Width = 2400;
        public const int Height = 1350;
        private const int PhaseBins = 72;
        private const int ChargeBins = 60;
        private static readonly RectangleF Plot = new RectangleF(225f, 245f, 1775f, 875f);

        private sealed class Pulse { internal double Phase, Charge; internal string PhaseName; }

        public static bool TryRender(string csvPath, string title, int languageIndex, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath) || new FileInfo(csvPath).Length > 32L * 1024 * 1024) return false;
            List<Pulse> pulses = ReadPulses(csvPath);
            if (pulses.Count == 0) return false;
            double maximum = NiceMaximum(pulses.Max(p => Math.Abs(p.Charge)) * 1.04);
            int[,] counts = new int[PhaseBins, ChargeBins];
            foreach (Pulse pulse in pulses)
            {
                int x = Math.Min(PhaseBins - 1, Math.Max(0, (int)(pulse.Phase / 360.0 * PhaseBins)));
                double signed = DisplayCharge(pulse);
                int y = Math.Min(ChargeBins - 1, Math.Max(0, (int)((signed / maximum + 1.0) * 0.5 * ChargeBins)));
                counts[x, y]++;
            }
            int peak = 1;
            foreach (int count in counts) peak = Math.Max(peak, count);
            string full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                bitmap.SetResolution(300f, 300f);
                using (Graphics g = Graphics.FromImage(bitmap)) Draw(g, counts, peak, maximum, pulses, title, languageIndex);
                bitmap.Save(full, ImageFormat.Png);
            }
            return true;
        }

        internal static bool HasData(string csvPath)
        {
            try { return File.Exists(csvPath) && new FileInfo(csvPath).Length <= 32L * 1024 * 1024 && ReadPulses(csvPath).Count > 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // The archived spectrum stores charge magnitude for some instruments. Keep
        // measured magnitudes and show the second half-cycle below zero, as in PRPD.
        private static double DisplayCharge(Pulse pulse)
        {
            return pulse.Charge < 0.0 ? pulse.Charge :
                pulse.Phase >= 180.0 ? -pulse.Charge : pulse.Charge;
        }

        private static List<Pulse> ReadPulses(string path)
        {
            var pulses = new List<Pulse>();
            using (var reader = new StreamReader(path))
            {
                string header = reader.ReadLine();
                if (header == null || !header.StartsWith("SourceRow,PhaseLabel,PDmapID,Column0,", StringComparison.OrdinalIgnoreCase)) return pulses;
                string line;
                while ((line = reader.ReadLine()) != null && pulses.Count < 1000000)
                {
                    string[] fields = line.Split(',');
                    if (fields.Length < 39) continue;
                    double phase, charge;
                    if (!double.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out phase) ||
                        !double.TryParse(fields[18], NumberStyles.Float, CultureInfo.InvariantCulture, out charge) ||
                        double.IsNaN(phase) || double.IsInfinity(phase) || double.IsNaN(charge) || double.IsInfinity(charge)) continue;
                    phase %= 360.0; if (phase < 0.0) phase += 360.0;
                    pulses.Add(new Pulse { Phase = phase, Charge = charge, PhaseName = fields[1] });
                }
            }
            return pulses;
        }

        private static void Draw(Graphics g, int[,] counts, int peak, double maximum, List<Pulse> pulses, string title, int language)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);
            bool chinese = language == 0;
            string phases = string.Join("/", pulses.Select(p => p.PhaseName).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => chinese ? p : Regex.Replace(p, @"\b([ABC])\b", match =>
                    "L" + ("ABC".IndexOf(match.Groups[1].Value.ToUpperInvariant(), StringComparison.Ordinal) + 1)))
                .Distinct().Take(3));
            using (Font titleFont = Font(61f, FontStyle.Bold))
            using (Font subFont = Font(35f, FontStyle.Regular))
            using (Font tickFont = Font(38f, FontStyle.Regular))
            using (Font axisFont = Font(43f, FontStyle.Bold))
            using (var ink = new SolidBrush(ReportTheme.Navy))
            using (var muted = new SolidBrush(ReportTheme.Muted))
            using (var grid = new Pen(ReportTheme.Grid, 2f))
            using (var border = new Pen(ReportTheme.Border, 3f))
            {
                using (var accent = new SolidBrush(ReportTheme.Cyan)) g.FillRectangle(accent, 85f, 51f, 9f, 126f);
                g.DrawString(string.IsNullOrWhiteSpace(title) ? "PRPD" : title, titleFont, ink, 120f, 43f);
                string subtitle = (ReportLanguages.Ui(language, "相位分辨局部放电", "Phase-resolved partial discharge")) + "   ·   " +
                    phases + "   ·   " + pulses.Count.ToString(CultureInfo.InvariantCulture) + (ReportLanguages.Ui(language, " 个脉冲", " pulses"));
                g.DrawString(subtitle, subFont, muted, 122f, 137f);

                for (int i = 0; i <= 6; i++)
                {
                    float x = Plot.Left + Plot.Width * i / 6f;
                    g.DrawLine(grid, x, Plot.Top, x, Plot.Bottom);
                    using (var format = new StringFormat { Alignment = StringAlignment.Center })
                        g.DrawString((i * 60).ToString(CultureInfo.InvariantCulture) + "°", tickFont, ink,
                            new RectangleF(x - 90f, Plot.Bottom + 17f, 180f, 60f), format);
                }
                for (int i = 0; i <= 4; i++)
                {
                    float y = Plot.Top + Plot.Height * i / 4f;
                    g.DrawLine(grid, Plot.Left, y, Plot.Right, y);
                    string value = (maximum * (1.0 - i * 0.5)).ToString("0", CultureInfo.InvariantCulture);
                    using (var format = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                        g.DrawString(value, tickFont, ink, new RectangleF(20f, y - 34f, 170f, 68f), format);
                }
                using (var wave = new Pen(Color.FromArgb(194, 207, 220), 3f))
                {
                    PointF[] sine = Enumerable.Range(0, 361).Select(i => new PointF(Plot.Left + Plot.Width * i / 360f,
                        Plot.Top + Plot.Height * (float)(0.5 - 0.47 * Math.Sin(i * Math.PI / 180.0)))).ToArray();
                    g.DrawLines(wave, sine);
                }
                // Paint original observations, not one rectangle per density bin.
                // Low-density points go first so the hot spots remain visible.
                foreach (Pulse pulse in pulses.OrderBy(p => PulseCount(p, counts, maximum)))
                {
                    int count = PulseCount(pulse, counts, maximum);
                    float x = Plot.Left + (float)(pulse.Phase / 360.0) * Plot.Width;
                    float y = Plot.Top + (float)((maximum - DisplayCharge(pulse)) / (2.0 * maximum)) * Plot.Height;
                    float intensity = peak <= 1 ? 0f : (count - 1f) / (peak - 1f);
                    float diameter = count >= peak && peak > 1 ? 9f : 7f;
                    using (var brush = new SolidBrush(DensityColor(intensity)))
                        g.FillEllipse(brush, x - diameter / 2f, y - diameter / 2f, diameter, diameter);
                }
                g.DrawRectangle(border, Plot.X, Plot.Y, Plot.Width, Plot.Height);
                using (var centered = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(ReportLanguages.Ui(language, "相位 / °", "Phase / °"), axisFont, ink,
                        new RectangleF(Plot.Left, Plot.Bottom + 91f, Plot.Width, 70f), centered);
                GraphicsState state = g.Save(); g.TranslateTransform(25f, Plot.Top + Plot.Height / 2f); g.RotateTransform(-90f);
                using (var centered = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(ReportLanguages.Ui(language, "放电量 / pC", "Discharge / pC"), axisFont, ink,
                        new RectangleF(-Plot.Height / 2f, 0f, Plot.Height, 72f), centered);
                g.Restore(state);
                DrawColorScale(g, peak, language, tickFont, ink, border);
            }
        }

        private static int PulseCount(Pulse pulse, int[,] counts, double maximum)
        {
            int x = Math.Min(PhaseBins - 1, Math.Max(0, (int)(pulse.Phase / 360.0 * PhaseBins)));
            int y = Math.Min(ChargeBins - 1, Math.Max(0,
                (int)((DisplayCharge(pulse) / maximum + 1.0) * 0.5 * ChargeBins)));
            return counts[x, y];
        }

        private static void DrawColorScale(Graphics g, int peak, int language, Font tickFont, Brush ink, Pen border)
        {
            const float left = 2075f, top = 294f, width = 37f, height = 765f;
            using (Font heading = Font(40f, FontStyle.Bold))
            {
                g.DrawString(ReportLanguages.Ui(language, "次数", "Count"), heading, ink, left - 6f, top - 67f);
            }
            for (int y = 0; y < (int)height; y++)
                using (var pen = new Pen(DensityColor(1f - y / height)))
                    g.DrawLine(pen, left, top + y, left + width, top + y);
            g.DrawRectangle(border, left, top, width, height);
            int[] values = { peak, Math.Max(1, (int)Math.Ceiling(peak * 0.66)),
                Math.Max(1, (int)Math.Ceiling(peak * 0.33)), 1 };
            for (int i = 0; i < values.Length; i++)
            {
                float y = top + i * height / 3f;
                g.DrawString(values[i].ToString(CultureInfo.InvariantCulture), tickFont, ink,
                    left + width + 17f, y - 28f);
            }
            using (Font note = Font(28f, FontStyle.Regular))
                g.DrawString(ReportLanguages.Ui(language, "每个相位-放电量区间", "Per phase-charge bin"),
                    note, ink, left - 5f, top + height + 24f);
        }

        private static Color DensityColor(float intensity)
        {
            Color[] stops = { Color.FromArgb(24, 72, 180), Color.FromArgb(0, 210, 230),
                Color.FromArgb(250, 220, 35), Color.FromArgb(220, 35, 28) };
            float step = Math.Max(0f, Math.Min(1f, intensity)) * 3f;
            int index = Math.Min(2, (int)step);
            float blend = step - index;
            Color a = stops[index], b = stops[index + 1];
            return Color.FromArgb((int)(a.R + (b.R - a.R) * blend),
                (int)(a.G + (b.G - a.G) * blend), (int)(a.B + (b.B - a.B) * blend));
        }

        private static double NiceMaximum(double value)
        {
            if (value <= 0.0) return 1.0;
            double power = Math.Pow(10.0, Math.Floor(Math.Log10(value)));
            double scaled = value / power;
            return (scaled <= 1.0 ? 1.0 : scaled <= 2.0 ? 2.0 : scaled <= 5.0 ? 5.0 : 10.0) * power;
        }

        private static Font Font(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei", size, style, GraphicsUnit.Pixel); }
            catch { return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Pixel); }
        }
    }
}
