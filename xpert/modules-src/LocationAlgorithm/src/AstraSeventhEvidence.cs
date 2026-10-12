using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // The sixth packet/discovery/output machinery is reused, but this switch is
    // set ONLY by the independent Seventh facade. Its evidence is applied BEFORE
    // graph eligibility/ownership; it is not a relabelled sixth output.
    internal bool SeventhMode { get; set; }
    private bool a7FirstArrival;
    private A7PulseDescriptor a7CalIncident,a7CalPrimary,a7CalSecondary;
    private double a7SecondaryDelaySeconds=double.NaN,a7SecondaryRetention=double.NaN;
    private string a7CalibrationRole="unavailable";
    private readonly Dictionary<int,A7PulseDescriptor> a7PulseShapes=new Dictionary<int,A7PulseDescriptor>();
    private readonly Dictionary<A4Pair,A7EvidenceResult> a7Results=new Dictionary<A4Pair,A7EvidenceResult>();
    private readonly Dictionary<A4Pair,string> a7RoleDetails=new Dictionary<A4Pair,string>();
    private readonly Dictionary<A4Pair,Tuple<int,int>> a7OutputTimings=new Dictionary<A4Pair,Tuple<int,int>>();

    private static A7PulseDescriptor A7Describe(A4AttenuationMeasurement m,double fs)
    {
        if(m==null||!m.Valid||m.SignalSamples==null)return new A7PulseDescriptor();
        return A7WaveformMath.Measure(m.SignalSamples,m.Start-m.SignalOrigin,m.End-m.SignalOrigin,
            m.Peak-m.SignalOrigin,m.Baseline,m.Noise,fs);
    }
    private A7PulseDescriptor A7Shape(A4Context c,A4Pulse p,A4AttenuationMeasurement m)
    {
        A7PulseDescriptor shape;if(a7PulseShapes.TryGetValue(p.Id,out shape))return shape;
        shape=A7Describe(m,c.Fs);a7PulseShapes[p.Id]=shape;return shape;
    }
    private void A7PrepareCalibration(A4Context c)
    {
        a7PulseShapes.Clear();a7Results.Clear();a7RoleDetails.Clear();a7OutputTimings.Clear();
        a7CalIncident=a7CalPrimary=a7CalSecondary=null;
        a7SecondaryDelaySeconds=a7SecondaryRetention=double.NaN;a7CalibrationRole="unavailable";
        var reference=c.AttenuationScoringReference;var cal=c.Calibration;
        if(!a6ReferenceReliable||reference==null||!reference.Valid||cal==null)return;
        a7CalIncident=A7Describe(reference.Incident,cal.SampleRate);
        a7CalPrimary=A7Describe(reference.Reflection,cal.SampleRate);
        if(!a7CalIncident.Valid||!a7CalPrimary.Valid)return;
        a7CalibrationRole=a7CalPrimary.MultipleHalfHeightComponents?
            "marked_primary_compound_packet_not_split_into_independent_arrivals":"marked_primary_packet";
        // Only independent, guarded later CAL packets may become an R2 reference.
        // A double peak inside R1 remains unresolved role evidence. No artificial
        // split is attempted at 2*lead/v when the measurement cannot resolve it.
        var profile=A4PrepareMeasurementSignal(cal.Samples,cal.SampleRate,
            (int)Math.Round(reference.TravelSeconds*cal.SampleRate));
        var cc=new A4Context {Raw=profile.DetectionRaw,Signal=profile.DetectionRaw,Fs=cal.SampleRate,
            Center=profile.DetectionCenter,RawCenter=profile.DetectionCenter,Sigma=profile.RawSigma,
            Threshold=4.5*profile.RawSigma,RoundTrip=(int)Math.Round(reference.TravelSeconds*cal.SampleRate),
            RoundTripExact=reference.TravelSeconds*cal.SampleRate};
        var packets=A4Extract(cc);cc.Raw=cal.Samples;cc.MeasurementSignal=profile;
        cc.AttenuationScoringSignal=profile.BandSignal;cc.AttenuationScoringRawSigma=profile.RawSigma;cc.AttenuationScoringPackets=packets;
        int primaryPeak=reference.Reflection.Peak;
        var choices=new List<Tuple<A7PulseDescriptor,double,double,double>>();
        foreach(var p in packets.Where(p=>p.Start>reference.Reflection.RawEnd).Take(64))
        {
            A6OriginalGuardTrace trace;var m=A6ScoringMeasurementCore(cc,p,out trace);
            if(!m.Valid||m.Snr<6)continue;
            var shape=A7Describe(m,cal.SampleRate);if(!shape.Valid)continue;
            double delta=(m.Peak-primaryPeak)/cal.SampleRate;
            double expectedLead=2*c.ConnectingCableLengthM/150*1e-6;
            double tolerance=2/cal.SampleRate;
            bool plausible=Math.Abs(delta-reference.TravelSeconds)<=tolerance||
                expectedLead>0&&Math.Abs(delta-expectedLead)<=tolerance;
            double similarity=A7WaveformMath.Similarity(a7CalPrimary,shape);
            double ratio=m.Amplitude/reference.Reflection.Amplitude;
            if(plausible&&Finite(similarity)&&similarity>=.55&&ratio>0&&ratio<=1.10)
                choices.Add(Tuple.Create(shape,delta,ratio,similarity));
        }
        var ranked=choices.OrderByDescending(x=>x.Item4).ThenBy(x=>x.Item2).ToArray();
        if(ranked.Length==0){a7CalibrationRole+=";no_independent_secondary_CAL_role";return;}
        if(ranked.Length>1&&ranked[0].Item4-ranked[1].Item4<=.05)
        {a7CalibrationRole+=";secondary_CAL_role_ambiguous_not_used";return;}
        a7CalSecondary=ranked[0].Item1;a7SecondaryDelaySeconds=ranked[0].Item2;a7SecondaryRetention=ranked[0].Item3;
        a7CalibrationRole+=";independent_secondary_CAL_role_timing_and_shape_supported";
    }
    private A6Evidence A7Evaluate(A4Context c,A4Pair pair,A6Settings settings)
    {
        // Reuse guarded amplitudes, structural correlation and homologous timing
        // measurements, NOT the sixth weighted score or eligibility decision.
        var e=A6Evaluate(c,pair,settings);
        int timingA,timingB;double baselineA,baselineB;string timingReason;
        if(!A4HomologousPeakTiming(c,pair,e.Correlation,e.A,e.B,out timingA,out timingB,out baselineA,out baselineB,out timingReason))
        {timingA=A4ChargeIndex(pair.Incident);timingB=-1;}
        if(a7FirstArrival&&timingB>timingA)
        {
            int onsetA=A4RawLeadingEdge(c,pair.Incident,timingA),onsetB=A4RawLeadingEdge(c,pair.Reflection,timingB);
            if(onsetA>=0&&onsetA<=timingA&&onsetB>=0&&onsetB<=timingB&&onsetB>onsetA)
            {timingA=onsetA;timingB=onsetB;}
            else {timingA=timingB=-1;e.TimingDelay=double.NaN;e.EvidencePosition=double.NaN;}
        }
        if(timingB>timingA)
        {
            e.TimingDelay=timingB-timingA;e.EvidencePosition=A4BoundaryOutputPosition(timingB-timingA,c.Fs,c.Joints,c.Velocities,c.Length);
            var correlation=A4UnifiedCorrelation(c,pair.Incident,pair.Reflection,timingB-timingA);
            e.Correlation=correlation.Measured;
            if(settings.AutomaticCorrelation)
            {e.CorrelationReference=correlation.Reference;e.CorrelationFloor=correlation.Floor;e.CorrelationBasis=correlation.Basis;}
        }
        a7OutputTimings[pair]=Tuple.Create(timingA,timingB);
        var a=A7Shape(c,pair.Incident,e.A);var b=A7Shape(c,pair.Reflection,e.B);
        bool calValid=a7CalIncident!=null&&a7CalPrimary!=null&&a7CalIncident.Valid&&a7CalPrimary.Valid;
        double fraction=Finite(e.TimingDelay)&&c.AttenuationScoringReference.TravelSeconds>0?
            Clamp(e.TimingDelay/c.Fs/c.AttenuationScoringReference.TravelSeconds,0,1):double.NaN;
        double primary=double.NaN,secondary=double.NaN;
        if(calValid&&b.Valid&&Finite(fraction))
        {
            double near=A7WaveformMath.Similarity(b,a7CalIncident),far=A7WaveformMath.Similarity(b,a7CalPrimary);
            if(Finite(near)&&Finite(far))primary=(1-fraction)*near+fraction*far;
            if(a7CalSecondary!=null)secondary=A7WaveformMath.Similarity(b,a7CalSecondary);
        }
        bool secondaryRole=false,ambiguousRole=false;int predecessor=-1;
        if(a7CalSecondary!=null&&b.Valid&&Finite(secondary)&&secondary>=.70&&Finite(a7SecondaryDelaySeconds))
        {
            double expected=A4ChargeIndex(pair.Reflection)-a7SecondaryDelaySeconds*c.Fs;
            foreach(var p in c.AttenuationScoringPackets.Where(p=>p.Id!=pair.Incident.Id&&p.Id!=pair.Reflection.Id&&
                A4ChargeIndex(p)>A4ChargeIndex(pair.Incident)&&Math.Abs(A4ChargeIndex(p)-expected)<=2).Take(8))
            {
                var m=A6ScoringMeasurement(c,p);var shape=A7Shape(c,p,m);
                double similarity=A7WaveformMath.Similarity(shape,a7CalPrimary);
                var ratio=ConfidenceMath.RelativeError(m.Amplitude>0?e.B.Amplitude/m.Amplitude:double.NaN,a7SecondaryRetention,settings.AttenuationErrorPercent);
                if(shape.Valid&&m.Snr>=6&&Finite(similarity)&&similarity>=.75&&ratio.Available&&ratio.Passed)
                {
                    predecessor=A4ChargeIndex(p);
                    // Supported causal predecessor is required; resemblance to a
                    // later CAL packet alone does not label a pulse as secondary.
                    secondaryRole=secondary>=primary+.10;
                    ambiguousRole=!secondaryRole;break;
                }
            }
        }
        bool fwhm=calValid&&a.Valid&&b.Valid&&a.FwhmValid&&b.FwhmValid&&a7CalIncident.FwhmValid&&a7CalPrimary.FwhmValid;
        double wa=a.Valid?(fwhm?a.FwhmNs:a.EnergyWidthNs):double.NaN;
        double wb=b.Valid?(fwhm?b.FwhmNs:b.EnergyWidthNs):double.NaN;
        double calRatio=calValid?(fwhm?a7CalPrimary.FwhmNs/a7CalIncident.FwhmNs:a7CalPrimary.EnergyWidthNs/a7CalIncident.EnergyWidthNs):double.NaN;
        var input=new A7EvidenceInput {
            GuardsValid=e.A.Valid&&e.B.Valid&&e.A.Snr>=4.5&&e.B.Snr>=4.5&&pair.Incident.MappingValid&&pair.Reflection.MappingValid&&
                !pair.Incident.Clipped&&!pair.Reflection.Clipped&&!pair.Incident.UnresolvedOverlap&&!pair.Reflection.UnresolvedOverlap&&
                e.Correlation.Valid&&e.Correlation.StructureCompatible&&!e.Correlation.OppositeTemporalPolarity,
            TimingValid=A4ReflectionDelayWithinWindow(c,e.TimingDelay)&&A4BoundaryTimingValid(c,e.TimingDelay),
            CalibrationReliable=a6ReferenceReliable&&calValid&&SixthConfidenceMath.BasicCalibrationRatioValid(c.AttenuationScoringReference.RawRetention),
            RoleEvidenceAvailable=calValid&&!a7CalPrimary.MultipleHalfHeightComponents&&Finite(primary),
            DefiniteSecondaryRole=secondaryRole,RoleAmbiguous=ambiguousRole,
            Correlation=e.Correlation.Fixed,CorrelationReference=e.CorrelationReference,CorrelationFloor=e.CorrelationFloor,
            IncidentAmplitude=e.A.RawAmplitude,ReflectionAmplitude=e.B.RawAmplitude,
            CalibrationRetention=c.AttenuationScoringReference.RawRetention,DelaySeconds=e.TimingDelay/c.Fs,
            CalibrationTravelSeconds=c.AttenuationScoringReference.TravelSeconds,
            WidthAvailable=a.Valid&&b.Valid&&calValid,IncidentWidthNs=wa,ReflectionWidthNs=wb,CalibrationWidthRatio=calRatio,
            PrimaryRoleSimilarity=primary,SecondaryRoleSimilarity=secondary,
            UseAttenuation=settings.UseAttenuation,UseWidth=settings.UseWidth,
            AttenuationTolerancePercent=settings.AttenuationErrorPercent,WidthTolerancePercent=settings.PulseWidthErrorPercent,Weights=settings.Weights};
        var result=A7EvidenceKernel.Evaluate(input);a7Results[pair]=result;
        string endpoint="unresolved_no_morphology_only_endpoint_flip";
        if(calValid&&a.Valid&&Math.Abs(e.TimingDelay-c.RoundTripExact)<=2&&a.EnergyWidthNs>=2*a7CalIncident.EnergyWidthNs)
            endpoint="far_end_morphology_hint_only_not_identification";
        a7RoleDetails[pair]="{\"primary_template_similarity\":"+NumberJson(primary)+",\"secondary_template_similarity\":"+NumberJson(secondary)+
            ",\"supported_predecessor_peak\":"+predecessor+",\"secondary_role\":"+(secondaryRole?"true":"false")+
            ",\"role_ambiguous\":"+(ambiguousRole?"true":"false")+",\"width_kind\":"+Json(fwhm?"same_polarity_fwhm":"whole_guarded_packet_energy_over_peak_squared")+
            ",\"incident_width_ns\":"+NumberJson(wa)+",\"reflection_width_ns\":"+NumberJson(wb)+",\"calibration_width_ratio\":"+NumberJson(calRatio)+
            ",\"endpoint_hint\":"+Json(endpoint)+",\"position_changed_by_endpoint_hint\":false}";
        e.Reasons=new List<string>(result.Reasons);e.Eligible=result.Eligible;e.Score=result.Available?100*result.Score:double.NaN;
        e.Attenuation=new A6ToleranceResult {Available=result.Attenuation.Available,Passed=result.Attenuation.Passed,
            Score=result.Attenuation.Score,ErrorPercent=result.Attenuation.ErrorPercent,
            Observed=e.A.Amplitude>0?e.B.Amplitude/e.A.Amplitude:double.NaN,
            Reference=result.Attenuation.ExpectedRatio,Reason=result.Attenuation.Reason};
        e.EnergyScore=result.RoleScore;e.WidthA=wa;e.WidthB=wb;e.WidthReference=result.WidthReference;
        e.Width=new A6ToleranceResult {Available=result.Width.Available,Passed=result.Width.Passed,Score=result.Width.Score,
            ErrorPercent=result.Width.ErrorPercent,Observed=wa>0?wb/wa:double.NaN,Reference=result.WidthReference,Reason=result.Width.Reason};
        pair.Correlation=e.Correlation.Valid?e.Correlation.Fixed:double.NaN;pair.WaveCorrelation=pair.Correlation;
        pair.AttenuationScore=e.Attenuation.Score;pair.WidthScore=e.Width.Score;pair.Confidence=result.Score;
        pair.Position=e.EvidencePosition;
        pair.StrategyWeight=result.Eligible?Math.Max(1e-8,(result.Score-.70)/.30):1e-8;
        pair.ReviewReason=String.Join(";",e.Reasons);return e;
    }
    private static bool A7LocalCompetitor(A6Evidence e,A6Settings settings)
    {
        if(e.Eligible)return true;
        if(e.A==null||e.B==null||!e.A.Valid||!e.B.Valid||e.A.Snr<4.5||e.B.Snr<4.5||
            e.Correlation==null||!e.Correlation.Valid||!e.Correlation.StructureCompatible||e.Correlation.OppositeTemporalPolarity)return false;
        foreach(string reason in e.Reasons)
        {
            if(reason=="correlation_below_reference_floor")
            {if(e.Correlation.Fixed+A4LocalCorrelationShadowMargin+1e-12<e.CorrelationFloor)return false;}
            else if(reason=="attenuation_conflict")
            {if(!e.Attenuation.Available||e.Attenuation.ErrorPercent>settings.AttenuationErrorPercent+A4LocalRelativeErrorShadowMargin+1e-9)return false;}
            else if(reason=="calibrated_width_trend_conflict")
            {if(!e.Width.Available||e.Width.ErrorPercent>settings.PulseWidthErrorPercent+A4LocalRelativeErrorShadowMargin+1e-9)return false;}
            else if(reason!="engineering_evidence_below_70_percent")return false;
        }
        return e.Reasons.Any(r=>r=="correlation_below_reference_floor"||r=="attenuation_conflict"||r=="calibrated_width_trend_conflict");
    }
    private string A7DiagnosticsJson(A4Context c,IList<A4Pair> output,A6Settings s)
    {
        return "{\"method\":7,\"strategy_index\":6,\"kernel\":"+Json(A7EvidenceKernel.Version)+
            ",\"interpretation\":\"engineering_evidence_not_probability\",\"candidate_evidence_applied_before_matching\":true,"+
            "\"fixed_measurement_band_hz\":[10000,3000000],\"calibration_role\":"+Json(a7CalibrationRole)+
            ",\"secondary_cal_delay_seconds\":"+NumberJson(a7SecondaryDelaySeconds)+",\"secondary_cal_retention\":"+NumberJson(a7SecondaryRetention)+
            ",\"attenuation_tolerance_percent\":"+NumberJson(s.AttenuationErrorPercent)+",\"width_tolerance_percent\":"+NumberJson(s.PulseWidthErrorPercent)+
            ",\"weights\":"+NumberArrayJson(s.Weights)+",\"raw_CAL_travel_for_amplitude_unchanged\":true,"+
            "\"width_growth_is_calibrated_engineering_trend_not_physical_necessity\":true,\"double_peak_is_not_far_end_proof\":true,"+
            "\"automatic_endpoint_flip_disabled\":true,\"candidate_evaluations\":"+c.CandidateEvaluations+",\"candidates\":["+
            String.Join(",",a7Results.OrderBy(x=>x.Key.EdgeId).Select(k=>"{\"incident_peak\":"+k.Key.OutputIncident+
                ",\"reflection_peak\":"+k.Key.OutputReflection+",\"available\":"+(k.Value.Available?"true":"false")+
                ",\"eligible_before_matching\":"+(k.Value.Eligible?"true":"false")+",\"accepted\":"+(k.Key.State=="accepted"?"true":"false")+
                ",\"score\":"+NumberJson(k.Value.Score)+",\"reasons\":"+StringArrayJson(k.Value.Reasons)+",\"role\":"+a7RoleDetails[k.Key]+"}"))+"],"+
            "\"outputs\":"+output.Count+"}";
    }
    private static string A7OutputStatusJson(string value)
    {
        return value.Replace("AnalyzeSixthLocationIncremental","AnalyzeSeventhLocationIncremental")
            .Replace("HVX.Task17.Location.SixthLocationAnalyzer","HVX.Task17.Location.SeventhLocationAnalyzer")
            .Replace("\"algorithm_index\":5","\"algorithm_index\":6")
            .Replace("\"column26_algorithm_index\":5","\"column26_algorithm_index\":6")
            .Replace("sixth_strict_calibrated_weighted_evidence","seventh_sequential_calibrated_dual_lobe_pipeline")
            .Replace("fixed_raw_absolute_peak_pair","pre_matching_homologous_peak_or_peak_anchored_onset")
            .Replace("\"first_arrival_applied_after_pair_commit\":true","\"first_arrival_applied_after_pair_commit\":false")
            .Replace("\"pairing_independent_of_timing_switch\":true","\"pairing_independent_of_timing_switch\":false")
            .Replace("canonical_raw_peak_delay_with_endpoint_clamp_only_for_compensation","same_pre_matching_physical_timing_with_endpoint_clamp_only_for_compensation")
            .Replace("\"column33_range\":[0,1]","\"column33_range\":[0,1],\"column33_missing_evidence\":\"NaN\"")
            .Replace("\"score_zero_for_unpaired_means\":\"not_scored_not_zero_discharge_probability\"","\"score_NaN_for_unpaired_means\":\"not_scored_not_zero_discharge_probability\"")
            .Replace("diagnostic_only_no_automatic_flip","calibrated_endpoint_projection_with_original_geometry_retained")
            .Replace("A6_state_equals_accepted","A7_mandatory_physics_and_unique_local_or_independent_prior_selection");
    }
}
