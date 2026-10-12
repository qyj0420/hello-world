using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.Owts.Acquisition;
using Xiongxin.Owts.Acquisition.Live;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ui;
using Xiongxin.Owts.HostAdapters;

namespace Xiongxin.Owts.XpertPressBridge
{
    /// <summary>
    /// Xpert「加压测试」硬件调用入口。会话、租约、急停、量程和故障语义
    /// 全部来自出厂加压包（OwtsServices.RunFactoryAlignedPress / LiveAcquisitionSession）。
    /// </summary>
    public static class XpertPressHardware
    {
        static readonly object Gate = new object();
        static OwtsServices _services;
        static string _lastWarning = string.Empty;

        public static bool HasDeviceSession
        {
            get
            {
                lock (Gate)
                {
                    return _services != null && _services.Acquisition != null && _services.Acquisition.IsOpen;
                }
            }
        }

        public static string LastStatusLine
        {
            get { lock (Gate) return _lastWarning ?? string.Empty; }
        }

        /// <summary>
        /// 打开唯一 ART+Pico 会话（兼容旧三参调用：FakeLive 演示增益）。
        /// </summary>
        public static string OpenForXpert(string artSerial, string picoSerial, bool fakeLive)
        {
            return OpenForXpert(artSerial, picoSerial, fakeLive, 2000, 12, 35, 6, 2000);
        }

        /// <summary>
        /// 打开唯一 ART+Pico 会话，并按 Xpert 设备参数 Apply 台架增益/分压/过压上限/放电校准。
        /// 与出厂 bot / XpertFactoryPressBackend.Open 同路径：DeviceSdkLease → Live.Open →
        /// TryApplyBenchCalibration(Xpert PressureRatio/DividerRatio) → RampStepAo ≈60V/步 →
        /// AbsoluteOverVoltageLimitV → LiveEmergencyMonitor（P0.4）已在会话内。
        /// fakeLive=true 或 OWTS_XPERT_FAKE_PRESS=1 走 FakeLive。
        /// </summary>
        public static string OpenForXpert(string artSerial, string picoSerial, bool fakeLive,
            double pressureRatioVPerAoV, double dividerRatioVPerV, double maximumKv,
            int initialRangeIndex, double pdCalibrationPcPerV)
        {
            lock (Gate)
            {
                if (_services != null && _services.Acquisition != null && _services.Acquisition.IsOpen)
                    return FormatFiveStates(0);

                if (!(pressureRatioVPerAoV > 0) || double.IsNaN(pressureRatioVPerAoV) || double.IsInfinity(pressureRatioVPerAoV))
                    throw new XpertPressFaultException("NotReady", "加压比例无效，请先完成设备参数配置。");
                if (LiveBenchCalibration.IsDangerousPlaceholderVoltageGain(pressureRatioVPerAoV))
                    throw new XpertPressFaultException("NotReady", StatusCopy.BenchGainNotCalibrated + "：加压比例为危险占位 1000，禁止非零加压。");
                if (!(dividerRatioVPerV > 0) || double.IsNaN(dividerRatioVPerV) || double.IsInfinity(dividerRatioVPerV))
                    throw new XpertPressFaultException("NotReady", "分压比无效，请先完成设备参数配置。");
                if (!(maximumKv > 0) || double.IsNaN(maximumKv) || double.IsInfinity(maximumKv))
                    throw new XpertPressFaultException("NotReady", "最高电压无效，请先完成设备参数配置。");
                if (initialRangeIndex < 0 || initialRangeIndex > 10) initialRangeIndex = 6;

                var options = OwtsRuntimeOptions.FromEnvironment();
                // ART 按模块名打开（OWTS_ART_DEVICE，默认 Dev1）；Xpert ART 序列号仅记录。
                // Pico 按 Xpert 已连接序列号打开并写入期望身份。
                options.ExpectedArtIdentity = string.Empty;
                if (!string.IsNullOrWhiteSpace(picoSerial))
                {
                    options.PicoSerial = picoSerial.Trim();
                    options.ExpectedPicoSerial = picoSerial.Trim();
                }
                options.SuppressPicoStartupWindow = true;
                options.PicoCaptureProfile = PicoCaptureProfile.Field;

                bool useFake = fakeLive
                    || string.Equals(Environment.GetEnvironmentVariable("OWTS_XPERT_FAKE_PRESS"), "1", StringComparison.Ordinal);
                if (useFake)
                {
                    options.AllowLiveHardware = false;
                    _services = OwtsServices.CreateFakeLive(options);
                }
                else
                {
                    options.AllowLiveHardware = true;
                    _services = OwtsServices.CreateLive(options);
                }

                LiveBenchCalibration bench;
                try
                {
                    if (!string.IsNullOrWhiteSpace(options.BenchCalibrationFilePath)
                        && File.Exists(options.BenchCalibrationFilePath))
                        bench = LiveBenchCalibration.LoadFile(options.BenchCalibrationFilePath);
                    else
                    {
                        bench = LiveBenchCalibration.CreateBenchSample(
                            pressureRatioVPerAoV,
                            dividerRatioVPerV,
                            calibratedBy: "Xpert device parameters",
                            notes: "Xpert PressureRatio/DividerRatio；IMON/VMON 比例沿用出厂加压默认，待现场核定");
                        bench.VoltageGainVPerAoVolt = pressureRatioVPerAoV;
                        bench.DividerRatioVPerV = dividerRatioVPerV;
                        bench.BenchCalibrated = true;
                    }
                }
                catch (Exception ex)
                {
                    SafeDisposeServices();
                    throw new XpertPressFaultException("NotReady", "台架标定无效：" + ex.Message, ex);
                }

                var open = new AcquisitionSessionOptions
                {
                    ArtDeviceName = options.ArtDeviceName,
                    ArtSerial = artSerial ?? string.Empty,
                    PicoSerial = picoSerial ?? string.Empty,
                    PicoRangeIndex = initialRangeIndex,
                    VoltageGainVPerAoVolt = 0, // Keep TryApplyBenchCalibration authoritative.
                    HardOverCurrentA = OwtsServices.FactoryBenchHardOverCurrentA,
                    Simulation = _services.IsFakeLive
                };
                try
                {
                    _services.Acquisition.Open(open);
                }
                catch (Exception ex)
                {
                    SafeDisposeServices();
                    throw new XpertPressFaultException("DeviceOffline", "设备会话打开失败：" + ex.Message, ex);
                }

                if (!_services.Acquisition.IsOpen)
                {
                    string detail = "Open 未持有会话。";
                    try
                    {
                        var st = _services.Acquisition.GetStatus();
                        if (st != null && !string.IsNullOrEmpty(st.LastError)) detail = st.LastError;
                    }
                    catch { }
                    SafeDisposeServices();
                    throw new XpertPressFaultException("DeviceOffline", detail);
                }

                string err;
                if (!_services.TryApplyBenchCalibration(bench, out err))
                {
                    SafeDisposeServices();
                    throw new XpertPressFaultException("NotReady", "台架增益 Apply 失败：" + (err ?? string.Empty));
                }

                var singleton = _services.Acquisition as SingletonAcquisitionSession;
                LiveAcquisitionSession live = singleton != null ? singleton.TryGetLiveSession() : null;
                if (live == null || live.HardwareOptions == null)
                {
                    SafeDisposeServices();
                    throw new XpertPressFaultException("DeviceOffline", "未取得出厂加压 Live 会话（DeviceSdkLease / LiveAcquisitionSession），拒绝加压。");
                }

                // 与 PressTestRunner / 出厂 bot：斜坡步长 clamp(60/gain, 0.002, 0.1) AO V，避免默认 0.05 在高增益下越窗。
                live.HardwareOptions.RampStepAoVolts = FactoryRampStepAoVolts(live.HardwareOptions.VoltageGainVPerAoVolt);
                live.HardwareOptions.AbsoluteOverVoltageLimitV = maximumKv * 1000.0
                    * (1.0 + LiveHardwareOptions.ClampOverVoltageTripFraction(live.HardwareOptions.OverVoltageTripFraction));

                string identity;
                if (!_services.TryValidateDualIdentity(out identity))
                {
                    SafeDisposeServices();
                    throw new XpertPressFaultException("DeviceOffline", "ART/Pico 身份与 Xpert 已连接设备不一致：" + identity);
                }

                _services.Acquisition.SetConnectionIndicationLamp(true);
                _services.ApplyXpertPdCalibrationPcPerVolt(pdCalibrationPcPerV);
                string align = _services.AlignHardwareAfterOpen();
                // AlignHardwareAfterOpen 可能从文件覆盖 pC/V；Xpert 已匹配校准优先。
                _services.ApplyXpertPdCalibrationPcPerVolt(pdCalibrationPcPerV);
                string harden = _services.DescribeAcquisitionHardening();
                _lastWarning = (align ?? string.Empty)
                    + (string.IsNullOrEmpty(harden) ? "" : " · " + harden)
                    + " · VoltageGain=" + live.HardwareOptions.VoltageGainVPerAoVolt.ToString("G6", CultureInfo.InvariantCulture)
                    + " Divider=" + live.HardwareOptions.DividerRatioVPerV.ToString("G6", CultureInfo.InvariantCulture)
                    + " RampStepAo=" + live.HardwareOptions.RampStepAoVolts.ToString("G4", CultureInfo.InvariantCulture)
                    + " 上限=" + maximumKv.ToString("G6", CultureInfo.InvariantCulture) + "kV";
                return FormatFiveStates(0) + (string.IsNullOrEmpty(_lastWarning) ? "" : " · " + _lastWarning);
            }
        }

        /// <summary>出厂加压同路径：目标工程电压约 60 V/步，换算为 AO 步长并夹紧。</summary>
        public static double FactoryRampStepAoVolts(double voltageGainVPerAoVolt)
        {
            double step = voltageGainVPerAoVolt > 0 ? 60.0 / voltageGainVPerAoVolt : 0.01;
            return Math.Max(0.002, Math.Min(0.1, step));
        }

        public static void MarkDischargeCalibrated()
        {
            lock (Gate)
            {
                RequireOpen();
                _services.Acquisition.SetCalibrated(true);
            }
        }

        public static string FormatFiveStates(double targetKv)
        {
            lock (Gate)
            {
                var board = BuildBoard(targetKv);
                string line = board.FormatFiveStates();
                if (!string.IsNullOrEmpty(_lastWarning)
                    && _lastWarning.IndexOf(StatusCopy.PicoCommAnomalyRetry, StringComparison.Ordinal) >= 0)
                    line = line + " · " + StatusCopy.PicoCommAnomalyRetry;
                return line;
            }
        }

        public static string PollEmergencyStage()
        {
            lock (Gate)
            {
                if (_services == null || _services.Acquisition == null || !_services.Acquisition.IsOpen)
                    return "EMERGENCY_UNKNOWN";
                AcquisitionStatusSnapshot st;
                try { st = _services.Acquisition.GetStatus(); }
                catch { return "EMERGENCY_UNKNOWN"; }
                if (st == null) return "EMERGENCY_UNKNOWN";
                if (!st.EStopRawDiValid && !st.Simulation && !_services.IsFakeLive)
                    return "EMERGENCY_UNKNOWN";
                if (st.EmergencyLatched && st.EStopPressed) return "EMERGENCY_LATCHED_ACTIVE";
                if (st.EmergencyLatched && !st.EStopPressed) return "EMERGENCY_LATCHED_RELEASED";
                return st.EStopPressed ? "EMERGENCY_ACTIVE" : "EMERGENCY_RELEASED";
            }
        }

        public static string PollEmergencyDetail()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        public static void RequestAbort()
        {
            lock (Gate)
            {
                if (_services != null) _services.RequestOperatorAbort();
            }
        }

        public static Task CloseSessionAsync()
        {
            lock (Gate)
            {
                SafeDisposeServices();
                _lastWarning = string.Empty;
            }
            return Task.FromResult(0);
        }

        public static Task<string> RunRoundAsync(string settingsXml, double targetKv, string directory, CancellationToken cancellation)
        {
            return Task.Run(() => RunRound(settingsXml, targetKv, directory, cancellation), cancellation);
        }

        static string RunRound(string settingsXml, double targetKv, string directory, CancellationToken cancellation)
        {
            OwtsServices services;
            lock (Gate)
            {
                RequireOpen();
                services = _services;
            }

            if (SingletonAcquisitionSession.ProcessCleanupFailed)
                throw new XpertPressFaultException("CleanupGate", "清理失败门闩已置位，禁止再次加压。确认源关断后才能重新连接。");

            using (cancellation.Register(() =>
            {
                try { services.RequestOperatorAbort(); } catch { }
            }))
            {
                var board = BuildBoard(targetKv);
                string gate;
                if (targetKv > 0 && !board.CanEnterNonZeroPressure(out gate))
                    throw new XpertPressFaultException("NotReady", gate ?? "当前不可加压。");
                if (!(targetKv > 0))
                    _lastWarning = StatusCopy.ZeroKvNoiseOnly;

                int requestedRange = ReadInt(settingsXml, "RangeIndex", 6);
                bool autoRange = ReadBool(settingsXml, "AutoRange", true);
                string prepare;
                services.PreparePressBatch(out prepare);

                IList<FactoryAlignedPressResult> results;
                try
                {
                    results = services.RunFactoryAlignedPress(
                        targetKv * 1000.0,
                        1,
                        cancellation,
                        null,
                        autoRange ? 2 : 1,
                        requestedRange);
                }
                catch (OperationCanceledException)
                {
                    throw new XpertPressFaultException("OperatorStop", StatusCopy.OperatorAbortNoLatch);
                }

                FactoryAlignedPressResult one = results != null && results.Count > 0 ? results[0] : null;
                if (one == null)
                    throw new XpertPressFaultException("DeviceOffline", "加压没有返回结果。");
                if (one.OperatorStopped)
                    throw new XpertPressFaultException("OperatorStop", one.Message ?? StatusCopy.OperatorAbortNoLatch);
                if (one.HardSafetyAbort || IsSafety(one))
                    throw new XpertPressFaultException(SafetyKind(one), one.Message ?? StatusCopy.PressAbortHvOffPicoCapture);
                if (!one.Success)
                {
                    string message = one.Message ?? "加压失败";
                    if (message.IndexOf(StatusCopy.PressAbortDeviceOffline, StringComparison.Ordinal) >= 0
                        || message.IndexOf("整机掉线", StringComparison.Ordinal) >= 0)
                        throw new XpertPressFaultException("DeviceOffline", message);
                    if (message.IndexOf(StatusCopy.PicoCommAnomalyRetry, StringComparison.Ordinal) >= 0)
                        throw new XpertPressFaultException("PicoOnly", message);
                    throw new XpertPressFaultException("Safety", message);
                }

                string warning = string.Empty;
                if (one.Cycle != null && !string.IsNullOrEmpty(one.Cycle.ErrorMessage)
                    && one.Cycle.ErrorMessage.IndexOf(StatusCopy.PicoCommAnomalyRetry, StringComparison.Ordinal) >= 0)
                    warning = StatusCopy.PicoCommAnomalyRetry;
                lock (Gate) _lastWarning = warning;

                WriteWorkbenchCapture(settingsXml, directory, one, targetKv, warning);
                return directory;
            }
        }

        static bool IsSafety(FactoryAlignedPressResult one)
        {
            if (one == null || one.Cycle == null) return false;
            switch (one.Cycle.ExitReason)
            {
                case AcquisitionExitReason.EmergencyStop:
                case AcquisitionExitReason.HardOverCurrent:
                case AcquisitionExitReason.OverVoltage:
                    return true;
                default:
                    return false;
            }
        }

        static string SafetyKind(FactoryAlignedPressResult one)
        {
            if (one != null && one.Cycle != null && one.Cycle.ExitReason == AcquisitionExitReason.EmergencyStop)
                return "EStop";
            return "Safety";
        }

        static OperationalStatusBoard BuildBoard(double targetKv)
        {
            var board = new OperationalStatusBoard { TargetVoltageKv = targetKv };
            if (_services == null || _services.Acquisition == null || !_services.Acquisition.IsOpen)
                return board;
            try
            {
                var st = _services.Acquisition.GetStatus();
                if (st == null) return board;
                board.Connected = st.IsDeviceConnected;
                board.Calibrated = st.IsCalibrated;
                board.Calibrating = st.IsCalibrating;
                board.EStopPressed = st.EStopPressed;
                board.EmergencyLatched = st.EmergencyLatched;
                board.ImpedanceBandwidthWide = st.ImpedanceBandwidthWide;
            }
            catch { }
            return board;
        }

        static void RequireOpen()
        {
            if (_services == null || _services.Acquisition == null || !_services.Acquisition.IsOpen)
                throw new XpertPressFaultException("DeviceOffline", StatusCopy.DeviceNotConnected);
        }

        static void SafeDisposeServices()
        {
            try
            {
                if (_services != null && _services.Acquisition != null && _services.Acquisition.IsOpen)
                    _services.Acquisition.SetConnectionIndicationLamp(false);
            }
            catch { }
            try { if (_services != null) _services.Dispose(); }
            catch { }
            _services = null;
        }

        static void WriteWorkbenchCapture(string settingsXml, string directory, FactoryAlignedPressResult one, double targetKv, string warning)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("缺少本轮采集目录。");
            Directory.CreateDirectory(directory);
            var cycle = one.Cycle;
            int used = one.RangeIndexUsed >= 0 ? one.RangeIndexUsed : ReadInt(settingsXml, "RangeIndex", 6);
            if (used < 0 || used > 10) used = 6;
            string settingsOut = ReplaceIntElement(settingsXml, "RangeIndex", used);
            File.WriteAllText(Path.Combine(directory, "settings.xml"), settingsOut, new UTF8Encoding(false));

            int bits = ReadInt(settingsOut, "ResolutionBits", 14);
            double durationMs = ReadDouble(settingsOut, "CaptureDurationMs", 60);
            double picoRate = ReadDouble(settingsOut, "RequestedPicoRateHz", 125000000);
            int artCount = SampleCount(80000, durationMs, 80000);
            int picoCount = SampleCount(picoRate, durationMs, 50000000);
            short[] pico = FitShort(cycle != null ? cycle.PicoRawAdc : null, picoCount);
            double[] voltage = FitDouble(cycle != null ? cycle.UsbDividerRawVolts : null, artCount);
            string artSerial = ReadString(settingsOut, "ArtSerial");
            string picoSerial = ReadString(settingsOut, "PicoSerial");
            bool real = ReadBool(settingsOut, "RealHardware", true);
            double fullScale = RangeVolts(used);
            int hardwareBits = HardwareBits(bits, used);
            bool overflow = cycle != null && cycle.PicoOverrangeOrClipped;
            int maximumAdc = cycle != null && cycle.PicoMaximumAdc > 0 && cycle.PicoMaximumAdc <= 32767
                ? cycle.PicoMaximumAdc : 32512;

            string pending = Path.Combine(directory, "capture.bin.pending");
            string finalPath = Path.Combine(directory, "capture.bin");
            using (var w = new BinaryWriter(File.Create(pending)))
            {
                w.Write("XIONGXIN-ZERO-BENCH-2");
                w.Write(!real);
                w.Write(DateTime.UtcNow.ToBinary());
                w.Write(artSerial ?? "");
                w.Write(picoSerial ?? "");
                w.Write("Xiongxin.Owts.FactoryPress/V1.1.2");
                w.Write(80000d);
                w.Write(picoRate);
                w.Write(maximumAdc);
                w.Write(fullScale);
                w.Write(hardwareBits);
                w.Write(overflow);
                w.Write(10d);
                w.Write(voltage.Length);
                WriteDoubles(w, voltage);
                w.Write(pico.Length);
                WriteShorts(w, pico);
            }
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(pending, finalPath);

            var locate = FactoryLocateExtract.Build(cycle, _services != null ? _services.PdCalibrationPcPerVolt : 2000);
            var manifest = new StringBuilder();
            manifest.Append("{\n");
            manifest.Append("  \"schema\": \"HVX.FactoryPress.Capture/1\",\n");
            manifest.Append("  \"product\": \"HV-DAC Xpert V1.1.2 + factory-press\",\n");
            manifest.Append("  \"rangeIndexUsed\": ").Append(used.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            manifest.Append("  \"rangeIndexRequested\": ").Append(ReadInt(settingsXml, "RangeIndex", used).ToString(CultureInfo.InvariantCulture)).Append(",\n");
            manifest.Append("  \"task6Suitable\": ").Append(cycle != null && cycle.Task6RangeSuitable ? "true" : "false").Append(",\n");
            manifest.Append("  \"suggestedRangeIndex\": ").Append((cycle != null ? cycle.SuggestedPicoRangeIndex : -1).ToString(CultureInfo.InvariantCulture)).Append(",\n");
            manifest.Append("  \"targetKv\": ").Append(targetKv.ToString("R", CultureInfo.InvariantCulture)).Append(",\n");
            manifest.Append("  \"locateRateHz\": ").Append(locate.EffectiveSampleRateHz.ToString("R", CultureInfo.InvariantCulture)).Append(",\n");
            manifest.Append("  \"locateStride\": ").Append(locate.Stride.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            manifest.Append("  \"picoCommWarning\": ").Append(JsonString(warning)).Append(",\n");
            manifest.Append("  \"locateDetail\": ").Append(JsonString(locate.Detail)).Append(",\n");
            manifest.Append("  \"locateSamplesPc\": [");
            double[] samples = locate.Request != null && locate.Request.LocData != null ? locate.Request.LocData : new double[0];
            for (int i = 0; i < samples.Length; i++)
            {
                if (i != 0) manifest.Append(',');
                double value = samples[i];
                if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
                manifest.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            manifest.Append("]\n}\n");
            File.WriteAllText(Path.Combine(directory, "factory-press-manifest.json"), manifest.ToString(), new UTF8Encoding(false));
        }

        static double RangeVolts(int index)
        {
            double[] ranges = { .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 20 };
            if (index < 0 || index >= ranges.Length) return 1;
            return ranges[index];
        }

        static int HardwareBits(int bits, int rangeIndex)
        {
            if (bits == 12 && rangeIndex == 0) return 11;
            if (bits >= 14) return bits - (rangeIndex == 0 ? 2 : rangeIndex == 1 ? 1 : 0);
            return bits;
        }

        static int SampleCount(double rate, double durationMs, int maximum)
        {
            double value = Math.Round(rate * durationMs / 1000.0, MidpointRounding.AwayFromZero);
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 1 || value > maximum)
                throw new InvalidOperationException("采样率 × 采集长度超出单次缓冲区上限。");
            return checked((int)value);
        }

        static short[] FitShort(short[] source, int target)
        {
            var dst = new short[target];
            if (source == null || source.Length == 0 || target <= 0) return dst;
            if (source.Length == target) return source;
            for (int i = 0; i < target; i++)
            {
                int start = (int)((long)i * source.Length / target);
                int end = (int)((long)(i + 1) * source.Length / target);
                if (end <= start) end = Math.Min(source.Length, start + 1);
                short peak = source[Math.Min(start, source.Length - 1)];
                int abs = Math.Abs((int)peak);
                for (int k = start + 1; k < end && k < source.Length; k++)
                {
                    int a = Math.Abs((int)source[k]);
                    if (a > abs) { peak = source[k]; abs = a; }
                }
                dst[i] = peak;
            }
            return dst;
        }

        static double[] FitDouble(double[] source, int target)
        {
            var dst = new double[target];
            if (source == null || source.Length == 0 || target <= 0) return dst;
            if (source.Length == target) return source;
            for (int i = 0; i < target; i++)
            {
                int start = (int)((long)i * source.Length / target);
                int end = (int)((long)(i + 1) * source.Length / target);
                if (end <= start) end = Math.Min(source.Length, start + 1);
                double peak = source[Math.Min(start, source.Length - 1)];
                double abs = Math.Abs(peak);
                for (int k = start + 1; k < end && k < source.Length; k++)
                {
                    double a = Math.Abs(source[k]);
                    if (a > abs) { peak = source[k]; abs = a; }
                }
                if (double.IsNaN(peak) || double.IsInfinity(peak)) peak = 0;
                dst[i] = peak;
            }
            return dst;
        }

        static void WriteDoubles(BinaryWriter writer, double[] values)
        {
            var bytes = new byte[values.Length * sizeof(double)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }

        static void WriteShorts(BinaryWriter writer, short[] values)
        {
            var bytes = new byte[values.Length * sizeof(short)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            writer.Write(bytes);
        }

        static string ReplaceIntElement(string xml, string name, int value)
        {
            string pattern = "(<" + name + ">)(-?\\d+)(</" + name + ">)";
            if (Regex.IsMatch(xml ?? "", pattern))
                return Regex.Replace(xml, pattern, "${1}" + value.ToString(CultureInfo.InvariantCulture) + "${3}", RegexOptions.None);
            return xml ?? "";
        }

        static int ReadInt(string xml, string name, int fallback)
        {
            var match = Regex.Match(xml ?? "", "<" + name + ">(-?\\d+)</" + name + ">");
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        static double ReadDouble(string xml, string name, double fallback)
        {
            var match = Regex.Match(xml ?? "", "<" + name + ">([^<]+)</" + name + ">");
            double value;
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : fallback;
        }

        static bool ReadBool(string xml, string name, bool fallback)
        {
            var match = Regex.Match(xml ?? "", "<" + name + ">([^<]+)</" + name + ">");
            if (!match.Success) return fallback;
            bool value;
            return bool.TryParse(match.Groups[1].Value.Trim(), out value) ? value : fallback;
        }

        static string ReadString(string xml, string name)
        {
            var match = Regex.Match(xml ?? "", "<" + name + ">([^<]*)</" + name + ">");
            return match.Success ? match.Groups[1].Value : "";
        }

        static string JsonString(string value)
        {
            if (value == null) value = "";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }

    public sealed class XpertPressFaultException : InvalidOperationException
    {
        public string Kind { get; private set; }

        public XpertPressFaultException(string kind, string message)
            : base(message)
        {
            Kind = kind ?? "Safety";
        }

        public XpertPressFaultException(string kind, string message, Exception inner)
            : base(message, inner)
        {
            Kind = kind ?? "Safety";
        }
    }
}
