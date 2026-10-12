using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HVX.OWTS.Task38
{
    public sealed class ConnectionInspectionRequest
    {
        public string ArtName;
        public string PicoSerial;
        public string PicoLibraryDirectory;
        public bool Simulation;
        public int SimulationDelayMilliseconds;
        public bool RuntimeOnly;
    }
    public sealed class ConnectionInspectionResult
    {
        public bool Passed;
        public bool WorkerStopped = true;
        public string Serial = "";
        public string Message = "";
        public string Diagnostic = "";
        public string RuntimePath = "";
    }
    // Discovery only: no Initialize/OpenUnit/CreateTask/ResetDevice or I/O writes.
    // Run in a disposable helper process so a stuck native enumeration cannot hang the UI.
    public static class ConnectionInspection
    {
        public static ConnectionInspectionResult CheckArt(ArtDeviceRecord[] records, string requestedName)
        {
            var matches = (records ?? new ArtDeviceRecord[0]).Where(x => x != null && x.DeviceName == requestedName).ToArray();
            string found = String.Join(", ", (records ?? new ArtDeviceRecord[0]).Where(x => x != null && x.DeviceType == "USB3135").Select(x => x.DeviceName));
            if (String.IsNullOrWhiteSpace(requestedName) || matches.Length != 1)
                return Fail("控制模块名称未找到或不唯一，请在硬件设置中核对名称。已登记：" + (found.Length == 0 ? "无" : found));
            var d = matches[0];
            if (d.DeviceType != "USB3135") return Fail("所选控制模块型号不是 USB3135。");
            // DMC DevNodeStatus is Windows CM status: DN_STARTED=0x8, DN_HAS_PROBLEM=0x400.
            if (String.IsNullOrWhiteSpace(d.InstanceId) || (d.Status & 8) == 0 || (d.Status & 0x400) != 0)
                return Fail("控制模块离线、未启动或驱动状态异常，请检查供电、通讯线和设备管理器。状态 0x" + d.Status.ToString("X"));
            if (d.SerialNumber == 0) return Fail("控制模块已枚举，但序列号为 0，身份校验未通过。");
            return new ConnectionInspectionResult { Passed = true, Serial = d.SerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), Message = "控制模块在线，序列号已读取；尚未初始化输入输出任务。" };
        }
        public static ConnectionInspectionResult CheckPico(string[] serials, string requestedSerial)
        {
            string[] available = (serials ?? new string[0]).Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray();
            if (available.Length == 0) return Fail("未发现可枚举的局放模块，请检查供电和通讯线，并确认其他采集软件已释放设备。");
            string selected = (requestedSerial ?? "").Trim();
            if (selected.Length == 0 && available.Length != 1)
                return Fail("发现多个局放模块，请在硬件设置中指定序列号：" + String.Join(", ", available));
            if (selected.Length == 0) selected = available[0];
            if (!available.Contains(selected)) return Fail("未发现指定的局放模块。可枚举序列号：" + String.Join(", ", available));
            return new ConnectionInspectionResult { Passed = true, Serial = selected, Message = "局放模块序列号已枚举；未打开设备，型号、占用状态和采集能力仍需正式连接确认。" };
        }
        public static ConnectionInspectionResult Inspect(ConnectionInspectionRequest request, string module)
        {
            try
            {
                if (request == null) return Fail("连接校验参数缺失。");
                if (module != "art" && module != "pico") return Fail("未知校验模块。");
                if (request.Simulation)
                {
                    System.Threading.Thread.Sleep(Math.Max(0, Math.Min(30000, request.SimulationDelayMilliseconds)));
                    return new ConnectionInspectionResult { Passed = true, Serial = module == "art" ? "SIM-USB3135" : "SIM-5242D", Message = "仿真连接校验通过，未访问真实设备。" };
                }
                if (module == "art") return CheckArt(new ArtDmcDeviceCatalog().ReadFresh(), request.ArtName);
                Pico5000aNative.EnsureLoaded(request.PicoLibraryDirectory);
                if (request.RuntimeOnly) return new ConnectionInspectionResult { Passed = true, RuntimePath = Pico5000aNative.LoadedPath,
                    Message = "局放运行库及配套依赖已加载；未枚举、打开或采集设备。", Diagnostic = PicoRuntimeLocator.Describe(Pico5000aNative.LoadedPath) };
                var buffer = new StringBuilder(4096); short length = 4096, count;
                uint status = EnumerateUnits(out count, buffer, ref length);
                if (status != 0) return new ConnectionInspectionResult { RuntimePath = Pico5000aNative.LoadedPath, Message = "局放模块枚举失败，请检查驱动、供电、通讯线及设备占用。错误码 0x" + status.ToString("X8"), Diagnostic = "ps5000aEnumerateUnits status=0x" + status.ToString("X8") };
                if (length > 4096 || count < 0) return Fail("局放模块返回的设备列表长度无效。");
                var values = buffer.ToString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (count != values.Length) return Fail("局放模块枚举数量不一致，请重新校验。");
                var result = CheckPico(values, request.PicoSerial);
                result.RuntimePath = Pico5000aNative.LoadedPath;
                result.Diagnostic = "运行库：" + PicoRuntimeLocator.Describe(result.RuntimePath);
                return result;
            }
            catch (Exception ex) { return new ConnectionInspectionResult { Message = (module == "art" ? "控制" : "局放") + "模块校验失败，请检查对应驱动及运行库；详见诊断信息。", Diagnostic = ex.ToString() }; }
        }
        public static ConnectionInspectionResult Fail(string message) { return new ConnectionInspectionResult { Message = message }; }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aEnumerateUnits", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern uint EnumerateUnits(out short count, StringBuilder serials, ref short length);
    }
}
