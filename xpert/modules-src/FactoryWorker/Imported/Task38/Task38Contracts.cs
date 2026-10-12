using System;
using System.IO;
using System.Xml.Serialization;

namespace HVX.OWTS.Task38
{
    public enum Task38ControllerState
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

    public enum Task38ExitReason
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

    // Values intentionally match PS5000A_RANGE for the ranges supported by a 5242D.
    public enum PicoInputRange
    {
        PlusMinus10mV = 0,
        PlusMinus20mV = 1,
        PlusMinus50mV = 2,
        PlusMinus100mV = 3,
        PlusMinus200mV = 4,
        PlusMinus500mV = 5,
        PlusMinus1V = 6,
        PlusMinus2V = 7,
        PlusMinus5V = 8,
        PlusMinus10V = 9,
        PlusMinus20V = 10
    }

    public enum Task6RangeAdjustmentDirection
    {
        Suitable = -1,
        CurrentRangeTooSmallIncreaseRange = 0,
        CurrentRangeTooLargeDecreaseRange = 1
    }

    [Serializable]
    public sealed class Task38Configuration
    {
        private static readonly double[] Pico5242DFullScaleVolts = new double[]
        {
            0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0
        };

        public Task38Configuration()
        {
            ArtDeviceName = "Dev1";
            ArtDaqLibraryDirectory = @"C:\Program Files (x86)\ART Technology\ART-DAQ\Lib\x64";
            SafetyReadyActiveHigh = true;
            SafetyPollMilliseconds = 50;
            HvEnableDelayMilliseconds = 50;
            RampStepAoVolts = 0.05;
            RampStepDelayMilliseconds = 100;
            GroundReleaseSettleMilliseconds = 50;
            GroundDelayAfterArmedMilliseconds = 200;
            HoldGroundAfterCapture = true;
            GroundTriggerPulseMilliseconds = 20;
            GroundInSafeIdle = true;

            TargetVoltageV = 0.0;
            VoltageGainVPerAoVolt = 0.0;
            DividerRatioVPerV = 0.0;
            SourceVoltageFeedbackFullScaleV = 0.0;
            SourceCurrentFeedbackFullScaleA = 0.0;
            CurrentLimitA = 0.0;
            HardOverCurrentA = 0.0;
            AoMaximumVolts = 5.0;
            AoTargetHeadroomFraction = 0.10;
            TargetReachedToleranceFraction = 0.005;
            OverVoltageTripFraction = 0.10;
            CurrentLimitEnterFraction = 0.95;
            CurrentLimitLeaveFraction = 0.90;
            CurrentLimitNoGrowthSeconds = 5.0;
            VoltageGrowthResetThresholdV = 10.0;
            MaximumRampSeconds = 300.0;
            ResidualVoltageSafeThresholdV = 50.0;
            PreStartDischargeTimeoutMilliseconds = 10000;
            DischargeVerifyTimeoutMilliseconds = 10000;

            ArtCurrentAiChannel = "ai0";
            ArtSourceVoltageAiChannel = "ai2";
            ArtDividerAiChannel = "ai4";
            ArtControlAoChannel = "ao0";
            ArtHvOnDoLine = "port0/line0";
            ArtHvIndicatorDoLine = "port0/line3";
            ArtConnectionIndicatorDoLine = "port0/line5";
            ArtGroundTriggerDoLine = "port0/line7";
            ArtSafetyDiLine = "port0/line4";
            ArtFiniteTriggerTerminal = "/Dev1/PFI0";
            ArtAnalogTriggerChannel = "ai1";
            ArtAnalogTriggerThresholdV = 1.5;
            TriggerWaitTimeoutMilliseconds = 3000;
            ArtMonitorInputMinimumV = -10.0;
            ArtMonitorInputMaximumV = 10.0;
            ArtFiniteSampleRateHz = 80000.0;
            CaptureDurationSeconds = 0.060;
            ArtCaptureTimeoutMilliseconds = 5000;

            PicoSdkLibraryDirectory = "auto";
            PicoSerial = string.Empty;
            PicoResolutionBits = 14;
            PicoTimebase = 3;
            PicoExpectedSampleIntervalNanoseconds = 8.0;
            PicoSampleRateHz = 125000000.0;
            PicoRangeIndex = (int)PicoInputRange.PlusMinus20V;
            PicoRangeFullScaleVolts = (double[])Pico5242DFullScaleVolts.Clone();
            PicoExternalTriggerThresholdV = 1.5;
            PicoCaptureTimeoutMilliseconds = 10000;
            PicoStartRelativeToArtNanoseconds = 0.0;

            SwitchingTransientSkipMicroseconds = 500.0;
            Task6PretriggerMicroseconds = 0.0;
            AutoApplyTask6RangeForNextCycle = true;
            CaptureOnNonEmergencyFault = true;
            CaptureOnEmergencyStop = true;
        }

        public string ArtDeviceName { get; set; }
        public string ArtDaqLibraryDirectory { get; set; }
        public bool SafetyReadyActiveHigh { get; set; }
        public int SafetyPollMilliseconds { get; set; }
        public int HvEnableDelayMilliseconds { get; set; }
        public double RampStepAoVolts { get; set; }
        public int RampStepDelayMilliseconds { get; set; }
        public int GroundReleaseSettleMilliseconds { get; set; }
        public int GroundDelayAfterArmedMilliseconds { get; set; }
        public bool HoldGroundAfterCapture { get; set; }
        public int GroundTriggerPulseMilliseconds { get; set; }
        public bool GroundInSafeIdle { get; set; }

        public double TargetVoltageV { get; set; }
        // Zero follows the residual threshold; a positive value can only tighten it.
        public double ZeroVoltageAlarmThresholdV { get; set; }
        public double FiniteDividerRatioVPerV { get; set; }
        public double EffectiveZeroVoltageThresholdV { get { return ZeroVoltageAlarmThresholdV > 0 ? Math.Min(ZeroVoltageAlarmThresholdV, ResidualVoltageSafeThresholdV) : ResidualVoltageSafeThresholdV; } }
        public double VoltageGainVPerAoVolt { get; set; }
        public double DividerRatioVPerV { get; set; }
        public double SourceVoltageFeedbackFullScaleV { get; set; }
        public double SourceCurrentFeedbackFullScaleA { get; set; }
        public double CurrentLimitA { get; set; }
        // False preserves the existing fully connected equipment validation path.
        public bool ControlBoxVerification { get; set; }
        public double HardOverCurrentA { get; set; }
        public double AoMaximumVolts { get; set; }
        public double AoTargetHeadroomFraction { get; set; }
        public double TargetReachedToleranceFraction { get; set; }
        public double OverVoltageTripFraction { get; set; }
        public double CurrentLimitEnterFraction { get; set; }
        public double CurrentLimitLeaveFraction { get; set; }
        public double CurrentLimitNoGrowthSeconds { get; set; }
        public double VoltageGrowthResetThresholdV { get; set; }
        public double MaximumRampSeconds { get; set; }
        public double ResidualVoltageSafeThresholdV { get; set; }
        public int PreStartDischargeTimeoutMilliseconds { get; set; }
        public int DischargeVerifyTimeoutMilliseconds { get; set; }

        public string ArtCurrentAiChannel { get; set; }
        public string ArtSourceVoltageAiChannel { get; set; }
        public string ArtDividerAiChannel { get; set; }
        public string ArtControlAoChannel { get; set; }
        public string ArtHvOnDoLine { get; set; }
        public string ArtHvIndicatorDoLine { get; set; }
        public string ArtConnectionIndicatorDoLine { get; set; }
        public string ArtGroundTriggerDoLine { get; set; }
        public string ArtSafetyDiLine { get; set; }
        public string ArtFiniteTriggerTerminal { get; set; }
        public bool ArtUseAnalogTrigger { get; set; }
        public string ArtAnalogTriggerChannel { get; set; }
        public double ArtAnalogTriggerThresholdV { get; set; }
        public bool ArtTriggerFalling { get; set; }
        // Shared deadline from the ground command; includes fixed-segment acquisition completion.
        public int TriggerWaitTimeoutMilliseconds { get; set; }
        public double ArtMonitorInputMinimumV { get; set; }
        public double ArtMonitorInputMaximumV { get; set; }
        public double ArtFiniteSampleRateHz { get; set; }
        public double CaptureDurationSeconds { get; set; }
        public int ArtCaptureTimeoutMilliseconds { get; set; }

        public string PicoSdkLibraryDirectory { get; set; }
        public string PicoSerial { get; set; }
        public int PicoResolutionBits { get; set; }
        public uint PicoTimebase { get; set; }
        public double PicoExpectedSampleIntervalNanoseconds { get; set; }
        public double PicoSampleRateHz { get; set; }
        public int PicoRangeIndex { get; set; }
        public double[] PicoRangeFullScaleVolts { get; set; }
        public double PicoExternalTriggerThresholdV { get; set; }
        public bool PicoTriggerOnChannelA { get; set; }
        public double PicoChannelATriggerThresholdV { get; set; }
        public bool PicoTriggerFalling { get; set; }
        public int PicoCaptureTimeoutMilliseconds { get; set; }

        // Positive means Pico sample zero occurs later than USB3135 sample zero.
        public double PicoStartRelativeToArtNanoseconds { get; set; }

        public double SwitchingTransientSkipMicroseconds { get; set; }
        public double Task6PretriggerMicroseconds { get; set; }
        public bool AutoApplyTask6RangeForNextCycle { get; set; }
        public bool CaptureOnNonEmergencyFault { get; set; }
        public bool CaptureOnEmergencyStop { get; set; }

        [XmlIgnore]
        public int ArtFiniteSampleCount
        {
            get { return checked((int)Math.Round(ArtFiniteSampleRateHz * CaptureDurationSeconds, MidpointRounding.AwayFromZero)); }
        }

        [XmlIgnore]
        public int PicoSampleCount
        {
            get { return checked((int)Math.Round(PicoSampleRateHz * CaptureDurationSeconds, MidpointRounding.AwayFromZero)); }
        }

        [XmlIgnore]
        public double AoHardLimitVolts
        {
            get
            {
                if (!(VoltageGainVPerAoVolt > 0.0)) return 0.0;
                return Math.Min(AoMaximumVolts,
                    TargetVoltageV / VoltageGainVPerAoVolt * (1.0 + AoTargetHeadroomFraction));
            }
        }

        [XmlIgnore]
        public double EffectiveHardOverCurrentA
        {
            get { return HardOverCurrentA > 0.0 ? HardOverCurrentA : CurrentLimitA * 1.10; }
        }

        public string Validate()
        {
            if (String.IsNullOrWhiteSpace(ArtDeviceName)) return "ArtDeviceName cannot be empty.";
            if (String.IsNullOrWhiteSpace(ArtDaqLibraryDirectory)) return "ArtDaqLibraryDirectory cannot be empty.";
            if (SafetyPollMilliseconds != 50) return "SafetyPollMilliseconds must be 50 ms for Task 38.";
            if (HvEnableDelayMilliseconds < 50) return "HvEnableDelayMilliseconds must be at least 50 ms.";
            if (GroundReleaseSettleMilliseconds <= 0) return "GroundReleaseSettleMilliseconds must be positive.";
            if (GroundDelayAfterArmedMilliseconds < 200)
                return "GroundDelayAfterArmedMilliseconds must be at least 200 ms.";
            if (GroundTriggerPulseMilliseconds <= 0) return "GroundTriggerPulseMilliseconds must be positive.";
            if (PreStartDischargeTimeoutMilliseconds <= 0)
                return "PreStartDischargeTimeoutMilliseconds must be positive.";
            if (DischargeVerifyTimeoutMilliseconds <= 0)
                return "DischargeVerifyTimeoutMilliseconds must be positive.";
            if (ArtCaptureTimeoutMilliseconds <= 0) return "ArtCaptureTimeoutMilliseconds must be positive.";
            if (PicoCaptureTimeoutMilliseconds <= 0) return "PicoCaptureTimeoutMilliseconds must be positive.";
            if (TriggerWaitTimeoutMilliseconds < 100 || TriggerWaitTimeoutMilliseconds > 60000)
                return "触发等待超时必须在 0.1～60 秒之间。";
            if (CaptureDurationSeconds * 1000 >= TriggerWaitTimeoutMilliseconds)
                return "触发等待超时必须大于单段时长；期限包含触发等待和固定段采集完成。";
            string terminal = "/" + ArtDeviceName + "/PFI";
            int channel;
            if (!ArtUseAnalogTrigger && (ArtFiniteTriggerTerminal == null || !ArtFiniteTriggerTerminal.StartsWith(terminal, StringComparison.OrdinalIgnoreCase) ||
                !Int32.TryParse(ArtFiniteTriggerTerminal.Substring(terminal.Length), out channel) || channel < 0 || channel > 3))
                return "电压数字触发端口必须属于当前控制模块的 PFI0～PFI3。";
            if (ArtUseAnalogTrigger && (ArtAnalogTriggerChannel == null || !ArtAnalogTriggerChannel.StartsWith("ai", StringComparison.OrdinalIgnoreCase) ||
                !Int32.TryParse(ArtAnalogTriggerChannel.Substring(2), out channel) || channel < 0 || channel > 15))
                return "电压模拟触发端口必须为 ai0～ai15，并核对实际接线。";
            if (ArtUseAnalogTrigger && (!IsFinite(ArtAnalogTriggerThresholdV) || ArtAnalogTriggerThresholdV <= ArtMonitorInputMinimumV || ArtAnalogTriggerThresholdV >= ArtMonitorInputMaximumV))
                return "电压模拟触发阈值必须位于模拟输入量程内部。";

            if (!IsFinite(AoMaximumVolts) || !(AoMaximumVolts > 0.0) || AoMaximumVolts > 5.0)
                return "AoMaximumVolts must be finite and in (0, 5].";
            if (!IsFinite(RampStepAoVolts) || !(RampStepAoVolts > 0.0) || RampStepAoVolts > AoMaximumVolts)
                return "RampStepAoVolts must be > 0 and <= AoMaximumVolts.";
            if (RampStepDelayMilliseconds <= 0) return "RampStepDelayMilliseconds must be positive.";
            if (!IsFinite(TargetVoltageV) || TargetVoltageV < 0.0)
                return "TargetVoltageV must be finite and non-negative; zero selects background capture.";
            if (!IsFinite(ZeroVoltageAlarmThresholdV) || ZeroVoltageAlarmThresholdV < 0 ||
                !IsFinite(FiniteDividerRatioVPerV) || FiniteDividerRatioVPerV < 0)
                return "零压报警阈值及段采集分压比必须是有限非负数，0表示沿用现有设置。";
            if (!IsFinite(VoltageGainVPerAoVolt) || !(VoltageGainVPerAoVolt > 0.0))
                return "VoltageGainVPerAoVolt must be finite, configured and positive.";
            if (!IsFinite(DividerRatioVPerV) || !(DividerRatioVPerV > 0.0))
                return "DividerRatioVPerV must be finite, configured and positive.";
            if (!IsFinite(SourceVoltageFeedbackFullScaleV) || !(SourceVoltageFeedbackFullScaleV > 0.0))
                return "SourceVoltageFeedbackFullScaleV must be finite, configured and positive.";
            if (!IsFinite(SourceCurrentFeedbackFullScaleA) || !(SourceCurrentFeedbackFullScaleA > 0.0))
                return "SourceCurrentFeedbackFullScaleA must be finite, configured and positive.";
            if (!IsFinite(CurrentLimitA) || !(CurrentLimitA > 0.0))
                return "CurrentLimitA must be finite, configured and positive.";
            if (!IsFinite(HardOverCurrentA) || HardOverCurrentA < 0.0)
                return "HardOverCurrentA must be finite and non-negative; zero selects the derived limit.";
            double requiredAoVolts = TargetVoltageV / VoltageGainVPerAoVolt;
            if (!IsFinite(requiredAoVolts) || requiredAoVolts > AoMaximumVolts + 1e-12)
                return "TargetVoltageV is unreachable at the configured voltage gain and AO maximum.";
            if (!IsFinite(AoTargetHeadroomFraction) || AoTargetHeadroomFraction < 0.0 ||
                AoTargetHeadroomFraction > 0.10 + 1e-12)
                return "AoTargetHeadroomFraction must be between 0 and 0.10.";
            if (!IsFinite(TargetReachedToleranceFraction) || TargetReachedToleranceFraction < 0.0 ||
                TargetReachedToleranceFraction >= 0.20)
                return "TargetReachedToleranceFraction must be finite and in [0, 0.20).";
            // This property is the fractional increment in Target * (1 + fraction), not the final multiplier.
            if (!IsFinite(OverVoltageTripFraction) || !(OverVoltageTripFraction > 0.0) ||
                OverVoltageTripFraction > 0.50)
                return "OverVoltageTripFraction must be finite and in (0, 0.50].";
            if (!IsFinite(CurrentLimitEnterFraction) || !IsFinite(CurrentLimitLeaveFraction) ||
                CurrentLimitLeaveFraction < 0.0 ||
                !(CurrentLimitEnterFraction > CurrentLimitLeaveFraction) || CurrentLimitEnterFraction > 1.0)
                return "Current-limit hysteresis is invalid.";
            if (!IsFinite(CurrentLimitNoGrowthSeconds) || !(CurrentLimitNoGrowthSeconds > 0.0) ||
                !IsFinite(VoltageGrowthResetThresholdV) || !(VoltageGrowthResetThresholdV > 0.0))
                return "Current-limit stall timing and voltage-growth threshold must be finite and positive.";
            if (!IsFinite(MaximumRampSeconds) || !(MaximumRampSeconds > 0.0))
                return "MaximumRampSeconds must be finite and positive.";
            if (!IsFinite(ResidualVoltageSafeThresholdV) || !(ResidualVoltageSafeThresholdV > 0.0) ||
                (TargetVoltageV > 0 && !(ResidualVoltageSafeThresholdV < TargetVoltageV)))
                return "ResidualVoltageSafeThresholdV must be finite, positive and below a nonzero TargetVoltageV.";

            if (!IsFinite(ArtMonitorInputMinimumV) || !IsFinite(ArtMonitorInputMaximumV) ||
                !(ArtMonitorInputMinimumV < ArtMonitorInputMaximumV))
                return "ART monitor input limits must be finite and strictly increasing.";
            if (!IsFinite(ArtFiniteSampleRateHz) || !(ArtFiniteSampleRateHz > 0.0))
                return "ART finite sample rate must be finite and positive.";
            if (Math.Abs(ArtFiniteSampleRateHz - 80000.0) > 0.5)
                return "Task 38 requires the USB3135 finite rate to be 80 kS/s.";
            if (!IsFinite(CaptureDurationSeconds) || !(CaptureDurationSeconds > 0.0))
                return "CaptureDurationSeconds must be finite and positive.";
            int artSampleCount;
            int picoSampleCount;
            try
            {
                artSampleCount = ArtFiniteSampleCount;
                picoSampleCount = PicoSampleCount;
            }
            catch (OverflowException)
            {
                return "CaptureDurationSeconds produces a sample count outside the Int32 range.";
            }
            if (artSampleCount < 2) return "ART finite acquisition settings are invalid.";
            if (PicoResolutionBits != 14 && PicoResolutionBits != 15) return "125 MS/s 局放采集仅支持14或15 bit模式；16 bit不能达到此采样率。";
            if (PicoTimebase != 3) return "PicoScope 5242D 125 MS/s single-channel block acquisition requires timebase 3.";
            if (!IsFinite(PicoExpectedSampleIntervalNanoseconds) ||
                !(PicoExpectedSampleIntervalNanoseconds > 0.0))
                return "PicoExpectedSampleIntervalNanoseconds must be finite and positive.";
            if (!IsFinite(PicoSampleRateHz) || !(PicoSampleRateHz > 0.0))
                return "PicoSampleRateHz must be finite and positive.";
            if (Math.Abs(PicoSampleRateHz - 125000000.0) > 1.0)
                return "Task 38 requires PicoScope A at 125 MS/s.";
            if (PicoRangeFullScaleVolts == null ||
                PicoRangeFullScaleVolts.Length != Pico5242DFullScaleVolts.Length)
                return "PicoRangeFullScaleVolts must contain the 11 supported 5242D ranges from 10 mV to 20 V.";
            for (int i = 0; i < PicoRangeFullScaleVolts.Length; ++i)
            {
                if (!IsFinite(PicoRangeFullScaleVolts[i]) ||
                    PicoRangeFullScaleVolts[i] != Pico5242DFullScaleVolts[i])
                    return "PicoRangeFullScaleVolts must exactly match the fixed PicoScope 5242D hardware range table " +
                        "[0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20] V.";
            }
            if (PicoRangeIndex < 0 || PicoRangeIndex >= PicoRangeFullScaleVolts.Length)
                return "PicoRangeIndex is outside the supported range table.";
            if (PicoTriggerOnChannelA && (!IsFinite(PicoChannelATriggerThresholdV) || Math.Abs(PicoChannelATriggerThresholdV) >= PicoRangeFullScaleVolts[PicoRangeIndex]))
                return "局放 A 通道触发阈值必须位于当前量程内部。";
            if (PicoTriggerOnChannelA && AutoApplyTask6RangeForNextCycle)
                return "A 通道自触发需关闭自动应用量程，防止下一轮量程改变导致阈值无效；同步采集推荐 EXT。";
            if (!IsFinite(PicoExternalTriggerThresholdV) ||
                !(PicoExternalTriggerThresholdV > -5.0 && PicoExternalTriggerThresholdV < 5.0))
                return "PicoExternalTriggerThresholdV must be between -5 V and +5 V.";
            if (!IsFinite(PicoStartRelativeToArtNanoseconds))
                return "PicoStartRelativeToArtNanoseconds must be finite.";
            if (!IsFinite(SwitchingTransientSkipMicroseconds) || SwitchingTransientSkipMicroseconds < 0.0)
                return "SwitchingTransientSkipMicroseconds must be finite and non-negative.";
            if (!IsFinite(Task6PretriggerMicroseconds) || Task6PretriggerMicroseconds < 0.0)
                return "Task6PretriggerMicroseconds must be finite and non-negative.";
            if (picoSampleCount < 2 || picoSampleCount > 64000000)
                return "Pico block sample count is outside the 5242D memory budget used by Task 38.";
            return String.Empty;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        public Task38Configuration Clone()
        {
            Task38Configuration copy = (Task38Configuration)MemberwiseClone();
            copy.PicoRangeFullScaleVolts = PicoRangeFullScaleVolts == null
                ? null : (double[])PicoRangeFullScaleVolts.Clone();
            return copy;
        }

        public void SaveXml(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A configuration path is required.", "path");
            XmlSerializer serializer = new XmlSerializer(typeof(Task38Configuration));
            using (FileStream stream = File.Create(path)) serializer.Serialize(stream, this);
        }

        public static Task38Configuration LoadXml(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A configuration path is required.", "path");
            XmlSerializer serializer = new XmlSerializer(typeof(Task38Configuration));
            using (FileStream stream = File.OpenRead(path)) return (Task38Configuration)serializer.Deserialize(stream);
        }
    }

    public sealed class FeedbackSample
    {
        public DateTime TimestampUtc { get; set; }
        public double CurrentFeedbackRawV { get; set; }
        public double SourceVoltageFeedbackRawV { get; set; }
        public double DividerFeedbackRawV { get; set; }
        public double CurrentA { get; set; }
        public double SourceVoltageV { get; set; }
        public double DividerVoltageV { get; set; }
    }

    public sealed class Task38StatusSnapshot
    {
        public bool ControlBoxVerification { get; internal set; }
        public double DividerFeedbackRawV { get; internal set; }
        public double SourceVoltageFeedbackRawV { get; internal set; }
        public double CurrentFeedbackRawV { get; internal set; }
        public Task38ControllerState State { get; internal set; }
        public bool Initialized { get; internal set; }
        public bool CycleRunning { get; internal set; }
        public bool SafetyReady { get; internal set; }
        public bool StartAllowed { get; internal set; }
        public bool EmergencyLatched { get; internal set; }
        public double AoCommandVolts { get; internal set; }
        public double ActualVoltageV { get; internal set; }
        public double SourceVoltageFeedbackV { get; internal set; }
        public double CurrentA { get; internal set; }
        public int PicoRangeIndex { get; internal set; }
        public string StateMessage { get; internal set; }
        public string LastError { get; internal set; }
        public DateTime UpdatedUtc { get; internal set; }
    }

    public sealed class PicoBlockCapture
    {
        public int ResolutionModeBits { get; set; }
        public int RangeHardwareBits { get; set; }
        public short[] RawAdc { get; set; }
        public int SampleCount { get; set; }
        public double SampleIntervalNanoseconds { get; set; }
        public short MaximumAdc { get; set; }
        public double FullScaleVolts { get; set; }
        public bool HardwareOverflow { get; set; }
        public long TriggerTimeOffset { get; set; }
        public int TriggerTimeUnitCode { get; set; }
        public string Variant { get; set; }
        public string Serial { get; set; }
    }

    public sealed class Task6InputBundle
    {
        public double[] VoltageRawVolts { get; internal set; }
        public double VoltageSampleRateHz { get; internal set; }
        public double[] PdRawVolts { get; internal set; }
        public double PdSampleRateHz { get; internal set; }
        public double PretriggerMicroseconds { get; internal set; }
        public double TransientSkipMicroseconds { get; internal set; }
        public double[] PdRangeValuesVolts { get; internal set; }
        public int CurrentPdRangeIndex { get; internal set; }
        public double AppliedPicoToArtOffsetNanoseconds { get; internal set; }
    }

    public sealed class Task6RangeFeedback
    {
        public bool Task6Success { get; set; }
        public int Task6Status { get; set; }
        public string ErrorMessage { get; set; }
        public int QualityFlags { get; set; }
        public bool PdOverRange { get; set; }
        public bool MultipleClippedPulses { get; set; }
        public bool RangeSuitable { get; set; }
        public int RangeAdjustmentDirection { get; set; }
        public int BestRangeIndex { get; set; }
        public double PdMaximumAbsolute { get; set; }
        public double FirstZeroTimeSeconds { get; set; }
    }

    public sealed class Task38CycleResult
    {
        public string RecordId { get; internal set; }
        public Task38CycleResult()
        {
            ErrorMessage = String.Empty;
            UsbDividerRawVolts = new double[0];
            PicoRawAdc = new short[0];
            RepeatPressurizationMessage = String.Empty;
            SuggestedPicoRangeIndex = -1;
            FirstZeroTimeSeconds = Double.NaN;
        }

        public bool Success { get; internal set; }
        public bool IsBackgroundCapture { get; internal set; }
        public Task38ExitReason ExitReason { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public DateTime StartedUtc { get; internal set; }
        public DateTime CompletedUtc { get; internal set; }
        public double PeakActualVoltageV { get; internal set; }
        public double PeakCurrentA { get; internal set; }
        public double FinalAoCommandVolts { get; internal set; }
        public double AoHardLimitVolts { get; internal set; }
        public double[] UsbDividerRawVolts { get; internal set; }
        public double UsbSampleRateHz { get; internal set; }
        public short[] PicoRawAdc { get; internal set; }
        public double PicoSampleRateHz { get; internal set; }
        public double PicoSampleIntervalNanoseconds { get; internal set; }
        public int PicoResolutionModeBits { get; internal set; }
        public int PicoRangeHardwareBits { get; internal set; }
        public short PicoMaximumAdc { get; internal set; }
        public double PicoFullScaleVolts { get; internal set; }
        public int PicoRangeIndexUsed { get; internal set; }
        public bool PicoHardwareOverflowDiagnosticOnly { get; internal set; }
        public string PicoVariant { get; internal set; }
        public string PicoSerial { get; internal set; }
        public bool BothDevicesArmedBeforeGroundTrigger { get; internal set; }
        public DateTime SourceOffUtc { get; internal set; }
        public DateTime GroundTriggerUtc { get; internal set; }

        public bool Task6AssessmentApplied { get; internal set; }
        public bool Task6RangeSuitable { get; internal set; }
        public bool RepeatPressurizationRequired { get; internal set; }
        public string RepeatPressurizationMessage { get; internal set; }
        public int SuggestedPicoRangeIndex { get; internal set; }
        public double FirstZeroTimeSeconds { get; internal set; }
        public int Task6QualityFlags { get; internal set; }

        public Task6InputBundle BuildTask6Input(Task38Configuration configuration)
        {
            if (IsBackgroundCapture) throw new InvalidOperationException("零电压背景不可作为振荡检波输入。");
            if (configuration == null) throw new ArgumentNullException("configuration");
            if (PicoRawAdc == null || PicoRawAdc.Length == 0)
                throw new InvalidOperationException("No Pico block capture is available.");
            if (PicoMaximumAdc <= 0 || !(PicoFullScaleVolts > 0.0))
                throw new InvalidOperationException("Pico scaling metadata is invalid.");

            double[] volts = new double[PicoRawAdc.Length];
            double scale = PicoFullScaleVolts / PicoMaximumAdc;
            for (int i = 0; i < volts.Length; ++i) volts[i] = PicoRawAdc[i] * scale;

            int shift = (int)Math.Round(configuration.PicoStartRelativeToArtNanoseconds *
                1.0e-9 * PicoSampleRateHz, MidpointRounding.AwayFromZero);
            if (shift != 0)
            {
                double[] aligned = new double[volts.Length];
                if (shift > 0)
                {
                    int copy = Math.Max(0, volts.Length - shift);
                    if (copy > 0) Array.Copy(volts, 0, aligned, shift, copy);
                }
                else
                {
                    int source = Math.Min(volts.Length, -shift);
                    int copy = Math.Max(0, volts.Length - source);
                    if (copy > 0) Array.Copy(volts, source, aligned, 0, copy);
                }
                volts = aligned;
            }

            return new Task6InputBundle
            {
                VoltageRawVolts = UsbDividerRawVolts == null ? new double[0] : (double[])UsbDividerRawVolts.Clone(),
                VoltageSampleRateHz = UsbSampleRateHz,
                PdRawVolts = volts,
                PdSampleRateHz = PicoSampleRateHz,
                PretriggerMicroseconds = configuration.Task6PretriggerMicroseconds,
                TransientSkipMicroseconds = configuration.SwitchingTransientSkipMicroseconds,
                PdRangeValuesVolts = (double[])configuration.PicoRangeFullScaleVolts.Clone(),
                CurrentPdRangeIndex = PicoRangeIndexUsed,
                AppliedPicoToArtOffsetNanoseconds = configuration.PicoStartRelativeToArtNanoseconds
            };
        }
    }
}
