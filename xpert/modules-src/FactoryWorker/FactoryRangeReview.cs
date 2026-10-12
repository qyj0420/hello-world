using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryRangeChoice
    {
        public RangeDecisionAction Action;
        public bool Suppress;
        public bool Automatic;
        public RangeDecisionRecord Record;
    }

    // Uses the same raw ADC advisor and decision dialog as the 100 PressureTest
    // module. Display always retains the full segment; range review uses the
    // whole 0 V segment, or first validated zero plus location N cycles,
    // limited to the actual record end with an explicit shortened flag.
    // It never derives a range from a detector or corrected ADC baseline.
    public static class FactoryRangeReview
    {
        // Same calibrated display convention as 100 PressureDischargeRanges:
        // hardware range remains volts; displayed >=10 pC limits round down
        // to a multiple of 10, never overstate the calibrated capacity.
        public static string RangeLabel(int rangeIndex, double coefficientPcPerV)
        {
            if (rangeIndex < 0 || rangeIndex >= BenchSettings.Ranges.Length)
                throw new ArgumentOutOfRangeException("rangeIndex");
            if (!FactorySettings.Positive(coefficientPcPerV)) return "未校准";
            double exact = BenchSettings.Ranges[rangeIndex] * coefficientPcPerV;
            if (!FactorySettings.Finite(exact)) return "未校准";
            double display = exact < 10 ? exact : Math.Floor(exact / 10) * 10;
            return "± " + display.ToString(display < 10 ? "G17" : "0", CultureInfo.InvariantCulture) + " pC";
        }

        public static LocationRangeResult Evaluate(FactoryResult round)
        {
            RequireCompletedRound(round);
            return EvaluateData(round);
        }

        // Measurement-only advice, not permission to retry, energize, or certify.
        // Existing retry/application APIs still require RequireCompletedRound.
        internal static LocationRangeResult EvaluateCapturedData(FactoryResult round)
        {
            if (round == null || !round.CaptureOnlyAvailable())
                throw new InvalidOperationException("缺少正常结束的完整采集，不能评估下一轮量程。");
            round.Capture.ValidateCapture();
            return EvaluateData(round);
        }

        private static LocationRangeResult EvaluateData(FactoryResult round)
        {
            var c = round.Capture;
            bool whole = round.Kind == FactoryRunKind.Calibration || round.Settings.TargetKv == 0;
            var phase = FactoryRangePhase.Analyze(c);
            if(round.Metrics!=null)round.Metrics.RangePhase=phase;
            var window = FactoryMeasuredRangeWindow.SelectCycles(whole, c.PdAdc.Length, c.PicoRateHz,
                c.Settings.PicoPreTriggerCount, phase, round.Settings.LocationSaveCycleCount);
            var quality = LocationRangeAdvisor.Evaluate(c.PdAdc, window, c.MaximumAdc,
                c.PicoFullScaleV, c.Settings.RangeIndex, whole ? c.Settings.CalibrationPulsePc : 0,
                c.Settings.PdCalibrationPcPerV, c.Overflow);
            quality.FactoryCycleWindow = !whole;
            quality.WindowShortened = window.Shortened;
            quality.EvaluationCycles = whole ? 0 : round.Settings.LocationSaveCycleCount;
            FactoryTransientRangePolicy.Apply(c, quality, !whole && round.Kind == FactoryRunKind.Pressure);
            if(!whole)FactoryRangeRetention.ApplyCycleWindow(quality,c.Settings.RangeIndex);
            else FactoryRangeRetention.Apply(quality,c.Settings.RangeIndex);
            quality.WindowDefinition = window.Definition;
            quality.LocationQuality = quality.LocationQuality.Replace("定位窗口", "局放评估窗口").Replace("本轮定位幅值", "本轮局放幅值").Replace("定位算法", "脉冲识别").Replace("定位信噪比", "局放信噪比");
            quality.Reason = quality.Reason.Replace("定位窗口", "局放评估窗口").Replace("定位原始峰值", "窗口原始峰值").Replace("定位窗内", "局放评估窗内").Replace("定位量程", "局放量程");
            return quality;
        }

        public static bool NeedsReview(FactoryResult round, LocationRangeResult quality)
        {
            RequireCompletedRound(round);
            if (quality == null) throw new ArgumentNullException("quality");
            var context = CreateContext(round, quality, 0, false);
            // Critical maximum-range warnings are always presented, including
            // when automatic ranging or ordinary prompts have been disabled.
            return RangeDecisionPolicy.IsMaximumRangeRisk(context) ||
                RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context) ||
                round.Settings.Input.AutoRange && (quality.Change || !quality.LocationQualityPassed || quality.MaximumRangeInsufficient);
        }

        public static RangeDecisionContext CreateContext(FactoryResult round, LocationRangeResult quality,
            int retries, bool automaticRangeRetries)
        {
            RequireCompletedRound(round);
            return BuildContext(round,quality,retries,automaticRangeRetries);
        }

        internal static RangeDecisionContext CreateMeasurementContext(FactoryResult round, LocationRangeResult quality)
        {
            if(round==null || !(round.CompleteCaptureAvailable() || round.CaptureOnlyAvailable()))
                throw new InvalidOperationException("缺少正常结束的完整采集，不能显示下一轮量程建议。");
            round.Capture.ValidateCapture();
            return BuildContext(round,quality,0,false); // Never grants automatic retry.
        }

        private static RangeDecisionContext BuildContext(FactoryResult round, LocationRangeResult quality,
            int retries, bool automaticRangeRetries)
        {
            if (quality == null) throw new ArgumentNullException("quality");
            int recommended = quality.RecommendedIndex;
            if (recommended < 0 || recommended >= BenchSettings.Ranges.Length)
                recommended = round.Capture.Settings.RangeIndex;
            bool pressure = round.Kind == FactoryRunKind.Pressure && !round.ReadOnlyAcquisition && !round.ZeroTriggerDiagnostic;
            return new RangeDecisionContext(quality, round.Capture.Settings.RangeIndex, recommended,
                pressure ? round.Settings.TargetKv * 1000 : 0, retries, false, pressure, automaticRangeRetries);
        }

        public static bool CanSilentRetry(RangeDecisionContext context, ISet<int> visited, out string reason)
        {
            if (!context.IsAutomaticRangeRetry)
            { reason = "普通量程提示仍开启，等待明确选择。"; return false; }
            if (!RangeDecisionPolicy.CanAdjustAndRetry(context, out reason)) return false;
            if (visited != null && visited.Contains(context.RecommendedRangeIndex))
            { reason = "推荐量程已在本轮使用，停止往复调档；请查看已保留的原始记录。"; return false; }
            return true;
        }

        public static FactoryRangeChoice AutomaticChoice(RangeDecisionContext context)
        {
            var record = RangeDecisionPolicy.CreateRecord(context, RangeDecisionAction.AdjustRetry);
            record.WasAutomaticDecision = true;
            record.SuppressFurtherRangePrompts = true;
            return new FactoryRangeChoice { Action = RangeDecisionAction.AdjustRetry,
                Suppress = true, Automatic = true, Record = record };
        }

        public static FactoryRangeChoice Choose(Form owner, RangeDecisionContext context, Icon icon = null)
        {
            using (var dialog = new RangeDecisionDialog(context, icon ?? (owner == null ? null : owner.Icon)))
            {
                dialog.ShowDialog(owner);
                return new FactoryRangeChoice { Action = dialog.Decision,
                    Suppress = dialog.SuppressFurtherRangePrompts, Record = dialog.Record };
            }
        }

        public static FactorySettings CreateRetrySettings(FactoryResult original, RangeDecisionContext context,
            ISet<int> visited)
        {
            RequireCompletedRound(original);
            if(original.ZeroTriggerDiagnostic) throw new InvalidOperationException("0 kV光触发诊断必须逐轮人工确认，不允许量程自动重发光。");
            string reason;
            if (!RangeDecisionPolicy.CanAdjustAndRetry(context, out reason)) throw new InvalidOperationException(reason);
            if (visited != null && visited.Contains(context.RecommendedRangeIndex))
                throw new InvalidOperationException("推荐量程已采集过，禁止重复访问已试档位。");
            if (context.CurrentRangeIndex != original.Capture.Settings.RangeIndex ||
                context.TargetVoltageV != (original.Kind == FactoryRunKind.Pressure ? original.Settings.TargetKv * 1000 : 0))
                throw new InvalidOperationException("量程上下文与原始单轮不一致，不能采用编辑后的电压或档位重做。");
            var retry = original.Settings.Clone();
            retry.Input.RangeIndex = context.RecommendedRangeIndex;
            retry.Input.RunNumber = checked(original.Settings.Input.RunNumber + 1);
            retry.Validate(original.Kind);
            return retry;
        }

        public static void WriteDecision(FactoryResult round, LocationRangeResult quality, FactoryRangeChoice choice)
        {
            RequireCompletedRound(round);
            if (quality == null || choice == null || choice.Record == null) throw new ArgumentException("缺少完整量程决定。");
            string directory = Path.GetFullPath(round.DirectoryPath);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            string path = Path.Combine(directory, "factory-range-decision.xml");
            // CreateNew prevents changing an earlier decision or original result.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(RangeDecisionRecord)).Serialize(stream, choice.Record);
        }

        private static void RequireCompletedRound(FactoryResult round)
        {
            if (round == null || round.Settings == null || round.Capture == null)
                throw new ArgumentException("缺少本轮完整采集。");
            if (!round.CompleteCaptureAvailable())
                throw new InvalidOperationException("本轮尚未完整结束或残压检查未通过，不允许量程重做流程。");
            round.Capture.ValidateCapture();
        }
    }
}
