using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LabVIEWChartCommon
{
    /// <summary>
    /// Small, vector-drawn export button used by interactive charts.  The flyout belongs
    /// to this child button, so it never replaces the host control's runtime shortcut menu.
    /// </summary>
    public sealed class ExportPopupButton : Control
    {
        readonly Func<bool> english;
        readonly Action exportData;
        readonly Action exportImage;
        readonly ContextMenuStrip flyout;
        Action addToReport;
        Func<bool> canAddToReport;
        bool hover, pressed;

        public ExportPopupButton(Func<bool> englishProvider, Action exportDataAction, Action exportImageAction)
        {
            english = englishProvider ?? delegate { return false; };
            exportData = exportDataAction;
            exportImage = exportImageAction;
            Name = "ExportButton";
            AccessibleName = "Export";
            TabStop = false;
            Cursor = Cursors.Hand;
            Size = new Size(28, 26);
            MinimumSize = new Size(28, 26);
            MaximumSize = new Size(28, 26);
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            flyout = new ContextMenuStrip();
            flyout.Items.Add("添加至报告", null, delegate { if (addToReport != null && (canAddToReport == null || canAddToReport())) addToReport(); });
            flyout.Items.Add("导出数据", null, delegate { if (exportData != null) exportData(); });
            flyout.Items.Add("导出图片", null, delegate { if (exportImage != null) exportImage(); });
            flyout.Opening += delegate { UpdateLanguage(); };
            UpdateLanguage();
        }

        public static ExportPopupButton Attach(Control host, Func<bool> englishProvider, Action exportDataAction, Action exportImageAction)
        {
            if (host == null) throw new ArgumentNullException("host");
            foreach (Control child in host.Controls)
            {
                var existing = child as ExportPopupButton;
                if (existing != null && existing.Name == "ExportButton") return existing;
            }
            var button = new ExportPopupButton(englishProvider, exportDataAction, exportImageAction);
            button.Location = new Point(Math.Max(0, host.ClientSize.Width - button.Width - 3),
                                        Math.Max(0, host.ClientSize.Height - button.Height - 3));
            host.Controls.Add(button);
            button.BringToFront();
            return button;
        }

        public void UpdateLanguage()
        {
            bool en = english();
            int language=Parent is LabVIEWPdMap.PdMapControl?((LabVIEWPdMap.PdMapControl)Parent).LanguageIndex:
                Parent is HVX.UnifiedSpectrum.UnifiedSpectrumControl?((HVX.UnifiedSpectrum.UnifiedSpectrumControl)Parent).LanguageIndex:en?1:0;
            AccessibleName = HVX.PdReview.ReviewLanguages.Text(language,"Export data","导出");
            flyout.Items[0].Text = HVX.PdReview.ReviewLanguages.Text(language,"Add to report","添加至报告");
            flyout.Items[0].Enabled = addToReport != null && (canAddToReport == null || canAddToReport());
            flyout.Items[1].Text = HVX.PdReview.ReviewLanguages.Text(language,"Export data","导出数据");
            flyout.Items[2].Text = HVX.PdReview.ReviewLanguages.Text(language,"Export image","导出图片");
            AccessibleDescription = HVX.PdReview.ReviewLanguages.Text(language,"Add chart to report attachments, export data or image","添加至报告附件、导出数据或图片");
        }

        internal void ConfigureReportAction(Action action, Func<bool> canAdd)
        { addToReport = action; canAddToReport = canAdd; UpdateLanguage(); }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) { pressed = false; Invalidate(); }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            UpdateLanguage();
            flyout.Show(this, new Point(Width, 0), ToolStripDropDownDirection.AboveLeft);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = ChartButtonChrome.Draw(g, ClientSize, hover, pressed, Enabled);
            using (var pen = new Pen(ink, 1.35f))
            using (var brush = new SolidBrush(ink))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float cx = Width / 2f, dy = (Height - 22) / 2f;
                g.DrawLine(pen, cx, 5 + dy, cx, 12 + dy);
                PointF[] arrow = { new PointF(cx - 3.2f, 9.2f + dy), new PointF(cx, 12.7f + dy), new PointF(cx + 3.2f, 9.2f + dy) };
                g.FillPolygon(brush, arrow);
                g.DrawLine(pen, cx - 5, 15.5f + dy, cx + 5, 15.5f + dy);
                g.DrawLine(pen, cx - 5, 15.5f + dy, cx - 5, 13.2f + dy);
                g.DrawLine(pen, cx + 5, 15.5f + dy, cx + 5, 13.2f + dy);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) flyout.Dispose();
            base.Dispose(disposing);
        }
    }

    // Shared frame for all three footer actions, independent of Windows themes.
    internal static class ChartButtonChrome
    {
        internal static Color Draw(Graphics graphics, Size size, bool hover, bool pressed, bool enabled)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            hover &= enabled; pressed &= enabled;
            Color background = !enabled?HVX.PdReview.ReviewTheme.Disabled:pressed||hover?HVX.PdReview.ReviewTheme.ActiveSoft:HVX.PdReview.ReviewTheme.Surface;
            Color border = enabled&&(hover||pressed)?HVX.PdReview.ReviewTheme.Active:HVX.PdReview.ReviewTheme.Border;
            using (var fill = new SolidBrush(background))
            using (var edge = new Pen(border, 1f))
            using (GraphicsPath path = Rounded(new Rectangle(1, 1, size.Width - 3, size.Height - 3), 3))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(edge, path);
            }
            return enabled?HVX.PdReview.ReviewTheme.Accent:HVX.PdReview.ReviewTheme.DisabledInk;
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

    }

    internal sealed class SampleStepButton : Button
    {
        readonly int direction;
        bool hover, pressed;
        internal SampleStepButton(int direction)
        {
            this.direction = direction;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Color.White : Parent.BackColor);
            Color ink = ChartButtonChrome.Draw(e.Graphics, ClientSize, hover, pressed, Enabled);
            float x = Width / 2f, y = Height / 2f;
            using (var brush = new SolidBrush(ink))
                e.Graphics.FillPolygon(brush, new[] { new PointF(x + direction * 3.5f, y),
                    new PointF(x - direction * 2.5f, y - 3.5f), new PointF(x - direction * 2.5f, y + 3.5f) });
        }
    }
}
