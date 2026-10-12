using System;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private static void A4ReviewBoundaryCompetition(A4Context c,System.Collections.Generic.List<A4Pulse> pulses,
        System.Collections.Generic.List<A4Pair> pool)
    {
        var reviewContexts=new System.Collections.Generic.Dictionary<int,A4Context>();
        var reviewPackets=new System.Collections.Generic.Dictionary<int,A4ReviewPacket>();
        foreach(A4Pair edge in pool.Where(e=>e.Confidence>A4MinimumOutputConfidence))
        foreach(A4Pulse other in pulses.Where(p=>p.UnresolvedOverlap&&A4ChargeIndex(p)>A4ChargeIndex(edge.Incident)&&
            A4ChargeIndex(p)-A4ChargeIndex(edge.Incident)<=A4PhysicalDelayLimit(c)))
        {
            if(!A4TakeRoleReviewStep(c))return;
            A4ReviewPacket r;
            if(!reviewPackets.TryGetValue(other.Id,out r))
            {r=A4ReviewBoundaryHypothesis(c,other);reviewPackets.Add(other.Id,r);}
            if(r.Pulse==null||r.Pulse.Start<=edge.Incident.End||!r.Pulse.MappingValid||r.Pulse.Clipped||r.Pulse.BaselineUnstable)continue;
            int peakA,peakB;if(!A4CanonicalPeaks(c,edge.Incident,r.Pulse,out peakA,out peakB))continue;
            int delay=peakB-peakA;
            if(!A4ReflectionDelayWithinWindow(c,delay)||!A4BoundaryTimingValid(c,delay))continue;
            A4Context reviewContext;
            if(!reviewContexts.TryGetValue(other.Id,out reviewContext))
            {
                reviewContext=A4ReviewContext(c);
                A4PrepareUnifiedCorrelationReview(reviewContext,c,pulses.Select(p=>p.Id==other.Id?r.Pulse:p).ToList());
                reviewContexts.Add(other.Id,reviewContext);
            }
            double wave,env;A4Correlate(c,edge.Incident,r.Pulse,out wave,out env);
            A4UnifiedCorrelationEvidence unified=A4UnifiedCorrelation(reviewContext,edge.Incident,r.Pulse,delay);
            double corr=unified.Measured==null?double.NaN:unified.Measured.Fixed,
                ratio=r.Pulse.LocalAmplitude/Math.Max(1e-30,edge.Incident.LocalAmplitude);
            // Boundary hypotheses cannot acquire ownership. They may only prevent a
            // premature unique claim when BOTH CAL-relative signed shape and raw amplitude
            // give a clearly stronger alternative for this SAME incident.
            double rawGain=A4RawCharge(c,A4ChargeIndex(other))-
                A4RawCharge(c,A4ChargeIndex(edge.Reflection));
            double noiseAllowance=3*(r.Pulse.NoiseSigma+edge.Reflection.NoiseSigma);
            A4LowBandPairEvidence low=A4MeasureLowBandPair(c,edge.Incident,other,delay);
            bool challenge=unified.Passed&&Finite(ratio)&&Math.Min(edge.Incident.Snr,r.Pulse.Snr)>=4.5&&
                ratio<=1.20+2/Math.Max(3,edge.Incident.Snr)&&rawGain>noiseAllowance&&
                edge.UnifiedCorrelation!=null&&unified.Score>edge.UnifiedCorrelation.Score+A4GlobalStabilityMargin;
            if(challenge){edge.BoundaryAmbiguous=true;edge.Ambiguous=true;}
            if(c.BoundaryReviewTraces.Count<256)c.BoundaryReviewTraces.Add("{\"kind\":\"pre_ownership_hypothesis_competition\",\"incident_peak\":"+
                A4ChargeIndex(edge.Incident)+",\"current_reflection_peak\":"+A4ChargeIndex(edge.Reflection)+
                ",\"hypothesis_reflection_peak\":"+A4ChargeIndex(other)+",\"signed_corr\":"+NumberJson(wave)+
                ",\"measured_corr\":"+NumberJson(corr)+",\"unified_correlation\":"+A4UnifiedCorrelationJson(unified)+",\"snr\":"+NumberJson(r.Pulse.Snr)+
                ",\"ratio\":"+NumberJson(ratio)+",\"raw_amplitude_advantage\":"+NumberJson(rawGain)+
                ",\"raw_noise_allowance\":"+NumberJson(noiseAllowance)+",\"blocks_early_ownership\":"+(challenge?"true":"false")+
                ",\"hypothesis_may_confirm\":false,\"low_band_evidence\":"+A4LowBandPairJson(low)+"}");
        }
    }
    // Re-evaluate only unresolved RAW supports, before filtering, scoring or ownership.
    // Period is measured from the absolute peak, not from an earlier small noise trigger.
    // No candidate, correlation, expected position or attenuation target is consulted.
    private static A4Pulse A4RefineUnresolvedRawPacket(A4Context raw,A4Pulse original,A4Context record,int origin)
    {
        if(!original.UnresolvedOverlap)return original;
        int peak=AbsoluteMaximumIndex(raw.Raw,original.Start,original.End,raw.RawCenter);
        A4Pulse p=A4SeededPacket(raw,peak,peak);
        string reason="retained_unresolved_no_complete_peak_seeded_support";
        // Proved index4/method6 guards are consumed by a >=16-sample measurement contract.
        // Never mark a 12-15 sample interval proved only to reject it irreversibly later.
        int hold=Math.Max(record.Algorithm==5||record.Algorithm==4?16:12,Math.Min(64,(int)Math.Ceiling(p.Period*.8)));
        int before=-1,after=-1;
        bool eligible=!original.Clipped&&!p.Clipped&&!p.UnresolvedOverlap&&p.MappingValid&&
            p.Start>=original.Start&&p.End<=original.End&&p.Start<peak&&p.End>peak&&
            AbsoluteMaximumIndex(raw.Raw,p.Start,p.End,raw.RawCenter)==peak;
        if(eligible)
        {
            // Require full external quiet intervals. Keep the entire leading/trailing
            // residual up to these intervals; never cut at a chosen correlation peak.
            for(int k=p.Start-hold;k>=Math.Max(original.Start,p.Start-2*hold);k--)
                if(A4QuietBlock(raw.Raw,k,k+hold-1,raw.RawCenter,raw.Sigma)){before=k;break;}
            // Index4 / method6: lastActive is measured relative to the fixed center, while
            // QuietBlock permits a bounded local DC offset. The terminal quiet
            // block can therefore contain lastActive. Reuse that same valid block
            // instead of requiring a second quiet interval after it. Final support
            // still ends BEFORE the guard; every existing quality gate is retained.
            int afterSearchStart=record.Algorithm==5||record.Algorithm==4?Math.Max(peak+1,p.End-hold+1):p.End+1;
            for(int k=afterSearchStart;k<=Math.Min(original.End-hold+1,p.End+2*hold);k++)
                if(A4QuietBlock(raw.Raw,k,k+hold-1,raw.RawCenter,raw.Sigma)){after=k;break;}
            eligible=before>=0&&after>=0;
            reason="retained_unresolved_missing_full_external_quiet_guards";
        }
        if(eligible)
        {
            p.Start=before+hold;p.End=after-1;
            double main=raw.Raw[peak]-raw.RawCenter,opposite=0;
            for(int k=p.Start;k<=p.End;k++)
                if((raw.Raw[k]-raw.RawCenter)*main<0)opposite=Math.Max(opposite,Math.Abs(raw.Raw[k]-raw.RawCenter));
            // A transient zero-crossing gap must not turn one bipolar packet into
            // a unipolar spike. Unipolar/ambiguous cases remain manual-review only.
            eligible=opposite>=Math.Max(5*raw.Sigma,.10*Math.Abs(main))&&p.End-p.Start<=raw.RoundTrip;
            reason="retained_unresolved_bipolar_support_not_demonstrated";
            // A weak opposite precursor is not the principal opposite lobe. If a
            // stronger opposite excursion remains nearby outside the proposed support,
            // retain uncertainty rather than cutting it off or expanding to force a pair.
            int lookAhead=Math.Min(original.End,peak+Math.Max(96,raw.RoundTrip/4));
            for(int k=p.End+1;eligible&&k<=lookAhead;k++)
                if((raw.Raw[k]-raw.RawCenter)*main<0&&Math.Abs(raw.Raw[k]-raw.RawCenter)>opposite+3*raw.Sigma)
                {eligible=false;reason="retained_unresolved_stronger_opposite_lobe_outside_support";}
        }
        if(eligible)
        {
            p.RawAmplitudeIndex=peak;p.BoundaryRefined=true;A4Measure(raw,p);
            if(record.Algorithm==5||record.Algorithm==4)
            {
                p.HasRefinedGuardProof=true;
                p.RefinedLeftGuardStart=before;p.RefinedLeftGuardEnd=before+hold-1;
                p.RefinedRightGuardStart=after;p.RefinedRightGuardEnd=after+hold-1;
            }
            reason="raw_absolute_peak_period_and_two_full_quiet_guards_before_matching";
        }
        if(record.BoundaryReviewTraces.Count<256)
            record.BoundaryReviewTraces.Add("{\"raw_peak\":"+(peak+origin)+",\"original_start\":"+(original.Start+origin)+
                ",\"original_end\":"+(original.End+origin)+",\"refined\":"+(eligible?"true":"false")+
                ",\"start\":"+((eligible?p.Start:original.Start)+origin)+",\"end\":"+((eligible?p.End:original.End)+origin)+
                ",\"guard_samples\":"+hold+",\"before_guard_start\":"+(before<0?-1:before+origin)+
                ",\"after_guard_start\":"+(after<0?-1:after+origin)+",\"reason\":"+Json(reason)+"}");
        return eligible?p:original;
    }
}
