using System;
using System.Globalization;
using System.Text;

namespace Xiongxin.Owts.Contracts.Ui
{
    /// <summary>
    /// 五态状态板：已连接 / 校准态(未校准|校准中|已校准) / 可加压 / 急停按下|解除。
    /// 「可加压」仅当：已连接 ∧ 已校准 ∧ 急停解除 ∧ 目标电压&gt;0（0 kV 不算）。
    /// 校准=Pico 采集中；校准中不强制锁死急停，阻抗可随时调。
    /// </summary>
    public sealed class OperationalStatusBoard
    {
        public bool Connected { get; set; }
        public bool Calibrated { get; set; }
        /// <summary>Pico 正在做校准采集（CycleRunning + 校准轮）。</summary>
        public bool Calibrating { get; set; }
        /// <summary>true = 急停按下；false = 急停解除。校准中亦可切换。</summary>
        public bool EStopPressed { get; set; }
        /// <summary>软件急停锁存（真 P0.4 边沿或 EmergencyStop）；OperatorAbort 不置位。</summary>
        public bool EmergencyLatched { get; set; }
        /// <summary>计划非零加压目标（kV）。0 或 NaN 时不可宣称可加压。</summary>
        public double TargetVoltageKv { get; set; }
        /// <summary>阻抗板带宽宽档（P1.2）；随时可调，校准不锁。</summary>
        public bool ImpedanceBandwidthWide { get; set; }

        public bool EStopReleased { get { return !EStopPressed; } }

        public bool ReadyToEnergize
        {
            get
            {
                return Connected
                    && Calibrated
                    && !Calibrating
                    && EStopReleased
                    && !EmergencyLatched
                    && TargetVoltageKv > 0
                    && !double.IsNaN(TargetVoltageKv)
                    && !double.IsInfinity(TargetVoltageKv);
            }
        }

        public string ConnectedLabel { get { return Connected ? StatusCopy.Connected : StatusCopy.DeviceNotConnected; } }
        public string CalibratedLabel
        {
            get
            {
                if (Calibrating) return StatusCopy.Calibrating;
                return Calibrated ? StatusCopy.Calibrated : StatusCopy.NotCalibrated;
            }
        }
        public string ReadyLabel { get { return ReadyToEnergize ? StatusCopy.ReadyToEnergize : "不可加压"; } }
        public string EStopLabel
        {
            get
            {
                string baseLabel = EStopPressed ? StatusCopy.EStopPressed : StatusCopy.EStopReleased;
                if (EmergencyLatched)
                    return baseLabel + "·" + StatusCopy.SoftwareEmergencyLatch;
                return baseLabel;
            }
        }

        /// <summary>
        /// 进入校准轮：须已连接且急停按下。
        /// 急停解除（高电平）→ 禁止校准（勿 silent 开干）；流程自适量程→最佳档→脉冲触发。
        /// </summary>
        public bool CanEnterCalibration(out string reason)
        {
            if (!Connected)
            {
                reason = StatusCopy.DeviceNotConnected;
                return false;
            }
            if (Calibrating)
            {
                reason = "已在「" + StatusCopy.Calibrating + "」（Pico 采集中）；请等待本轮结束。";
                return false;
            }
            // 急停解除 → 禁校准；须按下才可进（与非零加压相反）
            if (!EStopPressed)
            {
                reason = StatusCopy.CalibNeedsEStopPressed
                    + "（当前「" + StatusCopy.EStopReleased + "」）";
                return false;
            }
            reason = StatusCopy.CalibNeedsConnected;
            return true;
        }

        public bool CanEnterNonZeroPressure(out string reason)
        {
            if (!Connected)
            {
                reason = StatusCopy.DeviceNotConnected;
                return false;
            }
            if (Calibrating)
            {
                reason = "「" + StatusCopy.Calibrating + "」期间不可非零加压；急停仍可切换。";
                return false;
            }
            if (!Calibrated)
            {
                reason = "尚未「" + StatusCopy.Calibrated + "」；" + StatusCopy.ConnectedNotEnergize;
                return false;
            }
            if (EStopPressed)
            {
                reason = "非零加压需要「" + StatusCopy.EStopReleased + "」；当前为「" + StatusCopy.EStopPressed + "」";
                return false;
            }
            if (EmergencyLatched)
            {
                reason = "非零加压需要清除「" + StatusCopy.SoftwareEmergencyLatch
                    + "」（硬件须为「" + StatusCopy.EStopReleased + "」且源已关）";
                return false;
            }
            if (!(TargetVoltageKv > 0))
            {
                reason = StatusCopy.ZeroKvNoiseOnly;
                return false;
            }
            reason = StatusCopy.ReadyToEnergize;
            return true;
        }

        public string FormatFiveStates()
        {
            var sb = new StringBuilder();
            sb.Append("状态五态: ");
            sb.Append(Connected ? StatusCopy.Connected : "未连接");
            sb.Append(" | ");
            sb.Append(CalibratedLabel);
            sb.Append(" | ");
            sb.Append(ReadyToEnergize ? StatusCopy.ReadyToEnergize : "不可加压");
            sb.Append(" | ");
            sb.Append(EStopLabel);
            if (EmergencyLatched && !EStopPressed)
                sb.Append(" | ").Append(StatusCopy.SoftwareEmergencyLatch);
            if (!(TargetVoltageKv > 0))
                sb.Append(" | ").Append(StatusCopy.ZeroKvNoiseOnly);
            return sb.ToString();
        }

        public string FormatDetail()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}; 目标={1} kV; 阻抗宽频={2}; {3}",
                FormatFiveStates(),
                TargetVoltageKv.ToString("0.###", CultureInfo.InvariantCulture),
                ImpedanceBandwidthWide ? "开" : "关",
                Connected ? StatusCopy.ConnectedNotEnergize : StatusCopy.DeviceNotConnected);
        }
    }
}
