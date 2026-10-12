using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        readonly ImageList navigationIcons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(18, 18), TransparentColor = Color.Transparent };
        void InitializeNavigationIcons()
        {
            // Native storage must exist before disposable drawing bitmaps are
            // added; otherwise ImageList defers copying them until first paint.
            IntPtr iconHandle = navigationIcons.Handle;
            foreach (string key in new[] { "project", "cal", "dem", "loc", "stats" })
            {
                using (var bitmap = new Bitmap(18, 18))
                using (var g = Graphics.FromImage(bitmap))
                using (var pen = new Pen(key == "cal" ? BrowserTheme.Cyan : key == "dem" ? Color.SeaGreen : key == "loc" ? Color.DarkOrange : BrowserTheme.Navy, 1.7f))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                    if (key == "cal") g.DrawLines(pen, new[] { new Point(1, 12), new Point(5, 12), new Point(7, 3), new Point(9, 15), new Point(11, 8), new Point(13, 12), new Point(17, 12) });
                    else if (key == "dem") { g.DrawEllipse(pen, 2, 3, 14, 12); g.DrawLines(pen, new[] { new Point(7, 5), new Point(5, 10), new Point(11, 10), new Point(9, 14) }); }
                    else if (key == "loc") { g.DrawEllipse(pen, 4, 4, 10, 10); g.DrawLine(pen, 9, 0, 9, 17); g.DrawLine(pen, 0, 9, 17, 9); }
                    else if (key == "stats") { g.DrawLine(pen, 3, 15, 3, 9); g.DrawLine(pen, 8, 15, 8, 3); g.DrawLine(pen, 13, 15, 13, 6); g.DrawLine(pen, 1, 16, 17, 16); }
                    else { g.DrawRectangle(pen, 3, 2, 12, 14); g.DrawLine(pen, 6, 6, 12, 6); g.DrawLine(pen, 6, 10, 12, 10); }
                    navigationIcons.Images.Add(key, bitmap);
                }
            }
            tree.ImageList = navigationIcons; tree.ShowPlusMinus = true; tree.ShowRootLines = true;
        }
        static string NavigationIconKey(string kind)
        { return kind.StartsWith("cal", StringComparison.Ordinal) ? "cal" : kind.StartsWith("dem", StringComparison.Ordinal) ? "dem" : kind.StartsWith("loc", StringComparison.Ordinal) ? "loc" : kind.StartsWith("stats", StringComparison.Ordinal) ? "stats" : "project"; }
    }
}
