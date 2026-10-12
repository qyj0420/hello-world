using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.SignalEvidence
{
    // No position, cable-length prior, training label or CAL score enters this
    // solver. Nodes are measured physical main lobes; edges are hard-gate passed
    // LOC-LOC waveform hypotheses. Selection is not automatic physical truth.
    internal sealed class SeventhGraphNode
    {
        internal int Id,Peak,Left,Right,Sign;
        internal double Amplitude,Noise;
    }
    internal sealed class SeventhGraphEdge
    {
        internal int Id,I,R;
        internal double Score,Correlation,RawCorrelation;
        internal bool AutoEligible,RequiresPositionReview;
        internal string PositionReviewReason="";
    }
    internal sealed class SeventhGraphPacketRelation
    {
        internal int First,Second;
        internal bool Complete=true;
        internal long CandidateComparisons;
        internal string Kind="opposite_polarity_main_lobes_touch_without_quiet_gap_possible_same_packet_not_merged";
        internal int[] RoleConflictEdges=new int[0];
    }
    internal sealed class SeventhGraphPossibleDuplicate
    {
        internal int FirstEdge,SecondEdge;
        internal double DelayDifference;
    }
    internal sealed class SeventhGraphWave
    {
        internal bool Passed;
        internal double Correlation,RawCorrelation;
    }
    internal sealed class SeventhGraphChain
    {
        internal string Kind;
        internal int Source=-1,Later=-1,Subchain=-1,Whole=-1,Order;
        internal double FirstResidual,SecondResidual,FirstCorrelation,SecondCorrelation,RatioDifferenceDb,RatioAllowanceDb;
        internal bool Strong,Suppressed;
    }
    internal sealed class SeventhGraphComponent
    {
        internal int[] Nodes,Edges,Selected=new int[0],Alternative=new int[0],Provisional=new int[0];
        internal bool Complete=true,Ambiguous;
        internal int Steps;
        internal double Best=double.NaN,Second=double.NaN;
        internal string Reason="complete_component";
    }
    internal sealed class SeventhGraphResult
    {
        // Complete is an aggregate coverage diagnostic, NOT an all-record veto.
        // Catalogue/causal incompleteness can hide cross-component competitors;
        // a bounded matching failure is local to that component and its actual
        // causal dependencies, not to unrelated waveform episodes.
        internal bool Complete=true,CatalogueComplete=true,CausalComplete=true;
        internal string InputError="";
        internal readonly HashSet<int> Selected=new HashSet<int>(),Review=new HashSet<int>(),Echo=new HashSet<int>();
        internal readonly Dictionary<int,string> Reasons=new Dictionary<int,string>();
        internal readonly List<SeventhGraphChain> Chains=new List<SeventhGraphChain>();
        internal readonly List<SeventhGraphComponent> Components=new List<SeventhGraphComponent>();
        internal readonly List<SeventhGraphPacketRelation> PacketRelations=new List<SeventhGraphPacketRelation>();
        internal readonly List<SeventhGraphPossibleDuplicate> PossibleDuplicatePairs=new List<SeventhGraphPossibleDuplicate>();
    }
    internal static class SeventhCausalGraphMath
    {
        internal const string Version="2026.10.01-method7-bounded-causal-packet-graph-v4-complete-episode-role-evidence";
        internal const int MaximumComponentNodes=14,MaximumComponentEdges=32,MaximumSearchSteps=20000,MaximumCausalSteps=200000;
        internal const int MaximumPacketRelationComparisons=4096;
        static bool Finite(double x){return !double.IsNaN(x)&&!double.IsInfinity(x);}
        static bool Decays(SeventhGraphNode a,SeventhGraphNode b)
        {return Finite(a.Amplitude)&&Finite(b.Amplitude)&&Finite(a.Noise)&&Finite(b.Noise)&&a.Amplitude-b.Amplitude>2*Math.Sqrt(a.Noise*a.Noise+b.Noise*b.Noise);}
        static bool Wave(SeventhGraphWave w)
        {return w!=null&&w.Passed&&Finite(w.Correlation)&&w.Correlation>=.85&&w.Correlation<=1+1e-12&&
            Finite(w.RawCorrelation)&&w.RawCorrelation>=.60&&w.RawCorrelation<=1+1e-12;}
        static bool PassedWave(SeventhGraphWave w)
        {return w!=null&&w.Passed&&Finite(w.Correlation)&&w.Correlation>0&&w.Correlation<=1+1e-12&&
            Finite(w.RawCorrelation)&&w.RawCorrelation>0&&w.RawCorrelation<=1+1e-12;}
        static bool EdgeWave(SeventhGraphEdge e)
        {return e.Correlation>=.85&&e.RawCorrelation>=.60;}
        static void Review(SeventhGraphResult r,int id,string reason)
        {
            if(id<0)return;r.Review.Add(id);string previous;
            // A later presentation policy must not erase the measured cause.
            r.Reasons[id]=!r.Reasons.TryGetValue(id,out previous)?reason:
                previous.Split(';').Contains(reason)?previous:previous+";"+reason;
        }
        internal static SeventhGraphResult Resolve(IList<SeventhGraphNode> nodes,IList<SeventhGraphEdge> edges,
            double exactRoundTrip,int suppression,Func<int,int,SeventhGraphWave> compare,bool catalogueComplete)
        {
            var result=new SeventhGraphResult {Complete=catalogueComplete,CatalogueComplete=catalogueComplete};
            if(nodes==null||edges==null||!Finite(exactRoundTrip)||exactRoundTrip<=0||compare==null)
            {result.Complete=result.CatalogueComplete=result.CausalComplete=false;result.InputError="invalid_required_graph_input";return result;}
            if(nodes.Any(n=>n==null||n.Id<0||n.Peak<0||n.Left>n.Peak||n.Right<n.Peak||(n.Sign!=-1&&n.Sign!=1)||
                !Finite(n.Amplitude)||n.Amplitude<=0||!Finite(n.Noise)||n.Noise<0)||
                nodes.Select(n=>n.Id).Distinct().Count()!=nodes.Count||nodes.Select(n=>n.Peak).Distinct().Count()!=nodes.Count)
            {result.Complete=result.CatalogueComplete=result.CausalComplete=false;result.InputError="invalid_or_duplicate_physical_packet_catalogue";return result;}
            var map=nodes.ToDictionary(n=>n.Id);
            if(edges.Any(e=>e==null||e.Id<0||!map.ContainsKey(e.I)||!map.ContainsKey(e.R)||e.I==e.R||map[e.I].Peak>=map[e.R].Peak||
                !Finite(e.Score)||e.Score<=0||e.Score>1+1e-12||!Finite(e.Correlation)||e.Correlation<=0||e.Correlation>1+1e-12||
                !Finite(e.RawCorrelation)||e.RawCorrelation< -1||e.RawCorrelation>1+1e-12)||edges.Select(e=>e.Id).Distinct().Count()!=edges.Count)
            {result.Complete=result.CatalogueComplete=result.CausalComplete=false;result.InputError="invalid_or_duplicate_LOC_edge_catalogue_no_false_unique_selection";return result;}
            var valid=edges.ToArray();
            foreach(var edge in valid.Where(e=>e.RequiresPositionReview))
                Review(result,edge.Id,String.IsNullOrEmpty(edge.PositionReviewReason)?"local_timing_or_unresolved_waveform_competitor_position_not_unique":edge.PositionReviewReason);
            // Adjacent, opposite-polarity lobes whose measured supports touch
            // may be two halves of ONE bipolar wave packet. Keep both measured
            // extrema; this is a role-ambiguity relation, not permanent merging
            // or a short-cable rejection. Only I/R reuse creates a conflict.
            var orderedNodes=nodes.OrderBy(n=>n.Peak).ToArray();
            var possiblePacketNeighbours=new HashSet<long>();
            Func<int,int,long> packetKey=(a,b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
            for(int k=1;k<orderedNodes.Length;k++)
            {
                var a=orderedNodes[k-1];var b=orderedNodes[k];
                if(a.Sign==b.Sign||a.Right<b.Left)continue;
                result.PacketRelations.Add(new SeventhGraphPacketRelation{First=a.Id,Second=b.Id});
                possiblePacketNeighbours.Add(packetKey(a.Id,b.Id));
            }
            Func<int,int,bool> samePossiblePacket=(a,b)=>a==b||possiblePacketNeighbours.Contains(packetKey(a,b));
            var byIncident=valid.GroupBy(e=>e.I).ToDictionary(g=>g.Key,g=>g.ToArray());
            var byReflection=valid.GroupBy(e=>e.R).ToDictionary(g=>g.Key,g=>g.ToArray());
            foreach(var relation in result.PacketRelations)
            {
                var conflicts=new HashSet<int>();
                SeventhGraphEdge[] incidentFirst,incidentSecond,reflectionFirst,reflectionSecond;
                if(!byIncident.TryGetValue(relation.First,out incidentFirst))incidentFirst=new SeventhGraphEdge[0];
                if(!byIncident.TryGetValue(relation.Second,out incidentSecond))incidentSecond=new SeventhGraphEdge[0];
                if(!byReflection.TryGetValue(relation.First,out reflectionFirst))reflectionFirst=new SeventhGraphEdge[0];
                if(!byReflection.TryGetValue(relation.Second,out reflectionSecond))reflectionSecond=new SeventhGraphEdge[0];
                relation.CandidateComparisons=(long)reflectionFirst.Length*incidentSecond.Length+
                    (long)reflectionSecond.Length*incidentFirst.Length+(long)incidentFirst.Length*incidentSecond.Length;
                if(relation.CandidateComparisons>MaximumPacketRelationComparisons)
                {
                    // Bound this new cross-polarity audit without clearing remote
                    // complete episodes, and without accepting a prefix winner.
                    relation.Complete=false;result.Complete=false;
                    foreach(var edge in incidentFirst.Concat(incidentSecond).Concat(reflectionFirst).Concat(reflectionSecond))
                    {conflicts.Add(edge.Id);Review(result,edge.Id,"possible_bipolar_packet_relation_budget_incomplete_local_roles_unresolved");}
                    relation.RoleConflictEdges=conflicts.OrderBy(id=>id).ToArray();continue;
                }
                foreach(var direction in new[]{Tuple.Create(relation.First,relation.Second),Tuple.Create(relation.Second,relation.First)})
                {
                    SeventhGraphEdge[] incoming,leaving;
                    if(!byReflection.TryGetValue(direction.Item1,out incoming)||!byIncident.TryGetValue(direction.Item2,out leaving))continue;
                    foreach(var before in incoming)foreach(var after in leaving)
                    {
                        if(before.Id==after.Id)continue;
                        bool samePair=samePossiblePacket(before.I,after.I)&&samePossiblePacket(before.R,after.R)&&
                            Math.Abs((map[before.R].Peak-map[before.I].Peak)-(map[after.R].Peak-map[after.I].Peak))<=2;
                        if(samePair)continue;
                        conflicts.Add(before.Id);conflicts.Add(after.Id);
                        Review(result,before.Id,"possible_bipolar_packet_reused_as_reflection_and_new_incident_position_unresolved");
                        Review(result,after.Id,"possible_bipolar_packet_reused_as_reflection_and_new_incident_position_unresolved");
                    }
                }
                relation.RoleConflictEdges=conflicts.OrderBy(id=>id).ToArray();
                // Equal-delay two-polarity corresponding pairs are reported as
                // possible duplicate event representations, NOT banned merely
                // for being bipolar. Full waveform proof is needed to merge.
                SeventhGraphEdge[] left,right;
                if(!byIncident.TryGetValue(relation.First,out left)||!byIncident.TryGetValue(relation.Second,out right))continue;
                foreach(var a in left)foreach(var b in right)
                {
                    double difference=(map[a.R].Peak-map[a.I].Peak)-(map[b.R].Peak-map[b.I].Peak);
                    if(samePossiblePacket(a.R,b.R)&&Math.Abs(difference)<=2)
                        result.PossibleDuplicatePairs.Add(new SeventhGraphPossibleDuplicate{FirstEdge=a.Id,SecondEdge=b.Id,DelayDifference=difference});
                }
            }
            var outgoing=valid.GroupBy(e=>e.I).ToDictionary(g=>g.Key,g=>g.ToArray());
            var chronological=valid.OrderBy(e=>map[e.I].Peak).ThenBy(e=>e.Id).ToArray();
            var chronologicalStarts=chronological.Select(e=>map[e.I].Peak).ToArray();
            double tolerance=Math.Max(4,Math.Min(8,.01*exactRoundTrip));int causalSteps=0;bool causalComplete=true;
            var tripletReasons=new Dictionary<SeventhGraphChain,string>();
            // Three waves alone identify a competing role explanation, NEVER a
            // unique source or sufficient suppression proof. A/B and B/C already
            // passed the full LOC waveform gate; remeasure them independently,
            // and require a STRONG A/C recurrence at a complete RT order. The
            // extra suppression-strength .85 correlation and amplitude decay
            // conditions are NOT prerequisites for discovering a competing role:
            // coupling can make a reflected packet larger than an incident one.
            // A weak/growing three-wave episode remains unresolved; it never
            // enters the four-wave complete-pair echo suppression proof below.
            foreach(var ab in valid)
            {
                SeventhGraphEdge[] continuations;if(!outgoing.TryGetValue(ab.R,out continuations))continue;
                foreach(var bc in continuations)
                {
                    if(++causalSteps>MaximumCausalSteps){causalComplete=false;break;}
                    var a=map[ab.I];var b=map[ab.R];var c=map[bc.R];
                    double span=c.Peak-a.Peak;int order=(int)Math.Round(span/exactRoundTrip);
                    if(order<1||order>8||Math.Abs(span-order*exactRoundTrip)>tolerance)continue;
                    var ac=compare(a.Id,c.Id);if(!Wave(ac))continue;
                    var measuredAb=compare(a.Id,b.Id);var measuredBc=compare(b.Id,c.Id);
                    if(!PassedWave(measuredAb)||!PassedWave(measuredBc))continue;
                    bool strongEdges=EdgeWave(ab)&&EdgeWave(bc)&&Wave(measuredAb)&&Wave(measuredBc);
                    bool monotoneDecay=Decays(a,b)&&Decays(b,c);
                    bool strictThreeWave=strongEdges&&monotoneDecay;
                    string kind=strictThreeWave?"three_wave_RT_competing_role_not_unique_truth":
                        !strongEdges&&!monotoneDecay?"three_wave_RT_remeasured_passed_edges_below_suppression_strength_and_nondecaying_competing_roles":
                        !strongEdges?"three_wave_RT_remeasured_passed_edges_below_suppression_strength_competing_roles":
                        "three_wave_RT_remeasured_nondecaying_competing_roles";
                    var whole=valid.FirstOrDefault(e=>e.I==a.Id&&e.R==c.Id);
                    var triplet=new SeventhGraphChain{Kind=kind,Source=ab.Id,Subchain=bc.Id,
                        Whole=whole==null?-1:whole.Id,Order=order,FirstResidual=span-order*exactRoundTrip,
                        FirstCorrelation=ac.Correlation,SecondCorrelation=Math.Min(Math.Min(ab.Correlation,bc.Correlation),
                            Math.Min(measuredAb.Correlation,measuredBc.Correlation)),Strong=strictThreeWave};
                    result.Chains.Add(triplet);
                    string roleReason=strictThreeWave?"three_wave_roundtrip_competing_causal_role_requires_review":
                        kind+"_not_echo_suppression_proof";
                    // Retain the measured conflict, but classify it only after
                    // the complete episode has been audited. A weaker INTERNAL
                    // B/C bridge must not erase independently proved A/B -> C/D
                    // recurrence merely because two shorter triplets exist.
                    tripletReasons[triplet]=roleReason;
                }
                if(!causalComplete)break;
            }
            // Complete two-wave recurrence must reproduce BOTH endpoints at the
            // SAME RT order and have noise-resolved, mutually consistent decay.
            var full=new List<SeventhGraphChain>();
            if(causalComplete)foreach(var source in valid)
            {
                if(!EdgeWave(source))continue;
                var a=map[source.I];var b=map[source.R];
                // Index the physically possible RT-order interval BEFORE charging
                // the causal budget. Far-away unrelated events are not candidates.
                double first=a.Peak+exactRoundTrip-tolerance,last=a.Peak+8*exactRoundTrip+tolerance;
                for(int index=LowerBound(chronologicalStarts,first);index<chronological.Length&&chronologicalStarts[index]<=last;index++)
                {
                    var later=chronological[index];if(!EdgeWave(later)||map[later.I].Peak<=b.Peak)continue;
                    if(++causalSteps>MaximumCausalSteps){causalComplete=false;break;}
                    var c=map[later.I];var d=map[later.R];int order=(int)Math.Round((c.Peak-a.Peak)/exactRoundTrip);
                    if(order<1||order>8||Math.Abs(c.Peak-a.Peak-order*exactRoundTrip)>tolerance||
                        Math.Abs(d.Peak-b.Peak-order*exactRoundTrip)>tolerance||!Decays(a,c)||!Decays(b,d))continue;
                    var ac=compare(a.Id,c.Id);var bd=compare(b.Id,d.Id);if(!Wave(ac)||!Wave(bd))continue;
                    double ra=c.Amplitude/a.Amplitude,rb=d.Amplitude/b.Amplitude;
                    if(!Finite(ra)||!Finite(rb)||ra<=0||rb<=0)continue;
                    double difference=Math.Abs(20*Math.Log10(ra/rb));
                    double allowance=3+20*Math.Log10(1+2*Math.Sqrt(a.Noise*a.Noise+c.Noise*c.Noise)/c.Amplitude+
                        2*Math.Sqrt(b.Noise*b.Noise+d.Noise*d.Noise)/d.Amplitude);
                    if(!Finite(difference)||!Finite(allowance)||difference>allowance)continue;
                    full.Add(new SeventhGraphChain{Kind="complete_pair_same_RT_order_consistent_decay",Source=source.Id,Later=later.Id,Order=order,
                        FirstResidual=c.Peak-a.Peak-order*exactRoundTrip,SecondResidual=d.Peak-b.Peak-order*exactRoundTrip,
                        FirstCorrelation=ac.Correlation,SecondCorrelation=bd.Correlation,RatioDifferenceDb=difference,RatioAllowanceDb=allowance,Strong=true});
                }
                if(!causalComplete)break;
            }
            var uniqueRecurrences=new List<SeventhGraphChain>();
            if(causalComplete)foreach(var family in full.GroupBy(x=>x.Later))
            {
                var unique=family.GroupBy(x=>x.Source).Select(g=>g.First()).ToArray();
                if(unique.Length!=1)
                {foreach(var claim in unique){Review(result,claim.Source,"multiple_earlier_sources_explain_repeated_pair");Review(result,claim.Later,"multiple_earlier_sources_explain_repeated_pair");}continue;}
                // Do not turn a conditional child role into an intrinsic veto
                // of its own parent. In a connected AB/BC/CD episode, marking CD
                // Review here used to clear AB before AB could prove CD's repeat.
                // Resolve dependencies AFTER intrinsic matching competition.
                uniqueRecurrences.Add(unique[0]);
            }
            result.Chains.AddRange(full);
            result.CausalComplete=causalComplete;
            if(!causalComplete){result.Complete=false;foreach(var e in valid)Review(result,e.Id,"causal_graph_budget_incomplete_no_unique_acceptance");result.Echo.Clear();}
            var available=valid;
            var adjacency=available.SelectMany(e=>new[]{Tuple.Create(e.I,e),Tuple.Create(e.R,e)})
                .GroupBy(x=>x.Item1).ToDictionary(g=>g.Key,g=>g.Select(x=>x.Item2).ToArray());
            var remaining=new HashSet<int>(available.SelectMany(e=>new[]{e.I,e.R}));
            while(remaining.Count>0)
            {
                int initial=remaining.Min();var ids=new HashSet<int>{initial};var pending=new Queue<int>();pending.Enqueue(initial);
                var connected=new Dictionary<int,SeventhGraphEdge>();
                while(pending.Count>0){int node=pending.Dequeue();foreach(var e in adjacency[node])
                    {connected[e.Id]=e;if(ids.Add(e.I))pending.Enqueue(e.I);if(ids.Add(e.R))pending.Enqueue(e.R);}}
                remaining.ExceptWith(ids);var componentEdges=connected.Values.OrderBy(e=>e.Id).ToArray();
                var component=Solve(ids.OrderBy(x=>map[x].Peak).ToArray(),componentEdges,map);
                result.Components.Add(component);if(!component.Complete)result.Complete=false;
                foreach(int id in component.Selected)result.Selected.Add(id);
                foreach(int id in component.Selected)
                {
                    var edge=componentEdges.First(e=>e.Id==id);
                    bool strongCompetitor=componentEdges.Any(other=>other.Id!=id&&(other.I==edge.I||other.R==edge.I||other.I==edge.R||other.R==edge.R)&&
                        other.Correlation>=edge.Correlation-.05&&other.Score>=edge.Score-.08);
                    if(!component.Complete||component.Ambiguous||strongCompetitor)
                        Review(result,id,!component.Complete?component.Reason:strongCompetitor?"strong_LOC_competitor_retained_despite_CAL_or_global_score_difference":"near_equal_complete_component_explanations");
                }
            }
            if(!catalogueComplete)foreach(var e in valid)Review(result,e.Id,"incident_or_candidate_catalogue_incomplete_no_unique_acceptance");
            // Maximum-sum binary matching does not explain a whole echo episode:
            // it can reward splitting the same wave sequence into more events.
            // Do NOT publish that arbitrary matching as a recommended location.
            // Until a unique episode hypothesis exists, preserve every edge in
            // the candidate layer and publish NO primary pair for this component.
            var componentByEdge=new Dictionary<int,SeventhGraphComponent>();
            foreach(var component in result.Components)foreach(int id in component.Edges)componentByEdge[id]=component;
            var byId=valid.ToDictionary(e=>e.Id);
            var recurringChildren=new HashSet<int>(full.Select(x=>x.Later));
            var fullRecurrenceParticipants=new HashSet<int>(full.SelectMany(x=>new[]{x.Source,x.Later}));
            var strongTripletSources=new HashSet<int>(tripletReasons.Keys.Where(x=>x.Strong).Select(x=>x.Source));
            var explainedBridges=new Dictionary<int,SeventhGraphChain>();
            int explanationSteps=0;
            if(catalogueComplete&&causalComplete&&suppression>0)
                foreach(var recurrence in uniqueRecurrences)
                {
                    if(recurrence.Order>suppression||recurringChildren.Contains(recurrence.Source))continue;
                    SeventhGraphComponent sourceComponent,laterComponent;
                    if(!componentByEdge.TryGetValue(recurrence.Source,out sourceComponent)||
                        !componentByEdge.TryGetValue(recurrence.Later,out laterComponent)||sourceComponent!=laterComponent||
                        !sourceComponent.Complete||sourceComponent.Ambiguous||sourceComponent.Edges.Length!=3||
                        !result.Selected.Contains(recurrence.Source)||!result.Selected.Contains(recurrence.Later)||
                        sourceComponent.Edges.Any(id=>result.Review.Contains(id)))continue;
                    var source=byId[recurrence.Source];var later=byId[recurrence.Later];
                    int bridgeId=sourceComponent.Edges.Single(id=>id!=source.Id&&id!=later.Id);
                    var bridge=byId[bridgeId];
                    // This is a closed, four-node, three-edge episode only. A
                    // complete catalogue containing an additional endpoint or
                    // external role hypothesis is NOT reduced to this pattern.
                    if(sourceComponent.Nodes.Length!=4||bridge.I!=source.R||bridge.R!=later.I||EdgeWave(bridge)||
                        fullRecurrenceParticipants.Contains(bridge.Id)||
                        sourceComponent.Edges.Any(id=>strongTripletSources.Contains(id)))continue;
                    // full contains suppression-strength recurrences only, so
                    // membership there cannot rule out a repeated WEAKER B/C
                    // alternative in another component. For this exception to
                    // ordinary ambiguity handling, require that no other passed
                    // edge even has its complete two-endpoint RT geometry. Such
                    // an external candidate is not declared true or suppressed;
                    // it simply prevents release of the existing role conflict.
                    bool externalOrUnexamined=false;
                    double lower=map[bridge.I].Peak-8*exactRoundTrip-tolerance;
                    double upper=map[bridge.I].Peak+8*exactRoundTrip+tolerance;
                    for(int index=LowerBound(chronologicalStarts,lower);index<chronological.Length&&chronologicalStarts[index]<=upper;index++)
                    {
                        if(++explanationSteps>MaximumCausalSteps){externalOrUnexamined=true;break;}
                        var other=chronological[index];
                        if(other.Id==source.Id||other.Id==bridge.Id||other.Id==later.Id)continue;
                        int repeatOrder=(int)Math.Round((map[other.I].Peak-map[bridge.I].Peak)/exactRoundTrip);
                        if(repeatOrder!=0&&repeatOrder>=-8&&repeatOrder<=8&&
                            Math.Abs(map[other.I].Peak-map[bridge.I].Peak-repeatOrder*exactRoundTrip)<=tolerance&&
                            Math.Abs(map[other.R].Peak-map[bridge.R].Peak-repeatOrder*exactRoundTrip)<=tolerance)
                        {externalOrUnexamined=true;break;}
                    }
                    if(externalOrUnexamined)
                    {
                        Review(result,bridge.Id,explanationSteps>MaximumCausalSteps?
                            "complete_episode_exception_scan_budget_no_release_of_existing_role_ambiguity":
                            "weaker_bridge_has_external_complete_RT_geometry_no_release_of_existing_role_ambiguity");
                        continue;
                    }
                    explainedBridges[bridge.Id]=recurrence;
                }
            foreach(var item in tripletReasons)
            {
                var triplet=item.Key;SeventhGraphChain explanation=null;int bridge=-1;
                foreach(int candidate in new[]{triplet.Source,triplet.Subchain})
                {
                    SeventhGraphChain recurrence;if(!explainedBridges.TryGetValue(candidate,out recurrence))continue;
                    if(!triplet.Strong&&triplet.Whole<0&&
                        ((triplet.Source==recurrence.Source&&triplet.Subchain==candidate)||
                         (triplet.Source==candidate&&triplet.Subchain==recurrence.Later)))
                    {explanation=recurrence;bridge=candidate;break;}
                }
                if(explanation!=null)
                {
                    triplet.Kind+="_explained_by_strict_complete_pair_recurrence";
                    result.Reasons[bridge]="weaker_internal_bridge_explained_by_strict_complete_pair_recurrence_retained_as_candidate_not_deleted";
                    continue;
                }
                Review(result,triplet.Source,item.Value);
                Review(result,triplet.Subchain,item.Value+";later_reflection_may_be_new_incident_of_same_three_wave_episode");
                if(triplet.Whole>=0)Review(result,triplet.Whole,"full_roundtrip_repeat_competes_with_endpoint_pair_interpretation");
            }
            var blocked=new HashSet<SeventhGraphComponent>(result.Components.Where(component=>
                !catalogueComplete||!causalComplete||!component.Complete||component.Ambiguous||component.Edges.Any(id=>result.Review.Contains(id))));
            var possibleEchoChildren=new HashSet<int>(full.Select(chain=>chain.Later));
            Func<SeventhGraphChain,bool> canSuppress=chain=>suppression>0&&chain.Order<=suppression&&catalogueComplete&&causalComplete&&
                result.Selected.Contains(chain.Source)&&!result.Review.Contains(chain.Source)&&!possibleEchoChildren.Contains(chain.Source)&&
                componentByEdge.ContainsKey(chain.Source)&&componentByEdge.ContainsKey(chain.Later)&&
                !blocked.Contains(componentByEdge[chain.Source])&&componentByEdge[chain.Source].Complete&&
                componentByEdge[chain.Later].Complete;
            // A source which is itself an unresolved repeated-pair child is not
            // independent proof. Propagate unresolved child roles to their actual
            // packet component, then re-evaluate parents until the finite set of
            // blocked components stabilizes. This avoids both circular proof and
            // a stale suppression whose parent was cleared by a later dependency.
            bool changed;
            do
            {
                changed=false;
                foreach(var chain in uniqueRecurrences)
                    if(!canSuppress(chain)&&componentByEdge.ContainsKey(chain.Later))
                        changed|=blocked.Add(componentByEdge[chain.Later]);
            }while(changed);
            foreach(var chain in uniqueRecurrences.Where(chain=>!canSuppress(chain)))
                Review(result,chain.Later,"complete_pair_RT_echo_role_depends_on_final_independent_source_hypothesis");
            foreach(var component in result.Components)
            {
                if(!blocked.Contains(component))continue;
                component.Provisional=component.Selected;result.Selected.ExceptWith(component.Selected);component.Selected=new int[0];
                foreach(int id in component.Edges)Review(result,id,"whole_component_or_causal_episode_unresolved_candidate_layer_only_not_recommended_primary");
                component.Reason+=";unresolved_episode_or_competition_no_recommended_primary";
            }
            if(catalogueComplete&&causalComplete&&suppression>0)
                foreach(var chain in uniqueRecurrences.Where(canSuppress))
                {
                    chain.Suppressed=true;result.Echo.Add(chain.Later);result.Selected.Remove(chain.Later);
                    result.Reasons[chain.Later]="complete_two_endpoint_RT_echo_suppressed_only_after_unambiguous_selected_source_hypothesis";
                }
            // Keep the per-component display contract identical to the final
            // global set: an echo edge is not both suppressed and selected.
            foreach(var component in result.Components)
                component.Selected=component.Selected.Where(id=>result.Selected.Contains(id)).ToArray();
            return result;
        }
        static int LowerBound(int[] starts,double lower)
        {int first=0,last=starts.Length;while(first<last){int middle=first+(last-first)/2;if(starts[middle]<lower)first=middle+1;else last=middle;}return first;}
        static SeventhGraphComponent Solve(int[] nodes,SeventhGraphEdge[] edges,Dictionary<int,SeventhGraphNode> map)
        {
            var result=new SeventhGraphComponent{Nodes=nodes,Edges=edges.Select(e=>e.Id).ToArray()};
            if(nodes.Length>MaximumComponentNodes||edges.Length>MaximumComponentEdges)
            {result.Complete=false;result.Reason="component_size_budget_only_provisional_mutual_best_review";return MutualBest(result,edges);}
            double best=double.NegativeInfinity,second=double.NegativeInfinity;int[] bestIds=new int[0],secondIds=new int[0];
            var used=new HashSet<int>();var chosen=new List<int>();bool exhausted=false;int steps=0;
            Action<double> visit=null;visit=score=>
            {
                if(++steps>MaximumSearchSteps){exhausted=true;return;}
                int node=nodes.FirstOrDefault(n=>!used.Contains(n));if(used.Count==nodes.Length)
                {
                    int[] ids=chosen.OrderBy(id=>map[edges.First(e=>e.Id==id).I].Peak).ThenBy(id=>map[edges.First(e=>e.Id==id).R].Peak).ToArray();
                    // Equal evidence is resolved only for a reproducible DISPLAY
                    // hypothesis, by chronology; it always remains ambiguous.
                    if(score>best+1e-12){second=best;secondIds=bestIds;best=score;bestIds=ids;}
                    else if(!ids.SequenceEqual(bestIds)&&score>second){second=score;secondIds=ids;}
                    return;
                }
                used.Add(node);
                foreach(var edge in edges.Where(e=>(e.I==node||e.R==node)&&!used.Contains(e.I==node?e.R:e.I))
                    .OrderByDescending(e=>e.Score).ThenBy(e=>map[e.R].Peak).ThenBy(e=>e.Id))
                {int other=edge.I==node?edge.R:edge.I;used.Add(other);chosen.Add(edge.Id);visit(score+edge.Score);chosen.RemoveAt(chosen.Count-1);used.Remove(other);if(exhausted)break;}
                if(!exhausted)visit(score);used.Remove(node);
            };
            visit(0);result.Steps=steps;
            if(exhausted){result.Complete=false;result.Reason="component_search_budget_only_provisional_mutual_best_review";return MutualBest(result,edges);}
            result.Selected=bestIds;result.Alternative=secondIds;result.Best=best;result.Second=Finite(second)?second:double.NaN;
            result.Ambiguous=Finite(second)&&best-second<=.06*Math.Max(1,bestIds.Length);
            var selected=edges.Where(e=>bestIds.Contains(e.Id)).ToArray();
            bool countTradeoff=edges.Any(e=>!bestIds.Contains(e.Id)&&selected.Count(s=>s.I==e.I||s.R==e.I||s.I==e.R||s.R==e.R)>=2&&
                e.Score>=selected.Where(s=>s.I==e.I||s.R==e.I||s.I==e.R||s.R==e.R).Max(s=>s.Score)-.05);
            if(countTradeoff)result.Ambiguous=true;
            result.Reason=countTradeoff?"one_strong_edge_vs_multiple_weaker_edges_model_tradeoff_requires_review":
                result.Ambiguous?"near_equal_complete_component_explanations":"bounded_maximum_total_waveform_evidence_with_review";
            return result;
        }
        static SeventhGraphComponent MutualBest(SeventhGraphComponent component,SeventhGraphEdge[] edges)
        {
            // Scan ALL edges first. This is not a global optimum or prefix winner
            // and never receives automatic acceptance when budgets are exceeded.
            var best=new Dictionary<int,SeventhGraphEdge>();
            foreach(var edge in edges.OrderByDescending(e=>e.Score).ThenBy(e=>e.Id))
            {if(!best.ContainsKey(edge.I))best[edge.I]=edge;if(!best.ContainsKey(edge.R))best[edge.R]=edge;}
            component.Selected=edges.Where(e=>best[e.I]==e&&best[e.R]==e).Select(e=>e.Id).ToArray();component.Ambiguous=true;return component;
        }
    }
}
