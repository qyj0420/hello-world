using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;
internal sealed partial class LocationEngine
{
    private readonly List<string> a7WindowProposalDiagnostics=new List<string>();
    private void A7RecoverWindowProposals(A4Context c,List<A4Pulse> pulses,List<A4Pulse> incidents,List<A4Pair> all,List<A4Pair> output)
    {
        a7WindowProposalDiagnostics.Clear();
        double lower,upper;if(!A4ReflectionWindow(c,out lower,out upper))return;
        var used=new HashSet<int>(output.SelectMany(p=>new[]{A4ChargeIndex(p.Incident),A4ChargeIndex(p.Reflection)}));
        // Supplemental inventory receives small positive IDs later. Reserve a
        // disjoint identity range so proposals cannot hide unrelated packets.
        int nextId=int.MaxValue;
        foreach(var incident in incidents)
        {
            if(output.Count>=c.AnalysisLimit)break;
            int peak=A4ChargeIndex(incident);if(used.Contains(peak))continue;
            int anchor=peak;
            anchor=A4RequestedTiming(c,incident,peak,a7FirstArrival);if(anchor<0)continue;
            int required,valid;if(!A4PdmapCoverage(c,anchor,out required,out valid))continue;
            int first=(int)Math.Max(peak+1,(long)anchor+(long)Math.Ceiling(lower));
            int last=(int)Math.Min(c.Raw.Length-1,(long)anchor+(long)Math.Floor(upper));
            var reference=c.AttenuationScoringReference;
            double expected=reference!=null&&reference.Valid?reference.RawRetention:double.NaN;
            var proposal=WindowProposalMath.Find(c.Raw,c.MeasurementSignal.BandSignal,peak,first,last,
                Math.Max(8,Math.Min(64,c.RoundTrip/32)),A4RawBaselineAt(c,peak),c.Sigma,expected,
                index=>used.Any(other=>Math.Abs((long)index-other)<=2));
            if(proposal.Peak<0){a7WindowProposalDiagnostics.Add("{\"incident\":"+peak+",\"reason\":\"no_finite_independent_extremum_inside_window\"}");continue;}
            int reflectionPeak=proposal.Peak;
            var reflection=pulses.FirstOrDefault(p=>A4ChargeIndex(p)==reflectionPeak)??new A4Pulse {
                Id=nextId--,Start=reflectionPeak-1,End=reflectionPeak+1,Peak=reflectionPeak,RawPeak=reflectionPeak,
                RawAmplitudeIndex=reflectionPeak,MappingValid=true,Amplitude=A4RawCharge(c,reflectionPeak)};
            int reflectionTiming=A4RequestedTiming(c,reflection,reflectionPeak,a7FirstArrival),delay=reflectionTiming-anchor;
            if(reflectionTiming<0||delay<=0||!A4ReflectionDelayWithinWindow(c,delay)||!A4BoundaryTimingValid(c,delay))continue;
            double position=A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length);
            var result=new SeventhPipelineResult {Available=true,ReviewHypothesis=true,AutoAccept=false,CandidatePassed=false,
                Score=proposal.Score,RawPositionM=position,ProjectedPositionM=BoundaryConfidenceMath.ProjectEndpointPosition(position,c.Length),
                Reasons=new[]{"bounded_window_extremum_proposal_requires_manual_review"}};
            var pair=new A4Pair {Incident=incident,Reflection=reflection,OutputIncident=peak,OutputReflection=reflectionPeak,
                EdgeId=all.Count,State="review",Position=result.ProjectedPositionM,Correlation=proposal.Correlation,WaveCorrelation=proposal.Correlation,
                Confidence=Math.Min(.49,proposal.Score),AmplitudeRatio=proposal.AmplitudeRatio,CandidateCount=proposal.Candidates,
                ReviewReason="bounded_window_extremum_proposal_requires_manual_review",OutputTimingReason="hard_window_measured_raw_extremum"};
            all.Add(pair);output.Add(pair);a7PipelineResults.Add(pair,result);a7OutputTimings.Add(pair,Tuple.Create(anchor,reflectionTiming));
            used.Add(peak);used.Add(reflectionPeak);
            a7WindowProposalDiagnostics.Add("{\"incident\":"+peak+",\"reflection\":"+reflectionPeak+",\"correlation\":"+NumberJson(proposal.Correlation)+
                ",\"engineering_score\":"+NumberJson(proposal.Score)+",\"confidence\":"+NumberJson(pair.Confidence)+",\"candidate_count\":"+proposal.Candidates+
                ",\"automatic_acceptance\":false,\"window_first\":"+first+",\"window_last\":"+last+"}");
        }
    }
}
