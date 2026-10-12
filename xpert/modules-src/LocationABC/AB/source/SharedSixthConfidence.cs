using System;

namespace HVX.SignalEvidence
{
    internal sealed class SixthCalibrationReference
    {
        internal bool Available;
        internal string Reason="calibration_missing";
        internal int IncidentPeak=-1,ReflectionPeak=-1;
        internal double Baseline=double.NaN,IncidentAmplitude=double.NaN,ReflectionAmplitude=double.NaN;
        internal double Retention=double.NaN,TravelSeconds=double.NaN;
        internal double NoiseSigma=double.NaN;
        internal int QuietSampleCount;
        internal bool NoiseReliable;
        internal bool IndependentCompletePackets,Unclipped;
        internal CalibrationQualityResult Quality=new CalibrationQualityResult();
    }
    // Shared raw-amplitude tolerance policy used by all seven methods and
    // manual PDmap review. Name retained for internal source compatibility.
    internal static class SixthConfidenceMath
    {
        internal const string Version="2026.09.22-raw-calibration-quality-tolerance2";
        internal static bool BasicCalibrationRatioValid(double ratio)
        {return ConfidenceMath.Finite(ratio)&&ratio>0&&ratio<=1.10;}

        // Recognizability is independent of attenuation amount. The raw packets
        // need complete quiet guards and identifiable unsaturated peaks.
        internal static SixthCalibrationReference CalibrationFromMarkers(double[] raw,int markedIncident,int markedReflection,double fs)
        {
            var r=new SixthCalibrationReference();
            if(raw==null||raw.Length<3||!ConfidenceMath.Finite(fs)||fs<=0||
                markedIncident<0||markedReflection>=raw.Length||markedReflection<=markedIncident)
            {r.Reason="calibration_markers_samples_or_rate_invalid";return r;}
            double[] ordered=(double[])raw.Clone();
            foreach(double value in ordered)if(!ConfidenceMath.Finite(value))
            {r.Reason="calibration_nonfinite_sample";return r;}
            Array.Sort(ordered);int middle=ordered.Length/2;
            r.Baseline=ordered.Length%2==0?ordered[middle-1]/2+ordered[middle]/2:ordered[middle];
            int radius=Math.Max(2,Math.Min(64,(markedReflection-markedIncident)/8));
            r.IncidentAmplitude=0;r.ReflectionAmplitude=0;
            for(int k=Math.Max(0,markedIncident-radius);k<=Math.Min(markedReflection-1,markedIncident+radius);k++)
            {double amplitude=Math.Abs(raw[k]-r.Baseline);if(amplitude>r.IncidentAmplitude){r.IncidentAmplitude=amplitude;r.IncidentPeak=k;}}
            for(int k=Math.Max(r.IncidentPeak+1,markedReflection-radius);k<=Math.Min(raw.Length-1,markedReflection+radius);k++)
            {double amplitude=Math.Abs(raw[k]-r.Baseline);if(amplitude>r.ReflectionAmplitude){r.ReflectionAmplitude=amplitude;r.ReflectionPeak=k;}}
            if(!ConfidenceMath.Finite(r.IncidentAmplitude)||r.IncidentAmplitude<=0||
                !ConfidenceMath.Finite(r.ReflectionAmplitude)||r.ReflectionAmplitude<=0||r.ReflectionPeak<=r.IncidentPeak)
            {r.Reason="calibration_original_peaks_missing_or_nonfinite";return r;}
            r.Retention=r.ReflectionAmplitude/r.IncidentAmplitude;
            r.TravelSeconds=(r.ReflectionPeak-(double)r.IncidentPeak)/fs;
            if(!ConfidenceMath.Finite(r.Retention)||r.Retention<=0||!ConfidenceMath.Finite(r.TravelSeconds)||r.TravelSeconds<=0)
            {r.Reason="calibration_retention_or_travel_not_representable";return r;}
            var quiet=new double[raw.Length];
            for(int k=0;k<raw.Length;k++)
                if((k<markedIncident-radius||k>markedIncident+radius)&&(k<markedReflection-radius||k>markedReflection+radius))
                    quiet[r.QuietSampleCount++]=raw[k];
            if(r.QuietSampleCount<16){r.Reason="calibration_quiet_samples_below_sixteen";return r;}
            Array.Sort(quiet,0,r.QuietSampleCount);middle=r.QuietSampleCount/2;
            double quietMedian=r.QuietSampleCount%2==0?quiet[middle-1]/2+quiet[middle]/2:quiet[middle];
            for(int k=0;k<r.QuietSampleCount;k++)quiet[k]=Math.Abs(quiet[k]-quietMedian);
            Array.Sort(quiet,0,r.QuietSampleCount);
            double mad=r.QuietSampleCount%2==0?quiet[middle-1]/2+quiet[middle]/2:quiet[middle];
            r.NoiseSigma=1.4826*mad;
            r.NoiseReliable=ConfidenceMath.Finite(r.NoiseSigma)&&r.NoiseSigma>=0&&
                r.IncidentAmplitude>=6*r.NoiseSigma&&r.ReflectionAmplitude>=6*r.NoiseSigma;
            int split=r.IncidentPeak+(r.ReflectionPeak-r.IncidentPeak)/2;
            int leftI,rightI,leftR,rightR;
            r.IndependentCompletePackets=TryQuietGuards(raw,r.IncidentPeak,0,split,r.Baseline,r.NoiseSigma,r.IncidentAmplitude,out leftI,out rightI)&&
                TryQuietGuards(raw,r.ReflectionPeak,split+1,raw.Length-1,r.Baseline,r.NoiseSigma,r.ReflectionAmplitude,out leftR,out rightR);
            r.Unclipped=UnclippedPeak(raw,r.IncidentPeak,r.Baseline)&&UnclippedPeak(raw,r.ReflectionPeak,r.Baseline);
            r.Quality=CalibrationQualityMath.Evaluate(new CalibrationQualityInput {
                Identified=r.ReflectionPeak>r.IncidentPeak,IndependentCompletePackets=r.IndependentCompletePackets,Unclipped=r.Unclipped,
                IncidentAmplitude=r.IncidentAmplitude,ReflectionAmplitude=r.ReflectionAmplitude,IncidentNoise=r.NoiseSigma,ReflectionNoise=r.NoiseSigma});
            r.Available=r.Quality.Recognized;r.Reason=r.Quality.Reason;
            return r;
        }

        static bool TryQuietGuards(double[] raw,int peak,int minimum,int maximum,double baseline,double noise,double amplitude,out int left,out int right)
        {
            left=right=-1;
            if(!ConfidenceMath.Finite(noise)||noise<0||minimum<0||maximum>=raw.Length||peak<=minimum||peak>=maximum)return false;
            double quiet=Math.Max(6*noise,.02*amplitude);int run=0;
            for(int i=peak-1;i>=minimum;i--){run=Math.Abs(raw[i]-baseline)<=quiet?run+1:0;if(run==16){left=i;break;}}
            run=0;
            for(int i=peak+1;i<=maximum;i++){run=Math.Abs(raw[i]-baseline)<=quiet?run+1:0;if(run==16){right=i;break;}}
            return left>=minimum&&right<=maximum&&right>=0;
        }
        static bool UnclippedPeak(double[] raw,int peak,double baseline)
        {
            if(peak<=0||peak>=raw.Length-1)return false;
            double magnitude=Math.Abs(raw[peak]-baseline),epsilon=Math.Max(1e-15,magnitude*1e-12);
            int count=1;
            for(int i=peak-1;i>=0&&count<3&&Math.Abs(raw[i]-raw[peak])<=epsilon;i--)count++;
            for(int i=peak+1;i<raw.Length&&count<3&&Math.Abs(raw[i]-raw[peak])<=epsilon;i++)count++;
            return count<3;
        }

        internal static AttenuationEvidence Attenuation(double incidentAmplitude,double reflectionAmplitude,
            double calRetention,double delaySeconds,double calTravelSeconds,double tolerancePercent,bool referenceReliable)
        {
            if(!referenceReliable)return new AttenuationEvidence {Reason="calibration_reference_unreliable"};
            if(!BasicCalibrationRatioValid(calRetention))
                return new AttenuationEvidence {Reason="calibration_retention_nonpositive_or_above_1point10"};
            var r=ConfidenceMath.Attenuation(incidentAmplitude,reflectionAmplitude,calRetention,
                delaySeconds,calTravelSeconds,tolerancePercent,true);
            if(!r.Available)return r;
            r.BasePassed=r.Passed;r.RequestedTolerancePercent=r.EffectiveTolerancePercent=tolerancePercent;
            r.PropagatedUncertaintyPercent=0;r.AdaptationLimitPercent=tolerancePercent;
            // The entered tolerance is a full-contribution acceptance interval,
            // not the standard deviation of a statistical probability model.
            // Outside it the damping contribution decreases rationally; it is
            // never transferred to correlation, and it cannot authorize a pair.
            r.Score=r.Passed?1:tolerancePercent==0?0:
                ConfidenceMath.Unit(tolerancePercent/r.ErrorPercent);
            return r;
        }
        internal static AttenuationEvidence Attenuation(double incidentAmplitude,double reflectionAmplitude,
            double calRetention,double delaySeconds,double calTravelSeconds,double tolerancePercent,bool referenceReliable,
            double relativeUncertaintyPercent,double adaptationLimitPercent)
        {
            var r=Attenuation(incidentAmplitude,reflectionAmplitude,calRetention,delaySeconds,calTravelSeconds,tolerancePercent,referenceReliable);
            if(!r.Available)return r;
            double effective,propagated;
            if(!CalibrationQualityMath.ResolveTolerance(tolerancePercent,adaptationLimitPercent,relativeUncertaintyPercent,r.TravelFraction,out effective,out propagated))
                return new AttenuationEvidence {Reason="calibration_uncertainty_or_adaptation_limit_invalid"};
            r.EffectiveTolerancePercent=effective;r.PropagatedUncertaintyPercent=propagated;r.AdaptationLimitPercent=adaptationLimitPercent;
            r.LowerAmplitude=r.PredictedAmplitude*(1-effective/100);r.UpperAmplitude=r.PredictedAmplitude*(1+effective/100);
            r.Passed=effective==0?r.BasePassed:reflectionAmplitude>=r.LowerAmplitude&&reflectionAmplitude<=r.UpperAmplitude;
            r.Reason=r.Passed?(r.BasePassed?"within_requested_reflection_tolerance":"within_bounded_calibration_uncertainty_tolerance"):
                "outside_effective_reflection_tolerance";
            // Score and BasePassed remain from the requested tolerance. Noisier
            // calibration may widen a review gate, never improve the score.
            return r;
        }
        internal static double WeightedScore(double[] values,double[] weights,bool[] enabled,double outputScale=1)
        {return ConfidenceMath.WeightedScore(values,weights,enabled,outputScale);}
        internal static double AttenuationCeiling(bool enabled,bool available,bool passed,double score,double outputScale=1)
        {
            if(!ConfidenceMath.Finite(outputScale)||outputScale<=0)
                throw new ArgumentException("Evidence output scale must be finite and positive.");
            if(enabled&&!available)return double.NaN;
            // Bounded rational severity ceiling, not a minimum awarded score:
            // excellent rho cannot hide a gross mismatch, while the old
            // exponential collapse to numerical zero is avoided.
            return enabled&&!passed?.49*outputScale*(.5+.5*ConfidenceMath.Unit(score)):outputScale;
        }
    }
}
