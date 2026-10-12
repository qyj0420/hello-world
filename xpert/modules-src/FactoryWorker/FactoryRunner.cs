using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryClock : IFactoryClock
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        public long ElapsedMilliseconds { get { return stopwatch.ElapsedMilliseconds; } }
        public void Pause(int milliseconds, CancellationToken token)
        { if (milliseconds < 0) throw new ArgumentOutOfRangeException("milliseconds"); if (token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested(); }
    }
    internal static class FactoryRunner
    {
        internal static FactoryResult Run(FactorySettings requested, FactoryRunKind kind, IFactoryBackend backend,
            IFactoryClock clock, Action<string,string> report, CancellationToken cancellation,
            Action<FactoryResult> captureReady = null)
        {
            if (requested == null || backend == null || clock == null) throw new ArgumentNullException();
            if (kind == FactoryRunKind.Pressure && requested.TargetKv == 0)
                throw new ArgumentException("0 kV 使用 FactoryZeroInputRunner，不得进入带 AO/DO 的加压状态机。");
            var settings = requested.Clone();
            settings.PressureFeedbackContext=kind==FactoryRunKind.Pressure&&!settings.ManualZeroInputWithoutEmergency;
            if (kind == FactoryRunKind.Calibration)
            {
                settings.TargetKv = 0; // Manual calibration never adopts pressure-only selected-source rules.
                settings.Input.PreTriggerPercent=0;
                settings.EndAfterPressureCapture = false;
                settings.EndResidualCheckEnabled = false; // Percentage applies only AFTER a nonzero pressure run.
                settings.VmonAfterHvStartup = false;
                settings.ZeroBackgroundLimitV = 0; // Never relax calibration using the pressure noise allowance.
                settings.InitialResidualV = 0; // Commissioning's nonzero startup threshold never relaxes calibration.
                // Time-segment calibration uses only this requested <=60 ms
                // segment. A legacy target pulse count never extends/retries it.
            }
            settings.Validate(kind); // No native operation before parameter validation.
            if (report == null) report = delegate { };
            var trend = new FactoryFeedbackTrendPublisher(settings,clock,report);
            backend = new FactoryEmergencyReportingBackend(backend, report, settings.Simulation,trend.Observe);
            var result = new FactoryResult { Settings = settings, Kind = kind };
            Exception error = null;
            var cleanupErrors = new List<Exception>();
            string current = "INIT";
            bool hvAttempted = false;
            Action<string,string> begin = (stage, detail) => { current = stage; report("STEP_" + stage + "_RUNNING", detail); };
            Action<string> passed = detail => report("STEP_" + current + "_PASSED", detail);
            Action<string,string> skip = (stage, detail) => report("STEP_" + stage + "_SKIPPED", detail);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                begin("INIT", "精确校验两卡身份、型号及参数，之后才初始化安全输出；不切换为其他卡。");
                backend.Initialize(settings, kind == FactoryRunKind.Calibration);
                passed("两卡初始化调用通过；P0.2 固定低保护模式。驱动写入成功不等于独立物理输出回读。");
                begin("AO_ZERO", "请求 AO0=0 V、光触发低；按硬件默认 HV OFF，不提前发送 HV OFF 命令。");
                backend.Shutdown();
                passed("AO零与光低写入调用通过；HV ON之前不写HV OFF。未独立测量 AO0 物理电压。");
                // All cooldown waiting is before HV ON, never inside the
                // energized 500 ms arm-to-shutdown handoff.
                WaitForCaptureCooldown(settings.Simulation,report,cancellation,()=>CheckEmergency(backend));
                report("EMERGENCY_CONFIG", "P0.4独立读取，返回后至少10 ms；首次读取最多300 ms，明确SDK超时在500 ms无有效数据总预算内最多串行重读一次；低电平、其他驱动错误或总超时锁存停止，未知反馈不放行；Windows/USB非硬实时。");
                report("FEEDBACK_CONFIG", new FactoryArtFeedbackPlan(settings).Describe(settings));
                if(settings.SkipPressureResidualMeasurement)
                {
                    skip("SAFE", "启动前不启动或读取残压AI，不作软件残压判定；仍核验当前P0.4。开始前须按现场规程独立放电，未测量不代表零电压或已放电。");
                    report("RESIDUAL_POLICY", "加压前后自动残压测量已禁用；仅升压/达压保持读取配置选中电压。HVOFF、AO归零、急停、停止和限压仍执行；故障不自动续轮。");
                    cancellation.ThrowIfCancellationRequested();CheckEmergency(backend);
                }
                else if (settings.UsePostHvVmonStartup)
                {
                    begin("SAFE", settings.AutomaticBatch && settings.BatchContinuation ?
                        "同批次正常续轮：HV ON前不启动/读取AI、不判残压；核验当前P0.4及服务端批次许可，不补造本轮人工放电确认。" :
                        "HV ON前不启动/读取AI、不判残压；仅检查P0.4，开始前现场独立放电确认已由操作人完成（仿真不代表现场确认）。");
                    cancellation.ThrowIfCancellationRequested(); CheckEmergency(backend);
                    passed("P0.4高；本阶段未测AI、未作软件残压确认。AO保持0，下一步HV ON。");
                }
                else
                {
                    begin("SAFE", "HV OFF / AO=0 下连续监测；启动阈值 " + settings.InitialResidualLimitV.ToString("F1") + " V；先等待 " + settings.InitialFeedbackSettleMs + " ms，再连续5组确认初始残压；不自动扣零。结束残压另用 " + settings.EndResidualLimitV.ToString("F1") + " V。");
                    WaitInitialResidual(settings, backend, clock, result, report, cancellation);
                    passed("P0.4 高；归零等待后两路残压连续5组在阈值内，包含批次峰值与半码阶余量。这不是人员接近设备的授权。");
                }
                if (kind == FactoryRunKind.Pressure)
                {
                    begin("HV_ON", "再次检查 P0.4 高，然后 P0.0 高启动直流源；P0.2 保持低。");
                    CheckEmergency(backend); cancellation.ThrowIfCancellationRequested(); hvAttempted=true; backend.SetHv(true);
                    passed("HV ON 写入调用通过；非电源内部状态回读。");
                    begin("HV_DELAY", "AO保持0；启动后请求等待 " + settings.Input.HvDelayMs + " ms；期间持续检查急停。" +
                        (settings.SkipPressureResidualMeasurement ? "不启动/读取零输出背景或残压AI；此等待不是放电确认。" : settings.UsePostHvVmonStartup ? "VMON零输出上限 " + settings.StartupVmonLimitV.ToString("F1") +
                        " V；" + (settings.ZeroBackgroundLimitV > 0 ? "AI4启动/背景基准 " + settings.ZeroBackgroundLimitV.ToString("F1") + " V；VMON负向容差使用此背景基准，不引用采后残压。" : "AI4不参与零输出达标判定；") + "过流和独立过压保护保留。" : ""));
                    ZeroVoltageDelay(settings.Input.HvDelayMs, settings, backend, clock, result, report, cancellation);
                    passed("等待完成。");
                    begin("HV_UP", "目标 " + settings.TargetKv.ToString("0.000") + " kV；" +
                        "仅配置选中 " + SelectedVoltageName(settings) + " 窗口中位数闭环，另一电压仅作诊断；选中源原始峰值保护保留，首达后不再增加AO。" +
                        "原始极性保留；急停、持续过流、独立限压及持续时间限制继续执行。步长 " + settings.Input.AoStepV + " V，循环请求 " + settings.Input.LoopDelayMs + " ms。");
                    Ramp(settings, backend, clock, result, report, cancellation);
                    report("RAMP_SUMMARY",result.RampStatistics.Describe());
                    FactoryTargetHold.Verify(settings,backend,clock,result,report,cancellation);
                    // Publish the last real reached/hold window before arming
                    // stops monitoring; do not defer it to capture cleanup.
                    trend.Flush();
                    passed(SelectedVoltageName(settings) + (result.RampStatistics.PrecisionWarning ? "精度偏差复核结束，未声明精度合格，AO不再增加；" : "首次有效达压后AO不再增加；") + result.RampStatistics.Describe() + "；" + result.TargetHold.Detail);
                    begin("ARM", "升压循环已停止；开始布置时同时计时，停止/清除三路监测AI，重建AI4有限振荡采集并布置Pico EXT。两卡就绪且计时满设定值后才正常关断。切换期间无三路模拟监测，P0.4独立监测仍运行。");
                    long armStartedMs = ArmPressureCapture(backend,clock,cancellation);
                    passed("两卡启动等待共用检测触发的调用已返回；未发光，布置用时 " + (clock.ElapsedMilliseconds-armStartedMs) + " ms。该状态不是物理触发链路验收。");
                    begin("ARM_DELAY", "从开始布置采集起等待至少 " + Math.Max(500,settings.Input.ArmDelayMs) + " ms（包含布置耗时）；保持AO不再增加。急停、取消、布置异常立即关断，不等计时结束。");
                    WaitArmBeforeShutdown(settings,backend,clock,result,report,armStartedMs,cancellation);
                    passed("两卡布置已返回且从布置开始累计 " + (clock.ElapsedMilliseconds-armStartedMs) + " ms；执行HV OFF和AO归零。");
                    begin("HV_OFF", "P0.0 低并请求 AO0=0；保留回路储能用于振荡，不声称已经放电。");
                    backend.Shutdown(); passed("HV OFF / AO0=0 / 光低调用通过，两卡已在等待触发；不再追加500 ms等待，不声称物理关断已测量。");
                }
                else
                {
                    foreach (string stage in new[] { "HV_ON", "HV_DELAY", "HV_UP", "HV_OFF", "OPTICAL" })
                        skip(stage, "校准模式不启动高压、不升压、不发光；校准源为人工注入，程序只读脉冲。");
                }
                if (kind == FactoryRunKind.Pressure)
                {
                    CheckUnexpectedCapture(settings,backend,clock,result,report,cancellation);
                    begin("OPTICAL", "请求 P0.7 高 " + BenchSettings.OpticalHoldMilliseconds + " ms，正常结束后回低；急停/取消/错误可提前回低。");
                    CheckEmergency(backend); cancellation.ThrowIfCancellationRequested();
                    long triggerStart = clock.ElapsedMilliseconds;
                    Exception opticalFailure = null;
                    try { backend.SetOptical(true); triggerStart = clock.ElapsedMilliseconds; GuardedDelay(BenchSettings.OpticalHoldMilliseconds, backend, clock, cancellation); }
                    catch (Exception ex) { opticalFailure = ex; }
                    finally
                    {
                        try { backend.SetOptical(false); }
                        catch (Exception ex) { opticalFailure = opticalFailure == null ? ex : new AggregateException("光触发等待和回低均报告失败。", opticalFailure, ex); }
                    }
                    if (opticalFailure != null) throw opticalFailure;
                    passed("正常光触发保持请求已完成并回低；软件计时不是独立物理脉宽测量。");
                    begin("WAIT_TRIGGER", settings.UseOpticalTimeoutRetry ? "等待联动采集，期限3秒（从光触发返回起计）；纯超时且两卡均未完成时最多补发一次P0.7，补发段仅作诊断。" : "等待两卡完成同一同步检测边沿的完整段，无超时补发。");
                    if(settings.UseOpticalTimeoutRetry)
                        FactoryTriggerRecovery.WaitOrRecover(settings,backend,clock,result,triggerStart,report,cancellation);
                    else WaitCapture(settings, backend, clock, triggerStart, report, cancellation);
                    passed("两卡均完成；同一物理触发接线仍需现场核验。");
                }
                else
                {
                    begin("ARM", "校准自由长段采集 " + settings.Input.CaptureDurationMs.ToString("0.###") + " ms；两卡自由段起点不声明同步。");
                    CheckEmergency(backend); cancellation.ThrowIfCancellationRequested(); backend.Arm(true);
                    passed("校准两卡布置调用通过。");
                    skip("ARM_DELAY", "校准自由段不作布置后静默等待。");
                    begin("WAIT_TRIGGER", "校准源已人工注入；等待自由采集段完成，无 HV 或光输出。");
                    WaitCapture(settings, backend, clock, clock.ElapsedMilliseconds, report, cancellation);
                    passed("自由采集完成，后续按已知 pC 计算校准候选系数。");
                }
                begin("READ", "读取本轮完整原始 ADC 和电压，按实际率检波至 80 kS/s；保存原始信号，不抹除噪声和过冲。");
                result.Capture = backend.ReadCapture(); result.Capture.ValidateCapture();
                backend.StopCapture();
                passed("完整点数与实际时基校验通过；" + result.Capture.PdAdc.Length.ToString("N0") + " 个 Pico 原始点。");
                if (settings.ShouldCheckEndResidual)
                    begin("DISCHARGE", "HV OFF、AO0=0、光低后监测分压器与 VMON 残压；阈值 " + settings.EndResidualLimitV.ToString("F1") + " V，不使用启动阈值；不强行发光放电。");
                backend.Shutdown();
                if (!settings.ShouldCheckEndResidual)
                {
                    result.PostCaptureMonitoringSkipped = true;
                    result.SafetyDetail = settings.SkipPressureResidualMeasurement
                        ? "振荡与局放采集完成；按当前规则禁用采后及故障残压AI测量，不作软件残压判定。电压和放电状态未确认，必须按现场规程独立安全放电。"
                        : settings.FinalSequenceAi4Observation
                        ? "振荡与局放采集完成；跳过完整残压流程，另行执行末轮AI4新采10ms均值诊断。放电未确认，须现场独立确认。"
                        : "振荡与局放采集完成；采后不重新采集VMON/AI4，不作软件残压判定。放电未确认，须现场独立确认。";
                    skip("DISCHARGE", result.SafetyDetail);
                }
                // Publish only after the output shutdown call; disk work must not
                // delay output shutdown or the residual-monitoring loop.
                if (captureReady != null)
                {
                    try { captureReady(result); }
                    catch (Exception ex)
                    {
                        try { report("CAPTURE_PREVIEW_ERROR", "波形预览通知失败，关断及收尾流程继续：" + ex.Message); }
                        catch { /* A display failure cannot skip the residual loop. */ }
                    }
                }
                if (settings.ShouldCheckEndResidual)
                {
                long residualStart = clock.ElapsedMilliseconds;
                int safeCount = 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var feedback = ReadChecked(backend, settings);
                    bool safe = EndResidualOk(feedback, settings);
                    safeCount = safe ? safeCount + 1 : 0;
                    report("RESIDUAL", "分压器 " + Divider(feedback, settings).ToString("F2") + " V，VMON " + Vmon(feedback, settings).ToString("F2") + " V；连续安全 " + safeCount + "/5。");
                    if (safeCount >= 5) { result.ResidualSafe = true; break; }
                    if (clock.ElapsedMilliseconds - residualStart >= settings.DischargeTimeoutSeconds * 1000)
                        throw new InvalidOperationException("残压未在期限内降至阈值；未确认放电，禁止靠近设备。不会再次发光或自动重加压。");
                    clock.Pause(20, cancellation);
                }
                result.SafetyDetail = (settings.PressureFeedbackContext ? "选中 " + SelectedVoltageName(settings) : "两路") + "残压反馈连续 5 次低于阈值；仍需现场安全程序与独立放电确认。";
                passed(result.SafetyDetail);
                }
                if (kind == FactoryRunKind.Calibration)
                {
                    result.Calibration = FactoryCalibrationAdapter.Calibrate(result.Capture, settings.Input.CalibrationPulsePc,
                        settings.CalibrationFrequencyHz, settings.CalibrationPulseCount);
                    if (!result.Calibration.Valid) report("CALIBRATION_INVALID", result.Calibration.Detail);
                }
                if (settings.FinalSequenceAi4Observation && settings.ShouldCheckEndResidual)
                    skip("FINAL_AI4", "本轮已执行完整结束残压监测并保存真实反馈，不重复旧末轮AI4均值诊断；未伪造诊断记录。");
                if(settings.FinalSequenceAi4Observation && settings.SkipPressureResidualMeasurement)
                    skip("FINAL_AI4", "加压后自动残压测量禁用，不新采末轮AI4；未创建或伪造均值观察记录。电压未知，须现场独立安全放电。");
                if (settings.FinalSequenceAi4Observation && !settings.ShouldCheckEndResidual && !settings.SkipPressureResidualMeasurement)
                {
                    begin("FINAL_AI4", "连续末轮光触发结束、原采集停止且关断调用返回后，新采AI4 10ms；均值仅作诊断，不作放电或续压许可。");
                    result.FinalAi4Observation=new FactoryFinalAi4Observation { Requested=true,Simulation=settings.Simulation };
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var reader=backend as IFactoryFinalAi4Observation;
                        if(reader==null)throw new InvalidOperationException("当前后端缺少末轮AI4只读接口。");
                        result.FinalAi4Observation=reader.ReadFinalAi4Observation(cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        if(result.FinalAi4Observation==null)throw new InvalidOperationException("末轮AI4没有返回测量记录。");
                        report("FINAL_AI4_MEASUREMENT",result.FinalAi4Observation.Describe());
                        if(!result.FinalAi4Observation.Available)throw new InvalidOperationException(result.FinalAi4Observation.Error);
                        result.SafetyDetail="未执行完整残压确认。"+result.FinalAi4Observation.Describe();
                        passed("末轮AI4测量已记录；未赋予ResidualSafe或自动续压许可。");
                    }
                    catch(Exception ex)
                    {
                        var observedFailure=ex as FactoryFinalAi4ObservationException;
                        if(observedFailure!=null && observedFailure.PartialObservation!=null)
                            result.FinalAi4Observation=observedFailure.PartialObservation;
                        if(result.FinalAi4Observation==null)result.FinalAi4Observation=new FactoryFinalAi4Observation {Requested=true,Simulation=settings.Simulation};
                        result.FinalAi4Observation.Available=false;
                        result.FinalAi4Observation.AbsMeanBelowThreshold=false;
                        result.FinalAi4Observation.Error=ex.ToString();
                        throw;
                    }
                }
                if (kind == FactoryRunKind.Pressure && settings.DesktopPostProcessing)
                {
                    result.FullMetricsDeferred = true;
                    result.Metrics = new FactoryMetrics { RecommendedRangeIndex = settings.Input.RangeIndex,
                        Detail = "Full metrics deferred to the desktop saved-record pipeline." };
                    report("POST_PROCESSING_DEFERRED", "Raw capture retained; desktop filtering, detected record and saved-record analysis remain pending.");
                }
                else result.Metrics = FactoryAnalysis.Analyze(result);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                try
                {
                    backend.Shutdown();
                    if (error != null && hvAttempted)
                        SafeReport(report,"STEP_HV_OFF_PASSED","故障路径：HV OFF / AO=0 / 光低调用已返回；不是正常流程完成，不代表物理隔离或放电确认。",cleanupErrors);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add(ex);
                    if (error != null && hvAttempted)
                        SafeReport(report,"STEP_HV_OFF_ERROR","故障关断调用失败；状态未知："+ex.Message,cleanupErrors);
                }
                // Report the original failure after attempting shutdown but BEFORE
                // the bounded residual observation, so the UI cannot keep showing
                // HV UP running while DISCHARGE is already running for up to 30 s.
                if (error != null) SafeReport(report, "STEP_" + current + "_ERROR", error.Message, cleanupErrors);
                if (error != null && result.RampStatistics.Started)
                    SafeReport(report,"RAMP_SUMMARY",result.DescribeRampAndHold(),cleanupErrors);
                if (error != null && hvAttempted && !settings.ShouldCheckEndResidual)
                {
                    result.FaultResidual.Status="本流程不追加结束/故障AI残压采集；电压未知，必须按现场规程独立安全放电。";
                    SafeReport(report,"FAULT_RESIDUAL_SKIPPED",result.FaultResidual.Status+"保留原始故障，执行关断与资源释放，禁止自动恢复输出。",cleanupErrors);
                }
                if (error != null && hvAttempted && settings.ShouldCheckEndResidual && !result.PostCaptureMonitoringSkipped)
                {
                    try { FactoryFaultResidual.Observe(settings,backend,clock,result,report); }
                    catch (Exception ex) { cleanupErrors.Add(ex); result.SafetyDetail="异常残压监测失败；残压未知，未确认释放。"; }
                }
                try { backend.Dispose(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                try { trend.Flush(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                // Disable first; do not delay shutdown for disk/UI error reports.
                if (error == null && result.RampStatistics.Started) SafeReport(report,"RAMP_SUMMARY",result.DescribeRampAndHold(),cleanupErrors);
                // Reporting must never interrupt remaining safety cleanup attempts.
                foreach (var ex in cleanupErrors.ToArray()) SafeReport(report, "SAFETY_ERROR", "安全关断/资源清理未完整确认：" + ex.Message, cleanupErrors);
            }
            if (error != null || cleanupErrors.Count != 0)
            {
                SafeReport(report, "STEP_COMPLETE_ERROR", "本轮失败或安全清理不完整；不能声称设备已断电/放电，禁止自动继续加压。", cleanupErrors);
                if(!(error is FactoryLinkageFailureException) || cleanupErrors.Count!=0 || cancellation.IsCancellationRequested)
                    result.TriggerRecovery.EligibleForThresholdAdjustment=false;
                if (error != null) cleanupErrors.Insert(0, error);
                throw new FactoryRunException(result, cleanupErrors);
            }
            try
            {
                FactoryHealthyRoundStopEvidence.ThrowIfCanceledAfterRound(result,cancellation);
                if (result.PostCaptureMonitoringSkipped)
                {
                    result.AcquisitionCompleted = true;
                    report("STEP_COMPLETE_UNVERIFIED", result.FinalAi4Observation==null ? "采集及资源收尾完成；采后AI未重启。残压未测、放电未确认，不是安全验收通过；必须按现场规程独立安全放电。" : "采集及末轮AI4均值观察完成；完整残压/放电状态未确认，不是安全验收通过。");
                    FactoryHealthyRoundStopEvidence.ThrowIfCanceledAfterRound(result,cancellation);
                    return result;
                }
                report("STEP_COMPLETE_PASSED", "单次流程完成；不等于出厂计量认证，校准及比例核验结果见结果页。");
                FactoryHealthyRoundStopEvidence.ThrowIfCanceledAfterRound(result,cancellation);
                result.Completed = true;
                return result;
            }
            catch(FactoryRunException) { throw; }
            catch(Exception ex) { throw new FactoryRunException(result,new[]{ex}); }
        }
        internal static void WaitForCaptureCooldown(bool simulation,Action<string,string> report,
            CancellationToken token,Action checkSafety=null)
        {
            if(simulation)return;
            token.ThrowIfCancellationRequested();
            // A lost Stop/release acknowledgment cannot be repaired by time.
            if(FactoryCaptureCooldown.ReleasePending)FactoryCaptureCooldown.EnsureReady();
            int remaining=FactoryCaptureCooldown.RemainingMilliseconds;
            if(remaining<=0)return;
            if(report!=null)report("CAPTURE_COOLDOWN","Pico上一段已停止并释放缓冲或新会话已打开；冷却尚余至少"+remaining+
                " ms，在HV ON/新段布置前可取消等待；不作为物理放电或驱动恢复证明。");
            FactoryCaptureCooldown.Wait(token,null,checkSafety);
            if(report!=null)report("CAPTURE_COOLDOWN_PASSED","Pico共享单调时钟冷却已满足；仍须本轮急停、身份与输出许可检查。");
        }
        internal static long ArmPressureCapture(IFactoryBackend backend, IFactoryClock clock, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CheckEmergency(backend);
            long started = clock.ElapsedMilliseconds;
            backend.ArmBeforeShutdown();
            // Also enforce at the controller boundary, in addition to the native
            // per-SDK-call checks. A blocked SDK cannot be preempted by this check.
            if (clock.ElapsedMilliseconds-started > 500)
                throw new TimeoutException("两卡布置调用超过500 ms；执行异常关断，不继续等待或发光。软件检查不能抢占阻塞SDK。");
            return started;
        }
        internal static void WaitArmBeforeShutdown(FactorySettings settings, IFactoryBackend backend, IFactoryClock clock,
            FactoryResult result, Action<string,string> report, long armStartedMs, CancellationToken token)
        {
            int delay = Math.Max(500,settings.Input.ArmDelayMs);
            do
            {
                token.ThrowIfCancellationRequested(); CheckEmergency(backend);
                CheckUnexpectedCapture(settings,backend,clock,result,report,token);
                if (clock.ElapsedMilliseconds-armStartedMs >= delay) return;
                PauseRemaining(delay,armStartedMs,clock,token);
            } while (true);
        }
        internal static void CheckUnexpectedCapture(FactorySettings settings, IFactoryBackend backend, IFactoryClock clock,
            FactoryResult result, Action<string,string> report, CancellationToken token)
        {
            if (!backend.ArtComplete && !backend.PicoComplete) return;
            // This can now occur during the pre-OFF arming delay. Disable first,
            // before logging or waiting for diagnostic data; never wait with HV ON.
            backend.Shutdown();
            report("EARLY_TRIGGER", "光触发命令之前采集已完成/部分卡完成：保留诊断，不继续发光。两卡触发起点未经验证。");
            long start=clock.ElapsedMilliseconds;
            int wait=(int)Math.Min(1200,Math.Ceiling(settings.Input.CaptureDurationMs)+100);
            while ((!backend.ArtComplete || !backend.PicoComplete) && clock.ElapsedMilliseconds-start < wait)
            { token.ThrowIfCancellationRequested(); CheckEmergency(backend); clock.Pause(10,token); }
            if (backend.ArtComplete && backend.PicoComplete)
            {
                result.Capture=backend.ReadCapture(); result.Capture.ValidateCapture();
                result.Capture.Runtime += " | EARLY BEFORE OPTICAL: DIAGNOSTIC ONLY; shared trigger timing NOT validated";
                report("EARLY_CAPTURE_SAVED", "已取得提前触发完整段，将保存为失败记录；不是正常振荡试验通过，也不代表已放电。");
            }
            throw new InvalidOperationException(result.Capture != null ? "关断/等待阶段提前触发；完整原始段已保留为诊断，禁止继续光触发。" : "仅部分卡提前完成；缺少完整段，不补造数据，禁止继续光触发。");
        }
        internal static void Ramp(FactorySettings s, IFactoryBackend backend, IFactoryClock clock, FactoryResult result,
            Action<string,string> report, CancellationToken token)
        {
            long start = clock.ElapsedMilliseconds, lastReport = start - 100;
            double ao = 0;
            double target = s.TargetKv * 1000, targetMinimum = target * (1 - s.EffectiveTargetTolerancePercent / 100);
            double targetMaximum = s.RampUpperV;
            // Fixed increment ramp, capped at the explicitly authorized +5%
            // command envelope. Feedback acceptance/protection is unchanged.
            double ceiling = s.AoCommandCeilingV;
            report("AO_ENVELOPE", "标称AO=" + s.NominalAoV.ToString("F6") +
                " V；本轮AO上限=" + ceiling.ToString("F6") + " V；min(目标V/加压比×1.05,最大AO,5 V)，不是反馈过压阈值。");
            var stats = result.RampStatistics = new FactoryRampStatistics { Started = true,
                TargetMinimumV = targetMinimum, TargetMaximumV = targetMaximum, ExitCode = "Running", ExitReason = "升压中" };
            var fine = s.UseFinePressureRamp ? new UnifiedFinePressureRamp(s,stats,start) : null;
            bool precisionRecheck=false;
            DateTime precisionFeedbackUtc=DateTime.MinValue;
            if(s.UsePrecisionRamp)
            {
                stats.ProtectionUpperV=s.PrecisionProtectionUpperV;
                report("VOLTAGE_THRESHOLDS", "选中 "+SelectedVoltageName(s)+" 精度窗口±5%："+
                    (targetMinimum/1000).ToString("F4")+"～"+(targetMaximum/1000).ToString("F4")+
                    " kV（窗口统计）；异常保护上限 "+(s.PrecisionProtectionUpperV/1000).ToString("F4")+
                    " kV：min(目标×(1+"+s.RelativeOvervoltagePercent.ToString("F1")+"%),独立限压)，使用选中源原始峰值及半码余量；AO命令上限独立保持。");
            }
            var currentGuard = new FactoryRampCurrentGuard(target,start);
            report("CURRENT_GUARD_CONFIG", "仅当选中电压连续3秒无有效增长且AI0原始电流反馈连续3秒达到5 V时判过流；有效增长沿用原逻辑max(10 V,目标0.1%)，单点/未选中电压保留诊断，不自动重启输出。");
            Action checkDeadline = () => {
                if (clock.ElapsedMilliseconds - start >= s.RampTimeoutSeconds * 1000)
                    throw new TimeoutException("HV UP连续加压超时（时限 " + s.RampTimeoutSeconds + " s）；关断、AO归零，不自动继续发光。");
            };
            try
            {
              while (true)
              {
                token.ThrowIfCancellationRequested(); CheckEmergency(backend); checkDeadline();
                stats.Iterations++;
                var f = ReadChecked(backend, s); f.AoV = ao; result.Ramp.Add(f);
                // VMON is a positive magnitude monitor. AI4 follows the selected
                // HV polarity; only the control quantity is rectified, never raw data.
                double signedVm = FactoryFeedbackWindow.Vmon(f) * s.VmonFullScaleKv * 1000 / s.FeedbackFullScaleV;
                double vm = s.VmonWorkingVoltageV(signedVm); // Raw bounds are checked separately below.
                double signedDivider = FactoryFeedbackWindow.Divider(f) * s.Input.DividerVPerV;
                double divider = Math.Abs(signedDivider), current = FactoryFeedbackWindow.Imon(f) * s.ImonFullScaleMa / s.FeedbackFullScaleV;
                stats.ValidFeedbackCount++; stats.LastDividerV = signedDivider; stats.LastAoV = ao;
                // Complete-batch overvoltage, polarity and current guards retain
                // priority over a last sample that happens to be in the window.
                // Keep the configured independent protection ceiling separate
                // from the target acceptance window, including half-code bounds.
                CheckRampVoltage(f,s,s.FeedbackCeilingKv*1000);
                CheckPrecisionProtection(f,s);
                double measured = SelectedVoltage(f,s);
                currentGuard.Observe(clock.ElapsedMilliseconds, measured, f, ao > 0);
                if (!s.UsePrecisionRamp && measured > targetMaximum)
                    throw new InvalidOperationException("反馈窗口统计值超过达压容差；AI4窗口幅值 " + (divider/1000).ToString("F4") +
                        " kV，允许 " + (targetMinimum/1000).ToString("F4") + "～" + (targetMaximum/1000).ToString("F4") +
                        " kV；VMON窗口 " + (vm/1000).ToString("F4") + " kV；停止HV UP，不继续增加AO。" + FeedbackSnapshot(f,s));
                token.ThrowIfCancellationRequested(); CheckEmergency(backend); checkDeadline();
                // The detecting sample freezes AO but is not one of the three
                // subsequent feedback verifications. Cached/repeated/older UTC
                // can retain raw protection authority, never count as a new read.
                if(precisionRecheck)
                {
                    if(f.TimestampUtc<=precisionFeedbackUtc)
                    {
                        report("PRECISION_RECHECK_PENDING", "AO保持；本次反馈不是递增UTC的新段，不计入精度复核，仍执行急停和总时限。");
                        DelayPrecisionRecheck(s,backend,clock,start,token);
                        continue;
                    }
                    precisionFeedbackUtc=f.TimestampUtc;
                    stats.PrecisionRecheckReads++;
                }
                if (measured >= targetMinimum && measured <= targetMaximum)
                {
                    stats.PrecisionEvaluated=s.UsePrecisionRamp; stats.WithinPrecision=true;
                    stats.ExitCode = "Reached"; stats.ExitReason = "首次有效达压，停止HV UP";
                    return;
                }
                if(s.UsePrecisionRamp && (precisionRecheck || measured>targetMaximum))
                {
                    if(!precisionRecheck)
                    {
                        precisionRecheck=true;precisionFeedbackUtc=f.TimestampUtc;
                        report("PRECISION_RECHECK", "选中电压超过精度上沿；AO立即保持不再增加，随后读取3个新反馈复核。"+FeedbackSnapshot(f,s));
                    }
                    else
                    {
                        report("PRECISION_RECHECK", "AO保持；新反馈精度复核 "+stats.PrecisionRecheckReads+"/3；选中 "+
                            SelectedVoltageName(s)+" "+(measured/1000).ToString("F4")+" kV。"+FeedbackSnapshot(f,s));
                        if(stats.PrecisionRecheckReads>=3)
                        {
                            stats.PrecisionEvaluated=true;stats.WithinPrecision=false;stats.PrecisionWarning=true;
                            stats.ExitCode="ReachedWithPrecisionWarning";
                            stats.ExitReason="冻结AO后3个新反馈仍有精度偏差，按实测结果进入采集；不声明精度合格";
                            report("PRECISION_WARNING",stats.Describe());return;
                        }
                    }
                    DelayPrecisionRecheck(s,backend,clock,start,token);
                    continue;
                }
                // Keep every feedback sample in the record, but do not put
                // synchronous disk/UI reporting into every requested 1 ms step.
                if (clock.ElapsedMilliseconds - lastReport >= 100)
                {
                    stats.ElapsedMilliseconds = clock.ElapsedMilliseconds-start;
                    report("RAMP_PROGRESS",stats.Describe());
                    report("HV_CONDITION", "目标 " + s.TargetKv.ToString("F3") + " kV；VMON " + (vm / 1000).ToString("F3") +
                        " kV" + (vm != signedVm ? "（负噪声工作值按0，原始窗口 " + (signedVm/1000).ToString("F3") + " kV）" : "") + "；|分压器| " + (divider / 1000).ToString("F3") + " kV（窗口有符号 " + (signedDivider / 1000).ToString("F3") + " kV）；IMON窗口趋势 " + current.ToString("F3") +
                        " mA；AO 请求 " + ao.ToString("F4") + " / 上限 " + ceiling.ToString("F4") + " V；加压 " +
                        ((clock.ElapsedMilliseconds-start)/1000.0).ToString("F3") + " / " + s.RampTimeoutSeconds + " s；循环 " + stats.Iterations + " 次，首次进入容差即停止。");
                    lastReport = clock.ElapsedMilliseconds;
                }
                checkDeadline();
                if (ao < ceiling)
                {
                    CheckEmergency(backend); token.ThrowIfCancellationRequested(); checkDeadline();
                    // Observe once at the nominal command before spending any
                    // headroom. A coarse last step must not jump straight from
                    // below nominal to +5% and skip the selected precision band.
                    double stepCeiling = ao < s.NominalAoV ? Math.Min(ceiling,s.NominalAoV) : ceiling;
                    if (fine == null) {
                        ao = Math.Min(stepCeiling, ao + s.Input.AoStepV); s.ValidateAoCommand(ao);
                        stats.LastAoV = ao; backend.WriteAo(ao);
                    } else {
                        double next = fine.NextAo(ao,measured,clock.ElapsedMilliseconds);
                        s.ValidateAoCommand(next);
                        if (next > ao) { ao=next; stats.LastAoV=ao; backend.WriteAo(ao); }
                    }
                }
                // Never deliberately sleep beyond the ramp deadline. Native SDK
                // calls and Windows scheduling are still not hard-real-time.
                int remaining = (int)Math.Max(0, Math.Ceiling(s.RampTimeoutSeconds*1000-(clock.ElapsedMilliseconds-start)));
                int requestedWait = fine == null ? s.Input.LoopDelayMs : fine.FinishCycle(clock.ElapsedMilliseconds);
                GuardedDelay(Math.Min(requestedWait,remaining), backend, clock, token);
              }
            }
            catch (Exception ex)
            {
                stats.ExitCode = ex is OperationCanceledException ? "Cancelled" : ex is TimeoutException ? "Timeout" : "Fault";
                stats.ExitReason = ex.Message.Split(new[] { '；', '。', '\r', '\n' })[0];
                throw;
            }
            finally { stats.ElapsedMilliseconds = clock.ElapsedMilliseconds-start; }
        }
        private static void DelayPrecisionRecheck(FactorySettings s,IFactoryBackend backend,IFactoryClock clock,long start,CancellationToken token)
        {
            int remaining=(int)Math.Max(0,Math.Ceiling(s.RampTimeoutSeconds*1000-(clock.ElapsedMilliseconds-start)));
            GuardedDelay(Math.Min(Math.Max(s.Input.LoopDelayMs,s.FeedbackWindowMs),remaining),backend,clock,token);
        }
        internal static void CheckPrecisionProtection(FactoryFeedback f,FactorySettings s)
        {
            if(!s.UsePrecisionRamp)return;
            double rawUpper=s.DividerOnlyFeedback ? FactoryFeedbackBounds.DividerAbs(f)*s.Input.DividerVPerV :
                FactoryFeedbackBounds.VmonAbs(f)*s.VmonFullScaleKv*1000/s.FeedbackFullScaleV;
            if(!FactorySettings.Finite(rawUpper)||!FactorySettings.Positive(s.PrecisionProtectionUpperV)||rawUpper>=s.PrecisionProtectionUpperV)
                throw new InvalidOperationException("选中 "+SelectedVoltageName(s)+" 原始峰值达到异常过压保护上限 "+
                    (s.PrecisionProtectionUpperV/1000).ToString("F4")+" kV；立即停止，不等待精度复核。"+FeedbackSnapshot(f,s));
        }
        internal static void CheckRampVoltage(FactoryFeedback f, FactorySettings s, double upperV)
        {
            double scale = s.VmonFullScaleKv * 1000 / s.FeedbackFullScaleV;
            double vmonMaximum = (FactoryFeedbackBounds.Maximum(f.VmonRawV,f.VmonMaximumRawV,f.SampleCount) + f.VmonHalfCodeV) * scale;
            double vmonMinimum = (FactoryFeedbackBounds.Minimum(f.VmonRawV,f.VmonMinimumRawV,f.SampleCount) - f.VmonHalfCodeV) * scale;
            double dividerAbsoluteMaximum = FactoryFeedbackBounds.DividerAbs(f) * s.Input.DividerVPerV;
            if(!FactorySettings.Positive(upperV) || !FactorySettings.Finite(s.DividerOnlyFeedback?dividerAbsoluteMaximum:vmonMaximum) ||
                (!s.DividerOnlyFeedback&&!FactorySettings.Finite(vmonMinimum)))
                throw new InvalidOperationException("选中电压保护边界无效；不能继续输出。");
            string reason = !s.DividerOnlyFeedback && vmonMaximum > upperV ? "VMON 批次上界超过升压上限" :
                s.DividerOnlyFeedback && dividerAbsoluteMaximum > upperV ? "AI4 绝对值批次上界超过升压上限（正负均检查）" :
                !s.DividerOnlyFeedback && vmonMinimum < -s.VmonNegativeLimitV ? "VMON 负向异常（超出背景容差，不取绝对值放行）" : null;
            if (reason != null)
                throw new InvalidOperationException(reason + String.Format(CultureInfo.InvariantCulture,
                    "：升压上限 {0:F2} V；VMON批次 [{1:F2}, {2:F2}] V；|AI4|批次上界 {3:F2} V；VMON负向界限 -{4:F2} V。停止输出。",
                    upperV,vmonMinimum,vmonMaximum,dividerAbsoluteMaximum,s.VmonNegativeLimitV) + FeedbackSnapshot(f,s));
        }
        internal static void WaitCapture(FactorySettings s, IFactoryBackend b, IFactoryClock c, long start,
            Action<string,string> report, CancellationToken token,int timeoutMs=0)
        {
            long lastReport = -1000;
            while (true)
            {
                token.ThrowIfCancellationRequested(); CheckEmergency(b);
                long elapsed = c.ElapsedMilliseconds - start;
                bool art = b.ArtComplete, pico = b.PicoComplete;
                if (elapsed >= (timeoutMs>0?timeoutMs:s.Input.TriggerTimeoutMs)) throw new FactoryCaptureTimeoutException(art,pico,elapsed);
                if (art && pico) return;
                if (elapsed - lastReport >= 250) { report("WAIT", "ART=" + art + "，Pico=" + pico + "，等待 " + elapsed + " ms。"); lastReport = elapsed; }
                c.Pause(10, token);
            }
        }
        internal static FactoryFeedback ReadChecked(IFactoryBackend b, FactorySettings s)
        {
            var f = b.ReadFeedback();
            return ValidateFeedback(f,s,true);
        }
        internal static FactoryFeedback ValidateFeedback(FactoryFeedback f, FactorySettings s, bool requireOutputPermission)
        {
            if (f == null) throw new InvalidOperationException("反馈缺失：驱动未返回监测对象；不能继续输出。");
            if (f.TimestampUtc.Kind != DateTimeKind.Utc || Math.Abs((DateTime.UtcNow - f.TimestampUtc).TotalSeconds) > 2)
                throw new InvalidOperationException("反馈时间戳无效或过期（要求UTC、偏差≤2秒）；不能继续输出。" + FeedbackSnapshot(f,s));
            if (s.PressureFeedbackContext) return ValidateSelectedFeedback(f,s,requireOutputPermission);
            if (!FactorySettings.Finite(f.ImonRawV) || !FactorySettings.Finite(f.VmonRawV) || !FactorySettings.Finite(f.DividerRawV))
                throw new InvalidOperationException("AI反馈存在NaN/无穷值；不能继续输出。" + FeedbackSnapshot(f,s));
            if (Math.Abs(f.ImonRawV) > s.FeedbackFullScaleV * 1.02 || Math.Abs(f.VmonRawV) > s.FeedbackFullScaleV * 1.02 || Math.Abs(f.DividerRawV) >= 9.9)
                throw new InvalidOperationException("AI原始输入超范围：AI0/AI2绝对值允许≤" + (s.FeedbackFullScaleV * 1.02).ToString("F3") +
                    " V，AI4绝对值要求<9.9 V；不能继续输出。" + FeedbackSnapshot(f,s));
            if (f.SampleCount < 0 || !FactorySettings.Finite(f.ImonHalfCodeV) || !FactorySettings.Finite(f.VmonHalfCodeV) || !FactorySettings.Finite(f.DividerHalfCodeV) ||
                f.ImonHalfCodeV < 0 || f.VmonHalfCodeV < 0 || f.DividerHalfCodeV < 0)
                throw new InvalidOperationException("反馈批次或量化余量无效。" + FeedbackSnapshot(f,s));
            if (f.SampleCount > 0)
            {
                foreach (double value in new[] { f.ImonMinimumRawV, f.ImonMaximumRawV, f.VmonMinimumRawV, f.VmonMaximumRawV, f.DividerMinimumRawV, f.DividerMaximumRawV })
                    if (!FactorySettings.Finite(value)) throw new InvalidOperationException("反馈批次峰值非有限，不能放行。" + FeedbackSnapshot(f,s));
                if (f.ImonMinimumRawV > f.ImonRawV || f.ImonMaximumRawV < f.ImonRawV ||
                    f.VmonMinimumRawV > f.VmonRawV || f.VmonMaximumRawV < f.VmonRawV ||
                    f.DividerMinimumRawV > f.DividerRawV || f.DividerMaximumRawV < f.DividerRawV ||
                    FactoryFeedbackBounds.ImonAbs(f) > s.FeedbackFullScaleV * 1.02 ||
                    FactoryFeedbackBounds.VmonAbs(f) > s.FeedbackFullScaleV * 1.02 || FactoryFeedbackBounds.DividerAbs(f) >= 9.9)
                    throw new InvalidOperationException("反馈批次峰值、范围或排列不成立，不能只使用最后一点放行。" + FeedbackSnapshot(f,s));
            }
            if (f.WindowSampleCount < 0 || (f.WindowSampleCount > 0 &&
                (f.WindowSampleCount != s.FeedbackWindowMs || f.WindowSampleCount > f.SampleCount ||
                 !FactorySettings.Finite(f.ImonMedianRawV) || !FactorySettings.Finite(f.VmonMedianRawV) || !FactorySettings.Finite(f.DividerMedianRawV) ||
                 f.ImonMedianRawV < f.ImonMinimumRawV || f.ImonMedianRawV > f.ImonMaximumRawV ||
                 f.VmonMedianRawV < f.VmonMinimumRawV || f.VmonMedianRawV > f.VmonMaximumRawV ||
                 f.DividerMedianRawV < f.DividerMinimumRawV || f.DividerMedianRawV > f.DividerMaximumRawV)))
                throw new InvalidOperationException("反馈窗口统计无效或超出原始范围，不使用该值升压。");
            if (requireOutputPermission && !f.EmergencyReleased) throw new FactoryEmergencyStopException();
            if (requireOutputPermission && FactoryFeedbackBounds.ImonAbs(f) * s.ImonFullScaleMa / s.FeedbackFullScaleV >= s.CurrentLimitMa)
                throw new InvalidOperationException("IMON 达到限流阈值 " + s.CurrentLimitMa.ToString("F3") + " mA；立即停止本轮，不自动重启直流源。" + FeedbackSnapshot(f,s));
            return f;
        }
        private static FactoryFeedback ValidateSelectedFeedback(FactoryFeedback f,FactorySettings s,bool requireOutputPermission)
        {
            double raw=s.DividerOnlyFeedback?f.DividerRawV:f.VmonRawV;
            double half=s.DividerOnlyFeedback?f.DividerHalfCodeV:f.VmonHalfCodeV;
            double low=s.DividerOnlyFeedback?f.DividerMinimumRawV:f.VmonMinimumRawV;
            double high=s.DividerOnlyFeedback?f.DividerMaximumRawV:f.VmonMaximumRawV;
            double median=s.DividerOnlyFeedback?f.DividerMedianRawV:f.VmonMedianRawV;
            if(!FactorySettings.Finite(raw)||!FactorySettings.Finite(half)||half<0||f.SampleCount<0)
                throw new InvalidOperationException("选中电压反馈缺失、非有限或量化标记无效；不能继续输出。"+FeedbackSnapshot(f,s));
            double range=s.DividerOnlyFeedback?FactoryArtFeedbackPlan.DividerFullScale(s):5.5;
            if((s.DividerOnlyFeedback?Math.Abs(raw):raw)>=range*.995)
                throw new InvalidOperationException("选中电压原始值达到所设输入边界；不能继续输出。"+FeedbackSnapshot(f,s));
            if(f.SampleCount>0 && (!FactorySettings.Finite(low)||!FactorySettings.Finite(high)||low>raw||high<raw||
                (s.DividerOnlyFeedback?Math.Max(Math.Abs(low),Math.Abs(high)):high)>=range*.995))
                throw new InvalidOperationException("选中电压批次范围无效或达到输入边界；不能只使用最后一点放行。"+FeedbackSnapshot(f,s));
            if(f.WindowSampleCount<0 || (f.WindowSampleCount>0 &&
                (f.WindowSampleCount!=s.FeedbackWindowMs||f.WindowSampleCount>f.SampleCount||!FactorySettings.Finite(median)||median<low||median>high)))
                throw new InvalidOperationException("选中电压窗口无效；不能继续输出。");
            if(requireOutputPermission&&!f.EmergencyReleased)
                throw new FactoryEmergencyStopException();
            // AI0 and the unselected voltage remain unmodified diagnostics.
            // The sustained 5 V current condition is evaluated in the ramp.
            return f;
        }
        internal static string SelectedVoltageName(FactorySettings s){return s.DividerOnlyFeedback?"DIV":"VMON";}
        internal static double SelectedVoltage(FactoryFeedback f,FactorySettings s)
        {return s.DividerOnlyFeedback?Math.Abs(FactoryFeedbackWindow.Divider(f)*s.Input.DividerVPerV):
            s.VmonWorkingVoltageV(FactoryFeedbackWindow.Vmon(f)*s.VmonFullScaleKv*1000/s.FeedbackFullScaleV);}
        internal static double SelectedVoltageAbsoluteBound(FactoryFeedback f,FactorySettings s)
        {return s.DividerOnlyFeedback?FactoryFeedbackBounds.DividerAbs(f)*s.Input.DividerVPerV:
            FactoryFeedbackBounds.VmonAbs(f)*s.VmonFullScaleKv*1000/s.FeedbackFullScaleV;}
        internal static string FeedbackSnapshot(FactoryFeedback f, FactorySettings s)
        {
            if (f == null) return "反馈=null。";
            return String.Format(CultureInfo.InvariantCulture,
                " AO={0:F4} V；AI0/2/4原始={1:G9}/{2:G9}/{3:G9} V；AI2批次=[{4:G9},{5:G9}] V，AI4批次=[{6:G9},{7:G9}] V；" +
                "VMON={8:F2} V，AI4有符号={9:F2} V，|AI4|={10:F2} V；点数={11}，UTC={12:O}({13})，年龄={14:F1} ms；缓存={15}→{16}，读取={17} ms。",
                f.AoV,f.ImonRawV,f.VmonRawV,f.DividerRawV,
                FactoryFeedbackBounds.Minimum(f.VmonRawV,f.VmonMinimumRawV,f.SampleCount),FactoryFeedbackBounds.Maximum(f.VmonRawV,f.VmonMaximumRawV,f.SampleCount),
                FactoryFeedbackBounds.Minimum(f.DividerRawV,f.DividerMinimumRawV,f.SampleCount),FactoryFeedbackBounds.Maximum(f.DividerRawV,f.DividerMaximumRawV,f.SampleCount),
                Vmon(f,s),Divider(f,s),Math.Abs(Divider(f,s)),f.SampleCount,f.TimestampUtc,f.TimestampUtc.Kind,
                (DateTime.UtcNow - f.TimestampUtc).TotalMilliseconds,f.BacklogBefore,f.BacklogAfter,f.ReadElapsedMs) +
                (f.WindowSampleCount > 0 ? String.Format(CultureInfo.InvariantCulture,
                    " 窗口{0}点中位数：VMON={1:F2} V，AI4有符号={2:F2} V，IMON趋势={3:F3} mA（保护看原始峰值）。",
                    f.WindowSampleCount,f.VmonMedianRawV*s.VmonFullScaleKv*1000/s.FeedbackFullScaleV,
                    f.DividerMedianRawV*s.Input.DividerVPerV,f.ImonMedianRawV*s.ImonFullScaleMa/s.FeedbackFullScaleV) : "") +
                (f.FreshWindowAfterAoCommand ? " AO后驱动可见旧队列边界=" + f.FreshWindowBoundaryQueued +
                    "点，边界后已读=" + f.FreshWindowSamplesAfterBoundary + "点；不表示电源已稳定或硬件逐点同步。" : "");
        }
        private static double Divider(FactoryFeedback f, FactorySettings s) { return f.DividerRawV * s.Input.DividerVPerV; }
        private static double Vmon(FactoryFeedback f, FactorySettings s) { return f.VmonRawV * s.VmonFullScaleKv * 1000 / s.FeedbackFullScaleV; }
        private static double Current(FactoryFeedback f, FactorySettings s) { return f.ImonRawV * s.ImonFullScaleMa / s.FeedbackFullScaleV; }
        internal static bool ResidualOk(FactoryFeedback f, FactorySettings s)
        { return ResidualWithin(f, s, s.SafeResidualV); }
        internal static bool EndResidualOk(FactoryFeedback f, FactorySettings s)
        { return ResidualWithin(f, s, s.EndResidualLimitV); }
        internal static bool InitialResidualOk(FactoryFeedback f, FactorySettings s)
        { return ResidualWithin(f, s, s.InitialResidualLimitV); }
        private static bool ResidualWithin(FactoryFeedback f, FactorySettings s, double limit)
        { if(s.PressureFeedbackContext)return FactorySettings.Positive(limit)&&SelectedVoltageAbsoluteBound(f,s)<=limit;
            return FactorySettings.Positive(limit) && FactoryFeedbackBounds.DividerAbs(f) * s.Input.DividerVPerV <= limit &&
                FactoryFeedbackBounds.VmonAbs(f) * s.VmonFullScaleKv * 1000 / s.FeedbackFullScaleV <= limit; }
        private static void RequireResidual(FactoryFeedback f, FactorySettings s)
        { if (!ResidualOk(f, s)) throw new InvalidOperationException("初始残压超过阈值 " + s.SafeResidualV.ToString("F1") + " V：分压器 " + Divider(f, s).ToString("F2") +
            " V（AI4=" + f.DividerRawV.ToString("F6") + " V）；电源反馈 " + Vmon(f, s).ToString("F2") + " V（AI2=" + f.VmonRawV.ToString("F6") +
            " V）。不允许 HV ON 或校准采集。检查反馈与放电，禁止靠近设备。"); }
        private static string FeedbackDetail(FactoryFeedback f, FactorySettings s)
        {
            return "AI0 " + (f.ImonRawV * 1000).ToString("F3") + " mV；AI2 " + (f.VmonRawV * 1000).ToString("F3") +
                " mV；AI4 " + (f.DividerRawV * 1000).ToString("F3") + " mV；VMON " + Vmon(f,s).ToString("F2") +
                " V，分压器 " + Divider(f,s).ToString("F2") + " V；批次绝对上界（含半码阶） " +
                (FactoryFeedbackBounds.VmonAbs(f) * s.VmonFullScaleKv * 1000 / s.FeedbackFullScaleV).ToString("F2") +
                " / " + (FactoryFeedbackBounds.DividerAbs(f) * s.Input.DividerVPerV).ToString("F2") + " V；P0.4 高；读取 " +
                f.ReadBatchCount + " 组 / " + f.ReadElapsedMs + " ms，未读 " + f.BacklogBefore + "→" + f.BacklogAfter + "点。";
        }
        internal static void WaitInitialResidual(FactorySettings s, IFactoryBackend b, IFactoryClock c, FactoryResult result,
            Action<string,string> report, CancellationToken token)
        {
            if(s.SkipPressureResidualMeasurement)
            {
                token.ThrowIfCancellationRequested();CheckEmergency(b);
                report("INITIAL_FEEDBACK_SKIPPED", "加压前自动残压测量已禁用；未读AI，电压未知，须现场独立安全放电。");
                return;
            }
            long start = c.ElapsedMilliseconds, lastReport = -1000; int safe = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested(); var f = ReadChecked(b,s); result.StartupFeedback.Add(f);
                long elapsed = c.ElapsedMilliseconds - start;
                bool within = InitialResidualOk(f,s);
                safe = elapsed >= s.InitialFeedbackSettleMs && within ? safe + 1 : 0;
                if (elapsed - lastReport >= 100 || safe >= 5)
                {
                    report("INITIAL_FEEDBACK", "初始阈值 " + s.InitialResidualLimitV.ToString("F1") + " V；" + FeedbackDetail(f,s) + (s.ZeroSynchronizedOptical ? " 只读初始反馈等待 " : " 归零等待 ") + elapsed + " ms，连续确认 " + safe + "/5；" +
                        (within ? "阈值内，等待稳定确认。" : "等待反馈稳定或残压降低，不启动直流源。")); lastReport = elapsed;
                }
                if (elapsed >= s.InitialFeedbackTimeoutMs)
                    throw new InvalidOperationException((s.ZeroSynchronizedOptical ? "只读初始反馈连续确认超时（未操作HV/AO）（" : "HV OFF / AO=0 下初始反馈连续确认超时（") + s.InitialFeedbackTimeoutMs +
                        " ms，阈值 " + s.InitialResidualLimitV.ToString("F1") + " V）。" + FeedbackDetail(f,s) +
                        "未启动直流源。若独立确认无电压，检查 ART 原始读数、RSE参考及零点校准；本程序不自动把当前输入扣成零。");
                if (safe >= 5) return;
                c.Pause(10,token);
            }
        }
        internal static void CheckEmergency(IFactoryBackend backend)
        { if (!backend.ReadEmergencyReleased()) throw new FactoryEmergencyStopException(); }
        internal static void GuardedDelay(int duration, IFactoryBackend backend, IFactoryClock clock, CancellationToken cancellation)
        {
            long start = clock.ElapsedMilliseconds;
            while (clock.ElapsedMilliseconds - start < duration)
            {
                cancellation.ThrowIfCancellationRequested(); CheckEmergency(backend);
                PauseRemaining(duration, start, clock, cancellation);
            }
        }
        private static void ZeroVoltageDelay(int duration, FactorySettings settings, IFactoryBackend backend, IFactoryClock clock,
            FactoryResult result, Action<string,string> report, CancellationToken cancellation)
        {
            if(settings.SkipPressureResidualMeasurement)
            {
                report("HV_ZERO_FEEDBACK_SKIPPED", "HV ON零输出背景AI不采样、不判残压；按原启动延时仅监督真实急停和手动停止，升压阶段另读实际选中反馈。");
                GuardedDelay(duration,backend,clock,cancellation);
                return;
            }
            long start = clock.ElapsedMilliseconds;
            bool firstRequired = settings.UsePostHvVmonStartup;
            while (firstRequired || clock.ElapsedMilliseconds - start < duration)
            {
                cancellation.ThrowIfCancellationRequested();
                var f = ReadChecked(backend, settings); result.StartupFeedback.Add(f);
                firstRequired = false;
                report("HV_ZERO_FEEDBACK", "HV ON，AO保持0；" + FeedbackDetail(f,settings));
                if (settings.UsePostHvVmonStartup)
                {
                    double selectedUpper = SelectedVoltageAbsoluteBound(f,settings);
                    double startupLimit=settings.DividerOnlyFeedback&&settings.ZeroBackgroundLimitV>0?settings.ZeroBackgroundLimitV:settings.StartupVmonLimitV;
                    if(selectedUpper>startupLimit || selectedUpper>settings.FeedbackCeilingKv*1000)
                        throw new InvalidOperationException("HV ON后AO=0，选中 "+SelectedVoltageName(settings)+"批次绝对上界（含半码阶）"+selectedUpper.ToString("F2")+
                            " V超过启动背景/独立限压；不是采后残压阈值。"+FeedbackSnapshot(f,settings));
                }
                else if (!ResidualOk(f,settings)) throw new InvalidOperationException("HV ON 后 AO=0 时反馈超过零输出阈值。" +
                    FeedbackDetail(f,settings) + "启动脉冲已记录，停止输出；不能把未经验证的暂态当作正常跳过。");
                PauseRemaining(duration, start, clock, cancellation);
            }
        }
        private static void PauseRemaining(int duration, long start, IFactoryClock clock, CancellationToken cancellation)
        {
            long remaining = duration - (clock.ElapsedMilliseconds - start);
            if (remaining > 0) clock.Pause((int)Math.Min(10, remaining), cancellation);
        }
        private static void SafeReport(Action<string,string> report, string stage, string detail, List<Exception> failures)
        { try { report(stage, detail); } catch (Exception ex) { failures.Add(new IOException("诊断记录失败。", ex)); } }
    }

    // Operator-confirmed current condition: raw AI0 >= 5 V throughout a
    // continuously observed three seconds AND selected voltage has no effective
    // growth for three seconds. No mA conversion or single-point trip here.
    internal sealed class FactoryRampCurrentGuard
    {
        internal const double FullScaleRawV=5.0;
        internal const long RequiredDurationMs=3000;
        private readonly double growthStep;
        private double lastGrowthV;
        private long growthAt,fullCurrentSince=-1,lastObserved=-1;
        internal FactoryRampCurrentGuard(double targetV,long started)
        {growthStep=Math.Max(10,targetV*.001);growthAt=started;}
        internal void Observe(long now,double selectedVoltageV,FactoryFeedback f,bool outputRequested)
        {
            if(f==null||!FactorySettings.Finite(selectedVoltageV)||now<0)
                throw new InvalidOperationException("过流复核缺少有效选中电压或当前时间。");
            bool gap=lastObserved>=0&&(now<lastObserved||now-lastObserved>FactoryFeedbackStream.MaximumReadMs);
            lastObserved=now;
            if(!outputRequested||gap)
            {growthAt=now;lastGrowthV=selectedVoltageV;fullCurrentSince=-1;}
            if(!outputRequested)return;
            if(selectedVoltageV>lastGrowthV+growthStep)
            {lastGrowthV=selectedVoltageV;growthAt=now;}
            // A low or unknown current anywhere in the returned batch breaks
            // continuity. Preserve non-finite diagnostics rather than inventing
            // a full-current observation. Existing voltage/IO guards still run.
            double minimum=f.SampleCount>0?f.ImonMinimumRawV:f.ImonRawV;
            bool full=FactorySettings.Finite(minimum)&&minimum>=FullScaleRawV;
            if(!full){fullCurrentSince=-1;return;}
            if(fullCurrentSince<0)fullCurrentSince=now;
            if(now-growthAt>=RequiredDurationMs&&now-fullCurrentSince>=RequiredDurationMs)
                throw new InvalidOperationException("选中电压连续3秒无有效增长且AI0电流原始反馈连续3秒达到5 V；停止本轮，不自动重启输出。");
        }
    }
}
