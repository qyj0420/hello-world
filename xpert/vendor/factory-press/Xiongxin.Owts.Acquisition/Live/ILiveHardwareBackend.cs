using System;
using System.Threading;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>实机后端：真 Art/Pico 或 FakeLive。</summary>
    public interface ILiveHardwareBackend : IDisposable
    {
        bool IsOpen { get; }
        double LastAoVolts { get; }
        bool HvOn { get; }
        bool HvIndicatorOn { get; }
        bool ConnectionLampHigh { get; }
        bool PicoOpen { get; }

        string ProbeDrivers();
        void Open(string artDeviceName, string picoSerial);
        void Close();

        void WriteAoVolts(double volts);
        LiveFeedbackSample ReadFeedback();
        /// <summary>HVON 后同步确保连续 AI 监测任务已建立（幂等；已运行则直接返回）。</summary>
        void EnsureMonitorAiTask();
        void WriteDigitalOutputs(bool hvOn, bool hvIndicator, bool connectionLamp, bool groundTrigger);
        bool ReadEStopPressed();
        /// <summary>读 P0.4 急停采样（含 raw DI），供现场对表日志。</summary>
        LiveEStopSample ReadEStopSample();

        /// <summary>布防 AI4 有限采集（80 kS/s，PFI 触发）；与 Pico Arm 对齐后由 P0.7 同时触发。</summary>
        void ArmFiniteDividerCapture();
        double[] ReadFiniteDividerCapture(int timeoutMilliseconds);
        void AbortFiniteDividerCapture();

        /// <summary>
        /// 达压后、HVOFF 前：仅配置 Pico 触发（0V→自动；非0→数字 EXT）。
        /// 禁止 OpenUnit / 禁止等冷却 / 禁止 RunBlock / 禁止动 AI4 监测。
        /// </summary>
        void ConfigureBlockTriggerOnly(bool autoTriggerOnly);

        /// <summary>
        /// 布防 Pico 块采集（不 OpenUnit；须连接时已 Open）。
        /// autoTriggerOnly=true：0V/噪声 → 仅自动触发；false：非0 → 数字 EXT 触发（auto 仅兜底）。
        /// skipCooldownGate=true：关源后布防用——冷却不足立即布防（绝不干等）；冷却只在采集完成后 Wait。
        /// </summary>
        void ArmBlockCapture(int rangeIndex, Action onArmed, bool autoTriggerOnly, bool skipCooldownGate = false);
        PicoBlockCaptureResult ReadBlockCapture(int timeoutMilliseconds, CancellationToken cancellation);
        void AbortBlockCapture();

        /// <summary>
        /// 校准采集：Rapid Block Bulk Overlapped（通道 A 脉冲触发）。
        /// 与加压 EXT 单段 <see cref="ArmBlockCapture"/> 分离；onArmed 在 RunBlock 后调用。
        /// </summary>
        void ArmRapidBlockCapture(RapidBlockArmRequest request, Action onArmed);
        PicoRapidBlockCaptureResult ReadRapidBlockCaptures(int timeoutMilliseconds, CancellationToken cancellation);
        void AbortRapidBlockCapture();

        /// <summary>仅关闭 Pico（Art 会话保持）；供 Pico 掉线恢复。</summary>
        void ClosePicoOnly();

        /// <summary>在会话已 Open 时重新 OpenUnit Pico；成功 true。</summary>
        bool TryReopenPico(string picoSerial, out string error);
    }

    /// <summary>
    /// P0.4 急停 DI 采样。极性约定：SafetyReadyActiveHigh=true 时
    /// 物理高=急停解除（安全就绪），物理低=急停按下（有效低）。
    /// 超时/读失败 → ReadOk=false（未知）：不冒充有效低，也不能用历史高放行。
    /// </summary>
    public sealed class LiveEStopSample
    {
        /// <summary>ReadDigitalLines 原始字节（0/非0）。</summary>
        public byte RawDi { get; set; }
        public bool PhysicalHigh { get; set; }
        /// <summary>仅 ReadOk 时有效：true=有效低按下；超时不得置 true 冒充低。</summary>
        public bool Pressed { get; set; }
        /// <summary>与 LiveHardwareOptions.SafetyReadyActiveHigh 一致。</summary>
        public bool SafetyReadyActiveHigh { get; set; }
        public bool ReadOk { get; set; }
        public string Error { get; set; }

        public LiveEStopSample()
        {
            ReadOk = true;
            Error = string.Empty;
        }

        /// <summary>超时/读失败 → 未知（≠ 有效低）。</summary>
        public bool IsUnknown { get { return !ReadOk; } }

        /// <summary>未知或有效低均阻断加压/放行（不沿用历史高）。</summary>
        public bool BlocksEnergize { get { return !ReadOk || Pressed; } }

        public string JudgmentLabel
        {
            get
            {
                if (!ReadOk) return "未知(超时/读失败)";
                return Pressed ? "急停按下(有效低)" : "急停解除(有效高)";
            }
        }

        /// <summary>现场对表一行：raw / 电平 / 判定 / 极性。</summary>
        public string FormatLog()
        {
            string polarity = SafetyReadyActiveHigh
                ? "极性=高=解除/低=按下(有效低)"
                : "极性=低=解除/高=按下(有效高)";
            if (!ReadOk)
                return "P0.4 raw=? physical=? 判定=未知 err=" + (Error ?? "") + " " + polarity;
            return "P0.4 rawDi=" + RawDi
                + " physical=" + (PhysicalHigh ? "高" : "低")
                + " 判定=" + JudgmentLabel
                + " " + polarity;
        }
    }

    public sealed class LiveFeedbackSample
    {
        /// <summary>AI0：原始或已换算电流相关值（真机为 rawV；Fake 可为工程 A）。</summary>
        public double CurrentA { get; set; }
        /// <summary>AI2 源 VMON：真机为原始伏特；Fake 默认工程值，SourceFeedbackIsRaw 时为原始伏特。</summary>
        public double SourceVoltageV { get; set; }
        /// <summary>可选：显式 AI2 原始伏特；NaN=未单独提供，用 SourceVoltageV。</summary>
        public double SourceVoltageRawV { get; set; }
        public double DividerVoltageV { get; set; }
        public double DividerRawV { get; set; }
        /// <summary>本窗/本批样本数（新鲜窗策略）。</summary>
        public int SampleCount { get; set; }
        /// <summary>已凑满的控制窗口点数；新鲜窗要求 == WindowSamples。</summary>
        public int WindowSampleCount { get; set; }
        public int BacklogBefore { get; set; }
        public int BacklogAfter { get; set; }
        public int ReadBatchCount { get; set; }
        public long ReadElapsedMs { get; set; }
        public bool FreshWindowAfterAoCommand { get; set; }
        public int FreshWindowBoundaryQueued { get; set; }
        public int FreshWindowSamplesAfterBoundary { get; set; }
        public double PeakCurrentAbs { get; set; }
        public double PeakSourceAbs { get; set; }
        public double PeakDividerAbs { get; set; }
        public LiveFeedbackSample()
        {
            SourceVoltageRawV = double.NaN;
        }
    }

    public sealed class PicoBlockCaptureResult
    {
        public short[] RawAdc { get; set; }
        public double SampleIntervalNanoseconds { get; set; }
        public double SampleRateHz { get; set; }
        public double FullScaleVolts { get; set; }
        public short MaximumAdc { get; set; }
        public int RangeIndex { get; set; }
        public bool HardwareOverflow { get; set; }
        public string Serial { get; set; }

        public PicoBlockCaptureResult()
        {
            RawAdc = new short[0];
            Serial = string.Empty;
            MaximumAdc = 32512;
        }
    }
}
