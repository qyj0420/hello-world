using System;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.Acquisition
{
    /// <summary>对照 Task38Contracts.Task6RangeAdjustmentDirection。</summary>
    public enum Task6RangeAdjustmentDirection
    {
        Suitable = -1,
        CurrentRangeTooSmallIncreaseRange = 0,
        CurrentRangeTooLargeDecreaseRange = 1
    }

    public sealed class Task6RangeAdvice
    {
        public bool Suitable { get; set; }
        public int SuggestedRangeIndex { get; set; }
        public Task6RangeAdjustmentDirection Direction { get; set; }
        public string Message { get; set; }
        public int PeakAbsAdc { get; set; }
        public bool ClippedOrNearFullScale { get; set; }
        public bool UnderRange { get; set; }

        /// <summary>是否采纳了正式 Task6 DLL 首零相位反馈。</summary>
        public bool UsedDllEvaluation { get; set; }
        public string EvaluatorStatusCode { get; set; }
        /// <summary>无效时 NaN；禁止伪造。</summary>
        public double FirstZeroTimeSeconds { get; set; }

        public Task6RangeAdvice()
        {
            SuggestedRangeIndex = -1;
            Direction = Task6RangeAdjustmentDirection.Suitable;
            Message = string.Empty;
            EvaluatorStatusCode = string.Empty;
            FirstZeroTimeSeconds = double.NaN;
        }
    }

    /// <summary>
    /// Task6 量程决策：默认峰值 ADC 启发式（削顶升档/过小降档）。
    /// 若 <see cref="IFirstZeroPhaseEvaluator"/> 给出有效首零相位结果，则优先按 DLL 反馈调档（对照 Task38Controller.ApplyTask6RangeFeedback）。
    /// 缺 DLL / 无有效首零 → 保持启发式；不伪造相位。
    /// </summary>
    public static class Task6RangeAdvisor
    {
        /// <summary>峰≥此比例满量程 → 升档（一次算到合适索引，非盲跳 +1）。</summary>
        public const double NearFullScaleFraction = 0.80;
        /// <summary>峰&lt;此比例满量程 → 降档。</summary>
        public const double UnderRangeFraction = 0.10;
        /// <summary>降档后目标峰约占满量程的倒数（1/1.25≈80%→偏紧）；升档用 TargetUtilization。</summary>
        public const double DecreaseHeadroom = 1.25;
        /// <summary>调档后目标峰/满量程比（约 50%）。</summary>
        public const double TargetUtilization = 0.50;
        /// <summary>硬饱和/溢出时，按当前满量程至少放大此倍数再选档。</summary>
        public const double SaturatedJumpFactor = 4.0; // 硬饱和真峰未知：一次至少跳约 4×FS（±50mV→±200mV）
        /// <summary>QualityFlags bit：无有效振荡（对照 Task38Controller）。</summary>
        public const int QualityFlagNoValidOscillation = 8192;

        public static readonly double[] Pico5242DFullScaleVolts =
        {
            0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0
        };

        public static Task6RangeAdvice Advise(int currentRangeIndex, short[] adc, short maximumAdc, bool hardwareOverflow)
        {
            return Advise(currentRangeIndex, adc, maximumAdc, hardwareOverflow, Pico5242DFullScaleVolts, null);
        }

        public static Task6RangeAdvice Advise(int currentRangeIndex, short[] adc, short maximumAdc, bool hardwareOverflow, double[] fullScaleVolts)
        {
            return Advise(currentRangeIndex, adc, maximumAdc, hardwareOverflow, fullScaleVolts, null);
        }

        /// <summary>
        /// 有 DLL 有效评估时参与量程建议；否则启发式。
        /// </summary>
        public static Task6RangeAdvice Advise(
            int currentRangeIndex,
            short[] adc,
            short maximumAdc,
            bool hardwareOverflow,
            double[] fullScaleVolts,
            FirstZeroPhaseEvaluationResult dllEval)
        {
            Task6RangeAdvice heuristic = AdviseHeuristic(currentRangeIndex, adc, maximumAdc, hardwareOverflow, fullScaleVolts);
            if (dllEval == null || !dllEval.UsedDll)
            {
                if (dllEval != null)
                {
                    heuristic.EvaluatorStatusCode = dllEval.StatusCode ?? string.Empty;
                    if (!string.IsNullOrEmpty(dllEval.Diagnostic))
                        heuristic.Message = heuristic.Message + " [" + dllEval.StatusCode + ": " + dllEval.Diagnostic + "]";
                }
                heuristic.UsedDllEvaluation = false;
                heuristic.FirstZeroTimeSeconds = double.NaN;
                return heuristic;
            }

            // DLL 已调用但无有效首零/失败 → 不采用其量程字段，回退启发式；相位保持 NaN。
            bool noValidOscillation = (dllEval.QualityFlags & QualityFlagNoValidOscillation) != 0;
            bool finiteFirstZero = !double.IsNaN(dllEval.FirstZeroTimeSeconds)
                && !double.IsInfinity(dllEval.FirstZeroTimeSeconds)
                && dllEval.FirstZeroTimeSeconds >= 0.0;
            if (!dllEval.Task6Success || noValidOscillation || !finiteFirstZero || !dllEval.Success)
            {
                heuristic.UsedDllEvaluation = false;
                heuristic.EvaluatorStatusCode = dllEval.StatusCode ?? FirstZeroPhaseEvaluationResult.StatusEvaluationFailed;
                heuristic.FirstZeroTimeSeconds = double.NaN;
                heuristic.Message = heuristic.Message + " [DLL无有效首零相位，回退启发式；"
                    + (dllEval.Diagnostic ?? string.Empty) + "]";
                return heuristic;
            }

            return AdviseFromDll(currentRangeIndex, dllEval, fullScaleVolts ?? Pico5242DFullScaleVolts, heuristic.PeakAbsAdc);
        }

        /// <summary>
        /// 从采集结果构建请求 → 评估 → 与启发式合并。
        /// </summary>
        public static Task6RangeAdvice AdviseWithEvaluator(
            IFirstZeroPhaseEvaluator evaluator,
            int currentRangeIndex,
            short[] adc,
            short maximumAdc,
            bool hardwareOverflow,
            double fullScaleVolts,
            double picoSampleRateHz,
            double[] usbDividerVolts,
            double usbSampleRateHz,
            bool isBackgroundCapture,
            double targetVoltageV,
            double dividerRatioVPerV,
            double[] rangeTable = null)
        {
            double[] table = rangeTable ?? Pico5242DFullScaleVolts;
            FirstZeroPhaseEvaluationResult eval = null;
            if (evaluator != null && !isBackgroundCapture)
            {
                var req = BuildEvaluationRequest(
                    currentRangeIndex, adc, maximumAdc, fullScaleVolts, picoSampleRateHz,
                    usbDividerVolts, usbSampleRateHz, targetVoltageV, dividerRatioVPerV, table);
                req.IsBackgroundCapture = false;
                try { eval = evaluator.Evaluate(req); }
                catch (Exception ex)
                {
                    eval = new FirstZeroPhaseEvaluationResult
                    {
                        Success = false,
                        UsedDll = false,
                        StatusCode = FirstZeroPhaseEvaluationResult.StatusEvaluationFailed,
                        Diagnostic = "Evaluator threw: " + ex.GetType().Name + ": " + ex.Message,
                        FirstZeroTimeSeconds = double.NaN
                    };
                }
            }
            else if (isBackgroundCapture)
            {
                eval = new FirstZeroPhaseEvaluationResult
                {
                    Success = false,
                    UsedDll = false,
                    StatusCode = FirstZeroPhaseEvaluationResult.StatusBackgroundSkipped,
                    Diagnostic = "Background capture skipped.",
                    FirstZeroTimeSeconds = double.NaN
                };
            }

            Task6RangeAdvice merged = Advise(currentRangeIndex, adc, maximumAdc, hardwareOverflow, table, eval);
            // 无 DLL 有效首零时，用采样率重跑「首零+一周期」启发式覆盖全窗峰值
            if (merged != null && !merged.UsedDllEvaluation && picoSampleRateHz > 0)
            {
                Task6RangeAdvice period = AdviseHeuristic(currentRangeIndex, adc, maximumAdc, hardwareOverflow, table, picoSampleRateHz);
                if (period != null)
                {
                    period.EvaluatorStatusCode = merged.EvaluatorStatusCode;
                    return period;
                }
            }
            return merged;
        }

        public static FirstZeroPhaseEvaluationRequest BuildEvaluationRequest(
            int currentRangeIndex,
            short[] adc,
            short maximumAdc,
            double fullScaleVolts,
            double picoSampleRateHz,
            double[] usbDividerVolts,
            double usbSampleRateHz,
            double targetVoltageV,
            double dividerRatioVPerV,
            double[] rangeTable)
        {
            double[] pdVolts = new double[0];
            if (adc != null && adc.Length > 0 && maximumAdc > 0 && fullScaleVolts > 0)
            {
                pdVolts = new double[adc.Length];
                double scale = fullScaleVolts / maximumAdc;
                for (int i = 0; i < adc.Length; i++) pdVolts[i] = adc[i] * scale;
            }

            return new FirstZeroPhaseEvaluationRequest
            {
                VoltageRawVolts = usbDividerVolts == null ? new double[0] : (double[])usbDividerVolts.Clone(),
                VoltageSampleRateHz = usbSampleRateHz,
                PdRawVolts = pdVolts,
                PdSampleRateHz = picoSampleRateHz,
                PdRangeValuesVolts = rangeTable ?? Pico5242DFullScaleVolts,
                CurrentPdRangeIndex = currentRangeIndex,
                DividerRatioVPerV = dividerRatioVPerV > 0 ? dividerRatioVPerV : 11000.0,
                AcDividerRatioVPerV = dividerRatioVPerV > 0 ? dividerRatioVPerV : 11000.0,
                TargetVoltageV = targetVoltageV,
                VoltageCalibration = 1.0,
                PdCalibrationPcPerVolt = 1.0,
                InductanceH = 1.0,
                InductorDcrOhm = 0.0
            };
        }

        static Task6RangeAdvice AdviseFromDll(
            int currentRangeIndex,
            FirstZeroPhaseEvaluationResult dll,
            double[] fullScaleVolts,
            int peakAbsAdc)
        {
            var advice = new Task6RangeAdvice
            {
                PeakAbsAdc = peakAbsAdc,
                UsedDllEvaluation = true,
                EvaluatorStatusCode = dll.StatusCode ?? FirstZeroPhaseEvaluationResult.StatusEvaluationOk,
                FirstZeroTimeSeconds = dll.FirstZeroTimeSeconds,
                SuggestedRangeIndex = -1,
                Suitable = false,
                Direction = Task6RangeAdjustmentDirection.Suitable
            };

            bool clippedAfterFirstZero = dll.PdOverRange || dll.MultipleClippedPulses;
            int effectiveDirection = clippedAfterFirstZero
                ? (int)Task6RangeAdjustmentDirection.CurrentRangeTooSmallIncreaseRange
                : dll.RangeAdjustmentDirection;

            if (dll.RangeSuitable && !clippedAfterFirstZero
                && effectiveDirection == (int)Task6RangeAdjustmentDirection.Suitable)
            {
                advice.Suitable = true;
                advice.Direction = Task6RangeAdjustmentDirection.Suitable;
                advice.SuggestedRangeIndex = -1;
                advice.Message = "任务6判定首零点后局放波形量程合适（DLL）。firstZero_s="
                    + dll.FirstZeroTimeSeconds.ToString("G6");
                return advice;
            }

            if (dll.BestRangeIndex < 0 || dll.BestRangeIndex >= fullScaleVolts.Length)
            {
                advice.Suitable = false;
                advice.SuggestedRangeIndex = -1;
                advice.Message = "任务6返回的推荐量程索引无效；保持当前量程并人工检查。";
                return advice;
            }

            bool increaseOk = effectiveDirection == (int)Task6RangeAdjustmentDirection.CurrentRangeTooSmallIncreaseRange
                && dll.BestRangeIndex > currentRangeIndex;
            bool decreaseOk = effectiveDirection == (int)Task6RangeAdjustmentDirection.CurrentRangeTooLargeDecreaseRange
                && dll.BestRangeIndex < currentRangeIndex;

            if (!increaseOk && !decreaseOk)
            {
                advice.Suitable = false;
                advice.SuggestedRangeIndex = -1;
                advice.Message = clippedAfterFirstZero
                    ? "任务6报告首零点后存在过量程/多脉冲削顶，但推荐量程未向更大量程调整；保持当前量程并人工检查。"
                    : "任务6推荐量程与调整方向不一致；保持当前量程并人工检查。";
                return advice;
            }

            advice.Suitable = false;
            advice.SuggestedRangeIndex = dll.BestRangeIndex;
            advice.Direction = increaseOk
                ? Task6RangeAdjustmentDirection.CurrentRangeTooSmallIncreaseRange
                : Task6RangeAdjustmentDirection.CurrentRangeTooLargeDecreaseRange;
            if (increaseOk)
            {
                advice.ClippedOrNearFullScale = true;
                advice.Message = clippedAfterFirstZero
                    ? ("PicoScope量程过小，任务6在首零点后检测到过量程或多脉冲削顶；建议调大到索引 "
                        + dll.BestRangeIndex + "（±" + fullScaleVolts[dll.BestRangeIndex] + " V）。")
                    : ("PicoScope量程过小，首零点后有效数据存在近满量程/削顶风险；建议调大到索引 "
                        + dll.BestRangeIndex + "（±" + fullScaleVolts[dll.BestRangeIndex] + " V）。");
            }
            else
            {
                advice.UnderRange = true;
                advice.Message = "PicoScope量程过大，首零点后有效信号利用率偏低；建议调小到索引 "
                    + dll.BestRangeIndex + "（±" + fullScaleVolts[dll.BestRangeIndex] + " V）。";
            }
            return advice;
        }

        static Task6RangeAdvice AdviseHeuristic(int currentRangeIndex, short[] adc, short maximumAdc, bool hardwareOverflow, double[] fullScaleVolts)
        {
            return AdviseHeuristic(currentRangeIndex, adc, maximumAdc, hardwareOverflow, fullScaleVolts, 0);
        }

        /// <summary>
        /// 振荡导通后：以低压侧第一个过零点起一个周期的 Pico 波形峰值为量程参考；
        /// 超量程→升档并重加本轮；量程过大→降档并重加本轮；合适→下一轮。
        /// </summary>
        static Task6RangeAdvice AdviseHeuristic(int currentRangeIndex, short[] adc, short maximumAdc, bool hardwareOverflow, double[] fullScaleVolts, double picoSampleRateHz)
        {
            var advice = new Task6RangeAdvice { SuggestedRangeIndex = -1, Direction = Task6RangeAdjustmentDirection.Suitable, Suitable = true };
            if (fullScaleVolts == null || fullScaleVolts.Length == 0)
            {
                advice.Suitable = false;
                advice.Message = "量程表为空。";
                return advice;
            }
            if (currentRangeIndex < 0 || currentRangeIndex >= fullScaleVolts.Length)
            {
                advice.Suitable = false;
                advice.Message = "当前量程索引无效。";
                return advice;
            }
            if (maximumAdc <= 0)
            {
                advice.Suitable = false;
                advice.Message = "MaximumAdc 无效。";
                return advice;
            }

            int peak;
            int z0, z1;
            bool usedPeriod = TryPeakAbsAdcFirstZeroOnePeriod(adc, picoSampleRateHz, out peak, out z0, out z1);
            if (!usedPeriod)
            {
                peak = 0;
                if (adc != null)
                    for (int i = 0; i < adc.Length; i++)
                    {
                        int a = Math.Abs((int)adc[i]);
                        if (a > peak) peak = a;
                    }
            }
            advice.PeakAbsAdc = peak;
            if (usedPeriod && z0 >= 0)
                advice.FirstZeroTimeSeconds = picoSampleRateHz > 0 ? z0 / picoSampleRateHz : double.NaN;

            double fsNow = fullScaleVolts[currentRangeIndex];
            double peakVolts = (peak / (double)maximumAdc) * fsNow;
            double util = maximumAdc > 0 ? peak / (double)maximumAdc : 0;
            bool hardSat = hardwareOverflow || peak >= (int)(0.98 * maximumAdc);
            bool clipped = hardSat || peak >= (int)(NearFullScaleFraction * maximumAdc);
            advice.ClippedOrNearFullScale = clipped;

            if (clipped)
            {
                advice.Suitable = false;
                advice.Direction = Task6RangeAdjustmentDirection.CurrentRangeTooSmallIncreaseRange;
                // 一次算到合适索引：目标峰≈TargetUtilization·FS；硬饱和时按 ≥SaturatedJumpFactor×当前FS
                double needFs = hardSat
                    ? Math.Max(fsNow * SaturatedJumpFactor, peakVolts / TargetUtilization)
                    : (peakVolts / TargetUtilization);
                int next = FindSmallestRangeAtLeast(needFs, fullScaleVolts);
                if (next <= currentRangeIndex)
                    next = Math.Min(currentRangeIndex + 1, fullScaleVolts.Length - 1);
                advice.SuggestedRangeIndex = next;
                advice.Message = next > currentRangeIndex
                    ? ("量程过小（" + (hardSat ? "溢出/硬饱和" : ("峰/FS=" + (util * 100).ToString("0.0") + "%≥" + (int)(NearFullScaleFraction * 100) + "%"))
                        + "）；一次升到 ±" + fullScaleVolts[next] + " V（索引 " + next + "）后重加本轮。")
                    : ("已在最大量程仍溢出/削顶；保持索引 " + currentRangeIndex + " 并人工检查。");
                return advice;
            }

            bool under = peak < (int)(UnderRangeFraction * maximumAdc);
            advice.UnderRange = under;
            if (under && currentRangeIndex > 0)
            {
                // 须首零后一周期统计才允许降档；禁止无首零/全窗小峰盲目掉到 ±10mV 再饱和重加
                if (!usedPeriod || !(picoSampleRateHz > 0))
                {
                    advice.Suitable = true;
                    advice.SuggestedRangeIndex = -1;
                    advice.Direction = Task6RangeAdjustmentDirection.Suitable;
                    advice.Message = "峰偏低但无有效首零周期统计，禁止盲目降档；保持索引 "
                        + currentRangeIndex + "（±" + fullScaleVolts[currentRangeIndex] + " V）。";
                    return advice;
                }
                // 一次降到合适索引：使峰≈TargetUtilization·新FS（亦保留 DecreaseHeadroom 下限）
                double needFs = Math.Max(peakVolts / TargetUtilization, peakVolts * DecreaseHeadroom);
                int best = FindSmallestRangeThatFits(needFs, fullScaleVolts, currentRangeIndex);
                // 单次最多降 2 档；禁止 6→0 跳到 ±10mV
                int floorIdx = Math.Max(0, currentRangeIndex - 2);
                if (best < floorIdx) best = floorIdx;
                if (best == 0 && currentRangeIndex > 2)
                    best = Math.Max(1, floorIdx);
                if (best < currentRangeIndex)
                {
                    advice.Suitable = false;
                    advice.Direction = Task6RangeAdjustmentDirection.CurrentRangeTooLargeDecreaseRange;
                    advice.SuggestedRangeIndex = best;
                    advice.Message = "量程过大（首零后一周期 峰/FS=" + (util * 100).ToString("0.0") + "%<" + (int)(UnderRangeFraction * 100)
                        + "%）；降到 ±" + fullScaleVolts[best] + " V（索引 " + best + "，单次≤2档）后重加本轮。";
                    return advice;
                }
            }

            advice.Suitable = true;
            advice.SuggestedRangeIndex = -1;
            advice.Direction = Task6RangeAdjustmentDirection.Suitable;
            advice.Message = usedPeriod
                ? ("量程合适（首零后一周期参考，峰值利用率正常）。")
                : ("量程合适（全窗峰值参考，无有效首零周期）。");
            return advice;
        }

        /// <summary>找第一个过零点起一个完整周期内的 |ADC| 峰值。</summary>
        static bool TryPeakAbsAdcFirstZeroOnePeriod(short[] adc, double sampleRateHz, out int peakAbs, out int firstZeroIndex, out int periodEndIndex)
        {
            peakAbs = 0;
            firstZeroIndex = -1;
            periodEndIndex = -1;
            if (adc == null || adc.Length < 16) return false;
            // 噪声门限：约 2% 满量程用相对值——此处用绝对 ADC 小阈值
            int noise = 32;
            int z0 = -1;
            for (int i = 1; i < adc.Length; i++)
            {
                int a0 = adc[i - 1], a1 = adc[i];
                if (a0 == 0 && Math.Abs(a1) > noise) { z0 = i - 1; break; }
                if ((a0 < 0 && a1 >= 0) || (a0 > 0 && a1 <= 0))
                {
                    if (Math.Abs(a0) >= noise || Math.Abs(a1) >= noise) { z0 = i; break; }
                }
            }
            if (z0 < 0) return false;
            // 同一方向的下一个过零 ≈ 半周期；再下一个 ≈ 整周期
            int sign0 = adc[z0] >= 0 ? 1 : -1;
            // 找离开零后再回到同极性穿越的第二过零
            int zeros = 0;
            int zEnd = -1;
            for (int i = z0 + 1; i < adc.Length; i++)
            {
                int a0 = adc[i - 1], a1 = adc[i];
                bool cross = (a0 < 0 && a1 >= 0) || (a0 > 0 && a1 <= 0) || (a0 == 0 && a1 != 0);
                if (!cross) continue;
                if (Math.Abs(a0) < noise && Math.Abs(a1) < noise) continue;
                zeros++;
                if (zeros >= 2) { zEnd = i; break; } // 两个过零 ≈ 一个周期
            }
            if (zEnd <= z0 + 4)
            {
                // 回退：用采样率估算 500Hz 周期，或取固定窗
                int periodSamples = sampleRateHz > 1000 ? (int)Math.Max(16, Math.Round(sampleRateHz / 500.0)) : Math.Max(64, adc.Length / 32);
                zEnd = Math.Min(adc.Length - 1, z0 + periodSamples);
            }
            int peak = 0;
            for (int i = z0; i <= zEnd && i < adc.Length; i++)
            {
                int a = Math.Abs((int)adc[i]);
                if (a > peak) peak = a;
            }
            peakAbs = peak;
            firstZeroIndex = z0;
            periodEndIndex = zEnd;
            return peak > 0;
        }

        static int FindSmallestRangeThatFits(double needVolts, double[] table, int current)
        {
            for (int i = 0; i <= current; i++)
                if (table[i] >= needVolts) return i;
            return current;
        }

        /// <summary>升档：选能覆盖 needVolts 的最小满量程索引（可跳过多档）。</summary>
        static int FindSmallestRangeAtLeast(double needVolts, double[] table)
        {
            if (table == null || table.Length == 0) return 0;
            for (int i = 0; i < table.Length; i++)
                if (table[i] >= needVolts) return i;
            return table.Length - 1;
        }

        public static void ApplyToCycleResult(AcquisitionCycleResult result, Task6RangeAdvice advice)
        {
            if (result == null || advice == null) return;
            result.Task6RangeSuitable = advice.Suitable;
            result.SuggestedPicoRangeIndex = advice.SuggestedRangeIndex;
            result.PicoOverrangeOrClipped = advice.ClippedOrNearFullScale;
            result.Task6RangeMessage = advice.Message ?? string.Empty;
            // 仅在 DLL 给出有限首零时写入；否则保持 NaN（不伪造 0）
            if (advice.UsedDllEvaluation
                && !double.IsNaN(advice.FirstZeroTimeSeconds)
                && !double.IsInfinity(advice.FirstZeroTimeSeconds)
                && advice.FirstZeroTimeSeconds >= 0.0)
            {
                result.FirstZeroTimeSeconds = advice.FirstZeroTimeSeconds;
            }
            else if (double.IsNaN(result.FirstZeroTimeSeconds) == false
                     && advice.UsedDllEvaluation == false
                     && (double.IsNaN(advice.FirstZeroTimeSeconds) || advice.FirstZeroTimeSeconds < 0))
            {
                // 启发式路径：若调用方未设，确保为 NaN
                // 不覆盖已有有限值以外的场景；Simulation 应显式用 NaN
            }
        }
    }
}
