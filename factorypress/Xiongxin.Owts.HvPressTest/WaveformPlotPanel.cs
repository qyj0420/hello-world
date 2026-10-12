using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 轻量自绘波形区。支持单/双/三系列：左轴可叠两路（分压+源压绿），右轴一路（电流）。
    /// </summary>
    public sealed class WaveformPlotPanel : Control
    {
        readonly List<PointF> _left = new List<PointF>();
        readonly List<PointF> _mid = new List<PointF>(); // 左轴第二路：直流源电压（绿）
        readonly List<PointF> _right = new List<PointF>();
        bool _dual;
        string _title = "";
        string _subtitle = "";
        string _xLabel = "t";
        string _yLeftLabel = "V";
        string _yRightLabel = "";
        string _emptyHint = "等待数据";
        Color _leftColor = Color.FromArgb(0, 120, 180); // 分压
        Color _midColor = Color.FromArgb(40, 170, 70);   // 直流源电压·绿
        Color _rightColor = Color.FromArgb(200, 80, 40); // 电流
        float _xMin, _xMax = 1f;
        float _yLMin, _yLMax = 1f;
        float _yRMin, _yRMax = 1f;
        bool _overrangeMark;
        float _overrangeFullScaleV;
        string _overrangeBanner = "";
        /// <summary>数据全量边界（缩放基准）；交互缩放改 _x/_y 视窗。</summary>
        float _dataXMin, _dataXMax = 1f, _dataYLMin, _dataYLMax = 1f, _dataYRMin, _dataYRMax = 1f;
        bool _hasDataBounds;
        bool _zoomEnabled;
        /// <summary>右轴：超细直方/棒图，每个样点独立从 0 填到幅值（仅局放检波用；左轴振荡仍折线平滑）。</summary>
        bool _rightAsHistogramFill;
        /// <summary>右轴：双极 stem（按像素列取峰并从 0 画竖线）；定位原始局放用，禁止 DrawLines 紫带。</summary>
        bool _rightAsBipolarStem;

        public WaveformPlotPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9f);
            Padding = new Padding(8);
            MinimumSize = new Size(160, 80);
            // 随控件/分隔栏变大，绘图区变大 → 波形视觉放大（ResizeRedraw）
        }

        public string Title
        {
            get { return _title; }
            set { _title = value ?? ""; Invalidate(); }
        }

        /// <summary>标题旁/下的实测信息（如 Pico 实际采样率与位数）。</summary>
        public string Subtitle
        {
            get { return _subtitle; }
            set { _subtitle = value ?? ""; Invalidate(); }
        }

        public string XLabel
        {
            get { return _xLabel; }
            set { _xLabel = value ?? ""; Invalidate(); }
        }

        /// <summary>单轴时的 Y 标签；双轴时为左侧标签。</summary>
        public string YLabel
        {
            get { return _yLeftLabel; }
            set { _yLeftLabel = value ?? ""; Invalidate(); }
        }

        public string YLeftLabel
        {
            get { return _yLeftLabel; }
            set { _yLeftLabel = value ?? ""; Invalidate(); }
        }

        public string YRightLabel
        {
            get { return _yRightLabel; }
            set { _yRightLabel = value ?? ""; Invalidate(); }
        }

        public string EmptyHint
        {
            get { return _emptyHint; }
            set { _emptyHint = value ?? ""; Invalidate(); }
        }

        public Color TraceColor
        {
            get { return _leftColor; }
            set { _leftColor = value; Invalidate(); }
        }

        public Color LeftTraceColor
        {
            get { return _leftColor; }
            set { _leftColor = value; Invalidate(); }
        }

        public Color MidTraceColor
        {
            get { return _midColor; }
            set { _midColor = value; Invalidate(); }
        }

        public Color RightTraceColor
        {
            get { return _rightColor; }
            set { _rightColor = value; Invalidate(); }
        }

        /// <summary>滚轮缩放视窗（振荡局放可放大）；双击复位。控件变大时绘图区同步放大。</summary>
        public bool ZoomEnabled
        {
            get { return _zoomEnabled; }
            set
            {
                _zoomEnabled = value;
                this.MouseWheel -= OnPlotMouseWheel;
                this.MouseDoubleClick -= OnPlotMouseDoubleClick;
                if (_zoomEnabled)
                {
                    this.MouseWheel += OnPlotMouseWheel;
                    this.MouseDoubleClick += OnPlotMouseDoubleClick;
                }
            }
        }

        /// <summary>
        /// true：右轴画超细直方（每峰独立填到 0）；false：折线。左轴始终平滑折线。
        /// </summary>
        public bool RightAsHistogramFill
        {
            get { return _rightAsHistogramFill; }
            set { _rightAsHistogramFill = value; Invalidate(); }
        }

        /// <summary>
        /// true：右轴双极 stem（像素列峰，从 0 竖线）；与 HistogramFill 互斥优先 Stem。定位原始局放可见尖峰。
        /// </summary>
        public bool RightAsBipolarStem
        {
            get { return _rightAsBipolarStem; }
            set { _rightAsBipolarStem = value; Invalidate(); }
        }

        public void ClearSeries()
        {
            _left.Clear();
            _mid.Clear();
            _right.Clear();
            _dual = false;
            _overrangeMark = false;
            _overrangeFullScaleV = 0;
            _overrangeBanner = "";
            _xMin = 0; _xMax = 1;
            _yLMin = 0; _yLMax = 1;
            _yRMin = 0; _yRMax = 1;
            _hasDataBounds = false;
            Invalidate();
        }

        public void ResetZoom()
        {
            if (!_hasDataBounds) { Invalidate(); return; }
            _xMin = _dataXMin; _xMax = _dataXMax;
            _yLMin = _dataYLMin; _yLMax = _dataYLMax;
            _yRMin = _dataYRMin; _yRMax = _dataYRMax;
            Invalidate();
        }

        void OnPlotMouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ResetZoom();
        }

        void OnPlotMouseWheel(object sender, MouseEventArgs e)
        {
            if (!_zoomEnabled || !_hasDataBounds) return;
            // 滚轮：以指针 X 为中心缩放视窗（放大=视窗变窄，波形随窗口区域放大）
            float factor = e.Delta > 0 ? 0.8f : 1.25f;
            float xspan = Math.Max(1e-12f, _xMax - _xMin);
            // 估算 plot 区相对位置
            int leftMargin = (_yLeftLabel != null && _yLeftLabel.IndexOf("kV", StringComparison.OrdinalIgnoreCase) >= 0) ? 62 : 52;
            int titleH = Font.Height + 8;
            float plotLeft = leftMargin + 2;
            float plotW = Math.Max(10, ClientSize.Width - leftMargin - 20);
            float t = (e.X - plotLeft) / plotW;
            if (t < 0) t = 0; if (t > 1) t = 1;
            float pivot = _xMin + t * xspan;
            float newSpan = xspan * factor;
            float dataSpan = Math.Max(1e-12f, _dataXMax - _dataXMin);
            if (newSpan < dataSpan * 0.002f) newSpan = dataSpan * 0.002f;
            if (newSpan > dataSpan * 1.05f) newSpan = dataSpan * 1.05f;
            _xMin = pivot - newSpan * t;
            _xMax = _xMin + newSpan;
            if (_xMin < _dataXMin) { _xMax += _dataXMin - _xMin; _xMin = _dataXMin; }
            if (_xMax > _dataXMax) { _xMin -= _xMax - _dataXMax; _xMax = _dataXMax; }
            // Y 同步略缩放，保持「放大」感
            float yf = factor;
            ZoomAxis(ref _yLMin, ref _yLMax, _dataYLMin, _dataYLMax, yf);
            ZoomAxis(ref _yRMin, ref _yRMax, _dataYRMin, _dataYRMax, yf);
            Invalidate();
        }

        static void ZoomAxis(ref float min, ref float max, float dMin, float dMax, float factor)
        {
            float mid = (min + max) * 0.5f;
            float span = Math.Max(1e-12f, max - min) * factor;
            float dSpan = Math.Max(1e-12f, dMax - dMin);
            if (span < dSpan * 0.002f) span = dSpan * 0.002f;
            if (span > dSpan * 1.05f) { min = dMin; max = dMax; return; }
            min = mid - span * 0.5f;
            max = mid + span * 0.5f;
            if (min < dMin) { max += dMin - min; min = dMin; }
            if (max > dMax) { min -= max - dMax; max = dMax; }
        }

        void CaptureDataBounds()
        {
            _dataXMin = _xMin; _dataXMax = _xMax;
            _dataYLMin = _yLMin; _dataYLMax = _yLMax;
            _dataYRMin = _yRMin; _dataYRMax = _yRMax;
            _hasDataBounds = true;
        }

        /// <summary>右轴满量程 ±FS 虚线 + 超量程/饱和横幅（局放 Pico）。</summary>
        public void SetOverrangeMarker(bool active, float fullScaleVolts, string banner = null)
        {
            _overrangeMark = active && fullScaleVolts > 0;
            _overrangeFullScaleV = fullScaleVolts;
            _overrangeBanner = banner ?? (active ? "超量程/饱和" : "");
            Invalidate();
        }

        /// <summary>单系列（仅左轴），供升压图使用。</summary>
        public void SetSeries(IList<PointF> pts, bool autoScale = true)
        {
            _right.Clear();
            _mid.Clear();
            _dual = false;
            _left.Clear();
            if (pts == null || pts.Count == 0)
            {
                Invalidate();
                return;
            }
            CopyPts(pts, _left);
            if (autoScale)
            {
                float xmin, xmax, ymin, ymax;
                BoundsOf(_left, out xmin, out xmax, out ymin, out ymax);
                ApplyX(xmin, xmax);
                ApplyYL(ymin, ymax);
                CaptureDataBounds();
            }
            Invalidate();
        }

        public void SetSeries(IList<float> x, IList<float> y, bool autoScale = true)
        {
            if (x == null || y == null || x.Count == 0 || y.Count != x.Count)
            {
                ClearSeries();
                return;
            }
            var pts = new List<PointF>(x.Count);
            for (int i = 0; i < x.Count; i++)
                pts.Add(new PointF(x[i], y[i]));
            SetSeries(pts, autoScale);
        }

        /// <summary>
        /// 同框叠画两条曲线：left→左 Y，right→右 Y；共用 X。不分轨。
        /// </summary>
        public void SetDualSeries(IList<PointF> leftPts, IList<PointF> rightPts, bool autoScale = true)
        {
            _left.Clear();
            _mid.Clear();
            _right.Clear();
            if (leftPts != null) CopyPts(leftPts, _left);
            if (rightPts != null) CopyPts(rightPts, _right);
            _dual = _left.Count >= 2 || _right.Count >= 2;
            if (!_dual)
            {
                Invalidate();
                return;
            }
            if (autoScale)
            {
                float xmin = float.MaxValue, xmax = float.MinValue;
                if (_left.Count > 0)
                {
                    float a, b, c, d;
                    BoundsOf(_left, out a, out b, out c, out d);
                    if (a < xmin) xmin = a;
                    if (b > xmax) xmax = b;
                    ApplyYL(c, d);
                }
                else
                {
                    _yLMin = -1; _yLMax = 1;
                }
                if (_right.Count > 0)
                {
                    float a, b, c, d;
                    BoundsOf(_right, out a, out b, out c, out d);
                    if (a < xmin) xmin = a;
                    if (b > xmax) xmax = b;
                    if (_rightAsHistogramFill || _rightAsBipolarStem)
                    {
                        // 直方/stem 填到 0：Y 域必须含 0
                        if (c > 0) c = 0;
                        if (d < 0) d = 0;
                    }
                    ApplyYR(c, d);
                }
                else
                {
                    _yRMin = -1; _yRMax = 1;
                }
                if (xmin > xmax) { xmin = 0; xmax = 1; }
                ApplyX(xmin, xmax);
                CaptureDataBounds();
            }
            Invalidate();
        }

        /// <summary>
        /// 三通道同框同步：leftPts/midPts→左 Y（分压 / 源压绿），rightPts→右 Y（电流）；共用同一 X。
        /// </summary>
        public void SetTriSeries(IList<PointF> leftPts, IList<PointF> midPts, IList<PointF> rightPts, bool autoScale = true)
        {
            _left.Clear();
            _mid.Clear();
            _right.Clear();
            if (leftPts != null) CopyPts(leftPts, _left);
            if (midPts != null) CopyPts(midPts, _mid);
            if (rightPts != null) CopyPts(rightPts, _right);
            _dual = _left.Count >= 2 || _mid.Count >= 2 || _right.Count >= 2;
            if (!_dual)
            {
                Invalidate();
                return;
            }
            if (autoScale)
            {
                float xmin = float.MaxValue, xmax = float.MinValue;
                float yLmin = float.MaxValue, yLmax = float.MinValue;
                bool anyL = false;
                foreach (var series in new[] { _left, _mid })
                {
                    if (series.Count == 0) continue;
                    float a, b, c, d;
                    BoundsOf(series, out a, out b, out c, out d);
                    if (a < xmin) xmin = a;
                    if (b > xmax) xmax = b;
                    if (c < yLmin) yLmin = c;
                    if (d > yLmax) yLmax = d;
                    anyL = true;
                }
                if (anyL) ApplyYL(yLmin, yLmax);
                else { _yLMin = -1; _yLMax = 1; }

                if (_right.Count > 0)
                {
                    float a, b, c, d;
                    BoundsOf(_right, out a, out b, out c, out d);
                    if (a < xmin) xmin = a;
                    if (b > xmax) xmax = b;
                    ApplyYR(c, d);
                }
                else { _yRMin = -1; _yRMax = 1; }

                if (xmin > xmax) { xmin = 0; xmax = 1; }
                ApplyX(xmin, xmax);
                CaptureDataBounds();
            }
            Invalidate();
        }

        static void CopyPts(IList<PointF> src, List<PointF> dst)
        {
            for (int i = 0; i < src.Count; i++)
                dst.Add(src[i]);
        }

        static void BoundsOf(List<PointF> pts, out float xmin, out float xmax, out float ymin, out float ymax)
        {
            xmin = xmax = pts[0].X;
            ymin = ymax = pts[0].Y;
            for (int i = 1; i < pts.Count; i++)
            {
                PointF p = pts[i];
                if (p.X < xmin) xmin = p.X;
                if (p.X > xmax) xmax = p.X;
                if (p.Y < ymin) ymin = p.Y;
                if (p.Y > ymax) ymax = p.Y;
            }
        }

        void ApplyX(float xmin, float xmax)
        {
            if (Math.Abs(xmax - xmin) < 1e-9f) { xmin -= 0.5f; xmax += 0.5f; }
            float pad = (xmax - xmin) * 0.04f;
            _xMin = xmin - pad;
            _xMax = xmax + pad;
        }

        /// <summary>固定左轴 Y 范围（不自动扩边；定位可 ±对称）。</summary>
        public void SetFixedYL(float ymin, float ymax)
        {
            if (ymax < ymin) { float t = ymin; ymin = ymax; ymax = t; }
            if (Math.Abs(ymax - ymin) < 1e-12f) { ymin -= 1; ymax += 1; }
            _yLMin = ymin; _yLMax = ymax;
            CaptureDataBounds();
            Invalidate();
        }

        /// <summary>固定右轴 Y 范围（局放标尺；定位正负对称时传 ±FS）。</summary>
        public void SetFixedYR(float ymin, float ymax)
        {
            if (ymax < ymin) { float t = ymin; ymin = ymax; ymax = t; }
            if (Math.Abs(ymax - ymin) < 1e-12f) { ymin -= 1; ymax += 1; }
            _yRMin = ymin; _yRMax = ymax;
            CaptureDataBounds();
            Invalidate();
        }

        void ApplyYL(float ymin, float ymax)
        {
            if (Math.Abs(ymax - ymin) < 1e-9f) { ymin -= 0.5f; ymax += 0.5f; }
            float pad = (ymax - ymin) * 0.08f;
            if (pad < 1e-6f) pad = 0.1f;
            _yLMin = ymin - pad;
            _yLMax = ymax + pad;
        }

        void ApplyYR(float ymin, float ymax)
        {
            if (Math.Abs(ymax - ymin) < 1e-9f) { ymin -= 0.5f; ymax += 0.5f; }
            float pad = (ymax - ymin) * 0.08f;
            if (pad < 1e-6f) pad = 0.1f;
            _yRMin = ymin - pad;
            _yRMax = ymax + pad;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                PaintCore(e);
            }
            catch
            {
                try { base.OnPaint(e); } catch { }
            }
        }

        void PaintCore(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            Rectangle bounds = ClientRectangle;
            bounds.Inflate(-2, -2);
            using (var border = new Pen(Color.FromArgb(160, 176, 190)))
                g.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

            int titleH = Font.Height + 8;
            bool showRight = _dual && (_right.Count >= 2 || !string.IsNullOrEmpty(_yRightLabel));
            // 左轴 kV 标尺需更宽边距，刻度数字清晰可读
            int leftMargin = (_yLeftLabel != null && _yLeftLabel.IndexOf("kV", StringComparison.OrdinalIgnoreCase) >= 0) ? 62 : 52;
            int rightMargin = showRight ? 56 : 16;

            using (var titleFont = new Font(Font, FontStyle.Bold))
            {
                string head = string.IsNullOrEmpty(_subtitle) ? _title : (_title + "  ·  " + _subtitle);
                TextRenderer.DrawText(g, head, titleFont,
                    new Rectangle(bounds.X + 8, bounds.Y + 2, Math.Max(40, bounds.Width - rightMargin - 20), titleH),
                    Color.FromArgb(40, 60, 80),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            // 图例（双曲线时）
            if (_dual)
            {
                int legendY = bounds.Y + 4;
                int lx = bounds.Right - rightMargin - 210;
                if (lx < bounds.X + 120) lx = bounds.X + 120;
                if (_mid.Count >= 2)
                {
                    DrawLegendSwatch(g, lx, legendY, _leftColor, "分压");
                    DrawLegendSwatch(g, lx + 70, legendY, _midColor, "源V");
                    DrawLegendSwatch(g, lx + 140, legendY, _rightColor, string.IsNullOrEmpty(_yRightLabel) ? "电流" : _yRightLabel);
                }
                else
                {
                    DrawLegendSwatch(g, lx, legendY, _leftColor, string.IsNullOrEmpty(_yLeftLabel) ? "左轴" : _yLeftLabel);
                    DrawLegendSwatch(g, lx + 110, legendY, _rightColor, string.IsNullOrEmpty(_yRightLabel) ? "右轴" : _yRightLabel);
                }
            }

            Rectangle plot = new Rectangle(
                bounds.X + leftMargin,
                bounds.Y + titleH + 4,
                Math.Max(10, bounds.Width - leftMargin - rightMargin),
                Math.Max(10, bounds.Height - titleH - 30));

            using (var grid = new Pen(Color.FromArgb(220, 228, 234)))
            using (var axis = new Pen(Color.FromArgb(120, 140, 160)))
            {
                const int divx = 5, divy = 4;
                for (int i = 0; i <= divx; i++)
                {
                    float x = plot.Left + i * plot.Width / (float)divx;
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                }
                for (int i = 0; i <= divy; i++)
                {
                    float y = plot.Top + i * plot.Height / (float)divy;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
                g.DrawRectangle(axis, plot);

                // 左 Y 刻度
                for (int i = 0; i <= divy; i++)
                {
                    float t = 1f - i / (float)divy;
                    float val = _yLMin + (_yLMax - _yLMin) * t;
                    float y = plot.Bottom - t * plot.Height;
                    TextRenderer.DrawText(g, FormatTick(val), Font,
                        new Rectangle(bounds.X + 2, (int)y - Font.Height / 2, plot.Left - bounds.X - 4, Font.Height + 2),
                        _leftColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }

                // 右 Y 刻度
                if (showRight)
                {
                    for (int i = 0; i <= divy; i++)
                    {
                        float t = 1f - i / (float)divy;
                        float val = _yRMin + (_yRMax - _yRMin) * t;
                        float y = plot.Bottom - t * plot.Height;
                        TextRenderer.DrawText(g, FormatTick(val), Font,
                            new Rectangle(plot.Right + 2, (int)y - Font.Height / 2, rightMargin - 4, Font.Height + 2),
                            _rightColor,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                }

                // X 刻度
                for (int i = 0; i <= divx; i++)
                {
                    float t = i / (float)divx;
                    float val = _xMin + (_xMax - _xMin) * t;
                    float x = plot.Left + t * plot.Width;
                    TextRenderer.DrawText(g, FormatTick(val), Font,
                        new Rectangle((int)x - 28, plot.Bottom + 2, 56, Font.Height + 2),
                        Color.FromArgb(80, 96, 112),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                }

                // 轴单位标签
                TextRenderer.DrawText(g, _yLeftLabel, Font,
                    new Rectangle(bounds.X + 2, bounds.Y + 2, leftMargin + 8, titleH),
                    _leftColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (showRight && !string.IsNullOrEmpty(_yRightLabel))
                {
                    TextRenderer.DrawText(g, _yRightLabel, Font,
                        new Rectangle(plot.Right - 4, bounds.Y + 2, rightMargin + 8, titleH),
                        _rightColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
                TextRenderer.DrawText(g, _xLabel, Font,
                    new Rectangle(plot.Left + plot.Width / 2 - 40, plot.Bottom + 2, 80, Font.Height + 2),
                    Color.FromArgb(80, 96, 112),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            }

            bool hasLeft = _left.Count >= 2;
            bool hasRight = _right.Count >= 2;
            bool hasMid = _mid.Count >= 2;
            if (!hasLeft && !hasRight && !hasMid)
            {
                TextRenderer.DrawText(g, _emptyHint, Font, plot,
                    Color.FromArgb(120, 140, 160),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            float xspan = Math.Max(1e-9f, _xMax - _xMin);
            float yLspan = Math.Max(1e-9f, _yLMax - _yLMin);
            float yRspan = Math.Max(1e-9f, _yRMax - _yRMin);

            var saved = g.Save();
            g.SetClip(plot);
            try
            {
                // 左轴零线
                if (_yLMin < 0 && _yLMax > 0)
                {
                    float zy = plot.Bottom - (0 - _yLMin) / yLspan * (plot.Height - 1);
                    using (var zpen = new Pen(Color.FromArgb(120, _leftColor)) { DashStyle = DashStyle.Dash })
                        g.DrawLine(zpen, plot.Left, zy, plot.Right, zy);
                }
                // 右轴零线（直方/stem 填 0）
                if (hasRight && (_rightAsHistogramFill || _rightAsBipolarStem) && _yRMin < 0 && _yRMax > 0)
                {
                    float zy = plot.Bottom - (0 - _yRMin) / yRspan * (plot.Height - 1);
                    using (var zpen = new Pen(Color.FromArgb(100, _rightColor)) { DashStyle = DashStyle.Dot })
                        g.DrawLine(zpen, plot.Left, zy, plot.Right, zy);
                }
                // 先画右轴局放（stem/直方），再画左轴振荡折线
                if (hasRight && _rightAsBipolarStem)
                    DrawBipolarStemFromZero(g, _right, plot, xspan, _yRMin, yRspan, _rightColor);
                else if (hasRight && _rightAsHistogramFill)
                    DrawHistogramFillToZero(g, _right, plot, xspan, _yRMin, yRspan, _rightColor);
                if (hasLeft)
                    DrawTrace(g, _left, plot, xspan, _yLMin, yLspan, _leftColor, 1.9f);
                if (_mid.Count >= 2)
                    DrawTrace(g, _mid, plot, xspan, _yLMin, yLspan, _midColor, 1.8f);
                if (hasRight && !_rightAsHistogramFill && !_rightAsBipolarStem)
                    DrawTrace(g, _right, plot, xspan, _yRMin, yRspan, _rightColor, 1.6f);

                // 超量程：右轴 ±FS 虚线
                if (_overrangeMark && _dual && _overrangeFullScaleV > 0)
                {
                    float fs = _overrangeFullScaleV;
                    using (var pen = new Pen(Color.FromArgb(220, 40, 40), 1.4f) { DashStyle = DashStyle.Dash })
                    {
                        float yPos = plot.Bottom - (fs - _yRMin) / yRspan * (plot.Height - 1);
                        float yNeg = plot.Bottom - (-fs - _yRMin) / yRspan * (plot.Height - 1);
                        if (yPos >= plot.Top && yPos <= plot.Bottom)
                            g.DrawLine(pen, plot.Left, yPos, plot.Right, yPos);
                        if (yNeg >= plot.Top && yNeg <= plot.Bottom)
                            g.DrawLine(pen, plot.Left, yNeg, plot.Right, yNeg);
                    }
                }
            }
            finally
            {
                g.Restore(saved);
            }

            if (_overrangeMark && !string.IsNullOrEmpty(_overrangeBanner))
            {
                using (var font = new Font(Font, FontStyle.Bold))
                {
                    var rect = new Rectangle(plot.Left + 6, plot.Top + 4, Math.Min(220, plot.Width - 12), font.Height + 6);
                    using (var brush = new SolidBrush(Color.FromArgb(200, 180, 30, 30)))
                        g.FillRectangle(brush, rect);
                    TextRenderer.DrawText(g, _overrangeBanner, font, rect,
                        Color.White,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }

        /// <summary>
        /// 定位原始局放双极 stem：按像素列取 |峰|最大点（保留符号），从 Y=0 画竖线。
        /// 禁止满采样 DrawLines 叠成紫带淹没尖峰。
        /// </summary>
        void DrawBipolarStemFromZero(Graphics g, List<PointF> series, Rectangle plot, float xspan, float yMin, float ySpan, Color color)
        {
            if (series == null || series.Count < 1 || plot.Width < 2) return;
            float yZero = plot.Bottom - (0f - yMin) / ySpan * (plot.Height - 1);
            yZero = Math.Max(plot.Top, Math.Min(plot.Bottom, yZero));
            int cols = Math.Max(1, plot.Width);
            var colPeak = new float[cols];
            var colHas = new bool[cols];
            for (int i = 0; i < series.Count; i++)
            {
                float nx = (series[i].X - _xMin) / xspan;
                if (float.IsNaN(nx) || float.IsInfinity(nx)) continue;
                int c = (int)Math.Round(nx * (cols - 1));
                if (c < 0) c = 0;
                if (c >= cols) c = cols - 1;
                float y = series[i].Y;
                float a = Math.Abs(y);
                if (!colHas[c] || a > Math.Abs(colPeak[c]))
                {
                    colPeak[c] = y; // 保留符号
                    colHas[c] = true;
                }
            }
            using (var pen = new Pen(Color.FromArgb(210, color), 1.35f))
            {
                for (int c = 0; c < cols; c++)
                {
                    if (!colHas[c]) continue;
                    float x = plot.Left + c;
                    float ny = (colPeak[c] - yMin) / ySpan;
                    float y = plot.Bottom - ny * (plot.Height - 1);
                    g.DrawLine(pen, x, yZero, x, y);
                }
            }
        }

        /// <summary>
        /// 检波直方：先按像素列取峰（避免密点叠成噪声墙），再从 Y=0 填到幅值。
        /// </summary>
        void DrawHistogramFillToZero(Graphics g, List<PointF> series, Rectangle plot, float xspan, float yMin, float ySpan, Color color)
        {
            if (series == null || series.Count < 1 || plot.Width < 2) return;
            float yZero = plot.Bottom - (0f - yMin) / ySpan * (plot.Height - 1);
            yZero = Math.Max(plot.Top, Math.Min(plot.Bottom, yZero));
            int cols = Math.Max(1, plot.Width);
            var colPeak = new float[cols];
            var colHas = new bool[cols];
            for (int i = 0; i < series.Count; i++)
            {
                float nx = (series[i].X - _xMin) / xspan;
                if (float.IsNaN(nx) || float.IsInfinity(nx)) continue;
                int c = (int)Math.Round(nx * (cols - 1));
                if (c < 0) c = 0;
                if (c >= cols) c = cols - 1;
                float a = Math.Abs(series[i].Y);
                if (!colHas[c] || a > colPeak[c])
                {
                    colPeak[c] = a; // 检波直方用绝对值峰
                    colHas[c] = true;
                }
            }
            using (var brush = new SolidBrush(Color.FromArgb(185, color)))
            {
                for (int c = 0; c < cols; c++)
                {
                    if (!colHas[c]) continue;
                    float x = plot.Left + c;
                    float ny = (colPeak[c] - yMin) / ySpan;
                    float y = plot.Bottom - ny * (plot.Height - 1);
                    float top = Math.Min(y, yZero);
                    float bot = Math.Max(y, yZero);
                    float h = Math.Max(1f, bot - top);
                    g.FillRectangle(brush, x, top, 1.15f, h);
                }
            }
        }

        void DrawTrace(Graphics g, List<PointF> series, Rectangle plot, float xspan, float yMin, float ySpan, Color color, float width)
        {
            if (series.Count < 2) return;
            // 分块 DrawLines：GDI+ 单次超大点数易失败/只画几段；定位密 Pico 可达数万点
            const int chunk = 4096;
            using (var pen = new Pen(color, width))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int i = 0;
                while (i < series.Count - 1)
                {
                    int take = Math.Min(chunk, series.Count - i);
                    // 与下一块重叠 1 点，避免断裂
                    if (i + take < series.Count && take > 1) { /* keep */ }
                    var pts = new PointF[take];
                    for (int k = 0; k < take; k++)
                    {
                        float nx = (series[i + k].X - _xMin) / xspan;
                        float ny = (series[i + k].Y - yMin) / ySpan;
                        pts[k] = new PointF(
                            plot.Left + nx * (plot.Width - 1),
                            plot.Bottom - ny * (plot.Height - 1));
                    }
                    if (pts.Length >= 2)
                        g.DrawLines(pen, pts);
                    if (i + take >= series.Count) break;
                    i += take - 1;
                }
            }
        }

        static void DrawLegendSwatch(Graphics g, int x, int y, Color color, string text)
        {
            using (var pen = new Pen(color, 2.2f))
                g.DrawLine(pen, x, y + FontHeightHalf(), x + 18, y + FontHeightHalf());
            TextRenderer.DrawText(g, text, SystemFonts.DefaultFont,
                new Rectangle(x + 22, y, 90, 18),
                color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        static int FontHeightHalf()
        {
            return 8;
        }

        static string FormatTick(float v)
        {
            float a = Math.Abs(v);
            // kV / 工程量：常见量程用固定小数位，标尺更整齐清晰
            if (a >= 1000) return v.ToString("0", CultureInfo.InvariantCulture);
            if (a >= 100) return v.ToString("0", CultureInfo.InvariantCulture);
            if (a >= 10) return v.ToString("0.#", CultureInfo.InvariantCulture);
            if (a >= 1) return v.ToString("0.##", CultureInfo.InvariantCulture);
            if (a >= 0.1) return v.ToString("0.##", CultureInfo.InvariantCulture);
            if (a >= 0.01) return v.ToString("0.###", CultureInfo.InvariantCulture);
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
