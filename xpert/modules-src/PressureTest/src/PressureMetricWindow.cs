using System;
using Xiongxin.OWTS.Data;

namespace HVX.Controls
{
    internal static class PressureMetricWindow
    {
        // Display/measurement scope only; raw ADC, clipping and protection
        // always retain the complete segment, including the switching transient.
        internal static double PeakAfterFirstCrossing(double[] voltage,double voltageRate,double[] charge,double chargeRate)
        {
            if(voltage==null||charge==null||charge.Length==0||!Positive(voltageRate)||!Positive(chargeRate))return Double.NaN;
            var window=OscillationCycleWindow.Find(voltage,voltageRate,1,0);
            if(!window.Success)return Double.NaN;
            double end=Math.Min((voltage.Length-1)/voltageRate,(charge.Length-1)/chargeRate);
            double duration=Math.Floor((end-window.CrossingSeconds[0])*1000+1e-9)/1000;
            if(duration<.001)return Double.NaN;
            end=window.CrossingSeconds[0]+duration;
            int first=(int)Math.Ceiling(window.CrossingSeconds[0]*chargeRate-1e-9);
            int last=Math.Min(charge.Length-1,(int)Math.Ceiling(end*chargeRate-1e-9)-1);
            if(first<0||first>last)return Double.NaN;
            double peak=0;
            for(int i=first;i<=last;i++){if(!Finite(charge[i]))return Double.NaN;peak=Math.Max(peak,Math.Abs(charge[i]));}
            return peak;
        }
        internal static bool TanDeltaEligible(double[] voltage,double voltageRate,double phaseVoltageRms)
        {
            if(voltage==null||!Positive(voltageRate)||!Positive(phaseVoltageRms))return false;
            var window=OscillationCycleWindow.Find(voltage,voltageRate,1,0);
            if(!window.Success)return false;
            int start=Math.Max(0,(int)Math.Ceiling(window.CrossingSeconds[0]*voltageRate-1e-9));
            double peak=0;
            for(int i=start;i<voltage.Length;i++){if(!Finite(voltage[i]))return false;peak=Math.Max(peak,Math.Abs(voltage[i]));}
            return peak>=0.7*phaseVoltageRms*Math.Sqrt(2.0);
        }
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static bool Positive(double value){return Finite(value)&&value>0;}
    }
}
