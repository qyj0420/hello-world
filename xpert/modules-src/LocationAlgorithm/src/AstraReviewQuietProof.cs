using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // A review-only, per-packet proof chosen BEFORE any candidate is scored.
    // Fixed 16 original samples match the shared measurement minimum. Never
    // shrink a failed longer guard, optimize rho, normalize, or flip polarity.
    private static bool A4ReviewProofGuard(A4Context c,A4Pulse p,IList<A4Pulse> packets,
        int start,double amplitude,out A4ReviewGuard guard)
    {
        guard=null;
        foreach(A4Pulse q in packets)
            if(q.Id!=p.Id&&q.Start<=start+15&&q.End>=start)return false;
        return A4ReviewQuietGuard(c,start,16,amplitude,out guard);
    }

    private static A4ReviewPacket A4ReviewQuietProof(A4Context c,A4Pulse original,IList<A4Pulse> packets)
    {
        var r=new A4ReviewPacket {Original=original,Reason="review_original_quiet_proof_unavailable"};
        if(c==null||(c.Algorithm!=4&&c.Algorithm!=5)||original==null||original.Clipped||
            original.Start<0||original.End>=c.Raw.Length||!original.MappingValid)return r;
        int peak=A4ChargeIndex(original),radius=Math.Max(16,Math.Min(4096,c.RoundTrip));
        int lower=Math.Max(0,original.Start-(original.UnresolvedOverlap?64:radius));
        int upper=(int)Math.Min(c.Raw.Length-1L,(long)original.End+(original.UnresolvedOverlap?64:radius));
        // Reopening a boundary may use its immediately adjacent empty gap, but
        // must never expand a support through a known neighbouring packet.
        if(original.UnresolvedOverlap)
            foreach(A4Pulse q in packets)
            {
                if(q.Id==original.Id)continue;
                if(q.End<original.Start)lower=Math.Max(lower,q.End+1);
                if(q.Start>original.End)upper=Math.Min(upper,q.Start-1);
            }
        double amplitude=A4RawCharge(c,peak);A4ReviewGuard before=null,after=null,test;
        int begin=original.UnresolvedOverlap?peak-16:original.Start-16;
        for(int k=begin;k>=lower;k--)
            if(A4ReviewProofGuard(c,original,packets,k,amplitude,out test)){before=test;break;}
        begin=original.UnresolvedOverlap?peak+1:original.End+1;
        int last=original.UnresolvedOverlap?Math.Min(upper-15,peak+radius):upper-15;
        for(int k=begin;k<=last;k++)
            if(A4ReviewProofGuard(c,original,packets,k,amplitude,out test)){after=test;break;}
        r.Before=before;r.After=after;
        if(before==null||after==null){r.Reason="review_missing_two_fixed16_original_quiet_guards";return r;}
        A4Pulse p=A4ReviewClone(original);
        if(original.UnresolvedOverlap)
        {
            p.Start=before.End+1;p.End=after.Start-1;
            if(p.AnalysisSamples!=null&&p.AnalysisSamples.Length>0&&
                (p.Start<p.AnalysisOrigin||p.End>=(long)p.AnalysisOrigin+p.AnalysisSamples.Length))
            {r.Reason="review_boundary_exceeds_existing_analysis_samples";return r;}
            // Expanded audit envelope includes only the verified adjacent gap.
            // The raw dominant peak, opposite main lobe and stronger later
            // opposite-lobe checks remain those of the shared bipolar proof.
            A4Pulse envelope=A4ReviewClone(original);envelope.Start=lower;envelope.End=upper;
            string complete;
            if(p.End-p.Start>c.RoundTrip||!A6BoundaryHypothesisComplete(c,envelope,p,out complete))
            {r.Reason="review_quiet_proof_does_not_preserve_complete_bipolar_packet";return r;}
            p.UnresolvedOverlap=false;
            p.Peak=p.Start;
            for(int k=p.Start+1;k<=p.End;k++)
                if(Math.Abs(A4Sample(c.Signal,p,k)-c.Center)>Math.Abs(A4Sample(c.Signal,p,p.Peak)-c.Center))p.Peak=k;
            // Canonical raw identity and charge are never replaced by a new
            // filter extremum as a consequence of this diagnostic support.
            p.RawAmplitudeIndex=peak;
            A4Measure(c,p);
        }
        p.BoundaryRefined=true;p.HasRefinedGuardProof=true;
        p.RefinedLeftGuardStart=before.Start;p.RefinedLeftGuardEnd=before.End;
        p.RefinedRightGuardStart=after.Start;p.RefinedRightGuardEnd=after.End;
        p.AttenuationMeasurement=null;r.Pulse=p;
        r.Reason=original.UnresolvedOverlap?"review_fixed16_original_quiet_guards_complete_bipolar_boundary":
            "review_fixed16_original_quiet_guards_unchanged_packet_support";
        return r;
    }
}
