using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.ReportGenerator
{
    // Shared by the report footer and review dialog. Long translated captions
    // wrap, then move to another row; text is not shrunk or intentionally elided.
    internal static class ReportActionLayout
    {
        internal static int Arrange(TableLayoutPanel panel, Control[] controls, params int[] choices)
        {
            int available = Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal);
            int columns = 1;
            foreach (int candidate in choices)
            {
                if (candidate > controls.Length) continue;
                int width = Math.Max(1, available / candidate);
                bool fits = true;
                foreach (Control control in controls)
                {
                    int textWidth = Math.Max(1, width - control.Margin.Horizontal - (control is CheckBox ? 26 : 12));
                    Size measure = TextRenderer.MeasureText(control.Text, control.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    if (measure.Width > textWidth || measure.Height > control.Font.Height * 2 + 4) { fits = false; break; }
                }
                if (fits) { columns = candidate; break; }
            }
            int rows = (controls.Length + columns - 1) / columns;
            if (panel.ColumnCount != columns || panel.RowCount != rows || panel.RowStyles.Count != rows || panel.ColumnStyles.Count != columns || panel.ColumnStyles.Cast<ColumnStyle>().Any(c => c.SizeType != SizeType.Percent || Math.Abs(c.Width - 100f / columns) > .1f))
            {
                panel.SuspendLayout();
                panel.ColumnCount = columns; panel.RowCount = rows;
                panel.ColumnStyles.Clear(); panel.RowStyles.Clear();
                for (int column = 0; column < columns; column++) panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
                for (int row = 0; row < rows; row++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                for (int i = 0; i < controls.Length; i++) panel.SetCellPosition(controls[i], new TableLayoutPanelCellPosition(i % columns, i / columns));
                panel.ResumeLayout(false);
            }
            int height = panel.Padding.Vertical;
            for (int row = 0; row < rows; row++)
            {
                int rowHeight = 36;
                for (int i = row * columns; i < Math.Min(controls.Length, (row + 1) * columns); i++)
                {
                    Control control = controls[i];
                    int width = Math.Max(1, available / columns - control.Margin.Horizontal - (control is CheckBox ? 26 : 12));
                    int textHeight = TextRenderer.MeasureText(control.Text, control.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                    rowHeight = Math.Max(rowHeight, textHeight + control.Margin.Vertical + 12);
                }
                if (Math.Abs(panel.RowStyles[row].Height - rowHeight) > .1) panel.RowStyles[row].Height = rowHeight;
                height += rowHeight;
            }
            return height;
        }
    }
}
