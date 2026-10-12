using System;
using Xiongxin.Scope;

namespace Xiongxin.Calibration
{
    internal sealed class SignalQuality
    {
        internal double DurationMs, NoiseRmsV, MaxAbsV, Fundamental50V;
        internal int UsedSamples;
        internal static SignalQuality FromCapture(Capture c, double exclusionV)
        {
            if(c==null || c.Info==null || c.A==null || c.Info.MaxAdc<=0 || c.Info.IntervalNs<=0)throw new ArgumentException("没有有效的质量波形。");
            double dt=c.Info.IntervalNs*1e-9,scale=Settings.Ranges[c.Info.Config.A.Range]/c.Info.MaxAdc;
            double offset=CaptureVoltage.MeanAdc(c.A);
            int stride=Math.Max(1,c.A.Length/200000);
            double peak=0;
            for(int i=0;i<c.A.Length;i+=stride)peak=Math.Max(peak,Math.Abs((c.A[i]-offset)*scale));
            double sum=0,sum2=0,ss=0,cc=0,ys=0,yc=0;
            int count=0;double cutoff=Math.Max(exclusionV,peak*.1);
            for(int i=0;i<c.A.Length;i+=stride)
            {
                double y=(c.A[i]-offset)*scale;
                if(Math.Abs(y)>cutoff)continue; // incident/calibration pulses stay in raw plot
                double phase=2*Math.PI*50*i*dt,s=Math.Sin(phase),co=Math.Cos(phase);
                sum+=y;sum2+=y*y;ss+=s*s;cc+=co*co;ys+=y*s;yc+=y*co;count++;
            }
            if(count<100)throw new InvalidOperationException("有效噪声样本不足，检查输入量程和脉冲剔除阈值。");
            double mean=sum/count;
            return new SignalQuality {DurationMs=c.A.Length*dt*1000,NoiseRmsV=Math.Sqrt(Math.Max(0,sum2/count-mean*mean)),
                MaxAbsV=peak,Fundamental50V=Math.Sqrt(Math.Pow(ys/Math.Max(1,ss),2)+Math.Pow(yc/Math.Max(1,cc),2)),UsedSamples=count};
        }
    }
}
