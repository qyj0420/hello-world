using System;
using System.ComponentModel;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        LabVIEWPdMap.PdMapControl.ReviewConnectorGeometry projectedGeometry;
        bool projectionEnabled;
        bool projectionReversed;
        double projectionTolerancePercent;
        double projectionJointTolerancePercent;
        void InitializeApproximationLink()
        {
            spectrum.SetReviewLocationClusterTolerancePercent(pdmap.PositionTolerancePercent);
            pdmap.ApproximationDisplayChanged+=(s,e)=>
            {
                RefreshApproximationProjection();
                // Rebuilding the projection is display-only and does not raise
                // Spectrum.ViewChanged. Invalidate/requeue the matching conclusion
                // explicitly so it cannot remain based on the previous snap policy.
                QueueAutomaticEvaluation();
                RefreshEvaluationUi();
                RefreshReviewUi();
            };
        }
        void RefreshApproximationProjection()
        {
            var next=pdmap.GetReviewConnectorGeometry();
            bool enabled=pdmap.ApproximateCablePositions&&!AiLearningMode;
            double tolerance=pdmap.PositionTolerancePercent;
            // Median guides follow physical error even when joint snapping is off.
            spectrum.SetReviewLocationClusterTolerancePercent(tolerance);
            bool changed=enabled!=projectionEnabled||projectionReversed!=ReversePositionDisplay||
                !next.SameGeometry(projectedGeometry)||(enabled&&(!tolerance.Equals(projectionTolerancePercent)||!pdmap.JointApproximationErrorPercent.Equals(projectionJointTolerancePercent)));
            projectedGeometry=next;projectionEnabled=enabled;projectionTolerancePercent=tolerance;
            projectionJointTolerancePercent=pdmap.JointApproximationErrorPercent;
            projectionReversed=ReversePositionDisplay;
            // Selecting another phase does not change this all-phase snapshot.
            // Repeated navigation/identical geometry ports must not rebuild data.
            if(!changed)return;
            // Boundary normalization is unconditional and uses the same coordinate
            // as the working rows and hit testing. Joint approximation remains optional.
            spectrum.SetReviewDisplayGeometry(ProjectAccessoryLocation,
                next.CableLengthForPhase,PresentedJointPositions);
        }
        double[] PresentedJointPositions(int phase,double length)
        {
            var joints=projectedGeometry.JointPositionsForPhase(phase,length);
            if(projectionReversed){for(int i=0;i<joints.Length;i++)joints[i]=length-joints[i];Array.Sort(joints);}
            return joints;
        }
        double ProjectAccessoryLocation(int row,double raw)
        { return PresentReviewPosition(row,ProjectCanonicalAccessoryLocation(row,raw)); }
        // Canonical DUT coordinates are shared by evaluation, exports and statistics.
        // Source rows, measured timing and OriginalParameterMatrix remain untouched.
        double ProjectCanonicalAccessoryLocation(int row,double raw)
        {
            if(!Finite(raw)||row<0||row>=SourceRowCount)return raw;
            double length=working[row,5];if(!Finite(length)||length<=0)return raw;
            raw=HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(raw,length);
            if(!pdmap.ApproximateCablePositions||AiLearningMode||IsExactManualPositionRow(row))return raw;
            double radius=length*projectionTolerancePercent/100;
            if(!Finite(radius)||projectedGeometry==null)return raw;
            // Method 7 endpoint identity must come from CAL evidence, not a
            // display snap that changes an indeterminate near-boundary result.
            if(working.GetLength(1)>26&&working[row,26]==6&&
                Math.Min(Math.Abs(raw),Math.Abs(raw-length))<=radius)return raw;
            return projectedGeometry.Project(working[row,0],length,raw,radius,length*projectionJointTolerancePercent/100);
        }
        [Browsable(false)] public double[,] EffectiveResultParameterMatrix
        {
            get { RefreshApproximationProjection();var result=(double[,])working.Clone();
                for(int r=0;r<result.GetLength(0);r++)result[r,13]=ProjectCanonicalAccessoryLocation(r,result[r,13]);return result; }
        }
        [Browsable(false)] public string PositionProjectionPolicy {get{return "nearest joint within +/-"+pdmap.JointApproximationErrorPercent.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"% of DUT length; endpoint tolerance "+pdmap.PositionTolerancePercent.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"%; preserve raw timing and source; disabled in AI learning mode";}}
        [Category("Analysis"),DefaultValue(1.0)]
        public double JointApproximationErrorPercent
        {get{return pdmap.JointApproximationErrorPercent;}set{pdmap.JointApproximationErrorPercent=value;}}
        [Category("Analysis"),DefaultValue(2.0)]
        public double AccessoryApproximationBandPercent
        {get{return pdmap.AccessoryApproximationBandPercent;}set{pdmap.AccessoryApproximationBandPercent=value;}}
        [Category("Analysis"),DefaultValue(15.0)]
        public double ConfidenceAttenuationTolerancePercent
        {get{return AnalysisAttenuationTolerancePercent;}set{AnalysisAttenuationTolerancePercent=value;}}
        public double CalibrationToleranceAdaptationLimitPercent
        {get{return pdmap.CalibrationToleranceAdaptationLimitPercent;}set{pdmap.CalibrationToleranceAdaptationLimitPercent=value;}}
    }
}
