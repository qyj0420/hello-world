using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private static class Theme
        {
            internal static readonly Color TopBar = ColorTranslator.FromHtml("#0B2D4D");
            internal static readonly Color SectionBar = ColorTranslator.FromHtml("#EAF2F8");
            internal static readonly Color Accent = ColorTranslator.FromHtml("#00A3C7");
            internal static readonly Color AccentHover = ColorTranslator.FromHtml("#0089AA");
            internal static readonly Color Canvas = ColorTranslator.FromHtml("#F3F7FB");
            internal static readonly Color Surface = Color.White;
            internal static readonly Color Border = ColorTranslator.FromHtml("#CAD8E5");
            internal static readonly Color Ink = ColorTranslator.FromHtml("#18334D");
            internal static readonly Color Muted = ColorTranslator.FromHtml("#60758A");
            internal static readonly Color Danger = ColorTranslator.FromHtml("#C44747");
            internal static readonly Color Success = ColorTranslator.FromHtml("#238A67");
        }

        [ThreadStatic] private static float themeRenderScale;
        private PointF themeHover = new PointF(-1, -1);
        private static float ThemeScale { get { return themeRenderScale > 0 ? themeRenderScale : 1F; } }
        private static float Px(float pixels) { return pixels / ThemeScale; }

        private static Font ThemeFont(float points, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", points / ThemeScale, style, GraphicsUnit.Point);
        }

        private static void ThemeText(Graphics g, string text, RectangleF rect, Color color,
            float points = 9.5F, FontStyle style = FontStyle.Regular, bool center = false)
        {
            using (var font = ThemeFont(points, style))
            using (var brush = new SolidBrush(color))
            using (var format = ThemeCaptionFormat(g,BuiltIn(text),font,rect,center))
                g.DrawString(BuiltIn(text), font, brush, rect, format);
        }

        // Keep the established readable font size. Translated labels may use a
        // second line instead of silently losing their final words in NoWrap.
        private static StringFormat ThemeCaptionFormat(Graphics g,string text,Font font,RectangleF rect,bool center)
        {
            bool wrap=rect.Height>=font.GetHeight(g)*2 && g.MeasureString(text,font).Width>rect.Width;
            return new StringFormat {Alignment=center?StringAlignment.Center:StringAlignment.Near,
                LineAlignment=StringAlignment.Center,FormatFlags=wrap?StringFormatFlags.LineLimit:StringFormatFlags.NoWrap};
        }

        private static void ThemeBox(Graphics g, RectangleF rect, Color fill, Color border)
        {
            using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, rect);
            using (var pen = new Pen(border, Px(1))) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        }
        private static void ThemeButtonBox(Graphics g, RectangleF rect, Color fill, Color border)
        {
            if(rect.Width<=0||rect.Height<=0)return;
            float dpiScale=g.DpiX/96f;
            float d=Math.Min(Px(12*dpiScale),Math.Min(rect.Width,rect.Height));
            var state=g.Save();
            try {
                g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var path=new GraphicsPath()) {
                    path.AddArc(rect.Left,rect.Top,d,d,180,90);path.AddArc(rect.Right-d,rect.Top,d,d,270,90);
                    path.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90);path.AddArc(rect.Left,rect.Bottom-d,d,d,90,90);path.CloseFigure();
                    using(var b=new SolidBrush(fill))g.FillPath(b,path);
                    using(var p=new Pen(border,Px(dpiScale)))g.DrawPath(p,path);
                }
            } finally { g.Restore(state); }
        }

        private static void ThemeSection(Graphics g, RectangleF rect, string title)
        {
            ThemeBox(g, rect, Theme.Surface, Theme.Border);
            RectangleF bar = new RectangleF(rect.X, rect.Y, rect.Width, Px(32));
            using (var brush = new SolidBrush(Theme.SectionBar)) g.FillRectangle(brush, bar);
            using (var pen = new Pen(Theme.Border, Px(1))) g.DrawLine(pen, bar.Left, bar.Bottom, bar.Right, bar.Bottom);
            ThemeText(g, title, new RectangleF(bar.X + Px(12), bar.Y, bar.Width - Px(24), bar.Height),
                Theme.Ink, 10.5F, FontStyle.Bold);
        }

        private void DrawThemeShell(Graphics g)
        {
            using (var brush = new SolidBrush(Theme.Canvas)) g.FillRectangle(brush, 0, 0, ArtworkWidth, ArtworkHeight);
            using (var brush = new SolidBrush(Theme.TopBar)) g.FillRectangle(brush, 0, 0, ArtworkWidth, 54);
            ThemeText(g, "电缆振荡波校准系统", new RectangleF(Px(12), 0, 500, 54), Color.White, 12, FontStyle.Bold);
            ThemeText(g, "新软件测试 / 电缆长度 180.0 m", new RectangleF(645, 0, 380, 54), Color.White);
            ThemeText(g, "2026-08-26 08:06", new RectangleF(1080, 0, 180, 54), Color.White);
            ThemeText(g, "● 设备已连接", new RectangleF(1270, 0, 160, 54), Theme.Success);
            ThemeSection(g, new RectangleF(Px(12), 73, 234 - Px(12), 837), "校准参数");
            ThemeSection(g, new RectangleF(1380, 73, ArtworkWidth - 1380 - Px(12), 127), "相别");
            ThemeSection(g, new RectangleF(1380, 200 + Px(8), ArtworkWidth - 1380 - Px(12), 710 - Px(8)), "校准档位");
            ThemeBox(g, new RectangleF(31, 823, 180, 70), Theme.SectionBar, Theme.Border);
            ThemeText(g, "无线校准", new RectangleF(31 + Px(12), 825, 150, 30), Theme.Ink, 10.5F, FontStyle.Bold);
            ThemeText(g, "● 连接正常", new RectangleF(31 + Px(12), 854, 150, 30), Theme.Success);
        }

        private void DrawThemeCenter(Graphics g, WaveformData waveform, WaveformData noiseWaveform,
            double snr, double peak, double noise)
        {
            float left = 234 + Px(8), right = 1380 - Px(8);
            float bottom = signalQualityPanelTop - Px(4);
            ThemeSection(g, new RectangleF(left, 73, right - left, bottom - 73), "TDR 波形");
            ThemeText(g, "实时反射信号与定位标记", new RectangleF(left + Px(12), 73 + Px(35), 500, Px(22)), Theme.Muted);
            if (!IsCapacitiveLoad) DrawCheckBox(g, new PointF(1168, 110), JointMarkersVisible, CalibrationText("显示接头", "Show joints"));
            ThemeText(g, "待校准", new RectangleF(905, 75, 130, Px(28)), Theme.Accent, 9.5F, FontStyle.Bold, true);
            float qualityTop = signalQualityPanelTop + Px(4);
            ThemeSection(g, new RectangleF(left, qualityTop, right - left, 910 - qualityTop), "信号质量");
            DrawCheckBox(g, new PointF(1230F, signalQualityPanelTop + 34F), NoiseBaselineVisible, CalibrationText("筛选阈值", "Screening threshold"));
            double x0 = 0, x1 = 2.2e-6, y0 = -1.5, y1 = 2;
            if (waveform != null) GetWaveformView(waveform, out x0, out x1, out y0, out y1);
            DrawWaveformAxes(g, CurrentPlotRectangle, x0, x1, y0, y1);
            DrawNoiseAxes(g, CurrentNoisePlotRectangle);
            float y = Math.Min(807F, signalQualityPanelTop + 128F);
            string[] titles = { "信噪比", "峰值", "噪声" };
            string[] values = { snr.ToString("0.0", CultureInfo.InvariantCulture) + " dB",
                peak.ToString("0.00", CultureInfo.InvariantCulture) + " V", noise.ToString("0.000", CultureInfo.InvariantCulture) + " V" };
            float[] xs = { 877, 1040, 1208 };
            for (int i = 0; i < 3; i++)
            {
                var rect = new RectangleF(xs[i], y - 19, 137, 82);
                ThemeBox(g, rect, Theme.Surface, Theme.Border);
                ThemeText(g, titles[i], new RectangleF(rect.X + Px(12), rect.Y + Px(4), rect.Width - Px(16), Px(22)), Theme.Muted);
                ThemeText(g, values[i], new RectangleF(rect.X + Px(12), rect.Y + Px(28), rect.Width - Px(16), Px(22)), Theme.Ink, 9.5F, FontStyle.Bold);
            }
            DrawCenterSplitter(g);
        }

        private static void DrawThemePhaseSelection(Graphics g, int selected)
        {
            string[] labels = { "ALL", "A", "B", "C" };
            Color[] phases = { Theme.Accent, Color.Goldenrod, Color.ForestGreen, Color.Red };
            for (int i = 0; i < 4; i++)
            {
                var rect = new RectangleF(1399 + i * 60.75F, 122, 60.75F, 42);
                ThemeBox(g, rect, i == selected + 1 ? Theme.Accent : Theme.SectionBar, Theme.Border);
                ThemeText(g, labels[i], rect, i == selected + 1 ? Color.White : Theme.Ink, center: true);
                if (i > 0) using (var brush = new SolidBrush(phases[i]))
                    g.FillRectangle(brush, rect.Left + Px(8), rect.Bottom - Px(3), rect.Width - Px(16), Px(3));
            }
            ThemeText(g, "参数按相别独立保存", new RectangleF(1399, 169, 240, 30), Theme.Muted);
        }

        private void DrawThemeParameters(Graphics g, int phaseIndex, PhaseState phase)
        {
            ThemeText(g, CalibrationText("电缆长度", "Cable length") + " · " + PhaseCodeFromIndex(phaseIndex), new RectangleF(31, 143, 190, 32), Theme.Ink);
            ThemeInput(g, new RectangleF(37, 183, 173, 37), phase.CableLengthMeters.ToString("0.0", CultureInfo.InvariantCulture), "m");
            ThemeText(g, "波速", new RectangleF(31, 245, 180, 30), Theme.Ink);
            ThemeInput(g, new RectangleF(37, 280, 173, 37), phase.VelocityMPerUs.ToString("0.0", CultureInfo.InvariantCulture), "m/μs");
            ThemeText(g, "短电缆", new RectangleF(31, 366, 120, 30), Theme.Ink);
            ThemeText(g, CalibrationText("显示手动调节", "Manual controls"), new RectangleF(31, 420, 120, 30), Theme.Ink,8.5f);
            DrawToggle(g, new RectangleF(159, 367, 51, 29), phase.ShortCable);
            DrawToggle(g, new RectangleF(159, 421, 51, 29), phase.ManualBandwidthPanelVisible);
            if (phase.ManualBandwidthPanelVisible) DrawBandwidthSlider(g, phaseIndex, phase.BandwidthCutoffMHz);
            else ThemeText(g,ActualBandwidthText(phase),new RectangleF(31,454,184,30),Theme.Muted,8.5F);
        }

        private static void ThemeInput(Graphics g, RectangleF rect, string value, string unit)
        {
            ThemeBox(g, rect, Theme.Surface, Theme.Border);
            ThemeText(g, value, new RectangleF(rect.X + 3, rect.Y, 75, rect.Height), Theme.Ink);
            ThemeText(g, unit, new RectangleF(139, rect.Y, 70, rect.Height), Theme.Ink, center: true);
            using (var pen = new Pen(Theme.Border, Px(1))) g.DrawLine(pen, 136, rect.Top, 136, rect.Bottom);
            using (var pen = new Pen(Theme.Muted, Px(1)))
            {
                float y = rect.Top + 11;
                g.DrawLines(pen, new[] { new PointF(117, y + 3), new PointF(122, y), new PointF(127, y + 3) });
                y = rect.Top + 25;
                g.DrawLines(pen, new[] { new PointF(117, y), new PointF(122, y + 3), new PointF(127, y) });
            }
        }

        private void DrawThemeLevelList(Graphics g, string levelCode, bool allLevels)
        {
            DrawCheckBox(g, new PointF(1400, 270), allLevels, CalibrationText("显示全部档位", "Show all levels"));
            string[] visible = GetVisibleLevelCodes();
            for (int i = 0; i < visible.Length; i++)
            {
                var rect = new RectangleF(1398, 302F + i * 26.8F, 243, 26.5F);
                bool selected = visible[i] == levelCode;
                ThemeBox(g, rect, selected ? Theme.Accent : Theme.Surface, Theme.Border);
                ThemeText(g, LevelLabels[Array.IndexOf(LevelCodes, visible[i])], new RectangleF(rect.X + Px(12), rect.Y, rect.Width - Px(24), rect.Height), selected ? Color.White : Theme.Ink);
                if (selected) ThemeText(g, "✓", new RectangleF(rect.Right - Px(24), rect.Y, Px(20), rect.Height), Color.White, center: true);
            }
            var delete = new RectangleF(1398, 737, 243, 47);
            ThemeBox(g, delete, Theme.Surface, Theme.Border);
            ThemeText(g, CalibrationSaveCaption, delete, SelectedCalibrationHasData?Theme.Ink:Theme.Muted, center: true);
            var start = new RectangleF(1398, 796, 243, 94);
            ThemeBox(g, start, Enabled ? (start.Contains(themeHover) ? Theme.AccentHover : Theme.Accent) : Theme.Border, Theme.Border);
            ThemeText(g, levelCode == "TDR" ? "开始测距" : "开始校准", new RectangleF(start.X, start.Y + 8, start.Width, 40), Color.White, 10.5F, FontStyle.Bold, true);
            string caption = "当前量程：" + LevelLabels[Array.IndexOf(LevelCodes, levelCode)];
            ThemeText(g, caption, new RectangleF(start.X, start.Y + 50, start.Width, 30), Color.White, center: true);
        }
    }
}
