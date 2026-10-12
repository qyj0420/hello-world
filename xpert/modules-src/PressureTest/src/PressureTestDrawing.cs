using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal sealed partial class PressureWaveformView : Control
    {
        int waveformLanguageIndex;
        internal int LanguageIndex
        {
            get { return waveformLanguageIndex; }
            set { waveformLanguageIndex = value; UpdateLanguageDescription(); }
        }
        string L(string zh,string en){return Xiongxin.OWTS.Localization.UiLanguage.Text(LanguageIndex,zh,en);}
        void UpdateLanguageDescription()
        { AccessibleDescription = L("电压仅显示平滑曲线；原始数据、计算及保护不变。", "Voltage smoothing is display-only; original data, calculations and protection are unchanged."); }
        double[] voltage = new double[0];
        double[] displayVoltage = new double[0];
        double[] discharge = new double[0];
        double voltageRateHz = 1.0;
        double dischargeRateHz = 1.0;
        string displayWindowCode = "MAXIMUM";
        double displayFrequencyHz;
        double cachedFullDuration;
        bool cachedTimeKnown;
        double cachedVoltageAbs = 5.0;
        double cachedPdBaseline;
        PressurePdDisplayEnvelope cachedPdEnvelope;
        double cachedPdMaximum = 1.0;
        double cachedFirstCrossing=Double.NaN;
        double cachedPdWindowMaximum=Double.NaN;
        bool manualDischargeScale;
        double manualFullScalePc=Double.NaN;
        bool manualThresholdGuideEnabled;
        double manualThresholdGuidePc;

        internal void SetManualThresholdGuide(bool enabled,double thresholdPc)
        {
            if(!IsFinite(thresholdPc)||thresholdPc<0)throw new ArgumentOutOfRangeException("thresholdPc");
            if(!enabled)CancelManualThresholdDrag(false);
            if(manualThresholdGuideEnabled==enabled&&manualThresholdGuidePc.Equals(thresholdPc))return;
            manualThresholdGuideEnabled=enabled;manualThresholdGuidePc=thresholdPc;Invalidate();
        }
        internal bool ManualThresholdGuideEnabled {get{return manualThresholdGuideEnabled;}}
        internal double ManualThresholdGuidePc {get{return manualThresholdGuidePc;}}

        internal void SetDischargeScale(bool manual,double fullScalePc)
        {
            if(manualDischargeScale==manual&&manualFullScalePc.Equals(fullScalePc))return;
            manualDischargeScale=manual;manualFullScalePc=fullScalePc;Invalidate();
        }
        double DisplayPdMaximum { get {
            if(manualThresholdDragging)return manualThresholdDragMaximum;
            if(manualDischargeScale&&IsFinitePositive(manualFullScalePc))return manualFullScalePc;
            double maximum=DisplayStart>0&&IsFinite(cachedPdWindowMaximum)?Math.Max(1,cachedPdWindowMaximum):cachedPdMaximum;
            if(manualThresholdGuideEnabled)maximum=Math.Max(maximum,manualThresholdGuidePc);
            return AutomaticPdDisplayMaximum(maximum);
        } }
        double DisplayStart { get { return cachedTimeKnown&&IsFinitePositive(displayFrequencyHz)&&
            IsFinite(cachedFirstCrossing)&&
            cachedFirstCrossing>=0&&cachedFirstCrossing<cachedFullDuration?cachedFirstCrossing:0; } }

        internal PressureWaveformView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = PressureTestTheme.Surface;
            Font = PressureTestTheme.Font(9.5f);
            UpdateLanguageDescription();
        }

        internal void SetData(double[] voltageValues, double voltageSampleRateHz,
            double[] dischargeValues, double dischargeSampleRateHz)
        {
            CancelManualThresholdDrag(true);
            voltage = CopyFinite(voltageValues);
            displayVoltage = PrepareDisplayVoltage(voltage, CancellationToken.None);
            ResetVoltageDrawingCache();
            discharge = CopyFinite(dischargeValues);
            voltageRateHz = IsFinitePositive(voltageSampleRateHz) ? voltageSampleRateHz : 0;
            dischargeRateHz = IsFinitePositive(dischargeSampleRateHz) ? dischargeSampleRateHz : 0;
            RecalculateDisplayStatistics();
            Invalidate();
        }

        internal void ClearData()
        {
            CancelManualThresholdDrag(true);
            voltage = new double[0];
            displayVoltage = new double[0];
            ResetVoltageDrawingCache();
            discharge = new double[0];
            cachedFullDuration = 0.0;
            cachedTimeKnown = false;
            cachedVoltageAbs = 5.0;
            cachedPdBaseline = 0.0;
            cachedPdEnvelope = null;
            cachedPdMaximum = 1.0;
            cachedFirstCrossing=Double.NaN;
            cachedPdWindowMaximum=Double.NaN;
            Invalidate();
        }

        internal void SetDisplayWindow(string windowCode, double frequencyHz)
        {
            string nextCode = String.IsNullOrWhiteSpace(windowCode) ? "MAXIMUM" : windowCode;
            double nextFrequency = IsFinitePositive(frequencyHz) ? frequencyHz : 0;
            if(displayWindowCode==nextCode&&displayFrequencyHz.Equals(nextFrequency))return;
            displayWindowCode = nextCode;
            displayFrequencyHz = nextFrequency;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            double fullDuration = cachedFullDuration;
            if (fullDuration <= 0) fullDuration = cachedTimeKnown ? 1 / Math.Max(voltageRateHz, dischargeRateHz) : 1;
            double duration = ResolveDisplayDuration(fullDuration);
            double voltageAbs = cachedVoltageAbs;
            const double pdMin = 0.0;
            double pdBaseline = cachedPdBaseline;
            double pdMax = DisplayPdMaximum;

            using (Font axisFont = CreateAxisFont())
            {
                Rectangle plot = CreatePlotBounds(g, pdMax, voltageAbs, axisFont);
                Rectangle pdPlot = CreatePdTrack(plot);
                Rectangle voltagePlot = CreateVoltageTrack(plot);
                DrawGrid(g, plot);
                GraphicsState waveformState=g.Save();g.SetClip(plot);
                DrawPdPulsePlot(g, pdPlot, duration, pdMin, pdMax, pdBaseline);
                DrawVoltageLine(g, voltagePlot, duration, voltageAbs);
                DrawManualThresholdGuide(g,pdPlot,pdMin,pdMax,axisFont);
                g.Restore(waveformState);
                DrawAxes(g, plot, pdPlot, voltagePlot, duration, pdMin, pdMax, voltageAbs, axisFont);
                DrawLegend(g, plot, axisFont);
            }
        }

        double ResolveDisplayDuration(double fullDuration)
        {
            if(DisplayStart>0){
                fullDuration=Math.Floor(Math.Max(0,fullDuration-DisplayStart)*1000+1e-9)/1000;
                if(fullDuration<=0)return 1/Math.Max(voltageRateHz,dischargeRateHz);
            }
            if (!cachedTimeKnown || !IsFinitePositive(displayFrequencyHz) ||
                String.Equals(displayWindowCode, "MAXIMUM", StringComparison.OrdinalIgnoreCase))
                return fullDuration;
            double periods = String.Equals(displayWindowCode, "TWO", StringComparison.OrdinalIgnoreCase) ? 2.0 : 1.0;
            return Math.Min(fullDuration, periods / displayFrequencyHz);
        }

        void RecalculateDisplayStatistics()
        {
            cachedTimeKnown = HasTimeBase(voltage, voltageRateHz, discharge, dischargeRateHz);
            cachedFullDuration = DisplayExtent(voltage, voltageRateHz, discharge, dischargeRateHz, cachedTimeKnown);
            cachedVoltageAbs = VoltageDisplayMaximum(MaxAbs(voltage));
            cachedPdBaseline = Median(discharge);
            cachedPdEnvelope = new PressurePdDisplayEnvelope(discharge, cachedPdBaseline, CancellationToken.None);
            cachedPdMaximum = Math.Max(1.0, MaxAbsFromBaseline(discharge, cachedPdBaseline));
            cachedFirstCrossing=FindDisplayCrossing(voltage,voltageRateHz);
            cachedPdWindowMaximum=PressureMetricWindow.PeakAfterFirstCrossing(voltage,voltageRateHz,discharge,dischargeRateHz);
        }

        Rectangle CreatePdTrack(Rectangle plot)
        {
            return plot;
        }

        Rectangle CreateVoltageTrack(Rectangle plot)
        {
            return plot;
        }

        Font CreateAxisFont()
        {
            float widthSize = ClientSize.Width / 70f;
            float heightSize = ClientSize.Height / 28f;
            float responsiveSize = Math.Max(9.5f, Math.Min(10.5f, Math.Min(widthSize, heightSize)));
            return PressureTestTheme.TransientFont(responsiveSize);
        }

        Rectangle CreatePlotBounds(Graphics g, double pdMaximum, double voltageMaximum, Font axisFont)
        {
            string widestPdLabel = pdMaximum.ToString(pdMaximum < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
            string widestVoltageLabel = (-voltageMaximum).ToString("0.0", CultureInfo.InvariantCulture);
            int tickHeight = (int)Math.Ceiling(g.MeasureString("0", axisFont).Height);
            int titleWidth = (int)Math.Ceiling(axisFont.GetHeight(g)) + 14;
            int leftMargin = Math.Max(76, (int)Math.Ceiling(g.MeasureString(widestPdLabel, axisFont).Width) + titleWidth + 12);
            int rightMargin = Math.Max(82, (int)Math.Ceiling(g.MeasureString(widestVoltageLabel, axisFont).Width) + titleWidth + 12);
            int topMargin = Math.Max(24, tickHeight + 8);
            int bottomMargin = Math.Max(60, tickHeight * 2 + 14);

            int minimumPlotWidth = Math.Min(40, Math.Max(1, Width));
            int minimumPlotHeight = Math.Min(35, Math.Max(1, Height));
            FitMargins(ref leftMargin, ref rightMargin, Math.Max(0, Width - minimumPlotWidth));
            FitMargins(ref topMargin, ref bottomMargin, Math.Max(0, Height - minimumPlotHeight));

            return new Rectangle(leftMargin, topMargin,
                Math.Max(1, Width - leftMargin - rightMargin),
                Math.Max(1, Height - topMargin - bottomMargin));
        }

        static void FitMargins(ref int leading, ref int trailing, int available)
        {
            int total = leading + trailing;
            if (total <= available) return;
            if (available <= 0)
            {
                leading = 0;
                trailing = 0;
                return;
            }

            double leadingRatio = leading / (double)total;
            leading = (int)Math.Floor(available * leadingRatio);
            trailing = available - leading;
        }

        void DrawGrid(Graphics g, Rectangle plot)
        {
            using (SolidBrush brush = new SolidBrush(PressureTestTheme.Surface)) g.FillRectangle(brush, plot);
            using (Pen pen = new Pen(PressureTestTheme.Grid, 1f))
            {
                foreach (double tick in HorizontalTicks(cachedTimeKnown ? ResolveDisplayDuration(cachedFullDuration) * 1000 : cachedFullDuration))
                {
                    double extent = cachedTimeKnown ? ResolveDisplayDuration(cachedFullDuration) * 1000 : cachedFullDuration;
                    int x = plot.Left + (int)Math.Round(tick / Math.Max(1e-12, extent) * plot.Width);
                    g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                }
                for (int i = 0; i <= 6; i++)
                {
                    int y = plot.Top + i * plot.Height / 6;
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                }
            }
            using (Pen axis = new Pen(Color.FromArgb(139, 160, 180)))
            {
                g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
                g.DrawLine(axis, plot.Left, plot.Top, plot.Left, plot.Bottom);
                g.DrawLine(axis, plot.Right, plot.Top, plot.Right, plot.Bottom);
            }
        }

        void DrawPdPulsePlot(Graphics g, Rectangle plot, double duration, double minValue, double maxValue, double baseline)
        {
            if (discharge.Length == 0) return;
            int zeroY = MapY(0, minValue, maxValue, plot);
            double rate = cachedTimeKnown ? dischargeRateHz : 1;
            double start=DisplayStart;
            int firstIndex=Math.Min(discharge.Length-1,(int)Math.Ceiling(start*rate-1e-9));
            int lastIndex = VisibleLastIndex(start+duration, rate, discharge.Length);
            if(start>0)lastIndex=Math.Min(lastIndex,(int)Math.Ceiling((start+duration)*rate-1e-9)-1);
            int visibleCount = Math.Max(1, lastIndex-firstIndex + 1);
            int targetPoints = Math.Max(1, plot.Width / 3);
            int stride = Math.Max(1, (int)Math.Ceiling(visibleCount / (double)targetPoints));
            using (Pen stem = new Pen(Color.FromArgb(150, PressureTestTheme.Pd), 1f))
            {
                for (int first = firstIndex; first <= lastIndex; first += stride)
                {
                    int last = Math.Min(lastIndex, first + stride - 1);
                    int selectedIndex = cachedPdEnvelope.MaximumIndex(first, last);
                    double selected = Math.Abs(discharge[selectedIndex] - baseline);
                    double t = selectedIndex / rate-start;
                    int x = plot.Left + (int)Math.Round(t / Math.Max(1e-12, duration) * plot.Width);
                    // A manual scale stays fixed. An over-range sample is marked
                    // at the ceiling, never changes the retained sample/metric.
                    int y = MapY(Math.Min(selected,maxValue), minValue, maxValue, plot);
                    g.DrawLine(stem, x, zeroY, x, y);
                }
            }
        }

        void DrawVoltageLine(Graphics g, Rectangle plot, double duration, double absoluteMaximum)
        {
            if (voltage.Length < 2) return;
            PointF[] points = GetVoltageDrawingPoints(plot,duration,absoluteMaximum);
            if (points.Length < 2) return;
            using (Pen pen = new Pen(PressureTestTheme.Voltage, 1f)) g.DrawLines(pen, points);
        }

        PointF MapVoltagePoint(int index, Rectangle plot, double duration, double absoluteMaximum)
        {
            double t = index / (cachedTimeKnown ? voltageRateHz : 1)-DisplayStart;
            float x = plot.Left + (float)(t / Math.Max(1e-12, duration) * plot.Width);
            float y = plot.Top + (float)(VoltageDisplayYRatio(displayVoltage[index],absoluteMaximum) * plot.Height);
            return new PointF(x, y);
        }

        void DrawAxes(Graphics g, Rectangle plot, Rectangle pdPlot, Rectangle voltagePlot,
            double duration, double pdMin, double pdMax, double voltageAbs, Font axisFont)
        {
            using (SolidBrush ink = new SolidBrush(PressureTestTheme.Ink))
            using (SolidBrush muted = new SolidBrush(PressureTestTheme.SecondaryInk))
            {
                double extent = cachedTimeKnown ? duration * 1000 : duration;
                foreach (double value in HorizontalTicks(extent))
                {
                    string text = value.ToString("0", CultureInfo.InvariantCulture);
                    SizeF size = g.MeasureString(text, axisFont);
                    float x = plot.Left + (float)(value / Math.Max(1e-12, extent) * plot.Width) - size.Width / 2f;
                    g.DrawString(text, axisFont, muted, x, plot.Bottom + 4);
                }
                int verticalSteps = Math.Max(2, Math.Min(6, (int)(voltagePlot.Height / (axisFont.GetHeight(g) + 4))));
                verticalSteps -= verticalSteps % 2; // Always label the actual zero-voltage line.
                for (int i = 0; i <= verticalSteps; i++)
                {
                    double pdValue = pdMax - (pdMax - pdMin) * i / verticalSteps;
                    string left = (pdValue/(pdMax>=1000?1000:1)).ToString(pdMax>=1000?"0.##":pdMax < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
                    SizeF leftSize = g.MeasureString(left, axisFont);
                    g.DrawString(left, axisFont, muted, plot.Left - leftSize.Width - 5,
                        pdPlot.Top + i * pdPlot.Height / (float)verticalSteps - leftSize.Height / 2f);
                }
                foreach (double volts in VoltageDisplayLabels(voltageAbs,verticalSteps))
                {
                    string right = volts.ToString("0.0", CultureInfo.InvariantCulture);
                    float y=voltagePlot.Top+(float)(VoltageDisplayYRatio(volts,voltageAbs)*voltagePlot.Height);
                    SizeF rightSize=g.MeasureString(right,axisFont);
                    g.DrawString(right, axisFont, muted, plot.Right + 5,y-rightSize.Height/2f);
                }
                string xTitle = cachedTimeKnown ? L("时间 / ms","Time / ms") : L("采样点（时间基准未知）","Sample index (timebase unknown)");
                SizeF xt = g.MeasureString(xTitle, axisFont);
                g.DrawString(xTitle, axisFont, ink, plot.Left + (plot.Width - xt.Width) / 2f, Height - xt.Height - 1);
                DrawVerticalText(g, pdMax>=1000?L("局放量 / nC","Discharge / nC"):L("局放量 / pC","Discharge / pC"), axisFont, ink, 3, plot.Top + plot.Height / 2f);
                DrawVerticalText(g, L("电压幅值 / kV","Voltage / kV"), axisFont, ink,
                    Width - axisFont.Height - 2, plot.Top + plot.Height / 2f);
            }
        }

        internal static int VisibleLastIndex(double extent, double rate, int count)
        { return count <= 1 ? 0 : (int)Math.Max(0, Math.Min(count - 1, Math.Floor(extent * rate + 1e-9))); }
        internal static double[] HorizontalTicks(double extent)
        {
            if (!IsFinitePositive(extent)) return new[] { 0d };
            double step = Math.Max(1, Math.Ceiling(extent / 6));
            int count = (int)Math.Min(7, Math.Floor(extent / step) + 1);
            var ticks = new double[count]; for (int i = 0; i < count; i++) ticks[i] = i * step;
            return ticks;
        }

        void DrawLegend(Graphics g, Rectangle plot, Font axisFont)
        {
            string pdLabel=L("局放量","Discharge"),voltageLabel=L("电压（显示平滑）","Voltage (display smoothing)");
            int pdWidth=TextRenderer.MeasureText(pdLabel,axisFont,Size.Empty,TextFormatFlags.NoPadding).Width;
            int voltageWidth=TextRenderer.MeasureText(voltageLabel,axisFont,Size.Empty,TextFormatFlags.NoPadding).Width;
            int x = Math.Max(plot.Left + 8, plot.Right - pdWidth-voltageWidth-62);
            int y = 1;
            using (Pen pdPen = new Pen(PressureTestTheme.Pd, 3f)) g.DrawLine(pdPen, x, y + 8, x + 18, y + 8);
            TextRenderer.DrawText(g, pdLabel, axisFont, new Rectangle(x + 22, y, pdWidth+2, Math.Max(20,axisFont.Height+3)), PressureTestTheme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += pdWidth+38;
            using (Pen voltagePen = new Pen(PressureTestTheme.Voltage, 3f)) g.DrawLine(voltagePen, x, y + 8, x + 18, y + 8);
            TextRenderer.DrawText(g, voltageLabel, axisFont, new Rectangle(x + 22, y, voltageWidth+2, Math.Max(20,axisFont.Height+3)), PressureTestTheme.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void DrawManualThresholdGuide(Graphics g,Rectangle plot,double minimum,double maximum,Font font)
        {
            if(!manualThresholdGuideEnabled)return;
            bool inRange=manualThresholdGuidePc>=minimum&&manualThresholdGuidePc<=maximum;
            int y=inRange?MapY(manualThresholdGuidePc,minimum,maximum,plot):plot.Top;
            // Use the pC axis and unrounded value, never the voltage axis. A
            // fixed manual display range is not expanded or clamped silently.
            if(inRange)using(var pen=new Pen(PressureTestTheme.Warning,1.5f){DashStyle=DashStyle.Dash})
                g.DrawLine(pen,plot.Left,y,plot.Right,y);
            string text=String.Format(CultureInfo.InvariantCulture,L("阈值 {0:0.##} pC","Threshold {0:0.##} pC"),manualThresholdGuidePc);
            if(!inRange)text+=L("（超出显示范围）"," (outside display range)");
            SizeF size=g.MeasureString(text,font);
            float top=Math.Max(plot.Top,Math.Min(plot.Bottom-size.Height,y-size.Height-2));
            using(var background=new SolidBrush(Color.FromArgb(225,PressureTestTheme.Surface)))
            using(var ink=new SolidBrush(PressureTestTheme.Ink))
            {
                g.FillRectangle(background,plot.Left+5,top,size.Width+4,size.Height);
                g.DrawString(text,font,ink,plot.Left+7,top);
            }
        }

        static void DrawVerticalText(Graphics g, string text, Font font, Brush brush, float x, float centerY)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, centerY);
            g.RotateTransform(-90);
            SizeF size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, -size.Width / 2f, 0);
            g.Restore(state);
        }

        static int MapY(double value, double minValue, double maxValue, Rectangle plot)
        {
            double ratio = (value - minValue) / Math.Max(1e-12, maxValue - minValue);
            return plot.Bottom - (int)Math.Round(ratio * plot.Height);
        }

        static double Duration(double[] values, double rate)
        {
            return values.Length < 2 || !IsFinitePositive(rate) ? 0 : (values.Length - 1) / rate;
        }

        static double[] CopyFinite(double[] source)
        {
            if (source == null || source.Length == 0) return new double[0];
            double[] copy = new double[source.Length];
            for (int i = 0; i < source.Length; i++) copy[i] = IsFinite(source[i]) ? source[i] : 0;
            return copy;
        }

        static double MaxAbs(double[] values)
        {
            double max = 0;
            for (int i = 0; i < values.Length; i++) max = Math.Max(max, Math.Abs(values[i]));
            return max;
        }

        static double MaxAbsFromBaseline(double[] values, double baseline)
        {
            double max = 0;
            for (int i = 0; i < values.Length; i++) max = Math.Max(max, Math.Abs(values[i] - baseline));
            return max;
        }

        static double Median(double[] values)
        {
            return PressureDisplayMedian.Calculate(values, System.Threading.CancellationToken.None);
        }

        static double Min(double[] values)
        {
            if (values.Length == 0) return 0;
            double value = values[0];
            for (int i = 1; i < values.Length; i++) value = Math.Min(value, values[i]);
            return value;
        }

        static double Max(double[] values)
        {
            if (values.Length == 0) return 0;
            double value = values[0];
            for (int i = 1; i < values.Length; i++) value = Math.Max(value, values[i]);
            return value;
        }

        static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        static bool IsFinitePositive(double value) { return IsFinite(value) && value > 0; }
        static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
    }

    internal sealed class PressureLocationView : Control
    {
        double[] positions = new double[0];
        double[] charges = new double[0];
        double cableLength = 175.0;

        internal PressureLocationView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = PressureTestTheme.Surface;
            Font = PressureTestTheme.Font(9.5f);
        }

        internal void SetData(double[] positionMetres, double[] chargePc, double cableLengthMetres)
        {
            int count = Math.Min(positionMetres == null ? 0 : positionMetres.Length, chargePc == null ? 0 : chargePc.Length);
            positions = new double[count];
            charges = new double[count];
            int accepted = 0;
            for (int i = 0; i < count; i++)
            {
                if (!IsFinite(positionMetres[i]) || !IsFinite(chargePc[i])) continue;
                positions[accepted] = positionMetres[i];
                charges[accepted] = chargePc[i];
                accepted++;
            }
            if (accepted != count)
            {
                Array.Resize(ref positions, accepted);
                Array.Resize(ref charges, accepted);
            }
            cableLength = IsFinite(cableLengthMetres) && cableLengthMetres > 0 ? cableLengthMetres : 175.0;
            Invalidate();
        }

        internal void ClearData()
        {
            positions = new double[0];
            charges = new double[0];
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle plot = new Rectangle(54, 12, Math.Max(40, Width - 72), Math.Max(35, Height - 52));
            double maximum = 500;
            for (int i = 0; i < charges.Length; i++) maximum = Math.Max(maximum, Math.Abs(charges[i]));
            maximum = NiceMaximum(maximum);

            using (SolidBrush background = new SolidBrush(PressureTestTheme.SurfaceAlt)) g.FillRectangle(background, plot);
            using (Pen gridPen = new Pen(PressureTestTheme.Grid))
            {
                for (int i = 0; i <= 7; i++)
                {
                    int x = plot.Left + i * plot.Width / 7;
                    g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                }
                for (int i = 0; i <= 5; i++)
                {
                    int y = plot.Top + i * plot.Height / 5;
                    g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                }
            }
            using (Pen axis = new Pen(Color.FromArgb(139, 160, 180)))
            {
                g.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
                g.DrawLine(axis, plot.Left, plot.Top, plot.Left, plot.Bottom);
            }
            using (SolidBrush point = new SolidBrush(PressureTestTheme.Location))
            {
                for (int i = 0; i < positions.Length; i++)
                {
                    double xValue = Math.Max(0, Math.Min(cableLength, positions[i]));
                    double yValue = Math.Max(0, Math.Min(maximum, Math.Abs(charges[i])));
                    float x = plot.Left + (float)(xValue / cableLength * plot.Width);
                    float y = plot.Bottom - (float)(yValue / maximum * plot.Height);
                    g.FillEllipse(point, x - 4.5f, y - 4.5f, 9f, 9f);
                }
            }
            using (SolidBrush ink = new SolidBrush(PressureTestTheme.Ink))
            using (SolidBrush muted = new SolidBrush(PressureTestTheme.SecondaryInk))
            {
                for (int i = 0; i <= 7; i++)
                {
                    string text = (cableLength * i / 7.0).ToString("0", CultureInfo.InvariantCulture);
                    SizeF size = g.MeasureString(text, Font);
                    g.DrawString(text, Font, muted, plot.Left + i * plot.Width / 7f - size.Width / 2f, plot.Bottom + 3);
                }
                for (int i = 0; i <= 5; i++)
                {
                    string text = (maximum * (5 - i) / 5.0).ToString("0", CultureInfo.InvariantCulture);
                    SizeF size = g.MeasureString(text, Font);
                    g.DrawString(text, Font, muted, plot.Left - size.Width - 5, plot.Top + i * plot.Height / 5f - size.Height / 2f);
                }
                string xTitle = "位置 / m";
                SizeF titleSize = g.MeasureString(xTitle, Font);
                g.DrawString(xTitle, Font, ink, plot.Left + (plot.Width - titleSize.Width) / 2f, Height - titleSize.Height - 1);
                GraphicsState state = g.Save();
                g.TranslateTransform(3, plot.Top + plot.Height / 2f);
                g.RotateTransform(-90);
                SizeF ySize = g.MeasureString("放电量 / pC", Font);
                g.DrawString("放电量 / pC", Font, ink, -ySize.Width / 2f, 0);
                g.Restore(state);
            }
        }

        static double NiceMaximum(double value)
        {
            if (value <= 0) return 500;
            double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            return Math.Ceiling(value / power) * power;
        }

        static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }

}
