using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    // This interface deliberately has no output, device reset or device deletion API.
    // Constants/signatures verified against installed ART-DAQ Include/Art_DAQ.h.
    internal interface IArtEnvironmentApi
    {
        int CreateTask(string name, out IntPtr task);
        int CreateAI(IntPtr task, string channels, int terminal, double minimum, double maximum, int units);
        int ConfigureClock(IntPtr task, double rate, int edge, int mode, int samples);
        int Start(IntPtr task);
        int Read(IntPtr task, int samples, double timeout, int layout, double[] buffer, out int read);
        int Stop(IntPtr task);
        int Clear(IntPtr task);
    }

    internal sealed class ArtEnvironmentReader
    {
        internal const int Rse = 10083, Volts = 10348, Rising = 10280, FiniteSamples = 10178, GroupByScan = 1;
        internal const int SamplesNotYetAvailable = -200284;
        internal const int SamplesRequired = 1000;
        private static int inProgress;
        // Guarded by inProgress. A failed Clear must not orphan a task and let
        // the next one-second poll reserve another AI task on the same device.
        private static IArtEnvironmentApi pendingCleanupApi;
        private static IntPtr pendingCleanupTask;
        private static bool pendingCleanupStarted;
        private readonly Func<IArtEnvironmentApi> apiFactory;
        private readonly TimeSpan timeout;
        public ArtEnvironmentReader() : this(() => new NativeArtEnvironmentApi(), TimeSpan.FromSeconds(10)) { }
        internal ArtEnvironmentReader(Func<IArtEnvironmentApi> factory, TimeSpan timeout)
        { apiFactory = factory ?? throw new ArgumentNullException("factory"); this.timeout = timeout; }

        public Task<TemperatureHumidityReading> ReadAsync(string deviceName, CancellationToken cancellationToken)
        {
            // Task.Run is intentional: never block/capture the WinForms UI context.
            return Task.Run(() => Read(deviceName, cancellationToken));
        }

        internal TemperatureHumidityReading Read(string deviceName, CancellationToken cancellationToken)
        {
            var result = new TemperatureHumidityReading();
            if (cancellationToken.IsCancellationRequested) return Fail(result, "cancelled", "Cancelled before acquisition.");
            if (String.IsNullOrWhiteSpace(deviceName) || !Regex.IsMatch(deviceName, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z"))
                return Fail(result, "device_unavailable", "A configured ART device name is required.");
            if (Interlocked.CompareExchange(ref inProgress, 1, 0) != 0)
                return Fail(result, "busy", "An environment acquisition is already active.");
            IArtEnvironmentApi api = null;
            IntPtr task = IntPtr.Zero;
            bool started = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pendingCleanupTask != IntPtr.Zero)
                {
                    string pendingDiagnostic;
                    if (!ReleaseOwnedTask(pendingCleanupApi, pendingCleanupTask, ref pendingCleanupStarted, out pendingDiagnostic))
                        return Fail(result, "cleanup_pending", pendingDiagnostic);
                    pendingCleanupApi = null;
                    pendingCleanupTask = IntPtr.Zero;
                }
                api = apiFactory();
                Check(api.CreateTask("HVX_Environment_" + Guid.NewGuid().ToString("N"), out task), "create AI task");
                if (task == IntPtr.Zero) throw new ArtEnvironmentException(-1, "empty AI task");
                Check(api.CreateAI(task, deviceName + "/ai6:7", Rse, 0, 5, Volts), "configure AI6/AI7");
                Check(api.ConfigureClock(task, 1000, Rising, FiniteSamples, SamplesRequired), "configure sample clock");
                cancellationToken.ThrowIfCancellationRequested();
                Check(api.Start(task), "start AI task");
                started = true;
                double humiditySum = 0, temperatureSum = 0;
                bool humidityValid = true, temperatureValid = true;
                var timer = Stopwatch.StartNew();
                while (result.SamplesPerChannel < SamplesRequired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double remaining = (timeout - timer.Elapsed).TotalSeconds;
                    if (remaining <= 0) return Fail(result, "timeout", "Incomplete environmental sample frame.");
                    int requested = Math.Min(100, SamplesRequired - result.SamplesPerChannel);
                    double[] values = new double[requested * 2];
                    int read;
                    int status = api.Read(task, requested, Math.Min(0.25, remaining), GroupByScan, values, out read);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (status < 0 && status != SamplesNotYetAvailable) Check(status, "read AI samples");
                    if (read < 0 || read > requested) return Fail(result, "invalid_data", "Invalid returned sample count.");
                    // Interleaved scan layout: [AI6, AI7, AI6, AI7...]. Never average
                    // unfilled array entries after a partial driver read.
                    for (int i = 0; i < read; ++i)
                    {
                        double h = values[2 * i], t = values[2 * i + 1];
                        if (!ValidVoltage(h)) humidityValid = false; else humiditySum += h;
                        if (!ValidVoltage(t)) temperatureValid = false; else temperatureSum += t;
                    }
                    result.SamplesPerChannel += read;
                    if (read == 0 && cancellationToken.WaitHandle.WaitOne(5)) cancellationToken.ThrowIfCancellationRequested();
                }
                result.CapturedUtc = DateTimeOffset.UtcNow;
                if (humidityValid)
                {
                    result.HumidityVoltage = humiditySum / SamplesRequired;
                    double humidity = 25 * result.HumidityVoltage.Value - 12.5;
                    if (humidity >= 0 && humidity <= 100) { result.HumidityPercent = humidity; result.HumiditySuccess = true; }
                }
                if (temperatureValid)
                {
                    result.TemperatureVoltage = temperatureSum / SamplesRequired;
                    double temperature = 43.75 * result.TemperatureVoltage.Value - 66.875;
                    if (temperature >= -50 && temperature <= 120) { result.TemperatureC = temperature; result.TemperatureSuccess = true; }
                }
                if (!result.HumiditySuccess || !result.TemperatureSuccess)
                    Fail(result, "invalid_data", "One or more environmental channels are invalid or out of range.");
            }
            catch (OperationCanceledException) { Fail(result, "cancelled", "Environmental acquisition cancelled."); }
            catch (DllNotFoundException) { Fail(result, "driver_missing", "ART-DAQ x64 driver is not installed."); }
            catch (BadImageFormatException) { Fail(result, "driver_missing", "ART-DAQ driver architecture is incompatible."); }
            catch (EntryPointNotFoundException) { Fail(result, "driver_missing", "ART-DAQ API is unavailable."); }
            catch (ArtEnvironmentException error)
            {
                // ART-DAQ resource reservation error (Art_DAQ.h -50103) must not be
                // fixed by stopping another module's task or resetting its device.
                Fail(result, ClassifyInputStatus(error.Code), error.Message);
            }
            catch (Exception error) { Fail(result, "device_unavailable", error.GetType().Name); }
            finally
            {
                if (api != null && task != IntPtr.Zero)
                {
                    string cleanup;
                    if (!ReleaseOwnedTask(api, task, ref started, out cleanup))
                    {
                        pendingCleanupApi = api;
                        pendingCleanupTask = task;
                        pendingCleanupStarted = started;
                    }
                    if (cleanup.Length != 0)
                    {
                        result.Diagnostic = (result.Diagnostic + " " + cleanup).Trim();
                        if (result.ErrorCode.Length == 0) result.ErrorCode = "device_unavailable";
                    }
                }
                Interlocked.Exchange(ref inProgress, 0);
            }
            return result;
        }

        // Handoff never opens a task or resets a device. The cancelled reader
        // must finish first; only its retained Stop/Clear ownership is retried.
        internal static Task ReleaseRetainedTaskAsync(CancellationToken token)
        {
            return Task.Run(() => {
                token.ThrowIfCancellationRequested();
                if(Interlocked.CompareExchange(ref inProgress,1,0)!=0)
                    throw new InvalidOperationException("环境采集尚未结束，请稍后重试。");
                try {
                    if(pendingCleanupTask==IntPtr.Zero)return;
                    string diagnostic;
                    if(!ReleaseOwnedTask(pendingCleanupApi,pendingCleanupTask,ref pendingCleanupStarted,out diagnostic))
                        throw new IOException("环境采集资源未释放，暂不能开始试验。",new IOException(diagnostic));
                    pendingCleanupTask=IntPtr.Zero;pendingCleanupApi=null;
                } finally { Interlocked.Exchange(ref inProgress,0); }
            });
        }

        private static bool ReleaseOwnedTask(IArtEnvironmentApi api, IntPtr task, ref bool started, out string diagnostic)
        {
            diagnostic = String.Empty;
            try
            {
                if (started)
                {
                    if (api.Stop(task) < 0) diagnostic = "AI task stop failed.";
                    else started = false;
                }
            }
            catch { diagnostic = "AI task stop failed."; }
            try
            {
                if (api.Clear(task) >= 0) { started = false; return true; }
            }
            catch { }
            diagnostic = (diagnostic + " AI task clear failed; acquisition blocked until this owned task is released.").Trim();
            return false;
        }

        // Private bridge failures are not ART disconnected status aliases.
        internal static string ClassifyInputStatus(int code)
        {
            if (code == -4850013) return "cleanup_pending";
            if (code == -50103 || code == -4850003 || code == -4850005) return "busy";
            if (code == -4850011) return "timeout";
            if (code == -4850001 || code == -4850002 || code == -4850006 || code == -4850007) return "driver_missing";
            return "device_unavailable";
        }
        private static bool ValidVoltage(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0 && value <= 5; }
        private static TemperatureHumidityReading Fail(TemperatureHumidityReading result, string code, string diagnostic)
        { result.ErrorCode = code; result.Diagnostic = diagnostic; return result; }
        private static void Check(int status, string stage) { if (status < 0) throw new ArtEnvironmentException(status, stage); }
        private sealed class ArtEnvironmentException : Exception
        {
            internal readonly int Code;
            internal ArtEnvironmentException(int code, string stage) : base(stage + ": ART status " + code) { Code = code; }
        }
    }

    internal sealed class NativeArtEnvironmentApi : IArtEnvironmentApi
    {
        internal NativeArtEnvironmentApi()
        {
            if (!Environment.Is64BitProcess) throw new BadImageFormatException();
        }
        // All native task calls share the same C++ ownership boundary as pressure and calibration.
        public int CreateTask(string name, out IntPtr task) { return createTask(name, out task); }
        public int CreateAI(IntPtr task, string channels, int terminal, double minimum, double maximum, int units) { return createAi(task, channels, String.Empty, terminal, minimum, maximum, units, null); }
        public int ConfigureClock(IntPtr task, double rate, int edge, int mode, int samples) { return clock(task, String.Empty, rate, edge, mode, samples); }
        public int Start(IntPtr task) { return start(task); }
        public int Read(IntPtr task, int samples, double timeout, int layout, double[] buffer, out int count) { return read(task, samples, timeout, layout, buffer, (uint)buffer.Length, out count, IntPtr.Zero); }
        public int Stop(IntPtr task) { return stop(task); }
        public int Clear(IntPtr task) { return clear(task); }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_CreateTask", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)]
        private static extern int createTask(string name, out IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_CreateAIVoltageChan", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)]
        private static extern int createAi(IntPtr task, string channels, string name, int terminal, double minimum, double maximum, int units, string scale);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_CfgSampClkTiming", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)]
        private static extern int clock(IntPtr task, string source, double rate, int edge, int mode, int samples);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_StartTask", CallingConvention=CallingConvention.StdCall)] private static extern int start(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_StopTask", CallingConvention=CallingConvention.StdCall)] private static extern int stop(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_ClearTask", CallingConvention=CallingConvention.StdCall)] private static extern int clear(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_ReadAnalogF64", CallingConvention=CallingConvention.StdCall)]
        private static extern int read(IntPtr task, int count, double timeout, int layout, [Out] double[] values, uint size, out int readCount, IntPtr reserved);
    }
}
