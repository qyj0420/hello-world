using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace LabVIEWOscillationPd
{
    /// <summary>
    /// 任务11 RenderSynchronizedPd 的可交互双轴版本。
    /// HV 使用右Y轴，PD 使用左Y轴；时间内部统一使用秒。
    /// </summary>
    [ToolboxItem(true)]
    public sealed partial class OscillationPdControl : UserControl
    {
        private readonly object dataLock = new object();
        private double[] hv = new double[] { 0, 0 };
        private double[] pd = new double[] { 0, 0 };
        private double samplingRateHv = 1;
        private double samplingRatePd = 1;
        private double minimumCharge;

        private double durationSeconds = 1;
        private double viewXMin;
        private double viewXMax = 1;
        private double pdYMin;
        private double pdYMax = 1;
        private double hvYMin = -1;
        private double hvYMax = 1;

        private bool cursor1Active;
        private bool cursor2Active;
        private bool probeActive;
        private bool pdLevelActive;
        private double cursor1X = 0.25;
        private double cursor2X = 0.65;
        private double probeX = 0.45;
        private double pdLevel;
        private DragTarget dragTarget;

        private double estimatedPeriodSeconds;
        private double phaseOriginSeconds;
        private Measurement cursor1Measurement;
        private Measurement cursor2Measurement;
        private double attenuationPercentPerCycle = double.NaN;
        private bool englishVersion;
        private string customXAxisTitle, customLeftYAxisTitle, customRightYAxisTitle, customHvName, customPdName, customChartTitle;

        public OscillationPdControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            BackColor = Color.White;
            ForeColor = Color.FromArgb(35, 42, 52);
            Font = new Font("Arial", 9F, FontStyle.Regular, GraphicsUnit.Point);
            MinimumSize = new Size(360, 240);
            Size = new Size(1120, 620);
            TabStop = true;
        }

        [Category("Language"), DefaultValue(false)]
        [Description("False = 中文; True = English. Keeps waveforms, cursors, measurements, view and custom labels.")]
        public bool EnglishVersion { get => englishVersion; set => SetLanguage(value); }

        public void SetEnglishDisplay(bool englishVersion) { SetLanguage(englishVersion); }

        [Category("任务11配置")]
        public double TextScale { get; set; } = 1.0;
        [Category("Appearance"), DefaultValue(true)] public bool ShowChartTitle { get; set; }
        [Category("Appearance"), DefaultValue(false)] public bool SeparateWaveBands { get; set; } = true;
        public double FrontBlankingEndSeconds { get; set; }

        [Category("外观")]
        public Color HvColor { get; set; } = Color.FromArgb(174, 191, 204);

        [Category("外观")]
        public Color PdColor { get; set; } = Color.FromArgb(18, 103, 78);

        [Category("外观")]
        public Color Cursor1Color { get; set; } = Color.FromArgb(0, 145, 198);

        [Category("外观")]
        public Color Cursor2Color { get; set; } = Color.FromArgb(238, 112, 0);

        [Category("外观")]
        public Color ProbeColor { get; set; } = Color.FromArgb(215, 48, 39);

        [Category("外观")]
        public Color PdCursorColor { get; set; } = Color.FromArgb(138, 65, 176);

        [Category("外观")]
        [Description("振荡电压游标和探针显示单位，例如 kV、V。")]
        public string VoltageUnit { get; set; } = "kV";

        [Category("游标")]
        public bool VoltageCursor1Active
        {
            get => cursor1Active;
            set { cursor1Active = value; EnsureCursorsVisible(); UpdateMeasurements(); SafeInvalidate(); }
        }

        [Category("游标")]
        public bool VoltageCursor2Active
        {
            get => cursor2Active;
            set { cursor2Active = value; EnsureCursorsVisible(); UpdateMeasurements(); SafeInvalidate(); }
        }

        [Category("游标")]
        public bool VoltageProbeActive
        {
            get => probeActive;
            set { probeActive = value; EnsureCursorsVisible(); SafeInvalidate(); }
        }

        [Category("游标")]
        public bool PdLevelCursorActive
        {
            get => pdLevelActive;
            set { pdLevelActive = value; EnsureCursorsVisible(); SafeInvalidate(); }
        }

        [Browsable(false)] public double VoltageCursor1XSeconds => cursor1X;
        [Browsable(false)] public double VoltageCursor2XSeconds => cursor2X;
        [Browsable(false)] public double VoltageCursor1Peak => cursor1Measurement.Amplitude;
        [Browsable(false)] public double VoltageCursor2Peak => cursor2Measurement.Amplitude;
        [Browsable(false)] public double AttenuationPercentPerCycle => attenuationPercentPerCycle;
        [Browsable(false)] public double VoltageProbeXSeconds => probeX;
        [Browsable(false)] public double VoltageProbeVoltage => InterpolateHv(probeX);
        [Browsable(false)] public double VoltageProbePhaseDegrees => CalculatePhaseDegrees(probeX);
        [Browsable(false)] public double PdLevelCursorValue => pdLevel;
        [Browsable(false)] public double EstimatedPeriodSeconds => estimatedPeriodSeconds;
        [Browsable(false)] public string LastOutputPath { get; private set; } = string.Empty;

        public string TimeUnit { get; private set; } = "ms";
        public string XAxisTitle { get => DefaultIfBlank(customXAxisTitle, (EnglishVersion ? "Time / " : "时间 / ") + TimeUnit); private set => customXAxisTitle = value; }
        public string LeftYAxisTitle { get => DefaultIfBlank(customLeftYAxisTitle, EnglishVersion ? "Partial Discharge / pC" : "局部放电 / pC"); private set => customLeftYAxisTitle = value; }
        public string RightYAxisTitle { get => DefaultIfBlank(customRightYAxisTitle, (EnglishVersion ? "Voltage / " : "电压 / ") + DefaultIfBlank(VoltageUnit, "kV")); private set => customRightYAxisTitle = value; }
        public string HvName { get => DefaultIfBlank(customHvName, EnglishVersion ? "Oscillating Voltage" : "振荡电压"); private set => customHvName = value; }
        public string PdName { get => DefaultIfBlank(customPdName, EnglishVersion ? "Partial Discharge" : "局部放电"); private set => customPdName = value; }
        public string ChartTitle { get => DefaultIfBlank(customChartTitle, EnglishVersion ? "Synchronized Voltage and PD" : "同步振荡电压与局部放电"); private set => customChartTitle = value; }
        public double TickFontSizePx { get; private set; } = 24;

        public event EventHandler MeasurementChanged;

        /// <summary>LabVIEW布尔输入端：False=中文，True=English。</summary>
        public void SetLanguage(bool englishVersion)
        {
            this.englishVersion = englishVersion;
            if (exportButton != null) exportButton.UpdateLanguage();
            SafeInvalidate();
        }

        /// <summary>LabVIEW字符串输入端：en/en-US为英文，其余为中文。</summary>
        public void SetLanguageCode(string languageCode)
        {
            SetLanguage(!string.IsNullOrWhiteSpace(languageCode) &&
                        languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 与任务11 RenderSynchronizedPd 完全相同的15个输入。更新控件、输出2400×1250 PNG并返回路径。
        /// </summary>
        public string RenderSynchronizedPd(
            double[] HV,
            double samplingRateHV,
            double[] PD,
            double samplingRatePD,
            double minimumChargePc,
            double tickFontSizePx,
            string timeUnit,
            string xName,
            string leftYName,
            string rightYName,
            string HVname,
            string PDname,
            string chartName,
            string outputDirectory,
            string outputName)
        {
            UpdateSynchronizedPd(HV, samplingRateHV, PD, samplingRatePD, minimumChargePc,
                tickFontSizePx, timeUnit, xName, leftYName, rightYName, HVname, PDname, chartName);

            string directory = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(Path.GetTempPath(), "OWTS.ReportCharts")
                : outputDirectory;
            Directory.CreateDirectory(directory);
            string fileName = SanitizeFileName(string.IsNullOrWhiteSpace(outputName)
                ? "synchronized_pd.png"
                : outputName);
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";
            string path = Path.Combine(directory, fileName);
            SavePng(path, 2400, 1250);
            LastOutputPath = path;
            return path;
        }

        /// <summary>带中英文布尔输入端的16输入入口；False=中文，True=English。</summary>
        public string RenderSynchronizedPdWithLanguage(
            double[] HV,
            double samplingRateHV,
            double[] PD,
            double samplingRatePD,
            double minimumChargePc,
            double tickFontSizePx,
            string timeUnit,
            string xName,
            string leftYName,
            string rightYName,
            string HVname,
            string PDname,
            string chartName,
            string outputDirectory,
            string outputName,
            bool englishVersion)
        {
            EnglishVersion = englishVersion;
            return RenderSynchronizedPd(HV, samplingRateHV, PD, samplingRatePD, minimumChargePc,
                tickFontSizePx, timeUnit, xName, leftYName, rightYName, HVname, PDname, chartName,
                outputDirectory, outputName);
        }

        /// <summary>更新双轴图但不写文件；输入为任务11的前13项。</summary>
        public void UpdateSynchronizedPd(
            double[] HV,
            double samplingRateHV,
            double[] PD,
            double samplingRatePD,
            double minimumChargePc,
            double tickFontSizePx,
            string timeUnit,
            string xName,
            string leftYName,
            string rightYName,
            string HVname,
            string PDname,
            string chartName)
        {
            HV = HV == null || HV.Length < 2 ? new double[2] : HV;
            PD = PD == null || PD.Length < 2 ? new double[2] : PD;
            if (!IsFinite(samplingRateHV) || samplingRateHV <= 0)
                samplingRateHV = IsFinite(samplingRatePD) && samplingRatePD > 0 ? samplingRatePD : 1.0;
            if (!IsFinite(samplingRatePD) || samplingRatePD <= 0) samplingRatePD = samplingRateHV;
            if (!IsFinite(minimumChargePc)) minimumChargePc = 0;

            double[] hvCopy = (double[])HV.Clone();
            double[] pdCopy = new double[PD.Length];
            for (int i = 0; i < PD.Length; i++)
                pdCopy[i] = IsFinite(PD[i]) && PD[i] >= minimumChargePc ? PD[i] : 0;

            lock (dataLock)
            {
                hv = hvCopy;
                pd = pdCopy;
                samplingRateHv = samplingRateHV;
                samplingRatePd = samplingRatePD;
                minimumCharge = minimumChargePc;
            }

            durationSeconds = Math.Max((HV.Length - 1) / samplingRateHV, (PD.Length - 1) / samplingRatePD);
            if (!IsFinite(durationSeconds) || durationSeconds <= 0) durationSeconds = 1;
            TickFontSizePx = tickFontSizePx <= 0 ? 24 : Clamp(tickFontSizePx, 12, 36);
            TimeUnit = ResolveTimeUnit(timeUnit, durationSeconds);
            XAxisTitle = xName;
            LeftYAxisTitle = leftYName;
            RightYAxisTitle = rightYName;
            HvName = HVname;
            PdName = PDname;
            ChartTitle = chartName;

            CalculateAutomaticRanges();
            EstimateOscillationPeriod();
            viewXMin = 0;
            viewXMax = durationSeconds;
            InitializeOrClampCursors();
            UpdateMeasurements();
            SafeInvalidate();
        }

        /// <summary>两个波形使用相同采样率的简化接口。</summary>
        public void SetWaveforms(double[] HV, double[] PD, double samplingRateHz)
        {
            UpdateSynchronizedPd(HV, samplingRateHz, PD, samplingRateHz, 0, 24, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        }

        /// <summary>四个LabVIEW布尔控制端：两个竖直游标、圆形探针、PD横向游标。</summary>
        public void SetCursorActivation(bool voltageCursor1, bool voltageCursor2, bool voltageProbe, bool pdLevelCursor)
        {
            cursor1Active = voltageCursor1;
            cursor2Active = voltageCursor2;
            probeActive = voltageProbe;
            pdLevelActive = pdLevelCursor;
            EnsureCursorsVisible();
            UpdateMeasurements();
            SafeInvalidate();
        }

        public void SetCursorPositions(double cursor1Seconds, double cursor2Seconds, double probeSeconds, double pdChargePc)
        {
            cursor1X = Clamp(cursor1Seconds, viewXMin, viewXMax);
            cursor2X = Clamp(cursor2Seconds, viewXMin, viewXMax);
            probeX = Clamp(probeSeconds, viewXMin, viewXMax);
            pdLevel = Clamp(pdChargePc, pdYMin, pdYMax);
            UpdateMeasurements();
            SafeInvalidate();
        }

        public void ResetView()
        {
            viewXMin = 0;
            viewXMax = durationSeconds;
            CalculateAutomaticRanges();
            EnsureCursorsVisible();
            UpdateMeasurements();
            SafeInvalidate();
        }

        public void SavePng(string filePath, int width, int height)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = Path.Combine(Path.GetTempPath(), "OscillationPd.png");
            width = Math.Max(360, width); height = Math.Max(240, height);
            string directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var bitmap = new Bitmap(width, height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                DrawChart(graphics, new Rectangle(0, 0, width, height));
                bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawChart(e.Graphics, ClientRectangle);
        }

        private void DrawChart(Graphics g, Rectangle bounds)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            Rectangle plot = GetPlotRectangle(bounds);
            if (plot.Width < 50 || plot.Height < 50) return;

            using (Font tickFont = CreateScaledFont(bounds, TickFontSizePx, FontStyle.Regular))
            using (Font axisFont = CreateScaledFont(bounds, TickFontSizePx * 1.15, FontStyle.Regular))
            using (Font titleFont = CreateScaledFont(bounds, TickFontSizePx * 1.25, FontStyle.Bold))
            using (var textBrush = new SolidBrush(ForeColor))
            {
                DrawGridAxesTicks(g, bounds, plot, tickFont, axisFont, textBrush);
                if(FrontBlankingEndSeconds>viewXMin)
                {
                    float end=XToPixel(Math.Min(viewXMax,FrontBlankingEndSeconds),plot);
                    using(var blanking=new SolidBrush(Color.FromArgb(32,110,120,130)))g.FillRectangle(blanking,plot.Left,plot.Top,Math.Max(0,end-plot.Left),plot.Height);
                    using(var line=new Pen(Color.Gray){DashStyle=DashStyle.Dash})g.DrawLine(line,end,plot.Top,end,plot.Bottom);
                    g.DrawString(EnglishVersion?"T/4 blanking":"T/4 前沿屏蔽",tickFont,Brushes.Gray,Math.Min(plot.Right-110,end+4),plot.Top+4);
                }
                DrawPdWaveform(g, plot);
                DrawHvWaveform(g, plot);
                DrawLegendAndTitle(g, bounds, plot, tickFont, titleFont, textBrush);
                DrawMeasurementCursors(g, bounds, plot, tickFont, textBrush);
            }
        }

        private Rectangle GetPlotRectangle(Rectangle bounds)
        {
            double s = Clamp(TextScale, 0.6, 1.6);
            double widthScale = Math.Max(0.72, bounds.Width / 1500.0);
            double tickPixels = Math.Max(12, TickFontSizePx * s);

            int pdChars = Math.Max(FormatInteger(pdYMin).Length, FormatInteger(pdYMax).Length);
            int hvChars = Math.Max(FormatOneDecimal(hvYMin).Length, FormatOneDecimal(hvYMax).Length);
            int leftForTicks = (int)Math.Ceiling(pdChars * tickPixels * 0.64 + tickPixels + 34);
            int rightForTicks = (int)Math.Ceiling(hvChars * tickPixels * 0.64 + tickPixels + 46);

            bool compact = bounds.Width < 700 || bounds.Height < 360;
            int left = compact ? Math.Max(62, leftForTicks) : Math.Max((int)(100 * s * widthScale), leftForTicks);
            int right = compact ? Math.Max(58, rightForTicks) : Math.Max((int)(105 * s * widthScale), rightForTicks);
            int top = compact ? 72 : Math.Max((int)(78 * s), (int)Math.Ceiling(tickPixels * 3.2));
            int bottom = compact ? 58 : Math.Max((int)(76 * s), (int)Math.Ceiling(tickPixels * 3.4));
            return Rectangle.FromLTRB(bounds.Left + left, bounds.Top + top,
                Math.Max(bounds.Left + left + 50, bounds.Right - right),
                Math.Max(bounds.Top + top + 50, bounds.Bottom - bottom));
        }

        private Font CreateScaledFont(Rectangle bounds, double sourcePixelSize, FontStyle style)
        {
            double size = sourcePixelSize * Clamp(TextScale, 0.6, 1.6);
            return new Font("Arial", (float)Math.Max(12, size), style, GraphicsUnit.Pixel);
        }

        private void DrawGridAxesTicks(Graphics g, Rectangle bounds, Rectangle plot, Font tickFont, Font axisFont, Brush textBrush)
        {
            using (var gridPen = new Pen(Color.FromArgb(226, 231, 236), 1f))
            using (var axisPen = new Pen(Color.FromArgb(76, 84, 94), Math.Max(1f, bounds.Width / 1800f)))
            {
                double xStep = NiceStep(viewXMax - viewXMin, Math.Max(5, plot.Width / 135));
                double xFirst = Math.Ceiling(viewXMin / xStep) * xStep;
                float lastLabelRight = float.NegativeInfinity;
                for (double x = xFirst; x <= viewXMax + xStep * 1e-6; x += xStep)
                {
                    float px = XToPixel(x, plot);
                    g.DrawLine(gridPen, px, plot.Top, px, plot.Bottom);
                    g.DrawLine(axisPen, px, plot.Bottom, px, plot.Bottom + 6);
                    string label = FormatXAxisTick(x, xStep, TimeUnit);
                    SizeF labelSize = g.MeasureString(label, tickFont);
                    float labelLeft = px - labelSize.Width / 2f;
                    if (labelLeft >= lastLabelRight + 8 && labelLeft >= bounds.Left &&
                        labelLeft + labelSize.Width <= bounds.Right)
                    {
                        g.DrawString(label, tickFont, textBrush, labelLeft, plot.Bottom + 8);
                        lastLabelRight = labelLeft + labelSize.Width;
                    }
                }

                double pdStep = NiceStep(pdYMax - pdYMin, Math.Max(3, (SeparateWaveBands ? PdBand(plot).Height : plot.Height) / 80));
                double pdFirst = Math.Ceiling(pdYMin / pdStep) * pdStep;
                for (double y = pdFirst; y <= pdYMax + pdStep * 1e-6; y += pdStep)
                {
                    float py = PdYToPixel(y, plot);
                    g.DrawLine(gridPen, plot.Left, py, plot.Right, py);
                    g.DrawLine(axisPen, plot.Left - 6, py, plot.Left, py);
                    string text = FormatInteger(y);
                    SizeF size = g.MeasureString(text, tickFont);
                    g.DrawString(text, tickFont, textBrush, plot.Left - size.Width - 9, py - size.Height / 2);
                }

                double hvStep = NiceStep(hvYMax - hvYMin, Math.Max(3, (SeparateWaveBands ? HvBand(plot).Height : plot.Height) / 80));
                double hvFirst = Math.Ceiling(hvYMin / hvStep) * hvStep;
                for (double y = hvFirst; y <= hvYMax + hvStep * 1e-6; y += hvStep)
                {
                    float py = HvYToPixel(y, plot);
                    if (SeparateWaveBands) g.DrawLine(gridPen, plot.Left, py, plot.Right, py);
                    g.DrawLine(axisPen, plot.Right, py, plot.Right + 6, py);
                    g.DrawString(FormatOneDecimal(y), tickFont, textBrush, plot.Right + 10, py - tickFont.Height / 2f);
                }

                g.DrawRectangle(axisPen, plot);
                if (SeparateWaveBands)
                {
                    int separator = (HvBand(plot).Bottom + PdBand(plot).Top) / 2;
                    using (var divider = new Pen(Color.FromArgb(198, 209, 220)))
                        g.DrawLine(divider, plot.Left, separator, plot.Right, separator);
                }
                SizeF xSize = g.MeasureString(XAxisTitle, axisFont);
                g.DrawString(XAxisTitle, axisFont, textBrush, plot.Left + (plot.Width - xSize.Width) / 2,
                    plot.Bottom + Math.Max(30, tickFont.Height + 15));
                DrawRotatedAxisTitle(g, LeftYAxisTitle, axisFont, textBrush,
                    Math.Max(16, plot.Left - Math.Max(62, plot.Left * 0.58f)),
                    SeparateWaveBands ? PdBand(plot).Top + PdBand(plot).Height / 2f : plot.Top + plot.Height / 2f, -90);
                DrawRotatedAxisTitle(g, RightYAxisTitle, axisFont, textBrush,
                    Math.Min(bounds.Right - 17, plot.Right + Math.Max(65, (bounds.Right - plot.Right) * 0.60f)),
                    SeparateWaveBands ? HvBand(plot).Top + HvBand(plot).Height / 2f : plot.Top + plot.Height / 2f, 90);
            }
        }

        private void DrawHvWaveform(Graphics g, Rectangle plot)
        {
            double[] local;
            double rate;
            lock (dataLock) { local = hv; rate = samplingRateHv; }
            PointF[] points = CreateHvDisplayPoints(local, rate, plot);
            if (points.Length < 2) return;
            using (var pen = new Pen(HvColor, Math.Max(1.3f, plot.Width / 1400f)) { LineJoin = LineJoin.Round })
            {
                g.SetClip(plot);
                g.DrawLines(pen, points);
                g.ResetClip();
            }
        }

        private void DrawPdWaveform(Graphics g, Rectangle plot)
        {
            double[] local;
            double rate;
            lock (dataLock) { local = pd; rate = samplingRatePd; }
            PointF[] points = CreatePdPeakPreservingPoints(local, rate, plot);
            if (points.Length == 0) return;
            // PD is an impulse quantity. Draw each pixel bucket as a thin stem from
            // zero; connecting sparse peaks invents charge between samples.
            float baseline = PdYToPixel(0, plot);
            using (var pen = new Pen(PdColor, 1f))
            {
                g.SetClip(plot);
                foreach (PointF peak in points)
                    if (peak.Y < baseline - .5f) g.DrawLine(pen, peak.X, baseline, peak.X, peak.Y);
                g.ResetClip();
            }
        }

        private PointF[] CreateHvDisplayPoints(double[] values, double rate, Rectangle plot)
        {
            int start = ClampIndex((int)Math.Floor(viewXMin * rate), values.Length);
            int end = ClampIndex((int)Math.Ceiling(viewXMax * rate), values.Length);
            if (end <= start) return Array.Empty<PointF>();
            int maximum = Math.Max(2, plot.Width * 2);
            int count = end - start + 1;
            int stride = Math.Max(1, count / maximum);
            var points = new List<PointF>(Math.Min(count, maximum + 2));
            for (int i = start; i <= end; i += stride)
                if (IsFinite(values[i])) points.Add(new PointF(XToPixel(i / rate, plot), HvYToPixel(values[i], plot)));
            if (IsFinite(values[end])) points.Add(new PointF(XToPixel(end / rate, plot), HvYToPixel(values[end], plot)));
            return points.ToArray();
        }

        private PointF[] CreatePdPeakPreservingPoints(double[] values, double rate, Rectangle plot)
        {
            int start = ClampIndex((int)Math.Floor(viewXMin * rate), values.Length);
            int end = ClampIndex((int)Math.Ceiling(viewXMax * rate), values.Length);
            if (end <= start) return Array.Empty<PointF>();
            int buckets = Math.Max(2, plot.Width);
            var points = new List<PointF>(buckets + 2);
            for (int bucket = 0; bucket < buckets; bucket++)
            {
                int i0 = start + (int)((end - start + 1L) * bucket / buckets);
                int i1 = start + (int)((end - start + 1L) * (bucket + 1) / buckets) - 1;
                i1 = Math.Max(i0, Math.Min(end, i1));
                double peak = 0;
                int peakIndex = i0;
                for (int i = i0; i <= i1; i++)
                {
                    if (IsFinite(values[i]) && values[i] > peak) { peak = values[i]; peakIndex = i; }
                }
                points.Add(new PointF(XToPixel(peakIndex / rate, plot), PdYToPixel(peak, plot)));
            }
            return points.ToArray();
        }

        private void DrawLegendAndTitle(Graphics g, Rectangle bounds, Rectangle plot, Font legendFont, Font titleFont, Brush textBrush)
        {
            if (ShowChartTitle) g.DrawString(ChartTitle, titleFont, textBrush, plot.Left, bounds.Top + 6);

            float y = ShowChartTitle ? bounds.Top + Math.Max(32, titleFont.Height + 10) : bounds.Top + 10;
            float x = plot.Left;
            DrawLegendItem(g, PdName, PdColor, legendFont, textBrush, ref x, y);
            float hvWidth = 43 + g.MeasureString(HvName, legendFont).Width;
            if (x + 18 + hvWidth > plot.Right)
            {
                x = plot.Left;
                y += legendFont.Height + 6;
            }
            else x += 18;
            DrawLegendItem(g, HvName, HvColor, legendFont, textBrush, ref x, y);
        }

        private static void DrawLegendItem(Graphics g, string name, Color color, Font font, Brush textBrush, ref float x, float y)
        {
            using (var pen = new Pen(color, 2f)) g.DrawLine(pen, x, y + font.Height / 2f, x + 35, y + font.Height / 2f);
            g.DrawString(name, font, textBrush, x + 43, y);
            x += 43 + g.MeasureString(name, font).Width;
        }

        private void DrawMeasurementCursors(Graphics g, Rectangle bounds, Rectangle plot, Font font, Brush textBrush)
        {
            var occupied = new List<RectangleF>();
            if (cursor1Active && cursor2Active) DrawAttenuationCard(g, plot, font, occupied);
            if (cursor1Active) DrawVerticalPeakCursor(g, plot, cursor1X, cursor1Measurement, Cursor1Color, "C1", font, occupied);
            if (cursor2Active) DrawVerticalPeakCursor(g, plot, cursor2X, cursor2Measurement, Cursor2Color, "C2", font, occupied);
            if (probeActive) DrawVoltageProbe(g, plot, font, occupied);
            if (pdLevelActive) DrawPdLevelCursor(g, plot, font, occupied);
        }

        private void DrawVerticalPeakCursor(Graphics g, Rectangle plot, double x, Measurement measurement, Color color, string name, Font font, List<RectangleF> occupied)
        {
            float px = XToPixel(x, plot);
            using (var pen = new Pen(color, 1.4f) { DashStyle = DashStyle.Dash })
            using (var brush = new SolidBrush(color))
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawLine(pen, px, plot.Top, px, plot.Bottom);
                PointF[] handle = { new PointF(px - 7, plot.Top), new PointF(px + 7, plot.Top), new PointF(px, plot.Top + 10) };
                g.FillPolygon(brush, handle);
                string label = name + "  " + FormatTimeCompact(measurement.XSeconds) + "  " + FormatVoltage(measurement.Amplitude);
                RectangleF box = PlaceTag(g, plot, new PointF(px, plot.Top + 14), label, font, occupied, true);
                DrawTagBox(g, box, label, font, brush, white);
            }
        }

        private void DrawVoltageProbe(Graphics g, Rectangle plot, Font font, List<RectangleF> occupied)
        {
            double voltage = InterpolateHv(probeX);
            double phase = CalculatePhaseDegrees(probeX);
            float px = XToPixel(probeX, plot);
            float py = HvYToPixel(voltage, plot);
            using (var pen = new Pen(ProbeColor, 2f))
            using (var brush = new SolidBrush(Color.White))
            using (var tagBrush = new SolidBrush(ProbeColor))
            using (var white = new SolidBrush(Color.White))
            {
                g.FillEllipse(brush, px - 6, py - 6, 12, 12);
                g.DrawEllipse(pen, px - 6, py - 6, 12, 12);
                string label = FormatVoltage(voltage) + "  |  " + FormatNumber(phase) + "°";
                float labelY = py < plot.Top + 70 ? py + 14 : py - font.Height - 16;
                RectangleF box = PlaceTag(g, plot, new PointF(px, labelY), label, font, occupied, py < plot.Top + 70);
                DrawTagBox(g, box, label, font, tagBrush, white);
            }
        }

        private void DrawPdLevelCursor(Graphics g, Rectangle plot, Font font, List<RectangleF> occupied)
        {
            float py = PdYToPixel(pdLevel, plot);
            using (var pen = new Pen(PdCursorColor, 1.4f) { DashStyle = DashStyle.Dash })
            using (var brush = new SolidBrush(PdCursorColor))
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawLine(pen, plot.Left, py, plot.Right, py);
                string label = (EnglishVersion ? "PD " : "放电量 ") + FormatInteger(pdLevel) + " pC";
                RectangleF box = PlaceTag(g, plot, new PointF(plot.Right - 64, py - font.Height - 10), label, font, occupied, false);
                DrawTagBox(g, box, label, font, brush, white);
            }
        }

        private void DrawAttenuationCard(Graphics g, Rectangle plot, Font font, List<RectangleF> occupied)
        {
            string suffix = EnglishVersion ? "%/cycle" : "%/周期";
            string value = IsFinite(attenuationPercentPerCycle)
                ? FormatNumber(attenuationPercentPerCycle) + suffix
                : "--" + suffix;
            string caption = EnglishVersion ? "Amplitude attenuation" : "幅值衰减";
            string text = caption + "  " + value;
            using (var background = new SolidBrush(Color.FromArgb(225, 36, 44, 57)))
            using (var white = new SolidBrush(Color.White))
            {
                SizeF size = g.MeasureString(text, font);
                RectangleF box = new RectangleF(plot.Right - size.Width - 25, plot.Top + 8, size.Width + 14, size.Height + 6);
                occupied.Add(box);
                DrawTagBox(g, box, text, font, background, white);
            }
        }

        private static RectangleF PlaceTag(Graphics g, Rectangle plot, PointF anchor, string text, Font font,
            List<RectangleF> occupied, bool preferDown, bool centerOnAnchor = true)
        {
            SizeF size = g.MeasureString(text, font);
            float width = size.Width + 14;
            float height = size.Height + 6;
            float x = centerOnAnchor ? anchor.X - width / 2f : anchor.X;
            x = Clamp(x, plot.Left + 3, plot.Right - width - 3);
            float y = Clamp(anchor.Y, plot.Top + 3, plot.Bottom - height - 3);
            RectangleF candidate = new RectangleF(x, y, width, height);
            float step = height + 5;

            for (int attempt = 0; attempt < 16 && IntersectsAny(candidate, occupied); attempt++)
            {
                float proposed = preferDown ? candidate.Y + step : candidate.Y - step;
                if (proposed < plot.Top + 3 || proposed + height > plot.Bottom - 3)
                {
                    preferDown = !preferDown;
                    proposed = preferDown ? plot.Top + 3 + attempt * step : plot.Bottom - height - 3 - attempt * step;
                }
                candidate.Y = Clamp(proposed, plot.Top + 3, plot.Bottom - height - 3);
            }
            occupied.Add(candidate);
            return candidate;
        }

        private static bool IntersectsAny(RectangleF candidate, List<RectangleF> occupied)
        {
            RectangleF padded = RectangleF.Inflate(candidate, 4, 3);
            for (int i = 0; i < occupied.Count; i++)
                if (padded.IntersectsWith(occupied[i])) return true;
            return false;
        }

        private static void DrawTagBox(Graphics g, RectangleF box, string text, Font font, Brush background, Brush foreground)
        {
            FillRoundedRectangle(g, background, box, 5);
            g.DrawString(text, font, foreground, box.Left + 7, box.Top + 3);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (e.Button == MouseButtons.Right)
            {
                ResetView();
                return;
            }
            if (e.Button != MouseButtons.Left || !plot.Contains(e.Location)) return;

            if (cursor1Active && Math.Abs(e.X - XToPixel(cursor1X, plot)) <= 9) dragTarget = DragTarget.Cursor1;
            else if (cursor2Active && Math.Abs(e.X - XToPixel(cursor2X, plot)) <= 9) dragTarget = DragTarget.Cursor2;
            else if (probeActive && Distance(e.Location, GetProbePixel(plot)) <= 13) dragTarget = DragTarget.Probe;
            else if (pdLevelActive && Math.Abs(e.Y - PdYToPixel(pdLevel, plot)) <= 9) dragTarget = DragTarget.PdLevel;
            else dragTarget = DragTarget.None;

            if (dragTarget != DragTarget.None)
            {
                Capture = true;
                Cursor = dragTarget == DragTarget.PdLevel ? Cursors.SizeNS : Cursors.SizeWE;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (dragTarget == DragTarget.Cursor1) cursor1X = Clamp(PixelToX(e.X, plot), viewXMin, viewXMax);
            else if (dragTarget == DragTarget.Cursor2) cursor2X = Clamp(PixelToX(e.X, plot), viewXMin, viewXMax);
            else if (dragTarget == DragTarget.Probe) probeX = Clamp(PixelToX(e.X, plot), viewXMin, viewXMax);
            else if (dragTarget == DragTarget.PdLevel) pdLevel = Clamp(PixelToPdY(e.Y, plot), pdYMin, pdYMax);
            else return;

            UpdateMeasurements();
            Invalidate();
            MeasurementChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragTarget = DragTarget.None;
            Capture = false;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        { base.OnMouseWheel(e); ApplyWheelZoom(e, (ModifierKeys & Keys.Shift) != 0); }
        internal void ApplyWheelZoom(MouseEventArgs e, bool vertical)
        {
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (!plot.Contains(e.Location)) return;
            if (vertical)
            {
                double factorY = e.Delta > 0 ? .82 : 1.22;
                Rectangle pdBand = SeparateWaveBands ? PdBand(plot) : plot;
                Rectangle hvBand = SeparateWaveBands ? HvBand(plot) : plot;
                if (!SeparateWaveBands || pdBand.Contains(e.Location)) ZoomY(ref pdYMin, ref pdYMax, (pdBand.Bottom-e.Y)/(double)Math.Max(1,pdBand.Height), factorY);
                if (!SeparateWaveBands || hvBand.Contains(e.Location)) ZoomY(ref hvYMin, ref hvYMax, (hvBand.Bottom-e.Y)/(double)Math.Max(1,hvBand.Height), factorY);
                Invalidate(); return;
            }
            double anchor = PixelToX(e.X, plot);
            double factor = e.Delta > 0 ? 0.82 : 1.22;
            double newMin = anchor - (anchor - viewXMin) * factor;
            double newMax = anchor + (viewXMax - anchor) * factor;
            double minimumSpan = Math.Max(durationSeconds / 10000.0, 1e-12);
            if (newMax - newMin >= minimumSpan)
            {
                viewXMin = Math.Max(0, newMin);
                viewXMax = Math.Min(durationSeconds, newMax);
                if (viewXMax - viewXMin < minimumSpan) { viewXMin = 0; viewXMax = durationSeconds; }
                EnsureCursorsVisible();
                UpdateMeasurements();
                Invalidate();
            }
        }

        private void CalculateAutomaticRanges()
        {
            double[] localHv;
            double[] localPd;
            lock (dataLock) { localHv = hv; localPd = pd; }
            FindFiniteRange(localHv, out double hvMin, out double hvMax);
            FindFiniteRange(localPd, out double pMin, out double pMax);
            ExpandRange(ref hvMin, ref hvMax, 0.08);
            pMin = Math.Min(0, pMin);
            pMax = NiceCeiling(Math.Max(1, pMax), 8);
            if (pMax <= pMin) pMax = pMin + 1;
            hvYMin = hvMin;
            hvYMax = hvMax;
            pdYMin = pMin;
            pdYMax = pMax;
        }

        static void ZoomY(ref double low, ref double high, double fraction, double factor)
        {
            double span=(high-low)*factor;
            if (!IsFinite(span) || span<1e-15 || span>1e100) return;
            double anchor=low+(high-low)*fraction; low=anchor-span*fraction; high=low+span;
        }

        private void EstimateOscillationPeriod()
        {
            double[] local;
            double rate;
            lock (dataLock) { local = hv; rate = samplingRateHv; }
            if (local.Length < 4) { estimatedPeriodSeconds = 0; phaseOriginSeconds = 0; return; }

            double center = EstimateCenter(local);
            var crossings = new List<double>();
            for (int i = 1; i < local.Length; i++)
            {
                double a = local[i - 1] - center;
                double b = local[i] - center;
                if (IsFinite(a) && IsFinite(b) && a <= 0 && b > 0)
                {
                    double fraction = Math.Abs(b - a) < 1e-20 ? 0 : -a / (b - a);
                    crossings.Add((i - 1 + fraction) / rate);
                }
            }
            if (crossings.Count >= 2)
            {
                var periods = new List<double>();
                for (int i = 1; i < crossings.Count; i++)
                    if (crossings[i] > crossings[i - 1]) periods.Add(crossings[i] - crossings[i - 1]);
                periods.Sort();
                estimatedPeriodSeconds = periods[periods.Count / 2];
                phaseOriginSeconds = crossings[0];
            }
            else
            {
                estimatedPeriodSeconds = durationSeconds / 10.0;
                phaseOriginSeconds = 0;
            }
        }

        private void UpdateMeasurements()
        {
            cursor1Measurement = FindCycleExtremum(cursor1X);
            cursor2Measurement = FindCycleExtremum(cursor2X);

            // C1/C2始终落在所选周期的正峰或负谷；数据刷新、激活及拖动时均自动吸附。
            if (cursor1Active && IsFinite(cursor1Measurement.XSeconds))
                cursor1X = Clamp(cursor1Measurement.XSeconds, viewXMin, viewXMax);
            if (cursor2Active && IsFinite(cursor2Measurement.XSeconds))
                cursor2X = Clamp(cursor2Measurement.XSeconds, viewXMin, viewXMax);

            attenuationPercentPerCycle = CalculateAttenuation(cursor1Measurement, cursor2Measurement);
        }

        private Measurement FindCycleExtremum(double xSeconds)
        {
            double[] local;
            double rate;
            lock (dataLock) { local = hv; rate = samplingRateHv; }
            int centerIndex = ClampIndex((int)Math.Round(xSeconds * rate), local.Length);
            int radius = estimatedPeriodSeconds > 0
                ? Math.Max(2, (int)Math.Round(estimatedPeriodSeconds * rate * 0.48))
                : Math.Max(2, local.Length / 30);
            int start = Math.Max(0, centerIndex - radius);
            int end = Math.Min(local.Length - 1, centerIndex + radius);
            double center = EstimateCenter(local);
            bool positive = local[centerIndex] >= center;
            int best = centerIndex;
            double value = local[best];
            for (int i = start; i <= end; i++)
            {
                if (!IsFinite(local[i])) continue;
                if ((positive && local[i] > value) || (!positive && local[i] < value)) { value = local[i]; best = i; }
            }
            return new Measurement(best / rate, value);
        }

        private double CalculateAttenuation(Measurement a, Measurement b)
        {
            if (!IsFinite(a.Amplitude) || !IsFinite(b.Amplitude) || estimatedPeriodSeconds <= 0) return double.NaN;
            Measurement first = a.XSeconds <= b.XSeconds ? a : b;
            Measurement second = a.XSeconds <= b.XSeconds ? b : a;
            double cycles = (second.XSeconds - first.XSeconds) / estimatedPeriodSeconds;
            double amp1 = Math.Abs(first.Amplitude);
            double amp2 = Math.Abs(second.Amplitude);
            if (cycles <= 0 || amp1 <= 1e-20 || amp2 <= 1e-20) return double.NaN;
            return (1.0 - Math.Pow(amp2 / amp1, 1.0 / cycles)) * 100.0;
        }

        private double InterpolateHv(double xSeconds)
        {
            double[] local;
            double rate;
            lock (dataLock) { local = hv; rate = samplingRateHv; }
            double position = Clamp(xSeconds * rate, 0, local.Length - 1);
            int i0 = (int)Math.Floor(position);
            int i1 = Math.Min(local.Length - 1, i0 + 1);
            double fraction = position - i0;
            return local[i0] + (local[i1] - local[i0]) * fraction;
        }

        private double CalculatePhaseDegrees(double xSeconds)
        {
            if (!IsFinite(estimatedPeriodSeconds) || estimatedPeriodSeconds <= 0) return double.NaN;
            double phase = (xSeconds - phaseOriginSeconds) / estimatedPeriodSeconds * 360.0;
            phase %= 360.0;
            if (phase < 0) phase += 360.0;
            return phase;
        }

        private void InitializeOrClampCursors()
        {
            if (!IsFinite(cursor1X) || cursor1X < 0 || cursor1X > durationSeconds) cursor1X = durationSeconds * 0.24;
            if (!IsFinite(cursor2X) || cursor2X < 0 || cursor2X > durationSeconds) cursor2X = durationSeconds * 0.62;
            if (!IsFinite(probeX) || probeX < 0 || probeX > durationSeconds) probeX = durationSeconds * 0.42;
            if (!IsFinite(pdLevel) || pdLevel < pdYMin || pdLevel > pdYMax) pdLevel = pdYMin + (pdYMax - pdYMin) * 0.55;
            EnsureCursorsVisible();
        }

        private void EnsureCursorsVisible()
        {
            double span = Math.Max(1e-20, viewXMax - viewXMin);
            double margin = span * 0.015;
            double min = viewXMin + margin;
            double max = viewXMax - margin;
            if (max <= min) { min = viewXMin; max = viewXMax; }
            if (cursor1Active) cursor1X = Clamp(cursor1X, min, max);
            if (cursor2Active) cursor2X = Clamp(cursor2X, min, max);
            if (probeActive) probeX = Clamp(probeX, min, max);
            if (pdLevelActive) pdLevel = Clamp(pdLevel, pdYMin, pdYMax);
        }

        private PointF GetProbePixel(Rectangle plot) => new PointF(XToPixel(probeX, plot), HvYToPixel(InterpolateHv(probeX), plot));

        private float XToPixel(double value, Rectangle plot) =>
            plot.Left + (float)((value - viewXMin) / Math.Max(1e-20, viewXMax - viewXMin) * plot.Width);
        private Rectangle HvBand(Rectangle plot)
        {
            int gap = Math.Max(12, Math.Min(20, plot.Height / 25));
            int height = (int)Math.Round((plot.Height - gap) * .55);
            return new Rectangle(plot.Left, plot.Top, plot.Width, height);
        }
        private Rectangle PdBand(Rectangle plot)
        {
            Rectangle top = HvBand(plot);
            int start = top.Bottom + Math.Max(12, Math.Min(20, plot.Height / 25));
            return Rectangle.FromLTRB(plot.Left, start, plot.Right, plot.Bottom);
        }
        private float PdYToPixel(double value, Rectangle plot)
        {
            Rectangle band = SeparateWaveBands ? PdBand(plot) : plot;
            return band.Bottom - (float)((value - pdYMin) / Math.Max(1e-20, pdYMax - pdYMin) * band.Height);
        }
        private float HvYToPixel(double value, Rectangle plot)
        {
            Rectangle band = SeparateWaveBands ? HvBand(plot) : plot;
            return band.Bottom - (float)((value - hvYMin) / Math.Max(1e-20, hvYMax - hvYMin) * band.Height);
        }
        private double PixelToX(float pixel, Rectangle plot) =>
            viewXMin + (pixel - plot.Left) / Math.Max(1.0, plot.Width) * (viewXMax - viewXMin);
        private double PixelToPdY(float pixel, Rectangle plot)
        {
            Rectangle band = SeparateWaveBands ? PdBand(plot) : plot;
            return pdYMax - (pixel - band.Top) / Math.Max(1.0, band.Height) * (pdYMax - pdYMin);
        }

        private static string FormatXAxisTick(double seconds, double stepSeconds, string unit)
        {
            double factor = SecondsToDisplayFactor(unit);
            double value = seconds * factor;
            double step = Math.Abs(stepSeconds * factor);
            if (!IsFinite(value) || !IsFinite(step) || step <= 0) return FormatNumber(value);
            int places = 0;
            while (places < 12 && Math.Abs(step - Math.Round(step, places)) > Math.Max(1e-13, step * 1e-8))
                places++;
            string format = places == 0 ? "0" : "0." + new string('0', places);
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private string FormatTime(double seconds) => FormatNumber(seconds * SecondsToDisplayFactor(TimeUnit)) + " " + TimeUnit;
        private string FormatTimeCompact(double seconds) => FormatNumber(seconds * SecondsToDisplayFactor(TimeUnit)) + TimeUnit;
        private string FormatVoltage(double value) => FormatOneDecimal(value) + DefaultIfBlank(VoltageUnit, "kV");

        private static string ResolveTimeUnit(string requested, double duration)
        {
            if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
            if (duration < 1e-6) return "ns";
            if (duration < 1e-3) return "μs";
            if (duration < 1) return "ms";
            return "s";
        }

        private static double SecondsToDisplayFactor(string unit)
        {
            string u = (unit ?? string.Empty).Trim().ToLowerInvariant();
            if (u == "ns") return 1e9;
            if (u == "us" || u == "μs" || u == "µs") return 1e6;
            if (u == "ms") return 1e3;
            return 1;
        }

        private static void ValidateWaveform(double[] values, double rate, string valuesName, string rateName)
        {
            // 保留兼容方法；LabVIEW未接线端口在UpdateSynchronizedPd中自动补为空波形。
        }

        private static void FindFiniteRange(double[] values, out double minimum, out double maximum)
        {
            minimum = double.PositiveInfinity;
            maximum = double.NegativeInfinity;
            foreach (double value in values)
            {
                if (!IsFinite(value)) continue;
                if (value < minimum) minimum = value;
                if (value > maximum) maximum = value;
            }
            if (!IsFinite(minimum) || !IsFinite(maximum)) { minimum = -1; maximum = 1; }
            if (maximum <= minimum) { minimum -= 1; maximum += 1; }
        }

        private static void ExpandRange(ref double minimum, ref double maximum, double fraction)
        {
            double span = maximum - minimum;
            if (!IsFinite(span) || span <= 0) span = 1;
            minimum -= span * fraction;
            maximum += span * fraction;
        }

        private static double EstimateCenter(double[] values)
        {
            int stride = Math.Max(1, values.Length / 2048);
            var sample = new List<double>();
            for (int i = 0; i < values.Length; i += stride) if (IsFinite(values[i])) sample.Add(values[i]);
            if (sample.Count == 0) return 0;
            sample.Sort();
            return sample[sample.Count / 2];
        }

        private static void DrawRotatedAxisTitle(Graphics g, string text, Font font, Brush brush, float x, float y, float angle)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(angle);
            SizeF size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, -size.Width / 2, -size.Height / 2);
            g.Restore(state);
        }

        private static void DrawCentered(Graphics g, string text, Font font, Brush brush, PointF point)
        {
            SizeF size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, point.X - size.Width / 2, point.Y);
        }

        private static void FillRoundedRectangle(Graphics g, Brush brush, RectangleF rectangle, float radius)
        {
            using (var path = new GraphicsPath())
            {
                float d = radius * 2;
                path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90);
                path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270, 90);
                path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
                path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        private static double NiceStep(double range, int targetCount)
        {
            if (!IsFinite(range) || range <= 0) return 1;
            double rough = range / Math.Max(1, targetCount);
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            double n = rough / magnitude;
            double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
            return nice * magnitude;
        }

        private static double NiceCeiling(double value, int targetCount)
        {
            double step = NiceStep(Math.Abs(value), targetCount);
            return Math.Ceiling(value / step) * step;
        }

        private void SafeInvalidate()
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(new Action(Invalidate)); else Invalidate();
        }

        private static int ClampIndex(int index, int length) => Math.Max(0, Math.Min(length - 1, index));
        private static double Distance(Point a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        private static string DefaultIfBlank(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

        private static string FormatNumber(double value)
        {
            if (!IsFinite(value)) return "--";
            double abs = Math.Abs(value);
            if ((abs >= 100000 || (abs > 0 && abs < 0.001))) return value.ToString("0.###E+0", CultureInfo.InvariantCulture);
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatInteger(double value)
        {
            if (!IsFinite(value)) return "--";
            return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string FormatOneDecimal(double value)
        {
            if (!IsFinite(value)) return "--";
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            return new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private struct Measurement
        {
            public Measurement(double xSeconds, double amplitude) { XSeconds = xSeconds; Amplitude = amplitude; }
            public double XSeconds;
            public double Amplitude;
        }

        private enum DragTarget { None, Cursor1, Cursor2, Probe, PdLevel }
    }
}


