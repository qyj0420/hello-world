using System;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.Owts.Contracts.Acquisition
{
    /// <summary>
    /// 采集/通信会话。← Art USB3135 + PicoScope 5242D。
    /// 进程内 Pico+Art 只允许 Open 一次（校准与加压共用）；二次 Open 必须失败。
    /// </summary>
    public interface IAcquisitionSession : IDisposable
    {
        HardwareIdentityProbeResult Probe(HardwareProbeRequest request);
        event EventHandler<HardwareStatusChangedEventArgs> StatusChanged;

        /// <summary>当前是否已持有全局唯一 Open 会话。</summary>
        bool IsOpen { get; }

        void Open(AcquisitionSessionOptions options);
        void Close();
        AcquisitionStatusSnapshot GetStatus();

        Task<AcquisitionCycleResult> RunCycleAsync(AcquisitionCycleRequest request, CancellationToken cancellation);

        void EmergencyStop();

        /// <summary>
        /// UI「停止」：取消循环意图并关源，不置硬件急停锁存。
        /// 真 P0.4 边沿仍由监视锁存；下一批次可在硬件解除后继续。
        /// </summary>
        void RequestOperatorAbort();

        bool ReadSafetyReady();

        /// <summary>设置急停态（仅仿真/FakeLive）；实机抛 NotSupported，须读 P0.4。</summary>
        void SetEStopPressed(bool pressed);

        /// <summary>标记当前相已完成校准（门禁用，≠ 可加压）。</summary>
        void SetCalibrated(bool calibrated);

        /// <summary>
        /// 阻抗板带宽（P1.2）：随时可调，校准采集中不锁死。
        /// wide=true 宽频；false 窄/默认。仿真存状态；实机写 DO 若已接线。
        /// </summary>
        void SetImpedanceBandwidthWide(bool wide);

        /// <summary>当前阻抗板带宽是否宽档。</summary>
        bool ImpedanceBandwidthWide { get; }

        /// <summary>
        /// ART P0.5 连接状态指示灯：连通后置高并维持，断开置低。
        /// 仿真实现打日志，不碰真硬件。
        /// </summary>
        void SetConnectionIndicationLamp(bool high);

        bool ConnectionIndicationLampHigh { get; }

        /// <summary>残压未确认时的接地保持标志。</summary>
        bool HoldGroundActive { get; }

        /// <summary>
        /// 解除接地双确认：仅当 HoldGroundActive 且 operatorAcknowledged=true 时释放 P0.7。
        /// 返回 true=已解除；false=拒绝（未确认或未处于保持）。
        /// </summary>
        bool ConfirmReleaseGround(bool operatorAcknowledged);
    }
}
