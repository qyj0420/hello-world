using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.OWTS.Task38;

namespace HVX.OWTS.Task38.Desktop
{
    internal sealed class ArtDeviceCleanupForm : BrandedForm
    {
        private readonly ArtDeviceCleanup cleanup;
        private readonly bool readOnly;
        private readonly ListView devices = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
        private readonly Button refresh = new Button { Text = "刷新设备列表", AutoSize = true, Height = 34 };
        private readonly Button remove = new Button { Text = "删除所选离线设备", AutoSize = true, Height = 34, Enabled = false };
        private readonly Button close = new Button { Text = "关闭", AutoSize = true, Height = 34 };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(8), Text = "准备读取控制模块列表……" };
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
        private bool busy;
        internal ArtDeviceCleanupForm(ArtDeviceCleanup service, bool previewOnly = false)
        {
            cleanup = service; readOnly = previewOnly;
            Text = "任务 38 · 清理离线控制模块" + (readOnly ? "（只读预览）" : "");
            Size = new Size(820, 470); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.White;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "本窗口管理真实控制模块，与主界面的仿真模式无关。\n仅删除所选离线兼容模块的登记记录；不卸载驱动，不自动重命名设备。", Padding = new Padding(2, 4, 2, 4) }, 0, 0);
            devices.Columns.Add("设备名称", 100); devices.Columns.Add("型号", 120); devices.Columns.Add("序列号", 145);
            devices.Columns.Add("连接状态", 170); devices.Columns.Add("可执行操作", 205);
            layout.Controls.Add(devices, 0, 1); layout.Controls.Add(progress, 0, 2); layout.Controls.Add(status, 0, 3);
            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
            buttons.Controls.AddRange(new Control[] { refresh, remove, close }); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
            refresh.Click += async (s, e) => await RefreshDevices();
            remove.Click += async (s, e) => await RemoveSelected();
            close.Click += (s, e) => Close();
            devices.SelectedIndexChanged += (s, e) => UpdateControls();
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        }
        private ArtDeviceRecord Selected { get { return devices.SelectedItems.Count == 1 ? devices.SelectedItems[0].Tag as ArtDeviceRecord : null; } }
        private void UpdateControls()
        { refresh.Enabled = close.Enabled = devices.Enabled = !busy; progress.Visible = busy; remove.Enabled = !busy && !readOnly && Selected != null && Selected.CanRemove; }
        private void ShowRecords(ArtDeviceRecord[] records)
        {
            devices.Items.Clear();
            foreach (ArtDeviceRecord d in records)
            {
                string state = d.Offline ? "离线" : String.IsNullOrEmpty(d.InstanceId) ? "状态待确认" : "已连接／不可删除";
                ListViewItem item = new ListViewItem(new[] { d.DeviceName, d.DeviceType, d.SerialNumber.ToString(), state,
                    d.CanRemove ? "可删除离线记录" : "禁止删除" }) { Tag = d };
                if (!d.CanRemove) item.ForeColor = Color.DimGray;
                devices.Items.Add(item);
            }
        }
        internal async Task RefreshDevices()
        {
            if (busy) return;
            busy = true; UpdateControls(); status.Text = "正在刷新设备列表……";
            try
            {
                ArtDeviceRecord[] records = await Task.Run(() => cleanup.ReadFresh());
                ShowRecords(records);
                status.Text = records.Length == 0 ? "没有已登记的控制模块。" : "已读取 " + records.Length + " 个设备。请选择要清理的离线控制模块。";
                if (readOnly) status.Text += " 当前为只读预览。";
            }
            catch (Exception e) { devices.Items.Clear(); status.Text = "读取失败：" + OperatorDiagnostics.Report(e); }
            finally { busy = false; UpdateControls(); }
        }
        private async Task RemoveSelected()
        {
            ArtDeviceRecord selected = Selected;
            if (busy || readOnly || selected == null || !selected.CanRemove) return;
            if (MessageBox.Show(this, "删除离线设备 " + selected.DeviceName + "？\n型号：" + selected.DeviceType + "\n序列号：" + selected.SerialNumber +
                "\n\n将保存原记录并再次检查离线状态。请在本次操作期间保持设备拔出。", "确认删除设备记录", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = true; UpdateControls(); status.Text = "正在复核离线状态、保存原记录并删除……";
            try
            {
                ArtDeviceRemovalResult result = await Task.Run(() => cleanup.RemoveOffline(selected));
                devices.Items.Clear();
                status.Text = DeviceMessages.Describe(result.Message) + "\n原记录：" + result.BackupPath;
                MessageBox.Show(this, DeviceMessages.Describe(result.Message) + "\n\n原记录已保存：\n" + result.BackupPath,
                    "清理结果", MessageBoxButtons.OK, result.VerifiedAbsent ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                // Keep the outcome visible even if a subsequent list refresh fails.
                try { ShowRecords(await Task.Run(() => cleanup.ReadFresh())); }
                catch (Exception e) { status.Text += "\n列表刷新失败：" + OperatorDiagnostics.Report(e); }
            }
            catch (Exception e) { devices.Items.Clear(); status.Text = "清理失败：" + OperatorDiagnostics.Report(e); MessageBox.Show(this, status.Text, "清理离线设备", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { busy = false; UpdateControls(); }
        }
    }
}
