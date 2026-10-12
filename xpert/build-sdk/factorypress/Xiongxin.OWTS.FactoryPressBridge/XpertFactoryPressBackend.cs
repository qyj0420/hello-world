using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Xiongxin.Owts.Acquisition.Live;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;
using Xiongxin.Owts.HostAdapters;

namespace Xiongxin.OWTS.FactoryPressBridge
{
    /// <summary>
    /// 雄新电力 · 雄新 OWTS — FactoryPress hardware path for the Xpert「加压测试」page.
    ///
    /// One instance owns one LiveAcquisitionSession for the lifetime of a pressure page:
    /// Open acquires DeviceSdkLease (Pico 5242D opened once), starts LiveEmergencyMonitor
    /// (P0.4, independent loop) and drives P0.5 via the Live backend. Each round calls
    /// OwtsServices.RunFactoryAlignedPress (Task6RangeAdvisor auto-range, PicoCaptureCooldown,
    /// HV OFF + Pico sync on e-stop/fault inside LiveAcquisitionSession). Faults are latched by
    /// SessionFault; cleanup failure keeps the lease and forbids reopen in this process.
    ///
    /// The host must not run the legacy PressureRuntime worker for the same page while an
    /// instance is open: both stacks drive the same ART/Pico.
    /// </summary>
    public sealed class XpertFactoryPressBackend : IDisposable
    {
        public const string Brand = "雄新电力 · 雄新 OWTS";
        public const string EnableEnvVar = "XIONGXIN_OWTS_FACTORY_ALIGNED";
        public const string FakeLiveEnvVar = "XIONGXIN_OWTS_FACTORY_ALIGNED_FAKE";
        public const string EnableMarkerFile = "factory-press-backend.enabled";

        public const string OutcomeCompleted = "Completed";
        public const string OutcomeOperatorStopped = "OperatorStopped";
        public const string OutcomeSafetyAbort = "SafetyAbort";
        public const string OutcomeFailed = "Failed";
        public const string OutcomeRefused = "Refused";

        readonly object _gate = new object();
        OwtsServices _services;
        bool _disposed;
        bool _pressing;
        bool _requiresReopen;
        string _lastOutcome = string.Empty;

        readonly double _maximumKv;

        XpertFactoryPressBackend(OwtsServices services, double maximumKv)
        {
            _services = services;
            _maximumKv = maximumKv;
        }

        public double MaximumKv { get { return _maximumKv; } }

        /// <summary>
        /// Explicit opt-in: <c>XIONGXIN_OWTS_FACTORY_ALIGNED=1</c> or a <c>factory-press-backend.enabled</c>
        /// file next to the executable. Absent → the validated PressureRuntime path stays in charge.
        /// </summary>
        public static bool IsEnabled(string baseDirectory)
        {
            string v = Environment.GetEnvironmentVariable(EnableEnvVar);
            if (string.Equals(v, "0", StringComparison.Ordinal) || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(v, "1", StringComparison.Ordinal) || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrEmpty(baseDirectory) && File.Exists(Path.Combine(baseDirectory, EnableMarkerFile));
        }

        public static bool FakeLiveRequested
        {
            get { return string.Equals(Environment.GetEnvironmentVariable(FakeLiveEnvVar), "1", StringComparison.Ordinal); }
        }

        /// <summary>
        /// Pre-open gate: a SessionFault cleanup latch or the process cleanup-failed gate forbids
        /// opening a new hardware session. Returns an empty string when opening is allowed.
        /// </summary>
        public static string Preflight()
        {
            SessionFault fault;
            bool cleanupForbidsReopen;
            if (SessionFault.TryReadAnyLatch(out fault, out cleanupForbidsReopen)
                && (cleanupForbidsReopen || fault.Category == SessionFaultCategory.CleanupError))
                return Brand + "：上次设备清理失败，禁止重新打开硬件（" + (fault.Detail ?? fault.Category.ToString())
                    + "）。请确认高压已关断后由维护人员处理 session-fault 记录。";
            if (SessionFault.HasCleanupErrorForbiddingReopen())
                return Brand + "：存在未处理的设备清理失败记录，禁止重新打开硬件。";
            if (Xiongxin.Owts.Acquisition.SingletonAcquisitionSession.ProcessCleanupFailed)
                return Brand + "：本进程设备清理失败门闩已置位，须退出程序并核查现场后再连接。";
            return string.Empty;
        }

        /// <summary>
        /// Opens the FactoryPress Live session (FakeLive only when explicitly requested).
        /// Gain/divider come from the Xpert device parameters, not from FactoryPress samples.
        /// </summary>
        /// <remarks>
        /// OWTS opens ART by module name (OWTS_ART_DEVICE, default Dev1) and its identity gate compares
        /// that name, so the Xpert ART serial is recorded only. The Pico is opened by the Xpert serial.
        /// </remarks>
        public static XpertFactoryPressBackend Open(string xpertArtSerial, string picoSerial,
            double pressureRatioVPerAoV, double dividerRatioVPerV, double maximumKv, int initialRangeIndex,
            bool fakeLive, Action<string> log)
        {
            string gate = Preflight();
            if (gate.Length != 0) throw new InvalidOperationException(gate);
            if (!(pressureRatioVPerAoV > 0) || double.IsInfinity(pressureRatioVPerAoV))
                throw new ArgumentOutOfRangeException("pressureRatioVPerAoV", "加压比例无效，请先完成设备参数配置。");
            if (LiveBenchCalibration.IsDangerousPlaceholderVoltageGain(pressureRatioVPerAoV))
                throw new InvalidOperationException(StatusCopy.BenchGainNotCalibrated + "：加压比例为危险占位 1000，禁止非零加压。");
            if (!(dividerRatioVPerV > 0) || double.IsInfinity(dividerRatioVPerV))
                throw new ArgumentOutOfRangeException("dividerRatioVPerV", "分压比无效，请先完成设备参数配置。");
            if (!(maximumKv > 0) || double.IsInfinity(maximumKv))
                throw new ArgumentOutOfRangeException("maximumKv", "最高电压无效，请先完成设备参数配置。");

            var options = OwtsRuntimeOptions.FromEnvironment();
            options.ExpectedArtIdentity = string.Empty;
            options.ExpectedPicoSerial = picoSerial ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(picoSerial)) options.PicoSerial = picoSerial;
            options.SuppressPicoStartupWindow = true;

            OwtsServices services;
            if (fakeLive)
            {
                services = OwtsServices.CreateFakeLive(options);
            }
            else
            {
                options.AllowLiveHardware = true;
                services = OwtsServices.CreateLive(options);
            }

            bool opened = false;
            try
            {
                var bench = string.IsNullOrWhiteSpace(options.BenchCalibrationFilePath)
                    ? LiveBenchCalibration.CreateBenchSample(pressureRatioVPerAoV, dividerRatioVPerV,
                        calibratedBy: "Xpert device parameters", notes: "Xpert PressureRatio/DividerRatio；IMON/VMON 比例沿用出厂加压默认，待现场核定")
                    : LiveBenchCalibration.LoadFile(options.BenchCalibrationFilePath);
                if (string.IsNullOrWhiteSpace(options.BenchCalibrationFilePath))
                {
                    bench.VoltageGainVPerAoVolt = pressureRatioVPerAoV;
                    bench.DividerRatioVPerV = dividerRatioVPerV;
                    bench.BenchCalibrated = true;
                }

                services.Acquisition.Open(new AcquisitionSessionOptions
                {
                    Simulation = false,
                    ArtDeviceName = options.ArtDeviceName,
                    ArtSerial = string.Empty,
                    PicoSerial = picoSerial ?? string.Empty,
                    PicoRangeIndex = initialRangeIndex >= 0 ? initialRangeIndex : 6,
                    VoltageGainVPerAoVolt = 0, // keep the bench Apply below authoritative
                    HardOverCurrentA = OwtsServices.FactoryBenchHardOverCurrentA
                });
                opened = true;

                string err;
                if (!services.TryApplyBenchCalibration(bench, out err))
                    throw new InvalidOperationException("台架增益 Apply 失败：" + (err ?? string.Empty));
                var singleton = services.Acquisition as Xiongxin.Owts.Acquisition.SingletonAcquisitionSession;
                LiveAcquisitionSession live = singleton != null ? singleton.TryGetLiveSession() : null;
                if (live == null || live.HardwareOptions == null)
                    throw new InvalidOperationException("未取得出厂加压 Live 会话，拒绝加压。");
                // LiveHardwareOptions defaults to 0.05 AO V/step (300 V at gain 6000), which overshoots the ±5% reach window.
                live.HardwareOptions.RampStepAoVolts = FactoryRampStepAoVolts(live.HardwareOptions.VoltageGainVPerAoVolt);
                live.HardwareOptions.AbsoluteOverVoltageLimitV = maximumKv * 1000.0
                    * (1.0 + LiveHardwareOptions.ClampOverVoltageTripFraction(live.HardwareOptions.OverVoltageTripFraction));
                Log(log, "台架：VoltageGain=" + live.HardwareOptions.VoltageGainVPerAoVolt.ToString("G6", CultureInfo.InvariantCulture)
                    + " Divider=" + live.HardwareOptions.DividerRatioVPerV.ToString("G6", CultureInfo.InvariantCulture)
                    + " RampStepAo=" + live.HardwareOptions.RampStepAoVolts.ToString("G4", CultureInfo.InvariantCulture)
                    + " HardOC=" + OwtsServices.FactoryBenchHardOverCurrentA.ToString(CultureInfo.InvariantCulture) + "A"
                    + " 上限=" + maximumKv.ToString("G6", CultureInfo.InvariantCulture) + "kV"
                    + " ART=" + options.ArtDeviceName + "（Xpert 序列号 " + (string.IsNullOrEmpty(xpertArtSerial) ? "-" : xpertArtSerial) + " 仅记录）"
                    + " Pico=" + (string.IsNullOrEmpty(picoSerial) ? "(first unit)" : picoSerial));
                var backend = new XpertFactoryPressBackend(services, maximumKv);
                string identity;
                if (!services.TryValidateDualIdentity(out identity))
                    throw new InvalidOperationException("ART/Pico 身份与 Xpert 已连接设备不一致：" + identity);
                Log(log, services.AlignHardwareAfterOpen());
                Log(log, services.DescribeAcquisitionHardening());
                return backend;
            }
            catch
            {
                if (opened)
                {
                    try { services.Acquisition.Close(); }
                    catch (Exception closeError) { Log(log, "Open 回滚 Close 失败（保留锁）：" + closeError.Message); }
                }
                services.Dispose();
                throw;
            }
        }

        /// <summary>Same convention as PressTestRunner: ≈60 V engineering per AO step, clamped to [0.002, 0.1] AO V.</summary>
        public static double FactoryRampStepAoVolts(double voltageGainVPerAoVolt)
        {
            double step = voltageGainVPerAoVolt > 0 ? 60.0 / voltageGainVPerAoVolt : 0.01;
            return Math.Max(0.002, Math.Min(0.1, step));
        }

        public bool IsFakeLive { get { var s = _services; return s != null && s.IsFakeLive; } }

        public bool RequiresReopen { get { return _requiresReopen; } }

        public string LastOutcome { get { return _lastOutcome; } }

        /// <summary>LiveEmergencyMonitor-backed e-stop state (P0.4, active-low pressed).</summary>
        public bool EStopPressed
        {
            get
            {
                var s = _services;
                if (s == null || s.Acquisition == null) return true;
                return s.Acquisition.GetStatus().EStopPressed;
            }
        }

        public string StatusFiveStates
        {
            get
            {
                var s = _services;
                return s == null ? StatusCopy.DeviceNotConnected : s.FormatStatusFiveStates();
            }
        }

        public string SessionFaultSummary
        {
            get
            {
                var s = _services;
                return s == null ? SessionFault.FormatLatchSummary() : s.DescribeSessionFaultLatch();
            }
        }

        /// <summary>
        /// The Xpert project calibration (phase/device/timing matched by the host) is the
        /// 已校准 evidence for this session. It does not by itself permit energizing.
        /// </summary>
        public void MarkProjectCalibrated(bool calibrated)
        {
            ThrowIfDisposed();
            _services.Acquisition.SetCalibrated(calibrated);
        }

        /// <summary>FakeLive demo/smoke only; real hardware e-stop is read from P0.4 and cannot be set.</summary>
        public void SimulateFakeLiveEStop(bool pressed)
        {
            ThrowIfDisposed();
            if (!_services.IsFakeLive) throw new InvalidOperationException("仅 FakeLive 可模拟急停；实机急停只读 P0.4。");
            _services.Acquisition.SetEStopPressed(pressed);
        }

        public bool CanEnterNonZeroPressure(double targetKv, out string reason)
        {
            ThrowIfDisposed();
            _services.SyncStatusBoardFromAcquisition();
            _services.StatusBoard.TargetVoltageKv = targetKv;
            return _services.StatusBoard.CanEnterNonZeroPressure(out reason);
        }

        /// <summary>
        /// One pressure round through OwtsServices.RunFactoryAlignedPress (reps=1).
        /// Raw samples and the outcome are written to <paramref name="roundDirectory"/>.
        /// Returns one of the Outcome* codes; <paramref name="message"/> carries StatusCopy text.
        /// </summary>
        public string PressRound(double targetKv, int initialRangeIndex, string roundDirectory,
            CancellationToken token, Action<string> log, out string message)
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                if (_pressing) { message = "上一轮仍在执行，忽略重复请求。"; return OutcomeRefused; }
                if (_requiresReopen) { message = "上一轮安全退出后须关闭加压页并重新连接设备，不自动续压。"; return OutcomeRefused; }
                _pressing = true;
            }
            try
            {
                string reason;
                if (double.IsNaN(targetKv) || double.IsInfinity(targetKv) || targetKv < 0 || targetKv > _maximumKv)
                {
                    message = "目标电压须在 0～" + _maximumKv.ToString("G6", CultureInfo.InvariantCulture) + " kV（设备参数上限）。";
                    return Finish(ref message, OutcomeRefused, null, roundDirectory, targetKv);
                }
                if (targetKv > 0)
                {
                    string latchDetail;
                    if (!_services.PreparePressBatch(out latchDetail))
                    {
                        message = string.IsNullOrEmpty(latchDetail) ? StatusCopy.SoftwareEmergencyLatch : latchDetail;
                        return Finish(ref message, OutcomeRefused, null, roundDirectory, targetKv);
                    }
                    if (!CanEnterNonZeroPressure(targetKv, out reason))
                    {
                        message = reason;
                        return Finish(ref message, OutcomeRefused, null, roundDirectory, targetKv);
                    }
                }

                var results = _services.RunFactoryAlignedPress(targetKv * 1000.0, 1, token, log,
                    maxRangeRetries: 2, initialRangeIndex: initialRangeIndex);
                FactoryAlignedPressResult last = results != null && results.Count > 0 ? results[results.Count - 1] : null;
                if (last == null)
                {
                    message = "加压未返回结果。";
                    return Finish(ref message, OutcomeFailed, null, roundDirectory, targetKv);
                }
                if (last.OperatorStopped)
                {
                    message = StatusCopy.OperatorAbortNoLatch;
                    return Finish(ref message, OutcomeOperatorStopped, last, roundDirectory, targetKv);
                }
                if (last.HardSafetyAbort)
                {
                    _requiresReopen = true;
                    message = last.Message + "；" + SessionFault.FormatLatchSummary();
                    return Finish(ref message, OutcomeSafetyAbort, last, roundDirectory, targetKv);
                }
                message = last.Message ?? string.Empty;
                return Finish(ref message, last.Success ? OutcomeCompleted : OutcomeFailed, last, roundDirectory, targetKv);
            }
            catch (OperationCanceledException)
            {
                message = StatusCopy.OperatorAbortNoLatch;
                return Finish(ref message, OutcomeOperatorStopped, null, roundDirectory, targetKv);
            }
            finally
            {
                lock (_gate) _pressing = false;
            }
        }

        /// <summary>UI「停止」：OperatorAbort（不永久锁存）；P0.4 急停仍独立。</summary>
        public void RequestStop()
        {
            var s = _services;
            if (s != null && !_disposed) s.RequestOperatorAbort();
        }

        /// <summary>
        /// Close the Live session. Returns empty on a clean release; otherwise the cleanup
        /// failure (lease retained, reopen forbidden in this process).
        /// </summary>
        public string Close()
        {
            var s = _services;
            if (s == null) return string.Empty;
            try
            {
                if (s.Acquisition != null) s.Acquisition.Close();
            }
            catch (Exception ex)
            {
                return "设备关闭失败：" + ex.Message + "；" + SessionFault.FormatLatchSummary();
            }
            if (s.ProcessCleanupFailed)
                return "设备清理失败门闩已置位；不表示已关断或放电。" + SessionFault.FormatLatchSummary();
            return string.Empty;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var s = _services;
            _services = null;
            if (s != null) s.Dispose();
        }

        string Finish(ref string message, string outcome, FactoryAlignedPressResult result, string directory, double targetKv)
        {
            _lastOutcome = outcome;
            if (!string.IsNullOrEmpty(directory))
            {
                try { WriteRound(directory, outcome, message, result, targetKv); }
                catch (Exception ex) { message += "（原始记录写入失败：" + ex.Message + "）"; }
            }
            return outcome;
        }

        static void WriteRound(string directory, string outcome, string message, FactoryAlignedPressResult result, double targetKv)
        {
            Directory.CreateDirectory(directory);
            AcquisitionCycleResult cycle = result != null ? result.Cycle : null;
            var sb = new StringBuilder();
            sb.Append("{\n");
            Json(sb, "schema", "XIONGXIN.OWTS.FactoryAlignedRound/1");
            Json(sb, "brand", Brand);
            Json(sb, "outcome", outcome);
            Json(sb, "message", message ?? string.Empty);
            Json(sb, "targetKv", targetKv);
            Json(sb, "rangeIndexUsed", result != null ? result.RangeIndexUsed : -1);
            if (cycle != null)
            {
                Json(sb, "exitReason", cycle.ExitReason.ToString());
                Json(sb, "errorMessage", cycle.ErrorMessage ?? string.Empty);
                Json(sb, "startedUtc", cycle.StartedUtc.ToString("o", CultureInfo.InvariantCulture));
                Json(sb, "completedUtc", cycle.CompletedUtc.ToString("o", CultureInfo.InvariantCulture));
                Json(sb, "peakActualVoltageV", cycle.PeakActualVoltageV);
                Json(sb, "peakCurrentA", cycle.PeakCurrentA);
                Json(sb, "picoSampleRateHz", cycle.PicoSampleRateHz);
                Json(sb, "picoResolutionBits", cycle.PicoResolutionBits);
                Json(sb, "picoFullScaleVolts", cycle.PicoFullScaleVolts);
                Json(sb, "picoMaximumAdc", (int)cycle.PicoMaximumAdc);
                Json(sb, "picoRangeIndexUsed", cycle.PicoRangeIndexUsed);
                Json(sb, "picoOverrangeOrClipped", cycle.PicoOverrangeOrClipped);
                Json(sb, "task6RangeSuitable", cycle.Task6RangeSuitable);
                Json(sb, "suggestedPicoRangeIndex", cycle.SuggestedPicoRangeIndex);
                Json(sb, "task6RangeMessage", cycle.Task6RangeMessage ?? string.Empty);
                Json(sb, "usbSampleRateHz", cycle.UsbSampleRateHz);
                Json(sb, "picoSerial", cycle.PicoSerial ?? string.Empty);
                Json(sb, "artSerial", cycle.ArtSerial ?? string.Empty);
                Json(sb, "dischargeVerified", cycle.DischargeVerified);
                Json(sb, "picoRawAdcFile", cycle.PicoRawAdc != null ? "pico-raw-adc.i16le" : string.Empty);
                Json(sb, "dividerRawVoltsFile", cycle.UsbDividerRawVolts != null ? "divider-raw-volts.f64le" : string.Empty);
                if (cycle.PicoRawAdc != null) WriteShorts(Path.Combine(directory, "pico-raw-adc.i16le"), cycle.PicoRawAdc);
                if (cycle.UsbDividerRawVolts != null) WriteDoubles(Path.Combine(directory, "divider-raw-volts.f64le"), cycle.UsbDividerRawVolts);
            }
            Json(sb, "analysisFeed", "NotConnected: FactoryPress cycle format is not the Xpert BenchCapture round format; E+ is not fed from this record.", last: true);
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(directory, "owts-factory-round.json"), sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteShorts(string path, short[] values)
        {
            using (var w = new BinaryWriter(File.Create(path)))
                foreach (short v in values) w.Write(v);
        }

        static void WriteDoubles(string path, double[] values)
        {
            using (var w = new BinaryWriter(File.Create(path)))
                foreach (double v in values) w.Write(v);
        }

        static void Json(StringBuilder sb, string key, object value, bool last = false)
        {
            sb.Append("  \"").Append(key).Append("\": ");
            if (value is string)
                sb.Append('"').Append(Escape((string)value)).Append('"');
            else if (value is bool)
                sb.Append((bool)value ? "true" : "false");
            else if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
            }
            else
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            sb.Append(last ? "\n" : ",\n");
        }

        static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static void Log(Action<string> log, string line)
        {
            if (log != null && !string.IsNullOrEmpty(line)) log(line);
        }

        void ThrowIfDisposed()
        {
            if (_disposed || _services == null) throw new ObjectDisposedException(GetType().Name);
        }
    }
}
