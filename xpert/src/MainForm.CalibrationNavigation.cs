using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        internal Func<DialogResult> UnsavedCalibrationPrompt { get; set; }
        internal Func<string, bool, DialogResult> MissingCalibrationPrompt { get; set; }
        internal Action CalibrationRequiredNavigation { get; set; }
        internal Action<string> CalibrationNavigationError { get; set; }
        private bool calibrationLeavePromptActive;
        private bool calibrationDeviceReleaseActive;
        private CancellationTokenSource calibrationNavigationCancellation;

        private void CancelPendingCalibrationNavigation()
        {
            var pending=calibrationNavigationCancellation;
            if(pending!=null)pending.Cancel();
        }

        private bool IsCurrentCalibrationNavigation(long request,ModuleHost owner,string project)
        {
            return request==navigationRequestSequence && !IsDisposed && !Disposing &&
                !settingsDialogActive && !settingsWorkflowActive && !hardwareDisconnectInProgress &&
                !pressureExitInProgress && Object.ReferenceEquals(owner,moduleHost) &&
                String.Equals(project,state.ProjectPath,StringComparison.OrdinalIgnoreCase);
        }

        private async void OpenLiveCalibrationControlAsync(long request)
        {
            var owner=moduleHost;
            string project=state.ProjectPath;
            if(owner==null || !IsCurrentCalibrationNavigation(request,owner,project))return;
            CancelPendingCalibrationNavigation();
            var cancellation=new CancellationTokenSource();
            calibrationNavigationCancellation=cancellation;
            bool entryAdmitted=false;
            try
            {
                await CalibrationNavigationHandoff.RunAsync(
                    ()=>IsCurrentCalibrationNavigation(request,owner,project),
                    owner.DrainEnvironmentForHardwareAsync,
                    async ()=>{
                        entryAdmitted=true;
                        Control page=OpenTestModuleControlCore(ModuleHost.CalibrationTest,request,false);
                        if(page==null || !IsCurrentCalibrationNavigation(request,owner,project) ||
                            !Object.ReferenceEquals(activeModule,page) || page.IsDisposed)return;
                        // Invoke on this UI continuation and retain the lease
                        // through the actual entry read/cleanup, not just mounting.
                        await (Task)RequireCalibrationEntryAsyncMethod(page).Invoke(page,null);
                    },cancellation.Token);
            }
            catch(OperationCanceledException) when(!entryAdmitted && (cancellation.IsCancellationRequested || !IsCurrentCalibrationNavigation(request,owner,project))) { }
            catch(Exception error)
            {
                new Diagnostics().Error("CalibrationEnvironmentHandoff",entryAdmitted?
                    "Calibration entry did not complete after environment release.":
                    "Calibration preparation did not acknowledge environment release; entry was not continued.",error);
                if(IsCurrentCalibrationNavigation(request,owner,project))
                    DeviceConnectionNotice(IsChinese?"校准准备未完成。请检查设备状态后重试；如仍失败，请联系维护人员。":
                        "Calibration preparation is incomplete. Check the equipment state and retry. If it still fails, contact support.");
            }
            finally
            {
                if(Object.ReferenceEquals(calibrationNavigationCancellation,cancellation))calibrationNavigationCancellation=null;
                cancellation.Dispose();
            }
        }

        private static MethodInfo RequireCalibrationEntryAsyncMethod(Control page)
        {
            MethodInfo method=page.GetType().GetMethod("NotifyCalibrationPageEnteredAsync",BindingFlags.Public|BindingFlags.Instance,null,Type.EmptyTypes,null);
            if(method==null || !typeof(Task).IsAssignableFrom(method.ReturnType))
                throw new InvalidOperationException("Calibration async entry contract is unavailable.");
            return method;
        }

        private bool CanLeaveActiveCalibration()
        {
            Control calibration = activeModule;
            if (calibration == null || !String.Equals(calibration.Tag as string,
                ModuleHost.CalibrationTest, StringComparison.OrdinalIgnoreCase)) return true;
            if (calibrationLeavePromptActive) return false;
            if (LateBound.ReadBoolean(calibration, false, "HasActiveHardwareSession"))
            {
                if(LateBound.ReadBoolean(calibration,false,"HasReusableHardwareSession") ||
                    LateBound.ReadBoolean(calibration,false,"CanReleaseHardwareSession"))
                {
                    if(!calibrationDeviceReleaseActive)ReleaseIdleCalibrationForNavigation(calibration);
                    return false;
                }
                if (!verificationMode) MessageBox.Show(this,
                    IsChinese ? "请先停止采集，并断开 Pico 与控制箱，然后再离开校准页。" :
                        "Stop acquisition and disconnect Pico and the control box before leaving Calibration.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            try
            {
                PropertyInfo chargeDirty = calibration.GetType().GetProperty("HasUnsavedChargeCalibrationChanges");
                PropertyInfo dirty = chargeDirty ?? calibration.GetType().GetProperty("HasUnsavedCalibrationChanges");
                if (dirty != null && (dirty.PropertyType != typeof(bool) || !dirty.CanRead))
                    throw new InvalidOperationException("Invalid calibration state contract.");
                bool changed = dirty == null || (bool)dirty.GetValue(calibration, null);
                PropertyInfo savedValid = calibration.GetType().GetProperty("IsCalibrationSavedValid");
                if (chargeDirty == null && !changed && LateBound.ReadBoolean(calibration, false, "HasCalibrationData"))
                    changed = savedValid == null || savedValid.PropertyType != typeof(bool) ||
                        !(bool)savedValid.GetValue(calibration, null);
                if (!changed) return true;
                calibrationLeavePromptActive = true;
                string message = BuildUnsavedCalibrationNavigationMessage(calibration);
                DialogResult choice = UnsavedCalibrationPrompt != null ? UnsavedCalibrationPrompt()
                    : verificationMode ? DialogResult.Cancel
                    : MessageBox.Show(this, message, IsChinese ? Localize("保存校准提示") : "Unsaved calibration",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (!Object.ReferenceEquals(activeModule, calibration) || calibration.IsDisposed) return false;
                if (choice == DialogResult.No)
                {
                    MethodInfo discard = calibration.GetType().GetMethod("DiscardCalibrationChanges",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (discard == null) throw new InvalidOperationException("Calibration discard is unavailable.");
                    try {discard.Invoke(calibration, null);}
                    catch(Exception discardError) {
                        // A reload failure cannot revoke the user's explicit
                        // discard choice. Existing archives remain on disk.
                        moduleHost.LogProjectReleaseError("Discarded calibration view could not reload existing archives.",discardError);
                    }
                    // Explicitly discarded edits must not be checked again as
                    // a prerequisite to navigation. Reloaded stale archives are
                    // still rejected separately by live-test readiness checks.
                    return true;
                }
                if (choice != DialogResult.Yes) return false;
                MethodInfo save = calibration.GetType().GetMethod("SaveCalibrationFile",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (save == null || save.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Calibration save is unavailable.");
                return (bool)save.Invoke(calibration, null) && dirty != null &&
                    !(bool)dirty.GetValue(calibration, null) &&
                    (chargeDirty != null || (savedValid != null && savedValid.PropertyType == typeof(bool) &&
                    (bool)savedValid.GetValue(calibration, null)));
            }
            catch (Exception)
            {
                string message = IsChinese
                    ? Localize("校准文件未能确认保存成功，已保留校准界面。请检查保存提示；仿真校准不能作为实机校准。")
                    : "The calibration file could not be confirmed as saved. The page has been kept open. Check the save result; simulated calibration is not valid for live testing.";
                if (CalibrationNavigationError != null) CalibrationNavigationError(message);
                else if (!verificationMode) MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { calibrationLeavePromptActive = false; }
        }

        private string BuildUnsavedCalibrationNavigationMessage(Control calibration)
        {
            string details=LateBound.ReadString(calibration,"UnsavedCalibrationSummary");
            string introduction=String.IsNullOrWhiteSpace(details)
                ? (IsChinese?Localize("当前校准有未保存的数据或修改。"):
                    "Calibration data or changes have not been saved.")
                : (IsChinese?Localize("以下校准项尚未保存或需要重新核验：\r\n"):
                    "The following calibration items need saving or verification:\r\n")+details;
            return introduction+(IsChinese
                ? Localize("\r\n\r\n是否保存校准文件后返回主页？\r\n是：保存成功后返回。\r\n否：放弃本次未保存的修改（已保存文件保留）。\r\n取消：留在校准界面，核对上述相别与档位。")
                : "\r\n\r\nSave the calibration file before returning home?\r\nYes: save, then return.\r\nNo: discard unsaved changes (saved files are retained).\r\nCancel: stay on Calibration and review the listed phases and levels.");
        }

        private async void ReleaseIdleCalibrationForNavigation(Control calibration)
        {
            calibrationDeviceReleaseActive=true;
            try
            {
                var release=calibration.GetType().GetMethod("ReleaseHardwareSessionAsync",Type.EmptyTypes);
                if(release==null)throw new InvalidOperationException("校准设备释放接口不可用。");
                bool released=await (System.Threading.Tasks.Task<bool>)release.Invoke(calibration,null);
                if(!released || LateBound.ReadBoolean(calibration,true,"HasActiveHardwareSession"))
                    throw new InvalidOperationException("设备仍被占用，请等待操作完成后重试释放。");
                if(!IsDisposed && Object.ReferenceEquals(activeModule,calibration))ShowHome();
            }
            catch(Exception error)
            {
                moduleHost.LogProjectReleaseError("Calibration navigation release did not complete.",error);
                if(!IsDisposed)DeviceConnectionNotice(IsChinese ? "校准设备尚未释放，已保留当前页面。请重试释放。" : "Calibration devices have not been released. The page remains open; retry release.");
            }
            finally {calibrationDeviceReleaseActive=false;}
        }

        // This is an entry warning, not permission to energize hardware. The
        // preview's separate live-hardware guard remains in force.
        private bool CheckCalibrationBeforeTest(string id, Control testControl, bool simulationMode)
        {
            if (String.Equals(id, ModuleHost.CalibrationTest, StringComparison.OrdinalIgnoreCase)) return true;
            string phase = LateBound.ReadString(testControl, "SelectedPhaseCode", "Phase");
            if (String.IsNullOrWhiteSpace(phase)) phase = "A";
            string reason;
            Control calibration;
            string error;
            try
            {
                if (!moduleHost.TryCreateControl(ModuleHost.CalibrationTest, out calibration, out error) || calibration == null)
                    throw new InvalidOperationException("Calibration module unavailable.");
                PropertyInfo simulation = calibration.GetType().GetProperty("SimulationMode");
                if (simulation != null && simulation.PropertyType == typeof(bool) && simulation.CanWrite)
                    simulation.SetValue(calibration, simulationMode, null);
                MethodInfo ready = calibration.GetType().GetMethod("GetCalibrationReadiness",
                    new[] { typeof(string), typeof(bool), typeof(bool) });
                if (ready == null || ready.ReturnType != typeof(string))
                    throw new InvalidOperationException("Calibration readiness contract unavailable.");
                reason = (string)ready.Invoke(calibration, new object[] { phase, simulationMode, settings.TaskMinus3.UseFirstArrivalTiming });
                if (reason == String.Empty) return true;
                if (String.IsNullOrWhiteSpace(reason))
                    throw new InvalidOperationException("Calibration readiness was not determined.");
            }
            catch (Exception)
            {
                reason = IsChinese ? Localize("无法核实适用的校准文件，请先进入校准页面检查。")
                    : "Applicable calibration files could not be verified. Open Calibration to check them first.";
            }
            if(!simulationMode) {
                bool handled;bool usable=TryLegacyCalibrationBeforeTest(phase,out handled,()=>!testControl.IsDisposed&&LateBound.ReadString(testControl,"SelectedPhaseCode","Phase")==phase);
                if(handled)return usable;
                // Detailed rejections belong to maintenance diagnostics. A
                // customer only needs the required action, not serials, format
                // names, timing implementation or exception details.
                reason=IsChinese?Localize("请完成校准并保存后再试。"):
                    "Complete and save calibration, then try again.";
            }
            string message = (IsChinese
                ? Localize("当前项目的 ") + phase + Localize(" 相未找到适用的已保存校准数据。\r\n")
                : "No applicable saved calibration was found for phase " + phase + " in this project.\r\n") + reason;
            message += simulationMode
                ? (IsChinese ? Localize("\r\n\r\n是：先进入校准。\r\n否：仅查看仿真演示（不代表校准合格，不能实机加压）。\r\n取消：不进入测试。")
                    : "\r\n\r\nYes: open Calibration first.\r\nNo: view the simulation only (not calibration approval or permission for live voltage).\r\nCancel: do not open the test.")
                : (IsChinese ? Localize("\r\n\r\n是否先进入校准？未完成适用校准不能开始实机试验。")
                    : "\r\n\r\nOpen Calibration first? A live test requires applicable calibration.");
            DialogResult choice = MissingCalibrationPrompt != null ? MissingCalibrationPrompt(message, simulationMode)
                : verificationMode ? DialogResult.Cancel
                : MessageBox.Show(this, message, IsChinese ? Localize("请先校准") : "Calibration required",
                    simulationMode ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, simulationMode ? MessageBoxDefaultButton.Button3 : MessageBoxDefaultButton.Button2);
            if (choice == DialogResult.Yes)
            {
                if (pressureStandalone)
                {
                    ShowRequirement(IsChinese ? "请在校准程序中完成并保存校准，再重新加载此项目。" :
                        "Complete and save calibration in the calibration application, then reload this project.");
                    return false;
                }
                if (CalibrationRequiredNavigation != null) CalibrationRequiredNavigation();
                else OpenModule(ModuleHost.CalibrationTest);
            }
            return simulationMode && choice == DialogResult.No;
        }
    }
}
