using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private string a4ReviewReflectionExpansionDiagnosticJson="{\"enabled\":false,\"reason\":\"not_run\"}";

    // This is a reflection-only inventory for the explicitly separate review
    // pass. Existing packet objects, visits, ownership and primary graph scores
    // are read-only. In particular, a packet discovered as somebody else's
    // unaccepted reflection is not proof that its own next interval was searched.
    private List<A4Pulse> A4ExpandReviewReflections(A4Context c,List<A4Pulse> pulses,
        HashSet<int> visited,HashSet<int> represented)
    {
        var result=pulses==null?new List<A4Pulse>():new List<A4Pulse>(pulses);
        if(c==null||!c.ReviewQuietGuardRecovery||(c.Algorithm!=4&&c.Algorithm!=5)||
            pulses==null||visited==null||represented==null||c.Raw==null||c.Raw.Length<3||
            c.RoundTrip<=0||!Finite(c.Sigma)||c.Sigma<=0)
        {
            a4ReviewReflectionExpansionDiagnosticJson="{\"enabled\":false,\"reason\":\"review_raw_inventory_context_unavailable\"}";
            return result;
        }
        if(c.WindowBudgetExceeded||c.CandidateEvaluationLimitExceeded||c.CandidatePoolLimitExceeded)
        {
            a4ReviewReflectionExpansionDiagnosticJson="{\"enabled\":true,\"reason\":\"primary_budget_incomplete_no_expansion\",\"added_reflections\":0}";
            return result;
        }
        double[] detection=A4RawAnalysis(c);
        int nextId=pulses.Select(p=>p.Id).DefaultIfEmpty(-1).Max();
        if(nextId==int.MaxValue)return result;
        nextId++;
        var seeds=pulses.Where(p=>p.Owner<0&&visited.Contains(p.Id)&&!represented.Contains(p.Id)&&
                p.MappingValid&&!p.Clipped&&A4ChargeIndex(p)>=0&&A4ChargeIndex(p)<detection.Length&&
                A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold)
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex)
            .Take(Math.Max(0,Math.Min(c.AnalysisLimit,visited.Count))).ToList();
        int remaining=Math.Max(0,c.CandidateEvaluationLimit-c.CandidateEvaluations);
        int windows=0,examined=0;
        bool incomplete=false;
        var records=new List<string>();
        foreach(A4Pulse source in seeds)
        {
            if(windows>=4096||examined>=remaining){incomplete=true;break;}
            int seed=A4ChargeIndex(source),guard=Math.Max(128,c.RoundTrip/2);
            // Physical discovery context is independent of output pre/post crop.
            // Only candidate peaks inside one RT plus the already-existing
            // uncertainty envelope are eligible; the longer context is for
            // complete bipolar supports and original noise guards only.
            int start=(int)Math.Max(0L,(long)seed-c.RoundTrip-guard);
            int end=(int)Math.Min(detection.Length-1L,(long)seed+Math.Max(3L*c.RoundTrip,A4ReflectionMaximumDelay(c))+guard);
            int broadLimit=A4ReflectionMaximumDelay(c);
            double[] raw=A4Slice(detection,start,end);double rawCenter=Median(raw);
            var rawContext=new A4Context {Raw=raw,Signal=raw,RawCenter=rawCenter,Center=rawCenter,
                Sigma=c.Sigma,Threshold=4.5*c.Sigma,RawDiscoveryThreshold=c.RawDiscoveryThreshold,
                Fs=c.Fs,RoundTrip=c.RoundTrip,RoundTripExact=c.RoundTripExact};
            List<A4Pulse> rawPackets=A4Extract(rawContext);
            windows++;
            // Filtered measurement is attached only after raw candidates and
            // their canonical identities exist. Never use its reduced amplitude
            // as the incident threshold or reselect the source peak.
            double[] signal=c.Filtered?A4ZeroPhase(raw,c.Fs,c.EffectiveCutoffs):(double[])raw.Clone();
            double center=c.Filtered?Median(signal):rawCenter;
            double noise=c.Filtered?Math.Max(1e-15,Math.Max(RobustSigma(signal,center),signal.Max(v=>Math.Abs(v-center))*1e-8)):c.Sigma;
            var local=new A4Context {Raw=raw,Signal=signal,RawCenter=rawCenter,Center=center,Sigma=noise,
                Threshold=4.5*noise,RawDiscoveryThreshold=c.RawDiscoveryThreshold,Fs=c.Fs,
                RoundTrip=c.RoundTrip,RoundTripExact=c.RoundTripExact,Filtered=c.Filtered,
                FirstArrival=false,Attenuation=c.Attenuation,Width=c.Width,
                Bands=c.Bands,LowBand=c.LowBand,Calibration=c.Calibration};
            var mapped=new List<A4Pulse>();
            foreach(A4Pulse rawPacket in rawPackets)
            {
                rawPacket.RawAmplitudeIndex=AbsoluteMaximumIndex(raw,rawPacket.Start,rawPacket.End,rawCenter);
                mapped.Add(A4PacketWithinRawSupport(local,rawPacket));
            }
            A4WindowGuards(local,mapped);A4PrepareLowBandPackets(local,mapped);
            var attachedLow=new HashSet<A4Pulse>();
            foreach(A4Pulse packet in mapped.OrderBy(p=>p.Start).ThenBy(A4ChargeIndex))
            {
                int peak=A4ChargeIndex(packet)+start,delay=peak-seed;
                if(delay<=0||delay>broadLimit||packet.Start+start<=source.End)continue;
                if(examined>=remaining){incomplete=true;break;}
                examined++;
                if(packet.Clipped||!packet.MappingValid||
                    A4RawCharge(c,peak)<Math.Max(c.RawDiscoveryThreshold,4.5*c.Sigma)||
                    delay>A4ReviewDelayLimit(c,source,packet))continue;
                int globalStart=packet.Start+start,globalEnd=packet.End+start;
                // Keep every existing identity/support authoritative. A new
                // subordinate lobe inside an old packet is not a new reflection.
                if(result.Any(p=>A4ChargeIndex(p)==peak||p.Start<=globalEnd&&p.End>=globalStart))continue;
                if(nextId==int.MaxValue){incomplete=true;break;}
                if(packet.LowBandPulse!=null&&attachedLow.Add(packet.LowBandPulse))
                    A4AttachWindow(packet.LowBandPulse,local.LowBandSignal,start);
                A4AttachWindow(packet,signal,start);
                packet.Id=nextId++;packet.Owner=-1;packet.AttenuationMeasurement=null;
                result.Add(packet);
                records.Add("{\"source_id\":"+source.Id+",\"source_raw_peak\":"+seed+
                    ",\"reflection_id\":"+packet.Id+",\"reflection_raw_peak\":"+peak+
                    ",\"raw_start\":"+packet.Start+",\"raw_end\":"+packet.End+
                    ",\"canonical_delay\":"+delay+",\"raw_charge_pc\":"+NumberJson(A4RawCharge(c,peak))+
                    ",\"unresolved_support\":"+(packet.UnresolvedOverlap?"true":"false")+"}");
            }
            if(incomplete)break;
        }
        // A partial new search cannot silently appear complete. Preserve the
        // original pool and report the bounded diagnostic instead of publishing
        // whichever reflection happened to be visited before budget exhaustion.
        if(incomplete)result=new List<A4Pulse>(pulses);
        c.CandidateEvaluations+=examined;
        a4ReviewReflectionExpansionDiagnosticJson="{\"enabled\":true,\"reason\":\""+
            (incomplete?"review_raw_inventory_budget_incomplete_no_expansion":"visited_unrepresented_incident_raw_reflection_inventory")+
            "\",\"source_count\":"+seeds.Count+",\"windows\":"+windows+
            ",\"candidates_examined\":"+examined+",\"remaining_candidate_budget\":"+remaining+
            ",\"added_reflections\":"+(result.Count-pulses.Count)+
            ",\"adds_incident_visits\":false,\"commits_owners\":false,\"changes_primary_graph\":false"+
            ",\"output_crop_used\":false,\"candidate_peak_scope\":\"one_roundtrip_plus_existing_review_uncertainty\""+
            ",\"proposed_reflections\":["+String.Join(",",records)+"]}";
        return result;
    }
}
