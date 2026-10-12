using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal static class ProjectDialogBrand
    {
        internal static void Apply(Form form)
        {
            HVX.Presentation.OemUiBranding.Apply(form);
        }
    }

    internal sealed class TextPromptDialog : Form
    {
        readonly TextBox input;
        internal string Value { get { return input.Text.Trim(); } }

        internal TextPromptDialog(string title, string prompt, string initial, int languageIndex)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(420, 138);
            Font = ProjectCreationTheme.UiFont(9f);
            ProjectDialogBrand.Apply(this);
            BackColor = ProjectCreationTheme.Workspace;
            Label label = new Label { Text = prompt, AutoSize = false, Location = new Point(18, 16), Size = new Size(384, 22), ForeColor = ProjectCreationTheme.Ink };
            input = new TextBox { Text = initial ?? string.Empty, Location = new Point(18, 44), Size = new Size(384, 26), BorderStyle = BorderStyle.FixedSingle };
            ProjectCreationButton cancel = new ProjectCreationButton { Text = ProjectCreationLocalization.T(languageIndex, "取消"), DialogResult = DialogResult.Cancel, Location = new Point(226, 88), Size = new Size(82, 34) };
            ProjectCreationButton ok = new ProjectCreationButton { Text = ProjectCreationLocalization.T(languageIndex, "确定"), DialogResult = DialogResult.OK, Primary = true, Location = new Point(320, 88), Size = new Size(82, 34) };
            Controls.AddRange(new Control[] { label, input, cancel, ok });
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    internal sealed class PressureEditorDialog : Form
    {
        readonly DataGridView grid;
        readonly List<PressureStep> defaults;
        internal List<PressureStep> Steps { get; private set; }

        readonly int languageIndex;

        internal PressureEditorDialog(string mode, IEnumerable<PressureStep> steps, IEnumerable<PressureStep> defaultSteps, int languageIndex)
        {
            this.languageIndex = ProjectCreationLocalization.NormalizeLanguage(languageIndex);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Text = ProjectCreationLocalization.T(languageIndex, "加压列表编辑 · ") + ProjectCreationLocalization.TranslateCanonical(languageIndex, mode);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(520, 350);
            ClientSize = new Size(640, 480);
            Font = ProjectCreationTheme.UiFont(9f);
            BackColor = ProjectCreationTheme.Workspace;
            ProjectDialogBrand.Apply(this);
            defaults = defaultSteps.Select(item => item.Clone()).ToList();

            Label hint = new Label
            {
                Text = ProjectCreationLocalization.T(languageIndex, "每行表示：加压倍数 × 次数。电压值由相电压与倍数计算。"),
                Dock = DockStyle.Fill, AutoSize = false, UseMnemonic = false,
                Padding = new Padding(12, 8, 12, 8),
                ForeColor = ProjectCreationTheme.SecondaryInk, Margin = Padding.Empty
            };
            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = ProjectCreationTheme.Surface,
                BorderStyle = BorderStyle.None,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                ScrollBars = ScrollBars.Vertical, Margin = new Padding(12, 0, 12, 0)
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = ProjectCreationTheme.SurfaceAlt;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ProjectCreationTheme.Ink;
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.DefaultCellStyle.ForeColor = ProjectCreationTheme.Ink;
            grid.DefaultCellStyle.SelectionBackColor = ProjectCreationTheme.Active;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.GridColor = ProjectCreationTheme.Border;
            grid.Columns.Add("Multiplier", ProjectCreationLocalization.T(languageIndex, "加压倍数"));
            grid.Columns.Add("Repetitions", ProjectCreationLocalization.T(languageIndex, "次数"));
            foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            foreach (PressureStep step in steps) grid.Rows.Add(ProjectText.Number(step.Multiplier), step.Repetitions);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10), WrapContents = false,
                BackColor = ProjectCreationTheme.SurfaceAlt, Margin = Padding.Empty
            };
            ProjectCreationButton save = new ProjectCreationButton { Text = ProjectCreationLocalization.T(languageIndex, "保存"), Primary = true, Width = 92, DialogResult = DialogResult.None };
            ProjectCreationButton cancel = new ProjectCreationButton { Text = ProjectCreationLocalization.T(languageIndex, "取消"), Width = 92, DialogResult = DialogResult.Cancel };
            ProjectCreationButton reset = new ProjectCreationButton { Text = ProjectCreationLocalization.T(languageIndex, "恢复默认"), Width = 100 };
            save.Click += delegate { if (TryCollect()) DialogResult = DialogResult.OK; };
            reset.Click += delegate
            {
                grid.Rows.Clear();
                foreach (PressureStep step in defaults) grid.Rows.Add(ProjectText.Number(step.Multiplier), step.Repetitions);
            };
            actions.Controls.Add(save);
            actions.Controls.Add(cancel);
            actions.Controls.Add(reset);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            root.Controls.Add(hint, 0, 0);
            root.Controls.Add(grid, 0, 1);
            root.Controls.Add(actions, 0, 2);
            Controls.Add(root);
            EventHandler resize = delegate
            {
                int contentWidth = Math.Max(120, ClientSize.Width - hint.Padding.Horizontal - 12);
                root.RowStyles[0].Height = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(contentWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + hint.Padding.Vertical + 8;
                int height = 32;
                foreach (Button button in actions.Controls)
                {
                    button.Width = Math.Max(80, TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 30);
                    button.Height = Math.Max(32, button.Font.Height + 12);
                    height = Math.Max(height, button.Height);
                }
                root.RowStyles[2].Height = height + actions.Padding.Vertical + 8;
                grid.RowTemplate.Height = Math.Max(28, grid.Font.Height + 10);
                foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
                grid.AutoResizeColumnHeadersHeight();
            };
            SizeChanged += resize;
            Shown += delegate
            {
                Rectangle working = Screen.FromControl(Owner ?? this).WorkingArea;
                Size = new Size(Math.Min(Width, working.Width - 24), Math.Min(Height, working.Height - 24));
                resize(this, EventArgs.Empty);
                if (Owner != null) Location = new Point(Math.Max(working.Left, Math.Min(working.Right - Width, Owner.Left + (Owner.Width - Width) / 2)),
                    Math.Max(working.Top, Math.Min(working.Bottom - Height, Owner.Top + (Owner.Height - Height) / 2)));
            };
            resize(this, EventArgs.Empty);
            CancelButton = cancel;
        }

        bool TryCollect()
        {
            if (!grid.EndEdit()) return false;
            List<PressureStep> result = new List<PressureStep>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                string multiplierText = Convert.ToString(row.Cells[0].Value, CultureInfo.InvariantCulture);
                string repetitionText = Convert.ToString(row.Cells[1].Value, CultureInfo.InvariantCulture);
                double multiplier; int repetitions;
                if (!double.TryParse(multiplierText, NumberStyles.Float, CultureInfo.InvariantCulture, out multiplier) ||
                    !int.TryParse(repetitionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out repetitions) || repetitions <= 0)
                {
                    MessageBox.Show(this, ProjectCreationLocalization.T(languageIndex, "加压倍数必须为数字，次数必须为正整数。"), ProjectCreationLocalization.T(languageIndex, "数据错误"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                result.Add(new PressureStep { Multiplier = multiplier, Repetitions = repetitions });
            }
            try { VoltagePlanFile.Validate(result); }
            catch (InvalidDataException error)
            {
                MessageBox.Show(this, ProjectCreationLocalization.T(languageIndex, error.Message), ProjectCreationLocalization.T(languageIndex, "数据错误"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            Steps = result;
            return true;
        }
    }
}
