using System;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Selection window: two percent of NET DUT length at each endpoint,
    // consistent with the automatic endpoint projection boundary.
    // Distinct automatic echoes still require positive delay and all evidence
    // gates. This does not change the separate two-percent scoring policy.
    private const double A4ReflectionWindowTolerancePercent=2;
    private static bool A4ReflectionWindow(A4Context c,out double lower,out double upper)
    {
        lower=upper=double.NaN;
        if(c==null||!Finite(c.Fs)||c.Fs<=0||!Finite(c.Length)||c.Length<=0||
            c.Joints==null||c.Velocities==null||c.Velocities.Length<1||
            c.Joints.Length!=c.Velocities.Length+1||c.Joints[0]!=0||c.Joints[c.Joints.Length-1]!=c.Length)return false;
        double travel=0;
        for(int k=0;k<c.Velocities.Length;k++)
        {
            if(!Finite(c.Joints[k])||!Finite(c.Joints[k+1])||c.Joints[k+1]<=c.Joints[k]||
                !Finite(c.Velocities[k])||c.Velocities[k]<=0)return false;
            travel+=2*(c.Joints[k+1]-c.Joints[k])/c.Velocities[k]*1e-6*c.Fs;
        }
        double error=c.Length*A4ReflectionWindowTolerancePercent/100;
        // CABLE coordinates run from L at zero delay to 0 at one round trip.
        // Context geometry has already excluded a configured connecting lead.
        double lo=-2*error/c.Velocities[c.Velocities.Length-1]*1e-6*c.Fs;
        double hi=travel+2*error/c.Velocities[0]*1e-6*c.Fs;
        if(!Finite(travel)||travel<=0||!Finite(lo)||!Finite(hi)||hi<=0||lo<int.MinValue||hi>int.MaxValue)return false;
        lower=lo;upper=hi;return true;
    }
    private static bool A4ReflectionDelayWithinWindow(A4Context c,double delay)
    {
        if(!Finite(delay)||delay!=Math.Floor(delay))return false;
        double lower,upper;
        return A4ReflectionWindow(c,out lower,out upper)&&delay>=lower&&delay<=upper;
    }
    private static int A4ReflectionMaximumDelay(A4Context c)
    {
        double lower,upper;
        return A4ReflectionWindow(c,out lower,out upper)?(int)Math.Floor(upper):0;
    }
    private static bool A4ClearOutsideReflectionWindow(A4Context c,double[,] rows,int row)
    {
        // A user-explicit overlap remains unknown, not zero-score interference.
        if(HVX.SignalEvidence.CoincidentConfidenceMath.IsCoincidentRow(rows,row))return false;
        double incident=rows[row,10],reflection=rows[row,11];
        if(!Finite(reflection))return false;
        bool valid=Finite(incident)&&incident>=0&&reflection>=0&&incident<=int.MaxValue&&reflection<=int.MaxValue&&
            incident==Math.Floor(incident)&&reflection==Math.Floor(reflection)&&
            reflection-incident==rows[row,12]&&A4ReflectionDelayWithinWindow(c,reflection-incident);
        if(valid)return false;
        foreach(int column in new[]{11,12,13,16,17,21,22,33})rows[row,column]=double.NaN;
        rows[row,35]=0;return true;
    }
    private static bool A4PdmapCoverage(A4Context c,int incident,out int required,out int valid)
    {
        required=valid=0;double lower,upper;
        if(c==null||c.Raw==null||incident<0||incident>=c.Raw.Length||!A4ReflectionWindow(c,out lower,out upper))return false;
        double roundTrip=0;
        for(int k=0;k<c.Velocities.Length;k++)roundTrip+=2*(c.Joints[k+1]-c.Joints[k])/c.Velocities[k]*1e-6*c.Fs;
        // Inclusive integer coverage interval through ceil(physical RT).
        // Error allowance and display pre/post samples are NOT in the denominator.
        if(!Finite(roundTrip)||roundTrip<0||roundTrip>=int.MaxValue)return false;
        required=(int)Math.Ceiling(roundTrip)+1;
        long last=(long)incident+required-1;
        int availableLast=(int)Math.Min(c.Raw.Length-1L,last);
        for(int i=incident;i<=availableLast;i++)if(Finite(c.Raw[i]))valid++;
        // Integer comparison makes exactly 95% inclusive without rounding drift.
        return (long)valid*100>=(long)required*95;
    }
    private static bool A4TryCompletePdmapCrop(A4Context c,int peak,int displayIncident,int pre,int post,
        out int start,out int end,out string reason)
    {
        start=end=-1;reason="pdmap_geometry_or_sample_window_unavailable";
        double lower,upper;
        if(c==null||c.Raw==null||pre<0||post<0||peak<0||displayIncident<0||
            peak>=c.Raw.Length||displayIncident>=c.Raw.Length||!A4ReflectionWindow(c,out lower,out upper))return false;
        int required,valid;
        if(!A4PdmapCoverage(c,displayIncident,out required,out valid))
        {reason="pdmap_physical_roundtrip_finite_sample_coverage_below_95_percent";return false;}
        // Expand to real available samples. Missing pre/post or up to 5% tail
        // does not delete a valid coverage row. No zero padding or invented echo.
        // The public pre setting remains anchored to the canonical raw peak.
        // Moving display timing to an earlier first arrival must not silently
        // demand another full pre block before it (e.g. peak200/onset196/pre200).
        long first=(long)peak-pre;
        first=Math.Min(first,(long)Math.Min(peak,displayIncident)+(long)Math.Floor(lower));
        long last=(long)Math.Max(peak,displayIncident)+(long)Math.Ceiling(upper)+post;
        start=(int)Math.Max(0L,first);end=(int)Math.Min(c.Raw.Length-1L,last);
        reason="physical_roundtrip_has_at_least_95_percent_finite_original_samples";return true;
    }
    private static bool A4CompletePdmapCrop(A4Context c,int peak,int displayIncident,int pre,int post,out int start,out int end)
    {
        string reason;
        bool complete=A4TryCompletePdmapCrop(c,peak,displayIncident,pre,post,out start,out end,out reason);
        if(!complete&&c!=null)c.IncompletePdmapCrops[peak]=reason;
        return complete;
    }
    private static string A4ReflectionWindowJson(A4Context c)
    {
        double lower,upper;bool available=A4ReflectionWindow(c,out lower,out upper);
        return "{\"available\":"+(available?"true":"false")+",\"per_endpoint_position_error_percent\":"+
            NumberJson(A4ReflectionWindowTolerancePercent)+",\"minimum_delay_samples\":"+NumberJson(lower)+
            ",\"maximum_delay_samples\":"+NumberJson(upper)+",\"automatic_pair_still_requires_positive_delay\":true,"+
            "\"reflection_indices_not_clamped\":true,\"missing_samples_not_zero_padded\":true,\"minimum_physical_roundtrip_finite_coverage_percent\":95,"+
            "\"coverage_excludes_error_and_pre_post\":true,\"incomplete_pdmap_count\":"+c.IncompletePdmapCrops.Count+
            ",\"incomplete_pdmap_records\":["+String.Join(",",c.IncompletePdmapCrops.OrderBy(p=>p.Key).Select(p=>
                "{\"raw_incident_peak\":"+p.Key+",\"reason\":"+Json(p.Value)+"}"))+"]}";
    }
}
