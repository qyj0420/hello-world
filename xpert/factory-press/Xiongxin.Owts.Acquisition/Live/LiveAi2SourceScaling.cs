using System;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// AI2（直流源 VMON）原始电压 → 工程电压（V；展示可用 kV=V/1000）。
    /// 比例来自 <see cref="LiveHardwareOptions.SourceVoltageScaleVPerV"/> /
    /// <see cref="LiveBenchCalibration.SourceVoltageScaleVPerV"/>。
    /// 未标定（≤0）时 Status 必须明确；残压监测可回退 VoltageGain，但不得称为已标定。
    /// </summary>
    public static class LiveAi2SourceScaling
    {
        /// <summary>SourceVoltageScaleVPerV &gt; 0 且有限。</summary>
        public static bool IsCalibrated(double sourceVoltageScaleVPerV)
        {
            return sourceVoltageScaleVPerV > 0
                && !double.IsNaN(sourceVoltageScaleVPerV)
                && !double.IsInfinity(sourceVoltageScaleVPerV);
        }

        public static bool IsCalibrated(LiveHardwareOptions hw)
        {
            return hw != null && IsCalibrated(hw.SourceVoltageScaleVPerV);
        }

        /// <summary>
        /// rawAi2V（ART AI2 原始伏特）× scale → 工程伏特。
        /// 未标定：用 fallbackVoltageGainVPerAoVolt（通常 VoltageGain）作残压参考，Status=Fallback/NotCalibrated。
        /// </summary>
        public static double ToEngineeringVolts(
            double rawAi2V,
            double sourceVoltageScaleVPerV,
            double fallbackVoltageGainVPerAoVolt,
            out string statusMessage)
        {
            if (double.IsNaN(rawAi2V) || double.IsInfinity(rawAi2V))
            {
                statusMessage = StatusCopy.Ai2SourceScaleNotCalibrated + "（原始 AI2 无效）";
                return double.NaN;
            }

            if (IsCalibrated(sourceVoltageScaleVPerV))
            {
                statusMessage = StatusCopy.Ai2SourceScaleApplied;
                return rawAi2V * sourceVoltageScaleVPerV;
            }

            if (fallbackVoltageGainVPerAoVolt > 0
                && !double.IsNaN(fallbackVoltageGainVPerAoVolt)
                && !double.IsInfinity(fallbackVoltageGainVPerAoVolt))
            {
                statusMessage = StatusCopy.Ai2SourceScaleFallbackGain;
                return rawAi2V * fallbackVoltageGainVPerAoVolt;
            }

            statusMessage = StatusCopy.Ai2SourceScaleNotCalibrated;
            return rawAi2V;
        }

        public static double ToEngineeringVolts(LiveHardwareOptions hw, double rawAi2V, out string statusMessage)
        {
            if (hw == null)
            {
                statusMessage = StatusCopy.Ai2SourceScaleNotCalibrated;
                return rawAi2V;
            }
            return ToEngineeringVolts(rawAi2V, hw.SourceVoltageScaleVPerV, hw.VoltageGainVPerAoVolt, out statusMessage);
        }

        /// <summary>工程伏特 → kV（展示用）。</summary>
        public static double EngineeringVoltsToKv(double engineeringVolts)
        {
            if (double.IsNaN(engineeringVolts) || double.IsInfinity(engineeringVolts)) return engineeringVolts;
            return engineeringVolts / 1000.0;
        }

        public static string DescribeScale(LiveHardwareOptions hw)
        {
            if (hw == null) return StatusCopy.Ai2SourceScaleNotCalibrated;
            if (IsCalibrated(hw))
                return StatusCopy.Ai2SourceScaleApplied
                    + " scale=" + hw.SourceVoltageScaleVPerV.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " V/V";
            return StatusCopy.Ai2SourceScaleNotCalibrated;
        }
    }
}
