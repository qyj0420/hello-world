using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace HVX.OWTS.Task38
{
    public sealed class ArtDeviceRecord
    {
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
        public string InstanceId { get; set; }
        public string Category { get; set; }
        public uint DeviceClass { get; set; }
        public uint BusType { get; set; }
        public uint SerialNumber { get; set; }
        public uint SlotInfo { get; set; }
        public uint Status { get; set; }
        [XmlIgnore] public bool Offline { get { return InstanceId == String.Empty && (Status & 8) == 0; } }
        [XmlIgnore] public bool CanRemove { get { return Offline && DeviceType == "USB3135" && (DeviceClass & 1) != 0 && (BusType & 1) != 0; } }
    }

    public interface IArtDeviceCatalog
    {
        ArtDeviceRecord[] ReadFresh();
        void Remove(string deviceName);
    }

    public sealed class ArtDeviceRemovalResult
    {
        public string BackupPath { get; internal set; }
        public bool VerifiedAbsent { get; internal set; }
        public string Message { get; internal set; }
    }

    // Changes only the selected offline DMC record. No ResetDevice, rename or I/O calls.
    public sealed class ArtDeviceCleanup
    {
        private readonly IArtDeviceCatalog catalog;
        private readonly string backupDirectory;
        public ArtDeviceCleanup(IArtDeviceCatalog source, string recordDirectory)
        { catalog = source ?? throw new ArgumentNullException("source"); backupDirectory = recordDirectory; }
        public ArtDeviceRecord[] ReadFresh() { return catalog.ReadFresh(); }
        private ArtDeviceRecord Revalidate(ArtDeviceRecord selected)
        {
            ArtDeviceRecord[] matches = catalog.ReadFresh().Where(d => d.DeviceName == selected.DeviceName).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("目标设备已变化或不存在，请刷新列表。");
            ArtDeviceRecord current = matches[0];
            if (!current.CanRemove) throw new InvalidOperationException("设备已连接、离线状态不确定或型号不符，已取消删除。");
            if (current.SerialNumber != selected.SerialNumber || current.DeviceType != selected.DeviceType ||
                current.DeviceClass != selected.DeviceClass || current.BusType != selected.BusType || current.Category != selected.Category)
                throw new InvalidOperationException("设备身份已变化，已取消删除，请重新选择。");
            return current;
        }
        public ArtDeviceRemovalResult RemoveOffline(ArtDeviceRecord selected)
        {
            if (selected == null || !selected.CanRemove || !Regex.IsMatch(selected.DeviceName ?? "", @"^[A-Za-z][A-Za-z0-9_]{0,63}$"))
                throw new InvalidOperationException("只允许删除所选的离线 USB3135 记录。");
            ArtDeviceRecord current = Revalidate(selected);
            Directory.CreateDirectory(backupDirectory);
            string path = Path.Combine(backupDirectory, selected.DeviceName + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".xml");
            // Abort before removal if the previous record cannot be saved.
            using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                new XmlSerializer(typeof(ArtDeviceRecord)).Serialize(file, current);
            Revalidate(selected);
            catalog.Remove(selected.DeviceName);
            ArtDeviceRemovalResult result = new ArtDeviceRemovalResult { BackupPath = path };
            try
            {
                result.VerifiedAbsent = !catalog.ReadFresh().Any(d => d.DeviceName == selected.DeviceName);
                result.Message = result.VerifiedAbsent ? "已删除离线设备 " + selected.DeviceName + "，刷新核验通过。"
                    : "删除接口已成功，但刷新后该名称仍存在。请核对是否重新插入设备，不要直接重复删除。";
            }
            catch (Exception e)
            { result.Message = "删除接口已成功，但刷新核验失败：" + e.Message + "。请在 DMC 中检查结果。"; }
            return result;
        }
    }

    public sealed class ArtDmcDeviceCatalog : IArtDeviceCatalog
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
        private struct Node
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string Type;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Instance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string Category;
            public uint DeviceClass, BusType, Serial, Slot, Status;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RefreshFunction();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ListFunction(uint type, IntPtr data, int size, out int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] private delegate int RemoveFunction(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static readonly object gate = new object();
        private static IntPtr library;
        private static RefreshFunction refresh;
        private static ListFunction list;
        private static RemoveFunction remove;
        private static void Load()
        {
            if (refresh != null && list != null && remove != null) return;
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("设备管理需要 x64 程序。");
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"ART Technology\DMC\Lib\x64\DMCSCSL_64.dll");
            if (!File.Exists(path)) throw new FileNotFoundException("未找到 ART DMC x64 库，请安装官方 DMC。", path);
            // Load dependencies relative to the official DLL without changing process search paths.
            if (library == IntPtr.Zero) library = LoadLibraryEx(path, IntPtr.Zero, 8);
            if (library == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "加载 ART DMC 失败");
            refresh = (RefreshFunction)Marshal.GetDelegateForFunctionPointer(Export("DMCSCSL_UpdateDeviceTree"), typeof(RefreshFunction));
            list = (ListFunction)Marshal.GetDelegateForFunctionPointer(Export("DMCSCSL_GetDeviceList"), typeof(ListFunction));
            remove = (RemoveFunction)Marshal.GetDelegateForFunctionPointer(Export("DMCSCSL_RemoveDevice"), typeof(RemoveFunction));
        }
        private static IntPtr Export(string name)
        { IntPtr p = GetProcAddress(library, name); if (p == IntPtr.Zero) throw new EntryPointNotFoundException(name); return p; }
        private static void Check(int code, string operation)
        { if (code != 0) throw new InvalidOperationException(operation + "失败，ART 错误码 " + code + "。请检查 DMC 服务及权限。"); }
        public ArtDeviceRecord[] ReadFresh()
        {
            lock (gate)
            {
                Load(); Check(refresh(), "刷新设备列表");
                int count; Check(list(1, IntPtr.Zero, 0, out count), "读取设备数量");
                if (count < 0 || count > 1024) throw new InvalidOperationException("设备数量无效。");
                if (count == 0) return new ArtDeviceRecord[0];
                int capacity = count, stride = Marshal.SizeOf(typeof(Node));
                if (stride != 244) throw new InvalidOperationException("DMC 设备结构尺寸不匹配。");
                IntPtr memory = Marshal.AllocHGlobal(checked(capacity * stride));
                try
                {
                    Check(list(1, memory, capacity * stride, out count), "读取设备列表");
                    if (count < 0 || count > capacity) throw new InvalidOperationException("设备列表已变化，请刷新。");
                    ArtDeviceRecord[] records = new ArtDeviceRecord[count];
                    for (int i = 0; i < count; i++)
                    {
                        Node n = (Node)Marshal.PtrToStructure(IntPtr.Add(memory, i * stride), typeof(Node));
                        records[i] = new ArtDeviceRecord { DeviceName = n.Name, DeviceType = n.Type, InstanceId = n.Instance,
                            Category = n.Category, DeviceClass = n.DeviceClass, BusType = n.BusType, SerialNumber = n.Serial, SlotInfo = n.Slot, Status = n.Status };
                    }
                    return records;
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
        }
        public void Remove(string deviceName) { lock (gate) { Load(); Check(remove(deviceName), "删除 " + deviceName); } }
    }
}
