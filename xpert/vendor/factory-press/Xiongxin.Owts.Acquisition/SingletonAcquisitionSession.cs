using System;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;
using Xiongxin.Owts.Acquisition.Live;

namespace Xiongxin.Owts.Acquisition
{
    /// <summary>
    /// 进程级采集单例：全局只允许 Open 一次 Pico+Art 会话；校准与加压共用。
    /// 二次 Open 返回明确错误。仿真路径同样强制单例语义。
    /// </summary>
    public sealed class SingletonAcquisitionSession : IAcquisitionSession
    {
        static readonly object GlobalGate = new object();
        static SingletonAcquisitionSession s_openOwner;
        /// <summary>进程内清理失败门闩：置位后禁止任何 Singleton 再 Open。</summary>
        static bool s_cleanupFailedProcess;

        readonly IAcquisitionSession _inner;
        bool _disposed;
        bool _ownsOpen;

        public SingletonAcquisitionSession(IAcquisitionSession inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            _inner = inner;
        }

        /// <summary>便捷：包装仿真内核。</summary>
        public static SingletonAcquisitionSession CreateSimulation()
        {
            return CreateSimulation(null);
        }

        public static SingletonAcquisitionSession CreateSimulation(IFirstZeroPhaseEvaluator firstZeroPhaseEvaluator)
        {
            return new SingletonAcquisitionSession(new SimulationAcquisitionSession(firstZeroPhaseEvaluator));
        }

        public event EventHandler<HardwareStatusChangedEventArgs> StatusChanged
        {
            add { _inner.StatusChanged += value; }
            remove { _inner.StatusChanged -= value; }
        }

        public bool IsOpen
        {
            get
            {
                lock (GlobalGate)
                    return _ownsOpen && _inner.IsOpen;
            }
        }

        public bool ConnectionIndicationLampHigh
        {
            get { return _inner.ConnectionIndicationLampHigh; }
        }

        public bool HoldGroundActive { get { return _inner.HoldGroundActive; } }

        public bool ConfirmReleaseGround(bool operatorAcknowledged)
        {
            return _inner.ConfirmReleaseGround(operatorAcknowledged);
        }

        public HardwareIdentityProbeResult Probe(HardwareProbeRequest request)
        {
            return _inner.Probe(request);
        }

        public void Open(AcquisitionSessionOptions options)
        {
            ThrowIfDisposed();
            lock (GlobalGate)
            {
                if (s_cleanupFailedProcess || SessionFault.HasCleanupErrorForbiddingReopen())
                {
                    s_cleanupFailedProcess = true;
                    throw new InvalidOperationException(
                        StatusCopy.CleanupFailedNoReopen + "；落盘=" + SessionFault.DefaultDirectory());
                }
                if (s_openOwner != null && s_openOwner != this)
                {
                    throw new InvalidOperationException(
                        "Pico+Art 会话已被其它持有者 Open；校准与加压须共用同一会话，禁止二次 Open。"
                        + " 当前连接态文案应为「" + StatusCopy.Connected + "」，≠「" + StatusCopy.ReadyToEnergize + "」。");
                }
                if (_ownsOpen || _inner.IsOpen)
                {
                    throw new InvalidOperationException(
                        "本会话已 Open（Pico 5242D 全局只允许一次）。校准与加压共用；请勿再次 Open。"
                        + " 状态：「" + StatusCopy.Connected + "」。");
                }

                _inner.Open(options ?? new AcquisitionSessionOptions { Simulation = true });
                _ownsOpen = true;
                s_openOwner = this;
            }
        }

        public void Close()
        {
            lock (GlobalGate)
            {
                if (_ownsOpen || ReferenceEquals(s_openOwner, this))
                {
                    try { _inner.Close(); }
                    finally
                    {
                        var live = _inner as LiveAcquisitionSession;
                        if (live != null && live.CleanupFailed)
                            s_cleanupFailedProcess = true;
                        _ownsOpen = false;
                        if (ReferenceEquals(s_openOwner, this))
                            s_openOwner = null;
                    }
                }
            }
        }

        public AcquisitionStatusSnapshot GetStatus()
        {
            return _inner.GetStatus();
        }

        public Task<AcquisitionCycleResult> RunCycleAsync(AcquisitionCycleRequest request, CancellationToken cancellation)
        {
            ThrowIfDisposed();
            if (!IsOpen)
                throw new InvalidOperationException("Session is not open. 须先 Open 唯一 Pico+Art 会话。");
            return _inner.RunCycleAsync(request, cancellation);
        }

        public void EmergencyStop()
        {
            _inner.EmergencyStop();
        }

        public void RequestOperatorAbort()
        {
            _inner.RequestOperatorAbort();
        }

        /// <summary>Live/FakeLive：P0.4 解除且源关时清软件锁存；仿真返回 true。</summary>
        public bool TryClearEmergencyLatchIfHardwareSafe(out string detail)
        {
            var live = _inner as LiveAcquisitionSession;
            if (live != null)
                return live.TryClearEmergencyLatchIfHardwareSafe(out detail);
            detail = "非 Live/FakeLive（无软件急停锁存语义）";
            return true;
        }

        public bool ReadSafetyReady()
        {
            return _inner.ReadSafetyReady();
        }

        public void SetEStopPressed(bool pressed)
        {
            ThrowIfDisposed();
            _inner.SetEStopPressed(pressed);
        }

        public void SetCalibrated(bool calibrated)
        {
            ThrowIfDisposed();
            _inner.SetCalibrated(calibrated);
        }

        public bool ImpedanceBandwidthWide { get { return _inner.ImpedanceBandwidthWide; } }

        public void SetImpedanceBandwidthWide(bool wide)
        {
            ThrowIfDisposed();
            _inner.SetImpedanceBandwidthWide(wide);
        }

        public void SetConnectionIndicationLamp(bool high)
        {
            _inner.SetConnectionIndicationLamp(high);
        }

        /// <summary>若内层为 Live/FakeLive，返回会话；仿真内核返回 null。</summary>
        public LiveAcquisitionSession TryGetLiveSession()
        {
            return _inner as LiveAcquisitionSession;
        }

        /// <summary>将台架标定 Apply 到当前 Live/FakeLive 会话；仿真返回 false。</summary>
        public bool TryApplyBenchCalibration(LiveBenchCalibration cal, out string error)
        {
            ThrowIfDisposed();
            var live = _inner as LiveAcquisitionSession;
            if (live == null)
            {
                error = "当前为仿真会话，无 Live/FakeLive 可 Apply 台架标定（仿真不强制台架门；文件仍可保存供 CreateLive 加载）。";
                return false;
            }
            return live.TryApplyBenchCalibration(cal, out error);
        }

        public bool BenchCalibrationApplied
        {
            get
            {
                var live = _inner as LiveAcquisitionSession;
                return live != null && live.BenchCalibrationApplied;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            _inner.Dispose();
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SingletonAcquisitionSession));
        }

        /// <summary>测试用：清除进程级 owner（仅当无打开会话时）。</summary>
        internal static void ResetProcessOwnerForTests()
        {
            lock (GlobalGate)
            {
                if (s_openOwner != null && s_openOwner.IsOpen)
                    throw new InvalidOperationException("Cannot reset while a session is still open.");
                s_openOwner = null;
                s_cleanupFailedProcess = false;
            }
        }

        /// <summary>进程内或落盘 CleanupError 门闩：禁止再 Open。</summary>
        public static bool ProcessCleanupFailed
        {
            get
            {
                lock (GlobalGate)
                {
                    if (s_cleanupFailedProcess) return true;
                    if (SessionFault.HasCleanupErrorForbiddingReopen())
                    {
                        s_cleanupFailedProcess = true;
                        return true;
                    }
                    return false;
                }
            }
        }

        /// <summary>测试用：清除进程门闩并可选清除落盘（directory 空则 DefaultDirectory）。</summary>
        public static void ResetCleanupLatchForTests(bool clearDiskFiles = true)
        {
            lock (GlobalGate)
            {
                s_cleanupFailedProcess = false;
                if (clearDiskFiles)
                    SessionFault.ClearLatchFilesForTests();
            }
        }

        /// <summary>
        /// 操作员确认源已关断后，复位进程/落盘清理门，允许同进程再 Connect。
        /// 不自动调用；须 UI 显式确认。会话仍 Open 时拒绝。
        /// </summary>
        public static bool TryResetCleanupGateAfterOperatorAck(bool operatorAcknowledgedSourceOff, out string detail)
        {
            detail = null;
            if (!operatorAcknowledgedSourceOff)
            {
                detail = "须操作员确认高压/AO 已关断。";
                return false;
            }
            lock (GlobalGate)
            {
                if (s_openOwner != null && s_openOwner.IsOpen)
                {
                    detail = "仍有打开会话，请先 Release。";
                    return false;
                }
                s_openOwner = null;
                s_cleanupFailedProcess = false;
                SessionFault.ClearLatchFilesForTests();
                // 进程内 DeviceSdk 名表：干净复位后允许再 Acquire
                try { DeviceSdkLease.ClearProcessOwnedForTests(); DeviceSdkLease.ArmAllowAbandonedAfterCleanClose(); } catch { }
                detail = "清理门已复位（进程+落盘）；可再 Connect。落盘原目录=" + SessionFault.DefaultDirectory();
                return true;
            }
        }
    }
}
