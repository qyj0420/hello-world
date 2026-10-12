using System;

namespace HVX.SignalEvidence
{
    // Complete frozen fixed-band support. Time is relative to a measured peak;
    // template comparison resamples time, never stretches a waveform to match.
    internal sealed class A7PulseDescriptor
    {
        internal bool Valid, FwhmValid, MultipleHalfHeightComponents;
        internal double[] Samples;
        internal int Peak;
        internal double SampleRateHz, Amplitude, Noise, EnergyWidthNs=double.NaN, FwhmNs=double.NaN;
    }
    internal static class A7WaveformMath
    {
        internal static A7PulseDescriptor Measure(double[] values,int start,int end,int peak,double baseline,double noise,double sampleRateHz)
        {
            var r=new A7PulseDescriptor();
            if(values==null||start<0||end>=values.Length||end<=start||peak<start||peak>end||
                !ConfidenceMath.Finite(baseline)||!ConfidenceMath.Finite(noise)||noise<=0||
                !ConfidenceMath.Finite(sampleRateHz)||sampleRateHz<6000000||end-start>65535)return r;
            r.Samples=new double[end-start+1];r.Peak=peak-start;r.SampleRateHz=sampleRateHz;r.Noise=noise;
            double max=0;
            for(int i=start;i<=end;i++)
            {
                double v=values[i]-baseline;if(!ConfidenceMath.Finite(v))return r;
                r.Samples[i-start]=v;max=Math.Max(max,Math.Abs(v));
            }
            if(max<5*noise||!ConfidenceMath.Finite(max)||max<=0)return r;
            double signed=r.Samples[r.Peak];if(Math.Abs(signed)<.5*max)return r;
            r.Amplitude=max;double energy=0;
            for(int i=0;i<r.Samples.Length;i++){double v=r.Samples[i]/max;energy+=v*v;}
            r.EnergyWidthNs=energy/sampleRateHz*1e9;
            int sign=signed>0?1:-1;double half=Math.Abs(signed)/2;
            int left=r.Peak,right=r.Peak,components=0;bool active=false;
            for(int i=0;i<r.Samples.Length;i++)
            {bool above=sign*r.Samples[i]>=half;if(above&&!active)components++;active=above;}
            while(left>0&&sign*r.Samples[left-1]>=half)left--;
            while(right<r.Samples.Length-1&&sign*r.Samples[right+1]>=half)right++;
            r.MultipleHalfHeightComponents=components>1;
            if(left>0&&right<r.Samples.Length-1)
            {
                double la=sign*r.Samples[left],lb=sign*r.Samples[left-1];
                double ra=sign*r.Samples[right],rb=sign*r.Samples[right+1];
                double l=left-1+(half-lb)/(la-lb),rr=right+(ra-half)/(ra-rb);
                if(ConfidenceMath.Finite(l)&&ConfidenceMath.Finite(rr)&&rr>l&&!r.MultipleHalfHeightComponents)
                {r.FwhmNs=(rr-l)/sampleRateHz*1e9;r.FwhmValid=true;}
            }
            r.Valid=ConfidenceMath.Finite(r.EnergyWidthNs)&&r.EnergyWidthNs>0;return r;
        }
        private static double At(A7PulseDescriptor p,double seconds)
        {
            double index=p.Peak+seconds*p.SampleRateHz;
            if(index<0||index>p.Samples.Length-1)return 0;
            int lo=(int)Math.Floor(index),hi=Math.Min(lo+1,p.Samples.Length-1);
            // A global polarity reversal is normalised consistently at the supplied
            // peak; within-packet temporal lobe signs remain unchanged.
            double scale=p.Samples[p.Peak]>=0?p.Amplitude:-p.Amplitude;
            return (p.Samples[lo]+(p.Samples[hi]-p.Samples[lo])*(index-lo))/scale;
        }
        internal static double Similarity(A7PulseDescriptor a,A7PulseDescriptor b)
        {
            if(a==null||b==null||!a.Valid||!b.Valid)return double.NaN;
            double fs=Math.Min(a.SampleRateHz,b.SampleRateHz);
            double begin=Math.Min(-a.Peak/a.SampleRateHz,-b.Peak/b.SampleRateHz);
            double end=Math.Max((a.Samples.Length-1-a.Peak)/a.SampleRateHz,(b.Samples.Length-1-b.Peak)/b.SampleRateHz);
            double steps=Math.Ceiling((end-begin)*fs);
            if(!ConfidenceMath.Finite(steps)||steps<2||steps>65535)return double.NaN;
            double aa=0,bb=0,ab=0;
            for(int k=0;k<=steps;k++)
            {double t=begin+k/fs,x=At(a,t),y=At(b,t);aa+=x*x;bb+=y*y;ab+=x*y;}
            if(aa<=0||bb<=0)return double.NaN;
            double rho=ab/Math.Sqrt(aa)/Math.Sqrt(bb);
            return ConfidenceMath.Finite(rho)?Math.Max(-1,Math.Min(1,rho)):double.NaN;
        }
    }
}
