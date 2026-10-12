using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HVX.OWTS.Task38;

namespace Xiongxin.ZeroBench
{
    // The worker calls all native operations serially. The driver callback only
    // publishes completion; no thread blocks inside a long trigger wait.
    internal sealed class PicoInputCapture : IDisposable
    {
        private static readonly List<PicoInputCapture> retainedOnCloseFailure = new List<PicoInputCapture>();
        private short handle;
        private short[] buffer;
        private GCHandle pin;
        private bool pinned;
        private Pico5000aNative.BlockReadyCallback callback;
        private readonly object completionGate = new object();
        private readonly List<Pico5000aNative.BlockReadyCallback> callbacks = new List<Pico5000aNative.BlockReadyCallback>();
        private int acquisitionGeneration;
        private int ready;
        private uint completionStatus;
        private float intervalNs;
        private short maximumAdc;
        private int rangeIndex;
        private int resolutionBits;
        private int sampleCount;
        private int preTriggerCount;
        private uint timebase;
        private string variant;
        private bool freeRun;
        private int triggerDirection;
        private double captureDurationMs;
        private bool cooldownReleasePending;
        internal Action<string> CommunicationError;
        internal Action CommunicationOk;
        internal Action<string> CommunicationBusy;
        // Optional passive host timing. Failure to observe must never change an
        // SDK call, its status check, resource ownership, or the original error.
        internal Func<string, IDisposable> TimingScope;
        internal bool ConnectionOpen { get { return handle != 0; } }
        internal string Serial { get; private set; }
        internal bool Armed { get; private set; }
        internal double ActualRateHz { get { return 1e9 / intervalNs; } }
        internal uint Timebase { get { return timebase; } }
        internal short ExternalThresholdAdc { get; private set; }
        internal double ExternalThresholdQuantizedVolts { get { return ExternalThresholdAdc * 5.0 / 32767; } }
        internal bool Ready { get { return Volatile.Read(ref ready) != 0; } }

        internal void Initialize(BenchSettings settings, bool captureWithoutTrigger = false)
        {
            if (handle != 0) throw new InvalidOperationException("Pico 输入连接已存在。");
            if (String.IsNullOrWhiteSpace(settings.PicoSerial)) throw new ArgumentException("Pico 序列号不能为空。");
            settings.Validate();
            FactoryCaptureCooldown.RequireSupportedDuration(settings.CaptureDurationMs);
            resolutionBits = settings.ResolutionBits;
            sampleCount = settings.RequestedPicoCount;
            preTriggerCount = captureWithoutTrigger ? 0 : settings.PicoPreTriggerCount;
            timebase = PicoCaptureConfiguration.TimebaseFor(resolutionBits, settings.RequestedPicoRateHz);
            Pico5000aNative.EnsureLoaded(settings.PicoLibraryDirectory);
            Pico5000aNative.SuppressStartupWindow();
            uint code = Pico5000aNative.OpenUnit(out handle, settings.PicoSerial.Trim(), PicoCaptureConfiguration.DriverMode(resolutionBits));
            if (handle < 0) handle = 0;
            if (code == Pico5000aNative.PowerSupplyConnected || code == Pico5000aNative.PowerSupplyNotConnected || code == Pico5000aNative.Usb3DeviceOnUsb2Port)
            {
                if (handle == 0) throw new InvalidOperationException("Pico 电源状态未返回有效句柄。");
                Check(Pico5000aNative.ChangePowerSource(handle, code), "接受 Pico 电源状态");
                code = 0;
            }
            Check(code, "打开指定 Pico");
            if (handle == 0) throw new InvalidOperationException("Pico 未返回有效连接句柄。");
            FactoryCaptureCooldown.MarkOpened();
            Serial = UnitInfo(Pico5000aNative.BatchAndSerial);
            variant = UnitInfo(Pico5000aNative.VariantInfo);
            if (!String.Equals(Serial, settings.PicoSerial.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("Pico 序列号与指定设备不一致。");
            if (variant.IndexOf("5242D", StringComparison.OrdinalIgnoreCase) < 0 || variant.IndexOf("MSO", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("本采集要求非 MSO 的 5242D，实际为 " + variant + "。");
            ConfigureCore(settings,captureWithoutTrigger);
        }

        // Reconfigure the same exact-serial session only after its old segment
        // relinquishes all buffers. This method never opens or closes a handle.
        internal void Configure(BenchSettings settings,bool captureWithoutTrigger=false)
        {
            if(handle==0 || Armed || pinned || cooldownReleasePending)
                throw new InvalidOperationException("Pico既有会话尚未完成旧段停止/释放，不能重新配置。");
            if(settings==null)throw new ArgumentNullException("settings");
            settings.Validate();FactoryCaptureCooldown.RequireSupportedDuration(settings.CaptureDurationMs);
            if(!String.Equals(Serial,settings.PicoSerial.Trim(),StringComparison.Ordinal) ||
                !String.Equals(UnitInfo(Pico5000aNative.BatchAndSerial),Serial,StringComparison.Ordinal))
                throw new InvalidOperationException("Pico重配置身份不符，不能换用其他设备或旧句柄。");
            resolutionBits=settings.ResolutionBits;sampleCount=settings.RequestedPicoCount;
            preTriggerCount=captureWithoutTrigger?0:settings.PicoPreTriggerCount;
            timebase=PicoCaptureConfiguration.TimebaseFor(resolutionBits,settings.RequestedPicoRateHz);
            ConfigureCore(settings,captureWithoutTrigger);
        }

        private void ConfigureCore(BenchSettings settings,bool captureWithoutTrigger)
        {
            int requestedMode = PicoCaptureConfiguration.DriverMode(resolutionBits), actualMode;
            Check(Pico5000aNative.SetDeviceResolution(handle, requestedMode), "设置 Pico " + resolutionBits + " bit 模式");
            Check(Pico5000aNative.GetDeviceResolution(handle, out actualMode), "回读 Pico 分辨率");
            if (actualMode != requestedMode) throw new InvalidOperationException("Pico 分辨率回读不符。");
            rangeIndex = settings.RangeIndex;
            Check(Pico5000aNative.SetChannel(handle, 0, 1, settings.PicoCoupling, rangeIndex, 0), "设置 Pico A 量程、耦合及 0 V 模拟偏置");
            Check(Pico5000aNative.SetChannel(handle, 1, 0, 1, rangeIndex, 0), "关闭未使用的 Pico B");
            Check(SetBandwidthFilter(handle, 0, settings.PicoBandwidth20MHz ? 1 : 0), "设置 Pico A 带宽限制");
            int maxSamples;
            Check(Pico5000aNative.MemorySegments(handle, 1, out maxSamples), "设置单段 Pico 内存");
            if (maxSamples < sampleCount) throw new InvalidOperationException("Pico 内存不足以采集请求的 " + sampleCount + " 点。");
            Check(Pico5000aNative.GetTimebase2(handle, timebase, sampleCount, out intervalNs, out maxSamples, 0), "验证 Pico 请求时基 " + timebase);
            PicoCaptureConfiguration.ValidateSdkTimebase(settings.RequestedPicoRateHz, intervalNs, maxSamples, sampleCount);
            captureDurationMs=sampleCount*(double)intervalNs/1000000.0;
            FactoryCaptureCooldown.RequireSupportedDuration(captureDurationMs);
            Check(Pico5000aNative.MaximumValue(handle, out maximumAdc), "读取 Pico ADC 满刻度");
            if (maximumAdc <= 0) throw new InvalidOperationException("Pico 返回无效 ADC 满刻度。");
            short threshold = ConvertExternalThreshold(settings.ExternalTriggerVolts);
            triggerDirection = settings.FallingTrigger ? 3 : 2;
            Check(Pico5000aNative.SetSimpleTrigger(handle, 1, 4, threshold, settings.FallingTrigger ? 3 : 2, 0, 0), "设置 Pico EXT 外触发，禁用自动触发");
            ExternalThresholdAdc = threshold;
            SetFreeRun(captureWithoutTrigger);
        }

        // Factory calibration is a free block, not a synthetic EXT trigger.
        // Pressure acquisition always explicitly restores external triggering.
        internal void SetFreeRun(bool captureWithoutTrigger)
        {
            if (handle == 0 || Armed || pinned) throw new InvalidOperationException("Pico 连接未就绪或采集仍占用缓冲区，不能改变触发模式。");
            Check(Pico5000aNative.SetSimpleTrigger(handle, (short)(captureWithoutTrigger ? 0 : 1),
                4, ExternalThresholdAdc, triggerDirection, 0, 0), captureWithoutTrigger
                ? "校准：禁用 Pico EXT，使用自由单段采集"
                : "压测：启用 Pico EXT 上升沿，禁止自动触发替代");
            freeRun = captureWithoutTrigger;
        }

        internal static short ConvertExternalThreshold(double volts)
        {
            if (Double.IsNaN(volts) || Double.IsInfinity(volts) || volts <= -5 || volts >= 5)
                throw new ArgumentOutOfRangeException("volts", "EXT 阈值必须在 ±5 V 内，不使用 A 通道量程或 ADC 上限换算。");
            return checked((short)Math.Round(volts / 5.0 * 32767, MidpointRounding.AwayFromZero));
        }

        internal void Arm()
        {
            if (handle == 0 || Armed || pinned) throw new InvalidOperationException("Pico 未初始化或已占用采集缓冲区。");
            FactoryCaptureCooldown.RequireSupportedDuration(captureDurationMs);
            FactoryCaptureCooldown.EnsureReady(); // Never wait inside an energized 500 ms handoff.
            buffer = new short[sampleCount];
            pin = GCHandle.Alloc(buffer, GCHandleType.Pinned); pinned = true;
            Check(Pico5000aNative.SetDataBuffer(handle, 0, pin.AddrOfPinnedObject(), buffer.Length, 0, 0), "注册 Pico 原始缓冲区");
            int generation;
            lock(completionGate) {generation=++acquisitionGeneration;Volatile.Write(ref ready,0);completionStatus=UInt32.MaxValue;}
            callback = OnReady;
            callbacks.Add(callback); // Retain old delegates until Close; stale callbacks cannot complete a new block.
            FactoryCaptureCooldown.MarkCaptureStarted(captureDurationMs);
            cooldownReleasePending=true;
            Armed = true; // Keep native ownership even if RunBlock reports failure.
            int busyMs;
            int pre=freeRun?0:preTriggerCount;
            Check(Pico5000aNative.RunBlock(handle, pre, buffer.Length-pre, timebase, out busyMs, 0, callback, new IntPtr(generation)), "布置 Pico 外触发采集（前="+pre+"，后="+(buffer.Length-pre)+"）");
        }

        private void OnReady(short callbackHandle, uint status, IntPtr parameter)
        {
            lock(completionGate)
            {
                if(callbackHandle!=handle || parameter.ToInt64()!=acquisitionGeneration || !Armed)return;
                completionStatus = status;
                Volatile.Write(ref ready, 1);
            }
        }

        internal void CheckCompletionStatus()
        {
            if (Ready) Check(completionStatus, "Pico 采集完成回调");
        }

        internal PicoBlockCapture ReadCompleted()
        {
            using (BeginTiming("Pico.ReadCompleted"))
            {
            if (!Armed || !Ready || buffer == null) throw new InvalidOperationException("Pico 采集尚未完成。");
            CheckCompletionStatus();
            uint count = (uint)buffer.Length;
            short overflow;
            uint status;
            using (BeginTiming("Pico.GetValues"))
                status = Pico5000aNative.GetValues(handle, 0, ref count, 1, 0, 0, out overflow);
            Check(status, "传输已完成的 Pico 数据");
            if (count != buffer.Length) throw new InvalidOperationException("Pico 返回 " + count + " 点，未返回请求的完整 " + buffer.Length + " 点数据。");
            long triggerOffset = 0;
            int triggerUnits = -1; // No physical trigger offset exists for a free calibration block.
            if (!freeRun)
            {
                using (BeginTiming("Pico.GetTriggerTimeOffset64"))
                    status = Pico5000aNative.GetTriggerTimeOffset64(handle, out triggerOffset, out triggerUnits, 0);
                Check(status, "读取 Pico 触发时间偏移");
            }
            string capturedSerial = UnitInfo(Pico5000aNative.BatchAndSerial);
            if (!String.Equals(capturedSerial, Serial, StringComparison.Ordinal)) throw new InvalidOperationException("Pico 采集后身份发生变化。");
            return new PicoBlockCapture
            {
                RawAdc = buffer, SampleCount = buffer.Length, ResolutionModeBits = resolutionBits,
                RangeHardwareBits = PicoCaptureConfiguration.HardwareBits(resolutionBits, rangeIndex),
                SampleIntervalNanoseconds = intervalNs, MaximumAdc = maximumAdc,
                FullScaleVolts = BenchSettings.Ranges[rangeIndex], HardwareOverflow = overflow != 0,
                TriggerTimeOffset = triggerOffset, TriggerTimeUnitCode = triggerUnits, Variant = variant, Serial = Serial
            };
            }
        }

        internal void StopCapture()
        {
            lock(completionGate) {++acquisitionGeneration;Volatile.Write(ref ready,0);}
            if (handle != 0 && (Armed || pinned))
            {
                // A failed Stop retains the callback and pin until a successful
                // Stop or Close establishes the native ownership boundary.
                uint status;
                using (BeginTiming("Pico.StopCapture.Stop")) status = Pico5000aNative.Stop(handle);
                Check(status, "停止 Pico 单段采集");
            }
            Armed = false;
            if (pinned) { pin.Free(); pinned = false; }
            callback = null;
            buffer = null;
            Volatile.Write(ref ready, 0);
            MarkCaptureReleased();
        }

        public void Dispose()
        {
            var errors = new List<Exception>();
            if (handle != 0 && (Armed || pinned))
            {
                try
                {
                    uint status;
                    using (BeginTiming("Pico.Dispose.Stop")) status = Pico5000aNative.Stop(handle);
                    Check(status, "停止 Pico 采集"); Armed = false;
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (handle != 0)
            {
                try
                {
                    uint status;
                    using (BeginTiming("Pico.Dispose.CloseUnit")) status = Pico5000aNative.CloseUnit(handle);
                    Check(status, "关闭 Pico 连接"); handle = 0; Armed = false;
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            // CloseUnit is the final ownership boundary. A failed Stop/Close may
            // still retain the native pointer or callback; keep both alive then.
            if (handle == 0)
            {
                if (pinned) { pin.Free(); pinned = false; }
                callback = null; buffer = null; callbacks.Clear();
                // A successful Close is the final ownership boundary even if
                // Stop failed; retain that Stop exception and fail the old run.
                MarkCaptureReleased();
                lock (retainedOnCloseFailure) retainedOnCloseFailure.Remove(this);
            }
            else lock (retainedOnCloseFailure) if (!retainedOnCloseFailure.Contains(this)) retainedOnCloseFailure.Add(this);
            if (errors.Count != 0) throw new AggregateException("Pico 输入资源释放报告错误。", errors);
        }
        private void MarkCaptureReleased()
        {
            if(!cooldownReleasePending)return;
            FactoryCaptureCooldown.MarkReleased(captureDurationMs);
            cooldownReleasePending=false;
        }

        private string UnitInfo(int kind)
        {
            var value = new StringBuilder(128); short required;
            uint status;
            using (BeginTiming("Pico.GetUnitInfo"))
                status = Pico5000aNative.GetUnitInfo(handle, value, (short)value.Capacity, out required, kind);
            Check(status, "读取 Pico 设备信息");
            return value.ToString();
        }
        private IDisposable BeginTiming(string operation)
        {
            try
            {
                var begin = TimingScope;
                if (begin == null) return null;
                var scope = begin(operation);
                return scope == null ? null : new PassiveTimingScope(scope);
            }
            catch { return null; }
        }
        private sealed class PassiveTimingScope : IDisposable
        {
            private IDisposable inner;
            internal PassiveTimingScope(IDisposable inner) { this.inner = inner; }
            public void Dispose()
            {
                var scope = inner; inner = null;
                if (scope != null) { try { scope.Dispose(); } catch { } }
            }
        }
        private void Check(uint status, string operation)
        {
            if (status != 0)
            {
                // Snapshot the thread-local bridge detail before any reporting callback can call native code.
                var failure=Pico5000aNative.CreateStatusException(status, operation, Pico5000aNative.ReadBridgeDiagnostic, true);
                try { if(CommunicationError!=null)CommunicationError(failure.ToString()); }
                catch(Exception reportFailure)
                { throw new AggregateException("采集未完成，请检查设备后重试。",failure,reportFailure); }
                throw failure;
            }
        }
        internal void CheckTransport()
        {
            if(handle==0)throw new InvalidOperationException("Pico句柄未打开");
            // SDK PingUnit explicitly checks USB communication in all modes;
            // cached identity metadata is not a transport heartbeat.
            HandleTransportStatus(PingUnit(handle));
        }
        internal void HandleTransportStatus(uint status)
        {
            if(status==0x27)
            {
                bool expected=Armed && !Ready;
                if(CommunicationBusy!=null)CommunicationBusy(expected?
                    "PICO_BUSY 0x27：本轮已布置，等待触发/采集完成；不是断线或完成证据，仍受任务期限限制。":
                    "PICO_BUSY 0x27：非预期采集忙，停止本轮；不按物理断线记录，不自动重试输出。");
                if(expected)return;
                throw new InvalidOperationException("Pico非预期BUSY（0x27）；未确认就绪，禁止开始/继续本轮。");
            }
            Check(status,"Pico USB通信轮询");
            if(CommunicationOk!=null)CommunicationOk();
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll",EntryPoint="ps5000aPingUnit",CallingConvention=CallingConvention.StdCall)]
        private static extern uint PingUnit(short handle);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aSetBandwidthFilter", CallingConvention = CallingConvention.StdCall)]
        private static extern uint SetBandwidthFilter(short handle, int channel, int bandwidth);
    }
}
