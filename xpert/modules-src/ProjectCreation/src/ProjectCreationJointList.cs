using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        string JointBlockKey(JointData joint) { return joint.SourceBlockIndex >= 0 ? "block:" + joint.SourceBlockIndex : joint.Phase; }
        string JointSourceLabel(JointData joint)
        { return joint.Phase + (joint.SourceBlockIndex < 0 ? "" : " · " + MaterialText("结构段 ", "block ") + (joint.SourceBlockIndex + 1)); }
        string ListMaterialBefore(JointData joint)
        {
            bool repeated = joints.Where(j => j.Phase == joint.Phase && j.SourceBlockIndex >= 0).Select(j => j.SourceBlockIndex).Distinct().Count() > 1;
            return repeated && !String.IsNullOrWhiteSpace(joint.SourceMaterialBefore) ? joint.SourceMaterialBefore : JointMaterialBefore(joint);
        }
        string JointListValue(IEnumerable<string> values)
        {
            string[] distinct = values.Distinct(StringComparer.Ordinal).ToArray();
            return distinct.Length == 1 ? ProjectCreationLocalization.TranslateCanonical(languageIndex, distinct[0])
                : MaterialText("各结构段不同", "Varies by block");
        }
        void FillJointGridRows(string selectedPhase)
        {
            bool shared = CableMode == ProjectModes.ThreeCore;
            var blocks = joints.GroupBy(JointBlockKey).ToArray();
            bool common = shared && blocks.Length > 0 && blocks.All(g => g.Select(j => j.Position).OrderBy(p => p).SequenceEqual(blocks[0].Select(j => j.Position).OrderBy(p => p)));
            // Include empty blocks in the equality check, not just the blocks
            // represented by an interior node in the in-memory list.
            if (common && !String.IsNullOrWhiteSpace(loadedProjectPath))
            {
                try
                {
                    var snapshot = CableJointStore.Read(loadedProjectPath);
                    if (CanAppendMeasuredJoint) common = snapshot.Blocks.Count == blocks.Length && snapshot.Blocks.All(b => b.Positions.SequenceEqual(snapshot.Blocks[0].Positions));
                }
                catch { common = false; }
            }
            if (common && String.IsNullOrWhiteSpace(loadedProjectPath)) common = blocks.Length == 3;
            IEnumerable<JointData[]> rows = common
                ? joints.GroupBy(j => j.Position).OrderBy(g => g.Key).Select(g => g.OrderBy(j => j.SourceBlockIndex).ToArray())
                : joints.Where(j => shared || j.Phase == selectedPhase).OrderBy(j => j.Position).ThenBy(j => j.SourceBlockIndex).Select(j => new[] { j });
            foreach (JointData[] group in rows)
            {
                JointData source = group[0];
                string label = common ? MaterialText("ALL · 共用", "ALL · shared") : JointSourceLabel(source);
                int row = cableGrid.Rows.Add(label, JointListValue(group.Select(j => j.Name)), JointListValue(group.Select(j => j.AttachmentType)),
                    ProjectText.LengthMetres(source.Position), JointListValue(group.Select(j => j.Material)), JointListValue(group.Select(ListMaterialBefore)));
                cableGrid.Rows[row].Tag = source;
                string details = String.Join(Environment.NewLine, group.Select(j => JointSourceLabel(j) + " · " + j.Name + " · " + ProjectText.LengthMetres(j.Position) + " m · " + ListMaterialBefore(j) + " → " + j.Material));
                foreach (DataGridViewCell cell in cableGrid.Rows[row].Cells) cell.ToolTipText = details;
            }
        }
    }
}
