using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private static double A4RawSigned(A4Context c,int index)
    {
        if(c.MeasurementSignal!=null&&c.MeasurementSignal.DetectionRaw!=null)
            return c.MeasurementSignal.DetectionRaw[index]-c.MeasurementSignal.DetectionCenter;
        return c.Raw[index]-c.RawCenter;
    }
    private static double A4RawCharge(A4Context c,int index){return Math.Abs(A4RawSigned(c,index));}
    private static double A4RawMeasurementBaseline(A4AttenuationMeasurement m,int index)
    {
        int local=index-m.SignalOrigin;
        if(m.RawBaselineSamples!=null&&local>=0&&local<m.RawBaselineSamples.Length)return m.RawBaselineSamples[local];
        return m.RawBaseline+m.RawSlope*(index-m.RawBaselineOrigin);
    }
    private sealed class A4MeasurementSignal
    {
        internal double[] Raw,DetectionRaw,Trend,BandSignal,BandRamp;
        internal double Fs,RawSigma,DifferenceSigma,DetectionCenter;
        internal int BlockLength,ExcludedSamples,UsableBlocks;
        internal bool ShortRecord;
        internal string Rule="fixed_long_blocks_two_pass_pulse_excluded_linear_trend";
    }

    // One deterministic linear frequency operator. Subtracting the fixed DC
    // reference before all padding avoids injecting a step from a constant record
    // into a zero-filled FFT tail. Every FFT cell is mirror-extended; no zero seam.
    private static double[] A4FrequencyFilter(double[] input,double fs,double low,double high,bool hardUpper)
    {
        if(input==null||input.Length==0||!Finite(fs)||fs<=0||!Finite(low)||!Finite(high)||
            low<0||high<=low||high>fs/2||input.Any(v=>!Finite(v)))
            throw new ArgumentException("Finite nonempty signal and 0 <= low < high <= Nyquist are required.");
        if(input.Length==1)return new[]{low>0?0:input[0]};
        double reference=Median(input);
        double edge=low>0?low:high;
        int pad=Math.Min(input.Length-1,Math.Max(64,(int)Math.Min(65536,fs/edge*4)));
        int n=1;while(n<input.Length+2L*pad)n=checked(n*2);
        double[] re=new double[n],im=new double[n];long period=2L*(input.Length-1);
        for(int k=0;k<n;k++)
        {
            long index=((long)k-pad)%period;if(index<0)index+=period;
            if(index>=input.Length)index=period-index;
            re[k]=input[(int)index]-reference;
        }
        A4Fft(re,im);
        for(int k=0;k<n;k++)
        {
            double f=Math.Min(k,n-k)*fs/n;
            double hp=low<=0?1:f<=0?0:1/Math.Sqrt(1+Math.Pow(low/f,8));
            double lp=hardUpper?(f>=high?0:f<=high*.9?1:.5*(1+Math.Cos(Math.PI*(f-high*.9)/(high*.1)))):
                high>=fs/2?1:1/Math.Sqrt(1+Math.Pow(f/high,8));
            double gain=hp*lp;re[k]*=gain;im[k]*=-gain;
        }
        A4Fft(re,im);double[] output=new double[input.Length];
        for(int k=0;k<output.Length;k++)output[k]=re[pad+k]/n+(low==0?reference:0);
        return output;
    }
    private static double[] A4MeasurementFixedBand(double[] raw,double fs)
    {return A4FrequencyFilter(raw,fs,10000,3000000,true);}

    private static double A4DifferenceNoise(double[] raw)
    {
        if(raw==null||raw.Length<3)return 0;
        double[] differences=new double[raw.Length-1];for(int i=1;i<raw.Length;i++)differences[i-1]=raw[i]-raw[i-1];
        double center=Median(differences);
        for(int i=0;i<differences.Length;i++)differences[i]=Math.Abs(differences[i]-center);
        return Math.Max(1e-15,1.4826*Median(differences)/Math.Sqrt(2));
    }
    private static double[] A4InterpolateTrend(int length,List<double> x,List<double> y)
    {
        double[] trend=new double[length];if(x.Count==0)return trend;
        if(x.Count==1){for(int i=0;i<length;i++)trend[i]=y[0];return trend;}
        int right=1;
        for(int i=0;i<length;i++)
        {
            while(right<x.Count-1&&i>x[right])right++;
            trend[i]=y[right-1]+(i-x[right-1])*(y[right]-y[right-1])/(x[right]-x[right-1]);
        }
        return trend;
    }
    private static A4MeasurementSignal A4PrepareMeasurementSignal(double[] raw,double fs,int roundTrip,
        IList<A4Pulse> knownPackets=null)
    {
        if(raw==null||raw.Length<3||raw.Any(v=>!Finite(v))||!Finite(fs)||fs<6000000)
            throw new ArgumentException("Shared measurement requires finite raw samples and sample rate >= 6 MHz.");
        var s=new A4MeasurementSignal {Raw=raw,Fs=fs};
        s.BlockLength=(int)Math.Min(int.MaxValue,Math.Max(Math.Ceiling(fs/10000),4L*Math.Max(1,roundTrip)));
        s.ShortRecord=raw.Length<2L*s.BlockLength;
        s.DifferenceSigma=A4DifferenceNoise(raw);
        var centers=new List<double>();var values=new List<double>();
        for(int start=0;start<raw.Length;start+=Math.Min(s.BlockLength,raw.Length-start))
        {
            int end=(int)Math.Min(raw.Length-1L,(long)start+s.BlockLength-1);
            centers.Add(((double)start+end)/2);values.Add(Median(A4Slice(raw,start,end)));
        }
        // A short record does not establish a long-timescale trend. Only remove
        // its fixed DC here; independent two-sided quiet guards may establish a
        // local linear slope for scoring later. No short-window high-pass is used.
        if(s.ShortRecord){centers=new List<double>{(raw.Length-1)/2d};values=new List<double>{Median(raw)};}
        s.Trend=A4InterpolateTrend(raw.Length,centers,values);
        bool[] excluded=new bool[raw.Length];
        if(knownPackets!=null)foreach(A4Pulse pulse in knownPackets)
            if(pulse!=null)for(int i=Math.Max(0,pulse.Start);i<=Math.Min(raw.Length-1,pulse.End);i++)excluded[i]=true;
        // Fixed independent raw activity threshold, never a pair score. Do not
        // estimate drift from the strongest packet or shrink a broad pulse.
        double gate=6*s.DifferenceSigma;
        for(int i=0;i<raw.Length;i++)if(Math.Abs(raw[i]-s.Trend[i])>gate)excluded[i]=true;
        if(!s.ShortRecord)
        {
            var quietX=new List<double>();var quietY=new List<double>();
            for(int start=0;start<raw.Length;start+=Math.Min(s.BlockLength,raw.Length-start))
            {
                int end=(int)Math.Min(raw.Length-1L,(long)start+s.BlockLength-1);
                var points=new List<double>();var indices=new List<double>();
                for(int i=start;i<=end;i++)if(!excluded[i]){points.Add(raw[i]);indices.Add(i);}
                if(points.Count<Math.Max(32,(end-start+1)/4))continue;
                quietX.Add(Median(indices.ToArray()));quietY.Add(Median(points.ToArray()));
            }
            if(quietX.Count>=2){s.Trend=A4InterpolateTrend(raw.Length,quietX,quietY);s.UsableBlocks=quietX.Count;}
            else s.Rule="insufficient_quiet_blocks_initial_long_block_trend_only";
        }
        else{s.Rule="short_record_fixed_DC_only_local_guard_slope_required";s.UsableBlocks=1;}
        s.ExcludedSamples=excluded.Count(v=>v);
        s.DetectionRaw=new double[raw.Length];double[] ramp=new double[raw.Length];
        for(int i=0;i<raw.Length;i++){s.DetectionRaw[i]=raw[i]-s.Trend[i];ramp[i]=(i-(raw.Length-1)/2d)/Math.Max(1,raw.Length-1);}
        s.DetectionCenter=Median(s.DetectionRaw);
        s.RawSigma=Math.Max(1e-15,RobustSigma(s.DetectionRaw,s.DetectionCenter));
        s.BandSignal=A4MeasurementFixedBand(s.DetectionRaw,fs);
        s.BandRamp=A4MeasurementFixedBand(ramp,fs);
        return s;
    }
    private static string A4MeasurementSignalJson(A4MeasurementSignal s)
    {
        if(s==null)return "null";
        return "{\"rule\":"+Json(s.Rule)+",\"fixed_band_hz\":[10000,3000000],\"block_samples\":"+s.BlockLength+
            ",\"usable_quiet_blocks\":"+s.UsableBlocks+",\"excluded_activity_samples\":"+s.ExcludedSamples+
            ",\"short_record\":"+(s.ShortRecord?"true":"false")+",\"raw_noise_sigma\":"+NumberJson(s.RawSigma)+
            ",\"difference_noise_sigma\":"+NumberJson(s.DifferenceSigma)+",\"raw_input_mutated\":false,\"pair_score_used\":false}";
    }
}
