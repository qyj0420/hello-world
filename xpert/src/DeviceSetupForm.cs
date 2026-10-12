using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class DeviceSetupForm : Form
    {
        readonly DataGridView stepsGrid;
        readonly bool recheckAllowed;
        internal DeviceSetupAction RequestedAction { get; private set; }

        internal DeviceSetupForm(AppSettings settings, HardwareStatusSnapshot actual, bool loaded, bool checking)
        {
            settings = settings ?? new AppSettings();
            bool zh = AppLanguages.IsChinese(settings.LanguageCode);
            Func<string, string> display = value => AppLanguages.Display(settings.LanguageCode, value);
            Text = display(zh ? "HV DAC Xpert · 首次配置与状态检查" : "HV DAC Xpert · Initial setup and status");
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = AppTheme.Font(10.5f, FontStyle.Regular);
            ClientSize = new Size(960, 660); MinimumSize = new Size(760, 500);
            BackColor = AppTheme.Canvas; AppTheme.ApplyDialogIcon(this);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
            // Constrain the column before measuring wrapped labels. Otherwise an English
            // AutoSize label can widen the entire table beyond the form's visible client area.
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var intro = new Label { Name = "SetupIntroduction", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 3, 3, 6), TextAlign = ContentAlignment.MiddleLeft,
                Text = display(zh ? "双击、操作链接或 Enter 打开设置；原授权仍适用。这里只检查与导航，不安装、不采集、不输出高压。" : "Double-click, action link or Enter opens protected settings. No installation, acquisition or HV output.") };
            var grid = new DataGridView { Name = "DeviceSetupSteps", Dock = DockStyle.Fill, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = Color.White,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical,
                AllowUserToResizeRows = false, AllowUserToOrderColumns = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.DefaultCellStyle.Padding = new Padding(6);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.Surface;
            grid.DefaultCellStyle.ForeColor = AppTheme.Ink; grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 163, 199);
            grid.DefaultCellStyle.SelectionForeColor = Color.White; grid.GridColor = AppTheme.Border;
            stepsGrid = grid;
            grid.AccessibleName = display(zh ? "配置步骤；双击或按 Enter 打开所需设置" : "Setup steps; double-click or press Enter to open required settings");
            grid.Columns.Add("step", display(zh ? "配置步骤" : "Setup step")); grid.Columns[0].FillWeight = 34;
            grid.Columns.Add("status", display(zh ? "状态" : "Status")); grid.Columns[1].FillWeight = 28;
            grid.Columns.Add(new DataGridViewLinkColumn { Name = "action", HeaderText = display(zh ? "操作" : "Action"), FillWeight = 38,
                TrackVisitedState = false, LinkColor = AppTheme.Ink, ActiveLinkColor = AppTheme.Ink, VisitedLinkColor = AppTheme.Ink });
            var instructionPanel = new GroupBox { Name = "SetupInstructionsPanel", Text = display(zh ? "所选步骤：依据与完成方法" : "Selected step: evidence and instructions"), Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 8) };
            var instructions = new TextBox { Name = "SetupInstructions", Dock = DockStyle.Fill, ReadOnly = true, Multiline = true,
                WordWrap = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = AppTheme.Canvas, ForeColor = AppTheme.Ink,
                AccessibleName = display(zh ? "所选步骤的依据与完成方法" : "Evidence and completion instructions for the selected step") };
            instructionPanel.Controls.Add(instructions);
            foreach (var step in DeviceSetupStatus.Steps(settings, actual, loaded, checking, zh))
            {
                int row = grid.Rows.Add(display(step.Name), display(step.Status), display(step.ActionLabel));
                grid.Rows[row].Tag = step;
                grid.Rows[row].Cells[1].Style.ForeColor = step.Complete ? AppTheme.Success : AppTheme.Warning;
                grid.Rows[row].Cells[2].ToolTipText = display(step.Detail);
                if (step.Action == DeviceSetupAction.None)
                    grid.Rows[row].Cells[2] = new DataGridViewTextBoxCell { Value = display(step.ActionLabel), ToolTipText = display(step.Detail) };
            }
            Action showInstructions = delegate
            {
                var step = grid.CurrentRow == null ? null : grid.CurrentRow.Tag as DeviceSetupStep;
                instructions.Text = step == null ? string.Empty : display(step.Name + "\r\n" + step.Detail);
                instructions.SelectionStart = 0;
            };
            // SelectionChanged may fire before CurrentRow moves to the new row.
            grid.CurrentCellChanged += delegate { showInstructions(); };
            showInstructions();
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { TryRequestStep(e.RowIndex); };
            grid.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.ColumnIndex == 2) TryRequestStep(e.RowIndex); };
            grid.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && grid.CurrentRow != null)
                { e.Handled = e.SuppressKeyPress = true; TryRequestStep(grid.CurrentRow.Index); }
            };
            var actions = new FlowLayoutPanel { Name = "SetupActions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
            var close = AppTheme.Button(display(zh ? "关闭" : "Close"), false); close.DialogResult = DialogResult.Cancel;
            var edit = AppTheme.Button(display(zh ? "设备参数" : "Device parameters"), true); edit.AutoSize = true;
            var refresh = AppTheme.Button(display(zh ? "重新检查连接" : "Recheck connection"), false); refresh.AutoSize = true;
            var firstBind = AppTheme.Button(display(zh ? "首次绑定设备" : "First device setup"), false); firstBind.AutoSize = true;
            edit.Click += delegate { RequestAction(DeviceSetupAction.DeviceParameters); };
            refresh.Click += delegate { RequestAction(DeviceSetupAction.RecheckConnection); };
            firstBind.Click += delegate { RequestAction(DeviceSetupAction.FirstBinding); };
            foreach (var button in new[] { close, edit, refresh, firstBind })
            { button.AutoSize = true; button.AutoEllipsis = false; button.MinimumSize = new Size(92, 38); button.Padding = new Padding(10, 2, 10, 2); button.Margin = new Padding(2); }
            close.Name = "CloseSetup"; edit.Name = "ReviewParameters"; refresh.Name = "RecheckConnection";
            firstBind.Name = "FirstHardwareBinding";
            recheckAllowed = settings.Hardware != null && settings.Hardware.Enabled && !checking;
            refresh.Enabled = recheckAllowed;
            firstBind.Visible = settings.Hardware == null || !settings.Hardware.Enabled;
            firstBind.Enabled = firstBind.Visible && !checking;
            actions.Controls.Add(close); actions.Controls.Add(edit); actions.Controls.Add(refresh); actions.Controls.Add(firstBind);
            layout.Controls.Add(intro, 0, 0); layout.Controls.Add(grid, 0, 1); layout.Controls.Add(instructionPanel, 0, 2); layout.Controls.Add(actions, 0, 3);
            Controls.Add(layout); CancelButton = close;
            Shown += delegate
            {
                Rectangle area = Screen.FromControl(Owner ?? this).WorkingArea;
                int maxWidth = Math.Max(480, area.Width - 32), maxHeight = Math.Max(400, area.Height - 32);
                MinimumSize = new Size(Math.Min(MinimumSize.Width, maxWidth), Math.Min(MinimumSize.Height, maxHeight));
                Size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            };
        }

        internal bool TryRequestStep(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= stepsGrid.Rows.Count) return false;
            var step = stepsGrid.Rows[rowIndex].Tag as DeviceSetupStep;
            return step != null && RequestAction(step.Action);
        }

        bool RequestAction(DeviceSetupAction action)
        {
            if (RequestedAction != DeviceSetupAction.None || action == DeviceSetupAction.None ||
                (action == DeviceSetupAction.RecheckConnection && !recheckAllowed)) return false;
            RequestedAction = action;
            DialogResult = DialogResult.OK;
            return true;
        }
    }
}
