using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private string a6ShapeReviewDiagnosticJson="{\"enabled\":false,\"reason\":\"not_run\"}";
    private const double A6ShapeReviewMaximumConfidence=.49;

    private static bool A6ShapeReviewReasonAllowed(string reason)
    {
        if(reason==null)return false;
        foreach(string prefix in new[]{"incident_","reflection_"})
            if(reason==prefix+"temporal_first_raw_lobe_has_no_unique_significant_processed_correspondence"||
                reason==prefix+"temporal_second_raw_lobe_order_or_processed_correspondence_unresolved")return true;
        return false;
    }

    private static bool A6ShapeReviewCompleteOriginal(A4Context c,A4Pulse p)
    {
        if(c==null||p==null||c.Raw==null||!p.MappingValid||p.Clipped||p.UnresolvedOverlap||
            p.Start<0||p.End>=c.Raw.Length||p.Start>=p.End)return false;
        int peak=A4ChargeIndex(p);
        return peak>=p.Start&&peak<=p.End&&Finite(A4RawCharge(c,peak));
    }

    // This is NOT a substitute correlation measurement. It exposes a real,
    // independently guarded candidate as a low-confidence human-review record.
    // Missing correlation remains unavailable and contributes zero score.
    private bool A6ShapeReviewEligible(A4Context c,A4Pair pair,A6Evidence e,A6Settings settings,
        out string reason)
    {
        reason="not_shape_unresolved_candidate";
        if(c==null||pair==null||e==null||settings==null||e.Correlation==null||e.Correlation.Valid||
            e.Correlation.OppositeTemporalPolarity||!A6ShapeReviewReasonAllowed(e.Correlation.StructureReason))return false;
        if(!A6ShapeReviewCompleteOriginal(c,pair.Incident)||!A6ShapeReviewCompleteOriginal(c,pair.Reflection)||
            pair.Incident.Id==pair.Reflection.Id||pair.Incident.End>=pair.Reflection.Start)
        {reason="original_packets_not_complete_distinct_and_separate";return false;}
        if(e.A==null||e.B==null||!e.A.Valid||!e.B.Valid||!Finite(e.A.Snr)||!Finite(e.B.Snr)||
            e.A.Snr<A6PairSnrFloor||e.B.Snr<A6PairSnrFloor||
            !Finite(e.A.Amplitude)||!Finite(e.B.Amplitude)||e.A.Amplitude<=0||e.B.Amplitude<=0||e.A.End>=e.B.Start)
        {reason="two_independent_guarded_measurements_above_4point5_sigma_required";return false;}
        int ia=A4ChargeIndex(pair.Incident),ib=A4ChargeIndex(pair.Reflection);
        double maximum=A4BoundaryReviewDelayLimit(c);
        if(!Finite(maximum)||maximum<=0||ib<=ia||ib-ia>Math.Ceiling(maximum)||
            pair.OutputIncident!=ia||pair.OutputReflection!=ib||!Finite(pair.Position))
        {reason="canonical_raw_delay_or_geometry_unavailable_or_outside_existing_candidate_window";return false;}
        // When CAL was disabled/unavailable the main matcher can still run, but
        // this exceptional missing-shape pathway has no independent attenuation
        // constraint and therefore is deliberately unavailable.
        if(!settings.UseAttenuation||!a6ReferenceReliable||e.Attenuation==null||
            !e.Attenuation.Available||!e.Attenuation.Passed)
        {reason="explicitly_enabled_reliable_calibration_and_passed_attenuation_required";return false;}
        if(settings.UseWidth&&(e.Width==null||!e.Width.Available||!e.Width.Passed))
        {reason="enabled_width_requirement_not_satisfied";return false;}
        if(e.LobeA==null||e.LobeB==null||!e.LobeA.Valid||!e.LobeB.Valid)
        {reason="two_significant_original_lobe_structures_required";return false;}
        if(!Finite(e.Score)||e.Score<0||e.Score>100||Finite(e.CorrelationScore))
        {reason="missing_correlation_must_not_have_a_substituted_score";return false;}
        foreach(string failure in e.Reasons)
            if(failure!="engineering_score_below_70"&&
                failure!="delay_exceeds_cable_round_trip_plus_two_sample_quantization_allowance_signed_position_retained"&&
                failure!="pearson_correlation_unavailable:"+e.Correlation.StructureReason)
            {reason="additional_independent_failure_not_waived:"+failure;return false;}
        reason="guarded_attenuation_constrained_shape_unresolved_candidate_only";return true;
    }

    // Invoke after the primary graph and optional boundary-review pass. It never
    // replaces an existing accepted/review incident and never acquires ownership.
    private void A6RecoverShapeUnresolvedReviewPairs(A4Context c,A6Settings settings,List<A4Pair> outputs)
    {
        a6ShapeReviewDiagnosticJson="{\"enabled\":false,\"reason\":\"method6_context_unavailable\"}";
        if(c==null||c.Algorithm!=5||settings==null||outputs==null)return;
        if(c.WindowBudgetExceeded||c.CandidateEvaluationLimitExceeded)
        {a6ShapeReviewDiagnosticJson="{\"enabled\":true,\"reason\":\"primary_discovery_incomplete_no_shape_hypothesis_output\",\"recovered_review_pairs\":0}";return;}
        var represented=new HashSet<int>(outputs.Select(p=>p.Incident.Id));
        var owned=new HashSet<int>(outputs.Where(p=>p.State=="accepted").SelectMany(p=>new[]{p.Incident.Id,p.Reflection.Id}));
        var visited=new HashSet<int>(c.AnalysisVisitIds);
        var proposals=new List<A4Pair>();var attempts=new List<string>();
        foreach(var item in a6Evidence.OrderBy(x=>x.Key.OutputIncident).ThenBy(x=>x.Key.OutputReflection))
        {
            A4Pair pair=item.Key;A6Evidence e=item.Value;
            if(represented.Contains(pair.Incident.Id)||owned.Contains(pair.Incident.Id)||owned.Contains(pair.Reflection.Id)||
                !visited.Contains(pair.Incident.Id)||A4RawCharge(c,A4ChargeIndex(pair.Incident))<c.IncidentThreshold)continue;
            if(e.Correlation==null||e.Correlation.Valid||!A6ShapeReviewReasonAllowed(e.Correlation.StructureReason))continue;
            string reason;bool eligible=A6ShapeReviewEligible(c,pair,e,settings,out reason);
            attempts.Add("{\"raw_incident_peak\":"+A4ChargeIndex(pair.Incident)+",\"raw_reflection_peak\":"+A4ChargeIndex(pair.Reflection)+
                ",\"candidate_can_be_shown_for_review\":"+(eligible?"true":"false")+",\"reason\":"+Json(reason)+
                ",\"shape_failure\":"+Json(e.Correlation.StructureReason)+",\"measured_evidence_score_percent\":"+NumberJson(e.Score)+"}");
            if(eligible)proposals.Add(pair);
        }
        var recovered=new List<string>();
        foreach(var group in proposals.GroupBy(p=>p.Incident.Id).OrderBy(g=>g.Min(p=>p.OutputIncident)))
        {
            // No spatial prior can manufacture missing shape evidence or turn
            // this into an accepted event. Ties use deterministic raw time.
            A4Pair pair=group.OrderByDescending(p=>a6Evidence[p].Score).ThenBy(p=>p.OutputReflection).First();
            A6Evidence e=a6Evidence[pair];
            e.Eligible=false;e.Selected=false;e.Selection="shape_unresolved_candidate_human_review_only";
            e.Reasons.Add("shape_not_validated_candidate_only_never_accepted");
            pair.State="review";pair.Ambiguous=true;pair.CandidateCount=group.Count();
            pair.Confidence=Math.Min(A6ShapeReviewMaximumConfidence,Math.Max(0,e.Score/100));
            pair.Correlation=double.NaN;pair.WaveCorrelation=double.NaN;
            pair.ReviewReason=String.Join(";",e.Reasons.Concat(new[]{e.Selection}));
            outputs.Add(pair);represented.Add(pair.Incident.Id);
            a6OrphanIncidentIds.Remove(pair.Incident.Id);
            c.AnalysisExclusions[pair.Incident.Id]="shape_unresolved_real_candidate_retained_for_human_review_not_accepted";
            recovered.Add("{\"raw_incident_peak\":"+pair.OutputIncident+",\"raw_reflection_peak\":"+pair.OutputReflection+
                ",\"position_m\":"+NumberJson(pair.Position)+",\"output_score_percent\":"+NumberJson(100*pair.Confidence)+
                ",\"shape_validated\":false,\"accepted\":false,\"correlation_measured\":false,\"eligible_candidates_for_incident\":"+group.Count()+"}");
        }
        a6Orphans=a6OrphanIncidentIds.Count;
        a6ShapeReviewDiagnosticJson="{\"enabled\":true,\"scope\":\"post_graph_original_candidate_shape_unresolved_review_only\","+
            "\"existing_accepted_and_review_pairs_unchanged\":true,\"raw_indices_and_charge_unchanged\":true,"+
            "\"missing_correlation_not_substituted_or_reweighted\":true,\"disabled_attenuation_disables_this_extra_pathway\":true,"+
            "\"review_score_cap_percent\":49,\"spatial_history_cannot_promote_acceptance\":true,\"recovered_review_pairs\":"+recovered.Count+
            ",\"attempts\":["+String.Join(",",attempts)+"],\"rows\":["+String.Join(",",recovered)+"]}";
    }

    // Exact caller-supplied canonical raw identities are required because a
    // displayed leading-edge index is NOT necessarily the canonical raw peak.
    // This diagnostic never deletes a row or changes the numeric API.
    private static string A4AllOutputPulseRoleJson(double[,] parameters,int[] incidentRawPeaks,int[] reflectionRawPeaks,bool causalRoleAudit=false)
    {
        if(parameters==null||parameters.GetLength(1)<36||incidentRawPeaks==null||reflectionRawPeaks==null||
            incidentRawPeaks.Length!=parameters.GetLength(0)||reflectionRawPeaks.Length!=parameters.GetLength(0))
            return "{\"available\":false,\"reason\":\"exact_canonical_output_bindings_required\"}";
        var rows=new List<string>();int related=0,unpaired=0;
        for(int r=0;r<parameters.GetLength(0);r++)
        {
            int raw=incidentRawPeaks[r];bool paired=Finite(parameters[r,11])&&Finite(parameters[r,12])&&reflectionRawPeaks[r]>=0;
            int[] parents=raw<0?new int[0]:Enumerable.Range(0,parameters.GetLength(0)).Where(i=>i!=r&&
                reflectionRawPeaks[i]==raw&&Finite(parameters[i,11])&&Finite(parameters[i,12])).ToArray();
            bool acceptedParent=parents.Any(i=>parameters[i,35]==1);
            string role=raw<0?"canonical_identity_unavailable":paired?
                (parameters[r,35]==1?"accepted_pair_incident":"review_pair_incident"):
                parents.Length>0?(acceptedParent?"retained_pulse_already_an_accepted_pair_reflection":"retained_pulse_already_a_review_pair_reflection"):
                "unpaired_pulse_without_output_pair_role";
            if(parents.Length>0)related++;if(!paired)unpaired++;
            rows.Add("{\"row\":"+r+",\"canonical_raw_peak\":"+(raw<0?"null":raw.ToString())+
                ",\"canonical_raw_reflection\":"+(paired?reflectionRawPeaks[r].ToString(Inv):"null")+
                ",\"state\":"+Json(role)+",\"has_output_reflection\":"+(paired?"true":"false")+
                ",\"show_reflection_cursor\":"+(paired&&parameters[r,11]>=parameters[r,31]&&parameters[r,11]<=parameters[r,32]?"true":"false")+",\"reflection_of_output_rows\":["+String.Join(",",parents)+
                "],\"independent_discharge_not_established\":"+((!paired||(causalRoleAudit&&(parents.Length>0||parameters[r,35]!=1)))?"true":"false")+
                ",\"may_count_as_additional_independent_discharge\":"+((!paired||parents.Length>0)?"false":"null")+"}");
        }
        return "{\"available\":true,\"scope\":\"current_output_exact_canonical_packet_roles_all_rows_retained\","+
            "\"numeric_outputs_changed\":false,\"row_count_is_not_confirmed_discharge_count\":true,"+
            "\"related_reflection_pulse_rows\":"+related+",\"rows_without_output_reflection\":"+unpaired+
            ",\"cursor_rule\":\"show_only_if_finite_column11_and_column12;never_use_window_end_for_missing_reflection\",\"rows\":["+String.Join(",",rows)+"]}";
    }
}
