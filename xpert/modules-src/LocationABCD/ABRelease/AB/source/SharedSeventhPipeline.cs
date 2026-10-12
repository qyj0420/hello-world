using System;
using System.Collections.Generic;

// One pure engineering-evidence contract, compiled unchanged into Task17 and
// the review control. Neither a display filter nor a probability model.
namespace HVX.SignalEvidence
{
    internal sealed class SeventhPipelineInput
    {
        internal bool IndependentPackets,SamePolarity,GeometryValid,ReflectionWindowValid,PhysicalCoverageValid,CalibrationSamePolarity=true;
        internal bool CalibrationIdentified,CalibrationIndependentCompletePackets,CalibrationUnclipped;
        // Retained request flags are diagnostic only: method 7 always uses both.
        internal bool UseAttenuation=true,UseWidth=true;
        internal double CalibrationMainIncident=double.NaN,CalibrationMainReflection=double.NaN;
        internal double CalibrationOppositeIncident=double.NaN,CalibrationOppositeReflection=double.NaN;
        internal double CalibrationIncidentNoise=double.NaN,CalibrationReflectionNoise=double.NaN,CalibrationTravelSeconds=double.NaN;
        internal double IncidentMain=double.NaN,ReflectionMain=double.NaN,IncidentOpposite=double.NaN,ReflectionOpposite=double.NaN;
        internal double IncidentNoise=double.NaN,ReflectionNoise=double.NaN;
        internal double IncidentCalibrationCorrelation=double.NaN,PairCorrelation=double.NaN,CorrelationFloor=.7,DelaySeconds=double.NaN;
        internal double CalibrationIncidentWidthNs=double.NaN,CalibrationReflectionWidthNs=double.NaN,IncidentWidthNs=double.NaN,ReflectionWidthNs=double.NaN;
        internal double SamplePeriodNs=double.NaN,AttenuationTolerancePercent=15,WidthTolerancePercent=35;
        internal double CalibrationAdaptationLimitPercent=CalibrationQualityMath.DefaultAdaptationLimitPercent;
        internal double[] Weights=new[]{.40,.35,.15,.10};
        internal double RawPositionM=double.NaN,CableLengthM=double.NaN,NetDutRoundTripSeconds=double.NaN,LeadOneWaySeconds=0,PositionTolerancePercent=1;
        internal bool DoublePeakAvailable,IncidentDoublePeak,ReflectionDoublePeak,CalibrationExplainsDoublePeak;
        internal RawEndpointShape RawIncident,RawReflection,RawCalibrationIncident;
        internal RawEndpointPolicy EndpointPolicy;
        internal SeventhPipelineInput Copy(){return (SeventhPipelineInput)MemberwiseClone();}
    }
    internal sealed class SeventhPipelineResult
    {
        internal bool Available,CandidatePassed,AutoAccept,EndpointApplicable;
        internal bool CalibrationRecognized,BaseAttenuationPassed;
        internal bool RelaxedAttenuationFallback;
        internal bool ReviewHypothesis;
        internal double MainCalibrationUncertaintyPercent=double.NaN,OppositeCalibrationUncertaintyPercent=double.NaN;
        internal double MainEffectiveTolerancePercent=double.NaN,OppositeEffectiveTolerancePercent=double.NaN;
        internal double MainPropagatedUncertaintyPercent=double.NaN,OppositePropagatedUncertaintyPercent=double.NaN;
        internal double Score=double.NaN,AttenuationScore=double.NaN,WidthScore=double.NaN,CorrelationScore=double.NaN;
        internal double MainAttenuationErrorPercent=double.NaN,OppositeAttenuationErrorPercent=double.NaN;
        internal double ExpectedMainReflection=double.NaN,ExpectedOppositeReflection=double.NaN;
        internal double MainSlopePerSecond=double.NaN,OppositeSlopePerSecond=double.NaN;
        internal double RawPositionM=double.NaN,ProjectedPositionM=double.NaN;
        internal double NearExpectedMainOppositeRatio=double.NaN,FarExpectedMainOppositeRatio=double.NaN;
        internal double NearRatioErrorPercent=double.NaN,FarRatioErrorPercent=double.NaN;
        internal double ExpectedReflectionWidthNs=double.NaN,NearExpectedIncidentWidthNs=double.NaN,FarExpectedIncidentWidthNs=double.NaN;
        internal string EndpointDecision="Indeterminate";
        internal string[] Reasons=new string[0],EndpointReasons=new string[0];
    }
    internal static class SeventhPipelineMath
    {
        // Retry only a measured attenuation rejection. All other physical and
        // signal gates are re-evaluated unchanged; there is no unlimited retry.
        internal static SeventhPipelineResult EvaluateWithAttenuationFallback(SeventhPipelineInput x)
        {
            var original=Evaluate(x);
            if(!original.Available||original.CandidatePassed||
                Array.IndexOf(original.Reasons,"main_attenuation_outside_tolerance")<0)return original;
            if(!x.IndependentPackets||!x.SamePolarity||!x.CalibrationSamePolarity||
                x.PairCorrelation<=0||x.IncidentCalibrationCorrelation<=0||
                x.PairCorrelation<x.CorrelationFloor||x.IncidentCalibrationCorrelation<x.CorrelationFloor)return original;
            var retry=x.Copy();
            retry.AttenuationTolerancePercent=Math.Min(100,x.AttenuationTolerancePercent*1.5);
            retry.CalibrationAdaptationLimitPercent=Math.Max(retry.AttenuationTolerancePercent,x.CalibrationAdaptationLimitPercent);
            var relaxed=Evaluate(retry);
            if(!relaxed.CandidatePassed)
            {
                original.ReviewHypothesis=true;original.AutoAccept=false;
                original.Score=Math.Min(.49,original.Score*.5);
                var reviewReasons=new List<string>(original.Reasons);
                reviewReasons.Add("measured_echo_hypothesis_attenuation_still_failed_score_x0_5_manual_review");
                original.Reasons=reviewReasons.ToArray();return original;
            }
            // Keep the original requested-tolerance evidence, never reward
            // loosening the gate. A fallback is initialized for manual review.
            relaxed.Score=Math.Min(.69,original.Score*.8);
            relaxed.AttenuationScore=original.AttenuationScore;
            relaxed.BaseAttenuationPassed=original.BaseAttenuationPassed;
            relaxed.AutoAccept=false;relaxed.RelaxedAttenuationFallback=true;
            var reasons=new List<string>(original.Reasons);
            reasons.Add("attenuation_tolerance_x1_5_fallback_score_x0_8_manual_review");
            relaxed.Reasons=reasons.ToArray();return relaxed;
        }
        internal const string Version="2026.09.22-seventh-measured-review-endpoint6";
        internal const double MinimumAcceptedScore=.70;
        static bool Finite(double x){return ConfidenceMath.Finite(x);}
        static bool Positive(double x){return Finite(x)&&x>0;}
        static bool NoiseValid(double x){return Finite(x)&&x>=0;}
        static bool Snr(double amplitude,double noise,double minimum){return Positive(amplitude)&&NoiseValid(noise)&&amplitude>=minimum*noise;}
        static bool CorrelationValid(double x){return Finite(x)&&x>=-1&&x<=1;}
        static bool ToleranceValid(double x){return Finite(x)&&x>=0&&x<=100;}
        static double Error(double observed,double expected)
        {return Positive(observed)&&Positive(expected)&&Finite(observed/expected)?100*Math.Abs(observed/expected-1):double.NaN;}
        static bool Within(double error,double tolerance){return Finite(error)&&error<=tolerance;}
        static bool Fits(double observed,double expected,double tolerance)
        {return Positive(observed)&&Positive(expected)&&ToleranceValid(tolerance)&&observed>=expected*(1-tolerance/100)&&observed<=expected*(1+tolerance/100);}
        static double Consistency(double error,double tolerance)
        {
            if(!Finite(error))return double.NaN;
            if(tolerance==0)return error==0?1:0;
            double fraction=error/tolerance;
            return fraction<=1?1-.2*fraction*fraction:.8/fraction;
        }
        static bool WeightsValid(double[] weights)
        {
            if(weights==null||weights.Length!=4)return false;
            double sum=0;foreach(double value in weights){if(!Finite(value)||value<0)return false;sum+=value;}
            return Positive(sum);
        }
        // Full measured packet support is supplied by the caller. Outside that
        // finite support means comparison padding only, never missing acquisition
        // samples. Peak alignment is fixed: no lag search and no time stretching.
        internal static double Correlation(A7PulseDescriptor a,A7PulseDescriptor b)
        {
            if(!DescriptorValid(a)||!DescriptorValid(b))return double.NaN;
            double rate=Math.Min(a.SampleRateHz,b.SampleRateHz);
            double start=Math.Min(-a.Peak/a.SampleRateHz,-b.Peak/b.SampleRateHz);
            double end=Math.Max((a.Samples.Length-1-a.Peak)/a.SampleRateHz,(b.Samples.Length-1-b.Peak)/b.SampleRateHz);
            double steps=Math.Ceiling((end-start)*rate);
            if(!Finite(steps)||steps<2||steps>65535)return double.NaN;
            int n=(int)steps+1;double meanA=0,meanB=0,aa=0,bb=0,ab=0;
            for(int i=0;i<n;i++)
            {
                double x=DescriptorAt(a,start+i/rate),y=DescriptorAt(b,start+i/rate);
                if(!Finite(x)||!Finite(y))return double.NaN;
                double dx=x-meanA,dy=y-meanB;meanA+=dx/(i+1);meanB+=dy/(i+1);
                aa+=dx*(x-meanA);bb+=dy*(y-meanB);ab+=dx*(y-meanB);
            }
            if(!Positive(aa)||!Positive(bb)||!Finite(ab))return double.NaN;
            double rho=ab/Math.Sqrt(aa)/Math.Sqrt(bb);
            return Finite(rho)?Math.Max(-1,Math.Min(1,rho)):double.NaN;
        }
        static bool DescriptorValid(A7PulseDescriptor p)
        {
            if(p==null||!p.Valid||p.Samples==null||p.Samples.Length<3||p.Samples.Length>65536||
                p.Peak<=0||p.Peak>=p.Samples.Length-1||!Positive(p.SampleRateHz)||!Positive(Math.Abs(p.Samples[p.Peak])))return false;
            bool varied=false;double first=p.Samples[0];
            foreach(double value in p.Samples){if(!Finite(value))return false;if(value!=first)varied=true;}
            // Comparison padding must not turn a genuinely constant measured
            // support into a pulse with artificial variance/correlation.
            return varied;
        }
        static double DescriptorAt(A7PulseDescriptor p,double time)
        {
            double index=p.Peak+time*p.SampleRateHz;
            if(index<0||index>p.Samples.Length-1)return 0;
            int left=(int)Math.Floor(index),right=Math.Min(left+1,p.Samples.Length-1);
            return (p.Samples[left]+(p.Samples[right]-p.Samples[left])*(index-left))/p.Samples[p.Peak];
        }
        internal static SeventhPipelineResult Evaluate(SeventhPipelineInput x)
        {
            if(x==null)throw new ArgumentNullException("x");
            var r=new SeventhPipelineResult{RawPositionM=x.RawPositionM,ProjectedPositionM=x.RawPositionM};
            var reasons=new List<string>();
            bool geometry=x.GeometryValid&&Positive(x.CableLengthM)&&Finite(x.RawPositionM)&&
                Positive(x.NetDutRoundTripSeconds)&&Finite(x.LeadOneWaySeconds)&&x.LeadOneWaySeconds>=0&&
                Finite(x.PositionTolerancePercent)&&x.PositionTolerancePercent>=0&&x.PositionTolerancePercent<=100;
            if(!geometry)reasons.Add("physical_geometry_unavailable");
            if(!x.ReflectionWindowValid)reasons.Add("physical_reflection_selection_window_not_verified");
            if(!x.PhysicalCoverageValid)reasons.Add("minimum_95_percent_physical_waveform_coverage_not_verified");
            bool timing=Positive(x.DelaySeconds)&&Positive(x.CalibrationTravelSeconds);
            if(!timing)reasons.Add("positive_independent_travel_time_unavailable");
            if(!x.IndependentPackets)reasons.Add("packets_not_independent");
            if(!x.SamePolarity)reasons.Add("pulse_main_polarity_mismatch");
            if(!x.CalibrationSamePolarity)reasons.Add("calibration_main_polarity_mismatch");
            var mainQuality=CalibrationQualityMath.Evaluate(new CalibrationQualityInput {
                Identified=x.CalibrationIdentified,IndependentCompletePackets=x.CalibrationIndependentCompletePackets,Unclipped=x.CalibrationUnclipped,
                IncidentAmplitude=x.CalibrationMainIncident,ReflectionAmplitude=x.CalibrationMainReflection,
                IncidentNoise=x.CalibrationIncidentNoise,ReflectionNoise=x.CalibrationReflectionNoise});
            var oppositeQuality=CalibrationQualityMath.Evaluate(new CalibrationQualityInput {
                Identified=x.CalibrationIdentified,IndependentCompletePackets=x.CalibrationIndependentCompletePackets,Unclipped=x.CalibrationUnclipped,
                IncidentAmplitude=x.CalibrationOppositeIncident,ReflectionAmplitude=x.CalibrationOppositeReflection,
                IncidentNoise=x.CalibrationIncidentNoise,ReflectionNoise=x.CalibrationReflectionNoise});
            bool cal=mainQuality.Recognized;
            r.CalibrationRecognized=cal;
            r.MainCalibrationUncertaintyPercent=mainQuality.RelativeUncertaintyPercent;
            r.OppositeCalibrationUncertaintyPercent=oppositeQuality.RelativeUncertaintyPercent;
            if(!cal)reasons.Add("calibration_main_evidence_unavailable_or_below_six_sigma");
            if(!mainQuality.Recognized)reasons.Add("calibration_main_"+mainQuality.Reason);
            double mainRetention=cal?x.CalibrationMainReflection/x.CalibrationMainIncident:double.NaN;
            double oppositeRetention=oppositeQuality.Recognized?x.CalibrationOppositeReflection/x.CalibrationOppositeIncident:double.NaN;
            bool retention=Positive(mainRetention)&&mainRetention<=1;
            if(!retention)reasons.Add("recognized_calibration_nonincreasing_main_attenuation_model_unavailable");
            bool signal=Snr(x.IncidentMain,x.IncidentNoise,4.5)&&Snr(x.ReflectionMain,x.ReflectionNoise,4.5);
            bool endpointRatioAvailable=oppositeQuality.Recognized&&Positive(oppositeRetention)&&oppositeRetention<=1&&Snr(x.IncidentOpposite,x.IncidentNoise,4.5);
            if(!signal)reasons.Add("pulse_main_evidence_unavailable_or_below_signal_floor");
            bool correlations=CorrelationValid(x.PairCorrelation)&&CorrelationValid(x.IncidentCalibrationCorrelation)&&
                Finite(x.CorrelationFloor)&&x.CorrelationFloor>=0&&x.CorrelationFloor<=1;
            bool correlationPassed=correlations&&x.PairCorrelation>0&&x.IncidentCalibrationCorrelation>0&&
                x.PairCorrelation>=x.CorrelationFloor&&x.IncidentCalibrationCorrelation>=x.CorrelationFloor;
            if(!correlations)reasons.Add("signed_correlation_evidence_unavailable");
            else
            {
                r.CorrelationScore=ConfidenceMath.Unit(x.PairCorrelation);
                if(!correlationPassed)reasons.Add("signed_pair_or_calibration_correlation_below_floor");
            }
            bool tolerances=ToleranceValid(x.AttenuationTolerancePercent)&&ToleranceValid(x.WidthTolerancePercent)&&ToleranceValid(x.CalibrationAdaptationLimitPercent);
            if(!tolerances)reasons.Add("evidence_tolerance_invalid");
            bool attenuationAvailable=false,attenuationPassed=false;
            if(cal&&retention&&signal&&timing&&tolerances)
            {
                r.MainSlopePerSecond=Math.Log(mainRetention)/x.CalibrationTravelSeconds;
                r.OppositeSlopePerSecond=endpointRatioAvailable?Math.Log(oppositeRetention)/x.CalibrationTravelSeconds:double.NaN;
                r.ExpectedMainReflection=x.IncidentMain*Math.Exp(r.MainSlopePerSecond*x.DelaySeconds);
                r.ExpectedOppositeReflection=x.IncidentOpposite*Math.Exp(r.OppositeSlopePerSecond*x.DelaySeconds);
                r.MainAttenuationErrorPercent=Error(x.ReflectionMain,r.ExpectedMainReflection);
                r.OppositeAttenuationErrorPercent=Error(x.ReflectionOpposite,r.ExpectedOppositeReflection);
                double fraction=x.DelaySeconds/x.CalibrationTravelSeconds;
                bool mainTolerance=CalibrationQualityMath.ResolveTolerance(x.AttenuationTolerancePercent,x.CalibrationAdaptationLimitPercent,
                    mainQuality.RelativeUncertaintyPercent,fraction,out r.MainEffectiveTolerancePercent,out r.MainPropagatedUncertaintyPercent);
                CalibrationQualityMath.ResolveTolerance(x.AttenuationTolerancePercent,x.CalibrationAdaptationLimitPercent,
                    oppositeQuality.RelativeUncertaintyPercent,fraction,out r.OppositeEffectiveTolerancePercent,out r.OppositePropagatedUncertaintyPercent);
                // Opposite-lobe evidence is diagnostic/endpoint-only. It must
                // never veto or reduce a reflection candidate's attenuation score.
                attenuationAvailable=Finite(r.MainAttenuationErrorPercent)&&mainTolerance;
                r.BaseAttenuationPassed=attenuationAvailable&&Fits(x.ReflectionMain,r.ExpectedMainReflection,x.AttenuationTolerancePercent);
                attenuationPassed=attenuationAvailable&&Fits(x.ReflectionMain,r.ExpectedMainReflection,r.MainEffectiveTolerancePercent);
                if(attenuationAvailable)r.AttenuationScore=Consistency(r.MainAttenuationErrorPercent,x.AttenuationTolerancePercent);
            }
            if(!attenuationAvailable)reasons.Add("main_attenuation_prediction_unavailable");
            else if(!attenuationPassed)reasons.Add("main_attenuation_outside_tolerance");
            else if(!r.BaseAttenuationPassed)reasons.Add("calibration_uncertainty_expanded_qualification_base_score_unchanged");
            bool widths=Positive(x.CalibrationIncidentWidthNs)&&Positive(x.CalibrationReflectionWidthNs)&&Positive(x.IncidentWidthNs)&&
                Positive(x.ReflectionWidthNs)&&Positive(x.SamplePeriodNs)&&timing&&tolerances;
            bool widthAvailable=false,widthPassed=false;r.WidthScore=0;
            if(widths)
            {
                double fraction=x.DelaySeconds/x.CalibrationTravelSeconds;
                r.ExpectedReflectionWidthNs=x.IncidentWidthNs*(1+(x.CalibrationReflectionWidthNs/x.CalibrationIncidentWidthNs-1)*fraction);
                double error=Error(x.ReflectionWidthNs,r.ExpectedReflectionWidthNs);
                widthAvailable=Finite(error);bool widthFits=Fits(x.ReflectionWidthNs,r.ExpectedReflectionWidthNs,x.WidthTolerancePercent);
                r.WidthScore=Consistency(error,x.WidthTolerancePercent);
                widthPassed=widthAvailable&&widthFits&&
                    x.ReflectionWidthNs+x.SamplePeriodNs>=x.IncidentWidthNs&&
                    x.CalibrationReflectionWidthNs+x.SamplePeriodNs>=x.CalibrationIncidentWidthNs;
                if(!Finite(r.WidthScore))r.WidthScore=0;
                if(x.ReflectionWidthNs+x.SamplePeriodNs<x.IncidentWidthNs||
                    x.CalibrationReflectionWidthNs+x.SamplePeriodNs<x.CalibrationIncidentWidthNs)r.WidthScore*=.5;
            }
            if(!widthAvailable)reasons.Add("soft_width_unavailable_zero_width_component");
            else if(!widthPassed)reasons.Add("soft_width_or_broadening_conflict_score_only");
            bool weights=WeightsValid(x.Weights);if(!weights)reasons.Add("four_evidence_weights_invalid");
            r.Available=geometry&&x.ReflectionWindowValid&&x.PhysicalCoverageValid&&timing&&cal&&retention&&signal&&correlations&&tolerances&&attenuationAvailable&&weights;
            r.CandidatePassed=r.Available&&x.IndependentPackets&&x.SamePolarity&&x.CalibrationSamePolarity&&correlationPassed&&attenuationPassed;
            if(r.Available)
            {
                r.Score=ConfidenceMath.WeightedScore(new[]{r.CorrelationScore,r.AttenuationScore,r.WidthScore,ConfidenceMath.Unit(x.IncidentCalibrationCorrelation)},x.Weights,new[]{true,true,true,true});
                // Expanded qualification must never improve the same pair's
                // engineering score or remove its requested-tolerance ceiling.
                if(!r.CandidatePassed||!r.BaseAttenuationPassed)r.Score=Math.Min(r.Score,.49*r.AttenuationScore);
                if(!x.SamePolarity||!x.CalibrationSamePolarity||!x.IndependentPackets)r.Score=0;
            }
            Endpoint(x,r,geometry,cal&&retention&&signal&&endpointRatioAvailable,widths);
            r.AutoAccept=r.CandidatePassed&&r.Score>=MinimumAcceptedScore&&(!r.EndpointApplicable||r.EndpointDecision!="Indeterminate");
            if(r.Available&&r.Score<MinimumAcceptedScore)reasons.Add("engineering_evidence_below_70_percent");
            if(r.EndpointApplicable&&r.EndpointDecision=="Indeterminate")reasons.Add("endpoint_identity_requires_review");
            r.Reasons=reasons.ToArray();return r;
        }
        static void Endpoint(SeventhPipelineInput x,SeventhPipelineResult r,bool geometry,bool amplitudes,bool widths)
        {
            r.EndpointApplicable=geometry&&Math.Min(Math.Abs(x.RawPositionM),Math.Abs(x.RawPositionM-x.CableLengthM))<=x.CableLengthM*x.PositionTolerancePercent/100;
            if(!r.EndpointApplicable){r.EndpointReasons=new[]{"not_in_endpoint_position_band"};return;}
            if(!r.CandidatePassed){r.EndpointReasons=new[]{"pair_not_qualified_for_endpoint_projection"};return;}
            var decision=RawEndpointMath.Decide(x.RawIncident,x.RawReflection,x.RawCalibrationIncident,x.EndpointPolicy);
            r.EndpointDecision=decision.Direction;r.EndpointReasons=new[]{RawEndpointMath.Version,decision.Reason};
            if(decision.Direction=="Far")r.ProjectedPositionM=x.CableLengthM;
            else if(decision.Direction=="Near")r.ProjectedPositionM=0;
        }
    }
}
