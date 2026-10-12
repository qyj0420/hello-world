using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Xiongxin.Scope
{
    internal static class Native
    {
        private static readonly object loadSync = new object();
        internal static string LoadedPath;
        internal static string StartupNote = "";
        internal static void SuppressStartupWindow()
        {
            CheckHardwareBridge(HvxSuppressPicoStartupWindow(), "Prepare Pico runtime");
            StartupNote="已在 OpenUnit 前调用用户提供的启动窗口抑制参数 (485331304, 4582)。";
        }
        internal static ushort Machine(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            { if (r.ReadUInt16() != 0x5A4D) throw new IOException("不是 Windows DLL。"); r.BaseStream.Position = 60; int offset = r.ReadInt32(); r.BaseStream.Position = offset; if (r.ReadUInt32() != 0x4550) throw new IOException("PE 标头错误。"); return r.ReadUInt16(); }
        }
        internal static void Load(string configured)
        {
            lock (loadSync)
            {
            var paths = new List<string>();
            if (!String.IsNullOrWhiteSpace(configured)) paths.Add(Directory.Exists(configured) ? Path.Combine(configured, "ps5000a.dll") : configured);
            else
            {
                paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Pico Technology\SDK\lib\ps5000a.dll"));
                paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Pico Technology\PicoScope 7 T&M Stable\ps5000a.dll"));
            }
            var errors = new StringBuilder();
            foreach (string candidate in paths)
            {
                string path = Path.GetFullPath(candidate);
                if (!File.Exists(path)) { errors.AppendLine(path + "：未找到。"); continue; }
                if (Machine(path) != 0x8664) { errors.AppendLine(path + "：不是 x64 DLL，不能使用32位SDK。"); continue; }
                if (LoadedPath != null)
                {
                    if (!String.Equals(LoadedPath, path, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("局放运行库已配置，切换版本前须退出并重新启动程序。");
                    return;
                }
                // SDK loading and dependency selection belong exclusively to the native backend.
                CheckHardwareBridge(HvxConfigurePicoPathW(path), "Configure Pico runtime");
                LoadedPath = path; return;
            }
            throw new IOException("无法加载64位局放采集运行库。\r\n" + errors);
            }
        }
        internal static void Check(uint status, string op)
        {
            if (status == 0) return;
            throw CreateStatusException(status, op, ReadBridgeDiagnostic, true);
        }
        // BEGIN PURE PICO STATUS CLASSIFICATION: no SDK calls; diagnostic delegate runs synchronously.
        internal static bool IsBridgeStatus(uint status)
        { return (status & 0xFFFF0000u) == 0xE4850000u; }

        internal static InvalidOperationException CreateStatusException(uint status, string operation,
            Func<string> diagnosticReader, bool chinese)
        {
            bool bridge = IsBridgeStatus(status);
            uint kind = bridge ? status & 0xFFFFu : 0;
            string message = chinese ? "采集未完成，请检查设备后重试。" : "Acquisition did not complete. Check the device, then retry.";
            if (bridge)
            {
                if (kind == 3 || kind == 5)
                    message = chinese ? "采集设备正被使用，请先结束其他操作。" : "The acquisition device is in use. Finish the other operation first.";
                else if (kind == 1 || kind == 2 || kind == 6)
                    message = chinese ? "采集组件不可用，请检查软件安装和设置。" : "The acquisition component is unavailable. Check the installation and settings.";
                else if (kind == 7)
                    message = chinese ? "采集组件设置已改变，请关闭程序后重试。" : "The acquisition component settings changed. Close the application, then retry.";
                else if (kind == 4 || kind == 12)
                    message = chinese ? "采集会话不可用，请结束当前操作后重新连接。" : "The acquisition session is unavailable. Finish the current operation, then reconnect.";
                else
                    message = chinese ? "采集未完成，请停止试验并检查设备。" : "Acquisition did not complete. Stop the test and check the device.";
            }
            string detail = String.Empty;
            if (bridge && diagnosticReader != null)
            {
                try { detail = diagnosticReader() ?? String.Empty; }
                catch (Exception error) { detail = "Bridge diagnostic unavailable: " + error; }
            }
            string domain = bridge ? "HVX_BRIDGE_STATUS" : "PICO_STATUS";
            var technical = new InvalidOperationException(operation + " failed; " + domain + "=0x" +
                status.ToString("X8") + (detail.Length == 0 ? String.Empty : "; " + detail));
            var failure = new InvalidOperationException(message, technical);
            failure.Data["StatusDomain"] = bridge ? "HvxBridge" : "PicoSdk";
            failure.Data["RawStatus"] = status; // Keep uint32 intact, including vendor codes above Int32.MaxValue.
            failure.Data["Operation"] = operation ?? String.Empty;
            if (bridge) failure.Data["BridgeKind"] = kind;
            return failure;
        }
        // END PURE PICO STATUS CLASSIFICATION

        internal static string ReadBridgeDiagnostic()
        {
            var detail = new byte[4096];
            int copied = HvxGetLastErrorInfo(detail, (uint)detail.Length);
            if (copied != 0) return "Native backend diagnostic could not be read (bridge " + copied + ").";
            int end = Array.IndexOf(detail, (byte)0);
            return Encoding.UTF8.GetString(detail, 0, end < 0 ? detail.Length : end);
        }
        private static void CheckHardwareBridge(int status, string operation)
        {
            if (status == 0) return;
            var detail = new byte[4096];
            int copied = HvxGetLastErrorInfo(detail, (uint)detail.Length);
            int end = Array.IndexOf(detail, (byte)0);
            string message = copied == 0 ? Encoding.UTF8.GetString(detail, 0, end < 0 ? detail.Length : end) : "Native backend diagnostic unavailable.";
            throw new InvalidOperationException(operation + " failed; hardware backend status=" + status + ": " + message);
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CharSet=CharSet.Unicode, ExactSpelling=true, CallingConvention=CallingConvention.StdCall)] private static extern int HvxConfigurePicoPathW(string path);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", ExactSpelling=true, CallingConvention=CallingConvention.StdCall)] private static extern int HvxSuppressPicoStartupWindow();
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", ExactSpelling=true, CallingConvention=CallingConvention.StdCall)] private static extern int HvxGetLastErrorInfo([Out] byte[] buffer, uint capacity);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)] internal static extern uint ps5000aEnumerateUnits(out short count, StringBuilder serials, ref short length);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)] internal static extern uint ps5000aOpenUnit(out short handle, string serial, int resolution);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aChangePowerSource(short h, uint status);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aCloseUnit(short h);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aStop(short h);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aPingUnit(short h);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)] internal static extern uint ps5000aGetUnitInfo(short h, StringBuilder buffer, short size, out short required, int info);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aSetDeviceResolution(short h, int resolution);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aGetDeviceResolution(short h, out int resolution);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aSetChannel(short h, int channel, short enabled, int coupling, int range, float offset);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aSetBandwidthFilter(short h, int channel, int filter);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aMemorySegments(short h, uint segments, out int maximum);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aGetTimebase2(short h, uint tb, int samples, out float ns, out int maximum, uint segment);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aMaximumValue(short h, out short value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aSetSimpleTrigger(short h, short enable, int source, short threshold, int edge, uint delay, short autoMs);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aSetDataBuffer(short h, int channel, IntPtr buffer, int length, uint segment, int ratio);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aRunBlock(short h, int pre, int post, uint timebase, out int indisposed, uint segment, IntPtr callback, IntPtr parameter);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aIsReady(short h, out short ready);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CallingConvention=CallingConvention.StdCall)] internal static extern uint ps5000aGetValues(short h, uint start, ref uint samples, uint downsample, int ratio, uint segment, out short overflow);
    }
}
