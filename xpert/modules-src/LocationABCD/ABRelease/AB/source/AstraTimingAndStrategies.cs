using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private static string A4StrategyName(int algorithm)
    {
        switch(algorithm)
        {
            case 0:return "peak_ranked_qualified_packets";
            case 1:return "correlation_ranked_qualified_packets";
            case 2:return "energy_ranked_qualified_packets";
            case 3:return "consensus_ranked_qualified_packets";
            default:return "astra_global_physical_evidence";
        }
    }
    private static double A4StrategyWeight(A4Pair pair)
    {
        return Finite(pair.StrategyWeight)?pair.StrategyWeight:Math.Max(0,pair.Confidence-A4MinimumOutputConfidence);
    }
    private static void A4ApplyStrategy(A4Context c,List<A4Pair> pool)
    {
        foreach(A4Pair pair in pool)pair.StrategyWeight=double.NaN;
        if(c.Algorithm==4)return; // Preserve the fifth method's existing physical objective exactly.
        foreach(var group in pool.Where(p=>p.Confidence>A4MinimumOutputConfidence&&!p.Blind&&!p.Amplified).GroupBy(p=>p.Incident.Id))
        {
            A4Pair[] edges=group.ToArray();
            double peak=edges.Max(p=>A4RawCharge(c,A4ChargeIndex(p.Reflection)));
            double energy=edges.Max(p=>p.Reflection.Energy);
            double corr=edges.Max(p=>p.UnifiedCorrelation==null?0:p.UnifiedCorrelation.Score);
            foreach(A4Pair pair in edges)
            {
                double p=peak>0?A4RawCharge(c,A4ChargeIndex(pair.Reflection))/peak:0;
                double e=energy>0?pair.Reflection.Energy/energy:0;
                double r=corr>0&&pair.UnifiedCorrelation!=null?pair.UnifiedCorrelation.Score/corr:0;
                // Methods differ only AFTER the same complete-waveform, physical and
                // confidence gates. Consensus chooses a real edge, never median indices.
                double criterion=c.Algorithm==0?p:c.Algorithm==1?r:c.Algorithm==2?e:(p+r+e)/3;
                pair.StrategyWeight=Math.Max(0,pair.Confidence-A4MinimumOutputConfidence)*(.5+.5*Math.Max(0,Math.Min(1,criterion)));
            }
        }
    }
    private static int A4RawLeadingEdge(A4Context c,A4Pulse pulse)
    {
        return pulse==null?-1:A4RawLeadingEdge(c,pulse,A4ChargeIndex(pulse));
    }
    private static int A4RawLeadingEdge(A4Context c,A4Pulse pulse,int chosenPeak)
    {
        if(c==null||c.Raw==null||pulse==null||!Finite(c.RawCenter)||!Finite(c.Sigma)||c.Sigma<0)return -1;
        return PeakAnchoredLeadingEdgeMath.FindPacket(c.Raw.Length,pulse.Start,pulse.End,chosenPeak,c.RoundTrip,c.Sigma,i=>A4RawSigned(c,i));
    }
    private static int A4RequestedTiming(A4Context c,A4Pulse pulse,int peak,bool firstArrival)
    {
        return firstArrival?A4RawLeadingEdge(c,pulse,peak):peak;
    }
    private static bool A4PhysicalTiming(A4Context c,A4Pulse a,A4Pulse b,out int incident,out int reflection)
    {
        if(!A4CanonicalPeaks(c,a,b,out incident,out reflection))return false;
        if(c.FirstArrivalPhysicalTiming)
        {
            incident=A4RawLeadingEdge(c,a,incident);reflection=A4RawLeadingEdge(c,b,reflection);
            if(incident<0||reflection<=incident){incident=reflection=-1;return false;}
        }
        return true;
    }
    private static bool A4CanonicalPeaks(A4Context c,A4Pulse a,A4Pulse b,out int incident,out int reflection)
    {
        incident=reflection=-1;
        if(c==null||c.Raw==null||a==null||b==null||!Finite(c.RawCenter))return false;
        int ai=A4ChargeIndex(a),bi=A4ChargeIndex(b);
        if(ai<0||bi<0||ai>=c.Raw.Length||bi>=c.Raw.Length||ai<a.Start||ai>a.End||bi<b.Start||bi>b.End)return false;
        double av=A4RawSigned(c,ai),bv=A4RawSigned(c,bi);
        if(!Finite(av)||!Finite(bv)||av==0||bv==0)return false;
        // The larger absolute lobe can change during propagation without a true
        // polarity reversal. Keep the physical raw-peak identity immutable;
        // shared same-order lobe correlation owns the polarity acceptance gate.
        // Causal order is intrinsic to the named pair. Cable round-trip bounds are
        // checked by candidate acceptance/review, never by the user's timing display.
        if(bi<=ai)return false;
        incident=ai;reflection=bi;return true;
    }
    private static int A4ReviewDelayLimit(A4Context c,A4Pulse a,A4Pulse b)
    {
        if(c==null||a==null||b==null)return 0;
        return A4ReflectionMaximumDelay(c);
    }
    private static bool A4OutputTiming(A4Context c,A4Pair pair,bool first,out int incident,out int reflection)
    {
        incident=reflection=-1;
        if(pair==null)return false;
        A4Pulse a=pair.Incident,b=pair.Reflection;int peakA,peakB;
        if(!A4CanonicalPeaks(c,a,b,out peakA,out peakB))
        {
            pair.OutputTimingReason="raw_canonical_peak_identity_invalid";return false;
        }
        incident=peakA;reflection=peakB;
        pair.OutputTimingReason="fixed_baseline_corrected_raw_absolute_peaks_temporal_polarity_gate";
        if(first)
        {
            int t0=A4RawLeadingEdge(c,a,peakA),t1=A4RawLeadingEdge(c,b,peakB);
            if(t0>=0&&t0<c.Raw.Length&&t0<=peakA&&t1>=0&&t1<c.Raw.Length&&t1<=peakB&&t1>t0)
            {
                incident=t0;reflection=t1;
                pair.OutputTimingReason=t0<a.Start||t1<b.Start?
                    "raw_peak_anchored_leading_edges_with_bounded_guard_extension":"raw_peak_anchored_main_wave_leading_edges";
            }
            else
            {
                pair.OutputTimingReason="first_arrival_unresolved_no_peak_fallback";
                incident=reflection=-1;pair.OutputIncident=pair.OutputReflection=-1;return false;
            }
        }
        pair.OutputIncident=incident;pair.OutputReflection=reflection;
        // In particular, do not delete an already-selected near-end review row
        // because peak and first-arrival dispersion place them on different sides
        // of the round-trip boundary. Its acceptance/NaN position is decided earlier.
        return true;
    }
}
