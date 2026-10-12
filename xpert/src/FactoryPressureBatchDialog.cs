using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal static class FactoryPressureBatchDialog
    {
        // Initial batch authorization is a separate owned window. Normal
        // continuation has no window and never synthetically clicks this one.
        internal static Task<bool> ShowAsync(Control owner, string details, Func<bool> current, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (owner == null || owner.IsDisposed || !owner.IsHandleCreated || token.IsCancellationRequested)
            { completion.SetResult(false); return completion.Task; }
            Action show = () =>
            {
                if (owner.IsDisposed || token.IsCancellationRequested) { completion.TrySetResult(false); return; }
                var dialog = new Form { Name="PressureBatchStartDialog", Text="启动本电压批次",
                    StartPosition=FormStartPosition.CenterParent, ClientSize=new Size(650,255),
                    MinimumSize=new Size(560,280), ShowInTaskbar=false, MinimizeBox=false, MaximizeBox=false,
                    Font=owner.Font, AutoScaleMode=AutoScaleMode.Dpi };
                HVX.Presentation.OemUiBranding.Apply(dialog);
                var layout = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2, Padding=new Padding(16) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
                layout.Controls.Add(new Label { Text=details, Dock=DockStyle.Fill, AutoSize=false },0,0);
                var buttons = new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft };
                var cancel = new HVX.Presentation.R2RoundedButton { Name="CancelPressureBatch", Text="取消", Width=120, Height=36 };
                var accept = new HVX.Presentation.R2RoundedButton { Name="ConfirmPressureBatch", Text="确认并启动本批次", Width=185, Height=36 };
                buttons.Controls.Add(cancel); buttons.Controls.Add(accept); layout.Controls.Add(buttons,0,1);
                dialog.Controls.Add(layout); dialog.CancelButton=cancel;
                var timer = new System.Windows.Forms.Timer { Interval=100 };
                Action<bool> finish = accepted => { completion.TrySetResult(accepted); if (!dialog.IsDisposed) dialog.Close(); };
                Func<bool> allowed = () => { try { return !token.IsCancellationRequested && !owner.IsDisposed && owner.Visible && current(); } catch { return false; } };
                cancel.Click += (s,e) => finish(false);
                accept.Click += (s,e) => finish(allowed());
                timer.Tick += (s,e) => { if (!allowed()) finish(false); };
                var registration = token.Register(() => { try { if (dialog.IsHandleCreated) dialog.BeginInvoke((Action)(()=>finish(false))); else completion.TrySetResult(false); } catch (InvalidOperationException) { completion.TrySetResult(false); } });
                dialog.FormClosed += (s,e) => { timer.Stop();timer.Dispose();registration.Dispose();completion.TrySetResult(false);dialog.Dispose(); };
                if (!allowed()) { registration.Dispose();timer.Dispose();dialog.Dispose();completion.TrySetResult(false);return; }
                dialog.Show(owner.FindForm());timer.Start();
            };
            if (owner.InvokeRequired) { try { owner.BeginInvoke(show); } catch (InvalidOperationException) { completion.TrySetResult(false); } }
            else show();
            return completion.Task;
        }
    }
}
