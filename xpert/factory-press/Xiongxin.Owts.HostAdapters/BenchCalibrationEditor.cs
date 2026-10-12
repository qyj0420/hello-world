using System;
using System.Globalization;
using System.IO;
using Xiongxin.Owts.Acquisition.Live;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 台架标定编辑：字段校验、JSON 读写（对齐 <see cref="LiveBenchCalibration"/> 与
    /// fixtures/bench-calibration.sample.json）。DesktopUi 对话框与 Smoke 共用。
    /// 拒绝把 VoltageGain=1000（危险占位）标成已标定；文案「未台架标定禁止真高压」。
    /// </summary>
    public static class BenchCalibrationEditor
    {
        public static string SafetyBanner
        {
            get { return StatusCopy.BenchGainNotCalibrated; }
        }

        public static string SafetyDetail
        {
            get { return StatusCopy.BenchGainNotCalibratedDetail; }
        }

        /// <summary>合法台架样本（Gain≠1000），对齐 fixtures/bench-calibration.sample.json 默认值。</summary>
        public static LiveBenchCalibration CreateSample()
        {
            return LiveBenchCalibration.CreateBenchSample();
        }


        /// <summary>从编辑字段构造草案（不写文件、不 Apply）。</summary>
        public static LiveBenchCalibration CreateDraftFromFields(
            double voltageGainVPerAoVolt,
            double dividerRatioVPerV,
            double currentScaleAPerVolt,
            double sourceVoltageScaleVPerV,
            bool benchCalibrated,
            string calibratedBy,
            string notes)
        {
            return new LiveBenchCalibration
            {
                Version = 1,
                BenchCalibrated = benchCalibrated,
                VoltageGainVPerAoVolt = voltageGainVPerAoVolt,
                DividerRatioVPerV = dividerRatioVPerV,
                CurrentScaleAPerVolt = currentScaleAPerVolt,
                SourceVoltageScaleVPerV = sourceVoltageScaleVPerV,
                CalibratedBy = calibratedBy ?? string.Empty,
                CalibratedUtc = benchCalibrated
                    ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    : string.Empty,
                Notes = notes ?? string.Empty
            };
        }

        /// <summary>
        /// 保存前校验。若 <see cref="LiveBenchCalibration.BenchCalibrated"/> 为 true：
        /// 拒绝 Gain=1000 / 无效分压/电流；须满足 <see cref="LiveBenchCalibration.AllowsLiveNonZeroEnergize"/>。
        /// 未标定时仍拒绝 NaN/Inf/非正增益等明显非法数。
        /// </summary>
        public static bool TryValidateBeforeSave(LiveBenchCalibration draft, out string error)
        {
            if (draft == null)
            {
                error = "台架标定对象为空。";
                return false;
            }

            if (double.IsNaN(draft.VoltageGainVPerAoVolt) || double.IsInfinity(draft.VoltageGainVPerAoVolt)
                || draft.VoltageGainVPerAoVolt <= 0)
            {
                error = SafetyBanner + "：VoltageGainVPerAoVolt 须为有限正数。";
                return false;
            }

            if (double.IsNaN(draft.DividerRatioVPerV) || double.IsInfinity(draft.DividerRatioVPerV)
                || draft.DividerRatioVPerV <= 0)
            {
                error = SafetyBanner + "：DividerRatioVPerV 须为有限正数。";
                return false;
            }

            if (double.IsNaN(draft.CurrentScaleAPerVolt) || double.IsInfinity(draft.CurrentScaleAPerVolt)
                || draft.CurrentScaleAPerVolt <= 0)
            {
                error = SafetyBanner + "：CurrentScaleAPerVolt 须为有限正数。";
                return false;
            }

            if (double.IsNaN(draft.SourceVoltageScaleVPerV) || double.IsInfinity(draft.SourceVoltageScaleVPerV)
                || draft.SourceVoltageScaleVPerV < 0)
            {
                error = SafetyBanner + "：SourceVoltageScaleVPerV 须为有限且 ≥0（0=未配）。";
                return false;
            }

            if (draft.BenchCalibrated)
            {
                if (LiveBenchCalibration.IsDangerousPlaceholderVoltageGain(draft.VoltageGainVPerAoVolt))
                {
                    error = SafetyBanner + "：拒绝把 VoltageGain=1000（危险占位）标成已标定。";
                    return false;
                }

                if (LiveBenchCalibration.IsDangerousPlaceholderDividerRatio(draft.DividerRatioVPerV))
                {
                    error = SafetyBanner + "：拒绝把 DividerRatio=1.0（危险占位）标成已标定。";
                    return false;
                }

                if (!draft.AllowsLiveNonZeroEnergize())
                {
                    error = SafetyBanner + "：增益/分压/电流比例仍无效，不可标成已标定。";
                    return false;
                }
            }

            error = null;
            return true;
        }

        public static bool TryParseJson(string json, out LiveBenchCalibration cal, out string error)
        {
            cal = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "台架标定 JSON 为空。";
                return false;
            }

            try
            {
                cal = LiveBenchCalibration.ParseJson(json);
            }
            catch (Exception ex)
            {
                error = "台架标定 JSON 解析失败：" + ex.Message;
                return false;
            }

            // 已标定声明的 JSON：增益占位视为非法（不可直接 Save/Apply）
            if (cal.BenchCalibrated && LiveBenchCalibration.IsDangerousPlaceholderVoltageGain(cal.VoltageGainVPerAoVolt))
            {
                error = SafetyBanner + "：JSON 声明已标定但 VoltageGain 仍为危险占位 1000。";
                return false;
            }

            if (cal.BenchCalibrated && !cal.AllowsLiveNonZeroEnergize())
            {
                error = SafetyBanner + "：JSON 声明已标定但增益/分压/电流仍无效。";
                return false;
            }

            return true;
        }

        public static bool TryLoad(string path, out LiveBenchCalibration cal, out string error)
        {
            cal = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "台架标定文件路径为空。";
                return false;
            }

            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex)
            {
                error = "台架标定路径无效：" + ex.Message;
                return false;
            }

            if (!File.Exists(full))
            {
                error = "台架标定文件不存在：" + full;
                return false;
            }

            string json;
            try { json = File.ReadAllText(full); }
            catch (Exception ex)
            {
                error = "读取台架标定失败：" + ex.Message;
                return false;
            }

            if (!TryParseJson(json, out cal, out error))
                return false;
            return true;
        }

        /// <summary>校验后写入 JSON（UTF-8 无 BOM）。合法已标定样本可写；Gain=1000+已标定拒写。</summary>
        public static bool TrySave(LiveBenchCalibration cal, string path, out string error)
        {
            error = null;
            if (!TryValidateBeforeSave(cal, out error))
                return false;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "台架标定文件路径为空。";
                return false;
            }

            try
            {
                cal.SaveFile(path);
            }
            catch (Exception ex)
            {
                error = "保存台架标定失败：" + ex.Message;
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>从 LiveHardwareOptions 抓取当前会话标定快照（可能未标定 / 占位）。</summary>
        public static LiveBenchCalibration CaptureFromHardware(LiveHardwareOptions hw)
        {
            if (hw == null)
                return new LiveBenchCalibration();
            return new LiveBenchCalibration
            {
                Version = 1,
                BenchCalibrated = hw.BenchCalibrationApplied && !LiveBenchCalibration.IsDangerousPlaceholderVoltageGain(hw.VoltageGainVPerAoVolt),
                VoltageGainVPerAoVolt = hw.VoltageGainVPerAoVolt,
                DividerRatioVPerV = hw.DividerRatioVPerV,
                CurrentScaleAPerVolt = hw.CurrentScaleAPerVolt,
                SourceVoltageScaleVPerV = hw.SourceVoltageScaleVPerV,
                CalibratedBy = string.Empty,
                CalibratedUtc = string.Empty,
                Notes = hw.BenchCalibrationSource ?? string.Empty
            };
        }
    }
}
