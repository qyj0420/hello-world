using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Explicit conservative engineering policy, not a calibrated probability or a
    // new feature weight. All methods keep their original physical score/objective.
    private const double A4LocalHighScorePercent=70, A4LocalScoreGapPercent=5;
    private const double A4LocalCorrelationShadowMargin=.02, A4LocalRelativeErrorShadowMargin=5;
    private const string A4LocalAmbiguityReason="local_reflection_ambiguity_multiple_high_score_packets";
    private sealed class A4LocalCandidate
    {
        internal int IncidentId,ReflectionId,ReflectionStart,ReflectionEnd,ReflectionPeak,EdgeId;
        internal double ScorePercent;
        internal bool Shadow;
        internal string Qualification;
    }
    private sealed class A4LocalAmbiguity
    {
        internal int IncidentId,FeasibleCandidateCount,HighCandidateCount,ShadowCandidateCount;
        internal bool Ambiguous;
        internal double BestScore=double.NaN,SecondScore=double.NaN,Gap=double.NaN;
        internal A4LocalCandidate[] Ranked=new A4LocalCandidate[0];
    }
    private sealed class A4LocalShadowState
    {
        // At most two distinct high-scoring packets per incident are sufficient
        // to challenge its top two; no waveform or output/history arrays are held.
        internal readonly Dictionary<int,List<A4LocalCandidate>> ByIncident=new Dictionary<int,List<A4LocalCandidate>>();
        internal readonly Dictionary<int,int> QualifiedByIncident=new Dictionary<int,int>();
        internal int Evaluated,Qualified;
    }
    private static readonly ConditionalWeakTable<A4Context,A4LocalShadowState> a4LocalShadows=
        new ConditionalWeakTable<A4Context,A4LocalShadowState>();
    private static void A4ClearLocalShadows(A4Context c)
    {if(c!=null)a4LocalShadows.Remove(c);}
    private static A4LocalCandidate A4LocalDescriptor(A4Pair pair,double score,bool shadow,string qualification)
    {
        return new A4LocalCandidate {IncidentId=pair.Incident.Id,ReflectionId=pair.Reflection.Id,
            ReflectionStart=pair.Reflection.Start,ReflectionEnd=pair.Reflection.End,
            ReflectionPeak=A4ChargeIndex(pair.Reflection),EdgeId=pair.EdgeId,ScorePercent=score,
            Shadow=shadow,Qualification=qualification};
    }
    private static A4LocalCandidate[] A4DistinctLocalPackets(IEnumerable<A4LocalCandidate> source)
    {
        var kept=new List<A4LocalCandidate>();
        foreach(var candidate in source.Where(e=>Finite(e.ScorePercent)&&e.ScorePercent>=A4LocalHighScorePercent)
            .OrderByDescending(e=>e.ScorePercent).ThenBy(e=>e.Shadow).ThenBy(e=>e.ReflectionPeak).ThenBy(e=>e.EdgeId))
        {
            // Multiple anchors/representations of one overlapping raw packet are
            // not independent reflection alternatives.
            if(kept.Any(e=>e.ReflectionId==candidate.ReflectionId||
                e.ReflectionStart<=candidate.ReflectionEnd&&candidate.ReflectionStart<=e.ReflectionEnd))continue;
            kept.Add(candidate);
        }
        return kept.ToArray();
    }
    private static Dictionary<int,A4LocalAmbiguity> A4LocalAmbiguities(IEnumerable<A4LocalCandidate> source)
    {
        var result=new Dictionary<int,A4LocalAmbiguity>();
        foreach(var group in source.GroupBy(e=>e.IncidentId))
        {
            var ranked=A4DistinctLocalPackets(group);
            var review=new A4LocalAmbiguity {IncidentId=group.Key,FeasibleCandidateCount=group.Count(),
                HighCandidateCount=ranked.Length,ShadowCandidateCount=ranked.Count(e=>e.Shadow),Ranked=ranked};
            if(ranked.Length>0)review.BestScore=ranked[0].ScorePercent;
            if(ranked.Length>1)
            {
                review.SecondScore=ranked[1].ScorePercent;review.Gap=review.BestScore-review.SecondScore;
                review.Ambiguous=review.Gap<=A4LocalScoreGapPercent+1e-9;
            }
            result[group.Key]=review;
        }
        return result;
    }
    private static bool A4LocalAmbiguous(Dictionary<int,A4LocalAmbiguity> reviews,int incidentId)
    {A4LocalAmbiguity value;return reviews.TryGetValue(incidentId,out value)&&value.Ambiguous;}
    private static string A4LocalAmbiguityJson(A4LocalAmbiguity review)
    {
        if(review==null)return "null";
        return "{\"ambiguous\":"+(review.Ambiguous?"true":"false")+",\"incident_packet_id\":"+review.IncidentId+
            ",\"qualified_candidate_representation_count\":"+review.FeasibleCandidateCount+
            ",\"distinct_high_score_packet_count\":"+review.HighCandidateCount+",\"shadow_high_score_packet_count\":"+review.ShadowCandidateCount+
            ",\"best_original_score_percent\":"+NumberJson(review.BestScore)+",\"second_original_score_percent\":"+NumberJson(review.SecondScore)+
            ",\"raw_score_gap_percentage_points\":"+NumberJson(review.Gap)+",\"threshold_percentage_points\":5,\"score_modified\":false"+
            ",\"reason\":"+Json(review.Ambiguous?A4LocalAmbiguityReason:"no_close_distinct_high_score_alternative")+
            ",\"legacy_shadow_details_keep_top_two_only_counts_are_complete\":true,\"high_score_candidates\":["+String.Join(",",review.Ranked.Select(e=>"{\"edge_id\":"+e.EdgeId+
                ",\"reflection_packet_id\":"+e.ReflectionId+",\"reflection_raw_peak\":"+e.ReflectionPeak+
                ",\"raw_support\":["+e.ReflectionStart+","+e.ReflectionEnd+"],\"score_percent\":"+NumberJson(e.ScorePercent)+
                ",\"shadow_only_never_promoted\":"+(e.Shadow?"true":"false")+",\"qualification\":"+Json(e.Qualification)+"}"))+"]}";
    }
    private static string A4LocalAmbiguityPolicyJson()
    {
        return "{\"scope\":\"same_raw_incident_distinct_nonoverlapping_reflection_packets_only\",\"high_score_floor_percent\":70,"+
            "\"top_two_gap_percentage_points\":5,\"engineering_policy_not_probability_or_label_fitted\":true,"+
            "\"preserves_original_feature_score_and_method_objective\":true,\"acceptance_only_veto\":true,"+
            "\"global_objective_margin_is_separate\":true,\"unrelated_graph_components_unaffected\":true,"+
            "\"connected_component_with_selected_local_ambiguity_is_not_committed\":true,"+
            "\"shadow_correlation_below_floor_max_percentage_points\":2,\"shadow_relative_error_above_tolerance_max_percentage_points\":5,"+
            "\"shadow_requires_all_other_physical_and_quality_gates\":true,\"shadow_never_relaxes_hard_acceptance\":true}";
    }
    private static bool A4LegacyLocalPhysical(A4Context c,A4Pulse a,A4Pulse b,out int delay)
    {
        delay=0;
        if(a==null||b==null||!a.MappingValid||!b.MappingValid||a.Clipped||b.Clipped||a.BaselineUnstable||b.BaselineUnstable||
            a.UnresolvedOverlap||b.UnresolvedOverlap||a.End>=b.Start)return false;
        int ia,ib;if(!A4CanonicalPeaks(c,a,b,out ia,out ib))return false;
        delay=ib-ia;
        if(!A4ReflectionDelayWithinWindow(c,delay)||!A4BoundaryTimingValid(c,delay)||
            delay<=Math.Max(Math.Max(a.Period,b.Period)*1.5,Math.Max(a.EquivalentSamples,b.EquivalentSamples)*2))return false;
        double ratio=b.LocalAmplitude/Math.Max(1e-30,a.LocalAmplitude);
        if(!Finite(ratio)||ratio>3||ratio>1.20+2/Math.Max(3,a.Snr))return false;
        double reliability=1-Math.Exp(-Math.Pow(Math.Max(0,Math.Min(a.Snr,b.Snr)-3)/5,2));
        return A4DetectionReliable(reliability);
    }
    private static void A4CaptureLocalShadow(A4Context c,A4Pulse a,A4Pulse b,double score,string reason,
        A4AttenuationEvidence calibration,A4UnifiedCorrelationEvidence unified)
    {
        // Called before diagnostic example truncation. Rejected candidates do not
        // enter the normal solver and never become accepted through this helper.
        if(reason!="dynamic_correlation_below_candidate_floor"&&reason!="calibration_outside_measured_tolerance")return;
        var state=a4LocalShadows.GetOrCreateValue(c);state.Evaluated++;
        if(unified==null||!unified.Available||unified.Measured==null||!unified.Measured.Valid||
            !unified.Measured.StructureCompatible||unified.Measured.OppositeTemporalPolarity||
            unified.Measured.Fixed<0||unified.Measured.Fixed+A4LocalCorrelationShadowMargin+1e-12<unified.Floor)return;
        int delay;if(!A4LegacyLocalPhysical(c,a,b,out delay))return;
        var deviation=calibration??A4CalibrationDeviation(c,a,b);
        // The shared explicit tolerance applies to shadows too. A shadow is
        // never promoted, but an out-of-band/unknown CAL must not be represented
        // as a high-quality competitor through the former auxiliary dB margin.
        if(A4CalibrationConflict(deviation))return;
        if(reason=="dynamic_correlation_below_candidate_floor")
        {
            double width=Math.Exp(-Math.Abs(Math.Log(Math.Max(.01,b.EquivalentSamples)/Math.Max(.01,a.EquivalentSamples)))*.65);
            double hf=(b.HighFraction+.002)/(a.HighFraction+.002);
            double spectrum=Math.Exp(-Math.Max(0,Math.Log(hf))*.8-Math.Abs(Math.Log(hf))*.10);
            double snr=1-Math.Exp(-Math.Pow(Math.Max(0,Math.Min(a.Snr,b.Snr)-3)/5,2));
            score=A4WeightedEvidenceScore(unified.Score,width,spectrum,snr,c.Width,deviation);
        }
        if(!Finite(score)||score<.70)return;
        var candidate=new A4LocalCandidate {IncidentId=a.Id,ReflectionId=b.Id,ReflectionStart=b.Start,ReflectionEnd=b.End,
            ReflectionPeak=A4ChargeIndex(b),EdgeId=-1,ScorePercent=100*score,Shadow=true,Qualification="legacy_boundary_shadow:"+reason};
        List<A4LocalCandidate> existing;if(!state.ByIncident.TryGetValue(a.Id,out existing))existing=new List<A4LocalCandidate>();
        existing.Add(candidate);state.ByIncident[a.Id]=A4DistinctLocalPackets(existing).Take(2).ToList();state.Qualified++;
        int count;state.QualifiedByIncident.TryGetValue(a.Id,out count);state.QualifiedByIncident[a.Id]=count+1;
    }
    private static Dictionary<int,A4LocalAmbiguity> A4LegacyLocalAmbiguities(A4Context c)
    {
        var descriptors=c.CandidatePool.Where(e=>Finite(e.Confidence)&&!e.Blind&&!e.Amplified)
            .Select(e=>A4LocalDescriptor(e,100*e.Confidence,false,"legacy_complete_physical_candidate_pool")).ToList();
        A4LocalShadowState state=null;
        bool hasShadows=c.RejectedPairCount>0&&a4LocalShadows.TryGetValue(c,out state);
        if(hasShadows)descriptors.AddRange(state.ByIncident.Values.SelectMany(v=>v));
        var reviews=A4LocalAmbiguities(descriptors);
        if(hasShadows)
            foreach(var counts in state.QualifiedByIncident)
            {
                A4LocalAmbiguity review;if(!reviews.TryGetValue(counts.Key,out review))continue;
                int omitted=counts.Value-state.ByIncident[counts.Key].Count;
                review.FeasibleCandidateCount+=omitted;review.HighCandidateCount+=omitted;review.ShadowCandidateCount+=omitted;
            }
        return reviews;
    }
    private static A4Pair A4LocalOriginalReviewPair(A4Context c,A4Pulse a,A4Pulse b)
    {
        return c.CandidatePool.FirstOrDefault(p=>p.Incident.Id==a.Id&&p.Reflection.Id==b.Id&&
            p.Incident.Start==a.Start&&p.Incident.End==a.End&&p.Reflection.Start==b.Start&&p.Reflection.End==b.End&&
            Finite(p.Confidence)&&p.Confidence>=.70&&p.ReviewReason!=null&&p.ReviewReason.Contains(A4LocalAmbiguityReason));
    }
}
