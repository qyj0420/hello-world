using System;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryTargetHold
    {
        // No WriteAo / SetHv / SetOptical here. The first-reach AO remains frozen;
        // any exception escapes to the existing shutdown-only fault path.
        internal static void Verify(FactorySettings s, IFactoryBackend b, IFactoryClock clock,
            FactoryResult result, Action<string,string> report, CancellationToken token)
        {
            if (result.RampStatistics.ExitCode != "Reached" &&
                !(s.UsePrecisionRamp && result.RampStatistics.ExitCode=="ReachedWithPrecisionWarning"))
                throw new InvalidOperationException("仅首次达压后允许固定AO复核。");
            if(s.TargetHoldChecks==0 || s.UsePrecisionRamp && result.RampStatistics.PrecisionWarning)
            {
                result.TargetHold=new FactoryTargetHoldStatistics { FrozenAoV=result.RampStatistics.LastAoV,
                    Detail=result.RampStatistics.PrecisionWarning ? "AO冻结后的3个新反馈仍有精度偏差；按实测结果进入采集并保留警示，未声明精度合格。" :
                        "按设置取消多轮复核；首次有效达压，AO不再增加，进入下一步。" };
                report("TARGET_HOLD",result.TargetHold.Detail);
                return;
            }
            long started = clock.ElapsedMilliseconds;
            var stats = result.TargetHold = new FactoryTargetHoldStatistics { Started=true, FrozenAoV=result.RampStatistics.LastAoV };
            double minimum=s.TargetKv*1000*(1-s.EffectiveTargetTolerancePercent/100), maximum=s.RampUpperV;
            bool precisionDeviation=false;
            int precisionReads=0;
            DateTime precisionFeedbackUtc=DateTime.MinValue;
            try
            {
                report("TARGET_HOLD", "首次达压，AO锁定 " + stats.FrozenAoV.ToString("F4") + " V；连续复核 " + s.TargetHoldChecks + " 轮，不再增加AO。");
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    long left=s.TargetHoldTimeoutMs-(clock.ElapsedMilliseconds-started);
                    if(left<=0)throw new TimeoutException("固定AO达压复核超时；关断、AO归零，不自动发光。");
                    FactoryRunner.GuardedDelay((int)Math.Min(left,s.TargetHoldIntervalMs),b,clock,token);
                    var f=FactoryRunner.ReadChecked(b,s); result.TargetHoldFeedback.Add(f); stats.Reads++;
                    FactoryRunner.CheckRampVoltage(f,s,s.FeedbackCeilingKv*1000);
                    FactoryRunner.CheckPrecisionProtection(f,s);
                    double divider=Math.Abs(FactoryFeedbackWindow.Divider(f)*s.Input.DividerVPerV);
                    double vmon=FactoryFeedbackWindow.Vmon(f)*s.VmonFullScaleKv*1000/s.FeedbackFullScaleV;
                    double measured=FactoryRunner.SelectedVoltage(f,s);
                    if(!s.UsePrecisionRamp && measured>maximum)
                        throw new InvalidOperationException("固定AO复核窗口值超过达压容差上界；关断，不发光。");
                    if (clock.ElapsedMilliseconds-started >= s.TargetHoldTimeoutMs)
                        throw new TimeoutException("固定AO达压复核超时；迟到样本不替代按时验证。");
                    token.ThrowIfCancellationRequested();
                    if(!b.ReadEmergencyReleased())throw new FactoryEmergencyStopException();
                    bool within=measured>=minimum && measured<=maximum;
                    if(s.UsePrecisionRamp && precisionDeviation)
                    {
                        if(f.TimestampUtc<=precisionFeedbackUtc)continue;
                        precisionFeedbackUtc=f.TimestampUtc;precisionReads++;
                        if(within){precisionDeviation=false;precisionReads=0;}
                    }
                    stats.Consecutive=within?stats.Consecutive+1:0;
                    stats.ElapsedMilliseconds=clock.ElapsedMilliseconds-started;
                    stats.Detail="AO固定 " + stats.FrozenAoV.ToString("F4") + " V；达压复核 " + stats.Consecutive + "/" + s.TargetHoldChecks +
                        "，读取 " + stats.Reads + " 轮，用时 " + stats.ElapsedMilliseconds + " ms；选中 "+FactoryRunner.SelectedVoltageName(s)+" " + (measured/1000).ToString("F3") + " kV";
                    report("TARGET_HOLD",stats.Detail);
                    if(stats.Consecutive>=s.TargetHoldChecks)
                    {
                        stats.Passed=true;
                        if(s.UsePrecisionRamp)
                        {
                            result.RampStatistics.PrecisionEvaluated=true;result.RampStatistics.WithinPrecision=true;
                            result.RampStatistics.PrecisionWarning=false;result.RampStatistics.ExitCode="Reached";
                        }
                        return;
                    }
                    if(s.UsePrecisionRamp && !within)
                    {
                        if(!precisionDeviation){precisionDeviation=true;precisionFeedbackUtc=f.TimestampUtc;}
                        else if(precisionReads>=3)
                        {
                            result.RampStatistics.PrecisionEvaluated=true;result.RampStatistics.WithinPrecision=false;
                            result.RampStatistics.PrecisionWarning=true;result.RampStatistics.ExitCode="ReachedWithPrecisionWarning";
                            result.RampStatistics.PrecisionRecheckReads=precisionReads;
                            result.RampStatistics.ExitReason="固定AO后3个新反馈仍有精度偏差；继续采集并保留警示，不声明精度合格";
                            stats.Detail+="；精度偏差，不等于过压故障。";report("PRECISION_WARNING",stats.Detail);return;
                        }
                    }
                }
            }
            catch(Exception ex){stats.Detail=ex.Message;throw;}
            finally{stats.ElapsedMilliseconds=clock.ElapsedMilliseconds-started;}
        }
    }
}
