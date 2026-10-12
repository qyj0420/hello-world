using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location
{
    // Bounded, native-sample propagation evidence. No location targets, number
    // of clusters, labels, project names or historical output enter this kernel.
    internal static class EighthPropagationMath
    {
        const int MaximumPropagationComparisons = 200000;
        const int MaximumGeometrySteps = 2000000;
        sealed class Repeat
        {
            internal int Source, Later, Order;
            internal int DecayAnchor = -1;
            internal bool Strong;
            internal string Reason;
        }
        static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
        static void Review(EighthCandidateEvidence e, string reason)
        {
            if (e.Pair == null) return;
            e.Pair.Accepted = false; e.Pair.Role = "review";
            EighthKernelResources.Work(e.Pair.Reason.Length+reason.Length,"review_reason_search");
            if (!e.Pair.Reason.Contains(reason)) AppendReason(e.Pair,reason);
        }
        static void AppendReason(EighthPair pair,string reason)
        {
            if(EighthKernelResources.Current!=null)EighthKernelResources.Current.Bytes(32+2L*(pair.Reason.Length+reason.Length+1),"review_reason_string");
            EighthKernelResources.Work(pair.Reason.Length+reason.Length+1,"review_reason_copy");pair.Reason+=";"+reason;
        }
        internal static double Tolerance(EighthPulse a, EighthPulse b, double fs, double calUncertainty, int order, double tc)
        {
            double uncertainty = 2 + fs * (a.TimingUncertaintySeconds + b.TimingUncertaintySeconds +
                Math.Max(0, calUncertainty) * Math.Max(1, order));
            // Uncertainty is propagated, but an arbitrarily large calibration
            // error cannot make arbitrary periods into a physical recurrence.
            return Math.Min(Math.Max(2, .25 * tc), Math.Max(2, uncertainty));
        }
        internal static bool Decays(EighthPulse a, EighthPulse b)
        {
            double noise = Math.Max(a.Noise, b.Noise);
            return a.AbsAmplitude - b.AbsAmplitude > 3 * noise;
        }
        internal static bool ConsistentRatios(EighthPulse a,EighthPulse b,EighthPulse c,EighthPulse d)
        {
            double ri=c.AbsAmplitude/a.AbsAmplitude,rr=d.AbsAmplitude/b.AbsAmplitude;
            if(!Finite(ri)||!Finite(rr)||ri<=0||rr<=0)return false;
            double difference=Math.Abs(Math.Log(ri)-Math.Log(rr))*20/Math.Log(10);
            double allowance=3+20/Math.Log(10)*Math.Log(1+
                3*Math.Max(a.Noise,c.Noise)/Math.Max(c.AbsAmplitude,1e-15)+
                3*Math.Max(b.Noise,d.Noise)/Math.Max(d.AbsAmplitude,1e-15));
            return Finite(difference)&&Finite(allowance)&&difference<=allowance;
        }
        static int LowerBound(double[] values, double value)
        { int a=0,b=values.Length; while(a<b){int m=a+(b-a)/2;if(values[m]<value)a=m+1;else b=m;}return a; }

        internal static void Resolve(EighthAnalysis analysis, bool[] eligible, double fs, double rt,
            EighthResolvedClocks clocks, int recordLength, int maximumIncidents, int suppression,
            Func<EighthPulse,EighthPulse,double> compare)
        {
            var meter=EighthKernelResources.Current;
            var pulses=analysis.Pulses;
            meter.Work(12L*analysis.CandidateEvidence.Count+pulses.Count,"propagation_initial_indices");
            meter.Sort(analysis.CandidateEvidence.Count,"propagation_edge_sort");
            meter.Meta(1024+1024L*analysis.CandidateEvidence.Count+32L*pulses.Count,"propagation_fixed_indices_and_containers");
            var edges=analysis.CandidateEvidence.Where(e=>e.Pair!=null).OrderBy(e=>e.Pair.Incident.Peak)
                .ThenBy(e=>e.Pair.Reflection.Peak).ThenBy(e=>e.Id).ToArray();
            var byId=edges.ToDictionary(e=>e.Id);
            // Keep all independently measured pair supports for later role
            // competition. Failing the I/R shape floor does not make either
            // observed packet disappear from a one-RT three-wave hypothesis.
            var measuredRoleEdges=edges;
            // Every recurrence/three-wave/suffix comparison below uses p.Peak.
            // Unknown boundaries do not receive a guessed period or inference.
            bool loopKnown=clocks!=null&&clocks.PhysicalLoopKnown;
            double tc=loopKnown?clocks.PeakLoopSeconds*fs:0;
            double calibrationUncertainty=loopKnown?clocks.LoopUncertaintySeconds:0;
            var repeats=new List<Repeat>();
            // Each anchor is reached only by an already observed adjacent hop.
            // A small per-hop drop need not exceed noise forever: cumulative
            // decay from a measured strong ancestor can still be resolved.
            // Never bridge a missing cycle, growth, or an unanchored flat train.
            var decayAncestors=new Dictionary<int,HashSet<int>>();
            int comparisons=0,geometrySteps=0; bool complete=true;
            var starts=edges.Select(e=>(double)e.Pair.Incident.Peak).ToArray();
            double maxPacketTiming=pulses.Count==0?0:pulses.Max(p=>p.TimingUncertaintySeconds);
            // A causal hop requires both branches in the NEXT observed RT.
            // There is no cap on transitive ancestry: an eleven-generation
            // train has ten consecutive hops. Arbitrarily remote integer-RT
            // phase collisions without their intervening trajectory are not
            // evidence of descent (or grounds to veto independent events).
            if(loopKnown)foreach(var source in edges)
            {
                if(source.Pair.FullRoundTripRoleAmbiguous||source.Pair.FirstArrivalUnresolved)continue;
                var a=source.Pair.Incident; var b=source.Pair.Reflection;
                for(int scanOrder=1;scanOrder<=1;scanOrder++)
                {
                    meter.Geometry();geometrySteps++;
                    double predicted=a.Peak+scanOrder*tc;
                    double radius=Math.Min(Math.Max(2,.25*tc),Math.Max(2,2+fs*(2*maxPacketTiming+
                        Math.Max(0,calibrationUncertainty)*scanOrder)));
                    for(int k=LowerBound(starts,predicted-radius);k<edges.Length&&starts[k]<=predicted+radius;k++)
                    {
                    meter.PropagationComparison();comparisons++;
                    var later=edges[k]; var c=later.Pair.Incident;var d=later.Pair.Reflection;
                    if(later.Pair.FullRoundTripRoleAmbiguous||later.Pair.FirstArrivalUnresolved)continue;
                    if(c.Peak<=b.Peak)continue;
                    double orders=(c.Peak-a.Peak)/tc;
                    if(!Finite(orders)||orders<.5||orders>Int32.MaxValue-1)continue;
                    int order=(int)Math.Round(orders);
                    double ti=Tolerance(a,c,fs,calibrationUncertainty,order,tc);
                    double tr=Tolerance(b,d,fs,calibrationUncertainty,order,tc);
                    if(order!=1||Math.Abs(c.Peak-a.Peak-tc)>ti||Math.Abs(d.Peak-b.Peak-tc)>tr)continue;
                    double ac=compare(a,c),bd=compare(b,d);
                    if(!Finite(ac)||!Finite(bd)||Math.Abs(ac)<.90||Math.Abs(bd)<.90||Math.Sign(ac)!=Math.Sign(bd))continue;
                    bool ratios=ConsistentRatios(a,b,c,d);
                    bool strong=ratios&&Decays(a,c)&&Decays(b,d);
                    int decayAnchor=-1;HashSet<int> anchors;
                    if(!strong&&ratios&&a.AbsAmplitude>c.AbsAmplitude&&b.AbsAmplitude>d.AbsAmplitude&&
                        decayAncestors.TryGetValue(source.Id,out anchors))
                    {
                        meter.Sort(anchors.Count,"decay_ancestor_sort");meter.Meta(256+32L*anchors.Count,"decay_ancestor_sort_buffers");
                        foreach(int id in anchors.OrderBy(id=>byId[id].Pair.Incident.Peak).ThenBy(id=>id))
                        {
                            meter.PropagationComparison();comparisons++;
                            var anchor=byId[id].Pair;
                            double ai=compare(anchor.Incident,c),ar=compare(anchor.Reflection,d);
                            if(Finite(ai)&&Finite(ar)&&Math.Abs(ai)>=.90&&Math.Abs(ar)>=.90&&Math.Sign(ai)==Math.Sign(ar)&&
                                Decays(anchor.Incident,c)&&Decays(anchor.Reflection,d)&&
                                ConsistentRatios(anchor.Incident,anchor.Reflection,c,d))
                            {strong=true;decayAnchor=id;break;}
                        }
                    }
                    if(strong)
                    {
                        HashSet<int> inherited;
                        if(!decayAncestors.TryGetValue(later.Id,out inherited))decayAncestors[later.Id]=inherited=new HashSet<int>();
                        meter.Meta(64,"decay_ancestry_member");inherited.Add(source.Id);
                        if(decayAncestors.TryGetValue(source.Id,out anchors))foreach(int ancestor in anchors)
                        {
                            meter.Geometry();geometrySteps++;
                            meter.Meta(64,"decay_ancestry_member");inherited.Add(ancestor);
                        }
                    }
                    meter.Meta(256,"observed_repeat_record");
                    repeats.Add(new Repeat{Source=source.Id,Later=later.Id,Order=order,Strong=strong,
                        DecayAnchor=decayAnchor,
                        Reason=strong?(decayAnchor>=0?"adjacent_observed_RT_hop_with_resolved_ancestor_decay":
                            "complete_two_endpoint_RT_recurrence_consistent_decay"):
                        "complete_two_endpoint_RT_geometry_shape_but_growth_or_gain_inconclusive"});
                    }
                    if(!complete)break;
                }
                if(!complete)break;
            }

            // A different I/R shape is not itself proof of a reflection. It
            // becomes a graph edge only when two independently measured LOC
            // branches recur together with the CAL clock, shape and decay.
            // The high I/R correlation path remains available for single pairs.
            var physicallySupported=new HashSet<int>(repeats.Where(x=>x.Strong)
                .SelectMany(x=>new[]{x.Source,x.Later}));
            foreach(var e in edges)
            {
                e.PropagationEligible=e.Passed||physicallySupported.Contains(e.Id);
                if((!e.Passed||e.Pair.ModeRoundTripRoleUnresolved)&&physicallySupported.Contains(e.Id)&&!e.Pair.FullRoundTripRoleAmbiguous&&!e.Pair.FirstArrivalUnresolved)
                {
                    e.Pair.Accepted=true;e.Pair.Role="independent";
                    AppendReason(e.Pair,"two_independent_branches_recur_with_CAL_clock_shape_and_decay");
                    if(e.Pair.ModeRoundTripRoleUnresolved)
                        AppendReason(e.Pair,"soft_CAL_mode_role_concern_resolved_by_disjoint_observed_branches");
                }
            }
            edges=edges.Where(e=>e.PropagationEligible).ToArray();
            repeats=repeats.Where(x=>byId[x.Source].PropagationEligible&&byId[x.Later].PropagationEligible).ToList();

            // Determine roots transitively. An earlier root and its child are
            // not two independent sources merely because both explain a later
            // generation. Distinct ancestral roots remain a real ambiguity.
            var incoming=repeats.Where(r=>r.Strong).GroupBy(r=>r.Later).ToDictionary(g=>g.Key,g=>g.ToArray());
            var roots=new Dictionary<int,HashSet<int>>();
            var hops=new Dictionary<int,int>();
            var ambiguousHopRoots=new HashSet<int>();
            foreach(var e in edges)
            {
                Repeat[] parents;var set=new HashSet<int>();
                bool hasParents=incoming.TryGetValue(e.Id,out parents);
                var measuredDepths=new HashSet<int>();
                if(hasParents)foreach(var p in parents)
                {
                    HashSet<int> parentRoots;
                    meter.Work(1,"root_parent");
                    if(roots.TryGetValue(p.Source,out parentRoots))
                    {meter.Work(parentRoots.Count,"root_union");meter.Meta(64L*parentRoots.Count,"root_union_capacity");set.UnionWith(parentRoots);}
                    else {meter.Meta(64,"root_member");set.Add(p.Source);}
                    int depth;measuredDepths.Add(hops.TryGetValue(p.Source,out depth)&&depth>=0?depth+1:-1);
                }
                if(set.Count==0){meter.Meta(64,"root_member");set.Add(e.Id);}
                roots[e.Id]=set;
                hops[e.Id]=!hasParents?0:measuredDepths.Count==1&&!measuredDepths.Contains(-1)?measuredDepths.First():-1;
                if(hops[e.Id]<0)
                {
                    meter.Work(set.Count,"ambiguous_root_union");meter.Meta(64L*set.Count,"ambiguous_root_capacity");ambiguousHopRoots.UnionWith(set);
                    Review(e,"observed_propagation_paths_disagree_on_generation_count");
                }
                if(set.Count>1)
                {
                    Review(e,"multiple_independent_propagation_roots_unresolved");
                    meter.Work(set.Count,"multiple_root_review");
                    foreach(int id in set)Review(byId[id],"multiple_independent_propagation_roots_unresolved");
                }
            }
            meter.Work(12L*edges.Length+8L*repeats.Count,"propagation_grouping");
            var families=edges.Where(e=>roots[e.Id].Count==1).GroupBy(e=>roots[e.Id].First())
                .Where(g=>g.Count()>1).ToDictionary(g=>g.Key,g=>g.ToArray());
            var explained=new HashSet<int>();
            var familyNodes=families.ToDictionary(f=>f.Key,f=>new HashSet<int>(f.Value.SelectMany(e=>new[]{e.IncidentIndex,e.ReflectionIndex})));
            var embedded=new Dictionary<int,int>();
            var suffixBoundaryParent=new Dictionary<int,int>();
            foreach(var family in families)
            {
                // A complementary bridge can itself recur in a multi-cycle
                // record. If every one of its clocks is strictly contained in
                // a larger earlier complete family, it is a different pairing
                // of that family's already explained roles, not another source.
                meter.Work(families.Count,"embedded_family_scan");
                meter.Sort(families.Count,"embedded_family_parent_sort");
                meter.Meta(256+64L*families.Count,"embedded_family_parent_sort_buffers");
                var parents=families.Where(other=>other.Key!=family.Key&&
                    byId[other.Key].Pair.Incident.Peak<byId[family.Key].Pair.Incident.Peak&&
                    familyNodes[other.Key].Count>familyNodes[family.Key].Count&&
                    MeteredSubset(familyNodes[family.Key],familyNodes[other.Key]))
                    .OrderByDescending(other=>familyNodes[other.Key].Count).ThenBy(other=>byId[other.Key].Pair.Incident.Peak).ToArray();
                if(parents.Length>0)embedded[family.Key]=parents[0].Key;
            }
            // A complementary bridge may end one measured I packet beyond the
            // last complete I/R pair when the R branch becomes unmeasurable.
            // Keep that boundary explicitly unknown; do not let it retroactively
            // invalidate a uniquely explained, fully observed earlier prefix.
            // This is NOT a missing-packet completion or a one-branch echo rule.
            foreach(var child in families.Where(f=>!embedded.ContainsKey(f.Key)))
            {
                var possible=new List<Tuple<int,int,int>>();
                foreach(var parent in families)
                {
                    meter.Geometry();geometrySteps++;
                    if(parent.Key==child.Key||embedded.ContainsKey(parent.Key)||parent.Value.Length<3||
                        byId[parent.Key].Pair.Incident.Peak>=byId[child.Key].Pair.Incident.Peak)continue;
                    meter.Work(familyNodes[child.Key].Count+3L*child.Value.Length+parent.Value.Length,"censored_suffix_family_queries");
                    meter.Meta(128+32L*(familyNodes[child.Key].Count+child.Value.Length),"censored_suffix_temporary_arrays");
                    var nodes=familyNodes[parent.Key];
                    var extras=familyNodes[child.Key].Where(n=>!nodes.Contains(n)).ToArray();
                    if(extras.Length!=1)continue;
                    var internalEdges=child.Value.Where(e=>nodes.Contains(e.IncidentIndex)&&nodes.Contains(e.ReflectionIndex)).ToArray();
                    var boundary=child.Value.Where(e=>!nodes.Contains(e.IncidentIndex)||!nodes.Contains(e.ReflectionIndex)).ToArray();
                    if(internalEdges.Length<2||boundary.Length!=1)continue;
                    meter.Sort(parent.Value.Length,"censored_suffix_last_sort");
                    var last=parent.Value.OrderByDescending(e=>e.Pair.Incident.Peak).First();
                    var tail=pulses[extras[0]];
                    if(boundary[0].IncidentIndex!=last.ReflectionIndex||boundary[0].ReflectionIndex!=extras[0]||
                        tail.Peak<=last.Pair.Reflection.Peak||tail.AbsAmplitude>=last.Pair.Incident.AbsAmplitude)continue;
                    if(Math.Abs(tail.Peak-last.Pair.Incident.Peak-tc)>
                        Tolerance(last.Pair.Incident,tail,fs,calibrationUncertainty,1,tc))continue;
                    meter.PropagationComparison();comparisons++;
                    double shape=compare(last.Pair.Incident,tail);
                    if(!Finite(shape)||Math.Abs(shape)<.90||!Decays(byId[parent.Key].Pair.Incident,tail))continue;
                    // Another complete family or an actually visible next R
                    // cannot be called censoring just to simplify a graph.
                    meter.Work(families.Count,"censored_suffix_other_family_query");
                    if(families.Any(f=>f.Key!=parent.Key&&f.Key!=child.Key&&familyNodes[f.Key].Contains(extras[0])))continue;
                    double predictedR=tail.Peak+last.Pair.Reflection.Peak-last.Pair.Incident.Peak;
                    meter.Work(pulses.Count,"censored_suffix_observed_R_query");
                    if(pulses.Any(p=>p.Valid&&p.Peak>tail.Peak&&Math.Abs(p.Peak-predictedR)<=
                        Tolerance(last.Pair.Reflection,p,fs,calibrationUncertainty,1,tc)))continue;
                    meter.Meta(96,"censored_suffix_possible_parent");possible.Add(Tuple.Create(parent.Key,boundary[0].Id,extras[0]));
                }
                if(!complete)break;
                // Never choose the largest/earliest explanation among several
                // competing partial-prefix parents.
                if(possible.Count!=1)continue;
                var match=possible[0];embedded[child.Key]=match.Item1;
                meter.Sort(families[match.Item1].Length,"censored_suffix_matched_parent_sort");
                meter.Work(measuredRoleEdges.Length,"censored_suffix_role_edge_query");
                var lastPair=families[match.Item1].OrderByDescending(e=>e.Pair.Incident.Peak).First();
                foreach(var e in measuredRoleEdges.Where(e=>e.ReflectionIndex==match.Item3&&
                    (e.IncidentIndex==lastPair.IncidentIndex||e.IncidentIndex==lastPair.ReflectionIndex)))
                    suffixBoundaryParent[e.Id]=match.Item1;
            }
            foreach(var item in embedded)
            {
                foreach(var e in families[item.Key])
                {
                    e.Pair.FamilyRootPeak=byId[item.Value].Pair.Incident.Peak;
                    if(suffixBoundaryParent.ContainsKey(e.Id))Review(e,"single_visible_branch_suffix_remains_unknown_not_proved_echo");
                    else {explained.Add(e.Id);Review(e,"recurring_complementary_bridge_embedded_in_larger_complete_family");}
                }
                families.Remove(item.Key);
            }
            meter.Work(8L*edges.Length+8L*repeats.Count,"family_membership_and_conflict_scan");
            var nodeFamilies=new Dictionary<int,HashSet<int>>();
            foreach(var family in families)foreach(var e in family.Value)
                foreach(int node in new[]{e.IncidentIndex,e.ReflectionIndex})
                {meter.Work(1,"node_family_visit");meter.Meta(64,"node_family_member");HashSet<int> set;if(!nodeFamilies.TryGetValue(node,out set))nodeFamilies[node]=set=new HashSet<int>();set.Add(family.Key);}
            var familyConflict=new HashSet<int>();
            familyConflict.UnionWith(ambiguousHopRoots.Where(root=>families.ContainsKey(root)));
            foreach(var item in nodeFamilies.Where(x=>x.Value.Count>1)){meter.Work(item.Value.Count,"family_conflict_union");meter.Meta(64L*item.Value.Count,"family_conflict_capacity");familyConflict.UnionWith(item.Value);}
            foreach(var e in edges)
            {
                if(explained.Contains(e.Id))continue;
                meter.Work(1,"edge_family_ownership");
                HashSet<int> left,right;
                bool haveLeft=nodeFamilies.TryGetValue(e.IncidentIndex,out left),haveRight=nodeFamilies.TryGetValue(e.ReflectionIndex,out right);
                int ownRoot=roots[e.Id].Count==1?roots[e.Id].First():-1;
                if(ownRoot>=0&&families.ContainsKey(ownRoot))continue;
                if(haveLeft&&haveRight&&left.Count==1&&right.Count==1&&left.First()==right.First())
                {
                    // Both clocks already have measured roles in one complete
                    // family. This includes I-I/R-R and complementary R-I bridges.
                    explained.Add(e.Id);e.Pair.FamilyRootPeak=byId[left.First()].Pair.Incident.Peak;
                    Review(e,"alternate_pair_uses_roles_of_complete_same_root_family");
                }
                else if(haveLeft||haveRight)
                {
                    // A shared-packet dispute is symmetric: the competing pair
                    // cannot stay accepted while only its known family is vetoed.
                    Review(e,"external_pair_competes_with_observed_family_packet_role");
                    int prefix;
                    if(suffixBoundaryParent.TryGetValue(e.Id,out prefix)&&haveLeft&&!haveRight&&
                        left.Count==1&&left.Contains(prefix))
                    {
                        Review(e,"single_visible_branch_suffix_remains_unknown_not_proved_echo");
                        continue;
                    }
                    if(haveLeft){meter.Work(left.Count,"left_conflict_union");meter.Meta(64L*left.Count,"left_conflict_capacity");familyConflict.UnionWith(left);}
                    if(haveRight){meter.Work(right.Count,"right_conflict_union");meter.Meta(64L*right.Count,"right_conflict_capacity");familyConflict.UnionWith(right);}
                }
            }
            foreach(int root in familyConflict)foreach(var e in families[root])
                {meter.Work(1,"family_conflict_review");Review(e,"propagation_family_has_external_or_shared_packet_role_competitor");}

            // Nondecaying repeats are hypotheses, not absence of reflection and
            // not sufficient grounds for deleting a real independent event.
            foreach(var link in repeats.Where(r=>!r.Strong))
            {
                Review(byId[link.Later],link.Reason);
                if(!families.ContainsKey(link.Source))Review(byId[link.Source],link.Reason);
            }

            // Three-wave complementary roles stay undecided unless a complete
            // double-endpoint family already explains that internal bridge.
            meter.Work(8L*measuredRoleEdges.Length,"complement_role_index");
            var outgoing=measuredRoleEdges.GroupBy(e=>e.IncidentIndex).ToDictionary(g=>g.Key,g=>g.ToArray());
            if(loopKnown)foreach(var ab in measuredRoleEdges)
            {
                EighthCandidateEvidence[] following;
                if(!outgoing.TryGetValue(ab.ReflectionIndex,out following))continue;
                foreach(var bc in following)
                {
                    meter.PropagationComparison();comparisons++;
                    if(!ab.PropagationEligible&&!bc.PropagationEligible)continue;
                    if(explained.Contains(ab.Id)||explained.Contains(bc.Id))continue;
                    // A measured complete, unconflicted two-branch family can
                    // already identify all three roles even when one alternate
                    // adjacent pairing has low I/R similarity. Do not turn its
                    // known internal bridge back into an external competitor.
                    HashSet<int> af,bf,cf;
                    if(nodeFamilies.TryGetValue(ab.IncidentIndex,out af)&&
                        nodeFamilies.TryGetValue(ab.ReflectionIndex,out bf)&&
                        nodeFamilies.TryGetValue(bc.ReflectionIndex,out cf)&&
                        af.Count==1&&bf.Count==1&&cf.Count==1&&af.First()==bf.First()&&af.First()==cf.First()&&
                        !familyConflict.Contains(af.First())&&byId[af.First()].Pair.Accepted)continue;
                    var a=ab.Pair.Incident;var c=bc.Pair.Reflection;
                    double orders=(c.Peak-a.Peak)/tc;
                    if(!Finite(orders)||orders<.5||orders>Int32.MaxValue-1)continue;
                    int order=(int)Math.Round(orders);
                    // The complement test is one physical traversal too; it
                    // must not manufacture an alternative across silent cycles.
                    if(order!=1||Math.Abs(c.Peak-a.Peak-tc)>Tolerance(a,c,fs,calibrationUncertainty,1,tc))continue;
                    if(Math.Abs(compare(a,c))<.90)continue;
                    Review(ab,"three_wave_RT_complementary_roles_not_uniquely_identified");
                    Review(bc,"three_wave_RT_complementary_roles_not_uniquely_identified");
                    if(!ab.PropagationEligible||!bc.PropagationEligible)
                    {
                        Review(ab,"measured_role_competitor_not_absent_because_adjacent_shape_below_floor");
                        Review(bc,"measured_role_competitor_not_absent_because_adjacent_shape_below_floor");
                    }
                }
                if(!complete)break;
            }

            meter.Work(16L*analysis.CandidateEvidence.Count+16L*edges.Length+pulses.Count,"final_ownership_queries");
            meter.Sort(edges.Length,"final_ownership_orderings");
            var unknownIncident=new HashSet<int>(analysis.CandidateEvidence.Where(e=>e.Unknown).Select(e=>e.IncidentIndex));
            foreach(var e in edges)if(unknownIncident.Contains(e.IncidentIndex))
                Review(e,"unmeasurable_competing_packet_preserved_as_unknown");

            // All valid, unexplained competitors take part before ownership.
            // No earlier review pair can consume a later candidate's clocks.
            var active=edges.Where(e=>!explained.Contains(e.Id)&&roots[e.Id].SetEquals(new[]{e.Id})).ToArray();
            var byNode=active.SelectMany(e=>new[]{Tuple.Create(e.IncidentIndex,e),Tuple.Create(e.ReflectionIndex,e)})
                .GroupBy(x=>x.Item1);
            foreach(var group in byNode)
            {
                var conflict=group.Select(x=>x.Item2).Distinct().ToArray();
                if(conflict.Length>1)foreach(var e in conflict)Review(e,"multiple_complete_LOC_packet_assignments_preserved");
            }
            foreach(var e in edges)
            {
                double t0=e.Pair.T0;
                if(e.Pair.FirstArrivalUnresolved)Review(e,"unresolved_first_arrival_cannot_be_promoted_by_recurrence");
                if(!loopKnown||!clocks.SelectedRoleClockKnown)Review(e,clocks==null?"physical_loop_boundary_unknown":clocks.Reason);
                if(loopKnown&&!EighthPulseMath.SampleWindowCovered(recordLength-1-e.Pair.Incident.Peak,fs,clocks.PeakLoopSeconds))
                    Review(e,"record_censors_required_device_peak_loop_context");
                if(!Finite(t0)||!EighthPulseMath.SampleDelayFitsWindow(e.Pair.DelaySamples,fs,rt)||!Finite(e.Pair.Gain))
                    Review(e,"physical_timing_or_gain_not_representable");
                if(loopKnown&&calibrationUncertainty*fs>=tc/4)
                    Review(e,"CAL_observed_anchor_dispersion_cannot_resolve_RT_role_order");
            }
            // Advisory work has a separate bounded budget. Its exhaustion must
            // not turn a completed physical analysis into a no-PD/empty result.
            using(var identityScope=EighthKernelResources.Begin(new EighthKernelLimits{
                MaximumWorkUnits=50000000,MaximumLogicalBytes=64L*1024*1024,MaximumMetadataBytes=1024*1024}))
            {
                bool failed=false;
                foreach(var e in edges)
                {
                    if(!failed)try {EighthSourceIdentity.Assess(e.Pair,analysis.IdentityCalibration,
                        families.ContainsKey(e.Id)&&!familyConflict.Contains(e.Id)&&families[e.Id].Length>=2);}
                    catch(EighthBudgetExceeded){failed=true;}
                    if(failed){e.Pair.SourceIdentitySupported=false;e.Pair.SourceIdentityReason="source_identity_diagnostic_budget_unavailable";}
                }
                identityScope.Meter.Receipt.Complete=!failed;
                analysis.IdentityResourceUsage=identityScope.Meter.Receipt;
            }
            // Resolve every child's status only AFTER the final source state is
            // known; descendants never count as a second independent source.
            foreach(var family in families)
            {
                var root=byId[family.Key];
                foreach(var e in family.Value)
                {
                    e.Pair.FamilyRootPeak=root.Pair.Incident.Peak;
                    // Count proved causal traversals. Dividing the accumulated
                    // delay by CAL Tc miscounts long trains with small measured
                    // per-hop dispersion, despite every hop being supported.
                    int order=hops[e.Id];
                    e.Pair.EchoOrder=Math.Max(0,order);
                    if(e.Id==root.Id)continue;
                    if(complete&&!e.Pair.FirstArrivalUnresolved&&!familyConflict.Contains(root.Id)&&root.Pair.Accepted&&eligible[root.IncidentIndex]&&
                        (suppression<0||order<=suppression)&&suppression!=0)
                    {
                        e.Pair.Accepted=false;e.Pair.Role="echo";
                        e.Pair.Reason="observed_complete_pair_recurrence_of_final_independent_root";
                    }
                    else Review(e,"repeated_pair_root_or_suppression_policy_unresolved");
                }
            }
            if(!complete)meter.Fail("propagation_budget_incomplete_no_prefix");
            analysis.PropagationComplete=complete&&loopKnown&&clocks.SelectedRoleClockKnown;
            analysis.GraphEvaluationComplete=true;

            var echoNodes=new HashSet<int>(edges.Where(e=>e.Pair.Role=="echo")
                .SelectMany(e=>new[]{e.IncidentIndex,e.ReflectionIndex}));
            var acceptedReflection=new HashSet<int>(edges.Where(e=>e.Pair.Accepted&&eligible[e.IncidentIndex]).Select(e=>e.ReflectionIndex));
            var display=edges.Where(e=>eligible[e.IncidentIndex]&&e.Pair.Role!="echo"&&
                (!explained.Contains(e.Id)||e.Pair.FirstArrivalUnresolved||!complete||familyConflict.Contains(roots[e.Id].First())))
                .GroupBy(e=>e.IncidentIndex).Select(g=>g.OrderByDescending(e=>e.Pair.Accepted)
                    .ThenByDescending(e=>e.Pair.Score).ThenBy(e=>e.Pair.Reflection.Peak).First()).ToDictionary(e=>e.IncidentIndex);
            // Public display capacity is not a failed computational budget.
            // Restore bounded review only after the whole graph completed. All
            // later reservations still use the same sticky, atomic-failure meter.
            int outputCount=Enumerable.Range(0,pulses.Count).Count(i=>eligible[i]&&!echoNodes.Contains(i)&&!acceptedReflection.Contains(i));
            if(outputCount>maximumIncidents)
            {
                analysis.LimitReached=true;analysis.PropagationComplete=false;
                analysis.PublicationLimited=true;
                meter.Work(3L*pulses.Count+3L*analysis.CandidateEvidence.Count,"limited_review_projection");
                meter.Sort(edges.Length,"limited_review_representative_sort");
                meter.Meta(256+128L*edges.Length,"limited_review_projection_containers");
                // Revoke the entire graph, not only the displayed first rows.
                foreach(var e in analysis.CandidateEvidence)Review(e,"incident_output_budget_incomplete_no_prefix_acceptance");
                echoNodes.Clear();acceptedReflection.Clear();
                display=edges.Where(e=>eligible[e.IncidentIndex]).GroupBy(e=>e.IncidentIndex)
                    .Select(g=>g.OrderByDescending(e=>e.Pair.Score).ThenBy(e=>e.Pair.Reflection.Peak).First())
                    .ToDictionary(e=>e.IncidentIndex);
                // Revoking suppression makes formerly owned reflections visible.
                outputCount=Enumerable.Range(0,pulses.Count).Count(i=>eligible[i]);
            }
            analysis.PublicationEligibleCount=outputCount;
            analysis.PublicationComplete=!analysis.PublicationLimited;
            analysis.PublicationOmittedCount=Math.Max(0,outputCount-maximumIncidents);
            meter.Meta(32+32L*Math.Min(outputCount,maximumIncidents),"published_output_references");
            int published=0;
            for(int i=0;i<pulses.Count;i++)
            {
                if(!eligible[i]||echoNodes.Contains(i)||acceptedReflection.Contains(i))continue;
                if(published>=maximumIncidents){analysis.LimitReached=true;break;}
                EighthCandidateEvidence e;
                if(display.TryGetValue(i,out e))analysis.Pairs.Add(e.Pair);
                else analysis.Unpaired.Add(pulses[i]);
                published++;
            }
            analysis.PublishedCount=published;
            analysis.EchoCount=edges.Count(e=>e.Pair.Role=="echo");
            analysis.ReviewCount=analysis.Pairs.Count(p=>!p.Accepted);
            analysis.AmbiguousCount=analysis.ReviewCount;
            analysis.Reason=!loopKnown||!clocks.SelectedRoleClockKnown?"physical_loop_clock_unknown_candidates_preserved_for_review":
                !analysis.PropagationComplete?"incomplete_propagation_or_output_budget_no_automatic_acceptance":
                analysis.ReviewCount>0?"native_propagation_candidates_require_review":"native_complete_pair_propagation_analysis";
            // StringBuilder and final JSON string coexist; reserve conservative
            // UTF-16 capacity before formatting. Reason vocabulary is static here.
            meter.Work(analysis.Pulses.Count+analysis.CandidateEvidence.Count+repeats.Count,"propagation_summary_records");
            meter.Bytes(4096+2048L*analysis.Pulses.Count+8192L*analysis.CandidateEvidence.Count+1024L*repeats.Count,"propagation_summary_text");
            analysis.PropagationSummary=Summary(analysis,repeats,explained,comparisons,suppression);
        }

        static bool MeteredSubset(HashSet<int> left,HashSet<int> right)
        {EighthKernelResources.Work(left.Count,"embedded_family_subset");return left.IsSubsetOf(right);}

        static string Q(string s){return "\""+(s??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";}
        static string Summary(EighthAnalysis a,List<Repeat> repeats,HashSet<int> explained,int comparisons,int suppression)
        {
            var b=new StringBuilder();b.Append("{\"version\":\"method8-dual-clock-v314\",\"complete\":").Append(a.PropagationComplete?"true":"false");
            b.Append(",\"clock_basis\":\"DUT_selected_pair_window;original_CAL_peak_recurrence_and_device_context;original_CAL_selected_pair_role\"");
            b.Append(",\"physical_loop_boundary_known\":").Append(a.Clocks!=null&&a.Clocks.PhysicalLoopKnown?"true":"false");
            b.Append(",\"physical_loop_clock_reason\":").Append(Q(a.Clocks==null?"missing_context":a.Clocks.Reason));
            b.Append(",\"source_identity_policy\":\"advisory_only_hard_gate_failed_field_recall;accepted_is_engineering_candidate_not_physical_confirmation\"");
            b.Append(",\"source_identity_diagnostic_complete\":").Append(a.IdentityResourceUsage!=null&&a.IdentityResourceUsage.Complete?"true":"false");
            b.Append(",\"source_identity_diagnostic_work_units\":").Append(a.IdentityResourceUsage==null?0:a.IdentityResourceUsage.WorkUnits);
            b.Append(",\"position_prior_used\":false,\"cluster_count_limit_used\":false,\"local_interface_paths_modelled\":false,\"unknown_local_paths_not_silently_confirmed\":true");
            b.Append(",\"propagation_hops\":\"adjacent_observed_roundtrip_only;unlimited_transitive_depth;no_unobserved_cycle_skips\"");
            b.Append(",\"decay_evidence\":\"per_hop_significant_drop_or_monotone_adjacent_hop_with_significant_observed_ancestor_drop;no_missing_cycle_completion\"");
            b.Append(",\"complement_role_evidence\":\"all_measurable_adjacent_pairs_including_below_IR_shape_floor;requires_one_RT_clock_and_same_branch_shape;review_not_echo_deletion\"");
            b.Append(",\"censored_suffix_policy\":\"unique_complete_prefix_with_two_internal_bridges;one_measured_RT_continuation;boundary_unknown_and_visible;no_missing_R_completion\"");
            b.Append(",\"echo_order_basis\":\"count_of_proved_adjacent_causal_hops_not_total_delay_divided_by_CAL\"");
            b.Append(",\"suppression_order_limit\":").Append(suppression).Append(",\"comparisons\":").Append(comparisons);
            b.Append(",\"candidate_count\":").Append(a.CandidateCount).Append(",\"echo_count\":").Append(a.EchoCount).Append(",\"review_count\":").Append(a.ReviewCount);
            b.Append(",\"pulse_timing_evidence\":[");bool pulseFirst=true;
            foreach(var p in a.Pulses)
            {
                if(!pulseFirst)b.Append(',');pulseFirst=false;
                b.Append("{\"peak\":").Append(p.Peak).Append(",\"onset\":").Append(p.Onset)
                    .Append(",\"packet_noise_sigma\":").Append(Finite(p.Noise)?p.Noise.ToString("R",CultureInfo.InvariantCulture):"null")
                    .Append(",\"onset_earliest\":").Append(p.OnsetEarliest).Append(",\"onset_latest\":").Append(p.OnsetLatest)
                    .Append(",\"onset_identifiable\":").Append(p.OnsetIdentifiable?"true":"false")
                    .Append(",\"onset_uncertainty_seconds\":").Append(Finite(p.OnsetTimingUncertaintySeconds)?p.OnsetTimingUncertaintySeconds.ToString("R",CultureInfo.InvariantCulture):"null")
                    .Append(",\"onset_reason\":").Append(Q(p.OnsetReason))
                    .Append(",\"packet_spectrum\":").Append(EighthSourceIdentity.Json(p)).Append('}');
            }
            b.Append(']');
            b.Append(",\"candidates\":[");bool first=true;
            foreach(var e in a.CandidateEvidence)
            {
                if(!first)b.Append(',');first=false;
                b.Append("{\"id\":").Append(e.Id).Append(",\"incident_peak\":").Append(a.Pulses[e.IncidentIndex].Peak)
                    .Append(",\"reflection_peak\":").Append(a.Pulses[e.ReflectionIndex].Peak)
                    .Append(",\"waveform_passed\":").Append(e.Passed?"true":"false").Append(",\"unknown\":").Append(e.Unknown?"true":"false")
                    .Append(",\"source_identity_supported\":").Append(e.Pair!=null&&e.Pair.SourceIdentitySupported?"true":"false")
                    .Append(",\"source_identity_reason\":").Append(Q(e.Pair==null?"no_pair":e.Pair.SourceIdentityReason))
                    .Append(",\"propagation_eligible\":").Append(e.PropagationEligible?"true":"false")
                    .Append(",\"first_arrival_unresolved\":").Append(e.Pair!=null&&e.Pair.FirstArrivalUnresolved?"true":"false")
                    .Append(",\"raw_correlation\":").Append(e.RawCorrelation.ToString("R",CultureInfo.InvariantCulture))
                    .Append(",\"incident_snr\":").Append(Finite(e.IncidentSnr)?e.IncidentSnr.ToString("R",CultureInfo.InvariantCulture):"null")
                    .Append(",\"reflection_snr\":").Append(Finite(e.ReflectionSnr)?e.ReflectionSnr.ToString("R",CultureInfo.InvariantCulture):"null")
                    .Append(",\"timing_uncertainty_seconds\":").Append(e.TimingUncertaintySeconds.ToString("R",CultureInfo.InvariantCulture))
                    .Append(",\"incident_packet_reason\":").Append(Q(e.IncidentPacketReason))
                    .Append(",\"reflection_packet_reason\":").Append(Q(e.ReflectionPacketReason))
                    .Append(",\"output_eligible\":").Append(e.OutputEligible?"true":"false")
                    .Append(",\"role\":").Append(Q(e.Pair==null?"excluded":e.Pair.Role))
                    .Append(",\"family_root_peak\":").Append(e.Pair==null?-1:e.Pair.FamilyRootPeak)
                    .Append(",\"echo_order\":").Append(e.Pair==null?0:e.Pair.EchoOrder)
                    .Append(",\"internal_bridge_explained\":").Append(explained.Contains(e.Id)?"true":"false")
                    .Append(",\"reason\":").Append(Q(e.Pair==null?e.Reason:e.Pair.Reason)).Append('}');
            }
            b.Append("],\"recurrences\":[");first=true;
            foreach(var r in repeats){if(!first)b.Append(',');first=false;b.Append("{\"source_candidate\":").Append(r.Source)
                .Append(",\"later_candidate\":").Append(r.Later).Append(",\"order\":").Append(r.Order)
                .Append(",\"strong\":").Append(r.Strong?"true":"false").Append(",\"decay_anchor_candidate\":").Append(r.DecayAnchor)
                .Append(",\"reason\":").Append(Q(r.Reason)).Append('}');}
            return b.Append("]}").ToString();
        }
    }
}
