using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // This remains the overall confirmation-score threshold, NOT a shape-correlation floor.
    private const double A4MinimumOutputConfidence=.70;
    private sealed class A4RejectedPair
    {
        internal A4Pulse Incident,Reflection;
        internal double Wave,Envelope,Confidence,RequiredSigned;
        internal A4LowBandPairEvidence LowBand;
        internal A4UnifiedCorrelationEvidence UnifiedCorrelation;
        internal A4AttenuationEvidence CalibrationDeviation;
        internal string Reason;
    }
    private static void A4Reject(A4Context c,A4Pulse a,A4Pulse b,double wave,double envelope,double confidence,
        double required,A4LowBandPairEvidence low,string reason,bool correlation,A4AttenuationEvidence calibration=null,
        A4UnifiedCorrelationEvidence unified=null)
    {
        c.RejectedPairCount++;if(correlation)c.RejectedCorrelationCount++;else c.RejectedPhysicalConfidenceCount++;
        A4CaptureLocalShadow(c,a,b,confidence,reason,calibration,unified);
        // The aggregate counts are complete; bounded examples prevent a noise-heavy file from creating unbounded status JSON.
        if(c.RejectedPairs.Count<512)c.RejectedPairs.Add(new A4RejectedPair {Incident=a,Reflection=b,Wave=wave,Envelope=envelope,
            Confidence=confidence,RequiredSigned=required,LowBand=low,Reason=reason,CalibrationDeviation=calibration,UnifiedCorrelation=unified});
    }
    private sealed class A4ParallelEdge
    {
        internal A4Pulse A,B;
        internal double Wave,Envelope,Correlation,Ratio,AttenuationMargin,BandExcess;
        internal double RequiredSigned;
        internal A4LowBandPairEvidence LowBand;
        internal A4UnifiedCorrelationEvidence UnifiedCorrelation;
        internal int ValidBands;
    }
    private sealed class A4ParallelEvidence
    {
        internal A4ParallelEdge ShortFirst,ShortSecond,ParallelFirst,ParallelSecond;
    }
    private static long A4PairKey(A4Pulse a,A4Pulse b){return ((long)a.Id<<32)|(uint)b.Id;}
    private static int A4EchoTolerance(A4Pulse p){return Math.Max(2,Math.Min(10,p.Period/3));}
    private static bool A4RoleEndpointReliable(A4Pulse p)
    {return p.MappingValid&&!p.Clipped&&!p.BaselineUnstable&&!p.UnresolvedOverlap&&p.Snr>=4.5;}
    private static bool A4RoleEdgePossible(A4Context c,A4ParallelEdge edge)
    {
        if(edge==null)return false;
        int t0,t1;if(!A4PhysicalTiming(c,edge.A,edge.B,out t0,out t1))return false;
        int delay=t1-t0;
        if(edge.UnifiedCorrelation==null||!edge.UnifiedCorrelation.Passed)return false;
        if(!A4ReflectionDelayWithinWindow(c,delay)||!A4BoundaryTimingValid(c,delay)||edge.Ratio>3||edge.Ratio>1.20+2/Math.Max(3,edge.A.Snr))return false;
        return edge.A.End<edge.B.Start&&delay>Math.Max(Math.Max(edge.A.Period,edge.B.Period)*1.5,
            Math.Max(edge.A.EquivalentSamples,edge.B.EquivalentSamples)*2);
    }
    private static A4ParallelEdge A4ParallelEdgeEvidence(A4Context c,A4Pulse a,A4Pulse b,Dictionary<long,A4ParallelEdge> cache)
    {
        long key=A4PairKey(a,b);A4ParallelEdge value;
        // Both directions and four-packet review reuse the one physical evidence calculation for this edge.
        return cache.TryGetValue(key,out value)?value:null;
    }
    private static void A4FindParallelRoleAmbiguities(A4Context c,List<A4Pulse> pulses,List<A4Pair> pool)
    {
        c.ParallelAlternatives=new List<A4ParallelEvidence>();c.ParallelPairKeys=new HashSet<long>();
        Dictionary<long,A4ParallelEdge> cache=new Dictionary<long,A4ParallelEdge>();const int tolerance=3;
        foreach(A4Pair pair in pool)cache[A4PairKey(pair.Incident,pair.Reflection)]=new A4ParallelEdge {
            A=pair.Incident,B=pair.Reflection,Wave=pair.WaveCorrelation,Envelope=pair.EnvelopeCorrelation,Correlation=pair.Correlation,
            Ratio=pair.AmplitudeRatio,LowBand=pair.LowBand,RequiredSigned=pair.SignedCorrelationMinimum,UnifiedCorrelation=pair.UnifiedCorrelation,
            AttenuationMargin=pair.Incident.LocalAmplitude-pair.Reflection.LocalAmplitude-
                2*Math.Sqrt(pair.Incident.NoiseSigma*pair.Incident.NoiseSigma+pair.Reflection.NoiseSigma*pair.Reflection.NoiseSigma)};
        for(int ia=0;ia<pulses.Count;ia++)
        {
            if(!A4TakeRoleReviewStep(c))return;
            A4Pulse a=pulses[ia];if(!A4RoleEndpointReliable(a))continue;
            for(int ib=ia+1;ib<pulses.Count&&pulses[ib].Start-a.Start<=A4PhysicalDelayLimit(c);ib++)
            {
                if(!A4TakeRoleReviewStep(c))return;
                A4Pulse b=pulses[ib];if(!A4RoleEndpointReliable(b)||a.End>=b.Start)continue;
                A4ParallelEdge ab=A4ParallelEdgeEvidence(c,a,b,cache);
                if(!A4RoleEdgePossible(c,ab))continue;
                for(int ic=ib+1;ic<pulses.Count&&pulses[ic].Start-a.Start<=A4PhysicalDelayLimit(c);ic++)
                {
                    if(!A4TakeRoleReviewStep(c))return;
                    A4Pulse third=pulses[ic];int lag=third.Start-a.Start;
                    if(!A4RoleEndpointReliable(third)||b.End>=third.Start||A4EchoOrder(lag,c.RoundTripExact,A4EchoTolerance(a))>0)continue;
                    A4ParallelEdge ac=A4ParallelEdgeEvidence(c,a,third,cache);
                    if(ac==null||ac.AttenuationMargin<=0||third.Energy>=a.Energy||!A4RoleEdgePossible(c,ac))continue;
                    int target=b.Start+lag,low=ic+1,high=pulses.Count;
                    while(low<high){int mid=low+(high-low)/2;if(pulses[mid].Start<target-tolerance)low=mid+1;else high=mid;}
                    for(int id=low;id<pulses.Count&&pulses[id].Start<=target+tolerance;id++)
                    {
                        if(!A4TakeRoleReviewStep(c))return;
                        A4Pulse fourth=pulses[id];if(!A4RoleEndpointReliable(fourth)||third.End>=fourth.Start||fourth.Start-b.Start>A4PhysicalDelayLimit(c)||
                            A4EchoOrder(fourth.Start-b.Start,c.RoundTripExact,A4EchoTolerance(b))>0)continue;
                        A4ParallelEdge bd=A4ParallelEdgeEvidence(c,b,fourth,cache),cd=A4ParallelEdgeEvidence(c,third,fourth,cache);
                        if(bd==null||cd==null||bd.AttenuationMargin<=0||fourth.Energy>=b.Energy||
                            !A4RoleEdgePossible(c,bd)||!A4RoleEdgePossible(c,cd))continue;
                        // This is competing physical evidence, not proof that the longer delay is true.
                        // Mark the role ambiguity for component review; never change the original physical evidence score.
                        foreach(A4ParallelEdge edge in new[]{ab,cd,ac,bd})c.ParallelPairKeys.Add(A4PairKey(edge.A,edge.B));
                        // Cross-pairing the middle two or outer two does not resolve the same four-role ambiguity.
                        c.ParallelPairKeys.Add(A4PairKey(b,third));c.ParallelPairKeys.Add(A4PairKey(a,fourth));
                        foreach(A4ParallelEdge edge in new[]{ac,bd})
                        {
                            double gain,shape,excess;int count,unstable;
                            A4BandScores(c,edge.A,edge.B,edge.B.Start-edge.A.Start,out gain,out shape,out count,out excess,out unstable);
                            edge.ValidBands=count;edge.BandExcess=excess;
                        }
                        c.ParallelAlternatives.Add(new A4ParallelEvidence{ShortFirst=ab,ShortSecond=cd,ParallelFirst=ac,ParallelSecond=bd});
                    }
                }
            }
        }
    }
    private static string A4ParallelEdgeJson(A4Context c,A4ParallelEdge edge)
    {
        int a,b;bool peakPolarity=A4CanonicalPeaks(c,edge.A,edge.B,out a,out b);
        string reason=!peakPolarity?"canonical_raw_peaks_unavailable":edge.UnifiedCorrelation==null||!edge.UnifiedCorrelation.Passed?"dynamic_correlation_below_candidate_floor":"non_unique_physical_roles";
        return "{\"incident_onset\":"+edge.A.Start+",\"reflection_onset\":"+edge.B.Start+
            ",\"onset_delay\":"+(edge.B.Start-edge.A.Start)+",\"selected_incident_index\":"+a+",\"selected_reflection_index\":"+b+
            ",\"selected_delay\":"+(b-a)+",\"selected_peak_polarity_compatible\":"+(peakPolarity?"true":"false")+
            ",\"role_evidence\":true,\"publishable_candidate\":false,\"non_publishable_reason\":"+Json(reason)+
            ",\"diagnostic_role_evidence_only\":true,\"signed_full_correlation\":"+NumberJson(edge.Wave)+",\"full_envelope_correlation\":"+NumberJson(edge.Envelope)+
            ",\"amplitude_ratio\":"+NumberJson(edge.Ratio)+",\"incident_snr\":"+NumberJson(edge.A.Snr)+",\"reflection_snr\":"+NumberJson(edge.B.Snr)+
            ",\"unified_correlation\":"+A4UnifiedCorrelationJson(edge.UnifiedCorrelation)+
            ",\"attenuation_margin_after_two_sigma\":"+NumberJson(edge.AttenuationMargin)+",\"valid_calibration_bands\":"+edge.ValidBands+
            ",\"cal_band_excess_after_noise_db\":"+NumberJson(edge.BandExcess)+"}";
    }
    private static List<A4Pair> A4Match(A4Context c,List<A4Pulse> pulses)
    {
        // Shape alignment retains its independently measured packet anchors.
        // Physical delay/attenuation use FirstArrivalPhysicalTiming separately;
        // changing geometry must never mutate the raw packet/charge identity.
        bool outputFirst=c.FirstArrival,independent=c.IndependentTiming;c.FirstArrival=false;c.IndependentTiming=true;
        try{return A4MatchPhysical(c,pulses);}finally{c.FirstArrival=outputFirst;c.IndependentTiming=independent;}
    }
    private static List<A4Pair> A4MatchPhysical(A4Context c,List<A4Pulse> pulses)
    {
        // Owners are a final commitment for this record, never an input to candidate ranking.
        foreach(A4Pulse pulse in pulses)pulse.Owner=-1;
        c.Suppressed=0;c.RejectedPairs.Clear();c.RejectedPairCount=c.RejectedCorrelationCount=c.RejectedPhysicalConfidenceCount=0;
        A4ClearLocalShadows(c);
        c.AnalysisVisitIds.Clear();c.AnalysisExclusions.Clear();A4ResolveAutomaticSuppression(c);
        c.UnreliableInterveningIds.Clear();c.CandidatePoolLimitExceeded=false;
        c.CandidateEvaluations=c.RoleReviewSteps=0;c.CandidateEvaluationLimitExceeded=c.RoleReviewLimitExceeded=false;
        List<A4Pulse> ordered=pulses.OrderBy(p=>p.Start).ThenBy(p=>p.Id).ToList();
        A4PrepareUnifiedCorrelation(c,ordered);
        List<A4Pair> pool=A4BuildCandidatePool(c,ordered);
        if(!A4RecordReviewIncomplete(c))A4FindParallelRoleAmbiguities(c,ordered,pool);
        else {c.ParallelAlternatives=new List<A4ParallelEvidence>();c.ParallelPairKeys=new HashSet<long>();}
        foreach(A4Pair pair in pool)pair.ParallelAmbiguous=c.ParallelPairKeys.Contains(A4PairKey(pair.Incident,pair.Reflection));
        A4ApplyStrategy(c,pool);
        List<A4Pair> result=A4ResolveGlobalCandidates(c,ordered,pool);
        // Echo ownership is committed ONLY by A4CommitCausalEchoes during global
        // resolution: two ordered endpoints, noise-resolved decay, a unique
        // confirmed source and a consistent round-trip order are required.
        // A lone periodic look-alike remains unowned for independent/review analysis.
        return result;
    }
    private static List<A4Pair> A4BuildCandidatePool(A4Context c,List<A4Pulse> pulses)
    {
        List<A4Pair> pool=new List<A4Pair>();
        // F140/F276: incident analysis visits largest ORIGINAL absolute amplitudes first.
        // The temporal list remains available for reflection search; owners are not committed here.
        int[] priority=Enumerable.Range(0,pulses.Count)
            .OrderByDescending(i=>A4RawCharge(c,A4ChargeIndex(pulses[i])))
            .ThenBy(i=>pulses[i].Start).ThenBy(i=>pulses[i].Id).ToArray();
        foreach(int ia in priority)
        {
            A4Pulse a=pulses[ia];
            if(A4RawCharge(c,A4ChargeIndex(a))<c.IncidentThreshold)continue;
            if(!A4AnalysisPhaseEligible(c,A4ChargeIndex(a)))continue;
            if(c.AnalysisVisitIds.Count>=c.AnalysisLimit){c.AnalysisLimitReached=true;break;}
            c.AnalysisVisitIds.Add(a.Id);
            if(!a.MappingValid||a.UnresolvedOverlap||a.Amplitude<c.Threshold)
            {
                c.AnalysisExclusions[a.Id]=!a.MappingValid?"invalid_strict_raw_mapping":
                    a.UnresolvedOverlap?"unresolved_complete_packet_overlap":"analysis_band_below_discovery_floor";
                continue;
            }
            for(int ib=ia+1;ib<pulses.Count;ib++)
            {
                A4Pulse b=pulses[ib];if(b.Start-A4ChargeIndex(a)>A4PhysicalDelayLimit(c))break;
                if(c.CandidateEvaluations>=c.CandidateEvaluationLimit){c.CandidateEvaluationLimitExceeded=true;return pool;}
                c.CandidateEvaluations++;
                if(b.Start<=a.Start||!b.MappingValid||b.UnresolvedOverlap)
                {
                    A4Reject(c,a,b,double.NaN,double.NaN,0,double.NaN,null,
                        b.Start<=a.Start?"reflection_not_after_incident":!b.MappingValid?"reflection_invalid_strict_raw_mapping":"reflection_unresolved_overlap",false);continue;
                }
                int canonicalIncident,canonicalReflection;
                if(!A4CanonicalPeaks(c,a,b,out canonicalIncident,out canonicalReflection))
                {A4Reject(c,a,b,double.NaN,double.NaN,0,double.NaN,null,"canonical_raw_peak_not_resolved",false);continue;}
                int physicalA,physicalB;
                if(!A4PhysicalTiming(c,a,b,out physicalA,out physicalB))
                {A4Reject(c,a,b,double.NaN,double.NaN,0,double.NaN,null,"first_arrival_unresolved_no_peak_fallback",false);continue;}
                int delay=physicalB-physicalA;
                if(!A4ReflectionDelayWithinWindow(c,delay)||!A4BoundaryTimingValid(c,delay))
                {
                    A4Reject(c,a,b,double.NaN,double.NaN,0,double.NaN,null,
                        delay<=0?"nonpositive_selected_delay":"selected_delay_outside_cable_2percent_boundary_or_invalid_geometry",false);continue;
                }
                double ratio=b.LocalAmplitude/Math.Max(1e-30,a.LocalAmplitude);
                if(ratio>3)
                {A4Reject(c,a,b,double.NaN,double.NaN,0,double.NaN,null,"reflection_amplitude_more_than_three_times_incident",false);continue;}
                double wave,env;A4Correlate(c,a,b,out wave,out env);
                // Raw/full and envelope correlations remain diagnostics only. The common
                // guarded 3 MHz CAL-referenced comparison decides eligibility BEFORE ranking.
                A4UnifiedCorrelationEvidence unified=A4UnifiedCorrelation(c,a,b,delay);
                double corr=unified.Measured==null?double.NaN:unified.Measured.Fixed;
                A4LowBandPairEvidence lowBand=A4MeasureLowBandPair(c,a,b,delay);
                double requiredSigned=unified.Floor;
                if(!unified.Passed)
                {
                    string reason=unified.Available?"dynamic_correlation_below_candidate_floor":"guarded_correlation_measurement_unavailable";
                    A4Reject(c,a,b,wave,env,0,requiredSigned,lowBand,reason,true,null,unified);continue;
                }
                double widthScore=Math.Exp(-Math.Abs(Math.Log(Math.Max(.01,b.EquivalentSamples)/Math.Max(.01,a.EquivalentSamples)))*.65);
                double hfRatio=(b.HighFraction+.002)/(a.HighFraction+.002);
                double spectrum=Math.Exp(-Math.Max(0,Math.Log(hfRatio))*.8-Math.Abs(Math.Log(hfRatio))*.10);
                double snrScore=1-Math.Exp(-Math.Pow(Math.Max(0,Math.Min(a.Snr,b.Snr)-3)/5,2));
                double atten=1;
                if(c.Attenuation)
                {
                    double loss=Finite(c.Calibration.AttenuationLossDb)?Math.Max(0,c.Calibration.AttenuationLossDb):0;
                    double expected=Math.Pow(10,-loss*(double)delay/c.RoundTrip/20);
                    double observedDb=20*Math.Log10(Math.Max(1e-8,ratio)),expectedDb=20*Math.Log10(Math.Max(1e-8,expected));
                    double allowedDb=3+24/Math.Max(2,Math.Min(a.Snr,b.Snr));
                    atten=Math.Exp(-Math.Abs(observedDb-expectedDb)/allowedDb);
                }
                double bandGain,bandShape,bandExcess;int validBands,unstableNoiseBands;
                A4BandScores(c,a,b,delay,out bandGain,out bandShape,out validBands,out bandExcess,out unstableNoiseBands);
                // CAL band transfer remains diagnostic; its uncertainty is not
                // charged again inside the independent 15% other-evidence term.
                atten=Math.Min(atten,bandGain);
                if(lowBand.Trusted)atten=Math.Min(atten,lowBand.Score);
                double confidence;
                bool hasMeasuredCalibration=c.Calibration!=null&&c.Calibration.Samples!=null&&c.Calibration.Samples.Length>=16&&
                    Finite(c.Calibration.AttenuationLossDb)&&Finite(c.Calibration.StartTime)&&
                    Finite(c.Calibration.FinalTime)&&c.Calibration.FinalTime>c.Calibration.StartTime;
                double attenuationWeight=c.Attenuation&&hasMeasuredCalibration?(lowBand.Enhanced?.20:.08):0;
                double bandConsistency=validBands>0?Math.Exp(-Math.Max(0,bandExcess-2)):1;
                // Legacy weak-reference contribution. New deviation evidence can impose a stronger
                // bounded penalty below; combine by minimum, never multiply the same evidence twice.
                atten=Math.Min(atten,bandConsistency);
                A4AttenuationEvidence deviation=A4CalibrationDeviation(c,a,b);
                double scoreBeforeCalibration=A4WeightedEvidenceScore(unified.Score,widthScore,spectrum,snrScore,c.Width,null);
                confidence=A4WeightedEvidenceScore(unified.Score,widthScore,spectrum,snrScore,c.Width,deviation);
                double calibrationFactor=scoreBeforeCalibration>0?confidence/scoreBeforeCalibration:1;
                attenuationWeight=deviation.EffectiveWeight;
                if(!A4DetectionReliable(snrScore)||A4CalibrationConflict(deviation))
                {
                    A4Reject(c,a,b,wave,env,confidence,requiredSigned,lowBand,
                        !A4DetectionReliable(snrScore)?"insufficient_detection_reliability_for_confirmation":
                        "calibration_outside_measured_tolerance",false,deviation,unified);continue;
                }
                bool amplified=ratio>1.20+2/Math.Max(3,a.Snr);
                bool blind=delay<=Math.Max(Math.Max(a.Period,b.Period)*1.5,Math.Max(a.EquivalentSamples,b.EquivalentSamples)*2)||a.End>=b.Start;
                bool oppositeStructure=unified.Measured==null||unified.Measured.OppositeTemporalPolarity||!unified.Measured.StructureCompatible;
                if(blind||amplified||oppositeStructure||a.Clipped||b.Clipped||a.BaselineUnstable||b.BaselineUnstable||!Finite(confidence))
                {
                    string reason=blind?"overlapping_or_blind_pulse_support":amplified?"reflection_amplification_inconsistent":oppositeStructure?"incompatible_or_opposite_temporal_structure":
                        a.Clipped||b.Clipped?"clipped_pulse_support":a.BaselineUnstable||b.BaselineUnstable?"unstable_local_baseline":"non_finite_physical_score";
                    A4Reject(c,a,b,wave,env,confidence,requiredSigned,lowBand,reason,false,null,unified);continue;
                }
                A4Pulse[] between=pulses.Where(p=>p.Start>a.End&&p.End<b.Start&&p.Snr>8&&p.LocalAmplitude>a.LocalAmplitude*.40).ToArray();
                foreach(A4Pulse p in between)if(!p.MappingValid||p.Clipped||p.BaselineUnstable||p.UnresolvedOverlap)c.UnreliableInterveningIds.Add(p.Id);
                pool.Add(new A4Pair {Incident=a,Reflection=b,WaveCorrelation=wave,EnvelopeCorrelation=env,Correlation=corr,
                    WidthScore=widthScore,SpectrumScore=spectrum,AttenuationScore=deviation.Applied?deviation.ConsistencyScore:double.NaN,AmplitudeRatio=ratio,Confidence=confidence,
                    BandGainScore=bandGain,BandShapeScore=bandShape,ValidBands=validBands,BandExcessDb=bandExcess,
                    BandConsistencyFactor=bandConsistency,UnstableNoiseBands=unstableNoiseBands,LowBand=lowBand,
                    SignedCorrelationMinimum=requiredSigned,AttenuationWeight=attenuationWeight,UnifiedCorrelation=unified,
                    CalibrationDeviation=deviation,ScoreBeforeCalibration=scoreBeforeCalibration,CalibrationPenaltyFactor=calibrationFactor,
                    InterveningIds=between.Where(p=>p.MappingValid&&!p.Clipped&&!p.BaselineUnstable&&!p.UnresolvedOverlap).Select(p=>p.Id).Distinct().OrderBy(id=>id).ToArray(),
                    Position=A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length)});
                if(pool.Count>=A4MaximumCandidatePoolEdges){c.CandidatePoolLimitExceeded=true;return pool;}
            }
        }
        return pool;
    }
    private static void A4ResolveAutomaticSuppression(A4Context c)
    {
        if(c.Suppression>=0){c.EffectiveSuppression=c.Suppression;c.SuppressionResolution="explicit_user_count";return;}
        int dataLimit=c.RoundTripExact>0?Math.Max(0,(int)Math.Floor((c.Raw.Length-1)/c.RoundTripExact)):0;
        double loss=c.Calibration!=null?c.Calibration.AttenuationLossDb:double.NaN;
        if(!Finite(loss)||loss<=0)
        {
            c.EffectiveSuppression=dataLimit;
            c.SuppressionResolution="cal_no_positive_decay_data_bound_and_observed_echo_evidence_only";return;
        }
        double amplitude=c.Signal.Select(v=>Math.Abs(v-c.Center)).DefaultIfEmpty(0).Max();
        double floor=Math.Max(c.Threshold,3*c.Sigma);
        double estimate=amplitude>floor&&floor>0?20*Math.Log10(amplitude/floor)/loss:0;
        c.EffectiveSuppression=Math.Min(dataLimit,Math.Max(0,(int)Math.Min(int.MaxValue,Math.Floor(estimate))));
        c.SuppressionResolution="cal_roundtrip_exponential_decay_to_detection_noise_floor_and_data_bound";
    }
    private static int A4EchoOrder(int offset,double period,int tolerance)
    {
        if(offset<=0||period<=0)return 0;
        int order=(int)Math.Round((double)offset/period);
        return order>=1&&Math.Abs(offset-order*period)<=tolerance?order:0;
    }
    private static string A4LowBandPairJson(A4LowBandPairEvidence p)
    {
        if(p==null)return "null";
        return "{\"trusted\":"+(p.Trusted?"true":"false")+",\"enhanced\":"+(p.Enhanced?"true":"false")+
            ",\"reason\":"+Json(p.Reason)+",\"amplitude_ratio\":"+NumberJson(p.AmplitudeRatio)+
            ",\"expected_ratio_soft_model\":"+NumberJson(p.ExpectedRatio)+",\"uncertainty_db\":"+NumberJson(p.UncertaintyDb)+
            ",\"attenuation_penalty_score\":"+NumberJson(p.Score)+",\"incident_snr\":"+NumberJson(p.IncidentSnr)+
            ",\"reflection_snr\":"+NumberJson(p.ReflectionSnr)+",\"low_band_signed_correlation\":"+NumberJson(p.SignedCorrelation)+
            ",\"low_band_envelope_correlation\":"+NumberJson(p.EnvelopeCorrelation)+",\"separate_correlation_gate\":false,\"correlation_scope\":\"legacy_diagnostics_only_unified_guarded_3mhz_is_authoritative\"}";
    }
    private static string A4Diagnostics(A4Context c,List<A4Pulse> pulses,List<A4Pair> pairs)
    {
        StringBuilder b=new StringBuilder();
        b.Append("{\"full_envelope_diagnostic_only\":true,\"full_signed_wave_diagnostic_only\":true,\"local_guard_baseline_mad\":true,\"raw_same_polarity_gate\":false,\"temporal_structure_polarity_gate\":true");
        b.Append(",\"correlation_gate\":\"unified_fixed_3mhz_guarded_Pearson_dynamic_CAL_reference_before_candidate_pool_and_ranking\",\"correlation_score\":\"clamp(measured_signed_rho/time_dependent_reference,0,1)\",\"column21\":\"measured_signed_guarded_3mhz_Pearson\",\"unified_correlation_reference\":")
            .Append(A4UnifiedCorrelationReferenceJson(c));
        b.Append(",\"column22\":\"guarded_fixed_3mhz_reflection_to_incident_absolute_peak_ratio;NaN_if_unavailable;independent_of_attenuation_switch\"");
        b.Append(",\"raw_seed_window_pipeline\":").Append(A4WindowTraceJson(c));
        b.Append(",\"calibration_deviation_scoring\":{\"diagnostic_band_hz\":3000000,\"nominal_weights\":{\"correlation\":0.50,\"attenuation\":0.35,\"other\":0.15},\"severe_loss_retention\":0.05,\"snr_transition\":[3,10],\"engineering_spectral_margin_db\":6,\"mode\":\"weighted_measured_consistency; unavailable_weight_redistributed_50_to_15\",\"physical_delay_limit_samples\":")
            .Append(A4PhysicalDelayLimit(c)).Append(",\"quantization_allowance_samples\":0,\"candidate_endpoint_error_percent\":1,\"confirmed_boundary_tolerance_percent\":2,\"delay\":\"canonical_raw_peaks_independent_of_display_timing\",\"reference\":")
            .Append(A4AttenuationReferenceJson(c.AttenuationScoringReference)).Append(",\"candidate_evidence\":[");
        for(int ci=0;ci<c.CandidatePool.Count;ci++)
        {
            if(ci>0)b.Append(',');var cp=c.CandidatePool[ci];
            b.Append("{\"incident_raw_peak\":").Append(A4ChargeIndex(cp.Incident)).Append(",\"reflection_raw_peak\":").Append(A4ChargeIndex(cp.Reflection))
                .Append(",\"base_score_before_cal\":").Append(NumberJson(cp.ScoreBeforeCalibration))
                .Append(",\"combined_cal_factor\":").Append(NumberJson(cp.CalibrationPenaltyFactor))
                .Append(",\"signed_correlation\":").Append(NumberJson(cp.WaveCorrelation))
                .Append(",\"unified_correlation\":").Append(A4UnifiedCorrelationJson(cp.UnifiedCorrelation))
                .Append(",\"width_score\":").Append(NumberJson(cp.WidthScore))
                .Append(",\"width_comparison_enabled\":").Append(c.Width?"true":"false")
                .Append(",\"final_score\":").Append(NumberJson(cp.Confidence)).Append(",\"calibration_deviation\":").Append(A4AttenuationJson(cp.CalibrationDeviation)).Append('}');
        }
        b.Append("]}");
        b.Append(",\"incident_analysis_order\":\"original_absolute_amplitude_descending\",\"physical_pool_is_output_threshold_independent\":false")
            .Append(",\"raw_discovery_threshold\":").Append(NumberJson(c.RawDiscoveryThreshold))
            .Append(",\"incident_analysis_packet_ids\":").Append(NumberArrayJson(c.AnalysisVisitIds.Select(id=>(double)id).ToArray()))
            .Append(",\"incident_analysis_exclusions\":[");
        bool firstExcluded=true;
        foreach(var entry in c.AnalysisExclusions.OrderBy(v=>v.Key))
        {if(!firstExcluded)b.Append(',');firstExcluded=false;b.Append("{\"packet_id\":").Append(entry.Key).Append(",\"reason\":").Append(Json(entry.Value)).Append('}');}
        b.Append(']');
        b.Append(",\"excluded_unresolved_overlap_pulse_count\":").Append(pulses.Count(p=>p.UnresolvedOverlap))
            .Append(",\"excluded_unresolved_overlap_pulses\":[");
        bool firstUnresolved=true;
        foreach(A4Pulse p in pulses.Where(p=>p.UnresolvedOverlap))
        {
            if(!firstUnresolved)b.Append(',');firstUnresolved=false;
            b.Append("{\"pulse_id\":").Append(p.Id).Append(",\"onset\":").Append(p.Start).Append(",\"end\":").Append(p.End)
                .Append(",\"reason\":\"unresolved_overlapping_support\",\"candidate_eligible\":false,\"role_evidence_eligible\":false,\"echo_suppression_eligible\":false,\"claims_pulse_ownership\":false}");
        }
        b.Append(']');
        b.Append(",\"unreliable_intervening_events_no_veto_count\":").Append(c.UnreliableInterveningIds.Count)
            .Append(",\"unreliable_intervening_events_no_veto\":[");
        bool firstUnreliable=true;
        foreach(A4Pulse p in pulses.Where(p=>c.UnreliableInterveningIds.Contains(p.Id)))
        {
            if(!firstUnreliable)b.Append(',');firstUnreliable=false;
            b.Append("{\"pulse_id\":").Append(p.Id).Append(",\"onset\":").Append(p.Start).Append(",\"end\":").Append(p.End)
                .Append(",\"reason\":\"unreliable_mapping_clipped_unstable_or_unresolved_support\",\"vetoes_pair\":false,\"claims_pulse_ownership\":false}");
        }
        b.Append(']');
        b.Append(",\"minimum_matrix_physical_confidence\":0.70,\"confidence_capped_by_measured_correlation\":false,\"rejected_pairs_are_status_only\":true,\"legacy_minimum_matrix_physical_confidence_scope\":\"confirmed_graph_only\",\"rejected_pairs_are_status_only_scope\":\"rejected_graph_edges_only; eligible manual-review hypotheses are separately emitted\",\"main_parameter_matrix_includes_unaccepted_review\":true,\"manual_review_matrix_score_cap\":0.69");
        b.Append(",\"bidirectional_review\":").Append(A4CandidatePoolJson(c));
        b.Append(",\"rejected_pair_count\":").Append(c.RejectedPairCount).Append(",\"hard_correlation_rejected_count\":").Append(c.RejectedCorrelationCount)
            .Append(",\"physical_quality_rejected_count\":").Append(c.RejectedPhysicalConfidenceCount)
            .Append(",\"rejected_pair_examples_truncated\":").Append(c.RejectedPairCount>c.RejectedPairs.Count?"true":"false");
        b.Append(",\"rejected_pairs\":[");
        for(int k=0;k<c.RejectedPairs.Count;k++)
        {
            if(k>0)b.Append(',');A4RejectedPair p=c.RejectedPairs[k];
            b.Append("{\"incident_onset\":").Append(p.Incident.Start).Append(",\"reflection_onset\":").Append(p.Reflection.Start)
                .Append(",\"signed_full_correlation\":").Append(NumberJson(p.Wave)).Append(",\"envelope_correlation\":").Append(NumberJson(p.Envelope))
                .Append(",\"legacy_combined_correlation_diagnostic_only\":").Append(NumberJson(.78*p.Wave+.22*p.Envelope)).Append(",\"required_signed_correlation\":").Append(NumberJson(p.RequiredSigned))
                .Append(",\"unified_correlation\":").Append(A4UnifiedCorrelationJson(p.UnifiedCorrelation))
                .Append(",\"confidence\":").Append(NumberJson(p.Confidence)).Append(",\"reason\":").Append(Json(p.Reason))
                .Append(",\"claims_pulse_ownership\":false,\"matrix_row_emitted\":false,\"calibration_deviation\":").Append(A4AttenuationJson(p.CalibrationDeviation))
                .Append(",\"low_band_evidence\":").Append(A4LowBandPairJson(p.LowBand)).Append('}');
        }
        b.Append(']');
        if(c.LowBand!=null)
        {
            A4LowBandReference low=c.LowBand;
            b.Append(",\"calibration_low_band_reference\":{\"frequency_limit_hz\":3000000,\"transition_start_hz\":2700000,\"amplitude_measurement\":\"complete_packet_absolute_peak_after_fixed_lowpass\"")
                .Append(",\"retained_amplitude_activation_ratio\":0.35,\"maximum_trusted_cal_amplitude_ratio\":1.10,\"minimum_packet_snr\":10")
                .Append(",\"trusted\":").Append(low.Trusted?"true":"false").Append(",\"reason\":").Append(Json(low.Reason))
                .Append(",\"amplitude_ratio\":").Append(NumberJson(low.AmplitudeRatio)).Append(",\"incident_amplitude\":").Append(NumberJson(low.IncidentAmplitude))
                .Append(",\"reflection_amplitude\":").Append(NumberJson(low.ReflectionAmplitude)).Append(",\"incident_snr\":").Append(NumberJson(low.IncidentSnr))
                .Append(",\"reflection_snr\":").Append(NumberJson(low.ReflectionSnr)).Append(",\"uncertainty_db\":").Append(NumberJson(low.UncertaintyDb))
                .Append(",\"travel_seconds\":").Append(NumberJson(low.TravelSeconds)).Append(",\"incident_support\":[").Append(low.IncidentStart).Append(',').Append(low.IncidentEnd)
                .Append("],\"reflection_support\":[").Append(low.ReflectionStart).Append(',').Append(low.ReflectionEnd)
                .Append("],\"model\":\"min(1,cal_peak_ratio)^(loc_delay_seconds/cal_marked_roundtrip_seconds); soft_only; 3dB_model_margin_plus_noise\"}");
        }
        b.Append(",\"echo_ownership_policy\":\"complete_two_endpoint_unique_causal_chain_with_noise_resolved_decay_only;single_endpoint_periodic_coincidence_never_claims_owner\"");
        b.Append(",\"owned_later_echoes\":").Append(c.Suppressed);
        b.Append(",\"unresolved_long_envelopes\":").Append(c.OverlapCount);
        b.Append(",\"calibration_band_reference_available\":").Append(c.Bands!=null?"true":"false");
        b.Append(",\"calibration_noise_rejected_bands\":").Append(c.CalibrationNoiseRejectedBands);
        b.Append(",\"parallel_role_ambiguity_count\":").Append(c.ParallelAlternatives.Count);
        b.Append(",\"parallel_role_interpretation\":\"non_unique_one_to_one_roles_keep_component_pending_if_selected; physical_scores_unchanged; no_owner_or_confidence_boost\"");
        b.Append(",\"parallel_role_alternatives\":[");
        for(int k=0;k<c.ParallelAlternatives.Count;k++)
        {
            if(k>0)b.Append(',');A4ParallelEvidence e=c.ParallelAlternatives[k];
            b.Append("{\"first_short_edge\":").Append(A4ParallelEdgeJson(c,e.ShortFirst)).Append(",\"second_short_edge\":").Append(A4ParallelEdgeJson(c,e.ShortSecond))
                .Append(",\"first_parallel_edge\":").Append(A4ParallelEdgeJson(c,e.ParallelFirst)).Append(",\"second_parallel_edge\":").Append(A4ParallelEdgeJson(c,e.ParallelSecond));
            b.Append(",\"affected_pair_count\":6,\"affected_pair_count_definition\":\"six_conflicting_role_links_not_six_emitted_candidate_rows\",\"affected_pairs\":[");
            A4Pulse a=e.ShortFirst.A,second=e.ShortFirst.B,third=e.ShortSecond.A,fourth=e.ShortSecond.B;
            A4Pulse[][] links={new[]{a,second},new[]{third,fourth},new[]{a,third},new[]{second,fourth},new[]{second,third},new[]{a,fourth}};
            for(int j=0;j<links.Length;j++)
            {
                if(j>0)b.Append(',');int t0,t1;A4CanonicalPeaks(c,links[j][0],links[j][1],out t0,out t1);
                b.Append("{\"incident_onset\":").Append(links[j][0].Start).Append(",\"reflection_onset\":").Append(links[j][1].Start)
                    .Append(",\"selected_incident_index\":").Append(t0).Append(",\"selected_reflection_index\":").Append(t1)
                    .Append(",\"within_candidate_delay_range\":").Append(A4ReflectionDelayWithinWindow(c,t1-t0)?"true":"false")
                    .Append(",\"physical_score_unchanged\":true,\"review_state\":\"pending_if_selected_role_ambiguity\",\"reason\":\"shared_four_packet_non_unique_roles\"}");
            }
            b.Append("]}");
        }
        b.Append(']');
        if(c.Bands!=null)
        {
            b.Append(",\"calibration_band_edges_hz\":[").Append(String.Join(",",c.Bands.EdgesHz.Select(NumberJson))).Append("]");
            b.Append(",\"calibration_band_gain_db\":[").Append(String.Join(",",c.Bands.GainDb.Select(NumberJson))).Append("]");
            b.Append(",\"calibration_band_valid\":[").Append(String.Join(",",c.Bands.Valid.Select(v=>v?"true":"false"))).Append("]");
            b.Append(",\"calibration_noise_policy\":\"separate_packet_maximum_local_block_psd\"");
            b.Append(",\"calibration_guard_blocks_pre_post\":").Append(NumberArrayJson(c.Bands.GuardBlocks.Select(v=>(double)v).ToArray()));
            b.Append(",\"calibration_incident_band_noise\":").Append(NumberArrayJson(c.Bands.IncidentNoise));
            b.Append(",\"calibration_reflection_band_noise\":").Append(NumberArrayJson(c.Bands.ReflectionNoise));
            b.Append(",\"calibration_incident_band_snr\":").Append(NumberArrayJson(c.Bands.IncidentSnr));
            b.Append(",\"calibration_reflection_band_snr\":").Append(NumberArrayJson(c.Bands.ReflectionSnr));
            b.Append(",\"calibration_incident_support\":[").Append(c.Bands.IncidentStart).Append(",").Append(c.Bands.IncidentEnd).Append("]");
            b.Append(",\"calibration_reflection_support\":[").Append(c.Bands.ReflectionStart).Append(",").Append(c.Bands.ReflectionEnd).Append("]");
        }
        b.Append(",\"pulse_count\":").Append(pulses.Count).Append(",\"pulse_geometry_and_amplitude\":[");
        for(int k=0;k<pulses.Count;k++)
        {
            if(k>0)b.Append(',');A4Pulse pulse=pulses[k];
            double rawAmplitude=A4ChargeIndex(pulse)>=0&&A4ChargeIndex(pulse)<c.Raw.Length?A4RawCharge(c,A4ChargeIndex(pulse)):double.NaN;
            double filteredAmplitude=pulse.Peak>=0&&pulse.Peak<c.Raw.Length?Math.Abs(A4Sample(c.Signal,pulse,pulse.Peak)-pulse.Baseline):double.NaN;
            b.Append("{\"packet_id\":").Append(pulse.Id).Append(",\"raw_index\":").Append(pulse.RawPeak)
                .Append(",\"raw_absolute_peak_index_before_filter\":").Append(A4ChargeIndex(pulse))
                .Append(",\"filter_index\":").Append(pulse.Peak).Append(",\"onset\":").Append(pulse.Start).Append(",\"end\":").Append(pulse.End)
                .Append(",\"span_samples\":").Append(pulse.End-pulse.Start+1).Append(",\"raw_mapped_absolute_amplitude\":").Append(NumberJson(rawAmplitude))
                .Append(",\"filtered_absolute_peak_amplitude\":").Append(NumberJson(filteredAmplitude))
                .Append(",\"mapping_valid\":").Append(pulse.MappingValid?"true":"false").Append(",\"clipped\":").Append(pulse.Clipped?"true":"false")
                .Append(",\"baseline_unstable\":").Append(pulse.BaselineUnstable?"true":"false").Append(",\"unresolved_overlap\":").Append(pulse.UnresolvedOverlap?"true":"false")
                .Append(",\"owner\":").Append(pulse.Owner).Append('}');
        }
        b.Append("],\"pairs\":[");
        for(int i=0;i<pairs.Count;i++)
        {
            if(i>0)b.Append(",");
            A4Pair p=pairs[i];
            b.Append("{\"incident_onset\":").Append(p.Incident.Start).Append(",\"reflection_onset\":").Append(p.Reflection.Start)
                .Append(",\"raw_incident_charge_peak\":").Append(A4ChargeIndex(p.Incident)).Append(",\"raw_reflection_identity_peak\":").Append(A4ChargeIndex(p.Reflection))
                .Append(",\"incident_peak\":").Append(p.Incident.RawPeak).Append(",\"reflection_peak\":").Append(p.Reflection.RawPeak)
                .Append(",\"output_incident_index\":").Append(p.OutputIncident).Append(",\"output_reflection_index\":").Append(p.OutputReflection)
                .Append(",\"output_timing_reason\":").Append(Json(p.OutputTimingReason))
                .Append(",\"incident_end\":").Append(p.Incident.End).Append(",\"reflection_end\":").Append(p.Reflection.End)
                .Append(",\"wave_correlation\":").Append(NumberJson(p.WaveCorrelation)).Append(",\"envelope_correlation\":").Append(NumberJson(p.EnvelopeCorrelation))
                .Append(",\"measured_correlation\":").Append(NumberJson(p.Correlation)).Append(",\"unified_correlation\":").Append(A4UnifiedCorrelationJson(p.UnifiedCorrelation))
                .Append(",\"required_signed_correlation\":").Append(NumberJson(p.SignedCorrelationMinimum)).Append(",\"attenuation_weight\":").Append(NumberJson(p.AttenuationWeight))
                .Append(",\"low_band_evidence\":").Append(A4LowBandPairJson(p.LowBand))
                .Append(",\"amplitude_ratio\":").Append(NumberJson(p.AmplitudeRatio)).Append(",\"width_score\":").Append(NumberJson(p.WidthScore))
                .Append(",\"incident_snr\":").Append(NumberJson(p.Incident.Snr)).Append(",\"reflection_snr\":").Append(NumberJson(p.Reflection.Snr))
                .Append(",\"incident_local_baseline\":").Append(NumberJson(p.Incident.Baseline)).Append(",\"reflection_local_baseline\":").Append(NumberJson(p.Reflection.Baseline))
                .Append(",\"spectrum_score\":").Append(NumberJson(p.SpectrumScore)).Append(",\"attenuation_score\":").Append(NumberJson(p.AttenuationScore))
                .Append(",\"valid_calibration_bands\":").Append(p.ValidBands)
                .Append(",\"band_gain_score\":").Append(NumberJson(p.BandGainScore)).Append(",\"band_shape_score\":").Append(NumberJson(p.BandShapeScore))
                .Append(",\"cal_band_excess_after_noise_db\":").Append(NumberJson(p.BandExcessDb))
                .Append(",\"cal_band_consistency_factor\":").Append(NumberJson(p.BandConsistencyFactor))
                .Append(",\"unstable_or_missing_noise_bands\":").Append(p.UnstableNoiseBands)
                .Append(",\"confidence\":").Append(NumberJson(p.Confidence)).Append(",\"blind\":").Append(p.Blind?"true":"false")
                .Append(",\"ambiguous\":").Append(p.Ambiguous?"true":"false").Append(",\"amplified\":").Append(p.Amplified?"true":"false")
                .Append(",\"parallel_role_ambiguous\":").Append(p.ParallelAmbiguous?"true":"false")
                .Append(",\"later_echoes_suppressed\":").Append(p.Suppressed).Append("}");
        }
        b.Append("]}");return b.ToString();
    }
}
