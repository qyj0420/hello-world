using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Methods 1-5 only. Missing guarded evidence cannot certify a competing
    // physical explanation impossible. These records NEVER enter the solver,
    // acquire ownership, receive a physical score, or become a proposed pair.
    private const string A4UnresolvedAlternativeReason="unresolved_measurement_reflection_alternative";
    private const int A4UnresolvedAlternativeLimit=100000;
    private const double A4UnresolvedRawShapeFloor=.70;
    private sealed class A4UnresolvedAlternative
    {
        internal int IncidentId,ReflectionId,ReflectionStart,ReflectionEnd,ReflectionPeak,Delay;
        internal double RawSignedShape,RawEnvelopeShape;
        internal string MissingReason;
    }
    private sealed class A4UnresolvedAlternativeState
    {
        internal readonly Dictionary<int,List<A4UnresolvedAlternative>> ByIncident=new Dictionary<int,List<A4UnresolvedAlternative>>();
        internal readonly HashSet<long> PacketPairs=new HashSet<long>();
        internal int Count;
        internal bool BudgetExceeded;
    }
    private static readonly ConditionalWeakTable<A4Context,A4UnresolvedAlternativeState> a4UnresolvedAlternatives=
        new ConditionalWeakTable<A4Context,A4UnresolvedAlternativeState>();
    private static bool A4UnresolvedReviewEnabled(A4Context c)
    {return c!=null&&c.Algorithm>=0&&c.Algorithm<=4;}
    private static void A4ClearUnresolvedAlternatives(A4Context c)
    {if(A4UnresolvedReviewEnabled(c))a4UnresolvedAlternatives.Remove(c);}
    private static bool A4UnresolvedAlternativePhysical(A4Context c,A4Pulse a,A4Pulse b,out int delay)
    {
        delay=0;
        if(a==null||b==null||!a.MappingValid||!b.MappingValid||a.Clipped||b.Clipped||a.BaselineUnstable||b.BaselineUnstable||
            a.UnresolvedOverlap||b.UnresolvedOverlap||a.End>=b.Start)return false;
        int ia,ib;if(!A4CanonicalPeaks(c,a,b,out ia,out ib))return false;
        delay=ib-ia;
        if(!A4ReflectionDelayWithinWindow(c,delay)||!A4BoundaryTimingValid(c,delay)||
            delay<=Math.Max(Math.Max(a.Period,b.Period)*1.5,Math.Max(a.EquivalentSamples,b.EquivalentSamples)*2))return false;
        double incident=A4RawCharge(c,ia),reflection=A4RawCharge(c,ib);
        // A failed local guard may itself inflate the pulse-local noise value.
        // Requiring confirmation SNR here would silently discard precisely the
        // unresolved competitor this veto protects. Use the independent record
        // discovery noise only; it does not make the local measurement valid.
        if(!Finite(c.Sigma)||c.Sigma<=0||!Finite(incident)||!Finite(reflection)||
            incident<4.5*c.Sigma||reflection<4.5*c.Sigma)return false;
        double ratio=reflection/incident;
        return Finite(ratio)&&ratio<=3;
    }
    private static void A4CaptureUnresolvedAlternative(A4Context c,A4Pulse a,A4Pulse b,
        double rawSignedShape,double rawEnvelopeShape,string rejection,A4UnifiedCorrelationEvidence unified)
    {
        if(!A4UnresolvedReviewEnabled(c)||rejection!="guarded_correlation_measurement_unavailable"||
            unified==null||unified.Available||unified.Measured==null||
            !unified.Measured.StructureCompatible||unified.Measured.OppositeTemporalPolarity)return;
        // Raw/full diagnostics only screen whether an unresolved explanation is
        // credible enough to require review. They do NOT substitute for guarded
        // correlation, claim it passes its CAL floor, or contribute a score.
        if(!Finite(rawSignedShape)||!Finite(rawEnvelopeShape)||rawSignedShape<A4UnresolvedRawShapeFloor||
            rawEnvelopeShape<A4UnresolvedRawShapeFloor)return;
        int delay;if(!A4UnresolvedAlternativePhysical(c,a,b,out delay))return;
        int start,end;
        if(!A4PhysicalTiming(c,a,b,out start,out end)||!A4ReflectionDelayWithinWindow(c,end-start)||
            !A4BoundaryTimingValid(c,end-start))return;
        var state=a4UnresolvedAlternatives.GetOrCreateValue(c);
        // Packet identities are immutable during candidate construction. O(1)
        // deduplication avoids quadratic work on a noise-heavy long record.
        long key=A4PairKey(a,b);if(state.PacketPairs.Contains(key))return;
        List<A4UnresolvedAlternative> records;
        if(!state.ByIncident.TryGetValue(a.Id,out records))records=new List<A4UnresolvedAlternative>();
        if(state.Count>=A4UnresolvedAlternativeLimit){state.BudgetExceeded=true;return;}
        records.Add(new A4UnresolvedAlternative {IncidentId=a.Id,ReflectionId=b.Id,ReflectionStart=b.Start,ReflectionEnd=b.End,
            ReflectionPeak=A4ChargeIndex(b),Delay=end-start,RawSignedShape=rawSignedShape,RawEnvelopeShape=rawEnvelopeShape,
            MissingReason=unified.Reason});
        state.ByIncident[a.Id]=records;state.PacketPairs.Add(key);state.Count++;
    }
    private static bool A4HasUnresolvedAlternative(A4Context c,A4Pair selected)
    {
        if(!A4UnresolvedReviewEnabled(c)||selected==null||selected.Incident==null||selected.Reflection==null)return false;
        A4UnresolvedAlternativeState state;
        if(!a4UnresolvedAlternatives.TryGetValue(c,out state))return false;
        if(state.BudgetExceeded)return true; // Incomplete alternatives never imply uniqueness.
        List<A4UnresolvedAlternative> records;
        return state.ByIncident.TryGetValue(selected.Incident.Id,out records)&&records.Any(r=>
            r.ReflectionId!=selected.Reflection.Id&&
            (r.ReflectionEnd<selected.Reflection.Start||r.ReflectionStart>selected.Reflection.End));
    }
    private static string A4UnresolvedAlternativeJson(A4Context c)
    {
        A4UnresolvedAlternativeState state;
        if(!a4UnresolvedAlternatives.TryGetValue(c,out state))state=new A4UnresolvedAlternativeState();
        var records=state.ByIncident.OrderBy(k=>k.Key).SelectMany(k=>k.Value.OrderBy(r=>r.ReflectionPeak)).ToArray();
        return "{\"enabled\":true,\"methods\":[1,2,3,4,5],\"policy\":\"distinct_physically_possible_but_guarded_unmeasurable_reflection_requires_review\","+
            "\"acceptance_only_veto\":true,\"promotes_missing_evidence\":false,\"adds_solver_edges\":false,\"changes_physical_scores\":false,"+
            "\"raw_shape_screen_floor\":0.70,\"raw_shape_screen_is_confirmation\":false,\"raw_discovery_snr_floor\":4.5,\"invalid_local_guard_noise_not_used_for_confirmation\":true,\"alternative_limit\":"+A4UnresolvedAlternativeLimit+
            ",\"alternative_count\":"+state.Count+",\"budget_exceeded\":"+(state.BudgetExceeded?"true":"false")+
            ",\"examples_truncated\":"+(records.Length>64?"true":"false")+",\"alternatives\":["+
            String.Join(",",records.Take(64).Select(r=>"{\"incident_packet_id\":"+r.IncidentId+",\"reflection_packet_id\":"+r.ReflectionId+
                ",\"reflection_raw_peak\":"+r.ReflectionPeak+",\"raw_support\":["+r.ReflectionStart+","+r.ReflectionEnd+
                "],\"selected_timing_delay\":"+r.Delay+",\"raw_signed_shape_diagnostic\":"+NumberJson(r.RawSignedShape)+
                ",\"raw_envelope_shape_diagnostic\":"+NumberJson(r.RawEnvelopeShape)+",\"missing_reason\":"+Json(r.MissingReason)+"}"))+"]}";
    }
}
