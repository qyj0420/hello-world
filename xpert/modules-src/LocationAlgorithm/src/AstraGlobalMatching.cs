using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private const int A4MaximumCandidatePoolEdges=100000,A4MaximumComponentNodes=32,A4MaximumComponentEdges=128,A4MaximumSearchNodes=200000;
    private const int A4MaximumCandidateEvaluations=250000,A4MaximumRoleReviewSteps=250000;
    private static bool A4RecordReviewIncomplete(A4Context c)
    {return c.WindowBudgetExceeded||c.CandidatePoolLimitExceeded||c.CandidateEvaluationLimitExceeded||c.RoleReviewLimitExceeded;}
    private static bool A4TakeRoleReviewStep(A4Context c)
    {
        if(c.RoleReviewSteps>=c.RoleReviewLimit){c.RoleReviewLimitExceeded=true;return false;}
        c.RoleReviewSteps++;return true;
    }
    private const double A4GlobalStabilityMargin=.02,A4ObjectiveEpsilon=1e-12;
    private sealed class A4SearchBudget
    {
        internal int Limit=A4MaximumSearchNodes,Visited;
        internal bool Exceeded;
    }
    private sealed class A4MatchingSolution
    {
        internal double Weight;
        internal int[] EdgeIds=new int[0];
        internal bool Exact;
    }
    private sealed class A4AlternativeMatching
    {
        internal int ForbiddenEdgeId;
        internal double Weight,Gap;
        internal int[] EdgeIds=new int[0];
        internal bool Exact;
    }
    private sealed class A4MatchComponent
    {
        internal int Id,NodeCount,EdgeCount,SearchNodes,SearchNodeLimit=A4MaximumSearchNodes;
        internal double BestWeight,MinimumAlternativeGap=double.NaN;
        internal string State="pending",Reason="not_solved";
        internal int[] EdgeIds=new int[0],BestEdgeIds=new int[0];
        internal List<A4AlternativeMatching> Alternatives=new List<A4AlternativeMatching>();
    }
    private sealed class A4SolverEdge
    {
        internal int U,V,Id;
        internal double Weight;
    }
    private static A4MatchingSolution A4SolveMatchingComponent(List<A4Pair> edges,int forbiddenEdgeId,A4SearchBudget budget)
    {
        int[] nodes=edges.SelectMany(e=>new[]{e.Incident.Id,e.Reflection.Id}).Distinct().OrderBy(id=>id).ToArray();
        A4MatchingSolution best=new A4MatchingSolution();
        if(nodes.Length>A4MaximumComponentNodes||edges.Count>A4MaximumComponentEdges){return best;}
        Dictionary<int,int> local=nodes.Select((id,k)=>new {id,k}).ToDictionary(v=>v.id,v=>v.k);
        A4SolverEdge[] graph=edges.Where(e=>e.EdgeId!=forbiddenEdgeId&&e.Confidence>A4MinimumOutputConfidence)
            .Select(e=>new A4SolverEdge {U=local[e.Incident.Id],V=local[e.Reflection.Id],Id=e.EdgeId,Weight=A4StrategyWeight(e)})
            .OrderByDescending(e=>e.Weight).ThenBy(e=>e.Id).ToArray();
        A4SearchMatching(graph,nodes.Length,(1UL<<nodes.Length)-1,0,new List<int>(),best,budget);
        best.Exact=!budget.Exceeded;return best;
    }
    private static void A4SearchMatching(A4SolverEdge[] graph,int nodeCount,ulong available,double weight,List<int> chosen,
        A4MatchingSolution best,A4SearchBudget budget)
    {
        if(budget.Exceeded)return;
        if(budget.Visited>=budget.Limit){budget.Exceeded=true;return;}budget.Visited++;
        double[] maxima=new double[nodeCount];int[] degrees=new int[nodeCount];
        foreach(A4SolverEdge e in graph)
            if((available&(1UL<<e.U))!=0&&(available&(1UL<<e.V))!=0)
            {maxima[e.U]=Math.Max(maxima[e.U],e.Weight);maxima[e.V]=Math.Max(maxima[e.V],e.Weight);degrees[e.U]++;degrees[e.V]++;}
        double upper=weight+maxima.Sum()/2;
        if(upper<=best.Weight+A4ObjectiveEpsilon)return;
        int node=-1,degree=0;for(int k=0;k<nodeCount;k++)if(degrees[k]>degree){node=k;degree=degrees[k];}
        if(node<0)
        {
            if(weight>best.Weight+A4ObjectiveEpsilon){best.Weight=weight;best.EdgeIds=chosen.OrderBy(id=>id).ToArray();}return;
        }
        foreach(A4SolverEdge e in graph)
        {
            if(e.U!=node&&e.V!=node)continue;
            if((available&(1UL<<e.U))==0||(available&(1UL<<e.V))==0)continue;
            chosen.Add(e.Id);A4SearchMatching(graph,nodeCount,available&~(1UL<<e.U)&~(1UL<<e.V),weight+e.Weight,chosen,best,budget);
            chosen.RemoveAt(chosen.Count-1);if(budget.Exceeded)return;
        }
        // A packet can remain unmatched; adding a marginal edge is never rewarded merely for increasing pair count.
        A4SearchMatching(graph,nodeCount,available&~(1UL<<node),weight,chosen,best,budget);
    }
    private static void A4BidirectionalReview(List<A4Pair> pool)
    {
        foreach(var group in pool.GroupBy(e=>e.Incident.Id))
        {
            A4Pair[] ranked=group.OrderByDescending(e=>e.Confidence).ThenBy(e=>e.Reflection.Start).ThenBy(e=>e.EdgeId).ToArray();
            double margin=ranked[0].Confidence-(ranked.Length>1?ranked[1].Confidence:0);
            for(int k=0;k<ranked.Length;k++)
            {ranked[k].ForwardRank=k+1;ranked[k].ForwardBest=ranked[0].EdgeId;ranked[k].ForwardRunnerUp=ranked.Length>1?ranked[1].EdgeId:-1;ranked[k].ForwardMargin=margin;ranked[k].CandidateCount=ranked.Length;}
        }
        foreach(var group in pool.GroupBy(e=>e.Reflection.Id))
        {
            // Reverse review compares every physically possible predecessor, not the same edge with its arrays exchanged.
            A4Pair[] ranked=group.OrderByDescending(e=>e.Confidence).ThenBy(e=>e.Incident.Start).ThenBy(e=>e.EdgeId).ToArray();
            double margin=ranked[0].Confidence-(ranked.Length>1?ranked[1].Confidence:0);
            for(int k=0;k<ranked.Length;k++)
            {ranked[k].ReverseRank=k+1;ranked[k].ReverseBest=ranked[0].EdgeId;ranked[k].ReverseRunnerUp=ranked.Length>1?ranked[1].EdgeId:-1;ranked[k].ReverseMargin=margin;}
        }
        foreach(A4Pair edge in pool){edge.Reviewed=true;edge.MutualBest=edge.EdgeId==edge.ForwardBest&&edge.EdgeId==edge.ReverseBest;}
    }
    private static List<List<A4Pair>> A4CandidateComponents(List<A4Pair> edges)
    {
        Dictionary<int,List<A4Pair>> adjacency=new Dictionary<int,List<A4Pair>>();
        foreach(A4Pair edge in edges)foreach(int id in new[]{edge.Incident.Id,edge.Reflection.Id})
        {List<A4Pair> incident;if(!adjacency.TryGetValue(id,out incident)){incident=new List<A4Pair>();adjacency[id]=incident;}incident.Add(edge);}
        HashSet<int> seen=new HashSet<int>();List<List<A4Pair>> result=new List<List<A4Pair>>();
        foreach(A4Pair seed in edges.OrderBy(e=>e.EdgeId))
        {
            if(seen.Contains(seed.EdgeId))continue;Queue<A4Pair> queue=new Queue<A4Pair>();List<A4Pair> component=new List<A4Pair>();
            queue.Enqueue(seed);seen.Add(seed.EdgeId);
            while(queue.Count>0)
            {
                A4Pair edge=queue.Dequeue();component.Add(edge);
                foreach(int id in new[]{edge.Incident.Id,edge.Reflection.Id})foreach(A4Pair neighbor in adjacency[id])
                    if(seen.Add(neighbor.EdgeId))queue.Enqueue(neighbor);
            }
            result.Add(component.OrderBy(e=>e.EdgeId).ToList());
        }
        return result;
    }
    private static List<A4Pair> A4ResolveGlobalCandidates(A4Context c,List<A4Pulse> pulses,List<A4Pair> pool)
    {
        A4ResolveAutomaticSuppression(c);
        foreach(A4Pulse pulse in pulses)pulse.Owner=-1;
        pool=pool.OrderBy(e=>e.Incident.Start).ThenBy(e=>e.Reflection.Start).ThenBy(e=>e.Incident.Id).ThenBy(e=>e.Reflection.Id).ToList();
        c.CandidatePool=pool;c.MatchComponents=new List<A4MatchComponent>();
        for(int k=0;k<pool.Count;k++)
        {
            A4Pair edge=pool[k];edge.EdgeId=k;edge.ComponentId=-1;edge.GlobalEvidenceWeight=A4StrategyWeight(edge);
            edge.State="pending";edge.ReviewReason=edge.BoundaryAmbiguous?"stronger_unresolved_boundary_alternative_before_ownership":
                edge.Confidence<A4MinimumOutputConfidence?"physical_score_below_confirmation_floor":"zero_or_unproven_global_evidence";
        }
        A4BidirectionalReview(pool);
        Dictionary<int,A4LocalAmbiguity> localAmbiguities=A4LegacyLocalAmbiguities(c);
        if(!A4RecordReviewIncomplete(c))A4FindCausalEchoRoles(c,pool);
        if(A4RecordReviewIncomplete(c))
        {
            string reason=c.WindowBudgetExceeded?"record_raw_seed_window_budget_incomplete_no_owners":c.CandidateEvaluationLimitExceeded?"record_candidate_evaluation_budget_incomplete_no_owners":
                c.RoleReviewLimitExceeded?"record_role_review_budget_incomplete_no_owners":"record_candidate_pool_limit_incomplete_no_owners";
            foreach(A4Pair edge in pool)edge.ReviewReason=reason;return new List<A4Pair>();
        }
        List<A4Pair> eligible=pool.Where(e=>Finite(e.Confidence)&&e.Confidence>A4MinimumOutputConfidence&&!e.Blind&&!e.Amplified&&
            !e.Incident.UnresolvedOverlap&&!e.Reflection.UnresolvedOverlap&&!A4Touches(e,c.ConditionalEchoPulseIds)&&!A4Touches(e,c.CausalAmbiguousPulseIds)).ToList();
        foreach(List<A4Pair> componentEdges in A4CandidateComponents(eligible))
        {
            A4MatchComponent component=new A4MatchComponent {Id=c.MatchComponents.Count,EdgeCount=componentEdges.Count,
                NodeCount=componentEdges.SelectMany(e=>new[]{e.Incident.Id,e.Reflection.Id}).Distinct().Count(),EdgeIds=componentEdges.Select(e=>e.EdgeId).ToArray()};
            c.MatchComponents.Add(component);foreach(A4Pair edge in componentEdges)edge.ComponentId=component.Id;
            if(component.NodeCount>A4MaximumComponentNodes||component.EdgeCount>A4MaximumComponentEdges)
            {component.Reason="component_size_limit_no_greedy_fallback";continue;}
            A4SearchBudget budget=new A4SearchBudget();
            A4MatchingSolution best=A4SolveMatchingComponent(componentEdges,-1,budget);
            component.BestWeight=best.Weight;component.BestEdgeIds=best.EdgeIds;
            if(!best.Exact){component.SearchNodes=budget.Visited;component.Reason="exact_search_budget_exhausted";continue;}
            bool ambiguous=false;
            foreach(int selected in best.EdgeIds)
            {
                A4MatchingSolution alternative=A4SolveMatchingComponent(componentEdges,selected,budget);
                double gap=Math.Max(0,best.Weight-alternative.Weight);
                component.Alternatives.Add(new A4AlternativeMatching {ForbiddenEdgeId=selected,Weight=alternative.Weight,Gap=gap,EdgeIds=alternative.EdgeIds,Exact=alternative.Exact});
                component.MinimumAlternativeGap=Finite(component.MinimumAlternativeGap)?Math.Min(component.MinimumAlternativeGap,gap):gap;
                if(!alternative.Exact)break;
                // Removing an edge without introducing a different pairing is NOT a competing reflection.
                // Otherwise the stability check silently changes the 70% confirmation floor into 72%.
                bool hasCompetitor=alternative.EdgeIds.Any(id=>!best.EdgeIds.Contains(id));
                if(hasCompetitor&&gap<=A4GlobalStabilityMargin+A4ObjectiveEpsilon)ambiguous=true;
            }
            component.SearchNodes=budget.Visited;
            if(budget.Exceeded){component.Reason="alternative_search_budget_exhausted";continue;}
            if(componentEdges.Any(e=>best.EdgeIds.Contains(e.EdgeId)&&A4LocalAmbiguous(localAmbiguities,e.Incident.Id)))
            {component.Reason=A4LocalAmbiguityReason;continue;}
            if(ambiguous||best.EdgeIds.Length==0){component.Reason="near_equal_global_alternative";continue;}
            if(componentEdges.Any(e=>best.EdgeIds.Contains(e.EdgeId)&&e.ParallelAmbiguous))
            {component.Reason="unresolved_four_packet_physical_roles";continue;}
            component.State="proposed";component.Reason="exact_unique_global_evidence";
        }
        // Review only provisionally selected normal edges. A boundary hypothesis
        // cannot enter the exact matching graph or promote a different weak edge.
        A4ReviewBoundaryCompetition(c,pulses,c.MatchComponents.Where(g=>g.State=="proposed")
            .SelectMany(g=>g.BestEdgeIds).Select(id=>pool[id]).ToList());
        if(A4RecordReviewIncomplete(c))
        {
            foreach(A4Pair edge in pool){edge.State="pending";edge.ReviewReason="boundary_review_budget_incomplete_no_owners";}
            foreach(A4MatchComponent component in c.MatchComponents){component.State="pending";component.Reason="boundary_review_budget_incomplete_no_owners";}
            return new List<A4Pair>();
        }
        foreach(A4MatchComponent component in c.MatchComponents.Where(g=>g.State=="proposed"))
            if(component.BestEdgeIds.Any(id=>pool[id].BoundaryAmbiguous))
            {component.State="pending";component.Reason="stronger_unresolved_boundary_alternative_before_ownership";}
        // A reliable independent intervening event must be accounted for by a stable main pairing.
        // Iterate dependencies before committing anything; withdrawing one component can make another unresolved.
        bool changed;
        do
        {
            changed=false;
            HashSet<int> accounted=new HashSet<int>(c.MatchComponents.Where(g=>g.State=="proposed")
                .SelectMany(g=>g.BestEdgeIds).SelectMany(id=>new[]{pool[id].Incident.Id,pool[id].Reflection.Id}));
            foreach(A4MatchComponent component in c.MatchComponents.Where(g=>g.State=="proposed"))
                if(component.BestEdgeIds.Any(id=>pool[id].InterveningIds.Any(packet=>!accounted.Contains(packet))))
                {component.State="pending";component.Reason="unresolved_reliable_intervening_event";changed=true;}
        }while(changed);
        foreach(A4MatchComponent component in c.MatchComponents)
        {
            bool confirmed=component.State=="proposed";component.State=confirmed?"confirmed":"pending";
            foreach(int id in component.EdgeIds)
            {
                A4Pair edge=pool[id];
                if(confirmed&&component.BestEdgeIds.Contains(id)){edge.State="confirmed";edge.ReviewReason="unique_stable_global_matching";edge.Ambiguous=false;}
                else if(confirmed){edge.State="reviewed";edge.ReviewReason="not_selected_in_unique_global_matching";}
                else{edge.State="pending";edge.ReviewReason=component.Reason;edge.Ambiguous=component.Reason.Contains("alternative")||component.Reason.Contains("roles")||component.Reason.Contains("intervening")||component.Reason.Contains(A4LocalAmbiguityReason);}
            }
        }
        List<A4Pair> accepted=pool.Where(e=>e.State=="confirmed").OrderBy(e=>e.Incident.Start).ThenBy(e=>e.Reflection.Start).ToList();
        HashSet<int> finalVertices=new HashSet<int>();
        foreach(A4Pair edge in accepted)
            if(!finalVertices.Add(edge.Incident.Id)||!finalVertices.Add(edge.Reflection.Id))
                throw new InvalidOperationException("Global matching attempted to reuse a physical packet vertex.");
        // Commit only after every component and every selected endpoint has passed final validation.
        for(int k=0;k<accepted.Count;k++){accepted[k].Incident.Owner=k;accepted[k].Reflection.Owner=k;}
        A4CommitCausalEchoes(c,accepted);
        return accepted;
    }
    private static string A4CandidatePoolJson(A4Context c)
    {
        StringBuilder b=new StringBuilder();List<A4Pair> pool=c.CandidatePool;
        Dictionary<int,A4LocalAmbiguity> localAmbiguities=A4LegacyLocalAmbiguities(c);
        b.Append("{\"confirmation_score_floor\":0.70,\"objective\":\"sum(max(0,physical_score-0.70)); evidence_not_probability\",\"stability_gap\":0.02")
            .Append(",\"empty_or_subset_alternative_is_competing\":false,\"packet_vertex_roles\":\"one_node_for_both_incident_and_reflection\",\"reverse_review\":\"all_feasible_predecessors_of_each_reflection\",\"mutual_best_is_hard_gate\":false")
            .Append(",\"candidate_pool_limit\":").Append(A4MaximumCandidatePoolEdges).Append(",\"candidate_pool_complete\":").Append(A4RecordReviewIncomplete(c)?"false":"true")
            .Append(",\"candidate_evaluations_including_rejections\":").Append(c.CandidateEvaluations).Append(",\"candidate_evaluation_limit\":").Append(c.CandidateEvaluationLimit)
            .Append(",\"candidate_evaluation_limit_exceeded\":").Append(c.CandidateEvaluationLimitExceeded?"true":"false")
            .Append(",\"role_review_steps\":").Append(c.RoleReviewSteps).Append(",\"role_review_limit\":").Append(c.RoleReviewLimit)
            .Append(",\"role_review_limit_exceeded\":").Append(c.RoleReviewLimitExceeded?"true":"false")
            .Append(",\"component_node_limit\":").Append(A4MaximumComponentNodes).Append(",\"component_edge_limit\":").Append(A4MaximumComponentEdges)
            .Append(",\"search_node_budget_per_component_including_alternatives\":").Append(A4MaximumSearchNodes)
            .Append(",\"candidate_pool_count\":").Append(pool.Count).Append(",\"reviewed_count\":").Append(pool.Count(e=>e.Reviewed))
            .Append(",\"pending_count\":").Append(pool.Count(e=>e.State=="pending")).Append(",\"confirmed_count\":").Append(pool.Count(e=>e.State=="confirmed"))
            .Append(",\"reviewed_not_selected_count\":").Append(pool.Count(e=>e.State=="reviewed")).Append(",\"rejected_count\":").Append(c.RejectedPairCount)
            .Append(",\"reviewed_edge_ids\":").Append(NumberArrayJson(pool.Where(e=>e.Reviewed).Select(e=>(double)e.EdgeId).ToArray()))
            .Append(",\"pending_edge_ids\":").Append(NumberArrayJson(pool.Where(e=>e.State=="pending").Select(e=>(double)e.EdgeId).ToArray()))
            .Append(",\"confirmed_edge_ids\":").Append(NumberArrayJson(pool.Where(e=>e.State=="confirmed").Select(e=>(double)e.EdgeId).ToArray()))
            .Append(",\"reviewed_not_selected_edge_ids\":").Append(NumberArrayJson(pool.Where(e=>e.State=="reviewed").Select(e=>(double)e.EdgeId).ToArray()))
            .Append(",\"rejected_pair_details_path\":\"diagnostics.rejected_pairs\",\"owner_is_record_internal_confirmed_pair_index\":true,\"output_threshold_and_limit_applied_later\":false,\"scope\":\"bounded_raw_absolute_descending_incident_analysis\"")
            .Append(",\"review_does_not_recompute_or_increase_physical_score\":true,\"owners_committed_after_all_components\":true,\"echo_after_all_main_pair_commits\":true")
            .Append(",\"local_reflection_ambiguity_policy\":").Append(A4LocalAmbiguityPolicyJson())
            .Append(",\"causal_paired_echo_review\":").Append(A4CausalEchoJson(c)).Append(",\"candidate_pool\":[");
        for(int k=0;k<pool.Count;k++)
        {
            if(k>0)b.Append(',');A4Pair e=pool[k];
            A4LocalAmbiguity local;localAmbiguities.TryGetValue(e.Incident.Id,out local);
            b.Append("{\"edge_id\":").Append(e.EdgeId).Append(",\"incident_packet_id\":").Append(e.Incident.Id).Append(",\"reflection_packet_id\":").Append(e.Reflection.Id)
                .Append(",\"incident_onset\":").Append(e.Incident.Start).Append(",\"incident_end\":").Append(e.Incident.End)
                .Append(",\"reflection_onset\":").Append(e.Reflection.Start).Append(",\"reflection_end\":").Append(e.Reflection.End)
                .Append(",\"incident_peak\":").Append(e.Incident.RawPeak).Append(",\"reflection_peak\":").Append(e.Reflection.RawPeak)
                .Append(",\"raw_incident_charge_peak\":").Append(A4ChargeIndex(e.Incident)).Append(",\"raw_reflection_identity_peak\":").Append(A4ChargeIndex(e.Reflection))
                .Append(",\"output_incident_index\":").Append(e.OutputIncident).Append(",\"output_reflection_index\":").Append(e.OutputReflection)
                .Append(",\"output_timing_reason\":").Append(Json(e.OutputTimingReason))
                .Append(",\"physical_score\":").Append(NumberJson(e.Confidence)).Append(",\"local_reflection_ambiguity\":").Append(A4LocalAmbiguityJson(local))
                .Append(",\"signed_full_correlation\":").Append(NumberJson(e.WaveCorrelation))
                .Append(",\"measured_correlation\":").Append(NumberJson(e.Correlation)).Append(",\"unified_correlation\":").Append(A4UnifiedCorrelationJson(e.UnifiedCorrelation))
                .Append(",\"global_evidence_weight\":").Append(NumberJson(e.GlobalEvidenceWeight))
                .Append(",\"reviewed\":true,\"state\":").Append(Json(e.State)).Append(",\"reason\":").Append(Json(e.ReviewReason))
                .Append(",\"component_id\":").Append(e.ComponentId).Append(",\"forward_rank\":").Append(e.ForwardRank).Append(",\"reverse_rank\":").Append(e.ReverseRank)
                .Append(",\"forward_best_edge\":").Append(e.ForwardBest).Append(",\"forward_runner_up_edge\":").Append(e.ForwardRunnerUp)
                .Append(",\"reverse_best_edge\":").Append(e.ReverseBest).Append(",\"reverse_runner_up_edge\":").Append(e.ReverseRunnerUp)
                .Append(",\"forward_margin\":").Append(NumberJson(e.ForwardMargin)).Append(",\"reverse_margin\":").Append(NumberJson(e.ReverseMargin))
                .Append(",\"mutual_best\":").Append(e.MutualBest?"true":"false").Append(",\"claims_pulse_ownership\":").Append(e.State=="confirmed"?"true":"false")
                .Append(",\"incident_current_owner\":").Append(e.Incident.Owner).Append(",\"reflection_current_owner\":").Append(e.Reflection.Owner)
                .Append(",\"physical_confirmation\":").Append(e.State=="confirmed"?"true":"false").Append(",\"parallel_role_ambiguous\":").Append(e.ParallelAmbiguous?"true":"false")
                .Append(",\"intervening_packet_ids\":").Append(NumberArrayJson(e.InterveningIds.Select(id=>(double)id).ToArray()))
                .Append(",\"low_band_evidence\":").Append(A4LowBandPairJson(e.LowBand)).Append('}');
        }
        b.Append("],\"components\":[");
        for(int k=0;k<c.MatchComponents.Count;k++)
        {
            if(k>0)b.Append(',');A4MatchComponent g=c.MatchComponents[k];
            b.Append("{\"component_id\":").Append(g.Id).Append(",\"node_count\":").Append(g.NodeCount).Append(",\"edge_count\":").Append(g.EdgeCount)
                .Append(",\"state\":").Append(Json(g.State)).Append(",\"reason\":").Append(Json(g.Reason)).Append(",\"best_evidence_weight\":").Append(NumberJson(g.BestWeight))
                .Append(",\"minimum_forbidden_edge_gap\":").Append(NumberJson(g.MinimumAlternativeGap)).Append(",\"search_nodes\":").Append(g.SearchNodes)
                .Append(",\"search_node_limit\":").Append(g.SearchNodeLimit).Append(",\"edge_ids\":").Append(NumberArrayJson(g.EdgeIds.Select(id=>(double)id).ToArray()))
                .Append(",\"best_edge_ids\":").Append(NumberArrayJson(g.BestEdgeIds.Select(id=>(double)id).ToArray())).Append(",\"forbidden_edge_alternatives\":[");
            for(int j=0;j<g.Alternatives.Count;j++)
            {
                if(j>0)b.Append(',');A4AlternativeMatching a=g.Alternatives[j];
                b.Append("{\"forbidden_edge_id\":").Append(a.ForbiddenEdgeId).Append(",\"exact\":").Append(a.Exact?"true":"false")
                    .Append(",\"weight\":").Append(NumberJson(a.Weight)).Append(",\"gap\":").Append(NumberJson(a.Gap))
                    .Append(",\"edge_ids\":").Append(NumberArrayJson(a.EdgeIds.Select(id=>(double)id).ToArray())).Append('}');
            }
            b.Append("]}");
        }
        b.Append("]}");return b.ToString();
    }
}
