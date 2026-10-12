using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.OWTS.Task38.Desktop
{
    internal sealed class SynchronizedWaveChart : Control
    {
        private readonly List<PointF> voltage = new List<PointF>();
        private readonly List<PointF> discharge = new List<PointF>();
        private static readonly Color VoltageColor = Color.FromArgb(0, 137, 125);
        private static readonly Color DischargeColor = Color.FromArgb(151, 69, 184);
        private double durationMs = 60;
        private bool background;
        internal double FirstZeroMilliseconds { get; private set; }
        internal int VoltageCount { get { return voltage.Count; } }
        internal int DischargeCount { get { return discharge.Count; } }
        internal double FirstDischargeMilliseconds { get { return discharge.Count == 0 ? Double.NaN : discharge[0].X; } }
        internal double TimeMaximumMilliseconds { get { return durationMs; } }
        internal double VoltageAxisExtent { get { return Extent(voltage); } }
        internal double DischargeAxisExtent { get { return Extent(discharge); } }
        internal SynchronizedWaveChart()
        { Dock = DockStyle.Fill; BackColor = Color.White; Margin = new Padding(0, 0, 0, 10); DoubleBuffered = true; Clear(); }
        internal void Clear()
        { voltage.Clear(); discharge.Clear(); background = false; FirstZeroMilliseconds = Double.NaN; durationMs = 60; Invalidate(); }
        internal void SetVoltage(double[] values, double sampleRate, double scaleKv, double captureSeconds)
        {
            Clear();
            if (sampleRate <= 0 || !Finite(sampleRate) || !Finite(scaleKv)) return;
            durationMs = Math.Max(0.001, captureSeconds * 1000);
            for (int i = 0; i < values.Length; i++)
                if (Finite(values[i] * scaleKv)) voltage.Add(new PointF((float)(i * 1000.0 / sampleRate), (float)(values[i] * scaleKv)));
            if (voltage.Count > 0) durationMs = Math.Max(durationMs, voltage[voltage.Count - 1].X);
            Invalidate();
        }
        internal void SetDischarge(short[] values, double sampleRate, double scalePc, double firstZeroSeconds, double offsetSeconds, bool isBackground = false)
        {
            background = isBackground;
            discharge.Clear(); FirstZeroMilliseconds = Double.NaN;
            if (!Finite(firstZeroSeconds) || firstZeroSeconds < 0 || !Finite(sampleRate) || sampleRate <= 0 || !Finite(scalePc) || !Finite(offsetSeconds)) { Invalidate(); return; }
            FirstZeroMilliseconds = background ? Double.NaN : firstZeroSeconds * 1000;
            int first = background ? 0 : (int)Math.Min(values.Length, Math.Max(0, Math.Ceiling((firstZeroSeconds - offsetSeconds) * sampleRate)));
            int bucket = Math.Max(1, (values.Length - first + 4095) / 4096);
            // Keep both extrema of each bucket, in time order, without allocating millions of UI points.
            for (int i = first; i < values.Length; i += bucket)
            {
                int lo = i, hi = i, end = Math.Min(values.Length, i + bucket);
                for (int j = i + 1; j < end; j++) { if (values[j] < values[lo]) lo = j; if (values[j] > values[hi]) hi = j; }
                AddDischarge(values, Math.Min(lo, hi), sampleRate, scalePc, offsetSeconds);
                if (lo != hi) AddDischarge(values, Math.Max(lo, hi), sampleRate, scalePc, offsetSeconds);
            }
            if (values.Length > 0) durationMs = Math.Max(durationMs, ((values.Length - 1) / sampleRate + offsetSeconds) * 1000);
            Invalidate();
        }
        private void AddDischarge(short[] values, int index, double rate, double scale, double offset)
        { discharge.Add(new PointF((float)((index / rate + offset) * 1000), (float)(values[index] * scale))); }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        private static float Extent(List<PointF> points)
        { float peak = 0; foreach (PointF p in points) peak = Math.Max(peak, Math.Abs(p.Y)); return peak > 0 ? peak * 1.12f : 1; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Font title = new Font(Font.FontFamily, 12, FontStyle.Bold))
                g.DrawString(background ? "零电压背景同步波形" : "振荡电压与局放同步波形", title, Brushes.Black, 18, 12);
            RectangleF plot = new RectangleF(78, 78, Math.Max(20, Width - 166), Math.Max(20, Height - 126));
            float qExtent = Extent(discharge), uExtent = Extent(voltage);
            using (Font tick = new Font(Font.FontFamily, 8))
            using (Brush qBrush = new SolidBrush(DischargeColor))
            using (Brush uBrush = new SolidBrush(VoltageColor))
            using (Pen grid = new Pen(Color.FromArgb(229, 234, 240)))
            using (StringFormat right = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.DrawString(background ? "背景等效量 / pC（左轴，全段）" : "局放 / pC（左轴，首零相位后）", tick, qBrush, 18, 39);
                g.DrawString("电压 / kV（右轴）", tick, uBrush, Width - 18, 39, right);
                for (int i = 0; i <= 6; i++)
                {
                    float x = plot.Left + plot.Width * i / 6, y = plot.Top + plot.Height * i / 6;
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom); g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    g.DrawString((durationMs * i / 6).ToString("0.###"), tick, Brushes.Gray, x - 10, plot.Bottom + 6);
                    g.DrawString((qExtent * (1 - i / 3f)).ToString("0.###"), tick, qBrush, plot.Left - 8, y - 7, right);
                    g.DrawString((uExtent * (1 - i / 3f)).ToString("0.###"), tick, uBrush, plot.Right + 8, y - 7);
                }
                g.DrawString("触发后时间 / ms", tick, Brushes.Gray, plot.Right - 115, Height - 19);
                GraphicsState clip = g.Save();
                g.SetClip(plot);
                DrawWave(g, voltage, plot, uExtent, VoltageColor);
                DrawWave(g, discharge, plot, qExtent, DischargeColor);
                if (Finite(FirstZeroMilliseconds) && FirstZeroMilliseconds <= durationMs)
                {
                    float x = plot.Left + (float)(FirstZeroMilliseconds / durationMs) * plot.Width;
                    using (Pen marker = new Pen(Color.DarkOrange, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLine(marker, x, plot.Top, x, plot.Bottom);
                    g.DrawString("首零相位", tick, Brushes.DarkOrange, Math.Min(x + 5, plot.Right - 65), plot.Top + 4);
                }
                g.Restore(clip);
                if (voltage.Count == 0 && discharge.Count == 0) g.DrawString("等待本轮同步数据", Font, Brushes.SlateGray, plot.Left + 20, plot.Top + 25);
                else if (discharge.Count == 0) g.DrawString("局放：等待有效首零相位分析", tick, qBrush, plot.Left, 59);
            }
        }
        private void DrawWave(Graphics g, List<PointF> data, RectangleF plot, float extent, Color color)
        {
            using (Pen pen = new Pen(color, 1.25f))
            {
                PointF? previous = null;
                int bucket = Math.Max(1, data.Count / Math.Max(1, (int)plot.Width));
                for (int i = 0; i < data.Count; i += bucket)
                {
                    int lo = i, hi = i, end = Math.Min(data.Count, i + bucket);
                    for (int j = i + 1; j < end; j++) { if (data[j].Y < data[lo].Y) lo = j; if (data[j].Y > data[hi].Y) hi = j; }
                    int a = Math.Min(lo, hi), b = Math.Max(lo, hi);
                    PointF p = Map(data[a], plot, extent), q = Map(data[b], plot, extent);
                    if (previous.HasValue) g.DrawLine(pen, previous.Value, p);
                    g.DrawLine(pen, p, q); previous = q;
                }
            }
        }
        private PointF Map(PointF p, RectangleF plot, float extent)
        { return new PointF(plot.Left + (float)(p.X / durationMs) * plot.Width, plot.Top + plot.Height * (1 - p.Y / extent) / 2); }
    }
}
