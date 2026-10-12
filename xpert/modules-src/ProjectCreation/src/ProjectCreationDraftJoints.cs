using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        // A host may provide an explicit operator decision. Suppressing dialogs
        // by itself is not consent to discard a different insulation segment.
        [Browsable(false)] public Func<string, bool> ConfirmJointMaterialMerge { get; set; }

        string[] DraftJointPhases(string phase)
        {
            if (!String.IsNullOrWhiteSpace(loadedProjectPath))
            {
                var snapshot = CableJointStore.Read(loadedProjectPath);
                if (snapshot.Blocks.GroupBy(b => b.Phase, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    throw new InvalidOperationException("当前项目包含重复相别的独立结构段，普通草稿编辑不能展平或推断相别；请使用保留实际结构段的接头编辑流程。");
            }
            if (CableMode != ProjectModes.ThreeCore) return new[] { phase };
            var reference = joints.Where(j => j.Phase == "A相").OrderBy(j => j.Position).Select(j => j.Position).ToArray();
            foreach (string candidate in ProjectModes.Phases)
            {
                if (Math.Abs(PhaseLength(candidate) - CableLengthA) > .000001 ||
                    !joints.Where(j => j.Phase == candidate).OrderBy(j => j.Position).Select(j => j.Position).SequenceEqual(reference))
                    throw new InvalidOperationException("一体三芯的现有相长度或接头位置不一致，不能推断共享编辑范围；请先核对原结构。");
            }
            return ProjectModes.Phases;
        }

        JointData[] DraftJointTargets(JointData selected)
        {
            return DraftJointPhases(selected.Phase).Select(p => joints.Single(j => j.Phase == p && Math.Abs(j.Position - selected.Position) < .000001)).ToArray();
        }

        bool ConfirmDraftMaterialMerge(JointData[] targets, bool moving)
        {
            var changed = targets.Where(j => JointMaterialBefore(j) != j.Material).ToArray();
            if (changed.Length == 0) return true;
            bool zh = Xiongxin.OWTS.Localization.UiLanguage.IsChinese(languageIndex);
            string message = (zh ? "原位置两侧绝缘材料不同。合并后保留前段材料，确认继续？" : "Insulation differs across the original joint. Merge by retaining the preceding material?") + Environment.NewLine +
                String.Join(Environment.NewLine, changed.Select(j => j.Phase + " · " + j.Name + " · " + JointConfirmationPosition(j.Position) + " m: " + JointMaterialBefore(j) + " + " + j.Material + " → " + JointMaterialBefore(j)));
            if (moving) message += Environment.NewLine + (zh ? "接头后的材料随接头移动；新位置其他区段不自动改材。" : "The joint carries its following material to the new position.");
            LastJointChangeConfirmation = message;
            if (ConfirmJointMaterialMerge != null) return ConfirmJointMaterialMerge(message);
            return ShowDialogMessages && MessageBox.Show(this, message, zh ? "确认材料合并" : "Confirm insulation merge", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }

        bool RemoveDraftJoint(JointData selected)
        {
            EnsureGeometryEditable();
            JointData[] targets = DraftJointTargets(selected);
            if (!ConfirmDraftMaterialMerge(targets, false)) { CompleteOperation("RemoveJoint", false, "未确认材料合并，接头未删除。"); return false; }
            foreach (JointData target in targets) joints.Remove(target);
            foreach (string phase in targets.Select(j => j.Phase).Distinct()) RenumberAutomaticJoints(phase);
            MarkDirty(); RefreshCableView(); CompleteOperation("RemoveJoint", true, ""); return true;
        }

        bool EditDraftJoint(JointData selected, double position, string attachment, string beforeMaterial, string afterMaterial)
        {
            EnsureGeometryEditable(); JointData[] targets = DraftJointTargets(selected);
            foreach (JointData target in targets)
                if (!IsFinite(position) || position <= 0 || position >= PhaseLength(target.Phase) || joints.Any(j => j != target && j.Phase == target.Phase && Math.Abs(j.Position - position) < .000001))
                    throw new ArgumentOutOfRangeException("position", "位置必须位于本相范围内且不能重复。");
            if (Math.Abs(position - selected.Position) > .0000001 && !ConfirmDraftMaterialMerge(targets, true))
            { CompleteOperation("EditJoint", false, "未确认材料合并，接头未修改。"); return false; }
            foreach (JointData target in targets)
            {
                target.Position = position;
                if (!String.IsNullOrWhiteSpace(attachment)) target.AttachmentType = attachment.Trim();
                if (!String.IsNullOrWhiteSpace(afterMaterial)) target.Material = afterMaterial.Trim();
                SetPrecedingSegmentMaterial(target, beforeMaterial);
                RenumberAutomaticJoints(target.Phase);
            }
            joints.Sort((a, b) => a.Phase == b.Phase ? a.Position.CompareTo(b.Position) : String.CompareOrdinal(a.Phase, b.Phase));
            MarkDirty(); RefreshCableView(); CompleteOperation("EditJoint", true, ""); return true;
        }
    }
}
