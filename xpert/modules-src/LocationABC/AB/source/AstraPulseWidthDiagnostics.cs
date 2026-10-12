using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

// Independently written half-height measurement, not a vendor implementation.
// Read-only diagnostic: never used by pulse discovery, pairing, scores or output matrices.
internal static class AstraPulseWidthDiagnostic
{
    internal sealed class Measurement
    {
        internal bool Valid,Clipped,MultiPeak,LowSnr,TouchesRecordBoundary;
        internal double WidthNs=double.NaN,ComponentWidthNs=double.NaN;
        internal double Left=double.NaN,Right=double.NaN,Amplitude=double.NaN,Snr=double.NaN;
        internal int Components,Polarity;
        internal string Reason="invalid_input";
    }
    private static bool Finite(double x){return !double.IsNaN(x)&&!double.IsInfinity(x);}
    internal static Measurement Measure(double[] raw,int start,int end,int peak,double baseline,double noise,double fs)
    {
        var m=new Measurement();
        if(raw==null||start<0||end>=raw.Length||start>end||peak<start||peak>end||
            !Finite(baseline)||!Finite(noise)||noise<0||!Finite(fs)||fs<=0)return m;
        for(int i=start;i<=end;i++)if(!Finite(raw[i]))return m;
        double signed=raw[peak]-baseline;
        if(!Finite(signed)||signed==0){m.Reason="zero_peak";return m;}
        m.Polarity=signed>0?1:-1;m.Amplitude=Math.Abs(signed);
        m.Snr=noise>0?m.Amplitude/noise:double.NaN;
        m.LowSnr=noise>0&&m.Amplitude<5*noise;
        m.TouchesRecordBoundary=start==0||end==raw.Length-1;
        double half=.5*m.Amplitude;
        int left=peak,right=peak;
        while(left>start&&m.Polarity*(raw[left-1]-baseline)>=half)left--;
        while(right<end&&m.Polarity*(raw[right+1]-baseline)>=half)right++;
        double atLeft=m.Polarity*(raw[left]-baseline),atRight=m.Polarity*(raw[right]-baseline);
        if(atLeft==half)m.Left=left;
        else if(left>start)
        {
            double outside=m.Polarity*(raw[left-1]-baseline);
            m.Left=left-1+(half-outside)/(atLeft-outside);
        }
        if(atRight==half)m.Right=right;
        else if(right<end)
        {
            double outside=m.Polarity*(raw[right+1]-baseline);
            m.Right=right+(atRight-half)/(atRight-outside);
        }
        bool active=false;
        for(int i=start;i<=end;i++)
        {
            bool above=m.Polarity*(raw[i]-baseline)>=half;
            if(above&&!active)m.Components++;
            active=above;
        }
        m.MultiPeak=m.Components>1;
        m.Clipped=!Finite(m.Left)||!Finite(m.Right);
        if(!m.Clipped&&m.Right>m.Left)m.ComponentWidthNs=(m.Right-m.Left)/fs*1e9;
        var reasons=new List<string>();
        if(m.Clipped)reasons.Add("truncated_half_height_support");
        if(m.MultiPeak)reasons.Add("multiple_same_polarity_half_height_lobes");
        if(m.LowSnr)reasons.Add("raw_peak_snr_below_5");
        if(!m.Clipped&&(!Finite(m.ComponentWidthNs)||m.ComponentWidthNs<=0))reasons.Add("unresolved_positive_width");
        m.Valid=reasons.Count==0;
        if(m.Valid)m.WidthNs=m.ComponentWidthNs;
        m.Reason=m.Valid?"valid":String.Join(";",reasons);
        return m;
    }
}

internal sealed partial class LocationEngine
{
    // This engine is new for every public call; no state survives the invocation.
    private readonly Dictionary<string,A4Pair> widthDiagnosticPairs=new Dictionary<string,A4Pair>();
    private List<A4Pulse> widthDiagnosticPackets=new List<A4Pulse>();
    private void A4RememberWidthPair(double[] row,A4Pair pair)
    {widthDiagnosticPairs[A4RowKey(row)]=pair;}

    private sealed class A4RawWidth
    {
        internal AstraPulseWidthDiagnostic.Measurement Measurement;
        internal double Baseline,Noise;
        internal int GuardSamples;
        internal bool BaselineAvailable;
    }
    private A4RawWidth A4RawWidthMeasurement(A4Pulse pulse)
    {
        A4Context c=equivalentContext;
        int width=Math.Max(24,Math.Min(128,c.RoundTrip/5));
        int left=Math.Max(0,pulse.Start-width),right=Math.Min(c.Raw.Length-1,pulse.End+width);
        // Guard samples cannot include any discovered neighbour support. Only
        // the selected packet bounds and original raw signal are read.
        foreach(A4Pulse other in widthDiagnosticPackets)
        {
            if(other.End<pulse.Start)left=Math.Max(left,other.End+1);
            if(other.Start>pulse.End)right=Math.Min(right,other.Start-1);
        }
        var guards=new List<double>();
        double[] corrected=A4RawAnalysis(c);
        for(int i=left;i<pulse.Start;i++)guards.Add(corrected[i]);
        for(int i=pulse.End+1;i<=right;i++)guards.Add(corrected[i]);
        bool available=guards.Count>=8;
        double baseline=available?Median(guards.ToArray()):c.RawCenter;
        double sigma=Math.Max(1e-15,available?Math.Max(c.Sigma,RobustSigma(guards.ToArray(),baseline)):c.Sigma);
        var m=AstraPulseWidthDiagnostic.Measure(corrected,pulse.Start,pulse.End,A4ChargeIndex(pulse),baseline,sigma,c.Fs);
        if(!available)
        {
            m.Valid=false;m.WidthNs=double.NaN;
            m.Reason=(m.Reason=="valid"?"":m.Reason+";")+"insufficient_unoccupied_raw_guard_samples";
        }
        return new A4RawWidth {Measurement=m,Baseline=baseline,Noise=sigma,GuardSamples=guards.Count,BaselineAvailable=available};
    }
    private static string A4RawWidthJson(A4RawWidth result,A4Pulse pulse)
    {
        var m=result.Measurement;
        return "{\"raw_peak_index\":"+A4ChargeIndex(pulse)+",\"support_start\":"+pulse.Start+",\"support_end\":"+pulse.End+
            ",\"valid\":"+(m.Valid?"true":"false")+",\"status\":"+Json(m.Reason)+",\"width_ns\":"+NumberJson(m.WidthNs)+
            ",\"measured_main_lobe_width_ns\":"+NumberJson(m.ComponentWidthNs)+
            ",\"left_half_height_sample\":"+NumberJson(m.Left)+",\"right_half_height_sample\":"+NumberJson(m.Right)+
            ",\"polarity\":"+m.Polarity+",\"raw_peak_above_local_baseline_pc\":"+NumberJson(m.Amplitude)+
            ",\"raw_baseline_pc\":"+NumberJson(result.Baseline)+",\"raw_noise_sigma_pc\":"+NumberJson(result.Noise)+
            ",\"raw_snr\":"+NumberJson(m.Snr)+",\"guard_samples\":"+result.GuardSamples+
            ",\"local_baseline_available\":"+(result.BaselineAvailable?"true":"false")+
            ",\"truncated\":"+(m.Clipped?"true":"false")+",\"multiple_half_height_lobes\":"+(m.MultiPeak?"true":"false")+
            ",\"half_height_lobe_count\":"+m.Components+",\"low_snr\":"+(m.LowSnr?"true":"false")+
            ",\"support_touches_record_boundary\":"+(m.TouchesRecordBoundary?"true":"false")+"}";
    }
    internal void CompletePulseWidthDiagnostics(double[,] currentParameters)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        try { A4BuildPulseWidthDiagnostics(currentParameters); }
        catch(Exception ex)
        {
            // Diagnostic failure is nonfatal and cannot discard an already fixed
            // analysis result. A failure is explicit, never a zero/valid width.
            int end=StatusJson.LastIndexOf('}');
            StatusJson=StatusJson.Substring(0,end)+",\"raw_half_height_width_diagnostics\":{\"version\":\"1.0\",\"available\":false,\"status\":\"diagnostic_failed\",\"message\":"+Json(ex.Message)+",\"changes_output_matrices\":false,\"automatic_endpoint_classification\":false}}";
        }
        timer.Stop();
        int last=StatusJson.LastIndexOf('}');
        StatusJson=StatusJson.Substring(0,last)+",\"raw_half_height_diagnostic_elapsed_ms\":"+NumberJson(timer.Elapsed.TotalMilliseconds)+"}";
        LastResultJson=StatusJson;
    }
    private void A4BuildPulseWidthDiagnostics(double[,] currentParameters)
    {
        // The full current matrix is already fixed; this function performs NO
        // writes to matrices, pulses, pair scores, geometry or acceptance flags.
        var rows=new List<string>();int valid=0,unavailable=0;
        var cache=new Dictionary<A4Pulse,A4RawWidth>();
        for(int r=0;r<currentParameters.GetLength(0);r++)
        {
            string prefix="{\"current_row\":"+r+",\"phase\":"+NumberJson(currentParameters[r,0])+
                ",\"record_id\":"+NumberJson(currentParameters[r,1])+
                ",\"output_incident_index\":"+NumberJson(currentParameters[r,10])+
                ",\"output_reflection_index\":"+NumberJson(currentParameters[r,11])+
                ",\"accepted\":"+(currentParameters[r,35]==1?"true":"false");
            A4Pair pair=A4CurrentMixedPair(currentParameters,r);
            if(pair==null)
            {rows.Add(prefix+",\"valid\":false,\"status\":\"current_pair_support_unavailable\",\"reflection_incident_width_ratio\":null}");unavailable++;continue;}
            A4RawWidth a,b;
            if(!cache.TryGetValue(pair.Incident,out a)){a=A4RawWidthMeasurement(pair.Incident);cache[pair.Incident]=a;}
            if(!cache.TryGetValue(pair.Reflection,out b)){b=A4RawWidthMeasurement(pair.Reflection);cache[pair.Reflection]=b;}
            bool usable=a.Measurement.Valid&&b.Measurement.Valid;
            double ratio=usable?b.Measurement.WidthNs/a.Measurement.WidthNs:double.NaN;
            if(!Finite(ratio)||ratio<=0)usable=false;
            if(usable)valid++;else unavailable++;
            rows.Add(prefix+",\"valid\":"+(usable?"true":"false")+",\"status\":"+Json(usable?"valid":"one_or_both_widths_unavailable")+
                ",\"reflection_incident_width_ratio\":"+NumberJson(usable?ratio:double.NaN)+
                ",\"incident\":"+A4RawWidthJson(a,pair.Incident)+",\"reflection\":"+A4RawWidthJson(b,pair.Reflection)+"}");
        }
        string appended=",\"raw_half_height_width_diagnostics\":{\"version\":\"1.0\",\"scope\":\"current_rows_only_including_review; never_recompute_history\""+
            ",\"source\":\"original_raw_LOC_selected_absolute_peak_and_packet_support; not_filtered_or_display_crop\""+
            ",\"width_definition\":\"same_polarity_main_lobe_50_percent_of_local_baseline_subtracted_peak; linear_crossing_interpolation\""+
            ",\"noise_guard\":\"at_least_8_unoccupied_raw_samples; max(global_robust_sigma,local_robust_sigma); diagnostic_snr_floor_5\""+
            ",\"multiple_lobe_rule\":\"more_than_one_disconnected_same_polarity_component_at_or_above_half_height\""+
            ",\"invalid_width_and_ratio\":\"null; measured_main_lobe_width_is_diagnostic_only_when_status_invalid\""+
            ",\"changes_matching\":false,\"changes_acceptance\":false,\"changes_position\":false,\"changes_charge\":false"+
            ",\"changes_output_matrices\":false,\"automatic_endpoint_classification\":false,\"vendor_equivalence_claimed\":false"+
            ",\"row_count\":"+rows.Count+",\"valid_pair_count\":"+valid+",\"unavailable_pair_count\":"+unavailable+
            ",\"rows\":["+String.Join(",",rows)+"]}";
        int end=StatusJson.LastIndexOf('}');
        StatusJson=StatusJson.Substring(0,end)+appended+"}";
        LastResultJson=StatusJson;
    }
}
