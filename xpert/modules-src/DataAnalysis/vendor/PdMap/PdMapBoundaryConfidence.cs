using System.ComponentModel;
using HVX.SignalEvidence;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        [Browsable(false)] public double BoundaryConfidenceTolerancePercent { get { return BoundaryConfidenceMath.TolerancePercent; } }
        [Browsable(false)] public double BoundaryExcessPercent { get { return BoundaryConfidenceMath.ExcessPercent(currentLocationM,cableLengthM); } }
        [Browsable(false)] public bool IsWithinBoundaryConfidenceTolerance
        { get { return reflection>=incident&&BoundaryExcessPercent>0&&BoundaryConfidenceMath.WithinTolerance(currentLocationM,cableLengthM); } }
    }
}
