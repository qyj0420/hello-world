using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.Controls;

namespace CalibrationWaveform
{
    /// <summary>接头集合或接头几何关系发生变化时提供给 LabVIEW 的事件数据。</summary>
    public sealed class ConnectorsChangedEventArgs : EventArgs
    {
        internal ConnectorsChangedEventArgs(long sequence, int connectorCount)
        {
            Sequence = sequence;
            ConnectorCount = connectorCount;
        }

        /// <summary>每次有效变化递增，便于 LabVIEW 事件结构判定新数据。</summary>
        public long Sequence { get; }

        public int ConnectorCount { get; }
    }

    /// <summary>
    /// 可直接放入 LabVIEW .NET Container 的 TDR/校准波形控件。
    /// 目标框架必须是 .NET Framework 4.8，且必须保留无参数构造函数。
    /// </summary>
    [ToolboxItem(true)]
    public sealed partial class CalibrationWaveformControl : UserControl
    {
        private const int PlotLeft = 96;
        private const int PlotTop = 30;
        private const int PlotRight = 24;
        private const int PlotBottom = 58;
        private const int CursorHitWidth = 9;

        private readonly object dataLock = new object();
        private double[] xs = Array.Empty<double>();
        private double[] ys = Array.Empty<double>();
        private readonly List<ConnectorMark> connectors = new List<ConnectorMark>();

        private double dataXMin;
        private double dataXMax = 1;
        private double viewXMin;
        private double viewXMax = 1;
        private double viewYMin = -1;
        private double viewYMax = 1;
        private double lastPaintYMin = -1;
        private double lastPaintYMax = 1;
        private bool autoScaleY = true;
        private double incidentX = 0.25;
        private double reflectionX = 0.70;
        private double propagationVelocityMPerUs = 160.0;
        private double cableLengthMeters = 415.0;
        private long connectorSequence;
        private DragTarget dragTarget;
        private Point selectionStart;
        private Point selectionEnd;
        private string languageCode = "zh-CN";
        private string customChartTitle, customXAxisTitle, customYAxisTitle;
        private string customIncidentCaption, customReflectionCaption;

        public CalibrationWaveformControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);

            BackColor = BrowserTheme.Surface;
            ForeColor = BrowserTheme.Ink;
            Font = new Font("Arial", 10F, FontStyle.Regular, GraphicsUnit.Point);
            MinimumSize = new Size(320, 220);
            Size = new Size(960, 500);
            TabStop = true;
        }

        [Category("外观")]
        public string ChartTitle { get => customChartTitle ?? Text("校准波形", "Calibration Waveform"); set { customChartTitle = value; SafeInvalidate(); } }

        [Category("外观")]
        public string XAxisTitle { get => customXAxisTitle ?? Text("时间 (μs)", "Time (μs)"); set { customXAxisTitle = value; SafeInvalidate(); } }

        [Category("外观")]
        public string YAxisTitle { get => customYAxisTitle ?? Text("幅值 (mV)", "Amplitude (mV)"); set { customYAxisTitle = value; SafeInvalidate(); } }

        [Category("外观")]
        public string IncidentCaption { get => customIncidentCaption ?? Text("入射波", "Incident"); set { customIncidentCaption = value; SafeInvalidate(); } }

        [Category("外观")]
        public string ReflectionCaption { get => customReflectionCaption ?? Text("反射波", "Reflection"); set { customReflectionCaption = value; SafeInvalidate(); } }

        [Category("Language"), DefaultValue(false)]
        [Description("False = 中文; True = English. Changes display language without resetting waveform, cursors or view.")]
        public bool EnglishVersion
        {
            get => languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            set => SetEnglishDisplay(value);
        }

        public void SetEnglishDisplay(bool englishVersion) { SetLanguage(englishVersion ? "en-US" : "zh-CN"); }

        [Category("语言")]
        [Description("支持 zh-CN 和 en-US。")]
        public string LanguageCode
        {
            get => languageCode;
            set => SetLanguage(value);
        }

        [Category("外观")]
        public Color WaveformColor { get; set; } = BrowserTheme.Primary;

        [Category("外观")]
        public Color IncidentCursorColor { get; set; } = BrowserTheme.Cyan;

        [Category("外观")]
        public Color ReflectionCursorColor { get; set; } = BrowserTheme.PhaseA;

        [Category("计算")]
        [Description("传播速度，单位 m/μs。距离按 Δt × 传播速度 / 2 计算。")]
        public double PropagationVelocityMPerUs
        {
            get => propagationVelocityMPerUs;
            set
            {
                ValidateCableParameters(value, cableLengthMeters);
                if (value.Equals(propagationVelocityMPerUs)) return;
                propagationVelocityMPerUs = value;
                ConnectorGeometryChanged();
            }
        }

        [Category("计算")]
        [Description("电缆长度，单位 m。接头距离输入允许包含 0 m 和电缆终端位置。")]
        public double CableLengthMeters
        {
            get => cableLengthMeters;
            set
            {
                ValidateCableParameters(propagationVelocityMPerUs, value);
                if (value.Equals(cableLengthMeters)) return;
                cableLengthMeters = value;
                ConnectorGeometryChanged();
            }
        }

        [Category("计算")]
        public bool ShowCalculatedDistance { get; set; } = true;

        [Browsable(false)]
        public double IncidentCursorX => incidentX;

        [Browsable(false)]
        public double ReflectionCursorX => reflectionX;

        [Browsable(false)]
        public double TimeDifference => reflectionX - incidentX;

        [Browsable(false)]
        public double CalculatedDistance => Math.Abs(TimeDifference) * PropagationVelocityMPerUs / 2.0;

        [Browsable(false)]
        public int ConnectorCount => connectors.Count;

        [Browsable(false)]
        public long ConnectorChangeSequence => connectorSequence;

        /// <summary>ConnectorChangeSequence 的短名称，方便 LabVIEW 直接读取 sequence。</summary>
        [Browsable(false)]
        public long Sequence => connectorSequence;

        /// <summary>LabVIEW 可用 Register Event Callback 注册此事件。</summary>
        public event EventHandler CursorChanged;
        public bool AllowCursorEditing { get; set; } = true;
        public bool UseExternalConnectorDialog { get; set; }
        public event EventHandler ConnectorDialogRequested;
        public double RequestedConnectorTime { get; private set; }

        /// <summary>接头增删、参数或入射游标变化时触发；事件参数包含递增 Sequence。</summary>
        public event EventHandler<ConnectorsChangedEventArgs> ConnectorsChanged;

        /// <summary>设置等间隔波形。x = x0 + i * dx。</summary>
        public void SetWaveform(double[] samples, double x0, double dx)
        {
            if (samples == null || samples.Length < 2)
            {
                SetWaveformXY(Array.Empty<double>(), Array.Empty<double>());
                return;
            }
            if (dx <= 0 || double.IsNaN(dx) || double.IsInfinity(dx)) dx = 1.0;

            var newX = new double[samples.Length];
            for (int i = 0; i < newX.Length; i++) newX[i] = x0 + i * dx;
            SetWaveformXY(newX, samples);
        }

        /// <summary>原等间隔波形入口增加语言布尔端；x0 和 dx 的单位与原入口一致。</summary>
        public void SetWaveformWithLanguage(double[] samples, double x0, double dx, bool englishVersion)
        {
            SetEnglishDisplay(englishVersion);
            SetWaveform(samples, x0, dx);
        }

        /// <summary>设置 XY 波形，适用于非等间隔横坐标。</summary>
        public void SetWaveformXY(double[] xValues, double[] yValues)
        {
            int count = Math.Min(xValues == null ? 0 : xValues.Length, yValues == null ? 0 : yValues.Length);
            if (count < 2)
            {
                lock (dataLock)
                {
                    xs = Array.Empty<double>(); ys = Array.Empty<double>();
                    dataXMin = viewXMin = 0; dataXMax = viewXMax = 1;
                    viewYMin = -1; viewYMax = 1; autoScaleY = true;
                }
                SafeInvalidate();
                return;
            }

            var newX = xValues.Take(count).ToArray();
            var newY = yValues.Take(count).ToArray();
            double minX = newX.Where(IsFinite).DefaultIfEmpty(0).Min();
            double maxX = newX.Where(IsFinite).DefaultIfEmpty(1).Max();
            if (maxX <= minX) maxX = minX + 1;

            bool incidentChanged;
            lock (dataLock)
            {
                double oldIncident = incidentX;
                xs = newX;
                ys = newY;
                dataXMin = minX;
                dataXMax = maxX;
                viewXMin = minX;
                viewXMax = maxX;
                autoScaleY = true;

                if (incidentX < minX || incidentX > maxX) incidentX = minX + (maxX - minX) * 0.25;
                if (reflectionX < minX || reflectionX > maxX) reflectionX = minX + (maxX - minX) * 0.70;
                incidentChanged = !NearlyEqual(oldIncident, incidentX);
            }
            SafeInvalidate();
            if (incidentChanged)
            {
                CursorChanged?.Invoke(this, EventArgs.Empty);
                ConnectorGeometryChanged();
            }
        }

        public void SetCursorPositions(double incidentPosition, double reflectionPosition)
        {
            if (!IsFinite(incidentPosition))
                throw new ArgumentOutOfRangeException(nameof(incidentPosition), "入射游标位置必须是有限数值。");
            if (!IsFinite(reflectionPosition))
                throw new ArgumentOutOfRangeException(nameof(reflectionPosition), "反射游标位置必须是有限数值。");
            double oldIncident = incidentX;
            incidentX = Clamp(incidentPosition, dataXMin, dataXMax);
            reflectionX = Clamp(reflectionPosition, dataXMin, dataXMax);
            SafeInvalidate();
            CursorChanged?.Invoke(this, EventArgs.Empty);
            if (!NearlyEqual(oldIncident, incidentX)) ConnectorGeometryChanged();
        }

        public double GetIncidentCursorX() => incidentX;
        public double GetReflectionCursorX() => reflectionX;
        public double GetTimeDifference() => TimeDifference;
        public double GetCalculatedDistance() => CalculatedDistance;
        [Browsable(false)] public double CableBoundaryX { get { return incidentX+2*cableLengthMeters/propagationVelocityMPerUs; } }

        /// <summary>同时设置波速 v（m/μs）和电缆长度 L（m），参数在修改前完整校验。</summary>
        public void SetCableParameters(double propagationVelocity, double cableLength)
        {
            ValidateCableParameters(propagationVelocity, cableLength);
            if (propagationVelocity.Equals(propagationVelocityMPerUs) &&
                cableLength.Equals(cableLengthMeters)) return;

            propagationVelocityMPerUs = propagationVelocity;
            cableLengthMeters = cableLength;
            ConnectorGeometryChanged();
        }

        /// <summary>增加一个接头标记。position 使用与 X 轴相同的单位。</summary>
        public void AddConnector(double position, string label)
        {
            if (!IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position), "接头时刻必须是有限数值。");
            double distance = TimeToDistance(position);
            if (HasDuplicateDistance(distance))
                throw new ArgumentException("相同位置的接头已存在。", nameof(position));

            connectors.Add(ConnectorMark.FromTime(position,
                string.IsNullOrWhiteSpace(label) ? null : label.Trim()));
            ConnectorCollectionChanged();
        }

        /// <summary>一次性设置全部接头，便于 LabVIEW 传入 DBL 数组。</summary>
        public void SetConnectors(double[] positions)
        {
            var replacement = new List<ConnectorMark>();
            if (positions != null)
            {
                for (int i = 0; i < positions.Length; i++)
                {
                    double position = positions[i];
                    if (!IsFinite(position))
                        throw new ArgumentOutOfRangeException(nameof(positions), "接头时刻数组不能包含 NaN 或无穷值。");
                    double distance = TimeToDistance(position);
                    if (replacement.Any(mark => DistancesEqual(GetConnectorDistance(mark), distance)))
                        throw new ArgumentException("接头时刻数组不能包含重复位置。", nameof(positions));
                    replacement.Add(ConnectorMark.FromTime(position, null));
                }
            }
            connectors.Clear();
            connectors.AddRange(replacement);
            ConnectorCollectionChanged();
        }

        /// <summary>
        /// 用距测试端的米制位置设置接头。null 等同清空；NaN、无穷、越界或重复输入会抛出异常，
        /// 并保持原接头集合不变。
        /// </summary>
        public void SetCablePositions(double[] positionsMeters)
        {
            var replacement = new List<ConnectorMark>();
            if (positionsMeters != null)
            {
                var validated = new List<double>(positionsMeters.Length);
                for (int i = 0; i < positionsMeters.Length; i++)
                {
                    double distance = positionsMeters[i];
                    ValidateCableDistance(distance, nameof(positionsMeters));
                    if (validated.Any(existing => DistancesEqual(existing, distance)))
                        throw new ArgumentException("电缆位置数组不能包含重复位置。", nameof(positionsMeters));
                    validated.Add(distance);
                }
                validated.Sort();
                replacement.AddRange(validated.Select(distance => ConnectorMark.FromDistance(distance)));
            }

            connectors.Clear();
            connectors.AddRange(replacement);
            ConnectorCollectionChanged();
        }

        /// <summary>返回按实时距测试端米数升序排列的接头位置。</summary>
        public double[] GetCablePositionsMeters()
        {
            return GetOrderedConnectors().Select(item => item.DistanceMeters).ToArray();
        }

        /// <summary>返回与 GetCablePositionsMeters 一一对应的 X 轴时刻。</summary>
        public double[] GetConnectorTimePositions()
        {
            return GetOrderedConnectors().Select(item => item.TimePosition).ToArray();
        }

        /// <summary>
        /// 按时刻增加接头。入射前、超出电缆、NaN/无穷或重复时返回 false，绝不钳位输入。
        /// </summary>
        public bool AddConnectorAtTime(double timePosition)
        {
            if (!TryTimeToValidCableDistance(timePosition, out double distance)) return false;
            if (HasDuplicateDistance(distance)) return false;
            connectors.Add(ConnectorMark.FromTime(timePosition, null));
            ConnectorCollectionChanged();
            return true;
        }

        /// <summary>按当前米制排序后的 1-based 编号删除接头；无效编号返回 false。</summary>
        public bool RemoveConnectorByNumber(int connectorNumber)
        {
            if (connectorNumber < 1 || connectorNumber > connectors.Count) return false;
            ConnectorSnapshot target = GetOrderedConnectors()[connectorNumber - 1];
            connectors.Remove(target.Mark);
            ConnectorCollectionChanged();
            return true;
        }

        public void ClearConnectors()
        {
            connectors.Clear();
            ConnectorCollectionChanged();
        }

        public void ResetView()
        {
            FitPulseView();
        }

        public void SetLanguage(string code)
        {
            languageCode = !string.IsNullOrWhiteSpace(code) &&
                           code.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "en-US"
                : "zh-CN";

            if (exportButton != null) exportButton.UpdateLanguage();
            SafeInvalidate();
        }

        public void SavePng(string filePath, int width, int height)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = Path.Combine(Path.GetTempPath(), "Calibration.png");
            filePath = Path.GetFullPath(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            if (width < 100 || height < 100) throw new ArgumentOutOfRangeException("图片尺寸过小。");

            using (var bitmap = new Bitmap(width, height))
            {
                var oldSize = Size;
                try
                {
                    Size = new Size(width, height);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                        DrawChart(graphics, new Rectangle(0, 0, width, height));
                    bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                }
                finally
                {
                    Size = oldSize;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawChart(e.Graphics, ClientRectangle);
            DrawZoomSelection(e.Graphics, GetPlotRectangle(ClientRectangle));
        }

        private void DrawChart(Graphics g, Rectangle bounds)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            Rectangle plot = GetPlotRectangle(bounds);
            if (plot.Width < 20 || plot.Height < 20) return;

            double[] localX;
            double[] localY;
            lock (dataLock)
            {
                localX = xs;
                localY = ys;
            }

            if (autoScaleY)
                GetVisibleYRange(localX, localY, out viewYMin, out viewYMax);
            double yMin = viewYMin;
            double yMax = viewYMax;
            lastPaintYMin = yMin;
            lastPaintYMax = yMax;
            DrawGridAndAxes(g, plot, yMin, yMax);
            DrawConnectors(g, plot);
            DrawWaveform(g, plot, localX, localY, yMin, yMax);
            if(ShowDecayCurve)DrawExponentialDecay(g, plot, yMin, yMax);
            DrawCursor(g, plot, incidentX, IncidentCaption, IncidentCursorColor, yMin, yMax, 0);
            DrawCursor(g, plot, reflectionX, ReflectionCaption, ReflectionCursorColor, yMin, yMax, 1);
            DrawInformationCard(g, plot, localX, localY);
            DrawTitles(g, plot, bounds);
        }

        private Rectangle GetPlotRectangle(Rectangle bounds)
        {
            float scale = Math.Max(0.68f, Math.Min(1f, Math.Min(bounds.Width / 900f, bounds.Height / 480f)));
            int left = Math.Max(82, (int)Math.Round(PlotLeft * scale));
            int top = Math.Max(40, (int)Math.Round(PlotTop * scale + 12));
            int right = Math.Max(14, (int)Math.Round(PlotRight * scale));
            int bottom = Math.Max(50, (int)Math.Round(PlotBottom * scale));
            return Rectangle.FromLTRB(
                bounds.Left + left,
                bounds.Top + top,
                Math.Max(bounds.Left + left + 20, bounds.Right - right),
                Math.Max(bounds.Top + top + 20, bounds.Bottom - bottom));
        }

        private void DrawGridAndAxes(Graphics g, Rectangle plot, double yMin, double yMax)
        {
            using (var panelBrush = new SolidBrush(BrowserTheme.Surface)) g.FillRectangle(panelBrush, plot);
            // Physical boundary follows the current incident cursor and stored L/v,
            // not the adjustable reflection cursor. X uses microseconds here.
            double boundary=CableBoundaryX;
            double begin=Math.Max(viewXMin,incidentX),end=Math.Min(viewXMax,boundary);
            if(IsFinite(boundary)&&cableLengthMeters>0&&propagationVelocityMPerUs>0&&end>begin)
                using(var shade=new SolidBrush(Color.FromArgb(22,BrowserTheme.Cyan)))
                    g.FillRectangle(shade,XToPixel(begin,plot),plot.Top,XToPixel(end,plot)-XToPixel(begin,plot),plot.Height);
            using (var gridPen = new Pen(BrowserTheme.Grid, 1f) { DashStyle = DashStyle.Dot })
            using (var axisPen = new Pen(BrowserTheme.Muted, 1.2f))
            using (var labelBrush = new SolidBrush(BrowserTheme.Muted))
            {
                // Horizontal and vertical tick density depend on their own available
                // span, so reducing the viewport never squeezes labels together.
                int xDivisions = Math.Max(2, Math.Min(8, plot.Width / 88));
                int yDivisions = Math.Max(2, Math.Min(8, plot.Height / 40));
                for (int i = 0; i <= xDivisions; i++)
                {
                    float px = plot.Left + plot.Width * i / (float)xDivisions;
                    g.DrawLine(gridPen, px, plot.Top, px, plot.Bottom);
                    double xv = viewXMin + (viewXMax - viewXMin) * i / xDivisions;
                    string label = FormatNumber(xv);
                    SizeF size = g.MeasureString(label, Font);
                    g.DrawString(label, Font, labelBrush, px - size.Width / 2, plot.Bottom + 6);
                }
                for (int i = 0; i <= yDivisions; i++)
                {
                    float py = plot.Top + plot.Height * i / (float)yDivisions;
                    g.DrawLine(gridPen, plot.Left, py, plot.Right, py);
                    double yv = yMax - (yMax - yMin) * i / yDivisions;
                    string label = FormatNumber(yv);
                    SizeF size = g.MeasureString(label, Font);
                    g.DrawString(label, Font, labelBrush,
                        plot.Left - size.Width - 10, py - size.Height / 2);
                }
                g.DrawRectangle(axisPen, plot);
            }
        }

        private void DrawWaveform(Graphics g, Rectangle plot, double[] localX, double[] localY, double yMin, double yMax)
        {
            if (localX.Length < 2) return;
            int stride = Math.Max(1, localX.Length / Math.Max(1, plot.Width * 3));
            var points = new List<PointF>(Math.Min(localX.Length, plot.Width * 3 + 2));

            for (int i = 0; i < localX.Length; i += stride)
            {
                if (!IsFinite(localX[i]) || !IsFinite(localY[i])) continue;
                if (localX[i] < viewXMin || localX[i] > viewXMax) continue;
                points.Add(new PointF(XToPixel(localX[i], plot), YToPixel(localY[i], plot, yMin, yMax)));
            }
            int last = localX.Length - 1;
            if (IsFinite(localX[last]) && IsFinite(localY[last]) && localX[last] >= viewXMin && localX[last] <= viewXMax)
                points.Add(new PointF(XToPixel(localX[last], plot), YToPixel(localY[last], plot, yMin, yMax)));

            if (points.Count < 2) return;
            using (var line = new Pen(WaveformColor, 1f) { LineJoin = LineJoin.Round })
            {
                g.SetClip(plot);

                g.DrawLines(line, points.ToArray());
                g.ResetClip();
            }
        }

        private void DrawCursor(
            Graphics g,
            Rectangle plot,
            double x,
            string caption,
            Color color,
            double yMin,
            double yMax,
            int compactRow)
        {
            if (x < viewXMin || x > viewXMax) return;
            float px = XToPixel(x, plot);
            double y = InterpolateYAt(x);
            float py = YToPixel(y, plot, yMin, yMax);

            using (var cursorPen = new Pen(color, 1.5f) { DashStyle = DashStyle.Dash })
            using (var fill = new SolidBrush(color))
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawLine(cursorPen, px, plot.Top, px, plot.Bottom);

                PointF[] handle =
                {
                    new PointF(px - 7, plot.Top), new PointF(px + 7, plot.Top), new PointF(px, plot.Top + 10)
                };
                g.FillPolygon(fill, handle);
                g.FillEllipse(fill, px - 5.5f, py - 5.5f, 11f, 11f);
                g.FillEllipse(white, px - 2f, py - 2f, 4f, 4f);

                double distance = TimeToDistance(x);
                string text = caption + "  " + FormatNumber(x) + " μs  " +
                              FormatDistance(distance) + " m";
                SizeF size = g.MeasureString(text, Font);
                float tagX = Clamp(px - size.Width / 2 - 7, plot.Left, plot.Right - size.Width - 14);
                bool compact = plot.Width < 520 || plot.Height < 250;
                double otherX = compactRow == 0 ? reflectionX : incidentX;
                string otherCaption = compactRow == 0 ? ReflectionCaption : IncidentCaption;
                string otherText = otherCaption + "  " + FormatNumber(otherX) + " μs  " +
                                   FormatDistance(TimeToDistance(otherX)) + " m";
                SizeF otherSize = g.MeasureString(otherText, Font);
                bool tagCollision = Math.Abs(px - XToPixel(otherX, plot)) <
                    (size.Width + otherSize.Width) / 2f + 12;
                float tagY = plot.Top + 12 +
                    (compact || tagCollision ? compactRow * (size.Height + 7) : 0);
                var tag = new RectangleF(tagX, tagY, size.Width + 14, size.Height + 6);
                FillRoundedRectangle(g, fill, tag, 5);
                g.DrawString(text, Font, white, tag.Left + 7, tag.Top + 3);
            }
        }

        private void DrawConnectors(Graphics g, Rectangle plot)
        {
            using (var pen = new Pen(Color.FromArgb(130, BrowserTheme.Muted), 1f) { DashStyle = DashStyle.Dot })
            using (var fill = new SolidBrush(BrowserTheme.Muted))
            using (var textBrush = new SolidBrush(BrowserTheme.Ink))
            using (var labelBackground = new SolidBrush(Color.FromArgb(224, BrowserTheme.Surface)))
            using (var labelBorder = new Pen(Color.FromArgb(140, BrowserTheme.Border), 1f))
            {
                List<ConnectorSnapshot> ordered = GetOrderedConnectors();
                var occupiedLabels = new List<RectangleF>();
                bool compactLabels = plot.Width < 650 || plot.Height < 300;
                for (int i = 0; i < ordered.Count; i++)
                {
                    ConnectorSnapshot connector = ordered[i];
                    if (connector.TimePosition < viewXMin || connector.TimePosition > viewXMax) continue;
                    float px = XToPixel(connector.TimePosition, plot);
                    g.DrawLine(pen, px, plot.Top, px, plot.Bottom);
                    PointF[] diamond =
                    {
                        new PointF(px, plot.Bottom - 9), new PointF(px + 7, plot.Bottom),
                        new PointF(px, plot.Bottom + 9), new PointF(px - 7, plot.Bottom)
                    };
                    g.FillPolygon(fill, diamond);
                    string label = BuildConnectorDisplayLabel(connector, i + 1, compactLabels);
                    SizeF s = g.MeasureString(label, Font);
                    RectangleF labelRectangle = FindConnectorLabelRectangle(
                        plot, px, new SizeF(s.Width + 8, s.Height + 4), occupiedLabels);
                    occupiedLabels.Add(labelRectangle);
                    FillRoundedRectangle(g, labelBackground, labelRectangle, 3);
                    DrawRoundedRectangle(g, labelBorder, labelRectangle, 3);
                    g.DrawString(label, Font, textBrush, labelRectangle.Left + 4, labelRectangle.Top + 2);
                }
            }
        }

        private RectangleF FindConnectorLabelRectangle(
            Rectangle plot,
            float anchorX,
            SizeF labelSize,
            List<RectangleF> occupiedLabels)
        {
            float width = Math.Min(labelSize.Width, Math.Max(24, plot.Width - 4));
            float height = labelSize.Height;
            float centeredX = Clamp(anchorX - width / 2f, plot.Left + 2, Math.Max(plot.Left + 2, plot.Right - width - 2));
            float baseY = Math.Max(plot.Top + 2, plot.Bottom - height - 15);
            float layerStep = height + 3;
            int maximumLayers = Math.Max(1, Math.Min(6,
                (int)Math.Floor(Math.Max(height, plot.Height - 18) / Math.Max(1f, layerStep))));

            for (int layer = 0; layer < maximumLayers; layer++)
            {
                float y = Math.Max(plot.Top + 2, baseY - layer * layerStep);
                var candidate = new RectangleF(centeredX, y, width, height);
                if (!IntersectsAnyLabel(candidate, occupiedLabels)) return candidate;
            }

            // 极密集接头在所有纵向层均占用时，再向左右错位寻找空位。
            for (int offsetIndex = 1; offsetIndex <= 6; offsetIndex++)
            {
                float direction = offsetIndex % 2 == 1 ? -1f : 1f;
                float offset = (offsetIndex + 1) / 2 * Math.Max(12f, width * .55f) * direction;
                float x = Clamp(centeredX + offset, plot.Left + 2, Math.Max(plot.Left + 2, plot.Right - width - 2));
                for (int layer = 0; layer < maximumLayers; layer++)
                {
                    float y = Math.Max(plot.Top + 2, baseY - layer * layerStep);
                    var candidate = new RectangleF(x, y, width, height);
                    if (!IntersectsAnyLabel(candidate, occupiedLabels)) return candidate;
                }
            }

            // 最后仍保持在图内；正常三接头场景不会进入此分支。
            int fallbackLayer = occupiedLabels.Count % maximumLayers;
            return new RectangleF(centeredX,
                Math.Max(plot.Top + 2, baseY - fallbackLayer * layerStep), width, height);
        }

        private static bool IntersectsAnyLabel(RectangleF candidate, List<RectangleF> occupiedLabels)
        {
            var padded = candidate;
            padded.Inflate(2f, 1f);
            for (int i = 0; i < occupiedLabels.Count; i++)
                if (padded.IntersectsWith(occupiedLabels[i])) return true;
            return false;
        }

        private void DrawInformationCard(Graphics g, Rectangle plot, double[] localX, double[] localY)
        {
            bool compact = plot.Width < 520 || plot.Height < 250;
            if (compact)
            {
                // A card inside a short plot hides the waveform. Keep the delay in
                // the title strip; incident/reflection labels remain at the cursors.
                string delay = Text("时间差 Δt", "Time Difference Δt") + "  " + FormatNumber(TimeDifference) + " μs";
                using (var labelFont = new Font(Font, FontStyle.Bold))
                using (var labelBrush = new SolidBrush(ForeColor))
                {
                    SizeF size = g.MeasureString(delay, labelFont);
                    if (plot.Width > size.Width + 120)
                        g.DrawString(delay, labelFont, labelBrush, plot.Right - size.Width - 3, Math.Max(0, plot.Top - size.Height - 2));
                }
                return;
            }
            float width = compact ? Math.Min(238, plot.Width * .62f) : (languageCode == "en-US" ? 310 : 246);
            float height = compact ? (ShowCalculatedDistance ? 66 : 45) : (ShowCalculatedDistance ? 132 : 108);
            // The calibration summary sits in the upper-right corner; a thin
            // outline keeps the waveform visible under the information area.
            float top = plot.Top + 8;
            float availableHeight = Math.Max(24, plot.Bottom - top - 6);
            var card = new RectangleF(Math.Max(plot.Left + 6, plot.Right - width - 12), top,
                Math.Min(width, plot.Width - 12), Math.Min(height, availableHeight));
            using (var border = new Pen(Color.FromArgb(180, BrowserTheme.Border), 1f))
            using (var titleBrush = new SolidBrush(BrowserTheme.Ink))
            using (var valueBrush = new SolidBrush(BrowserTheme.Primary))
            using (var titleFont = new Font(Font, FontStyle.Bold))
            {
                DrawRoundedRectangle(g, border, card, 9);

                float x = card.Left + 10;
                float y = card.Top + 7;
                if (!compact)
                {
                    g.DrawString(Text("校准信息", "Calibration Information"), titleFont, titleBrush, x, y);
                    y += 26;
                    DrawInfoRow(g, IncidentCaption, FormatNumber(incidentX), x, y, card.Width - 20, titleBrush, valueBrush);
                    y += 23;
                    DrawInfoRow(g, ReflectionCaption, FormatNumber(reflectionX), x, y, card.Width - 20, titleBrush, valueBrush);
                    y += 23;
                }
                DrawInfoRow(g, Text("时间差 Δt", "Time Difference Δt"), FormatNumber(TimeDifference) + " μs", x, y, card.Width - 20, titleBrush, valueBrush);
                if (ShowCalculatedDistance)
                {
                    y += compact ? 21 : 23;
                    DrawInfoRow(g, Text("计算距离", "Calculated Distance"), FormatNumber(CalculatedDistance) + " m", x, y, card.Width - 20, titleBrush, valueBrush);
                }
            }
        }

        private void DrawTitles(Graphics g, Rectangle plot, Rectangle bounds)
        {
            using (var brush = new SolidBrush(ForeColor))
            using (var titleFont = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold))
            {
                SizeF titleSize = g.MeasureString(ChartTitle, titleFont);
                g.DrawString(ChartTitle, titleFont, brush, plot.Left, Math.Max(bounds.Top + 2, plot.Top - titleSize.Height - 2));

                SizeF xSize = g.MeasureString(XAxisTitle, Font);
                g.DrawString(XAxisTitle, Font, brush, plot.Left + (plot.Width - xSize.Width) / 2, Math.Min(bounds.Bottom - xSize.Height - 2, plot.Bottom + 27));

                GraphicsState state = g.Save();
                g.TranslateTransform(Math.Max(bounds.Left + 8, plot.Left - 78), plot.Top + plot.Height / 2f);
                g.RotateTransform(-90);
                SizeF ySize = g.MeasureString(YAxisTitle, Font);
                g.DrawString(YAxisTitle, Font, brush, -ySize.Width / 2, 0);
                g.Restore(state);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (!plot.Contains(e.Location)) return;

            // 右键完全交给 LabVIEW 运行时菜单以及外部 MouseDown 复位处理。
            // 此处不得 Capture，否则会吞掉运行时快捷菜单事件。
            if (e.Button == MouseButtons.Right) return;
            if (e.Button != MouseButtons.Left) return;

            float incidentPixel = XToPixel(incidentX, plot);
            float reflectionPixel = XToPixel(reflectionX, plot);
            if (AllowCursorEditing && Math.Abs(e.X - incidentPixel) <= CursorHitWidth)
                dragTarget = DragTarget.Incident;
            else if (AllowCursorEditing && Math.Abs(e.X - reflectionPixel) <= CursorHitWidth)
                dragTarget = DragTarget.Reflection;
            else
            {
                dragTarget = DragTarget.ZoomSelection;
                selectionStart = e.Location;
                selectionEnd = e.Location;
            }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (plot.Width <= 0) return;

            if (dragTarget == DragTarget.Incident || dragTarget == DragTarget.Reflection)
            {
                double x = Clamp(PixelToX(e.X, plot), dataXMin, dataXMax);
                bool incidentChanged = false;
                if (dragTarget == DragTarget.Incident)
                {
                    incidentChanged = !NearlyEqual(incidentX, x);
                    incidentX = x;
                }
                else reflectionX = x;
                Invalidate();
                CursorChanged?.Invoke(this, EventArgs.Empty);
                if (incidentChanged) ConnectorGeometryChanged();
            }
            else if (dragTarget == DragTarget.ZoomSelection)
            {
                selectionEnd = new Point(
                    (int)Clamp(e.X, plot.Left, plot.Right),
                    (int)Clamp(e.Y, plot.Top, plot.Bottom));
                Invalidate();
            }
            else
            {
                float pi = XToPixel(incidentX, plot);
                float pr = XToPixel(reflectionX, plot);
                Cursor = Math.Abs(e.X - pi) <= CursorHitWidth || Math.Abs(e.X - pr) <= CursorHitWidth
                    ? Cursors.SizeWE
                    : Cursors.Cross;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (dragTarget == DragTarget.ZoomSelection)
            {
                Rectangle selection = NormalizedRectangle(selectionStart, selectionEnd);
                if (selection.Width >= 8 && selection.Height >= 8)
                {
                    double x1 = PixelToX(selection.Left, plot);
                    double x2 = PixelToX(selection.Right, plot);
                    SetViewClamped(Math.Min(x1, x2), Math.Max(x1, x2));

                    double yTop = PixelToY(selection.Top, plot, lastPaintYMin, lastPaintYMax);
                    double yBottom = PixelToY(selection.Bottom, plot, lastPaintYMin, lastPaintYMax);
                    viewYMin = Math.Min(yTop, yBottom);
                    viewYMax = Math.Max(yTop, yBottom);
                    autoScaleY = false;
                }
            }
            dragTarget = DragTarget.None;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        { base.OnMouseWheel(e); ApplyWheelZoom(e, (ModifierKeys & Keys.Shift) != 0); }
        internal void ApplyWheelZoom(MouseEventArgs e, bool vertical)
        {
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (!plot.Contains(e.Location)) return;

            double factor = e.Delta > 0 ? 0.82 : 1.22;
            if (vertical)
            {
                double anchor = PixelToY(e.Y, plot, lastPaintYMin, lastPaintYMax);
                viewYMin = anchor - (anchor - lastPaintYMin) * factor;
                viewYMax = anchor + (lastPaintYMax - anchor) * factor;
                autoScaleY = false;
            }
            else
            {
                // Horizontal zoom must retain the Y scale instead of autoscaling
                // to whichever peak remains inside the new horizontal window.
                viewYMin = lastPaintYMin; viewYMax = lastPaintYMax; autoScaleY = false;
                double anchor = PixelToX(e.X, plot);
                double newMin = anchor - (anchor - viewXMin) * factor;
                double newMax = anchor + (viewXMax - anchor) * factor;
                SetViewClamped(newMin, newMax);
            }
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            Rectangle plot = GetPlotRectangle(ClientRectangle);
            if (e.Button == MouseButtons.Left && plot.Contains(e.Location))
            {
                double timePosition = Clamp(PixelToX(e.X, plot), dataXMin, dataXMax);
                if (UseExternalConnectorDialog)
                {
                    if (timePosition > incidentX && timePosition < reflectionX)
                    {
                        RequestedConnectorTime = timePosition;
                        ConnectorDialogRequested?.Invoke(this, EventArgs.Empty);
                    }
                    return;
                }
                if (TryTimeToValidCableDistance(timePosition, out _))
                    ShowAddConnectorDialog(timePosition);
                else
                    System.Media.SystemSounds.Beep.Play();
            }
        }

        private void SetViewClamped(double min, double max)
        {
            double dataSpan = Math.Max(1e-12, dataXMax - dataXMin);
            double minSpan = dataSpan / 10000.0;
            double span = Clamp(max - min, minSpan, dataSpan);
            double center = (min + max) / 2.0;
            min = center - span / 2.0;
            max = center + span / 2.0;
            if (min < dataXMin) { max += dataXMin - min; min = dataXMin; }
            if (max > dataXMax) { min -= max - dataXMax; max = dataXMax; }
            viewXMin = Math.Max(dataXMin, min);
            viewXMax = Math.Min(dataXMax, max);
        }

        private void DrawZoomSelection(Graphics g, Rectangle plot)
        {
            if (dragTarget != DragTarget.ZoomSelection) return;
            Rectangle rectangle = NormalizedRectangle(selectionStart, selectionEnd);
            rectangle.Intersect(plot);
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;

            using (var fill = new SolidBrush(Color.FromArgb(42, WaveformColor)))
            using (var pen = new Pen(Color.FromArgb(210, WaveformColor), 1.3f) { DashStyle = DashStyle.Dash })
            {
                g.FillRectangle(fill, rectangle);
                g.DrawRectangle(pen, rectangle);
            }
        }

        private void ShowAddConnectorDialog(double timePosition)
        {
            if (!TryTimeToValidCableDistance(timePosition, out double distanceMeters)) return;
            if (HasDuplicateDistance(distanceMeters))
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            using (var dialog = new Form())
            using (var okButton = new Button())
            using (var cancelButton = new Button())
            {
                dialog.Text = Text("添加接头", "Add Connector");
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.ClientSize = new Size(360, 154);
                dialog.Font = Font;

                var timeLabel = new Label
                {
                    AutoSize = true,
                    Location = new Point(18, 18),
                    Text = Text("时刻：", "Time: ") + FormatNumber(timePosition) + " μs"
                };
                var distanceLabel = new Label
                {
                    AutoSize = true,
                    Location = new Point(18, 55),
                    Text = Text("距测试端：", "Distance from test end: ") +
                           FormatDistance(distanceMeters) + " m"
                };

                okButton.Text = Text("确定", "OK");
                okButton.DialogResult = DialogResult.OK;
                okButton.Location = new Point(178, 103);
                okButton.Size = new Size(76, 30);

                cancelButton.Text = Text("取消", "Cancel");
                cancelButton.DialogResult = DialogResult.Cancel;
                cancelButton.Location = new Point(262, 103);
                cancelButton.Size = new Size(76, 30);

                dialog.Controls.Add(timeLabel);
                dialog.Controls.Add(distanceLabel);
                dialog.Controls.Add(okButton);
                dialog.Controls.Add(cancelButton);
                dialog.AcceptButton = okButton;
                dialog.CancelButton = cancelButton;

                IWin32Window owner = FindForm();
                DialogResult result = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                if (result == DialogResult.OK)
                    AddConnectorAtTime(timePosition);
            }
        }

        private static Rectangle NormalizedRectangle(Point a, Point b)
        {
            return Rectangle.FromLTRB(
                Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        private void GetVisibleYRange(double[] localX, double[] localY, out double min, out double max)
        {
            min = double.PositiveInfinity;
            max = double.NegativeInfinity;
            for (int i = 0; i < localX.Length; i++)
            {
                if (localX[i] < viewXMin || localX[i] > viewXMax || !IsFinite(localY[i])) continue;
                if (localY[i] < min) min = localY[i];
                if (localY[i] > max) max = localY[i];
            }
            if (!IsFinite(min) || !IsFinite(max)) { min = -1; max = 1; }
            if (Math.Abs(max - min) < 1e-15) { min -= 1; max += 1; }
            double margin = (max - min) * 0.10;
            min -= margin;
            max += margin;
        }

        private double InterpolateYAt(double x)
        {
            lock (dataLock)
            {
                if (xs.Length == 0) return 0;
                int index = Array.BinarySearch(xs, x);
                if (index >= 0) return ys[index];
                index = ~index;
                if (index <= 0) return ys[0];
                if (index >= xs.Length) return ys[ys.Length - 1];
                double span = xs[index] - xs[index - 1];
                if (Math.Abs(span) < 1e-20) return ys[index];
                double t = (x - xs[index - 1]) / span;
                return ys[index - 1] + (ys[index] - ys[index - 1]) * t;
            }
        }

        private double TimeToDistance(double timePosition)
        {
            return (timePosition - incidentX) * propagationVelocityMPerUs / 2.0;
        }

        private double DistanceToTime(double distanceMeters)
        {
            return incidentX + 2.0 * distanceMeters / propagationVelocityMPerUs;
        }

        private bool TryTimeToValidCableDistance(double timePosition, out double distanceMeters)
        {
            distanceMeters = double.NaN;
            if (!IsFinite(timePosition) || !IsFinite(propagationVelocityMPerUs) ||
                propagationVelocityMPerUs <= 0 || !IsFinite(cableLengthMeters) || cableLengthMeters <= 0)
                return false;

            double candidate = TimeToDistance(timePosition);
            if (!IsFinite(candidate)) return false;
            double boundaryTolerance = Math.Max(1e-10, cableLengthMeters * 1e-12);
            if (candidate < -boundaryTolerance || candidate > cableLengthMeters + boundaryTolerance) return false;
            if (Math.Abs(candidate) <= boundaryTolerance) distanceMeters = 0;
            else if (Math.Abs(candidate - cableLengthMeters) <= boundaryTolerance) distanceMeters = cableLengthMeters;
            else distanceMeters = candidate;
            return true;
        }

        private static void ValidateCableParameters(double propagationVelocity, double cableLength)
        {
            if (!IsFinite(propagationVelocity) || propagationVelocity <= 0)
                throw new ArgumentOutOfRangeException(nameof(propagationVelocity),
                    "传播速度必须是大于 0 的有限数值。");
            if (!IsFinite(cableLength) || cableLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(cableLength),
                    "电缆长度必须是大于 0 的有限数值。");
        }

        private void ValidateCableDistance(double distanceMeters, string parameterName)
        {
            if (!IsFinite(distanceMeters))
                throw new ArgumentOutOfRangeException(parameterName, "电缆位置不能包含 NaN 或无穷值。");
            if (distanceMeters < 0 || distanceMeters > cableLengthMeters)
                throw new ArgumentOutOfRangeException(parameterName,
                    "电缆位置必须位于 0 m 到电缆长度之间；输入不会被静默钳位。");
        }

        private double GetConnectorDistance(ConnectorMark mark)
        {
            return mark.Anchor == ConnectorAnchor.Distance
                ? mark.AnchorValue
                : TimeToDistance(mark.AnchorValue);
        }

        private double GetConnectorTime(ConnectorMark mark)
        {
            return mark.Anchor == ConnectorAnchor.Time
                ? mark.AnchorValue
                : DistanceToTime(mark.AnchorValue);
        }

        private List<ConnectorSnapshot> GetOrderedConnectors()
        {
            var ordered = new List<ConnectorSnapshot>(connectors.Count);
            foreach (ConnectorMark mark in connectors)
                ordered.Add(new ConnectorSnapshot(mark, GetConnectorTime(mark), GetConnectorDistance(mark)));

            ordered.Sort((left, right) =>
            {
                int distanceOrder = left.DistanceMeters.CompareTo(right.DistanceMeters);
                return distanceOrder != 0
                    ? distanceOrder
                    : left.TimePosition.CompareTo(right.TimePosition);
            });
            return ordered;
        }

        private bool HasDuplicateDistance(double distanceMeters)
        {
            return connectors.Any(mark => DistancesEqual(GetConnectorDistance(mark), distanceMeters));
        }

        private bool DistancesEqual(double left, double right)
        {
            double tolerance = Math.Max(1e-9, cableLengthMeters * 1e-9);
            return Math.Abs(left - right) <= tolerance;
        }

        private string BuildConnectorDisplayLabel(ConnectorSnapshot connector, int number, bool compact)
        {
            string text;
            if (compact)
            {
                text = Text("接", "C") + number.ToString(CultureInfo.InvariantCulture) + "  " +
                       FormatDistance(connector.DistanceMeters) + " m";
            }
            else
            {
                text = Text("接头 ", "Connector ") +
                       number.ToString(CultureInfo.InvariantCulture) + "  " +
                       FormatDistance(connector.DistanceMeters) + " m";
            }
            if (!compact && !string.IsNullOrWhiteSpace(connector.Mark.CustomLabel))
                text += "  " + connector.Mark.CustomLabel;
            return text;
        }

        private void ConnectorCollectionChanged()
        {
            ConnectorGeometryChanged();
        }

        private void ConnectorGeometryChanged()
        {
            unchecked { connectorSequence++; }
            SafeInvalidate();
            ConnectorsChanged?.Invoke(this,
                new ConnectorsChangedEventArgs(connectorSequence, connectors.Count));
        }

        private float XToPixel(double x, Rectangle plot)
        {
            return plot.Left + (float)((x - viewXMin) / Math.Max(1e-20, viewXMax - viewXMin) * plot.Width);
        }

        private double PixelToX(float px, Rectangle plot)
        {
            return viewXMin + (px - plot.Left) / Math.Max(1.0, plot.Width) * (viewXMax - viewXMin);
        }

        private static float YToPixel(double y, Rectangle plot, double yMin, double yMax)
        {
            return plot.Bottom - (float)((y - yMin) / Math.Max(1e-20, yMax - yMin) * plot.Height);
        }

        private static double PixelToY(float py, Rectangle plot, double yMin, double yMax)
        {
            return yMax - (py - plot.Top) / Math.Max(1.0, plot.Height) * (yMax - yMin);
        }

        private void SafeInvalidate()
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(new Action(Invalidate));
            else Invalidate();
        }

        private static void DrawInfoRow(Graphics g, string name, string value, float x, float y, float width, Brush nameBrush, Brush valueBrush)
        {
            using (var infoFont = new Font("Arial", 9F, FontStyle.Regular, GraphicsUnit.Point))
            {
                g.DrawString(name, infoFont, nameBrush, x, y);
                SizeF size = g.MeasureString(value, infoFont);
                g.DrawString(value, infoFont, valueBrush, x + width - size.Width, y);
            }
        }

        private static void FillRoundedRectangle(Graphics g, Brush brush, RectangleF rectangle, float radius)
        {
            using (GraphicsPath path = CreateRoundedRectangle(rectangle, radius)) g.FillPath(brush, path);
        }

        private static void DrawRoundedRectangle(Graphics g, Pen pen, RectangleF rectangle, float radius)
        {
            using (GraphicsPath path = CreateRoundedRectangle(rectangle, radius)) g.DrawPath(pen, path);
        }

        private static GraphicsPath CreateRoundedRectangle(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool NearlyEqual(double left, double right)
        {
            double scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
            return Math.Abs(left - right) <= scale * 1e-12;
        }
        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));

        // Host applications can use the same language catalog as their surrounding UI.
        // Standalone LabVIEW callers keep the existing Chinese/English behavior.
        [Browsable(false)] public Func<string, string, string> TextTranslator { get; set; }
        private string Text(string chinese, string english)
        {
            return TextTranslator == null ? (languageCode == "en-US" ? english : chinese) : TextTranslator(chinese, english);
        }

        private static string FormatNumber(double value)
        {
            double abs = Math.Abs(value);
            if ((abs >= 100000 || (abs > 0 && abs < 0.001))) return value.ToString("0.###E+0", CultureInfo.InvariantCulture);
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatDistance(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private sealed class ConnectorMark
        {
            private ConnectorMark(ConnectorAnchor anchor, double anchorValue, string customLabel)
            {
                Anchor = anchor;
                AnchorValue = anchorValue;
                CustomLabel = customLabel;
            }

            public static ConnectorMark FromTime(double timePosition, string customLabel)
            {
                return new ConnectorMark(ConnectorAnchor.Time, timePosition, customLabel);
            }

            public static ConnectorMark FromDistance(double distanceMeters)
            {
                return new ConnectorMark(ConnectorAnchor.Distance, distanceMeters, null);
            }

            public ConnectorAnchor Anchor { get; }
            public double AnchorValue { get; }
            public string CustomLabel { get; }
        }

        private sealed class ConnectorSnapshot
        {
            public ConnectorSnapshot(ConnectorMark mark, double timePosition, double distanceMeters)
            {
                Mark = mark;
                TimePosition = timePosition;
                DistanceMeters = distanceMeters;
            }

            public ConnectorMark Mark { get; }
            public double TimePosition { get; }
            public double DistanceMeters { get; }
        }

        private enum ConnectorAnchor { Time, Distance }
        private enum DragTarget { None, Incident, Reflection, ZoomSelection }
    }
}
