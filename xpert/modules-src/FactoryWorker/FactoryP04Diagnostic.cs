using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Separate from NativeFactoryBackend: this interface cannot create/write any output.
    internal interface IP04ReadOnlyNative
    {
        string Load(string directory);
        string Identity(string device);
        void OpenLine(string device, int grouping);
        P04RawRead ReadLine();
        P04RawRead ReadPort(string device);
        void CloseLine();
    }
    internal sealed class P04RawRead
    {
        internal int Status, Samples, Bytes;
        internal uint Raw;
    }
    internal static class FactoryP04Diagnostic
    {
        internal static void Validate(BenchSettings input)
        {
            if (input == null || !input.RealHardware || String.IsNullOrWhiteSpace(input.ArtDevice))
                throw new ArgumentException("请切换实机模式并选择准确的 ART 设备；本诊断不需要 Pico 或反馈确认勾选。");
            foreach (char c in input.ArtDevice)
                if (!Char.IsLetterOrDigit(c) || c > 127)
                    if (c != '_' && c != '-') throw new ArgumentException("ART 模块名含非法通道或路径语法。");
            uint serial;
            if (!UInt32.TryParse(input.ArtSerial, NumberStyles.None, CultureInfo.InvariantCulture, out serial) || serial == 0)
                throw new ArgumentException("只读诊断仍须锁定非零 ART 序列号，不能自动换卡。");
            if (String.IsNullOrWhiteSpace(input.ArtLibraryDirectory)) throw new ArgumentException("缺少 ART x64 运行库目录。");
        }
        internal static string LineState(P04RawRead value)
        {
            if (value == null || value.Status < 0 || value.Samples != 1 || value.Bytes != 1 || value.Raw > 1) return "无效/未知";
            return value.Raw == 1 ? "高" : "低";
        }
        internal static string PortState(P04RawRead value)
        {
            if (value == null || value.Status < 0 || value.Raw > 255) return "无效/未知";
            return (value.Raw & 0x10) != 0 ? "高" : "低";
        }
        internal static string Run(BenchSettings input, IP04ReadOnlyNative native, Action<string,string> report, CancellationToken token)
        {
            Validate(input);
            if (native == null) throw new ArgumentNullException("native");
            var text = new StringBuilder(); int mismatches = 0, invalid = 0;
            Action<string> log = detail => { text.AppendLine(detail); if (report != null) report("P04_DIAG", detail); };
            try
            {
                token.ThrowIfCancellationRequested();
                log("P0.4 独立只读诊断；仅配置 Dev/port0/line4 为 DI。没有 AO/DO 任务、输出写入、设备复位或 Pico 打开。");
                log("既有输出不由本诊断关闭或接管；请勿与加压程序/DMC设备测试同时运行。结果绝不作为加压许可。");
                log(native.Load(input.ArtLibraryDirectory));
                RequireIdentity(input, native, log);
                for (int grouping = 0; grouping <= 1; grouping++)
                {
                    token.ThrowIfCancellationRequested();
                    log("配置 " + input.ArtDevice + "/port0/line4；grouping=" + grouping + (grouping == 0 ? "（逐线通道，与加压版相同）" : "（全线分组，但仍仅包含 line4）"));
                    try
                    {
                        native.OpenLine(input.ArtDevice, grouping);
                        for (int i = 0; i < 10; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            P04RawRead line = native.ReadLine(), port = native.ReadPort(input.ArtDevice);
                            string ls = LineState(line), ps = PortState(port);
                            log(DateTime.Now.ToString("HH:mm:ss.fff") + " #" + (i + 1) + " ReadDigitalLines: status=" + line.Status +
                                "; samples=" + line.Samples + "; bytes=" + line.Bytes + "; raw=" + line.Raw + " (0x" + line.Raw.ToString("X2") + "); P0.4=" + ls);
                            log("    ReadDigitalPort: status=" + port.Status + "; Port0=0x" + port.Raw.ToString("X8") +
                                "; bit4=" + ps + "；仅读取，不将整口配置为输入；两次调用并非同一时刻。");
                            if (ls == "无效/未知" || ps == "无效/未知") invalid++;
                            else if (ls != ps) mismatches++;
                            if (i != 9 && token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
                        }
                    }
                    finally { native.CloseLine(); }
                }
                RequireIdentity(input, native, log);
                log("诊断完成：单线/端口不一致 " + mismatches + " 次；无效/不支持 " + invalid + " 次。请结合 DMC 的同设备结果分析；没有验证高压、残压或完整联锁。");
                return text.ToString();
            }
            finally { native.CloseLine(); }
        }
        private static void RequireIdentity(BenchSettings input, IP04ReadOnlyNative native, Action<string> log)
        {
            string identity = native.Identity(input.ArtDevice); log("身份复核：" + input.ArtDevice + " / " + identity);
            if (identity != "USB3135 / " + input.ArtSerial) throw new InvalidOperationException("ART 型号或序列号不符，停止只读诊断，不操作其他卡。");
        }
    }
    internal sealed class P04ReadOnlyNative : IP04ReadOnlyNative
    {
        private IntPtr task;
        private bool started;
        private string requestedPath;
        public string Load(string directory)
        {
            requestedPath = InputOnlyArtNative.Load(directory);
            string hash;
            using (var stream = File.OpenRead(requestedPath)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            return "已配置 DLL（尚未调用 SDK）：" + requestedPath + "；版本 " + FileVersionInfo.GetVersionInfo(requestedPath).FileVersion + "；x64=" + Environment.Is64BitProcess + "；SHA256=" + hash;
        }
        public string Identity(string device)
        {
            uint serial; var model = new StringBuilder(128);
            InputOnlyArtNative.Check(InputOnlyArtNative.GetSerial(device, 0x0632, out serial, IntPtr.Zero), "只读身份：序列号");
            InputOnlyArtNative.Check(InputOnlyArtNative.GetProduct(device, 0x0631, model, model.Capacity, IntPtr.Zero), "只读身份：型号");
            // The bridge loads the selected SDK lazily on the successful read-only call above.
            // Never pass a null module to GetModuleFileName: that would describe this EXE.
            IntPtr module = GetModuleHandle("Art_DAQ.dll");
            var actual = new StringBuilder(32768);
            if (module == IntPtr.Zero || GetModuleFileName(module, actual, actual.Capacity) == 0)
                throw new InvalidOperationException("无法回读实际 ART DLL 路径。");
            if (requestedPath == null || !String.Equals(requestedPath, Path.GetFullPath(actual.ToString()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("实际加载 DLL 与所选目录不一致：" + actual);
            return model.ToString().Trim() + " / " + serial.ToString(CultureInfo.InvariantCulture);
        }
        public void OpenLine(string device, int grouping)
        {
            if (task != IntPtr.Zero || (grouping != 0 && grouping != 1)) throw new InvalidOperationException("只读 DI 任务重叠或分组无效。");
            InputOnlyArtNative.Check(InputOnlyArtNative.CreateTask("", out task), "创建仅 DI 诊断任务");
            if (task == IntPtr.Zero) throw new InvalidOperationException("DI 句柄为空。");
            InputOnlyArtNative.Check(InputOnlyArtNative.CreateDi(task, device + "/port0/line4", "", grouping), "配置仅 P0.4 输入");
            started = true;
            InputOnlyArtNative.Check(InputOnlyArtNative.StartTask(task), "启动仅 P0.4 输入");
        }
        public P04RawRead ReadLine()
        {
            var raw = new byte[] { 0xa5 }; int count, bytes;
            int status = InputOnlyArtNative.ReadDigital(task, 1, .1, 0, raw, 1, out count, out bytes, IntPtr.Zero);
            return new P04RawRead { Status = status, Samples = count, Bytes = bytes, Raw = raw[0] };
        }
        public P04RawRead ReadPort(string device)
        {
            uint raw = UInt32.MaxValue;
            try { return new P04RawRead { Status = ReadDigitalPort(task, device, 0, out raw), Raw = raw }; }
            catch (EntryPointNotFoundException) { return new P04RawRead { Status = Int32.MinValue, Raw = raw }; }
        }
        public void CloseLine()
        {
            if (task == IntPtr.Zero) return;
            var failures = new List<Exception>();
            if (started) try { InputOnlyArtNative.Check(InputOnlyArtNative.StopTask(task), "停止只读 DI"); started = false; } catch (Exception ex) { failures.Add(ex); }
            try { InputOnlyArtNative.Check(InputOnlyArtNative.ClearTask(task), "释放只读 DI"); task = IntPtr.Zero; started = false; } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) throw new AggregateException("只读 DI 清理失败；未创建或写入任何输出任务。", failures);
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadDigitalPort", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int ReadDigitalPort(IntPtr task, string device, int portIndex, out uint value);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int length);
    }
}
