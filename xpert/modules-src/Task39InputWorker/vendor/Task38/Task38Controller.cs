using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace HVX.OWTS.Task38
{
    public sealed class Task38Controller : IDisposable
    {
        private readonly object sync = new object();
        private readonly IArtUsb3135Device art;
        private readonly IPicoScope5242DBlockDevice pico;
        private Task38Configuration configuration;
        private Task38StatusSnapshot status;
        private Task38CycleResult lastResult;
        private Thread safetyThread;
        private Thread cycleThread;
        private int shutdownRequested;
        private int stopRequested;
        private int emergencyLatched;
        private bool initialized;
        private bool cycleRunning;
        private bool disposing;
        private bool disposed;
        private bool safetyReady;

        public Task38Controller()
            : this(new ArtUsb3135Device(), new PicoScope5242DBlockDevice())
        {
        }

        public Task38Controller(IArtUsb3135Device artDevice,
            IPicoScope5242DBlockDevice picoDevice)
        {
            if (artDevice == null) throw new ArgumentNullException("artDevice");
            if (picoDevice == null) throw new ArgumentNullException("picoDevice");
            art = artDevice;
            pico = picoDevice;
            status = NewStatus(Task38ControllerState.Uninitialized, "未初始化");
        }

        public void Initialize(Task38Configuration requestedConfiguration)
        { Initialize(requestedConfiguration, null); }

        public void Initialize(Task38Configuration requestedConfiguration, Action<string> progress)
        {
            if (requestedConfiguration == null) throw new ArgumentNullException("requestedConfiguration");
            string validation = requestedConfiguration.Validate();
            if (!String.IsNullOrEmpty(validation)) throw new ArgumentException(validation, "requestedConfiguration");

            lock (sync)
            {
                ThrowIfDisposed();
                if (initialized) throw new InvalidOperationException("Task 38 controller is already initialized.");
                configuration = requestedConfiguration.Clone();
            }

            try
            {
                if (progress != null) progress("初始化控制模块：安全输出、联锁输入、反馈及有限采集触发验证");
                art.Initialize(configuration);
                if (progress != null) progress("初始化局放模块：打开指定设备并验证通道、分辨率、时基、内存及触发");
                pico.Initialize(configuration);
                if (progress != null) progress("两模块初始化已返回，正在检查联锁和残压");
                bool firstSafety = art.ReadSafetyReady();
                lock (sync)
                {
                    safetyReady = firstSafety;
                    initialized = true;
                    status = NewStatus(Task38ControllerState.SafeIdle,
                        configuration.ControlBoxVerification ? "控制箱校验：两卡已初始化，反馈仅作原始输入诊断；禁止加压，不证明已放电。" :
                        firstSafety ? "安全联锁有效，允许加压" : "安全联锁未就绪，禁止加压");
                    status.Initialized = true;
                    status.SafetyReady = firstSafety;
                    status.PicoRangeIndex = configuration.PicoRangeIndex;
                    RefreshStartAllowedLocked();
                }
                safetyThread = new Thread(SafetyPollLoop);
                PollZeroVoltageIdle();
                safetyThread.IsBackground = true;
                safetyThread.Name = "Task38-SafetyPoll-50ms";
                safetyThread.Start();
            }
            catch
            {
                try { art.ForceSourceOff(); } catch { }
                try { pico.Dispose(); } catch { }
                try { art.Dispose(); } catch { }
                lock (sync)
                {
                    initialized = false;
                    status = NewStatus(Task38ControllerState.Faulted, "初始化失败");
                }
                throw;
            }
        }

        public void InitializeFromXml(string configurationPath)
        {
            Initialize(Task38Configuration.LoadXml(configurationPath));
        }

        public bool StartCycle()
        { return StartCycle(String.Empty); }

        private string activeRecordId = String.Empty;
        public bool StartCycle(string recordId)
        {
            lock (sync)
            {
                if (configuration != null && configuration.ControlBoxVerification)
                {
                    status.LastError = "控制箱校验禁止加压、零压触发及同步开关动作；请退出校验后选择设备验证并重新连接。";
                    return false;
                }
                EnsureInitializedLocked();
                if (cycleRunning)
                {
                    status.LastError = "已有加压采集循环正在运行。";
                    return false;
                }
                if (status.State == Task38ControllerState.Faulted)
                {
                    status.LastError = "故障状态已锁存；确认安全后先调用 ResetFaultLatch。";
                    RefreshStartAllowedLocked();
                    return false;
                }
                if (!safetyReady)
                {
                    status.LastError = "安全控制按钮/急停联锁未开启，禁止加压。";
                    RefreshStartAllowedLocked();
                    return false;
                }
                if (Interlocked.CompareExchange(ref emergencyLatched, 0, 0) != 0)
                {
                    status.LastError = "急停故障已锁存；确认安全后先调用 ResetFaultLatch。";
                    RefreshStartAllowedLocked();
                    return false;
                }
                Interlocked.Exchange(ref stopRequested, 0);
                cycleRunning = true;
                activeRecordId = recordId ?? String.Empty;
                lastResult = null;
                status.CycleRunning = true;
                status.LastError = String.Empty;
                RefreshStartAllowedLocked();
                cycleThread = new Thread(CycleWorker);
                cycleThread.IsBackground = true;
                cycleThread.Name = "Task38-HV-Cycle";
                cycleThread.Start();
                return true;
            }
        }

        public Task38CycleResult RunCycleBlocking()
        {
            if (!StartCycle()) throw new InvalidOperationException(GetStatus().LastError);
            WaitForCycle(-1);
            return GetLastResult();
        }

        public bool WaitForCycle(int timeoutMilliseconds)
        {
            Thread worker;
            lock (sync) worker = cycleThread;
            if (worker == null) return true;
            if (timeoutMilliseconds < 0)
            {
                worker.Join();
                return true;
            }
            return worker.Join(timeoutMilliseconds);
        }

        public void RequestStop()
        {
            Interlocked.Exchange(ref stopRequested, 1);
        }

        public bool ResetFaultLatch()
        {
            lock (sync)
            {
                EnsureInitializedLocked();
                if (cycleRunning || !safetyReady) return false;
            }
            art.ForceSourceOff();
            art.SetDigitalOutputs(false, false, true, configuration.GroundInSafeIdle);
            if (configuration.TargetVoltageV == 0 && !configuration.ControlBoxVerification)
            {
                FeedbackSample zero = art.ReadFeedback(); PublishFeedback(zero); CheckZeroVoltage(zero);
            }
            Interlocked.Exchange(ref emergencyLatched, 0);
            lock (sync)
            {
                status.State = Task38ControllerState.SafeIdle;
                status.StateMessage = configuration.ControlBoxVerification ? "控制箱诊断已复位，禁止加压，不证明已放电。" : "故障锁存已复位，安全联锁有效";
                status.EmergencyLatched = false;
                status.LastError = String.Empty;
                status.UpdatedUtc = DateTime.UtcNow;
                RefreshStartAllowedLocked();
            }
            return true;
        }

        public Task38StatusSnapshot GetStatus()
        {
            lock (sync)
            {
                return new Task38StatusSnapshot
                {
                    ControlBoxVerification = configuration != null && configuration.ControlBoxVerification,
                    DividerFeedbackRawV = status.DividerFeedbackRawV,
                    SourceVoltageFeedbackRawV = status.SourceVoltageFeedbackRawV,
                    CurrentFeedbackRawV = status.CurrentFeedbackRawV,
                    State = status.State,
                    Initialized = status.Initialized,
                    CycleRunning = status.CycleRunning,
                    SafetyReady = status.SafetyReady,
                    StartAllowed = status.StartAllowed,
                    EmergencyLatched = status.EmergencyLatched,
                    AoCommandVolts = status.AoCommandVolts,
                    ActualVoltageV = status.ActualVoltageV,
                    SourceVoltageFeedbackV = status.SourceVoltageFeedbackV,
                    CurrentA = status.CurrentA,
                    PicoRangeIndex = status.PicoRangeIndex,
                    StateMessage = status.StateMessage,
                    LastError = status.LastError,
                    UpdatedUtc = status.UpdatedUtc
                };
            }
        }

        public Task38CycleResult GetLastResult()
        {
            lock (sync) return lastResult;
        }

        public Task38Configuration GetConfigurationSnapshot()
        {
            lock (sync)
            {
                if (configuration == null) return null;
                return configuration.Clone();
            }
        }

        public void SetPicoRangeIndexForNextCycle(int rangeIndex)
        {
            lock (sync)
            {
                EnsureInitializedLocked();
                if (cycleRunning) throw new InvalidOperationException("Cannot change PicoScope range during an active cycle.");
                if (rangeIndex < 0 || rangeIndex >= configuration.PicoRangeFullScaleVolts.Length)
                    throw new ArgumentOutOfRangeException("rangeIndex");
                configuration.PicoRangeIndex = rangeIndex;
                status.PicoRangeIndex = rangeIndex;
                status.StateMessage = "下一轮PicoScope量程索引已设置为 " + rangeIndex + "；不会自动启动加压。";
                status.UpdatedUtc = DateTime.UtcNow;
            }
        }

        public Task6InputBundle GetTask6InputBundle()
        {
            Task38CycleResult result;
            Task38Configuration cfg;
            lock (sync)
            {
                if (lastResult == null) throw new InvalidOperationException("No completed Task 38 cycle is available.");
                if (lastResult.IsBackgroundCapture) throw new InvalidOperationException("零电压背景采集不应用振荡首零相位量程反馈。");
                result = lastResult;
                cfg = configuration.Clone();
            }
            return result.BuildTask6Input(cfg);
        }

        public void ApplyTask6RangeFeedback(Task6RangeFeedback feedback)
        {
            if (feedback == null) throw new ArgumentNullException("feedback");
            lock (sync)
            {
                if (lastResult == null) throw new InvalidOperationException("No completed Task 38 cycle is available.");
                if (lastResult.IsBackgroundCapture) throw new InvalidOperationException("背景采集禁止自动应用振荡量程反馈。");
                lastResult.Task6AssessmentApplied = true;
                lastResult.Task6QualityFlags = feedback.QualityFlags;
                lastResult.FirstZeroTimeSeconds = feedback.FirstZeroTimeSeconds;
                lastResult.Task6RangeSuitable = false;
                lastResult.RepeatPressurizationRequired = false;
                lastResult.SuggestedPicoRangeIndex = -1;

                bool noValidOscillation = (feedback.QualityFlags & 8192) != 0;
                bool finiteFirstZero = !Double.IsNaN(feedback.FirstZeroTimeSeconds) &&
                    !Double.IsInfinity(feedback.FirstZeroTimeSeconds) && feedback.FirstZeroTimeSeconds >= 0.0;
                if (!feedback.Task6Success || noValidOscillation || !finiteFirstZero)
                {
                    lastResult.RepeatPressurizationRequired = true;
                    lastResult.SuggestedPicoRangeIndex = configuration.PicoRangeIndex;
                    lastResult.RepeatPressurizationMessage = noValidOscillation || !finiteFirstZero
                        ? "任务6未得到有效振荡首零点，本次不得依据量程字段调档；检查触发/波形后重复加压。"
                        : "任务6分析失败，本次量程结论无效；排除错误后重复加压。";
                    status.StateMessage = lastResult.RepeatPressurizationMessage;
                    status.UpdatedUtc = DateTime.UtcNow;
                    return;
                }

                bool clippedAfterFirstZero = feedback.PdOverRange || feedback.MultipleClippedPulses;
                int effectiveDirection = clippedAfterFirstZero
                    ? (int)Task6RangeAdjustmentDirection.CurrentRangeTooSmallIncreaseRange
                    : feedback.RangeAdjustmentDirection;

                if (feedback.RangeSuitable && !clippedAfterFirstZero &&
                    effectiveDirection == (int)Task6RangeAdjustmentDirection.Suitable)
                {
                    lastResult.Task6RangeSuitable = true;
                    lastResult.RepeatPressurizationMessage = "任务6判定首零点后局放波形量程合适。";
                    status.StateMessage = lastResult.RepeatPressurizationMessage;
                    status.UpdatedUtc = DateTime.UtcNow;
                    return;
                }

                if (feedback.BestRangeIndex < 0 ||
                    feedback.BestRangeIndex >= configuration.PicoRangeFullScaleVolts.Length)
                {
                    lastResult.RepeatPressurizationRequired = true;
                    lastResult.RepeatPressurizationMessage = "任务6返回的推荐量程索引无效；保持当前量程并人工检查。";
                    status.StateMessage = lastResult.RepeatPressurizationMessage;
                    status.UpdatedUtc = DateTime.UtcNow;
                    return;
                }

                int capturedRangeIndex = lastResult.PicoRangeIndexUsed;
                bool increaseIsConsistent = effectiveDirection ==
                    (int)Task6RangeAdjustmentDirection.CurrentRangeTooSmallIncreaseRange &&
                    feedback.BestRangeIndex > capturedRangeIndex;
                bool decreaseIsConsistent = effectiveDirection ==
                    (int)Task6RangeAdjustmentDirection.CurrentRangeTooLargeDecreaseRange &&
                    feedback.BestRangeIndex < capturedRangeIndex;

                lastResult.RepeatPressurizationRequired = true;
                if (!increaseIsConsistent && !decreaseIsConsistent)
                {
                    lastResult.RepeatPressurizationMessage = clippedAfterFirstZero
                        ? "任务6报告首零点后存在过量程/多脉冲削顶，但推荐量程未向更大量程调整；保持当前量程并人工检查后重复加压。"
                        : "任务6推荐量程与调整方向不一致；保持当前量程并人工检查后重复加压。";
                    status.StateMessage = lastResult.RepeatPressurizationMessage;
                    status.UpdatedUtc = DateTime.UtcNow;
                    return;
                }

                lastResult.SuggestedPicoRangeIndex = feedback.BestRangeIndex;
                if (increaseIsConsistent)
                {
                    lastResult.RepeatPressurizationMessage = clippedAfterFirstZero
                        ? "PicoScope量程过小，任务6在首零点后检测到过量程或多脉冲削顶；建议调大到索引 " +
                            feedback.BestRangeIndex + " 后由操作者确认重复加压。"
                        : "PicoScope量程过小，首零点后有效数据存在近满量程/削顶风险；建议调大到索引 " +
                        feedback.BestRangeIndex + " 后由操作者确认重复加压。";
                }
                else
                {
                    lastResult.RepeatPressurizationMessage = "PicoScope量程过大，首零点后有效信号利用率低于20%；建议调小到索引 " +
                        feedback.BestRangeIndex + " 后由操作者确认重复加压。";
                }

                if (configuration.AutoApplyTask6RangeForNextCycle)
                {
                    configuration.PicoRangeIndex = feedback.BestRangeIndex;
                    status.PicoRangeIndex = feedback.BestRangeIndex;
                }
                status.StateMessage = lastResult.RepeatPressurizationMessage;
                status.UpdatedUtc = DateTime.UtcNow;
            }
        }

        public void Dispose()
        {
            Thread worker;
            Thread poller;
            lock (sync)
            {
                while (disposing && !disposed) Monitor.Wait(sync);
                if (disposed) return;
                disposing = true;
                Interlocked.Exchange(ref shutdownRequested, 1);
                Interlocked.Exchange(ref stopRequested, 1);
                worker = cycleThread;
                poller = safetyThread;
            }
            try { art.ForceSourceOff(); } catch { }
            try { pico.AbortBlockCapture(); } catch { }
            try { art.AbortFiniteDividerCapture(); } catch { }
            if (worker != null && worker != Thread.CurrentThread) worker.Join();
            if (poller != null && poller != Thread.CurrentThread) poller.Join();
            try { pico.Dispose(); } catch { }
            try { art.Dispose(); } catch { }
            lock (sync)
            {
                initialized = false;
                disposed = true;
                disposing = false;
                cycleRunning = false;
                status = NewStatus(Task38ControllerState.Disposed, "已释放硬件资源");
                Monitor.PulseAll(sync);
            }
        }

        private void SafetyPollLoop()
        {
            while (Interlocked.CompareExchange(ref shutdownRequested, 0, 0) == 0)
            {
                Stopwatch pollPeriod = Stopwatch.StartNew();
                bool ready = false;
                string error = String.Empty;
                try { ready = art.ReadSafetyReady(); }
                catch (Exception ex) { error = "安全输入读取失败：" + ex.Message; }

                bool activeCycle;
                lock (sync)
                {
                    safetyReady = ready;
                    activeCycle = cycleRunning;
                    status.SafetyReady = ready;
                    if (!String.IsNullOrEmpty(error)) status.LastError = error;
                    status.UpdatedUtc = DateTime.UtcNow;
                    RefreshStartAllowedLocked();
                }

                if (!ready && activeCycle)
                {
                    Interlocked.Exchange(ref emergencyLatched, 1);
                    try { art.ForceSourceOff(); }
                    catch (Exception ex)
                    {
                        lock (sync) status.LastError = "急停后强制断开电源失败：" + ex.Message;
                    }
                }
                PollZeroVoltageIdle();
                int periodMilliseconds = configuration == null ? 50 : configuration.SafetyPollMilliseconds;
                int remainingMilliseconds = periodMilliseconds - (int)pollPeriod.ElapsedMilliseconds;
                if (remainingMilliseconds > 0) Thread.Sleep(remainingMilliseconds);
            }
        }

        private void CycleWorker()
        {
            Task38CycleResult result = new Task38CycleResult
            {
                RecordId = activeRecordId,
                IsBackgroundCapture = configuration.TargetVoltageV == 0,
                StartedUtc = DateTime.UtcNow,
                UsbSampleRateHz = configuration.ArtFiniteSampleRateHz,
                PicoSampleRateHz = configuration.PicoSampleRateHz,
                PicoRangeIndexUsed = configuration.PicoRangeIndex,
                AoHardLimitVolts = configuration.AoHardLimitVolts
            };
            Task38ExitReason exitReason = Task38ExitReason.None;
            string captureError = String.Empty;
            bool sourceWasEnabled = false;
            try
            {
                SetState(Task38ControllerState.Precheck, "检查安全联锁与残余电压");
                WaitForSafeResidualBeforeStart(result);
                ThrowIfEmergencyOrStop(false);

                SetState(Task38ControllerState.ReleasingGround, "释放接地开关并等待稳定");
                art.SetDigitalOutputs(false, false, true, false);
                WaitInterruptible(configuration.GroundReleaseSettleMilliseconds, false);
                ThrowIfEmergencyOrStop(false);
                if (result.IsBackgroundCapture)
                {
                    FeedbackSample zero = art.ReadFeedback(); PublishFeedback(zero); CheckZeroVoltage(zero);
                }

                art.WriteAoVolts(0.0);
                UpdateAoStatus(0.0);
                if (!ReadSafetyNow()) throw new EmergencyStopException();
                if (result.IsBackgroundCapture)
                {
                    // Keep HV and its indicator OFF. The existing arm/ground edge still
                    // synchronizes both recorders after the residual-voltage precheck.
                    exitReason = Task38ExitReason.TargetReached;
                }
                else
                {
                    art.SetDigitalOutputs(true, true, true, false);
                    sourceWasEnabled = true;
                    SetState(Task38ControllerState.HvEnableDelay, "HV ON，等待50 ms后开始步进升压");
                    WaitInterruptible(configuration.HvEnableDelayMilliseconds, true);
                    exitReason = RampUntilExit(result);
                }
            }
            catch (EmergencyStopException)
            {
                exitReason = Task38ExitReason.EmergencyStop;
                Interlocked.Exchange(ref emergencyLatched, 1);
            }
            catch (OperatorStopException)
            {
                exitReason = Task38ExitReason.OperatorStop;
            }
            catch (Exception ex)
            {
                exitReason = Task38ExitReason.HardwareFault;
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                SetState(Task38ControllerState.SourceOff, "关闭HV、指示灯并将AO置零");
                try
                {
                    art.ForceSourceOff();
                    UpdateAoStatus(0.0);
                    result.SourceOffUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    if (String.IsNullOrEmpty(result.ErrorMessage)) result.ErrorMessage = ex.Message;
                    exitReason = Task38ExitReason.HardwareFault;
                }
            }

            if (Interlocked.CompareExchange(ref emergencyLatched, 0, 0) != 0)
                exitReason = Task38ExitReason.EmergencyStop;

            try
            {
                bool shouldCapture = (result.IsBackgroundCapture && exitReason == Task38ExitReason.TargetReached)
                    || ShouldCapture(exitReason, sourceWasEnabled);
                if (shouldCapture)
                    captureError = CaptureAndGround(result);
                else
                    GroundWithoutCapture(result);
            }
            catch (Exception ex)
            {
                captureError = ex.Message;
                try { GroundWithoutCapture(result); } catch { }
            }

            if (Interlocked.CompareExchange(ref emergencyLatched, 0, 0) != 0)
                exitReason = Task38ExitReason.EmergencyStop;

            if (!String.IsNullOrEmpty(captureError))
            {
                result.ErrorMessage = AppendError(result.ErrorMessage, captureError);
                if (exitReason == Task38ExitReason.TargetReached || exitReason == Task38ExitReason.OperatorStop)
                    exitReason = Task38ExitReason.CaptureFault;
            }

            if (result.BothDevicesArmedBeforeGroundTrigger &&
                (result.UsbDividerRawVolts.Length != configuration.ArtFiniteSampleCount || result.PicoRawAdc.Length != configuration.PicoSampleCount))
            {
                result.ErrorMessage = AppendError(result.ErrorMessage, String.Format(
                    "同步采集数据不完整：电压 {0}/{1} 点，局放 {2}/{3} 点；本轮不可用于测量。",
                    result.UsbDividerRawVolts.Length, configuration.ArtFiniteSampleCount, result.PicoRawAdc.Length, configuration.PicoSampleCount));
                if (exitReason == Task38ExitReason.TargetReached || exitReason == Task38ExitReason.OperatorStop) exitReason = Task38ExitReason.CaptureFault;
            }
            if (!String.IsNullOrEmpty(result.ErrorMessage) && (exitReason == Task38ExitReason.TargetReached || exitReason == Task38ExitReason.OperatorStop))
                exitReason = Task38ExitReason.HardwareFault;

            bool faulted = exitReason == Task38ExitReason.EmergencyStop ||
                exitReason == Task38ExitReason.CurrentLimitStall ||
                exitReason == Task38ExitReason.HardOverCurrent ||
                exitReason == Task38ExitReason.OverVoltage ||
                exitReason == Task38ExitReason.RampTimeout ||
                exitReason == Task38ExitReason.HardwareFault ||
                exitReason == Task38ExitReason.CaptureFault;
            result.ExitReason = exitReason;
            result.CompletedUtc = DateTime.UtcNow;
            result.Success = !faulted && String.IsNullOrEmpty(result.ErrorMessage) &&
                result.BothDevicesArmedBeforeGroundTrigger &&
                result.UsbDividerRawVolts.Length == configuration.ArtFiniteSampleCount &&
                result.PicoRawAdc.Length == configuration.PicoSampleCount;

            lock (sync)
            {
                lastResult = result;
                cycleRunning = false;
                cycleThread = null;
                status.CycleRunning = false;
                status.EmergencyLatched = Interlocked.CompareExchange(ref emergencyLatched, 0, 0) != 0;
                status.State = faulted ? Task38ControllerState.Faulted : Task38ControllerState.Completed;
                status.StateMessage = BuildCompletionMessage(result);
                status.LastError = result.ErrorMessage ?? String.Empty;
                status.UpdatedUtc = DateTime.UtcNow;
                RefreshStartAllowedLocked();
            }
        }

        private Task38ExitReason RampUntilExit(Task38CycleResult result)
        {
            SetState(Task38ControllerState.Ramping, "正在步进升压并监测AI0/AI2/AI4");
            Stopwatch rampClock = Stopwatch.StartNew();
            Stopwatch currentLimitedClock = new Stopwatch();
            bool currentLimited = false;
            double currentLimitedBaselineVoltage = 0.0;
            double ao = 0.0;

            while (true)
            {
                ThrowIfEmergencyOrStop(true);
                FeedbackSample feedback = art.ReadFeedback();
                PublishFeedback(feedback);
                result.PeakActualVoltageV = Math.Max(result.PeakActualVoltageV, Math.Abs(feedback.DividerVoltageV));
                result.PeakCurrentA = Math.Max(result.PeakCurrentA, Math.Abs(feedback.CurrentA));

                double actualVoltage = Math.Abs(feedback.DividerVoltageV);
                double current = Math.Abs(feedback.CurrentA);
                if (current >= configuration.EffectiveHardOverCurrentA)
                    return Task38ExitReason.HardOverCurrent;
                if (actualVoltage > configuration.TargetVoltageV * (1.0 + configuration.OverVoltageTripFraction))
                    return Task38ExitReason.OverVoltage;
                if (actualVoltage >= configuration.TargetVoltageV * (1.0 - configuration.TargetReachedToleranceFraction))
                    return Task38ExitReason.TargetReached;
                if (rampClock.Elapsed.TotalSeconds >= configuration.MaximumRampSeconds)
                    return Task38ExitReason.RampTimeout;

                if (current >= configuration.CurrentLimitA * configuration.CurrentLimitEnterFraction)
                {
                    if (!currentLimited)
                    {
                        currentLimited = true;
                        currentLimitedBaselineVoltage = actualVoltage;
                        currentLimitedClock.Restart();
                    }
                    else if (actualVoltage >= currentLimitedBaselineVoltage + configuration.VoltageGrowthResetThresholdV)
                    {
                        currentLimitedBaselineVoltage = actualVoltage;
                        currentLimitedClock.Restart();
                    }
                    if (currentLimitedClock.Elapsed.TotalSeconds >= configuration.CurrentLimitNoGrowthSeconds)
                        return Task38ExitReason.CurrentLimitStall;
                }
                else if (current < configuration.CurrentLimitA * configuration.CurrentLimitLeaveFraction)
                {
                    currentLimited = false;
                    currentLimitedClock.Reset();
                }

                if (!currentLimited)
                {
                    double next = Math.Min(ao + configuration.RampStepAoVolts, configuration.AoHardLimitVolts);
                    if (next > ao + 1e-12)
                    {
                        art.WriteAoVolts(next);
                        ao = next;
                        result.FinalAoCommandVolts = ao;
                        UpdateAoStatus(ao);
                    }
                }
                WaitInterruptible(configuration.RampStepDelayMilliseconds, true);
            }
        }

        private string CaptureAndGround(Task38CycleResult result)
        {
            List<string> errors = new List<string>();
            bool artArmed = false;
            bool picoArmed = false;
            SetState(Task38ControllerState.ArmingAcquisition,
                "配置USB3135 80 kS/s有限触发与PicoScope 125 MS/s单段Block采集");
            try
            {
                art.StopMonitoringInputs();
                art.ArmFiniteDividerCapture();
                artArmed = true;
            }
            catch (Exception ex) { errors.Add("USB3135布置失败：" + ex.Message); }
            try
            {
                pico.ArmBlockCapture(configuration.PicoRangeIndex);
                picoArmed = true;
            }
            catch (Exception ex) { errors.Add("PicoScope布置失败：" + ex.Message); }

            result.BothDevicesArmedBeforeGroundTrigger = artArmed && picoArmed;
            SetState(Task38ControllerState.GroundDelay,
                result.BothDevicesArmedBeforeGroundTrigger
                    ? "两台采集设备已等待同一物理触发，延时200 ms"
                    : "采集布置不完整；电源已关闭，仍按安全策略延时后接地");
            WaitGroundDelayOrShutdown(configuration.GroundDelayAfterArmedMilliseconds);

            art.SetDigitalOutputs(false, false, true, true);
            result.GroundTriggerUtc = DateTime.UtcNow;
            Stopwatch captureClock = Stopwatch.StartNew();
            SetState(Task38ControllerState.CaptureWaiting, "已发出接地导通命令，等待同步数据，超时 " + (configuration.TriggerWaitTimeoutMilliseconds / 1000.0).ToString("0.###") + " 秒；命令发出不代表已收到有效触发");

            if (artArmed)
            {
                try
                {
                    result.UsbDividerRawVolts = art.ReadFiniteDividerCapture(RemainingCaptureWait(captureClock, configuration.ArtCaptureTimeoutMilliseconds));
                    if (result.IsBackgroundCapture)
                    {
                        double ratio = configuration.FiniteDividerRatioVPerV > 0 ? configuration.FiniteDividerRatioVPerV : configuration.DividerRatioVPerV;
                        double peak = 0;
                        foreach (double raw in result.UsbDividerRawVolts)
                        {
                            if (!Finite(raw) || !Finite(raw * ratio)) throw new InvalidOperationException("零压检查电压段数据无效，不能确认无高压。");
                            peak = Math.Max(peak, Math.Abs(raw * ratio));
                        }
                        result.PeakActualVoltageV = Math.Max(result.PeakActualVoltageV, peak);
                        if (peak > configuration.EffectiveZeroVoltageThresholdV)
                            throw new InvalidOperationException("零压异常：电压段峰值 " + peak.ToString("F2") + " V 超过阈值。保留原始数据，禁止继续试验和靠近设备。");
                    }
                }
                catch (Exception ex) { errors.Add("USB3135读取失败：" + ex.Message); }
            }
            if (picoArmed)
            {
                try
                {
                    PicoBlockCapture capture = pico.ReadBlockCapture(RemainingCaptureWait(captureClock, configuration.PicoCaptureTimeoutMilliseconds));
                    result.PicoRawAdc = capture.RawAdc ?? new short[0];
                    result.PicoSampleIntervalNanoseconds = capture.SampleIntervalNanoseconds;
                    if (!(capture.SampleIntervalNanoseconds > 0) || Double.IsInfinity(capture.SampleIntervalNanoseconds))
                        throw new InvalidOperationException("局放采集返回无效采样间隔。");
                    result.PicoSampleRateHz = 1000000000.0 / capture.SampleIntervalNanoseconds;
                    result.PicoResolutionModeBits = capture.ResolutionModeBits;
                    result.PicoRangeHardwareBits = capture.RangeHardwareBits;
                    result.PicoMaximumAdc = capture.MaximumAdc;
                    result.PicoFullScaleVolts = capture.FullScaleVolts;
                    result.PicoHardwareOverflowDiagnosticOnly = capture.HardwareOverflow;
                    result.PicoVariant = capture.Variant;
                    result.PicoSerial = capture.Serial;
                }
                catch (Exception ex) { errors.Add("PicoScope读取失败：" + ex.Message); }
            }

            bool dischargeVerifiedSafe = RestoreMonitoringAndVerifyDischarge(errors);
            if (!configuration.HoldGroundAfterCapture && dischargeVerifiedSafe)
            {
                WaitUninterruptible(configuration.GroundTriggerPulseMilliseconds);
                art.SetDigitalOutputs(false, false, true, false);
            }
            return String.Join(" | ", errors.ToArray());
        }

        private int RemainingCaptureWait(Stopwatch clock, int deviceLimit)
        {
            // A completed second device may still be read, but it must not get a new full timeout.
            return (int)Math.Max(1, Math.Min(deviceLimit, configuration.TriggerWaitTimeoutMilliseconds - clock.ElapsedMilliseconds));
        }

        private void GroundWithoutCapture(Task38CycleResult result)
        {
            try { pico.AbortBlockCapture(); } catch { }
            try { art.AbortFiniteDividerCapture(); } catch { }
            WaitGroundDelayOrShutdown(configuration.GroundDelayAfterArmedMilliseconds);
            art.SetDigitalOutputs(false, false, true, true);
            result.GroundTriggerUtc = DateTime.UtcNow;
            List<string> errors = new List<string>();
            RestoreMonitoringAndVerifyDischarge(errors);
            if (errors.Count > 0) result.ErrorMessage = AppendError(result.ErrorMessage, String.Join(" | ", errors.ToArray()));
        }

        private bool RestoreMonitoringAndVerifyDischarge(List<string> errors)
        {
            try { art.AbortFiniteDividerCapture(); } catch { }
            try { pico.AbortBlockCapture(); } catch { }
            try { art.RestartMonitoringInputs(); }
            catch (Exception ex)
            {
                errors.Add("恢复AI监测失败：" + ex.Message);
                return false;
            }

            SetState(Task38ControllerState.DischargeVerify, "保持接地并确认残余电压降至安全阈值");
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < configuration.DischargeVerifyTimeoutMilliseconds)
            {
                try
                {
                    FeedbackSample feedback = art.ReadFeedback();
                    PublishFeedback(feedback);
                    if (configuration.TargetVoltageV == 0) CheckZeroVoltage(feedback);
                    if (ResidualSafe(feedback)) return true;
                }
                catch (Exception ex)
                {
                    errors.Add("放电确认失败：" + ex.Message);
                    return false;
                }
                Thread.Sleep(configuration.SafetyPollMilliseconds);
            }
            errors.Add("接地后残余电压未在超时时间内降至安全阈值。" );
            return false;
        }

        private void WaitForSafeResidualBeforeStart(Task38CycleResult result)
        {
            art.SetDigitalOutputs(false, false, true, true);
            Stopwatch timer = Stopwatch.StartNew();
            while (true)
            {
                ThrowIfEmergencyOrStop(false);
                FeedbackSample feedback = art.ReadFeedback();
                PublishFeedback(feedback);
                result.PeakActualVoltageV = Math.Max(result.PeakActualVoltageV, Math.Abs(feedback.DividerVoltageV));
                if (configuration.TargetVoltageV == 0) CheckZeroVoltage(feedback);
                if (ResidualSafe(feedback)) return;
                if (timer.ElapsedMilliseconds >= configuration.PreStartDischargeTimeoutMilliseconds)
                    throw new InvalidOperationException("加压前残余电压未降至安全阈值，保持接地并禁止启动。" );
                Thread.Sleep(configuration.SafetyPollMilliseconds);
            }
        }

        private bool ShouldCapture(Task38ExitReason reason, bool sourceWasEnabled)
        {
            if (!sourceWasEnabled) return false;
            if (reason == Task38ExitReason.EmergencyStop) return configuration.CaptureOnEmergencyStop;
            if (reason == Task38ExitReason.HardOverCurrent || reason == Task38ExitReason.OverVoltage ||
                reason == Task38ExitReason.RampTimeout || reason == Task38ExitReason.HardwareFault)
                return configuration.CaptureOnNonEmergencyFault;
            return true;
        }

        private bool ReadSafetyNow()
        {
            bool ready = art.ReadSafetyReady();
            lock (sync)
            {
                safetyReady = ready;
                status.SafetyReady = ready;
                status.UpdatedUtc = DateTime.UtcNow;
                RefreshStartAllowedLocked();
            }
            return ready;
        }

        private void WaitInterruptible(int milliseconds, bool sourceEnergized)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds)
            {
                ThrowIfEmergencyOrStop(sourceEnergized);
                int remaining = milliseconds - (int)timer.ElapsedMilliseconds;
                Thread.Sleep(Math.Max(1, Math.Min(configuration.SafetyPollMilliseconds, remaining)));
            }
        }

        private static void WaitUninterruptible(int milliseconds)
        {
            if (milliseconds > 0) Thread.Sleep(milliseconds);
        }

        private void WaitGroundDelayOrShutdown(int milliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds &&
                Interlocked.CompareExchange(ref shutdownRequested, 0, 0) == 0)
            {
                int remaining = milliseconds - (int)timer.ElapsedMilliseconds;
                Thread.Sleep(Math.Max(1, Math.Min(configuration.SafetyPollMilliseconds, remaining)));
            }
        }

        private void ThrowIfEmergencyOrStop(bool sourceEnergized)
        {
            if (Interlocked.CompareExchange(ref emergencyLatched, 0, 0) != 0 || !safetyReady)
            {
                if (sourceEnergized) try { art.ForceSourceOff(); } catch { }
                throw new EmergencyStopException();
            }
            if (Interlocked.CompareExchange(ref stopRequested, 0, 0) != 0)
                throw new OperatorStopException();
        }

        private void PublishFeedback(FeedbackSample feedback)
        {
            if (feedback == null || !Finite(feedback.DividerVoltageV) || !Finite(feedback.SourceVoltageV) || !Finite(feedback.CurrentA) ||
                !Finite(feedback.DividerFeedbackRawV) || !Finite(feedback.SourceVoltageFeedbackRawV) || !Finite(feedback.CurrentFeedbackRawV))
                throw new InvalidOperationException("高压控制模块反馈无效，已中止本轮升压。未确认放电，禁止靠近设备！");
            lock (sync)
            {
                status.ActualVoltageV = feedback.DividerVoltageV;
                status.SourceVoltageFeedbackV = feedback.SourceVoltageV;
                status.CurrentA = feedback.CurrentA;
                status.DividerFeedbackRawV = feedback.DividerFeedbackRawV;
                status.SourceVoltageFeedbackRawV = feedback.SourceVoltageFeedbackRawV;
                status.CurrentFeedbackRawV = feedback.CurrentFeedbackRawV;
                status.UpdatedUtc = DateTime.UtcNow;
            }
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private void CheckZeroVoltage(FeedbackSample feedback)
        {
            if (Math.Abs(feedback.DividerVoltageV) > configuration.EffectiveZeroVoltageThresholdV ||
                Math.Abs(feedback.SourceVoltageV) > configuration.EffectiveZeroVoltageThresholdV)
                throw new InvalidOperationException(String.Format("零压异常：分压器 {0:F2} V，电源反馈 {1:F2} V，阈值 {2:F2} V。HV关闭、AO为0仍检测到电压；检查残压、反馈线路或电源异常。未确认放电，禁止靠近设备。",
                    feedback.DividerVoltageV, feedback.SourceVoltageV, configuration.EffectiveZeroVoltageThresholdV));
        }
        private void PollZeroVoltageIdle()
        {
            // Serialize against StartCycle; never read the monitor AI while finite AI owns it.
            lock (sync)
            {
                if (!initialized || disposing || disposed || cycleRunning ||
                    (configuration.TargetVoltageV != 0 && !configuration.ControlBoxVerification)) return;
                try
                {
                    FeedbackSample feedback = art.ReadFeedback(); PublishFeedback(feedback);
                    if (!configuration.ControlBoxVerification) CheckZeroVoltage(feedback);
                }
                catch (Exception ex)
                {
                    string error = ex.Message;
                    if (status.State != Task38ControllerState.Faulted)
                    {
                        try { art.ForceSourceOff(); } catch (Exception off) { error += " | 关断失败：" + off.Message; }
                        try { art.SetDigitalOutputs(false, false, true, true); } catch (Exception ground) { error += " | 接地命令失败：" + ground.Message; }
                    }
                    status.State = Task38ControllerState.Faulted;
                    status.StateMessage = status.LastError = (configuration.ControlBoxVerification ? "控制箱输入诊断失败：" : "零压监测报警：") + error;
                    RefreshStartAllowedLocked();
                }
            }
        }
        private bool ResidualSafe(FeedbackSample feedback)
        {
            return Math.Abs(feedback.DividerVoltageV) <= configuration.ResidualVoltageSafeThresholdV &&
                Math.Abs(feedback.SourceVoltageV) <= configuration.ResidualVoltageSafeThresholdV;
        }

        private void UpdateAoStatus(double aoVolts)
        {
            lock (sync)
            {
                status.AoCommandVolts = aoVolts;
                status.UpdatedUtc = DateTime.UtcNow;
            }
        }

        private void SetState(Task38ControllerState state, string message)
        {
            lock (sync)
            {
                status.State = state;
                status.StateMessage = message;
                status.UpdatedUtc = DateTime.UtcNow;
            }
        }

        private void RefreshStartAllowedLocked()
        {
            status.Initialized = initialized;
            status.CycleRunning = cycleRunning;
            status.SafetyReady = safetyReady;
            status.EmergencyLatched = Interlocked.CompareExchange(ref emergencyLatched, 0, 0) != 0;
            status.StartAllowed = initialized && !configuration.ControlBoxVerification && !cycleRunning && safetyReady &&
                !status.EmergencyLatched && status.State != Task38ControllerState.Disposed &&
                status.State != Task38ControllerState.Faulted;
        }

        private void EnsureInitializedLocked()
        {
            ThrowIfDisposed();
            if (!initialized) throw new InvalidOperationException("Task 38 controller is not initialized.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed || disposing) throw new ObjectDisposedException("Task38Controller");
        }

        private static Task38StatusSnapshot NewStatus(Task38ControllerState state, string message)
        {
            return new Task38StatusSnapshot
            {
                State = state,
                StateMessage = message,
                LastError = String.Empty,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        private static string AppendError(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first)) return second ?? String.Empty;
            if (String.IsNullOrWhiteSpace(second)) return first;
            return first + " | " + second;
        }

        private static string BuildCompletionMessage(Task38CycleResult result)
        {
            if (!String.IsNullOrEmpty(result.ErrorMessage))
                return "本轮存在异常，不能仅凭软件确认放电完成；禁止靠近设备。" + result.ErrorMessage;
            if (result.IsBackgroundCapture && result.Success)
                return "零电压背景采集完成：未使能HV，AO保持0 V，已执行开关同步触发。";
            switch (result.ExitReason)
            {
                case Task38ExitReason.TargetReached: return "达到预设电压，已完成关断、接地与同步采集";
                case Task38ExitReason.EmergencyStop: return "急停触发：已立即关断电源并执行安全接地";
                case Task38ExitReason.OperatorStop: return "操作者停止：已关断、接地并完成可用采集";
                case Task38ExitReason.CurrentLimitStall: return "最大电流条件下5秒无有效升压：疑似泄漏过大/绝缘过低";
                case Task38ExitReason.HardOverCurrent: return "硬过流保护触发";
                case Task38ExitReason.OverVoltage: return "过电压保护触发";
                case Task38ExitReason.RampTimeout: return "升压总时限到达";
                case Task38ExitReason.CaptureFault: return "关断与接地完成，但同步采集失败";
                default: return String.IsNullOrEmpty(result.ErrorMessage) ? "循环结束" : "硬件故障：" + result.ErrorMessage;
            }
        }

        private sealed class EmergencyStopException : Exception { }
        private sealed class OperatorStopException : Exception { }
    }
}
