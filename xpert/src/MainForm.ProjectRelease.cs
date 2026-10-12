using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private Button releaseProjectButton;
        private bool projectReleaseAuthorized, projectReleaseInProgress;
        internal Func<DialogResult> UnsavedProjectPrompt { get; set; }
        internal Action<string> ProjectReleaseError { get; set; }

        private void AddProjectReleaseButton(Panel card)
        {
            releaseProjectButton = AppTheme.Button(String.Empty, false);
            releaseProjectButton.Name = "ReleaseCurrentProject";
            releaseProjectButton.Font = AppTheme.Font(8.5f, FontStyle.Regular);
            releaseProjectButton.Click += delegate { TryReleaseCurrentProject(); };
            card.Controls.Add(releaseProjectButton);
        }

        private void UpdateProjectReleaseUi()
        {
            if (releaseProjectButton != null)
            {
                releaseProjectButton.Text = IsChinese ? Localize("释放项目") : "Release project";
                releaseProjectButton.AccessibleDescription = IsChinese ? Localize("仅卸载当前项目，不删除磁盘文件。") : "Unload the current project without deleting any files.";
                releaseProjectButton.Visible = activeModule == null && state.ProjectLoaded;
                releaseProjectButton.Enabled = state.ProjectLoaded && !projectReleaseInProgress;
                if (projectStatusCard != null) ArrangeProjectReleaseButton(projectStatusCard, projectStatus, projectDetail);
            }
            ModuleTile tile;
            if (tiles.TryGetValue(ModuleHost.ProjectCreation, out tile))
            {
                tile.Title = state.ProjectLoaded ? Localize(IsChinese ? "编辑项目" : "Edit project") : AppLanguages.Text("ProjectCreation", settings.LanguageCode);
                tile.Description = Localize(state.ProjectLoaded ? (IsChinese ? "编辑当前项目的信息与接头" : "Edit the current project and cable joints") : (IsChinese ? "建立电缆与检测任务档案" : "Create cable and test records"));
            }
        }

        private void ArrangeProjectReleaseButton(Panel card, Label primary, Label detail)
        {
            if (releaseProjectButton == null) return;
            releaseProjectButton.Visible = activeModule == null && state.ProjectLoaded;
            if (activeModule != null || !state.ProjectLoaded)
            {
                if (card != null && primary != null && detail != null)
                {
                    int inset = compactDisplayChrome ? 8 : ScaleLogical(8);
                    primary.Width = Math.Max(1, card.ClientSize.Width - primary.Left - inset);
                    detail.Width = Math.Max(1, card.ClientSize.Width - detail.Left - inset);
                }
                return;
            }
            ArrangeStatusCardAction(card, primary, detail, releaseProjectButton);
        }

        private static bool ReadReleaseFlag(Control control, string name)
        {
            PropertyInfo property = control.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) return false;
            if (property.PropertyType != typeof(bool) || !property.CanRead) throw new InvalidOperationException("Invalid release-state contract: " + name);
            return (bool)property.GetValue(control, null);
        }

        private bool CheckProjectReleaseIdle(out string reason)
        {
            reason = String.Empty;
            try
            {
                if (moduleHost.HasExternalCalibrationProcess) { reason = "请先关闭独立校准采集会话。 / Close the standalone acquisition session first."; return false; }
                if (offlineSimulationBridge != null && offlineSimulationBridge.IsBusy) { reason = "仿真采集或处理尚未结束，请完成后再释放项目。 / Simulation acquisition or processing is still in progress."; return false; }
                var checkedControls = new HashSet<Control>(moduleHost.LoadedProjectControls());
                if (activeModule != null) checkedControls.Add(activeModule);
                foreach (Control control in checkedControls)
                {
                    if (control == null || control.IsDisposed) continue;
                    foreach (string flag in new[] { "IsBusy", "IsAnalysisRunning", "IsRunning", "IsCapturing", "IsAcquiring", "HasActiveHardwareSession", "PressureNavigationLocked", "EvaluationBusy", "AutomaticEvaluationPending", "DischargeRecognitionBusy", "ActionPending", "OverRangeDecisionPending", "EnvironmentAcquisitionBusy" })
                        if (ReadReleaseFlag(control, flag)) { reason = "正在采集、加压或分析，请完成或安全停止后再释放项目。 / Acquisition, testing or analysis is in progress; finish or stop safely before release."; return false; }
                    PropertyInfo pressure = control.GetType().GetProperty("PressureStateCode");
                    if (pressure != null)
                    {
                        string code = Convert.ToString(pressure.GetValue(control, null)).ToUpperInvariant();
                        // FAULT is not proof that the hardware has stopped. Release
                        // must not send a stop command or assume the fault is safe.
                        if (code != "WAITING" && code != "COMPLETE") { reason = "加压尚未确认安全结束（含故障状态），不能释放项目。 / Pressure operation is not confirmed safely idle (including a fault)."; return false; }
                    }
                    if (ReadReleaseFlag(control, "IsEditingCalibration")) { reason = "请先保存或取消数据浏览中的校准编辑。 / Save or cancel the calibration edit in Data Browser first."; return false; }
                    if (control != activeModule && (ReadReleaseFlag(control, "HasUnsavedAnalysisChanges") || ReadReleaseFlag(control, "HasUnsavedCalibrationChanges") || ReadReleaseFlag(control, "IsDirty")))
                    { reason = "尚有后台页面未保存的修改，请返回该页面保存或取消后再释放。 / An inactive page has unsaved changes; save or cancel them first."; return false; }
                }
                return true;
            }
            catch (Exception) { reason = "无法确认当前任务已安全停止，已保留项目。 / Cannot verify that all operations are idle; the project has been kept open."; return false; }
        }

        private bool CanLeaveProjectEditor()
        {
            try
            {
            Control creator = activeModule;
            if (creator == null || !String.Equals(creator.Tag as string, ModuleHost.ProjectCreation, StringComparison.OrdinalIgnoreCase) || !ReadReleaseFlag(creator, "IsDirty")) return true;
            DialogResult choice = UnsavedProjectPrompt != null ? UnsavedProjectPrompt() : verificationMode ? DialogResult.Cancel : MessageBox.Show(this,
                IsChinese ? Localize("当前项目有未保存修改。是否先保存再离开？\r\n否：放弃本次编辑；取消：继续编辑。") : "Save the project edits before leaving?\r\nNo: discard these edits. Cancel: keep editing.",
                IsChinese ? Localize("未保存的项目修改") : "Unsaved project edits", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
            if (creator != activeModule || creator.IsDisposed) return false;
            if (choice == DialogResult.No) return true;
            if (choice != DialogResult.Yes) return false;
            var save = creator.GetType().GetMethod("SaveProject", Type.EmptyTypes);
            return save != null && save.ReturnType == typeof(bool) && (bool)save.Invoke(creator, null) && !ReadReleaseFlag(creator, "IsDirty");
            }
            catch (Exception error)
            {
                moduleHost.LogProjectReleaseError("Project editor could not confirm saved state; navigation cancelled.", error);
                ReportProjectReleaseError(IsChinese ? Localize("无法确认项目已保存，已保留当前页面。请检查保存结果后重试。") : "Could not confirm that the project was saved. The editor remains open; check the save result before retrying.");
                return false;
            }
        }

        internal bool TryReleaseCurrentProject()
        {
            return TryReleaseProjectContext(false);
        }

        private bool TryReleaseProjectContext(bool allowEmptyEditor)
        {
            bool newEditor = allowEmptyEditor && activeModule != null && String.Equals(activeModule.Tag as string, ModuleHost.ProjectCreation, StringComparison.OrdinalIgnoreCase);
            if (projectReleaseInProgress || pressureExitInProgress || moduleHost.HasPressureDeviceOwner || (!state.ProjectLoaded && !newEditor)) return false;
            projectReleaseInProgress = true;
            string project = state.ProjectPath;
            try
            {
                string reason;
                if (!CheckProjectReleaseIdle(out reason)) { ReportProjectReleaseError(reason); return false; }
                if (!CanLeaveActiveAnalysis() || !CanLeaveActiveCalibration() || !CanLeaveProjectEditor()) return false;
                if (!String.Equals(project, state.ProjectPath, StringComparison.OrdinalIgnoreCase)) return false;
                if (!CheckProjectReleaseIdle(out reason)) { ReportProjectReleaseError(reason); return false; }
                navigationRequestSequence++;
                pendingHardwareNavigation = null;
                moduleHost.PauseProjectRequestsForRelease();
                projectReleaseAuthorized = true;
                moduleHost.ReleaseLoadedProjectControls(activeModule);
                if (activeModule != null && activeModule.IsDisposed) activeModule = null;
                ShowHome();
                state.ClearProject();
                ApplyState();
                ApplyResponsiveChrome();
                return true;
            }
            catch (Exception error)
            {
                moduleHost.LogProjectReleaseError("Project release did not finish cleanly.", error);
                ReportProjectReleaseError(state.ProjectLoaded
                    ? "释放未完成，已保留项目状态；请检查保存结果后重试。 / Release did not finish; the project context was retained. Check the saved state and retry."
                    : "项目已卸载，但页面刷新遇到异常；磁盘文件未删除。 / The project was unloaded, but refreshing a page failed. No project files were deleted.");
                return false;
            }
            finally
            {
                // ClearProject sets the context before notifying subscribers. Even
                // if an unrelated observer throws, the independent viewer must
                // receive the final empty project instead of retaining stale data.
                if (projectReleaseAuthorized && !state.ProjectLoaded && competitionDefectLauncher != null)
                    try { competitionDefectLauncher.UpdateContext(settings, state); }
                    catch (Exception error) { moduleHost.LogProjectReleaseError("Could not publish the released project context.", error); }
                projectReleaseAuthorized = false; projectReleaseInProgress = false; UpdateProjectReleaseUi();
            }
        }

        private void ReportProjectReleaseError(string message)
        {
            if (ProjectReleaseError != null) ProjectReleaseError(message);
            else if (!verificationMode) MessageBox.Show(this, IsChinese ? Localize(message) : message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
