using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class SettingsForm
    {
        private Button searchConnectDeviceButton;
        private Label deviceSearchFeedback;
        private bool deviceSearchAvailable = true;
        internal AppSettings DeviceSearchDraft { get; private set; }
        internal bool DeviceSearchAvailable
        {
            set { deviceSearchAvailable = value; if (searchConnectDeviceButton != null) searchConnectDeviceButton.Enabled = value; }
        }
        private void AddDeviceSearchRow(TableLayoutPanel grid)
        {
            searchConnectDeviceButton = AppTheme.Button("自动搜索并连接", true);
            searchConnectDeviceButton.Name = "SearchAndConnectAvailableDevice";
            searchConnectDeviceButton.Enabled = deviceSearchAvailable;
            searchConnectDeviceButton.AutoSize = true;
            searchConnectDeviceButton.MinimumSize = new Size(180, 38);
            searchConnectDeviceButton.Click += delegate { RequestDeviceSearch(); };
            AddWideRow(grid, searchConnectDeviceButton);
            searchConnectDeviceButton.Anchor = AnchorStyles.Left;
            deviceSearchFeedback = CreateNote("仅有一组可用设备时自动连接；多组时请选择。连接后自动更新设备信息，不启动采集或加压。");
            deviceSearchFeedback.Name = "DeviceSearchFeedback";
            AddWideRow(grid, deviceSearchFeedback);
        }
        private void RequestDeviceSearch()
        {
            if (DeviceSearchDraft != null || searchConnectDeviceButton == null || !searchConnectDeviceButton.Enabled) return;
            try
            {
                // Keep the draft in memory. Empty device address fields are precisely
                // why discovery is requested, so do not validate/save them first.
                DeviceSearchDraft = CaptureValues();
                searchConnectDeviceButton.Enabled = false;
                DialogResult = DialogResult.Retry;
                Close();
            }
            catch (Exception error)
            {
                DeviceSearchDraft = null;
                MessageBox.Show(this, SettingsText("无法保留当前设置：", "Unable to retain current settings: ") + SettingsCaption(error.Message),
                    SettingsText("设置校验", "Check settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        internal void ShowDeviceSearchResult(bool connected)
        {
            if (deviceSearchFeedback == null) return;
            settingsCaptions[deviceSearchFeedback] = connected
                ? "设备已连接，连接信息已更新。其他修改尚未保存，请核对后保存设置。"
                : "未完成连接，原有修改已保留。可重试搜索或检查设备状态。";
            ApplySettingsLanguage();
        }
    }

    internal static class DeviceSearchSettingsDraft
    {
        // Discovery persists only the selected, verified device through the existing
        // host transaction. Unrelated edits stay unsaved until the operator saves.
        internal static AppSettings Rebase(AppSettings basis, AppSettings draft, AppSettings latest, bool connectionUpdated)
        {
            if (basis == null || draft == null || latest == null) throw new ArgumentNullException("settings");
            var result = latest.Clone();
            CopyChangedScalars(basis, draft, result);
            CopyChangedScalars(basis.TaskMinus3, draft.TaskMinus3, result.TaskMinus3);
            if (!connectionUpdated) result.Hardware = draft.Hardware.Clone();
            else CopyChangedScalars(basis.Hardware, draft.Hardware, result.Hardware, property =>
                property.Name == nameof(HardwareSettings.DriverPackagePath) ||
                property.Name == nameof(HardwareSettings.FirstConfigurationToolPath) ||
                property.Name == nameof(HardwareSettings.DriverInstallerPath) ||
                property.Name == nameof(HardwareSettings.Task39InputSettingsPath));
            return result;
        }
        private static void CopyChangedScalars(object basis, object draft, object result, Predicate<PropertyInfo> include = null)
        {
            if (basis == null || draft == null || result == null) return;
            foreach (PropertyInfo property in basis.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 ||
                    property.IsDefined(typeof(ConfigIgnoreAttribute), true) || property.Name.StartsWith("DeviceParametersReviewed", StringComparison.Ordinal)) continue;
                if (include != null && !include(property)) continue;
                Type type = property.PropertyType;
                if (type != typeof(string) && !type.IsValueType) continue;
                object edited = property.GetValue(draft, null);
                if (!Object.Equals(property.GetValue(basis, null), edited)) property.SetValue(result, edited, null);
            }
        }
    }
}
