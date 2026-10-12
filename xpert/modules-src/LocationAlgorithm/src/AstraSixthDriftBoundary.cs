using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Methods 5 (index 4) and 6 share this raw-only boundary proof. Keep the
    // peak-derived quiet duration and its original
    // variance/active-sample limits. A small residual DC level (up to 6 sigma,
    // NOT a percentage of the main peak) may no longer bridge whole packets.
    private static bool A6DriftBoundedQuietBlock(double[] x,int first,int last,double center,double sigma)
    {
        if(first<0||last>=x.Length||last-first+1<16||!Finite(sigma)||sigma<=0)return false;
        double mean=0;for(int i=first;i<=last;i++)mean+=x[i]-center;
        mean/=last-first+1;
        return Math.Abs(mean)<=6*sigma&&A4QuietBlock(x,first,last,center+mean,sigma);
    }

    private static A4Pulse A6RecoverDriftBoundedRawPacket(A4Context raw,A4Pulse original,
        IList<A4Pulse> neighbours,A4Context record,int origin)
    {
        if((record.Algorithm!=5&&record.Algorithm!=4)||!original.UnresolvedOverlap||original.Clipped)return original;
        int peak=AbsoluteMaximumIndex(raw.Raw,original.Start,original.End,raw.RawCenter);
        A4Pulse p=A4SeededPacket(raw,peak,peak);
        // This fallback addresses a seed envelope extended by background drift.
        // Do not re-open a complete seeded packet rejected by the older bipolar,
        // stronger-opposite-lobe or external-guard checks.
        if(!p.MappingValid||(!p.UnresolvedOverlap&&p.Start>=original.Start&&p.End<=original.End))return original;
        int hold=Math.Max(16,Math.Min(64,(int)Math.Ceiling(p.Period*.8)));
        int lower=Math.Max(1,original.Start-2*hold),upper=Math.Min(raw.Raw.Length-2,original.End);
        foreach(A4Pulse q in neighbours)
        {
            if(Object.ReferenceEquals(q,original))continue;
            if(q.End<original.Start)lower=Math.Max(lower,q.End+1);
            if(q.Start>original.End)upper=Math.Min(upper,q.Start-1);
        }
        int before=-1,after=-1;
        for(int k=peak-hold;k>=lower;k--)
            if(A6DriftBoundedQuietBlock(raw.Raw,k,k+hold-1,raw.RawCenter,raw.Sigma)){before=k;break;}
        for(int k=peak+1;k<=Math.Min(upper-hold+1,peak+raw.RoundTrip-hold+1);k++)
            if(A6DriftBoundedQuietBlock(raw.Raw,k,k+hold-1,raw.RawCenter,raw.Sigma)){after=k;break;}
        string reason="drift_boundary_missing_two_full_quiet_guards";
        bool valid=before>=0&&after>=0;
        if(valid)
        {
            p.Start=before+hold;p.End=after-1;
            valid=p.Start<peak&&p.End>peak&&p.End-p.Start<=raw.RoundTrip&&
                p.Start>=original.Start&&p.End<=original.End&&
                AbsoluteMaximumIndex(raw.Raw,p.Start,p.End,raw.RawCenter)==peak;
            double main=raw.Raw[peak]-raw.RawCenter,opposite=0;
            for(int k=p.Start;k<=p.End;k++)
                if((raw.Raw[k]-raw.RawCenter)*main<0)opposite=Math.Max(opposite,Math.Abs(raw.Raw[k]-raw.RawCenter));
            valid=valid&&opposite>=Math.Max(5*raw.Sigma,.10*Math.Abs(main));
            reason="drift_boundary_complete_bipolar_support_not_demonstrated";
            int lookAhead=Math.Min(original.End,peak+Math.Max(96,raw.RoundTrip/4));
            for(int k=p.End+1;valid&&k<=lookAhead;k++)
                if((raw.Raw[k]-raw.RawCenter)*main<0&&Math.Abs(raw.Raw[k]-raw.RawCenter)>opposite+3*raw.Sigma)
                {valid=false;reason="drift_boundary_stronger_opposite_lobe_outside_support";}
        }
        if(valid)
        {
            p.RawAmplitudeIndex=peak;p.BoundaryRefined=true;p.UnresolvedOverlap=false;p.Clipped=false;
            p.HasRefinedGuardProof=true;
            p.RefinedLeftGuardStart=before;p.RefinedLeftGuardEnd=before+hold-1;
            p.RefinedRightGuardStart=after;p.RefinedRightGuardEnd=after+hold-1;
            A4Measure(raw,p);
            reason="method6_fixed_duration_six_sigma_drift_guards_before_matching";
        }
        if(record.BoundaryReviewTraces.Count<256)
            record.BoundaryReviewTraces.Add("{\"kind\":\"method6_drift_boundary\",\"raw_peak\":"+(peak+origin)+
                ",\"original_start\":"+(original.Start+origin)+",\"original_end\":"+(original.End+origin)+
                ",\"refined\":"+(valid?"true":"false")+",\"start\":"+((valid?p.Start:original.Start)+origin)+
                ",\"end\":"+((valid?p.End:original.End)+origin)+",\"guard_samples\":"+hold+
                ",\"before_guard_start\":"+(before<0?-1:before+origin)+",\"after_guard_start\":"+(after<0?-1:after+origin)+
                ",\"reason\":"+Json(reason)+",\"uses_pair_score\":false}");
        return valid?p:original;
    }
}
