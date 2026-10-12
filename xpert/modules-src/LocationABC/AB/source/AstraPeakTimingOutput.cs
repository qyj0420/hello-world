using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Canonical packet identity/charge is distinct from a homologous timing peak.
    // Resolve from frozen measurements; never search the plot for a favourable sign.
    private static bool A4HomologousPeakTiming(A4Context c,A4Pair pair,A6Correlation rho,
        A4AttenuationMeasurement a,A4AttenuationMeasurement b,out int t0,out int t1,
        out double baselineA,out double baselineB,out string reason)
    {
        t0=t1=-1;baselineA=baselineB=double.NaN;reason="frozen_temporal_evidence_unavailable";
        if(c==null||pair==null||a==null||b==null||rho==null||!rho.Valid||!rho.StructureCompatible||
            rho.AnchorA==null||rho.AnchorB==null||!rho.AnchorA.Valid||!rho.AnchorB.Valid)return false;
        var aa=rho.AnchorA;var bb=rho.AnchorB;
        int canonical=A4ChargeIndex(pair.Incident),reflection=A4ChargeIndex(pair.Reflection);
        if(canonical<a.RawStart||canonical>a.RawEnd||reflection<b.RawStart||reflection>b.RawEnd)return false;
        baselineA=A4RawMeasurementBaseline(a,canonical);
        double av=c.Raw[canonical]-baselineA;
        if(!Finite(av)||av==0)return false;
        int bandPeak;
        if(!A6TemporalLowLobe(a,canonical,Math.Sign(av),out bandPeak))return false;
        bool second=bandPeak==aa.SecondPeak&&aa.HasSecond;
        if(bandPeak!=aa.Peak&&!second){reason="canonical_incident_not_in_frozen_corresponding_lobes";return false;}
        int expected=second?bb.SecondPeak:bb.Peak;
        int chosen=second?bb.RawSecondPeak:bb.RawFirstPeak;
        if(second&&!bb.HasSecond||chosen<b.RawStart||chosen>b.RawEnd||chosen<=canonical)return false;
        // Keep the reflection canonical maximum if it belongs to that same
        // verified connected lobe; otherwise choose that lobe's frozen raw peak.
        double cv=c.Raw[reflection]-A4RawMeasurementBaseline(b,reflection);int mapped;
        if(Finite(cv)&&cv!=0&&Math.Sign(cv)==Math.Sign(av)&&
            A6TemporalLowLobe(b,reflection,Math.Sign(cv),out mapped)&&mapped==expected)chosen=reflection;
        baselineB=A4RawMeasurementBaseline(b,chosen);
        double bv=c.Raw[chosen]-baselineB;
        if(!Finite(bv)||bv==0||Math.Sign(av)!=Math.Sign(bv))
        {reason="corresponding_original_peak_polarity_unresolved";return false;}
        if(!A4ReflectionDelayWithinWindow(c,chosen-canonical))
        {reason="corresponding_original_peak_outside_reflection_candidate_window";return false;}
        t0=canonical;t1=chosen;
        reason=second?"frozen_corresponding_second_lobe_same_polarity":"frozen_corresponding_first_lobe_same_polarity";
        return true;
    }

    // This stage runs after physical validation and endpoint preclassification,
    // before explicit history append. No ownership, packet, raw waveform,
    // calibration, charge identity or native matching decision is rewritten.
    internal void CompletePeakTimingProjection(ref double[,] parameters,bool firstArrival)
    {
        var c=equivalentContext;
        if(c==null||c.Algorithm<0||c.Algorithm>5)return;
        int count=parameters.GetLength(0);
        var next=(double[,])parameters.Clone();var rows=new List<string>();
        var incidentPeaks=new int[count];var reflectionPeaks=new int[count];
        var packets=c.Algorithm!=5?projectRetainedPackets:
            a6CurrentOutputPulses.Select(p=>p.Pulse).Concat(a6Evidence.Keys.SelectMany(p=>new[]{p.Incident,p.Reflection})).Distinct().ToList();
        int corrected=0,recovered=0,unresolved=0;
        for(int r=0;r<count;r++)
        {
            A4Pair pair=c.Algorithm!=5?A4CurrentMixedPair(parameters,r):a6CurrentOutputPulses[r].Pair;
            int peak=pair!=null?A4ChargeIndex(pair.Incident):c.Algorithm==5?A4ChargeIndex(a6CurrentOutputPulses[r].Pulse):
                projectRetainedRawPeaks[r];
            A4Pulse incident=pair!=null?pair.Incident:packets.FirstOrDefault(p=>A4ChargeIndex(p)==peak);
            incidentPeaks[r]=peak;reflectionPeaks[r]=pair==null?-1:A4ChargeIndex(pair.Reflection);
            if(firstArrival&&pair!=null)
            {
                // Packet/charge identities and correlations stay frozen, but
                // ALL physical timing coordinates must use the requested edges.
                // Do not retain a peak-based endpoint projection or fallback.
                int onsetA=A4RawLeadingEdge(c,pair.Incident,peak),onsetB=A4RawLeadingEdge(c,pair.Reflection,reflectionPeaks[r]);
                if(onsetA<0||onsetB<=onsetA)
                    throw new InvalidOperationException("Reliable first-arrival timing could not be resolved. This record was not localized; no peak timing was substituted.");
                ApplyFirstArrivalCoordinates(c,next,r,onsetA,onsetB);
                bool outside=A4ClearOutsideReflectionWindow(c,next,r);
                bool rejected=A4ApplyFinalBoundaryConfidence(c,next,r);
                rows.Add("{\"row\":"+r+",\"canonical_raw_incident\":"+peak+",\"canonical_raw_reflection\":"+reflectionPeaks[r]+
                    ",\"resolved\":"+(!outside&&!rejected?"true":"false")+",\"reason\":\"strict_first_arrival_coordinates_no_peak_fallback\",\"candidate_recovered\":false}");
                if(onsetA!=parameters[r,10]||onsetB!=parameters[r,11])corrected++;
                continue;
            }
            // The shared recovery fixes missing OUTPUT in methods 0..3, not
            // their strategy. Never rescore, retime, or promote an existing pair.
            if(c.Algorithm<4&&pair!=null)
            {
                bool windowRejected=A4ClearOutsideReflectionWindow(c,next,r);
                bool boundaryRejected=A4ApplyFinalBoundaryConfidence(c,next,r);
                rows.Add("{\"row\":"+r+",\"canonical_raw_incident\":"+peak+
                    ",\"canonical_raw_reflection\":"+reflectionPeaks[r]+
                    ",\"resolved\":true,\"candidate_recovered\":false,\"reason\":"+Json(boundaryRejected?
                        "existing_strategy_pair_preserved;final_geometry_outside_2percent_or_unavailable":windowRejected?
                        "existing_strategy_pair_reflection_outside_hard_window_unresolved":"existing_strategy_pair_preserved")+"}");
                continue;
            }
            int t0=-1,t1=-1;double baseA=double.NaN,baseB=double.NaN;
            string reason=firstArrival?"first_arrival_timing_preserved":"no_real_candidate";
            bool resolved=Finite(parameters[r,11])&&Finite(parameters[r,12]),candidateRecovered=false;
            A4BestCandidateReview candidate=null;
            if(pair!=null)
            {
                A6Correlation rho=null;A4AttenuationMeasurement ma=null,mb=null;
                if(c.Algorithm==5)
                {A6Evidence e;if(a6Evidence.TryGetValue(pair,out e)){rho=e.Correlation;ma=e.A;mb=e.B;}}
                else if(pair.UnifiedCorrelation!=null)
                {rho=pair.UnifiedCorrelation.Measured;ma=pair.UnifiedCorrelation.Incident;mb=pair.UnifiedCorrelation.Reflection;}
                resolved=A4HomologousPeakTiming(c,pair,rho,ma,mb,out t0,out t1,out baseA,out baseB,out reason);
                if(!resolved)candidate=A4FindBestOutputCandidate(c,pair.Incident,new[]{pair.Reflection},false);
            }
            else if(pair==null&&incident!=null)
            {
                candidate=A4FindBestOutputCandidate(c,incident,packets,false);
                candidateRecovered=candidate!=null;
                if(candidate==null&&c.Attenuation&&(c.AttenuationScoringReference==null||!c.AttenuationScoringReference.Valid))
                    reason="requested_attenuation_recovery_blocked_unqualified_calibration:"+
                        (c.AttenuationScoringReference==null?"reference_missing":c.AttenuationScoringReference.Reason);
            }
            if(candidate!=null)
            {
                t0=candidate.IncidentTiming;t1=candidate.ReflectionTiming;
                baseA=candidate.IncidentBaseline;baseB=candidate.ReflectionBaseline;
                reason=candidate.Reason;resolved=true;
                reflectionPeaks[r]=A4ChargeIndex(candidate.Pair.Reflection);
                next[r,21]=candidate.Rho;next[r,22]=candidate.AmplitudeRatio;
                next[r,33]=candidate.Score;next[r,35]=0;
            }
            int correspondingPeakDelay=t1-t0;
            A6OutputReview sixthReview=candidate==null?null:candidate.SixthOutputReview;
            if(c.Algorithm==5&&resolved&&pair!=null&&candidate==null)
            {
                // Recompute at the actual corresponding peak delay, before any
                // display-onset conversion. Never retain a stale CAL fit after
                // moving from the packet maximum to its same-polarity lobe.
                sixthReview=A6ReviewOutput(c,pair.Incident,pair.Reflection,correspondingPeakDelay,next[r,21]);
                if(sixthReview!=null)
                {
                    next[r,22]=sixthReview.ObservedRatio;
                    // Preserve earlier ambiguity downgrades, but not an old
                    // exponential attenuation cap or a fabricated missing=0.
                    double currentScore=Finite(next[r,33])?Clamp(next[r,33],0,1):double.NaN;
                    next[r,33]=Math.Min(currentScore,Math.Min(sixthReview.Score,sixthReview.ScoreCeiling));
                    if(!sixthReview.Passed)
                    {next[r,35]=0;next[r,33]=Math.Min(.49,next[r,33]);}
                    reason+=";"+sixthReview.Reason;
                }
            }
            if(firstArrival&&resolved)
            {
                var reflected=candidate==null?pair.Reflection:candidate.Pair.Reflection;
                int onsetA=A4RawLeadingEdge(c,incident,A4ChargeIndex(incident)),onsetB=A4RawLeadingEdge(c,reflected,A4ChargeIndex(reflected));
                if(onsetA>=0&&onsetB>onsetA)
                {t0=onsetA;t1=onsetB;baseA=A4RawBaselineAt(c,t0);baseB=A4RawBaselineAt(c,t1);reason+=";strict_first_arrival_after_candidate_choice";}
                else {resolved=false;t0=t1=-1;reason+=";first_arrival_unresolved_no_peak_fallback";}
            }
            {
                // Display cropping must not change candidate selection or erase
                // a real global index. The cursor is hidden if it is outside
                // the requested window; analysis still uses the complete LOC.
                if(resolved&&!A4ReflectionDelayWithinWindow(c,t1-t0))
                    reason+=";actual_requested_timing_outside_reflection_hard_window_unresolved";
                if(resolved&&t0>=0&&t1<c.Raw.Length&&t1>t0&&A4ReflectionDelayWithinWindow(c,t1-t0))
                {
                    bool timingChanged=t0!=parameters[r,10]||t1!=parameters[r,11];
                    next[r,10]=t0;next[r,11]=t1;next[r,12]=t1-t0;next[r,20]=t0-next[r,31];
                    next[r,3]=Finite(EffectiveOscillationFrequencyHz)&&EffectiveOscillationFrequencyHz>0?
                        NormalizeDegrees(EffectiveInitialPhaseDegrees+360*EffectiveOscillationFrequencyHz*t0/c.Fs):NormalizeDegrees(EffectiveInitialPhaseDegrees);
                    if(firstArrival||candidateRecovered||!endpointPreclassifiedRows.Contains(r))
                    {
                        next[r,13]=A4BoundaryOutputPosition(t1-t0,c.Fs,c.Joints,c.Velocities,c.Length);
                        double compensationPosition=A4BoundaryOutputPosition(firstArrival?t1-t0:correspondingPeakDelay,c.Fs,c.Joints,c.Velocities,c.Length);
                        next[r,16]=c.Attenuation&&c.Calibration!=null?
                            A4ContextCompensateCharge(c,next[r,15],compensationPosition):next[r,15];
                        next[r,17]=next[r,15]>0?next[r,16]/next[r,15]:1;
                    }
                    if(timingChanged)corrected++;if(candidateRecovered)recovered++;
                    if(A4ApplyFinalBoundaryConfidence(c,next,r))
                    {
                        reason+=";final_display_timing_geometry_outside_2percent_or_unavailable";
                    }
                }
                else
                {
                    // A real candidate is required even for unaccepted review.
                    // Never manufacture a window-end index to avoid a NaN.
                    resolved=false;unresolved++;
                    next[r,11]=next[r,12]=next[r,13]=next[r,16]=next[r,17]=next[r,21]=next[r,22]=double.NaN;
                    next[r,33]=double.NaN;next[r,35]=0;
                }
            }
            rows.Add("{\"row\":"+r+",\"canonical_raw_incident\":"+peak+
                ",\"canonical_raw_reflection\":"+(reflectionPeaks[r]<0?"null":reflectionPeaks[r].ToString(Inv))+
                ",\"resolved\":"+(resolved?"true":"false")+",\"reason\":"+Json(reason)+
                ",\"candidate_recovered\":"+(candidateRecovered?"true":"false")+",\"candidate_evidence\":"+(candidate==null?"null":candidate.DiagnosticJson)+
                ",\"final_requested_evidence\":"+A6OutputReviewJson(sixthReview)+
                ",\"incident_baseline\":"+NumberJson(baseA)+",\"reflection_baseline\":"+NumberJson(baseB)+
                ",\"incident_value\":"+NumberJson(t0>=0&&t0<c.Raw.Length?c.Raw[t0]-baseA:double.NaN)+
                ",\"reflection_value\":"+NumberJson(t1>=0&&t1<c.Raw.Length?c.Raw[t1]-baseB:double.NaN)+
                ",\"original_timing_samples\":["+NumberJson(parameters[r,10])+","+NumberJson(parameters[r,11])+","+NumberJson(parameters[r,12])+
                "],\"output_timing_samples\":["+NumberJson(next[r,10])+","+NumberJson(next[r,11])+","+NumberJson(next[r,12])+
                "],\"show_reflection_cursor\":"+(Finite(next[r,11])&&Finite(next[r,12])&&next[r,11]>=next[r,31]&&next[r,11]<=next[r,32]?"true":"false")+"}");
        }
        string payload="{\"enabled\":true,\"first_arrival\":"+(firstArrival?"true":"false")+
            ",\"policy\":\"same_polarity_homologous_peak_or_best_real_signed_correlation_candidate_unaccepted\",\"score_is_probability\":false,"+
            "\"native_pairing_and_history_unchanged\":true,\"waveforms_and_raw_charge_unchanged\":true,\"earlier_roles_scope\":\"before_final_output_projection\","+
            "\"final_accepted_rows\":"+Enumerable.Range(0,count).Count(r=>next[r,35]==1)+
            ",\"final_review_candidate_rows\":"+Enumerable.Range(0,count).Count(r=>next[r,35]==0&&Finite(next[r,11]))+","+
            "\"corrected_timing_rows\":"+corrected+",\"recovered_candidate_rows\":"+recovered+",\"no_resolved_candidate_rows\":"+unresolved+
            ",\"reflection_candidate_window\":"+A4ReflectionWindowJson(c)+",\"output_pulse_roles\":"+A4AllOutputPulseRoleJson(next,incidentPeaks,reflectionPeaks)+",\"rows\":["+String.Join(",",rows)+"]}";
        string nextStatus=StatusJson.Substring(0,StatusJson.LastIndexOf('}'))+",\"output_peak_timing\":"+payload+"}";
        parameters=next;ParameterMatrix2D=next;ParameterMatrix=Flatten(next);
        ValidatedRuntimePositionM=Enumerable.Range(0,count).Select(r=>next[r,13]).ToArray();
        StatusJson=LastResultJson=nextStatus;
    }
    private void ApplyFirstArrivalCoordinates(A4Context c,double[,] rows,int row,int t0,int t1)
    {
        rows[row,10]=t0;rows[row,11]=t1;rows[row,12]=t1-t0;rows[row,20]=t0-rows[row,31];
        rows[row,3]=Finite(EffectiveOscillationFrequencyHz)&&EffectiveOscillationFrequencyHz>0?
            NormalizeDegrees(EffectiveInitialPhaseDegrees+360*EffectiveOscillationFrequencyHz*t0/c.Fs):NormalizeDegrees(EffectiveInitialPhaseDegrees);
        rows[row,13]=A4BoundaryOutputPosition(t1-t0,c.Fs,c.Joints,c.Velocities,c.Length);
        rows[row,16]=c.Attenuation&&c.Calibration!=null?A4ContextCompensateCharge(c,rows[row,15],rows[row,13]):rows[row,15];
        rows[row,17]=rows[row,15]>0?rows[row,16]/rows[row,15]:1;
    }
}
