using System;

// Compiled unchanged into Task17 and PDmap. Scores are engineering consistency
// measures, NOT calibrated probabilities. Measurement and provenance stay with
// the caller; a missing requested factor is never silently reweighted away.
namespace HVX.SignalEvidence
{
    internal sealed class ToleranceEvidence
    {
        internal bool Available, Passed;
        internal double ErrorPercent=double.NaN, Score=double.NaN;
        internal string Reason="invalid_input";
    }
    internal sealed class AttenuationEvidence
    {
        internal bool Available, Passed;
        internal bool BasePassed;
        internal double RequestedTolerancePercent=double.NaN,EffectiveTolerancePercent=double.NaN;
        internal double PropagatedUncertaintyPercent=double.NaN,AdaptationLimitPercent=double.NaN;
        internal double ObservedRatio=double.NaN, ExpectedRatio=double.NaN;
        internal double PredictedAmplitude=double.NaN, LowerAmplitude=double.NaN, UpperAmplitude=double.NaN;
        internal double ErrorPercent=double.NaN, Score=double.NaN;
        internal double TravelFraction=double.NaN, ModelTravelFraction=double.NaN;
        internal string Reason="invalid_input";
    }
    internal static class ConfidenceMath
    {
        internal const string Version="2026.09.21-shared1";
        internal static bool Finite(double v){return !double.IsNaN(v)&&!double.IsInfinity(v);}
        internal static double Unit(double v){return Math.Max(0,Math.Min(1,v));}
        internal static ToleranceEvidence RelativeError(double observed,double reference,double tolerancePercent)
        {
            var r=new ToleranceEvidence();
            if(!Finite(observed)||observed<=0){r.Reason="observed_missing_nonfinite_or_nonpositive";return r;}
            if(!Finite(reference)||reference<=0){r.Reason="reference_missing_nonfinite_or_nonpositive";return r;}
            if(!Finite(tolerancePercent)||tolerancePercent<0||tolerancePercent>100)
            {r.Reason="tolerance_outside_0_100_percent";return r;}
            double ratio=observed/reference,error=100*Math.Abs(ratio-1);
            if(!Finite(error)){r.Reason="relative_error_not_representable";return r;}
            r.Available=true;r.ErrorPercent=error;
            if(tolerancePercent==0){r.Passed=error<=1e-9;r.Score=r.Passed?1:0;}
            else
            {
                double f=tolerancePercent/100;
                r.Passed=ratio>=1-f&&ratio<=1+f;
                double z=error/tolerancePercent;r.Score=Math.Exp(-.5*z*z);
            }
            r.Reason=r.Passed?"within_relative_error_tolerance":"outside_relative_error_tolerance";
            return r;
        }
        internal static AttenuationEvidence Attenuation(double incidentAmplitude,double reflectionAmplitude,
            double calRetention,double delaySeconds,double calTravelSeconds,double tolerancePercent,bool referenceReliable)
        {
            var r=new AttenuationEvidence();
            if(!referenceReliable){r.Reason="calibration_reference_unreliable";return r;}
            if(!Finite(incidentAmplitude)||incidentAmplitude<=0||!Finite(reflectionAmplitude)||reflectionAmplitude<=0)
            {r.Reason="amplitude_missing_nonfinite_or_nonpositive";return r;}
            if(!Finite(calRetention)||calRetention<=0||calRetention>1.10)
            {r.Reason="calibration_retention_outside_0_to_1point10";return r;}
            if(!Finite(delaySeconds)||delaySeconds<0||!Finite(calTravelSeconds)||calTravelSeconds<=0)
            {r.Reason="travel_time_missing_nonfinite_or_invalid";return r;}
            if(!Finite(tolerancePercent)||tolerancePercent<0||tolerancePercent>100)
            {r.Reason="tolerance_outside_0_100_percent";return r;}
            r.TravelFraction=delaySeconds/calTravelSeconds;
            if(!Finite(r.TravelFraction)){r.Reason="travel_fraction_not_representable";return r;}
            r.ModelTravelFraction=Math.Min(r.TravelFraction,1);
            r.ObservedRatio=reflectionAmplitude/incidentAmplitude;
            r.ExpectedRatio=Math.Pow(Math.Min(1,calRetention),r.ModelTravelFraction);
            r.PredictedAmplitude=incidentAmplitude*r.ExpectedRatio;
            double fraction=tolerancePercent/100;
            r.LowerAmplitude=r.PredictedAmplitude*(1-fraction);r.UpperAmplitude=r.PredictedAmplitude*(1+fraction);
            if(!Finite(r.ObservedRatio)||r.ObservedRatio<=0||!Finite(r.ExpectedRatio)||r.ExpectedRatio<=0||
                !Finite(r.PredictedAmplitude)||r.PredictedAmplitude<=0||!Finite(r.LowerAmplitude)||!Finite(r.UpperAmplitude))
            {r.Reason="predicted_amplitude_or_ratio_not_representable";return r;}
            var t=RelativeError(reflectionAmplitude,r.PredictedAmplitude,tolerancePercent);
            if(!t.Available){r.Reason=t.Reason;return r;}
            r.Available=true;r.ErrorPercent=t.ErrorPercent;r.Score=t.Score;
            r.Passed=tolerancePercent==0?t.Passed:reflectionAmplitude>=r.LowerAmplitude&&reflectionAmplitude<=r.UpperAmplitude;
            r.Reason=r.Passed?"within_predicted_reflection_relative_error_tolerance":"outside_predicted_reflection_relative_error_tolerance";
            return r;
        }
        internal static double WeightedScore(double[] values,double[] weights,bool[] enabled,double outputScale=1)
        {
            if(values==null||weights==null||enabled==null||values.Length!=weights.Length||values.Length!=enabled.Length)
                throw new ArgumentException("Evidence arrays must have equal lengths.");
            if(!Finite(outputScale)||outputScale<=0)throw new ArgumentException("Evidence output scale must be finite and positive.");
            double denominator=0,numerator=0;
            for(int k=0;k<values.Length;k++)if(enabled[k])
            {
                if(!Finite(weights[k])||weights[k]<0)throw new ArgumentException("Evidence weights must be finite and nonnegative.");
                denominator+=weights[k];if(Finite(values[k]))numerator+=weights[k]*Unit(values[k]);
            }
            if(!Finite(denominator)||!Finite(numerator))throw new ArgumentException("Evidence weight sum overflow.");
            // Preserve the original Task17 percent-domain order of operations.
            // 100*numerator/denominator is not bit-identical to 100*(numerator/denominator).
            double scaled=outputScale*numerator;
            if(!Finite(scaled))throw new ArgumentException("Scaled evidence numerator overflow.");
            return denominator>0?Math.Max(0,Math.Min(outputScale,scaled/denominator)):0;
        }
        internal static double AttenuationCeiling(bool enabled,bool available,bool passed,double score,double outputScale=1)
        {
            // Preserve the existing Task17 conflict ceiling. Missing CAL is
            // handled separately by the caller's evidence-availability gate.
            if(!Finite(outputScale)||outputScale<=0)throw new ArgumentException("Evidence output scale must be finite and positive.");
            return enabled&&available&&!passed?(.49*outputScale)*Unit(score):outputScale;
        }
    }
}
