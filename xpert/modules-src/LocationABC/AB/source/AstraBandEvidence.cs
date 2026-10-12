using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4LowBandReference
    {
        internal bool Trusted;
        internal string Reason="not_measured";
        internal double AmplitudeRatio,IncidentAmplitude,ReflectionAmplitude,IncidentSnr,ReflectionSnr,UncertaintyDb,TravelSeconds;
        internal int IncidentStart,IncidentEnd,ReflectionStart,ReflectionEnd;
    }
    private sealed class A4LowBandPairEvidence
    {
        internal bool Trusted,Enhanced;
        internal string Reason="not_measured";
        internal double AmplitudeRatio,ExpectedRatio,UncertaintyDb,Score=1,IncidentSnr,ReflectionSnr;
        internal double SignedCorrelation,EnvelopeCorrelation;
    }
    // This diagnostic band is fixed in physical Hz, independent of CAL quantile bands and user filter settings.
    // The response is exactly zero above 3 MHz; a 2.7..3 MHz raised-cosine transition limits artificial ringing.
    private static double[] A4LowPassThreeMHz(double[] input,double fs)
    {
        return A4FrequencyFilter(input,fs,0,3000000,true);
    }
    private static A4Context A4LowBandContext(double[] raw,double fs,int roundTrip,double exact)
    {
        double[] signal=A4LowPassThreeMHz(raw,fs);double center=Median(signal);
        double sigma=Math.Max(1e-15,RobustSigma(signal,center));
        return new A4Context {Raw=signal,Signal=signal,Fs=fs,Center=center,RawCenter=center,Sigma=sigma,
            Threshold=4.5*sigma,RoundTrip=roundTrip,RoundTripExact=exact};
    }
    private static List<A4Pulse> A4ExtractLowBand(A4Context c)
    {
        List<A4Pulse> pulses=A4Extract(c);
        foreach(A4Pulse p in pulses)
        {
            // Re-evaluate the absolute maximum against the local baseline over the entire isolated support.
            double amplitude=0;for(int i=p.Start;i<=p.End;i++)amplitude=Math.Max(amplitude,Math.Abs(c.Signal[i]-p.Baseline));
            p.LocalAmplitude=amplitude;
        }
        return pulses;
    }
    private static bool A4StrongLowBandRetention(double ratio)
    {return Finite(ratio)&&ratio>=.35-1e-12;} // Floating-point boundary tolerance only; not a physical ratio margin.
    private static bool A4LowBandPacketTrusted(A4Pulse p,out double amplitude,out double noise,out double snr)
    {
        amplitude=p.LocalAmplitude;noise=p.NoiseSigma;snr=0;
        if(p.SpectralGuards==null||p.SpectralGuards.Length<2||p.SpectralGuards.Any(g=>g==null||g.Length<16)||
            p.Clipped||p.UnresolvedOverlap||p.BaselineUnstable)return false;
        foreach(double[] side in p.SpectralGuards)
        {
            double center=Median(side);noise=Math.Max(noise,RobustSigma(side,center));
            // Separate sides are mandatory: averaging cannot hide a noisier reflected-packet guard.
            if(Math.Abs(center-p.Baseline)>3*Math.Max(1e-15,noise))return false;
        }
        snr=amplitude/Math.Max(1e-15,noise);
        return Finite(amplitude)&&amplitude>0&&Finite(snr)&&snr>=10;
    }
    private static double A4LowBandAmplitudeErrorDb(double amplitude,double noise)
    {return 20*Math.Log10((amplitude+2*noise)/Math.Max(1e-30,amplitude-2*noise));}
    private static A4LowBandReference A4BuildLowBandReference(A4Context c)
    {
        A4LowBandReference reference=new A4LowBandReference();ParsedCalibration cal=c.Calibration;
        if(!c.Attenuation){reference.Reason="attenuation_reference_disabled";return reference;}
        if(cal==null||cal.Samples==null||cal.Samples.Length<64||!Finite(cal.SampleRate)||cal.SampleRate<6000000||c.Fs<6000000)
        {reference.Reason="missing_cal_or_sampling_rate_below_6mhz";return reference;}
        if(!Finite(cal.StartTime)||!Finite(cal.FinalTime)||cal.FinalTime<=cal.StartTime)
        {reference.Reason="invalid_cal_marked_round_trip";return reference;}
        int markedA=IndexFromTime(cal.StartTime,cal.SampleRate,cal.Samples.Length,0),
            markedB=IndexFromTime(cal.FinalTime,cal.SampleRate,cal.Samples.Length,cal.Samples.Length-1);
        if(markedB-markedA<12){reference.Reason="cal_round_trip_too_short";return reference;}
        A4Context cc=A4LowBandContext(cal.Samples,cal.SampleRate,markedB-markedA,markedB-markedA);
        List<A4Pulse> packets=A4ExtractLowBand(cc);int tolerance=Math.Max(12,cc.RoundTrip/8);
        A4Pulse a=packets.Where(p=>Math.Abs(p.Peak-markedA)<=tolerance).OrderBy(p=>Math.Abs(p.Peak-markedA)).FirstOrDefault(),
            b=packets.Where(p=>Math.Abs(p.Peak-markedB)<=tolerance).OrderBy(p=>Math.Abs(p.Peak-markedB)).FirstOrDefault();
        if(a==null||b==null||a==b||a.End>=b.Start||a.End>=markedB||b.End>=markedB+cc.RoundTrip)
        {reference.Reason="cal_low_band_complete_packets_not_separable";return reference;}
        reference.IncidentStart=a.Start;reference.IncidentEnd=a.End;reference.ReflectionStart=b.Start;reference.ReflectionEnd=b.End;
        double aa,ab,na,nb,sa,sb;
        bool validA=A4LowBandPacketTrusted(a,out aa,out na,out sa),validB=A4LowBandPacketTrusted(b,out ab,out nb,out sb);
        reference.IncidentAmplitude=aa;reference.ReflectionAmplitude=ab;reference.IncidentSnr=sa;reference.ReflectionSnr=sb;
        reference.AmplitudeRatio=aa>0?ab/aa:double.NaN;
        reference.TravelSeconds=(markedB-markedA)/cal.SampleRate;
        if(!validA||!validB){reference.Reason="cal_low_band_support_guard_or_snr_unreliable";return reference;}
        if(!Finite(reference.AmplitudeRatio)||reference.AmplitudeRatio<=0||reference.AmplitudeRatio>1.10)
        {reference.Reason="cal_low_band_ratio_outside_0_to_1point10";return reference;}
        reference.UncertaintyDb=A4LowBandAmplitudeErrorDb(aa,na)+A4LowBandAmplitudeErrorDb(ab,nb);
        reference.Trusted=true;reference.Reason=A4StrongLowBandRetention(reference.AmplitudeRatio)?"trusted_retained_amplitude_at_least_35_percent":"trusted_retained_amplitude_below_35_percent";
        return reference;
    }
    private static void A4PrepareLowBandPackets(A4Context c,List<A4Pulse> pulses)
    {
        if(c.LowBand==null||!c.LowBand.Trusted||!c.Attenuation)return;
        A4Context lc=A4LowBandContext(c.Raw,c.Fs,c.RoundTrip,c.RoundTripExact);
        c.LowBandSignal=lc.Signal;
        List<A4Pulse> low=A4ExtractLowBand(lc);
        foreach(A4Pulse p in pulses)
        {
            A4Pulse[] overlap=low.Where(v=>v.Start<=p.End&&v.End>=p.Start).ToArray();
            if(overlap.Length!=1)continue;
            A4Pulse match=overlap[0];
            if(pulses.Count(v=>v.Start<=match.End&&v.End>=match.Start)!=1)continue;
            double amplitude,noise,snr;if(A4LowBandPacketTrusted(match,out amplitude,out noise,out snr))p.LowBandPulse=match;
        }
    }
    private static A4LowBandPairEvidence A4MeasureLowBandPair(A4Context c,A4Pulse a,A4Pulse b,int delay)
    {
        A4LowBandPairEvidence evidence=new A4LowBandPairEvidence();
        if(!c.Attenuation){evidence.Reason="attenuation_reference_disabled";return evidence;}
        if(c.LowBand==null||!c.LowBand.Trusted){evidence.Reason="cal_low_band_not_trusted";return evidence;}
        if(a.LowBandPulse==null||b.LowBandPulse==null||a.LowBandPulse==b.LowBandPulse||a.LowBandPulse.End>=b.LowBandPulse.Start||
            (c.LowBandSignal==null&&(a.LowBandPulse.AnalysisSamples==null||b.LowBandPulse.AnalysisSamples==null)))
        {evidence.Reason="loc_low_band_complete_packets_or_unique_support_unavailable";return evidence;}
        double aa,ab,na,nb,sa,sb;
        if(!A4LowBandPacketTrusted(a.LowBandPulse,out aa,out na,out sa)||!A4LowBandPacketTrusted(b.LowBandPulse,out ab,out nb,out sb))
        {evidence.Reason="loc_low_band_noise_or_support_unreliable";return evidence;}
        evidence.Trusted=true;evidence.IncidentSnr=sa;evidence.ReflectionSnr=sb;evidence.AmplitudeRatio=ab/aa;
        A4Correlate(new A4Context {Signal=c.LowBandSignal},a.LowBandPulse,b.LowBandPulse,out evidence.SignedCorrelation,out evidence.EnvelopeCorrelation);
        double fraction=delay/c.Fs/c.LowBand.TravelSeconds;
        // CAL includes a far-end reflection coefficient and a different travel path: this is a soft model, never a hard lower bound.
        evidence.ExpectedRatio=Math.Pow(Math.Min(1,c.LowBand.AmplitudeRatio),Math.Max(0,fraction));
        evidence.UncertaintyDb=A4LowBandAmplitudeErrorDb(aa,na)+A4LowBandAmplitudeErrorDb(ab,nb)+c.LowBand.UncertaintyDb*Math.Max(0,fraction);
        double difference=Math.Abs(20*Math.Log10(Math.Max(1e-30,evidence.AmplitudeRatio)/Math.Max(1e-30,evidence.ExpectedRatio)));
        evidence.Score=Math.Exp(-Math.Max(0,difference-evidence.UncertaintyDb-3)/6);
        evidence.Enhanced=A4StrongLowBandRetention(c.LowBand.AmplitudeRatio);
        evidence.Reason=evidence.Enhanced?"trusted_low_band_retention_35_percent_or_more":"trusted_cal_retention_below_35_percent";
        return evidence;
    }
    private sealed class A4BandReference
    {
        internal double[] EdgesHz,GainDb,UncertaintyDb;
        internal bool[] Valid;
        internal int IncidentStart,IncidentEnd,ReflectionStart,ReflectionEnd;
        internal double TravelSeconds;
        internal double[] IncidentNoise,ReflectionNoise,IncidentSnr,ReflectionSnr;
        internal int[] GuardBlocks;
    }
    private sealed class A4PowerSpectrum
    {
        internal double[] Power,Noise;
        internal double BinHz;
    }
    private static A4PowerSpectrum A4Power(double[] signal,double fs,A4Pulse p,int requestedN=0)
    {
        int length=p.End-p.Start+1,n=1;
        while(n<length*4)n=checked(n*2);
        n=Math.Max(n,Math.Max(4096,requestedN));
        double[] real=new double[n],imag=new double[n];
        for(int i=0;i<length;i++)real[i]=A4Sample(signal,p,p.Start+i)-p.Baseline;
        A4Fft(real,imag);
        A4PowerSpectrum s=new A4PowerSpectrum{Power=new double[n/2+1],Noise=new double[n/2+1],BinHz=fs/n};
        for(int k=0;k<s.Power.Length;k++)
        {
            double weight=k==0||k==n/2?1:2;
            s.Power[k]=weight*(real[k]*real[k]+imag[k]*imag[k])/n;
            // Parseval units are amplitude squared times samples, not unnormalised FFT bin power.
            s.Noise[k]=weight*p.NoiseSigma*p.NoiseSigma*length/n;
        }
        return s;
    }
    private static List<double[]> A4QuietBlocks(double[][] guards)
    {
        List<double[]> blocks=new List<double[]>();
        if(guards==null)return blocks;
        foreach(double[] guard in guards)
        {
            if(guard==null||guard.Length<8)continue;
            int width=Math.Min(64,guard.Length);
            for(int start=0;start+width<=guard.Length;start+=width)
            {
                double[] block=guard.Skip(start).Take(width).ToArray();double center=Median(block);
                for(int i=0;i<block.Length;i++)block[i]-=center;blocks.Add(block);
            }
        }
        return blocks;
    }
    private static double[] A4NoisePower(double[][] guards,double fs,int n)
    {
        List<double[]> blocks=A4QuietBlocks(guards).OrderBy(b=>b.Sum(v=>v*v)/b.Length).ToList();
        if(blocks.Count==0)return null;
        blocks=blocks.Take(Math.Max(1,blocks.Count/2)).ToList();
        double[] noise=new double[n/2+1];
        foreach(double[] block in blocks)
        {
            A4PowerSpectrum s=A4Power(block,fs,new A4Pulse{Start=0,End=block.Length-1,Baseline=0},n);
            for(int k=0;k<noise.Length;k++)noise[k]+=s.Power[k]/block.Length/blocks.Count;
        }
        return noise;
    }
    private static List<double[]> A4NoiseBandBlocks(double[][] guards,double fs,double[] edges)
    {
        List<double[]> values=new List<double[]>();
        foreach(double[] block in A4QuietBlocks(guards))
        {
            A4PowerSpectrum s=A4Power(block,fs,new A4Pulse{Start=0,End=block.Length-1,Baseline=0});
            double[] row=new double[edges.Length];row[row.Length-1]=block.Sum(v=>v*v)/block.Length;
            for(int k=1;k<s.Power.Length;k++)for(int b=0;b<edges.Length-1;b++)
                if(k*s.BinHz>=edges[b]&&k*s.BinHz<edges[b+1])row[b]+=s.Power[k]/block.Length;
            values.Add(row);
        }
        return values;
    }
    private static double[] A4CalibrationNoisePower(double[][] guards,double fs,int n)
    {
        // CAL endpoints are known to be the same injection. Different local noise levels are not
        // evidence against that identity. Use each packet's own larger observed block PSD,
        // never substitute the other packet's quiet guard and never discard a noisy local side.
        List<double[]> blocks=A4QuietBlocks(guards).Where(v=>v.Length>=16).ToList();
        if(blocks.Count==0)return null;
        double[] noise=new double[n/2+1];
        foreach(double[] block in blocks)
        {
            A4PowerSpectrum s=A4Power(block,fs,new A4Pulse{Start=0,End=block.Length-1,Baseline=0},n);
            for(int k=0;k<noise.Length;k++)noise[k]=Math.Max(noise[k],s.Power[k]/block.Length);
        }
        return noise;
    }
    private static void A4MeasureBands(double[] signal,double fs,A4Pulse pulse,double[] edges,double[] coloredNoise=null)
    {
        A4PowerSpectrum s=A4Power(signal,fs,pulse,coloredNoise!=null?(coloredNoise.Length-1)*2:0);
        pulse.BandEnergy=new double[edges.Length-1];pulse.BandNoise=new double[edges.Length-1];
        pulse.BandRawEnergy=new double[edges.Length-1];
        for(int band=0;band<pulse.BandEnergy.Length;band++)
        {
            double total=0,noise=0;
            for(int k=1;k<s.Power.Length;k++)
            {
                double frequency=k*s.BinHz;
                if(frequency<edges[band]||frequency>=edges[band+1])continue;
                total+=s.Power[k];noise+=coloredNoise!=null?coloredNoise[k]*(pulse.End-pulse.Start+1):s.Noise[k];
            }
            pulse.BandNoise[band]=noise;
            pulse.BandRawEnergy[band]=total;
            pulse.BandEnergy[band]=Math.Max(0,total-noise);
        }
    }
    private static A4BandReference A4BuildBandReference(A4Context c,double[] cutoffs)
    {
        ParsedCalibration cal=c.Calibration;
        if(cal.Samples==null||cal.Samples.Length<32||!Finite(cal.SampleRate)||cal.SampleRate<=0||cal.FinalTime<=cal.StartTime)return null;
        int markedA=IndexFromTime(cal.StartTime,cal.SampleRate,cal.Samples.Length,0);
        int markedB=IndexFromTime(cal.FinalTime,cal.SampleRate,cal.Samples.Length,cal.Samples.Length-1);
        if(markedB-markedA<12)return null;
        double[] signal=cal.Samples;
        if(c.Filtered)
        {
            double high=Math.Min(cutoffs[1],cal.SampleRate/2);
            if(cutoffs[0]>=high)return null;
            signal=A4ZeroPhase(signal,cal.SampleRate,new[]{cutoffs[0],high});
        }
        double center=Median(signal),sigma=Math.Max(1e-15,RobustSigma(signal,center));
        A4Context cc=new A4Context {Raw=cal.Samples,Signal=signal,Center=center,RawCenter=Median(cal.Samples),
            Fs=cal.SampleRate,Sigma=sigma,Threshold=4.5*sigma,RoundTrip=markedB-markedA,
            RoundTripExact=markedB-markedA,Filtered=c.Filtered};
        List<A4Pulse> pulses=A4Extract(cc);
        int tolerance=Math.Max(12,cc.RoundTrip/8);
        A4Pulse a=pulses.Where(p=>Math.Abs(p.Peak-markedA)<=tolerance).OrderBy(p=>Math.Abs(p.Peak-markedA)).FirstOrDefault();
        A4Pulse b=pulses.Where(p=>Math.Abs(p.Peak-markedB)<=tolerance).OrderBy(p=>Math.Abs(p.Peak-markedB)).FirstOrDefault();
        // No fabricated calibration transfer if complete supports cannot be isolated from neighbouring returns.
        if(a==null||b==null||a==b||a.End>=b.Start||a.Clipped||b.Clipped||a.BaselineUnstable||b.BaselineUnstable||
            a.End>=markedB||b.End>=markedB+cc.RoundTrip||a.Snr<10||b.Snr<10)return null;
        A4PowerSpectrum spectrum=A4Power(signal,cal.SampleRate,a);
        int commonN=Math.Max((spectrum.Power.Length-1)*2,4096);
        while(commonN<(b.End-b.Start+1)*4)commonN*=2;
        spectrum=A4Power(signal,cal.SampleRate,a,commonN);
        double[][] incidentGuard=a.SpectralGuards,reflectionGuard=b.SpectralGuards;
        double[] incidentNoisePower=A4CalibrationNoisePower(incidentGuard,cal.SampleRate,commonN);
        double[] reflectionNoisePower=A4CalibrationNoisePower(reflectionGuard,cal.SampleRate,commonN);
        if(incidentNoisePower==null||reflectionNoisePower==null){c.CalibrationNoiseRejectedBands=3;return null;}
        double commonNyquist=Math.Min(c.Fs,cal.SampleRate)/2;
        double[] energy=spectrum.Power.Select((v,k)=>k>0&&k*spectrum.BinHz<commonNyquist?Math.Max(0,v-incidentNoisePower[k]*(a.End-a.Start+1)):0).ToArray();
        double sum=energy.Sum();if(sum<=0)return null;
        double[] quantiles={.05,.35,.65,.95};double[] edges=new double[4];
        for(int q=0;q<quantiles.Length;q++)
        {
            double cumulative=0;
            for(int k=1;k<energy.Length;k++)
            {
                cumulative+=energy[k];
                if(cumulative>=sum*quantiles[q]){edges[q]=k*spectrum.BinHz;break;}
            }
        }
        if(edges.Zip(edges.Skip(1),(x,y)=>y-x).Any(v=>v<2*spectrum.BinHz))return null;
        A4MeasureBands(signal,cal.SampleRate,a,edges,incidentNoisePower);A4MeasureBands(signal,cal.SampleRate,b,edges,reflectionNoisePower);
        A4BandReference reference=new A4BandReference{EdgesHz=edges,GainDb=new double[3],UncertaintyDb=new double[3],
            Valid=new bool[3],IncidentStart=a.Start,IncidentEnd=a.End,ReflectionStart=b.Start,ReflectionEnd=b.End,
            TravelSeconds=(markedB-markedA)/cal.SampleRate,IncidentNoise=a.BandNoise,ReflectionNoise=b.BandNoise,
            IncidentSnr=new double[3],ReflectionSnr=new double[3],
            GuardBlocks=incidentGuard.Concat(reflectionGuard).Select(g=>A4QuietBlocks(new[]{g}).Count(v=>v.Length>=16)).ToArray()};
        for(int band=0;band<3;band++)
        {
            double ea=a.BandEnergy[band],eb=b.BandEnergy[band],na=a.BandNoise[band],nb=b.BandNoise[band];
            reference.IncidentSnr[band]=ea/Math.Max(1e-30,na);reference.ReflectionSnr[band]=eb/Math.Max(1e-30,nb);
            reference.Valid[band]=ea>6*na&&eb>6*nb&&ea>0&&eb>0;
            if(!reference.Valid[band]){c.CalibrationNoiseRejectedBands++;continue;}
            reference.GainDb[band]=10*Math.Log10(eb/ea);
            reference.UncertaintyDb[band]=A4BandErrorDb(ea,na)+A4BandErrorDb(eb,nb);
        }
        return reference.Valid.Any(v=>v)?reference:null;
    }
    private static double A4BandErrorDb(double energy,double noise)
    {
        // Conservative energy confidence radius combines signal-noise cross energy and noise energy.
        // A weak band has a wider interval and therefore cannot create a precise transfer-function veto.
        return 10*Math.Log10(1+2*Math.Sqrt(Math.Max(0,noise)/Math.Max(1e-30,energy))+2*noise/Math.Max(1e-30,energy));
    }
    private static bool A4SideNoise(List<double[]> allBlocks,int band,out double mean)
    {
        mean=0;if(allBlocks==null||allBlocks.Count==0)return false;
        List<double[]> sorted=allBlocks.OrderBy(v=>v[v.Length-1]).ToList();
        List<double[]> selected=sorted.Take(Math.Max(1,sorted.Count/2)).ToList();
        mean=selected.Average(v=>v[band]);
        if(sorted.Count<3)return true;
        double[] bandValues=sorted.Select(v=>v[band]).OrderBy(v=>v).ToArray();
        double low=bandValues[Math.Max(0,bandValues.Length/4)],high=bandValues[Math.Min(bandValues.Length-1,3*bandValues.Length/4)];
        return high<=9*Math.Max(1e-30,low);
    }
    private static void A4BandScores(A4Context c,A4Pulse a,A4Pulse b,int delay,out double gainScore,out double shapeScore,out int validBands,out double maximumExcessDb,out int unstableNoiseBands)
    {
        gainScore=shapeScore=1;validBands=0;maximumExcessDb=0;unstableNoiseBands=0;
        if(c.Bands==null||a.BandEnergy==null||b.BandEnergy==null)return;
        List<double> observed=new List<double>(),expected=new List<double>(),errors=new List<double>();
        double calTravel=c.Bands.TravelSeconds;
        double travelFraction=calTravel>0?delay/c.Fs/calTravel:delay/c.RoundTripExact;
        for(int band=0;band<c.Bands.Valid.Length;band++)
        {
            double beforeNoise,afterNoise;
            bool beforeStable=A4SideNoise(a.SpectralBefore,band,out beforeNoise),afterStable=A4SideNoise(b.SpectralAfter,band,out afterNoise);
            // Each physical end owns its own noise. A quiet incident guard must never erase a noisy reflection guard.
            bool changed=Math.Max(beforeNoise,afterNoise)>4*Math.Max(1e-30,Math.Min(beforeNoise,afterNoise));
            if(!beforeStable||!afterStable||changed){unstableNoiseBands++;continue;}
            double na=beforeNoise*(a.End-a.Start+1),nb=afterNoise*(b.End-b.Start+1);
            double ea=Math.Max(0,a.BandRawEnergy[band]-na),eb=Math.Max(0,b.BandRawEnergy[band]-nb);
            if(!c.Bands.Valid[band]||ea<=6*na||eb<=6*nb||ea<=0||eb<=0)continue;
            observed.Add(10*Math.Log10(eb/ea));expected.Add(c.Bands.GainDb[band]*travelFraction);
            errors.Add(A4BandErrorDb(ea,na)+A4BandErrorDb(eb,nb)+c.Bands.UncertaintyDb[band]*travelFraction);
        }
        validBands=observed.Count;if(validBands<1)return;
        double gainExcess=0,shapeExcess=0;
        for(int j=0;j<validBands;j++)
        {
            // A single CAL cannot separate endpoint reflection from cable loss. Always allow model slack.
            gainExcess=Math.Max(gainExcess,Math.Max(0,observed[j]-expected[j]-errors[j]-3));
            maximumExcessDb=Math.Max(maximumExcessDb,observed[j]-expected[j]-errors[j]);
            for(int k=0;k<j;k++)
                shapeExcess=Math.Max(shapeExcess,Math.Max(0,Math.Abs((observed[j]-observed[k])-(expected[j]-expected[k]))-errors[j]-errors[k]-3));
        }
        gainScore=Math.Exp(-.5*Math.Pow(gainExcess/3,2));
        shapeScore=Math.Exp(-.5*Math.Pow(shapeExcess/3,2));
    }
}
