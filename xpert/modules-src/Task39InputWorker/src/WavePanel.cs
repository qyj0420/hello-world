using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Xiongxin.ZeroBench
{
    internal sealed class WavePanel : Control
    {
        private double[] voltage;
        private short[] adc;
        private double rate, scale = 1, spanMs = 60, leftMs, widthMs = 60;
        private Point dragStart;
        private double dragLeft;
        private bool dragging;
        private double minimumSymmetricHalfRange;
        internal string Title = "等待波形";
        internal string Unit = "V";
        internal Color TraceColor = Color.Teal;
        internal string EmptyText = "等待本轮完整数据";
        // Display-only floor, in the displayed Unit. Zero preserves ordinary auto-scaling.
        // A positive value keeps a symmetric axis without clipping genuine excursions.
        internal double MinimumSymmetricHalfRange
        {
            get { return minimumSymmetricHalfRange; }
            set
            {
                ValidateMinimumHalfRange(value);
                minimumSymmetricHalfRange = value;
                Invalidate();
            }
        }

        internal static void CalculateBounds(double dataMinimum, double dataMaximum, double minimumHalfRange,
            out double minimum, out double maximum)
        {
            ValidateMinimumHalfRange(minimumHalfRange);
            if (!IsFinite(dataMinimum) || !IsFinite(dataMaximum) || dataMinimum > dataMaximum)
                throw new ArgumentException("波形显示范围必须是有限且有序的数值。");
            double pad = Math.Max((dataMaximum - dataMinimum) * .12, Math.Max(Math.Abs(dataMaximum), .001) * .02);
            minimum = dataMinimum - pad;
            maximum = dataMaximum + pad;
            if (minimumHalfRange > 0)
            {
                double halfRange = Math.Max(minimumHalfRange, Math.Max(Math.Abs(minimum), Math.Abs(maximum)));
                minimum = -halfRange;
                maximum = halfRange;
            }
            if (!IsFinite(minimum) || !IsFinite(maximum) || !IsFinite(maximum - minimum) || maximum <= minimum)
                throw new ArgumentException("波形显示范围超出有限数值范围。");
        }

        private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static void ValidateMinimumHalfRange(double value)
        {
            if (!IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException("value", "最小对称半量程必须是非负有限数值。");
        }
        internal WavePanel()
        {
            DoubleBuffered = true; BackColor = Color.White; Dock = DockStyle.Fill;
            SetStyle(ControlStyles.ResizeRedraw, true);
            MouseWheel += (s, e) =>
            {
                double fraction = Math.Max(0, Math.Min(1, (e.X - 82.0) / Math.Max(1, Width - 105.0)));
                double next = Math.Max(spanMs / 10000, Math.Min(spanMs, widthMs * (e.Delta > 0 ? .65 : 1 / .65)));
                leftMs = Math.Max(0, Math.Min(spanMs - next, leftMs + fraction * (widthMs - next)));
                widthMs = next; Invalidate();
            };
            MouseDown += (s, e) => { Focus(); if (e.Button == MouseButtons.Right) ResetView(); else { dragging = true; dragStart = e.Location; dragLeft = leftMs; Capture = true; } };
            MouseUp += (s, e) => { dragging = false; Capture = false; };
            MouseMove += (s, e) => { if (dragging) { leftMs = Math.Max(0, Math.Min(spanMs - widthMs, dragLeft - (e.X - dragStart.X) * widthMs / Math.Max(1, Width - 105))); Invalidate(); } };
        }
        internal void Set(double[] data, double fs, double factor, double durationMs)
        { voltage = data; adc = null; rate = fs; scale = factor; spanMs = durationMs; ResetView(); }
        internal void Set(short[] data, double fs, double factor, double durationMs)
        { adc = data; voltage = null; rate = fs; scale = factor; spanMs = durationMs; ResetView(); }
        internal void ResetView() { leftMs = 0; widthMs = spanMs; Invalidate(); }
        internal void ClearWave() { voltage = null; adc = null; Invalidate(); }
        private double Value(int i) { return (voltage != null ? voltage[i] : adc[i]) * scale; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bold = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold))
                g.DrawString(Title, bold, Brushes.DarkSlateGray, 14, 8);
            RectangleF p = new RectangleF(82, 40, Math.Max(20, Width - 106), Math.Max(20, Height - 81));
            int n = voltage != null ? voltage.Length : adc != null ? adc.Length : 0;
            int start = n == 0 ? 0 : Math.Min(n - 1, Math.Max(0, (int)(leftMs * rate / 1000)));
            int end = n == 0 ? 0 : Math.Min(n, Math.Max(start + 1, (int)Math.Ceiling((leftMs + widthMs) * rate / 1000)));
            double minimum = Double.PositiveInfinity, maximum = Double.NegativeInfinity;
            // Min/max buckets preserve short overshoots that point-skipping would hide.
            int buckets = Math.Max(1, (int)p.Width);
            var lo = new double[buckets]; var hi = new double[buckets];
            if (n > 0)
            {
                for (int b = 0; b < buckets; b++)
                {
                    int i0 = start + (int)((long)(end - start) * b / buckets);
                    int i1 = Math.Min(end, Math.Max(i0 + 1, start + (int)((long)(end - start) * (b + 1) / buckets)));
                    double a = Double.PositiveInfinity, z = Double.NegativeInfinity;
                    for (int i = i0; i < i1; i++) { double v = Value(i); a = Math.Min(a, v); z = Math.Max(z, v); }
                    lo[b] = a; hi[b] = z; minimum = Math.Min(minimum, a); maximum = Math.Max(maximum, z);
                }
            }
            if (n == 0) { minimum = -1; maximum = 1; }
            try { CalculateBounds(minimum, maximum, minimumSymmetricHalfRange, out minimum, out maximum); }
            catch (ArgumentException)
            {
                g.DrawString("波形含无效数值，无法显示；原始数据未被修改。", Font, Brushes.Firebrick, p.Left + 15, p.Top + 20);
                return;
            }
            using (var grid = new Pen(Color.FromArgb(229, 237, 242)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = p.Top + p.Height * i / 4;
                    g.DrawLine(grid, p.Left, y, p.Right, y);
                    string text = (maximum - (maximum - minimum) * i / 4).ToString("G4");
                    SizeF size = g.MeasureString(text, Font); g.DrawString(text, Font, Brushes.SlateGray, p.Left - size.Width - 7, y - 8);
                }
                for (int i = 0; i <= 4; i++)
                {
                    float x = p.Left + p.Width * i / 4;
                    g.DrawLine(grid, x, p.Top, x, p.Bottom);
                    g.DrawString((leftMs + widthMs * i / 4).ToString("G5"), Font, Brushes.SlateGray, x - 12, p.Bottom + 5);
                }
            }
            g.DrawString(Unit, Font, Brushes.SlateGray, 12, 31);
            g.DrawString("时间 / ms  · 滚轮缩放 · 拖动平移 · 右键复位", Font, Brushes.SlateGray, p.Left, Height - 19);
            if (n == 0) { g.DrawString(EmptyText, Font, Brushes.SlateGray, p.Left + 15, p.Top + 20); return; }
            using (var pen = new Pen(TraceColor, 1))
            {
                float lastY = 0;
                for (int b = 0; b < buckets; b++)
                {
                    float x = p.Left + b;
                    float y0 = p.Bottom - (float)((lo[b] - minimum) / (maximum - minimum) * p.Height);
                    float y1 = p.Bottom - (float)((hi[b] - minimum) / (maximum - minimum) * p.Height);
                    g.DrawLine(pen, x, y0, x, y1);
                    float middle = (y0 + y1) / 2;
                    if (b > 0) g.DrawLine(pen, x - 1, lastY, x, middle);
                    lastY = middle;
                }
            }
        }
    }
}
