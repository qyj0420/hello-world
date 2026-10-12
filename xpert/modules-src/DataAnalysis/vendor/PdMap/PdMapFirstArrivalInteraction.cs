using System;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // Timing uses raw samples. Display-filter ringing must not move an onset.
        int FirstArrivalAtRelease(int target)
        {
            int first,last;
            if (!TryReflectionSelectionBounds(out first,out last) || pulse.Length < 32) return target;
            int radius = Math.Max(8, Math.Min(128, Math.Max(n1,n2)));
            int left = Math.Max(first,target-radius), right = Math.Min(last,target+radius);
            int peak = -1;
            for (int i=left;i<=right;i++)
                if(IsFinite(pulse[i]) && (peak<0 || Math.Abs(pulse[i])>Math.Abs(pulse[peak]))) peak=i;
            if(peak<0)return target;
            double baseline,sigma;
            if(!TryArrivalNoise(out baseline,out sigma))return target;
            if(Math.Abs(pulse[peak]-baseline)<6*sigma)return target;
            int onset=HVX.Task17.Location.PeakAnchoredLeadingEdgeMath.FindPacket(pulse.Length,
                Math.Max(first,peak-radius),Math.Min(last,peak+radius),peak,
                Math.Max(1,peak-incident),sigma,i=>pulse[i]-baseline);
            // The pre-incident noise baseline can differ from the local foot
            // after an incident tail. Recheck against the quiet support directly
            // before the provisional onset, without altering any raw samples.
            if(onset>=first+12 && onset<peak)
            {
                double[] local=new double[12];
                for(int i=0;i<local.Length;i++)local[i]=pulse[onset-local.Length+i];
                Array.Sort(local);double localBaseline=(local[5]+local[6])*.5;
                for(int i=0;i<local.Length;i++)local[i]=Math.Abs(local[i]-localBaseline);
                Array.Sort(local);double localSigma=Math.Max(sigma,1.4826*(local[5]+local[6])*.5);
                double amplitude=Math.Abs(pulse[peak]-baseline);
                if(IsFinite(localBaseline)&&IsFinite(localSigma)&&Math.Abs(localBaseline-baseline)>3*sigma
                    &&Math.Abs(localBaseline-baseline)<.15*amplitude&&localSigma<.05*amplitude)
                {
                    int refined=HVX.Task17.Location.PeakAnchoredLeadingEdgeMath.FindPacket(pulse.Length,
                        Math.Max(first,peak-radius),Math.Min(last,peak+radius),peak,
                        Math.Max(1,peak-incident),localSigma,i=>pulse[i]-localBaseline);
                    if(refined<onset || refined>=peak)return target;
                    onset=refined;
                }
            }
            // Never fall back to a peak, or move to another far-away packet.
            return onset>=first && onset<=peak && Math.Abs(onset-target)<=radius ? onset : target;
        }

        bool TryArrivalNoise(out double baseline,out double sigma)
        {
            baseline=0;sigma=0;
            int count=Math.Min(128,Math.Max(0,incident/2));
            if(count<16 || count>pulse.Length)return false;
            double[] values=new double[count];
            for(int i=0;i<count;i++){if(!IsFinite(pulse[i]))return false;values[i]=pulse[i];}
            Array.Sort(values);baseline=values[count/2];
            for(int i=0;i<count;i++)values[i]=Math.Abs(values[i]-baseline);
            Array.Sort(values);sigma=Math.Max(1e-12,1.4826*values[count/2]);return true;
        }

        int DisplayIncidentPeak()
        {
            if(positioningMode!=0 || pulse.Length==0)return incident;
            int peak=Limit(incident,0,pulse.Length-1);
            int end=Math.Min(pulse.Length-1,peak+Math.Max(8,Math.Min(128,Math.Max(n1,n2))));
            if(reflectionSelected && reflection>incident)end=Math.Min(end,incident+(reflection-incident)/2);
            for(int i=peak+1;i<=end;i++)if(IsFinite(pulse[i]) && Math.Abs(pulse[i])>Math.Abs(pulse[peak]))peak=i;
            return peak;
        }
    }
}
