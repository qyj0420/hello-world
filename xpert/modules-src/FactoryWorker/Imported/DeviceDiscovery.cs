using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Serialization;
using HVX.OWTS.Task38;

namespace Xiongxin.ZeroBench
{
    [Serializable]
    public sealed class ArtDeviceIdentity
    {
        public string Name = "";
        public string Serial = "";
        public string Model = "";
        public override string ToString() { return Name + " · " + Model + " · SN " + Serial; }
    }

    [Serializable]
    public sealed class DiscoveryResult
    {
        public List<ArtDeviceIdentity> ArtDevices = new List<ArtDeviceIdentity>();
        public List<string> PicoSerials = new List<string>();
        public string ArtError = "";
        public string PicoError = "";
        public string ArtRuntime = "";
        public string PicoRuntime = "";

        public void Save(string path)
        {
            string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            string pending = full + ".pending";
            using (var stream = File.Create(pending)) new XmlSerializer(typeof(DiscoveryResult)).Serialize(stream, this);
            if (File.Exists(full)) File.Replace(pending, full, null); else File.Move(pending, full);
        }
        public static DiscoveryResult Load(string path)
        {
            DiscoveryResult result;
            using (var stream = File.OpenRead(path)) result = (DiscoveryResult)new XmlSerializer(typeof(DiscoveryResult)).Deserialize(stream);
            if (result == null || result.ArtDevices == null || result.PicoSerials == null) throw new InvalidDataException("设备枚举结果不完整。");
            return result;
        }
    }

    public sealed class DiscoverySelectionResult
    {
        public ArtDeviceIdentity ArtDevice;
        public string PicoSerial = "";
        public string ArtMessage = "";
        public string PicoMessage = "";
        public bool Ready { get { return ArtDevice != null && !String.IsNullOrEmpty(PicoSerial); } }
    }

    public static class DiscoverySelection
    {
        public static DiscoverySelectionResult Resolve(DiscoveryResult found, string previousArtSerial, string previousPicoSerial)
        {
            if (found == null) throw new ArgumentNullException("found");
            var result = new DiscoverySelectionResult();
            var art = new List<ArtDeviceIdentity>();
            foreach (var item in found.ArtDevices ?? new List<ArtDeviceIdentity>())
                if (item != null && item.Model == "USB3135" && !String.IsNullOrWhiteSpace(item.Name) && ValidArtSerial(item.Serial)) art.Add(item);
            string previousArt = (previousArtSerial ?? "").Trim(), previousPico = (previousPicoSerial ?? "").Trim();
            if (previousArt.Length != 0)
            {
                var matches = art.FindAll(x => String.Equals(x.Serial, previousArt, StringComparison.Ordinal));
                if (matches.Count == 1) { result.ArtDevice = matches[0]; result.ArtMessage = "保留原 ART 精确序列号；模块名称更新为 " + matches[0].Name; }
                else result.ArtMessage = matches.Count == 0 ? "未枚举到原 ART " + previousArt + "；未替换为其他采集卡" : "原 ART 序列号对应多个模块名称，需核对";
            }
            else if (!String.IsNullOrEmpty(found.ArtError)) result.ArtMessage = "ART 枚举未完整成功，不能自动认定唯一卡：" + found.ArtError;
            else if (art.Count == 1) { result.ArtDevice = art[0]; result.ArtMessage = "唯一 USB3135，已建议使用 " + art[0].Name + " / " + art[0].Serial; }
            else result.ArtMessage = art.Count == 0 ? "未发现具有有效序列号的 USB3135" : "发现多台 USB3135，请选择本次要使用的准确序列号";

            var pico = found.PicoSerials ?? new List<string>();
            if (previousPico.Length != 0)
            {
                int count = 0; foreach (string serial in pico) if (String.Equals(serial, previousPico, StringComparison.Ordinal)) count++;
                if (count == 1) { result.PicoSerial = previousPico; result.PicoMessage = "保留原 Pico 精确序列号 " + previousPico; }
                else result.PicoMessage = count == 0 ? "未枚举到原 Pico " + previousPico + "；未替换为其他采集卡" : "Pico 序列号重复，不能自动选择";
            }
            else if (!String.IsNullOrEmpty(found.PicoError)) result.PicoMessage = "Pico 枚举未完整成功，不能自动认定唯一卡：" + found.PicoError;
            else if (pico.Count == 1 && !String.IsNullOrWhiteSpace(pico[0]))
            { result.PicoSerial = pico[0]; result.PicoMessage = "唯一可枚举 Pico：" + pico[0] + "；型号与采集能力须正式连接后确认"; }
            else result.PicoMessage = pico.Count == 0 ? "未发现可枚举 Pico；设备可能未连接或被其他程序占用" : "发现多台 Pico，请选择本次要使用的准确序列号";
            return result;
        }
        private static bool ValidArtSerial(string serial)
        { uint value; return UInt32.TryParse(serial, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value != 0; }
    }

    /// <summary>Enumeration only. No unit is opened and no ART task is created.
    /// Run in the disposable discovery worker: native enumeration can block on an unavailable USB transport.</summary>
    public static class DeviceDiscovery
    {
        public static DiscoveryResult Run(BenchSettings settings, Action<string, string> report, CancellationToken cancellation)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            // Do not call settings.Validate(): identities are intentionally empty before discovery.
            var result = new DiscoveryResult();
            if (report == null) report = delegate { };
            cancellation.ThrowIfCancellationRequested();
            report("DISCOVER_ART", "只读枚举 ART 系统模块名称、型号和序列号，不创建采集任务。");
            try
            {
                result.ArtRuntime = InputOnlyArtNative.Load(settings.ArtLibraryDirectory);
                RequireExports("Art_DAQ.dll", "ArtDAQ_GetSystemAttribute", "ArtDAQ_GetDeviceAttribute", "ArtDAQ_GetDeviceAttributeEx");
                var buffer = new byte[65536];
                int status = GetSystemAttribute(0x193B, buffer, buffer.Length);
                InputOnlyArtNative.Check(status, "枚举 ART 系统设备名称");
                if (status > buffer.Length) throw new InvalidDataException("ART 设备列表超出枚举缓冲区。");
                int length = Array.IndexOf(buffer, (byte)0);
                if (length < 0) throw new InvalidDataException("ART 设备列表缺少结束符，拒绝使用截断结果。");
                var errors = new List<string>();
                foreach (string name in ParseArtNames(Encoding.Default.GetString(buffer, 0, length)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var model = new StringBuilder(256);
                        InputOnlyArtNative.Check(InputOnlyArtNative.GetProduct(name, 0x0631, model, model.Capacity, IntPtr.Zero), "读取 " + name + " 型号");
                        if (!String.Equals(model.ToString().Trim(), "USB3135", StringComparison.OrdinalIgnoreCase)) continue;
                        uint serial;
                        InputOnlyArtNative.Check(InputOnlyArtNative.GetSerial(name, 0x0632, out serial, IntPtr.Zero), "读取 " + name + " 序列号");
                        if (serial == 0) throw new InvalidDataException(name + " 返回空序列号，不能锁定身份。");
                        result.ArtDevices.Add(new ArtDeviceIdentity { Name = name, Model = "USB3135", Serial = serial.ToString(CultureInfo.InvariantCulture) });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors.Add(name + "：" + ex.Message); }
                }
                result.ArtError = String.Join("；", errors.ToArray());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.ArtError = ex.Message; }
            cancellation.ThrowIfCancellationRequested();
            report("DISCOVER_ART", result.ArtError.Length == 0 ? "ART 枚举完成，USB3135 数量 " + result.ArtDevices.Count : "ART 枚举诊断：" + result.ArtError);

            report("DISCOVER_PICO", "只读枚举 Pico SDK 序列号，不打开设备；不会探测其他卡的采集能力。");
            try
            {
                Pico5000aNative.EnsureLoaded(settings.PicoLibraryDirectory);
                result.PicoRuntime = Pico5000aNative.LoadedPath;
                RequireExports("ps5000a.dll", "ps5000aEnumerateUnits");
                const short capacity = 32767;
                var buffer = new StringBuilder(capacity); short length = capacity, count;
                uint status = EnumerateUnits(out count, buffer, ref length);
                if (status != 0) throw new InvalidOperationException("Pico 枚举失败，PICO_STATUS=0x" + status.ToString("X8") + "；未连接或被占用设备可能不出现在枚举列表。");
                if (length < 0 || length > capacity || count < 0) throw new InvalidDataException("Pico 枚举返回无效长度或数量。");
                result.PicoSerials = ParsePicoSerials(buffer.ToString(), count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.PicoError = ex.Message; }
            cancellation.ThrowIfCancellationRequested();
            report("DISCOVER_PICO", result.PicoError.Length == 0 ? "Pico 枚举完成，序列号数量 " + result.PicoSerials.Count : "Pico 枚举诊断：" + result.PicoError);
            return result;
        }

        public static List<string> ParseArtNames(string value)
        {
            var list = Split(value, new[] { ',', ';', '\r', '\n', '\0' });
            foreach (string name in list)
                if (name.IndexOfAny(new[] { '/', '\\', ':', '\t' }) >= 0) throw new InvalidDataException("ART 返回无效模块名称。");
            return list;
        }
        public static List<string> ParsePicoSerials(string value, int expectedCount)
        {
            if (expectedCount < 0) throw new InvalidDataException("Pico 设备数量无效。");
            var list = Split((value ?? "").TrimEnd('\0'), new[] { ',' });
            if (list.Count != expectedCount) throw new InvalidDataException("Pico 序列号数量与 SDK 返回数量不符。");
            foreach (string serial in list)
                foreach (char c in serial)
                    if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && c != '/' && c != '-' && c != '_')
                        throw new InvalidDataException("Pico 返回含非法字符的序列号。");
            return list;
        }
        private static List<string> Split(string value, char[] separators)
        {
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in (value ?? "").Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                string item = raw.Trim(); if (item.Length == 0) continue;
                if (!seen.Add(item)) throw new InvalidDataException("SDK 枚举包含重复身份，拒绝自动选择。");
                result.Add(item);
            }
            return result;
        }
        private static void RequireExports(string library, params string[] names)
        {
            // Validate the owned bridge surface; the bridge validates the selected SDK export on invocation.
            IntPtr module = GetModuleHandle("HvxAcquisitionNative.dll");
            if (module == IntPtr.Zero) throw new InvalidOperationException("枚举运行库尚未加载：" + library);
            foreach (string name in names) if (GetProcAddress(module, name) == IntPtr.Zero) throw new EntryPointNotFoundException("本机运行库缺少枚举接口 " + name);
        }

        // Official installed ART Include/Art_DAQ.h: int32 GetSystemAttribute(int32, void*, int32).
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetSystemAttribute", CallingConvention = CallingConvention.StdCall)]
        private static extern int GetSystemAttribute(int attribute, [Out] byte[] value, int bufferSize);
        // Official installed Pico SDK/inc/ps5000aApi.h: PICO_STATUS EnumerateUnits(int16_t*, int8_t*, int16_t*).
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ps5000aEnumerateUnits", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern uint EnumerateUnits(out short count, StringBuilder serials, ref short length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
