using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private string a7CausalGraphJson="{\"version\":\"not_run\",\"complete\":false}";
    private List<A4Pair> A7ResolveCausalGraph(A4Context c,List<A4Pair> all,double floor,HashSet<int> visited)
    {
        var evidence=new Dictionary<A4Pair,SeventhProposalEvidence>();
        var reasons=new Dictionary<A4Pair,string>();var valid=new List<A4Pair>();
        foreach(var pair in all)
        {
            SeventhProposalEvidence measured;
            if(!a7ProposalEvidence.TryGetValue(pair,out measured))measured=A7MeasuredProposal(c,pair.OutputIncident,pair.OutputReflection,floor);
            evidence[pair]=measured;Tuple<int,int> timing;
            bool known=a7OutputTimings.TryGetValue(pair,out timing);
            double position=known?A4BoundaryOutputPosition(timing.Item2-timing.Item1,c.Fs,c.Joints,c.Velocities,c.Length):double.NaN;
            bool nominal=known&&timing.Item1>=0&&timing.Item2>timing.Item1&&timing.Item2<c.Raw.Length&&
                timing.Item2-timing.Item1<=c.RoundTripExact&&Finite(position)&&position>=0&&position<=c.Length&&
                Finite(c.Raw[timing.Item1])&&Finite(c.Raw[timing.Item2]);
            if(!nominal){reasons[pair]="invalid_nonfinite_or_outside_exact_current_timing_window";continue;}
            if(measured==null||!measured.Passed){reasons[pair]="LOC_waveform_hard_gate_failed:"+(measured==null?"missing":measured.Reason);continue;}
            if(!a7PipelineResults.ContainsKey(pair)){reasons[pair]="missing_pipeline_result_no_acceptance";continue;}
            valid.Add(pair);
        }
        // Passed Measure requires the canonical maximum of its raw 20% main
        // lobe. No index or timing is silently rewritten to make a pair fit.
        var lobes=valid.SelectMany(p=>new[]{evidence[p].Incident,evidence[p].Reflection})
            .GroupBy(l=>l.Peak).Select(g=>g.First()).OrderBy(l=>l.Peak).ToArray();
        var nodes=lobes.Select((l,id)=>new SeventhGraphNode{Id=id,Peak=l.Peak,Left=l.Left,Right=l.Right,Sign=l.Sign,
            Amplitude=l.Amplitude,Noise=l.Noise}).ToArray();
        var nodeByPeak=nodes.ToDictionary(n=>n.Peak,n=>n.Id);
        Func<A4Pair,bool> autoEligible=p=>a7PipelineResults[p].AutoAccept&&!a7ManualReviewBlocks.Contains(p)&&
            !a7UnknownCompetitorPeaks.ContainsKey(p.OutputIncident);
        // CAL may select the representative of an identical physical edge. It
        // never eliminates a DISTINCT LOC-LOC reflection hypothesis.
        var representatives=valid.GroupBy(p=>Tuple.Create(nodeByPeak[evidence[p].Incident.Peak],nodeByPeak[evidence[p].Reflection.Peak]))
            .Select(g=>g.OrderByDescending(autoEligible).ThenByDescending(p=>evidence[p].Score)
                .ThenByDescending(p=>Finite(p.Confidence)?p.Confidence:-1).ThenBy(p=>p.EdgeId).First()).ToArray();
        var represented=new HashSet<A4Pair>(representatives);
        foreach(var pair in valid.Where(p=>!represented.Contains(p)))reasons[pair]="duplicate_same_measured_packet_pair_retained_diagnostic_only";
        var graphEdges=representatives.Select(p=>
        {
            TimingAnchorUncertainty uncertainty;int[] unknown;
            bool timingReview=a7TimingUncertainties.TryGetValue(p,out uncertainty)&&uncertainty.RequiresReview;
            bool competitorReview=a7UnknownCompetitorPeaks.TryGetValue(p.OutputIncident,out unknown)&&unknown.Length>0;
            string positionReason=timingReview?"requested_timing_anchor_not_unique:"+uncertainty.Reason:"";
            if(competitorReview)positionReason+=(positionReason.Length>0?";":"")+"measured_unknown_reflection_competitor_cannot_be_excluded_position_not_unique";
            return new SeventhGraphEdge{Id=p.EdgeId,I=nodeByPeak[evidence[p].Incident.Peak],
                R=nodeByPeak[evidence[p].Reflection.Peak],Score=evidence[p].Score,Correlation=evidence[p].Correlation,
                RawCorrelation=evidence[p].RawCorrelation,AutoEligible=autoEligible(p),
                RequiresPositionReview=timingReview||competitorReview,PositionReviewReason=positionReason};
        }).ToArray();
        var waveCache=new Dictionary<long,SeventhGraphWave>();
        Func<int,int,SeventhGraphWave> compare=(a,b)=>
        {
            long key=((long)a<<32)|(uint)b;SeventhGraphWave wave;if(waveCache.TryGetValue(key,out wave))return wave;
            var measured=A7MeasuredProposal(c,nodes[a].Peak,nodes[b].Peak,floor);
            wave=new SeventhGraphWave{Passed=measured.Passed,Correlation=measured.Correlation,RawCorrelation=measured.RawCorrelation};
            waveCache[key]=wave;return wave;
        };
        bool complete=a7ProposalCatalogueComplete&&!c.AnalysisLimitReached&&!c.WindowBudgetExceeded&&!c.CandidateEvaluationLimitExceeded&&!c.CandidatePoolLimitExceeded;
        int suppression=c.Suppression<0?8:Math.Min(8,c.Suppression);
        var graph=SeventhCausalGraphMath.Resolve(nodes,graphEdges,c.RoundTripExact,suppression,compare,complete);
        var selected=new List<A4Pair>();
        foreach(var pair in all){pair.Incident.Owner=-1;pair.Reflection.Owner=-1;}
        a7IndependentSeedPairs.Clear();
        foreach(var pair in representatives)
        {
            if(graph.Echo.Contains(pair.EdgeId)){pair.State="suppressed_echo";pair.ReviewReason=graph.Reasons[pair.EdgeId];reasons[pair]=pair.ReviewReason;continue;}
            if(!graph.Selected.Contains(pair.EdgeId))
            {pair.State="alternative";string alternateReason;pair.ReviewReason=graph.Reasons.TryGetValue(pair.EdgeId,out alternateReason)?alternateReason:
                "mutual_exclusive_packet_role_alternative_not_primary_location";reasons[pair]=pair.ReviewReason;continue;}
            // Aggregate graph.Complete may be false only because a remote,
            // unrelated component exceeded its matching budget. Resolve already
            // fail-closes catalogue/causal gaps and each dependent component.
            bool review=graph.Review.Contains(pair.EdgeId)||!autoEligible(pair);
            pair.Ambiguous=graph.Review.Contains(pair.EdgeId);
            pair.State=review?"review":"accepted";
            string reason;
            if(graph.Reasons.TryGetValue(pair.EdgeId,out reason))pair.ReviewReason=reason;
            else pair.ReviewReason=review?"complete_graph_representative_LOC_hypothesis_requires_manual_confirmation":
                "complete_mutual_exclusive_graph_and_strict_CAL_evidence_passed_not_physical_truth_label";
            if(review&&Finite(pair.Confidence))pair.Confidence=Math.Min(.49,pair.Confidence);
            reasons[pair]=pair.ReviewReason;selected.Add(pair);
            // Ownership exists only for the FINAL mutually exclusive primary
            // hypothesis. It does not hide alternate graph edges or seed history.
            pair.Incident.Owner=pair.Reflection.Owner=pair.EdgeId;
        }
        c.EffectiveSuppression=suppression;c.Suppressed=graph.Echo.Count;
        c.SuppressionResolution=c.Suppression<0?"method7_auto_bounded_8_RT_orders_complete_two_endpoint_decay_evidence_only":
            "method7_requested_RT_orders_capped_8_complete_two_endpoint_decay_evidence_only";
        A7CaptureCausalDiagnostic(c,all,nodes,graphEdges,evidence,reasons,graph);
        return selected.OrderBy(p=>p.OutputIncident).ThenBy(p=>p.OutputReflection).ToList();
    }
    private string A7CausalGraphDiagnostic(A4Context c,IList<A4Pair> all,SeventhGraphNode[] nodes,SeventhGraphEdge[] edges,
        Dictionary<A4Pair,SeventhProposalEvidence> evidence,Dictionary<A4Pair,string> reasons,SeventhGraphResult graph)
    {
        var nodeByPeak=nodes.ToDictionary(n=>n.Peak,n=>n.Id);var representative=new HashSet<int>(edges.Select(e=>e.Id));
        // Presentation sets are derived only after the final physical checks and
        // publish gate. Never rewrite the solver's graph or rerun its matching.
        var published=a7ConfidenceGateFinalized?a7PublishedAfterConfidence:graph.Selected;
        var beforeConfidence=a7SelectedBeforeConfidence;
        string edgeJson=String.Join(",",all.Select(pair=>
        {
            var e=evidence[pair];Tuple<int,int> timing;bool known=a7OutputTimings.TryGetValue(pair,out timing);string reason;
            if(!reasons.TryGetValue(pair,out reason))reason="unresolved_diagnostic_candidate";
            string solverReason;
            if(a7CausalDiagnosticSnapshot==null||!a7CausalDiagnosticSnapshot.SolverReasons.TryGetValue(pair,out solverReason))
                solverReason="unresolved_diagnostic_candidate";
            int i=e!=null&&e.Incident!=null&&nodeByPeak.ContainsKey(e.Incident.Peak)?nodeByPeak[e.Incident.Peak]:-1;
            int r=e!=null&&e.Reflection!=null&&nodeByPeak.ContainsKey(e.Reflection.Peak)?nodeByPeak[e.Reflection.Peak]:-1;
            double pos=known?A4BoundaryOutputPosition(timing.Item2-timing.Item1,c.Fs,c.Joints,c.Velocities,c.Length):double.NaN;
            bool included=representative.Contains(pair.EdgeId);
            return "{\"candidate_id\":"+pair.EdgeId+",\"incident_packet\":"+i+",\"reflection_packet\":"+r+
                ",\"input_incident_peak\":"+pair.OutputIncident+",\"input_reflection_peak\":"+pair.OutputReflection+
                ",\"timing_incident\":"+(known?timing.Item1:-1)+",\"timing_reflection\":"+(known?timing.Item2:-1)+
                ",\"position_m\":"+NumberJson(pos)+",\"projected_position_m\":"+NumberJson(pair.Position)+
                ",\"amplitude_pc\":"+NumberJson(A4RawCharge(c,pair.OutputIncident))+",\"base_score\":"+NumberJson(a7PipelineResults.ContainsKey(pair)?a7PipelineResults[pair].Score:double.NaN)+
                ",\"loc_score\":"+NumberJson(e==null?double.NaN:e.Score)+",\"correlation\":"+NumberJson(e==null?double.NaN:e.Correlation)+
                ",\"raw_correlation\":"+NumberJson(e==null?double.NaN:e.RawCorrelation)+",\"waveform_passed\":"+(e!=null&&e.Passed?"true":"false")+
                ",\"representative\":"+(included?"true":"false")+",\"selected\":"+(published.Contains(pair.EdgeId)?"true":"false")+
                ",\"graph_selected_before_confidence\":"+(graph.Selected.Contains(pair.EdgeId)?"true":"false")+
                ",\"final_physical_selected_before_confidence\":"+(beforeConfidence.Contains(pair.EdgeId)?"true":"false")+
                ",\"engineering_quality\":"+A7EngineeringQualityJson(pair)+
                ",\"auto_eligible\":"+(included&&edges.First(x=>x.Id==pair.EdgeId).AutoEligible?"true":"false")+
                ",\"position_requires_review\":"+(included&&edges.First(x=>x.Id==pair.EdgeId).RequiresPositionReview?"true":"false")+
                ",\"position_uncertainty\":"+A7UncertaintyJson(pair)+
                ",\"state\":"+Json(included?pair.State:"excluded")+",\"reason\":"+Json(reason)+",\"graph_reason_before_confidence\":"+Json(solverReason)+
                ",\"conflict_candidate_ids\":"+NumberArrayJson(edges.Where(x=>x.Id!=pair.EdgeId&&(x.I==i||x.R==i||x.I==r||x.R==r)).Select(x=>(double)x.Id).ToArray())+"}";
        }));
        return "{\"version\":"+Json(SeventhCausalGraphMath.Version)+",\"complete\":"+(graph.Complete?"true":"false")+
            ",\"catalogue_complete\":"+(graph.CatalogueComplete?"true":"false")+",\"causal_scan_complete\":"+(graph.CausalComplete?"true":"false")+
            ",\"input_error\":"+Json(graph.InputError)+
            ",\"confidence_gate\":"+A7ConfidenceGateSummaryJson()+
            ",\"solver_selected_candidate_ids\":"+NumberArrayJson(graph.Selected.OrderBy(id=>id).Select(id=>(double)id).ToArray())+
            ",\"graph_selected_before_confidence\":"+NumberArrayJson(graph.Selected.OrderBy(id=>id).Select(id=>(double)id).ToArray())+
            ",\"final_physical_selected_candidate_ids_before_confidence\":"+NumberArrayJson(beforeConfidence.OrderBy(id=>id).Select(id=>(double)id).ToArray())+
            ",\"published_selected_candidate_ids\":"+NumberArrayJson(published.OrderBy(id=>id).Select(id=>(double)id).ToArray())+
            ",\"final_physical_withheld_count\":"+graph.Selected.Count(id=>!beforeConfidence.Contains(id))+
            ",\"confidence_withheld_packet_links\":"+A7ConfidenceWithheldPacketLinksJson(all)+
            ",\"completeness_scope\":\"aggregate_diagnostic_local_component_failure_does_not_veto_unrelated_complete_components\""+
            ",\"no_position_prior\":true,\"old_spatial_prior_used\":false,\"amplitude_units\":\"native_baseline_detrended_pC\","+
            "\"selection_objective\":\"bounded_maximum_total_waveform_evidence_with_review;sum_positive_edge_scores_can_favor_multiple_pairs_not_physical_truth\","+
            "\"unresolved_component_policy\":\"candidate_layer_only_no_arbitrary_matching_as_recommended_primary\","+
            "\"suppression_requested\":"+c.Suppression+",\"suppression_effective_order_limit\":"+c.EffectiveSuppression+",\"suppressed_edge_count\":"+graph.Echo.Count+
            ",\"maximum_component_nodes\":"+SeventhCausalGraphMath.MaximumComponentNodes+",\"maximum_component_edges\":"+SeventhCausalGraphMath.MaximumComponentEdges+
            ",\"maximum_component_search_steps\":"+SeventhCausalGraphMath.MaximumSearchSteps+",\"maximum_causal_steps\":"+SeventhCausalGraphMath.MaximumCausalSteps+
            ",\"maximum_packet_relation_comparisons\":"+SeventhCausalGraphMath.MaximumPacketRelationComparisons+
            ",\"nodes\":["+String.Join(",",nodes.Select(n=>"{\"packet_id\":"+n.Id+",\"peak\":"+n.Peak+",\"left\":"+n.Left+",\"right\":"+n.Right+
                ",\"sign\":"+n.Sign+",\"amplitude\":"+NumberJson(n.Amplitude)+",\"noise\":"+NumberJson(n.Noise)+"}"))+"]"+
            ",\"edges\":["+edgeJson+"],\"components\":["+String.Join(",",graph.Components.Select(x=>"{\"node_ids\":"+NumberArrayJson(x.Nodes.Select(n=>(double)n).ToArray())+
                ",\"candidate_ids\":"+NumberArrayJson(x.Edges.Select(n=>(double)n).ToArray())+",\"selected_candidate_ids\":"+NumberArrayJson(x.Selected.Where(published.Contains).Select(n=>(double)n).ToArray())+
                ",\"solver_selected_candidate_ids\":"+NumberArrayJson(x.Selected.Select(n=>(double)n).ToArray())+
                ",\"final_physical_selected_candidate_ids_before_confidence\":"+NumberArrayJson(x.Selected.Where(beforeConfidence.Contains).Select(n=>(double)n).ToArray())+
                ",\"provisional_selected_candidate_ids\":"+NumberArrayJson(x.Provisional.Select(n=>(double)n).ToArray())+
                ",\"alternative_candidate_ids\":"+NumberArrayJson(x.Alternative.Select(n=>(double)n).ToArray())+",\"complete\":"+(x.Complete?"true":"false")+
                ",\"ambiguous\":"+(x.Ambiguous?"true":"false")+",\"search_steps\":"+x.Steps+",\"best_evidence_sum\":"+NumberJson(x.Best)+
                ",\"alternative_evidence_sum\":"+NumberJson(x.Second)+",\"reason\":"+Json(x.Reason)+"}"))+"]"+
            ",\"possible_same_packet_relations\":["+String.Join(",",graph.PacketRelations.Select(x=>"{\"first_packet\":"+x.First+",\"second_packet\":"+x.Second+
                ",\"kind\":"+Json(x.Kind)+",\"complete\":"+(x.Complete?"true":"false")+",\"candidate_comparisons\":"+x.CandidateComparisons+
                ",\"role_conflict_candidate_ids\":"+NumberArrayJson(x.RoleConflictEdges.Select(id=>(double)id).ToArray())+"}"))+"]"+
            ",\"possible_duplicate_bipolar_pairs\":["+String.Join(",",graph.PossibleDuplicatePairs.Select(x=>"{\"first_candidate_id\":"+x.FirstEdge+",\"second_candidate_id\":"+x.SecondEdge+
                ",\"delay_difference_samples\":"+NumberJson(x.DelayDifference)+",\"status\":\"possible_duplicate_not_merged_without_complete_waveform_proof\"}"))+"]"+
            ",\"chains\":["+String.Join(",",graph.Chains.Select(x=>"{\"kind\":"+Json(x.Kind)+",\"source_candidate_id\":"+x.Source+",\"later_candidate_id\":"+x.Later+
                ",\"subchain_candidate_id\":"+x.Subchain+",\"whole_candidate_id\":"+x.Whole+",\"order\":"+x.Order+
                ",\"first_residual_samples\":"+NumberJson(x.FirstResidual)+",\"second_residual_samples\":"+NumberJson(x.SecondResidual)+
                ",\"first_correlation\":"+NumberJson(x.FirstCorrelation)+",\"second_correlation\":"+NumberJson(x.SecondCorrelation)+
                ",\"ratio_difference_db\":"+NumberJson(x.RatioDifferenceDb)+",\"ratio_allowance_db\":"+NumberJson(x.RatioAllowanceDb)+
                ",\"strong\":"+(x.Strong?"true":"false")+",\"suppressed\":"+(x.Suppressed?"true":"false")+
                ",\"source_published_after_confidence\":"+(published.Contains(x.Source)?"true":"false")+
                ",\"source_withheld_by_confidence\":"+(beforeConfidence.Contains(x.Source)&&!published.Contains(x.Source)?"true":"false")+
                ",\"suppression_recomputed_after_confidence\":false}"))+"]}";
    }
}
