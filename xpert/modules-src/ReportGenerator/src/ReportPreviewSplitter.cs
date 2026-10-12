using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.ReportGenerator
{
    // The footer lives outside this split, so dragging can never cover its buttons.
    internal sealed class ReportPreviewSplitter : SplitContainer
    {
        double previewRatio = 0.66;
        bool adjusting;
        internal ReportPreviewSplitter()
        {
            Dock = DockStyle.Fill; Margin = Padding.Empty; Orientation = Orientation.Horizontal;
            BackColor = ReportTheme.Border; Size = new Size(900, 650); SplitterWidth = 12;
            Panel1MinSize = 110; Panel2MinSize = 112; SplitterDistance = 420;
            SplitterMoved += (s, e) => { if (!adjusting && Height > SplitterWidth) previewRatio = (double)SplitterDistance / (Height - SplitterWidth); };
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (adjusting || ClientSize.Height <= SplitterWidth + 4) return;
            adjusting = true;
            try
            {
                int available = ClientSize.Height - SplitterWidth;
                int firstMin = Math.Min(110, available / 3), secondMin = Math.Min(112, available / 3);
                Panel1MinSize = 0; Panel2MinSize = 0;
                SplitterDistance = Math.Max(firstMin, Math.Min(available - secondMin, (int)Math.Round(available * previewRatio)));
                Panel1MinSize = firstMin; Panel2MinSize = secondMin;
            }
            finally { adjusting = false; }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle r = SplitterRectangle;
            using (var fill = new SolidBrush(ReportTheme.SurfaceAlt)) e.Graphics.FillRectangle(fill, r);
            using (var pen = new Pen(ReportTheme.Muted))
                for (int offset = -2; offset <= 2; offset += 2)
                    e.Graphics.DrawLine(pen, r.Left + r.Width / 2 - 28, r.Top + r.Height / 2 + offset,
                        r.Left + r.Width / 2 + 28, r.Top + r.Height / 2 + offset);
        }
    }

    public sealed partial class ReportGeneratorControl
    {
        readonly ReportPreviewSplitter previewConclusionSplit = new ReportPreviewSplitter();
        readonly Button conclusionFeedbackButton = new ReportButton();
    }
}
