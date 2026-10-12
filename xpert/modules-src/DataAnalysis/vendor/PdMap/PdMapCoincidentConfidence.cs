using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using HVX.SignalEvidence;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        readonly FilterCache coincidentPulseBand=new FilterCache(),coincidentCalibrationBand=new FilterCache();
        static readonly double[] coincidentBandSettings={0,10000,3000000};
        int coincidentIndependentCount;
        [Browsable(false)] public int CoincidentIndependentSameLocationCount {get{return coincidentIndependentCount;}}

        // Called before the ordinary ordered-pair guard, for all method IDs.
        // Neither a placeholder reflection nor a constant trace has a self rho.
        void UpdateCoincidentConfidence()
        {
            // A coincident self match has no independent echo confidence.
            confidence=double.NaN;
            coincidentIndependentCount=0;
            double ampA,ampB,rho;
            if(!TryConfidenceWindows(out ampA,out ampB,out rho))
            {confidenceStatusCode="CoincidentWaveformUnavailable";return;}
            correlation=100;
            if(!IsFinite(dtNs)||dtNs<=0||originalParameterRow.Length<=7||
                !IsFinite(originalParameterRow[7])||originalParameterRow[7]<=0||
                !IsFinite(originalParameterRow[5])||originalParameterRow[5]<=0||!velocityConfigured||!IsFinite(currentLocationM)||
                !BoundaryConfidenceMath.WithinTolerance(currentLocationM,cableLengthM))
            {confidenceStatusCode="GeometryUnavailable";return;}
            double sigma=Safe(originalParameterRow,28,double.NaN),threshold=Safe(originalParameterRow,4,double.NaN);
            if(!IsFinite(sigma)||sigma<=0||!IsFinite(threshold)||threshold<0)
            {confidenceStatusCode="CoincidentNoiseEvidenceUnavailable";return;}
            if(ampA<threshold||ampA/sigma<4.5)
            {confidenceStatusCode="CoincidentPulseBelowSignalFloor";return;}
            double retention,travel;string basis,error;
            if(!TrySixthManualReference(out retention,out travel,out basis,out error))
            {confidenceStatusCode=error;return;}
            double calRate=sixthReferenceRate,fs=1e9/dtNs;
            if(calRate<=6000000||fs<=6000000)
            {confidenceStatusCode="CoincidentFixedBandUnavailable";return;}
            string pulseStatus,calStatus;
            double[] pdBand=coincidentPulseBand.Apply(pulse,fs,coincidentBandSettings,out pulseStatus);
            double[] calBand=coincidentCalibrationBand.Apply(sixthReferenceWave,calRate,coincidentBandSettings,out calStatus);
            if(pulseStatus.StartsWith("bypass:",StringComparison.Ordinal)||calStatus.StartsWith("bypass:",StringComparison.Ordinal))
            {confidenceStatusCode="CoincidentFixedBandUnavailable";return;}
            var pd=CoincidentDescriptor(pulse,pdBand,incident,fs,sigma,travel*1e-9);
            double calNoise=Math.Max(sixthReference.NoiseSigma,sixthReference.IncidentAmplitude*1e-12);
            var near=CoincidentDescriptor(sixthReferenceWave,calBand,sixthReference.IncidentPeak,calRate,calNoise,travel*1e-9);
            var far=CoincidentDescriptor(sixthReferenceWave,calBand,sixthReference.ReflectionPeak,calRate,calNoise,travel*1e-9);
            double nearRho=A7WaveformMath.Similarity(pd,near),farRho=A7WaveformMath.Similarity(pd,far),
                contrast=A7WaveformMath.Similarity(near,far);
            double widthMatch=pd.Valid&&far.Valid?Math.Min(pd.EnergyWidthNs/far.EnergyWidthNs,far.EnergyWidthNs/pd.EnergyWidthNs):double.NaN;
            coincidentIndependentCount=CountIndependentCoincidentHistory();
            var result=CoincidentConfidenceMath.Evaluate(true,nearRho,farRho,contrast,widthMatch,coincidentIndependentCount);
            confidence=result.Score;confidenceStatusCode="CoincidentLocationPending";
            confidenceDetails=string.Format(CultureInfo.InvariantCulture,
                "math={0};reason={1};independentSameLocationCount={2};nearTemplateSimilarity={3:R};farTemplateSimilarity={4:R};calTemplateSimilarity={5:R};widthSimilarity={6:R};farEndSupportIndex={7:R};farEndSupportAvailable={8};scoreIsProbability=false;selfCorrelationIsNotReflectionEvidence=true;autoAccept=false;confidenceUnavailable=true",
                CoincidentConfidenceMath.Version,result.Reason,coincidentIndependentCount,nearRho,farRho,contrast,widthMatch,result.FarEndSupport,result.FarEndSupportAvailable);
        }

        // Matrix load and restore must not promote an old support index to a
        // general confidence, or invent self correlation on a constant trace.
        void NormalizeLoadedCoincidentConfidence()
        {
            if(!reflectionSelected)return;
            if(reflection<incident)
            {
                confidence=double.NaN;correlation=double.NaN;coincidentIndependentCount=0;
                confidenceStatusCode="ReflectionBeforeIncidentReviewOnly";
                confidenceDetails="confidenceUnavailable=true;negativeDelayIsNotIndependentReflectionEvidence=true;autoAccept=false;sourceMatrixPreserved=true";
                double measuredA,measuredB,measuredRho;
                if(TryConfidenceWindows(out measuredA,out measuredB,out measuredRho))correlation=measuredRho*100;
                return;
            }
            if(reflection!=incident)return;
            confidence=double.NaN;correlation=double.NaN;coincidentIndependentCount=0;
            confidenceStatusCode="CoincidentLocationPending";
            confidenceDetails="confidenceUnavailable=true;selfCorrelationIsNotReflectionEvidence=true;autoAccept=false;sourceMatrixPreserved=true";
            double a,b,rho;
            if(TryConfidenceWindows(out a,out b,out rho))correlation=100;
        }

        static A7PulseDescriptor CoincidentDescriptor(double[] raw,double[] band,int marker,double fs,double sigma,double roundTripSeconds)
        {
            var missing=new A7PulseDescriptor();
            if(raw==null||band==null||raw.Length!=band.Length||marker<0||marker>=raw.Length||
                !IsFinite(fs)||fs<=0||!IsFinite(sigma)||sigma<=0||!IsFinite(roundTripSeconds)||roundTripSeconds<=0)return missing;
            // Full supplied-crop filtering precedes bounded raw support discovery. Never
            // filter a cropped packet or widen a support to improve a correlation.
            int reach=(int)Math.Min(4096,Math.Max(24,roundTripSeconds*fs/3));
            int lo=Math.Max(0,marker-reach),hi=Math.Min(raw.Length-1,marker+reach);
            double[] ordered=(double[])raw.Clone();
            foreach(double v in ordered)if(!IsFinite(v))return missing;
            Array.Sort(ordered);double baseline=ordered[ordered.Length/2];
            int peak=marker,radius=Math.Max(2,Math.Min(12,reach/8));double amplitude=0;
            for(int i=Math.Max(lo,marker-radius);i<=Math.Min(hi,marker+radius);i++)
            {double a=Math.Abs(raw[i]-baseline);if(a>amplitude){amplitude=a;peak=i;}}
            if(amplitude<6*sigma)return missing;
            double quietThreshold=Math.Max(3*sigma,.02*amplitude);int left=-1,right=-1,quiet=0;
            for(int i=peak-1;i>=lo;i--)
            {quiet=Math.Abs(raw[i]-baseline)<=quietThreshold?quiet+1:0;if(quiet>=16){left=i;break;}}
            quiet=0;
            for(int i=peak+1;i<=hi;i++)
            {quiet=Math.Abs(raw[i]-baseline)<=quietThreshold?quiet+1:0;if(quiet>=16){right=i;break;}}
            if(left<=0||right>=raw.Length-1||right<=left)return missing;
            int bandPeak=left;double bandAmplitude=0;
            for(int i=left;i<=right;i++)
            {double a=Math.Abs(band[i]);if(a>bandAmplitude){bandAmplitude=a;bandPeak=i;}}
            // A high-pass signal is zero centered; do not subtract the raw offset.
            return A7WaveformMath.Measure(band,left,right,bandPeak,0,sigma,fs);
        }

        int CountIndependentCoincidentHistory()
        {
            if(!IsUsableMatrix(positioningMatrix,36)||originalParameterRow.Length<36)return 0;
            double phase=originalParameterRow[0],record=originalParameterRow[1],length=originalParameterRow[5];
            if(!IsFinite(phase)||!IsFinite(record)||!IsFinite(length)||length<=0)return 0;
            double velocity=originalParameterRow[6],rate=originalParameterRow[7];
            if(!IsFinite(velocity)||velocity<=0||!IsFinite(rate)||rate<=0)return 0;
            double tolerance=cableLengthM*acceptedPositionErrorPercent/100;
            if(!IsFinite(tolerance)||tolerance<0)return 0;
            var events=new List<double[]>();
            for(int r=0;r<positioningMatrix.GetLength(0);r++)
            {
                if(r==currentParameterRowIndex||positioningMatrix[r,35]!=1||positioningMatrix[r,0]!=phase||
                    !IsFinite(positioningMatrix[r,5])||Math.Abs(positioningMatrix[r,5]-length)>1e-6||
                    !IsFinite(positioningMatrix[r,6])||Math.Abs(positioningMatrix[r,6]-velocity)>Math.Max(1e-9,Math.Abs(velocity)*1e-9)||
                    !IsFinite(positioningMatrix[r,7])||Math.Abs(positioningMatrix[r,7]-rate)>rate*1e-9||
                    !IsFinite(positioningMatrix[r,13])||
                    !IsFinite(positioningMatrix[r,12])||positioningMatrix[r,12]<=0)continue;
                double id=positioningMatrix[r,1],index=positioningMatrix[r,10];
                if(!IsFinite(id)||!IsFinite(index)||id<0||index<0||id!=Math.Truncate(id)||index!=Math.Truncate(index))continue;
                double start=positioningMatrix[r,31],end=positioningMatrix[r,32];
                if(!IsFinite(start)||!IsFinite(end)||start<0||end<start||index<start||index>end)continue;
                // Exclude duplicates of the current source event, also after a
                // cursor edit. Overlapping crops of one record are not new trials.
                if(id==record&&(index==originalParameterRow[10]||
                    IsFinite(positioningMatrix[r,31])&&IsFinite(positioningMatrix[r,32])&&
                    positioningMatrix[r,31]<=originalParameterRow[32]&&positioningMatrix[r,32]>=originalParameterRow[31]))continue;
                events.Add(new[]{id,start,end,positioningMatrix[r,13]});
            }
            // A historical event saved with peak vs first-arrival or a one-sample
            // cursor edit remains ONE event. Conservatively merge intersecting
            // source crops; inconsistent locations cannot support either claim.
            events.Sort((a,b)=>{int order=a[0].CompareTo(b[0]);return order!=0?order:a[1].CompareTo(b[1]);});
            int count=0;
            for(int i=0;i<events.Count;)
            {
                double id=events[i][0],end=events[i][2],minimum=events[i][3],maximum=minimum;int j=i+1;
                while(j<events.Count&&events[j][0]==id&&events[j][1]<=end)
                {end=Math.Max(end,events[j][2]);minimum=Math.Min(minimum,events[j][3]);maximum=Math.Max(maximum,events[j][3]);j++;}
                if(maximum-minimum<=tolerance&&Math.Abs(minimum-currentLocationM)<=tolerance&&Math.Abs(maximum-currentLocationM)<=tolerance)count++;
                i=j;
            }
            return count;
        }
    }
}
