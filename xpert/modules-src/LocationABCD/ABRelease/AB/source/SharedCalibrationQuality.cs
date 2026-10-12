using System;

namespace HVX.SignalEvidence
{
    // Engineering measurement uncertainty, not a calibrated probability.
    // A weak reflection is not poor quality when its own signal/noise is good.
    internal sealed class CalibrationQualityInput
    {
        internal bool Identified,IndependentCompletePackets,Unclipped;
        internal double IncidentAmplitude=double.NaN,ReflectionAmplitude=double.NaN;
        internal double IncidentNoise=double.NaN,ReflectionNoise=double.NaN;
    }
    internal sealed class CalibrationQualityResult
    {
        internal bool Recognized;
        internal double Retention=double.NaN,IncidentSnr=double.NaN,ReflectionSnr=double.NaN;
        internal double RelativeUncertaintyPercent=double.NaN;
        internal string Reason="calibration_not_identified";
    }
    internal static class CalibrationQualityMath
    {
        internal const string Version="2026.09.22-calibration-recognition-uncertainty1";
        internal const double DefaultAdaptationLimitPercent=30;
        static bool Finite(double value){return ConfidenceMath.Finite(value);}
        internal static CalibrationQualityResult Evaluate(CalibrationQualityInput x)
        {
            if(x==null)throw new ArgumentNullException("x");
            var r=new CalibrationQualityResult();
            if(!x.Identified)return r;
            if(!x.IndependentCompletePackets){r.Reason="calibration_independent_complete_guards_unavailable";return r;}
            if(!x.Unclipped){r.Reason="calibration_clipping_or_peak_resolution_unavailable";return r;}
            if(!Finite(x.IncidentAmplitude)||x.IncidentAmplitude<=0||!Finite(x.ReflectionAmplitude)||x.ReflectionAmplitude<=0||
                !Finite(x.IncidentNoise)||x.IncidentNoise<0||!Finite(x.ReflectionNoise)||x.ReflectionNoise<0)
            {r.Reason="calibration_amplitude_or_background_invalid";return r;}
            r.Retention=x.ReflectionAmplitude/x.IncidentAmplitude;
            r.IncidentSnr=x.IncidentNoise==0?double.PositiveInfinity:x.IncidentAmplitude/x.IncidentNoise;
            r.ReflectionSnr=x.ReflectionNoise==0?double.PositiveInfinity:x.ReflectionAmplitude/x.ReflectionNoise;
            if(!Finite(r.Retention)||r.Retention<=0){r.Reason="calibration_ratio_not_representable";return r;}
            // No relative-amplitude floor. Recognizability is tested in the raw
            // amplitude domain against each packet's own guarded noise.
            if(x.IncidentAmplitude<6*x.IncidentNoise||x.ReflectionAmplitude<6*x.ReflectionNoise)
            {r.Reason="calibration_peak_snr_below_six_sigma";return r;}
            double a=x.IncidentNoise/x.IncidentAmplitude,b=x.ReflectionNoise/x.ReflectionAmplitude;
            r.RelativeUncertaintyPercent=100*Math.Sqrt(a*a+b*b);
            if(!Finite(r.RelativeUncertaintyPercent)){r.Reason="calibration_uncertainty_not_representable";return r;}
            r.Recognized=true;r.Reason="raw_calibration_identified_with_guarded_six_sigma_evidence";
            return r;
        }
        internal static bool ResolveTolerance(double requested,double cap,double relativeUncertaintyPercent,double propagationFraction,
            out double effective,out double propagated)
        {
            effective=propagated=double.NaN;
            if(!Finite(requested)||requested<0||requested>100||!Finite(cap)||cap<0||cap>100||
                !Finite(relativeUncertaintyPercent)||relativeUncertaintyPercent<0||!Finite(propagationFraction)||propagationFraction<0)return false;
            propagated=propagationFraction*relativeUncertaintyPercent;
            if(!Finite(propagated))return false;
            effective=Math.Max(requested,Math.Min(cap,propagated));
            return true;
        }
    }
}
