using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Output-role evidence only. Call AFTER A4Match has committed the ordinary graph.
    // No signal, cache, owner, pair score, position, existing collection or matrix is changed.
    // A caller may retain DuplicateReviewOnlyPairs as unaccepted review rows and avoid
    // recounting ConfirmedEchoPulseIds as independent incidents. Preserve their raw indices.
    // "Confirmed" here qualifies the source's graph role, NOT independently labelled truth.
    private sealed class A4Index4EchoReviewResult
    {
        internal bool Enabled,Complete=true;
        internal string Reason="not_evaluated";
        internal int Steps;
        internal readonly HashSet<A4Pair> DuplicateReviewOnlyPairs=new HashSet<A4Pair>();
        internal readonly HashSet<int> ConfirmedEchoPulseIds=new HashSet<int>();
        internal readonly List<A4Index4EchoChain> Chains=new List<A4Index4EchoChain>();
        internal readonly List<string> EndpointHypotheses=new List<string>();
        internal string DiagnosticJson="{}";
    }
    private sealed class A4Index4EchoChain
    {
        internal A4Pair Source,Later;
        internal int Delay01,Delay12,Tolerance,SourceCount;
        internal double Ratio01=double.NaN,Ratio12=double.NaN,Noise01=double.NaN,Noise12=double.NaN;
        internal double RatioDifferenceDb=double.NaN,AllowedRatioDifferenceDb=double.NaN;
        internal bool Qualified,SourceConfirmed,ReviewOnlyRole;
        internal string Reason="not_evaluated";
    }
    private static bool A4Index4ReviewStep(A4Index4EchoReviewResult r)
    {
        if(r.Steps>=A4MaximumRoleReviewSteps)
        {r.Complete=false;r.Reason="index4_echo_review_budget_incomplete";return false;}
        r.Steps++;return true;
    }
    private static bool A4Index4PacketReliable(A4Context c,A4Pulse p)
    {
        if(c==null||c.Raw==null||p==null||!A4RoleEndpointReliable(p)||!Finite(p.Snr)||p.Snr<8||
            p.Start<0||p.End>=c.Raw.Length||p.End<=p.Start)return false;
        int peak=A4ChargeIndex(p);
        return peak>=p.Start&&peak<=p.End;
    }
    private static bool A4Index4FixedMeasurementReliable(A4AttenuationMeasurement m)
    {
        return m!=null&&m.Valid&&Finite(m.Amplitude)&&m.Amplitude>0&&
            Finite(m.Noise)&&m.Noise>=0&&Finite(m.Snr)&&m.Snr>=8&&
            m.Start>=0&&m.End>m.Start&&m.Peak>=m.Start&&m.Peak<=m.End;
    }
    private static bool A4Index4StepEvidence(A4Context c,A4Pair p,out string reason)
    {
        reason="unreliable_or_overlapping_packet_support";
        if(p==null||!A4Index4PacketReliable(c,p.Incident)||!A4Index4PacketReliable(c,p.Reflection)||
            p.Incident.End>=p.Reflection.Start||p.Blind||p.Amplified||p.ParallelAmbiguous||p.BoundaryAmbiguous||
            !Finite(p.Confidence)||p.Confidence<=A4MinimumOutputConfidence)return false;
        A4UnifiedCorrelationEvidence u=p.UnifiedCorrelation;
        reason="fixed_band_dynamic_correlation_or_temporal_polarity_unavailable";
        if(u==null||!u.Available||!u.Passed||u.Measured==null||!u.Measured.Valid||
            !u.Measured.StructureCompatible||u.Measured.OppositeTemporalPolarity||
            !Finite(u.Measured.PolarityRatio)||u.Measured.PolarityRatio<=0||
            !Finite(u.Measured.Fixed)||!Finite(u.Floor)||u.Measured.Fixed<u.Floor||
            u.CanonicalDelay!=A4ChargeIndex(p.Reflection)-A4ChargeIndex(p.Incident)||
            !A4Index4FixedMeasurementReliable(u.Incident)||!A4Index4FixedMeasurementReliable(u.Reflection))return false;
        // Reuse the already evaluated CAL interval. Missing/disabled CAL never creates
        // an invented retention target. A usable enabled CAL must actually be satisfied.
        if(c.Attenuation&&c.AttenuationScoringReference!=null&&c.AttenuationScoringReference.Valid)
        {
            A4AttenuationEvidence a=p.CalibrationDeviation;
            reason="enabled_calibration_interval_not_satisfied_or_unmeasured";
            if(a==null||!a.Applied||!Finite(a.ExcessDb)||a.ExcessDb>0)return false;
        }
        reason="existing_fixed_band_evidence_passed";return true;
    }
    private static bool A4Index4IsRoundTrip(A4Context c,A4Pair p,out int delay,out int tolerance)
    {
        delay=0;tolerance=0;
        if(p==null||p.Incident==null||p.Reflection==null||!Finite(c.RoundTripExact)||c.RoundTripExact<=0)return false;
        delay=A4ChargeIndex(p.Reflection)-A4ChargeIndex(p.Incident);
        tolerance=Math.Min(A4EchoTolerance(p.Incident),A4EchoTolerance(p.Reflection));
        return delay>0&&Math.Abs(delay-c.RoundTripExact)<=tolerance;
    }
    private static A4Index4EchoChain A4Index4EvaluateSharedMiddle(A4Context c,A4Pair source,A4Pair later)
    {
        var e=new A4Index4EchoChain{Source=source,Later=later};
        int tolerance01,tolerance12;
        if(source==null||later==null||source.Reflection==null||later.Incident==null||
            !ReferenceEquals(source.Reflection,later.Incident)||
            !A4Index4IsRoundTrip(c,source,out e.Delay01,out tolerance01)||
            !A4Index4IsRoundTrip(c,later,out e.Delay12,out tolerance12))
        {e.Reason="not_three_packets_with_same_middle_identity_and_one_round_trip_steps";return e;}
        e.Tolerance=Math.Min(tolerance01,tolerance12);
        // Two independent quantized intervals may disagree by up to twice their
        // existing timing allowance. No newly fitted tolerance is introduced.
        if(Math.Abs(e.Delay01-e.Delay12)>2*e.Tolerance)
        {e.Reason="round_trip_step_difference_outside_existing_quantization_allowance";return e;}
        string reason;
        if(!A4Index4StepEvidence(c,source,out reason)||!A4Index4StepEvidence(c,later,out reason))
        {e.Reason=reason;return e;}
        var m0=source.UnifiedCorrelation.Incident;var m1=source.UnifiedCorrelation.Reflection;
        var m1b=later.UnifiedCorrelation.Incident;var m2=later.UnifiedCorrelation.Reflection;
        // The shared pulse is measured once in the common fixed band, not in two
        // different display filters or source-dependent amplitude normalizations.
        if(m1.Amplitude!=m1b.Amplitude||m1.Noise!=m1b.Noise||m1.Start!=m1b.Start||
            m1.End!=m1b.End||m1.Peak!=m1b.Peak)
        {e.Reason="middle_fixed_band_measurement_disagrees";return e;}
        e.Ratio01=m1.Amplitude/m0.Amplitude;e.Ratio12=m2.Amplitude/m1.Amplitude;
        e.Noise01=2*Math.Sqrt(m0.Noise*m0.Noise+m1.Noise*m1.Noise);
        e.Noise12=2*Math.Sqrt(m1.Noise*m1.Noise+m2.Noise*m2.Noise);
        if(!Finite(e.Ratio01)||!Finite(e.Ratio12)||e.Ratio01<=0||e.Ratio12<=0||
            !Finite(e.Noise01)||!Finite(e.Noise12)||
            m0.Amplitude-m1.Amplitude<=e.Noise01||m1.Amplitude-m2.Amplitude<=e.Noise12)
        {e.Reason="both_steps_require_noise_resolved_decay_not_periodicity_or_shape_alone";return e;}
        e.RatioDifferenceDb=Math.Abs(20*Math.Log10(e.Ratio01/e.Ratio12));
        // Exactly the existing A4CausalEchoes engineering consistency interval,
        // now in the cached fixed measurement band rather than display amplitudes.
        e.AllowedRatioDifferenceDb=3+20*Math.Log10(1+e.Noise01/m1.Amplitude+e.Noise12/m2.Amplitude);
        if(!Finite(e.RatioDifferenceDb)||!Finite(e.AllowedRatioDifferenceDb)||
            e.RatioDifferenceDb>e.AllowedRatioDifferenceDb)
        {e.Reason="successive_round_trip_decay_ratios_inconsistent";return e;}
        e.Qualified=true;e.Reason="three_packet_round_trip_echo_compatible_not_endpoint_proof";return e;
    }
    private static bool A4Index4SourceConfirmed(A4Pair source)
    {
        return source!=null&&source.State=="confirmed"&&!source.Ambiguous&&
            source.Incident.Owner>=0&&source.Incident.Owner==source.Reflection.Owner;
    }
    private static bool A4Index4OtherConfirmedClaim(IList<A4Pair> pool,A4Pair source,A4Pair later)
    {
        int id=later.Reflection.Id;
        // Do not steal a packet from any independently committed primary pair.
        if(later.Reflection.Owner>=0&&later.Reflection.Owner!=source.Incident.Owner)return true;
        return pool.Any(p=>p!=null&&!ReferenceEquals(p,source)&&p.State=="confirmed"&&
            (p.Incident.Id==id||p.Reflection.Id==id));
    }
    private static int A4Index4LowerBound(int[] peaks,double value)
    {
        int lo=0,hi=peaks.Length;
        while(lo<hi){int mid=lo+(hi-lo)/2;if(peaks[mid]<value)lo=mid+1;else hi=mid;}
        return lo;
    }
    private static A4Index4EchoReviewResult A4ReviewIndex4EchoTopology(A4Context c,IList<A4Pulse> pulses)
    {
        var r=new A4Index4EchoReviewResult{Enabled=c!=null&&c.Algorithm==4};
        if(!r.Enabled){r.Reason="not_index4_unchanged";r.DiagnosticJson=A4Index4EchoReviewJson(r);return r;}
        if(c.Raw==null||pulses==null||c.CandidatePool==null||!Finite(c.RoundTripExact)||c.RoundTripExact<=0)
        {r.Complete=false;r.Reason="missing_record_geometry_or_candidate_inventory";r.DiagnosticJson=A4Index4EchoReviewJson(r);return r;}
        if(pulses.Any(p=>p==null)||pulses.GroupBy(p=>p.Id).Any(g=>g.Count()!=1))
        {r.Complete=false;r.Reason="packet_inventory_identity_conflict";r.DiagnosticJson=A4Index4EchoReviewJson(r);return r;}
        r.Complete=!A4RecordReviewIncomplete(c)&&!c.AnalysisLimitReached;
        r.Reason=r.Complete?"read_only_index4_role_review_complete":"underlying_candidate_or_incident_budget_incomplete";
        var pool=c.CandidatePool.Where(p=>p!=null&&p.Incident!=null&&p.Reflection!=null).ToList();
        var rt=pool.Where(p=>{int d,t;return A4Index4IsRoundTrip(c,p,out d,out t);})
            .OrderBy(p=>A4ChargeIndex(p.Incident)).ThenBy(p=>A4ChargeIndex(p.Reflection)).ThenBy(p=>p.EdgeId).ToList();
        var incoming=rt.GroupBy(p=>p.Reflection.Id).ToDictionary(g=>g.Key,g=>g.ToList());
        foreach(A4Pair later in rt)
        {
            if(!A4Index4ReviewStep(r))break;
            List<A4Pair> predecessors;if(!incoming.TryGetValue(later.Incident.Id,out predecessors))continue;
            foreach(A4Pair source in predecessors)
            {
                if(!A4Index4ReviewStep(r))break;
                var chain=A4Index4EvaluateSharedMiddle(c,source,later);r.Chains.Add(chain);
            }
            if(r.Steps>=A4MaximumRoleReviewSteps)break;
        }
        var qualifiedIncoming=r.Chains.Where(e=>e.Qualified).GroupBy(e=>e.Later)
            .ToDictionary(g=>g.Key,g=>g.ToList());
        foreach(var chain in r.Chains.Where(e=>e.Qualified))
        {
            chain.SourceCount=qualifiedIncoming[chain.Later].Select(e=>e.Source.Incident.Id).Distinct().Count();
            chain.SourceConfirmed=A4Index4SourceConfirmed(chain.Source);
            if(!r.Complete){chain.Reason="incomplete_inventory_diagnostic_only";continue;}
            if(c.EffectiveSuppression<=0){chain.Reason="suppression_disabled_diagnostic_only";continue;}
            if(chain.SourceCount!=1){chain.Reason="multiple_predecessor_sources_diagnostic_only";continue;}
            if(qualifiedIncoming.ContainsKey(chain.Source))
            {chain.Reason="source_itself_has_earlier_echo_hypothesis_no_suppression_order_restart";continue;}
            if(!chain.SourceConfirmed){chain.Reason="source_not_globally_confirmed_diagnostic_only";continue;}
            if(A4Index4OtherConfirmedClaim(pool,chain.Source,chain.Later))
            {chain.Reason="later_packet_has_independent_confirmed_owner_preserved";continue;}
            chain.ReviewOnlyRole=true;chain.Reason="unique_confirmed_source_compatible_echo_role_review_only";
            r.DuplicateReviewOnlyPairs.Add(chain.Later);r.ConfirmedEchoPulseIds.Add(chain.Later.Reflection.Id);
        }
        A4Pulse[] ordered=pulses.OrderBy(A4ChargeIndex).ThenBy(p=>p.Id).ToArray();
        int[] peaks=ordered.Select(A4ChargeIndex).ToArray();
        foreach(A4Pair pair in rt)
        {
            if(!A4Index4ReviewStep(r))break;
            int delay,tolerance;A4Index4IsRoundTrip(c,pair,out delay,out tolerance);
            double target=A4ChargeIndex(pair.Incident)-c.RoundTripExact;
            double low=target-tolerance,high=target+tolerance;
            int at=A4Index4LowerBound(peaks,low);var ids=new List<int>();var reliableIds=new List<int>();
            for(;at<peaks.Length&&peaks[at]<=high;at++)
            {
                if(!A4Index4ReviewStep(r))break;
                if(ordered[at].Id==pair.Incident.Id||ordered[at].Id==pair.Reflection.Id)continue;
                ids.Add(ordered[at].Id);if(A4Index4PacketReliable(c,ordered[at]))reliableIds.Add(ordered[at].Id);
            }
            string coverage=low>=0&&high<c.Raw.Length?"expected_peak_interval_inside_raw_record":"expected_peak_interval_not_fully_recorded";
            r.EndpointHypotheses.Add("{\"edge_id\":"+pair.EdgeId+",\"incident_peak\":"+A4ChargeIndex(pair.Incident)+
                ",\"reflection_peak\":"+A4ChargeIndex(pair.Reflection)+",\"peak_delay_samples\":"+delay+
                ",\"round_trip_samples\":"+NumberJson(c.RoundTripExact)+",\"existing_tolerance_samples\":"+tolerance+
                ",\"ordinary_formula_position_m\":"+NumberJson(pair.Position)+",\"endpoint_hypothesis\":\"near_far_or_later_echo_unresolved\","+
                "\"position_overwritten\":false,\"double_arrival_resolved\":false,\"bipolar_lobes_are_not_independent_arrivals\":true,"+
                "\"double_peak_classifier\":\"not_inferred_without_independent_arrival_evidence\",\"predecessor_search_scope\":\"current_LOC_detected_packet_inventory_only\","+
                "\"predecessor_expected_peak\":"+NumberJson(target)+",\"predecessor_record_coverage\":"+Json(coverage)+
                ",\"predecessor_detected_ids\":["+String.Join(",",ids)+"],\"predecessor_reliable_ids\":["+String.Join(",",reliableIds)+
                "],\"absence_proven\":false,\"no_predecessor_does_not_confirm_far_end\":true}");
        }
        // Late diagnostic budget exhaustion also revokes EVERY tentative output-role
        // suggestion. Never publish a partial scan as a complete absence/uniqueness test.
        if(!r.Complete)
        {
            r.DuplicateReviewOnlyPairs.Clear();r.ConfirmedEchoPulseIds.Clear();
            foreach(var chain in r.Chains){chain.ReviewOnlyRole=false;if(chain.Qualified)chain.Reason="incomplete_inventory_diagnostic_only";}
        }
        r.DiagnosticJson=A4Index4EchoReviewJson(r);return r;
    }
    private static string A4Index4EchoReviewJson(A4Index4EchoReviewResult r)
    {
        var b=new StringBuilder("{\"enabled\":").Append(r.Enabled?"true":"false")
            .Append(",\"algorithm_index\":4,\"complete\":").Append(r.Complete?"true":"false")
            .Append(",\"reason\":").Append(Json(r.Reason)).Append(",\"review_steps\":").Append(r.Steps)
            .Append(",\"score_changed\":false,\"owner_changed\":false,\"position_changed\":false,\"raw_samples_changed\":false,")
            .Append("\"history_used\":false,\"new_independent_arrival_detector\":false,\"endpoint_L_forced\":false,")
            .Append("\"echo_role_is_not_independently_labelled_ground_truth\":true,\"same_middle_three_packet_policy\":")
            .Append(Json("existing_one_RT_timing_allowance; cached_fixed_band_SNR8_same_temporal_polarity_dynamic_CAL_correlation; two_step_2sigma_decay; existing_3dB_plus_noise_ratio_interval; unique_confirmed_source; no_independent_owner; no_score_bonus; no_restart"))
            .Append(",\"review_only_edge_ids\":[").Append(String.Join(",",r.DuplicateReviewOnlyPairs.Select(p=>p.EdgeId).OrderBy(v=>v)))
            .Append("],\"confirmed_source_echo_packet_ids\":[").Append(String.Join(",",r.ConfirmedEchoPulseIds.OrderBy(v=>v)))
            .Append("],\"chains\":[");
        bool first=true;
        foreach(var e in r.Chains)
        {
            if(!first)b.Append(',');first=false;
            b.Append("{\"source_edge_id\":").Append(e.Source.EdgeId).Append(",\"later_edge_id\":").Append(e.Later.EdgeId)
                .Append(",\"packet_ids\":[").Append(e.Source.Incident.Id).Append(',').Append(e.Source.Reflection.Id).Append(',').Append(e.Later.Reflection.Id)
                .Append("],\"raw_peak_indices\":[").Append(A4ChargeIndex(e.Source.Incident)).Append(',').Append(A4ChargeIndex(e.Source.Reflection)).Append(',').Append(A4ChargeIndex(e.Later.Reflection))
                .Append("],\"delay01\":").Append(e.Delay01).Append(",\"delay12\":").Append(e.Delay12).Append(",\"tolerance_samples\":").Append(e.Tolerance)
                .Append(",\"ratio01\":").Append(NumberJson(e.Ratio01)).Append(",\"ratio12\":").Append(NumberJson(e.Ratio12))
                .Append(",\"noise_2sigma01\":").Append(NumberJson(e.Noise01)).Append(",\"noise_2sigma12\":").Append(NumberJson(e.Noise12))
                .Append(",\"ratio_difference_db\":").Append(NumberJson(e.RatioDifferenceDb)).Append(",\"allowed_ratio_difference_db\":").Append(NumberJson(e.AllowedRatioDifferenceDb))
                .Append(",\"qualified_evidence\":").Append(e.Qualified?"true":"false").Append(",\"qualified_source_count\":").Append(e.SourceCount)
                .Append(",\"source_globally_confirmed\":").Append(e.SourceConfirmed?"true":"false").Append(",\"review_only_echo_role\":").Append(e.ReviewOnlyRole?"true":"false")
                .Append(",\"reason\":").Append(Json(e.Reason)).Append('}');
        }
        return b.Append("],\"endpoint_hypotheses\":[").Append(String.Join(",",r.EndpointHypotheses)).Append("]}").ToString();
    }
}
