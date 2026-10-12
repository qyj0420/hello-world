using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        // One measured header serves painting, point projection and hit testing.
        // Its cache contains layout only; it never clones or aggregates source rows.
        sealed class HeaderLegendItem
        { internal int Phase; internal string Text; internal Rectangle Bounds; }
        sealed class SpectrumHeaderLayout
        {
            internal Rectangle Title, Summary, Region, Classification, Order, Error, PhaseReference,DischargeType;
            internal string SummaryText, RegionText, ClassificationText, OrderText, ErrorText, PhaseReferenceText;
            internal readonly List<HeaderLegendItem> Legend = new List<HeaderLegendItem>();
            internal int PlotTop;
        }
        SpectrumHeaderLayout measuredHeader;
        string measuredHeaderKey;
        const TextFormatFlags HeaderLineFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags HeaderWrapFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;

        string OrderHeaderText
        {
            get
            {
                return mode == 9 ? (L("Bars: U   Box: Q1–Q3   Line: median   Square: mean   Whiskers: min–max","柱形：电压   箱体：Q1–Q3   实线：中位数   方块：均值   须线：最小–最大"))
                    : mode == 10 ? (L("Bars: U   Line: pulse count","柱形：电压   实线：放电次数")) : "";
            }
        }
        SpectrumHeaderLayout HeaderLayout(int width)
        {
            string summary = SummaryTitleSuffix, region = RegionStatisticsSummary;
            bool phaseReference = view != null && view.Is3D && HasPhaseReference && (Camera.PlaneIndex == 0 || IsPhaseHorizontalPlane);
            string key = width.ToString(CultureInfo.InvariantCulture) + "|" + Font.ToString() + "|" + english + "|" + mode + "|" +
                (view == null ? 0 : RuntimeHelpers.GetHashCode(view)) + "|" + summary + "|" + ClassificationActive + "|" +
                classificationLowThresholdPercent.ToString("R", CultureInfo.InvariantCulture) + "|" + classificationHighThresholdPercent.ToString("R", CultureInfo.InvariantCulture) +
                "|" + (lastError.Length > 0) + "|" + phaseReference + "|" + region+"|"+dischargeIdentificationText;
            if (measuredHeader != null && measuredHeaderKey == key) return measuredHeader;
            var layout = new SpectrumHeaderLayout { SummaryText = summary };
            int contentWidth = Math.Max(1, width - 32), left = 16, top = 8;
            int textHeight = TextRenderer.MeasureText("Ag国", Font, Size.Empty, HeaderLineFlags).Height;
            using (var titleFont = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold))
            using (var phaseFont = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold))
            {
                Size title = TextRenderer.MeasureText(SpectrumTitleText, titleFont, Size.Empty, HeaderLineFlags);
                layout.Title = new Rectangle(left, top, title.Width + 2, title.Height);
                int legendWidth = 0;
                foreach (int phase in displayedPhases)
                {
                    string text = phase == 0 ? (L("Combined","合并数据")) : GetPhaseLabel(phase);
                    Size phaseSize = TextRenderer.MeasureText(text, phaseFont, Size.Empty, HeaderLineFlags);
                    int itemWidth = phaseSize.Width + 33;
                    layout.Legend.Add(new HeaderLegendItem { Phase = phase, Text = text, Bounds = new Rectangle(0, 0, itemWidth, phaseSize.Height) });
                    legendWidth += itemWidth;
                }
                int legendLeft = width - 16 - legendWidth;
                bool legendSameRow = layout.Legend.Count == 0 || layout.Title.Right + 12 <= legendLeft;
                int phaseHeight = TextRenderer.MeasureText("L1", phaseFont, Size.Empty, HeaderLineFlags).Height;
                int firstRowHeight = Math.Max(title.Height, phaseHeight), cursor = legendSameRow ? width - 16 : left + legendWidth;
                int legendTop = legendSameRow ? top + (firstRowHeight - phaseHeight) / 2 : top + firstRowHeight + 3;
                foreach (var item in layout.Legend)
                {
                    cursor -= item.Bounds.Width;
                    item.Bounds = new Rectangle(cursor, legendTop, item.Bounds.Width, phaseHeight);
                }
                int bottom = top + firstRowHeight;
                if (!legendSameRow) bottom = legendTop + phaseHeight;
                Size inlineSummary = TextRenderer.MeasureText(summary, Font, Size.Empty, HeaderLineFlags);
                int summaryLeft = layout.Title.Right + 3;
                int inlineRight = layout.Legend.Count == 0 ? width - 16 : legendLeft - 10;
                if (legendSameRow && summaryLeft + inlineSummary.Width <= inlineRight)
                    layout.Summary = new Rectangle(summaryLeft, top + (firstRowHeight - textHeight) / 2, inlineSummary.Width, textHeight);
                else
                {
                    int summaryHeight = MeasureHeaderText(summary, contentWidth);
                    layout.Summary = new Rectangle(left, bottom + 3, contentWidth, summaryHeight);
                    bottom = layout.Summary.Bottom;
                }
                if (region.Length > 0)
                { layout.RegionText = region; layout.Region = NextHeaderRow(region, left, contentWidth, ref bottom); }
                if((mode==11||mode==12)&&dischargeIdentificationText.Length>0)
                {layout.DischargeType=new Rectangle(left,bottom+3,contentWidth,textHeight);bottom=layout.DischargeType.Bottom;}
                if (ClassificationActive)
                { layout.ClassificationText = ClassificationHeaderText; layout.Classification = NextHeaderRow(layout.ClassificationText, left, contentWidth, ref bottom); }
                // Dependency diagnostics remain available through ConclusionStatus
                // and ConclusionStatusCode, not as operator-facing chart content.
                // Genuine conclusion annotations are rendered separately in the plot.
                layout.OrderText = OrderHeaderText;
                if (layout.OrderText.Length > 0) layout.Order = NextHeaderRow(layout.OrderText, left, contentWidth, ref bottom);
                if (lastError.Length > 0)
                {
                    layout.ErrorText = L("Warning: read LastError","异常提示：请读取 LastError");
                    layout.Error = NextHeaderRow(layout.ErrorText, left, contentWidth, ref bottom);
                }
                // The phase sine remains on the chart; omit its explanatory header.
                // Reserve only real axis-label clearance, not the former 82px
                // generic header. Both 3D Z labels and the PRPD density-scale
                // title sit above the box (the latter starts at PlotTop - 24).
                int axisClearance = view != null && (view.Is3D || view.IsHeatmap) ? Math.Max(26, textHeight + 6) : Math.Max(10, (textHeight + 1) / 2 + 4);
                // A density title sits above the maximum tick (whose centre is
                // at PlotTop), not on top of it. Use the same measured metrics
                // as painting so large fonts cannot collide with the legend.
                if(view != null && (view.IsHeatmap || mode == 12))
                    axisClearance = Math.Max(axisClearance, DensityScaleHeaderClearance);
                layout.PlotTop = bottom + axisClearance;
            }
            measuredHeader = layout; measuredHeaderKey = key;
            return layout;
        }
        int MeasureHeaderText(string text, int width)
        { return TextRenderer.MeasureText(text, Font, new Size(Math.Max(1, width), int.MaxValue), HeaderWrapFlags).Height; }
        Rectangle NextHeaderRow(string text, int left, int width, ref int bottom)
        {
            var result = new Rectangle(left, bottom + 3, width, MeasureHeaderText(text, width));
            bottom = result.Bottom; return result;
        }
        static Rectangle OffsetHeader(Rectangle rectangle, Rectangle bounds)
        { rectangle.Offset(bounds.Location); return rectangle; }
        void DrawHeaderText(Graphics graphics, string text, Rectangle rectangle, Rectangle bounds, Color color)
        {
            if (rectangle.IsEmpty || string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(graphics, text, Font, OffsetHeader(rectangle, bounds), color, HeaderWrapFlags | TextFormatFlags.PreserveGraphicsClipping);
        }
        void DrawSpectrumHeader(Graphics graphics, Rectangle bounds, RenderPalette palette)
        {
            SpectrumHeaderLayout layout = HeaderLayout(bounds.Width);
            using(var fill=new SolidBrush(HVX.PdReview.ReviewTheme.SurfaceAlt))graphics.FillRectangle(fill,new Rectangle(bounds.Left,bounds.Top,bounds.Width,28));
            using (var title = new Font(Font.FontFamily, Font.Size + 1, FontStyle.Bold))
                TextRenderer.DrawText(graphics, SpectrumTitleText, title, OffsetHeader(layout.Title, bounds), ForeColor, HeaderLineFlags);
            DrawHeaderText(graphics, layout.SummaryText, layout.Summary, bounds, ForeColor);
            DrawHeaderText(graphics, layout.RegionText, layout.Region, bounds, HVX.PdReview.ReviewTheme.SecondaryInk);
            if(!layout.DischargeType.IsEmpty)TextRenderer.DrawText(graphics,dischargeIdentificationText,Font,OffsetHeader(layout.DischargeType,bounds),HVX.PdReview.ReviewTheme.Active,
                HeaderLineFlags|TextFormatFlags.Right|TextFormatFlags.EndEllipsis);
            foreach (var item in layout.Legend)
            {
                Rectangle box = OffsetHeader(item.Bounds, bounds);
                using(var marker=new Pen(mode==12?HVX.PdReview.ReviewTheme.SecondaryInk:PhaseColors[Math.Max(0,Math.Min(3,item.Phase))],1))
                    DrawMarker(graphics, new PointF(box.Left + 6, box.Top + box.Height / 2f), item.Phase, 4,marker);
                using (var phaseFont = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold))
                    TextRenderer.DrawText(graphics, item.Text, phaseFont, new Rectangle(box.Left + 17, box.Top, box.Width - 17, box.Height), ForeColor, HeaderLineFlags);
            }
            DrawHeaderText(graphics, layout.ClassificationText, layout.Classification, bounds, HVX.PdReview.ReviewTheme.SecondaryInk);
            DrawHeaderText(graphics, layout.OrderText, layout.Order, bounds, HVX.PdReview.ReviewTheme.SecondaryInk);
            DrawHeaderText(graphics, layout.ErrorText, layout.Error, bounds, Color.Firebrick);
            DrawHeaderText(graphics, layout.PhaseReferenceText, layout.PhaseReference, bounds, HVX.PdReview.ReviewTheme.SecondaryInk);
        }
    }
}
