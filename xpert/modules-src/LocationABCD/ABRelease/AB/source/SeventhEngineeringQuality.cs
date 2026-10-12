using System;

namespace HVX.Task17.Location;

/// <summary>Transparent waveform engineering quality. This is not a correctness probability or an acceptance decision.</summary>
public static class SeventhEngineeringQuality
{
    public const string Version="method7-engineering-quality-v1";
    public const string Formula="100*min(CoreCorrelation,LongCorrelation,RawCorrelation,min(IncidentSnr,ReflectionSnr)/(min(IncidentSnr,ReflectionSnr)+6))";
    private static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
    public static void ValidateMinimumPercent(double minimumPercent)
    {
        if(!Finite(minimumPercent)||minimumPercent<0||minimumPercent>100)
            throw new ArgumentOutOfRangeException("minimumPercent","Engineering quality threshold must be finite and between 0 and 100; zero disables the publishing gate.");
    }
    public static SeventhEngineeringQualityResult Evaluate(bool completeWaveformPassed,double coreCorrelation,
        double longCorrelation,double rawCorrelation,double incidentSnr,double reflectionSnr)
    {
        string reason=!completeWaveformPassed?"complete_passed_LOC_waveform_unavailable":
            !Finite(coreCorrelation)||!Finite(longCorrelation)||!Finite(rawCorrelation)||!Finite(incidentSnr)||!Finite(reflectionSnr)?
                "nonfinite_required_waveform_quality_component":
            coreCorrelation<0||coreCorrelation>1||longCorrelation<0||longCorrelation>1||rawCorrelation<0||rawCorrelation>1||incidentSnr<0||reflectionSnr<0?
                "required_waveform_quality_component_outside_domain":"";
        if(reason.Length>0)return new SeventhEngineeringQualityResult(false,double.NaN,double.NaN,"unavailable",reason,
            coreCorrelation,longCorrelation,rawCorrelation,incidentSnr,reflectionSnr);
        double snr=Math.Min(incidentSnr,reflectionSnr),noise=snr/(snr+6);
        double minimum=Math.Min(Math.Min(coreCorrelation,longCorrelation),Math.Min(rawCorrelation,noise));
        string weakest=coreCorrelation==minimum?"core_band_correlation":longCorrelation==minimum?"long_band_correlation":
            rawCorrelation==minimum?"raw_main_correlation":"minimum_packet_snr";
        return new SeventhEngineeringQualityResult(true,100*minimum,noise,weakest,"complete_LOC_weakest_component_quality_not_probability",
            coreCorrelation,longCorrelation,rawCorrelation,incidentSnr,reflectionSnr);
    }
    /// <summary>Zero disables only this post-solver publishing gate, never the physical safety or review requirements.</summary>
    public static bool PassesMinimum(SeventhEngineeringQualityResult result,double minimumPercent)
    {
        ValidateMinimumPercent(minimumPercent);
        return minimumPercent==0||(result!=null&&result.Available&&Finite(result.Percent)&&result.Percent>=minimumPercent);
    }
}

public sealed class SeventhEngineeringQualityResult
{
    public bool Available {get;private set;}
    public double Percent {get;private set;}
    public double NoiseQuality {get;private set;}
    public string WeakestComponent {get;private set;}
    public string Reason {get;private set;}
    public double CoreCorrelation {get;private set;}
    public double LongCorrelation {get;private set;}
    public double RawCorrelation {get;private set;}
    public double IncidentSnr {get;private set;}
    public double ReflectionSnr {get;private set;}
    internal SeventhEngineeringQualityResult(bool available,double percent,double noiseQuality,string weakest,string reason,
        double core,double longer,double raw,double incidentSnr,double reflectionSnr)
    {
        Available=available;Percent=percent;NoiseQuality=noiseQuality;WeakestComponent=weakest;Reason=reason;
        CoreCorrelation=core;LongCorrelation=longer;RawCorrelation=raw;IncidentSnr=incidentSnr;ReflectionSnr=reflectionSnr;
    }
}
