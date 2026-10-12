using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        bool detectionSelectionBusy;
        bool deviceManagerOpening;
        internal Func<Task> RunDeviceManagerForTests;
        internal Func<Action,Task> RunDeviceManagerWithStartForTests;
        internal Diagnostics DeviceManagerDiagnosticsForTests;
        internal Func<ManagedDeviceManagerForm,ManagedDeviceManagerAction> ShowManagedDeviceDialogForTests;
        internal Action<SettingsSection> OpenManagedDeviceSettingsForTests;
        object deviceManagerAttempt;
        readonly CancellationTokenSource detectionSelectionCancellation=new CancellationTokenSource();
        internal Func<CancellationToken,Task<DetectionDeviceList>> DiscoverDetectorsForTests;
        internal Func<DetectionDeviceList,DetectionDevice> SelectDetectorForTests;
        internal Func<DetectionDevice,CancellationToken,Task<bool>> ConnectDetectorForTests;

        internal static DetectionDevice UniqueAvailableDevice(DetectionDeviceList receipt,DateTime utc)
        {
            if(receipt==null || !receipt.IsFresh(utc)) return null;
            DetectionDevice selected=null;
            foreach(var device in receipt.Devices)
                if(receipt.CanSelect(device,utc)) { if(selected!=null) return null; selected=device; }
            return selected;
        }
        internal bool CanRefreshDetectionDevices()
        {
            string reason;
            return !IsDisposed && !Disposing && !deviceManagerOpening && !detectionSelectionBusy && !firstBindingInProgress && !settingsDialogActive && activeModule==null &&
                !hardwareRefreshInProgress && !hardwareDisconnectInProgress && !hardwareConnectionQueued && pendingHardwareNavigation==null &&
                !settings.SessionHardwareForcedOffline && !detectionSelectionCancellation.IsCancellationRequested && CheckProjectReleaseIdle(out reason);
        }
        private bool SelectionCanProceed()
        {
            string reason;
            return !IsDisposed && !Disposing && !detectionSelectionCancellation.IsCancellationRequested && !settings.SessionHardwareForcedOffline &&
                activeModule==null && !firstBindingInProgress && !hardwareRefreshInProgress && !hardwareDisconnectInProgress &&
                !hardwareConnectionQueued && pendingHardwareNavigation==null && CheckProjectReleaseIdle(out reason);
        }
        private async void RefreshDetectionDevices()
        { await SearchAndConnectDevices(false); }

        private async void ManageDetectionDevices()
        {
            if (IsDisposed || Disposing || deviceManagerOpening || firstBindingInProgress || settingsDialogActive || settingsWorkflowActive || activeModule != null ||
                hardwareRefreshInProgress || hardwareDisconnectInProgress || detectionSelectionBusy || hardwareConnectionQueued || pendingHardwareNavigation != null) return;
            string reason;
            if (!CheckProjectReleaseIdle(out reason)) { DeviceConnectionNotice(reason); return; }
            if (verificationMode && ShowManagedDeviceDialogForTests == null) return;
            ManagedDeviceManagerAction action = ManagedDeviceManagerAction.None;
            bool resumePoll = devicePollTimer != null && devicePollTimer.Enabled;
            deviceManagerOpening = true;
            if (devicePollTimer != null) devicePollTimer.Stop();
            try
            {
                // This window reads the existing snapshot only. Opening it does
                // not launch vendor code, enumerate devices, bind or write parameters.
                using (var dialog = new ManagedDeviceManagerForm(settings.Clone(),state.Hardware,settings.LanguageCode))
                {
                    if (verificationMode && ShowManagedDeviceDialogForTests != null) action = ShowManagedDeviceDialogForTests(dialog);
                    else { dialog.ShowDialog(this); action = dialog.RequestedAction; }
                }
            }
            catch (Exception error)
            {
                var log = verificationMode ? DeviceManagerDiagnosticsForTests : new Diagnostics();
                if (log != null) log.Error("ManagedDeviceManagerOpen","Host read-only device window failed; no vendor launch or binding was attempted.",error);
                reason = IsChinese ? "设备管理窗口未打开。请重试；如仍失败，请联系维护人员。"
                    : "The device window did not open. Retry. If the problem persists, contact support.";
            }
            finally
            {
                deviceManagerOpening = false;
                if (!IsDisposed && !Disposing)
                {
                    ApplyState();
                    if (resumePoll && devicePollTimer != null && IsHardwareEnabled && !hardwareManuallyDisconnected) devicePollTimer.Start();
                }
            }
            if (IsDisposed || Disposing) return;
            if (!String.IsNullOrEmpty(reason)) { DeviceConnectionNotice(reason); return; }
            // Explicit navigation is performed only after closing the modal
            // viewer. The existing actions retain their own ownership/gates.
            if (action == ManagedDeviceManagerAction.ConnectRefresh) await SearchAndConnectDevices(false);
            else if (action == ManagedDeviceManagerAction.Parameters)
            {
                if (verificationMode && OpenManagedDeviceSettingsForTests != null) OpenManagedDeviceSettingsForTests(SettingsSection.Device);
                else OpenSettings(SettingsSection.Device);
            }
        }

        // Retained maintenance path. The default Manage button no longer relies
        // on an unreviewed installed vendor executable, and this gate is unchanged.
        private async void ManageInstalledDetectionDevices()
        {
            if (deviceManagerOpening || firstBindingInProgress || settingsDialogActive || activeModule != null ||
                hardwareDisconnectInProgress || detectionSelectionBusy || pendingHardwareNavigation != null) return;
            string reason;
            if (!CheckProjectReleaseIdle(out reason)) { DeviceConnectionNotice(reason); return; }
            if (verificationMode && RunDeviceManagerForTests == null && RunDeviceManagerWithStartForTests == null) return;
            deviceManagerOpening = true;
            object attempt = new object(); deviceManagerAttempt = attempt;
            HardwareStatusSnapshot previousHardware = state.Hardware;
            Exception managerError = null;
            hardwareConnectionQueued = false;
            if (devicePollTimer != null) devicePollTimer.Stop();
            try
            {
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (hardwareRefreshInProgress)
                {
                    refreshDeviceButton.Text = IsChinese ? "等待当前检查结束…" : "Waiting for current check…";
                    await Task.Delay(100);
                    if (IsDisposed || Disposing) return;
                    if (wait.ElapsedMilliseconds > 45000) throw new TimeoutException("当前设备检查尚未结束，未并发打开绑定工具。");
                }
                refreshDeviceButton.Text = IsChinese ? "正在打开设备管理…" : "Opening device manager…";
                Action started = delegate {
                    Action publish = delegate {
                        if (IsDisposed || Disposing || !deviceManagerOpening || !Object.ReferenceEquals(deviceManagerAttempt,attempt)) return;
                        refreshDeviceButton.Text = IsChinese ? "设备管理已打开…" : "Device manager is open…";
                        state.SetHardware(new HardwareStatusSnapshot { HardwareEnabled = IsHardwareEnabled,
                            StatusCode = "DeviceManagement", Message = "驱动包设备管理中，试验暂停。", CheckedUtc = DateTime.UtcNow });
                        var log = verificationMode ? DeviceManagerDiagnosticsForTests : new Diagnostics();
                        if (log != null) log.Info("DeviceManagerOpen","Verified driver process start succeeded; binding/readiness not yet confirmed.");
                    };
                    if (InvokeRequired) { try { BeginInvoke(publish); } catch (InvalidOperationException) { } }
                    else publish();
                };
                await (RunDeviceManagerWithStartForTests != null ? RunDeviceManagerWithStartForTests(started) :
                    RunDeviceManagerForTests != null ? RunDeviceManagerForTests() : InstalledDeviceManager.RunAsync(started));
                if (IsDisposed || Disposing) return;
                deviceManagerOpening = false;
                deviceManagerAttempt = null;
                if (state.Hardware.StatusCode == "DeviceManagement") state.SetHardware(previousHardware);
                hardwareManuallyDisconnected = false;
                homeConnectionRetryStep = 0;
                await SearchAndConnectDevices(true);
            }
            catch (Exception error) {
                managerError = error;
                var log = verificationMode ? DeviceManagerDiagnosticsForTests : new Diagnostics();
                if (log != null) {
                    string detail = "Driver management failed; no binding/readiness claim. Restoring host UI before notification.";
                    foreach (string key in new[]{"ExecutablePath","ExpectedSHA256","ActualSHA256","FileVersion"})
                        if (error.Data.Contains(key)) detail += " " + key + "=" + error.Data[key] + ";";
                    log.Error("DeviceManagerOpen",detail,error);
                }
            }
            finally
            {
                deviceManagerOpening = false;
                if (Object.ReferenceEquals(deviceManagerAttempt,attempt)) deviceManagerAttempt = null;
                if (!IsDisposed && !Disposing) {
                    if (state.Hardware.StatusCode == "DeviceManagement") state.SetHardware(previousHardware);
                    refreshDeviceButton.Text = AppLanguages.Text("RefreshDevice", settings.LanguageCode);
                    ApplyState();
                    if (devicePollTimer != null && IsHardwareEnabled && !hardwareManuallyDisconnected) devicePollTimer.Start();
                }
            }
            if (managerError != null && !IsDisposed && !Disposing)
                DeviceConnectionNotice(IsChinese ? "设备管理未完成。请检查设备管理工具是否已关闭后重试；如仍失败，请联系维护人员。"
                    : "Device management did not complete. Check that the device manager has closed, then retry. If the problem persists, contact support.");
        }

        private async Task<bool> SearchAndConnectDevices(bool automaticSingle)
        {
            if(verificationMode && DiscoverDetectorsForTests==null) return false;
            if(!CanRefreshDetectionDevices())
            { DeviceConnectionNotice(IsChinese?"请先结束当前采集、检查或配置，再刷新设备。":"Finish acquisition, inspection or configuration before refreshing devices."); return false; }
            if(!verificationMode)
            {
                string gate=FirstHardwareProvisioning.DiscoveryGate();
                if(gate.Length>0) { DeviceConnectionNotice(gate); return false; }
            }
            CancellationToken token=detectionSelectionCancellation.Token;
            bool connected=false;
            detectionSelectionBusy=true; Enabled=false; UseWaitCursor=true;
            string refreshText=refreshDeviceButton.Text;
            refreshDeviceButton.Text=IsChinese?"正在搜索设备…":"Searching for devices…"; if(devicePollTimer!=null) devicePollTimer.Stop(); refreshDeviceButton.Enabled=false;
            try
            {
                bool repeat;
                do
                {
                    repeat=false;
                    var receipt=await (DiscoverDetectorsForTests!=null?DiscoverDetectorsForTests(token):HardwareDeviceDiscovery.DiscoverAsync(token));
                    if(!SelectionCanProceed()) return false;
                    if(receipt==null || !receipt.IsFresh(DateTime.UtcNow)) throw new InvalidOperationException("设备列表已过期或不完整，请重新刷新。 / Device list is stale or incomplete; refresh it.");
                    DetectionDevice selected=automaticSingle?UniqueAvailableDevice(receipt,DateTime.UtcNow):null;
                    if(selected==null && SelectDetectorForTests!=null) selected=SelectDetectorForTests(receipt);
                    else if(selected==null) using(var dialog=new DetectionDeviceSelectionForm(receipt,settings.LanguageCode,settings.Hardware.SelectedHubMac))
                    { Enabled=true; dialog.ShowDialog(this); Enabled=false; repeat=dialog.RefreshRequested; selected=dialog.SelectedDevice; }
                    if(repeat) continue;
                    if(selected==null) return false;
                    if(!receipt.CanSelect(selected,DateTime.UtcNow)) throw new InvalidOperationException("设备列表已过期，请刷新后重新选择。 / Device list expired; refresh and select again.");
                    refreshDeviceButton.Text=IsChinese?"正在连接设备…":"Connecting device…";
                    connected=verificationMode && ConnectDetectorForTests!=null
                        ? await ConnectDetectorForTests(selected,token)
                        : await ConnectSelectedDetectionDevice(selected,token);
                    return connected;
                } while(repeat && !IsDisposed);
            }
            catch(OperationCanceledException) { }
            catch(Exception error)
            {
                var log = verificationMode ? DeviceManagerDiagnosticsForTests : new Diagnostics();
                if (log != null) log.Error("DetectionDeviceSelection", "Device discovery or selected-device connection did not complete.", error);
                DeviceConnectionNotice(IsChinese ? "设备连接未完成。请检查连接与首次配置，刷新设备后重试；如仍失败，请联系维护人员。"
                    : "Device connection did not complete. Check the connection and initial setup, refresh the device list and retry. If the problem persists, contact support.");
            }
            finally
            {
                detectionSelectionBusy=false;
                if(!IsDisposed && !Disposing) { Enabled=true; UseWaitCursor=false; refreshDeviceButton.Text=refreshText; refreshDeviceButton.Enabled=true; ApplyState(); if(devicePollTimer!=null && IsHardwareEnabled && !hardwareManuallyDisconnected) devicePollTimer.Start(); }
                if(connected) CompleteDeviceConnectionSetup();
            }
            return false;
        }
        private async Task<bool> ConnectSelectedDetectionDevice(DetectionDevice selected,CancellationToken token)
        {
            if(!SelectionCanProceed()) throw new InvalidOperationException("当前任务未确认空闲，不能切换设备。 / Current operation is not safely idle.");
            // Re-discover before any mutation, not from the dialog's expired or cached list.
            var live=await (DiscoverDetectorsForTests!=null?DiscoverDetectorsForTests(token):HardwareDeviceDiscovery.DiscoverAsync(token));
            if(!SelectionCanProceed()) throw new InvalidOperationException("刷新期间任务状态已改变，未切换设备。 / Operation state changed during discovery; device was not switched.");
            if(live==null) throw new InvalidOperationException("未返回完整设备列表。 / No complete device list was returned.");
            DetectionDevice target=null; foreach(var device in live.Devices) if(device.Key==selected.Key) target=device;
            if(!live.CanSelect(target,DateTime.UtcNow) || target.PersistentBound!=selected.PersistentBound) throw new InvalidOperationException("设备结构或绑定已变化，请重新选择。 / Device architecture or binding changed; select again.");
            string digest=SettingsDigest(); AppSettings basis=settings.Clone();
            if(state.Hardware.Connected && !hardwareManuallyDisconnected &&
                (state.Hardware.HubMac!=target.Mac || basis.Hardware.ControllerLocation!=target.ControllerLocation || basis.Hardware.AcquisitionLocation!=target.AcquisitionLocation))
            {
                if(!await TryDisconnectCurrentDevice()) return false;
            }
            if(!SelectionCanProceed()) return false;
            settingsDialogActive=true; Enabled=false; UseWaitCursor=true;
            bool connectionAttempted=false, committed=false;
            try
            {
                AppSettings next; HardwareStatusSnapshot exact=null;
                if(!target.PersistentBound)
                {
                    throw new InvalidOperationException("设备尚未完成首次绑定。首次绑定仍须使用已审核兼容的振荡波驱动包；本软件的设备管理窗口不执行绑定。 / Initial binding requires a reviewed compatible oscillating-wave driver package; this device window does not perform binding.");
#if LEGACY_PRIVATE_BINDING
                    Enabled=true; UseWaitCursor=false;
                    if(MessageBox.Show(this,IsChinese?
                        "发现未绑定的局放检测设备。将打开首次绑定列表，请明确选择要绑定的设备组合；可能请求管理员权限并重载通信服务。其他设备采集必须已停止。是否继续？":
                        "An unbound PD detector was found. Open initial binding and explicitly select its component pair? Administrator approval and a communications-service reload may be required. Other device acquisitions must be stopped.",
                        Text,MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return false;
                    Enabled=false; UseWaitCursor=true;
                    // Existing binding UI owns explicit selection. Discovery alone never Applys.
                    var offline=basis.Clone(); offline.Hardware.Enabled=false;
                    var activation=await FirstHardwareProvisioning.RunAsync(offline);
                    if(IsDisposed) return false;
                    if(activation.Candidate==null || activation.Candidate.Mac!=target.Mac || activation.Candidate.ControllerLocation!=target.ControllerLocation ||
                        activation.Candidate.AcquisitionLocation!=target.AcquisitionLocation)
                        throw new InvalidOperationException("首次绑定中选择了另一组设备，未导入当前配置。绑定工具的更改可能已生效，请刷新后核对。 / Initial binding selected a different pair; no host settings were imported. The binding tool may have applied changes; refresh and review.");
                    next=FirstHardwareProvisioning.ApplyToClone(offline,activation);
                    AppSettings latest; string diagnostic;
                    if(!AppSettings.TryLoad(AppPaths.SettingsFile,out latest,out diagnostic) || !SameSelectionConfiguration(basis,latest))
                        throw new InvalidOperationException("绑定期间配置已改变；没有覆盖原配置。 / Configuration changed during binding; it was not overwritten.");
                    if(!next.TrySaveIfUnchanged(digest,false,()=>SelectionCanProceed() && SameSelectionConfiguration(basis,settings) && ProvisioningImportAdapter.IsCurrentActivation(activation,DateTime.UtcNow)))
                        throw new InvalidOperationException("配置已变化或绑定凭据已过期；没有保存。 / Settings changed or binding evidence expired; nothing was saved.");
#endif
                }
                else
                {
                    FirstHardwareProvisioning.VerifyExactConnector(); next=HardwareDeviceDiscovery.SelectionProbe(basis,target);
                    DateTime inspectionStartedUtc=DateTime.UtcNow;
                    using(var adapter=new HardwareStatusAdapter(next))
                    {
                        adapter.BindingImportInspection=true;
                        connectionAttempted=true;
                        // Explicit vendor binding import only; inspect the exact
                        // selected association without Connect or Apply commands.
                        exact=await Task.Run(()=>adapter.Refresh(true));
                    }
                    if(IsDisposed) return false;
                    FirstHardwareProvisioning.VerifyExactConnector();
                    if(!SelectionCanProceed() || !HardwareDeviceDiscovery.ExactSelection(target,exact,DateTime.UtcNow) || exact.CheckedUtc<inspectionStartedUtc ||
                        !HardwareDeviceDiscovery.SavedSerialsMatch(basis,target,exact))
                        throw new InvalidOperationException("所选设备的精确身份未通过，未改变当前配置。 / Selected device identity was not verified; current configuration was retained.");
                    next.Hardware.ControllerSerial=exact.ControllerSerial; next.Hardware.AcquisitionSerial=exact.AcquisitionSerial;
                    if(basis.Hardware.SelectedHubMac==target.Mac && basis.Hardware.ControllerLocation==target.ControllerLocation && basis.Hardware.AcquisitionLocation==target.AcquisitionLocation &&
                        !String.IsNullOrWhiteSpace(exact.ControllerSerial) && !String.IsNullOrWhiteSpace(exact.AcquisitionSerial) &&
                        basis.Hardware.ControllerSerial==exact.ControllerSerial && basis.Hardware.AcquisitionSerial==exact.AcquisitionSerial)
                    { next.DeviceParametersReviewedHash=basis.DeviceParametersReviewedHash; next.DeviceParametersReviewedIdentity=basis.DeviceParametersReviewedIdentity; next.DeviceParametersReviewedUtc=basis.DeviceParametersReviewedUtc; }
                    if(!next.TrySaveIfUnchanged(digest,false,()=>SelectionCanProceed() && SameSelectionConfiguration(basis,settings) &&
                        HardwareDeviceDiscovery.ExactSelection(target,exact,DateTime.UtcNow) && exact.CheckedUtc>=inspectionStartedUtc && HardwareDeviceDiscovery.SavedSerialsMatch(basis,target,exact)))
                        throw new InvalidOperationException("配置已变化或设备证据已过期；没有保存。 / Settings changed or device evidence expired; nothing was saved.");
                }
                // Commit is complete; subsequent UI failures must not claim it was undone.
                committed=true; settings=next; hardwareManuallyDisconnected=false; driverPromptShown=false;
                try { RecreateIntegrationServices(); ApplyLanguageAndBranding(); ApplyState(); }
                catch(Exception error)
                {
                    var log = verificationMode ? DeviceManagerDiagnosticsForTests : new Diagnostics();
                    if (log != null) log.Error("DetectionDeviceSelectionUi", "Device settings were committed, but the host interface could not refresh.", error);
                    DeviceConnectionNotice(IsChinese ? "设备配置已保存，但界面未刷新。请关闭并重新打开软件。"
                        : "Device settings were saved, but the interface did not refresh. Close and reopen the application.");
                    return false;
                }
                if(exact!=null) PublishHardwareInspection(exact,false);
            }
            catch
            {
                if(connectionAttempted && !committed && !IsDisposed)
                { hardwareManuallyDisconnected=true; state.SetHardware(new HardwareStatusSnapshot { HardwareEnabled=IsHardwareEnabled, StatusCode="SelectionUnverified", Message="所选设备身份未核验；自动连接暂停。 / Selected device was not verified; automatic connection paused.", CheckedUtc=DateTime.UtcNow }); }
                throw;
            }
            finally { settingsDialogActive=false; if(!IsDisposed) { Enabled=true; UseWaitCursor=false; } }
            return committed;
        }
        static bool SameSelectionConfiguration(AppSettings first,AppSettings second)
        { return first!=null && second!=null && first.Hardware.Enabled==second.Hardware.Enabled && DeviceSetupStatus.ParameterHash(first)==DeviceSetupStatus.ParameterHash(second); }
    }
}
