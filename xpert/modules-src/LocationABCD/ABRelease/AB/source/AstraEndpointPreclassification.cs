using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Experimental two-stage decisions, intentionally separate from the six
    // matchers' feature weights and acceptance. Only the output writer may
    // apply these proposals to matrix 13/33 and recalculate 16/17. Column 35,
    // packet ownership, raw timing, candidate scores and caches are read-only.
    private sealed class A4EndpointPreclassificationResult
    {
        internal bool Complete=true;
        internal bool AllIncidentInventoriesComplete=true;
        internal string Reason="complete";
        internal int Steps;
        internal readonly List<A4EndpointPredecision> Decisions=new List<A4EndpointPredecision>();
        internal string DiagnosticJson="{}";
    }
    private sealed class A4EndpointPredecision
    {
        internal A4Pair Source;
        internal int RawIncident,RawReflection,Delay,StrictEndpointSamples,BoundaryUncertaintySamples;
        internal double CanonicalPosition=double.NaN,OriginalPosition=double.NaN,OriginalScore=double.NaN;
        internal double NewPosition=double.NaN,NewScore=double.NaN,ScoreFactor=1;
        internal bool PositionProposed,PositionChanged,ScoreChanged,PhysicalPassed,PreliminaryEligible,InventoryComplete,RegionHigh;
        internal double RaiseToEndpointScore=.90;
        internal string GeometryRegion="region_uncertain",Region="region_uncertain",RegionTier="insufficient";
        internal string Direction="direction_unresolved",DirectionTier="insufficient",DirectionReason="not_an_endpoint";
        internal double NearDistance=double.NaN,FarDistance=double.NaN,LikenessMargin=double.NaN;
        internal double ShapeNoiseBound=double.NaN,CalibrationContrast=double.NaN;
        internal bool ShapeNoiseResolved;
        internal int CloseCount,CloseBodyCount,CloseEndpointCount,CloseUnknownCount;
        internal double BestCandidateScore=double.NaN,SecondCandidateScore=double.NaN,CandidateGap=double.NaN;
        internal readonly List<string> CandidateJson=new List<string>();
        internal readonly List<string> Reasons=new List<string>();
        internal A4PreShape IncidentShape,ReflectionShape,CalibrationIncidentShape,CalibrationReflectionShape;
    }
    private sealed class A4PreShape
    {
        internal bool Measurable;
        internal string Reason="measurement_unavailable";
        internal double Snr=double.NaN,MinimumSnr,MainWidthNs=double.NaN,FirstWidthNs=double.NaN,OppositeWidthNs=double.NaN;
        internal double OppositeWidthRatio=double.NaN,TemporalEnergyRatio=double.NaN,FirstEnergy=double.NaN,OppositeEnergy=double.NaN;
        internal double LogNoiseBound=double.NaN;
        internal int MainPeak=-1,FirstPeak=-1,OppositePeak=-1,FirstSign,OppositeSign,SignificantLobes;
        internal bool MainMultipleHalfHeightComponents;
    }
    private sealed class A4PreLobe
    {
        internal int Start,End,Peak,Sign;
        internal double Energy,Amplitude;
    }
    private sealed class A4PreBudget
    {
        internal int Steps;
        internal bool Complete=true;
        internal bool Take(int n=1)
        {
            if(n<0||Steps>A4MaximumRoleReviewSteps-n){Complete=false;return false;}
            Steps+=n;return true;
        }
    }
    private sealed class A4PreObservation
    {
        internal A4Pair Pair;
        internal A4AttenuationMeasurement A,B;
        internal A6Correlation Correlation;
        internal double Floor=double.NaN,ScorePercent=double.NaN;
        internal bool PhysicalPassed,PreliminaryEligible;
        internal string PhysicalReason="cached_evidence_unavailable";
    }

    private A4EndpointPreclassificationResult A4PreclassifyEndpoints(A4Context c,IList<A4Pair> currentPairs,
        IList<A4Pulse> pulses,IList<A4Pair> evaluatedPairs)
    {return A4PreclassifyEndpointsSafe(c,currentPairs,pulses,evaluatedPairs,false);}
    private A4EndpointPreclassificationResult A6PreclassifyEndpoints(A4Context c,IList<A4Pair> currentPairs,IList<A4Pulse> pulses)
    {return A4PreclassifyEndpointsSafe(c,currentPairs,pulses,a6Evidence.Keys.ToList(),true);}

    private A4EndpointPreclassificationResult A4PreclassifyEndpointsSafe(A4Context c,IList<A4Pair> current,
        IList<A4Pulse> pulses,IList<A4Pair> evaluated,bool sixth)
    {
        var r=new A4EndpointPreclassificationResult();
        try
        {
            if(c==null||c.Raw==null||current==null||pulses==null||evaluated==null||!Finite(c.Fs)||c.Fs<=0||
                !Finite(c.RoundTripExact)||c.RoundTripExact<=0||!Finite(c.Length)||c.Length<=0)
            {r.Complete=false;r.Reason="preclassification_context_unavailable";}
            else if(current.Count>A4MaximumCandidateEvaluations||evaluated.Count>A4MaximumCandidateEvaluations||pulses.Count>A4MaximumCandidateEvaluations)
            {r.Complete=false;r.Reason="preclassification_input_budget_incomplete";}
            else A4BuildPreclassification(c,current,pulses,evaluated,sixth,r);
            r.DiagnosticJson=A4PreclassificationJson(r,sixth);return r;
        }
        catch(Exception ex)
        {
            // A failed auxiliary pass cannot clear the existing public outputs
            // or leave partially applied proposals. The caller gets no changes.
            r=new A4EndpointPreclassificationResult {Complete=false,Reason="preclassification_unavailable:"+ex.GetType().Name};
            r.DiagnosticJson=A4PreclassificationJson(r,sixth);return r;
        }
    }

    private A4PreObservation A4PreObserve(A4Context c,A4Pair p,bool sixth)
    {
        var o=new A4PreObservation {Pair=p};
        if(p==null||p.Incident==null||p.Reflection==null)return o;
        bool otherPreliminaryGates=false;
        if(sixth)
        {
            A6Evidence e;
            if(!a6Evidence.TryGetValue(p,out e)||e==null)return o;
            o.A=e.A;o.B=e.B;o.Correlation=e.Correlation;o.Floor=e.CorrelationFloor;o.ScorePercent=e.Score;
            o.PhysicalPassed=e.Eligible;
            o.PhysicalReason=e.Eligible?"native_method6_enabled_physical_gates_passed":"native_method6_enabled_gate_failure:"+String.Join(";",e.Reasons);
            otherPreliminaryGates=e.Eligible||e.Reasons!=null&&e.Reasons.All(reason=>
                reason=="signed_correlation_below_effective_floor"||reason=="engineering_score_below_70");
        }
        else
        {
            var u=p.UnifiedCorrelation;
            if(u==null)return o;
            o.A=u.Incident;o.B=u.Reflection;o.Correlation=u.Measured;o.Floor=u.Floor;o.ScorePercent=100*p.Confidence;
            bool attenuation=!c.Attenuation||(p.CalibrationDeviation!=null&&
                p.CalibrationDeviation.Applied&&p.CalibrationDeviation.Passed);
            o.PhysicalPassed=u.Available&&u.Passed&&attenuation;
            o.PhysicalReason=o.PhysicalPassed?"existing_index4_correlation_and_enabled_CAL_gate_passed":
                !attenuation?"existing_index4_CAL_gate_failed_or_unavailable":"existing_index4_signed_correlation_gate_failed_or_unavailable";
            otherPreliminaryGates=u.Available&&attenuation;
        }
        var a=p.Incident;var b=p.Reflection;var rho=o.Correlation;
        bool geometry=A4ReflectionDelayWithinWindow(c,A4ChargeIndex(b)-A4ChargeIndex(a))&&A4BoundaryTimingValid(c,A4ChargeIndex(b)-A4ChargeIndex(a));
        bool supports=a.MappingValid&&b.MappingValid&&!a.Clipped&&!b.Clipped&&!a.UnresolvedOverlap&&!b.UnresolvedOverlap&&
            !a.BaselineUnstable&&!b.BaselineUnstable&&a.Start>=0&&b.End<c.Raw.Length&&a.End<b.Start&&
            !p.Blind&&!p.Amplified;
        bool measured=A4PreMeasurementAvailable(o.A,4.5)&&A4PreMeasurementAvailable(o.B,4.5);
        bool signed=rho!=null&&rho.Valid&&rho.StructureCompatible&&!rho.OppositeTemporalPolarity&&
            Finite(rho.PolarityRatio)&&rho.PolarityRatio>0&&Finite(rho.Fixed)&&Finite(o.Floor)&&rho.Fixed>=o.Floor;
        o.PreliminaryEligible=geometry&&otherPreliminaryGates&&supports&&measured&&rho!=null&&rho.Valid&&rho.StructureCompatible&&
            !rho.OppositeTemporalPolarity&&Finite(rho.PolarityRatio)&&rho.PolarityRatio>0&&Finite(rho.Fixed)&&rho.Fixed>0&&
            Finite(o.Floor)&&rho.Fixed+A4LocalCorrelationShadowMargin+1e-12>=o.Floor;
        if(!geometry||!supports||!measured||!signed)
        {
            o.PhysicalPassed=false;
            o.PhysicalReason=!geometry?"geometry_outside_2percent_or_unavailable":!supports?"raw_packet_support_or_structure_not_reliable":!measured?
                "full_guarded_fixed_band_packet_below_4point5_sigma_or_unavailable":"signed_temporal_correlation_not_physical_gate_quality";
        }
        return o;
    }
    private static bool A4PreMeasurementAvailable(A4AttenuationMeasurement m,double minimumSnr)
    {
        return m!=null&&m.Valid&&m.SignalSamples!=null&&Finite(m.Amplitude)&&m.Amplitude>0&&
            Finite(m.Baseline)&&Finite(m.Noise)&&m.Noise>=0&&Finite(m.Snr)&&m.Snr>=minimumSnr&&
            m.Start>=m.SignalOrigin&&m.End>m.Start&&m.Peak>=m.Start&&m.Peak<=m.End&&m.End-m.SignalOrigin<m.SignalSamples.Length;
    }
    private static A4PreShape A4PreMeasureShape(A4AttenuationMeasurement m,double fs,double minimumSnr,A4PreBudget budget)
    {
        var s=new A4PreShape {MinimumSnr=minimumSnr,Snr=m==null?double.NaN:m.Snr};
        if(!A4PreMeasurementAvailable(m,minimumSnr)||!Finite(fs)||fs<=0)
        {s.Reason="guarded_fixed_band_support_or_required_snr_unavailable";return s;}
        int lo=m.Start-m.SignalOrigin,hi=m.End-m.SignalOrigin,n=hi-lo+1;
        if(n>A4MaximumRoleReviewSteps/6||!budget.Take(6*n))
        {s.Reason="shape_scan_budget_incomplete";return s;}
        var lobes=new List<A4PreLobe>();int start=lo,sign=0,peak=lo,main=lo;
        double energy=0,amplitude=0,mainAmplitude=0;
        for(int i=lo;i<=hi+1;i++)
        {
            double v=i<=hi?m.SignalSamples[i]-m.Baseline:0;
            if(!Finite(v)){s.Reason="nonfinite_fixed_band_packet";return s;}
            int next=i>hi?0:v>0?1:v<0?-1:0;
            if(next!=sign)
            {
                int count=i-start;
                if(sign!=0&&amplitude>=4.5*m.Noise&&energy>m.Noise*m.Noise*(count+4.5*Math.Sqrt(2.0*count)))
                    lobes.Add(new A4PreLobe {Start=start,End=i-1,Peak=peak,Sign=sign,Energy=energy/fs,Amplitude=amplitude});
                start=i;sign=next;peak=i;energy=0;amplitude=0;
            }
            if(i<=hi)
            {
                energy+=v*v;
                if(Math.Abs(v)>amplitude){amplitude=Math.Abs(v);peak=i;}
                if(Math.Abs(v)>mainAmplitude){mainAmplitude=Math.Abs(v);main=i;}
            }
        }
        s.MainPeak=main+m.SignalOrigin;s.SignificantLobes=lobes.Count;
        var w=AstraPulseWidthDiagnostic.Measure(m.SignalSamples,lo,hi,main,m.Baseline,m.Noise,fs);
        s.MainMultipleHalfHeightComponents=w.MultiPeak;
        // LowSnr in the older width helper is a 5-sigma flag. Here a bounded
        // component width is still measurable at the explicitly requested 4.5;
        // report it as a weak experimental feature, never as accepted evidence.
        if(!w.Clipped&&Finite(w.ComponentWidthNs)&&w.ComponentWidthNs>0)s.MainWidthNs=w.ComponentWidthNs;
        if(lobes.Count<2){s.Reason="chronological_opposite_lobe_pair_unavailable";return s;}
        var first=lobes[0];var opposite=lobes.Skip(1).FirstOrDefault(q=>q.Sign!=first.Sign);
        if(opposite==null){s.Reason="chronological_opposite_lobe_pair_unavailable";return s;}
        var wf=AstraPulseWidthDiagnostic.Measure(m.SignalSamples,first.Start,first.End,first.Peak,m.Baseline,m.Noise,fs);
        var wo=AstraPulseWidthDiagnostic.Measure(m.SignalSamples,opposite.Start,opposite.End,opposite.Peak,m.Baseline,m.Noise,fs);
        s.FirstPeak=first.Peak+m.SignalOrigin;s.OppositePeak=opposite.Peak+m.SignalOrigin;
        s.FirstSign=first.Sign;s.OppositeSign=opposite.Sign;
        s.FirstEnergy=first.Energy;s.OppositeEnergy=opposite.Energy;
        if(!wf.Clipped&&Finite(wf.ComponentWidthNs)&&wf.ComponentWidthNs>0)s.FirstWidthNs=wf.ComponentWidthNs;
        if(!wo.Clipped&&Finite(wo.ComponentWidthNs)&&wo.ComponentWidthNs>0)s.OppositeWidthNs=wo.ComponentWidthNs;
        if(Finite(s.FirstWidthNs)&&Finite(s.OppositeWidthNs))s.OppositeWidthRatio=s.FirstWidthNs/s.OppositeWidthNs;
        if(first.Energy>0&&opposite.Energy>0)s.TemporalEnergyRatio=first.Energy/opposite.Energy;
        s.Measurable=new[]{s.MainWidthNs,s.OppositeWidthRatio,s.TemporalEnergyRatio}.All(x=>Finite(x)&&x>0);
        // A log-domain engineering noise scale accompanies a weak difference.
        // It is not a confidence interval or an endpoint probability.
        s.LogNoiseBound=2*m.Noise/Math.Max(1e-30,mainAmplitude)+
            2*m.Noise/Math.Max(1e-30,first.Amplitude)+2*m.Noise/Math.Max(1e-30,opposite.Amplitude);
        s.Reason=s.Measurable?"all_three_same_band_shape_features_measurable_not_endpoint_proof":"one_or_more_shape_features_unavailable";
        return s;
    }
    private static A4PreShape A4PreShapeCached(A4AttenuationMeasurement m,double fs,double snr,A4PreBudget budget,
        Dictionary<Tuple<A4AttenuationMeasurement,double,double>,A4PreShape> cache)
    {
        var key=Tuple.Create(m,fs,snr);A4PreShape value;
        if(!cache.TryGetValue(key,out value)){value=A4PreMeasureShape(m,fs,snr,budget);cache.Add(key,value);}return value;
    }
    private static string A4PreGeometryRegion(A4Context c,A4Pulse a,A4Pulse b,out int strict,out int uncertain,out double position)
    {
        strict=uncertain=0;position=double.NaN;
        if(a==null||b==null)return "region_uncertain";
        int delay=A4ChargeIndex(b)-A4ChargeIndex(a);
        strict=Math.Min(A4EchoTolerance(a),A4EchoTolerance(b));
        uncertain=Math.Max(strict,(int)Math.Ceiling(Math.Min(c.RoundTripExact/8.0,Math.Max(a.Period,b.Period)/2.0)));
        position=A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length);
        if(delay<=0||!Finite(position))return "region_uncertain";
        if(delay<=strict||Math.Abs(delay-c.RoundTripExact)<=strict)return "endpoint";
        if(position<0||position>c.Length||delay<=uncertain||Math.Abs(delay-c.RoundTripExact)<=uncertain)return "boundary_uncertain";
        return "body";
    }
    private static string A4PreCandidateRegion(A4Context c,A4Pulse incident,A4LocalCandidate candidate)
    {
        // Shadow descriptors retain raw support/identity, but not a separately
        // estimated reflection period. Missing that period is not filled with a
        // convenient value: only a strict endpoint or clearly interior body is
        // asserted; the remaining boundary band stays unknown.
        int delay=candidate.ReflectionPeak-A4ChargeIndex(incident);
        if(delay<=0)return "region_uncertain";
        int strict=Math.Min(A4EchoTolerance(incident),2);
        int uncertain=Math.Max(A4EchoTolerance(incident),(int)Math.Ceiling(Math.Min(c.RoundTripExact/8.0,incident.Period/2.0)));
        double position=A4BoundaryOutputPosition(delay,c.Fs,c.Joints,c.Velocities,c.Length);
        if(!Finite(position))return "region_uncertain";
        if(delay<=strict||Math.Abs(delay-c.RoundTripExact)<=strict)return "endpoint";
        if(position<0||position>c.Length||delay<=uncertain||Math.Abs(delay-c.RoundTripExact)<=uncertain)return "boundary_uncertain";
        return "body";
    }

    private static A4LocalCandidate[] A4PreDistinctCandidates(List<A4LocalCandidate> source,A4PreBudget budget)
    {
        if(!budget.Take(source.Count))return new A4LocalCandidate[0];
        var kept=new List<A4LocalCandidate>();
        foreach(var q in source.Where(x=>x!=null&&Finite(x.ScorePercent)&&x.ScorePercent>=A4LocalHighScorePercent)
            .OrderByDescending(x=>x.ScorePercent).ThenBy(x=>x.Shadow).ThenBy(x=>x.ReflectionPeak).ThenBy(x=>x.EdgeId))
        {
            bool duplicate=false;
            foreach(var old in kept)
            {
                if(!budget.Take())return kept.ToArray();
                if(old.ReflectionId==q.ReflectionId||old.ReflectionStart<=q.ReflectionEnd&&q.ReflectionStart<=old.ReflectionEnd)
                {duplicate=true;break;}
            }
            if(!duplicate)kept.Add(q);
        }
        return kept.ToArray();
    }
    private static void A4PreAddDescriptor(Dictionary<int,List<A4LocalCandidate>> map,A4LocalCandidate candidate)
    {
        if(candidate==null)return;
        List<A4LocalCandidate> list;
        if(!map.TryGetValue(candidate.IncidentId,out list))map[candidate.IncidentId]=list=new List<A4LocalCandidate>();
        list.Add(candidate);
    }

    private void A4BuildPreclassification(A4Context c,IList<A4Pair> current,IList<A4Pulse> pulses,
        IList<A4Pair> evaluated,bool sixth,A4EndpointPreclassificationResult result)
    {
        var budget=new A4PreBudget();
        var shapeCache=new Dictionary<Tuple<A4AttenuationMeasurement,double,double>,A4PreShape>();
        var observations=new Dictionary<A4Pair,A4PreObservation>();
        var observationByRawSupport=new Dictionary<Tuple<int,int,int,int>,A4PreObservation>();
        var candidateByIncident=new Dictionary<int,List<A4LocalCandidate>>();
        var omittedIncidents=new HashSet<int>();
        foreach(var p in evaluated.Concat(current).Where(p=>p!=null&&p.Incident!=null&&p.Reflection!=null).Distinct())
        {
            if(!budget.Take())break;
            var o=A4PreObserve(c,p,sixth);observations[p]=o;
            observationByRawSupport[Tuple.Create(p.Incident.Id,A4ChargeIndex(p.Reflection),p.Reflection.Start,p.Reflection.End)]=o;
            if(o.PhysicalPassed&&Finite(o.ScorePercent)&&o.ScorePercent>=70)
                A4PreAddDescriptor(candidateByIncident,A4LocalDescriptor(p,o.ScorePercent,false,"already_evaluated_complete_physical_candidate"));
        }
        if(sixth)
        {
            foreach(var entry in a6LocalAmbiguities)
            {
                if(!budget.Take())break;
                var prior=entry.Value;
                if(prior==null||prior.Ranked==null){omittedIncidents.Add(entry.Key);continue;}
                if(prior.HighCandidateCount>prior.Ranked.Length)omittedIncidents.Add(entry.Key);
                foreach(var q in prior.Ranked){if(!budget.Take())break;A4PreAddDescriptor(candidateByIncident,q);}
            }
        }
        else
        {
            // Consume the existing pool/shadows without invoking an unbudgeted
            // second all-pairs ambiguity pass. These descriptors never enter
            // the matcher, and retained top-two shadows cannot imply absence
            // of a third interior competitor.
            foreach(var p in c.CandidatePool)
            {
                if(!budget.Take())break;
                if(p!=null&&p.Incident!=null&&p.Reflection!=null&&Finite(p.Confidence)&&!p.Blind&&!p.Amplified)
                    A4PreAddDescriptor(candidateByIncident,A4LocalDescriptor(p,100*p.Confidence,false,"legacy_complete_physical_candidate_pool"));
            }
            A4LocalShadowState shadows;
            if(c.RejectedPairCount>0&&a4LocalShadows.TryGetValue(c,out shadows))
            {
                foreach(var entry in shadows.ByIncident)
                {
                    if(!budget.Take())break;
                    int qualified;shadows.QualifiedByIncident.TryGetValue(entry.Key,out qualified);
                    if(entry.Value==null){omittedIncidents.Add(entry.Key);continue;}
                    if(qualified>entry.Value.Count)omittedIncidents.Add(entry.Key);
                    foreach(var q in entry.Value){if(!budget.Take())break;A4PreAddDescriptor(candidateByIncident,q);}
                }
            }
        }
        // Parallel role alternatives are not failed waveforms. Their existing
        // edge evidence has no score, however, so an alternative absent from
        // the fully scored pool makes this incident's region inventory partial.
        if(c.ParallelAlternatives!=null)
        foreach(var roles in c.ParallelAlternatives)
        {
            if(!budget.Take())break;
            foreach(var edge in new[]{roles.ShortFirst,roles.ShortSecond,roles.ParallelFirst,roles.ParallelSecond})
            {
                if(!budget.Take())break;
                if(edge==null||edge.A==null||edge.B==null)continue;
                if(!observationByRawSupport.ContainsKey(Tuple.Create(edge.A.Id,A4ChargeIndex(edge.B),edge.B.Start,edge.B.End)))
                    omittedIncidents.Add(edge.A.Id);
            }
        }
        bool globalComplete=!A4RecordReviewIncomplete(c)&&(!sixth||a6IncompleteComponents==0)&&budget.Complete;
        var calibration=sixth?c.AttenuationScoringReference:c.UnifiedCorrelation==null?null:c.UnifiedCorrelation.CalibrationMeasurement;
        var calRho=sixth?a6CalibrationCorrelation:c.UnifiedCorrelation==null?null:c.UnifiedCorrelation.CalibrationCorrelation;
        double calReference,calFloor,calMargin;
        bool comparableCalShape=calRho!=null&&calRho.Valid&&calRho.StructureCompatible&&!calRho.OppositeTemporalPolarity&&
            Finite(calRho.PolarityRatio)&&calRho.PolarityRatio>0&&Finite(calRho.Fixed)&&calRho.Fixed>=A6Settings.MinimumUsableCalibrationCorrelation&&
            A4UnifiedCorrelationReferenceMath(calRho.Fixed,calRho.Lower,1,out calReference,out calFloor,out calMargin);
        bool validCal=c.Attenuation&&comparableCalShape&&calibration!=null&&calibration.Valid&&c.Calibration!=null&&Finite(c.Calibration.SampleRate)&&c.Calibration.SampleRate>0&&
            Finite(calibration.TravelSeconds)&&calibration.TravelSeconds>0;
        double calFs=validCal?c.Calibration.SampleRate:0;
        var calA=A4PreShapeCached(validCal?calibration.Incident:null,calFs,6,budget,shapeCache);
        var calB=A4PreShapeCached(validCal?calibration.Reflection:null,calFs,6,budget,shapeCache);
        foreach(var p in current)
        {
            if(!budget.Take())break;
            if(p==null||p.Incident==null||p.Reflection==null)continue;
            var d=new A4EndpointPredecision {Source=p,RawIncident=A4ChargeIndex(p.Incident),RawReflection=A4ChargeIndex(p.Reflection),
                OriginalPosition=p.Position,OriginalScore=p.Confidence,NewPosition=p.Position,NewScore=p.Confidence};
            d.Delay=d.RawReflection-d.RawIncident;
            d.GeometryRegion=A4PreGeometryRegion(c,p.Incident,p.Reflection,out d.StrictEndpointSamples,out d.BoundaryUncertaintySamples,out d.CanonicalPosition);
            d.Region=d.GeometryRegion;
            A4PreObservation o;
            if(!observations.TryGetValue(p,out o))o=A4PreObserve(c,p,sixth);
            d.PhysicalPassed=o.PhysicalPassed;
            d.PreliminaryEligible=o.PreliminaryEligible;
            d.IncidentShape=A4PreShapeCached(o.A,c.Fs,4.5,budget,shapeCache);
            d.ReflectionShape=A4PreShapeCached(o.B,c.Fs,4.5,budget,shapeCache);
            d.CalibrationIncidentShape=calA;d.CalibrationReflectionShape=calB;
            List<A4LocalCandidate> descriptors;
            if(!candidateByIncident.TryGetValue(p.Incident.Id,out descriptors))descriptors=new List<A4LocalCandidate>();
            bool omitted=omittedIncidents.Contains(p.Incident.Id)||p.BoundaryAmbiguous;
            var distinct=A4PreDistinctCandidates(descriptors,budget);
            d.InventoryComplete=globalComplete&&!omitted&&budget.Complete;
            if(distinct.Length>0)d.BestCandidateScore=distinct[0].ScorePercent;
            if(distinct.Length>1){d.SecondCandidateScore=distinct[1].ScorePercent;d.CandidateGap=d.BestCandidateScore-d.SecondCandidateScore;}
            var close=distinct.Where(q=>q.ScorePercent>=d.BestCandidateScore-A4LocalScoreGapPercent-1e-9).ToArray();
            foreach(var q in close)
            {
                if(!budget.Take())break;
                A4PreObservation matched;
                observationByRawSupport.TryGetValue(Tuple.Create(p.Incident.Id,q.ReflectionPeak,q.ReflectionStart,q.ReflectionEnd),out matched);
                int s,t;double x;
                string region=matched!=null?A4PreGeometryRegion(c,p.Incident,matched.Pair.Reflection,out s,out t,out x):A4PreCandidateRegion(c,p.Incident,q);
                if(region=="endpoint")d.CloseEndpointCount++;else if(region=="body")d.CloseBodyCount++;else d.CloseUnknownCount++;
                d.CandidateJson.Add("{\"reflection_raw_peak\":"+q.ReflectionPeak+",\"raw_support\":["+q.ReflectionStart+","+q.ReflectionEnd+
                    "],\"existing_score_percent\":"+NumberJson(q.ScorePercent)+",\"region\":"+Json(region)+",\"shadow\":"+(q.Shadow?"true":"false")+"}");
            }
            d.CloseCount=close.Length;
            bool sourceClose=close.Any(q=>q.ReflectionPeak==d.RawReflection||q.ReflectionId==p.Reflection.Id||
                q.ReflectionStart<=p.Reflection.End&&p.Reflection.Start<=q.ReflectionEnd);
            if(!d.InventoryComplete)d.Reasons.Add(omitted?"some_shadow_or_unscored_role_locations_not_retained_cannot_exclude_body_alternative":"candidate_or_matching_budget_incomplete");
            bool mixed=d.CloseBodyCount>0&&d.CloseEndpointCount>0||
                d.GeometryRegion=="endpoint"&&d.CloseBodyCount>0||d.GeometryRegion=="body"&&d.CloseEndpointCount>0;
            if(mixed||d.CloseUnknownCount>0){d.Region="region_uncertain";d.Reasons.Add("close_score_candidates_span_body_endpoint_or_unknown_boundary");}
            d.RegionHigh=d.GeometryRegion=="endpoint"&&d.Region=="endpoint"&&d.InventoryComplete&&d.PhysicalPassed&&
                sourceClose&&Finite(p.Confidence)&&p.Confidence>=.70&&
                d.CloseBodyCount==0&&d.CloseUnknownCount==0;
            d.RegionTier=d.RegionHigh?"high_engineering_region_support":d.InventoryComplete&&d.PhysicalPassed?
                "preliminary_region_support":"insufficient_for_high_region_support";
            if(!d.PhysicalPassed)d.Reasons.Add(o.PhysicalReason);
            if(d.GeometryRegion=="endpoint")
            {
                var rawDecision=HVX.SignalEvidence.RawEndpointMath.Decide(A4RawEndpointShape(c.Raw,o.A,c.Fs),
                    A4RawEndpointShape(c.Raw,o.B,c.Fs),A4RawEndpointShape(c.Calibration==null?null:c.Calibration.Samples,
                    calibration==null?null:calibration.Incident,calFs),ManufacturerEndpointPolicy);
                d.Direction=rawDecision.Direction=="Far"?"far_tendency":rawDecision.Direction=="Near"?"near_tendency":"direction_unresolved";
                d.DirectionReason=HVX.SignalEvidence.RawEndpointMath.Version+";"+rawDecision.Reason;
                d.DirectionTier="raw_only_engineering_policy";
                // A physically small raw delay already corresponds to L under
                // the existing travel-time map. Shape must never flip it to 0.
                if(d.Delay<=d.StrictEndpointSamples)
                {d.Direction="far_tendency";d.DirectionTier="geometry_near_L_not_morphology_reversal";d.DirectionReason="short_canonical_delay_preserves_far_end_geometry";}
                bool measurable=rawDecision.Direction!="Indeterminate";
                if(d.Region=="endpoint"&&d.InventoryComplete&&d.PreliminaryEligible&&measurable&&
                    (d.Direction=="near_tendency"||d.Direction=="far_tendency"))
                {
                    d.PositionProposed=true;
                    d.NewPosition=d.Direction=="near_tendency"?0:c.Length;
                    d.PositionChanged=Finite(d.NewPosition)&&(!Finite(d.OriginalPosition)||d.NewPosition!=d.OriginalPosition);
                    d.Reasons.Add("experimental_not_confirmed_endpoint_position_proposal_acceptance_preserved");
                }
            }
            else d.DirectionReason=d.GeometryRegion=="body"?"direction_preclassification_not_applied_to_body":"outside_strict_endpoint_region_no_endpoint_snap";
            if(d.RegionHigh)
            {d.NewScore=Math.Max(d.OriginalScore,.90);d.Reasons.Add("region_score_floor_point90_does_not_certify_direction");}
            else if(d.CloseCount>=2&&(d.GeometryRegion=="body"||d.CloseBodyCount>0))
            {
                d.ScoreFactor=Math.Max(.5,1-.15*(d.CloseCount-1));
                if(Finite(d.OriginalScore))d.NewScore=d.OriginalScore*d.ScoreFactor;
                d.Reasons.Add("body_or_cross_region_close_score_ambiguity_auxiliary_factor");
            }
            if(!d.InventoryComplete)
            {
                // Incomplete evidence never applies a positive or negative
                // numerical claim about unseen alternatives.
                d.NewPosition=d.OriginalPosition;d.NewScore=d.OriginalScore;d.PositionProposed=d.PositionChanged=false;d.RegionHigh=false;d.ScoreFactor=1;
                d.RegionTier="insufficient_for_high_region_support";
            }
            d.ScoreChanged=Finite(d.NewScore)&&(!Finite(d.OriginalScore)||d.NewScore!=d.OriginalScore);
            result.Decisions.Add(d);
        }
        result.Steps=budget.Steps;result.Complete=budget.Complete&&globalComplete;
        result.AllIncidentInventoriesComplete=result.Decisions.All(d=>d.InventoryComplete);
        if(!budget.Complete)
        {
            result.Reason="preclassification_budget_incomplete_no_proposals_applied";
            foreach(var d in result.Decisions){d.NewPosition=d.OriginalPosition;d.NewScore=d.OriginalScore;d.PositionProposed=d.PositionChanged=d.ScoreChanged=d.RegionHigh=false;d.InventoryComplete=false;d.ScoreFactor=1;d.RegionTier="insufficient_for_high_region_support";}
            result.AllIncidentInventoriesComplete=false;
        }
        else if(!globalComplete)result.Reason="original_candidate_inventory_incomplete_no_proposals_applied";
        else if(result.Decisions.Any(d=>!d.InventoryComplete))result.Reason="some_incidents_have_omitted_shadow_locations_no_proposals_for_those_rows";
    }
    private static HVX.SignalEvidence.RawEndpointShape A4RawEndpointShape(double[] raw,A4AttenuationMeasurement m,double fs)
    {
        if(raw==null||m==null||!m.Valid||m.RawStart<0||m.RawEnd>=raw.Length||m.RawEnd<=m.RawStart)
            return new HVX.SignalEvidence.RawEndpointShape();
        var residual=new double[m.RawEnd-m.RawStart+1];
        for(int i=0;i<residual.Length;i++)residual[i]=raw[m.RawStart+i]-A4RawMeasurementBaseline(m,m.RawStart+i);
        return HVX.SignalEvidence.RawEndpointMath.Measure(residual,fs,m.RawNoise);
    }

    private static string A4PreShapeJson(A4PreShape s)
    {
        if(s==null)return "null";
        return "{\"measurable\":"+(s.Measurable?"true":"false")+",\"reason\":"+Json(s.Reason)+",\"snr\":"+NumberJson(s.Snr)+
            ",\"minimum_snr\":"+NumberJson(s.MinimumSnr)+",\"main_fwhm_ns\":"+NumberJson(s.MainWidthNs)+
            ",\"first_fwhm_ns\":"+NumberJson(s.FirstWidthNs)+",\"following_opposite_fwhm_ns\":"+NumberJson(s.OppositeWidthNs)+
            ",\"first_to_opposite_width_ratio\":"+NumberJson(s.OppositeWidthRatio)+",\"first_to_opposite_energy_ratio\":"+NumberJson(s.TemporalEnergyRatio)+
            ",\"first_energy_input_units_squared_seconds\":"+NumberJson(s.FirstEnergy)+",\"opposite_energy_input_units_squared_seconds\":"+NumberJson(s.OppositeEnergy)+
            ",\"main_peak\":"+s.MainPeak+",\"first_peak\":"+s.FirstPeak+",\"opposite_peak\":"+s.OppositePeak+
            ",\"first_sign\":"+s.FirstSign+",\"opposite_sign\":"+s.OppositeSign+",\"significant_lobe_count\":"+s.SignificantLobes+
            ",\"main_multiple_halfheight_components\":"+(s.MainMultipleHalfHeightComponents?"true":"false")+",\"log_noise_engineering_bound\":"+NumberJson(s.LogNoiseBound)+"}";
    }
    private static string A4PredecisionJson(A4EndpointPredecision decision)
    {
        if(decision==null)return "null";
        var single=new A4EndpointPreclassificationResult();single.Decisions.Add(decision);
        string document=A4PreclassificationJson(single,false);
        const string prefix="\"decisions\":[";
        int start=document.IndexOf(prefix,StringComparison.Ordinal)+prefix.Length;
        return document.Substring(start,document.Length-start-2);
    }
    private static string A4PreclassificationJson(A4EndpointPreclassificationResult r,bool sixth)
    {
        var b=new StringBuilder("{\"experimental_not_confirmed\":true,\"certified\":false,\"acceptance_changed\":false,\"proposal_only_until_output_writer\":true,\"method6_native_cache\":")
            .Append(sixth?"true":"false").Append(",\"complete\":").Append(r.Complete?"true":"false").Append(",\"reason\":").Append(Json(r.Reason))
            .Append(",\"all_incident_inventories_complete\":").Append(r.AllIncidentInventoriesComplete?"true":"false")
            .Append(",\"steps\":").Append(r.Steps).Append(",\"primary_match_weights_changed\":false,\"region_score_is_not_direction_confidence\":true,")
            .Append("\"band_hz\":[10000,3000000],\"event_minimum_snr\":4.5,\"CAL_minimum_snr\":6,\"shape_distance\":\"mean_absolute_log_ratio_three_features_equal_auxiliary_weights\",")
            .Append("\"shape_features\":[\"main_lobe_FWHM_ns\",\"chronological_first_to_following_opposite_lobe_FWHM_ratio\",\"chronological_lobe_energy_ratio\"],")
            .Append("\"energy_units_policy\":\"only_within_packet_lobe_energy_ratio_is_compared; no_absolute_CAL_to_LOC_energy_comparison_across_input_units\",")
            .Append("\"high_candidate_score_percent\":70,\"close_gap_percentage_points\":5,\"body_ambiguity_factor\":\"max(0.5,1-0.15*(close_distinct_count-1))\",")
            .Append("\"endpoint_high_score_floor\":0.90,\"endpoint_scope\":\"canonical_delay<=min(existing_EchoTolerance) or abs(canonical_delay-RT)<=that_tolerance; tolerance2..10samples\",")
            .Append("\"boundary_uncertainty_scope\":\"max(strict,min(RT/8,max(packet_period)/2)); diagnostic_only_no_endpoint_snap\",")
            .Append("\"matrix_contract\":\"output_writer_may_apply_proposals_to13and33_then_recompute16and17; never_change35_or_raw_timing; join_raw_incident_and_raw_reflection\",\"decisions\":[");
        bool first=true;
        foreach(var d in r.Decisions)
        {
            if(!first)b.Append(',');first=false;
            b.Append("{\"raw_incident\":").Append(d.RawIncident).Append(",\"raw_reflection\":").Append(d.RawReflection)
                .Append(",\"canonical_delay_samples\":").Append(d.Delay).Append(",\"canonical_position_m\":").Append(NumberJson(d.CanonicalPosition))
                .Append(",\"source_pair_original_position_m\":").Append(NumberJson(d.OriginalPosition)).Append(",\"proposed_position_m\":").Append(NumberJson(d.NewPosition))
                .Append(",\"source_pair_original_score\":").Append(NumberJson(d.OriginalScore)).Append(",\"source_pair_proposed_score\":").Append(NumberJson(d.NewScore))
                .Append(",\"position_proposed\":").Append(d.PositionProposed?"true":"false").Append(",\"position_changed\":").Append(d.PositionChanged?"true":"false").Append(",\"score_changed\":").Append(d.ScoreChanged?"true":"false")
                .Append(",\"score_factor\":").Append(NumberJson(d.ScoreFactor)).Append(",\"geometry_region\":").Append(Json(d.GeometryRegion))
                .Append(",\"region\":").Append(Json(d.Region)).Append(",\"region_tier\":").Append(Json(d.RegionTier)).Append(",\"region_high\":").Append(d.RegionHigh?"true":"false")
                .Append(",\"strict_endpoint_samples\":").Append(d.StrictEndpointSamples).Append(",\"boundary_uncertainty_samples\":").Append(d.BoundaryUncertaintySamples)
                .Append(",\"physical_gates_passed\":").Append(d.PhysicalPassed?"true":"false").Append(",\"preliminary_shape_eligible\":").Append(d.PreliminaryEligible?"true":"false")
                .Append(",\"inventory_complete\":").Append(d.InventoryComplete?"true":"false")
                .Append(",\"raise_to_endpoint_score\":").Append(NumberJson(d.RaiseToEndpointScore))
                .Append(",\"output_score_operation\":").Append(Json(d.RegionHigh?"max(existing_output_score,raise_to_endpoint_score)":
                    d.ScoreFactor<1?"existing_output_score*score_factor":"keep_existing_output_score"))
                .Append(",\"original_values_are_source_pair_not_display_row\":true")
                .Append(",\"direction\":").Append(Json(d.Direction)).Append(",\"direction_tier\":").Append(Json(d.DirectionTier)).Append(",\"direction_reason\":").Append(Json(d.DirectionReason))
                .Append(",\"near_profile_log_distance\":").Append(NumberJson(d.NearDistance)).Append(",\"far_profile_log_distance\":").Append(NumberJson(d.FarDistance))
                .Append(",\"signed_near_likeness_margin\":").Append(NumberJson(d.LikenessMargin)).Append(",\"shape_noise_bound\":").Append(NumberJson(d.ShapeNoiseBound))
                .Append(",\"shape_noise_resolved\":").Append(d.ShapeNoiseResolved?"true":"false").Append(",\"CAL_profile_contrast\":").Append(NumberJson(d.CalibrationContrast))
                .Append(",\"close_count\":").Append(d.CloseCount).Append(",\"close_body_count\":").Append(d.CloseBodyCount).Append(",\"close_endpoint_count\":").Append(d.CloseEndpointCount)
                .Append(",\"close_unknown_count\":").Append(d.CloseUnknownCount).Append(",\"best_existing_candidate_score_percent\":").Append(NumberJson(d.BestCandidateScore))
                .Append(",\"second_existing_candidate_score_percent\":").Append(NumberJson(d.SecondCandidateScore)).Append(",\"candidate_gap_percentage_points\":").Append(NumberJson(d.CandidateGap))
                .Append(",\"close_candidates\":[").Append(String.Join(",",d.CandidateJson)).Append("],\"incident_shape\":").Append(A4PreShapeJson(d.IncidentShape))
                .Append(",\"reflection_shape\":").Append(A4PreShapeJson(d.ReflectionShape)).Append(",\"CAL_incident_shape\":").Append(A4PreShapeJson(d.CalibrationIncidentShape))
                .Append(",\"CAL_reflection_shape\":").Append(A4PreShapeJson(d.CalibrationReflectionShape)).Append(",\"reasons\":[").Append(String.Join(",",d.Reasons.Select(Json))).Append("]}");
        }
        return b.Append("]}").ToString();
    }
}
