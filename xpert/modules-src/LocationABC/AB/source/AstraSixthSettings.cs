using System;
using System.Linq;

namespace HVX.Task17.Location;

// Per-call settings only. This type is not part of the LabVIEW public API.
internal sealed class A6Settings
{
    internal bool AutomaticCorrelation,UseAttenuation,UseWidth;
    internal bool AutomaticWeights,AutomaticAttenuationTolerance,AutomaticWidthTolerance;
    internal double MinimumCorrelationPercent,AttenuationErrorPercent,PulseWidthErrorPercent;
    internal double[] Weights,RequestedWeights;
    internal const double MinimumOverallScore=70.0;
    internal const double AutomaticCorrelationReferenceMargin=0.15;
    internal const double MinimumUsableCalibrationCorrelation=0.15;

    internal static A6Settings Create(double minimumCorrelationPercent,double attenuationErrorPercent,
        double pulseWidthErrorPercent,double[] scoreWeights,bool useAttenuationReference,bool usePulseWidthComparison)
    {
        if(double.IsInfinity(minimumCorrelationPercent))throw new ArgumentException("Correlation threshold must be finite or -1/NaN for automatic calibration reference.");
        var s=new A6Settings {AutomaticCorrelation=minimumCorrelationPercent==-1||double.IsNaN(minimumCorrelationPercent),
            MinimumCorrelationPercent=A6EvidenceMath.NormalizePercent(minimumCorrelationPercent,70),
            AttenuationErrorPercent=A6EvidenceMath.NormalizePercent(attenuationErrorPercent,60),
            PulseWidthErrorPercent=A6EvidenceMath.NormalizePercent(pulseWidthErrorPercent,60),
            UseAttenuation=useAttenuationReference,UseWidth=usePulseWidthComparison,
            AutomaticWeights=scoreWeights==null||scoreWeights.Length==0,
            AutomaticAttenuationTolerance=attenuationErrorPercent==-1||double.IsNaN(attenuationErrorPercent),
            AutomaticWidthTolerance=pulseWidthErrorPercent==-1||double.IsNaN(pulseWidthErrorPercent)};
        double[] w=scoreWeights==null||scoreWeights.Length==0?new[]{50.0,35.0,10.0,5.0}:(double[])scoreWeights.Clone();
        if(w.Length!=4||w.Any(x=>double.IsNaN(x)||double.IsInfinity(x)||x<0)||w[0]<=0)
            throw new ArgumentException("scoreWeights requires four finite nonnegative values [correlation,attenuation,width,lobeEnergy], with positive correlation and positive sum; empty enables calibration-quality policy based on [50,35,10,5].");
        double sum=w.Sum();
        if(double.IsNaN(sum)||double.IsInfinity(sum)||sum<=0)
            throw new ArgumentException("scoreWeights sum must be finite and positive; reduce the magnitude of the four weights.");
        s.Weights=w.Select(x=>x/sum).ToArray();
        if(s.Weights[0]<=0)
            throw new ArgumentException("Normalized correlation weight must remain positive; reduce the dynamic range of scoreWeights.");
        s.RequestedWeights=(double[])s.Weights.Clone();
        return s;
    }
}
