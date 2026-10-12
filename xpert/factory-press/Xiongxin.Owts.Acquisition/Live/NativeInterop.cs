using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 驱动预加载：先 LoadLibraryEx 探测 Art_DAQ.dll / ps5000a.dll，缺失时给出明确错误而不是在 P/Invoke 时崩溃。
    /// 签名对照 dac-source/modules-src/HardwareControl/src/ArtUsb3135Device.cs 与 PicoScope5242DBlockDevice.cs（只读参考）。
    /// </summary>
    internal static class NativeDriverLoader
    {
        const uint LoadWithAlteredSearchPath = 0x00000008;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

        public static bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        /// <summary>尝试加载；成功返回 true，失败给出可读原因。</summary>
        public static bool TryPreload(string dllName, string directory, out string error)
        {
            error = string.Empty;
            if (!IsWindows)
            {
                error = dllName + "：实机驱动仅支持 Windows（当前 " + Environment.OSVersion.Platform + "）。";
                return false;
            }
            string candidate = dllName;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                candidate = Path.Combine(directory, dllName);
                if (!File.Exists(candidate))
                {
                    error = "未找到驱动 " + candidate + "。请安装厂商驱动或在实机配置中指定正确目录。";
                    return false;
                }
            }
            try
            {
                IntPtr h = LoadLibraryEx(candidate, IntPtr.Zero,
                    Path.IsPathRooted(candidate) ? LoadWithAlteredSearchPath : 0);
                if (h == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    error = "加载 " + candidate + " 失败（Win32 错误 " + code + "）。"
                        + (code == 126 ? " 文件或其依赖缺失。" : code == 193 ? " 位数不匹配（x86/x64）。" : string.Empty);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "加载 " + candidate + " 异常：" + ex.Message;
                return false;
            }
        }
    }

    internal static class ArtDaqNative
    {
        internal const int ChannelPerLine = 0;
        internal const int GroupByChannel = 0;
        internal const int DefaultTerminalConfiguration = -1;
        internal const int Volts = 10348;
        internal const int Rising = 10280;
        internal const int Falling = 10171;
        internal const int FiniteSamples = 10178;
        /// <summary>ArtDAQ_Val_ContSamps — 连续监测时钟。</summary>
        internal const int ContinuousSamples = 10123;
        /// <summary>ArtDAQ_Val_GroupByScan — interleaved 样本布局（新鲜反馈窗）。</summary>
        internal const int GroupByScan = 1;

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CreateTask", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateTask(string taskName, out IntPtr taskHandle);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_StartTask", CallingConvention = CallingConvention.StdCall)]
        internal static extern int StartTask(IntPtr taskHandle);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_StopTask", CallingConvention = CallingConvention.StdCall)]
        internal static extern int StopTask(IntPtr taskHandle);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_ClearTask", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ClearTask(IntPtr taskHandle);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CreateDIChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateDIChannel(IntPtr taskHandle, string lines, string nameToAssignToLines, int lineGrouping);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CreateDOChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateDOChannel(IntPtr taskHandle, string lines, string nameToAssignToLines, int lineGrouping);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CreateAIVoltageChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateAIVoltageChan(IntPtr taskHandle, string physicalChannel, string nameToAssignToChannel,
            int terminalConfiguration, double minimumValue, double maximumValue, int units, string customScaleName);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CreateAOVoltageChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int CreateAOVoltageChan(IntPtr taskHandle, string physicalChannel, string nameToAssignToChannel,
            double minimumValue, double maximumValue, int units, string customScaleName);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_ReadAnalogF64", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReadAnalogF64(IntPtr taskHandle, int samplesPerChannel, double timeoutSeconds, int fillMode,
            [Out] double[] readArray, uint arraySizeInSamples, out int samplesPerChannelRead, IntPtr reserved);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_GetReadAvailSampPerChan", CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetReadAvailSampPerChan(IntPtr taskHandle, out uint readAvailSampPerChan);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_WriteAnalogF64", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WriteAnalogF64(IntPtr taskHandle, int samplesPerChannel, int autoStart, double timeoutSeconds,
            int dataLayout, [In] double[] writeArray, out int samplesPerChannelWritten, IntPtr reserved);


        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_ReadDigitalLines", CallingConvention = CallingConvention.StdCall)]
        internal static extern int ReadDigitalLines(IntPtr taskHandle, int samplesPerChannel, double timeoutSeconds, int fillMode,
            [Out] byte[] readArray, uint arraySizeInBytes, out int samplesPerChannelRead, out int bytesPerSample, IntPtr reserved);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_WriteDigitalLines", CallingConvention = CallingConvention.StdCall)]
        internal static extern int WriteDigitalLines(IntPtr taskHandle, int samplesPerChannel, int autoStart, double timeoutSeconds,
            int dataLayout, [In] byte[] writeArray, out int samplesPerChannelWritten, IntPtr reserved);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CfgSampClkTiming", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ConfigureSampleClock(IntPtr taskHandle, string source, double rate, int activeEdge, int sampleMode, int samplesPerChannel);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CfgDigEdgeStartTrig", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ConfigureDigitalStartTrigger(IntPtr taskHandle, string triggerSource, int triggerEdge);

        /// <summary>数字边沿参考触发（含 pretrigger 样点）；与 Pico 触发位置% 同步用。</summary>
        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CfgDigEdgeRefTrig", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ConfigureDigitalReferenceTrigger(IntPtr taskHandle, string triggerSource, int triggerEdge, uint pretriggerSamples);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_CfgAnlgEdgeStartTrig", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ConfigureAnalogStartTrigger(IntPtr taskHandle, string triggerSource, int triggerSlope, double triggerLevel);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_TaskControl", CallingConvention = CallingConvention.StdCall)]
        internal static extern int TaskControl(IntPtr taskHandle, int action);

        [DllImport("Art_DAQ.dll", EntryPoint = "ArtDAQ_GetExtendedErrorInfo", CallingConvention = CallingConvention.StdCall)]
        internal static extern int GetExtendedErrorInfo([Out] byte[] errorString, uint bufferSize);

        /// <summary>
        /// ART 设备已从总线消失/不可访问：Clear/Stop 失败时不得永久 cleanupFailed，
        /// 应丢弃托管句柄并允许 USB 恢复后同进程再 Connect。
        /// 含现场见过的 -200477（无设备）与常见 NI 系「设备不在系统」码。
        /// </summary>
        internal static bool IsDeviceGoneStatus(int status)
        {
            if (status >= 0) return false;
            switch (status)
            {
                case -200220: // Device not present / cannot be accessed
                case -200477: // 现场：无设备
                case -201003: // Device no longer present
                case -88705:  // Device not present in system
                case -50103:  // Resource reserved/unavailable after unplug
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsDeviceGoneMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("-200477", StringComparison.Ordinal) >= 0
                || message.IndexOf("-200220", StringComparison.Ordinal) >= 0
                || message.IndexOf("-201003", StringComparison.Ordinal) >= 0
                || message.IndexOf("无设备", StringComparison.Ordinal) >= 0
                || message.IndexOf("Device not present", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("not present in the system", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("DeviceCannotBeAccessed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static void Check(int status, string operation)
        {
            if (status >= 0) return;
            string detail = string.Empty;
            try
            {
                var buf = new byte[2048];
                GetExtendedErrorInfo(buf, (uint)buf.Length);
                detail = Encoding.Default.GetString(buf).TrimEnd('\0', ' ', '\r', '\n');
            }
            catch { /* ignore */ }
            string suffix = IsDeviceGoneStatus(status) ? "（设备已掉线/未枚举，可恢复后重连）" : "";
            throw new InvalidOperationException("ART-DAQ " + operation + " 失败（" + status + "）" + suffix
                + (detail.Length > 0 ? "：" + detail : "。"));
        }
    }

    internal static class Pico5000aNative
    {
        internal const uint Ok = 0x00000000;
        /// <summary>PICO_NOT_FOUND：未枚举到或仍被其它句柄/进程占用。</summary>
        internal const uint NotFound = 0x00000003;
        /// <summary>PICO_NOT_RESPONDING。</summary>
        internal const uint NotResponding = 0x00000005;
        /// <summary>PICO_OS_NOT_SUPPORTED（部分路径在句柄失效时亦见 0x7）。</summary>
        internal const uint OsNotSupportedOrDeadHandle = 0x00000007;
        /// <summary>PICO_INVALID_HANDLE（SDK 常见 0x0C）。</summary>
        internal const uint InvalidHandle = 0x0000000C;
        /// <summary>PICO_BUSY：已布置未完成；期望中≠断线；非期望=停轮不重连。</summary>
        internal const uint Busy = 0x00000027;
        internal const uint PowerSupplyConnected = 0x00000119;
        internal const uint PowerSupplyNotConnected = 0x0000011A;
        internal const uint Usb3DeviceOnUsb2Port = 0x0000011E;

        /// <summary>USB 掉线/句柄已死：CloseUnit 失败时应丢弃托管句柄，勿永久 cleanupFailed。</summary>
        internal static bool IsDeviceGoneStatus(uint status)
        {
            return status == NotFound
                || status == NotResponding
                || status == InvalidHandle
                || status == OsNotSupportedOrDeadHandle;
        }

        /// <summary>OpenUnit 前抑制说明；仅 SDK，永不启动 PicoScope.exe。</summary>
        internal static string StartupNote = string.Empty;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void ApplyFixDelegate(int fixNumber, short value);

        /// <summary>
        /// OpenUnit 前抑制 Pico 官方启动/欢迎窗（对照现网 LabVIEW/Factory：ps5000aApplyFix(485331304, 4582)）。
        /// 仅走 ps5000a.dll SDK，不启动 PicoScope.exe。须先 TryPreload/LoadLibrary 成功。
        /// </summary>
        internal static void SuppressStartupWindow()
        {
            IntPtr module = GetModuleHandle("ps5000a.dll");
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("ps5000a.dll 尚未加载，无法抑制启动窗口。请先预加载 SDK。");
            IntPtr entry = GetProcAddress(module, "ps5000aApplyFix");
            if (entry == IntPtr.Zero)
                throw new EntryPointNotFoundException("局放运行库缺少启动窗口抑制接口 ps5000aApplyFix。");
            ((ApplyFixDelegate)Marshal.GetDelegateForFunctionPointer(entry, typeof(ApplyFixDelegate)))(485331304, 4582);
            StartupNote = "已在 OpenUnit 前调用启动窗口抑制 (485331304, 4582)；仅 SDK 驱动，不启动 PicoScope.exe。";
        }

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aOpenUnit", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern uint OpenUnit(out short handle, string serial, int resolution);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aCloseUnit", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint CloseUnit(short handle);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aChangePowerSource", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint ChangePowerSource(short handle, uint powerState);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aSetDeviceResolution", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetDeviceResolution(short handle, int resolution);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetDeviceResolution", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetDeviceResolution(short handle, out int resolution);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aSetChannel", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetChannel(short handle, int channel, short enabled, int coupling, int range, float analogOffset);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aMemorySegments", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint MemorySegments(short handle, uint segments, out int maximumSamples);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetTimebase2", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetTimebase2(short handle, uint timebase, int samples, out float timeIntervalNanoseconds, out int maximumSamples, uint segmentIndex);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aMaximumValue", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint MaximumValue(short handle, out short value);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aSetSimpleTrigger", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetSimpleTrigger(short handle, short enable, int source, short threshold, int direction, uint delay, short autoTriggerMilliseconds);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aSetDataBuffer", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetDataBuffer(short handle, int source, System.IntPtr buffer, int bufferLength, uint segmentIndex, int ratioMode);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aRunBlock", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint RunBlock(short handle, int preTriggerSamples, int postTriggerSamples, uint timebase,
            out int timeIndisposedMilliseconds, uint segmentIndex, BlockReadyCallback callback, System.IntPtr parameter);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetValues", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetValues(short handle, uint startIndex, ref uint samples, uint downSampleRatio, int downSampleRatioMode, uint segmentIndex, out short overflow);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetTriggerTimeOffset64", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetTriggerTimeOffset64(short handle, out long time, out int timeUnits, uint segmentIndex);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aStop", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint Stop(short handle);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aPingUnit", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint PingUnit(short handle);

        // —— Rapid Block / Bulk Overlapped（对照 LabVIEW PicoScope5000aExampleRapidBlockBulkOverlapped）——

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aSetNoOfCaptures", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetNoOfCaptures(short handle, uint nCaptures);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetNoOfCaptures", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetNoOfCaptures(short handle, out uint nCaptures);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetNoOfProcessedCaptures", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetNoOfProcessedCaptures(short handle, out uint nProcessedCaptures);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aSetDataBuffers", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint SetDataBuffers(short handle, int source, System.IntPtr bufferMax, System.IntPtr bufferMin,
            int bufferLength, uint segmentIndex, int ratioMode);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetValuesBulk", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetValuesBulk(short handle, ref uint noOfSamples, uint fromSegmentIndex, uint toSegmentIndex,
            uint downSampleRatio, int downSampleRatioMode, short[] overflow);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetValuesOverlapped", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetValuesOverlapped(short handle, uint startIndex, ref uint noOfSamples,
            uint downSampleRatio, int downSampleRatioMode, uint segmentIndex, out short overflow);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetValuesOverlappedBulk", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetValuesOverlappedBulk(short handle, uint startIndex, ref uint noOfSamples,
            uint downSampleRatio, int downSampleRatioMode, uint fromSegmentIndex, uint toSegmentIndex, short[] overflow);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aGetValuesTriggerTimeOffsetBulk64", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint GetValuesTriggerTimeOffsetBulk64(short handle, long[] times, int[] timeUnits,
            uint fromSegmentIndex, uint toSegmentIndex);

        [DllImport("ps5000a.dll", EntryPoint = "ps5000aIsReady", CallingConvention = CallingConvention.StdCall)]
        internal static extern uint IsReady(short handle, out short ready);

        internal delegate void BlockReadyCallback(short handle, uint status, System.IntPtr parameter);

        internal const int DcCoupling = 1;
        internal const int ChannelA = 0;
        internal const int ChannelB = 1;
        internal const int ExternalTrigger = 4;
        internal const int RisingDirection = 2;
        internal const int RatioModeNone = 0;

        /// <summary>14 bit → 2，15 bit → 3（125 MS/s 只允许这两档）。</summary>
        internal static int DriverMode(int bits)
        {
            if (bits == 14) return 2;
            if (bits == 15) return 3;
            throw new ArgumentException("125 MS/s 采集仅提供 14 或 15 bit 模式。");
        }

        internal static void Check(uint status, string operation)
        {
            if (status == Ok) return;
            string hint = "";
            if (status == NotFound)
                hint = "：示波器未找到或仍被占用（上次异常退出/掉线未 CloseUnit 时常见；可拔插 USB 或复位清理门后重连）";
            else if (IsDeviceGoneStatus(status))
                hint = "：句柄失效或设备已掉线（可恢复后重连）";
            throw new InvalidOperationException("PicoScope 5242D " + operation + " 失败（PICO_STATUS 0x"
                + status.ToString("X8") + "）" + hint + "。");
        }

        /// <summary>
        /// 传输态分类：期望 BUSY 时仅记录；非期望 BUSY 停轮不按断线、不自动重连/重加压。
        /// </summary>
        internal static void CheckTransport(uint status, string operation, bool expectBusyOk)
        {
            if (status == Busy)
            {
                if (expectBusyOk) return;
                throw new InvalidOperationException(
                    "Pico 非预期 BUSY（0x27）；" + operation
                    + "：未确认就绪，禁止开始/继续本轮；不按物理断线记录，不自动重试输出。");
            }
            Check(status, operation);
        }

        internal static string DescribeBusy(bool expectBusyOk)
        {
            return expectBusyOk
                ? "PICO_BUSY 0x27：本轮已布置，等待触发/采集完成；不是断线或完成证据，仍受任务期限限制。"
                : "PICO_BUSY 0x27：非预期采集忙，停止本轮；不按物理断线记录，不自动重试输出。";
        }
    }
}
