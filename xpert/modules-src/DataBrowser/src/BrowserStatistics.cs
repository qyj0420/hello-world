using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal sealed class BrowserStatisticsPlot : Control
    {
        BrowserRecord[] rows = new BrowserRecord[0];
        int mode;
        int languageIndex;
        double viewMin = .5, viewMax = 2, fullMax = 2;
        readonly Color[] colors =
        {
            BrowserTheme.PhaseA,
            BrowserTheme.PhaseB,
            BrowserTheme.PhaseC
        };
        static readonly Color Ink = BrowserTheme.Ink;
        static readonly Color Muted = BrowserTheme.Muted;

        internal BrowserStatisticsPlot()
        {
            DoubleBuffered = true;
            BackColor = BrowserTheme.Surface;
            Font = new Font("Arial", 10);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        internal void SetRecords(BrowserRecord[] records, int kind, int index)
        {
            rows = records;
            mode = kind;
            languageIndex = index;
            viewMin = .5;
            fullMax = viewMax = rows.Length == 0 ? 2 : Math.Max(2, rows.Max(r => r.Order) + .5);
            Invalidate();
        }

        int LegendHeight { get { return mode == 2 && (Width < 1100 || languageIndex > 1) ? 43 : 30; } }
        Rectangle Plot
        {
            get
            {
                // Leave a full text line between the description and both axis titles.
                int top = 42 + LegendHeight + Math.Max(34, Font.Height + 18);
                return new Rectangle(72, top, Math.Max(20, Width - 137), Math.Max(20, Height - top - 53));
            }
        }

        string T(string zh, string en) { return BrowserLanguages.Text(languageIndex, zh, en); }
        static string F(double value, string format = "0.0") { return value.ToString(format, CultureInfo.InvariantCulture); }

        static double NiceUpper(double value)
        {
            if (!(value > 0) || double.IsInfinity(value) || double.IsNaN(value)) return 1;
            double unit = Math.Pow(10, Math.Floor(Math.Log10(value / 5)));
            foreach (int factor in new[] { 1, 2, 5, 10 })
                if (value <= unit * factor * 5) return unit * factor * 5;
            return unit * 50;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var p = Plot;
            if (!p.Contains(e.Location)) return;
            double span = Math.Max(2, Math.Min(fullMax - .5, (viewMax - viewMin) * (e.Delta > 0 ? .8 : 1.25)));
            double ratio = (double)(e.X - p.Left) / p.Width;
            double at = viewMin + (viewMax - viewMin) * ratio;
            viewMin = Math.Max(.5, Math.Min(fullMax - span, at - span * ratio));
            viewMax = viewMin + span;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right)
            {
                viewMin = .5;
                viewMax = fullMax;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 250 || Height < 150) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot;
            var active = rows.Where(r => r.Order >= viewMin - .5 && r.Order <= viewMax + .5).ToArray();
            double maximumVoltage = active.Where(r => BrowserPlot.Finite(r.VoltageKv))
                                          .Select(r => Math.Abs(r.VoltageKv)).DefaultIfEmpty(1).Max();
            double voltageMax = NiceUpper(Math.Max(1, maximumVoltage * 1.10));
            double leftMin = 0;
            double valueMax = mode == 1 ? voltageMax :
                active.Select(r => mode == 2 ? r.MaxPdPc : r.TanDelta)
                      .Where(BrowserPlot.Finite).DefaultIfEmpty(1).Max();
            if (mode == 3)
                leftMin = Math.Min(0, active.Select(r => r.TanDelta).Where(BrowserPlot.Finite)
                                           .DefaultIfEmpty(0).Min() * 1.1);
            double leftMax = mode == 1 ? voltageMax : NiceUpper(Math.Max(1, valueMax * 1.10));
            leftMax = Math.Max(leftMin + 1e-6, leftMax);

            Func<double, float> x = v => (float)(p.Left + (v - viewMin) / (viewMax - viewMin) * p.Width);
            Func<double, float> y = v => (float)(p.Bottom - (v - leftMin) / (leftMax - leftMin) * p.Height);
            Func<double, float> voltageY = v => (float)(p.Bottom - v / voltageMax * p.Height);
            string leftTitle = mode == 1 ? T("电压 / kV", "Voltage / kV") :
                mode == 2 ? T("放电量 / pC", "Discharge / pC") : "tan δ / %";

            using (var ink = new SolidBrush(Ink))
            using (var muted = new SolidBrush(Muted))
            using (var grid = new Pen(BrowserTheme.Grid))
            using (var border = new Pen(BrowserTheme.Border))
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                g.DrawString(mode == 1 ? "U" : mode == 2 ? "U-Q" : "U-tan δ", bold, ink, 16, 10);
                int legendX = 90;
                foreach (string phase in rows.Select(r => r.Phase).Distinct().OrderBy(v => v))
                {
                    int pi = "ABC".IndexOf(phase);
                    if (pi < 0) continue;
                    using (var swatch = new SolidBrush(colors[pi]))
                        g.FillEllipse(swatch, legendX, 17, 9, 9);
                    g.DrawString(languageIndex != 0 ? "L" + (pi + 1) : phase + "相", Font, ink, legendX + 15, 10);
                    legendX += 76;
                }
                g.DrawLine(grid, 16, 35, Width - 16, 35);

                var legendBox = new Rectangle(16, 40, Width - 32, LegendHeight);
                using (var background = new SolidBrush(BrowserTheme.Canvas))
                    g.FillRectangle(background, legendBox);
                g.DrawRectangle(grid, legendBox);
                if (mode == 2 && (Width < 1100 || languageIndex > 1))
                {
                    g.DrawString(T("柱：电压     ●：最大放电量", "Bar: voltage     ●: maximum PD"),
                                 Font, ink, 27, 42);
                    g.DrawString(T("箱线：Q1–Q3 / 中位数 / 均值 / 最小–最大",
                                   "Box: Q1–Q3 / median / mean / min–max"),
                                 Font, muted, 27, 62);
                }
                else
                {
                    string legend = mode == 1 ? T("柱：电压", "Bar: voltage") :
                        mode == 2 ? T("柱：电压     ●：最大放电量     箱线：Q1–Q3 / 中位数 / 均值 / 最小–最大",
                                      "Bar: voltage     ●: maximum PD     Box: Q1–Q3 / median / mean / min–max") :
                        T("柱：电压     线：介损", "Bar: voltage     Line: tan δ");
                    g.DrawString(legend, Font, ink, 27, 45);
                }

                using (var background = new SolidBrush(BrowserTheme.Surface))
                    g.FillRectangle(background, p);
                for (int i = 0; i <= 5; i++)
                {
                    float py = p.Bottom - p.Height * i / 5f;
                    g.DrawLine(grid, p.Left, py, p.Right, py);
                    string tick = F(leftMin + (leftMax - leftMin) * i / 5, mode == 3 ? "0.###" : "0");
                    var size = g.MeasureString(tick, Font);
                    g.DrawString(tick, Font, muted, p.Left - size.Width - 9, py - size.Height / 2);
                    if (mode != 1)
                        g.DrawString(F(voltageMax * i / 5, "0.#"), Font, muted, p.Right + 8, py - size.Height / 2);
                }
                int step = Math.Max(1, (int)Math.Ceiling((viewMax - viewMin) / Math.Max(2, p.Width / 72)));
                for (int n = Math.Max(1, (int)Math.Ceiling(viewMin)); n <= viewMax; n += step)
                {
                    float px = x(n);
                    g.DrawLine(grid, px, p.Top, px, p.Bottom);
                    string label = n.ToString(CultureInfo.InvariantCulture);
                    var size = g.MeasureString(label, Font);
                    g.DrawString(label, Font, muted, px - size.Width / 2, p.Bottom + 8);
                }
                g.DrawRectangle(border, p);
                g.DrawString(leftTitle, bold, ink, 16, p.Top - 22);
                if (mode != 1)
                {
                    string title = T("电压 / kV", "Voltage / kV");
                    var size = g.MeasureString(title, bold);
                    g.DrawString(title, bold, ink, p.Right - size.Width, p.Top - 22);
                }
                string xTitle = Width < 630 ? T("加压次序", "Application sequence") :
                    T("加压次序（各相按测试时间排序）", "Application sequence (time order per phase)");
                var titleSize = g.MeasureString(xTitle, Font);
                g.DrawString(xTitle, Font, ink, p.Left + (p.Width - titleSize.Width) / 2, p.Bottom + 31);

                var state = g.Save();
                g.SetClip(p);
                var phaseNames = rows.Select(r => r.Phase).Distinct().OrderBy(v => v).ToArray();
                float unit = p.Width / (float)(viewMax - viewMin);
                float barWidth = Math.Max(.7f, Math.Min(36, unit * .68f / Math.Max(1, phaseNames.Length)));
                for (int pi = 0; pi < phaseNames.Length; pi++)
                {
                    string phase = phaseNames[pi];
                    int ci = Math.Max(0, "ABC".IndexOf(phase));
                    Color color = colors[ci];
                    PointF? previous = null;
                    using (var bar = new SolidBrush(Color.FromArgb(65, color)))
                    using (var box = new SolidBrush(Color.FromArgb(142, color)))
                    using (var barEdge = new Pen(Color.FromArgb(175, color), 1))
                    using (var pen = new Pen(color, 1.7f))
                    using (var solid = new SolidBrush(color))
                    foreach (var row in active.Where(v => v.Phase == phase).OrderBy(v => v.Order))
                    {
                        float px = x(row.Order) + (pi - (phaseNames.Length - 1) / 2f) * barWidth;
                        if (BrowserPlot.Finite(row.VoltageKv))
                        {
                            float voltageTop = voltageY(Math.Abs(row.VoltageKv));
                            g.FillRectangle(bar, px - barWidth / 2, voltageTop, barWidth, p.Bottom - voltageTop);
                            g.DrawRectangle(barEdge, px - barWidth / 2, voltageTop, barWidth, p.Bottom - voltageTop);
                            if (unit > 78 && phaseNames.Length == 1)
                            {
                                string label = F(row.VoltageKv);
                                var size = g.MeasureString(label, Font);
                                g.DrawString(label, Font, ink, px - size.Width / 2, voltageTop - size.Height - 2);
                            }
                        }
                        double value = mode == 2 ? row.MaxPdPc : mode == 3 ? row.TanDelta : double.NaN;
                        if (mode == 2 && BrowserPlot.Finite(row.PdMinimumPc) &&
                            BrowserPlot.Finite(row.PdMaximumPc))
                        {
                            float boxWidth = Math.Max(2, barWidth * .65f);
                            float low = y(row.PdMinimumPc), high = y(row.PdMaximumPc);
                            g.DrawLine(pen, px, high, px, low);
                            g.DrawLine(pen, px - boxWidth / 2, high, px + boxWidth / 2, high);
                            g.DrawLine(pen, px - boxWidth / 2, low, px + boxWidth / 2, low);
                            if (BrowserPlot.Finite(row.PdQ1Pc) && BrowserPlot.Finite(row.PdQ3Pc))
                            {
                                float top = y(row.PdQ3Pc), bottom = y(row.PdQ1Pc);
                                float height = Math.Max(.7f, bottom - top);
                                g.FillRectangle(box, px - boxWidth / 2, top, boxWidth, height);
                                g.DrawRectangle(pen, px - boxWidth / 2, top, boxWidth, height);
                                g.DrawLine(pen, px - boxWidth / 2, y(row.PdMedianPc),
                                                px + boxWidth / 2, y(row.PdMedianPc));
                            }
                            if (BrowserPlot.Finite(row.PdMeanPc))
                            {
                                float mean = y(row.PdMeanPc);
                                g.FillRectangle(Brushes.White, px - 3, mean - 3, 6, 6);
                                g.DrawRectangle(pen, px - 3, mean - 3, 6, 6);
                            }
                        }
                        if (BrowserPlot.Finite(value))
                        {
                            var point = new PointF(px, y(value));
                            if (previous.HasValue) g.DrawLine(pen, previous.Value, point);
                            g.FillEllipse(solid, point.X - 2.5f, point.Y - 2.5f, 5, 5);
                            previous = point;
                        }
                        else previous = null;
                    }
                }
                g.Restore(state);
                if (rows.Length == 0)
                    g.DrawString(T("无可显示的数据，请选择相别。", "No data to display. Select a phase."),
                                 Font, ink, p.Left + 25, p.Top + 25);
            }
        }
    }

    public sealed partial class ProjectDataBrowserControl
    {
        readonly BrowserStatisticsPlot statisticsPlot = new BrowserStatisticsPlot();

        void ShowStatistics(int mode)
        {
            if (project == null) return;
            statsMode = mode;
            SelectedNodeKind = "stats" + mode;
            SelectedFilePath = "";
            heading.Text = mode == 1 ? T("加压次序－电压", "Sequence – voltage") :
                mode == 2 ? T("加压次序－电压－最大放电量 / 箱线统计",
                              "Sequence – voltage – maximum discharge / box statistics") :
                T("加压次序－电压－介损", "Sequence – voltage – tan δ");
            var rows = project.Tests.Where(r =>
            {
                int i = "ABC".IndexOf(r.Phase);
                return i >= 0 && i < 3 && phaseButtons[i].Checked;
            }).ToArray();
            statsHost.Controls.Clear();
            table.Dock = DockStyle.Bottom;
            statisticsPlot.Dock = DockStyle.Fill;
            statsHost.Controls.Add(statisticsPlot);
            statsHost.Controls.Add(statisticsGap);
            statsHost.Controls.Add(table);
            FillTable(rows);
            ShowControl(statsHost);
            AdjustStatisticsLayout();
            statisticsPlot.SetRecords(rows, mode, languageIndex);
            Buttons(false, false, true);
            information.Clear();
            AdjustInformationHeight();
            SetStatus(T("统计记录数：", "Statistics records: ") + rows.Length);
        }
    }
}
