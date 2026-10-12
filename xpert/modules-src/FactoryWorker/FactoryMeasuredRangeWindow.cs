using System;
using System.Globalization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Factory ranging is independent of the N-cycle attenuation measurement.
    internal static class FactoryMeasuredRangeWindow
    {
        internal static LocationRangeWindowResult SelectCycles(bool whole, int count, double rate,
            int preTrigger, FactoryRangePhaseResult phase, int cycles)
        {
            if(whole)return Select(true,count,rate,preTrigger,false,Double.NaN);
            var r=new LocationRangeWindowResult { RequestedCycles=cycles, ActualSampleRateHz=rate };
            if(count<=0 || !FactorySettings.Positive(rate) || preTrigger<0 || preTrigger>count || cycles<1 || cycles>100)
                return Invalid(r,"定位周期数、采样率或记录边界无效。");
            double duration=count*1000.0/rate;
            if(phase==null || !phase.Valid || !FactorySettings.Finite(phase.FirstZeroMs) ||
                !FactorySettings.Positive(phase.PeriodMs) || phase.FirstZeroMs<preTrigger*1000.0/rate)
                return Invalid(r,"实际记录 "+F(duration)+" ms；缺少可靠首过零："+(phase==null?"未计算":phase.Detail));
            double endMs=phase.FirstZeroMs+cycles*phase.PeriodMs;
            string timing="定位保存 "+cycles+" 周期；首过零 "+F(phase.FirstZeroMs)+" ms；周期 "+F(phase.PeriodMs)+
                " ms；所需记录至少 "+F(endMs)+" ms / 实际 "+F(duration)+" ms";
            if(!FactorySettings.Finite(endMs) || phase.FirstZeroMs>=duration)
                return Invalid(r,timing+"；首过零后没有有效样点。");
            r.Shortened=endMs>duration;
            if(r.Shortened)endMs=duration;
            r.StartSample=(int)Math.Ceiling(phase.FirstZeroMs*rate/1000);
            r.EndExclusive=Math.Min(count,(int)Math.Ceiling(endMs*rate/1000));
            r.Count=r.EndExclusive-r.StartSample;
            if(r.Count<=0 || r.EndExclusive>count)return Invalid(r,timing+"；样点不足。");
            r.Valid=true;r.StartTimeSeconds=r.StartSample/rate;r.EndTimeSeconds=r.EndExclusive/rate;
            r.Definition=timing+(r.Shortened?"；N周期不足，使用首过零至实际末尾的有限长度":"；已覆盖设置的N周期")+
                "；实际使用 "+F(r.Count*1000.0/rate)+" ms；原始样点 ["+r.StartSample+", "+r.EndExclusive+
                ")；保存及量程使用同一窗口，窗外削顶单列保留。";
            return r;
        }
        internal static LocationRangeWindowResult Select(bool whole, int count, double rate,
            int preTrigger, bool phaseValid, double firstZeroMs)
        {
            var r = new LocationRangeWindowResult { ActualSampleRateHz = rate };
            if (count <= 0 || !FactorySettings.Positive(rate) || preTrigger < 0 || preTrigger > count)
                return Invalid(r, "实际采样率、记录长度或触发位置无效。");
            double duration = count * 1000.0 / rate;
            string actual = "实际记录 " + F(duration) + " ms；";
            if (!whole && (!phaseValid || !FactorySettings.Finite(firstZeroMs) ||
                firstZeroMs < preTrigger * 1000.0 / rate || firstZeroMs >= duration))
                return Invalid(r, actual + "所需：经验证的振荡第一过零点及其后的原始样点；本轮相位缺失或不在触发后有效记录内。");
            int start = whole ? 0 : (int)Math.Ceiling(firstZeroMs * rate / 1000);
            if (start >= count) return Invalid(r, actual + "第一过零点后无可用样点。");
            r.Valid = true; r.StartSample = start; r.Count = count - start; r.EndExclusive = count;
            r.StartTimeSeconds = start / rate; r.EndTimeSeconds = count / rate;
            r.Definition = actual + (whole ? "整段原始输入" : "第一过零点 " + F(firstZeroMs) + " ms 起至实际记录末尾") +
                "；原始样点 [" + start + ", " + count + ")；有效时长 " + F(r.Count * 1000.0 / rate) +
                " ms。量程不要求8周期；8周期只用于衰减测量。窗口内削顶仍计入。";
            return r;
        }
        private static string F(double v) { return v.ToString("0.######", CultureInfo.InvariantCulture); }
        private static LocationRangeWindowResult Invalid(LocationRangeWindowResult r, string reason)
        { r.FailureReason = reason; r.Definition = "无有效量程窗口：" + reason; return r; }
    }
}
