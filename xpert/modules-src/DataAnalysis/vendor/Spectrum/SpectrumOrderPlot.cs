using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    // Task11's min/Q1/median/Q3/max and mean, with explicit source provenance.
    internal sealed class SpectrumOrderGroup
    {
        internal int Phase, Count;
        internal double Order, RecordId, Voltage, Minimum, LowerQuartile, Median,
            UpperQuartile, Maximum, Mean;
        internal int[] SourceRows;
        readonly List<int> rows = new List<int>();
        readonly List<double> charges = new List<double>();

        internal void Add(int row, double charge)
        {
            rows.Add(row);
            if (double.IsNaN(charge) || double.IsInfinity(charge)) return;
            double q = Math.Abs(charge);
            charges.Add(q);
            // Positive-value online mean avoids an overflowing sum.
            Mean += (q - Mean) / charges.Count;
        }

        internal void Complete()
        {
            Count = rows.Count;
            SourceRows = rows.ToArray();
            charges.Sort();
            if (charges.Count == 0) return; // N is still valid without Q.
            Minimum = charges[0]; Maximum = charges[charges.Count - 1];
            LowerQuartile = Quantile(0.25); Median = Quantile(0.50); UpperQuartile = Quantile(0.75);
        }

        double Quantile(double p)
        {
            // Same interpolation as Task11 UnifiedDischargeMatrixReader.OrderGroup.
            double position = p * (charges.Count - 1);
            int low = (int)Math.Floor(position), high = (int)Math.Ceiling(position);
            return charges[low] + (charges[high] - charges[low]) * (position - low);
        }
    }

    public partial class UnifiedSpectrumControl
    {
        sealed class OrderGlyph
        {
            internal SpectrumOrderGroup Group;
            internal double X, Width;
            internal bool AmbiguousOrder;
        }

        List<OrderGlyph> OrderGlyphs()
        {
            int[] phases = view.OrderGroups.Select(p => p.Phase).Distinct().OrderBy(p => p).ToArray();
            var result = new List<OrderGlyph>();
            if (phases.Length == 0) return result;
            double phaseWidth = 0.72 / phases.Length;
            // Even conflicting groups stay individually visible, not collapsed.
            foreach (var sameOrder in view.OrderGroups.GroupBy(p => new { p.Phase, p.Order }))
            {
                var groups = sameOrder.ToArray();
                int phaseSlot = Array.IndexOf(phases, sameOrder.Key.Phase);
                double phaseOffset = (phaseSlot - (phases.Length - 1) / 2.0) * phaseWidth;
                double width = phaseWidth / groups.Length;
                for (int i = 0; i < groups.Length; i++)
                    result.Add(new OrderGlyph { Group = groups[i], Width = width,
                        X = groups[i].Order + phaseOffset + (i - (groups.Length - 1) / 2.0) * width,
                        AmbiguousOrder = groups.Length > 1 });
            }
            result.Sort(delegate(OrderGlyph a, OrderGlyph b) {
                int c = a.Group.Phase.CompareTo(b.Group.Phase); return c != 0 ? c : a.X.CompareTo(b.X); });
            return result;
        }

        void DrawOrderPlot(Graphics g, Rectangle area)
        {
            if (view == null || !view.IsOrderPlot) return;
            List<OrderGlyph> glyphs = OrderGlyphs();
            // Right-axis voltage bars are the background, never a third depth axis.
            foreach (OrderGlyph glyph in glyphs)
            {
                float left = Project(new SpectrumPoint { X = glyph.X - glyph.Width * 0.43, Y = 0 }, area).X;
                float right = Project(new SpectrumPoint { X = glyph.X + glyph.Width * 0.43, Y = 0 }, area).X;
                float y = OrderVoltageY(glyph.Group.Voltage, area), zero = OrderVoltageY(0, area);
                if (!Finite(left) || !Finite(right) || !Finite(y) || !Finite(zero)) continue;
                Color color = PhaseColors[Math.Max(0, Math.Min(3, glyph.Group.Phase))];
                using (var fill = new SolidBrush(Color.FromArgb(36, color)))
                using (var outline = new Pen(Color.FromArgb(90, color), 1))
                {
                    RectangleF bar = new RectangleF(left, Math.Min(y, zero), Math.Max(1, right - left), Math.Abs(zero - y));
                    g.FillRectangle(fill, bar); g.DrawRectangle(outline, bar.X, bar.Y, bar.Width, bar.Height);
                }
            }
            foreach (var phaseGlyphs in glyphs.GroupBy(p => p.Group.Phase))
            {
                int phase = phaseGlyphs.Key;
                Color color = PhaseColors[Math.Max(0, Math.Min(3, phase))];
                using (var pen = new Pen(color, 1.6f))
                using (var boxFill = new SolidBrush(Color.FromArgb(75, color)))
                using (var meanFill = new SolidBrush(Color.White))
                {
                    PointF previous = PointF.Empty; bool hasPrevious = false;
                    foreach (OrderGlyph glyph in phaseGlyphs)
                    {
                        SpectrumOrderGroup group = glyph.Group;
                        double centerValue = mode == 9 ? group.Median : group.Count;
                        PointF center = Project(new SpectrumPoint { X = glyph.X, Y = centerValue }, area);
                        if (!Finite(center.X) || !Finite(center.Y)) { hasPrevious = false; continue; }
                        // Conflicting records at one n are separate observations, not a time sequence.
                        if (hasPrevious && !glyph.AmbiguousOrder) g.DrawLine(pen, previous, center);
                        hasPrevious = !glyph.AmbiguousOrder; previous = center;
                        if (mode == 10) { DrawMarker(g, center, phase, 3.5f, pen); continue; }
                        PointF min = Project(new SpectrumPoint { X = glyph.X, Y = group.Minimum }, area);
                        PointF max = Project(new SpectrumPoint { X = glyph.X, Y = group.Maximum }, area);
                        PointF q1 = Project(new SpectrumPoint { X = glyph.X, Y = group.LowerQuartile }, area);
                        PointF q3 = Project(new SpectrumPoint { X = glyph.X, Y = group.UpperQuartile }, area);
                        PointF mean = Project(new SpectrumPoint { X = glyph.X, Y = group.Mean }, area);
                        float half = (float)Math.Max(1.0, Math.Min(18.0, glyph.Width * area.Width / (State.X1 - State.X0) * 0.28));
                        g.DrawLine(pen, min, max);
                        g.DrawLine(pen, min.X - half * 0.7f, min.Y, min.X + half * 0.7f, min.Y);
                        g.DrawLine(pen, max.X - half * 0.7f, max.Y, max.X + half * 0.7f, max.Y);
                        float boxHeight = Math.Max(1, q1.Y - q3.Y);
                        g.FillRectangle(boxFill, center.X - half, q3.Y, 2 * half, boxHeight);
                        g.DrawRectangle(pen, center.X - half, q3.Y, 2 * half, boxHeight);
                        g.DrawLine(pen, center.X - half, center.Y, center.X + half, center.Y);
                        DrawMarker(g, center, phase, 3, pen);
                        g.FillRectangle(meanFill, mean.X - 2.5f, mean.Y - 2.5f, 5, 5);
                        g.DrawRectangle(pen, mean.X - 2.5f, mean.Y - 2.5f, 5, 5);
                    }
                }
            }
        }

        float OrderVoltageY(double voltage, Rectangle area)
        {
            double span = view.ZMax - view.ZMin;
            if (!(span > 0) || !Finite(span)) return area.Bottom;
            return (float)(area.Bottom - (voltage - view.ZMin) / span * area.Height);
        }

        void DrawOrderVoltageAxis(Graphics g, Rectangle area)
        {
            if (view == null || !view.IsOrderPlot) return;
            using (var axis = new Pen(HVX.PdReview.ReviewTheme.SecondaryInk))
            using (var ink = new SolidBrush(ForeColor))
            {
                g.DrawLine(axis, area.Right, area.Top, area.Right, area.Bottom);
                float widest = 0;
                foreach (double value in AxisTicks(view.ZMin, view.ZMax, "U", 5))
                {
                    float y = OrderVoltageY(value, area);
                    string label = value.ToString(Math.Abs(value) >= 1e6 ? "0.#E+0" : "0.0", CultureInfo.InvariantCulture);
                    SizeF size = g.MeasureString(label, Font); widest = Math.Max(widest, size.Width);
                    g.DrawLine(axis, area.Right, y, area.Right + 4, y);
                    g.DrawString(label, Font, ink, area.Right + 7, y - size.Height / 2);
                }
                var save = g.Save();
                g.TranslateTransform(area.Right + Math.Max(50, widest + 22), area.Top + area.Height / 2);
                g.RotateTransform(90);
                CenterText(g, AxisLabel("U"), Font, ink, new RectangleF(-area.Height / 2, -12, area.Height, 24));
                g.Restore(save);
            }
        }

        /// <summary>Columns: phase, record ID, n, U/kV, count, min, Q1, median, Q3, max, mean (pC).</summary>
        public double[,] GetOrderStatisticsMatrix()
        {
            int count = view == null || !view.IsOrderPlot ? 0 : view.OrderGroups.Count;
            var matrix = new double[count, 11];
            for (int r = 0; r < count; r++)
            {
                SpectrumOrderGroup p = view.OrderGroups[r];
                double[] row = { p.Phase, p.RecordId, p.Order, p.Voltage, p.Count,
                    p.Minimum, p.LowerQuartile, p.Median, p.UpperQuartile, p.Maximum, p.Mean };
                for (int c = 0; c < row.Length; c++) matrix[r, c] = row[c];
            }
            return matrix;
        }
    }
}
