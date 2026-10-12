using System;
using System.IO;
using System.Threading;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 校准采集（示波器式）：阻抗 → ChannelA Rapid 自适量程找最佳档 →
    /// ChannelA 自动触发（低电平+auto 兜底，不用 50%FS 严电平）→ 固定间隔筛选 → N 平均 → SetCalibrated。
    /// 禁止走加压 EXT/开关导通单段路径（否则注入校准脉冲采不到峰）。
    /// </summary>
    public sealed class CalibrationPulseAcquisitionRunner
    {
        /// <summary>探采/终采自动触发辅助电平（相对满量程）；仅辅助边沿，勿用 50%FS。</summary>
        public const double CalibrationAutoTriggerLevelFraction = 0.01;

        /// <summary>兼容旧名：同 CalibrationAutoTriggerLevelFraction。</summary>
        public const double ProbeTriggerFractionOfFullScale = CalibrationAutoTriggerLevelFraction;

        /// <summary>探采段数（够测峰即可）。</summary>
        public const int ProbeCaptureCount = 4;

        /// <summary>探采自动触发超时 ms（无边沿则 auto；有脉冲边沿仍优先）。</summary>
        public const short ProbeAutoTriggerMilliseconds = 100;

        /// <summary>终采自动触发超时 ms；覆盖 &gt;1 个 50Hz 周期，避免空等。</summary>
        public const short CalibrationAutoTriggerMilliseconds = 100;

        public CalibrationPulseAcquisitionResult Run(
            LiveAcquisitionSession session,
            CalibrationPulseRequest request,
            TextWriter log,
            CancellationToken cancellation)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (!session.IsOpen) throw new InvalidOperationException("会话未 Open，无法校准采集。");
            request = request ?? new CalibrationPulseRequest();
            log = log ?? TextWriter.Null;

            var result = new CalibrationPulseAcquisitionResult();
            ILiveHardwareBackend backend = session.Backend;
            if (backend == null) throw new InvalidOperationException("无 Live 后端，无法 Rapid 校准采集。");

            // 1) 阻抗
            session.SetImpedanceBandwidthWide(request.ImpedanceBandwidthWide);
            log.WriteLine("校准采集：阻抗带宽=" + (request.ImpedanceBandwidthWide ? "宽" : "窄")
                + "；路径=ChannelA Rapid 自适量程→最佳档→自动触发（不走 EXT/开关导通，不用 50%FS）");

            // 2) 时序与采样设定（与加压 Live Pico 一致）
            int rangeIndex = request.InitialPicoRangeIndex >= 0
                ? request.InitialPicoRangeIndex
                : 6;
            int maxRetries = request.MaxRangeRetries >= 0 ? request.MaxRangeRetries : 2;
            int resolutionBits = request.PicoResolutionBits > 0 ? request.PicoResolutionBits : 14;
            uint timebase = request.PicoTimebase > 0 ? request.PicoTimebase : 3;
            double targetHz = request.PicoTargetSampleRateHz > 0 ? request.PicoTargetSampleRateHz : 125e6;

            session.ApplyPicoCaptureSettings(resolutionBits, timebase, targetHz, rangeIndex);
            LiveHardwareOptions hw = session.HardwareOptions;
            double intervalNs = hw != null && hw.PicoExpectedSampleIntervalNanoseconds > 0
                ? hw.PicoExpectedSampleIntervalNanoseconds
                : (request.SampleIntervalNanoseconds > 0
                    ? request.SampleIntervalNanoseconds
                    : LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(timebase));
            if (hw != null && hw.PicoResolutionBits > 0)
                resolutionBits = hw.PicoResolutionBits;

            CalibrationCaptureTimingResult timing = CalibrationCaptureTiming.Compute(
                request.CableLengthM > 0 ? request.CableLengthM : CalibrationCaptureTiming.DefaultCableLengthM,
                request.VelocityMPerUs > 0 ? request.VelocityMPerUs : CalibrationCaptureTiming.DefaultVelocityMPerUs,
                intervalNs);
            result.Timing = timing;

            // 3) ChannelA 低阈值 Rapid 探采 → Task6 自适到最佳档（禁止 RunCycle/EXT）
            Task6RangeAdvice lastAdvice = null;
            PicoRapidBlockCaptureResult lastProbeRapid = null;
            for (int attempt = 1; attempt <= maxRetries + 1; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                session.ApplyPicoCaptureSettings(resolutionBits, timebase, targetHz, rangeIndex);

                double probeTrigV = PicoRapidBlockBulkOverlapped.ChannelATriggerVoltsFromRange(
                    rangeIndex, CalibrationAutoTriggerLevelFraction);
                var probeArm = new RapidBlockArmRequest
                {
                    RangeIndex = rangeIndex,
                    CaptureCount = ProbeCaptureCount,
                    PreTriggerSamples = timing.PreTriggerSamples,
                    PostTriggerSamples = timing.PostTriggerSamples,
                    ChannelATriggerThresholdVolts = probeTrigV,
                    AutoTriggerMilliseconds = ProbeAutoTriggerMilliseconds
                };
                log.WriteLine("校准探采 Rapid（ChannelA）：attempt=" + attempt
                    + "/" + (maxRetries + 1)
                    + " range=" + rangeIndex
                    + " FS=±" + LiveHardwareOptions.Pico5242DFullScaleVolts[rangeIndex] + " V"
                    + " trigA=" + probeTrigV.ToString("0.####") + " V (~"
                    + (CalibrationAutoTriggerLevelFraction * 100).ToString("0.#") + "% FS 自动触发)"
                    + " autoMs=" + ProbeAutoTriggerMilliseconds);

                PicoRapidBlockCaptureResult probeRapid;
                try
                {
                    backend.ArmRapidBlockCapture(probeArm, null);
                    int probeTimeout = Math.Max(4000, ProbeCaptureCount * (ProbeAutoTriggerMilliseconds + 500));
                    probeRapid = backend.ReadRapidBlockCaptures(probeTimeout, cancellation);
                }
                catch (OperationCanceledException ex)
                {
                    try { backend.AbortRapidBlockCapture(); } catch { }
                    result.Success = false;
                    result.ExitCode = 4;
                    result.Message = "校准探采取消/急停：" + ex.Message;
                    return result;
                }
                catch (Exception ex)
                {
                    try { backend.AbortRapidBlockCapture(); } catch { }
                    result.Success = false;
                    result.ExitCode = 3;
                    result.Message = "校准探采 Rapid 失败：" + ex.Message;
                    return result;
                }

                lastProbeRapid = probeRapid;
                short[] probeWave = PickPeakiestCapture(probeRapid);
                lastAdvice = Task6RangeAdvisor.Advise(
                    rangeIndex,
                    probeWave,
                    probeRapid.MaximumAdc,
                    probeRapid.AnyHardwareOverflow);

                // 合成探采 Cycle 供上层诊断落盘
                result.ProbeCycle = BuildProbeCycle(probeRapid, probeWave, rangeIndex, resolutionBits, lastAdvice);

                log.WriteLine("校准探采峰：peakAdc=" + lastAdvice.PeakAbsAdc
                    + " suitable=" + lastAdvice.Suitable
                    + " suggest=" + lastAdvice.SuggestedRangeIndex
                    + " " + (lastAdvice.Message ?? ""));

                if (!lastAdvice.Suitable
                    && lastAdvice.SuggestedRangeIndex >= 0
                    && lastAdvice.SuggestedRangeIndex != rangeIndex
                    && attempt <= maxRetries)
                {
                    log.WriteLine("校准自动量程：" + rangeIndex + " → " + lastAdvice.SuggestedRangeIndex
                        + "（" + attempt + "/" + maxRetries + "）");
                    rangeIndex = lastAdvice.SuggestedRangeIndex;
                    continue;
                }
                break;
            }

            result.RangeIndexUsed = rangeIndex;
            log.WriteLine("校准量程就绪：index=" + rangeIndex
                + " FS=±" + LiveHardwareOptions.Pico5242DFullScaleVolts[rangeIndex] + " V"
                + " suitable=" + (lastAdvice != null && lastAdvice.Suitable));

            // 4) 最佳档：ChannelA 自动触发采够段（低电平+auto；禁止 50%FS 严电平导致空波）
            session.ApplyPicoCaptureSettings(resolutionBits, timebase, targetHz, rangeIndex);
            int averageN = PicoRapidBlockBulkOverlapped.ClampCaptureCount(
                request.PulseAverageCount > 0 ? request.PulseAverageCount : PicoRapidBlockBulkOverlapped.DefaultCaptureCount);
            int captureCount = PicoRapidBlockBulkOverlapped.ClampCaptureCount(
                Math.Max(averageN + Math.Max(2, averageN / 2), averageN));

            double triggerV = PicoRapidBlockBulkOverlapped.ChannelATriggerVoltsFromRange(
                rangeIndex, CalibrationAutoTriggerLevelFraction);
            // 校准强制自动触发：请求未指定或为 0 时用默认 autoMs（勿用 0=一直等边沿）
            short autoMs = request.AutoTriggerMilliseconds > 0
                ? request.AutoTriggerMilliseconds
                : CalibrationAutoTriggerMilliseconds;
            log.WriteLine("校准 Rapid 自动触发：captures=" + captureCount
                + " averageN=" + averageN
                + " pre=" + timing.PreTriggerSamples
                + " post=" + timing.PostTriggerSamples
                + " trigA=" + triggerV.ToString("0.####") + " V (~"
                + (CalibrationAutoTriggerLevelFraction * 100).ToString("0.#") + "% FS，自动触发)"
                + " L=" + timing.CableLengthM + "m v=" + timing.VelocityMPerUs + " m/µs"
                + " bits=" + resolutionBits
                + " intervalNs=" + intervalNs.ToString("0.###")
                + " tb=" + timebase
                + " autoMs=" + autoMs);

            var arm = new RapidBlockArmRequest
            {
                RangeIndex = rangeIndex,
                CaptureCount = captureCount,
                PreTriggerSamples = timing.PreTriggerSamples,
                PostTriggerSamples = timing.PostTriggerSamples,
                ChannelATriggerThresholdVolts = triggerV,
                AutoTriggerMilliseconds = autoMs
            };

            PicoRapidBlockCaptureResult rapid;
            try
            {
                backend.ArmRapidBlockCapture(arm, null);
                int timeoutMs = request.RapidTimeoutMilliseconds > 0
                    ? request.RapidTimeoutMilliseconds
                    : Math.Max(5000, averageN * Math.Max(1000, (int)autoMs + 200));
                rapid = backend.ReadRapidBlockCaptures(timeoutMs, cancellation);
            }
            catch (OperationCanceledException ex)
            {
                try { backend.AbortRapidBlockCapture(); } catch { }
                result.Success = false;
                result.ExitCode = 4;
                result.Message = "校准 Rapid 取消/急停：" + ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                try { backend.AbortRapidBlockCapture(); } catch { }
                result.Success = false;
                result.ExitCode = 3;
                result.Message = "校准 Rapid 失败：" + ex.Message;
                return result;
            }

            // 若终采削顶且还能升档：再升一档重采一次（示波器升档）
            if (rapid != null && rapid.AnyHardwareOverflow && rangeIndex < LiveHardwareOptions.Pico5242DFullScaleVolts.Length - 1
                && maxRetries > 0)
            {
                short[] clippedWave = PickPeakiestCapture(rapid);
                var clipAdvice = Task6RangeAdvisor.Advise(rangeIndex, clippedWave, rapid.MaximumAdc, true);
                if (clipAdvice.SuggestedRangeIndex > rangeIndex)
                {
                    log.WriteLine("校准终采削顶：升档 " + rangeIndex + " → " + clipAdvice.SuggestedRangeIndex + " 后重采");
                    rangeIndex = clipAdvice.SuggestedRangeIndex;
                    result.RangeIndexUsed = rangeIndex;
                    session.ApplyPicoCaptureSettings(resolutionBits, timebase, targetHz, rangeIndex);
                    triggerV = PicoRapidBlockBulkOverlapped.ChannelATriggerVoltsFromRange(
                        rangeIndex, CalibrationAutoTriggerLevelFraction);
                    arm.RangeIndex = rangeIndex;
                    arm.ChannelATriggerThresholdVolts = triggerV;
                    try
                    {
                        backend.ArmRapidBlockCapture(arm, null);
                        int timeoutMs = request.RapidTimeoutMilliseconds > 0
                            ? request.RapidTimeoutMilliseconds
                            : Math.Max(5000, averageN * Math.Max(1000, (int)autoMs + 200));
                        rapid = backend.ReadRapidBlockCaptures(timeoutMs, cancellation);
                    }
                    catch (OperationCanceledException ex)
                    {
                        try { backend.AbortRapidBlockCapture(); } catch { }
                        result.Success = false;
                        result.ExitCode = 4;
                        result.Message = "校准 Rapid 升档重采取消/急停：" + ex.Message;
                        return result;
                    }
                    catch (Exception ex)
                    {
                        try { backend.AbortRapidBlockCapture(); } catch { }
                        result.Success = false;
                        result.ExitCode = 3;
                        result.Message = "校准 Rapid 升档重采失败：" + ex.Message;
                        return result;
                    }
                }
            }

            result.Rapid = rapid;
            if (rapid == null || rapid.Captures == null || rapid.Captures.Length == 0)
            {
                result.Success = false;
                result.ExitCode = 5;
                result.Message = StatusCopy.NoCalibrationWaveform + "（Rapid 空缓冲）";
                log.WriteLine(result.Message);
                return result;
            }

            // 5) 固定间隔筛选 → 按 N 平均
            double expectedPeriodNs = request.ExpectedPeriodNanoseconds > 0
                ? request.ExpectedPeriodNanoseconds
                : double.NaN;
            var filter = CalibrationPulseIntervalFilter.Filter(
                rapid,
                averageN,
                request.PeriodToleranceFraction > 0
                    ? request.PeriodToleranceFraction
                    : CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction,
                CalibrationPulseIntervalFilter.DefaultMinKeepFractionOfTarget,
                CalibrationPulseIntervalFilter.DefaultMinKeepAbsolute,
                expectedPeriodNs,
                CalibrationPulseIntervalFilter.DefaultExpectedPeriodCandidatesNs);
            result.IntervalFilter = filter;
            log.WriteLine(filter.Message ?? "");
            if (!filter.Success)
            {
                // 间隔筛失败时：若各段峰均过低 → 统一无波形文案；否则保留筛失败细节
                if (!AnyCaptureHasMeaningfulPeak(rapid))
                {
                    result.Success = false;
                    result.ExitCode = 5;
                    result.Message = StatusCopy.NoCalibrationWaveform
                        + "（" + (filter.Message ?? "间隔筛失败且峰过低") + "）";
                    log.WriteLine(result.Message);
                    return result;
                }
                result.Success = false;
                result.ExitCode = 5;
                result.Message = filter.Message;
                return result;
            }

            short[] averaged = CalibrationPulseIntervalFilter.AverageCaptures(rapid, filter.KeptIndices);
            result.AveragedWaveform = averaged;
            result.AveragedCount = filter.KeptIndices.Length;
            log.WriteLine("校准平均波形：用 " + result.AveragedCount + " 段按点平均（页面 N=" + averageN
                + "，剔除干扰 " + filter.RejectedCount + "）。");

            // 6) 合成 CycleResult 供落盘/指标
            var cycle = new AcquisitionCycleResult
            {
                Success = true,
                IsBackgroundCapture = true,
                ExitReason = AcquisitionExitReason.TargetReached,
                PicoRawAdc = averaged,
                PicoSampleRateHz = rapid.SampleRateHz,
                PicoSampleIntervalNanoseconds = rapid.SampleIntervalNanoseconds,
                PicoResolutionBits = resolutionBits,
                PicoFullScaleVolts = rapid.FullScaleVolts,
                PicoMaximumAdc = rapid.MaximumAdc,
                PicoRangeIndexUsed = rapid.RangeIndex,
                PicoSerial = rapid.Serial ?? string.Empty,
                Task6RangeSuitable = lastAdvice == null || lastAdvice.Suitable,
                SuggestedPicoRangeIndex = lastAdvice != null ? lastAdvice.SuggestedRangeIndex : -1,
                Task6RangeMessage = lastAdvice != null ? (lastAdvice.Message ?? "") : "",
                PicoOverrangeOrClipped = rapid.AnyHardwareOverflow,
                DischargeVerified = true,
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow
            };
            var advice = Task6RangeAdvisor.Advise(rangeIndex, averaged, rapid.MaximumAdc, rapid.AnyHardwareOverflow);
            Task6RangeAdvisor.ApplyToCycleResult(cycle, advice);
            result.AveragedCycle = cycle;

            // 7) 空波形 / 峰过低 → 失败（勿 silent 标已校准；真无信号才此文案）
            string emptyReason;
            if (!HasMeaningfulAveragedPulse(averaged, rapid.MaximumAdc, out emptyReason))
            {
                result.Success = false;
                result.ExitCode = 5;
                result.Message = StatusCopy.NoCalibrationWaveform
                    + (string.IsNullOrEmpty(emptyReason) ? "" : ("（" + emptyReason + "）"));
                log.WriteLine(result.Message);
                return result;
            }

            session.SetCalibrated(true);
            result.Success = true;
            result.ExitCode = 0;
            result.Message = "校准采集完成：平均 " + result.AveragedCount + " 段，量程 index=" + rangeIndex
                + "，已 SetCalibrated。";
            log.WriteLine(result.Message);
            return result;
        }

        /// <summary>平均脉冲至少 1% FS 峰（绝对 ADC），否则视为校准源未开/电量低。</summary>
        public const double MinPeakFractionOfFullScale = 0.01;

        static bool HasMeaningfulAveragedPulse(short[] averaged, int maximumAdc, out string detail)
        {
            detail = null;
            if (averaged == null || averaged.Length == 0)
            {
                detail = "平均数组为空";
                return false;
            }
            if (!(maximumAdc > 0))
            {
                detail = "MaximumAdc 无效";
                return false;
            }
            int peak = PeakAbs(averaged);
            int minPeak = Math.Max(1, (int)(maximumAdc * MinPeakFractionOfFullScale));
            if (peak < minPeak)
            {
                detail = "峰ADC=" + peak + " < " + minPeak + "（约 "
                    + (MinPeakFractionOfFullScale * 100).ToString("0.#") + "% FS）";
                return false;
            }
            return true;
        }

        static bool AnyCaptureHasMeaningfulPeak(PicoRapidBlockCaptureResult rapid)
        {
            if (rapid == null || rapid.Captures == null || rapid.MaximumAdc <= 0) return false;
            int minPeak = Math.Max(1, (int)(rapid.MaximumAdc * MinPeakFractionOfFullScale));
            for (int i = 0; i < rapid.Captures.Length; i++)
            {
                if (PeakAbs(rapid.Captures[i]) >= minPeak) return true;
            }
            return false;
        }

        static int PeakAbs(short[] wave)
        {
            if (wave == null || wave.Length == 0) return 0;
            int peak = 0;
            for (int i = 0; i < wave.Length; i++)
            {
                int a = Math.Abs((int)wave[i]);
                if (a > peak) peak = a;
            }
            return peak;
        }

        static short[] PickPeakiestCapture(PicoRapidBlockCaptureResult rapid)
        {
            if (rapid == null || rapid.Captures == null || rapid.Captures.Length == 0)
                return new short[0];
            int best = 0;
            int bestPeak = -1;
            for (int i = 0; i < rapid.Captures.Length; i++)
            {
                int p = PeakAbs(rapid.Captures[i]);
                if (p > bestPeak)
                {
                    bestPeak = p;
                    best = i;
                }
            }
            return rapid.Captures[best] ?? new short[0];
        }

        static AcquisitionCycleResult BuildProbeCycle(
            PicoRapidBlockCaptureResult rapid,
            short[] wave,
            int rangeIndex,
            int resolutionBits,
            Task6RangeAdvice advice)
        {
            var cycle = new AcquisitionCycleResult
            {
                Success = rapid != null && wave != null && wave.Length > 0,
                IsBackgroundCapture = true,
                ExitReason = AcquisitionExitReason.TargetReached,
                PicoRawAdc = wave ?? new short[0],
                PicoSampleRateHz = rapid != null ? rapid.SampleRateHz : 0,
                PicoSampleIntervalNanoseconds = rapid != null ? rapid.SampleIntervalNanoseconds : 0,
                PicoResolutionBits = resolutionBits,
                PicoFullScaleVolts = rapid != null ? rapid.FullScaleVolts : 0,
                PicoMaximumAdc = rapid != null ? rapid.MaximumAdc : (short)0,
                PicoRangeIndexUsed = rangeIndex,
                PicoSerial = rapid != null ? (rapid.Serial ?? string.Empty) : string.Empty,
                PicoOverrangeOrClipped = rapid != null && rapid.AnyHardwareOverflow,
                DischargeVerified = true,
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow
            };
            Task6RangeAdvisor.ApplyToCycleResult(cycle, advice);
            return cycle;
        }
    }

    public sealed class CalibrationPulseRequest
    {
        public bool ImpedanceBandwidthWide { get; set; }
        public int InitialPicoRangeIndex { get; set; }
        public int MaxRangeRetries { get; set; }
        public int PicoResolutionBits { get; set; }
        public uint PicoTimebase { get; set; }
        public double PicoTargetSampleRateHz { get; set; }
        public double SampleIntervalNanoseconds { get; set; }
        public double CableLengthM { get; set; }
        public double VelocityMPerUs { get; set; }
        /// <summary>页面「脉冲平均次数」N：筛选后取至多 N 段平均。</summary>
        public int PulseAverageCount { get; set; }
        public double PeriodToleranceFraction { get; set; }
        /// <summary>显式标称周期（ns）；≤0 则在 50/100/400Hz 候选与实测中位间自适应。</summary>
        public double ExpectedPeriodNanoseconds { get; set; }
        public short AutoTriggerMilliseconds { get; set; }
        public int RapidTimeoutMilliseconds { get; set; }

        public CalibrationPulseRequest()
        {
            InitialPicoRangeIndex = 6;
            MaxRangeRetries = 2;
            PicoResolutionBits = 14;
            PicoTimebase = 3;
            PicoTargetSampleRateHz = 125e6;
            SampleIntervalNanoseconds = LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(3);
            CableLengthM = CalibrationCaptureTiming.DefaultCableLengthM;
            VelocityMPerUs = CalibrationCaptureTiming.DefaultVelocityMPerUs;
            PulseAverageCount = PicoRapidBlockBulkOverlapped.DefaultCaptureCount;
            PeriodToleranceFraction = CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction;
            ExpectedPeriodNanoseconds = double.NaN; // 自适应 50/100/400Hz
            AutoTriggerMilliseconds = CalibrationPulseAcquisitionRunner.CalibrationAutoTriggerMilliseconds; // 校准默认自动触发
            RapidTimeoutMilliseconds = 0;
        }
    }

    public sealed class CalibrationPulseAcquisitionResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; }
        public int RangeIndexUsed { get; set; }
        public AcquisitionCycleResult ProbeCycle { get; set; }
        public PicoRapidBlockCaptureResult Rapid { get; set; }
        public CalibrationPulseIntervalFilterResult IntervalFilter { get; set; }
        public CalibrationCaptureTimingResult Timing { get; set; }
        public short[] AveragedWaveform { get; set; }
        public int AveragedCount { get; set; }
        public AcquisitionCycleResult AveragedCycle { get; set; }

        public CalibrationPulseAcquisitionResult()
        {
            Message = string.Empty;
            RangeIndexUsed = -1;
            AveragedWaveform = new short[0];
        }
    }
}
