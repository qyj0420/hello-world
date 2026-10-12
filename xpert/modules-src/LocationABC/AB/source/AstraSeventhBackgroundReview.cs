using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private readonly List<string> a7BackgroundProofDiagnostics=new List<string>();

    private bool A7OutputTimingInsideWindow(A4Context c,A4Pair pair)
    {
        Tuple<int,int> timing;
        if(c==null||c.Raw==null||pair==null||!a7OutputTimings.TryGetValue(pair,out timing))return false;
        int a=timing.Item1,b=timing.Item2;
        return a>=0&&b>a&&b<c.Raw.Length&&Finite(c.Raw[a])&&Finite(c.Raw[b])&&
            A4ReflectionDelayWithinWindow(c,b-a)&&A4BoundaryTimingValid(c,b-a);
    }

    private static bool A7ScorableReview(SeventhPipelineInput x,SeventhPipelineResult r)
    {
        return r.Available&&Finite(r.Score)&&r.Score>0&&r.Score<=1&&
            x.GeometryValid&&x.ReflectionWindowValid&&x.PhysicalCoverageValid&&
            x.IndependentPackets&&x.SamePolarity&&x.CalibrationSamePolarity&&
            Finite(x.PairCorrelation)&&x.PairCorrelation>0&&
            Finite(x.IncidentCalibrationCorrelation)&&x.IncidentCalibrationCorrelation>=x.CorrelationFloor;
    }

    // Review ownership is resolved between depth batches to refund echo slots,
    // and once after ambiguity resolution. Recovered edges never become seeds.
    private void A7RecoverBackgroundReview(A4Context c,List<A4Pulse> pulses,A6Settings settings,
        List<A4Pulse> incidents,Dictionary<int,A7PipelinePulse> originalCache,
        A7PipelinePulse calI,A7PipelinePulse calR,bool calibrationReady,
        List<A4Pair> all,List<A4Pair> output,double floor)
    {
        if(!SeventhMode||!calibrationReady||c.WindowBudgetExceeded||c.CandidateEvaluationLimitExceeded)return;
        // The public seventh facade reuses the sixth raw input context (index 5).
        // Mark ONLY this private read-only copy index 6; never mutate the primary.
        var fallbackContext=A4ReviewContext(c);fallbackContext.Algorithm=6;
        var protectedIds=new HashSet<int>(output.SelectMany(p=>new[]{p.Incident.Id,p.Reflection.Id}));
        double lower,upper;if(!A4ReflectionWindow(c,out lower,out upper))return;
        var bounds=incidents.Where(p=>!protectedIds.Contains(p.Id)).Select(p=>{
            int anchor=A4ChargeIndex(p);
            anchor=A4RequestedTiming(c,p,anchor,a7FirstArrival);
            return Tuple.Create(p,anchor);
        }).Where(b=>b.Item2>=0).ToArray();
        // Membership uses only the already capped primary incident inventory
        // and physical windows, never a measured score or expected location.
        var eligible=pulses.Where(p=>!protectedIds.Contains(p.Id)&&bounds.Any(b=>
            p.Id==b.Item1.Id||(p.Start>b.Item1.End&&p.End>=b.Item2+Math.Ceiling(lower)&&p.Start<=b.Item2+Math.Floor(upper)))).ToArray();
        if(eligible.Length>512)
        {a7BackgroundProofDiagnostics.Add("{\"BackgroundFallbackUsed\":false,\"reason\":\"proof_inventory_exceeds_512_no_prefix_recovery\"}");return;}
        var recovered=new HashSet<int>();
        var measurements=new Dictionary<int,A7PipelinePulse>();
        // All raw proofs are frozen before ANY fallback candidate scoring.
        foreach(var p in eligible)
        {
            A7PipelinePulse original;
            if(!originalCache.TryGetValue(p.Id,out original))
            {A6OriginalGuardTrace trace;original=A7PipelineMeasure(c.Raw,A6ScoringMeasurementCore(c,p,out trace),c.Fs);}
            measurements[p.Id]=original;
            if(protectedIds.Contains(p.Id)||original.Valid)continue;
            string reason;
            var m=A7BackgroundFallbackMeasurement(fallbackContext,p,original.Measurement,out reason);
            var replacement=ReferenceEquals(m,original.Measurement)?original:A7PipelineMeasure(c.Raw,m,c.Fs);
            bool used=!ReferenceEquals(m,original.Measurement)&&replacement.Valid;
            if(used){measurements[p.Id]=replacement;recovered.Add(p.Id);}
            if(a7BackgroundProofDiagnostics.Count<16384)a7BackgroundProofDiagnostics.Add("{\"incident_or_reflection_peak\":"+A4ChargeIndex(p)+
                ",\"original_support_start\":"+p.Start+",\"original_support_end\":"+p.End+
                ",\"BackgroundFallbackUsed\":"+(used?"true":"false")+",\"reason\":"+Json(reason)+
                ",\"complete_packet_valid\":"+(replacement.Valid?"true":"false")+
                ",\"main_peak\":"+replacement.MainPeak+",\"opposite_peak\":"+replacement.OppositePeak+
                ",\"complete_packet_reason\":"+Json(replacement.Reason)+"}");
            else a7PipelineDiagnosticTruncated=true;
        }
        var reference=c.AttenuationScoringReference;
        var chronological=pulses.OrderBy(A4ChargeIndex).ThenBy(p=>p.Id).ToArray();
        foreach(var incident in incidents)
        {
            if(protectedIds.Contains(incident.Id))continue;
            int canonical=A4ChargeIndex(incident),anchor=canonical;
            anchor=A4RequestedTiming(c,incident,canonical,a7FirstArrival);if(anchor<0)continue;
            int required,valid;bool coverage=A4PdmapCoverage(c,anchor,out required,out valid);
            A7PipelinePulse a;if(!measurements.TryGetValue(incident.Id,out a))continue;
            if(!coverage||!a.Valid||!incident.MappingValid||incident.Clipped||incident.UnresolvedOverlap)continue;
            double incidentRho=SeventhPipelineMath.Correlation(a.Shape,calI.Shape);
            if(!Finite(incidentRho)||incidentRho<floor)continue;
            var candidates=new List<A4Pair>();
            foreach(var reflection in chronological)
            {
                if(reflection.Id==incident.Id||protectedIds.Contains(reflection.Id)||reflection.Start<=incident.End)continue;
                if(reflection.End<anchor+Math.Ceiling(lower)||reflection.Start>anchor+Math.Floor(upper))continue;
                if(c.CandidateEvaluations>=c.CandidateEvaluationLimit){c.CandidateEvaluationLimitExceeded=true;candidates.Clear();break;}
                c.CandidateEvaluations++;
                A7PipelinePulse b;if(!measurements.TryGetValue(reflection.Id,out b)||!b.Valid)continue;
                int reflectedCanonical=A4ChargeIndex(reflection),reflectionAnchor=b.MainPeak;
                if(reflectionAnchor>=0)reflectionAnchor=A4RequestedTiming(c,reflection,reflectionAnchor,a7FirstArrival);
                int delay=reflectionAnchor-anchor;
                bool timing=reflectionAnchor>=0&&delay>0&&A4ReflectionDelayWithinWindow(c,delay)&&A4BoundaryTimingValid(c,delay);
                if(!timing)continue;
                double rawPosition=timing?A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length):double.NaN;
                bool fwhm=a.Shape.FwhmValid&&b.Shape.FwhmValid&&calI.Shape.FwhmValid&&calR.Shape.FwhmValid;
                    var x=new SeventhPipelineInput {
                        EndpointPolicy=ManufacturerEndpointPolicy,
                        RawIncident=a.RawShape,RawReflection=b.RawShape,RawCalibrationIncident=calI.RawShape,
                        IndependentPackets=a.Valid&&b.Valid&&incident.End<reflection.Start&&incident.MappingValid&&reflection.MappingValid&&
                            !incident.Clipped&&!reflection.Clipped&&!incident.UnresolvedOverlap&&!reflection.UnresolvedOverlap,
                        SamePolarity=b.MainPeak>=0&&a.MainPeak>=0&&Math.Sign(c.Raw[a.MainPeak]-A4RawMeasurementBaseline(a.Measurement,a.MainPeak))==
                            Math.Sign(c.Raw[b.MainPeak]-A4RawMeasurementBaseline(b.Measurement,b.MainPeak)),
                        CalibrationSamePolarity=calI.Sign==calR.Sign,GeometryValid=timing,
                        CalibrationIdentified=reference.Quality!=null&&reference.Quality.Recognized,
                        CalibrationIndependentCompletePackets=calibrationReady&&reference.IndependentCompletePackets,
                        CalibrationUnclipped=calibrationReady&&reference.Unclipped,
                        ReflectionWindowValid=delay>0&&A4ReflectionDelayWithinWindow(c,delay),PhysicalCoverageValid=coverage,
                        UseAttenuation=a7RequestedAttenuation,UseWidth=a7RequestedWidth,
                        CalibrationMainIncident=calI.Main,CalibrationMainReflection=calR.Main,
                        CalibrationOppositeIncident=calI.Opposite,CalibrationOppositeReflection=calR.Opposite,
                        CalibrationIncidentNoise=calI.Noise,CalibrationReflectionNoise=calR.Noise,CalibrationTravelSeconds=reference.TravelSeconds,
                        IncidentMain=a.Main,ReflectionMain=b.Main,IncidentOpposite=a.Opposite,ReflectionOpposite=b.Opposite,
                        IncidentNoise=a.Noise,ReflectionNoise=b.Noise,IncidentCalibrationCorrelation=incidentRho,
                        PairCorrelation=SeventhPipelineMath.Correlation(a.Shape,b.Shape),CorrelationFloor=floor,DelaySeconds=delay/c.Fs,
                        CalibrationIncidentWidthNs=fwhm?calI.Shape.FwhmNs:calI.Shape.EnergyWidthNs,
                        CalibrationReflectionWidthNs=fwhm?calR.Shape.FwhmNs:calR.Shape.EnergyWidthNs,
                        IncidentWidthNs=fwhm?a.Shape.FwhmNs:a.Shape.EnergyWidthNs,ReflectionWidthNs=fwhm?b.Shape.FwhmNs:b.Shape.EnergyWidthNs,
                        SamplePeriodNs=1e9/c.Fs,AttenuationTolerancePercent=settings.AttenuationErrorPercent,
                        CalibrationAdaptationLimitPercent=c.CalibrationToleranceAdaptationLimitPercent,
                        WidthTolerancePercent=settings.PulseWidthErrorPercent,Weights=settings.Weights,
                        RawPositionM=rawPosition,CableLengthM=c.Length,NetDutRoundTripSeconds=c.RoundTripExact/c.Fs,
                        LeadOneWaySeconds=c.ConnectingCableLengthM/150*1e-6,PositionTolerancePercent=1,
                        DoublePeakAvailable=b.Shape.Valid&&calI.Shape.Valid&&calR.Shape.Valid,
                        IncidentDoublePeak=a.Shape.MultipleHalfHeightComponents,
                        ReflectionDoublePeak=b.Shape.MultipleHalfHeightComponents,
                        CalibrationExplainsDoublePeak=calI.Shape.MultipleHalfHeightComponents||calR.Shape.MultipleHalfHeightComponents};
                var result=SeventhPipelineMath.Evaluate(x);
                if(!A7ScorableReview(x,result))continue;
                bool backgroundUsed=recovered.Contains(incident.Id)||recovered.Contains(reflection.Id);
                result.AutoAccept=false;result.ReviewHypothesis=true;
                var pair=new A4Pair {Incident=incident,Reflection=reflection,OutputIncident=canonical,OutputReflection=reflectedCanonical,
                    EdgeId=all.Count,State="review",Position=BoundaryConfidenceMath.ProjectEndpointPosition(result.ProjectedPositionM,c.Length),
                    Correlation=x.PairCorrelation,WaveCorrelation=x.PairCorrelation,
                    Confidence=Math.Min(.49,result.Score),AmplitudeRatio=a.Main>0?b.Main/a.Main:double.NaN,
                    AttenuationScore=result.AttenuationScore,WidthScore=result.WidthScore,
                    ReviewReason="highest_scored_in_window_review_only;"+(backgroundUsed?"raw_guard_recovered_review_only;":"")+String.Join(";",result.Reasons),
                    OutputTimingReason="seventh_requested_timing_raw_background_review_only"};
                A7ApplyTimingUncertainty(c,pair,result,b.MainPeak,anchor,reflectionAnchor,a.MainPeak);
                all.Add(pair);a7PipelineResults.Add(pair,result);a7OutputTimings.Add(pair,Tuple.Create(anchor,reflectionAnchor));
                string item=A7PipelineCandidateJson(pair,x,result,fwhm);
                item=item.Substring(0,item.Length-1)+",\"BackgroundFallbackUsed\":"+(backgroundUsed?"true":"false")+",\"review_only\":true,"+
                    "\"base_engineering_score\":"+NumberJson(result.Score)+",\"background_limited_confidence\":"+NumberJson(pair.Confidence)+
                    ",\"automatic_acceptance_prohibited\":true}";
                if(a7PipelineCandidates.Count<16384)a7PipelineCandidates.Add(item);else a7PipelineDiagnosticTruncated=true;
                candidates.Add(pair);
            }
            if(c.CandidateEvaluationLimitExceeded)break;
            if(candidates.Count==0)continue;
            var ranked=candidates.OrderByDescending(p=>a7PipelineResults[p].Score).ThenByDescending(p=>p.Correlation)
                .ThenBy(p=>a7OutputTimings[p].Item2).ToArray();
            var rank=SeventhCandidateRanking.Select(ranked.Select(p=>new SeventhRankCandidate {
                Id=p.EdgeId,HardPassed=a7PipelineResults[p].CandidatePassed||a7PipelineResults[p].ReviewHypothesis,
                Correlation=p.Correlation,BaseScore=a7PipelineResults[p].Score,Position=p.Position}),
                new SeventhPriorSeed[0],a7RecordKey,A4ChargeIndex(incident),incident.Start,incident.End,c.Length,true);
            // Select by uncapped engineering score, not the 49% display ceiling.
            var best=ranked[0];
            best.Ambiguous=rank.Ambiguous;best.CandidateCount=ranked.Length;
            if(best.Ambiguous)best.ReviewReason+=";multiple_background_recovered_candidates";
            // Explicit review-only even if every numerical gate passed. No owner,
            // independent seed, accepted count or prior sidecar entry is assigned.
            best.State="review";output.Add(best);
            // Reserve only within this fallback pass. A recovered reflection may
            // not manufacture a second recovered incident/echo chain.
            protectedIds.Add(best.Incident.Id);protectedIds.Add(best.Reflection.Id);
            c.AnalysisExclusions[incident.Id]="highest_scored_in_window_review_only";
        }
    }
}
