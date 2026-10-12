using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4Context
    {
        internal double[] Raw,Signal,Joints,Velocities;
        internal A4MeasurementSignal MeasurementSignal;
        internal double Center,RawCenter,Sigma,Threshold,Fs,Length,Velocity,RoundTripExact,RawDiscoveryThreshold;
        internal double ConnectingCableLengthM;
        internal double AttenuationTolerancePercent=15;
        internal double CalibrationToleranceAdaptationLimitPercent=HVX.SignalEvidence.CalibrationQualityMath.DefaultAdaptationLimitPercent;
        internal int RoundTrip,Suppression,Suppressed,OverlapCount,CalibrationPolarity;
        internal int CalibrationNoiseRejectedBands;
        internal int EffectiveSuppression;
        internal int AnalysisLimit=100;
        internal int Algorithm=4;
        internal double IncidentThreshold;
        internal bool[] AnalysisPhaseQuadrants=new[]{true,true,true,true};
        internal double AnalysisInitialPhaseDegrees,AnalysisFrequencyHz;
        internal Xiongxin.OWTS.Data.LocationCycleReference CycleReference;
#if HYBRID_CORE
        internal Dictionary<double,double> HybridPhaseCache;
        private Xiongxin.OWTS.Data.HybridPhaseCore.CycleSession hybridCycleSession;
        internal Xiongxin.OWTS.Data.HybridPhaseCore.CycleSession HybridCycleSession { get { return hybridCycleSession; } }
        internal void BindHybridCycle(Xiongxin.OWTS.Data.LocationCycleReference reference,double sampleRate)
        {
            // Acquire first: invalid new input cannot partially replace a live snapshot.
            var next=reference==null?null:reference.AcquireHybridPhaseSession();
            if(next!=null&&(Double.IsNaN(sampleRate)||Double.IsInfinity(sampleRate)||sampleRate<=0||
                Math.Abs(next.SampleRateHz-sampleRate)>1e-9*sampleRate))
                throw new System.IO.InvalidDataException("The immutable LOC phase session does not match the analysis sample rate.");
            hybridCycleSession=next;HybridPhaseCache=null;
        }
        internal void ShareHybridCycle(A4Context source)
        {hybridCycleSession=source.hybridCycleSession;HybridPhaseCache=null;}
#endif
        internal bool AnalysisLimitReached;
        internal double[] EffectiveCutoffs;
        internal string SuppressionResolution="not_resolved";
        internal List<int> AnalysisVisitIds=new List<int>();
        internal Dictionary<int,string> AnalysisExclusions=new Dictionary<int,string>();
        internal Dictionary<int,string> IncompletePdmapCrops=new Dictionary<int,string>();
        internal bool Filtered,FirstArrival,FirstArrivalPhysicalTiming,Attenuation,Width,IndependentTiming,Index4ExtendedGuardSearch,SixthHybridGuardSearch;
        // Opt-in only for a new, unaccepted review-recovery context. Never set on primary analysis.
        internal bool ReviewQuietGuardRecovery;
        internal ParsedCalibration Calibration;
        internal A4BandReference Bands;
        internal A4LowBandReference LowBand;
        internal double[] LowBandSignal;
        internal A4AttenuationScoringReference AttenuationScoringReference;
        internal double[] AttenuationScoringSignal;
        internal double AttenuationScoringRawSigma;
        internal List<A4Pulse> AttenuationScoringPackets;
        // Shape correlation owns an independent measurement context/cache. It never reuses or
        // changes the attenuation measurements attached to the caller's raw packets.
        internal A4UnifiedCorrelationState UnifiedCorrelation;
        internal List<int> RawSeedOrder;
        internal List<A4WindowTrace> WindowTraces;
        internal List<string> BoundaryReviewTraces=new List<string>();
        internal bool WindowBudgetExceeded;
        internal HashSet<int> UnreliableInterveningIds=new HashSet<int>();
        internal List<A4RejectedPair> RejectedPairs=new List<A4RejectedPair>();
        internal int RejectedPairCount,RejectedCorrelationCount,RejectedPhysicalConfidenceCount;
        internal List<A4ParallelEvidence> ParallelAlternatives;
        internal HashSet<long> ParallelPairKeys;
        internal List<A4Pair> CandidatePool=new List<A4Pair>();
        internal List<A4MatchComponent> MatchComponents=new List<A4MatchComponent>();
        internal bool CandidatePoolLimitExceeded;
        internal int CandidateEvaluations,RoleReviewSteps;
        internal int CandidateEvaluationLimit=A4MaximumCandidateEvaluations,RoleReviewLimit=A4MaximumRoleReviewSteps;
        internal bool CandidateEvaluationLimitExceeded,RoleReviewLimitExceeded;
        internal List<A4CausalEcho> CausalEchoes=new List<A4CausalEcho>();
        internal Dictionary<int,string> Index4DisplayDiagnostics=new Dictionary<int,string>();
        internal HashSet<int> ConditionalEchoPulseIds=new HashSet<int>(),CausalAmbiguousPulseIds=new HashSet<int>(),CausalEchoProtectedPulseIds=new HashSet<int>();
    }
    private sealed class A4Pulse
    {
        internal int Id,Start,End,Peak,RawPeak,Period,Owner=-1;
        internal int RawAmplitudeIndex=-1,AnalysisOrigin;
        internal double[] AnalysisSamples;
        internal double Amplitude,LocalAmplitude,Energy,EquivalentSamples,BandwidthHz,HighFraction,Snr,Baseline,NoiseSigma;
        internal bool MappingValid,Clipped,UnresolvedOverlap,BaselineUnstable,TriggeredFromRaw;
        internal int RefinedLeftGuardStart=-1,RefinedLeftGuardEnd=-1,RefinedRightGuardStart=-1,RefinedRightGuardEnd=-1;
        internal bool HasRefinedGuardProof;
        internal bool BoundaryRefined;
        internal double[] BandEnergy,BandNoise;
        internal double[] BandRawEnergy;
        internal double[][] BackgroundGuards,SpectralGuards;
        internal List<double[]> SpectralBefore,SpectralAfter;
        internal A4Pulse LowBandPulse;
        internal A4AttenuationMeasurement AttenuationMeasurement;
    }
    private sealed class A4Pair
    {
        internal A4Pulse Incident,Reflection;
        internal double Correlation,WaveCorrelation,EnvelopeCorrelation,WidthScore,SpectrumScore,AttenuationScore,
            AmplitudeRatio,Confidence,Position,Ambiguity;
        internal bool Ambiguous,Blind,Amplified,ParallelAmbiguous,BoundaryAmbiguous;
        internal int Suppressed,CandidateCount;
        internal int ValidBands;
        internal double BandGainScore=1,BandShapeScore=1;
        internal double BandExcessDb,BandConsistencyFactor=1;
        internal int UnstableNoiseBands;
        internal A4LowBandPairEvidence LowBand;
        internal A4UnifiedCorrelationEvidence UnifiedCorrelation;
        internal double SignedCorrelationMinimum,AttenuationWeight;
        internal A4AttenuationEvidence CalibrationDeviation;
        internal double ScoreBeforeCalibration,CalibrationPenaltyFactor=1;
        internal int EdgeId=-1,ComponentId=-1,ForwardBest=-1,ForwardRunnerUp=-1,ReverseBest=-1,ReverseRunnerUp=-1;
        internal double StrategyWeight=double.NaN;
        internal int OutputIncident=-1,OutputReflection=-1;
        internal string OutputTimingReason="not_evaluated";
        internal int ForwardRank,ReverseRank;
        internal double ForwardMargin,ReverseMargin,GlobalEvidenceWeight;
        internal bool MutualBest,Reviewed;
        internal string State="pending",ReviewReason="not_reviewed";
        internal int[] InterveningIds=new int[0];
    }
    private static double[] A4ZeroPhase(double[] input,double fs,double[] cutoffs)
    {
        if(cutoffs==null||cutoffs.Length!=2||!Finite(cutoffs[0])||!Finite(cutoffs[1]))
            throw new ArgumentException("linearFilterCutoffsHz must contain [lowHz, highHz].");
        double low=cutoffs[0],high=cutoffs[1],nyquist=fs/2;
        if(low<0||high<=low||high>nyquist)throw new ArgumentException("Require 0 <= lowHz < highHz <= sampleRate/2.");
        return A4FrequencyFilter(input,fs,low,high,false);
    }
    private static void A4Fft(double[] re,double[] im)
    {
        int n=re.Length;
        for(int i=1,j=0;i<n;i++)
        {
            int bit=n>>1;for(; (j&bit)!=0;bit>>=1)j^=bit;j^=bit;
            if(i<j){double t=re[i];re[i]=re[j];re[j]=t;t=im[i];im[i]=im[j];im[j]=t;}
        }
        for(int size=2;size<=n;size<<=1)
        {
            double theta=-2*Math.PI/size,wrStep=Math.Cos(theta),wiStep=Math.Sin(theta);
            for(int start=0;start<n;start+=size)
            {
                double wr=1,wi=0;
                for(int j=0;j<size/2;j++)
                {
                    int a=start+j,b=a+size/2;double tr=wr*re[b]-wi*im[b],ti=wr*im[b]+wi*re[b];
                    re[b]=re[a]-tr;im[b]=im[a]-ti;re[a]+=tr;im[a]+=ti;
                    double next=wr*wrStep-wi*wiStep;wi=wr*wiStep+wi*wrStep;wr=next;
                }
            }
            if(size==n)break;
        }
    }
    private static List<A4Pulse> A4Extract(A4Context c)
    {
        List<A4Pulse> result=new List<A4Pulse>();double[] x=c.Signal;
        int scan=0,maximumPulseSpan=Math.Max(24,c.RoundTrip*2);
        while(scan<x.Length)
        {
            // F239/F275: retain raw absolute triggers despite filter peak attenuation.
            // Discovery is not confirmation: full waveform/SNR/ownership checks remain mandatory.
            bool filteredTrigger=Math.Abs(x[scan]-c.Center)>=c.Threshold;
            bool rawTrigger=c.RawDiscoveryThreshold>0&&A4RawCharge(c,scan)>=c.RawDiscoveryThreshold;
            if(!filteredTrigger&&!rawTrigger){scan++;continue;}
            int trigger=scan;
            int initialEnd=Math.Min(x.Length-1,trigger+Math.Max(12,Math.Min(96,c.RoundTrip/6)));
            int initialPeak=AbsoluteMaximumIndex(x,trigger,initialEnd,c.Center);
            // Do not pull a later isolated pulse into the first pulse's initial peak estimate.
            int quiet=0;
            for(int k=trigger;k<=initialPeak;k++)
            {
                if(Math.Abs(x[k]-c.Center)<2.5*c.Sigma)quiet++;else quiet=0;
                if(quiet>=6){initialPeak=AbsoluteMaximumIndex(x,trigger,k-quiet,c.Center);break;}
            }
            double amplitude=Math.Abs(x[initialPeak]-c.Center);
            int period=A4Period(x,trigger,Math.Min(x.Length-1,initialPeak+Math.Max(12,c.RoundTrip/8)),c.Center,Math.Max(c.Sigma*2,amplitude*.05));
            int hold=Math.Max(6,Math.Min(64,(int)Math.Ceiling(period*.8)));
            double quietLevel=2.5*c.Sigma;
            // A completed packet already established a quiet gap. A later packet's longer
            // backtracking hold must not cross that gap and transitively merge unrelated noise.
            int previousEnd=result.Count>0?result[result.Count-1].End:-1;
            int backtrackLowerBound=Math.Max(previousEnd+1,Math.Max(0,trigger-Math.Max(hold*3,c.RoundTrip/5)));
            // A short inter-packet gap may not fit this packet's longer quiet hold.
            // Even then the onset scan must include the already bounded leading edge;
            // starting at trigger would misclassify that edge as the previous guard.
            int start=backtrackLowerBound;
            // A quiet PRE-trigger block must end before the trigger. Allowing it
            // to include a weak trigger could move Start past it (or past EOF).
            for(int k=trigger-hold;k>=backtrackLowerBound;k--)
            {
                if(A4QuietBlock(x,k,k+hold-1,c.Center,c.Sigma)){start=k+hold;break;}start=k;
            }
            // Earliest significant sample, never search after the global absolute peak.
            int peak=trigger,lastActive=trigger,end=Math.Min(x.Length-1,trigger+maximumPulseSpan);quiet=0;
            for(int k=trigger;k<=end;k++)
            {
                double value=Math.Abs(x[k]-c.Center);
                if(value>amplitude){amplitude=value;peak=k;}
                if(value>=quietLevel){lastActive=k;quiet=0;}else quiet++;
                if(k-trigger+1>=hold&&A4QuietBlock(x,k-hold+1,k,c.Center,c.Sigma)){end=lastActive;break;}
            }
            peak=AbsoluteMaximumIndex(x,start,end,c.Center);
            amplitude=Math.Abs(x[peak]-c.Center);
            double onsetLevel=Math.Max(c.Sigma*3,amplitude*.025);
            int onset=start;while(onset<peak&&Math.Abs(x[onset]-c.Center)<onsetLevel)onset++;
            start=onset;
            if(end<start)end=start;
            A4Pulse p=new A4Pulse {Id=result.Count,Start=start,End=end,Peak=peak,
                Amplitude=amplitude,Period=period,Snr=amplitude/c.Sigma,Baseline=c.Center,NoiseSigma=c.Sigma,Clipped=start==0||end>=x.Length-1,TriggeredFromRaw=rawTrigger&&!filteredTrigger};
            int radius=c.Filtered?2:0;
            p.RawPeak=AbsoluteMaximumIndex(A4RawAnalysis(c),Math.Max(start,peak-radius),Math.Min(end,peak+radius),c.RawCenter);
            p.MappingValid=Math.Abs(p.RawPeak-peak)<=radius && A4RawCharge(c,p.RawPeak)>=c.Sigma*3;
            // No fallback outside the strict phase compensation window.
            A4Measure(c,p);
            // Keep a complete ringing packet up to one full round trip. A half-round-trip
            // duration alone is not evidence of overlap; actual pair support overlap remains a hard veto.
            p.UnresolvedOverlap=end-start>c.RoundTrip;
            if(p.UnresolvedOverlap)c.OverlapCount++;
            if(result.Count>0&&p.Start<=result[result.Count-1].End)
            {
                // Backtracking can revisit a still-ringing envelope. Canonicalize its entire support
                // before candidate pairing so one physical pulse never acquires two independent IDs.
                A4Pulse previous=result[result.Count-1];previous.End=Math.Max(previous.End,p.End);
                previous.Peak=AbsoluteMaximumIndex(x,previous.Start,previous.End,c.Center);
                previous.Amplitude=Math.Abs(x[previous.Peak]-c.Center);previous.Snr=previous.Amplitude/c.Sigma;
                previous.RawPeak=AbsoluteMaximumIndex(A4RawAnalysis(c),Math.Max(previous.Start,previous.Peak-radius),Math.Min(previous.End,previous.Peak+radius),c.RawCenter);
                previous.MappingValid=Math.Abs(previous.RawPeak-previous.Peak)<=radius&&A4RawCharge(c,previous.RawPeak)>=c.Sigma*3;
                previous.TriggeredFromRaw|=p.TriggeredFromRaw;
                previous.UnresolvedOverlap=previous.End-previous.Start>c.RoundTrip;A4Measure(c,previous);
            }
            else result.Add(p);
            scan=Math.Max(trigger+1,end+hold);
        }
        // Guard regions explicitly exclude neighbouring detected supports. Local background cannot
        // inflate waveform correlation or make a low-contrast pedestal a confident physical pulse.
        for(int index=0;index<result.Count;index++)
        {
            A4Pulse p=result[index];int guard=Math.Max(24,Math.Min(128,c.RoundTrip/5));
            int left=Math.Max(0,Math.Max(p.Start-guard,index>0?result[index-1].End+1:0));
            int right=Math.Min(x.Length-1,Math.Min(p.End+guard,index+1<result.Count?result[index+1].Start-1:x.Length-1));
            double[] before=left<p.Start?A4Slice(x,left,p.Start-1):new double[0];
            double[] after=right>p.End?A4Slice(x,p.End+1,right):new double[0];
            double[] background=before.Concat(after).ToArray();
            p.BackgroundGuards=new[]{before,after};
            p.Baseline=background.Length>=8?Median(background):c.Center;
            p.NoiseSigma=background.Length>=8?Math.Max(c.Sigma,1.4826*Median(background.Select(v=>Math.Abs(v-p.Baseline)).ToArray())):c.Sigma;
            p.Snr=Math.Abs(x[p.Peak]-p.Baseline)/Math.Max(1e-15,p.NoiseSigma);
            p.BaselineUnstable=before.Length>=8&&after.Length>=8&&Math.Abs(Median(before)-Median(after))>3*p.NoiseSigma;
            A4Measure(c,p);
            int spectralLeft=Math.Max(0,Math.Max(p.Start-256,index>0?result[index-1].End+1:0));
            int spectralRight=Math.Min(x.Length-1,Math.Min(p.End+256,index+1<result.Count?result[index+1].Start-1:x.Length-1));
            p.SpectralGuards=new[]{spectralLeft<p.Start-16?A4Slice(x,spectralLeft,p.Start-17):new double[0],
                spectralRight>p.End+16?A4Slice(x,p.End+17,spectralRight):new double[0]};
            if(c.Bands!=null)
            {
                A4MeasureBands(c.Signal,c.Fs,p,c.Bands.EdgesHz);
                p.SpectralBefore=A4NoiseBandBlocks(new[]{p.SpectralGuards[0]},c.Fs,c.Bands.EdgesHz);
                p.SpectralAfter=A4NoiseBandBlocks(new[]{p.SpectralGuards[1]},c.Fs,c.Bands.EdgesHz);
            }
        }
        return result;
    }
    private static int A4Period(double[] x,int begin,int end,double center,double level)
    {
        // A bipolar packet may contain only ONE sign transition within this interval.
        // Returning a fixed eight samples then truncates its slower opposite lobe/tail.
        // Measure the dominant lobe separation before falling back to crossing gaps.
        int main=AbsoluteMaximumIndex(x,begin,end,center),opposite=-1;
        double mainValue=x[main]-center,oppositeAmplitude=0;
        for(int i=main+1;i<=end;i++)
        {
            double value=x[i]-center;
            if(value*mainValue<0&&Math.Abs(value)>oppositeAmplitude){opposite=i;oppositeAmplitude=Math.Abs(value);}
        }
        if(opposite>=0&&oppositeAmplitude>=Math.Max(level,Math.Abs(mainValue)*.10))
            return Math.Max(4,Math.Min(80,2*(opposite-main)));
        List<int> crossings=new List<int>();int previousSign=0;
        for(int i=begin;i<=end;i++)
        {
            double y=x[i]-center;if(Math.Abs(y)<level)continue;
            int sign=y>=0?1:-1;if(previousSign!=0&&sign!=previousSign)crossings.Add(i);previousSign=sign;
        }
        if(crossings.Count<2)return 8;
        List<double> gaps=new List<double>();for(int i=1;i<crossings.Count;i++)gaps.Add(crossings[i]-crossings[i-1]);
        return Math.Max(4,Math.Min(80,(int)Math.Round(2*Median(gaps.ToArray()))));
    }
    private static bool A4QuietBlock(double[] x,int begin,int end,double center,double sigma)
    {
        if(begin<0||end>=x.Length||end<begin)return false;
        int count=end-begin+1,active=0;double energy=0,level=2.5*sigma,localMean=0;
        for(int i=begin;i<=end;i++)localMean+=x[i]-center;
        localMean/=count;
        // Bound local background drift; never classify a high flat pulse plateau
        // as quiet merely because its variance is small. This changes boundaries
        // only, not the original waveform or any output charge measurement.
        if(Math.Abs(localMean)>4*sigma)return false;
        for(int i=begin;i<=end;i++)
        {double value=x[i]-center-localMean;energy+=value*value;if(Math.Abs(value)>=level)active++;}
        // A few background excursions cannot indefinitely bridge two packets.
        // The RMS guard still prevents one large event from being called quiet.
        return active<=count/10&&energy<=count*level*level;
    }
    private static void A4Measure(A4Context c,A4Pulse p)
    {
        int len=p.End-p.Start+1;double energy=0,derivative=0,area=0;
        for(int i=p.Start;i<=p.End;i++)
        {
            double y=A4Sample(c.Signal,p,i)-p.Baseline;energy+=y*y;area+=Math.Abs(y);
            if(i>p.Start){double d=A4Sample(c.Signal,p,i)-A4Sample(c.Signal,p,i-1);derivative+=d*d;}
        }
        p.LocalAmplitude=Math.Abs(A4Sample(c.Signal,p,p.Peak)-p.Baseline);
        p.Energy=energy;p.EquivalentSamples=energy/Math.Max(1e-30,p.LocalAmplitude*p.LocalAmplitude);
        p.BandwidthHz=c.Fs/(2*Math.PI)*Math.Sqrt(derivative/Math.Max(1e-30,energy));
        p.HighFraction=derivative/Math.Max(1e-30,4*energy);
    }
    private static void A4Correlate(A4Context c,A4Pulse a,A4Pulse b,out double wave,out double envelope)
    {
        if(!c.IndependentTiming){A4CorrelateAnchors(c,a,b,c.FirstArrival?a.Start:a.Peak,c.FirstArrival?b.Start:b.Peak,out wave,out envelope);return;}
        // One peak alignment for the fixed physical pair, with only +/-2 samples
        // of local analysis refinement. A precursor onset cannot borrow a later
        // main wave's score and then supply a different propagation delay.
        int homologous=b.Peak;double sign=Math.Sign(A4Sample(c.Signal,a,a.Peak)-a.Baseline);
        if(sign*(A4Sample(c.Signal,b,homologous)-b.Baseline)<0)
        {
            double maximum=double.NegativeInfinity;
            for(int i=b.Start;i<=b.End;i++)
            {double value=sign*(A4Sample(c.Signal,b,i)-b.Baseline);if(value>maximum){maximum=value;homologous=i;}}
        }
        A4CorrelateAnchors(c,a,b,a.Peak,homologous,out wave,out envelope);
    }
    private static void A4CorrelateAnchors(A4Context c,A4Pulse a,A4Pulse b,int anchorA,int anchorB,out double wave,out double envelope)
    {
        double bestEnv=0;
        int begin=Math.Min(a.Start-anchorA,b.Start-anchorB)-2,end=Math.Max(a.End-anchorA,b.End-anchorB)+2;
        // Keep BOTH complete supports with zero padding; peak mode must align the selected
        // peaks, not unrelated pre-trigger noise. This never changes output timing indices.
        // This does not stretch time or align a positive lobe to an unrelated negative lobe.
        // A single global propagation polarity is allowed; no per-lobe sign changes.
        wave=0;double bestCombined=-1;
        for(int shift=-2;shift<=2;shift++)
        {
            double xy=0,xx=0,yy=0,e=0;
            for(int k=begin;k<=end;k++)
            {
                int i=anchorA+k,j=anchorB+k+shift;
                double u=i>=a.Start&&i<=a.End?A4Sample(c.Signal,a,i)-a.Baseline:0,v=j>=b.Start&&j<=b.End?A4Sample(c.Signal,b,j)-b.Baseline:0;
                xy+=u*v;xx+=u*u;yy+=v*v;e+=Math.Abs(u*v);
            }
            double n=Math.Sqrt(xx*yy);if(n<=1e-30)continue;
            double s=xy/n,ev=e/n,combined=.78*(c.IndependentTiming?s:Math.Abs(s))+.22*ev;
            if(combined>bestCombined){bestCombined=combined;wave=s;bestEnv=ev;}
        }
        envelope=bestEnv;
    }
}
