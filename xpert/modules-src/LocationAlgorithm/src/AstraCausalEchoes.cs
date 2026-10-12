using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4CausalEcho
    {
        internal A4Pair Source,Later;
        internal int Order;
        internal double FirstWave,SecondWave,FirstRatio,SecondRatio,RatioDifferenceDb,AllowedRatioDifferenceDb;
        internal bool Strong;
        internal string State="pending",Reason="conditional_causal_echo_role";
    }
    private static bool A4EchoEndpointEvidence(A4Context c,A4Pulse a,A4Pulse b,out double wave,out double ratio,out double noise)
    {
        wave=0;ratio=0;noise=0;
        if(!A4RoleEndpointReliable(a)||!A4RoleEndpointReliable(b)||a.Snr<8||b.Snr<8)return false;
        A4UnifiedCorrelationEvidence unified=A4UnifiedCorrelation(c,a,b,A4ChargeIndex(b)-A4ChargeIndex(a));
        wave=unified.Measured==null?double.NaN:unified.Measured.Fixed;
        if(!unified.Passed||unified.Measured==null||!unified.Measured.StructureCompatible||
            unified.Measured.OppositeTemporalPolarity)return false;
        ratio=b.LocalAmplitude/Math.Max(1e-30,a.LocalAmplitude);
        noise=2*Math.Sqrt(a.NoiseSigma*a.NoiseSigma+b.NoiseSigma*b.NoiseSigma);
        return Finite(ratio)&&ratio>0&&ratio<=1.25;
    }
    private static bool A4Touches(A4Pair pair,HashSet<int> ids)
    {return ids.Contains(pair.Incident.Id)||ids.Contains(pair.Reflection.Id);}
    private static void A4AddEchoVertices(HashSet<int> ids,A4CausalEcho chain)
    {
        ids.Add(chain.Source.Incident.Id);ids.Add(chain.Source.Reflection.Id);
        ids.Add(chain.Later.Incident.Id);ids.Add(chain.Later.Reflection.Id);
    }
    private static void A4FindCausalEchoRoles(A4Context c,List<A4Pair> pool)
    {
        c.CausalEchoes=new List<A4CausalEcho>();c.ConditionalEchoPulseIds.Clear();
        c.CausalAmbiguousPulseIds.Clear();c.CausalEchoProtectedPulseIds.Clear();
        if(c.EffectiveSuppression==0)return;
        List<A4CausalEcho> strong=new List<A4CausalEcho>();
        // A single periodic coincidence is insufficient. The complete ordered pair must recur at the same RT order.
        A4Pair[] ordered=pool.OrderBy(p=>p.Incident.Start).ThenBy(p=>p.Reflection.Start).ThenBy(p=>p.EdgeId).ToArray();
        for(int i=0;i<ordered.Length;i++)
        {
            if(!A4TakeRoleReviewStep(c))return;
            A4Pair source=ordered[i];
            if(!Finite(source.Confidence)||source.Confidence<=A4MinimumOutputConfidence)continue;
            for(int j=i+1;j<ordered.Length;j++)
            {
                if(!A4TakeRoleReviewStep(c))return;
                A4Pair later=ordered[j];
                if(source.Reflection.End>=later.Incident.Start)continue;
                int orderA=A4EchoOrder(A4ChargeIndex(later.Incident)-A4ChargeIndex(source.Incident),c.RoundTripExact,A4EchoTolerance(source.Incident));
                int orderB=A4EchoOrder(A4ChargeIndex(later.Reflection)-A4ChargeIndex(source.Reflection),c.RoundTripExact,A4EchoTolerance(source.Reflection));
                if(orderA<=0||orderA!=orderB||orderA>c.EffectiveSuppression)continue;
                double waveA,waveB,ratioA,ratioB,noiseA,noiseB;
                if(!A4EchoEndpointEvidence(c,source.Incident,later.Incident,out waveA,out ratioA,out noiseA)||
                   !A4EchoEndpointEvidence(c,source.Reflection,later.Reflection,out waveB,out ratioB,out noiseB))continue;
                c.CausalEchoProtectedPulseIds.Add(later.Incident.Id);c.CausalEchoProtectedPulseIds.Add(later.Reflection.Id);
                bool decayA=source.Incident.LocalAmplitude-later.Incident.LocalAmplitude>noiseA;
                bool decayB=source.Reflection.LocalAmplitude-later.Reflection.LocalAmplitude>noiseB;
                double difference=Math.Abs(20*Math.Log10(ratioA/ratioB));
                double uncertainty=3+20*Math.Log10(1+noiseA/Math.Max(1e-30,later.Incident.LocalAmplitude)+
                    noiseB/Math.Max(1e-30,later.Reflection.LocalAmplitude));
                A4CausalEcho chain=new A4CausalEcho {Source=source,Later=later,Order=orderA,
                    FirstWave=waveA,SecondWave=waveB,FirstRatio=ratioA,SecondRatio=ratioB,
                    RatioDifferenceDb=difference,AllowedRatioDifferenceDb=uncertainty};
                if(!decayA&&!decayB)
                {
                    // Equal-amplitude independent discharges are not vetoed by period or shape alone.
                    chain.State="not_echo";chain.Reason="no_noise_resolved_two_endpoint_decay";
                    c.CausalEchoes.Add(chain);continue;
                }
                chain.Strong=decayA&&decayB&&difference<=uncertainty;
                if(!chain.Strong)
                {
                    chain.Reason="two_endpoint_decay_evidence_ambiguous";
                    A4AddEchoVertices(c.CausalAmbiguousPulseIds,chain);c.CausalEchoes.Add(chain);continue;
                }
                strong.Add(chain);
            }
        }
        // Pair-to-pair causality is acyclic (the later pair starts after the complete source pair).
        // A derived echo pair is never promoted to an independent source merely to restart the suppression count.
        Dictionary<A4Pair,List<A4CausalEcho>> incomingByPair=new Dictionary<A4Pair,List<A4CausalEcho>>();
        foreach(A4CausalEcho chain in strong)
        {
            if(!A4TakeRoleReviewStep(c))return;
            List<A4CausalEcho> incoming;
            if(!incomingByPair.TryGetValue(chain.Later,out incoming)){incoming=new List<A4CausalEcho>();incomingByPair[chain.Later]=incoming;}
            incoming.Add(chain);
        }
        Dictionary<A4Pair,HashSet<A4Pair>> roots=new Dictionary<A4Pair,HashSet<A4Pair>>();
        foreach(A4Pair pair in ordered)
        {
            if(!A4TakeRoleReviewStep(c))return;
            List<A4CausalEcho> incoming;
            if(!incomingByPair.TryGetValue(pair,out incoming))incoming=new List<A4CausalEcho>();
            HashSet<A4Pair> current=new HashSet<A4Pair>();
            if(incoming.Count==0)current.Add(pair);
            else foreach(A4CausalEcho chain in incoming)
            {
                if(!A4TakeRoleReviewStep(c))return;
                HashSet<A4Pair> earlier;
                if(roots.TryGetValue(chain.Source,out earlier))
                {foreach(A4Pair ancestor in earlier){if(current.Count>=2)break;current.Add(ancestor);}}
                else if(current.Count<2)current.Add(chain.Source);
            }
            roots[pair]=current;
        }
        List<A4CausalEcho> canonical=new List<A4CausalEcho>();
        foreach(A4CausalEcho chain in strong)
        {
            if(!A4TakeRoleReviewStep(c))return;
            HashSet<A4Pair> sourceRoots=roots[chain.Source],laterRoots=roots[chain.Later];
            if(sourceRoots.Count!=1||laterRoots.Count!=1)
            {
                chain.Reason="multiple_causal_source_explanations";
                A4AddEchoVertices(c.CausalAmbiguousPulseIds,chain);c.CausalEchoes.Add(chain);continue;
            }
            A4Pair root=sourceRoots.First();
            if(root!=chain.Source)continue; // Only direct evidence from the earliest root within the user's order limit is applied.
            canonical.Add(chain);c.CausalEchoes.Add(chain);
            c.ConditionalEchoPulseIds.Add(chain.Later.Incident.Id);c.ConditionalEchoPulseIds.Add(chain.Later.Reflection.Id);
        }
        // A later phase-shifted subchain (for example B0-A1 followed by B1-A2) does not create a
        // second source when its COMPLETE evidence episode is a strict subset of an earlier paired chain.
        // Equal or non-containing episodes remain independent competing explanations.
        Dictionary<A4Pair,HashSet<int>> episodes=new Dictionary<A4Pair,HashSet<int>>();
        foreach(A4CausalEcho chain in canonical)
        {
            if(!A4TakeRoleReviewStep(c))return;
            HashSet<int> episode;
            if(!episodes.TryGetValue(chain.Source,out episode)){episode=new HashSet<int>();episodes[chain.Source]=episode;}
            A4AddEchoVertices(episode,chain);
        }
        HashSet<A4Pair> derivedRoots=new HashSet<A4Pair>();
        foreach(var laterEpisode in episodes)
            foreach(var earlierEpisode in episodes)
            {
                if(!A4TakeRoleReviewStep(c))return;
                if(earlierEpisode.Key.Incident.Start>=laterEpisode.Key.Incident.Start||
                    earlierEpisode.Value.Count<=laterEpisode.Value.Count)continue;
                if(laterEpisode.Value.IsSubsetOf(earlierEpisode.Value)){derivedRoots.Add(laterEpisode.Key);break;}
            }
        foreach(A4CausalEcho chain in canonical)
            if(derivedRoots.Contains(chain.Source))
            {chain.State="derived_subchain";chain.Reason="strict_subset_of_complete_earlier_two_endpoint_echo_episode";}
        canonical=canonical.Where(chain=>!derivedRoots.Contains(chain.Source)).ToList();
        // Different source pairs may not reserve the same echo packet, share a source packet, or borrow another source's echo.
        Dictionary<int,HashSet<A4Pair>> targetSources=new Dictionary<int,HashSet<A4Pair>>();
        Dictionary<int,HashSet<A4Pair>> sourceRoles=new Dictionary<int,HashSet<A4Pair>>();
        foreach(A4CausalEcho chain in canonical)
        {
            if(!A4TakeRoleReviewStep(c))return;
            foreach(int id in new[]{chain.Later.Incident.Id,chain.Later.Reflection.Id})
            {
                HashSet<A4Pair> sources;if(!targetSources.TryGetValue(id,out sources)){sources=new HashSet<A4Pair>();targetSources[id]=sources;}
                sources.Add(chain.Source);
            }
            foreach(int id in new[]{chain.Source.Incident.Id,chain.Source.Reflection.Id})
            {
                HashSet<A4Pair> sources;if(!sourceRoles.TryGetValue(id,out sources)){sources=new HashSet<A4Pair>();sourceRoles[id]=sources;}
                sources.Add(chain.Source);
            }
        }
        foreach(var entry in targetSources)if(entry.Value.Count>1)c.CausalAmbiguousPulseIds.Add(entry.Key);
        foreach(var entry in sourceRoles)if(entry.Value.Count>1||targetSources.ContainsKey(entry.Key))c.CausalAmbiguousPulseIds.Add(entry.Key);
        bool changed;
        do
        {
            int count=c.CausalAmbiguousPulseIds.Count;
            foreach(A4CausalEcho chain in canonical)
            {
                if(!A4TakeRoleReviewStep(c))return;
                if(A4Touches(chain.Source,c.CausalAmbiguousPulseIds)||A4Touches(chain.Later,c.CausalAmbiguousPulseIds))
                {chain.Reason="competing_or_ambiguous_causal_packet_roles";A4AddEchoVertices(c.CausalAmbiguousPulseIds,chain);}
            }
            changed=count!=c.CausalAmbiguousPulseIds.Count;
        }while(changed);
    }
    private static void A4CommitCausalEchoes(A4Context c,List<A4Pair> accepted)
    {
        HashSet<A4Pair> confirmed=new HashSet<A4Pair>(accepted);
        foreach(A4CausalEcho chain in c.CausalEchoes)
        {
            if(!chain.Strong||chain.State=="not_echo"||chain.State=="derived_subchain")continue;
            if(A4Touches(chain.Source,c.CausalAmbiguousPulseIds)||A4Touches(chain.Later,c.CausalAmbiguousPulseIds)||!confirmed.Contains(chain.Source))
            {chain.State="pending";continue;}
            int owner=chain.Source.Incident.Owner;
            if(owner<0||chain.Source.Reflection.Owner!=owner)throw new InvalidOperationException("Causal echo source has not committed both primary endpoints.");
            foreach(A4Pulse pulse in new[]{chain.Later.Incident,chain.Later.Reflection})
            {
                if(pulse.Owner>=0&&pulse.Owner!=owner)throw new InvalidOperationException("Causal echo attempted to reuse another primary packet.");
                if(pulse.Owner<0){pulse.Owner=owner;chain.Source.Suppressed++;c.Suppressed++;}
            }
            chain.State="echo_confirmed";chain.Reason="complete_pair_same_roundtrip_order_and_noise_resolved_consistent_decay";
        }
        foreach(A4Pair edge in c.CandidatePool)
        {
            if(edge.State=="confirmed")continue;
            if(A4Touches(edge,c.CausalAmbiguousPulseIds))
            {edge.State="pending";edge.ReviewReason="ambiguous_causal_roles_no_owner";}
            else if(A4Touches(edge,c.ConditionalEchoPulseIds))
            {
                bool owned=(c.ConditionalEchoPulseIds.Contains(edge.Incident.Id)&&edge.Incident.Owner>=0)||
                    (c.ConditionalEchoPulseIds.Contains(edge.Reflection.Id)&&edge.Reflection.Owner>=0);
                edge.State=owned?"reviewed":"pending";
                edge.ReviewReason=owned?"packet_committed_to_confirmed_source_echo_role":"conditional_echo_source_not_globally_confirmed";
            }
        }
    }
    private static string A4CausalEchoJson(A4Context c)
    {
        StringBuilder b=new StringBuilder();
        b.Append("{\"enabled\":").Append(c.EffectiveSuppression==0?"false":"true")
            .Append(",\"effective_count\":").Append(c.EffectiveSuppression).Append(",\"resolution\":").Append(Json(c.SuppressionResolution))
            .Append(",\"policy\":\"complete_pair_same_RT_order_guarded_fixed_band_dynamic_CAL_correlation_same_temporal_polarity_and_noise_resolved_consistent_amplitude_decay; unique_earliest_source; no_score_bonus\"")
            .Append(",\"equal_amplitude_is_not_echo_evidence\":true,\"conditional_role_claims_owner\":false,\"chains\":[");
        for(int i=0;i<c.CausalEchoes.Count;i++)
        {
            if(i>0)b.Append(',');A4CausalEcho chain=c.CausalEchoes[i];
            b.Append("{\"source_edge_id\":").Append(chain.Source.EdgeId).Append(",\"later_pair_edge_id\":").Append(chain.Later.EdgeId)
                .Append(",\"order\":").Append(chain.Order).Append(",\"first_signed_correlation\":").Append(NumberJson(chain.FirstWave))
                .Append(",\"second_signed_correlation\":").Append(NumberJson(chain.SecondWave)).Append(",\"first_amplitude_ratio\":").Append(NumberJson(chain.FirstRatio))
                .Append(",\"first_unified_correlation\":").Append(A4UnifiedCorrelationJson(A4UnifiedCorrelation(c,chain.Source.Incident,chain.Later.Incident,A4ChargeIndex(chain.Later.Incident)-A4ChargeIndex(chain.Source.Incident))))
                .Append(",\"second_unified_correlation\":").Append(A4UnifiedCorrelationJson(A4UnifiedCorrelation(c,chain.Source.Reflection,chain.Later.Reflection,A4ChargeIndex(chain.Later.Reflection)-A4ChargeIndex(chain.Source.Reflection))))
                .Append(",\"second_amplitude_ratio\":").Append(NumberJson(chain.SecondRatio)).Append(",\"ratio_difference_db\":").Append(NumberJson(chain.RatioDifferenceDb))
                .Append(",\"allowed_ratio_difference_db\":").Append(NumberJson(chain.AllowedRatioDifferenceDb))
                .Append(",\"strong_two_endpoint_evidence\":").Append(chain.Strong?"true":"false")
                .Append(",\"state\":").Append(Json(chain.State)).Append(",\"reason\":").Append(Json(chain.Reason)).Append('}');
        }
        b.Append("],\"conditional_echo_packet_ids\":").Append(NumberArrayJson(c.ConditionalEchoPulseIds.OrderBy(id=>id).Select(id=>(double)id).ToArray()))
            .Append(",\"ambiguous_packet_ids\":").Append(NumberArrayJson(c.CausalAmbiguousPulseIds.OrderBy(id=>id).Select(id=>(double)id).ToArray()))
            .Append(",\"protected_from_single_endpoint_echo_fallback_ids\":").Append(NumberArrayJson(c.CausalEchoProtectedPulseIds.OrderBy(id=>id).Select(id=>(double)id).ToArray()))
            .Append('}');return b.ToString();
    }
}
