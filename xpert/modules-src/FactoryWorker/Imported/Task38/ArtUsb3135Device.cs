using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HVX.OWTS.Task38
{
    public sealed class ArtUsb3135Device : IArtUsb3135Device, IDeviceIdentity
    {
        private const double SinglePointIoTimeoutSeconds = 0.020;
        private readonly object sync = new object();
        private readonly object safetyInputSync = new object();
        private readonly object digitalOutputSync = new object();
        private Task38Configuration configuration;
        private IntPtr digitalOutputTask;
        private IntPtr safetyInputTask;
        private IntPtr analogOutputTask;
        private IntPtr monitoringInputTask;
        private IntPtr finiteInputTask;
        private volatile bool initialized;
        private volatile bool disposing;
        private volatile bool disposed;
        private bool monitoringInputReady;
        private bool finiteInputReady;
        private bool hvOn;
        private bool hvIndicatorOn;
        private bool connectionIndicatorOn;
        private bool groundTriggerOn;
        public string DeviceSerial { get; private set; }
        public string IdentityDiagnostic { get; private set; }

        public bool FiniteCaptureArmed
        {
            get { lock (sync) return finiteInputReady && finiteInputTask != IntPtr.Zero; }
        }

        public void Initialize(Task38Configuration requestedConfiguration)
        {
            if (requestedConfiguration == null) throw new ArgumentNullException("requestedConfiguration");
            lock (sync)
            {
                ThrowIfDisposed();
                if (initialized) throw new InvalidOperationException("USB3135 is already initialized.");
                if (HasRetainedTaskHandles())
                    throw new InvalidOperationException("USB3135 still owns task handles from a failed cleanup; call Dispose again before reinitializing.");
                configuration = requestedConfiguration.Clone();
                if (configuration.ControlBoxVerification) configuration.GroundInSafeIdle = true;
                ArtDaqNative.EnsureLoaded(configuration.ArtDaqLibraryDirectory);
                DeviceSerial = String.Empty; IdentityDiagnostic = String.Empty;
                try
                {
                    uint serial;
                    Check(ArtDaqNative.GetDeviceSerial(configuration.ArtDeviceName, 0x0632, out serial, IntPtr.Zero), "read control module serial number");
                    if (serial != 0) DeviceSerial = serial.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else IdentityDiagnostic = "控制模块返回序列号 0，不能作为有效设备身份。";
                }
                catch (Exception ex) { IdentityDiagnostic = "控制模块序列号未获取：" + ex.Message; }
                try
                {
                    lock (digitalOutputSync)
                    {
                        CreateDigitalOutputTask();
                        hvOn = false;
                        hvIndicatorOn = false;
                        connectionIndicatorOn = false;
                        groundTriggerOn = configuration.GroundInSafeIdle;
                        WriteDigitalOutputsCore();
                    }

                    CreateAnalogOutputTask();
                    WriteAoCore(0.0);
                    lock (safetyInputSync) CreateSafetyInputTask();
                    CreateMonitoringInputTask();

                    lock (digitalOutputSync)
                    {
                        connectionIndicatorOn = true;
                        groundTriggerOn = configuration.GroundInSafeIdle;
                        WriteDigitalOutputsCore();
                    }
                    initialized = true;
                    // Validate finite channels, clock and trigger now, without starting AI.
                    ConfigureFiniteDividerCapture(false);
                    Check(ArtDaqNative.ClearTask(finiteInputTask), "clear verified finite AI task");
                    finiteInputTask = IntPtr.Zero;
                    CreateMonitoringInputTask();
                }
                catch (Exception setupFailure)
                {
                    initialized = false;
                    try { SafeClearAllTasks(); }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("USB3135 initialization and task cleanup both failed.",
                            setupFailure, cleanupFailure);
                    }
                    throw;
                }
            }
        }

        public bool ReadSafetyReady()
        {
            lock (safetyInputSync)
            {
                EnsureInitialized();
                if (safetyInputTask == IntPtr.Zero)
                    throw new InvalidOperationException("The P0.4 safety input task is not active.");
                byte[] value = new byte[1];
                int read;
                int bytesPerSample;
                Check(ArtDaqNative.ReadDigitalLines(safetyInputTask, 1, SinglePointIoTimeoutSeconds,
                    ArtDaqNative.GroupByChannel, value, (uint)value.Length,
                    out read, out bytesPerSample, IntPtr.Zero), "read P0.4 safety input");
                if (read != 1 || bytesPerSample < 1)
                    throw new InvalidOperationException("ART-DAQ returned no safety input sample.");
                bool physicalHigh = value[0] != 0;
                return configuration.SafetyReadyActiveHigh ? physicalHigh : !physicalHigh;
            }
        }

        public FeedbackSample ReadFeedback()
        {
            lock (sync)
            {
                EnsureInitialized();
                if (!monitoringInputReady || monitoringInputTask == IntPtr.Zero)
                    throw new InvalidOperationException("The monitoring AI task is not active.");
                double[] raw = new double[3];
                int read;
                Check(ArtDaqNative.ReadAnalogF64(monitoringInputTask, 1, SinglePointIoTimeoutSeconds,
                    ArtDaqNative.GroupByChannel, raw, (uint)raw.Length,
                    out read, IntPtr.Zero), "read AI0/AI2/AI4 feedback");
                if (read != 1) throw new InvalidOperationException("ART-DAQ returned no feedback sample.");
                return new FeedbackSample
                {
                    TimestampUtc = DateTime.UtcNow,
                    CurrentFeedbackRawV = raw[0],
                    SourceVoltageFeedbackRawV = raw[1],
                    DividerFeedbackRawV = raw[2],
                    CurrentA = raw[0] / 5.0 * configuration.SourceCurrentFeedbackFullScaleA,
                    SourceVoltageV = raw[1] / 5.0 * configuration.SourceVoltageFeedbackFullScaleV,
                    DividerVoltageV = raw[2] * configuration.DividerRatioVPerV
                };
            }
        }

        public void WriteAoVolts(double volts)
        {
            lock (sync)
            {
                EnsureInitialized();
                if (configuration.ControlBoxVerification && volts != 0)
                    throw new InvalidOperationException("控制箱校验禁止非零AO输出。");
                if (Double.IsNaN(volts) || Double.IsInfinity(volts) || volts < 0.0 || volts > configuration.AoMaximumVolts + 1e-12)
                    throw new ArgumentOutOfRangeException("volts", "AO command is outside the configured 0-5 V safety envelope.");
                WriteAoCore(Math.Max(0.0, Math.Min(configuration.AoMaximumVolts, volts)));
            }
        }

        public void SetDigitalOutputs(bool requestedHvOn, bool requestedHvIndicatorOn,
            bool requestedConnectionIndicatorOn, bool requestedGroundTriggerOn)
        {
            lock (digitalOutputSync)
            {
                EnsureInitialized();
                if (configuration.ControlBoxVerification && (requestedHvOn || requestedHvIndicatorOn || !requestedGroundTriggerOn))
                    throw new InvalidOperationException("控制箱校验禁止高压使能及释放接地输出。");
                if (requestedHvOn && requestedGroundTriggerOn)
                    throw new InvalidOperationException("HV ON and grounding output cannot be active together.");
                hvOn = requestedHvOn;
                hvIndicatorOn = requestedHvIndicatorOn;
                connectionIndicatorOn = requestedConnectionIndicatorOn;
                groundTriggerOn = requestedGroundTriggerOn;
                WriteDigitalOutputsCore();
            }
        }

        public void ForceSourceOff()
        {
            if (!initialized || disposed || disposing) return;
            Exception first = null;
            lock (digitalOutputSync)
            {
                if (!initialized || disposed || disposing) return;
                hvOn = false;
                hvIndicatorOn = false;
                try { WriteDigitalOutputsCore(); }
                catch (Exception ex) { if (first == null) first = ex; }
            }
            lock (sync)
            {
                if (!initialized || disposed || disposing)
                {
                    if (first != null) throw new InvalidOperationException("Failed to force the source OFF.", first);
                    return;
                }
                try { WriteAoCore(0.0); }
                catch (Exception ex) { if (first == null) first = ex; }
            }
            if (first != null) throw new InvalidOperationException("Failed to force the source OFF.", first);
        }

        public void StopMonitoringInputs()
        {
            lock (sync)
            {
                EnsureInitialized();
                monitoringInputReady = false;
                ClearTask(ref monitoringInputTask, "clear monitoring AI task");
            }
        }

        public void RestartMonitoringInputs()
        {
            lock (sync)
            {
                EnsureInitialized();
                if (finiteInputTask != IntPtr.Zero)
                    throw new InvalidOperationException("Cannot restart monitoring while finite AI owns the USB3135 AI subsystem.");
                if (monitoringInputTask != IntPtr.Zero && !monitoringInputReady)
                    ClearTask(ref monitoringInputTask, "retry cleanup of incomplete monitoring AI task");
                if (monitoringInputTask == IntPtr.Zero) CreateMonitoringInputTask();
            }
        }

        public void ArmFiniteDividerCapture()
        { ConfigureFiniteDividerCapture(true); }

        private void ConfigureFiniteDividerCapture(bool startCapture)
        {
            lock (sync)
            {
                EnsureInitialized();
                if (finiteInputTask != IntPtr.Zero) throw new InvalidOperationException("Finite AI capture is already armed.");
                monitoringInputReady = false;
                ClearTask(ref monitoringInputTask, "switch monitoring AI to finite capture");
                finiteInputReady = false;
                try
                {
                    IntPtr createdTask;
                    int createCode = ArtDaqNative.CreateTask(String.Empty, out createdTask);
                    if (createdTask != IntPtr.Zero) finiteInputTask = createdTask;
                    Check(createCode, "create finite divider AI task");
                    Check(ArtDaqNative.CreateAIVoltageChan(finiteInputTask,
                        Physical(configuration.ArtDividerAiChannel), String.Empty,
                        ArtDaqNative.DefaultTerminalConfiguration,
                        configuration.ArtMonitorInputMinimumV,
                        configuration.ArtMonitorInputMaximumV,
                        ArtDaqNative.Volts, null), "create finite AI4 channel");
                    Check(ArtDaqNative.ConfigureSampleClock(finiteInputTask, String.Empty,
                        configuration.ArtFiniteSampleRateHz, ArtDaqNative.Rising,
                        ArtDaqNative.FiniteSamples, configuration.ArtFiniteSampleCount),
                        "configure 80 kS/s finite AI4 capture");
                    int triggerEdge = configuration.ArtTriggerFalling ? ArtDaqNative.Falling : ArtDaqNative.Rising;
                    if (configuration.ArtUseAnalogTrigger)
                        Check(ArtDaqNative.ConfigureAnalogStartTrigger(finiteInputTask,
                            Physical(configuration.ArtAnalogTriggerChannel), triggerEdge, configuration.ArtAnalogTriggerThresholdV),
                            "configure finite AI analog start trigger");
                    else
                        Check(ArtDaqNative.ConfigureDigitalStartTrigger(finiteInputTask,
                            configuration.ArtFiniteTriggerTerminal, triggerEdge), "configure finite AI PFI start trigger");
                    Check(ArtDaqNative.TaskControl(finiteInputTask, 2), "verify finite divider channels, clock and trigger");
                    if (startCapture)
                    {
                        Check(ArtDaqNative.StartTask(finiteInputTask), "arm finite AI4 capture");
                        finiteInputReady = true;
                    }
                }
                catch (Exception setupFailure)
                {
                    finiteInputReady = false;
                    try { ClearTask(ref finiteInputTask, "cleanup incomplete finite AI4 task"); }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException("Finite AI setup and cleanup both failed; any uncleared task handle was retained for retry.",
                            setupFailure, cleanupFailure);
                    }
                    throw;
                }
            }
        }

        public double[] ReadFiniteDividerCapture(int timeoutMilliseconds)
        {
            lock (sync)
            {
                EnsureInitialized();
                if (!finiteInputReady || finiteInputTask == IntPtr.Zero)
                    throw new InvalidOperationException("Finite AI capture is not armed.");
                double[] data = new double[configuration.ArtFiniteSampleCount];
                int read;
                try
                {
                    int readCode = ArtDaqNative.ReadAnalogF64(finiteInputTask,
                        configuration.ArtFiniteSampleCount,
                        Math.Max(0.001, timeoutMilliseconds / 1000.0),
                        ArtDaqNative.GroupByChannel, data, (uint)data.Length,
                        out read, IntPtr.Zero);
                    // Art_DAQ.h: SamplesNotYetAvailable=-200284, Timeout=-26802.
                    if (readCode == -200284 || readCode == -26802)
                        throw new TimeoutException("振荡电压采集模块：触发等待超时，未获得完整单段数据；请检查触发接线、边沿和电平。诊断码 " + readCode);
                    Check(readCode, "read finite AI4 capture");
                    if (read < 0 || read > data.Length)
                        throw new InvalidOperationException("ART-DAQ returned an invalid finite sample count.");
                    if (read != data.Length) Array.Resize(ref data, read);
                    return data;
                }
                finally
                {
                    finiteInputReady = false;
                    ClearTask(ref finiteInputTask, "clear completed finite AI4 task");
                }
            }
        }

        public void AbortFiniteDividerCapture()
        {
            lock (sync)
            {
                if (disposed || disposing) return;
                finiteInputReady = false;
                ClearTask(ref finiteInputTask, "abort finite AI4 task");
            }
        }

        public void Dispose()
        {
            bool wasInitialized;
            lock (sync)
            {
                while (disposing && !disposed) System.Threading.Monitor.Wait(sync);
                if (disposed) return;
                disposing = true;
                wasInitialized = initialized;
            }

            Exception shutdownFailure = null;
            if (wasInitialized)
            {
                lock (digitalOutputSync)
                {
                    hvOn = false;
                    hvIndicatorOn = false;
                    connectionIndicatorOn = false;
                    groundTriggerOn = configuration != null && configuration.GroundInSafeIdle;
                    try { WriteDigitalOutputsCore(); }
                    catch (Exception ex) { shutdownFailure = ex; }
                }
                lock (sync)
                {
                    try { WriteAoCore(0.0); }
                    catch (Exception ex) { if (shutdownFailure == null) shutdownFailure = ex; }
                }
            }

            Exception cleanupFailure = null;
            try { SafeClearAllTasks(); }
            catch (Exception ex) { cleanupFailure = ex; }
            lock (sync)
            {
                initialized = false;
                disposing = false;
                disposed = cleanupFailure == null;
                System.Threading.Monitor.PulseAll(sync);
            }
            if (cleanupFailure != null && shutdownFailure != null)
                throw new AggregateException("USB3135 shutdown and task cleanup both failed.", shutdownFailure, cleanupFailure);
            if (cleanupFailure != null)
                throw new InvalidOperationException("USB3135 task cleanup failed; retained handles can be retried by calling Dispose again.", cleanupFailure);
            if (shutdownFailure != null)
                throw new InvalidOperationException("USB3135 outputs could not be driven to their safe state during disposal.", shutdownFailure);
        }

        private void CreateDigitalOutputTask()
        {
            Check(ArtDaqNative.CreateTask(String.Empty, out digitalOutputTask), "create P0 digital output task");
            string lines = String.Join(",", new string[]
            {
                Physical(configuration.ArtHvOnDoLine),
                Physical(configuration.ArtHvIndicatorDoLine),
                Physical(configuration.ArtConnectionIndicatorDoLine),
                Physical(configuration.ArtGroundTriggerDoLine)
            });
            Check(ArtDaqNative.CreateDOChannel(digitalOutputTask, lines, String.Empty,
                ArtDaqNative.ChannelPerLine), "create P0.0/P0.3/P0.5/P0.7 outputs");
            Check(ArtDaqNative.StartTask(digitalOutputTask), "start P0 digital output task");
        }

        private void CreateSafetyInputTask()
        {
            Check(ArtDaqNative.CreateTask(String.Empty, out safetyInputTask), "create P0.4 safety input task");
            Check(ArtDaqNative.CreateDIChannel(safetyInputTask,
                Physical(configuration.ArtSafetyDiLine), String.Empty,
                ArtDaqNative.ChannelPerLine), "create P0.4 safety input");
            Check(ArtDaqNative.StartTask(safetyInputTask), "start P0.4 safety input task");
        }

        private void CreateAnalogOutputTask()
        {
            Check(ArtDaqNative.CreateTask(String.Empty, out analogOutputTask), "create AO0 task");
            Check(ArtDaqNative.CreateAOVoltageChan(analogOutputTask,
                Physical(configuration.ArtControlAoChannel), String.Empty,
                0.0, configuration.AoMaximumVolts, ArtDaqNative.Volts, null), "create AO0 VSET channel");
            Check(ArtDaqNative.StartTask(analogOutputTask), "start AO0 task");
        }

        private void CreateMonitoringInputTask()
        {
            monitoringInputReady = false;
            try
            {
                IntPtr createdTask;
                int createCode = ArtDaqNative.CreateTask(String.Empty, out createdTask);
                if (createdTask != IntPtr.Zero) monitoringInputTask = createdTask;
                Check(createCode, "create monitoring AI task");
                CreateMonitoringChannel(monitoringInputTask, configuration.ArtCurrentAiChannel, "AI0 IMON");
                CreateMonitoringChannel(monitoringInputTask, configuration.ArtSourceVoltageAiChannel, "AI2 VMON");
                CreateMonitoringChannel(monitoringInputTask, configuration.ArtDividerAiChannel, "AI4 divider");
                Check(ArtDaqNative.StartTask(monitoringInputTask), "start monitoring AI task");
                monitoringInputReady = true;
            }
            catch (Exception setupFailure)
            {
                monitoringInputReady = false;
                try { ClearTask(ref monitoringInputTask, "cleanup incomplete monitoring AI task"); }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Monitoring AI setup and cleanup both failed; any uncleared task handle was retained for retry.",
                        setupFailure, cleanupFailure);
                }
                throw;
            }
        }

        private void CreateMonitoringChannel(IntPtr task, string channel, string label)
        {
            Check(ArtDaqNative.CreateAIVoltageChan(task, Physical(channel), String.Empty,
                ArtDaqNative.DefaultTerminalConfiguration,
                configuration.ArtMonitorInputMinimumV,
                configuration.ArtMonitorInputMaximumV,
                ArtDaqNative.Volts, null), "create " + label + " channel");
        }

        private void WriteAoCore(double volts)
        {
            if (analogOutputTask == IntPtr.Zero) throw new InvalidOperationException("AO0 task is not active.");
            int written;
            Check(ArtDaqNative.WriteAnalogF64(analogOutputTask, 1, 0, SinglePointIoTimeoutSeconds,
                ArtDaqNative.GroupByChannel, new double[] { volts }, out written, IntPtr.Zero),
                "write AO0 VSET");
            if (written != 1) throw new InvalidOperationException("ART-DAQ did not write the AO0 sample.");
        }

        private void WriteDigitalOutputsCore()
        {
            if (digitalOutputTask == IntPtr.Zero) throw new InvalidOperationException("Digital output task is not active.");
            byte[] data = new byte[]
            {
                hvOn ? (byte)1 : (byte)0,
                hvIndicatorOn ? (byte)1 : (byte)0,
                connectionIndicatorOn ? (byte)1 : (byte)0,
                groundTriggerOn ? (byte)1 : (byte)0
            };
            int written;
            Check(ArtDaqNative.WriteDigitalLines(digitalOutputTask, 1, 0, SinglePointIoTimeoutSeconds,
                ArtDaqNative.GroupByChannel, data, out written, IntPtr.Zero),
                "write P0 digital outputs");
            if (written != 1) throw new InvalidOperationException("ART-DAQ did not write the digital output sample.");
        }

        private string Physical(string relative)
        {
            if (String.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("An ART channel mapping is empty.");
            if (relative.StartsWith("/", StringComparison.Ordinal)) return relative;
            if (relative.StartsWith(configuration.ArtDeviceName + "/", StringComparison.OrdinalIgnoreCase)) return relative;
            return configuration.ArtDeviceName + "/" + relative;
        }

        private static void ClearTask(ref IntPtr task, string operation)
        {
            IntPtr handle = task;
            if (handle == IntPtr.Zero) return;

            Exception stopFailure = null;
            try
            {
                int stopCode = ArtDaqNative.StopTask(handle);
                if (stopCode < 0) stopFailure = CreateNativeException(stopCode, operation + ": stop");
            }
            catch (Exception ex)
            {
                stopFailure = new InvalidOperationException(operation + ": stop call failed.", ex);
            }

            Exception clearFailure = null;
            try
            {
                int clearCode = ArtDaqNative.ClearTask(handle);
                if (clearCode < 0)
                    clearFailure = CreateNativeException(clearCode, operation + ": clear");
                else
                    task = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                clearFailure = new InvalidOperationException(operation + ": clear call failed.", ex);
            }

            if (stopFailure != null && clearFailure != null)
                throw new AggregateException(operation + " failed; ClearTask did not succeed, so the handle was retained.",
                    stopFailure, clearFailure);
            if (clearFailure != null)
                throw new InvalidOperationException(operation + " failed; ClearTask did not succeed, so the handle was retained.",
                    clearFailure);
            if (stopFailure != null)
                throw new InvalidOperationException(operation + " reported a StopTask failure; the successfully cleared handle was released.",
                    stopFailure);
        }

        private void SafeClearAllTasks()
        {
            List<Exception> failures = new List<Exception>();
            lock (sync)
            {
                finiteInputReady = false;
                monitoringInputReady = false;
                TryClearTask(ref finiteInputTask, "clear finite AI task", failures);
                TryClearTask(ref monitoringInputTask, "clear monitoring AI task", failures);
                TryClearTask(ref analogOutputTask, "clear AO0 task", failures);
            }
            lock (safetyInputSync)
                TryClearTask(ref safetyInputTask, "clear P0.4 safety input task", failures);
            lock (digitalOutputSync)
                TryClearTask(ref digitalOutputTask, "clear P0 digital output task", failures);
            if (failures.Count > 0)
                throw new AggregateException("One or more USB3135 tasks could not be stopped or cleared.", failures.ToArray());
        }

        private static void TryClearTask(ref IntPtr task, string operation, List<Exception> failures)
        {
            try { ClearTask(ref task, operation); }
            catch (Exception ex) { failures.Add(ex); }
        }

        private bool HasRetainedTaskHandles()
        {
            if (finiteInputTask != IntPtr.Zero || monitoringInputTask != IntPtr.Zero || analogOutputTask != IntPtr.Zero)
                return true;
            lock (safetyInputSync)
            {
                if (safetyInputTask != IntPtr.Zero) return true;
            }
            lock (digitalOutputSync)
                return digitalOutputTask != IntPtr.Zero;
        }

        private void EnsureInitialized()
        {
            ThrowIfDisposed();
            if (disposing) throw new ObjectDisposedException("ArtUsb3135Device", "USB3135 disposal is in progress.");
            if (!initialized) throw new InvalidOperationException("USB3135 is not initialized.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("ArtUsb3135Device");
        }

        private static void Check(int code, string operation)
        {
            if (code >= 0) return;
            throw CreateNativeException(code, operation);
        }

        private static InvalidOperationException CreateNativeException(int code, string operation)
        {
            byte[] buffer = new byte[2048];
            try { ArtDaqNative.GetExtendedErrorInfo(buffer, (uint)buffer.Length); }
            catch { }
            string text = Encoding.Default.GetString(buffer);
            int terminator = text.IndexOf('\0');
            if (terminator >= 0) text = text.Substring(0, terminator);
            return new InvalidOperationException(operation + " failed (ART-DAQ " + code + "): " + text.Trim());
        }
    }

    internal static class ArtDaqNative
    {
        // Official Art_DAQ.h: ArtDAQ_Dev_SerialNum=0x0632, uint32; zero means unavailable.
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetDeviceAttribute", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int GetDeviceSerial(string deviceName, int attributeType, out uint value, IntPtr taskHandle);
        internal const int DefaultTerminalConfiguration = -1;
        internal const int ChannelPerLine = 0;
        internal const int Volts = 10348;
        internal const int Rising = 10280;
        internal const int Falling = 10171;
        internal const int FiniteSamples = 10178;
        internal const int GroupByChannel = 0;
        private static readonly object loadSync = new object();
        private static string loadedPath;

        internal static void EnsureLoaded(string libraryDirectory)
        {
            lock (loadSync)
            {
                if (String.IsNullOrWhiteSpace(libraryDirectory))
                    throw new ArgumentException("ArtDaqLibraryDirectory cannot be empty.", "libraryDirectory");
                string path = Path.GetFullPath(Path.Combine(libraryDirectory, "Art_DAQ.dll"));
                if (loadedPath != null)
                {
                    if (!String.Equals(path, loadedPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("ART runtime is already configured; restart before selecting another version.");
                    return;
                }
                if (!File.Exists(path)) throw new FileNotFoundException("ART-DAQ x64 Art_DAQ.dll was not found.", path);
                int status = HvxConfigureArtPathW(path);
                if (status != 0) throw new InvalidOperationException("ART backend configuration failed (bridge " + status + ").");
                loadedPath = path;
            }
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CharSet=CharSet.Unicode, ExactSpelling=true, CallingConvention=CallingConvention.StdCall)]
        private static extern int HvxConfigureArtPathW(string path);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateTask", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateTask(string taskName, out IntPtr taskHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_StartTask", CallingConvention = CallingConvention.StdCall)]
        internal static extern int StartTask(IntPtr taskHandle);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_TaskControl", CallingConvention = CallingConvention.StdCall)]
        internal static extern int TaskControl(IntPtr taskHandle, int action);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_StopTask", CallingConvention = CallingConvention.StdCall)]
        internal static extern int StopTask(IntPtr taskHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ClearTask", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ClearTask(IntPtr taskHandle);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateAIVoltageChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateAIVoltageChan(IntPtr taskHandle, string physicalChannel,
            string nameToAssignToChannel, int terminalConfiguration, double minimumValue,
            double maximumValue, int units, string customScaleName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateAOVoltageChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateAOVoltageChan(IntPtr taskHandle, string physicalChannel,
            string nameToAssignToChannel, double minimumValue, double maximumValue,
            int units, string customScaleName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateDIChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateDIChannel(IntPtr taskHandle, string lines,
            string nameToAssignToLines, int lineGrouping);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateDOChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateDOChannel(IntPtr taskHandle, string lines,
            string nameToAssignToLines, int lineGrouping);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CfgSampClkTiming", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ConfigureSampleClock(IntPtr taskHandle, string source,
            double rate, int activeEdge, int sampleMode, int samplesPerChannel);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CfgDigEdgeStartTrig", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ConfigureDigitalStartTrigger(IntPtr taskHandle,
            string triggerSource, int triggerEdge);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CfgAnlgEdgeStartTrig", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ConfigureAnalogStartTrigger(IntPtr taskHandle,
            string triggerSource, int triggerSlope, double triggerLevel);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadAnalogF64", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReadAnalogF64(IntPtr taskHandle, int samplesPerChannel,
            double timeoutSeconds, int fillMode, [Out] double[] readArray,
            uint arraySizeInSamples, out int samplesPerChannelRead, IntPtr reserved);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadDigitalLines", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReadDigitalLines(IntPtr taskHandle, int samplesPerChannel,
            double timeoutSeconds, int fillMode, [Out] byte[] readArray,
            uint arraySizeInBytes, out int samplesPerChannelRead,
            out int bytesPerSample, IntPtr reserved);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_WriteAnalogF64", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WriteAnalogF64(IntPtr taskHandle, int samplesPerChannel,
            int autoStart, double timeoutSeconds, int dataLayout, [In] double[] writeArray,
            out int samplesPerChannelWritten, IntPtr reserved);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_WriteDigitalLines", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WriteDigitalLines(IntPtr taskHandle, int samplesPerChannel,
            int autoStart, double timeoutSeconds, int dataLayout, [In] byte[] writeArray,
            out int samplesPerChannelWritten, IntPtr reserved);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetExtendedErrorInfo", CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetExtendedErrorInfo([Out] byte[] errorString, uint bufferSize);
    }
}
