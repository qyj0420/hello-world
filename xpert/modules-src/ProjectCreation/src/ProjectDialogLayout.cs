using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.Controls
{
    // Limits only the window, not the font or the data. Callers keep a scrollable
    // body and a separately docked action bar when the working area is too short.
    internal static class ProjectDialogLayout
    {
        internal static void FitToWorkingArea(Form dialog, Rectangle area, Size minimum)
        {
            int width = Math.Max(1, area.Width - 24), height = Math.Max(1, area.Height - 24);
            dialog.MinimumSize = new Size(Math.Min(minimum.Width, width), Math.Min(minimum.Height, height));
            dialog.Size = new Size(Math.Min(dialog.Width, width), Math.Min(dialog.Height, height));
            dialog.Location = new Point(Math.Max(area.Left + 12, Math.Min(dialog.Left, area.Right - dialog.Width - 12)),
                Math.Max(area.Top + 12, Math.Min(dialog.Top, area.Bottom - dialog.Height - 12)));
        }

        internal static void MeasureActions(FlowLayoutPanel actions, Font font, int width)
        {
            foreach (Control action in actions.Controls)
            {
                action.Font = font;
                action.Width = Math.Max(100, TextRenderer.MeasureText(action.Text, font).Width + 32);
                action.Height = Math.Max(36, font.Height + 18);
            }
            actions.Height = actions.GetPreferredSize(new Size(Math.Max(40, width), 0)).Height + 4;
        }
    }
}
