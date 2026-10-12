using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A6OriginalGuardTrace
    {
        internal int Radius,PreferredLength,LeftStart=-1,LeftEnd=-1,RightStart=-1,RightEnd=-1;
        internal int LeftCrossedPackets,RightCrossedPackets;
        internal string Source="no_complete_pair_of_support_excluded_guards";
    }
    // Per-engine diagnostics only; public analysis constructs a new engine for every call.
    private readonly Dictionary<A4AttenuationMeasurement,A6OriginalGuardTrace> a6GuardTraces=
        new Dictionary<A4AttenuationMeasurement,A6OriginalGuardTrace>();

    private void A6ClearGuardMeasurementDiagnostics(){a6GuardTraces.Clear();}

    private static bool A6RefinedGuard(A4Context c,A4Pulse p,bool left,out int start,out int end)
    {
        start=left?p.RefinedLeftGuardStart:p.RefinedRightGuardStart;
        end=left?p.RefinedLeftGuardEnd:p.RefinedRightGuardEnd;
        if(!p.HasRefinedGuardProof||!p.BoundaryRefined||start<0||end>=c.Raw.Length||end-start+1<16||
            (left?end>=p.Start:start<=p.End))return false;
        foreach(A4Pulse other in c.AttenuationScoringPackets)
            if(other.Id!=p.Id&&other.Start<=end&&other.End>=start)return false;
        return true;
    }

    private static bool A6NearestSupportExcludedGuard(A4Context c,A4Pulse p,bool left,
        int radius,int preferred,out int start,out int end,out int crossed)
    {
        start=end=-1;crossed=0;
        int lower=left?Math.Max(0,p.Start-radius):p.End+1;
        int upper=left?p.Start-1:(int)Math.Min(c.Raw.Length-1L,(long)p.End+radius);
        if(lower>upper||upper-lower+1<16)return false;
        var occupied=new bool[upper-lower+1];
        foreach(A4Pulse q in c.AttenuationScoringPackets)
        {
            if(q.Id==p.Id||q.End<lower||q.Start>upper)continue;
            int first=Math.Max(lower,q.Start),last=Math.Min(upper,q.End);
            for(int i=first;i<=last;i++)occupied[i-lower]=true;
        }
        // Search nearest to this packet first. Skip only blocked or <16-sample gaps.
        // Selection never inspects correlation, amplitude ratios, expected position,
        // candidate score, or which baseline would improve a later measurement.
        int cursor=left?upper:lower,step=left?-1:1;
        while(cursor>=lower&&cursor<=upper)
        {
            if(occupied[cursor-lower]){cursor+=step;continue;}
            int near=cursor,count=0;
            while(cursor>=lower&&cursor<=upper&&!occupied[cursor-lower]){count++;cursor+=step;}
            if(count<16)continue;
            int length=Math.Min(preferred,count);
            start=left?near-length+1:near;end=left?near:near+length-1;
            foreach(A4Pulse q in c.AttenuationScoringPackets)
            {
                if(q.Id==p.Id)continue;
                if(left&&q.End> end&&q.Start<p.Start||!left&&q.Start<start&&q.End>p.End)crossed++;
            }
            return true;
        }
        return false;
    }

    private A4AttenuationMeasurement A6ScoringMeasurement(A4Context c,A4Pulse p)
    {
        if(p!=null&&p.AttenuationMeasurement!=null)return p.AttenuationMeasurement;
        A6OriginalGuardTrace trace;
        A4AttenuationMeasurement measurement=A6ScoringMeasurementCore(c,p,out trace);
        a6GuardTraces[measurement]=trace;
        if(p!=null)p.AttenuationMeasurement=measurement;
        return measurement;
    }

    // Shared read-only core. In particular, do not inspect or write p.AttenuationMeasurement:
    // legacy attenuation and unified shape correlation must retain independent caches.
    private static A4AttenuationMeasurement A6ScoringMeasurementCore(A4Context c,A4Pulse p,
        out A6OriginalGuardTrace trace)
    {
        trace=new A6OriginalGuardTrace();A4AttenuationMeasurement measurement;
        if(c==null||p==null||c.Raw==null||c.AttenuationScoringSignal==null||
            c.AttenuationScoringPackets==null||p.Start<0||p.End>=c.Raw.Length||p.Start>p.End)
            measurement=new A4AttenuationMeasurement {Reason="sixth_original_guard_measurement_inputs_unavailable"};
        else
        {
            // Index4 and explicit method6 hybrid: a small neighbouring packet can occupy the whole old
            // 1/5-RT radius. Search the nearest support-free background within
            // one RT, bounded to 4096 samples. Keep the SAME preferred length,
            // 16-sample minimum and original-sample quality tests. Never retry
            // another guard after its quality or a later pair score fails.
            trace.Radius=(c.Algorithm==4&&c.Index4ExtendedGuardSearch||c.Algorithm==5&&c.SixthHybridGuardSearch||c.Algorithm==6)?Math.Max(128,Math.Min(4096,c.RoundTrip)):
                Math.Max(128,Math.Min(512,c.RoundTrip/5));
            trace.PreferredLength=Math.Max(24,Math.Min(128,c.RoundTrip/5));
            bool overlap=false;
            foreach(A4Pulse q in c.AttenuationScoringPackets)
                if(q.Id!=p.Id&&q.Start<=p.End&&q.End>=p.Start){overlap=true;break;}
            if(overlap)measurement=new A4AttenuationMeasurement {Reason="raw_scoring_support_overlaps_other_packet"};
            else
            {
                bool proofRequested=(c.Algorithm==5||c.Algorithm==4)&&p.HasRefinedGuardProof;
                // Boundary proof is determined from raw samples before matching.
                // Do not enlarge its quiet intervals into a neighbouring packet or
                // retry guard locations based on a downstream score.
                bool left=proofRequested?A6RefinedGuard(c,p,true,out trace.LeftStart,out trace.LeftEnd):
                    A6NearestSupportExcludedGuard(c,p,true,trace.Radius,trace.PreferredLength,
                        out trace.LeftStart,out trace.LeftEnd,out trace.LeftCrossedPackets);
                bool right=proofRequested?A6RefinedGuard(c,p,false,out trace.RightStart,out trace.RightEnd):
                    A6NearestSupportExcludedGuard(c,p,false,trace.Radius,trace.PreferredLength,
                        out trace.RightStart,out trace.RightEnd,out trace.RightCrossedPackets);
                if(!left||!right)
                    measurement=new A4AttenuationMeasurement {Reason=proofRequested?
                        "sixth_refined_guard_proof_invalid_no_fallback":"sixth_two_original_support_excluded_guards_of_16_samples_required"};
                else
                {
                    trace.Source=proofRequested?"pre_matching_refined_raw_boundary_quiet_guards":trace.LeftCrossedPackets+trace.RightCrossedPackets>0?
                        "bounded_nearest_original_guards_across_known_packet":"bounded_nearest_adjacent_original_guards";
                    // Preserve every existing baseline, clipping, original-signal contamination,
                    // lowpass-extension and SNR check. If this nearest selection fails, do NOT
                    // retry more distant windows that happen to produce a favorable score.
                    if(c.MeasurementSignal==null)c.MeasurementSignal=A4PrepareMeasurementSignal(c.Raw,c.Fs,c.RoundTrip);
                    measurement=A4MeasureSharedPacket(c.MeasurementSignal,p,c.AttenuationScoringPackets,
                        trace.LeftStart,trace.LeftEnd,trace.RightStart,trace.RightEnd);
                }
            }
        }
        measurement.Reason+=";sixth_guard_source="+trace.Source;
        return measurement;
    }

    private string A6GuardMeasurementJson(A4AttenuationMeasurement measurement)
    {
        A6OriginalGuardTrace t;
        if(measurement==null||!a6GuardTraces.TryGetValue(measurement,out t))return "null";
        return A6GuardTraceJson(t);
    }

    private static string A6GuardTraceJson(A6OriginalGuardTrace t)
    {
        if(t==null)return "null";
        return "{\"source\":"+Json(t.Source)+",\"search_radius_samples\":"+t.Radius+
            ",\"preferred_guard_samples\":"+t.PreferredLength+",\"left_start\":"+t.LeftStart+",\"left_end\":"+t.LeftEnd+
            ",\"right_start\":"+t.RightStart+",\"right_end\":"+t.RightEnd+
            ",\"left_crossed_known_packets\":"+t.LeftCrossedPackets+",\"right_crossed_known_packets\":"+t.RightCrossedPackets+
            ",\"selection_uses_pair_score\":false,\"original_quality_checks_preserved\":true}";
    }
}
