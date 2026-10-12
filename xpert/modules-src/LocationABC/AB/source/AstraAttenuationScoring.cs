using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4AttenuationEvidence
    {
        internal bool Enabled,Applied,Passed,BasePassed;
        internal double TolerancePercent=15,ErrorPercent=double.NaN;
        internal double EffectiveTolerancePercent=double.NaN,PropagatedCalibrationUncertaintyPercent=double.NaN,CalibrationAdaptationLimitPercent=double.NaN;
        internal string Reason="not_measured";
        internal double CalRetention,ObservedRatio,ExpectedRatio,LowerRatio,UpperRatio,DeviationDb,UncertaintyDb,ExcessDb;
        internal double CalReflectionSnr,PairReflectionSnr,EffectiveWeight,PenaltyFactor=1,TravelFraction;
        internal double PredictedReflectionSnr=double.NaN;
        internal double ModelTravelFraction,CorrelationWeight,OtherWeight,ConsistencyScore,OtherScore,WeightedScore;
        internal double SoftConsistencyScore=double.NaN;
        internal bool BoundaryModelClamped;
        internal string WeightQualitySource="not_measured";
    }
    internal double AttenuationErrorTolerancePercent { get; set; }=15;
    internal double CalibrationToleranceAdaptationLimitPercent { get; set; }=HVX.SignalEvidence.CalibrationQualityMath.DefaultAdaptationLimitPercent;
    // One shared raw-amplitude policy for all seven methods. Fixed-band waveforms
    // remain correlation/width evidence, not a substitute amplitude domain.
    private static A4AttenuationEvidence A4AttenuationPenalty(bool enabled,bool referenceValid,
        double calRetention,double observedRatio,double travelFraction,double calIncidentSnr,double calReflectionSnr,
        double pairIncidentSnr,double pairReflectionSnr,double extraUncertaintyDb)
    {
        return A4RawAttenuationEvidence(enabled,referenceValid,1,observedRatio,calRetention,
            travelFraction,1,15,calIncidentSnr,calReflectionSnr,pairIncidentSnr,pairReflectionSnr);
    }
    private static A4AttenuationEvidence A4RawAttenuationEvidence(bool enabled,bool referenceValid,
        double incident,double reflection,double calRetention,double delay,double travel,double tolerance,
        double calIncidentSnr,double calReflectionSnr,double pairIncidentSnr,double pairReflectionSnr)
    {return A4QualityAdjustedAttenuationEvidence(enabled,referenceValid,incident,reflection,calRetention,delay,travel,tolerance,
        calIncidentSnr,calReflectionSnr,pairIncidentSnr,pairReflectionSnr,0,0);}
    private static A4AttenuationEvidence A4QualityAdjustedAttenuationEvidence(bool enabled,bool referenceValid,
        double incident,double reflection,double calRetention,double delay,double travel,double tolerance,
        double calIncidentSnr,double calReflectionSnr,double pairIncidentSnr,double pairReflectionSnr,
        double calibrationUncertaintyPercent,double calibrationAdaptationLimitPercent)
    {
        var e=new A4AttenuationEvidence {Enabled=enabled,TolerancePercent=tolerance,CalRetention=calRetention,
            CalReflectionSnr=calReflectionSnr,PairReflectionSnr=pairReflectionSnr,
            ObservedRatio=incident>0?reflection/incident:double.NaN};
        if(!enabled){e.Reason="attenuation_reference_disabled";return e;}
        bool reliable=referenceValid&&Finite(calIncidentSnr)&&Finite(calReflectionSnr)&&
            calIncidentSnr>=6&&calReflectionSnr>=6&&Finite(pairIncidentSnr)&&Finite(pairReflectionSnr)&&
            pairIncidentSnr>=4.5&&pairReflectionSnr>=4.5;
        var shared=HVX.SignalEvidence.SixthConfidenceMath.Attenuation(incident,reflection,calRetention,delay,travel,tolerance,reliable,
            calibrationUncertaintyPercent,calibrationAdaptationLimitPercent);
        e.Applied=shared.Available;e.Passed=shared.Passed;e.Reason=shared.Reason;
        e.BasePassed=shared.BasePassed;e.EffectiveTolerancePercent=shared.EffectiveTolerancePercent;
        e.PropagatedCalibrationUncertaintyPercent=shared.PropagatedUncertaintyPercent;e.CalibrationAdaptationLimitPercent=shared.AdaptationLimitPercent;
        e.ErrorPercent=shared.ErrorPercent;e.SoftConsistencyScore=shared.Score;
        e.ExpectedRatio=shared.ExpectedRatio;e.LowerRatio=shared.LowerAmplitude/incident;e.UpperRatio=shared.UpperAmplitude/incident;
        e.TravelFraction=shared.TravelFraction;e.ModelTravelFraction=shared.ModelTravelFraction;
        e.BoundaryModelClamped=shared.TravelFraction>shared.ModelTravelFraction;
        e.EffectiveWeight=enabled?.35:0;e.WeightQualitySource="guarded_original_CAL_LOC_fixed_nominal_weight";
        if(shared.Available)
        {
            e.DeviationDb=Math.Abs(20*Math.Log10(shared.ObservedRatio/shared.ExpectedRatio));
            e.UncertaintyDb=0;
            e.ExcessDb=shared.Passed?0:shared.ObservedRatio<e.LowerRatio&&e.LowerRatio>0?
                20*Math.Log10(e.LowerRatio/shared.ObservedRatio):20*Math.Log10(shared.ObservedRatio/e.UpperRatio);
            e.PenaltyFactor=shared.Score;
        }
        return e;
    }
    private static void A4PrepareAttenuationScoring(A4Context c,List<A4Pulse> pulses)
    {
        c.AttenuationScoringReference=A4BuildAttenuationScoringReference(c);
        c.AttenuationScoringPackets=pulses;
        // The column-22 measurement is a diagnostic independent of the user's
        // attenuation switch and of whether a usable CAL exists.
        if(c.Raw==null||!Finite(c.Fs)||c.Fs<6000000)return;
        if(c.MeasurementSignal==null)c.MeasurementSignal=A4PrepareMeasurementSignal(c.Raw,c.Fs,c.RoundTrip);
        c.AttenuationScoringRawSigma=c.MeasurementSignal.RawSigma;
        // A fixed diagnostic band only. Never replaces original charge or analysis waveforms.
        c.AttenuationScoringSignal=c.MeasurementSignal.BandSignal;
    }
    private static double A4OutputAttenuationRatio(A4Context c,A4Pulse a,A4Pulse b)
    {
        var incident=A4ScoringMeasurement(c,a);var reflection=A4ScoringMeasurement(c,b);
        if(!incident.Valid||!reflection.Valid||incident.RawAmplitude<=0)return double.NaN;
        double ratio=reflection.RawAmplitude/incident.RawAmplitude;
        return Finite(ratio)&&ratio>0?ratio:double.NaN;
    }
    private static A4AttenuationMeasurement A4ScoringMeasurement(A4Context c,A4Pulse p)
    {
        if(p.AttenuationMeasurement!=null)return p.AttenuationMeasurement;
        if(c.AttenuationScoringSignal==null||c.AttenuationScoringPackets==null)
            return new A4AttenuationMeasurement {Reason="scoring_signal_unavailable"};
        A6OriginalGuardTrace trace;
        p.AttenuationMeasurement=A6ScoringMeasurementCore(c,p,out trace);
        return p.AttenuationMeasurement;
    }
    private static A4AttenuationEvidence A4CalibrationDeviation(A4Context c,A4Pulse a,A4Pulse b)
    {
        int peakA,peakB;
        bool peaks=A4CanonicalPeaks(c,a,b,out peakA,out peakB);
        return A4CalibrationDeviationAtDelay(c,a,b,peaks?(peakB-peakA)/c.Fs:double.NaN);
    }
    private static A4AttenuationEvidence A4CalibrationDeviationAtDelay(A4Context c,A4Pulse a,A4Pulse b,double delay)
    {
        var reference=c.AttenuationScoringReference;
        if(!c.Attenuation||reference==null||!reference.Valid)
        {
            var neutral=new A4AttenuationEvidence {Enabled=c.Attenuation,TolerancePercent=c.AttenuationTolerancePercent};
            neutral.Reason=!c.Attenuation?"attenuation_reference_disabled":reference==null?"calibration_score_reference_missing":reference.Reason;
            return neutral;
        }
        var ma=A4ScoringMeasurement(c,a);var mb=A4ScoringMeasurement(c,b);
        var evidence=A4QualityAdjustedAttenuationEvidence(true,ma.Valid&&mb.Valid,ma.RawAmplitude,mb.RawAmplitude,
            reference.RawRetention,delay,reference.TravelSeconds,c.AttenuationTolerancePercent,
            reference.IncidentSnr,reference.ReflectionSnr,ma.Snr,mb.Snr,
            reference.Quality==null?double.NaN:reference.Quality.RelativeUncertaintyPercent,c.CalibrationToleranceAdaptationLimitPercent);
        if(!ma.Valid||!mb.Valid)evidence.Reason="loc_incident_"+ma.Reason+";loc_reflection_"+mb.Reason;
        return evidence;
    }
    private static string A4AttenuationJson(A4AttenuationEvidence e)
    {
        if(e==null)return "null";
        return "{\"enabled\":"+(e.Enabled?"true":"false")+",\"applied\":"+(e.Applied?"true":"false")+",\"passed\":"+(e.Passed?"true":"false")+",\"tolerance_percent\":"+NumberJson(e.TolerancePercent)+",\"error_percent\":"+NumberJson(e.ErrorPercent)+",\"reason\":"+Json(e.Reason)+
            ",\"base_tolerance_passed\":"+(e.BasePassed?"true":"false")+",\"effective_acceptance_tolerance_percent\":"+NumberJson(e.EffectiveTolerancePercent)+
            ",\"propagated_calibration_uncertainty_percent\":"+NumberJson(e.PropagatedCalibrationUncertaintyPercent)+
            ",\"calibration_tolerance_adaptation_limit_percent\":"+NumberJson(e.CalibrationAdaptationLimitPercent)+
            ",\"cal_retention\":"+NumberJson(e.CalRetention)+",\"observed_original_amplitude_ratio\":"+NumberJson(e.ObservedRatio)+
            ",\"expected_ratio\":"+NumberJson(e.ExpectedRatio)+",\"lower_ratio_before_tolerance\":"+NumberJson(e.LowerRatio)+
            ",\"upper_ratio_before_tolerance\":"+NumberJson(e.UpperRatio)+",\"deviation_db\":"+NumberJson(e.DeviationDb)+
            ",\"uncertainty_db\":"+NumberJson(e.UncertaintyDb)+",\"excess_db\":"+NumberJson(e.ExcessDb)+
            ",\"cal_reflection_snr\":"+NumberJson(e.CalReflectionSnr)+",\"pair_reflection_snr\":"+NumberJson(e.PairReflectionSnr)+
            ",\"effective_weight\":"+NumberJson(e.EffectiveWeight)+",\"penalty_factor\":"+NumberJson(e.PenaltyFactor)+
            ",\"predicted_reflection_snr_lower_bound\":"+NumberJson(e.PredictedReflectionSnr)+
            ",\"travel_fraction\":"+NumberJson(e.TravelFraction)+
            ",\"model_travel_fraction\":"+NumberJson(e.ModelTravelFraction)+
            ",\"boundary_model_clamped\":"+(e.BoundaryModelClamped?"true":"false")+
            ",\"weight_quality_source\":"+Json(e.WeightQualitySource)+
            ",\"correlation_weight\":"+NumberJson(e.CorrelationWeight)+
            ",\"other_weight\":"+NumberJson(e.OtherWeight)+
            ",\"consistency_score\":"+NumberJson(e.ConsistencyScore)+
            ",\"measured_soft_consistency_score\":"+NumberJson(e.SoftConsistencyScore)+
            ",\"soft_consistency_policy\":\"shared_raw_CAL_LOC;within_explicit_tolerance=1;outside=tolerance/error;missing=unknown;review_only_ceiling\""+
            ",\"other_score\":"+NumberJson(e.OtherScore)+
            ",\"weighted_score_before_review_cap\":"+NumberJson(e.WeightedScore)+"}";
    }
}
