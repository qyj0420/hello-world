using System;
using System.Diagnostics;
using System.Threading;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// P0.4 监视事件。极性：默认有效低按下（SafetyReadyActiveHigh=true → 高=解除）。
    /// </summary>
    public enum LiveEStopMonitorKind
    {
        Pressed = 0,
        Unknown = 1,
        Released = 2
    }

    /// <summary>
    /// P0.4 急停独立串行只读监视（对照 FactoryEmergencyMonitor）：
    /// 与加压升压、开关(DO)解耦；请求节拍约 10 ms（非硬实时），单次读超时上限约 300 ms。
    /// DI 走后端 _diGate（不与 AO/AI/Pico 抢 _sync）；样本带时间戳，年龄&gt;300 ms 视为未知阻断。
    /// 空闲：一次超时重读 + 二次超时标 IdleTimeoutRebuildRequired（owner 调 IdleDiRebuildPolicy）；
    /// 加压中 EnterActiveMode 禁用空闲重试/重建。
    /// </summary>
    public sealed class LiveEmergencyMonitor : IDisposable
    {
        public const int DefaultPollIntervalMs = 10;
        public const int MaximumAgeMs = 300;

        readonly ILiveHardwareBackend _backend;
        readonly Action<LiveEStopMonitorKind, LiveEStopSample> _onEvent;
        readonly Action _onIdleRebuildRequired;
        readonly int _pollMs;
        readonly object _cacheGate = new object();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        Thread _thread;
        volatile bool _run;
        bool _disposed;
        bool _lastPressedOrUnknown;
        bool _activeMode; // 加压中：禁空闲重试
        bool _idleRetryEnabled;
        bool _retryPending;
        bool _retryExhausted;
        long _retryBeganAtMs = -1;
        string _retryDetail = string.Empty;
        LiveEStopSample _lastSample;
        long _lastSampleAtMs = -1;

        public LiveEmergencyMonitor(ILiveHardwareBackend backend, Action<LiveEStopMonitorKind, LiveEStopSample> onEvent, int pollIntervalMs = DefaultPollIntervalMs)
            : this(backend, onEvent, null, pollIntervalMs)
        {
        }

        public LiveEmergencyMonitor(
            ILiveHardwareBackend backend,
            Action<LiveEStopMonitorKind, LiveEStopSample> onEvent,
            Action onIdleRebuildRequired,
            int pollIntervalMs = DefaultPollIntervalMs)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            if (onEvent == null) throw new ArgumentNullException("onEvent");
            _backend = backend;
            _onEvent = onEvent;
            _onIdleRebuildRequired = onIdleRebuildRequired;
            _pollMs = pollIntervalMs < 1 ? DefaultPollIntervalMs : pollIntervalMs;
        }

        public LiveEmergencyMonitor(ILiveHardwareBackend backend, Action<LiveEStopMonitorKind> onEvent, int pollIntervalMs = DefaultPollIntervalMs)
            : this(backend, (kind, sample) => { if (onEvent != null) onEvent(kind); }, pollIntervalMs)
        {
        }

        public bool IsRunning { get { return _run && _thread != null && _thread.IsAlive; } }
        public bool IsActiveMode { get { lock (_cacheGate) return _activeMode; } }
        /// <summary>空闲两次明确超时后为 true；仅允许 owner 串行 IdleDiRebuildPolicy 一次。</summary>
        public bool IdleTimeoutRebuildRequired { get { lock (_cacheGate) return _retryExhausted; } }
        public bool IdleRetryPending { get { lock (_cacheGate) return _retryPending; } }
        public string IdleRetryDetail { get { lock (_cacheGate) return _retryDetail ?? string.Empty; } }

        /// <summary>加压轮次开始：禁用空闲重试/重建路径。若空闲重读未决则拒绝进入加压。</summary>
        public void EnterActiveMode()
        {
            lock (_cacheGate)
            {
                if (_retryPending || _retryExhausted)
                    throw new InvalidOperationException("P0.4 空闲重读/重建未决，电平未知；禁止开始本轮加压。");
                _activeMode = true;
                _idleRetryEnabled = false;
            }
        }

        /// <summary>加压结束回空闲：允许后续一次超时重读策略。</summary>
        public void EnterIdleMode()
        {
            lock (_cacheGate)
            {
                _activeMode = false;
                if (!_retryExhausted)
                    _idleRetryEnabled = true;
            }
        }

        /// <summary>显式启用空闲超时重读（Open 后待机）。故障/停止态不可用。</summary>
        public void EnableIdleTimeoutRetry()
        {
            lock (_cacheGate)
            {
                if (_disposed)
                    throw new InvalidOperationException("P0.4 监视已释放，不能启用空闲重读。");
                if (_retryExhausted)
                    throw new InvalidOperationException("本会话空闲 DI 重建已用尽或待重建，不能再启空闲重读。");
                _idleRetryEnabled = true;
                _activeMode = false;
            }
        }

        /// <summary>
        /// FakeLive 软件急停切换后失效缓存，使 GetStatus 立刻走 ReadEStopSample，
        /// 避免顶栏/门禁在最多 300ms 内仍读到过期「按下」。
        /// </summary>
        public void InvalidateCache()
        {
            lock (_cacheGate)
            {
                _lastSample = null;
                _lastSampleAtMs = -1;
            }
        }

        /// <summary>读监视缓存；年龄超限返回 false（调用方应按阻断处理）。</summary>
        public bool TryGetCachedSample(out LiveEStopSample sample, out long ageMs)
        {
            lock (_cacheGate)
            {
                if (_lastSample == null || _lastSampleAtMs < 0)
                {
                    sample = null;
                    ageMs = long.MaxValue;
                    return false;
                }
                ageMs = _clock.ElapsedMilliseconds - _lastSampleAtMs;
                sample = _lastSample;
                return ageMs <= MaximumAgeMs;
            }
        }

        /// <summary>缓存过期/未知/按下 → 阻断放行。</summary>
        public bool CachedBlocksEnergize(out string reason)
        {
            lock (_cacheGate)
            {
                if (_retryPending || _retryExhausted)
                {
                    reason = "P0.4 空闲重读/重建未决（未知）；不以历史高放行。"
                        + (string.IsNullOrEmpty(_retryDetail) ? "" : " " + _retryDetail);
                    return true;
                }
            }
            LiveEStopSample sample;
            long age;
            if (!TryGetCachedSample(out sample, out age))
            {
                reason = "P0.4 监视缓存过期或缺失（年龄=" + (age == long.MaxValue ? "未知" : age.ToString())
                    + " ms）；不以历史高放行。";
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

        public void Start()
        {
            ThrowIfDisposed();
            if (_run) return;
            try
            {
                LiveEStopSample s = _backend.ReadEStopSample();
                Publish(s);
                _lastPressedOrUnknown = s.BlocksEnergize;
                System.Console.WriteLine("[P0.4监视] 启动基线 " + s.FormatLog());
            }
            catch (Exception ex)
            {
                _lastPressedOrUnknown = true;
                var unk = new LiveEStopSample
                {
                    ReadOk = false,
                    Pressed = false,
                    Error = ex.Message,
                    SafetyReadyActiveHigh = true
                };
                Publish(unk);
                System.Console.WriteLine("[P0.4监视] 启动基线读失败 → 按按下保守: " + ex.Message);
            }
            lock (_cacheGate)
            {
                if (!_activeMode && !_retryExhausted)
                    _idleRetryEnabled = true;
            }
            _run = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Owts-P04-EStopMonitor"
            };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            lock (_cacheGate) { _idleRetryEnabled = false; }
            Thread t = _thread;
            _thread = null;
            if (t != null && t.IsAlive)
            {
                try { t.Join(500); } catch { }
            }
        }

        void Loop()
        {
            while (_run)
            {
                try
                {
                    long begin = _clock.ElapsedMilliseconds;
                    LiveEStopSample sample;
                    try
                    {
                        sample = _backend.ReadEStopSample();
                    }
                    catch (Exception ex)
                    {
                        sample = new LiveEStopSample
                        {
                            ReadOk = false,
                            Pressed = false,
                            Error = ex.Message,
                            SafetyReadyActiveHigh = true
                        };
                    }

                    long ended = _clock.ElapsedMilliseconds;
                    if (!_run) break;

                    // 空闲路径：超时/读失败 → 一次重读，二次标重建（不在本线程清 DI）
                    if (!sample.ReadOk)
                    {
                        if (TryBeginIdleRetry(sample, begin, ended))
                        {
                            try { Thread.Sleep(_pollMs); } catch { }
                            continue;
                        }
                        if (TryExhaustIdleRetry(sample, begin, ended))
                        {
                            Publish(sample);
                            FireEdge(LiveEStopMonitorKind.Unknown, sample);
                            _lastPressedOrUnknown = true;
                            _run = false;
                            Action cb = _onIdleRebuildRequired;
                            if (cb != null)
                            {
                                try { ThreadPool.QueueUserWorkItem(_ => { try { cb(); } catch { } }); }
                                catch { }
                            }
                            break;
                        }
                    }
                    else
                    {
                        ClearIdleRetryOnSuccess(sample, ended);
                    }

                    Publish(sample);

                    bool active = sample.BlocksEnergize;
                    if (active != _lastPressedOrUnknown)
                    {
                        LiveEStopMonitorKind kind;
                        if (!sample.ReadOk) kind = LiveEStopMonitorKind.Unknown;
                        else if (sample.Pressed) kind = LiveEStopMonitorKind.Pressed;
                        else kind = LiveEStopMonitorKind.Released;
                        FireEdge(kind, sample);
                    }
                    _lastPressedOrUnknown = active;
                }
                catch { }
                try { Thread.Sleep(_pollMs); } catch { }
            }
        }

        bool TryBeginIdleRetry(LiveEStopSample sample, long began, long ended)
        {
            lock (_cacheGate)
            {
                if (_activeMode || !_idleRetryEnabled || _retryPending || _retryExhausted)
                    return false;
                if (ended - began > MaximumAgeMs)
                    return false;
                _retryPending = true;
                _retryBeganAtMs = began;
                _lastSample = null;
                _lastSampleAtMs = -1;
                _retryDetail = "P0.4空闲一次串行重读；firstFail=" + (sample != null ? sample.Error : "")
                    + "; beganMs=" + began + "; endedMs=" + ended
                    + "；不清除驱动/历史错误，不复用旧HIGH。";
                System.Console.WriteLine("[P0.4监视] " + _retryDetail);
                return true;
            }
        }

        bool TryExhaustIdleRetry(LiveEStopSample sample, long began, long ended)
        {
            lock (_cacheGate)
            {
                if (_activeMode || !_idleRetryEnabled || !_retryPending || _retryExhausted)
                    return false;
                if (ended - _retryBeganAtMs > MaximumAgeMs)
                {
                    // 总不确定性超 300 ms：仍标重建，不接受结果
                }
                _retryPending = false;
                _retryExhausted = true;
                _idleRetryEnabled = false;
                _lastSample = null;
                _lastSampleAtMs = -1;
                _retryDetail += "; secondFail=" + (sample != null ? sample.Error : "")
                    + "; secondUtcMs=" + ended
                    + "；禁止输出，只允许owner确认旧读退出后一次DI重建。";
                System.Console.WriteLine("[P0.4监视] P04_IDLE_RETRY_EXHAUSTED " + _retryDetail);
                return true;
            }
        }

        void ClearIdleRetryOnSuccess(LiveEStopSample sample, long ended)
        {
            lock (_cacheGate)
            {
                if (!_retryPending) return;
                System.Console.WriteLine("[P0.4监视] P04_IDLE_RETRY_RECOVERED；仅恢复只读状态，不启动加压；"
                    + (sample != null ? sample.FormatLog() : "") + "; atMs=" + ended);
                _retryPending = false;
                _retryDetail = string.Empty;
                _retryBeganAtMs = -1;
            }
        }

        void FireEdge(LiveEStopMonitorKind kind, LiveEStopSample sample)
        {
            System.Console.WriteLine("[P0.4监视] 边沿 " + kind + " " + (sample != null ? sample.FormatLog() : ""));
            try { _onEvent(kind, sample); } catch { }
        }

        void Publish(LiveEStopSample sample)
        {
            if (sample == null) return;
            lock (_cacheGate)
            {
                _lastSample = sample;
                _lastSampleAtMs = _clock.ElapsedMilliseconds;
            }
        }

        /// <summary>重建成功后由会话调用：清重建门闩，可再启空闲重读（本会话 IdleDiRebuildPolicy 仍只允许一次）。</summary>
        public void AcknowledgeIdleRebuildRecovered()
        {
            lock (_cacheGate)
            {
                _retryExhausted = false;
                _retryPending = false;
                _retryDetail = string.Empty;
                _retryBeganAtMs = -1;
                if (!_activeMode)
                    _idleRetryEnabled = true;
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LiveEmergencyMonitor));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
