using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    /// <summary>
    /// A compact, keyboard-accessible dashboard tile.  The artwork is drawn at
    /// runtime so that the application does not need a second, mismatched icon
    /// set beside the XIONGXIN brand assets.
    /// </summary>
    internal sealed class ModuleTile : Control
    {
        private bool hovered;
        private bool compactMode;
        private string title = String.Empty;
        private string description = String.Empty;
        private string disabledHint = String.Empty;
        private string modeBadge = String.Empty;
        private Color accentColor = AppTheme.Accent;
        private Color modeBadgeColor = AppTheme.Warning;

        public string ModuleId { get; private set; }
        public string Glyph { get; private set; }

        public bool CompactMode
        {
            get { return compactMode; }
            set { if (compactMode != value) { compactMode = value; Invalidate(); } }
        }

        public string Title
        {
            get { return title; }
            set { title = value ?? String.Empty; Invalidate(); }
        }

        public string Description
        {
            get { return description; }
            set { description = value ?? String.Empty; Invalidate(); }
        }

        public string DisabledHint
        {
            get { return disabledHint; }
            set { disabledHint = value ?? String.Empty; Invalidate(); }
        }

        public string ModeBadge
        {
            get { return modeBadge; }
            set { modeBadge = value ?? String.Empty; Invalidate(); }
        }

        public Color ModeBadgeColor
        {
            get { return modeBadgeColor; }
            set { modeBadgeColor = value; Invalidate(); }
        }

        public Color AccentColor
        {
            get { return accentColor; }
            set { accentColor = value; Invalidate(); }
        }

        public ModuleTile(string moduleId, string glyph)
        {
            ModuleId = moduleId ?? String.Empty;
            Glyph = glyph ?? String.Empty;
            Size = new Size(252, 166);
            Margin = new Padding(10);
            Padding = new Padding(18);
            BackColor = AppTheme.Canvas;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Enabled) Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space))
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float scale = Math.Max(1f, Height / 186f);
            Func<int, int> scaled = value => (int)Math.Round(value * scale);

            Rectangle card = new Rectangle(2, 2, Width - 5, Height - 5);
            Color surface = Enabled
                ? (hovered ? Color.FromArgb(248, 252, 255) : AppTheme.Surface)
                : Color.FromArgb(245, 247, 249);
            Color border = hovered && Enabled ? accentColor : AppTheme.Border;

            using (GraphicsPath path = RoundedRectangle(card, scaled(12)))
            using (SolidBrush fill = new SolidBrush(surface))
            using (Pen outline = new Pen(border, hovered && Enabled ? 2f : 1f))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(outline, path);
            }

            Rectangle accentBar = new Rectangle(card.X, card.Y + scaled(18), scaled(4), card.Height - scaled(36));
            if (compactMode)
                accentBar = new Rectangle(card.X, card.Y + 10, 4, Math.Max(1, card.Height - 20));
            using (GraphicsPath path = RoundedRectangle(accentBar, scaled(2)))
            using (SolidBrush brush = new SolidBrush(Enabled ? accentColor : Color.FromArgb(190, 199, 207)))
                graphics.FillPath(brush, path);

            Rectangle glyphBounds = new Rectangle(scaled(20), scaled(20), scaled(48), scaled(48));
            if (compactMode)
                glyphBounds = new Rectangle(13, (Height - 36) / 2, 36, 36);
            Color glyphBack = Enabled
                ? Color.FromArgb(28, accentColor)
                : Color.FromArgb(232, 236, 239);
            using (SolidBrush brush = new SolidBrush(glyphBack))
                graphics.FillEllipse(brush, glyphBounds);
            DrawGlyph(graphics, glyphBounds, Enabled ? accentColor : Color.FromArgb(151, 163, 174));

            Color titleColor = Enabled ? AppTheme.Ink : Color.FromArgb(132, 145, 156);
            Color bodyColor = Enabled ? AppTheme.Muted : Color.FromArgb(160, 169, 177);
            Rectangle titleBounds;
            Rectangle descriptionBounds;
            Rectangle footerBounds;
            GetContentBounds(out titleBounds, out descriptionBounds, out footerBounds);
            using (Font font = AppTheme.Font(compactMode ? 9.2f : 11.6f, FontStyle.Bold))
            using (SolidBrush brush = new SolidBrush(titleColor))
                graphics.DrawString(title, font, brush, titleBounds,
                    new StringFormat { Trimming = StringTrimming.EllipsisWord,
                        LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.LineLimit });

            if (modeBadge.Length > 0)
            {
                Rectangle badge = GetModeBadgeBounds();
                using (GraphicsPath path = RoundedRectangle(badge, Math.Min(scaled(10), badge.Height / 2)))
                using (SolidBrush brush = new SolidBrush(modeBadgeColor))
                using (Font font = AppTheme.Font(compactMode ? 7.1f : 8.2f, FontStyle.Bold))
                {
                    graphics.FillPath(brush, path);
                    // Measure and paint with the same single-line renderer. A
                    // fixed 52 px badge split the four-character Chinese label.
                    TextRenderer.DrawText(graphics, modeBadge, font, badge, Color.White,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis);
                }
            }

            if (!compactMode)
            {
                using (Font font = AppTheme.Font(9.0f, FontStyle.Regular))
                using (SolidBrush brush = new SolidBrush(bodyColor))
                    graphics.DrawString(description, font, brush, descriptionBounds,
                        new StringFormat { Trimming = StringTrimming.EllipsisWord,
                            FormatFlags = StringFormatFlags.LineLimit });
            }

            if (!Enabled && disabledHint.Length > 0)
            {
                Rectangle badge = footerBounds;
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(238, 231, 214)))
                using (GraphicsPath path = RoundedRectangle(badge, 8))
                using (Font font = AppTheme.Font(compactMode ? 7f : 8.2f, FontStyle.Regular))
                using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(144, 100, 18)))
                {
                    graphics.FillPath(brush, path);
                    graphics.DrawString(disabledHint, font, textBrush, badge,
                        new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                            Trimming = StringTrimming.EllipsisCharacter,
                            FormatFlags = StringFormatFlags.NoWrap });
                }
            }
            else if (!compactMode)
            {
                Rectangle arrow = new Rectangle(footerBounds.Right - scaled(16), footerBounds.Top,
                    scaled(16), scaled(16));
                using (Pen pen = new Pen(Enabled ? accentColor : Color.FromArgb(175, 184, 192), 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    graphics.DrawLine(pen, arrow.Left, arrow.Top + 8, arrow.Right - 3, arrow.Top + 8);
                    graphics.DrawLine(pen, arrow.Right - 7, arrow.Top + 4, arrow.Right - 3, arrow.Top + 8);
                    graphics.DrawLine(pen, arrow.Right - 7, arrow.Top + 12, arrow.Right - 3, arrow.Top + 8);
                }
            }

            if (Focused && ShowFocusCues)
            {
                Rectangle focus = Rectangle.Inflate(card, -5, -5);
                ControlPaint.DrawFocusRectangle(graphics, focus, titleColor, surface);
            }
        }

        internal Rectangle GetModeBadgeBounds()
        {
            if (modeBadge.Length == 0) return Rectangle.Empty;
            float scale = Math.Max(1f, Height / 186f);
            int inset = compactMode ? 9 : (int)Math.Round(16 * scale);
            int top = compactMode ? 5 : (int)Math.Round(12 * scale);
            using (Font font = AppTheme.Font(compactMode ? 7.1f : 8.2f, FontStyle.Bold))
            {
                Size text = TextRenderer.MeasureText(modeBadge, font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                int width = Math.Max(compactMode ? 39 : (int)Math.Round(52 * scale), text.Width + (compactMode ? 12 : 20));
                width = Math.Min(width, Math.Max(1, Width - 2 * inset));
                int height = Math.Max(compactMode ? 15 : (int)Math.Round(22 * scale), text.Height + (compactMode ? 2 : 6));
                return new Rectangle(Width - inset - width, top, width, height);
            }
        }

        internal void GetContentBounds(out Rectangle titleBounds, out Rectangle descriptionBounds,
            out Rectangle footerBounds)
        {
            if (compactMode)
            {
                descriptionBounds = Rectangle.Empty;
                footerBounds = new Rectangle(55, Height - 19, Math.Max(1, Width - 64), 14);
                int titleTop = modeBadge.Length > 0 ? GetModeBadgeBounds().Bottom + 1 : 10;
                titleBounds = new Rectangle(55, titleTop, Math.Max(1, Width - 64),
                    Math.Max(1, Math.Min(modeBadge.Length > 0 ? 32 : 40, footerBounds.Top - titleTop - 1)));
                return;
            }
            float scale = Math.Max(1f, Height / 186f);
            Func<int, int> scaled = value => (int)Math.Round(value * scale);
            int titleLeft = scaled(80);
            titleBounds = modeBadge.Length == 0
                ? new Rectangle(titleLeft, scaled(19), Width - titleLeft - scaled(16), scaled(58))
                : new Rectangle(titleLeft, scaled(34), Width - titleLeft - scaled(16), scaled(61));
            descriptionBounds = new Rectangle(scaled(20), scaled(92), Width - scaled(40),
                Math.Max(scaled(36), Height - scaled(92) - scaled(38)));
            footerBounds = new Rectangle(scaled(20), Height - scaled(32), Width - scaled(40), scaled(21));
            if (modeBadge.Length > 0 && titleBounds.Top < GetModeBadgeBounds().Bottom + 2)
            {
                int bottom = titleBounds.Bottom;
                titleBounds.Y = GetModeBadgeBounds().Bottom + 2;
                titleBounds.Height = Math.Max(1, bottom - titleBounds.Y);
            }
            // Some translated module names need three lines below the mode
            // badge. Preserve the title, then give the description the remaining
            // space above the fixed action/status footer.
            int descriptionTop = Math.Max(scaled(92), titleBounds.Bottom + scaled(8));
            descriptionBounds = new Rectangle(scaled(20), descriptionTop, Width - scaled(40),
                Math.Max(1, footerBounds.Top - descriptionTop - scaled(6)));
        }

        private void DrawGlyph(Graphics graphics, Rectangle bounds, Color color)
        {
            float glyphScale = bounds.Width / 48f;
            float left = bounds.Left + 13f * glyphScale;
            float top = bounds.Top + 12f * glyphScale;
            float right = bounds.Right - 12f * glyphScale;
            float bottom = bounds.Bottom - 12f * glyphScale;
            using (Pen pen = new Pen(color, 2.2f * glyphScale))
            using (SolidBrush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (String.Equals(Glyph, "folder", StringComparison.OrdinalIgnoreCase))
                {
                    graphics.DrawLine(pen, left, top + 5, left + 8, top + 5);
                    graphics.DrawLine(pen, left + 8, top + 5, left + 12, top + 1);
                    graphics.DrawLine(pen, left + 12, top + 1, right, top + 1);
                    graphics.DrawRectangle(pen, left, top + 5, right - left, bottom - top - 4);
                }
                else if (String.Equals(Glyph, "plus", StringComparison.OrdinalIgnoreCase))
                {
                    graphics.DrawRectangle(pen, left + 2, top + 1, right - left - 3, bottom - top - 2);
                    graphics.DrawLine(pen, (left + right) / 2, top + 7, (left + right) / 2, bottom - 6);
                    graphics.DrawLine(pen, left + 7, (top + bottom) / 2, right - 6, (top + bottom) / 2);
                }
                else if (String.Equals(Glyph, "search", StringComparison.OrdinalIgnoreCase))
                {
                    graphics.DrawEllipse(pen, left, top, 16, 16);
                    graphics.DrawLine(pen, left + 14, top + 14, right, bottom);
                }
                else if (String.Equals(Glyph, "chart", StringComparison.OrdinalIgnoreCase))
                {
                    graphics.DrawLine(pen, left, bottom, left, top);
                    graphics.DrawLine(pen, left, bottom, right, bottom);
                    PointF[] points = { new PointF(left + 3, bottom - 4), new PointF(left + 9, top + 10),
                        new PointF(left + 15, bottom - 10), new PointF(right, top + 3) };
                    graphics.DrawLines(pen, points);
                }
                else if (String.Equals(Glyph, "report", StringComparison.OrdinalIgnoreCase))
                {
                    graphics.DrawRectangle(pen, left + 2, top, right - left - 4, bottom - top);
                    graphics.DrawLine(pen, left + 7, top + 7, right - 6, top + 7);
                    graphics.DrawLine(pen, left + 7, top + 13, right - 6, top + 13);
                    graphics.DrawLine(pen, left + 7, top + 19, right - 10, top + 19);
                }
                else if (String.Equals(Glyph, "sine", StringComparison.OrdinalIgnoreCase))
                {
                    var points = new PointF[49];
                    for (int i = 0; i < points.Length; i++)
                        points[i] = new PointF(left + (right-left)*i/48f,
                            (top+bottom)/2f - (float)Math.Sin(i*Math.PI*2/48)*(bottom-top)*.39f);
                    graphics.DrawLines(pen, points);
                }
                else if (String.Equals(Glyph, "pulse", StringComparison.OrdinalIgnoreCase))
                {
                    float w=right-left, mid=(top+bottom)/2;
                    graphics.DrawLines(pen,new[]{new PointF(left,mid),new PointF(left+w*.27f,mid),
                        new PointF(left+w*.42f,top+2),new PointF(left+w*.57f,bottom-2),
                        new PointF(left+w*.72f,mid),new PointF(right,mid)});
                }
                else if (String.Equals(Glyph, "square", StringComparison.OrdinalIgnoreCase))
                {
                    float w=right-left, hi=top+4,lo=bottom-4;
                    graphics.DrawLines(pen,new[]{new PointF(left,lo),new PointF(left+w*.2f,lo),
                        new PointF(left+w*.2f,hi),new PointF(left+w*.6f,hi),
                        new PointF(left+w*.6f,lo),new PointF(right,lo)});
                }
                else if (String.Equals(Glyph, "bolt", StringComparison.OrdinalIgnoreCase))
                {
                    PointF[] points = { new PointF(left + 13, top), new PointF(left + 3, top + 14),
                        new PointF(left + 12, top + 14), new PointF(left + 7, bottom),
                        new PointF(right, top + 10), new PointF(left + 16, top + 10) };
                    graphics.FillPolygon(brush, points);
                }
                else
                {
                    graphics.DrawEllipse(pen, left, top, right - left, bottom - top);
                    graphics.DrawLine(pen, left + 5, (top + bottom) / 2, right - 5, (top + bottom) / 2);
                    graphics.DrawLine(pen, (left + right) / 2, top + 5, (left + right) / 2, bottom - 5);
                }
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = Math.Max(1, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
