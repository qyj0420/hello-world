using System;
using System.Collections.Generic;
using System.Globalization;

namespace Xiongxin.FactoryTest
{
    public enum FactoryAcceptanceStatus { Pending, Passed, Failed, DiagnosticOnly, ExternalRecordRequired }

    public sealed class FactoryAcceptanceCheck
    {
        public string Group = "", Name = "", Clause = "", Requirement = "", Actual = "", Detail = "";
        public FactoryAcceptanceStatus Status;
        public bool? CriterionMet;
    }

    // Evidence is separate from acquisition settings. Entering a software gain,
    // coefficient, capacitor nominal value, or target is not evidence of accuracy.
    public sealed class FactoryAcceptanceEvidence
    {
        public double LoadCapacitanceNf = 200;
        public double RatedMaximumKv = 28;
        public double RatedPhaseVoltageKv;
        public bool VoltageWaveformAt80PercentConfirmed;
        public bool VoltageReferenceConfirmed;
        public string VoltageReferenceId = "";
        public double IndependentVoltageReferenceKv = Double.NaN;
        public bool VoltageRangeCoverageConfirmed;
        public bool PdReferenceConfirmed;
        public string PdReferenceId = "";
        public List<FactoryChargeVerificationReading> ChargeReadings = new List<FactoryChargeVerificationReading>();
        public List<FactoryVoltageVerificationReading> VoltageReadings = new List<FactoryVoltageVerificationReading>();
        public bool SensitivitySequenceConfirmed;
        public double MinimumDetectablePc = Double.NaN;
        public double BackgroundNoisePc = Double.NaN;
        public bool MaximumOutputTestConfirmed;
        public double IndependentlyMeasuredMaximumKv = Double.NaN;
        public bool MaximumVoltageCurrentTestConfirmed;
        public double IndependentlyMeasuredChargingCurrentMa = Double.NaN;
        public bool MaximumRateCharging150NfConfirmed;
        public double IndependentlyMeasuredChargingTimeMs = Double.NaN;
        public double TemperatureC = Double.NaN, HumidityPercent = Double.NaN, AltitudeM = Double.NaN;
        public double MainsVoltageV = Double.NaN, MainsFrequencyHz = Double.NaN, MainsThdPercent = Double.NaN;
        public bool NoCondensationConfirmed, NoInterferenceConfirmed;
    }

    public sealed class FactoryChargeVerificationReading
    {
        public DateTime AcquiredUtc;
        public string AcquisitionId = "", ReferenceId = "";
        public string ArtSerial = "", PicoSerial = "";
        public double KnownPc, MeasuredPc, PeakV, AppliedCoefficientPcPerV;
        public int RangeIndex;
        public bool Simulation, IndependentReferenceConfirmed;
        public FactoryVerificationConditions Conditions = new FactoryVerificationConditions();
    }
    public sealed class FactoryVoltageVerificationReading
    {
        public DateTime AcquiredUtc;
        public string AcquisitionId = "", ReferenceId = "", ArtSerial = "", PicoSerial = "";
        public double NominalKv, MeasuredKv, ReferenceKv;
        public bool Simulation, IndependentReferenceConfirmed;
        public FactoryVerificationConditions Conditions = new FactoryVerificationConditions();
    }

    // Recorded at acquisition, never reconstructed from the current UI. Older
    // evidence without these fields can remain a diagnostic, not become proof
    // merely because an operator changes a nominal value after collection.
    public sealed class FactoryVerificationConditions
    {
        public bool Recorded;
        public double LoadCapacitanceNf = Double.NaN, RatedMaximumKv = Double.NaN;
        public bool At80PercentConfirmed;
        public double PicoRequestedRateHz = Double.NaN, PicoActualRateHz = Double.NaN;
        public int ResolutionBits, HardwareBits, PicoCoupling = -1;
        public bool PicoBandwidth20MHz;
        public double DividerVPerV = Double.NaN;
        public double ArtRateHz = Double.NaN;
        public int ArtTerminal = -1;
        public double CalibrationFrequencyHz = Double.NaN;
        public int CalibrationPulseCount;
        public string PdFilterProtocol = "", CalibrationProtocol = "", VoltagePeakProtocol = "";
        public bool RawDividerWithinRange;
    }

    /// <summary>
    /// Offline evidence review for the non-location factory checks requested by
    /// the operator. It neither operates equipment nor issues a verification
    /// certificate. Limits below were checked against the actual scanned pages.
    /// </summary>
    public static class FactoryAcceptanceChecks
    {
        public const string SystemStandardPath = @"D:\汇总\宣传与标准\相关标准\DL∕T1575-2016 6KV-35KV电缆振荡波局部放电测量系统.pdf";
        public const string VerificationStandardPath = @"D:\汇总\宣传与标准\相关标准\DL∕T 1932-2018 6kV-35kV电缆振荡波局部放电测量系统检定方法.pdf";
        public const string ScopeNote = "本页用于所选非定位项目的出厂核验；单轮、200 nF 参考负载及本机换算值不能构成完整标准检定结论。定位项目按本工具范围未纳入，安规、频带及独立器具资质需要另行实测和记录。";
        private static readonly double[] charges = { 20000, 10000, 5000, 1000, 500, 100, 50, 20 };
        public const string VoltagePeakProtocol = "ART-AI4-full-raw-absolute-peak-v1: 80kS/s, no transient exclusion, all raw values strictly inside +/-9.95V";
        public static double[] RequiredChargeLevelsPc { get { return (double[])charges.Clone(); } }

        public static FactoryChargeVerificationReading AddChargeReading(FactoryAcceptanceEvidence evidence,
            FactoryResult result, double knownPc, string referenceId)
        {
            if (evidence == null) throw new ArgumentNullException("evidence");
            if (!Positive(knownPc)) throw new ArgumentException("独立标准电量必须大于 0。");
            if (!evidence.PdReferenceConfirmed || String.IsNullOrWhiteSpace(referenceId))
                throw new ArgumentException("先确认使用独立标准校准器，并填写其证据/器具编号。");
            RequireValidCapture(result);
            if (result.Kind != FactoryRunKind.Calibration || result.Calibration == null || !result.Calibration.Valid ||
                !Positive(result.Calibration.MeanPeakV) || result.Capture.Overflow)
                throw new ArgumentException("本轮没有可靠校准脉冲峰值，不能作为独立电量校验读数。");
            var capture = result.Capture;
            double applied = capture.Settings.PdCalibrationPcPerV;
            if (!Positive(applied)) throw new ArgumentException("采集时实际使用的旧校准系数无效。");
            string id = CaptureId(result);
            if (evidence.ChargeReadings == null) evidence.ChargeReadings = new List<FactoryChargeVerificationReading>();
            foreach (var prior in evidence.ChargeReadings)
                if (prior != null && prior.AcquisitionId == id)
                    throw new InvalidOperationException("同一次采集只能记录一次校验读数；五次测量需要五份不同采集。");
            var reading = new FactoryChargeVerificationReading {
                AcquiredUtc = capture.CapturedUtc, AcquisitionId = id, ReferenceId = referenceId.Trim(),
                ArtSerial = capture.ArtSerial, PicoSerial = capture.PicoSerial, KnownPc = knownPc,
                PeakV = result.Calibration.MeanPeakV, AppliedCoefficientPcPerV = applied,
                MeasuredPc = result.Calibration.MeanPeakV * applied, RangeIndex = capture.Settings.RangeIndex,
                Simulation = capture.Simulation, IndependentReferenceConfirmed = true,
                Conditions = CaptureConditions(result)
            };
            // Never use result.Calibration.CoefficientPcPerV here: that new
            // candidate was derived from the very signal being checked.
            evidence.ChargeReadings.Add(reading); return reading;
        }

        public static FactoryVoltageVerificationReading AddVoltageReading(FactoryAcceptanceEvidence evidence,
            FactoryResult result, double referenceKv, string referenceId)
        {
            if (evidence == null) throw new ArgumentNullException("evidence");
            if (!Positive(referenceKv) || !evidence.VoltageReferenceConfirmed || String.IsNullOrWhiteSpace(referenceId))
                throw new ArgumentException("电压比对需有效的独立标准分压器读数及证据/器具编号。");
            RequireValidCapture(result);
            if (result.Kind != FactoryRunKind.Pressure || !Positive(result.Settings.TargetKv) || result.Metrics == null ||
                !result.Metrics.FrequencyValid || !Positive(result.Metrics.OscillationPeakKv) || !VoltagePeakReliable(result))
                throw new ArgumentException("本轮没有有效振荡峰值，不能记录标准电压比对。");
            string id = CaptureId(result);
            if (evidence.VoltageReadings == null) evidence.VoltageReadings = new List<FactoryVoltageVerificationReading>();
            foreach (var prior in evidence.VoltageReadings)
                if (prior != null && prior.AcquisitionId == id)
                    throw new InvalidOperationException("同一次采集不能重复计为多个电压测量点。");
            var reading = new FactoryVoltageVerificationReading {
                AcquiredUtc = result.Capture.CapturedUtc, AcquisitionId = id, ReferenceId = referenceId.Trim(),
                ArtSerial = result.Capture.ArtSerial, PicoSerial = result.Capture.PicoSerial,
                NominalKv = result.Settings.TargetKv, MeasuredKv = result.Metrics.OscillationPeakKv,
                ReferenceKv = referenceKv, Simulation = result.Capture.Simulation, IndependentReferenceConfirmed = true,
                Conditions = CaptureConditions(result)
            };
            evidence.VoltageReadings.Add(reading); return reading;
        }

        public static List<FactoryAcceptanceCheck> Evaluate(FactoryResult result, FactoryAcceptanceEvidence evidence)
        {
            evidence = evidence ?? new FactoryAcceptanceEvidence();
            var checks = new List<FactoryAcceptanceCheck>();
            bool actualCapture = ValidCapture(result);
            bool simulation = actualCapture && result.Capture.Simulation;
            var metrics = result == null ? null : result.Metrics;
            var reference = metrics == null ? null : metrics.ReferenceAnalysis;
            bool frozenContext = result != null && result.Settings != null && result.Settings.VerificationContextRecorded;
            double recordedLoad = frozenContext ? result.Settings.VerificationLoadCapacitanceNf : Double.NaN;
            double recordedMaximum = frozenContext ? result.Settings.VerificationRatedMaximumKv : Double.NaN;
            bool loadQualified = QualifiedLoad(recordedLoad);
            Add(checks, "试验条件", "DL/T 1932 标准负载", "DL/T 1932-2018 §5.1a、§6.2.1", "150 nF ±7.5 nF",
                "采集时 " + F(recordedLoad) + " nF；当前输入 " + F(evidence.LoadCapacitanceNf) + " nF", loadQualified ? FactoryAcceptanceStatus.Passed : FactoryAcceptanceStatus.DiagnosticOnly,
                loadQualified ? "采集开始时人工确认的负载在检定规定范围；其工作电压及背景局放还需独立确认。当前输入修改不追认旧记录。" :
                    !frozenContext ? "旧记录没有采集开始时的负载条件快照，只作诊断，不能由当前输入追认标准工况。" : "采集时负载仅用于参考出厂测试；约 200 nF 不属于 150±7.5 nF 的检定负载。", loadQualified);
            bool maximumKnown = Positive(recordedMaximum);
            bool at80 = actualCapture && maximumKnown && Positive(result.Settings.TargetKv) &&
                Math.Abs(result.Settings.TargetKv / recordedMaximum - .8) <= .008;
            bool fullMethod = actualCapture && !simulation && loadQualified && at80 && result.Settings.VerificationAt80PercentConfirmed &&
                evidence.VoltageReferenceConfirmed && !String.IsNullOrWhiteSpace(evidence.VoltageReferenceId);
            Add(checks, "试验条件", "80% Umax 电压波形测点", "DL/T 1932-2018 §6.2.1；DL/T 1575-2016 §6.2.1", "标准分压器；80% 最大试验电压；记录前 8 周期",
                "采集时额定 Umax " + F(recordedMaximum) + " kV；本轮设定 " + F(result == null || result.Settings == null ? Double.NaN : result.Settings.TargetKv) + " kV",
                fullMethod ? FactoryAcceptanceStatus.Passed : FactoryAcceptanceStatus.Pending,
                simulation ? "仿真记录不能作为实机标准核验。" : "需负载、独立标准分压器、80% Umax 及实际试验条件均有证据；本软件不会自动运行最大电压测试。", fullMethod);

            bool frequencyAvailable = actualCapture && metrics != null && metrics.FrequencyValid && Positive(metrics.FrequencyHz);
            Limit(checks, "当前采集", "振荡频率", "DL/T 1575-2016 §5.3.1；DL/T 1932-2018 §6.2.1", "30～500 Hz",
                frequencyAvailable ? F(metrics.FrequencyHz) + " Hz" : "无有效振荡频率", frequencyAvailable,
                frequencyAvailable && metrics.FrequencyHz >= 30 && metrics.FrequencyHz <= 500, fullMethod,
                "只使用通过振荡模型验证的实测频率；背景噪声不参与判断。");
            bool decayAvailable = actualCapture && reference != null && reference.PhaseValid && reference.DecayValid && reference.RequestedCycles == 8 &&
                reference.CyclePeakKv != null && reference.CyclePeakKv.Length >= 9 && Finite(reference.MeasuredDecayPercent);
            Limit(checks, "当前采集", "前 8 周期幅值衰减", "DL/T 1575-2016 §5.3.1；DL/T 1932-2018 §4.2.2a、§6.2.1", "≤50%（8 个完整周期）",
                decayAvailable ? F(reference.MeasuredDecayPercent) + " %；9 个同极性实测峰" : "没有 8 个完整周期的可靠峰值",
                decayAvailable, decayAvailable && reference.MeasuredDecayPercent >= 0 && reference.MeasuredDecayPercent <= 50, fullMethod,
                "采用 100×(1−第9峰/第1峰)；不足 9 峰不外推。200 nF 和非 80% Umax 时显示参考诊断。");

            bool voltageAvailable = actualCapture && result.Kind == FactoryRunKind.Pressure && Positive(result.Settings.TargetKv) &&
                metrics != null && metrics.FrequencyValid && Positive(metrics.OscillationPeakKv) && VoltagePeakReliable(result) &&
                Positive(evidence.IndependentVoltageReferenceKv) && evidence.VoltageReferenceConfirmed && !String.IsNullOrWhiteSpace(evidence.VoltageReferenceId);
            double voltageError = voltageAvailable ? RelativeError(metrics.OscillationPeakKv, evidence.IndependentVoltageReferenceKv) : Double.NaN;
            Limit(checks, "独立参考比对", "本轮电压峰值误差", "DL/T 1575-2016 §5.3.2；DL/T 1932-2018 §4.2.2b、§6.2.2", "≤3%；全量程须均匀 5 测点",
                voltageAvailable ? "本机 " + F(metrics.OscillationPeakKv) + " / 标准 " + F(evidence.IndependentVoltageReferenceKv) + " kV；误差 " + F(voltageError) + " %" : "待独立标准分压器读数",
                voltageAvailable, voltageAvailable && voltageError <= 3, !simulation && loadQualified,
                "本轮只判当前单点。分压比设定、VMON 及目标电压不能代替独立标准值；参考峰值必须包含相同过冲/波形范围。");
            EvaluateVoltageCoverage(checks, result, evidence, loadQualified);
            EvaluateChargeReadings(checks, result, evidence, loadQualified);

            bool sensitivityAvailable = evidence.PdReferenceConfirmed && !String.IsNullOrWhiteSpace(evidence.PdReferenceId) &&
                evidence.SensitivitySequenceConfirmed && Positive(evidence.MinimumDetectablePc) &&
                Finite(evidence.BackgroundNoisePc) && evidence.BackgroundNoisePc >= 0 && evidence.BackgroundNoisePc < 2;
            Limit(checks, "独立参考比对", "局放测量灵敏度", "DL/T 1932-2018 §4.2.4、§6.4、§6.1e", "优于 10 pC；背景噪声<2 pC",
                "最小可测 " + F(evidence.MinimumDetectablePc) + " pC；背景 " + F(evidence.BackgroundNoisePc) + " pC",
                sensitivityAvailable, sensitivityAvailable && evidence.MinimumDetectablePc < 10,
                actualCapture && !simulation && loadQualified,
                "须先以 20 pC 校准，再用独立标准校准器从 20 pC 逐级降低；一次 100 pC/500 pC 采集不能证明灵敏度。");
            EvaluateGenerator(checks, evidence);
            EvaluateExternal(checks, evidence);
            return checks;
        }

        private static void EvaluateVoltageCoverage(List<FactoryAcceptanceCheck> checks, FactoryResult result,
            FactoryAcceptanceEvidence evidence, bool loadQualified)
        {
            var accepted = new List<FactoryVoltageVerificationReading>();
            var ids = new HashSet<string>();
            double maximumError = 0;
            bool allMethodsQualified = true, conditionsKnown = true, conditionsConsistent = true, allPeaksReliable = true;
            FactoryVerificationConditions firstConditions = null;
            if (evidence.VoltageReadings != null)
                foreach (var point in evidence.VoltageReadings)
                    if (point != null && !point.Simulation && point.IndependentReferenceConfirmed && !String.IsNullOrWhiteSpace(point.ReferenceId) &&
                        Positive(point.NominalKv) && Positive(point.MeasuredKv) && Positive(point.ReferenceKv) &&
                        CurrentIdentity(point.ArtSerial, point.PicoSerial, result) && ids.Add(point.AcquisitionId))
                    {
                        accepted.Add(point); maximumError = Math.Max(maximumError, RelativeError(point.MeasuredKv, point.ReferenceKv));
                        bool known = ConditionsRecorded(point.Conditions);
                        conditionsKnown &= known;
                        allMethodsQualified &= known && QualifiedLoad(point.Conditions.LoadCapacitanceNf) && point.Conditions.RawDividerWithinRange;
                        if (known)
                        {
                            allPeaksReliable &= point.Conditions.RawDividerWithinRange;
                            if (firstConditions == null) firstConditions = point.Conditions;
                            else conditionsConsistent &= SameVoltageChain(firstConditions, point.Conditions);
                            conditionsConsistent &= CurrentVoltageChain(point.Conditions, result);
                        }
                    }
            accepted.Sort((a, b) => a.NominalKv.CompareTo(b.NominalKv));
            var levels = new List<double>();
            foreach (var point in accepted) if (levels.Count == 0 || Math.Abs(point.NominalKv - levels[levels.Count - 1]) > .001) levels.Add(point.NominalKv);
            bool coverage = evidence.VoltageRangeCoverageConfirmed && levels.Count >= 5 && Positive(evidence.RatedMaximumKv);
            if (coverage)
            {
                // The five selected nonzero points span 20,40,60,80,100% of
                // Umax. A zero reading has no defined relative peak error.
                foreach (double fraction in new[] { .2, .4, .6, .8, 1.0 })
                {
                    bool found = false; foreach (double level in levels)
                        if (Math.Abs(level / evidence.RatedMaximumKv - fraction) <= .02) found = true;
                    if (!found) coverage = false;
                }
            }
            if (conditionsKnown && firstConditions != null)
                conditionsConsistent &= Positive(evidence.RatedMaximumKv) && SameNumber(firstConditions.RatedMaximumKv, evidence.RatedMaximumKv);
            Limit(checks, "独立参考比对", "电压全范围 5 点误差", "DL/T 1932-2018 §6.2.2；§4.2.2b", "从零至最大范围均匀 5 个测量点；每点误差≤3%",
                "有效不同电压点 " + levels.Count + "/5；当前最大误差 " + (accepted.Count == 0 ? "—" : F(maximumError) + " %"),
                coverage && conditionsConsistent && allPeaksReliable, coverage && maximumError <= 3, allMethodsQualified && conditionsKnown,
                !allPeaksReliable ? "历史读数的完整AI4段含饱和/削顶，过冲真实峰值未知，不计为有效电压峰值误差。" :
                !conditionsConsistent ? "历史电压读数的分压比、额定范围或测量协议与当前设备记录不同，不能合并为同一全范围验证序列。" :
                "需五次不同实机采集、每次冻结的150±7.5 nF工况和同一当前设备的独立标准分压器读数；原始AI4全部过冲均不得削顶。按20/40/60/80/100% Umax检查覆盖。缺少历史条件快照仅作诊断；当前负载修改不能追认过去200 nF数据。");
        }

        private static void EvaluateChargeReadings(List<FactoryAcceptanceCheck> checks, FactoryResult result,
            FactoryAcceptanceEvidence evidence, bool loadQualified)
        {
            foreach (double knownPc in charges)
            {
                var readings = new List<FactoryChargeVerificationReading>(); var ids = new HashSet<string>();
                bool fixedCoefficient = true, sameReference = true, conditionsKnown = true, conditionsConsistent = true, allMethodsQualified = true;
                double coefficient = Double.NaN; string referenceId = null;
                FactoryVerificationConditions firstConditions = null;
                if (evidence.ChargeReadings != null)
                    foreach (var reading in evidence.ChargeReadings)
                    {
                        if (reading == null || reading.Simulation || !reading.IndependentReferenceConfirmed ||
                            String.IsNullOrWhiteSpace(reading.AcquisitionId) || String.IsNullOrWhiteSpace(reading.ReferenceId) ||
                            Math.Abs(reading.KnownPc - knownPc) > Math.Max(1e-9, knownPc * 1e-9) ||
                            !Positive(reading.MeasuredPc) || !Positive(reading.AppliedCoefficientPcPerV) ||
                            !CurrentIdentity(reading.ArtSerial, reading.PicoSerial, result) || !ids.Add(reading.AcquisitionId)) continue;
                        if (readings.Count == 0) { coefficient = reading.AppliedCoefficientPcPerV; referenceId = reading.ReferenceId; }
                        else
                        {
                            fixedCoefficient &= Math.Abs(reading.AppliedCoefficientPcPerV / coefficient - 1) <= 1e-9;
                            sameReference &= referenceId == reading.ReferenceId;
                        }
                        bool known = ConditionsRecorded(reading.Conditions);
                        conditionsKnown &= known;
                        allMethodsQualified &= known && QualifiedLoad(reading.Conditions.LoadCapacitanceNf);
                        if (known)
                        {
                            if (firstConditions == null) firstConditions = reading.Conditions;
                            else conditionsConsistent &= SameChargeChain(firstConditions, reading.Conditions);
                            conditionsConsistent &= CurrentChargeChain(reading.Conditions, reading.AppliedCoefficientPcPerV, result);
                        }
                        readings.Add(reading);
                    }
                double min = Double.PositiveInfinity, max = Double.NegativeInfinity, error = 0;
                foreach (var r in readings) { min = Math.Min(min, r.MeasuredPc); max = Math.Max(max, r.MeasuredPc); error = Math.Max(error, RelativeError(r.MeasuredPc, knownPc)); }
                double stability = readings.Count == 0 ? Double.NaN : (max - min) / knownPc * 100;
                bool available = readings.Count >= 5 && fixedCoefficient && sameReference && conditionsConsistent && evidence.PdReferenceConfirmed && !String.IsNullOrWhiteSpace(evidence.PdReferenceId);
                string why = !fixedCoefficient ? "同档读数的实际校准系数不同，应重新以固定系数采集五次。" :
                    !sameReference ? "同档标准校准器证据编号不同，不能合并为同一五次稳定度序列。" :
                    !conditionsConsistent ? "同档历史读数的采样率、分辨率、耦合、带宽、滤波/校准协议或此前已应用系数与当前采集条件不一致，不能合并为同一五次序列。" :
                    "每档需至少5次不同采集，固定此前已应用系数及同一采集/10kHz滤波条件；稳定度=(最大−最小)/标准电量，误差为各次最差值。每次负载条件取采集开始快照，缺少快照或历史200 nF仅作诊断，不能由当前150 nF输入追认。";
                string actual = F(knownPc) + " pC；有效读数 " + readings.Count + "/5；稳定度 " + F(stability) + " %；最差误差 " + (readings.Count == 0 ? "—" : F(error)) + " %";
                Limit(checks, "局放八档独立校验", ChargeLabel(knownPc), "DL/T 1932-2018 §4.2.3、§6.3", "稳定度<5%；每次相对误差≤10%",
                    actual, available, available && stability < 5 && error <= 10, conditionsKnown && allMethodsQualified, why);
            }
            Add(checks, "独立参考比对", "20 pC～20 nC 测量范围", "DL/T 1575-2016 §5.2.1、§6.1.1", "20/50/100/500 pC，1/5/10/20 nC 八档；检波脉冲高度可调至量程 50%～100%",
                "由上述八档逐项记录；不能由软件下拉量程或校准系数设定推断", FactoryAcceptanceStatus.ExternalRecordRequired,
                "须在屏蔽实验条件和规定电容接线下注入独立已知电量，并验证各档脉冲、实际范围及无削顶。", null);
        }

        private static void EvaluateGenerator(List<FactoryAcceptanceCheck> checks, FactoryAcceptanceEvidence evidence)
        {
            bool maxAvailable = evidence.MaximumOutputTestConfirmed && Positive(evidence.IndependentlyMeasuredMaximumKv) && Positive(evidence.RatedPhaseVoltageKv);
            double required = Positive(evidence.RatedPhaseVoltageKv) ? 2 * Math.Sqrt(2) * evidence.RatedPhaseVoltageKv : Double.NaN;
            Limit(checks, "独立发生器试验", "直流激励最高输出电压", "DL/T 1575-2016 §5.1.1；DL/T 1932-2018 §4.2.2c、§6.2.3", "峰值≥2√2×电缆额定相电压",
                "独立峰值 " + F(evidence.IndependentlyMeasuredMaximumKv) + " kV；最低要求 " + F(required) + " kV",
                maxAvailable, maxAvailable && evidence.IndependentlyMeasuredMaximumKv >= required, true,
                "最高输出能力须以标准分压器和规定负载另行测量；软件设定上限不能当作实测最高电压。");
            bool currentAvailable = evidence.MaximumVoltageCurrentTestConfirmed && Finite(evidence.IndependentlyMeasuredChargingCurrentMa) && evidence.IndependentlyMeasuredChargingCurrentMa >= 0;
            Limit(checks, "独立发生器试验", "最高电压充电电流", "DL/T 1575-2016 §5.1.1、§6.5；DL/T 1932-2018 §4.2.2d、§6.2.4.1", "≥8 mA（直流激励、最大试验电压、负载电阻）",
                F(evidence.IndependentlyMeasuredChargingCurrentMa) + " mA", currentAvailable, currentAvailable && evidence.IndependentlyMeasuredChargingCurrentMa >= 8, true,
                "需要规定负载电阻下的独立电流记录；本机 IMON 换算或电流限值设定不能证明输出能力。");
            bool timeAvailable = evidence.MaximumRateCharging150NfConfirmed && Math.Abs(evidence.LoadCapacitanceNf - 150) <= 7.5 &&
                Positive(evidence.IndependentlyMeasuredChargingTimeMs) && Positive(evidence.RatedMaximumKv);
            double timeLimit = Positive(evidence.RatedMaximumKv) ? 40 * evidence.RatedMaximumKv : Double.NaN;
            Limit(checks, "独立发生器试验", "150 nF 负载充电时间", "DL/T 1932-2018 §3.1、§4.2.2e、§6.2.4.2", "tf=2.5×(30%→70% Umax 时间差)；tf≤40 Umax ms",
                "tf " + F(evidence.IndependentlyMeasuredChargingTimeMs) + " ms；上限 " + F(timeLimit) + " ms", timeAvailable,
                timeAvailable && evidence.IndependentlyMeasuredChargingTimeMs <= timeLimit, true,
                "需 150±7.5 nF、最快充电及独立示波器升压记录；本软件 0.01 V 慢步进时间不可替代该检定时间。");
        }

        private static void EvaluateExternal(List<FactoryAcceptanceCheck> checks, FactoryAcceptanceEvidence evidence)
        {
            bool environmentAvailable = Finite(evidence.TemperatureC) && Finite(evidence.HumidityPercent) && Finite(evidence.AltitudeM) &&
                Finite(evidence.MainsVoltageV) && Finite(evidence.MainsFrequencyHz) && Finite(evidence.MainsThdPercent) && Finite(evidence.BackgroundNoisePc);
            bool environmentPass = environmentAvailable && evidence.TemperatureC >= 18 && evidence.TemperatureC <= 28 &&
                evidence.HumidityPercent >= 0 && evidence.HumidityPercent <= 75 && evidence.AltitudeM >= 0 && evidence.AltitudeM <= 2000 &&
                evidence.MainsVoltageV >= 198 && evidence.MainsVoltageV <= 242 && evidence.MainsFrequencyHz >= 49.5 && evidence.MainsFrequencyHz <= 50.5 &&
                evidence.MainsThdPercent >= 0 && evidence.MainsThdPercent <= 5 && evidence.BackgroundNoisePc >= 0 && evidence.BackgroundNoisePc < 2 &&
                evidence.NoCondensationConfirmed && evidence.NoInterferenceConfirmed;
            Limit(checks, "外部条件及器具", "检定环境", "DL/T 1932-2018 §6.1", "23±5 °C、RH≤75%、无凝露、海拔≤2000 m、220±22 V、50±0.5 Hz、THD≤5%、背景<2 pC",
                "温度 " + F(evidence.TemperatureC) + " °C；RH " + F(evidence.HumidityPercent) + " %；背景 " + F(evidence.BackgroundNoisePc) + " pC",
                environmentAvailable, environmentPass, true, "需实际环境/供电/噪声及无干扰记录；单纯填写 nominal 值不作为器具溯源证明。");
            External(checks, "标准器具资质", "DL/T 1932-2018 §4.2.1b、§5.1、§5.2、§5.5～§5.7", "负载150±7.5 nF、工作60 kV、最大工作电压下局放≤10 pC；本机校准器5级、标准校准器2级；标准分压器±1%、≤100 ns；独立示波器幅度±2%、时间±0.5%、带宽≥200 MHz、采样率≥1 GHz",
                "这些是独立检定器具要求，不是把本机 Pico 的采样设置判为不合格；需有效检定/校准证据编号。");
            External(checks, "测量频带", "DL/T 1575-2016 §5.2.3、§6.1.3", "按 GB/T 7354-2003 对频带验证；上下截止频率与标称值偏差≤±10%", "采样率或 20 MHz 带宽开关不能代替整机测量链频带实测。");
            External(checks, "补偿电容自身性能", "DL/T 1575-2016 §5.5.1～§5.5.2、§6.4", "电容量宜不小于150 nF，±5%；最大试验电压下局放≤1 pC；交流耐压无击穿/闪络", "约200 nF、tanδ≈0.24% 可用于回路参考反算；自身局放、容量允差和耐压须另行核验。");
            External(checks, "外部电源绝缘耐压", "DL/T 1575-2016 §5.7.1、§6.7", "外部电源输入与外壳间 2000 V 工频、1 min，无击穿/闪络", "本工具不执行绝缘耐压，需另行安规试验记录。");
            External(checks, "危险故障保护和报警", "DL/T 1575-2016 §5.1.3、§5.7.2～§5.7.3", "过压、短路、过载保护；击穿自动切断振荡电源并报警；防误操作和紧急切断", "软件步骤灯和一次正常加压不能证明真实故障保护动作；需独立故障/互锁试验记录。");
            External(checks, "软件功能与外观", "DL/T 1575-2016 §5.6、§5.11、§6.6、§6.11、§7.2及表1", "软件运行、校准/控制/测量功能及结构外观逐项检验；每台出厂需检验合格", "本工具按用户要求未验证定位功能，不能据非定位项目宣称整机全部出厂项目完成。");
        }

        private static void External(List<FactoryAcceptanceCheck> checks, string name, string clause, string requirement, string detail)
        { Add(checks, "需要另行试验", name, clause, requirement, "未提供本工具可验证的独立试验记录", FactoryAcceptanceStatus.ExternalRecordRequired, detail, null); }
        private static void Limit(List<FactoryAcceptanceCheck> checks, string group, string name, string clause, string requirement,
            string actual, bool available, bool criterionMet, bool qualifiedMethod, string detail)
        {
            FactoryAcceptanceStatus status = !available ? FactoryAcceptanceStatus.Pending : qualifiedMethod
                ? (criterionMet ? FactoryAcceptanceStatus.Passed : FactoryAcceptanceStatus.Failed) : FactoryAcceptanceStatus.DiagnosticOnly;
            if (available && !qualifiedMethod) detail += " 现有数据" + (criterionMet ? "满足" : "不满足") + "数值要求，但试验方法/来源证据不足，仅作诊断。";
            Add(checks, group, name, clause, requirement, actual, status, detail, available ? (bool?)criterionMet : null);
        }
        private static void Add(List<FactoryAcceptanceCheck> checks, string group, string name, string clause, string requirement,
            string actual, FactoryAcceptanceStatus status, string detail, bool? criterionMet)
        { checks.Add(new FactoryAcceptanceCheck { Group = group, Name = name, Clause = clause, Requirement = requirement, Actual = actual, Status = status, Detail = detail, CriterionMet = criterionMet }); }
        private static FactoryVerificationConditions CaptureConditions(FactoryResult result)
        {
            var input = result.Capture.Settings;
            var settings = result.Settings;
            return new FactoryVerificationConditions {
                Recorded = settings.VerificationContextRecorded,
                LoadCapacitanceNf = settings.VerificationContextRecorded ? settings.VerificationLoadCapacitanceNf : Double.NaN,
                RatedMaximumKv = settings.VerificationContextRecorded ? settings.VerificationRatedMaximumKv : Double.NaN,
                At80PercentConfirmed = settings.VerificationContextRecorded && settings.VerificationAt80PercentConfirmed,
                PicoRequestedRateHz = input.RequestedPicoRateHz, PicoActualRateHz = result.Capture.PicoRateHz,
                ResolutionBits = input.ResolutionBits, HardwareBits = result.Capture.HardwareBits,
                PicoCoupling = input.PicoCoupling, PicoBandwidth20MHz = input.PicoBandwidth20MHz,
                DividerVPerV = input.DividerVPerV, ArtRateHz = result.Capture.ArtRateHz, ArtTerminal = input.ArtTerminal,
                CalibrationFrequencyHz = settings.CalibrationFrequencyHz,
                CalibrationPulseCount = settings.CalibrationPulseCount,
                PdFilterProtocol = FactoryDetectedWaveforms.PdAlgorithm,
                CalibrationProtocol = RecordedCalibrationProtocol(result),
                VoltagePeakProtocol = VoltagePeakProtocol,
                RawDividerWithinRange = VoltagePeakReliable(result)
            };
        }
        private static string RecordedCalibrationProtocol(FactoryResult result)
        {
            // Voltage-only evidence does not consume a calibration mean. Keep
            // that path unchanged; charge evidence must identify its actual
            // saved algorithm, never relabel a historical mean as today's one.
            if(result.Kind!=FactoryRunKind.Calibration)return FactoryCalibrationAdapter.Method;
            string detail=result.Calibration==null?null:result.Calibration.Detail;
            if(String.IsNullOrEmpty(detail))return "";
            foreach(string protocol in new[]{FactoryCalibrationAdapter.Method,FactoryCalibrationAdapter.LegacyFixedCountMethod})
                if(detail==protocol || detail.StartsWith(protocol+"。",StringComparison.Ordinal))return protocol;
            return ""; // Missing/unrecognized provenance stays explicitly unknown.
        }
        private static bool ConditionsRecorded(FactoryVerificationConditions c)
        {
            return c != null && c.Recorded && Positive(c.LoadCapacitanceNf) && Positive(c.RatedMaximumKv) &&
                Positive(c.PicoRequestedRateHz) && Positive(c.PicoActualRateHz) &&
                (c.ResolutionBits == 8 || c.ResolutionBits == 12 || c.ResolutionBits == 14 || c.ResolutionBits == 15 || c.ResolutionBits == 16) &&
                c.HardwareBits > 0 && c.HardwareBits <= c.ResolutionBits && (c.PicoCoupling == 0 || c.PicoCoupling == 1) &&
                Positive(c.DividerVPerV) && Positive(c.ArtRateHz) && (c.ArtTerminal == 10083 || c.ArtTerminal == 10078 || c.ArtTerminal == 10106) &&
                Positive(c.CalibrationFrequencyHz) &&
                (c.CalibrationProtocol == FactoryCalibrationAdapter.Method || c.CalibrationPulseCount >= 3) &&
                !String.IsNullOrWhiteSpace(c.PdFilterProtocol) && !String.IsNullOrWhiteSpace(c.CalibrationProtocol) && !String.IsNullOrWhiteSpace(c.VoltagePeakProtocol);
        }
        private static bool SameChargeChain(FactoryVerificationConditions a, FactoryVerificationConditions b)
        {
            return SameNumber(a.PicoRequestedRateHz, b.PicoRequestedRateHz) && SameNumber(a.PicoActualRateHz, b.PicoActualRateHz) &&
                a.ResolutionBits == b.ResolutionBits && a.HardwareBits == b.HardwareBits && a.PicoCoupling == b.PicoCoupling &&
                a.PicoBandwidth20MHz == b.PicoBandwidth20MHz && a.PdFilterProtocol == b.PdFilterProtocol &&
                a.CalibrationProtocol == b.CalibrationProtocol && SameNumber(a.CalibrationFrequencyHz, b.CalibrationFrequencyHz) &&
                // The current time-segment protocol uses every quality-valid
                // pulse; the old target count is historical metadata only.
                // Other saved protocols keep their original count semantics.
                (a.CalibrationProtocol == FactoryCalibrationAdapter.Method || a.CalibrationPulseCount == b.CalibrationPulseCount);
        }
        private static bool CurrentChargeChain(FactoryVerificationConditions conditions, double coefficient, FactoryResult result)
        {
            if (result == null || result.Capture == null || result.Capture.Settings == null || result.Settings == null) return false;
            var input = result.Capture.Settings;
            return SameNumber(conditions.PicoRequestedRateHz, input.RequestedPicoRateHz) && SameNumber(conditions.PicoActualRateHz, result.Capture.PicoRateHz) &&
                conditions.ResolutionBits == input.ResolutionBits && conditions.PicoCoupling == input.PicoCoupling &&
                conditions.PicoBandwidth20MHz == input.PicoBandwidth20MHz && conditions.PdFilterProtocol == FactoryDetectedWaveforms.PdAlgorithm &&
                !String.IsNullOrEmpty(conditions.CalibrationProtocol) &&
                conditions.CalibrationProtocol == RecordedCalibrationProtocol(result) && SameNumber(coefficient, input.PdCalibrationPcPerV);
        }
        private static bool SameVoltageChain(FactoryVerificationConditions a, FactoryVerificationConditions b)
        {
            return SameNumber(a.DividerVPerV, b.DividerVPerV) && SameNumber(a.RatedMaximumKv, b.RatedMaximumKv) &&
                SameNumber(a.ArtRateHz, b.ArtRateHz) && a.ArtTerminal == b.ArtTerminal && a.VoltagePeakProtocol == b.VoltagePeakProtocol;
        }
        private static bool CurrentVoltageChain(FactoryVerificationConditions conditions, FactoryResult result)
        {
            return result != null && result.Capture != null && result.Capture.Settings != null && result.Settings != null &&
                SameNumber(conditions.DividerVPerV, result.Capture.Settings.DividerVPerV) &&
                SameNumber(conditions.ArtRateHz, result.Capture.ArtRateHz) && conditions.ArtTerminal == result.Capture.Settings.ArtTerminal &&
                conditions.VoltagePeakProtocol == VoltagePeakProtocol && result.Settings.VerificationContextRecorded &&
                SameNumber(conditions.RatedMaximumKv, result.Settings.VerificationRatedMaximumKv);
        }
        private static bool QualifiedLoad(double nf) { return Positive(nf) && Math.Abs(nf - 150) <= 7.5; }
        private static bool SameNumber(double a, double b)
        { return Finite(a) && Finite(b) && Math.Abs(a - b) <= Math.Max(1e-12, Math.Max(Math.Abs(a), Math.Abs(b)) * 1e-9); }
        private static bool VoltagePeakReliable(FactoryResult result)
        {
            if (result == null || result.Capture == null || result.Capture.VoltageRaw == null || result.Capture.VoltageRaw.Length == 0) return false;
            // ART is configured at ±10 V. Include every sample, especially the
            // excluded switching transient: filtered tail fits cannot recover a
            // saturated initial oscillation/overshoot peak.
            foreach (double value in result.Capture.VoltageRaw)
                if (!Finite(value) || Math.Abs(value) >= 9.95) return false;
            return true;
        }
        private static bool CurrentIdentity(string art, string pico, FactoryResult result)
        { return ValidCapture(result) && !result.Capture.Simulation && !String.IsNullOrWhiteSpace(art) && !String.IsNullOrWhiteSpace(pico) && art == result.Capture.ArtSerial && pico == result.Capture.PicoSerial; }
        private static string CaptureId(FactoryResult result)
        { return result.Capture.CapturedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "|" + result.Capture.ArtSerial + "|" + result.Capture.PicoSerial; }
        private static void RequireValidCapture(FactoryResult result)
        { if (!ValidCapture(result)) throw new ArgumentException("需完整有效的本轮采集和保存时的实际设置，未完成/残压未知记录不计入标准校验。"); }
        private static bool ValidCapture(FactoryResult result)
        {
            if (result == null || result.Settings == null || result.Capture == null || !result.Completed || !result.ResidualSafe) return false;
            try { result.Capture.ValidateCapture(); return !result.Capture.Overflow; } catch { return false; }
        }
        private static string ChargeLabel(double known) { return known >= 1000 ? F(known / 1000) + " nC 电量档" : F(known) + " pC 电量档"; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool Positive(double value) { return Finite(value) && value > 0; }
        private static double RelativeError(double measured, double standard) { return Math.Abs(measured - standard) / standard * 100; }
        private static string F(double value) { return Finite(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "—"; }
    }
}
