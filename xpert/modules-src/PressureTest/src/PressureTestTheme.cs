using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal static class PressureTestTheme
    {
        static readonly object FontCacheLock = new object();
        static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>(StringComparer.Ordinal);

        internal static readonly Color Navy = Color.FromArgb(11, 45, 77);
        internal static readonly Color NavyLight = Color.FromArgb(24, 51, 77);
        internal static readonly Color Accent = Color.FromArgb(0, 163, 199);
        internal static readonly Color AccentHover = Color.FromArgb(0, 137, 170);
        internal static readonly Color AccentSoft = Color.FromArgb(226, 246, 250);
        internal static readonly Color Workspace = Color.FromArgb(243, 247, 251);
        internal static readonly Color Canvas = Color.FromArgb(243, 247, 251);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color SurfaceAlt = Color.FromArgb(243, 247, 251);
        internal static readonly Color SectionBar = Color.FromArgb(234, 242, 248);
        internal static readonly Color Border = Color.FromArgb(202, 216, 229);
        internal static readonly Color Grid = Color.FromArgb(218, 228, 237);
        internal static readonly Color Ink = Color.FromArgb(24, 51, 77);
        internal static readonly Color SecondaryInk = Color.FromArgb(96, 117, 138);
        internal static readonly Color DisabledInk = Color.FromArgb(142, 158, 173);
        internal static readonly Color Success = Color.FromArgb(35, 138, 103);
        internal static readonly Color SuccessDark = Color.FromArgb(27, 112, 82);
        internal static readonly Color Warning = Color.FromArgb(243, 168, 31);
        internal static readonly Color Danger = Color.FromArgb(196, 71, 71);
        internal static readonly Color PhaseA = Color.FromArgb(245, 174, 35);
        internal static readonly Color PhaseB = Color.FromArgb(35, 138, 103);
        internal static readonly Color PhaseC = Color.FromArgb(196, 71, 71);
        internal static readonly Color Pd = Color.FromArgb(44, 133, 110);
        // Match the test-data browser; electrical channels keep the same colors.
        internal static readonly Color Voltage = Color.FromArgb(0, 120, 156);
        internal static readonly Color Location = Color.FromArgb(0, 163, 199);

        internal static Font Font(float size, FontStyle style)
        {
            // UI fonts are process-lifetime shared resources.  The layout uses a
            // small, fixed set of sizes; caching prevents resize and repeated
            // create/dispose cycles from continuously allocating GDI handles.
            string key = size.ToString("0.###", CultureInfo.InvariantCulture) + ":" + ((int)style).ToString(CultureInfo.InvariantCulture);
            lock (FontCacheLock)
            {
                Font font;
                if (!FontCache.TryGetValue(key, out font))
                {
                    // Point fonts follow the same Windows DPI as the analysis page.
                    // Fixed pixel fonts made every label too small on 125/150% laptops.
                    font = new Font("Microsoft YaHei UI", Math.Max(9.5f,size), style, GraphicsUnit.Point);
                    FontCache.Add(key, font);
                }
                return font;
            }
        }

        internal static Font Font(float size)
        {
            return Font(size, FontStyle.Regular);
        }

        internal static Font TransientFont(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", Math.Max(9.5f,size), style, GraphicsUnit.Point);
        }

        internal static Font TransientFont(float size)
        {
            return TransientFont(size, FontStyle.Regular);
        }

        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0) return path;
            float diameter = Math.Min(Math.Max(0f, radius * 2f), Math.Min(bounds.Width, bounds.Height));
            if (diameter < 1f)
            {
                path.AddRectangle(bounds);
                path.CloseFigure();
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class PressureEmergencyStatusLabel : Label
    {
        internal PressureEmergencyStatusLabel()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent==null?PressureTestTheme.Surface:Parent.BackColor);
            using(var path=PressureTestTheme.Rounded(new RectangleF(0,0,Width-1,Height-1),6f))
            using(var brush=new SolidBrush(BackColor))e.Graphics.FillPath(brush,path);
            // The live status stays readable even while neighbouring parameter
            // controls are disabled. Colour does not establish any permission.
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Color.White,
                TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class PressureTestCard : Panel
    {
        internal PressureTestCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = PressureTestTheme.Surface;
            Padding = new Padding(1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = PressureTestTheme.Rounded(bounds, 4f))
            using (Pen pen = new Pen(PressureTestTheme.Border))
                e.Graphics.DrawPath(pen, path);
        }
    }

    internal class PressureTestButton : Button
    {
        internal bool WrapText { get; set; }
        bool hover;
        bool pressed;
        bool primary;
        bool success;
        bool danger;
        bool measurementProgressVisible;
        double measurementProgress = Double.NaN;

        internal bool MeasurementProgressVisible
        { get { return measurementProgressVisible; } set { if(measurementProgressVisible==value)return;measurementProgressVisible=value;Invalidate(); } }
        // NaN means there is no usable feedback. Never substitute a timer,
        // command value, or an invented percentage for an actual measurement.
        internal double MeasurementProgress
        { get { return measurementProgress; } set { double next=Double.IsNaN(value)||Double.IsInfinity(value)?Double.NaN:Math.Max(0,Math.Min(1,value));if(measurementProgress.Equals(next))return;measurementProgress=next;Invalidate(); } }

        internal bool Primary
        {
            get { return primary; }
            set { if (primary == value) return; primary = value; Invalidate(); }
        }

        internal bool Success
        {
            get { return success; }
            set { if (success == value) return; success = value; Invalidate(); }
        }

        internal bool Danger
        {
            get { return danger; }
            set { if (danger == value) return; danger = value; Invalidate(); }
        }

        internal PressureTestButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            Height = 34;
            BackColor = PressureTestTheme.Surface;
            ForeColor = PressureTestTheme.Ink;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) hover = pressed = false;
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color canvas = PressureTestTheme.Surface;
            if (BackColor.A == 255) canvas = BackColor;
            for (Control ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor.BackColor.A != 255) continue;
                canvas = ancestor.BackColor;
                break;
            }
            e.Graphics.Clear(canvas);
            if (Width < 4 || Height < 4) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Color top;
            Color bottom;
            Color border;
            Color ink;
            ResolvePalette(out top, out bottom, out border, out ink);
            if(CompactPressureAppearance&&Enabled&&!Danger)
            {
                top=bottom=Color.FromArgb(220,244,248);
                border=PressureTestTheme.Accent;ink=PressureTestTheme.Navy;
            }
            if(MeasurementProgressVisible)
            {
                top=bottom=CompactPressureAppearance&&Enabled&&!Danger?Color.FromArgb(220,244,248):PressureTestTheme.SurfaceAlt;
                border=Danger?PressureTestTheme.Danger:PressureTestTheme.Accent;
                ink=Danger?PressureTestTheme.Danger:PressureTestTheme.Navy;
            }

            float pressOffset = pressed && Enabled ? 1f : 0f;
            RectangleF shadowBounds = new RectangleF(1.5f, 2.5f, Math.Max(1f, Width - 3f), Math.Max(1f, Height - 4f));
            RectangleF faceBounds = new RectangleF(1.5f, 1f + pressOffset,
                Math.Max(1f, Width - 3f), Math.Max(1f, Height - 4f));
            int shadowAlpha = !Enabled ? 8 : pressed ? 10 : hover ? 30 : 22;

            if (!pressed || !Enabled)
            {
                using (GraphicsPath shadowPath = PressureTestTheme.Rounded(shadowBounds, 6f*e.Graphics.DpiX/96f))
                using (SolidBrush shadow = new SolidBrush(Color.FromArgb(shadowAlpha, PressureTestTheme.Navy)))
                    e.Graphics.FillPath(shadow, shadowPath);
            }

            using (GraphicsPath facePath = PressureTestTheme.Rounded(faceBounds, 6f*e.Graphics.DpiX/96f))
            using (SolidBrush fill = new SolidBrush(top))
            using (Pen outline = new Pen(border,e.Graphics.DpiX/96f))
            {
                e.Graphics.FillPath(fill, facePath);
                if(MeasurementProgressVisible&&!Double.IsNaN(MeasurementProgress)&&MeasurementProgress>0)
                {
                    var saved=e.Graphics.Save();
                    try
                    {
                        e.Graphics.SetClip(facePath,System.Drawing.Drawing2D.CombineMode.Intersect);
                        using(var progressBrush=new SolidBrush(Danger?Color.FromArgb(255,209,212):Color.FromArgb(103,214,230)))
                            e.Graphics.FillRectangle(progressBrush,faceBounds.X,faceBounds.Y,(float)(faceBounds.Width*MeasurementProgress),faceBounds.Height);
                    }
                    finally { e.Graphics.Restore(saved); }
                }
                e.Graphics.DrawPath(outline, facePath);
            }

            Rectangle textBounds = new Rectangle(7, 2 + (int)pressOffset,
                Math.Max(1, Width - 14), Math.Max(1, Height - 6));
            DrawButtonText(e.Graphics,textBounds,ink);

            if (Focused && ShowFocusCues)
            {
                RectangleF focusBounds = RectangleF.Inflate(faceBounds, -3.5f, -3.5f);
                if (focusBounds.Width > 1f && focusBounds.Height > 1f)
                {
                    using (GraphicsPath focusPath = PressureTestTheme.Rounded(focusBounds, 4f))
                    using (Pen focusPen = new Pen(Color.FromArgb(170, ink)))
                    {
                        focusPen.DashStyle = DashStyle.Dot;
                        e.Graphics.DrawPath(focusPen, focusPath);
                    }
                }
            }
        }

        protected virtual bool CompactPressureAppearance { get { return false; } }
        protected virtual void DrawButtonText(Graphics graphics,Rectangle bounds,Color ink)
        {
            TextRenderer.DrawText(graphics,Text,Font,bounds,ink,
                TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|
                (WrapText?TextFormatFlags.WordBreak:TextFormatFlags.EndEllipsis)|TextFormatFlags.NoPadding|
                TextFormatFlags.PreserveGraphicsClipping);
        }

        void ResolvePalette(out Color top, out Color bottom, out Color border, out Color ink)
        {
            if (!Enabled)
            {
                top = Color.FromArgb(247, 249, 251);
                bottom = Color.FromArgb(232, 238, 243);
                border = Color.FromArgb(214, 223, 230);
                ink = PressureTestTheme.DisabledInk;
                return;
            }

            if (Danger)
            {
                top = pressed ? Color.FromArgb(255, 226, 229) :
                    hover ? Color.FromArgb(255, 246, 247) : PressureTestTheme.Surface;
                bottom = pressed ? Color.FromArgb(255, 238, 240) :
                    hover ? Color.FromArgb(255, 232, 235) : Color.FromArgb(255, 247, 248);
                border = hover || pressed ? PressureTestTheme.Danger : Color.FromArgb(238, 185, 188);
                ink = PressureTestTheme.Danger;
                return;
            }

            if (Success)
            {
                top = bottom = pressed ? PressureTestTheme.SuccessDark : PressureTestTheme.Success;
                border = PressureTestTheme.SuccessDark;
                ink = Color.White;
                return;
            }

            if (Primary)
            {
                top = bottom = pressed || hover ? PressureTestTheme.AccentHover : PressureTestTheme.Accent;
                border = pressed || hover ? PressureTestTheme.AccentHover : PressureTestTheme.Accent;
                ink = Color.White;
                return;
            }

            top = pressed ? Color.FromArgb(227, 241, 251) : PressureTestTheme.Surface;
            bottom = pressed ? Color.FromArgb(211, 233, 248) :
                hover ? PressureTestTheme.AccentSoft : PressureTestTheme.SurfaceAlt;
            border = hover || pressed ? PressureTestTheme.Accent : PressureTestTheme.Border;
            ink = PressureTestTheme.Ink;
        }
    }

    internal sealed class PressureTestSegmentButton : PressureTestButton
    {
        bool selected;

        internal bool Selected
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Primary = selected;
            base.OnPaint(e);
        }
    }

    internal sealed class PressureTestRangeMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(PressureTestTheme.Surface);
            RectangleF bounds = new RectangleF(0.5f, 0.5f,
                Math.Max(1f, e.ToolStrip.Width - 1f), Math.Max(1f, e.ToolStrip.Height - 1f));
            using (GraphicsPath path = PressureTestTheme.Rounded(bounds, 8f))
            using (SolidBrush fill = new SolidBrush(PressureTestTheme.Surface))
                e.Graphics.FillPath(fill, path);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(0.5f, 0.5f,
                Math.Max(1f, e.ToolStrip.Width - 1.5f), Math.Max(1f, e.ToolStrip.Height - 1.5f));
            using (GraphicsPath path = PressureTestTheme.Rounded(bounds, 8f))
            using (Pen outline = new Pen(Color.FromArgb(122, 188, 236), 1.2f))
                e.Graphics.DrawPath(outline, path);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // The range dropdown uses the same full-width selection treatment as the segmented buttons.
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripMenuItem item = e.Item as ToolStripMenuItem;
            if (item == null) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF face = new RectangleF(4f, 2f,
                Math.Max(1f, item.Width - 8f), Math.Max(1f, item.Height - 4f));
            if (item.Checked)
            {
                using (GraphicsPath path = PressureTestTheme.Rounded(face, 6f))
                using (LinearGradientBrush fill = new LinearGradientBrush(face,
                    PressureTestTheme.Accent, PressureTestTheme.Accent, LinearGradientMode.Vertical))
                using (Pen outline = new Pen(PressureTestTheme.AccentHover))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(outline, path);
                }
                return;
            }

            if (item.Selected)
            {
                using (GraphicsPath path = PressureTestTheme.Rounded(face, 6f))
                using (SolidBrush fill = new SolidBrush(PressureTestTheme.AccentSoft))
                using (Pen outline = new Pen(Color.FromArgb(139, 199, 240)))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(outline, path);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            ToolStripMenuItem item = e.Item as ToolStripMenuItem;
            Color ink = item != null && item.Checked ? Color.White : PressureTestTheme.Ink;
            Rectangle bounds = new Rectangle(12, 0,
                Math.Max(1, e.Item.Width - 24), Math.Max(1, e.Item.Height));
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }
    }
}
