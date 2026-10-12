using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // This second, read-only support pass never imports a strategy-4 score,
    // changes a primary graph decision, or assigns ownership to a hypothesis.
    private string a6BoundaryReviewDiagnosticJson="{\"enabled\":false,\"reason\":\"not_run\"}";
    private readonly HashSet<int> a6BoundaryReviewAssociatedReflectionIds=new HashSet<int>();

    private static A4Pulse A6BoundaryReviewClone(A4Pulse original)
    {
        A4Pulse p=A4ReviewClone(original);
        p.BoundaryRefined=original.BoundaryRefined;
        p.HasRefinedGuardProof=original.HasRefinedGuardProof;
        p.RefinedLeftGuardStart=original.RefinedLeftGuardStart;
        p.RefinedLeftGuardEnd=original.RefinedLeftGuardEnd;
        p.RefinedRightGuardStart=original.RefinedRightGuardStart;
        p.RefinedRightGuardEnd=original.RefinedRightGuardEnd;
        return p;
    }

    private static List<A4Pulse> A6BoundaryRawOccupancy(A4Context c,IList<A4Pulse> original)
    {
        // Original-sample inventory only. This invokes no matcher, CAL scorer,
        // display-band choice or location hypothesis. The same bounded raw seed
        // windows are reused solely to keep small known packets out of guards.
        A4Context raw=A4ReviewContext(c);raw.Filtered=false;raw.FirstArrival=false;
        raw.Algorithm=4;raw.AnalysisLimit=c.AnalysisLimit;
        raw.Signal=A4RawAnalysis(c);raw.Center=c.RawCenter;
        var observed=A4DiscoverRawSeedWindows(raw,new double[0],200,500);
        if(raw.WindowBudgetExceeded||raw.CandidateEvaluationLimitExceeded)return new List<A4Pulse>();
        var masks=new List<A4Pulse>();int id=original.Select(p=>p.Id).DefaultIfEmpty(-1).Max()+1;
        foreach(A4Pulse packet in observed.OrderBy(p=>p.Start).ThenBy(A4ChargeIndex))
        {
            if(packet.Clipped||packet.UnresolvedOverlap||!packet.MappingValid||packet.Start<0||packet.End>=c.Raw.Length||
                original.Any(p=>p.Start<=packet.End&&p.End>=packet.Start)||
                masks.Any(p=>p.Start<=packet.End&&p.End>=packet.Start))continue;
            A4Pulse mask=A6BoundaryReviewClone(packet);mask.Id=id++;mask.Owner=-1;mask.AttenuationMeasurement=null;
            masks.Add(mask);
        }
        return masks;
    }

    private static bool A6BoundaryHypothesisComplete(A4Context c,A4Pulse original,A4Pulse p,out string reason)
    {
        reason="complete_raw_bipolar_support_with_no_stronger_excluded_opposite_lobe";
        int peak=A4ChargeIndex(original);double[] raw=A4RawAnalysis(c);
        if(p==null||p.Start<original.Start||p.End>original.End||p.Start>=peak||p.End<=peak||
            AbsoluteMaximumIndex(raw,p.Start,p.End,c.RawCenter)!=peak)
        {reason="hypothesis_does_not_preserve_original_dominant_peak";return false;}
        double main=raw[peak]-c.RawCenter,opposite=0;
        for(int k=p.Start;k<=p.End;k++)if((raw[k]-c.RawCenter)*main<0)
            opposite=Math.Max(opposite,Math.Abs(raw[k]-c.RawCenter));
        if(opposite<Math.Max(5*c.Sigma,.10*Math.Abs(main)))
        {reason="hypothesis_complete_opposite_main_lobe_not_demonstrated";return false;}
        int lookAhead=Math.Min(original.End,peak+Math.Max(96,c.RoundTrip/4));
        for(int k=p.End+1;k<=lookAhead;k++)if((raw[k]-c.RawCenter)*main<0&&Math.Abs(raw[k]-c.RawCenter)>opposite+3*c.Sigma)
        {reason="hypothesis_stronger_opposite_lobe_outside_support";return false;}
        return true;
    }

    private void A6RecoverBoundaryReviewPairs(A4Context c,List<A4Pulse> pulses,A6Settings settings,List<A4Pair> outputs,bool quietRecovery=false)
    {
        string priorDiagnostic=a6BoundaryReviewDiagnosticJson;
        if(!quietRecovery)a6BoundaryReviewAssociatedReflectionIds.Clear();
        a6BoundaryReviewDiagnosticJson="{\"enabled\":true,\"reason\":\"no_unpaired_analyzed_incident\",\"recovered_review_pairs\":0}";
        if(c==null||c.Algorithm!=5||pulses==null||outputs==null||settings==null)
        {a6BoundaryReviewDiagnosticJson=quietRecovery?priorDiagnostic:"{\"enabled\":false,\"reason\":\"method6_context_unavailable\"}";return;}
        if(c.WindowBudgetExceeded||c.CandidateEvaluationLimitExceeded)
        {a6BoundaryReviewDiagnosticJson=quietRecovery?priorDiagnostic:"{\"enabled\":true,\"reason\":\"primary_discovery_budget_incomplete_no_extra_search\",\"recovered_review_pairs\":0}";return;}
        var visited=new HashSet<int>(c.AnalysisVisitIds);
        var represented=new HashSet<int>(outputs.Select(p=>p.Incident.Id));
        var confirmed=outputs.Where(p=>p.State=="accepted").SelectMany(p=>new[]{p.Incident,p.Reflection})
            .GroupBy(p=>p.Id).Select(g=>g.First()).ToList();
        var owned=new HashSet<int>(confirmed.Select(p=>p.Id));
        var seeds=pulses.Where(p=>!owned.Contains(p.Id)&&!represented.Contains(p.Id)&&visited.Contains(p.Id)&&
            A4ChargeIndex(p)>=0&&A4ChargeIndex(p)<c.Raw.Length&&A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold)
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex).ToList();
        if(seeds.Count==0){if(quietRecovery)a6BoundaryReviewDiagnosticJson=priorDiagnostic;return;}

        // Determine all alternative supports before measuring or ranking ANY
        // pair. Each original packet receives at most one raw-only hypothesis.
        // Unresolved packets without a proof remain in the occupancy list.
        var context=A4ReviewContext(c);
        context.ReviewQuietGuardRecovery=quietRecovery;
        if(quietRecovery)
        {
            pulses=A4ExpandReviewReflections(context,pulses,visited,represented);
            c.CandidateEvaluations=context.CandidateEvaluations;
        }
        var hypothesis=new Dictionary<int,A4Pulse>();
        var supportReasons=new Dictionary<int,string>();
        var changed=new HashSet<int>();
        foreach(A4Pulse original in pulses)
        {
            A4Pulse p=A6BoundaryReviewClone(original);
            string reason="unchanged_original_complete_support";
            if(quietRecovery&&!owned.Contains(original.Id))
            {
                A4ReviewPacket proof=A4ReviewQuietProof(context,original,pulses);reason=proof.Reason;
                if(proof.Pulse!=null&&!A4ReviewConflicts(proof.Pulse,confirmed))
                {p=proof.Pulse;changed.Add(original.Id);}
            }
            else if(!owned.Contains(original.Id)&&original.UnresolvedOverlap&&!original.Clipped)
            {
                A4ReviewPacket review=A4ReviewBoundaryHypothesis(context,original);
                reason=review.Reason;
                string completeReason;
                if(review.Pulse!=null&&review.Pulse.Start>=original.Start&&review.Pulse.End<=original.End&&
                    review.Pulse.Start<A4ChargeIndex(original)&&review.Pulse.End>A4ChargeIndex(original)&&
                    review.Pulse.End-review.Pulse.Start<=c.RoundTrip&&
                    A4ChargeIndex(review.Pulse)==A4ChargeIndex(original)&&!A4ReviewConflicts(review.Pulse,confirmed)&&
                    A6BoundaryHypothesisComplete(c,original,review.Pulse,out completeReason))
                {
                    p=review.Pulse;
                    // The 12-sample raw boundary hypothesis is NOT a measurement
                    // guard proof. The shared measurement must independently
                    // choose and validate two original guards of >=16 samples.
                    p.HasRefinedGuardProof=false;p.BoundaryRefined=true;
                    if(p.Start!=original.Start||p.End!=original.End||original.UnresolvedOverlap)changed.Add(original.Id);
                }
            }
            p.AttenuationMeasurement=null;
            hypothesis.Add(p.Id,p);supportReasons.Add(p.Id,reason);
        }
        var prepared=pulses.Select(p=>hypothesis[p.Id]).ToList();
        var occupancy=A6BoundaryRawOccupancy(c,prepared);
        var guardedPackets=prepared.Concat(occupancy).ToList();
        context.AttenuationScoringPackets=guardedPackets;
        // Both direct A6 amplitude/width measurement and unified correlation
        // must see the exact SAME frozen supports and independent caches.
        A4PrepareUnifiedCorrelationReview(context,c,guardedPackets);
        var originalLobes=new Dictionary<int,A6LobeResult>(a6Lobes);
        var originalWidths=new Dictionary<int,AstraPulseWidthDiagnostic.Measurement>(a6Widths);
        a6Lobes.Clear();a6Widths.Clear();
        var proposals=new List<A4Pair>();var attempts=new List<string>();
        int evaluated=0,recovered=0;
        int nextEdge=a6Evidence.Keys.Select(p=>p.EdgeId).DefaultIfEmpty(-1).Max()+1;
        int maxDelay=A4BoundaryReviewDelayLimit(c);
        try
        {
            foreach(A4Pulse seed in seeds)
            {
                A4Pulse a=hypothesis[seed.Id];
                if(a.UnresolvedOverlap||a.Clipped||!a.MappingValid||A4ReviewConflicts(a,confirmed))continue;
                foreach(A4Pulse b in prepared.OrderBy(A4ChargeIndex))
                {
                    int delay=A4ChargeIndex(b)-A4ChargeIndex(a);
                    if(a.Id==b.Id||owned.Contains(b.Id)||delay<=0||delay>maxDelay||a.End>=b.Start||
                        b.UnresolvedOverlap||b.Clipped||!b.MappingValid||A4ReviewConflicts(b,confirmed))continue;
                    // Complete unchanged pairs were already evaluated by A6.
                    // Repeating them with the same supports is not a rescue.
                    if(!changed.Contains(a.Id)&&!changed.Contains(b.Id)&&occupancy.Count==0)continue;
                    if(c.CandidateEvaluations+evaluated>=c.CandidateEvaluationLimit)break;
                    evaluated++;
                    var pair=new A4Pair {Incident=a,Reflection=b,OutputIncident=A4ChargeIndex(a),
                        OutputReflection=A4ChargeIndex(b),EdgeId=nextEdge++,State="review",
                        Position=A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length)};
                    A6Evidence e=A6Evaluate(context,pair,settings);
                    bool measurable=A6ReviewMeasurable(e)&&e.Correlation.Fixed>=0&&
                        Finite(pair.Position)&&Finite(pair.AmplitudeRatio)&&pair.AmplitudeRatio<=3;
                    if(attempts.Count<512)attempts.Add("{\"incident_raw_peak\":"+pair.OutputIncident+
                        ",\"reflection_raw_peak\":"+pair.OutputReflection+",\"measurable\":"+(measurable?"true":"false")+
                        ",\"incident_support_reason\":"+Json(supportReasons[a.Id])+
                        ",\"reflection_support_reason\":"+Json(supportReasons[b.Id])+
                        ",\"original_incident_measurement_reason\":"+Json(seed.AttenuationMeasurement==null?"unmeasured":seed.AttenuationMeasurement.Reason)+
                        ",\"incident_support\":["+a.Start+","+a.End+"],\"reflection_support\":["+b.Start+","+b.End+
                        "],\"raw_engineering_score_percent\":"+NumberJson(e.Score)+
                        ",\"incident_measurement_reason\":"+Json(e.A.Reason)+",\"reflection_measurement_reason\":"+Json(e.B.Reason)+
                        ",\"incident_guard\":"+A6GuardMeasurementJson(e.A)+",\"reflection_guard\":"+A6GuardMeasurementJson(e.B)+"}");
                    if(!measurable)continue;
                    bool physicalPassed=e.Eligible;
                    e.Reasons.Add("hybrid_complete_support_hypothesis_not_revalidated_in_primary_matching_graph");
                    e.Eligible=false;e.Selected=false;
                    e.Selection=physicalPassed?"hybrid_all_physical_gates_passed_but_review_only_support_hypothesis":
                        "hybrid_measurable_support_hypothesis_physical_gates_not_all_passed";
                    bool alternativeTemporal=quietRecovery&&e.Correlation.AlignmentRule.StartsWith("review_only_earliest_significant_",StringComparison.Ordinal);
                    pair.Confidence=Math.Min(alternativeTemporal?.49:.69,Math.Max(0,pair.Confidence));
                    if(alternativeTemporal)e.Reasons.Add("three_lobe_temporal_hypothesis_review_score_capped_0.49");
                    pair.ReviewReason=String.Join(";",e.Reasons.Concat(new[]{e.Selection}));
                    a6Evidence.Add(pair,e);proposals.Add(pair);
                }
            }
            foreach(A4Pulse seed in seeds)
            {
                A4Pair best=proposals.Where(p=>p.Incident.Id==seed.Id)
                    .OrderByDescending(p=>a6Evidence[p].Score).ThenBy(p=>p.OutputReflection).FirstOrDefault();
                if(best==null)continue;
                // Apply the existing method-6 spatial review preference only;
                // it can change a review choice/score, never acceptance.
                best=A6ChooseSpatialReview(c,seed,proposals,best);
                best.State="review";best.Confidence=Math.Min(a6Evidence[best].Correlation.AlignmentRule.StartsWith("review_only_earliest_significant_",StringComparison.Ordinal)?.49:.69,best.Confidence);
                outputs.Add(best);represented.Add(seed.Id);recovered++;
                a6BoundaryReviewAssociatedReflectionIds.Add(best.Reflection.Id);
                a6OrphanIncidentIds.Remove(seed.Id);
                c.AnalysisExclusions[seed.Id]="hybrid_complete_support_recovered_unaccepted_review_pair";
            }
            a6Orphans=a6OrphanIncidentIds.Count;
        }
        finally
        {
            a6Lobes.Clear();foreach(var x in originalLobes)a6Lobes.Add(x.Key,x.Value);
            a6Widths.Clear();foreach(var x in originalWidths)a6Widths.Add(x.Key,x.Value);
        }
        c.CandidateEvaluations+=evaluated;
        a6BoundaryReviewDiagnosticJson="{\"enabled\":true,\"scope\":\"post_graph_review_only_same_method6_scoring\","+
            "\"fixed16_quiet_proof_recovery\":"+(quietRecovery?"true":"false")+",\"prior_review\":"+(quietRecovery?priorDiagnostic:"null")+","+
            "\"reflection_only_inventory\":"+(quietRecovery?a4ReviewReflectionExpansionDiagnosticJson:"null")+","+
            "\"candidate_supports_chosen_before_scoring\":true,\"primary_pairs_and_ownership_unchanged\":true,"+
            "\"hypothesis_cannot_become_accepted\":true,\"review_display_score_cap_percent\":69,"+
            "\"raw_complete_support_hypotheses\":"+changed.Count+",\"extra_pair_evaluations\":"+evaluated+
            ",\"raw_occupancy_only_masks\":["+String.Join(",",occupancy.Select(p=>"{\"raw_peak\":"+A4ChargeIndex(p)+",\"start\":"+p.Start+",\"end\":"+p.End+"}"))+"],\"occupancy_masks_are_not_matching_candidates\":true"+
            ",\"recovered_review_pairs\":"+recovered+",\"associated_reflection_ids\":["+
            String.Join(",",a6BoundaryReviewAssociatedReflectionIds.OrderBy(x=>x))+"],\"attempts\":["+String.Join(",",attempts)+"]}";
    }
}
