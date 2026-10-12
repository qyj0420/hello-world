using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.Presentation
{
    // Paint-only adaptation of Button. In particular, do not change FlatStyle,
    // Font, padding, preferred size, hit testing, or any command/keyboard path.
    // The native Button renderer remains responsible for text and image layout.
    internal class R2RoundedButton : Button
    {
        internal R2RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            ForeColor = Color.FromArgb(24, 51, 77);
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color canvas = Color.White;
            for (Control p = Parent; p != null; p = p.Parent)
                if (p.BackColor.A == 255) { canvas = p.BackColor; break; }
            e.Graphics.Clear(canvas);
            if (Width < 3 || Height < 3) return;
            GraphicsState state = e.Graphics.Save();
            try
            {
                float scale = Math.Max(.5f, e.Graphics.DpiX / 96f);
                float line = Math.Min(scale, Math.Min(Width, Height) - 1f);
                RectangleF face = new RectangleF(line / 2, line / 2,
                    Width - line, Height - line);
                float diameter = Math.Min(12f * scale, Math.Min(face.Width, face.Height));
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddArc(face.Left, face.Top, diameter, diameter, 180, 90);
                    path.AddArc(face.Right - diameter, face.Top, diameter, diameter, 270, 90);
                    path.AddArc(face.Right - diameter, face.Bottom - diameter, diameter, diameter, 0, 90);
                    path.AddArc(face.Left, face.Bottom - diameter, diameter, diameter, 90, 90);
                    path.CloseFigure();
                    e.Graphics.SetClip(path, CombineMode.Intersect);
                    base.OnPaint(e);
                    e.Graphics.Restore(state);
                    state = e.Graphics.Save();
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    if (FlatStyle != FlatStyle.Flat || FlatAppearance.BorderSize > 0)
                    {
                        Color border = FlatAppearance.BorderColor.IsEmpty
                            ? Color.FromArgb(202, 216, 229) : FlatAppearance.BorderColor;
                        if (!Enabled) border = Color.FromArgb(202, 216, 229);
                        using (Pen pen = new Pen(border, line)) e.Graphics.DrawPath(pen, path);
                    }
                }
            }
            finally { e.Graphics.Restore(state); }
        }
    }
}
