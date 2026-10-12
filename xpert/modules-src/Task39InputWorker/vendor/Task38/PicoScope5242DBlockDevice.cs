using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HVX.OWTS.Task38
{
    public sealed class PicoScope5242DBlockDevice : IPicoScope5242DBlockDevice, IDeviceConnectionProbe
    {
        private readonly object sync = new object();
        private readonly ManualResetEvent blockReady = new ManualResetEvent(false);
        private Task38Configuration configuration;
        private short handle;
        private bool initialized;
        private bool disposed;
        private bool armed;
        private bool captureCancelled;
        private short[] captureBuffer;
        private GCHandle captureBufferPin;
        private bool captureBufferPinned;
        private Pico5000aNative.BlockReadyCallback callback;
        private uint callbackStatus;
        private float actualSampleIntervalNanoseconds;
        private short maximumAdc;
        private int activeRangeIndex;
        private string variant = String.Empty;
        private string serial = String.Empty;

        public bool BlockCaptureArmed
        {
            get { lock (sync) return armed; }
        }

        public string CheckConnection()
        {
            lock (sync)
            {
                EnsureInitialized();
                string connectedSerial = GetUnitInfo(Pico5000aNative.BatchAndSerial);
                if (!String.Equals(serial, connectedSerial, StringComparison.Ordinal))
                    throw new InvalidOperationException("PicoScope serial identity changed.");
                return connectedSerial;
            }
        }

        public void Initialize(Task38Configuration requestedConfiguration)
        {
            if (requestedConfiguration == null) throw new ArgumentNullException("requestedConfiguration");
            lock (sync)
            {
                ThrowIfDisposed();
                if (initialized) throw new InvalidOperationException("PicoScope is already initialized.");
                configuration = requestedConfiguration.Clone();
                Pico5000aNative.EnsureLoaded(configuration.PicoSdkLibraryDirectory);
                Pico5000aNative.SuppressStartupWindow();
                if (String.IsNullOrWhiteSpace(configuration.PicoSerial))
                {
                    var check = ConnectionInspection.Inspect(new ConnectionInspectionRequest { PicoLibraryDirectory = configuration.PicoSdkLibraryDirectory }, "pico");
                    if (!check.Passed) throw new InvalidOperationException(check.Message + " " + check.Diagnostic);
                    configuration.PicoSerial = check.Serial;
                }
                try
                {
                    uint status = Pico5000aNative.OpenUnit(out handle, configuration.PicoSerial,
                        PicoResolutionInfo.DriverMode(configuration.PicoResolutionBits));
                    if (handle < 0) handle = 0;
                    if (status == Pico5000aNative.PowerSupplyConnected ||
                        status == Pico5000aNative.PowerSupplyNotConnected ||
                        status == Pico5000aNative.Usb3DeviceOnUsb2Port)
                    {
                        Check(Pico5000aNative.ChangePowerSource(handle, status), "accept PicoScope power source");
                        status = Pico5000aNative.Ok;
                    }
                    Check(status, "open PicoScope 5242D in selected resolution mode");
                    if (handle == 0) throw new InvalidOperationException("局放模块打开未返回有效句柄。");
                    Check(Pico5000aNative.SetDeviceResolution(handle,
                        PicoResolutionInfo.DriverMode(configuration.PicoResolutionBits)), "set selected resolution mode");
                    int actualResolution;
                    Check(Pico5000aNative.GetDeviceResolution(handle, out actualResolution),
                        "read PicoScope resolution");
                    if (actualResolution != PicoResolutionInfo.DriverMode(configuration.PicoResolutionBits))
                        throw new InvalidOperationException("局放模块实际分辨率模式与请求不一致。");

                    variant = GetUnitInfo(Pico5000aNative.VariantInfo);
                    serial = GetUnitInfo(Pico5000aNative.BatchAndSerial);
                    if (!String.IsNullOrWhiteSpace(configuration.PicoSerial) && serial != configuration.PicoSerial)
                        throw new InvalidOperationException("局放模块序列号与指定设备不一致，停止初始化。");
                    if (variant.IndexOf("5242D", StringComparison.OrdinalIgnoreCase) < 0 ||
                        variant.IndexOf("MSO", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException("Expected a non-MSO PicoScope 5242D, but the driver reported '" + variant + "'.");
                    initialized = true;
                    ConfigureAndMaybeArm(configuration.PicoRangeIndex, false);
                }
                catch (Exception setupFailure)
                {
                    initialized = false;
                    if (handle != 0)
                    {
                        try { Check(Pico5000aNative.CloseUnit(handle), "close PicoScope after initialization failure"); handle = 0; }
                        catch (Exception cleanupFailure) { throw new AggregateException("局放采集模块初始化与连接释放均失败，保留连接供重试释放。", setupFailure, cleanupFailure); }
                    }
                    throw;
                }
            }
        }

        public void ArmBlockCapture(int rangeIndex)
        { ConfigureAndMaybeArm(rangeIndex, true); }

        private void ConfigureAndMaybeArm(int rangeIndex, bool startCapture)
        {
            lock (sync)
            {
                EnsureInitialized();
                AbortCore();
                if (rangeIndex < 0 || rangeIndex >= configuration.PicoRangeFullScaleVolts.Length)
                    throw new ArgumentOutOfRangeException("rangeIndex");
                activeRangeIndex = rangeIndex;
                int sampleCount = configuration.PicoSampleCount;

                Check(Pico5000aNative.SetDeviceResolution(handle,
                    PicoResolutionInfo.DriverMode(configuration.PicoResolutionBits)), "set resolution before block capture");
                int readbackMode;
                Check(Pico5000aNative.GetDeviceResolution(handle, out readbackMode), "read resolution before capture");
                if (readbackMode != PicoResolutionInfo.DriverMode(configuration.PicoResolutionBits))
                    throw new InvalidOperationException("局放模块分辨率回读不匹配，未启动采集。");
                Check(Pico5000aNative.SetChannel(handle, Pico5000aNative.ChannelA, 1,
                    Pico5000aNative.DcCoupling, rangeIndex, 0.0f), "enable PicoScope channel A");
                Check(Pico5000aNative.SetChannel(handle, Pico5000aNative.ChannelB, 0,
                    Pico5000aNative.DcCoupling, rangeIndex, 0.0f), "disable PicoScope channel B");

                int maximumSamples;
                Check(Pico5000aNative.MemorySegments(handle, 1, out maximumSamples),
                    "configure one PicoScope memory segment");
                if (maximumSamples < sampleCount)
                    throw new InvalidOperationException("PicoScope memory is too small for " + sampleCount + " samples in the selected mode.");

                int timebaseMaximumSamples;
                Check(Pico5000aNative.GetTimebase2(handle, configuration.PicoTimebase,
                    sampleCount, out actualSampleIntervalNanoseconds,
                    out timebaseMaximumSamples, 0), "validate PicoScope timebase 3");
                if (timebaseMaximumSamples < sampleCount)
                    throw new InvalidOperationException("PicoScope timebase cannot provide the requested 60 ms block.");
                if (!(actualSampleIntervalNanoseconds > 0) || Single.IsInfinity(actualSampleIntervalNanoseconds) ||
                    Math.Abs(actualSampleIntervalNanoseconds - configuration.PicoExpectedSampleIntervalNanoseconds) > 0.05)
                    throw new InvalidOperationException("PicoScope timebase 3 returned " +
                        actualSampleIntervalNanoseconds + " ns instead of the expected 8 ns.");

                Check(Pico5000aNative.MaximumValue(handle, out maximumAdc),
                    "read PicoScope maximum ADC value");
                if (maximumAdc <= 0) throw new InvalidOperationException("PicoScope returned an invalid maximum ADC value.");
                // SDK ps5000aApi.h: EXT input has its own +/-32767 scale,
                // independent of the channel ADC resolution/MaximumValue.
                double threshold = configuration.PicoChannelATriggerThresholdV;
                if (configuration.PicoTriggerOnChannelA && (Double.IsNaN(threshold) || Double.IsInfinity(threshold) || Math.Abs(threshold) >= configuration.PicoRangeFullScaleVolts[rangeIndex]))
                    throw new InvalidOperationException("局放 A 触发阈值超出本轮量程，请调整阈值或量程后重新应用设置。");
                int triggerCounts = configuration.PicoTriggerOnChannelA
                    ? (int)Math.Round(threshold / configuration.PicoRangeFullScaleVolts[rangeIndex] * maximumAdc)
                    : ExternalTriggerCounts(configuration.PicoExternalTriggerThresholdV);
                Check(Pico5000aNative.SetSimpleTrigger(handle, 1,
                    configuration.PicoTriggerOnChannelA ? Pico5000aNative.ChannelA : Pico5000aNative.ExternalTrigger, (short)triggerCounts,
                    configuration.PicoTriggerFalling ? 3 : Pico5000aNative.RisingDirection, 0, 0),
                    "configure PicoScope selected edge trigger");

                // Connection verifies the same settings, but must not issue RunBlock.
                if (!startCapture) return;

                captureBuffer = new short[sampleCount];
                captureBufferPin = GCHandle.Alloc(captureBuffer, GCHandleType.Pinned);
                captureBufferPinned = true;
                try
                {
                    Check(Pico5000aNative.SetDataBuffer(handle, Pico5000aNative.ChannelA,
                        captureBufferPin.AddrOfPinnedObject(), sampleCount, 0,
                        Pico5000aNative.RatioModeNone), "register PicoScope channel A buffer");
                    blockReady.Reset();
                    captureCancelled = false;
                    callbackStatus = UInt32.MaxValue;
                    callback = OnBlockReady;
                    int timeIndisposedMilliseconds;
                    Check(Pico5000aNative.RunBlock(handle, 0, sampleCount,
                        configuration.PicoTimebase, out timeIndisposedMilliseconds,
                        0, callback, IntPtr.Zero), "arm PicoScope block capture");
                    armed = true;
                }
                catch (Exception setupFailure)
                {
                    // Treat failed arming as potentially active until Stop/Close succeeds.
                    // Otherwise a later AbortCore could free a still-owned native buffer.
                    armed = true;
                    try { Check(Pico5000aNative.Stop(handle), "stop PicoScope after arming failure"); }
                    catch (Exception cleanupFailure) { throw new AggregateException("局放采集模块启动与停止均失败，保留缓存及连接供安全清理。", setupFailure, cleanupFailure); }
                    armed = false;
                    callback = null;
                    ReleasePinnedBuffer();
                    captureBuffer = null;
                    throw;
                }
            }
        }

        public PicoBlockCapture ReadBlockCapture(int timeoutMilliseconds)
        {
            EnsureInitializedOutsideLock();
            if (!blockReady.WaitOne(Math.Max(1, timeoutMilliseconds)))
            {
                lock (sync) AbortCore();
                throw new TimeoutException("PicoScope block capture timed out while waiting for the external trigger or completion.");
            }

            lock (sync)
            {
                EnsureInitialized();
                if (captureCancelled) throw new OperationCanceledException("PicoScope block capture was cancelled.");
                if (!armed) throw new InvalidOperationException("PicoScope block capture is not armed.");
                try
                {
                    Check(callbackStatus, "complete PicoScope block capture");
                    uint samples = (uint)captureBuffer.Length;
                    short overflow;
                    Check(Pico5000aNative.GetValues(handle, 0, ref samples, 1,
                        Pico5000aNative.RatioModeNone, 0, out overflow),
                        "read PicoScope channel A values");
                    long triggerTimeOffset;
                    int triggerTimeUnits;
                    Check(Pico5000aNative.GetTriggerTimeOffset64(handle,
                        out triggerTimeOffset, out triggerTimeUnits, 0),
                        "read PicoScope trigger time offset");

                    Check(Pico5000aNative.Stop(handle), "stop PicoScope after block capture");
                    armed = false;
                    callback = null;
                    short[] resultBuffer = captureBuffer;
                    ReleasePinnedBuffer();
                    captureBuffer = null;
                    if (samples > resultBuffer.Length) throw new InvalidOperationException("局放采集模块返回的数据点数超过缓冲区长度。");
                    if (samples < resultBuffer.Length) throw new InvalidOperationException("局放采集点数不足，不能作为完整单段数据。");
                    return new PicoBlockCapture
                    {
                        RawAdc = resultBuffer,
                        SampleCount = resultBuffer.Length,
                        ResolutionModeBits = configuration.PicoResolutionBits,
                        RangeHardwareBits = PicoResolutionInfo.HardwareBits(configuration.PicoResolutionBits, activeRangeIndex),
                        SampleIntervalNanoseconds = actualSampleIntervalNanoseconds,
                        MaximumAdc = maximumAdc,
                        FullScaleVolts = configuration.PicoRangeFullScaleVolts[activeRangeIndex],
                        HardwareOverflow = overflow != 0,
                        TriggerTimeOffset = triggerTimeOffset,
                        TriggerTimeUnitCode = triggerTimeUnits,
                        Variant = variant,
                        Serial = serial
                    };
                }
                catch
                {
                    AbortCore();
                    throw;
                }
            }
        }

        public void AbortBlockCapture()
        {
            lock (sync)
            {
                if (disposed) return;
                AbortCore();
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                captureCancelled = true;
                blockReady.Set();
                // CloseUnit is the final ownership boundary. Retain pinned buffers and
                // handle on failure so another cleanup attempt cannot lose native state.
                if (handle != 0 && armed) Pico5000aNative.Stop(handle);
                if (handle != 0)
                {
                    Check(Pico5000aNative.CloseUnit(handle), "close PicoScope unit");
                    handle = 0;
                }
                armed = false;
                callback = null;
                ReleasePinnedBuffer();
                captureBuffer = null;
                initialized = false;
                disposed = true;
                blockReady.Dispose();
            }
        }

        private void OnBlockReady(short callbackHandle, uint status, IntPtr parameter)
        {
            callbackStatus = status;
            try { blockReady.Set(); } catch (ObjectDisposedException) { }
        }

        private string GetUnitInfo(int info)
        {
            StringBuilder buffer = new StringBuilder(128);
            short required;
            Check(Pico5000aNative.GetUnitInfo(handle, buffer, (short)buffer.Capacity,
                out required, info), "read PicoScope unit information");
            return buffer.ToString();
        }

        private void AbortCore()
        {
            captureCancelled = true;
            blockReady.Set();
            if (handle != 0 && armed) Check(Pico5000aNative.Stop(handle), "abort PicoScope block capture");
            armed = false;
            callback = null;
            ReleasePinnedBuffer();
            captureBuffer = null;
            blockReady.Set();
        }

        private void ReleasePinnedBuffer()
        {
            if (!captureBufferPinned) return;
            captureBufferPin.Free();
            captureBufferPinned = false;
        }

        private void EnsureInitializedOutsideLock()
        {
            lock (sync) EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            ThrowIfDisposed();
            if (!initialized || handle == 0) throw new InvalidOperationException("PicoScope 5242D is not initialized.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("PicoScope5242DBlockDevice");
        }

        private static void Check(uint status, string operation)
        {
            if (status == Pico5000aNative.Ok) return;
            throw Pico5000aNative.CreateStatusException(status, operation, Pico5000aNative.ReadBridgeDiagnostic, false);
        }

        internal static short ExternalTriggerCounts(double volts)
        {
            if (Double.IsNaN(volts) || Double.IsInfinity(volts) || Math.Abs(volts) > 5)
                throw new ArgumentOutOfRangeException("volts", "局放同步触发阈值必须在 -5 至 5 V 内。");
            return (short)Math.Max(-32767, Math.Min(32767, Math.Round(volts / 5.0 * 32767, MidpointRounding.AwayFromZero)));
        }
    }

    internal static class Pico5000aNative
    {
        internal const uint Ok = 0x00000000;
        internal const uint PowerSupplyConnected = 0x00000119;
        internal const uint PowerSupplyNotConnected = 0x0000011A;
        internal const uint Usb3DeviceOnUsb2Port = 0x0000011E;
        internal const int Resolution14Bit = 2;
        internal const int DcCoupling = 1;
        internal const int ChannelA = 0;
        internal const int ChannelB = 1;
        internal const int ExternalTrigger = 4;
        internal const int RisingDirection = 2;
        internal const int RatioModeNone = 0;
        internal const int VariantInfo = 3;
        internal const int BatchAndSerial = 4;
        private static readonly object loadSync = new object();
        internal static string LoadedPath { get; private set; }
        internal static void SuppressStartupWindow()
        {
            // The native backend owns SDK loading and the user's verified void ApplyFix call.
            CheckHardwareBridge(HvxSuppressPicoStartupWindow(), "Prepare Pico runtime");
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void BlockReadyCallback(short handle, uint status, IntPtr parameter);

        internal static void EnsureLoaded(string libraryDirectory)
        {
            lock (loadSync)
            {
                string path = PicoRuntimeLocator.Resolve(libraryDirectory);
                if (LoadedPath != null)
                {
                    if (!String.Equals(path, LoadedPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("局放运行库已加载，切换版本前须退出并重新启动程序。");
                    return;
                }
                CheckHardwareBridge(HvxConfigurePicoPathW(path), "Configure Pico runtime");
                LoadedPath = path;
            }
        }

        // BEGIN PURE PICO STATUS CLASSIFICATION: no SDK calls; diagnostic delegate runs synchronously.
        internal static bool IsBridgeStatus(uint status)
        { return (status & 0xFFFF0000u) == 0xE4850000u; }

        internal static InvalidOperationException CreateStatusException(uint status, string operation,
            Func<string> diagnosticReader, bool chinese)
        {
            bool bridge = IsBridgeStatus(status);
            uint kind = bridge ? status & 0xFFFFu : 0;
            string message = chinese ? "采集未完成，请检查设备后重试。" : "Acquisition did not complete. Check the device, then retry.";
            if (bridge)
            {
                if (kind == 3 || kind == 5)
                    message = chinese ? "采集设备正被使用，请先结束其他操作。" : "The acquisition device is in use. Finish the other operation first.";
                else if (kind == 1 || kind == 2 || kind == 6)
                    message = chinese ? "采集组件不可用，请检查软件安装和设置。" : "The acquisition component is unavailable. Check the installation and settings.";
                else if (kind == 7)
                    message = chinese ? "采集组件设置已改变，请关闭程序后重试。" : "The acquisition component settings changed. Close the application, then retry.";
                else if (kind == 4 || kind == 12)
                    message = chinese ? "采集会话不可用，请结束当前操作后重新连接。" : "The acquisition session is unavailable. Finish the current operation, then reconnect.";
                else
                    message = chinese ? "采集未完成，请停止试验并检查设备。" : "Acquisition did not complete. Stop the test and check the device.";
            }
            string detail = String.Empty;
            if (bridge && diagnosticReader != null)
            {
                try { detail = diagnosticReader() ?? String.Empty; }
                catch (Exception error) { detail = "Bridge diagnostic unavailable: " + error; }
            }
            string domain = bridge ? "HVX_BRIDGE_STATUS" : "PICO_STATUS";
            var technical = new InvalidOperationException(operation + " failed; " + domain + "=0x" +
                status.ToString("X8") + (detail.Length == 0 ? String.Empty : "; " + detail));
            var failure = new InvalidOperationException(message, technical);
            failure.Data["StatusDomain"] = bridge ? "HvxBridge" : "PicoSdk";
            failure.Data["RawStatus"] = status; // Keep uint32 intact, including vendor codes above Int32.MaxValue.
            failure.Data["Operation"] = operation ?? String.Empty;
            if (bridge) failure.Data["BridgeKind"] = kind;
            return failure;
        }
        // END PURE PICO STATUS CLASSIFICATION

        internal static string ReadBridgeDiagnostic()
        {
            var detail = new byte[4096];
            int copied = HvxGetLastErrorInfo(detail, (uint)detail.Length);
            if (copied != 0) return "Native backend diagnostic could not be read (bridge " + copied + ").";
            int end = Array.IndexOf(detail, (byte)0);
            return Encoding.UTF8.GetString(detail, 0, end < 0 ? detail.Length : end);
        }
        private static void CheckHardwareBridge(int status, string operation)
        {
            if (status == 0) return;
            var detail = new byte[4096];
            int copied = HvxGetLastErrorInfo(detail, (uint)detail.Length);
            int end = Array.IndexOf(detail, (byte)0);
            string message = copied == 0 ? Encoding.UTF8.GetString(detail, 0, end < 0 ? detail.Length : end) : "Native backend diagnostic unavailable.";
            throw new InvalidOperationException(operation + " failed; hardware backend status=" + status + ": " + message);
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int HvxConfigurePicoPathW(string path);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int HvxSuppressPicoStartupWindow();
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int HvxGetLastErrorInfo([Out] byte[] buffer, uint capacity);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aOpenUnit", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern uint OpenUnit(out short handle, string serial, int resolution);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aCloseUnit", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint CloseUnit(short handle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aChangePowerSource", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ChangePowerSource(short handle, uint powerState);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetUnitInfo", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern uint GetUnitInfo(short handle, StringBuilder value,
            short stringLength, out short requiredSize, int info);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetDeviceResolution", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetDeviceResolution(short handle, int resolution);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetDeviceResolution", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetDeviceResolution(short handle, out int resolution);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetChannel", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetChannel(short handle, int channel, short enabled,
            int coupling, int range, float analogOffset);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aMemorySegments", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint MemorySegments(short handle, uint segments, out int maximumSamples);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetTimebase2", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetTimebase2(short handle, uint timebase,
            int samples, out float timeIntervalNanoseconds, out int maximumSamples, uint segmentIndex);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aMaximumValue", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint MaximumValue(short handle, out short value);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetSimpleTrigger", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetSimpleTrigger(short handle, short enable,
            int source, short threshold, int direction, uint delay, short autoTriggerMilliseconds);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetDataBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetDataBuffer(short handle, int source,
            IntPtr buffer, int bufferLength, uint segmentIndex, int ratioMode);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aRunBlock", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint RunBlock(short handle, int preTriggerSamples,
            int postTriggerSamples, uint timebase, out int timeIndisposedMilliseconds,
            uint segmentIndex, BlockReadyCallback callback, IntPtr parameter);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetValues", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetValues(short handle, uint startIndex,
            ref uint samples, uint downSampleRatio, int downSampleRatioMode,
            uint segmentIndex, out short overflow);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetTriggerTimeOffset64", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetTriggerTimeOffset64(short handle,
            out long time, out int timeUnits, uint segmentIndex);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aStop", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Stop(short handle);
    }
}
