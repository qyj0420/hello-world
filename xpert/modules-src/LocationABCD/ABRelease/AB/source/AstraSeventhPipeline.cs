using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Method 7 scans a bounded catalogue BEFORE committing any packet role.
    // CAL consistency is auxiliary evidence; LOC competitors remain in the
    // final causal/mutual-exclusive graph. No sixth matcher is invoked.
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
        A7ResetConfidenceState();
        a7PipelineResults.Clear();a7PipelineIncidents.Clear();a7PipelineCandidates.Clear();a7OutputTimings.Clear();
        a7TimingUncertainties.Clear();a7MeasurementTimingUncertainties.Clear();a7TimingPeakIdentities.Clear();
        a7UnknownCompetitorPeaks.Clear();a7ManualReviewBlocks.Clear();
        a7PipelineDiagnosticTruncated=false;a6Evidence.Clear();a6Lobes.Clear();a6Widths.Clear();
        a7BackgroundProofDiagnostics.Clear();a7WaveformVetoDiagnostics.Clear();
        a7ProposalCatalogueComplete=false;
        a7DepthRefillRounds=a7IndependentVisitedCount=a7RefundedIncidentSlots=0;
        A6ClearGuardMeasurementDiagnostics();
        var calibrationCapabilities=A7BuildCalibrationCapabilities(c);
        c.AttenuationScoringReference=calibrationCapabilities.Reference;
        a7CalibrationCapabilitiesJson=calibrationCapabilities.DiagnosticJson;
        a7CalibrationTemplateReliable=calibrationCapabilities.TemplateReliable;
        a7CalibrationAttenuationReliable=calibrationCapabilities.AttenuationReliable;
        a7ProposalExpectedRetention=calibrationCapabilities.AttenuationReliable?calibrationCapabilities.Reference.RawRetention:double.NaN;
        c.AttenuationScoringSignal=c.MeasurementSignal.BandSignal;
        c.AttenuationScoringRawSigma=c.MeasurementSignal.RawSigma;
        c.AttenuationScoringPackets=pulses.Concat(A6BoundaryRawOccupancy(c,pulses)).ToList();
        foreach(var p in pulses){p.Owner=-1;p.AttenuationMeasurement=null;}
        var reference=c.AttenuationScoringReference;
        a7PipelineCalibration=reference.Reason;
        var calI=calibrationCapabilities.TemplateReliable?A7PipelineMeasure(c.Calibration.Samples,reference.Incident,c.Calibration.SampleRate):new A7PipelinePulse();
        var calR=calibrationCapabilities.TemplateReliable&&calI.Valid?A7PipelineMeasure(c.Calibration.Samples,reference.Reflection,c.Calibration.SampleRate):new A7PipelinePulse();
        bool calibrationReady=calibrationCapabilities.TemplateReliable&&calibrationCapabilities.AttenuationReliable&&calI.Valid&&calR.Valid;
        var cache=new Dictionary<int,A7PipelinePulse>();
        Func<A4Pulse,A7PipelinePulse> measure=p=>
        {
            A7PipelinePulse d;
            if(!cache.TryGetValue(p.Id,out d)){d=A7PipelineMeasure(c.Raw,A6ScoringMeasurement(c,p),c.Fs);cache.Add(p.Id,d);}
            return d;
        };
        var chronological=pulses.OrderBy(A4ChargeIndex).ThenBy(p=>p.Id).ToArray();
        var incidents=pulses.Where(p=>A4ChargeIndex(p)>=0&&A4ChargeIndex(p)<c.Raw.Length&&
            A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold&&A4RawCharge(c,A4ChargeIndex(p))>0&&A4AnalysisPhaseEligible(c,A4ChargeIndex(p)))
            .GroupBy(A4ChargeIndex).Select(g=>g.OrderBy(p=>p.Id).First())
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex).ToList();
        c.AnalysisLimitReached=incidents.Count>c.AnalysisLimit;
        var output=new List<A4Pair>();var all=new List<A4Pair>();
        double floor=settings.AutomaticCorrelation?.70:settings.MinimumCorrelationPercent/100;
        double lower,upper;bool window=A4ReflectionWindow(c,out lower,out upper);
        var visitedPeaks=new HashSet<int>();
        // Provisional edges cannot refund or own an incident before graph review.
        Func<int> independentVisits=()=>visitedPeaks.Count;
        int ordinal=0;
        while(ordinal<incidents.Count)
        {
        for(;ordinal<incidents.Count;ordinal++)
        {
            A4Pulse incident=incidents[ordinal];int canonical=A4ChargeIndex(incident);
            // A provisional A->B, including a strict CAL pass, must not prevent
            // the later scan of A->C or B->D. Roles are committed only at the end.
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
            else if(!coverage)reason="physical_roundtrip_finite_coverage_below_95_percent";
            else if(!calibrationReady)reason="CAL_template_or_loss_unreliable_LOC_LOC_review_available:"+reference.Reason;
            else if(!a.Valid||!incident.MappingValid||incident.Clipped||incident.UnresolvedOverlap)reason="incident_complete_guarded_shape_unavailable";
            else
            {
                incidentRho=SeventhPipelineMath.Correlation(a.Shape,calI.Shape);
                // CAL mismatch is not proof of no PD. This strict auto-confirmation
                // path retains its CAL evidence; independent LOC-LOC recovery below
                // can preserve a genuine echo as manual review without CAL likeness.
                reason="incident_guarded_LOC_shape_measured_CAL_similarity_auxiliary";
            }
            var candidates=new List<A4Pair>();
            var unknownCompetitors=new List<int>();
            var candidateDiagnostics=new List<Tuple<A4Pair,SeventhPipelineInput,bool>>();
            if(reason=="incident_guarded_LOC_shape_measured_CAL_similarity_auxiliary"&&window)
            {
                foreach(A4Pulse reflection in chronological)
                {
                    if(reflection.Id==incident.Id||reflection.Start<=incident.End)continue;
                    int reflectedCanonical=A4ChargeIndex(reflection);
                    // Support scanning is bounded by the actual requested incident
                    // timing, not by the display crop or an averaged cable speed.
                    if(reflection.End<anchor+Math.Ceiling(lower)||reflection.Start>anchor+Math.Floor(upper))continue;
                    if(c.CandidateEvaluations>=c.CandidateEvaluationLimit)
                    {c.CandidateEvaluationLimitExceeded=true;candidates.Clear();reason="candidate_scan_incomplete_no_prefix_winner";break;}
                    c.CandidateEvaluations++;
                    var b=measure(reflection);
                    if(A7UnresolvedReflection(c,incident,reflection,b))unknownCompetitors.Add(reflectedCanonical);
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
                    A7ApplyTimingUncertainty(c,pair,result,b.MainPeak,anchor,reflectionAnchor,a.MainPeak);
                    all.Add(pair);a7PipelineResults.Add(pair,result);a7OutputTimings.Add(pair,Tuple.Create(anchor,reflectionAnchor));
                    candidateDiagnostics.Add(Tuple.Create(pair,x,fwhm));
                    if(result.CandidatePassed||result.ReviewHypothesis)candidates.Add(pair);
                }
                if(!c.CandidateEvaluationLimitExceeded)
                {
                    // A failed complete measurement is unknown evidence, not a
                    // negative echo test. Do not award uniqueness by deleting it.
                    if(unknownCompetitors.Count>0)
                    {
                        a7UnknownCompetitorPeaks[canonical]=unknownCompetitors.Distinct().OrderBy(v=>v).ToArray();
                        foreach(var candidate in candidates)A7RequireManualReview(candidate,a7PipelineResults[candidate],
                            "unresolved_high_snr_in_window_competitor_prevents_unique_acceptance");
                    }
                    // CAL failure is not a negative LOC echo test. Preserve every
                    // measured review hypothesis as a competing graph edge.
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
                        // This is a diagnostic local proposal, never ownership or
                        // an independent seed. The full graph can replace it.
                    }
                    else reason="no_reflection_passed_required_main_attenuation_correlation_and_physical_gates";
                }
                // Even an interrupted candidate scan retains its diagnostic
                // prefix; the incomplete-scan veto above still forbids a winner.
                foreach(var diagnostic in candidateDiagnostics)
                {
                    if(a7PipelineCandidates.Count<16384)a7PipelineCandidates.Add(A7PipelineCandidateJson(diagnostic.Item1,
                        diagnostic.Item2,a7PipelineResults[diagnostic.Item1],diagnostic.Item3));else a7PipelineDiagnosticTruncated=true;
                }
            }
            c.AnalysisExclusions[incident.Id]=reason;
            a7PipelineIncidents.Add("{\"ordinal\":"+ordinal+",\"incident_peak\":"+canonical+",\"raw_absolute_amplitude\":"+NumberJson(A4RawCharge(c,canonical))+
                ",\"requested_timing_anchor\":"+anchor+",\"first_arrival\":"+(a7FirstArrival?"true":"false")+",\"required_roundtrip_samples\":"+required+
                ",\"valid_roundtrip_samples\":"+valid+",\"coverage_passed\":"+(coverage?"true":"false")+",\"calibration_incident_correlation\":"+NumberJson(incidentRho)+
                ",\"qualified_reflection_count\":"+candidates.Count+",\"reason\":"+Json(reason)+"}");
        }
        if(ordinal>=incidents.Count||independentVisits()>=c.AnalysisLimit||c.CandidateEvaluationLimitExceeded)break;
        a7DepthRefillRounds++;
        }
        // No provisional role or old spatial prior is allowed to hide recovery
        // candidates. The recovery pass exposes ALL passed LOC-LOC packet edges.
        output.Clear();a7IndependentSeedPairs.Clear();
        var proposalIncidents=incidents.Concat(retained).Where(p=>A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold&&A4RawCharge(c,A4ChargeIndex(p))>0&&
            A4AnalysisPhaseEligible(c,A4ChargeIndex(p))).GroupBy(A4ChargeIndex).Select(g=>g.First())
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex).ToList();
        A7RecoverWindowProposals(c,pulses.Concat(retained).ToList(),proposalIncidents,all,output,floor,visitedPeaks);
        c.AnalysisLimitReached=incidents.Any(p=>!visitedPeaks.Contains(A4ChargeIndex(p)));
        output=A7ResolveCausalGraph(c,all,floor,visitedPeaks);
        A7ApplyFinalWaveformEvidence(c,output,floor);
        // Last fail-closed check after every primary, historical and review pass.
        // Keep the independent incident row when no in-window timing remains.
        output.RemoveAll(p=>!A7OutputTimingInsideWindow(c,p));
        foreach(var pair in output.Where(p=>a7ManualReviewBlocks.Contains(p)))
        {
            pair.State="review";
            if(Finite(pair.Confidence))pair.Confidence=Math.Min(.49,pair.Confidence);
        }
        A7ApplyFinalConfidenceGate(c,all,output,floor);
        a7IndependentVisitedCount=independentVisits();
        a7RefundedIncidentSlots=visitedPeaks.Count-a7IndependentVisitedCount;
        c.AnalysisLimitReached=incidents.Any(p=>!visitedPeaks.Contains(A4ChargeIndex(p)));
        c.CandidatePool=all;
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
            ",\"uncertainty\":"+A7UncertaintyJson(pair)+
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
        return "{\"method\":7,\"strategy_index\":6,\"pipeline\":\"bounded_all_LOC_LOC_packet_candidates_causal_mutual_exclusive_graph_CAL_auxiliary\","+
            "\"causal_graph\":"+a7CausalGraphJson+",\"depth_policy\":\"real_unique_incident_visits_before_any_role_commit_no_provisional_refund\",\"requested_depth\":"+c.AnalysisLimit+
            ",\"independent_incidents_evaluated\":"+a7IndependentVisitedCount+",\"refunded_incident_slots\":"+a7RefundedIncidentSlots+
            ",\"depth_refill_rounds\":"+a7DepthRefillRounds+","+
            "\"secondary_lobe_role\":\"endpoint_identity_only_not_candidate_gate_or_score\",\"width_role\":\"soft_score_only_not_candidate_gate\","+
            "\"interpretation\":\"engineering_consistency_not_probability\",\"legacy_sixth_matching_used\":false,\"output_candidate_recovery_used\":false,"+
            "\"unknown_competitor_policy\":\"separate_same_polarity_raw_6sigma_unmeasurable_in_window_requires_review\","+
            "\"timing_uncertainty_policy\":\"first_arrival_window_membership_disagreement_OR_delay_difference_gt_max_2samples_1percent_roundtrip_requires_review_not_statistical_CI\","+
            "\"output_candidate_recovery_scope\":\"legacy_recovery_not_used;measured_review_hypotheses_are_retained_in_pipeline\","+
            "\"measured_review_hypothesis_selected\":"+(output.Any(p=>a7PipelineResults[p].ReviewHypothesis)?"true":"false")+","+
            "\"relaxed_attenuation_fallback_selected\":"+(output.Any(p=>a7PipelineResults[p].RelaxedAttenuationFallback)?"true":"false")+","+
            "\"requested_attenuation\":"+(a7RequestedAttenuation?"true":"false")+",\"requested_width\":"+(a7RequestedWidth?"true":"false")+
            ",\"effective_attenuation\":"+(a7CalibrationAttenuationReliable?"true":"false")+",\"effective_width\":true,\"mandatory_joint_steps\":false,"+
            "\"attenuation_scope\":\"strict_CAL_path_gate;LOC_LOC_review_auxiliary_only_when_reliable\",\"width_scope\":\"LOC_LOC_soft_consistency_not_CAL_required\","+
            "\"strict_CAL_template_reliable\":"+(a7CalibrationTemplateReliable?"true":"false")+",\"calibration_reason\":"+Json(a7PipelineCalibration)+
            ",\"calibration_capabilities\":"+a7CalibrationCapabilitiesJson+",\"waveform_evidence_version\":"+Json(SeventhProposalMath.Version)+
            ",\"requested_correlation_floor\":"+NumberJson(s.AutomaticCorrelation?.70:s.MinimumCorrelationPercent/100)+
            ",\"CAL_similarity_required_for_review\":false,\"final_waveform_vetoes\":["+String.Join(",",a7WaveformVetoDiagnostics)+"]"+
            ",\"threshold_pc\":"+NumberJson(c.IncidentThreshold)+",\"threshold_domain\":\"original_baseline_corrected_absolute_peak\",\"incident_order\":\"descending_raw_abs_then_sample\","+
            "\"reflection_ranking\":\"bounded_maximum_total_waveform_evidence_with_review_no_position_prior;all_CAL_review_competitors_retained\",\"correlation_ambiguity_margin\":0.02,"+
            "\"attenuation_tolerance_percent\":"+NumberJson(s.AttenuationErrorPercent)+",\"width_tolerance_percent\":"+NumberJson(s.PulseWidthErrorPercent)+
            ",\"calibration_adaptation_limit_percent\":"+NumberJson(c.CalibrationToleranceAdaptationLimitPercent)+",\"calibration_quality_policy\":"+Json(CalibrationQualityMath.Version)+
            ",\"weights\":"+NumberArrayJson(s.Weights)+",\"weight_order\":[\"pair_correlation\",\"main_attenuation\",\"soft_calibrated_width\",\"CAL_incident_similarity\"],"+
            "\"amplitudes_domain\":\"guarded_original_CAL_LOC_main_and_opposite_lobes\",\"shape_band_hz\":[10000,3000000],"+
            "\"endpoint_ratio_slopes\":\"ln(CAL_reflection_lobe/CAL_incident_lobe)/CAL_measured_roundtrip_seconds\",\"endpoint_template_time\":\"near=lead/150;far=near+netDUT_piecewise_roundtrip/2\","+
            "\"endpoint_position_projection\":\"Far=netDUT_L;Near=0;all_outside_within_2_percent=nearest_endpoint;raw_geometry_preserved;no_index_or_delay_rewrite\","+
            "\"reflection_window\":"+A4ReflectionWindowJson(c)+",\"candidate_evaluations\":"+c.CandidateEvaluations+
            ",\"candidate_budget_exceeded\":"+(c.CandidateEvaluationLimitExceeded?"true":"false")+
            ",\"supplemental_candidate_catalogue_complete\":"+(a7ProposalCatalogueComplete?"true":"false")+
            ",\"discovery_window_budget_exceeded\":"+(c.WindowBudgetExceeded?"true":"false")+",\"candidate_diagnostic_truncated\":"+(a7PipelineDiagnosticTruncated?"true":"false")+
            ",\"background_fallback_proofs\":["+String.Join(",",a7BackgroundProofDiagnostics)+"],\"review_fallback_policy\":\"complete_raw_lobes_6sigma_signed_multiscale_requested_floor_best_runnerup_inside_exact_window_no_auto_acceptance\""+
            ",\"bounded_window_proposals\":["+String.Join(",",a7WindowProposalDiagnostics)+"],\"incidents\":["+String.Join(",",a7PipelineIncidents)+"],\"candidates\":["+String.Join(",",a7PipelineCandidates)+"],\"output_pairs\":"+output.Count+
            ",\"spatial_prior\":"+A7PriorDiagnosticJson()+",\"selected_candidates\":["+
            String.Join(",",output.Select(p=>"{\"candidate_id\":"+p.EdgeId+",\"state\":"+Json(p.State)+",\"ambiguous\":"+(p.Ambiguous?"true":"false")+
                ",\"base_evidence_score\":"+NumberJson(p.Confidence)+",\"final_location_confidence\":"+
                NumberJson((p.Ambiguous||(a7PipelineResults[p].EndpointApplicable&&a7PipelineResults[p].EndpointDecision=="Indeterminate"))&&
                    !a7PipelineResults[p].RelaxedAttenuationFallback&&!a7PipelineResults[p].ReviewHypothesis?double.NaN:p.Confidence)+
                ",\"uncertainty\":"+A7UncertaintyJson(p)+",\"reason\":"+Json(p.ReviewReason)+"}"))+"]}";
    }
}
