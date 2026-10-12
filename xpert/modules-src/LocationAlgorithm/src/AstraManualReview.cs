using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4ReviewGuard
    {
        internal int Start,End;
        internal double Mean,Rms,MaximumResidual;
    }
    private sealed class A4ReviewPacket
    {
        internal A4Pulse Original,Pulse;
        internal A4ReviewGuard Before,After;
        internal string Reason;
    }
    private sealed class A4ReviewEdge
    {
        internal A4ReviewPacket A,B;
        internal A4Pair Pair;
        internal A4UnifiedCorrelationEvidence UnifiedCorrelation;
        internal int IncidentIndex=-1,ReflectionIndex=-1,CanonicalIncident=-1,CanonicalReflection=-1,CanonicalPeakDelay=-1;
        internal double CanonicalPosition=double.NaN;
        internal double RankingScore;
        internal bool CalibrationConflict;
        internal string Reason;
        internal bool Selected;
    }
    private static A4Context A4ReviewContext(A4Context source)
    {
        // Arrays below are read-only signal/geometry references. All mutable
        // matching collections belong to this NEW context and are never populated.
        var context=new A4Context {Raw=source.Raw,MeasurementSignal=source.MeasurementSignal,Signal=source.Signal,Center=source.Center,RawCenter=source.RawCenter,
            Sigma=source.Sigma,Threshold=source.Threshold,RawDiscoveryThreshold=source.RawDiscoveryThreshold,
            Fs=source.Fs,Length=source.Length,Velocity=source.Velocity,RoundTrip=source.RoundTrip,RoundTripExact=source.RoundTripExact,
            ConnectingCableLengthM=source.ConnectingCableLengthM,
            AttenuationTolerancePercent=source.AttenuationTolerancePercent,
            CalibrationToleranceAdaptationLimitPercent=source.CalibrationToleranceAdaptationLimitPercent,
            Joints=source.Joints,Velocities=source.Velocities,Filtered=source.Filtered,FirstArrival=source.FirstArrival,FirstArrivalPhysicalTiming=source.FirstArrivalPhysicalTiming,
            IndependentTiming=true,Attenuation=source.Attenuation,Width=source.Width,Calibration=source.Calibration,
            CalibrationPolarity=source.CalibrationPolarity,EffectiveCutoffs=source.EffectiveCutoffs,
            AttenuationScoringReference=source.AttenuationScoringReference,AttenuationScoringSignal=source.AttenuationScoringSignal,
            AttenuationScoringRawSigma=source.AttenuationScoringRawSigma,
            AttenuationScoringPackets=source.AttenuationScoringPackets,
            Algorithm=source.Algorithm,IncidentThreshold=source.IncidentThreshold,Suppression=source.Suppression,
            AnalysisPhaseQuadrants=source.AnalysisPhaseQuadrants,AnalysisInitialPhaseDegrees=source.AnalysisInitialPhaseDegrees,AnalysisFrequencyHz=source.AnalysisFrequencyHz,
            CycleReference=source.CycleReference,
            Index4ExtendedGuardSearch=source.Index4ExtendedGuardSearch,SixthHybridGuardSearch=source.SixthHybridGuardSearch,
            AnalysisLimit=source.AnalysisLimit,CandidateEvaluations=source.CandidateEvaluations,
            CandidateEvaluationLimit=source.CandidateEvaluationLimit,CandidatePoolLimitExceeded=source.CandidatePoolLimitExceeded,
            WindowBudgetExceeded=source.WindowBudgetExceeded,CandidateEvaluationLimitExceeded=source.CandidateEvaluationLimitExceeded};
#if HYBRID_CORE
        context.ShareHybridCycle(source);
#endif
        return context;
    }
    private static A4Pulse A4ReviewClone(A4Pulse p)
    {
        return new A4Pulse {Id=p.Id,Start=p.Start,End=p.End,Peak=p.Peak,RawPeak=p.RawPeak,Period=p.Period,Owner=-1,
            RawAmplitudeIndex=p.RawAmplitudeIndex,AnalysisOrigin=p.AnalysisOrigin,AnalysisSamples=p.AnalysisSamples,
            Amplitude=p.Amplitude,LocalAmplitude=p.LocalAmplitude,Energy=p.Energy,EquivalentSamples=p.EquivalentSamples,
            BandwidthHz=p.BandwidthHz,HighFraction=p.HighFraction,Snr=p.Snr,Baseline=p.Baseline,NoiseSigma=p.NoiseSigma,
            MappingValid=p.MappingValid,Clipped=p.Clipped,UnresolvedOverlap=p.UnresolvedOverlap,
            BaselineUnstable=p.BaselineUnstable,TriggeredFromRaw=p.TriggeredFromRaw,
            BoundaryRefined=p.BoundaryRefined,HasRefinedGuardProof=p.HasRefinedGuardProof,
            RefinedLeftGuardStart=p.RefinedLeftGuardStart,RefinedLeftGuardEnd=p.RefinedLeftGuardEnd,
            RefinedRightGuardStart=p.RefinedRightGuardStart,RefinedRightGuardEnd=p.RefinedRightGuardEnd};
    }
    private static bool A4ReviewQuietGuard(A4Context c,int start,int count,double amplitude,out A4ReviewGuard guard)
    {
        guard=null;
        if(start<0||start+count>c.Raw.Length)return false;
        double mean=0,energy=0,maximum=0;
        double[] corrected=A4RawAnalysis(c);
        for(int i=start;i<start+count;i++)mean+=corrected[i];
        mean/=count;
        // A small stable offset is allowable evidence for a boundary hypothesis.
        // A flat HIGH pulse cannot pass just because its variance is small.
        if(Math.Abs(mean-c.RawCenter)>.05*amplitude)return false;
        for(int i=start;i<start+count;i++)
        {double v=corrected[i]-mean;energy+=v*v;maximum=Math.Max(maximum,Math.Abs(v));}
        double rms=Math.Sqrt(energy/count);
        if(rms>2.5*c.Sigma||maximum>5*c.Sigma)return false;
        // Independent original-sample guard validation: remove only its fitted
        // affine offset/slope. A slow trend is not contamination; nonlinear pulse
        // residuals cannot be erased by the record-wide preprocessing profile.
        double rawMean=0,xy=0,xx=0,middle=(count-1)*.5;
        for(int k=0;k<count;k++){rawMean+=c.Raw[start+k];double x=k-middle;xy+=x*c.Raw[start+k];xx+=x*x;}
        rawMean/=count;double slope=xx>0?xy/xx:0,rawEnergy=0,rawMaximum=0;
        for(int k=0;k<count;k++){double v=c.Raw[start+k]-rawMean-slope*(k-middle);rawEnergy+=v*v;rawMaximum=Math.Max(rawMaximum,Math.Abs(v));}
        if(Math.Sqrt(rawEnergy/count)>2.5*c.Sigma||rawMaximum>5*c.Sigma)return false;
        guard=new A4ReviewGuard {Start=start,End=start+count-1,Mean=mean,Rms=rms,MaximumResidual=maximum};return true;
    }
    private static A4ReviewPacket A4ReviewBoundaryHypothesis(A4Context c,A4Pulse original)
    {
        var review=new A4ReviewPacket {Original=original,Pulse=A4ReviewClone(original),Reason="original_complete_unowned_packet"};
        if(!original.UnresolvedOverlap)return review;
        const int guardSamples=12;
        int peak=A4ChargeIndex(original);double amplitude=A4RawCharge(c,peak);
        A4ReviewGuard before=null,after=null,test;
        for(int i=peak-guardSamples;i>=Math.Max(original.Start,peak-c.RoundTrip);i--)
            if(A4ReviewQuietGuard(c,i,guardSamples,amplitude,out test)){before=test;break;}
        for(int i=peak+1;i<=Math.Min(original.End-guardSamples+1,peak+c.RoundTrip);i++)
            if(A4ReviewQuietGuard(c,i,guardSamples,amplitude,out test)){after=test;break;}
        review.Before=before;review.After=after;
        if(before==null||after==null)
        {review.Reason="unresolved_packet_without_two_complete_quiet_guards";review.Pulse=null;return review;}
        int start=before.End+1,end=after.Start-1;
        if(start>=peak||end<=peak||end-start>c.RoundTrip||
            AbsoluteMaximumIndex(A4RawAnalysis(c),start,end,c.RawCenter)!=peak)
        {review.Reason="quiet_guard_hypothesis_does_not_preserve_complete_dominant_packet";review.Pulse=null;return review;}
        A4Pulse p=review.Pulse;p.Start=start;p.End=end;p.UnresolvedOverlap=false;
        p.Peak=start;
        for(int i=start+1;i<=end;i++)
            if(Math.Abs(A4Sample(c.Signal,p,i)-c.Center)>Math.Abs(A4Sample(c.Signal,p,p.Peak)-c.Center))p.Peak=i;
        int radius=c.Filtered?2:0;
        p.RawPeak=AbsoluteMaximumIndex(A4RawAnalysis(c),Math.Max(start,p.Peak-radius),Math.Min(end,p.Peak+radius),c.RawCenter);
        p.MappingValid=Math.Abs(p.RawPeak-p.Peak)<=radius&&A4RawCharge(c,p.RawPeak)>=3*c.Sigma;
        double[] left=new double[guardSamples],right=new double[guardSamples];
        for(int i=0;i<guardSamples;i++)
        {left[i]=A4Sample(c.Signal,p,before.Start+i);right[i]=A4Sample(c.Signal,p,after.Start+i);}
        double[] noise=left.Concat(right).ToArray();p.Baseline=Median(noise);
        p.NoiseSigma=Math.Max(c.Sigma,RobustSigma(noise,p.Baseline));
        p.BaselineUnstable=Math.Abs(Median(left)-Median(right))>3*p.NoiseSigma;
        p.Amplitude=Math.Abs(A4Sample(c.Signal,p,p.Peak)-c.Center);
        p.Snr=Math.Abs(A4Sample(c.Signal,p,p.Peak)-p.Baseline)/Math.Max(1e-15,p.NoiseSigma);
        A4Measure(c,p);review.Reason="unresolved_raw_packet_with_two_low_offset_quiet_guard_hypothesis";return review;
    }
    private static bool A4ReviewConflicts(A4Pulse p,List<A4Pulse> confirmed)
    {return confirmed.Any(q=>p.Start<=q.End&&q.Start<=p.End);}
    private static string A4ReviewGuardJson(A4ReviewGuard g)
    {
        if(g==null)return "null";
        return "{\"start\":"+g.Start+",\"end\":"+g.End+",\"raw_mean\":"+NumberJson(g.Mean)+
            ",\"demeaned_rms\":"+NumberJson(g.Rms)+",\"maximum_demeaned_residual\":"+NumberJson(g.MaximumResidual)+"}";
    }
    private static string A4ReviewPacketJson(A4ReviewPacket p)
    {
        return "{\"packet_id\":"+p.Original.Id+",\"raw_peak\":"+A4ChargeIndex(p.Original)+
            ",\"original_start\":"+p.Original.Start+",\"original_end\":"+p.Original.End+
            ",\"hypothesis_start\":"+(p.Pulse==null?"null":p.Pulse.Start.ToString(Inv))+
            ",\"hypothesis_end\":"+(p.Pulse==null?"null":p.Pulse.End.ToString(Inv))+
            ",\"reason\":"+Json(p.Reason)+",\"before_guard\":"+A4ReviewGuardJson(p.Before)+
            ",\"after_guard\":"+A4ReviewGuardJson(p.After)+"}";
    }
    private string A4CollectManualReview(A4Context c,List<A4Pulse> pulses,int phase,long recordId,double voltage,
        double frequency,double initialPhase,bool automatic,int pre,int post,double tanDelta,bool quietRecovery=false)
    {
        if(quietRecovery&&(c.WindowBudgetExceeded||c.CandidateEvaluationLimitExceeded))
            return "{\"enabled\":false,\"reason\":\"primary_discovery_budget_incomplete_no_extra_search\"}";
        if(!quietRecovery){reviewCurrentParameterRows.Clear();reviewCurrentRawRows.Clear();reviewCurrentFilteredRows.Clear();}
        reviewCurrentPhase=phase;reviewCurrentRecordId=recordId;
        var context=A4ReviewContext(c);var confirmed=pulses.Where(p=>p.Owner>=0).ToList();
        context.ReviewQuietGuardRecovery=quietRecovery;
        var alreadyRepresented=new HashSet<int>();
        if(quietRecovery)foreach(double[] row in reviewCurrentParameterRows)
            alreadyRepresented.Add(A4RegisteredCanonicalIdentity(row).Incident);
        int previousReviewCount=reviewCurrentParameterRows.Count;
        var visited=new HashSet<int>(c.AnalysisVisitIds);
        if(quietRecovery)
        {
            var representedIds=new HashSet<int>(pulses.Where(p=>alreadyRepresented.Contains(A4ChargeIndex(p))).Select(p=>p.Id));
            pulses=A4ExpandReviewReflections(context,pulses,visited,representedIds);
            c.CandidateEvaluations=context.CandidateEvaluations;
        }
        var hypotheses=new Dictionary<int,A4ReviewPacket>();var attempts=new List<A4ReviewEdge>();
        // Review may establish a different guarded support. Prepare those exact
        // packet identities in a private cache, never reuse the normal-pool cache.
        foreach(A4Pulse p in pulses.Where(p=>p.Owner<0))hypotheses.Add(p.Id,
            quietRecovery?A4ReviewQuietProof(context,p,pulses):A4ReviewBoundaryHypothesis(context,p));
        var prepared=pulses.Select(p=>hypotheses.ContainsKey(p.Id)&&hypotheses[p.Id].Pulse!=null?
            hypotheses[p.Id].Pulse:p).ToList();
        // The display band can hide small ORIGINAL packets that still occupy a
        // quiet guard. Index4 adds only raw-domain occupancy masks to this
        // private measurement context, never to the source/candidate loops.
        // Direct attenuation and unified correlation see the same frozen list.
        var occupancy=c.Algorithm==4?A6BoundaryRawOccupancy(c,prepared):new List<A4Pulse>();
        var guarded=prepared.Concat(occupancy).ToList();
        if(c.Algorithm==4)context.AttenuationScoringPackets=guarded;
        A4PrepareUnifiedCorrelationReview(context,c,guarded);
        foreach(A4Pulse source in pulses.Where(p=>p.Owner<0&&visited.Contains(p.Id)&&!alreadyRepresented.Contains(A4ChargeIndex(p))&&
                A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold)
            .OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(p=>A4ChargeIndex(p)).Take(c.AnalysisLimit))
        {
            A4ReviewPacket ar;
            if(!hypotheses.TryGetValue(source.Id,out ar)){ar=A4ReviewBoundaryHypothesis(context,source);hypotheses.Add(source.Id,ar);}
            if(ar.Pulse==null||A4ReviewConflicts(ar.Pulse,confirmed))continue;
            A4ReviewEdge best=null;
            foreach(A4Pulse possible in pulses.Where(p=>p.Owner<0&&p.Id!=source.Id&&p.Start>ar.Pulse.Start&&
                (long)p.Start-A4ChargeIndex(ar.Pulse)<=A4ReviewDelayLimit(context,ar.Pulse,p)))
            {
                if(quietRecovery&&c.CandidateEvaluations>=c.CandidateEvaluationLimit)break;
                if(quietRecovery)c.CandidateEvaluations++;
                A4ReviewPacket br;
                if(!hypotheses.TryGetValue(possible.Id,out br)){br=A4ReviewBoundaryHypothesis(context,possible);hypotheses.Add(possible.Id,br);}
                var edge=new A4ReviewEdge {A=ar,B=br,Reason="not_evaluated"};attempts.Add(edge);
                if(br.Pulse==null){edge.Reason=br.Reason;continue;}
                A4Pulse a=ar.Pulse,b=br.Pulse;
                if(A4ReviewConflicts(b,confirmed)){edge.Reason="conflict_with_confirmed_owned_packet";continue;}
                if(a.End>=b.Start||a.Clipped||b.Clipped)
                {edge.Reason=a.End>=b.Start?"hypothesized_packets_still_overlap":"clipped_packet_support";continue;}
                if(!a.MappingValid||!b.MappingValid){edge.Reason="invalid_strict_raw_peak_mapping";continue;}
                // Keep raw peak identities for shape/charge measurements; physical
                // geometry, reference matching and compensation use the selected
                // timing mode, consistently with the final output coordinates.
                int peakIncident,peakReflection;
                if(!A4CanonicalPeaks(context,a,b,out peakIncident,out peakReflection))
                {edge.Reason="canonical_raw_homologous_peaks_unavailable";continue;}
                edge.CanonicalIncident=peakIncident;edge.CanonicalReflection=peakReflection;
                int physicalA,physicalB;
                if(!A4PhysicalTiming(context,a,b,out physicalA,out physicalB)){edge.Reason="first_arrival_unresolved_no_peak_fallback";continue;}
                int peakDelay=physicalB-physicalA;edge.CanonicalPeakDelay=peakReflection-peakIncident;
                if(peakDelay<=0||peakDelay>A4ReviewDelayLimit(context,a,b))
                {edge.Reason=peakDelay<=0?"nonpositive_canonical_peak_delay":"canonical_peak_delay_exceeds_manual_review_uncertainty";continue;}
                edge.CanonicalPosition=A4BoundaryOutputPosition(peakDelay,c.Fs,c.Joints,c.Velocities,c.Length);
                double wave,env;A4Correlate(context,a,b,out wave,out env);
                A4UnifiedCorrelationEvidence unified=A4UnifiedCorrelation(context,a,b,peakDelay);
                edge.UnifiedCorrelation=unified;
                if(!unified.Available)
                {edge.Reason="guarded_correlation_measurement_unavailable";continue;}
                double correlation=unified.Measured.Fixed,ratio=b.LocalAmplitude/Math.Max(1e-30,a.LocalAmplitude);
                // Rank with the same explicit 50/35/15 evidence as normal pairs.
                // The output cap expresses unaccepted status, not a ranking tie:
                // apply it AFTER comparing the full weighted evidence.
                double snrScore=1-Math.Exp(-Math.Pow(Math.Max(0,Math.Min(a.Snr,b.Snr)-3)/5,2));
                double widthScore=Math.Exp(-Math.Abs(Math.Log(Math.Max(.01,b.EquivalentSamples)/Math.Max(.01,a.EquivalentSamples)))*.65);
                double hfRatio=(b.HighFraction+.002)/(a.HighFraction+.002);
                double spectrum=Math.Exp(-Math.Max(0,Math.Log(hfRatio))*.8-Math.Abs(Math.Log(hfRatio))*.10);
                double baseScore=A4WeightedEvidenceScore(unified.Score,widthScore,spectrum,snrScore,c.Width,null);
                A4AttenuationEvidence deviation=A4CalibrationDeviation(context,a,b);
                edge.RankingScore=A4WeightedEvidenceScore(unified.Score,widthScore,spectrum,snrScore,c.Width,deviation);
                edge.CalibrationConflict=A4CalibrationConflict(deviation);
                double score=Math.Min(.69,edge.RankingScore);
                // A new local ambiguity is an acceptance veto, not a lower
                // original-method feature score. Preserve only exact unchanged
                // candidates from that rejected component, never new hypotheses.
                A4Pair originalLocalReview=A4LocalOriginalReviewPair(c,a,b);
                if(originalLocalReview!=null)
                {score=originalLocalReview.Confidence;edge.RankingScore=originalLocalReview.Confidence;}
                bool alternativeTemporal=quietRecovery&&unified.Measured.AlignmentRule.StartsWith("review_only_earliest_significant_",StringComparison.Ordinal);
                if(alternativeTemporal)score=Math.Min(.49,score);
                var pair=new A4Pair {Incident=a,Reflection=b,WaveCorrelation=wave,EnvelopeCorrelation=env,
                    Correlation=correlation,AmplitudeRatio=ratio,Confidence=score,CalibrationDeviation=deviation,
                    UnifiedCorrelation=unified,SignedCorrelationMinimum=unified.Floor,
                    ScoreBeforeCalibration=baseScore,CalibrationPenaltyFactor=baseScore>0?edge.RankingScore/baseScore:1};edge.Pair=pair;
                if(correlation<0||correlation>1+1e-12)
                {edge.Reason="reversed_or_negative_complete_wave_similarity";continue;}
                if(correlation>1){correlation=1;pair.Correlation=1;} // Numerical roundoff only.
                if(!Finite(correlation)||!Finite(score)||!Finite(ratio)||ratio>3)
                {edge.Reason=ratio>3?"reflection_amplitude_more_than_three_times_incident":"non_finite_review_evidence";continue;}
                pair.Position=edge.CanonicalPosition;
                bool restored=source.UnresolvedOverlap||possible.UnresolvedOverlap;
                var reasons=new List<string>{restored?"manual_only_unresolved_boundary_hypothesis":"manual_only_unconfirmed_pair"};
                if(originalLocalReview!=null)reasons.Add(A4LocalAmbiguityReason+";original_method_score_preserved_acceptance_zero");
                if(alternativeTemporal)reasons.Add("three_lobe_temporal_hypothesis_review_score_capped_0.49");
                if(peakDelay>c.RoundTrip)reasons.Add("manual_only_peak_delay_round_trip_uncertainty");
                if(!unified.Passed)reasons.Add("dynamic_correlation_below_floor_unaccepted_review_only");
                if(a.BaselineUnstable||b.BaselineUnstable)reasons.Add("unstable_local_baseline");
                if(Math.Min(a.Snr,b.Snr)<4.5)reasons.Add("low_local_snr");
                if(ratio>1.20+2/Math.Max(3,a.Snr))reasons.Add("reflection_amplification_inconsistent");
                if(edge.CalibrationConflict)reasons.Add("calibration_outside_measured_tolerance");
                reasons.Add(deviation.Reason);reasons.Add("global_ownership_not_revalidated");edge.Reason=String.Join(";",reasons);
                if(best==null||!best.Pair.UnifiedCorrelation.Passed&&unified.Passed||
                    best.Pair.UnifiedCorrelation.Passed==unified.Passed&&(best.CalibrationConflict&&!edge.CalibrationConflict||
                    best.CalibrationConflict==edge.CalibrationConflict&&(edge.RankingScore>best.RankingScore||
                    edge.RankingScore==best.RankingScore&&unified.Score>best.Pair.UnifiedCorrelation.Score||
                    edge.RankingScore==best.RankingScore&&unified.Score==best.Pair.UnifiedCorrelation.Score&&peakReflection<best.CanonicalReflection)))best=edge;
            }
            if(best==null)continue;
            A4Pair chosen=best.Pair;A4Pulse incident=chosen.Incident;
            // Only the selected peak pair is converted to first-arrival display
            // indices. A near-round-trip hypothesis stays an unaccepted row;
            // preserve a signed extrapolated coordinate, never a clamped location.
            int t0,t1;
            if(!A4OutputTiming(context,chosen,c.FirstArrival,out t0,out t1))
            {best.Reason+=";"+chosen.OutputTimingReason;continue;}
            // Keep the requested first-arrival indices. Final projection marks
            // an out-of-window reflection unresolved; it never clamps a cursor
            // or silently changes the requested timing back to a peak.
            best.Reason+=";"+chosen.OutputTimingReason;
            best.IncidentIndex=t0;best.ReflectionIndex=t1;best.Selected=true;
            int outputDelay=t1-t0;
            chosen.Position=A4BoundaryOutputPosition(outputDelay,c.Fs,c.Joints,c.Velocities,c.Length);
            if(!Finite(chosen.Position))best.Reason+=";output_timing_position_unavailable_invalid_geometry";
            else if(chosen.Position<0||chosen.Position>c.Length)
                best.Reason+=";output_timing_position_signed_boundary_extrapolation_unaccepted";
            if(Finite(best.CanonicalPosition)&&(best.CanonicalPosition<0||best.CanonicalPosition>c.Length))
                best.Reason+=";canonical_compensation_clamped_to_nearest_endpoint";
            int peak=A4ChargeIndex(incident),start,end;
            if(!A4CompletePdmapCrop(c,peak,t0,pre,post,out start,out end))continue;
            double[] row=NewNaNRow();row[0]=phase;row[1]=recordId;row[2]=voltage;
            row[3]=A4PhaseAt(c,best.IncidentIndex);
            row[4]=c.IncidentThreshold;row[5]=c.Length;row[6]=c.Velocity;row[7]=c.Fs;row[8]=recordId;
            row[9]=reviewCurrentParameterRows.Count;row[10]=best.IncidentIndex;row[11]=best.ReflectionIndex;
            row[12]=best.ReflectionIndex-best.IncidentIndex;row[13]=chosen.Position;row[14]=c.RoundTrip;
            row[15]=A4RawCharge(c,peak);
            row[16]=c.Attenuation?A4ContextCompensateCharge(c,row[15],best.CanonicalPosition):row[15];
            row[17]=row[15]>0?row[16]/row[15]:1;row[18]=incident.Energy/c.Fs;row[19]=tanDelta;row[20]=best.IncidentIndex-start;
            A4RememberWidthPair(row,chosen);
            row[21]=chosen.Correlation;row[22]=A4OutputAttenuationRatio(context,chosen.Incident,chosen.Reflection);row[23]=double.NaN;row[24]=double.NaN;
            row[25]=tanDelta;row[26]=c.Algorithm;row[27]=c.Suppression;row[28]=c.Sigma;
            row[29]=A4RawBaselineAt(c,peak)+c.IncidentThreshold;row[30]=A4RawBaselineAt(c,peak)-c.IncidentThreshold;
            row[31]=start;row[32]=end;row[33]=chosen.Confidence;row[34]=automatic?1:0;row[35]=0;
            A4RegisterCanonicalOutputIdentity(row,best.CanonicalIncident,best.CanonicalReflection,chosen);
            reviewCurrentParameterRows.Add(row);reviewCurrentRawRows.Add(A4Slice(c.Raw,start,end));
            reviewCurrentFilteredRows.Add(A4MatchedFilteredView(context,incident,start,end));
        }
        A4SortRowsByCharge(reviewCurrentParameterRows,reviewCurrentRawRows,reviewCurrentFilteredRows);
        // Only propagate display diagnostics; never share the review matching graph.
        if(c.Algorithm==4)
            foreach(var display in context.Index4DisplayDiagnostics)
                c.Index4DisplayDiagnostics[display.Key]=display.Value;
        var json=new StringBuilder("{\"confirmed_results_modified\":false,\"owners_committed\":false,\"scope\":\"unowned_incidents_in_existing_analysis_visit_budget\",\"status\":\"manual_review_only\",\"maximum_one_row_per_incident\":true,\"pending_candidates_may_share_unowned_packets\":true,\"project_acceptance_column35\":0,\"reference_score_cap\":0.69,\"reference_score_definition\":\"min(0.69,weighted_correlation_attenuation_other); rank_before_cap; calibration_compatible_candidates_first\",\"nominal_weights\":[0.50,0.35,0.15],\"reference_score_is_calibrated_probability\":false,\"equivalent_width_and_bandwidth\":\"not_validated_NaN\",\"guard_rule\":\"12 raw samples; abs(mean-global_center)<=0.05*raw_peak; demeaned_RMS<=2.5*original_sigma; max_demeaned_residual<=5*original_sigma; no signal normalization\",\"added_rows\":");
        json.Append(reviewCurrentParameterRows.Count-previousReviewCount)
            .Append(",\"fixed16_quiet_proof_recovery\":").Append(quietRecovery?"true":"false")
            .Append(",\"reflection_only_inventory\":").Append(quietRecovery?a4ReviewReflectionExpansionDiagnosticJson:"null")
            .Append(",\"original_occupancy_only_mask_count\":").Append(occupancy.Count)
            .Append(",\"occupancy_masks_enter_candidate_loop\":false,\"direct_and_correlation_measurement_occupancy_identical\":").Append(c.Algorithm==4?"true":"null")
            .Append(",\"local_ambiguity_original_score_exception\":\"exact unchanged original-pool pair from locally-ambiguous component retains its method score; column35 remains zero\"")
            .Append(",\"correlation_policy\":\"guarded_3mhz_dynamic_CAL_floor; available_below_floor_is_unaccepted_review_only; pass_before_calibration_compatibility_before_normalized_score\"")
            .Append(",\"column21\":\"measured_signed_guarded_3mhz_Pearson_not_normalized_score\"")
            .Append(",\"column22\":\"guarded_fixed_3mhz_reflection_to_incident_absolute_peak_ratio;NaN_if_unavailable;independent_of_attenuation_switch\"")
            .Append(",\"unified_correlation_reference\":").Append(A4UnifiedCorrelationReferenceJson(context))
            .Append(",\"packet_hypotheses\":[");int n=0;
        foreach(A4ReviewPacket p in hypotheses.Values.OrderBy(p=>p.Original.Id)){if(n++>0)json.Append(',');json.Append(A4ReviewPacketJson(p));}
        json.Append("],\"candidates\":[");n=0;
        foreach(A4ReviewEdge edge in attempts)
        {
            if(n++>0)json.Append(',');json.Append("{\"incident_raw_peak\":").Append(A4ChargeIndex(edge.A.Original))
                .Append(",\"reflection_raw_peak\":").Append(A4ChargeIndex(edge.B.Original)).Append(",\"selected_for_manual_review\":").Append(edge.Selected?"true":"false")
                .Append(",\"output_incident_index\":").Append(edge.IncidentIndex<0?"null":edge.IncidentIndex.ToString(Inv))
                .Append(",\"output_reflection_index\":").Append(edge.ReflectionIndex<0?"null":edge.ReflectionIndex.ToString(Inv))
                .Append(",\"raw_charge_pc\":").Append(edge.CanonicalIncident<0?"null":NumberJson(A4RawCharge(c,edge.CanonicalIncident)))
                .Append(",\"canonical_peak_delay\":").Append(edge.CanonicalPeakDelay<0?"null":edge.CanonicalPeakDelay.ToString(Inv))
                .Append(",\"canonical_incident_peak\":").Append(edge.CanonicalIncident<0?"null":edge.CanonicalIncident.ToString(Inv))
                .Append(",\"canonical_reflection_peak\":").Append(edge.CanonicalReflection<0?"null":edge.CanonicalReflection.ToString(Inv))
                .Append(",\"canonical_compensation_position\":").Append(NumberJson(edge.CanonicalPosition))
                .Append(",\"output_timing_reason\":").Append(edge.Pair==null?"null":Json(edge.Pair.OutputTimingReason))
                .Append(",\"reason\":").Append(Json(edge.Reason)).Append(",\"signed_correlation\":").Append(edge.Pair==null?"null":NumberJson(edge.Pair.WaveCorrelation))
                .Append(",\"envelope_correlation\":").Append(edge.Pair==null?"null":NumberJson(edge.Pair.EnvelopeCorrelation))
                .Append(",\"measured_correlation\":").Append(edge.Pair==null?"null":NumberJson(edge.Pair.Correlation))
                .Append(",\"unified_correlation\":").Append(A4UnifiedCorrelationJson(edge.UnifiedCorrelation))
                .Append(",\"base_score_before_cal\":").Append(edge.Pair==null?"null":NumberJson(edge.Pair.ScoreBeforeCalibration))
                .Append(",\"calibration_deviation\":").Append(A4AttenuationJson(edge.Pair==null?null:edge.Pair.CalibrationDeviation))
                .Append(",\"reference_score\":").Append(edge.Pair==null?"null":NumberJson(edge.Pair.Confidence)).Append('}');
        }
        string result=json.Append("]}").ToString();
        return quietRecovery?result.Replace("12 raw samples;","fixed 16 raw samples selected once per packet before pair scoring;"):result;
    }
}
