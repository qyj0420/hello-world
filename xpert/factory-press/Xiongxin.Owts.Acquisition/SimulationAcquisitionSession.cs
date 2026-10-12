using System;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.Acquisition
{
    /// <summary>
    /// 仿真采集内核：不加载 Art_DAQ / Pico 驱动。← MainForm.ReferenceSimulation 语义。
    /// 进程级单例由 <see cref="SingletonAcquisitionSession"/> 强制；本类可单独单测。
    /// </summary>
    public sealed class SimulationAcquisitionSession : IAcquisitionSession
    {
        AcquisitionSessionOptions _options;
        AcquisitionStatusSnapshot _status = new AcquisitionStatusSnapshot { Simulation = true, State = AcquisitionControllerState.Uninitialized };
        bool _disposed;
        bool _open;
        bool _estopPressed = true;
        bool _calibrated;
        bool _calibrating;
        bool _impedanceWide;
        bool _lampHigh;
        readonly IFirstZeroPhaseEvaluator _firstZero;

        public SimulationAcquisitionSession()
            : this(null)
        {
        }

        public SimulationAcquisitionSession(IFirstZeroPhaseEvaluator firstZeroPhaseEvaluator)
        {
            _firstZero = firstZeroPhaseEvaluator ?? new MissingDllFirstZeroPhaseEvaluator();
        }

        /// <summary>首零相位评估器（缺 DLL → MissingDll）。</summary>
        public IFirstZeroPhaseEvaluator FirstZeroPhaseEvaluator { get { return _firstZero; } }

        public event EventHandler<HardwareStatusChangedEventArgs> StatusChanged;

        public bool IsOpen { get { return _open && !_disposed; } }

        public bool ConnectionIndicationLampHigh { get { return _lampHigh; } }

        public bool HoldGroundActive { get { return false; } }

        public bool ConfirmReleaseGround(bool operatorAcknowledged)
        {
            return true; // 仿真无接地保持
        }

        public HardwareIdentityProbeResult Probe(HardwareProbeRequest request)
        {
            return new HardwareIdentityProbeResult
            {
                Connected = true,
                IdentityValid = true,
                ControllerSerial = "SIM-ART",
                AcquisitionSerial = "SIM-PICO",
                StatusCode = "Ready",
                Message = "仿真设备",
                AssetMatchStatus = "Simulation"
            };
        }

        public void Open(AcquisitionSessionOptions options)
        {
            ThrowIfDisposed();
            if (_open)
                throw new InvalidOperationException("仿真会话已 Open，禁止重复 Open（Pico 5242D 全局只允许一次）。");
            _options = options ?? new AcquisitionSessionOptions { Simulation = true };
            _options.Simulation = true;
            _open = true;
            _estopPressed = true;
            _calibrated = false;
            _calibrating = false;
            SetConnectionIndicationLamp(true);
            RefreshStatus("Simulation Open → " + StatusCopy.Connected + "（≠ " + StatusCopy.ReadyToEnergize + "）");
        }

        public void Close()
        {
            if (!_open && !_lampHigh)
            {
                _status.State = AcquisitionControllerState.Disposed;
                _status.Initialized = false;
                _status.IsDeviceConnected = false;
                _status.ConnectionStatusLabel = string.Empty;
                return;
            }
            _open = false;
            _calibrated = false;   // 断开后校准不再有效，下次连接须重新校准
            _calibrating = false;
            _estopPressed = true;  // 未知态按「急停按下」保守处理
            SetConnectionIndicationLamp(false);
            _status.State = AcquisitionControllerState.Disposed;
            _status.Initialized = false;
            _status.IsDeviceConnected = false;
            _status.ConnectionStatusLabel = string.Empty;
            _status.UpdatedUtc = DateTime.UtcNow;
            RaiseStatus();
        }

        public AcquisitionStatusSnapshot GetStatus()
        {
            return CloneStatus();
        }

        public Task<AcquisitionCycleResult> RunCycleAsync(AcquisitionCycleRequest request, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            if (!_open) throw new InvalidOperationException("Session is not open.");
            cancellation.ThrowIfCancellationRequested();

            double target = request != null ? request.TargetVoltageV : 0;
            bool zeroKv = !(target > 0);

            if (zeroKv)
            {
                // 0 kV 噪声对照允许，但不宣称可加压
                Console.WriteLine("[SIM] " + StatusCopy.ZeroKvNoiseOnly);
            }
            else if (_estopPressed)
            {
                return Task.FromResult(new AcquisitionCycleResult
                {
                    Success = false,
                    ExitReason = AcquisitionExitReason.EmergencyStop,
                    ErrorMessage = "非零加压需要「" + StatusCopy.EStopReleased + "」；当前「" + StatusCopy.EStopPressed + "」",
                    StartedUtc = DateTime.UtcNow,
                    CompletedUtc = DateTime.UtcNow
                });
            }

            bool isBgEarly = request != null && request.IsBackgroundCapture;
            _calibrating = isBgEarly;
            _status.CycleRunning = true;
            _status.IsCalibrating = _calibrating;
            _status.State = AcquisitionControllerState.Ramping;
            _status.StateMessage = _calibrating
                ? (StatusCopy.Calibrating + "（Pico 采集；急停/阻抗可随时切换）")
                : "Simulation cycle running";
            RaiseStatus();

            int samples = 4096;
            var adc = new short[samples];
            var divider = new double[samples];
            for (int i = 0; i < samples; i++)
            {
                double t = i / 125e6;
                adc[i] = (short)(Math.Sin(2 * Math.PI * 50 * t) * 1000);
                divider[i] = target * (0.2 + 0.8 * i / (double)samples);
            }

            int rangeIdx = _options != null ? _options.PicoRangeIndex : 6;
            if (rangeIdx < 0) rangeIdx = 6;
            double fullScale = Task6RangeAdvisor.Pico5242DFullScaleVolts[Math.Min(rangeIdx, Task6RangeAdvisor.Pico5242DFullScaleVolts.Length - 1)];
            bool isBg = request != null && request.IsBackgroundCapture;
            double dividerRatio = _options != null && _options.DividerRatioVPerV > 0 ? _options.DividerRatioVPerV : 11000.0;
            var advice = Task6RangeAdvisor.AdviseWithEvaluator(
                _firstZero, rangeIdx, adc, 32512, false, fullScale, 125e6,
                divider, 10000, isBg, target, dividerRatio);
            var result = new AcquisitionCycleResult
            {
                Success = true,
                IsBackgroundCapture = isBg,
                ExitReason = AcquisitionExitReason.TargetReached,
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
                PeakActualVoltageV = target,
                PeakCurrentA = 0.01,
                UsbDividerRawVolts = divider,
                UsbSampleRateHz = 10000,
                PicoRawAdc = adc,
                PicoSampleRateHz = 125e6,
                PicoSampleIntervalNanoseconds = 8,
                PicoFullScaleVolts = fullScale,
                PicoMaximumAdc = 32512,
                PicoRangeIndexUsed = rangeIdx,
                PicoSerial = "SIM-PICO",
                ArtSerial = "SIM-ART",
                Task6RangeSuitable = advice.Suitable,
                SuggestedPicoRangeIndex = advice.SuggestedRangeIndex,
                FirstZeroTimeSeconds = double.NaN,
                DischargeVerified = true,
                DischargeStatusMessage = Xiongxin.Owts.Contracts.Ui.StatusCopy.DischargeVerifiedSafe
            };
            Task6RangeAdvisor.ApplyToCycleResult(result, advice);

            _calibrating = false;
            _status.CycleRunning = false;
            _status.IsCalibrating = false;
            _status.State = AcquisitionControllerState.Completed;
            _status.ActualVoltageV = target;
            _status.StateMessage = zeroKv
                ? ("Simulation 0 kV: " + StatusCopy.ZeroKvNoiseOnly)
                : "Simulation cycle completed";
            _status.UpdatedUtc = DateTime.UtcNow;
            RaiseStatus();
            return Task.FromResult(result);
        }

        public void EmergencyStop()
        {
            _estopPressed = true;
            _status.EmergencyLatched = true;
            _status.EStopPressed = true;
            _status.EStopHardwareLog = "(仿真软件标志；非 P0.4)";
            _status.State = AcquisitionControllerState.Faulted;
            _status.CycleRunning = false;
            _status.StateMessage = "EmergencyStop → " + StatusCopy.EStopPressed + " (simulation)";
            _status.UpdatedUtc = DateTime.UtcNow;
            RaiseStatus();
        }

        public void RequestOperatorAbort()
        {
            // UI 停止：不置急停按下、不永久锁存（对齐 LiveAcquisitionSession.RequestOperatorAbort）
            _status.CycleRunning = false;
            _status.IsCalibrating = false;
            _calibrating = false;
            if (_status.State == AcquisitionControllerState.Ramping
                || _status.State == AcquisitionControllerState.CaptureWaiting
                || _status.State == AcquisitionControllerState.ArmingAcquisition)
                _status.State = AcquisitionControllerState.SafeIdle;
            _status.StateMessage = StatusCopy.OperatorAbortNoLatch + " (simulation)";
            _status.UpdatedUtc = DateTime.UtcNow;
            RaiseStatus();
        }

        public bool ReadSafetyReady()
        {
            // 加压侧「联锁就绪」≈ 急停解除；≠ 可加压全文
            return _open && !_estopPressed;
        }

        public void SetEStopPressed(bool pressed)
        {
            ThrowIfDisposed();
            _estopPressed = pressed;
            if (!pressed) _status.EmergencyLatched = false;
            RefreshStatus(pressed ? StatusCopy.EStopPressed : StatusCopy.EStopReleased);
        }

        public void SetCalibrated(bool calibrated)
        {
            ThrowIfDisposed();
            _calibrated = calibrated;
            if (calibrated) _calibrating = false;
            RefreshStatus(calibrated
                ? (StatusCopy.Calibrated + "（≠ " + StatusCopy.ReadyToEnergize + "）")
                : StatusCopy.NotCalibrated);
        }

        public bool ImpedanceBandwidthWide { get { return _impedanceWide; } }

        public void SetImpedanceBandwidthWide(bool wide)
        {
            ThrowIfDisposed();
            _impedanceWide = wide;
            // 校准中亦可调：不检查 _calibrating
            RefreshStatus(StatusCopy.ImpedanceAdjustableAnytime
                + "；当前宽频=" + (wide ? "开" : "关")
                + (_calibrating ? "（" + StatusCopy.Calibrating + "中已生效）" : ""));
        }

        public void SetConnectionIndicationLamp(bool high)
        {
            _lampHigh = high;
            Console.WriteLine(high
                ? "[SIM] ART P0.5 连接灯置高（连通维持）"
                : "[SIM] ART P0.5 连接灯置低（断开）");
            _status.ConnectionLampHigh = high;
        }

        void RefreshStatus(string message)
        {
            _status = new AcquisitionStatusSnapshot
            {
                Initialized = _open,
                SafetyReady = _open && !_estopPressed,
                State = _open ? AcquisitionControllerState.SafeIdle : AcquisitionControllerState.Uninitialized,
                Simulation = true,
                StateMessage = message ?? string.Empty,
                UpdatedUtc = DateTime.UtcNow,
                IsDeviceConnected = _open,
                ConnectionLampHigh = _lampHigh,
                EStopPressed = _estopPressed,
                EStopHardwareLog = "(仿真软件标志；非 P0.4)",
                IsCalibrated = _calibrated,
                IsCalibrating = _calibrating,
                ImpedanceBandwidthWide = _impedanceWide,
                ConnectionStatusLabel = _open ? StatusCopy.Connected : string.Empty,
                EmergencyLatched = _status != null && _status.EmergencyLatched,
                ActualVoltageV = _status != null ? _status.ActualVoltageV : 0,
                CurrentA = _status != null ? _status.CurrentA : 0,
                CycleRunning = _status != null && _status.CycleRunning,
                LastError = _status != null ? _status.LastError : string.Empty
            };
            RaiseStatus();
        }

        AcquisitionStatusSnapshot CloneStatus()
        {
            return new AcquisitionStatusSnapshot
            {
                State = _status.State,
                Initialized = _status.Initialized,
                CycleRunning = _status.CycleRunning,
                SafetyReady = _status.SafetyReady,
                EmergencyLatched = _status.EmergencyLatched,
                ActualVoltageV = _status.ActualVoltageV,
                CurrentA = _status.CurrentA,
                StateMessage = _status.StateMessage,
                LastError = _status.LastError,
                Simulation = true,
                UpdatedUtc = _status.UpdatedUtc,
                IsDeviceConnected = _open,
                ConnectionLampHigh = _lampHigh,
                EStopPressed = _estopPressed,
                EStopHardwareLog = "(仿真软件标志；非 P0.4)",
                IsCalibrated = _calibrated,
                IsCalibrating = _calibrating,
                ImpedanceBandwidthWide = _impedanceWide,
                ConnectionStatusLabel = _open ? StatusCopy.Connected : string.Empty
            };
        }

        void RaiseStatus()
        {
            EventHandler<HardwareStatusChangedEventArgs> handler = StatusChanged;
            if (handler != null) handler(this, new HardwareStatusChangedEventArgs(CloneStatus()));
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimulationAcquisitionSession));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }
}
