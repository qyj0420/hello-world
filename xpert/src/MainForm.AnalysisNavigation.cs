using System;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        // Test seams run the same navigation decision without opening a modal
        // window or sending commands to hardware.
        internal Func<DialogResult> UnsavedAnalysisPrompt { get; set; }
        internal Action<string> AnalysisNavigationError { get; set; }
        private bool analysisLeavePromptActive;

        private bool CanLeaveActiveAnalysis()
        {
            Control analysis = activeModule;
            if (analysis == null || !String.Equals(analysis.Tag as string,
                ModuleHost.DataAnalysis, StringComparison.OrdinalIgnoreCase)) return true;
            if (analysisLeavePromptActive) return false;
            try
            {
                PropertyInfo dirty = analysis.GetType().GetProperty("HasUnsavedAnalysisChanges");
                // Older, read-only module builds have no pending-save contract.
                if (dirty == null) return true;
                if (dirty.PropertyType != typeof(bool) || !dirty.CanRead)
                    throw new InvalidOperationException("Invalid analysis state contract.");
                if (!(bool)dirty.GetValue(analysis, null)) return true;

                analysisLeavePromptActive = true;
                MethodInfo leave = analysis.GetType().GetMethod("ConfirmLeaveAnalysis",BindingFlags.Public | BindingFlags.Instance,null,Type.EmptyTypes,null);
                if(UnsavedAnalysisPrompt==null&&leave!=null) {
                    if(leave.ReturnType!=typeof(bool))throw new InvalidOperationException("Invalid analysis exit contract.");
                    if(verificationMode)return false;
                    // The module owns the four-way choice, including explicit discard.
                    bool allowed=(bool)leave.Invoke(analysis,null);
                    return Object.ReferenceEquals(activeModule,analysis)&&!analysis.IsDisposed&&allowed;
                }
                string message = IsChinese
                    ? Localize("当前分析有未保存的内容，是否先保存？\r\n\r\n是：命名并保存后返回主页。\r\n否：不保存，离开当前分析。\r\n取消：继续留在分析界面。")
                    : "This analysis has unsaved changes. Save before leaving?\r\n\r\nYes: name and save the analysis, then leave.\r\nNo: leave without saving.\r\nCancel: keep working on this analysis.";
                DialogResult choice = UnsavedAnalysisPrompt != null ? UnsavedAnalysisPrompt()
                    : verificationMode ? DialogResult.Cancel
                    : MessageBox.Show(this, message,
                        IsChinese ? Localize("保存分析提示") : "Unsaved analysis",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button3);
                if (!Object.ReferenceEquals(activeModule, analysis) || analysis.IsDisposed) return false;
                if (choice == DialogResult.No) return true;
                if (choice != DialogResult.Yes) return false;
                MethodInfo save = analysis.GetType().GetMethod("ShowSaveAnalysisDialog",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (save == null || save.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Analysis save is unavailable.");
                // Cancelling the naming dialog or a failed disk write must never
                // dispose the page. A true result also requires a clean snapshot.
                return (bool)save.Invoke(analysis, null) &&
                    !(bool)dirty.GetValue(analysis, null);
            }
            catch (Exception)
            {
                string error = IsChinese
                    ? Localize("未能确认分析已保存，已保留当前界面。请使用“保存分析”检查保存结果后再返回主页。")
                    : "The analysis could not be confirmed as saved. This page has been kept open. Use Save analysis and check the result before returning home.";
                if (AnalysisNavigationError != null) AnalysisNavigationError(error);
                else if (!verificationMode)
                    MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { analysisLeavePromptActive = false; }
        }
    }
}
