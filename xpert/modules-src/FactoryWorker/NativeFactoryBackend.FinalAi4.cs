using System;
using System.Collections.Generic;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class NativeFactoryBackend
    {
        public FactoryFinalAi4Observation ReadFinalAi4Observation(CancellationToken token)
        {
            EnsureInitialized();
            if(settings.SkipPressureResidualMeasurement)
                throw new InvalidOperationException("加压后自动残压测量已禁用；不创建末轮AI4新采任务，电压及放电状态未知。");
            if (!roundActive || calibration || faulted || !settings.FinalSequenceAi4Observation ||
                settings.SequenceRound!=settings.SequenceCount || !settings.UseCaptureOnlyEnd ||
                hvOn || hvCommands.MayBeOn || opticalOn || lastAo!=0 || !opticalAttempted ||
                finite.Handle!=IntPtr.Zero || monitor.Handle!=IntPtr.Zero || pico.Armed)
                throw new InvalidOperationException("末轮AI4观察条件不满足；不能并行占用AI、恢复输出或读取旧段。");
            var failures=new List<Exception>();
            FactoryFinalAi4Observation observation=null;
            try
            {
                token.ThrowIfCancellationRequested();
                if(!ReadEmergencyReleased())throw new InvalidOperationException("末轮AI4观察前P0.4许可不满足。");
                using(IoTiming.BeginOperation("FinalSequenceAI4Mean10ms"))
                {
                    // A brand-new finite input task, after the original waveform
                    // task was cleared. No trigger, AO, DO, Pico arm or reconnect.
                    CreateTask(finite);
                    double range=FactoryArtFeedbackPlan.DividerFullScale(settings);
                    Check(InputOnlyArtNative.CreateAi(finite.Handle,Physical("ai4"),String.Empty,Rse,-range,range,Volts,null),"末轮AI4只读分压器量程±"+range+"V");
                    finite.Configured=true;
                    Check(InputOnlyArtNative.ConfigureClock(finite.Handle,String.Empty,BenchSettings.ArtRate,Rising,FiniteSamples,800),"末轮AI4 800点10ms");
                    Check(InputOnlyArtNative.TaskControl(finite.Handle,2),"验证末轮AI4有限输入");
                    Check(InputOnlyArtNative.TaskControl(finite.Handle,3),"提交末轮AI4有限输入");
                    FactoryArtFeedbackNative.VerifyDividerRange(finite.Handle,Physical("ai4"),range,Check);
                    double rate;
                    Check(InputOnlyArtNative.GetTimingRate(finite.Handle,0x1344,out rate,sizeof(double)),"回读末轮AI4采样率");
                    if(!FactorySettings.Finite(rate) || Math.Abs(rate-BenchSettings.ArtRate)>.5)
                        throw new InvalidOperationException("末轮AI4实际采样率不符，未启动诊断采样。");
                    token.ThrowIfCancellationRequested();
                    if(!ReadEmergencyReleased())throw new InvalidOperationException("末轮AI4启动前P0.4许可不满足。");
                    DateTime started=DateTime.UtcNow;
                    StartTask(finite);
                    var raw=new double[800]; int received;
                    Check(InputOnlyArtNative.ReadAnalog(finite.Handle,800,DriverIoTimeoutSeconds,0,raw,800,out received,IntPtr.Zero),"读取末轮AI4新段");
                    DateTime ended=DateTime.UtcNow;
                    if(received!=800)throw new InvalidOperationException("末轮AI4样点不足，不能用零填充计算均值。");
                    observation=FactoryFinalAi4Observation.Evaluate(raw,rate,settings.Input.DividerVPerV,false,started,ended,range);
                    token.ThrowIfCancellationRequested();
                    if(!ReadEmergencyReleased())throw new InvalidOperationException("末轮AI4读取后P0.4许可不满足。");
                }
            }
            catch(Exception ex){failures.Add(ex);faulted=true;}
            finally
            {
                Try(()=>ClearTask(finite),failures);
                finiteArmed=finite.Handle!=IntPtr.Zero;
            }
            if(failures.Count!=0)
            {faulted=true;throw new FactoryFinalAi4ObservationException(observation,
                new AggregateException("末轮AI4诊断或清理失败，未确认放电。",failures));}
            return observation;
        }
    }
}
