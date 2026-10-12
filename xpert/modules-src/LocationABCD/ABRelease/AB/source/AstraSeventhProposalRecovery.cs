using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;
internal sealed partial class LocationEngine
{
    private readonly List<string> a7WaveformVetoDiagnostics=new List<string>();
    // Every complete supplemental edge is retained for the method-7 causal
    // graph. A temporary display winner is not a committed packet owner.
    private readonly Dictionary<A4Pair,SeventhProposalEvidence> a7ProposalEvidence=new Dictionary<A4Pair,SeventhProposalEvidence>();
    // A completed primary CAL catalogue is not proof that the supplemental
    // LOC catalogue is complete. Any unscanned prefix/physical window blocks
    // global uniqueness; this flag is consumed by the causal graph bridge.
    private bool a7ProposalCatalogueComplete;
    private string a7CalibrationCapabilitiesJson="{}";
    private double a7ProposalExpectedRetention=double.NaN;
    private bool a7CalibrationTemplateReliable,a7CalibrationAttenuationReliable;
    private SeventhProposalEvidence A7MeasuredProposal(A4Context c,int incident,int reflection,double floor)
    {
        double expected=a7ProposalExpectedRetention;
        return SeventhProposalMath.Evaluate(c.MeasurementSignal.DetectionRaw,c.MeasurementSignal.BandSignal,
            incident,reflection,c.MeasurementSignal.DetectionCenter,c.MeasurementSignal.RawSigma,c.Fs,floor,expected,c.Raw);
    }
    private string A7ProposalEvidenceJson(SeventhProposalEvidence e)
    {
        return "{\"reflection\":"+e.Peak+",\"passed\":"+(e.Passed?"true":"false")+",\"reason\":"+Json(e.Reason)+
            ",\"raw_band_strong_polarity_conflict\":"+(e.FilterPolarityConflict?"true":"false")+
            ",\"core_half_samples\":"+e.CoreHalf+",\"long_half_samples\":"+e.LongHalf+",\"short_context\":"+(e.ShortContext?"true":"false")+
            ",\"core_signed_correlation\":"+NumberJson(e.CoreCorrelation)+",\"long_signed_correlation\":"+NumberJson(e.LongCorrelation)+
            ",\"raw_main_lobe_correlation\":"+NumberJson(e.RawCorrelation)+",\"correlation\":"+NumberJson(e.Correlation)+
            ",\"engineering_score\":"+NumberJson(e.Score)+",\"raw_amplitude_ratio\":"+NumberJson(e.AmplitudeRatio)+
            ",\"incident_main_left\":"+(e.Incident==null?-1:e.Incident.Left)+",\"incident_main_right\":"+(e.Incident==null?-1:e.Incident.Right)+
            ",\"reflection_main_left\":"+(e.Reflection==null?-1:e.Reflection.Left)+",\"reflection_main_right\":"+(e.Reflection==null?-1:e.Reflection.Right)+
            ",\"reflection_canonical_raw_peak\":"+(e.Reflection==null?-1:e.Reflection.CanonicalPeak)+
            ",\"incident_raw_snr\":"+NumberJson(e.Incident==null?double.NaN:e.Incident.Snr)+
            ",\"reflection_raw_snr\":"+NumberJson(e.Reflection==null?double.NaN:e.Reflection.Snr)+
            ",\"reflection_quiet_left\":"+(e.Reflection!=null&&e.Reflection.QuietLeft?"true":"false")+
            ",\"reflection_quiet_right\":"+(e.Reflection!=null&&e.Reflection.QuietRight?"true":"false")+"}";
    }
    private void A7RecoverWindowProposals(A4Context c,List<A4Pulse> pulses,List<A4Pulse> incidents,List<A4Pair> all,List<A4Pair> output,double floor,HashSet<int> visitedPeaks)
    {
        a7WindowProposalDiagnostics.Clear();
        a7ProposalEvidence.Clear();
        a7ProposalCatalogueComplete=true;
        if(c.WindowBudgetExceeded||c.CandidateEvaluationLimitExceeded)
        {a7ProposalCatalogueComplete=false;a7WindowProposalDiagnostics.Add("{\"reason\":\"incomplete_primary_catalogue_no_supplemental_prefix_winner\"}");return;}
        double lower,upper;if(!A4ReflectionWindow(c,out lower,out upper)){a7ProposalCatalogueComplete=false;return;}
        var raw=c.MeasurementSignal.DetectionRaw;var band=c.MeasurementSignal.BandSignal;
        double center=c.MeasurementSignal.DetectionCenter,noise=c.MeasurementSignal.RawSigma;
        // Primary accepted/review edges are provisional until the complete
        // causal graph is resolved. Neither endpoint may block a stronger
        // LOC-LOC alternative merely because a greedy pair was displayed first.
        var packets=pulses.GroupBy(A4ChargeIndex).ToDictionary(g=>g.Key,g=>g.First());
        int nextId=int.MaxValue;
        var canonicalIncidents=new List<A4Pulse>();
        foreach(var original in incidents)
        {
            int sourcePeak=A4ChargeIndex(original);
            // A detector may legitimately retain the first/last stored sample
            // for an unpaired diagnostic row. It cannot have two measured raw
            // main-lobe crossings, so it is ineligible, NOT a truncated search
            // of otherwise eligible candidates. Do not invalidate the record.
            if(sourcePeak==0||sourcePeak==raw.Length-1)
            {
                a7WindowProposalDiagnostics.Add("{\"incident\":"+sourcePeak+",\"reason\":\"record_boundary_incident_has_no_two_sided_lobe_ineligible_not_catalogue_gap\",\"source_sample_count\":"+raw.Length+"}");
                continue;
            }
            int canonical=SeventhProposalMath.CanonicalPeak(raw,sourcePeak,center,noise,c.Fs,c.Raw);
            if(canonical<=0||canonical>=raw.Length-1)
            {a7ProposalCatalogueComplete=false;a7WindowProposalDiagnostics.Add("{\"incident\":"+sourcePeak+",\"reason\":\"internal_canonical_resolution_failed_candidate_catalogue_incomplete\",\"complete_candidate_scan\":false}");continue;}
            // Eligibility belongs to the real canonical peak, not to the
            // detector's smaller shoulder. A missing sibling is materialized
            // explicitly as a new supplemental packet, leaving primary pairs
            // and their original identities unchanged for audit.
            if(A4RawCharge(c,canonical)<c.IncidentThreshold||A4RawCharge(c,canonical)<=0||!A4AnalysisPhaseEligible(c,canonical))
            {
                if(canonical!=sourcePeak)a7WindowProposalDiagnostics.Add("{\"incident\":"+sourcePeak+",\"canonical_incident\":"+canonical+",\"reason\":\"canonical_incident_threshold_or_phase_ineligible\"}");
                continue;
            }
            A4Pulse packet=original;
            if(canonical!=sourcePeak)
            {
                if(!packets.TryGetValue(canonical,out packet))
                {
                    var lobe=SeventhProposalMath.Measure(raw,canonical,center,noise,c.Fs,c.Raw);
                    packet=new A4Pulse {Id=nextId--,Start=lobe.Left,End=lobe.Right,Peak=canonical,RawPeak=canonical,
                        RawAmplitudeIndex=canonical,MappingValid=lobe.Complete,Amplitude=A4RawCharge(c,canonical)};
                    packets.Add(canonical,packet);
                }
                a7WindowProposalDiagnostics.Add("{\"incident\":"+sourcePeak+",\"canonical_incident\":"+canonical+",\"reason\":\"supplemental_canonical_incident_materialized_from_detector_shoulder\",\"primary_identity_rewritten\":false}");
            }
            canonicalIncidents.Add(packet);
        }
        var supplementalVisited=new HashSet<int>();
        foreach(var incident in canonicalIncidents.GroupBy(A4ChargeIndex).Select(g=>g.First())
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex))
        {
            int peak=A4ChargeIndex(incident);
            if(peak<=0||peak>=raw.Length-1)continue;
            if(!supplementalVisited.Add(peak))continue;
            // Count REAL supplemental work under the same depth budget. Primary
            // CAL scoring may have rejected a packet, but that does not erase the
            // subsequent LOC-LOC visit or make it an unvisited budget casualty.
            if(!visitedPeaks.Contains(peak)&&visitedPeaks.Count>=c.AnalysisLimit)
            {a7ProposalCatalogueComplete=false;a7WindowProposalDiagnostics.Add("{\"incident\":"+peak+",\"reason\":\"real_canonical_packet_analysis_depth_limit\"}");break;}
            visitedPeaks.Add(peak);
            if(!c.AnalysisVisitIds.Contains(incident.Id))c.AnalysisVisitIds.Add(incident.Id);
            var incidentLobe=SeventhProposalMath.Measure(raw,peak,center,noise,c.Fs);
            int anchor=A4RequestedTiming(c,incident,peak,a7FirstArrival),required,valid;
            if(anchor<0||!A4PdmapCoverage(c,anchor,out required,out valid))
            {a7WindowProposalDiagnostics.Add("{\"incident\":"+peak+",\"reason\":\"supplemental_timing_or_coverage_ineligible_no_legal_pair\"}");continue;}
            int first=(int)Math.Max(peak+1,(long)anchor+(long)Math.Ceiling(Math.Max(1,lower)));
            long lastPeak=(long)anchor+(long)Math.Floor(Math.Min(upper,c.RoundTripExact));
            var candidatePackets=new Dictionary<int,A4Pulse>();var candidateTimings=new Dictionary<int,int>();
            Func<int,A4Pulse> packetFor=index=>
            {
                A4Pulse packet;if(candidatePackets.TryGetValue(index,out packet))return packet;
                if(!packets.TryGetValue(index,out packet))
                {
                    var lobe=SeventhProposalMath.Measure(raw,index,center,noise,c.Fs);
                    packet=new A4Pulse {Id=-1,Start=lobe.Left,End=lobe.Right,Peak=index,RawPeak=index,
                        RawAmplitudeIndex=index,MappingValid=lobe.Complete,Amplitude=A4RawCharge(c,index)};
                }
                candidatePackets.Add(index,packet);return packet;
            };
            Func<int,int> timingFor=index=>
            {
                int time;if(candidateTimings.TryGetValue(index,out time))return time;
                time=A4RequestedTiming(c,packetFor(index),index,a7FirstArrival);candidateTimings.Add(index,time);return time;
            };
            if(a7FirstArrival)
            {
                // Only measured packets may extend the peak search for a legal
                // leading edge. No synthetic peak +/-1 or 25-sample allowance.
                long lastAnchor=lastPeak;
                foreach(var item in packets)
                {
                    if(item.Key<=lastAnchor||item.Key<0||item.Key>=raw.Length||item.Value.Start>lastAnchor)continue;
                    int time=timingFor(item.Key),delay=time-anchor;
                    if(time>=0&&delay>0&&delay<=c.RoundTripExact&&A4ReflectionDelayWithinWindow(c,delay)&&A4BoundaryTimingValid(c,delay))lastPeak=Math.Max(lastPeak,item.Key);
                }
            }
            int last=(int)Math.Min(raw.Length-2L,lastPeak);
            double expected=a7ProposalExpectedRetention;
            var search=SeventhProposalMath.Find(raw,band,peak,first,last,center,noise,c.Fs,floor,expected,index=>
            {
                int time=timingFor(index),delay=time-anchor;
                return time<0||delay<=0||delay>c.RoundTripExact||!A4ReflectionDelayWithinWindow(c,delay)||!A4BoundaryTimingValid(c,delay);
            },c.Raw);
            c.CandidateEvaluations+=search.Evaluated;
            if(c.CandidateEvaluations>c.CandidateEvaluationLimit)
            {c.CandidateEvaluationLimitExceeded=true;search.Complete=false;search.Best=null;search.Reason="total_candidate_budget_no_prefix_winner";}
            if(!search.Complete)a7ProposalCatalogueComplete=false;
            // Only this incident's graph component becomes position-undecided.
            // Keep failed reflections failed, preserve their full diagnostics,
            // and do not mark an otherwise completely scanned record incomplete.
            int[] filterConflictPeaks=search.Candidates.Where(e=>e.FilterPolarityConflict).Select(e=>e.Peak).Distinct().OrderBy(v=>v).ToArray();
            int[] unresolvedPeaks=search.IncompleteCompetitorPeaks.Concat(filterConflictPeaks).Distinct().OrderBy(v=>v).ToArray();
            if(unresolvedPeaks.Length>0)
            {
                int[] existing;
                if(a7UnknownCompetitorPeaks.TryGetValue(peak,out existing))unresolvedPeaks=existing.Concat(unresolvedPeaks).Distinct().OrderBy(v=>v).ToArray();
                a7UnknownCompetitorPeaks[peak]=unresolvedPeaks;
            }
            string diagnostic="{\"incident\":"+peak+",\"reason\":"+Json(search.Reason)+",\"correlation_floor\":"+NumberJson(floor)+
                ",\"complete_candidate_scan\":"+(search.Complete?"true":"false")+",\"evaluated_count\":"+search.Evaluated+",\"qualified_count\":"+search.Qualified+
                ",\"observed_raw_extrema\":"+search.ObservedExtrema+",\"merged_same_lobe_extrema\":"+search.MergedExtrema+",\"candidate_unit\":\"canonical_raw_main_lobe_packet\""+
                ",\"incomplete_competitors\":"+search.IncompleteCompetitors+",\"ambiguous\":"+(search.Ambiguous?"true":"false")+
                ",\"raw_band_conflict_competitor_peaks\":"+NumberArrayJson(filterConflictPeaks.Select(v=>(double)v).ToArray())+
                ",\"incomplete_measurement_competitor_peaks\":"+NumberArrayJson(search.IncompleteCompetitorPeaks.Select(v=>(double)v).ToArray())+
                ",\"propagated_unknown_competitor_peaks\":"+NumberArrayJson(unresolvedPeaks.Select(v=>(double)v).ToArray())+
                ",\"window_first\":"+first+",\"window_last\":"+last+",\"candidate_evidence\":["+String.Join(",",search.Candidates.Select(A7ProposalEvidenceJson))+"]";
            if(!search.Complete||search.Best==null)
            {a7WindowProposalDiagnostics.Add(diagnostic+"}");if(c.CandidateEvaluationLimitExceeded)break;continue;}
            A4Pair temporaryBest=null;int bestTiming=-1,graphCandidates=0;
            foreach(var proposal in search.Candidates.Where(e=>e.Passed))
            {
                int reflectionPeak=proposal.Peak;var reflection=packetFor(reflectionPeak);
                if(!packets.ContainsKey(reflectionPeak)&&reflection.Id==-1)reflection.Id=nextId--;
                int reflectionTiming=timingFor(reflectionPeak),candidateDelay=reflectionTiming-anchor;
                double position=A4BoundaryOutputPosition(candidateDelay,c.Fs,c.Joints,c.Velocities,c.Length);
                if(!Finite(position)||position<0||position>c.Length)continue;
                var result=new SeventhPipelineResult {Available=true,ReviewHypothesis=true,AutoAccept=false,CandidatePassed=false,
                    Score=proposal.Score,RawPositionM=position,ProjectedPositionM=position,Reasons=new[]{proposal.Reason,"complete_canonical_packet_candidate_for_causal_graph","CAL_template_similarity_is_auxiliary_review_only"}};
                var pair=new A4Pair {Incident=incident,Reflection=reflection,OutputIncident=peak,OutputReflection=reflectionPeak,
                    EdgeId=all.Count,State="review",Position=position,Correlation=proposal.Correlation,WaveCorrelation=proposal.Correlation,
                    Confidence=Math.Min(.49,proposal.Score),AmplitudeRatio=proposal.AmplitudeRatio,CandidateCount=search.Qualified,Ambiguous=search.Ambiguous,
                    ReviewReason=proposal.Reason+";manual_confirmation_required",OutputTimingReason=a7FirstArrival?
                        "hard_window_measured_first_arrival_multiscale_review":"hard_window_raw_main_lobe_multiscale_review"};
                all.Add(pair);a7PipelineResults.Add(pair,result);a7OutputTimings.Add(pair,Tuple.Create(anchor,reflectionTiming));
                a7ProposalEvidence.Add(pair,proposal);graphCandidates++;
                A7ApplyTimingUncertainty(c,pair,result,reflectionPeak,anchor,reflectionTiming);
                if(Object.ReferenceEquals(proposal,search.Best)){temporaryBest=pair;bestTiming=reflectionTiming;}
            }
            if(temporaryBest!=null)output.Add(temporaryBest);
            var best=search.Best;
            a7WindowProposalDiagnostics.Add(diagnostic+",\"reflection\":"+best.Peak+",\"correlation\":"+NumberJson(best.Correlation)+
                ",\"requested_incident_timing_anchor\":"+anchor+",\"requested_reflection_timing_anchor\":"+bestTiming+
                ",\"engineering_score\":"+NumberJson(best.Score)+",\"confidence\":"+NumberJson(temporaryBest==null?double.NaN:temporaryBest.Confidence)+
                ",\"graph_candidates_retained\":"+graphCandidates+",\"temporary_display_winner_not_packet_owner\":true"+
                ",\"second_score\":"+NumberJson(search.Second==null?double.NaN:search.Second.Score)+",\"candidate_count\":"+search.Qualified+",\"automatic_acceptance\":false}");
        }
    }
    private void A7ApplyFinalWaveformEvidence(A4Context c,List<A4Pair> output,double floor)
    {
        foreach(var pair in output.ToArray())
        {
            var evidence=A7MeasuredProposal(c,pair.OutputIncident,pair.OutputReflection,floor);
            Tuple<int,int> timing;bool timingKnown=a7OutputTimings.TryGetValue(pair,out timing);
            double position=timingKnown?A4BoundaryOutputPosition(timing.Item2-timing.Item1,c.Fs,c.Joints,c.Velocities,c.Length):double.NaN;
            bool nominal=timingKnown&&timing.Item2>timing.Item1&&timing.Item2-timing.Item1<=c.RoundTripExact&&Finite(position)&&position>=0&&position<=c.Length;
            if(evidence.Passed&&nominal)continue;
            output.Remove(pair);a7IndependentSeedPairs.Remove(pair);
            // A review edge may share a packet with a later accepted edge. Never
            // erase the other edge's ownership while vetoing this hypothesis.
            if(pair.Incident.Owner==pair.EdgeId)pair.Incident.Owner=-1;
            if(pair.Reflection.Owner==pair.EdgeId)pair.Reflection.Owner=-1;
            a7WaveformVetoDiagnostics.Add("{\"incident\":"+pair.OutputIncident+",\"reflection\":"+pair.OutputReflection+
                ",\"nominal_physical_window\":"+(nominal?"true":"false")+",\"final_waveform_veto\":"+A7ProposalEvidenceJson(evidence)+"}");
        }
    }
}
