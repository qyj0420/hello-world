using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4UnifiedCorrelationState
    {
        internal A4Context MeasurementContext;
        internal A4AttenuationScoringReference CalibrationMeasurement;
        internal A6Correlation CalibrationCorrelation=new A6Correlation();
        internal bool CalibrationReliable,Prepared;
        internal string CalibrationReason="not_prepared",PreparationReason="not_prepared";
        internal double CalibrationTravelSeconds=double.NaN;
        internal readonly Dictionary<int,A4AttenuationMeasurement> Measurements=new Dictionary<int,A4AttenuationMeasurement>();
        internal readonly Dictionary<int,A6OriginalGuardTrace> GuardTraces=new Dictionary<int,A6OriginalGuardTrace>();
        internal readonly Dictionary<int,int[]> PacketSignatures=new Dictionary<int,int[]>();
    }

    private sealed class A4UnifiedCorrelationEvidence
    {
        internal A6Correlation Measured=new A6Correlation();
        internal double Reference=1,Floor=.70,Score=double.NaN,TravelFraction=double.NaN,AlignmentMargin=double.NaN;
        internal double UnclampedTravelFraction=double.NaN;
        internal bool Passed,Available,CalibrationUsed,ModelTravelClamped,ZeroAlignmentMargin;
        internal string Basis="not_measured",Reason="not_measured";
        internal int CanonicalDelay;
        internal A4AttenuationMeasurement Incident,Reflection;
        internal A6OriginalGuardTrace IncidentGuard,ReflectionGuard;
    }

    // Prepare after raw packet supports/identities have been finalized. The private context
    // deliberately does not share attenuation references, measurement caches or mutable flags.
    private static void A4PrepareUnifiedCorrelation(A4Context c,List<A4Pulse> pulses)
    {
        if(c==null)return;
        var state=new A4UnifiedCorrelationState();c.UnifiedCorrelation=state;
        var calibrationContext=new A4Context {Calibration=c.Calibration,Attenuation=true,Fs=c.Fs};
        state.CalibrationMeasurement=A4BuildAttenuationScoringReference(calibrationContext);
        state.CalibrationCorrelation=A6CalibrationCorrelation(calibrationContext,state.CalibrationMeasurement);
        A4AttenuationScoringReference cal=state.CalibrationMeasurement;
        A6Correlation rho=state.CalibrationCorrelation;
        state.CalibrationTravelSeconds=cal.TravelSeconds;
        double unusedReference,unusedFloor,unusedMargin;
        bool validShape=rho.Valid&&rho.Fixed>=A6Settings.MinimumUsableCalibrationCorrelation&&
            A4UnifiedCorrelationReferenceMath(rho.Fixed,rho.Lower,1,out unusedReference,out unusedFloor,out unusedMargin);
        bool validTravel=Finite(cal.TravelSeconds)&&cal.TravelSeconds>0;
        state.CalibrationReliable=cal.Valid&&validTravel&&cal.IncidentSnr>=6&&cal.ReflectionSnr>=6&&validShape;
        state.CalibrationReason=!cal.Valid?cal.Reason:!validTravel?"calibration_round_trip_not_positive_finite":
            cal.IncidentSnr<6||cal.ReflectionSnr<6?"calibration_snr_below_six_sigma":
            !validShape?"calibration_signed_correlation_or_alignment_envelope_unusable":
            "guarded_calibration_at_least_six_sigma_signed_rho_at_least_point15";
        A4PrepareUnifiedCorrelationMeasurements(c,pulses,state,null,double.NaN);
    }

    // Review hypotheses can change raw support boundaries. Reuse only read-only signal/CAL
    // data, never the source's measurement cache or its known-support guard occupancy list.
    private static void A4PrepareUnifiedCorrelationReview(A4Context c,A4Context source,List<A4Pulse> pulses)
    {
        if(c==null)return;
        A4UnifiedCorrelationState prior=source==null?null:source.UnifiedCorrelation;
        if(prior==null||!prior.Prepared||!Object.ReferenceEquals(c.Raw,prior.MeasurementContext.Raw)||
            c.Fs!=prior.MeasurementContext.Fs||c.RawCenter!=prior.MeasurementContext.RawCenter||
            !Object.ReferenceEquals(c.Calibration,source.Calibration)||
            c.MeasurementSignal!=null&&!Object.ReferenceEquals(c.MeasurementSignal,prior.MeasurementContext.MeasurementSignal))
        {A4PrepareUnifiedCorrelation(c,pulses);return;}
        if(c.MeasurementSignal==null)c.MeasurementSignal=prior.MeasurementContext.MeasurementSignal;
        var state=new A4UnifiedCorrelationState {
            CalibrationMeasurement=prior.CalibrationMeasurement,CalibrationCorrelation=prior.CalibrationCorrelation,
            CalibrationReliable=prior.CalibrationReliable,CalibrationReason=prior.CalibrationReason,
            CalibrationTravelSeconds=prior.CalibrationTravelSeconds};
        c.UnifiedCorrelation=state;
        A4PrepareUnifiedCorrelationMeasurements(c,pulses,state,prior.MeasurementContext.AttenuationScoringSignal,
            prior.MeasurementContext.AttenuationScoringRawSigma);
    }

    private static void A4PrepareUnifiedCorrelationMeasurements(A4Context c,List<A4Pulse> pulses,
        A4UnifiedCorrelationState state,double[] preparedLowSignal,double preparedRawSigma)
    {
        if(c.Raw==null||c.Raw.Length<3||pulses==null||!Finite(c.Fs)||c.Fs<6000000||!Finite(c.RawCenter))
        {state.PreparationReason="unified_raw_signal_packets_or_sample_rate_unavailable";return;}
        if(preparedLowSignal==null)
            foreach(double sample in c.Raw)if(!Finite(sample))
            {state.PreparationReason="unified_raw_signal_contains_nonfinite_samples";return;}
        var ids=new HashSet<int>();
        foreach(A4Pulse p in pulses)if(p==null||!ids.Add(p.Id))
        {state.PreparationReason="unified_packet_ids_missing_or_not_unique";return;}
        foreach(A4Pulse p in pulses)state.PacketSignatures.Add(p.Id,A4UnifiedPacketSignature(p));
        A4MeasurementSignal profile=c.MeasurementSignal;
        if(profile==null||!Object.ReferenceEquals(profile.Raw,c.Raw)||profile.Fs!=c.Fs)
        {profile=A4PrepareMeasurementSignal(c.Raw,c.Fs,c.RoundTrip,pulses);c.MeasurementSignal=profile;}
        state.MeasurementContext=new A4Context {
            Algorithm=c.Algorithm,Index4ExtendedGuardSearch=c.Index4ExtendedGuardSearch,SixthHybridGuardSearch=c.SixthHybridGuardSearch,
            Raw=c.Raw,RawCenter=c.RawCenter,Fs=c.Fs,RoundTrip=c.RoundTrip,RoundTripExact=c.RoundTripExact,
            MeasurementSignal=profile,AttenuationScoringSignal=profile.BandSignal,
            AttenuationScoringRawSigma=profile.RawSigma,
            AttenuationScoringPackets=new List<A4Pulse>(pulses)};
        state.Prepared=true;state.PreparationReason="shared_debiased_10khz_3mhz_signal_independent_guard_measurement_context";
    }

    private static int[] A4UnifiedPacketSignature(A4Pulse p)
    {
        return new[]{p.Start,p.End,p.Peak,p.RawPeak,p.RawAmplitudeIndex,p.Clipped?1:0,p.UnresolvedOverlap?1:0,
            p.HasRefinedGuardProof?1:0,p.BoundaryRefined?1:0,p.RefinedLeftGuardStart,p.RefinedLeftGuardEnd,p.RefinedRightGuardStart,p.RefinedRightGuardEnd};
    }
    private static bool A4UnifiedPacketUnchanged(A4UnifiedCorrelationState state,A4Pulse p)
    {
        int[] signature;
        if(p==null||!state.PacketSignatures.TryGetValue(p.Id,out signature))return false;
        return signature[0]==p.Start&&signature[1]==p.End&&signature[2]==p.Peak&&signature[3]==p.RawPeak&&
            signature[4]==p.RawAmplitudeIndex&&signature[5]==(p.Clipped?1:0)&&signature[6]==(p.UnresolvedOverlap?1:0)&&
            signature[7]==(p.HasRefinedGuardProof?1:0)&&signature[8]==(p.BoundaryRefined?1:0)&&
            signature[9]==p.RefinedLeftGuardStart&&signature[10]==p.RefinedLeftGuardEnd&&
            signature[11]==p.RefinedRightGuardStart&&signature[12]==p.RefinedRightGuardEnd;
    }

    // The CAL +/-2-sample sensitivity is an engineering alignment allowance, not a
    // noise bound or statistical confidence interval. Do not exponentiate that allowance.
    private static bool A4UnifiedCorrelationReferenceMath(double calFixed,double calLower,double fraction,
        out double reference,out double floor,out double margin)
    {
        reference=floor=margin=double.NaN;
        if(!Finite(calFixed)||!Finite(calLower)||!Finite(fraction)||calFixed<=0||calFixed>1||
            calLower<=0||calLower>calFixed||fraction<0||fraction>1)return false;
        margin=calFixed-calLower;
        reference=Math.Pow(calFixed,fraction);
        floor=Math.Max(calLower,reference-margin);
        return Finite(reference)&&Finite(floor)&&floor>0&&floor<=reference;
    }

    private static A4AttenuationMeasurement A4UnifiedCorrelationMeasurement(A4UnifiedCorrelationState state,
        A4Pulse p,out A6OriginalGuardTrace trace)
    {
        trace=null;
        if(p==null)return new A4AttenuationMeasurement {Reason="unified_packet_unavailable"};
        if(!A4UnifiedPacketUnchanged(state,p))return new A4AttenuationMeasurement {Reason="unified_packet_identity_or_support_changed_reprepare_required"};
        A4AttenuationMeasurement measurement;
        if(state.Measurements.TryGetValue(p.Id,out measurement))
        {state.GuardTraces.TryGetValue(p.Id,out trace);return measurement;}
        measurement=A6ScoringMeasurementCore(state.MeasurementContext,p,out trace);
        state.Measurements.Add(p.Id,measurement);state.GuardTraces.Add(p.Id,trace);
        return measurement;
    }

    private static A4UnifiedCorrelationEvidence A4UnifiedCorrelation(A4Context c,A4Pulse a,A4Pulse b,int canonicalDelay)
    {
        var e=new A4UnifiedCorrelationEvidence {CanonicalDelay=canonicalDelay};
        A4UnifiedCorrelationState state=c==null?null:c.UnifiedCorrelation;
        if(state==null||!state.Prepared)
        {e.Basis="unified_context_unavailable";e.Reason=state==null?"unified_context_not_prepared":state.PreparationReason;return e;}
        if(!Object.ReferenceEquals(c.Raw,state.MeasurementContext.Raw)||c.Fs!=state.MeasurementContext.Fs||
            c.RawCenter!=state.MeasurementContext.RawCenter||
            !Object.ReferenceEquals(c.MeasurementSignal,state.MeasurementContext.MeasurementSignal))
        {e.Basis="unified_context_unavailable";e.Reason="raw_signal_or_sampling_context_changed_reprepare_required";return e;}
        foreach(A4Pulse prepared in state.MeasurementContext.AttenuationScoringPackets)
            if(!A4UnifiedPacketUnchanged(state,prepared))
            {e.Basis="unified_context_unavailable";e.Reason="known_packet_support_changed_reprepare_required";return e;}
        e.Basis="automatic_calibration_unusable_fallback_absolute_70_percent";
        if(canonicalDelay<=0)
        {e.Reason="canonical_delay_must_be_positive";return e;}
        double travelSeconds=canonicalDelay/c.Fs;
        double roundTripSeconds=state.CalibrationReliable?state.CalibrationTravelSeconds:
            Finite(c.RoundTripExact)&&c.RoundTripExact>0?c.RoundTripExact/c.Fs:double.NaN;
        if(Finite(roundTripSeconds)&&roundTripSeconds>0)
        {
            e.UnclampedTravelFraction=travelSeconds/roundTripSeconds;
            e.TravelFraction=Clamp(e.UnclampedTravelFraction,0,1);
            e.ModelTravelClamped=e.UnclampedTravelFraction!=e.TravelFraction;
        }
        if(state.CalibrationReliable)
        {
            if(!A4UnifiedCorrelationReferenceMath(state.CalibrationCorrelation.Fixed,state.CalibrationCorrelation.Lower,
                e.TravelFraction,out e.Reference,out e.Floor,out e.AlignmentMargin))
            {e.Basis="calibration_reference_math_unavailable";e.Reason="calibration_reference_math_invalid";return e;}
            e.CalibrationUsed=true;e.ZeroAlignmentMargin=e.AlignmentMargin==0;
            e.Basis="calibration_power_reference_fixed_alignment_margin_not_noise_CI";
        }
        e.Incident=A4UnifiedCorrelationMeasurement(state,a,out e.IncidentGuard);
        e.Reflection=A4UnifiedCorrelationMeasurement(state,b,out e.ReflectionGuard);
        if(!e.Incident.Valid||!e.Reflection.Valid)
        {e.Reason="guarded_measurement_unavailable:"+e.Incident.Reason+"/"+e.Reflection.Reason;return e;}
        if(a.Id==b.Id)
        {e.Reason="distinct_original_packet_identities_required";return e;}
        e.Measured=c.ReviewQuietGuardRecovery?
            A6ReviewConnectedTemporalCorrelation(c.Raw,c.Fs,e.Incident,e.Reflection,A4ChargeIndex(a),A4ChargeIndex(b)):
            A6TemporalPacketCorrelation(c.Raw,c.Fs,e.Incident,e.Reflection,A4ChargeIndex(a),A4ChargeIndex(b));
        if(!e.Measured.Valid)
        {e.Reason="signed_temporal_pearson_correlation_unavailable:"+e.Measured.StructureReason;return e;}
        e.Available=true;e.Score=Clamp(e.Measured.Fixed/e.Reference,0,1);
        // This is only the correlation gate. Callers retain their independent quality,
        // temporal polarity/overlap, geometry, ownership and acceptance gates, including delays >RT.
        e.Passed=e.Measured.Fixed+1e-12>=e.Floor;
        e.Reason=e.Passed?"signed_fixed_correlation_meets_effective_floor":"signed_fixed_correlation_below_effective_floor";
        return e;
    }

    private static string A4UnifiedCorrelationJson(A4UnifiedCorrelationEvidence e)
    {
        if(e==null)return "null";
        return "{\"available\":"+(e.Available?"true":"false")+",\"passed_correlation_only\":"+(e.Passed?"true":"false")+
            ",\"signed_fixed\":"+NumberJson(e.Measured.Fixed)+",\"minus_plus_two_samples_lower\":"+NumberJson(e.Measured.Lower)+
            ",\"minus_plus_two_samples_upper\":"+NumberJson(e.Measured.Upper)+",\"diagnostic_best_shift_samples\":"+e.Measured.Shift+
            ",\"polarity_ratio\":"+NumberJson(e.Measured.PolarityRatio)+",\"reference\":"+NumberJson(e.Reference)+
            ",\"temporal_alignment\":"+A6TemporalCorrelationJson(e.Measured)+
            ",\"effective_floor\":"+NumberJson(e.Floor)+",\"normalized_score\":"+NumberJson(e.Score)+
            ",\"basis\":"+Json(e.Basis)+",\"reason\":"+Json(e.Reason)+",\"calibration_used\":"+(e.CalibrationUsed?"true":"false")+
            ",\"canonical_delay_samples\":"+e.CanonicalDelay+",\"travel_fraction_model_clamped\":"+NumberJson(e.TravelFraction)+
            ",\"travel_fraction_unclamped\":"+NumberJson(e.UnclampedTravelFraction)+",\"model_travel_was_clamped\":"+(e.ModelTravelClamped?"true":"false")+
            ",\"fixed_alignment_margin\":"+NumberJson(e.AlignmentMargin)+",\"zero_alignment_margin_strict\":"+(e.ZeroAlignmentMargin?"true":"false")+
            ",\"interval_is_not_statistical_confidence_interval\":true,\"noise_snr_correction_applied\":false,\"geometry_acceptance_is_callers_responsibility\":true"+
            ",\"incident_measurement\":"+A4AttenuationMeasurementJson(e.Incident)+",\"reflection_measurement\":"+A4AttenuationMeasurementJson(e.Reflection)+
            ",\"incident_guard_selection\":"+A6GuardTraceJson(e.IncidentGuard)+",\"reflection_guard_selection\":"+A6GuardTraceJson(e.ReflectionGuard)+"}";
    }

    private static string A4UnifiedCorrelationReferenceJson(A4Context c)
    {
        A4UnifiedCorrelationState s=c==null?null:c.UnifiedCorrelation;
        if(s==null)return "null";
        return "{\"prepared\":"+(s.Prepared?"true":"false")+",\"preparation_reason\":"+Json(s.PreparationReason)+
            ",\"measurement_band_hz\":[10000,3000000],\"shape_anchor\":\"original_temporal_lobe_unique_processed_correspondence\",\"timing_anchor\":\"unchanged_canonical_raw_peak\""+
            ",\"calibration_reliable\":"+(s.CalibrationReliable?"true":"false")+",\"calibration_reason\":"+Json(s.CalibrationReason)+
            ",\"calibration_fixed\":"+NumberJson(s.CalibrationCorrelation.Fixed)+",\"calibration_minus_plus_two_samples_lower\":"+NumberJson(s.CalibrationCorrelation.Lower)+
            ",\"calibration_minus_plus_two_samples_upper\":"+NumberJson(s.CalibrationCorrelation.Upper)+
            ",\"calibration_temporal_alignment\":"+A6TemporalCorrelationJson(s.CalibrationCorrelation)+
            ",\"calibration_round_trip_seconds\":"+NumberJson(s.CalibrationTravelSeconds)+
            ",\"fixed_alignment_margin\":"+NumberJson(s.CalibrationReliable?s.CalibrationCorrelation.Fixed-s.CalibrationCorrelation.Lower:double.NaN)+
            ",\"zero_alignment_margin_strict\":"+(s.CalibrationReliable&&s.CalibrationCorrelation.Fixed==s.CalibrationCorrelation.Lower?"true":"false")+
            ",\"reference_model\":\"CALfixed^clamp(canonical_delay_seconds/CAL_round_trip_seconds,0,1)\""+
            ",\"floor_model\":\"max(CALlower,reference-(CALfixed-CALlower))\",\"fallback_floor\":0.70"+
            ",\"tolerance_interpretation\":\"fixed_alignment_margin_not_noise_CI\",\"interval_is_not_statistical_confidence_interval\":true,\"noise_snr_correction_applied\":false"+
            ",\"snr_interpretation\":\"low_band_peak_amplitude_over_conservative_raw_noise_sigma_not_energy_snr\""+
            ",\"independent_measurement_cache\":true,\"measured_packet_count\":"+s.Measurements.Count+
            ",\"calibration_measurement\":"+A4AttenuationReferenceJson(s.CalibrationMeasurement)+"}";
    }
}
