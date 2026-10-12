using System;
using System.IO;

namespace Xiongxin.OWTS.Desktop
{
    internal enum ReportOutputFormat
    {
        PdfOnly = 0,
        WordOnly = 1,
        PdfAndWord = 2
    }

    internal enum HvupVoltageSource
    {
        Divider = 0,
        VMonitor = 1
    }

    internal sealed class HardwareStatusSnapshot
    {
        public bool HardwareEnabled { get; set; }
        public bool DriverInstalled { get; set; }
        public bool Connected { get; set; }
        public bool IdentityValid { get; set; }
        public string StatusCode { get; set; }
        public string Message { get; set; }
        public string Diagnostic { get; set; }
        public string HubMac { get; set; }
        // Address hint only: disconnect must re-read and match the hub MAC and ports.
        public string HubIp { get; set; }
        public string ControllerSerial { get; set; }
        public string AcquisitionSerial { get; set; }
        public string ControllerTransportSerial { get; set; }
        public string ControllerInstanceId { get; set; }
        public string AcquisitionTransportSerial { get; set; }
        public string AcquisitionInstanceId { get; set; }
        // Exact SDK aliases reported with the selected module identity, never inferred from Dev1.
        public string ControllerDeviceName { get; set; }
        public string AcquisitionDeviceName { get; set; }
        // Populated only after this adapter launched the exact connector and
        // accepted its unique live output. Cached/status-file paths stay false.
        public bool FromLiveIntegrationProcess { get; set; }
        // A connect command is trusted only after its complete identity/readiness
        // readback, not merely because a connection command exited successfully.
        public bool ConnectionReadbackVerified { get; set; }
        public bool HasLiveIdentityReadback
        {
            get { return FromLiveIntegrationProcess && (IntegrationRequestMode == "--inspect" ||
                (IntegrationRequestMode == "--connect" && ConnectionReadbackVerified)); }
        }
        public string IntegrationExecutablePathUsed { get; set; }
        public string IntegrationRequestMode { get; set; }
        public string RequestedHubMac { get; set; }
        public string RequestedControllerLocation { get; set; }
        public string RequestedAcquisitionLocation { get; set; }
        public string AssetMatchStatus { get; set; }
        public string AssetMatchMessage { get; set; }
        public string AssetCatalogVersion { get; set; }
        public string MatchedFactoryNumber { get; set; }
        public string MatchedDeviceModel { get; set; }
        public double? MatchedCouplingCapacitanceNanoFarad { get; set; }
        public bool? MatchedCompensationSelected { get; set; }
        public double? MatchedCompensationCapacitanceNanoFarad { get; set; }
        public double? MatchedCompensationVoltageKilovolt { get; set; }
        public double? MatchedCalibrationCoefficient { get; set; }
        public bool AssetParametersApplied { get; set; }
        public DateTime CheckedUtc { get; set; }

        public bool Ready
        {
            get
            {
                return HardwareEnabled && DriverInstalled && Connected && IdentityValid &&
                    String.Equals(StatusCode, "Ready", StringComparison.OrdinalIgnoreCase);
            }
        }

        public HardwareStatusSnapshot()
        {
            StatusCode = "Offline";
            Message = "硬件模式未启用";
            Diagnostic = String.Empty;
            HubMac = String.Empty;
            HubIp = String.Empty;
            ControllerSerial = String.Empty;
            AcquisitionSerial = String.Empty;
            ControllerTransportSerial = String.Empty;
            ControllerInstanceId = String.Empty;
            AcquisitionTransportSerial = String.Empty;
            AcquisitionInstanceId = String.Empty;
            ControllerDeviceName = String.Empty;
            AcquisitionDeviceName = String.Empty;
            IntegrationExecutablePathUsed = String.Empty;
            IntegrationRequestMode = String.Empty;
            RequestedHubMac = String.Empty;
            RequestedControllerLocation = String.Empty;
            RequestedAcquisitionLocation = String.Empty;
            AssetMatchStatus = "NotEvaluated";
            AssetMatchMessage = String.Empty;
            AssetCatalogVersion = String.Empty;
            MatchedFactoryNumber = String.Empty;
            MatchedDeviceModel = String.Empty;
            CheckedUtc = DateTime.MinValue;
        }

        public HardwareStatusSnapshot Clone()
        {
            return (HardwareStatusSnapshot)MemberwiseClone();
        }
    }

    internal sealed class ApplicationState
    {
        private string projectPath = String.Empty;
        private HardwareStatusSnapshot hardware = new HardwareStatusSnapshot();

        public event EventHandler Changed;

        public string ProjectPath { get { return projectPath; } }
        public bool ProjectLoaded { get { return projectPath.Length > 0; } }
        public HardwareStatusSnapshot Hardware { get { return hardware.Clone(); } }
        public bool DeviceConnected { get { return hardware.Ready; } }
        // Calibration and pressure-test entries are always visible.  A valid current
        // project enables them; hardware availability only selects LIVE or SIM mode.
        public bool AcquisitionModulesVisible { get { return true; } }
        public bool AcquisitionModulesEnabled { get { return ProjectLoaded; } }

        public bool TryLoadProject(string path, out string message)
        {
            string normalized;
            if (!ProjectValidator.TryValidate(path, out normalized, out message)) return false;
            if (!String.Equals(projectPath, normalized, StringComparison.OrdinalIgnoreCase))
            {
                projectPath = normalized;
                RaiseChanged();
            }
            return true;
        }

        public void ClearProject()
        {
            if (projectPath.Length == 0) return;
            projectPath = String.Empty;
            RaiseChanged();
        }

        public void SetHardware(HardwareStatusSnapshot value)
        { SetHardware(value, true); }

        internal void SetHardware(HardwareStatusSnapshot value, bool notify)
        {
            hardware = value == null ? new HardwareStatusSnapshot() : value.Clone();
            if (notify) RaiseChanged();
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    internal static class ProjectValidator
    {
        public static bool TryValidate(string path, out string normalized, out string message)
        {
            normalized = String.Empty;
            message = String.Empty;
            try
            {
                if (String.IsNullOrWhiteSpace(path))
                {
                    message = "请选择项目目录。";
                    return false;
                }
                normalized = Path.GetFullPath(path.Trim());
                if (!Directory.Exists(normalized))
                {
                    message = "项目目录不存在。";
                    return false;
                }
                string item = Path.Combine(normalized, "ITEM.in");
                string cable = Path.Combine(normalized, "CABLE.in");
                if (!File.Exists(item) || !File.Exists(cable))
                {
                    message = "有效项目必须在顶层同时包含 ITEM.in 和 CABLE.in。";
                    return false;
                }
                using (FileStream stream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length < 1) throw new InvalidDataException("ITEM.in 为空。");
                }
                using (FileStream stream = new FileStream(cable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length < 1) throw new InvalidDataException("CABLE.in 为空。");
                }
                message = "项目已加载。";
                return true;
            }
            catch (Exception error)
            {
                normalized = String.Empty;
                message = "项目校验失败：" + error.Message;
                return false;
            }
        }
    }

    internal static class AppPaths
    {
        // Preview preferences, diagnostics and audit logs must not read or
        // overwrite the installed release's profile. Do not add a legacy
        // fallback or automatic migration here; project locations are separate.
#if DAC_REBUILD_FACTORYPRESS_20261012
        internal const string ProfileDirectoryName = "DAC-Rebuild-FactoryPress-20261012";
#elif PUBLIC_BETA_NO_DRIVER_V112_20261011
        internal const string ProfileDirectoryName = "PublicBeta-NoDriver-1.1.2-20261011";
#elif PUBLIC_BETA_NO_DRIVER_20261011
        internal const string ProfileDirectoryName = "PublicBeta-NoDriver-20261011";
#elif PRESSURE_STANDALONE_20261008
        internal const string ProfileDirectoryName = "Pressure-Standalone-20261008";
#elif V30_BATCH_VALIDATION_20261008
        internal const string ProfileDirectoryName = "Validation-V30-Batch-20261008";
#elif V30_VALIDATION_20261007
        internal const string ProfileDirectoryName = "Validation-V30-20261007";
#elif R27_VALIDATION_20261007
        internal const string ProfileDirectoryName = "Validation-R27-20261007";
#elif FROZEN_INTERNAL_20261006
        internal const string ProfileDirectoryName = "Frozen-20261006-Internal01";
#else
        internal const string ProfileDirectoryName = "Preview-1.1.3";
#endif
        public static readonly string LocalDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XIONGXIN", "HVXUnifiedDesktop", ProfileDirectoryName);

        public static string SettingsFile { get { return Path.Combine(LocalDataDirectory, "settings.xml"); } }
        public static string DiagnosticsDirectory { get { return Path.Combine(LocalDataDirectory, "diagnostics"); } }
    }
}
