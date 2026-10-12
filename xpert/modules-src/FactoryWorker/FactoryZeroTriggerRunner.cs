using System;
using System.Collections.Generic;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Background/synchronization diagnostic, not an HV sequence or a discharge
    // command. Native initialization in this mode can create only P0.7 output.
    internal static class FactoryZeroTriggerRunner
    {
        internal static FactoryResult Run(FactorySettings requested, IFactoryBackend backend,
            IFactoryClock clock, Action<string,string> report, CancellationToken token)
        {
            var s=requested.Clone();
            if (!s.ZeroSynchronizedOptical || s.TargetKv!=0) throw new ArgumentException("需要明确选择0 kV同步光触发诊断。");
            s.Validate(FactoryRunKind.Pressure);
            report=report ?? delegate { };
            var trend=new FactoryFeedbackTrendPublisher(s,clock,report);
            backend=new FactoryEmergencyReportingBackend(backend,report,s.Simulation,trend.Observe);
            var r=new FactoryResult { Settings=s,Kind=FactoryRunKind.Pressure,ZeroTriggerDiagnostic=true,
                SafetyDetail="0 kV外部数字触发诊断：只操作P0.7，不操作HV、AO及其他数字输出；不是故障放电，不代表设备已放电或完成带电验收。" };
            var failures=new List<Exception>(); string stage="INIT";
            Action<string,string> begin=(key,text)=>{stage=key;report("STEP_"+key+"_RUNNING",text);};
            Action<string> pass=text=>report("STEP_"+stage+"_PASSED",text);
            try
            {
                token.ThrowIfCancellationRequested();
                begin("INIT","初始化两卡与P0.4独立监测，仅创建P0.7光触发输出并置低；不创建HV/AO/P0.2/P0.3输出。");
                backend.Initialize(s,false); backend.Shutdown(); pass("初始化调用完成；这里只能回低光触发，不能启停直流源。");
                FactoryRunner.WaitForCaptureCooldown(s.Simulation,report,token,()=>Emergency(backend));
                foreach(string key in new[]{"AO_ZERO","HV_ON","HV_DELAY","HV_UP","HV_OFF"})
                    report("STEP_"+key+"_SKIPPED","0 kV同步光触发诊断不执行此输出，不是放电流程。");
                begin("SAFE","检查P0.4高及新鲜有效的AI0/AI2/AI4，连续确认初始残压；现场须独立确认断源与放电。");
                FactoryRunner.WaitInitialResidual(s,backend,clock,r,report,token); pass("初始反馈检查通过，不等于独立放电确认。");
                begin("ARM","开始计时，停止/清除三路监测，配置ART AI4 PFI0与Pico A EXT数字触发并启动；禁止自由采集及超时自动触发替代。");
                token.ThrowIfCancellationRequested(); Emergency(backend);
                long armStarted=clock.ElapsedMilliseconds;
                backend.Arm(false);
                if(clock.ElapsedMilliseconds-armStarted>500) throw new TimeoutException("0 kV两卡布置超过500 ms；回低P0.7，不发光。");
                pass("ART/Pico启动调用通过；不是硬件等待触发状态的独立回读。");
                begin("ARM_DELAY","从布置开始累计等待至少 "+Math.Max(500,s.Input.ArmDelayMs)+" ms；期间检查急停及提前完成状态。");
                FactoryRunner.WaitArmBeforeShutdown(s,backend,clock,r,report,armStarted,token);
                pass("布置计时完成；HV OFF/AO输出仍跳过。");
                begin("OPTICAL","仅P0.7光触发一次，正常保持" + BenchSettings.OpticalHoldMilliseconds + " ms；共用物理检测信号应进入ART PFI0和Pico EXT。");
                token.ThrowIfCancellationRequested(); Emergency(backend);
                long lightStarted=clock.ElapsedMilliseconds;
                Exception opticalError=null;
                try
                {
                    backend.SetOptical(true); lightStarted=clock.ElapsedMilliseconds;
                    FactoryRunner.GuardedDelay(BenchSettings.OpticalHoldMilliseconds,backend,clock,token);
                }
                catch(Exception ex){opticalError=ex;}
                finally
                {
                    try{backend.SetOptical(false);}
                    catch(Exception ex){opticalError=opticalError==null?ex:new AggregateException("光触发及回低均报告错误。",opticalError,ex);}
                }
                if(opticalError!=null)throw opticalError;
                pass("光高保持请求结束并回低；驱动返回不等于物理光输出或实际放电回读。");
                begin("WAIT_TRIGGER","等待两卡完整段；分别显示完成状态，不自动重发光。");
                bool? lastArt=null,lastPico=null;
                while(true)
                {
                    token.ThrowIfCancellationRequested(); Emergency(backend);
                    bool a=backend.ArtComplete,p=backend.PicoComplete;
                    if(a!=lastArt || p!=lastPico) report("CAPTURE_STATUS","ART "+(a?"完整段已完成":"未完成")+"；Pico "+(p?"完整段已完成":"未完成")+"。未完成不等于已经证实正在等触发。");
                    lastArt=a;lastPico=p;
                    if(clock.ElapsedMilliseconds-lightStarted>=s.Input.TriggerTimeoutMs)
                        throw new TimeoutException("0 kV同步光触发采集超时：ART="+a+"，Pico="+p+"；不自动触发替代、不重发光。");
                    if(a&&p)break;
                    clock.Pause(10,token);
                }
                pass("两卡完整段均完成；物理共用触发接线及时间对齐仍需独立核验。");
                begin("READ","读取ART振荡电压和Pico原始局放完整段；检波80 kS/s，保留原始数据。");
                r.Capture=backend.ReadCapture();r.Capture.ValidateCapture();
                r.Capture.Runtime+=" | ZERO_EXTERNAL_OPTICAL_DIAGNOSTIC; NO_HV_AO_COMMANDS; NOT_DISCHARGE_CONFIRMATION";
                backend.StopCapture();pass("两路完整点数和时基校验通过。");
                report("STEP_DISCHARGE_SKIPPED","本模式不是放电流程；光触发成功和采集完成均不能证明储能已释放。");
                FactoryZeroVoltageObservation.Evaluate(r,report);
                if(s.DesktopPostProcessing)
                {
                    r.FullMetricsDeferred=true;
                    r.Metrics=new FactoryMetrics {RecommendedRangeIndex=s.Input.RangeIndex,
                        Detail="Full metrics deferred to the desktop saved-record pipeline."};
                    report("POST_PROCESSING_DEFERRED","Raw optical reference capture retained; desktop processing remains pending.");
                }
                else r.Metrics=FactoryAnalysis.Analyze(r);
            }
            catch(Exception ex){failures.Add(ex);}
            finally
            {
                try{backend.Shutdown();}catch(Exception ex){failures.Add(ex);}
                try{backend.Dispose();}catch(Exception ex){failures.Add(ex);}
                try{trend.Flush();}catch(Exception ex){failures.Add(ex);}
            }
            if(failures.Count>0)
            {
                report("STEP_"+stage+"_ERROR",failures[0].Message);
                report("STEP_COMPLETE_ERROR","同步光触发诊断未完成；未自动重连或重发光，未确认放电。");
                throw new FactoryRunException(r,failures);
            }
            token.ThrowIfCancellationRequested();r.Completed=true;
            report("STEP_COMPLETE_PASSED",r.SafetyDetail);return r;
        }
        private static void Emergency(IFactoryBackend backend)
        {if(!backend.ReadEmergencyReleased())throw new InvalidOperationException("P0.4低或不可用，禁止光输出。");}
    }
}
