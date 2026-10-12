using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.ZeroBench
{
    internal sealed partial class MainForm
    {
        private readonly StageDiagnostics stageDiagnostics = new StageDiagnostics();
        private readonly ToolTip stageTips = new ToolTip { AutoPopDelay = 20000 };

        private Control BuildStageLamps()
        {
            Disposed += (s, e) => stageTips.Dispose();
            var sequence = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 2 };
            for (int i = 0; i < 7; i++) sequence.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 7));
            sequence.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); sequence.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            for (int i = 0; i < StageDiagnostics.Keys.Length; i++)
            {
                string key = StageDiagnostics.Keys[i];
                var label = new Label { Dock = DockStyle.Fill, Margin = new Padding(3), TextAlign = ContentAlignment.MiddleCenter,
                    BackColor = Color.White, ForeColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand, TabStop = true, AccessibleRole = AccessibleRole.PushButton };
                label.Click += (s, e) => ShowStageDetails(key);
                label.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.Handled = true; e.SuppressKeyPress = true; ShowStageDetails(key); } };
                lamps.Add(key, label); sequence.Controls.Add(label, i % 7, i / 7);
            }
            var legend = new Label { Dock = DockStyle.Fill, Margin = new Padding(3), TextAlign = ContentAlignment.MiddleCenter,
                Text = "绿=通过  浅红=错误\n黄=跳过  点击查看详情", ForeColor = Color.DarkSlateGray };
            sequence.Controls.Add(legend, 6, 1); RefreshStageLamps(); return sequence;
        }

        private void ResetStageDiagnostics() { stageDiagnostics.Reset(); RefreshStageLamps(); }

        private void ApplyStageDiagnostic(string key, string message)
        {
            if (!stageDiagnostics.TryApply(key, message))
            {
                if (key == "ERROR") stageDiagnostics.Abort(message, false);
                else if (key == "CANCELED") stageDiagnostics.Abort(message, true);
                else stageDiagnostics.Observe(key, message);
            }
            RefreshStageLamps();
        }

        private void RefreshStageLamps()
        {
            foreach (string key in StageDiagnostics.Keys)
            {
                Label label; if (!lamps.TryGetValue(key, out label)) continue;
                StageDiagnosticRecord record = stageDiagnostics[key];
                label.Text = record.Caption + "\n● " + StageDiagnostics.StateText(record.State);
                label.ForeColor = Color.DarkSlateGray;
                switch (record.State)
                {
                    case StageState.Passed: label.BackColor = Color.FromArgb(195, 234, 207); label.ForeColor = Color.FromArgb(20, 91, 42); break;
                    case StageState.Failed: label.BackColor = Color.FromArgb(255, 210, 210); label.ForeColor = Color.DarkRed; break;
                    case StageState.Skipped: label.BackColor = Color.FromArgb(255, 238, 151); label.ForeColor = Color.FromArgb(103, 76, 0); break;
                    case StageState.Running: label.BackColor = Color.FromArgb(200, 226, 255); label.ForeColor = Color.FromArgb(15, 64, 111); break;
                    case StageState.Unverified: label.BackColor = Color.FromArgb(231, 218, 247); label.ForeColor = Color.FromArgb(90, 53, 126); break;
                    case StageState.Canceled: label.BackColor = Color.FromArgb(220, 224, 229); label.ForeColor = Color.FromArgb(68, 74, 80); break;
                    default: label.BackColor = Color.White; label.ForeColor = Color.Gray; break;
                }
                label.AccessibleName = record.Caption + "：" + StageDiagnostics.StateText(record.State) + "，点击查看详细记录";
                stageTips.SetToolTip(label, record.Caption + "：" + StageDiagnostics.StateText(record.State) + "\n点击查看完整异常、错误码、时间和步骤记录。");
            }
        }

        private Form CreateStageDetailsDialog(string key)
        {
            StageDiagnosticRecord record = stageDiagnostics[key];
            var dialog = new Form { Text = "雄新电力科技 · 步骤诊断 · " + record.Caption, StartPosition = FormStartPosition.CenterParent,
                Size = new Size(900, 620), MinimumSize = new Size(650, 420), Font = Font, BackColor = BackColor, ShowInTaskbar = false };
            if (Icon != null) dialog.Icon = (Icon)Icon.Clone();
            var container = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
            container.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); container.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Dock = DockStyle.Fill, BackColor = Color.White, Text = stageDiagnostics.Details(key) };
            container.Controls.Add(details, 0, 0);
            var close = new Button { Text = "关闭", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 100 };
            container.Controls.Add(close, 0, 1); dialog.Controls.Add(container); dialog.AcceptButton = close; dialog.CancelButton = close;
            return dialog;
        }
        private void ShowStageDetails(string key) { using (var dialog = CreateStageDetailsDialog(key)) dialog.ShowDialog(this); }

        // Offline UI verification hook: shows a preserved trigger failure after successful cleanup.
        internal void SetDiagnosticFailureSmokeState()
        {
            ResetStageDiagnostics();
            Progress("STEP_INIT_PASSED", "界面测试：两卡初始化记录（未访问硬件）");
            Progress("STEP_AO_ZERO_SKIPPED", "未创建 AO 任务，未写模拟输出。");
            Progress("STEP_HV_ON_SKIPPED", "HV ON 未执行。");
            Progress("STEP_ARM_PASSED", "界面测试：两卡已布置。");
            Progress("STEP_TRG_OUTPUT_UNVERIFIED", "界面测试：P0.7 写调用成功，但物理电平没有独立回读。");
            Progress("STEP_WAIT_TRIGGER_FAILED", "界面测试：TimeoutException。ART 0/4800；Pico 等待完成回调。错误码示例 PICO_STATUS=0x00000003。");
            Progress("STEP_RELEASE_PASSED", "界面测试：设备任务已清理；不能据此抹去触发失败。");
            Progress("STEP_COMPLETE_FAILED", "界面测试：无完整同步采集结果。");
        }
    }
}
