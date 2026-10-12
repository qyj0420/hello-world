using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    // The data-browser display convention is deliberately retained: positive
    // detected charge is a stem, and the voltage has its own physical ruler.
    // The raw view instead retains both ADC polarities. All reduction is for
    // painting only; the input arrays are referenced, never rewritten.
    internal sealed partial class FactoryWaveformControl : UserControl
    {
        internal static readonly Color PdColor = Color.FromArgb(44, 133, 110);
        internal static readonly Color VoltageColor = Color.FromArgb(0, 120, 156);
        internal static readonly Color GridColor = Color.FromArgb(233, 239, 244);
        internal static readonly Color BorderColor = Color.FromArgb(216, 226, 234);
        internal static readonly Color InkColor = Color.FromArgb(35, 53, 72);
        private readonly FactoryWaveformCanvas canvas;
        private readonly CheckBox showPd = new CheckBox { Text = "局放", Checked = true, AutoSize = true };
        private readonly CheckBox showVoltage = new CheckBox { Text = "振荡电压", Checked = true, AutoSize = true };
        private readonly CheckBox cursor = new CheckBox { Text = "光标读数", Checked = true, AutoSize = true };
        private readonly Label heading = new Label { Text = "同步波形", AutoSize = true, Padding = new Padding(4, 4, 10, 0) };
        private readonly Button reset = new Button { Text = "复位", AutoSize = true, FlatStyle = FlatStyle.Flat };
        private readonly Button settings = new Button { Text = "显示设置", AutoSize = true, FlatStyle = FlatStyle.Flat };
        private readonly FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 35, WrapContents = false, AutoScroll = true, Padding = new Padding(2) };
        private double voltageReferenceKv = 1;
        private double fixedChargeMaximumPc;
        private bool showGrid = true;
        internal string RawUnit { get; set; } = "pC";
        private string signalCaption="原始输入";
        private readonly CheckBox showEnvelope = new CheckBox { Text = "包络", AutoSize = true };
        internal string LabelStatus { get; set; } = "未识别";
        internal string LabelCaption { get; set; } = "未识别";
        private ContextMenuStrip pulseLabelMenu;
        private Action markOriginal, markEnvelopeLabel;
        private Func<List<FactoryTfAnnotationRegion>> editablePulseLabels;
        private Action<FactoryTfAnnotationRegion> editPulseLabel;
        internal void ConfigurePulseLabelMenu(Action markPulse, Action markEnvelope,
            Func<List<FactoryTfAnnotationRegion>> editable = null, Action<FactoryTfAnnotationRegion> edit = null)
        {
            markOriginal = markPulse; markEnvelopeLabel = markEnvelope; editablePulseLabels = editable; editPulseLabel = edit;
            showEnvelope.CheckedChanged += (s,e) => canvas.SceneChanged();
            pulseLabelMenu = new ContextMenuStrip();
            pulseLabelMenu.Opening += (s,e) => { PreparePulseLabelMenu(); if (!Enabled || !HasWaveform) e.Cancel = true; };
            canvas.ContextMenuStrip = pulseLabelMenu; Disposed += (s,e) => pulseLabelMenu.Dispose();
        }
        internal ContextMenuStrip PreparePulseLabelMenu()
        {
            var menu = pulseLabelMenu; if (menu == null) return null;
            FactoryLabelMenus.Clear(menu.Items);
            menu.Items.Add(new ToolStripMenuItem(LabelStatus) { Enabled = false });
            if (!Enabled || !HasWaveform) return menu;
            FactoryLabelMenus.AddEdits(menu.Items, editablePulseLabels == null ? new List<FactoryTfAnnotationRegion>() : editablePulseLabels(), editPulseLabel);
            if (markOriginal != null) menu.Items.Add("添加原始脉冲标签…", null, (s,e) => markOriginal());
            if (markEnvelopeLabel != null) menu.Items.Add("添加包络标签…", null, (s,e) => markEnvelopeLabel());
            var overlay = new ToolStripMenuItem("显示 Hilbert 包络") { CheckOnClick = true };
            overlay.Checked = showEnvelope.Checked;
            overlay.CheckedChanged += (s,e) => showEnvelope.Checked = overlay.Checked; menu.Items.Add(overlay);
            menu.Items.Add("复位视窗", null, (s,e) => ResetView());
            return menu;
        }
        internal void SetPulseEnvelope(double[] valuesV, double coefficient)
        { canvas.SetEnvelope(valuesV, coefficient); }

        internal FactoryWaveformControl()
        {
            BackColor = Color.White; ForeColor = InkColor;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            AutoScaleMode = AutoScaleMode.None;
            canvas = new FactoryWaveformCanvas(this) { Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = InkColor };
            canvas.Font = Font;
            Controls.Add(canvas); Controls.Add(tools);
            heading.Font = new Font(Font, FontStyle.Bold);
            showPd.ForeColor = PdColor; showVoltage.ForeColor = VoltageColor;
            foreach (Control c in new Control[] { heading, showPd, showVoltage, cursor, reset, settings })
            { c.Margin = new Padding(4, 2, 5, 2); tools.Controls.Add(c); }
            showPd.CheckedChanged += (s, e) => canvas.SceneChanged();
            showVoltage.CheckedChanged += (s, e) => canvas.SceneChanged();
            cursor.CheckedChanged += (s, e) => canvas.Invalidate();
            reset.Click += (s, e) => ResetView(); settings.Click += (s, e) => ShowSettings();
            FontChanged += (s, e) => { canvas.Font = Font; tools.Height = Math.Max(35, Font.Height + 15); };
        }

        internal string Title { get { return heading.Text; } set { heading.Text = value ?? ""; } }
        internal double VoltageReferenceKv
        {
            get { return voltageReferenceKv; }
            set
            {
                if (!Finite(value) || value < 0) throw new ArgumentOutOfRangeException("value");
                voltageReferenceKv = Math.Max(1, value); canvas.SceneChanged();
            }
        }
        internal bool HasWaveform { get { return canvas.Count > 0; } }
        internal bool RawMode { get { return canvas.IsRaw; } }
        internal double VisibleStartMs { get { return canvas.ViewStartMs; } }
        internal double VisibleEndMs { get { return canvas.ViewEndMs; } }
        internal int SourceSampleCount { get { return canvas.Count; } }
        internal int StaticRenderCount { get { return canvas.StaticRenderCount; } }
        internal bool WrapToolbar
        {
            get { return tools.WrapContents; }
            set { tools.WrapContents = value; tools.AutoScroll = !value; tools.AutoSize = value; tools.AutoSizeMode = AutoSizeMode.GrowAndShrink; tools.PerformLayout(); }
        }
        internal Rectangle PlotBounds
        {
            get { var bounds = canvas.PlotBounds; bounds.Offset(canvas.Location); return bounds; }
        }
        internal void UsePulseNavigation(Button previous, Button next, Label position)
        {
            // One compact strip, rather than a navigation row plus another
            // wave toolbar. Leaves useful drawing height for all four panes.
            heading.Visible = showPd.Visible = showVoltage.Visible = cursor.Visible = false;
            previous.Height = next.Height = 30;
            position.Dock = DockStyle.None; position.Width = 160; position.Height = 30; position.Margin = new Padding(4, 2, 5, 2);
            tools.Controls.Add(previous); tools.Controls.SetChildIndex(previous, 0);
            tools.Controls.Add(next); tools.Controls.SetChildIndex(next, 1);
            tools.Controls.Add(position); tools.Controls.SetChildIndex(position, 2);
            tools.WrapContents = false; tools.AutoSize = false; tools.AutoScroll = true; tools.Height = 42;
            canvas.SetRaw(new double[0], 1, 1, 1);
        }

        internal void SetCombined(double[] voltageKv, double[] pdPc, double sampleRateHz, double durationMs)
        {
            ValidateTime(sampleRateHz, durationMs);
            canvas.SetCombined(voltageKv ?? new double[0], pdPc ?? new double[0], sampleRateHz, durationMs);
            showVoltage.Visible = true; showPd.Text = "检波局放";
        }

        internal void SetRaw(short[] adc, double sampleRateHz, double fullScaleV, int maxAdc,
            double coefficientPcPerV, double durationMs)
        {
            ValidateTime(sampleRateHz, durationMs);
            if (!Finite(fullScaleV) || fullScaleV <= 0 || maxAdc <= 0 || !Finite(coefficientPcPerV) || coefficientPcPerV <= 0)
                throw new ArgumentException("原始局放显示需要有效的量程、ADC 最大值及校准系数。");
            canvas.SetRaw(adc ?? new short[0], sampleRateHz, fullScaleV / maxAdc * coefficientPcPerV, durationMs);
            signalCaption="原始输入";
            showVoltage.Visible = false; showPd.Text = "整段原始局放";
        }

        internal void SetPulse(double[] samplesV, double sampleRateHz, double coefficientPcPerV,bool highPass=false)
        {
            if (samplesV == null || samplesV.Length == 0 || !Finite(coefficientPcPerV) || coefficientPcPerV <= 0)
                throw new ArgumentException("脉冲显示需要原始电压及采集时的校准系数。");
            double durationMs = samplesV.Length * 1000.0 / sampleRateHz;
            ValidateTime(sampleRateHz, durationMs);
            canvas.SetRaw(samplesV, sampleRateHz, coefficientPcPerV, durationMs);
            signalCaption=highPass?"10 kHz高通":"原始输入";
            showVoltage.Visible = false; showPd.Text = highPass?"高通脉冲":"原始脉冲";
        }

        internal void SetFilteredCapture(Xiongxin.ZeroBench.BenchCapture capture,double coefficient)
        {
            if(!Finite(coefficient) || coefficient<=0)throw new ArgumentOutOfRangeException("coefficient");
            var signal=FactoryPdSignal.Get(capture);
            ValidateTime(capture.PicoRateHz,capture.Settings.CaptureDurationMs);
            canvas.SetRaw(signal,capture.PicoRateHz,coefficient,capture.Settings.CaptureDurationMs);
            signalCaption="10 kHz高通";
            showVoltage.Visible=false;showPd.Text="10 kHz高通";
        }

        internal void ClearWave() { canvas.ClearWave(); }
        internal void ResetView() { canvas.ResetView(); }
        internal void SetVisibleTime(double startMs, double endMs) { canvas.SetVisibleTime(startMs, endMs); }
        internal Bitmap Snapshot()
        {
            var image = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); return image;
        }

        private static void ValidateTime(double rate, double duration)
        {
            if (!Finite(rate) || rate <= 0 || !Finite(duration) || duration <= 0)
                throw new ArgumentOutOfRangeException("sampleRateHz", "必须提供实际采样率及完整采集时长。");
        }

        private void ShowSettings()
        {
            using (var dialog = new Form { Text = "波形显示设置", StartPosition = FormStartPosition.CenterParent,
                Size = new Size(470, 300), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
                MinimizeBox = false, Font = Font, BackColor = Color.White })
            {
                var owner = FindForm(); if (owner != null) dialog.Icon = owner.Icon;
                else using (var iconStream = GetType().Assembly.GetManifestResourceStream("Factory.Brand.ico"))
                    if (iconStream != null) dialog.Icon = new Icon(iconStream);
                var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 5 };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                var voltage = new NumericUpDown { DecimalPlaces = 2, Minimum = 1, Maximum = 1000, Value = (decimal)Math.Min(1000, voltageReferenceKv), Dock = DockStyle.Fill };
                var charge = new NumericUpDown { DecimalPlaces = 2, Minimum = 0, Maximum = 100000000, Value = (decimal)Math.Min(100000000, fixedChargeMaximumPc), Dock = DockStyle.Fill };
                var grid = new CheckBox { Text = "显示网格", Checked = showGrid, AutoSize = true };
                root.Controls.Add(new Label { Text = "电压参考尺度 / kV（最小 1 kV）", AutoSize = true }, 0, 0); root.Controls.Add(voltage, 1, 0);
                root.Controls.Add(new Label { Text = "局放纵轴上限 / " + (canvas.IsRaw ? RawUnit : "pC") + "（0 为自动）", AutoSize = true }, 0, 1); root.Controls.Add(charge, 1, 1);
                root.Controls.Add(grid, 0, 2); root.SetColumnSpan(grid, 2);
                var help = new Label { Text = "滚轮：时间缩放；Shift + 滚轮：纵轴缩放。\n" + (locationTools ? "勾选框选放大后左键拖框；A/B线调整选区；中键平移。\n" : "拖动：平移；") + "右键或“复位”：显示完整数据。\n显示设置不改变原始数据、检波结果及分析参数。", AutoSize = true, MaximumSize = new Size(400, 0) };
                root.Controls.Add(help, 0, 3); root.SetColumnSpan(help, 2);
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
                var apply = new Button { Text = "应用", DialogResult = DialogResult.OK, AutoSize = true };
                buttons.Controls.Add(apply); buttons.Controls.Add(new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true });
                root.Controls.Add(buttons, 0, 4); root.SetColumnSpan(buttons, 2); dialog.Controls.Add(root); dialog.AcceptButton = apply;
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    voltageReferenceKv = (double)voltage.Value; ChargeRulerMaximumPc = charge.Value; showGrid = grid.Checked;
                    canvas.SceneChanged();
                }
            }
        }

        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static double VoltageFraction(double voltage, double scale) { return .35 - .25 * voltage / scale; }
        internal static double NiceCeiling(double value)
        {
            if (!Finite(value) || value <= 0) return 1;
            double power = Math.Pow(10, Math.Floor(Math.Log10(value))), unit = value / power;
            return (unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10) * power;
        }

        private sealed partial class FactoryWaveformCanvas : Control
        {
            private readonly FactoryWaveformControl owner;
            private FactorySampleEnvelope pd = new FactorySampleEnvelope(new double[0]);
            private FactorySampleEnvelope voltage = new FactorySampleEnvelope(new double[0]);
            private FactorySampleEnvelope envelope = new FactorySampleEnvelope(new double[0]);
            internal void SetEnvelope(double[] valuesV, double coefficient) { envelope = new FactorySampleEnvelope(valuesV, coefficient); SceneChanged(); }
            private double stepMs = 1, endMs = 1, viewMin, viewMax = 1, verticalMin, verticalMax = 1;
            private double chargeMinimum, chargeMaximum = 1, voltageScale = 1;
            private bool raw, dragging;
            private int dragStartX;
            private double dragMin, dragMax;
            private Point? mouse;
            private Bitmap scene;
            private float sceneDpiX, sceneDpiY;
            private bool Compact { get { return Height < 200; } }
            internal int StaticRenderCount { get; private set; }
            internal int Count { get { return Math.Max(pd.Count, voltage.Count); } }
            internal bool IsRaw { get { return raw; } }
            internal double ViewStartMs { get { return viewMin; } }
            internal double ViewEndMs { get { return viewMax; } }
            internal Rectangle PlotBounds { get { return Plot; } }

            internal FactoryWaveformCanvas(FactoryWaveformControl owner)
            {
                this.owner = owner; DoubleBuffered = true; TabStop = true;
                SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            }
            private Rectangle Plot
            {
                get
                {
                    // Small main-window layouts leave only 105-140 px below
                    // the tool strip. Keep one title row and one tick row;
                    // the mouse help remains available in display settings.
                    int top = Compact ? Font.Height + 7 : Math.Max(55, Font.Height * 2 + 16);
                    int bottom = Compact ? Font.Height + 9 : Math.Max(47, Font.Height * 2 + 14);
                    int left = Math.Max(76, Font.Height * 4), right = raw ? 26 : Math.Max(76, Font.Height * 4);
                    return new Rectangle(left, top, Math.Max(1, Width - left - right), Math.Max(1, Height - top - bottom));
                }
            }

            internal void SetCombined(double[] volts, double[] charge, double rate, double duration)
            {
                selectionEnabled=false;scaleStartMs=0;
                raw = false; pd = new FactorySampleEnvelope(charge); voltage = new FactorySampleEnvelope(volts);
                stepMs = 1000 / rate; endMs = Math.Min(duration, Count * stepMs); if (endMs <= 0) endMs = duration;
                RebuildRanges(); ResetView();
            }
            internal void SetRaw(short[] adc, double rate, double multiplier, double duration)
            {
                selectionEnabled=false;scaleStartMs=0;
                raw = true; pd = new FactorySampleEnvelope(adc, multiplier); voltage = new FactorySampleEnvelope(new double[0]);
                stepMs = 1000 / rate; endMs = Math.Min(duration, adc.Length * stepMs); if (endMs <= 0) endMs = duration;
                RebuildRanges(); ResetView();
            }
            internal void SetRaw(double[] samples, double rate, double multiplier, double duration)
            {
                selectionEnabled=false;scaleStartMs=0;
                raw = true; pd = new FactorySampleEnvelope(samples, multiplier); voltage = new FactorySampleEnvelope(new double[0]);
                stepMs = 1000 / rate; endMs = Math.Min(duration, samples.Length * stepMs); if (endMs <= 0) endMs = duration;
                RebuildRanges(); ResetView();
            }
            internal void ClearWave()
            {
                selectionEnabled=false;scaleStartMs=0;
                pd = new FactorySampleEnvelope(new double[0]); voltage = new FactorySampleEnvelope(new double[0]);
                envelope = new FactorySampleEnvelope(new double[0]); owner.LabelCaption = owner.LabelStatus = "未识别";
                stepMs = endMs = 1; RebuildRanges(); ResetView();
            }
            internal void ResetView() { CancelBoxZoom();viewMin = 0; viewMax = endMs; verticalMin = 0; verticalMax = 1; mouse = null; SceneChanged(); }
            internal void SetVisibleTime(double first, double last)
            {
                if (!Finite(first) || !Finite(last) || first < 0 || last <= first || last > endMs * (1 + 1e-12))
                    throw new ArgumentOutOfRangeException("first", "显示范围必须在完整采集时间内。");
                viewMin = first; viewMax = Math.Min(endMs, last); mouse = null; SceneChanged();
            }
            private void ReleaseScene() { if (scene != null) { scene.Dispose(); scene = null; } }
            internal void SceneChanged() { ReleaseScene(); Invalidate(); }
            protected override void OnResize(EventArgs e) { ReleaseScene(); base.OnResize(e); }
            protected override void OnFontChanged(EventArgs e) { ReleaseScene(); base.OnFontChanged(e); }
            protected override void Dispose(bool disposing) { if (disposing) ReleaseScene(); base.Dispose(disposing); }

            private void RebuildRanges()
            {
                int lo, hi; bool missing; pd.Query(Bound(scaleStartMs,pd.Count), pd.Count, out lo, out hi, out missing);
                double min = lo < 0 ? 0 : pd.Value(lo), max = hi < 0 ? 0 : pd.Value(hi);
                if (raw)
                {
                    double half = NiceCeiling(Math.Max(Math.Abs(min), Math.Abs(max)) * 1.06);
                    chargeMinimum = -half; chargeMaximum = half;
                }
                else { chargeMinimum = 0; chargeMaximum = NiceCeiling(Math.Max(0, max) * 1.04); }
                voltage.Query(0, voltage.Count, out lo, out hi, out missing);
                double peak = Math.Max(lo < 0 ? 0 : Math.Abs(voltage.Value(lo)), hi < 0 ? 0 : Math.Abs(voltage.Value(hi)));
                voltageScale = Math.Max(1, peak / 1.2);
            }
            private double VoltageScale { get { return Math.Max(voltageScale, owner.voltageReferenceKv); } }
            private void ChargeRange(out double min, out double max)
            {
                double baseMin = raw && owner.fixedChargeMaximumPc > 0 ? -owner.fixedChargeMaximumPc : chargeMinimum;
                double baseMax = owner.fixedChargeMaximumPc > 0 ? owner.fixedChargeMaximumPc : chargeMaximum;
                min = baseMin + (baseMax - baseMin) * (1 - verticalMax);
                max = baseMin + (baseMax - baseMin) * (1 - verticalMin);
            }
            private float ProjectCharge(double value, Rectangle p, double min, double max)
            { return (float)(p.Bottom - (value - min) / (max - min) * p.Height); }
            private float ProjectVoltage(double value, Rectangle p)
            { return (float)(p.Top + p.Height * (VoltageFraction(value, VoltageScale) - verticalMin) / (verticalMax - verticalMin)); }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e); if (Width < 210 || Height < Font.Height * 2 + 30) return;
                if (scene == null || scene.Size != ClientSize || sceneDpiX != e.Graphics.DpiX || sceneDpiY != e.Graphics.DpiY)
                {
                    ReleaseScene(); var next = new Bitmap(Width, Height);
                    try
                    {
                        next.SetResolution(e.Graphics.DpiX, e.Graphics.DpiY);
                        using (var g = Graphics.FromImage(next)) { g.Clear(BackColor); RenderScene(g); }
                        scene = next; sceneDpiX = e.Graphics.DpiX; sceneDpiY = e.Graphics.DpiY; StaticRenderCount++;
                    }
                    catch { next.Dispose(); throw; }
                }
                e.Graphics.DrawImageUnscaled(scene, 0, 0);
                if (!boxDragging && owner.cursor.Checked && mouse.HasValue && Plot.Contains(mouse.Value) && Count > 0) DrawCursor(e.Graphics, mouse.Value);
                DrawBoxZoom(e.Graphics);
            }

            private void RenderScene(Graphics g)
            {
                var p = Plot; double qmin, qmax; ChargeRange(out qmin, out qmax);
                using (var grid = new Pen(GridColor)) using (var border = new Pen(BorderColor))
                using (var ink = new SolidBrush(InkColor)) using (var pdPen = new Pen(PdColor, 1.05f))
                using (var voltagePen = new Pen(VoltageColor, 1.45f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    int titleY = p.Top - Font.Height - (Compact ? 4 : 8);
                    DrawLabel(g, raw ? owner.signalCaption+" / " + owner.RawUnit + (owner.showEnvelope.Checked ? " +包络" : "") : "放电量 / pC", new Rectangle(p.Left, titleY, p.Width / 2, Font.Height + 4), PdColor);
                    if (raw && ContextMenuStrip != null) DrawLabel(g, "识别：" + owner.LabelCaption, new Rectangle(p.Left + p.Width / 2, p.Top + 2, p.Width / 2 - 3, Font.Height + 4), Color.DarkSlateBlue, TextFormatFlags.Right);
                    if (!raw) DrawLabel(g, "电压 / kV", new Rectangle(p.Left + p.Width / 2, titleY, p.Width / 2, Font.Height + 4), VoltageColor, TextFormatFlags.Right);
                    if (Compact) DrawLabel(g, "时间 / ms", new Rectangle(p.Left + p.Width / 3, titleY, p.Width / 3, Font.Height + 4), Color.FromArgb(97, 116, 136), TextFormatFlags.HorizontalCenter);
                    int chargeDivisions = Math.Max(1, Math.Min(5, p.Height / (Font.Height + 7)));
                    for (int i = 0; i <= chargeDivisions; i++)
                    {
                        float y = p.Bottom - i * p.Height / (float)chargeDivisions;
                        if (owner.showGrid) g.DrawLine(grid, p.Left, y, p.Right, y);
                        DrawLabel(g, Tick(qmin + (qmax - qmin) * i / chargeDivisions), new Rectangle(1, (int)y - Font.Height / 2, p.Left - 8, Font.Height + 4), InkColor, TextFormatFlags.Right);
                    }
                    double tickStep = TimeTickStep(viewMax - viewMin), firstTick = Math.Ceiling(viewMin / tickStep) * tickStep;
                    for (double t = firstTick; t <= viewMax + tickStep * 1e-9; t += tickStep)
                    {
                        float x = (float)(p.Left + (t - viewMin) / (viewMax - viewMin) * p.Width);
                        if (owner.showGrid) g.DrawLine(grid, x, p.Top, x, p.Bottom);
                        DrawLabel(g, Tick(t), new Rectangle((int)x - 40, p.Bottom + (Compact ? 2 : 6), 80, Font.Height + 4), InkColor, TextFormatFlags.HorizontalCenter);
                    }
                    g.DrawRectangle(border, p);
                    if (!raw)
                    {
                        // Preserve the independent voltage ruler and its zero
                        // position. At compact heights, omit crowded labels,
                        // never move or rescale the voltage trace to fit them.
                        var labelRows = new List<float>();
                        foreach (double v in Compact ? new[] { 0, VoltageScale, -VoltageScale, VoltageScale / 2, -VoltageScale / 2 }
                            : new[] { VoltageScale, VoltageScale / 2, 0, -VoltageScale / 2, -VoltageScale })
                        {
                            float y = ProjectVoltage(v, p); if (y < p.Top || y > p.Bottom) continue;
                            g.DrawLine(border, p.Right, y, p.Right + 5, y);
                            bool crowded = false; foreach (float prior in labelRows) if (Math.Abs(y - prior) < Font.Height + 3) { crowded = true; break; }
                            if (Compact && crowded) continue;
                            labelRows.Add(y);
                            DrawLabel(g, Tick(v), new Rectangle(p.Right + 7, (int)y - Font.Height / 2, Width - p.Right - 8, Font.Height + 4), VoltageColor);
                        }
                    }
                    if (!Compact) DrawLabel(g, owner.locationTools?"时间 / ms · 左键框选 · A/B选区 · 中键平移 · 右键复位":"时间 / ms · 滚轮缩放 · Shift 纵向缩放 · 拖动平移 · 右键复位", new Rectangle(p.Left, p.Bottom + Font.Height + 14, p.Width, Font.Height + 5), Color.FromArgb(97, 116, 136), TextFormatFlags.HorizontalCenter);
                    var saved = g.Save(); g.SetClip(p);
                    try
                    {
                        DrawSelection(g,p,qmin,qmax);
                        if (!raw && owner.showVoltage.Checked)
                        {
                            using (var zero = new Pen(Color.FromArgb(90, VoltageColor))) { zero.DashStyle = DashStyle.Dash; float y = ProjectVoltage(0, p); g.DrawLine(zero, p.Left, y, p.Right, y); }
                            DrawTrace(g, voltagePen, p, voltage, v => ProjectVoltage(v, p));
                        }
                        if (owner.showPd.Checked)
                        {
                            if (raw) DrawTrace(g, pdPen, p, pd, v => ProjectCharge(v, p, qmin, qmax));
                            else DrawChargeStems(g, pdPen, p, qmin, qmax);
                            if (raw && owner.showEnvelope.Checked) using (var envelopePen = new Pen(Color.DarkOrange, 1.3f)) DrawTrace(g, envelopePen, p, envelope, v => ProjectCharge(v, p, qmin, qmax));
                        }
                        if (Count == 0) DrawLabel(g, "等待本轮完整数据", p, Color.FromArgb(97, 116, 136), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    finally { g.Restore(saved); }
                }
            }

            private void PixelRange(int column, int width, int count, out int first, out int end)
            {
                double span = viewMax - viewMin;
                double left = viewMin + column * span / width, right = viewMin + (column + 1.0) * span / width;
                first = Bound(left, count); end = column == width - 1 ? BoundInclusive(viewMax, count) : Bound(right, count);
                if (end < first) end = first;
            }
            private int Bound(double t, int count)
            {
                double index = Math.Ceiling(t / stepMs - 1e-9);
                return index <= 0 ? 0 : index >= count ? count : (int)index;
            }
            private int BoundInclusive(double t, int count)
            {
                double index = Math.Floor(t / stepMs + 1e-9) + 1;
                return index <= 0 ? 0 : index >= count ? count : (int)index;
            }
            private void DrawTrace(Graphics g, Pen pen, Rectangle p, FactorySampleEnvelope source, Func<double, float> project)
            {
                var path = new List<PointF>(Math.Min(4 * p.Width, 16384));
                Action flush = () => { if (path.Count > 1) g.DrawLines(pen, path.ToArray()); else if (path.Count == 1) g.DrawEllipse(pen, path[0].X - 1, path[0].Y - 1, 2, 2); path.Clear(); };
                Action<int> add = i => path.Add(new PointF((float)(p.Left + (i * stepMs - viewMin) / (viewMax - viewMin) * Math.Max(1, p.Width - 1)), project(source.Value(i))));
                for (int x = 0; x < p.Width; x++)
                {
                    int first, end, min, max; bool missing; PixelRange(x, p.Width, source.Count, out first, out end);
                    if (first >= end) continue; source.Query(first, end, out min, out max, out missing);
                    if (min < 0) { flush(); continue; }
                    if (missing)
                    {
                        // Never draw through invalid samples. A missing bucket is
                        // shown as a gap rather than inventing continuity.
                        flush(); continue;
                    }
                    var ordered = new[] { first, min, max, end - 1 }; Array.Sort(ordered);
                    int previous = -1; foreach (int i in ordered) { if (i != previous) add(i); previous = i; }
                }
                flush();
            }
            private void DrawChargeStems(Graphics g, Pen pen, Rectangle p, double min, double max)
            {
                var smoothing = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                try
                {
                    float zero = ProjectCharge(0, p, min, max);
                    for (int x = 0; x < p.Width; x++)
                    {
                        int first, end, lo, hi; bool missing; PixelRange(x, p.Width, pd.Count, out first, out end);
                        pd.Query(first, end, out lo, out hi, out missing); if (hi < 0) continue;
                        double charge = Math.Max(0, pd.Value(hi));
                        g.DrawLine(pen, p.Left + x, zero, p.Left + x, ProjectCharge(charge, p, min, max));
                    }
                }
                finally { g.SmoothingMode = smoothing; }
            }
            private void DrawCursor(Graphics g, Point point)
            {
                var p = Plot; double t = viewMin + (viewMax - viewMin) * (point.X - p.Left) / p.Width;
                int index = Math.Max(0, Math.Min(Math.Max(0, Count - 1), (int)Math.Round(t / stepMs)));
                string text = "t " + (index * stepMs).ToString("0.######", CultureInfo.InvariantCulture) + " ms";
                if (index < pd.Count) text += "   输入 " + Tick(pd.Value(index)) + " " + (raw ? owner.RawUnit : "pC");
                if (!raw && index < voltage.Count) text += "   电压 " + Tick(voltage.Value(index)) + " kV";
                using (var pen = new Pen(Color.FromArgb(125, InkColor))) { pen.DashStyle = DashStyle.Dash; g.DrawLine(pen, point.X, p.Top, point.X, p.Bottom); }
                // In compact mode the cursor shares the single title row;
                // erase that row so its readout cannot overlap unit labels.
                if (Compact) using (var background = new SolidBrush(BackColor)) g.FillRectangle(background, p.Left, 0, p.Width, p.Top - 2);
                DrawLabel(g, text, new Rectangle(p.Left, 4, p.Width, Font.Height + 5), InkColor);
            }
            private void DrawLabel(Graphics g, string text, Rectangle bounds, Color color, TextFormatFlags flags = TextFormatFlags.Left)
            {
                using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap,
                    Trimming = StringTrimming.EllipsisCharacter,
                    Alignment = (flags & TextFormatFlags.Right) != 0 ? StringAlignment.Far :
                        (flags & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : StringAlignment.Near,
                    LineAlignment = (flags & TextFormatFlags.VerticalCenter) != 0 ? StringAlignment.Center : StringAlignment.Near })
                using (var brush = new SolidBrush(color)) g.DrawString(text, Font, brush, bounds, format);
            }
            private static string Tick(double value)
            {
                if (!Finite(value)) return "—";
                return Math.Abs(value) > 0 && (Math.Abs(value) < .0001 || Math.Abs(value) >= 1000000)
                    ? value.ToString("0.###E+0", CultureInfo.InvariantCulture) : value.ToString("0.####", CultureInfo.InvariantCulture);
            }
            private static double TimeTickStep(double span)
            {
                double raw = span / 6;
                double power = Math.Pow(10, Math.Floor(Math.Log10(raw))), unit = raw / power;
                return (unit <= 1 ? 1 : unit <= 2 ? 2 : unit <= 5 ? 5 : 10) * power;
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e); var p = Plot; if (!p.Contains(e.Location) || Count == 0) return;
                double factor = e.Delta > 0 ? .8 : 1.25;
                if ((ModifierKeys & Keys.Shift) != 0)
                {
                    double span = (verticalMax - verticalMin) * factor, f = (e.Y - p.Top) / (double)p.Height;
                    if (span < 1e-8 || span > 1e8) return;
                    double anchor = verticalMin + (verticalMax - verticalMin) * f;
                    verticalMin = anchor - span * f; verticalMax = verticalMin + span; SceneChanged(); return;
                }
                double next = Math.Min(endMs, Math.Max(stepMs * 4, (viewMax - viewMin) * factor));
                next = Math.Min(endMs, next); double fraction = (e.X - p.Left) / (double)p.Width;
                double pivot = viewMin + (viewMax - viewMin) * fraction;
                viewMin = Math.Max(0, Math.Min(endMs - next, pivot - next * fraction)); viewMax = viewMin + next;
                SceneChanged();
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e); Focus();
                if (e.Button == MouseButtons.Right) { if (ContextMenuStrip == null) ResetView(); return; }
                if ((e.Button == MouseButtons.Left || e.Button==MouseButtons.Middle && owner.locationTools) && Plot.Contains(e.Location) && Count>0)
                { if(e.Button==MouseButtons.Left && BeginSelectionDrag(e.X)){Capture=true;return;}
                  if(e.Button==MouseButtons.Left && owner.locationTools && (owner.boxZoom.Checked || owner.pulseWindow.Checked)){boxFirst=boxLast=e.Location;boxDragging=true;Capture=true;Invalidate();return;}
                  dragging = true; dragStartX = e.X; dragMin = viewMin; dragMax = viewMax; Capture = true; }
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e); if (mouse.HasValue && mouse.Value == e.Location) return; mouse = e.Location;
                if(boxDragging){boxLast=ClampBoxPoint(e.Location);Invalidate();return;}
                if(selectionDrag!=0){MoveSelection(e.X);return;}
                if (dragging)
                {
                    double span = dragMax - dragMin, shift = (dragStartX - e.X) * span / Plot.Width;
                    viewMin = Math.Max(0, Math.Min(endMs - span, dragMin + shift)); viewMax = viewMin + span;
                    SceneChanged(); return;
                }
                // Cursor moves reuse the static scene. They do not traverse raw
                // ADC arrays, rebuild the min/max index, or repaint the traces.
                Invalidate(new Rectangle(0, 0, Width, Math.Max(1, Font.Height + 12)));
                Invalidate(Plot);
            }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if(boxDragging && e.Button==MouseButtons.Left)FinishBoxZoom(e.Location); dragging = false; selectionDrag=0; Capture = false; }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) {CancelBoxZoom();dragging = false;selectionDrag=0;} }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!dragging) { mouse = null; Invalidate(); } }
        }
    }

    // Block summaries and a tree of their extrema keep zoom/pan bounded by the
    // display width. A one-sample impulse survives every pixel reduction, and a
    // 7.5-million-sample short ADC segment does not become a double[] copy.
    internal sealed class FactorySampleEnvelope
    {
        private const int BlockSize = 256;
        private readonly double[] doubles;
        private readonly short[] adc;
        private readonly double multiplier;
        private readonly int[] minima, maxima, missing;
        private readonly int leaves;
        internal int Count { get; private set; }

        internal FactorySampleEnvelope(double[] data)
            : this(data, null, 1) { }
        internal FactorySampleEnvelope(double[] data, double multiplier)
            : this(data, null, multiplier) { }
        internal FactorySampleEnvelope(short[] data, double multiplier)
            : this(null, data, multiplier) { }
        private FactorySampleEnvelope(double[] doubles, short[] adc, double multiplier)
        {
            this.doubles = doubles; this.adc = adc; this.multiplier = multiplier;
            Count = doubles != null ? doubles.Length : adc.Length;
            int blocks = (int)(((long)Count + BlockSize - 1) / BlockSize);
            leaves = 1; while (leaves < blocks) leaves <<= 1;
            minima = new int[leaves * 2]; maxima = new int[leaves * 2]; missing = new int[leaves * 2];
            for (int i = 0; i < minima.Length; i++) minima[i] = maxima[i] = -1;
            for (int i = 0; i < Count; i++)
            {
                int b = leaves + i / BlockSize; double value = Value(i);
                if (!FactoryWaveformControl.Finite(value)) { missing[b]++; continue; }
                if (minima[b] < 0 || value < Value(minima[b])) minima[b] = i;
                if (maxima[b] < 0 || value > Value(maxima[b])) maxima[b] = i;
            }
            for (int i = leaves - 1; i > 0; i--)
            {
                minima[i] = Minimum(minima[i * 2], minima[i * 2 + 1]);
                maxima[i] = Maximum(maxima[i * 2], maxima[i * 2 + 1]);
                missing[i] = missing[i * 2] + missing[i * 2 + 1];
            }
        }
        internal double Value(int index) { return (doubles != null ? doubles[index] : adc[index]) * multiplier; }
        private int Minimum(int first, int second) { return first < 0 ? second : second < 0 || Value(first) <= Value(second) ? first : second; }
        private int Maximum(int first, int second) { return first < 0 ? second : second < 0 || Value(first) >= Value(second) ? first : second; }
        private void AddSample(int sample, ref int min, ref int max, ref bool gap)
        {
            if (!FactoryWaveformControl.Finite(Value(sample))) { gap = true; return; }
            min = Minimum(min, sample); max = Maximum(max, sample);
        }
        private void AddNode(int node, ref int min, ref int max, ref bool gap)
        { min = Minimum(min, minima[node]); max = Maximum(max, maxima[node]); gap |= missing[node] != 0; }
        internal void Query(int first, int end, out int minimum, out int maximum, out bool hasMissing)
        {
            minimum = maximum = -1; hasMissing = false;
            first = Math.Max(0, Math.Min(Count, first)); end = Math.Max(first, Math.Min(Count, end));
            while (first < end && first % BlockSize != 0) AddSample(first++, ref minimum, ref maximum, ref hasMissing);
            int completeEnd = end - end % BlockSize;
            if (completeEnd > first)
            {
                int left = leaves + first / BlockSize, right = leaves + completeEnd / BlockSize;
                while (left < right)
                {
                    if ((left & 1) != 0) AddNode(left++, ref minimum, ref maximum, ref hasMissing);
                    if ((right & 1) != 0) AddNode(--right, ref minimum, ref maximum, ref hasMissing);
                    left >>= 1; right >>= 1;
                }
                first = completeEnd;
            }
            while (first < end) AddSample(first++, ref minimum, ref maximum, ref hasMissing);
        }
    }
}
