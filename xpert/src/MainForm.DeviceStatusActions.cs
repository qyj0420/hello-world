using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private Button disconnectDeviceButton;

        private void AddDeviceStatusActions(Panel card)
        {
            disconnectDeviceButton = AppTheme.Button(String.Empty, false);
            disconnectDeviceButton.Name = "DisconnectCurrentDevice";
            disconnectDeviceButton.Font = AppTheme.Font(8.5f, FontStyle.Regular);
            disconnectDeviceButton.Click += async delegate
            {
                // Re-evaluate the current snapshot on click; the displayed text
                // is not an authority to reconnect or to disconnect a device.
                if (hardwareDisconnectInProgress || activeModule != null || settingsDialogActive || detectionSelectionBusy || firstBindingInProgress) return;
                if (!IsHardwareEnabled) { RefreshDetectionDevices(); return; }
                if (HomeDeviceActionIsDisconnect(state.Hardware)) await TryDisconnectCurrentDevice();
                else if (!hardwareRefreshInProgress) ReconnectCurrentDevice();
            };
            card.Controls.Add(disconnectDeviceButton);
            if (deviceDot != null)
            {
                deviceDot.Name = "DeviceConnectionLamp";
                deviceDot.AccessibleRole = AccessibleRole.Indicator;
                deviceDot.Paint += PaintDeviceConnectionLamp;
            }
        }

        private void ArrangeStatusCardAction(Panel card, Label primary, Label detail, Button action)
        {
            if (card == null || primary == null || detail == null || action == null) return;
            int inset = compactDisplayChrome ? 4 : ScaleLogical(4);
            int gap = compactDisplayChrome ? 8 : ScaleLogical(8);
            int width = Math.Max(compactDisplayChrome ? 86 : ScaleLogical(86), ButtonTextWidth(action));
            // Fill the card vertically, while retaining a visible breathing edge.
            action.SetBounds(Math.Max(0, card.ClientSize.Width - width - inset), inset,
                width, Math.Max(1, card.ClientSize.Height - inset * 2));
            primary.Width = detail.Width = Math.Max(1, action.Left - primary.Left - gap);
        }

        private int MinimumHomeStatusCardWidth(Label primary, Button action, bool withLamp)
        {
            if (primary == null || action == null) return 0;
            Func<int, int> unit = pixels => compactDisplayChrome ? pixels : ScaleLogical(pixels);
            int textLeft = withLamp ? unit(8) + 28 + unit(8) : unit(31);
            int titleWidth = TextRenderer.MeasureText(primary.Text, primary.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
            return textLeft + titleWidth + unit(8) + Math.Max(unit(86), ButtonTextWidth(action)) + unit(4);
        }

        private void ArrangeDeviceStatusActions(Panel card, Label primary, Label detail)
        {
            if (disconnectDeviceButton == null || card == null) return;
            disconnectDeviceButton.Visible = activeModule == null;
            // Visible includes ancestor visibility. Arrange before the first Show.
            if (activeModule != null) return;
            int diameter = Math.Max(18, Math.Min(28, card.ClientSize.Height / 3));
            int lampLeft = compactDisplayChrome ? 8 : ScaleLogical(8);
            if (deviceDot != null)
            {
                deviceDot.SetBounds(lampLeft, (card.ClientSize.Height - diameter) / 2, diameter, diameter);
                int textLeft = lampLeft + diameter + (compactDisplayChrome ? 8 : ScaleLogical(8));
                primary.Left = detail.Left = textLeft;
            }
            ArrangeStatusCardAction(card, primary, detail, disconnectDeviceButton);
        }

        private void UpdateDeviceStatusActions()
        {
            if (disconnectDeviceButton == null || deviceDot == null) return;
            HardwareStatusSnapshot hardware = state.Hardware;
            bool checking = hardwareRefreshInProgress && !hardwareRefreshSilent;
            bool disconnectFailed = String.Equals(hardware.StatusCode, "DisconnectFailed", StringComparison.OrdinalIgnoreCase);
            bool physicallyConnected = IsHardwareEnabled && !hardwareManuallyDisconnected && hardware.Connected;
            bool disconnect = HomeDeviceActionIsDisconnect(hardware);
            string actionText = disconnect ? (IsChinese ? Localize("断开设备") : "Disconnect device")
                : (IsChinese ? Localize("连接设备") : "Connect device");
            actionText = Localize(actionText);
            bool actionTextChanged = disconnectDeviceButton.Text != actionText;
            disconnectDeviceButton.Text = actionText;
            disconnectDeviceButton.AccessibleDescription = !IsHardwareEnabled
                ? (IsChinese ? Localize("查找并选择要连接的设备；不会启动采集或加压。") : "Find and select a device to connect. No acquisition or high-voltage output is started.")
                : disconnectFailed
                    ? (IsChinese ? Localize("上次断开尚未确认；点击重试断开，不会自动重新连接。") : "The previous disconnection was not confirmed. Retry disconnection without reconnecting.")
                    : disconnect
                        ? (IsChinese ? Localize("安全断开当前设备连接；断开成功后可点击“连接设备”重新连接。") : "Safely disconnect the current device. After disconnection, use Connect device to reconnect.")
                        : (IsChinese ? Localize("连接已配置的设备并检查连接状态；此操作不会启动加压。") : "Connect the configured device and check its status. This does not start a high-voltage test.");
            disconnectDeviceButton.Visible = activeModule == null;
            disconnectDeviceButton.Enabled = activeModule == null && !detectionSelectionBusy && !firstBindingInProgress &&
                !settings.SessionHardwareForcedOffline && !hardwareDisconnectInProgress && !settingsDialogActive &&
                !pressureExitInProgress && !(moduleHost!=null&&moduleHost.HasPressureDeviceOwner) &&
                (IsHardwareEnabled ? (disconnect || !hardwareRefreshInProgress) : CanRefreshDetectionDevices());
            string text;
            Color color;
            if (!IsHardwareEnabled)
            { color = AppTheme.Muted; text = IsChinese ? Localize("硬件模式未启用") : "Hardware mode disabled"; }
            else if (hardwareDisconnectInProgress)
            { color = AppTheme.Warning; text = IsChinese ? Localize("正在断开设备") : "Disconnecting device"; }
            else if (checking)
            { color = AppTheme.Warning; text = IsChinese ? Localize("正在检查连接") : "Checking connection"; }
            else if (disconnectFailed)
            {
                color = AppTheme.Warning; text = IsChinese ? Localize("断开未确认，请重试") : "Disconnect not confirmed; retry";
                deviceDetail.Text = text;
            }
            else if (hardware.StatusCode=="StopReleaseUnconfirmed"||hardware.StatusCode=="ResourceCleanupFailed"||
                hardware.StatusCode=="SessionEndedAfterFailedRelease"||hardware.StatusCode=="WorkerSessionExited")
            { color=AppTheme.Warning;text=DeviceConnectionStatusText(hardware);deviceDetail.Text=text; }
            else if (hardwareManuallyDisconnected)
            {
                color = Color.FromArgb(207, 58, 66); text = IsChinese ? Localize("设备已手动断开") : "Device manually disconnected";
                deviceDetail.Text = text;
            }
            else if (String.Equals(hardware.StatusCode,"PresenceUnknown",StringComparison.Ordinal))
            { color=AppTheme.Warning;text=IsChinese?Localize("设备连接状态无法确认"):"Device presence unconfirmed"; }
            else if (!physicallyConnected)
            { color = Color.FromArgb(207, 58, 66); text = IsChinese ? Localize("设备未连接") : "Device disconnected"; }
            else if (!hardware.Ready)
            { color = AppTheme.Warning; text = IsChinese ? Localize("设备已连接，状态待核对") : "Connected; readiness needs review"; }
            else
            { color = AppTheme.Accent; text = IsChinese ? Localize("指定设备已识别 · 占用状态未确认") : "Designated devices detected · Availability unconfirmed"; }
            deviceDot.BackColor = color;
            deviceDot.AccessibleName = text;
            deviceDot.AccessibleDescription = text + Environment.NewLine + deviceDetail.Text;
            deviceDot.Invalidate();
            if (deviceSetupTip != null)
            {
                deviceSetupTip.SetToolTip(deviceDot, deviceDot.AccessibleDescription);
                deviceSetupTip.SetToolTip(disconnectDeviceButton, disconnectDeviceButton.AccessibleDescription);
            }
            ArrangeDeviceStatusActions(deviceStatusCard, deviceStatus, deviceDetail);
            if (actionTextChanged) RequestResponsiveChrome();
        }

        private bool HomeDeviceActionIsDisconnect(HardwareStatusSnapshot hardware)
        {
            // Readiness/factory registration is deliberately not used here:
            // a connected device can still need configuration. A failed release
            // is uncertain, so it must remain a retry-disconnect action.
            return hardwareDisconnectInProgress ||
                String.Equals(hardware.StatusCode, "DisconnectFailed", StringComparison.OrdinalIgnoreCase) ||
                (!hardwareManuallyDisconnected && hardware.Connected);
        }

        private void PaintDeviceConnectionLamp(object sender, PaintEventArgs e)
        {
            var lamp = sender as Panel;
            if (lamp == null || lamp.Width < 3 || lamp.Height < 3) return;
            GraphicsState saved = e.Graphics.Save();
            try
            {
                e.Graphics.Clear(lamp.Parent == null ? Color.White : lamp.Parent.BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var bounds = new RectangleF(1, 1, lamp.Width - 3, lamp.Height - 3);
                using (var fill = new SolidBrush(lamp.BackColor)) e.Graphics.FillEllipse(fill, bounds);
            }
            finally { e.Graphics.Restore(saved); }
        }
    }
}
