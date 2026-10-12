using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private double projectCurrentVoltage;
    private bool projectCurrentAutomatic;
    private int[] projectRetainedRawPeaks=new int[0];
    private List<A4Pulse> projectRetainedPackets=new List<A4Pulse>();

    private sealed class A4RetainedOutputRow
    {
        internal double[] P,R,F,E;
        internal A4CanonicalOutputIdentity Identity;
        internal int Peak;
        internal int PreRetentionRow=-1;
        internal string State,Reason,EquivalentDetail;
    }

    // This runs AFTER physical matching, review, equivalent calculation and
    // merge. It does not add new pairs, change scores, or feed retained peaks
    // back into matching. A missing reflection is never the window's end.
    internal void CompleteProjectRetainedOutput(ref double[,] p,ref double[,] raw,
        ref double[,] filtered,ref double[,] equivalent,bool customWindow,double[] windowNs)
    {
        A4Context c=equivalentContext;
        if(c==null||c.Algorithm<0||c.Algorithm>4)return;
        if(canonicalMixedRows.Length!=p.GetLength(0))
            throw new InvalidOperationException("Project canonical mixed rows are unavailable.");
        var rows=new List<A4RetainedOutputRow>();var represented=new HashSet<int>();
        for(int r=0;r<p.GetLength(0);r++)
        {
            A4CanonicalOutputIdentity identity=canonicalMixedRows[r];
            if(identity==null||identity.Pair==null)
                throw new InvalidOperationException("Project current pair identity is missing.");
            int peak=A4ChargeIndex(identity.Pair.Incident);represented.Add(peak);
            rows.Add(new A4RetainedOutputRow {P=A4Row(p,r),R=A4Row(raw,r),F=A4Row(filtered,r),E=A4Row(equivalent,r),
                Identity=identity,Peak=peak,PreRetentionRow=r,State=p[r,35]==1?"accepted_pair":"review_pair",Reason=identity.Pair.ReviewReason});
        }
        A6RetentionInventory inventory=A6BuildRetentionInventory(c,widthDiagnosticPackets);
        A4Index4EchoReviewResult echo=A4ReviewIndex4EchoTopology(c,widthDiagnosticPackets);
        // A complete graph review cannot prove uniqueness across packets that the
        // subsequent raw inventory discovered outside that graph. Retain them.
        if(inventory.Incomplete||inventory.Packets.Count>0)
        {
            echo.Complete=false;echo.Reason="retention_inventory_not_covered_by_matching_diagnostic_only";
            echo.DuplicateReviewOnlyPairs.Clear();echo.ConfirmedEchoPulseIds.Clear();
            foreach(var chain in echo.Chains)
            {chain.ReviewOnlyRole=false;if(chain.Qualified)chain.Reason=echo.Reason;}
            echo.DiagnosticJson=A4Index4EchoReviewJson(echo);
        }
        var packets=widthDiagnosticPackets.Concat(inventory.Packets).OrderByDescending(q=>A4RawCharge(c,A4ChargeIndex(q)))
            .ThenBy(A4ChargeIndex).ToList();
        projectRetainedPackets=packets;
        bool custom;string normalization;
        double[] times=A4NormalizeEquivalent(customWindow,windowNs,c.Fs,out custom,out normalization);
        // A review decision concerns the PAIR, not whether its independent
        // incident pulse has measurable time-frequency features. The legacy
        // mixed stage deliberately blanked every review E row. Recover only
        // measurable incident features here, after all scoring/ownership is over.
        // The shared single-pulse helper still refuses clipped, unresolved,
        // nonfinite and zero-energy support. Confidence and acceptance stay intact.
        // Methods 0..3 keep every existing pair and feature bit-for-bit; only
        // previously discarded unpaired pulses are added as review rows.
        foreach(A4RetainedOutputRow review in rows.Where(q=>c.Algorithm==4&&q.P[35]==0))
        {
            A4Pulse incident=review.Identity.Pair.Incident;
            var one=A4Matrix(new List<double[]>{review.P},36);string eqDetail;
            review.E=A6UnpairedEquivalentWaveform(incident,one,0,custom,times,out eqDetail);
            review.P[23]=one[0,23];review.P[24]=one[0,24];review.EquivalentDetail=eqDetail;
        }
        var accounted=new List<string>();var visited=new HashSet<int>(c.AnalysisVisitIds);
        foreach(A4Pulse pulse in packets)
        {
            int peak=A4ChargeIndex(pulse);double charge=A4RawCharge(c,peak);
            if(!A4AnalysisPhaseEligible(c,peak))continue;
            if(charge<c.IncidentThreshold)continue;
            string disposition;
            if(represented.Contains(peak))disposition="represented_as_pair_incident";
            else if(pulse.Owner>=0)disposition="owned_primary_reflection_or_confirmed_causal_echo_not_an_extra_discharge";
            else if(echo.ConfirmedEchoPulseIds.Contains(pulse.Id))
                disposition="unique_confirmed_source_round_trip_echo_not_an_extra_discharge_see_echo_topology";
            else if(rows.Count>=c.AnalysisLimit)
                disposition="excluded_by_maximum_output_pulse_count";
            else
            {
                represented.Add(peak);
                int time=peak;
                if(c.FirstArrival){int first=A4RawLeadingEdge(c,pulse,peak);if(first>=0&&first<=peak)time=first;}
                int start,end;
                if(!A4CompletePdmapCrop(c,peak,time,EffectiveIncidentPreExtensionPoints,EffectiveRoundTripPostExtensionPoints,out start,out end))
                {
                    accounted.Add("{\"packet_id\":"+pulse.Id+",\"raw_peak\":"+peak+",\"raw_charge\":"+NumberJson(charge)+
                        ",\"disposition\":\"excluded_pdmap_incomplete_original_sample_window\"}");
                    continue;
                }
                double[] row=NewNaNRow();
                row[0]=reviewCurrentPhase;row[1]=reviewCurrentRecordId;row[2]=projectCurrentVoltage;
                row[3]=Finite(EffectiveOscillationFrequencyHz)&&EffectiveOscillationFrequencyHz>0?
                    NormalizeDegrees(EffectiveInitialPhaseDegrees+360*EffectiveOscillationFrequencyHz*peak/c.Fs):NormalizeDegrees(EffectiveInitialPhaseDegrees);
                row[4]=c.IncidentThreshold;row[5]=c.Length;row[6]=c.Velocity;row[7]=c.Fs;row[8]=reviewCurrentRecordId;
                row[9]=rows.Count;row[10]=time;row[14]=c.RoundTrip;row[15]=charge;row[18]=pulse.Energy/c.Fs;
                row[19]=ResolvedTanDelta;row[20]=time-start;row[25]=ResolvedTanDelta;row[26]=c.Algorithm;row[27]=c.Suppression;
                row[28]=c.Sigma;row[29]=A4RawBaselineAt(c,peak)+c.IncidentThreshold;
                row[30]=A4RawBaselineAt(c,peak)-c.IncidentThreshold;row[31]=start;row[32]=end;
                row[33]=0;row[34]=projectCurrentAutomatic?1:0;row[35]=0;
                string reason;
                if(!inventory.Reasons.TryGetValue(pulse.Id,out reason)&&!c.AnalysisExclusions.TryGetValue(pulse.Id,out reason))
                    reason=visited.Contains(pulse.Id)?"no_reliable_or_measurable_final_pair":"not_analyzed_incident_cap_or_discovery_scope";
                var one=A4Matrix(new List<double[]>{row},36);string eqDetail;
                double[] eq=A6UnpairedEquivalentWaveform(pulse,one,0,custom,times,out eqDetail);
                row[23]=one[0,23];row[24]=one[0,24];
                ReviewedMatrixBudget(rows.Count+1,Math.Max(end-start+1,rows.Select(v=>v.R.Length).DefaultIfEmpty(0).Max()));
                rows.Add(new A4RetainedOutputRow {P=row,R=A4Slice(c.Raw,start,end),F=A4MatchedFilteredView(c,pulse,start,end),E=eq,
                    Peak=peak,State="unpaired",Reason=reason,EquivalentDetail=eqDetail});
                disposition="retained_unpaired_without_fabricated_position_or_reflection";
            }
            accounted.Add("{\"packet_id\":"+pulse.Id+",\"raw_peak\":"+peak+",\"raw_charge\":"+NumberJson(charge)+
                ",\"disposition\":"+Json(disposition)+"}");
        }
        rows=rows.OrderByDescending(q=>q.P[15]).ThenBy(q=>q.Peak).ToList();
        var details=new List<string>();
        for(int r=0;r<rows.Count;r++)
        {
            var row=rows[r];row.P[9]=r;
            details.Add("{\"row\":"+r+",\"pre_retention_row\":"+(row.PreRetentionRow<0?"null":row.PreRetentionRow.ToString(Inv))+
                ",\"raw_peak\":"+row.Peak+",\"state\":"+Json(row.State)+",\"reason\":"+Json(row.Reason??"")+
                ",\"equivalent\":"+(row.EquivalentDetail==null?"null":"{"+row.EquivalentDetail+"}")+"}");
        }
        string finalWindows=ReviewedWindowJson(c,rows.Select(q=>q.P));
        ReviewedMatrixBudget(rows.Count,rows.Select(q=>q.R.Length).DefaultIfEmpty(0).Max());
        ReviewedMatrixBudget(rows.Count,rows.Select(q=>q.E.Length).DefaultIfEmpty(0).Max());
        double[,] nextP=A4Matrix(rows.Select(q=>q.P).ToList(),36),nextR=A4Matrix(rows.Select(q=>q.R).ToList(),0),
            nextF=A4Matrix(rows.Select(q=>q.F).ToList(),0),nextE=A4Matrix(rows.Select(q=>q.E).ToList(),0);
        string append=",\"final_window_review\":"+finalWindows+",\"final_coverage_review\":"+ReviewedCoverageJson(c,packets,AppliedMaximumDischargeCount)+
            ",\"output_pulse_roles\":"+A4AllOutputPulseRoleJson(nextP,rows.Select(q=>q.Peak).ToArray(),
            rows.Select(q=>q.Identity==null||q.Identity.Pair==null?-1:A4ChargeIndex(q.Identity.Pair.Reflection)).ToArray())+
            ",\"index4_echo_topology\":"+echo.DiagnosticJson+",\""+(c.Algorithm==4?"index4_retained_output":"project_retained_output")+"\":{\"policy\":\"raw_threshold_packet_accounting_after_matching_no_fabricated_pairs\",\"matching_unchanged_by_retention\":true,"+
            "\"retained_output_obeys_analysis_pulse_cap\":true,\"unpaired_score_zero_means_not_scored\":true,"+
            "\"row_space\":\"final_current_output_all_four_matrices\",\"earlier_stage_row_space\":\"physical_and_review_before_retention_merge_use_canonical_raw_indices_and_pre_retention_row_mapping\","+
            "\"missing_reflection_not_window_end\":true,\"missing_pair_columns_zero_based\":[11,12,13,16,17,21,22],"+
            "\"review_incident_equivalent_features\":\"measured_after_legacy_merge_when_complete_support_available_not_pair_acceptance\","+
            "\"current_row_count\":"+rows.Count+",\"unpaired_rows\":"+rows.Count(q=>q.Identity==null)+
            ",\"raw_inventory\":"+A6RetentionInventoryJson(inventory)+",\"threshold_packets\":["+String.Join(",",accounted)+"],\"rows\":["+String.Join(",",details)+"]},"+
            "\"index4_display_amplitude\":{\"scope\":\"display_only_single_common_gain_from_fixed_incident_support\",\"raw_charge_and_scoring_unchanged\":true,"+
            "\"equivalent_waveform_not_rescaled\":true,\"rows\":["+String.Join(",",c.Index4DisplayDiagnostics.OrderBy(k=>k.Key).Select(k=>k.Value))+ "]}";
        canonicalMixedRows=rows.Select(q=>q.Identity).ToArray();projectRetainedRawPeaks=rows.Select(q=>q.Peak).ToArray();p=nextP;raw=nextR;filtered=nextF;equivalent=nextE;
        StatusJson=StatusJson.Replace("\"equivalent_waveform\":{\"scope\":\"all_rows_including_history\"",
            "\"equivalent_waveform\":{\"scope\":\"physical_subset_before_review_and_index4_retention_use_raw_peak_anchor\"");
        StatusJson=StatusJson.Substring(0,StatusJson.LastIndexOf('}'))+append+"}";LastResultJson=StatusJson;
    }
}
