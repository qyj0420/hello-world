using System;
using System.Linq;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        bool jointBeforeMaterialChanged, jointAfterMaterialChanged;

        string JointMaterialBefore(JointData joint)
        {
            if (CanAppendMeasuredJoint && joint.SourceBlockIndex >= 0 && !String.IsNullOrWhiteSpace(joint.SourceMaterialBefore))
                return joint.SourceMaterialBefore;
            return MaterialAt(joint.Phase, joint.Position);
        }

        void SetPrecedingSegmentMaterial(JointData joint, string material)
        {
            if (String.IsNullOrWhiteSpace(material)) return;
            JointData previous = joints.Where(j => j != joint && j.Phase == joint.Phase && j.Position < joint.Position)
                .OrderBy(j => j.Position).LastOrDefault();
            if (previous == null) phaseFirstMaterials[joint.Phase] = material.Trim();
            else previous.Material = material.Trim();
        }

        void ResetJointMaterialDefaults()
        {
            jointBeforeMaterialChanged = jointAfterMaterialChanged = false;
            RefreshJointMaterialFromPosition();
        }
    }
}
