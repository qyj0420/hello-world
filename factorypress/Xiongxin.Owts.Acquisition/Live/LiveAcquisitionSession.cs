using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 实机 Art + Pico 会话：安全优先最小加压/采集循环骨架。
    /// 必须包在 <see cref="SingletonAcquisitionSession"/> 外。
    /// 硬安全门（代码 + FakeLive Smoke 证明）：
    /// 1) 急停有效低 → AO=0，禁止非零与 HV；P0.4 独立串行 ~10ms/超时~300ms；未知不冒充低、不以历史高放行
    /// 1b) 非零启动：HV ON 前 AO=0+查P0.4；HVON 后同步建 AI → 延时 100ms → 再 HVUP；背景默认 500 V 可配
    /// 1c) 达压 ±5% 进窗停增 AO；异常过压默认 +20%（可配）用原始峰值
    /// 2) 未校准 → 禁止非零（0 kV 噪声对照除外）
    /// 2b) 加压接通：P0.0 HVON 与 P0.3 加压指示灯同步；开关(P0.5/阻抗)独立
    /// 3) 目标 ≤0 → 仅噪声对照，不算可加压
    /// 4) 未台架标定（VoltageGain 危险占位如 1000）→ 禁止非零真高压
    /// 5) EmergencyStop → 立即关源锁存
    /// 6) 退出路径（过流/超压/急停/超时/异常）finally 强制 AO=0、HV 关
    /// 7) 加压中异常：HV OFF（P0.0↔P0.3）+ Pico 同步采集（整机掉线除外）
    /// 8) 仅 Pico 掉线：加压控制继续；ClosePicoOnly + TryReopenPico；提示通讯异常建议重压
    /// </summary>
    public sealed class LiveAcquisitionSession : IAcquisitionSession
    {
        readonly LiveHardwareOptions _hw;
        readonly ILiveHardwareBackend _backend;
        readonly bool _ownsBackend;
        readonly IFirstZeroPhaseEvaluator _firstZero;
        readonly object _sync = new object();
        AcquisitionSessionOptions _options;
        bool _open;
        bool _disposed;
        bool _calibrated;
        bool _calibrating;
        bool _impedanceWide;
        bool _latched;
        bool _holdGroundActive;
        bool _lastDischargeVerified;
        bool _cycleRunning;
        string _message = string.Empty;
        string _lastError = string.Empty;
        string _lastAi2ScaleStatus = string.Empty;
        double _lastSourceEngV;
        double _lastCurrentA;
        int _stepHvonMs;
        int _stepHvupMs;
        int _stepHvoffMs;
        int _stepCaptureMs;
        double _stepDelayLeakageA = double.NaN;
        Stopwatch _pressIntervalEpoch;
        double _lastDividerEngV;
        /// <summary>HVOFF 后 AI4 切 finite 期间抑制直流监控假平台（分压缓存不再刷高压）。</summary>
        volatile bool _dividerMonitorSuspendedAfterHvOff;
        int _operatorStop;
        LiveEmergencyMonitor _estopMonitor;
        readonly IdleDiRebuildPolicy _idleDiRebuild = new IdleDiRebuildPolicy();
        int _idleDiRebuildInFlight;
        string _picoSerialCached = string.Empty;
        string _artDeviceCached = string.Empty;
        volatile bool _pressAbortCapturePending;
        string _picoCommWarning = string.Empty;
        string _lastEStopSampleLog = string.Empty;
        bool? _lastEStopPressedLogged;
        /// <summary>首因已落盘 session-fault（非 cleanup）；同会话不覆盖。</summary>
        bool _sessionFaultLatched;
        string _sessionFaultPriorDetail = string.Empty;

        public LiveAcquisitionSession(LiveHardwareOptions options)
            : this(options, null, null)
        {
        }

        /// <summary>注入 FakeLive 等后端（联锁单测）；null = 真 Art/Pico 后端。</summary>
        public LiveAcquisitionSession(LiveHardwareOptions options, ILiveHardwareBackend backend)
            : this(options, backend, null)
        {
        }

        /// <summary>注入 FakeLive 后端与可选 Task6 首零相位评估器。</summary>
        public LiveAcquisitionSession(LiveHardwareOptions options, ILiveHardwareBackend backend, IFirstZeroPhaseEvaluator firstZeroPhaseEvaluator)
        {
            _hw = options ?? new LiveHardwareOptions();
            _firstZero = firstZeroPhaseEvaluator ?? new MissingDllFirstZeroPhaseEvaluator();
            if (backend != null)
            {
                _backend = backend;
                _ownsBackend = false;
            }
            else
            {
                _backend = new ArtPicoLiveBackend(_hw);
                _ownsBackend = true;
            }
        }

        /// <summary>首零相位评估器（缺 DLL → MissingDll）。</summary>
        public IFirstZeroPhaseEvaluator FirstZeroPhaseEvaluator { get { return _firstZero; } }

        /// <summary>测试钩：当前后端（FakeLive 断言 AO）。</summary>
        public ILiveHardwareBackend Backend { get { return _backend; } }

        /// <summary>当前 Pico/ART 硬件选项（加压与校准 Rapid 共用 timebase/分辨率/间隔）。</summary>
        public LiveHardwareOptions HardwareOptions { get { return _hw; } }

        /// <summary>Clear/Close 失败后为 true；禁止同进程再 Open。</summary>
        public bool CleanupFailed
        {
            get
            {
                var art = _backend as ArtPicoLiveBackend;
                return art != null && art.CleanupFailed;
            }
        }

        public event EventHandler<HardwareStatusChangedEventArgs> StatusChanged;
        public bool IsOpen { get { return _open && !_disposed && _backend.IsOpen; } }
        public bool ConnectionIndicationLampHigh { get { return _backend.ConnectionLampHigh; } }

        public HardwareIdentityProbeResult Probe(HardwareProbeRequest request)
        {
            string err = _backend.ProbeDrivers();
            string art = !string.IsNullOrEmpty(_artDeviceCached) ? _artDeviceCached : _hw.ArtDeviceName;
            string pico = !string.IsNullOrEmpty(_picoSerialCached) ? _picoSerialCached : (_hw.PicoSerial ?? string.Empty);
            var fake = _backend as FakeLiveHardwareBackend;
            if (fake != null && fake.IsOpen)
            {
                if (!string.IsNullOrEmpty(fake.OpenedArtDeviceName)) art = fake.OpenedArtDeviceName;
                pico = fake.OpenedPicoSerial ?? string.Empty;
            }
            string displayPico = string.IsNullOrEmpty(pico) ? "(first unit)" : pico;
            var probe = new HardwareIdentityProbeResult
            {
                Connected = _open,
                IdentityValid = string.IsNullOrEmpty(err),
                ControllerSerial = art ?? string.Empty,
                AcquisitionSerial = displayPico,
                StatusCode = string.IsNullOrEmpty(err) ? (_open ? "Open" : "DriversPresent") : "DriverMissing",
                Message = string.IsNullOrEmpty(err)
                    ? (_open
                        ? (StatusCopy.Connected + "；" + StatusCopy.PicoSdkOnlyNoSplash)
                        : "驱动可加载（尚未 Open）；" + StatusCopy.PicoSdkOnlyNoSplash)
                    : err,
                AssetMatchStatus = fake != null ? "FakeLive" : "Live",
                Diagnostic = StatusCopy.PicoSdkOnlyNoSplash
            };
            if (!string.IsNullOrEmpty(err))
                return probe;

            string expArt = !string.IsNullOrWhiteSpace(_hw.ExpectedArtIdentity)
                ? _hw.ExpectedArtIdentity
                : (request != null ? request.RequestedControllerLocation : null);
            string expPico = !string.IsNullOrWhiteSpace(_hw.ExpectedPicoSerial)
                ? _hw.ExpectedPicoSerial
                : (request != null ? request.RequestedAcquisitionLocation : null);
            // 若 hw 期望空但 Open 选项/配置有 PicoSerial 作为目标，仍用 Expected*；空则跳过强制
            return HardwareDualIdentity.ApplyToProbe(probe, expArt, expPico);
        }

        public void Open(AcquisitionSessionOptions options)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                if (SessionFault.HasCleanupErrorForbiddingReopen() || CleanupFailed)
                    throw new InvalidOperationException(StatusCopy.CleanupFailedNoReopen
                        + "；落盘=" + SessionFault.DefaultDirectory());
                if (_open) throw new InvalidOperationException("实机会话已 Open，禁止重复 Open（Pico 全局只允许一次）。");
                _options = options ?? new AcquisitionSessionOptions { Simulation = false };
                _options.Simulation = false;
                try
                {
                    string device = !string.IsNullOrWhiteSpace(_options.ArtDeviceName) ? _options.ArtDeviceName : _hw.ArtDeviceName;
                    string serial = !string.IsNullOrWhiteSpace(_options.PicoSerial) ? _options.PicoSerial : _hw.PicoSerial;
                    _artDeviceCached = device ?? string.Empty;
                    _picoSerialCached = serial ?? string.Empty;
                    if (_options.AoMaximumVolts > 0) _hw.AoMaximumVolts = Math.Min(_options.AoMaximumVolts, 5.0);
                    if (_options.VoltageGainVPerAoVolt > 0) _hw.VoltageGainVPerAoVolt = _options.VoltageGainVPerAoVolt;
                    if (_options.DividerRatioVPerV > 0) _hw.DividerRatioVPerV = _options.DividerRatioVPerV;
                    if (_options.CurrentScaleAPerVolt > 0) _hw.CurrentScaleAPerVolt = _options.CurrentScaleAPerVolt;
                    if (_options.SourceVoltageScaleVPerV > 0) _hw.SourceVoltageScaleVPerV = _options.SourceVoltageScaleVPerV;
                    if (_options.CurrentLimitA > 0) _hw.CurrentLimitA = _options.CurrentLimitA;
                    if (_options.HardOverCurrentA > 0) _hw.HardOverCurrentA = _options.HardOverCurrentA;
                    if (_options.TargetReachedToleranceFraction > 0) _hw.TargetReachedToleranceFraction = _options.TargetReachedToleranceFraction;
                    // Open 时以会话选项为准重置台架写入标志（避免上次 Apply 粘滞）
                    _hw.BenchCalibrationApplied = _options.BenchCalibrationApplied;
                    SyncFakeGainFromHw();

                    _backend.Open(device, serial);
                    ForceSourceOffSafe();
                    _backend.WriteDigitalOutputs(false, false, true, false); // P0.5 置高并维持
                    _open = true;
                    _calibrated = false;
                    _calibrating = false;
                    _latched = false;
                    _sessionFaultLatched = false;
                    _sessionFaultPriorDetail = string.Empty;
                    _holdGroundActive = false;
                    _lastDischargeVerified = false;
                    _pressAbortCapturePending = false;
                    _picoCommWarning = string.Empty;
                    _message = "实机 Open → " + StatusCopy.Connected + "（≠ " + StatusCopy.ReadyToEnergize + "）"
                        + "；P0.4 急停独立监视已启动；加压时 P0.0/P0.3 同步；P0.5 连接灯已置高";
                    _lastError = string.Empty;
                    StartEStopMonitorUnlocked();
                    // 双序列号：配置了期望 ART/Pico 时须匹配，否则回滚 Open（长会话前硬门）
                    AssertDualIdentityOrRollbackUnlocked();
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    try { StopEStopMonitor(); } catch { }
                    try
                    {
                        if (_backend != null && _backend.IsOpen)
                            _backend.WriteDigitalOutputs(false, false, false, false);
                    }
                    catch { }
                    try { _backend.Close(); } catch { }
                    _open = false;
                    _artDeviceCached = string.Empty;
                    _picoSerialCached = string.Empty;
                    throw new InvalidOperationException(ex.Message, ex);
                }
            }
            RaiseStatus();
        }

        public void Close()
        {
            StopEStopMonitor();
            lock (_sync)
            {
                try { ForceSourceOffSafe(); } catch { }
                try
                {
                    if (_backend.IsOpen)
                        _backend.WriteDigitalOutputs(false, false, false, false); // P0.5 置低
                }
                catch { }
                Exception closeEx = null;
                try { _backend.Close(); }
                catch (Exception ex) { closeEx = ex; }
                // 仅真 cleanupFailed（ClearTask/CloseUnit 失败保留句柄）落盘禁重开。
                // 软关闭异常（跨线程租约、StopTask 警告等）只记日志，不得永久禁 Connect。
                if (CleanupFailed)
                {
                    try
                    {
                        var prior = _sessionFaultPriorDetail;
                        var fault = SessionFault.Create(
                            "live",
                            SessionFaultCategory.CleanupError,
                            "Cleanup",
                            StatusCopy.CleanupFailedNoReopen + "：" + (closeEx != null ? closeEx.Message : "cleanupFailed"),
                            "Session");
                        fault.PriorDetail = prior;
                        SessionFault.WriteDefault(fault, cleanup: true);
                    }
                    catch { }
                }
                else if (closeEx != null)
                {
                    try
                    {
                        System.Console.WriteLine("[LiveClose] 软关闭异常（不置清理门）：" + closeEx.Message);
                        _lastError = "软关闭：" + closeEx.Message;
                    }
                    catch { }
                }
                _open = false;
                _calibrated = false;
                _calibrating = false;
                _holdGroundActive = false;
                _artDeviceCached = string.Empty;
                _picoSerialCached = string.Empty;
                _message = CleanupFailed
                    ? "实机会话已关闭（清理失败，须确认关断后可复位清理门再 Connect）"
                    : "实机会话已关闭";
                // 软关闭异常不再向上抛：Release 路径应总能放下会话，便于同进程再 Connect
                if (closeEx != null && CleanupFailed) throw closeEx;
            }
            RaiseStatus();
        }

        public AcquisitionStatusSnapshot GetStatus()
        {
            bool estop = true;
            string err = _lastError;
            string estopLog = _lastEStopSampleLog ?? string.Empty;
            byte rawDi = 0;
            bool rawValid = false;
            if (_open)
            {
                try
                {
                    // 顶栏轮询优先用 P0.4 监视缓存，避免每 200~500ms 硬读 DI 与加压抢锁 → 未知/按下抖动。
                    // 门禁加压路径仍走 BlocksEnergizePreferCache / 实读，安全不受影响。
                    LiveEStopSample sample = null;
                    long ageMs = long.MaxValue;
                    bool fromCache = false;
                    if (_estopMonitor != null)
                        fromCache = _estopMonitor.TryGetCachedSample(out sample, out ageMs);
                    if (!fromCache || sample == null)
                    {
                        var art = _backend as ArtPicoLiveBackend;
                        if (art != null)
                            fromCache = art.TryGetCachedEStopSample(out sample, out ageMs);
                    }
                    if (!fromCache || sample == null)
                        sample = _backend.ReadEStopSample();
                    estop = sample.BlocksEnergize; // 未知或有效低均阻断；未知≠冒充低
                    estopLog = sample.FormatLog();
                    if (sample.ReadOk)
                    {
                        rawDi = sample.RawDi;
                        rawValid = true;
                    }
                    else
                    {
                        err = string.IsNullOrEmpty(err) ? ("P0.4 回读失败：" + sample.Error) : err;
                    }
                    NoteEStopSample(sample);
                }
                catch (Exception ex)
                {
                    err = "P0.4 回读失败：" + ex.Message;
                    estop = true;
                    estopLog = "P0.4 raw=? 判定=未知 err=" + ex.Message;
                }
            }
            return new AcquisitionStatusSnapshot
            {
                State = !_open ? AcquisitionControllerState.Uninitialized
                    : _latched ? AcquisitionControllerState.Faulted
                    : _cycleRunning ? AcquisitionControllerState.Ramping
                    : AcquisitionControllerState.SafeIdle,
                Initialized = _open,
                CycleRunning = _cycleRunning,
                SafetyReady = _open && !estop,
                EmergencyLatched = _latched,
                StateMessage = _message,
                LastError = err ?? string.Empty,
                Simulation = false,
                UpdatedUtc = DateTime.UtcNow,
                IsDeviceConnected = _open,
                ConnectionLampHigh = _backend.ConnectionLampHigh,
                EStopPressed = estop,
                EStopRawDi = rawDi,
                EStopRawDiValid = rawValid,
                EStopHardwareLog = estopLog,
                IsCalibrated = _calibrated,
                IsCalibrating = _calibrating,
                ImpedanceBandwidthWide = _impedanceWide,
                ConnectionStatusLabel = _open ? StatusCopy.Connected : string.Empty,
                HoldGroundActive = _holdGroundActive,
                DischargeVerified = _lastDischargeVerified,
                BenchCalibrationApplied = _hw.BenchCalibrationApplied,
                SourceVoltageScaleCalibrated = LiveAi2SourceScaling.IsCalibrated(_hw),
                Ai2SourceScaleStatus = string.IsNullOrEmpty(_lastAi2ScaleStatus)
                    ? LiveAi2SourceScaling.DescribeScale(_hw)
                    : _lastAi2ScaleStatus,
                LastSourceVoltageEngineeringV = _dividerMonitorSuspendedAfterHvOff ? 0.0 : _lastSourceEngV, // 关源后清源压缓存，禁止绿线假平台
                ActualVoltageV = _dividerMonitorSuspendedAfterHvOff ? 0.0 : _lastDividerEngV, // 到位/实测以分压为准；源压仅 LastSourceVoltageEngineeringV 显示
                CurrentA = _lastCurrentA,
                LastDividerEngineeringV = _dividerMonitorSuspendedAfterHvOff ? 0.0 : _lastDividerEngV,
                StepHvonElapsedMs = _stepHvonMs,
                StepHvupElapsedMs = _stepHvupMs,
                StepHvoffElapsedMs = _stepHvoffMs,
                StepCaptureElapsedMs = _stepCaptureMs,
                StepDelayLeakageA = _stepDelayLeakageA
            };
        }

        public Task<AcquisitionCycleResult> RunCycleAsync(AcquisitionCycleRequest request, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            if (!_open) throw new InvalidOperationException("实机会话未 Open。");

            var result = new AcquisitionCycleResult
            {
                StartedUtc = DateTime.UtcNow,
                IsBackgroundCapture = request != null && request.IsBackgroundCapture,
                ArtSerial = _hw.ArtDeviceName,
                PicoSerial = _hw.PicoSerial ?? string.Empty,
                PicoRawAdc = new short[0],
                UsbDividerRawVolts = new double[0]
            };

            double targetV = request != null ? request.TargetVoltageV : 0;
            bool noiseOnly = !(targetV > 0);
            Interlocked.Exchange(ref _operatorStop, 0);

            bool isBg = request != null && request.IsBackgroundCapture;
            lock (_sync)
            {
                _cycleRunning = true;
                _calibrating = isBg;
                if (isBg)
                    _message = StatusCopy.Calibrating + "（Pico 采集；急停/阻抗可随时切换）";
                else
                    _message = noiseOnly ? ("噪声对照：" + StatusCopy.ZeroKvNoiseOnly) : "联锁预检…";
            }
            RaiseStatus();

            AcquisitionExitReason exit = AcquisitionExitReason.None;
            _picoCommWarning = string.Empty;
            try
            {
                // —— 安全门 ——
                string refuse = EvaluateNonZeroGate(targetV);
                if (refuse != null)
                {
                    ForceSourceOffSafe();
                    AssertAoZero("安全门拒绝后");
                    result.Success = false;
                    result.ExitReason = _backend.ReadEStopPressed() ? AcquisitionExitReason.EmergencyStop : AcquisitionExitReason.HardwareFault;
                    result.ErrorMessage = refuse;
                    _message = refuse;
                    return Finish(result);
                }

                if (noiseOnly)
                {
                    // 0 V/0 kV：跳过 HVON 与 HVUP，直接噪声/采集路径（不开高压、不升压）
                    _stepHvonMs = 0;
                    _stepHvupMs = 0;
                    _message = "0 V：跳过 HVON/HVUP → 直接噪声采集";
                    RaiseStatus();
                    ForceSourceOffSafe();
                    AssertAoZero("0 kV 噪声对照");
                    string capErr = CaptureAfterSourceOff(result, request, cancellation);
                    result.Success = capErr == null;
                    result.ExitReason = capErr == null ? AcquisitionExitReason.TargetReached : AcquisitionExitReason.CaptureFault;
                    result.ErrorMessage = capErr ?? StatusCopy.ZeroKvNoiseOnly;
                    if (capErr == null)
                        _message = "0 V 已跳过 HVON/HVUP；" + StatusCopy.ZeroKvNoiseOnly
                            + "；Pico=" + result.PicoRawAdc.Length
                            + " AI4=" + result.UsbDividerRawVolts.Length + "；" + result.DischargeStatusMessage;
                    else
                        _message = capErr;
                    return Finish(result);
                }

                // —— 非零加压：R23 启动顺序（HV ON 前不读 AI）——
                if (_latched)
                {
                    string clr;
                    if (!TryClearEmergencyLatchIfHardwareSafe(out clr))
                    {
                        result.Success = false;
                        result.ExitReason = AcquisitionExitReason.EmergencyStop;
                        result.ErrorMessage = "急停已锁存，禁止加压。" + (clr ?? "");
                        ForceSourceOffSafe();
                        return Finish(result);
                    }
                }

                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (_estopMonitor != null) _estopMonitor.EnterActiveMode();
                }
                catch { }
                _stepHvonMs = 0; _stepHvupMs = 0; _stepHvoffMs = 0; _stepCaptureMs = 0; _stepDelayLeakageA = double.NaN;
                // 自上次开关导通指令起算满 2s 再进本轮 HVON（非另加无关档间冷却）
                WaitForNextPressSlotAfterConduction(cancellation);
                // 停后再压：HV OFF 下先等 AI4 残压落到启动背景限，避免 HVON 后单拍误报「启动背景超限/OverVoltage」
                string residualWaitErr = WaitResidualBelowBackgroundLimit(result, cancellation);
                if (residualWaitErr != null)
                {
                    ForceSourceOffSafe();
                    result.Success = false;
                    if (result.ExitReason == AcquisitionExitReason.None)
                        result.ExitReason = AcquisitionExitReason.OverVoltage;
                    result.ErrorMessage = residualWaitErr;
                    _message = residualWaitErr;
                    return Finish(result);
                }
                var swHvon = Stopwatch.StartNew();
                string startErr = PrepareNonZeroStartup(result, cancellation);
                _stepHvonMs = (int)swHvon.ElapsedMilliseconds;
                RaiseStatus();
                if (startErr != null)
                {
                    ForceSourceOffSafe();
                    result.Success = false;
                    if (result.ExitReason == AcquisitionExitReason.None)
                        result.ExitReason = AcquisitionExitReason.HardwareFault;
                    result.ErrorMessage = startErr;
                    _message = startErr;
                    // 任一环节急停（含 HVON 段）：跳 HVOFF 采集路径，勿只停在 HVON
                    bool estopJump = result.ExitReason == AcquisitionExitReason.EmergencyStop
                        || _pressAbortCapturePending
                        || startErr.IndexOf("急停", StringComparison.Ordinal) >= 0;
                    if (estopJump && !IsWholeDeviceOffline())
                    {
                        _message = "急停→跳 HVOFF：" + startErr;
                        RaiseStatus();
                        string hvoffErr = CaptureAfterHvupStop(result, request, cancellation, assertAoZeroStrict: false);
                        if (!string.IsNullOrEmpty(hvoffErr))
                            result.ErrorMessage = (result.ErrorMessage ?? "") + " | " + hvoffErr;
                        _pressAbortCapturePending = false;
                    }
                    return Finish(result);
                }
                _message = "加压中：HVON=" + _stepHvonMs + "ms 已接通；升压中（分压到位；P0.4 独立监视）";
                RaiseStatus();

                var swHvup = Stopwatch.StartNew();
                exit = RampUntilExit(result, targetV, cancellation);
                _stepHvupMs = (int)swHvup.ElapsedMilliseconds;
                RaiseStatus();
                // 达压或加压中急停/过流/超压等异常：一律先 HV OFF（灯同步），再 Pico 同步采集（整机掉线除外）
                bool doCapture = exit == AcquisitionExitReason.TargetReached
                    || ShouldSyncPicoCaptureOnAbort(exit)
                    || _pressAbortCapturePending;
                if (doCapture)
                {
                    // HVUP 停后：CaptureAfterHvupStop 内并行 Pico设置+AI切振荡，500ms 内 HVOFF+AO清零，再开关导通触发
                    if (IsWholeDeviceOffline())
                    {
                        ForceSourceOffSafe();
                        result.ErrorMessage = (result.ErrorMessage ?? DescribeExit(exit))
                            + "；" + StatusCopy.PressAbortDeviceOffline;
                        _message = StatusCopy.PressAbortDeviceOffline;
                    }
                    else
                    {
                        if (exit != AcquisitionExitReason.TargetReached)
                            _message = StatusCopy.PressAbortHvOffPicoCapture;
                        // OperatorStop 时外层 token 已取消：须用 None 完成关源后同步采集，否则立刻 OCE→HoldGround→再压失败
                        var capToken = exit == AcquisitionExitReason.OperatorStop
                            ? CancellationToken.None
                            : cancellation;
                        string capErr = CaptureAfterHvupStop(result, request, capToken,
                            exit == AcquisitionExitReason.TargetReached);
                        RaiseStatus();
                        if (capErr != null)
                        {
                            bool hardPressed = false;
                            try { hardPressed = _backend.ReadEStopPressed(); } catch { hardPressed = false; }
                            bool nonEstopAbort = capErr.IndexOf("非急停中止", StringComparison.Ordinal) >= 0
                                || capErr.IndexOf("缓存失效", StringComparison.Ordinal) >= 0;
                            if (!hardPressed)
                            {
                                string clr;
                                TryClearEmergencyLatchIfHardwareSafe(out clr);
                            }
                            if (hardPressed)
                            {
                                // 硬件确认按下才可标 EmergencyStop
                                exit = AcquisitionExitReason.EmergencyStop;
                            }
                            else if (exit == AcquisitionExitReason.OperatorStop)
                            {
                                // UI 停止：保持 OperatorStop，不升 CaptureFault/不锁存
                            }
                            else if (exit == AcquisitionExitReason.TargetReached || nonEstopAbort
                                || exit == AcquisitionExitReason.EmergencyStop)
                            {
                                // 假急停/缓存失效：达压或原急停出口一律降为 CaptureFault，不停批当急停
                                exit = AcquisitionExitReason.CaptureFault;
                            }
                            result.ErrorMessage = (result.ErrorMessage ?? "")
                                + (string.IsNullOrEmpty(result.ErrorMessage) ? "" : " | ")
                                + capErr;
                        }
                        else if (exit != AcquisitionExitReason.TargetReached)
                        {
                            result.ErrorMessage = (result.ErrorMessage ?? DescribeExit(exit))
                                + "；" + StatusCopy.PressAbortHvOffPicoCapture
                                + (string.IsNullOrEmpty(_picoCommWarning) ? "" : "；" + _picoCommWarning);
                        }
                    }
                    _pressAbortCapturePending = false;
                }
                result.ExitReason = exit;
                result.Success = exit == AcquisitionExitReason.TargetReached;
                if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
                    result.ErrorMessage = DescribeExit(exit);
                if (exit == AcquisitionExitReason.EmergencyStop
                    && string.IsNullOrWhiteSpace(result.ErrorMessage))
                    result.ErrorMessage = "急停：已关源并锁存（缺详细文案，请查 P0.4 硬读/锁存边沿）";
                if (!string.IsNullOrEmpty(_picoCommWarning) && result.Success)
                    result.ErrorMessage = string.IsNullOrEmpty(result.ErrorMessage)
                        ? _picoCommWarning
                        : result.ErrorMessage + "；" + _picoCommWarning;
                _message = result.Success
                    ? DescribeExit(exit)
                    : (result.ErrorMessage ?? DescribeExit(exit));
                if (exit == AcquisitionExitReason.EmergencyStop)
                {
                    string em = result.ErrorMessage ?? "";
                    if (em.IndexOf("非急停中止", StringComparison.Ordinal) >= 0
                        || em.IndexOf("缓存失效", StringComparison.Ordinal) >= 0)
                    {
                        bool hardPressed = false;
                        try { hardPressed = _backend.ReadEStopPressed(); } catch { hardPressed = false; }
                        if (!hardPressed)
                        {
                            exit = AcquisitionExitReason.CaptureFault;
                            result.ExitReason = exit;
                            string clr;
                            TryClearEmergencyLatchIfHardwareSafe(out clr);
                        }
                        else
                            _latched = true;
                    }
                    else
                        _latched = true;
                }
                if (!result.Success && exit != AcquisitionExitReason.None && exit != AcquisitionExitReason.OperatorStop
                    && exit != AcquisitionExitReason.TargetReached)
                    PersistSessionFault(exit, result.ErrorMessage ?? _lastError ?? _message, "RampOrCapture");
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ExitReason = AcquisitionExitReason.OperatorStop;
                result.ErrorMessage = "操作取消";
            }
            catch (Exception ex)
            {
                result.Success = false;
                bool estopish = false;
                try { estopish = _backend.ReadEStopPressed() || _latched; } catch { }
                result.ExitReason = estopish ? AcquisitionExitReason.EmergencyStop : AcquisitionExitReason.HardwareFault;
                result.ErrorMessage = ex.Message;
                _lastError = ex.Message;
                if (estopish) _latched = true;
                PersistSessionFault(result.ExitReason, result.ErrorMessage, "Exception");
                // 异常路径未走到达压采集时：仍尝试 HV OFF + Pico 同步采集
                if (!IsWholeDeviceOffline() && (result.PicoRawAdc == null || result.PicoRawAdc.Length == 0))
                {
                    try
                    {
                        ForceSourceOffSafe();
                        string capErr = CaptureAfterSourceOff(result, request, cancellation);
                        result.ErrorMessage = (result.ErrorMessage ?? "")
                            + "；" + StatusCopy.PressAbortHvOffPicoCapture
                            + (capErr != null ? (" | " + capErr) : "");
                    }
                    catch (Exception cx)
                    {
                        result.ErrorMessage = (result.ErrorMessage ?? "") + " | 异常采集失败：" + cx.Message;
                    }
                }
            }
            finally
            {
                try
                {
                    ForceSourceOffSafe();
                    AssertAoZero("循环 finally");
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ExitReason = AcquisitionExitReason.HardwareFault;
                    result.ErrorMessage = (result.ErrorMessage ?? "") + " | 关源失败：" + ex.Message;
                    _lastError = ex.Message;
                }
                // 保留连接灯高
                try { _backend.WriteDigitalOutputs(false, false, true, false); } catch { }
                try
                {
                    if (_estopMonitor != null) _estopMonitor.EnterIdleMode();
                }
                catch { }
                try { TryRebuildIdleDiIfRequired("cycle-finally"); }
                catch { /* 已锁存/打日志 */ }
                lock (_sync) { _cycleRunning = false; _calibrating = false; }
            }

            return Finish(result);
        }

        AcquisitionExitReason RampUntilExit(AcquisitionCycleResult result, double targetV, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            double ao = 0;
            var dividerTrace = new List<double>(256);

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                {
                    bool pressed = false;
                    try { pressed = _backend.ReadEStopPressed(); } catch { pressed = false; }
                    if (pressed)
                    {
                        result.ErrorMessage = "升压中急停按下（P0.4 硬读确认）";
                        return AcquisitionExitReason.EmergencyStop;
                    }
                    if (_latched)
                    {
                        string clr;
                        TryClearSoftLatchIfP04Released(out clr);
                        if (_latched)
                        {
                            // 仍锁存但硬读未确认按下：不按急停停升压（过流过压另判）
                            try { pressed = _backend.ReadEStopPressed(); } catch { pressed = false; }
                            if (pressed)
                            {
                                result.ErrorMessage = "升压中急停按下（锁存后硬读确认）";
                                return AcquisitionExitReason.EmergencyStop;
                            }
                        }
                    }
                }
                if (Interlocked.CompareExchange(ref _operatorStop, 0, 0) != 0)
                    return AcquisitionExitReason.OperatorStop;

                LiveFeedbackSample fb;
                try
                {
                    fb = _backend.ReadFeedback();
                }
                catch (InvalidOperationException ex)
                {
                    // AO 后积压/新鲜窗失败 → 故障关断（finally HV OFF）
                    string msg = ex.Message ?? string.Empty;
                    if (msg.IndexOf("积压", StringComparison.Ordinal) >= 0
                        || msg.IndexOf("新鲜", StringComparison.Ordinal) >= 0
                        || msg.IndexOf("追读", StringComparison.Ordinal) >= 0
                        || msg.IndexOf("反馈", StringComparison.Ordinal) >= 0)
                    {
                        result.ErrorMessage = "升压反馈窗故障：" + msg;
                        return AcquisitionExitReason.HardwareFault;
                    }
                    throw;
                }
                double actual = Math.Abs(ScaleDivider(fb));
                double current = Math.Abs(ScaleCurrent(fb));
                _lastDividerEngV = actual;
                _lastCurrentA = current;
                try { _lastSourceEngV = ScaleSourceVoltage(fb); } catch { } // 仅显示
                // 新鲜窗原始峰值优先参与过流/过压判定
                if (fb.PeakDividerAbs > 0)
                    actual = Math.Max(actual, Math.Abs(ScaleDividerRawPeak(fb)));
                if (fb.PeakCurrentAbs > 0)
                    current = Math.Max(current, Math.Abs(ScaleCurrentPeak(fb)));
                result.PeakActualVoltageV = Math.Max(result.PeakActualVoltageV, actual);
                result.PeakCurrentA = Math.Max(result.PeakCurrentA, current);
                dividerTrace.Add(fb.DividerRawV);

                if (current >= _hw.HardOverCurrentA)
                    return AcquisitionExitReason.HardOverCurrent;
                // 异常过压：原始峰值（本拍 actual + 已跟踪 Peak）；比例默认+20%，可与绝对上限取严
                double ovFrac = LiveHardwareOptions.ClampOverVoltageTripFraction(_hw.OverVoltageTripFraction);
                double ovByFrac = targetV * (1.0 + ovFrac);
                double ovLimit = ovByFrac;
                if (_hw.AbsoluteOverVoltageLimitV > 0)
                    ovLimit = Math.Min(ovByFrac, _hw.AbsoluteOverVoltageLimitV);
                double peakRaw = Math.Max(result.PeakActualVoltageV, actual);
                if (peakRaw > ovLimit)
                    return AcquisitionExitReason.OverVoltage;
                // 达压有效窗口 ±5%：只用分压器测量判断（直流源 V/I 仅显示，不参与到位）
                double reachTol = LiveHardwareOptions.ClampTargetReachedToleranceFraction(_hw.TargetReachedToleranceFraction);
                double lo = targetV * (1.0 - reachTol);
                double hi = targetV * (1.0 + reachTol);
                // 分压瞬时或本档分压峰值任一方落入达压窗即成功（步进擦边时瞬时可能漏窗）
                if ((actual >= lo && actual <= hi)
                    || (result.PeakActualVoltageV >= lo && result.PeakActualVoltageV <= hi))
                {
                    result.UsbDividerRawVolts = dividerTrace.ToArray();
                    return AcquisitionExitReason.TargetReached;
                }
                // 窗口上沿～异常过压之间：仍停增（AO 包络另限），继续监测至过压或回窗
                if (actual > hi)
                {
                    // 不继续加 AO；落入下方 next<=ao 分支
                }
                if (clock.Elapsed.TotalSeconds >= _hw.MaximumRampSeconds)
                    return AcquisitionExitReason.RampTimeout;
                if (current >= _hw.CurrentLimitA)
                    return AcquisitionExitReason.CurrentLimitStall;

                // AO 命令 +5% 包络不随异常过压阈值放宽
                double maxAo = Math.Min(_hw.AoMaximumVolts, targetV / Math.Max(_hw.VoltageGainVPerAoVolt, 1e-9) * 1.05);
                double reachTolAo = LiveHardwareOptions.ClampTargetReachedToleranceFraction(_hw.TargetReachedToleranceFraction);
                bool aboveReachWindow = actual > targetV * (1.0 + reachTolAo);
                double next = aboveReachWindow ? ao : Math.Min(ao + _hw.RampStepAoVolts, maxAo);
                if (next > ao + 1e-12)
                {
                    try
                    {
                        _backend.WriteAoVolts(next);
                        ao = next;
                    }
                    catch (Exception)
                    {
                        bool pressed = false;
                        try { pressed = _backend.ReadEStopPressed(); } catch { pressed = false; }
                        if (pressed)
                        {
                            result.ErrorMessage = "写 AO 时急停按下（P0.4 硬读确认）";
                            return AcquisitionExitReason.EmergencyStop;
                        }
                        if (_latched)
                        {
                            string clr;
                            TryClearSoftLatchIfP04Released(out clr);
                        }
                        throw;
                    }
                }
                else
                {
                    // AO 已顶满：若峰值已在达压窗内则成功（避免步进擦边漏窗）
                    if (result.PeakActualVoltageV >= lo && result.PeakActualVoltageV <= hi)
                    {
                        result.UsbDividerRawVolts = dividerTrace.ToArray();
                        return AcquisitionExitReason.TargetReached;
                    }
                    if (clock.Elapsed.TotalSeconds > 2)
                        return AcquisitionExitReason.RampTimeout;
                }

                Thread.Sleep(Math.Max(1, _hw.RampStepDelayMilliseconds));
            }
        }


        /// <summary>
        /// 布防 Pico 前等待采后冷却（MarkOpened / MarkReleased）。剩余 0 且无 releasePending 则立即返回。
        /// FakeLive 不打 Mark* 时为零等待；实机路径与 HvPressTest 档间冷却对齐。
        /// </summary>
        void WaitForPicoCaptureReady(CancellationToken cancellation)
        {
            int rem = PicoCaptureCooldown.RemainingMilliseconds;
            bool pending = PicoCaptureCooldown.ReleasePending;
            if (rem <= 0 && !pending) return;
            _message = pending
                ? "Pico 上一段释放确认中，等待冷却…"
                : ("Pico 采后冷却剩余 " + rem + " ms…");
            RaiseStatus();
            PicoCaptureCooldown.Wait(
                cancellation,
                remaining =>
                {
                    _message = remaining == int.MaxValue
                        ? "Pico 上一段释放确认中，等待冷却…"
                        : ("Pico 采后冷却剩余 " + remaining + " ms…");
                    TryPollMonitorFeedbackIntoCaches(raiseStatus: false);
                    RaiseStatus();
                },
                null);
        }

        /// <summary>
        /// HVUP 停后采集时序（用户权威）：
        /// Pico 在「连接」时 Open（禁止 HV OFF 才 OpenUnit，否则拖长平台）。
        /// 达压 → 立刻 HVOFF+AO=0（关源前禁止 Configure/ArmFinite/RunBlock/等冷却；预算≤500ms 仅作超时告警）
        /// → 关源后立即全量布防（ArmBlock 内配触发；skipCooldownGate）→ 导通触发。
        /// 关源后 AI4 finite 占用期间抑制分压监控假平台。Pico 冷却只在采集完成后。
        /// StepHvoffElapsedMs=真正 HVOFF+AO 清零；触发起→ StepCaptureElapsedMs。
        /// </summary>
        const int PreHvoffTriggerConfigBudgetMilliseconds = 500;
        const int PostHvupPreTriggerDelayMilliseconds = 50;
        /// <summary>兼容旧名：预臂设置窗预算= HVOFF 前触发配置预算。</summary>
        const int PostHvupSetupWindowMilliseconds = PreHvoffTriggerConfigBudgetMilliseconds;
        /// <summary>开关导通（P0.7）保持高电平时长。</summary>
        const int SwitchConductionHoldMilliseconds = 1000;
        /// <summary>自导通指令发出起算，满此时长才允许进入下一次加压（导通1s + 再1s）。</summary>
        const int NextPressAfterConductionStartMilliseconds = 2000;

        string CaptureAfterSourceOff(AcquisitionCycleResult result, AcquisitionCycleRequest request, CancellationToken cancellation)
        {
            return CaptureAfterHvupStop(result, request, cancellation, assertAoZeroStrict: true);
        }

        string CaptureAfterHvupStop(AcquisitionCycleResult result, AcquisitionCycleRequest request, CancellationToken cancellation, bool assertAoZeroStrict)
        {
            // 优先 request → 当前 _hw（ApplyUiCaptureSettings 写入）→ 勿用 Open 时陈旧 _options（会导致设定±100mV 实际仍 ±2V）
            int range = request != null && request.SuggestedPicoRangeIndex >= 0
                ? request.SuggestedPicoRangeIndex
                : (_hw.PicoRangeIndex >= 0 ? _hw.PicoRangeIndex : 6);
            double targetV = request != null ? request.TargetVoltageV : 0;
            bool zeroTarget = !(targetV > 1e-6) || (result != null && result.IsBackgroundCapture);
            bool autoTriggerOnly = zeroTarget; // 0V→自动触发；非0→数字 EXT
            // 权威：达压后立刻 HVOFF（禁止在关源前 ArmFinite/RunBlock/等冷却/Open/干等）。
            // 触发配置放到关源后 ArmBlockCapture（内含 SetSimpleTrigger）；关源前不再 ConfigureBlockTriggerOnly，
            // 避免 Pico 锁/SDK 拖长达压平台。假平台：finite 占用 AI4 时抑制分压缓存高压。
            _message = "达压后：立刻 HVOFF+AO=0（关源前不配触发/不布防；Pico 已在连接时启动）";
            RaiseStatus();
            try
            {
                _stepHvoffMs = 0;
                _stepCaptureMs = 0;
                _dividerMonitorSuspendedAfterHvOff = false;

                // (1) 立刻 HVOFF+AO 清零 — 关源前零等待
                var preHvoffClock = Stopwatch.StartNew();
                _message = "HVOFF+AO清零中…";
                RaiseStatus();
                var swHvoff = Stopwatch.StartNew();
                ForceSourceOffSafe();
                try
                {
                    AssertAoZero(assertAoZeroStrict ? "HVUP停后立即关源" : "异常路径关源");
                }
                catch (Exception ax)
                {
                    if (assertAoZeroStrict)
                        return "SAFETY：" + ax.Message;
                    result.ErrorMessage = (result.ErrorMessage ?? "") + " | " + ax.Message;
                }
                _stepHvoffMs = (int)swHvoff.ElapsedMilliseconds;
                int preElapsed = (int)preHvoffClock.ElapsedMilliseconds;
                if (preElapsed > PreHvoffTriggerConfigBudgetMilliseconds)
                {
                    _message = "HVOFF前耗时 " + preElapsed + "ms（预算"
                        + PreHvoffTriggerConfigBudgetMilliseconds + "）— 已关源";
                    RaiseStatus();
                }
                // 关源后趁监测 AI 仍在：刷一拍；随即清分压+源压缓存，禁止假平台（finite 将占用 AI4）
                TryPollMonitorFeedbackIntoCaches(raiseStatus: true);
                _lastDividerEngV = 0;
                _lastSourceEngV = 0;
                _dividerMonitorSuspendedAfterHvOff = true;
                RaiseStatus();

                // (2) 关源后立即全量布防（AI4 finite + Pico RunBlock；触发在 ArmBlock 内配置）
                // skipCooldownGate=绝不干等冷却。Pico 冷却只在采集完成后。
                _message = "关源后布防：AI4+Pico（HVOFF=" + _stepHvoffMs
                    + "ms pre=" + preElapsed + "ms；"
                    + (autoTriggerOnly ? "0V→自动" : "非0→EXT") + "；冷却不等待）…";
                RaiseStatus();
                _backend.ArmFiniteDividerCapture();
                _backend.ArmBlockCapture(range, null, autoTriggerOnly, skipCooldownGate: true);
                _message = "预臂：再延时 " + PostHvupPreTriggerDelayMilliseconds + " ms 后开关导通触发";
                RaiseStatus();
                SleepCancellable(PostHvupPreTriggerDelayMilliseconds, cancellation, "预臂触发前延时");

                // (4) 开关导通 + ART/Pico 触发 — 自此起计「采集触发」
                if (_backend.HvOn || _backend.HvIndicatorOn || Math.Abs(_backend.LastAoVolts) > 1e-6)
                {
                    try { _backend.AbortFiniteDividerCapture(); } catch { }
                    try { _backend.AbortBlockCapture(); } catch { }
                    return "SAFETY：触发前源未关闭，拒绝开关导通触发。";
                }
                _message = "采集触发：开关导通上升沿即采（等待触发超时≤"
                    + Math.Max(_hw.ArtCaptureTimeoutMilliseconds, _hw.PicoCaptureTimeoutMilliseconds)
                    + "ms；导通保持 " + SwitchConductionHoldMilliseconds + "ms；Range " + range + "）";
                RaiseStatus();
                var swCap = Stopwatch.StartNew();
                // 边沿即采：先拉高 P0.7 → 立刻 Read（触发到即返回，勿先睡满导通再读导致空等3s）
                var condClock = Stopwatch.StartNew();
                BeginSwitchConduction();
                double[] divider;
                PicoBlockCaptureResult cap;
                try
                {
                    divider = _backend.ReadFiniteDividerCapture(_hw.ArtCaptureTimeoutMilliseconds);
                    if (divider == null || divider.Length == 0)
                        return "AI4 分压器有限采集返回空数据。";
                    result.UsbDividerRawVolts = divider;
                    result.UsbSampleRateHz = _hw.ArtFiniteSampleRateHz;

                    cap = _backend.ReadBlockCapture(_hw.PicoCaptureTimeoutMilliseconds, cancellation);
                    if (cap == null || cap.RawAdc == null || cap.RawAdc.Length == 0)
                        return "Pico 块采集返回空数据。";
                }
                finally
                {
                    EndSwitchConduction(condClock);
                    _stepCaptureMs = (int)swCap.ElapsedMilliseconds;
                    RaiseStatus();
                }
                result.PicoRawAdc = cap.RawAdc;
                result.PicoSampleRateHz = cap.SampleRateHz;
                result.PicoResolutionBits = _hw.PicoResolutionBits;
                result.PicoSampleIntervalNanoseconds = cap.SampleIntervalNanoseconds;
                result.PicoFullScaleVolts = cap.FullScaleVolts;
                result.PicoMaximumAdc = cap.MaximumAdc;
                result.PicoRangeIndexUsed = cap.RangeIndex;
                if (!string.IsNullOrEmpty(cap.Serial)) result.PicoSerial = cap.Serial;
                double dividerRatio = _hw.DividerRatioVPerV > 0 ? _hw.DividerRatioVPerV : 11000.0;
                // OperatorAbort/取消后：跳过量程核算，Suggested 保持已用量程（量程不动）
                bool skipRangeAdvise = Interlocked.CompareExchange(ref _operatorStop, 0, 0) != 0;
                if (skipRangeAdvise)
                {
                    result.Task6RangeSuitable = true;
                    result.SuggestedPicoRangeIndex = cap.RangeIndex;
                    result.PicoOverrangeOrClipped = cap.HardwareOverflow;
                    result.Task6RangeMessage = "操作员停止：跳过量程核算";
                }
                else
                {
                    var advice = Task6RangeAdvisor.AdviseWithEvaluator(
                        _firstZero,
                        cap.RangeIndex,
                        cap.RawAdc,
                        cap.MaximumAdc,
                        cap.HardwareOverflow,
                        cap.FullScaleVolts,
                        cap.SampleRateHz,
                        result.UsbDividerRawVolts,
                        result.UsbSampleRateHz,
                        result.IsBackgroundCapture,
                        result.PeakActualVoltageV,
                        dividerRatio);
                    Task6RangeAdvisor.ApplyToCycleResult(result, advice);
                    if (!string.IsNullOrEmpty(advice.Message))
                        _message = advice.Message;
                }

                VerifyDischarge(result);
                _dividerMonitorSuspendedAfterHvOff = false;
                return null;
            }
            catch (OperationCanceledException ex)
            {
                try { _backend.AbortFiniteDividerCapture(); } catch { }
                try { _backend.AbortBlockCapture(); } catch { }
                _dividerMonitorSuspendedAfterHvOff = false;
                // 仅硬件按下才锁存；缓存过期/未知不得假急停锁存
                bool hardPressed = false;
                try { hardPressed = _backend.ReadEStopPressed(); } catch { hardPressed = false; }
                bool operatorCancel = Interlocked.CompareExchange(ref _operatorStop, 0, 0) != 0
                    || cancellation.IsCancellationRequested;
                if (hardPressed) _latched = true;
                else
                {
                    string clr;
                    TryClearEmergencyLatchIfHardwareSafe(out clr);
                }
                result.DischargeVerified = false;
                result.DischargeStatusMessage = StatusCopy.DischargeNotVerified;
                _lastDischargeVerified = false;
                if (hardPressed || !operatorCancel)
                {
                    SetHoldGround(true);
                    string kind = hardPressed ? "急停按下" : "非急停中止（缓存失效/取消，P0.4 未按下）";
                    return "块采集中止（" + kind + "）：" + ex.Message + "；" + StatusCopy.DischargeNotVerified + "；" + StatusCopy.HoldGroundActive;
                }
                // UI 停止取消：关源即可，不 HoldGround 永久挡再压
                try { SetHoldGroundCore(false, allowReleaseWithoutConfirm: true); } catch { }
                return "块采集中止（操作员停止）：" + ex.Message + "；已关源未锁存";
            }
            catch (Exception ex)
            {
                try { _backend.AbortFiniteDividerCapture(); } catch { }
                try { _backend.AbortBlockCapture(); } catch { }
                _dividerMonitorSuspendedAfterHvOff = false;

                // 仅 Pico 掉线：加压控制侧已关源；提示通讯异常并内部重开 Pico 后再试一次采集
                bool picoDown = false;
                try { picoDown = !_backend.PicoOpen; } catch { picoDown = true; }
                bool artOk = !IsWholeDeviceOffline();
                if (picoDown && artOk)
                {
                    string recoverMsg = TryRecoverPicoComm();
                    try
                    {
                        // 恢复路径：先关源（灯只计此段），再 Pico 冷却，再预臂
                        _message = "HVOFF恢复：关源…";
                        RaiseStatus();
                        var swHvoff2 = Stopwatch.StartNew();
                        ForceSourceOffSafe();
                        _stepHvoffMs = (int)swHvoff2.ElapsedMilliseconds;
                        RaiseStatus();
                        // 恢复路径：关源后立即布防，绝不 WaitForPicoCaptureReady
                        _backend.ArmFiniteDividerCapture();
                        _backend.ArmBlockCapture(range, null, autoTriggerOnly: false, skipCooldownGate: true);
                        SleepCancellable(PostHvupPreTriggerDelayMilliseconds, cancellation, "预臂恢复触发前延时");
                        var swCap2 = Stopwatch.StartNew();
                        var cond2 = Stopwatch.StartNew();
                        BeginSwitchConduction();
                        double[] divider2 = null;
                        PicoBlockCaptureResult cap2 = null;
                        try
                        {
                            divider2 = _backend.ReadFiniteDividerCapture(_hw.ArtCaptureTimeoutMilliseconds);
                            if (divider2 != null && divider2.Length > 0)
                            {
                                result.UsbDividerRawVolts = divider2;
                                result.UsbSampleRateHz = _hw.ArtFiniteSampleRateHz;
                            }
                            cap2 = _backend.ReadBlockCapture(_hw.PicoCaptureTimeoutMilliseconds, cancellation);
                        }
                        finally
                        {
                            EndSwitchConduction(cond2);
                            _stepCaptureMs = (int)swCap2.ElapsedMilliseconds;
                            RaiseStatus();
                        }
                        if (cap2 != null && cap2.RawAdc != null && cap2.RawAdc.Length > 0)
                        {
                            result.PicoRawAdc = cap2.RawAdc;
                            result.PicoSampleRateHz = cap2.SampleRateHz;
                            result.PicoResolutionBits = _hw.PicoResolutionBits;
                            result.PicoSampleIntervalNanoseconds = cap2.SampleIntervalNanoseconds;
                            result.PicoFullScaleVolts = cap2.FullScaleVolts;
                            result.PicoMaximumAdc = cap2.MaximumAdc;
                            result.PicoRangeIndexUsed = cap2.RangeIndex;
                            if (!string.IsNullOrEmpty(cap2.Serial)) result.PicoSerial = cap2.Serial;
                            VerifyDischarge(result);
                            // 警告已在 _picoCommWarning；返回 null 表示样本可用，不把达压改成 CaptureFault
                            return null;
                        }
                    }
                    catch (Exception ex2)
                    {
                        try { _backend.AbortFiniteDividerCapture(); } catch { }
                        try { _backend.AbortBlockCapture(); } catch { }
                        result.DischargeVerified = false;
                        result.DischargeStatusMessage = StatusCopy.DischargeNotVerified;
                        _lastDischargeVerified = false;
                        SetHoldGround(true);
                        return "对齐采集失败：" + ex.Message
                            + "；Pico 恢复后仍失败：" + ex2.Message
                            + "；" + (recoverMsg ?? StatusCopy.PicoCommAnomalyRetry)
                            + "；" + StatusCopy.DischargeNotVerified;
                    }
                }

                result.DischargeVerified = false;
                result.DischargeStatusMessage = StatusCopy.DischargeNotVerified;
                _lastDischargeVerified = false;
                SetHoldGround(true);
                return "对齐采集失败：" + ex.Message + "；" + StatusCopy.DischargeNotVerified + "；" + StatusCopy.HoldGroundActive;
            }
            finally
            {
                try { _backend.WriteDigitalOutputs(false, false, true, _holdGroundActive); } catch { }
            }
        }

        /// <summary>
        /// 残压/放电确认骨架：关源后轮询 AI2/AI4（ReadFeedback）。
        /// 超限 → DischargeVerified=false，文案 StatusCopy.DischargeResidualHigh / DischargeVerifyTimeout；
        /// 永不把「未放电确认」说成可接近。
        /// </summary>
        void VerifyDischarge(AcquisitionCycleResult result)
        {
            _message = "放电确认（残压）…";
            RaiseStatus();
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < _hw.DischargeVerifyTimeoutMilliseconds)
            {
                if (_backend.ReadEStopPressed())
                {
                    result.DischargeVerified = false;
                    result.DischargeStatusMessage = StatusCopy.DischargeNotVerified + "（急停按下）";
                    _lastDischargeVerified = false;
                    SetHoldGround(true);
                    return;
                }
                LiveFeedbackSample fb = _backend.ReadFeedback();
                double div = Math.Abs(ScaleDivider(fb));
                // AI2/AI4：工程单位来自台架 DividerRatio / SourceVoltageScale（Fake 已是工程值）
                double srcEng = Math.Abs(ScaleSourceVoltage(fb)); // 侧写 _lastSourceEngV
                double cur = Math.Abs(ScaleCurrent(fb));
                // 与源压对称写入分压/电流缓存，避免放电确认时绿线掉零、蓝线仍钉在 HVUP 峰值
                _lastDividerEngV = div;
                _lastCurrentA = cur;
                _message = "放电确认（残压）… AI4=" + div.ToString("0.#") + " V VMON=" + srcEng.ToString("0.#") + " V";
                RaiseStatus();
                if (div <= _hw.ResidualVoltageSafeThresholdV && srcEng <= _hw.ResidualVoltageSafeThresholdV)
                {
                    result.DischargeVerified = true;
                    result.DischargeStatusMessage = StatusCopy.DischargeVerifiedSafe
                        + (LiveAi2SourceScaling.IsCalibrated(_hw) ? "" : "；" + StatusCopy.Ai2SourceScaleNotCalibrated);
                    _lastDischargeVerified = true;
                    SetHoldGroundCore(false, allowReleaseWithoutConfirm: true);
                    _message = result.DischargeStatusMessage;
                    return;
                }
                Thread.Sleep(Math.Max(1, _hw.SafetyPollMilliseconds));
            }
            result.DischargeVerified = false;
            result.DischargeStatusMessage = StatusCopy.DischargeVerifyTimeout
                + (LiveAi2SourceScaling.IsCalibrated(_hw) ? "" : "；" + (_lastAi2ScaleStatus ?? StatusCopy.Ai2SourceScaleNotCalibrated));
            _lastDischargeVerified = false;
            SetHoldGround(true);
            _message = StatusCopy.HoldGroundActive;
            if (string.IsNullOrEmpty(result.ErrorMessage))
                result.ErrorMessage = StatusCopy.DischargeVerifyTimeout + "；" + StatusCopy.HoldGroundActive;
            else
                result.ErrorMessage = result.ErrorMessage + "；" + StatusCopy.DischargeVerifyTimeout + "；" + StatusCopy.HoldGroundActive;
        }

        void SetHoldGround(bool active)
        {
            SetHoldGroundCore(active, allowReleaseWithoutConfirm: !active && !_holdGroundActive);
        }

        /// <summary>
        /// active=true 始终可进入保持。
        /// active=false：若当前已 HoldGroundActive，必须经 ConfirmReleaseGround；残压刚确认通过时用 allowReleaseWithoutConfirm。
        /// </summary>
        void SetHoldGroundCore(bool active, bool allowReleaseWithoutConfirm)
        {
            if (!active && _holdGroundActive && !allowReleaseWithoutConfirm)
            {
                _message = StatusCopy.GroundReleaseNeedsConfirm;
                _lastError = StatusCopy.GroundReleaseNeedsConfirm;
                RaiseStatus();
                return;
            }
            _holdGroundActive = active;
            if (!_open) return;
            try
            {
                _backend.WriteDigitalOutputs(false, false, true, active);
            }
            catch (Exception ex) { _lastError = "HoldGround 写 DO 失败：" + ex.Message; }
            if (active)
                _message = StatusCopy.HoldGroundActive;
            RaiseStatus();
        }

        public bool HoldGroundActive { get { return _holdGroundActive; } }

        public bool ConfirmReleaseGround(bool operatorAcknowledged)
        {
            ThrowIfDisposed();
            if (!_holdGroundActive)
            {
                _message = "当前未处于接地保持，无需解除。";
                return true;
            }
            if (!operatorAcknowledged)
            {
                _message = StatusCopy.GroundReleaseDenied;
                _lastError = StatusCopy.GroundReleaseDenied;
                RaiseStatus();
                return false;
            }
            SetHoldGroundCore(false, allowReleaseWithoutConfirm: true);
            _message = StatusCopy.GroundReleasedAfterConfirm + "；" + StatusCopy.GroundReleaseProcedureHint;
            RaiseStatus();
            return !_holdGroundActive;
        }


        /// <summary>
        /// R23 非零启动：HV ON 前仅 AO=0 + 查 P0.4（不读 AI）；
        /// HVON 后同步建 AI → 延时 100ms → 再背景检查 / HVUP；背景默认 ZeroBackgroundLimitV（500 V，可配）。
        /// </summary>
        string PrepareNonZeroStartup(AcquisitionCycleResult result, CancellationToken cancellation)
        {
            // (1) AO=0 + P0.4（有效高才放行；未知/有效低拒绝）
            try { _backend.WriteAoVolts(0); } catch (Exception ex) { return "启动前 AO 归零失败：" + ex.Message; }
            if (Math.Abs(_backend.LastAoVolts) > 1e-6)
                return "启动前 AO 未归零（" + _backend.LastAoVolts + " V）。";
            LiveEStopSample s = ResolveEStopSampleBeforeHvOn();
            if (s.IsUnknown)
            {
                result.ExitReason = AcquisitionExitReason.HardwareFault;
                return "HV ON 前 P0.4 未知，拒绝加压：" + s.FormatLog();
            }
            if (s.Pressed)
            {
                result.ExitReason = AcquisitionExitReason.EmergencyStop;
                return "HV ON 前急停有效低，拒绝加压：" + s.FormatLog();
            }
            cancellation.ThrowIfCancellationRequested();

            // (2) HV ON（AO 必须仍为 0）；不在此前读 AI / 判残压
            try { SetHvEnergizedSynced(true); }
            catch (Exception ex)
            {
                bool hardPressed = false;
                try { hardPressed = _backend.ReadEStopPressed(); } catch { hardPressed = false; }
                if (hardPressed)
                {
                    result.ExitReason = AcquisitionExitReason.EmergencyStop;
                    _latched = true;
                    return "加压接通时急停按下；已拒绝。" + ex.Message;
                }
                if (_latched)
                {
                    string clr;
                    TryClearSoftLatchIfP04Released(out clr);
                }
                throw;
            }
            if (Math.Abs(_backend.LastAoVolts) > 1e-6)
            {
                ForceSourceOffSafe();
                return "HV ON 后 AO 非零，拒绝启 AI/升压。";
            }

            // (3) HVON 后同步建立 AI 监测任务 → 延时 100ms → 再背景检查 / HVUP
            try { _backend.EnsureMonitorAiTask(); }
            catch (Exception ex)
            {
                ForceSourceOffSafe();
                return "HV ON 后建立 AI 失败：" + ex.Message;
            }
            _message = "HVON 后 AI 已建立，延时 100ms 再进 HVUP";
            RaiseStatus();
            try { SleepCancellable(100, cancellation, "HVON→HVUP"); }
            catch (OperationCanceledException ex)
            {
                ForceSourceOffSafe();
                if (cancellation.IsCancellationRequested)
                {
                    result.ExitReason = AcquisitionExitReason.OperatorStop;
                    return "HVON→HVUP 操作员取消：" + ex.Message;
                }
                bool hardPressed = false;
                try { hardPressed = _backend.ReadEStopPressed(); } catch { hardPressed = false; }
                if (hardPressed)
                {
                    result.ExitReason = AcquisitionExitReason.EmergencyStop;
                    _latched = true;
                    return "HVON→HVUP 延时内急停按下：" + ex.Message;
                }
                // 软取消不应再从 SleepCancellable 抛出；若仍落到此，清虚锁存后继续升压
                string clr;
                TryClearSoftLatchIfP04Released(out clr);
                _message = "HVON→HVUP 延时软中断已忽略，继续 HVUP：" + (clr ?? ex.Message);
                RaiseStatus();
            }

            // (4) AO=0 时读取 AI：延时漏电核对 + 启动背景（到位仍只用分压）
            _message = "HV ON 后 AO=0：延时漏电/背景检查（限 " + _hw.ZeroBackgroundLimitV.ToString("0.#") + " V）";
            RaiseStatus();
            double bgLimit = _hw.ZeroBackgroundLimitV > 0 ? _hw.ZeroBackgroundLimitV : 500.0;
            // 急停/停后再开：AI 首拍常偏置或残压未落；丢弃并短轮询，勿单拍误报 OverVoltage
            double divEng = 0, srcEng = 0, leakA = 0;
            const int bgSettleMs = 400;
            var bgClock = Stopwatch.StartNew();
            int sampleN = 0;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                try { _backend.ReadFeedback(); } catch { }
                LiveFeedbackSample fb = _backend.ReadFeedback();
                divEng = Math.Abs(ScaleDivider(fb));
                srcEng = ScaleSourceVoltage(fb);
                leakA = Math.Abs(ScaleCurrent(fb));
                sampleN++;
                _stepDelayLeakageA = leakA;
                _lastCurrentA = leakA;
                _lastDividerEngV = divEng;
                // 不把启动背景写入 PeakActualVoltageV：残压会污染 Ramp 异常过压（停后再压误触发 OverVoltage）
                result.PeakCurrentA = Math.Max(result.PeakCurrentA, leakA);
                if (leakA >= _hw.HardOverCurrentA)
                {
                    ForceSourceOffSafe();
                    result.ExitReason = AcquisitionExitReason.HardOverCurrent;
                    return "HVON→HVUP 延时漏电过流 I=" + leakA.ToString("G6")
                        + " A ≥ 硬限 " + _hw.HardOverCurrentA.ToString("G6") + " A；已关源。";
                }
                _message = "延时漏电 I=" + (leakA * 1000.0).ToString("0.###") + " mA；背景 AI4="
                    + divEng.ToString("0.#") + " V VMON=" + srcEng.ToString("0.#")
                    + " V（VMON不参与门限）限=" + bgLimit.ToString("0.#") + " V"
                    + " settle=" + bgClock.ElapsedMilliseconds + "ms#" + sampleN;
                RaiseStatus();
                if (divEng <= bgLimit)
                    return null;
                if (bgClock.ElapsedMilliseconds >= bgSettleMs && sampleN >= 3)
                    break;
                try { SleepCancellable(40, cancellation, "HVON后背景settle"); }
                catch (OperationCanceledException)
                {
                    ForceSourceOffSafe();
                    result.ExitReason = AcquisitionExitReason.OperatorStop;
                    return "HVON后背景检查取消。";
                }
            }
            // 启动背景门限：仅 AI4/分压工程 V（与到位一致）。VMON 仅显示；
            // 负 VMON（双极性偏置×scale，如 -1.5kV）不得假过压停批。
            ForceSourceOffSafe();
            result.ExitReason = AcquisitionExitReason.OverVoltage;
            return "启动背景超限：AI4=" + divEng.ToString("G6") + " V（分压门限）VMON="
                + srcEng.ToString("G6") + " V（仅显示）限=" + bgLimit.ToString("G6")
                + " V；已跳 HVOFF，不自动恢复加压。";
        }

        /// <summary>
        /// 停后再压：HV OFF + AO=0 下等 AI4 工程残压 ≤ ZeroBackgroundLimitV。
        /// 避免上一档残留/关源后未落稳 → HVON 后误报「启动背景超限」。
        /// </summary>
        string WaitResidualBelowBackgroundLimit(AcquisitionCycleResult result, CancellationToken cancellation)
        {
            ForceSourceOffSafe();
            double bgLimit = _hw.ZeroBackgroundLimitV > 0 ? _hw.ZeroBackgroundLimitV : 500.0;
            int timeoutMs = _hw.DischargeVerifyTimeoutMilliseconds > 0
                ? _hw.DischargeVerifyTimeoutMilliseconds : 2000;
            // 至少给短 settle；残压已低则立刻返回
            if (timeoutMs < 300) timeoutMs = 300;
            var clock = Stopwatch.StartNew();
            double lastDiv = 0;
            double lastSrc = 0;
            int n = 0;
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                cancellation.ThrowIfCancellationRequested();
                try { _backend.ReadFeedback(); } catch { }
                LiveFeedbackSample fb;
                try { fb = _backend.ReadFeedback(); }
                catch (Exception ex)
                {
                    return "停后再压残压读取失败：" + ex.Message;
                }
                lastDiv = Math.Abs(ScaleDivider(fb));
                try { lastSrc = ScaleSourceVoltage(fb); } catch { lastSrc = 0; }
                n++;
                _lastDividerEngV = lastDiv;
                _message = "停后再压残压等待：AI4=" + lastDiv.ToString("0.#")
                    + " V 限=" + bgLimit.ToString("0.#") + " V（" + clock.ElapsedMilliseconds + "ms#" + n + "）";
                RaiseStatus();
                if (lastDiv <= bgLimit)
                    return null;
                try { SleepCancellable(Math.Max(20, _hw.SafetyPollMilliseconds > 0 ? _hw.SafetyPollMilliseconds : 50),
                    cancellation, "停后再压残压"); }
                catch (OperationCanceledException)
                {
                    result.ExitReason = AcquisitionExitReason.OperatorStop;
                    return "停后再压残压等待取消。";
                }
            }
            // 超时仍高：记 OverVoltage，由调用方 Finish；文案区分「残压未落」与 HVON 后背景
            result.ExitReason = AcquisitionExitReason.OverVoltage;
            return "停后再压残压未落：AI4=" + lastDiv.ToString("G6") + " V（门限 "
                + bgLimit.ToString("G6") + " V）VMON=" + lastSrc.ToString("G6")
                + " V；已等 " + timeoutMs + "ms，未进 HVON。";
        }

        /// <summary>
        /// 加压路径优先读监视/后端缓存（带年龄）；过期=阻断。必要时再短读 DI（走 _diGate）。
        /// </summary>
        bool BlocksEnergizePreferCache(out string reason)
        {
            reason = null;
            if (_estopMonitor != null)
            {
                string monReason;
                if (_estopMonitor.CachedBlocksEnergize(out monReason))
                {
                    // 缓存阻断时再读后端：软件急停解除 / FakeLive 可能比监视缓存新一拍；
                    // 后端明确解除则放行（过期/未知仍以本次实读为准）。
                    try
                    {
                        LiveEStopSample fresh = _backend.ReadEStopSample();
                        NoteEStopSample(fresh);
                        if (!fresh.BlocksEnergize)
                        {
                            reason = null;
                            return false;
                        }
                        reason = fresh.FormatLog();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        reason = monReason ?? ex.Message;
                        return true;
                    }
                }
                return false;
            }
            var art = _backend as ArtPicoLiveBackend;
            if (art != null)
            {
                string cacheReason;
                if (art.CachedEStopBlocksEnergize(out cacheReason))
                {
                    reason = cacheReason;
                    return true;
                }
                return false;
            }
            try
            {
                LiveEStopSample s = _backend.ReadEStopSample();
                if (s.BlocksEnergize)
                {
                    reason = s.FormatLog();
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return true;
            }
        }

        /// <summary>
        /// 非零加压联锁。返回拒绝原因；null = 允许（或目标≤0 走噪声路径由调用方处理）。
        /// </summary>
        public string EvaluateNonZeroGate(double targetVoltageV)
        {
            if (!(targetVoltageV > 0)) return null; // 噪声对照不走非零门
            {
                string estopReason;
                if (BlocksEnergizePreferCache(out estopReason))
                {
                    if (estopReason != null && estopReason.IndexOf("急停按下", StringComparison.Ordinal) >= 0)
                        return "安全门拒绝：急停按下（P0.4 有效低）→ 禁止非零加压，AO 必须为 0。当前「" + StatusCopy.EStopPressed + "」。" + estopReason;
                    return "安全门拒绝：P0.4 未知/过期/按下 → 不冒充低、不以历史高放行；禁止非零加压。 " + (estopReason ?? "");
                }
            }
            if (!_calibrated)
                return "安全门拒绝：未校准 → 禁止非零加压（0 kV 噪声对照除外）。";
            if (_hw.BlocksLiveNonZeroEnergize())
                return StatusCopy.BenchGainNotCalibratedDetail
                    + "（VoltageGain=" + _hw.VoltageGainVPerAoVolt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " DividerRatio=" + _hw.DividerRatioVPerV.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " BenchApplied=" + _hw.BenchCalibrationApplied + "）。";
            // 陈旧软件锁存：仅当 P0.4 仍阻断时才拒。硬件已解除且源已关 → 允许清锁存再加压。
            // 安全依据：真急停/未知仍由上方 BlocksEnergizePreferCache 硬拒；此处只丢掉「Stop/历史边沿」残留。
            if (_latched)
            {
                string clearMsg;
                if (!TryClearEmergencyLatchIfHardwareSafe(out clearMsg))
                    return "安全门拒绝：急停已锁存。" + (string.IsNullOrEmpty(clearMsg) ? "" : (" " + clearMsg));
            }
            return null;
        }

        /// <summary>
        /// 若 P0.4 确认解除（非未知）且 AO/HV 已关，清除软件急停锁存。
        /// 不削弱真联锁：硬件按下/未知时返回 false 并保持 _latched。
        /// </summary>
        public bool TryClearEmergencyLatchIfHardwareSafe(out string detail)
        {
            detail = null;
            if (!_latched)
            {
                detail = "未锁存";
                return true;
            }
            if (!_open)
            {
                detail = "会话未 Open";
                return false;
            }
            try { ForceSourceOffSafe(); } catch (Exception ex) { detail = "关源失败：" + ex.Message; return false; }
            if (_backend.HvOn || _backend.HvIndicatorOn || Math.Abs(_backend.LastAoVolts) > 1e-6)
            {
                detail = "源未关断安全（HV/AO）";
                return false;
            }
            LiveEStopSample sample;
            try { sample = _backend.ReadEStopSample(); }
            catch (Exception ex)
            {
                detail = "P0.4 回读失败：" + ex.Message;
                return false;
            }
            NoteEStopSample(sample);
            if (!sample.ReadOk || sample.Pressed || sample.BlocksEnergize)
            {
                detail = sample.FormatLog();
                return false;
            }
            _latched = false;
            Interlocked.Exchange(ref _operatorStop, 0);
            detail = "已清软件锁存（P0.4 解除且源关断）" + sample.FormatLog();
            _message = detail;
            try { RaiseStatus(); } catch { }
            return true;
        }

        /// <summary>
        /// UI「停止」：关源并取消循环意图，不置硬件急停锁存。
        /// 真 P0.4 边沿仍由监视回调锁存；下一批次可在硬件解除后继续。
        /// </summary>
        public void RequestOperatorAbort()
        {
            Interlocked.Exchange(ref _operatorStop, 1);
            try { _backend.AbortFiniteDividerCapture(); } catch { }
            try { _backend.AbortBlockCapture(); } catch { }
            try { ForceSourceOffSafe(); } catch (Exception ex) { _lastError = ex.Message; }
            // UI 停止：不置 _latched；释放 HoldGround，避免「停止后再压」被接地保持挡死
            try { SetHoldGroundCore(false, allowReleaseWithoutConfirm: true); } catch { }
            try { if (_open) _backend.WriteDigitalOutputs(false, false, true, false); } catch { }
            _message = StatusCopy.OperatorAbortNoLatch;
            RaiseStatus();
        }

        public void EmergencyStop()
        {
            _latched = true;
            Interlocked.Exchange(ref _operatorStop, 1);
            try { _backend.AbortFiniteDividerCapture(); } catch { }
            try { _backend.AbortBlockCapture(); } catch { }
            try { ForceSourceOffSafe(); } catch (Exception ex) { _lastError = ex.Message; }
            try { if (_open) _backend.WriteDigitalOutputs(false, false, true, false); } catch { }
            _message = "EmergencyStop → AO=0 / HV 关 / 锁存";
            PersistSessionFault(AcquisitionExitReason.EmergencyStop, _message, "EmergencyStop");
            RaiseStatus();
        }

        public bool ReadSafetyReady()
        {
            if (!_open) return false;
            try { return !_backend.ReadEStopPressed(); } catch { return false; }
        }

        public void SetEStopPressed(bool pressed)
        {
            var fake = _backend as FakeLiveHardwareBackend;
            if (fake == null)
                throw new NotSupportedException("实机急停只能由 P0.4 回读（急停盒）；测试请用 FakeLive。");
            fake.SetEStopPressed(pressed);
            // 失效 P0.4 监视缓存：否则 GetStatus 顶栏优先缓存会在 ≤300ms 内仍报按下 → DesktopUi/门禁误判
            try
            {
                LiveEmergencyMonitor mon = _estopMonitor;
                if (mon != null) mon.InvalidateCache();
            }
            catch { }
            if (!pressed) { string d; TryClearEmergencyLatchIfHardwareSafe(out d); }
            RaiseStatus();
        }

        public void SetCalibrated(bool calibrated)
        {
            ThrowIfDisposed();
            _calibrated = calibrated;
            if (calibrated) _calibrating = false;
            RaiseStatus();
        }

        public bool ImpedanceBandwidthWide { get { return _impedanceWide; } }

        /// <summary>运行中更新 Pico 分辨率/timebase/量程（连续加压每轮沿用界面选择）。</summary>
        public void ApplyPicoCaptureSettings(int resolutionBits, uint timebase, double targetSampleRateHz, int rangeIndex)
        {
            ThrowIfDisposed();
            if (!_open) throw new InvalidOperationException("实机会话未 Open。");
            _hw.PicoResolutionBits = resolutionBits == 15 ? 15 : 14;
            _hw.ApplyPicoTimebase(timebase > 0 ? timebase : 3);
            // 采样率变更后仍保持 Pico/ART=当前振荡同窗时长（Field）；Short/Smoke 不改点数
            if (_hw.CaptureProfile == PicoCaptureProfile.Field)
                _hw.ApplyPicoOscillationWindow(_hw.OscillationCaptureDurationMs);
            if (rangeIndex >= 0 && rangeIndex < LiveHardwareOptions.Pico5242DFullScaleVolts.Length)
                _hw.PicoRangeIndex = rangeIndex;
            if (_options != null)
                _options.PicoRangeIndex = _hw.PicoRangeIndex;
            _message = "Pico 采集设定已更新：" + _hw.PicoResolutionBits + "bit tb=" + _hw.PicoTimebase
                + " range=" + _hw.PicoRangeIndex
                + " capMs=" + LiveHardwareOptions.ClampOscillationCaptureDurationMs(_hw.OscillationCaptureDurationMs).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + " PicoN=" + _hw.PicoSampleCount
                + " ART_N=" + _hw.ArtFiniteSampleCount
                + (targetSampleRateHz > 0 ? (" ~" + (targetSampleRateHz / 1e6).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "MS/s") : "");
            RaiseStatus();
        }

        public void SetImpedanceBandwidthWide(bool wide)
        {
            ThrowIfDisposed();
            // 校准中亦可调：不检查 _calibrating / _cycleRunning
            _impedanceWide = wide;
            _message = StatusCopy.ImpedanceAdjustableAnytime
                + "；当前宽频=" + (wide ? "开" : "关")
                + (_calibrating ? "（" + StatusCopy.Calibrating + "中已生效）" : "");
            RaiseStatus();
        }

        public void SetConnectionIndicationLamp(bool high)
        {
            if (!_open) return;
            _backend.WriteDigitalOutputs(_backend.HvOn, _backend.HvIndicatorOn, high, false);
        }

        /// <summary>
        /// P0.0 HVON 与 P0.3 加压指示灯同步通断（现网加压态要求）。
        /// 连接灯 P0.5 / 接地触发 P0.7 由其它路径独立控制。
        /// </summary>
        void SetHvEnergizedSynced(bool energized)
        {
            bool lamp = _backend.ConnectionLampHigh;
            _backend.WriteDigitalOutputs(energized, energized, lamp, false);
            if (_backend.HvOn != _backend.HvIndicatorOn)
                throw new InvalidOperationException("SAFETY：P0.0 HVON 与 P0.3 加压灯不同步（HvOn="
                    + _backend.HvOn + " Indicator=" + _backend.HvIndicatorOn + "）。");
        }

        /// <summary>
        /// 延时窗：硬读 P0.4，仅真按下才取消；虚锁存可清则清后继续；
        /// 缓存失效/读失败/锁存未清等软故障不得取消采集。
        /// </summary>
        void SleepCancellable(int milliseconds, CancellationToken cancellation, string phase)
        {
            int remain = milliseconds;
            if (remain <= 0) return;
            const int slice = 20;
            while (remain > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                bool pressed = false;
                try
                {
                    LiveEStopSample sample = _backend.ReadEStopSample();
                    NoteEStopSample(sample);
                    if (sample.ReadOk)
                        pressed = sample.Pressed;
                    // ReadOk=false / 未知：软故障，不按按下处理
                }
                catch
                {
                    pressed = false;
                }
                if (pressed)
                {
                    // 急停：立刻关源并锁存。若已在关源后（HV/AO 已断）则留在 HVOFF 采集路径继续布防/触发，勿再抛取消打断同步采集。
                    try { ForceSourceOffSafe(); } catch { }
                    _pressAbortCapturePending = true;
                    _latched = true;
                    bool sourceAlreadyOff = false;
                    try
                    {
                        sourceAlreadyOff = !_backend.HvOn && !_backend.HvIndicatorOn
                            && Math.Abs(_backend.LastAoVolts) <= 1e-6;
                    }
                    catch { sourceAlreadyOff = false; }
                    if (sourceAlreadyOff)
                    {
                        _message = "急停已关源，继续 HVOFF 采集（" + phase + "）";
                        try { RaiseStatus(); } catch { }
                        // 不抛：全流程急停→跳 HVOFF 后须完成 Pico/ART 同步采集
                    }
                    else
                    {
                        _message = "急停→HVOFF（" + phase + "）";
                        try { RaiseStatus(); } catch { }
                        throw new OperationCanceledException(phase + "延时内急停按下，跳 HVOFF。");
                    }
                }
                if (_latched)
                {
                    string clr;
                    TryClearSoftLatchIfP04Released(out clr);
                    // 仍锁存也不抛：软故障不得取消采集
                }
                int sleep = remain < slice ? remain : slice;
                Thread.Sleep(sleep);
                remain -= sleep;
            }
        }

        /// <summary>
        /// 延时窗虚锁存清理：硬读 P0.4 确认未按下则清 _latched（不 ForceSourceOff，避免 HVON 途中误关源）。
        /// 读失败/未知：返回 true（允许继续），不把软故障当急停。
        /// </summary>
        /// <summary>
        /// HV ON 前解析 P0.4：ReadEStopSample 已含串行忙等待/缓存；
        /// 若仍未知再短重试，避免「串行忙→未知」误拒压。
        /// </summary>
        LiveEStopSample ResolveEStopSampleBeforeHvOn()
        {
            LiveEStopSample s = _backend.ReadEStopSample();
            NoteEStopSample(s);
            if (!s.IsUnknown)
                return s;
            for (int i = 0; i < 5; i++)
            {
                Thread.Sleep(40);
                s = _backend.ReadEStopSample();
                NoteEStopSample(s);
                if (!s.IsUnknown)
                    return s;
            }
            return s;
        }

        bool TryClearSoftLatchIfP04Released(out string detail)
        {
            detail = null;
            if (!_latched)
            {
                detail = "未锁存";
                return true;
            }
            try
            {
                LiveEStopSample sample = _backend.ReadEStopSample();
                NoteEStopSample(sample);
                if (sample.ReadOk && sample.Pressed)
                {
                    detail = sample.FormatLog();
                    return false;
                }
                if (sample.ReadOk && !sample.Pressed)
                {
                    _latched = false;
                    Interlocked.Exchange(ref _operatorStop, 0);
                    detail = "延时窗已清虚锁存（P0.4 未按下）" + sample.FormatLog();
                    _message = detail;
                    try { RaiseStatus(); } catch { }
                    return true;
                }
                detail = "P0.4 未知/无效，延时窗不取消：" + sample.FormatLog();
                return true;
            }
            catch (Exception ex)
            {
                detail = "P0.4 硬读异常，延时窗不取消：" + ex.Message;
                return true;
            }
        }

        /// <summary>P0.7 上升沿：触发 ART PFI0 + Pico EXT（须已 HVOFF 且 AO=0）。随后立刻 Read，勿先睡满再读。</summary>
        void BeginSwitchConduction()
        {
            if (_backend.HvOn)
                throw new InvalidOperationException("SAFETY：HV 未关，禁止开关导通触发。");
            if (Math.Abs(_backend.LastAoVolts) > 1e-6)
                throw new InvalidOperationException("SAFETY：AO 未清零，禁止开关导通触发。");
            // 导通 1s 指令发出即起算：第 2 秒结束才允许下一次加压
            _pressIntervalEpoch = Stopwatch.StartNew();
            bool lamp = _backend.ConnectionLampHigh;
            _backend.WriteDigitalOutputs(false, false, lamp, true);
        }

        /// <summary>
        /// 触发/导通指令起算：距下一次加压至少 NextPressAfterConductionStartMilliseconds（2s）。
        /// 已超过则立即返回（不必空等满 2s）；不足则只补足差额。
        /// </summary>
        void WaitForNextPressSlotAfterConduction(CancellationToken cancellation)
        {
            if (_pressIntervalEpoch == null) return;
            int elapsed = (int)_pressIntervalEpoch.ElapsedMilliseconds;
            int remain = NextPressAfterConductionStartMilliseconds - elapsed;
            if (remain <= 0) return; // 已满至少 2s，直接进下一次
            _message = "触发间隔：已过 " + elapsed + "ms，补足 " + remain
                + "ms（至少 " + NextPressAfterConductionStartMilliseconds + "ms，已过则不等满）";
            RaiseStatus();
            SleepCancellable(remain, cancellation, "触发至下一次加压至少间隔");
        }

        /// <summary>导通保持满 SwitchConductionHoldMilliseconds（默认1s）后拉低；已超时则立即拉低。</summary>
        void EndSwitchConduction(Stopwatch condClock)
        {
            try
            {
                if (condClock != null)
                {
                    int remain = SwitchConductionHoldMilliseconds - (int)condClock.ElapsedMilliseconds;
                    if (remain > 0)
                        Thread.Sleep(remain);
                }
            }
            catch { /* 仍拉低 */ }
            try
            {
                bool lamp = _backend.ConnectionLampHigh;
                _backend.WriteDigitalOutputs(false, false, lamp, false);
            }
            catch { }
        }

        /// <summary>兼容：上升沿 + 保持 1s + 拉低（无伴随采集时用）。</summary>
        void PulseSwitchConductionTrigger()
        {
            var clock = Stopwatch.StartNew();
            BeginSwitchConduction();
            EndSwitchConduction(clock);
        }

        /// <summary>
        /// HVOFF/预臂期间刷新直流监控缓存（分压/源V/电流），避免 UI 继续画 HVUP 末拍高压平台。
        /// 有限 AI4 占用时 ReadFeedback 会失败，返回 false（保持上一拍真实关源后读数，勿回写峰值）。
        /// </summary>
        bool TryPollMonitorFeedbackIntoCaches(bool raiseStatus)
        {
            try
            {
                LiveFeedbackSample fb = _backend.ReadFeedback();
                double div = Math.Abs(ScaleDivider(fb));
                double cur = Math.Abs(ScaleCurrent(fb));
                _lastDividerEngV = div;
                _lastCurrentA = cur;
                try { ScaleSourceVoltage(fb); } catch { /* 侧写 _lastSourceEngV */ }
                if (raiseStatus) RaiseStatus();
                return true;
            }
            catch
            {
                return false;
            }
        }

        void ForceSourceOffSafe()
        {
            try { _backend.WriteAoVolts(0); } catch { /* 仍尝试关 HV */ }
            try { SetHvEnergizedSynced(false); } catch
            {
                try { _backend.WriteDigitalOutputs(false, false, _backend.ConnectionLampHigh, false); } catch { }
            }
        }

        void AssertAoZero(string where)
        {
            if (Math.Abs(_backend.LastAoVolts) > 1e-6)
                throw new InvalidOperationException("SAFETY ASSERT 失败（" + where + "）：AO 仍为 " + _backend.LastAoVolts + " V，必须为 0。");
            if (_backend.HvOn || _backend.HvIndicatorOn)
                throw new InvalidOperationException("SAFETY ASSERT 失败（" + where + "）：高压继电器/指示灯仍为接通。");
            if (_backend.HvOn != _backend.HvIndicatorOn)
                throw new InvalidOperationException("SAFETY ASSERT 失败（" + where + "）：HVON 与加压灯不同步。");
        }

        /// <summary>
        /// Open 后 ART+Pico 双身份硬门：期望串（LiveHardwareOptions.Expected* 或 Options.ArtSerial）已配置则必须匹配。
        /// 未配置期望 → 跳过（与 HardwareDualIdentity / StatusCopy.DualIdentityNotConfigured 一致）。
        /// 失败：停监视、P0.5 置低、Close 后端（释租约）、清 Open 态后抛错。
        /// </summary>
        void AssertDualIdentityOrRollbackUnlocked()
        {
            string expArt = _hw != null ? _hw.ExpectedArtIdentity : null;
            string expPico = _hw != null ? _hw.ExpectedPicoSerial : null;
            if (string.IsNullOrWhiteSpace(expArt) && _options != null && !string.IsNullOrWhiteSpace(_options.ArtSerial))
                expArt = _options.ArtSerial;

            if (string.IsNullOrWhiteSpace(expArt) && string.IsNullOrWhiteSpace(expPico))
            {
                _message += "；" + StatusCopy.DualIdentityNotConfigured;
                return;
            }

            string actArt = _artDeviceCached ?? string.Empty;
            string actPico = _picoSerialCached ?? string.Empty;
            var fake = _backend as FakeLiveHardwareBackend;
            if (fake != null)
            {
                if (!string.IsNullOrEmpty(fake.OpenedArtDeviceName)) actArt = fake.OpenedArtDeviceName;
                actPico = fake.OpenedPicoSerial ?? string.Empty;
            }
            else
            {
                var art = _backend as ArtPicoLiveBackend;
                if (art != null)
                {
                    if (!string.IsNullOrEmpty(art.OpenedArtDeviceName)) actArt = art.OpenedArtDeviceName;
                    if (art.OpenedPicoSerial != null) actPico = art.OpenedPicoSerial;
                }
            }

            string msg, status;
            if (HardwareDualIdentity.TryValidate(expArt, expPico, actArt, actPico, out msg, out status))
            {
                _message += "；" + msg;
                return;
            }

            try { StopEStopMonitor(); } catch { }
            try { _backend.WriteDigitalOutputs(false, false, false, false); } catch { }
            try { _backend.Close(); } catch { }
            _open = false;
            _artDeviceCached = string.Empty;
            _picoSerialCached = string.Empty;
            _lastError = msg;
            throw new InvalidOperationException(msg
                + " 已回滚 Open（P0.5 已置低；SDK 租约已释放）。请核对 OWTS_EXPECTED_ART_IDENTITY / OWTS_EXPECTED_PICO_SERIAL / OWTS_PICO_SERIAL。");
        }

        void StartEStopMonitorUnlocked()
        {
            StopEStopMonitor();
            int poll = _hw.EStopPollIntervalMilliseconds > 0 ? _hw.EStopPollIntervalMilliseconds : LiveEmergencyMonitor.DefaultPollIntervalMs;
            _estopMonitor = new LiveEmergencyMonitor(_backend, OnEStopMonitorEvent, OnIdleDiRebuildRequired, poll);
            _estopMonitor.Start();
            try { _estopMonitor.EnableIdleTimeoutRetry(); } catch { }
        }

        /// <summary>监视线程排队：空闲两次 DI 超时后请求 owner 一次重建（加压中不会触发）。</summary>
        void OnIdleDiRebuildRequired()
        {
            try { TryRebuildIdleDiIfRequired("monitor-callback"); }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _message = "空闲 DI 重建失败：" + ex.Message;
                try { RaiseStatus(); } catch { }
            }
        }

        /// <summary>
        /// 空闲且无输出时执行 IdleDiRebuildPolicy（每会话一次）。
        /// FakeLive：仅重启监视；Art：RebuildIdleDiTask + 新监视。
        /// </summary>
        void TryRebuildIdleDiIfRequired(string reason)
        {
            if (Interlocked.CompareExchange(ref _idleDiRebuildInFlight, 1, 0) != 0)
                return;
            try
            {
                LiveEmergencyMonitor mon = _estopMonitor;
                if (mon == null || !mon.IdleTimeoutRebuildRequired)
                    return;
                if (_cycleRunning || _calibrating)
                    return;
                bool hv;
                double ao;
                try { hv = _backend.HvOn; ao = _backend.LastAoVolts; }
                catch { return; }
                if (hv || Math.Abs(ao) > 1e-9)
                    throw new InvalidOperationException("存在输出时禁止 DI 重建。");

                System.Console.WriteLine("[P0.4监视] P04_IDLE_REBUILD_BEGIN reason=" + reason
                    + " detail=" + mon.IdleRetryDetail);

                // 先停旧监视（读线程须退出后才能 Clear DI）
                StopEStopMonitor();

                var art = _backend as ArtPicoLiveBackend;
                if (art != null)
                {
                    art.RebuildIdleDiTask(_idleDiRebuild);
                }
                else
                {
                    // FakeLive：无真实 DI 任务；仍消耗每会话一次策略门闩（第二次抛错→锁存）
                    _idleDiRebuild.Rebuild(
                        verifiedIdleNoOutput: true,
                        stopOldReader: () => { },
                        probeExistingIdentity: () => { },
                        clearOldTask: () => { },
                        createAndValidateReplacement: () => { });
                }

                // 重启监视并确认新鲜可读
                int poll = _hw.EStopPollIntervalMilliseconds > 0 ? _hw.EStopPollIntervalMilliseconds : LiveEmergencyMonitor.DefaultPollIntervalMs;
                _estopMonitor = new LiveEmergencyMonitor(_backend, OnEStopMonitorEvent, OnIdleDiRebuildRequired, poll);
                _estopMonitor.Start();
                // 重建后：本会话不再允许第二次 IdleDi 重建；不再 EnableIdleTimeoutRetry 的「再重建」路径
                // Acknowledge 清 exhausted，但 IdleDiRebuildPolicy.Attempted 已 true → 再 Rebuild 会抛
                _estopMonitor.AcknowledgeIdleRebuildRecovered();
                // 进一步超时直接 Unknown 锁存，不再二次重建（与 Factory「further double-timeouts latch」一致）
                // 不调用 EnableIdleTimeoutRetry：避免又走到 RebuildRequired
                _message = "空闲 P0.4 DI 已一次重建（仅恢复只读待机，不自动加压）；reason=" + reason;
                System.Console.WriteLine("[P0.4监视] P04_IDLE_REBUILD_RECOVERED " + _message);
                try { RaiseStatus(); } catch { }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _latched = true;
                try { ForceSourceOffSafe(); } catch { }
                _message = "空闲 DI 重建失败已锁存：" + ex.Message;
                System.Console.WriteLine("[P0.4监视] P04_IDLE_REBUILD_FAILED " + ex.Message);
                try { RaiseStatus(); } catch { }
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _idleDiRebuildInFlight, 0);
            }
        }

        void StopEStopMonitor()
        {
            LiveEmergencyMonitor m = _estopMonitor;
            _estopMonitor = null;
            if (m != null)
            {
                try { m.Stop(); } catch { }
                try { m.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// P0.4 独立监视回调（边沿）：与加压循环解耦。
        /// 按下/未知：加压中 → HV OFF + 锁存 + Pico 同步采集标记；空闲 → 关源不锁存。
        /// 解除：仅刷新状态（顶栏以硬件为准）；不自动清软件锁存。
        /// </summary>
        void OnEStopMonitorEvent(LiveEStopMonitorKind kind, LiveEStopSample sample)
        {
            if (sample == null)
                sample = new LiveEStopSample { ReadOk = false, Pressed = false, Error = "null sample" };
            NoteEStopSample(sample);
            string log = sample.FormatLog();

            if (kind == LiveEStopMonitorKind.Released)
            {
                // 加压中急停边沿会置软件锁存；硬件解除且源已关后必须清锁存，否则「开始加压」一直灰
                string clr = null;
                bool cleared = false;
                if (_latched)
                {
                    try { cleared = TryClearEmergencyLatchIfHardwareSafe(out clr); }
                    catch (Exception ex) { clr = ex.Message; cleared = false; }
                }
                if (cleared)
                    _message = "急停独立监视（P0.4）：解除并已清软件锁存；" + (clr ?? "") + "；" + log;
                else
                    _message = "急停独立监视（P0.4）：解除；" + log
                        + (_latched ? ("（软件锁存仍在，将在源关断后自动清：" + (clr ?? "") + "）") : "");
                try { RaiseStatus(); } catch { }
                return;
            }

            bool operatorAborting = Interlocked.CompareExchange(ref _operatorStop, 0, 0) != 0;
            bool sourceOn = false;
            try { sourceOn = _backend.HvOn || Math.Abs(_backend.LastAoVolts) > 1e-6; } catch { sourceOn = false; }
            // 操作员停止后 _cycleRunning 仍可能为 true：未知边沿不得据此永久锁存
            bool energizing = sourceOn || (_cycleRunning && !operatorAborting);
            try { ForceSourceOffSafe(); } catch { }
            if (operatorAborting && kind == LiveEStopMonitorKind.Unknown)
            {
                _message = "操作员停止后 P0.4 未知边沿已忽略（未锁存，可再加压）；" + log;
                try { RaiseStatus(); } catch { }
                return;
            }
            if (energizing)
            {
                // 真急停/加压中未知：关源并锁存；Ramp/启动失败路径收 CaptureAfterHvupStop
                _latched = true;
                _pressAbortCapturePending = true;
                _message = "急停→跳 HVOFF；" + StatusCopy.PressAbortHvOffPicoCapture
                    + "（监视=" + kind + "；" + log + "）";
            }
            else
            {
                // 空闲急停：不置 _operatorStop / 不锁存，避免后续加压启动即被取消
                _message = "急停独立监视（P0.4）：" + kind
                    + "；空闲态已确认关源（未加压锁存，解除后可再加压）；" + log;
            }
            try { RaiseStatus(); } catch { }
        }

        void NoteEStopSample(LiveEStopSample sample)
        {
            if (sample == null) return;
            _lastEStopSampleLog = sample.FormatLog();
            bool? pressed = sample.ReadOk ? (bool?)sample.Pressed : null;
            // 仅缓存；边沿日志由 LiveEmergencyMonitor / DesktopUi 顶栏打印，避免 GetStatus 轮询刷屏
            _lastEStopPressedLogged = pressed;
        }

        static bool ShouldSyncPicoCaptureOnAbort(AcquisitionExitReason exit)
        {
            return exit == AcquisitionExitReason.EmergencyStop
                || exit == AcquisitionExitReason.HardOverCurrent
                || exit == AcquisitionExitReason.OverVoltage
                || exit == AcquisitionExitReason.CurrentLimitStall
                || exit == AcquisitionExitReason.RampTimeout
                || exit == AcquisitionExitReason.OperatorStop
                || exit == AcquisitionExitReason.HardwareFault
                || exit == AcquisitionExitReason.CaptureFault;
        }

        bool IsWholeDeviceOffline()
        {
            try
            {
                if (!_open || _backend == null || !_backend.IsOpen) return true;
                // Art 急停可读且会话仍开 → 非整机掉线
                try { _backend.ReadEStopPressed(); }
                catch { return true; }
                return false;
            }
            catch { return true; }
        }

        /// <summary>仅 Pico 掉线时：关闭再重开；返回提示文案（成功或失败说明）。</summary>
        string TryRecoverPicoComm()
        {
            try
            {
                if (_backend.PicoOpen) return null;
            }
            catch { }

            try { _backend.ClosePicoOnly(); } catch { }
            string err;
            if (_backend.TryReopenPico(_picoSerialCached, out err))
            {
                _picoCommWarning = StatusCopy.PicoCommAnomalyRetry;
                return _picoCommWarning;
            }
            _picoCommWarning = StatusCopy.PicoCommAnomalyRetry + "（重开失败：" + (err ?? "") + "）";
            return _picoCommWarning;
        }

        double ScaleDividerRawPeak(LiveFeedbackSample fb)
        {
            if (fb == null) return 0;
            // Fake：峰值已是工程电压；真机：raw 峰值 × DividerRatio
            if (_backend is FakeLiveHardwareBackend) return fb.PeakDividerAbs > 0 ? fb.PeakDividerAbs : Math.Abs(fb.DividerVoltageV);
            double raw = fb.PeakDividerAbs > 0 ? fb.PeakDividerAbs : Math.Abs(fb.DividerRawV);
            return raw * (_hw.DividerRatioVPerV > 0 ? _hw.DividerRatioVPerV : 1.0);
        }

        double ScaleCurrentPeak(LiveFeedbackSample fb)
        {
            if (fb == null) return 0;
            if (_backend is FakeLiveHardwareBackend) return fb.PeakCurrentAbs > 0 ? fb.PeakCurrentAbs : Math.Abs(fb.CurrentA);
            double raw = fb.PeakCurrentAbs > 0 ? fb.PeakCurrentAbs : Math.Abs(fb.CurrentA);
            return raw * (_hw.CurrentScaleAPerVolt > 0 ? _hw.CurrentScaleAPerVolt : 1.0);
        }

        double ScaleDivider(LiveFeedbackSample fb)
        {
            // Fake：DividerVoltageV 已是工程电压；真机：raw × 台架 DividerRatio
            if (_backend is FakeLiveHardwareBackend) return fb.DividerVoltageV;
            return fb.DividerRawV * _hw.DividerRatioVPerV;
        }

        double ScaleCurrent(LiveFeedbackSample fb)
        {
            // Fake：CurrentA 已是工程安培；真机 AI0 rawV × CurrentScaleAPerVolt
            if (_backend is FakeLiveHardwareBackend) return fb.CurrentA;
            return fb.CurrentA * _hw.CurrentScaleAPerVolt;
        }

        /// <summary>
        /// AI2 原始 → 工程 V。Fake 默认已是工程值（兼容旧残压注入）；
        /// Fake.SourceFeedbackIsRaw=true 或真机 → 走 <see cref="LiveAi2SourceScaling"/>。
        /// 未标定时 Status 明确（回退 VoltageGain 仅供残压参考）。
        /// </summary>
        double ScaleSourceVoltage(LiveFeedbackSample fb)
        {
            string status;
            double eng = ScaleSourceVoltageCore(fb, out status);
            _lastAi2ScaleStatus = status ?? string.Empty;
            if (!double.IsNaN(eng)) _lastSourceEngV = eng;
            return eng;
        }

        double ScaleSourceVoltageCore(LiveFeedbackSample fb, out string statusMessage)
        {
            double raw = ResolveAi2RawVolts(fb);
            var fake = _backend as FakeLiveHardwareBackend;
            if (fake != null && !fake.SourceFeedbackIsRaw)
            {
                // Fake 默认：SourceVoltageV 已是工程伏特；仍报告标定状态
                statusMessage = LiveAi2SourceScaling.IsCalibrated(_hw)
                    ? StatusCopy.Ai2SourceScaleApplied
                    : StatusCopy.Ai2SourceScaleNotCalibrated;
                return raw;
            }
            return LiveAi2SourceScaling.ToEngineeringVolts(_hw, raw, out statusMessage);
        }

        static double ResolveAi2RawVolts(LiveFeedbackSample fb)
        {
            if (fb == null) return 0;
            if (!double.IsNaN(fb.SourceVoltageRawV) && !double.IsInfinity(fb.SourceVoltageRawV))
                return fb.SourceVoltageRawV;
            return fb.SourceVoltageV;
        }

        /// <summary>测试/台架：显式 raw AI2 → 工程 V，并更新最近 Status。</summary>
        public double ScaleAi2SourceVoltage(double rawAi2V, out string statusMessage)
        {
            ThrowIfDisposed();
            double eng = LiveAi2SourceScaling.ToEngineeringVolts(_hw, rawAi2V, out statusMessage);
            _lastAi2ScaleStatus = statusMessage ?? string.Empty;
            if (!double.IsNaN(eng)) _lastSourceEngV = eng;
            return eng;
        }

        public bool SourceVoltageScaleCalibrated { get { return LiveAi2SourceScaling.IsCalibrated(_hw); } }
        public string Ai2SourceScaleStatus
        {
            get
            {
                return string.IsNullOrEmpty(_lastAi2ScaleStatus)
                    ? LiveAi2SourceScaling.DescribeScale(_hw)
                    : _lastAi2ScaleStatus;
            }
        }

        void SyncFakeGainFromHw()
        {
            var fake = _backend as FakeLiveHardwareBackend;
            if (fake != null) fake.SetVoltageGain(_hw.VoltageGainVPerAoVolt);
        }

        /// <summary>台架写入增益/分压/电流比例；成功后 Live 非零加压才放行。</summary>
        public bool TryApplyBenchCalibration(LiveBenchCalibration cal, out string error)
        {
            ThrowIfDisposed();
            if (!_hw.TryApplyBenchCalibration(cal, out error)) return false;
            SyncFakeGainFromHw();
            _lastAi2ScaleStatus = LiveAi2SourceScaling.DescribeScale(_hw);
            _message = "台架标定已写入：VoltageGain=" + _hw.VoltageGainVPerAoVolt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " DividerRatio=" + _hw.DividerRatioVPerV.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " CurrentScale=" + _hw.CurrentScaleAPerVolt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " AI2Scale=" + _hw.SourceVoltageScaleVPerV.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "；" + _lastAi2ScaleStatus
                + "（≠ " + StatusCopy.ReadyToEnergize + "）";
            RaiseStatus();
            return true;
        }

        public void ApplyBenchCalibration(LiveBenchCalibration cal)
        {
            string error;
            if (!TryApplyBenchCalibration(cal, out error))
                throw new InvalidOperationException(error ?? StatusCopy.BenchGainNotCalibrated);
        }

        public bool BenchCalibrationApplied { get { return _hw.BenchCalibrationApplied; } }

        /// <summary>当前硬件选项上的台架标定快照（供编辑页回填；可能仍为占位）。</summary>
        public LiveBenchCalibration CaptureBenchCalibration()
        {
            ThrowIfDisposed();
            return new LiveBenchCalibration
            {
                Version = 1,
                BenchCalibrated = _hw.BenchCalibrationApplied
                    && !LiveBenchCalibration.IsDangerousPlaceholderVoltageGain(_hw.VoltageGainVPerAoVolt),
                VoltageGainVPerAoVolt = _hw.VoltageGainVPerAoVolt,
                DividerRatioVPerV = _hw.DividerRatioVPerV,
                CurrentScaleAPerVolt = _hw.CurrentScaleAPerVolt,
                SourceVoltageScaleVPerV = _hw.SourceVoltageScaleVPerV,
                CalibratedBy = string.Empty,
                CalibratedUtc = string.Empty,
                Notes = _hw.BenchCalibrationSource ?? string.Empty
            };
        }

        static string DescribeExit(AcquisitionExitReason e)
        {
            switch (e)
            {
                case AcquisitionExitReason.TargetReached: return "达到目标电压（±5% 有效窗口）";
                case AcquisitionExitReason.EmergencyStop: return "急停：已关源并锁存";
                case AcquisitionExitReason.HardOverCurrent: return "硬过流跳闸：已关源";
                case AcquisitionExitReason.OverVoltage: return "超压跳闸：已关源";
                case AcquisitionExitReason.CurrentLimitStall: return "电流限幅停滞：已关源";
                case AcquisitionExitReason.RampTimeout: return "升压超时：已关源";
                case AcquisitionExitReason.OperatorStop: return "操作停止：已关源";
                default: return e.ToString();
            }
        }

        Task<AcquisitionCycleResult> Finish(AcquisitionCycleResult result)
        {
            result.CompletedUtc = DateTime.UtcNow;
            RaiseStatus();
            return Task.FromResult(result);
        }

        /// <summary>首因冻结写 session-fault.json（无弹窗）；CleanupError 另走 Close 路径。</summary>
        void PersistSessionFault(AcquisitionExitReason exit, string detail, string phase)
        {
            if (_sessionFaultLatched) return;
            try
            {
                var cat = SessionFault.FromExitReason(exit);
                if (cat == SessionFaultCategory.Unknown && exit != AcquisitionExitReason.None)
                    cat = SessionFaultCategory.DriverError;
                var fault = SessionFault.Create(
                    "live",
                    cat,
                    phase ?? "Cycle",
                    detail ?? exit.ToString(),
                    "Session");
                fault.PriorDetail = _sessionFaultPriorDetail ?? string.Empty;
                SessionFault.WriteDefault(fault, cleanup: false);
                _sessionFaultLatched = true;
                _sessionFaultPriorDetail = fault.Detail ?? string.Empty;
            }
            catch { /* 诊断落盘失败不阻断关源 */ }
        }

        void RaiseStatus()
        {
            var h = StatusChanged;
            if (h == null) return;
            try { h(this, new HardwareStatusChangedEventArgs(GetStatus())); } catch { }
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LiveAcquisitionSession));
        }

        public void Dispose()
        {
            if (_disposed) return;
            StopEStopMonitor();
            Close();
            if (_ownsBackend) _backend.Dispose();
            _disposed = true;
        }
    }
}
