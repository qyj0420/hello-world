using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Diagnostics only: neither this set nor serialized dispositions participates
    // in discovery, physical evidence, graph selection, ownership, or matrix output.
    private readonly HashSet<int> a6OrphanIncidentIds=new HashSet<int>();
    private const int A6IncidentDiagnosticDetailLimit=128;

    private void A6ResetIncidentDiagnostics(A4Context c)
    {
        a6OrphanIncidentIds.Clear();a6Orphans=0;
        c.AnalysisVisitIds=new List<int>();c.AnalysisExclusions=new Dictionary<int,string>();
    }
    private void A6MarkOrphan(A4Context c,int id,string reason)
    {
        a6OrphanIncidentIds.Add(id);a6Orphans=a6OrphanIncidentIds.Count;
        string previous;
        if(!c.AnalysisExclusions.TryGetValue(id,out previous))c.AnalysisExclusions[id]=reason;
        else if(!previous.Split(';').Contains(reason))c.AnalysisExclusions[id]=previous+";"+reason;
    }
    private static int A6RawMaximumCountAtThreshold(A4Context c,double threshold)
    {
        double[] raw=A4RawAnalysis(c);int count=0;
        for(int i=0;i<raw.Length;i++)
        {
            double value=Math.Abs(raw[i]-c.RawCenter);
            if(value<threshold)continue;
            double before=i>0?Math.Abs(raw[i-1]-c.RawCenter):-1;
            double after=i+1<raw.Length?Math.Abs(raw[i+1]-c.RawCenter):-1;
            if(value>before&&value>=after)count++;
        }
        return count;
    }
    private string A6IncidentDiagnosticsJson(A4Context c,List<A4Pulse> pulses,List<A4Pair> outputs)
    {
        var seeds=pulses.Where(p=>A4ChargeIndex(p)>=0&&A4ChargeIndex(p)<c.Raw.Length&&
            A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold)
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(p=>A4ChargeIndex(p)).ToList();
        var visited=new HashSet<int>(c.AnalysisVisitIds);
        var windows=new HashSet<int>((c.WindowTraces??new List<A4WindowTrace>()).Select(w=>w.Seed));
        var edges=a6Evidence.Keys.GroupBy(p=>p.Incident.Id).ToDictionary(g=>g.Key,g=>g.ToList());
        var dispositions=new Dictionary<string,int>();
        var details=new List<string>();
        for(int index=0;index<seeds.Count;index++)
        {
            A4Pulse seed=seeds[index];List<A4Pair> candidates;
            if(!edges.TryGetValue(seed.Id,out candidates))candidates=new List<A4Pair>();
            int eligible=candidates.Count(p=>a6Evidence[p].Eligible);
            int reviewMeasurable=candidates.Count(p=>A6ReviewMeasurable(a6Evidence[p]));
            A4Pair output=outputs.FirstOrDefault(p=>p.Incident.Id==seed.Id);
            A4Pair reflectionOwner=outputs.FirstOrDefault(p=>p.State=="accepted"&&p.Reflection.Id==seed.Id);
            string exclusion;c.AnalysisExclusions.TryGetValue(seed.Id,out exclusion);
            string disposition=output!=null?(output.State=="accepted"?"accepted_pair_output":"review_pair_output"):
                reflectionOwner!=null?"packet_owned_as_accepted_reflection_not_counted_again":
                !visited.Contains(seed.Id)?(index>=c.AnalysisLimit?"known_packet_beyond_incident_analysis_cap":"not_visited_discovery_or_candidate_budget_boundary"):
                a6OrphanIncidentIds.Contains(seed.Id)?"unpaired_incident_no_measurable_review_pair":"not_output_unresolved_diagnostic_state";
            int old;dispositions.TryGetValue(disposition,out old);dispositions[disposition]=old+1;
            if(index>=A6IncidentDiagnosticDetailLimit)continue;
            details.Add("{\"packet_id\":"+seed.Id+",\"rank_by_raw_charge_1based\":"+(index+1)+
                ",\"raw_peak\":"+A4ChargeIndex(seed)+",\"raw_start\":"+seed.Start+",\"raw_end\":"+seed.End+
                ",\"raw_charge_pc\":"+NumberJson(A4RawCharge(c,A4ChargeIndex(seed)))+
                ",\"discovery_window_seed\":"+(windows.Contains(A4ChargeIndex(seed))?"true":"false")+
                ",\"within_incident_analysis_cap\":"+(index<c.AnalysisLimit?"true":"false")+
                ",\"visited_for_pair_evaluation\":"+(visited.Contains(seed.Id)?"true":"false")+
                ",\"candidate_count\":"+candidates.Count+",\"all_enabled_gate_pass_count\":"+eligible+
                ",\"review_measurable_candidate_count\":"+reviewMeasurable+
                ",\"output_pair_count\":"+(output==null?0:1)+",\"accepted_output_count\":"+(output!=null&&output.State=="accepted"?1:0)+
                ",\"owner_edge_id\":"+seed.Owner+",\"accepted_reflection_owner_edge_id\":"+(reflectionOwner==null?-1:reflectionOwner.EdgeId)+
                ",\"disposition\":"+Json(disposition)+",\"exclusion_reasons\":"+StringArrayJson((exclusion??"").Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries))+
                ",\"candidate_failure_counts\":{\"entries\":["+String.Join(",",candidates.SelectMany(p=>a6Evidence[p].Reasons).GroupBy(r=>r).OrderBy(g=>g.Key)
                    .Select(g=>"{\"reason\":"+Json(g.Key)+",\"count\":"+g.Count()+"}"))+"]}}");
        }
        var b=new StringBuilder("{\"scope\":\"read_only_per_call_diagnostics_not_extra_discharge_rows\"");
        b.Append(",\"orphan_count_basis\":\"unique_unpaired_incident_packet_ids_not_failure_stage_count\"")
            .Append(",\"output_threshold_pc\":").Append(NumberJson(c.IncidentThreshold))
            .Append(",\"raw_discovery_floor_pc\":").Append(NumberJson(c.RawDiscoveryThreshold))
            .Append(",\"effective_raw_seed_threshold_pc\":").Append(NumberJson(Math.Max(c.RawDiscoveryThreshold,c.IncidentThreshold)))
            .Append(",\"raw_local_maxima_at_output_threshold\":").Append(A6RawMaximumCountAtThreshold(c,c.IncidentThreshold))
            .Append(",\"raw_local_maxima_after_discovery_floor\":").Append(c.RawSeedOrder==null?0:c.RawSeedOrder.Count)
            .Append(",\"raw_local_maxima_are_not_independent_discharge_count\":true")
            .Append(",\"canonical_packets_discovered\":").Append(pulses.Count)
            .Append(",\"known_packets_at_output_threshold\":").Append(seeds.Count)
            .Append(",\"discovery_windows_created\":").Append(c.WindowTraces==null?0:c.WindowTraces.Count)
            .Append(",\"discovery_window_and_incident_count_limit\":").Append(c.AnalysisLimit)
            .Append(",\"analyzed_unique_incident_count\":").Append(visited.Count)
            .Append(",\"known_packets_beyond_incident_cap\":").Append(Math.Max(0,seeds.Count-c.AnalysisLimit))
            .Append(",\"unique_orphan_incident_count\":").Append(a6OrphanIncidentIds.Count)
            .Append(",\"candidate_evaluations\":").Append(c.CandidateEvaluations)
            .Append(",\"all_enabled_gate_pass_candidates\":").Append(a6Evidence.Values.Count(e=>e.Eligible))
            .Append(",\"review_measurable_candidates\":").Append(a6Evidence.Values.Count(A6ReviewMeasurable))
            .Append(",\"pair_outputs_before_final_output_threshold\":").Append(outputs.Count)
            .Append(",\"accepted_pair_outputs\":").Append(outputs.Count(p=>p.State=="accepted"))
            .Append(",\"review_pair_outputs\":").Append(outputs.Count(p=>p.State!="accepted"))
            .Append(",\"detail_limit\":").Append(A6IncidentDiagnosticDetailLimit)
            .Append(",\"seed_details_truncated\":").Append(seeds.Count>A6IncidentDiagnosticDetailLimit?"true":"false")
            .Append(",\"analysis_exclusions_truncated\":").Append(c.AnalysisExclusions.Count>A6IncidentDiagnosticDetailLimit?"true":"false")
            .Append(",\"disposition_counts\":{").Append(String.Join(",",dispositions.OrderBy(v=>v.Key).Select(v=>Json(v.Key)+":"+v.Value))).Append('}')
            .Append(",\"analysis_exclusions\":[").Append(String.Join(",",c.AnalysisExclusions.OrderBy(v=>v.Key).Take(A6IncidentDiagnosticDetailLimit)
                .Select(v=>"{\"packet_id\":"+v.Key+",\"reasons\":"+StringArrayJson(v.Value.Split(';'))+"}"))).Append(']')
            .Append(",\"incident_seed_dispositions\":[").Append(String.Join(",",details)).Append(']')
            .Append(",\"discovery_coverage_limit\":\"Only discovered canonical packets have per-packet dispositions; raw maxima suppressed inside a packet or beyond discovery-window cap are not asserted to be independent events.\"")
            .Append(",\"review_coverage_rule\":\"Ordinary and boundary review require two valid guarded measurements, valid signed temporal correlation and both SNR at least 3. The separately labelled shape-unresolved candidate requires both SNR at least 4.5, reliable enabled CAL with passed attenuation, original lobe evidence and any enabled width gate; correlation remains unavailable, confidence is at most 0.49 and acceptance is zero. No arbitrary reflection is emitted for an unpaired packet.\"")
            .Append('}');
        return b.ToString();
    }
}
