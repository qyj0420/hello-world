using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        Func<int,double> reviewCableLengthProvider;
        Func<int,double,double[]> reviewJointProvider;

        // Accessory geometry belongs to the cable, not to accepted/plottable
        // pulses. Never derive its phase from view.Points/visibleRows.
        int[] CableMarkerPhases()
        {
            if(phaseFilter>=0)
                return phaseFilter==0?visible.Any(v=>v)?new[]{0}:new int[0]:
                    visible[phaseFilter-1]?new[]{phaseFilter}:new int[0];
            var phases=new HashSet<int>();bool hasAbc=false,hasAll=false;
            for(int r=0;r<source.GetLength(0);r++)
            {
                double p=source[r,0];
                if(p>=1&&p<=3&&p==Math.Truncate(p))
                {hasAbc=true;if(visible[(int)p-1])phases.Add((int)p);}
                else if(p==0)hasAll=true;
            }
            if(hasAbc)return phases.OrderBy(p=>p).ToArray();
            if(hasAll)return visible.Any(v=>v)?new[]{0}:new int[0];
            // Before loading pulses, show only geometry that the project
            // actually provides. Missing/ambiguous phase blocks give no joints.
            return Enumerable.Range(1,3).Where(p=>visible[p-1]).ToArray();
        }

        double MarkerProjectLength(int phase)
        { return reviewCableLengthProvider==null?metadata.LengthForPhase(phase):reviewCableLengthProvider(phase); }

        double CableMarkerLength()
        {
            double length=double.NaN;
            foreach(int phase in CableMarkerPhases())
            {
                double candidate=MarkerProjectLength(phase);
                if(Finite(candidate)&&candidate>0&&(!Finite(length)||candidate>length))length=candidate;
            }
            if(Finite(length))return length;
            return MarkerProjectLength(MetadataPhase());
        }

        double[] CableMarkerJoints()
        {
            var result=new SortedSet<double>();
            foreach(int phase in CableMarkerPhases())
            {
                // Full source rows preserve project context even when every
                // point is rejected by acceptance, statistics, or Q-L validity.
                var lengths=new HashSet<double>();bool anyPhaseRow=false;
                for(int r=0;r<source.GetLength(0);r++)if(source[r,0]==phase)
                {
                    anyPhaseRow=true;double length=source[r,5];
                    if(Finite(length)&&length>0)lengths.Add(length);
                }
                if(!anyPhaseRow)
                {
                    double length=MarkerProjectLength(phase);
                    if(Finite(length)&&length>0)lengths.Add(length);
                }
                foreach(double length in lengths)
                {
                    double[] joints;
                    if(reviewJointProvider!=null)joints=reviewJointProvider(phase,length);
                    else
                    {
                        double projectLength=metadata.LengthForPhase(phase);
                        if(!Finite(projectLength)||Math.Abs(length-projectLength)>Math.Max(1e-6,projectLength*1e-9))continue;
                        joints=metadata.JointsForPhase(phase);
                    }
                    if(joints==null)continue;
                    foreach(double joint in joints)
                        if(Finite(joint)&&joint>0&&joint<length&&joint<currentLength)result.Add(joint);
                }
            }
            return result.ToArray();
        }
    }
}
