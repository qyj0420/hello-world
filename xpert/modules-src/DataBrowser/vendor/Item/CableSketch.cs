using System;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace LabVIEWItemInformation
{
    internal sealed class CableSketch : Control
    {
        internal double[][] Positions = new double[0][];
        internal string[] Phases = new string[0];
        internal int LanguageId;
        internal float UiScale = 1f;
        internal string EmptyCaption = "";
        internal string StartType="",EndType="";
        internal CableSketch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (Positions.Length == 0)
            {
                TextRenderer.DrawText(g, EmptyCaption, Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak); return;
            }
            float scale = UiScale;
            float left = (Positions.Length > 1 ? 145 : 124) * scale, right = Width - 124 * scale;
            if (right <= left) return;
            double max = 0;
            foreach (double[] row in Positions) if (row.Length > 1) max = Math.Max(max, row[row.Length - 1]);
            if (max <= 0 || double.IsNaN(max) || double.IsInfinity(max)) return;
            float rowHeight = (float)Height / Positions.Length;
            for (int p = 0; p < Positions.Length; p++)
            {
                double[] row = Positions[p];
                float y = rowHeight * p + 25 * scale;
                if (Positions.Length > 1)
                {
                    string phase = Phases.Length > p ? Phases[p] : "ABC"[Math.Min(2, p)].ToString();
                    int index = "ABC".IndexOf(phase);
                    if (LanguageId != 0 && index >= 0) phase = "L" + (index + 1);
                    TextRenderer.DrawText(g, phase, Font, new Rectangle(0, (int)(y - 12 * scale), (int)(40 * scale), (int)(25 * scale)), ForeColor, TextFormatFlags.VerticalCenter);
                }
                if (row.Length < 2)
                {
                    TextRenderer.DrawText(g, EmptyCaption, Font, new Rectangle((int)left, (int)(y - 12 * scale), Math.Max(1, (int)(right - left)), (int)(25 * scale)), Color.Gray, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
                    continue;
                }
                float end = left + (right - left) * (float)(row[row.Length - 1] / max);
                bool integrated=Phases.Length>p&&Phases[p]=="ALL";
                Color phaseColor=p==0?Color.FromArgb(209,154,18):p==1?Color.FromArgb(43,154,104):Color.FromArgb(207,77,81);
                HVX.ProjectCables.CableVisualStyle.DrawBody(g,left,end,y,integrated,phaseColor,scale);
                HVX.ProjectCables.CableVisualStyle.DrawTermination(g,left,y,-1,StartType,scale);
                HVX.ProjectCables.CableVisualStyle.DrawTermination(g,end,y,1,EndType,scale);
                Rectangle lastLabel = Rectangle.Empty;
                for (int i = 0; i < row.Length; i++)
                {
                    float x = left + (right - left) * (float)(row[i] / max);
                    if (i > 0 && i < row.Length - 1)
                        using (Brush joint = new SolidBrush(Color.FromArgb(0, 145, 150))) g.FillRectangle(joint, x - 7 * scale, y - scale, 14 * scale, 3 * scale);
                    string text = row[i].ToString("0.###", CultureInfo.InvariantCulture) + "m";
                    Size size = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    Rectangle box = new Rectangle((int)x - size.Width / 2, (int)(y + 16 * scale), size.Width, size.Height);
                    if (i == row.Length - 1 || !box.IntersectsWith(lastLabel))
                    {
                        // Reserve the terminal label so nearby intermediate labels never overwrite it.
                        Size endSize = TextRenderer.MeasureText(row[row.Length - 1].ToString("0.###", CultureInfo.InvariantCulture) + "m", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                        if (i != row.Length - 1 && box.Right > end - endSize.Width / 2 - 5 * scale) continue;
                        TextRenderer.DrawText(g, text, Font, box, ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine); lastLabel = box;
                    }
                }
            }
        }
        static void DrawTerminal(Graphics g, float x, float y, int direction, float scale)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(x, y); g.ScaleTransform(direction * scale, scale);
            using (Pen edge = new Pen(Color.FromArgb(65, 65, 65), 1.5f))
            using (LinearGradientBrush metal = new LinearGradientBrush(new Rectangle(0, -5, 123, 10), Color.White, Color.FromArgb(152, 155, 156), 90))
            using (LinearGradientBrush red = new LinearGradientBrush(new Rectangle(0, -15, 90, 30), Color.FromArgb(255, 187, 176), Color.FromArgb(211, 63, 49), 90))
            {
                g.FillRectangle(metal, 0, -3, 123, 6); g.DrawRectangle(edge, 0, -3, 123, 6);
                g.FillRectangle(metal, 94, -5, 7, 10); g.DrawRectangle(edge, 94, -5, 7, 10);
                g.FillRectangle(red, 0, -11, 22, 22); g.DrawRectangle(edge, 0, -11, 22, 22);
                for (int i = 0; i < 4; i++)
                {
                    float a = 20 + i * 14;
                    PointF[] points = { new PointF(a, -15), new PointF(a + 14, -8), new PointF(a + 14, 8), new PointF(a, 15) };
                    g.FillPolygon(red, points); g.DrawPolygon(edge, points);
                }
                g.FillRectangle(red, 77, -8, 11, 16); g.DrawRectangle(edge, 77, -8, 11, 16);
            }
            g.Restore(state);
        }
    }
}
