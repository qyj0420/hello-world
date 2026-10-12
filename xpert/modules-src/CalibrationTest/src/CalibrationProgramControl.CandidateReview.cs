using System;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private bool ReviewJointCandidate(Form owner, CalibrationJoint candidate, string project, int phase, string level,
            bool simulation, ref string expectedCableHash, string expectedItemHash)
        {
            RequireJointReviewContext(project, phase, level, simulation);
            RequirePendingJointCandidate(candidate, phase);
            var snapshot = JointSnapshotForCurrentContext(phase);
            if (!simulation && (!String.Equals(CableJointStore.Hash(snapshot.Original), expectedCableHash, StringComparison.Ordinal) ||
                !String.Equals(snapshot.ItemHash, expectedItemHash, StringComparison.Ordinal)))
                throw new InvalidOperationException(CalibrationText("项目结构已变化，请重新打开接头窗口核对。", "Project structure changed. Reopen the joint review window."));
            jointDialogOpen = true;
            try {
                using (var icon = CalibrationBranding.LoadIcon())
                using (var dialog = new CableJointDialog(snapshot, candidate.DistanceMeters, PhaseCodeFromIndex(phase), LanguageIndex, icon)) {
                    ConfigureJointCustomerDiagnostics(dialog);
                    dialog.ConfigureCandidateReview(candidate.DistanceMeters, phase < 0 && snapshot.Mode != CableJointMode.SharedThreeCore && snapshot.PhaseChoices.Length > 1);
                    dialog.AdditionalWarning = CalibrationText("请核对候选的位置、附件类型和前后绝缘，再确认此接头。取消不会写入此候选。", "Review the candidate position, attachment type and before/after insulation before confirming this joint. Cancel leaves it pending.") +
                        (simulation ? CalibrationText(" 仿真仅更新显示，不写入项目。", " Simulation updates display only; project files are unchanged.") : "");
                    dialog.ConfigureCommit((plan, operation) => CommitReviewedJointCandidate(candidate, project, phase, level, simulation, plan, operation));
                    if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                    if (!simulation) expectedCableHash = CableJointStore.Hash(dialog.Plan.Replacement);
                    if (!String.IsNullOrEmpty(LastJointCommitWarning)) ShowSavedJointCustomerWarning(owner);
                    return true;
                }
            }
            finally { jointDialogOpen = false; }
        }

        private void RequirePendingJointCandidate(CalibrationJoint candidate, int phase)
        {
            lock (stateLock) {
                if (candidate == null || !candidate.AutomaticallyDetected || candidate.Confirmed ||
                    !GetPhaseStateNoLock(phase).Joints.Any(j => Object.ReferenceEquals(j, candidate)))
                    throw new InvalidOperationException(CalibrationText("接头候选已变化，请重新打开窗口核对。", "Joint candidate changed. Reopen the review window."));
            }
        }

        private void CommitReviewedJointCandidate(CalibrationJoint candidate, string project, int phase, string level,
            bool simulation, CableJointPlan plan, string operation)
        {
            RequireJointReviewContext(project, phase, level, simulation);
            RequirePendingJointCandidate(candidate, phase);
            if (plan == null || operation != "AddJoint" || plan.OperationCode != "AddJoint")
                throw new InvalidOperationException(CalibrationText("候选确认只新增已核对的接头，不修改已有接头。", "Candidate review adds the reviewed joint only; it does not edit existing joints."));
            CommitSharedJointPlan(plan);
            // Only after the transactional commit and marker publication have
            // succeeded. A moved candidate is removed by identity, not proximity.
            lock (stateLock) {
                GetPhaseStateNoLock(phase).Joints.RemoveAll(j => Object.ReferenceEquals(j, candidate));
                candidate.Confirmed = true;
                System.Threading.Interlocked.Increment(ref jointChangedSequence);
            }
            Invalidate();
        }
    }
}
