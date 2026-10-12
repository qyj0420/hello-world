using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private bool recognitionSuspended;
        private bool batchRevocationFailed;
        private CancellationTokenSource recognitionCancellation;
        private CancellationTokenSource analysisCancellation;
        private Action rejectNextRound;
        private void RequestUserStop()
        {
            recognitionSuspended=true;
            // Keep waveforms already displayed, but invalidate work still loading
            // and ignore further preview notifications from this stopped worker.
            capturePreviewSuspended=true;
            ++capturePreviewGeneration;
            ++timelineGeneration;
            if(recognitionCancellation!=null)recognitionCancellation.Cancel();
            if(analysisCancellation!=null)analysisCancellation.Cancel();
            if(cancellation!=null)cancellation.Cancel();
            string revokeFailure=null;
            try { FactoryResidentDevices.RevokeBatch(); }
            catch(Exception ex) { batchRevocationFailed=true;revokeFailure=ex.Message; }
            if(rejectNextRound!=null)rejectNextRound();
            feedbackTrend.SetLive(false);pressureTrend.SetLive(false);
            status.Text="已请求停止本轮、连续队列及自动分析；等待关断与资源释放后恢复就绪。急停/连接只读状态保留。";
            if(revokeFailure!=null)status.Text+=" 批次撤销记录失败："+revokeFailure+"；不要重新开始，请关闭程序并检查日志。";
            RefreshStartAvailability();
        }

        // Legacy non-batch per-round confirmation is a separate modeless owned
        // window, independent of optional-message preferences. Stop on the main
        // form remains usable; close/cancellation never acknowledges this round.
        private async Task<bool> ConfirmNextRound(int index,int total,FactorySettings next,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(rejectNextRound!=null)throw new InvalidOperationException("上一轮确认尚未结束。");
            var done=new TaskCompletionSource<bool>();
            Action reject=()=>done.TrySetResult(false);
            EventHandler accept=(s,e)=>done.TrySetResult(!token.IsCancellationRequested && !closeAfterStop);
            EventHandler stopQueue=(s,e)=>{RequestUserStop();reject();};
            FormClosingEventHandler ownerClosing=(s,e)=>reject();
            EventHandler ownerDisposed=(s,e)=>reject();
            FormClosedEventHandler popupClosed=(s,e)=>reject();
            FactoryNextRoundDialog dialog=null;
            Button continueButton=null,stopButton=null;
            rejectNextRound=reject;FormClosing+=ownerClosing;Disposed+=ownerDisposed;
            string summary="下一轮待确认："+index+" / "+total+"，目标 "+next.TargetKv.ToString("0.000")+
                " kV，量程 ±"+BenchSettings.Ranges[next.Input.RangeIndex]+" V。";
            string instruction="请独立确认已放电、核对接线且人员撤离，再点击“确认并继续”。";
            try
            {
                dialog=new FactoryNextRoundDialog(Icon,Font,"已保存上一轮。"+summary+"\n\n"+instruction+
                    "\n关闭此窗或点击停止均结束队列。此项确认不受普通提示开关影响。");
                continueButton=dialog.Continue;stopButton=dialog.Stop;
                dialog.FormClosed+=popupClosed;
                continueButton.Click+=accept;stopButton.Click+=stopQueue;
                using(token.Register(reject))
                {
                    if(!done.Task.IsCompleted)
                    {
                        dialog.Show(this);
                        stopButton.Focus();
                    }
                    bool confirmed=await done.Task;
                    return confirmed && !token.IsCancellationRequested && !closeAfterStop;
                }
            }
            finally
            {
                rejectNextRound=null;FormClosing-=ownerClosing;Disposed-=ownerDisposed;
                if(continueButton!=null)continueButton.Click-=accept;
                if(stopButton!=null)stopButton.Click-=stopQueue;
                if(dialog!=null)
                {
                    dialog.FormClosed-=popupClosed;
                    // Dispose also closes this modeless window; do not issue a
                    // second Close while handling its own FormClosed event.
                    dialog.Dispose();
                }
            }
        }
    }

    internal sealed class FactoryNextRoundDialog : Form
    {
        internal readonly Button Continue=new Button {Text="确认并继续",Width=124,Height=34};
        internal readonly Button Stop=new Button {Text="停止队列",Width=108,Height=34};
        internal FactoryNextRoundDialog(Icon icon,Font font,string message)
        {
            Text="下一轮待确认";Icon=icon;Font=font;StartPosition=FormStartPosition.CenterParent;
            ClientSize=new Size(620,212);FormBorderStyle=FormBorderStyle.FixedDialog;
            MinimizeBox=MaximizeBox=ShowInTaskbar=false;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=2};
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text=message},0,0);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
            actions.Controls.Add(Stop);actions.Controls.Add(Continue);layout.Controls.Add(actions,0,1);Controls.Add(layout);
            CancelButton=Stop; // Enter must not silently approve the next round.
        }
    }
}
