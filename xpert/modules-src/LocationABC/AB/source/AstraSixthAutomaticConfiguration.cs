using System;
using System.Linq;

namespace HVX.Task17.Location;

// Method 6 only. This is an explicit engineering policy, not a fitted probability
// model or a statistical confidence interval. No current LOC candidate scores,
// positions or pass rate enter calibration configuration.
internal sealed class A6AutomaticConfiguration
{
    internal double[] Weights;
    internal double AttenuationTolerance,WidthTolerance,NoiseAllowancePercent,TimingAllowancePercent;
    internal bool ReferenceUsed,AttenuationCapped,WidthCapped;
    internal string Basis;

    internal static A6AutomaticConfiguration Resolve(A6Settings s,bool reliable,double incidentSnr,
        double reflectionSnr,bool shapeValid,double shapeLower,double shapeUpper,
        bool widthValid,double widthASeconds,double widthBSeconds,double fs)
    {
        var r=new A6AutomaticConfiguration {Weights=(double[])s.Weights.Clone(),
            AttenuationTolerance=s.AttenuationErrorPercent,WidthTolerance=s.PulseWidthErrorPercent,
            Basis="explicit_manual_values_or_calibration_unavailable_engineering_defaults"};
        bool usable=reliable&&Finite(incidentSnr)&&Finite(reflectionSnr)&&incidentSnr>=6&&reflectionSnr>=6;
        if(!usable)return r; // Do not disable a requested missing CAL gate or reward missing evidence.
        r.ReferenceUsed=true;r.Basis="single_cal_quality_bounded_engineering_policy_not_noise_confidence_interval";
        double inverseA=1/incidentSnr,inverseB=1/reflectionSnr;
        // Conservative engineering noise sensitivity of a ratio. The 60 percent
        // spectrum-mismatch floor is a declared policy, NOT learned from one CAL.
        r.NoiseAllowancePercent=300*Math.Sqrt(inverseA*inverseA+inverseB*inverseB);
        if(s.AutomaticAttenuationTolerance)
        {
            r.AttenuationCapped=r.NoiseAllowancePercent>100;
            r.AttenuationTolerance=Math.Min(100,Math.Max(60,r.NoiseAllowancePercent));
        }
        bool resolvedWidth=widthValid&&Finite(widthASeconds)&&Finite(widthBSeconds)&&
            widthASeconds>0&&widthBSeconds>0&&Finite(fs)&&fs>0;
        if(resolvedWidth)
            r.TimingAllowancePercent=100*(2/(fs*widthASeconds)+2/(fs*widthBSeconds));
        if(s.AutomaticWidthTolerance&&resolvedWidth)
        {
            double allowance=r.NoiseAllowancePercent+r.TimingAllowancePercent;
            r.WidthCapped=allowance>100;
            r.WidthTolerance=Math.Min(100,Math.Max(60,allowance));
        }
        if(s.AutomaticWeights)
        {
            // Quality depends on measured SNR / alignment sensitivity, not on
            // attenuation itself: a highly attenuated but clean CAL keeps weight.
            double amplitudeQuality=Math.Max(.5,Math.Min(1,Math.Min(incidentSnr,reflectionSnr)/12));
            double shapeQuality=shapeValid&&Finite(shapeLower)&&Finite(shapeUpper)&&shapeUpper>=shapeLower
                ?Math.Max(.5,Math.Min(1,1-(shapeUpper-shapeLower))):1;
            double[] w={50*shapeQuality,35*amplitudeQuality,10*(resolvedWidth?amplitudeQuality:1),5};
            double sum=w.Sum();r.Weights=w.Select(v=>v/sum).ToArray();
        }
        return r;
    }
    private static bool Finite(double x){return !double.IsNaN(x)&&!double.IsInfinity(x);}
}

internal sealed partial class LocationEngine
{
    private string a6AutomaticConfigurationJson="{}";

    private void A6ApplyAutomaticCalibrationSettings(A4Context c,A6Settings settings,
        A4AttenuationScoringReference reference,A6Correlation correlation,double widthRatio,
        bool reliable,bool widthValid)
    {
        double widthA=double.NaN,widthB=double.NaN;
        if(widthValid&&c.Calibration!=null&&reference.Incident!=null&&reference.Reflection!=null)
        {
            double[] filtered=A4LowPassThreeMHz(c.Calibration.Samples,c.Calibration.SampleRate);
            var a=A6MeasureWidth(filtered,reference.Incident,c.Calibration.SampleRate);
            var b=A6MeasureWidth(filtered,reference.Reflection,c.Calibration.SampleRate);
            if(a.Valid&&b.Valid){widthA=a.WidthNs*1e-9;widthB=b.WidthNs*1e-9;}
        }
        var r=A6AutomaticConfiguration.Resolve(settings,reliable,reference.IncidentSnr,reference.ReflectionSnr,
            correlation.Valid&&correlation.Fixed>=A6Settings.MinimumUsableCalibrationCorrelation,
            correlation.Lower,correlation.Upper,widthValid,widthA,widthB,c.Calibration==null?double.NaN:c.Calibration.SampleRate);
        settings.Weights=r.Weights;
        settings.AttenuationErrorPercent=r.AttenuationTolerance;
        settings.PulseWidthErrorPercent=r.WidthTolerance;
        a6AutomaticConfigurationJson="{\"scope\":\"method6_only\",\"automatic_weights\":"+(settings.AutomaticWeights?"true":"false")+
            ",\"automatic_attenuation_tolerance\":"+(settings.AutomaticAttenuationTolerance?"true":"false")+
            ",\"automatic_width_tolerance\":"+(settings.AutomaticWidthTolerance?"true":"false")+
            ",\"basis\":"+Json(r.Basis)+",\"reference_used\":"+(r.ReferenceUsed?"true":"false")+
            ",\"configured_weights\":"+NumberArrayJson(r.Weights)+
            ",\"attenuation_error_percent\":"+NumberJson(r.AttenuationTolerance)+
            ",\"pulse_width_error_percent\":"+NumberJson(r.WidthTolerance)+
            ",\"declared_engineering_error_floor_percent\":60,\"snr_weight_reference\":12,"+
            "\"noise_ratio_sensitivity_percent\":"+NumberJson(r.NoiseAllowancePercent)+
            ",\"width_sampling_allowance_percent\":"+NumberJson(r.TimingAllowancePercent)+
            ",\"attenuation_tolerance_capped\":"+(r.AttenuationCapped?"true":"false")+
            ",\"width_tolerance_capped\":"+(r.WidthCapped?"true":"false")+
            ",\"uses_current_candidate_scores_or_positions\":false,\"manual_values_preserved\":true,"+
            "\"learned_optimal_weights\":false,\"statistical_confidence_interval\":false}";
    }
}
