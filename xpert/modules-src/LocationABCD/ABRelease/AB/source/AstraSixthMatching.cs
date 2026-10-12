using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // These are engineering evidence/measurement limits, not learned defect probabilities.
    private const double A6ReferenceSnrFloor=6, A6PairSnrFloor=4.5;
    private sealed class A6Evidence
    {
        internal A4Pair Pair;
        internal A4AttenuationMeasurement A,B;
        internal A6Correlation Correlation;
        internal A6ToleranceResult Attenuation,Width;
        internal A6LobeResult LobeA,LobeB;
        internal double CorrelationReference=1,CorrelationFloor=.7,CorrelationScore=double.NaN;
        internal double WidthA=double.NaN,WidthB=double.NaN,WidthReference=1;
        internal double TravelFraction,EnergyScore=double.NaN,Coverage,Score;
        internal double EvidencePosition=double.NaN,TimingDelay=double.NaN;
        internal double[] EffectiveWeights=new double[4];
        internal double GlobalObjectiveGap=double.NaN;
        internal bool Eligible,Selected;
        internal string CorrelationBasis="manual_absolute",Selection="not_selected";
        internal List<string> Reasons=new List<string>();
    }
    private readonly Dictionary<A4Pair,A6Evidence> a6Evidence=new Dictionary<A4Pair,A6Evidence>();
    private readonly Dictionary<int,A6LobeResult> a6Lobes=new Dictionary<int,A6LobeResult>();
    private readonly Dictionary<int,AstraPulseWidthDiagnostic.Measurement> a6Widths=new Dictionary<int,AstraPulseWidthDiagnostic.Measurement>();
    private Dictionary<int,A4LocalAmbiguity> a6LocalAmbiguities=new Dictionary<int,A4LocalAmbiguity>();
    private A6Correlation a6CalibrationCorrelation;
    private bool a6ReferenceReliable,a6CalibrationWidthValid;
    private double a6CalibrationWidthRatio=double.NaN;
    private string a6ReferenceReason="not_measured";
    private int a6Orphans,a6Components,a6IncompleteComponents;

    private List<A4Pair> A6Match(A4Context c,List<A4Pulse> pulses,A6Settings settings)
    {
        a6Evidence.Clear();a6Lobes.Clear();a6Widths.Clear();a6Components=a6IncompleteComponents=0;
        a6LocalAmbiguities.Clear();
        a6OutputGlobalCalMeasured=false;a6OutputGlobalCalRetention=double.NaN;
        A6ClearGuardMeasurementDiagnostics();
        A6ResetIncidentDiagnostics(c);
        // CAL is measured even when its attenuation gate is explicitly disabled, since automatic
        // correlation can still use its shape. No old matching, role-rewrite or acceptance code runs.
        var calibrationContext=new A4Context {Calibration=c.Calibration,Attenuation=true,Fs=c.Fs};
        c.AttenuationScoringReference=A4BuildAttenuationScoringReference(calibrationContext,!SeventhMode);
        if(c.MeasurementSignal==null)c.MeasurementSignal=A4PrepareMeasurementSignal(c.Raw,c.Fs,c.RoundTrip,pulses);
        c.AttenuationScoringSignal=c.MeasurementSignal.BandSignal;
        c.AttenuationScoringRawSigma=c.MeasurementSignal.RawSigma;
        // Raw-only occupancy is frozen before any score: small known pulses
        // must not be mistaken for quiet baseline in the primary matcher.
        var scoringPackets=pulses.Concat(A6BoundaryRawOccupancy(c,pulses)).ToList();
        c.AttenuationScoringPackets=scoringPackets;
        A4AttenuationScoringReference reference=c.AttenuationScoringReference;
        a6CalibrationCorrelation=A6CalibrationCorrelation(c,reference);
        a6ReferenceReliable=reference.Valid&&reference.IncidentSnr>=A6ReferenceSnrFloor&&reference.ReflectionSnr>=A6ReferenceSnrFloor;
        if(!SeventhMode)a6ReferenceReliable=a6ReferenceReliable&&
            HVX.SignalEvidence.SixthConfidenceMath.BasicCalibrationRatioValid(reference.RawRetention);
        a6ReferenceReason=!reference.Valid?reference.Reason:!a6ReferenceReliable?"calibration_snr_below_six_sigma":"guarded_calibration_at_least_six_sigma";
        A4PrepareUnifiedCorrelation(c,scoringPackets);
        a6CalibrationWidthValid=false;a6CalibrationWidthRatio=double.NaN;
        if(a6ReferenceReliable)
        {
            double[] lowCal=A4LowPassThreeMHz(c.Calibration.Samples,c.Calibration.SampleRate);
            var wa=A6MeasureWidth(lowCal,reference.Incident,c.Calibration.SampleRate);
            var wb=A6MeasureWidth(lowCal,reference.Reflection,c.Calibration.SampleRate);
            if(wa.Valid&&wb.Valid&&wa.WidthNs>0&&wb.WidthNs>0)
            {a6CalibrationWidthValid=true;a6CalibrationWidthRatio=wb.WidthNs/wa.WidthNs;}
        }
        A6ApplyAutomaticCalibrationSettings(c,settings,reference,a6CalibrationCorrelation,a6CalibrationWidthRatio,
            a6ReferenceReliable,a6CalibrationWidthValid);
        a6OutputSettings=settings;
        if(SeventhMode)A7PrepareCalibration(c);
        foreach(A4Pulse pulse in pulses){pulse.Owner=-1;pulse.AttenuationMeasurement=null;}
        var incidentSeeds=pulses.Where(p=>A4ChargeIndex(p)>=0&&A4ChargeIndex(p)<c.Raw.Length&&
            A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold&&A4AnalysisPhaseEligible(c,A4ChargeIndex(p)))
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(p=>A4ChargeIndex(p)).ToList();
        if(incidentSeeds.Count>c.AnalysisLimit)c.AnalysisLimitReached=true;
        incidentSeeds=incidentSeeds.Take(c.AnalysisLimit).ToList();
        var all=new List<A4Pair>();
        int maximumDelay=A4BoundaryReviewDelayLimit(c);
        foreach(A4Pulse a in incidentSeeds)
        {
            c.AnalysisVisitIds.Add(a.Id);int ia=A4ChargeIndex(a),before=all.Count;
            foreach(A4Pulse b in pulses.OrderBy(p=>A4ChargeIndex(p)))
            {
                int ib=A4ChargeIndex(b),delay=ib-ia;
                // A packet cannot reflect itself or an earlier/overlapping packet.
                // Exclude those identities before strict timing so they do not
                // falsely report that an otherwise valid incident lacks an onset.
                if(a.Id==b.Id||a.End>=b.Start)continue;
                if(c.FirstArrivalPhysicalTiming)
                {
                    int physicalA,physicalB;
                    if(!A4PhysicalTiming(c,a,b,out physicalA,out physicalB))
                    {c.AnalysisExclusions[a.Id]="first_arrival_unresolved_no_peak_fallback";continue;}
                    delay=physicalB-physicalA;
                }
                if(delay<=0||delay>maximumDelay||a.Id==b.Id||a.End>=b.Start)continue;
                if(c.CandidateEvaluations>=c.CandidateEvaluationLimit)
                {c.CandidateEvaluationLimitExceeded=true;break;}
                c.CandidateEvaluations++;
                var pair=new A4Pair {Incident=a,Reflection=b,OutputIncident=ia,OutputReflection=ib,
                    EdgeId=all.Count,State="review",ReviewReason="not_evaluated",Position=A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length)};
                A6Evidence evidence=SeventhMode?A7Evaluate(c,pair,settings):A6Evaluate(c,pair,settings);a6Evidence[pair]=evidence;all.Add(pair);
            }
            if(all.Count==before)A6MarkOrphan(c,a.Id,"no_separate_candidate_in_bounded_round_trip_window");
            if(c.CandidateEvaluationLimitExceeded)break;
        }
        c.CandidatePool=all;
        a6LocalAmbiguities=A4LocalAmbiguities(all.Where(p=>SeventhMode?A7LocalCompetitor(a6Evidence[p],settings):A6LocalCompetitor(a6Evidence[p],settings))
            .Select(p=>A4LocalDescriptor(p,a6Evidence[p].Score,!a6Evidence[p].Eligible,
                a6Evidence[p].Eligible?"all_enabled_physical_gates_passed":"sixth_measurement_boundary_shadow:"+String.Join(";",a6Evidence[p].Reasons))));
        // A graph edge is allowed to compete only after all enabled independent gates have passed.
        // The solver therefore cannot compensate an attenuation failure with high correlation.
        var eligible=all.Where(p=>a6Evidence[p].Eligible).ToList();
        bool complete=!c.WindowBudgetExceeded&&!c.CandidateEvaluationLimitExceeded;
        var committed=new List<A4Pair>();
        if(complete)
        foreach(var component in A4CandidateComponents(eligible))
        {
            a6Components++;
            A4MatchingSolution best=A6Solve(component,-1);
            if(!best.Exact)
            {a6IncompleteComponents++;foreach(var p in component)a6Evidence[p].Selection="matching_component_budget_incomplete";continue;}
            bool localAmbiguity=best.EdgeIds.Any(id=>A4LocalAmbiguous(a6LocalAmbiguities,
                component.First(p=>p.EdgeId==id).Incident.Id));
            if(localAmbiguity)
                foreach(var p in component)
                {
                    p.Ambiguous=true;
                    a6Evidence[p].Selection=A4LocalAmbiguous(a6LocalAmbiguities,p.Incident.Id)?A4LocalAmbiguityReason:
                        "connected_component_contains_"+A4LocalAmbiguityReason;
                }
            foreach(int edgeId in best.EdgeIds)
            {
                A4Pair pair=component.First(p=>p.EdgeId==edgeId);
                A4MatchingSolution alternate=A6Solve(component,edgeId);
                if(!alternate.Exact){a6Evidence[pair].Selection="alternative_matching_budget_incomplete";a6IncompleteComponents++;continue;}
                pair.Ambiguity=best.Weight-alternate.Weight;
                a6Evidence[pair].GlobalObjectiveGap=pair.Ambiguity;
                // Do not remove ambiguous edges then promote a weaker assignment.
                // Separate packet-graph components remain unaffected.
                if(localAmbiguity)continue;
                if(pair.Ambiguity<=.02)
                {pair.Ambiguous=true;a6Evidence[pair].Selection="alternative_pairing_within_two_percent_objective_margin";continue;}
                a6Evidence[pair].Selected=true;a6Evidence[pair].Selection="independent_matching_unique_at_margin";
                pair.State="accepted";pair.ReviewReason="all_enabled_gates_and_global_stability_passed";committed.Add(pair);
                pair.Incident.Owner=pair.EdgeId;pair.Reflection.Owner=pair.EdgeId;
            }
        }
        else foreach(var p in all)a6Evidence[p].Selection="record_candidate_discovery_incomplete";
        if(!SeventhMode)A6PrepareSpatialPrior(c,committed);
        // Retain at most one candidate for each remaining incident seed; accepted packet ownership
        // is never used by another accepted pair. Reviews may overlap and are explicitly unaccepted.
        var result=new List<A4Pair>(committed);
        var owned=new HashSet<int>(committed.SelectMany(p=>new[]{p.Incident.Id,p.Reflection.Id}));
        foreach(A4Pulse a in incidentSeeds)
        {
            if(owned.Contains(a.Id))continue;
            A4Pair best=all.Where(p=>p.Incident.Id==a.Id&&A6ReviewMeasurable(a6Evidence[p]))
                .OrderByDescending(p=>a6Evidence[p].Eligible)
                .ThenByDescending(p=>a6Evidence[p].Score).ThenBy(p=>p.OutputReflection).FirstOrDefault();
            if(best==null)
            {A6MarkOrphan(c,a.Id,"no_guarded_correlation_candidate_above_three_sigma_no_arbitrary_pair_output");continue;}
            if(!SeventhMode)best=A6ChooseSpatialReview(c,a,all,best);
            var e=a6Evidence[best];
            if(A4LocalAmbiguous(a6LocalAmbiguities,a.Id))
            {best.Ambiguous=true;e.Selection=A4LocalAmbiguityReason;}
            if(e.Reasons.Count==0&&e.Selection=="not_selected")e.Selection="competing_packet_ownership";
            best.State="review";best.ReviewReason=String.Join(";",e.Reasons.Concat(new[]{e.Selection}).Where(s=>s!="not_selected"));
            result.Add(best);
        }
        if(!SeventhMode)
        {
            A6RecoverBoundaryReviewPairs(c,pulses,settings,result);
            A6RecoverShapeUnresolvedReviewPairs(c,settings,result);
            A6RecoverBoundaryReviewPairs(c,pulses,settings,result,true);
        }
        foreach(var pair in result)pair.CandidateCount=a6Evidence.Keys.Count(p=>p.Incident.Id==pair.Incident.Id);
        c.EffectiveSuppression=0;c.SuppressionResolution=SeventhMode?"seventh_CAL_echo_role_evidence_before_independent_matching":"sixth_independent_packet_matching_no_legacy_echo_role_rewrite";
        return result.OrderByDescending(p=>A4RawCharge(c,p.OutputIncident)).ThenBy(p=>p.OutputIncident).ToList();
    }

    private static bool A6ReviewMeasurable(A6Evidence e)
    {
        return e.A.Valid&&e.B.Valid&&e.Correlation.Valid&&e.A.Snr>=3&&e.B.Snr>=3;
    }

    private static bool A6LocalCompetitor(A6Evidence e,A6Settings settings)
    {
        if(e==null||!Finite(e.Score)||e.Score<A4LocalHighScorePercent)return false;
        if(e.Eligible)return true;
        // A close measurement boundary may create uncertainty, never eligibility.
        // All other physical/quality requirements must already have passed.
        if(e.A==null||e.B==null||!e.A.Valid||!e.B.Valid||e.A.Snr<A6PairSnrFloor||e.B.Snr<A6PairSnrFloor||
            e.Correlation==null||!e.Correlation.Valid||!e.Correlation.StructureCompatible||e.Correlation.OppositeTemporalPolarity||e.Correlation.Fixed<0)return false;
        foreach(string reason in e.Reasons)
        {
            if(reason=="signed_correlation_below_effective_floor")
            {if(e.Correlation.Fixed+A4LocalCorrelationShadowMargin+1e-12<e.CorrelationFloor)return false;}
            else if(reason=="attenuation_relative_error_outside_tolerance")
            {if(!e.Attenuation.Available||!Finite(e.Attenuation.EffectiveTolerancePercent)||e.Attenuation.ErrorPercent>e.Attenuation.EffectiveTolerancePercent+A4LocalRelativeErrorShadowMargin+1e-9)return false;}
            else if(reason=="pulse_width_relative_error_outside_tolerance")
            {if(!e.Width.Available||e.Width.ErrorPercent>settings.PulseWidthErrorPercent+A4LocalRelativeErrorShadowMargin+1e-9)return false;}
            else return false;
        }
        return e.Reasons.Count>0;
    }

    private static AstraPulseWidthDiagnostic.Measurement A6MeasureWidth(double[] signal,A4AttenuationMeasurement m,double fs)
    {
        if(m==null||!m.Valid)return new AstraPulseWidthDiagnostic.Measurement {Reason="guarded_low_band_packet_unavailable"};
        if(m.SignalSamples!=null)
            return AstraPulseWidthDiagnostic.Measure(m.SignalSamples,m.Start-m.SignalOrigin,m.End-m.SignalOrigin,
                m.Peak-m.SignalOrigin,m.Baseline,m.Noise,fs);
        return AstraPulseWidthDiagnostic.Measure(signal,m.Start,m.End,m.Peak,m.Baseline,m.Noise,fs);
    }
    private A6LobeResult A6Lobes(A4Context c,A4Pulse p,A4AttenuationMeasurement measurement)
    {
        A6LobeResult l;if(a6Lobes.TryGetValue(p.Id,out l))return l;
        if(measurement!=null&&measurement.Valid)
        {
            double[] originalResidual=new double[p.End-p.Start+1];
            for(int i=0;i<originalResidual.Length;i++)
                originalResidual[i]=c.Raw[p.Start+i]-A4RawMeasurementBaseline(measurement,p.Start+i);
            l=A6EvidenceMath.Lobes(originalResidual,0,originalResidual.Length-1,A4ChargeIndex(p)-p.Start,0,
                measurement.RawNoise>0?measurement.RawNoise:measurement.Noise,c.Fs);
            if(l.FirstStart>=0)l.FirstStart+=p.Start;if(l.FirstEnd>=0)l.FirstEnd+=p.Start;if(l.FirstPeak>=0)l.FirstPeak+=p.Start;
            if(l.SecondStart>=0)l.SecondStart+=p.Start;if(l.SecondEnd>=0)l.SecondEnd+=p.Start;if(l.SecondPeak>=0)l.SecondPeak+=p.Start;
            if(l.MainStart>=0)l.MainStart+=p.Start;if(l.MainEnd>=0)l.MainEnd+=p.Start;if(l.MainPeak>=0)l.MainPeak+=p.Start;
            if(l.SecondaryStart>=0)l.SecondaryStart+=p.Start;if(l.SecondaryEnd>=0)l.SecondaryEnd+=p.Start;if(l.SecondaryPeak>=0)l.SecondaryPeak+=p.Start;
        }
        else l=new A6LobeResult {Reason="original_guard_baseline_unavailable"};
        a6Lobes[p.Id]=l;return l;
    }
    private AstraPulseWidthDiagnostic.Measurement A6Width(A4Context c,A4Pulse p,A4AttenuationMeasurement m)
    {
        AstraPulseWidthDiagnostic.Measurement w;if(a6Widths.TryGetValue(p.Id,out w))return w;
        w=A6MeasureWidth(c.AttenuationScoringSignal,m,c.Fs);a6Widths[p.Id]=w;return w;
    }
    private A6Evidence A6Evaluate(A4Context c,A4Pair pair,A6Settings settings)
    {
        A4Pulse a=pair.Incident,b=pair.Reflection;var e=new A6Evidence {Pair=pair};
        A4AttenuationScoringReference reference=c.AttenuationScoringReference;
        e.A=A6ScoringMeasurement(c,a);e.B=A6ScoringMeasurement(c,b);
        double travel=(pair.OutputReflection-pair.OutputIncident)/c.Fs;
        e.TravelFraction=reference.Valid&&reference.TravelSeconds>0?Clamp(travel/reference.TravelSeconds,0,1):Clamp((pair.OutputReflection-pair.OutputIncident)/c.RoundTripExact,0,1);
        if(!a.MappingValid||!b.MappingValid)e.Reasons.Add("raw_packet_mapping_invalid");
        if(a.Clipped||b.Clipped||a.UnresolvedOverlap||b.UnresolvedOverlap)e.Reasons.Add("raw_packet_clipped_or_unresolved_overlap");
        if(!e.A.Valid||!e.B.Valid)e.Reasons.Add("guarded_measurement_unavailable:"+e.A.Reason+"/"+e.B.Reason);
        if(e.A.Valid&&e.B.Valid&&(e.A.Snr<A6PairSnrFloor||e.B.Snr<A6PairSnrFloor))e.Reasons.Add("pair_low_band_snr_below_4point5");
        bool measured=e.A.Valid&&e.B.Valid;
        var unifiedCorrelation=A4UnifiedCorrelation(c,a,b,pair.OutputReflection-pair.OutputIncident);
        e.Correlation=unifiedCorrelation.Measured;
        int timingA,timingB;double timingBaselineA,timingBaselineB;string timingReason;
        bool timingResolved;
        if(c.FirstArrivalPhysicalTiming)
        {
            timingResolved=A4PhysicalTiming(c,a,b,out timingA,out timingB);
            timingBaselineA=timingBaselineB=double.NaN;timingReason="first_arrival_unresolved_no_peak_fallback";
            if(!timingResolved){travel=double.NaN;e.TravelFraction=double.NaN;}
        }
        else timingResolved=A4HomologousPeakTiming(c,pair,e.Correlation,e.A,e.B,out timingA,out timingB,
            out timingBaselineA,out timingBaselineB,out timingReason);
        if(timingResolved)
        {
            // The acceptance/ownership decision must use the same selected delay
            // that the final matrix will publish. Do this before retention can
            // suppress an accepted reflection's independent above-threshold row.
            int actualDelay=timingB-timingA;
            e.TimingDelay=actualDelay;
            e.EvidencePosition=A4BoundaryOutputPosition(actualDelay,c.Fs,c.Joints,c.Velocities,c.Length);
            travel=actualDelay/c.Fs;
            e.TravelFraction=reference.Valid&&reference.TravelSeconds>0?
                Clamp(travel/reference.TravelSeconds,0,1):Clamp(actualDelay/c.RoundTripExact,0,1);
            unifiedCorrelation=A4UnifiedCorrelation(c,a,b,actualDelay);
            e.Correlation=unifiedCorrelation.Measured;
            if(!A4ReflectionDelayWithinWindow(c,actualDelay)||!A4BoundaryTimingValid(c,actualDelay))
                e.Reasons.Add("corresponding_peak_delay_exceeds_cable_boundary_tolerance");
        }
        else e.Reasons.Add("corresponding_same_polarity_output_peak_unresolved:"+timingReason);
        // Correlation uses shared structurally homologous temporal anchors;
        // raw packet identities/timing never move to those shape anchors.
        e.CorrelationFloor=settings.MinimumCorrelationPercent/100;
        if(settings.AutomaticCorrelation)
        {
            e.CorrelationReference=unifiedCorrelation.Reference;
            e.CorrelationFloor=unifiedCorrelation.Floor;
            e.CorrelationBasis=unifiedCorrelation.Basis;
        }
        if(e.Correlation.Valid)
        {
            e.CorrelationScore=Clamp(e.Correlation.Fixed/e.CorrelationReference,0,1);
            if(e.Correlation.Fixed+1e-12<e.CorrelationFloor)e.Reasons.Add("signed_correlation_below_effective_floor");
            if(e.Correlation.Fixed<=0)e.Reasons.Add("zero_or_opposite_low_band_correlation_not_accepted");
        }
        else e.Reasons.Add("pearson_correlation_unavailable:"+e.Correlation.StructureReason);
        double observed=measured&&e.A.RawAmplitude>0?e.B.RawAmplitude/e.A.RawAmplitude:double.NaN;
        var attenuation=A6OutputAttenuationMath.EvaluateWithCalibrationQuality(e.A.RawAmplitude,e.B.RawAmplitude,
            reference.RawRetention,travel,reference.TravelSeconds,settings.AttenuationErrorPercent,a6ReferenceReliable,
            reference.Quality==null?double.NaN:reference.Quality.RelativeUncertaintyPercent,c.CalibrationToleranceAdaptationLimitPercent);
        // Acceptance and final output share the exact amplitude-domain boundary
        // operation. Ratio-domain redivision can disagree by one ULP at a limit.
        e.Attenuation=new A6ToleranceResult {Available=attenuation.Available,Passed=attenuation.Passed,BasePassed=attenuation.BasePassed,
            EffectiveTolerancePercent=attenuation.EffectiveTolerancePercent,PropagatedUncertaintyPercent=attenuation.PropagatedUncertaintyPercent,
            Observed=observed,Reference=attenuation.ExpectedRatio,ErrorPercent=attenuation.ErrorPercent,
            Score=attenuation.Score,Reason=attenuation.Reason};
        if(settings.UseAttenuation)
        {
            if(!a6ReferenceReliable)e.Reasons.Add("requested_attenuation_reference_unreliable:"+a6ReferenceReason);
            if(!e.Attenuation.Available)e.Reasons.Add("attenuation_ratio_unavailable");
            else if(!e.Attenuation.Passed)e.Reasons.Add("attenuation_relative_error_outside_tolerance");
        }
        var wa=A6Width(c,a,e.A);var wb=A6Width(c,b,e.B);
        e.WidthA=wa.Valid?wa.WidthNs:double.NaN;e.WidthB=wb.Valid?wb.WidthNs:double.NaN;
        // Same-band CAL broadening supplies an optional width reference. Without it, explicitly
        // compare widths directly; a failed measurement never substitutes a perfect width score.
        e.WidthReference=a6CalibrationWidthValid?1+(a6CalibrationWidthRatio-1)*e.TravelFraction:1;
        e.Width=A6EvidenceMath.RelativeError(wa.Valid&&wb.Valid&&wa.WidthNs>0?wb.WidthNs/wa.WidthNs:double.NaN,
            e.WidthReference,settings.PulseWidthErrorPercent);
        if(settings.UseWidth)
        {
            if(!e.Width.Available)e.Reasons.Add("low_band_main_lobe_fwhm_unavailable:"+wa.Reason+"/"+wb.Reason);
            else if(!e.Width.Passed)e.Reasons.Add("pulse_width_relative_error_outside_tolerance");
        }
        e.LobeA=A6Lobes(c,a,e.A);e.LobeB=A6Lobes(c,b,e.B);
        if(e.LobeA.Valid&&Finite(e.EvidencePosition))
        {
            // User-proposed near/far morphology hypothesis, only a soft auxiliary factor (5%).
            // No location is inferred or changed by this model; absent lobes remove this factor.
            double nearFraction=1-Clamp(e.EvidencePosition/c.Length,0,1);
            double expectedFirst=.25+.50*nearFraction;
            e.EnergyScore=Clamp(1-Math.Abs(e.LobeA.FirstFraction-expectedFirst),0,1);
        }
        double[] factors={e.CorrelationScore,settings.UseAttenuation?e.Attenuation.Score:double.NaN,
            settings.UseWidth?e.Width.Score:double.NaN,e.EnergyScore};
        bool[] active={true,settings.UseAttenuation,settings.UseWidth,true};
        double requested=0,available=0;
        for(int k=0;k<4;k++)if(active[k])
        {requested+=settings.Weights[k];if(Finite(factors[k]))available+=settings.Weights[k];}
        e.Coverage=requested>0?available/requested:0;
        // Missing requested evidence does not increase the remaining factors' influence.
        // Its measurement remains unavailable (NaN), with zero contribution to the aggregate.
        e.Score=HVX.SignalEvidence.SixthConfidenceMath.WeightedScore(factors,settings.Weights,active,100);
        // A hard attenuation conflict cannot be offset by excellent rho or
        // lobes. Apply one review-only ceiling before review selection,
        // not after choosing a winner with a now-obsolete score.
        e.Score=Math.Min(e.Score,HVX.SignalEvidence.SixthConfidenceMath.AttenuationCeiling(settings.UseAttenuation,
            e.Attenuation.Available,e.Attenuation.BasePassed,e.Attenuation.Score,100));
        // Missing mandatory evidence is unknown, not an engineering zero.
        // Conversely, attenuation agreement cannot rescue inverted polarity or
        // a physically impossible delay/location, even with custom weights.
        if(!measured||!e.Correlation.Valid||settings.UseWidth&&!e.Width.Available)e.Score=double.NaN;
        if(e.Correlation.Valid&&(e.Correlation.Fixed<=0||e.Correlation.OppositeTemporalPolarity))e.Score=0;
        if(Finite(e.TimingDelay)&&(!A4ReflectionDelayWithinWindow(c,e.TimingDelay)||!A4BoundaryTimingValid(c,e.TimingDelay)))
        {e.Score=Finite(e.Score)&&Finite(e.EvidencePosition)?0:double.NaN;e.Reasons.Add("physical_position_outside_tested_cable");}
        for(int k=0;k<4;k++)e.EffectiveWeights[k]=active[k]&&Finite(factors[k])&&requested>0?settings.Weights[k]/requested:0;
        if(!Finite(e.Score))e.Reasons.Add("engineering_score_unavailable_missing_required_evidence");
        else if(e.Score+1e-9<A6Settings.MinimumOverallScore)e.Reasons.Add("engineering_score_below_70");
        // Extrapolated negative positions are retained. They are not silently rounded to endpoints.
        if(!Finite(pair.Position))e.Reasons.Add("geometry_position_unavailable");
        e.Eligible=e.Reasons.Count==0;
        pair.Correlation=e.Correlation.Valid?e.Correlation.Fixed:double.NaN;
        pair.WaveCorrelation=pair.Correlation;pair.EnvelopeCorrelation=double.NaN;
        pair.WidthScore=e.Width.Available?e.Width.Score:double.NaN;
        pair.AttenuationScore=e.Attenuation.Available?e.Attenuation.Score:double.NaN;
        // Method 7 borrows this routine's measurements before replacing the
        // score. Its public column 22 must retain the original same-band ratio.
        pair.AmplitudeRatio=SeventhMode?(measured&&e.A.Amplitude>0?e.B.Amplitude/e.A.Amplitude:double.NaN):observed;
        pair.Confidence=e.Score/100;
        pair.StrategyWeight=Finite(e.Score)?Math.Max(1e-8,(e.Score-A6Settings.MinimumOverallScore)/(100-A6Settings.MinimumOverallScore)):1e-8;
        pair.ReviewReason=String.Join(";",e.Reasons);return e;
    }
    private static A4MatchingSolution A6Solve(List<A4Pair> edges,int forbidden)
    {
        int[] nodes=edges.SelectMany(p=>new[]{p.Incident.Id,p.Reflection.Id}).Distinct().OrderBy(v=>v).ToArray();
        var result=new A4MatchingSolution();
        if(nodes.Length>32||edges.Count>128)return result;
        var local=nodes.Select((id,k)=>new {id,k}).ToDictionary(x=>x.id,x=>x.k);
        var graph=edges.Where(p=>p.EdgeId!=forbidden).Select(p=>new A4SolverEdge {
            U=local[p.Incident.Id],V=local[p.Reflection.Id],Id=p.EdgeId,Weight=p.StrategyWeight})
            .OrderByDescending(e=>e.Weight).ThenBy(e=>e.Id).ToArray();
        var budget=new A4SearchBudget {Limit=200000};
        A4SearchMatching(graph,nodes.Length,(1UL<<nodes.Length)-1,0,new List<int>(),result,budget);
        result.Exact=!budget.Exceeded;return result;
    }
    private static string A6ToleranceJson(A6ToleranceResult e)
    {
        return e==null?"null":"{\"available\":"+(e.Available?"true":"false")+",\"passed\":"+(e.Passed?"true":"false")+
            ",\"observed\":"+NumberJson(e.Observed)+",\"reference\":"+NumberJson(e.Reference)+",\"error_percent\":"+NumberJson(e.ErrorPercent)+
            ",\"score\":"+NumberJson(e.Score)+",\"reason\":"+Json(e.Reason)+"}";
    }
    private static string A6LobesJson(A6LobeResult e)
    {
        return e==null?"null":"{\"valid\":"+(e.Valid?"true":"false")+",\"reason\":"+Json(e.Reason)+
            ",\"first_peak\":"+e.FirstPeak+",\"second_peak\":"+e.SecondPeak+",\"first_energy\":"+NumberJson(e.FirstEnergy)+
            ",\"second_energy\":"+NumberJson(e.SecondEnergy)+",\"first_fraction\":"+NumberJson(e.FirstFraction)+
            ",\"second_to_first_ratio\":"+NumberJson(e.SecondToFirstRatio)+"}";
    }
    private string A6PairEvidenceJson(A4Pair p)
    {
        A6Evidence e=a6Evidence[p];A4LocalAmbiguity local;a6LocalAmbiguities.TryGetValue(p.Incident.Id,out local);
        return "{\"edge_id\":"+p.EdgeId+",\"raw_incident_peak\":"+p.OutputIncident+",\"raw_reflection_peak\":"+p.OutputReflection+
            ",\"canonical_position_m\":"+NumberJson(p.Position)+",\"evidence_position_m\":"+NumberJson(e.EvidencePosition)+
            ",\"evidence_timing_delay_samples\":"+NumberJson(e.TimingDelay)+",\"accepted\":"+(p.State=="accepted"?"true":"false")+",\"score_percent\":"+NumberJson(e.Score)+
            ",\"output_score_percent\":"+NumberJson(100*p.Confidence)+",\"spatial_preference\":"+A6SpatialDecisionJson(p)+
            ",\"eligible_before_matching\":"+(e.Eligible?"true":"false")+",\"selection\":"+Json(e.Selection)+",\"reasons\":"+StringArrayJson(e.Reasons)+
            ",\"global_forbidden_edge_objective_gap\":"+NumberJson(e.GlobalObjectiveGap)+",\"local_reflection_ambiguity\":"+A4LocalAmbiguityJson(local)+
            ",\"correlation\":{\"signed_fixed\":"+NumberJson(e.Correlation.Fixed)+",\"reference\":"+NumberJson(e.CorrelationReference)+
            ",\"effective_floor\":"+NumberJson(e.CorrelationFloor)+",\"normalized_score\":"+NumberJson(e.CorrelationScore)+",\"basis\":"+Json(e.CorrelationBasis)+
            ",\"temporal_alignment\":"+A6TemporalCorrelationJson(e.Correlation)+"}"+
            ",\"travel_fraction_model_clamped\":"+NumberJson(e.TravelFraction)+",\"attenuation\":"+A6ToleranceJson(e.Attenuation)+
            ",\"width\":"+A6ToleranceJson(e.Width)+",\"incident_fwhm_ns\":"+NumberJson(e.WidthA)+",\"reflection_fwhm_ns\":"+NumberJson(e.WidthB)+
            ",\"incident_lobes\":"+A6LobesJson(e.LobeA)+",\"reflection_lobes\":"+A6LobesJson(e.LobeB)+",\"energy_hypothesis_score\":"+NumberJson(e.EnergyScore)+
            ",\"effective_weights\":"+NumberArrayJson(e.EffectiveWeights)+",\"requested_evidence_coverage\":"+NumberJson(e.Coverage)+
            ",\"incident_measurement\":"+A4AttenuationMeasurementJson(e.A)+",\"reflection_measurement\":"+A4AttenuationMeasurementJson(e.B)+
            ",\"incident_guard_selection\":"+A6GuardMeasurementJson(e.A)+",\"reflection_guard_selection\":"+A6GuardMeasurementJson(e.B)+"}";
    }
    private string A6DiagnosticsJson(A4Context c,List<A4Pulse> pulses,List<A4Pair> outputs,A6Settings s)
    {
        var sb=new StringBuilder();sb.Append("{\"score_interpretation\":\"deterministic_engineering_evidence_not_probability\",\"minimum_overall_score\":70,");
        sb.Append("\"fixed_shape_measurement_band_hz\":[10000,3000000],\"fixed_discovery_band_hz\":[10000,3000000],\"shape_peak_basis\":\"shared_temporal_structure_homologous_packet_anchor\",\"timing_peak_basis\":\"immutable_raw_absolute_peak\",");
        sb.Append("\"requested_weights\":").Append(NumberArrayJson(s.RequestedWeights)).Append(",\"automatic_configuration\":").Append(a6AutomaticConfigurationJson)
            .Append(",\"local_reflection_ambiguity_policy\":").Append(A4LocalAmbiguityPolicyJson())
            .Append(",\"spatial_preference_policy\":").Append(A6SpatialPriorJson(c))
            .Append(",\"automatic_correlation\":").Append(s.AutomaticCorrelation?"true":"false");
        sb.Append(",\"unified_correlation_reference\":").Append(A4UnifiedCorrelationReferenceJson(c));
        sb.Append(",\"minimum_correlation_percent\":").Append(NumberJson(s.MinimumCorrelationPercent));
        sb.Append(",\"attenuation_error_percent\":").Append(NumberJson(s.AttenuationErrorPercent)).Append(",\"pulse_width_error_percent\":").Append(NumberJson(s.PulseWidthErrorPercent));
        sb.Append(",\"relative_error_definition\":\"100*abs(observed/reference-1); actual error is not clamped; enabled gates cannot be compensated by another score\",");
        sb.Append("\"attenuation_model\":\"Rcal^clamp(delay/Tcal,0,1), empirical single-exponential engineering reference, terminal coefficient not separately identified\",");
        sb.Append("\"width_model\":\"same_3mhz_main_lobe_fwhm_ratio; linear CAL ratio interpolation with travel fraction if valid, otherwise ratio 1\",");
        sb.Append("\"energy_model\":\"optional raw time-ordered two-lobe energy; incident first-lobe fraction soft near/far hypothesis only, not a hard gate or endpoint classifier\",");
        sb.Append("\"calibration_quality_floor_snr\":6,\"pair_quality_floor_snr\":4.5,\"calibration_reliable\":").Append(a6ReferenceReliable?"true":"false");
        if(!SeventhMode)sb.Append(",\"sixth_confidence_policy\":{\"version\":")
            .Append(Json(HVX.SignalEvidence.SixthConfidenceMath.Version))
            .Append(",\"attenuation_domain\":\"original_baseline_corrected_amplitudes\",\"calibration_basis\":\"displayed_CAL_marked_neighborhood_peaks_global_median\",\"relative_calibration_amplitude_floor_used\":false,\"calibration_raw_retention\":")
            .Append(NumberJson(c.AttenuationScoringReference.RawRetention))
            .Append(",\"calibration_adaptation_limit_percent\":").Append(NumberJson(c.CalibrationToleranceAdaptationLimitPercent))
            .Append(",\"within_tolerance_attenuation_contribution\":1,\"outside_tolerance_attenuation_contribution\":\"tolerance_percent/relative_error_percent; zero_tolerance_conflict=0\",\"outside_tolerance_overall_ceiling\":\"0.49*(0.5+0.5*attenuation_contribution); upper_bound_not_awarded_minimum\",\"missing_required_evidence\":\"NaN\",\"default_attenuation_tolerance_percent\":15,\"correlation_measurement_band_unchanged\":true,\"acceptance_requires_all_requested_gates\":true}");
        sb.Append(",\"width_measurement_snr_floor\":5,\"review_pair_snr_floor\":3,\"review_requires_guarded_pair_and_valid_correlation\":true,\"matching_objective_margin\":0.02,\"matching_objective\":\"sum((score-70)/30) over independent edges; isolated accepted score must exceed 70.6\"");
        sb.Append(",\"calibration_width_valid\":").Append(a6CalibrationWidthValid?"true":"false");
        sb.Append(",\"calibration_reason\":").Append(Json(a6ReferenceReason)).Append(",\"calibration_measurement\":").Append(A4AttenuationReferenceJson(c.AttenuationScoringReference));
        sb.Append(",\"calibration_correlation\":{\"valid\":").Append(a6CalibrationCorrelation.Valid?"true":"false").Append(",\"fixed\":").Append(NumberJson(a6CalibrationCorrelation.Fixed));
        sb.Append(",\"minus_plus_two_samples_lower\":").Append(NumberJson(a6CalibrationCorrelation.Lower)).Append(",\"minus_plus_two_samples_upper\":").Append(NumberJson(a6CalibrationCorrelation.Upper)).Append(",\"interval_is_not_statistical_confidence_interval\":true}");
        sb.Append(",\"calibration_width_ratio\":").Append(NumberJson(a6CalibrationWidthRatio)).Append(",\"candidate_evaluations\":").Append(c.CandidateEvaluations);
        sb.Append(",\"candidate_budget_exceeded\":").Append(c.CandidateEvaluationLimitExceeded?"true":"false").Append(",\"orphan_incident_seeds\":").Append(a6Orphans);
        sb.Append(",\"incident_diagnostics\":").Append(A6IncidentDiagnosticsJson(c,pulses,outputs));
        sb.Append(",\"matching_components\":").Append(a6Components).Append(",\"incomplete_components\":").Append(a6IncompleteComponents);
        sb.Append(",\"all_candidate_failure_counts\":{");bool first=true;
        foreach(var reason in a6Evidence.Values.SelectMany(e=>e.Reasons).GroupBy(r=>r).OrderBy(g=>g.Key))
        {if(!first)sb.Append(',');first=false;sb.Append(Json(reason.Key)).Append(':').Append(reason.Count());}
        sb.Append("},\"output_pairs\":[").Append(String.Join(",",outputs.Select(A6PairEvidenceJson))).Append("],\"top_alternatives_per_incident\":[");
        sb.Append(String.Join(",",a6Evidence.Keys.GroupBy(p=>p.Incident.Id).OrderBy(g=>g.Key).SelectMany(g=>g.OrderByDescending(p=>a6Evidence[p].Eligible)
            .ThenByDescending(p=>a6Evidence[p].Score).ThenBy(p=>p.OutputReflection).Take(3)).Where(p=>!outputs.Contains(p)).Select(A6PairEvidenceJson)));
        sb.Append("]}");return sb.ToString();
    }
}
