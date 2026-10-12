using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private bool hardwareManuallyDisconnected, hardwareDisconnectInProgress, hardwareRefreshSilent, hardwareConnectionQueued;
        private Control notifiedDisconnectedModule;
        internal Func<Task<HardwareStatusSnapshot>> DisconnectDeviceForTests;
        internal Func<bool> ConfirmDeviceDisconnectForTests;
        internal Action<string> DeviceConnectionNoticeForTests;
        internal Func<bool, Action<HardwareStatusSnapshot>, bool> RefreshDeviceForTests;
        internal Action<bool> CalibrationNavigationForTests;
        internal Action<string, bool> TestNavigationForTests;
        internal Func<DateTime> HomeConnectionUtcForTests;
        private DateTime homeConnectionNotBeforeUtc;
        private int homeConnectionRetryStep;

        private DateTime HomeConnectionUtc { get { return HomeConnectionUtcForTests == null ? DateTime.UtcNow : HomeConnectionUtcForTests(); } }
        private void NoteHomeConnectionAttempt()
        {
            int seconds = homeConnectionRetryStep == 0 ? 2 : homeConnectionRetryStep == 1 ? 4 : homeConnectionRetryStep == 2 ? 8 : 15;
            homeConnectionRetryStep = Math.Min(3,homeConnectionRetryStep+1);
            homeConnectionNotBeforeUtc = HomeConnectionUtc.AddSeconds(seconds);
        }
        private bool HasSavedAutomaticConnectionTarget()
        {
            try {
                string key = DeviceParameterProfiles.ConfiguredKey(settings);
                return key.Length != 0 && key == settings.ActiveDeviceProfileKey && DeviceParameterProfiles.Has(settings,key) &&
                    !String.IsNullOrWhiteSpace(settings.Hardware.SelectedHubMac) &&
                    !String.IsNullOrWhiteSpace(settings.Hardware.ControllerLocation) && !String.IsNullOrWhiteSpace(settings.Hardware.AcquisitionLocation);
            } catch { return false; }
        }

        private void BeginLiveCalibrationNavigation(long navigationRequest)
        { BeginLiveTestNavigation(ModuleHost.CalibrationTest, navigationRequest); }

        private void CompleteLiveTestNavigation(string id, long request, bool simulation, HardwareStatusSnapshot snapshot)
        {
            if(request!=navigationRequestSequence || IsDisposed || pressureExitInProgress)return;
            if (!simulation && id == ModuleHost.PressureTest && !DeviceParameterProfiles.IsConfigured(settings, snapshot)) {
                DeviceConnectionNotice(IsChinese ? "请先在设备设置中完成当前设备的参数配置，再进入加压测试。" :
                    "Complete the connected device's configuration in Device settings before opening the pressure test.");
                return;
            }
            if(TestNavigationForTests!=null)TestNavigationForTests(id,simulation);
            else if(id==ModuleHost.CalibrationTest && CalibrationNavigationForTests!=null)CalibrationNavigationForTests(simulation);
            else if(simulation)OpenTestModuleInSimulation(id,DeviceConnectionStatusText(snapshot),request);
            else OpenTestModuleControl(id,request,false);
        }

        private async void BeginLiveTestNavigation(string id, long navigationRequest)
        {
            if(navigationRequest!=navigationRequestSequence||IsDisposed||settingsDialogActive||settingsWorkflowActive||hardwareDisconnectInProgress||pressureExitInProgress)return;
            var navigationOwner=moduleHost;
            if(id!=ModuleHost.PressureTest&&moduleHost!=null){
                try{await moduleHost.CloseRetainedPressureSessionsAsync();}
                catch(Exception error){new Diagnostics().Error("AcquisitionNavigationRelease","Previous device operation did not acknowledge release; navigation blocked.",error);DeviceConnectionNotice(IsChinese?"设备操作尚未确认结束，未开始采集。请按现场规程检查设备；如仍失败，请联系维护人员。":"The equipment operation has not been confirmed as ended; acquisition was not started. Check the equipment under site procedures. If it still fails, contact support.");return;}
                if(navigationRequest!=navigationRequestSequence||IsDisposed||settingsDialogActive||settingsWorkflowActive||hardwareDisconnectInProgress||!Object.ReferenceEquals(navigationOwner,moduleHost))return;
            }
            var recent=state.Hardware;
            if(String.Equals(recent.StatusCode,"PresenceUnknown",StringComparison.Ordinal)){
                DeviceConnectionNotice(DeviceConnectionStatusText(recent));return;
            }
            // Navigation consumes the established session; it never owns a new
            // SDK inspection. Unknown/lost sessions require the explicit Connect
            // action, while actual acquisition independently checks identity.
            if(!hardwareRefreshInProgress&&!hardwareConnectionQueued&&CanOpenLiveCalibration()&&recent.Ready&&recent.FromLiveIntegrationProcess){
                if(validatedDeviceSession!=null&&!TryReuseValidatedDevice()){
                    DeviceConnectionNotice("设备连接已变化，请使用“刷新设备”重新连接。");return;
                }
                CompleteLiveTestNavigation(id,navigationRequest,false,recent);return;
            }
            bool identifiedSession = validatedDeviceSession != null;
            double age=recent==null?Double.PositiveInfinity:(DateTime.UtcNow-recent.CheckedUtc).TotalSeconds;
            // Reuse the identified session while the two exact OS nodes remain
            // present. Legacy/status-file sources keep the short freshness rule.
            // Never overlap an in-flight discovery with the acquisition coordinator.
            // Actual capture still performs its own connection and low-output checks.
            if(!hardwareRefreshInProgress&&!hardwareConnectionQueued&&CanOpenLiveCalibration()&&recent.Ready&&
                (TryReuseValidatedDevice() || (!identifiedSession&&age>=0&&age<=10&&state.Hardware.Ready))) {
                // Leave the timer enabled: CanPollHomeDevice suppresses it while
                // a module is open and resumes presence checks on return home.
                CompleteLiveTestNavigation(id,navigationRequest,false,recent);
                return;
            }
            // Join/finish any home inspection before admitting a live page. Its
            // SDK enumeration must not overlap a newly started acquisition.
            BeginHardwareRefresh(false, true, delegate(HardwareStatusSnapshot snapshot)
            {
                if (navigationRequest != navigationRequestSequence || IsDisposed) return;
                bool simulation = snapshot == null || !snapshot.Ready || !CanOpenLiveCalibration();
                CompleteLiveTestNavigation(id,navigationRequest,simulation,snapshot);
            });
        }

        private bool CanPollHomeDevice()
        {
            return IsHardwareEnabled && activeModule == null && !pressureExitInProgress && !settingsDialogActive && !settingsWorkflowActive &&
                !deviceManagerOpening && !detectionSelectionBusy && !firstBindingInProgress &&
                !hardwareManuallyDisconnected && !hardwareDisconnectInProgress && !hardwareRefreshInProgress &&
                moduleHost != null && !moduleHost.HasExternalCalibrationProcess && !moduleHost.HasPressureDeviceOwner;
        }

        private void PollHomeDeviceConnection()
        {
            TryShowPendingDeviceSetup();
            // A failed passive node query is not permission to repeatedly launch
            // an expensive connector. Recovery is the explicit Refresh action.
            if(String.Equals(state.Hardware.StatusCode,"PresenceUnknown",StringComparison.Ordinal))return;
            if (CanPollHomeDevice() && state.Hardware.Ready && state.Hardware.FromLiveIntegrationProcess) {
                if(validatedDeviceSession!=null)TryReuseValidatedDevice();
                return;
            }
            if (CanPollHomeDevice() && !TryReuseValidatedDevice())
            {
                if (homeConnectionRetryStep >= 3 || HomeConnectionUtc < homeConnectionNotBeforeUtc) return;
                if (HasSavedAutomaticConnectionTarget()) {
                    string reason;
                    string code = state.Hardware.StatusCode;
                    // Only an ordinary absent/not-ready transport may retry.
                    // Unknown, identity errors and failed cleanup need action.
                    bool retryable = code == "Offline" || code == "NotReady" || code == "Disconnected";
                    if (hardwareConnectionQueued || (retryable && homeConnectionRetryStep < 3 && HomeConnectionUtc >= homeConnectionNotBeforeUtc && CheckProjectReleaseIdle(out reason)))
                        BeginHardwareRefresh(true, false);
                    return;
                }
                BeginHardwareRefresh(false, false);
            }
        }

        private bool CanOpenLiveCalibration()
        { return IsHardwareEnabled && !deviceManagerOpening && !hardwareManuallyDisconnected && !hardwareDisconnectInProgress && state.DeviceConnected; }

        private async void ReconnectCurrentDevice()
        {
            if (!IsHardwareEnabled || hardwareDisconnectInProgress || settingsDialogActive || settingsWorkflowActive || pressureExitInProgress) return;
            if (deviceManagerOpening) return;
            var connectionOwner=moduleHost;
            long connectionNavigation=navigationRequestSequence;
            try{if(connectionOwner!=null)await connectionOwner.CloseRetainedPressureSessionsAsync();}
            catch(Exception error){new Diagnostics().Error("ReconnectRelease","Previous device operation did not acknowledge release; reconnection blocked.",error);DeviceConnectionNotice(IsChinese?"设备操作尚未确认结束，未重新连接。请按现场规程检查设备；如仍失败，请联系维护人员。":"The equipment operation has not been confirmed as ended; reconnection was not started. Check the equipment under site procedures. If it still fails, contact support.");return;}
            if(IsDisposed||Disposing||settingsDialogActive||settingsWorkflowActive||hardwareDisconnectInProgress||
                connectionNavigation!=navigationRequestSequence||!Object.ReferenceEquals(connectionOwner,moduleHost))return;
            homeConnectionRetryStep = 0;
            hardwareManuallyDisconnected = false;
            BeginHardwareRefresh(true, true);
        }

        private static bool SameConnectionHealth(HardwareStatusSnapshot a, HardwareStatusSnapshot b)
        {
            return a != null && b != null && a.HardwareEnabled == b.HardwareEnabled &&
                a.DriverInstalled == b.DriverInstalled && a.Connected == b.Connected && a.IdentityValid == b.IdentityValid &&
                String.Equals(a.StatusCode, b.StatusCode, StringComparison.Ordinal) &&
                String.Equals(a.HubMac, b.HubMac, StringComparison.Ordinal) &&
                String.Equals(a.ControllerSerial, b.ControllerSerial, StringComparison.Ordinal) &&
                String.Equals(a.AcquisitionSerial, b.AcquisitionSerial, StringComparison.Ordinal) &&
                String.Equals(a.ControllerDeviceName, b.ControllerDeviceName, StringComparison.Ordinal) &&
                String.Equals(a.AcquisitionDeviceName, b.AcquisitionDeviceName, StringComparison.Ordinal) &&
                a.FromLiveIntegrationProcess == b.FromLiveIntegrationProcess &&
                a.ConnectionReadbackVerified == b.ConnectionReadbackVerified &&
                String.Equals(a.IntegrationRequestMode, b.IntegrationRequestMode, StringComparison.Ordinal) &&
                String.Equals(a.IntegrationExecutablePathUsed, b.IntegrationExecutablePathUsed, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.RequestedHubMac, b.RequestedHubMac, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.RequestedControllerLocation, b.RequestedControllerLocation, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.RequestedAcquisitionLocation, b.RequestedAcquisitionLocation, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.ControllerInstanceId, b.ControllerInstanceId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(a.AcquisitionInstanceId, b.AcquisitionInstanceId, StringComparison.OrdinalIgnoreCase);
        }

        private void PublishHardwareInspection(HardwareStatusSnapshot snapshot, bool silent)
        {
            if (snapshot == null) return;
            if (!snapshot.Ready && homeConnectionRetryStep > 0)
                homeConnectionNotBeforeUtc = HomeConnectionUtc.AddSeconds(homeConnectionRetryStep == 1 ? 2 : homeConnectionRetryStep == 2 ? 4 : 15);
            if (snapshot.Ready && snapshot.HasLiveIdentityReadback) { homeConnectionRetryStep = 0; homeConnectionNotBeforeUtc = DateTime.MinValue; }
            HardwareStatusSnapshot previous = state.Hardware;
            if (silent && SameConnectionHealth(previous, snapshot))
            {
                RememberValidatedDevice(snapshot);
                // One-second health checks must not rebuild tiles, relayout pages,
                // rematch saved settings or broadcast project state when unchanged.
                previous.CheckedUtc = snapshot.CheckedUtc;
                state.SetHardware(previous, false);
                return;
            }
            MatchDeviceAssetAndApply(snapshot);
            RememberValidatedDevice(snapshot);
            if (!verificationMode) HardwareDisconnectDiagnostics.RecordTransition(previous, snapshot);
            state.SetHardware(snapshot);
        }

        internal async Task<bool> TryDisconnectCurrentDevice()
        {
            if (!IsHardwareEnabled || hardwareDisconnectInProgress || activeModule != null || settingsDialogActive || pressureExitInProgress ||
                moduleHost!=null&&moduleHost.HasForegroundPressureDeviceOwner) return false;
            if (hardwareManuallyDisconnected && state.Hardware.StatusCode != "DisconnectFailed") return false;
            string reason;
            if (!CheckProjectReleaseIdle(out reason)) { DeviceConnectionNotice(reason); return false; }
            bool confirmed = ConfirmDeviceDisconnectForTests != null ? ConfirmDeviceDisconnectForTests() : !verificationMode &&
                MessageBox.Show(this, IsChinese
                    ? Localize("断开当前指定设备的通信连接？\r\n仅在所有试验已安全结束后操作。此按钮不是急停或高压断电开关。\r\n断开后暂停自动重连，点击“连接设备”可重新连接。")
                    : "Disconnect communications with the designated device?\r\nAll tests must be safely stopped first. This is not an emergency stop or high-voltage isolation.\r\nAutomatic reconnection will pause; use Connect device to reconnect.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            if (!confirmed || !CheckProjectReleaseIdle(out reason)) return false;
            hardwareDisconnectInProgress = true;
            hardwareManuallyDisconnected = true;
            navigationRequestSequence++; hardwareRefreshGeneration++; hardwareRefreshRequestEpoch++;
            pendingHardwareNavigation = null; hardwareRefreshInProgress = false; hardwareRefreshSilent = false; hardwareConnectionQueued = false;
            if (devicePollTimer != null) devicePollTimer.Stop();
            refreshDeviceButton.Enabled = false;
            ApplyState();
            try
            {
                if(moduleHost!=null)await moduleHost.CloseRetainedPressureSessionsAsync();
                HardwareStatusSnapshot snapshot;
                if (DisconnectDeviceForTests != null) snapshot = await DisconnectDeviceForTests();
                else
                {
                    HardwareStatusAdapter previous = hardwareAdapter;
                    hardwareAdapter = null;
                    if (previous != null) await Task.Run(() => previous.Dispose());
                    // Page retirement owns and verifies ART/Pico task cleanup.
                    // Never unbind DS600 here: the vendor binding must survive
                    // ordinary disconnect/reconnect. Re-check after the await.
                    if (activeModule != null || moduleHost != null &&
                        (moduleHost.HasPressureDeviceOwner || moduleHost.HasExternalCalibrationProcess) ||
                        !CheckProjectReleaseIdle(out reason))
                        throw new InvalidOperationException("设备任务尚未释放，未确认断开；请等待当前操作安全结束。");
                    snapshot = LocalCommunicationDisconnect(state.Hardware);
                    validatedDeviceSession = null;
                    hardwareAdapter = new HardwareStatusAdapter(settings);
                }
                if (snapshot == null) throw new InvalidOperationException("No disconnect verification was returned.");
                bool success = !snapshot.Connected && String.Equals(snapshot.StatusCode, "Disconnected", StringComparison.Ordinal);
                if (!success) snapshot.StatusCode = "DisconnectFailed";
                state.SetHardware(snapshot);
                if (!success) {
                    new Diagnostics().Error("DeviceDisconnect", snapshot.Message);
                    DeviceConnectionNotice(IsChinese
                        ? "设备断开未完成。请确认试验已结束并检查设备后重试；当前不能开始实机试验。"
                        : "Disconnection did not complete. Confirm the test has ended and check the device before retrying. Live tests are unavailable.");
                }
                return success;
            }
            catch (Exception error)
            {
                var failure = state.Hardware; failure.StatusCode = "DisconnectFailed"; failure.Message = error.Message;
                failure.CheckedUtc = DateTime.UtcNow; state.SetHardware(failure);
                new Diagnostics().Error("DeviceDisconnect", "Device disconnection was not confirmed.", error);
                DeviceConnectionNotice(IsChinese ? "设备断开未完成。请按现场规程确认设备状态后重试；如仍失败，请联系维护人员。"
                    : "Disconnection did not complete. Verify the equipment under site procedures before retrying. If it still fails, contact support.");
                return false;
            }
            finally
            {
                hardwareDisconnectInProgress = false;
                if (!IsDisposed) { refreshDeviceButton.Enabled = true; ApplyState(); }
            }
        }

        private void DeviceConnectionNotice(string message)
        {
            if (DeviceConnectionNoticeForTests != null) DeviceConnectionNoticeForTests(message);
            else if (!verificationMode && !IsDisposed) MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        internal static HardwareStatusSnapshot LocalCommunicationDisconnect(HardwareStatusSnapshot current)
        {
            var result = current == null ? new HardwareStatusSnapshot() : current.Clone();
            result.Connected = false;
            result.IdentityValid = false;
            result.FromLiveIntegrationProcess = false;
            result.ConnectionReadbackVerified = false;
            result.IntegrationRequestMode = String.Empty;
            result.StatusCode = "Disconnected";
            result.Message = "本软件通信会话已断开，驱动绑定保留；不代表试品已放电或接地。";
            result.Diagnostic = "Local session detached after owner-release checks; no DS600 disconnect/unbind request.";
            result.CheckedUtc = DateTime.UtcNow;
            return result;
        }

        private void HandleLiveConnectionLoss(Control page)
        {
            if (page == null || page.IsDisposed || page == notifiedDisconnectedModule) return;
            // A preparation failure is reported by its awaited owner, with the
            // acquisition/calibration cause. Do not race it with another modal.
            if (pressureExitInProgress) return;
            if (String.Equals(page.Tag as string, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase) &&
                !pressureInputPreparationTask.IsCompleted) return;
            notifiedDisconnectedModule = page;
            // Do not claim ShowHome succeeded: an acquisition/unsaved-data guard
            // may correctly keep the real page open. Never swap its data to SIM.
            bool requested = false;
            try
            {
                MethodInfo cancel = page.GetType().GetMethod("RequestHardwareStop", Type.EmptyTypes);
                if (cancel != null && cancel.ReturnType == typeof(bool)) requested = (bool)cancel.Invoke(page, null);
            }
            catch { }
            DeviceConnectionNotice(LiveConnectionLossNotice(state.Hardware.StatusCode,requested,IsChinese));
        }
        internal static string LiveConnectionLossNotice(string status,bool stopRequested,bool chinese)
        {
            if(status=="SessionEndedAfterFailedRelease")return chinese?
                "本轮采集未完成；原控制会话已主动结束，需要重新连接。原始数据保留，不自动继续。已核验软件资源清理，不代表试品已放电；请按现场规程安全放电。":
                "The capture did not complete and its control session has ended. Reconnect explicitly; raw data are retained and no round restarts automatically. Software cleanup was verified, not physical discharge; follow the site discharge procedure.";
            if(status=="StopReleaseUnconfirmed")return chinese?
                "本轮停止及资源释放尚未核验；工作进程仍在运行，保留当前会话等待处理。当前页面与原始数据保留，不能计为完成或自动继续，请按现场规程安全处置。":
                "Round stop and resource release are not yet verified. The worker is still running; retain this session for resolution. The page and raw data remain; do not treat this as completion or restart automatically. Follow site safety procedures.";
            if(status=="WorkerSessionExited")return chinese?
                "控制工作进程已退出，清理状态仍待核验。原始数据保留，不自动恢复；请核对日志及现场状态后重新连接。":
                "The control worker has exited; cleanup is not yet verified. Raw data are retained and output is not restored. Check the logs and site state before reconnecting.";
            return chinese?
                "实机连接状态未通过。"+(stopRequested?"已请求停止采集，请等待设备释放。":"请核对设备并安全结束当前操作。")+"当前页面与已采集数据保留，不会自动切成仿真；返回主页后可重新选择仿真。":
                "Live connection validation failed. "+(stopRequested?"Acquisition stop was requested; wait for release. ":"Check the device and safely end the operation. ")+"The page and acquired data are retained; no simulated data will be substituted.";
        }

        private async void DispatchDeviceSetupAction(DeviceSetupAction action)
        {
            switch (action)
            {
                case DeviceSetupAction.HardwareSettings: OpenSettings(SettingsSection.SystemAdmin); break;
                case DeviceSetupAction.DeviceParameters: await EditDeviceParameters(); break;
                case DeviceSetupAction.RecheckConnection: ReconnectCurrentDevice(); break;
                case DeviceSetupAction.ProjectManagement: OpenModule(ModuleHost.ProjectManagement); break;
                case DeviceSetupAction.FirstBinding: BeginFirstHardwareBinding(); break;
            }
        }
    }
}
