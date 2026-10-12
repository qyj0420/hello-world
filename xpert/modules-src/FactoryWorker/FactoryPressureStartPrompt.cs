using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPressureStartPrompt
    {
        internal static void ValidateRangePreflight(FactorySettings s,int count)
        {
            if(count<=1 || s.TargetKv<=0)return;
            if(s.Input.PreTriggerPercent>=100)
                throw new System.ArgumentException("采集位置为全直流，没有振荡后段可评估下一轮量程；请将滑块从+50向振荡方向调整。普通量程不要求8周期。");
        }
        internal static string Timing(FactorySettings s)
        { return "记录 " + s.Input.CaptureDurationMs.ToString("0.###") + " ms，触发后 " +
            (s.Input.CaptureDurationMs-s.Input.PreTriggerDurationMs).ToString("0.###") +
            " ms；定位保存/量程使用首过零后 "+s.LocationSaveCycleCount+" 周期，不足取实际末尾；不要求8周期（除非定位N设为8）。衰减另需"+s.DecayCycleCount+"周期同极性峰，采后报告所需/实际时长。"; }
        internal static string Text(FactorySettings s, int count, bool remindRange)
        {
            string operation = s.ZeroSynchronizedOptical ? "0 kV 同步光触发诊断：只操作P0.7，不操作HV/AO。" :
                s.TargetKv == 0 ? "0 kV 只读采集：不操作HV/AO，不发光。" :
                "目标 " + s.TargetKv.ToString("0.000") + " kV · " + (count > 1 ? "连续 " + count + " 轮" : "单次加压") +
                " · Pico ±" + BenchSettings.Ranges[s.Input.RangeIndex] + " V\n量程" + (remindRange ? "调整时提醒" : "按有效评估自动调整下一轮") + "。";
            return operation + "\nART " + s.Input.ArtSerial + " / Pico " + s.Input.PicoSerial + "\n" + Timing(s) + "\n\n" +
                (s.AutomaticBatch ? "本次确认启动整批。正常完成一轮并保存、释放采集后，至少等待2秒自动进入下一轮，不再逐轮确认。\n整批期间人员保持撤离；停止、故障或断线会撤销批次，恢复连接不会自动恢复加压。\n仅最后一轮采AI4新数据10 ms，比较有符号均值的绝对值与1 kV；该读数不是人员进入许可。\n\n" : "") +
                (s.TargetKv == 0 && !s.ZeroSynchronizedOptical ? "采集不代表已放电。开始采集？" :
                "点击确定表示：" + (s.AutomaticBatch ? "批次开始前" : "本轮") + "已按现场程序" + (s.ZeroSynchronizedOptical ? "独立断源、" : "") + "独立放电、核对接线及换算，人员已撤离。开始？");
        }
    }
}
