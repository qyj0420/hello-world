using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class ModuleHost
    {
        string calibrationIdentityKey;
        long calibrationIdentityEpoch;
        string[] ReadCurrentCalibrationHardwareIdentity()
        {
            string[] value;
            try { value=CalibrationHardwareIdentity.Read(settings as AppSettings,state.Hardware); }
            catch { if(calibrationIdentityKey!=null){calibrationIdentityKey=null;checked{calibrationIdentityEpoch++;}} throw; }
            if(calibrationIdentityKey!=value[0]) {calibrationIdentityKey=value[0];checked{calibrationIdentityEpoch++;}}
            value[0]+="|session="+calibrationIdentityEpoch.ToString(CultureInfo.InvariantCulture);
            return value;
        }
        void ApplyCalibrationHardwareIdentity(Control control)
        {
            try {ReadCurrentCalibrationHardwareIdentity();}catch { /* Invalidate cached identity, do not prompt on navigation. */ }
            // The provider is evaluated again for each round and after confirmation.
            // It consumes host evidence only; it never searches or opens a device.
            TrySet(control, "CalibrationHardwareIdentityProvider", new Func<string[]>(() =>
            {
                if (disposed || control.IsDisposed) throw new InvalidOperationException("校准页面已关闭。");
                return ReadCurrentCalibrationHardwareIdentity();
            }));
            TrySet(control,"CalibrationEntryDiagnosticSink",new Action<string>(message=>diagnostics.Error("CalibrationEntry",message)));
        }
    }

    internal static class CalibrationHardwareIdentity
    {
        internal static string[] Read(AppSettings app, HardwareStatusSnapshot actual)
        {
            string identity = DeviceSetupStatus.ActualIdentity(actual);
            var configured = app == null ? null : app.Hardware;
            if (configured == null || !configured.Enabled || actual == null || !actual.Ready ||
                !actual.HasLiveIdentityReadback ||
                String.IsNullOrWhiteSpace(actual.IntegrationExecutablePathUsed) || identity.Length == 0 ||
                actual.CheckedUtc == DateTime.MinValue || actual.CheckedUtc > DateTime.UtcNow.AddSeconds(5) ||
                Mac(configured.SelectedHubMac) != Mac(actual.HubMac) || Mac(actual.RequestedHubMac) != Mac(actual.HubMac) ||
                N(configured.ControllerLocation) != N(actual.RequestedControllerLocation) ||
                N(configured.AcquisitionLocation) != N(actual.RequestedAcquisitionLocation) ||
                N(configured.ControllerSerial) != N(actual.ControllerSerial) ||
                N(configured.AcquisitionSerial) != N(actual.AcquisitionSerial))
                throw new InvalidOperationException("当前设备身份未核验或已改变，请返回主页重新连接所选设备后再校准。");
            string[] pair = identity.Split('|');
            bool controllerIsArt = DeviceAssetCatalog.IsArtSerial(actual.ControllerSerial);
            string alias = (controllerIsArt ? actual.ControllerDeviceName : actual.AcquisitionDeviceName) ?? "";
            string instance = (controllerIsArt ? actual.ControllerInstanceId : actual.AcquisitionInstanceId) ?? "";
            if (!Regex.IsMatch(alias, "^[A-Za-z0-9_-]{1,63}$", RegexOptions.CultureInvariant) ||
                !String.Equals(instance, "USB\\VID_3063&PID_3135\\" + pair[2], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("尚未确认所选控制卡的设备名称，请返回主页重新连接；不会使用默认设备开始校准。");
            // Do not age out a still-established live session or refresh its timestamp.
            // Imported/status-file evidence lacks FromLiveIntegrationProcess and is rejected above.
            string key = identity + "|" + alias + "|" + actual.ControllerInstanceId + "|" + actual.AcquisitionInstanceId + "|" +
                N(configured.ControllerLocation) + "|" + N(configured.AcquisitionLocation) + "|" + actual.IntegrationExecutablePathUsed;
            return new[] { key, alias, pair[2], pair[1] };
        }
        static string N(string value) { return DeviceAssetCatalog.NormalizeIdentity(value); }
        static string Mac(string value) { return N(value).Replace(":", "").Replace("-", ""); }
    }
}
