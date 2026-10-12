using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        [Browsable(false)] public string CableStructureEditStatus { get; private set; } = "";
        bool cableJointDialogOpen;
        string jointRefreshProject="",jointRefreshHash="";
        public event EventHandler CableStructureChanged;
        public bool RefreshCableStructure()
        {
            if(IsDisposed||Disposing)return false;
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(RefreshCableStructure));
            if(IsAnalysisRunning||cableJointDialogOpen)return false;
            if(String.IsNullOrWhiteSpace(projectPath))return true;
            try
            {
                var snapshot=CableJointStore.Read(projectPath);string hash=CableJointStore.Hash(snapshot.Original);
                if(String.Equals(jointRefreshProject,projectPath,StringComparison.OrdinalIgnoreCase)&&hash==jointRefreshHash)return true;
                InvalidateCableStructureAnalysis();
                jointRefreshProject=projectPath;jointRefreshHash=hash;
                CableStructureEditStatus=L("Cable structure changed in another page. Run analysis again.","其他页面已修改接头结构，请重新分析。");
                AnalysisStatus=CableStructureEditStatus;SetReviewOperationStatus(CableStructureEditStatus);return true;
            }
            catch(Exception ex){if(IsFatalHostException(ex))throw;LastError=ex.Message;SetReviewOperationStatus(ex.Message);return false;}
        }
        void InitializeCableJointEditing()
        {
            spectrum.ConfigureCableJointAction(ShowAddCableJoint, () => !IsAnalysisRunning && !cableJointDialogOpen &&
                !string.IsNullOrWhiteSpace(projectPath));
        }
        void ShowAddCableJoint(double displayedPositionM)
        {
            if (IsAnalysisRunning || cableJointDialogOpen) return;
            cableJointDialogOpen = true;
            try
            {
                var snapshot = CableJointStore.Read(projectPath);
                int activePhase = spectrum.PhaseFilterIndex;
                if (activePhase < 0)
                {
                    int[] visible = Enumerable.Range(1, 3).Where(p => spectrum.PhaseVisibility[p - 1]).ToArray();
                    if (visible.Length == 1) activePhase = visible[0];
                }
                string preferred = activePhase >= 1 && activePhase <= 3 ? ((char)('A' + activePhase - 1)).ToString() : "ALL";
                var owner = FindForm();
                // The shared dialog loads this assembly's embedded XIONGXIN
                // icon and owns its lifetime; no second dialog is created.
                using (var form = new CableJointDialog(snapshot, displayedPositionM, preferred, LanguageIndex, null))
                {
                    string dialogProject = projectPath;
                    form.ConfigureCommit((plan, operation) => {
                        if(IsDisposed || !String.Equals(dialogProject,projectPath,StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(L("Project changed. Reopen joint management.","项目已变化，请重新打开接头管理。"));
                        ApplySharedCableJoint(plan);
                    });
                    form.AdditionalWarning = L("This changes the project's cable structure. Current analysis and report attachments will be cleared and must be recalculated. Saved analyses remain historical data. Raw acquisition files are unchanged; a pre-edit cable backup is retained.",
                        "此操作将修改项目的实际电缆结构。当前分析结果和报告附件将清空，必须重新分析；已保存的分析保留为历史数据。原始采集文件不变，并保留修改前的电缆结构备份。");
                    form.ConfigurePositionResolver(phases =>
                    {
                        var blocks = snapshot.Blocks.Where(b => snapshot.Mode == CableJointMode.SharedThreeCore || phases.Contains(b.Phase)).ToArray();
                        if (ReversePositionDisplay && blocks.Select(b => b.Length).Distinct().Count() > 1)
                            throw new InvalidOperationException(L("Selected cables have different lengths in reversed view. Edit the position field to confirm one physical distance from the project start for all selected cables.",
                                "反向显示下，所选电缆长度不同。请手动编辑位置，明确所有选中结构距项目始端的实际距离，再确认添加。"));
                        return ReviewCableJointStore.ProjectPosition(displayedPositionM, blocks.Min(b => b.Length), ConnectingCableLengthM, ReversePositionDisplay);
                    });
                    form.ShowDialog(owner);
                }
            }
            catch (Exception ex)
            {
                if (IsFatalHostException(ex)) throw;
                LastError=ex.Message;
                string message=CustomerFailure("CableStructureEditFailed",ex.ToString(),"Cable structure could not be updated. Reopen the project and check the joint information before retrying.","无法更新电缆结构。请重新打开项目并检查接头信息后重试。");
                SetReviewOperationStatus(message);
                MessageBox.Show(FindForm(), message, L("Cable structure", "电缆结构"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { cableJointDialogOpen = false; }
        }
        internal void ApplyConfirmedCableJoint(ReviewCableJointStore.Plan plan)
        {
            if (IsAnalysisRunning) throw new InvalidOperationException(L("Wait for analysis to finish.", "请等待当前分析结束。"));
            string backup = ReviewCableJointStore.Commit(plan);
            FinishCableJointCommit(backup, plan.Phase, plan.PositionM);
        }
        internal void ApplySharedCableJoint(CableJointPlan plan)
        {
            if (IsAnalysisRunning) throw new InvalidOperationException(L("Wait for analysis to finish.", "请等待当前分析结束。"));
            var committed = CableJointStore.CommitWithResult(plan);
            FinishCableJointCommit(committed.BackupPath, String.Join(",", plan.Phases), plan.PositionM);
            if (!String.IsNullOrEmpty(committed.Warning))
            {
                CableStructureEditStatus += Environment.NewLine + committed.Warning;
                AnalysisStatus = CableStructureEditStatus; SetReviewOperationStatus(CableStructureEditStatus);
                LastError = committed.Warning;
            }
        }
        void FinishCableJointCommit(string backup, string phases, double positionM)
        {
            // File replacement has committed. Never leave pre-edit results valid,
            // even if a secondary view refresh fails afterwards.
            string refreshWarning = String.Empty;
            try { InvalidateCableStructureAnalysis(); }
            catch (Exception ex)
            {
                if (IsFatalHostException(ex)) throw;
                Enabled = false;
                refreshWarning = L("Cable structure was saved, but the view could not refresh. Reopen this project before further analysis. ",
                    "电缆结构已保存，但界面同步失败，请重新打开项目后分析。") + ex.Message;
                LastError = refreshWarning;
            }
            finally
            {
                try{jointRefreshHash=CableJointStore.Hash(System.IO.File.ReadAllBytes(System.IO.Path.Combine(projectPath,"CABLE.in")));jointRefreshProject=projectPath;}
                catch{jointRefreshHash="";}
                NotifyReviewHost(CableStructureChanged);
            }
            CableStructureEditStatus = L("Cable structure updated. Previous analysis is out of date; run analysis again. Backup: ",
                "电缆结构已更新，原分析已失效，请重新分析。备份：") + System.IO.Path.GetFileName(backup);
            if(refreshWarning.Length>0) CableStructureEditStatus += Environment.NewLine + refreshWarning;
            AnalysisStatus = CableStructureEditStatus; SetReviewOperationStatus(CableStructureEditStatus);
            RecordOperation("CableStructureChanged", "phase=" + phases + "; positionM=" + positionM.ToString("R", CultureInfo.InvariantCulture) + "; reanalysis required");
        }
        void InvalidateCableStructureAnalysis()
        {
            double attenuation = AnalysisAttenuationTolerancePercent, correlation = AnalysisCorrelationErrorPercent, threshold = AnalysisAutomaticThresholdMultiplier;
            int maximum = AnalysisMaximumPulseCount, algorithm = AnalysisAlgorithmIndex, suppression = AnalysisMultipleReflectionSuppressionCount;
            bool phase = AnalysisPhaseFilterEnabled, timing = AnalysisUseFirstArrivalTiming, reverse = ReversePositionDisplay, snap = pdmap.ApproximateCablePositions;
            bool[] quadrants = AnalysisQuadrants; double lead = ConnectingCableLengthM;
            if (!ClearAllAnalysis()) throw new InvalidOperationException(LastError);
            ResetAnalysisArchiveContext();
            pdmap.ReloadProjectMetadata(); spectrum.ReloadProjectMetadata();
            SetAnalysisSettings(attenuation, correlation, threshold, maximum, phase, quadrants);
            AnalysisAlgorithmIndex = algorithm; AnalysisMultipleReflectionSuppressionCount = suppression; AnalysisUseFirstArrivalTiming = timing;
            ConnectingCableLengthM = lead; ReversePositionDisplay = reverse; pdmap.ApproximateCablePositions = snap;
            RefreshApproximationProjection(); RefreshReviewUi();
            NotifyReviewHost(AnalysisArchiveChanged); NotifyReviewHost(ChartReportStatusChanged);
        }
    }
}
