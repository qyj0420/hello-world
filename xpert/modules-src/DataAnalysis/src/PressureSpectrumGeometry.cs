using System;
using LabVIEWPdMap;
using HVX.UnifiedSpectrum;
using HVX.SignalEvidence;

namespace HVX.PdReview
{
    // Immutable geometry handoff: no live control, file I/O or algorithm runs
    // are retained by the pressure chart after the analysis owner is disposed.
    public sealed class PressureSpectrumGeometry
    {
        readonly PdMapControl.ReviewConnectorGeometry geometry;
        readonly double endpointPercent,jointPercent;
        internal PressureSpectrumGeometry(PdMapControl.ReviewConnectorGeometry value,double endpoint,double joint)
        {geometry=value;endpointPercent=endpoint;jointPercent=joint;}
        public static void Clear(UnifiedSpectrumControl chart){chart.SetReviewDisplayGeometry(null,null,null);}
        public void Apply(UnifiedSpectrumControl chart,bool approximate,bool reverse)
        {
            if(chart==null)throw new ArgumentNullException("chart");
            double[,] rows=chart.ResultParameterMatrix;
            chart.SetReviewLocationClusterTolerancePercent(endpointPercent);
            chart.SetReviewDisplayGeometry((r,raw)=>{
                if(r<0||r>=rows.GetLength(0)||!Finite(raw))return raw;
                double length=rows[r,5];if(!Finite(length)||length<=0)return raw;
                double position=BoundaryConfidenceMath.ProjectEndpointPosition(raw,length);
                double radius=length*endpointPercent/100;
                // Same method-7 endpoint-evidence exclusion as review display.
                bool endpointEvidenceRequired=rows.GetLength(1)>26&&rows[r,26]==6&&Math.Min(Math.Abs(position),Math.Abs(position-length))<=radius;
                if(approximate&&!endpointEvidenceRequired)position=geometry.Project(rows[r,0],length,position,radius,length*jointPercent/100);
                return reverse?length-position:position;
            },geometry.CableLengthForPhase,(phase,length)=>{
                var joints=geometry.JointPositionsForPhase(phase,length);
                if(reverse){for(int i=0;i<joints.Length;i++)joints[i]=length-joints[i];Array.Sort(joints);}return joints;
            });
        }
        static bool Finite(double n){return !Double.IsNaN(n)&&!Double.IsInfinity(n);}
    }
    public sealed partial class PdReviewControl
    {
        public PressureSpectrumGeometry CapturePressureSpectrumGeometry()
        {
            if(InvokeRequired)throw new InvalidOperationException("Capture project geometry on the UI thread.");
            return new PressureSpectrumGeometry(pdmap.GetReviewConnectorGeometry(),pdmap.PositionTolerancePercent,pdmap.JointApproximationErrorPercent);
        }
    }
}
