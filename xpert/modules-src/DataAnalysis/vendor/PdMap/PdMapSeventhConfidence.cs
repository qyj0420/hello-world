using System;
using System.Globalization;
using HVX.SignalEvidence;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        sealed class ManualSeventhBand
        {internal double[] Raw,Band;internal double Rate,Baseline=double.NaN,BandNoise=double.NaN;internal string Reason;}
        ManualSeventhBand seventhPulseBand,seventhCalibrationBand;
        internal double SeventhManualCorrelationFloor=.7;

        ManualSeventhBand SeventhFixedBand(double[] raw,double rate,bool isCalibration)
        {
            var cached=isCalibration?seventhCalibrationBand:seventhPulseBand;
            if(cached!=null&&ReferenceEquals(cached.Raw,raw)&&cached.Rate.Equals(rate))return cached;
            cached=new ManualSeventhBand{Raw=raw,Rate=rate};
            cached.Band=PdMapSignalFilter.Apply(raw,rate,new[]{0d,10000,3000000,4,5,2},out cached.Reason);
            if(raw!=null&&raw.Length>0)
            {
                var sorted=(double[])raw.Clone();Array.Sort(sorted);
                if(IsFinite(sorted[0])&&IsFinite(sorted[sorted.Length-1]))cached.Baseline=MedianSeventh(sorted);
            }
            if(cached.Band!=null&&cached.Band.Length>0)
            {
                // The fixed-band shape gate must use fixed-band noise. A robust
                // whole-record MAD is selected before the manual pair is scored;
                // real pulses occupy a small minority of the source record.
                var sorted=(double[])cached.Band.Clone();Array.Sort(sorted);
                if(IsFinite(sorted[0])&&IsFinite(sorted[sorted.Length-1]))
                {
                    double center=MedianSeventh(sorted);
                    for(int i=0;i<sorted.Length;i++)sorted[i]=Math.Abs(sorted[i]-center);
                    Array.Sort(sorted);
                    cached.BandNoise=Math.Max(1e-15,1.4826*MedianSeventh(sorted));
                }
            }
            if(isCalibration)seventhCalibrationBand=cached;else seventhPulseBand=cached;
            return cached;
        }
        bool IsSeventhRow {get{return originalParameterRow.Length>26&&originalParameterRow[26]==6;}}
        bool SeventhEndpointTimingMustBePreserved()
        {
            if(!IsSeventhRow||!reflectionSelected||reflection<=incident||!IsFinite(cableLengthM)||cableLengthM<=0)return false;
            double raw=CalculatedLocation(),position=LocationM,tolerance=cableLengthM*acceptedPositionErrorPercent/100;
            return IsFinite(raw)&&Math.Min(Math.Abs(raw),Math.Abs(raw-cableLengthM))<=tolerance||
                IsFinite(position)&&(position==0||position==cableLengthM);
        }
        bool SeventhPhysicalCoverage(double roundTripSamples)
        {
            if(!IsFinite(roundTripSamples)||roundTripSamples<=0||roundTripSamples>int.MaxValue-1)return false;
            long needed=(long)Math.Ceiling(roundTripSamples)+1,valid=0;
            int end=(int)Math.Min(pulse.Length-1,(long)incident+needed-1);
            for(int i=incident;i<=end;i++)if(i>=0&&IsFinite(pulse[i]))valid++;
            return valid*100>=needed*95;
        }
        // A cropped review matrix cannot recover the complete automatic packet
        // catalog. Measure only an independently isolated packet with two real
        // 16-sample quiet guards; missing provenance never becomes a fake score.
        static SeventhPacketMeasurement MeasureManualSeventh(ManualSeventhBand signal,int anchor,int minimum,int maximum,double noise,
            out int supportStart,out int supportEnd)
        {
            supportStart=supportEnd=-1;
            var unavailable=new SeventhPacketMeasurement();
            if(signal==null||signal.Raw==null||signal.Band==null||signal.Reason==null||signal.Reason.StartsWith("bypass:")||
                !IsFinite(signal.Rate)||signal.Rate<6000000||!IsFinite(noise)||noise<0||!IsFinite(signal.BandNoise))return unavailable;
            double[] raw=signal.Raw,band=signal.Band;
            if(raw.Length!=band.Length||anchor<0||anchor>=raw.Length||minimum<0||maximum>=raw.Length||anchor<minimum||anchor>maximum)return unavailable;
            // Both domains were independently filtered from unchanged raw input.
            if(!IsFinite(signal.Baseline))return unavailable;
            double baseline=signal.Baseline,seed=Math.Abs(raw[anchor]-baseline);
            noise=Math.Max(1e-15,noise);
            if(!IsFinite(seed)||seed<4.5*noise){unavailable.Reason="selected_sample_below_raw_noise_gate";return unavailable;}
            double quiet=Math.Max(4*noise,.02*seed);
            // Ring-down in a real raw packet can exceed 2 us. Search the
            // available, pair-separated crop (bounded to 4096 samples), not a
            // fixed duration which truncated valid endpoint-pulse tails.
            // Guards remain the FIRST sixteen quiet original samples, chosen
            // before measuring a pair score or attenuation error.
            int span=Math.Min(4096,Math.Max(64,maximum-minimum));
            int left=-1,right=-1,count=0;
            for(int i=anchor;i>=Math.Max(minimum,anchor-span);i--)
            {if(Math.Abs(raw[i]-baseline)<=quiet)count++;else count=0;if(count==16){left=i+16;break;}}
            count=0;
            for(int i=anchor;i<=Math.Min(maximum,anchor+span);i++)
            {if(Math.Abs(raw[i]-baseline)<=quiet)count++;else count=0;if(count==16){right=i-16;break;}}
            if(left<minimum+16||right>maximum-16||right<=left||anchor<left||anchor>right){unavailable.Reason="independent_quiet_guards_unavailable";return unavailable;}
            var before=new double[16];var after=new double[16];
            Array.Copy(raw,left-16,before,0,16);Array.Copy(raw,right+1,after,0,16);Array.Sort(before);Array.Sort(after);
            double first=MedianSeventh(before),last=MedianSeventh(after);
            double guardNoise=Math.Max(noise,Math.Max(SeventhGuardRms(before,first),SeventhGuardRms(after,last)));
            if(!IsFinite(guardNoise)||Math.Abs(first-last)>3*guardNoise){unavailable.Reason="guard_baseline_unstable";return unavailable;}
            foreach(double value in before)if(Math.Abs(value-first)>Math.Max(6*noise,.15*seed)){unavailable.Reason="left_guard_pulse_contamination";return unavailable;}
            foreach(double value in after)if(Math.Abs(value-last)>Math.Max(6*noise,.15*seed)){unavailable.Reason="right_guard_pulse_contamination";return unavailable;}
            int begin=left-16,end=right+16;
            // minimum/maximum are inclusive acquisition limits. A complete
            // guard ending exactly at either limit is not missing a sample.
            if(begin<minimum||end>maximum)return unavailable;
            var rawResidual=new double[right-left+1];var bandResidual=new double[end-begin+1];
            double leftCenter=left-8.5,rightCenter=right+8.5;
            for(int i=left;i<=right;i++)rawResidual[i-left]=raw[i]-(first+(last-first)*(i-leftCenter)/(rightCenter-leftCenter));
            double bandBaseline=0;
            for(int i=begin;i<left;i++)bandBaseline+=band[i]/32;
            for(int i=right+1;i<=end;i++)bandBaseline+=band[i]/32;
            for(int i=begin;i<=end;i++)bandResidual[i-begin]=band[i]-bandBaseline;
            var result=SeventhPacketMath.Measure(new SeventhPacketInput{RawResidual=rawResidual,FixedBandResidual=bandResidual,
                RawOrigin=left,FixedBandOrigin=begin,SampleRateHz=signal.Rate,RawNoise=guardNoise,BandNoise=signal.BandNoise,CompleteGuard=true});
            if(result.Valid){supportStart=begin;supportEnd=end;}
            return result;
        }
        static double MedianSeventh(double[] sorted){int n=sorted.Length;return n%2==0?sorted[n/2-1]/2+sorted[n/2]/2:sorted[n/2];}
        static double SeventhGuardRms(double[] samples,double center)
        {double sum=0;foreach(double x in samples){double d=x-center;sum+=d*d;}return Math.Sqrt(sum/samples.Length);}

        void UpdateSeventhManualConfidence()
        {
            double roundTrip;int lower,upper;
            bool geometry=TrySampleDeltaForPosition(mirrorLocation?cableLengthM:0,out roundTrip)&&roundTrip>0;
            bool window=TryReflectionSelectionBounds(out lower,out upper)&&reflection>=lower&&reflection<=upper;
            var input=new SeventhPipelineInput{EndpointPolicy=ManufacturerEndpointPolicy,GeometryValid=geometry,ReflectionWindowValid=window,
                PhysicalCoverageValid=geometry&&SeventhPhysicalCoverage(roundTrip),RawPositionM=currentLocationM,CableLengthM=cableLengthM,
                DelaySeconds=(reflection-(double)incident)*dtNs*1e-9,NetDutRoundTripSeconds=roundTrip*dtNs*1e-9,
                LeadOneWaySeconds=ConnectingCableLengthM/150*1e-6,PositionTolerancePercent=acceptedPositionErrorPercent,
                SamplePeriodNs=dtNs,AttenuationTolerancePercent=confidenceAttenuationTolerancePercent,CorrelationFloor=SeventhManualCorrelationFloor};
            input.CalibrationAdaptationLimitPercent=calibrationToleranceAdaptationLimitPercent;
            SixthCalibrationReference reference;
            // The display-only nondecreasing-line gate must not change the
            // shared main-lobe scoring contract; opposite lobes are endpoint-only.
            bool calibrated=TryRawDampingReference(out reference,false);
            SeventhPacketMeasurement a=new SeventhPacketMeasurement(),b=new SeventhPacketMeasurement(),ca=new SeventhPacketMeasurement(),cb=new SeventhPacketMeasurement();
            int a0=-1,a1=-1,b0=-1,b1=-1,c0=-1,c1=-1,d0=-1,d1=-1;
            if(calibrated)
            {
                double[] cal=ProjectCalibrationEnabled?selectedProjectCalibration.Wave:calibration;
                double rate=ProjectCalibrationEnabled?selectedProjectCalibration.Rate:1e9/dtNs;
                var cbands=SeventhFixedBand(cal,rate,true);int mid=reference.IncidentPeak+(reference.ReflectionPeak-reference.IncidentPeak)/2;
                ca=MeasureManualSeventh(cbands,reference.IncidentPeak,0,mid,reference.NoiseSigma,out c0,out c1);
                cb=MeasureManualSeventh(cbands,reference.ReflectionPeak,mid+1,cal.Length-1,reference.NoiseSigma,out d0,out d1);
                input.CalibrationTravelSeconds=reference.TravelSeconds;
                var band=SeventhFixedBand(pulse,1e9/dtNs,false);int middle=incident+(reflection-incident)/2;
                double noise=Safe(originalParameterRow,28,double.NaN);
                a=MeasureManualSeventh(band,incident,0,middle,noise,out a0,out a1);
                b=MeasureManualSeventh(band,reflection,middle+1,pulse.Length-1,noise,out b0,out b1);
            }
            input.IndependentPackets=a.Valid&&b.Valid&&a1<b0;
            input.SamePolarity=a.Valid&&b.Valid&&a.Sign==b.Sign;
            input.CalibrationSamePolarity=ca.Valid&&cb.Valid&&ca.Sign==cb.Sign;
            input.CalibrationIdentified=calibrated&&reference!=null&&reference.Quality.Recognized;
            input.CalibrationIndependentCompletePackets=ca.Valid&&cb.Valid&&c1<d0;
            input.CalibrationUnclipped=calibrated&&reference!=null&&reference.Unclipped;
            input.CalibrationMainIncident=ca.Main;input.CalibrationMainReflection=cb.Main;
            input.CalibrationOppositeIncident=ca.Opposite;input.CalibrationOppositeReflection=cb.Opposite;
            input.CalibrationIncidentNoise=ca.Noise;input.CalibrationReflectionNoise=cb.Noise;
            input.IncidentMain=a.Main;input.ReflectionMain=b.Main;input.IncidentOpposite=a.Opposite;input.ReflectionOpposite=b.Opposite;
            input.IncidentNoise=a.Noise;input.ReflectionNoise=b.Noise;
            input.IncidentCalibrationCorrelation=SeventhPipelineMath.Correlation(a.Shape,ca.Shape);
            input.PairCorrelation=SeventhPipelineMath.Correlation(a.Shape,b.Shape);
            bool fwhm=a.Shape.FwhmValid&&b.Shape.FwhmValid&&ca.Shape.FwhmValid&&cb.Shape.FwhmValid;
            input.IncidentWidthNs=fwhm?a.Shape.FwhmNs:a.Shape.EnergyWidthNs;input.ReflectionWidthNs=fwhm?b.Shape.FwhmNs:b.Shape.EnergyWidthNs;
            input.CalibrationIncidentWidthNs=fwhm?ca.Shape.FwhmNs:ca.Shape.EnergyWidthNs;
            input.CalibrationReflectionWidthNs=fwhm?cb.Shape.FwhmNs:cb.Shape.EnergyWidthNs;
            input.DoublePeakAvailable=a.Valid&&b.Valid&&ca.Valid&&cb.Valid;input.ReflectionDoublePeak=b.Shape.MultipleHalfHeightComponents;
            input.CalibrationExplainsDoublePeak=ca.Shape.MultipleHalfHeightComponents||cb.Shape.MultipleHalfHeightComponents;
            input.RawIncident=a.RawShape;input.RawReflection=b.RawShape;input.RawCalibrationIncident=ca.RawShape;
            var result=SeventhPipelineMath.EvaluateWithAttenuationFallback(input);
            // Endpoint identity and measured pair quality are distinct. A
            // measured low/conflicting score must stay visible for review even
            // while Near/Far cannot be resolved; NaN means missing evidence.
            confidence=result.Available?100*result.Score:double.NaN;
            correlation=IsFinite(input.PairCorrelation)?100*input.PairCorrelation:double.NaN;
            confidenceStatusCode=!result.Available?"SeventhMeasurementContextUnavailable":result.ReviewHypothesis?"SeventhAttenuationReviewHypothesis":!result.CandidatePassed?"SeventhEvidenceConflict":
                result.EndpointApplicable&&result.EndpointDecision=="Indeterminate"?"SeventhEndpointPending":"SeventhManualEvidenceMeasured";
            if(result.CandidatePassed&&result.EndpointDecision!="Indeterminate")
            {currentLocationM=result.ProjectedPositionM;CountSamePositionPulses(currentLocationM);}
            confidenceDetails=string.Format(CultureInfo.InvariantCulture,
                "math={0};measurement=manual_independently_guarded_crop;automaticContextRecreated=false;manualRawFallbackAllowed=false;fixedBandHz=10000,3000000;scoreIsProbability=false;automaticAcceptance=false;historyTieBreakerUsed=false;correlationFloor={12:R};widthTolerancePercent=35;attenuationTolerancePercent={1:R};mainErrorPercent={2:R};oppositeErrorPercent={3:R};rawTimeLocationM={4:R};projectedPositionM={5:R};endpointDecision={6};rawIndicesAndDelayPreserved=true;reason={7};endpointReason={8};rawReference={9};engineeringBaseScore={10:R};confidenceUnavailable={11}",
                SeventhPipelineMath.Version,confidenceAttenuationTolerancePercent,result.MainAttenuationErrorPercent,result.OppositeAttenuationErrorPercent,
                result.RawPositionM,result.ProjectedPositionM,result.EndpointDecision,string.Join(",",result.Reasons),string.Join(",",result.EndpointReasons),rawDampingReferenceReason,result.Score,double.IsNaN(confidence),input.CorrelationFloor);
            confidenceDetails+=string.Format(CultureInfo.InvariantCulture,
                ";calibrationRecognized={0};mainCalibrationUncertaintyPercent={1:R};oppositeCalibrationUncertaintyPercent={2:R};mainEffectiveTolerancePercent={3:R};oppositeEffectiveTolerancePercent={4:R};mainPropagatedUncertaintyPercent={5:R};oppositePropagatedUncertaintyPercent={6:R};calibrationAdaptationLimitPercent={7:R};baseAttenuationPassed={8};adaptationDoesNotIncreaseScore=true",
                result.CalibrationRecognized,result.MainCalibrationUncertaintyPercent,result.OppositeCalibrationUncertaintyPercent,
                result.MainEffectiveTolerancePercent,result.OppositeEffectiveTolerancePercent,result.MainPropagatedUncertaintyPercent,
                result.OppositePropagatedUncertaintyPercent,calibrationToleranceAdaptationLimitPercent,result.BaseAttenuationPassed);
            confidenceDetails+=";secondaryLobeRole=endpoint_only_not_candidate_gate_or_score;widthRole=soft_score_only";
            confidenceDetails+=";incidentMeasurement="+a.Reason+";reflectionMeasurement="+b.Reason+
                ";calibrationIncidentMeasurement="+ca.Reason+";calibrationReflectionMeasurement="+cb.Reason+
                ";measuredScorePreservedWhenEndpointPending=true";
            confidenceDetails+=string.Format(CultureInfo.InvariantCulture,";rawEndpointPolicy={0};rawIncidentFwhmNs={1:R};rawCalIncidentFwhmNs={2:R};rawIncidentRatio={3:R};rawCalRatio={4:R};rawReflectionSplit={5}",
                RawEndpointMath.Version,a.RawShape.FwhmNs,ca.RawShape.FwhmNs,a.RawShape.Ratio,ca.RawShape.Ratio,b.RawShape.Split);
        }
    }
}
