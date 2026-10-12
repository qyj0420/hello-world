using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 真 Art_DAQ + PicoScope 5242D SDK 后端（仅 ps5000a.dll，不启动 PicoScope.exe）。
    /// P0.4 DI 使用独立 _diGate；AO/AI/Pico 使用 _sync；加压路径读监视缓存（过期=阻断）。
    /// Clear 失败保留句柄 + cleanupFailed；CloseUnit 失败保留 _pico；跨进程 DeviceSdkLease。
    /// </summary>
    public sealed class ArtPicoLiveBackend : ILiveHardwareBackend
    {
        const double IoTimeout = 0.020; // AI/AO/DO 请求超时 20 ms（不变）
        public const int EStopCacheMaxAgeMs = 300;

        readonly LiveHardwareOptions _hw;
        /// <summary>AO/AI/DO/Pico 输出与块采集临界区。</summary>
        readonly object _sync = new object();
        /// <summary>P0.4 DI 专用短锁：禁止在持有 _sync 时做 300 ms DI 读。</summary>
        readonly object _diGate = new object();
        /// <summary>P0.4 串行占用：阻塞期间不再叠新读（节拍≠硬实时）。</summary>
        int _diReadBusy;

        IntPtr _doTask = IntPtr.Zero;
        IntPtr _diTask = IntPtr.Zero;
        IntPtr _aoTask = IntPtr.Zero;
        IntPtr _aiTask = IntPtr.Zero;
        IntPtr _finiteTask = IntPtr.Zero;
        bool _finiteReady;
        short _pico;
        bool _open;
        bool _disposed;
        bool _cleanupFailed;
        bool _requireFreshWindowAfterAo;
        DeviceSdkLease _lease;
        string _device = "Dev1";
        string _openedArtDeviceName = string.Empty;
        string _openedPicoSerial = string.Empty;

        /// <summary>本次 Open 的 ART 设备名（双身份）。</summary>
        public string OpenedArtDeviceName { get { lock (_sync) return _openedArtDeviceName ?? string.Empty; } }
        /// <summary>本次 Open 传入/命中的 Pico 序列号（空=第一台）。</summary>
        public string OpenedPicoSerial { get { lock (_sync) return _openedPicoSerial ?? string.Empty; } }
        double _lastAo;
        bool _hvOn, _hvInd, _lamp, _gnd;
        readonly ManualResetEvent _blockReady = new ManualResetEvent(false);
        uint _callbackStatus = uint.MaxValue;
        Pico5000aNative.BlockReadyCallback _callback; // 保持引用防 GC
        short[] _buffer;
        GCHandle _pin;
        bool _armed;
        double _armedDurationMs;
        int _armedRange;
        float _armedIntervalNs;
        short _armedMaxAdc;

        // Rapid Block Bulk Overlapped（校准采集；与 EXT 单段 Block 分离）
        bool _rapidArmed;
        int _rapidCaptureCount;
        int _rapidPre;
        int _rapidPost;
        int _rapidSamplesPerCapture;
        short[][] _rapidBuffers;
        GCHandle[] _rapidPins;
        short[] _rapidOverflow;

        // DI 监视缓存：由 ReadEStopSample 更新；WriteAo/WriteDo 只读缓存，过期=阻断
        readonly object _cacheGate = new object();
        readonly Stopwatch _cacheClock = Stopwatch.StartNew();
        LiveEStopSample _cachedEstop;
        long _cacheAtMs = -1;

        public ArtPicoLiveBackend(LiveHardwareOptions options) { _hw = options ?? new LiveHardwareOptions(); }

        public bool IsOpen { get { return _open && !_disposed; } }
        public bool CleanupFailed { get { return _cleanupFailed; } }
        public double LastAoVolts { get { return _lastAo; } }
        public bool HvOn { get { return _hvOn; } }
        public bool HvIndicatorOn { get { return _hvInd; } }
        public bool ConnectionLampHigh { get { return _lamp; } }
        public bool PicoOpen { get { return _pico != 0; } }

        public string ProbeDrivers()
        {
            string artErr, picoErr;
            bool art = NativeDriverLoader.TryPreload("Art_DAQ.dll", _hw.ArtDriverDirectory, out artErr);
            bool pico = NativeDriverLoader.TryPreload("ps5000a.dll", _hw.PicoSdkDirectory, out picoErr);
            if (art && pico) return string.Empty;
            return ((art ? "" : artErr + " ") + (pico ? "" : picoErr)).Trim();
        }

        public void Open(string artDeviceName, string picoSerial)
        {
            ThrowIfDisposed();
            if (_cleanupFailed)
                throw new InvalidOperationException("上次清理失败（cleanupFailed），禁止同进程再次 Open；请退出进程并核查硬件关断状态。");

            DeviceSdkLease lease = DeviceSdkLease.Acquire();
            lock (_sync)
            {
                if (_open)
                {
                    lease.Dispose();
                    throw new InvalidOperationException("ArtPico 后端已 Open。");
                }
                string err = ProbeDrivers();
                if (!string.IsNullOrEmpty(err))
                {
                    lease.Dispose();
                    throw new InvalidOperationException(err);
                }

                _device = string.IsNullOrWhiteSpace(artDeviceName) ? _hw.ArtDeviceName : artDeviceName;
                string serial = string.IsNullOrWhiteSpace(picoSerial) ? _hw.PicoSerial : picoSerial;
                try
                {
                    // 先 Pico 身份成功，再创建 ART 输出任务（对照 Factory OpenSession 顺序）
                    if (_hw.SuppressPicoStartupWindow)
                        Pico5000aNative.SuppressStartupWindow();

                    short handle;
                    uint status = Pico5000aNative.OpenUnit(out handle, string.IsNullOrEmpty(serial) ? null : serial,
                        Pico5000aNative.DriverMode(_hw.PicoResolutionBits));
                    if (handle < 0) handle = 0;
                    _pico = handle;
                    if (status == Pico5000aNative.PowerSupplyConnected || status == Pico5000aNative.PowerSupplyNotConnected
                        || status == Pico5000aNative.Usb3DeviceOnUsb2Port)
                    {
                        Pico5000aNative.Check(Pico5000aNative.ChangePowerSource(handle, status), "确认供电");
                        status = Pico5000aNative.Ok;
                    }
                    Pico5000aNative.Check(status, "OpenUnit");
                    if (_pico == 0) throw new InvalidOperationException("PicoScope OpenUnit 未返回有效句柄。");
                    PicoCaptureCooldown.MarkOpened();

                    ArtDaqNative.Check(ArtDaqNative.CreateTask(string.Empty, out _doTask), "创建 DO 任务");
                    string lines = string.Join(",", new[]
                    {
                        Physical(_hw.HvOnDoLine), Physical(_hw.HvIndicatorDoLine),
                        Physical(_hw.ConnectionIndicatorDoLine), Physical(_hw.GroundTriggerDoLine)
                    });
                    ArtDaqNative.Check(ArtDaqNative.CreateDOChannel(_doTask, lines, string.Empty, ArtDaqNative.ChannelPerLine), "创建 DO 通道");
                    ArtDaqNative.Check(ArtDaqNative.StartTask(_doTask), "启动 DO");

                    lock (_diGate)
                    {
                        ArtDaqNative.Check(ArtDaqNative.CreateTask(string.Empty, out _diTask), "创建 DI 任务");
                        ArtDaqNative.Check(ArtDaqNative.CreateDIChannel(_diTask, Physical(_hw.SafetyDiLine), string.Empty, ArtDaqNative.ChannelPerLine), "创建 P0.4");
                        ArtDaqNative.Check(ArtDaqNative.StartTask(_diTask), "启动 DI");
                    }

                    ArtDaqNative.Check(ArtDaqNative.CreateTask(string.Empty, out _aoTask), "创建 AO0 任务");
                    ArtDaqNative.Check(ArtDaqNative.CreateAOVoltageChan(_aoTask, Physical(_hw.ControlAoChannel), string.Empty,
                        0.0, _hw.AoMaximumVolts, ArtDaqNative.Volts, null), "创建 AO0");
                    ArtDaqNative.Check(ArtDaqNative.StartTask(_aoTask), "启动 AO0");

                    ArtDaqNative.Check(ArtDaqNative.CreateTask(string.Empty, out _aiTask), "创建 AI 监测任务");
                    CreateAi(_aiTask, _hw.CurrentAiChannel);
                    CreateAi(_aiTask, _hw.SourceVoltageAiChannel);
                    CreateAi(_aiTask, _hw.DividerAiChannel);
                    ConfigureContinuousMonitorClock(_aiTask);
                    ArtDaqNative.Check(ArtDaqNative.StartTask(_aiTask), "启动 AI");
                    _requireFreshWindowAfterAo = false;

                    WriteAoCore(0);
                    WriteDoCore(false, false, false, false);
                    // 首采 DI 填充缓存，供加压路径年龄门闩
                    try { ReadEStopSample(); } catch { /* 缓存仍未知 → 非零输出被阻断 */ }
                    _openedArtDeviceName = _device ?? string.Empty;
                    _openedPicoSerial = serial == null ? string.Empty : serial.Trim();
                    _lease = lease;
                    lease = null;
                    _open = true;
                }
                catch (DllNotFoundException ex) { RollbackBestEffort(); throw new InvalidOperationException("驱动缺失：" + ex.Message); }
                catch (EntryPointNotFoundException ex) { RollbackBestEffort(); throw new InvalidOperationException("驱动入口缺失：" + ex.Message); }
                catch (BadImageFormatException ex) { RollbackBestEffort(); throw new InvalidOperationException("驱动位数不匹配：" + ex.Message); }
                catch
                {
                    RollbackBestEffort();
                    throw;
                }
                finally
                {
                    if (lease != null)
                    {
                        try { lease.Dispose(); } catch { }
                    }
                }
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                try { if (_open) { WriteAoCore(0); WriteDoCore(false, false, false, false); } } catch { /* best effort */ }
                bool failed = RollbackBestEffort();
                _open = false;
                _openedArtDeviceName = string.Empty;
                _openedPicoSerial = string.Empty;
                DeviceSdkLease held = _lease;
                _lease = null;
                DeviceSdkLease.ReleaseAfterCleanup(held, failed || _cleanupFailed, msg =>
                {
                    System.Console.WriteLine("[DeviceSdkLease] " + msg);
                });
            }
        }

        public void WriteAoVolts(double volts)
        {
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("后端未 Open。");
                if (volts > 1e-9)
                    AssertEStopCacheAllowsEnergize("写非零 AO");
                WriteAoCore(volts);
            }
        }

        public LiveFeedbackSample ReadFeedback()
        {
            lock (_sync)
            {
                if (_aiTask == IntPtr.Zero) throw new InvalidOperationException("AI 未启动。");
                if (_finiteTask != IntPtr.Zero)
                    throw new InvalidOperationException("有限 AI4 采集占用 AI 子系统；此阶段不能读监测反馈。");
                bool fresh = _requireFreshWindowAfterAo;
                var clock = Stopwatch.StartNew();
                try
                {
                    var sample = ArtFreshFeedbackWindow.Read(
                        new MonitorFeedbackBuffer(this),
                        ArtFreshFeedbackWindow.DefaultWindowSamples,
                        () => clock.ElapsedMilliseconds,
                        fresh);
                    // 仅成功读消费新鲜窗请求；失败保留请求并让会话关断
                    _requireFreshWindowAfterAo = false;
                    return sample;
                }
                catch
                {
                    throw;
                }
            }
        }

        public void WriteDigitalOutputs(bool hvOn, bool hvIndicator, bool connectionLamp, bool groundTrigger)
        {
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("后端未 Open。");
                if (hvOn)
                    AssertEStopCacheAllowsEnergize("接通高压继电器");
                WriteDoCore(hvOn, hvIndicator, connectionLamp, groundTrigger);
            }
        }

        /// <summary>仅硬读确认按下才为 true；未知/串行忙不算按下（避免空 EmergencyStop）。</summary>
        public bool ReadEStopPressed()
        {
            LiveEStopSample s = ReadEStopSample();
            return s.ReadOk && s.Pressed;
        }

        public LiveEStopSample ReadEStopSample()
        {
            // 串行忙：短等重试；仍忙则用未过期缓存；勿直接标未知拒压。
            const int maxWaitMs = 400;
            const int sliceMs = 20;
            int waited = 0;
            while (Interlocked.CompareExchange(ref _diReadBusy, 1, 0) != 0)
            {
                if (waited >= maxWaitMs)
                {
                    LiveEStopSample cached;
                    long age;
                    if (TryGetCachedEStopSample(out cached, out age)
                        && age <= EStopCacheMaxAgeMs
                        && cached != null
                        && cached.ReadOk)
                    {
                        var copy = CloneSample(cached);
                        copy.Error = string.IsNullOrEmpty(copy.Error)
                            ? ("串行忙，用未过期缓存 age=" + age + "ms")
                            : (copy.Error + "；串行忙，用未过期缓存 age=" + age + "ms");
                        return copy;
                    }
                    return new LiveEStopSample
                    {
                        ReadOk = false,
                        Pressed = false,
                        SafetyReadyActiveHigh = _hw.SafetyReadyActiveHigh,
                        Error = "P0.4 串行忙（等待" + waited + "ms仍忙且无可用缓存）"
                    };
                }
                Thread.Sleep(sliceMs);
                waited += sliceMs;
            }
            try
            {
                // 仅 _diGate：禁止在持有 _sync 时做 300 ms DI 读
                lock (_diGate)
                {
                    LiveEStopSample sample = ReadEStopSampleDiUnlocked();
                    PublishEStopCache(sample);
                    return sample;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _diReadBusy, 0);
            }
        }

        /// <summary>加压路径用：读监视缓存；过期/未知/按下均阻断。不进 SDK。</summary>
        public bool TryGetCachedEStopSample(out LiveEStopSample sample, out long ageMs)
        {
            lock (_cacheGate)
            {
                if (_cachedEstop == null || _cacheAtMs < 0)
                {
                    sample = new LiveEStopSample
                    {
                        ReadOk = false,
                        Pressed = false,
                        SafetyReadyActiveHigh = _hw.SafetyReadyActiveHigh,
                        Error = "P0.4 监视缓存尚无样本"
                    };
                    ageMs = long.MaxValue;
                    return false;
                }
                ageMs = _cacheClock.ElapsedMilliseconds - _cacheAtMs;
                sample = CloneSample(_cachedEstop);
                return true;
            }
        }

        /// <summary>缓存年龄超过 MaxAge 或阻断态 → true（禁止非零输出）。</summary>
        public bool CachedEStopBlocksEnergize(out string reason)
        {
            LiveEStopSample sample;
            long age;
            if (!TryGetCachedEStopSample(out sample, out age) || age > EStopCacheMaxAgeMs)
            {
                reason = "P0.4 监视缓存过期或缺失（年龄=" + (age == long.MaxValue ? "未知" : age.ToString())
                    + " ms，上限 " + EStopCacheMaxAgeMs + " ms）；不以历史高放行。";
                return true;
            }
            if (sample.BlocksEnergize)
            {
                reason = sample.FormatLog();
                return true;
            }
            reason = null;
            return false;
        }

        void AssertEStopCacheAllowsEnergize(string action)
        {
            string reason;
            if (CachedEStopBlocksEnergize(out reason))
                throw new InvalidOperationException("SAFETY: 急停/未知时禁止" + action + "。" + reason);
        }

        void PublishEStopCache(LiveEStopSample sample)
        {
            if (sample == null) return;
            lock (_cacheGate)
            {
                _cachedEstop = CloneSample(sample);
                _cacheAtMs = _cacheClock.ElapsedMilliseconds;
            }
        }

        static LiveEStopSample CloneSample(LiveEStopSample s)
        {
            return new LiveEStopSample
            {
                RawDi = s.RawDi,
                PhysicalHigh = s.PhysicalHigh,
                Pressed = s.Pressed,
                SafetyReadyActiveHigh = s.SafetyReadyActiveHigh,
                ReadOk = s.ReadOk,
                Error = s.Error
            };
        }

        LiveEStopSample ReadEStopSampleDiUnlocked()
        {
            var sample = new LiveEStopSample { SafetyReadyActiveHigh = _hw.SafetyReadyActiveHigh };
            if (_diTask == IntPtr.Zero)
            {
                sample.ReadOk = false;
                sample.Pressed = false;
                sample.Error = "P0.4 未启动";
                return sample;
            }
            double diTimeoutSec = ResolveEStopTimeoutSeconds();
            try
            {
                var value = new byte[1];
                int read, bps;
                ArtDaqNative.Check(ArtDaqNative.ReadDigitalLines(_diTask, 1, diTimeoutSec, ArtDaqNative.GroupByChannel,
                    value, 1, out read, out bps, IntPtr.Zero), "读 P0.4");
                sample.RawDi = value[0];
                sample.PhysicalHigh = value[0] != 0;
                bool safetyReady = _hw.SafetyReadyActiveHigh ? sample.PhysicalHigh : !sample.PhysicalHigh;
                sample.Pressed = !safetyReady;
                sample.ReadOk = true;
                return sample;
            }
            catch (Exception ex)
            {
                sample.ReadOk = false;
                sample.Error = "P0.4 超时/读失败(上限" + ((int)(diTimeoutSec * 1000)) + "ms)：" + ex.Message;
                sample.Pressed = false;
                return sample;
            }
        }

        double ResolveEStopTimeoutSeconds()
        {
            int ms = _hw.EStopReadTimeoutMilliseconds;
            if (ms < 50) ms = 300;
            if (ms > 2000) ms = 2000;
            return ms / 1000.0;
        }

        /// <summary>空闲 DI 一次重建（owner 串行、无输出）；加压中勿调。</summary>
        public void RebuildIdleDiTask(IdleDiRebuildPolicy policy)
        {
            if (policy == null) throw new ArgumentNullException("policy");
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("后端未 Open。");
                if (_hvOn || Math.Abs(_lastAo) > 1e-9 || _armed || _finiteReady)
                    throw new InvalidOperationException("存在输出/采集时禁止 DI 重建。");
                policy.Rebuild(
                    verifiedIdleNoOutput: true,
                    stopOldReader: () => { },
                    probeExistingIdentity: () =>
                    {
                        if (_diTask == IntPtr.Zero)
                            throw new InvalidOperationException("P0.4 任务句柄为空，无法重建。");
                    },
                    clearOldTask: () =>
                    {
                        lock (_diGate)
                            Clear(ref _diTask, "空闲重建清除旧 P0.4");
                    },
                    createAndValidateReplacement: () =>
                    {
                        lock (_diGate)
                        {
                            ArtDaqNative.Check(ArtDaqNative.CreateTask(string.Empty, out _diTask), "重建 DI 任务");
                            ArtDaqNative.Check(ArtDaqNative.CreateDIChannel(_diTask, Physical(_hw.SafetyDiLine), string.Empty, ArtDaqNative.ChannelPerLine), "重建 P0.4");
                            ArtDaqNative.Check(ArtDaqNative.StartTask(_diTask), "启动重建 DI");
                        }
                    });
            }
            try { ReadEStopSample(); } catch { }
        }

        public void ArmFiniteDividerCapture()
        {
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("后端未 Open。");
                if (_finiteTask != IntPtr.Zero) throw new InvalidOperationException("Finite AI4 已布防。");
                Clear(ref _aiTask, "切 finite 前清监测 AI");
                try
                {
                    ArtDaqNative.Check(ArtDaqNative.CreateTask(string.Empty, out _finiteTask), "创建 finite AI4");
                    ArtDaqNative.Check(ArtDaqNative.CreateAIVoltageChan(_finiteTask, Physical(_hw.DividerAiChannel), string.Empty,
                        ArtDaqNative.DefaultTerminalConfiguration, _hw.ArtMonitorInputMinimumV, _hw.ArtMonitorInputMaximumV,
                        ArtDaqNative.Volts, null), "finite AI4 通道");
                    ArtDaqNative.Check(ArtDaqNative.ConfigureSampleClock(_finiteTask, string.Empty, _hw.ArtFiniteSampleRateHz,
                        ArtDaqNative.Rising, ArtDaqNative.FiniteSamples, _hw.ArtFiniteSampleCount), "80 kS/s finite 时钟");
                    string trig = string.IsNullOrWhiteSpace(_hw.ArtFiniteTriggerTerminal)
                        ? "/" + _device + "/PFI0" : _hw.ArtFiniteTriggerTerminal.Replace("Dev1", _device);
                    // 触发位置%：与 Pico SplitTriggerSamples 同源；pre>0 用参考触发（pretrigger），否则启动触发
                    int artPre, artPost;
                    LiveHardwareOptions.SplitTriggerSamples(_hw.ArtFiniteSampleCount, _hw.PicoTriggerPositionPercent, out artPre, out artPost);
                    if (artPre > 0)
                        ArtDaqNative.Check(ArtDaqNative.ConfigureDigitalReferenceTrigger(_finiteTask, trig, ArtDaqNative.Rising, (uint)artPre),
                            "PFI 参考触发 pre=" + artPre + "（触发位置 " + LiveHardwareOptions.ClampTriggerPositionPercent(_hw.PicoTriggerPositionPercent) + "%）");
                    else
                        ArtDaqNative.Check(ArtDaqNative.ConfigureDigitalStartTrigger(_finiteTask, trig, ArtDaqNative.Rising), "PFI 启动触发");
                    ArtDaqNative.Check(ArtDaqNative.TaskControl(_finiteTask, 2), "校验 finite 任务");
                    ArtDaqNative.Check(ArtDaqNative.StartTask(_finiteTask), "布防 finite AI4");
                    _finiteReady = true;
                }
                catch
                {
                    _finiteReady = false;
                    try { Clear(ref _finiteTask, "清理未完成 finite AI4"); } catch { }
                    try { RestartMonitoringUnlocked(); } catch { }
                    throw;
                }
            }
        }

        public double[] ReadFiniteDividerCapture(int timeoutMilliseconds)
        {
            lock (_sync)
            {
                if (!_finiteReady || _finiteTask == IntPtr.Zero)
                    throw new InvalidOperationException("Finite AI4 未布防。");
                var data = new double[_hw.ArtFiniteSampleCount];
                try
                {
                    int read;
                    int code = ArtDaqNative.ReadAnalogF64(_finiteTask, _hw.ArtFiniteSampleCount,
                        Math.Max(0.001, timeoutMilliseconds / 1000.0), ArtDaqNative.GroupByChannel,
                        data, (uint)data.Length, out read, IntPtr.Zero);
                    if (code == -200284 || code == -26802)
                        throw new TimeoutException("AI4 有限采集触发等待超时（诊断码 " + code + "）。");
                    ArtDaqNative.Check(code, "读 finite AI4");
                    if (read != data.Length) Array.Resize(ref data, Math.Max(0, read));
                    return data;
                }
                finally
                {
                    _finiteReady = false;
                    Clear(ref _finiteTask, "清完成 finite AI4");
                    try { RestartMonitoringUnlocked(); } catch { }
                }
            }
        }

        public void AbortFiniteDividerCapture()
        {
            lock (_sync)
            {
                _finiteReady = false;
                try { Clear(ref _finiteTask, "中止 finite AI4"); } catch { }
                try { if (_open) RestartMonitoringUnlocked(); } catch { }
            }
        }

        public void ConfigureBlockTriggerOnly(bool autoTriggerOnly)
        {
            lock (_sync)
            {
                if (!_open || _pico == 0)
                    throw new InvalidOperationException("Pico 未 Open（禁止在采集里再次 OpenUnit；须连接时启动）。");
                // 仅 SetSimpleTrigger：不等冷却、不 RunBlock、不动 AI4 监测
                short autoMs = _hw.PicoAutoTriggerMilliseconds > 0 ? _hw.PicoAutoTriggerMilliseconds : (short)800;
                if (autoTriggerOnly)
                {
                    Pico5000aNative.Check(Pico5000aNative.SetSimpleTrigger(_pico, 0, Pico5000aNative.ExternalTrigger, 0,
                        Pico5000aNative.RisingDirection, 0, autoMs), "ConfigureTrigger auto-only");
                }
                else
                {
                    short ext = (short)Math.Max(-32767, Math.Min(32767, Math.Round(_hw.PicoExternalTriggerThresholdV / 5.0 * 32767)));
                    Pico5000aNative.Check(Pico5000aNative.SetSimpleTrigger(_pico, 1, Pico5000aNative.ExternalTrigger, ext,
                        Pico5000aNative.RisingDirection, 0, autoMs), "ConfigureTrigger EXT digital");
                }
            }
        }

        public void ArmBlockCapture(int rangeIndex, Action onArmed, bool autoTriggerOnly, bool skipCooldownGate = false)
        {
            lock (_sync)
            {
                if (!_open || _pico == 0) throw new InvalidOperationException("Pico 未 Open（禁止在采集里再次 OpenUnit；须连接时启动）。");
                if (rangeIndex < 0 || rangeIndex >= LiveHardwareOptions.Pico5242DFullScaleVolts.Length)
                    throw new ArgumentOutOfRangeException("rangeIndex");

                if (!skipCooldownGate)
                    PicoCaptureCooldown.EnsureReady();
                // 非期望 BUSY → 停轮；期望中由 ReadBlockCapture 等待
                uint ping = Pico5000aNative.PingUnit(_pico);
                Pico5000aNative.CheckTransport(ping, "Arm 前 PingUnit", expectBusyOk: false);

                AbortCore(markReleased: true);
                int n = _hw.PicoSampleCount;
                int pre, post;
                LiveHardwareOptions.SplitTriggerSamples(n, _hw.PicoTriggerPositionPercent, out pre, out post);
                int mode = Pico5000aNative.DriverMode(_hw.PicoResolutionBits);
                Pico5000aNative.Check(Pico5000aNative.SetDeviceResolution(_pico, mode), "SetDeviceResolution");
                int readback;
                Pico5000aNative.Check(Pico5000aNative.GetDeviceResolution(_pico, out readback), "GetDeviceResolution");
                if (readback != mode) throw new InvalidOperationException("Pico 分辨率回读不匹配。");
                Pico5000aNative.Check(Pico5000aNative.SetChannel(_pico, Pico5000aNative.ChannelA, 1, Pico5000aNative.DcCoupling, rangeIndex, 0f), "SetChannel A");
                Pico5000aNative.Check(Pico5000aNative.SetChannel(_pico, Pico5000aNative.ChannelB, 0, Pico5000aNative.DcCoupling, rangeIndex, 0f), "SetChannel B off");
                int maxSamples;
                Pico5000aNative.Check(Pico5000aNative.MemorySegments(_pico, 1, out maxSamples), "MemorySegments");
                if (maxSamples < n) throw new InvalidOperationException("Pico 内存不足 " + n + " 点。");
                float intervalNs; int tbMax;
                Pico5000aNative.Check(Pico5000aNative.GetTimebase2(_pico, _hw.PicoTimebase, n, out intervalNs, out tbMax, 0), "GetTimebase2");
                if (tbMax < n || Math.Abs(intervalNs - _hw.PicoExpectedSampleIntervalNanoseconds) > 0.05)
                    throw new InvalidOperationException("Pico timebase 异常：" + intervalNs + " ns。");
                short maxAdc;
                Pico5000aNative.Check(Pico5000aNative.MaximumValue(_pico, out maxAdc), "MaximumValue");
                short autoMs = _hw.PicoAutoTriggerMilliseconds > 0 ? _hw.PicoAutoTriggerMilliseconds : (short)800;
                if (autoTriggerOnly)
                {
                    // 0V/噪声：禁用条件触发，仅 autoTrigger_ms 到时自触发（连接时已 Open，此处不 OpenUnit）
                    Pico5000aNative.Check(Pico5000aNative.SetSimpleTrigger(_pico, 0, Pico5000aNative.ExternalTrigger, 0,
                        Pico5000aNative.RisingDirection, 0, autoMs), "SetSimpleTrigger auto-only");
                }
                else
                {
                    // 非0：数字 EXT；auto 仅兜底
                    short ext = (short)Math.Max(-32767, Math.Min(32767, Math.Round(_hw.PicoExternalTriggerThresholdV / 5.0 * 32767)));
                    Pico5000aNative.Check(Pico5000aNative.SetSimpleTrigger(_pico, 1, Pico5000aNative.ExternalTrigger, ext,
                        Pico5000aNative.RisingDirection, 0, autoMs), "SetSimpleTrigger EXT digital");
                }

                _buffer = new short[n];
                _pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
                _blockReady.Reset();
                _callbackStatus = uint.MaxValue;
                _callback = OnBlockReady;
                _armedRange = rangeIndex;
                _armedIntervalNs = intervalNs;
                _armedMaxAdc = maxAdc;
                _armedDurationMs = PicoCaptureCooldown.EstimateDurationMs(n, intervalNs);
                try
                {
                    Pico5000aNative.Check(Pico5000aNative.SetDataBuffer(_pico, Pico5000aNative.ChannelA, _pin.AddrOfPinnedObject(), n, 0, Pico5000aNative.RatioModeNone), "SetDataBuffer");
                    int indisposed;
                    if (skipCooldownGate)
                        PicoCaptureCooldown.MarkCaptureStartedAllowUnready(_armedDurationMs);
                    else
                        PicoCaptureCooldown.MarkCaptureStarted(_armedDurationMs);
                    _armed = true;
                    // pre/post：触发位置 percent（0=窗起点，50=50%，100=近末端）
                    Pico5000aNative.Check(Pico5000aNative.RunBlock(_pico, pre, post, _hw.PicoTimebase, out indisposed, 0, _callback, IntPtr.Zero), "RunBlock");
                }
                catch
                {
                    AbortCore(markReleased: true);
                    throw;
                }
            }
            try { if (onArmed != null) onArmed(); }
            catch { AbortBlockCapture(); throw; }
        }

        public PicoBlockCaptureResult ReadBlockCapture(int timeoutMilliseconds, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            while (!_blockReady.WaitOne(20))
            {
                if (cancellation.IsCancellationRequested) { AbortBlockCapture(); throw new OperationCanceledException("块采集已取消。"); }
                // 等待期间可期望 BUSY；读缓存而非抢 _sync 做 DI
                string reason;
                if (CachedEStopBlocksEnergize(out reason))
                {
                    // 缓存过期/未知：硬读确认。仅真按下才中止；软故障继续等触发直到超时。
                    LiveEStopSample hard = null;
                    try { hard = ReadEStopSample(); } catch { hard = null; }
                    if (hard != null && hard.ReadOk && hard.Pressed)
                    {
                        AbortBlockCapture();
                        throw new OperationCanceledException("块采集中急停按下（P0.4 硬读确认），已 Abort。" + hard.FormatLog());
                    }
                    // 未按下 / 硬读失败 / 未知：不 Abort，继续等待触发
                }
                if (clock.ElapsedMilliseconds > Math.Max(1, timeoutMilliseconds))
                { AbortBlockCapture(); throw new TimeoutException("Pico 块采集超时。"); }
            }
            lock (_sync)
            {
                try
                {
                    if (_callbackStatus == Pico5000aNative.Busy)
                    {
                        // 完成回调仍报 BUSY：按期望忙处理说明，仍校验 Ok
                        System.Console.WriteLine("[Pico] " + Pico5000aNative.DescribeBusy(true));
                    }
                    Pico5000aNative.CheckTransport(_callbackStatus, "块采集完成回调", expectBusyOk: false);
                    uint samples = (uint)_buffer.Length;
                    short overflow;
                    Pico5000aNative.Check(Pico5000aNative.GetValues(_pico, 0, ref samples, 1, Pico5000aNative.RatioModeNone, 0, out overflow), "GetValues");
                    long tOff; int tUnits;
                    Pico5000aNative.Check(Pico5000aNative.GetTriggerTimeOffset64(_pico, out tOff, out tUnits, 0), "GetTriggerTimeOffset64");
                    Pico5000aNative.Check(Pico5000aNative.Stop(_pico), "Stop");
                    _armed = false;
                    PicoCaptureCooldown.MarkReleased(_armedDurationMs > 0 ? _armedDurationMs : 1.0);
                    short[] data = _buffer;
                    ReleaseBuffer();
                    if (samples != data.Length) throw new InvalidOperationException("Pico 返回点数不符。");
                    return new PicoBlockCaptureResult
                    {
                        RawAdc = data,
                        SampleIntervalNanoseconds = _armedIntervalNs,
                        SampleRateHz = 1e9 / _armedIntervalNs,
                        FullScaleVolts = LiveHardwareOptions.Pico5242DFullScaleVolts[_armedRange],
                        MaximumAdc = _armedMaxAdc,
                        RangeIndex = _armedRange,
                        HardwareOverflow = overflow != 0,
                        Serial = _hw.PicoSerial ?? string.Empty
                    };
                }
                catch { AbortCore(markReleased: true); throw; }
            }
        }


        public void AbortBlockCapture()
        {
            lock (_sync) AbortCore(markReleased: true);
        }

        public void ArmRapidBlockCapture(RapidBlockArmRequest request, Action onArmed)
        {
            if (request == null) throw new ArgumentNullException("request");
            lock (_sync)
            {
                if (!_open || _pico == 0) throw new InvalidOperationException("Pico 未 Open（禁止在采集里再次 OpenUnit）。");
                int rangeIndex = request.RangeIndex;
                if (rangeIndex < 0 || rangeIndex >= LiveHardwareOptions.Pico5242DFullScaleVolts.Length)
                    throw new ArgumentOutOfRangeException("rangeIndex");
                int nCaptures = PicoRapidBlockBulkOverlapped.ClampCaptureCount(request.CaptureCount);
                int pre = Math.Max(0, request.PreTriggerSamples);
                int post = Math.Max(1, request.PostTriggerSamples);
                int samples = pre + post;

                PicoCaptureCooldown.EnsureReady();
                uint ping = Pico5000aNative.PingUnit(_pico);
                Pico5000aNative.CheckTransport(ping, "Rapid Arm 前 PingUnit", expectBusyOk: false);

                AbortCore(markReleased: true);
                int mode = Pico5000aNative.DriverMode(_hw.PicoResolutionBits);
                Pico5000aNative.Check(Pico5000aNative.SetDeviceResolution(_pico, mode), "SetDeviceResolution");
                int readback;
                Pico5000aNative.Check(Pico5000aNative.GetDeviceResolution(_pico, out readback), "GetDeviceResolution");
                if (readback != mode) throw new InvalidOperationException("Pico 分辨率回读不匹配。");
                Pico5000aNative.Check(Pico5000aNative.SetChannel(_pico, Pico5000aNative.ChannelA, 1, Pico5000aNative.DcCoupling, rangeIndex, 0f), "SetChannel A");
                Pico5000aNative.Check(Pico5000aNative.SetChannel(_pico, Pico5000aNative.ChannelB, 0, Pico5000aNative.DcCoupling, rangeIndex, 0f), "SetChannel B off");

                int maxSamples;
                Pico5000aNative.Check(Pico5000aNative.MemorySegments(_pico, (uint)nCaptures, out maxSamples), "MemorySegments rapid");
                if (maxSamples < samples)
                    throw new InvalidOperationException("Pico Rapid 段内存不足：每段需 " + samples + " 点，可用 " + maxSamples + "。");
                Pico5000aNative.Check(Pico5000aNative.SetNoOfCaptures(_pico, (uint)nCaptures), "SetNoOfCaptures");

                float intervalNs; int tbMax;
                Pico5000aNative.Check(Pico5000aNative.GetTimebase2(_pico, _hw.PicoTimebase, samples, out intervalNs, out tbMax, 0), "GetTimebase2 rapid");
                if (tbMax < samples || Math.Abs(intervalNs - _hw.PicoExpectedSampleIntervalNanoseconds) > 0.05)
                    throw new InvalidOperationException("Pico Rapid timebase 异常：" + intervalNs + " ns。");

                short maxAdc;
                Pico5000aNative.Check(Pico5000aNative.MaximumValue(_pico, out maxAdc), "MaximumValue");
                double fs = LiveHardwareOptions.Pico5242DFullScaleVolts[rangeIndex];
                short thrAdc = PicoRapidBlockBulkOverlapped.VoltsToAdcThreshold(request.ChannelATriggerThresholdVolts, fs, maxAdc);
                short autoMs = request.AutoTriggerMilliseconds < 0 ? (short)0 : request.AutoTriggerMilliseconds;
                Pico5000aNative.Check(Pico5000aNative.SetSimpleTrigger(_pico, 1, Pico5000aNative.ChannelA, thrAdc,
                    Pico5000aNative.RisingDirection, 0, autoMs), "SetSimpleTrigger ChannelA");

                _rapidBuffers = new short[nCaptures][];
                _rapidPins = new GCHandle[nCaptures];
                _rapidOverflow = new short[nCaptures];
                for (int i = 0; i < nCaptures; i++)
                {
                    _rapidBuffers[i] = new short[samples];
                    _rapidPins[i] = GCHandle.Alloc(_rapidBuffers[i], GCHandleType.Pinned);
                    Pico5000aNative.Check(Pico5000aNative.SetDataBuffer(_pico, Pico5000aNative.ChannelA,
                        _rapidPins[i].AddrOfPinnedObject(), samples, (uint)i, Pico5000aNative.RatioModeNone),
                        "SetDataBuffer seg " + i);
                }

                uint nSamples = (uint)samples;
                Pico5000aNative.Check(Pico5000aNative.GetValuesOverlappedBulk(_pico, 0, ref nSamples, 1,
                    Pico5000aNative.RatioModeNone, 0, (uint)(nCaptures - 1), _rapidOverflow),
                    "GetValuesOverlappedBulk");

                _blockReady.Reset();
                _callbackStatus = uint.MaxValue;
                _callback = OnBlockReady;
                _armedRange = rangeIndex;
                _armedIntervalNs = intervalNs;
                _armedMaxAdc = maxAdc;
                _rapidCaptureCount = nCaptures;
                _rapidPre = pre;
                _rapidPost = post;
                _rapidSamplesPerCapture = samples;
                _armedDurationMs = PicoCaptureCooldown.EstimateDurationMs(samples * nCaptures, intervalNs);
                try
                {
                    int indisposed;
                    PicoCaptureCooldown.MarkCaptureStarted(_armedDurationMs);
                    _rapidArmed = true;
                    Pico5000aNative.Check(Pico5000aNative.RunBlock(_pico, pre, post, _hw.PicoTimebase, out indisposed, 0, _callback, IntPtr.Zero), "RunBlock rapid");
                }
                catch
                {
                    AbortCore(markReleased: true);
                    throw;
                }
            }
            try { if (onArmed != null) onArmed(); }
            catch { AbortRapidBlockCapture(); throw; }
        }

        public PicoRapidBlockCaptureResult ReadRapidBlockCaptures(int timeoutMilliseconds, CancellationToken cancellation)
        {
            // Rapid = 校准 ChannelA 路径：校准备急停按下（可 AO=0）；禁止因「已按下」Abort。
            // 加压 EXT 单段走 ReadBlockCapture，那里仍对急停按下中止。
            var clock = Stopwatch.StartNew();
            while (!_blockReady.WaitOne(20))
            {
                if (cancellation.IsCancellationRequested) { AbortRapidBlockCapture(); throw new OperationCanceledException("Rapid 采集已取消。"); }
                if (clock.ElapsedMilliseconds > Math.Max(1, timeoutMilliseconds))
                { AbortRapidBlockCapture(); throw new TimeoutException("Pico Rapid 块采集超时。"); }
            }
            lock (_sync)
            {
                try
                {
                    if (!_rapidArmed) throw new InvalidOperationException("Pico Rapid 未布防。");
                    Pico5000aNative.CheckTransport(_callbackStatus, "Rapid 完成回调", expectBusyOk: false);

                    uint got;
                    Pico5000aNative.Check(Pico5000aNative.GetNoOfCaptures(_pico, out got), "GetNoOfCaptures");
                    int n = _rapidCaptureCount;
                    if (got > 0 && got < (uint)n) n = (int)got;
                    if (n < 1) throw new InvalidOperationException("Rapid 未捕获到任何段。");

                    var times = new long[n];
                    var units = new int[n];
                    Pico5000aNative.Check(Pico5000aNative.GetValuesTriggerTimeOffsetBulk64(_pico, times, units, 0, (uint)(n - 1)),
                        "GetValuesTriggerTimeOffsetBulk64");

                    try { Pico5000aNative.Stop(_pico); } catch { }
                    _rapidArmed = false;
                    PicoCaptureCooldown.MarkReleased(_armedDurationMs > 0 ? _armedDurationMs : 1.0);

                    var captures = new short[n][];
                    var overflow = new short[n];
                    for (int i = 0; i < n; i++)
                    {
                        captures[i] = (short[])_rapidBuffers[i].Clone();
                        overflow[i] = _rapidOverflow != null && i < _rapidOverflow.Length ? _rapidOverflow[i] : (short)0;
                    }

                    var result = new PicoRapidBlockCaptureResult
                    {
                        Captures = captures,
                        TriggerTimeOffsets = times,
                        TriggerTimeUnits = units,
                        OverflowPerCapture = overflow,
                        SampleIntervalNanoseconds = _armedIntervalNs,
                        SampleRateHz = 1e9 / _armedIntervalNs,
                        FullScaleVolts = LiveHardwareOptions.Pico5242DFullScaleVolts[_armedRange],
                        MaximumAdc = _armedMaxAdc,
                        RangeIndex = _armedRange,
                        PreTriggerSamples = _rapidPre,
                        PostTriggerSamples = _rapidPost,
                        CaptureCount = n,
                        Serial = _hw.PicoSerial ?? string.Empty
                    };
                    ReleaseRapidBuffers();
                    // 恢复单段，供加压 ArmBlockCapture(EXT) 使用
                    try
                    {
                        int maxSamples;
                        Pico5000aNative.Check(Pico5000aNative.MemorySegments(_pico, 1, out maxSamples), "MemorySegments restore1");
                        Pico5000aNative.Check(Pico5000aNative.SetNoOfCaptures(_pico, 1), "SetNoOfCaptures restore1");
                    }
                    catch { }
                    return result;
                }
                catch { AbortCore(markReleased: true); throw; }
            }
        }

        public void AbortRapidBlockCapture()
        {
            lock (_sync) AbortCore(markReleased: true);
        }

        void OnBlockReady(short handle, uint status, IntPtr parameter)
        {
            _callbackStatus = status;
            _blockReady.Set();
        }

        void AbortCore(bool markReleased)
        {
            double dur = _armedDurationMs > 0 ? _armedDurationMs : 1.0;
            bool wasArmed = _armed || _rapidArmed;
            if ((_armed || _rapidArmed) && _pico != 0)
            {
                try { Pico5000aNative.Stop(_pico); } catch { }
            }
            _armed = false;
            ReleaseBuffer();
            ReleaseRapidBuffers();
            _rapidArmed = false;
            if (markReleased && wasArmed)
            {
                try { PicoCaptureCooldown.MarkReleased(dur); } catch { }
            }
        }

        void ReleaseBuffer()
        {
            if (_pin.IsAllocated) _pin.Free();
            _buffer = null;
        }

        void ReleaseRapidBuffers()
        {
            if (_rapidPins != null)
            {
                for (int i = 0; i < _rapidPins.Length; i++)
                {
                    if (_rapidPins[i].IsAllocated) _rapidPins[i].Free();
                }
            }
            _rapidPins = null;
            _rapidBuffers = null;
            _rapidOverflow = null;
            _rapidCaptureCount = 0;
            _rapidSamplesPerCapture = 0;
        }

        public void EnsureMonitorAiTask()
        {
            lock (_sync)
            {
                if (!_open) throw new InvalidOperationException("Art 未 Open，无法建立 AI 监测。");
                RestartMonitoringUnlocked();
            }
        }

        void RestartMonitoringUnlocked()
        {
            if (_aiTask != IntPtr.Zero) return;
            ArtDaqNative.Check(ArtDaqNative.CreateTask(string.Empty, out _aiTask), "恢复 AI 监测");
            CreateAi(_aiTask, _hw.CurrentAiChannel);
            CreateAi(_aiTask, _hw.SourceVoltageAiChannel);
            CreateAi(_aiTask, _hw.DividerAiChannel);
            ConfigureContinuousMonitorClock(_aiTask);
            ArtDaqNative.Check(ArtDaqNative.StartTask(_aiTask), "启动 AI 监测");
            _requireFreshWindowAfterAo = false;
        }

        void ConfigureContinuousMonitorClock(IntPtr task)
        {
            // 对照 FactoryArtFeedbackNative：1000 S/s/通道连续监测，缓冲 1000 点
            ArtDaqNative.Check(ArtDaqNative.ConfigureSampleClock(task, string.Empty,
                ArtFreshFeedbackWindow.RateHz, ArtDaqNative.Rising,
                ArtDaqNative.ContinuousSamples, 1000), "配置 ART 三通道 1000 S/s 连续监测");
        }

        uint GetMonitorAvailableUnlocked()
        {
            if (_aiTask == IntPtr.Zero) return 0;
            uint available;
            ArtDaqNative.Check(ArtDaqNative.GetReadAvailSampPerChan(_aiTask, out available), "读取 ART 连续反馈未读点数");
            return available;
        }

        double[] ReadMonitorSamplesUnlocked(int samplesPerChannel)
        {
            if (_aiTask == IntPtr.Zero) throw new InvalidOperationException("AI 未启动。");
            if (samplesPerChannel < 1) throw new ArgumentOutOfRangeException("samplesPerChannel");
            var raw = new double[samplesPerChannel * ArtFreshFeedbackWindow.Channels];
            int read;
            ArtDaqNative.Check(ArtDaqNative.ReadAnalogF64(_aiTask, samplesPerChannel, IoTimeout,
                ArtDaqNative.GroupByScan, raw, (uint)raw.Length, out read, IntPtr.Zero),
                "追读 ART 三通道反馈（" + samplesPerChannel + "点/通道）");
            if (read != samplesPerChannel)
                throw new InvalidOperationException("ART 追读点数不足：请求=" + samplesPerChannel + "，返回=" + read + "。");
            return raw;
        }

        sealed class MonitorFeedbackBuffer : IArtFeedbackBuffer
        {
            readonly ArtPicoLiveBackend _owner;
            public MonitorFeedbackBuffer(ArtPicoLiveBackend owner) { _owner = owner; }
            public uint Available() { return _owner.GetMonitorAvailableUnlocked(); }
            public double[] Read(int samplesPerChannel) { return _owner.ReadMonitorSamplesUnlocked(samplesPerChannel); }
        }

        void WriteAoCore(double volts)
        {
            if (_aoTask == IntPtr.Zero) throw new InvalidOperationException("AO0 未启动。");
            double v = Math.Max(0, Math.Min(volts, _hw.AoMaximumVolts));
            int written;
            ArtDaqNative.Check(ArtDaqNative.WriteAnalogF64(_aoTask, 1, 0, IoTimeout, ArtDaqNative.GroupByChannel,
                new[] { v }, out written, IntPtr.Zero), "写 AO0");
            if (written != 1) throw new InvalidOperationException("AO0 未写入。");
            _lastAo = v;
            // 正 AO 后下一拍 ReadFeedback 必须用新边界后的完整新鲜窗（对照 Factory WriteAoCore）
            _requireFreshWindowAfterAo = v > 1e-9;
        }

        void WriteDoCore(bool hvOn, bool hvInd, bool lamp, bool gnd)
        {
            if (_doTask == IntPtr.Zero) throw new InvalidOperationException("DO 未启动。");
            int written;
            ArtDaqNative.Check(ArtDaqNative.WriteDigitalLines(_doTask, 1, 0, IoTimeout, ArtDaqNative.GroupByChannel,
                new byte[] { hvOn ? (byte)1 : (byte)0, hvInd ? (byte)1 : (byte)0, lamp ? (byte)1 : (byte)0, gnd ? (byte)1 : (byte)0 },
                out written, IntPtr.Zero), "写 DO");
            _hvOn = hvOn; _hvInd = hvInd; _lamp = lamp; _gnd = gnd;
        }

        void CreateAi(IntPtr task, string ch)
        {
            ArtDaqNative.Check(ArtDaqNative.CreateAIVoltageChan(task, Physical(ch), string.Empty,
                ArtDaqNative.DefaultTerminalConfiguration, _hw.ArtMonitorInputMinimumV, _hw.ArtMonitorInputMaximumV,
                ArtDaqNative.Volts, null), "创建 " + ch);
        }

        string Physical(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("ART 端子映射为空。");
            if (relative.StartsWith("/", StringComparison.Ordinal)) return relative;
            if (relative.StartsWith(_device + "/", StringComparison.OrdinalIgnoreCase)) return relative;
            return _device + "/" + relative;
        }

        public void ClosePicoOnly()
        {
            lock (_sync)
            {
                AbortCore(markReleased: true);
                if (_pico != 0)
                    ClosePicoHandleOrMark("ClosePicoOnly");
            }
        }

        /// <summary>
        /// CloseUnit：成功或设备已掉线 → 清零 _pico；仅「句柄仍可能有效但 Close 失败」才 cleanupFailed。
        /// </summary>
        void ClosePicoHandleOrMark(string operation)
        {
            short handle = _pico;
            if (handle == 0) return;
            uint st;
            try { st = Pico5000aNative.CloseUnit(handle); }
            catch (Exception ex)
            {
                // 驱动层抛错多见于 USB 已拔：按掉线丢弃托管句柄，允许重连
                System.Console.WriteLine("[ArtPico] " + operation + " CloseUnit 异常（按掉线丢弃句柄）：" + ex.Message);
                _pico = 0;
                return;
            }
            if (st == Pico5000aNative.Ok || Pico5000aNative.IsDeviceGoneStatus(st))
            {
                if (st != Pico5000aNative.Ok)
                    System.Console.WriteLine("[ArtPico] " + operation + " CloseUnit 0x"
                        + st.ToString("X8") + "（设备已掉线/句柄失效）→ 丢弃托管句柄，不置 cleanupFailed");
                _pico = 0;
                return;
            }
            _cleanupFailed = true;
            throw new InvalidOperationException(
                "Pico CloseUnit 失败（0x" + st.ToString("X8") + "），保留句柄；禁止假装已释放。operation=" + operation);
        }

        public bool TryReopenPico(string picoSerial, out string error)
        {
            error = null;
            lock (_sync)
            {
                if (_cleanupFailed)
                {
                    error = "清理失败后禁止重开 Pico。";
                    return false;
                }
                if (!_open)
                {
                    error = "Art 会话未 Open，无法重开 Pico。";
                    return false;
                }
                if (_pico != 0) return true;
                try
                {
                    if (_hw.SuppressPicoStartupWindow)
                        Pico5000aNative.SuppressStartupWindow();

                    short handle;
                    uint status = Pico5000aNative.OpenUnit(out handle, string.IsNullOrEmpty(picoSerial) ? null : picoSerial,
                        Pico5000aNative.DriverMode(_hw.PicoResolutionBits));
                    if (handle < 0) handle = 0;
                    _pico = handle;
                    if (status == Pico5000aNative.PowerSupplyConnected || status == Pico5000aNative.PowerSupplyNotConnected
                        || status == Pico5000aNative.Usb3DeviceOnUsb2Port)
                    {
                        Pico5000aNative.Check(Pico5000aNative.ChangePowerSource(handle, status), "确认供电");
                        status = Pico5000aNative.Ok;
                    }
                    Pico5000aNative.Check(status, "OpenUnit(reopen)");
                    if (_pico == 0)
                    {
                        error = "PicoScope 重开 OpenUnit 未返回有效句柄。";
                        return false;
                    }
                    PicoCaptureCooldown.MarkOpened();
                    return true;
                }
                catch (Exception ex)
                {
                    // Open 失败：若句柄无效才清零；否则保留
                    if (_pico != 0)
                    {
                        // OpenUnit 可能返回了句柄但后续失败 — 仍持有
                    }
                    else
                        _pico = 0;
                    error = "Pico 重开失败：" + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>尽力回滚；Clear/Close 失败置 cleanupFailed 并保留句柄。返回是否有清理失败。</summary>
        bool RollbackBestEffort()
        {
            var failures = new List<Exception>();
            var soft = new List<Exception>();
            try { AbortCore(markReleased: true); } catch (Exception ex) { soft.Add(ex); }
            _finiteReady = false;
            TryClear(ref _finiteTask, "回滚 finite", failures);
            if (_pico != 0)
            {
                try
                {
                    ClosePicoHandleOrMark("Rollback");
                }
                catch (Exception ex)
                {
                    // ClosePicoHandleOrMark 仅在非掉线失败时抛并已置 cleanupFailed
                    failures.Add(ex);
                }
            }
            TryClear(ref _aoTask, "回滚 AO", failures);
            TryClear(ref _aiTask, "回滚 AI", failures);
            TryClear(ref _doTask, "回滚 DO", failures);
            lock (_diGate)
                TryClear(ref _diTask, "回滚 DI", failures);
            _lastAo = 0; _hvOn = _hvInd = _lamp = _gnd = false;
            if (soft.Count > 0)
            {
                try
                {
                    System.Console.WriteLine("[ArtPico] Rollback 软异常（不单独置 cleanupFailed）："
                        + soft[0].Message);
                }
                catch { }
            }
            // 仅 ClearTask/CloseUnit 路径会置 _cleanupFailed；此处不再因 soft 列表误置
            if (failures.Count > 0 && !_cleanupFailed)
            {
                // TryClear 已在 Clear 失败时置位；若有未标失败的条目，保守置位
                bool anyHard = false;
                foreach (var ex in failures)
                {
                    string m = ex != null ? (ex.Message ?? "") : "";
                    if (m.IndexOf("ClearTask", StringComparison.Ordinal) >= 0
                        || m.IndexOf("CloseUnit", StringComparison.Ordinal) >= 0
                        || m.IndexOf("句柄已保留", StringComparison.Ordinal) >= 0)
                    {
                        anyHard = true;
                        break;
                    }
                }
                if (anyHard)
                    _cleanupFailed = true;
                else
                {
                    try
                    {
                        System.Console.WriteLine("[ArtPico] Rollback 非硬清理异常，不置 cleanupFailed："
                            + failures[0].Message);
                    }
                    catch { }
                }
            }
            return _cleanupFailed;
        }

        void TryClear(ref IntPtr task, string operation, List<Exception> failures)
        {
            try { Clear(ref task, operation); }
            catch (Exception ex) { failures.Add(ex); }
        }

        /// <summary>
        /// 诚实 Clear：ClearTask 失败且设备仍可能持有句柄 → cleanupFailed；
        /// 设备已掉线（-200477 等）→ 丢弃托管句柄，不禁同进程重连。
        /// </summary>
        void Clear(ref IntPtr task, string operation)
        {
            if (task == IntPtr.Zero) return;
            IntPtr handle = task;
            Exception stopFailure = null;
            int stopCode = 0;
            try
            {
                stopCode = ArtDaqNative.StopTask(handle);
                if (stopCode < 0 && !ArtDaqNative.IsDeviceGoneStatus(stopCode))
                    stopFailure = new InvalidOperationException(operation + ": StopTask 失败（" + stopCode + "）。");
            }
            catch (Exception ex)
            {
                stopFailure = new InvalidOperationException(operation + ": StopTask 调用失败。", ex);
            }

            Exception clearFailure = null;
            int clearCode = 0;
            bool clearDeviceGone = false;
            try
            {
                clearCode = ArtDaqNative.ClearTask(handle);
                if (clearCode < 0)
                {
                    if (ArtDaqNative.IsDeviceGoneStatus(clearCode)
                        || ArtDaqNative.IsDeviceGoneStatus(stopCode))
                    {
                        clearDeviceGone = true;
                        task = IntPtr.Zero;
                        System.Console.WriteLine("[ArtPico] " + operation + " ClearTask=" + clearCode
                            + "（设备已掉线）→ 丢弃托管句柄，不置 cleanupFailed");
                    }
                    else
                        clearFailure = new InvalidOperationException(operation + ": ClearTask 失败（" + clearCode + "）。");
                }
                else
                    task = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                if (ArtDaqNative.IsDeviceGoneMessage(ex.Message)
                    || ArtDaqNative.IsDeviceGoneStatus(stopCode))
                {
                    clearDeviceGone = true;
                    task = IntPtr.Zero;
                    System.Console.WriteLine("[ArtPico] " + operation + " ClearTask 异常（按掉线丢弃句柄）：" + ex.Message);
                }
                else
                    clearFailure = new InvalidOperationException(operation + ": ClearTask 调用失败。", ex);
            }

            if (clearDeviceGone)
                return;

            if (clearFailure != null)
            {
                _cleanupFailed = true;
                if (stopFailure != null)
                    throw new AggregateException(operation + " 失败；ClearTask 未成功，句柄已保留。", stopFailure, clearFailure);
                throw new InvalidOperationException(operation + " 失败；ClearTask 未成功，句柄已保留。", clearFailure);
            }
            // Clear 已成功：StopTask 失败只是警告，不得置 cleanupFailed / 不得禁重开
            if (stopFailure != null)
                System.Console.WriteLine("[ArtPico] " + operation + " StopTask 警告（句柄已 Clear）：" + stopFailure.Message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { Close(); } catch { }
            _disposed = true;
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ArtPicoLiveBackend));
        }
    }
}
