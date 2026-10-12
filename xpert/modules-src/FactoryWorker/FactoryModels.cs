using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    public enum FactoryRunKind { Pressure, Calibration }
    [Serializable]
    public sealed class FactorySettings
    {
        public BenchSettings Input = new BenchSettings { ArmDelayMs = 500 };
        public bool Simulation = true;
        // Explicit zero-voltage optical diagnostic. False keeps legacy zero mode
        // strictly input-only/free-running. Native mode owns P0.7 only, never HV/AO.
        public bool ZeroSynchronizedOptical;
        // Explicit manual 0 kV AI/Pico capability. DI is display-only; no AO,
        // DO, HV or optical task is created and DI never licenses this capture.
        public bool ManualZeroInputWithoutEmergency;
        // Measurement-source context, never an output/P0.4 permission. Derived
        // from the actual runner kind after XML loading, not a persisted claim.
        [XmlIgnore] internal bool PressureFeedbackContext;
        public double TargetKv;
        public double MaximumKv = 28;
        // Zero uses MaximumKv. Compact R18 explicitly caps both at 30 kV;
        // target tolerance cannot enlarge the independent feedback ceiling.
        public double MaximumFeedbackKv;
        [XmlIgnore] public double FeedbackCeilingKv { get { return MaximumFeedbackKv == 0 ? MaximumKv : MaximumFeedbackKv; } }
        [XmlIgnore] public double EffectiveTargetTolerancePercent { get { return UsePrecisionRamp ? 5 : TargetTolerancePercent; } }
        [XmlIgnore] public double RampUpperV { get { return Math.Min(FeedbackCeilingKv * 1000, TargetKv * 1000 * (1 + EffectiveTargetTolerancePercent / 100)); } }
        public double MaximumAoV = 4.5;
        public double PressureVPerV = 6000;
        // User-confirmed command headroom only. This never changes the target
        // acceptance window, independent voltage ceiling or current protection.
        [XmlIgnore] public double NominalAoV { get { return TargetKv * 1000 / PressureVPerV; } }
        [XmlIgnore] public double AoCommandCeilingV { get { return Math.Min(5, Math.Min(MaximumAoV, NominalAoV * 1.05)); } }
        internal void ValidateAoCommand(double volts)
        {
            double ceiling = AoCommandCeilingV;
            if (!Finite(volts) || volts < 0 || !Finite(ceiling) || ceiling < 0 || volts > ceiling)
                throw new ArgumentOutOfRangeException("volts", "AO命令超过min(目标V/加压比×1.05,最大AO,5 V)，或命令无效。");
        }
        public double VmonFullScaleKv = 31;
        public double ImonFullScaleMa = 13;
        public double FeedbackFullScaleV = 5;
        public double CurrentLimitMa = 10;
        // 1 kS/s monitoring: one new block per feedback read, not a rolling
        // average across AO commands. Raw extrema retain protection authority.
        public int FeedbackWindowMs = 10;
        public double SafeResidualV = 50;
        // Unified opt-in AFTER-pressure limit, independent of startup,
        // calibration and the historical 50 V residual policy. Old XML keeps
        // this disabled; the desktop binds the selected device peak ceiling.
        public bool EndResidualCheckEnabled;
        public double EndResidualThresholdV;
        // Latest pressure policy supersedes the previous optional 4% checks.
        // This is derived, not an XML output permission or a discharge claim:
        // old persisted settings cannot silently re-enable pressure residual AI.
        [XmlIgnore] public bool SkipPressureResidualMeasurement
        { get { return PressureFeedbackContext && ResidualMeasurementProhibitedFor(FactoryRunKind.Pressure); } }
        // Archive classification has an actual recorded run kind; its XML
        // deliberately does not persist the in-memory measurement context.
        internal bool ResidualMeasurementProhibitedFor(FactoryRunKind kind)
        { return kind == FactoryRunKind.Pressure && TargetKv > 0 && !ZeroSynchronizedOptical && !ManualZeroInputWithoutEmergency; }
        [XmlIgnore] public bool UseEndResidualThreshold
        { get { return EndResidualCheckEnabled && TargetKv > 0 && !ZeroSynchronizedOptical && !ManualZeroInputWithoutEmergency; } }
        [XmlIgnore] public double EndResidualLimitV
        { get { return UseEndResidualThreshold ? EndResidualThresholdV : SafeResidualV; } }
        // Explicit R25 automatic pressure workflow only. Old records default false.
        // Ends the measurement after the complete finite capture, NOT a discharge clearance.
        public bool EndAfterPressureCapture;
        // Explicit V1.0.30 automatic pressure protocol, never inherited by old records.
        public bool OpticalTimeoutRetry;
        [XmlIgnore] public bool UseOpticalTimeoutRetry { get { return OpticalTimeoutRetry && UseCaptureOnlyEnd; } }
        public string SequenceId;
        public int SequenceRound = 1;
        public int SequenceCount = 1;
        // A serialized request describes a batch; only the resident session can
        // grant an in-memory, single-use permit after the preceding round ends.
        public bool AutomaticBatch;
        public bool BatchStartConfirmed;
        public bool BatchContinuation;
        [XmlIgnore] internal FactoryBatchPermit BatchPermit;
        // Per-issued final sequence round only. Old records and ordinary single
        // runs default false. This is a diagnostic, never a safety permission.
        public bool FinalSequenceAi4Observation;
        // Explicit user-started attempt of the same sequence; interrupted rounds
        // keep their logical round number but always have a new archive directory.
        public int SequenceAttempt = 1;
        [XmlIgnore] public bool UseCaptureOnlyEnd
        { get { return EndAfterPressureCapture && TargetKv > 0 && !ZeroSynchronizedOptical; } }
        [XmlIgnore] public bool ShouldCheckEndResidual
        { get { return !SkipPressureResidualMeasurement && (!UseCaptureOnlyEnd || UseEndResidualThreshold); } }
        // Automatic nonzero pressure only: no AI acquisition before HV ON.
        // False preserves the meaning of old files and manual/calibration flows.
        public bool VmonAfterHvStartup;
        public double StartupVmonLimitV = 300;
        // R23 opt-in zero/background allowance (HV side volts), not residual
        // clearance or target resolution. Zero preserves historical records.
        public double ZeroBackgroundLimitV;
        [XmlIgnore] public double VmonNegativeLimitV
        { get { return ZeroBackgroundLimitV > 0 ? ZeroBackgroundLimitV : SafeResidualV; } }
        internal double VmonWorkingVoltageV(double signedVolts)
        { return ZeroBackgroundLimitV > 0 && signedVolts < 0 && signedVolts >= -ZeroBackgroundLimitV ? 0 : signedVolts; }
        public bool IndependentDischargeConfirmed;
        [XmlIgnore] public bool UsePostHvVmonStartup
        { get { return VmonAfterHvStartup && TargetKv > 0 && !ZeroSynchronizedOptical; } }
        // User explicitly confirmed 300 V before HV ON and 50 V after the
        // round/fault on 2026-10-06. Zero keeps older configurations unchanged.
        // This limit never changes zero-optical or HV-ON zero-output checks.
        public double InitialResidualV;
        [XmlIgnore] public double InitialResidualLimitV
        { get { return TargetKv <= 0 || ZeroSynchronizedOptical || InitialResidualV == 0 ? SafeResidualV : InitialResidualV; } }
        public int InitialFeedbackSettleMs = 200;
        public int InitialFeedbackTimeoutMs = 2000;
        public double RampTimeoutSeconds = 10;
        // Explicit new desktop pressure entry only. Historical XML/API, manual
        // input and calibration do not inherit the precision-warning workflow.
        public bool PrecisionRampControl;
        // Explicit unified-desktop pressure route only. The desktop owns the
        // full-segment filtering and saved-record analysis; default factory and
        // calibration callers keep their existing synchronous diagnostics.
        public bool DesktopPostProcessing;
        public double RelativeOvervoltagePercent = 20;
        [XmlIgnore] public bool UsePrecisionRamp
        { get { return PrecisionRampControl && SkipPressureResidualMeasurement && UseCaptureOnlyEnd; } }
        [XmlIgnore] public double PrecisionProtectionUpperV
        { get { return Math.Min(FeedbackCeilingKv * 1000, TargetKv * 1000 * (1 + RelativeOvervoltagePercent / 100)); } }
        // Unified-only opt-in step scheduling. This does not change target
        // acceptance, fault handling or any independent protection threshold.
        public bool EnableFinePressureRamp;
        public double FinePressureThresholdKv = 3;
        [XmlIgnore] public bool UseFinePressureRamp
        { get { return EnableFinePressureRamp && UseCaptureOnlyEnd && TargetKv <= FinePressureThresholdKv; } }
        public int TargetHoldChecks = 0; // user-confirmed first-reach flow; 0 disables multi-round hold
        public int TargetHoldIntervalMs = 10;
        public int TargetHoldTimeoutMs = 500;
        public double ManualHvOnTimeoutSeconds = 300;
        public double DischargeTimeoutSeconds = 30;
        public double TargetTolerancePercent = 2;
        public double AgreementTolerancePercent = 15;
        // Opt-in bench mode: only |AI4| closes the ramp loop. Independent
        // raw validity, overvoltage, current, emergency and residual guards remain.
        public bool DividerOnlyFeedback;
        public bool FeedbackWiringConfirmed;
        public bool CalibrationSourceConfirmed;
        public int CalibrationPulseCount = 8;
        public double CalibrationFrequencyHz = 100;
        // Independent external readings permit ratio verification, not automatic metrology.
        public double ReferenceDcKv;
        public double ReferenceOscillationPeakKv;
        public double RatioTolerancePercent = 5;
        // Nominal standard capacitor data, editable by the factory operator.
        public bool ReferenceCapacitorEnabled = true;
        public double ReferenceCapacitanceNf = 200;
        public double ReferenceLossTangent = .0024;
        public int DecayCycleCount = 8;
        public int RangeEvaluationCycleCount = 8;
        // Independent of the legacy, formerly inactive range count and decay=8.
        public int LocationSaveCycleCount = 1;
        // Freeze the operator's protocol context at acquisition start. Later UI
        // edits cannot make a 200 nF record a 150 nF verification record.
        public bool VerificationContextRecorded;
        public double VerificationLoadCapacitanceNf = 200;
        public double VerificationRatedMaximumKv = 28;
        public bool VerificationAt80PercentConfirmed;
        public void Validate(FactoryRunKind kind)
        { Validate(kind, true); }
        // UI-only preflight. The worker always calls Validate(kind), including
        // the per-round acknowledgements; this cannot authorize an output.
        internal void ValidateBeforeOperatorConfirmation(FactoryRunKind kind)
        { Validate(kind, false); }
        private void Validate(FactoryRunKind kind, bool requireOperatorConfirmation)
        {
            // Re-derive measurement purpose from the actual caller, never from
            // saved XML or a context carried over from another run. This is not
            // a P0.4/output permission. Runner normalizes calibration to zero;
            // direct calibration/native preflight must reject a nonzero target.
            PressureFeedbackContext = kind == FactoryRunKind.Pressure && !ManualZeroInputWithoutEmergency;
            if (kind == FactoryRunKind.Calibration && TargetKv != 0)
                throw new ArgumentException("校准目标必须为0 kV，不能继承加压残压测量策略。");
            if(ManualZeroInputWithoutEmergency && (kind!=FactoryRunKind.Pressure || TargetKv!=0 ||
                ZeroSynchronizedOptical || AutomaticBatch || BatchStartConfirmed || BatchContinuation))
                throw new ArgumentException("人工0 kV只读模式不能用于自动加压、非零电压、校准或光输出。");
            bool batchPermitValid = AutomaticBatch && BatchPermit != null && BatchPermit.Matches(this);
            if ((BatchStartConfirmed || BatchContinuation) && !AutomaticBatch)
                throw new ArgumentException("批次确认只能用于连续加压。");
            if (AutomaticBatch && (kind != FactoryRunKind.Pressure || !UseCaptureOnlyEnd || !UsePostHvVmonStartup))
                throw new ArgumentException("自动批次仅用于正常非零加压采集流程。");
            if (BatchContinuation && (BatchStartConfirmed || SequenceRound < 2 || String.IsNullOrWhiteSpace(SequenceId)))
                throw new ArgumentException("自动续轮的批次和轮次无效。");
            if (requireOperatorConfirmation && AutomaticBatch &&
                (BatchContinuation ? (!Simulation && !batchPermitValid) : !BatchStartConfirmed))
                throw new ArgumentException("连续加压需要本次显式启动；续轮须由原设备会话核验上一轮收尾及保存完成。");
            if (SequenceCount < 1 || SequenceCount > 100 || SequenceRound < 1 || SequenceRound > SequenceCount)
                throw new ArgumentException("连续次数须为1～100，轮次不得超过总次数。");
            if (FinalSequenceAi4Observation && (kind != FactoryRunKind.Pressure || !UseCaptureOnlyEnd ||
                String.IsNullOrWhiteSpace(SequenceId) || SequenceRound != SequenceCount))
                throw new ArgumentException("末轮AI4均值观察只允许正常非零连续序列的最后一轮，不用于单次、校准或中间轮。");
            if (ZeroSynchronizedOptical && (kind != FactoryRunKind.Pressure || TargetKv != 0))
                throw new ArgumentException("零电压同步光触发仅用于0 kV独立诊断，不能用于加压或校准。");
            if (requireOperatorConfirmation && ZeroSynchronizedOptical && !Simulation && !FeedbackWiringConfirmed)
                throw new ArgumentException("同步光触发会操作P0.7，必须确认高压源已独立隔离、储能已按现场程序释放、反馈接线有效且人员已撤离。");
            if (Input == null) throw new ArgumentException("缺少采集参数。");
            Input.RealHardware = !Simulation;
            Input.TargetVoltageV = 0; // Reused INPUT contract is separate from HV target.
            Input.AutomaticSharedTrigger = false;
            Input.TriggerPulseMs = BenchSettings.OpticalHoldMilliseconds;
            Input.ValidateForNewRun();
            if(kind==FactoryRunKind.Pressure && TargetKv>0 && Input.PreTriggerDurationMs>=500)
                throw new ArgumentException("触发前长度须小于500 ms布置时限；请缩短采集长度或减少直流比例，不延长加压等待。");
            if (FeedbackWindowMs < 3 || FeedbackWindowMs > 40)
                throw new ArgumentException("AI反馈窗口须为3～40 ms（1000 S/s时每通道3～40点）；窗口统计不替代原始峰值保护。");
            if (PrecisionRampControl && (!Finite(RelativeOvervoltagePercent) || RelativeOvervoltagePercent <= 5 || RelativeOvervoltagePercent > 50))
                throw new ArgumentException("相对目标的异常过压比例须大于5%且不超过50%；精度窗口和独立最高限压不随该比例改变。");
            // The threshold selects a mode, never enlarges MaximumKv (the
            // desktop bridge independently caps actual pressure at 30 kV).
            if (!Positive(FinePressureThresholdKv) || FinePressureThresholdKv > 1000000 ||
                (kind == FactoryRunKind.Pressure && UseFinePressureRamp && RampTimeoutSeconds <= UnifiedFinePressureRamp.PlannedSeconds))
                throw new ArgumentException("精细化加压阈值无效，或升压超时不大于3秒。");
            if (!Finite(InitialResidualV) || InitialResidualV < 0 || InitialResidualV > 1000)
                throw new ArgumentException("初始反馈稳定阈值无效。");
            if (!Positive(StartupVmonLimitV) || StartupVmonLimitV > 1000)
                throw new ArgumentException("HV ON后VMON零输出上限须大于0且不超过1000 V。");
            if (!Finite(ZeroBackgroundLimitV) || ZeroBackgroundLimitV < 0 || ZeroBackgroundLimitV > 1000)
                throw new ArgumentException("启动/背景基准须为0～1000 V；0仅保留旧记录语义，不取消保护。");
            if (EndResidualCheckEnabled && !SkipPressureResidualMeasurement && (!Positive(EndResidualThresholdV) || EndResidualThresholdV > 100000))
                throw new ArgumentException("结束残压阈值须为有限正数且不超过100000 V；不改变启动或校准阈值。");
            if (requireOperatorConfirmation && kind == FactoryRunKind.Pressure && (UsePostHvVmonStartup || SkipPressureResidualMeasurement) && !Simulation && !IndependentDischargeConfirmed && !batchPermitValid)
                throw new ArgumentException("本轮HV ON前不采AI，必须先按现场程序独立确认试品已放电；不能用AO=0或VMON启动检查替代该确认。");
            if (Input.RequestedPicoRateHz < 100000)
                throw new ArgumentException("本工具校准和加压采用同一10kHz高通计量链，Pico实际请求至少100kS/s。");
            if (!Finite(TargetKv) || TargetKv < 0 || TargetKv > MaximumKv || TargetKv > VmonFullScaleKv || MaximumKv > VmonFullScaleKv ||
                !Positive(MaximumKv) || MaximumKv > 100 || !Positive(MaximumAoV) || MaximumAoV > 5 ||
                !Positive(PressureVPerV) || !Positive(VmonFullScaleKv) || !Positive(ImonFullScaleMa) ||
                !Positive(FeedbackFullScaleV) || !Positive(CurrentLimitMa) || CurrentLimitMa > ImonFullScaleMa ||
                !Positive(SafeResidualV) || SafeResidualV > 1000 || !Positive(RampTimeoutSeconds) || RampTimeoutSeconds > 600 ||
                !Positive(DischargeTimeoutSeconds) || DischargeTimeoutSeconds > 120 ||
                !Positive(TargetTolerancePercent) || TargetTolerancePercent > 10 ||
                !Positive(AgreementTolerancePercent) || AgreementTolerancePercent > 30 ||
                !Positive(RatioTolerancePercent) || RatioTolerancePercent > 20)
                throw new ArgumentException("加压、安全或反馈参数无效：" + InvalidPressureParameters());
            if (!Finite(MaximumFeedbackKv) || MaximumFeedbackKv < 0 || !Positive(FeedbackCeilingKv) ||
                FeedbackCeilingKv > VmonFullScaleKv || FeedbackCeilingKv < TargetKv ||
                !Positive(ManualHvOnTimeoutSeconds) || ManualHvOnTimeoutSeconds > 600)
                throw new ArgumentException("独立反馈上限或手动HV ON保持时限无效。");
            if (InitialFeedbackSettleMs < 50 || InitialFeedbackSettleMs > 1000 ||
                InitialFeedbackTimeoutMs < InitialFeedbackSettleMs + 50 || InitialFeedbackTimeoutMs > 10000)
                throw new ArgumentException("初始反馈等待须为 50～1000 ms，超时须覆盖连续确认且不超过 10 秒。");
            if (TargetHoldChecks < 0 || TargetHoldChecks > 50 || TargetHoldIntervalMs < 1 || TargetHoldIntervalMs > 100 ||
                TargetHoldTimeoutMs <= TargetHoldChecks*TargetHoldIntervalMs || TargetHoldTimeoutMs > 2000)
                throw new ArgumentException("达压复核0轮为取消；启用时1～50轮、间隔1～100 ms，限时覆盖各轮且不超过2000 ms。");
            if (TargetKv * 1000 / PressureVPerV > MaximumAoV)
                throw new ArgumentException("目标电压所需 AO 超过软件上限；不能通过修改显示绕过限制。");
            if (Input.TriggerTimeoutMs <= BenchSettings.OpticalHoldMilliseconds + Input.CaptureDurationMs)
                throw new ArgumentException("触发超时必须大于 " + BenchSettings.OpticalHoldMilliseconds + " ms 光触发保持与采集时长之和。");
            if (!Finite(ReferenceDcKv) || ReferenceDcKv < 0 || !Finite(ReferenceOscillationPeakKv) || ReferenceOscillationPeakKv < 0)
                throw new ArgumentException("参考电压不能为负数或非有限值。");
            if (!Positive(ReferenceCapacitanceNf) || ReferenceCapacitanceNf > 1000000 ||
                !Finite(ReferenceLossTangent) || ReferenceLossTangent < 0 || ReferenceLossTangent > .2 || DecayCycleCount != 8 || RangeEvaluationCycleCount < 1 || RangeEvaluationCycleCount > 100 || LocationSaveCycleCount < 1 || LocationSaveCycleCount > 100)
                throw new ArgumentException("标准电容及介损参数无效；本工具固定按8个完整周期计算衰减。");
            if (VerificationContextRecorded && (!Positive(VerificationLoadCapacitanceNf) || VerificationLoadCapacitanceNf > 1000000 ||
                !Positive(VerificationRatedMaximumKv) || VerificationRatedMaximumKv > 100))
                throw new ArgumentException("采集开始时的检定负载或最高电压上下文无效。");
            // Historical target pulse count is metadata, not a time-segment
            // acquisition minimum or permission to extend this <=60 ms run.
            if (!Positive(CalibrationFrequencyHz) || CalibrationFrequencyHz < 1 || CalibrationFrequencyHz > 10000)
                throw new ArgumentException("校准频率须为 1～10000 Hz。");
            if (requireOperatorConfirmation && !Simulation && !(kind == FactoryRunKind.Pressure && TargetKv == 0) && !FeedbackWiringConfirmed && !batchPermitValid)
                throw new ArgumentException("实机操作前必须确认 AI0 IMON、AI2 VMON、AI4 分压器及 P0.4 接线有效，不能忽略悬空反馈。");
            if (kind == FactoryRunKind.Calibration && !Simulation && !CalibrationSourceConfirmed)
                throw new ArgumentException("请先确认已知电量的低压校准源已手动注入，且高压源已隔离。");
        }
        public FactorySettings Clone()
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(FactorySettings));
                serializer.Serialize(stream, this); stream.Position = 0;
                var copy = (FactorySettings)serializer.Deserialize(stream);
                copy.BatchPermit = BatchPermit; // In-process only; never persisted or accepted from XML.
                copy.PressureFeedbackContext=PressureFeedbackContext;
                return copy;
            }
        }
        public void Save(string path)
        { using (var stream = File.Create(path)) new XmlSerializer(typeof(FactorySettings)).Serialize(stream, this); }
        public static FactorySettings Load(string path)
        { using (var stream = File.OpenRead(path)) return (FactorySettings)new XmlSerializer(typeof(FactorySettings)).Deserialize(stream); }
        internal static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        internal static bool Positive(double v) { return Finite(v) && v > 0; }

        private string InvalidPressureParameters()
        {
            var errors=new System.Collections.Generic.List<string>();
            Action<bool,string,double,string> add=(bad,name,value,allowed)=>{if(bad)errors.Add(name+"="+value.ToString("G",System.Globalization.CultureInfo.InvariantCulture)+"（"+allowed+"）");};
            add(!Finite(TargetKv)||TargetKv<0||TargetKv>MaximumKv||TargetKv>VmonFullScaleKv,"目标/kV",TargetKv,"须≥0，且≤最大目标和VMON满量程");
            add(!Positive(MaximumKv)||MaximumKv>100||MaximumKv>VmonFullScaleKv,"最大目标/kV",MaximumKv,"须>0、≤100且≤VMON满量程"+VmonFullScaleKv);
            add(!Positive(MaximumAoV)||MaximumAoV>5,"最大AO/V",MaximumAoV,"须>0且≤5");
            add(!Positive(PressureVPerV),"加压比",PressureVPerV,"须为有限正数");
            add(!Positive(VmonFullScaleKv),"VMON满量程/kV",VmonFullScaleKv,"须为有限正数");
            add(!Positive(ImonFullScaleMa),"IMON满量程/mA",ImonFullScaleMa,"须为有限正数，按实际硬件填写");
            add(!Positive(FeedbackFullScaleV),"反馈满量程/V",FeedbackFullScaleV,"须为有限正数");
            add(!Positive(CurrentLimitMa)||CurrentLimitMa>ImonFullScaleMa,"限流/mA",CurrentLimitMa,"须>0且≤IMON满量程 "+ImonFullScaleMa+" mA；不要为通过校验虚改反馈比例");
            add(!Positive(SafeResidualV)||SafeResidualV>1000,"故障/校准残压阈值/V",SafeResidualV,"须>0且≤1000");
            add(!Positive(RampTimeoutSeconds)||RampTimeoutSeconds>600,"升压超时/s",RampTimeoutSeconds,"须>0且≤600");
            add(!Positive(DischargeTimeoutSeconds)||DischargeTimeoutSeconds>120,"放电等待超时/s",DischargeTimeoutSeconds,"须>0且≤120");
            add(!Positive(TargetTolerancePercent)||TargetTolerancePercent>10,"目标容差/%",TargetTolerancePercent,"须>0且≤10");
            add(!Positive(AgreementTolerancePercent)||AgreementTolerancePercent>30,"双反馈容差/%",AgreementTolerancePercent,"须>0且≤30");
            add(!Positive(RatioTolerancePercent)||RatioTolerancePercent>20,"参考比值容差/%",RatioTolerancePercent,"须>0且≤20");
            return String.Join("；",errors);
        }
    }
    public sealed class FactoryFeedback
    {
        public DateTime TimestampUtc;
        public double ImonRawV, VmonRawV, DividerRawV;
        public bool EmergencyReleased;
        public double AoV;
        // SampleCount=0 keeps old single-sample records/fakes compatible.
        public int SampleCount;
        public double ImonMinimumRawV, ImonMaximumRawV, VmonMinimumRawV, VmonMaximumRawV, DividerMinimumRawV, DividerMaximumRawV;
        public double ImonHalfCodeV, VmonHalfCodeV, DividerHalfCodeV;
        public int BacklogBefore, BacklogAfter, ReadBatchCount;
        public long ReadElapsedMs;
        // Driver-visible FIFO boundary after an AO command, not a claim of
        // physical settling or hardware timestamp synchronization.
        public bool FreshWindowAfterAoCommand;
        public int FreshWindowBoundaryQueued, FreshWindowSamplesAfterBoundary;
        // Separate derived values. Never overwrite the SDK last sample/extrema.
        public int WindowSampleCount;
        public double ImonMedianRawV, VmonMedianRawV, DividerMedianRawV;
    }
    public sealed class FactoryResult
    {
        public FactorySettings Settings;
        public FactoryRunKind Kind;
        [XmlIgnore] public BenchCapture Capture;
        public List<FactoryFeedback> Ramp = new List<FactoryFeedback>();
        public FactoryRampStatistics RampStatistics = new FactoryRampStatistics();
        public FactoryTargetHoldStatistics TargetHold = new FactoryTargetHoldStatistics();
        public List<FactoryFeedback> TargetHoldFeedback = new List<FactoryFeedback>();
        public FactoryFaultResidualRecord FaultResidual = new FactoryFaultResidualRecord();
        public FactoryTriggerRecoveryRecord TriggerRecovery = new FactoryTriggerRecoveryRecord();
        public FactoryZeroVoltageObservation ZeroVoltageObservation;
        public FactoryFinalAi4Observation FinalAi4Observation;
        public string DescribeRampAndHold()
        {
            return RampStatistics.Describe() + (TargetHold!=null && TargetHold.Started ?
                "\n达压固定AO复核" + (TargetHold.Passed?"通过":"未通过") + "；" + TargetHold.Detail : "");
        }
        public List<FactoryFeedback> StartupFeedback = new List<FactoryFeedback>();
        public CalibrationResult Calibration;
        public FactoryMetrics Metrics;
        // Deferred means no full metrics/detected waveform has been computed.
        // Raw ADC quality and real stop/close receipts are still mandatory.
        public bool FullMetricsDeferred;
        public bool ResidualSafe;
        // Zero-pressure input-only acquisition is not a discharge confirmation.
        public bool ReadOnlyAcquisition;
        public bool ZeroTriggerDiagnostic;
        public bool Completed;
        public bool PostCaptureMonitoringSkipped;
        public bool AcquisitionCompleted;
        public string DirectoryPath = "";
        public string SafetyDetail = "";
        public bool CompleteCaptureAvailable()
        {
            return Completed && (ResidualSafe || ((ReadOnlyAcquisition || (ZeroTriggerDiagnostic && Settings != null && Settings.ZeroSynchronizedOptical)) && Kind == FactoryRunKind.Pressure &&
                Settings != null && Settings.TargetKv == 0));
        }
        public bool CaptureOnlyAvailable()
        {
            return AcquisitionCompleted && PostCaptureMonitoringSkipped && !Completed && !ResidualSafe &&
                Kind == FactoryRunKind.Pressure && Settings != null &&
                (Settings.UseCaptureOnlyEnd || Settings.ResidualMeasurementProhibitedFor(Kind)) && Capture != null;
        }
    }
    public sealed class FactoryRampStatistics
    {
        public bool Started;
        // Iterations count attempted HV UP loop bodies, including failed reads.
        public int Iterations;
        public int ValidFeedbackCount;
        public long ElapsedMilliseconds;
        public double TargetMinimumV, TargetMaximumV, LastDividerV, LastAoV;
        public bool FineRampApplied;
        public int FineRampPeriodMs, FineRampSkippedPeriods;
        public double FineRampPlannedSeconds, FineRampMaximumIncrementV;
        public bool PrecisionEvaluated, WithinPrecision, PrecisionWarning;
        public int PrecisionRecheckReads;
        public double ProtectionUpperV;
        public string ExitCode = "NotStarted", ExitReason = "未执行HV UP";
        public string Describe()
        {
            if (!Started) return "HV UP：未执行（历史记录可能没有升压统计）";
            return "HV UP 循环 " + Iterations + " 次 / 有效反馈 " + ValidFeedbackCount + " 次；实际用时 " +
                (ElapsedMilliseconds / 1000.0).ToString("F3") + " s；达压区间 " +
                (TargetMinimumV / 1000).ToString("F3") + "～" + (TargetMaximumV / 1000).ToString("F3") +
                " kV；" + ExitReason + (FineRampApplied ? "；精细化加压，预计 " + FineRampPlannedSeconds.ToString("0.##") +
                " s，反馈节拍 " + FineRampPeriodMs + " ms，跳过逾期节拍 " + FineRampSkippedPeriods + " 次" : String.Empty) +
                (PrecisionEvaluated ? "；精度" + (WithinPrecision ? "在范围内" : "偏差（未达精度，不等于过压故障）") +
                    "，异常保护上限 " + (ProtectionUpperV / 1000).ToString("F3") + " kV" : String.Empty);
        }
    }
    public sealed class FactoryTargetHoldStatistics
    {
        public bool Started, Passed;
        public int Reads, Consecutive;
        public long ElapsedMilliseconds;
        public double FrozenAoV;
        public string Detail = "达压复核未执行";
    }
    // Partial captures are deliberately in-process only. Exception.Data on this
    // Framework runtime requires serializable values, while captures contain
    // native-sized arrays and are persisted separately by the worker.
    internal sealed class FactoryRunException : AggregateException
    {
        internal FactoryResult PartialResult { get; private set; }
        internal FactoryRunException(FactoryResult partial, IEnumerable<Exception> errors)
            : base("本轮未完成安全测试流程。", errors) { PartialResult = partial; }
    }
    internal interface IFactoryBackend : IDisposable
    {
        void Initialize(FactorySettings settings, bool calibration);
        FactoryFeedback ReadFeedback();
        bool ReadEmergencyReleased();
        void WriteAo(double volts);
        void SetHv(bool on);
        void SetOptical(bool on);
        void Arm(bool freeRun);
        // Only after the pressure loop has reached target and stopped AO increments.
        // Stops/clears the monitor before allocating the finite AI task.
        void ArmBeforeShutdown();
        bool ArtComplete { get; }
        bool PicoComplete { get; }
        BenchCapture ReadCapture();
        void StopCapture();
        void Shutdown();
    }
    internal interface IFactoryClock
    {
        long ElapsedMilliseconds { get; }
        void Pause(int milliseconds, System.Threading.CancellationToken cancellation);
    }
    internal interface IFactoryOpticalRecovery
    {
        // Stop and discard BOTH previous finite blocks, then arm a fresh pair.
        // Must not reconnect, reset a fault latch, enable HV or write positive AO.
        void PrepareOpticalRetry();
    }
    // Input-only recovery of an already opened session. Never reconnects a
    // device, clears an output fault latch, arms a trigger or enables an output.
    internal interface IFactoryFaultMonitor
    {
        void PrepareFaultMonitoring();
        FactoryFeedback ReadFaultFeedback();
    }
}
