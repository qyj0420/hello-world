using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private readonly HashSet<string> unlistedDeviceNoticeQueued = new HashSet<string>(StringComparer.Ordinal);
        private string pendingFirstDeviceSetupKey = String.Empty;

        private void CompleteDeviceConnectionSetup()
        {
            if (IsDisposed || Disposing) return;
            HardwareStatusSnapshot current = state.Hardware;
            if (current != null && current.Ready && DeviceParameterProfiles.Key(current) == DeviceParameterProfiles.ConfiguredKey(settings)) {
                // A new explicit connection may offer setup again after an earlier cancellation.
                unlistedDeviceNoticeQueued.Remove(DeviceParameterProfiles.Key(current));
                QueueUnlistedDeviceNotice(current);
            }
            else BeginHardwareRefresh(false, true);
        }

        private void QueueUnlistedDeviceNotice(HardwareStatusSnapshot snapshot)
        {
            if ((verificationMode && DeviceParameterEditorForTests == null) || snapshot == null || !snapshot.Ready || DeviceParameterProfiles.IsConfigured(settings, snapshot)) return;
            string key = DeviceParameterProfiles.Key(snapshot);
            if (key.Length == 0 ||
                !unlistedDeviceNoticeQueued.Add(key)) return;
            pendingFirstDeviceSetupKey = key;
            try { BeginInvoke(new Action(async delegate
            {
                bool shown = false;
                try
                {
                if (IsDisposed || Disposing || activeModule != null || settingsDialogActive || detectionSelectionBusy || firstBindingInProgress ||
                    hardwareRefreshInProgress || hardwareDisconnectInProgress || hardwareConnectionQueued || pendingHardwareNavigation != null) return;
                HardwareStatusSnapshot current = state.Hardware;
                if (!current.Ready || !String.Equals(DeviceParameterProfiles.Key(current), key, StringComparison.Ordinal)) return;
                if (DeviceParameterProfiles.IsConfigured(settings, current)) { pendingFirstDeviceSetupKey = String.Empty; return; }
                shown = true;
                pendingFirstDeviceSetupKey = String.Empty;
                await EditDeviceParameters();
                }
                finally { if (!shown) unlistedDeviceNoticeQueued.Remove(key); }
            })); }
            catch (InvalidOperationException) { unlistedDeviceNoticeQueued.Remove(key); }
        }

        private void TryShowPendingDeviceSetup()
        {
            if (pendingFirstDeviceSetupKey.Length == 0 || !CanPollHomeDevice()) return;
            var current = state.Hardware;
            if (current == null || !current.Ready || DeviceParameterProfiles.Key(current) != pendingFirstDeviceSetupKey ||
                DeviceParameterProfiles.ConfiguredKey(settings) != pendingFirstDeviceSetupKey || DeviceParameterProfiles.IsConfigured(settings, current)) {
                pendingFirstDeviceSetupKey = String.Empty;
                return;
            }
            QueueUnlistedDeviceNotice(current);
        }
    }
}
