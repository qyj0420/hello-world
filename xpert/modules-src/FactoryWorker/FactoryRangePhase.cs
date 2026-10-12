using System;
using System.Collections.Generic;
using System.Globalization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Ranging only: measured zero crossings, independent of the attenuation fit.
    // Never sets FrequencyValid, DecayValid, acceptance or hardware state.
    public sealed class FactoryRangePhaseResult
    {
        public bool Valid;
        public double FirstZeroMs = Double.NaN;
        public double PeriodMs = Double.NaN;
        public int ConfirmedCrossings;
        public string Detail = "";
    }

    internal static class FactoryRangePhase
    {
        internal static FactoryRangePhaseResult Analyze(BenchCapture c)
        {
            var r=new FactoryRangePhaseResult();
            if(c==null || c.Settings==null)return Fail(r,"本轮采集或采集配置缺失，无法评估量程窗口。");
            // Historical false only disabled this diagnostic feature. It is
            // not a wiring attestation or HV permit; assess actual raw evidence.
            if(c.VoltageRaw==null||c.VoltageRaw.Length==0)return Fail(r,"本轮缺少原始电压采样，无法评估量程窗口。");
            if(!FactorySettings.Positive(c.ArtRateHz))return Fail(r,"本轮电压采样率无效，无法建立量程窗口时基。");
            if(!FactorySettings.Positive(c.ArtFullScaleV)||c.ArtFullScaleV>10)return Fail(r,"本轮电压输入量程无效。");
            int begin=c.Settings.ArtPreTriggerCount, count=c.VoltageRaw.Length-begin;
            if(begin<0 || count<32)return Fail(r,"触发后电压记录不足，不能确定首过零。");
            var v=new double[count];var curvature=new double[count-2];double peak=0;
            for(int i=0;i<count;i++)
            {
                double raw=c.VoltageRaw[begin+i];
                if(!FactorySettings.Finite(raw) || Math.Abs(raw)>=c.ArtFullScaleV*.995)
                    return Fail(r,"原始振荡电压无效或触及输入边界。");
                // Only phase detection is smoothed. Original voltage and raw PD
                // samples remain unchanged, including every clipping flag.
                v[i]=(c.VoltageRaw[begin+Math.Max(0,i-1)]+raw+
                    c.VoltageRaw[begin+Math.Min(count-1,i+1)])/3;
                peak=Math.Max(peak,Math.Abs(v[i]));
                if(i>0 && i<count-1)curvature[i-1]=Math.Abs(c.VoltageRaw[begin+i+1]-2*raw+c.VoltageRaw[begin+i-1]);
            }
            double noise=Median(curvature)/(.67448975*Math.Sqrt(6));
            if(!(peak>Math.Max(1e-6,noise*10)))return Fail(r,"电压信噪比不足，未把背景噪声作为振荡。");
            double gate=Math.Max(peak*.05,noise*4), candidate=Double.NaN;
            int state=0;var crossings=new List<double>();
            for(int i=0;i<count;i++)
            {
                if(i>0 && ((v[i-1]<=0 && v[i]>0)||(v[i-1]>=0 && v[i]<0)))
                    candidate=i-1-v[i-1]/(v[i]-v[i-1]);
                int next=v[i]>gate?1:v[i]<-gate?-1:state;
                if(state!=0 && next!=state && FactorySettings.Finite(candidate))crossings.Add(candidate);
                if(next!=0)state=next;
            }
            // One complete cycle corroborates the FIRST crossing; never skip an
            // inconsistent first crossing and quietly select a later oscillation.
            if(crossings.Count<3)return Fail(r,"首过零后缺少至少一个完整周期的双极性过零证据。");
            var halves=new double[crossings.Count-1];
            for(int i=0;i<halves.Length;i++)halves[i]=crossings[i+1]-crossings[i];
            double half=Median((double[])halves.Clone());
            if(half<8)return Fail(r,"每周期有效电压样点不足。");
            foreach(double h in halves)if(Math.Abs(h/half-1)>.20)
                return Fail(r,"首过零后的半周期不一致，不能把突变或噪声当作稳定振荡。");
            r.Valid=true;r.FirstZeroMs=(begin+crossings[0])*1000/c.ArtRateHz;
            r.PeriodMs=2*half*1000/c.ArtRateHz;r.ConfirmedCrossings=crossings.Count;
            r.Detail="量程独立首过零 "+r.FirstZeroMs.ToString("0.######",CultureInfo.InvariantCulture)+
                " ms；"+r.ConfirmedCrossings+" 个幅值门槛确认过零；不依赖阻尼拟合或8周期衰减，不改变衰减结果。";
            return r;
        }
        private static FactoryRangePhaseResult Fail(FactoryRangePhaseResult r,string detail){r.Detail=detail;return r;}
        private static double Median(double[] x){Array.Sort(x);int m=x.Length/2;return x.Length%2==0?(x[m-1]+x[m])/2:x[m];}
    }
}
