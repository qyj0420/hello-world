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
        bool hover;

        public ExportPopupButton(Func<bool> englishProvider, Action exportDataAction, Action exportImageAction)
        {
            english = englishProvider ?? delegate { return false; };
            exportData = exportDataAction;
            exportImage = exportImageAction;
            Name = "ExportButton";
            AccessibleName = "Export";
            TabStop = false;
            Cursor = Cursors.Hand;
            Size = new Size(22, 22);
            MinimumSize = new Size(22, 22);
            MaximumSize = new Size(22, 22);
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            flyout = new ContextMenuStrip();
            flyout.Items.Add("导出数据", null, delegate { if (exportData != null) exportData(); });
            flyout.Items.Add("导出图片", null, delegate { if (exportImage != null) exportImage(); });
            flyout.Opening += delegate { UpdateLanguage(); };
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
            flyout.Items[0].Text = en ? "Export data" : "导出数据";
            flyout.Items[1].Text = en ? "Export image" : "导出图片";
            AccessibleDescription = en ? "Export data or image" : "导出数据或图片";
        }

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

        protected override void OnMouseUp(MouseEventArgs e)
        {
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
            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var fill = new SolidBrush(hover ? Color.FromArgb(232, 244, 252) : Color.FromArgb(246, 249, 251)))
            using (var edge = new Pen(hover ? Color.FromArgb(0, 122, 170) : Color.FromArgb(126, 142, 153), 1f))
            using (GraphicsPath path = Rounded(r, 3))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
            Color ink = hover ? Color.FromArgb(0, 104, 145) : Color.FromArgb(65, 83, 95);
            using (var pen = new Pen(ink, 1.35f))
            using (var brush = new SolidBrush(ink))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float cx = Width / 2f;
                g.DrawLine(pen, cx, 5, cx, 12);
                PointF[] arrow = { new PointF(cx - 3.2f, 9.2f), new PointF(cx, 12.7f), new PointF(cx + 3.2f, 9.2f) };
                g.FillPolygon(brush, arrow);
                g.DrawLine(pen, 6, 15.5f, 16, 15.5f);
                g.DrawLine(pen, 6, 15.5f, 6, 13.2f);
                g.DrawLine(pen, 16, 15.5f, 16, 13.2f);
            }
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) flyout.Dispose();
            base.Dispose(disposing);
        }
    }
}
