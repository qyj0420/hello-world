using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.HardwareLinkStress.Adapters
{
    // One exact, non-MSO 5242D per endpoint. The host MUST own an exclusive
    // lease before constructing this endpoint. No signal generator, external
    // trigger output, AO/DO, reset, or SX binding API is called here.
    public sealed class PicoInputOnlyStressEndpoint : ITransferEndpoint
    {
        private const int ApplicationMaximumSamples = 7500000;
        private const double ApplicationSampleRateHz = 125000000.0;
        private const uint Ok = 0;
        private readonly object gate = new object();
        private readonly string dllPath;
        private readonly string ippPath;
        private readonly string expectedSerial;
        private readonly int maxSamples;
        private readonly int rangeIndex;
        private TransferPlan plan;
        private short handle;
        private bool initialized;
        private bool disposed;
        private bool poisoned;
        private string observedSerial = String.Empty;
        private CaptureState activeCapture;
        private short[] reusableBuffer;
        private GCHandle reusableBufferPin;
        private bool reusableBufferPinned;
        private double actualCaptureDurationMs;
        public Action<string> CaptureScheduling { get; set; }

        private sealed class CaptureState
        {
            internal readonly ManualResetEvent Completed = new ManualResetEvent(false);
            internal short[] Buffer;
            internal PicoNative.BlockReadyCallback Callback;
            internal uint CallbackStatus = UInt32.MaxValue;
            internal bool Cancelled;
            internal bool WaiterExpected;
            internal bool WaiterFinished;
            internal HVX.Acquisition.PicoCaptureCadence.Lease Cadence;

            internal void Release()
            {
                Buffer = null;
                Callback = null;
            }
        }

        public PicoInputOnlyStressEndpoint(string ps5000aDllPath, string exactSerial,
            int maximumSamplesPerCycle, int inputRangeIndex)
        {
            dllPath = NativeLibraryGuard.RequireX64File(ps5000aDllPath, "ps5000a.dll");
            ippPath = NativeLibraryGuard.RequireX64File(
                Path.Combine(Path.GetDirectoryName(dllPath), "picoipp.dll"), "picoipp.dll");
            if (String.IsNullOrWhiteSpace(exactSerial) || exactSerial.Length > 128)
                throw new ArgumentException("An exact Pico 5242D serial is required.", "exactSerial");
            if (maximumSamplesPerCycle < 1 || maximumSamplesPerCycle > ApplicationMaximumSamples)
                throw new ArgumentOutOfRangeException("maximumSamplesPerCycle",
                    "Pico diagnostic samples must not exceed the existing application segment.");
            if (inputRangeIndex < 0 || inputRangeIndex > 10)
                throw new ArgumentOutOfRangeException("inputRangeIndex");
            this.expectedSerial = exactSerial.Trim();
            maxSamples = maximumSamplesPerCycle;
            rangeIndex = inputRangeIndex;
        }

        public string ObservedSerial { get { lock (gate) return observedSerial; } }

        public Task InitializeAsync(TransferPlan requestedPlan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() => InitializeCore(requestedPlan, cancellationToken), cancellationToken);
        }

        private void InitializeCore(TransferPlan requestedPlan, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedPlan == null) throw new ArgumentNullException("requestedPlan");
            requestedPlan.Validate();
            if (requestedPlan.PicoBytesPerSample != sizeof(short) ||
                requestedPlan.PicoSamplesPerCycle < 1 || requestedPlan.PicoSamplesPerCycle > maxSamples)
                throw new InvalidOperationException("Pico plan is outside the one-channel input-only diagnostic envelope.");
            double expectedDuration = requestedPlan.PicoSamplesPerCycle / ApplicationSampleRateHz * 1000.0;
            HVX.Acquisition.PicoCaptureCadence.RequireDuration(expectedDuration);
            if (Math.Abs(expectedDuration - requestedPlan.RequestedDurationMilliseconds) > 0.01)
                throw new InvalidOperationException("Pico plan duration does not match the verified 125 MS/s timebase.");
            lock (gate)
            {
                EnsureAvailable();
                if (initialized) throw new InvalidOperationException("Pico diagnostic endpoint is already initialized.");
                NativeLibraryGuard.LoadExact(ippPath);
                NativeLibraryGuard.LoadExact(dllPath);
                try
                {
                    uint status = PicoNative.OpenUnit(out handle, expectedSerial, 2); // 14-bit mode.
                    if (status != Ok)
                        throw new InvalidOperationException("Pico OpenUnit refused the exact selected card (PICO_STATUS 0x" +
                            status.ToString("X8") + "). No power-source mode is changed automatically.");
                    if (handle <= 0) throw new InvalidOperationException("Pico OpenUnit returned no valid handle.");
                    string variant = GetUnitInfo(3);
                    string serial = GetUnitInfo(4);
                    if (variant.IndexOf("5242D", StringComparison.OrdinalIgnoreCase) < 0 ||
                        variant.IndexOf("MSO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        !String.Equals(serial, expectedSerial, StringComparison.Ordinal))
                        throw new InvalidOperationException("Pico model or serial does not match the exact selected non-MSO 5242D.");
                    Check(PicoNative.SetDeviceResolution(handle, 2), "set Pico 14-bit acquisition resolution");
                    int actualResolution;
                    Check(PicoNative.GetDeviceResolution(handle, out actualResolution), "read Pico resolution");
                    if (actualResolution != 2) throw new InvalidOperationException("Pico did not accept 14-bit mode.");
                    Check(PicoNative.SetChannel(handle, 0, 1, 1, rangeIndex, 0.0f),
                        "enable Pico input channel A");
                    Check(PicoNative.SetChannel(handle, 1, 0, 1, rangeIndex, 0.0f),
                        "disable unused Pico input channel B");
                    int maximumSamples;
                    Check(PicoNative.MemorySegments(handle, 1, out maximumSamples),
                        "verify Pico one-segment capacity");
                    if (maximumSamples < requestedPlan.PicoSamplesPerCycle)
                        throw new InvalidOperationException("Pico memory cannot hold the selected diagnostic block.");
                    float intervalNanoseconds;
                    int timebaseMaximumSamples;
                    Check(PicoNative.GetTimebase2(handle, 3, requestedPlan.PicoSamplesPerCycle,
                        out intervalNanoseconds, out timebaseMaximumSamples, 0),
                        "verify Pico timebase 3");
                    if (timebaseMaximumSamples < requestedPlan.PicoSamplesPerCycle ||
                        !(intervalNanoseconds > 0) || Single.IsInfinity(intervalNanoseconds) ||
                        Math.Abs(intervalNanoseconds - 8.0f) > 0.05f)
                        throw new InvalidOperationException("Pico did not confirm the requested 8 ns input-only block.");
                    HVX.Acquisition.PicoCaptureCadence.RequireSamples(requestedPlan.PicoSamplesPerCycle,1e9/intervalNanoseconds);
                    actualCaptureDurationMs=requestedPlan.PicoSamplesPerCycle*intervalNanoseconds/1000000.0;
                    // The official 5000 A API defines enable=0 as trigger disabled.
                    // This deliberately avoids depending on a high-voltage or ART pulse.
                    Check(PicoNative.SetSimpleTrigger(handle, 0, 0, 0, 2, 0, 0),
                        "disable Pico trigger for passive diagnostic capture");
                    // Reuse the managed array for the whole run. Allocating a 15 MB
                    // large-object-heap array every cycle would itself distort
                    // long-run latency and could mimic a link failure.
                    reusableBuffer = new short[requestedPlan.PicoSamplesPerCycle];
                    observedSerial = serial;
                    plan = requestedPlan.Copy();
                    initialized = true;
                }
                catch (Exception setupFailure)
                {
                    if (handle != 0)
                    {
                        uint closeStatus = PicoNative.CloseUnit(handle);
                        if (closeStatus == Ok) handle = 0;
                        else
                        {
                            poisoned = true;
                            throw new AggregateException("Pico setup and handle cleanup both failed.", setupFailure,
                                StatusError(closeStatus, "close Pico after failed setup"));
                        }
                    }
                    ReleaseReusableBuffer();
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async Task<TransferSample> ReadAsync(int requestedBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using(var cadence=await HVX.Acquisition.PicoCaptureCadence.Shared.EnterAsync(expectedSerial,cancellationToken,CaptureScheduling).ConfigureAwait(false))
                return await Task.Run(() => ReadCore(requestedBytes, cancellationToken,cadence), cancellationToken).ConfigureAwait(false);
        }

        private TransferSample ReadCore(int requestedBytes, CancellationToken cancellationToken,HVX.Acquisition.PicoCaptureCadence.Lease cadence)
        {
            CaptureState capture;
            lock (gate)
            {
                EnsureAvailable();
                if (!initialized || plan == null) throw new InvalidOperationException("Pico diagnostic endpoint is not initialized.");
                if (requestedBytes != plan.PicoBytesPerCycle)
                    throw new ArgumentException("Pico stress reads must use the frozen tier plan.", "requestedBytes");
                cancellationToken.ThrowIfCancellationRequested();
                if (reusableBuffer == null || reusableBuffer.Length != plan.PicoSamplesPerCycle)
                    throw new InvalidOperationException("Pico diagnostic input buffer is not valid.");
                cadence.BeginCapture();
                capture = new CaptureState { Buffer = reusableBuffer, Cadence=cadence };
                capture.Callback = (callbackHandle, status, parameter) =>
                {
                    capture.CallbackStatus = status;
                    try { capture.Completed.Set(); } catch (ObjectDisposedException) { }
                };
                activeCapture = capture;
                try
                {
                    reusableBufferPin=GCHandle.Alloc(reusableBuffer,GCHandleType.Pinned);reusableBufferPinned=true;
                    Check(PicoNative.SetDataBuffer(handle, 0, reusableBufferPin.AddrOfPinnedObject(),
                        capture.Buffer.Length, 0, 0), "register Pico input channel A buffer");
                    int timeIndisposedMilliseconds;
                    Check(PicoNative.RunBlock(handle, 0, capture.Buffer.Length, 3,
                        out timeIndisposedMilliseconds, 0, capture.Callback, IntPtr.Zero),
                        "start Pico passive input block");
                    capture.WaiterExpected = true;
                }
                catch (Exception setupFailure)
                {
                    try { StopAndReleaseCaptureLocked(capture); }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("Pico block setup and cleanup both failed.",
                            setupFailure, cleanupFailure);
                    }
                    throw;
                }
            }

            try
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(
                    Math.Min(10000.0, Math.Max(1000.0, 1000.0 + plan.RequestedDurationMilliseconds * 4.0)));
                while (!capture.Completed.WaitOne(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException("Pico passive block did not complete before the diagnostic deadline.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (disposed || capture.Cancelled || activeCapture != capture)
                        throw new OperationCanceledException("Pico diagnostic capture was stopped.");
                    Check(capture.CallbackStatus, "complete Pico passive input block");
                    uint count = (uint)capture.Buffer.Length;
                    short overflow;
                    Check(PicoNative.GetValues(handle, 0, ref count, 1, 0, 0, out overflow),
                        "read Pico input channel A values");
                    if (count > capture.Buffer.Length)
                        throw new InvalidOperationException("Pico returned an invalid sample count.");
                    int actualBytes = checked((int)count * sizeof(short));
                    if (count != capture.Buffer.Length)
                        throw new TransferReadException("Pico short read: " + count + "/" +
                            capture.Buffer.Length + " samples.", actualBytes);
                    StopAndReleaseCaptureLocked(capture);
                    return new TransferSample { BytesRead = actualBytes };
                }
            }
            catch (Exception readFailure)
            {
                lock (gate)
                {
                    if (activeCapture == capture)
                    {
                        try { StopAndReleaseCaptureLocked(capture); }
                        catch (Exception cleanupFailure)
                        {
                            throw new AggregateException("Pico passive read and cleanup both failed.",
                                readFailure, cleanupFailure);
                        }
                    }
                }
                throw;
            }
            finally
            {
                lock (gate)
                {
                    capture.WaiterFinished = true;
                    if (activeCapture != capture) capture.Completed.Dispose();
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                if (activeCapture != null) StopAndReleaseCaptureLocked(activeCapture);
                if (handle != 0)
                {
                    Check(PicoNative.CloseUnit(handle), "close Pico diagnostic device");
                    handle = 0;
                }
                ReleaseReusableBuffer();
                plan = null;
                initialized = false;
                disposed = true;
            }
        }

        private void StopAndReleaseCaptureLocked(CaptureState capture)
        {
            if (capture == null || activeCapture != capture) return;
            capture.Cancelled = true;
            capture.Completed.Set();
            uint status = PicoNative.Stop(handle);
            if (status != Ok)
            {
                poisoned = true;
                // Native may still own the pinned buffer. Retain it and the
                // callback until a later Dispose can successfully stop/close.
                throw StatusError(status, "stop Pico passive block");
            }
            activeCapture = null;
            capture.Release();
            if(reusableBufferPinned){reusableBufferPin.Free();reusableBufferPinned=false;}
            if(capture.Cadence.IsDisposed)
                HVX.Acquisition.PicoCaptureCadence.Shared.ConfirmReleasedAsync(expectedSerial,CaptureScheduling).GetAwaiter().GetResult();
            else capture.Cadence.Released(actualCaptureDurationMs);
            if (!capture.WaiterExpected || capture.WaiterFinished) capture.Completed.Dispose();
        }

        private void ReleaseReusableBuffer()
        {
            if (reusableBufferPinned)
            {
                reusableBufferPin.Free();
                reusableBufferPinned = false;
            }
            reusableBuffer = null;
        }

        private string GetUnitInfo(int selector)
        {
            var buffer = new StringBuilder(128);
            short required;
            Check(PicoNative.GetUnitInfo(handle, buffer, (short)buffer.Capacity,
                out required, selector), "read Pico identity");
            return buffer.ToString();
        }

        private void EnsureAvailable()
        {
            if (disposed) throw new ObjectDisposedException("PicoInputOnlyStressEndpoint");
            if (poisoned || activeCapture != null)
                throw new InvalidOperationException("Pico native capture has not been safely released; refuse a new block.");
        }

        private static void Check(uint status, string operation)
        {
            if (status != Ok) throw StatusError(status, operation);
        }

        private static InvalidOperationException StatusError(uint status, string operation)
        {
            return new InvalidOperationException(operation + " failed (PICO_STATUS 0x" + status.ToString("X8") + ").");
        }

        private static class PicoNative
        {
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            internal delegate void BlockReadyCallback(short handle, uint status, IntPtr parameter);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aOpenUnit", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            internal static extern uint OpenUnit(out short handle, string serial, int resolution);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aCloseUnit", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint CloseUnit(short handle);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetUnitInfo", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            internal static extern uint GetUnitInfo(short handle, StringBuilder value,
                short size, out short required, int selector);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetDeviceResolution", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint SetDeviceResolution(short handle, int resolution);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetDeviceResolution", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint GetDeviceResolution(short handle, out int resolution);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetChannel", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint SetChannel(short handle, int channel, short enabled,
                int coupling, int range, float offset);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aMemorySegments", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint MemorySegments(short handle, uint segments, out int maximumSamples);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetTimebase2", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint GetTimebase2(short handle, uint timebase, int samples,
                out float intervalNanoseconds, out int maximumSamples, uint segment);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetSimpleTrigger", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint SetSimpleTrigger(short handle, short enabled, int source,
                short threshold, int direction, uint delay, short autoTriggerMilliseconds);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetDataBuffer", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint SetDataBuffer(short handle, int channel, IntPtr buffer,
                int length, uint segment, int ratioMode);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aRunBlock", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint RunBlock(short handle, int preTriggerSamples, int postTriggerSamples,
                uint timebase, out int timeIndisposedMilliseconds, uint segment,
                BlockReadyCallback callback, IntPtr parameter);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aGetValues", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint GetValues(short handle, uint startIndex, ref uint samples,
                uint ratio, int ratioMode, uint segment, out short overflow);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aStop", CallingConvention = CallingConvention.StdCall)]
            internal static extern uint Stop(short handle);
        }
    }
}
