using System;
using System.IO;
using System.Linq;

namespace HVX.Task17.Location;

internal static class AnalysisPhaseQuadrantPolicy
{
    internal static bool[] Copy(bool[] value)
    {
        if(value==null||value.Length!=4)throw new ArgumentException("AnalysisPhaseQuadrants must contain exactly four Boolean values.","AnalysisPhaseQuadrants");
        return (bool[])value.Clone();
    }
    internal static bool Eligible(bool[] quadrants,double initialDegrees,double frequency,double sampleRate,int sample)
    {
        if(quadrants==null||quadrants.All(v=>v))return true;
        if(!quadrants.Any(v=>v))return false;
        if(sample<0||Double.IsNaN(frequency)||Double.IsInfinity(frequency)||frequency<=0||
            Double.IsNaN(sampleRate)||Double.IsInfinity(sampleRate)||sampleRate<=0)return false;
        double degrees=(initialDegrees+360*frequency*sample/sampleRate)%360;
        if(Double.IsNaN(degrees)||Double.IsInfinity(degrees))return false;
        if(degrees<0)degrees+=360;
        return quadrants[Math.Min(3,(int)(degrees/90))];
    }
}

internal sealed partial class LocationEngine
{
    internal bool[] AnalysisPhaseQuadrants=new[]{true,true,true,true};
    private void A4ConfigureAnalysisPhase(A4Context c,double initialDegrees,double frequency)
    {
        c.AnalysisPhaseQuadrants=AnalysisPhaseQuadrantPolicy.Copy(AnalysisPhaseQuadrants);
        c.AnalysisInitialPhaseDegrees=initialDegrees;c.AnalysisFrequencyHz=frequency;
        if(c.AnalysisPhaseQuadrants.Any(v=>v)&&!c.AnalysisPhaseQuadrants.All(v=>v)&&(!Finite(frequency)||frequency<=0))
            throw new InvalidDataException("A partial analysis phase-quadrant selection requires a positive oscillation frequency from the caller or matched measurement metadata; unknown phase is not assumed to be zero.");
    }
    private static bool A4AnalysisPhaseEligible(A4Context c,int canonicalPeak)
    {return AnalysisPhaseQuadrantPolicy.Eligible(c.AnalysisPhaseQuadrants,c.AnalysisInitialPhaseDegrees,c.AnalysisFrequencyHz,c.Fs,canonicalPeak);}
    private static string A4AnalysisPhaseJson(A4Context c)
    {return "{\"enabled_quadrants\":["+String.Join(",",c.AnalysisPhaseQuadrants.Select(v=>v?"true":"false"))+
        "],\"boundaries_degrees\":[0,90,180,270,360],\"intervals\":\"left_closed_right_open\",\"anchor\":\"original_canonical_incident_peak\",\"before_analysis_count_limit\":true}";}
}
