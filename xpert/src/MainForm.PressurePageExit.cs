using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        bool pressureExitInProgress;
        Task<bool> pressureHomeExitTask;
        internal Func<int,Task> PressureExitDelayForTests;
        internal const int PressureExitSettleMilliseconds=50;

        // The synchronous navigation API is only a dispatcher. Callers which
        // perform further navigation/reconfiguration must await this method.
        internal Task<bool> ShowHomeAsync()
        {
            if(pressureExitInProgress)return pressureHomeExitTask??Task.FromResult(false);
            bool pressurePage=activeModule!=null&&String.Equals(activeModule.Tag as string,ModuleHost.PressureTest,StringComparison.OrdinalIgnoreCase);
            // Home is navigation, not an implicit Stop command. Validate before
            // BeginPressurePageExit can cancel work, hide the page or retire DI.
            if(pressurePage&&!CanLeaveActivePressure())return Task.FromResult(false);
            if(pressurePage||moduleHost!=null&&moduleHost.HasPressureDeviceOwner){
                pressureHomeExitTask=ExitPressurePageToHomeAsync(activeModule,moduleHost);
                return pressureHomeExitTask;
            }
            return Task.FromResult(ShowHomeCore());
        }

        async Task<bool> ExitPressurePageToHomeAsync(Control page,ModuleHost owner)
        {
            if(owner==null||IsDisposed||Disposing)return false;
            pressureExitInProgress=true;
            long navigation=++navigationRequestSequence;
            hardwareRefreshRequestEpoch++;
            pendingHardwareNavigation=null;hardwareConnectionQueued=false;
            if(devicePollTimer!=null)devicePollTimer.Stop();
            try {
                owner.BeginPressurePageExit(page);
                // Initialization may still be in calibration lookup, native
                // capture, or its input-owner handoff. Join it before Close.
                var wait=Stopwatch.StartNew();
                Task preparation=Task.WhenAll(pressureInputPreparationTask,owner.JoinPressurePagePreparationsAsync(page));
                Task preparationDeadline=Task.Delay(30000);
                if(await Task.WhenAny(preparation,preparationDeadline)!=preparation)
                    throw new TimeoutException("初始化任务尚未结束，原设备所有权仍保留。 / Initialization has not ended; the original device ownership is retained.");
                await preparation;
                while(page!=null&&!page.IsDisposed&&(
                    ReadReleaseFlag(page,"HasActiveHardwareSession")||ReadReleaseFlag(page,"IsPressureNoiseInitializing")||
                    ReadReleaseFlag(page,"ActionPending")||ReadReleaseFlag(page,"IsBusy"))) {
                    if(wait.ElapsedMilliseconds>=30000)throw new TimeoutException("停止或文件保存尚未回执，原设备会话仍保留。 / Stop or saving has not acknowledged completion; the original device owner is retained.");
                    await Task.Delay(10);
                }
                await owner.ClosePressurePageAsync(page);
                await owner.CloseRetainedPressureSessionsAsync();
                if(owner.HasPressureDeviceOwner)throw new InvalidOperationException("原设备任务尚未释放。 / The original device tasks have not been released.");
                if(page!=null&&!page.IsDisposed)page.Dispose();
                // Start the settling interval only AFTER actual Close success.
                // It is not a substitute for Stop/Clear/worker-close receipts.
                if(PressureExitDelayForTests!=null)await PressureExitDelayForTests(PressureExitSettleMilliseconds);
                else await Task.Delay(PressureExitSettleMilliseconds);
                if(IsDisposed||Disposing||navigation!=navigationRequestSequence||!Object.ReferenceEquals(owner,moduleHost)||
                    !Object.ReferenceEquals(page,activeModule))return false;
                return ShowHomeCore();
            } catch(Exception error) {
                while(error is TargetInvocationException && error.InnerException!=null)error=error.InnerException;
                new Diagnostics().Error("PressurePageExit","Pressure-page task release was not confirmed; home monitoring remains paused.",error);
                if(!IsDisposed&&!Disposing&&Object.ReferenceEquals(page,activeModule)){
                    if(page!=null&&!page.IsDisposed)page.Visible=true;
                    DeviceConnectionNotice(Localize(IsChinese?"停止尚未确认，请保持停止并检查设备。":
                        "Stop is not yet confirmed. Keep the test stopped and check the equipment."));
                }
                return false;
            } finally {
                pressureExitInProgress=false;
                if(!IsDisposed&&!Disposing&&activeModule==null&&Object.ReferenceEquals(owner,moduleHost)&&!owner.HasPressureDeviceOwner&&
                    IsHardwareEnabled&&!hardwareManuallyDisconnected&&!settingsDialogActive&&!settingsWorkflowActive&&devicePollTimer!=null)
                    devicePollTimer.Start();
            }
        }
    }
}
