using System;

namespace Xiongxin.Calibration
{
    internal sealed class TdrCorrelationResult
    {
        internal bool Valid;
        internal bool IncidentWindowTruncated;
        internal double DistanceM, Correlation;
        internal int IncidentIndex, ReflectionIndex;
        internal string Message;
    }
    internal static class TdrCorrelation
    {
        // The project length provides a search prior; it never overwrites the
        // measured incident/reflection separation used for the reported length.
        internal static TdrCorrelationResult Match(double[] volts,int nominalIncident,double sampleRate,double lengthM,double velocityMPerUs, double tolerance = .5)
        { return MatchCore(volts,nominalIncident,sampleRate,lengthM,velocityMPerUs,tolerance,false); }

        internal static TdrCorrelationResult MatchTerminal(double[] volts,int nominalIncident,double sampleRate,double lengthM,double velocityMPerUs,double tolerance)
        { return MatchCore(volts,nominalIncident,sampleRate,lengthM,velocityMPerUs,tolerance,true); }

        private static TdrCorrelationResult MatchCore(double[] volts,int nominalIncident,double sampleRate,double lengthM,double velocityMPerUs,double tolerance,bool preferTerminal)
        {
            var r=new TdrCorrelationResult {IncidentIndex=-1,ReflectionIndex=-1,Message="没有可靠的入射波与反射波配对。"};
            if(volts==null || volts.Length<128 || !Finite(sampleRate)||sampleRate<=0 || !Finite(lengthM)||lengthM<=0 || !Finite(velocityMPerUs)||velocityMPerUs<=0)
            {r.Message="波形、采样率、长度或波速无效，无法进行反射测量。";return r;}
            if(nominalIncident<0||nominalIncident>=volts.Length){r.Message="名义入射位置不在采集记录内。";return r;}
            if(!Finite(tolerance)||tolerance<=0 || tolerance>.5)throw new ArgumentOutOfRangeException("tolerance");
            foreach(double value in volts)if(!Finite(value)){r.Message="波形含 NaN 或无穷值，无法进行反射测量。";return r;}
            int begin=Math.Max(0,nominalIncident-64),end=(int)Math.Min(volts.Length-1L,(long)nominalIncident+64);
            int incident=begin;for(int i=begin+1;i<=end;i++)if(Math.Abs(volts[i])>Math.Abs(volts[incident]))incident=i;
            r.IncidentIndex=incident;
            double gap=2*lengthM/velocityMPerUs*sampleRate/1e6;
            if(!Finite(gap)||gap<16 || gap>Int32.MaxValue/2){r.Message="当前长度与时基不足以分辨首个反射。";return r;}
            int radius=Math.Max(4,Math.Min(64,(int)(gap/12)));
            // Require an entire correlation packet and at least eight preceding
            // baseline samples. Filtering cannot recreate missing edge evidence.
            if(incident-radius<8||incident+radius>=volts.Length)
            {r.IncidentWindowTruncated=true;r.Message="入射波过于靠近记录边界，缺少完整包络或前置基线，无法可靠测量；请增加预触发或重新采集。";return r;}
            double firstPosition=Math.Max((double)incident+radius+1,Math.Floor(incident+gap*(1-tolerance)));
            double lastPosition=Math.Min(volts.Length-radius-1,Math.Ceiling(incident+gap*(1+tolerance)));
            if(lastPosition<=firstPosition){r.Message="采集窗口未覆盖预计反射区间的 50%–150%。";return r;}
            int first=(int)firstPosition,last=(int)lastPosition;
            double baseline=0,noise=0;int baseCount=Math.Min(200,incident-radius);
            for(int i=0;i<baseCount;i++)baseline+=volts[i]/baseCount;
            for(int i=0;i<baseCount;i++)noise+=(volts[i]-baseline)*(volts[i]-baseline)/baseCount;
            noise=Math.Sqrt(noise);
            double incidentMagnitude=Math.Abs(volts[incident]-baseline);
            if(!Finite(baseline)||!Finite(noise)||!Finite(incidentMagnitude)||incidentMagnitude<=Math.Max(1e-12,noise*4))
            {r.IncidentIndex=-1;r.Message="没有可区分于基线噪声的有效入射脉冲，无法可靠测量。";return r;}
            int best=-1;double bestCorr=0,bestScore=Double.NegativeInfinity;
            for(int candidate=first;candidate<=last;candidate++)
            {
                double value=Math.Abs(volts[candidate]-baseline);
                if(!Finite(value)||value<Math.Max(noise*4,incidentMagnitude*.01))continue;
                if(value<Math.Abs(volts[candidate-1]-baseline) || value<Math.Abs(volts[candidate+1]-baseline))continue;
                double xy=0,xx=0,yy=0;
                for(int j=-radius;j<=radius;j++)
                {
                    double a=volts[incident+j]-baseline,b=volts[candidate+j]-baseline;
                    xy+=a*b;xx+=a*a;yy+=b*b;
                }
                double corr=xx>0 && yy>0?xy/Math.Sqrt(xx*yy):0;
                if(!Finite(corr))continue;
                // In charge calibration the measured cable length is a prior,
                // not a distance result. A stronger interior reflection must not
                // always defeat a credible terminal packet. Still require real
                // correlation and >=10% amplitude; never fabricate a marker.
                if(Math.Abs(corr)<.6||(preferTerminal&&value<incidentMagnitude*.1))continue;
                // TDR also has a rough length/velocity prior. Rank only real,
                // correlated local extrema against it so an earlier ringing
                // lobe cannot win solely by a higher absolute correlation.
                // The prior neither creates a peak nor replaces measured time.
                double score=Math.Abs(corr)-2*Math.Abs(candidate-incident-gap)/gap;
                if(score>bestScore){best=candidate;bestCorr=corr;bestScore=score;}
            }
            if(best<0 || Math.Abs(bestCorr)<.6){r.Message="反射搜索区内无相关度足够的候选峰，请人工核对。";return r;}
            r.Valid=true;r.IncidentIndex=incident;r.ReflectionIndex=best;r.Correlation=bestCorr;
            r.DistanceM=(best-incident)/sampleRate*1e6*velocityMPerUs/2;
            r.Message="在预计反射区间 ±"+(tolerance*100).ToString("0")+"% 内"+
                (preferTerminal?"结合已知电缆长度和波形相关度选择终端反射，请核对首达位置。":"结合参考长度和波形相关度选取实测反射峰。");
            return r;
        }
        private static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }

    internal sealed class CalibrationBandwidthSearchResult
    {
        internal TdrCorrelationResult Reflection;
        internal HVX.SignalProcessing.ReflectionBandwidthResult Bandwidth;
    }
    internal static class CalibrationReflectionBandwidth
    {
        // Each trial is derived from the original high-passed signal, never a
        // cascade. A failed match is not converted to a synthetic reflection.
        internal static CalibrationBandwidthSearchResult Search(double[] samples,int nominalIncident,double sampleRate,double length,double velocity,double tolerance,bool capacitor,Func<bool> cancelled)
        { return SearchCore(samples,nominalIncident,sampleRate,length,velocity,tolerance,capacitor,cancelled,false); }

        internal static CalibrationBandwidthSearchResult SearchTerminal(double[] samples,int nominalIncident,double sampleRate,double length,double velocity,double tolerance,bool capacitor,Func<bool> cancelled)
        { return SearchCore(samples,nominalIncident,sampleRate,length,velocity,tolerance,capacitor,cancelled,true); }

        private static CalibrationBandwidthSearchResult SearchCore(double[] samples,int nominalIncident,double sampleRate,double length,double velocity,double tolerance,bool capacitor,Func<bool> cancelled,bool preferTerminal)
        {
            if(samples==null||samples.Length<128||!Finite(sampleRate)||sampleRate<=0)throw new ArgumentException("带宽评估需要有效采样率及完整脉冲窗口。");
            if(nominalIncident<0||nominalIncident>=samples.Length)throw new ArgumentOutOfRangeException("nominalIncident","名义入射位置不在采集记录内。");
            foreach(double value in samples)if(!Finite(value))throw new ArgumentException("波形含 NaN 或无穷值，不能进行自动带宽评估。");
            if(capacitor)return new CalibrationBandwidthSearchResult{Reflection=new TdrCorrelationResult{IncidentIndex=nominalIncident,ReflectionIndex=-1,Message="电容测试，不进行反射识别或测距。"}};
            if(sampleRate<2e6)throw new ArgumentException("反射带宽评估采样率须至少 2 MS/s，不能应用低于 1 MHz 的截止频率。");
            if(!Finite(length)||length<=0||!Finite(velocity)||velocity<=0||!Finite(tolerance)||tolerance<=0||tolerance>.5)throw new ArgumentException("自动带宽评估的长度、波速或反射容差无效。");
            if(cancelled!=null&&cancelled())throw new OperationCanceledException("带宽搜索已取消。");
            Func<double[],TdrCorrelationResult> match=values=>preferTerminal?
                TdrCorrelation.MatchTerminal(values,nominalIncident,sampleRate,length,velocity,tolerance):
                TdrCorrelation.Match(values,nominalIncident,sampleRate,length,velocity,tolerance);
            var reflection=match(samples);
            var originalReflection=reflection;
            var result=new HVX.SignalProcessing.ReflectionBandwidthResult{Waveform=(double[])samples.Clone(),TargetRatio=HVX.SignalProcessing.CalibrationDisplayBandwidth.TargetRatio,Stable=false,StabilityReason="UnverifiedRawPair"};
            if(reflection.Valid)result=HVX.SignalProcessing.CalibrationDisplayBandwidth.Assess(samples,result.Waveform,
                reflection.IncidentIndex,reflection.ReflectionIndex,reflection.IncidentIndex,reflection.ReflectionIndex,0);
            if(result.Sufficient)return new CalibrationBandwidthSearchResult{Reflection=reflection,Bandwidth=result};
            // Missing raw edge evidence cannot be recovered by mirror padding.
            if(originalReflection.IncidentWindowTruncated)return new CalibrationBandwidthSearchResult{Reflection=reflection,Bandwidth=result};
            double maximum=Math.Min(60,Math.Floor(sampleRate*.5/1e6));
            // No meaningful 3 MHz low-pass is possible at or below Nyquist.
            // Preserve the actual raw evidence rather than claim a filter ran.
            if(maximum<=3)return new CalibrationBandwidthSearchResult{Reflection=originalReflection,Bandwidth=result};
            // Calibration display only: independently derive each candidate.
            // Keep the highest stable passing cutoff, never search below 3 MHz.
            double[] candidates={60,50,40,30,20,15,10,7,5,3};
            double prior=Double.PositiveInfinity;
            double[] manualDisplay=null;
            foreach(double value in candidates)
            {
                double cutoff=Math.Min(maximum,value);if(cutoff>=prior)continue;prior=cutoff;
                if(cancelled!=null&&cancelled())throw new OperationCanceledException("带宽搜索已取消。");
                var filtered=HVX.SignalProcessing.PdLinearLowPass.Apply(samples,1/sampleRate,cutoff);
                if(cutoff==3)manualDisplay=filtered;
                var candidateReflection=match(filtered);if(!candidateReflection.Valid)continue;
                int rawIncident=originalReflection.Valid?originalReflection.IncidentIndex:candidateReflection.IncidentIndex;
                int rawReflection=originalReflection.Valid?originalReflection.ReflectionIndex:candidateReflection.ReflectionIndex;
                var candidate=HVX.SignalProcessing.CalibrationDisplayBandwidth.Assess(samples,filtered,rawIncident,rawReflection,
                    candidateReflection.IncidentIndex,candidateReflection.ReflectionIndex,cutoff);
                if(candidate.Stable!=true)continue;
                if(result.Stable!=true||candidate.Sufficient||candidate.Ratio>result.Ratio+1e-9)
                {result=candidate;reflection=candidateReflection;}
                if(result.Sufficient)break;
            }
            if(!result.Sufficient) {
                // Show the requested 3 MHz fallback for manual review only.
                // Assess it honestly against the original pair: it may broaden
                // a packet, so never manufacture Stable/Sufficient or timing.
                if(cancelled!=null&&cancelled())throw new OperationCanceledException("带宽搜索已取消。");
                if(manualDisplay==null)manualDisplay=HVX.SignalProcessing.PdLinearLowPass.Apply(samples,1/sampleRate,3);
                result=HVX.SignalProcessing.CalibrationDisplayBandwidth.Assess(samples,manualDisplay,
                    originalReflection.IncidentIndex,originalReflection.ReflectionIndex,
                    originalReflection.IncidentIndex,originalReflection.ReflectionIndex,3);
                result.DisplayFallback=true;
                result.Sufficient=false;
                reflection=originalReflection;
            }
            return new CalibrationBandwidthSearchResult{Reflection=reflection,Bandwidth=result};
        }
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
}
