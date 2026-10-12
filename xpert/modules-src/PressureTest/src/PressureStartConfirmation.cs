using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        TableLayoutPanel pressureStartConfirmationPanel;
        Button pressureStartConfirmButton;
        TaskCompletionSource<bool> pressureStartConfirmation;
        System.Windows.Forms.Timer pressureStartConfirmationTimer;
        CancellationTokenRegistration pressureStartCancellation;
        Func<bool> pressureStartContextCurrent;
        double pressureStartTarget;

        // A one-shot operator action, never a persisted preference or a synthetic
        // checkbox value. The supervised worker independently checks interlocks.
        public Task<bool> RequestPressureStartConfirmationAsync(double targetKv, string details, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool installed = false;
            RunOnUiThread(delegate {
                if (token.IsCancellationRequested || !Visible || !IsHandleCreated || pressureStartConfirmation != null ||
                    !IsFinite(targetKv) || targetKv <= 0 || !ActionPending || ActionCode != "PRESSURE") return;
                long sequence = ActionSequence;
                string project = ProjectPath, phase = SelectedPhaseCode;
                pressureStartContextCurrent = () => !token.IsCancellationRequested && !completion.Task.IsCompleted &&
                    !IsDisposed && !Disposing && Visible && IsHandleCreated && ActionPending &&
                    ActionSequence == sequence && ActionCode == "PRESSURE" && ProjectPath == project &&
                    SelectedPhaseCode == phase && CalculatedTargetPeakVoltageKv == targetKv;
                pressureStartConfirmation = completion;
                pressureStartTarget = targetKv;
                pressureStartCancellation = token.Register(() => completion.TrySetResult(false));
                var panel = new TableLayoutPanel { Name = "PressureStartConfirmationPanel", Dock = DockStyle.Fill,
                    ColumnCount = 2, RowCount = 1, Padding = new Padding(8), Margin = Padding.Empty,
                    BackColor = Color.FromArgb(255, 247, 225) };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Math.Max(190, Font.Height * 12)));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                panel.Controls.Add(new TextBox { Name = "PressureStartConfirmationDetails", Text = details ?? "",
                    Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    BorderStyle = BorderStyle.None, BackColor = panel.BackColor, ForeColor = ForeColor, TabStop = false }, 0, 0);
                var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                pressureStartConfirmButton = new HVX.Presentation.R2RoundedButton { Name = "ConfirmPressureRoundButton", Text = "确认本轮并开始",
                    Dock = DockStyle.Fill, BackColor = PressureTestTheme.Accent, ForeColor = Color.White };
                pressureStartConfirmButton.Click += delegate {
                    if (pressureStartContextCurrent == null || !pressureStartContextCurrent()) { CancelPressureStartConfirmation(); return; }
                    lock (stateLock) if (pressureStartTarget > maximumPeakVoltageKv || !CanRequestVoltageNoLock(pressureStartTarget)) return;
                    FinishPressureStartConfirmation(true);
                };
                var cancel = new HVX.Presentation.R2RoundedButton { Name = "CancelPressureRoundButton", Text = "取消本轮", Dock = DockStyle.Fill };
                cancel.Click += delegate {
                    if(pressureStartContextCurrent!=null&&pressureStartContextCurrent())ReportPressurePreparationCanceled(sequence);
                    CancelPressureStartConfirmation();
                };
                buttons.Controls.Add(pressureStartConfirmButton, 0, 0); buttons.Controls.Add(cancel, 0, 1);
                panel.Controls.Add(buttons, 1, 0);
                pressureStartConfirmationPanel = panel;
                rootLayout.Controls.Add(panel, 0, 2);
                pressureStartConfirmationTimer = new System.Windows.Forms.Timer { Interval = 100 };
                pressureStartConfirmationTimer.Tick += delegate { RefreshPressureStartConfirmation(); };
                pressureStartConfirmationTimer.Start();
                installed = true;
                UpdateEmbeddedLayout();
                RefreshPressureStartConfirmation();
            });
            if (!installed) completion.TrySetResult(false);
            return completion.Task;
        }

        void RefreshPressureStartConfirmation()
        {
            if (pressureStartContextCurrent == null || !pressureStartContextCurrent()) { CancelPressureStartConfirmation(); return; }
            bool permitted;
            lock (stateLock) permitted = pressureStartTarget <= maximumPeakVoltageKv && CanRequestVoltageNoLock(pressureStartTarget);
            pressureStartConfirmButton.Enabled = permitted;
            pressureStartConfirmButton.Text = permitted ? "确认本轮并开始" : "等待急停解除及有效回读";
        }

        void CancelPressureStartConfirmation() { FinishPressureStartConfirmation(false); }

        void FinishPressureStartConfirmation(bool accepted)
        {
            var completion = pressureStartConfirmation;
            pressureStartConfirmation = null; pressureStartContextCurrent = null;
            if (pressureStartConfirmationTimer != null) { pressureStartConfirmationTimer.Stop(); pressureStartConfirmationTimer.Dispose(); pressureStartConfirmationTimer = null; }
            pressureStartCancellation.Dispose();
            if (pressureStartConfirmationPanel != null) {
                rootLayout.Controls.Remove(pressureStartConfirmationPanel);
                pressureStartConfirmationPanel.Dispose(); pressureStartConfirmationPanel = null;
                pressureStartConfirmButton = null;
            }
            if (rootLayout != null && !rootLayout.IsDisposed) rootLayout.RowStyles[2].Height = 0;
            if (completion != null) completion.TrySetResult(accepted);
        }
    }
}
