using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        [ThreadStatic] private static bool compactRendering;
        [ThreadStatic] private static bool legacyRendering;
        private Rectangle compactRenderBounds;
        private bool compactQualityTab;
        private bool compactLevelsOpen;
        private bool embeddedMode;
        // All three bottom actions share the existing primary-action typography.
        // The requested 30% emphasis changes the button height, not its font size.
        private const float CompactActionFontSize = 10f;
        private const float CompactPrimaryActionHeight = 94f;
        [System.ComponentModel.Category("布局")]
        [System.ComponentModel.DefaultValue(false)]
        public bool EmbeddedMode
        {
            get { return embeddedMode; }
            set
            {
                RunOnUiThread(delegate
                {
                    if (embeddedMode == value) return;
                    CancelNumericEditor();
                    EndPlotGesture();
                    compactLevelsOpen = false;
                    embeddedMode = value;
                    UpdateNumericEditorBounds();
                    Invalidate();
                });
            }
        }
        private static bool IsCompactSize(Rectangle bounds) { return bounds.Width < 1500 || bounds.Height < 820; }
        private bool UseCompactLayout { get { return !legacyRendering && (EmbeddedMode || compactRendering || IsCompactSize(ClientRectangle)); } }
        private CompactGeometry CompactLayout
        {
            get
            {
                if (compactRendering) return new CompactGeometry(compactRenderBounds);
                Rectangle bounds = ClientRectangle;
                if (EmbeddedMode) bounds.Height += 40;
                return new CompactGeometry(bounds, EmbeddedMode ? -40 : 0);
            }
        }

        // Pixel geometry is shared by rendering and hit testing. Font sizes never depend on
        // the artwork scale, so a short embedded viewport keeps readable numbers and units.
        private sealed class CompactGeometry
        {
            internal readonly RectangleF Left, Center, Right, Plot, NoisePlot, LengthValue, VelocityValue;
            internal readonly RectangleF TdrTab, QualityTab, Level, AllLevels, Start, Delete, Bandwidth, Save, VelocityReview, ConfirmJoints, SegmentMode, PulseMode, Assessment;
            private readonly float verticalOffset;
            internal CompactGeometry(Rectangle bounds, float offset = 0)
            {
                verticalOffset = offset;
                float h = bounds.Height, w = bounds.Width;
                Left = new RectangleF(8, 48, 218, h - 56);
                Right = new RectangleF(w - 226, 48, 218, h - 56);
                Center = new RectangleF(234, 48, Math.Max(200, w - 468), h - 56);
                float tabWidth = Math.Min(184, Math.Max(124, Center.Width * .3F));
                TdrTab = new RectangleF(Center.Left + 10, 58, tabWidth, 34);
                QualityTab = new RectangleF(TdrTab.Right + 8, 58, 100, 34);
                SegmentMode = new RectangleF(Center.Right - 290, 101, 78, 34);
                PulseMode = new RectangleF(Center.Right - 208, 101, 82, 34);
                Assessment = new RectangleF(Center.Right - 122, 101, 108, 34);
                Plot = new RectangleF(Center.Left + 68, 138, Center.Width - 104, Math.Max(80, h - 218));
                NoisePlot = new RectangleF(Center.Left + 60, 244, Center.Width - 86, Math.Max(60, h - 304));
                LengthValue = new RectangleF(20, 115, 116, 34);
                VelocityValue = new RectangleF(20, 181, 116, 34);
                Level = new RectangleF(Right.Left + 12, 182, Right.Width - 24, 36);
                AllLevels = new RectangleF(Right.Left + 12, 228, Right.Width - 24, 32);
                float actionHeight=Math.Max(44,Math.Min(CompactPrimaryActionHeight,(h-280)/2-8));
                Start = new RectangleF(Right.Left + 12, h - 20-actionHeight, Right.Width - 24, actionHeight);
                Delete = RectangleF.Empty;
                Save = new RectangleF(Right.Left + 12, Start.Top-8-40, Right.Width - 24, 40);
                ConfirmJoints = new RectangleF(Save.Left, Save.Top-8-36, Save.Width, 36);
                VelocityReview = new RectangleF(20, h - 102, 194, 54);
                Bandwidth = new RectangleF(22, 324, 182, 24);
                if (offset != 0)
                {
                    Left = Shift(Left, offset); Center = Shift(Center, offset); Right = Shift(Right, offset);
                    Plot = Shift(Plot, offset); NoisePlot = Shift(NoisePlot, offset);
                    LengthValue = Shift(LengthValue, offset); VelocityValue = Shift(VelocityValue, offset);
                    TdrTab = Shift(TdrTab, offset); QualityTab = Shift(QualityTab, offset);
                    Level = Shift(Level, offset); AllLevels = Shift(AllLevels, offset);
                    Start = Shift(Start, offset); Delete = Shift(Delete, offset); Bandwidth = Shift(Bandwidth, offset);
                    Save = Shift(Save, offset); VelocityReview = Shift(VelocityReview, offset);
                    ConfirmJoints = Shift(ConfirmJoints, offset); SegmentMode = Shift(SegmentMode, offset); PulseMode = Shift(PulseMode, offset);
                    Assessment = Shift(Assessment, offset);
                }
            }
            private static RectangleF Shift(RectangleF value, float y) { value.Offset(0, y); return value; }
            internal RectangleF Phase(int i) { return new RectangleF(Right.Left + 12 + i * 48, 88 + verticalOffset, 48, 34); }
            internal RectangleF PopupRow(int i, int count)
            {
                float rowHeight = Math.Min(30, Math.Max(18, (Right.Height - 24) / Math.Max(1, count)));
                float top = Math.Max(Right.Top + 8, Math.Min(Level.Bottom + 2, Right.Bottom - count * rowHeight - 8));
                return new RectangleF(Right.Left + 8, top + i * rowHeight, Right.Width - 16, rowHeight);
            }
        }

        private void RenderCompactFrame(Graphics g, Rectangle bounds)
        {
            GraphicsState saved = g.Save();
            float previousScale = themeRenderScale;
            bool previousCompact = compactRendering;
            Rectangle previousBounds = compactRenderBounds;
            if (EmbeddedMode)
            {
                bounds.Height += 40;
                g.TranslateTransform(0, -40);
            }
            compactRendering = true; compactRenderBounds = bounds; themeRenderScale = 1;
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                CompactGeometry ui = CompactLayout;
                int phaseIndex; string level; PhaseState phase; double snr, peak, noise; bool allLevels;
                WaveformData waveform = null, noiseWaveform; MarkerData markers;
                lock (stateLock)
                {
                    phaseIndex = selectedPhaseIndex; level = selectedLevelCode;
                    phase = GetPhaseStateNoLock(phaseIndex).CopyShallow();
                    snr = signalToNoiseDb; peak = peakVoltageV; noise = noiseVoltageV; allLevels = showAllLevels;
                    if (phase.WideBand) phase.FilteredWaveforms.TryGetValue(level, out waveform);
                    if (waveform == null) phase.Waveforms.TryGetValue(level, out waveform);
                    phase.Markers.TryGetValue(level, out markers); noiseWaveform = phase.Noise;
                }
                if (!EmbeddedMode)
                {
                    using (var brush = new SolidBrush(Theme.TopBar)) g.FillRectangle(brush, 0, 0, bounds.Width, 40);
                    ThemeText(g, CalibrationText("电缆振荡波校准系统","DAC cable calibration"), new RectangleF(14, 0, 370, 40), Color.White, 12, FontStyle.Bold);
                    ThemeText(g, CalibrationText("相别", "Phase") + " " + PhaseCodeFromIndex(phaseIndex) + "  ·  " + LevelLabels[Array.IndexOf(LevelCodes, level)],
                        new RectangleF(bounds.Width - 320, 0, 308, 40), Color.White, center: true);
                }
                ThemeSection(g, ui.Left, "校准参数");
                ThemeBox(g, ui.Center, Theme.Surface, Theme.Border);
                ThemeSection(g, ui.Right, "相别");
                if(!IsCapacitiveLoad) {
                ThemeText(g, CalibrationText("电缆长度", "Cable length") + " · " + PhaseCodeFromIndex(phaseIndex), new RectangleF(20, 82, 194, 30), Theme.Ink);
                DrawCompactInput(g, ui.LengthValue, phase.CableLengthMeters, "m");
                ThemeText(g, "波速", new RectangleF(20, 151, 194, 28), Theme.Ink);
                DrawCompactInput(g, ui.VelocityValue, phase.VelocityMPerUs, "m/μs");
                } else {
                    ThemeText(g,CalibrationText("电容局放校准","Capacitor PD calibration"),new RectangleF(20,82,194,30),Theme.Ink);
                    ThemeText(g,CalibrationText("按入射信号计算系数","Coefficient from incident pulse"),new RectangleF(20,115,194,30),Theme.Muted,8.5f);
                    ThemeText(g,CalibrationText("不进行电缆测距","Cable ranging not applicable"),new RectangleF(20,148,194,30),Theme.Muted,8.5f);
                }
                ThemeText(g, "短电缆", new RectangleF(20, 221, 130, 30), Theme.Ink);
                DrawToggle(g, new RectangleF(166, 224, 42, 24), phase.ShortCable);
                ThemeText(g, CalibrationText("显示手动调节", "Manual controls"), new RectangleF(20, 257, 140, 30), Theme.Ink,8.5f);
                DrawToggle(g, new RectangleF(166, 260, 42, 24), phase.ManualBandwidthPanelVisible);
                double bandwidthDisplay = bandwidthSliderDragging ? bandwidthSliderPreviewMHz : phase.BandwidthCutoffMHz;
                ThemeText(g,ActualBandwidthText(phase),new RectangleF(20,289,194,30),Theme.Ink,8.5F);
                if (phase.ManualBandwidthPanelVisible)
                {
                    using (var pen = new Pen(Theme.Border, 4)) g.DrawLine(pen, ui.Bandwidth.Left, 336, ui.Bandwidth.Right, 336);
                    float x = ui.Bandwidth.Left + (float)((bandwidthDisplay - 1) / 59) * ui.Bandwidth.Width;
                    using (var brush = new SolidBrush(Theme.Accent)) g.FillEllipse(brush, x - 7, 329, 14, 14);
                    ThemeText(g, "1 MHz", new RectangleF(20, 347, 85, 25), Theme.Muted, 8.5F);
                    ThemeText(g, "60 MHz", new RectangleF(132, 347, 82, 25), Theme.Muted, 8.5F, center: true);
                }
                ThemeText(g, "参数按相别独立保存", new RectangleF(20, bounds.Height - 46, 194, 40), Theme.Muted, 8.5F);
                if(!IsCapacitiveLoad) DrawCompactButton(g, ui.VelocityReview, CalibrationText("核对本相波速", "Review phase\nvelocity"), false, null, true);
                string[] phases = { "ALL", "A", "B", "C" };
                Color[] phaseColors = { Theme.Accent, Color.Goldenrod, Color.ForestGreen, Color.Red };
                for (int i = 0; i < 4; i++)
                {
                    RectangleF r = ui.Phase(i);
                    DrawCompactButton(g, r, phases[i], i == phaseIndex + 1);
                    using (var brush = new SolidBrush(phaseColors[i])) g.FillRectangle(brush, r.Left + 8, r.Bottom - 3, r.Width - 16, 3);
                }
                ThemeText(g, "校准档位", new RectangleF(ui.Right.Left + 12, 146, 194, 30), Theme.Ink, 10.5F, FontStyle.Bold);
                ThemeText(g, CalibrationText("当前：", "Current: ")+LevelLabels[Array.IndexOf(LevelCodes, level)], ui.Level, Theme.Ink);
                DrawCheckBox(g, new PointF(ui.AllLevels.Left, ui.AllLevels.Top + 6), allLevels, CalibrationText("显示全部档位", "Show all levels"),ui.AllLevels.Width-27);
                DrawCompactLevelList(g,ui,level);
                if(!IsCapacitiveLoad) DrawCompactActionButton(g, ui.ConfirmJoints, CalibrationText("确认接头", "Review joints"), false);
                DrawCompactActionButton(g, ui.Save, CalibrationSaveCaption, false,
                    !SelectedCalibrationHasData?Theme.Muted:SelectedCalibrationSaved?Theme.Danger:Theme.Ink);
                DrawCompactActionButton(g, ui.Start, CalibrationStartCaption, true);
                DrawCompactButton(g, ui.TdrTab, CalibrationWaveformCaption, !compactQualityTab);
                DrawCompactButton(g, ui.QualityTab, CalibrationText("噪声评估", "Noise evaluation"), compactQualityTab);
                if (compactQualityTab)
                {
                    DrawCompactButton(g,NoiseModeBounds(ui,0),CalibrationText("放大","Zoom"),!noiseTemplateMode,fontSize:8.5F);
                    DrawCompactButton(g,NoiseModeBounds(ui,1),CalibrationText("选取干扰","Select noise"),noiseTemplateMode,fontSize:8.5F);
                    DrawCompactButton(g,NoiseModeBounds(ui,2),CalibrationText("自动阈值","Auto threshold"),!ManualNoiseThreshold(SelectedPhaseIndex).HasValue,fontSize:8.5F);
                    DrawCompactButton(g,NoiseResetBounds(ui),CalibrationText("恢复原始视图","Reset view"),false,fontSize:8.5F);
                    var thresholdCheck=NoiseThresholdCheckBounds(ui);
                    DrawCheckBox(g, new PointF(thresholdCheck.Left,thresholdCheck.Top+4), NoiseBaselineVisible, NoiseThresholdCaption,thresholdCheck.Width-27);
                    string[] titles = { "信噪比", "峰值", "噪声" };
                    string[] values = { SignalToNoiseMetricCaption(snr), MetricCaption(peak, "0.00", "V"), MetricCaption(noise, "0.000", "V") };
                    float width = (ui.Center.Width - 40) / 3;
                    for (int i = 0; i < 3; i++)
                    {
                        RectangleF r = new RectangleF(ui.Center.Left + 12 + i * (width + 8), 144, width, 66);
                        ThemeBox(g, r, Theme.Canvas, Theme.Border);
                        ThemeText(g, titles[i], new RectangleF(r.Left + 8, r.Top + 3, r.Width - 16, 28), Theme.Muted);
                        ThemeText(g, values[i], new RectangleF(r.Left + 8, r.Top + 32, r.Width - 16, 30), Theme.Ink, 9.5F, FontStyle.Bold);
                    }
                    // Draw exactly one set of axes. The old fixed +/-0.2 V
                    // labels otherwise remained behind the measured scale.
                    if (noiseWaveform != null) DrawNoiseOverlay(g, noiseWaveform);
                    else DrawWaveformAxes(g, ui.NoisePlot, 0, .04, -.2, .2);
                    if (NoiseBaselineVisible) DrawNoiseBaselineOverlay(g);
                }
                else
                {
                    double x0 = 0, x1 = 10 * phase.CableLengthMeters / phase.VelocityMPerUs * 1e-6, y0 = -1.5, y1 = 2;
                    if (waveform != null) GetWaveformView(waveform, out x0, out x1, out y0, out y1);
                    bool warning = waveform != null && DisplayWindowStatus != "Ready";
                    if (!IsCapacitiveLoad) DrawCheckBox(g, new PointF(ui.QualityTab.Right + 16, ui.QualityTab.Top + 6), JointMarkersVisible, CalibrationText("显示接头", "Show joints"));
                    ThemeText(g, warning ? DisplayWindowMessage : IsCapacitiveLoad?CalibrationText("入射信号与校准质量 · V","Incident signal and calibration quality · V"):CalibrationText("实时反射信号与定位标记 · V","Reflection signal and location markers · V"), new RectangleF(ui.Center.Left + 16, 101, Math.Max(20,ui.Center.Width - 320), 28), Theme.Muted, 8.5F);
                    DrawCompactButton(g, ui.SegmentMode, "段模式", AcquisitionMode == "Segment",fontSize:8.5F);
                    DrawCompactButton(g, ui.PulseMode, "脉冲模式", AcquisitionMode == "Pulse",fontSize:8.5F);
                    DrawCompactButton(g, ui.Assessment, CalibrationText("校准评估", "Assessment"), false, waveform == null ? Theme.Muted : Theme.Accent, fontSize:9F);
                    TdrAutoAnalysisResult analysis; phase.Analyses.TryGetValue(level, out analysis);
                    if (waveform != null) DrawWaveformOverlay(g, waveform, markers, analysis, phase.VelocityMPerUs);
                    else DrawCompactAxes(g, ui.Plot, x0, x1, y0, y1);
                    if (!IsCapacitiveLoad && waveform != null && markers != null) DrawJointMarkers(g, waveform, phase.Joints, markers, phase.VelocityMPerUs);
                    if (waveform != null) DrawMeasurementInformation(g, ui.Plot, phaseIndex, level, phase, waveform, markers, analysis);
                    else { measurementInformationHit=RectangleF.Empty; measurementInformationText=String.Empty; }
                    if(waveform!=null)DrawPendingReflectionSelection(g,ui.Plot,x0,x1);
                }
                if (compactLevelsOpen)
                {
                    string[] visible = GetVisibleLevelCodes();
                    for (int i = 0; i < visible.Length; i++)
                        DrawCompactButton(g, ui.PopupRow(i, visible.Length), LevelLabels[Array.IndexOf(LevelCodes, visible[i])], visible[i] == level);
                }
            }
            finally { themeRenderScale = previousScale; compactRendering = previousCompact; compactRenderBounds = previousBounds; g.Restore(saved); }
        }

        private static void DrawCompactButton(Graphics g, RectangleF r, string text, bool selected, Color? ink = null, bool multiline = false,float fontSize=9.5f)
        {
            ThemeButtonBox(g, r, selected ? Theme.Accent : Theme.Surface, Theme.Border);
            if (!multiline) ThemeText(g, text, r, selected ? Color.White : ink ?? Theme.Ink,fontSize, center: true);
            else using (var font = ThemeFont(10f, FontStyle.Regular))
            using (var brush = new SolidBrush(selected ? Color.White : ink ?? Theme.Ink))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(BuiltIn(text), font, brush, RectangleF.Inflate(r, -4, -2), format);
        }

        private static void DrawCompactActionButton(Graphics g, RectangleF r, string text, bool primary, Color? ink = null)
        {
            ThemeButtonBox(g, r, primary ? Theme.Accent : Theme.Surface, Theme.Border);
            using (var font = ThemeFont(CompactActionFontSize, FontStyle.Regular))
            using (var brush = new SolidBrush(primary ? Color.White : ink ?? Theme.Ink))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(BuiltIn(text), font, brush, RectangleF.Inflate(r, -4, -2), format);
        }

        private static void DrawCompactInput(Graphics g, RectangleF valueBox, double value, string unit)
        {
            ThemeBox(g, new RectangleF(valueBox.Left, valueBox.Top, 194, valueBox.Height), Theme.Surface, Theme.Border);
            string text = IsFinite(value) ? value.ToString(unit=="m"?"0.0":"0.0##", CultureInfo.InvariantCulture) : "—";
            using (var font = ThemeFont(9.5F, FontStyle.Regular))
                if (g.MeasureString(text, font).Width > valueBox.Width - 6) text = value.ToString("0.##E+0", CultureInfo.InvariantCulture);
            ThemeText(g, text, new RectangleF(valueBox.Left + 4, valueBox.Top, valueBox.Width - 4, valueBox.Height), Theme.Ink);
            float divider = valueBox.Right + 16;
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, divider, valueBox.Top, divider, valueBox.Bottom);
            ThemeText(g, unit, new RectangleF(divider + 3, valueBox.Top, 58, valueBox.Height), Theme.Ink, center: true);
            ThemeText(g, "⌃", new RectangleF(valueBox.Right - 2, valueBox.Top - 2, 19, 20), Theme.Muted, 8.5F, center: true);
            ThemeText(g, "⌄", new RectangleF(valueBox.Right - 2, valueBox.Top + 15, 19, 20), Theme.Muted, 8.5F, center: true);
        }

        private static void DrawCompactAxes(Graphics g, RectangleF plot, double x0, double x1, double y0, double y1)
        {
            ThemeBox(g, plot, Theme.Canvas, Theme.Border);
            int columns = plot.Width < 650 ? 5 : 8;
            using (var pen = new Pen(Theme.Border))
            {
                for (int i = 1; i < columns; i++) g.DrawLine(pen, plot.Left + i * plot.Width / columns, plot.Top, plot.Left + i * plot.Width / columns, plot.Bottom);
                for (int i = 1; i < 4; i++) g.DrawLine(pen, plot.Left, plot.Top + i * plot.Height / 4, plot.Right, plot.Top + i * plot.Height / 4);
            }
            string timeUnit=TimeAxisUnit(x0,x1);
            for (int i = 0; i <= columns; i++)
            {
                string label = FormatTimeAxisTick(x0 + (x1 - x0) * i / columns,timeUnit);
                ThemeText(g, label, new RectangleF(plot.Left + i * plot.Width / columns - 36, plot.Bottom + 3, 72, 28), Theme.Ink, center: true);
            }
            for (int i = 0; i <= 4; i++)
                ThemeText(g, (y1 - (y1 - y0) * i / 4).ToString("G4", CultureInfo.InvariantCulture),
                    new RectangleF(plot.Left - 60, plot.Top + i * plot.Height / 4 - 14, 54, 28), Theme.Ink, center: true);
            ThemeText(g, BuiltIn("时间")+" / "+timeUnit, new RectangleF(plot.Left + plot.Width / 2 - 75, plot.Bottom + 32, 150, 28), Theme.Ink, center: true);
        }

        private static string TimeAxisUnit(double first,double last)
        {
            double span=Math.Max(Math.Abs(last-first),Math.Max(Math.Abs(first),Math.Abs(last)));
            return span>=1?"s":span>=.001?"ms":span>=.000001?"μs":"ns";
        }
        private static string FormatTimeAxisTick(double seconds,string unit)
        {
            double scale=unit=="ms"?1e3:unit=="μs"?1e6:unit=="ns"?1e9:1;
            double value=seconds*scale;
            return Math.Abs(value)<.0005?"0":value.ToString("0.###",CultureInfo.InvariantCulture);
        }

        private bool HandleCompactMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;
            CompactGeometry ui = CompactLayout; PointF p = e.Location;
            if(compactQualityTab&&NoiseResetBounds(ui).Contains(p)){ResetNoiseView();return true;}
            if (compactLevelsOpen)
            {
                string[] visible = GetVisibleLevelCodes();
                for (int i = 0; i < visible.Length; i++) if (ui.PopupRow(i, visible.Length).Contains(p))
                {
                    string code = visible[i]; compactLevelsOpen = false;
                    ExecuteParameterAction("LEVEL", delegate { SetSelectedLevel(code); }); Invalidate(); return true;
                }
                compactLevelsOpen = false; Invalidate(); return true;
            }
            if (ui.TdrTab.Contains(p) || ui.QualityTab.Contains(p))
            { compactQualityTab = ui.QualityTab.Contains(p); EndPlotGesture(); CancelNumericEditor(); Invalidate(); return true; }
            if(!compactQualityTab&&ui.Assessment.Contains(p)){ShowMeasurementAssessment();return true;}
            if(!compactQualityTab&&!IsCapacitiveLoad&&JointVisibilityBounds.Contains(p)){JointMarkersVisible=!JointMarkersVisible;return true;}
            if(HandleCompactLevelClick(p,ui))return true;
            for (int i = 0; i < 4; i++) if (ui.Phase(i).Contains(p))
            { int option = i; ExecuteParameterAction("PHASE", delegate { SetSelectedPhaseOption(option); }); return true; }
            if (ui.Level.Contains(p)) {
                int rows=CompactLevelRows(CompactLevelListBounds(ui));compactLevelOffset=Math.Max(0,Math.Min(Array.IndexOf(GetVisibleLevelCodes(),SelectedLevelCode),GetVisibleLevelCodes().Length-rows));
                CancelNumericEditor(); Invalidate(); return true;
            }
            if (ui.AllLevels.Contains(p)) { ShowAllLevels = !ShowAllLevels; return true; }
            if (ui.Start.Contains(p)) { RequestStart(); return true; }
            if (ui.Save.Contains(p)) { if(!RequestSaveOrDelete() && !String.IsNullOrWhiteSpace(LastCalibrationError))MessageBox.Show(FindForm(),LastCalibrationError,CalibrationText("保存未完成","Save not completed"),MessageBoxButtons.OK,MessageBoxIcon.Warning); return true; }
            if (!IsCapacitiveLoad && ui.ConfirmJoints.Contains(p)) { ShowJointConfirmation(); return true; }
            if (!compactQualityTab && (ui.SegmentMode.Contains(p) || ui.PulseMode.Contains(p)))
            { AcquisitionMode = ui.SegmentMode.Contains(p) ? "Segment" : "Pulse"; Invalidate(); return true; }
            if (!IsCapacitiveLoad && ui.VelocityReview.Contains(p)) { ShowPhaseVelocityReview(); return true; }
            if (!IsCapacitiveLoad && ui.LengthValue.Contains(p)) { ShowNumericEditor(1); return true; }
            if (!IsCapacitiveLoad && ui.VelocityValue.Contains(p)) { ShowNumericEditor(2); return true; }
            for (int i = 0; !IsCapacitiveLoad && i < 2; i++)
            {
                RectangleF value = i == 0 ? ui.LengthValue : ui.VelocityValue;
                if (new RectangleF(value.Right, value.Top, 16, value.Height).Contains(p))
                {
                    bool length = i == 0; double increment = p.Y < value.Top + value.Height / 2 ? 1 : -1;
                    ExecuteParameterAction(length ? "CABLE_LENGTH" : "VELOCITY", delegate {
                        if (length) CableLengthMeters = Math.Max(0.1, CableLengthMeters + increment);
                        else PropagationVelocityMPerUs = Math.Max(0.1, PropagationVelocityMPerUs + increment * 0.5);
                    }); return true;
                }
            }
            float offset = EmbeddedMode ? -40 : 0;
            if (new RectangleF(160, 219 + offset, 54, 34).Contains(p)) { ExecuteParameterAction("SHORT_CABLE", delegate { ShortCable = !ShortCable; }); return true; }
            if (new RectangleF(160, 255 + offset, 54, 34).Contains(p)) { ManualBandwidthPanelVisible = !ManualBandwidthPanelVisible; return true; }
            if (ManualBandwidthPanelVisible && ui.Bandwidth.Contains(p))
            {
                double value = Math.Max(1, Math.Min(60, 1 + (p.X - ui.Bandwidth.Left) * 59 / ui.Bandwidth.Width));
                ExecuteParameterAction("BANDWIDTH", delegate { SetPhaseBandwidth(SelectedPhaseIndex, true, value); }); return true;
            }
            if(compactQualityTab) {
                if(NoiseModeBounds(ui,0).Contains(p)){noiseTemplateMode=false;Invalidate();return true;}
                if(NoiseModeBounds(ui,1).Contains(p)){noiseTemplateMode=true;Invalidate();return true;}
                if(NoiseModeBounds(ui,2).Contains(p)){RestoreAutomaticNoiseThreshold();return true;}
            }
            if (compactQualityTab && NoiseThresholdCheckBounds(ui).Contains(p))
            { NoiseBaselineVisible = !NoiseBaselineVisible; Invalidate(); return true; }
            return compactQualityTab || !ui.Plot.Contains(p);
        }
    }
}
