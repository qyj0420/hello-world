using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal enum FactorySequenceChoice { Cancel, Continue, Restart }
    internal sealed class FactorySequenceResumeDialog : Form
    {
        internal FactorySequenceChoice Choice { get; private set; }
        internal readonly Button ContinueButton, RestartButton, CancelChoice;
        internal FactorySequenceResumeDialog(FactorySequenceProgress progress, FactorySettings current, int requestedCount, Icon icon)
        {
            bool compatible = progress.Matches(current,requestedCount);
            Text = "中断后连续加压 · 选择操作"; Icon = icon;
            Font = new Font("Microsoft YaHei UI",10); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(680,300); ShowInTaskbar = false;
            var text = new Label { Dock = DockStyle.Fill, Padding = new Padding(20), Text =
                "当前目标 " + current.TargetKv.ToString("0.000") + " kV · " + (current.Simulation ? "仿真" : "实机") + "\n" +
                progress.Describe() + "\n\n" + (compatible ?
                "设备及参数未改变。继续：从中断轮重新执行，保留原序列编号和已完成次数。" :
                "电压、设备、采集参数或次数已改变，不能继续原序列；可按当前设置重新开始。") +
                "\n重新开始：创建新序列，从第1轮执行，共 " + requestedCount + " 轮。" +
                "\n两种操作均从 AO=0 和正常启动检查开始；原始记录保留。" +
                (current.AutomaticBatch ? "\n选择后启动整批剩余轮次；正常轮次自动继续，停止/故障则撤销。整批期间人员保持撤离。" : "") +
                (current.Simulation ? "" : "\n\n选择继续或重新开始即确认：已按现场程序独立放电、核对接线及换算、人员已撤离。") };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(12,4,12,6), FlowDirection = FlowDirection.RightToLeft };
            CancelChoice = MakeButton("取消",FactorySequenceChoice.Cancel);
            RestartButton = MakeButton("重新开始 " + requestedCount + " 轮",FactorySequenceChoice.Restart);
            ContinueButton = MakeButton("继续剩余 " + progress.Remaining + " 轮",FactorySequenceChoice.Continue);
            ContinueButton.Enabled = compatible;
            actions.Controls.Add(CancelChoice); actions.Controls.Add(RestartButton); actions.Controls.Add(ContinueButton);
            Controls.Add(text); Controls.Add(actions); CancelButton = CancelChoice; AcceptButton = CancelChoice;
        }
        private Button MakeButton(string text, FactorySequenceChoice choice)
        {
            var button = new Button { Text = text, Width = 184, Height = 38 };
            button.Click += (s,e) => { Choice = choice; DialogResult = choice == FactorySequenceChoice.Cancel ? DialogResult.Cancel : DialogResult.OK; Close(); };
            return button;
        }
    }
}
