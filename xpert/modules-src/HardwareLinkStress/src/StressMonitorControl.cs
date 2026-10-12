using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.HardwareLinkStress
{
    // The host owns device discovery, input-only adapters, the exclusive lease,
    // process isolation and evidence storage. This control never opens hardware.
    public interface IStressUiHost
    {
        bool SupportsAutomaticRecovery { get; }
        Task<DiagnosticCapacity> InspectCapacityAsync(CancellationToken cancellationToken);
        StressRunOptions CreateRunOptions();
        Task<StressSummary> RunAsync(TransferPlan plan, StressRunOptions options,
            IProgress<StressEvent> progress, CancellationToken cancellationToken);
    }

    public sealed class StressMonitorControl : UserControl
    {
        private readonly IStressUiHost host;
        private readonly ComboBox tier = new ComboBox();
        private readonly CheckBox automaticRecovery = new CheckBox();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Label status = new Label();
        private readonly Label totals = new Label();
        private readonly TextBox events = new TextBox();
        private CancellationTokenSource cancellation;
        private DateTime lastCycleUiUtc = DateTime.MinValue;
        private bool running;

        public StressMonitorControl(IStressUiHost host)
        {
            this.host = host ?? throw new ArgumentNullException("host");
            Dock = DockStyle.Fill;
            BackColor = Color.White;
            Padding = new Padding(16);
            Font = new Font("Microsoft YaHei UI", 9F);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var choice = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            choice.Controls.Add(new Label { Text = "传输数据量", AutoSize = true, Margin = new Padding(0, 8, 12, 0) });
            tier.DropDownStyle = ComboBoxStyle.DropDownList;
            tier.Width = 140;
            tier.Items.AddRange(new object[] { "低", "中", "高" });
            tier.SelectedIndex = 0;
            choice.Controls.Add(tier);
            choice.Controls.Add(new Label
            {
                Text = "按实际设备容量调整；显示的速度以实测为准",
                AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(14, 8, 0, 0)
            });
            layout.Controls.Add(choice, 0, 0);

            automaticRecovery.Text = "掉线后自动重连并从新一轮继续";
            automaticRecovery.Checked = false;
            automaticRecovery.AutoSize = true;
            automaticRecovery.Enabled = host.SupportsAutomaticRecovery;
            automaticRecovery.Margin = new Padding(0, 7, 0, 0);
            layout.Controls.Add(automaticRecovery, 0, 1);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            start.Text = "开始测试";
            start.Width = 126;
            start.Height = 32;
            stop.Text = "停止测试";
            stop.Width = 126;
            stop.Height = 32;
            stop.Enabled = false;
            actions.Controls.Add(start);
            actions.Controls.Add(stop);
            layout.Controls.Add(actions, 0, 2);

            status.Dock = DockStyle.Fill;
            status.Text = host.SupportsAutomaticRecovery
                ? "待开始。测试只读取输入，不启动输出。"
                : "待开始。当前连接方式不支持自动恢复。";
            layout.Controls.Add(status, 0, 3);
            totals.Dock = DockStyle.Fill;
            totals.Text = "累计传输：0 MB    实测速度：—";
            layout.Controls.Add(totals, 0, 4);

            events.Dock = DockStyle.Fill;
            events.Multiline = true;
            events.ReadOnly = true;
            events.ScrollBars = ScrollBars.Vertical;
            events.BackColor = Color.FromArgb(248, 250, 252);
            layout.Controls.Add(events, 0, 5);

            start.Click += async (sender, args) => await StartAsync();
            stop.Click += (sender, args) => { if (cancellation != null) cancellation.Cancel(); };
            Disposed += (sender, args) => { if (cancellation != null) cancellation.Cancel(); };
        }

        private async Task StartAsync()
        {
            if (running) return;
            running = true;
            cancellation = new CancellationTokenSource();
            start.Enabled = false;
            stop.Enabled = true;
            tier.Enabled = false;
            automaticRecovery.Enabled = false;
            status.Text = "正在核对所选设备和数据量…";
            try
            {
                TransferTier selected = (TransferTier)Math.Max(0, tier.SelectedIndex);
                DiagnosticCapacity capacity = await host.InspectCapacityAsync(cancellation.Token);
                TransferPlan plan = TransferTierPlanner.Create(selected, capacity);
                StressRunOptions options = host.CreateRunOptions();
                if (options == null) throw new InvalidOperationException("没有可用的测试设备配置。");
                options.AutoReconnectAndResume = automaticRecovery.Checked;
                options.Validate();
                status.Text = plan.LimitedByCapacity
                    ? "已按当前设备容量限制本档数据量；准备测试。"
                    : "已核对本档数据量；准备测试。";
                Append("本档每轮：控制模块 " + Megabytes(plan.ArtBytesPerCycle) +
                    " MB，采集模块 " + Megabytes(plan.PicoBytesPerCycle) + " MB，约 " +
                    plan.RequestedDurationMilliseconds.ToString("F1") + " ms。" +
                    (plan.LimitedByCapacity ? " 本档标称 " +
                        Megabytes(plan.NominalPicoBytesPerCycle) + " MB，已按容量限制。" : String.Empty));
                var progress = new Progress<StressEvent>(OnEvent);
                StressSummary result = await host.RunAsync(plan, options, progress, cancellation.Token);
                if (IsDisposed) return;
                totals.Text = "累计传输：" + Megabytes(result.ArtBytes + result.PicoBytes) +
                    " MB    实测全程速度：" + Megabytes(result.WholeRunBytesPerSecond) + " MB/s";
                status.Text = result.Outcome == StressOutcome.Completed ? "测试完成，未观察到链路中断。" :
                    result.Outcome == StressOutcome.CompletedWithRecoveredFaults
                        ? "测试完成，但发生过掉线；已从新轮次恢复，事故记录已保留。" :
                    result.Outcome == StressOutcome.Cancelled ? "测试已停止，结果已保留。" :
                    "测试未完成；请查看事故日志和设备状态。";
                if (!String.IsNullOrWhiteSpace(result.Failure)) Append(OperatorMessage(result.Failure));
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) status.Text = "测试已停止。";
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    status.Text = "无法继续测试，请检查设备状态和诊断记录。";
                    Append(OperatorMessage(error.Message));
                }
            }
            finally
            {
                running = false;
                if (!IsDisposed)
                {
                    start.Enabled = true;
                    stop.Enabled = false;
                    tier.Enabled = true;
                    automaticRecovery.Enabled = host.SupportsAutomaticRecovery;
                }
                cancellation.Dispose();
                cancellation = null;
            }
        }

        private void OnEvent(StressEvent item)
        {
            if (IsDisposed || item == null) return;
            if (item.Kind == StressEventKind.CycleCompleted &&
                (DateTime.UtcNow - lastCycleUiUtc).TotalSeconds < 1) return;
            lastCycleUiUtc = DateTime.UtcNow;
            totals.Text = "累计传输：" + Megabytes(item.ArtBytesTotal + item.PicoBytesTotal) +
                " MB    已完成轮次：" + item.Cycle;
            if (item.Kind != StressEventKind.CycleCompleted || item.Cycle % 10 == 0)
                Append(item.Utc.ToLocalTime().ToString("HH:mm:ss") + "  " + EventMessage(item));
        }

        private void Append(string line)
        {
            if (events.TextLength > 64000) events.Text = events.Text.Substring(events.TextLength - 32000);
            events.AppendText(line + Environment.NewLine);
        }

        private static string Megabytes(double bytes)
        { return (bytes / 1000000.0).ToString("F2"); }

        private static string EventMessage(StressEvent item)
        {
            switch (item.Kind)
            {
                case StressEventKind.Started: return "测试开始，已记录本次数据量设置。";
                case StressEventKind.Baseline: return "已记录设备初始状态。";
                case StressEventKind.CycleStarted: return "第 " + item.Cycle + " 轮读取中。";
                case StressEventKind.CycleCompleted: return "第 " + item.Cycle + " 轮读取完成。";
                case StressEventKind.LinkDropped: return "设备连接中断；已保存事故证据。";
                case StressEventKind.TransferFault: return "数据读取异常；已保存事故证据。";
                case StressEventKind.MonitorFault: return "设备状态检查异常；已保存诊断记录。";
                case StressEventKind.ReconnectAttempt: return "正在等待并尝试恢复选定设备。";
                case StressEventKind.ReconnectRejected: return "自动恢复未通过设备核对，已保存原因。";
                case StressEventKind.Reconnected: return "已观察到选定设备连接恢复。";
                case StressEventKind.ResumePlanned: return "准备从新一轮继续，上一轮失败数据不会补算。";
                case StressEventKind.ResumedCycleCompleted: return "恢复后的首轮读取已完成。";
                case StressEventKind.RecoveryObservationEnded: return "恢复观察结束，测试未自动重启。";
                case StressEventKind.StoppedByUser: return "测试已由操作员停止。";
                case StressEventKind.Completed: return "所选轮次已完成。";
                default: return "状态已更新，详细信息见诊断记录。";
            }
        }

        private static string OperatorMessage(string detail)
        {
            if (String.IsNullOrWhiteSpace(detail)) return "没有可显示的错误详情。";
            string text = detail.Replace("PicoScope", "采集模块").Replace("Pico", "采集模块")
                .Replace("ART-DAQ", "控制模块").Replace("ART", "控制模块")
                .Replace("DS600", "汇集器").Replace("SX Virtual Link Lite", "通信服务");
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 500 ? text : text.Substring(0, 500) + "…";
        }
    }
}
