using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private static bool A4CandidateScoresClose(double first,double second,bool absolute)
    {
        // An unavailable runner-up is not evidence of a close competing score.
        // Explicit finite guards also preserve unordered-NaN semantics through
        // the release control-flow protector, which reverses comparison branches.
        if(!Finite(first)||!Finite(second))return false;
        double gap=absolute?Math.Abs(first-second):first-second;
        if(!Finite(gap))return false;
        return gap<=.05;
    }
    // Output-only evidence. No valid guard, measurement cache entry, or packet
    // ownership is installed. The original matching graph remains unchanged.
    private sealed class A4BestCandidateReview
    {
        internal A4Pair Pair;
        internal int IncidentTiming,ReflectionTiming,PeakIncident,PeakReflection;
        internal int CanonicalIncident,CanonicalReflection,CandidateCount,RunnerUpReflection=-1;
        internal double Score,Rho,RunnerUpScore=double.NaN,RunnerUpRho=double.NaN;
        internal double SelectedPeakSnr,SnrEvidenceFactor;
        // Normalized 0..1 original method score, not percentage/probability.
        internal double OriginalEngineeringScore=double.NaN;
        internal double IncidentBaseline,ReflectionBaseline,IncidentValue,ReflectionValue,AmplitudeRatio;
        internal int IncidentSupportLobeCount,ReflectionSupportLobeCount,IncidentAnchorStartOrdinal,ReflectionAnchorStartOrdinal;
        internal int IncidentAnchorFirst,IncidentAnchorSecond=-1,ReflectionAnchorFirst,ReflectionAnchorSecond=-1;
        internal bool GuardedEvidenceAvailable;
        internal bool LocalLobeOrderResolved;
        internal A6OutputReview SixthOutputReview;
        internal A4AttenuationEvidence SharedAttenuation;
        internal string Reason,DiagnosticJson;
    }

    private sealed class A4CandidateRawLobe
    {
        internal int Start,End,Peak,Sign;
        internal double Amplitude,SumSquares;
    }

    private sealed class A4CandidateRawAnchor
    {
        internal int SupportStartOrdinal,CanonicalOrdinal;
        internal A4CandidateRawLobe First,Second;
    }

    private static A4CandidateRawAnchor A4CandidateLocalRawAnchor(A4Pulse pulse,
        List<A4CandidateRawLobe> lobes,int canonical)
    {
        if(pulse==null||lobes==null)return null;
        int main=lobes.FindIndex(l=>canonical>=l.Start&&canonical<=l.End);
        if(main<0)return null;
        int partner=-1;
        // A discovered support can include precursor/tail lobes. Its global
        // lobe ordinal is not the order within the main pulse. Freeze the
        // canonical lobe and temporally nearest adjacent opposite lobe independently
        // for EACH raw packet, before seeing its partner or computing rho.
        // Peak height must NOT choose which side is the leading lobe: unequal
        // lobe attenuation can otherwise turn a trailing lobe into a new onset.
        // This is an anchor choice, not a cross-packet amplitude/energy gate.
        double reach=Math.Max(8,2d*pulse.Period);
        foreach(int index in new[]{main-1,main+1})
        {
            if(index<0||index>=lobes.Count||lobes[index].Sign==lobes[main].Sign||
                Math.Abs((double)lobes[index].Peak-canonical)>reach)continue;
            if(partner<0||Math.Abs(lobes[index].Peak-canonical)<Math.Abs(lobes[partner].Peak-canonical))partner=index;
        }
        int first=partner<0?main:Math.Min(main,partner),second=partner<0?-1:Math.Max(main,partner);
        return new A4CandidateRawAnchor{SupportStartOrdinal=first,CanonicalOrdinal=main-first,
            First=lobes[first],Second=second<0?null:lobes[second]};
    }

    private static List<A4CandidateRawLobe> A4CandidateRawLobes(A4Context c,A4Pulse p,double noise)
    {
        if(p==null||p.Start<0||p.End>=c.Raw.Length||p.Start>=p.End)return null;
        var runs=new List<A4CandidateRawLobe>();A4CandidateRawLobe run=null;
        for(int i=p.Start;i<=p.End;i++)
        {
            double value=A4RawSigned(c,i);if(!Finite(value))return null;
            int sign=Math.Sign(value);
            if(sign==0){run=null;continue;}
            if(run==null||run.Sign!=sign)
            {run=new A4CandidateRawLobe{Start=i,End=i,Peak=i,Sign=sign};runs.Add(run);}
            run.End=i;run.SumSquares+=value*value;
            if(!Finite(run.SumSquares))return null;
            if(Math.Abs(value)>run.Amplitude){run.Amplitude=Math.Abs(value);run.Peak=i;}
        }
        var significant=new List<A4CandidateRawLobe>();
        foreach(var item in runs)
        {
            int n=item.End-item.Start+1;
            // Peak AND integrated 3-sigma significance. No narrow/tall versus
            // broad/short energy-ratio matching gate.
            double limit=noise*noise*(n+3*Math.Sqrt(2d*n));
            if(n<2||item.Amplitude<=3*noise||!Finite(limit)||item.SumSquares<=limit)continue;
            var previous=significant.Count==0?null:significant[significant.Count-1];
            bool connected=previous!=null&&previous.Sign==item.Sign;
            if(connected)
            {
                int quiet=0;
                for(int k=previous.End+1;k<item.Start;k++)
                {
                    quiet=Math.Abs(A4RawSigned(c,k))<=3*noise?quiet+1:0;
                    if(quiet>=16){connected=false;break;}
                }
            }
            if(connected)
            {
                previous.End=item.End;previous.SumSquares+=item.SumSquares;
                if(item.Amplitude>previous.Amplitude){previous.Peak=item.Peak;previous.Amplitude=item.Amplitude;}
            }
            else significant.Add(item);
        }
        return significant;
    }

    private static bool A4CandidateFixedSignedRho(A4Context c,A4Pulse a,A4Pulse b,int pa,int pb,out double rho)
    {
        rho=double.NaN;
        var signal=c.MeasurementSignal==null?null:c.MeasurementSignal.BandSignal;
        if(signal==null||signal.Length!=c.Raw.Length||a.End>=b.Start)return false;
        int begin=Math.Min(a.Start-pa,b.Start-pb),end=Math.Max(a.End-pa,b.End-pb);
        double scaleA=0,scaleB=0;
        for(int i=a.Start;i<=a.End;i++)
        {if(!Finite(signal[i]))return false;scaleA=Math.Max(scaleA,Math.Abs(signal[i]));}
        for(int i=b.Start;i<=b.End;i++)
        {if(!Finite(signal[i]))return false;scaleB=Math.Max(scaleB,Math.Abs(signal[i]));}
        if(scaleA<=0||scaleB<=0)return false;
        double meanA=0,meanB=0,aa=0,bb=0,ab=0;int count=0;
        // Both complete frozen supports, zero outside each. No lag search,
        // abs(rho), sign flip, or score-dependent support cropping.
        for(int k=begin;k<=end;k++)
        {
            int ia=pa+k,ib=pb+k;
            double va=ia>=a.Start&&ia<=a.End?signal[ia]/scaleA:0;
            double vb=ib>=b.Start&&ib<=b.End?signal[ib]/scaleB:0;
            count++;double da=va-meanA,db=vb-meanB;meanA+=da/count;meanB+=db/count;
            aa+=da*(va-meanA);bb+=db*(vb-meanB);ab+=da*(vb-meanB);
        }
        if(count<3||!Finite(aa)||!Finite(bb)||!Finite(ab)||aa<=0||bb<=0)return false;
        double value=(ab/Math.Sqrt(aa))/Math.Sqrt(bb);
        if(!Finite(value)||value< -1-1e-12||value>1+1e-12)return false;
        rho=Clamp(value,-1,1);return true;
    }

    // Passing exactly one reflection constrains fallback of an existing pair.
    // Otherwise use real discovered/evaluated packet references, never maxima
    // invented by searching the output waveform or by taking its last sample.
    private A4BestCandidateReview A4FindBestOutputCandidate(A4Context c,A4Pulse incident,
        IEnumerable<A4Pulse> discovered,bool firstArrival,int displayStart=0,int displayEnd=int.MaxValue)
    {
        if(c==null||c.Algorithm<0||c.Algorithm>5||c.Raw==null||incident==null||
            !incident.MappingValid||incident.Clipped||!Finite(c.Fs)||c.Fs<=0||
            !Finite(c.RoundTripExact)||c.RoundTripExact<=0)return null;
        // Recovery is a new reflection choice, not a historical/manual choice.
        // If attenuation was requested, an absent/unqualified original-domain
        // CAL cannot be replaced by sorting unknown scores on correlation.
        // Keep the independent incident waveform unresolved. A qualified CAL
        // outside the requested pair tolerance can still yield review-only
        // candidates, and an explicitly disabled reference retains its policy.
        if(c.Attenuation&&(c.AttenuationScoringReference==null||!c.AttenuationScoringReference.Valid))return null;
        int ca=A4ChargeIndex(incident);
        if(ca<incident.Start||ca>incident.End||ca<0||ca>=c.Raw.Length)return null;
        double noise=c.MeasurementSignal==null?c.Sigma:c.MeasurementSignal.RawSigma;
        if(!Finite(noise)||noise<=0||A4RawCharge(c,ca)<=3*noise)return null;
        var supplied=discovered==null?new List<A4Pulse>():discovered.Where(p=>p!=null).ToList();
        bool constrained=supplied.Count==1;
        var packets=new Dictionary<int,A4Pulse>();
        Action<A4Pulse> remember=p=>
        {
            if(p==null)return;int peak=A4ChargeIndex(p);
            if(peak>=0&&!packets.ContainsKey(peak))packets.Add(peak,p);
        };
        foreach(var pulse in supplied)remember(pulse);
        if(!constrained)
        {
            if(c.CandidatePool!=null)foreach(var p in c.CandidatePool)
                if(A4ChargeIndex(p.Incident)==ca)remember(p.Reflection);
            if(c.RejectedPairs!=null)foreach(var p in c.RejectedPairs)
                if(A4ChargeIndex(p.Incident)==ca)remember(p.Reflection);
            if(c.Algorithm==5)foreach(var p in a6Evidence.Keys)
                if(A4ChargeIndex(p.Incident)==ca)remember(p.Reflection);
        }
        // Do not publish a prefix winner of a truncated candidate scan.
        if(packets.Count>4096)return null;
        var lobesA=A4CandidateRawLobes(c,incident,noise);
        if(lobesA==null||lobesA.Count==0)return null;
        var anchorA=A4CandidateLocalRawAnchor(incident,lobesA,ca);
        if(anchorA==null)return null;
        int ordinal=anchorA.CanonicalOrdinal;
        var results=new List<A4BestCandidateReview>();
        foreach(var reflection in packets.Values.OrderBy(A4ChargeIndex))
        {
            int cb=A4ChargeIndex(reflection);
            if(reflection.Id==incident.Id||cb<=ca||!reflection.MappingValid||reflection.Clipped||
                reflection.Start<=incident.End||cb<reflection.Start||cb>reflection.End||
                reflection.Start<0||reflection.End>=c.Raw.Length||A4RawCharge(c,cb)<=3*noise)continue;
            if(!constrained&&reflection.Owner>=0)continue;
            int limit=A4ReflectionMaximumDelay(c);
            if(cb-ca>limit)continue;
            var lobesB=A4CandidateRawLobes(c,reflection,noise);
            var anchorB=A4CandidateLocalRawAnchor(reflection,lobesB,cb);
            if(anchorB==null)continue;
            bool localOrder=anchorB.First.Sign==anchorA.First.Sign;
            // Different precursor/tail lobe order is not proof of no reflection.
            // Preserve two independently frozen canonical peaks when they have
            // the same polarity; full signed rho will expose weak similarity.
            // Never search another lobe merely to repair an inverted canonical
            // pair when its local order is also unresolved.
            var timingLobeB=localOrder?(ordinal==0?anchorB.First:anchorB.Second):
                Math.Sign(A4RawSigned(c,cb))==Math.Sign(A4RawSigned(c,ca))?
                    lobesB.FirstOrDefault(l=>cb>=l.Start&&cb<=l.End):null;
            if(timingLobeB==null||timingLobeB.Sign!=Math.Sign(A4RawSigned(c,ca)))continue;
            int pa=ca,pb=localOrder?timingLobeB.Peak:cb;
            if(pa<displayStart||pb>displayEnd||pb<=pa||pb-pa>limit||Math.Sign(A4RawSigned(c,pa))!=Math.Sign(A4RawSigned(c,pb)))continue;
            double rho;if(!A4CandidateFixedSignedRho(c,incident,reflection,pa,pb,out rho)||rho<=0)continue;
            int evidenceDelay=pb-pa;
            if(c.FirstArrivalPhysicalTiming)
            {int physicalA,physicalB;if(!A4PhysicalTiming(c,incident,reflection,out physicalA,out physicalB))continue;evidenceDelay=physicalB-physicalA;
                if(!A4ReflectionDelayWithinWindow(c,evidenceDelay)||!A4BoundaryTimingValid(c,evidenceDelay))continue;}
            double oldScore=double.NaN;bool guarded=false;string originalReason="no_existing_scored_edge";
            if(c.Algorithm==5)
            {
                var native=a6Evidence.Where(x=>A4ChargeIndex(x.Key.Incident)==ca&&A4ChargeIndex(x.Key.Reflection)==cb)
                    .OrderByDescending(x=>x.Value.Score).ThenBy(x=>x.Key.EdgeId).ToList();
                if(native.Count>0)
                {
                    var e=native[0].Value;
                    if(Finite(e.Score)&&e.Score>=0&&e.Score<=100)oldScore=e.Score/100;
                    guarded=e.A!=null&&e.B!=null&&e.A.Valid&&e.B.Valid&&e.Correlation!=null&&e.Correlation.Valid;
                    originalReason=String.Join(";",e.Reasons);
                }
            }
            else if(c.CandidatePool!=null)
            {
                var old=c.CandidatePool.Where(p=>A4ChargeIndex(p.Incident)==ca&&A4ChargeIndex(p.Reflection)==cb)
                    .OrderByDescending(p=>p.Confidence).FirstOrDefault();
                if(old!=null)
                {
                    if(Finite(old.Confidence)&&old.Confidence>=0&&old.Confidence<=1)oldScore=old.Confidence;
                    guarded=old.UnifiedCorrelation!=null&&old.UnifiedCorrelation.Available;
                    originalReason=old.ReviewReason;
                }
            }
            double va=A4RawSigned(c,pa),vb=A4RawSigned(c,pb);
            double snr=Math.Min(Math.Abs(va),Math.Abs(vb))/noise;
            double snrEvidenceFactor=Clamp((snr-3)/3,0,1);
            double proxy=.49*Clamp(rho,0,1)*snrEvidenceFactor;
            // Native engineering zero can mean missing guarded evidence, not
            // zero similarity. Keep it diagnostic-only for output candidates.
            double score=Math.Min(.49,proxy);
            var outputReview=c.Algorithm==5?A6ReviewOutput(c,incident,reflection,evidenceDelay,rho):null;
            // Method6's requested attenuation/width/weights must also govern
            // review-only candidates. High rho cannot conceal a curve mismatch.
            // Keep the actual candidate, but never promote it to accepted.
            if(outputReview!=null)score=Math.Min(.49,Math.Min(outputReview.Score*snrEvidenceFactor,outputReview.ScoreCeiling));
            A4AttenuationEvidence sharedAttenuation=null;
            if(c.Algorithm!=5&&c.Attenuation)
            {
                sharedAttenuation=A4CalibrationDeviationAtDelay(c,incident,reflection,evidenceDelay/c.Fs);
                double weighted=HVX.SignalEvidence.SixthConfidenceMath.WeightedScore(
                    new[]{Clamp(rho,0,1),sharedAttenuation.SoftConsistencyScore,snrEvidenceFactor},
                    new[]{.50,.35,.15},new[]{true,true,true});
                score=Math.Min(.49,Math.Min(weighted*snrEvidenceFactor,
                    HVX.SignalEvidence.SixthConfidenceMath.AttenuationCeiling(true,sharedAttenuation.Applied,
                        sharedAttenuation.BasePassed,sharedAttenuation.SoftConsistencyScore)));
            }
            var result=new A4BestCandidateReview {
                CanonicalIncident=ca,CanonicalReflection=cb,PeakIncident=pa,PeakReflection=pb,
                IncidentTiming=pa,ReflectionTiming=pb,Score=Math.Max(0,score),Rho=rho,
                SelectedPeakSnr=snr,SnrEvidenceFactor=snrEvidenceFactor,
                OriginalEngineeringScore=oldScore,GuardedEvidenceAvailable=guarded,LocalLobeOrderResolved=localOrder,
                SixthOutputReview=outputReview,
                SharedAttenuation=sharedAttenuation,
                IncidentSupportLobeCount=lobesA.Count,ReflectionSupportLobeCount=lobesB.Count,
                IncidentAnchorStartOrdinal=anchorA.SupportStartOrdinal,ReflectionAnchorStartOrdinal=anchorB.SupportStartOrdinal,
                IncidentAnchorFirst=anchorA.First.Peak,IncidentAnchorSecond=anchorA.Second==null?-1:anchorA.Second.Peak,
                ReflectionAnchorFirst=anchorB.First.Peak,ReflectionAnchorSecond=anchorB.Second==null?-1:anchorB.Second.Peak,
                IncidentBaseline=c.Raw[pa]-va,ReflectionBaseline=c.Raw[pb]-vb,
                IncidentValue=va,ReflectionValue=vb,AmplitudeRatio=outputReview!=null?outputReview.ObservedRatio:Math.Abs(vb)/Math.Abs(va),
                Reason="best_real_discovered_candidate_only;fixed_global_baseline_not_independent_guard_proof;"+
                    "independently_frozen_canonical_local_raw_structure;"+
                    "signed_fixed_full_support_correlation_primary;no_lobe_energy_ratio_gate;not_accepted;"+
                    (localOrder?"local_lobe_order_resolved;":"same_polarity_canonical_peaks_local_order_unresolved;")+originalReason+
                    (outputReview==null?"":";"+outputReview.Reason)};
            result.Pair=new A4Pair {Incident=incident,Reflection=reflection,OutputIncident=ca,OutputReflection=cb,
                State="review",Reviewed=true,Confidence=result.Score,Correlation=rho,WaveCorrelation=rho,
                AmplitudeRatio=result.AmplitudeRatio,Position=A4BoundaryOutputPosition(cb-ca,c.Fs,c.Joints,c.Velocities,c.Length),
                OutputTimingReason="candidate_only_same_order_same_polarity_raw_lobe_peaks",ReviewReason=result.Reason};
            results.Add(result);
        }
        if(results.Count==0)return null;
        var ranked=results.OrderByDescending(r=>c.Algorithm==5||c.Attenuation?r.Score:r.Rho).ThenByDescending(r=>c.Algorithm==5||c.Attenuation?r.Rho:r.Score)
            .ThenBy(r=>r.CanonicalReflection).ToList();
        var best=ranked[0];best.CandidateCount=ranked.Count;
        if(ranked.Count>1)
        {
            best.RunnerUpReflection=ranked[1].CanonicalReflection;best.RunnerUpRho=ranked[1].Rho;best.RunnerUpScore=ranked[1].Score;
            bool close=c.Algorithm==5?
                A4CandidateScoresClose(best.SixthOutputReview.Score,ranked[1].SixthOutputReview.Score,true):
                c.Attenuation?A4CandidateScoresClose(best.Score,best.RunnerUpScore,false):
                A4CandidateScoresClose(best.Rho,best.RunnerUpRho,false);
            if(close)
            {best.Score*=.5;best.Pair.Confidence=best.Score;best.Reason+=";multiple_close_candidates_score_halved";}
        }
        if(firstArrival)
        {
            int onsetA=A4RawLeadingEdge(c,incident,best.PeakIncident);
            int onsetB=A4RawLeadingEdge(c,best.Pair.Reflection,best.PeakReflection);
            if(onsetA>=displayStart&&onsetA<=best.PeakIncident&&onsetB>=0&&onsetB<=best.PeakReflection&&onsetB>onsetA)
            {best.IncidentTiming=onsetA;best.ReflectionTiming=onsetB;best.Reason+=";same_raw_lobe_anchored_leading_edges";}
            else best.Reason+=";leading_edge_unavailable_same_polarity_peaks_retained";
        }
        if(!A4ReflectionDelayWithinWindow(c,best.ReflectionTiming-best.IncidentTiming))return null;
        // Record baselines at the actual output samples, including first-arrival mode.
        best.IncidentBaseline=c.Raw[best.IncidentTiming]-A4RawSigned(c,best.IncidentTiming);
        best.ReflectionBaseline=c.Raw[best.ReflectionTiming]-A4RawSigned(c,best.ReflectionTiming);
        best.IncidentValue=A4RawSigned(c,best.IncidentTiming);best.ReflectionValue=A4RawSigned(c,best.ReflectionTiming);
        best.DiagnosticJson="{\"basis\":\"candidate_only_fixed_global_preprocessing_baseline_signed_10khz_3mhz_full_support_Pearson\","+
            "\"guard_quality_promoted\":false,\"existing_guarded_evidence_available\":"+(best.GuardedEvidenceAvailable?"true":"false")+
            ",\"raw_noise_sigma\":"+NumberJson(noise)+",\"canonical_raw_incident\":"+best.CanonicalIncident+
            ",\"canonical_raw_reflection\":"+best.CanonicalReflection+",\"raw_lobe_peaks\":["+best.PeakIncident+","+best.PeakReflection+
            "],\"local_lobe_order_resolved\":"+(best.LocalLobeOrderResolved?"true":"false")+
            ",\"same_order_lobe_ordinal_zero_based\":"+(best.LocalLobeOrderResolved?ordinal.ToString(Inv):"null")+
            ",\"anchor_basis\":\"canonical_and_temporally_nearest_adjacent_opposite_raw_lobe_within_two_packet_periods_frozen_before_rho\""+
            ",\"full_support_significant_lobe_counts\":["+best.IncidentSupportLobeCount+","+best.ReflectionSupportLobeCount+
            "],\"local_anchor_start_support_ordinals\":["+best.IncidentAnchorStartOrdinal+","+best.ReflectionAnchorStartOrdinal+
            "],\"incident_local_anchor_raw_peaks\":["+best.IncidentAnchorFirst+","+(best.IncidentAnchorSecond<0?"null":best.IncidentAnchorSecond.ToString(Inv))+
            "],\"reflection_local_anchor_raw_peaks\":["+best.ReflectionAnchorFirst+","+(best.ReflectionAnchorSecond<0?"null":best.ReflectionAnchorSecond.ToString(Inv))+
            "],\"incident_raw_baseline_at_lobe_peak\":"+NumberJson(best.IncidentBaseline)+
            ",\"reflection_raw_baseline_at_lobe_peak\":"+NumberJson(best.ReflectionBaseline)+",\"incident_signed_raw_lobe_value\":"+NumberJson(best.IncidentValue)+
            ",\"reflection_signed_raw_lobe_value\":"+NumberJson(best.ReflectionValue)+",\"signed_fixed_rho\":"+NumberJson(best.Rho)+
            ",\"score\":"+NumberJson(best.Score)+
            ",\"score_basis\":"+Json(best.SixthOutputReview!=null?
                "requested_weighted_evidence_times_snr_capped_by_attenuation_and_point49_with_close_score_runner_up_penalty_not_probability":
                best.SharedAttenuation!=null?
                "shared_raw_CAL_LOC_weighted_with_signed_rho_and_snr_capped_by_attenuation_and_point49_with_close_score_runner_up_penalty_not_probability":
                "0.49_times_clamped_signed_rho_times_snr_evidence_factor_with_separate_close_runner_up_penalty_not_probability")+
            ",\"final_requested_evidence\":"+A6OutputReviewJson(best.SixthOutputReview)+
            ",\"shared_raw_attenuation\":"+A4AttenuationJson(best.SharedAttenuation)+
            ",\"selected_peak_minimum_snr\":"+NumberJson(best.SelectedPeakSnr)+
            ",\"snr_evidence_factor\":"+NumberJson(best.SnrEvidenceFactor)+
            ",\"snr_evidence_factor_rule\":\"clamp((selected_peak_minimum_snr-3)/3,0,1)\""+
            ",\"native_engineering_score_diagnostic_only\":true,\"original_engineering_score\":"+NumberJson(best.OriginalEngineeringScore)+
            ",\"eligible_real_candidate_count\":"+best.CandidateCount+",\"runner_up_canonical_reflection\":"+
            (best.RunnerUpReflection<0?"null":best.RunnerUpReflection.ToString(Inv))+",\"runner_up_signed_rho\":"+NumberJson(best.RunnerUpRho)+
            ",\"runner_up_score\":"+NumberJson(best.RunnerUpScore)+",\"accepted\":false,\"reason\":"+Json(best.Reason)+"}";
        return best;
    }
}
