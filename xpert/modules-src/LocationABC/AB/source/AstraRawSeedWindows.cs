using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4WindowTrace
    {
        internal int Seed,Start,End,RawIncidentStart,RawIncidentEnd,IncidentPeak;
        internal double RawAmplitude;
    }
    private static int A4ChargeIndex(A4Pulse p){return p.RawAmplitudeIndex>=0?p.RawAmplitudeIndex:p.RawPeak;}
    private static int A4DisplayExtensionLimit(double roundTripExact)
    {return (int)Math.Max(0,Math.Min(int.MaxValue,Math.Floor(1.5*roundTripExact)));}
    private static int A4DisplayExtensionPoints(int requested,int limit)
    {return Math.Max(0,Math.Min(limit,requested));}
    private static double A4Sample(double[] fallback,A4Pulse p,int index)
    {return p.AnalysisSamples!=null?p.AnalysisSamples[index-p.AnalysisOrigin]:fallback[index];}
    private static List<int> A4AbsoluteSeeds(double[] raw,double center,double threshold,int first,int last)
    {
        var seeds=new List<int>();
        for(int i=Math.Max(0,first);i<=Math.Min(raw.Length-1,last);i++)
        {
            double a=Math.Abs(raw[i]-center);
            if(a<threshold)continue;
            double before=i>0?Math.Abs(raw[i-1]-center):-1,after=i+1<raw.Length?Math.Abs(raw[i+1]-center):-1;
            if(a>before&&a>=after)seeds.Add(i);
        }
        return seeds.OrderByDescending(i=>Math.Abs(raw[i]-center)).ThenBy(i=>i).ToList();
    }
    private static A4Pulse A4SeededPacket(A4Context c,int peak,int chargePeak)
    {
        double[] x=c.Signal;double amp=Math.Abs(x[peak]-c.Center);
        int period=A4Period(x,peak,Math.Min(x.Length-1,peak+Math.Max(12,c.RoundTrip/8)),c.Center,Math.Max(c.Sigma*2,amp*.05));
        int hold=Math.Max(6,Math.Min(64,(int)Math.Ceiling(period*.8)));
        int start=Math.Max(0,peak-c.RoundTrip),end=Math.Min(x.Length-1,peak+2*c.RoundTrip);
        for(int k=peak-hold;k>=start;k--)
            if(A4QuietBlock(x,k,k+hold-1,c.Center,c.Sigma)){start=k+hold;break;}
        int lastActive=peak;
        for(int k=peak;k<=end;k++)
        {
            if(Math.Abs(x[k]-c.Center)>=2.5*c.Sigma)lastActive=k;
            if(k-peak+1>=hold&&A4QuietBlock(x,k-hold+1,k,c.Center,c.Sigma)){end=lastActive;break;}
        }
        double onset=Math.Max(3*c.Sigma,.025*amp);
        while(start<peak&&Math.Abs(x[start]-c.Center)<onset)start++;
        int radius=c.Filtered?2:0;
        int mapped=AbsoluteMaximumIndex(c.Raw,Math.Max(start,peak-radius),Math.Min(end,peak+radius),c.RawCenter);
        if(chargePeak<0)chargePeak=AbsoluteMaximumIndex(c.Raw,start,end,c.RawCenter);
        var p=new A4Pulse {Start=start,End=end,Peak=peak,RawPeak=mapped,RawAmplitudeIndex=chargePeak,
            Amplitude=amp,Period=period,Baseline=c.Center,NoiseSigma=c.Sigma,Snr=amp/c.Sigma,
            MappingValid=chargePeak>=start&&chargePeak<=end&&Math.Abs(mapped-peak)<=radius&&Math.Abs(c.Raw[mapped]-c.RawCenter)>=3*c.Sigma,
            Clipped=start==0||end==x.Length-1,UnresolvedOverlap=end-start>c.RoundTrip};
        A4Measure(c,p);return p;
    }
    private static A4Pulse A4PacketWithinRawSupport(A4Context c,A4Pulse rawPacket)
    {
        // Filtering can create pre-ringing and a long low-frequency pedestal.
        // Neither is a new physical pulse. Preserve the complete RAW packet
        // support; locate the filtered absolute maximum only inside that packet.
        int peak=AbsoluteMaximumIndex(c.Signal,rawPacket.Start,rawPacket.End,c.Center);
        int radius=c.Filtered?2:0;
        int mapped=AbsoluteMaximumIndex(c.Raw,Math.Max(rawPacket.Start,peak-radius),Math.Min(rawPacket.End,peak+radius),c.RawCenter);
        double amp=Math.Abs(c.Signal[peak]-c.Center);
        var p=new A4Pulse {Start=rawPacket.Start,End=rawPacket.End,Peak=peak,RawPeak=mapped,
            RawAmplitudeIndex=A4ChargeIndex(rawPacket),Amplitude=amp,Period=rawPacket.Period,
            Baseline=c.Center,NoiseSigma=c.Sigma,Snr=amp/Math.Max(1e-15,c.Sigma),
            MappingValid=Math.Abs(mapped-peak)<=radius&&Math.Abs(c.Raw[mapped]-c.RawCenter)>=3*c.Sigma,
            Clipped=rawPacket.Clipped,UnresolvedOverlap=rawPacket.UnresolvedOverlap,TriggeredFromRaw=true,BoundaryRefined=rawPacket.BoundaryRefined,
            RefinedLeftGuardStart=rawPacket.RefinedLeftGuardStart,RefinedLeftGuardEnd=rawPacket.RefinedLeftGuardEnd,
            HasRefinedGuardProof=rawPacket.HasRefinedGuardProof,
            RefinedRightGuardStart=rawPacket.RefinedRightGuardStart,RefinedRightGuardEnd=rawPacket.RefinedRightGuardEnd};
        A4Measure(c,p);return p;
    }
    private static void A4WindowGuards(A4Context c,List<A4Pulse> pulses)
    {
        pulses.Sort((a,b)=>a.Start.CompareTo(b.Start));
        for(int n=0;n<pulses.Count;n++)
        {
            A4Pulse p=pulses[n];p.Id=n;int guard=Math.Max(24,Math.Min(128,c.RoundTrip/5));
            int left=Math.Max(0,Math.Max(p.Start-guard,n>0?pulses[n-1].End+1:0));
            int right=Math.Min(c.Signal.Length-1,Math.Min(p.End+guard,n+1<pulses.Count?pulses[n+1].Start-1:c.Signal.Length-1));
            double[] before=left<p.Start?A4Slice(c.Signal,left,p.Start-1):new double[0],after=right>p.End?A4Slice(c.Signal,p.End+1,right):new double[0];
            double[] noise=before.Concat(after).ToArray();
            p.BackgroundGuards=new[]{before,after};p.SpectralGuards=new[]{before,after};
            p.Baseline=noise.Length>=8?Median(noise):c.Center;
            p.NoiseSigma=noise.Length>=8?Math.Max(c.Sigma,RobustSigma(noise,p.Baseline)):c.Sigma;
            p.Snr=Math.Abs(c.Signal[p.Peak]-p.Baseline)/Math.Max(1e-15,p.NoiseSigma);
            p.BaselineUnstable=before.Length>=8&&after.Length>=8&&Math.Abs(Median(before)-Median(after))>3*p.NoiseSigma;
            A4Measure(c,p);
            if(c.Bands!=null)
            {
                A4MeasureBands(c.Signal,c.Fs,p,c.Bands.EdgesHz);
                p.SpectralBefore=A4NoiseBandBlocks(new[]{before},c.Fs,c.Bands.EdgesHz);
                p.SpectralAfter=A4NoiseBandBlocks(new[]{after},c.Fs,c.Bands.EdgesHz);
            }
        }
    }
    private static void A4AttachWindow(A4Pulse p,double[] samples,int origin)
    {
        p.AnalysisSamples=samples;p.AnalysisOrigin=origin;
        p.Start+=origin;p.End+=origin;p.Peak+=origin;p.RawPeak+=origin;
        if(p.RawAmplitudeIndex>=0)p.RawAmplitudeIndex+=origin;
        if(p.RefinedLeftGuardStart>=0){p.RefinedLeftGuardStart+=origin;p.RefinedLeftGuardEnd+=origin;}
        if(p.RefinedRightGuardStart>=0){p.RefinedRightGuardStart+=origin;p.RefinedRightGuardEnd+=origin;}
    }
    private static List<A4Pulse> A4DiscoverRawSeedWindows(A4Context c,double[] cutoffs,int pre,int post)
    {
        // No LOC filter has run when this list is built. The maximum ORIGINAL
        // absolute sample is the first seed, independent of bandwidth or output Top-N.
        double[] detection=A4RawAnalysis(c);
        List<int> seeds=A4AbsoluteSeeds(detection,c.RawCenter,Math.Max(c.RawDiscoveryThreshold,c.IncidentThreshold),0,c.Raw.Length-1);
        List<A4Pulse> result=new List<A4Pulse>();
        c.RawSeedOrder=seeds;c.WindowTraces=new List<A4WindowTrace>();
        foreach(int seed in seeds)
        {
            if(!A4AnalysisPhaseEligible(c,seed))continue;
            // Canonical packet identity is not matching ownership. Unresolved
            // long windows must never hide later strong seeds before their review.
            if(result.Any(p=>p.MappingValid&&!p.UnresolvedOverlap&&seed>=p.Start&&seed<=p.End))continue;
            if(c.WindowTraces.Count>=c.AnalysisLimit){c.AnalysisLimitReached=true;break;}
            if(c.WindowTraces.Count>=4096){c.WindowBudgetExceeded=true;break;}
            int guard=Math.Max(128,c.RoundTrip/2);
            int start=(int)Math.Max(0L,(long)seed-c.RoundTrip-pre-guard);
            int end=(int)Math.Min(c.Raw.Length-1L,(long)seed+Math.Max(3L*c.RoundTrip,A4ReflectionMaximumDelay(c))+post+guard);
            double[] raw=A4Slice(detection,start,end);int localSeed=seed-start;
            // A long-block baseline can retain a small slowly varying residual.
            // Remove its fixed local DC for boundary discovery, before any pair
            // exists; the window spans several round trips and is never selected
            // by a downstream score. Original-domain output charge stays global.
            double rawCenter=Median(raw);
            // Identify the incident on raw data BEFORE invoking the user's filter.
            var rawContext=new A4Context {Raw=raw,Signal=raw,RawCenter=rawCenter,Center=rawCenter,
                Sigma=c.Sigma,Threshold=4.5*c.Sigma,RawDiscoveryThreshold=c.RawDiscoveryThreshold,
                Fs=c.Fs,RoundTrip=c.RoundTrip,RoundTripExact=c.RoundTripExact};
            // Boundary discovery is local to the already selected raw seed window.
            // Neighbour-aware raw supports stop backward search crossing a separate
            // preceding packet, while retaining the entire bipolar/tail support.
            List<A4Pulse> rawPackets=A4Extract(rawContext);
            for(int ri=0;ri<rawPackets.Count;ri++)
                rawPackets[ri]=A4RefineUnresolvedRawPacket(rawContext,rawPackets[ri],c,start);
            if(c.Algorithm==5||c.Algorithm==4)
                for(int ri=0;ri<rawPackets.Count;ri++)
                    rawPackets[ri]=A6RecoverDriftBoundedRawPacket(rawContext,rawPackets[ri],rawPackets,c,start);
            A4Pulse rawIncident=rawPackets.FirstOrDefault(p=>localSeed>=p.Start&&localSeed<=p.End)??A4SeededPacket(rawContext,localSeed,localSeed);
            // A secondary lobe cannot become a second identity for a stronger packet.
            int rawDominant=AbsoluteMaximumIndex(raw,rawIncident.Start,rawIncident.End,rawCenter);
            if(rawDominant!=localSeed)continue;
            rawIncident.RawAmplitudeIndex=localSeed;
            double[] signal=c.Filtered?A4ZeroPhase(raw,c.Fs,cutoffs):(double[])raw.Clone();
            double center=c.Filtered?Median(signal):rawCenter;
            double sigma=c.Filtered?Math.Max(1e-15,Math.Max(RobustSigma(signal,center),signal.Max(v=>Math.Abs(v-center))*1e-8)):c.Sigma;
            var local=new A4Context {Raw=raw,Signal=signal,RawCenter=rawCenter,Center=center,Sigma=sigma,Threshold=4.5*sigma,
                RawDiscoveryThreshold=c.RawDiscoveryThreshold,Fs=c.Fs,RoundTrip=c.RoundTrip,RoundTripExact=c.RoundTripExact,
                Filtered=c.Filtered,FirstArrival=c.FirstArrival,Attenuation=c.Attenuation,Width=c.Width,
                Bands=c.Bands,LowBand=c.LowBand,Calibration=c.Calibration};
            A4Pulse incident=A4PacketWithinRawSupport(local,rawIncident);int peak=incident.Peak;
            var packets=new List<A4Pulse>{incident};
            c.WindowTraces.Add(new A4WindowTrace {Seed=seed,Start=start,End=end,RawIncidentStart=rawIncident.Start+start,
                RawIncidentEnd=rawIncident.End+start,IncidentPeak=peak+start,RawAmplitude=A4RawCharge(c,seed)});
            // Reflection discovery is performed ONLY now, within this filtered
            // incident window. Delay/shape/ownership gates still run globally later.
            foreach(int reflectionSeed in A4AbsoluteSeeds(signal,center,local.Threshold,incident.End+1,
                (int)Math.Min(signal.Length-1L,(long)localSeed+A4ReflectionMaximumDelay(c))))
            {
                if(packets.Any(p=>reflectionSeed>=p.Start&&reflectionSeed<=p.End))continue;
                int mappedSeed=AbsoluteMaximumIndex(raw,Math.Max(0,reflectionSeed-(c.Filtered?2:0)),Math.Min(raw.Length-1,reflectionSeed+(c.Filtered?2:0)),rawCenter);
                if(Math.Abs(raw[mappedSeed]-rawCenter)<rawContext.Sigma*3)continue;
                A4Pulse rawReflection=rawPackets.FirstOrDefault(p=>mappedSeed>=p.Start&&mappedSeed<=p.End)??A4SeededPacket(rawContext,mappedSeed,-1);
                rawReflection.RawAmplitudeIndex=AbsoluteMaximumIndex(raw,rawReflection.Start,rawReflection.End,rawCenter);
                A4Pulse reflection=A4PacketWithinRawSupport(local,rawReflection);
                if(!A4ReflectionDelayWithinWindow(c,A4ChargeIndex(reflection)-localSeed))continue;
                if(reflection.Start<=incident.End)continue;
                if(!reflection.MappingValid)continue;
                packets.Add(reflection);
            }
            A4WindowGuards(local,packets);A4PrepareLowBandPackets(local,packets);
            HashSet<A4Pulse> attachedLow=new HashSet<A4Pulse>();
            foreach(A4Pulse p in packets)
            {
                if(p.LowBandPulse!=null&&attachedLow.Add(p.LowBandPulse))A4AttachWindow(p.LowBandPulse,local.LowBandSignal,start);
                A4AttachWindow(p,signal,start);
                A4Pulse same=result.FirstOrDefault(q=>A4ChargeIndex(q)==A4ChargeIndex(p));
                if(same!=null)
                {
                    // A independently guarded complete support may replace an unresolved
                    // view only inside the SAME analysis window. Never select a different
                    // filter context because it happens to improve a correlation.
                    if(same.UnresolvedOverlap&&p.BoundaryRefined&&!p.UnresolvedOverlap&&
                        p.Start>=same.Start&&p.End<=same.End&&p.AnalysisOrigin==same.AnalysisOrigin&&
                        p.AnalysisSamples.Length==same.AnalysisSamples.Length)
                        result[result.IndexOf(same)]=p;
                    // Retain the earlier, stronger-seed window. A later window cannot
                    // replace its evidence merely because that gives a larger correlation.
                    continue;
                }
                A4Pulse covering=result.FirstOrDefault(q=>q.MappingValid&&!q.UnresolvedOverlap&&A4ChargeIndex(p)>=q.Start&&A4ChargeIndex(p)<=q.End);
                if(covering!=null)continue;
                // Never silently discard a different strong packet identity.
                result.Add(p);
            }
        }
        result=result.OrderBy(p=>p.Start).ThenBy(p=>A4ChargeIndex(p)).ToList();
        for(int i=0;i<result.Count;i++)result[i].Id=i;
        return result;
    }
    private static double[] A4MatchedFilteredView(A4Context c,A4Pulse incident,int start,int end)
    {
        double[] view=A4MatchedFilteredViewUnscaled(c,incident,start,end);
        if(c.Algorithm!=4)return view;
        A4Index4DisplayGainInfo gain;
        double[] output=A4Index4PreserveDisplayAmplitude(c,incident,start,view,out gain);
        c.Index4DisplayDiagnostics[A4ChargeIndex(incident)]=A4Index4DisplayGainJson(gain);
        return output;
    }
    private static double[] A4MatchedFilteredViewUnscaled(A4Context c,A4Pulse incident,int start,int end)
    {
        if(!c.Filtered)return A4Slice(c.Raw,start,end);
        int origin=incident.AnalysisOrigin;
        if(incident.AnalysisSamples!=null&&start>=origin&&end<(long)origin+incident.AnalysisSamples.Length)
            return A4Slice(incident.AnalysisSamples,start-origin,end-origin);
        // Only the extra DISPLAY region is filtered here. Use a fixed maximum-range
        // context so changing the requested crop cannot change shared displayed samples.
        // Existing matched samples are copied unchanged; this view never feeds analysis
        // or the independently computed equivalent-pulse waveform.
        int peak=A4ChargeIndex(incident),limit=A4DisplayExtensionLimit(c.RoundTripExact);
        int guard=Math.Max(128,c.RoundTrip);
        int left=(int)Math.Max(0L,(long)peak-limit-guard);
        int right=(int)Math.Min(c.Raw.Length-1L,(long)peak+c.RoundTrip+limit+guard);
        if(incident.AnalysisSamples!=null)
        {
            left=Math.Min(left,origin);
            right=(int)Math.Max(right,Math.Min(c.Raw.Length-1L,(long)origin+incident.AnalysisSamples.Length-1));
        }
        double[] display=A4ZeroPhase(A4Slice(A4RawAnalysis(c),left,right),c.Fs,c.EffectiveCutoffs);
        double[] result=A4Slice(display,start-left,end-left);
        if(incident.AnalysisSamples!=null)
        {
            int first=Math.Max(start,origin),last=(int)Math.Min(end,(long)origin+incident.AnalysisSamples.Length-1);
            for(int i=first;i<=last;i++)result[i-start]=incident.AnalysisSamples[i-origin];
        }
        return result;
    }
    private static string A4WindowTraceJson(A4Context c)
    {
        if(c.WindowTraces==null)return "null";
        var b=new StringBuilder("{\"order\":\"detrended_original_absolute_descending_then_incident_then_window_filter_then_reflection\",\"whole_loc_prefiltered\":false,\"raw_amplitude_preserved_separately_from_timing\":true");
        b.Append(",\"shared_preprocessing\":").Append(A4MeasurementSignalJson(c.MeasurementSignal));
        b.Append(",\"pre_matching_boundary_review\":[").Append(String.Join(",",c.BoundaryReviewTraces)).Append(']');
        b.Append(",\"filter_enabled\":").Append(c.Filtered?"true":"false").Append(",\"window_count\":").Append(c.WindowTraces.Count)
            .Append(",\"budget_exceeded\":").Append(c.WindowBudgetExceeded?"true":"false").Append(",\"examples_truncated\":").Append(c.WindowTraces.Count>128?"true":"false").Append(",\"windows\":[");
        for(int i=0;i<Math.Min(128,c.WindowTraces.Count);i++)
        {
            if(i>0)b.Append(',');A4WindowTrace t=c.WindowTraces[i];
            b.Append("{\"raw_seed\":").Append(t.Seed).Append(",\"raw_absolute_pc\":").Append(NumberJson(t.RawAmplitude))
                .Append(",\"window_start\":").Append(t.Start).Append(",\"window_end\":").Append(t.End)
                .Append(",\"raw_incident_start\":").Append(t.RawIncidentStart).Append(",\"raw_incident_end\":").Append(t.RawIncidentEnd)
                .Append(",\"filtered_incident_peak\":").Append(t.IncidentPeak).Append('}');
        }
        return b.Append("]}").ToString();
    }
}
