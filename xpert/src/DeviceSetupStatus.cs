using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Xiongxin.OWTS.Desktop
{
    // Navigation requests only. The host owns settings dialogs
    // and read-only connection refreshes; this enum grants no hardware authority.
    internal enum DeviceSetupAction
    {
        None = 0,
        HardwareSettings = 1,
        DeviceParameters = 2,
        RecheckConnection = 3,
        ProjectManagement = 4,
        FirstBinding = 5
    }

    internal sealed class DeviceSetupStep
    {
        public string Name, Status, Detail;
        public bool Complete;
        public DeviceSetupAction Action;
        public string ActionLabel;
    }

    internal static class DeviceSetupStatus
    {
        internal static string ParameterHash(AppSettings settings)
        {
            if (settings == null) return String.Empty;
            // Versioned change detector, not a signature or permission to energize hardware.
            var s = new StringBuilder("DeviceParameters.v4|");
            foreach (double value in new[] { settings.DividerRatio, settings.PressureRatio,
                settings.PdSampleRateHz, settings.PdSampleBits, settings.PdCalibrationCorrectionFactor,
                settings.InductanceHenry, settings.InductorDcResistanceOhm, settings.MaximumVoltageKv,
                settings.OscillatingPdCaptureDurationMs, (double)settings.HvupVoltageSource,
                settings.DeviceTriggerVolts, settings.CompensationCapacitanceUf, settings.FinePressureThresholdKv })
                s.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            s.Append(settings.SupportsCosineVlf ? "1|" : "0|").Append(settings.SupportsCombinedVlf ? "1|" : "0|");
            s.Append(settings.EnableFinePressureRamp ? "1|" : "0|");
            s.Append(settings.FinePressureThresholdAutomatic ? "1|" : "0|");
            // Retain old hashes only for the exact pre-existing default law.
            // A changed speed invalidates preparation and parameter confirmation.
            if(settings.PressureRampStepV!=.01||settings.PressureRampIntervalMs!=1)
                s.Append("Ramp|").Append(settings.PressureRampStepV.ToString("R",CultureInfo.InvariantCulture)).Append('|').Append(settings.PressureRampIntervalMs).Append('|');
            if(settings.EndResidualPercent!=4)
                s.Append("EndResidual|").Append(settings.EndResidualPercent.ToString("R",CultureInfo.InvariantCulture)).Append('|');
            var h = settings.Hardware ?? new HardwareSettings();
            foreach (string value in new[] { Mac(h.SelectedHubMac), N(h.ControllerLocation), N(h.AcquisitionLocation),
                N(h.ControllerSerial), N(h.AcquisitionSerial), N(h.ExcludedAcquisitionSerial) })
                s.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToString())));
        }
        internal static string ActualIdentity(HardwareStatusSnapshot hardware)
        {
            if (hardware == null || !hardware.Ready) return String.Empty;
            string hub = Mac(hardware.HubMac), first = N(hardware.ControllerSerial), second = N(hardware.AcquisitionSerial);
            if (!ValidHub(hub)) return String.Empty;
            if (DeviceAssetCatalog.IsPicoSerial(first) && DeviceAssetCatalog.IsArtSerial(second)) return hub + "|" + first + "|" + second;
            if (DeviceAssetCatalog.IsPicoSerial(second) && DeviceAssetCatalog.IsArtSerial(first)) return hub + "|" + second + "|" + first;
            return String.Empty;
        }
        static string N(string value) { return DeviceAssetCatalog.NormalizeIdentity(value); }
        static string Mac(string value) { return N(value).Replace(":", String.Empty).Replace("-", String.Empty); }
        static bool ValidHub(string value)
        { if (value.Length != 12) return false; foreach (char c in value) if (!Uri.IsHexDigit(c)) return false; return true; }
        static bool SameDesignatedHub(AppSettings settings, HardwareStatusSnapshot actual)
        {
            return settings != null && settings.Hardware != null && settings.Hardware.Enabled && actual != null &&
                ValidHub(Mac(actual.HubMac)) && String.Equals(Mac(settings.Hardware.SelectedHubMac), Mac(actual.HubMac), StringComparison.Ordinal);
        }
        internal static void ConfirmParameters(AppSettings candidate, HardwareStatusSnapshot actual)
        {
            string validation;
            if (candidate == null) throw new ArgumentNullException("candidate");
            if (!candidate.TryValidate(out validation)) throw new ArgumentException(validation);
            candidate.DeviceParametersReviewedHash = ParameterHash(candidate);
            candidate.DeviceParametersReviewedIdentity = SameDesignatedHub(candidate, actual) ? ActualIdentity(actual) : String.Empty;
            candidate.DeviceParametersReviewedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }
        internal static bool ParametersSaved(AppSettings settings)
        {
            DateTime when; string validation;
            return settings != null && !String.IsNullOrWhiteSpace(settings.DeviceParametersReviewedUtc) &&
                settings.TryValidate(out validation) &&
                DateTime.TryParse(settings.DeviceParametersReviewedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out when) && when != DateTime.MinValue &&
                when.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(5) &&
                String.Equals(settings.DeviceParametersReviewedHash, ParameterHash(settings), StringComparison.Ordinal);
        }
        internal static bool IdentityMatches(AppSettings settings, HardwareStatusSnapshot actual)
        {
            string identity = ActualIdentity(actual);
            return ParametersSaved(settings) && SameDesignatedHub(settings, actual) && identity.Length != 0 &&
                String.Equals(identity, settings.DeviceParametersReviewedIdentity, StringComparison.Ordinal);
        }
        internal static bool IdentityChanged(AppSettings settings, HardwareStatusSnapshot actual)
        {
            string identity = ActualIdentity(actual);
            return ParametersSaved(settings) && settings.Hardware != null && settings.Hardware.Enabled && identity.Length != 0 &&
                !String.IsNullOrEmpty(settings.DeviceParametersReviewedIdentity) &&
                !String.Equals(identity, settings.DeviceParametersReviewedIdentity, StringComparison.Ordinal);
        }
        internal static string Summary(AppSettings settings, HardwareStatusSnapshot actual, bool zh)
        {
            if (!ParametersSaved(settings)) return zh ? "关键参数待核对 · 查看配置流程" : "Parameters need review · Open setup";
            if (IdentityChanged(settings, actual)) return zh ? "设备已更换，需重新核对参数" : "Device changed; review parameters";
            if (!IdentityMatches(settings, actual)) return zh ? "参数已保存 · 设备身份待核对" : "Parameters saved · Identity review pending";
            return zh ? "参数已核对保存 · 查看配置流程" : "Parameters reviewed and saved · Open setup";
        }
        internal static List<DeviceSetupStep> Steps(AppSettings settings, HardwareStatusSnapshot actual,
            bool projectLoaded, bool checking, bool zh)
        {
            actual = actual ?? new HardwareStatusSnapshot();
            bool enabled = settings != null && settings.Hardware != null && settings.Hardware.Enabled;
            bool saved = ParametersSaved(settings);
            bool connected = enabled && !checking && actual.Ready && SameDesignatedHub(settings, actual);
            bool identified = connected && ActualIdentity(actual).Length > 0;
            bool matched = identified && (actual.AssetMatchStatus == "Matched" || actual.AssetMatchStatus == "Applied");
            var steps = new List<DeviceSetupStep>();
            Add(steps, zh, "1. 工作模式", "1. Working mode", enabled,
                enabled ? "已启用硬件模式" : "当前离线，仅使用仿真", enabled ? "Hardware mode enabled" : "Offline; simulation only");
            Add(steps, zh, "2. 驱动与组件", "2. Drivers and components", enabled && actual.DriverInstalled,
                actual.DriverInstalled ? "已检测到所需组件" : "尚未确认驱动与组件，请检查设置", actual.DriverInstalled ? "Required components detected" : "Components not confirmed; check settings");
            Add(steps, zh, "3. 连接检查", "3. Connection check", connected,
                checking ? "正在重新检查连接" : connected ? "指定设备连接检查通过" : "未确认指定设备连接，请检查硬件模式、供电与连接",
                checking ? "Rechecking connection" : connected ? "Designated device connection verified" : "Designated connection not confirmed; check hardware mode, power and connections");
            Add(steps, zh, "4. 实机身份", "4. Actual device identity", identified,
                identified ? "两张板卡实际序列号已取得" : "尚未取得完整实机序列号；不以端口或备注代替",
                identified ? "Both actual device serials available" : "Actual serials incomplete; ports and notes are not substitutes");
            Add(steps, zh, "5. 出厂档案", "5. Factory record", matched,
                matched ? "已唯一匹配出厂档案" : "未自动匹配；可通过人工核对保存参数，不等于设备断线",
                matched ? "Unique factory record matched" : "Not auto-matched; manual parameter review is available. This is not disconnection");
            Add(steps, zh, "6. 关键参数保存", "6. Save key parameters", saved,
                saved ? "已保存当前参数，时间：" + settings.DeviceParametersReviewedUtc : "现有参数已载入；核对后保存一次，无需重复输入",
                saved ? "Current parameters saved at " + settings.DeviceParametersReviewedUtc : "Existing values loaded; review and save once without re-entering them");
            bool associated = connected && IdentityMatches(settings, actual);
            Add(steps, zh, "7. 参数与设备关联", "7. Bind parameters to device", associated,
                associated ? "本次设备与已确认参数一致" : checking ? "连接复核完成后再判断参数关联" : IdentityChanged(settings, actual) ? "检测到另一台设备，需重新核对" : "需要完整实机身份，并在设备设置中核对保存",
                associated ? "Current device matches the reviewed parameters" : checking ? "Waiting for the connection recheck" : IdentityChanged(settings, actual) ? "Different device detected; review again" : "Obtain actual identity, then review and save in device settings");
            Add(steps, zh, "8. 项目", "8. Project", projectLoaded,
                projectLoaded ? "项目已加载" : "新建或加载有效项目", projectLoaded ? "Project loaded" : "Create or load a valid project");
            steps.Add(new DeviceSetupStep { Name = zh ? "9. 高压试验" : "9. High-voltage testing", Status = zh ? "按轮确认" : "Confirm each round",
                Detail = zh ? "加压页已接入独立受监督控制进程。须具有适用校准、有效急停回读，并在非零加压前确认现场条件。连接或配置完成不代表取得加压许可。" : "The pressure page uses a supervised control process. Applicable calibration, current emergency feedback and an explicit nonzero-round confirmation are required. Connection or setup alone is not permission to energize." });
            ConfigureAction(steps[0], DeviceSetupAction.HardwareSettings, zh, "设置工作模式", "Set working mode",
                "双击进入 系统管理 → 硬件集成，选择是否启用硬件模式并保存；离线模式可继续使用仿真，不会标为实机配置完成。",
                "Open System administration → Hardware integration, choose hardware mode and save. Offline simulation remains available, but does not count as live setup completion.");
            ConfigureAction(steps[1], DeviceSetupAction.HardwareSettings, zh, "核对驱动路径", "Check driver paths",
                "双击进入 系统管理 → 硬件集成，核对驱动标记、驱动包及安装程序路径。此入口不执行安装；完成外部驱动安装后重新检查连接。",
                "Open System administration → Hardware integration to check the driver marker, package and installer paths. This action does not install drivers. Recheck after external installation.");
            ConfigureAction(steps[2], checking ? DeviceSetupAction.None : enabled ? DeviceSetupAction.RecheckConnection : DeviceSetupAction.HardwareSettings, zh,
                checking ? "等待检查" : enabled ? "重新检查连接" : "启用硬件设置", checking ? "Wait for check" : enabled ? "Recheck connection" : "Hardware settings",
                checking ? "本次检查仍在进行，请等待结果，不重复启动。" : enabled ? "确认供电、网线/USB及指定设备网络地址后双击，连接/复核指定设备；不采集、不输出高压。" : "当前为离线模式。先在系统管理中核对硬件模式和设备地址，保存后再重新检查连接。",
                checking ? "A check is already running. Wait for its result." : enabled ? "Check power, Ethernet/USB and the designated device address, then double-click to connect/recheck the designated device. No acquisition or HV output is started." : "Currently offline. Review hardware mode and address settings in System administration, save, then recheck.");
            ConfigureAction(steps[3], DeviceSetupAction.HardwareSettings, zh, "核对设备配置", "Check device setup",
                "双击进入 系统管理 → 硬件集成，核对指定设备网络地址和板卡配置，连接实机后重新检查。必须读取真实序列号，填写备注或端口不能完成身份确认。",
                "Open System administration → Hardware integration to review the designated address and board configuration, then connect and recheck. Actual serials must be read; notes or ports cannot confirm identity.");
            ConfigureAction(steps[4], DeviceSetupAction.DeviceParameters, zh, "人工核对参数", "Review parameters",
                "双击进入设备关键参数，核对并保存。设备未登记不等于断线；人工保存不会伪造出厂档案匹配。",
                "Open Device key parameters, review and save. An unregistered device is not disconnected; manual saving does not fabricate a factory-record match.");
            ConfigureAction(steps[5], DeviceSetupAction.DeviceParameters, zh, "核对并保存参数", "Review and save",
                "双击进入设备关键参数，核对已有数值并点击保存设置。局放采样率/位数在HV DAC Xpert Plus Data 页；改动后需再次核对保存设备参数。",
                "Open Device key parameters, review existing values and Save settings. PD sample rate/bits are managed under HV DAC Xpert Plus Data; review and save device parameters again after changing them.");
            ConfigureAction(steps[6], checking ? DeviceSetupAction.None : identified ? DeviceSetupAction.DeviceParameters : enabled ? DeviceSetupAction.RecheckConnection : DeviceSetupAction.HardwareSettings, zh,
                checking ? "等待检查" : identified ? "核对设备关联" : enabled ? "重新读取身份" : "核对硬件设置", checking ? "Wait for check" : identified ? "Review device binding" : enabled ? "Recheck identity" : "Hardware settings",
                "取得完整实机身份后，进入设备关键参数核对并保存，才会记录设备与参数的关联；离线保存只保存参数，不虚构设备身份。",
                "After actual identity is complete, review and save Device key parameters to bind it. Offline saving retains parameters only and never invents a device identity.");
            ConfigureAction(steps[7], DeviceSetupAction.ProjectManagement, zh, "打开项目管理", "Open projects",
                "双击进入项目管理，选择有效项目后加载；需要新项目时使用创建项目入口。这里只跳转，不自动改写或创建项目文件。",
                "Open project management and load a valid project; use Create project when needed. Navigation does not create or modify project files.");
            ConfigureAction(steps[8], DeviceSetupAction.None, zh, "仅说明", "Information only",
                "此项没有可跳转的高压启用开关；即使前面均已完成，本窗口也不会启动采集或高压输出。",
                "There is no enabling switch for high-voltage testing here. Even if the preceding steps are complete, this window starts neither acquisition nor HV output.");
            return steps;
        }
        static void ConfigureAction(DeviceSetupStep step, DeviceSetupAction action, bool zh,
            string zLabel, string eLabel, string zGuide, string eGuide)
        { step.Action = action; step.ActionLabel = zh ? zLabel : eLabel; step.Detail += "\r\n" + (zh ? zGuide : eGuide); }
        static void Add(List<DeviceSetupStep> steps, bool zh, string zn, string en, bool done, string zd, string ed)
        { steps.Add(new DeviceSetupStep { Name = zh ? zn : en, Complete = done, Status = done ? (zh ? "已完成" : "Complete") : (zh ? "待处理" : "Pending"), Detail = zh ? zd : ed }); }
    }
}
