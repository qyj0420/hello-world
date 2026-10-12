using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Output retention only. Never append these packets to the matching input,
    // alter existing supports/ownership, or derive a reflection from this list.
    private sealed class A6RetentionInventory
    {
        internal readonly List<A4Pulse> Packets=new List<A4Pulse>();
        internal readonly Dictionary<int,string> Reasons=new Dictionary<int,string>();
        internal bool Incomplete,RawSeedCountComplete=true;
        internal string Reason="all_threshold_raw_seeds_accounted_for_without_extra_pairing";
        internal int RawSeedsAboveThreshold,RawExtractionSeeds,ProcessedRawSeeds,CoveredExistingSeeds,CoveredAddedSeeds,
            CanonicalAlreadyRepresentedSeeds;
        internal readonly int SafetyLimit=4096,RawSampleLimit=8388608,RawSeedLimit=16384;
    }

    private static bool A6RetentionCovered(IEnumerable<A4Pulse> packets,int index)
    {return packets.Any(p=>p!=null&&p.Start<=index&&p.End>=index);}

    private static string A6RetentionInventoryJson(A6RetentionInventory inventory)
    {
        if(inventory==null)return "{\"complete\":false,\"reason\":\"retention_inventory_not_run\"}";
        return "{\"scope\":\"output_only_original_packet_inventory_never_changes_matching\",\"complete\":"+(!inventory.Incomplete?"true":"false")+
            ",\"reason\":"+Json(inventory.Reason)+",\"raw_seed_count_complete\":"+(inventory.RawSeedCountComplete?"true":"false")+
            ",\"raw_seeds_at_effective_threshold\":"+inventory.RawSeedsAboveThreshold+",\"processed_raw_seeds\":"+inventory.ProcessedRawSeeds+
            ",\"raw_extraction_local_maxima_observed\":"+inventory.RawExtractionSeeds+
            ",\"covered_existing_support_seeds\":"+inventory.CoveredExistingSeeds+",\"covered_added_support_seeds\":"+inventory.CoveredAddedSeeds+
            ",\"secondary_seeds_with_already_represented_raw_canonical_packet\":"+inventory.CanonicalAlreadyRepresentedSeeds+
            ",\"additional_unpaired_raw_packets\":"+inventory.Packets.Count+",\"additional_packet_safety_limit\":"+inventory.SafetyLimit+
            ",\"raw_sample_safety_limit\":"+inventory.RawSampleLimit+",\"raw_seed_safety_limit\":"+inventory.RawSeedLimit+
            ",\"clipped_added_packets\":"+inventory.Packets.Count(p=>p.Clipped)+",\"overlap_or_unresolved_added_packets\":"+inventory.Packets.Count(p=>p.UnresolvedOverlap)+
            ",\"matching_pool_modified\":false,\"accepted_results_modified\":false,\"raw_samples_modified\":false,\"packet_count_is_not_confirmed_discharge_count\":true}";
    }

    private static A6RetentionInventory A6BuildRetentionInventory(A4Context c,IList<A4Pulse> existingPackets)
    {
        var result=new A6RetentionInventory();
        if(c==null||c.Raw==null||existingPackets==null||!Finite(c.Fs)||c.Fs<=0||c.RoundTrip<1)
        {result.Incomplete=true;result.RawSeedCountComplete=false;result.Reason="retention_invalid_original_context";return result;}
        double[] detection=A4RawAnalysis(c);
        if(detection==null||detection.Length!=c.Raw.Length||!Finite(c.RawCenter)||!Finite(c.Sigma)||c.Sigma<=0)
        {result.Incomplete=true;result.RawSeedCountComplete=false;result.Reason="retention_original_baseline_or_noise_unavailable";return result;}
        if(detection.Length>result.RawSampleLimit)
        {result.Incomplete=true;result.RawSeedCountComplete=false;result.Reason="retention_raw_sample_safety_limit_exceeded_no_claim_of_complete_inventory";return result;}
        double threshold=Math.Max(c.IncidentThreshold,c.RawDiscoveryThreshold);
        if(!Finite(threshold)||threshold<=0)
        {result.Incomplete=true;result.RawSeedCountComplete=false;result.Reason="retention_threshold_not_positive_finite";return result;}
        double discoveryThreshold=c.RawDiscoveryThreshold>0?c.RawDiscoveryThreshold:4.5*c.Sigma;

        // Count before allocating/sorting the seed array or extracting packets.
        // The exact same end-point/local-maximum predicate as A4AbsoluteSeeds is
        // used; an EOF spike stays visible as clipped, never a confirmed event.
        for(int i=0;i<detection.Length;i++)
        {
            double value=Math.Abs(detection[i]-c.RawCenter);
            if(!Finite(value))
            {result.Incomplete=true;result.RawSeedCountComplete=false;result.Reason="retention_nonfinite_original_sample";return result;}
            double before=i>0?Math.Abs(detection[i-1]-c.RawCenter):-1;
            double after=i+1<detection.Length?Math.Abs(detection[i+1]-c.RawCenter):-1;
            if(value>before&&value>=after)
            {
                if(value>=threshold)result.RawSeedsAboveThreshold++;
                if(value>=discoveryThreshold)result.RawExtractionSeeds++;
                if(result.RawExtractionSeeds>result.RawSeedLimit||result.RawSeedsAboveThreshold>result.RawSeedLimit)
                {result.Incomplete=true;result.RawSeedCountComplete=false;result.Reason="retention_raw_seed_safety_limit_exceeded_counts_are_lower_bounds_no_claim_of_complete_inventory";return result;}
            }
        }
        if(result.RawSeedsAboveThreshold==0)return result;
        List<int> seeds=A4AbsoluteSeeds(detection,c.RawCenter,threshold,0,detection.Length-1);
        // Independent context: no filtered discovery, CAL, scoring, pair count
        // limit, shared measurement cache or matching-side counter is touched.
        var rawContext=new A4Context {Raw=detection,Signal=detection,RawCenter=c.RawCenter,Center=c.RawCenter,
            Sigma=c.Sigma,Threshold=discoveryThreshold,RawDiscoveryThreshold=discoveryThreshold,Fs=c.Fs,
            RoundTrip=c.RoundTrip,RoundTripExact=c.RoundTripExact};
        List<A4Pulse> rawPackets=A4Extract(rawContext);
        var canonicalCoverageOnly=new List<A4Pulse>();
        int nextId=existingPackets.Where(p=>p!=null).Select(p=>p.Id).DefaultIfEmpty(-1).Max()+1;
        foreach(int seed in seeds)
        {
            if(A6RetentionCovered(existingPackets,seed))
            {result.ProcessedRawSeeds++;result.CoveredExistingSeeds++;continue;}
            if(A6RetentionCovered(result.Packets,seed))
            {result.ProcessedRawSeeds++;result.CoveredAddedSeeds++;continue;}
            if(A6RetentionCovered(canonicalCoverageOnly,seed))
            {result.ProcessedRawSeeds++;result.CanonicalAlreadyRepresentedSeeds++;continue;}

            A4Pulse source=rawPackets.FirstOrDefault(p=>p.Start<=seed&&p.End>=seed)
                ??A4SeededPacket(rawContext,seed,-1);
            if(source.Start<0||source.End>=detection.Length||source.End<source.Start)
            {result.Incomplete=true;result.Reason="retention_original_support_unavailable_no_arbitrary_single_peak_substitution";break;}
            int canonical=AbsoluteMaximumIndex(detection,source.Start,source.End,c.RawCenter);
            // A longer independently recovered RAW support may expose additional
            // lobes of an already represented canonical packet. Do not turn that
            // same dominant event into a second row with a weaker anchor.
            if(A6RetentionCovered(existingPackets,canonical)||A6RetentionCovered(result.Packets,canonical))
            {canonicalCoverageOnly.Add(source);result.ProcessedRawSeeds++;result.CanonicalAlreadyRepresentedSeeds++;continue;}
            if(result.Packets.Count>=result.SafetyLimit)
            {result.Incomplete=true;result.Reason="retention_additional_packet_safety_limit_exceeded_no_claim_of_complete_inventory";break;}
            bool overlaps=existingPackets.Concat(result.Packets).Any(p=>p!=null&&p.Start<=source.End&&p.End>=source.Start);
            var packet=new A4Pulse {Id=nextId++,Start=source.Start,End=source.End,Peak=canonical,RawPeak=canonical,
                RawAmplitudeIndex=canonical,Amplitude=A4RawCharge(c,canonical),Baseline=c.RawCenter,NoiseSigma=c.Sigma,
                Snr=A4RawCharge(c,canonical)/c.Sigma,Period=source.Period,Clipped=source.Clipped,
                UnresolvedOverlap=source.UnresolvedOverlap||overlaps,MappingValid=true,TriggeredFromRaw=true,
                Energy=source.Energy,EquivalentSamples=source.EquivalentSamples,BandwidthHz=source.BandwidthHz,
                HighFraction=source.HighFraction,LocalAmplitude=source.LocalAmplitude};
            result.Packets.Add(packet);
            result.Reasons[packet.Id]=packet.Clipped?"unpaired_retained_original_packet_clipped_not_analyzed":
                packet.UnresolvedOverlap?"unpaired_retained_original_support_overlap_or_unresolved_not_resegmented_not_analyzed":
                "unpaired_retained_full_record_original_packet_not_submitted_to_matching";
            result.ProcessedRawSeeds++;
        }
        return result;
    }
}
