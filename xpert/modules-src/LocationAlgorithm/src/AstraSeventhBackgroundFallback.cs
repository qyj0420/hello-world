using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Review-only measurement proof. Fixed-size raw quiet intervals are
    // chosen before CAL, attenuation, correlation, geometry or candidate ranking.
    // No original support, raw sample, ownership or successful measurement changes.
    private static bool A7QuietOriginalBlock(A4Context c,int start)
    {
        const int count=16;
        if(c==null||c.MeasurementSignal==null||c.Raw==null||start<1||start+count>=c.Raw.Length)return false;
        var s=c.MeasurementSignal;double sigma=Math.Max(s.RawSigma,s.DifferenceSigma);
        if(!Finite(sigma)||sigma<=0||s.DetectionRaw==null||s.DetectionRaw.Length!=c.Raw.Length)return false;
        double mean=0;
        for(int k=0;k<count;k++){double v=s.DetectionRaw[start+k];if(!Finite(v)||!Finite(c.Raw[start+k]))return false;mean+=v;}
        mean/=count;
        // Reject a stable but high plateau: low variance alone is not background.
        if(Math.Abs(mean)>4*sigma)return false;
        double energy=0,maximum=0,rawMean=0,xy=0,xx=0;int active=0;
        for(int k=0;k<count;k++)
        {
            double v=s.DetectionRaw[start+k]-mean;energy+=v*v;maximum=Math.Max(maximum,Math.Abs(v));
            if(Math.Abs(v)>2.5*sigma)active++;
            double x=k-7.5;rawMean+=c.Raw[start+k];xy+=x*c.Raw[start+k];xx+=x*x;
        }
        if(active>1||energy>count*6.25*sigma*sigma||maximum>5*sigma)return false;
        rawMean/=count;double slope=xy/xx,rawEnergy=0,rawMaximum=0;
        for(int k=0;k<count;k++){double v=c.Raw[start+k]-rawMean-slope*(k-7.5);rawEnergy+=v*v;rawMaximum=Math.Max(rawMaximum,Math.Abs(v));}
        return rawEnergy<=count*6.25*sigma*sigma&&rawMaximum<=5*sigma;
    }

    private static bool A7FallbackGuard(A4Context c,A4Pulse p,bool left,int radius,
        out int start,out int end,out bool carved)
    {
        start=end=-1;carved=false;int first,last,crossed;
        // Freeze the original nearest support-free gap. Never hop to a second
        // gap because its baseline happens to improve a later measurement.
        bool free=A6NearestSupportExcludedGuard(c,p,left,radius,int.MaxValue,out first,out last,out crossed);
        if(!free)
        {
            // Only the immediately adjacent over-wide packet's OUTER flank can
            // release background. Never cross its canonical raw peak or another
            // packet, and never reinterpret a clipped/unmapped packet as quiet.
            var adjacent=c.AttenuationScoringPackets.Where(q=>q.Id!=p.Id&&
                (left?q.End==p.Start-1:q.Start==p.End+1)).ToArray();
            if(adjacent.Length!=1)return false;
            var q=adjacent[0];int peak=A4ChargeIndex(q);
            if(q.Clipped||!q.MappingValid||peak<q.Start||peak>q.End)return false;
            first=left?Math.Max(p.Start-radius,peak+17):p.End+1;
            last=left?p.Start-1:Math.Min(p.End+radius,peak-17);
            first=Math.Max(first,1);last=Math.Min(last,c.Raw.Length-2);
            if(c.AttenuationScoringPackets.Any(o=>o.Id!=p.Id&&o.Id!=q.Id&&o.Start<=last&&o.End>=first))return false;
            carved=true;
        }
        if(last-first+1<16)return false;
        int near=left?last-15:first,far=left?first:last-15,step=left?-1:1;
        for(int i=near;left?i>=far:i<=far;i+=step)
        {
            if(!A7QuietOriginalBlock(c,i))continue;
            // A quiet slice may not erase another packet's canonical endpoint.
            if(c.AttenuationScoringPackets.Any(q=>q.Id!=p.Id&&A4ChargeIndex(q)>=i-16&&A4ChargeIndex(q)<=i+31))continue;
            start=i;end=i+15;return true;
        }
        start=end=-1;return false;
    }

    private static List<A4Pulse> A7CarveProvenGuards(IList<A4Pulse> packets,A4Pulse target,int l0,int l1,int r0,int r1)
    {
        var result=new List<A4Pulse>();
        foreach(var p in packets)
        {
            if(p.Id==target.Id){result.Add(p);continue;}
            var spans=new List<Tuple<int,int>>{Tuple.Create(p.Start,p.End)};
            foreach(var cut in new[]{Tuple.Create(l0,l1),Tuple.Create(r0,r1)})
            {
                var next=new List<Tuple<int,int>>();
                foreach(var span in spans)
                {
                    if(cut.Item2<span.Item1||cut.Item1>span.Item2){next.Add(span);continue;}
                    if(span.Item1<cut.Item1)next.Add(Tuple.Create(span.Item1,cut.Item1-1));
                    if(span.Item2>cut.Item2)next.Add(Tuple.Create(cut.Item2+1,span.Item2));
                }
                spans=next;
            }
            foreach(var span in spans){var clone=A4ReviewClone(p);clone.Start=span.Item1;clone.End=span.Item2;result.Add(clone);}
        }
        return result;
    }

    private static A4AttenuationMeasurement A7BackgroundFallbackMeasurement(A4Context c,A4Pulse p,
        A4AttenuationMeasurement original,out string diagnostic)
    {
        diagnostic="original_measurement_retained";
        if(c==null||c.Algorithm!=6||p==null||original==null||original.Valid)return original;
        if(p.Clipped||p.UnresolvedOverlap||!p.MappingValid||p.Start<1||p.End>=c.Raw.Length-1)
        {diagnostic="raw_packet_clipped_unmapped_overlapping_or_at_edge";return original;}
        string reason=original.Reason??"";
        if(!reason.StartsWith("original_guard_contains_unresolved_signal_after_detrend")&&
           !reason.StartsWith("original_guard_nonlinear_drift_after_detrend")&&
           !reason.StartsWith("sixth_two_original_support_excluded_guards_of_16_samples_required"))
        {diagnostic="original_failure_not_eligible_for_background_fallback:"+reason;return original;}
        int radius=Math.Max(0,Math.Min(4096,c.RoundTrip)),l0,l1,r0,r1;bool lc,rc;
        if(radius<16){diagnostic="physical_roundtrip_too_short_for_16_sample_background";return original;}
        if(!A7FallbackGuard(c,p,true,radius,out l0,out l1,out lc)||!A7FallbackGuard(c,p,false,radius,out r0,out r1,out rc))
        {diagnostic="bounded_raw_background_proof_missing";return original;}
        var occupancy=A7CarveProvenGuards(c.AttenuationScoringPackets,p,l0,l1,r0,r1);
        var measured=A4MeasureSharedPacket(c.MeasurementSignal,p,occupancy,l0,l1,r0,r1);
        diagnostic="fixed_raw_quiet_16x2;left="+l0+".."+l1+";right="+r0+".."+r1+
            ";carved_adjacent_flank="+(lc||rc?"true":"false")+";shared_measurement="+measured.Reason;
        // Retain all original-sample contamination, drift, extension, edge and SNR
        // gates. There is ONE measured proof; no retry follows a failed baseline.
        if(!measured.Valid)return original;
        measured.Reason+=";raw_guard_recovered_review_only";return measured;
    }
}
