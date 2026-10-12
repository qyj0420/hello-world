using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private A6Settings a6OutputSettings;
    private double a6OutputGlobalCalRetention=double.NaN;
    private bool a6OutputGlobalCalMeasured;
    private sealed class A6OutputBandAmplitude
    {
        internal double Amplitude=double.NaN;
        internal bool Guarded;
        internal string Reason="unavailable";
    }
    private sealed class A6OutputReview
    {
        internal A6OutputAttenuationResult Attenuation;
        internal bool AttenuationEnabled,GuardsValid,Passed;
        internal double Score,CorrelationFloor,ObservedRatio=double.NaN;
        internal double WidthScore=double.NaN,EnergyScore=double.NaN;
        internal double IncidentAmplitude=double.NaN,ReflectionAmplitude=double.NaN;
        internal double CalibrationRetention=double.NaN,ScoreCeiling=1;
        internal double[] Factors,Weights;
        internal bool[] Enabled;
        internal string MeasurementReason,Reason;
    }

    // Output review never writes a valid guard into the matching caches. A
    // missing guard can still support a measured review candidate, not acceptance.
    private A6OutputBandAmplitude A6OutputAmplitude(A4Context c,A4Pulse p,bool forceGlobal=false)
    {
        var result=new A6OutputBandAmplitude();
        if(c==null||p==null)return result;
        var guarded=A6ScoringMeasurement(c,p);
        if(!forceGlobal&&guarded!=null&&guarded.Valid&&Finite(guarded.RawAmplitude)&&guarded.RawAmplitude>0)
        {
            result.Amplitude=guarded.RawAmplitude;result.Guarded=true;
            result.Reason="guarded_original_domain_packet_amplitude";return result;
        }
        var band=c.Raw;
        if(band==null||p.Start<0||p.End>=band.Length||p.End<=p.Start||p.Clipped||p.UnresolvedOverlap)return result;
        double amplitude=0;
        // CAL and LOC use the same original amplitude domain. The fallback uses the complete frozen support,
        // not a favourable lobe or the end of the display window.
        for(int k=p.Start;k<=p.End;k++)
        {
            if(!Finite(band[k]))return result;
            double value=band[k]-A4RawBaselineAt(c,k);
            if(!Finite(value))return result;
            amplitude=Math.Max(amplitude,Math.Abs(value));
        }
        if(amplitude<=0)return result;
        result.Amplitude=amplitude;
        result.Reason="unguarded_original_domain_complete_frozen_support_review_only";
        return result;
    }

    private double A6GlobalCalibrationRetention(A4Context c)
    {
        if(a6OutputGlobalCalMeasured)return a6OutputGlobalCalRetention;
        a6OutputGlobalCalMeasured=true;
        var reference=c.AttenuationScoringReference;
        if(reference==null||!reference.Valid)return double.NaN;
        a6OutputGlobalCalRetention=reference.RawRetention;
        return a6OutputGlobalCalRetention;
    }

    private A6OutputReview A6ReviewOutput(A4Context c,A4Pulse a,A4Pulse b,int peakDelay,double signedRho)
    {
        var settings=a6OutputSettings;
        if(c==null||c.Algorithm!=5||settings==null)return null;
        var result=new A6OutputReview {AttenuationEnabled=settings.UseAttenuation};
        var ma=A6OutputAmplitude(c,a);var mb=A6OutputAmplitude(c,b);
        if(!ma.Guarded||!mb.Guarded)
        {
            // Use one baseline/support convention on BOTH sides. The CAL is
            // measured with this same global-band convention below as well.
            ma=A6OutputAmplitude(c,a,true);mb=A6OutputAmplitude(c,b,true);
        }
        result.IncidentAmplitude=ma.Amplitude;result.ReflectionAmplitude=mb.Amplitude;
        result.GuardsValid=ma.Guarded&&mb.Guarded;
        result.MeasurementReason=ma.Reason+";"+mb.Reason;
        var reference=c.AttenuationScoringReference;
        result.CalibrationRetention=result.GuardsValid?(reference==null?double.NaN:reference.RawRetention):A6GlobalCalibrationRetention(c);
        result.Attenuation=A6OutputAttenuationMath.EvaluateWithCalibrationQuality(ma.Amplitude,mb.Amplitude,
            result.CalibrationRetention,peakDelay/c.Fs,
            reference==null?double.NaN:reference.TravelSeconds,settings.AttenuationErrorPercent,
            a6ReferenceReliable&&reference!=null&&reference.Valid,
            reference==null||reference.Quality==null?double.NaN:reference.Quality.RelativeUncertaintyPercent,
            c.CalibrationToleranceAdaptationLimitPercent);
        if(Finite(ma.Amplitude)&&ma.Amplitude>0&&Finite(mb.Amplitude)&&mb.Amplitude>0)
            result.ObservedRatio=mb.Amplitude/ma.Amplitude;
        var native=a6Evidence.Where(k=>Object.ReferenceEquals(k.Key.Incident,a)&&
            Object.ReferenceEquals(k.Key.Reflection,b)).Select(k=>k.Value).FirstOrDefault();
        double fraction=Finite(result.Attenuation.ModelTravelFraction)?result.Attenuation.ModelTravelFraction:
            Clamp(peakDelay/c.RoundTripExact,0,1);
        var width=new A6ToleranceResult();
        if(native!=null)
        {
            double widthReference=a6CalibrationWidthValid?1+(a6CalibrationWidthRatio-1)*fraction:1;
            width=A6EvidenceMath.RelativeError(native.WidthA>0?native.WidthB/native.WidthA:double.NaN,
                widthReference,settings.PulseWidthErrorPercent);
            result.EnergyScore=native.EnergyScore;
        }
        result.WidthScore=width.Score;
        var correlation=A4UnifiedCorrelation(c,a,b,peakDelay);
        result.CorrelationFloor=settings.AutomaticCorrelation?correlation.Floor:settings.MinimumCorrelationPercent/100;
        double rhoReference=settings.AutomaticCorrelation?correlation.Reference:1;
        double correlationScore=Finite(signedRho)&&Finite(rhoReference)&&rhoReference>0?
            Clamp(signedRho/rhoReference,0,1):double.NaN;
        double[] values={correlationScore,result.Attenuation.Score,result.WidthScore,result.EnergyScore};
        bool[] enabled={true,settings.UseAttenuation,settings.UseWidth,true};
        result.Factors=values;result.Enabled=enabled;result.Weights=settings.Weights;
        result.Score=HVX.SignalEvidence.SixthConfidenceMath.WeightedScore(values,settings.Weights,enabled);
        result.ScoreCeiling=HVX.SignalEvidence.SixthConfidenceMath.AttenuationCeiling(settings.UseAttenuation,
            result.Attenuation.Available,result.Attenuation.BasePassed,result.Attenuation.Score);
        result.Score=Math.Min(result.Score,result.ScoreCeiling);
        if(!Finite(signedRho)||!Finite(ma.Amplitude)||!Finite(mb.Amplitude)||
            settings.UseWidth&&!width.Available)result.Score=double.NaN;
        double finalPosition=A4BoundaryOutputPosition(peakDelay,c.Fs,c.Joints,c.Velocities,c.Length);
        bool withinBoundary=HVX.SignalEvidence.BoundaryConfidenceMath.WithinTolerance(finalPosition,c.Length);
        if(signedRho<=0)result.Score=0;
        else if(peakDelay<=0||Finite(finalPosition)&&!withinBoundary)
            result.Score=Finite(result.Score)?0:double.NaN;
        if(!Finite(finalPosition))result.Score=double.NaN;
        var reasons=new List<string>();
        if(!Finite(signedRho)||!Finite(result.CorrelationFloor)||signedRho+1e-12<result.CorrelationFloor||signedRho<=0)
            reasons.Add("final_signed_correlation_below_floor");
        if(settings.UseAttenuation&&(!result.Attenuation.Available||!result.Attenuation.Passed))
            reasons.Add("final_attenuation_"+result.Attenuation.Reason);
        if(settings.UseWidth&&(!width.Available||!width.Passed))reasons.Add("final_width_"+width.Reason);
        if(!result.GuardsValid)reasons.Add("final_measurement_is_review_only_not_guarded");
        if(peakDelay<=0||!withinBoundary)reasons.Add("final_peak_delay_outside_cable_boundary_tolerance");
        if(!Finite(result.Score))reasons.Add("final_engineering_score_unavailable_missing_required_evidence");
        else if(100*result.Score+1e-9<A6Settings.MinimumOverallScore)reasons.Add("final_engineering_score_below_70");
        result.Passed=reasons.Count==0;
        result.Reason=result.Passed?"final_raw_attenuation_same_band_correlation_and_requested_limits_passed":String.Join(";",reasons);
        return result;
    }

    private static string A6OutputReviewJson(A6OutputReview e)
    {
        if(e==null)return "null";
        var a=e.Attenuation;
        return "{\"attenuation_enabled\":"+(e.AttenuationEnabled?"true":"false")+
            ",\"measurement_domain\":\"original_baseline_corrected_complete_packet_amplitude\""+
            ",\"measurement_reason\":"+Json(e.MeasurementReason)+",\"guarded\":"+(e.GuardsValid?"true":"false")+
            ",\"available\":"+(a.Available?"true":"false")+",\"within_attenuation_tolerance\":"+(a.Passed?"true":"false")+
            ",\"within_base_attenuation_tolerance\":"+(a.BasePassed?"true":"false")+
            ",\"base_tolerance_percent\":"+NumberJson(a.RequestedTolerancePercent)+",\"effective_tolerance_percent\":"+NumberJson(a.EffectiveTolerancePercent)+
            ",\"propagated_calibration_uncertainty_percent\":"+NumberJson(a.PropagatedUncertaintyPercent)+",\"calibration_adaptation_limit_percent\":"+NumberJson(a.AdaptationLimitPercent)+
            ",\"observed_ratio\":"+NumberJson(e.ObservedRatio)+",\"expected_ratio\":"+NumberJson(a.ExpectedRatio)+
            ",\"incident_amplitude\":"+NumberJson(e.IncidentAmplitude)+",\"reflection_amplitude\":"+NumberJson(e.ReflectionAmplitude)+
            ",\"calibration_retention_used\":"+NumberJson(e.CalibrationRetention)+
            ",\"predicted_reflection_amplitude\":"+NumberJson(a.PredictedAmplitude)+
            ",\"lower_reflection_amplitude\":"+NumberJson(a.LowerAmplitude)+",\"upper_reflection_amplitude\":"+NumberJson(a.UpperAmplitude)+
            ",\"relative_error_percent\":"+NumberJson(a.ErrorPercent)+",\"attenuation_score\":"+NumberJson(a.Score)+
            ",\"travel_fraction\":"+NumberJson(a.TravelFraction)+",\"model_travel_fraction\":"+NumberJson(a.ModelTravelFraction)+
            ",\"correlation_floor\":"+NumberJson(e.CorrelationFloor)+",\"weighted_score\":"+NumberJson(e.Score)+
            ",\"attenuation_failure_score_ceiling\":"+NumberJson(e.ScoreCeiling)+
            ",\"factors\":"+NumberArrayJson(e.Factors)+",\"configured_weights\":"+NumberArrayJson(e.Weights)+
            ",\"enabled_factors\":["+String.Join(",",e.Enabled.Select(v=>v?"true":"false"))+"]"+
            ",\"passed\":"+(e.Passed?"true":"false")+",\"reason\":"+Json(e.Reason)+"}";
    }
}
