using System;

namespace Xiongxin.Owts.Contracts.Acquisition
{
    /// <summary>← HardwareStatusSnapshot / IDeviceIdentity</summary>
    public sealed class HardwareProbeRequest
    {
        public string RequestedHubMac { get; set; }
        public string RequestedControllerLocation { get; set; }
        public string RequestedAcquisitionLocation { get; set; }
        public bool AllowSimulation { get; set; }
    }

    public sealed class HardwareIdentityProbeResult
    {
        public bool Connected { get; set; }
        public bool IdentityValid { get; set; }
        public string ControllerSerial { get; set; }
        public string AcquisitionSerial { get; set; }
        public string StatusCode { get; set; }
        public string Message { get; set; }
        public string Diagnostic { get; set; }
        public string AssetMatchStatus { get; set; }
        public string MatchedFactoryNumber { get; set; }
        public string MatchedDeviceModel { get; set; }

        public HardwareIdentityProbeResult()
        {
            StatusCode = "Offline";
            Message = string.Empty;
            Diagnostic = string.Empty;
            ControllerSerial = string.Empty;
            AcquisitionSerial = string.Empty;
            AssetMatchStatus = "NotEvaluated";
            MatchedFactoryNumber = string.Empty;
            MatchedDeviceModel = string.Empty;
        }
    }

    public sealed class HardwareStatusChangedEventArgs : EventArgs
    {
        public AcquisitionStatusSnapshot Status { get; private set; }
        public HardwareStatusChangedEventArgs(AcquisitionStatusSnapshot status) { Status = status ?? new AcquisitionStatusSnapshot(); }
    }

    /// <summary>← Task38Configuration (subset)</summary>
    public sealed class AcquisitionSessionOptions
    {
        public string ArtDeviceName { get; set; }
        public string ArtSerial { get; set; }
        public string PicoSerial { get; set; }
        public double VoltageGainVPerAoVolt { get; set; }
        public double DividerRatioVPerV { get; set; }
        /// <summary>AI0 电流比例 A/V；0=不覆盖 LiveHardwareOptions。</summary>
        public double CurrentScaleAPerVolt { get; set; }
        /// <summary>AI2 源反馈工程比例 V/V；0=不覆盖 / 未标定。</summary>
        public double SourceVoltageScaleVPerV { get; set; }
        /// <summary>台架标定已写入（非危险占位）。Live 非零加压硬门。</summary>
        public bool BenchCalibrationApplied { get; set; }
        public double CurrentLimitA { get; set; }
        public double HardOverCurrentA { get; set; }
        public double AoMaximumVolts { get; set; }
        public double TargetReachedToleranceFraction { get; set; }
        public int PicoRangeIndex { get; set; }
        public bool Simulation { get; set; }

        public AcquisitionSessionOptions()
        {
            ArtDeviceName = "Dev1";
            ArtSerial = string.Empty;
            PicoSerial = string.Empty;
            AoMaximumVolts = 5.0;
            TargetReachedToleranceFraction = 0.02;
            Simulation = true;
        }
    }

    public sealed class AcquisitionCycleRequest
    {
        public double TargetVoltageV { get; set; }
        public bool IsBackgroundCapture { get; set; }
        public int SuggestedPicoRangeIndex { get; set; }

        public AcquisitionCycleRequest() { SuggestedPicoRangeIndex = -1; }
    }

    /// <summary>← Task38ExitReason</summary>
    public enum AcquisitionExitReason
    {
        None = 0,
        TargetReached = 1,
        EmergencyStop = 2,
        OperatorStop = 3,
        CurrentLimitStall = 4,
        HardOverCurrent = 5,
        OverVoltage = 6,
        RampTimeout = 7,
        HardwareFault = 8,
        CaptureFault = 9
    }

    /// <summary>← Task38ControllerState</summary>
    public enum AcquisitionControllerState
    {
        Uninitialized = 0,
        SafeIdle = 1,
        Precheck = 2,
        ReleasingGround = 3,
        HvEnableDelay = 4,
        Ramping = 5,
        SourceOff = 6,
        ArmingAcquisition = 7,
        GroundDelay = 8,
        CaptureWaiting = 9,
        DischargeVerify = 10,
        Completed = 11,
        Faulted = 12,
        Disposed = 13
    }

    /// <summary>← Task38CycleResult</summary>
    public sealed class AcquisitionCycleResult
    {
        public bool Success { get; set; }
        public bool IsBackgroundCapture { get; set; }
        public AcquisitionExitReason ExitReason { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
        public double PeakActualVoltageV { get; set; }
        public double PeakCurrentA { get; set; }
        public double[] UsbDividerRawVolts { get; set; }
        public double UsbSampleRateHz { get; set; }
        public short[] PicoRawAdc { get; set; }
        public double PicoSampleRateHz { get; set; }
        /// <summary>实际分辨率位数（14/15）。</summary>
        public int PicoResolutionBits { get; set; }
        public double PicoSampleIntervalNanoseconds { get; set; }
        public double PicoFullScaleVolts { get; set; }
        public short PicoMaximumAdc { get; set; }
        public int PicoRangeIndexUsed { get; set; }
        public string PicoSerial { get; set; }
        public string ArtSerial { get; set; }
        public bool Task6RangeSuitable { get; set; }
        public int SuggestedPicoRangeIndex { get; set; }
        /// <summary>溢出/削顶或峰近满量程（自动量程升档依据）。</summary>
        public bool PicoOverrangeOrClipped { get; set; }
        /// <summary>Task6/启发式量程建议文案（供日志/UI）。</summary>
        public string Task6RangeMessage { get; set; }
        public double FirstZeroTimeSeconds { get; set; }

        /// <summary>关源后残压确认是否通过。false 时必须展示 StatusCopy.DischargeNotVerified。</summary>
        public bool DischargeVerified { get; set; }
        public string DischargeStatusMessage { get; set; }

        public AcquisitionCycleResult()
        {
            ErrorMessage = string.Empty;
            UsbDividerRawVolts = new double[0];
            PicoRawAdc = new short[0];
            PicoSerial = string.Empty;
            ArtSerial = string.Empty;
            SuggestedPicoRangeIndex = -1;
            Task6RangeMessage = string.Empty;
            FirstZeroTimeSeconds = double.NaN;
            PicoMaximumAdc = 32512;
            PicoResolutionBits = 14;
            DischargeStatusMessage = string.Empty;
        }
    }

    /// <summary>← Task38StatusSnapshot + HardwareStatusSnapshot</summary>
    public sealed class AcquisitionStatusSnapshot
    {
        public AcquisitionControllerState State { get; set; }
        public bool Initialized { get; set; }
        public bool CycleRunning { get; set; }
        public bool SafetyReady { get; set; }
        public bool EmergencyLatched { get; set; }
        public double ActualVoltageV { get; set; }
        public double CurrentA { get; set; }
        public string StateMessage { get; set; }
        public string LastError { get; set; }
        public bool Simulation { get; set; }
        public DateTime UpdatedUtc { get; set; }

        /// <summary>通信会话已 Open（Art+Pico）；显示用 StatusCopy.Connected，≠ 可加压。</summary>
        public bool IsDeviceConnected { get; set; }
        /// <summary>ART P0.5 连接灯当前是否置高（仿真可日志）。</summary>
        public bool ConnectionLampHigh { get; set; }
        /// <summary>急停蘑菇头是否按下。校准中亦可切换；非零加压页要 false。实机以 P0.4 硬件为准。</summary>
        public bool EStopPressed { get; set; }
        /// <summary>最近一次 P0.4 raw DI 字节（仅 EStopRawDiValid 时有意义）。</summary>
        public byte EStopRawDi { get; set; }
        /// <summary>true=EStopRawDi 来自成功硬件/Fake 读。</summary>
        public bool EStopRawDiValid { get; set; }
        /// <summary>P0.4 对表日志一行（raw/电平/判定/极性）。</summary>
        public string EStopHardwareLog { get; set; }
        /// <summary>当前相/工程是否已完成校准（≠ 校准中）。</summary>
        public bool IsCalibrated { get; set; }
        /// <summary>Pico 正在做校准采集（背景/校准轮 CycleRunning）。</summary>
        public bool IsCalibrating { get; set; }
        /// <summary>阻抗板带宽宽档（P1.2）；随时可调。</summary>
        public bool ImpedanceBandwidthWide { get; set; }
        /// <summary>定稿连接态文案，恒为「已连接」或空。</summary>
        public string ConnectionStatusLabel { get; set; }
        /// <summary>残压未通过时保持接地标志；true 时禁止任何「可接近」暗示。</summary>
        public bool HoldGroundActive { get; set; }
        public bool DischargeVerified { get; set; }
        /// <summary>Live：台架增益/分压已写入且非危险占位；仿真恒可视为不强制。</summary>
        public bool BenchCalibrationApplied { get; set; }
        /// <summary>AI2 SourceVoltageScaleVPerV &gt; 0 已写入。</summary>
        public bool SourceVoltageScaleCalibrated { get; set; }
        /// <summary>最近一次 AI2 换算 StatusCopy（已标定 / 未标定 / 回退 Gain）。</summary>
        public string Ai2SourceScaleStatus { get; set; }
        /// <summary>最近一次 AI2 工程电压（V）；未读过可为 0。</summary>
        public double LastSourceVoltageEngineeringV { get; set; }
        /// <summary>最近一次分压器工程电压绝对值（V）。</summary>
        public double LastDividerEngineeringV { get; set; }

        /// <summary>本轮 HVON 段耗时（含建 AI+100ms），毫秒；未跑为 0。</summary>
        public int StepHvonElapsedMs { get; set; }
        /// <summary>本轮 HVUP 升压段耗时（分压到位），毫秒。</summary>
        public int StepHvupElapsedMs { get; set; }
        /// <summary>本轮真正 HVOFF+AO 清零耗时（不含 Pico/ART 预臂与 500/50ms 固定延时），毫秒。</summary>
        public int StepHvoffElapsedMs { get; set; }
        /// <summary>本轮采集触发段耗时（开关导通触发起至读回），毫秒；不含触发前 HVOFF/延时。</summary>
        public int StepCaptureElapsedMs { get; set; }
        /// <summary>HVON→HVUP 延时后读到的电流（A），供漏电核对；NaN=未采。</summary>
        public double StepDelayLeakageA { get; set; }

        public AcquisitionStatusSnapshot()
        {
            StateMessage = string.Empty;
            LastError = string.Empty;
            ConnectionStatusLabel = string.Empty;
            Ai2SourceScaleStatus = string.Empty;
            EStopHardwareLog = string.Empty;
            StepDelayLeakageA = double.NaN;
            UpdatedUtc = DateTime.UtcNow;
        }
    }
}
