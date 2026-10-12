using System;
using System.Globalization;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryZeroVoltageObservation
    {
        public bool Evaluated, Warning;
        public double ThresholdV=50, MeanV, RmsV, AbsolutePeakV, TailMeanV, DurationMs;
        public int SampleCount;
        public string Detail;

        internal static FactoryZeroVoltageObservation Analyze(double[] raw,double rate,double ratio)
        {
            if(raw==null || raw.Length==0 || !FactorySettings.Positive(rate) || !FactorySettings.Positive(ratio))
                throw new ArgumentException("0 kV残压观察缺少有效波形或换算参数，残压未知。");
            var r=new FactoryZeroVoltageObservation { SampleCount=raw.Length, DurationMs=raw.Length*1000.0/rate };
            int tailCount=Math.Min(raw.Length,Math.Max(1,(int)Math.Min(Int32.MaxValue,rate*.010)));
            double sum=0,squares=0,tail=0;
            for(int i=0;i<raw.Length;i++)
            {
                double v=raw[i]*ratio;
                if(!FactorySettings.Finite(v) || Math.Abs(raw[i])>=9.95)
                    throw new InvalidOperationException("0 kV振荡电压通道非有限或接近±10 V饱和，残压未知。");
                sum+=v;squares+=v*v;r.AbsolutePeakV=Math.Max(r.AbsolutePeakV,Math.Abs(v));
                if(i>=raw.Length-tailCount)tail+=v;
            }
            r.MeanV=sum/raw.Length;r.RmsV=Math.Sqrt(squares/raw.Length);r.TailMeanV=tail/tailCount;
            r.Evaluated=true;r.Warning=r.AbsolutePeakV>r.ThresholdV;
            r.Detail=String.Format(CultureInfo.InvariantCulture,
                "0 kV残压观察{0}：{1:F1} ms窗口均值 {2:F1} V，末10 ms均值 {3:F1} V，RMS {4:F1} V，绝对峰值 {5:F1} V；阈值50 V。{6}这是采集时段记录，非实时放电确认。",
                r.Warning?"警告":"",r.DurationMs,r.MeanV,r.TailMeanV,r.RmsV,r.AbsolutePeakV,
                r.Warning?"检测到超阈值电压，可能含残压或噪声，需独立核实；":"本窗口未检出超阈值点，但不证明已放电；");
            return r;
        }
        internal static void Evaluate(FactoryResult result,Action<string,string> report)
        {
            var c=result.Capture;
            result.ZeroVoltageObservation=Analyze(c.VoltageRaw,c.ArtRateHz,c.Settings.DividerVPerV);
            result.SafetyDetail+=" " + result.ZeroVoltageObservation.Detail;
            report(result.ZeroVoltageObservation.Warning?"RESIDUAL_WARNING":"ZERO_VOLTAGE_OBSERVATION",result.ZeroVoltageObservation.Detail);
        }
    }
}
