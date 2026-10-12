using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        Task pressureInputPreparationTask=Task.CompletedTask;
        internal Func<Control,Task> PressureInputPreparationForTests;
        // Navigation never interrupts/disposes a worker still owning the devices.
        private bool CanLeaveActivePressure()
        {
            Control page = activeModule;
            if (page == null || !String.Equals(page.Tag as string, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase)) return true;
            if(page.IsDisposed)return moduleHost==null||!moduleHost.HasPressureDeviceOwner;
            try
            {
                if(state.Hardware.StatusCode=="StopReleaseUnconfirmed"&&moduleHost!=null&&moduleHost.HasPressureDeviceOwner){
                    DeviceConnectionNotice(Localize(IsChinese?"停止尚未确认，请保持停止并检查设备。":"Stop is not yet confirmed. Keep the test stopped and check the equipment."));
                    return false;
                }
                if (!ReadReleaseFlag(page, "PressureNavigationLocked") && !ReadReleaseFlag(page, "IsBusy") &&
                    !ReadReleaseFlag(page, "HasActiveHardwareSession") && !ReadReleaseFlag(page, "ActionPending")) return true;
                // A navigation attempt must not cancel the current acquisition or
                // save. The pressure button owns the finish-current-round request.
                DeviceConnectionNotice(Localize(IsChinese ? "操作尚未结束。请先点击停止，待采集和保存完成后再返回主页。"
                    : "The operation has not finished. Select Stop, then wait for acquisition and saving to complete before returning home."));
                return false;
            }
            catch { return false; }
        }

        private void ConfigurePressureInputPage(Control page)
        { pressureInputPreparationTask=PreparePressureInputPageAsync(page); }

        private async Task PreparePressureInputPageAsync(Control page)
        {
            try
            {
                if(PressureInputPreparationForTests!=null)await PressureInputPreparationForTests(page);
                else await moduleHost.ConfigureTask39PressureAsync(page);
                if (!pressureExitInProgress && page == activeModule && !page.IsDisposed && !state.DeviceConnected)
                    HandleLiveConnectionLoss(page);
            }
            catch (Exception error)
            {
                new Diagnostics().Error("PressureInput", "Input preparation failed.", error);
                if (!pressureExitInProgress && page == activeModule && !page.IsDisposed)
                {
                    // The state transition and the awaited initialization error
                    // describe one failed request. Present their evidence once,
                    // not a second connection-loss modal behind this dialog.
                    if (!state.DeviceConnected) notifiedDisconnectedModule = page;
                    DeviceConnectionNotice(InputOperationFeedback.Describe(error, settings.LanguageCode));
                }
            }
        }
    }
}
