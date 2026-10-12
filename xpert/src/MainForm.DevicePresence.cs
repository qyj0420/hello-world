using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private HardwareStatusSnapshot validatedDeviceSession;
        private string validatedDeviceConfiguration;
        private long validatedDeviceGeneration;
        internal Func<string, bool> DevicePresentForTests;

        private string DeviceConfigurationKey()
        {
            return DeviceConnectionKey(settings);
        }

        private static string DeviceConnectionKey(AppSettings value)
        {
            var h=value.Hardware;
            // Installation paths and acquisition parameters are not connection
            // identity. The acquisition contract still validates those separately.
            return String.Join("\n",new[]{h.Enabled.ToString(),h.SelectedHubMac,h.ControllerLocation,h.AcquisitionLocation,
                h.ControllerSerial,h.AcquisitionSerial,h.ExcludedAcquisitionSerial,h.DriverMarkerPath,h.StatusJsonPath,
                h.ConnectorExecutablePath,h.IntegrationExecutablePath});
        }

        private async Task FinishSettingsWorkflow(bool saved,AppSettings before)
        {
            if(saved) {
                bool connectionChanged=DeviceConnectionKey(before)!=DeviceConnectionKey(settings);
                bool serialChanged=before.Hardware.ControllerSerial!=settings.Hardware.ControllerSerial ||
                    before.Hardware.AcquisitionSerial!=settings.Hardware.AcquisitionSerial;
                bool enabledNow=!before.Hardware.Enabled&&settings.Hardware.Enabled;
                var remembered=validatedDeviceSession;
                string rememberedKey=validatedDeviceConfiguration;
                bool rememberedCurrent=validatedDeviceGeneration==hardwareRefreshGeneration;
                if(!await ShowHomeAsync()){
                    if(moduleHost!=null&&moduleHost.HasPressureDeviceOwner){
                        validatedDeviceSession=null;hardwareManuallyDisconnected=true;hardwareConnectionQueued=false;
                        pendingHardwareNavigation=null;hardwareRefreshRequestEpoch++;
                        if(devicePollTimer!=null)devicePollTimer.Stop();
                        var retained=state.Hardware.Clone();retained.Connected=false;retained.StatusCode="ResourceCleanupFailed";
                        retained.Message="设置已保存，但原设备会话清理未确认；未替换会话，禁止新试验。 / Settings saved, but previous session cleanup is unverified; no replacement session was opened.";
                        state.SetHardware(retained);
                    }
                    return;
                }
                ApplyLanguageAndBranding();
                // A hidden pressure page still owns its one DI session. Settings
                // replacement must await that owner's acknowledged cleanup,
                // rather than disposing the host and opening a competing task.
                try {
                    if(moduleHost!=null)await moduleHost.CloseRetainedPressureSessionsAsync();
                    if(IsDisposed||Disposing)return;
                } catch(Exception cleanup) {
                    validatedDeviceSession=null;
                    hardwareManuallyDisconnected=true;
                    hardwareConnectionQueued=false;
                    pendingHardwareNavigation=null;
                    hardwareRefreshRequestEpoch++;
                    if(devicePollTimer!=null)devicePollTimer.Stop();
                    var failed=state.Hardware.Clone();
                    failed.Connected=false;failed.StatusCode="ResourceCleanupFailed";
                    failed.Message="设置已保存，但原设备会话清理未确认；未替换会话，禁止新试验。 / Settings saved, but previous session cleanup is unverified; no replacement session was opened.";
                    failed.Diagnostic=cleanup.Message;
                    state.SetHardware(failed);ApplyState();
                    return;
                }
                // The adapter consumes only fields in DeviceConnectionKey. Keep
                // its immutable old settings snapshot when all those fields are
                // unchanged; the module host receives the new parameter object.
                // No cross-thread mutation of the adapter's settings is needed.
                if(connectionChanged) RecreateIntegrationServices();
                else RecreateModuleHost();
                // Rebind unchanged configuration to the new service generation;
                // do not create fresh identity evidence or refresh CheckedUtc.
                if(!connectionChanged&&rememberedCurrent&&remembered!=null&&rememberedKey==DeviceConfigurationKey()) {
                    validatedDeviceSession=remembered;
                    validatedDeviceGeneration=hardwareRefreshGeneration;
                } else validatedDeviceSession=null;
                if(connectionChanged) {
                    state.SetHardware(new HardwareStatusSnapshot {HardwareEnabled=IsHardwareEnabled,StatusCode="SettingsChanged",
                        Message="设备连接设置已变化，请重新连接。 / Connection settings changed; reconnect the device."});
                    if(IsHardwareEnabled&&(serialChanged||enabledNow)&&!hardwareManuallyDisconnected)
                        BeginHardwareRefresh(true,true);
                    else if(IsHardwareEnabled)hardwareManuallyDisconnected=true;
                }
            }
            // Closing/cancelling an unchanged editor must not recreate services,
            // invalidate a session or connect. Resume the ordinary home monitor.
            if(IsHardwareEnabled&&!hardwareManuallyDisconnected) {
                if(devicePollTimer!=null)devicePollTimer.Start();
            } else {
                if(devicePollTimer!=null)devicePollTimer.Stop();
                if(!IsHardwareEnabled)state.SetHardware(new HardwareStatusSnapshot());
            }
            ApplyState();
        }

        private void RememberValidatedDevice(HardwareStatusSnapshot value)
        {
            validatedDeviceSession = null;
            if (value == null || !value.Ready || !value.FromLiveIntegrationProcess ||
                String.IsNullOrWhiteSpace(value.ControllerSerial) || String.IsNullOrWhiteSpace(value.AcquisitionSerial) ||
                !ValidDeviceInstance(value.ControllerInstanceId) || !ValidDeviceInstance(value.AcquisitionInstanceId) ||
                String.Equals(value.ControllerInstanceId, value.AcquisitionInstanceId, StringComparison.OrdinalIgnoreCase)) return;
            validatedDeviceSession = value.Clone();
            validatedDeviceConfiguration = DeviceConfigurationKey();
            validatedDeviceGeneration = hardwareRefreshGeneration;
        }

        private bool TryReuseValidatedDevice()
        {
            var current = state.Hardware;
            var saved = validatedDeviceSession;
            if (!CanOpenLiveCalibration() || hardwareConnectionQueued || saved == null ||
                validatedDeviceGeneration != hardwareRefreshGeneration || validatedDeviceConfiguration != DeviceConfigurationKey() ||
                !current.Ready || !SameConnectionHealth(saved, current) ||
                !String.Equals(saved.ControllerInstanceId, current.ControllerInstanceId, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(saved.AcquisitionInstanceId, current.AcquisitionInstanceId, StringComparison.OrdinalIgnoreCase)) {
                validatedDeviceSession = null;
                return false;
            }
            // Read the two exact Windows device nodes only: no SDK open, network
            // scan, connector process, relay or output operation. This is not a
            // fresh electrical safety check and must not refresh CheckedUtc.
            bool present;
            try {
                if (verificationMode && DevicePresentForTests == null) return false;
                Func<string,bool> probe = DevicePresentForTests ?? DeviceInstanceStarted;
                present = probe(saved.ControllerInstanceId) && probe(saved.AcquisitionInstanceId);
            } catch {
                validatedDeviceSession = null;
                current.Connected=false;current.StatusCode="PresenceUnknown";
                current.Message="设备连接状态无法确认，请检查后刷新设备。";
                state.SetHardware(current);return false;
            }
            if (present) return true;
            validatedDeviceSession = null;
            current.Connected = false; current.StatusCode = "Disconnected";
            current.Message = "指定设备已断开，请检查连接后重新连接。";
            state.SetHardware(current);
            return false;
        }

        private static bool ValidDeviceInstance(string id)
        { return !String.IsNullOrWhiteSpace(id) && id.Length < 1024 && id.IndexOf('\\') > 0; }

        private static bool DeviceInstanceStarted(string id)
        {
            if (!ValidDeviceInstance(id)) return false;
            uint node, status, problem;
            // NORMAL (0) excludes phantom nodes; never CANCELREMOVE a device.
            return CM_Locate_DevNodeW(out node, id, 0) == 0 &&
                CM_Get_DevNode_Status(out status, out problem, node, 0) == 0 &&
                (status & 0x8) != 0 && (status & 0x400) == 0; // DN_STARTED, DN_HAS_PROBLEM
        }
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    }
}
