using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal static class ProjectUiTheme
    {
        internal static readonly Color Graphite = Color.FromArgb(11, 45, 77);
        internal static readonly Color GraphiteSoft = Color.FromArgb(234, 242, 248);
        internal static readonly Color Navy = Color.FromArgb(24, 51, 77);
        internal static readonly Color Header = Color.FromArgb(234, 242, 248);
        internal static readonly Color ElectricBlue = Color.FromArgb(0, 163, 199);
        internal static readonly Color ElectricBlueHover = Color.FromArgb(0, 137, 170);
        internal static readonly Color Cyan = Color.FromArgb(0, 163, 199);
        internal static readonly Color Canvas = Color.FromArgb(243, 247, 251);
        internal static readonly Color Surface = Color.FromArgb(255, 255, 255);
        internal static readonly Color SurfaceAlt = Color.FromArgb(243, 247, 251);
        internal static readonly Color Border = Color.FromArgb(202, 216, 229);
        internal static readonly Color Grid = Color.FromArgb(202, 216, 229);
        internal static readonly Color Ink = Color.FromArgb(24, 51, 77);
        internal static readonly Color Muted = Color.FromArgb(96, 117, 138);
        internal static readonly Color Selection = Color.FromArgb(0, 163, 199);
        internal static readonly Color Success = Color.FromArgb(35, 138, 103);
        internal static readonly Color SuccessPale = Color.FromArgb(225, 248, 241);
        internal static readonly Color Warning = Color.FromArgb(201, 139, 32);
        internal static readonly Color Danger = Color.FromArgb(196, 71, 71);
        internal static readonly Color DangerHover = Color.FromArgb(154, 48, 48);

        internal static void ApplyDialogIcon(Form form)
        {
            HVX.Presentation.OemUiBranding.Apply(form);
        }

        internal static Font Font(Control owner, float size, FontStyle style = FontStyle.Regular)
        {
            var font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            // WinForms does not own fonts assigned by the application. Keep ownership
            // local (never a static cache), and release after the owner's children.
            owner.Disposed += delegate { font.Dispose(); };
            return font;
        }

        internal static GraphicsPath Rounded(Rectangle rectangle, int radius)
        {
            int diameter = Math.Max(2, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal enum InstrumentIcon
    {
        None, Folder, Refresh, Search, Load, Delete, Import, Export, Report, Merge, Browse, Arrow
    }

    internal sealed class BrandHeaderPanel : Panel
    {
        public BrandHeaderPanel()
        {
            DoubleBuffered = true;
            BackColor = ProjectUiTheme.Graphite;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Rectangle area = ClientRectangle;
            if (area.Width <= 0 || area.Height <= 0) return;
            e.Graphics.Clear(ProjectUiTheme.Graphite);
            using (SolidBrush accent = new SolidBrush(ProjectUiTheme.ElectricBlue))
                e.Graphics.FillRectangle(accent, 0, Height - 2, Width, 2);
        }
    }

    internal sealed class InstrumentPanel : Panel
    {
        public InstrumentPanel()
        {
            DoubleBuffered = true;
            BackColor = ProjectUiTheme.Surface;
            Padding = new Padding(12);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(ProjectUiTheme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
    }

    internal sealed class InstrumentButton : Button
    {
        bool hover;
        bool pressed;

        public InstrumentIcon IconKind { get; set; }
        public bool Primary { get; set; }
        public bool Danger { get; set; }
        public bool Compact { get; set; }

        public InstrumentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Height = 38;
            MinimumSize = new Size(38, 34);
            Font = ProjectUiTheme.Font(this, 9.5f);
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? ProjectUiTheme.Surface : Parent.BackColor);
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color background;
            Color foreground;
            Color border;
            if (!Enabled)
            {
                background = Color.FromArgb(243, 247, 252); foreground = Color.FromArgb(145, 158, 178); border = ProjectUiTheme.Border;
            }
            else if (Danger)
            {
                background = hover ? Color.FromArgb(252, 238, 238) : ProjectUiTheme.Surface; foreground = ProjectUiTheme.Danger; border = hover ? ProjectUiTheme.Danger : ProjectUiTheme.Border;
            }
            else if (Primary)
            {
                background = hover ? ProjectUiTheme.ElectricBlueHover : ProjectUiTheme.ElectricBlue; foreground = Color.White; border = background;
            }
            else
            {
                background = hover ? ProjectUiTheme.Header : ProjectUiTheme.Surface;
                foreground = ProjectUiTheme.Ink; border = hover ? ProjectUiTheme.ElectricBlue : ProjectUiTheme.Border;
            }
            if (pressed && Enabled) background = ControlPaint.Dark(background, 0.08f);
            using (GraphicsPath path = ProjectUiTheme.Rounded(bounds, (int)Math.Round(6f * Math.Max(.5f, e.Graphics.DpiX / 96f))))
            using (SolidBrush brush = new SolidBrush(background))
            using (Pen pen = new Pen(border, 1f))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
            int iconSize = Math.Max(14, Math.Min(20, Height - 16));
            int textWidth = TextRenderer.MeasureText(e.Graphics, Text ?? string.Empty, Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
            int totalWidth = IconKind == InstrumentIcon.None ? textWidth : iconSize + (string.IsNullOrEmpty(Text) ? 0 : 8 + textWidth);
            int left = Compact ? (Width - totalWidth) / 2 : Math.Max(10, (Width - totalWidth) / 2);
            if (IconKind != InstrumentIcon.None)
            {
                DrawIcon(e.Graphics, new Rectangle(left, (Height - iconSize) / 2, iconSize, iconSize), foreground, IconKind);
                left += iconSize + 8;
            }
            if (!string.IsNullOrEmpty(Text))
            {
                Rectangle textArea = IconKind == InstrumentIcon.None
                    ? new Rectangle(10, 4, Math.Max(1, Width - 20), Math.Max(1, Height - 8))
                    : new Rectangle(left, 4, Math.Max(1, Width - left - 8), Math.Max(1, Height - 8));
                TextRenderer.DrawText(e.Graphics, Text, Font, textArea, foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            if (Focused && ShowFocusCues)
            {
                Rectangle focus = Rectangle.Inflate(bounds, -4, -4);
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, foreground, background);
            }
        }

        static void DrawIcon(Graphics g, Rectangle r, Color color, InstrumentIcon icon)
        {
            float unit = r.Width / 20f;
            Func<float, float> x = value => r.Left + value * unit;
            Func<float, float> y = value => r.Top + value * unit;
            using (Pen pen = new Pen(color, Math.Max(1.5f, unit * 1.65f)))
            using (SolidBrush brush = new SolidBrush(color))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (icon)
                {
                    case InstrumentIcon.Folder:
                    case InstrumentIcon.Browse:
                        g.DrawLines(pen, new[] { new PointF(x(2), y(6)), new PointF(x(7), y(6)), new PointF(x(9), y(8)), new PointF(x(18), y(8)), new PointF(x(17), y(16)), new PointF(x(3), y(16)), new PointF(x(2), y(6)) });
                        break;
                    case InstrumentIcon.Refresh:
                        g.DrawArc(pen, x(3), y(3), 14 * unit, 14 * unit, 35, 275);
                        g.DrawLines(pen, new[] { new PointF(x(14), y(2)), new PointF(x(18), y(5)), new PointF(x(14), y(7)) });
                        break;
                    case InstrumentIcon.Search:
                        g.DrawEllipse(pen, x(3), y(3), 10 * unit, 10 * unit); g.DrawLine(pen, x(12), y(12), x(18), y(18));
                        break;
                    case InstrumentIcon.Load:
                        g.DrawRectangle(pen, x(2), y(3), 11 * unit, 14 * unit); g.DrawLine(pen, x(8), y(10), x(18), y(10));
                        g.DrawLines(pen, new[] { new PointF(x(14), y(6)), new PointF(x(18), y(10)), new PointF(x(14), y(14)) });
                        break;
                    case InstrumentIcon.Delete:
                        g.DrawRectangle(pen, x(5), y(6), 10 * unit, 11 * unit); g.DrawLine(pen, x(3), y(5), x(17), y(5));
                        g.DrawLine(pen, x(8), y(2), x(12), y(2)); g.DrawLine(pen, x(8), y(9), x(8), y(14)); g.DrawLine(pen, x(12), y(9), x(12), y(14));
                        break;
                    case InstrumentIcon.Import:
                        g.DrawRectangle(pen, x(3), y(12), 14 * unit, 5 * unit); g.DrawLine(pen, x(10), y(2), x(10), y(13));
                        g.DrawLines(pen, new[] { new PointF(x(6), y(9)), new PointF(x(10), y(13)), new PointF(x(14), y(9)) });
                        break;
                    case InstrumentIcon.Export:
                    case InstrumentIcon.Report:
                        g.DrawRectangle(pen, x(3), y(3), 14 * unit, 14 * unit); g.DrawLine(pen, x(10), y(14), x(10), y(3));
                        g.DrawLines(pen, new[] { new PointF(x(6), y(7)), new PointF(x(10), y(3)), new PointF(x(14), y(7)) });
                        if (icon == InstrumentIcon.Report) g.DrawLine(pen, x(6), y(14), x(14), y(14));
                        break;
                    case InstrumentIcon.Merge:
                        g.DrawLine(pen, x(3), y(4), x(7), y(4)); g.DrawLine(pen, x(3), y(16), x(7), y(16));
                        g.DrawBezier(pen, x(7), y(4), x(12), y(4), x(10), y(10), x(17), y(10));
                        g.DrawBezier(pen, x(7), y(16), x(12), y(16), x(10), y(10), x(17), y(10));
                        g.DrawLines(pen, new[] { new PointF(x(14), y(7)), new PointF(x(18), y(10)), new PointF(x(14), y(13)) });
                        break;
                    case InstrumentIcon.Arrow:
                        g.DrawLines(pen, new[] { new PointF(x(5), y(8)), new PointF(x(10), y(13)), new PointF(x(15), y(8)) });
                        break;
                    default:
                        g.FillEllipse(brush, x(8), y(8), 4 * unit, 4 * unit);
                        break;
                }
            }
        }
    }
}
