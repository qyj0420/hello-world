using System;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>Pico 块采集长度配置。Field≈现网 60 ms@125 MS/s（7.5M 点），默认 Short 保证 Smoke 快。</summary>
    public enum PicoCaptureProfile
    {
        Short = 0,
        Field = 1
    }

    /// <summary>实机驱动/端子/联锁阈值。端子默认对齐 docs/art-db37-pinout.md 与 Task38Contracts。</summary>
    public sealed class LiveHardwareOptions
    {
        public string ArtDriverDirectory { get; set; }
        public string PicoSdkDirectory { get; set; }
        /// <summary>
        /// true（默认）= OpenUnit 前调用 ps5000aApplyFix 抑制官方启动/欢迎窗；仅 SDK，不启 PicoScope.exe。
        /// </summary>
        public bool SuppressPicoStartupWindow { get; set; }
        public string ArtDeviceName { get; set; }
        public string PicoSerial { get; set; }
        /// <summary>期望 ART 身份（设备名或序列号）；空=不强制。双身份校验用。</summary>
        public string ExpectedArtIdentity { get; set; }
        /// <summary>期望 Pico 序列号；空=不强制。与 Open 目标 PicoSerial 可相同。</summary>
        public string ExpectedPicoSerial { get; set; }
        public int PicoResolutionBits { get; set; }

        public string ConnectionIndicatorDoLine { get; set; } // P0.5
        public string SafetyDiLine { get; set; }              // P0.4
        public string HvOnDoLine { get; set; }                // P0.0
        public string HvIndicatorDoLine { get; set; }         // P0.3
        public string GroundTriggerDoLine { get; set; }       // P0.7
        public string ControlAoChannel { get; set; }          // ao0
        public string CurrentAiChannel { get; set; }          // ai0
        public string SourceVoltageAiChannel { get; set; }    // ai2
        public string DividerAiChannel { get; set; }          // ai4

        /// <summary>
        /// true（默认）= P0.4 物理高表示急停解除/安全就绪（有效低按下）；
        /// false = 物理高表示急停按下。对照 docs/art-db37-pinout.md。
        /// </summary>
        public bool SafetyReadyActiveHigh { get; set; }
        public double AoMaximumVolts { get; set; }
        public double RampStepAoVolts { get; set; }
        public int RampStepDelayMilliseconds { get; set; }
        public double MaximumRampSeconds { get; set; }
        public double VoltageGainVPerAoVolt { get; set; }
        public double CurrentLimitA { get; set; }
        public double HardOverCurrentA { get; set; }
        public double OverVoltageTripFraction { get; set; }
        public double TargetReachedToleranceFraction { get; set; }
        public double ArtMonitorInputMinimumV { get; set; }
        public double ArtMonitorInputMaximumV { get; set; }

        // —— Pico 5242D 块采集（对照 Task38Configuration）——
        public uint PicoTimebase { get; set; }
        public double PicoExpectedSampleIntervalNanoseconds { get; set; }
        public int PicoSampleCount { get; set; }
        public PicoCaptureProfile CaptureProfile { get; set; }
        /// <summary>默认振荡/局放同窗采集时长（ms）。</summary>
        public const int OscillationCaptureDurationMilliseconds = 60;
        public const int MinOscillationCaptureDurationMilliseconds = 10;
        public const int MaxOscillationCaptureDurationMilliseconds = 120;
        /// <summary>60ms@125MS/s 参考点数。</summary>
        public const int FieldPicoSampleCount = 7500000; // 60ms@125MS/s (8ns)
        /// <summary>可配时长上限对应点数（120ms@125MS/s）。</summary>
        public const int MaxPicoSampleCount = 15000000;
        public const int ShortPicoSampleCount = 125000;
        public const int FieldArtFiniteSampleCount = 4800; // 60ms@80kS/s
        public const int ShortArtFiniteSampleCount = 4800; // 60ms@80kS/s
        /// <summary>当前触发采集振荡/局放长度（ms）；写入 PicoN/ART_N 并生效。默认 60。</summary>
        public double OscillationCaptureDurationMs { get; set; }
        public int PicoRangeIndex { get; set; }
        public double PicoExternalTriggerThresholdV { get; set; }
        /// <summary>0 = 等外触发；&gt;0 = 自动触发毫秒（台架无触发源时用）。</summary>
        public short PicoAutoTriggerMilliseconds { get; set; }
        /// <summary>触发位置 0–100（%）：0=窗起点（全 post），50=窗中点，100=窗末端（几乎全 pre）。默认 0。</summary>
        public int PicoTriggerPositionPercent { get; set; }
        public int PicoCaptureTimeoutMilliseconds { get; set; }

        public string ArtFiniteTriggerTerminal { get; set; }
        public double ArtFiniteSampleRateHz { get; set; }
        public int ArtFiniteSampleCount { get; set; }
        public int ArtCaptureTimeoutMilliseconds { get; set; }
        /// <summary>残压安全阈值（工程电压，分压器换算后）。</summary>
        public double ResidualVoltageSafeThresholdV { get; set; }
        public int DischargeVerifyTimeoutMilliseconds { get; set; }
        public int SafetyPollMilliseconds { get; set; }
        /// <summary>P0.4 独立请求节拍（ms），默认 10；非硬实时。</summary>
        public int EStopPollIntervalMilliseconds { get; set; }
        /// <summary>P0.4 单次 SDK/主机读超时上限（ms），默认 300；其他 AI/AO/DO 仍用 20 ms。</summary>
        public int EStopReadTimeoutMilliseconds { get; set; }
        /// <summary>HV ON 后 AO=0 时 VMON/AI4 启动背景容差（工程 V），默认 500；可配。</summary>
        public double ZeroBackgroundLimitV { get; set; }
        /// <summary>连续加压成功后档间冷却（ms），默认 2000；不作为放电确认。</summary>
        public int InterRoundCooldownMilliseconds { get; set; }
        /// <summary>独立绝对过压上限（工程 V）；≤0 表示仅用 OverVoltageTripFraction。</summary>
        public double AbsoluteOverVoltageLimitV { get; set; }
        public double DividerRatioVPerV { get; set; }
        /// <summary>AI0：A = rawV × CurrentScaleAPerVolt。默认 1=占位。</summary>
        public double CurrentScaleAPerVolt { get; set; }
        /// <summary>AI2 源反馈比例；0=未配，Live 残压路径可回退 VoltageGain。</summary>
        public double SourceVoltageScaleVPerV { get; set; }
        /// <summary>已从台架配置写入且通过危险占位检查；默认 false。</summary>
        public bool BenchCalibrationApplied { get; set; }
        public string BenchCalibrationSource { get; set; }
        public static readonly double[] Pico5242DFullScaleVolts = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0 };

        public LiveHardwareOptions()
        {
            ArtDriverDirectory = string.Empty;
            PicoSdkDirectory = string.Empty;
            SuppressPicoStartupWindow = true;
            ArtDeviceName = "Dev1";
            PicoSerial = string.Empty;
            ExpectedArtIdentity = string.Empty;
            ExpectedPicoSerial = string.Empty;
            PicoResolutionBits = 14;
            ConnectionIndicatorDoLine = "port0/line5";
            SafetyDiLine = "port0/line4";
            HvOnDoLine = "port0/line0";
            HvIndicatorDoLine = "port0/line3";
            GroundTriggerDoLine = "port0/line7";
            ControlAoChannel = "ao0";
            CurrentAiChannel = "ai0";
            SourceVoltageAiChannel = "ai2";
            DividerAiChannel = "ai4";
            SafetyReadyActiveHigh = true;
            AoMaximumVolts = 5.0;
            RampStepAoVolts = 0.05;
            RampStepDelayMilliseconds = 50;
            MaximumRampSeconds = 120;
            VoltageGainVPerAoVolt = 1000; // 台架须按分压器标定覆盖
            CurrentLimitA = 0.5;
            HardOverCurrentA = 1.0;
            OverVoltageTripFraction = 0.20; // 异常过压默认 +20%（可配 >5%～+50%）
            TargetReachedToleranceFraction = 0.05; // 达压有效窗口 ±5%
            ArtMonitorInputMinimumV = -5; // 台架监控 0–5V，勿用 ±10V
            ArtMonitorInputMaximumV = 5;
            PicoTimebase = 3;                         // 8 ns @14/15 bit
            PicoExpectedSampleIntervalNanoseconds = 8.0;
            CaptureProfile = PicoCaptureProfile.Short;
            OscillationCaptureDurationMs = OscillationCaptureDurationMilliseconds;
            PicoSampleCount = ShortPicoSampleCount;
            PicoRangeIndex = 6;                       // ±1 V 初始（自动量程）
            PicoExternalTriggerThresholdV = 3.0; // 用户规定触发值=3
            // 无 EXT 时自触发上限；有边沿立刻返回。原 3000ms 会在达压关源后造成约 3s「平台」观感（AI4 占用无法刷反馈）
            PicoAutoTriggerMilliseconds = 800;
            PicoTriggerPositionPercent = 0;
            PicoCaptureTimeoutMilliseconds = 1000; // ≥ AutoTrigger；边沿到即返回
            ArtFiniteTriggerTerminal = "/Dev1/PFI0";
            ArtFiniteSampleRateHz = 80000;
            // 振荡持续采集 60ms @ 80 kS/s → 4800 点（与开关导通保持 1s 独立）
            ArtFiniteSampleCount = (int)(ArtFiniteSampleRateHz * 0.060 + 0.5); // =4800
            ArtCaptureTimeoutMilliseconds = 1000;
            ResidualVoltageSafeThresholdV = 50.0;
            DischargeVerifyTimeoutMilliseconds = 2000; // 骨架；现网 10 s
            SafetyPollMilliseconds = 10;
            EStopPollIntervalMilliseconds = 10;
            EStopReadTimeoutMilliseconds = 300;
            ZeroBackgroundLimitV = 500.0;
            InterRoundCooldownMilliseconds = 2000;
            AbsoluteOverVoltageLimitV = 0;
            DividerRatioVPerV = 1.0; // 台架标定前 raw≈工程值（危险占位）
            CurrentScaleAPerVolt = 1.0; // 占位；台架写入
            SourceVoltageScaleVPerV = 0;
            BenchCalibrationApplied = false;
            BenchCalibrationSource = string.Empty;
        }

        /// <summary>按 Profile 设置 Pico/ART 样本数。Field 会占用大量内存；落盘受 Persister.MaxRawSamples 限制。</summary>
        /// <summary>按当前 timebase 间隔计算指定时长（ms）的 Pico 点数，封顶 Field 长度。</summary>
        public static double ClampOscillationCaptureDurationMs(double durationMs)
        {
            if (double.IsNaN(durationMs) || double.IsInfinity(durationMs) || durationMs <= 0)
                return OscillationCaptureDurationMilliseconds;
            if (durationMs < MinOscillationCaptureDurationMilliseconds)
                return MinOscillationCaptureDurationMilliseconds;
            if (durationMs > MaxOscillationCaptureDurationMilliseconds)
                return MaxOscillationCaptureDurationMilliseconds;
            return durationMs;
        }

        public static int PicoSampleCountForDurationMs(double durationMs, double intervalNanoseconds)
        {
            if (durationMs <= 0 || intervalNanoseconds <= 0) return ShortPicoSampleCount;
            double n = durationMs * 1e6 / intervalNanoseconds; // ms→ns
            if (n < 1) n = 1;
            if (n > MaxPicoSampleCount) n = MaxPicoSampleCount;
            return (int)(n + 0.5);
        }

        public void ApplyCaptureProfile(PicoCaptureProfile profile)
        {
            CaptureProfile = profile;
            double interval = PicoExpectedSampleIntervalNanoseconds > 0
                ? PicoExpectedSampleIntervalNanoseconds : 8.0;
            double dur = ClampOscillationCaptureDurationMs(OscillationCaptureDurationMs > 0
                ? OscillationCaptureDurationMs : OscillationCaptureDurationMilliseconds);
            OscillationCaptureDurationMs = dur;
            if (profile == PicoCaptureProfile.Field)
            {
                PicoSampleCount = PicoSampleCountForDurationMs(dur, interval);
                if (ArtFiniteSampleRateHz <= 0) ArtFiniteSampleRateHz = 80000;
                ArtFiniteSampleCount = (int)(ArtFiniteSampleRateHz * (dur / 1000.0) + 0.5);
            }
            else
            {
                // Smoke/短采：仍短 Pico；ART 保持当前振荡窗时长
                PicoSampleCount = ShortPicoSampleCount;
                if (ArtFiniteSampleRateHz <= 0) ArtFiniteSampleRateHz = 80000;
                ArtFiniteSampleCount = (int)(ArtFiniteSampleRateHz * (dur / 1000.0) + 0.5);
            }
        }

        /// <summary>按当前采样间隔把 Pico/ART 点数对齐到 OscillationCaptureDurationMs（Field 语义）。</summary>
        public void ApplyPicoOscillationWindow60Ms()
        {
            ApplyPicoOscillationWindow(OscillationCaptureDurationMs);
        }

        /// <summary>按指定时长（ms）写入 PicoN/ART_N 并生效。</summary>
        public void ApplyPicoOscillationWindow(double durationMs)
        {
            double dur = ClampOscillationCaptureDurationMs(durationMs);
            OscillationCaptureDurationMs = dur;
            CaptureProfile = PicoCaptureProfile.Field;
            double interval = PicoExpectedSampleIntervalNanoseconds > 0
                ? PicoExpectedSampleIntervalNanoseconds : 8.0;
            PicoSampleCount = PicoSampleCountForDurationMs(dur, interval);
            if (ArtFiniteSampleRateHz <= 0) ArtFiniteSampleRateHz = 80000;
            ArtFiniteSampleCount = (int)(ArtFiniteSampleRateHz * (dur / 1000.0) + 0.5);
        }

        public string CaptureProfileRiskNote()
        {
            if (CaptureProfile != PicoCaptureProfile.Field) return "Short 采集：" + PicoSampleCount + " 点。";
            double dur = ClampOscillationCaptureDurationMs(OscillationCaptureDurationMs > 0
                ? OscillationCaptureDurationMs : OscillationCaptureDurationMilliseconds);
            return "Field 采集：PicoSampleCount=" + PicoSampleCount
                + "（" + dur.ToString("0.###") + " ms 振荡同窗）。内存峰值≈" + (PicoSampleCount * 2 / (1024 * 1024)) + " MB（int16）；"
                + "超过 MaxRawSamples 时默认分块旁路落盘（FieldWaveformChunkStore）+ 降采样预览；"
                + "WriteChunkedWhenOversized=false 时仅 previewOnly。台架确认内存与磁盘后再用。";
        }

        /// <summary>Live 非零加压是否被未台架标定门拒绝。</summary>
        public bool BlocksLiveNonZeroEnergize()
        {
            return !AllowsLiveNonZeroEnergize();
        }

        public bool AllowsLiveNonZeroEnergize()
        {
            if (!BenchCalibrationApplied) return false;
            if (LiveBenchCalibration.IsDangerousPlaceholderVoltageGain(VoltageGainVPerAoVolt)) return false;
            if (!(DividerRatioVPerV > 0) || double.IsNaN(DividerRatioVPerV) || double.IsInfinity(DividerRatioVPerV)) return false;
            if (!(CurrentScaleAPerVolt > 0) || double.IsNaN(CurrentScaleAPerVolt)) return false;
            return true;
        }

        /// <summary>
        /// 从台架标定对象写入增益/分压/电流比例。要求 BenchCalibrated=true 且增益非危险占位 1000。
        /// </summary>
        public bool TryApplyBenchCalibration(LiveBenchCalibration cal, out string error)
        {
            if (cal == null)
            {
                error = "台架标定对象为空。";
                return false;
            }
            if (!cal.BenchCalibrated)
            {
                error = StatusCopyBench() + "（配置 benchCalibrated=false）";
                return false;
            }
            if (!cal.AllowsLiveNonZeroEnergize())
            {
                error = StatusCopyBench() + "（增益/分压/电流比例仍为危险占位或无效）";
                return false;
            }
            VoltageGainVPerAoVolt = cal.VoltageGainVPerAoVolt;
            DividerRatioVPerV = cal.DividerRatioVPerV;
            CurrentScaleAPerVolt = cal.CurrentScaleAPerVolt;
            if (cal.SourceVoltageScaleVPerV > 0)
                SourceVoltageScaleVPerV = cal.SourceVoltageScaleVPerV;
            BenchCalibrationApplied = true;
            BenchCalibrationSource = string.IsNullOrEmpty(cal.Notes) ? (cal.CalibratedBy ?? "bench") : cal.Notes;
            error = null;
            return true;
        }

        public void ApplyBenchCalibration(LiveBenchCalibration cal)
        {
            string error;
            if (!TryApplyBenchCalibration(cal, out error))
                throw new InvalidOperationException(error ?? StatusCopyBench());
        }

        /// <summary>从工程配置 JSON 文件写入；失败抛错。</summary>
        public void ApplyBenchCalibrationFile(string path)
        {
            ApplyBenchCalibration(LiveBenchCalibration.LoadFile(path));
            BenchCalibrationSource = path;
        }

        static string StatusCopyBench()
        {
            return Xiongxin.Owts.Contracts.Ui.StatusCopy.BenchGainNotCalibrated;
        }
        /// <summary>将异常过压比例夹到 (0.05, 0.50]；缺省/非法回 0.20。</summary>

        /// <summary>目标采样率 → Pico timebase（14/15bit：tb3=125MS/s, tb4=62.5, tb5=31.25）。与加压/校准共用，勿另开一套。</summary>
        public static uint ResolvePicoTimebase(double targetSampleRateHz)
        {
            if (targetSampleRateHz >= 100e6) return 3;   // 125 MS/s → 8 ns
            if (targetSampleRateHz >= 50e6) return 4;    // 62.5 MS/s → 16 ns
            return 5;                                    // 31.25 MS/s → 32 ns
        }

        /// <summary>timebase → 期望采样间隔（ns）。与 ArtPicoLiveBackend GetTimebase2 校验一致。</summary>
        public static double SampleIntervalNanosecondsFromTimebase(uint timebase)
        {
            if (timebase <= 3) return 8.0;
            if (timebase == 4) return 16.0;
            return 32.0;
        }

        /// <summary>写入 timebase + 期望间隔（加压 EXT Block 与校准 Rapid 共用）。</summary>
        public void ApplyPicoTimebase(uint timebase)
        {
            PicoTimebase = timebase > 0 ? timebase : 3;
            PicoExpectedSampleIntervalNanoseconds = SampleIntervalNanosecondsFromTimebase(PicoTimebase);
        }

        /// <summary>按目标采样率设置 timebase/间隔。</summary>
        public void ApplyPicoTargetSampleRate(double targetSampleRateHz)
        {
            ApplyPicoTimebase(ResolvePicoTimebase(targetSampleRateHz > 0 ? targetSampleRateHz : 125e6));
        }

        public static double ClampOverVoltageTripFraction(double fraction)
        {
            if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction <= 0.05)
                return 0.20;
            if (fraction > 0.50) return 0.50;
            return fraction;
        }

        /// <summary>达压容差：非法则 0.05。</summary>
        public static double ClampTargetReachedToleranceFraction(double fraction)
        {
            if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction <= 0)
                return 0.05;
            if (fraction > 0.20) return 0.20;
            return fraction;
        }
        /// <summary>触发位置百分比夹到 [0,100]。</summary>
        public static int ClampTriggerPositionPercent(int percent)
        {
            if (percent < 0) return 0;
            if (percent > 100) return 100;
            return percent;
        }

        /// <summary>按触发位置拆 pre/post；总和 = totalSamples；percent=50 → 触发在 50%。</summary>
        public static void SplitTriggerSamples(int totalSamples, int positionPercent, out int preTriggerSamples, out int postTriggerSamples)
        {
            int n = totalSamples < 2 ? 2 : totalSamples;
            int pct = ClampTriggerPositionPercent(positionPercent);
            int pre = (int)((long)n * pct / 100L);
            if (pre >= n) pre = n - 1;
            if (pre < 0) pre = 0;
            int post = n - pre;
            if (post < 1) { post = 1; pre = n - 1; }
            preTriggerSamples = pre;
            postTriggerSamples = post;
        }

    }
}
