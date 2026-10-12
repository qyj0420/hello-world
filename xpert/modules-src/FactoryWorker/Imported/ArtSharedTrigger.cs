using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    // The only physical output owned by this adapter is P0.7 / DB37 pin 4.
    // It is not an HV controller and has no AO, port-wide DO, or device-reset API.
    // P0.7 drives the thyristor optical circuit. The common switching-detection
    // signal goes to ART PFI0 and Pico EXT; it is not the P0.7 output itself.
    // The caller must arm BOTH receivers before requesting the optical hold.
    internal sealed class ArtSharedTrigger : IDisposable
    {
        private readonly IArtSharedTriggerBackend backend;
        private readonly IArtSharedTriggerClock clock;
        private IntPtr task;
        private bool prepareAttempted, pulseAttempted, configured, writeAttempted, disposed;
        private int pulseMilliseconds;
        internal bool Ready { get; private set; }

        internal ArtSharedTrigger() : this(new NativeArtSharedTriggerBackend(), new ArtSharedTriggerClock()) { }
        internal ArtSharedTrigger(IArtSharedTriggerBackend backend, IArtSharedTriggerClock clock)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            if (clock == null) throw new ArgumentNullException("clock");
            this.backend = backend; this.clock = clock;
        }

        internal void Prepare(BenchSettings settings)
        {
            if (disposed || prepareAttempted) throw new InvalidOperationException("P0.7 触发器不可重复初始化。");
            ValidateSettings(settings);
            prepareAttempted = true;
            pulseMilliseconds = settings.TriggerPulseMs;
            try
            {
                backend.LoadAndVerify(settings);
                // Retain even a partially-created handle when a driver call fails.
                backend.CreateTask(out task);
                if (task == IntPtr.Zero) throw new InvalidOperationException("ART 未返回有效 P0.7 单线输出任务句柄。");
                backend.ConfigureLine(task, settings.ArtDevice + "/port0/line7");
                configured = true;
                writeAttempted = true;
                backend.Write(task, false);
                Ready = true;
            }
            catch (Exception failure)
            {
                ThrowFailure(failure, ReleaseOwnedTask());
            }
        }

        internal static void ValidateSettings(BenchSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            settings.ValidateForNewRun();
            if (!settings.RealHardware || !settings.AutomaticSharedTrigger || settings.TargetVoltageV != 0)
                throw new ArgumentException("P0.7 主动触发只用于显式启用的实机 0 V 采集。");
            if (!String.Equals(settings.ArtTrigger, "/" + settings.ArtDevice + "/PFI0", StringComparison.Ordinal) || settings.FallingTrigger)
                throw new ArgumentException("已确认接线：P0.7 控制晶闸管光触发；ART PFI0 与 Pico EXT 共用开关同步检测的上升沿。");
            // A DAQ physical-channel list accepts commas/ranges. Do not allow any
            // such syntax in a device name before appending the fixed single line.
            foreach (char c in settings.ArtDevice)
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    throw new ArgumentException("P0.7 输出的 ART 模块名不得包含通道列表、范围或路径语法。");
            uint serial;
            if (!UInt32.TryParse(settings.ArtSerial.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out serial) || serial == 0)
                throw new ArgumentException("P0.7 输出需要实际枚举所得的非零 ART 序列号。");
            if (settings.TriggerPulseMs != BenchSettings.OpticalHoldMilliseconds)
                throw new ArgumentException("P0.7 正常光触发请求固定 " + BenchSettings.OpticalHoldMilliseconds + " ms；Windows 延时不是硬实时保证。");
        }

        internal void Pulse(CancellationToken cancellation)
        {
            if (disposed || !Ready || pulseAttempted || task == IntPtr.Zero)
                throw new InvalidOperationException("P0.7 未就绪或本轮脉冲已发送；禁止重复触发。");
            Ready = false;
            pulseAttempted = true;
            Exception failure = null;
            List<Exception> cleanup;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                backend.Write(task, true);
                clock.Pause(pulseMilliseconds, cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // Normal completion holds the optical circuit for the shared fixed duration. Stop,
                // cancellation or errors may end it early and are NOT successful
                // full-duration pulses. Always attempt low before Stop/Clear.
                cleanup = ReleaseOwnedTask();
            }
            ThrowFailure(failure, cleanup);
        }

        public void Dispose()
        {
            disposed = true;
            ThrowFailure(null, ReleaseOwnedTask());
        }

        private List<Exception> ReleaseOwnedTask()
        {
            Ready = false;
            var failures = new List<Exception>();
            if (task == IntPtr.Zero) return failures;
            if (configured && writeAttempted)
            {
                try { backend.Write(task, false); } catch (Exception ex) { failures.Add(ex); }
                try { backend.Stop(task); } catch (Exception ex) { failures.Add(ex); }
            }
            try
            {
                backend.Clear(task);
                task = IntPtr.Zero; configured = false; writeAttempted = false;
            }
            catch (Exception ex) { failures.Add(ex); }
            return failures;
        }

        private static void ThrowFailure(Exception failure, List<Exception> cleanup)
        {
            if (cleanup.Count != 0)
            {
                if (failure != null) cleanup.Insert(0, failure);
                throw new AggregateException("P0.7 触发或清理失败，不能确认触发线已恢复低电平。驱动阻塞、断连或强制结束进程时，软件不能保证硬件电平恢复；请检查 TRG 线路状态。", cleanup);
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    internal interface IArtSharedTriggerBackend
    {
        void LoadAndVerify(BenchSettings settings);
        void CreateTask(out IntPtr task);
        void ConfigureLine(IntPtr task, string physicalLine);
        void Write(IntPtr task, bool high);
        void Stop(IntPtr task);
        void Clear(IntPtr task);
    }

    internal interface IArtSharedTriggerClock { void Pause(int milliseconds, CancellationToken cancellation); }
    internal sealed class ArtSharedTriggerClock : IArtSharedTriggerClock
    {
        public void Pause(int milliseconds, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (cancellation.WaitHandle.WaitOne(milliseconds)) cancellation.ThrowIfCancellationRequested();
        }
    }

    internal sealed class NativeArtSharedTriggerBackend : IArtSharedTriggerBackend
    {
        public void LoadAndVerify(BenchSettings settings)
        {
            InputOnlyArtNative.Load(settings.ArtLibraryDirectory);
            uint serial;
            InputOnlyArtNative.Check(InputOnlyArtNative.GetSerial(settings.ArtDevice, 0x0632, out serial, IntPtr.Zero), "TRG 输出前复核 ART 序列号");
            if (serial == 0 || !String.Equals(serial.ToString(CultureInfo.InvariantCulture), settings.ArtSerial.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("TRG 输出前 ART 序列号不匹配；未创建任何输出任务。");
            var model = new StringBuilder(128);
            InputOnlyArtNative.Check(InputOnlyArtNative.GetProduct(settings.ArtDevice, 0x0631, model, model.Capacity, IntPtr.Zero), "TRG 输出前复核 ART 型号");
            if (!String.Equals(model.ToString().Trim(), "USB3135", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("TRG 输出仅允许 USB3135，实际 " + model + "；未创建任何输出任务。");
        }

        public void CreateTask(out IntPtr task)
        { InputOnlyArtNative.Check(InputOnlyArtNative.CreateTask(String.Empty, out task), "创建仅 P0.7 的 TRG 任务"); }
        public void ConfigureLine(IntPtr task, string physicalLine)
        {
            const string suffix = "/port0/line7";
            if (physicalLine == null || !physicalLine.EndsWith(suffix, StringComparison.Ordinal) ||
                physicalLine.IndexOf('/') != physicalLine.Length - suffix.Length || physicalLine.IndexOfAny(new[] { ',', ':', '\\' }) >= 0)
                throw new ArgumentException("TRG 任务仅允许一个模块的 port0/line7，禁止整个端口或其他数字线。");
            InputOnlyArtNative.Check(CreateDo(task, physicalLine, String.Empty, 0), "配置 P0.7 单线 TRG 输出（ChanPerLine）");
        }
        public void Write(IntPtr task, bool high)
        {
            int written;
            // bool32 / uInt32 are 32-bit in the installed Windows ART SDK.
            // One sample on one line, NOT a bitmask for an entire port.
            InputOnlyArtNative.Check(WriteLines(task, 1, 1, .5, 0, new byte[] { high ? (byte)1 : (byte)0 }, out written, IntPtr.Zero),
                "P0.7 写入" + (high ? "高" : "低") + "电平");
            if (written != 1) throw new InvalidOperationException("P0.7 写入样本数不完整；不能确认实际电平。");
        }
        public void Stop(IntPtr task) { InputOnlyArtNative.Check(InputOnlyArtNative.StopTask(task), "停止 P0.7 TRG 任务"); }
        public void Clear(IntPtr task) { InputOnlyArtNative.Check(InputOnlyArtNative.ClearTask(task), "释放 P0.7 TRG 任务"); }

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateDOChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int CreateDo(IntPtr task, string lines, string assignedName, int grouping);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_WriteDigitalLines", CallingConvention = CallingConvention.StdCall)]
        private static extern int WriteLines(IntPtr task, int samples, uint autoStart, double timeout, uint layout,
            [In] byte[] data, out int written, IntPtr reserved);
    }
}
