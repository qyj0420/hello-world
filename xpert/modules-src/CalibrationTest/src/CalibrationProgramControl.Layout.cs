using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Media;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private const float CenterPanelLeft = 270F;
        private const float CenterPanelRight = 1387F;
        private const float CenterPanelTop = 73F;
        private const float CenterPanelBottom = 910F;
        private const float MinimumSignalQualityTop = 620F;
        private const float MaximumSignalQualityTop = 730F;

        private float signalQualityPanelTop = 679F;
        private bool centerSplitterDragging;
        private TextBox numericEditor;
        private Font numericEditorFont;
        private int numericEditorKind;
        private bool numericEditorClosing;
        private string numericEditorOriginalText;

        public event EventHandler CenterSplitterMoved;

        [Category("布局")]
        [Description("信号质量面板在1672×941参考坐标中的顶部位置。左右侧栏始终固定且不可拖动。")]
        public float SignalQualityPanelTop
        {
            get { return signalQualityPanelTop; }
            set
            {
                if (Single.IsNaN(value) || Single.IsInfinity(value))
                    throw new ArgumentOutOfRangeException("value");
                RunOnUiThread(delegate
                {
                    float clamped = Math.Max(MinimumSignalQualityTop, Math.Min(MaximumSignalQualityTop, value));
                    if (Math.Abs(signalQualityPanelTop - clamped) < 0.01F) return;
                    signalQualityPanelTop = clamped;
                    Invalidate();
                    EventHandler handler = CenterSplitterMoved;
                    if (handler != null) handler(this, EventArgs.Empty);
                });
            }
        }

        [Browsable(false)]
        public bool AreSidePanelsResizable { get { return false; } }

        [Browsable(false)]
        public bool IsToolbarVisible { get { return false; } }

        private RectangleF CurrentPlotRectangle
        {
            get { return UseCompactLayout ? CompactLayout.Plot : new RectangleF(324F, 160F, 1021F, Math.Max(250F, signalQualityPanelTop - 243F)); }
        }

        private RectangleF CurrentNoisePlotRectangle
        {
            get
            {
                if (UseCompactLayout) return CompactLayout.NoisePlot;
                float top = signalQualityPanelTop + 97F;
                return new RectangleF(303F, top, 544F, Math.Max(42F, 876F - top));
            }
        }

        private void InitializeEditableLayout()
        {
            numericEditor = new TextBox();
            numericEditorFont = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
            numericEditor.Font = numericEditorFont;
            numericEditor.Name = "CalibrationNumericEditor";
            numericEditor.Visible = false;
            numericEditor.BorderStyle = BorderStyle.FixedSingle;
            numericEditor.TextAlign = HorizontalAlignment.Left;
            numericEditor.BackColor = Color.White;
            numericEditor.ForeColor = ColorTranslator.FromHtml("#18334D");
            numericEditor.KeyDown += NumericEditorKeyDown;
            numericEditor.LostFocus += NumericEditorLostFocus;
            Controls.Add(numericEditor);
        }

        private void DisposeEditableLayout()
        {
            if (numericEditor == null) return;
            numericEditor.KeyDown -= NumericEditorKeyDown;
            numericEditor.LostFocus -= NumericEditorLostFocus;
            numericEditor.Dispose();
            numericEditor = null;
            if (numericEditorFont != null) { numericEditorFont.Dispose(); numericEditorFont = null; }
        }

        private void ShowNumericEditor(int kind)
        {
            if (numericEditor == null) return;
            CancelNumericEditor();
            numericEditorKind = kind;
            numericEditor.AccessibleName = kind == 1 ? CalibrationText("电缆长度 / m", "Cable length / m") : CalibrationText("传播波速 / m/μs", "Propagation velocity / m/μs");
            numericEditor.BackColor = Color.White;
            numericEditor.Text = (kind == 1 ? CableLengthMeters : PropagationVelocityMPerUs)
                .ToString(kind==1?"0.0":"0.###", CultureInfo.InvariantCulture);
            numericEditorOriginalText = numericEditor.Text;
            UpdateNumericEditorBounds();
            numericEditor.Visible = true;
            numericEditor.BringToFront();
            numericEditor.Focus();
            numericEditor.SelectAll();
        }

        private void UpdateNumericEditorBounds()
        {
            if (numericEditor == null || numericEditorKind == 0) return;
            if (UseCompactLayout)
            {
                numericEditor.Bounds = Rectangle.Round(numericEditorKind == 1 ? CompactLayout.LengthValue : CompactLayout.VelocityValue);
                return;
            }
            RectangleF reference = numericEditorKind == 1
                ? new RectangleF(37F, 183F, 76F, 37F)
                : new RectangleF(37F, 280F, 76F, 37F);
            Rectangle target = ReferenceRectangleToClient(reference);
            numericEditor.Bounds = target;
        }

        private Rectangle ReferenceRectangleToClient(RectangleF reference)
        {
            RectangleF content = GetContentBounds(ClientRectangle);
            float scaleX = content.Width / ArtworkWidth, scaleY = content.Height / ArtworkHeight;
            return Rectangle.Round(new RectangleF(content.Left + reference.X * scaleX, content.Top + reference.Y * scaleY,
                reference.Width * scaleX, reference.Height * scaleY));
        }

        private void NumericEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitNumericEditor(true);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                CancelNumericEditor();
                Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void NumericEditorLostFocus(object sender, EventArgs e)
        {
            if (!numericEditorClosing) CommitNumericEditor(false);
        }

        private bool CommitNumericEditor(bool keepOpenOnError)
        {
            if (numericEditor == null || !numericEditor.Visible || numericEditorKind == 0 || numericEditorClosing) return true;
            // Opening and leaving a rounded display is not a parameter edit.
            // Keep the full precision used for timing and persisted geometry.
            if(String.Equals((numericEditor.Text??String.Empty).Trim(),numericEditorOriginalText,StringComparison.Ordinal)) {
                CancelNumericEditor();return true;
            }
            double value;
            string text = (numericEditor.Text ?? String.Empty).Trim().Replace('，', ',');
            bool valid = Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                         Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            valid = valid && IsFinite(value) && value > 0.0;
            valid=valid&&(numericEditorKind!=1||value<=50000);
            if (!valid)
            {
                numericEditor.BackColor = Color.MistyRose;
                SystemSounds.Beep.Play();
                if(keepOpenOnError)numericEditor.Focus();
                numericEditor.SelectAll();
                return false;
            }

            int kind = numericEditorKind;
            numericEditorClosing = true;
            numericEditor.Visible = false;
            numericEditorKind = 0;
            numericEditorClosing = false;
            ExecuteParameterAction(kind == 1 ? "CABLE_LENGTH" : "VELOCITY", delegate {
                if (kind == 1) CableLengthMeters = value;
                else PropagationVelocityMPerUs = value;
            });
            Invalidate();
            return true;
        }

        private void CancelNumericEditor()
        {
            if (numericEditor == null || numericEditorClosing) return;
            numericEditorClosing = true;
            numericEditor.Visible = false;
            numericEditorKind = 0;
            numericEditor.BackColor = Color.White;
            numericEditorClosing = false;
        }

        private bool HandleLayoutMouseDown(MouseEventArgs e)
        {
            if (UseCompactLayout) return false;
            if (e.Button != MouseButtons.Left) return false;
            PointF point;
            if (!TryReferencePoint(e.Location, out point)) return false;
            if (point.X >= CenterPanelLeft && point.X <= CenterPanelRight &&
                Math.Abs(point.Y - signalQualityPanelTop) <= 9F)
            {
                CancelNumericEditor();
                centerSplitterDragging = true;
                Capture = true;
                Cursor = Cursors.HSplit;
                return true;
            }
            return false;
        }

        private bool HandleLayoutMouseMove(MouseEventArgs e)
        {
            if (UseCompactLayout) return false;
            PointF point;
            if (!TryReferencePoint(e.Location, out point)) return centerSplitterDragging;
            if (centerSplitterDragging)
            {
                if ((e.Button & MouseButtons.Left) == 0)
                {
                    FinishCenterSplitterDrag();
                    return true;
                }
                SignalQualityPanelTop = point.Y;
                Cursor = Cursors.HSplit;
                return true;
            }
            if (point.X >= CenterPanelLeft && point.X <= CenterPanelRight &&
                Math.Abs(point.Y - signalQualityPanelTop) <= 7F)
            {
                Cursor = Cursors.HSplit;
                return true;
            }
            return false;
        }

        private bool HandleLayoutMouseUp(MouseEventArgs e)
        {
            PointF resetPoint;
            if(!UseCompactLayout&&e.Button==MouseButtons.Left&&TryReferencePoint(e.Location,out resetPoint)&&LegacyNoiseResetBounds.Contains(resetPoint)){ResetNoiseView();return true;}
            if (e.Button != MouseButtons.Left || !centerSplitterDragging) return false;
            PointF point;
            if (TryReferencePoint(e.Location, out point)) SignalQualityPanelTop = point.Y;
            FinishCenterSplitterDrag();
            return true;
        }

        private void FinishCenterSplitterDrag()
        {
            centerSplitterDragging = false;
            if (Capture) Capture = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        private void DrawDynamicCenterLayout(Graphics g, WaveformData waveform, WaveformData noiseWaveform,
            double snr, double peak, double noise)
        {
            using (var background = new SolidBrush(ColorTranslator.FromHtml("#F3F7FB")))
                g.FillRectangle(background, new RectangleF(240F, 65F, 1156F, 850F));

            float mainBottom = signalQualityPanelTop - 12F;
            DrawPanel(g, new RectangleF(CenterPanelLeft, CenterPanelTop,
                CenterPanelRight - CenterPanelLeft, mainBottom - CenterPanelTop));
            DrawPanel(g, new RectangleF(CenterPanelLeft, signalQualityPanelTop + 10F,
                CenterPanelRight - CenterPanelLeft, CenterPanelBottom - signalQualityPanelTop - 10F));

            DrawTdrHeader(g);
            DrawSignalQualityHeader(g);

            RectangleF plot = CurrentPlotRectangle;
            double x0 = 0.0, x1 = 2.2e-6, y0 = -1.5, y1 = 2.0;
            if (waveform != null) GetWaveformView(waveform, out x0, out x1, out y0, out y1);
            DrawWaveformAxes(g, plot, x0, x1, y0, y1);
            if(noiseWaveform==null)DrawNoiseAxes(g, CurrentNoisePlotRectangle);
            DrawQualityMetrics(g, snr, peak, noise);
            DrawCenterSplitter(g);
        }

        private static void DrawPanel(Graphics g, RectangleF rect)
        {
            using (var path = RoundedRectangle(rect, 10F))
            using (var fill = new SolidBrush(Color.White))
            using (var border = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
        }

        private static void DrawTdrHeader(Graphics g)
        {
            using (var blue = new SolidBrush(ColorTranslator.FromHtml("#00A3C7")))
            {
                g.FillRectangle(blue, new RectangleF(292F, 91F, 4F, 19F));
                g.FillRectangle(blue, new RectangleF(300F, 84F, 4F, 26F));
                g.FillRectangle(blue, new RectangleF(308F, 94F, 4F, 16F));
            }
            using (var title = CreateFont(15F, FontStyle.Bold))
            using (var subtitle = CreateFont(9.8F, FontStyle.Regular))
            using (var ink = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
            using (var muted = new SolidBrush(ColorTranslator.FromHtml("#60758A")))
            {
                g.DrawString(BuiltIn("TDR 波形"), title, ink, new PointF(329F, 82F));
                g.DrawString(BuiltIn("实时反射信号与定位标记"), subtitle, muted, new PointF(329F, 112F));
            }
            RectangleF badge = new RectangleF(905F, 85F, 82F, 31F);
            using (var path = RoundedRectangle(badge, 15F))
            using (var fill = new SolidBrush(ColorTranslator.FromHtml("#EAF2F8"))) g.FillPath(fill, path);
            using (var font = CreateFont(9.6F, FontStyle.Bold))
            using (var brush = new SolidBrush(ColorTranslator.FromHtml("#00A3C7"))) DrawCentered(g, "待校准", font, brush, badge);
        }

        private void DrawSignalQualityHeader(Graphics g)
        {
            float top = signalQualityPanelTop + 10F;
            using (var blue = new SolidBrush(ColorTranslator.FromHtml("#00A3C7")))
            {
                g.FillRectangle(blue, new RectangleF(292F, top + 25F, 4F, 16F));
                g.FillRectangle(blue, new RectangleF(300F, top + 17F, 4F, 24F));
                g.FillRectangle(blue, new RectangleF(308F, top + 29F, 4F, 12F));
            }
            using (var title = CreateFont(13F, FontStyle.Bold))
            using (var ink = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
                g.DrawString(BuiltIn("信号质量"), title, ink, new PointF(329F, top + 19F));

            DrawCheckBox(g, new PointF(1230F, top + 24F), NoiseBaselineVisible, CalibrationText("筛选阈值", "Screening threshold"));
            DrawCompactButton(g,LegacyNoiseResetBounds,CalibrationText("恢复原始视图","Reset view"),false,fontSize:8.5F);
        }

        private static void DrawCheckBox(Graphics g, PointF point, bool value, string label,float labelWidth=0)
        {
            RectangleF box = new RectangleF(point.X, point.Y, 20F, 20F);
            using (var path = RoundedRectangle(box, 3F))
            using (var fill = new SolidBrush(value ? ColorTranslator.FromHtml("#00A3C7") : Color.White))
            using (var border = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            if (value)
                using (var pen = new Pen(Color.White, 2F))
                    g.DrawLines(pen, new[] { new PointF(point.X + 4F, point.Y + 10F), new PointF(point.X + 8F, point.Y + 15F), new PointF(point.X + 16F, point.Y + 5F) });
            using (var font = CreateFont(9.4F, FontStyle.Regular))
            using (var brush = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
            {
                string caption=BuiltIn(label);
                if(labelWidth>0) {
                    var rect=new RectangleF(point.X+27F,point.Y-8F,labelWidth,36);
                    using(var format=ThemeCaptionFormat(g,caption,font,rect,false))g.DrawString(caption,font,brush,rect,format);
                } else g.DrawString(caption, font, brush, new PointF(point.X + 27F, point.Y + 1F));
            }
        }

        private void DrawCenterSplitter(Graphics g)
        {
            float y = signalQualityPanelTop;
            using (var pen = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F))
                g.DrawLine(pen, CenterPanelLeft + 15F, y, CenterPanelRight - 15F, y);
            RectangleF handle = new RectangleF(804F, y - 4F, 50F, 8F);
            using (var path = RoundedRectangle(handle, 4F))
            using (var fill = new SolidBrush(ColorTranslator.FromHtml("#CAD8E5"))) g.FillPath(fill, path);
        }

        private static void DrawWaveformAxes(Graphics g, RectangleF plot, double x0, double x1, double y0, double y1)
        {
            if (compactRendering) { DrawCompactAxes(g, plot, x0, x1, y0, y1); return; }
            using (var fill = new SolidBrush(ColorTranslator.FromHtml("#F3F7FB"))) g.FillRectangle(fill, plot);
            using (var grid = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F))
            {
                for (int i = 0; i <= 11; i++) g.DrawLine(grid, plot.Left + i * plot.Width / 11F, plot.Top, plot.Left + i * plot.Width / 11F, plot.Bottom);
                for (int i = 0; i <= 7; i++) g.DrawLine(grid, plot.Left, plot.Top + i * plot.Height / 7F, plot.Right, plot.Top + i * plot.Height / 7F);
            }
            using (var border = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F)) g.DrawRectangle(border, plot.X, plot.Y, plot.Width, plot.Height);
            using (var font = CreateFont(8.5F, FontStyle.Regular))
            using (var brush = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
            {
                for (int i = 0; i <= 11; i++)
                {
                    double value = x0 + (x1 - x0) * i / 11.0;
                    string label = FormatSeconds(value);
                    SizeF size = g.MeasureString(label, font);
                    g.DrawString(label, font, brush, plot.Left + i * plot.Width / 11F - size.Width / 2F, plot.Bottom + 7F);
                }
                for (int i = 0; i <= 7; i++)
                {
                    double value = y1 - (y1 - y0) * i / 7.0;
                    string label = value.ToString("0.##", CultureInfo.InvariantCulture);
                    SizeF size = g.MeasureString(label, font);
                    g.DrawString(label, font, brush, plot.Left - size.Width - 8F, plot.Top + i * plot.Height / 7F - size.Height / 2F);
                }
                using (var axisFont = CreateFont(10F, FontStyle.Regular))
                {
                    string xLabel = BuiltIn("时间")+" / s";
                    SizeF size = g.MeasureString(xLabel, axisFont);
                    g.DrawString(xLabel, axisFont, brush, plot.Left + (plot.Width - size.Width) / 2F, plot.Bottom + Px(27));
                    GraphicsState state = g.Save();
                    g.TranslateTransform(plot.Left - Px(48), plot.Top + plot.Height / 2F);
                    g.RotateTransform(-90F);
                    string amplitudeLabel=BuiltIn("幅值")+" / V";
                    size = g.MeasureString(amplitudeLabel, axisFont);
                    g.DrawString(amplitudeLabel, axisFont, brush, -size.Width / 2F, 0F);
                    g.Restore(state);
                }
            }
        }

        private static string FormatSeconds(double seconds)
        {
            if (Math.Abs(seconds) < 0.5e-9) return "0";
            string unit=TimeAxisUnit(0,seconds);
            return FormatTimeAxisTick(seconds,unit)+" "+unit;
        }

        private RectangleF incidentBadgeHit, reflectionBadgeHit, velocityBadgeHit;
        private MarkerData paintedBadgeMarkers;
        private void DrawWaveformBadges(Graphics g, RectangleF plot, MarkerData markers,
            TdrAutoAnalysisResult analysis, double velocity, double xMinimum, double xMaximum)
        {
            if (markers == null || xMaximum <= xMinimum) return;
            float incidentX = plot.Left + (float)((markers.StartTimeSeconds - xMinimum) / (xMaximum - xMinimum)) * plot.Width;
            float distanceX = plot.Left + (float)((markers.DistanceMarkerSeconds - xMinimum) / (xMaximum - xMinimum)) * plot.Width;
            float velocityX = plot.Left + (float)((markers.VelocityMarkerSeconds - xMinimum) / (xMaximum - xMinimum)) * plot.Width;
            incidentBadgeHit = DrawInfoBadge(g, plot, incidentX + 10F, plot.Top + 12F, 102F, 48F,
                CalibrationText("入射波峰", "Incident peak"), FormatSeconds(markers.StartTimeSeconds), ColorTranslator.FromHtml("#C44747"), Color.FromArgb(255, 240, 239));
            reflectionBadgeHit = DrawInfoBadge(g, plot, distanceX - 118F, plot.Top + 132F, 108F, 50F,
                CalibrationText("反射波峰", "Reflection peak"), FormatSeconds(markers.DistanceMarkerSeconds),
                ColorTranslator.FromHtml("#00A3C7"), Color.FromArgb(235, 245, 255));
            velocityBadgeHit = DrawInfoBadge(g, plot, velocityX + 10F, plot.Top + 12F, 118F, 48F,
                "波速", velocity.ToString("0.0", CultureInfo.InvariantCulture) + " m/μs",
                ColorTranslator.FromHtml("#238A67"), Color.FromArgb(236, 250, 236));
            if (compactRendering && EmbeddedMode) { incidentBadgeHit.Offset(0, -40); reflectionBadgeHit.Offset(0, -40); velocityBadgeHit.Offset(0, -40); }
            paintedBadgeMarkers = markers;
        }

        private static RectangleF DrawInfoBadge(Graphics g, RectangleF plot, float x, float y, float width, float height,
            string title, string value, Color accent, Color fillColor)
        {
            using (var font = CreateFont(9.5F, FontStyle.Bold))
            {
                width = Math.Max(width, Math.Max(g.MeasureString(title, font).Width, g.MeasureString(value, font).Width) + Px(16));
                height = Math.Max(height, font.GetHeight(g) * 2 + Px(12));
            }
            x = Math.Max(plot.Left + 4F, Math.Min(plot.Right - width - 4F, x));
            y = Math.Max(plot.Top + 4F, Math.Min(plot.Bottom - height - 4F, y));
            RectangleF rect = new RectangleF(x, y, width, height);
            using (var path = RoundedRectangle(rect, 6F))
            using (var fill = new SolidBrush(fillColor))
            using (var border = new Pen(Color.FromArgb(150, accent), 1F))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using (var titleFont = CreateFont(8.2F, FontStyle.Bold))
            using (var valueFont = CreateFont(10.2F, FontStyle.Bold))
            using (var brush = new SolidBrush(accent))
            {
                g.DrawString(BuiltIn(title), titleFont, brush, new PointF(rect.X + Px(8), rect.Y + Px(4)));
                g.DrawString(value, valueFont, brush, new PointF(rect.X + Px(8), rect.Y + Px(4) + titleFont.GetHeight(g)));
            }
            return rect;
        }

        private static void DrawNoiseAxes(Graphics g, RectangleF plot)
        {
            using (var fill = new SolidBrush(ColorTranslator.FromHtml("#F3F7FB"))) g.FillRectangle(fill, plot);
            using (var grid = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F))
            {
                for (int i = 0; i <= 8; i++) g.DrawLine(grid, plot.Left + i * plot.Width / 8F, plot.Top, plot.Left + i * plot.Width / 8F, plot.Bottom);
                for (int i = 0; i <= 2; i++) g.DrawLine(grid, plot.Left, plot.Top + i * plot.Height / 2F, plot.Right, plot.Top + i * plot.Height / 2F);
            }
            using (var border = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F)) g.DrawRectangle(border, plot.X, plot.Y, plot.Width, plot.Height);
            using (var font = CreateFont(8F, FontStyle.Regular))
            using (var brush = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
            {
                g.DrawString("0.2", font, brush, plot.Left - 28F, plot.Top - 4F);
                g.DrawString("0", font, brush, plot.Left - 18F, plot.Top + plot.Height / 2F - 7F);
                g.DrawString("-0.2", font, brush, plot.Left - 32F, plot.Bottom - 12F);
            }
        }

        private void DrawNoiseBaselineOverlay(Graphics g)
        {
            RectangleF plot = CurrentNoisePlotRectangle;
            if(noiseViewSource==null||noiseY1<=noiseY0)return;
            double threshold=DisplayNoiseThreshold;
            using (var pen = new Pen(ColorTranslator.FromHtml("#238A67"), 1.6F))
            using (var fill = new SolidBrush(Color.White))
            using (var ink = new SolidBrush(ColorTranslator.FromHtml("#238A67")))
            using (var font = CreateFont(8.5F,FontStyle.Bold))
            {
                pen.DashStyle = DashStyle.Dash;
                foreach(double value in threshold>0?new[]{threshold,-threshold}:new[]{0d}) {
                    if(value<noiseY0||value>noiseY1)continue;
                    float y=NoiseValueY(value);
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    g.FillPolygon(ink,new[]{new PointF(plot.Right-9,y-5),new PointF(plot.Right-1,y),new PointF(plot.Right-9,y+5)});
                    string text=threshold>0?CalibrationText("筛选阈值 ","Screening ")+value.ToString("G4",CultureInfo.InvariantCulture)+" V":CalibrationText("自动阈值 · 拖动可手动设置","Auto threshold · drag to set");
                    var size=g.MeasureString(text,font);float labelY=Math.Max(plot.Top,Math.Min(plot.Bottom-size.Height,y-size.Height-2));
                    g.FillRectangle(fill,plot.Left+6,labelY,size.Width+8,size.Height);
                    g.DrawString(text,font,ink,plot.Left+10,labelY);
                }
            }
        }

        private void DrawQualityMetrics(Graphics g, double snr, double peak, double noise)
        {
            float y = Math.Min(807F, signalQualityPanelTop + 128F);
            RectangleF[] rects =
            {
                new RectangleF(877F, y - 19F, 125F, 82F),
                new RectangleF(1040F, y - 19F, 125F, 82F),
                new RectangleF(1208F, y - 19F, 125F, 82F)
            };
            string[] names = { "信噪比", "峰值", "噪声" };
            string[] values =
            {
                snr.ToString("0.0", CultureInfo.InvariantCulture) + " dB",
                peak.ToString("0.00", CultureInfo.InvariantCulture) + " V",
                noise.ToString("0.000", CultureInfo.InvariantCulture) + " V"
            };
            Color[] colors = { ColorTranslator.FromHtml("#238A67"), ColorTranslator.FromHtml("#00A3C7"), ColorTranslator.FromHtml("#18334D") };
            for (int i = 0; i < rects.Length; i++)
            {
                using (var path = RoundedRectangle(rects[i], 8F))
                using (var fill = new SolidBrush(Color.White))
                using (var border = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);
                }
                using (var nameFont = CreateFont(9.2F, FontStyle.Regular))
                using (var valueFont = CreateFont(17F, FontStyle.Bold))
                using (var ink = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
                using (var valueBrush = new SolidBrush(colors[i]))
                {
                    g.DrawString(BuiltIn(names[i]), nameFont, ink, new PointF(rects[i].X + 13F, rects[i].Y + 10F));
                    g.DrawString(values[i], valueFont, valueBrush, new PointF(rects[i].X + 13F, rects[i].Y + 35F));
                }
            }
        }
    }
}
