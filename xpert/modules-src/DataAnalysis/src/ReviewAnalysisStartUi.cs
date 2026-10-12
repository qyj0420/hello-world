using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        [Browsable(false)]
        public string AnalysisStartBlockReason
        {
            get
            {
                if (analysisBusy) return L("Analysis is already running. Wait for it to finish, or cancel it in the progress window.", "分析正在运行，请等待完成，或在进度窗口中取消。");
                if (IntPtr.Size != 8) return L("Automatic analysis requires the 64-bit application.", "自动分析需要使用 64 位软件。");
                if (analysisTree == null || analysisTree.GetSelectedDataPaths().Length == 0)
                    return L("Select pending LOC files in the tree first. Selecting a row alone does not check it.", "请先在树形中勾选待分析的 LOC 文件；只选中行不等于勾选文件。");
                if (AiLearningMode) return L("AI learning mode is ON. Automatic and batch analysis are paused while recording manual review.", "AI 学习模式已开启：此模式记录人工复核，自动与批量分析已暂停。");
                return "";
            }
        }

        // The UI may explain a blocked action, but it must never silently disable
        // learning mode. The analysis engine's independent guards remain intact.
        bool PrepareSelectedAnalysisFromUi(Func<string, bool> confirmLeaveLearning, out string reason)
        {
            reason = AnalysisStartBlockReason;
            if (AiLearningMode && !analysisBusy && IntPtr.Size == 8 && analysisTree != null && analysisTree.GetSelectedDataPaths().Length > 0)
            {
                if (confirmLeaveLearning == null || !confirmLeaveLearning(reason + Environment.NewLine + Environment.NewLine +
                    L("Turn OFF AI learning mode and analyse the checked files now? Choosing No keeps learning mode unchanged.",
                      "是否关闭 AI 学习模式，并开始分析已勾选的文件？选择“否”将保持学习模式不变。"))) return false;
                // Recheck after the modal prompt: a callback may have changed the
                // selection or started another operation while it was displayed.
                if (analysisBusy || analysisTree.GetSelectedDataPaths().Length == 0) { reason = AnalysisStartBlockReason; return false; }
                AiLearningMode = false;
                reason = AnalysisStartBlockReason;
            }
            return reason.Length == 0;
        }

        void RequestSelectedAnalysisFromUi(bool automaticAcceptance)
        {
            bool asked = false;
            string reason;
            bool ready = PrepareSelectedAnalysisFromUi(message =>
            {
                asked = true;
                return MessageBox.Show(FindForm(), message, L("Start analysis", "开始分析"), MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }, out reason);
            if (!ready)
            {
                SetReviewOperationStatus(reason);
                if (!asked) MessageBox.Show(FindForm(), reason, L("Analysis not started", "未开始分析"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!StartSelectedAnalysis(automaticAcceptance))
            {
                reason = String.IsNullOrWhiteSpace(LastError)
                    ? L("No analysable LOC files were found in the checked selection. Review the selected records and their diagnostics.", "已勾选记录中没有可分析的 LOC 文件，请检查记录及其诊断信息。")
                    : CustomerFailure("AnalysisStartRejected",LastError,"Analysis could not start. Check the selected records and settings, then retry.","无法开始分析。请检查所选记录及分析设置后重试。");
                SetReviewOperationStatus(reason);
                MessageBox.Show(FindForm(), reason, L("Analysis not started", "未开始分析"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
