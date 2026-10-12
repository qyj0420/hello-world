using System;

namespace HVX.Task17.Location;

internal sealed class A6OutputAttenuationResult
{
    internal bool Available, Passed, BasePassed;
    internal double RequestedTolerancePercent=double.NaN,EffectiveTolerancePercent=double.NaN;
    internal double PropagatedUncertaintyPercent=double.NaN,AdaptationLimitPercent=double.NaN;
    internal double ObservedRatio=double.NaN, ExpectedRatio=double.NaN;
    internal double PredictedAmplitude=double.NaN, LowerAmplitude=double.NaN, UpperAmplitude=double.NaN;
    internal double ErrorPercent=double.NaN, Score=double.NaN;
    internal double TravelFraction=double.NaN, ModelTravelFraction=double.NaN;
    internal string Reason="invalid_input";
}

// Original-domain amplitude-only reference. The percentage is relative to the
// predicted reflection, never to the incident amplitude or charge correction.
internal static class A6OutputAttenuationMath
{
    internal static A6OutputAttenuationResult Evaluate(double incidentAmplitude,
        double reflectionAmplitude,double calRetention,double delaySeconds,
        double calTravelSeconds,double tolerancePercent,bool referenceReliable)
    {
        return EvaluateWithCalibrationQuality(incidentAmplitude,reflectionAmplitude,calRetention,delaySeconds,
            calTravelSeconds,tolerancePercent,referenceReliable,0,0);
    }

    internal static A6OutputAttenuationResult EvaluateWithCalibrationQuality(double incidentAmplitude,
        double reflectionAmplitude,double calRetention,double delaySeconds,
        double calTravelSeconds,double tolerancePercent,bool referenceReliable,
        double relativeUncertaintyPercent,double adaptationLimitPercent)
    {
        var shared=HVX.SignalEvidence.SixthConfidenceMath.Attenuation(incidentAmplitude,reflectionAmplitude,
            calRetention,delaySeconds,calTravelSeconds,tolerancePercent,referenceReliable,relativeUncertaintyPercent,adaptationLimitPercent);
        return new A6OutputAttenuationResult {Available=shared.Available,Passed=shared.Passed,BasePassed=shared.BasePassed,
            RequestedTolerancePercent=shared.RequestedTolerancePercent,EffectiveTolerancePercent=shared.EffectiveTolerancePercent,
            PropagatedUncertaintyPercent=shared.PropagatedUncertaintyPercent,AdaptationLimitPercent=shared.AdaptationLimitPercent,
            ObservedRatio=shared.ObservedRatio,ExpectedRatio=shared.ExpectedRatio,
            PredictedAmplitude=shared.PredictedAmplitude,LowerAmplitude=shared.LowerAmplitude,UpperAmplitude=shared.UpperAmplitude,
            ErrorPercent=shared.ErrorPercent,Score=shared.Score,TravelFraction=shared.TravelFraction,
            ModelTravelFraction=shared.ModelTravelFraction,Reason=shared.Reason};
    }

}
