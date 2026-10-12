using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryFaultResidualRecord
    {
        public bool Started, WithinThreshold;
        public int Consecutive;
        public long ElapsedMilliseconds;
        public string Status="未执行故障残压观察";
        public List<FactoryFeedback> Feedback=new List<FactoryFeedback>();
    }
    internal static class FactoryFaultResidual
    {
        // Called AFTER shutdown has been attempted and BEFORE session disposal.
        // There is deliberately no output API, reconnect, trigger or analysis here.
        // Even WithinThreshold never sets Completed or ResidualSafe on the failed run.
        internal static void Observe(FactorySettings s,IFactoryBackend b,IFactoryClock clock,
            FactoryResult result,Action<string,string> report)
        {
            if(s.SkipPressureResidualMeasurement)
            {
                result.FaultResidual=new FactoryFaultResidualRecord { Status="故障后自动残压测量已禁用；电压未知，须现场独立安全放电。" };
                result.SafetyDetail=result.FaultResidual.Status;
                Report(report,"FAULT_RESIDUAL_SKIPPED",result.SafetyDetail+"本轮仍失败，不重连、不恢复输出、不自动续轮。");
                return;
            }
            var record=result.FaultResidual=new FactoryFaultResidualRecord { Started=true };
            long start=clock.ElapsedMilliseconds,lastReport=-1000;
            double limit=Math.Min(30,s.DischargeTimeoutSeconds)*1000;
            var wall=Stopwatch.StartNew();
            try
            {
                Report(report,"STEP_DISCHARGE_RUNNING","异常输出关断已先行尝试；继续只读残压观察，阈值 "+s.EndResidualLimitV.ToString("F1")+" V，最长 "+(limit/1000).ToString("0.###")+" 秒；不重连、不加压、不发光。急停低不阻止读输入，不代表输出许可。");
                var monitor=b as IFactoryFaultMonitor;
                if(monitor==null)throw new InvalidOperationException("当前后端没有故障只读监测接口。");
                monitor.PrepareFaultMonitoring();
                while(clock.ElapsedMilliseconds-start<limit && wall.ElapsedMilliseconds<limit && record.Feedback.Count<1501)
                {
                    var f=monitor.ReadFaultFeedback();
                    FactoryRunner.ValidateFeedback(f,s,false);
                    if(clock.ElapsedMilliseconds-start>=limit || wall.ElapsedMilliseconds>=limit)
                        throw new TimeoutException("残压样本迟于观察时限，不能据此确认窗口内状态。");
                    if(f.BacklogAfter<0 || f.BacklogAfter>20)throw new InvalidOperationException("残压反馈积压或标记无效。");
                    record.Feedback.Add(f);
                    record.Consecutive=FactoryRunner.EndResidualOk(f,s)?record.Consecutive+1:0;
                    record.ElapsedMilliseconds=clock.ElapsedMilliseconds-start;
                    record.Status=String.Format(CultureInfo.InvariantCulture,
                        "故障只读残压：分压器 {0:F1} V，VMON {1:F1} V；阈值内连续 {2}/5，已记录 {3} 轮；不代表独立放电确认。",
                        f.DividerRawV*s.Input.DividerVPerV,f.VmonRawV*s.VmonFullScaleKv*1000/s.FeedbackFullScaleV,record.Consecutive,record.Feedback.Count);
                    if(record.ElapsedMilliseconds-lastReport>=100 || record.Consecutive>=5)
                    { Report(report,"FAULT_RESIDUAL",record.Status);lastReport=record.ElapsedMilliseconds; }
                    if(record.Consecutive>=5)
                    {
                        record.WithinThreshold=true;
                        record.Status="异常后选中 "+FactoryRunner.SelectedVoltageName(s)+"反馈连续5轮低于配置阈值；本轮仍失败，不等于物理隔离或独立放电确认。";
                        return;
                    }
                    clock.Pause((int)Math.Min(20,Math.Max(0,limit-(clock.ElapsedMilliseconds-start))),CancellationToken.None);
                }
                record.Status=record.Feedback.Count==0?"残压未知：观察期没有有效数据。":"残压未确认释放：观察时限内未连续5轮低于阈值；末次读数仅为历史记录。";
            }
            catch(Exception ex) { record.WithinThreshold=false;record.Status="残压未知／未确认释放："+ex.Message; }
            finally
            {
                record.ElapsedMilliseconds=clock.ElapsedMilliseconds-start;
                result.SafetyDetail=record.Status;
                Report(report,"FAULT_RESIDUAL_END",record.Status);
                // Never paint the failed run's discharge lamp green on diagnostics.
                Report(report,"STEP_DISCHARGE_ERROR",record.Status);
            }
        }
        private static void Report(Action<string,string> report,string key,string text)
        { try { if(report!=null)report(key,text); } catch { /* reporting cannot prevent read-only safety observation or cleanup */ } }
    }
}
