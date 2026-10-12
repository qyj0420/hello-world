using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Xiongxin.Owts.Acquisition;
using Xiongxin.Owts.Acquisition.Live;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 雄新电力 · 雄新 OWTS 出厂加压测试 高阶 API。
    /// 仅编排：Connect → 校准采集 → Press(list + auto range) → Stop/Release。
    /// 不迁 Factory UI / Resident server。
    /// </summary>
    public sealed class FactoryPressApi : IDisposable
    {
        public const string BrandName = "雄新电力";
        public const string ProductName = "雄新 OWTS 出厂加压测试";

        readonly TextWriter _log;
        PressTestConfig _config;
        PressTestRunner _runner;
        bool _connected;
        bool _disposed;
        int _lastExit = -1;

        public FactoryPressApi(TextWriter log = null)
        {
            _log = log ?? Console.Out;
        }

        string _runDirectory;
        string _csvPath;
        int _completedSteps;
        bool _abortedBySafety;
        string _abortReason;

        public PressTestConfig Config { get { return _config; } }
        public string RunDirectory { get { return _runner != null ? _runner.RunDirectory : _runDirectory; } }
        public string CsvPath { get { return _runner != null ? _runner.CsvPath : _csvPath; } }
        public int CompletedSteps { get { return _runner != null ? _runner.CompletedSteps : _completedSteps; } }
        public bool AbortedBySafety { get { return _runner != null ? _runner.AbortedBySafety : _abortedBySafety; } }
        public string AbortReason { get { return _runner != null ? _runner.AbortReason : _abortReason; } }
        public int LastExitCode { get { return _lastExit; } }
        public bool IsConnected { get { return _connected && _runner != null; } }
        /// <summary>自动量程后的当前 Pico 量程索引（UI 下拉同步）。</summary>
        public int CurrentPicoRangeIndex
        {
            get
            {
                if (_runner != null) return _runner.CurrentPicoRangeIndex;
                return _config != null ? _config.InitialPicoRangeIndex : -1;
            }
        }

        public double LastCalibrationPeakVolts
        {
            get { return _runner != null ? _runner.LastCalibrationPeakVolts : double.NaN; }
        }
        public double LastComputedPdCalibrationPcPerVolt
        {
            get { return _runner != null ? _runner.LastComputedPdCalibrationPcPerVolt : double.NaN; }
        }
        public double LastInjectedChargePc
        {
            get { return _runner != null ? _runner.LastInjectedChargePc : double.NaN; }
        }

        /// <summary>写入配置中的局放校准系数（加压路径直接读取 Config.PdCalibrationPcPerVolt）。</summary>
        public void ApplyPdCalibrationPcPerVolt(double pcPerVolt, double injectedChargePc = double.NaN)
        {
            EnsureConnected();
            if (!(pcPerVolt > 0) || double.IsNaN(pcPerVolt) || double.IsInfinity(pcPerVolt))
                throw new ArgumentOutOfRangeException("pcPerVolt", "校准系数须为正有限值（pC/V）。");
            _config.PdCalibrationPcPerVolt = pcPerVolt;
            if (injectedChargePc > 0 && !double.IsNaN(injectedChargePc))
                _config.InjectedChargePc = injectedChargePc;
            _log.WriteLine("已应用局放校准系数 pdCalibrationPcPerVolt="
                + pcPerVolt.ToString(CultureInfo.InvariantCulture)
                + (injectedChargePc > 0 ? (" injectedChargePc=" + injectedChargePc.ToString(CultureInfo.InvariantCulture)) : ""));
        }

        /// <summary>从本地 JSON 加载并应用到当前 Config（启动加压前调用）。</summary>
        public bool TryLoadAndApplySavedPdCalibration(out string pathUsed, out string detail)
        {
            pathUsed = null;
            detail = null;
            PdCalibrationRecord rec;
            if (!PdCalibrationStore.TryLoad(out rec, out pathUsed))
            {
                detail = "无已保存校准系数：" + (pathUsed ?? PdCalibrationStore.DefaultPath());
                return false;
            }
            if (_config == null)
            {
                detail = "Config 为空。";
                return false;
            }
            _config.PdCalibrationPcPerVolt = rec.PdCalibrationPcPerVolt;
            if (rec.InjectedChargePc > 0) _config.InjectedChargePc = rec.InjectedChargePc;
            detail = "已加载 pdCalibrationPcPerVolt="
                + rec.PdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture)
                + " from " + pathUsed;
            _log.WriteLine(detail);
            return true;
        }


        /// <summary>操作员确认源关断后复位清理门；成功则可再 Connect。</summary>
        public static bool TryResetCleanupGateAfterOperatorAck(bool operatorAcknowledgedSourceOff, out string detail)
        {
            return SingletonAcquisitionSession.TryResetCleanupGateAfterOperatorAck(operatorAcknowledgedSourceOff, out detail);
        }

        /// <summary>当前是否存在清理失败门（进程或落盘）。</summary>
        public static bool IsCleanupGateSet
        {
            get { return SingletonAcquisitionSession.ProcessCleanupFailed; }
        }

        /// <summary>Connect：打开 Singleton 会话（FakeLive 或 Live），Apply 台架标定。</summary>
        public void Connect(PressTestConfig config)
        {
            ThrowIfDisposed();
            if (_connected)
                throw new InvalidOperationException("已 Connect；请先 Release 再开新会话。");
            if (config == null) throw new ArgumentNullException("config");
            if (SingletonAcquisitionSession.ProcessCleanupFailed)
                throw new InvalidOperationException(
                    "本进程清理失败门闩已置位。若已确认高压/AO 关断，请调用 TryResetCleanupGateAfterOperatorAck 后再 Connect。落盘="
                    + SessionFault.DefaultDirectory());

            _config = config;
            _runner = new PressTestRunner(_config, _log);
            _runner.ConnectOnly();
            _connected = true;
            _log.WriteLine("[{0}] Connect OK mode={1} gain={2}",
                ProductName,
                _config.Mode,
                (!double.IsNaN(_config.VoltageGainVPerAoVolt) && _config.VoltageGainVPerAoVolt > 0
                    ? _config.VoltageGainVPerAoVolt
                    : PressTestConfig.TestBenchVoltageGain).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>校准前把界面注入档/脉冲平均/缆长等写入 Config（勿沿用 Connect 时的旧注入量）。</summary>
        public void ApplyCalibrationUiSettings(double injectedChargePc, int pulseAverageCount, double cableLengthM, double velocityMPerUs, double periodToleranceFraction)
        {
            EnsureConnected();
            if (!(injectedChargePc > 0) || double.IsNaN(injectedChargePc) || double.IsInfinity(injectedChargePc))
                throw new ArgumentOutOfRangeException("injectedChargePc", "注入放电量须为正有限值（pC）。");
            _config.InjectedChargePc = injectedChargePc;
            if (pulseAverageCount >= 2) _config.PulseAverageCount = pulseAverageCount;
            if (cableLengthM > 0) _config.CableLengthM = cableLengthM;
            if (velocityMPerUs > 0) _config.VelocityMPerUs = velocityMPerUs;
            if (periodToleranceFraction > 0) _config.PeriodToleranceFraction = periodToleranceFraction;
            _log.WriteLine("校准 UI 设定：injectedChargePc="
                + injectedChargePc.ToString("G9", CultureInfo.InvariantCulture)
                + " pC（" + (injectedChargePc / 1000.0).ToString("G6", CultureInfo.InvariantCulture)
                + " nC）；pulseAvg=" + _config.PulseAverageCount
                + "；L=" + _config.CableLengthM.ToString("G6", CultureInfo.InvariantCulture) + " m");
        }

        /// <summary>校准采集：自动量程 + Rapid 脉冲固定间隔筛选 + 页面 N 次平均；阻抗可调；急停可中止。</summary>
        public int Calibrate(CancellationToken cancellation = default(CancellationToken))
        {
            EnsureConnected();
            int code = _runner.CalibrateOnly(cancellation);
            _lastExit = code;
            return code;
        }

        /// <summary>连续加压每轮前同步界面 Pico/阻抗设定。</summary>
        public void ApplyUiCaptureSettings(int resolutionBits, double targetSampleRateHz, int rangeIndex, bool impedanceWide)
        {
            EnsureConnected();
            _runner.ApplyUiCaptureSettings(resolutionBits, targetSampleRateHz, rangeIndex, impedanceWide);
        }

        /// <summary>阻抗带宽即时生效，与加压/Pico 采集设定解耦。</summary>
        public void SetImpedanceBandwidthWide(bool wide)
        {
            EnsureConnected();
            if (_config != null) _config.ImpedanceBandwidthWide = wide;
            _runner.SetImpedanceBandwidthWide(wide);
        }

        /// <summary>Press：连续加压列表 + 量程自动调整；档间等 Pico 冷却（Live）或配置冷却。</summary>
        public int Press(IList<double> targetsKv = null, CancellationToken cancellation = default(CancellationToken))
        {
            EnsureConnected();
            if (targetsKv != null && targetsKv.Count > 0)
            {
                _config.TargetsKv = new List<double>(targetsKv);
            }
            int code = _runner.PressOnly(cancellation);
            _lastExit = code;
            return code;
        }

        /// <summary>Stop：取消当前加压并关源（不永久锁存）；不释放会话。</summary>
        public void Stop()
        {
            if (_runner != null)
                _runner.RequestStop();
        }

        /// <summary>顶栏/联锁用：真实会话快照；未连接返回 null。</summary>
        public AcquisitionStatusSnapshot TryGetStatus()
        {
            if (!_connected || _runner == null) return null;
            return _runner.TryGetStatus();
        }

        public bool TryClearEmergencyLatchIfHardwareSafe(out string detail)
        {
            detail = null;
            if (!_connected || _runner == null) return false;
            return _runner.TryClearEmergencyLatchIfHardwareSafe(out detail);
        }

        /// <summary>Release：Close 会话并释放；cleanupFailed 时退出码非 0 语义由调用方处理。</summary>
        public int Release()
        {
            if (_runner == null)
            {
                _connected = false;
                return 0;
            }
            _runDirectory = _runner.RunDirectory;
            _csvPath = _runner.CsvPath;
            _completedSteps = _runner.CompletedSteps;
            _abortedBySafety = _runner.AbortedBySafety;
            _abortReason = _runner.AbortReason;
            // Dispose 会 Close；先记下清理失败，再 Dispose
            try { _runner.Dispose(); } catch { }
            int code = _runner.CleanupFailedOnClose ? 7 : 0;
            _runner = null;
            _connected = false;
            _log.WriteLine("[{0}] Release done exitHint={1}", ProductName, code);
            return code;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Release(); } catch { }
        }

        void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!_connected || _runner == null)
                throw new InvalidOperationException("尚未 Connect。");
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FactoryPressApi));
        }
    }
}
