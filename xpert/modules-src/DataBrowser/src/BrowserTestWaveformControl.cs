using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace HVX.Controls
{
    // Display-only projection. Keep the source sample arrays and their effective
    // common lengths; a DEM with millions of samples must not be copied again.
    internal sealed class BrowserTestWaveformData
    {
        internal double[] Pd = new double[0], Voltage = new double[0];
        internal int PdCount, VoltageCount;
        internal double PdStep = 1, VoltageStep = 1, EndX;
        internal BrowserSampleIndex PdIndex, VoltageIndex;
        internal bool Timed, Estimated;
        internal double VoltageReferenceKv = double.NaN;
        internal double MaximumPositivePd, MaximumAbsoluteVoltage;
        private static readonly ConditionalWeakTable<BrowserWaveform, BrowserTestWaveformData> prepared =
            new ConditionalWeakTable<BrowserWaveform, BrowserTestWaveformData>();

        internal BrowserTestWaveformData()
        {
            PdIndex = new BrowserSampleIndex(Pd, 0);
            VoltageIndex = new BrowserSampleIndex(Voltage, 0);
        }

        internal static BrowserTestWaveformData From(BrowserWaveform wave)
        { return Prepare(wave, CancellationToken.None); }

        // Called by the record loader before posting its result to the UI. Publish
        // only complete indexes; language refreshes use the same immutable source.
        internal static BrowserTestWaveformData Prepare(BrowserWaveform wave, CancellationToken cancellation)
        {
            if (wave == null) throw new ArgumentNullException("wave");
            cancellation.ThrowIfCancellationRequested();
            BrowserTestWaveformData cached;
            if (prepared.TryGetValue(wave, out cached)) return cached;
            lock (prepared)
            {
                cancellation.ThrowIfCancellationRequested();
                BrowserTestWaveformData result;
                if (prepared.TryGetValue(wave, out result)) return result;
                result = Build(wave, cancellation);
                cancellation.ThrowIfCancellationRequested();
                prepared.Add(wave, result);
                return result;
            }
        }

        private static BrowserTestWaveformData Build(BrowserWaveform wave, CancellationToken cancellation)
        {
            var result = new BrowserTestWaveformData();
            result.Pd = wave.Y ?? new double[0];
            result.Voltage = wave.VoltageKv ?? new double[0];
            bool pdTimed = BrowserPlot.Finite(wave.SampleRateHz) && wave.SampleRateHz > 0;
            bool voltageTimed = BrowserPlot.Finite(wave.VoltageSampleRateHz) && wave.VoltageSampleRateHz > 0;
            result.Timed = pdTimed && voltageTimed;
            result.Estimated = wave.SamplingRateEstimated;
            result.VoltageReferenceKv = wave.VoltageReferenceKv;
            if (result.Timed)
            {
                result.PdStep = 1000.0 / wave.SampleRateHz;
                result.VoltageStep = 1000.0 / wave.VoltageSampleRateHz;
                double pdEnd = result.Pd.Length > 1 ? (result.Pd.Length - 1.0) * result.PdStep : 0;
                double voltageEnd = result.Voltage.Length > 1 ? (result.Voltage.Length - 1.0) * result.VoltageStep : 0;
                result.EndX = result.Pd.Length == 0 ? voltageEnd
                    : result.Voltage.Length == 0 ? pdEnd : Math.Min(pdEnd, voltageEnd);
                result.PdCount = CountWithin(result.Pd.Length, result.PdStep, result.EndX);
                result.VoltageCount = CountWithin(result.Voltage.Length, result.VoltageStep, result.EndX);
            }
            else
            {
                // Unknown intervals stay in sample-index coordinates, never measured ms.
                int count = result.Pd.Length == 0 ? result.Voltage.Length
                    : result.Voltage.Length == 0 ? result.Pd.Length
                    : Math.Min(result.Pd.Length, result.Voltage.Length);
                result.EndX = Math.Max(0, count - 1);
                result.PdCount = Math.Min(result.Pd.Length, count);
                result.VoltageCount = Math.Min(result.Voltage.Length, count);
            }
            result.PdIndex = new BrowserSampleIndex(result.Pd, result.PdCount, cancellation);
            result.VoltageIndex = new BrowserSampleIndex(result.Voltage, result.VoltageCount, cancellation);
            int lo, hi; bool missing;
            result.PdIndex.Query(0, result.PdCount, out lo, out hi, out missing);
            result.MaximumPositivePd = hi < 0 ? 0 : Math.Max(0, result.Pd[hi]);
            result.VoltageIndex.Query(0, result.VoltageCount, out lo, out hi, out missing);
            if (lo >= 0) result.MaximumAbsoluteVoltage = Math.Max(result.MaximumAbsoluteVoltage, Math.Abs(result.Voltage[lo]));
            if (hi >= 0) result.MaximumAbsoluteVoltage = Math.Max(result.MaximumAbsoluteVoltage, Math.Abs(result.Voltage[hi]));
            return result;
        }

        private static int CountWithin(int length, double step, double endX)
        {
            if (length == 0) return 0;
            double allowed = Math.Floor(endX / step + 1e-8) + 1;
            return allowed >= length ? length : Math.Max(0, (int)allowed);
        }

        internal static int LowerBound(double x, double step, int count)
        {
            int low = 0, high = count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (mid * step < x) low = mid + 1; else high = mid;
            }
            return low;
        }

        private static int UpperBound(double x, double step, int count)
        {
            int low = 0, high = count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (mid * step <= x) low = mid + 1; else high = mid;
            }
            return low;
        }

        internal static void VisibleRange(double xmin, double xmax, double step, int count,
            out int first, out int end)
        {
            if (count <= 0 || step <= 0 || xmax < xmin)
            { first = end = 0; return; }
            double tolerance = step * 1e-8;
            first = LowerBound(xmin - tolerance, step, count);
            end = UpperBound(xmax + tolerance, step, count);
        }
    }

    // Exact finite min/max queries over source samples. A 256-sample block
    // summary costs only O(n/256) extra memory and avoids rescanning millions
    // of samples for each pixel column or a zoomed view.
    internal sealed class BrowserSampleIndex
    {
        private const int BlockSize = 256;
        private readonly double[] samples;
        private readonly int count;
        private readonly int[] blockMin, blockMax, blockMissing;

        internal BrowserSampleIndex(double[] samples, int count)
            : this(samples, count, CancellationToken.None) { }

        internal BrowserSampleIndex(double[] samples, int count, CancellationToken cancellation)
        {
            this.samples = samples;
            this.count = Math.Max(0, Math.Min(samples.Length, count));
            int blocks = (this.count + BlockSize - 1) / BlockSize;
            blockMin = new int[blocks];
            blockMax = new int[blocks];
            blockMissing = new int[blocks];
            for (int b = 0; b < blocks; b++) { blockMin[b] = -1; blockMax[b] = -1; }
            for (int i = 0; i < this.count; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int b = i / BlockSize;
                if (!BrowserPlot.Finite(samples[i])) { blockMissing[b]++; continue; }
                if (blockMin[b] < 0 || samples[i] < samples[blockMin[b]]) blockMin[b] = i;
                if (blockMax[b] < 0 || samples[i] > samples[blockMax[b]]) blockMax[b] = i;
            }
        }

        private void AcceptSample(int i, ref int minimum, ref int maximum, ref bool missing)
        {
            if (!BrowserPlot.Finite(samples[i])) { missing = true; return; }
            if (minimum < 0 || samples[i] < samples[minimum]) minimum = i;
            if (maximum < 0 || samples[i] > samples[maximum]) maximum = i;
        }

        internal void Query(int first, int end, out int minimum, out int maximum, out bool missing)
        {
            minimum = maximum = -1;
            missing = false;
            first = Math.Max(0, Math.Min(count, first));
            end = Math.Max(first, Math.Min(count, end));
            int i = first;
            while (i < end && i % BlockSize != 0)
            { AcceptSample(i, ref minimum, ref maximum, ref missing); i++; }
            while (i + BlockSize <= end)
            {
                int b = i / BlockSize;
                missing |= blockMissing[b] != 0;
                int lo = blockMin[b], hi = blockMax[b];
                if (lo >= 0 && (minimum < 0 || samples[lo] < samples[minimum])) minimum = lo;
                if (hi >= 0 && (maximum < 0 || samples[hi] > samples[maximum])) maximum = hi;
                i += BlockSize;
            }
            while (i < end)
            { AcceptSample(i, ref minimum, ref maximum, ref missing); i++; }
        }
    }

    /// <summary>One overlaid plot for DEM charge and voltage, with independent charge and voltage rulers.</summary>
    internal sealed class BrowserTestWaveformControl : Control
    {
        BrowserTestWaveformData data = new BrowserTestWaveformData();
        int languageIndex; bool dragging;
        int dragX;
        double dragStartMin, dragStartMax;
        double viewMin, viewMax = 1;
        double verticalMin, verticalMax = 1;
        Bitmap staticLayer;
        float staticDpiX, staticDpiY;
        int staticRenderCount;

        internal int StaticRenderCount { get { return staticRenderCount; } }

        void ReleaseStaticLayer()
        { if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; } }
        void InvalidateScene() { ReleaseStaticLayer(); Invalidate(); }

        protected override void OnResize(EventArgs e) { ReleaseStaticLayer(); base.OnResize(e); }
        protected override void OnFontChanged(EventArgs e) { ReleaseStaticLayer(); base.OnFontChanged(e); }
        protected override void OnForeColorChanged(EventArgs e) { ReleaseStaticLayer(); base.OnForeColorChanged(e); }
        protected override void OnBackColorChanged(EventArgs e) { ReleaseStaticLayer(); base.OnBackColorChanged(e); }
        protected override void Dispose(bool disposing) { if (disposing) ReleaseStaticLayer(); base.Dispose(disposing); }

        internal BrowserTestWaveformControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = BrowserTheme.Surface;
            ForeColor = BrowserTheme.Ink;
            Font = new Font("Arial", 10f);
            TabStop = true;
        }

        internal BrowserTestWaveformData DisplayData { get { return data; } }
        internal Rectangle PlotBounds
        {
            get
            {
                // The legend and the two axis titles use separate rows. A fixed
                // 58 px top margin made them touch at large DPI/font settings.
                int top = Math.Max(76, Font.Height * 2 + 38);
                return new Rectangle(86, top, Math.Max(30, Width - 156), Math.Max(30, Height - top - 60));
            }
        }
        internal void SetWaveform(BrowserWaveform waveform, int languageIndex)
        {
            var next = BrowserTestWaveformData.From(waveform);
            if (ReferenceEquals(data, next))
            {
                BrowserLanguages.Validate(languageIndex);
                if (this.languageIndex != languageIndex) { this.languageIndex = languageIndex; InvalidateScene(); }
                return;
            }
            data = next;
            BrowserLanguages.Validate(languageIndex); this.languageIndex = languageIndex;
            viewMin = 0;
            verticalMin = 0; verticalMax = 1;
            viewMax = data.EndX > 0 ? data.EndX : (data.Timed ? .001 : 1);
            InvalidateScene();
        }

        internal static int MillisecondTickStep(double span)
        {
            if (!BrowserPlot.Finite(span) || span <= 0) return 1;
            double raw = Math.Max(1, span / 6.0);
            double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double unit = raw / power;
            double rounded = unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10;
            return (int)Math.Min(int.MaxValue, Math.Max(1, rounded * power));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {base.OnMouseWheel(e);ApplyWheelZoom(e,(ModifierKeys&Keys.Shift)!=0);}
        internal void ApplyWheelZoom(MouseEventArgs e,bool vertical)
        {
            var plot = PlotBounds;
            if (!plot.Contains(e.Location) || data.EndX <= 0) return;
            if(vertical)
            {
                double nextY=(verticalMax-verticalMin)*(e.Delta>0?.8:1.25), f=(e.Y-plot.Top)/(double)plot.Height;
                if(nextY<1e-9||nextY>1e9)return;
                double anchor=verticalMin+(verticalMax-verticalMin)*f;
                verticalMin=anchor-nextY*f;verticalMax=verticalMin+nextY;InvalidateScene();return;
            }
            double total = data.EndX, span = viewMax - viewMin;
            double next = Math.Min(total, Math.Max(total / 1000.0, span * (e.Delta > 0 ? .8 : 1.25)));
            double fraction = Math.Max(0, Math.Min(1, (e.X - plot.Left) / (double)plot.Width));
            double pivot = viewMin + span * fraction;
            viewMin = Math.Max(0, Math.Min(total - next, pivot - next * fraction));
            viewMax = viewMin + next;
            InvalidateScene();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right) { verticalMin=0;verticalMax=1;viewMin = 0; viewMax = data.EndX > 0 ? data.EndX : (data.Timed ? .001 : 1); InvalidateScene(); }
            else if (e.Button == MouseButtons.Left && PlotBounds.Contains(e.Location))
            { dragging = true; dragX = e.X; dragStartMin = viewMin; dragStartMax = viewMax; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            double span = dragStartMax - dragStartMin;
            double delta = (dragX - e.X) * span / PlotBounds.Width;
            double next = Math.Max(0, Math.Min(Math.Max(0, data.EndX - span), dragStartMin + delta));
            if (next == viewMin) return;
            viewMin = next; viewMax = viewMin + span;
            InvalidateScene();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        { base.OnMouseUp(e); dragging = false; Capture = false; }

        private static double NiceCeiling(double value)
        {
            if (!BrowserPlot.Finite(value) || value <= 0) return 1;
            double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double unit = value / power;
            return (unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10) * power;
        }

        private void ChargeRange(out double minimum, out double maximum)
        {
            double max = data.MaximumPositivePd;
            minimum = 0;
            maximum = max > 0 ? NiceCeiling(max * 1.04) : 0;
            if (minimum == 0 && maximum == 0) maximum = 1;
            double fullMaximum=maximum;
            minimum=fullMaximum*(1-verticalMax);maximum=fullMaximum*(1-verticalMin);
        }

        internal double DisplayVoltageScaleKv
        {
            get
            {
                // The DEM header gives the selected physical voltage range. A nearly
                // zero trace must remain near zero instead of filling the chart.
                return Math.Max(1, Math.Max(BrowserPlot.Finite(data.VoltageReferenceKv) ? data.VoltageReferenceKv : 0,
                    data.MaximumAbsoluteVoltage / 1.2));
            }
        }

        internal static double VoltageYFraction(double voltageKv, double scaleKv)
        { return .35 - .25 * voltageKv / scaleKv; }
        double VisibleVoltageYFraction(double voltageKv,double scaleKv)
        { return (VoltageYFraction(voltageKv,scaleKv)-verticalMin)/(verticalMax-verticalMin); }
        private static void PixelRange(int column, int width, double xmin, double span,
            double step, int count, int visibleFirst, int visibleEnd, out int first, out int end)
        {
            double left = xmin + (column - .5) * span / width;
            double right = xmin + (column + .5) * span / width;
            first = Math.Max(visibleFirst, BrowserTestWaveformData.LowerBound(left, step, count));
            end = column == width - 1 ? visibleEnd
                : Math.Min(visibleEnd, BrowserTestWaveformData.LowerBound(right, step, count));
            if (end < first) end = first;
        }

        private static void DrawSamples(Graphics graphics, Pen pen, Rectangle plot,
            double[] y, int count, double step, BrowserSampleIndex index,
            double xmin, double xmax, Func<double, float> projectY)
        {
            if (xmax <= xmin || count == 0) return;
            int visibleFirst, visibleEnd;
            BrowserTestWaveformData.VisibleRange(xmin, xmax, step, count,
                out visibleFirst, out visibleEnd);
            var points = new List<PointF>(Math.Min(plot.Width * 4, 16384));
            Action flush = () => {
                if (points.Count > 1) graphics.DrawLines(pen, points.ToArray());
                points.Clear();
            };
            Action<int> add = sample => points.Add(new PointF(
                (float)(plot.Left + (sample * step - xmin) / (xmax - xmin) * plot.Width),
                projectY(y[sample])));
            double span = xmax - xmin;
            for (int column = 0; column < plot.Width; column++)
            {
                int first, end, lo, hi; bool missing;
                PixelRange(column, plot.Width, xmin, span, step, count,
                    visibleFirst, visibleEnd, out first, out end);
                if (first >= end) continue;
                index.Query(first, end, out lo, out hi, out missing);
                if (lo < 0) { flush(); continue; }
                if (missing)
                {
                    // Preserve actual gaps rather than connecting voltage across NaNs.
                    for (int i = first; i < end; i++)
                    { if (BrowserPlot.Finite(y[i])) add(i); else flush(); }
                    continue;
                }
                int[] candidates = { first, lo, hi, end - 1 };
                Array.Sort(candidates);
                int prior = -1;
                foreach (int sample in candidates)
                { if (sample != prior) add(sample); prior = sample; }
            }
            flush();
        }

        // Display only positive charge stems. Raw signed samples are retained
        // for analysis/export; the bipolar voltage trace is unaffected.
        // The block index queries this union without rescanning the entire DEM
        // on zoom/pan or passing millions of lines to GDI.
        private static void DrawChargeStems(Graphics graphics, Pen pen, Rectangle plot,
            BrowserTestWaveformData data, double xmin, double xmax, double ymin, double ymax)
        {
            if (xmax <= xmin || ymax <= ymin || plot.Width <= 0 || data.PdCount == 0) return;
            int visibleFirst, visibleEnd;
            BrowserTestWaveformData.VisibleRange(xmin, xmax, data.PdStep, data.PdCount,
                out visibleFirst, out visibleEnd);
            SmoothingMode previous = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.None;
            try
            {
                using (var stems = new GraphicsPath())
                {
                    int pending = 0;
                    double span = xmax - xmin;
                    for (int column = 0; column < plot.Width; column++)
                    {
                        int first, end, lo, hi; bool missing;
                        PixelRange(column, plot.Width, xmin, span, data.PdStep, data.PdCount,
                            visibleFirst, visibleEnd, out first, out end);
                        if (first >= end) continue;
                        data.PdIndex.Query(first, end, out lo, out hi, out missing);
                        if (lo < 0) continue;
                        double positive = Math.Max(0, data.Pd[hi]);
                        double negative = 0;
                        float upper = (float)(plot.Bottom -
                            (positive - ymin) / (ymax - ymin) * plot.Height);
                        float lower = (float)(plot.Bottom -
                            (negative - ymin) / (ymax - ymin) * plot.Height);
                        float px = plot.Left + column;
                        stems.StartFigure();
                        stems.AddLine(px, upper, px, lower);
                        if (++pending < 8192) continue;
                        graphics.DrawPath(pen, stems);
                        stems.Reset();
                        pending = 0;
                    }
                    if (pending > 0) graphics.DrawPath(pen, stems);
                }
            }
            finally { graphics.SmoothingMode = previous; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 250 || Height < 190) return;
            if (staticLayer == null || staticLayer.Size != ClientSize ||
                staticDpiX != e.Graphics.DpiX || staticDpiY != e.Graphics.DpiY)
            {
                ReleaseStaticLayer();
                var layer = new Bitmap(ClientSize.Width, ClientSize.Height);
                try
                {
                    layer.SetResolution(e.Graphics.DpiX, e.Graphics.DpiY);
                    using (var graphics = Graphics.FromImage(layer))
                    { graphics.Clear(BackColor); RenderScene(graphics); }
                    staticLayer = layer; staticDpiX = e.Graphics.DpiX; staticDpiY = e.Graphics.DpiY;
                    staticRenderCount++;
                }
                catch { layer.Dispose(); throw; }
            }
            e.Graphics.DrawImageUnscaled(staticLayer, Point.Empty);
        }

        private void RenderScene(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle p = PlotBounds;
            double chargeMin, chargeMax;
            ChargeRange(out chargeMin, out chargeMax);
            double voltageScaleKv = DisplayVoltageScaleKv;
            using (var ink = new SolidBrush(ForeColor))
            using (var grid = new Pen(BrowserTheme.Grid))
            using (var border = new Pen(BrowserTheme.Border))
            using (var pdPen = new Pen(BrowserTheme.Pd, 1f))
            using (var voltagePen = new Pen(BrowserTheme.Voltage, 1.45f))
            {
                string chargeLegend = BrowserLanguages.Text(languageIndex, "放电量", "Discharge");
                int voltageLegendX = p.Left + 30 + (int)Math.Ceiling(g.MeasureString(chargeLegend, Font).Width) + 20;
                g.DrawLine(pdPen, p.Left, 26, p.Left + 22, 26);
                g.DrawString(chargeLegend, Font, ink, p.Left + 30, 17);
                g.DrawLine(voltagePen, voltageLegendX, 26, voltageLegendX + 22, 26);
                g.DrawString(BrowserLanguages.Text(languageIndex, "振荡电压", "Oscillating Voltage"), Font, ink, voltageLegendX + 30, 17);
                for (int i = 0; i <= 5; i++)
                {
                    float y = p.Bottom - i * p.Height / 5f;
                    g.DrawLine(grid, p.Left, y, p.Right, y);
                    string label = (chargeMin + (chargeMax - chargeMin) * i / 5.0).ToString("0.###", CultureInfo.InvariantCulture);
                    SizeF labelSize = g.MeasureString(label, Font);
                    g.DrawString(label, Font, ink, p.Left - labelSize.Width - 9, y - labelSize.Height / 2);
                }
                int step = data.Timed ? MillisecondTickStep(viewMax - viewMin) : Math.Max(1, MillisecondTickStep(viewMax - viewMin));
                double firstTick = Math.Ceiling(viewMin / step) * step;
                for (double tick = firstTick; tick <= viewMax + 1e-8; tick += step)
                {
                    float x = (float)(p.Left + (tick - viewMin) / (viewMax - viewMin) * p.Width);
                    g.DrawLine(grid, x, p.Top, x, p.Bottom);
                    string label = tick.ToString("0", CultureInfo.InvariantCulture);
                    SizeF size = g.MeasureString(label, Font);
                    g.DrawString(label, Font, ink, x - size.Width / 2, p.Bottom + 7);
                }
                g.DrawRectangle(border, p);
                // The voltage ruler uses the same projection as the overlaid trace.
                // Label its physical ±scale and zero positions, rather than adding
                // a second pane or reusing the charge axis.
                foreach (double voltage in new double[] { voltageScaleKv, voltageScaleKv / 2, 0, -voltageScaleKv / 2, -voltageScaleKv })
                {
                    float y = (float)(p.Top + p.Height * VisibleVoltageYFraction(voltage, voltageScaleKv));
                    if (y < p.Top || y > p.Bottom) continue;
                    g.DrawLine(border, p.Right, y, p.Right + 6, y);
                    string label = voltage.ToString("0.#", CultureInfo.InvariantCulture);
                    SizeF size = g.MeasureString(label, Font);
                    g.DrawString(label, Font, ink, p.Right + 10, y - size.Height / 2);
                }
                string voltageTitle = BrowserLanguages.Text(languageIndex, "电压 / kV", "Voltage / kV");
                SizeF voltageTitleSize = g.MeasureString(voltageTitle, Font);
                g.DrawString(voltageTitle, Font, ink, p.Right - voltageTitleSize.Width, p.Top - 22);
                g.DrawString(BrowserLanguages.Text(languageIndex, "放电量 / pC", "Discharge / pC"), Font, ink, p.Left, p.Top - 22);
                string xTitle = !data.Timed ? (BrowserLanguages.Text(languageIndex, "采样点", "Sample index"))
                    : data.Estimated ? (BrowserLanguages.Text(languageIndex, "估算时间 / ms", "Estimated time / ms"))
                    : (BrowserLanguages.Text(languageIndex, "时间 / ms", "Time / ms"));
                SizeF titleSize = g.MeasureString(xTitle, Font);
                g.DrawString(xTitle, Font, ink, p.Left + (p.Width - titleSize.Width) / 2, p.Bottom + 38);
                GraphicsState state = g.Save(); g.SetClip(p);
                DrawSamples(g, voltagePen, p, data.Voltage, data.VoltageCount,
                    data.VoltageStep, data.VoltageIndex, viewMin, viewMax,
                    v => (float)(p.Top + p.Height * VisibleVoltageYFraction(v, voltageScaleKv)));
                DrawChargeStems(g, pdPen, p, data, viewMin, viewMax, chargeMin, chargeMax);
                g.Restore(state);
            }
        }
    }
}
