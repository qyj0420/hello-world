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
        return EligibleDegrees(quadrants,degrees);
    }
    internal static bool EligibleDegrees(bool[] quadrants,double degrees)
    {
        if(quadrants==null||quadrants.All(v=>v))return true;
        if(!quadrants.Any(v=>v))return false;
        if(Double.IsNaN(degrees)||Double.IsInfinity(degrees))return false;
        degrees%=360;
        if(degrees<0)degrees+=360;
        return quadrants[Math.Min(3,(int)(degrees/90))];
    }
}

internal sealed partial class LocationEngine
{
    internal bool[] AnalysisPhaseQuadrants=new[]{true,true,true,true};
    private static void A4ValidateCycleSampleRate(Xiongxin.OWTS.Data.LocationCycleReference reference,double sampleRate)
    {
        if(reference!=null&&(!Finite(sampleRate)||sampleRate<=0||Math.Abs(reference.SampleRateHz-sampleRate)>1e-9*sampleRate))
            throw new InvalidDataException("LOC cycle reference does not match the resolved analysis sample rate; fixed-frequency fallback is prohibited.");
    }
    private void A4ConfigureAnalysisPhase(A4Context c,double initialDegrees,double frequency,Xiongxin.OWTS.Data.LocationCycleReference reference=null)
    {
        A4ValidateCycleSampleRate(reference,c.Fs);
#if HYBRID_CORE
        c.BindHybridCycle(reference,c.Fs);
#endif
        c.CycleReference=reference;
#if HYBRID_CORE
        if(c.HybridCycleSession!=null){initialDegrees=c.HybridCycleSession.InitialPhaseDegrees;frequency=c.HybridCycleSession.AverageFrequencyHz;}
#else
        if(reference!=null){initialDegrees=reference.InitialPhaseDegrees;frequency=reference.AverageFrequencyHz;}
#endif
        c.AnalysisPhaseQuadrants=AnalysisPhaseQuadrantPolicy.Copy(AnalysisPhaseQuadrants);
        c.AnalysisInitialPhaseDegrees=initialDegrees;c.AnalysisFrequencyHz=frequency;
        if(c.AnalysisPhaseQuadrants.Any(v=>v)&&!c.AnalysisPhaseQuadrants.All(v=>v)&&(!Finite(frequency)||frequency<=0))
            throw new InvalidDataException("A partial analysis phase-quadrant selection requires a positive oscillation frequency from the caller or matched measurement metadata; unknown phase is not assumed to be zero.");
    }
    private static double A4PhaseAt(A4Context c,double sample)
    {
#if HYBRID_CORE
        double cached;
        if(c.HybridPhaseCache!=null&&c.HybridPhaseCache.TryGetValue(sample,out cached))return cached;
        if(c.HybridCycleSession!=null)return c.HybridCycleSession.PhaseAt(sample);
        if(c.CycleReference!=null)throw new InvalidDataException("The LOC analysis context has no bound immutable phase session.");
#endif
        if(c.CycleReference!=null)return c.CycleReference.PhaseAt(sample);
        return Finite(c.AnalysisFrequencyHz)&&c.AnalysisFrequencyHz>0?
            NormalizeDegrees(c.AnalysisInitialPhaseDegrees+360*c.AnalysisFrequencyHz*sample/c.Fs):NormalizeDegrees(c.AnalysisInitialPhaseDegrees);
    }
#if HYBRID_CORE
    private static void A4PrepareCyclePhases(A4Context c,System.Collections.Generic.IEnumerable<double> samples)
    {
        if(c.CycleReference==null)return;
        if(c.HybridCycleSession==null)throw new InvalidDataException("The LOC batch context has no bound immutable phase session.");
        // Cache candidates only; never materialize a full waveform phase table.
        var indices=samples.Distinct().Take(4096).ToArray();
        var phases=c.HybridCycleSession.PhasesAt(indices);
        var cache=new System.Collections.Generic.Dictionary<double,double>(indices.Length);
        for(int i=0;i<indices.Length;i++)cache.Add(indices[i],phases[i]);
        c.HybridPhaseCache=cache;
    }
#endif
    private static bool A4AnalysisPhaseEligible(A4Context c,int canonicalPeak)
    {
        // Keep the original all/none truth table before evaluating a phase.
        if(c.AnalysisPhaseQuadrants==null||c.AnalysisPhaseQuadrants.All(v=>v))return true;
        if(!c.AnalysisPhaseQuadrants.Any(v=>v))return false;
        return c.CycleReference!=null?AnalysisPhaseQuadrantPolicy.EligibleDegrees(c.AnalysisPhaseQuadrants,A4PhaseAt(c,canonicalPeak)):
            AnalysisPhaseQuadrantPolicy.Eligible(c.AnalysisPhaseQuadrants,c.AnalysisInitialPhaseDegrees,c.AnalysisFrequencyHz,c.Fs,canonicalPeak);
    }
    private static string A4AnalysisPhaseJson(A4Context c)
    {return "{\"enabled_quadrants\":["+String.Join(",",c.AnalysisPhaseQuadrants.Select(v=>v?"true":"false"))+
        "],\"reference_source\":"+Json(c.CycleReference==null?"legacy_uniform_frequency":"recorded_alternating_zero_crossings")+
        ",\"boundaries_degrees\":[0,90,180,270,360],\"intervals\":\"left_closed_right_open\",\"anchor\":\"original_canonical_incident_peak\",\"before_analysis_count_limit\":true}";}
}
