using System;
using System.ComponentModel;
using System.Linq;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        string[] cableMaterialOverride;
        bool cableStructurePositionsOverride;
        bool velocityConfigured;
        bool manualVelocityConfigured;
        double manualVelocityMPerUs;
        string cableStructureStatus = "ProjectDefaults";

        [Category("Cable")]
        [Description("Segment materials in distance order from 0 m. Empty/null restores project materials. Use SetCableStructure to change materials and joints atomically.")]
        public string[] CableMaterials
        {
            get { return cableMaterialOverride == null ? Array.Empty<string>() : (string[])cableMaterialOverride.Clone(); }
            set { SetCableStructure(value, connectorPositionsExplicit ? explicitConnectorPositions : null); }
        }

        [Category("Cable")]
        [Description("Ordered internal accessory positions in metres; optional 0/L endpoints accepted. Empty/null restores project joints. Use SetCableStructure for a paired update.")]
        public double[] CableAccessoryPositionsM
        {
            get { return connectorPositionsExplicit ? (double[])explicitConnectorPositions.Clone() : Array.Empty<double>(); }
            set { SetCableStructure(cableMaterialOverride, value); }
        }

        [Browsable(false)] public string CableStructureStatus { get { return cableStructureStatus; } }

        /// <summary>
        /// One atomic two-array port. Null/empty means project defaults for that input.
        /// Materials are ordered by physical distance, never by the time axis; their count
        /// must be internal-joint count + 1. Reject unordered geometry rather than silently
        /// sorting the positions and assigning a material to the wrong interval.
        /// Legacy SetConnectors(empty) continues to mean explicitly no joints.
        /// </summary>
        public void SetCableStructure(string[] materials, double[] accessoryPositionsM)
        {
            string[] nextMaterials = materials == null || materials.Length == 0 ? null : (string[])materials.Clone();
            double[] nextPositions = accessoryPositionsM == null || accessoryPositionsM.Length == 0 ? null : (double[])accessoryPositionsM.Clone();
            if (nextMaterials != null && (nextMaterials.Length > 4097 || nextMaterials.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256)))
                throw new ArgumentException("Each cable material must be a nonempty string (maximum 256 characters).", "materials");
            if (nextPositions != null)
            {
                if (nextPositions.Length > 4098 || !IsFinite(cableLengthM) || cableLengthM <= 0)
                    throw new ArgumentException("Set a valid cable length before the cable structure.", "accessoryPositionsM");
                for (int i = 0; i < nextPositions.Length; i++)
                    if (!IsFinite(nextPositions[i]) || nextPositions[i] < 0 || nextPositions[i] > cableLengthM || (i > 0 && nextPositions[i] <= nextPositions[i - 1]))
                        throw new ArgumentException("Accessory positions must be finite, strictly increasing, and within [0, cable length].", "accessoryPositionsM");
                nextPositions = nextPositions.Where(p => p > 0 && p < cableLengthM).ToArray();
            }
            double[] effective = nextPositions ?? projectConnectorPositions.Where(p => p > 0 && p < cableLengthM).ToArray();
            if (nextMaterials != null && nextMaterials.Length != effective.Length + 1)
                throw new ArgumentException("Material count must equal the number of internal joints plus one.", "materials");
            if (nextMaterials != null) nextMaterials = nextMaterials.Select(s => s.Trim()).ToArray();
            cableMaterialOverride = nextMaterials;
            connectorPositionsExplicit = nextPositions != null;
            cableStructurePositionsOverride = nextPositions != null;
            explicitConnectorPositions = nextPositions ?? Array.Empty<double>();
            ApplyProjectMetadataForMap(matrixDriven ? originalParameterRow : null);
            // External edits use only the cached matrices and project metadata. No disk read.
            RefreshCableStructureAnalysis();
        }

        public void ResetCableStructureOverride() { SetCableStructure(null, null); }

        bool ApplyManualMaterialSegments()
        {
            if (cableMaterialOverride == null && !cableStructurePositionsOverride)
            { cableStructureStatus = "ProjectDefaults"; return false; }
            double[] joints = connectors.Where(p => IsFinite(p) && p > 0 && p < cableLengthM).ToArray();
            string[] names = cableMaterialOverride;
            if (names == null)
            {
                // A geometry-only override may move the existing boundaries but cannot
                // bridge a missing/ambiguous material interval from the project.
                bool complete = projectMaterialStartsM.Length == joints.Length + 1 && projectMaterialStartsM.Length > 0;
                double end = 0;
                for (int i = 0; complete && i < projectMaterialStartsM.Length; i++)
                {
                    complete = Math.Abs(projectMaterialStartsM[i] - end) < 1e-6 && projectMaterialEndsM[i] > end;
                    end = projectMaterialEndsM[i];
                }
                complete = complete && Math.Abs(end - cableLengthM) < 1e-6;
                if (complete) names = projectMaterialOriginalNames;
            }
            bool valid = IsFinite(cableLengthM) && cableLengthM > 0 && names != null && names.Length == joints.Length + 1;
            if (connectorPositionsExplicit && explicitConnectorPositions.Any(p => !IsFinite(p) || p < 0 || p > cableLengthM)) valid = false;
            if (!valid)
            {
                currentMaterialStartsM = currentMaterialEndsM = Array.Empty<double>();
                currentMaterialNames = currentMaterialOriginalNames = Array.Empty<string>();
                materialSegmentsSource = cableStructureStatus = "OverrideGeometryMissingMaterials";
                return true;
            }
            currentMaterialStartsM = new double[names.Length]; currentMaterialEndsM = new double[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                currentMaterialStartsM[i] = i == 0 ? 0 : joints[i - 1];
                currentMaterialEndsM[i] = i == joints.Length ? cableLengthM : joints[i];
            }
            currentMaterialOriginalNames = (string[])names.Clone();
            currentMaterialNames = names.Select(ProjectMaterialEnglishName).ToArray();
            materialSegmentsSource = cableMaterialOverride != null ? "ExplicitInput" : "ProjectMaterialsWithExplicitGeometry";
            cableStructureStatus = "Applied";
            return true;
        }

        void RefreshCableStructureAnalysis(bool connectorDisplayOnly=false)
        {
            RefreshMixedCableModel();
            // A marker-only change cannot erase an untouched method-7 endpoint
            // identity supplied by the source result. Real geometry still remeasures.
            if(connectorDisplayOnly&&IsSeventhRow&&!MixedCableCalculation)RefreshPositionApproximationForTargets();
            else RefreshPositionApproximationForGeometry();
            NotifyApproximationDisplayChanged();
            viewReady = false;
            UpdateDisplayOptions();
            Invalidate();
        }
    }
}
