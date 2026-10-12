using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.ReportGenerator
{
    internal sealed class StatisticsRangeOption
    {
        internal VoltageInterval Interval;
        internal bool Include = true;
        internal bool Maximum = true;
        internal bool Average = true;
        internal bool Count = true;

        internal StatisticsRangeOption Clone()
        {
            return new StatisticsRangeOption
            {
                Interval = new VoltageInterval(Interval.Id, Interval.Title, Interval.MinimumU0, Interval.MaximumU0, true, false),
                Include = Include,
                Maximum = Maximum,
                Average = Average,
                Count = Count
            };
        }
    }

    internal sealed class VoltageRangeDialog : Form
    {
        readonly NumericUpDown minimum = new NumericUpDown();
        readonly NumericUpDown maximum = new NumericUpDown();
        readonly TextBox title = new TextBox();
        readonly Label message = new Label();
        readonly int languageIndex;

        internal VoltageInterval ResultInterval { get; private set; }

        internal VoltageRangeDialog(int languageIndex, VoltageInterval current)
        {
            this.languageIndex = languageIndex;
            Text = ReportLanguages.Text(languageIndex, ReportLanguages.UiKeys.VoltageRange);
            ClientSize = new Size(520, 310);
            MinimumSize = new Size(420, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = ReportTheme.Canvas;
            Font = ReportTheme.UiFont(9.5f, FontStyle.Regular);
            ReportTheme.ApplyDialogIcon(this);

            Padding = new Padding(12);
            var viewport = new Panel { Name = "VoltageRangeViewport", Dock = DockStyle.Fill, AutoScroll = true };
            var shell = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), ColumnCount = 2, RowCount = 4, BackColor = ReportTheme.Canvas };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            for(int row=0;row<4;row++)shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            viewport.Controls.Add(shell);Controls.Add(viewport);

            minimum.DecimalPlaces = 2; minimum.Increment = 0.05M; minimum.Minimum = 0; minimum.Maximum = 2; minimum.Dock = DockStyle.Fill;
            maximum.DecimalPlaces = 2; maximum.Increment = 0.05M; maximum.Minimum = 0; maximum.Maximum = 2; maximum.Dock = DockStyle.Fill;
            title.Dock = DockStyle.Fill;
            minimum.Value = current == null ? 0 : (decimal)current.MinimumU0;
            maximum.Value = current == null ? 1 : (decimal)current.MaximumU0;
            title.Text = current == null ? string.Empty : current.Title;

            AddRow(shell, 0, ReportLanguages.Ui(languageIndex, "下限（U0倍数）", "Minimum (×U0)"), minimum);
            AddRow(shell, 1, ReportLanguages.Ui(languageIndex, "上限（U0倍数）", "Maximum (×U0)"), maximum);
            AddRow(shell, 2, ReportLanguages.Ui(languageIndex, "显示名称（可选）", "Display name (optional)"), title);
            message.Dock = DockStyle.Fill; message.AutoSize = true; message.MinimumSize = new Size(0, 38); message.ForeColor = ReportTheme.Error; message.TextAlign = ContentAlignment.MiddleLeft;
            shell.Controls.Add(message, 0, 3); shell.SetColumnSpan(message, 2);

            var footer = new FlowLayoutPanel { Name = "VoltageRangeActions", Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
            var cancel = new ReportButton { Text = ReportLanguages.Text(languageIndex, ReportLanguages.UiKeys.Cancel), DialogResult = DialogResult.Cancel, Width = 92 };
            var ok = new ReportButton { Text = ReportLanguages.Ui(languageIndex, "确定", "OK"), Width = 92 };
            ReportTheme.StyleSecondaryButton(cancel); ReportTheme.StylePrimaryButton(ok);
            footer.Controls.Add(cancel); footer.Controls.Add(ok);
            Controls.Add(footer);
            CancelButton = cancel; AcceptButton = ok;
            ok.Click += OnAccept;
            bool arranging=false;
            Action fit=()=>{
                if(arranging)return;arranging=true;
                try {
                    int available=Math.Max(100,shell.ClientSize.Width-shell.Padding.Horizontal);
                    for(int row=0;row<3;row++) {
                        var label=(Label)shell.GetControlFromPosition(0,row);
                        label.MaximumSize=new Size(Math.Max(80,(int)(available*.54)-label.Margin.Horizontal),0);
                        var frame=shell.GetControlFromPosition(1,row);var editor=frame.Controls[0];
                        frame.Height=Math.Max(36,editor.GetPreferredSize(Size.Empty).Height+14);
                    }
                    message.MaximumSize=new Size(available-message.Margin.Horizontal,0);
                }finally{arranging=false;}
            };
            shell.SizeChanged+=(s,e)=>fit();foreach(Control field in shell.Controls){field.FontChanged+=(s,e)=>fit();foreach(Control editor in field.Controls)editor.FontChanged+=(s,e)=>fit();}
            Shown+=(s,e)=>fit();ReportTheme.BindDialogFooter(this,footer);ReportTheme.ReserveDialogBody(this,footer,new Size(420,300));
        }

        static void AddRow(TableLayoutPanel shell, int row, string labelText, Control editor)
        {
            var label = new Label { Name = "VoltageRangeLabel" + row, AutoSize = true, Text = labelText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = ReportTheme.Ink };
            var frame = new Panel { Dock = DockStyle.Fill, Height = 36, BackColor = ReportTheme.Border, Padding = new Padding(1), Margin = new Padding(3) };
            editor.BackColor = ReportTheme.Surface; editor.ForeColor = ReportTheme.Ink;
            var text = editor as TextBox; if (text != null) text.BorderStyle = BorderStyle.None;
            var numeric = editor as NumericUpDown; if (numeric != null) numeric.BorderStyle = BorderStyle.None;
            frame.Controls.Add(editor);
            shell.Controls.Add(label, 0, row); shell.Controls.Add(frame, 1, row);
        }

        void OnAccept(object sender, EventArgs e)
        {
            double low = (double)minimum.Value;
            double high = (double)maximum.Value;
            if (low < 0.0 || high > 2.0 || low >= high)
            {
                message.Text = ReportLanguages.Text(languageIndex, ReportLanguages.UiKeys.RangeOutsideBounds);
                return;
            }
            string name = title.Text.Trim();
            if (name.Length == 0) name = FormatRange(low, high);
            ResultInterval = new VoltageInterval("custom_" + Guid.NewGuid().ToString("N"), name, low, high, true, false);
            DialogResult = DialogResult.OK;
            Close();
        }

        internal static string FormatRange(double low, double high)
        {
            return low.ToString("0.##", CultureInfo.InvariantCulture) + "U0≤U≤" + high.ToString("0.##", CultureInfo.InvariantCulture) + "U0";
        }
    }

    internal sealed class ReportSettingsDialog : Form
    {
        readonly DataGridView grid = new DataGridView();
        readonly List<StatisticsRangeOption> working;
        readonly int languageIndex;

        internal IList<StatisticsRangeOption> Options { get { return working.Select(item => item.Clone()).ToList(); } }

        internal ReportSettingsDialog(int languageIndex, IEnumerable<StatisticsRangeOption> current)
        {
            this.languageIndex = languageIndex;
            working = current == null ? new List<StatisticsRangeOption>() : current.Select(item => item.Clone()).ToList();
            Text = ReportLanguages.Ui(languageIndex, "报告统计设置", "Report statistics settings");
            ClientSize = new Size(720, 390);
            MinimumSize = new Size(620, 350);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ReportTheme.Canvas;
            Font = ReportTheme.UiFont(9.5f, FontStyle.Regular);
            ShowInTaskbar = false;
            ReportTheme.ApplyDialogIcon(this);

            Padding = new Padding(14);
            var viewport = new Panel { Name = "ReportStatisticsViewport", Dock = DockStyle.Fill, AutoScroll = true };
            var shell = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            viewport.Controls.Add(shell);Controls.Add(viewport);
            var intro = new Label {
                Dock = DockStyle.Fill, ForeColor = ReportTheme.Muted, TextAlign = ContentAlignment.MiddleLeft,
                Text = ReportLanguages.Ui(languageIndex, "增加0–2U0内的统计区间，并选择写入报告的最大放电量、平均放电量和放电次数。", "Add ranges within 0–2U0 and choose maximum charge, average charge and discharge count for the report.")
            };
            shell.Controls.Add(intro, 0, 0);

            ConfigureGrid();
            shell.Controls.Add(grid, 0, 1);
            var footer = new FlowLayoutPanel { Name = "ReportStatisticsActions", Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
            var cancel = new ReportButton { Text = ReportLanguages.Text(languageIndex, ReportLanguages.UiKeys.Cancel), DialogResult = DialogResult.Cancel, Width = 92 };
            var ok = new ReportButton { Text = ReportLanguages.Ui(languageIndex, "应用", "Apply"), DialogResult = DialogResult.OK, Width = 92 };
            var remove = new ReportButton { Text = ReportLanguages.Ui(languageIndex, "删除区间", "Remove range"), Width = 112 };
            var add = new ReportButton { Text = ReportLanguages.Ui(languageIndex, "增加区间", "Add range"), Width = 112 };
            ReportTheme.StyleSecondaryButton(cancel); ReportTheme.StylePrimaryButton(ok); ReportTheme.StyleSecondaryButton(remove); ReportTheme.StyleSecondaryButton(add);
            footer.Controls.Add(cancel); footer.Controls.Add(ok); footer.Controls.Add(remove); footer.Controls.Add(add);
            Controls.Add(footer);
            CancelButton = cancel; AcceptButton = ok;
            add.Click += (s, e) => AddRange();
            remove.Click += (s, e) => RemoveRange();
            ok.Click += (s, e) => CommitGrid();
            ReportTheme.BindDialogFooter(this,footer);ReportTheme.ReserveDialogBody(this,footer,new Size(620,350));
            bool arranging=false;
            Action fitBody=()=>{
                if(arranging)return;arranging=true;
                try {
                    int width=Math.Max(100,viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-intro.Margin.Horizontal);
                    int introHeight=Math.Max(48,TextRenderer.MeasureText(intro.Text,intro.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height+12);
                    shell.RowStyles[0].Height=introHeight;
                    shell.Height=Math.Max(viewport.ClientSize.Height,introHeight+Math.Max(180,grid.ColumnHeadersHeight+grid.Font.Height*4)+grid.Margin.Vertical);
                }finally{arranging=false;}
            };
            viewport.SizeChanged+=(s,e)=>fitBody();intro.FontChanged+=(s,e)=>fitBody();grid.FontChanged+=(s,e)=>fitBody();grid.ColumnHeadersHeightChanged+=(s,e)=>fitBody();Shown+=(s,e)=>fitBody();
            RefreshGrid();
        }

        void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = ReportTheme.Surface; grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false; grid.AutoGenerateColumns = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = ReportTheme.Header;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ReportTheme.Ink; grid.ColumnHeadersDefaultCellStyle.Font = ReportTheme.UiFont(9.5f, FontStyle.Bold);
            // The host can enlarge fonts after opening a dialog. Keep header
            // measurement live so bold or wrapped captions are never clipped
            // by DataGridView's default fixed 23-pixel header.
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(3, 4, 3, 4);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.DefaultCellStyle.BackColor = ReportTheme.Surface; grid.AlternatingRowsDefaultCellStyle.BackColor = ReportTheme.SurfaceAlt;
            grid.DefaultCellStyle.ForeColor = ReportTheme.Ink; grid.DefaultCellStyle.SelectionBackColor = ReportTheme.Selection;
            grid.DefaultCellStyle.SelectionForeColor = Color.White; grid.GridColor = ReportTheme.Border; grid.RowTemplate.Height = 29;
            // Range titles are user-defined. Preserve complete rows when a
            // narrow viewport or the host's accessibility font wraps them.
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = ReportLanguages.Ui(languageIndex, "启用", "Use"), Width = 56 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Range", HeaderText = ReportLanguages.Ui(languageIndex, "U0区间", "U0 range"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Maximum", HeaderText = ReportLanguages.Ui(languageIndex, "最大放电量", "Maximum"), Width = 108 });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Average", HeaderText = ReportLanguages.Ui(languageIndex, "平均放电量", "Average"), Width = 108 });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Count", HeaderText = ReportLanguages.Ui(languageIndex, "放电次数", "Count"), Width = 96 });
        }

        void RefreshGrid()
        {
            grid.Rows.Clear();
            foreach (StatisticsRangeOption option in working)
            {
                int index = grid.Rows.Add(option.Include, option.Interval.Title, option.Maximum, option.Average, option.Count);
                grid.Rows[index].Tag = option;
            }
        }

        void AddRange()
        {
            using (var dialog = new VoltageRangeDialog(languageIndex, null))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                VoltageInterval candidate = dialog.ResultInterval;
                if (working.Any(item => Math.Abs(item.Interval.MinimumU0 - candidate.MinimumU0) < 1e-9 && Math.Abs(item.Interval.MaximumU0 - candidate.MaximumU0) < 1e-9))
                {
                    MessageBox.Show(this, ReportLanguages.Text(languageIndex, ReportLanguages.UiKeys.RangeOverlap), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                working.Add(new StatisticsRangeOption { Interval = candidate });
                RefreshGrid();
            }
        }

        void RemoveRange()
        {
            if (grid.SelectedRows.Count != 1) return;
            StatisticsRangeOption option = grid.SelectedRows[0].Tag as StatisticsRangeOption;
            if (option != null) working.Remove(option);
            RefreshGrid();
        }

        void CommitGrid()
        {
            grid.EndEdit();
            foreach (DataGridViewRow row in grid.Rows)
            {
                StatisticsRangeOption option = row.Tag as StatisticsRangeOption;
                if (option == null) continue;
                option.Include = Convert.ToBoolean(row.Cells["Include"].Value ?? false);
                option.Maximum = Convert.ToBoolean(row.Cells["Maximum"].Value ?? false);
                option.Average = Convert.ToBoolean(row.Cells["Average"].Value ?? false);
                option.Count = Convert.ToBoolean(row.Cells["Count"].Value ?? false);
            }
        }
    }
}
