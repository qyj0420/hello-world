using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    // A waveform-only LOC view modeled on Task 3's overview. It does not
    // embed the Task 3 analysis workspace or alter analysis results.
    internal sealed class BrowserLocationPlot : Control
    {
        sealed class Bin
        {
            internal double First, Last, Low = double.PositiveInfinity, High = double.NegativeInfinity;
        }

        double[] samples = new double[0];
        double sampleRate = 1, baseline, threshold;
        double cableLengthMetres = double.NaN, velocityMPerUs = double.NaN;
        string path = "";
        bool selecting; int languageIndex;
        int start, end, t1 = -1, t2 = -1, moving = -1, dragStart, dragEnd;
        Bin[] bins = new Bin[0];
        int cacheStart = -1, cacheEnd = -1, cacheWidth = -1;
        double cacheLow, cacheHigh;
        double viewYLow = -1, viewYHigh = 1;
        Font bold;

        internal BrowserLocationPlot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = BrowserTheme.Surface;
            ForeColor = BrowserTheme.Ink;
            Font = new Font("Arial", 10f);
            MinimumSize = new Size(200, 150);
            TabStop = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && bold != null) bold.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            if (bold != null) { bold.Dispose(); bold = null; }
            base.OnFontChanged(e);
        }

        Font Bold { get { return bold ?? (bold = new Font(Font, FontStyle.Bold)); } }
        internal int LanguageIndex { get { return languageIndex; } set { BrowserLanguages.Validate(value); languageIndex = value; Invalidate(); } }
        internal bool EnglishVersion { get { return languageIndex == 1; } set { LanguageIndex = value ? 1 : 0; } }
        string L(string zh, string en) { return BrowserLanguages.Text(languageIndex, zh, en); }
        internal string DisplayedPath { get { return path; } }
        internal int SampleCount { get { return samples.Length; } }
        internal int ViewStartIndex { get { return start; } }
        internal int ViewEndIndex { get { return end; } }
        internal double PreviewThresholdPc { get { return threshold; } }
        internal int CacheBuildCount { get; private set; }
        internal double CableLengthMetres { get { return cableLengthMetres; } }
        internal double VelocityMPerUs { get { return velocityMPerUs; } }
        internal int Cursor1Index { get { return t1; } }
        internal int Cursor2Index { get { return t2; } }
        internal double CursorDeltaNs
        { get { return t1 >= 0 && t2 > t1 ? (t2 - (double)t1) / sampleRate * 1e9 : double.NaN; } }
        internal double ReflectionBoundaryIndex
        {
            get
            {
                double roundTrip = RoundTripSamples;
                return t1 >= 0 && Finite(roundTrip) ? t1 + roundTrip : double.NaN;
            }
        }
        internal double CursorPositionMetres
        {
            get
            {
                double boundary = ReflectionBoundaryIndex;
                if (!Finite(boundary) || t2 <= t1 || t2 > boundary + 1e-7) return double.NaN;
                double position = cableLengthMetres -
                    (t2 - (double)t1) / sampleRate * 1e6 * velocityMPerUs / 2;
                return position >= -1e-7 && position <= cableLengthMetres + 1e-7
                    ? Math.Max(0, Math.Min(cableLengthMetres, position)) : double.NaN;
            }
        }
        internal event EventHandler MeasurementChanged;

        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        bool ValidGeometry { get { return Finite(cableLengthMetres) && cableLengthMetres > 0 &&
            Finite(velocityMPerUs) && velocityMPerUs > 0 && velocityMPerUs <= 299.792458; } }
        double RoundTripSamples { get { return ValidGeometry ? 2 * cableLengthMetres / velocityMPerUs *
            1e-6 * sampleRate : double.NaN; } }
        void NotifyMeasurement()
        {
            Invalidate();
            EventHandler changed = MeasurementChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }
        static double Median(List<double> sorted)
        {
            int k = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[k - 1] + sorted[k]) / 2 : sorted[k];
        }

        internal void SetData(string filePath, double[] chargePc, double rateHz)
        { SetData(filePath, chargePc, rateHz, double.NaN, double.NaN); }

        internal void SetData(string filePath, double[] chargePc, double rateHz,
            double cableLengthM, double waveVelocityMPerUs)
        {
            if (chargePc == null || chargePc.Length < 16) throw new ArgumentException("LOC waveform needs at least 16 samples.", "chargePc");
            if (!Finite(rateHz) || rateHz <= 0) throw new ArgumentOutOfRangeException("rateHz");
            samples = chargePc;
            sampleRate = rateHz;
            cableLengthMetres = cableLengthM;
            velocityMPerUs = waveVelocityMPerUs;
            path = filePath ?? "";
            start = 0;
            end = samples.Length - 1;
            t1 = t2 = moving = -1;
            selecting = false;
            EstimateThreshold();
            ResetVerticalView();
            InvalidateBins();
            NotifyMeasurement();
        }

        void EstimateThreshold()
        {
            var probe = new List<double>();
            int stride = Math.Max(1, samples.Length / 4096);
            for (int i = 0; i < samples.Length; i += stride)
                if (Finite(samples[i])) probe.Add(samples[i]);
            if (probe.Count == 0) { baseline = threshold = 0; return; }
            probe.Sort();
            baseline = Median(probe);
            for (int i = 0; i < probe.Count; i++) probe[i] = Math.Abs(probe[i] - baseline);
            probe.Sort();
            double noise = 1.4826 * Median(probe), maximum = 0;
            for (int i = 0; i < samples.Length; i++)
                if (Finite(samples[i])) maximum = Math.Max(maximum, Math.Abs(samples[i] - baseline));
            double sigma = Math.Max(noise, Math.Max(1e-15, maximum * 1e-8));
            threshold = Math.Min(maximum, Math.Max(5, Math.Sqrt(2 * Math.Log(Math.Max(2, samples.Length)))) * sigma * 3);
        }

        void InvalidateBins()
        {
            cacheStart = cacheEnd = cacheWidth = -1;
            bins = new Bin[0];
        }

        void PrepareBins(int width)
        {
            if (cacheStart == start && cacheEnd == end && cacheWidth == width) return;
            int length = end - start + 1;
            int count = Math.Min(Math.Max(1, width), length);
            bins = new Bin[count];
            cacheLow = double.PositiveInfinity;
            cacheHigh = double.NegativeInfinity;
            for (int b = 0; b < count; b++)
            {
                int first = start + (int)((long)length * b / count);
                int last = start + (int)((long)length * (b + 1) / count) - 1;
                var bin = new Bin { First = samples[first], Last = samples[last] };
                for (int i = first; i <= last; i++)
                {
                    double value = samples[i];
                    if (!Finite(value)) continue;
                    bin.Low = Math.Min(bin.Low, value);
                    bin.High = Math.Max(bin.High, value);
                }
                if (Finite(bin.Low)) cacheLow = Math.Min(cacheLow, bin.Low);
                if (Finite(bin.High)) cacheHigh = Math.Max(cacheHigh, bin.High);
                bins[b] = bin;
            }
            cacheStart = start;
            cacheEnd = end;
            cacheWidth = width;
            CacheBuildCount++;
        }

        // Reserve separate rows below the plot for ticks, the X-axis title and
        // the interaction hint. At the browser's minimum window size the chart
        // shrinks vertically; labels keep their normal Arial font and spacing.
        Rectangle Plot { get { return new Rectangle(82, 54, Math.Max(1, Width - 108), Math.Max(1, Height - 161)); } }
        internal Rectangle TimeTickBounds { get { Rectangle p = Plot; return new Rectangle(p.Left, p.Bottom + 3, p.Width, 20); } }
        internal Rectangle TimeAxisCaptionBounds { get { Rectangle p = Plot; return new Rectangle(p.Left, p.Bottom + 27, p.Width, 20); } }
        internal Rectangle ResultCardBounds { get { Rectangle p = Plot; return new Rectangle(p.Left, p.Bottom + 50, p.Width, 55); } }
        internal Rectangle MeasurementBounds { get { Rectangle c = ResultCardBounds; return new Rectangle(c.Left + 8, c.Top + 4, Math.Max(1, c.Width - 16), 21); } }
        internal Rectangle InteractionCaptionBounds { get { Rectangle c = ResultCardBounds; return new Rectangle(c.Left + 8, c.Top + 29, Math.Max(1, c.Width - 16), 21); } }
        float Px(double index, Rectangle plot)
        { return plot.Left + (float)((index - start) / Math.Max(1, end - start) * plot.Width); }
        int IndexAt(int x, Rectangle plot)
        { return Math.Max(start, Math.Min(end, start + (int)Math.Round((x - plot.Left) / (double)plot.Width * (end - start)))); }
        int CursorAt(Point p, Rectangle plot)
        {
            if (t1 < 0 || t2 < 0 || !plot.Contains(p)) return -1;
            double d1 = Math.Abs(Px(t1, plot) - p.X), d2 = Math.Abs(Px(t2, plot) - p.X);
            return Math.Min(d1, d2) > 8 ? -1 : d1 <= d2 ? 0 : 1;
        }
        void EnsureCursors()
        {
            if (t1 < start || t1 > end || t2 < start || t2 > end)
            {
                t1 = start + (end - start) / 3;
                t2 = start + 2 * (end - start) / 3;
            }
            ConstrainInteractiveReflection();
        }
        void ConstrainInteractiveReflection()
        {
            double boundary = ReflectionBoundaryIndex;
            if (!Finite(boundary) || t1 < 0 || t2 < 0) return;
            int maximum = (int)Math.Min(samples.Length - 1, Math.Floor(boundary + 1e-7));
            if (maximum > t1) t2 = Math.Max(t1 + 1, Math.Min(maximum, t2));
        }
        internal void SetMeasurementCursors(int first, int second)
        {
            if (first < 0 || second < 0 || first >= samples.Length || second >= samples.Length)
                throw new ArgumentOutOfRangeException("first", "Cursor indices must identify recorded samples.");
            t1 = first;
            t2 = second;
            NotifyMeasurement();
        }
        internal RectangleF ReflectionRangeRectangle(Rectangle plot)
        {
            double boundary = ReflectionBoundaryIndex;
            if (!Finite(boundary)) return RectangleF.Empty;
            double first = Math.Max(t1, start), last = Math.Min(boundary, end);
            if (last <= first) return RectangleF.Empty;
            float left = Math.Max(plot.Left, Px(first, plot));
            float right = Math.Min(plot.Right, Px(last, plot));
            return right > left
                ? new RectangleF(left, plot.Top, right - left, plot.Height) : RectangleF.Empty;
        }
        internal void ResetView()
        {
            ResetVerticalView();
            start = 0;
            end = Math.Max(0, samples.Length - 1);
            t1 = t2 = moving = -1;
            selecting = false;
            Capture = false;
            InvalidateBins();
            NotifyMeasurement();
        }
        void ResetVerticalView()
        {
            double low=baseline-threshold, high=baseline+threshold;
            foreach(double value in samples) if(Finite(value)){low=Math.Min(low,value);high=Math.Max(high,value);}
            if(!Finite(low)||!Finite(high)){low=-1;high=1;}
            if(high<=low){low-=.5;high+=.5;}
            double pad=(high-low)*.08;viewYLow=low-pad;viewYHigh=high+pad;
        }
        internal void ZoomToSamples(int first, int last)
        {
            if (first < 0 || last < first || last >= samples.Length) throw new ArgumentOutOfRangeException("first");
            int minimum = Math.Min(16, samples.Length);
            if (last - first + 1 < minimum)
            {
                first = Math.Max(0, Math.Min(samples.Length - minimum, (first + last) / 2 - minimum / 2));
                last = first + minimum - 1;
            }
            start = first;
            end = last;
            EnsureCursors();
            InvalidateBins();
            NotifyMeasurement();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (Width < 200 || Height < 150) return;
            Rectangle plot = Plot;
            TextRenderer.DrawText(g, L("定位波形", "Location waveform"), Bold,
                new Rectangle(16, 8, plot.Width / 2, 22), BrowserTheme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (samples.Length == 0)
            {
                TextRenderer.DrawText(g, L("请选择定位数据", "Select a data file in the tree to view its waveform."), Font,
                    plot, BrowserTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            TextRenderer.DrawText(g, L("预览阈值 ", "Preview threshold ") +
                threshold.ToString("0.##", CultureInfo.InvariantCulture) + " pC", Font,
                new Rectangle(plot.Left, 29, plot.Width, 21), BrowserTheme.PhaseA,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            PrepareBins(plot.Width);
            double low = viewYLow, high = viewYHigh;
            double range = high - low;
            Func<double, float> py = v => (float)(plot.Bottom - (v - low) / range * plot.Height);
            RectangleF reflectionRange = ReflectionRangeRectangle(plot);
            if (!reflectionRange.IsEmpty)
                using (var shade = new SolidBrush(Color.FromArgb(35, BrowserTheme.Cyan)))
                    g.FillRectangle(shade, reflectionRange);
            double timeScale = (end - start) / sampleRate < .0001 ? 1e6 : 1e3;
            string timeUnit = timeScale == 1e6 ? "μs" : "ms";
            double chargeScale = Math.Max(Math.Abs(low), Math.Abs(high)) >= 1000 ? 1000 : 1;
            using (var grid = new Pen(BrowserTheme.Grid))
            using (var border = new Pen(BrowserTheme.Border))
            {
                for (int k = 0; k <= 5; k++)
                {
                    float x = plot.Left + plot.Width * k / 5f;
                    float y = plot.Top + plot.Height * k / 5f;
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    double time = (start + (end - start) * k / 5.0) / sampleRate * timeScale;
                    TextRenderer.DrawText(g, time.ToString("0.###", CultureInfo.InvariantCulture), Font,
                        new Rectangle((int)x - 42, TimeTickBounds.Top, 84, TimeTickBounds.Height), BrowserTheme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    double charge = (high - range * k / 5) / chargeScale;
                    TextRenderer.DrawText(g, charge.ToString("0.###", CultureInfo.InvariantCulture), Font,
                        new Rectangle(0, (int)y - 10, plot.Left - 7, 20), BrowserTheme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
                g.DrawRectangle(border, plot);
            }
            TextRenderer.DrawText(g, (L("放电量", "Discharge") + " / ") + (chargeScale == 1 ? "pC" : "nC"),
                Font, new Rectangle(plot.Left + plot.Width / 2, 8, plot.Width / 2, 21), BrowserTheme.Ink,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, (L("时间", "Time") + " / ") + timeUnit,
                Font, TimeAxisCaptionBounds, BrowserTheme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            GraphicsState clip = g.Save();
            g.SetClip(plot);
            using (var limit = new Pen(BrowserTheme.PhaseA, 1.2f) { DashStyle = DashStyle.Dash })
            using (var wave = new Pen(BrowserTheme.Pd, 1.35f))
            {
                g.DrawLine(limit, plot.Left, py(baseline + threshold), plot.Right, py(baseline + threshold));
                g.DrawLine(limit, plot.Left, py(baseline - threshold), plot.Right, py(baseline - threshold));
                if ((long)end - start + 1 <= 4L * plot.Width)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    PointF? previous = null;
                    for (int i = start; i <= end; i++)
                    {
                        if (!Finite(samples[i])) { previous = null; continue; }
                        var current = new PointF(Px(i, plot), py(samples[i]));
                        if (previous.HasValue) g.DrawLine(wave, previous.Value, current);
                        previous = current;
                    }
                }
                else
                {
                    PointF? previous = null;
                    for (int b = 0; b < bins.Length; b++)
                    {
                        Bin bin = bins[b];
                        if (!Finite(bin.Low) || !Finite(bin.High)) { previous = null; continue; }
                        float x = plot.Left + plot.Width * b / (float)Math.Max(1, bins.Length - 1);
                        if (previous.HasValue && Finite(bin.First))
                            g.DrawLine(wave, previous.Value, new PointF(x, py(bin.First)));
                        g.DrawLine(wave, x, py(bin.Low), x, py(bin.High));
                        previous = Finite(bin.Last) ? new PointF(x, py(bin.Last)) : (PointF?)null;
                    }
                }
            }
            if (selecting)
            {
                var rect = new Rectangle(Math.Min(dragStart, dragEnd), plot.Top,
                    Math.Abs(dragEnd - dragStart), plot.Height);
                using (var brush = new SolidBrush(Color.FromArgb(35, BrowserTheme.Cyan)))
                    g.FillRectangle(brush, rect);
            }
            if (t1 >= 0 && t2 >= 0)
            {
                double boundary = ReflectionBoundaryIndex;
                if (Finite(boundary))
                {
                    float actual = Px(boundary, plot);
                    float x = Math.Max(plot.Left, Math.Min(plot.Right, actual));
                    Color edge = BrowserTheme.Muted;
                    using (var marker = new Pen(edge, 1.2f) { DashStyle = DashStyle.DashDot })
                        g.DrawLine(marker, x, plot.Top + Font.Height + 29, x, plot.Bottom);
                    string label = "2L" +
                        (actual > plot.Right ? " →" : actual < plot.Left ? " ←" : "");
                    int labelWidth = Math.Min(plot.Width, TextRenderer.MeasureText(g, label, Font).Width + 8);
                    int labelLeft = (int)Math.Max(plot.Left, Math.Min(plot.Right - labelWidth, x - labelWidth / 2));
                    Rectangle labelBox = new Rectangle(labelLeft, plot.Top + Font.Height + 9, labelWidth, Font.Height + 6);
                    using (var background = new SolidBrush(BrowserTheme.SurfaceAlt))
                        g.FillRectangle(background, labelBox);
                    TextRenderer.DrawText(g, label, Font, labelBox, edge,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                int[] marks = { t1, t2 };
                Color[] colors = { BrowserTheme.Primary, BrowserTheme.PhaseA };
                for (int k = 0; k < 2; k++)
                {
                    float x = Px(marks[k], plot);
                    using (var marker = new Pen(colors[k], moving == k ? 2f : 1.3f) { DashStyle = DashStyle.Dash })
                        g.DrawLine(marker, x, plot.Top, x, plot.Bottom);
                    using (var brush = new SolidBrush(colors[k]))
                        g.FillPolygon(brush, new[] { new PointF(x - 5, plot.Top),
                            new PointF(x + 5, plot.Top), new PointF(x, plot.Top + 8) });
                    int labelWidth = Math.Max(32, Font.Height * 2);
                    int left = (int)Math.Max(plot.Left, Math.Min(plot.Right - labelWidth, x - labelWidth / 2));
                    Rectangle labelBox = new Rectangle(left, plot.Top + 9, labelWidth, Font.Height + 5);
                    using (var background = new SolidBrush(BrowserTheme.SurfaceAlt))
                        g.FillRectangle(background, labelBox);
                    TextRenderer.DrawText(g, "T" + (k + 1), Bold, labelBox, colors[k],
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            g.Restore(clip);
            Rectangle card = ResultCardBounds;
            const int radius = 10;
            using (var shape = new GraphicsPath())
            using (var fill = new SolidBrush(BrowserTheme.SurfaceAlt))
            using (var outline = new Pen(BrowserTheme.Border))
            {
                shape.AddArc(card.Left, card.Top, radius, radius, 180, 90);
                shape.AddArc(card.Right - radius, card.Top, radius, radius, 270, 90);
                shape.AddArc(card.Right - radius, card.Bottom - radius, radius, radius, 0, 90);
                shape.AddArc(card.Left, card.Bottom - radius, radius, radius, 90, 90);
                shape.CloseFigure();
                g.FillPath(fill, shape);
                g.DrawPath(outline, shape);
            }
            string timeText = L("滚轮缩放，右键恢复。", "Wheel: zoom. Right-click: reset.") + "  T1/T2";
            if (t1 >= 0 && t2 >= 0)
                timeText = "T1 " + (t1 / sampleRate * timeScale).ToString("0.###", CultureInfo.InvariantCulture) +
                    " " + timeUnit + "   T2 " +
                    (t2 / sampleRate * timeScale).ToString("0.###", CultureInfo.InvariantCulture) +
                    " " + timeUnit + "   |Δt| " +
                    CursorDeltaNs.ToString("0.0", CultureInfo.InvariantCulture) + " ns";
            TextRenderer.DrawText(g, timeText, Font, MeasurementBounds, BrowserTheme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string speed = ValidGeometry ? velocityMPerUs.ToString("0.##", CultureInfo.InvariantCulture) + " m/μs" : "—";
            string position = Finite(CursorPositionMetres)
                ? CursorPositionMetres.ToString("0.0", CultureInfo.InvariantCulture) + " m" : "—";
            string result = L("波速：", "Velocity: ") + speed + "     " +
                L("位置：", "Position: ") + position;
            TextRenderer.DrawText(g, result, Font, InteractionCaptionBounds, BrowserTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {base.OnMouseWheel(e);ApplyWheelZoom(e,(ModifierKeys&Keys.Shift)!=0);}
        internal void ApplyWheelZoom(MouseEventArgs e,bool vertical)
        {
            Rectangle plot = Plot;
            if (samples.Length < 16 || !plot.Contains(e.Location)) return;
            if(vertical)
            {
                double span=(viewYHigh-viewYLow)*(e.Delta>0?.7:1/.7), f=(plot.Bottom-e.Y)/(double)plot.Height;
                if(!Finite(span)||span<1e-15||span>1e100)return;
                double anchorY=viewYLow+(viewYHigh-viewYLow)*f;viewYLow=anchorY-span*f;viewYHigh=viewYLow+span;
                Invalidate();return;
            }
            int current = end - start + 1;
            int next = Math.Max(16, Math.Min(samples.Length,
                (int)Math.Round(current * (e.Delta > 0 ? .7 : 1 / .7))));
            double fraction = Math.Max(0, Math.Min(1, (e.X - plot.Left) / (double)plot.Width));
            int anchor = start + (int)Math.Round((current - 1) * fraction);
            int first = Math.Max(0, Math.Min(samples.Length - next,
                anchor - (int)Math.Round((next - 1) * fraction)));
            ZoomToSamples(first, first + next - 1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { ResetView(); return; }
            Rectangle plot = Plot;
            if (e.Button != MouseButtons.Left || samples.Length < 16 || !plot.Contains(e.Location)) return;
            Focus();
            moving = CursorAt(e.Location, plot);
            if (moving < 0) { selecting = true; dragStart = dragEnd = e.X; }
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle plot = Plot;
            if (Capture && moving >= 0)
            {
                if (moving == 0) t1 = IndexAt(e.X, plot); else t2 = IndexAt(e.X, plot);
                ConstrainInteractiveReflection();
                NotifyMeasurement();
            }
            else if (Capture && selecting)
            {
                dragEnd = Math.Max(plot.Left, Math.Min(plot.Right, e.X));
                Invalidate();
            }
            else Cursor = CursorAt(e.Location, plot) >= 0 ? Cursors.SizeWE : Cursors.Cross;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            bool zoom = selecting && Math.Abs(e.X - dragStart) >= 6;
            int first = IndexAt(dragStart, Plot), last = IndexAt(e.X, Plot);
            selecting = false;
            moving = -1;
            Capture = false;
            if (zoom) ZoomToSamples(Math.Min(first, last), Math.Max(first, last));
            else Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        { base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left) ResetView(); }
    }
}
