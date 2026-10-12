using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.Owts.Acquisition;
using Xiongxin.Owts.Acquisition.Live;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 雄新电力 · 雄新 OWTS 出厂加压测试编排（不挤 DesktopUi）：
    /// Connect → Calibrate → Press(list+auto range) → Stop/Release。
    /// 只验加压/采集；不做定位/缺陷分析。急停/过流/超压 → 立刻停，会话 finally AO=0。
    /// 高阶入口见 <see cref="FactoryPressApi"/>。
    /// </summary>
    public sealed class PressTestRunner : IDisposable
    {
        readonly PressTestConfig _config;
        readonly TextWriter _log;
        FakeLiveHardwareBackend _fake;
        LiveAcquisitionSession _live;
        SingletonAcquisitionSession _session;
        bool _disposed;
        bool _connected;
        bool _cleanupFailedOnClose;
        CancellationTokenSource _stopCts;
        /// <summary>上次达压采集成功时实际使用的 Pico 量程索引；连续同压沿用，勿每轮回原点。</summary>
        int _lastSuccessfulRangeIndex = -1;
        /// <summary>与 <see cref="_lastSuccessfulRangeIndex"/> 配对的目标 kV（NaN=尚未成功）。</summary>
        double _lastSuccessfulRangeKv = double.NaN;

        public PressTestRunner(PressTestConfig config, TextWriter log = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            _config = config;
            _log = log ?? Console.Out;
            LastCalibrationPeakVolts = double.NaN;
            LastComputedPdCalibrationPcPerVolt = double.NaN;
            LastInjectedChargePc = double.NaN;
        }

        public string RunDirectory { get; private set; }
        public string CsvPath { get; private set; }
        public int CompletedSteps { get; private set; }
        /// <summary>自动量程后的当前 Pico 量程索引（供 UI 下拉同步；优先上次成功量程）。</summary>
        public int CurrentPicoRangeIndex
        {
            get
            {
                if (_lastSuccessfulRangeIndex >= 0) return _lastSuccessfulRangeIndex;
                return _config != null ? _config.InitialPicoRangeIndex : -1;
            }
        }
        /// <summary>上次达压采集成功的目标 kV；无则 NaN。</summary>
        public double LastSuccessfulRangeKv
        {
            get { return _lastSuccessfulRangeKv; }
        }
        public bool AbortedBySafety { get; private set; }
        /// <summary>硬过流/急停/过压后禁止自动再压，直至操作员显式开始加压（ClearSafetyAbortForNewPress）。</summary>
        public bool SafetyAbortBlocksRepress { get; private set; }
        public string AbortReason { get; private set; }
        public bool CleanupFailedOnClose { get { return _cleanupFailedOnClose; } }
        /// <summary>最近一次校准脉冲峰压（V）；未成功则为 NaN。</summary>
        public double LastCalibrationPeakVolts { get; private set; }
        /// <summary>最近一次由注入放电量/峰压算出的 pC/V；未算则为 NaN。</summary>
        public double LastComputedPdCalibrationPcPerVolt { get; private set; }
        /// <summary>最近一次用于计算的注入放电量 pC。</summary>
        public double LastInjectedChargePc { get; private set; }

        /// <summary>供 UI 顶栏刷新真实急停/锁存/连接态。</summary>
        public AcquisitionStatusSnapshot TryGetStatus()
        {
            if (!_connected || _session == null) return null;
            try { return _session.GetStatus(); }
            catch { return null; }
        }

        /// <summary>完整流程：Connect+Calibrate?+Press（兼容旧入口）。</summary>
        public int Run(CancellationToken cancellation = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (_config.TargetsKv == null || _config.TargetsKv.Count == 0)
                throw new InvalidOperationException("加压列表为空。");

            ConnectOnly();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopCts.Token))
            {
                if (_config.RunCalibrationFirst)
                {
                    int calCode = CalibrateOnly(linked.Token);
                    if (calCode != 0) return calCode;
                }
                else
                {
                    // 与旧 Run 一致：跳过校准时仍做联锁标记
                    PrepareInterlocks();
                }
                return PressOnly(linked.Token);
            }
        }

        /// <summary>仅 Connect / Open 会话 + 落盘目录。</summary>
        public void ConnectOnly()
        {
            ThrowIfDisposed();
            if (_connected) return;
            if (SingletonAcquisitionSession.ProcessCleanupFailed)
                throw new InvalidOperationException("本进程清理失败门闩已置位。确认源关断后可复位清理门再 Connect。");

            OpenSession();
            RunDirectory = WaveformSummaryWriter.EnsureRunDirectory(_config.OutputDirectory);
            CsvPath = Path.Combine(RunDirectory, "press-results.csv");
            WaveformSummaryWriter.WriteCsvHeader(CsvPath);
            WriteRunManifest();
            _stopCts = new CancellationTokenSource();
            _connected = true;

            _log.WriteLine("════════════════════════════════════════");
            _log.WriteLine("  雄新电力 · 雄新 OWTS 出厂加压测试");
            _log.WriteLine("════════════════════════════════════════");
            _log.WriteLine("mode=" + _config.Mode + "  " + PressTestConfig.TestBenchGainNote);
            _log.WriteLine("连续加压 + " + (_config.AutoRangeAdjust ? "自动量程（可重加本轮）" : "手动量程（仅提示、不重加）") + "；只验加压/采集（不做定位/缺陷分析）。");
            _log.WriteLine("HVON(P0.0)↔加压灯(P0.3) 同步；急停(P0.4) 独立串行~10ms/超时~300ms；未知不冒充低、不以历史高放行。");
            _log.WriteLine("启动：HV ON 前 AO=0+查P0.4；HVON→建AI→100ms→HVUP；背景限=" + _config.ZeroBackgroundLimitV + " V。");
            _log.WriteLine("达压±" + (_config.TargetReachedToleranceFraction * 100).ToString("0.#", CultureInfo.InvariantCulture)
                + "%；异常过压+" + (_config.OverVoltageTripFraction * 100).ToString("0.#", CultureInfo.InvariantCulture)
                + "%（原始峰值）；重复加压：自导通指令起算满 2s 再进下轮（Live 另等 Pico 缓冲释放）。");
            _log.WriteLine("异常→先 HV OFF/AO=0，不自动恢复加压；加压中急停仍 Pico 同步采集（整机掉线除外）。");
            _log.WriteLine("Stop/急停/未知/断线 → 撤销批次，须显式重开。Pico：会话持有，仅 SDK，抑制启动窗。");
            _log.WriteLine("输出目录: " + RunDirectory);
            _log.WriteLine("目标档数: " + (_config.TargetsKv != null ? _config.TargetsKv.Count : 0));
        }

        /// <summary>联锁：标记已校准；FakeLive 解除急停；Live 若急停按下则拒；硬件就绪时清陈旧锁存。</summary>
        public int PrepareInterlocks()
        {
            ThrowIfDisposed();
            if (!_connected) throw new InvalidOperationException("尚未 Connect。");
            _session.SetCalibrated(true);
            if (_config.IsFakeLive)
            {
                _session.SetEStopPressed(false);
                string fakeClr = null;
                if (_live != null)
                    _live.TryClearEmergencyLatchIfHardwareSafe(out fakeClr);
                _log.WriteLine("FakeLive：已 SetCalibrated + 急停解除"
                    + (string.IsNullOrEmpty(fakeClr) ? "" : ("；" + fakeClr)));
                return 0;
            }
            // Live：硬件已解除且源关 → 清陈旧软件锁存（无需客户 Release→Connect）
            if (_live != null)
            {
                string clr;
                if (_live.TryClearEmergencyLatchIfHardwareSafe(out clr) && !string.IsNullOrEmpty(clr))
                    _log.WriteLine("Live：锁存复位 — " + clr);
                else if (_session.GetStatus().EmergencyLatched)
                {
                    AbortReason = "实机急停锁存且 P0.4 仍阻断，拒绝开始加压列表。" + (clr ?? "");
                    AbortedBySafety = true;
                    _log.WriteLine("ABORT: " + AbortReason);
                    return 2;
                }
            }
            var st = _session.GetStatus();
            if (st.EStopPressed)
            {
                AbortReason = "实机急停仍按下，拒绝开始加压列表。";
                AbortedBySafety = true;
                _log.WriteLine("ABORT: " + AbortReason);
                return 2;
            }
            _log.WriteLine("Live：急停由 P0.4 回读（当前解除）；校准系数已 Apply；EmergencyLatched="
                + st.EmergencyLatched);
            return 0;
        }

        /// <summary>校准态：0 kV Pico 采集。须急停按下；解除（高电平）则禁校准。流程=自适量程→最佳档→脉冲触发。</summary>
        public int CalibrateOnly(CancellationToken cancellation = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (!_connected) throw new InvalidOperationException("尚未 Connect。");
            ResetStopTokenForNewBatch();
            AbortReason = null;
            AbortedBySafety = false;
            int gate = EnsureEStopPressedForCalibration();
            if (gate != 0) return gate;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopCts.Token))
                return RunCalibrationPhase(linked.Token);
        }

        /// <summary>
        /// 进入校准采集前：急停须按下。解除（高电平）→ 拒绝开干并提示。
        /// FakeLive：若已解除则自动按下以便演示；不在此 SetCalibrated。
        /// 0kV 校准不走加压列表门禁；按下时仍可写 AO=0。
        /// </summary>
        int EnsureEStopPressedForCalibration()
        {
            if (_config.IsFakeLive)
            {
                var stFake = _session.GetStatus();
                if (!stFake.EStopPressed)
                {
                    _session.SetEStopPressed(true);
                    _log.WriteLine("FakeLive：校准前急停为解除，已自动按下（演示；真机须按下急停后再校准）");
                }
                else
                    _log.WriteLine("FakeLive：校准前急停已按下（允许校准；急停解除时禁校准）");
                return 0;
            }

            var st = _session.GetStatus();
            // 急停解除 → 禁校准；按下才可进；0kV 校准不走加压列表门禁
            if (!st.EStopPressed)
            {
                AbortReason = StatusCopy.CalibNeedsEStopPressed
                    + "（当前「" + StatusCopy.EStopReleased + "」）";
                AbortedBySafety = false;
                _log.WriteLine("ABORT: " + AbortReason);
                return 2;
            }
            _log.WriteLine("Live：校准前急停已按下（允许校准；自适量程→最佳档→脉冲触发）；EmergencyLatched="
                + _session.GetStatus().EmergencyLatched);
            return 0;
        }

        /// <summary>连续加压 + 量程自调；档间等 PicoCaptureCooldown（非 Fake 时）或配置冷却。</summary>
        public int PressOnly(CancellationToken cancellation = default(CancellationToken))
        {
            ThrowIfDisposed();
            if (!_connected) throw new InvalidOperationException("尚未 Connect。");
            if (_config.TargetsKv == null || _config.TargetsKv.Count == 0)
                throw new InvalidOperationException("加压列表为空。");

            ResetStopTokenForNewBatch();
            // 操作员显式点「开始加压」才清安全中止再压门；本批内过流后立刻停且不再自动开下轮
            SafetyAbortBlocksRepress = false;
            AbortedBySafety = false;
            AbortReason = null;
            int gate = PrepareInterlocks();
            if (gate != 0) return gate;

            try
            {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopCts.Token))
            {
                var token = linked.Token;
                // 自动量程：优先沿用上次成功量程，禁止每轮回原点（默认 ±1V）再爬 3～4 次
                int rangeIndex = ResolveStartingRangeIndex();
                CompletedSteps = 0;
                _log.WriteLine();
                _log.WriteLine("—— 连续加压开始（" + (_config.AutoRangeAdjust ? "自动量程" : "手动量程")
                    + " maxRetries=" + _config.MaxRangeRetries
                    + " startRange=" + rangeIndex
                    + (_lastSuccessfulRangeIndex >= 0
                        ? (" lastOkRange=" + _lastSuccessfulRangeIndex
                            + " lastOkKv=" + (double.IsNaN(_lastSuccessfulRangeKv) ? "NaN"
                                : _lastSuccessfulRangeKv.ToString("0.###", CultureInfo.InvariantCulture)))
                        : "")
                    + "；AO步进间隔 RampStepDelay=" + Math.Max(1, _config.RampStepDelayMilliseconds) + "ms）——");

                for (int i = 0; i < _config.TargetsKv.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double targetKv = _config.TargetsKv[i];
                    double targetV = targetKv * 1000.0;
                    // 连续同压：强制沿用该压上次成功量程（勿用 Suggested/原点重爬）
                    if (_config.AutoRangeAdjust
                        && _lastSuccessfulRangeIndex >= 0
                        && !double.IsNaN(_lastSuccessfulRangeKv)
                        && Math.Abs(targetKv - _lastSuccessfulRangeKv) < 1e-9)
                    {
                        if (rangeIndex != _lastSuccessfulRangeIndex)
                        {
                            _log.WriteLine("同压沿用成功量程：目标 "
                                + targetKv.ToString("0.###", CultureInfo.InvariantCulture) + " kV"
                                + " range " + rangeIndex + " → " + _lastSuccessfulRangeIndex
                                + "（跳过回原点重调）");
                            rangeIndex = _lastSuccessfulRangeIndex;
                            _config.InitialPicoRangeIndex = rangeIndex;
                            ApplyRangeToLiveHardware(rangeIndex);
                        }
                    }
                    _log.WriteLine();
                    _log.WriteLine("—— 档 " + (i + 1) + "/" + _config.TargetsKv.Count
                        + " 目标 " + targetKv.ToString("0.###", CultureInfo.InvariantCulture) + " kV"
                        + "（range=" + rangeIndex + "）——"
                        + (!(targetV > 0) ? " 【0V：跳过 HVON/HVUP→噪声采集】" : ""));

                    AcquisitionCycleResult cycle = null;
                    int attempt = 0;
                    while (true)
                    {
                        attempt++;
                        cycle = _session.RunCycleAsync(
                            new AcquisitionCycleRequest
                            {
                                TargetVoltageV = targetV,
                                SuggestedPicoRangeIndex = rangeIndex
                            },
                            token).GetAwaiter().GetResult();

                        bool aoZero = ConfirmAoZero();
                        if (!aoZero)
                        {
                            AbortedBySafety = true;
                            AbortReason = "SAFETY：循环后 AO 未归零。";
                            var metricsFail = BasicMetricsCalculator.Compute(cycle, _config);
                            WaveformSummaryWriter.AppendCycle(RunDirectory, CsvPath, i + 1, targetKv, cycle, metricsFail, false);
                            _log.WriteLine("ABORT: " + AbortReason);
                            return 3;
                        }

                        // UI 停止 / 取消：关源不永久锁存；量程不动，跳过 range advise 与升档重加
                        if (cycle != null && cycle.ExitReason == AcquisitionExitReason.OperatorStop)
                            return FinishOperatorStop(cycle, i + 1, targetKv, "exit=OperatorStop");

                        if (token.IsCancellationRequested)
                            return FinishOperatorStop(cycle, i + 1, targetKv, "CancellationToken（跳过量程核算/升档重加）");

                        if (IsHardSafetyExit(cycle))
                        {
                            AbortedBySafety = true;
                            SafetyAbortBlocksRepress = true;
                            AbortReason = "安全退出：" + cycle.ExitReason + " — " + (cycle.ErrorMessage ?? "")
                                + "；已停批，禁止自动再压。";
                            var metricsSafe = BasicMetricsCalculator.Compute(cycle, _config);
                            WaveformSummaryWriter.AppendCycle(RunDirectory, CsvPath, i + 1, targetKv, cycle, metricsSafe, true);
                            _log.WriteLine("ABORT: " + AbortReason + "（须操作员再次点开始加压；AO=0）");
                            string clr;
                            if (_live != null && _live.TryClearEmergencyLatchIfHardwareSafe(out clr) && !string.IsNullOrEmpty(clr))
                                _log.WriteLine("P0.4 已解除，已尝试清除软件急停锁存：" + clr);
                            return 4;
                        }

                        if (!cycle.Success
                            && IsTransientNonSafetyAbort(cycle)
                            && attempt <= _config.MaxRangeRetries)
                        {
                            if (token.IsCancellationRequested)
                                return FinishOperatorStop(cycle, i + 1, targetKv, "取消：跳过短暂中止重试");
                            _log.WriteLine("短暂采集中止（非急停），重试本轮 " + attempt + "/" + _config.MaxRangeRetries
                                + "：" + (cycle.ErrorMessage ?? ""));
                            string clr;
                            if (_live != null && _live.TryClearEmergencyLatchIfHardwareSafe(out clr)
                                && !string.IsNullOrEmpty(clr))
                                _log.WriteLine("已尝试清除软件急停锁存：" + clr);
                            continue;
                        }

                        // 手动量程：禁止因量程升/降档重加本轮（MaxRangeRetries 仅自动量程可用）
                        if (cycle.Success
                            && !cycle.Task6RangeSuitable
                            && cycle.SuggestedPicoRangeIndex >= 0
                            && cycle.SuggestedPicoRangeIndex != rangeIndex
                            && !_config.AutoRangeAdjust)
                        {
                            LogManualRangeHints(cycle, rangeIndex);
                            StripReapplyWordingFromCycle(cycle);
                            _log.WriteLine("手动量程：跳过升/降档重加本轮（Suggested="
                                + cycle.SuggestedPicoRangeIndex + " 保持 range=" + rangeIndex
                                + "；autoRangeAdjust=false）");
                            break;
                        }

                        if (cycle.Success
                            && !cycle.Task6RangeSuitable
                            && cycle.SuggestedPicoRangeIndex >= 0
                            && cycle.SuggestedPicoRangeIndex != rangeIndex
                            && _config.AutoRangeAdjust
                            && attempt <= _config.MaxRangeRetries)
                        {
                            // 操作员已点停止/取消：禁止升档重加，量程保持本轮进入时索引
                            if (token.IsCancellationRequested)
                                return FinishOperatorStop(cycle, i + 1, targetKv, "取消：跳过自动量程升档重加");
                            bool isDecrease = cycle.SuggestedPicoRangeIndex < rangeIndex;
                            // 记住成功档：禁止峰过小盲目降到远低于已成功档（再 ±10mV 饱和重加）
                            if (isDecrease && !cycle.PicoOverrangeOrClipped
                                && _lastSuccessfulRangeIndex >= 0
                                && cycle.SuggestedPicoRangeIndex < _lastSuccessfulRangeIndex)
                            {
                                _log.WriteLine("自动量程：拒绝盲目降档 " + rangeIndex + " → " + cycle.SuggestedPicoRangeIndex
                                    + "（低于已成功档 " + _lastSuccessfulRangeIndex
                                    + "；无超量程）；保持 " + rangeIndex
                                    + (string.IsNullOrEmpty(cycle.Task6RangeMessage) ? "" : ("；" + cycle.Task6RangeMessage)));
                                break;
                            }
                            // 单次最多接受降 2 档；更陡降档钳制后再重加
                            if (isDecrease && !cycle.PicoOverrangeOrClipped
                                && rangeIndex - cycle.SuggestedPicoRangeIndex > 2)
                            {
                                int clamped = Math.Max(0, rangeIndex - 2);
                                if (clamped == 0 && rangeIndex > 2) clamped = Math.Max(1, rangeIndex - 2);
                                _log.WriteLine("自动量程：钳制降档 " + rangeIndex + " → " + cycle.SuggestedPicoRangeIndex
                                    + " 为 " + clamped + "（单次≤2档，禁掉±10mV悬崖）");
                                cycle.SuggestedPicoRangeIndex = clamped;
                                if (clamped == rangeIndex) break;
                            }
                            _log.WriteLine("自动量程：索引 " + rangeIndex + " → " + cycle.SuggestedPicoRangeIndex
                                + "（重加本轮 " + attempt + "/" + _config.MaxRangeRetries + "）"
                                + (cycle.PicoOverrangeOrClipped ? " [超量程/饱和]" : "")
                                + (string.IsNullOrEmpty(cycle.Task6RangeMessage) ? "" : (" " + cycle.Task6RangeMessage))
                                + (cycle.DischargeVerified ? "" : (string.IsNullOrEmpty(cycle.DischargeStatusMessage) ? "" : ("；残压：" + cycle.DischargeStatusMessage))));
                            rangeIndex = cycle.SuggestedPicoRangeIndex;
                            _config.InitialPicoRangeIndex = rangeIndex;
                            if (_live != null)
                                _live.ApplyPicoCaptureSettings(
                                    _config.PicoResolutionBits > 0 ? _config.PicoResolutionBits : 14,
                                    _config.PicoTimebase > 0 ? _config.PicoTimebase : 3,
                                    _config.PicoTargetSampleRateHz > 0 ? _config.PicoTargetSampleRateHz : 125e6,
                                    rangeIndex);
                            continue;
                        }
                        if (cycle.Success && !_config.AutoRangeAdjust)
                        {
                            LogManualRangeHints(cycle, rangeIndex);
                            StripReapplyWordingFromCycle(cycle);
                        }
                        break; // 手动量程或量程合适/耗尽：结束本档，绝不因量程再压
                    }

                    var metrics = BasicMetricsCalculator.Compute(cycle, _config);
                    string stem = WaveformSummaryWriter.AppendCycle(
                        RunDirectory, CsvPath, i + 1, targetKv, cycle, metrics, true);
                    CompletedSteps++;

                    _log.WriteLine("结果: success=" + cycle.Success
                        + " exit=" + cycle.ExitReason
                        + " peakV=" + cycle.PeakActualVoltageV.ToString("G6", CultureInfo.InvariantCulture)
                        + " picoN=" + (cycle.PicoRawAdc != null ? cycle.PicoRawAdc.Length : 0)
                        + " range=" + cycle.PicoRangeIndexUsed
                        + " suitable=" + cycle.Task6RangeSuitable
                        + (cycle.PicoOverrangeOrClipped ? " overrange=True" : "")
                        + (cycle.Success && !cycle.Task6RangeSuitable
                            ? "（success=达压采集完成；suitable=False 表示量程仍建议调整，属预期）"
                            : ""));
                    _log.WriteLine("基本量: f=" + StatusOrVal(metrics.FrequencyStatus, metrics.OscillationFrequencyHz, "Hz")
                        + " C=" + StatusOrVal(metrics.CapacitanceStatus, metrics.EquivalentCapacitanceUf, "uF")
                        + " Q=" + StatusOrVal(metrics.DischargeStatus, metrics.ApparentChargePc, "pC")
                        + " joint=" + metrics.JointStatus);
                    if (!string.IsNullOrEmpty(cycle.ErrorMessage))
                        _log.WriteLine("提示: " + cycle.ErrorMessage);
                    _log.WriteLine("波形: " + stem);

                    if (!cycle.Success)
                    {
                        AbortedBySafety = true;
                        // 假急停/缓存失效耗尽重试：停本批但不按硬安全锁死「禁止再压」
                        if (IsTransientNonSafetyAbort(cycle))
                        {
                            SafetyAbortBlocksRepress = false;
                            AbortReason = "档失败（非急停短暂中止，已重试）exit=" + cycle.ExitReason
                                + " " + (cycle.ErrorMessage ?? "") + "；已停本档，可再次点开始加压。";
                        }
                        else
                        {
                            SafetyAbortBlocksRepress = true;
                            AbortReason = "档失败 exit=" + cycle.ExitReason + " " + (cycle.ErrorMessage ?? "")
                                + "；已停批，禁止自动再压。";
                        }
                        _log.WriteLine("ABORT: " + AbortReason);
                        return 5;
                    }

                    if (_config.AutoRangeAdjust)
                    {
                        // 记住本档实际成功量程；禁止把未验证的 Suggested 带入下一档/同压重爬
                        int used = cycle.PicoRangeIndexUsed >= 0 ? cycle.PicoRangeIndexUsed : rangeIndex;
                        rangeIndex = used;
                        _config.InitialPicoRangeIndex = used;
                        RememberSuccessfulRange(used, targetKv);
                        if (cycle.SuggestedPicoRangeIndex >= 0
                            && cycle.SuggestedPicoRangeIndex != used
                            && !cycle.Task6RangeSuitable)
                        {
                            _log.WriteLine("量程记忆：本档成功用 index=" + used
                                + "；Suggested=" + cycle.SuggestedPicoRangeIndex
                                + " 仅作提示，不带入下一档/同压（避免回原点重调）");
                        }
                    }
                    else
                    {
                        // 手动量程：保持界面所选，不把 Suggested 带入下一档
                        rangeIndex = _config.InitialPicoRangeIndex;
                        if (cycle.PicoRangeIndexUsed >= 0)
                            RememberSuccessfulRange(cycle.PicoRangeIndexUsed, targetKv);
                    }

                    if (i + 1 < _config.TargetsKv.Count)
                        WaitInterRoundCooldown(token);
                }

                _log.WriteLine();
                _log.WriteLine("加压列表完成：成功 " + CompletedSteps + "/" + _config.TargetsKv.Count
                    + " CSV=" + CsvPath);
                return 0;
            }
            }
            catch (OperationCanceledException)
            {
                AbortedBySafety = false;
                SafetyAbortBlocksRepress = false;
                AbortReason = "操作员停止：已关源（未永久锁存）";
                _log.WriteLine("STOP: " + AbortReason + "（CancellationToken）；可再次开始加压。");
                try
                {
                    string clr;
                    if (_live != null && _live.TryClearEmergencyLatchIfHardwareSafe(out clr)
                        && !string.IsNullOrEmpty(clr))
                        _log.WriteLine("停止后清软锁存：" + clr);
                }
                catch { }
                return 6;
            }
        }

        /// <summary>
        /// 档间：仅等 Pico 缓冲释放（若需要）。下一次加压时刻由会话
        /// 「自开关导通指令起算满 2s」保证，此处不再另加无关固定冷却。
        /// </summary>
        void WaitInterRoundCooldown(CancellationToken cancellation)
        {
            if (_config.IsFakeLive)
            {
                _log.WriteLine("档间：FakeLive 无 Pico 缓冲等待；下轮由导通起算 2s 门控。");
                return;
            }
            int remain = PicoCaptureCooldown.RemainingMilliseconds;
            if (remain == int.MaxValue)
            {
                _log.WriteLine("档间等待 Pico 缓冲释放确认（加压间隔仍按导通起算 2s）…");
                PicoCaptureCooldown.Wait(cancellation, r =>
                {
                    if (r == int.MaxValue) return;
                    _log.WriteLine("Pico 缓冲剩余 " + r + " ms…");
                }, checkSafety: null);
            }
            else if (remain > 0)
            {
                _log.WriteLine("档间等 Pico 缓冲 " + remain + " ms（非另加固定冷却）…");
                PicoCaptureCooldown.Wait(cancellation, r => _log.WriteLine("Pico 缓冲剩余 " + r + " ms…"), null);
            }
            else
                _log.WriteLine("档间：Pico 缓冲已释放；下轮由导通起算 2s 门控。");
        }

        /// <summary>请求停止当前加压（取消令牌 + 关源；不置永久硬件锁存）。</summary>
        /// <summary>连续加压每轮前：沿用界面所选 Pico 位数/采样率/量程，勿回落默认。</summary>
        /// <summary>仅改阻抗带宽（校准/空闲/加压中均可），立即写会话并打日志。</summary>
        public void SetImpedanceBandwidthWide(bool wide)
        {
            if (_session == null) throw new InvalidOperationException("会话未打开。");
            _config.ImpedanceBandwidthWide = wide;
            _session.SetImpedanceBandwidthWide(wide);
            _log.WriteLine("阻抗带宽已切换: " + (wide ? "宽" : "窄")
                + "（即时生效，不跟加压绑死）");
        }

        public void ApplyUiCaptureSettings(int resolutionBits, double targetSampleRateHz, int rangeIndex, bool impedanceWide)
        {
            if (_live == null) throw new InvalidOperationException("会话未打开。");
            int bits = resolutionBits == 15 ? 15 : 14;
            double rate = targetSampleRateHz > 0 ? targetSampleRateHz : 125e6;
            uint tb = PressTestConfig.ResolvePicoTimebase(rate);
            _config.PicoResolutionBits = bits;
            _config.PicoTargetSampleRateHz = rate;
            _config.PicoTimebase = tb;
            int appliedRange = rangeIndex >= 0 ? rangeIndex : 6;
            // 自动量程：若调用方仍传入原点/默认而会话已有成功量程，保留成功量程（防每轮重爬）
            if (_config.AutoRangeAdjust
                && _lastSuccessfulRangeIndex >= 0
                && appliedRange != _lastSuccessfulRangeIndex
                && (appliedRange == 6 || appliedRange < _lastSuccessfulRangeIndex))
            {
                _log.WriteLine("ApplyUiCaptureSettings：保留上次成功量程 index="
                    + _lastSuccessfulRangeIndex + "（忽略传入 " + appliedRange + "，防回原点）");
                appliedRange = _lastSuccessfulRangeIndex;
            }
            _config.InitialPicoRangeIndex = appliedRange;
            _config.ImpedanceBandwidthWide = impedanceWide;
            double capMs = LiveHardwareOptions.ClampOscillationCaptureDurationMs(_config.OscillationCaptureDurationMs);
            _config.OscillationCaptureDurationMs = capMs;
            if (_live.HardwareOptions != null)
                _live.HardwareOptions.OscillationCaptureDurationMs = capMs;
            _live.ApplyPicoCaptureSettings(bits, tb, rate, _config.InitialPicoRangeIndex);
            int trigPct = LiveHardwareOptions.ClampTriggerPositionPercent(_config.PicoTriggerPositionPercent);
            _config.PicoTriggerPositionPercent = trigPct;
            if (_live.HardwareOptions != null)
                _live.HardwareOptions.PicoTriggerPositionPercent = trigPct;
            _live.SetImpedanceBandwidthWide(impedanceWide);
            _log.WriteLine("沿用界面采集设定：bits=" + bits
                + " rate=" + PressTestConfig.FormatPicoRate(rate)
                + " tb=" + tb
                + " range=" + _config.InitialPicoRangeIndex
                + " trigPos=" + trigPct + "%"
                + " capMs=" + capMs.ToString("0.###", CultureInfo.InvariantCulture)
                + " PicoN=" + (_live.HardwareOptions != null ? _live.HardwareOptions.PicoSampleCount : 0)
                + " ART_N=" + (_live.HardwareOptions != null ? _live.HardwareOptions.ArtFiniteSampleCount : 0)
                + " 阻抗=" + (impedanceWide ? "宽" : "窄"));
        }

        public bool TryClearEmergencyLatchIfHardwareSafe(out string detail)
        {
            detail = null;
            if (_live == null) return false;
            return _live.TryClearEmergencyLatchIfHardwareSafe(out detail);
        }

        public void RequestStop()
        {
            try { if (_stopCts != null) _stopCts.Cancel(); } catch { }
            // UI 停止：OperatorAbort（不锁存）。真 P0.4 边沿仍由监视锁存。
            try
            {
                if (_live != null) _live.RequestOperatorAbort();
                else if (_session != null) _session.EmergencyStop();
            }
            catch { }
            _log.WriteLine("RequestStop：已取消并 OperatorAbort（未永久锁存）。");
        }

        /// <summary>
        /// 每批 Calibrate/Press 重建停止令牌。
        /// 根因：Stop 后 _stopCts 保持 IsCancellationRequested，下一批 linked token 立刻「已取消」。
        /// </summary>
        void ResetStopTokenForNewBatch()
        {
            try { if (_stopCts != null) _stopCts.Dispose(); } catch { }
            _stopCts = new CancellationTokenSource();
        }

        void OpenSession()
        {
            var hw = new LiveHardwareOptions
            {
                ArtDeviceName = _config.ArtDeviceName ?? "Dev1",
                PicoSerial = _config.PicoSerial ?? string.Empty,
                PicoRangeIndex = _config.InitialPicoRangeIndex > 0 ? _config.InitialPicoRangeIndex : 6,
                PicoResolutionBits = _config.PicoResolutionBits > 0 ? _config.PicoResolutionBits : 14,
                PicoTimebase = _config.PicoTimebase > 0 ? _config.PicoTimebase : PressTestConfig.ResolvePicoTimebase(
                    _config.PicoTargetSampleRateHz > 0 ? _config.PicoTargetSampleRateHz : 125e6),
                PicoExpectedSampleIntervalNanoseconds = LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(
                    _config.PicoTimebase > 0 ? _config.PicoTimebase : PressTestConfig.ResolvePicoTimebase(
                        _config.PicoTargetSampleRateHz > 0 ? _config.PicoTargetSampleRateHz : 125e6)),
                RampStepDelayMilliseconds = Math.Max(1, _config.RampStepDelayMilliseconds),
                RampStepAoVolts = 0.1,
                MaximumRampSeconds = 60,
                SuppressPicoStartupWindow = _config.SuppressPicoStartupWindow,
                TargetReachedToleranceFraction = LiveHardwareOptions.ClampTargetReachedToleranceFraction(_config.TargetReachedToleranceFraction),
                OverVoltageTripFraction = LiveHardwareOptions.ClampOverVoltageTripFraction(_config.OverVoltageTripFraction),
                ZeroBackgroundLimitV = _config.ZeroBackgroundLimitV > 0 ? _config.ZeroBackgroundLimitV : 500.0,
                EStopReadTimeoutMilliseconds = _config.EStopReadTimeoutMilliseconds > 0 ? _config.EStopReadTimeoutMilliseconds : 300,
                EStopPollIntervalMilliseconds = _config.EStopPollIntervalMilliseconds > 0 ? _config.EStopPollIntervalMilliseconds : 10,
                InterRoundCooldownMilliseconds = _config.InterRoundCooldownMilliseconds > 0 ? _config.InterRoundCooldownMilliseconds : 1000,
                SafetyPollMilliseconds = 10,
                ArtMonitorInputMinimumV = -5,
                ArtMonitorInputMaximumV = 5,
                HardOverCurrentA = PressTestConfig.TestBenchHardOverCurrentA,
                PicoExternalTriggerThresholdV = 3.0,
                PicoTriggerPositionPercent = LiveHardwareOptions.ClampTriggerPositionPercent(_config.PicoTriggerPositionPercent),
                PicoAutoTriggerMilliseconds = 800,
                PicoCaptureTimeoutMilliseconds = 1000,
                ArtCaptureTimeoutMilliseconds = 1000
            };
            // Live：Pico/ART 同窗时长取配置（默认 60ms）；FakeSmoke 仍 Short 以免拖慢
            double capMs = LiveHardwareOptions.ClampOscillationCaptureDurationMs(_config.OscillationCaptureDurationMs);
            _config.OscillationCaptureDurationMs = capMs;
            hw.OscillationCaptureDurationMs = capMs;
            if (_config.IsFakeLive)
                hw.ApplyCaptureProfile(PicoCaptureProfile.Short);
            else
                hw.ApplyPicoOscillationWindow(capMs);
            _log.WriteLine("采集窗：PicoN=" + hw.PicoSampleCount
                + " ART_N=" + hw.ArtFiniteSampleCount
                + " profile=" + hw.CaptureProfile
                + "（振荡/局放同窗 " + capMs.ToString("0.###", CultureInfo.InvariantCulture) + "ms）");

            LiveBenchCalibration cal = ResolveBenchCalibration();
            // AO 步长可调（界面 RampStepAoVolts）；未配/≤0 时按工程 60V/Gain 估算。步间延时默认 1ms。
            double gForStep = cal.VoltageGainVPerAoVolt > 1 ? cal.VoltageGainVPerAoVolt : PressTestConfig.TestBenchVoltageGain;
            const double targetEngStepV = 60.0;
            double aoStep = _config.RampStepAoVolts;
            if (!(aoStep > 0) || double.IsNaN(aoStep) || double.IsInfinity(aoStep))
                aoStep = targetEngStepV / gForStep;
            hw.RampStepAoVolts = Math.Max(0.002, Math.Min(0.1, aoStep));
            double engStepV = hw.RampStepAoVolts * gForStep;
            int stepDelayMs = Math.Max(1, _config.RampStepDelayMilliseconds);
            // 粗估：5kV / 步长 × 步间延时（未计源/缆慢响应）
            double estMsAt5kV = engStepV > 1e-9 ? (5000.0 / engStepV) * stepDelayMs : double.NaN;
            _log.WriteLine("台架标定: VoltageGain=" + cal.VoltageGainVPerAoVolt.ToString(CultureInfo.InvariantCulture)
                + " Divider=" + cal.DividerRatioVPerV.ToString(CultureInfo.InvariantCulture)
                + " RampStepAo=" + hw.RampStepAoVolts.ToString("G4", CultureInfo.InvariantCulture)
                + "（工程步≈" + engStepV.ToString("0.##", CultureInfo.InvariantCulture) + "V/步；界面可调；10mA+8uF∥20M）"
                + " RampStepDelay=" + stepDelayMs + "ms"
                + (double.IsNaN(estMsAt5kV) ? "" : (" 估5kV步进耗时≈" + (estMsAt5kV / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + "s（仅AO节拍，不含源/缆）"))
                + " notes=" + (cal.Notes ?? ""));
            if (Math.Abs(cal.VoltageGainVPerAoVolt - PressTestConfig.TestBenchVoltageGain) > 1e-6)
            {
                _log.WriteLine("警告：当前增益≠台架写死 6000；仍须非危险占位 1000。本程序约定 AO5V→30kV ⇒ Gain=6000、Divider=12000。");
            }

            if (_config.IsFakeLive)
            {
                _fake = new FakeLiveHardwareBackend(hw);
                _fake.SetEStopPressed(true); // Open 前默认急停；开压前再解除
                _live = new LiveAcquisitionSession(hw, _fake, new MissingDllFirstZeroPhaseEvaluator());
            }
            else
            {
                _live = new LiveAcquisitionSession(hw, null, new MissingDllFirstZeroPhaseEvaluator());
            }

            _session = new SingletonAcquisitionSession(_live);
            _session.Open(new AcquisitionSessionOptions
            {
                Simulation = false,
                ArtDeviceName = hw.ArtDeviceName,
                PicoSerial = hw.PicoSerial,
                PicoRangeIndex = hw.PicoRangeIndex,
                AoMaximumVolts = hw.AoMaximumVolts,
                TargetReachedToleranceFraction = hw.TargetReachedToleranceFraction,
                VoltageGainVPerAoVolt = 0, // 勿用选项占位覆盖台架 Apply
                HardOverCurrentA = hw.HardOverCurrentA
            });
            _live.ApplyBenchCalibration(cal);
            _log.WriteLine("会话 Open OK；BenchCalibrationApplied=" + _live.BenchCalibrationApplied
                + "；Pico=仅 SDK（suppressSplash=" + _config.SuppressPicoStartupWindow
                + "，不启 PicoScope.exe）");
        }

        LiveBenchCalibration ResolveBenchCalibration()
        {
            LiveBenchCalibration cal;
            if (!string.IsNullOrWhiteSpace(_config.BenchCalibrationFile) && File.Exists(_config.BenchCalibrationFile))
            {
                cal = LiveBenchCalibration.LoadFile(_config.BenchCalibrationFile);
            }
            else
            {
                double gain = !double.IsNaN(_config.VoltageGainVPerAoVolt) && _config.VoltageGainVPerAoVolt > 0
                    ? _config.VoltageGainVPerAoVolt
                    : PressTestConfig.TestBenchVoltageGain;
                double src = !double.IsNaN(_config.SourceVoltageScaleVPerV) && _config.SourceVoltageScaleVPerV > 0
                    ? _config.SourceVoltageScaleVPerV
                    : PressTestConfig.TestBenchSourceVoltageScale;
                cal = LiveBenchCalibration.CreateBenchSample(
                    gain,
                    _config.DividerRatioVPerV > 0 ? _config.DividerRatioVPerV : PressTestConfig.TestBenchDividerRatio,
                    _config.CurrentScaleAPerVolt > 0 ? _config.CurrentScaleAPerVolt : PressTestConfig.TestBenchCurrentScaleAPerVolt,
                    src,
                    "HvPressTest",
                    PressTestConfig.TestBenchGainNote);
            }

            // CLI/配置覆盖增益等
            if (!double.IsNaN(_config.VoltageGainVPerAoVolt) && _config.VoltageGainVPerAoVolt > 0)
                cal.VoltageGainVPerAoVolt = _config.VoltageGainVPerAoVolt;
            if (_config.DividerRatioVPerV > 0)
                cal.DividerRatioVPerV = _config.DividerRatioVPerV;
            if (_config.CurrentScaleAPerVolt > 0)
                cal.CurrentScaleAPerVolt = _config.CurrentScaleAPerVolt;
            if (!double.IsNaN(_config.SourceVoltageScaleVPerV) && _config.SourceVoltageScaleVPerV > 0)
                cal.SourceVoltageScaleVPerV = _config.SourceVoltageScaleVPerV;
            if (!(cal.SourceVoltageScaleVPerV > 0))
                cal.SourceVoltageScaleVPerV = PressTestConfig.TestBenchSourceVoltageScale;
            if (!(cal.CurrentScaleAPerVolt > 0) || Math.Abs(cal.CurrentScaleAPerVolt - 1.0) < 1e-12)
                cal.CurrentScaleAPerVolt = PressTestConfig.TestBenchCurrentScaleAPerVolt;
            cal.BenchCalibrated = true;
            if (string.IsNullOrEmpty(cal.Notes))
                cal.Notes = PressTestConfig.TestBenchGainNote;

            if (!cal.AllowsLiveNonZeroEnergize())
                throw new InvalidOperationException("台架标定未放行非零加压（增益不可为危险占位 1000）：gain="
                    + cal.VoltageGainVPerAoVolt.ToString(CultureInfo.InvariantCulture));
            return cal;
        }

        /// <summary>
        /// 校准采集：阻抗 → 探采自适量程 → 最佳档 → ChannelA 脉冲触发 Rapid（示波器式）→
        /// 固定间隔筛选 → 按页面 N 平均 → 须有有效信号 → SetCalibrated。
        /// IsBackgroundCapture=true；勿走加压列表门禁；阻抗随时可调。
        /// </summary>
        int RunCalibrationPhase(CancellationToken cancellation)
        {
            _log.WriteLine();
            _log.WriteLine("—— 校准采集（自适量程→最佳档→自动触发 Rapid + 间隔筛选 + N 平均；须有信号）——");
            if (_live == null)
            {
                AbortReason = "校准采集需要 LiveAcquisitionSession。";
                _log.WriteLine("ABORT: " + AbortReason);
                return 3;
            }

            if (_config.IsFakeLive)
            {
                // 演示保持按下态（与校准备急停按下门禁一致）；勿再解除
                if (!_session.GetStatus().EStopPressed)
                    _session.SetEStopPressed(true);
                _log.WriteLine("FakeLive：校准相位急停保持按下 EStopPressed=" + _session.GetStatus().EStopPressed);
            }

            uint tb = _config.PicoTimebase > 0
                ? _config.PicoTimebase
                : PressTestConfig.ResolvePicoTimebase(_config.PicoTargetSampleRateHz > 0 ? _config.PicoTargetSampleRateHz : 125e6);
            double intervalNs = LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(tb);
            var req = new CalibrationPulseRequest
            {
                ImpedanceBandwidthWide = _config.ImpedanceBandwidthWide,
                InitialPicoRangeIndex = _config.InitialPicoRangeIndex,
                MaxRangeRetries = _config.MaxRangeRetries,
                PicoResolutionBits = _config.PicoResolutionBits > 0 ? _config.PicoResolutionBits : 14,
                PicoTimebase = tb,
                PicoTargetSampleRateHz = _config.PicoTargetSampleRateHz > 0 ? _config.PicoTargetSampleRateHz : 125e6,
                SampleIntervalNanoseconds = intervalNs,
                CableLengthM = _config.CableLengthM > 0 ? _config.CableLengthM : CalibrationCaptureTiming.DefaultCableLengthM,
                VelocityMPerUs = _config.VelocityMPerUs > 0 ? _config.VelocityMPerUs : CalibrationCaptureTiming.DefaultVelocityMPerUs,
                PulseAverageCount = _config.PulseAverageCount > 0 ? _config.PulseAverageCount : 8,
                PeriodToleranceFraction = _config.PeriodToleranceFraction > 0
                    ? _config.PeriodToleranceFraction
                    : CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction,
                ExpectedPeriodNanoseconds = _config.ExpectedPeriodNanoseconds
            };

            var runner = new CalibrationPulseAcquisitionRunner();
            CalibrationPulseAcquisitionResult cal = runner.Run(_live, req, _log, cancellation);

            AcquisitionCycleResult cycle = cal.AveragedCycle ?? cal.ProbeCycle;
            if (cycle != null)
            {
                var metrics = BasicMetricsCalculator.Compute(cycle, _config);
                string stem = WaveformSummaryWriter.AppendCycle(
                    RunDirectory, CsvPath, 0, 0.0, cycle, metrics, ConfirmAoZero());
                _log.WriteLine("校准采集落盘: success=" + cal.Success
                    + " exitCode=" + cal.ExitCode
                    + " avgN=" + cal.AveragedCount
                    + " range=" + cal.RangeIndexUsed
                    + " picoN=" + (cycle.PicoRawAdc != null ? cycle.PicoRawAdc.Length : 0)
                    + " stem=" + stem);
            }
            else
            {
                _log.WriteLine("校准采集: success=" + cal.Success + " exitCode=" + cal.ExitCode
                    + " " + (cal.Message ?? ""));
            }

            // 校准量程常是探噪小档；不得覆盖加压「上次成功量程」记忆（否则同压又从原点重爬）
            if (cal.RangeIndexUsed >= 0)
            {
                if (_lastSuccessfulRangeIndex >= 0)
                {
                    _config.InitialPicoRangeIndex = _lastSuccessfulRangeIndex;
                    _log.WriteLine("校准量程 index=" + cal.RangeIndexUsed
                        + " 仅用于校准；加压沿用上次成功量程 index=" + _lastSuccessfulRangeIndex);
                }
                else
                    _config.InitialPicoRangeIndex = cal.RangeIndexUsed;
            }

            if (!cal.Success)
            {
                AbortedBySafety = cal.ExitCode == 4;
                AbortReason = cal.Message ?? ("校准采集失败 exit=" + cal.ExitCode);
                _log.WriteLine("ABORT: " + AbortReason);
                return cal.ExitCode != 0 ? cal.ExitCode : 3;
            }

            if (!ConfirmAoZero())
            {
                AbortedBySafety = true;
                AbortReason = "校准态后 AO 未归零。";
                _log.WriteLine("ABORT: " + AbortReason);
                return 3;
            }

            // 按注入放电量自动算校准系数：pcPerVolt = injectedPc / peakV
            LastCalibrationPeakVolts = double.NaN;
            LastComputedPdCalibrationPcPerVolt = double.NaN;
            LastInjectedChargePc = _config.InjectedChargePc > 0
                ? _config.InjectedChargePc
                : PdCalibrationStore.DefaultInjectedChargePc;
            if (cycle == null
                || cycle.PicoRawAdc == null
                || cycle.PicoRawAdc.Length == 0
                || cal.AveragedCount <= 0)
            {
                AbortedBySafety = false;
                AbortReason = StatusCopy.NoCalibrationWaveform;
                _log.WriteLine("ABORT: " + AbortReason);
                return 5;
            }

            double peakV;
            if (!BasicMetricsCalculator.TryPdPeakVolts(cycle, out peakV) || !(peakV > 0))
            {
                AbortedBySafety = false;
                AbortReason = StatusCopy.NoCalibrationWaveform;
                _log.WriteLine("ABORT: " + AbortReason + "（无有效峰压）");
                return 5;
            }
            LastCalibrationPeakVolts = peakV;
            // 相对满量程过低 → 同「无校准波形」
            if (cycle.PicoFullScaleVolts > 0
                && peakV < cycle.PicoFullScaleVolts * CalibrationPulseAcquisitionRunner.MinPeakFractionOfFullScale)
            {
                AbortedBySafety = false;
                AbortReason = StatusCopy.NoCalibrationWaveform;
                _log.WriteLine("ABORT: " + AbortReason
                    + " peakV=" + peakV.ToString("G6", CultureInfo.InvariantCulture)
                    + " FS=" + cycle.PicoFullScaleVolts.ToString("G6", CultureInfo.InvariantCulture));
                return 5;
            }

            double factor;
            string ferr;
            if (PdCalibrationStore.TryComputePcPerVolt(LastInjectedChargePc, peakV, out factor, out ferr))
            {
                LastComputedPdCalibrationPcPerVolt = factor;
                _config.PdCalibrationPcPerVolt = factor;
                _log.WriteLine("校准系数自动计算：injected=" + LastInjectedChargePc.ToString("G6", CultureInfo.InvariantCulture)
                    + " pC (" + (LastInjectedChargePc / 1000.0).ToString("G6", CultureInfo.InvariantCulture)
                    + " nC) / peakV=" + peakV.ToString("G6", CultureInfo.InvariantCulture)
                    + " V → pdCalibrationPcPerVolt=" + factor.ToString("G9", CultureInfo.InvariantCulture)
                    + "；自检 Q=peak×cal=" + (peakV * factor).ToString("G6", CultureInfo.InvariantCulture)
                    + " pC（应≈注入）"
                    + "（" + PdCalibrationStore.FormulaId + "）"
                    + " bits=" + cycle.PicoResolutionBits
                    + " intervalNs=" + cycle.PicoSampleIntervalNanoseconds.ToString("0.###", CultureInfo.InvariantCulture));
            }
            else
                _log.WriteLine("校准系数未算出：" + (ferr ?? ""));

            // 勿调用 PrepareInterlocks：那是「开始加压列表」门禁，会在急停按下时误报「拒绝开始加压列表」。
            // Runner 已 SetCalibrated；此处仅确认会话标记。
            _session.SetCalibrated(true);
            _log.WriteLine("校准采集完成；已标记已校准（≠ 可加压；开始加压仍须急停解除）。");
            return 0;
        }


        /// <summary>
        /// 操作员停止/取消：停加压循环，量程不动（不写 InitialPicoRangeIndex / 不 ApplyPico 升档），
        /// 跳过 range advise 驱动的升档重加；关源不永久锁存。
        /// </summary>
        int FinishOperatorStop(AcquisitionCycleResult cycle, int step, double targetKv, string detail)
        {
            AbortedBySafety = false;
            SafetyAbortBlocksRepress = false;
            AbortReason = "操作员停止：已关源（未永久锁存）";
            if (cycle != null)
            {
                // 丢弃建议量程，避免下游把 Suggested 当升档依据
                cycle.SuggestedPicoRangeIndex = cycle.PicoRangeIndexUsed >= 0
                    ? cycle.PicoRangeIndexUsed
                    : (_config != null ? _config.InitialPicoRangeIndex : -1);
                cycle.Task6RangeSuitable = true;
                if (string.IsNullOrEmpty(cycle.Task6RangeMessage)
                    || cycle.Task6RangeMessage.IndexOf("操作员停止", StringComparison.Ordinal) < 0)
                    cycle.Task6RangeMessage = "操作员停止：跳过量程核算/升档重加";
                var metricsStop = BasicMetricsCalculator.Compute(cycle, _config);
                WaveformSummaryWriter.AppendCycle(RunDirectory, CsvPath, step, targetKv, cycle, metricsStop, true);
            }
            _log.WriteLine("STOP: " + AbortReason + " " + (detail ?? "") + "；量程保持 index="
                + (_config != null ? _config.InitialPicoRangeIndex.ToString(CultureInfo.InvariantCulture) : "?")
                + "；可再次开始加压。");
            try
            {
                string clrStop;
                if (_live != null && _live.TryClearEmergencyLatchIfHardwareSafe(out clrStop)
                    && !string.IsNullOrEmpty(clrStop))
                    _log.WriteLine("停止后清软锁存：" + clrStop);
            }
            catch { }
            return 6;
        }

        bool ConfirmAoZero()
        {
            if (_fake != null)
                return Math.Abs(_fake.LastAoVolts) < 1e-6 && !_fake.HvOn && !_fake.HvIndicatorOn;
            // Live：依赖会话 Assert；此处读状态
            return !_live.Backend.HvOn && Math.Abs(_live.Backend.LastAoVolts) < 1e-6;
        }

        /// <summary>真急停/过流/过压才硬停批。日志含「非急停中止/缓存失效」即使误标 EmergencyStop 也不当硬安全。</summary>
        static bool IsHardSafetyExit(AcquisitionCycleResult cycle)
        {
            if (cycle == null) return true;
            if (IsTransientNonSafetyAbort(cycle)) return false;
            switch (cycle.ExitReason)
            {
                case AcquisitionExitReason.EmergencyStop:
                case AcquisitionExitReason.HardOverCurrent:
                case AcquisitionExitReason.OverVoltage:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>缓存失效/取消且 P0.4 未按下等短暂中止：可重试本轮，勿整批当急停停死。</summary>
        static bool IsTransientNonSafetyAbort(AcquisitionCycleResult cycle)
        {
            if (cycle == null || cycle.Success) return false;
            string m = cycle.ErrorMessage ?? "";
            if (m.IndexOf("非急停中止", StringComparison.Ordinal) < 0
                && m.IndexOf("缓存失效", StringComparison.Ordinal) < 0)
                return false;
            // 过流/过压仍硬停，即使消息碰巧含上述字样
            if (cycle.ExitReason == AcquisitionExitReason.HardOverCurrent
                || cycle.ExitReason == AcquisitionExitReason.OverVoltage)
                return false;
            return true;
        }


        /// <summary>自动量程起始索引：上次成功 &gt; 配置初值；禁止无记忆时以外的静默回原点。</summary>
        int ResolveStartingRangeIndex()
        {
            if (_config.AutoRangeAdjust && _lastSuccessfulRangeIndex >= 0)
            {
                _config.InitialPicoRangeIndex = _lastSuccessfulRangeIndex;
                ApplyRangeToLiveHardware(_lastSuccessfulRangeIndex);
                return _lastSuccessfulRangeIndex;
            }
            int idx = _config.InitialPicoRangeIndex >= 0 ? _config.InitialPicoRangeIndex : 6;
            return idx;
        }

        void RememberSuccessfulRange(int rangeIndex, double targetKv)
        {
            if (rangeIndex < 0) return;
            _lastSuccessfulRangeIndex = rangeIndex;
            _lastSuccessfulRangeKv = targetKv;
            if (_config != null) _config.InitialPicoRangeIndex = rangeIndex;
        }

        void ApplyRangeToLiveHardware(int rangeIndex)
        {
            if (_live == null || rangeIndex < 0) return;
            try
            {
                _live.ApplyPicoCaptureSettings(
                    _config.PicoResolutionBits > 0 ? _config.PicoResolutionBits : 14,
                    _config.PicoTimebase > 0 ? _config.PicoTimebase : 3,
                    _config.PicoTargetSampleRateHz > 0 ? _config.PicoTargetSampleRateHz : 125e6,
                    rangeIndex);
            }
            catch (Exception ex)
            {
                _log.WriteLine("沿用成功量程写硬件跳过: " + ex.Message);
            }
        }

        /// <summary>
        /// 手动量程：不自动切档；超量程提示（增大量程可跳一档）；峰&lt;20%FS 按需求文案提示「量程过小」。
        /// </summary>
        void LogManualRangeHints(AcquisitionCycleResult cycle, int currentRangeIndex)
        {
            if (cycle == null) return;
            int peak = 0;
            if (cycle.PicoRawAdc != null)
            {
                for (int i = 0; i < cycle.PicoRawAdc.Length; i++)
                {
                    int a = Math.Abs((int)cycle.PicoRawAdc[i]);
                    if (a > peak) peak = a;
                }
            }
            int maxAdc = cycle.PicoMaximumAdc > 0 ? cycle.PicoMaximumAdc : 0;
            double util = maxAdc > 0 ? peak / (double)maxAdc : 0;
            double[] fsTable = Task6RangeAdvisor.Pico5242DFullScaleVolts;
            int maxIdx = fsTable != null && fsTable.Length > 0 ? fsTable.Length - 1 : currentRangeIndex;

            bool over = cycle.PicoOverrangeOrClipped || util >= Task6RangeAdvisor.NearFullScaleFraction;
            if (over)
            {
                int suggest = cycle.SuggestedPicoRangeIndex >= 0 ? cycle.SuggestedPicoRangeIndex : currentRangeIndex;
                int skipOne = Math.Min(currentRangeIndex + 2, maxIdx); // 增大量程可跳一档
                if (suggest < skipOne)
                    suggest = skipOne;
                if (suggest <= currentRangeIndex)
                    suggest = Math.Min(currentRangeIndex + 1, maxIdx);
                string fs = (fsTable != null && suggest >= 0 && suggest < fsTable.Length)
                    ? ("±" + fsTable[suggest].ToString("G4", CultureInfo.InvariantCulture) + " V")
                    : ("索引 " + suggest);
                _log.WriteLine("手动量程提示：超量程/削顶（峰/FS=" + (util * 100).ToString("0.0", CultureInfo.InvariantCulture)
                    + "%）；建议升到 " + fs + "（索引 " + suggest + "，可跳一档）；未自动切换。");
                return;
            }

            // 需求：量程过小判定 = 信号最大值 < 量程 20%（利用率过低；不自动降档）
            if (maxAdc > 0 && util < 0.20 && currentRangeIndex > 0)
            {
                int suggest = currentRangeIndex - 1;
                if (cycle.SuggestedPicoRangeIndex >= 0 && cycle.SuggestedPicoRangeIndex < currentRangeIndex)
                    suggest = cycle.SuggestedPicoRangeIndex;
                string fs = (fsTable != null && suggest >= 0 && suggest < fsTable.Length)
                    ? ("±" + fsTable[suggest].ToString("G4", CultureInfo.InvariantCulture) + " V")
                    : ("索引 " + suggest);
                _log.WriteLine("手动量程提示：量程过小判定（信号最大值/FS=" + (util * 100).ToString("0.0", CultureInfo.InvariantCulture)
                    + "% < 20%）；建议降到 " + fs + "（索引 " + suggest + "）；未自动切换。");
                return;
            }

            if (!cycle.Task6RangeSuitable && !string.IsNullOrEmpty(cycle.Task6RangeMessage))
            {
                string msg = StripReapplyWording(cycle.Task6RangeMessage);
                _log.WriteLine("手动量程提示：" + msg + "；未自动切换。");
            }
        }

        /// <summary>手动量程落盘/日志：去掉 Task6 文案里的「重加本轮」，避免与「仅提示、不重加」混淆。</summary>
        static void StripReapplyWordingFromCycle(AcquisitionCycleResult cycle)
        {
            if (cycle == null || string.IsNullOrEmpty(cycle.Task6RangeMessage)) return;
            cycle.Task6RangeMessage = StripReapplyWording(cycle.Task6RangeMessage);
        }

        static string StripReapplyWording(string message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? "";
            string m = message;
            // Task6 固定后缀：「…后重加本轮。」/「…后重加本轮」
            m = m.Replace("后重加本轮。", "。", StringComparison.Ordinal);
            m = m.Replace("后重加本轮", "", StringComparison.Ordinal);
            m = m.Replace("重加本轮。", "。", StringComparison.Ordinal);
            m = m.Replace("重加本轮", "", StringComparison.Ordinal);
            while (m.Contains("。。", StringComparison.Ordinal))
                m = m.Replace("。。", "。", StringComparison.Ordinal);
            return m.Trim();
        }

        static string StatusOrVal(string status, double value, string unit)
        {
            if (status != "Ok" || double.IsNaN(value)) return "NotAvailable";
            return value.ToString("G6", CultureInfo.InvariantCulture) + unit;
        }

        void WriteRunManifest()
        {
            var sb = new System.Text.StringBuilder();
            double gain = !double.IsNaN(_config.VoltageGainVPerAoVolt) && _config.VoltageGainVPerAoVolt > 0
                ? _config.VoltageGainVPerAoVolt : PressTestConfig.TestBenchVoltageGain;
            sb.AppendLine("program=Xiongxin.Owts.HvPressTest");
            sb.AppendLine("brand=雄新电力");
            sb.AppendLine("product=雄新 OWTS 出厂加压测试");
            sb.AppendLine("purpose=verify-press-and-capture-only");
            sb.AppendLine("mode=" + _config.Mode);
            sb.AppendLine("voltageGainNote=" + PressTestConfig.TestBenchGainNote);
            sb.AppendLine("voltageGainVPerAoVolt=" + gain.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("runCalibrationFirst=" + _config.RunCalibrationFirst);
            sb.AppendLine("continuousPress=true");
            sb.AppendLine("autoRangeAdjust=" + (_config.AutoRangeAdjust ? "true" : "false"));
            sb.AppendLine("suppressPicoStartupWindow=" + _config.SuppressPicoStartupWindow);
            sb.AppendLine("picoPolicy=SDK-only-session-hold-no-PicoScope.exe");
            sb.AppendLine("liveRules=docs/LIVE_VALIDATION_RULES.md");
            sb.AppendLine("targetReachedToleranceFraction=" + _config.TargetReachedToleranceFraction.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("overVoltageTripFraction=" + _config.OverVoltageTripFraction.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("zeroBackgroundLimitV=" + _config.ZeroBackgroundLimitV.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("eStopReadTimeoutMilliseconds=" + _config.EStopReadTimeoutMilliseconds);
            sb.AppendLine("eStopPollIntervalMilliseconds=" + _config.EStopPollIntervalMilliseconds);
            sb.AppendLine("interRoundCooldownMilliseconds=" + _config.InterRoundCooldownMilliseconds);
            sb.AppendLine("batchRevokeOnStopEstopUnknownDisconnect=true");
            sb.AppendLine("inductanceH=" + _config.InductanceH.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("pdCalibrationPcPerVolt=" + (_config.HasPdCalibration
                ? _config.PdCalibrationPcPerVolt.ToString(CultureInfo.InvariantCulture) : "null"));
            sb.AppendLine("maxRangeRetries=" + _config.MaxRangeRetries);
            sb.AppendLine("initialPicoRangeIndex=" + _config.InitialPicoRangeIndex);
            sb.AppendLine("pulseAverageCount=" + _config.PulseAverageCount);
            sb.AppendLine("cableLengthM=" + _config.CableLengthM.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("velocityMPerUs=" + _config.VelocityMPerUs.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("periodToleranceFraction=" + _config.PeriodToleranceFraction.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("picoResolutionBits=" + _config.PicoResolutionBits);
            sb.AppendLine("picoTargetSampleRateHz=" + _config.PicoTargetSampleRateHz.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine("picoTimebase=" + _config.PicoTimebase);
            sb.AppendLine("targetsKv=" + string.Join(",", _config.TargetsKv.ConvertAll(
                k => k.ToString(CultureInfo.InvariantCulture))));
            sb.AppendLine("jointStatusPolicy=NotAvailable(no location/defect analysis)");
            sb.AppendLine("startedLocal=" + DateTime.Now.ToString("o"));
            File.WriteAllText(Path.Combine(RunDirectory, "run-manifest.txt"), sb.ToString());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_stopCts != null) _stopCts.Cancel(); } catch { }
            try { if (_session != null) _session.Close(); } catch { }
            if (_live != null && _live.CleanupFailed)
                _cleanupFailedOnClose = true;
            if (SingletonAcquisitionSession.ProcessCleanupFailed)
                _cleanupFailedOnClose = true;
            try { if (_session != null) _session.Dispose(); } catch { }
            _session = null;
            _live = null;
            try { if (_fake != null) _fake.Dispose(); } catch { }
            _fake = null;
            _connected = false;
            try { if (_stopCts != null) _stopCts.Dispose(); } catch { }
            _stopCts = null;
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PressTestRunner));
        }
    }
}
