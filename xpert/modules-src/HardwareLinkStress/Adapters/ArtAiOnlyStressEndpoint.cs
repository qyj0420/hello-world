using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.HardwareLinkStress.Adapters
{
    // A fresh endpoint owns one ART card for one diagnostic run. It never
    // creates AO/DO tasks, writes outputs, configures a trigger, or resets a device.
    // The host MUST first obtain an exclusive lease covering all other ART users.
    public sealed class ArtAiOnlyStressEndpoint : ITransferEndpoint
    {
        private const int VerifiedApplicationMaximumSamples = 4800;
        private const double VerifiedApplicationMaximumRateHz = 80000.0;
        private readonly object gate = new object();
        private readonly string dllPath;
        private readonly string deviceName;
        private readonly uint expectedSerial;
        private readonly int maxSamples;
        private readonly double requestedRateHz;
        private TransferPlan plan;
        private IntPtr activeTask;
        private bool initialized;
        private bool disposed;
        private bool poisoned;
        private string observedSerial = String.Empty;

        public ArtAiOnlyStressEndpoint(string artDaqDllPath, string deviceName,
            string exactSerial, int maximumSamplesPerCycle, double sampleRateHz)
        {
            dllPath = NativeLibraryGuard.RequireX64File(artDaqDllPath, "Art_DAQ.dll");
            if (String.IsNullOrWhiteSpace(deviceName) ||
                !System.Text.RegularExpressions.Regex.IsMatch(deviceName, "^[A-Za-z][A-Za-z0-9_]{0,63}$"))
                throw new ArgumentException("An exact ART device name is required.", "deviceName");
            if (!UInt32.TryParse(exactSerial, out expectedSerial) || expectedSerial == 0)
                throw new ArgumentException("An exact nonzero ART serial is required.", "exactSerial");
            if (maximumSamplesPerCycle < 1 || maximumSamplesPerCycle > VerifiedApplicationMaximumSamples)
                throw new ArgumentOutOfRangeException("maximumSamplesPerCycle",
                    "ART diagnostic samples must not exceed the existing verified application segment.");
            if (Double.IsNaN(sampleRateHz) || Double.IsInfinity(sampleRateHz) ||
                sampleRateHz < 1 || sampleRateHz > VerifiedApplicationMaximumRateHz)
                throw new ArgumentOutOfRangeException("sampleRateHz");
            this.deviceName = deviceName;
            maxSamples = maximumSamplesPerCycle;
            requestedRateHz = sampleRateHz;
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
            if (requestedPlan.ArtBytesPerSample != sizeof(double) ||
                requestedPlan.ArtSamplesPerCycle < 1 || requestedPlan.ArtSamplesPerCycle > maxSamples)
                throw new InvalidOperationException("ART plan is outside the one-channel AI-only diagnostic envelope.");
            double expectedSamples = requestedRateHz * requestedPlan.RequestedDurationMilliseconds / 1000.0;
            if (Math.Abs(requestedPlan.ArtSamplesPerCycle - expectedSamples) > 1.05)
                throw new InvalidOperationException("ART plan sample count does not match the verified sample clock.");
            lock (gate)
            {
                EnsureAvailable();
                if (initialized) throw new InvalidOperationException("ART diagnostic endpoint is already initialized.");
                NativeLibraryGuard.LoadExact(dllPath);
                ConfirmSerial();
                double driverMaximum;
                Check(ArtNative.GetMaxSingleChannelRate(deviceName, 0x298C,
                    out driverMaximum, IntPtr.Zero), "query ART maximum single-channel AI rate");
                if (Double.IsNaN(driverMaximum) || Double.IsInfinity(driverMaximum) ||
                    driverMaximum < requestedRateHz)
                    throw new InvalidOperationException("ART driver does not confirm the requested single-channel AI rate.");
                plan = requestedPlan.Copy();
                initialized = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task<TransferSample> ReadAsync(int requestedBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The native ART read is synchronous. It is dispatched off the UI thread,
            // with a bounded SDK timeout; cancellation is observed before/after it.
            return Task.Run(() => ReadCore(requestedBytes, cancellationToken), cancellationToken);
        }

        private TransferSample ReadCore(int requestedBytes, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                EnsureAvailable();
                if (!initialized || plan == null) throw new InvalidOperationException("ART diagnostic endpoint is not initialized.");
                if (requestedBytes != plan.ArtBytesPerCycle)
                    throw new ArgumentException("ART stress reads must use the frozen tier plan.", "requestedBytes");
                cancellationToken.ThrowIfCancellationRequested();
                ConfirmSerial();
                double[] samples = new double[plan.ArtSamplesPerCycle];
                Exception failure = null;
                TransferSample result = null;
                try
                {
                    IntPtr created;
                    int createCode = ArtNative.CreateTask(String.Empty, out created);
                    if (created != IntPtr.Zero) activeTask = created;
                    Check(createCode, "create ART AI-only task");
                    if (activeTask == IntPtr.Zero) throw new InvalidOperationException("ART returned no AI task handle.");
                    Check(ArtNative.CreateAIVoltageChan(activeTask, deviceName + "/ai4", String.Empty,
                        -1, -10.0, 10.0, 10348, null), "create ART AI4 voltage input");
                    Check(ArtNative.ConfigureSampleClock(activeTask, String.Empty, requestedRateHz,
                        10280, 10178, samples.Length), "configure ART finite input-only clock");
                    Check(ArtNative.StartTask(activeTask), "start ART AI-only input task");
                    cancellationToken.ThrowIfCancellationRequested();
                    int actual;
                    double timeoutSeconds = Math.Min(3.0,
                        Math.Max(0.5, 0.5 + plan.RequestedDurationMilliseconds * 0.004));
                    int status = ArtNative.ReadAnalogF64(activeTask, samples.Length, timeoutSeconds,
                        0, samples, (uint)samples.Length, out actual, IntPtr.Zero);
                    if (status == -200284 || status == -26802)
                        throw new TimeoutException("ART AI-only read timed out (ART-DAQ " + status + ").");
                    Check(status, "read ART AI4 voltage input");
                    if (actual < 0 || actual > samples.Length)
                        throw new InvalidOperationException("ART returned an invalid AI sample count: " + actual + ".");
                    if (actual != samples.Length)
                        throw new TransferReadException("ART AI short read: " + actual + "/" + samples.Length +
                            " samples.", checked(actual * sizeof(double)));
                    result = new TransferSample { BytesRead = checked(actual * sizeof(double)) };
                }
                catch (Exception ex) { failure = ex; }
                try { ReleaseTask(); }
                catch (Exception cleanupFailure)
                {
                    failure = failure == null ? cleanupFailure :
                        new AggregateException("ART read and input task cleanup both failed.", failure, cleanupFailure);
                }
                if (failure != null) throw failure;
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                ReleaseTask();
                initialized = false;
                plan = null;
                disposed = true;
            }
        }

        private void ConfirmSerial()
        {
            uint current;
            Check(ArtNative.GetDeviceSerial(deviceName, 0x0632, out current, IntPtr.Zero),
                "read ART device serial");
            if (current == 0 || current != expectedSerial)
                throw new InvalidOperationException("ART device identity changed or does not match the explicitly selected card.");
            observedSerial = current.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void ReleaseTask()
        {
            if (activeTask == IntPtr.Zero) return;
            IntPtr task = activeTask;
            int stopCode = ArtNative.StopTask(task);
            int clearCode = ArtNative.ClearTask(task);
            if (clearCode >= 0) activeTask = IntPtr.Zero;
            if (stopCode < 0 || clearCode < 0) poisoned = true;
            if (stopCode < 0 || clearCode < 0)
                throw new InvalidOperationException("ART AI-only task cleanup failed: stop=" + stopCode +
                    ", clear=" + clearCode + "; no further reads will be attempted.");
        }

        private void EnsureAvailable()
        {
            if (disposed) throw new ObjectDisposedException("ArtAiOnlyStressEndpoint");
            if (poisoned || activeTask != IntPtr.Zero)
                throw new InvalidOperationException("An ART native input task remains active; refuse a new read.");
        }

        private static void Check(int status, string operation)
        {
            if (status >= 0) return;
            byte[] buffer = new byte[2048];
            try { ArtNative.GetExtendedErrorInfo(buffer, (uint)buffer.Length); } catch { }
            string message = Encoding.Default.GetString(buffer).Split('\0')[0].Trim();
            throw new InvalidOperationException(operation + " failed (ART-DAQ " + status + "): " + message);
        }

        private static class ArtNative
        {
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetDeviceAttribute", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            internal static extern int GetDeviceSerial(string name, int attribute, out uint serial, IntPtr task);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetDeviceAttribute", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            internal static extern int GetMaxSingleChannelRate(string name, int attribute, out double rate, IntPtr task);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateTask", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            internal static extern int CreateTask(string name, out IntPtr task);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateAIVoltageChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            internal static extern int CreateAIVoltageChan(IntPtr task, string channel, string name,
                int terminal, double minimum, double maximum, int units, string customScale);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CfgSampClkTiming", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
            internal static extern int ConfigureSampleClock(IntPtr task, string source, double rate,
                int edge, int sampleMode, int samplesPerChannel);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_StartTask", CallingConvention = CallingConvention.StdCall)]
            internal static extern int StartTask(IntPtr task);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadAnalogF64", CallingConvention = CallingConvention.StdCall)]
            internal static extern int ReadAnalogF64(IntPtr task, int samplesPerChannel, double timeoutSeconds,
                int fillMode, [Out] double[] samples, uint arraySize, out int samplesRead, IntPtr reserved);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_StopTask", CallingConvention = CallingConvention.StdCall)]
            internal static extern int StopTask(IntPtr task);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ClearTask", CallingConvention = CallingConvention.StdCall)]
            internal static extern int ClearTask(IntPtr task);
            [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetExtendedErrorInfo", CallingConvention = CallingConvention.StdCall)]
            internal static extern int GetExtendedErrorInfo([Out] byte[] message, uint size);
        }
    }
}
