using System;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Per-call output diagnostic only. No static cache or scoring state is used.
    private sealed class A4Index4DisplayGainInfo
    {
        internal bool Applied;
        internal string Reason="not_evaluated";
        internal double Gain=1,Baseline,FilteredPacketPeak,TargetRawCharge,MinimumUsablePeak;
        internal int SupportStart=-1,SupportEnd=-1,RawChargeIndex=-1,WindowStart;
    }

    private static double[] A4Index4PreserveDisplayAmplitude(
        A4Context c,A4Pulse incident,int windowStart,double[] alreadyFiltered)
    {
        A4Index4DisplayGainInfo diagnostic;
        return A4Index4PreserveDisplayAmplitude(c,incident,windowStart,alreadyFiltered,out diagnostic);
    }

    // This function is called only after matching and waveform measurement.
    // Its gain is determined on the fixed complete incident packet, NEVER on
    // the requested display crop or on any reflection's amplitude. A single
    // baseline subtraction and positive gain apply to every displayed sample.
    private static double[] A4Index4PreserveDisplayAmplitude(
        A4Context c,A4Pulse incident,int windowStart,double[] alreadyFiltered,
        out A4Index4DisplayGainInfo diagnostic)
    {
        diagnostic=new A4Index4DisplayGainInfo {WindowStart=windowStart};
        if(incident!=null)
        {
            diagnostic.SupportStart=incident.Start;diagnostic.SupportEnd=incident.End;
            diagnostic.RawChargeIndex=A4ChargeIndex(incident);diagnostic.Baseline=incident.Baseline;
        }
        double[] unchanged=alreadyFiltered==null?new double[0]:(double[])alreadyFiltered.Clone();
        if(c==null){diagnostic.Reason="missing_context";return unchanged;}
        if(c.Algorithm!=4){diagnostic.Reason="algorithm_not_index4";return unchanged;}
        if(!c.Filtered){diagnostic.Reason="display_filter_disabled";return unchanged;}
        if(incident==null){diagnostic.Reason="missing_incident";return unchanged;}
        if(alreadyFiltered==null||alreadyFiltered.Length==0){diagnostic.Reason="empty_display_window";return unchanged;}
        diagnostic.SupportStart=incident.Start;diagnostic.SupportEnd=incident.End;
        diagnostic.RawChargeIndex=A4ChargeIndex(incident);diagnostic.Baseline=incident.Baseline;
        if(!Finite(incident.Baseline)){diagnostic.Reason="nonfinite_incident_baseline";return unchanged;}
        if(incident.AnalysisSamples==null||incident.AnalysisSamples.Length==0)
        {diagnostic.Reason="missing_fixed_incident_analysis_samples";return unchanged;}
        long supportFirst=(long)incident.Start-incident.AnalysisOrigin;
        long supportLast=(long)incident.End-incident.AnalysisOrigin;
        if(supportFirst<0||supportLast<supportFirst||supportLast>=incident.AnalysisSamples.Length)
        {diagnostic.Reason="fixed_incident_support_outside_analysis_window";return unchanged;}
        if(diagnostic.RawChargeIndex<incident.Start||diagnostic.RawChargeIndex>incident.End)
        {diagnostic.Reason="raw_charge_peak_outside_fixed_incident_support";return unchanged;}
        double[] reference=A4RawAnalysis(c);
        if(reference==null||diagnostic.RawChargeIndex<0||diagnostic.RawChargeIndex>=reference.Length)
        {diagnostic.Reason="raw_charge_peak_unavailable";return unchanged;}
        if(!Finite(incident.NoiseSigma)||incident.NoiseSigma<0)
        {diagnostic.Reason="incident_noise_estimate_unavailable";return unchanged;}
        double peak=0;
        for(int n=(int)supportFirst;n<=(int)supportLast;n++)
        {
            double centered=incident.AnalysisSamples[n]-incident.Baseline;
            if(!Finite(centered)){diagnostic.Reason="nonfinite_fixed_incident_support";return unchanged;}
            peak=Math.Max(peak,Math.Abs(centered));
        }
        diagnostic.FilteredPacketPeak=peak;
        diagnostic.MinimumUsablePeak=Math.Max(1e-12,3*incident.NoiseSigma);
        if(!(peak>diagnostic.MinimumUsablePeak))
        {diagnostic.Reason="filtered_incident_peak_not_above_noise_floor";return unchanged;}
        double target=A4RawCharge(c,diagnostic.RawChargeIndex);
        diagnostic.TargetRawCharge=target;
        if(!Finite(target)||target<=0){diagnostic.Reason="raw_charge_not_positive_finite";return unchanged;}
        double gain=target/peak;
        if(!Finite(gain)||gain<=0){diagnostic.Reason="display_gain_not_positive_finite";return unchanged;}
        double[] scaled=new double[alreadyFiltered.Length];
        for(int n=0;n<scaled.Length;n++)
        {
            // Preserve missing/padding samples, but do not turn infinity or an
            // overflowing transformed sample into a partially scaled window.
            if(double.IsNaN(alreadyFiltered[n])){scaled[n]=double.NaN;continue;}
            double value=(alreadyFiltered[n]-incident.Baseline)*gain;
            if(!Finite(value)){diagnostic.Reason="display_sample_scaling_nonfinite";return unchanged;}
            scaled[n]=value;
        }
        diagnostic.Gain=gain;diagnostic.Applied=true;
        diagnostic.Reason="fixed_incident_packet_single_gain_display_only";
        return scaled;
    }

    private static string A4Index4DisplayGainJson(A4Index4DisplayGainInfo g)
    {
        return "{\"applied\":"+(g.Applied?"true":"false")+",\"reason\":"+Json(g.Reason)+
            ",\"raw_peak\":"+g.RawChargeIndex+",\"gain\":"+NumberJson(g.Gain)+
            ",\"baseline_removed\":"+NumberJson(g.Baseline)+",\"filtered_packet_peak\":"+NumberJson(g.FilteredPacketPeak)+
            ",\"target_raw_charge\":"+NumberJson(g.TargetRawCharge)+",\"fixed_support_start\":"+g.SupportStart+
            ",\"fixed_support_end\":"+g.SupportEnd+",\"window_start\":"+g.WindowStart+"}";
    }
}
