using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Method 7 owns its threshold -> CAL incident -> physical window -> dual
    // attenuation gate -> correlation-ranked reflection workflow. It does not
    // invoke A6Match, its graph objective, A7Evaluate, or output recovery.
    private bool a7RequestedAttenuation,a7RequestedWidth;
    private readonly Dictionary<A4Pair,SeventhPipelineResult> a7PipelineResults=new Dictionary<A4Pair,SeventhPipelineResult>();
    private readonly List<string> a7PipelineIncidents=new List<string>();
    private readonly List<string> a7PipelineCandidates=new List<string>();
    private string a7PipelineCalibration="not_prepared";
    private bool a7PipelineDiagnosticTruncated;
    private int a7DepthRefillRounds,a7IndependentVisitedCount,a7RefundedIncidentSlots;

    private sealed class A7PipelinePulse
    {
        internal bool Valid;
        internal int MainPeak=-1,OppositePeak=-1,Sign;
        internal double Main=double.NaN,Opposite=double.NaN,Noise=double.NaN;
        internal A4AttenuationMeasurement Measurement;
        internal A7PulseDescriptor Shape=new A7PulseDescriptor();
        internal RawEndpointShape RawShape=new RawEndpointShape();
        internal string Reason="packet_measurement_unavailable";
    }

    private static A7PipelinePulse A7PipelineMeasure(double[] raw,A4AttenuationMeasurement m,double fs)
    {
        var d=new A7PipelinePulse {Measurement=m,Reason=m==null?"packet_measurement_missing":m.Reason};
        if(m==null||!m.Valid||raw==null||m.RawStart<0||m.RawEnd>=raw.Length||m.RawEnd<=m.RawStart||
            m.SignalSamples==null||!Finite(fs)||fs<=0)return d;
        int begin=m.Start-m.SignalOrigin,end=m.End-m.SignalOrigin;
        if(begin<0||end>=m.SignalSamples.Length)return d;
        var rawResidual=new double[m.RawEnd-m.RawStart+1];
        for(int i=0;i<rawResidual.Length;i++)rawResidual[i]=raw[m.RawStart+i]-A4RawMeasurementBaseline(m,m.RawStart+i);
        var bandResidual=new double[end-begin+1];
        for(int i=0;i<bandResidual.Length;i++)bandResidual[i]=m.SignalSamples[begin+i]-m.Baseline;
        var measured=SeventhPacketMath.Measure(new SeventhPacketInput {RawResidual=rawResidual,FixedBandResidual=bandResidual,
            RawOrigin=m.RawStart,FixedBandOrigin=m.Start,SampleRateHz=fs,RawNoise=m.RawNoise,BandNoise=m.Noise,CompleteGuard=m.Valid});
        d.MainPeak=measured.MainPeak;d.OppositePeak=measured.OppositePeak;d.Main=measured.Main;
        d.Opposite=measured.Opposite;d.Sign=measured.Sign;d.Noise=measured.Noise;
        d.Shape=measured.Shape;d.RawShape=measured.RawShape;d.Valid=measured.Valid;d.Reason=measured.Reason;
        return d;
    }

    private List<A4Pair> A7SequentialLocate(A4Context c,List<A4Pulse> pulses,A6Settings settings,List<A4Pulse> retained)
    {
        a7PipelineResults.Clear();a7PipelineIncidents.Clear();a7PipelineCandidates.Clear();a7OutputTimings.Clear();
        a7PipelineDiagnosticTruncated=false;a6Evidence.Clear();a6Lobes.Clear();a6Widths.Clear();
        a7BackgroundProofDiagnostics.Clear();
        a7DepthRefillRounds=a7IndependentVisitedCount=a7RefundedIncidentSlots=0;
        A6ClearGuardMeasurementDiagnostics();
        c.AttenuationScoringReference=A4BuildAttenuationScoringReference(new A4Context {Calibration=c.Calibration,Attenuation=true,Fs=c.Fs});
        c.AttenuationScoringSignal=c.MeasurementSignal.BandSignal;
        c.AttenuationScoringRawSigma=c.MeasurementSignal.RawSigma;
        c.AttenuationScoringPackets=pulses.Concat(A6BoundaryRawOccupancy(c,pulses)).ToList();
        foreach(var p in pulses){p.Owner=-1;p.AttenuationMeasurement=null;}
        var reference=c.AttenuationScoringReference;
        a7PipelineCalibration=reference.Reason;
        var calI=reference.Valid?A7PipelineMeasure(c.Calibration.Samples,reference.Incident,c.Calibration.SampleRate):new A7PipelinePulse();
        var calR=reference.Valid&&calI.Valid?A7PipelineMeasure(c.Calibration.Samples,reference.Reflection,c.Calibration.SampleRate):new A7PipelinePulse();
        bool calibrationReady=reference.Valid&&calI.Valid&&calR.Valid;
        var cache=new Dictionary<int,A7PipelinePulse>();
        Func<A4Pulse,A7PipelinePulse> measure=p=>
        {
            A7PipelinePulse d;
            if(!cache.TryGetValue(p.Id,out d)){d=A7PipelineMeasure(c.Raw,A6ScoringMeasurement(c,p),c.Fs);cache.Add(p.Id,d);}
            return d;
        };
        var chronological=pulses.OrderBy(A4ChargeIndex).ThenBy(p=>p.Id).ToArray();
        var incidents=pulses.Where(p=>A4ChargeIndex(p)>=0&&A4ChargeIndex(p)<c.Raw.Length&&
            A4RawCharge(c,A4ChargeIndex(p))>c.IncidentThreshold&&A4AnalysisPhaseEligible(c,A4ChargeIndex(p)))
            .GroupBy(A4ChargeIndex).Select(g=>g.OrderBy(p=>p.Id).First())
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex).ToList();
        c.AnalysisLimitReached=incidents.Count>c.AnalysisLimit;
        var output=new List<A4Pair>();var owned=new HashSet<int>();var all=new List<A4Pair>();
        var ambiguousSets=new List<Tuple<A4Pulse,List<A4Pair>,A4Pair>>();
        double floor=settings.AutomaticCorrelation?.70:settings.MinimumCorrelationPercent/100;
        double lower,upper;bool window=A4ReflectionWindow(c,out lower,out upper);
        var visitedPeaks=new HashSet<int>();
        Func<int> independentVisits=()=>IndependentIncidentBudget.Count(
            visitedPeaks,
            output.Select(p=>A4ChargeIndex(p.Reflection)));
        int ordinal=0;
        while(ordinal<incidents.Count)
        {
        for(;ordinal<incidents.Count;ordinal++)
        {
            A4Pulse incident=incidents[ordinal];int canonical=A4ChargeIndex(incident);
            // The depth budget counts independent incident work, not echoes
            // already represented by an earlier pair. Do not truncate the raw
            // packet list before reflection ownership is known.
            if(owned.Contains(incident.Id)||output.Any(p=>A4ChargeIndex(p.Reflection)==canonical))continue;
            if(independentVisits()>=c.AnalysisLimit)break;
            c.AnalysisVisitIds.Add(incident.Id);
            visitedPeaks.Add(canonical);
            string reason="not_evaluated";double incidentRho=double.NaN;
            int anchor=canonical;
            anchor=A4RequestedTiming(c,incident,canonical,a7FirstArrival);
            int required,valid;bool coverage=A4PdmapCoverage(c,anchor,out required,out valid);
            var a=measure(incident);
            if(anchor<0)reason="first_arrival_unresolved_no_peak_fallback";
            else if(c.WindowBudgetExceeded)reason="discovery_window_catalog_incomplete_independent_waveform_only";
            else if(owned.Contains(incident.Id))reason="already_owned_by_stronger_accepted_pair";
            else if(!coverage)reason="physical_roundtrip_finite_coverage_below_95_percent";
            else if(!calibrationReady)reason="calibration_unavailable_preserve_independent_waveform:"+reference.Reason;
            else if(!a.Valid||!incident.MappingValid||incident.Clipped||incident.UnresolvedOverlap)reason="incident_complete_guarded_shape_unavailable";
            else
            {
                incidentRho=SeventhPipelineMath.Correlation(a.Shape,calI.Shape);
                reason=!Finite(incidentRho)?"incident_calibration_correlation_unavailable":incidentRho<floor?
                    "incident_not_confirmed_as_calibration_like_PD":"incident_calibration_PD_gate_passed";
            }
            var candidates=new List<A4Pair>();
            if(reason=="incident_calibration_PD_gate_passed"&&window)
            {
                foreach(A4Pulse reflection in chronological)
                {
                    if(reflection.Id==incident.Id||owned.Contains(reflection.Id)||reflection.Start<=incident.End)continue;
                    int reflectedCanonical=A4ChargeIndex(reflection);
                    // Support scanning is bounded by the actual requested incident
                    // timing, not by the display crop or an averaged cable speed.
                    if(reflection.End<anchor+Math.Ceiling(lower)||reflection.Start>anchor+Math.Floor(upper))continue;
                    if(c.CandidateEvaluations>=c.CandidateEvaluationLimit)
                    {c.CandidateEvaluationLimitExceeded=true;candidates.Clear();reason="candidate_scan_incomplete_no_prefix_winner";break;}
                    c.CandidateEvaluations++;
                    var b=measure(reflection);
                    int reflectionAnchor=b.MainPeak;
                    if(reflectionAnchor>=0)reflectionAnchor=A4RequestedTiming(c,reflection,reflectionAnchor,a7FirstArrival);
                    int delay=reflectionAnchor>=0?reflectionAnchor-anchor:0;
                    bool timing=reflectionAnchor>=0&&delay>0&&A4ReflectionDelayWithinWindow(c,delay)&&A4BoundaryTimingValid(c,delay);
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
                        PairCorrelation=SeventhPipelineMath.Correlation(a.Shape,b.Shape),CorrelationFloor=floor,DelaySeconds=reflectionAnchor>=0?delay/c.Fs:double.NaN,
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
                    var result=SeventhPipelineMath.EvaluateWithAttenuationFallback(x);
                    if(!b.Valid)result.Reasons=new[]{"reflection_measurement_unavailable:"+b.Reason,"dependent_pair_gates_not_evaluated"};
                    var pair=new A4Pair {Incident=incident,Reflection=reflection,OutputIncident=canonical,OutputReflection=reflectedCanonical,
                        EdgeId=all.Count,State="review",Position=BoundaryConfidenceMath.ProjectEndpointPosition(result.ProjectedPositionM,c.Length),Correlation=x.PairCorrelation,WaveCorrelation=x.PairCorrelation,
                        Confidence=result.Score,AmplitudeRatio=a.Main>0?b.Main/a.Main:double.NaN,AttenuationScore=result.AttenuationScore,
                        WidthScore=result.WidthScore,ReviewReason=String.Join(";",result.Reasons),OutputTimingReason="seventh_sequential_requested_same_polarity_timing"};
                    all.Add(pair);a7PipelineResults.Add(pair,result);a7OutputTimings.Add(pair,Tuple.Create(anchor,reflectionAnchor));
                    if(a7PipelineCandidates.Count<16384)a7PipelineCandidates.Add(A7PipelineCandidateJson(pair,x,result,fwhm));else a7PipelineDiagnosticTruncated=true;
                    if(result.CandidatePassed||result.ReviewHypothesis)candidates.Add(pair);
                }
                if(!c.CandidateEvaluationLimitExceeded)
                {
                    // Prefer original-tolerance candidates. Use the bounded
                    // relaxed set only when none qualified at the requested gate.
                    if(candidates.Any(p=>a7PipelineResults[p].CandidatePassed))
                        candidates.RemoveAll(p=>a7PipelineResults[p].ReviewHypothesis);
                    if(candidates.Any(p=>!a7PipelineResults[p].RelaxedAttenuationFallback&&!a7PipelineResults[p].ReviewHypothesis))
                        candidates.RemoveAll(p=>a7PipelineResults[p].RelaxedAttenuationFallback);
                    var ranked=candidates.OrderByDescending(p=>p.Confidence).ThenByDescending(p=>p.Correlation)
                        .ThenBy(p=>a7OutputTimings[p].Item2).ToArray();
                    if(ranked.Length>0)
                    {
                        var rank=A7Rank(c,ranked,new SeventhPriorSeed[0],incident);
                        A4Pair best=ranked.First(p=>p.EdgeId==rank.SelectedId);var result=a7PipelineResults[best];
                        bool ambiguous=rank.Ambiguous;A7RecordRank(incident,rank,"local_evidence_first_pass");
                        best.Ambiguous=ambiguous;best.CandidateCount=ranked.Length;
                        best.State=result.AutoAccept&&!ambiguous?"accepted":"review";
                        best.ReviewReason=best.State=="accepted"?"seventh_sequential_all_required_gates_passed":
                            ambiguous?"multiple_qualified_reflections_within_correlation_resolution":String.Join(";",result.Reasons.Concat(result.EndpointReasons));
                        output.Add(best);reason=best.State=="accepted"?"qualified_reflection_accepted":"qualified_reflection_requires_review";
                        if(best.State=="accepted")
                        {owned.Add(incident.Id);owned.Add(best.Reflection.Id);incident.Owner=best.Reflection.Owner=best.EdgeId;a7IndependentSeedPairs.Add(best);}
                        if(ambiguous)ambiguousSets.Add(Tuple.Create(incident,candidates,best));
                    }
                    else reason="no_reflection_passed_required_main_attenuation_correlation_and_physical_gates";
                }
            }
            c.AnalysisExclusions[incident.Id]=reason;
            a7PipelineIncidents.Add("{\"ordinal\":"+ordinal+",\"incident_peak\":"+canonical+",\"raw_absolute_amplitude\":"+NumberJson(A4RawCharge(c,canonical))+
                ",\"requested_timing_anchor\":"+anchor+",\"first_arrival\":"+(a7FirstArrival?"true":"false")+",\"required_roundtrip_samples\":"+required+
                ",\"valid_roundtrip_samples\":"+valid+",\"coverage_passed\":"+(coverage?"true":"false")+",\"calibration_incident_correlation\":"+NumberJson(incidentRho)+
                ",\"qualified_reflection_count\":"+candidates.Count+",\"reason\":"+Json(reason)+"}");
        }
        // Resolve measured review ownership before deciding that the depth is
        // exhausted. A packet later proven to be a displayed reflection refunds
        // its incident slot; the next unvisited incident receives full scoring.
        A7RecoverBackgroundReview(c,pulses,settings,incidents.Where(p=>c.AnalysisVisitIds.Contains(p.Id)).ToList(),cache,calI,calR,calibrationReady,all,output,floor);
        if(ordinal>=incidents.Count||independentVisits()>=c.AnalysisLimit||c.CandidateEvaluationLimitExceeded)break;
        a7DepthRefillRounds++;
        }
        // A locally unique R1->R2 is not independent if those packets are also
        // hypotheses of an unresolved I->R1/R2. Otherwise a suspected echo could
        // manufacture its own spatial proof in this or the following invocation.
        var unresolvedPackets=ambiguousSets.SelectMany(s=>s.Item2.SelectMany(p=>new[]{p.Incident,p.Reflection})).ToArray();
        foreach(var seedPair in a7IndependentSeedPairs.ToArray())
        {
            bool dependent=unresolvedPackets.Any(p=>p.Id==seedPair.Incident.Id||p.Id==seedPair.Reflection.Id||
                Math.Max(p.Start,seedPair.Incident.Start)<=Math.Min(p.End,seedPair.Incident.End)||
                Math.Max(p.Start,seedPair.Reflection.Start)<=Math.Min(p.End,seedPair.Reflection.End));
            if(!dependent)continue;
            a7IndependentSeedPairs.Remove(seedPair);seedPair.State="review";seedPair.Ambiguous=true;
            seedPair.ReviewReason="packet_participates_in_unresolved_parent_hypotheses_not_independent_seed";
            owned.Remove(seedPair.Incident.Id);owned.Remove(seedPair.Reflection.Id);seedPair.Incident.Owner=seedPair.Reflection.Owner=-1;
        }
        // Immutable first-pass seeds only. History-selected pairs are deliberately
        // excluded from this set and from the next invocation's sidecar.
        foreach(var current in a7IndependentSeedPairs)
        {
            string key=a7RecordKey+":"+current.OutputIncident;
            if(a7VerifiedPriorEntries.Any(e=>e.event_key==key&&(e.position_m!=current.Position||e.local_score!=current.Confidence||
                e.support_start!=current.Incident.Start||e.support_end!=current.Incident.End)))a7ExcludedPriorEvents.Add(key);
        }
        a7ExternalSeeds.RemoveAll(s=>a7ExcludedPriorEvents.Contains(s.EventKey));
        a7VerifiedPriorEntries.RemoveAll(e=>a7ExcludedPriorEvents.Contains(e.event_key));
        a7IndependentSeedPairs.RemoveAll(p=>a7PriorQuarantined||a7ExcludedPriorEvents.Contains(a7RecordKey+":"+p.OutputIncident));
        var frozenSeeds=a7ExternalSeeds.Concat(a7IndependentSeedPairs.Select(A7CurrentSeed)).ToArray();
        foreach(var pending in ambiguousSets)
        {
            var rank=A7Rank(c,pending.Item2,frozenSeeds,pending.Item1);
            A4Pair chosen=pending.Item2.FirstOrDefault(p=>p.EdgeId==rank.SelectedId);
            if(rank.HistoryUsed&&chosen!=null&&!owned.Contains(pending.Item1.Id)&&!owned.Contains(chosen.Reflection.Id))
            {
                int index=output.IndexOf(pending.Item3);if(index>=0)output[index]=chosen;
                chosen.Ambiguous=false;chosen.CandidateCount=pending.Item2.Count;
                chosen.State=a7PipelineResults[chosen].AutoAccept?"accepted":"review";
                chosen.ReviewReason="independent_concentration_tiebreak;not_exportable_as_independent_seed";
                if(chosen.State=="accepted")
                {owned.Add(chosen.Incident.Id);owned.Add(chosen.Reflection.Id);chosen.Incident.Owner=chosen.Reflection.Owner=chosen.EdgeId;}
            }
            else if(rank.HistoryUsed)
            {rank.HistoryUsed=false;rank.Ambiguous=true;rank.Reason="history_choice_packet_already_owned_no_acceptance";}
            A7RecordRank(pending.Item1,rank,"frozen_independent_prior_second_pass");
        }
        A7RecoverBackgroundReview(c,pulses,settings,incidents.Where(p=>c.AnalysisVisitIds.Contains(p.Id)).ToList(),cache,calI,calR,calibrationReady,all,output,floor);
        var proposalIncidents=incidents.Concat(retained).Where(p=>A4RawCharge(c,A4ChargeIndex(p))>c.IncidentThreshold&&
            A4AnalysisPhaseEligible(c,A4ChargeIndex(p))).GroupBy(A4ChargeIndex).Select(g=>g.First())
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex).ToList();
        A7RecoverWindowProposals(c,pulses.Concat(retained).ToList(),proposalIncidents,all,output);
        // Last fail-closed check after every primary, historical and review pass.
        // Keep the independent incident row when no in-window timing remains.
        output.RemoveAll(p=>!A7OutputTimingInsideWindow(c,p));
        a7IndependentVisitedCount=independentVisits();
        a7RefundedIncidentSlots=visitedPeaks.Count-a7IndependentVisitedCount;
        c.AnalysisLimitReached=incidents.Any(p=>!visitedPeaks.Contains(A4ChargeIndex(p))&&
            !output.Any(pair=>A4ChargeIndex(pair.Reflection)==A4ChargeIndex(p)));
        c.CandidatePool=all;c.EffectiveSuppression=0;c.SuppressionResolution="seventh_descending_incident_sequential_distinct_packet_ownership";
        return output;
    }

    private string A7PipelineCandidateJson(A4Pair pair,SeventhPipelineInput x,SeventhPipelineResult r,bool fwhm)
    {
        var timing=a7OutputTimings[pair];
        return "{\"candidate_id\":"+pair.EdgeId+",\"incident_peak\":"+pair.OutputIncident+",\"reflection_canonical_peak\":"+pair.OutputReflection+
            ",\"raw_endpoint_incident_fwhm_ns\":"+NumberJson(x.RawIncident==null?double.NaN:x.RawIncident.FwhmNs)+
            ",\"raw_endpoint_calibration_fwhm_ns\":"+NumberJson(x.RawCalibrationIncident==null?double.NaN:x.RawCalibrationIncident.FwhmNs)+
            ",\"raw_endpoint_incident_ratio\":"+NumberJson(x.RawIncident==null?double.NaN:x.RawIncident.Ratio)+
            ",\"raw_endpoint_calibration_ratio\":"+NumberJson(x.RawCalibrationIncident==null?double.NaN:x.RawCalibrationIncident.Ratio)+
            ",\"raw_endpoint_reflection_split\":"+(x.RawReflection!=null&&x.RawReflection.Valid&&x.RawReflection.Split?"true":"false")+
            ",\"requested_incident_timing_anchor\":"+timing.Item1+",\"requested_reflection_timing_anchor\":"+timing.Item2+
            ",\"delay_seconds\":"+NumberJson(x.DelaySeconds)+",\"same_polarity\":"+(x.SamePolarity?"true":"false")+
            ",\"independent_packets\":"+(x.IndependentPackets?"true":"false")+",\"geometry_valid\":"+(x.GeometryValid?"true":"false")+
            ",\"reflection_window_passed\":"+(x.ReflectionWindowValid?"true":"false")+",\"physical_coverage_passed\":"+(x.PhysicalCoverageValid?"true":"false")+
            ",\"reflection_double_peak\":"+(x.ReflectionDoublePeak?"true":"false")+",\"calibration_explains_double_peak\":"+(x.CalibrationExplainsDoublePeak?"true":"false")+
            ",\"pair_correlation\":"+NumberJson(x.PairCorrelation)+",\"incident_calibration_correlation\":"+NumberJson(x.IncidentCalibrationCorrelation)+
            ",\"incident_main\":"+NumberJson(x.IncidentMain)+",\"incident_opposite\":"+NumberJson(x.IncidentOpposite)+
            ",\"reflection_main\":"+NumberJson(x.ReflectionMain)+",\"reflection_opposite\":"+NumberJson(x.ReflectionOpposite)+
            ",\"expected_main_reflection\":"+NumberJson(r.ExpectedMainReflection)+",\"expected_opposite_reflection\":"+NumberJson(r.ExpectedOppositeReflection)+
            ",\"main_error_percent\":"+NumberJson(r.MainAttenuationErrorPercent)+",\"opposite_error_percent\":"+NumberJson(r.OppositeAttenuationErrorPercent)+
            ",\"main_slope_per_second\":"+NumberJson(r.MainSlopePerSecond)+",\"opposite_slope_per_second\":"+NumberJson(r.OppositeSlopePerSecond)+
            ",\"calibration_main_incident\":"+NumberJson(x.CalibrationMainIncident)+",\"calibration_main_reflection\":"+NumberJson(x.CalibrationMainReflection)+
            ",\"calibration_opposite_incident\":"+NumberJson(x.CalibrationOppositeIncident)+",\"calibration_opposite_reflection\":"+NumberJson(x.CalibrationOppositeReflection)+
            ",\"calibration_incident_noise\":"+NumberJson(x.CalibrationIncidentNoise)+",\"calibration_reflection_noise\":"+NumberJson(x.CalibrationReflectionNoise)+
            ",\"calibration_recognized\":"+(r.CalibrationRecognized?"true":"false")+",\"base_attenuation_passed\":"+(r.BaseAttenuationPassed?"true":"false")+
            ",\"base_attenuation_tolerance_percent\":"+NumberJson(x.AttenuationTolerancePercent)+",\"calibration_adaptation_limit_percent\":"+NumberJson(x.CalibrationAdaptationLimitPercent)+
            ",\"main_calibration_uncertainty_percent\":"+NumberJson(r.MainCalibrationUncertaintyPercent)+",\"opposite_calibration_uncertainty_percent\":"+NumberJson(r.OppositeCalibrationUncertaintyPercent)+
            ",\"main_effective_tolerance_percent\":"+NumberJson(r.MainEffectiveTolerancePercent)+",\"opposite_effective_tolerance_percent\":"+NumberJson(r.OppositeEffectiveTolerancePercent)+
            ",\"main_propagated_uncertainty_percent\":"+NumberJson(r.MainPropagatedUncertaintyPercent)+",\"opposite_propagated_uncertainty_percent\":"+NumberJson(r.OppositePropagatedUncertaintyPercent)+
            ",\"width_kind\":"+Json(fwhm?"all_four_same_polarity_FWHM":"all_four_complete_packet_energy_width")+
            ",\"incident_width_ns\":"+NumberJson(x.IncidentWidthNs)+",\"reflection_width_ns\":"+NumberJson(x.ReflectionWidthNs)+
            ",\"expected_reflection_width_ns\":"+NumberJson(r.ExpectedReflectionWidthNs)+
            ",\"calibration_incident_width_ns\":"+NumberJson(x.CalibrationIncidentWidthNs)+",\"calibration_reflection_width_ns\":"+NumberJson(x.CalibrationReflectionWidthNs)+
            ",\"candidate_passed\":"+(r.CandidatePassed?"true":"false")+",\"auto_accept_evidence\":"+(r.AutoAccept?"true":"false")+
            ",\"relaxed_attenuation_fallback\":"+(r.RelaxedAttenuationFallback?"true":"false")+",\"measured_review_hypothesis\":"+(r.ReviewHypothesis?"true":"false")+
            ",\"score\":"+NumberJson(r.Score)+",\"raw_geometry_position_m\":"+NumberJson(r.RawPositionM)+",\"projected_position_m\":"+NumberJson(r.ProjectedPositionM)+
            ",\"endpoint_applicable\":"+(r.EndpointApplicable?"true":"false")+",\"endpoint_decision\":"+Json(r.EndpointDecision)+
            ",\"near_main_opposite_ratio_reference\":"+NumberJson(r.NearExpectedMainOppositeRatio)+",\"far_main_opposite_ratio_reference\":"+NumberJson(r.FarExpectedMainOppositeRatio)+
            ",\"near_ratio_error_percent\":"+NumberJson(r.NearRatioErrorPercent)+",\"far_ratio_error_percent\":"+NumberJson(r.FarRatioErrorPercent)+
            ",\"near_template_propagation_seconds\":"+NumberJson(x.LeadOneWaySeconds)+",\"far_template_propagation_seconds\":"+NumberJson(x.LeadOneWaySeconds+.5*x.NetDutRoundTripSeconds)+
            ",\"calibration_travel_seconds\":"+NumberJson(x.CalibrationTravelSeconds)+",\"reasons\":"+StringArrayJson(r.Reasons)+",\"endpoint_reasons\":"+StringArrayJson(r.EndpointReasons)+"}";
    }

    private string A7PipelineDiagnosticsJson(A4Context c,IList<A4Pair> output,A6Settings s)
    {
        return "{\"method\":7,\"strategy_index\":6,\"pipeline\":\"threshold_abs_desc_CAL_incident_main_attenuation_hard_correlation_soft_width\","+
            "\"depth_policy\":\"distinct_incident_slots_refunded_after_reflection_ownership\",\"requested_depth\":"+c.AnalysisLimit+
            ",\"independent_incidents_evaluated\":"+a7IndependentVisitedCount+",\"refunded_incident_slots\":"+a7RefundedIncidentSlots+
            ",\"depth_refill_rounds\":"+a7DepthRefillRounds+","+
            "\"secondary_lobe_role\":\"endpoint_identity_only_not_candidate_gate_or_score\",\"width_role\":\"soft_score_only_not_candidate_gate\","+
            "\"interpretation\":\"engineering_consistency_not_probability\",\"legacy_sixth_matching_used\":false,\"output_candidate_recovery_used\":false,"+
            "\"output_candidate_recovery_scope\":\"legacy_recovery_not_used;measured_review_hypotheses_are_retained_in_pipeline\","+
            "\"measured_review_hypothesis_selected\":"+(output.Any(p=>a7PipelineResults[p].ReviewHypothesis)?"true":"false")+","+
            "\"relaxed_attenuation_fallback_selected\":"+(output.Any(p=>a7PipelineResults[p].RelaxedAttenuationFallback)?"true":"false")+","+
            "\"requested_attenuation\":"+(a7RequestedAttenuation?"true":"false")+",\"requested_width\":"+(a7RequestedWidth?"true":"false")+
            ",\"effective_attenuation\":true,\"effective_width\":true,\"mandatory_joint_steps\":true,\"calibration_reason\":"+Json(a7PipelineCalibration)+
            ",\"threshold_pc\":"+NumberJson(c.IncidentThreshold)+",\"threshold_domain\":\"original_baseline_corrected_absolute_peak\",\"incident_order\":\"descending_raw_abs_then_sample\","+
            "\"reflection_ranking\":\"strict_then_bounded_relaxed_then_measured_review;engineering_score_then_signed_full_support_Pearson\",\"correlation_ambiguity_margin\":0.02,"+
            "\"attenuation_tolerance_percent\":"+NumberJson(s.AttenuationErrorPercent)+",\"width_tolerance_percent\":"+NumberJson(s.PulseWidthErrorPercent)+
            ",\"calibration_adaptation_limit_percent\":"+NumberJson(c.CalibrationToleranceAdaptationLimitPercent)+",\"calibration_quality_policy\":"+Json(CalibrationQualityMath.Version)+
            ",\"weights\":"+NumberArrayJson(s.Weights)+",\"weight_order\":[\"pair_correlation\",\"main_attenuation\",\"soft_calibrated_width\",\"CAL_incident_similarity\"],"+
            "\"amplitudes_domain\":\"guarded_original_CAL_LOC_main_and_opposite_lobes\",\"shape_band_hz\":[10000,3000000],"+
            "\"endpoint_ratio_slopes\":\"ln(CAL_reflection_lobe/CAL_incident_lobe)/CAL_measured_roundtrip_seconds\",\"endpoint_template_time\":\"near=lead/150;far=near+netDUT_piecewise_roundtrip/2\","+
            "\"endpoint_position_projection\":\"Far=netDUT_L;Near=0;all_outside_within_2_percent=nearest_endpoint;raw_geometry_preserved;no_index_or_delay_rewrite\","+
            "\"reflection_window\":"+A4ReflectionWindowJson(c)+",\"candidate_evaluations\":"+c.CandidateEvaluations+
            ",\"candidate_budget_exceeded\":"+(c.CandidateEvaluationLimitExceeded?"true":"false")+
            ",\"discovery_window_budget_exceeded\":"+(c.WindowBudgetExceeded?"true":"false")+",\"candidate_diagnostic_truncated\":"+(a7PipelineDiagnosticTruncated?"true":"false")+
            ",\"background_fallback_proofs\":["+String.Join(",",a7BackgroundProofDiagnostics)+"],\"review_fallback_policy\":\"highest_finite_score_inside_hard_window_no_auto_acceptance\""+
            ",\"bounded_window_proposals\":["+String.Join(",",a7WindowProposalDiagnostics)+"],\"incidents\":["+String.Join(",",a7PipelineIncidents)+"],\"candidates\":["+String.Join(",",a7PipelineCandidates)+"],\"output_pairs\":"+output.Count+
            ",\"spatial_prior\":"+A7PriorDiagnosticJson()+",\"selected_candidates\":["+
            String.Join(",",output.Select(p=>"{\"candidate_id\":"+p.EdgeId+",\"state\":"+Json(p.State)+",\"ambiguous\":"+(p.Ambiguous?"true":"false")+
                ",\"base_evidence_score\":"+NumberJson(p.Confidence)+",\"final_location_confidence\":"+
                NumberJson((p.Ambiguous||(a7PipelineResults[p].EndpointApplicable&&a7PipelineResults[p].EndpointDecision=="Indeterminate"))&&
                    !a7PipelineResults[p].RelaxedAttenuationFallback&&!a7PipelineResults[p].ReviewHypothesis?double.NaN:p.Confidence)+
                ",\"reason\":"+Json(p.ReviewReason)+"}"))+"]}";
    }
}
