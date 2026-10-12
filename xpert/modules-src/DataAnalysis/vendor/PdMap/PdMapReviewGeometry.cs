using System;
using System.Linq;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // Immutable review snapshot. Its arrays never escape this class, and point
        // rendering only reads cached geometry: it must not reread project files.
        internal sealed class ReviewConnectorGeometry
        {
            readonly bool globalOverride;
            readonly double[][] joints;
            readonly double[] lengths;
            internal ReviewConnectorGeometry(bool global,double[][] positions,double[] cableLengths)
            { globalOverride=global;joints=positions;lengths=cableLengths; }

            internal bool SameGeometry(ReviewConnectorGeometry other)
            {
                if(ReferenceEquals(this,other))return true;
                if(other==null||globalOverride!=other.globalOverride)return false;
                for(int p=0;p<4;p++)
                    if(!lengths[p].Equals(other.lengths[p])||!joints[p].SequenceEqual(other.joints[p]))return false;
                return true;
            }

            internal double CableLengthForPhase(int phase)
            { return phase>=0&&phase<lengths.Length?lengths[phase]:double.NaN; }

            internal double[] JointPositionsForPhase(int phase,double length)
            {
                int p=globalOverride?0:phase;
                if(p<0||p>=joints.Length||!IsFinite(length)||length<=0)return Array.Empty<double>();
                if(!globalOverride&&(!IsFinite(lengths[p])||Math.Abs(length-lengths[p])>Math.Max(1e-6,lengths[p]*1e-9)))
                    return Array.Empty<double>();
                return joints[p].Where(j=>j>0&&j<length).ToArray();
            }

            internal double Project(double phase,double length,double raw,double radius)
            { return Project(phase,length,raw,radius,radius); }
            internal double Project(double phase,double length,double raw,double radius,double jointRadius)
            {
                double best=raw,distance=double.PositiveInfinity;
                Consider(raw,0,radius,ref best,ref distance);
                Consider(raw,length,radius,ref best,ref distance);
                int p=globalOverride?0:IsFinite(phase)&&phase>=0&&phase<=3&&phase==Math.Round(phase)?(int)phase:-1;
                // A matrix can contain older records from another cable/project.
                // A same-phase name alone does not prove its geometry: mismatched
                // lengths use only the row's own endpoints, never foreign joints.
                if(p>=0&&(globalOverride||(IsFinite(lengths[p])&&Math.Abs(length-lengths[p])<=Math.Max(1e-6,lengths[p]*1e-9))))
                    foreach(double joint in joints[p])
                        if(joint>0&&joint<length)Consider(raw,joint,jointRadius,ref best,ref distance);
                return best;
            }
            static void Consider(double raw,double target,double radius,ref double best,ref double distance)
            {
                double d=Math.Abs(raw-target);
                if(raw>=target-radius&&raw<=target+radius&&d<distance){best=target;distance=d;}
            }
        }

        ReviewConnectorGeometry reviewGeometryCache;
        int reviewGeometryReadCount=-1;
        bool reviewGeometryEnabled,reviewGeometryExplicit;
        string reviewGeometryLoadCode;
        double[] reviewGeometryExplicitValues;
        double reviewGeometryLeadLength=double.NaN;

        internal ReviewConnectorGeometry GetReviewConnectorGeometry()
        {
            if(reviewGeometryCache!=null&&reviewGeometryLeadLength.Equals(connectingCableLengthM)&&reviewGeometryReadCount==ProjectMetadataReadCount&&
                reviewGeometryEnabled==ProjectCalibrationEnabled&&reviewGeometryLoadCode==projectMetadataLoadCode&&
                reviewGeometryExplicit==connectorPositionsExplicit&&
                (!connectorPositionsExplicit||ReferenceEquals(reviewGeometryExplicitValues,explicitConnectorPositions)))return reviewGeometryCache;
            var positions=new double[4][];var lengths=new[]{double.NaN,double.NaN,double.NaN,double.NaN};
            for(int p=0;p<4;p++)
            {
                positions[p]=Array.Empty<double>();
                if(connectorPositionsExplicit){positions[p]=(double[])explicitConnectorPositions.Clone();continue;}
                if(!ProjectCalibrationEnabled||projectMetadataLoadCode!="MetadataLoaded")continue;
                string phase=p==0?"ALL":p==1?"A":p==2?"B":"C";
                var selected=projectCableBlocks.Where(b=>b.Phase==phase&&b.Records.Count>0).ToArray();
                if(selected.Length==0&&p!=0)selected=projectCableBlocks.Where(b=>b.Phase=="ALL"&&b.Records.Count>0).ToArray();
                if(selected.Length==0)continue;
                ProjectCableBlock first=selected[0];
                if(!selected.All(b=>b.Valid&&b.Positions.Count>0&&b.Positions[b.Positions.Count-1]>0&&b.Records.SequenceEqual(first.Records)))continue;
                double length=first.Positions[first.Positions.Count-1];lengths[p]=length-connectingCableLengthM;
                // Internal level-1 accessory boundaries are joints regardless
                // of their editable label; the lead and both endpoints remain excluded.
                positions[p]=first.Positions.Where(v=>v>connectingCableLengthM&&v<length)
                    .Select(ProjectPositionToDut).Distinct().OrderBy(v=>v).ToArray();
            }
            reviewGeometryCache=new ReviewConnectorGeometry(connectorPositionsExplicit,positions,lengths);
            reviewGeometryReadCount=ProjectMetadataReadCount;reviewGeometryEnabled=ProjectCalibrationEnabled;
            reviewGeometryLoadCode=projectMetadataLoadCode;reviewGeometryExplicit=connectorPositionsExplicit;
            reviewGeometryExplicitValues=explicitConnectorPositions;
            reviewGeometryLeadLength=connectingCableLengthM;
            return reviewGeometryCache;
        }
    }
}
