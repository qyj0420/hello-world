using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        // Test substitution controls only the editor/file location; it never
        // creates identity evidence or invokes a native connection.
        internal Func<AppSettings, string, bool, AppSettings> DeviceParameterEditorForTests;
        internal string DeviceParameterSettingsPathForTests;
        string DeviceParameterSettingsPath { get { return DeviceParameterSettingsPathForTests ?? AppPaths.SettingsFile; } }

        bool LoadSavedDeviceParameters(HardwareStatusSnapshot snapshot)
        {
            if (snapshot == null || DeviceParameterProfiles.Key(snapshot).Length == 0) return false;
            try {
                string key = DeviceParameterProfiles.Key(snapshot);
                if (!DeviceParameterProfiles.Has(settings, key)) return false;
                if (!DeviceParameterProfiles.IsConfigured(settings, snapshot)) {
                    var next = settings.Clone();
                    if (!DeviceParameterProfiles.Load(next, snapshot)) return false;
                    next.Save(DeviceParameterSettingsPath);
                    // Preserve the shared object held by existing services.
                    foreach (var p in typeof(AppSettings).GetProperties())
                        if (p.CanRead && p.CanWrite) p.SetValue(settings, p.GetValue(next, null), null);
                }
                snapshot.AssetMatchStatus = "LocalProfile"; snapshot.AssetParametersApplied = true;
                snapshot.AssetMatchMessage = IsChinese ? "已加载此设备的最新本机配置。" : "Latest local configuration loaded for this device.";
                return true;
            } catch (Exception error) {
                snapshot.AssetMatchStatus = "ProfileLoadFailed"; snapshot.AssetParametersApplied = false;
                snapshot.AssetMatchMessage = IsChinese ? "设备配置未加载，请核对后保存。" : "Device configuration was not loaded; review and save it.";
                if (!verificationMode) new Diagnostics().Warning("DeviceParameters", error.GetType().Name);
                // Do not fall back to another device's values or factory defaults
                // after a saved local profile fails to load.
                settings.ActiveDeviceProfileKey = String.Empty; settings.DeviceParametersReviewedHash = String.Empty;
                return true;
            }
        }
        AppSettings DeviceParameterDraft(HardwareStatusSnapshot actual)
        {
            var candidate = settings.Clone(); string key = DeviceParameterProfiles.Key(actual);
            if (DeviceParameterProfiles.Load(candidate, actual)) return candidate;
            // A first pair uses neutral defaults, never the previous pair's values.
            var defaults = new AppSettings();
            candidate.DividerRatio = defaults.DividerRatio; candidate.DeviceTriggerVolts = defaults.DeviceTriggerVolts;
            candidate.InductanceHenry = defaults.InductanceHenry; candidate.InductorDcResistanceOhm = defaults.InductorDcResistanceOhm;
            candidate.MaximumVoltageKv = defaults.MaximumVoltageKv; candidate.PressureRatio = DeviceParameterProfiles.PressureRatioFor(candidate.MaximumVoltageKv);
            candidate.EnableFinePressureRamp = defaults.EnableFinePressureRamp;
            candidate.PressureRampStepV=defaults.PressureRampStepV;candidate.PressureRampIntervalMs=defaults.PressureRampIntervalMs;
            candidate.FinePressureThresholdAutomatic = defaults.FinePressureThresholdAutomatic;
            candidate.FinePressureThresholdKv = defaults.FinePressureThresholdKv;
            candidate.CompensationCapacitanceUf = defaults.CompensationCapacitanceUf;
            candidate.SupportsCosineVlf = defaults.SupportsCosineVlf; candidate.SupportsCombinedVlf = defaults.SupportsCombinedVlf;
            var match = deviceAssetCatalog == null ? null : deviceAssetCatalog.Match(actual);
            if (match != null && match.IsMatch) {
                var r = match.Record; candidate.DividerRatio = r.DividerRatio.Value;
                candidate.InductanceHenry = r.Inductor.InductanceHenry.Value; candidate.InductorDcResistanceOhm = r.Inductor.DcResistanceOhm.Value;
                if (r.Compensation != null && r.Compensation.CapacitanceNanoFarad.HasValue)
                    candidate.CompensationCapacitanceUf = r.Compensation.CapacitanceNanoFarad.Value / 1000.0;
            }
            candidate.ActiveDeviceProfileKey = String.Empty; candidate.DeviceParametersReviewedHash = String.Empty;
            return candidate;
        }
        async Task<bool> EditDeviceParameters()
        {
            if (activeModule != null || settingsDialogActive || hardwareRefreshInProgress || hardwareDisconnectInProgress || detectionSelectionBusy || firstBindingInProgress) return false;
            var actual = state.Hardware; string key = DeviceParameterProfiles.Key(actual);
            if (key.Length == 0 || key != DeviceParameterProfiles.ConfiguredKey(settings)) {
                DeviceConnectionNotice(IsChinese ? "请先连接要配置的设备。" : "Connect the device to configure first."); return false;
            }
            AppSettings before = settings.Clone(); bool saved = false;
            settingsDialogActive = true; if (devicePollTimer != null) devicePollTimer.Stop();
            try {
                string path = DeviceParameterSettingsPath; bool absent = !File.Exists(path);
                string digest = absent ? null : AppSettings.FileDigest(path);
                var draft = DeviceParameterDraft(actual); bool first = !DeviceParameterProfiles.Has(settings, key);
                if (SettingsAccessControl.RequiresDeviceAuthorization(first))
                {
                    string password = null;
                    // Offline verification never displays a modal prompt or manufactures authorization.
                    if (verificationMode || !MaintenanceAuthorizationPrompt.TryRead(this, SettingsSection.Device, settings.LanguageCode, out password)) return false;
                    bool authorized;
                    try { authorized = SettingsAccessControl.Verify(SettingsSection.Device, password); }
                    finally { password = null; }
                    RecordOperation(authorized ? OperationAuditAction.AuthorizationAccepted : OperationAuditAction.AuthorizationRejected,
                        authorized ? AuditOutcome.Succeeded : AuditOutcome.Failed, null, AuditModule.Settings);
                    if (!authorized) {
                        DeviceConnectionNotice(IsChinese ? "口令不正确，设备参数未打开。" : "The password is incorrect. Device settings remain closed.");
                        return false;
                    }
                }
                AppSettings candidate = null;
                if (DeviceParameterEditorForTests != null) candidate = DeviceParameterEditorForTests(draft, key, first);
                else if (!verificationMode) using (var form = new DeviceParametersForm(draft, key, first, IsChinese))
                    if (form.ShowDialog(this) == DialogResult.OK) candidate = form.Candidate;
                if (candidate == null) return false;
                // This internal token means explicit Save, not password validation.
                // Existing-device edits have already passed the maintenance gate in the guarded edition.
                candidate.DeviceProfileEditAuthorized = true;
                DeviceParameterProfiles.Confirm(candidate, state.Hardware);
                if (!candidate.TrySaveIfUnchanged(path, digest, absent, () =>
                    key == DeviceParameterProfiles.Key(state.Hardware) && DeviceConnectionKey(before) == DeviceConnectionKey(settings)))
                    throw new InvalidOperationException(IsChinese ? "设置已在另一窗口改变，请重新打开。" : "Settings changed in another window; reopen the editor.");
                settings = candidate; saved = true;
                if (!verificationMode) RecordOperation(OperationAuditAction.SettingsSaved, AuditOutcome.Succeeded, state.ProjectPath, AuditModule.Settings);
                return true;
            } catch (Exception error) {
                if(!verificationMode)new Diagnostics().Error("DeviceParameters","Device parameters were not saved.",error);
                DeviceConnectionNotice(IsChinese ? "设备参数未保存。请重新打开配置窗口，核对数值后再保存；如仍失败，请检查本机设置文件是否可写。" : "Device parameters were not saved. Reopen the configuration, check the values and save again. If the problem persists, check that local settings are writable."); return false;
            } finally {
                // Keep the editor workflow owned until a retained device
                // session has acknowledged cleanup; another navigation must
                // not race this asynchronous handoff.
                try { if (!IsDisposed) await FinishSettingsWorkflow(saved, before); }
                finally { settingsDialogActive = false; if (!IsDisposed) ApplyState(); }
            }
        }
    }
}
