using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        static void ConfigureJointGridColumns(DataGridView grid)
        {
            if(grid==null||grid.Columns.Count<6)return;
            var font=grid.ColumnHeadersDefaultCellStyle.Font??grid.Font;
            int padding=Math.Max(14,font.Height);
            foreach(DataGridViewColumn column in grid.Columns)
            {
                int header=TextRenderer.MeasureText(column.HeaderText,font,Size.Empty,
                    TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+padding;
                bool compact=column.Name=="Phase"||column.Name=="Node";
                column.AutoSizeMode=compact?DataGridViewAutoSizeColumnMode.None:DataGridViewAutoSizeColumnMode.Fill;
                column.MinimumWidth=Math.Max(compact?42:65,header);
                if(compact)column.Width=column.MinimumWidth;
                column.FillWeight=column.Name.StartsWith("Material",StringComparison.Ordinal)?32:20;
                // Keep each entry on one line. Wrapped cells can make two
                // rows taller than the outer editor's viewport at large fonts.
                // Full values remain available through the grid's tooltips.
                column.DefaultCellStyle.WrapMode=DataGridViewTriState.False;
            }
            grid.ColumnHeadersDefaultCellStyle.WrapMode=DataGridViewTriState.False;
            grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None;
            int rowHeight=Math.Max(28,grid.Font.Height+8);
            grid.RowTemplate.Height=rowHeight;
            foreach(DataGridViewRow row in grid.Rows)row.Height=rowHeight;
            grid.AutoResizeColumnHeadersHeight();
        }
    }
}
