using System;
using System.Threading;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>联锁/采集/残压单测后端：不加载驱动。</summary>
    public sealed class FakeLiveHardwareBackend : ILiveHardwareBackend
    {
        readonly object _sync = new object();
        bool _open, _disposed, _estopPressed = true, _hvOn, _hvIndicator, _lamp, _pico;
        double _ao, _voltageGainVPerAo = 1000, _injectedCurrentA;
        double _injectedDividerOverride = double.NaN;
        double _residualDividerV, _residualSourceV;
        int _aoWriteCount, _forceZeroCount, _picoOpenCount, _captureCount, _dividerCaptureCount;
        bool _estopDuringCapture, _overflowNextCapture;
        int _estopReadFailRemaining;
        /// <summary>true：ReadFeedback 的 SourceVoltageV 视为 AI2 原始伏特（走会话侧 SourceVoltageScale）。默认 false=已是工程值（兼容旧残压注入）。</summary>
        bool _sourceFeedbackIsRaw;
        bool _finiteArmed, _blockArmed, _rapidArmed;
        int _armedRange = 6;
        RapidBlockArmRequest _rapidRequest;
        bool _requireFreshWindowAfterAo;
        int _injectedAiBacklog;
        int _freshWindowCalls;
        Action _onArmed;
        string _openedArtDeviceName = string.Empty;
        string _openedPicoSerial = string.Empty;
        readonly LiveHardwareOptions _hw;

        public FakeLiveHardwareBackend() : this(null) { }

        /// <summary>与会话共用 LiveHardwareOptions，加压 Block / 校准 Rapid 采样率·分辨率一致。</summary>
        public FakeLiveHardwareBackend(LiveHardwareOptions options)
        {
            _hw = options ?? new LiveHardwareOptions();
        }

        /// <summary>最近一次 Open 传入的 ART 设备名（双身份校验用）。</summary>
        public string OpenedArtDeviceName { get { lock (_sync) return _openedArtDeviceName ?? string.Empty; } }
        /// <summary>最近一次 Open 传入的 Pico 序列号（空=第一台）。</summary>
        public string OpenedPicoSerial { get { lock (_sync) return _openedPicoSerial ?? string.Empty; } }

        public bool IsOpen { get { return _open && !_disposed; } }
        public double LastAoVolts { get { lock (_sync) return _ao; } }
        public bool HvOn { get { return _hvOn; } }
        public bool HvIndicatorOn { get { return _hvIndicator; } }
        public bool ConnectionLampHigh { get { return _lamp; } }
        public bool PicoOpen { get { return _pico; } }
        public int AoWriteCount { get { return _aoWriteCount; } }
        public int ForceZeroCount { get { return _forceZeroCount; } }
        public int PicoOpenCount { get { return _picoOpenCount; } }
        public int CaptureCount { get { return _captureCount; } }
        public int DividerCaptureCount { get { return _dividerCaptureCount; } }

        public void SetEStopPressed(bool pressed) { _estopPressed = pressed; }
        public void SetVoltageGain(double gain) { _voltageGainVPerAo = gain; }
        public void InjectCurrentA(double a) { _injectedCurrentA = a; }
        public void InjectDividerVoltageV(double v) { _injectedDividerOverride = v; }
        /// <summary>设置关源后 ReadFeedback 残压。默认按工程电压（SourceFeedbackIsRaw=false）；测 AI2 标定时用 InjectResidualRaw。</summary>
        public void InjectResidual(double dividerV, double sourceV) { _residualDividerV = dividerV; _residualSourceV = sourceV; }
        public void ClearResidual() { _residualDividerV = 0; _residualSourceV = 0; }
        /// <summary>true 时 SourceVoltageV 为 AI2 原始 V，由 LiveAcquisitionSession × SourceVoltageScaleVPerV。</summary>
        public bool SourceFeedbackIsRaw
        {
            get { return _sourceFeedbackIsRaw; }
            set { _sourceFeedbackIsRaw = value; }
        }
        /// <summary>注入关源后残压；当 SourceFeedbackIsRaw 时 sourceV 为 AI2 原始伏特。</summary>
        public void InjectResidualRaw(double dividerEngineeringV, double sourceRawAi2V)
        {
            _sourceFeedbackIsRaw = true;
            _residualDividerV = dividerEngineeringV;
            _residualSourceV = sourceRawAi2V;
        }
        public void ArmEStopDuringNextCapture() { _estopDuringCapture = true; }
        /// <summary>随后 N 次 ReadEStopSample 返回 ReadOk=false（测空闲重读/重建）。</summary>
        public void ArmEStopReadFailures(int count) { _estopReadFailRemaining = count < 0 ? 0 : count; }
        public void ArmOverflowOnNextCapture() { _overflowNextCapture = true; }
        /// <summary>注入 AI 监测积压点数/通道；&gt;MaximumQueue 时下一拍新鲜窗读应故障。</summary>
        public void InjectAiBacklog(int samplesPerChannel) { _injectedAiBacklog = samplesPerChannel < 0 ? 0 : samplesPerChannel; }
        public int FreshWindowCalls { get { return _freshWindowCalls; } }
        public bool RequireFreshWindowPending { get { return _requireFreshWindowAfterAo; } }

        public string ProbeDrivers() { return string.Empty; }

        public void Open(string artDeviceName, string picoSerial)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                if (_open) throw new InvalidOperationException("FakeLive 已 Open。");
                _open = true; _pico = true; _picoOpenCount++;
                _ao = 0; _hvOn = _hvIndicator = _lamp = false;
                _openedArtDeviceName = string.IsNullOrWhiteSpace(artDeviceName) ? "Dev1" : artDeviceName.Trim();
                _openedPicoSerial = picoSerial == null ? string.Empty : picoSerial.Trim();
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                AbortFiniteDividerCapture();
                AbortBlockCapture();
                _ao = 0; _forceZeroCount++; _aoWriteCount++;
                _hvOn = _hvIndicator = false; _lamp = false; _pico = false; _open = false;
                _openedArtDeviceName = string.Empty;
                _openedPicoSerial = string.Empty;
            }
        }

        public void WriteAoVolts(double volts)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("FakeLive 未 Open。");
                if (_estopPressed && volts > 1e-9)
                    throw new InvalidOperationException("SAFETY: FakeLive 拒绝在急停按下时写非零 AO。");
                _ao = volts; _aoWriteCount++;
                if (Math.Abs(volts) < 1e-12) _forceZeroCount++;
                _requireFreshWindowAfterAo = volts > 1e-9;
            }
        }

        public void EnsureMonitorAiTask()
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("FakeLive 未 Open。");
                // FakeLive 无独立 AI 任务；Open 后即可读反馈
            }
        }

        public LiveFeedbackSample ReadFeedback()
        {
            lock (_sync)
            {
                bool fresh = _requireFreshWindowAfterAo;
                // 积压超限：对照 Factory MaximumQueue=1000 → 故障（会话应 HV OFF）
                if (fresh && _injectedAiBacklog > ArtFreshFeedbackWindow.MaximumQueue)
                {
                    int backlog = _injectedAiBacklog;
                    throw new InvalidOperationException("ART 缓存积压超过1秒：未读=" + backlog
                        + "点/通道，追读组=0；禁止继续升压。");
                }

                LiveFeedbackSample sample;
                // 源关闭且有注入残压时，反馈残压（放电确认路径）
                if (Math.Abs(_ao) < 1e-9 && !_hvOn && (_residualDividerV != 0 || _residualSourceV != 0))
                {
                    sample = new LiveFeedbackSample
                    {
                        CurrentA = 0,
                        SourceVoltageV = _residualSourceV,
                        DividerVoltageV = _residualDividerV,
                        DividerRawV = _residualDividerV,
                        WindowSampleCount = ArtFreshFeedbackWindow.DefaultWindowSamples,
                        SampleCount = ArtFreshFeedbackWindow.DefaultWindowSamples
                    };
                }
                else
                {
                    double divider = !double.IsNaN(_injectedDividerOverride) ? _injectedDividerOverride : _ao * _voltageGainVPerAo;
                    sample = new LiveFeedbackSample
                    {
                        CurrentA = _injectedCurrentA,
                        SourceVoltageV = _sourceFeedbackIsRaw ? _ao : (_ao * _voltageGainVPerAo),
                        SourceVoltageRawV = _ao,
                        DividerVoltageV = divider,
                        DividerRawV = divider / Math.Max(_voltageGainVPerAo, 1e-9),
                        WindowSampleCount = ArtFreshFeedbackWindow.DefaultWindowSamples,
                        SampleCount = ArtFreshFeedbackWindow.DefaultWindowSamples,
                        PeakCurrentAbs = Math.Abs(_injectedCurrentA),
                        PeakDividerAbs = Math.Abs(divider)
                    };
                }

                if (fresh)
                {
                    _freshWindowCalls++;
                    sample.FreshWindowAfterAoCommand = true;
                    sample.FreshWindowBoundaryQueued = _injectedAiBacklog;
                    sample.FreshWindowSamplesAfterBoundary = ArtFreshFeedbackWindow.DefaultWindowSamples;
                    sample.BacklogBefore = _injectedAiBacklog;
                    sample.BacklogAfter = 0;
                    // 成功新鲜窗消费边界前积压
                    _injectedAiBacklog = 0;
                }
                _requireFreshWindowAfterAo = false;
                return sample;
            }
        }

        public void WriteDigitalOutputs(bool hvOn, bool hvIndicator, bool connectionLamp, bool groundTrigger)
        {
            lock (_sync)
            {
                if (_estopPressed && hvOn)
                    throw new InvalidOperationException("SAFETY: FakeLive 拒绝在急停按下时接通高压继电器。");
                _hvOn = hvOn; _hvIndicator = hvIndicator; _lamp = connectionLamp;
            }
        }

        public bool ReadEStopPressed() { return _estopPressed; }

        public LiveEStopSample ReadEStopSample()
        {
            if (_estopReadFailRemaining > 0)
            {
                _estopReadFailRemaining--;
                return new LiveEStopSample
                {
                    ReadOk = false,
                    Pressed = false,
                    SafetyReadyActiveHigh = true,
                    Error = "FakeLive 注入 P0.4 读超时"
                };
            }
            // Fake 模拟 SafetyReadyActiveHigh=true：解除=物理高，按下=物理低
            bool pressed = _estopPressed;
            bool physicalHigh = !pressed;
            return new LiveEStopSample
            {
                RawDi = physicalHigh ? (byte)1 : (byte)0,
                PhysicalHigh = physicalHigh,
                Pressed = pressed,
                SafetyReadyActiveHigh = true,
                ReadOk = true
            };
        }

        public void ArmFiniteDividerCapture()
        {
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("FakeLive 未 Open。");
                if (_finiteArmed) throw new InvalidOperationException("Finite AI 已布防。");
                _finiteArmed = true;
            }
        }

        public double[] ReadFiniteDividerCapture(int timeoutMilliseconds)
        {
            lock (_sync)
            {
                if (!_finiteArmed) throw new InvalidOperationException("Finite AI 未布防。");
                _finiteArmed = false;
                _dividerCaptureCount++;
                int n = _hw.ArtFiniteSampleCount > 0 ? _hw.ArtFiniteSampleCount : 4800;
                int artPre, artPost;
                LiveHardwareOptions.SplitTriggerSamples(n, _hw.PicoTriggerPositionPercent, out artPre, out artPost);
                var data = new double[n];
                // 触发事件放在 pre 处，与 Pico 触发位置% 对齐（同步采集）
                int spike = artPre > 0 ? artPre : Math.Min(800, n / 5);
                for (int i = 0; i < n; i++)
                    data[i] = 0.2 * Math.Sin(2 * Math.PI * (i - artPre) / 160.0) + (i == spike ? 1.5 : 0);
                return data;
            }
        }

        public void AbortFiniteDividerCapture()
        {
            lock (_sync) { _finiteArmed = false; }
        }

        public void ConfigureBlockTriggerOnly(bool autoTriggerOnly)
        {
            _ = autoTriggerOnly; // Fake：无硬件触发寄存器
        }

        public void ArmBlockCapture(int rangeIndex, Action onArmed, bool autoTriggerOnly, bool skipCooldownGate = false)
        {
            lock (_sync)
            {
                if (!_pico) throw new InvalidOperationException("FakeLive Pico 未 Open（禁止在采集里 OpenUnit）。");
                if (rangeIndex < 0 || rangeIndex >= LiveHardwareOptions.Pico5242DFullScaleVolts.Length)
                    throw new ArgumentOutOfRangeException("rangeIndex");
                _armedRange = rangeIndex;
                _blockArmed = true;
                _onArmed = onArmed;
                _ = autoTriggerOnly; // 0V 自动 / 非0 数字：Fake 行为相同
                _ = skipCooldownGate;
            }
            if (onArmed != null) onArmed();
        }

        public PicoBlockCaptureResult ReadBlockCapture(int timeoutMilliseconds, CancellationToken cancellation)
        {
            lock (_sync)
            {
                if (!_blockArmed) throw new InvalidOperationException("Pico 块采集未布防。");
                if (_estopDuringCapture)
                {
                    _estopDuringCapture = false;
                    _estopPressed = true;
                    _blockArmed = false;
                    throw new OperationCanceledException("FakeLive：采集中急停按下，已 Abort。");
                }
            }
            cancellation.ThrowIfCancellationRequested();
            const int n = 4096;
            var adc = new short[n];
            bool overflow;
            lock (_sync)
            {
                overflow = _overflowNextCapture;
                _overflowNextCapture = false;
                for (int i = 0; i < n; i++)
                {
                    double v = overflow ? 30000 : 800 * Math.Sin(2 * Math.PI * i / 512.0);
                    if (!overflow && (i == 1000 || i == 2600)) v += 12000;
                    adc[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v));
                }
                _captureCount++;
                _blockArmed = false;
            }
            double intervalNs = _hw.PicoExpectedSampleIntervalNanoseconds > 0
                ? _hw.PicoExpectedSampleIntervalNanoseconds
                : LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(_hw.PicoTimebase);
            return new PicoBlockCaptureResult
            {
                RawAdc = adc,
                SampleIntervalNanoseconds = intervalNs,
                SampleRateHz = 1e9 / intervalNs,
                FullScaleVolts = LiveHardwareOptions.Pico5242DFullScaleVolts[_armedRange],
                MaximumAdc = 32512,
                RangeIndex = _armedRange,
                HardwareOverflow = overflow,
                Serial = "FAKE-PICO"
            };
        }

        public void AbortBlockCapture()
        {
            lock (_sync) { _blockArmed = false; _onArmed = null; }
        }

        public void ArmRapidBlockCapture(RapidBlockArmRequest request, Action onArmed)
        {
            if (request == null) throw new ArgumentNullException("request");
            lock (_sync)
            {
                if (!_pico) throw new InvalidOperationException("FakeLive Pico 未 Open（禁止在采集里 OpenUnit）。");
                if (request.RangeIndex < 0 || request.RangeIndex >= LiveHardwareOptions.Pico5242DFullScaleVolts.Length)
                    throw new ArgumentOutOfRangeException("rangeIndex");
                _armedRange = request.RangeIndex;
                _rapidRequest = request;
                _rapidArmed = true;
                _blockArmed = false;
                _onArmed = onArmed;
            }
            if (onArmed != null) onArmed();
        }

        public PicoRapidBlockCaptureResult ReadRapidBlockCaptures(int timeoutMilliseconds, CancellationToken cancellation)
        {
            RapidBlockArmRequest req;
            lock (_sync)
            {
                if (!_rapidArmed) throw new InvalidOperationException("Pico Rapid 未布防。");
                if (_estopDuringCapture)
                {
                    _estopDuringCapture = false;
                    _estopPressed = true;
                    _rapidArmed = false;
                    throw new OperationCanceledException("FakeLive：Rapid 采集中急停按下，已 Abort。");
                }
                req = _rapidRequest ?? new RapidBlockArmRequest();
            }
            cancellation.ThrowIfCancellationRequested();
            int n = PicoRapidBlockBulkOverlapped.ClampCaptureCount(req.CaptureCount);
            int pre = Math.Max(0, req.PreTriggerSamples);
            int post = Math.Max(1, req.PostTriggerSamples);
            int samples = pre + post;
            // 固定间隔 2.5 ms = 400Hz（现场常用）；夹杂 1 段干扰便于筛选测试
            const long periodNs = 2500000;
            var captures = new short[n][];
            var times = new long[n];
            var units = new int[n];
            var overflow = new short[n];
            bool overflowFlag;
            lock (_sync)
            {
                overflowFlag = _overflowNextCapture;
                _overflowNextCapture = false;
                _captureCount++;
                _rapidArmed = false;
            }
            for (int c = 0; c < n; c++)
            {
                captures[c] = new short[samples];
                units[c] = 2; // nanoseconds
                // 最后一段故意偏周期（干扰），其余固定间隔
                times[c] = c == n - 1 && n > 2
                    ? (c * periodNs + periodNs / 2)
                    : (c * periodNs);
                for (int i = 0; i < samples; i++)
                {
                    double v = overflowFlag ? 30000 : 600 * Math.Sin(2 * Math.PI * i / 64.0);
                    if (!overflowFlag && i == pre + 8) v += 10000;
                    captures[c][i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, v));
                }
                overflow[c] = overflowFlag ? (short)1 : (short)0;
            }
            double intervalNs = _hw.PicoExpectedSampleIntervalNanoseconds > 0
                ? _hw.PicoExpectedSampleIntervalNanoseconds
                : LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(_hw.PicoTimebase);
            return new PicoRapidBlockCaptureResult
            {
                Captures = captures,
                TriggerTimeOffsets = times,
                TriggerTimeUnits = units,
                OverflowPerCapture = overflow,
                SampleIntervalNanoseconds = intervalNs,
                SampleRateHz = 1e9 / intervalNs,
                FullScaleVolts = LiveHardwareOptions.Pico5242DFullScaleVolts[_armedRange],
                MaximumAdc = 32512,
                RangeIndex = _armedRange,
                PreTriggerSamples = pre,
                PostTriggerSamples = post,
                CaptureCount = n,
                Serial = "FAKE-PICO"
            };
        }

        public void AbortRapidBlockCapture()
        {
            lock (_sync) { _rapidArmed = false; _rapidRequest = null; _onArmed = null; }
        }

        /// <summary>测试：模拟仅 Pico 掉线（Art 仍 Open）。</summary>
        public void SimulatePicoDrop()
        {
            lock (_sync)
            {
                AbortBlockCapture();
                AbortRapidBlockCapture();
                _pico = false;
            }
        }

        public void ClosePicoOnly()
        {
            lock (_sync)
            {
                AbortBlockCapture();
                AbortRapidBlockCapture();
                _pico = false;
            }
        }

        public bool TryReopenPico(string picoSerial, out string error)
        {
            error = null;
            lock (_sync)
            {
                if (!_open)
                {
                    error = "FakeLive 会话未 Open，无法重开 Pico。";
                    return false;
                }
                if (_pico) return true;
                _pico = true;
                _picoOpenCount++;
                return true;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Close();
            _disposed = true;
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FakeLiveHardwareBackend));
        }
    }
}
